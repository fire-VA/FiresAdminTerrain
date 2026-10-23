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
            // Chunk blueprints are placed on any voxel world, so their keys answer before the zone-mode gate.
            ZoneModeChunkKeys.ClientTick();
            BiomePaint.Tick();
            ZoneModeCloseable.Tick();   // make-closeable runs on a world that is not zone mode yet
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
}
