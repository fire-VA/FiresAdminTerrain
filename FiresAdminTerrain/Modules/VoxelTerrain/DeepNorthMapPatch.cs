using HarmonyLib;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── DeepNorth must follow the biome MAP, not vanilla's geometry ───────
    // WorldGenerator.IsDeepnorth is a pure distance test on the vanilla layout:
    // Vector2(x, y + 4000).magnitude > 12000 + WorldAngle(x, y) * 100. It never
    // consults GetBiome, so on a world whose biomes come from a map - a migrated
    // BetterContinents world, or a Fires-generated one - it answers about a
    // DeepNorth that is not where the world's DeepNorth actually is.
    //
    // It is not cosmetic. Callers in 1.0: EnvMan twice (which weather table a
    // position gets), TerrainComp.ApplyToHeightmap (whether Cultivate paints
    // snow), and WorldGenerator's river pass (which rivers are frozen).
    //
    // Deliberately does NOT call GetBiome: vanilla GetBiome calls IsDeepnorth,
    // so any fall-through would recurse forever. Both sources read their map
    // array directly instead. ZoneModeGate is not consulted - it is a per-zone
    // gate needing a WorldGenerator instance, not a world-space predicate.
    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.IsDeepnorth))]
    internal static class Patch_WorldGenerator_IsDeepnorth_FiresMap
    {
        private static bool Prefix(float x, float y, ref bool __result)
        {
            if (BcImport.BcMigratedWorld.TryGetBiome(x, y, out Heightmap.Biome painted))
            {
                __result = painted == Heightmap.Biome.DeepNorth;
                return false;
            }

            if (FiresWorldGenBase.Active)
            {
                __result = FiresWorldGenBase.SampleBiome(x, y) == Heightmap.Biome.DeepNorth;
                return false;
            }

            return true;
        }
    }
}
