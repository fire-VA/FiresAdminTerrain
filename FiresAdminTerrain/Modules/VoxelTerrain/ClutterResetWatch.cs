using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1080 (Fire: painted Deep North clutter "flash in and out every frame ... until we get real close"): proof of a
    // regenerate loop. ClutterSystem.ResetGrass destroys and rebuilds every patch it touches; called again and again over the
    // same ground, a patch shows, vanishes and reappears frame to frame. Every 5 s, when any caller reset grass more than 20
    // times, one Debug line names the callers (type.method) with their counts and the last centre/radius each used.
    //
    // 0.2.1082 (Fire's 1080 log: 98-240 resets per 5 s, TerrainComp.DoOperation x189 at one spot, FatClutterRegrow.Collider x46):
    // ResetGrass is COALESCED. The ground is split into 16 m cells; a cell re-rolls at most once per Window seconds. A call whose
    // cells were all re-rolled inside the window is held, and its cells re-roll once when the window ends (flushed from
    // ClutterSystem.LateUpdate), so a real edit inside the window still regrows, just once instead of every frame. The watch
    // line now also says how many calls ran, were held and were flushed. Once per 30 s, the call stack of a DoOperation reset
    // is logged, to name whoever keeps placing terrain ops.
    internal static class ClutterResetWatch
    {
        private const float Cell = 16f, Window = 2f;
        private static readonly Dictionary<string, (int n, Vector3 c, float r)> s_calls = new Dictionary<string, (int, Vector3, float)>();
        private static readonly Dictionary<long, float> s_lastReset = new Dictionary<long, float>();
        private static readonly Dictionary<long, float> s_held = new Dictionary<long, float>();   // cell -> its window (0.2.1162: trail cells 8 s)
        private static readonly List<long> s_flush = new List<long>();
        private static float s_next, s_nextStack;
        private static int s_ran, s_skipped, s_flushed;
        private static bool s_inFlush;

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        // 0.2.1162: the trail-only narrowing (FatTrailFast.TrailGrass) is valid only inside the vanilla call that set it.
        [HarmonyPatch(typeof(TerrainComp), "DoOperation")]
        private static class Patch_DoOpScope
        {
            private static void Prefix() => FatTrailFast.ClearTrailGrass();
            private static System.Exception Finalizer(System.Exception __exception) { FatTrailFast.ClearTrailGrass(); return __exception; }
        }

        [HarmonyPatch(typeof(TerrainComp), "CheckLoad")]
        private static class Patch_CheckLoadScope
        {
            private static void Prefix() => FatTrailFast.ClearTrailGrass();
            private static System.Exception Finalizer(System.Exception __exception) { FatTrailFast.ClearTrailGrass(); return __exception; }
        }

        private const float TrailWindow = 8f;   // a 16 m cell re-rolls for trails at most every 8 s (other resets: Window)
        private static int s_trailNarrowed, s_trailSkipped, s_resetCalls, s_rebuildFrames;
        private static float s_trailNarrowedArea, s_perfAt;
        private static long s_lateTicks;

        [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.ResetGrass))]
        private static class Patch_ResetGrass
        {
            private static bool Prefix(ClutterSystem __instance, ref Vector3 center, ref float radius)
            {
                if (s_inFlush) return true;
                s_resetCalls++;
                float window = Window;
                // 0.2.1346: an open river stroke, or a zone mid-swap (FatClutterRegrow regrows it at the swap): cells only, no pass. After the trail narrowing below would not matter: a stroke holds them all.
                // 0.2.1347: AllInMidSwap almost never held a chunk: a chunk's circle is its half + 1 m and a zone is 2 x 2 chunks, so one corner always reads the neighbour zone.
                // A reset CENTRED in a zone mid-swap now holds its cells; the flush below runs the cells of settled zones at once and the rest at the swap.
                bool perCell = HoldMidSwapOn;
                if (s_strokeOpen || (ZoneReveal.On && (perCell && radius <= MidSwapHoldRadius ? VoxelZoneStreamer.ZoneMidSwap(ZoneSystem.GetZone(center)) : AllInMidSwap(center, radius))))
                {
                    if (FatTrailFast.TrailGrass && FatTrailFast.TrailGrassRadius < 0f) { s_trailSkipped++; return false; }
                    if (s_strokeOpen) s_strokeCalls++;
                    else if (perCell && radius <= MidSwapHoldRadius) NoteMidSwapHold();
                    HoldCells(center, radius, window);
                    return false;
                }
                if (FatTrailFast.TrailGrass)
                {
                    // 0.2.1162: a trail-only terrain change: grass only where the trail paint changed (or none), and a longer window
                    if (FatTrailFast.TrailGrassRadius < 0f) { s_trailSkipped++; return false; }
                    if (FatTrailFast.TrailGrassRadius < radius)
                    {
                        s_trailNarrowedArea += radius * radius - FatTrailFast.TrailGrassRadius * FatTrailFast.TrailGrassRadius;
                        center = FatTrailFast.TrailGrassCenter; radius = FatTrailFast.TrailGrassRadius;
                    }
                    s_trailNarrowed++;
                    window = TrailWindow;
                }
                float now = Time.unscaledTime;
                string who = "?";
                if (FatDiag.ClutterGate)   // 0.2.1102: the StackFrame + lines only with the Clutter gate diagnostic on
                {
                    var m = new System.Diagnostics.StackFrame(2, false).GetMethod();
                    who = m != null ? (m.DeclaringType != null ? m.DeclaringType.Name : "?") + "." + m.Name : "?";
                    if (who.Contains("DoOperation") && now >= s_nextStack)
                    {
                        s_nextStack = now + 30f;
                        FiresLogger.LogDebug("[ClutterGate] DoOperation reset at (" + center.x.ToString("F0") + "," + center.z.ToString("F0")
                            + ") r " + radius.ToString("F0") + ", placed by: " + new System.Diagnostics.StackTrace(2, false).ToString().Replace("\n", " <- "));
                    }
                }

                // 0.2.1347: vanilla's ResetGrass only marks patches in m_patches; with none in the rect it does nothing, not even m_forceRebuild, and GeneratePatch rolls a patch
                // fresh from the ground of that moment when the player gets there. The same rect test as vanilla, on the live patches (a patch left behind for its 2 s timeout still counts),
                // so skipping changes no grass. After the stroke / mid-swap hold above: a patch born during a hold is covered by the flush.
                bool noPatch = false;
                if (FiresVoxelConfig.ClutterSkipNoPatch != null && FiresVoxelConfig.ClutterSkipNoPatch.Value)
                {
                    long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    try { noPatch = !TouchesPatch(__instance, center, radius); }
                    catch (System.Exception ex) { FiresVoxelConfig.ClutterSkipNoPatch.Value = false; FiresLogger.LogWarning($"[ClutterGate] the no-patch test threw, key turned off: {ex.GetType().Name} (0.2.1347)"); }
                    double gms = (System.Diagnostics.Stopwatch.GetTimestamp() - g0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    s_gateMs += gms; if (gms > s_gateWorst) s_gateWorst = gms;
                    if (noPatch)
                    {
                        s_noPatch++;
                        if (!s_noPatchProof) { s_noPatchProof = true; FiresLogger.LogInfo("[ClutterGate] a grass reset that touches no grass patch stops at once (0.2.1347)"); }
                    }
                }

                // Coalesce: run when any covered cell is outside its window; else hold the cells for one flush.
                int x0 = Mathf.FloorToInt((center.x - radius) / Cell), x1 = Mathf.FloorToInt((center.x + radius) / Cell);
                int z0 = Mathf.FloorToInt((center.z - radius) / Cell), z1 = Mathf.FloorToInt((center.z + radius) / Cell);
                bool anyDue = false;
                if (!noPatch)   // 0.2.1347
                {
                for (int x = x0; x <= x1 && !anyDue; x++)
                    for (int z = z0; z <= z1; z++)
                        if (!s_lastReset.TryGetValue(Key(x, z), out float t) || now - t >= window) { anyDue = true; break; }
                if (anyDue)
                {
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++) { long k = Key(x, z); s_lastReset[k] = now; s_held.Remove(k); s_heldAt.Remove(k); }
                    s_ran++;
                }
                else
                {
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++) { long k = Key(x, z); s_held[k] = s_held.TryGetValue(k, out float w0) ? Mathf.Min(w0, window) : window; }
                    s_skipped++;
                }
                }

                if (FatDiag.ClutterGate)
                {
                    s_calls.TryGetValue(who, out var e);
                    s_calls[who] = (e.n + 1, center, radius);
                    if (now >= s_next)
                    {
                        s_next = now + 5f;
                        int total = 0;
                        foreach (var kv in s_calls) total += kv.Value.n;
                        if (total > 20)
                        {
                            var sb = new System.Text.StringBuilder("[ClutterGate] ResetGrass ").Append(total).Append(" call(s) in 5 s (ran ")
                                .Append(s_ran).Append(", held ").Append(s_skipped).Append(", flushed cells ").Append(s_flushed).Append("): ");
                            foreach (var kv in s_calls)
                                sb.Append(kv.Key).Append(" x").Append(kv.Value.n).Append(" (last at ").Append(kv.Value.c.x.ToString("F0")).Append(',')
                                  .Append(kv.Value.c.z.ToString("F0")).Append(" r ").Append(kv.Value.r.ToString("F0")).Append("); ");
                            FiresLogger.LogDebug(sb.ToString());
                        }
                        s_calls.Clear();
                        s_ran = s_skipped = s_flushed = 0;
                    }
                }
                if (s_lastReset.Count > 20000) s_lastReset.Clear();
                return anyDue;
            }
        }

        // ---- 0.2.1346 (Fire: one stroke = one carve + one clutter pass). A river stroke fired ResetGrass from every dab, its water and its wet re-paint, so grass
        // rebuilt again and again and rolled against ground and water still arriving. While the stroke is open every call only notes its cells; it closes when the carve
        // job, the queued water, the wet re-paints and its zones' chunk swaps are done, at most StrokeCapSeconds after the carve (a stuck chunk cannot hold grass forever).
        private const float StrokeCapSeconds = 30f, StrokeCheckSeconds = 0.1f;
        private static bool s_strokeOpen, s_strokeProof;
        private static int s_strokeN, s_strokeCalls;
        private static float s_strokeOpenedAt, s_strokeCarvedAt, s_strokeCheckAt;
        private static readonly HashSet<Vector2s> s_strokeZones = new HashSet<Vector2s>();

        // 0.2.1347: mid-swap holds + the no-patch gate. The give-up is the only way a zone that never swaps could leave a cell stale.
        private const float HoldGiveUp = 20f, MidSwapHoldRadius = 48f;   // the widest local reset is a heightmap's 36 m; a world-wide one (config change) is never split into cells here
        private static readonly Dictionary<long, float> s_heldAt = new Dictionary<long, float>();
        private static bool HoldMidSwapOn => FiresVoxelConfig.ClutterHoldMidSwapZone != null && FiresVoxelConfig.ClutterHoldMidSwapZone.Value;
        private static int s_noPatch, s_midSwapCalls, s_midSwapGaveUp, s_flushWindow;
        private static double s_gateMs, s_gateWorst;
        private static bool s_noPatchProof, s_midSwapProof;

        private static void NoteMidSwapHold()
        {
            s_midSwapCalls++;
            if (!s_midSwapProof) { s_midSwapProof = true; FiresLogger.LogInfo("[ClutterGate] a grass reset centred in a zone mid-swap waits for the swap (0.2.1347)"); }
        }

        private static bool TouchesPatch(ClutterSystem cs, Vector3 center, float radius)
        {
            float half = cs.m_grassPatchSize / 2f;
            foreach (var kv in cs.m_patches)
            {
                Vector3 c = kv.Value.center;
                if (c.x + half >= center.x - radius && c.x - half <= center.x + radius && c.z + half >= center.z - radius && c.z - half <= center.z + radius) return true;
            }
            return false;
        }

        private static void HoldCells(Vector3 center, float radius, float window)
        {
            int x0 = Mathf.FloorToInt((center.x - radius) / Cell), x1 = Mathf.FloorToInt((center.x + radius) / Cell);
            int z0 = Mathf.FloorToInt((center.z - radius) / Cell), z1 = Mathf.FloorToInt((center.z + radius) / Cell);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long k = Key(x, z); s_held[k] = s_held.TryGetValue(k, out float w0) ? Mathf.Min(w0, window) : window;
                    if (!s_heldAt.ContainsKey(k)) s_heldAt[k] = Time.unscaledTime;   // 0.2.1347
                }
            s_skipped++;
        }

        private static bool AllInMidSwap(Vector3 c, float r)
            => VoxelZoneStreamer.ZoneMidSwap(ZoneSystem.GetZone(new Vector3(c.x - r, 0f, c.z - r))) && VoxelZoneStreamer.ZoneMidSwap(ZoneSystem.GetZone(new Vector3(c.x + r, 0f, c.z - r)))
            && VoxelZoneStreamer.ZoneMidSwap(ZoneSystem.GetZone(new Vector3(c.x - r, 0f, c.z + r))) && VoxelZoneStreamer.ZoneMidSwap(ZoneSystem.GetZone(new Vector3(c.x + r, 0f, c.z + r)));

        // From WaterToolDriver when a carve job starts. A second carve inside an open stroke joins it.
        internal static void StrokeBegin()
        {
            if (FiresVoxelConfig.RiverStrokeOneClutterPass == null || !FiresVoxelConfig.RiverStrokeOneClutterPass.Value) return;
            s_strokeCarvedAt = -1f;
            if (s_strokeOpen) return;
            s_strokeOpen = true; s_strokeN++; s_strokeCalls = 0;
            s_strokeOpenedAt = Time.realtimeSinceStartup; s_strokeZones.Clear();
            if (!s_strokeProof) { s_strokeProof = true; FiresLogger.LogInfo("[ClutterGate] a river stroke holds its grass resets and runs one pass when it is done (0.2.1346)"); }
        }
        internal static void StrokeZone(Vector2s zone) { if (s_strokeOpen) s_strokeZones.Add(zone); }

        private static bool TryCloseStroke()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_strokeCheckAt) return false;
            s_strokeCheckAt = now + StrokeCheckSeconds;
            string waiting = null;
            if (VerdantsAscent.Water.Tools.WaterToolDriver.StudioRiverCarving) return false;   // the carve job: never capped, it ends by its own samples or its catch
            if (s_strokeCarvedAt < 0f) s_strokeCarvedAt = now;
            // 0.2.1348: the 30 s cap sat under the water's own 45 s wait, so a slow swap could run the pass before its water. The finish (water, fill, falls, paint) has its own caps; the 30 s counts from its end.
            if (VerdantsAscent.Water.Tools.WaterShapingTools.RiverFinishPending) { s_strokeCarvedAt = now; return false; }
            if (VerdantsAscent.Water.Tools.WaterShapingTools.PendingWaterCount > 0) waiting = "water";
            else if (VoxelZoneStreamer.PendingWetCount > 0) waiting = "wet re-paint";
            else foreach (var z in s_strokeZones) if (VoxelZoneStreamer.ZoneEditBusy(z)) { waiting = $"zone ({z.x},{z.y}) chunks"; break; }
            bool capped = waiting != null && now - s_strokeCarvedAt >= StrokeCapSeconds;
            if (waiting != null && !capped) return false;
            s_strokeOpen = false;
            ClutterSystem_GenerateVegPatch_WaterEcology.ForgetPatchWater();
            foreach (var kv in s_held) s_lastReset.Remove(kv.Key);   // every held cell is due now: all in this frame's flush, one pass
            FiresLogger.LogDebug($"[ClutterGate] stroke {s_strokeN}: {s_strokeCalls} reset calls held, 1 pass ({s_held.Count} cell(s), {now - s_strokeOpenedAt:0.0} s open{(capped ? $", CAPPED {StrokeCapSeconds:0} s after the carve, still waiting on {waiting}" : "")}) (0.2.1346)");
            s_strokeStat.Note((now - s_strokeOpenedAt) * 1000.0);
            return true;
        }
        private static readonly FatSpreadStat s_strokeStat = new FatSpreadStat("river stroke open time");

        // Held cells re-roll once when their window has passed.
        [HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
        private static class Patch_Flush
        {
            private static void Postfix(ClutterSystem __instance)
            {
                if (s_strokeOpen && !TryCloseStroke()) return;   // 0.2.1346
                if (s_held.Count == 0 || __instance == null) return;
                float now = Time.unscaledTime;
                s_flush.Clear();
                foreach (var kv in s_held)
                    if (!s_lastReset.TryGetValue(kv.Key, out float t) || now - t >= kv.Value)
                    {
                        if (ZoneReveal.On && VoxelZoneStreamer.ZoneMidSwap(ZoneSystem.GetZone(new Vector3(((int)(kv.Key >> 32) + 0.5f) * Cell, 0f, ((int)(uint)kv.Key + 0.5f) * Cell))))   // 0.2.1346
                        {
                            if (!HoldMidSwapOn || !s_heldAt.TryGetValue(kv.Key, out float since) || now - since < HoldGiveUp) continue;
                            s_midSwapGaveUp++;   // 0.2.1347
                        }
                        s_flush.Add(kv.Key);
                    }
                if (s_flush.Count == 0) return;
                s_inFlush = true;
                try
                {
                    foreach (long k in s_flush)
                    {
                        s_held.Remove(k); s_heldAt.Remove(k);   // 0.2.1347
                        s_flushWindow++;
                        s_lastReset[k] = now;
                        int x = (int)(k >> 32), z = (int)(uint)k;
                        __instance.ResetGrass(new Vector3((x + 0.5f) * Cell, 0f, (z + 0.5f) * Cell), Cell * 0.75f);
                        s_flushed++;
                    }
                }
                finally { s_inFlush = false; }
            }
        }

        // 0.2.1162 [FatPerf] clutter (Debug, every 10 s): the whole ClutterSystem.LateUpdate (vanilla + every mod's hooks) in ms/s,
        // the frames that ran a forced grass rebuild, and the ResetGrass calls with the trail narrowing.
        private static readonly AccessTools.FieldRef<ClutterSystem, bool> ForceRebuild = AccessTools.FieldRefAccess<ClutterSystem, bool>("m_forceRebuild");
        private static long s_lateT0;

        [HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
        private static class Patch_LateTimer
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ClutterSystem __instance)
            {
                if (!FiresLogger.DebugEnabled) return;
                s_lateT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (__instance != null && ForceRebuild(__instance)) s_rebuildFrames++;
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix()
            {
                if (!FiresLogger.DebugEnabled || s_lateT0 == 0) return;
                s_lateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - s_lateT0;
                s_lateT0 = 0;
                float now = Time.unscaledTime;
                if (s_perfAt == 0f) { s_perfAt = now + 10f; return; }
                if (now < s_perfAt) return;
                float span = 10f + (now - s_perfAt);
                s_perfAt = now + 10f;
                double ms = s_lateTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                FiresLogger.LogDebug($"[FatPerf] clutter: LateUpdate (vanilla + all hooks) {ms / span:0.0} ms/s; forced grass rebuild frames {s_rebuildFrames / span:0.0}/s; "
                    + $"ResetGrass calls {s_resetCalls / span:0.0}/s, trail-only narrowed {s_trailNarrowed} (area cut {Mathf.PI * s_trailNarrowedArea:0} m2), trail no-change skipped {s_trailSkipped}, "
                    + $"held cells {s_held.Count} (0.2.1162: trail cells re-roll at most every {TrailWindow:0} s)");
                FiresLogger.LogDebug($"[ClutterGate] last 10 s: colliders fired {FatClutterRegrow.GroundFired}, same ground skipped {FatClutterRegrow.SameGroundSkipped}; no patch skipped {s_noPatch} (gate total {s_gateMs:0.00} ms, worst {s_gateWorst:0.000} ms); mid-swap held {s_midSwapCalls} call(s), {s_midSwapGaveUp} cell(s) gave up, cells flushed {s_flushWindow}; wet stamps partial held {Patch_Heightmap_WetMask.GrassPartialHeld}, same band skipped {Patch_Heightmap_WetMask.GrassSameBand}, rolled {Patch_Heightmap_WetMask.GrassRolled} (0.2.1347)");
                FatClutterRegrow.GroundFired = FatClutterRegrow.SameGroundSkipped = 0; s_noPatch = s_midSwapCalls = s_midSwapGaveUp = s_flushWindow = 0; s_gateMs = s_gateWorst = 0;
                Patch_Heightmap_WetMask.GrassPartialHeld = Patch_Heightmap_WetMask.GrassSameBand = Patch_Heightmap_WetMask.GrassRolled = 0;
                s_lateTicks = 0; s_rebuildFrames = 0; s_resetCalls = 0; s_trailNarrowed = 0; s_trailSkipped = 0; s_trailNarrowedArea = 0f;
            }
        }

        internal static void Clear() { s_lastReset.Clear(); s_held.Clear(); s_heldAt.Clear(); s_strokeOpen = false; s_strokeZones.Clear(); }
    }
}
