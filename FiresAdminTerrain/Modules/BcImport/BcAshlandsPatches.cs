using HarmonyLib;

namespace VerdantsAscent.Terrain.BcImport
{
    // Where the Ashlands are on a converted BetterContinents world, as BetterContinents decides it (0.7.30 and 0.9.4 alike):
    //  - with a heat map (HeatMapScale > 0): Ashlands wherever the heat map is above 0 (IsAshlands), and the sea's Ashlands
    //    gradient is that heat, a hair below so a cold sea stays harmless (GetAshlandsOceanGradient);
    //  - with a biome map but no heat map: the biome map's own Ashlands, and vanilla's crescent where the map leaves it None.
    // Without this a converted world kept vanilla's fixed southern crescent: vanilla GetBiome asks IsAshlands before every
    // other rule, so wherever the biome map is None (black) the ground came out Ashlands, or not, unlike BetterContinents'
    // (the 2026-10-05 reference test: Ashlands <-> Mistlands / Black Forest at 7-10 km, heights 10-60 m apart).
    [HarmonyPatch]
    internal static class BcAshlandsPatches
    {
        // BetterContinents: Priority.VeryHigh and after Expand World Data's own IsAshlands patch.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        [HarmonyAfter("expand_world_data")]
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.IsAshlands))]
        private static bool IsAshlands(float x, float y, ref bool __result)
        {
            var gen = BcMigratedWorld.Generator;
            if (gen == null) return true;
            var s = gen.Settings;
            if (gen.Maps?.Heat != null && s.HeatMapScale > 0f)
            {
                __result = gen.HeatAt(s.Normalize(x), s.Normalize(y)) > 0f;
                return false;
            }
            if (gen.Maps?.Biome != null)
            {
                var biome = gen.MapBiomeAt(s.Normalize(x), s.Normalize(y));
                if (biome == Heightmap.Biome.None) return true;
                __result = biome == Heightmap.Biome.AshLands;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetAshlandsOceanGradient), new[] { typeof(float), typeof(float) })]
        private static bool AshlandsOceanGradient(float x, float y, ref float __result)
        {
            var gen = BcMigratedWorld.Generator;
            if (gen == null || gen.Maps?.Heat == null || gen.Settings.HeatMapScale <= 0f) return true;
            __result = gen.HeatAt(gen.Settings.Normalize(x), gen.Settings.Normalize(y)) - 0.00001f;
            return false;
        }
    }
}
