using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Voxel
{
    // Round-87 — make LiquidVolume water READ voxel terrain exactly.
    //
    // Vanilla LiquidVolume.UpdateHeights raycasts DOWN per cell from
    // transform.y + m_maxDepth, and a MISSED ray defaults the cell's ground to
    // −m_maxDepth (20m below!). Voxel geometry breaks those rays two ways:
    //   * a mound top ABOVE the ray origin puts the origin INSIDE the mesh —
    //     the downward ray exits through backfaces with no hit → miss → the
    //     water sees a phantom 20m pit and SINKS THROUGH the mound ("spring
    //     placed in the air");
    //   * right after a brush, the baked collider lags the density by a few
    //     frames (async pipeline) → transient misses.
    //
    // Fix: after vanilla's raycast pass, overwrite each cell's ground height
    // with the voxel DENSITY surface wherever a voxel column covers it and is
    // higher — same interpolated crossing the mesh renders, no physics, no
    // races. Solid-at-top columns become full-height walls (+maxDepth), so
    // water dams against thick voxel rock just like a cliff.
    [HarmonyPatch(typeof(LiquidVolume), "UpdateHeights")]
    internal static class LiquidVolume_UpdateHeights_VoxelSurface
    {
        [HarmonyPostfix]
        private static void Postfix(LiquidVolume __instance)
        {
            try
            {
                if (VoxelWorld.ChunkCount == 0) return;

                var heights = FiresLiquidGridOps.Heights(__instance);
                if (heights == null) return;
                int num = __instance.m_width + 1;
                if (heights.Count != num * num) return;

                float scale = __instance.m_scale;
                float half = __instance.m_width * scale * 0.5f;
                float maxD = __instance.m_maxDepth;
                Vector3 p = __instance.transform.position;
                float topY = p.y + maxD;
                float botY = p.y - maxD;

                bool changed = false;
                var mutex = FiresLiquidGridOps.MeshDataLock(__instance);
                if (mutex != null) mutex.WaitOne();   // reentrant for this thread — safe if caller holds it
                try
                {
                    for (int z = 0; z < num; z++)
                    {
                        float wz = p.z - half + z * scale;
                        for (int x = 0; x < num; x++)
                        {
                            float wx = p.x - half + x * scale;
                            if (!VoxelWorld.TrySampleSurfaceY(wx, wz, topY, botY, out float surfY, out bool wall))
                            {
                                if (!wall) continue;
                                surfY = topY;   // solid all the way up — full-height wall
                            }
                            float rel = Mathf.Clamp(surfY - p.y, -maxD, maxD);
                            int i = z * num + x;
                            if (rel > heights[i] + 0.01f)
                            {
                                heights[i] = rel;
                                changed = true;
                            }
                        }
                    }
                    if (changed)
                    {
                        FiresLiquidGridOps.Dirty(__instance) = true;
                    }
                }
                finally
                {
                    if (mutex != null) mutex.ReleaseMutex();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[FiresVoxel] UpdateHeights voxel bridge threw: {ex.Message}");
            }
        }
    }
}
