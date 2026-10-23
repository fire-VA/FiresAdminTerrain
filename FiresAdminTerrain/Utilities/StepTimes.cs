using System.Collections.Generic;
using System.Diagnostics;

namespace VerdantsAscent
{
    // How long each step of a sequence took, for one log line ("flow field 12 ms, lava field 410 ms, ..."). The Fires
    // generator times its world init with it (after the bake preload, a join still spent 1.7 s there).
    // With TrackHeap (a memory census once found Mono's heap at 2,752 MB, 1,749 MB used, ~1 GB held from a load-time peak),
    // each lap also records the managed bytes in use (GC.GetTotalMemory) and Mono's reserved heap, so the step that sets
    // the peak shows in the same line. Measurement only.
    internal sealed class StepTimes
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<string> _laps = new List<string>();
        private long _lastMs;
        internal bool TrackHeap;
        private long _peakUsed;
        private string _peakStep;

        internal void Lap(string step)
        {
            long now = _clock.ElapsedMilliseconds;
            if (TrackHeap)
            {
                long used = System.GC.GetTotalMemory(false);
                long heap;
                try { heap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong(); } catch { heap = -1048576L; }   // Unity API off the main thread: -1
                if (used > _peakUsed) { _peakUsed = used; _peakStep = step; }
                _laps.Add($"{step} {now - _lastMs:N0} ms [used {used / 1048576L:N0} / heap {heap / 1048576L:N0} MB]");
            }
            else _laps.Add($"{step} {now - _lastMs:N0} ms");
            _lastMs = now;
        }

        // "peak used N MB after <step>", or "" without TrackHeap.
        internal string PeakSummary => TrackHeap && _peakStep != null ? $"peak used {_peakUsed / 1048576L:N0} MB after '{_peakStep}'" : "";

        public override string ToString() => string.Join(", ", _laps.ToArray());
    }
}
