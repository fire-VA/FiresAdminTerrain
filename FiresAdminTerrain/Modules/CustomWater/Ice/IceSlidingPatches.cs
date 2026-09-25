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

        #region Slide State Tracking

        private struct SlideState
        {
            public Vector3 SlideVelocity;
            public float SlipFactor;
        }

        private static readonly Dictionary<Character, SlideState> _slideStates =
            new Dictionary<Character, SlideState>();

        /// <summary>
        /// Initiates an ice slide with the given velocity.
        /// </summary>
        private static void StartSlide(Character character, Vector3 velocity, bool checkMagnitude = false)
        {
            if (character == null) return;

            SlideState state;
            _slideStates.TryGetValue(character, out state);
            state.SlipFactor = 1f;

            if (checkMagnitude && state.SlideVelocity.magnitude > velocity.magnitude)
                return;

            state.SlideVelocity = Vector3.ClampMagnitude(velocity, 10f);
            _slideStates[character] = state;
        }

        /// <summary>
        /// Updates the slide, blending current velocity toward the stored slide velocity.
        /// Called from ApplyGroundForce postfix.
        /// </summary>
        private static void UpdateSlide(Character character, ref Vector3 vel)
        {
            if (!_slideStates.TryGetValue(character, out SlideState state))
                return;

            if (state.SlipFactor > 0f && (character.IsOnCustomIce() || !character.IsOnGround()))
            {
                vel = Vector3.Lerp(vel, state.SlideVelocity, state.SlipFactor);

                float maxDelta = character.IsOnGround()
                    ? Time.fixedDeltaTime / 2f / ICE_SLIPPERINESS
                    : Time.fixedDeltaTime;

                state.SlipFactor = Mathf.MoveTowards(state.SlipFactor, 0f, maxDelta);
                _slideStates[character] = state;
            }
            else
            {
                StopSlide(character);
            }
        }

        private static void StopSlide(Character character)
        {
            _slideStates.Remove(character);
        }

        #endregion

        #region Patches

        /// <summary>
        /// Clean up slide state when a character is destroyed.
        /// </summary>
        [HarmonyPatch(typeof(Character), "OnDestroy")]
        [HarmonyPostfix]
        private static void Character_OnDestroy_Postfix(Character __instance)
        {
            StopSlide(__instance);
        }

        /// <summary>
        /// Override body friction when standing on custom ice.
        /// Sets near-zero friction so the character slides.
        /// </summary>
        [HarmonyPatch(typeof(Character), "UpdateBodyFriction")]
        [HarmonyPrefix]
        private static bool Character_UpdateBodyFriction_Prefix(Character __instance, CapsuleCollider ___m_collider)
        {
            if (!__instance.IsOnCustomIce())
                return true; // Not on our ice - let vanilla handle it

            if (___m_collider == null || ___m_collider.material == null)
                return true;

            ___m_collider.material.staticFriction = 0.1f;
            ___m_collider.material.dynamicFriction = 0.1f;
            ___m_collider.material.frictionCombine = PhysicsMaterialCombine.Minimum;
            return false; // Skip vanilla friction update
        }

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

        /// <summary>
        /// When the player stops running on ice, initiate a slide with their
        /// current velocity so they glide to a stop.
        /// </summary>
        [HarmonyPatch(typeof(Player), "SetControls")]
        [HarmonyPrefix]
        private static void Player_SetControls_Prefix(
            Player __instance, bool run, bool ___m_run, Vector3 ___m_currentVel)
        {
            if (__instance != Player.m_localPlayer) return;
            if (!__instance.IsOnCustomIce()) return;

            // Detect run -> walk transition
            if (!run && ___m_run)
            {
                StartSlide(__instance, ___m_currentVel, checkMagnitude: false);
            }
        }

        /// <summary>
        /// Apply the slide velocity blending in ApplyGroundForce.
        /// This is where the actual sliding momentum is injected.
        /// </summary>
        [HarmonyPatch(typeof(Character), "ApplyGroundForce")]
        [HarmonyPostfix]
        private static void Character_ApplyGroundForce_Postfix(
            Character __instance, ZNetView ___m_nview, ref Vector3 vel)
        {
            if (!_slideStates.ContainsKey(__instance))
                return;

            if (___m_nview == null || !___m_nview.IsValid() || !___m_nview.IsOwner())
            {
                StopSlide(__instance);
                return;
            }

            UpdateSlide(__instance, ref vel);
        }

        /// <summary>
        /// When landing on ice from a fall, initiate a slide with the impact velocity.
        /// Tracks pre-landing state to detect the moment of landing.
        /// </summary>
        [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
        public static class Character_UpdateGroundContact_Patch
        {
            private static readonly Dictionary<Character, Vector3> _preLandVelocity =
                new Dictionary<Character, Vector3>();

            [HarmonyPrefix]
            private static void Prefix(
                Character __instance, ZNetView ___m_nview,
                float ___m_maxAirAltitude, Rigidbody ___m_body)
            {
                if (___m_nview == null || !___m_nview.IsValid() || !___m_nview.IsOwner())
                    return;

                float fallHeight = Mathf.Max(0f, ___m_maxAirAltitude - __instance.transform.position.y);
                if (fallHeight > 1f && ___m_body != null)
                {
                    _preLandVelocity[__instance] = ___m_body.linearVelocity;
                }
            }

            [HarmonyPostfix]
            private static void Postfix(
                Character __instance, float ___m_maxAirAltitude)
            {
                if (!_preLandVelocity.TryGetValue(__instance, out Vector3 landVel))
                    return;

                _preLandVelocity.Remove(__instance);

                // Check if we just landed (maxAirAltitude reset to current pos)
                if (___m_maxAirAltitude != __instance.transform.position.y)
                    return;

                if (__instance.IsOnCustomIce())
                {
                    StartSlide(__instance, landVel, checkMagnitude: true);
                }
            }
        }

        /// <summary>
        /// When a dodge roll ends on ice, carry the dodge momentum into a slide.
        /// </summary>
        [HarmonyPatch(typeof(Player), "UpdateDodge")]
        public static class Player_UpdateDodge_Patch
        {
            private static bool _wasInDodge;
            private static Vector3 _dodgeVelocity;

            [HarmonyPrefix]
            private static void Prefix(
                Player __instance, bool ___m_inDodge,
                Rigidbody ___m_body, Vector3 ___m_queuedDodgeDir)
            {
                if (__instance != Player.m_localPlayer) return;

                // If we were dodging and just finished, trigger slide on ice
                if (_wasInDodge && !___m_inDodge && __instance.IsOnCustomIce())
                {
                    StartSlide(__instance, _dodgeVelocity);
                }

                // Track state for next frame
                if (___m_inDodge && ___m_body != null)
                {
                    _dodgeVelocity = ___m_queuedDodgeDir * ___m_body.linearVelocity.magnitude;
                }
            }

            [HarmonyPostfix]
            private static void Postfix(Player __instance, bool ___m_inDodge)
            {
                if (__instance != Player.m_localPlayer) return;
                _wasInDodge = ___m_inDodge;
            }
        }

        #endregion
    }
}
