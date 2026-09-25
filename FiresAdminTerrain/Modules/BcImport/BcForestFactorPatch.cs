using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    // A migrated BetterContinents world's forest cover. BC scales the query position by ForestScale and then maps
    // vanilla's answer through its forest map, so both halves are needed: the prefix stretches the noise, the
    // postfix applies the map. Nothing else in FAT hooks GetForestFactor.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetForestFactor))]
    internal static class Patch_WorldGenerator_GetForestFactor_BcImport
    {
        // __state carries the UNSCALED position to the postfix. Recovering it by dividing the scaled
        // position back would not round-trip exactly in float, and divides by zero at ForestScale = 0.
        private static void Prefix(ref Vector3 pos, out Vector3 __state)
        {
            __state = pos;
            if (!BcMigratedWorld.Active) return;
            pos *= BcMigratedWorld.Generator.Settings.ForestScale;
        }

        private static void Postfix(ref float __result, Vector3 __state)
        {
            if (!BcMigratedWorld.Active) return;
            BcMigratedWorld.TryApplyForest(__state, ref __result);
        }
    }
}
