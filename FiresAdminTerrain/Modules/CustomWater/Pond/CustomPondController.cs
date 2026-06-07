using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Custom pond - static water contained within invisible box bounds.
    /// Water will NOT spill over edges regardless of terrain.
    /// Based on Valheim's WaterVolume for simple contained water.
    /// 
    /// GHOST PREVIEW: Shows semi-transparent box bounds with water surface at top.
    /// After placement, the bounds box becomes invisible but still works as container.
    /// Similar to EnvironmentBoxController's visibility handling.
    /// 
    /// PLACEMENT: The transform.position is the CENTER of the water volume.
    /// - Water surface is at transform.position.y + (BoundsSize.y * (WaterLevel - 0.5))
    /// - Bottom of volume is at transform.position.y - BoundsSize.y/2
    /// - Top of volume is at transform.position.y + BoundsSize.y/2
    /// 
    /// Use for:
    /// - Swimming pools
    /// - Decorative fountains
    /// - Indoor water features
    /// - Controlled water areas
    /// </summary>
    public class CustomPondController : CustomWaterBase
    {
        #region Pond-Specific Configuration

        [Header("Pond Settings")]
        [Tooltip("Water level as percentage of box height (0-1). 0.5 means water surface is at transform.y")]
        [Range(0f, 1f)]
        public float WaterLevel = 0.8f; // Default to 80% full for better swimming

        #endregion

        #region Bounds Box Visualization

        /// <summary>
        /// The bounds box mesh (visible during placement, hidden after).
        /// </summary>
        private GameObject m_boundsBoxGO;
        private MeshFilter m_boundsBoxMeshFilter;
        private MeshRenderer m_boundsBoxMeshRenderer;
        private bool m_isGhostMode = true;  // True during placement preview
        private bool m_boundsBoxHidden = false;

        #endregion

        #region Cached Values

        private float _cachedWaterSurfaceY;

        #endregion

        #region Implementation

        public override CustomWaterType GetWaterType() => CustomWaterType.Pond;

        protected override void Awake()
        {
            // CRITICAL: Force LiquidType to Water (value 0) to prevent tar footstep effects
            // The tar effects are caused by LiquidType.Tar (value 1)
            // This must happen BEFORE base.Awake() which sets up the swim trigger
            LiquidType = LiquidType.Water;

            // Double-check the integer value
            Debug.Log($"[CustomPond] Awake - Forcing LiquidType to Water. Current value: {LiquidType} (int={(int)LiquidType})");

            // Safety: If somehow still not 0, force it
            if ((int)LiquidType != 0)
            {
                Debug.LogError($"[CustomPond] LiquidType was not 0! Forcing enum value directly.");
                LiquidType = (LiquidType)0;  // Force to Water's integer value
            }

            // Check if ghost mode (no valid ZDO = placement preview)
            var nview = GetComponent<ZNetView>();
            m_isGhostMode = nview == null || nview.GetZDO() == null;

            Debug.Log($"[CustomPond] Awake - isGhostMode={m_isGhostMode}");

            base.Awake();

            // Cache the water surface Y
            UpdateCachedSurfaceY();

            // Create the bounds box visualization
            CreateBoundsBoxVisualization();
        }

        protected override void Start()
        {
            base.Start();

            // If not ghost mode (actually placed), hide the bounds box
            if (!m_isGhostMode)
            {
                HideBoundsBox();
            }

            Debug.Log($"[CustomPond] Started at {transform.position}, BoundsSize={BoundsSize}, WaterLevel={WaterLevel}, SurfaceY={_cachedWaterSurfaceY}, isGhost={m_isGhostMode}");
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            // Clean up bounds box
            if (m_boundsBoxGO != null)
            {
                Destroy(m_boundsBoxGO);
            }
        }

        protected override void Update()
        {
            base.Update();

            // Check if we transitioned from ghost to placed
            if (m_isGhostMode)
            {
                var nview = GetComponent<ZNetView>();
                if (nview != null && nview.GetZDO() != null)
                {
                    // Transitioned to placed - hide bounds box
                    m_isGhostMode = false;
                    HideBoundsBox();
                    Debug.Log("[CustomPond] Transitioned from ghost to placed - hiding bounds box");
                }
            }

            // Update bounds box size if it changed
            if (m_boundsBoxGO != null && !m_boundsBoxHidden)
            {
                UpdateBoundsBoxMesh();
            }
        }

        /// <summary>
        /// Called when the piece is placed (from VAPieceManager or WearNTear).
        /// </summary>
        public void OnPlaced()
        {
            m_isGhostMode = false;
            HideBoundsBox();
            Debug.Log($"[CustomPond] OnPlaced called - hiding bounds box");
        }

        #endregion

        #region Bounds Box Visualization

        /// <summary>
        /// Creates the semi-transparent bounds box visualization for ghost preview.
        /// Shows the container bounds with water surface at the top.
        /// </summary>
        private void CreateBoundsBoxVisualization()
        {
            m_boundsBoxGO = new GameObject("PondBoundsBox");
            m_boundsBoxGO.transform.SetParent(transform);
            m_boundsBoxGO.transform.localPosition = Vector3.zero;
            m_boundsBoxGO.transform.localRotation = Quaternion.identity;
            m_boundsBoxGO.transform.localScale = Vector3.one;

            m_boundsBoxMeshFilter = m_boundsBoxGO.AddComponent<MeshFilter>();
            m_boundsBoxMeshRenderer = m_boundsBoxGO.AddComponent<MeshRenderer>();

            // Create the box mesh
            UpdateBoundsBoxMesh();

            // Create semi-transparent material (like EnvironmentBox)
            var boxMat = CreateBoundsBoxMaterial();
            m_boundsBoxMeshRenderer.material = boxMat;
            m_boundsBoxMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_boundsBoxMeshRenderer.receiveShadows = false;

            // Start visible in ghost mode
            m_boundsBoxGO.SetActive(m_isGhostMode);
            m_boundsBoxHidden = !m_isGhostMode;

            Debug.Log($"[CustomPond] Created bounds box visualization, visible={m_isGhostMode}");
        }

        /// <summary>
        /// Updates the bounds box mesh when size changes.
        /// Creates an inverted box (faces inward) so it's visible from inside AND outside.
        /// </summary>
        private void UpdateBoundsBoxMesh()
        {
            if (m_boundsBoxMeshFilter == null) return;

            Mesh mesh = m_boundsBoxMeshFilter.sharedMesh;
            if (mesh == null)
            {
                mesh = new Mesh();
                mesh.name = "PondBoundsBoxMesh";
                m_boundsBoxMeshFilter.sharedMesh = mesh;
            }

            Vector3 half = BoundsSize / 2f;

            // Create box vertices (8 corners)
            Vector3[] vertices = new Vector3[]
            {
                // Bottom face corners
                new Vector3(-half.x, -half.y, -half.z),  // 0: back-left-bottom
                new Vector3(half.x, -half.y, -half.z),   // 1: back-right-bottom
                new Vector3(half.x, -half.y, half.z),    // 2: front-right-bottom
                new Vector3(-half.x, -half.y, half.z),   // 3: front-left-bottom
                // Top face corners
                new Vector3(-half.x, half.y, -half.z),   // 4: back-left-top
                new Vector3(half.x, half.y, -half.z),    // 5: back-right-top
                new Vector3(half.x, half.y, half.z),     // 6: front-right-top
                new Vector3(-half.x, half.y, half.z)     // 7: front-left-top
            };

            // Triangles - double-sided (visible from both inside and outside)
            int[] triangles = new int[]
            {
                // Bottom face (both sides)
                0, 1, 2, 0, 2, 3,  // from below
                0, 2, 1, 0, 3, 2,  // from above
                // Top face (both sides)
                4, 6, 5, 4, 7, 6,  // from above
                4, 5, 6, 4, 6, 7,  // from below
                // Front face (+Z, both sides)
                3, 2, 6, 3, 6, 7,  // from front
                3, 6, 2, 3, 7, 6,  // from back
                // Back face (-Z, both sides)
                0, 5, 1, 0, 4, 5,  // from back
                0, 1, 5, 0, 5, 4,  // from front
                // Left face (-X, both sides)
                0, 3, 7, 0, 7, 4,  // from left
                0, 7, 3, 0, 4, 7,  // from right
                // Right face (+X, both sides)
                1, 5, 6, 1, 6, 2,  // from right
                1, 6, 5, 1, 2, 6   // from left
            };

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// Creates a semi-transparent blue material for the bounds box.
        /// Similar to EnvironmentBoxController.CreateBoxMaterial().
        /// </summary>
        private Material CreateBoundsBoxMaterial()
        {
            // Try Sprites/Default first (supports transparency well)
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("UI/Default");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                Debug.LogWarning("[CustomPond] Could not find shader for bounds box material!");
                return null;
            }

            var mat = new Material(shader);

            // Semi-transparent blue for water
            mat.color = new Color(0.2f, 0.5f, 0.9f, 0.25f);
            mat.renderQueue = 3000; // Transparent queue

            return mat;
        }

        /// <summary>
        /// Hides the bounds box (after placement).
        /// The box collider/trigger still works, just the visualization is hidden.
        /// </summary>
        public void HideBoundsBox()
        {
            if (m_boundsBoxGO != null)
            {
                m_boundsBoxGO.SetActive(false);
                m_boundsBoxHidden = true;
                Debug.Log("[CustomPond] Bounds box hidden");
            }
        }

        /// <summary>
        /// Shows the bounds box (for editing/visibility toggle).
        /// </summary>
        public void ShowBoundsBox()
        {
            if (m_boundsBoxGO != null)
            {
                m_boundsBoxGO.SetActive(true);
                m_boundsBoxHidden = false;
                Debug.Log("[CustomPond] Bounds box shown");
            }
        }

        /// <summary>
        /// Toggles bounds box visibility.
        /// </summary>
        public void ToggleBoundsBoxVisibility()
        {
            if (m_boundsBoxHidden)
                ShowBoundsBox();
            else
                HideBoundsBox();
        }

        #endregion

        #region Water Surface

        private void UpdateCachedSurfaceY()
        {
            // Calculate world Y of water surface
            // If WaterLevel=0.5, surface is at transform.position.y
            // If WaterLevel=1.0, surface is at transform.position.y + BoundsSize.y/2
            // If WaterLevel=0.0, surface is at transform.position.y - BoundsSize.y/2
            float halfHeight = BoundsSize.y * 0.5f;
            float localSurfaceY = (WaterLevel - 0.5f) * BoundsSize.y;
            _cachedWaterSurfaceY = transform.position.y + localSurfaceY;
        }

        public override float GetWaterSurfaceAt(Vector3 worldPos)
        {
            // Check if position is inside bounds (XZ only - Y doesn't matter for surface query)
            Vector3 localPos = transform.InverseTransformPoint(worldPos);
            Vector3 halfSize = BoundsSize * 0.5f;

            if (Mathf.Abs(localPos.x) > halfSize.x || Mathf.Abs(localPos.z) > halfSize.z)
                return float.MinValue;

            // Calculate base water surface Y
            float baseSurfaceY = _cachedWaterSurfaceY;

            // Add wave offset (scale-independent)
            float depth = Mathf.Clamp01(BoundsSize.y / 10f);
            float wave = CalculateWaveHeight(worldPos, depth);

            return baseSurfaceY + wave + SurfaceOffset;
        }

        public override void UpdateWater(float dt)
        {
            // Ponds don't flow - just static water
            // Update cached surface Y in case transform moved
            UpdateCachedSurfaceY();
        }

        protected override void GenerateMesh()
        {
            // Calculate water surface Y position in LOCAL space
            // The mesh is relative to the transform, so we need local coordinates
            float localWaterY = (WaterLevel - 0.5f) * BoundsSize.y;

            // Generate simple surface mesh at the local water level
            GenerateSimpleSurfaceMesh(localWaterY);

            Debug.Log($"[CustomPond] Generated mesh: localY={localWaterY:F2}, worldSurfaceY={_cachedWaterSurfaceY:F2}");
        }

        protected override void SetupSwimTrigger()
        {
            // Override to position the swim trigger correctly
            // The trigger should be a volume from the bottom of the water to the surface

            m_swimTriggerGO = new GameObject("SwimTrigger");
            m_swimTriggerGO.transform.SetParent(transform);
            m_swimTriggerGO.transform.localRotation = Quaternion.identity;
            m_swimTriggerGO.transform.localScale = Vector3.one;

            // Get character_trigger layer
            int layer = LayerMask.NameToLayer("character_trigger");
            if (layer == -1)
            {
                layer = 0;
                Debug.LogWarning("[CustomPond] Layer 'character_trigger' not found, using Default");
            }
            m_swimTriggerGO.layer = layer;

            m_swimCollider = m_swimTriggerGO.AddComponent<BoxCollider>();
            m_swimCollider.isTrigger = true;

            // The trigger should cover the water-filled portion of the volume
            // Height from bottom to water surface
            float waterFilledHeight = BoundsSize.y * WaterLevel;

            // Center the collider at the middle of the filled portion
            // Bottom of water is at -BoundsSize.y/2 (local), water surface is at (WaterLevel-0.5)*BoundsSize.y
            float colliderCenterY = -BoundsSize.y * 0.5f + waterFilledHeight * 0.5f;

            m_swimCollider.size = new Vector3(BoundsSize.x, waterFilledHeight, BoundsSize.z);
            m_swimCollider.center = new Vector3(0, colliderCenterY, 0);
            m_swimTriggerGO.transform.localPosition = Vector3.zero;

            // Add trigger handler
            var triggerHandler = m_swimTriggerGO.AddComponent<CustomWaterTrigger>();
            triggerHandler.Initialize(this);

            Debug.Log($"[CustomPond] SwimTrigger: size={m_swimCollider.size}, center={m_swimCollider.center}, layer={m_swimTriggerGO.layer}");
        }

        #endregion

        #region Size Changes

        public override void SetBoundsSize(Vector3 newSize)
        {
            base.SetBoundsSize(newSize);

            // Update bounds box mesh
            UpdateBoundsBoxMesh();

            // Update swim trigger
            if (m_swimCollider != null)
            {
                float waterFilledHeight = newSize.y * WaterLevel;
                float colliderCenterY = -newSize.y * 0.5f + waterFilledHeight * 0.5f;
                m_swimCollider.size = new Vector3(newSize.x, waterFilledHeight, newSize.z);
                m_swimCollider.center = new Vector3(0, colliderCenterY, 0);
            }

            UpdateCachedSurfaceY();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Gets the current fill percentage (0-1).
        /// </summary>
        public float GetFillPercent() => WaterLevel;

        /// <summary>
        /// Sets the water level (0-1 percentage of box height).
        /// </summary>
        public void SetWaterLevel(float level)
        {
            WaterLevel = Mathf.Clamp01(level);
            UpdateCachedSurfaceY();
            m_meshDirty = true;

            // Update swim collider size
            if (m_swimCollider != null)
            {
                float waterFilledHeight = BoundsSize.y * WaterLevel;
                float colliderCenterY = -BoundsSize.y * 0.5f + waterFilledHeight * 0.5f;
                m_swimCollider.size = new Vector3(BoundsSize.x, waterFilledHeight, BoundsSize.z);
                m_swimCollider.center = new Vector3(0, colliderCenterY, 0);
            }
        }

        /// <summary>
        /// Adds water volume (raises water level).
        /// </summary>
        public void AddWater(float amount)
        {
            float volumeChange = amount / (BoundsSize.x * BoundsSize.z);
            float levelChange = volumeChange / BoundsSize.y;
            SetWaterLevel(WaterLevel + levelChange);
        }

        /// <summary>
        /// Removes water volume (lowers water level).
        /// </summary>
        public void RemoveWater(float amount)
        {
            AddWater(-amount);
        }

        /// <summary>
        /// Gets the world-space Y coordinate of the water surface.
        /// </summary>
        public float GetWaterSurfaceY()
        {
            return _cachedWaterSurfaceY + SurfaceOffset;
        }

        /// <summary>
        /// Gets whether the bounds box is currently visible.
        /// </summary>
        public bool IsBoundsBoxVisible => !m_boundsBoxHidden;

        #endregion
    }
}
