using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1322 ([lead] 10-31, PLAN_fat_driver_stalls_20261031 step 1, PROOF ONLY - no behaviour change): per-phase timers for the
    // driver units that land in the >= 60 ms stall frames (view create, refine start, paint finish, snow-depth step). Each unit
    // passes its phase timestamps (Stopwatch ticks, n phases = n + 1 stamps) and the GC gen-0 collection count delta across it.
    // Debug every 10 s: count, avg / worst total, per-phase avg / worst, units with a GC inside, the worst unit's split; a unit over
    // its flag threshold logs its split at Debug (at most one a second). Info once per timer when it first runs.
    internal sealed class StallPhaseTimer
    {
        private static readonly double Ms = 1000.0 / Stopwatch.Frequency;
        private readonly string _tag;
        private readonly string[] _names;
        private readonly double[] _sum, _worst;
        private readonly double _flagMs;
        private double _total, _totalWorst;
        private int _n, _gcUnits, _flagged;
        private string _worstSplit = "-";
        private float _reportAt, _flagAt;
        private bool _infoSaid;
        internal readonly long[] T;

        internal StallPhaseTimer(string tag, double flagMs, params string[] names)
        {
            _tag = tag; _names = names; _flagMs = flagMs;
            _sum = new double[names.Length]; _worst = new double[names.Length];
            T = new long[names.Length + 1];
        }

        internal static long Now() => Stopwatch.GetTimestamp();
        internal static int Gc() => System.GC.CollectionCount(0);

        // T[0..names.Length] filled by the caller; gcDelta = GC.CollectionCount(0) after - before; what = the unit (chunk key, zone).
        internal void Note(int gcDelta, string what)
        {
            if (!_infoSaid) { _infoSaid = true; FiresLogger.LogInfo($"[FatStallProof] {_tag} phase timers running ({_names.Length} phases, 10 s Debug summary, flag > {_flagMs:0} ms) (0.2.1322)"); }
            if (!FiresLogger.DebugEnabled) return;
            double total = (T[_names.Length] - T[0]) * Ms;
            _total += total; _n++;
            if (gcDelta > 0) _gcUnits++;
            for (int i = 0; i < _names.Length; i++)
            {
                double ms = (T[i + 1] - T[i]) * Ms;
                _sum[i] += ms; if (ms > _worst[i]) _worst[i] = ms;
            }
            bool slow = total > _flagMs;
            if (total > _totalWorst || _worstSplit == "-" || slow)
            {
                string split = Split(total, gcDelta, what);
                if (total > _totalWorst || _worstSplit == "-") { _totalWorst = total; _worstSplit = split; }
                if (slow)
                {
                    _flagged++;
                    float now0 = Time.realtimeSinceStartup;
                    if (now0 >= _flagAt) { _flagAt = now0 + 1f; FiresLogger.LogDebug($"[FatStallProof] {_tag} SLOW unit {split} (over {_flagMs:0} ms) (0.2.1322)"); }
                }
            }
            float now = Time.realtimeSinceStartup;
            if (now < _reportAt) return;
            _reportAt = now + 10f;
            var sb = new StringBuilder();
            for (int i = 0; i < _names.Length; i++)
                sb.Append(i > 0 ? ", " : "").Append(_names[i]).Append(' ').Append((_sum[i] / _n).ToString("0.00", CultureInfo.InvariantCulture)).Append('/').Append(_worst[i].ToString("0.00", CultureInfo.InvariantCulture));
            FiresLogger.LogDebug($"[FatStallProof] {_tag} 10 s: {_n} unit(s), avg {(_total / _n).ToString("0.00", CultureInfo.InvariantCulture)} ms, worst {_totalWorst.ToString("0.0", CultureInfo.InvariantCulture)} ms, {_flagged} over {_flagMs:0} ms, {_gcUnits} with a gen-0 GC inside; phases avg/worst ms: {sb}; worst = {_worstSplit} (0.2.1322)");
            _total = _totalWorst = 0; _n = _gcUnits = _flagged = 0; _worstSplit = "-";
            for (int i = 0; i < _names.Length; i++) { _sum[i] = 0; _worst[i] = 0; }
        }

        private string Split(double total, int gcDelta, string what)
        {
            var sb = new StringBuilder();
            sb.Append(what).Append(' ').Append(total.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms (");
            for (int i = 0; i < _names.Length; i++)
                sb.Append(i > 0 ? ", " : "").Append(_names[i]).Append(' ').Append(((T[i + 1] - T[i]) * Ms).ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append("; gen-0 GC +").Append(gcDelta).Append(')');
            return sb.ToString();
        }
    }
}
