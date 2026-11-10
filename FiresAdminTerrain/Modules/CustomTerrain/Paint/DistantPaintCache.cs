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
    /// Distant terrain PAINT. Distant terrain — vanilla TerrainLod heightmaps or
    /// our FarQuilt lod tiles — is built purely from worldgen: the zones'
    /// TerrainComp ZDOs never instantiate out there, so painted terrain (carved
    /// rivers' beds) hard-stops at the loaded-zone edge and a river visibly
    /// "gets cut off" seen from any height.
    ///
    /// Two paint layers, one compositor (OverlayMask):
    ///   CACHE  every time a loaded zone's TerrainComp composites onto its
    ///          heightmap (ApplyToHeightmap postfix — the clearedMask arg IS the
    ///          final composite: worldgen base + player paint), grab its 65x65
    ///          pixels and persist them per world (deflate) so every zone the
    ///          player has EVER visited keeps its paint across sessions.
    ///   WET    the worldgen river-shore band (VoxelZoneStreamer.PaintWet01) is a
    ///          pure function of world position over whole-world baked grids — it
    ///          needs no cache and paints never-visited zones too. Layered at
    ///          APPLY time, never into the cache: the near-field wet stamp runs
    ///          AFTER the capture hook, so cached masks are always wet-free
    ///          player/vanilla paint and never need migrating.
    ///
    /// Consumers:
    ///   * FarQuilt lod tiles (the live horizon in voxel mode) — per-tile masks
    ///     composed in FarQuilt.DrainMasks and bound via MaterialPropertyBlock.
    ///   * Vanilla TerrainLod heightmaps (fallback when the Fires lod is off) —
    ///     Regenerate postfix overlays onto the tile's OWN worldgen base mask at
    ///     the tile's REAL bounds (3x3 tiles of ~800m, ~10m texels — the old
    ///     single-zone inject stretched one 64m zone mask across a whole tile).
    ///
    /// Never-visited zones keep the worldgen base + wet band (no wrong data, just
    /// no player paint). Rendering-only: inert on dedicated servers / batch mode.
    /// </summary>
    public static class DistantPaintCache
    {
        private static ConfigEntry<bool> s_enabled;

        private sealed class Entry { public int Res; public Color32[] Pixels; }
        private static readonly Dictionary<Vector2s, Entry> s_cache = new Dictionary<Vector2s, Entry>();
        private static bool s_dirty;
        private static Task s_saving;
        private static volatile bool s_saveFailed;
        private static string s_loadedPath;         // path the current cache was loaded from (null = not loaded)
        private static float s_nextFlush;
        // Vanilla TerrainLod heightmaps we've injected into — texture reused across re-Regenerates.
        private static readonly Dictionary<Heightmap, Texture2D> s_injected = new Dictionary<Heightmap, Texture2D>();

        // Memory line: cached zone pixels (managed) and injected LOD textures (native, counted by size).
        internal static void MemoryReport(out int zones, out long pixelBytes, out int injected, out long textureBytes)
        {
            zones = s_cache.Count; pixelBytes = 0;
            foreach (var e in s_cache.Values) if (e != null && e.Pixels != null) pixelBytes += e.Pixels.Length * 4L;
            injected = s_injected.Count; textureBytes = 0;
            foreach (var t in s_injected.Values) if (t != null) textureBytes += (long)t.width * t.height * 4L;
        }
        // TerrainLod heightmaps that regenerated before the worldgen water field
        // was queryable — binding then would freeze a wet-free horizon (the
        // near-field stamp's load-window lesson). Retried from VanillaLodSweep.
        private static readonly HashSet<Heightmap> s_lodPending = new HashSet<Heightmap>();
        private static readonly List<Heightmap> s_lodScratch = new List<Heightmap>(16);

        private static readonly AccessTools.FieldRef<Heightmap, bool> s_isDistantLod
            = AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");
        private static readonly AccessTools.FieldRef<Heightmap, Material> s_materialInstance
            = AccessTools.FieldRefAccess<Heightmap, Material>("m_materialInstance");
        private static int s_propClearedMask;

        public static void Bind(ConfigFile config)
        {
            s_enabled = config.Bind("02 - Terrain", "02 Distant paint", true,   // Was [07 - Distant Paint] 01 Enabled
                "Show terrain PAINT (paved/cultivated/dirt — e.g. carved river beds and the worldgen river "
                + "shore sand) on the distant low-LOD terrain beyond the loaded zones. Player paint is cached "
                + "per zone as zones load and persisted per world; the worldgen shore band is computed "
                + "directly, so rivers keep their beds out to the horizon even where you've never been. "
                + "Off = vanilla (paint pops out at the loaded-zone edge).");
            FatConfigRetire.Move(config, "07 - Distant Paint", "01 Enabled", s_enabled);
        }

        private static bool Active =>
            (s_enabled == null || s_enabled.Value)
            && !Application.isBatchMode
            && (ZNet.instance == null || !ZNet.instance.IsDedicated());

        // FarQuilt's gate for its per-tile masks — same switch, same surface.
        internal static bool MaskFeatureOn => Active;

        // ------------------------------------------------------------------ capture
        [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyToHeightmap))]
        private static class Capture
        {
            private static void Postfix(TerrainComp __instance, Texture2D clearedMask, Heightmap hm)
            {
                if (!Active || clearedMask == null || hm == null) return;
                try
                {
                    EnsureLoaded();
                    Vector2s zone = ZoneSystem.GetZone(hm.transform.position);
                    var px = clearedMask.GetPixels32();
                    KeepCultivationOnly(__instance, hm, px, clearedMask.width);
                    s_cache[zone] = new Entry { Res = clearedMask.width, Pixels = px };
                    s_cacheRev++;   // 0.2.1319: running spread LOD jobs restart
                    s_dirty = true;
                    FarQuilt.OnZonePaintChanged(zone);
                }
                catch { /* best-effort — never break terrain application */ }
            }
        }

        // 0.2.1308 ([lead]: Core 1060 writes its seasonal snow into the heightmap's RUNTIME mask G, sRGB-encoded, ~0.41 = 104/255 on
        // 0.29 m of snow; this cache copied that mask, and OverlayMask / the far tiles read G > 63 as cultivated soil = winter snow drawn
        // as tilled fields far away): outside world-gen Deep North the cached G is rebuilt from the TerrainComp's OWN stored paint - a
        // modified texel keeps the G an op stored there (real cultivation), an untouched one is 0 - so Core's runtime snow never reaches
        // the far tiles. Real Deep North keeps the mask's G (vanilla's own deep snow there). Debug count once per 30 s.
        private static readonly HarmonyLib.AccessTools.FieldRef<TerrainComp, Color[]> s_compPaint = HarmonyLib.AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly HarmonyLib.AccessTools.FieldRef<TerrainComp, bool[]> s_compMod = HarmonyLib.AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static int s_snowGDropped; private static float s_snowGLogAt;
        private static void KeepCultivationOnly(TerrainComp comp, Heightmap hm, Color32[] px, int res)
        {
            if (px == null || res < 2) return;
            Color[] cp = comp != null ? s_compPaint(comp) : null; bool[] cm = comp != null ? s_compMod(comp) : null;
            bool compOk = cp != null && cm != null && cp.Length == px.Length && cm.Length == px.Length;
            float span = hm.m_width * hm.m_scale, texel = span / (res - 1);
            Vector3 pos = hm.transform.position;
            float x0 = pos.x - span * 0.5f, z0 = pos.z - span * 0.5f;
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    int i = y * res + x;
                    if (WorldGenerator.IsDeepnorth(x0 + x * texel, z0 + y * texel)) continue;
                    float bG = compOk && cm[i] ? VoxelWorld.BaseG(hm, x, y) : 0f;   // 0.2.1312: proportional cut over the build-data base G, as the near compose
                    byte g = compOk && cm[i] ? (byte)Mathf.RoundToInt(Mathf.Clamp01((cp[i].g - bG) / Mathf.Max(1e-3f, 1f - bG)) * 255f) : (byte)0;
                    if (px[i].g != g) { if (px[i].g > 63 && g <= 63) s_snowGDropped++; px[i].g = g; }
                }
            if (s_snowGDropped > 0 && Time.unscaledTime >= s_snowGLogAt)
            {
                s_snowGLogAt = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[DistantPaint] far-tile cache: {s_snowGDropped} texel(s) whose runtime G (Core's seasonal snow) would have read as cultivated were reset to the stored cultivation (0.2.1308)");
                s_snowGDropped = 0;
            }
        }

        // ------------------------------------------------------------------ compositor
        // Overlay both distant-paint layers onto an existing RGBA32 pixel grid
        // where texel (x,y) covers world (cornerX + x*texel, cornerZ + y*texel):
        //   1. cached zone masks — replace (the cache IS the zone's final
        //      composite, worldgen base included);
        //   2. the wet band — the near-field stamp's exact math (r deepens
        //      toward wet, alpha follows, cultivated/paved player paint wins),
        //      so a lod tile and the loaded zone it borders read identical
        //      paint at the shared edge.
        // `wetPending` = worldgen is active but its water field is not
        // queryable yet; the caller MUST retry later instead of binding.
        internal static void OverlayMask(float cornerX, float cornerZ, float texelMeters, int res,
            Color32[] px, out bool changed, out bool wetPending)
        {
            changed = false;
            wetPending = FiresWorldGenBase.Active && !FiresWorldGenBase.WaterSurfaceReady;
            if (wetPending || px == null || px.Length < res * res || res < 2) return;
            EnsureLoaded();
            // 0.2.1319: the two passes are per-row helpers now (shared with the spread LOD job below); same pixels, same order per texel
            if (s_cache.Count > 0)
                for (int y = 0; y < res; y++) OverlayCacheRow(cornerX, cornerZ, texelMeters, res, px, y, ref changed);
            if (!FiresWorldGenBase.Active) return;
            if (!AnyWetPaint(cornerX, cornerZ, texelMeters, res)) return;
            for (int y = 0; y < res; y++) OverlayWetRow(cornerX, cornerZ, texelMeters, res, px, y, ref changed);
        }

        // 1. cached zone masks for one row (replace).
        private static void OverlayCacheRow(float cornerX, float cornerZ, float texelMeters, int res, Color32[] px, int y, ref bool changed)
        {
            int lastZx = int.MinValue, lastZz = int.MinValue;
            Entry e = null;
            float wz = cornerZ + y * texelMeters;
            int zz = Mathf.FloorToInt((wz + 32f) / 64f);
            int row = y * res;
            for (int x = 0; x < res; x++)
            {
                float wx = cornerX + x * texelMeters;
                int zx = Mathf.FloorToInt((wx + 32f) / 64f);
                if (zx != lastZx || zz != lastZz)
                {
                    lastZx = zx; lastZz = zz;
                    s_cache.TryGetValue(new Vector2s(zx, zz), out e);
                }
                if (e == null || e.Pixels == null || e.Res < 2) continue;
                int sx = Mathf.Clamp(Mathf.RoundToInt((wx - (zx * 64f - 32f)) * (e.Res - 1) / 64f), 0, e.Res - 1);
                int sz = Mathf.Clamp(Mathf.RoundToInt((wz - (zz * 64f - 32f)) * (e.Res - 1) / 64f), 0, e.Res - 1);
                px[row + x] = e.Pixels[sz * e.Res + sx];
                changed = true;
            }
        }

        // Whole-grid early out from the BAKED zone flags, +1 zone border —
        // the band reaches ~12m past wet cells (near-field stamp precedent).
        private static bool AnyWetPaint(float cornerX, float cornerZ, float texelMeters, int res)
        {
            float spanM = texelMeters * (res - 1);
            int zMinX = Mathf.FloorToInt((cornerX + 32f) / 64f) - 1;
            int zMaxX = Mathf.FloorToInt((cornerX + spanM + 32f) / 64f) + 1;
            int zMinZ = Mathf.FloorToInt((cornerZ + 32f) / 64f) - 1;
            int zMaxZ = Mathf.FloorToInt((cornerZ + spanM + 32f) / 64f) + 1;
            for (int zz = zMinZ; zz <= zMaxZ; zz++)
                for (int zx = zMinX; zx <= zMaxX; zx++)
                    if ((FiresWorldGenBase.ZoneWaterFlags(zx, zz) & FiresWorldGenBase.ZW_PAINT) != 0)
                        return true;
            return false;
        }

        // 2. the wet band for one row.
        private static void OverlayWetRow(float cornerX, float cornerZ, float texelMeters, int res, Color32[] px, int y, ref bool changed)
        {
            float wz = cornerZ + y * texelMeters;
            int row = y * res;
            for (int x = 0; x < res; x++)
            {
                float wet = VoxelZoneStreamer.PaintWet01(cornerX + x * texelMeters, wz);
                if (wet <= 0.03f) continue;
                var c = px[row + x];
                if (c.g > 63 || c.b > 63) continue;   // cultivated/paved player paint wins
                byte r2 = (byte)Mathf.Max(c.r, Mathf.RoundToInt(wet * 255f));
                // ASHLANDS: alpha is LAVA — wet paint keeps its hands off
                // (the shoreline lava band). r (dirt) is safe.
                byte a2 = FiresWorldGenBase.BiomeCodeAt(cornerX + x * texelMeters, wz) == 6
                    ? c.a : (byte)Mathf.RoundToInt(Mathf.Lerp(c.a, wet * 255f, wet));
                if (r2 != c.r || a2 != c.a)
                {
                    px[row + x] = new Color32(r2, c.g, c.b, a2);
                    changed = true;
                }
            }
        }

        // ------------------------------------------------------------------ vanilla TerrainLod inject
        // From FAT's one Heightmap.Regenerate hook (FatVanillaHooks), after the voxel streamer's rewrap mark.
        internal static void OnHeightmapRegenerate(Heightmap heightmap)
        {
            if (!Active || heightmap == null) return;
            try
            {
                if (!s_isDistantLod(heightmap)) { FlushMaybe(); return; }
                EnsureLoaded();
                ApplyVanillaLod(heightmap);
            }
            catch { }
        }

        private static void ApplyVanillaLod(Heightmap hm)
        {
            Material mat = s_materialInstance(hm);
            var own = hm.GetPaintMask();
            if (mat == null || own == null) return;
            int res = own.width;
            if (res < 2) return;
            float span = hm.m_width * hm.m_scale;
            if (span <= 0f) return;
            float texel = span / (res - 1);
            Vector3 pos = hm.transform.position;

            // 0.2.1319 (PLAN_fat_frame_spread item 11): key "Distant paint copy spread" composes the rows over frames into this
            // job's own buffer (PumpLodJobs, under FatBudget) and uploads + binds ONCE when every row is done; the tile keeps its
            // current binding until then (never a half-composed mask). Off = the old one-frame compose below.
            if (SpreadOn)
            {
                bool pending = FiresWorldGenBase.Active && !FiresWorldGenBase.WaterSurfaceReady;
                if (pending) { s_lodPending.Add(hm); s_lodJobs.Remove(hm); return; }
                s_lodPending.Remove(hm);
                EnsureLoaded();
                StartLodJob(hm, own, res, pos.x - span * 0.5f, pos.z - span * 0.5f, texel);
                return;
            }

            // Base = the tile's OWN worldgen mask — richer than a flat fill
            // (keeps whatever vanilla's builder painted, e.g. Ashlands lava alpha).
            var px = own.GetPixels32();
            OverlayMask(pos.x - span * 0.5f, pos.z - span * 0.5f, texel, res, px,
                out bool changed, out bool wetPending);
            if (wetPending) { s_lodPending.Add(hm); return; }
            s_lodPending.Remove(hm);
            if (!changed) return;   // nothing beyond its own base — leave vanilla's binding alone
            BindLod(hm, mat, res, px);
        }

        private static void BindLod(Heightmap hm, Material mat, int res, Color32[] px)
        {
            if (s_propClearedMask == 0) s_propClearedMask = Shader.PropertyToID("_ClearedMaskTex");
            if (!s_injected.TryGetValue(hm, out Texture2D tex) || tex == null)
            {
                tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
                { name = "FiresDistantPaint", wrapMode = TextureWrapMode.Clamp };
                s_injected[hm] = tex;
            }
            if (tex.width != res) tex.Reinitialize(res, res, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            mat.SetTexture(s_propClearedMask, tex);
        }

        // ---- 0.2.1319 spread LOD compose jobs
        private static bool SpreadOn => VoxelZoneStreamer.HasDriver && (VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.DistantPaintSpread != null && VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.DistantPaintSpread.Value);
        private sealed class LodJob { public Color32[] Px; public int Res, Row, Rev, Restarts; public float X0, Z0, Texel; public bool Cache, Wet, Changed; }
        private static readonly Dictionary<Heightmap, LodJob> s_lodJobs = new Dictionary<Heightmap, LodJob>();
        private static readonly List<Heightmap> s_lodJobScratch = new List<Heightmap>(16);
        private static int s_cacheRev;   // bumped by every capture: a job that read rows before a capture restarts (same final pixels as a one-frame compose after it)
        private const int LodJobMaxRestarts = 3;
        private static bool s_lodSpreadProof;
        private static readonly VerdantsAscent.Terrain.Voxel.FatSpreadStat s_lodSpread = new VerdantsAscent.Terrain.Voxel.FatSpreadStat("distant paint LOD compose");

        private static void StartLodJob(Heightmap hm, Texture2D own, int res, float x0, float z0, float texel)
        {
            var px = own.GetPixels32();
            if (px == null || px.Length < res * res) { s_lodJobs.Remove(hm); return; }
            int restarts = s_lodJobs.TryGetValue(hm, out var old) && old != null ? old.Restarts : 0;
            s_lodJobs[hm] = new LodJob
            {
                Px = px, Res = res, Row = 0, Rev = s_cacheRev, Restarts = restarts, X0 = x0, Z0 = z0, Texel = texel,
                Cache = s_cache.Count > 0,
                Wet = FiresWorldGenBase.Active && AnyWetPaint(x0, z0, texel, res),
            };
            if (!s_lodSpreadProof) { s_lodSpreadProof = true; FiresLogger.LogInfo("[DistantPaint] distant LOD paint composes over frames into a scratch buffer, one upload when done (0.2.1319)"); }
        }

        // Every frame from the streamer driver: rows of the running jobs while FatBudget has time (at least one row a frame).
        internal static void PumpLodJobs()
        {
            if (s_lodJobs.Count == 0) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double allow = System.Math.Max(0.1, System.Math.Min(1.0, VerdantsAscent.Terrain.Voxel.FatBudget.LeftMs()));
            long limit = t0 + (long)(allow * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            s_lodJobScratch.Clear();
            foreach (var kv in s_lodJobs) s_lodJobScratch.Add(kv.Key);
            bool any = false;
            for (int i = 0; i < s_lodJobScratch.Count; i++)
            {
                var hm = s_lodJobScratch[i];
                if (hm == null || !Active) { s_lodJobs.Remove(hm); continue; }
                var job = s_lodJobs[hm];
                if (job.Rev != s_cacheRev && job.Cache && job.Restarts < LodJobMaxRestarts)
                {
                    // a zone mask changed under the rows already read: start over from the tile's own mask
                    job.Restarts++;
                    var own = hm.GetPaintMask();
                    if (own == null || own.width != job.Res) { s_lodJobs.Remove(hm); continue; }
                    StartLodJob(hm, own, job.Res, job.X0, job.Z0, job.Texel);
                    job = s_lodJobs[hm];
                }
                job.Rev = s_cacheRev;
                bool cacheNow = s_cache.Count > 0;
                while (job.Row < job.Res)
                {
                    if (any && System.Diagnostics.Stopwatch.GetTimestamp() >= limit) break;
                    if (job.Cache && cacheNow) OverlayCacheRow(job.X0, job.Z0, job.Texel, job.Res, job.Px, job.Row, ref job.Changed);
                    if (job.Wet) OverlayWetRow(job.X0, job.Z0, job.Texel, job.Res, job.Px, job.Row, ref job.Changed);
                    job.Row++; any = true;
                }
                if (job.Row < job.Res) break;
                s_lodJobs.Remove(hm);
                Material mat = s_materialInstance(hm);
                if (job.Changed && mat != null) BindLod(hm, mat, job.Res, job.Px);   // not changed = leave vanilla's binding alone (as before)
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= limit) break;
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            VerdantsAscent.Terrain.Voxel.FatBudget.Spend(VerdantsAscent.Terrain.Voxel.FatBudget.Who.Spread, ms);
            s_lodSpread.Note(ms);
        }

        // A TerrainLod heightmap that is destroyed used to leave its injected texture behind
        // (native memory, one per LOD rebuild across teleports). Every 10 s the dead heightmaps' textures are destroyed;
        // a world change (now) does the same at once. Live heightmaps keep their texture and binding, as before.
        private static float s_nextInjectedPrune;
        private static readonly List<Heightmap> s_deadInjected = new List<Heightmap>();

        private static void PruneInjected(bool now)
        {
            if (!now && Time.time < s_nextInjectedPrune) return;
            s_nextInjectedPrune = Time.time + 10f;
            s_deadInjected.Clear();
            foreach (var kv in s_injected) if (kv.Key == null) s_deadInjected.Add(kv.Key);
            for (int i = 0; i < s_deadInjected.Count; i++)
            {
                var hm = s_deadInjected[i];
                if (s_injected.TryGetValue(hm, out Texture2D tex) && tex != null) UnityEngine.Object.Destroy(tex);
                s_injected.Remove(hm);
            }
            if (s_deadInjected.Count > 0)
                FiresLogger.LogDebug($"[DistantPaintCache] released {s_deadInjected.Count} injected texture(s) of destroyed LOD heightmaps ({s_injected.Count} live)");
            s_deadInjected.Clear();
        }

        // Ticked from VoxelZoneStreamer — re-runs TerrainLod tiles that built
        // before the water field was ready, once it is.
        internal static void VanillaLodSweep()
        {
            PruneInjected(false);
            if (s_lodPending.Count == 0) return;
            if (!FiresWorldGenBase.Active) { s_lodPending.Clear(); return; }
            if (!FiresWorldGenBase.WaterSurfaceReady || !Active) return;
            s_lodScratch.Clear();
            s_lodScratch.AddRange(s_lodPending);
            s_lodPending.Clear();
            for (int i = 0; i < s_lodScratch.Count; i++)
                if (s_lodScratch[i] != null)
                    try { ApplyVanillaLod(s_lodScratch[i]); } catch { }
        }

        // ------------------------------------------------------------------ persistence
        private const int CacheMagic = 0x43504446;
        private const int CacheFormatVersion = 2;
        private const int MaxCachedZones = 1 << 17;
        private const int MaxMaskResolution = 1025;
        private const int BytesPerPixel = 4;

        private static string ResolvePath()
        {
            try
            {
                World world = null;
                if (ZNet.instance != null) world = ZNet.World;
                if (world == null || string.IsNullOrEmpty(world.m_name)) return null;
                string dir = global::VerdantsAscent.FiresCachePaths.For("FiresAdminTerrain", "DistantPaint");   // Out of config
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, world.m_name + ".fdp");
            }
            catch { return null; }
        }

        private static void EnsureLoaded()
        {
            string path = ResolvePath();
            if (path == null || path == s_loadedPath) return;
            s_cache.Clear();
            PruneInjected(true);
            s_lodJobs.Clear(); s_cacheRev++;   // 0.2.1319
            s_lodPending.Clear();
            s_dirty = false;
            s_loadedPath = path;
            if (!File.Exists(path)) return;
            try
            {
                using (var fs = File.OpenRead(path))
                    ReadCache(fs, s_cache);
                FiresLogger.LogInfo($"[DistantPaintCache] Loaded {s_cache.Count} painted zone(s) from {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                s_cache.Clear();
                FiresLogger.LogWarning($"[DistantPaintCache] {Path.GetFileName(path)} not loaded ({ex.Message}); starting empty, zones re-cache as they load.");
            }
        }

        // The periodic flush serializes off the main thread: deflating every cached zone took a 274 ms frame inside a
        // Heightmap.Regenerate. Captured masks are never written in place (a capture swaps in a new Entry), so a
        // snapshot of the entries can be written while play goes on.
        private static void FlushMaybe()
        {
            if (s_saveFailed) { s_saveFailed = false; s_dirty = true; }
            if (!s_dirty || s_loadedPath == null || Time.realtimeSinceStartup < s_nextFlush) return;
            if (s_saving != null && !s_saving.IsCompleted) return;
            s_nextFlush = Time.realtimeSinceStartup + 60f;
            var snapshot = new List<KeyValuePair<Vector2s, Entry>>(s_cache);
            string path = s_loadedPath;
            s_dirty = false;
            s_saving = Task.Run(() =>
            {
                if (!TryWrite(path, snapshot)) s_saveFailed = true;
            });
        }

        internal static void Save()
        {
            try { s_saving?.Wait(); } catch { }
            s_saving = null;
            if (s_saveFailed) { s_saveFailed = false; s_dirty = true; }
            if (!s_dirty || s_loadedPath == null) return;
            if (TryWrite(s_loadedPath, new List<KeyValuePair<Vector2s, Entry>>(s_cache))) s_dirty = false;
        }

        private static bool TryWrite(string path, List<KeyValuePair<Vector2s, Entry>> entries)
        {
            string temp = path + ".tmp";
            try
            {
                using (var fs = File.Create(temp))
                    WriteCache(fs, entries);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[DistantPaintCache] Save failed: {ex.Message}");
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return false;
            }
        }

        // File layout: int magic "FDPC", int version, then a deflate body of
        // int count and per zone short x, short y, int res, res*res RGBA bytes.
        private static void ReadCache(Stream stream, Dictionary<Vector2s, Entry> cache)
        {
            using (var header = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (stream.Length < 8 || header.ReadInt32() != CacheMagic)
                    throw new InvalidDataException("unrecognized file format");
                int version = header.ReadInt32();
                if (version != CacheFormatVersion)
                    throw new InvalidDataException($"format version {version}, expected {CacheFormatVersion}");
            }

            using (var ds = new DeflateStream(stream, CompressionMode.Decompress, true))
            using (var r = new BinaryReader(ds))
            {
                int count = r.ReadInt32();
                if (count < 0 || count > MaxCachedZones)
                    throw new InvalidDataException($"zone count {count} out of range");
                for (int i = 0; i < count; i++)
                {
                    var zone = new Vector2s(r.ReadInt16(), r.ReadInt16());
                    int res = r.ReadInt32();
                    if (res < 2 || res > MaxMaskResolution)
                        throw new InvalidDataException($"mask resolution {res} out of range");
                    int byteCount = res * res * BytesPerPixel;
                    byte[] raw = r.ReadBytes(byteCount);
                    if (raw.Length != byteCount)
                        throw new InvalidDataException("truncated zone mask");
                    var pixels = new Color32[res * res];
                    for (int p = 0; p < pixels.Length; p++)
                        pixels[p] = new Color32(raw[p * 4], raw[p * 4 + 1], raw[p * 4 + 2], raw[p * 4 + 3]);
                    cache[zone] = new Entry { Res = res, Pixels = pixels };
                }
            }
        }

        private static void WriteCache(Stream stream, List<KeyValuePair<Vector2s, Entry>> cache)
        {
            using (var header = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                header.Write(CacheMagic);
                header.Write(CacheFormatVersion);
            }

            using (var ds = new DeflateStream(stream, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(ds))
            {
                w.Write(cache.Count);
                foreach (var kv in cache)
                {
                    w.Write((short)kv.Key.x);
                    w.Write((short)kv.Key.y);
                    w.Write(kv.Value.Res);
                    var px = kv.Value.Pixels;
                    var raw = new byte[px.Length * BytesPerPixel];
                    for (int p = 0; p < px.Length; p++)
                    {
                        raw[p * 4] = px[p].r; raw[p * 4 + 1] = px[p].g;
                        raw[p * 4 + 2] = px[p].b; raw[p * 4 + 3] = px[p].a;
                    }
                    w.Write(raw);
                }
            }
        }
    }
}
