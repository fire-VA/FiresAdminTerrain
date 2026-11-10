using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // DYNAMIC SNOW on voxel ground (0.2.1027; Fire 10-24 via [lead]: snow depth done like vanilla 1.0's Deep North, one source of
    // truth). Core's FiresCore.Snow.SnowField owns the snow; FAT pulls it. Each zone gets one R8 depth texture (SnowField
    // FillZoneDepth: row = z, column = x, corner to corner over the 64 m zone) that every voxel chunk of the zone binds through its
    // MaterialPropertyBlock (_FiresSnowZoneTex/_FiresSnowZoneRect/_FiresSnowZoneOn); SnowField's GlobalCover and DepthScale are
    // the shader globals _FiresSnowCover/_FiresSnowDepthScale. FiresVoxelHeightmap (fatvoxelshader 0.2.1027) whitens and raises
    // the up-facing ground by it. On ZoneChanged the zone's texture is refilled in place, so the chunks need no rebind.
    // SnowField is read by reflection: an older Core (no SnowField) or Dynamic snow off leaves every chunk at _FiresSnowZoneOn 0,
    // which is exactly the shader before.
    internal static class VoxelSnowDepth
    {
        // 0.2.1278 (Fire 10-30 "Light slow"): vanilla's slow scaled by the key outside Deep North / Mountain (those keep vanilla).
        // 0.2.1309: moved here from the removed heightmap snow skin (voxel snow keeps it; heightmap snow is vanilla's own now).
        private static Heightmap.Biome s_slowBiome = Heightmap.Biome.None;
        internal static float SlowFactorAt(Character c, Vector3 p)
        {
            var b = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(p) : Heightmap.Biome.None;
            bool snowBiome = b == Heightmap.Biome.DeepNorth || b == Heightmap.Biome.Mountain;
            float f = snowBiome ? 1f : (FiresVoxelConfig.DeepSnowSlowOutside != null ? Mathf.Clamp01(FiresVoxelConfig.DeepSnowSlowOutside.Value) : 0.5f);
            if (c == Player.m_localPlayer && b != s_slowBiome)
            {
                s_slowBiome = b;
                FiresLogger.LogDebug($"[Snow] slow factor {f:0.00} in {b} ({(snowBiome ? "snow biome: vanilla" : "outside the snow biomes: key")})");
            }
            return f;
        }
        private const int Size = 32;
        private const float ZoneSize = 64f;
        private static readonly int ZoneOnId = Shader.PropertyToID("_FiresSnowZoneOn");
        private static readonly int ZoneRectId = Shader.PropertyToID("_FiresSnowZoneRect");
        private static readonly int ZoneTexId = Shader.PropertyToID("_FiresSnowZoneTex");
        private static readonly int CoverId = Shader.PropertyToID("_FiresSnowCover");
        private static readonly int DepthScaleId = Shader.PropertyToID("_FiresSnowDepthScale");
        private static readonly int TrenchId = Shader.PropertyToID("_FiresSnowTrench");   // 0.2.1094 walk-trail trench
        private static float s_trenchSaid = -1f;

        private static bool s_bound, s_present;
        private static PropertyInfo s_enabled, s_cover, s_scale;
        private static Func<Vector2s, byte[], int, bool> s_fill;
        private static readonly Dictionary<Vector2s, Texture2D> s_tex = new Dictionary<Vector2s, Texture2D>();
        private static readonly HashSet<Vector2s> s_dirty = new HashSet<Vector2s>();
        private static readonly object s_dirtyLock = new object();
        private static readonly byte[] s_buf = new byte[Size * Size];
        private static float s_lastCover = -1f, s_lastScale = -1f;
        private static bool s_lastEnabled;
        private static int s_fills, s_snowyFills;
        private static float s_nextLog;
        private static float s_rebindAt, s_rebindLogAt;

        // SnowField's Enabled, read by reflection (false when Core has no SnowField). BiomeOverride.ApplyPieceSnow asks it too.
        internal static bool SnowFieldEnabled
        {
            get
            {
                Bind();
                if (!s_present || s_enabled == null) return false;
                try { return (bool)s_enabled.GetValue(null, null); } catch { return false; }
            }
        }

        // 0.2.1407 ([seasons] 11-07: Vedr 0.8.158's "Ground snow" key ships OFF, and with it off SnowEnabled is false and every depth 0;
        // 0.2.1406 bound Vedr on ApiVersion >= 15 alone, once, so voxel ground would have lost its snow). Both sources are bound; Vedr is
        // the one in use only while VedrApi.GroundSnowActive is true (Vedr draws ground snow, Core's stands down), re-read once a second
        // (SourceTick), so a live toggle of Vedr's key switches over. Core SnowField otherwise; none without either. A switch refills every
        // voxel zone's snow; events from the source not in use are ignored. One Info line per source in use.
        private sealed class Src
        {
            public string Name;
            public Type Type;
            public PropertyInfo Enabled, Cover, Scale;
            public Func<Vector2s, byte[], int, bool> Fill;
            public Func<Vector3, float> DepthAt;
            public float MaxDepth = 1.2f;
            public bool ZoneEv, CellsEv;
        }
        private static Src s_core, s_vedr, s_cur;
        private static PropertyInfo s_vedrActive;
        private static float s_sourceCheckAt;

        private static Src BindSrc(Type t, string name, string nEnabled, string nCover, string nScale, string nFill, string nMax, string nDepthAt, string nZone, string nCells)
        {
            const BindingFlags SF = BindingFlags.Public | BindingFlags.Static;
            var s = new Src { Name = name, Type = t, Enabled = t.GetProperty(nEnabled, SF), Cover = t.GetProperty(nCover, SF), Scale = t.GetProperty(nScale, SF) };
            var fill = t.GetMethod(nFill, SF, null, new[] { typeof(Vector2s), typeof(byte[]), typeof(int) }, null);
            if (fill == null || s.Enabled == null) { FiresLogger.LogWarning($"[FiresVoxel] snow: {name} found but {nFill}/{nEnabled} missing - not used"); return null; }
            s.Fill = (Func<Vector2s, byte[], int, bool>)Delegate.CreateDelegate(typeof(Func<Vector2s, byte[], int, bool>), fill);
            // 0.2.1043 ([ghost]): depth 1 = MaxDepthMetres metres (vanilla Deep North 1.2); DepthAt for the slow.
            var maxField = t.GetField(nMax, SF);
            var maxProp = maxField == null ? t.GetProperty(nMax, SF) : null;
            try { object mv = maxField != null ? maxField.GetValue(null) : maxProp?.GetValue(null, null); if (mv is float mf && mf > 0f) s.MaxDepth = mf; } catch { }
            var depthAt = t.GetMethod(nDepthAt, SF, null, new[] { typeof(Vector3) }, null);
            if (depthAt != null) s.DepthAt = (Func<Vector3, float>)Delegate.CreateDelegate(typeof(Func<Vector3, float>), depthAt);
            var ev = t.GetEvent(nZone, SF);
            if (ev != null) { ev.AddEventHandler(null, new Action<Vector2s>(z => { if (s_cur == s) OnZoneChanged(z, CauseZoneChanged); })); s.ZoneEv = true; }   // 0.2.1322 cause tag
            // 0.2.1283: piece / location / path-mask changes come as CellsChanged; the zone texture refills through the same path.
            var cev = t.GetEvent(nCells, SF);
            if (cev != null) { cev.AddEventHandler(null, new Action<Vector2s, Rect>((z, r) => { if (s_cur == s) OnZoneChanged(z, CauseCellsChanged); })); s.CellsEv = true; }
            return s;
        }

        private static void Bind()
        {
            if (s_bound) return;
            s_bound = true;
            const BindingFlags SF = BindingFlags.Public | BindingFlags.Static;
            try
            {
                var vedr = Type.GetType("Vedr.VedrApi, Norger.Vedr", false);
                object ver = vedr?.GetField("ApiVersion", SF)?.GetValue(null) ?? vedr?.GetProperty("ApiVersion", SF)?.GetValue(null, null);
                if (vedr != null && ver != null && Convert.ToInt32(ver) >= 15)
                {
                    s_vedrActive = vedr.GetProperty("GroundSnowActive", SF);
                    if (s_vedrActive == null) FiresLogger.LogWarning($"[FiresVoxel] snow: VedrApi {ver} has no GroundSnowActive - Vedr's depth not used, Core SnowField stays the source");
                    else s_vedr = BindSrc(vedr, $"Vedr (VedrApi {ver})", "SnowEnabled", "SnowGlobalCover", "SnowDepthScale", "SnowFillZoneDepth", "SnowMaxDepthMetres", "SnowDepthAt", "SnowZoneChanged", "SnowCellsChanged");
                }
            }
            catch (Exception ex) { FiresLogger.LogDebug($"[FiresVoxel] snow: VedrApi snow depth not bound ({ex.Message})"); s_vedr = null; }
            try
            {
                Type core = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { core = asm.GetType("FiresCore.Snow.SnowField", false); if (core != null) break; }
                if (core != null) s_core = BindSrc(core, "Core SnowField", "Enabled", "GlobalCover", "DepthScale", "FillZoneDepth", "MaxDepthMetres", "DepthAt", "ZoneChanged", "CellsChanged");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] snow: binding Core SnowField threw ({ex.Message})"); s_core = null; }
            s_present = s_core != null || s_vedr != null;
            if (!s_present) { FiresLogger.LogInfo("[FiresVoxel] snow: depth source none (no Vedr ground snow, no Core SnowField) - voxel snow depth off (0.2.1407)"); return; }
            Use(Pick(), false);
        }

        private static bool VedrActive()
        {
            if (s_vedr == null || s_vedrActive == null) return false;
            try { return (bool)s_vedrActive.GetValue(null, null); } catch { return false; }
        }

        private static Src Pick() => VedrActive() ? s_vedr : (s_core ?? null);

        private static void Use(Src s, bool refill)
        {
            if (s == s_cur && s_enabled != null) return;
            var was = s_cur;
            s_cur = s;
            s_fieldType = s?.Type;
            s_enabled = s?.Enabled; s_cover = s?.Cover; s_scale = s?.Scale;
            s_fill = s?.Fill; s_depthAt = s?.DepthAt;
            s_maxDepth = s != null ? s.MaxDepth : 1.2f;
            string why = s == null ? "none (Vedr's ground snow is off and Core has no SnowField)"
                : s == s_vedr ? "Vedr draws the ground snow (GroundSnowActive)"
                : s_vedr != null ? "Vedr's ground snow is off (GroundSnowActive false)" : "no Vedr ground snow";
            FiresLogger.LogInfo($"[FiresVoxel] snow: depth source {(s != null ? s.Name : "none")} (max {s_maxDepth:0.0} m): {why}{(was != null ? $"; was {was.Name}" : "")} (0.2.1407)");
            if (s != null) FiresLogger.LogDebug($"[FiresVoxel] snow: pulling {s.Name} (zone event {(s.ZoneEv ? "subscribed" : "MISSING - textures fill at chunk build only")}, cells event {(s.CellsEv ? "subscribed" : "not there")})");
            if (!refill) return;
            // every voxel zone's depth comes from the new source: drop the grids and mark every textured zone for a refill
            s_depthGrid.Clear();
            lock (s_dirtyLock) foreach (var z in s_tex.Keys) { s_dirty.Add(z); NoteCause(z, CauseSourceSwitch); }
            JobAllGridsChanged();
            s_lastEnabled = !SnowFieldEnabledRaw();   // Tick re-announces on / off for the new source
        }

        private static bool SnowFieldEnabledRaw()
        {
            if (s_enabled == null) return false;
            try { return (bool)s_enabled.GetValue(null, null); } catch { return false; }
        }

        // From Tick, once a second: Vedr's ground snow switched on or off -> the other source.
        private static void SourceTick()
        {
            if (s_vedr == null || Time.unscaledTime < s_sourceCheckAt) return;
            s_sourceCheckAt = Time.unscaledTime + 1f;
            var want = Pick();
            if (want != s_cur) Use(want, true);
        }

        // ── 0.2.1043: snow DEPTH in metres and vanilla's deep-snow slow on voxel ground ([ghost], Fire: "barely covers our
        // boots", he wants it waist deep like vanilla's deepest Deep North). Depth 1 = MaxDepthMetres (Core; 1.2 m when Core
        // has none). Vanilla slows a character in deep snow only through the heightmap in Deep North (Character.UpdateWalking);
        // FAT owns voxel ground, so the same rule runs here on voxel zones wherever SnowField is on: above
        // m_deepSnowSlowStartHeight (0.3 m) the speed drops linearly to m_deepSnowSlowMax at m_deepSnowSlowMaxHeight (2 m),
        // for players and creatures, and vanilla's snow-walk puff (m_deepSnowWalkObj) is left behind as in vanilla.
        private static readonly int MaxDepthId = Shader.PropertyToID("_FiresSnowMaxDepth");
        private static float s_maxDepth = 1.2f;
        private static Func<Vector3, float> s_depthAt;
        private static bool s_slowLogged;

        internal static float MaxDepthMetres => s_maxDepth;

        // 0.2.1086 (Fire: snow on painted Deep North, fat_biome_probe): Core's DepthAt in metres and a fresh FillZoneDepth of the
        // zone (texels with snow of Size x Size), straight from Core, no cache. -1 = Core not bound.
        internal static float ProbeDepthMetres(Vector3 p) => s_depthAt == null ? -1f : s_depthAt(p) * s_maxDepth;

        internal static int ProbeZoneFill(Vector2s zone)
        {
            if (s_fill == null) return -1;
            var buf = new byte[Size * Size];
            bool snowy;
            try { snowy = s_fill(zone, buf, Size); } catch { return -1; }
            int n = 0;
            if (snowy) foreach (var b in buf) if (b != 0) n++;
            return n;
        }

        // 0.2.1139 (fat_winter_voxel_snow_test, read-only): the zone texture depth the voxel shader draws at p, in metres
        // (the same cached FillZoneDepth grid ApplyTo uploads; 0 = no snow in the zone texture there).
        internal static float ZoneTexDepthMetres(Vector3 p) => CachedDepthMetres(ZoneSystem.GetZone(p), p);
        internal static bool SnowFieldOn => SnowFieldEnabled;

        // Metres of snow at a point on voxel ground (0 off / no snow).
        internal static float DepthMetresAt(Vector3 p)
        {
            if (!SnowFieldEnabled || s_depthAt == null) return 0f;
            try { return Mathf.Clamp01(s_depthAt(p)) * s_maxDepth; } catch { return 0f; }
        }

        [HarmonyLib.HarmonyPatch(typeof(Character), "ApplyLiquidResistance")]
        private static class DeepSnowSlowOnVoxel
        {
            private static void Postfix(Character __instance, ref float speed, GameObject ___m_deepSnowWalkObj, float ___m_deepSnowWalkObjDist, ref Vector3 ___m_lastDeepSnowWalkPos)
            {
                if (__instance == null || __instance.m_deepSnowSlowMax == 0f) return;
                long t0 = FatHooks.Start();
                bool acted = false, local = __instance == Player.m_localPlayer;
                try { acted = Slow(__instance, ref speed, ___m_deepSnowWalkObj, ___m_deepSnowWalkObjDist, ref ___m_lastDeepSnowWalkPos, local); }
                finally { FatHooks.Stop(FatHooks.Hook.DeepSnow, t0, acted, local); }
            }

            // 0.2.1160 (PERF): the local player's raw + shown depth are kept per 0.5 m cell and re-read when the player leaves the cell
            // or 0.25 s passes (a fresh trench under the feet shows within 0.25 s); every other character reads them per step as before.
            private static Vector2Int s_pCell = new Vector2Int(int.MinValue, 0);
            private static float s_pRaw, s_pShown, s_pAt;
            private static bool Slow(Character __instance, ref float speed, GameObject ___m_deepSnowWalkObj, float ___m_deepSnowWalkObjDist, ref Vector3 ___m_lastDeepSnowWalkPos, bool local)
            {
                if (!__instance.IsOnGround()) return false;
                Vector3 p = __instance.transform.position;
                float raw, depth;
                var cell = new Vector2Int(Mathf.FloorToInt(p.x * 2f), Mathf.FloorToInt(p.z * 2f));
                if (local && cell == s_pCell && Time.time - s_pAt < 0.25f) { raw = s_pRaw; depth = s_pShown; }
                else
                {
                    var zone = ZoneSystem.GetZone(p);
                    if (!VoxelZoneStreamer.IsZoneKnown(zone)) { if (local) { s_pCell = cell; s_pAt = Time.time; s_pRaw = s_pShown = -1f; } return false; }   // heightmap zones keep vanilla's own rule
                    raw = CachedDepthMetres(zone, p);   // 0.2.1058: the zone's depth grid, not a SnowField chain per tick
                    // 0.2.1149 (Fire: "walking through our trodden path should not slow us down as much"): the slow read the RAW depth,
                    // but the trench only exists in the shader. The speed now uses the SHOWN depth, the same cut the voxel shader applies
                    // to the raise: raw x (1 - trench x smoothstep(0.46, 0.54, bilinear G)) (FiresVoxelHeightmap.shader :354, :451).
                    // The walk-op spawn below still keys on the raw depth, so walking a trench keeps it trodden.
                    depth = raw > __instance.m_deepSnowSlowStartHeight ? ShownDepthMetres(raw, p) : raw;
                    if (local) { s_pCell = cell; s_pAt = Time.time; s_pRaw = raw; s_pShown = depth; }
                }
                if (raw <= __instance.m_deepSnowSlowStartHeight) return false;
                float span = Mathf.Max(0.01f, __instance.m_deepSnowSlowMaxHeight - __instance.m_deepSnowSlowStartHeight);
                float slow = depth <= __instance.m_deepSnowSlowStartHeight ? 0f : Mathf.Clamp01((depth - __instance.m_deepSnowSlowStartHeight) / span) * __instance.m_deepSnowSlowMax; slow *= VoxelSnowDepth.SlowFactorAt(__instance, p);   // 0.2.1278 (moved from the removed heightmap snow skin, 0.2.1309)
                if (__instance == Player.m_localPlayer) LogSlowState(raw, depth, slow);
                speed *= 1f - slow;
                if (___m_deepSnowWalkObj != null && Vector3.Distance(___m_lastDeepSnowWalkPos, p) > ___m_deepSnowWalkObjDist)
                {
                    UnityEngine.Object.Instantiate(___m_deepSnowWalkObj, p, __instance.transform.rotation);
                    ___m_lastDeepSnowWalkPos = p;
                }
                if (!s_slowLogged && __instance.IsPlayer())
                {
                    s_slowLogged = true;
                    FiresLogger.LogDebug($"[FiresVoxel] snow: deep-snow slow on voxel ground - {depth:F2} m of snow (max {s_maxDepth:F2} m) slows {__instance.m_name} by {slow * 100f:F0}% (vanilla start {__instance.m_deepSnowSlowStartHeight:F1} m, full at {__instance.m_deepSnowSlowMaxHeight:F1} m)");
                }
                return true;
            }
        }

        // 0.2.1149: the snow height the voxel shader DRAWS at p (the walk-trail trench applied), in metres.
        internal static float ShownDepthMetres(float raw, Vector3 p)
        {
            if (raw <= 0f) return raw;
            float trench = FiresVoxelConfig.SnowTrailTrench != null ? Mathf.Clamp01(FiresVoxelConfig.SnowTrailTrench.Value) : 1f;
            if (trench <= 0f) return raw;
            float g = VoxelWorld.PaintGAt(p.x, p.z);
            float t = Mathf.Clamp01((g - 0.46f) / 0.08f);
            float walk = t * t * (3f - 2f * t);
            ProofTrench(g, walk, p);
            return raw * (1f - trench * walk);
        }

        // 0.2.1308 ([lead]: the voxel paint mask is an sRGB Texture2D, so FiresVoxelHeightmap.shader's trench step read LINEARIZED G while
        // this CPU step reads the stored (raw) G - the drawn trench and the walkable depth disagreed): the shader now re-encodes the sampled
        // G (LinearToGammaSpaceExact) before its 0.46-0.54 step, i.e. it uses the same raw G as here. Debug at most every 10 s at the
        // player: raw G, the trench factor the shader drew BEFORE 1308 (step on sRGB-decoded G), the factor drawn now = walkable.
        private static float s_trenchProofAt;
        private static void ProofTrench(float g, float walk, Vector3 p)
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_trenchProofAt || Player.m_localPlayer == null || (Player.m_localPlayer.transform.position - p).sqrMagnitude > 1f) return;
            s_trenchProofAt = Time.unscaledTime + 10f;
            float lin = Mathf.GammaToLinearSpace(g), to = Mathf.Clamp01((lin - 0.46f) / 0.08f), oldDrawn = to * to * (3f - 2f * to);
            float e = -1f, bG = -1f, cut = -1f;
            var hmb = Heightmap.FindHeightmap(p);
            if (hmb != null) { hmb.WorldToVertexMask(p, out int bx, out int by); cut = VoxelWorld.CutG(hmb, bx, by, out e, out bG); }
            FiresLogger.LogDebug($"[Snow] trench at the player ({p.x:0.0},{p.z:0.0}): edited G {e:0.000}, base G {bG:0.000}, cut {cut:0.000}, drawn {walk:0.00} (composed G {g:0.000}; as sampled before 1308 {lin:0.000}, drawn before 1308 {oldDrawn:0.00}) (0.2.1312)");
        }

        // Last local-player slow, read by the trench_speed test; one Debug line per state change (on/off the path, 10 % speed steps).
        internal static float LastPlayerSpeedFactor = 1f;
        private static int s_slowState = -1;
        private static void LogSlowState(float raw, float shown, float slow)
        {
            LastPlayerSpeedFactor = 1f - slow;
            int state = (shown < raw - 0.05f ? 1000 : 0) + Mathf.RoundToInt((1f - slow) * 10f);
            if (state == s_slowState) return;
            s_slowState = state;
            FiresLogger.LogDebug($"[Snow] slow: depth raw {raw:0.00} m, after path {shown:0.00} m, speed {(1f - slow) * 100f:0}%");
        }

        private static void OnZoneChanged(Vector2s zone, string cause)
        {
            lock (s_dirtyLock)
            {
                if (!s_dirty.Add(zone)) s_mergedSameFrame++;   // 0.2.1322 proof: a repeat mark before this frame's Tick (the HashSet already merges it)
                NoteCause(zone, cause);
            }
        }

        // ── 0.2.1322 ([lead] 10-31, PLAN_fat_driver_stalls step 1d, PROOF ONLY): WHY a zone is pending again. Every mark (Core
        // ZoneChanged / CellsChanged) and every refill outside the marks (paint-version grid clear, first-show retry, first bind)
        // is counted by cause, per zone; Debug every 10 s: marks, merged repeats, the top zones and causes, and how many grid
        // refills came back with the SAME bytes (a re-dirty with no actual change). Counters live under s_dirtyLock (Core may
        // raise its events off the main thread). FAT has no ZoneAreaChanged subscription (the two Core events above are all).
        private const string CauseZoneChanged = "Core ZoneChanged", CauseCellsChanged = "Core CellsChanged";
        private const string CauseSourceSwitch = "depth source switched (0.2.1407)";
        private const string CausePaintVersion ="paint version (grid cache cleared)", CauseFirstShow = "first-show retry", CauseFirstBind = "first bind";
        private static readonly Dictionary<string, int> s_causeN = new Dictionary<string, int>();
        private static readonly Dictionary<Vector2s, int> s_zoneMarks = new Dictionary<Vector2s, int>();
        private static readonly Dictionary<Vector2s, string> s_zoneLastCause = new Dictionary<Vector2s, string>();
        private static readonly Dictionary<string, int> s_unchangedBy = new Dictionary<string, int>();
        private static int s_marksN, s_mergedSameFrame, s_mergedPending, s_refillsN, s_refillsSame;
        private static float s_causeLogAt;
        private static readonly List<KeyValuePair<Vector2s, int>> s_topScratch = new List<KeyValuePair<Vector2s, int>>();

        private static void NoteCause(Vector2s zone, string cause)   // caller holds s_dirtyLock
        {
            s_marksN++;
            s_causeN[cause] = s_causeN.TryGetValue(cause, out int n) ? n + 1 : 1;
            s_zoneMarks[zone] = s_zoneMarks.TryGetValue(zone, out int zn) ? zn + 1 : 1;
            s_zoneLastCause[zone] = cause;
        }

        private static void NoteCauseMain(Vector2s zone, string cause) { lock (s_dirtyLock) NoteCause(zone, cause); }

        // A grid step's refill result vs the grid it replaced: same bytes (or both null) = the re-dirty changed nothing.
        private static void NoteRefill(Vector2s zone, byte[] before, bool hadBefore, byte[] after)
        {
            bool same = hadBefore && (before == null ? after == null : after != null && SameBytes(before, after));
            lock (s_dirtyLock)
            {
                s_refillsN++;
                if (!same) return;
                s_refillsSame++;
                string c = s_zoneLastCause.TryGetValue(zone, out var lc) ? lc : "?";
                s_unchangedBy[c] = s_unchangedBy.TryGetValue(c, out int n) ? n + 1 : 1;
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void LogCauses()
        {
            if (!FiresLogger.DebugEnabled) return;
            float now = Time.unscaledTime;
            if (now < s_causeLogAt) return;
            s_causeLogAt = now + 10f;
            string line;
            lock (s_dirtyLock)
            {
                if (s_marksN == 0 && s_refillsN == 0) return;
                var sb = new System.Text.StringBuilder();
                foreach (var kv in s_causeN) sb.Append(sb.Length > 0 ? ", " : "").Append(kv.Key).Append(' ').Append(kv.Value);
                s_topScratch.Clear(); s_topScratch.AddRange(s_zoneMarks);
                s_topScratch.Sort((a, b) => b.Value.CompareTo(a.Value));
                var top = new System.Text.StringBuilder();
                for (int i = 0; i < s_topScratch.Count && i < 5; i++)
                {
                    var z = s_topScratch[i].Key;
                    top.Append(i > 0 ? ", " : "").Append('(').Append(z.x).Append(',').Append(z.y).Append(") ").Append(s_topScratch[i].Value)
                       .Append(" (last: ").Append(s_zoneLastCause.TryGetValue(z, out var lc) ? lc : "?").Append(')');
                }
                var same = new System.Text.StringBuilder();
                foreach (var kv in s_unchangedBy) same.Append(same.Length > 0 ? ", " : "").Append(kv.Key).Append(' ').Append(kv.Value);
                line = $"[VoxelSnow] re-dirty 10 s: {s_marksN} mark(s)/refill cause(s) over {s_zoneMarks.Count} zone(s); merged repeats: {s_mergedSameFrame} same frame, {s_mergedPending} while already pending; causes: {(sb.Length > 0 ? sb.ToString() : "-")}; top zones: {(top.Length > 0 ? top.ToString() : "-")}; grid refills {s_refillsN}, {s_refillsSame} with NO change{(same.Length > 0 ? " (by last cause: " + same + ")" : "")} (0.2.1322)";
                s_causeN.Clear(); s_zoneMarks.Clear(); s_unchangedBy.Clear();
                s_marksN = s_mergedSameFrame = s_mergedPending = s_refillsN = s_refillsSame = 0;
            }
            FiresLogger.LogDebug(line);
        }

        // ── 0.2.1058 ([seasons]' snow diag row a): the deep-snow slow reads a per-zone depth grid (the same 32x32 R8 depth the zone's
        // texture holds, nearest texel), filled once per zone instead of a full DepthAt chain (GetBiome + providers + melt + paths)
        // per character per movement tick. A zone's grid refills with its texture on ZoneChanged; every grid drops when FAT's
        // paint changes (FatApi.PaintVersion) and when the streamer forgets the zone. null = no snow in that zone.
        private static readonly Dictionary<Vector2s, byte[]> s_depthGrid = new Dictionary<Vector2s, byte[]>();
        private static int s_gridPaintVersion = -1;
        private static float s_nextGridLog;

        private static float CachedDepthMetres(Vector2s zone, Vector3 p)
        {
            if (!SnowFieldEnabled || s_fill == null) return 0f;
            int pv = VerdantsAscent.Terrain.Zones.BiomePaintVersion.Current;
            if (pv != s_gridPaintVersion) { s_gridPaintVersion = pv; s_depthGrid.Clear(); lock (s_dirtyLock) { s_causeN[CausePaintVersion] = s_causeN.TryGetValue(CausePaintVersion, out int pn) ? pn + 1 : 1; s_marksN++; } JobAllGridsChanged(); }   // 0.2.1322: cause proof + a running resumable step restarts
            // 0.2.1318 (Fire 10-31: "were still putting snow under the ice"): this filled s_depthGrid straight from Core WITHOUT
            // MaskUnderwater, and Build's SharedTexel then uploaded that cached grid = snow on the bed until the zone's next ZoneChanged
            // (every paint-version clear above re-opened it). One path: the masked Grid.
            var grid = Grid(zone, false);
            if (FatDiag.Snow && Time.unscaledTime >= s_nextGridLog)   // 0.2.1104: under the Snow diagnostics toggle (default off)
            {
                s_nextGridLog = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[FiresVoxel] snow depth cache: {s_depthGrid.Count} zones");
            }
            if (grid == null) return 0f;
            Vector3 c = ZoneSystem.GetZonePos(zone);
            float step = ZoneSize / (Size - 1);
            int x = Mathf.Clamp(Mathf.RoundToInt((p.x - (c.x - ZoneSize * 0.5f)) / step), 0, Size - 1);
            int z = Mathf.Clamp(Mathf.RoundToInt((p.z - (c.z - ZoneSize * 0.5f)) / step), 0, Size - 1);
            return SoftTexel(zone, x, z) / 255f * s_maxDepth;   // 0.2.1154: the same softened value the shader draws
        }

        // VoxelChunkView.PushBlock: the chunk's zone texture, or off. True = the chunk is bound to its zone's snow.
        // 0.2.1089 ([SnowSeam], Fire shot 520: dark cracks in the raised snow): a voxel chunk's border vertices reach up to one
        // voxel into the NEXT zone (the seam quads), and FiresVoxelHeightmap.shader FiresZoneSnow01 (~line 346) returns 0 for a
        // uv outside 0..1, so the same border vertex was raised by its own zone in one chunk and not at all in the other: a crack
        // up to the full snow depth along every 64 m zone border. Each zone texture is now (Size + 2)^2 with a one-texel margin
        // taken from the neighbour zones' own depth grids. Core samples every zone on the same world grid (corner to corner,
        // 64 / 31 m), so both sides bilinear the SAME world texels at a shared vertex and raise it identically. C# only.
        // 0.2.1159 (Fire: "you fix, I check"; Debug only): [WinterSnow] proof lines - Core 935 winter depth drawn on a non-snow biome, or missing.
        private static Type s_fieldType;
        private static bool s_winterSaid, s_winterMissingSaid;
        private static float s_winterCheckAt = -1f;
        private static bool NonSnowBiome(Heightmap.Biome b) => b != Heightmap.Biome.DeepNorth && b != Heightmap.Biome.Mountain && b != Heightmap.Biome.None;
        private static string CoreVer()
        {
            try
            {
                if (s_fieldType == null) return "?";
                foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                    if (info?.Instance != null && info.Instance.GetType().Assembly == s_fieldType.Assembly) return info.Metadata.Version.ToString();
            }
            catch { }
            return "?";
        }
        private static void WinterProbe(Vector2s zone)
        {
            if (s_winterSaid || !FiresLogger.DebugEnabled || WorldGenerator.instance == null) return;
            Vector3 c = ZoneSystem.GetZonePos(zone);
            for (int i = 0; i < 5; i++)
            {
                Vector3 p = c + (i == 0 ? Vector3.zero : new Vector3((i & 1) == 1 ? 16f : -16f, 0f, i < 3 ? 16f : -16f));
                var b = WorldGenerator.instance.GetBiome(p.x, p.z);
                if (!NonSnowBiome(b)) continue;
                float d = CachedDepthMetres(zone, p);
                if (d <= 0.05f) continue;
                s_winterSaid = true;
                FiresLogger.LogDebug($"[WinterSnow] snow depth {d:0.00} m drawn on {b} (a non-snow biome) at ({p.x:0},{p.z:0}), Core {CoreVer()} - the winter depth reaches FAT voxel ground (Core 0.2.935 winter term; once per session)");
                return;
            }
        }
        private static void WinterMissingCheck()
        {
            if (s_winterSaid || s_winterMissingSaid || !FiresLogger.DebugEnabled) return;
            float now = Time.unscaledTime;
            if (s_winterCheckAt < 0f) { s_winterCheckAt = now + 60f; return; }
            if (now < s_winterCheckAt) return;
            s_winterCheckAt = now + 30f;
            if (s_fieldType == null || Player.m_localPlayer == null) return;
            string ver = CoreVer();
            if (!System.Version.TryParse(ver, out var v) || v < new System.Version(0, 2, 935)) return;
            float winter = 0f;
            try
            {
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var wp = s_fieldType.GetProperty("WinterCover", F);
                object o = wp != null ? wp.GetValue(null, null) : s_fieldType.GetField("WinterCover", F)?.GetValue(null);
                if (o is float f) winter = f;
            }
            catch { }
            if (winter <= 0.3f) return;
            Vector3 pp = Player.m_localPlayer.transform.position;
            var b = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(pp.x, pp.z) : Heightmap.Biome.None;
            s_winterMissingSaid = true;
            FiresLogger.LogDebug($"[WinterSnow] winter depth MISSING: Core {ver}, WinterCover {winter:0.00} > 0.30, but no non-snow-biome zone drew depth > 0.05 m (player on {b} at ({pp.x:0},{pp.z:0}), depth there {CachedDepthMetres(ZoneSystem.GetZone(pp), pp):0.00} m) - Core 935 winter term not reaching FAT (once per session)");
        }

        private const int Pad = Size + 2;
        private static bool s_seamSaid;
        private static readonly byte[] s_padBuf = new byte[Pad * Pad];
        private static int s_bindLogs;
        private static float s_bindLogAt;

        internal static bool ApplyTo(MaterialPropertyBlock mpb, Vector3 chunkPos)
        {
            if (!SnowFieldEnabled) { mpb.SetFloat(ZoneOnId, 0f); return false; }
            Tick();
            var zone = ZoneSystem.GetZone(chunkPos + new Vector3(16f, 0f, 16f));   // a chunk's centre picks its zone (32 m chunks, 64 m zones)
            if (!s_tex.TryGetValue(zone, out var tex) || tex == null)
            {
                tex = new Texture2D(Pad, Pad, TextureFormat.R8, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "FiresSnowZone" };
                s_tex[zone] = tex;
                s_texBornAt[zone] = Time.realtimeSinceStartup;   // 0.2.1310 [SnowShow]: the zone's first bind = its load
                NoteCauseMain(zone, CauseFirstBind);   // 0.2.1322 proof
                Grid(zone, true);
                Build(zone, tex);
            }
            // Texel i (0..Pad-1) holds the world point zoneMin + (i - 1) * step (step = 64 / (Size - 1)); its centre is uv (i + 0.5) / Pad,
            // so uv = (w - (zoneMin - 1.5 step)) / (step * Pad): the shader reads uv = (w - xy) * z.
            Vector3 c = ZoneSystem.GetZonePos(zone);
            float step = ZoneSize / (Size - 1);
            float k = 1f / (step * Pad);
            float minX = c.x - ZoneSize * 0.5f - 1.5f * step, minZ = c.z - ZoneSize * 0.5f - 1.5f * step;
            mpb.SetTexture(ZoneTexId, tex);
            mpb.SetVector(ZoneRectId, new Vector4(minX, minZ, k, 0f));
            mpb.SetFloat(ZoneOnId, 1f);
            float trench = FiresVoxelConfig.SnowTrailTrench != null ? Mathf.Clamp01(FiresVoxelConfig.SnowTrailTrench.Value) : 1f;
            mpb.SetFloat(TrenchId, trench);
            if (trench != s_trenchSaid) { s_trenchSaid = trench; FiresLogger.LogDebug($"[SnowTrail] trench {trench:0.00} bound (snow raise sinks by it along vanilla's deep-snow walk paint, G channel; shader 0.2.1094+, older shader ignores it)"); }
            if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_bindLogAt)
            {
                s_bindLogAt = Time.unscaledTime + 10f;
                var g = s_depthGrid.TryGetValue(zone, out var gg) ? gg : null;
                int max = 0, n = 0;
                if (g != null) foreach (var b in g) { if (b > max) max = b; if (b != 0) n++; }
                if (FatDiag.Snow) FiresLogger.LogDebug($"[VoxelSnow] snow MPB bound: chunk at ({chunkPos.x:0},{chunkPos.z:0}) zone ({zone.x},{zone.y}) tex {tex.GetInstanceID()} {Pad}x{Pad} (1-texel neighbour margin), {n}/{Size * Size} texel(s) with snow, max raise {max / 255f * s_maxDepth:0.00} m before cover {s_lastCover:0.00} x scale {s_lastScale:0.00}");
            }
            return true;
        }

        // The zone's own 32 x 32 depth grid (null = no snow), filled from Core once and kept until the zone changes.
        private static byte[] Grid(Vector2s zone, bool fresh)
        {
            if (!fresh && s_depthGrid.TryGetValue(zone, out var g)) return g;
            s_gridT[0] = StallPhaseTimer.Now();   // 0.2.1322 proof stamps (GridStep notes them)
            bool snowy = FillInto(zone, s_buf);
            s_gridT[1] = StallPhaseTimer.Now();
            g = snowy ? (byte[])s_buf.Clone() : null;   // false = buffer untouched: no snow in this zone
            if (g != null) MaskUnderwater(zone, g); else s_wetTexels.Remove(zone);   // 0.2.1318: no snow = no stale wet mask   // 0.2.1315: no voxel snow on a water column's bed
            s_gridT[2] = StallPhaseTimer.Now();
            GridCommit(zone, g, snowy);
            s_gridT[3] = StallPhaseTimer.Now();
            return g;
        }

        // 0.2.1322: Grid's pieces, shared by the resumable step (one implementation, same order).
        private static readonly long[] s_gridT = new long[4];
        private static bool FillInto(Vector2s zone, byte[] buf)
        {
            bool snowy = false;
            try { snowy = s_fill != null && s_fill(zone, buf, Size); }
            catch (Exception ex) { FiresLogger.LogDebug($"[VoxelSnow] bail: FillZoneDepth {zone} threw: {ex.Message}"); }
            return snowy;
        }
        private static void GridCommit(Vector2s zone, byte[] g, bool snowy)
        {
            s_depthGrid[zone] = g;   // 0.2.1058: the slow's grid follows the texture
            s_fills++;
            if (snowy) s_snowyFills++;
            if (snowy && s_texBornAt.TryGetValue(zone, out float born) && s_shown.Add(zone)) NoteShown(zone, born);
            JobGridChanged(zone);   // 0.2.1322: a resumable step that reads this grid restarts (not when that step is the one filling it)
        }

        // 0.2.1315 (Fire 10-31: "snow should not draw underwater" + "the rivers start and end ponds need to freeze smooth not all bumpy",
        // shot x257 z840): Core's depth grid knows nothing of FAT water, so the voxel snow raise (up to MaxDepthMetres) was drawn on the
        // BED of every pond and river; where the raise is taller than the water it pokes up through the flat ice / water sheet as lumps.
        // Every texel whose column holds water (open or frozen, more than 0.05 m) (IceFrost.ColumnUnderwater: generated depth or placed surface - bed)
        // gets no snow. Debug per zone: texels cleared and how many would have stood ABOVE the water surface (the lumps).
        private static readonly Dictionary<Vector2s, float> s_maskLogAt = new Dictionary<Vector2s, float>();
        private static bool s_maskSaid;
        // 0.2.1318: the texels MaskUnderwater zeroed, per zone (texel index of that zone's own grid).
        private static readonly Dictionary<Vector2s, bool[]> s_wetTexels = new Dictionary<Vector2s, bool[]>();
        // 0.2.1322: MaskUnderwater in row pieces (MaskRows + MaskEnd) so the resumable snow step can carry it over frames; the
        // one-call path runs every row then the end, exactly as before.
        private sealed class MaskAcc { internal int Cleared, Poked; internal float Worst; internal Vector2 At; internal bool[] Wet; }
        private static void MaskUnderwater(Vector2s zone, byte[] g)
        {
            var acc = new MaskAcc();
            MaskRows(zone, g, 0, Size, acc);
            MaskEnd(zone, acc);
        }
        private static void MaskRows(Vector2s zone, byte[] g, int j0, int j1, MaskAcc acc)
        {
            Vector3 c = ZoneSystem.GetZonePos(zone);
            float step = ZoneSize / (Size - 1);
            float x0 = c.x - ZoneSize * 0.5f, z0 = c.z - ZoneSize * 0.5f;
            for (int j = j0; j < j1; j++)
                for (int i = 0; i < Size; i++)
                {
                    int k = i + Size * j;
                    if (g[k] == 0) continue;
                    float x = x0 + i * step, z = z0 + j * step;
                    if (!global::VerdantsAscent.Water.Core.IceFrost.ColumnUnderwater(x, z, float.NaN, out _, out float depth, 0.05f)) continue;   // open or frozen: the surface / ice sheet stands above the bed
                    float raise = g[k] / 255f * s_maxDepth;
                    if (raise > depth) { acc.Poked++; if (raise - depth > acc.Worst) { acc.Worst = raise - depth; acc.At = new Vector2(x, z); } }
                    g[k] = 0; acc.Cleared++;
                    if (acc.Wet == null) acc.Wet = new bool[Size * Size];
                    acc.Wet[k] = true;
                }
        }
        private static void MaskEnd(Vector2s zone, MaskAcc acc)
        {
            int cleared = acc.Cleared, poked = acc.Poked; float worst = acc.Worst; Vector2 at = acc.At; bool[] wet = acc.Wet;
            if (wet != null) s_wetTexels[zone] = wet; else s_wetTexels.Remove(zone);   // 0.2.1318: SoftTexel keeps these hard zeros
            if (cleared == 0) return;
            if (!s_maskSaid) { s_maskSaid = true; FiresLogger.LogInfo($"[VoxelSnow] no snow under water: zone ({zone.x},{zone.y}) {cleared} texel(s) over water columns cleared, {poked} of them would have stood above the water surface (0.2.1315)"); }
            if (FiresLogger.DebugEnabled && (!s_maskLogAt.TryGetValue(zone, out float mAt) || Time.unscaledTime >= mAt))
            {
                s_maskLogAt[zone] = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[VoxelSnow] underwater snow cleared: zone ({zone.x},{zone.y}) {cleared} texel(s) (why: water or ice above the bed there, open or frozen); {poked} would have poked ABOVE the water (snow raise > water depth; worst {worst:0.00} m at ({at.x:0},{at.y:0}) = a lump through the ice) (0.2.1315)");
            }
        }

        // 0.2.1310 (Fire: "the snow showed up after a while, we were loaded in for quite a few seconds, and then it popped in"):
        // a zone whose first fill found no snow (Core's field / build-up not answered yet at load) kept a null grid until Core's next
        // ZoneChanged, and chunks bound while the snow was not ready waited for the 5 s rebind check. FIRST-SHOW: a newly bound zone
        // with no snow is re-asked every second for its first FirstShowSeconds, outside any budget, and the unbound-chunk rebind runs
        // every 0.5 s; refinements keep their budgets. Debug once per zone + a 30 s summary.
        private const float FirstShowSeconds = 20f;
        private static readonly Dictionary<Vector2s, float> s_texBornAt = new Dictionary<Vector2s, float>();
        private static readonly HashSet<Vector2s> s_shown = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_retryScratch = new List<Vector2s>();
        private static float s_firstShowRetryAt, s_showSumAt;
        private static int s_showN, s_showRetried; private static double s_showMsSum, s_showMsMax;
        private static int s_showLogged;
        private static string s_showPath = "first bind";
        private static void NoteShown(Vector2s zone, float born)
        {
            double ms = (Time.realtimeSinceStartup - born) * 1000.0;
            s_showN++; s_showMsSum += ms; if (ms > s_showMsMax) s_showMsMax = ms;
            if (s_showLogged < 40) { s_showLogged++; FiresLogger.LogDebug($"[SnowShow] zone ({zone.x},{zone.y}) voxel snow shown {ms:0} ms after zone load (path {s_showPath}) (0.2.1310)"); }
        }
        // 0.2.1346 (r22: 194 of 214 marks in one 10 s window; 0 grid refills): Core's FillZoneDepth says false for a zone with no snow, and that zone was re-asked
        // every second for 20 s, for 26 zones. The retry exists for a field not answering yet at load, so the asks now back off (1, 2, 4, 8 s), and a cover change asks at once.
        private static readonly Dictionary<Vector2s, KeyValuePair<float, float>> s_retryAt = new Dictionary<Vector2s, KeyValuePair<float, float>>();   // zone -> (next ask, gap)
        private static float s_retryCover = -2f;
        private static int s_retryHeldBack; private static float s_retryLogAt;
        private static bool s_retryProof;
        private static readonly FatSpreadStat s_retryStat = new FatSpreadStat("snow first-show retries");
        private static bool RetryDue(Vector2s zone, float now)
        {
            if (FiresVoxelConfig.SnowRetryBackoff == null || !FiresVoxelConfig.SnowRetryBackoff.Value) return true;
            if (!s_retryProof) { s_retryProof = true; FiresLogger.LogInfo("[VoxelSnow] snow first-show retries back off (1, 2, 4, 8 s; a cover change asks at once) (0.2.1346)"); }
            if (!s_retryAt.TryGetValue(zone, out var e)) { s_retryAt[zone] = new KeyValuePair<float, float>(now + 1f, 1f); return true; }
            if (now < e.Key) { s_retryHeldBack++; return false; }
            float gap = Mathf.Min(e.Value * 2f, 8f);
            s_retryAt[zone] = new KeyValuePair<float, float>(now + gap, gap);
            return true;
        }
        // 0.2.1346: the swap waits for this zone's queued snow work (VoxelZoneStreamer.RevealPrepReady).
        internal static bool ZoneWorkPending(Vector2s zone) => s_gridPending.Contains(zone) || s_buildPending.Contains(zone) || (s_job != null && s_job.Zone == zone);

        private static void FirstShowTick()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= s_firstShowRetryAt)
            {
                s_firstShowRetryAt = now + 1f;
                s_retryScratch.Clear();
                if (!Mathf.Approximately(s_retryCover, s_lastCover)) { s_retryCover = s_lastCover; s_retryAt.Clear(); }   // 0.2.1346
                if (FiresLogger.DebugEnabled && now >= s_retryLogAt)
                {
                    s_retryLogAt = now + 10f;
                    if (s_retryHeldBack > 0) FiresLogger.LogDebug($"[SnowShow] first-show retries: {s_retryHeldBack} ask(s) backed off in 10 s (0.2.1346)");
                    s_retryHeldBack = 0;
                }
                foreach (var kv in s_texBornAt)
                    if (!s_shown.Contains(kv.Key) && now - kv.Value <= FirstShowSeconds && (!s_depthGrid.TryGetValue(kv.Key, out var g) || g == null) && RetryDue(kv.Key, now)) s_retryScratch.Add(kv.Key);
                if (s_retryScratch.Count > 0)
                {
                    long retry0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1346
                    s_showPath = "first-show retry";
                    foreach (var z in s_retryScratch)
                    {
                        s_showRetried++;
                        NoteCauseMain(z, CauseFirstShow);   // 0.2.1322 proof
                        if (Grid(z, true) != null && s_tex.TryGetValue(z, out var t) && t != null)
                            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) { var n = new Vector2s(z.x + dx, z.y + dz); if (s_tex.TryGetValue(n, out var tn) && tn != null) Build(n, tn); }
                    }
                    s_showPath = "first bind";
                    s_retryStat.Note((System.Diagnostics.Stopwatch.GetTimestamp() - retry0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);   // 0.2.1346
                }
            }
            if (FiresLogger.DebugEnabled && now >= s_showSumAt)
            {
                s_showSumAt = now + 30f;
                if (s_showN > 0 || s_showRetried > 0) FiresLogger.LogDebug($"[SnowShow] 30 s: {s_showN} zone(s) shown, avg {(s_showN > 0 ? s_showMsSum / s_showN : 0):0} ms / max {s_showMsMax:0} ms after load; {s_showRetried} first-show retr(ies) (0.2.1310)");
                s_showN = 0; s_showRetried = 0; s_showMsSum = 0; s_showMsMax = 0;
            }
        }

        // The padded texture: the zone's grid in the middle, the neighbours' next-to-border texels round it.
        private static readonly Dictionary<Vector2s, float> s_rezeroLogAt = new Dictionary<Vector2s, float>();   // 0.2.1318
        private static void Build(Vector2s zone, Texture2D tex)
        {
            if (!s_seamSaid) { s_seamSaid = true; FiresLogger.LogDebug($"[SnowSeam] padded zone snow textures executing: {Pad}x{Pad}, one-texel margin from the neighbour zones (seam-free raise at zone borders)"); }
            if (tex.width != Pad) { tex.Reinitialize(Pad, Pad); }
            BuildSaid();
            s_buildT[0] = StallPhaseTimer.Now();   // 0.2.1322 proof stamps (BuildStep notes them)
            SoftRows(zone, s_padBuf, 0, Pad);
            s_buildT[1] = StallPhaseTimer.Now();
            // 0.2.1318 (Fire 10-31: "were still putting snow under the ice"): after softening, every texel whose column holds water
            // (> 0.05 m, open or frozen; IceFrost.ColumnUnderwater, the MaskUnderwater test) is zeroed again - the tent brings shore snow
            // into water texels MaskUnderwater never saw (Core gave them 0, or a neighbour zone's margin). Debug per zone (30 s).
            RezeroLog(zone, RezeroRows(zone, s_padBuf, 0, Pad));
            s_buildT[2] = StallPhaseTimer.Now();
            tex.LoadRawTextureData(s_padBuf);
            tex.Apply(false, false);
            s_buildT[3] = StallPhaseTimer.Now();
            WinterProbe(zone);
            s_buildT[4] = StallPhaseTimer.Now();
        }

        // 0.2.1322: Build's pieces (one implementation; the resumable step runs the same rows into its own buffer).
        private static readonly long[] s_buildT = new long[5];
        private static void BuildSaid()
        {
            if (!s_softSaid) { s_softSaid = true; FiresLogger.LogDebug($"[SnowEdge] zone snow masks softened before upload: 3x3 tent over the world texel grid (2 m texels), so the stepped cover edge becomes a ramp instead of 1-2 m sawtooth teeth; the slow and tests read the same softened value (0.2.1154, round 35)"); }
        }
        private static void SoftRows(Vector2s zone, byte[] buf, int pz0, int pz1)
        {
            for (int pz = pz0; pz < pz1; pz++)
                for (int px = 0; px < Pad; px++)
                    buf[pz * Pad + px] = SoftTexel(zone, px - 1, pz - 1);   // -1 / Size = the neighbour margin
        }
        private static int RezeroRows(Vector2s zone, byte[] buf, int pz0, int pz1)
        {
            Vector3 zc = ZoneSystem.GetZonePos(zone);
            float st = ZoneSize / (Size - 1), bx = zc.x - ZoneSize * 0.5f, bz = zc.z - ZoneSize * 0.5f;
            int rez = 0;
            for (int pz = pz0; pz < pz1; pz++)
                for (int px = 0; px < Pad; px++)
                {
                    int pi = pz * Pad + px;
                    if (buf[pi] == 0) continue;
                    if (!global::VerdantsAscent.Water.Core.IceFrost.ColumnUnderwater(bx + (px - 1) * st, bz + (pz - 1) * st, float.NaN, out _, out _, 0.05f)) continue;
                    buf[pi] = 0; rez++;
                }
            return rez;
        }
        private static void RezeroLog(Vector2s zone, int rez)
        {
            if (rez > 0 && FiresLogger.DebugEnabled && (!s_rezeroLogAt.TryGetValue(zone, out float rAt) || Time.unscaledTime >= rAt))
            {
                s_rezeroLogAt[zone] = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[VoxelSnow] underwater texels re-zeroed after softening: zone ({zone.x},{zone.y}) {rez} texel(s) (why: the 3x3 tent pulled shore snow onto a water column; uploaded as 0) (0.2.1318)");
            }
        }

        // 0.2.1097 ([SnowSeam], Fire shot 555: straight steps and thin dark walls in the raised snow along zone borders): Core fills
        // each zone corner to corner, so a zone's EDGE texel and the neighbour's first texel are the SAME world point, but Core's
        // per-zone build-up gives them different values; each chunk drew its own zone's value there, so the shared edge had two
        // heights. A texel on a shared edge or corner now takes the AVERAGE of every zone that holds that world point (2 on an
        // edge, 4 on a corner; a zone with no snow counts as 0). Every texture computes it the same way, so both sides of a border
        // bilinear identical values. Interior texels are untouched.
        // 0.2.1154 ([SnowEdge], round 35 RDC frame31996, Fire: "hard angles"): Core's cover arrives as a 2 m staircase (242 -> 210
        // -> 95 -> 0 in axis blocks) and the raise followed it on the 1 m mesh as sawtooth teeth. Every texel is now a 1-2-1 tent of
        // its world neighbours. The texel index is folded into the zone that owns it first (zones share their edge texel, step
        // Size - 1), so the value is a function of the world point alone: both sides of a zone border still upload identical values.
        private static bool s_softSaid;
        private static int RawAt(Vector2s zone, int gx, int gz)
        {
            while (gx < 0) { zone = new Vector2s(zone.x - 1, zone.y); gx += Size - 1; }
            while (gx > Size - 1) { zone = new Vector2s(zone.x + 1, zone.y); gx -= Size - 1; }
            while (gz < 0) { zone = new Vector2s(zone.x, zone.y - 1); gz += Size - 1; }
            while (gz > Size - 1) { zone = new Vector2s(zone.x, zone.y + 1); gz -= Size - 1; }
            return SharedTexel(zone, gx, gz);
        }
        // 0.2.1318 (Fire 10-31: "were still putting snow under the ice"): an underwater texel (MaskUnderwater zeroed it) stays a HARD
        // zero - the tent took up to 12/16 of the dry shore texels' snow back into it, and the bilinear spread that under the ice.
        private static bool WetTexel(Vector2s zone, int gx, int gz)
        {
            while (gx < 0) { zone = new Vector2s(zone.x - 1, zone.y); gx += Size - 1; }
            while (gx > Size - 1) { zone = new Vector2s(zone.x + 1, zone.y); gx -= Size - 1; }
            while (gz < 0) { zone = new Vector2s(zone.x, zone.y - 1); gz += Size - 1; }
            while (gz > Size - 1) { zone = new Vector2s(zone.x, zone.y + 1); gz -= Size - 1; }
            return s_wetTexels.TryGetValue(zone, out var w) && w[gx + Size * gz];
        }
        private static byte SoftTexel(Vector2s zone, int gx, int gz)
        {
            if (WetTexel(zone, gx, gz)) return 0;
            int sum = 0;
            for (int oz = -1; oz <= 1; oz++)
                for (int ox = -1; ox <= 1; ox++)
                    sum += RawAt(zone, gx + ox, gz + oz) * ((ox == 0 ? 2 : 1) * (oz == 0 ? 2 : 1));
            return (byte)((sum + 8) / 16);
        }

        private static byte SharedTexel(Vector2s zone, int gx, int gz)
        {
            int sx0 = 0, sx1 = 0, sz0 = 0, sz1 = 0;   // which neighbour offsets also hold this point
            if (gx == 0) sx0 = -1; else if (gx == Size - 1) sx1 = 1;
            if (gz == 0) sz0 = -1; else if (gz == Size - 1) sz1 = 1;
            int sum = 0, count = 0;
            for (int ox = sx0; ox <= sx1; ox++)
                for (int oz = sz0; oz <= sz1; oz++)
                {
                    var z = (ox == 0 && oz == 0) ? zone : new Vector2s(zone.x + ox, zone.y + oz);
                    var g = Grid(z, false);
                    int ix = ox < 0 ? Size - 1 : ox > 0 ? 0 : gx;
                    int iz = oz < 0 ? Size - 1 : oz > 0 ? 0 : gz;
                    sum += g == null ? 0 : g[iz * Size + ix];
                    count++;
                }
            return (byte)((sum + count / 2) / count);
        }

        // [SnowSeam] why-line: the largest raise difference between the two sides of any zone edge near the player, read from the
        // built textures (both sides' copy of each shared texel). Target 0.00 m.
        // [SnowTrail] why-line (0.2.1097): at the player, the trail G the shader reads, the zone raise and the raise after the trench
        // (same curve as the shader: raise x (1 - trench x smoothstep(0.1, 0.8, G))). Every 10 s while the player moves in snow.
        private static float s_trailLogAt;
        private static Vector3 s_trailLastPos;
        private static void LogTrailAtPlayer()
        {
            var pl = Player.m_localPlayer;
            if (!FiresLogger.DebugEnabled || pl == null || Time.unscaledTime < s_trailLogAt) return;
            Vector3 p = pl.transform.position;
            if ((p - s_trailLastPos).sqrMagnitude < 1f) return;
            float raise = CachedDepthMetres(ZoneSystem.GetZone(p), p);
            if (raise <= 0.01f) return;
            s_trailLogAt = Time.unscaledTime + 10f;
            s_trailLastPos = p;
            float trench = FiresVoxelConfig.SnowTrailTrench != null ? Mathf.Clamp01(FiresVoxelConfig.SnowTrailTrench.Value) : 1f;
            bool have = VoxelWorld.TryTrailG(p, out float g);
            float t = Mathf.Clamp01((g - 0.1f) / 0.7f); t = t * t * (3f - 2f * t);
            if (FatDiag.Snow) FiresLogger.LogDebug($"[SnowTrail] trench at player ({p.x:0.0},{p.z:0.0}): G={g:0.00}{(have ? "" : " (no paint mask for this column)")} raise {raise:0.00} m -> {raise * (1f - trench * t):0.00} m (trench {trench:0.00})");
        }

        private static float s_seamLogAt;
        private static void LogSeamDelta()
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_seamLogAt || Player.m_localPlayer == null) return;
            s_seamLogAt = Time.unscaledTime + 10f;
            var pz = ZoneSystem.GetZone(Player.m_localPlayer.transform.position);
            float worst = 0f; int edges = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var a = new Vector2s(pz.x + dx, pz.y + dz);
                    if (!s_tex.TryGetValue(a, out var ta) || ta == null) continue;
                    var b = new Vector2s(a.x + 1, a.y);   // the east edge of a = the west edge of b
                    if (!s_tex.TryGetValue(b, out var tb) || tb == null) continue;
                    var ra = ta.GetRawTextureData<byte>(); var rb = tb.GetRawTextureData<byte>();
                    for (int g = 0; g < Size; g++)
                    {
                        int pa = (g + 1) * Pad + Size, pb = (g + 1) * Pad + 1;   // own-grid texel Size-1 in a, 0 in b
                        worst = Mathf.Max(worst, Mathf.Abs(ra[pa] - rb[pb]) / 255f * s_maxDepth);
                    }
                    edges++;
                }
            if (FatDiag.Snow) FiresLogger.LogDebug($"[SnowSeam] max height delta across zone edges near the player: {worst:0.00} m (target 0.00), {edges} edge(s) checked");
        }

        // Globals + the zones SnowField changed. Called from PushBlock and VoxelZoneStreamer's update.
        internal static void Tick()
        {
            FatSnowLookDefaults.Tick();   // 0.2.1310: the snow look globals (black voxel snow when FTS doesn't push them)
            if (!s_present) return;
            SourceTick();   // 0.2.1407: Vedr's ground snow on / off -> the source in use
            WinterMissingCheck();
            bool on = SnowFieldEnabled;
            if (on != s_lastEnabled)
            {
                s_lastEnabled = on;
                FiresLogger.LogDebug($"[VoxelSnow] Dynamic snow {(on ? "ON - voxel chunks bind their zone depth" : "OFF - chunks unbound")}");
                if (!on) Shader.SetGlobalFloat(CoverId, 0f);
            }
            if (!on) return;
            float cover = 0f, scale = 1f;
            try { cover = (float)s_cover.GetValue(null, null); scale = s_scale != null ? (float)s_scale.GetValue(null, null) : 1f; } catch { }
            if (!Mathf.Approximately(cover, s_lastCover)) { s_lastCover = cover; Shader.SetGlobalFloat(CoverId, cover); }
            if (!Mathf.Approximately(scale, s_lastScale)) { s_lastScale = scale; Shader.SetGlobalFloat(DepthScaleId, scale); }
            Shader.SetGlobalFloat(MaxDepthId, s_maxDepth);   // 0.2.1043: metres at depth 1 (cheap; one float a frame)

            Vector2s[] dirty = null;
            lock (s_dirtyLock)
            {
                if (s_dirty.Count > 0) { dirty = new Vector2s[s_dirty.Count]; s_dirty.CopyTo(dirty); s_dirty.Clear(); }
            }
            if (dirty != null)
                foreach (var z in dirty)
                {
                    if (!s_gridPending.Add(z)) { lock (s_dirtyLock) s_mergedPending++; }   // 0.2.1322 proof: already pending = merged (HashSet)
                    JobMarked(z);   // 0.2.1322: a resumable step in progress for / reading this zone restarts once
                }
            DrainDepth();
            FirstShowTick();
            LogCauses();   // 0.2.1322 proof

            // 0.2.1089 ([VoxelSnow], Fire shot 521: flat white snow, no raise; his log said "1 zone texture(s), 1 fill(s)" all
            // session): a chunk pushed while Dynamic snow was still off, or whose PushBlock failed before the snow step, kept
            // _FiresSnowZoneOn 0 for good (PushBlock only reruns on a paint or waterline change). Every 5 s any unbound chunk rebinds.
            if (Time.unscaledTime >= s_rebindAt)
            {
                s_rebindAt = Time.unscaledTime + 0.5f;   // 0.2.1310: was 5 s (a chunk bound before the snow was ready waited up to 5 s)
                int fixedN = VoxelWorld.RebindSnow(out int total);
                if (fixedN > 0 || (FiresLogger.DebugEnabled && Time.unscaledTime >= s_rebindLogAt))
                {
                    s_rebindLogAt = Time.unscaledTime + 30f;
                    if (FatDiag.Snow) FiresLogger.LogDebug($"[VoxelSnow] rebind check: {fixedN} of {total} chunk(s) had no snow binding and were re-pushed; {s_tex.Count} zone texture(s)");
                }
            }

            LogSeamDelta();
            LogTrailAtPlayer();
            if (Time.unscaledTime >= s_nextLog)
            {
                s_nextLog = Time.unscaledTime + 30f;
                if (FatDiag.Snow) FiresLogger.LogDebug($"[VoxelSnow] cover {cover:F2}, depth scale {scale:F2}, {s_tex.Count} zone texture(s), {s_fills} fill(s) ({s_snowyFills} with snow)");
            }
        }

        // 0.2.1196 ([ghost] fly53: Core's SnowTicker announced every loaded snow zone at once, 150 s and 6 s cadences, and this
        // drain did up to 25 FillZoneDepth + their ring of texture builds in ONE frame = 150-200 ms). Fresh grids for the changed
        // zones first, then rebuild every texture that reads them (the zone and the neighbours whose margin holds its border
        // texels; no cascade beyond one ring). With "Snow depth rebuild budget (ms)" > 0 the work stops at the budget (always at
        // least one step) and resumes next frame; 0 = the whole set this frame, as before.
        private static readonly HashSet<Vector2s> s_gridPending = new HashSet<Vector2s>();
        private static readonly HashSet<Vector2s> s_buildPending = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_drainScratch = new List<Vector2s>();
        private static readonly System.Diagnostics.Stopwatch s_drainSw = new System.Diagnostics.Stopwatch();
        private static int s_drainSpread, s_drainFrames, s_drainSteps;
        private static double s_drainWorstMs;
        private static float s_drainLogAt, s_waveLogAt;

        // 0.2.1317 ([SnowFill], Fire: "fill from closest out, not random"): with a local player every step takes the pending
        // work nearest the player's zone (Chebyshev, ties by Euclidean): a fresh grid, or a texture whose own 3x3 ring of grids
        // is fresh (none of the 9 pending; ties go to the texture). The old "textures only once every pending grid is fresh"
        // rule held for the whole set; now it holds per ring, so a texture still never builds from a half-updated neighbour
        // set. Build/ApplyTo/upload unchanged. No player = the old pick order and the old all-grids-first rule.
        private static bool s_waveOn, s_waveFirstSaid, s_fillInfoSaid;
        private static float s_waveStartAt;
        private static int s_waveBuilds;
        private static double s_waveWorstMs;
        private static string s_waveFirst;
        private static readonly System.Text.StringBuilder s_waveOrder = new System.Text.StringBuilder();

        private static long FillDist(Vector2s z, int px, int pz)
        {
            int dx = z.x - px, dz = z.y - pz;
            long cheb = Math.Max(Math.Abs(dx), Math.Abs(dz));
            return cheb * 1000000L + (long)dx * dx + (long)dz * dz;   // Chebyshev first, Euclidean (squared) breaks ties
        }

        private static bool RingFresh(Vector2s n)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (s_gridPending.Contains(new Vector2s(n.x + dx, n.y + dz))) return false;
            return true;
        }

        // 0.2.1322 (PLAN_fat_driver_stalls step 1d, proof only): one snow-depth step's phases (Debug 10 s, > 8 ms flagged).
        private static readonly StallPhaseTimer s_gridStepTimer = new StallPhaseTimer("[VoxelSnow] grid step", 8.0, "Core FillZoneDepth", "mask underwater", "commit", "ring queue");
        private static readonly StallPhaseTimer s_buildStepTimer = new StallPhaseTimer("[VoxelSnow] texture step", 8.0, "soften", "re-zero underwater", "upload", "winter probe");

        private static void GridStep(Vector2s z)
        {
            s_gridPending.Remove(z);
            if (s_tex.ContainsKey(z))
            {
                int gc0 = StallPhaseTimer.Gc();
                bool had = s_depthGrid.TryGetValue(z, out var before);
                var after = Grid(z, true);
                NoteRefill(z, before, had, after);   // 0.2.1322 proof: same bytes = a re-dirty that changed nothing
                var gt = s_gridStepTimer.T;
                for (int i = 0; i < 4; i++) gt[i] = s_gridT[i];
                RingQueue(z);
                gt[4] = StallPhaseTimer.Now();
                s_gridStepTimer.Note(StallPhaseTimer.Gc() - gc0, $"zone ({z.x},{z.y})");
                return;
            }
            s_depthGrid.Remove(z);   // no texture: the slow's grid refills on its next read
            JobGridChanged(z);   // 0.2.1322 (only reached with no resumable step running)
            RingQueue(z);
        }

        private static void RingQueue(Vector2s z)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var n = new Vector2s(z.x + dx, z.y + dz);
                    if (s_tex.TryGetValue(n, out var t) && t != null) s_buildPending.Add(n);
                }
        }

        private static void BuildStep(Vector2s z, bool hasPlayer, int px, int pz)
        {
            s_buildPending.Remove(z);
            if (s_tex.TryGetValue(z, out var t) && t != null)
            {
                int gc0 = StallPhaseTimer.Gc();
                Build(z, t);
                var bt = s_buildStepTimer.T;
                for (int i = 0; i < 5; i++) bt[i] = s_buildT[i];
                s_buildStepTimer.Note(StallPhaseTimer.Gc() - gc0, $"zone ({z.x},{z.y})");
                WaveNote(z, hasPlayer, px, pz);
            }
        }

        private static void WaveNote(Vector2s z, bool hasPlayer, int px, int pz)
        {
            s_waveBuilds++;
            if (s_waveFirst == null)
                s_waveFirst = $"({z.x},{z.y}){(hasPlayer ? (z.x == px && z.y == pz ? " = player zone" : $" (player zone ({px},{pz}))") : " (no player: old order)")} after {(Time.unscaledTime - s_waveStartAt) * 1000f:0} ms";
            if (s_waveBuilds <= 8) s_waveOrder.Append(s_waveBuilds > 1 ? "," : "").Append('(').Append(z.x).Append(',').Append(z.y).Append(')');
        }

        // ── 0.2.1322 ([lead] 10-31, PLAN_fat_driver_stalls F1, key "Snow depth step resumable", default OFF): one grid step
        // (FillZoneDepth + MaskUnderwater over 32 rows) or texture step (SoftTexel over 34 rows + the underwater re-zero over 34
        // rows + upload) cost ~12 ms against the 2 ms budget, and the "at least one step" rule ran it whole every frame. With the
        // key on, ONE step at a time runs as a job in units (the Core fill call, one row, the commit / upload) under the same
        // budget (at least one unit a frame) and carries its cursor to the next frame. Nothing is visible until the step is
        // complete: a grid job fills its own buffer and commits s_depthGrid / s_wetTexels at the end (the zone stays in
        // s_gridPending meanwhile, so no texture builds from it); a texture job softens into its own buffer and uploads at the
        // end. Same rows, same order, same functions as the one-frame step = identical result. Coalescing (provably same only):
        //  - repeat marks in a frame / while pending are already one entry (HashSets; counted as merged);
        //  - a mark for the zone a grid job is filling restarts that job ONCE (a flag, however many marks) - Core changed the data;
        //  - a mark in a texture job's 3x3 ring drops the job: the zone stays in s_buildPending and rebuilds after the new grid
        //    (the one-frame path would have built it now AND again then; the final texture is the same);
        //  - a grid the texture job reads changed outside the drain (first bind, first-show retry, paint-version clear, Forget)
        //    restarts it once. Refills the job itself triggers (a missing neighbour grid read through SharedTexel) do not.
        // Water state (IceFrost) is read when each row runs, as the one-frame step reads it when it runs.
        private sealed class SnowJob
        {
            internal bool IsGrid, Restart, Snowy;
            internal Vector2s Zone;
            internal int Phase, Row, Rez, Slices;
            internal byte[] G;
            internal MaskAcc Acc = new MaskAcc();
            internal Texture2D Tex;
            internal bool HasPlayer; internal int Px, Pz;
        }
        private static SnowJob s_job;
        private static bool s_inJobSlice, s_resumableSaid;
        private static readonly byte[] s_jobBuf = new byte[Size * Size];
        private static readonly byte[] s_jobPad = new byte[Pad * Pad];
        private static int s_rsSteps, s_rsSlices, s_rsRestarts, s_rsDropped, s_rsMaxSlices;
        private static double s_rsWorstSliceMs;
        private static float s_rsLogAt;

        private static bool ResumableOn => FiresVoxelConfig.SnowDepthStepResumable != null && FiresVoxelConfig.SnowDepthStepResumable.Value;
        private static bool InRing(Vector2s c, Vector2s z) => Math.Abs(c.x - z.x) <= 1 && Math.Abs(c.y - z.y) <= 1;

        private static void JobMarked(Vector2s z)
        {
            var j = s_job;
            if (j == null) return;
            if (j.IsGrid) { if (j.Zone == z) j.Restart = true; }
            else if (InRing(j.Zone, z)) { s_job = null; s_rsDropped++; }
        }

        private static void JobGridChanged(Vector2s z)
        {
            var j = s_job;
            if (j == null || s_inJobSlice) return;
            if (j.IsGrid ? j.Zone == z : InRing(j.Zone, z)) j.Restart = true;
        }

        private static void JobAllGridsChanged()
        {
            var j = s_job;
            if (j != null && !s_inJobSlice) j.Restart = true;
        }

        private static void StartGridJob(Vector2s z)
        {
            if (!s_tex.ContainsKey(z)) { GridStep(z); return; }   // no texture: nothing to slice (one dictionary remove)
            s_job = new SnowJob { IsGrid = true, Zone = z };
        }

        private static void StartBuildJob(Vector2s z, bool hasPlayer, int px, int pz)
        {
            if (!s_tex.TryGetValue(z, out var t) || t == null) { BuildStep(z, hasPlayer, px, pz); return; }
            s_job = new SnowJob { IsGrid = false, Zone = z, Tex = t, HasPlayer = hasPlayer, Px = px, Pz = pz };
        }

        // One unit of the running job. True = its step completed (committed / uploaded) this unit.
        private static bool RunJobUnit()
        {
            var j = s_job;
            if (j.Restart) { j.Restart = false; j.Phase = 0; j.Row = 0; j.Rez = 0; j.G = null; j.Snowy = false; j.Acc = new MaskAcc(); s_rsRestarts++; }
            long t0 = StallPhaseTimer.Now();
            bool done = false;
            s_inJobSlice = true;
            try
            {
                if (j.IsGrid)
                {
                    if (j.Phase == 0)
                    {
                        j.Snowy = FillInto(j.Zone, s_jobBuf);
                        j.G = j.Snowy ? (byte[])s_jobBuf.Clone() : null;   // false = buffer untouched: no snow in this zone
                        j.Phase = j.G != null ? 1 : 2;
                    }
                    else if (j.Phase == 1)
                    {
                        MaskRows(j.Zone, j.G, j.Row, j.Row + 1, j.Acc);
                        if (++j.Row >= Size) j.Phase = 2;
                    }
                    else
                    {
                        bool had = s_depthGrid.TryGetValue(j.Zone, out var before);
                        if (j.G != null) MaskEnd(j.Zone, j.Acc); else s_wetTexels.Remove(j.Zone);
                        GridCommit(j.Zone, j.G, j.Snowy);
                        NoteRefill(j.Zone, before, had, j.G);
                        s_gridPending.Remove(j.Zone);
                        RingQueue(j.Zone);
                        done = true;
                    }
                }
                else
                {
                    if (j.Phase == 0)
                    {
                        SoftRows(j.Zone, s_jobPad, j.Row, j.Row + 1);
                        if (++j.Row >= Pad) { j.Phase = 1; j.Row = 0; }
                    }
                    else if (j.Phase == 1)
                    {
                        j.Rez += RezeroRows(j.Zone, s_jobPad, j.Row, j.Row + 1);
                        if (++j.Row >= Pad) j.Phase = 2;
                    }
                    else
                    {
                        s_buildPending.Remove(j.Zone);
                        if (j.Tex != null && s_tex.TryGetValue(j.Zone, out var t) && t == j.Tex)
                        {
                            if (!s_seamSaid) { s_seamSaid = true; FiresLogger.LogDebug($"[SnowSeam] padded zone snow textures executing: {Pad}x{Pad}, one-texel margin from the neighbour zones (seam-free raise at zone borders)"); }
                            if (t.width != Pad) { t.Reinitialize(Pad, Pad); }
                            BuildSaid();
                            RezeroLog(j.Zone, j.Rez);
                            t.LoadRawTextureData(s_jobPad);
                            t.Apply(false, false);
                            WinterProbe(j.Zone);
                            WaveNote(j.Zone, j.HasPlayer, j.Px, j.Pz);
                        }
                        done = true;
                    }
                }
            }
            finally { s_inJobSlice = false; }
            double ms = (StallPhaseTimer.Now() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            j.Slices++; s_rsSlices++;
            if (ms > s_rsWorstSliceMs) s_rsWorstSliceMs = ms;
            if (done)
            {
                s_job = null; s_rsSteps++;
                if (j.Slices > s_rsMaxSlices) s_rsMaxSlices = j.Slices;
            }
            return done;
        }

        private static void LogResumable(float budget)
        {
            if (!s_resumableSaid) { s_resumableSaid = true; FiresLogger.LogInfo($"[VoxelSnow] snow depth step resumable ON: steps run in row slices under the {budget:0.0} ms budget, published when complete (key \"Snow depth step resumable\", 0.2.1322)"); }
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_rsLogAt) return;
            s_rsLogAt = Time.unscaledTime + 10f;
            if (s_rsSlices == 0) return;
            int merged;
            lock (s_dirtyLock) merged = s_mergedSameFrame + s_mergedPending;
            FiresLogger.LogDebug($"[VoxelSnow] resumable step 10 s: {s_rsSteps} step(s) completed in {s_rsSlices} slice(s) (avg {(s_rsSteps > 0 ? (double)s_rsSlices / s_rsSteps : 0):0.0}, most {s_rsMaxSlices} per step), worst slice {s_rsWorstSliceMs:0.00} ms (budget {budget:0.0}); restarted once by a mark / grid change {s_rsRestarts}, texture steps dropped for a ring mark (rebuilt after) {s_rsDropped}; merged marks so far this window {merged}; running: {(s_job == null ? "none" : (s_job.IsGrid ? "grid" : "texture") + $" ({s_job.Zone.x},{s_job.Zone.y}) phase {s_job.Phase} row {s_job.Row}")} (0.2.1322)");
            s_rsSteps = s_rsSlices = s_rsRestarts = s_rsDropped = s_rsMaxSlices = 0; s_rsWorstSliceMs = 0;
        }

        private static void DrainDepth()
        {
            bool resumable = ResumableOn;   // 0.2.1322
            if (!resumable && s_job != null) s_job = null;   // key turned off mid-step: its zone is still pending, the one-frame step redoes it
            if (s_gridPending.Count == 0 && s_buildPending.Count == 0) return;
            float budget = FiresVoxelConfig.SnowDepthBudgetMs != null ? FiresVoxelConfig.SnowDepthBudgetMs.Value : 0f;
            int startGrids = s_gridPending.Count, steps = 0, units = 0;   // 0.2.1322: units = steps when the key is off
            if (!s_waveOn) { s_waveOn = true; s_waveStartAt = Time.unscaledTime; s_waveBuilds = 0; s_waveWorstMs = 0; s_waveFirst = null; s_waveOrder.Clear(); }
            bool hasPlayer = Player.m_localPlayer != null;
            int px = 0, pz = 0;
            if (hasPlayer) { var pzone = ZoneSystem.GetZone(Player.m_localPlayer.transform.position); px = pzone.x; pz = pzone.y; }
            s_drainSw.Restart();
            if (hasPlayer)
            {
                while (s_gridPending.Count > 0 || s_buildPending.Count > 0)
                {
                    if (budget > 0f && units > 0 && s_drainSw.Elapsed.TotalMilliseconds >= budget) break;
                    if (resumable && s_job != null) { if (RunJobUnit()) steps++; units++; continue; }
                    bool haveG = false, haveB = false; Vector2s g = default, b = default; long gd = long.MaxValue, bd = long.MaxValue;
                    foreach (var k in s_gridPending) { long d = FillDist(k, px, pz); if (d < gd) { gd = d; g = k; haveG = true; } }
                    foreach (var k in s_buildPending) { long d = FillDist(k, px, pz); if (d < bd && RingFresh(k)) { bd = d; b = k; haveB = true; } }
                    if (resumable)
                    {
                        if (haveB && (!haveG || bd <= gd)) StartBuildJob(b, true, px, pz);
                        else if (haveG) StartGridJob(g);
                        else break;
                        if (s_job == null) { steps++; units++; }   // finished instantly (no texture)
                        continue;
                    }
                    if (haveB && (!haveG || bd <= gd)) BuildStep(b, true, px, pz);
                    else if (haveG) GridStep(g);
                    else break;   // cannot happen: a pending build with no pending grid is always ring-fresh
                    steps++; units++;
                }
            }
            else
            {
                while (s_gridPending.Count > 0)
                {
                    if (budget > 0f && units > 0 && s_drainSw.Elapsed.TotalMilliseconds >= budget) break;
                    if (resumable && s_job != null) { if (RunJobUnit()) steps++; units++; continue; }
                    Vector2s z = default; foreach (var k in s_gridPending) { z = k; break; }
                    if (resumable) { StartGridJob(z); if (s_job == null) { steps++; units++; } continue; }
                    GridStep(z);
                    steps++; units++;
                }
                // Textures only once every pending grid is fresh, so a ring never builds from a half-updated neighbour set.
                if (s_gridPending.Count == 0)
                {
                    if (resumable)
                    {
                        while (s_buildPending.Count > 0 || s_job != null)
                        {
                            if (budget > 0f && units > 0 && s_drainSw.Elapsed.TotalMilliseconds >= budget) break;
                            if (s_job != null) { if (RunJobUnit()) steps++; units++; continue; }
                            Vector2s z = default; foreach (var k in s_buildPending) { z = k; break; }
                            StartBuildJob(z, false, 0, 0);
                            if (s_job == null) { steps++; units++; }
                        }
                    }
                    else
                    {
                        s_drainScratch.Clear(); s_drainScratch.AddRange(s_buildPending);
                        foreach (var z in s_drainScratch)
                        {
                            if (budget > 0f && steps > 0 && s_drainSw.Elapsed.TotalMilliseconds >= budget) break;
                            BuildStep(z, false, 0, 0);
                            steps++; units++;
                        }
                    }
                }
            }
            double ms = s_drainSw.Elapsed.TotalMilliseconds;
            if (resumable) LogResumable(budget);
            if (ms > s_waveWorstMs) s_waveWorstMs = ms;
            if (s_gridPending.Count == 0 && s_buildPending.Count == 0)
            {
                s_waveOn = false;
                if (s_waveBuilds > 0)
                {
                    if (!s_fillInfoSaid) { s_fillInfoSaid = true; FiresLogger.LogInfo($"[SnowFill] snow depth fills nearest-first from the player's zone: first zone rebuilt {s_waveFirst} (0.2.1317)"); }
                    if (FiresLogger.DebugEnabled && (!s_waveFirstSaid || Time.unscaledTime >= s_waveLogAt))
                    {
                        s_waveFirstSaid = true; s_waveLogAt = Time.unscaledTime + 10f;
                        FiresLogger.LogDebug($"[SnowFill] first zone rebuilt {s_waveFirst}; order: {s_waveOrder}{(s_waveBuilds > 8 ? ",..." : "")}; {s_waveBuilds} texture(s) in {(Time.unscaledTime - s_waveStartAt) * 1000f:0} ms, worst frame {s_waveWorstMs:0.0} ms (0.2.1317)");
                    }
                }
            }
            s_drainFrames++; s_drainSteps += steps;
            if (ms > s_drainWorstMs) s_drainWorstMs = ms;
            bool spread = s_gridPending.Count > 0 || s_buildPending.Count > 0;
            if (spread) s_drainSpread++;
            if (FiresLogger.DebugEnabled && (spread || ms >= 20.0) && Time.unscaledTime >= s_drainLogAt)
            {
                s_drainLogAt = Time.unscaledTime + 10f;
                FiresLogger.LogDebug($"[VoxelSnow] depth drain: {startGrids} changed zone(s) waiting, {steps} step(s) in {ms:0.0} ms (budget {(budget > 0f ? budget.ToString("0.0") + " ms" : "off = all in one frame")}); left for next frame: {s_gridPending.Count} grid(s), {s_buildPending.Count} texture(s); since the last line {s_drainFrames} drain frame(s), {s_drainSpread} spread, {s_drainSteps} step(s), worst {s_drainWorstMs:0.0} ms (0.2.1196)");
                s_drainFrames = s_drainSpread = s_drainSteps = 0; s_drainWorstMs = 0;
            }
        }

        // A zone the streamer dropped: its texture goes with it.
        internal static void Forget(Vector2s zone)
        {
            s_gridPending.Remove(zone); s_buildPending.Remove(zone);
            if (s_job != null && s_job.Zone == zone) s_job = null;   // 0.2.1322: its resumable step goes with it
            s_depthGrid.Remove(zone);
            JobGridChanged(zone);   // 0.2.1322: a texture step reading this grid restarts once
            lock (s_dirtyLock) s_zoneLastCause.Remove(zone);   // 0.2.1322 proof map stays bounded
            s_texBornAt.Remove(zone); s_shown.Remove(zone);   // 0.2.1310
            s_retryAt.Remove(zone);   // 0.2.1346
            if (s_tex.TryGetValue(zone, out var tex)) { if (tex != null) UnityEngine.Object.Destroy(tex); s_tex.Remove(zone); }
        }
    }
}
