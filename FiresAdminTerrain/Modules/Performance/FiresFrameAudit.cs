using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Terrain.Performance
{
    // ── FRAME AUDIT — who is actually eating the main thread ─────────────
    //
    // The voxel pipeline was instrumented first and measured IDLE (applies
    // ~1.7/s at 1.0ms each, queues empty, buffer pool balanced) while fps sat
    // at 17-24 — so the frame cost is elsewhere. The next suspect at scale is
    // vanilla LiquidVolume under auto-pour (assembly_valheim/LiquidVolume.cs):
    //
    //   * Update:254 → UpdateHeights:356 once per second PER VOLUME:
    //     (m_width+1)² = 4,225 jobified raycasts whose ScheduleBatch(..)
    //     .Complete() blocks the MAIN thread. 44 volumes ≈ 186k raycasts/s,
    //     arriving as millisecond spikes wherever volume timers line up.
    //   * Awake:90 starts one dedicated OS thread per volume (Sleep(1) spin).
    //   * NONE of it stops at freeze — frozen holds the SIM, but Update keeps
    //     paying for raycasts, effects and save timers forever.
    //
    // Two jobs in this file: MEASURE it (the [FiresFpsAudit] ledger, every
    // 10s), and remove the frozen share of it (idle skip, config-gated). If
    // the ledger shows LiquidVolume cheap and fps still low, the cost is
    // RENDER-side — grass distance, shadow pass — and the ledger proves that
    // too, instead of another guess.
    internal static class FiresFrameAudit
    {
        private const float REPORT_EVERY = 10f;
        private static readonly double s_ticksToMs = 1000.0 / Stopwatch.Frequency;

        private static long s_lvTicks, s_uhTicks, s_pourTicks;
        private static int s_uhRuns, s_skipped;
        private static int s_frame = -1, s_frames, s_volsThisFrame, s_volsMax, s_frozenThisFrame, s_frozenMax;
        private static float s_dtSum, s_dtMax, s_nextReport;
        private static int s_gcBase;

        internal static void AddPour(long ticks) { s_pourTicks += ticks; }

        private static void OnVolumeUpdate(bool frozen)
        {
            if (Time.frameCount != s_frame)
            {
                s_frame = Time.frameCount;
                s_frames++;
                if (s_volsThisFrame > s_volsMax) s_volsMax = s_volsThisFrame;
                if (s_frozenThisFrame > s_frozenMax) s_frozenMax = s_frozenThisFrame;
                s_volsThisFrame = 0;
                s_frozenThisFrame = 0;
                float dt = Time.unscaledDeltaTime;
                s_dtSum += dt;
                if (dt > s_dtMax) s_dtMax = dt;
                Report();
            }
            s_volsThisFrame++;
            if (frozen) s_frozenThisFrame++;
        }

        private static void Report()
        {
            if (s_nextReport == 0f)
            {
                s_nextReport = Time.time + REPORT_EVERY;
                s_gcBase = System.GC.CollectionCount(0);
                return;
            }
            if (Time.time < s_nextReport || s_frames == 0) return;

            float avgDt = s_dtSum / s_frames;
            double lvMs = s_lvTicks * s_ticksToMs;
            double uhMs = s_uhTicks * s_ticksToMs;
            double pourMs = s_pourTicks * s_ticksToMs;
            int gc = System.GC.CollectionCount(0) - s_gcBase;
            int threads = 0;
            try { threads = Process.GetCurrentProcess().Threads.Count; } catch { }

            FiresLogger.LogInfo(
                $"[FiresFpsAudit] frame avg {avgDt * 1000f:0.0}ms (max {s_dtMax * 1000f:0}ms, ~{1f / Mathf.Max(avgDt, 0.0001f):0}fps) over {s_frames} frame(s) " +
                $"| LiquidVolume.Update {lvMs / s_frames:0.00}ms/frame across {s_volsMax} vol(s), {s_frozenMax} frozen, {(float)s_skipped / s_frames:0.0} skipped/frame " +
                $"| UpdateHeights {s_uhRuns} burst(s) x {(s_uhRuns > 0 ? uhMs / s_uhRuns : 0):0.00}ms = {uhMs / REPORT_EVERY:0.00}ms/s of main thread " +
                $"| pour scan {pourMs / s_frames:0.000}ms/frame | GC0 +{gc} | process threads {threads}");

            s_lvTicks = 0; s_uhTicks = 0; s_pourTicks = 0;
            s_uhRuns = 0; s_skipped = 0;
            s_frames = 0; s_dtSum = 0f; s_dtMax = 0f; s_volsMax = 0; s_frozenMax = 0;
            s_nextReport = Time.time + REPORT_EVERY;
            s_gcBase = System.GC.CollectionCount(0);
        }

        // ── LiquidVolume.Update: time every call, IDLE the frozen ones ───
        //
        // A frozen volume's shape is final and force-saved at freeze, so its
        // per-second raycast burst is pure waste. Skip vanilla Update while
        // frozen, but let ONE full pass through every ~2s per volume:
        // CheckLoad still sees ZDO changes (a peer unfreezing or merging),
        // saves still flush, and the height timer still creeps forward so
        // terraforming under a frozen river corrects eventually rather than
        // never. Settling volumes are untouched — they need every tick.
        [HarmonyPatch(typeof(LiquidVolume), "Update")]
        private static class LiquidVolume_Update_AuditIdle
        {
            private static readonly Dictionary<int, float> s_nextFull = new Dictionary<int, float>();

            private static bool Prefix(LiquidVolume __instance, out long __state)
            {
                __state = Stopwatch.GetTimestamp();
                var rc = __instance.GetComponentInParent<FiresLiquidRiverController>();
                bool frozen = rc != null && rc.m_frozen;
                OnVolumeUpdate(frozen);
                if (!frozen) return true;
                if (FiresVoxelConfig.FrozenWaterIdle != null && !FiresVoxelConfig.FrozenWaterIdle.Value) return true;

                int id = __instance.GetInstanceID();
                float now = Time.time;
                if (!s_nextFull.TryGetValue(id, out float next) || now >= next)
                {
                    s_nextFull[id] = now + 2f;
                    return true;
                }
                s_skipped++;
                return false;
            }

            private static void Postfix(long __state)
            {
                s_lvTicks += Stopwatch.GetTimestamp() - __state;
            }
        }

        // FAT's one LiquidVolume.UpdateHeights hook (FatVanillaHooks) takes the start timestamp in its prefix and
        // hands it here from its postfix, after the voxel surface pass and before the clamp guard, as before.
        internal static void CountUpdateHeights(long start) { s_uhTicks += Stopwatch.GetTimestamp() - start; s_uhRuns++; }
    }
}
