using HarmonyLib;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.BcImport
{
    /// <summary>Whether the Ocean ring 1.0 carves between the main landmass and Ashlands / Deep North stays.</summary>
    public enum FiresOceanGap
    {
        /// <summary>Removed on a migrated BetterContinents world, kept on any other. A BC map drew its own land out
        /// to the edge and never asked for a ring of Ocean through it.</summary>
        Auto = 0,
        Keep = 1,
        Remove = 2,
    }

    // 1.0 cuts an Ocean ring around Ashlands (south) and Deep North (north) at 12,000m +/- a WorldAngle wobble:
    //
    //   private double CreateAshlandsGap(float wx, float wy)      -> 0 at the ring, 1 away from it
    //   public static double CreateDeepNorthGap(float wx, float wy)
    //
    // The result multiplies terrain height, so 0 at the ring is what sinks it to Ocean. Returning 1 removes the gap.
    //
    // A BetterContinents map has its own coastline everywhere, so the ring runs straight through authored land -
    // which is the band visible at the top and bottom of a converted world's map.
    //
    // COMPOSES WITH ExpandWorldData, which owns the same two methods. EWD applies a PREFIX returning false only
    // while its own "Ashlands gap"/"Deep North gap" settings are false (they default true = keep the gap), and
    // unpatches when they go back. So this is a POSTFIX: when EWD has already removed the gap we agree with it,
    // when it has not we remove it ourselves, and when this setting says Keep we write nothing and EWD still
    // decides. Neither mod has to know the other is there, and nothing is unpatched out from under anyone.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal static class BcOceanGap
    {
        private const double NoGap = 1.0;

        internal static bool Removed
        {
            get
            {
                var mode = FiresVoxelConfig.OceanGap?.Value ?? FiresOceanGap.Auto;
                if (mode == FiresOceanGap.Remove) return true;
                if (mode == FiresOceanGap.Keep) return false;
                return BcMigratedWorld.Active;
            }
        }

        [HarmonyPatch(typeof(WorldGenerator), "CreateAshlandsGap")]
        internal static class Patch_WorldGenerator_CreateAshlandsGap_FiresGap
        {
            [HarmonyPostfix]
            private static void Postfix(ref double __result)
            {
                if (Removed) __result = NoGap;
            }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.CreateDeepNorthGap))]
        internal static class Patch_WorldGenerator_CreateDeepNorthGap_FiresGap
        {
            [HarmonyPostfix]
            private static void Postfix(ref double __result)
            {
                if (Removed) __result = NoGap;
            }
        }
    }
}
