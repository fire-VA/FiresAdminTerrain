using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Paint
{
    /// <summary>
    /// 0.2.1440 ([worldgen], Fire 11-16: "build the distant terrain cache with a config toggle to cleanly turn it completely off if we
    /// dont want it or it hurts performance"). Distant terrain HEIGHTS, the partner of DistantPaintCache (paint only).
    ///
    /// Why: vanilla's distant LOD never applies terrain edits (Heightmap.ApplyModifiers: "m_isDistantLod ? null :
    /// TerrainComp.FindTerrainCompiler(...)"), and a zone's edits live in its _TerrainCompiler, a NON-distant object that exists only
    /// inside the near simulation distance. So past the loaded ground every leveled / raised / dug area is drawn at raw worldgen
    /// height (a leveled town "buried in the mountain"), and it drops back the moment the player leaves the near ring.
    ///
    ///   CAPTURE  in FAT's one Heightmap.ApplyModifiers postfix: a ZONE heightmap (64 x 1 m) whose TerrainComp is instantiated
    ///            stores its edit as (final height - worldgen base height) per vertex, 65 x 65 shorts in centimetres. Zones with no
    ///            edit over 5 cm are not stored (an existing entry is removed only when its compiler is present and the edit is gone).
    ///            Nothing is touched while the compiler is not instantiated, so a zone loading in never erases good data.
    ///            0.2.1517 ([lead] 11-23: zones flipped A <-> B as vanilla TerrainModifiers (locations, some pieces) spawned and
    ///            despawned around zone load, and every flip bumped Revision, so EBM rebuilt all far tiles): the stored value is still
    ///            final - base INCLUDING modifiers, but a zone is re-captured only when its signature changed (the compiler's ZDO data
    ///            revision + the count and hash of the enabled level / smooth TerrainModifiers over this heightmap, the set vanilla's
    ///            ApplyModifiers just used), and only when every valid ZDO in its 3x3 zones is instantiated and all 9 zones are loaded
    ///            (vanilla's ZNetScene.IsAreaReady rule on our own list, plus the neighbours loaded so an unloading edge never counts).
    ///            A half-loaded or half-unloaded zone keeps its earlier, fuller capture. Edge zones of the loaded area wait until the
    ///            player is within one zone of them.
    ///   APPLY    in the same postfix on a DISTANT-LOD heightmap (vanilla TerrainLod tiles and FAT's FarTerrainVanilla ring), before
    ///            vanilla builds the mesh: each vertex adds the MEAN edit over its own footprint (vertex spacing x spacing, from a
    ///            per-zone summed-area table), so thin cuts never spike a coarse tile. FarQuilt tiles (Fires-generated worlds) add the
    ///            same mean at their step. Vanilla resets m_heights from the base on every Generate, so nothing accumulates.
    ///   REFRESH  a zone whose edit changed queues the far tiles over it; they rebuild once the zone is outside the near ring
    ///            ((near + 1) zones away), one tile per 0.25 s, so editing next to the player never rebuilds far tiles.
    ///   PERSIST  per world, deflate: BepInEx\cache\FiresAdminTerrain\DistantHeights\[world].fdh, flushed on a worker every 60 s
    ///            and on ZNet.Shutdown.
    ///
    /// [DistantHeightCache] Enabled OFF = vanilla far terrain, nothing runs: the postfix call returns on its first line, no capture,
    /// no file read or write, no allocation, no driver tick. Live toggle: on/off rebuilds the far tiles once.
    /// Client only (inert on dedicated servers / batch mode).
    /// </summary>
    public static class DistantHeightCache
    {
        internal static ConfigEntry<bool> Enabled;

        private const int Res = 65;               // zone heightmap: 64 cells x 1 m
        private const int Cells = Res * Res;
        private const short MinDeltaCm = 5;       // edits smaller than 5 cm are not cached
        private const short ChangedCm = 5;        // a re-capture differing by 5 cm or more counts as changed

        private sealed class Entry
        {
            public short[] Cm;   // Res*Res, row-major (z * Res + x), centimetres
            public int[] Sat;    // (Res+1)^2 summed-area table of Cm, built on first use, memory only
        }

        private static readonly Dictionary<Vector2s, Entry> s_cache = new Dictionary<Vector2s, Entry>();
        private static readonly short[] s_scratch = new short[Cells];
        private static readonly List<KeyValuePair<Vector2s, Entry>> s_hits = new List<KeyValuePair<Vector2s, Entry>>(32);
        private static readonly HashSet<Vector2s> s_changedZones = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_zoneScratch = new List<Vector2s>(16);
        private static readonly Queue<Heightmap> s_regen = new Queue<Heightmap>();
        private static readonly HashSet<Heightmap> s_regenSet = new HashSet<Heightmap>();

        private static string s_loadedPath;
        private static bool s_dirty;
        private static Task s_saving;
        private static volatile bool s_saveFailed;
        private static float s_nextFlush, s_nextRegen, s_nextStats;
        private static GameObject s_driver;

        // stats (Debug every 60 s)
        private static int s_captures, s_changes, s_applied, s_regens;
        private static double s_captureMs, s_applyMs, s_regenMs;
        // 0.2.1517: capture proof (Info every 60 s while captures run): calls seen, skipped (same signature), refused (zone objects
        // still loading / unloading), revision bumps.
        private static int s_seen, s_skipped, s_refused, s_bumps;

        // 0.2.1517: per-zone signature of the last capture that ran (session memory only; cleared with the cache).
        private struct Sig { public ZDOID Tc; public uint Rev; public int Mods; public uint ModHash; }
        private static readonly Dictionary<Vector2s, Sig> s_sig = new Dictionary<Vector2s, Sig>();
        private static readonly List<ZDO> s_areaZdos = new List<ZDO>(256);
        private static readonly AccessTools.FieldRef<TerrainComp, ZNetView> s_tcView = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");

        private static readonly AccessTools.FieldRef<Heightmap, List<float>> s_heights = AccessTools.FieldRefAccess<Heightmap, List<float>>("m_heights");
        private static readonly AccessTools.FieldRef<Heightmap, HeightmapBuilder.HMBuildData> s_buildData = AccessTools.FieldRefAccess<Heightmap, HeightmapBuilder.HMBuildData>("m_buildData");
        private static readonly AccessTools.FieldRef<Heightmap, bool> s_isDistantLod = AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("DistantHeightCache", "Enabled", true,
                "Leveled, raised and dug ground shows on the distant terrain too: each zone's terrain edits are remembered when you are near it "
                + "(per world, in BepInEx\\cache\\FiresAdminTerrain\\DistantHeights) and drawn on the far terrain tiles, so a big leveled build no longer "
                + "looks buried in the hillside from a distance. Detail follows the far terrain's own grid (about 10 m, 32 m past 1200 m). "
                + "OFF = vanilla far terrain: nothing runs, nothing is read or written. Applies live (the far terrain rebuilds once).");
            Enabled.SettingChanged += (_, __) => OnToggle();
            if (!Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) EnsureDriver();   // 0.2.1444: publishes for FatApi readers
            FiresLogger.LogInfo(Enabled.Value
                ? "[DistantHeights] on: terrain edits of visited zones are drawn on the distant terrain (0.2.1440)"
                : "[DistantHeights] off ([DistantHeightCache] Enabled = false): vanilla distant terrain, nothing runs");
        }

        private static bool Active =>
            Enabled != null && Enabled.Value
            && !Application.isBatchMode
            && (ZNet.instance == null || !ZNet.instance.IsDedicated());

        // ------------------------------------------------------------------ the one ApplyModifiers postfix (Patch_Heightmap_WetMask)
        internal static void AfterApplyModifiers(Heightmap hm)
        {
            if (!Active || hm == null) return;
            try
            {
                if (s_isDistantLod(hm)) Apply(hm);
                else Capture(hm);
            }
            catch (Exception ex) { FiresLogger.LogDebug($"[DistantHeights] skipped one heightmap: {ex.Message}"); }   // never break terrain building
        }

        // ------------------------------------------------------------------ capture
        private static void Capture(Heightmap hm)
        {
            if (hm.m_width != Res - 1 || Mathf.Abs(hm.m_scale - 1f) > 1e-3f) return;   // zone heightmaps only
            Vector3 pos = hm.transform.position;
            var tc = TerrainComp.FindTerrainCompiler(pos);
            if (tc == null) return;                                                    // edits not loaded here: keep what we have
            var bd = s_buildData(hm);
            var heights = s_heights(hm);
            if (bd == null || bd.m_baseHeights == null || heights == null || bd.m_baseHeights.Count != Cells || heights.Count != Cells) return;

            // 0.2.1517: re-capture only on a changed signature, and only with the zone's objects all in place.
            s_seen++;
            Vector2s sigZone = ZoneSystem.GetZone(pos);
            if (!TrySignature(hm, tc, out Sig sig)) { s_refused++; return; }
            if (s_sig.TryGetValue(sigZone, out Sig last) && SameSig(last, sig)) { s_skipped++; return; }
            if (!AreaSettled(sigZone)) { s_refused++; return; }
            s_sig[sigZone] = sig;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureLoaded();
            EnsureDriver();
            int maxAbs = 0;
            for (int i = 0; i < Cells; i++)
            {
                int cm = Mathf.RoundToInt((heights[i] - bd.m_baseHeights[i]) * 100f);
                if (cm > short.MaxValue) cm = short.MaxValue; else if (cm < short.MinValue) cm = short.MinValue;
                s_scratch[i] = (short)cm;
                int a = cm < 0 ? -cm : cm;
                if (a > maxAbs) maxAbs = a;
            }
            Vector2s zone = ZoneSystem.GetZone(pos);
            s_cache.TryGetValue(zone, out Entry old);
            bool changed;
            if (maxAbs < MinDeltaCm)
            {
                changed = old != null;
                if (changed) s_cache.Remove(zone);
            }
            else
            {
                changed = old == null || Differs(old.Cm, s_scratch);
                if (changed) s_cache[zone] = new Entry { Cm = (short[])s_scratch.Clone() };   // the only allocation: a changed zone
            }
            s_captures++;
            if (changed)
            {
                s_changes++;
                s_dirty = true;
                s_pubDirty = true;   // 0.2.1444
                s_changedZones.Add(zone);
            }
            s_captureMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        // 0.2.1517: the zone's capture signature. Compiler: its ZDOID and ZDO data revision (vanilla TerrainComp reads the same
        // revision, TerrainComp.cs:194). Modifiers: the enabled TerrainModifiers that change height (level / smooth) and that this
        // heightmap's ApplyModifiers applies (Heightmap.TerrainVSModifier, Heightmap.cs:537), as count + an order-free hash of what
        // shapes their effect. Paint-only modifiers never move heights, so they are left out. False = no usable compiler ZDO yet.
        private static bool TrySignature(Heightmap hm, TerrainComp tc, out Sig sig)
        {
            sig = default;
            var view = s_tcView(tc);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null) return false;
            sig.Tc = zdo.m_uid;
            sig.Rev = zdo.DataRevision;
            uint sum = 0u, mix = 0u;
            int count = 0;
            var mods = TerrainModifier.GetAllInstances();
            for (int i = 0; i < mods.Count; i++)
            {
                var m = mods[i];
                if (m == null || !m.enabled || !(m.m_level || m.m_smooth) || !hm.TerrainVSModifier(m)) continue;
                Vector3 p = m.transform.position;
                uint h = 2166136261u;
                h = Mix(h, Mathf.RoundToInt(p.x * 100f)); h = Mix(h, Mathf.RoundToInt(p.y * 100f)); h = Mix(h, Mathf.RoundToInt(p.z * 100f));
                h = Mix(h, (m.m_level ? 1 : 0) | (m.m_smooth ? 2 : 0) | (m.m_square ? 4 : 0) | (m.m_playerModifiction ? 8 : 0));
                h = Mix(h, Mathf.RoundToInt(m.m_levelOffset * 100f)); h = Mix(h, Mathf.RoundToInt(m.m_levelRadius * 100f));
                h = Mix(h, Mathf.RoundToInt(m.m_smoothRadius * 100f)); h = Mix(h, Mathf.RoundToInt(m.m_smoothPower * 100f));
                h = Mix(h, m.m_sortOrder);
                sum += h; mix ^= h * 2654435761u;
                count++;
            }
            sig.Mods = count;
            sig.ModHash = sum ^ (mix << 1 | mix >> 31);
            return true;
        }

        private static uint Mix(uint h, int v)
        {
            h = (h ^ (byte)v) * 16777619u; h = (h ^ (byte)(v >> 8)) * 16777619u;
            h = (h ^ (byte)(v >> 16)) * 16777619u; h = (h ^ (byte)(v >> 24)) * 16777619u;
            return h;
        }

        private static bool SameSig(Sig a, Sig b) => a.Tc == b.Tc && a.Rev == b.Rev && a.Mods == b.Mods && a.ModHash == b.ModHash;

        // 0.2.1517: vanilla's "everything here is instantiated" rule (ZNetScene.IsAreaReady, ZNetScene.cs:156-174) on our own list
        // (IsAreaReady fills ZNetScene's shared m_tempCurrentObjects, and other mods patch it), plus all 9 zones loaded, so neither a
        // zone still streaming in nor one at the unloading edge (its neighbours' objects already gone) counts as settled. No polling:
        // a refused zone is captured at its next ApplyModifiers (an arriving TerrainModifier pokes the heightmaps it touches,
        // TerrainModifier.Awake; any other rebuild of that heightmap counts too). Until then the zone keeps what it had.
        private static bool AreaSettled(Vector2s zone)
        {
            var zs = ZoneSystem.instance; var scene = ZNetScene.instance; var man = ZDOMan.instance;
            if (zs == null || scene == null || man == null) return false;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!zs.IsZoneLoaded(new Vector2s(zone.x + dx, zone.y + dz))) return false;
            s_areaZdos.Clear();
            man.FindSectorObjects(zone, new SimulationDistance(1, 0), s_areaZdos);
            for (int i = 0; i < s_areaZdos.Count; i++)
            {
                ZDO z = s_areaZdos[i];
                int prefab = z.GetPrefab();
                if (prefab == 0 || scene.GetPrefab(prefab) == null) continue;   // vanilla IsPrefabZDOValid
                if (scene.FindInstance(z) == null) { s_areaZdos.Clear(); return false; }
            }
            s_areaZdos.Clear();
            return true;
        }

        private static bool Differs(short[] a, short[] b)
        {
            for (int i = 0; i < Cells; i++)
            {
                int d = a[i] - b[i];
                if (d >= ChangedCm || d <= -ChangedCm) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ apply (distant LOD, before vanilla builds the mesh)
        private static void Apply(Heightmap hm)
        {
            EnsureLoaded();
            if (s_cache.Count == 0) return;
            var heights = s_heights(hm);
            int w = hm.m_width, n = w + 1;
            float scale = hm.m_scale;
            if (heights == null || heights.Count != n * n || scale <= 0f) return;
            Vector3 pos = hm.transform.position;
            float span = w * scale, x0 = pos.x - span * 0.5f, z0 = pos.z - span * 0.5f, half = scale * 0.5f;

            s_hits.Clear();
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var kv in s_cache)
            {
                float zx0 = kv.Key.x * 64f - 32f, zz0 = kv.Key.y * 64f - 32f;
                if (zx0 + 64f < x0 - half || zx0 > x0 + span + half || zz0 + 64f < z0 - half || zz0 > z0 + span + half) continue;
                s_hits.Add(kv);
                if (zx0 < minX) minX = zx0; if (zz0 < minZ) minZ = zz0;
                if (zx0 + 64f > maxX) maxX = zx0 + 64f; if (zz0 + 64f > maxZ) maxZ = zz0 + 64f;
            }
            if (s_hits.Count == 0) return;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int ix0 = Mathf.Clamp(Mathf.FloorToInt((minX - half - x0) / scale), 0, w), ix1 = Mathf.Clamp(Mathf.CeilToInt((maxX + half - x0) / scale), 0, w);
            int iz0 = Mathf.Clamp(Mathf.FloorToInt((minZ - half - z0) / scale), 0, w), iz1 = Mathf.Clamp(Mathf.CeilToInt((maxZ + half - z0) / scale), 0, w);
            for (int iz = iz0; iz <= iz1; iz++)
            {
                float vz = z0 + iz * scale;
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    float d = BoxMean(x0 + ix * scale, vz, scale);
                    if (d != 0f) heights[iz * n + ix] += d;
                }
            }
            s_applied++;
            s_applyMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>FarQuilt: the mean cached edit (m) over a size x size footprint at (x, z); 0 when off or nothing cached there.</summary>
        internal static float FarDelta(float x, float z, float size)
        {
            if (!Active || s_cache.Count == 0) return 0f;
            return BoxMean(x, z, size);
        }

        // ------------------------------------------------------------------ 0.2.1444: published copy for FatApi readers (any thread)
        // s_cache is main-thread only. Readers on other threads (EBM's far-tile builder) see an immutable dictionary that the driver
        // swaps in at most every 0.5 s after a change. Entry arrays are replaced on change and never written in place, so the copy only
        // holds references (one dictionary per publish, after an edit). An edit print (FNV-1a over the centimetres) goes with each zone.
        private sealed class Pub { public short[] Cm; public uint Print; }
        private static readonly Dictionary<Vector2s, Pub> EmptyPub = new Dictionary<Vector2s, Pub>();
        private static volatile Dictionary<Vector2s, Pub> s_pub = EmptyPub;
        private static bool s_pubDirty;
        private static float s_nextPublish;
        private static int s_revision;
        private static readonly Dictionary<short[], uint> s_printMemo = new Dictionary<short[], uint>();

        internal static int Revision => System.Threading.Volatile.Read(ref s_revision);
        private static void BumpRevision() { System.Threading.Interlocked.Increment(ref s_revision); s_bumps++; }   // 0.2.1517: + bump count (main thread only, see callers)

        private static void Publish(float now)
        {
            s_pubDirty = false;
            s_nextPublish = now + 0.5f;
            var next = new Dictionary<Vector2s, Pub>(s_cache.Count);
            var keep = new Dictionary<short[], uint>(s_cache.Count);
            foreach (var kv in s_cache)
            {
                short[] cm = kv.Value.Cm;
                if (!s_printMemo.TryGetValue(cm, out uint print)) print = PrintOf(cm);
                keep[cm] = print;
                next[kv.Key] = new Pub { Cm = cm, Print = print };
            }
            s_printMemo.Clear();
            foreach (var kv in keep) s_printMemo[kv.Key] = kv.Value;
            s_pub = next;
            BumpRevision();
        }

        private static uint PrintOf(short[] cm)
        {
            uint h = 2166136261u;
            for (int i = 0; i < cm.Length; i++) { h = (h ^ (byte)cm[i]) * 16777619u; h = (h ^ (byte)(cm[i] >> 8)) * 16777619u; }
            return h == 0 ? 1u : h;
        }

        /// <summary>
        /// Any thread. The zone's cached edit (final height minus world-gen, metres) on its 65 x 65 grid (1 m, row-major z * 65 + x,
        /// vertex 0 at the zone's min corner = zone * 64 - 32) and its edit print. False (delta untouched, print 0) when the zone has no
        /// cached edit or the cache is off.
        /// </summary>
        internal static bool TryZoneDelta(int zoneX, int zoneZ, float[] delta, out uint print)
        {
            print = 0;
            if (delta == null || delta.Length < Cells) return false;
            if (!s_pub.TryGetValue(new Vector2s(zoneX, zoneZ), out Pub p)) return false;
            short[] cm = p.Cm;
            for (int i = 0; i < Cells; i++) delta[i] = cm[i] * 0.01f;
            print = p.Print;
            return true;
        }

        /// <summary>Any thread: how many edited zones the published copy holds.</summary>
        internal static int PublishedZones => s_pub.Count;

        // Mean edit (metres) over the box centred on (cx, cz), uncached ground counting as 0. A zone owns its local vertices 0..63
        // (vertex 64 is the next zone's 0, same world point), so shared edges are counted once.
        private static float BoxMean(float cx, float cz, float size)
        {
            float half = Mathf.Max(0.5f, size * 0.5f);
            float bx0 = cx - half, bx1 = cx + half, bz0 = cz - half, bz1 = cz + half;
            int zx0 = Mathf.FloorToInt((bx0 + 32f) / 64f), zx1 = Mathf.FloorToInt((bx1 + 32f) / 64f);
            int zz0 = Mathf.FloorToInt((bz0 + 32f) / 64f), zz1 = Mathf.FloorToInt((bz1 + 32f) / 64f);
            long sum = 0; int count = 0;
            for (int zz = zz0; zz <= zz1; zz++)
                for (int zx = zx0; zx <= zx1; zx++)
                {
                    float ox = zx * 64f - 32f, oz = zz * 64f - 32f;
                    int i0 = Mathf.Max(0, Mathf.CeilToInt(bx0 - ox)), i1 = Mathf.Min(63, Mathf.CeilToInt(bx1 - ox) - 1);
                    int j0 = Mathf.Max(0, Mathf.CeilToInt(bz0 - oz)), j1 = Mathf.Min(63, Mathf.CeilToInt(bz1 - oz) - 1);
                    if (i1 < i0 || j1 < j0) continue;
                    count += (i1 - i0 + 1) * (j1 - j0 + 1);
                    if (s_cache.TryGetValue(new Vector2s(zx, zz), out Entry e)) sum += SatSum(e, i0, j0, i1, j1);
                }
            return count > 0 && sum != 0 ? (float)(sum / (double)count) * 0.01f : 0f;
        }

        private static long SatSum(Entry e, int i0, int j0, int i1, int j1)
        {
            if (e.Sat == null) e.Sat = BuildSat(e.Cm);
            int s = Res + 1;
            var t = e.Sat;
            return (long)t[(j1 + 1) * s + (i1 + 1)] - t[j0 * s + (i1 + 1)] - t[(j1 + 1) * s + i0] + t[j0 * s + i0];
        }

        private static int[] BuildSat(short[] cm)
        {
            int s = Res + 1;
            var t = new int[s * s];
            for (int j = 0; j < Res; j++)
            {
                int row = 0;
                for (int i = 0; i < Res; i++)
                {
                    row += cm[j * Res + i];
                    t[(j + 1) * s + (i + 1)] = t[j * s + (i + 1)] + row;
                }
            }
            return t;
        }

        // ------------------------------------------------------------------ refresh far tiles over changed zones (driver)
        private static void EnsureDriver()
        {
            if (s_driver != null) return;
            s_driver = new GameObject("FAT DistantHeightCache");
            UnityEngine.Object.DontDestroyOnLoad(s_driver);
            s_driver.hideFlags = HideFlags.HideAndDontSave;
            s_driver.AddComponent<Driver>();
        }

        private sealed class Driver : MonoBehaviour
        {
            private void Update()
            {
                if (!Active) { if (s_pub.Count != 0) { s_pub = EmptyPub; BumpRevision(); } return; }
                float now = Time.unscaledTime;
                if (ZNet.instance != null && ZNet.World != null) EnsureLoaded();   // 0.2.1444: loaded for FatApi readers even with no vanilla far tile
                if (s_pubDirty && now >= s_nextPublish) Publish(now);
                if (s_changedZones.Count > 0 && ZNet.instance != null && ZoneSystem.instance != null) QueueFarZones();
                if (s_regen.Count > 0 && now >= s_nextRegen) { s_nextRegen = now + 0.25f; RegenOne(); }
                FlushMaybe();
                if (now >= s_nextStats) { s_nextStats = now + 60f; Stats(); }
            }
        }

        // A changed zone's far tiles rebuild once the zone has left the near ring (its edits then show only through this cache).
        private static void QueueFarZones()
        {
            Vector2s me = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            int ring = Mathf.Max(2, ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance) + 1;
            s_zoneScratch.Clear();
            foreach (var z in s_changedZones)
                if (Mathf.Max(Mathf.Abs(z.x - me.x), Mathf.Abs(z.y - me.y)) > ring) s_zoneScratch.Add(z);
            if (s_zoneScratch.Count == 0) return;
            var all = DistantTiles();
            for (int k = 0; k < s_zoneScratch.Count; k++)
            {
                Vector2s z = s_zoneScratch[k];
                s_changedZones.Remove(z);
                float zx0 = z.x * 64f - 32f, zz0 = z.y * 64f - 32f;
                for (int i = 0; i < all.Count; i++)
                {
                    var hm = all[i];
                    if (hm == null || s_regenSet.Contains(hm)) continue;
                    float span = hm.m_width * hm.m_scale; Vector3 p = hm.transform.position;
                    if (zx0 + 64f < p.x - span * 0.5f || zx0 > p.x + span * 0.5f || zz0 + 64f < p.z - span * 0.5f || zz0 > p.z + span * 0.5f) continue;
                    s_regen.Enqueue(hm); s_regenSet.Add(hm);
                }
                FarQuilt.RebuildTilesFor(z);
            }
        }

        // 0.2.1442 FIX: Heightmap.GetAllHeightmaps() is vanilla's NEAR list (s_heightmaps; the IsDistantLod setter removes distant tiles
        // from it), so 1440/1441 never found a distant tile to refresh. Enabled heightmaps of both kinds are in Heightmap.Instances
        // (OnEnable adds every one while playing).
        private static readonly List<Heightmap> s_distantScratch = new List<Heightmap>(64);
        internal static List<Heightmap> DistantTiles()
        {
            s_distantScratch.Clear();
            var all = Heightmap.Instances;
            for (int i = 0; i < all.Count; i++)
                if (all[i] is Heightmap hm && hm != null && s_isDistantLod(hm)) s_distantScratch.Add(hm);
            return s_distantScratch;
        }

        private static void RegenOne()
        {
            while (s_regen.Count > 0)
            {
                var hm = s_regen.Dequeue();
                s_regenSet.Remove(hm);
                if (hm == null || !hm.isActiveAndEnabled) continue;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                hm.Poke();   // vanilla's own full rebuild: Generate (base + ApplyModifiers -> Apply above) + RebuildRenderMesh
                s_regenMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                s_regens++;
                return;
            }
        }

        private static void Stats()
        {
            // 0.2.1517 ([lead] 11-23 proof line): Info, only in a minute where captures were asked or the revision moved. Expected on a
            // run with no edits: 0 changed and 0 bumps once each zone has been seen.
            if (s_seen > 0 || s_bumps > 0)
                FiresLogger.LogInfo($"[DistantHeights] last 60 s: {s_seen} capture call(s), {s_skipped} skipped (same compiler revision + modifiers), "
                                    + $"{s_refused} refused (zone objects still loading / unloading), {s_captures} captured, {s_changes} changed, {s_bumps} revision bump(s)");
            s_seen = s_skipped = s_refused = s_bumps = 0;
            if (!FiresLogger.DebugEnabled) { s_captures = s_changes = s_applied = s_regens = 0; s_captureMs = s_applyMs = s_regenMs = 0; return; }
            long bytes = 0; int sats = 0;
            foreach (var e in s_cache.Values) { bytes += Cells * 2L; if (e.Sat != null) { bytes += (Res + 1) * (Res + 1) * 4L; sats++; } }
            FiresLogger.LogDebug($"[DistantHeights] last 60 s: {s_captures} capture(s) ({s_captureMs:0.0} ms, {s_changes} changed), {s_applied} far tile(s) given edits ({s_applyMs:0.0} ms), " +
                                 $"{s_regens} far tile rebuild(s) ({s_regenMs:0.0} ms); cache {s_cache.Count} zone(s), {sats} table(s), {bytes / 1024} KB; {s_regen.Count} queued, {s_changedZones.Count} zone(s) waiting to leave the near ring");
            s_captures = s_changes = s_applied = s_regens = 0; s_captureMs = s_applyMs = s_regenMs = 0;
        }

        // ------------------------------------------------------------------ live toggle
        private static void OnToggle()
        {
            bool on = Active;
            if (!on)
            {
                Save();                    // keep what was learned, then go inert
                s_cache.Clear(); s_changedZones.Clear(); s_regen.Clear(); s_regenSet.Clear(); s_sig.Clear();   // 0.2.1517: + s_sig
                s_loadedPath = null; s_world = null;
                s_pub = EmptyPub; BumpRevision();   // 0.2.1444
            }
            else { EnsureLoaded(); s_pubDirty = true; }
            int n = 0;
            var all = DistantTiles();
            for (int i = 0; i < all.Count; i++)
            {
                var hm = all[i];
                if (hm == null || !hm.isActiveAndEnabled) continue;
                try { hm.Poke(); n++; } catch { }
            }
            FarQuilt.RebuildAllTiles();
            FiresLogger.LogInfo($"[DistantHeights] {(on ? "on" : "off")}: {n} far terrain tile(s) rebuilt {(on ? "with" : "without")} the cached edits");
        }

        // ------------------------------------------------------------------ persistence (same pattern as DistantPaintCache)
        private const int CacheMagic = 0x43484446;   // "FDHC"
        private const int CacheFormatVersion = 1;
        private const int MaxCachedZones = 1 << 17;

        private static string ResolvePath()
        {
            try
            {
                World world = ZNet.instance != null ? ZNet.World : null;
                if (world == null || string.IsNullOrEmpty(world.m_name)) return null;
                string dir = global::VerdantsAscent.FiresCachePaths.For("FiresAdminTerrain", "DistantHeights");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, world.m_name + ".fdh");
            }
            catch { return null; }
        }

        private static World s_world;   // the world the loaded path belongs to (no file-system call per heightmap)

        private static void EnsureLoaded()
        {
            World world = ZNet.instance != null ? ZNet.World : null;
            if (world != null && ReferenceEquals(world, s_world) && s_loadedPath != null) return;
            s_world = world;
            string path = ResolvePath();
            if (path == null || path == s_loadedPath) return;
            if (s_loadedPath != null) Save();   // world changed in one session
            s_cache.Clear(); s_changedZones.Clear(); s_regen.Clear(); s_regenSet.Clear(); s_sig.Clear();   // 0.2.1517: + s_sig
            s_dirty = false;
            s_pubDirty = true;   // 0.2.1444: the new world's set (or empty) is published on the next driver tick
            s_loadedPath = path;
            if (!File.Exists(path)) return;
            try
            {
                using (var fs = File.OpenRead(path)) ReadCache(fs, s_cache);
                FiresLogger.LogInfo($"[DistantHeights] loaded {s_cache.Count} edited zone(s) from {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                s_cache.Clear();
                FiresLogger.LogWarning($"[DistantHeights] {Path.GetFileName(path)} not loaded ({ex.Message}); starting empty, zones re-cache as you visit them.");
            }
        }

        private static void FlushMaybe()
        {
            if (s_saveFailed) { s_saveFailed = false; s_dirty = true; }
            if (!s_dirty || s_loadedPath == null || Time.realtimeSinceStartup < s_nextFlush) return;
            if (s_saving != null && !s_saving.IsCompleted) return;
            s_nextFlush = Time.realtimeSinceStartup + 60f;
            var snapshot = new List<KeyValuePair<Vector2s, short[]>>(s_cache.Count);
            foreach (var kv in s_cache) snapshot.Add(new KeyValuePair<Vector2s, short[]>(kv.Key, kv.Value.Cm));   // entries are replaced, never written in place
            string path = s_loadedPath;
            s_dirty = false;
            s_saving = Task.Run(() => { if (!TryWrite(path, snapshot)) s_saveFailed = true; });
        }

        /// <summary>ZNet.Shutdown (FatVanillaHooks) and the off toggle. Does nothing when nothing was learned.</summary>
        internal static void Save()
        {
            try { s_saving?.Wait(); } catch { }
            s_saving = null;
            if (s_saveFailed) { s_saveFailed = false; s_dirty = true; }
            if (!s_dirty || s_loadedPath == null) return;
            var all = new List<KeyValuePair<Vector2s, short[]>>(s_cache.Count);
            foreach (var kv in s_cache) all.Add(new KeyValuePair<Vector2s, short[]>(kv.Key, kv.Value.Cm));
            if (TryWrite(s_loadedPath, all)) s_dirty = false;
        }

        private static bool TryWrite(string path, List<KeyValuePair<Vector2s, short[]>> entries)
        {
            string temp = path + ".tmp";
            try
            {
                using (var fs = File.Create(temp)) WriteCache(fs, entries);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[DistantHeights] save failed: {ex.Message}");
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return false;
            }
        }

        // File: int magic "FDHC", int version, then deflate: int count; per zone short x, short z, Res*Res shorts (cm, row-major).
        private static void ReadCache(Stream stream, Dictionary<Vector2s, Entry> cache)
        {
            using (var header = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (stream.Length < 8 || header.ReadInt32() != CacheMagic) throw new InvalidDataException("unrecognized file format");
                int version = header.ReadInt32();
                if (version != CacheFormatVersion) throw new InvalidDataException($"format version {version}, expected {CacheFormatVersion}");
            }
            using (var ds = new DeflateStream(stream, CompressionMode.Decompress, true))
            using (var r = new BinaryReader(ds))
            {
                int count = r.ReadInt32();
                if (count < 0 || count > MaxCachedZones) throw new InvalidDataException($"zone count {count} out of range");
                for (int i = 0; i < count; i++)
                {
                    var zone = new Vector2s(r.ReadInt16(), r.ReadInt16());
                    byte[] raw = r.ReadBytes(Cells * 2);
                    if (raw.Length != Cells * 2) throw new InvalidDataException("truncated zone");
                    var cm = new short[Cells];
                    Buffer.BlockCopy(raw, 0, cm, 0, raw.Length);
                    cache[zone] = new Entry { Cm = cm };
                }
            }
        }

        private static void WriteCache(Stream stream, List<KeyValuePair<Vector2s, short[]>> entries)
        {
            using (var header = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                header.Write(CacheMagic);
                header.Write(CacheFormatVersion);
            }
            var raw = new byte[Cells * 2];
            using (var ds = new DeflateStream(stream, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(ds))
            {
                w.Write(entries.Count);
                foreach (var kv in entries)
                {
                    w.Write((short)kv.Key.x);
                    w.Write((short)kv.Key.y);
                    Buffer.BlockCopy(kv.Value, 0, raw, 0, raw.Length);
                    w.Write(raw);
                }
            }
        }
    }
}
