using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // CAVE FIRES + ROOF ON VOXEL GROUND (0.2.1055; Fire 10-26 picked these from the Voxheim comparison, our own code).
    //
    // Fires: vanilla Fireplace.CheckUnderTerrain puts a fire out when Heightmap.GetHeight at its x,z is above it. On a
    // voxel zone that heightmap is the hidden vanilla ground, so every fire in a cave below the old surface went out. In a
    // voxel zone the heightmap test is replaced by the real question: is the fire's point inside solid voxel? The rest of
    // vanilla's check (the 0.5 m solid ray above the fire, the smoke spawner's blocked test) is kept as it is: both test
    // real colliders, so a fire jammed under a low rock still goes out, as it should.
    //
    // Roof: Cover.IsUnderRoof / GetCoverForPoint already include the "terrain" layer, which our voxel chunks use
    // (VoxelChunkView), so rested / wet / rain on fires already count voxel rock. Character.IsUnderRoof (the Ashlands day
    // heat) uses Default + static_solid + piece only: in a voxel zone a voxel ceiling within its 20 m now counts too.
    // Off voxel zones every method runs vanilla, unchanged.
    internal static class VoxelCaveCover
    {
        private const float LogEvery = 30f;
        private static readonly Dictionary<int, float> s_fireNoted = new Dictionary<int, float>();
        private static float s_nextRoofNote;
        private static int s_terrainMask = -1;
        private static readonly RaycastHit[] s_hits = new RaycastHit[8];

        // A voxel zone: the streamer swapped it to voxel colliders, or the world is voxel only.
        internal static bool InVoxelZone(Vector3 p)
            => VoxelOnlyMode.Active || VoxelZoneStreamer.IsZoneSolid(ZoneSystem.GetZone(p));

        [HarmonyPatch(typeof(Fireplace), "CheckUnderTerrain")]
        private static class Fireplace_CheckUnderTerrain
        {
            private static bool Prefix(Fireplace __instance)
            {
                if (__instance == null || __instance.m_disableCoverCheck) return true;
                Vector3 p = __instance.transform.position;
                if (!InVoxelZone(p)) return true;
                bool blocked = false;
                // Inside solid voxel (the point a few cm above the fire's base, where vanilla's offset sits) = buried.
                Vector3 test = p + Vector3.up * Mathf.Max(0.05f, -__instance.m_checkTerrainOffset);
                if (VoxelWorld.TryIsSolid(test.x, test.y, test.z, out bool solid) && solid) blocked = true;
                else if (Physics.Raycast(p + Vector3.up * __instance.m_coverCheckOffset, Vector3.up, out RaycastHit _, 0.5f, SolidMask()))
                    blocked = true;
                else if (__instance.m_smokeSpawner != null && __instance.m_smokeSpawner.IsBlocked())
                    blocked = true;
                __instance.m_blocked = blocked;
                if (!blocked && FiresLogger.DebugEnabled && Heightmap.GetHeight(p, out float h) && h > p.y + __instance.m_checkTerrainOffset)
                {
                    int id = __instance.GetInstanceID();
                    if (!s_fireNoted.TryGetValue(id, out float next) || Time.time >= next)
                    {
                        s_fireNoted[id] = Time.time + LogEvery;
                        FiresLogger.LogDebug($"[FiresVoxel] cave fire: {Utils.GetPrefabName(__instance.gameObject)} at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}) kept lit under voxel roof (smoke point in air)");
                    }
                }
                return false;
            }

            private static int s_solidMask;
            private static int SolidMask()
            {
                if (s_solidMask == 0) s_solidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
                return s_solidMask;
            }
        }

        [HarmonyPatch(typeof(Character), "IsUnderRoof")]
        private static class Character_IsUnderRoof
        {
            private static void Postfix(Character __instance, ref bool __result)
            {
                if (__result || __instance == null) return;
                Vector3 p = __instance.transform.position;
                if (!InVoxelZone(p)) return;
                if (s_terrainMask < 0) s_terrainMask = LayerMask.GetMask("terrain");
                int n = Physics.RaycastNonAlloc(p + Vector3.up * 0.2f, Vector3.up, s_hits, 20f, s_terrainMask);
                for (int i = 0; i < n; i++)
                {
                    if (s_hits[i].collider == null || s_hits[i].collider.GetComponentInParent<VoxelChunkView>() == null) continue;
                    __result = true;
                    if (FiresLogger.DebugEnabled && Time.time >= s_nextRoofNote)
                    {
                        s_nextRoofNote = Time.time + LogEvery;
                        FiresLogger.LogDebug($"[FiresVoxel] roof: voxel rock overhead at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}) counts as roof (dist {s_hits[i].distance:0.0} m)");
                    }
                    return;
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ZNet_OnDestroy_ClearFires
        {
            private static void Postfix() => s_fireNoted.Clear();
        }
    }
}
