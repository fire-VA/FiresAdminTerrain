using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Forces snow-biome footstep FX (sound + VFX) when the player walks
    /// over any LandSnowController footprint, regardless of underlying biome.
    ///
    /// Vanilla decision path (verified 2026-05-25):
    ///   FootStep.OnFoot → FootStep.GetGroundMaterial → Character.m_lastGroundCollider
    ///     → if collider has Heightmap → Heightmap.GetGroundMaterial(normal, point)
    ///       → biome switch (Mountain/DeepNorth → Snow)
    ///
    /// Because LandSnowController is a visual-only overlay with NO collider,
    /// the player's m_lastGroundCollider is always the vanilla Heightmap
    /// beneath. So patching Heightmap.GetGroundMaterial postfix and
    /// converting the result to Snow when `point` lies inside any active
    /// LandSnow disk gives us the right footstep FX on any biome.
    ///
    /// We patch the PUBLIC Heightmap method rather than the PRIVATE
    /// FootStep.GetGroundMaterial — same effect, cleaner Harmony target.
    /// </summary>
    [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetGroundMaterial))]
    internal static class LandSnow_HeightmapGroundMaterial_Patch
    {
        private static void Postfix(Vector3 point, ref FootStep.GroundMaterial __result)
        {
            // Already snow? Nothing to do — Mountain/DeepNorth biome path.
            if (__result == FootStep.GroundMaterial.Snow) return;

            var all = LandSnowController.AllLandSnow;
            if (all == null || all.Count == 0) return;

            // Point-in-footprint test against every active LandSnow disk.
            // Cheap: each disk is a circle test, count is small (single-digit
            // per zone in normal use).
            for (int i = 0; i < all.Count; i++)
            {
                var ctrl = all[i];
                if (ctrl == null) continue;
                if (ctrl.ContainsPosition(point))
                {
                    __result = FootStep.GroundMaterial.Snow;
                    return;
                }
            }
        }
    }
}
