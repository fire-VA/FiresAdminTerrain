using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Diagnostic timing for "why does my tile mesh take forever to pop in?".
    //
    // Records Time.realtimeSinceStartup the first time vanilla ZoneSystem
    // returns success from SpawnZone for each zone key. TileAnchor.Awake +
    // TileMeshRenderer.RebuildMesh log the delta against this so we can see
    // the actual latency between "zone became active" and "mesh visible".
    //
    // Cleared on world unload so a logout/login cycle resets the timeline.
    // Zero overhead when our anchors are not in the zone.
    public static class ZoneStreamTiming
    {
        private static readonly Dictionary<Vector2i, float> _zoneActivatedAt =
            new Dictionary<Vector2i, float>();

        // Anchor at world XZ (TileXZ.x * 64 + 32, _, TileXZ.y * 64 + 32) lives
        // in zone (TileXZ.x, TileXZ.y) too — both grids are 64m, our anchor
        // sits at the tile center which is also the zone center for tiles
        // whose origin is on the zone boundary (which they always are because
        // tile and zone grids are coincident at 64m).
        //
        // Vanilla zone math: zone = floor((world + 32) / 64). Our anchor at
        // (X*64 + 32) → (X*64 + 32 + 32) / 64 = X + 1, floored = X + 1.
        // Wait — that gives the WRONG zone. Let me re-check.
        //
        // Actually our anchor sits at TILE center which is offset 32 from
        // tile origin. Tile origin at (X*64, 0, Y*64). Tile center at
        // (X*64+32, _, Y*64+32). Zone for that center = floor((X*64+32+32)/64),
        // floor((Y*64+32+32)/64) = floor((X*64+64)/64), floor((Y*64+64)/64)
        // = X+1, Y+1. So the anchor sits in zone (X+1, Y+1), NOT zone (X, Y).
        //
        // This works fine for ZoneSystem streaming (it still picks the right
        // zone), but for THIS timing helper we want to match TileXZ to the
        // zone vanilla actually streams it in. Hence the +1 offset below.
        public static Vector2i ZoneFromAnchor(Vector2i tileXZ)
        {
            // Anchor world position is at tile center (tileXZ * 64 + 32).
            // Vanilla GetZone = floor((p + 32) / 64) = tileXZ + 1.
            return new Vector2i(tileXZ.x + 1, tileXZ.y + 1);
        }

        public static float MillisSinceZoneActive(Vector2i zoneKey)
        {
            if (_zoneActivatedAt.TryGetValue(zoneKey, out float t))
                return (Time.realtimeSinceStartup - t) * 1000f;
            return -1f;
        }

        // Postfix on ZoneSystem.SpawnZone — fires every time SpawnZone is
        // called for a zone, regardless of success/failure. We only record
        // the FIRST success per zone (subsequent SpawnZone calls for the
        // same zone are TTL pokes, not fresh activations).
        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        private static class ZoneSystem_SpawnZone_RecordActivation
        {
            // SpawnZone signature: bool SpawnZone(Vector2i zoneID, SpawnMode mode, out GameObject root)
            // __result == true means the zone was successfully spawned.
            private static void Postfix(Vector2i zoneID, GameObject root, bool __result)
            {
                if (!__result || root == null) return;
                if (_zoneActivatedAt.ContainsKey(zoneID)) return;
                _zoneActivatedAt[zoneID] = Time.realtimeSinceStartup;
                // Verbose because this fires for every zone activation in the
                // world, not just ones with our anchors. The matching delta
                // log on tile spawn is the useful signal.
                FiresLogger.LogVerbose($"[ZoneStreamTiming] zone ({zoneID.x},{zoneID.y}) activated at T={Time.realtimeSinceStartup:F2}s (frame={Time.frameCount}).");
            }
        }

        // Reset on world unload so a fresh login starts the timeline at zero.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private static class ZNet_Shutdown_ResetZoneStreamTiming
        {
            private static void Prefix()
            {
                if (_zoneActivatedAt.Count > 0)
                {
                    FiresLogger.LogInfo($"[ZoneStreamTiming] ZNet.Shutdown — clearing {_zoneActivatedAt.Count} zone activation timestamps.");
                    _zoneActivatedAt.Clear();
                }
            }
        }
    }
}
