using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1188 (the lead, Fire: "our performance is still spotty"; live 1187 [ZoneUpdate] "whole call 22.7 ms/s ... BiomePaint 19.88;
    // BiomeDeepCells 19.82", tint "restarted by a rebuild 26, running 31"): FAT's background work under ZoneSystem.Update each kept
    // its own per-frame allowance (tint 2 ms, deep-cell pump up to 2 ms + 20 ms/s, wet-mask band rows 2 ms), so at 60+ fps they
    // added up to tens of ms a second. ONE shared allowance now: at most FrameMs a frame and SecondMs a second across all of them.
    // A caller asks LeftMs() before a unit and reports what it spent with Spend(); a unit already running finishes (worst unit logged).
    internal static class FatBudget
    {
        internal const double FrameMs = 1.0, SecondMs = 15.0;
        internal enum Who { Tint, DeepPump, WetMask, Refill, Spread, Count }   // 0.2.1319: Spread = the frame-spread ops (distant paint compose, tile mesh render)
        private static int s_frame = -1;
        private static double s_frameSpent, s_secSpent, s_secBucketAt;
        private static readonly double[] s_ms = new double[(int)Who.Count];
        private static int s_framesOver;
        private static double s_worstUnit; private static Who s_worstWho;
        private static float s_logAt;

        internal static double LeftMs()
        {
            Roll();
            double f = FrameMs - s_frameSpent, s = SecondMs - s_secSpent;
            return f < s ? f : s;
        }

        internal static void Spend(Who who, double ms)
        {
            Roll();
            s_frameSpent += ms; s_secSpent += ms; s_ms[(int)who] += ms;
            if (ms > s_worstUnit) { s_worstUnit = ms; s_worstWho = who; }
            Log();
        }

        private static void Roll()
        {
            if (Time.frameCount != s_frame)
            {
                if (s_frameSpent > FrameMs) s_framesOver++;
                s_frame = Time.frameCount; s_frameSpent = 0;
            }
            double now = Time.unscaledTimeAsDouble;
            if (now - s_secBucketAt >= 1.0) { s_secBucketAt = now; s_secSpent = 0; }
        }

        private static void Log()
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_logAt) return;
            if (s_logAt == 0f) { s_logAt = Time.unscaledTime + 10f; return; }
            s_logAt = Time.unscaledTime + 10f;
            double total = 0; for (int i = 0; i < s_ms.Length; i++) total += s_ms[i];
            FiresLogger.LogDebug($"[FatBudget] 10 s: tint {s_ms[(int)Who.Tint]:0.0} ms, deep-cell pump {s_ms[(int)Who.DeepPump]:0.0} ms, wetmask {s_ms[(int)Who.WetMask]:0.0} ms, trail refill {s_ms[(int)Who.Refill]:0.0} ms, spread ops {s_ms[(int)Who.Spread]:0.0} ms (0.2.1319), total {total / 10.0:0.0} ms/s (cap {FrameMs} ms a frame, {SecondMs} ms a second), frames over {s_framesOver}, worst unit {s_worstUnit:0.0} ms ({s_worstWho}) (0.2.1188)");
            for (int i = 0; i < s_ms.Length; i++) s_ms[i] = 0;
            s_framesOver = 0; s_worstUnit = 0;
        }
    }
}

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1319 (PLAN_fat_frame_spread_20261031 item 2): one Debug line per spread op every 10 s - worst ms in one frame, total ms,
    // frames that ran a slice - so each op's A/B key (OFF = the old one-frame path) can be compared on the same route.
    internal sealed class FatSpreadStat
    {
        private readonly string _name;
        private int _frame = -1, _frames, _units;
        private double _frameMs, _worstFrame, _total;
        private float _logAt;
        internal FatSpreadStat(string name) { _name = name; }

        internal void Note(double ms)
        {
            int f = Time.frameCount;
            if (f != _frame) { _frame = f; _frameMs = 0; _frames++; }
            _frameMs += ms; _total += ms; _units++;
            if (_frameMs > _worstFrame) _worstFrame = _frameMs;
            Log();
        }

        private void Log()
        {
            float now = Time.unscaledTime;
            if (now < _logAt) return;
            if (_logAt == 0f) { _logAt = now + 10f; return; }
            _logAt = now + 10f;
            if (FiresLogger.DebugEnabled)
                FiresLogger.LogDebug($"[FatSpread] {_name} 10 s: worst {_worstFrame:0.00} ms in one frame / total {_total:0.0} ms / {_frames} frame(s), {_units} unit(s) (0.2.1319)");
            _frames = _units = 0; _worstFrame = _total = 0;
        }
    }
}
