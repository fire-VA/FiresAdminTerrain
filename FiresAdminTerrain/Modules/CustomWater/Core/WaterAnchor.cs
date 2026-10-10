using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// ZDO-backed anchor for custom water persistence.
    /// 
    /// ARCHITECTURE:
    /// - ONE invisible anchor per custom water instance
    /// - Anchor stores all water data in ZDO (type, size, flow settings, etc.)
    /// - On world load, anchor recreates the water instance
    /// - Uses existing prefab for ZDO (like TerrainCombinerAnchor)
    /// 
    /// DATA FLOW:
    /// 1. Water placed -> Create anchor ZDO at same position
    /// 2. World saves -> Anchor ZDO persists
    /// 3. World loads -> Anchor spawns -> Recreates water from ZDO data
    /// </summary>
    public class WaterAnchor : MonoBehaviour
    {
        #region ZDO Data Keys

        private static readonly int s_waterTypeHash = "va_water_type".GetStableHashCode();
        private static readonly int s_boundsSizeHash = "va_water_bounds".GetStableHashCode();
        private static readonly int s_liquidTypeHash = "va_water_liquid".GetStableHashCode();
        private static readonly int s_waveIntensityHash = "va_water_waves".GetStableHashCode();
        private static readonly int s_waterLevelHash = "va_water_level".GetStableHashCode();  // For ponds
        private static readonly int s_flowDirectionHash = "va_water_flowdir".GetStableHashCode();  // For rivers
        private static readonly int s_flowSpeedHash = "va_water_flowspd".GetStableHashCode();  // For rivers
        private static readonly int s_dropHeightHash = "va_water_drop".GetStableHashCode();  // For waterfalls
        private static readonly int s_flowRateHash = "va_water_flowrate".GetStableHashCode();  // For waterfalls
        private static readonly int s_widthHash = "va_water_width".GetStableHashCode();  // For waterfalls
        private static readonly int s_isAnchorHash = "va_is_water_anchor".GetStableHashCode();
        private static readonly int s_versionHash = "va_water_version".GetStableHashCode();

        private const int CURRENT_VERSION = 1;

        #endregion

        #region Fields

        private ZNetView m_nview;
        private CustomWaterBase m_linkedWater;
        private bool m_dataLoaded = false;
        private bool m_isStripped = false;
        private bool m_isNewlyCreated = false;

        #endregion

        #region Properties

        /// <summary>
        /// The water instance this anchor manages.
        /// </summary>
        public CustomWaterBase LinkedWater => m_linkedWater;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            m_nview = GetComponent<ZNetView>();

            if (m_nview == null)
            {
                FiresLogger.LogError("[WaterAnchor] No ZNetView found!");
                Destroy(this);
                return;
            }

            // Wait for ZDO to be valid
            if (m_nview.GetZDO() == null)
            {
                return;
            }

            // Check if this is an anchor or something else
            bool isAnchor = m_nview.GetZDO().GetBool(s_isAnchorHash, false);
            if (!isAnchor && !m_isNewlyCreated)
            {
                // Not an anchor - remove component
                Destroy(this);
                return;
            }

            StripToInvisible();
        }

        private void Start()
        {
            if (m_nview == null || m_nview.GetZDO() == null)
            {
                return;
            }

            // Ensure we're stripped
            if (!m_isStripped)
            {
                StripToInvisible();
            }

            // If newly created with water already linked, don't restore
            if (m_isNewlyCreated || m_linkedWater != null)
            {
                m_dataLoaded = true;
                return;
            }

            // Already loaded
            if (m_dataLoaded)
            {
                return;
            }

            // Check if we have saved data to restore
            int version = m_nview.GetZDO().GetInt(s_versionHash, 0);
            if (version > 0 && !m_dataLoaded)
            {
                RestoreFromZDO();
            }
        }

        private void OnDestroy()
        {
            // Unlink from water if we're still the anchor
            if (m_linkedWater != null)
            {
                // Water manages its own lifecycle
                m_linkedWater = null;
            }
        }

        #endregion

        #region Stripping

        /// <summary>
        /// Makes this GameObject invisible - no mesh, no colliders.
        /// Just a ZNetView holding data.
        /// </summary>
        private void StripToInvisible()
        {
            if (m_isStripped) return;
            m_isStripped = true;

            // Disable renderers
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null)
                    renderer.enabled = false;
            }

            // Disable colliders
            foreach (var collider in GetComponentsInChildren<Collider>(true))
            {
                if (collider != null)
                    collider.enabled = false;
            }

            // Disable children
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (child != null)
                    child.gameObject.SetActive(false);
            }

            // Put on Ignore Raycast layer
            gameObject.layer = 2;
        }

        #endregion

        #region Linking

        /// <summary>
        /// Marks this anchor as newly created (skip restoration in Start).
        /// Must be called immediately after AddComponent.
        /// </summary>
        public void MarkAsNewAnchor()
        {
            m_isNewlyCreated = true;
            m_dataLoaded = true;
        }

        /// <summary>
        /// Links this anchor to a water instance.
        /// </summary>
        public void LinkToWater(CustomWaterBase water)
        {
            m_linkedWater = water;

            // Mark ZDO as anchor
            if (m_nview != null && m_nview.IsValid() && m_nview.IsOwner())
            {
                var zdo = m_nview.GetZDO();
                if (zdo != null)
                {
                    zdo.Set(s_isAnchorHash, true);
                    zdo.Set(s_versionHash, CURRENT_VERSION);
                }
            }

            FiresLogger.LogInfo($"[WaterAnchor] Linked to {water.GetWaterType()} at {water.transform.position}");
        }

        #endregion

        #region Save/Load

        /// <summary>
        /// Saves all water data to the ZDO.
        /// Call this after water modifications.
        /// </summary>
        public void SaveToZDO()
        {
            if (m_nview == null || !m_nview.IsValid() || !m_nview.IsOwner())
            {
                return;
            }

            if (m_linkedWater == null)
            {
                FiresLogger.LogWarning("[WaterAnchor] Cannot save - no water linked");
                return;
            }

            var zdo = m_nview.GetZDO();
            if (zdo == null) return;

            try
            {
                // Ensure anchor flags are set
                zdo.Set(s_isAnchorHash, true);
                zdo.Set(s_versionHash, CURRENT_VERSION);

                // Save common properties
                zdo.Set(s_waterTypeHash, (int)m_linkedWater.GetWaterType());
                zdo.Set(s_boundsSizeHash, m_linkedWater.BoundsSize);
                zdo.Set(s_liquidTypeHash, (int)m_linkedWater.LiquidType);
                zdo.Set(s_waveIntensityHash, m_linkedWater.WaveIntensity);

                // Save type-specific properties
                switch (m_linkedWater)
                {
                    case CustomPondController pond:
                        zdo.Set(s_waterLevelHash, pond.WaterLevel);
                        break;

                    case CustomRiverController river:
                        zdo.Set(s_flowDirectionHash, river.FlowDirection);
                        zdo.Set(s_flowSpeedHash, river.FlowSpeed);
                        break;

                    case CustomWaterfallController waterfall:
                        zdo.Set(s_dropHeightHash, waterfall.DropHeight);
                        zdo.Set(s_flowRateHash, waterfall.FlowRate);
                        zdo.Set(s_widthHash, waterfall.Width);
                        break;
                }

                FiresLogger.LogInfo($"[WaterAnchor] Saved {m_linkedWater.GetWaterType()} to ZDO at {transform.position}");
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[WaterAnchor] Failed to save to ZDO: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores the water instance from ZDO data.
        /// </summary>
        private void RestoreFromZDO()
        {
            if (m_dataLoaded) return;

            var zdo = m_nview?.GetZDO();
            if (zdo == null) return;

            m_dataLoaded = true;

            try
            {
                // Load water type
                CustomWaterType waterType = (CustomWaterType)zdo.GetInt(s_waterTypeHash, 0);

                // Load common properties
                Vector3 boundsSize = zdo.GetVec3(s_boundsSizeHash, new Vector3(10f, 3f, 10f));
                LiquidType liquidType = (LiquidType)zdo.GetInt(s_liquidTypeHash, 0);
                float waveIntensity = zdo.GetFloat(s_waveIntensityHash, 0.5f);

                // Create water based on type
                CustomWaterBase water = null;

                switch (waterType)
                {
                    case CustomWaterType.Pond:
                        float waterLevel = zdo.GetFloat(s_waterLevelHash, 0.5f);
                        var pond = CustomWaterManager.SpawnPond(transform.position, boundsSize, waterLevel);
                        pond.LiquidType = liquidType;
                        pond.WaveIntensity = waveIntensity;
                        water = pond;
                        break;

                    case CustomWaterType.River:
                        float flowDirection = zdo.GetFloat(s_flowDirectionHash, 0f);
                        float flowSpeed = zdo.GetFloat(s_flowSpeedHash, 2f);
                        var river = CustomWaterManager.SpawnRiver(transform.position, boundsSize, flowDirection, flowSpeed);
                        river.LiquidType = liquidType;
                        river.WaveIntensity = waveIntensity;
                        water = river;
                        break;

                    case CustomWaterType.Waterfall:
                        float dropHeight = zdo.GetFloat(s_dropHeightHash, 5f);
                        float flowRate = zdo.GetFloat(s_flowRateHash, 1f);
                        var waterfall = CustomWaterManager.SpawnWaterfall(transform.position, dropHeight, flowRate);
                        waterfall.LiquidType = liquidType;
                        waterfall.WaveIntensity = waveIntensity;
                        waterfall.Width = zdo.GetFloat(s_widthHash, 2f);
                        water = waterfall;
                        break;
                }

                if (water != null)
                {
                    m_linkedWater = water;
                    FiresLogger.LogInfo($"[WaterAnchor] Restored {waterType} from ZDO at {transform.position}");
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[WaterAnchor] Failed to restore from ZDO: {ex.Message}");
            }
        }

        #endregion

        #region Permanent Deletion

        /// <summary>
        /// Permanently destroys this anchor and its ZDO.
        /// Called when the water is deleted.
        /// 
        /// Uses ZNetScene.Destroy() for proper ZDO cleanup that works with other mods.
        /// Falls back to manual cleanup if ZNetScene is unavailable.
        /// </summary>
        public void DestroyPermanently()
        {
            FiresLogger.LogInfo($"[WaterAnchor] Permanently destroying anchor at {transform.position}");

            m_linkedWater = null;

            // Clear ZDO data to prevent it from being restored
            if (m_nview != null && m_nview.IsValid())
            {
                var zdo = m_nview.GetZDO();
                if (zdo != null)
                {
                    zdo.Set(s_versionHash, 0);
                    zdo.Set(s_isAnchorHash, false);
                }
            }

            // Use ZNetScene.Destroy() for proper networked destruction
            // This handles ZDO destruction in the correct order and notifies other mods
            if (ZNetScene.instance != null && m_nview != null && m_nview.IsValid())
            {
                try
                {
                    ZNetScene.instance.Destroy(gameObject);
                    return; // ZNetScene.Destroy handles everything
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[WaterAnchor] ZNetScene.Destroy failed: {ex.Message}");
                    // Fall through to manual cleanup
                }
            }

            // Fallback: Manual destruction if ZNetScene isn't available
            Destroy(gameObject);
        }

        #endregion

        #region Static Factory

        /// <summary>
        /// Creates a new invisible anchor at a position.
        /// </summary>
        public static WaterAnchor CreateAnchor(Vector3 position, CustomWaterBase water)
        {
            if (ZNetScene.instance == null)
            {
                FiresLogger.LogError("[WaterAnchor] Cannot create anchor - ZNetScene not available");
                return null;
            }

            // Find the CustomRiver prefab for anchor ZDO
            GameObject prefab = FindAnchorPrefab();
            if (prefab == null)
            {
                FiresLogger.LogError("[WaterAnchor] Cannot find prefab for anchor");
                return null;
            }

            // Spawn anchor underground (at Y=0) to hide it
            Vector3 anchorPos = new Vector3(position.x, 0f, position.z);

            var go = UnityEngine.Object.Instantiate(prefab, anchorPos, Quaternion.identity);
            go.name = $"WaterAnchor_{DateTime.UtcNow.Ticks}";

            // Immediately hide it
            go.layer = 2;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null) renderer.enabled = false;
            }

            // Add anchor component
            var anchor = go.AddComponent<WaterAnchor>();
            anchor.MarkAsNewAnchor();
            anchor.LinkToWater(water);

            // Mark ZDO immediately
            var nview = go.GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null)
            {
                var zdo = nview.GetZDO();
                zdo.Set(s_isAnchorHash, true);
                zdo.Set(s_versionHash, CURRENT_VERSION);
            }

            // Force strip
            anchor.StripToInvisible();

            // Save water data
            anchor.SaveToZDO();

            FiresLogger.LogInfo($"[WaterAnchor] Created anchor for {water.GetWaterType()} at {position}");

            return anchor;
        }

        /// <summary>
        /// Finds a prefab to use for anchor ZDO.
        /// Uses CustomRiver prefab (piece layer) to avoid terrain raycast interference.
        /// Does NOT fall back to dirtfloor - using dirtfloor caused water anchors to
        /// spawn as terrain pieces and interfere with the terrain system.
        /// </summary>
        private static GameObject FindAnchorPrefab()
        {
            // Water anchors MUST use CustomRiver prefab (piece layer)
            string[] names = {
                "CustomRiver",
                "customriver",
            };

            foreach (var name in names)
            {
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab != null)
                    return prefab;
            }

            FiresLogger.LogError("[WaterAnchor] FindAnchorPrefab: CustomRiver prefab NOT found in ZNetScene! " +
                "Water persistence will FAIL! Ensure CustomRiver is registered.");
            return null;
        }

        #endregion
    }
}
