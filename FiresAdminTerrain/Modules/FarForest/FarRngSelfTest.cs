using System;
using System.Diagnostics;
using UnityEngine;

namespace VerdantsAscent.Terrain.FarForest
{
    // 0.2.1412 (FarLands #2, spec §2.5): proves FarRng against UnityEngine.Random in THIS process before the worker replay may use it.
    // 2,000 seeds x 64 draws each of value, Range(float), Range(int) and insideUnitCircle; plus the seeded state itself and the state
    // after each run (so the draws per call are proven too). Each float/circle formula is picked from FarRng's candidates: the one with
    // zero misses. UnityEngine.Random's state is saved and restored around the test. Runs once, only when a Far Forest key needs it.
    internal static class FarRngSelfTest
    {
        internal static bool Ran { get; private set; }
        internal static bool Trusted { get; private set; }
        internal static string Summary { get; private set; } = "not run";

        private const int Seeds = 2000, Draws = 64;

        internal static unsafe void ReadState(out int s0, out int s1, out int s2, out int s3)
        {
            UnityEngine.Random.State st = UnityEngine.Random.state;
            int* p = (int*)&st;
            s0 = p[0]; s1 = p[1]; s2 = p[2]; s3 = p[3];
        }

        internal static void Run()
        {
            if (Ran) return;
            Ran = true;
            var saved = UnityEngine.Random.state;
            var sw = Stopwatch.StartNew();
            int[] seeds = new int[Seeds];
            try
            {
                var rnd = new System.Random(20261108);
                for (int i = 0; i < Seeds; i++) seeds[i] = i < 8 ? new[] { 0, 1, -1, int.MaxValue, int.MinValue, 42, 4271, 9187 }[i] : rnd.Next(int.MinValue, int.MaxValue);

                int seedMiss = 0;
                foreach (int s in seeds)
                {
                    UnityEngine.Random.InitState(s);
                    ReadState(out int a, out int b, out int c, out int d);
                    if (!FarRng.FromSeed(s).SameState(a, b, c, d)) seedMiss++;
                }

                int valueMode = Pick(new[] { 1, 2 }, (m, s) => SeqValue(s, m));
                FarRng.ValueMode = valueMode;
                int rangeFMode = Pick(new[] { 1, 2 }, (m, s) => SeqRangeF(s, m));
                FarRng.RangeFloatMode = rangeFMode;
                int rangeIMiss = 0;
                foreach (int s in seeds) if (!SeqRangeI(s)) rangeIMiss++;
                int circleMode = Pick(new[] { 1, 2 }, (m, s) => SeqCircle(s, m));   // 0.2.1417: tested on its own (1415 skipped it when Range(float) failed)
                FarRng.CircleMode = circleMode;

                Trusted = seedMiss == 0 && valueMode != 0 && rangeFMode != 0 && rangeIMiss == 0 && circleMode != 0;
                Summary = (s_firstMiss != null && rangeFMode == 0 ? $"first Range(float) miss: {s_firstMiss}; " : "") + $"seed state {(seedMiss == 0 ? "OK" : seedMiss + " miss")}, value mode {Mode(valueMode)}, Range(float) mode {Mode(rangeFMode)}, " +
                          $"Range(int) {(rangeIMiss == 0 ? "OK" : rangeIMiss + " seed miss")}, insideUnitCircle mode {Mode(circleMode)}; {Seeds} seeds x {Draws} draws, {sw.Elapsed.TotalMilliseconds:0} ms";
            }
            catch (Exception e)
            {
                Trusted = false;
                Summary = "threw: " + e.Message;
            }
            finally { UnityEngine.Random.state = saved; }

            if (Trusted) FiresLogger.LogInfo($"[FarForest] RNG self-test PASSED: worker replay allowed ({Summary}).");
            else
            {
                FiresLogger.LogInfo($"[FarForest] RNG self-test FAILED: the replay uses the main-thread path with UnityEngine.Random (exact, slower). Why: {Summary}.");
                try { FiresLogger.LogInfo("[FarForest] RNG self-test detail (seed 42): " + Detail(42)); } catch { }
                finally { UnityEngine.Random.state = saved; }
            }

            string Mode(int m) => m == 0 ? "NONE matched" : m.ToString();

            int Pick(int[] modes, Func<int, int, bool> seq)
            {
                foreach (int m in modes)
                {
                    bool all = true;
                    foreach (int s in seeds) if (!seq(m, s)) { all = false; break; }
                    if (all) return m;
                }
                return 0;
            }
        }

