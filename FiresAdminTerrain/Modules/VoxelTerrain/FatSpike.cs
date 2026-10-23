using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Running was smooth but bursts still made 300 ms frames (Mono heap
    // 2782/2840 MB, a GC every ~1.7 s). Spike + allocation attribution for FAT's main-thread stages:
    //  * each stage adds its time and the managed bytes it allocated to this frame's buckets;
    //  * when a frame takes longer than SpikeMs, the next frame logs (Debug) that frame's stages, its GCs and the time FAT did NOT
    //    account for (another mod, rendering, the GC itself) - at most one line every 2 s;
    //  * every 10 s (Debug) a summary: what FAT allocated per stage, Mono heap used / reserved, and GC count.
    // Main thread only.
    internal static class FatSpike
    {
        internal enum Stage { MeshApply, ColliderAssign, RecordApply, WaterSheet, IceCollider, RemeshFlush, Count }

        internal struct Tok { internal long T, M; }

        private const float SpikeMs = 100f, SummaryEvery = 60f;   // Summary once a minute
        private static readonly long[] s_cur = new long[(int)Stage.Count], s_prev = new long[(int)Stage.Count];
        private static readonly int[] s_curN = new int[(int)Stage.Count], s_prevN = new int[(int)Stage.Count];
        private static readonly long[] s_alloc = new long[(int)Stage.Count];
        private static int s_frame = -1, s_gc0, s_gcPrev, s_gcSummary0 = -1;
        private static float s_nextLog, s_nextSummary;

        internal static Tok Start() => new Tok { T = Stopwatch.GetTimestamp(), M = System.GC.GetTotalMemory(false) };

        internal static void Add(Stage s, Tok t)
        {
            Roll();
            s_cur[(int)s] += Stopwatch.GetTimestamp() - t.T;
            s_curN[(int)s]++;
            long d = System.GC.GetTotalMemory(false) - t.M;
            if (d > 0) s_alloc[(int)s] += d;   // a GC inside the stage makes the delta negative: dropped, never counted
        }

        // Called at the top of the pipeline driver's Update: closes the last frame, reports it if it was a spike, and the 10 s summary.
        internal static void FrameStart()
        {
            Roll();
            if (!FiresLogger.DebugEnabled) return;
            float ms = Time.unscaledDeltaTime * 1000f;
            if (ms >= SpikeMs && Time.unscaledTime >= s_nextLog)
            {
                s_nextLog = Time.unscaledTime + 10f;
                double total = 0;
                var sb = new StringBuilder();
                sb.Append("[FatSpike] frame ").Append(ms.ToString("0")).Append(" ms: ");
                for (int i = 0; i < (int)Stage.Count; i++)
                {
                    if (s_prevN[i] == 0) continue;
                    double t = s_prev[i] * 1000.0 / Stopwatch.Frequency;
                    total += t;
                    sb.Append(((Stage)i).ToString()).Append(' ').Append(t.ToString("0.0")).Append(" ms x").Append(s_prevN[i]).Append("; ");
                }
                sb.Append("FAT total ").Append(total.ToString("0.0")).Append(" ms, NOT FAT ").Append((ms - total).ToString("0"))
                  .Append(" ms, GC gen0 in that frame ").Append(s_gcPrev);
                FiresLogger.LogDebug(sb.ToString());
            }
            if (Time.unscaledTime >= s_nextSummary)
            {
                s_nextSummary = Time.unscaledTime + SummaryEvery;
                int gc = System.GC.CollectionCount(0);
                if (s_gcSummary0 >= 0)
                {
                    var sb = new StringBuilder("[FatSpike] last 60 s: FAT allocated ");
                    long all = 0;
                    for (int i = 0; i < (int)Stage.Count; i++) all += s_alloc[i];
                    sb.Append((all / 1048576.0).ToString("0.0")).Append(" MB (");
                    for (int i = 0; i < (int)Stage.Count; i++)
                        if (s_alloc[i] > 0) sb.Append(((Stage)i).ToString()).Append(' ').Append((s_alloc[i] / 1048576.0).ToString("0.0")).Append(" MB; ");
                    sb.Append("); Mono heap used ").Append((UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576).ToString())
                      .Append(" / reserved ").Append((UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576).ToString())
                      .Append(" MB; GCs ").Append(gc - s_gcSummary0);
                    FiresLogger.LogDebug(sb.ToString());
                }
                System.Array.Clear(s_alloc, 0, s_alloc.Length);
                s_gcSummary0 = gc;
            }
        }

        private static void Roll()
        {
            int f = Time.frameCount;
            if (f == s_frame) return;
            System.Array.Copy(s_cur, s_prev, s_cur.Length);
            System.Array.Copy(s_curN, s_prevN, s_curN.Length);
            System.Array.Clear(s_cur, 0, s_cur.Length);
            System.Array.Clear(s_curN, 0, s_curN.Length);
            int gc = System.GC.CollectionCount(0);
            s_gcPrev = s_frame < 0 ? 0 : gc - s_gc0;
            s_gc0 = gc;
            s_frame = f;
        }
    }
}
