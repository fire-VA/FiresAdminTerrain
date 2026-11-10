using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace VerdantsAscent.Terrain.Voxel
{
    // CAVE PATHING ON THE SAME LEVEL (0.2.1056; Fire 10-26 picked this from the Voxheim comparison, our own code).
    //
    // Vanilla Pathfinding.SnapToNavMesh first lifts the point to ZoneSystem.GetGroundHeight when it is below it, then samples
    // the navmesh around the lifted point (1.5 / 3 / 6 / 12 m, or 1 m). In a voxel cave that ground height is the surface
    // ABOVE the cave, so a creature or companion inside a cave got its start / end snapped onto the hillside on top and
    // walked the wrong level. The navmesh itself already holds cave floors: vanilla builds tiles from physics colliders on
    // m_layers (which include "terrain", the voxel chunks' layer) over a 6,000 m tall tile.
    //
    // In a voxel zone the lift only happens when the point is inside solid voxel (buried: the surface is the right answer);
    // a point in cave air keeps its own height, so the same samples find the floor on its own level. Swimmers keep vanilla's
    // sea clamp. Off voxel zones the method runs vanilla, unchanged.
    internal static class VoxelCavePath
    {
        private const float LogEvery = 30f;
        private static float s_nextNote;

        [HarmonyPatch(typeof(Pathfinding), "SnapToNavMesh")]
        private static class Pathfinding_SnapToNavMesh
        {
            private static bool Prefix(ref Vector3 point, bool extendedSearchArea, Pathfinding.AgentSettings settings, ref bool __result)
            {
                if (settings == null || !VoxelCaveCover.InVoxelZone(point)) return true;
                Vector3 asked = point;
                float surface = float.NaN;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(point, out float height) && point.y < height)
                {
                    surface = height;
                    // Buried in rock: vanilla's lift is right. In cave air: keep the level.
                    if (!VoxelWorld.TryIsSolid(point.x, point.y + 0.5f, point.z, out bool solid) || solid) return true;
                }
                if (float.IsNaN(surface)) return true;   // not under the surface: vanilla does the same thing
                if (settings.m_canSwim) point.y = Mathf.Max(30f - settings.m_swimDepth, point.y);

                var filter = new NavMeshQueryFilter { agentTypeID = settings.m_build.agentTypeID, areaMask = settings.m_areaMask };
                NavMeshHit hit;
                bool found = false;
                if (extendedSearchArea)
                {
                    foreach (float r in s_ranges)
                        if (NavMesh.SamplePosition(point, out hit, r, filter)) { point = hit.position; found = true; break; }
                }
                else if (NavMesh.SamplePosition(point, out hit, 1f, filter)) { point = hit.position; found = true; }
                __result = found;
                if (found && Time.unscaledTime >= s_nextNote)
                {
                    s_nextNote = Time.unscaledTime + LogEvery;
                    FiresLogger.LogDebug($"[FiresVoxel] cave path: snapped ({asked.x:0.0}, {asked.y:0.0}, {asked.z:0.0}) to the same level (dy {point.y - asked.y:0.0} m) instead of the surface (dy {surface - asked.y:0.0} m)");
                }
                return false;
            }

            private static readonly float[] s_ranges = { 1.5f, 3f, 6f, 12f };
        }
    }
}
