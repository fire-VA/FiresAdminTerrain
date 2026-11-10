using System.Diagnostics;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1107 (round #5 profile: Character.UpdateWalking ~9.4-10 of UpdateMotion's 12.1-12.8 ms/s, CustomFixedUpdate 30+ ms/s, with
    // FAT's per-character hooks on them): what FAT's per-character hooks cost, every character, every physics step. Ticks only (no
    // allocation per call); one line every 10 s while any of them ran.
    // 0.2.1160 (lead's profile of Fire's session: the PLAYER alone 19.7 ms/s in FixedUpdate.Character vs 11.7 for 5 companions):
    // + the deep-snow slow (ApplyLiquidResistance) and the custom-water liquid hooks (InLiquid / InWater / IsSwimming /
    // GetLiquidLevel), each split into the local player's share. Debug only (Fire: proof logging is Debug).
    internal static class FatHooks
    {
        internal enum Hook { Trigger, SkylandHold, IceCreatures, DeepSnow, Water, Count }
        private static readonly string[] Names = { "Trigger", "SkylandHold", "IceCreatures", "DeepSnowSlow", "WaterLiquid" };

        private static readonly long[] s_ticks = new long[(int)Hook.Count], s_playerTicks = new long[(int)Hook.Count];
        private static readonly int[] s_calls = new int[(int)Hook.Count], s_acted = new int[(int)Hook.Count], s_playerCalls = new int[(int)Hook.Count];
        private static float s_windowStart = -1f;

        internal static long Start() => Stopwatch.GetTimestamp();

        // 0.2.1196 (lead, round51: WaterLiquid a steady ~14 ms/s, ~2.3k uncached lookups/s at ~6 us): the uncached water lookup split
        // into its four sources and the blocked-by-geometry raycast. Returns now, so consecutive stages chain.
        private static readonly string[] WaterStageNames = { "legacy water", "mesh combiner", "pours", "generated water", "raycast" };
        private static readonly long[] s_waterStage = new long[5];
        private static readonly int[] s_waterStageCalls = new int[5];
        internal static long WaterStage(int stage, long t0)
        {
            long now = Stopwatch.GetTimestamp();
            s_waterStage[stage] += now - t0; s_waterStageCalls[stage]++;
            return now;
        }

        internal static void Stop(Hook h, long t0, bool acted) => Stop(h, t0, acted, false);

        internal static void Stop(Hook h, long t0, bool acted, bool localPlayer)
        {
            int i = (int)h;
            long dt = Stopwatch.GetTimestamp() - t0;
            s_ticks[i] += dt;
            s_calls[i]++;
            if (acted) s_acted[i]++;
            if (localPlayer) { s_playerTicks[i] += dt; s_playerCalls[i]++; }
            float now = Time.unscaledTime;
            if (s_windowStart < 0f) { s_windowStart = now; return; }
            float span = now - s_windowStart;
            if (span < 10f) return;
            if (FiresLogger.DebugEnabled)
            {
                double k = 1000.0 / Stopwatch.Frequency / span;
                var sb = new System.Text.StringBuilder($"[FatHooks] per-character ms/s over {span:0} s:");
                for (int j = 0; j < (int)Hook.Count; j++)
                    sb.Append($" {Names[j]} {s_ticks[j] * k:0.00} ({s_calls[j]} calls, {s_acted[j]} acted; local player {s_playerTicks[j] * k:0.00} ms/s, {s_playerCalls[j]} calls){(j < (int)Hook.Count - 1 ? "," : "")}");
                sb.Append("; WaterLiquid uncached split:");
                for (int j = 0; j < s_waterStage.Length; j++)
                    sb.Append($" {WaterStageNames[j]} {s_waterStage[j] * k:0.00} ms/s ({s_waterStageCalls[j]} timed)");
                sb.Append(" (0.2.1196)");
                FiresLogger.LogDebug(sb.ToString());
            }
            System.Array.Clear(s_ticks, 0, s_ticks.Length);
            System.Array.Clear(s_playerTicks, 0, s_playerTicks.Length);
            System.Array.Clear(s_calls, 0, s_calls.Length);
            System.Array.Clear(s_acted, 0, s_acted.Length);
            System.Array.Clear(s_playerCalls, 0, s_playerCalls.Length);
            System.Array.Clear(s_waterStage, 0, s_waterStage.Length);
            System.Array.Clear(s_waterStageCalls, 0, s_waterStageCalls.Length);
            s_windowStart = now;
        }
    }
}
