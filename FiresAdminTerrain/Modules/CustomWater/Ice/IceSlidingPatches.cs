using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Harmony patches for ice surface physics on custom water ice.
    /// 
    /// Based on Seasons mod's frozen ocean sliding system, adapted for our
    /// custom ice surfaces. Provides:
    /// 
    /// 1. ICE DETECTION: Checks if a character's ground collider is our "IceShell"
    ///    or "IceOverlay" mesh, indicating they are standing on custom ice.
    /// 
    /// 2. LOW FRICTION: Overrides UpdateBodyFriction when on ice to apply
    ///    near-zero friction, making the surface slippery.
    /// 
    /// 3. SLIDING MOMENTUM: When the player stops running or lands on ice,
    ///    their current velocity is preserved and gradually decays, causing
    ///    them to slide. Dodge rolls on ice also trigger a slide.
    /// 
    /// 4. FOOTSTEP SOUNDS: Overrides GetGroundMaterial when on ice to play
    ///    snow/ice footstep sounds instead of the default.
    /// 
    /// 5. ICE RESTORE: When an ice1 prefab spawns from ZDO (zone reload),
    ///    detects custom ice data on the ZDO and adds FloatingIceController
    ///    or LandIceController so the ice mesh + collision are rebuilt.
    ///    Legacy ZDO keys are auto-migrated to the new format.
    /// 
    /// The collider name checked is "IceShell" / "IceOverlay" which matches
    /// the GameObjects created by FloatingIceController and LandIceController.
    /// </summary>
    [HarmonyPatch]
    public static class IceSlidingPatches
    {
        #region Ice Restore on Zone Reload

        /// <summary>
        /// Prefab names that we use for custom ice (ice1 or ice_floor).
        /// When these prefabs spawn from ZDO and have our custom ice data,
        /// we add the appropriate ice controller to restore the ice surface.
        /// </summary>
        private static readonly HashSet<string> s_icePrefabNames = new HashSet<string>
        {
            "ice1", "ice_floor", "StandaloneIce", "ice1(Clone)", "ice_floor(Clone)", "StandaloneIce(Clone)"
        };

        /// <summary>
        /// Patch ZNetView.Awake to detect ice1 prefabs with custom ice data.
        /// When found, adds the appropriate controller so it can restore from ZDO in Start().
        /// </summary>
        [HarmonyPatch(typeof(ZNetView), "Awake")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void ZNetView_Awake_IceRestore_Postfix(ZNetView __instance)
        {
            if (__instance == null || __instance.GetZDO() == null) return;

            // Quick name check to avoid ZDO reads on every object
            string goName = __instance.gameObject.name;
            bool nameMatch = false;
            foreach (var name in s_icePrefabNames)
            {
                if (goName.Contains(name) || goName.StartsWith(name))
                {
                    nameMatch = true;
                    break;
                }
            }

            // Also check the prefab hash for ice1/ice_floor
            if (!nameMatch)
            {
                int prefabHash = __instance.GetZDO().GetPrefab();
                if (prefabHash != "ice1".GetStableHashCode()
                    && prefabHash != "ice_floor".GetStableHashCode())
                    return;
            }

            // Check for new FloatingIceController data first
            if (FloatingIceController.ZdoHasFloatingIceData(__instance.GetZDO()))
            {
                if (__instance.GetComponent<FloatingIceController>() == null)
                {
                    __instance.gameObject.AddComponent<FloatingIceController>();
                    FiresLogger.LogInfo($"[IceSlidingPatches] Added FloatingIceController to respawned ice prefab at {__instance.transform.position}");
                }
                return;
            }

            // Check for new LandIceController data
            if (LandIceController.ZdoHasLandIceData(__instance.GetZDO()))
            {
                if (__instance.GetComponent<LandIceController>() == null)
                {
                    __instance.gameObject.AddComponent<LandIceController>();
                    FiresLogger.LogInfo($"[IceSlidingPatches] Added LandIceController to respawned ice prefab at {__instance.transform.position}");
                }
                return;
            }

            // Check for legacy ice data (va_is_custom_ice) - auto-migrate to new controllers
            if (!HasLegacyIceData(__instance.GetZDO()))
                return;

            // Already has a controller (fresh placement, not a reload)
            if (__instance.GetComponent<FloatingIceController>() != null
                || __instance.GetComponent<LandIceController>() != null)
                return;

            // Migrate legacy ZDO data to the new controllers.
            // Read the IsFloating flag from the legacy ZDO to determine which
            // new controller to use, then let the new controller's Start()
            // restore from ZDO.
            if (MigrateLegacyIceZDO(__instance))
                return;

            // Migration failed - log warning (legacy data will be lost)
            FiresLogger.LogWarning($"[IceSlidingPatches] Failed to migrate legacy ice ZDO at {__instance.transform.position} - ice data lost");
        }

        #endregion

        #region Legacy ZDO Migration

        /// <summary>
        /// Legacy ZDO key hashes (from the removed StandaloneIceController).
        /// Used to detect and migrate old ice data to the new controllers.
        /// </summary>
        private static readonly int s_legacyIsCustomIceHash = "va_is_custom_ice".GetStableHashCode();
        private static readonly int s_legacyIceModeHash = "va_ice_mode".GetStableHashCode();
        private static readonly int s_legacyIceFootprintsHash = "va_ice_footprints".GetStableHashCode();
        private static readonly int s_legacyIceNoiseSeedHash = "va_ice_noise_seed".GetStableHashCode();

        /// <summary>
        /// New ZDO key hashes (must match FloatingIceController / LandIceController).
        /// </summary>
        private static readonly int s_floatIceHash = "va_float_ice".GetStableHashCode();
        private static readonly int s_floatIceFpHash = "va_float_ice_fp".GetStableHashCode();
        private static readonly int s_floatIceSeedHash = "va_float_ice_seed".GetStableHashCode();
        private static readonly int s_landIceHash = "va_land_ice".GetStableHashCode();
        private static readonly int s_landIceFpHash = "va_land_ice_fp".GetStableHashCode();
        private static readonly int s_landIceSeedHash = "va_land_ice_seed".GetStableHashCode();

        /// <summary>
        /// Checks if a ZDO has legacy ice data (va_is_custom_ice key).
        /// </summary>
        private static bool HasLegacyIceData(ZDO zdo)
        {
            return zdo != null && zdo.GetBool(s_legacyIsCustomIceHash, false);
        }

        /// <summary>
        /// Migrates a legacy ice ZDO to the new format and adds the
        /// appropriate new controller component.
        /// Returns true if migration succeeded.
        /// </summary>
        private static bool MigrateLegacyIceZDO(ZNetView nview)
        {
            var zdo = nview.GetZDO();
            if (zdo == null) return false;

            // Read legacy footprints
            byte[] fpData = zdo.GetByteArray(s_legacyIceFootprintsHash);
            if (fpData == null || fpData.Length < 12) return false;

            bool isFloating = zdo.GetInt(s_legacyIceModeHash, 1) == 1;
            float noiseSeed = zdo.GetFloat(s_legacyIceNoiseSeedHash, 0f);

            // Write new-format ZDO keys so the new controller can restore in Start()
            if (isFloating)
            {
                zdo.Set(s_floatIceHash, true);
                zdo.Set(s_floatIceFpHash, fpData);
                zdo.Set(s_floatIceSeedHash, noiseSeed);
                // No height grid - the new controller will init a flat grid in LoadFromZDO

                nview.gameObject.AddComponent<FloatingIceController>();
                FiresLogger.LogInfo($"[IceSlidingPatches] Migrated legacy floating ice ZDO at {nview.transform.position}");
            }
            else
            {
                zdo.Set(s_landIceHash, true);
                zdo.Set(s_landIceFpHash, fpData);
                zdo.Set(s_landIceSeedHash, noiseSeed);
                // No height grid - the new controller will init from terrain raycasts in LoadFromZDO

                nview.gameObject.AddComponent<LandIceController>();
                FiresLogger.LogInfo($"[IceSlidingPatches] Migrated legacy land ice ZDO at {nview.transform.position}");
            }

            return true;
        }

        #endregion

        #region Ice Detection

        /// <summary>
        /// Name of the ice shell collider GameObject (FrozenWaterController).
        /// Must match the name set in FrozenWaterController.CreateGameObjects().
        /// </summary>
        private const string ICE_COLLIDER_NAME = "IceShell";

        /// <summary>
        /// Name of the floating ice shell collider (FloatingIceController).
        /// </summary>
        private const string ICE_STANDALONE_SHELL_NAME = "IceShell";

        /// <summary>
        /// Name of the land ice overlay collider (LandIceController).
        /// </summary>
        private const string ICE_OVERLAY_NAME = "IceOverlay";

        /// <summary>
        /// How quickly ice sliding decays. Higher = faster decay = less slippery.
        /// The Seasons mod uses a configurable value; we use a fixed moderate value.
        /// </summary>
        private const float ICE_SLIPPERINESS = 2f;

        /// <summary>
        /// Returns true if the character is standing on a custom ice surface.
        /// Checks the character's last ground collider name against all ice collider names.
        /// Detects FrozenWaterController, FloatingIceController, and LandIceController ice.
        /// </summary>
        public static bool IsOnCustomIce(this Character character)
        {
            if (character == null || !character.IsOnGround()) return false;

            Collider lastGround = character.GetLastGroundCollider();
            if (lastGround == null) return false;

            string name = lastGround.gameObject.name;
            return name == ICE_COLLIDER_NAME
                || name == ICE_STANDALONE_SHELL_NAME
                || name == ICE_OVERLAY_NAME;
        }

        #endregion

        #region Patches

        // 0.2.1095 (Fire: "we now have that vanilla slippery mechanic to work with instead of our previous home made one"): the
        // home-made slide is REMOVED (slide state, Character.UpdateBodyFriction / ApplyGroundForce / UpdateGroundContact /
        // OnDestroy, Player.SetControls / UpdateDodge patches). FAT ice colliders opt into vanilla's own slippery ice instead
        // (VanillaIce.OptIn: layer static_solid + PhysicsMaterial SlipperyIce, Character.ApplySlippery). Only the icy footstep
        // sound stays.

        /// <summary>
        /// Override footstep sounds when walking on custom ice.
        /// Returns Snow ground material for icy footstep sounds.
        /// </summary>
        [HarmonyPatch(typeof(FootStep), "GetGroundMaterial")]
        [HarmonyPrefix]
        private static bool FootStep_GetGroundMaterial_Prefix(
            Character character, ref FootStep.GroundMaterial __result)
        {
            if (character == null || character != Player.m_localPlayer)
                return true;

            if (!character.IsOnCustomIce())
                return true;

            __result = FootStep.GroundMaterial.Snow;
            return false;
        }

        #endregion
    }
}
