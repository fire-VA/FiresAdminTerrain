using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Vanilla character physics patches so the player can walk freely UNDER
    // custom floating tiles without being forced up to the vanilla heightmap
    // surface by safety teleports or ground-snap.
    //
    // 1. Character.UnderWorldCheck prefix
    //    Vanilla snaps any character below `worldHeight - 1m` back up to the
    //    heightmap. Walking under a 50m-tall floating platform = always under
    //    the heightmap (because there's no heightmap above us, the heightmap
    //    is the ground below the platform). Without this patch, the snap
    //    fires every frame and the player can never stand under their builds.
    //    Skip the check when a custom tile is anywhere near (48m XZ radius).
    //
    // NOTE: A previous version of this file also patched
    // `Heightmap.GetHeight(Vector3, out float)`. That patch had the WRONG
    // signature spec (typeof(Vector3), typeof(float) instead of the
    // explicit Out-byref form) which made Harmony throw
    // "Undefined target method" at PatchAll time — and Harmony's PatchAll
    // aborts the rest of the assembly on first failure. The cascade
    // skipped TerrainMaterialCache's RebuildRenderMesh capture postfix +
    // every other patch downstream, so tiles spawned with the Standard
    // fallback material instead of Custom/Heightmap (looked like dark
    // gray "water"). The patch is removed — Character.UnderWorldCheck +
    // the existing ZoneSystem.GetGroundHeight patches in TileHammerCompat
    // are sufficient for the walk-under behavior the user wants. If
    // tile-height reads in motion code prove needed later, re-add via
    // explicit ArgumentType.Out spec to avoid the same PatchAll abort.
    internal static class TileCharacterPhysicsPatches
    {
        // Match radius for "near custom terrain" detection. Roughly the
        // diagonal of a 64m tile — gives us a generous skip window.
        // Larger is safer (no over-eager snap); smaller is cheaper.
        private const float NEAR_TILE_RADIUS = 48f;

        // ── 1. UnderWorldCheck skip ──────────────────────────────────────
        // Called after VoxelUnderWorld from FAT's one Character.UnderWorldCheck hook (FatVanillaHooks).
        internal static bool AllowUnderWorldCheck(Character __instance)
        {
            if (__instance == null) return true;

            // Only worry about the local player — NPCs / monsters don't
            // place builds, and we don't want them clipping into ground
            // because of this skip.
            if (__instance != Player.m_localPlayer) return true;

            if (IsNearCustomTile(__instance.transform.position, NEAR_TILE_RADIUS))
            {
                // Skip vanilla — let the player stay below the heightmap.
                return false;
            }
            return true;
        }

        // ── helpers ─────────────────────────────────────────────────────

        // True when any TileAnchor's footprint covers (or is near) the
        // given world XZ. Used as a fast gate on UnderWorldCheck so the
        // skip only fires when there's actually a custom build to be
        // standing under. Pure XZ check — vertical separation doesn't
        // matter because UnderWorldCheck is itself a vertical thing.
        private static bool IsNearCustomTile(Vector3 worldPos, float radius)
        {
            // First: cheap check — is there a tile exactly at this position?
            var tileXZ = TileAnchorRegistry.WorldToTileIndex(worldPos);
            if (TileAnchorRegistry.GetAt(tileXZ) != null) return true;

            // Wider scan: any tile within the radius? 64m tiles → at radius
            // 48m we need at most a 3x3 sweep around the player's tile.
            int tilesEachWay = Mathf.CeilToInt(radius / TerrainTile.TILE_SIZE);
            for (int dz = -tilesEachWay; dz <= tilesEachWay; dz++)
            for (int dx = -tilesEachWay; dx <= tilesEachWay; dx++)
            {
                var neighbor = new Vector2i(tileXZ.x + dx, tileXZ.y + dz);
                if (TileAnchorRegistry.GetAt(neighbor) != null) return true;
            }
            return false;
        }
    }
}
