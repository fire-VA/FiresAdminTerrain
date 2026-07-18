using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Terrain.Biome;
using FiresCore.Utilities; // EnvironmentBoxController (moved to Core)


namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// Harmony patches for Environment Box functionality.
    /// 
    /// Key patches:
    /// 1. Heightmap.GetBiome - Returns forced biome when inside env box (for terrain paint, vegetation)
    /// 2. Heightmap.FindBiomeClutter - Returns forced biome for clutter/vegetation spawning
    /// 3. ClutterSystem.GetGroundInfo - Returns forced biome for clutter placement
    /// 4. EnvMan.GetBiome - Returns forced biome for environment calculations
    /// 5. Heightmap.RebuildRenderMesh - Applies per-vertex biome colors for visual terrain override
    /// 
    /// NOTE: We intentionally do NOT patch WorldGenerator.GetBiome because:
    /// - It's used by the minimap/map rendering system
    /// - Patching it causes ocean bands to appear on the map where env boxes exist
    /// - The Heightmap.GetBiome patch handles all gameplay biome queries we need
    /// </summary>
    [HarmonyPatch]
    public static class EnvironmentBoxPatches
    {
        #region Reflection Cache

        private static bool _reflectionCached = false;
        private static FieldInfo _renderMeshField;
        private static FieldInfo _tempColorsField;
        private static FieldInfo _isDistantLodField;
        private static FieldInfo _cornerBiomesField;
        private static MethodInfo _rebuildRenderMeshMethod;

        private static void EnsureReflectionCached()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;

            try
            {
                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

                _renderMeshField = typeof(Heightmap).GetField("m_renderMesh", instanceFlags);
                _isDistantLodField = typeof(Heightmap).GetField("m_isDistantLod", instanceFlags);
                _tempColorsField = typeof(Heightmap).GetField("s_tempColors", staticFlags);
                _cornerBiomesField = typeof(Heightmap).GetField("m_cornerBiomes", instanceFlags);
                _rebuildRenderMeshMethod = typeof(Heightmap).GetMethod("RebuildRenderMesh", instanceFlags);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EnvironmentBoxPatches] Failed to cache reflection: {ex.Message}");
            }
        }

        #endregion

        #region State Tracking

        // Track which heightmaps have been modified by env boxes for cleanup
        private static readonly HashSet<int> _modifiedHeightmaps = new HashSet<int>();

        // Fast check flag - avoids iteration when no boxes have biome overrides
        private static bool _hasAnyBiomeOverrideBoxes = false;

        /// <summary>
        /// Call this when an env box's biome setting changes.
        /// </summary>
        public static void InvalidateBiomeOverrideCache()
        {
            _hasAnyBiomeOverrideBoxes = false;
            // Will be recalculated on next check
        }

        /// <summary>
        /// Checks if any environment boxes currently have biome overrides active.
        /// </summary>
        public static bool HasAnyBiomeOverrideBoxes()
        {
            // Check the player's current box first (fast path)
            var playerBox = EnvironmentBoxController.GetPlayerEnvBox();
            if (playerBox != null && playerBox.ForcedBiome != Heightmap.Biome.None)
            {
                _hasAnyBiomeOverrideBoxes = true;
                return true;
            }

            return _hasAnyBiomeOverrideBoxes;
        }

        /// <summary>
        /// Marks that there are active biome override boxes.
        /// Called by EnvironmentBoxController when player enters a box with biome override.
        /// </summary>
        public static void SetHasBiomeOverrideBoxes(bool value)
        {
            _hasAnyBiomeOverrideBoxes = value;
        }

        #endregion

        #region Heightmap.GetBiome Patch

        /// <summary>
        /// Patch Heightmap.GetBiome to return the forced biome when inside an environment box.
        /// 
        /// Method signature: GetBiome(Vector3 point, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        /// 
        /// This affects:
        /// - Terrain paint colors (vanilla)
        /// - Vegetation/clutter spawning
        /// - Creature spawning rules
        /// 
        /// It does NOT affect:
        /// - The actual heightmap biome data
        /// - World generation
        /// - Minimap rendering (which uses WorldGenerator.GetBiome)
        /// </summary>
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetBiome), typeof(Vector3), typeof(float), typeof(bool))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void GetBiome_Postfix(Vector3 point, ref Heightmap.Biome __result)
        {
            // Check if point is inside an env box with biome override
            var envBox = EnvironmentBoxController.GetEnvBoxAtPosition(point);
            if (envBox != null && envBox.ForcedBiome != Heightmap.Biome.None)
            {
                __result = envBox.ForcedBiome;
            }
        }

        #endregion

        #region Heightmap.FindBiomeClutter Patch

        /// <summary>
        /// Patch Heightmap.FindBiomeClutter to return forced biome when inside an env box.
        /// This is a STATIC method used by ClutterSystem.GetPatchBiomes() to determine
        /// which biome's vegetation can spawn in a given area.
        /// 
        /// Without this patch, vegetation/clutter will not change when inside an env box.
        /// </summary>
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.FindBiomeClutter), typeof(Vector3))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void FindBiomeClutter_Postfix(Vector3 point, ref Heightmap.Biome __result)
        {
            // Check if point is inside an env box with biome override
            var envBox = EnvironmentBoxController.GetEnvBoxAtPosition(point);
            if (envBox != null && envBox.ForcedBiome != Heightmap.Biome.None)
            {
                __result = envBox.ForcedBiome;
            }
        }

        #endregion

        #region ClutterSystem.GetGroundInfo Patch

        // Cached layer mask for custom terrain raycast
        private static int _customTerrainLayerMask = 0;

        // Round-89 — one-time proof-of-life logs for clutter on voxel terrain.
        private static int s_voxelClutterLogs = 0;

        // Track which patches have logged NRE to avoid spam
        private static readonly HashSet<Vector2Int> _loggedNrePatches = new HashSet<Vector2Int>();
        private static int _lastClearFrame = 0;
        private const int ClearLoggedPatchesInterval = 18000; // Clear every ~5 minutes at 60fps

        private static int GetTerrainLayerMask()
        {
            if (_customTerrainLayerMask == 0)
            {
                _customTerrainLayerMask = LayerMask.GetMask("terrain");
            }
            return _customTerrainLayerMask;
        }

        /// <summary>
        /// PREFIX patch for ClutterSystem.GetGroundInfo to handle custom terrain.
        /// 
        /// The vanilla method raycasts on the "terrain" layer and then calls:
        ///   hmap = hitInfo.collider.GetComponent&lt;Heightmap&gt;();
        ///   biome = hmap.GetBiome(point);  // NRE if hmap is null!
        /// 
        /// Our custom terrain (CustomHeightmap, DirtFloorMeshCombiner) is on the "terrain" layer
        /// to allow vanilla clutter to spawn, but it doesn't have a vanilla Heightmap component.
        /// 
        /// IMPORTANT: GenerateVegPatch uses hmap for additional checks AFTER GetGroundInfo returns:
        /// - hmap.GetOceanDepth() for ocean depth filtering
        /// - hmap.GetVegetationMask() for vegetation mask filtering
        /// - hmap.IsCleared() for cleared/uncleared filtering
        /// 
        /// So we MUST return a valid Heightmap even for custom terrain. We find the nearest
        /// vanilla heightmap at that position to use for these checks.
        /// </summary>
        [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool ClutterSystem_GetGroundInfo_Prefix(
            ClutterSystem __instance,
            Vector3 p,
            out Vector3 point,
            out Vector3 normal,
            out Heightmap hmap,
            out Heightmap.Biome biome,
            ref bool __result)
        {
            // Initialize out parameters
            point = p;
            normal = Vector3.up;
            hmap = null;
            biome = Heightmap.Biome.Meadows;

            // Do the raycast ourselves (same as vanilla)
            int layerMask = GetTerrainLayerMask();
            RaycastHit hitInfo;

            if (!Physics.Raycast(p + Vector3.up * 500f, Vector3.down, out hitInfo, 1000f, layerMask))
            {
                // No hit - return false
                __result = false;
                return false; // Skip original
            }

            // COMMON CASE FIRST: an ordinary vanilla Heightmap collider is by far
            // the most frequent hit. ClutterSystem fires one raycast per grass
            // candidate — thousands per 8m patch — so doing this cheap single-object
            // GetComponent<Heightmap> BEFORE the GetComponentInParent<TerrainTile>
            // hierarchy walk keeps the common path off the walk entirely. A collider
            // carrying a Heightmap is never one of our custom tiles, so the outcome
            // is identical to checking the tile first.
            var vanillaHeightmap = hitInfo.collider.GetComponent<Heightmap>();
            if (vanillaHeightmap != null)
            {
                point = hitInfo.point;
                normal = hitInfo.normal;
                hmap = vanillaHeightmap;
                biome = vanillaHeightmap.GetBiome(point);

                // Check for env box biome override
                var envBox = EnvironmentBoxController.GetEnvBoxAtPosition(point);
                if (envBox != null && envBox.ForcedBiome != Heightmap.Biome.None)
                {
                    biome = envBox.ForcedBiome;
                }

                __result = true;
                return false; // Skip original since we handled it
            }

            // Round-89 — VOXEL terrain (wrapped zones + brush chunks). This
            // prefix REPLACES vanilla GetGroundInfo entirely, so the old bail
            // below silently starved wrapped zones of ALL clutter ("grass gone
            // when returning"): a voxel collider is neither a vanilla Heightmap
            // nor a TerrainTile. The wrapped zone's Heightmap component is
            // still alive as the data API — use it exactly like the tile path.
            if (VerdantsAscent.Terrain.Voxel.VoxelWorld.ChunkCount > 0
                && hitInfo.collider.GetComponent<VerdantsAscent.Terrain.Voxel.VoxelChunkView>() != null)
            {
                point = hitInfo.point;
                normal = hitInfo.normal;
                hmap = Heightmap.FindHeightmap(point);
                if (hmap == null)
                {
                    __result = false;   // zone data not up yet — no clutter this pass
                    return false;
                }
                biome = hmap.GetBiome(point);
                var envBoxVoxel = EnvironmentBoxController.GetEnvBoxAtPosition(point);
                if (envBoxVoxel != null && envBoxVoxel.ForcedBiome != Heightmap.Biome.None)
                    biome = envBoxVoxel.ForcedBiome;
                if (s_voxelClutterLogs < 3)
                {
                    s_voxelClutterLogs++;
                    Debug.Log($"[FiresVoxel] clutter ground resolved on voxel terrain at {point} biome={biome} (log {s_voxelClutterLogs}/3).");
                }
                __result = true;
                return false;
            }

            // Not a vanilla Heightmap. ONLY now pay for the parent walk — and only
            // when at least one custom tile is actually loaded. A pure-vanilla world,
            // or any area with no placed custom terrain, never walks the hierarchy.
            var customTile = VerdantsAscent.Terrain.Persistence.TerrainTile.AnyActive
                ? hitInfo.collider.GetComponentInParent<VerdantsAscent.Terrain.Persistence.TerrainTile>()
                : null;

            if (customTile == null)
            {
                // Terrain-layer collider with neither a Heightmap nor a custom tile.
                // Vanilla would NRE on hmap.GetBiome here — bail safely (no clutter).
                __result = false;
                return false;
            }

            // It's our custom terrain! Handle it properly.
            point = hitInfo.point;
            normal = hitInfo.normal;

            // CRITICAL: We must return a valid Heightmap for GenerateVegPatch to use
            // for ocean depth, vegetation mask, and cleared checks.
            // Find the vanilla heightmap at this position (there's always one under custom terrain).
            hmap = Heightmap.FindHeightmap(point);

            // If no heightmap found (edge case during zone loading), let vanilla handle it
            // This prevents NREs when heightmaps aren't loaded yet
            if (hmap == null)
            {
                // During zone loading, heightmaps may not be available yet
                // Return true to let vanilla ClutterSystem.GetGroundInfo run and handle it
                // This is safer than proceeding with null heightmap
                return true; // Let vanilla method run
            }

            // Determine biome for this position
            // First check for env box override
            var envBoxAtPoint = EnvironmentBoxController.GetEnvBoxAtPosition(point);
            if (envBoxAtPoint != null && envBoxAtPoint.ForcedBiome != Heightmap.Biome.None)
            {
                biome = envBoxAtPoint.ForcedBiome;
            }
            else if (WorldGenerator.instance != null)
            {
                // Use world generator biome (what vanilla terrain would return)
                biome = WorldGenerator.instance.GetBiome(point);
            }
            else
            {
                biome = Heightmap.Biome.Meadows;
            }

            __result = true;
            return false; // Skip original method - we've handled everything
        }

        /// <summary>
        /// POSTFIX patch for ClutterSystem.GetGroundInfo to handle env box biome overrides
        /// when vanilla terrain is hit. This runs after the original method (if prefix didn't skip it).
        /// 
        /// Note: With the prefix handling all cases, this postfix may not run often,
        /// but it's here as a safety net for any edge cases.
        /// </summary>
        [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void ClutterSystem_GetGroundInfo_Postfix(Vector3 p, Heightmap hmap, ref Heightmap.Biome biome, bool __result)
        {
            // Guard: Skip during world shutdown/loading
            if (ZNet.instance == null) return;

            // Only override if the original method succeeded and we have a valid heightmap
            if (!__result) return;
            if (hmap == null) return;

            // Fast check - skip if no biome override boxes are active
            if (!HasAnyBiomeOverrideBoxes()) return;

            // Additional guard: Make sure heightmap hasn't been destroyed
            try
            {
                if (hmap.gameObject == null) return;
            }
            catch
            {
                return;
            }

            // Check if point is inside an env box with biome override
            var envBox = EnvironmentBoxController.GetEnvBoxAtPosition(p);
            if (envBox != null && envBox.ForcedBiome != Heightmap.Biome.None)
            {
                biome = envBox.ForcedBiome;
            }
        }

        #endregion

        #region ClutterSystem.GetPatchBiomes Patch

        /// <summary>
        /// CRITICAL PATCH: This is the key to making clutter spawn for the forced biome.
        /// 
        /// GetPatchBiomes() returns a biome mask that determines WHICH Clutter definitions
        /// are eligible for the patch. If this returns Meadows, only Meadows clutter can spawn.
        /// 
        /// By patching this to include the forced biome, we allow Mountain/Swamp/etc clutter
        /// definitions to be considered for spawning in the patch.
        /// 
        /// Method signature: private Heightmap.Biome GetPatchBiomes(Vector3 center, float halfSize)
        /// </summary>
        [HarmonyPatch(typeof(ClutterSystem), "GetPatchBiomes")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void ClutterSystem_GetPatchBiomes_Postfix(Vector3 center, float halfSize, ref Heightmap.Biome __result)
        {
            // Check if the patch center or any corner is inside an env box with biome override
            var envBox = EnvironmentBoxController.GetEnvBoxAtPosition(center);
            if (envBox == null)
            {
                // Check corners too
                envBox = EnvironmentBoxController.GetEnvBoxAtPosition(new Vector3(center.x - halfSize, center.y, center.z - halfSize));
            }
            if (envBox == null)
            {
                envBox = EnvironmentBoxController.GetEnvBoxAtPosition(new Vector3(center.x + halfSize, center.y, center.z - halfSize));
            }
            if (envBox == null)
            {
                envBox = EnvironmentBoxController.GetEnvBoxAtPosition(new Vector3(center.x - halfSize, center.y, center.z + halfSize));
            }
            if (envBox == null)
            {
                envBox = EnvironmentBoxController.GetEnvBoxAtPosition(new Vector3(center.x + halfSize, center.y, center.z + halfSize));
            }

            if (envBox != null && envBox.ForcedBiome != Heightmap.Biome.None)
            {
                // Add the forced biome to the result mask
                // This allows clutter definitions for that biome to be considered
                __result |= envBox.ForcedBiome;

                // If we want ONLY the forced biome's clutter (not mixed), use:
                // __result = envBox.ForcedBiome;
            }
        }

        #endregion

        #region ClutterSystem.GenerateVegPatch Error Handling

        /// <summary>
        /// PREFIX patch for ClutterSystem.GenerateVegPatch.
        ///
        /// On a dedicated server there is no rendered camera and no point generating
        /// vegetation patches at all - but Unity still ticks ClutterSystem because
        /// it's a MonoBehaviour, and on top of our custom-terrain layer it produces
        /// a steady stream of NullReferenceExceptions in zones the server has loaded
        /// for ZDO ownership but never renders. Skipping the method entirely on
        /// dedicated servers eliminates the NRE storm at the root rather than
        /// suppressing it via a Finalizer (which still produced log spam).
        ///
        /// On clients this patch falls through to the original method, then the
        /// Finalizer below catches any remaining NREs from custom-terrain edge
        /// cases (zone transitions, Heightmap destroyed mid-call, etc.).
        /// </summary>
        [HarmonyPatch(typeof(ClutterSystem), "GenerateVegPatch")]
        [HarmonyPrefix]
        private static bool ClutterSystem_GenerateVegPatch_SkipOnServer()
        {
            return !(ZNet.instance != null && ZNet.instance.IsDedicated());
        }

        /// <summary>
        /// FINALIZER patch for ClutterSystem.GenerateVegPatch to catch any residual
        /// NullReferenceExceptions that slip past the dedicated-server skip above.
        /// On a client these can still occur during zone transitions, with custom
        /// terrain on the "terrain" layer that lacks a vanilla Heightmap, or when
        /// GetGroundInfo returns a stale hmap reference.
        /// </summary>
        [HarmonyPatch(typeof(ClutterSystem), "GenerateVegPatch")]
        [HarmonyFinalizer]
        private static Exception ClutterSystem_GenerateVegPatch_Finalizer(Exception __exception, Vector2Int patchID)
        {
            if (__exception is NullReferenceException)
            {
                // Suppress NRE - this happens with custom terrain or during zone transitions
                // The system will retry on the next frame

                // Clear the logged patches set periodically to allow re-logging after a long time
                if (Time.frameCount - _lastClearFrame > ClearLoggedPatchesInterval)
                {
                    _loggedNrePatches.Clear();
                    _lastClearFrame = Time.frameCount;
                }

                // Only log each unique patch once per session, and only when
                // timing diagnostics are on — these NREs are expected transient
                // zone-load noise on our voxel terrain (the finalizer retries).
                if (_loggedNrePatches.Add(patchID)
                    && VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.LogTimings != null
                    && VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.LogTimings.Value)
                {
                    Debug.LogWarning($"[EnvironmentBoxPatches] Suppressed ClutterSystem NRE for patch {patchID} (custom terrain or zone loading - heightmap unavailable)");
                }

                return null; // Swallow the exception
            }
            return __exception; // Let other exceptions propagate
        }

        #endregion

        #region EnvMan.GetBiome Patch

        /// <summary>
        /// Patch EnvMan.GetBiome to return forced biome when camera is inside env box.
        /// This affects environment/weather calculations.
        /// EnvMan.GetBiome is a private method that gets the biome at camera position.
        /// </summary>
        [HarmonyPatch(typeof(EnvMan), "GetBiome")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void EnvMan_GetBiome_Postfix(ref Heightmap.Biome __result)
        {
            // Check if player is inside an env box
            var envBox = EnvironmentBoxController.GetPlayerEnvBox();
            if (envBox != null && envBox.ForcedBiome != Heightmap.Biome.None)
            {
                __result = envBox.ForcedBiome;
            }
        }

        #endregion

        #region Heightmap.RebuildRenderMesh Patch - Corner Biome Override

        // Debug logging flag - set to true to diagnose terrain issues
        private static bool _debugTerrainPatches = false; // Disabled after successful fix

        /// <summary>
        /// PREFIX patch on RebuildRenderMesh to modify corner biomes BEFORE the mesh is built.
        /// This is the key to getting terrain textures to change - the shader selects textures
        /// based on m_cornerBiomes, and vertex colors blend between them.
        /// 
        /// By setting corner biomes in a PREFIX (before mesh generation), we ensure the
        /// shader has the correct biome textures available to blend.
        /// 
        /// This does NOT affect terrain HEIGHT because Heightmap.Regenerate() uses
        /// WorldGenerator.GetBiome() for height calculation, which we don't patch.
        /// </summary>
        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Heightmap_RebuildRenderMesh_Prefix(Heightmap __instance)
        {
            // Fast exits
            if (__instance == null) return;
            if (ZNet.instance == null) return;

            bool hasBiomeBoxes = HasAnyBiomeOverrideBoxes();
            if (_debugTerrainPatches)
            {
                Debug.Log($"[EnvBoxPatches] PREFIX: HasAnyBiomeOverrideBoxes={hasBiomeBoxes}");
            }

            if (!hasBiomeBoxes) return;

            EnsureReflectionCached();
            if (_cornerBiomesField == null)
            {
                if (_debugTerrainPatches) Debug.LogWarning("[EnvBoxPatches] PREFIX: _cornerBiomesField is NULL!");
                return;
            }

            // Get player's env box
            var playerEnvBox = EnvironmentBoxController.GetPlayerEnvBox();
            if (playerEnvBox == null)
            {
                if (_debugTerrainPatches) Debug.Log("[EnvBoxPatches] PREFIX: playerEnvBox is NULL");
                return;
            }

            if (playerEnvBox.ForcedBiome == Heightmap.Biome.None)
            {
                if (_debugTerrainPatches) Debug.Log("[EnvBoxPatches] PREFIX: ForcedBiome is None");
                return;
            }

            if (_debugTerrainPatches)
            {
                Debug.Log($"[EnvBoxPatches] PREFIX: ForcedBiome={playerEnvBox.ForcedBiome}");
            }

            // Skip distant LOD heightmaps
            if (_isDistantLodField != null)
            {
                try
                {
                    bool isDistantLod = (bool)_isDistantLodField.GetValue(__instance);
                    if (isDistantLod)
                    {
                        if (_debugTerrainPatches) Debug.Log("[EnvBoxPatches] PREFIX: Skipping distant LOD");
                        return;
                    }
                }
                catch { return; }
            }

            // Get heightmap bounds
            var hmPos = __instance.transform.position;
            float scale = __instance.m_scale;
            int width = __instance.m_width;
            float halfSize = width * scale * 0.5f;

            // Quick bounds check
            Bounds hmBounds = new Bounds(hmPos, new Vector3(width * scale, 1000f, width * scale));
            Bounds envBoxBounds = playerEnvBox.GetWorldBounds();

            if (!hmBounds.Intersects(envBoxBounds))
            {
                if (_debugTerrainPatches) Debug.Log($"[EnvBoxPatches] PREFIX: Bounds don't intersect. HM={hmPos}, EnvBox={envBoxBounds.center}");
                return;
            }

            if (_debugTerrainPatches)
            {
                Debug.Log($"[EnvBoxPatches] PREFIX: Processing heightmap at {hmPos}, envBox bounds={envBoxBounds}");
            }

            try
            {
                // Get the corner biomes array
                var cornerBiomes = _cornerBiomesField.GetValue(__instance) as Heightmap.Biome[];
                if (cornerBiomes == null || cornerBiomes.Length < 4)
                {
                    if (_debugTerrainPatches) Debug.LogWarning("[EnvBoxPatches] PREFIX: cornerBiomes array is null or too small");
                    return;
                }

                if (_debugTerrainPatches)
                {
                    Debug.Log($"[EnvBoxPatches] PREFIX: Original corners: [{cornerBiomes[0]}, {cornerBiomes[1]}, {cornerBiomes[2]}, {cornerBiomes[3]}]");
                }

                // Pre-compute corner positions (XZ only - Y doesn't matter for terrain)
                float c0x = hmPos.x - halfSize, c0z = hmPos.z - halfSize;
                float c1x = hmPos.x + halfSize, c1z = hmPos.z - halfSize;
                float c2x = hmPos.x - halfSize, c2z = hmPos.z + halfSize;
                float c3x = hmPos.x + halfSize, c3z = hmPos.z + halfSize;

                // Get env box 2D bounds (ignore Y - heightmap is at Y=0 but terrain is at world height)
                Bounds envBox2DBounds = playerEnvBox.GetWorldBounds();
                float minX = envBox2DBounds.min.x;
                float maxX = envBox2DBounds.max.x;
                float minZ = envBox2DBounds.min.z;
                float maxZ = envBox2DBounds.max.z;

                Heightmap.Biome forcedBiome = playerEnvBox.ForcedBiome;
                bool anyChanged = false;

                // Check each corner using 2D (XZ) containment
                bool c0Inside = c0x >= minX && c0x <= maxX && c0z >= minZ && c0z <= maxZ;
                bool c1Inside = c1x >= minX && c1x <= maxX && c1z >= minZ && c1z <= maxZ;
                bool c2Inside = c2x >= minX && c2x <= maxX && c2z >= minZ && c2z <= maxZ;
                bool c3Inside = c3x >= minX && c3x <= maxX && c3z >= minZ && c3z <= maxZ;

                if (_debugTerrainPatches)
                {
                    Debug.Log($"[EnvBoxPatches] PREFIX: 2D bounds: X=[{minX:F1}, {maxX:F1}], Z=[{minZ:F1}, {maxZ:F1}]");
                    Debug.Log($"[EnvBoxPatches] PREFIX: Corner positions: c0=({c0x:F1}, {c0z:F1}), c1=({c1x:F1}, {c1z:F1}), c2=({c2x:F1}, {c2z:F1}), c3=({c3x:F1}, {c3z:F1})");
                    Debug.Log($"[EnvBoxPatches] PREFIX: Corner containment: c0={c0Inside}, c1={c1Inside}, c2={c2Inside}, c3={c3Inside}");
                }

                if (c0Inside) { cornerBiomes[0] = forcedBiome; anyChanged = true; }
                if (c1Inside) { cornerBiomes[1] = forcedBiome; anyChanged = true; }
                if (c2Inside) { cornerBiomes[2] = forcedBiome; anyChanged = true; }
                if (c3Inside) { cornerBiomes[3] = forcedBiome; anyChanged = true; }

                if (anyChanged)
                {
                    _cornerBiomesField.SetValue(__instance, cornerBiomes);
                    _modifiedHeightmaps.Add(__instance.GetInstanceID());

                    if (_debugTerrainPatches)
                    {
                        Debug.Log($"[EnvBoxPatches] PREFIX: SET corners to [{cornerBiomes[0]}, {cornerBiomes[1]}, {cornerBiomes[2]}, {cornerBiomes[3]}]");
                    }
                }
                else
                {
                    if (_debugTerrainPatches) Debug.Log("[EnvBoxPatches] PREFIX: No corners inside env box");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EnvironmentBoxPatches] Error in RebuildRenderMesh prefix: {ex.Message}");
            }
        }

        #endregion

        #region Heightmap.RebuildRenderMesh Patch - Terrain Visual Override

        /// <summary>
        /// Patch Heightmap.RebuildRenderMesh to apply per-vertex biome colors.
        /// This handles BOTH:
        /// 1. Environment Box biome overrides (temporary, while player is inside)
        /// 2. Persistent biome paint overrides (permanent, stored in TerrainComp ZDO)
        /// 
        /// Priority: Persistent paints > Env Box overrides > World biome
        /// This ensures painted areas stay painted even inside env boxes.
        /// </summary>
        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Heightmap_RebuildRenderMesh_Postfix(Heightmap __instance)
        {
            // Fast exits
            if (__instance == null) return;
            if (ZNet.instance == null) return;

            // Check if we have ANY reason to modify vertex colors
            bool hasBiomeBoxes = HasAnyBiomeOverrideBoxes();
            bool hasPaintedAreas = BiomePaintPersistence.HasAnyPaintedAreas();

            if (!hasBiomeBoxes && !hasPaintedAreas)
            {
                return;
            }

            // Ensure reflection is cached
            EnsureReflectionCached();

            // Skip distant LOD heightmaps
            if (_isDistantLodField != null)
            {
                try
                {
                    bool isDistantLod = (bool)_isDistantLodField.GetValue(__instance);
                    if (isDistantLod) return;
                }
                catch { return; }
            }

            // Get render mesh via reflection
            Mesh renderMesh = null;
            if (_renderMeshField != null)
            {
                try
                {
                    renderMesh = _renderMeshField.GetValue(__instance) as Mesh;
                    if (renderMesh == null) return;
                }
                catch { return; }
            }
            else return;

            // Get the static temp colors list via reflection
            List<Color32> tempColors = null;
            if (_tempColorsField != null)
            {
                try
                {
                    tempColors = _tempColorsField.GetValue(null) as List<Color32>;
                    if (tempColors == null) return;
                }
                catch { return; }
            }
            else return;

            // Get heightmap bounds
            var hmPos = __instance.transform.position;
            float scale = __instance.m_scale;
            int width = __instance.m_width;
            float halfSize = width * scale * 0.5f;

            // Calculate world position of heightmap origin (bottom-left corner)
            Vector3 origin = hmPos - new Vector3(halfSize, 0f, halfSize);

            // Get player's current env box (may be null)
            var playerEnvBox = EnvironmentBoxController.GetPlayerEnvBox();
            Heightmap.Biome envBoxBiome = Heightmap.Biome.None;
            float envMinX = 0, envMaxX = 0, envMinZ = 0, envMaxZ = 0;

            if (playerEnvBox != null && playerEnvBox.ForcedBiome != Heightmap.Biome.None)
            {
                envBoxBiome = playerEnvBox.ForcedBiome;
                Bounds envBoxBounds = playerEnvBox.GetWorldBounds();
                envMinX = envBoxBounds.min.x;
                envMaxX = envBoxBounds.max.x;
                envMinZ = envBoxBounds.min.z;
                envMaxZ = envBoxBounds.max.z;
            }

            try
            {
                int num = width + 1;
                bool anyChanged = false;
                int changedCount = 0;

                // Clear and reuse the static temp colors list
                tempColors.Clear();

                // Iterate through each vertex position
                for (int z = 0; z < num; z++)
                {
                    for (int x = 0; x < num; x++)
                    {
                        // Calculate world position for this vertex
                        float worldX = origin.x + x * scale;
                        float worldZ = origin.z + z * scale;

                        // Priority 1: Check for persistent biome paint
                        var paintData = BiomePaintPersistence.GetPaintAtPosition(worldX, worldZ);
                        if (paintData != null)
                        {
                            // Apply painted biome color
                            tempColors.Add(Heightmap.GetBiomeColor(paintData.Biome));
                            anyChanged = true;
                            changedCount++;
                            continue;
                        }

                        // Priority 2: Check for env box override
                        if (envBoxBiome != Heightmap.Biome.None)
                        {
                            bool insideEnvBox = worldX >= envMinX && worldX <= envMaxX &&
                                                worldZ >= envMinZ && worldZ <= envMaxZ;

                            if (insideEnvBox)
                            {
                                tempColors.Add(Heightmap.GetBiomeColor(envBoxBiome));
                                anyChanged = true;
                                changedCount++;
                                continue;
                            }
                        }

                        // Priority 3: Use original world biome color
                        Heightmap.Biome biome = WorldGenerator.instance.GetBiome(worldX, worldZ);
                        tempColors.Add(Heightmap.GetBiomeColor(biome));
                    }
                }

                if (anyChanged)
                {
                    renderMesh.SetColors(tempColors);
                    _modifiedHeightmaps.Add(__instance.GetInstanceID());
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EnvironmentBoxPatches] Error applying biome colors: {ex.Message}");
            }
        }

        #endregion

        #region Terrain Refresh Methods

        /// <summary>
        /// Forces all heightmaps within/near a given bounds to regenerate their render meshes.
        /// Call this when entering/exiting an env box with biome override.
        /// </summary>
        public static void RefreshHeightmapsInBounds(Bounds bounds)
        {
            if (ZNet.instance == null) return;

            Debug.Log($"[EnvBoxPatches] RefreshHeightmapsInBounds called: center={bounds.center}, size={bounds.size}");

            EnsureReflectionCached();

            var heightmaps = UnityEngine.Object.FindObjectsByType<Heightmap>(UnityEngine.FindObjectsSortMode.None);
            Debug.Log($"[EnvBoxPatches] Found {heightmaps.Length} heightmaps in world");

            int regeneratedCount = 0;
            foreach (var hm in heightmaps)
            {
                if (hm == null) continue;

                // Skip distant LODs
                if (_isDistantLodField != null)
                {
                    try
                    {
                        bool isDistantLod = (bool)_isDistantLodField.GetValue(hm);
                        if (isDistantLod) continue;
                    }
                    catch { continue; }
                }

                // Check if heightmap bounds overlap with our bounds
                float hmSize = hm.m_width * hm.m_scale;
                Vector3 hmCenter = hm.transform.position;
                Bounds hmBounds = new Bounds(hmCenter, new Vector3(hmSize, 1000f, hmSize));

                if (bounds.Intersects(hmBounds))
                {
                    // Force terrain mesh regeneration - this triggers our RebuildRenderMesh patch
                    try
                    {
                        Debug.Log($"[EnvBoxPatches] Regenerating heightmap at {hmCenter}");
                        hm.Regenerate();
                        regeneratedCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[EnvironmentBoxPatches] Failed to regenerate heightmap: {ex.Message}");
                    }
                }
            }

            Debug.Log($"[EnvBoxPatches] RefreshHeightmapsInBounds complete: regenerated {regeneratedCount} heightmaps");
        }

        /// <summary>
        /// Clears all modified heightmaps and forces them to regenerate with original biome colors.
        /// Call this when exiting an env box.
        /// </summary>
        public static void RestoreModifiedHeightmaps()
        {
            if (_modifiedHeightmaps.Count == 0) return;

            var heightmaps = UnityEngine.Object.FindObjectsByType<Heightmap>(UnityEngine.FindObjectsSortMode.None);
            foreach (var hm in heightmaps)
            {
                if (hm == null) continue;

                if (_modifiedHeightmaps.Contains(hm.GetInstanceID()))
                {
                    try
                    {
                        hm.Regenerate();
                    }
                    catch { }
                }
            }

            _modifiedHeightmaps.Clear();
        }

        #endregion

        #region Clutter/Vegetation Refresh

        /// <summary>
        /// Forces ClutterSystem to regenerate within a given bounds.
        /// This updates grass/vegetation to match the biome override.
        /// </summary>
        public static void RefreshClutterInBounds(Bounds bounds)
        {
            if (ClutterSystem.instance == null) return;

            try
            {
                // ClutterSystem.ResetGrass forces grass to regenerate in an area
                // The grass will query GetBiome which our patch intercepts
                Vector3 center = bounds.center;
                float radius = Mathf.Max(bounds.extents.x, bounds.extents.z) + 10f;

                // Clear existing clutter in the area
                ClutterSystem.instance.ResetGrass(center, radius);

                Debug.Log($"[EnvironmentBoxPatches] Refreshed clutter at {center} with radius {radius}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EnvironmentBoxPatches] Failed to refresh clutter: {ex.Message}");
            }
        }

        #endregion

        #region TerrainComp.DoOperation Patch - Vanilla Paint Integration

        /// <summary>
        /// Patch TerrainComp's internal terrain painting to:
        /// 1. Update env box clutter spawner paint mask
        /// 2. Update our custom terrain (DirtFloorMeshCombiner) paint AND vertex colors
        /// 
        /// This is the KEY to making vanilla paint tools (hoe, cultivator, Infinity Hammer)
        /// work properly on our custom terrain.
        /// </summary>
        [HarmonyPatch(typeof(TerrainComp), "DoOperation")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void TerrainComp_DoOperation_Postfix(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier)
        {
            // Only care about paint operations
            if (!modifier.m_paintCleared) return;

            float radius = modifier.m_paintRadius;

            // 1. Check if this position is inside an env box and update clutter spawner
            var envBox = EnvironmentBoxController.GetEnvBoxAtPosition(pos);
            if (envBox != null)
            {
                var clutterSpawner = envBox.GetComponent<EnvironmentBoxClutterSpawner>();
                if (clutterSpawner != null)
                {
                    clutterSpawner.PaintAt(pos, modifier.m_paintType, radius);
                }
            }

            // Phase F: legacy DirtFloorMeshCombiner update branch removed.
            // Vanilla terrain paint now flows through VanillaTerrainOpScrubber
            // (Modules/CustomTerrain/Lifecycle/) which clears our biome
            // overlay + clutter + grass in radius without touching the
            // custom-tile data. ConvertVanillaPaintType helper deleted along
            // with the DirtFloorController.PaintType enum.
        }

        #endregion

        // NOTE: WorldGenerator.GetBiome patch was REMOVED in Iteration 8
        // It was causing map corruption (ocean bands appearing on land)
        // The map rendering system uses WorldGenerator.GetBiome and patching it
        // caused incorrect biome values to be returned for map pixels.
        // The Heightmap.GetBiome patch handles all gameplay needs.

        #region Helper Methods

        /// <summary>
        /// Patch to integrate with our custom terrain paint system.
        /// When painting inside an env box, use the forced biome's colors.
        /// </summary>
        public static Heightmap.Biome GetEffectiveBiome(Vector3 worldPos)
        {
            // First check env box
            var envBox = EnvironmentBoxController.GetEnvBoxAtPosition(worldPos);
            if (envBox != null && envBox.ForcedBiome != Heightmap.Biome.None && envBox.AffectsTerrainPaint)
            {
                return envBox.ForcedBiome;
            }

            // Fall back to world biome
            return WorldGenerator.instance?.GetBiome(worldPos) ?? Heightmap.Biome.Meadows;
        }

        /// <summary>
        /// Gets the biome paint colors for a given biome.
        /// Used by our custom terrain system when inside an env box.
        /// </summary>
        public static Color GetBiomeDirtColor(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows:
                    return new Color(0.45f, 0.35f, 0.25f);
                case Heightmap.Biome.BlackForest:
                    return new Color(0.3f, 0.25f, 0.2f);
                case Heightmap.Biome.Swamp:
                    return new Color(0.2f, 0.18f, 0.12f);
                case Heightmap.Biome.Mountain:
                    return new Color(0.5f, 0.5f, 0.5f);
                case Heightmap.Biome.Plains:
                    return new Color(0.6f, 0.5f, 0.35f);
                case Heightmap.Biome.Mistlands:
                    return new Color(0.25f, 0.2f, 0.25f);
                case Heightmap.Biome.AshLands:
                    return new Color(0.3f, 0.15f, 0.1f);
                case Heightmap.Biome.DeepNorth:
                    return new Color(0.7f, 0.75f, 0.8f);
                default:
                    return new Color(0.4f, 0.3f, 0.2f);
            }
        }

        /// <summary>
        /// Gets the grass/vegetation tint for a given biome.
        /// </summary>
        public static Color GetBiomeGrassColor(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows:
                    return new Color(0.4f, 0.6f, 0.25f);
                case Heightmap.Biome.BlackForest:
                    return new Color(0.25f, 0.4f, 0.2f);
                case Heightmap.Biome.Swamp:
                    return new Color(0.2f, 0.35f, 0.15f);
                case Heightmap.Biome.Mountain:
                    return new Color(0.3f, 0.35f, 0.25f);
                case Heightmap.Biome.Plains:
                    return new Color(0.55f, 0.6f, 0.3f);
                case Heightmap.Biome.Mistlands:
                    return new Color(0.35f, 0.3f, 0.4f);
                case Heightmap.Biome.AshLands:
                    return new Color(0.4f, 0.25f, 0.15f);
                case Heightmap.Biome.DeepNorth:
                    return new Color(0.5f, 0.55f, 0.5f);
                default:
                    return new Color(0.4f, 0.55f, 0.25f);
            }
        }

        #endregion
    }
}
