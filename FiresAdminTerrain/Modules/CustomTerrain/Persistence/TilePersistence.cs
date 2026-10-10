using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Terrain.Persistence
{
    // Façade + toggle gate for the new tile-anchor persistence system.
    // Op entry points (disk place / paint / height ops) consult IsEnabled
    // before branching; OFF preserves the legacy TerrainCombinerAnchor path,
    // ON routes through TileAnchor.Spawn + ApplyOpAndSave per overlapping tile.
    //
    // Phase 8d wraps the legacy ops with conditional branches:
    //
    //   if (TilePersistence.IsEnabled)
    //       TilePersistence.RecordDiskOnOverlappingTiles(disk);
    //   else
    //       DirtFloorTerrainCompStorage.SaveDisk(disk);  // legacy
    //
    // RecordDiskOnOverlappingTiles handles the tile discovery, anchor spawn,
    // and per-tile mutation. Each overlapping tile gets the disk's metadata
    // added to its Disks list; per-vertex height/paint application from disk
    // footprint is a follow-up step (Phase 8e — wiring the existing
    // DirtFloorMeshCombiner.HeightOps height computation into the tile-grid
    // update).
    public static class TilePersistence
    {
        public static bool IsEnabled =>
            ConfigManager.Instance?.configUseNewTilePersistence?.Value ?? false;

        // Spawn (or fetch) anchors for every tile the disk overlaps and append
        // the disk record. Idempotent on disk StableId — re-recording the same
        // disk replaces the prior record in each overlapping tile.
        //
        // Returns the number of tiles touched (for logging / debug).
        public static int RecordDiskOnOverlappingTiles(DiskRecord disk)
        {
            int touched = 0;
            float radius = Mathf.Max(disk.Radius, 0.5f);
            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(disk.Center, radius))
            {
                var anchor = TileAnchor.Spawn(tileXZ);
                if (anchor == null) continue;
                anchor.ApplyOpAndSave(tile => tile.AddOrReplaceDisk(disk));
                touched++;
            }
            FiresLogger.LogVerbose(
                $"[TilePersistence] RecordDisk id={disk.StableId} center={disk.Center} r={radius} → {touched} tile(s)");
            return touched;
        }

        // Remove a disk record by StableId from every tile it overlaps.
        // The tile's anchor remains in place even if its disk count drops to 0
        // (heights / paint may still hold data); a separate sweep cleans up
        // empty tiles later if needed.
        public static int RemoveDiskFromOverlappingTiles(int stableId, Vector3 lastKnownCenter, float lastKnownRadius)
        {
            int removed = 0;
            float radius = Mathf.Max(lastKnownRadius, 0.5f);
            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(lastKnownCenter, radius))
            {
                var anchor = TileAnchorRegistry.GetAt(tileXZ);
                if (anchor == null) continue; // tile never loaded → nothing to remove
                anchor.ApplyOpAndSave(tile =>
                {
                    if (tile.RemoveDisk(stableId)) removed++;
                });
            }
            FiresLogger.LogVerbose(
                $"[TilePersistence] RemoveDisk id={stableId} center={lastKnownCenter} r={radius} → {removed} tile(s) updated");
            return removed;
        }
    }
}
