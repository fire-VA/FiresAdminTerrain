using System.Collections.Generic;
using System.Diagnostics;

namespace VerdantsAscent
{
    // How long each step of a sequence took, for one log line ("flow field 12 ms, lava field 410 ms, ..."). The Fires
    // generator times its world init with it ([perf] 2026-09-28: after the bake preload, R29's join still spent 1.7 s there).
    internal sealed class StepTimes
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<string> _laps = new List<string>();
        private long _lastMs;

        internal void Lap(string step)
        {
            long now = _clock.ElapsedMilliseconds;
            _laps.Add($"{step} {now - _lastMs:N0} ms");
            _lastMs = now;
        }

        public override string ToString() => string.Join(", ", _laps.ToArray());
    }
}
