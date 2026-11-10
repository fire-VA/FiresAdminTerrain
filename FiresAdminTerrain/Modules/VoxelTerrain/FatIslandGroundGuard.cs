using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1099 (Fire, shot 558 under a skyland on SuperFlatSkyCaves3: "were still applying ground paint from skylands it seems" -
    // a tan dirt ring with a darker band and a green centre on the flat ground under the island, no water). Vanilla picks the
    // TerrainModifiers a heightmap applies by x,z only (Heightmap.ApplyModifiers -> TerrainVSModifier, Heightmap.cs:346-396): a
    // location's level / smooth / paint ring placed ON a skyland (0.2.1096 gives every skyland a site) was applied to the GROUND
    // heightmap 100+ m below it, so the ring painted the ground (and its level op would have pulled the hidden ground heights).
    // A modifier standing more than FloatAbove metres above the heightmap's own ground at its x,z never touches that heightmap.
    internal static class FatIslandGroundGuard
    {
        private const float FloatAbove = 8f;
        private static readonly HashSet<int> s_said = new HashSet<int>();
        private static int s_blocked;

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.TerrainVSModifier))]
        private static class Patch_TerrainVSModifier
        {
            private static void Postfix(Heightmap __instance, TerrainModifier modifier, ref bool __result)
            {
                if (!__result || modifier == null || __instance == null) return;
                Vector3 p = modifier.transform.position;
                if (!__instance.GetWorldHeight(p, out float ground) || p.y <= ground + FloatAbove) return;
                __result = false;
                s_blocked++;
                int key = modifier.GetInstanceID();
                if (s_said.Add(key))
                {
                    string island = FiresSkyIslands.TryIslandAt(p, 4f, out uint id, out _, out _) ? id.ToString("x8") : "none";
                    FiresLogger.LogDebug($"[IslandPaint] ground writes under island {island}: 0 (blocked {s_blocked}: TerrainModifier '{Utils.GetPrefabName(modifier.gameObject)}' at ({p.x:0},{p.y:0},{p.z:0}) is {p.y - ground:0} m above the ground heightmap - level/smooth/paint r {modifier.m_paintRadius:0.#} kept off the ground)");
                }
            }
        }
    }
}
