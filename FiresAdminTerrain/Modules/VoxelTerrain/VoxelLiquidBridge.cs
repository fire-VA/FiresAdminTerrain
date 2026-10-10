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
    // Called from the postfix of FAT's one LiquidVolume.UpdateHeights hook (FatVanillaHooks), before the clamp guard.
    internal static class LiquidVoxelSurface
    {
        internal static void Apply(LiquidVolume __instance)
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
                if (mutex != null) System.Threading.Monitor.Enter(mutex);   // reentrant for this thread — safe if caller holds it
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
                    if (mutex != null) System.Threading.Monitor.Exit(mutex);
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxel] UpdateHeights voxel bridge threw: {ex.Message}");
            }
        }

        // 0.2.834 (Fire 10-07, TarPit1 underground at (-773,47,-3459): "they stretch and look fucked because of the way they drop
        // into place ... they need to sit like a cave pool instead"): a location's liquid (tar) in an underground cavern got its
        // ground from rays and voxel samples over +-m_maxDepth (10 m): a column the ray missed read a 10 m pit, and the liquid
        // poured into it as tall stretched sheets. Underground, the ground is the cavern floor within FloorBand of the volume's
        // own height, a column with no floor there is the floor (flat), rock reaching above it stays a dam, and nothing is
        // deeper than PoolDepth: a flat pool on the cavern floor.
        private const float FloorBand = 3f, PoolDepth = 1.5f;
        private static int s_flattened;

        internal static void FlattenUnderground(LiquidVolume __instance)
        {
            try
            {
                if (VoxelWorld.ChunkCount == 0 || __instance == null) return;
                Vector3 p = __instance.transform.position;
                // A location's liquid in its dome only (a player's poured water in a natural cave keeps the normal path).
                if (!FatUndergroundLocations.UnderDome(p) && __instance.GetComponentInParent<Location>() == null) return;
                if (!FatUndergroundLocations.IsUnderground(p)) return;
                var heights = FiresLiquidGridOps.Heights(__instance);
                int num = __instance.m_width + 1;
                if (heights == null || heights.Count != num * num) return;
                float scale = __instance.m_scale, half = __instance.m_width * scale * 0.5f, maxD = __instance.m_maxDepth;
                var mutex = FiresLiquidGridOps.MeshDataLock(__instance);
                if (mutex != null) System.Threading.Monitor.Enter(mutex);
                try
                {
                    for (int z = 0; z < num; z++)
                        for (int x = 0; x < num; x++)
                        {
                            float wx = p.x - half + x * scale, wz = p.z - half + z * scale, rel;
                            if (VoxelWorld.TrySampleSurfaceY(wx, wz, p.y + FloorBand, p.y - FloorBand, out float surfY, out bool wall)) rel = surfY - p.y;
                            else rel = wall ? maxD : 0f;
                            heights[z * num + x] = Mathf.Clamp(rel, -PoolDepth, maxD);
                        }
                    FiresLiquidGridOps.Dirty(__instance) = true;
                }
                finally { if (mutex != null) System.Threading.Monitor.Exit(mutex); }
                if (s_flattened++ == 0)
                    FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[Underground] liquid '{0}' at ({1:0.0},{2:0.0},{3:0.0}) laid flat on the cavern floor (band +-{4:0} m, at most {5:0.0} m deep; first of this session)",
                        __instance.name, p.x, p.y, p.z, FloorBand, PoolDepth));
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxel] underground liquid flatten threw: {ex.Message}");
            }
        }
    }
}
