using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Harmony patches to make vanilla ships/boats work with custom water.
    /// Patches Floating.GetWaterLevel and Ship.UpdateWaterForce to check custom water.
    /// </summary>
    [HarmonyPatch]
    public static class WaterBoatSupport
    {
        #region Ship.CustomFixedUpdate Patch

        /// <summary>
        /// Patches Ship.CustomFixedUpdate to apply river current forces to ships in custom water.
        /// This is called every physics update for ships.
        /// </summary>
        [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
        [HarmonyPostfix]
        private static void Ship_CustomFixedUpdate_Postfix(Ship __instance, float fixedDeltaTime)
        {
            // Check if we're in a custom river
            Vector3 shipPos = __instance.transform.position;
            var river = CustomWaterManager.GetRiverAt(shipPos);

            if (river != null)
            {
                // Get the ship's rigidbody
                var body = __instance.GetComponent<Rigidbody>();
                if (body == null) return;

                // Get flow force at ship position
                Vector3 flowForce = river.GetFlowForceAt(shipPos);

                if (flowForce.sqrMagnitude > 0.01f)
                {
                    // Check how deep the ship is in water
                    float customWaterLevel = river.GetWaterSurfaceAt(shipPos);
                    float depth = customWaterLevel - shipPos.y;

                    if (depth > 0f)
                    {
                        // Scale force by how submerged the ship is
                        float submersionFactor = Mathf.Clamp01(depth / 2f);

                        // Apply current force to ship
                        float forceMagnitude = body.mass * submersionFactor * 0.5f * fixedDeltaTime;
                        body.AddForce(flowForce.normalized * forceMagnitude, ForceMode.Impulse);
                    }
                }
            }
        }

        #endregion

        #region Floating.GetWaterLevel Patch

        /// <summary>
        /// Patches Floating.GetWaterLevel (static method) to check custom water.
        /// This is used by ships and other floating objects to determine water level.
        /// </summary>
        [HarmonyPatch(typeof(Floating), nameof(Floating.GetWaterLevel))]
        [HarmonyPostfix]
        private static void Floating_GetWaterLevel_Postfix(Vector3 p, ref float __result)
        {
            // Check for custom water at this position
            float customLevel = CustomWaterManager.GetWaterLevelAt(p);

            // Use custom water if it's higher than vanilla result.
            //
            // 2026-05-27 round-52 — REMOVED the SWIM_DEBUG diagnostic
            // Debug.LogWarning. This Harmony postfix is called by every
            // floating object every FixedUpdate — when the player is
            // near water with several ships + dropped items in range,
            // the per-frame Debug.LogWarning was firing thousands of
            // times per second and stalling the main thread on log
            // I/O. The land-ice placement lock-up the user reported
            // was actually this log spam, not the LandIceController.
            if (customLevel > __result)
            {
                __result = customLevel;
            }
        }

        #endregion

        #region WaterVolume Integration

        /// <summary>
        /// Checks if a position is in custom water and returns the water instance.
        /// Used by other systems that need to know about custom water.
        /// </summary>
        public static bool TryGetCustomWaterAt(Vector3 worldPos, out CustomWaterBase water, out float waterLevel)
        {
            water = CustomWaterManager.GetWaterAt(worldPos);

            if (water != null)
            {
                waterLevel = water.GetWaterSurfaceAt(worldPos);
                return waterLevel > float.MinValue;
            }

            waterLevel = float.MinValue;
            return false;
        }

        /// <summary>
        /// Gets the combined water level from both vanilla and custom sources.
        /// </summary>
        public static float GetCombinedWaterLevel(Vector3 worldPos)
        {
            // Get vanilla water level
            float vanillaLevel = float.MinValue;

            if (ZoneSystem.instance != null)
            {
                vanillaLevel = ZoneSystem.instance.m_waterLevel;
            }

            // Check WaterVolume instances for more accurate vanilla level
            foreach (var wv in WaterVolume.Instances)
            {
                if (wv == null) continue;

                var collider = wv.GetComponent<Collider>();
                if (collider != null)
                {
                    Bounds bounds = collider.bounds;
                    bounds.Expand(new Vector3(0f, 100f, 0f));

                    if (bounds.Contains(worldPos))
                    {
                        float level = wv.GetWaterSurface(worldPos, 1f);
                        if (level > vanillaLevel)
                            vanillaLevel = level;
                    }
                }
            }

            // Get custom water level
            float customLevel = CustomWaterManager.GetWaterLevelAt(worldPos);

            // Return the higher of the two
            return Mathf.Max(vanillaLevel, customLevel);
        }

        #endregion
    }
}
