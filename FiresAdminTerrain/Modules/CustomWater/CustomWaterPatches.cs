using HarmonyLib;
using System.Reflection;
using UnityEngine;


namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Harmony patches for custom water integration with Valheim systems.
    /// Handles swimming, status effects, water detection, and footstep sounds.
    /// </summary>
    [HarmonyPatch]
    public static class CustomWaterPatches
    {
        #region Constants

        /// <summary>
        /// Maximum vertical distance (meters) where water is considered "right at the player's level"
        /// and skip the raycast check. Below this threshold, the player is assumed to be wading
        /// into water normally, not under terrain with water above.
        /// </summary>
        private const float WATER_WADING_THRESHOLD = 1.5f;

        /// <summary>
        /// Layer mask for terrain blocking raycasts between player and water.
        /// Cached for performance.
        /// </summary>
        private static int s_terrainBlockingLayerMask = -1;

        #endregion

        #region Cave/Terrain Water Filtering

        /// <summary>
        /// Determines if water at a given surface level should affect a character at a position.
        /// 
        /// Uses THREE checks to prevent cave swimmers from being pulled up:
        /// 1. TerrainCaveDetector: If the LOCAL PLAYER is under custom terrain ceiling, block water above.
        ///    ONLY used for the local player - NPCs skip this check because the detector
        ///    tracks the player's position, not the NPC's. Using it for NPCs would
        ///    incorrectly block their water detection whenever the player is in a cave.
        /// 2. Wading threshold: Water within 1.5m above is always allowed (normal wading).
        /// 3. Physics raycast: If solid terrain/geometry exists between character and water, block it.
        ///    This catches vanilla terrain, custom terrain, and built structures that separate
        ///    the character from the water.
        /// </summary>
        /// <param name="characterPos">Character position</param>
        /// <param name="waterSurfaceY">Y level of water surface</param>
        /// <param name="isLocalPlayer">True if this check is for the local player</param>
        /// <returns>True if this water should NOT affect the character (blocked)</returns>
        private static bool IsWaterBlockedFromCharacter(Vector3 characterPos, float waterSurfaceY, bool isLocalPlayer)
        {
            // Water below character's feet = not relevant (they're above it)
            if (waterSurfaceY <= characterPos.y)
                return false;

            float distanceToWater = waterSurfaceY - characterPos.y;

            // Phase F: TerrainCaveDetector removed (legacy cave system out
            // of FAT scope). Players in caves no longer get a fast-path
            // skip on the water-detection raycast; they fall through to
            // the standard distance check below.

            // For very shallow water right at the character's level, skip the raycast.
            // This handles normal wading where the character walks into water.
            if (distanceToWater <= WATER_WADING_THRESHOLD)
                return false;

            // Raycast upward from character to water surface to detect ANY blocking geometry.
            // This catches vanilla terrain, custom terrain, pieces, and other solid objects
            // that separate the character from the water.
            if (IsTerrainBlockingVertically(characterPos, waterSurfaceY))
                return true;

            return false;
        }

        /// <summary>
        /// Legacy overload for backward compatibility. Assumes local player.
        /// </summary>
        private static bool IsWaterBlockedFromPlayer(Vector3 playerPos, float waterSurfaceY)
        {
            return IsWaterBlockedFromCharacter(playerPos, waterSurfaceY, true);
        }

        /// <summary>
        /// Raycasts from the player upward toward the water surface to detect blocking terrain/geometry.
        /// Returns true if solid geometry exists between the player and the water, meaning
        /// the player is in a cave or under terrain and shouldn't be affected by the water above.
        /// </summary>
        private static bool IsTerrainBlockingVertically(Vector3 playerPos, float waterSurfaceY)
        {
            // Initialize layer mask once
            if (s_terrainBlockingLayerMask == -1)
            {
                s_terrainBlockingLayerMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            }

            // Raycast from player head upward toward the water surface
            Vector3 rayStart = playerPos + Vector3.up * 1.8f; // Head height
            float rayDistance = waterSurfaceY - rayStart.y;

            if (rayDistance <= 0f)
                return false; // Water is at or below head level, no blocking needed

            if (Physics.Raycast(rayStart, Vector3.up, out RaycastHit hit, rayDistance, s_terrainBlockingLayerMask))
            {
                // Something solid is between us and the water - we're in a cave or under terrain
                return true;
            }

            return false;
        }

        /// <summary>
        /// Finds the LOWEST valid custom water level that should affect a character.
        /// This handles multi-layer scenarios by finding water in the character's "layer".
        /// </summary>
        /// <param name="pos">Character position</param>
        /// <param name="isLocalPlayer">True if this is the local player (enables cave detector)</param>
        /// <returns>Water level if found and valid, float.MinValue otherwise</returns>
        private static float GetValidWaterLevelForCharacter(Vector3 pos, bool isLocalPlayer)
        {
            float bestWaterLevel = float.MinValue;

            // Check legacy custom water
            var water = CustomWaterManager.GetWaterAt(pos);
            if (water != null)
            {
                float level = water.GetWaterSurfaceAt(pos);
                if (level > float.MinValue && !IsWaterBlockedFromCharacter(pos, level, isLocalPlayer))
                {
                    if (level > bestWaterLevel)
                    {
                        bestWaterLevel = level;
                    }
                }
            }

            // Check WaterMeshCombiner (terrain-style water)
            var combiner = WaterMeshCombiner.GetCombinerAt(pos);
            if (combiner != null)
            {
                float level = combiner.GetWaterSurfaceAt(pos);
                if (level > float.MinValue && !IsWaterBlockedFromCharacter(pos, level, isLocalPlayer))
                {
                    // For multi-layer water, prefer water that's closer to character
                    // If we already found water, only replace if this one is lower (closer to character)
                    if (bestWaterLevel == float.MinValue || level < bestWaterLevel)
                    {
                        bestWaterLevel = level;
                    }
                }
            }

            // Round-84 — FiresLiquidWater vanilla-LiquidVolume pours (springs +
            // water volumes). These aren't combiners or legacy water, so add
            // their surface here — this is what makes swimming trigger at the
            // top of our custom liquid mesh.
            float pourLevel = FiresLiquidWaterPrefab.GetSurfaceAt(pos);
            if (pourLevel > float.MinValue && !IsWaterBlockedFromCharacter(pos, pourLevel, isLocalPlayer))
            {
                if (pourLevel > bestWaterLevel)
                    bestWaterLevel = pourLevel;
            }

            // GENERATED river/lake water. THIS is the function every swim check
            // actually calls (InWater / IsSwimming / GetLiquidLevel all route
            // here) — CustomWaterManager.GetWaterLevelAt is NOT consulted by
            // them, so reporting there did nothing and generated water stayed
            // unswimmable. Reported here it behaves exactly like a pour: the
            // same blocked-by-geometry test, the same swim thresholds.
            float genLevel = VerdantsAscent.Terrain.Voxel.VoxelWaterSurface.SurfaceAt(pos);
            if (genLevel > -9999f && !IsWaterBlockedFromCharacter(pos, genLevel, isLocalPlayer))
            {
                if (genLevel > bestWaterLevel)
                    bestWaterLevel = genLevel;
            }

            return bestWaterLevel;
        }

        /// <summary>
        /// Legacy overload for backward compatibility. Assumes local player.
        /// </summary>
        private static float GetValidWaterLevelForPlayer(Vector3 pos)
        {
            return GetValidWaterLevelForCharacter(pos, true);
        }

        /// <summary>
        /// Gets the liquid type at a position (for determining wet vs tarred).
        /// </summary>
        private static LiquidType GetLiquidTypeAt(Vector3 pos)
        {
            var water = CustomWaterManager.GetWaterAt(pos);
            if (water != null)
            {
                return water.LiquidType;
            }

            var combiner = WaterMeshCombiner.GetCombinerAt(pos);
            if (combiner != null)
            {
                return combiner.LiquidType;
            }

            return LiquidType.Water;
        }

        #endregion

        #region Character Swimming Patches

        // W4.2 — a character deep inside a voxel cave BELOW the waterline
        // still gets the zone-wide water trigger's level pushed at them, so
        // swim engaged and dragged them up through the rock ("it tries to
        // force us up"). If solid ground separates the character from the
        // reported surface, that water isn't theirs — report dry instead
        // (caves under the water table are walkable; a cave OPEN to the
        // water still swims because the raycast reaches the surface).
        // Surface swimming (level within 1.5m overhead) never raycasts.
        private static readonly AccessTools.FieldRef<Character, float> s_waterLevelRef =
            AccessTools.FieldRefAccess<Character, float>("m_waterLevel");
        private static readonly AccessTools.FieldRef<Character, float> s_tarLevelRef =
            AccessTools.FieldRefAccess<Character, float>("m_tarLevel");
        private static readonly AccessTools.FieldRef<Character, float> s_liquidLevelRef =
            AccessTools.FieldRefAccess<Character, float>("m_liquidLevel");

        [HarmonyPatch(typeof(Character), nameof(Character.SetLiquidLevel))]
        [HarmonyPrefix]
        private static bool Character_SetLiquidLevel_CaveGate(Character __instance, float level, LiquidType type)
        {
            if (type != LiquidType.Water || __instance == null) return true;
            if (VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.WorldKind == VerdantsAscent.Terrain.Voxel.VoxelWorldMode.Vanilla) return true;
            Vector3 pos = __instance.transform.position;
            if (level - pos.y < 1.5f) return true;
            if (!IsTerrainBlockingVertically(pos, level)) return true;

            s_waterLevelRef(__instance) = -10000f;
            s_liquidLevelRef(__instance) = Mathf.Max(-10000f, s_tarLevelRef(__instance));
            return false;
        }

        /// <summary>
        /// Patch Character.InLiquid to detect custom water.
        /// This affects swimming state and wet debuff.
        /// Works for ALL characters (players and NPCs/creatures).
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.InLiquid))]
        [HarmonyPostfix]
        private static void Character_InLiquid_Postfix(Character __instance, ref bool __result)
        {
            if (__result) return;

            Vector3 pos = __instance.transform.position;
            bool isLocal = __instance == Player.m_localPlayer;
            float waterLevel = GetValidWaterLevelForCharacter(pos, isLocal);

            if (waterLevel > float.MinValue && pos.y < waterLevel)
            {
                __result = true;
            }
        }

        /// <summary>
        /// Patch Character.InWater to detect custom water (not tar).
        /// 
        /// CRITICAL FOR TAR FIX: In Valheim, if InLiquid() returns true but InWater()
        /// returns false, the game assumes the character is in TAR and applies tar VFX
        /// instead of wet VFX. This patch ensures InWater() returns true for ALL
        /// characters (including NPCs/creatures) when they are in custom water that
        /// is not tar, preventing the incorrect tar effect.
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.InWater))]
        [HarmonyPostfix]
        private static void Character_InWater_Postfix(Character __instance, ref bool __result)
        {
            if (__result) return;

            Vector3 pos = __instance.transform.position;
            bool isLocal = __instance == Player.m_localPlayer;
            float waterLevel = GetValidWaterLevelForCharacter(pos, isLocal);

            if (waterLevel > float.MinValue && pos.y < waterLevel)
            {
                // Only return true for water (not tar)
                if (GetLiquidTypeAt(pos) != LiquidType.Tar)
                {
                    __result = true;
                }
            }
        }

        /// <summary>
        /// Patch Character.IsSwimming to detect custom water.
        /// Works for ALL characters (players and NPCs/creatures).
        /// 
        /// Previously disabled due to bounding-box false positives. Now re-enabled because
        /// WaterMeshCombiner.GetWaterSurfaceAt() uses actual disk geometry (IsWithinAnyDiskRadius)
        /// and Y-proximity checks, which eliminates the false positives that caused swimming
        /// in mid-air near water bounding boxes.
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.IsSwimming))]
        [HarmonyPostfix]
        private static void Character_IsSwimming_Postfix(Character __instance, ref bool __result)
        {
            if (__result) return;

            Vector3 pos = __instance.transform.position;
            bool isLocal = __instance == Player.m_localPlayer;
            float waterLevel = GetValidWaterLevelForCharacter(pos, isLocal);

            if (waterLevel > float.MinValue)
            {
                float depth = waterLevel - pos.y;
                if (depth > 1.6f)
                {
                    __result = true;
                }
            }
        }

        #endregion

        #region Character.GetLiquidLevel Patch

        /// <summary>
        /// Patch Character.GetLiquidLevel to report custom water levels.
        /// 
        /// Previously disabled due to bounding-box false positives. Now re-enabled because
        /// GetWaterSurfaceAt uses actual disk geometry checks and Y-proximity validation.
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.GetLiquidLevel))]
        [HarmonyPostfix]
        private static void Character_GetLiquidLevel_Postfix(Character __instance, ref float __result)
        {
            Vector3 pos = __instance.transform.position;
            bool isLocal = __instance == Player.m_localPlayer;
            float waterLevel = GetValidWaterLevelForCharacter(pos, isLocal);

            if (waterLevel > __result)
            {
                __result = waterLevel;
            }
        }

        #endregion

        #region Underwater Env / Buoyancy Integration

        /// <summary>
        /// Round-85 — feed FiresLiquidWater pours into the GLOBAL water-level
        /// query. FiresUnderwaterEnv reads Floating.GetWaterLevel(eye) for its
        /// fog + submersion ramp (_FiresPlayerSubmerged) and the underwater env
        /// switch; buoyancy uses it too. So diving under a custom pool now fogs
        /// the view and floats objects. Position-accurate — GetSurfaceAt returns
        /// below-ground for dry cells, so no far-water false positives (unlike
        /// the old bounding-box WaterVolume patch that had to be disabled).
        /// </summary>
        internal static void RaiseToPourSurface(Vector3 p, ref float __result)
        {
            float pour = FiresLiquidWaterPrefab.GetSurfaceAt(p);
            if (pour > float.MinValue && pour > __result)
                __result = pour;
        }

        #endregion

        #region WaterVolume Integration

        // DISABLED: WaterVolume.GetWaterSurface patch commented out - bounding box
        // detection causes vanilla to think the player is in water from far away.
        //
        // [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.GetWaterSurface), typeof(Vector3), typeof(float))]
        // [HarmonyPostfix]
        // private static void WaterVolume_GetWaterSurface_Postfix(Vector3 point, float waveFactor, ref float __result)
        // {
        //     // Check legacy custom water
        //     float customLevel = CustomWaterManager.GetWaterLevelAt(point);
        //     if (customLevel > __result)
        //     {
        //         __result = customLevel;
        //     }
        //     
        //     // Check WaterMeshCombiner (terrain-style water)
        //     var combiner = WaterMeshCombiner.GetCombinerAt(point);
        //     if (combiner != null)
        //     {
        //         float combinerLevel = combiner.GetWaterSurfaceAt(point);
        //         if (combinerLevel > __result)
        //         {
        //             __result = combinerLevel;
        //         }
        //     }
        // }

        #endregion

        #region Status Effects

        // DISABLED: UpdateWater postfix removed - it was redundantly pushing water levels
        // onto characters every frame using bounding-box detection, which caused false
        // swimming triggers in caves. The WaterCombinerTrigger (OnTriggerEnter/Stay/Exit)
        // already handles swim detection properly with its own terrain-blocking raycasts.
        // The InLiquid/InWater patches above are sufficient for vanilla's swimming state checks.
        //
        // [HarmonyPatch(typeof(Character), "UpdateWater")]
        // [HarmonyPostfix]
        // private static void Character_UpdateWater_Postfix(Character __instance, float dt) { ... }

        #endregion

        #region Player Spawn / Multiplayer Sync

        /// <summary>
        /// When a player spawns, anchors will restore automatically as zones load.
        /// v5: No RPC sync needed - anchors are the source of truth.
        /// </summary>
        internal static void OnPlayerSpawned(Player __instance)
        {
            // Only trigger for the local player
            if (__instance != Player.m_localPlayer) return;

            // DIAG (login-freeze)
            FiresLogger.LogInfo("[LoginFreeze][DIAG] ENTER CustomWaterPatches.Player_OnSpawned_Postfix");

            // Ensure water cache is initialized. On client reconnect, ZNet.Start()
            // fires before world data is received, so the cache may not have been
            // initialized yet. By OnSpawned, the world name is always available.
            try
            {
                string worldName = ZNet.instance?.GetWorldName();
                long worldSeed = ZNet.instance?.GetWorldUID() ?? 0;
                if (!string.IsNullOrEmpty(worldName))
                {
                    WaterCombinerCache.InitializeForWorld(worldName, worldSeed);
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[CustomWaterPatches] Failed to init water cache on spawn: {ex.Message}");
            }

            // v6: After ensuring cache is initialized, proactively restore from
            // local file cache if no combiners exist yet. This handles re-login
            // from main menu where the delayed load coroutine may not have run yet.
            if (WaterMeshCombiner.AllCombiners.Count == 0 && !ZNet.instance.IsServer())
            {
                RestoreWaterFromClientCache();
            }
            else
            {
                FiresLogger.LogInfo($"[CustomWaterPatches] Player spawned - {WaterMeshCombiner.AllCombiners.Count} water combiners already present");
            }

            // DIAG (login-freeze)
            FiresLogger.LogInfo("[LoginFreeze][DIAG] EXIT CustomWaterPatches.Player_OnSpawned_Postfix");
        }

        /// <summary>
        /// Clear water operation caches on world unload to prevent stale data.
        /// </summary>
        internal static void OnLogout()
        {
            try
            {
                // Save water cache before clearing
                WaterCombinerCache.OnWorldUnload();

                // Clear combiners and caches
                WaterPersistence.ClearAllWaterCombiners();

                // Clear anchor tracking to prevent stale references on next login
                WaterMeshCombinerAnchor.ClearAnchorTracking();
                WaterMeshCombinerAnchor.ClearConversionTracking();

                // Reset RPC and persistence _initialized flags so they
                // reinitialize properly on the next login session.
                // Without this, Initialize() silently returns on reconnect
                // because the old _initialized = true is still set.
                WaterOperationRPC.ClearCaches();
                WaterPersistence.ResetInitialized();

                FiresLogger.LogInfo("[CustomWaterPatches] Cleared water combiners and caches on logout");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[CustomWaterPatches] Failed to clear water caches: {ex.Message}");
            }
        }

        #endregion

        #region Anchor Protection

        /// <summary>
        /// Prevents WearNTear.Remove from destroying our anchor ZDOs.
        /// 
        /// Water anchors use the CustomRiver prefab, terrain anchors use piece_dirtfloor_vad.
        /// Both have WearNTear. When Infinity Hammer's remove tool or other destruction
        /// methods target the anchor, WearNTear.Remove would destroy the ZDO, permanently
        /// losing all persisted data. This prefix blocks removal for anchors.
        /// 
        /// The anchors are invisible (layer 2 / Ignore Raycast) so they shouldn't be
        /// targeted normally, but edge cases exist (area-of-effect removal, mods that
        /// iterate all pieces, etc.).
        /// </summary>
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Remove))]
        [HarmonyPrefix]
        private static bool WearNTear_Remove_Prefix(WearNTear __instance)
        {
            if (__instance == null) return true;

            // Check if this is a water anchor
            var waterAnchor = __instance.GetComponent<WaterMeshCombinerAnchor>();
            if (waterAnchor != null)
            {
                FiresLogger.LogWarning($"[CustomWaterPatches] Blocked removal of water anchor at {__instance.transform.position}. Use va_clear_auto_water command instead.");
                return false; // Skip WearNTear.Remove entirely
            }

            // Phase F: legacy TerrainCombinerAnchor type removed. The new
            // tile-anchor system uses TileAnchor which has its own ZDO key
            // checked below. No need for a typed lookup here.

            // Check ZDO flags directly (anchor component may have been stripped)
            var nview = __instance.GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null)
            {
                var zdo = nview.GetZDO();
                bool isWaterAnchor = zdo.GetBool(WaterMeshCombinerAnchor.IsWaterAnchorHash, false);
                bool isTerrainAnchor = zdo.GetBool("va_is_terrain_combiner_anchor".GetStableHashCode(), false);

                if (isWaterAnchor || isTerrainAnchor)
                {
                    string type = isWaterAnchor ? "water" : "terrain";
                    FiresLogger.LogWarning($"[CustomWaterPatches] Blocked removal of {type} anchor ZDO at {__instance.transform.position}.");
                    return false;
                }
            }

            return true; // Allow normal removal for everything else
        }

        /// <summary>
        /// Prevents WearNTear.Destroy from destroying our anchor ZDOs.
        /// Same protection as Remove but for the Destroy path.
        /// </summary>
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
        [HarmonyPrefix]
        private static bool WearNTear_Destroy_Prefix(WearNTear __instance)
        {
            if (__instance == null) return true;
            // A chunk world's starter generator and first-hammer chest survive every path that ends here: wear, damage
            // and removal.
            if (VerdantsAscent.Terrain.Zones.ZoneModeChunkMachines.IsProtected(__instance)) return false;

            var nview = __instance.GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null)
            {
                var zdo = nview.GetZDO();
                bool isWaterAnchor = zdo.GetBool(WaterMeshCombinerAnchor.IsWaterAnchorHash, false);
                bool isTerrainAnchor = zdo.GetBool("va_is_terrain_combiner_anchor".GetStableHashCode(), false);

                if (isWaterAnchor || isTerrainAnchor)
                {
                    string type = isWaterAnchor ? "water" : "terrain";
                    FiresLogger.LogWarning($"[CustomWaterPatches] Blocked destruction of {type} anchor at {__instance.transform.position}.");
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region World Save/Load Hooks

        /// <summary>
        /// Initialize RPC handlers when ZNet starts.
        /// v5: Anchors restore automatically - no file loading needed.
        /// v6: File-based WaterCombinerCache for reliable client-side restoration.
        /// </summary>
        internal static void OnZNetStart(ZNet __instance)
        {
            // CRITICAL: Don't run during main menu
            if (Game.instance == null)
            {
                return;
            }

            try
            {
                // Initialize RPC handlers for real-time operations
                WaterOperationRPC.Initialize();
                global::VerdantsAscent.Water.Tools.WaterCleanupCommand.EnsureRpcs();   // 0.2.755: va_water_cleanup_spread runs on the server
                global::VerdantsAscent.Terrain.Voxel.FatGroundProbe.EnsureRegistered();   // 0.2.689: va_ground_probe
                global::VerdantsAscent.Water.Core.FiresWaveDepthMatch.EnsureRegistered();   // 0.2.699: wavematch_test server step
                WaterPersistence.Initialize();

                // Initialize file-based water cache (mirrors DirtFloorCombinerCache)
                // This allows clients to restore water from local cache even when
                // ZDO byte arrays haven't synced yet from the server.
                //
                // Guard against null world name: on client reconnect, ZNet.Start()
                // fires before world data is received from the server. GetWorldName()
                // returns null in that case. The cache will be initialized later when
                // Player.OnSpawned fires (world data is available by then).
                string worldName = null;
                long worldSeed = 0;
                try
                {
                    worldName = ZNet.instance?.GetWorldName();
                    worldSeed = ZNet.instance?.GetWorldUID() ?? 0;
                }
                catch { }

                if (!string.IsNullOrEmpty(worldName))
                {
                    WaterCombinerCache.InitializeForWorld(worldName, worldSeed);
                    FiresLogger.LogInfo("[CustomWaterPatches] Water RPC, persistence, and cache initialized");
                }
                else
                {
                    FiresLogger.LogInfo("[CustomWaterPatches] Water RPC and persistence initialized. " +
                        "Cache deferred - world name not available yet (client connecting)");
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[CustomWaterPatches] Failed to initialize water systems: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize water cache and load persisted water data when ZoneSystem starts.
        /// Mirrors DirtFloorPatches.ZoneSystem_Start_Postfix which loads terrain data.
        /// </summary>
        internal static void OnZoneSystemStart(ZoneSystem __instance)
        {
            // CRITICAL: Don't run during main menu
            if (Game.instance == null)
            {
                return;
            }

            // CRITICAL: Ensure water RPCs are initialized.
            // The primary init in ZNet.Start may have been skipped because
            // Game.instance was null at that point (ZNet starts before Game).
            // On re-login this is essential because the old ZRoutedRpc was destroyed.
            try
            {
                WaterOperationRPC.Initialize();
                global::VerdantsAscent.Water.Tools.WaterCleanupCommand.EnsureRpcs();   // 0.2.755: va_water_cleanup_spread runs on the server
                global::VerdantsAscent.Terrain.Voxel.FatGroundProbe.EnsureRegistered();   // 0.2.689: va_ground_probe
                global::VerdantsAscent.Water.Core.FiresWaveDepthMatch.EnsureRegistered();   // 0.2.699: wavematch_test server step
                WaterPersistence.Initialize();
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[CustomWaterPatches] Failed to ensure water RPC init: {ex.Message}");
            }

            // Ensure cache is initialized (backup in case ZNet.Start didn't have world name)
            string worldName = null;
            long worldSeed = 0;
            try
            {
                if (ZNet.instance != null)
                {
                    worldName = ZNet.instance.GetWorldName();
                    worldSeed = ZNet.instance.GetWorldUID();
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(worldName))
            {
                WaterCombinerCache.InitializeForWorld(worldName, worldSeed);
            }

            // Delayed load - wait for world to be fully ready before restoring water from cache
            __instance.StartCoroutine(LoadWaterDataDelayed());
        }

        /// <summary>
        /// Coroutine to load water data from the file cache after the world is fully initialized.
        /// Mirrors DirtFloorPatches.LoadTerrainDataDelayed() for terrain persistence.
        /// 
        /// On server restart, ZDO anchors may not have spawned yet (zones around the player
        /// haven't loaded). This proactively restores water combiners from the local file
        /// cache so water is visible immediately, then the anchors will link up when zones load.
        /// </summary>
        private static System.Collections.IEnumerator LoadWaterDataDelayed()
        {
            // Wait for the world to be fully initialized
            yield return new UnityEngine.WaitForSeconds(2.0f);

            // Don't run during main menu
            if (Game.instance == null)
            {
                yield break;
            }

            if (ZNet.instance == null || WorldGenerator.instance == null)
            {
                FiresLogger.LogInfo("[CustomWaterPatches] World not ready for water data load - will load when zones spawn");
                yield break;
            }

            // Both server and clients restore from their local file cache.
            // The server has the authoritative cache (saved every world save).
            // Clients have a local cache (saved on logout) that provides instant
            // visual restoration. When ZDO anchors spawn (zone load), the anchor's
            // Start() detects existing cache-restored combiners and links to them
            // instead of creating duplicates (deduplication by position).
            bool isServer = ZNet.instance.IsServer();

            try
            {
                if (isServer)
                {
                    // Server: authoritative restore from file cache
                    WaterPersistence.LoadWaterData();
                }
                else
                {
                    // Client: restore from local file cache for instant visuals.
                    // Anchors will link up when zones load from server.
                    RestoreWaterFromClientCache();
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[CustomWaterPatches] Failed to load water data (isServer={isServer}): {ex.Message}");
            }
        }

        /// <summary>
        /// Restores water combiners from the client's local file cache.
        /// Called on client login for instant visual restoration.
        /// When ZDO anchors eventually spawn (zone load), they detect existing
        /// cache-restored combiners and link to them (no duplicates).
        /// </summary>
        private static void RestoreWaterFromClientCache()
        {
            var cache = WaterCombinerCache.GetCurrentCache();
            if (cache == null || cache.Groups == null || cache.Groups.Count == 0)
            {
                FiresLogger.LogInfo("[CustomWaterPatches] Client: no local water cache to restore");
                return;
            }

            int restoredCount = 0;
            foreach (var group in cache.Groups)
            {
                if (group == null || !group.HasValidData) continue;

                // Skip if combiner already exists at this position (e.g., anchor already restored it)
                var existing = WaterMeshCombiner.GetCombinerAt(group.CenterPosition);
                if (existing != null) continue;

                try
                {
                    var combiner = WaterCombinerCache.RestoreFromCache(group, null);
                    if (combiner != null)
                        restoredCount++;
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[CustomWaterPatches] Client: failed to restore water group at {group.CenterPosition}: {ex.Message}");
                }
            }

            if (restoredCount > 0)
                FiresLogger.LogInfo($"[CustomWaterPatches] Client: restored {restoredCount} water combiners from local cache");
            else
                FiresLogger.LogInfo("[CustomWaterPatches] Client: no water combiners to restore from cache");
        }

        /// <summary>
        /// Save water cache on world shutdown (mirrors DirtFloorPatches.ZNet_Shutdown_Prefix).
        /// This ensures the cache is saved even if the server process is stopped without
        /// going through Game.Logout (e.g., dedicated server restart).
        /// </summary>
        internal static void SaveOnShutdown()
        {
            try
            {
                // Force save all water combiners to their anchors
                WaterPersistence.SaveWaterData();

                // Save file cache synchronously (not async - we're shutting down)
                WaterCombinerCache.SaveCache();
                WaterCombinerCache.OnWorldUnload();

                FiresLogger.LogInfo("[CustomWaterPatches] Saved water cache on world shutdown");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[CustomWaterPatches] Failed to save water cache on shutdown: {ex.Message}");
            }
        }

        /// <summary>
        /// Save water data when the world saves. Called from FAT's one ZNet.SaveWorld hook (FatVanillaHooks).
        /// </summary>
        internal static void OnWorldSave()
        {
            try
            {
                WaterPersistence.SaveWaterData();

                // Also save file cache (batched, async)
                WaterCombinerCache.SaveCacheAsync();
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[CustomWaterPatches] Failed to save water data: {ex.Message}");
            }
        }

        /// <summary>
        /// Periodically update water cache (batched saves).
        /// </summary>
        [HarmonyPatch(typeof(Game), "Update")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void Game_Update_Postfix_Water()
        {
            WaterCombinerCache.Update();
            VerdantsAscent.Water.Core.SeasonalWaterFreeze.Tick();   // 0.2.873: Vedr winter freezes/thaws FAT custom water (2 s throttle inside)
        }

        #endregion
    }
}
