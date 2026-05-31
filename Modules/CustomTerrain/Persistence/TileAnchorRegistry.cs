using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Static lookup table: Vector2i tile index -> TileAnchor.
    //
    // Replaces DirtFloorZoneRegistry's connectivity-based grouping with
    // deterministic tile-index addressing. Same world position always maps
    // to the same tile, so there's no proximity matching, no "destroy
    // duplicate" logic — the dictionary key is the identity.
    //
    // Anchors register on Awake (after ZNetScene streams them in) and
    // unregister on OnDestroy. Lookups are O(1). World-rect overlap queries
    // (for cross-tile ops like a paint disk that spans 4 tiles) iterate
    // the candidate index range — bounded by op size, typically 1-4 entries.
    public static class TileAnchorRegistry
    {
        private static readonly Dictionary<Vector2i, TileAnchor> _byIndex
            = new Dictionary<Vector2i, TileAnchor>();

        public static int Count => _byIndex.Count;

        public static IEnumerable<TileAnchor> All => _byIndex.Values;

        public static TileAnchor GetAt(Vector2i tileXZ)
        {
            _byIndex.TryGetValue(tileXZ, out var anchor);
            return anchor;
        }

        public static TileAnchor GetAtWorld(Vector3 worldPos)
        {
            return GetAt(WorldToTileIndex(worldPos));
        }

        // Phase 8a stub. Phase 8c will instantiate a TileAnchor prefab here
        // for ops that need to write to a tile not currently loaded (e.g. a
        // disk-place op that overlaps an unloaded neighbor tile). For 8a,
        // returns null — callers must handle missing anchors gracefully.
        public static TileAnchor GetOrCreate(Vector2i tileXZ)
        {
            if (_byIndex.TryGetValue(tileXZ, out var existing))
                return existing;
            // 8c TODO: spawn the persistent ZNetView prefab at WorldCenterForTile(tileXZ)
            // with ClaimOwnership, register, return. For now we don't auto-spawn.
            return null;
        }

        // Enumerate every tile index whose 64x64m world rect intersects the
        // given world-space XZ rect. Caller passes rect.x/y as world XZ.
        public static IEnumerable<Vector2i> TilesOverlapping(Rect worldXZRect)
        {
            int minX = Mathf.FloorToInt(worldXZRect.xMin / TerrainTile.TILE_SIZE);
            int maxX = Mathf.FloorToInt(worldXZRect.xMax / TerrainTile.TILE_SIZE);
            int minZ = Mathf.FloorToInt(worldXZRect.yMin / TerrainTile.TILE_SIZE);
            int maxZ = Mathf.FloorToInt(worldXZRect.yMax / TerrainTile.TILE_SIZE);

            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                yield return new Vector2i(x, z);
            }
        }

        // Convenience: tile rect derived from a center + radius circular op.
        public static IEnumerable<Vector2i> TilesOverlappingCircle(Vector3 center, float radius)
        {
            var rect = new Rect(
                center.x - radius,
                center.z - radius,
                radius * 2f,
                radius * 2f);
            return TilesOverlapping(rect);
        }

        // World XZ -> tile index. Negative coordinates floor correctly
        // (Mathf.FloorToInt(-0.1f / 64f) = -1, which is what we want).
        public static Vector2i WorldToTileIndex(Vector3 worldPos)
        {
            return new Vector2i(
                Mathf.FloorToInt(worldPos.x / TerrainTile.TILE_SIZE),
                Mathf.FloorToInt(worldPos.z / TerrainTile.TILE_SIZE));
        }

        // World XZ center of a tile. Y is the anchor's fixed Y (set by TileAnchor).
        public static Vector3 WorldCenterForTile(Vector2i tileXZ)
        {
            return new Vector3(
                tileXZ.x * TerrainTile.TILE_SIZE + TerrainTile.TILE_SIZE * 0.5f,
                0f,
                tileXZ.y * TerrainTile.TILE_SIZE + TerrainTile.TILE_SIZE * 0.5f);
        }

        // World-position → tile height lookup. Returns true + height if the
        // tile at this position is loaded AND has a modified vertex at the
        // nearest grid cell. Used by IH ruler-snap to lift the ruler ring
        // onto our floating platforms.
        public static bool TrySampleHeightAtWorld(Vector3 worldPos, out float height)
        {
            height = 0f;
            var tileXZ = WorldToTileIndex(worldPos);
            var anchor = GetAt(tileXZ);
            if (anchor == null || anchor.Tile == null) return false;
            return anchor.Tile.TrySampleHeightAtWorld(worldPos, out height);
        }

        internal static void Register(TileAnchor anchor)
        {
            if (anchor == null) return;
            var key = anchor.TileXZ;
            if (_byIndex.TryGetValue(key, out var existing) && existing != anchor && existing != null)
            {
                // Two anchors at the same tile index — should not happen with
                // deterministic identity. Log and keep the newer one (existing
                // will get destroyed by ZNetScene on the next sync since one
                // of them has the wrong ZDO).
                FiresLogger.LogWarning($"[TileAnchorRegistry] Duplicate anchor at tile ({key.x},{key.y}) — replacing previous registration.");
            }
            _byIndex[key] = anchor;
            FiresLogger.LogVerbose($"[TileAnchorRegistry] Registered anchor at ({key.x},{key.y}) | total={_byIndex.Count}");
        }

        internal static void Unregister(TileAnchor anchor)
        {
            if (anchor == null) return;
            var key = anchor.TileXZ;
            if (_byIndex.TryGetValue(key, out var existing) && existing == anchor)
            {
                _byIndex.Remove(key);
                FiresLogger.LogVerbose($"[TileAnchorRegistry] Unregistered anchor at ({key.x},{key.y}) | remaining={_byIndex.Count}");
            }
        }

        // For tests / debug commands. Wipes the table — does NOT destroy
        // anchor GameObjects (caller's responsibility if desired).
        public static void Clear() => _byIndex.Clear();

        // Drop entries pointing at fake-null Unity objects (TileAnchor MBs
        // that were destroyed during a session teardown but whose OnDestroy
        // didn't fire cleanly, leaving stale dict entries). Called by
        // TileAnchorPrefab.Ensure on session-change so a logout-and-back-in
        // doesn't carry stale refs into the new session.
        //
        // Returns the count removed.
        public static int ScrubFakeNulls()
        {
            List<Vector2i> deadKeys = null;
            foreach (var kvp in _byIndex)
            {
                // Unity's overloaded == catches fake-null (destroyed but
                // C# reference still set). Real-null is impossible here
                // (Register guards against it).
                if (kvp.Value == null)
                    (deadKeys ??= new List<Vector2i>()).Add(kvp.Key);
            }

            if (deadKeys == null) return 0;

            foreach (var k in deadKeys)
                _byIndex.Remove(k);

            FiresLogger.LogInfo($"[TileAnchorRegistry] ScrubFakeNulls: removed {deadKeys.Count} stale entries | remaining={_byIndex.Count}");
            return deadKeys.Count;
        }
    }
}
