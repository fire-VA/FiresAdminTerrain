using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1112 (Fire: "Wide trails that are already saved should fill in due to our snow anyways"). Nothing refilled the trail paint:
    // vanilla never decays TerrainComp G (its only snow re-mask is TerrainComp.SetSnowMask, TerrainComp.cs:969, with no caller), and
    // Core's refill (SnowPathMask.cs:21 BaseRefillSeconds 1800, RefillBoost 2 snowing / 4 blizzard, SnowTicker.cs:69) works on Core's
    // own 4 m path mask, which walk ops have skipped since Core 0.2.830. So FAT decays its own trail G (FatSnowTrail) toward 0 while it
    // snows, at Core's path refill rate (one game day / boost), and leaves it in clear weather. On the TerrainComp owner, written
    // into m_paintMask + Save + Poke like vanilla's DoOperation (TerrainComp.cs:417-420), so it saves and syncs. Voxel Deep North
    // cells only (cultivated soil elsewhere also lives in G). Bounded: CompsPerPass comps every PassSeconds, round robin.
    internal static class FatSnowTrailRefill
    {
        private const float PassSeconds = 2f;
        private const int CompsPerPass = 4;
        private const double BaseRefillSeconds = 1800.0;   // Core SnowPathMask: one game day of world time
        // fat_proof_snowsoil_test speeds the decay; 1 otherwise. 0.2.1150 (Fire: "fills back in sometimes when we walk through it"):
        // the tests set 200 and reset it to 1 after their waits, outside any finally - a test that stopped in between (an exception,
        // the round closed) left every trail refilling 200x faster (a fresh trench gone in seconds while snowing). The scale now
        // expires 30 s after it was set, whatever the caller does.
        private static float s_testScale = 1f, s_testScaleUntil;
        internal static float TestRateScale
        {
            get => Time.unscaledTime < s_testScaleUntil ? s_testScale : 1f;
            set { s_testScale = value; s_testScaleUntil = value != 1f ? Time.unscaledTime + 30f : 0f; }
        }
        // 0.2.1150 proof: cells lowered within LowerLogRadius of the local player (the walker), for the "[SnowTrail] G LOWERED" lines
        // and fat_proof's trench_continuous (only the refill and vanilla's own restore can lower trail G; the writer only raises it).
        internal const float LowerLogRadius = 6f;
        internal static int LoweredNearPlayer;
        private static bool s_loweredInfoSaid;
        private static float s_loweredLogAt;

        private static readonly AccessTools.FieldRef<object> Instances = AccessTools.StaticFieldRefAccess<object>(AccessTools.Field(typeof(TerrainComp), "s_instances"));
        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> Hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> PaintMask = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedPaint = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly AccessTools.FieldRef<TerrainComp, ZNetView> NView = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
        private static readonly System.Action<TerrainComp, bool> Save = AccessTools.MethodDelegate<System.Action<TerrainComp, bool>>(AccessTools.Method(typeof(TerrainComp), "Save"));

        private static readonly Dictionary<TerrainComp, double> s_last = new Dictionary<TerrainComp, double>();
        private static bool s_hmRefillSaid;   // 0.2.1309
        private static bool s_voxelOffSaid;   // 0.2.1312
        private static int s_cursor;
        private static float s_nextPass, s_nextLog, s_gProbeAt;
        private static bool s_resume;   // 0.2.1195: the last pass stopped at FatBudget and resumes next frame
        private static int s_sliced;
        private static int s_remain;
        // 0.2.1196: refill-only G changes push (Save + Poke = the chunk remesh) at most once every RefillPushSeconds per comp
        internal const float RefillPushSeconds = 10f;
        private static readonly Dictionary<TerrainComp, float> s_pushedAt = new Dictionary<TerrainComp, float>();
        private static readonly HashSet<TerrainComp> s_unpushed = new HashSet<TerrainComp>();
        private static int s_held, s_pushes;
        private static int s_cellsSinceLog;

        // the local player (host/client owner) or, on a dedicated server, any connected peer's reference position
        private static bool NearWalker(Vector3 w)
        {
            float r2 = LowerLogRadius * LowerLogRadius;
            var lp = Player.m_localPlayer;
            if (lp != null && (new Vector2(w.x - lp.transform.position.x, w.z - lp.transform.position.z)).sqrMagnitude <= r2) return true;
            var zn = ZNet.instance;
            if (zn == null || !zn.IsServer()) return false;
            foreach (var peer in zn.GetPeers())
                if (peer != null && (new Vector2(w.x - peer.m_refPos.x, w.z - peer.m_refPos.z)).sqrMagnitude <= r2) return true;
            return false;
        }

        // 0.2.1155 (Fire, round 35 refill answers): "10 min untouched" - a trail cell gets no refill for GraceSeconds (real time)
        // after the last trail op wrote it; "1x (~30 min full)" - snow and storms both refill at the normal snowfall speed (the old
        // Core 2x / 4x boost erased a fresh trench in seconds in his blizzards); "follow Vedr's weather" - the weather source is
        // Vedr's when its calendar runs and the shown environment is one of Vedr's (Vedr_WinterSnow / Vedr_WinterStorm snow,
        // Vedr_WinterClear / Vedr_WinterFog don't), else the env name the way Core's SnowTicker reads it.
        internal const float GraceSeconds = 600f;
        private static readonly Dictionary<TerrainComp, float[]> s_graceUntil = new Dictionary<TerrainComp, float[]>();
        private static int s_graceSkipped;
        private static bool s_graceInfoSaid;

        // From FatSnowTrail's owner write: every cell within half + 1 m of the written segment a..b keeps its G for GraceSeconds.
        internal static void NoteTrailWrite(TerrainComp comp, Heightmap hm, int pitch, Vector3 a, Vector3 b, float half)
        {
            if (comp == null || hm == null || pitch <= 0) return;
            if (!s_graceUntil.TryGetValue(comp, out var until) || until.Length != pitch * pitch)
            {
                if (s_graceUntil.Count >= 256) PruneGrace();
                until = new float[pitch * pitch];
                s_graceUntil[comp] = until;
            }
            float reach = half + 1f, t = Time.unscaledTime + GraceSeconds;
            hm.WorldToVertexMask(new Vector3(Mathf.Min(a.x, b.x) - reach, 0f, Mathf.Min(a.z, b.z) - reach), out int x0, out int y0);
            hm.WorldToVertexMask(new Vector3(Mathf.Max(a.x, b.x) + reach, 0f, Mathf.Max(a.z, b.z) + reach), out int x1, out int y1);
            for (int y = Mathf.Max(0, y0); y <= Mathf.Min(pitch - 1, y1); y++)
                for (int x = Mathf.Max(0, x0); x <= Mathf.Min(pitch - 1, x1); x++)
                    until[y * pitch + x] = t;
        }

        private static readonly List<TerrainComp> s_pruneTmp = new List<TerrainComp>();
        private static void PruneGrace()
        {
            float now = Time.unscaledTime;
            s_pruneTmp.Clear();
            foreach (var kv in s_graceUntil)
            {
                if (kv.Key == null) { s_pruneTmp.Add(kv.Key); continue; }
                bool live = false;
                var u = kv.Value;
                for (int i = 0; i < u.Length && !live; i++) live = u[i] > now;
                if (!live) s_pruneTmp.Add(kv.Key);
            }
            foreach (var k in s_pruneTmp) s_graceUntil.Remove(k);
        }

        private static System.Func<bool> s_vedrReady;
        private static bool s_vedrBound;
        private static bool VedrReady()
        {
            if (!s_vedrBound)
            {
                s_vedrBound = true;
                try
                {
                    var api = System.Type.GetType("Vedr.VedrApi, Norger.Vedr");
                    var getter = api?.GetProperty("IsReady")?.GetGetMethod();
                    if (getter != null) s_vedrReady = (System.Func<bool>)System.Delegate.CreateDelegate(typeof(System.Func<bool>), getter);
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[SnowTrail] VedrApi.IsReady not bound: {ex.Message}"); }
            }
            try { return s_vedrReady != null && s_vedrReady(); } catch { return false; }
        }

        private static System.Reflection.MethodInfo s_shown;
        private static bool s_shownBound;
        private static bool TryVedrShown(out string name, out bool snowing, out float intensity)
        {
            name = ""; snowing = false; intensity = 0f;
            if (!s_shownBound)
            {
                s_shownBound = true;
                try
                {
                    var api = System.Type.GetType("Vedr.VedrApi, Norger.Vedr");
                    var ver = api?.GetField("ApiVersion")?.GetValue(null) ?? api?.GetProperty("ApiVersion")?.GetValue(null);
                    if (ver != null && System.Convert.ToInt32(ver) >= 8) s_shown = api.GetMethod("GetShownWeather");
                    FiresLogger.LogDebug($"[SnowTrail] VedrApi GetShownWeather {(s_shown != null ? "bound" : $"not available (ApiVersion {ver ?? "none"}): env-name fallback")}");
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[SnowTrail] GetShownWeather not bound: {ex.Message}"); }
            }
            if (s_shown == null) return false;
            try
            {
                var args = new object[] { null, false, 0f };
                if (!(bool)s_shown.Invoke(null, args)) return false;
                name = args[0] as string ?? ""; snowing = (bool)args[1]; intensity = args[2] is float fi ? fi : 0f;
                return true;
            }
            catch { return false; }
        }

        internal static bool IsSnowing(out float boost) => IsSnowing(out boost, out _, out _);

        internal static bool IsSnowing(out float boost, out string source, out string env)
        {
            env = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment()?.m_name ?? "" : "";
            bool snow;
            // 0.2.1156 (Fire: "follow Vedr's weather"): VedrApi 8 GetShownWeather = the weather this client SHOWS.
            if (TryVedrShown(out string shown, out bool shownSnow, out float shownIntensity))
            {
                source = "Vedr shown"; env = shown;
                // 0.2.1188 (Fire: "Faster in storms"): storm / blizzard (intensity >= 1) refills at StormRefillRate, snow at SnowRefillRate
                boost = shownSnow ? (shownIntensity >= 1f ? StormRefillRate : SnowRefillRate) : 0f;
                return shownSnow;
            }
            if (VedrReady() && env.StartsWith("Vedr_", System.StringComparison.Ordinal))
            {
                source = "Vedr";
                snow = env.IndexOf("Snow", System.StringComparison.OrdinalIgnoreCase) >= 0 || env.IndexOf("Storm", System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
            else
            {
                // the same reading as Core's SnowTicker (SnowTicker.cs:185-186): "snow" in the env name
                source = "EnvMan";
                snow = env.IndexOf("snow", System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
            bool storm = env.IndexOf("Storm", System.StringComparison.OrdinalIgnoreCase) >= 0 || env.IndexOf("Blizzard", System.StringComparison.OrdinalIgnoreCase) >= 0;
            boost = snow ? (storm ? StormRefillRate : SnowRefillRate) : 0f;   // 0.2.1188: Fire "Faster in storms" (was 1x alike, 0.2.1155)
            return snow;
        }

        private static string s_decision;
        private static void NoteDecision(bool snowing, float boost, string source, string env)
        {
            string d = $"snowing {(snowing ? "y" : "n")}, speed x{boost:0}, weather source {source}";
            if (d == s_decision) return;
            s_decision = d;
            FiresLogger.LogDebug($"[SnowTrail] refill decision: {d} (env '{env}'), grace {GraceSeconds:0} s after a trail write, full refill ~{BaseRefillSeconds / 60.0:0} min of snowfall"
                + $" - Fire: 10 min untouched, 1x, follow Vedr's weather");
        }

        [HarmonyPatch(typeof(ZNet), "Update")]
        private static class Patch_Tick
        {
            private static void Postfix()
            {
                long t0 = global::VerdantsAscent.Terrain.Performance.FatZnetTiming.Now();   // 0.2.1268
                try { Body(); }
                finally { global::VerdantsAscent.Terrain.Performance.FatZnetTiming.Add(global::VerdantsAscent.Terrain.Performance.FatZnetTiming.Slot.TrailRefill, t0); }
            }

            private static void Body()
            {
                // 0.2.1150 proof (Debug only): the trail G under the local player and its 3x3, once a second while in deep snow
                if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_gProbeAt && Player.m_localPlayer != null)
                {
                    s_gProbeAt = Time.unscaledTime + 1f;
                    try
                    {
                        var p = Player.m_localPlayer.transform.position;
                        // 0.2.1369: voxel paint only. A heightmap has no such mask, so this line read a false "G 0.00" there.
                        if (VoxelWorld.ChunkCount > 0 && VoxelSnowDepth.DepthMetresAt(p) > 0.3f)
                        {
                            float g0 = VoxelWorld.PaintGAt(p.x, p.z), mn = 9f, mx = -1f;
                            for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) { float g = VoxelWorld.PaintGAt(p.x + i, p.z + j); mn = Mathf.Min(mn, g); mx = Mathf.Max(mx, g); }
                            FiresLogger.LogDebug($"[SnowTrail] G at the player ({p.x:0.0},{p.z:0.0}) {g0:0.00}, 3x3 min {mn:0.00} max {mx:0.00} (snowing {IsSnowing(out _)})");
                        }
                    }
                    catch { }
                }
                if (Time.unscaledTime < s_nextPass) return;
                s_nextPass = Time.unscaledTime + PassSeconds;
                try { Pass(); if (s_resume) s_nextPass = 0f; }   // 0.2.1195: an unfinished (sliced) pass resumes next frame
                catch (System.Exception ex) { FiresLogger.LogDebug($"[SnowTrail] bail: refill threw: {ex.Message}"); }
            }
        }

        // One pass; returns the cells decayed. The proof test calls it directly.
        // 0.2.1188 (Fire: "our homestead area normal winter snow should fill in during storms as well instead of always being
        // trampled"): WHILE it snows the 1155 10-min grace no longer holds trampled cells - they refill even while walked on (walkers
        // keep carving fresh trails that fill back in). Clear weather has no refill at all (trails stay). One named rate
        // (Fire 10-28: 1x, ~30 min full in snow and storms alike); a later "faster in storms" is this one line.
        internal const float SnowRefillRate = 1f, StormRefillRate = 2f;   // Fire 10-28: "Faster in storms"

        internal static int Pass()
        {
            long tPass = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return PassInner(); }
            finally { FatBudget.Spend(FatBudget.Who.Refill, (System.Diagnostics.Stopwatch.GetTimestamp() - tPass) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); }
        }

        private static int PassInner()
        {
            if (ZNet.instance == null || WorldGenerator.instance == null || !(Instances() is List<TerrainComp> comps) || comps.Count == 0) return 0;
            double now = ZNet.instance.GetTimeSeconds();
            bool snowing = IsSnowing(out float boost, out string source, out string env);
            NoteDecision(snowing, boost, source, env);
            float tnow = Time.unscaledTime;
            int cells = 0;
            // a fresh pass visits CompsPerPass comps; a resumed slice only the ones that pass had left (same rate as before)
            if (!s_resume) s_remain = System.Math.Min(CompsPerPass, comps.Count);
            s_resume = false;
            for (int k = 0; s_remain > 0; k++)
            {
                // 0.2.1195 (live 1193: "[FatBudget] … worst unit 28.3 / 28.5 / 28.7 ms (Refill)" - one pass walked every visited comp's
                // whole paint mask + Save + Poke inside one frame): the pass is sliced. After the first comp it continues only while
                // FatBudget has time left this frame; the rest resumes on the next frame from the same cursor (same end state).
                if (k > 0 && FatBudget.LeftMs() <= 0.0) { s_resume = true; s_sliced++; break; }
                s_cursor = (s_cursor + 1) % comps.Count;
                s_remain--;
                var comp = comps[s_cursor];
                if (comp == null) continue;
                bool had = s_last.TryGetValue(comp, out double last);
                s_last[comp] = now;
                if (!had || !snowing)
                {
                    // 0.2.1196: refill edits held back by the push gate still go out when the snow stops (none are lost)
                    if (!snowing && s_unpushed.Remove(comp)) { var hmp = Hmap(comp); var nvp = NView(comp); if (hmp != null && nvp != null && nvp.IsValid() && nvp.IsOwner()) { Save(comp, true); hmp.Poke(1, true); s_pushedAt[comp] = tnow; s_pushes++; } }
                    continue;   // clear weather: trails stay; the first visit only starts the clock
                }
                var nv = NView(comp);
                if (nv == null || !nv.IsValid() || !nv.IsOwner()) continue;
                var hm = Hmap(comp); var paint = PaintMask(comp); var mod = ModifiedPaint(comp);
                // 0.2.1312 (Fire 10-31: hut patch + trails flip filled <-> trodden): FAT's refill lowered G in memory and saved it at
                // most every 10 s, so a TerrainComp.Load in between reloaded the old trodden G (snap-back); on the dedi it never ran.
                // ONE voxel refill owner now = Core 1066 SnowRefill (owner-only, saved, synced, G down toward base). FAT writes no G here.
                if (hm != null && FatSnowTrail.VoxelWorldHere(hm.transform.position))
                {
                    if (!s_voxelOffSaid) { s_voxelOffSaid = true; FiresLogger.LogDebug("[SnowTrail] refill: voxel paint refilled by Core SnowRefill (owner, synced); FAT refill off for voxel (0.2.1312)"); }
                    continue;
                }
                if (hm == null || paint == null || mod == null || !FatSnowTrail.VoxelWorldHere(hm.transform.position))
                {
                    // 0.2.1309: heightmap (non-voxel) ground is refilled by Core toward its base mask (the Deep North way); FAT refills voxel only
                    if (hm != null && !s_hmRefillSaid) { s_hmRefillSaid = true; FiresLogger.LogDebug($"[SnowTrail] refill: heightmap TerrainComp at ({hm.transform.position.x:0},{hm.transform.position.z:0}) skipped - Core refills heightmap ground, FAT refills voxel ground only (0.2.1309)"); }
                    continue;
                }
                float drop = (float)((now - last) * boost * TestRateScale / BaseRefillSeconds);
                if (drop <= 0f) continue;
                int n = 0, pitch = Mathf.RoundToInt(Mathf.Sqrt(paint.Length));
                s_graceUntil.TryGetValue(comp, out var grace);
                if (grace != null && grace.Length != paint.Length) grace = null;
                if (snowing) grace = null;   // 0.2.1188: no grace while it snows (Fire)
                for (int i = 0; i < paint.Length; i++)
                {
                    var c = paint[i];
                    if (c.g <= 0f) continue;
                    if (i < mod.Length && !mod[i]) continue;   // 0.2.1310: only player-edited paint is a trail (never a base value)
                    if (grace != null && grace[i] > tnow)
                    {
                        s_graceSkipped++;
                        if (!s_graceInfoSaid) { s_graceInfoSaid = true; var gw = hm.VertexMaskToWorld(i % pitch, i / pitch); FiresLogger.LogDebug($"[SnowTrail] refill grace held a fresh trail cell at ({gw.x:0.0},{gw.z:0.0}): G {c.g:0.00} kept, {grace[i] - tnow:0} s of the {GraceSeconds:0} s grace left - Fire: 10 min untouched (0.2.1155; once per session)"); }
                        continue;
                    }   // 0.2.1155: fresh trail, 10 min untouched
                    var w = hm.VertexMaskToWorld(i % pitch, i / pitch);
                    // 0.2.1140: Deep North or Mountain cells (trail G there); cultivated soil in other biomes stays untouched
                    if (!FatSnowTrail.SnowTrailBiome(w)) continue;   // 0.2.1154: snow depth, any biome
                    float oldG = c.g;
                    c.g = Mathf.Max(0f, c.g - drop);
                    paint[i] = c;
                    if (NearWalker(w))
                    {
                        LoweredNearPlayer++;
                        if (!s_loweredInfoSaid)
                        {
                            s_loweredInfoSaid = true;
                            FiresLogger.LogDebug($"[SnowTrail] G LOWERED at ({w.x:0.0},{w.z:0.0}) {oldG:0.00}->{c.g:0.00} by the snowfall refill (weather snowing, boost x{boost:0}, test scale x{TestRateScale:0}) - trails near a walker refill while it snows (by design; once per session)");
                        }
                        else if (Time.unscaledTime >= s_loweredLogAt)
                        {
                            s_loweredLogAt = Time.unscaledTime + 1f;
                            FiresLogger.LogDebug($"[SnowTrail] G LOWERED at ({w.x:0.0},{w.z:0.0}) {oldG:0.00}->{c.g:0.00} by the snowfall refill (boost x{boost:0}, test scale x{TestRateScale:0})");
                        }
                    }
                    mod[i] = true;
                    n++;
                }
                // 0.2.1196 (live 1193 round51: the same homestead chunks re-applied over and over - "slow apply" (9,1,28) x30,
                // (9,1,29) x22, (10,1,28) x22, (8,1,28) x21 at 12-22 ms each, the 0.25 m G refinement on every remesh; the lead/Fire's
                // pick "A"): a comp whose G changed ONLY by this snowfall refill is saved + poked (which is what remeshes its chunks) at
                // most once every RefillPushSeconds. The G edits keep landing in the paint arrays every pass; the next push carries all
                // of them, so the trench reaches the same end state in fewer, larger steps.
                bool pending = s_unpushed.Contains(comp);
                if (n == 0 && !pending) continue;
                cells += n;
                if (s_pushedAt.TryGetValue(comp, out float pushedAt) && tnow - pushedAt < RefillPushSeconds) { s_unpushed.Add(comp); s_held++; continue; }
                Save(comp, true);
                hm.Poke(1, true);
                s_pushedAt[comp] = tnow;
                s_unpushed.Remove(comp);
                s_pushes++;
            }
            s_cellsSinceLog += cells;
            if (Time.unscaledTime >= s_nextLog && (s_cellsSinceLog > 0 || snowing))
            {
                s_nextLog = Time.unscaledTime + 10f;
                FiresLogger.LogDebug($"[SnowTrail] refill pass: weather '{env}' (snowing {(snowing ? "y" : "n")}, source {source}), grace {(snowing ? "off" : "on")}, cells refilled {s_cellsSinceLog}, rate x{boost:0.##}; {s_graceSkipped} cell visit(s) kept by the grace (0.2.1188); passes sliced at the frame budget {s_sliced} (0.2.1195: a pass continues next frame instead of spiking); comps pushed (saved + remeshed) {s_pushes}, held for the {RefillPushSeconds:0} s push gate {s_held}, waiting {s_unpushed.Count} (0.2.1196)");
                s_cellsSinceLog = 0; s_graceSkipped = 0; s_sliced = 0; s_held = 0; s_pushes = 0;
            }
            if (s_last.Count > 256) s_last.Clear();
            if (s_pushedAt.Count > 256) s_pushedAt.Clear();   // 0.2.1196: destroyed comps drop out (a cleared entry only means "push now")
            s_unpushed.RemoveWhere(c => c == null);
            return cells;
        }
    }
}
