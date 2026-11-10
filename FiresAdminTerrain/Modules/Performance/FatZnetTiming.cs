using System.Diagnostics;

namespace VerdantsAscent.Terrain.Performance
{
    // 0.2.1268 ([fgn] PatchTimer, Fire 10-30 standing fps: "ZNet.Update ... postfix FiresAdminTerrain:Driver.Postfix + Driver.Postfix +
    // Patch_Tick.Postfix 8162 us/call 158 ms/s" - Harmony runs FAT's three ZNet.Update postfixes back to back, so the timer could not
    // split them): each postfix and the snow skin's parts time themselves here; one Debug line every 10 s with ms/s, us/call, worst.
    internal static class FatZnetTiming
    {
        internal enum Slot { IceCover, TrailRefill, SkinDriver, SkinTrails, SkinScan, SkinBuild, SkinWalkers, Count }
        private static readonly string[] Names = { "ice cover", "trail refill", "snow skin (all)", "  trails", "  scan", "  build", "  walkers" };
        private static readonly long[] s_ticks = new long[(int)Slot.Count], s_worst = new long[(int)Slot.Count];
        private static readonly int[] s_calls = new int[(int)Slot.Count];
        private static long s_logAt;

        internal static long Now() => Stopwatch.GetTimestamp();

        internal static long Add(Slot slot, long t0)
        {
            long now = Stopwatch.GetTimestamp(), d = now - t0;
            int i = (int)slot;
            s_ticks[i] += d; s_calls[i]++; if (d > s_worst[i]) s_worst[i] = d;
            if (slot == Slot.IceCover) Log(now);   // the first postfix logs for all
            return now;
        }

        private static void Log(long now)
        {
            if (s_logAt == 0) { s_logAt = now + 10 * Stopwatch.Frequency; return; }
            if (now < s_logAt) return;
            s_logAt = now + 10 * Stopwatch.Frequency;
            if (FiresLogger.DebugEnabled)
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                var sb = new System.Text.StringBuilder("[ZNetPostfix] FAT's ZNet.Update postfixes, last 10 s:");
                for (int i = 0; i < (int)Slot.Count; i++)
                    sb.Append($" {Names[i]} {s_ticks[i] * ms / 10.0:0.0} ms/s ({(s_calls[i] > 0 ? s_ticks[i] * ms * 1000.0 / s_calls[i] : 0):0} us/call, worst {s_worst[i] * ms:0.0} ms);");
                FiresLogger.LogDebug(sb.Append(" (FAT ").Append(global::VerdantsAscent.FiresAdminTerrain.PluginVersion).Append(")").ToString());
            }
            System.Array.Clear(s_ticks, 0, s_ticks.Length); System.Array.Clear(s_worst, 0, s_worst.Length); System.Array.Clear(s_calls, 0, s_calls.Length);
        }
    }
}
