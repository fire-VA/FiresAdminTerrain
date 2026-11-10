using System;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    /// <summary>
    /// 0.2.1209 (lead / Fire 10-28 17:25, VA DN at 1028 -2046: 0.2 fps; one vanilla Heightmap.Generate = 3.7 s, all of it waiting in
    /// HeightmapBuilder.RequestTerrainSync on the builder thread): READ-ONLY timers. Every WorldGenerator sampler the builder calls is
    /// wrapped first/last (inclusive: every mod's prefix + vanilla), and FAT's own steps inside them (BC base height, BC biome map,
    /// zone-mode gates) are timed apart. Per builder thread, per Build call; a Build over SlowMs logs one Debug split (worst per 5 s).
    /// Counts nested calls inclusively: GetBiomeHeight contains GetBaseHeight etc.
    /// </summary>
    internal static class FatGenTiming
    {
        internal const int GetBiome = 0, GetBiomeHeight = 1, GetBaseHeight = 2, GetForestFactor = 3, IsDeepnorth = 4, IsAshlands = 5,
            AshGradient = 6, BcBase = 7, BcBiome = 8, GateBiome = 9, GateHeight = 10, BcForest = 11,
            HeightVanilla = 12, HeightFatPost = 13, HeightRpgmPost = 14, BuildPrefixes = 15, BiomeSector = 16, Slots = 17;
        private static readonly string[] Names = { "GetBiome", "GetBiomeHeight", "GetBaseHeight", "GetForestFactor", "IsDeepnorth", "IsAshlands",
            "GetAshlandsOceanGradient", "  FAT BC base height", "  FAT BC biome map", "  FAT zone gate (biome)", "  FAT zone gate (height)", "  FAT BC forest map",
            "  GetBiomeHeight: vanilla body (after every prefix)", "  GetBiomeHeight: FAT postfix (BC paint/rough + start-cave hill)", "  GetBiomeHeight: FiresRPGmaker TerritoryBiomeBlend postfix", "Build: FAT ZoneMode + BiomePaint prefixes", "GetBiomeSector (FAT ZoneMode / BiomePaint / SuperFlat / BiomeOverride + vanilla)" };
        private const double SlowMs = 300.0;

        [ThreadStatic] private static long[] t_ticks;
        [ThreadStatic] private static int[] t_calls;
        [ThreadStatic] private static bool t_inBuild;
        [ThreadStatic] private static int t_gc0, t_gc1, t_gc2;   // 0.2.1215: GC collections during this build (any thread caused them)
        private static readonly object s_lock = new object();
        private static readonly Stopwatch s_clock = Stopwatch.StartNew();
        private static long s_nextLogMs;
        private static double s_worstMs;
        private static string s_worst;

        internal static long Begin() => t_inBuild ? Stopwatch.GetTimestamp() : 0L;
        internal static void End(int slot, long start)
        {
            if (start == 0L || t_ticks == null) return;
            t_ticks[slot] += Stopwatch.GetTimestamp() - start;
            t_calls[slot]++;
        }

        private static void BuildStart()
        {
            if (t_ticks == null) { t_ticks = new long[Slots]; t_calls = new int[Slots]; }
            Array.Clear(t_ticks, 0, Slots); Array.Clear(t_calls, 0, Slots);
            t_lastEnd = 0L; t_gapMax = t_gapSum = 0L; t_gaps = 0;
            t_gc0 = GC.CollectionCount(0); t_gc1 = GC.CollectionCount(1); t_gc2 = GC.CollectionCount(2);
            t_inBuild = true;
        }

        // 0.2.1212 (bcslow84: main thread waits 5.4-5.7 s in RequestTerrainSync while the worst single build is 0.48 s): vanilla's
        // builder is ONE thread working a FIFO list, and a sync request is appended at the END, so it waits for every build queued
        // ahead of it. Read-only: queue depth and the request's place in it when asked, builds finished during the wait and their
        // total time, and the builder's own idle sleep. A wait over SyncSlowMs logs one Debug line (worst per 5 s).
        private static readonly AccessTools.FieldRef<HeightmapBuilder, object> LockRef = AccessTools.FieldRefAccess<HeightmapBuilder, object>("m_lock");
        private static readonly AccessTools.FieldRef<HeightmapBuilder, System.Collections.Generic.List<HeightmapBuilder.HMBuildData>> ToBuildRef =
            AccessTools.FieldRefAccess<HeightmapBuilder, System.Collections.Generic.List<HeightmapBuilder.HMBuildData>>("m_toBuild");
        private const double SyncSlowMs = 100.0;
        private static int s_buildsDone, s_syncDepth, s_syncPos, s_syncBuildsAtStart, s_distantQueued;
        private static long s_buildTicksDone, s_syncBuildTicksAtStart, s_syncLogMs;
        private static string s_syncWorst; private static double s_syncWorstMs;

        internal static void SyncQueueAtRequest(HeightmapBuilder b, Vector3 center, int width, float scale, bool distantLod, WorldGenerator wg)
        {
            s_syncDepth = -1; s_syncPos = -1; s_distantQueued = 0;
            s_syncBuildsAtStart = System.Threading.Volatile.Read(ref s_buildsDone);
            s_syncBuildTicksAtStart = System.Threading.Interlocked.Read(ref s_buildTicksDone);
            try
            {
                object gate = LockRef(b); var list = ToBuildRef(b);
                if (gate == null || list == null) return;
                lock (gate)
                {
                    s_syncDepth = list.Count;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].m_distantLod) s_distantQueued++;
                        if (s_syncPos < 0 && list[i].IsEqual(center, width, scale, distantLod, wg)) s_syncPos = i;
                    }
                }
            }
            catch { s_syncDepth = -2; }
        }

        internal static void SyncWaitDone(long ticks, Vector3 center)
        {
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            if (ms < SyncSlowMs) return;
            int builds = System.Threading.Volatile.Read(ref s_buildsDone) - s_syncBuildsAtStart;
            double buildMs = (System.Threading.Interlocked.Read(ref s_buildTicksDone) - s_syncBuildTicksAtStart) * 1000.0 / Stopwatch.Frequency;
            string line = $"sync heightmap wait {ms:0} ms for ({center.x:0},{center.z:0}): builder queue {s_syncDepth} job(s) when asked ({s_distantQueued} distant-LOD), "
                + $"this one {(s_syncPos < 0 ? "not queued yet (appended at the end)" : "already at #" + s_syncPos)}; {builds} build(s) finished during the wait taking {buildMs:0} ms, "
                + $"so {Math.Max(0, ms - buildMs):0} ms outside builds (vanilla sleeps 10 ms after every build, + GC / CPU)";
            if (ms > s_syncWorstMs) { s_syncWorstMs = ms; s_syncWorst = line; }
            long now = s_clock.ElapsedMilliseconds;
            if (now < s_syncLogMs) return;
            s_syncLogMs = now + 5000;
            FiresLogger.LogDebug("[BcImport] " + s_syncWorst + " (worst in 5 s; 0.2.1212)");
            s_syncWorst = null; s_syncWorstMs = 0;
        }

        private static void BuildEnd(long start)
        {
            System.Threading.Interlocked.Add(ref s_buildTicksDone, Stopwatch.GetTimestamp() - start);
            System.Threading.Interlocked.Increment(ref s_buildsDone);
            t_inBuild = false;
            if (t_ticks == null) return;
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms < SlowMs) return;
            var sb = new StringBuilder();
            sb.Append($"heightmap build {ms:0} ms on thread {System.Threading.Thread.CurrentThread.ManagedThreadId}, BcImport active {BcMigratedWorld.Active}:");
            sb.Append($" GC during the build gen0 {GC.CollectionCount(0) - t_gc0} gen1 {GC.CollectionCount(1) - t_gc1} gen2 {GC.CollectionCount(2) - t_gc2} (0.2.1215); builder thread priority {System.Threading.Thread.CurrentThread.Priority};");
            sb.Append($" stalls between height samples (thread not running): {t_gaps} over 1 ms, {t_gapSum * 1000.0 / Stopwatch.Frequency:0} ms in total, longest {t_gapMax * 1000.0 / Stopwatch.Frequency:0} ms;");
            for (int i = 0; i < Slots; i++)
                if (t_calls[i] > 0)
                    sb.Append($" {Names[i]} {t_ticks[i] * 1000.0 / Stopwatch.Frequency:0} ms / {t_calls[i]:N0} calls ({t_ticks[i] * 1e6 / Stopwatch.Frequency / t_calls[i]:0.0} us each);");
            string line = sb.ToString();
            lock (s_lock)
            {
                if (ms > s_worstMs) { s_worstMs = ms; s_worst = line; }
                long now = s_clock.ElapsedMilliseconds;
                if (now < s_nextLogMs) return;
                s_nextLogMs = now + 5000;
                line = s_worst; s_worst = null; s_worstMs = 0;
            }
            if (line != null) FiresLogger.LogDebug("[BcImport] slow " + line + " (worst in 5 s; inclusive, nested calls count in their parents; 0.2.1209, segments 0.2.1210)");
        }

        [HarmonyPatch(typeof(HeightmapBuilder), "Build")]
        private static class BuildTimer
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) { BuildStart(); __state = Stopwatch.GetTimestamp(); }
            [HarmonyPriority(Priority.Last)] private static void Postfix(long __state) => BuildEnd(__state);
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(float), typeof(float), typeof(float), typeof(bool))]
        private static class T0 { [HarmonyPriority(Priority.First)] static void Prefix(out long __state) => __state = Begin(); [HarmonyPriority(Priority.Last)] static void Postfix(long __state) => End(GetBiome, __state); }
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight))]
        private static class T1
        {
            [HarmonyPriority(Priority.First)] static void Prefix(out long __state) { __state = Begin(); NoteGap(__state); }
            [HarmonyPriority(Priority.Last)] static void Postfix(long __state) { End(GetBiomeHeight, __state); if (__state != 0L) t_lastEnd = Stopwatch.GetTimestamp(); }
        }
        // 0.2.1215: the time BETWEEN two GetBiomeHeight calls of one build is vanilla's own loop (microseconds); a long gap there is the
        // builder thread not running (GC stop-the-world, or the OS giving its core to other threads). Largest gap + total of gaps > 1 ms.
        [ThreadStatic] private static long t_lastEnd, t_gapMax, t_gapSum; [ThreadStatic] private static int t_gaps;
        private static void NoteGap(long now)
        {
            if (now == 0L || t_lastEnd == 0L) return;
            long g = now - t_lastEnd;
            if (g > t_gapMax) t_gapMax = g;
            if (g * 1000 > Stopwatch.Frequency) { t_gapSum += g; t_gaps++; }
        }
        // 0.2.1210 (bcslow80: GetBiomeHeight 341 us a call on some DN tiles, FAT's own prefix steps ~0): its segments, by priority. A prefix
        // after every other prefix marks the body start; postfixes at 500 / 300 / 100 close vanilla's body, FAT's postfix (400), RPGM's (200).
        [ThreadStatic] private static long t_mark;
        private static void Seg(int slot) { long now = Stopwatch.GetTimestamp(); if (t_mark != 0L) End(slot, t_mark); t_mark = t_inBuild ? now : 0L; }
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight))]
        private static class HeightSegs
        {
            [HarmonyPriority(Priority.Last - 1)] static void Prefix() => t_mark = Begin();
            [HarmonyPriority(500)] static void Postfix() => Seg(HeightVanilla);
        }
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight))]
        private static class HeightSegFat { [HarmonyPriority(300)] static void Postfix() => Seg(HeightFatPost); }
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight))]
        private static class HeightSegRpgm { [HarmonyPriority(100)] static void Postfix() { Seg(HeightRpgmPost); t_mark = 0L; } }
        [HarmonyPatch(typeof(HeightmapBuilder), "Build")]
        private static class BuildSegStart { [HarmonyPriority(500)] static void Prefix() => t_mark = Begin(); }
        [HarmonyPatch(typeof(HeightmapBuilder), "Build")]
        private static class BuildSegEnd { [HarmonyPriority(300)] static void Prefix() { Seg(BuildPrefixes); t_mark = 0L; } }
        // 0.2.1215: vanilla Build also asks GetBiomeSector 4x a tile; FAT patches it four times and none were timed.
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(float), typeof(float), typeof(bool))]
        private static class T7 { [HarmonyPriority(Priority.First)] static void Prefix(out long __state) => __state = Begin(); [HarmonyPriority(Priority.Last)] static void Postfix(long __state) => End(BiomeSector, __state); }
        [HarmonyPatch(typeof(WorldGenerator), "GetBaseHeight")]
        private static class T2 { [HarmonyPriority(Priority.First)] static void Prefix(out long __state) => __state = Begin(); [HarmonyPriority(Priority.Last)] static void Postfix(long __state) => End(GetBaseHeight, __state); }
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetForestFactor))]
        private static class T3 { [HarmonyPriority(Priority.First)] static void Prefix(out long __state) => __state = Begin(); [HarmonyPriority(Priority.Last)] static void Postfix(long __state) => End(GetForestFactor, __state); }
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.IsDeepnorth))]
        private static class T4 { [HarmonyPriority(Priority.First)] static void Prefix(out long __state) => __state = Begin(); [HarmonyPriority(Priority.Last)] static void Postfix(long __state) => End(IsDeepnorth, __state); }
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.IsAshlands))]
        private static class T5 { [HarmonyPriority(Priority.First)] static void Prefix(out long __state) => __state = Begin(); [HarmonyPriority(Priority.Last)] static void Postfix(long __state) => End(IsAshlands, __state); }
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetAshlandsOceanGradient), new[] { typeof(float), typeof(float) })]
        private static class T6 { [HarmonyPriority(Priority.First)] static void Prefix(out long __state) => __state = Begin(); [HarmonyPriority(Priority.Last)] static void Postfix(long __state) => End(AshGradient, __state); }
    }
}
