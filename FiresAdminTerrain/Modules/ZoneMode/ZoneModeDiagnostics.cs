using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Drives the server's periodic work (acknowledgement releases, location islands) and writes
    // one [FiresZones] summary line a minute while anything happened, so a test run can be read
    // from the log alone: what was gated, what stayed empty, what was refused, and whether any
    // planning pass read a closed zone (it must read the real world, and this proves it did).
    internal static class ZoneModeDiagnostics
    {
        private const float SummaryIntervalSeconds = 60f;

        private static float s_nextSummary;
        private static readonly ZoneModeGate.SampleTally s_mainThread = new ZoneModeGate.SampleTally();

        internal static void Tick()
        {
            // 0.2.1089 (Fire, live 1088 overlay: ZoneSystem.Update 288 ms/s): every FAT cause in this postfix is timed;
            // ZoneUpdateTimes writes the per-cause [ZoneUpdate] line every 10 s.
            long t = ZoneUpdateTimes.Now();
            // Chunk blueprints are placed on any voxel world, so their keys answer before the zone-mode gate.
            ZoneModeChunkKeys.ClientTick();
            t = ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.ChunkKeys, t);
            BiomePaint.Tick();
            t = ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.BiomePaint, t);
            ZoneModeCloseable.Tick();   // make-closeable runs on a world that is not zone mode yet
            t = ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.Closeable, t);
            if (ZoneModeSkylands.PlayerDriven)   // 0.2.1075 super flat, 0.2.1076 every non-zone-mode world with sky islands
            {
                ZoneModeSkylands.ServerTick();
                t = ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.SkylandsServer, t);
                ZoneModeSkylands.ClientTick();
                t = ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.SkylandsClient, t);
            }
            if (!ZoneModeRegistry.ModeActive) return;
            ZoneModeOpening.Tick();
            ZoneModeOverClosed.Tick();
            ZoneModePark.CloseTick();
            ZoneModeLandmarks.Tick();
            ZoneModeIslands.Tick();
            ZoneModeBorderWalls.Tick();
            ZoneModeChunkMachines.ServerTick();
            ZoneModeChunkGenerator.ServerTick();
            ZoneModeHammerChest.ServerTick();
            ZoneModeCultivatorChest.ServerTick();
            ZoneModeStartPins.ClientTick();
            ZoneModeSkylands.ServerTick();
            ZoneModeSkylands.ClientTick();
            ZoneModeSeedGhost.ClientTick();
            ZoneModeChunkSpawner.ClientTick();
            ZoneModeChunkTools.ClientTick();
            ZoneModeSwaps.EnsureIndex();
            ZoneModeEyeOfTheBeholder.ClientTick();
            ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.ZoneModeTicks, t);

            if (Time.time < s_nextSummary) return;
            s_nextSummary = Time.time + SummaryIntervalSeconds;
            if (ZoneModeConfig.SummaryLogging == null || !ZoneModeConfig.SummaryLogging.Value) return;
            WriteSummary();
        }

        internal static void FlushMainThreadTally()
        {
            var tally = ZoneModeGate.TakeMainThreadTally();
            s_mainThread.ClosedBiome += tally.ClosedBiome;
            s_mainThread.ClosedHeight += tally.ClosedHeight;
            s_mainThread.CoastHeight += tally.CoastHeight;
            s_mainThread.ClosedSector += tally.ClosedSector;
            s_mainThread.PlanningClosedReads += tally.PlanningClosedReads;
        }

        private static void WriteSummary()
        {
            FlushMainThreadTally();
            var worker = ZoneModeGate.TakeWorkerTotals();
            int staleDropped = ZoneModeBuilderCache.TakeDroppedStale();
            var parts = new List<string>();

            if (worker.Any)
                parts.Add($"heightmap builds {worker.Builds} ({worker.BuildsTouchingClosed} touched closed zones: "
                    + $"{worker.ClosedHeight + worker.CoastHeight:N0} sea-floor/coast vertices, {worker.ClosedSector} ocean corners)");
            long mainGated = s_mainThread.Gated;
            if (mainGated > 0)
                parts.Add($"main-thread closed samples {mainGated:N0} (map/environment/player biome)");
            if (s_mainThread.PlanningClosedReads > 0)
                parts.Add($"planning passes read {s_mainThread.PlanningClosedReads:N0} closed-zone samples UNGATED (as intended)");
            if (staleDropped > 0) parts.Add($"{staleDropped} stale build(s) dropped");
            if (ZoneModeBorderWalls.WallCount > 0 || ZoneModeBorderWalls.BuiltTotal > 0)
                parts.Add($"border walls: {ZoneModeBorderWalls.WallCount} standing, {ZoneModeBorderWalls.BuiltTotal} built so far, "
                    + $"{ZoneModeBorderWalls.CaveCellsCut:N0} cave sample(s) cut open, {ZoneModeBorderWalls.FallCount} waterfall(s)");

            if (ZoneModeRegistry.IsAuthority)
            {
                if (ZoneMode_ZoneSystem_SpawnZone.ClosedKeptEmpty > 0 || ZoneMode_ZoneSystem_SpawnZone.GhostRefused > 0
                    || ZoneMode_ZoneSystem_SpawnZone.OpenPopulated > 0)
                    parts.Add($"zones: {ZoneMode_ZoneSystem_SpawnZone.OpenPopulated} open populated, "
                        + $"{ZoneMode_ZoneSystem_SpawnZone.ClosedKeptEmpty} closed kept as sea, "
                        + $"{ZoneMode_ZoneSystem_SpawnZone.GhostRefused} ghost generations refused");
                ZoneMode_ZoneSystem_SpawnZone.ResetCounters();
                if (ZoneModeIslands.RevealedLocations > 0) parts.Add($"{ZoneModeIslands.RevealedLocations} location island(s) revealed so far");
            }

            int refusals = ZoneModeRefusals.BuildsRefused + ZoneModeRefusals.TerrainEditsRefused + ZoneModeRefusals.TeleportsRefused
                + ZoneModeRefusals.SpawnPointsCleared + ZoneModeRefusals.CreatureSpawnsRefused;
            if (refusals > 0)
                parts.Add($"refused: {ZoneModeRefusals.BuildsRefused} build(s), {ZoneModeRefusals.TerrainEditsRefused} terrain edit(s), "
                    + $"{ZoneModeRefusals.TeleportsRefused} teleport(s), {ZoneModeRefusals.CreatureSpawnsRefused} creature spawn(s); "
                    + $"{ZoneModeRefusals.SpawnPointsCleared} spawn point(s) cleared");
            ZoneModeRefusals.BuildsRefused = ZoneModeRefusals.TerrainEditsRefused = ZoneModeRefusals.TeleportsRefused = 0;
            ZoneModeRefusals.SpawnPointsCleared = ZoneModeRefusals.CreatureSpawnsRefused = 0;

            s_mainThread.Clear();
            string syncWaits = ZoneModeBuilderCache.TakeSyncWaitReport();
            if (syncWaits != null) FiresLogger.LogInfo($"[FiresZones] last {SummaryIntervalSeconds:0}s: {syncWaits}.");
            if (parts.Count == 0) return;
            FiresLogger.LogVerbose($"[FiresZones] last {SummaryIntervalSeconds:0}s —{ZoneModeRegistry.OpenCount} zone(s) open (v{ZoneModeRegistry.Version}); "
                + string.Join("; ", parts) + ".");
        }

        internal static void Reset()
        {
            s_nextSummary = 0f;
            s_mainThread.Clear();
            ZoneModeGate.TakeWorkerTotals();
            ZoneModeGate.TakeMainThreadTally();
            ZoneModeBuilderCache.TakeDroppedStale();
            ZoneMode_ZoneSystem_SpawnZone.ResetCounters();
            ZoneMode_ZoneSystem_SpawnZone.ForgetReported();
            ZoneModeIslands.Reset();
            ZoneModeOpening.Reset();
            ZoneModeBorderWalls.Reset();
            ZoneModeChunkMachines.Reset();
            ZoneModeChunkGenerator.Reset();
            ZoneModeChunkSpawner.Reset();
            ZoneModeHammerChest.Reset();
            ZoneModeCultivatorChest.Reset();
            ZoneModeStartSpawn.Reset();
            ZoneModeStartPins.Reset();
            ZoneModeSkylands.Reset();
            ZoneModeSeedGhost.Reset();
            ZoneModeLiftShore.Reset();
            ZoneModeSwaps.Reset();
            ZoneModeEyeOfTheBeholder.Reset();
            BiomePaint.Reset();
            ZoneModeCloseable.Reset();
        }
    }

    // 0.2.1089: per-cause timing of ZoneSystem.Update (Fire, live 1088: the overlay's top cost, 288 ms/s). The prefix/postfix pair on
    // ZoneSystem.Update (ZoneModePatches) gives the whole call including vanilla and every other mod; FAT's own causes are timed where
    // they run: the ZoneModeDiagnostics.Tick stages, BiomePaint.Tick's stages, and FAT's heightmap postfixes when they run INSIDE
    // ZoneSystem.Update (a zone spawning builds its heightmap in that call). Debug, one line every 10 s:
    //   [ZoneUpdate] last 10 s: whole call X ms/s (n calls, worst W ms); FAT Y ms/s; <cause> ms/s (calls) ...; rest (vanilla, other mods, FAT's cultivated-bind postfix) Z ms/s
    internal static class ZoneUpdateTimes
    {
        internal enum Cause
        {
            ChunkKeys, BiomePaint, BiomeRefresh, BiomeDeepRecolour, BiomeDeepCells, BiomeServer, Closeable,
            SkylandsServer, SkylandsClient, ZoneModeTicks,
            HmTint, HmPaintColours, HmEnvBox, HmWetMask, Count
        }

        private const float Every = 30f;   // 0.2.1102: the [ZoneUpdate] summary every 30 s (was 10 s)
        private static readonly long[] s_ticks = new long[(int)Cause.Count];
        private static readonly int[] s_calls = new int[(int)Cause.Count];
        private static long s_wholeTicks, s_wholeWorst, s_wholeStart;
        private static int s_wholeCalls, s_depth;
        private static float s_since = -1f;
        private static readonly System.Text.StringBuilder s_sb = new System.Text.StringBuilder(512);
        private static readonly int[] s_order = new int[(int)Cause.Count];

        // True while ZoneSystem.Update runs (heightmap postfixes count as a ZoneSystem.Update cause only then).
        internal static bool Inside => s_depth > 0;

        internal static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();

        // Adds the time since t0 to the cause and returns now, so stages chain: t = Add(a, t); t = Add(b, t);
        internal static long Add(Cause c, long t0)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            s_ticks[(int)c] += now - t0;
            s_calls[(int)c]++;
            s_cumulative += now - t0;
            return now;
        }

        // 0.2.1103 (fat_proof_snow_mp_test): every FAT cause's time since start, never reset (Stopwatch ticks).
        private static long s_cumulative;
        internal static long CumulativeTicks => s_cumulative;

        // A heightmap postfix: counted only inside ZoneSystem.Update.
        internal static void AddIfInside(Cause c, long t0)
        {
            if (s_depth > 0) Add(c, t0);
        }

        internal static void Begin()
        {
            if (s_depth++ == 0) s_wholeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        internal static void End()
        {
            if (s_depth <= 0) return;
            if (--s_depth > 0) return;
            long d = System.Diagnostics.Stopwatch.GetTimestamp() - s_wholeStart;
            s_wholeTicks += d;
            if (d > s_wholeWorst) s_wholeWorst = d;
            s_wholeCalls++;
            Report();
        }

        private static void Report()
        {
            float now = Time.unscaledTime;
            if (s_since < 0f) { s_since = now; return; }
            float span = now - s_since;
            if (span < Every) return;
            if (FiresLogger.DebugEnabled)
            {
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency / span;   // ticks -> ms per second
                long fat = 0;
                for (int i = 0; i < (int)Cause.Count; i++) { fat += s_ticks[i]; s_order[i] = i; }
                // the stages nest: BiomePaint's own stages are inside BiomePaint, so FAT's total counts the outer stages only
                fat -= s_ticks[(int)Cause.BiomeRefresh] + s_ticks[(int)Cause.BiomeDeepRecolour] + s_ticks[(int)Cause.BiomeDeepCells]
                     + s_ticks[(int)Cause.BiomeServer];
                System.Array.Sort(s_order, (a, b) => s_ticks[b].CompareTo(s_ticks[a]));
                s_sb.Length = 0;
                s_sb.Append("[ZoneUpdate] last ").Append(span.ToString("0")).Append(" s: whole call ")
                    .Append((s_wholeTicks * f).ToString("0.0")).Append(" ms/s (").Append(s_wholeCalls).Append(" calls, worst ")
                    .Append((s_wholeWorst * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0")).Append(" ms); FAT ")
                    .Append((fat * f).ToString("0.0")).Append(" ms/s:");
                int shown = 0;
                for (int k = 0; k < s_order.Length && shown < 8; k++)
                {
                    int i = s_order[k];
                    if (s_calls[i] == 0) break;
                    s_sb.Append(' ').Append(((Cause)i).ToString()).Append(' ').Append((s_ticks[i] * f).ToString("0.00"))
                        .Append(" (").Append(s_calls[i]).Append(");");
                    shown++;
                }
                s_sb.Append(" rest (vanilla, other mods, FAT's cultivated-bind postfix) ").Append(((s_wholeTicks - fat) * f).ToString("0.0")).Append(" ms/s");
                FiresLogger.LogDebug(s_sb.ToString());
            }
            System.Array.Clear(s_ticks, 0, s_ticks.Length);
            System.Array.Clear(s_calls, 0, s_calls.Length);
            s_wholeTicks = s_wholeWorst = 0; s_wholeCalls = 0;
            s_since = now;
        }
    }
}
