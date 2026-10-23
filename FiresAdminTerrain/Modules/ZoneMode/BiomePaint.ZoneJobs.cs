using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // A 1476-zone dry run in one frame held the server's main thread 33.5 s and dropped the client, so
    // ReplaceZoneLocations and RegenerateZones run as ONE time-sliced job at a time, stepped from BiomePaint.Tick on the server for
    // at most JobSliceMs per frame; the caller's done(counts, report) runs on the main thread when it ends, and a progress line is
    // logged every JobProgressSeconds. Planning never touches the world's location list (picks and removals live in s_planExtra /
    // s_planExclude while a pick is tested); only the apply phase changes the world, one zone per step.
    internal static partial class BiomePaint
    {
        internal sealed class ZoneJobResult
        {
            internal int[] Counts;
            internal string Report = string.Empty;
            internal string Phase = string.Empty;
            internal int Step, Steps;
        }

        private const double JobSliceMs = 4.0;
        private const float JobProgressSeconds = 3f;
        private const int PlanTriesPerType = 160;   // Fits tests per location type per run

        private static IEnumerator s_job;
        private static string s_jobName;
        private static ZoneJobResult s_jobResult;
        private static Action<int[], string> s_jobDone;
        private static float s_jobProgressAt, s_jobStartedAt;
        private static int s_jobFrames;
        private static double s_jobLongestMs;
        private static readonly System.Diagnostics.Stopwatch s_jobSw = new System.Diagnostics.Stopwatch();

        private static HashSet<Vector2s> s_planExclude;
        private static List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>> s_planExtra;
        private static Dictionary<string, int> s_planTries;

        internal static bool JobRunning => s_job != null;
        internal static string JobName => s_jobName;

        internal static bool StartJob(string name, IEnumerator job, ZoneJobResult result, Action<int[], string> done, out string why)
        {
            why = string.Empty;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { why = "not the server"; return false; }
            if (s_job != null) { why = $"FAT is still running {s_jobName} ({s_jobResult.Phase} {s_jobResult.Step}/{s_jobResult.Steps}); try again when it ends"; return false; }
            s_job = Flatten(job); s_jobName = name; s_jobResult = result; s_jobDone = done;
            s_jobProgressAt = Time.realtimeSinceStartup + JobProgressSeconds;
            s_jobStartedAt = Time.realtimeSinceStartup;
            s_jobFrames = 0; s_jobLongestMs = 0;
            FiresLogger.LogInfo($"[BiomePaint] {name} started (time-sliced, {JobSliceMs} ms per frame).");
            return true;
        }

        // Blocking run, for the old synchronous API calls (kept for compatibility).
        internal static void RunToEnd(IEnumerator job)
        {
            try { var steps = Flatten(job); while (steps.MoveNext()) { } }
            finally { ClearPlan(); }
        }

        // From Tick, on the server.
        private static void RunJob()
        {
            if (s_job == null) return;
            s_jobSw.Restart();
            s_jobFrames++;
            bool more = true;
            try
            {
                while (more && s_jobSw.Elapsed.TotalMilliseconds < JobSliceMs) more = s_job.MoveNext();
            }
            catch (Exception ex)
            {
                more = false;
                s_jobResult.Counts = null;
                s_jobResult.Report = $"{s_jobName} failed: {ex.Message}";
                FiresLogger.LogWarning($"[BiomePaint] {s_jobResult.Report}");
            }
            finally { ClearPlan(); }
            double ms = s_jobSw.Elapsed.TotalMilliseconds;
            if (ms > s_jobLongestMs) s_jobLongestMs = ms;
            if (more)
            {
                if (Time.realtimeSinceStartup >= s_jobProgressAt)
                {
                    s_jobProgressAt = Time.realtimeSinceStartup + JobProgressSeconds;
                    FiresLogger.LogInfo($"[BiomePaint] {s_jobName}: {s_jobResult.Phase} {s_jobResult.Step}/{s_jobResult.Steps} "
                        + $"({Time.realtimeSinceStartup - s_jobStartedAt:0} s, longest slice {s_jobLongestMs:0.0} ms).");
                }
                return;
            }
            var done = s_jobDone;
            var result = s_jobResult;
            string name = s_jobName;
            FiresLogger.LogInfo($"[BiomePaint] {name} finished in {Time.realtimeSinceStartup - s_jobStartedAt:0.0} s over {s_jobFrames} frame(s), longest slice {s_jobLongestMs:0.0} ms.");
            s_job = null; s_jobName = null; s_jobResult = null; s_jobDone = null;
            try { done?.Invoke(result.Counts, result.Report); }
            catch (Exception ex) { FiresLogger.LogWarning($"[BiomePaint] {name}: the caller's done callback threw: {ex.Message}"); }
        }

        // A job may yield a sub-job (an IEnumerator); this steps into it, so the job loop and the blocking run both see one flat
        // sequence of steps.
        private static IEnumerator Flatten(IEnumerator job)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(job);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                if (top.Current is IEnumerator inner) { stack.Push(inner); continue; }
                yield return top.Current;
            }
        }
        private static void ClearPlan() { s_planExclude = null; s_planExtra = null; s_planTries = null; }

        private static void AbortJob(string why)
        {
            if (s_job == null) return;
            var done = s_jobDone; var result = s_jobResult; string name = s_jobName;
            s_job = null; s_jobName = null; s_jobResult = null; s_jobDone = null;
            ClearPlan();
            FiresLogger.LogWarning($"[BiomePaint] {name} stopped: {why}.");
            try { done?.Invoke(null, $"{name} stopped: {why}"); } catch (Exception) { }
        }
    }
}
