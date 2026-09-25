using System.Collections.Generic;

namespace VerdantsAscent.Terrain
{
    // FAT's line in Core's status box (FiresCore.Logging.StatusBanner): what the mod holds right now, in one glance.
    // Runs on the main thread each banner interval, so it only reads counters and never logs.
    internal static class FatStatus
    {
        internal static string Line()
        {
            if (ZNet.instance == null || ZNetScene.instance == null) return null;
            var parts = new List<string>(6);
            if (Zones.ZoneModeRegistry.ModeActive)
                parts.Add($"zones open {Zones.ZoneModeRegistry.OpenCount} (v{Zones.ZoneModeRegistry.Version})"
                    + (Zones.ZoneModePark.ParkedCount > 0 ? $", parked {Zones.ZoneModePark.ParkedCount}" : ""));
            if (Voxel.FiresVoxelConfig.StreamerRuns)
                parts.Add($"voxel chunks {Voxel.VoxelWorld.BudgetedChunkCount}/{Voxel.FiresVoxelConfig.MaxLoadedChunks.Value}");
            if (Voxel.FiresVoxelConfig.CarvesRiverBeds)
                parts.Add(Voxel.VoxelHydrology.Ready ? "hydrology ready" : "hydrology building");
            if (ZNet.instance.IsServer() && Zones.ZoneModeRegistry.ModeActive)
            {
                parts.Add(Zones.ZoneModeChunkMachines.StarterStatus);
                parts.Add(Zones.ZoneModeHammerChest.Status);
            }
            return parts.Count == 0 ? null : string.Join(" | ", parts);
        }
    }
}
