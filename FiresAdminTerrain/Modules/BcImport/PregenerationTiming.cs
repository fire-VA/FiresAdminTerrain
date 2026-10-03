using System.Diagnostics;
using HarmonyLib;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.BcImport
{
    // Times vanilla's world pre-generation (lakes, rivers, streams, Deep North streams), which WorldGenerator's constructor
    // runs inside WorldGenerator.Initialize, so inside ZNet.RPC_PeerInfo on every join. FDT's join ledger could only call it
    // "716 ms in this call, outside every timed child" (R5). One line per world load, with how many base heights were
    // sampled through the BetterContinents generator meanwhile, which says whether that sampling is FAT's to speed up.
    internal static class PregenerationTiming
    {
        private const double MillisecondsPerSecond = 1000d;

        private static long s_lakes, s_rivers, s_streams;
        private static int s_samplesAtStart;

        private static double Milliseconds(long ticks) => ticks * MillisecondsPerSecond / Stopwatch.Frequency;

        [HarmonyPatch(typeof(WorldGenerator), "FindLakes")]
        private static class FindLakes
        {
            private static void Prefix(out long __state)
            {
                s_samplesAtStart = BcMigratedWorld.BaseHeightSamples;
                __state = Stopwatch.GetTimestamp();
            }

            private static void Postfix(long __state) => s_lakes = Stopwatch.GetTimestamp() - __state;
        }

        [HarmonyPatch(typeof(WorldGenerator), "PlaceRivers")]
        private static class PlaceRivers
        {
            private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

            private static void Postfix(long __state) => s_rivers = Stopwatch.GetTimestamp() - __state;
        }

        // Pre-generation runs the plain streams, then the Deep North ones last, so the Deep North pass closes the line.
        [HarmonyPatch(typeof(WorldGenerator), "PlaceStreams")]
        private static class PlaceStreams
        {
            private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

            private static void Postfix(WorldGenerator __instance, bool isDN, long __state)
            {
                long elapsed = Stopwatch.GetTimestamp() - __state;
                if (!isDN)
                {
                    s_streams = elapsed;
                    return;
                }
                if (WorldGeneratorAccess.IsMenuWorld(__instance)) return;
                int samples = BcMigratedWorld.BaseHeightSamples - s_samplesAtStart;
                FiresLogger.LogInfo($"[BcImport] vanilla world pre-generation: lakes {Milliseconds(s_lakes):0} ms, rivers "
                    + $"{Milliseconds(s_rivers):0} ms, streams {Milliseconds(s_streams):0} ms, Deep North streams {Milliseconds(elapsed):0} ms; "
                    + $"{(BcMigratedWorld.Active ? $"about {samples:N0} base heights through the BetterContinents generator" : "vanilla heights")}.");
            }
        }
    }
}
