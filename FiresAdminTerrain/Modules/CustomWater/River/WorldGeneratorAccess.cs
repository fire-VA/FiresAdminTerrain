using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Cached reflection accessors for private WorldGenerator fields and methods.
    /// These are private in the Valheim assembly - we access them via Harmony's AccessTools.
    /// 
    /// Fields accessed:
    /// - m_rivers: List&lt;WorldGenerator.River&gt; - generated river paths
    /// - m_streams: List&lt;WorldGenerator.River&gt; - generated stream paths
    /// - m_lakes: List&lt;Vector2&gt; - detected lake positions (XZ)
    /// - m_world: World - the current world data (needed to check m_menu)
    /// 
    /// Methods accessed:
    /// - GetRiverWeight: void(float wx, float wy, out float weight, out float width)
    ///   This is the EXACT function Heightmap biome methods call via AddRivers() to carve
    ///   river depressions. Where weight > 0, the terrain is depressed to form a river channel.
    ///   The width output gives the weighted average river width at that point.
    /// 
    /// Better Continents compatibility:
    /// - Detects if BC is loaded and whether HeightmapOverrideAll is active
    /// - When HeightmapOverrideAll = true, terrain has no river channels even though
    ///   m_rivers data exists - auto water generation should be skipped
    /// - BC never patches GetRiverWeight(), so our channel detection always works
    /// - BC can disable rivers (RiversEnabled=false), which empties m_rivers - handled naturally
    /// </summary>
    public static class WorldGeneratorAccess
    {
        private static FieldInfo _riversField;
        private static FieldInfo _streamsField;
        private static FieldInfo _lakesField;
        private static FieldInfo _worldField;
        private static MethodInfo _getRiverWeightMethod;
        private static bool _initialized;

        // Better Continents reflection cache
        private static bool _bcInitialized;
        private static bool _bcLoaded;
        private static FieldInfo _bcSettingsField;
        private static FieldInfo _bcEnabledField;
        private static FieldInfo _bcHeightmapOverrideAllField;
        private static FieldInfo _bcRiversEnabledField;
        private static PropertyInfo _bcHasHeightMapProp;
        private static PropertyInfo _bcShouldOverrideAllProp;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            _riversField = AccessTools.Field(typeof(WorldGenerator), "m_rivers");
            _streamsField = AccessTools.Field(typeof(WorldGenerator), "m_streams");
            _lakesField = AccessTools.Field(typeof(WorldGenerator), "m_lakes");
            _worldField = AccessTools.Field(typeof(WorldGenerator), "m_world");

            // Vanilla signature: private void GetRiverWeight(float wx, float wy, out float weight, out float width)
            _getRiverWeightMethod = AccessTools.Method(typeof(WorldGenerator), "GetRiverWeight",
                new[] { typeof(float), typeof(float), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() });

            if (_riversField == null) FiresLogger.LogWarning("[WorldGeneratorAccess] Could not find field m_rivers");
            if (_streamsField == null) FiresLogger.LogWarning("[WorldGeneratorAccess] Could not find field m_streams");
            if (_lakesField == null) FiresLogger.LogWarning("[WorldGeneratorAccess] Could not find field m_lakes");
            if (_worldField == null) FiresLogger.LogWarning("[WorldGeneratorAccess] Could not find field m_world");
            if (_getRiverWeightMethod == null) FiresLogger.LogWarning("[WorldGeneratorAccess] Could not find method GetRiverWeight(float, float, out float, out float)");
        }

        /// <summary>Gets WorldGenerator.m_rivers (list of river paths between lakes).</summary>
        public static List<WorldGenerator.River> GetRivers(WorldGenerator wg)
        {
            EnsureInitialized();
            return _riversField?.GetValue(wg) as List<WorldGenerator.River>;
        }

        /// <summary>Gets WorldGenerator.m_streams (list of stream paths).</summary>
        public static List<WorldGenerator.River> GetStreams(WorldGenerator wg)
        {
            EnsureInitialized();
            return _streamsField?.GetValue(wg) as List<WorldGenerator.River>;
        }

        /// <summary>Gets WorldGenerator.m_lakes (list of lake center positions in XZ).</summary>
        public static List<Vector2> GetLakes(WorldGenerator wg)
        {
            EnsureInitialized();
            return _lakesField?.GetValue(wg) as List<Vector2>;
        }

        /// <summary>Gets WorldGenerator.m_world (current world data).</summary>
        public static World GetWorld(WorldGenerator wg)
        {
            EnsureInitialized();
            return _worldField?.GetValue(wg) as World;
        }

        /// <summary>Returns true if the WorldGenerator is in menu/preview mode.</summary>
        public static bool IsMenuWorld(WorldGenerator wg)
        {
            var world = GetWorld(wg);
            return world == null || world.m_menu;
        }

        /// <summary>
        /// Calls WorldGenerator.GetRiverWeight(wx, wy, out weight, out width) via reflection.
        /// Returns the river weight (0-1 influence) and width at the given world XZ position.
        /// Where weight > 0, the terrain is carved to form a river/stream channel.
        /// This is the EXACT function that AddRivers() calls in every biome height method.
        /// </summary>
        /// <param name="wg">The WorldGenerator instance</param>
        /// <param name="wx">World X coordinate</param>
        /// <param name="wz">World Z coordinate (passed as wy to vanilla)</param>
        /// <param name="weight">Output: 0-1 river influence (0 = not river, 1 = center of river)</param>
        /// <param name="width">Output: weighted average river width at this point</param>
        /// <returns>True if the method was called successfully</returns>
        public static bool GetRiverWeight(WorldGenerator wg, float wx, float wz, out float weight, out float width)
        {
            EnsureInitialized();
            weight = 0f;
            width = 0f;
            if (_getRiverWeightMethod == null || wg == null) return false;

            try
            {
                // Vanilla signature: void GetRiverWeight(float wx, float wy, out float weight, out float width)
                object[] args = new object[] { wx, wz, 0f, 0f };
                _getRiverWeightMethod.Invoke(wg, args);
                weight = (float)args[2];
                width = (float)args[3];
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if GetRiverWeight is available via reflection.
        /// </summary>
        public static bool HasRiverWeightAccess
        {
            get
            {
                EnsureInitialized();
                return _getRiverWeightMethod != null;
            }
        }

        #region Better Continents Detection

        private static void EnsureBCInitialized()
        {
            if (_bcInitialized) return;
            _bcInitialized = true;

            try
            {
                // Look for BetterContinents assembly
                var bcType = Type.GetType("BetterContinents.BetterContinents, BetterContinents");
                if (bcType == null)
                {
                    _bcLoaded = false;
                    return;
                }

                _bcLoaded = true;
                _bcSettingsField = AccessTools.Field(bcType, "Settings");

                if (_bcSettingsField != null)
                {
                    var settingsType = _bcSettingsField.FieldType;
                    _bcEnabledField = AccessTools.Field(settingsType, "EnabledForThisWorld");
                    _bcHeightmapOverrideAllField = AccessTools.Field(settingsType, "HeightmapOverrideAll");
                    _bcRiversEnabledField = AccessTools.Field(settingsType, "RiversEnabled");
                    _bcHasHeightMapProp = AccessTools.Property(settingsType, "HasHeightMap");
                    _bcShouldOverrideAllProp = AccessTools.Property(settingsType, "ShouldHeightMapOverrideAll");
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WorldGeneratorAccess] Failed to initialize BC detection: {ex.Message}");
                _bcLoaded = false;
            }
        }

        /// <summary>Returns true if Better Continents mod is loaded.</summary>
        public static bool IsBetterContinentsLoaded
        {
            get
            {
                EnsureBCInitialized();
                return _bcLoaded;
            }
        }

        /// <summary>Returns true if BC is enabled for the current world.</summary>
        public static bool IsBetterContinentsEnabled
        {
            get
            {
                EnsureBCInitialized();
                if (!_bcLoaded || _bcSettingsField == null || _bcEnabledField == null) return false;

                try
                {
                    var settings = _bcSettingsField.GetValue(null);
                    if (settings == null) return false;
                    return (bool)_bcEnabledField.GetValue(settings);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Returns true if BC's HeightmapOverrideAll is active (heightmap replaces ALL terrain generation).
        /// When true, river channels are NOT carved into terrain even though m_rivers has data.
        /// GetBiomeHeight returns GetBaseHeight*200 directly, bypassing AddRivers().
        /// Auto water generation should be skipped in this scenario.
        /// </summary>
        public static bool IsBCHeightmapOverrideAll
        {
            get
            {
                EnsureBCInitialized();
                if (!_bcLoaded || _bcSettingsField == null) return false;

                try
                {
                    var settings = _bcSettingsField.GetValue(null);
                    if (settings == null) return false;

                    // Use the ShouldHeightMapOverrideAll property if available (checks both HasHeightMap AND HeightmapOverrideAll)
                    if (_bcShouldOverrideAllProp != null)
                        return (bool)_bcShouldOverrideAllProp.GetValue(settings, null);

                    // Fallback: check both fields manually
                    if (_bcHeightmapOverrideAllField == null || _bcHasHeightMapProp == null) return false;
                    bool overrideAll = (bool)_bcHeightmapOverrideAllField.GetValue(settings);
                    bool hasHeightMap = (bool)_bcHasHeightMapProp.GetValue(settings, null);
                    return overrideAll && hasHeightMap;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Returns true if BC has rivers enabled. When false, m_rivers will be empty
        /// (BC's PlaceRivers prefix returns an empty list).
        /// </summary>
        public static bool IsBCRiversEnabled
        {
            get
            {
                EnsureBCInitialized();
                if (!_bcLoaded || _bcSettingsField == null || _bcRiversEnabledField == null) return true; // default to enabled

                try
                {
                    var settings = _bcSettingsField.GetValue(null);
                    if (settings == null) return true;
                    return (bool)_bcRiversEnabledField.GetValue(settings);
                }
                catch { return true; }
            }
        }

        /// <summary>
        /// Returns true if BC has a heightmap loaded for the current world (regardless of override mode).
        /// When true, BCHeightmapWaterDetector can read the heightmap for depression analysis.
        /// </summary>
        public static bool HasBCHeightmap
        {
            get
            {
                EnsureBCInitialized();
                if (!_bcLoaded || _bcSettingsField == null || _bcHasHeightMapProp == null) return false;

                try
                {
                    var settings = _bcSettingsField.GetValue(null);
                    if (settings == null) return false;
                    return (bool)_bcHasHeightMapProp.GetValue(settings, null);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Returns a diagnostic string describing the BC state for logging.
        /// </summary>
        public static string GetBCStatusString()
        {
            EnsureBCInitialized();
            if (!_bcLoaded) return "Better Continents: not loaded";
            if (!IsBetterContinentsEnabled) return "Better Continents: loaded but disabled for this world";

            return $"Better Continents: enabled, rivers={IsBCRiversEnabled}, heightmapOverrideAll={IsBCHeightmapOverrideAll}, hasHeightmap={HasBCHeightmap}";
        }

        #endregion
    }
}
