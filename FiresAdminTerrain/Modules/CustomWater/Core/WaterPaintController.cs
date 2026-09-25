using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Controller for custom water pieces - acts as a data container like DirtFloorController.
    /// 
    /// When placed, this creates a WaterDisk and registers it with the WaterMeshCombiner system.
    /// The combiner handles all mesh generation, wave animation, and swim triggers.
    /// 
    /// This is the "anchor" that gets saved via ZDO - the WaterDisk is pure data.
    /// 
    /// KEY DIFFERENCES FROM OLD SYSTEM:
    /// - Water surface is painted at placement Y level (like terrain)
    /// - Terrain acts as container (water fills down to terrain)
    /// - Adjacent water paints blend together (like terrain disks)
    /// - No complex flow simulation - just paint the surface you want!
    /// </summary>
    public class WaterPaintController : MonoBehaviour, Hoverable
    {
        #region ZDO Keys

        private static readonly int ZDO_WaterRadius = "water_radius".GetStableHashCode();
        private static readonly int ZDO_WaterDepth = "water_depth".GetStableHashCode();
        private static readonly int ZDO_LiquidType = "water_liquidtype".GetStableHashCode();
        private static readonly int ZDO_WaveIntensity = "water_waveintensity".GetStableHashCode();
        private static readonly int ZDO_SurfaceY = "water_surfacey".GetStableHashCode();

        #endregion

        #region Configuration

        /// <summary>
        /// Radius of the water paint disk.
        /// </summary>
        public float WaterRadius = 5f;

        /// <summary>
        /// Maximum depth below the surface level.
        /// Water fills from surface down to terrain, up to this depth.
        /// </summary>
        public float MaxDepth = 5f;

        /// <summary>
        /// Type of liquid (Water or Tar).
        /// </summary>
        public LiquidType LiquidType = LiquidType.Water;

        /// <summary>
        /// Wave intensity multiplier.
        /// </summary>
        public float WaveIntensity = 0.5f;

        /// <summary>
        /// The Y level of the water surface when placed.
        /// This is the "painted" surface level.
        /// </summary>
        public float SurfaceY;

        #endregion

        #region Runtime State

        private ZNetView _nview;
        private WaterDisk _waterDisk;
        private WaterMeshCombiner _combiner;
        private bool _initialized = false;
        private bool _zdoLoaded = false;

        // Visual components (hidden when in combiner)
        private GameObject _visualPlaceholder;
        private MeshRenderer _placeholderRenderer;

        #endregion

        #region Static Registry

        private static List<WaterPaintController> _allWaterPaints = new List<WaterPaintController>();

        /// <summary>
        /// Gets all water paint instances.
        /// </summary>
        public static IReadOnlyList<WaterPaintController> AllWaterPaints => _allWaterPaints;

        /// <summary>
        /// Gets a water paint controller at a position.
        /// </summary>
        public static WaterPaintController GetWaterPaintAt(Vector3 position, float checkRadius = 0.5f)
        {
            foreach (var paint in _allWaterPaints)
            {
                if (paint == null) continue;

                float dist = Vector3.Distance(
                    new Vector3(position.x, 0, position.z),
                    new Vector3(paint.transform.position.x, 0, paint.transform.position.z)
                );

                if (dist <= paint.WaterRadius + checkRadius)
                {
                    return paint;
                }
            }
            return null;
        }

        #endregion

        #region Lifecycle

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            SurfaceY = transform.position.y; // Default to placement Y
        }

        private void Start()
        {
            if (_nview == null || _nview.GetZDO() == null)
            {
                return;
            }

            Initialize();
        }

        private void OnEnable()
        {
            if (!_allWaterPaints.Contains(this))
            {
                _allWaterPaints.Add(this);
            }
        }

        private void OnDisable()
        {
            _allWaterPaints.Remove(this);
        }

        private void OnDestroy()
        {
            _allWaterPaints.Remove(this);

            // Remove from combiner
            if (_combiner != null && _waterDisk != null)
            {
                _combiner.RemoveWaterDisk(_waterDisk);
            }

            // Clean up visual placeholder
            if (_visualPlaceholder != null)
            {
                Destroy(_visualPlaceholder);
            }
        }

        #endregion

        #region Initialization

        private void Initialize()
        {
            if (_initialized) return;

            // Load state from ZDO
            if (!_zdoLoaded && _nview != null && _nview.GetZDO() != null)
            {
                LoadStateFromZDO();
                _zdoLoaded = true;
            }

            // Create water disk data
            CreateWaterDisk();

            // Register with combiner system
            RegisterWithCombiner();

            // Hide visual placeholder (combiner handles rendering)
            HideVisualPlaceholder();

            _initialized = true;

            FiresLogger.LogInfo($"[WaterPaintController] Initialized at {transform.position}, radius={WaterRadius}, surface={SurfaceY}");
        }

        /// <summary>
        /// Called when the piece is first placed.
        /// </summary>
        public void OnPlaced()
        {
            // Set surface Y to placement height
            SurfaceY = transform.position.y;

            // Save state
            SaveStateToZDO();

            // Initialize immediately
            if (!_initialized)
            {
                Initialize();
            }
            else
            {
                // Update existing disk
                if (_waterDisk != null)
                {
                    _waterDisk.Position = new Vector3(transform.position.x, SurfaceY, transform.position.z);
                    _waterDisk.Radius = WaterRadius;
                    _waterDisk.MaxDepth = MaxDepth;
                    _waterDisk.LiquidType = LiquidType;
                }

                // Regenerate combiner
                _combiner?.ForceRegenerate();
            }

            FiresLogger.LogInfo($"[WaterPaintController] Placed at {transform.position}, surface Y={SurfaceY}");
        }

        /// <summary>
        /// Creates the WaterDisk data object.
        /// </summary>
        private void CreateWaterDisk()
        {
            _waterDisk = new WaterDisk(
                new Vector3(transform.position.x, SurfaceY, transform.position.z),
                WaterRadius,
                MaxDepth,
                LiquidType
            );

            // Link ZDO ID if available
            if (_nview != null && _nview.GetZDO() != null)
            {
                _waterDisk.ZdoId = _nview.GetZDO().m_uid;
            }
        }

        /// <summary>
        /// Registers this water paint with the combiner system.
        /// </summary>
        private void RegisterWithCombiner()
        {
            if (_waterDisk == null) return;

            // Get or create a combiner for this disk
            _combiner = WaterMeshCombiner.GetOrCreateCombinerFor(_waterDisk);

            if (_combiner != null)
            {
                _combiner.LiquidType = LiquidType;
                _combiner.WaveIntensity = WaveIntensity;
            }
        }

        /// <summary>
        /// Hides the visual placeholder since the combiner renders the water.
        /// </summary>
        private void HideVisualPlaceholder()
        {
            // Hide any renderers on this object
            var renderers = GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                r.enabled = false;
            }

            // Disable colliders (combiner provides swim trigger)
            var colliders = GetComponentsInChildren<Collider>();
            foreach (var c in colliders)
            {
                // Keep the interaction trigger if it's a trigger collider
                if (!c.isTrigger)
                {
                    c.enabled = false;
                }
            }
        }

        #endregion

        #region ZDO Persistence

        private void SaveStateToZDO()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;

            _nview.GetZDO().Set(ZDO_WaterRadius, WaterRadius);
            _nview.GetZDO().Set(ZDO_WaterDepth, MaxDepth);
            _nview.GetZDO().Set(ZDO_LiquidType, (int)LiquidType);
            _nview.GetZDO().Set(ZDO_WaveIntensity, WaveIntensity);
            _nview.GetZDO().Set(ZDO_SurfaceY, SurfaceY);
        }

        private void LoadStateFromZDO()
        {
            if (_nview == null || _nview.GetZDO() == null) return;

            WaterRadius = _nview.GetZDO().GetFloat(ZDO_WaterRadius, 5f);
            MaxDepth = _nview.GetZDO().GetFloat(ZDO_WaterDepth, 5f);
            LiquidType = (LiquidType)_nview.GetZDO().GetInt(ZDO_LiquidType, (int)LiquidType.Water);
            WaveIntensity = _nview.GetZDO().GetFloat(ZDO_WaveIntensity, 0.5f);
            SurfaceY = _nview.GetZDO().GetFloat(ZDO_SurfaceY, transform.position.y);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Sets the water radius and updates the combiner.
        /// </summary>
        public void SetRadius(float newRadius)
        {
            WaterRadius = newRadius;

            if (_waterDisk != null)
            {
                _waterDisk.Radius = newRadius;
            }

            SaveStateToZDO();
            _combiner?.ForceRegenerate();
        }

        /// <summary>
        /// Sets the max depth and updates the combiner.
        /// </summary>
        public void SetMaxDepth(float newDepth)
        {
            MaxDepth = newDepth;

            if (_waterDisk != null)
            {
                _waterDisk.MaxDepth = newDepth;
            }

            SaveStateToZDO();
            _combiner?.ForceRegenerate();
        }

        /// <summary>
        /// Sets the surface Y level (water surface height).
        /// </summary>
        public void SetSurfaceY(float newSurfaceY)
        {
            SurfaceY = newSurfaceY;

            if (_waterDisk != null)
            {
                _waterDisk.Position = new Vector3(_waterDisk.Position.x, newSurfaceY, _waterDisk.Position.z);
            }

            SaveStateToZDO();
            _combiner?.ForceRegenerate();
        }

        /// <summary>
        /// Gets the water surface level at this paint location.
        /// </summary>
        public float GetWaterSurfaceAt(Vector3 worldPos)
        {
            if (_combiner != null)
            {
                return _combiner.GetWaterSurfaceAt(worldPos);
            }

            // Fallback to simple surface Y
            float dx = worldPos.x - transform.position.x;
            float dz = worldPos.z - transform.position.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);

            if (dist <= WaterRadius)
            {
                return SurfaceY;
            }

            return float.MinValue;
        }

        /// <summary>
        /// Checks if a position is inside this water body.
        /// </summary>
        public bool IsPositionInWater(Vector3 worldPos)
        {
            float surface = GetWaterSurfaceAt(worldPos);
            return surface > float.MinValue && worldPos.y < surface;
        }

        /// <summary>
        /// Gets the WaterDisk data object.
        /// </summary>
        public WaterDisk GetWaterDisk()
        {
            return _waterDisk;
        }

        /// <summary>
        /// Gets the combiner this paint is registered with.
        /// </summary>
        public WaterMeshCombiner GetCombiner()
        {
            return _combiner;
        }

        #endregion

        #region Hoverable Interface

        public string GetHoverText()
        {
            if (!IsLocalPlayerAdmin()) return "";

            string text = $"<color=cyan>Custom Water</color>\n";
            text += $"Surface: <color=white>{SurfaceY:F1}m</color>\n";
            text += $"Radius: <color=white>{WaterRadius:F1}m</color>\n";
            text += $"Depth: <color=white>{MaxDepth:F1}m</color>\n";
            text += $"Type: <color=white>{LiquidType}</color>";

            return text;
        }

        public float GetHoverOffset() => 0f;

        public string GetHoverName()
        {
            return "Custom Water";
        }

        private static bool IsLocalPlayerAdmin()
        {
            if (ZNet.instance?.IsServer() == true) return true;
            if (FiresAdminTerrain.Instance?.configSync?.IsAdmin == true) return true;
            return false;
        }

        #endregion
    }
}
