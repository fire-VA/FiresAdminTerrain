using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Custom river - terrain-following water that maintains a consistent depth above terrain.
    /// The water surface follows the terrain contour while keeping a minimum depth.
    /// 
    /// Key behaviors:
    /// - Water surface = terrain height + configured depth at each point
    /// - Floating objects follow gravity naturally (no artificial flow force removing water)
    /// - Flow direction affects visual current and boat movement, not water volume
    /// - Can spawn waterfalls at cliff edges where terrain drops sharply
    /// 
    /// Use for:
    /// - Natural streams and rivers
    /// - Canals following terrain
    /// - Mountain streams with cascades
    /// </summary>
    public class CustomRiverController : CustomWaterBase
    {
        #region River-Specific Configuration

        [Header("River Settings")]
        [Tooltip("Flow direction in degrees (0 = North, 90 = East) - affects visual current and boat movement")]
        [Range(0f, 360f)]
        public float FlowDirection = 0f;

        [Tooltip("Flow speed in meters per second - affects boats and floating objects")]
        [Range(0.1f, 10f)]
        public float FlowSpeed = 2f;

        [Tooltip("Minimum water depth above terrain (meters)")]
        [Range(0.5f, 10f)]
        public float MinDepth = 1f;

        [Tooltip("Grid resolution for terrain sampling")]
        [Range(8, 64)]
        public int GridResolution = 32;

        [Tooltip("How quickly depth adjusts to terrain changes (for smooth transitions)")]
        [Range(0.1f, 3f)]
        public float DepthSmoothing = 1f;

        #endregion

        #region Runtime State

        // Terrain sampling grid
        private float[] _terrainHeights;  // Terrain height at each grid cell
        private float[] _waterSurface;    // Calculated water surface at each cell (terrain + depth)
        private int _gridWidth;
        private int _gridHeight;
        private float _cellSize;
        private Vector3 _gridOrigin;

        // Spawned waterfalls at cliff edges
        private List<CustomWaterfallController> _spawnedWaterfalls = new List<CustomWaterfallController>();

        // Downstream rivers (spawned after waterfalls land)
        private List<CustomRiverController> _downstreamRivers = new List<CustomRiverController>();

        // Upstream source (if this river was spawned by a waterfall)
        private CustomRiverController _upstreamSource;

        // Update timers
        private float _terrainUpdateTimer = 0f;
        private float _waterfallCheckTimer = 0f;
        private const float TERRAIN_UPDATE_INTERVAL = 0.1f;  // 10 updates/sec for terrain sampling
        private const float WATERFALL_CHECK_INTERVAL = 0.5f;

        // Ghost mode detection
        private bool _isGhostMode = false;
        private Vector3 _lastGhostPosition;

        // Physics collider for floating objects
        private BoxCollider _physicsCollider;

        #endregion

        #region Implementation

        public override CustomWaterType GetWaterType() => CustomWaterType.River;

        protected override void Awake()
        {
            // Force water type
            LiquidType = LiquidType.Water;

            // Check if we're in ghost/preview mode
            _isGhostMode = m_nview == null || m_nview.GetZDO() == null;

            base.Awake();
        }

        protected override void Start()
        {
            InitializeGrid();

            // Setup physics collider for floating objects (not in ghost mode)
            if (!_isGhostMode)
            {
                SetupPhysicsCollider();
            }

            base.Start();

            _lastGhostPosition = transform.position;
        }

        private void InitializeGrid()
        {
            _gridWidth = GridResolution;
            _gridHeight = GridResolution;

            // Calculate cell size based on bounds
            _cellSize = Mathf.Max(BoundsSize.x, BoundsSize.z) / GridResolution;

            int cellCount = _gridWidth * _gridHeight;
            _terrainHeights = new float[cellCount];
            _waterSurface = new float[cellCount];

            // Calculate grid origin (bottom-left corner in local space)
            _gridOrigin = transform.position - new Vector3(BoundsSize.x * 0.5f, 0f, BoundsSize.z * 0.5f);

            // Sample terrain and calculate initial water surface
            UpdateTerrainAndWaterSurface();
        }

        /// <summary>
        /// Samples terrain heights and calculates water surface.
        /// Water surface = terrain height + MinDepth (maintaining consistent depth above terrain)
        /// </summary>
        private void UpdateTerrainAndWaterSurface()
        {
            // Update grid origin based on current position
            _gridOrigin = transform.position - new Vector3(BoundsSize.x * 0.5f, 0f, BoundsSize.z * 0.5f);

            float highestTerrain = float.MinValue;
            float lowestTerrain = float.MaxValue;

            // First pass: sample all terrain heights
            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    Vector3 worldPos = GridToWorld(x, z);
                    float terrainH = SampleTerrainHeight(worldPos);
                    _terrainHeights[idx] = terrainH;

                    if (terrainH > highestTerrain) highestTerrain = terrainH;
                    if (terrainH < lowestTerrain) lowestTerrain = terrainH;
                }
            }

            // Second pass: calculate water surface
            // Water maintains MinDepth above terrain at each point
            for (int i = 0; i < _terrainHeights.Length; i++)
            {
                _waterSurface[i] = _terrainHeights[i] + MinDepth;
            }

            // Mark mesh as dirty to regenerate
            m_meshDirty = true;
        }

        /// <summary>
        /// Sets up a physics collider that matches the water volume for floating objects.
        /// </summary>
        private void SetupPhysicsCollider()
        {
            // Create a child object for physics
            var physicsGO = new GameObject("RiverPhysics");
            physicsGO.transform.SetParent(transform);
            physicsGO.transform.localPosition = Vector3.zero;
            physicsGO.transform.localRotation = Quaternion.identity;
            physicsGO.transform.localScale = Vector3.one;
            physicsGO.layer = LayerMask.NameToLayer("Water");

            _physicsCollider = physicsGO.AddComponent<BoxCollider>();
            _physicsCollider.isTrigger = true;
            _physicsCollider.size = BoundsSize;
            _physicsCollider.center = Vector3.zero;
        }

        public override float GetWaterSurfaceAt(Vector3 worldPos)
        {
            if (!IsInsideBounds(worldPos))
                return float.MinValue;

            // Convert to grid coordinates
            int x, z;
            if (!WorldToGrid(worldPos, out x, out z))
                return float.MinValue;

            int idx = z * _gridWidth + x;
            if (idx < 0 || idx >= _waterSurface.Length)
                return float.MinValue;

            // Get the pre-calculated water surface level
            float baseSurface = _waterSurface[idx];

            // Add wave offset (scale-independent)
            float normalizedDepth = Mathf.Clamp01(MinDepth / 5f);
            float wave = CalculateWaveHeight(worldPos, normalizedDepth);

            return baseSurface + wave + SurfaceOffset;
        }

        public override void UpdateWater(float dt)
        {
            // In ghost mode, check if position changed and update terrain sampling
            if (_isGhostMode || (m_nview != null && m_nview.GetZDO() == null))
            {
                if (Vector3.Distance(transform.position, _lastGhostPosition) > 0.1f)
                {
                    UpdateTerrainAndWaterSurface();
                    _lastGhostPosition = transform.position;
                }
                return;
            }

            // Update terrain sampling periodically (terrain may change)
            _terrainUpdateTimer += dt;
            if (_terrainUpdateTimer >= TERRAIN_UPDATE_INTERVAL)
            {
                UpdateTerrainAndWaterSurface();
                _terrainUpdateTimer = 0f;
            }

            // Check for waterfall transitions at cliff edges
            _waterfallCheckTimer += dt;
            if (_waterfallCheckTimer >= WATERFALL_CHECK_INTERVAL)
            {
                CheckForWaterfallTransitions();
                _waterfallCheckTimer = 0f;
            }
        }

        protected override void GenerateMesh()
        {
            if (_terrainHeights == null || _waterSurface == null)
            {
                InitializeGrid();
            }

            // Generate mesh that follows terrain + consistent depth
            int vertexCount = _gridWidth * _gridHeight;

            Vector3[] vertices = new Vector3[vertexCount];
            Vector2[] uvs = new Vector2[vertexCount];
            List<int> triangles = new List<int>();

            // Generate vertices at water surface
            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;

                    // Local position relative to transform
                    float localX = (x * _cellSize) - (BoundsSize.x * 0.5f) + (_cellSize * 0.5f);
                    float localZ = (z * _cellSize) - (BoundsSize.z * 0.5f) + (_cellSize * 0.5f);

                    // Y is water surface relative to transform
                    float worldSurfaceY = _waterSurface[idx];
                    float localY = worldSurfaceY - transform.position.y;

                    vertices[idx] = new Vector3(localX, localY, localZ);
                    uvs[idx] = new Vector2((float)x / _gridWidth, (float)z / _gridHeight);
                }
            }

            // Generate triangles
            for (int z = 0; z < _gridHeight - 1; z++)
            {
                for (int x = 0; x < _gridWidth - 1; x++)
                {
                    int bottomLeft = z * _gridWidth + x;
                    int bottomRight = bottomLeft + 1;
                    int topLeft = bottomLeft + _gridWidth;
                    int topRight = topLeft + 1;

                    // First triangle
                    triangles.Add(bottomLeft);
                    triangles.Add(topLeft);
                    triangles.Add(bottomRight);

                    // Second triangle
                    triangles.Add(bottomRight);
                    triangles.Add(topLeft);
                    triangles.Add(topRight);
                }
            }

            // Create/update mesh
            if (m_waterMesh == null)
            {
                m_waterMesh = new Mesh();
                m_waterMesh.name = "RiverWaterMesh";
            }

            m_waterMesh.Clear();
            m_waterMesh.vertices = vertices;
            m_waterMesh.uv = uvs;
            m_waterMesh.triangles = triangles.ToArray();
            m_waterMesh.RecalculateNormals();
            m_waterMesh.RecalculateBounds();

            if (m_meshFilter != null)
                m_meshFilter.sharedMesh = m_waterMesh;
        }

        #endregion

        #region Waterfall Transitions

        private void CheckForWaterfallTransitions()
        {
            // Check edge cells for cliff drops
            // A cliff is detected when terrain drops more than MinDepth * 2 at the edge

            float cliffThreshold = MinDepth * 2f;

            // Check all four edges
            CheckEdgeForCliffs(0, 1, 0, _gridWidth, cliffThreshold, Vector3.back);      // South edge
            CheckEdgeForCliffs(_gridHeight - 1, 1, 0, _gridWidth, cliffThreshold, Vector3.forward);  // North edge
            CheckEdgeForCliffs(0, _gridWidth, 0, _gridHeight, cliffThreshold, Vector3.left);   // West edge
            CheckEdgeForCliffs(_gridWidth - 1, _gridWidth, 0, _gridHeight, cliffThreshold, Vector3.right); // East edge
        }

        private void CheckEdgeForCliffs(int fixedCoord, int step, int start, int end, float threshold, Vector3 outDir)
        {
            // This is a simplified cliff detection - full implementation would iterate edge cells
            // For now, just check the center of each edge
            int centerIdx = (start + end) / 2;

            Vector3 edgeWorldPos;
            if (step == 1)  // North/South edge
                edgeWorldPos = GridToWorld(centerIdx, fixedCoord);
            else  // East/West edge
                edgeWorldPos = GridToWorld(fixedCoord, centerIdx);

            Vector3 outsidePos = edgeWorldPos + outDir * _cellSize * 2f;

            float edgeTerrain = SampleTerrainHeight(edgeWorldPos);
            float outsideTerrain = SampleTerrainHeight(outsidePos);

            float drop = edgeTerrain - outsideTerrain;

            if (drop > threshold)
            {
                // Cliff detected - could spawn waterfall here
                // SpawnWaterfallAtEdge(edgeWorldPos, drop);
            }
        }

        /// <summary>
        /// Called by waterfall when it lands on level terrain.
        /// Spawns a new river segment continuing the flow.
        /// </summary>
        public void OnWaterfallLanded(CustomWaterfallController waterfall, Vector3 landingPos, float flowRate)
        {
            // Check what's at landing position
            float terrainHeight = SampleTerrainHeight(landingPos);
            float vanillaWater = GetVanillaWaterLevel(landingPos);

            if (vanillaWater > terrainHeight)
            {
                Debug.Log($"[CustomRiver] Waterfall landed in vanilla water at {landingPos}");
                return;
            }

            // Check for existing custom water
            var existingWater = CustomWaterManager.GetWaterAt(landingPos);
            if (existingWater != null)
            {
                Debug.Log($"[CustomRiver] Waterfall merged into existing water at {landingPos}");
                return;
            }

            // Spawn new downstream river
            var downstreamRiver = CustomWaterManager.SpawnRiver(
                landingPos,
                new Vector3(BoundsSize.x, BoundsSize.y, BoundsSize.z),
                FlowDirection,
                FlowSpeed
            );

            downstreamRiver.SetUpstreamSource(this);
            _downstreamRivers.Add(downstreamRiver);

            Debug.Log($"[CustomRiver] Spawned downstream river at {landingPos}");
        }

        #endregion

        #region Public API

        /// <summary>
        /// Sets the upstream source river (for rivers spawned by waterfalls).
        /// </summary>
        public void SetUpstreamSource(CustomRiverController source)
        {
            _upstreamSource = source;
        }

        /// <summary>
        /// Gets the flow force at a position (for boat physics).
        /// Flow direction affects boats but not water volume.
        /// </summary>
        public Vector3 GetFlowForceAt(Vector3 worldPos)
        {
            if (!IsInsideBounds(worldPos))
                return Vector3.zero;

            // Convert flow direction to vector
            float radians = FlowDirection * Mathf.Deg2Rad;
            Vector3 flowDir = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));

            // Check if position is actually in water
            float waterLevel = GetWaterSurfaceAt(worldPos);
            if (waterLevel <= float.MinValue || worldPos.y > waterLevel)
                return Vector3.zero;

            return flowDir * FlowSpeed;
        }

        /// <summary>
        /// Forces a terrain re-sample and mesh update.
        /// Call this after terrain changes near the river.
        /// </summary>
        public void RefreshTerrainSampling()
        {
            UpdateTerrainAndWaterSurface();
        }

        #endregion

        #region Grid Utilities

        private Vector3 GridToWorld(int x, int z)
        {
            return _gridOrigin + new Vector3(
                x * _cellSize + _cellSize * 0.5f,
                0f,
                z * _cellSize + _cellSize * 0.5f
            );
        }

        private bool WorldToGrid(Vector3 worldPos, out int x, out int z)
        {
            Vector3 local = worldPos - _gridOrigin;
            x = Mathf.FloorToInt(local.x / _cellSize);
            z = Mathf.FloorToInt(local.z / _cellSize);

            return x >= 0 && x < _gridWidth && z >= 0 && z < _gridHeight;
        }

        #endregion

        #region Adjacent Water Stitching

        /// <summary>
        /// Finds adjacent custom water bodies and adjusts edge heights to match.
        /// This creates seamless connections between water pieces.
        /// </summary>
        public void StitchWithAdjacentWater()
        {
            float searchRadius = Mathf.Max(BoundsSize.x, BoundsSize.z) + 5f;
            var nearbyWater = CustomWaterManager.GetWaterInRadius(transform.position, searchRadius);

            foreach (var water in nearbyWater)
            {
                if (water == this) continue;

                // Check if bounds overlap or are adjacent
                Bounds myBounds = GetWorldBounds();
                Bounds otherBounds = water.GetWorldBounds();

                // Expand bounds slightly for adjacency check
                myBounds.Expand(1f);

                if (myBounds.Intersects(otherBounds))
                {
                    // Found adjacent water - match heights at edges
                    MatchEdgeHeightsWithNeighbor(water);
                }
            }
        }

        private void MatchEdgeHeightsWithNeighbor(CustomWaterBase neighbor)
        {
            // Find overlapping region and average water heights
            // This is a simplified version - full implementation would interpolate edge vertices

            Bounds overlap = GetOverlapRegion(GetWorldBounds(), neighbor.GetWorldBounds());
            if (overlap.size.x < 0.1f && overlap.size.z < 0.1f)
                return;

            // For each vertex in overlap region, sample neighbor's height and blend
            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    Vector3 worldPos = GridToWorld(x, z);

                    if (overlap.Contains(worldPos))
                    {
                        float neighborLevel = neighbor.GetWaterSurfaceAt(worldPos);
                        if (neighborLevel > float.MinValue)
                        {
                            int idx = z * _gridWidth + x;
                            // Blend our height with neighbor's
                            _waterSurface[idx] = (_waterSurface[idx] + neighborLevel) * 0.5f;
                        }
                    }
                }
            }

            m_meshDirty = true;
        }

        private Bounds GetOverlapRegion(Bounds a, Bounds b)
        {
            Vector3 min = Vector3.Max(a.min, b.min);
            Vector3 max = Vector3.Min(a.max, b.max);

            if (min.x >= max.x || min.y >= max.y || min.z >= max.z)
                return new Bounds(Vector3.zero, Vector3.zero);

            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            return new Bounds(center, size);
        }

        #endregion
    }
}
