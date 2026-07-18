using System;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // W1.1 — grass/clutter on voxel ground.
    //
    // Vanilla ClutterSystem.GetGroundInfo raycasts the place mask and then does
    //   hmap = hit.collider.GetComponent<Heightmap>();
    //   biome = hmap.GetBiome(point);          // ← NRE when the hit is OUR collider
    // Our voxel terrain (terrain layer) has no Heightmap component, so every
    // grass candidate over wrapped/brushed voxel ground THREW and no clutter
    // spawned ("leaving and coming back, the grass is gone").
    //
    // A finalizer recovers precisely that case with ZERO happy-path cost (no
    // extra raycasts): point/normal were already assigned before the throw;
    // the wrapped zone's Heightmap component is still ALIVE as the data API,
    // so FindHeightmap(point) supplies hmap + biome and clutter proceeds
    // exactly as if it had hit vanilla ground.
    [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo))]
    internal static class ClutterSystem_GetGroundInfo_VoxelGround
    {
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception,
            Vector3 p, ref Vector3 point, ref Vector3 normal,
            ref Heightmap hmap, ref Heightmap.Biome biome, ref bool __result)
        {
            if (__exception == null) return null;
            if (!(__exception is NullReferenceException)) return __exception;

            try
            {
                hmap = Heightmap.FindHeightmap(point);
                if (hmap != null)
                {
                    biome = hmap.GetBiome(point);
                }
                else if (WorldGenerator.instance != null)
                {
                    biome = WorldGenerator.instance.GetBiome(point.x, point.z);
                }
                else
                {
                    biome = Heightmap.Biome.Meadows;
                }
                __result = hmap != null;
            }
            catch
            {
                __result = false;
            }
            return null;   // swallow — recovered
        }
    }
}
