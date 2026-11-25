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
    // 0.2.1518 (Fire 11-23 snow seams, chunk borders on HD ground): only a modifier standing ON a skyland is kept off. On normal
    // ground the height test alone was one-sided: GetWorldHeight (Heightmap.cs:836-846) is false outside a heightmap's own
    // vertices, so the heightmap holding a location's LevelTerrain on a slope (> 8 m above its ground) dropped it while the
    // neighbour applied it, and the shared border vertices split (live log: LevelTerrain (1109,101,781) blocked, plain height
    // 8.42 m apart at x = 1120). Normal ground now keeps vanilla's answer on both sides.
    internal static class FatIslandGroundGuard
    {
        private const float FloatAbove = 8f;
        private static readonly HashSet<int> s_said = new HashSet<int>();
        private static readonly HashSet<int> s_saidKept = new HashSet<int>();
        private static int s_blocked, s_kept;

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.TerrainVSModifier))]
        private static class Patch_TerrainVSModifier
        {
            private static void Postfix(Heightmap __instance, TerrainModifier modifier, ref bool __result)
            {
                if (!__result || modifier == null || __instance == null) return;
                Vector3 p = modifier.transform.position;
                if (!__instance.GetWorldHeight(p, out float ground) || p.y <= ground + FloatAbove) return;
                int key = modifier.GetInstanceID();
                if (!FiresSkyIslands.TryIslandAt(p, 4f, out uint id, out _, out _))
                {
                    // 0.2.1518: not on a skyland - vanilla's answer stands (both neighbours apply it)
                    s_kept++;
                    if (s_saidKept.Add(key))
                        FiresLogger.LogDebug($"[IslandPaint] normal ground, vanilla kept {s_kept} (0.2.1518): TerrainModifier '{Utils.GetPrefabName(modifier.gameObject)}' at ({p.x:0},{p.y:0},{p.z:0}) is {p.y - ground:0} m above the heightmap ground, no skyland - " +
                                             $"level {modifier.m_level} r {modifier.m_levelRadius:0.#}, smooth {modifier.m_smooth} r {modifier.m_smoothRadius:0.#}, paint {modifier.m_paintCleared} r {modifier.m_paintRadius:0.#}");
                    return;
                }
                __result = false;
                s_blocked++;
                if (s_said.Add(key))
                {
                    FiresLogger.LogDebug($"[IslandPaint] ground writes under island {id:x8}: 0 (blocked {s_blocked}: TerrainModifier '{Utils.GetPrefabName(modifier.gameObject)}' at ({p.x:0},{p.y:0},{p.z:0}) is {p.y - ground:0} m above the ground heightmap - level/smooth/paint r {modifier.m_paintRadius:0.#} kept off the ground)");
                }
            }
        }
    }
}
