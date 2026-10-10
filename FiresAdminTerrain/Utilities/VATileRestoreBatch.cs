using System.Collections;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VATileRestoreBatch — accumulator that COLLAPSES per-tile
    // "[TileAnchor] tile=(X,Y) loaded ..." logs into a single batch
    // 🪨 TILES banner emitted whenever there's a pause in restores.
    //
    // The problem this fixes:
    //   Every active zone restore re-instantiates each tile in that
    //   zone via ZNetScene, which fires TileAnchor.Awake + EnsureTile
    //   + TileMeshRenderer.Awake + (sometimes) TileMeshUnderside +
    //   the final "loaded" log per tile — typically 4-7 lines per
    //   tile, ×N tiles per zone activation. With multiple zones
    //   coming active in quick succession (initial world load,
    //   zone crossing), this drowns the BepInEx console.
    //
    // The fix:
    //   1. Per-tile success logs are verbose-gated (FiresLogger
    //      .VerboseEnabled) at each emit site — off by default.
    //   2. The TileAnchor.Load path calls RecordTileRestored once
    //      per successful restore with that tile's stats.
    //   3. A polling coroutine started by FiresAdminTerrain.Awake
    //      ticks every 0.5s. If counters are non-zero AND no new
    //      restores have arrived for `DebounceSeconds` (default 2s),
    //      the batch banner is emitted and counters reset.
    //   4. Result: one mini-banner per "wave" of tile restores
    //      instead of N×7 lines.
    //
    // Failures are NOT batched — TileAnchor's error / warning logs
    // bypass this tracker and stay visible at full verbosity so a
    // bad save / missing prefab / NRE is still obvious in the log.
    public static class VATileRestoreBatch
    {
        // Window of quiescence after the last restore before we flush
        // a banner. Tuned to be long enough to coalesce a full
        // initial-world-load tile wave (typical 1-2s for 3-10 tiles)
        // but short enough that a zone-crossing wave during gameplay
        // doesn't sit silently for too long.
        public const float DebounceSeconds = 2f;

        // Polling cadence. We don't need sub-second precision — the
        // banner is a UX nicety, not a real-time signal.
        private const float PollIntervalSeconds = 0.5f;

        // Accumulator state. Reset to zero after each Flush.
        private static int _tiles;
        private static long _payloadBytes;
        private static int _heightMods;
        private static int _paintMods;
        private static int _disks;
        private static float _lastRestoreTime;
        private static bool _pending;

        // Records one successful tile-restore. Called from
        // TileAnchor.Load (the path that fires the existing per-tile
        // "loaded | payload=..." log). Increments accumulators and
        // resets the debounce timer.
        public static void RecordTileRestored(int payloadBytes, int heightMods, int paintMods, int disks)
        {
            _tiles++;
            _payloadBytes += (long)payloadBytes;
            _heightMods += heightMods;
            _paintMods += paintMods;
            _disks += disks;
            _lastRestoreTime = Time.realtimeSinceStartup;
            _pending = true;
        }

        // True if the accumulator has data AND the debounce window
        // has elapsed since the last restore. The polling coroutine
        // checks this every PollIntervalSeconds.
        public static bool ShouldFlush()
        {
            if (!_pending) return false;
            return (Time.realtimeSinceStartup - _lastRestoreTime) >= DebounceSeconds;
        }

        // Emit the banner with the accumulated totals and reset.
        // Idempotent — calling when _pending==false is a no-op.
        public static void Flush()
        {
            if (!_pending || _tiles == 0)
            {
                _pending = false;
                return;
            }

            try
            {
                VAFatLoadSummary.EmitTilesRestored(_tiles, _payloadBytes, _heightMods, _paintMods, _disks);
            }
            catch { /* banner failure must not leak the accumulator */ }

            _tiles = 0;
            _payloadBytes = 0;
            _heightMods = 0;
            _paintMods = 0;
            _disks = 0;
            _pending = false;
        }

        // Coroutine started by FAT's Awake. Runs forever (the FAT
        // plugin is DontDestroyOnLoad); on each tick, checks the
        // debounce condition and flushes if ready.
        //
        // Cost per tick: one Time.realtimeSinceStartup compare. Per-
        // restore: increment a few counters. Both negligible.
        public static IEnumerator PollAndFlushCoroutine()
        {
            var wait = new WaitForSeconds(PollIntervalSeconds);
            while (true)
            {
                yield return wait;
                if (ShouldFlush()) Flush();
            }
        }
    }
}
