using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// The type of custom water body.
    /// </summary>
    public enum CustomWaterType
    {
        Pond,
        River,
        Waterfall
    }

    /// <summary>
    /// Abstract base class for all custom water types (Pond, River, Waterfall).
    /// Based on Valheim's WaterVolume and LiquidVolume implementation.
    /// </summary>
    public abstract class CustomWaterBase : MonoBehaviour, Hoverable
    {
        #region Configuration

        [Header("Bounds")]
        [Tooltip("Size of the water volume box (Width, Height/Depth, Length)")]
        public Vector3 BoundsSize = new Vector3(10f, 3f, 10f);

        [Header("Liquid Properties")]
        public LiquidType LiquidType = LiquidType.Water;

        [Range(0f, 2f)]
        public float WaveIntensity = 0.5f;

        [Tooltip("Surface offset from calculated water level")]
        public float SurfaceOffset = 0f;

        #endregion

        #region Components

        protected ZNetView m_nview;
        protected MeshFilter m_meshFilter;
        protected MeshRenderer m_meshRenderer;
        protected BoxCollider m_swimCollider;
        protected Mesh m_waterMesh;
        protected Material m_waterMaterial;
        protected GameObject m_swimTriggerGO;

        #endregion

        #region Runtime State

        protected bool m_initialized = false;
        protected bool m_meshDirty = true;
        protected readonly List<IWaterInteractable> m_inWater = new List<IWaterInteractable>();
        private static readonly List<int> s_removeIndices = new List<int>();

        // Wave calculation (from WaterVolume)
        protected static float s_waterTime = 0f;

        // Base wave values (not affected by scale)
        protected const float BASE_WAVE_SCALE = 1f;

        #endregion

        #region Abstract Methods

        public abstract CustomWaterType GetWaterType();
        public abstract float GetWaterSurfaceAt(Vector3 worldPos);
        public abstract void UpdateWater(float dt);
        protected abstract void GenerateMesh();

        #endregion

        #region Unity Lifecycle

        protected virtual void Awake()
        {
            m_nview = GetComponent<ZNetView>();

            // Check if ghost mode (no valid ZDO)
            bool isGhost = m_nview == null || m_nview.GetZDO() == null;

            FiresLogger.LogInfo($"[CustomWater] Awake: {gameObject.name}, isGhost={isGhost}, LiquidType={LiquidType}");

            // Setup mesh components
            m_meshFilter = GetComponent<MeshFilter>();
            if (m_meshFilter == null)
                m_meshFilter = gameObject.AddComponent<MeshFilter>();

            m_meshRenderer = GetComponent<MeshRenderer>();
            if (m_meshRenderer == null)
                m_meshRenderer = gameObject.AddComponent<MeshRenderer>();

            // Create water mesh
            m_waterMesh = new Mesh();
            m_waterMesh.name = $"CustomWater_{GetWaterType()}_Mesh";
            m_meshFilter.sharedMesh = m_waterMesh;

            // Get/create material
            SetupMaterial();

            // Setup swim trigger (only if not ghost)
            if (!isGhost)
            {
                SetupSwimTrigger();
            }
        }

        protected virtual void Start()
        {
            // Generate initial mesh
            GenerateMesh();
            m_initialized = true;

            // Register with manager
            if (m_nview != null && m_nview.GetZDO() != null)
            {
                CustomWaterManager.Register(this);
                FiresLogger.LogInfo($"[CustomWater] Registered {GetWaterType()} at {transform.position}, Bounds={BoundsSize}");
            }
        }

        protected virtual void OnDestroy()
        {
            CustomWaterManager.Unregister(this);

            // CRITICAL: Clean up the swim trigger FIRST to properly clear all tracked interactables
            // This must happen BEFORE we clear m_inWater as the trigger has its own tracking
            if (m_swimTriggerGO != null)
            {
                var trigger = m_swimTriggerGO.GetComponent<CustomWaterTrigger>();
                if (trigger != null)
                {
                    trigger.ForceCleanup();
                }
                Destroy(m_swimTriggerGO);
                m_swimTriggerGO = null;
            }

            // Clear interactables - use Water type explicitly
            foreach (var interactable in m_inWater)
            {
                if (interactable != null)
                {
                    try
                    {
                        if (interactable.Decrement(LiquidType) == 0)
                            interactable.SetLiquidLevel(-10000f, LiquidType, this);
                    }
                    catch (Exception e)
                    {
                        // Ignore errors from already-destroyed objects
                    }
                }
            }
            m_inWater.Clear();

            if (m_waterMesh != null)
                Destroy(m_waterMesh);

            FiresLogger.LogInfo($"[CustomWater] OnDestroy: Cleaned up water at {transform.position}");
        }

        protected virtual void Update()
        {
            if (!m_initialized) return;

            float dt = Time.deltaTime;

            // Update water time (synchronized like WaterVolume)
            s_waterTime += dt;

            // Update water simulation
            UpdateWater(dt);

            // Update floaters in water
            UpdateFloaters();

            // Regenerate mesh if dirty
            if (m_meshDirty)
            {
                GenerateMesh();
                m_meshDirty = false;
            }

            // Update material time
            if (m_meshRenderer != null && m_meshRenderer.material != null)
            {
                m_meshRenderer.material.SetFloat("_WaterTime", s_waterTime);
            }
        }

        protected virtual void FixedUpdate()
        {
            // Nothing by default - subclasses can override for physics
        }

        #endregion

        #region Setup Methods

        protected virtual void SetupMaterial()
        {
            // IMPORTANT: Force LiquidType to Water - the Tar effects are caused by wrong liquid type
            if (LiquidType != LiquidType.Water && LiquidType != LiquidType.Tar)
            {
                FiresLogger.LogWarning($"[CustomWater] Invalid LiquidType {LiquidType}, forcing to Water");
                LiquidType = LiquidType.Water;
            }

            m_waterMaterial = WaterMaterialManager.GetWaterMaterial(LiquidType);

            FiresLogger.LogInfo($"[CustomWater] SetupMaterial: LiquidType={LiquidType}, Material={m_waterMaterial?.name ?? "NULL"}");

            if (m_waterMaterial != null)
            {
                // Create instance to avoid modifying shared material
                m_meshRenderer.material = new Material(m_waterMaterial);
                m_meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_meshRenderer.receiveShadows = true;

                // Set depth values for shader (uniform depth for custom water)
                float normalizedDepth = Mathf.Clamp01(BoundsSize.y / 10f);
                m_meshRenderer.material.SetFloatArray("_depth", new float[] {
                    normalizedDepth, normalizedDepth, normalizedDepth, normalizedDepth
                });
                m_meshRenderer.material.SetFloat("_UseGlobalWind", 1f);
            }
            else
            {
                // Create visible fallback material
                CreateFallbackMaterial();
            }
        }

        protected void CreateFallbackMaterial()
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Standard");

            if (shader != null)
            {
                m_meshRenderer.material = new Material(shader);

                // Use explicit water blue color - never tar color for ponds/rivers
                Color waterColor = LiquidType == LiquidType.Tar
                    ? new Color(0.1f, 0.08f, 0.05f, 0.9f)
                    : new Color(0.2f, 0.5f, 0.8f, 0.6f);

                m_meshRenderer.material.color = waterColor;
                m_meshRenderer.material.renderQueue = 3000;
                m_meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                FiresLogger.LogInfo($"[CustomWater] Using fallback material for {gameObject.name}, color={waterColor}");
            }
        }

        protected virtual void SetupSwimTrigger()
        {
            // Create trigger collider for swim detection
            // The trigger is a thin slab (2m thick) at the water surface level.
            // This ensures the player only triggers swimming when they're actually
            // at the water surface, not when walking on terrain below the water volume.
            m_swimTriggerGO = new GameObject("SwimTrigger");
            m_swimTriggerGO.transform.SetParent(transform);
            m_swimTriggerGO.transform.localPosition = Vector3.zero;
            m_swimTriggerGO.transform.localRotation = Quaternion.identity;
            m_swimTriggerGO.transform.localScale = Vector3.one;

            // CRITICAL: Use the correct layer for character interaction
            int layer = LayerMask.NameToLayer("character_trigger");
            if (layer == -1)
            {
                // Fallback to Default if character_trigger doesn't exist
                layer = 0;
                FiresLogger.LogWarning("[CustomWater] Layer 'character_trigger' not found, using Default");
            }
            m_swimTriggerGO.layer = layer;

            m_swimCollider = m_swimTriggerGO.AddComponent<BoxCollider>();
            m_swimCollider.isTrigger = true;

            // Use a thin slab (2m thick) at the water surface for swim detection.
            // The slab covers the full XZ footprint of the water but is only 2m tall,
            // centered at the top of the water volume. This prevents triggering swim
            // when a player walks on terrain below the water body (e.g., under a cave
            // with water on top). The Harmony patches on IsSwimming/InLiquid use
            // GetWaterSurfaceAt for the actual surface level, so the trigger just needs
            // to detect when the player enters the water's XZ area at the right height.
            const float SWIM_TRIGGER_THICKNESS = 2f;
            float surfaceLocalY = BoundsSize.y * 0.5f; // Top of the water volume
            float triggerCenterY = surfaceLocalY - SWIM_TRIGGER_THICKNESS * 0.5f;
            m_swimCollider.size = new Vector3(BoundsSize.x, SWIM_TRIGGER_THICKNESS, BoundsSize.z);
            m_swimCollider.center = new Vector3(0f, triggerCenterY, 0f);

            // Add trigger handler
            var triggerHandler = m_swimTriggerGO.AddComponent<CustomWaterTrigger>();
            triggerHandler.Initialize(this);

            FiresLogger.LogInfo($"[CustomWater] SetupSwimTrigger: xz=({BoundsSize.x},{BoundsSize.z}), thickness={SWIM_TRIGGER_THICKNESS}, centerY={triggerCenterY:F2}, layer={m_swimTriggerGO.layer}");
        }

        #endregion

        #region Floater Updates (from WaterVolume.UpdateFloaters)

        protected void UpdateFloaters()
        {
            if (m_inWater.Count == 0) return;

            s_removeIndices.Clear();

            // CRITICAL: Default to Water for proper wet effects
            LiquidType typeToUse = LiquidType.Water;
            if (LiquidType == LiquidType.Tar)
            {
                typeToUse = LiquidType.Tar;
            }

            // Cached layer mask for terrain blocking raycast
            int blockingMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");

            for (int i = 0; i < m_inWater.Count; i++)
            {
                var interactable = m_inWater[i];
                if (interactable == null)
                {
                    s_removeIndices.Add(i);
                    continue;
                }

                Transform t = interactable.GetTransform();
                if (t == null)
                {
                    s_removeIndices.Add(i);
                    continue;
                }

                float waterSurface = GetWaterSurfaceAt(t.position);

                // CAVE FIX: If water is far above the interactable, check for blocking terrain
                float distToWater = waterSurface - t.position.y;
                if (distToWater > 5f)
                {
                    Vector3 rayStart = t.position + Vector3.up * 1.8f;
                    float rayDist = waterSurface - rayStart.y;
                    if (rayDist > 0f && Physics.Raycast(rayStart, Vector3.up, rayDist, blockingMask))
                    {
                        // Blocked by terrain - remove from water tracking
                        if (interactable.Decrement(typeToUse) == 0)
                        {
                            interactable.SetLiquidLevel(-10000f, typeToUse, this);
                        }
                        s_removeIndices.Add(i);
                        continue;
                    }
                }

                // Use Water type for proper wet effects
                interactable.SetLiquidLevel(waterSurface, typeToUse, this);
            }

            // Remove invalid entries
            for (int i = s_removeIndices.Count - 1; i >= 0; i--)
            {
                m_inWater.RemoveAt(s_removeIndices[i]);
            }
        }

        /// <summary>
        /// Called by CustomWaterTrigger when something enters the water.
        /// </summary>
        public void OnWaterTriggerEnter(Collider other)
        {
            if (other == null) return;
            if (other.attachedRigidbody == null) return;

            var interactable = other.attachedRigidbody.GetComponent<IWaterInteractable>();
            if (interactable == null) return;

            // CRITICAL: For custom water ponds/rivers, ALWAYS use LiquidType.Water
            // to ensure proper "wet" effects, not "tarred" effects.
            // The tar effects are triggered by LiquidType.Tar (value 1)
            // We want LiquidType.Water (value 0) for blue water effects
            LiquidType typeToUse = LiquidType.Water;  // Force water for now

            // Only use Tar if this is explicitly a tar pond (rare case)
            if (LiquidType == LiquidType.Tar)
            {
                typeToUse = LiquidType.Tar;
            }

            FiresLogger.LogInfo($"[CustomWater] TriggerEnter: {other.name}, using LiquidType={typeToUse} (int={(int)typeToUse}), configured={LiquidType}");

            // Increment ONLY the water counter (or tar if explicit)
            interactable.Increment(typeToUse);

            if (!m_inWater.Contains(interactable))
            {
                m_inWater.Add(interactable);

                // Immediately set water level with the correct type
                Transform t = interactable.GetTransform();
                if (t != null)
                {
                    float waterSurface = GetWaterSurfaceAt(t.position);
                    interactable.SetLiquidLevel(waterSurface, typeToUse, this);
                }
            }
        }

        /// <summary>
        /// Called by CustomWaterTrigger when something exits the water.
        /// </summary>
        public void OnWaterTriggerExit(Collider other)
        {
            if (other == null) return;
            if (other.attachedRigidbody == null) return;

            var interactable = other.attachedRigidbody.GetComponent<IWaterInteractable>();
            if (interactable == null) return;

            // CRITICAL: Use same type as enter - default to Water
            LiquidType typeToUse = LiquidType.Water;
            if (LiquidType == LiquidType.Tar)
            {
                typeToUse = LiquidType.Tar;
            }

            FiresLogger.LogInfo($"[CustomWater] TriggerExit: {other.name}, using LiquidType={typeToUse} (int={(int)typeToUse})");

            // Decrement the same counter we incremented
            if (interactable.Decrement(typeToUse) == 0)
            {
                interactable.SetLiquidLevel(-10000f, typeToUse, this);
            }

            m_inWater.Remove(interactable);
        }

        #endregion

        #region Wave Calculation (from WaterVolume)

        // Fixed wave parameters - NEVER affected by object scale
        // These define the visual appearance of waves consistently across all water sizes
        private const float WAVE_FREQ_1 = 0.08f;   // Large slow waves
        private const float WAVE_FREQ_2 = 0.15f;   // Medium waves  
        private const float WAVE_FREQ_3 = 0.25f;   // Small ripples
        private const float WAVE_SPEED_1 = 0.4f;
        private const float WAVE_SPEED_2 = 0.6f;
        private const float WAVE_SPEED_3 = 0.9f;
        private const float WAVE_AMP_1 = 0.15f;    // Max wave height in meters
        private const float WAVE_AMP_2 = 0.08f;
        private const float WAVE_AMP_3 = 0.04f;

        /// <summary>
        /// Calculates wave height at a world position using Valheim's wind system.
        /// CRITICAL: Uses ABSOLUTE world position - completely independent of object scale.
        /// Waves will look the same whether the water is 10m or 100m wide.
        /// </summary>
        protected float CalculateWaveHeight(Vector3 worldPos, float depth)
        {
            if (WaveIntensity <= 0.001f) return 0f;
            if (EnvMan.instance == null) return 0f;

            // Get wind data from environment
            EnvMan.instance.GetWindData(out Vector4 wind1, out Vector4 wind2, out float windAlpha);

            // Calculate waves using ABSOLUTE world position (not local position)
            // This ensures waves look the same regardless of object scale
            float waveHeight1 = CalcScaleIndependentWave(worldPos, wind1.w, s_waterTime);
            float waveHeight2 = CalcScaleIndependentWave(worldPos, wind2.w, s_waterTime);

            float wave = Mathf.Lerp(waveHeight1, waveHeight2, windAlpha);

            // Depth factor reduces waves in shallow water (0-1 range)
            // depth parameter should be normalized (0 = surface, 1 = deep)
            float depthFactor = Mathf.Clamp01(depth);

            // Apply user-configurable intensity (capped to prevent extreme waves)
            float intensity = Mathf.Clamp(WaveIntensity, 0f, 1.5f);

            return wave * depthFactor * intensity;
        }

        /// <summary>
        /// Calculates wave height using fixed frequencies that don't scale with object size.
        /// Uses absolute world coordinates for consistent wave appearance.
        /// </summary>
        private float CalcScaleIndependentWave(Vector3 worldPos, float windStrength, float time)
        {
            // Use ABSOLUTE world coordinates - waves tile across the world consistently
            // This means a 10m pond and a 100m pond will have the same wave density

            // Large slow-moving waves
            float wave1 = Mathf.Sin(worldPos.x * WAVE_FREQ_1 + worldPos.z * WAVE_FREQ_1 * 0.7f + time * WAVE_SPEED_1) * WAVE_AMP_1;

            // Medium cross-waves
            float wave2 = Mathf.Sin(worldPos.x * WAVE_FREQ_2 * 0.8f - worldPos.z * WAVE_FREQ_2 + time * WAVE_SPEED_2) * WAVE_AMP_2;

            // Small ripples for detail
            float wave3 = Mathf.Sin(worldPos.x * WAVE_FREQ_3 + worldPos.z * WAVE_FREQ_3 * 1.2f + time * WAVE_SPEED_3) * WAVE_AMP_3;

            // Combine waves, modulated by wind strength (0-1)
            float totalWave = (wave1 + wave2 + wave3) * Mathf.Clamp01(windStrength * 0.5f + 0.5f);

            // Cap maximum wave height to prevent extreme values
            return Mathf.Clamp(totalWave, -0.5f, 0.5f);
        }

        #endregion

        #region Terrain Sampling

        protected float SampleTerrainHeight(Vector3 worldPos)
        {
            // Phase F: legacy DirtFloorZoneRegistry path removed. Sample
            // the new tile-anchor system instead.
            if (VerdantsAscent.Terrain.Persistence.TileAnchorRegistry
                    .TrySampleHeightAtWorld(worldPos, out float tileH))
            {
                return tileH;
            }

            // Fallback to vanilla terrain
            if (ZoneSystem.instance != null)
            {
                ZoneSystem.instance.GetGroundHeight(worldPos, out float groundHeight);
                return groundHeight;
            }

            return worldPos.y - BoundsSize.y;
        }

        #endregion

        #region Utility Methods

        public bool IsInsideBounds(Vector3 worldPos)
        {
            Vector3 localPos = transform.InverseTransformPoint(worldPos);
            Vector3 halfSize = BoundsSize * 0.5f;

            return Mathf.Abs(localPos.x) <= halfSize.x &&
                   Mathf.Abs(localPos.y) <= halfSize.y &&
                   Mathf.Abs(localPos.z) <= halfSize.z;
        }

        public Bounds GetWorldBounds()
        {
            return new Bounds(transform.position, BoundsSize);
        }

        protected float GetVanillaWaterLevel(Vector3 worldPos)
        {
            if (ZoneSystem.instance == null) return -10000f;
            return ZoneSystem.instance.m_waterLevel;
        }

        public virtual void SetBoundsSize(Vector3 newSize)
        {
            BoundsSize = newSize;

            if (m_swimCollider != null)
            {
                // Maintain thin slab at water surface
                const float SWIM_TRIGGER_THICKNESS = 2f;
                float surfaceLocalY = BoundsSize.y * 0.5f;
                float triggerCenterY = surfaceLocalY - SWIM_TRIGGER_THICKNESS * 0.5f;
                m_swimCollider.size = new Vector3(BoundsSize.x, SWIM_TRIGGER_THICKNESS, BoundsSize.z);
                m_swimCollider.center = new Vector3(0f, triggerCenterY, 0f);
            }

            m_meshDirty = true;
        }

        #endregion

        #region Mesh Generation Helpers

        /// <summary>
        /// Creates a simple water surface quad mesh at a specific Y level.
        /// </summary>
        protected void GenerateSimpleSurfaceMesh(float localWaterY)
        {
            float halfWidth = BoundsSize.x * 0.5f;
            float halfLength = BoundsSize.z * 0.5f;

            int subdivisions = 8;
            int vertCount = (subdivisions + 1) * (subdivisions + 1);

            Vector3[] vertices = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            int[] triangles = new int[subdivisions * subdivisions * 6];

            // Generate vertices - mesh is in LOCAL space
            for (int z = 0; z <= subdivisions; z++)
            {
                for (int x = 0; x <= subdivisions; x++)
                {
                    int idx = z * (subdivisions + 1) + x;
                    float xPos = Mathf.Lerp(-halfWidth, halfWidth, (float)x / subdivisions);
                    float zPos = Mathf.Lerp(-halfLength, halfLength, (float)z / subdivisions);

                    vertices[idx] = new Vector3(xPos, localWaterY, zPos);
                    uvs[idx] = new Vector2((float)x / subdivisions, (float)z / subdivisions);
                }
            }

            // Generate triangles (double-sided for visibility from above and below)
            int triIdx = 0;
            for (int z = 0; z < subdivisions; z++)
            {
                for (int x = 0; x < subdivisions; x++)
                {
                    int bottomLeft = z * (subdivisions + 1) + x;
                    int bottomRight = bottomLeft + 1;
                    int topLeft = bottomLeft + (subdivisions + 1);
                    int topRight = topLeft + 1;

                    // Front face
                    triangles[triIdx++] = bottomLeft;
                    triangles[triIdx++] = topLeft;
                    triangles[triIdx++] = bottomRight;

                    triangles[triIdx++] = bottomRight;
                    triangles[triIdx++] = topLeft;
                    triangles[triIdx++] = topRight;
                }
            }

            m_waterMesh.Clear();
            m_waterMesh.vertices = vertices;
            m_waterMesh.uv = uvs;
            m_waterMesh.triangles = triangles;
            m_waterMesh.RecalculateNormals();
            m_waterMesh.RecalculateBounds();

            FiresLogger.LogInfo($"[CustomWater] GenerateSimpleSurfaceMesh: localY={localWaterY:F2}, verts={vertices.Length}");
        }

        #endregion

        #region Hoverable Implementation

        public string GetHoverText()
        {
            if (!IsLocalPlayerAdmin()) return "";

            string typeName = GetWaterType().ToString();
            string liquidName = LiquidType == LiquidType.Water ? "Water" : "Tar";

            return $"<color=cyan><b>Custom {typeName}</b></color>\n" +
                   $"Size: {BoundsSize.x:F1}m x {BoundsSize.y:F1}m x {BoundsSize.z:F1}m\n" +
                   $"Type: <color=yellow>{liquidName}</color>\n" +
                   $"In Water: {m_inWater.Count}";
        }

        public float GetHoverOffset() => 0f;

        public string GetHoverName() => $"Custom {GetWaterType()}";

        private static bool IsLocalPlayerAdmin()
        {
            if (ZNet.instance?.IsServer() == true) return true;
            if (FiresAdminTerrain.Instance?.configSync?.IsAdmin == true) return true;
            return false;
        }

        #endregion
    }

    /// <summary>
    /// Trigger handler component for water swimming detection.
    /// Separate component to handle OnTriggerEnter/Exit events.
    /// Tracks interactables and properly cleans up on destruction.
    /// </summary>
    public class CustomWaterTrigger : MonoBehaviour
    {
        private CustomWaterBase m_water;
        private HashSet<IWaterInteractable> m_inWater = new HashSet<IWaterInteractable>();
        private LiquidType m_liquidType = LiquidType.Water;

        /// <summary>
        /// Layer mask for terrain blocking raycasts. Cached for performance.
        /// </summary>
        private static int s_blockingLayerMask = -1;

        public void Initialize(CustomWaterBase water)
        {
            m_water = water;
            m_liquidType = water != null ? water.LiquidType : LiquidType.Water;

            if (s_blockingLayerMask == -1)
            {
                s_blockingLayerMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            }

            FiresLogger.LogInfo($"[CustomWaterTrigger] Initialized for {water?.GetWaterType()}, LiquidType={m_liquidType}");
        }

        /// <summary>
        /// Checks if solid terrain/geometry blocks the path between a character and the water surface.
        /// Prevents characters in caves from being affected by water far above them.
        /// </summary>
        private bool IsBlockedByTerrain(Vector3 characterPos, float waterSurfaceY)
        {
            float distToWater = waterSurfaceY - characterPos.y;

            // If water is close (within 5m above), don't bother checking - allow it
            if (distToWater <= 5f)
                return false;

            // Raycast upward from character head toward water surface
            Vector3 rayStart = characterPos + Vector3.up * 1.8f;
            float rayDistance = waterSurfaceY - rayStart.y;

            if (rayDistance <= 0f)
                return false;

            return Physics.Raycast(rayStart, Vector3.up, rayDistance, s_blockingLayerMask);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (m_water == null || other == null) return;

            // Get interactable - check rigidbody first (for characters with multiple colliders)
            IWaterInteractable interactable = null;
            if (other.attachedRigidbody != null)
            {
                interactable = other.attachedRigidbody.GetComponent<IWaterInteractable>();
            }
            if (interactable == null)
            {
                interactable = other.GetComponentInParent<IWaterInteractable>();
            }

            if (interactable != null)
            {
                // CAVE FIX: Check if terrain blocks the path to this water
                Transform t = interactable.GetTransform();
                if (t != null)
                {
                    float waterSurface = m_water.GetWaterSurfaceAt(t.position);
                    if (waterSurface > float.MinValue && IsBlockedByTerrain(t.position, waterSurface))
                        return; // Blocked by terrain - don't enter water
                }

                // Track before notifying water (in case water is destroyed during callback)
                if (!m_inWater.Contains(interactable))
                {
                    m_inWater.Add(interactable);
                    interactable.Increment(m_liquidType);
                }

                m_water.OnWaterTriggerEnter(other);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (other == null) return;

            // Get interactable
            IWaterInteractable interactable = null;
            if (other.attachedRigidbody != null)
            {
                interactable = other.attachedRigidbody.GetComponent<IWaterInteractable>();
            }
            if (interactable == null)
            {
                interactable = other.GetComponentInParent<IWaterInteractable>();
            }

            if (interactable != null && m_inWater.Remove(interactable))
            {
                // Decrement and clear if no longer in any water
                if (interactable.Decrement(m_liquidType) == 0)
                {
                    interactable.SetLiquidLevel(-10000f, m_liquidType, m_water);
                }
            }

            // Also notify base class (if still valid)
            if (m_water != null)
            {
                m_water.OnWaterTriggerExit(other);
            }
        }

        private void OnDestroy()
        {
            // CRITICAL: Clean up all tracked interactables when this trigger is destroyed
            // This ensures characters stop swimming when water is deleted
            foreach (var interactable in m_inWater)
            {
                if (interactable != null)
                {
                    try
                    {
                        // Decrement water count
                        if (interactable.Decrement(m_liquidType) == 0)
                        {
                            // Set water level far below to trigger exit
                            interactable.SetLiquidLevel(-10000f, m_liquidType, m_water);
                        }
                    }
                    catch (System.Exception)
                    {
                        // Ignore errors from already-destroyed objects
                    }
                }
            }
            m_inWater.Clear();
            m_water = null;

            FiresLogger.LogInfo("[CustomWaterTrigger] OnDestroy: Cleaned up all tracked water interactables");
        }

        /// <summary>
        /// Force clears all tracked interactables. Call this before destroying the water.
        /// </summary>
        public void ForceCleanup()
        {
            foreach (var interactable in m_inWater)
            {
                if (interactable != null)
                {
                    try
                    {
                        interactable.Decrement(m_liquidType);
                        interactable.SetLiquidLevel(-10000f, m_liquidType, m_water);
                    }
                    catch (System.Exception)
                    {
                        // Ignore errors
                    }
                }
            }
            m_inWater.Clear();
        }
    }
}