        // Raw facts for a failed test, so the next build can be fixed from the log: Unity's state after InitState and after each draw,
        // its value / Range(0,1) / Range(0,2pi) / insideUnitCircle, and our raw words from the same seed.
        private static string Detail(int seed)
        {
            var sb = new System.Text.StringBuilder();
            UnityEngine.Random.InitState(seed);
            ReadState(out int a, out int b, out int c, out int d);
            var r = FarRng.FromSeed(seed);
            sb.Append($"unity init {a:X8} {b:X8} {c:X8} {d:X8} / ours {r.X:X8} {r.Y:X8} {r.Z:X8} {r.W:X8}; ");
            sb.Append($"unity value {UnityEngine.Random.value:R} {UnityEngine.Random.value:R}, Range(0,1) {UnityEngine.Random.Range(0f, 1f):R}, Range(2,10) {UnityEngine.Random.Range(2f, 10f):R}; ");
            Vector2 u = UnityEngine.Random.insideUnitCircle;
            ReadState(out a, out b, out c, out d);
            sb.Append($"circle {u.x:R},{u.y:R} then state {a:X8} {b:X8} {c:X8} {d:X8}; ours words");
            for (int i = 0; i < 7; i++) sb.Append(' ').Append(r.NextU32().ToString("X8"));
            return sb.ToString();
        }

        private static string s_firstMiss;

        private static bool EndStateMatches(FarRng r)
        {
            ReadState(out int a, out int b, out int c, out int d);
            return r.SameState(a, b, c, d);
        }

        private static bool SeqValue(int seed, int mode)
        {
            UnityEngine.Random.InitState(seed);
            var r = FarRng.FromSeed(seed);
            for (int i = 0; i < Draws; i++) if (UnityEngine.Random.value != r.Value01(mode)) return false;
            return EndStateMatches(r);
        }

        private static void Bounds(int i, out float min, out float max)
        {
            min = -((i % 7) * 3.3f) - (i % 3) * 0.125f;
            max = min + i * 1.7f + 0.5f;
            if ((i & 7) == 5) { float t = min; min = max; max = t; }   // reversed bounds too (vanilla passes -m_randTilt .. m_randTilt, sometimes 0..0)
            if ((i & 15) == 9) max = min;
        }

        private static bool SeqRangeF(int seed, int mode)
        {
            UnityEngine.Random.InitState(seed);
            var r = FarRng.FromSeed(seed);
            for (int i = 0; i < Draws; i++)
            {
                Bounds(i, out float min, out float max);
                float u = UnityEngine.Random.Range(min, max), o = r.RangeF(min, max, mode);
                if (u != o) { if (s_firstMiss == null) s_firstMiss = $"Range({min:R}, {max:R}) seed {seed} draw {i}: unity {u:R}, ours (mode {mode}) {o:R}"; return false; }
            }
            return EndStateMatches(r);
        }

        private static bool SeqRangeI(int seed)
        {
            UnityEngine.Random.InitState(seed);
            var r = FarRng.FromSeed(seed);
            for (int i = 0; i < Draws; i++)
            {
                int min = (i % 5) - 2, max = min + 1 + i * 3;
                if ((i & 7) == 5) { int t = min; min = max; max = t; }
                if ((i & 15) == 9) max = min;
                if (i == 11) { min = 0; max = 360; }   // vanilla's yaw draw
                if (UnityEngine.Random.Range(min, max) != r.RangeI(min, max)) return false;
            }
            return EndStateMatches(r);
        }

        private static bool SeqCircle(int seed, int mode)
        {
            UnityEngine.Random.InitState(seed);
            var r = FarRng.FromSeed(seed);
            for (int i = 0; i < Draws; i++)
            {
                Vector2 u = UnityEngine.Random.insideUnitCircle, o = r.InsideCircle(mode);
                if (Mathf.Abs(u.x - o.x) > 2e-6f || Mathf.Abs(u.y - o.y) > 2e-6f) return false;   // sin/cos may differ by an ulp; 2e-6 of a 10 m radius = 0.02 mm
            }
            return EndStateMatches(r);
        }
    }
}
