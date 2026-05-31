using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Per-tile biome override. When the player paints a biome onto one of
    // our tiles via va_brush_biome, that tile's TerrainTile.BiomeOverride
    // field carries the chosen Heightmap.Biome value. This patch makes
    // every system that asks "what biome is at this world position?" see
    // our value when the position is over a painted tile — without it,
    // vanilla code keeps returning the underlying biome of the world
    // beneath the skyland.
    //
    // Two vanilla entry points cover all callers I can find:
    //
    //   1. Heightmap.FindBiome(Vector3 point) — preferred by ZoneSystem,
    //      AI, ambient sound, lighting, vegetation generation, etc.
    //   2. WorldGenerator.GetBiome(Vector3 point) — used by some systems
    //      that bypass Heightmap. Less hot but we patch it for parity.
    //
    // Both prefixes do a fast TileAnchorRegistry lookup — O(1) dict +
    // one byte test. If no override, fall through to vanilla.
    public static class TileBiomeOverride
    {
        // Vanilla Heightmap.FindBiome(Vector3) — primary biome query.
        // Returns Heightmap.Biome. We override the return value when the
        // world position falls inside a tile whose BiomeOverride != 0.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.FindBiome), new[] { typeof(Vector3) })]
        private static class Heightmap_FindBiome_TileOverride
        {
            private static bool Prefix(Vector3 point, ref Heightmap.Biome __result)
            {
                if (!TryGetTileBiome(point, out var biome)) return true; // fall through
                __result = biome;
                return false;
            }
        }

        // WorldGenerator.GetBiome(Vector3) — secondary entry point.
        // Some systems (e.g. spawn rules in EW Spawns) prefer this over
        // Heightmap. Same override semantics.
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(Vector3) })]
        private static class WorldGenerator_GetBiome_TileOverride
        {
            private static bool Prefix(Vector3 point, ref Heightmap.Biome __result)
            {
                if (!TryGetTileBiome(point, out var biome)) return true;
                __result = biome;
                return false;
            }
        }

        // Single source of truth for the lookup. Returns true + the biome
        // if the world position is over a loaded tile with an active
        // override. The tile-anchor registry is keyed by tile index (XZ)
        // so this is a single dict lookup — cheap enough to call from
        // hot biome-query paths.
        private static bool TryGetTileBiome(Vector3 worldPos, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            var tileXZ = TileAnchorRegistry.WorldToTileIndex(worldPos);
            var anchor = TileAnchorRegistry.GetAt(tileXZ);
            if (anchor == null || anchor.Tile == null) return false;

            // Per-vertex sampling (codec v3). Returns 0 if the nearest vertex
            // wasn't painted. Falls back to tile-wide BiomeOverride for the
            // v2-style "whole tile is biome X" semantics — supports old
            // ZDOs that haven't been re-painted under the v3 brush yet.
            ushort raw = anchor.Tile.SampleBiomeAtWorld(worldPos);
            if (raw == 0) raw = anchor.Tile.BiomeOverride;
            if (raw == 0) return false;   // no override at this vertex
            biome = (Heightmap.Biome)raw;
            return true;
        }
    }
}
