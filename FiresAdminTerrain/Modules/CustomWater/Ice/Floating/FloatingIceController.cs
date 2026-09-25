using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Water.Ice;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Floating ice controller - water-based iceberg with height-modifiable top surface.
    /// 
    /// Floating ice controller features:
    /// - Floating at water level with vanilla Floating component for bobbing
    /// - Top surface supports height modification (ridges, hills via IH)
    /// - Bottom surface drops deep like an upside-down mountain (visual only, no collision)
    /// - Walking MeshCollider follows sculpted top surface (players feel ridges/hills)
    /// - Flat BoxCollider (trigger) on root for Floating buoyancy component only
    /// - Hole-breaking, climb-out edge handler, slippery physics
    /// - Marching-squares boundary with noise-randomized edges (IceMeshUtility)
    /// - ZDO persistence via ice1 prefab clone
    /// 
    /// THREE-COLLIDER ARCHITECTURE:
    /// 1. Walking MeshCollider on child "IceShell" GO - follows sculpted top surface
    /// 2. Floating BoxCollider on root GO (trigger) - buoyancy only
    /// 3. Bottom mesh has NO collider - purely cosmetic
    /// </summary>
    public class FloatingIceController : MonoBehaviour
    {
        #region Constants

        private const float ICE_THICKNESS = 0.45f;
        private const float ICE_SURFACE_OFFSET = 0.05f;
        private const float CELL_SIZE = 0.25f;
        private const int MAX_HOLES = 20;
        private const float ICEBERG_DEPTH = 2.5f;

        private const float HOLE_WALL_DEPTH = 0.5f;
        private const float HOLE_WALL_TOP_INSET = 0.3f;
        private const float HOLE_WALL_THICKNESS = 0.25f;
        private const int HOLE_WALL_SEGMENTS = 8;

        private const float FLOAT_SMOOTH_TIME = 0.4f;
        private const float FLOAT_BOB_AMPLITUDE = 0.03f;
        private const float FLOAT_BOB_FREQUENCY = 0.5f;

        #endregion

        #region ZDO Keys

        private static readonly int s_isFloatIceHash = "va_float_ice".GetStableHashCode();
        private static readonly int s_floatIceFootprintsHash = "va_float_ice_fp".GetStableHashCode();
        private static readonly int s_floatIceNoiseSeedHash = "va_float_ice_seed".GetStableHashCode();
        private static readonly int s_floatIceHeightsHash = "va_float_ice_heights".GetStableHashCode();
        private static readonly int s_floatIceGridHash = "va_float_ice_grid".GetStableHashCode();
        private static readonly int s_floatIceWaterYHash = "va_float_ice_wy".GetStableHashCode();

        #endregion

        #region State

        private List<IceDiskFootprint> _footprints = new List<IceDiskFootprint>();
        private List<IceHole> _holes = new List<IceHole>();
        private float _noiseSeed;
        private static int s_instanceCounter = 0;
        private bool _initialized = false;

        // Height grid (top surface deltas from water level)
        private float[] _heights;
        private int _gridWidth;
        private int _gridHeight;
        private float _boundsMinX, _boundsMaxX, _boundsMinZ, _boundsMaxZ;

        // Mesh components - ice shell (top + sides)
        private GameObject _iceGO;
        private MeshFilter _iceMeshFilter;
        private MeshRenderer _iceRenderer;
        private MeshCollider _walkCollider;
        private Mesh _iceMesh;
        private Mesh _walkColliderMesh;

        // Bottom mesh (visual only, no collider)
        private GameObject _bottomGO;
        private MeshFilter _bottomMeshFilter;
        private MeshRenderer _bottomRenderer;
        private Mesh _bottomMesh;

        // Water under-mesh (visible through holes)
        private GameObject _waterGO;
        private MeshFilter _waterMeshFilter;
        private MeshRenderer _waterRenderer;
        private Mesh _waterMesh;

        // Materials
        private Material _iceMaterial;
        private Material _waterMaterial;

        // Hole/edge walls
        private List<GameObject> _holeWalls = new List<GameObject>();
        private List<GameObject> _edgeWalls = new List<GameObject>();

        // Floating state
        private float _floatTargetY;
        private float _floatVelocity;

        #endregion

        #region Properties

        public float IceSurfaceY { get; private set; }
        public bool IsFloating => true;
        public int HoleCount => _holes.Count;
        public IReadOnlyList<IceHole> Holes => _holes;
        public IReadOnlyList<IceDiskFootprint> Footprints => _footprints;

        #endregion

        #region Static Registry

        private static List<FloatingIceController> s_allFloatingIce = new List<FloatingIceController>();
        public static IReadOnlyList<FloatingIceController> AllFloatingIce => s_allFloatingIce;

        #endregion

        #region Public API - Initialization

        public void InitializeFloating(Vector3 center, float radius)
        {
            _noiseSeed = (s_instanceCounter++) * 137.5f;
            _iceMaterial = WaterMaterialManager.GetMaterial(CustomLiquidType.Ice);
            _waterMaterial = WaterMaterialManager.GetMaterial(CustomLiquidType.Water);

            float waterY = SampleWaterLevel(center);
            if (waterY > -999f)
                IceSurfaceY = waterY + ICE_SURFACE_OFFSET;
            else
                IceSurfaceY = center.y;

            transform.position = new Vector3(center.x, IceSurfaceY, center.z);

            _footprints.Add(new IceDiskFootprint { Center = center, Radius = radius });
            RecalcBounds();
            InitializeHeightGrid();

            CreateGameObjects();
            SetupFloatingPhysics();
            RebuildMesh();

            _initialized = true;
            if (!s_allFloatingIce.Contains(this))
                s_allFloatingIce.Add(this);

            SaveToZDO();
            FiresLogger.LogInfo($"[FloatingIceController] Initialized: r={radius}, Y={IceSurfaceY:F2}");
        }

        #endregion

        #region Public API - Footprints

        public void AddFootprint(Vector3 center, float radius)
        {
            _footprints.Add(new IceDiskFootprint { Center = center, Radius = radius });
            ExpandHeightGrid();

            var box = gameObject.GetComponent<BoxCollider>();
            if (box != null) UpdateFloatingColliderBounds(box);
            RebuildMesh();

            SaveToZDO();
            FiresLogger.LogInfo($"[FloatingIceController] Added footprint at {center}, r={radius}, total={_footprints.Count}");
        }

        public bool OverlapsWithDisk(Vector3 center, float radius)
        {
            return IceMeshUtility.OverlapsWithAnyFootprint(_footprints, center, radius);
        }

        public bool ContainsPosition(Vector3 worldPos)
        {
            return IceMeshUtility.IsInsideAnyFootprint(_footprints, worldPos.x, worldPos.z);
        }

        #endregion

        #region Public API - Height Modification

        public void ModifyTopHeights(Vector3 worldPos, float delta, float radius)
        {
            if (_heights == null || _heights.Length == 0) return;

            bool modified = false;

            for (int gz = 0; gz < _gridHeight; gz++)
            {
                for (int gx = 0; gx < _gridWidth; gx++)
                {
                    int idx = gz * _gridWidth + gx;
                    if (_heights[idx] < -50f) continue;

                    float wx = _boundsMinX + gx * CELL_SIZE;
                    float wz = _boundsMinZ + gz * CELL_SIZE;

                    float dx = wx - worldPos.x;
                    float dz = wz - worldPos.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);

                    if (dist <= radius)
                    {
                        float t = 1f - (dist / radius);
                        t = t * t * (3f - 2f * t); // smoothstep
                        _heights[idx] += delta * t;
                        modified = true;
                    }
                }
            }

            if (modified)
            {
                RebuildMesh();
                SaveToZDO();
            }
        }

        public void SmoothTopHeights(Vector3 worldPos, float radius, int passes = 3)
        {
            if (_heights == null || _heights.Length == 0) return;

            float[] smoothed = new float[_heights.Length];

            for (int pass = 0; pass < passes; pass++)
            {
                Array.Copy(_heights, smoothed, _heights.Length);

                for (int gz = 1; gz < _gridHeight - 1; gz++)
                {
                    for (int gx = 1; gx < _gridWidth - 1; gx++)
                    {
                        int idx = gz * _gridWidth + gx;
                        if (_heights[idx] < -50f) continue;

                        float wx = _boundsMinX + gx * CELL_SIZE;
                        float wz = _boundsMinZ + gz * CELL_SIZE;

                        float dx = wx - worldPos.x;
                        float dz = wz - worldPos.z;
                        float dist = Mathf.Sqrt(dx * dx + dz * dz);
                        if (dist > radius) continue;

                        float sum = _heights[idx] * 2f;
                        float weightSum = 2f;

                        for (int nz = -1; nz <= 1; nz++)
                        {
                            for (int nx = -1; nx <= 1; nx++)
                            {
                                if (nx == 0 && nz == 0) continue;
                                int ni = (gz + nz) * _gridWidth + (gx + nx);
                                if (_heights[ni] > -50f)
                                {
                                    float w = (nx == 0 || nz == 0) ? 1f : 0.7f;
                                    sum += _heights[ni] * w;
                                    weightSum += w;
                                }
                            }
                        }

                        float blend = 1f - (dist / radius);
                        blend = Mathf.Clamp01(blend);
                        smoothed[idx] = Mathf.Lerp(_heights[idx], sum / weightSum, blend * 0.6f);
                    }
                }

                Array.Copy(smoothed, _heights, _heights.Length);
            }

            RebuildMesh();
            SaveToZDO();
        }

        #endregion

        #region Public API - Holes

        public bool BreakIceAt(Vector3 worldPos, float radius)
        {
            if (!IceMeshUtility.IsInsideAnyFootprint(_footprints, worldPos.x, worldPos.z))
                return false;

            if (_holes.Count >= MAX_HOLES)
                _holes.RemoveAt(0);

            _holes.Add(new IceHole
            {
                Center = worldPos,
                Radius = Mathf.Max(0.5f, radius),
                TimeCreated = Time.time
            });

            RebuildMesh();
            FiresLogger.LogInfo($"[FloatingIceController] Broke ice at {worldPos}, r={radius:F1} ({_holes.Count} holes)");
            return true;
        }

        #endregion

        #region Public API - Queries & Removal

        public static FloatingIceController GetFloatingIceAt(Vector3 worldPos)
        {
            s_allFloatingIce.RemoveAll(c => c == null);
            foreach (var ctrl in s_allFloatingIce)
            {
                if (ctrl != null && ctrl.ContainsPosition(worldPos))
                    return ctrl;
            }
            return null;
        }

        public static List<FloatingIceController> GetFloatingIceInRadius(Vector3 center, float radius)
        {
            s_allFloatingIce.RemoveAll(c => c == null);
            var result = new List<FloatingIceController>();
            foreach (var ctrl in s_allFloatingIce)
            {
                if (ctrl == null) continue;
                if (ctrl.OverlapsWithDisk(center, radius))
                    result.Add(ctrl);
            }
            return result;
        }

        public void Remove()
        {
            s_allFloatingIce.Remove(this);
            Cleanup();

            IcePlacementTool.SafeDestroyIcePrefab(gameObject);
        }

        public static bool ZdoHasFloatingIceData(ZDO zdo)
        {
            return zdo != null && zdo.GetBool(s_isFloatIceHash, false);
        }

        #endregion

        #region Lifecycle

        private void Start()
        {
            if (_initialized) return;
            if (LoadFromZDO())
            {
                _initialized = true;
                FiresLogger.LogInfo($"[FloatingIceController] Restored from ZDO at {transform.position} with {_footprints.Count} footprints");
            }
        }

        private void FixedUpdate()
        {
            var floating = gameObject.GetComponent<Floating>();
            if (floating != null)
            {
                IceSurfaceY = transform.position.y;
                return;
            }

            // Fallback custom floating
            float waterY = SampleWaterLevel(transform.position);
            if (waterY <= -999f) return;

            _floatTargetY = waterY + ICE_SURFACE_OFFSET;
            float bob = Mathf.Sin(Time.time * FLOAT_BOB_FREQUENCY * Mathf.PI * 2f) * FLOAT_BOB_AMPLITUDE;
            _floatTargetY += bob;

            float currentY = transform.position.y;
            float newY = Mathf.SmoothDamp(currentY, _floatTargetY, ref _floatVelocity, FLOAT_SMOOTH_TIME, Mathf.Infinity, Time.fixedDeltaTime);

            var pos = transform.position;
            pos.y = newY;
            transform.position = pos;
            IceSurfaceY = newY;
        }

        private void OnDestroy()
        {
            s_allFloatingIce.Remove(this);
            Cleanup();
        }

        private void Cleanup()
        {
            DestroyHoleWalls();
            DestroyEdgeWalls();
            if (_waterGO != null) Destroy(_waterGO);
            if (_iceGO != null) Destroy(_iceGO);
            if (_bottomGO != null) Destroy(_bottomGO);
            if (_waterMesh != null) Destroy(_waterMesh);
            if (_iceMesh != null) Destroy(_iceMesh);
            if (_bottomMesh != null) Destroy(_bottomMesh);
            if (_walkColliderMesh != null) Destroy(_walkColliderMesh);
        }

        #endregion

        #region Height Grid

        private void RecalcBounds()
        {
            IceMeshUtility.RecalcBounds(_footprints, CELL_SIZE,
                out _boundsMinX, out _boundsMaxX, out _boundsMinZ, out _boundsMaxZ);
        }

        private void InitializeHeightGrid()
        {
            _gridWidth = Mathf.CeilToInt((_boundsMaxX - _boundsMinX) / CELL_SIZE) + 2;
            _gridHeight = Mathf.CeilToInt((_boundsMaxZ - _boundsMinZ) / CELL_SIZE) + 2;
            _heights = new float[_gridWidth * _gridHeight];

            for (int gz = 0; gz < _gridHeight; gz++)
            {
                for (int gx = 0; gx < _gridWidth; gx++)
                {
                    float wx = _boundsMinX + gx * CELL_SIZE;
                    float wz = _boundsMinZ + gz * CELL_SIZE;
                    int idx = gz * _gridWidth + gx;

                    float edgeDist = IceMeshUtility.GetEdgeDistance(_footprints, wx, wz, _noiseSeed);
                    if (edgeDist <= 0f)
                    {
                        _heights[idx] = -100f; // outside
                        continue;
                    }

                    // Default: flat at water level (delta = 0)
                    _heights[idx] = 0f;
                }
            }
        }

        private void ExpandHeightGrid()
        {
            float oldMinX = _boundsMinX, oldMinZ = _boundsMinZ;
            int oldW = _gridWidth, oldH = _gridHeight;
            float[] oldHeights = _heights;

            RecalcBounds();
            _gridWidth = Mathf.CeilToInt((_boundsMaxX - _boundsMinX) / CELL_SIZE) + 2;
            _gridHeight = Mathf.CeilToInt((_boundsMaxZ - _boundsMinZ) / CELL_SIZE) + 2;
            _heights = new float[_gridWidth * _gridHeight];

            for (int i = 0; i < _heights.Length; i++)
                _heights[i] = -100f;

            // Copy old heights
            if (oldHeights != null && oldW > 0 && oldH > 0)
            {
                for (int oz = 0; oz < oldH; oz++)
                {
                    for (int ox = 0; ox < oldW; ox++)
                    {
                        int oldIdx = oz * oldW + ox;
                        if (oldIdx >= oldHeights.Length) continue;
                        if (oldHeights[oldIdx] < -50f) continue;

                        float wx = oldMinX + ox * CELL_SIZE;
                        float wz = oldMinZ + oz * CELL_SIZE;

                        int nx = Mathf.RoundToInt((wx - _boundsMinX) / CELL_SIZE);
                        int nz = Mathf.RoundToInt((wz - _boundsMinZ) / CELL_SIZE);
                        if (nx >= 0 && nx < _gridWidth && nz >= 0 && nz < _gridHeight)
                        {
                            _heights[nz * _gridWidth + nx] = oldHeights[oldIdx];
                        }
                    }
                }
            }

            // Fill new areas that are inside footprints
            for (int gz = 0; gz < _gridHeight; gz++)
            {
                for (int gx = 0; gx < _gridWidth; gx++)
                {
                    int idx = gz * _gridWidth + gx;
                    if (_heights[idx] > -50f) continue;

                    float wx = _boundsMinX + gx * CELL_SIZE;
                    float wz = _boundsMinZ + gz * CELL_SIZE;

                    float edgeDist = IceMeshUtility.GetEdgeDistance(_footprints, wx, wz, _noiseSeed);
                    if (edgeDist <= 0f) continue;

                    _heights[idx] = 0f;
                }
            }
        }

        #endregion

        #region Floating Physics

        private void SetupFloatingPhysics()
        {
            var rb = gameObject.GetComponent<Rigidbody>();
            if (rb == null)
                rb = gameObject.AddComponent<Rigidbody>();

            rb.mass = 20f;
            rb.linearDamping = 0.5f;
            rb.angularDamping = 0.5f;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.constraints = RigidbodyConstraints.FreezeRotation;

            var box = gameObject.GetComponent<BoxCollider>();
            if (box == null)
                box = gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            UpdateFloatingColliderBounds(box);

            var floating = gameObject.GetComponent<Floating>();
            if (floating == null)
                floating = gameObject.AddComponent<Floating>();

            floating.m_waterLevelOffset = 0.3f;
            floating.m_forceDistance = 0.5f;
            floating.m_force = 0.5f;
            floating.m_balanceForceFraction = 0.02f;
            floating.m_damping = 0.05f;

            _floatTargetY = IceSurfaceY;
            _floatVelocity = 0f;
        }

        private void UpdateFloatingColliderBounds(BoxCollider box)
        {
            if (box == null) return;
            Vector3 myPos = transform.position;
            float cx = (_boundsMinX + _boundsMaxX) * 0.5f - myPos.x;
            float cz = (_boundsMinZ + _boundsMaxZ) * 0.5f - myPos.z;
            float sx = _boundsMaxX - _boundsMinX;
            float sz = _boundsMaxZ - _boundsMinZ;
            box.center = new Vector3(cx, -ICE_THICKNESS * 0.5f, cz);
            box.size = new Vector3(sx, ICE_THICKNESS, sz);
        }

        #endregion

        #region Game Object Setup

        private void CreateGameObjects()
        {
            // Water under-mesh (visible through holes)
            if (_waterGO != null) Destroy(_waterGO);
            _waterGO = new GameObject("IceWaterUnder");
            _waterGO.transform.SetParent(transform);
            _waterGO.transform.localPosition = Vector3.zero;
            _waterGO.transform.localRotation = Quaternion.identity;
            _waterGO.transform.localScale = Vector3.one;
            _waterGO.layer = LayerMask.NameToLayer("Water");

            _waterMesh = new Mesh { name = "FloatingIceWaterMesh" };
            _waterMeshFilter = _waterGO.AddComponent<MeshFilter>();
            _waterMeshFilter.sharedMesh = _waterMesh;
            _waterRenderer = _waterGO.AddComponent<MeshRenderer>();
            if (_waterMaterial != null) _waterRenderer.material = _waterMaterial;

            // Ice shell (top face + side walls, walkable collider)
            if (_iceGO != null) Destroy(_iceGO);
            _iceGO = new GameObject("IceShell");
            _iceGO.transform.SetParent(transform);
            _iceGO.transform.localPosition = Vector3.zero;
            _iceGO.transform.localRotation = Quaternion.identity;
            _iceGO.transform.localScale = Vector3.one;
            // Use "piece" layer so Infinity Hammer's placement ruler can hover
            // over existing ice (same as DirtFloorMeshCombiner). The "Default" layer
            // blocks IH's raycast and shows "invalid placement".
            int iceShellLayer = LayerMask.NameToLayer("piece");
            if (iceShellLayer < 0) iceShellLayer = LayerMask.NameToLayer("Default");
            _iceGO.layer = iceShellLayer;

            _iceMesh = new Mesh { name = "FloatingIceShellMesh" };
            _iceMeshFilter = _iceGO.AddComponent<MeshFilter>();
            _iceMeshFilter.sharedMesh = _iceMesh;
            _iceRenderer = _iceGO.AddComponent<MeshRenderer>();
            if (_iceMaterial != null) _iceRenderer.material = _iceMaterial;

            // Kinematic Rigidbody on child so non-convex MeshCollider works
            var shellRb = _iceGO.AddComponent<Rigidbody>();
            shellRb.isKinematic = true;
            shellRb.useGravity = false;

            _walkColliderMesh = new Mesh { name = "FloatingIceWalkCollider" };
            _walkCollider = _iceGO.AddComponent<MeshCollider>();
            _walkCollider.convex = false;

            // Bottom mesh (visual only - deep iceberg shape, no collider)
            if (_bottomGO != null) Destroy(_bottomGO);
            _bottomGO = new GameObject("IceBottom");
            _bottomGO.transform.SetParent(transform);
            _bottomGO.transform.localPosition = Vector3.zero;
            _bottomGO.transform.localRotation = Quaternion.identity;
            _bottomGO.transform.localScale = Vector3.one;
            _bottomGO.layer = LayerMask.NameToLayer("Water"); // visible underwater

            _bottomMesh = new Mesh { name = "FloatingIceBottomMesh" };
            _bottomMeshFilter = _bottomGO.AddComponent<MeshFilter>();
            _bottomMeshFilter.sharedMesh = _bottomMesh;
            _bottomRenderer = _bottomGO.AddComponent<MeshRenderer>();
            if (_iceMaterial != null) _bottomRenderer.material = _iceMaterial;
        }

        #endregion

        #region Mesh Generation

        private void RebuildMesh()
        {
            if (_heights == null || _heights.Length == 0) return;

            RecalcBounds();
            Vector3 myPos = transform.position;

            int totalVerts = _gridWidth * _gridHeight;
            var edgeDist = new float[totalVerts];
            var inHole = new bool[totalVerts];

            // Compute signed distance field and hole membership
            for (int gz = 0; gz < _gridHeight; gz++)
            {
                for (int gx = 0; gx < _gridWidth; gx++)
                {
                    float wx = _boundsMinX + gx * CELL_SIZE;
                    float wz = _boundsMinZ + gz * CELL_SIZE;
                    int idx = gz * _gridWidth + gx;

                    edgeDist[idx] = IceMeshUtility.GetEdgeDistance(_footprints, wx, wz, _noiseSeed);

                    if (edgeDist[idx] > 0f)
                    {
                        for (int h = 0; h < _holes.Count; h++)
                        {
                            float hdx = wx - _holes[h].Center.x;
                            float hdz = wz - _holes[h].Center.z;
                            if (hdx * hdx + hdz * hdz <= _holes[h].Radius * _holes[h].Radius)
                            {
                                inHole[idx] = true;
                                break;
                            }
                        }
                    }
                }
            }

            // Build ice shell mesh (top face with heights + side walls)
            BuildIceShellMesh(edgeDist, inHole, myPos);

            // Build walking collider mesh (top face only, following heights)
            BuildWalkColliderMesh(edgeDist, inHole, myPos);

            // Build bottom mesh (deep iceberg shape, visual only)
            BuildBottomMesh(edgeDist, myPos);

            // Build water under-mesh
            BuildWaterUnderMesh(edgeDist, myPos);

            // Hole walls & edge walls
            RebuildHoleWalls(myPos.y);
            RebuildEdgeWalls(myPos.y);
        }

        private void BuildIceShellMesh(float[] edgeDist, bool[] inHole, Vector3 myPos)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();
            var norms = new List<Vector3>();

            Func<float, float> topY = (ed) => -IceMeshUtility.ComputeBevel(ed);

            for (int gz = 0; gz < _gridHeight - 1; gz++)
            {
                for (int gx = 0; gx < _gridWidth - 1; gx++)
                {
                    int i0 = gz * _gridWidth + gx, i1 = i0 + 1;
                    int i2 = (gz + 1) * _gridWidth + gx, i3 = i2 + 1;

                    float d0 = edgeDist[i0], d1 = edgeDist[i1];
                    float d2 = edgeDist[i2], d3 = edgeDist[i3];

                    float wx0 = _boundsMinX + gx * CELL_SIZE, wz0 = _boundsMinZ + gz * CELL_SIZE;
                    float wx1 = wx0 + CELL_SIZE, wz1 = wz0;
                    float wx2 = wx0, wz2 = wz0 + CELL_SIZE;
                    float wx3 = wx0 + CELL_SIZE, wz3 = wz0 + CELL_SIZE;

                    // Height offsets from the grid
                    float h0 = _heights[i0] > -50f ? _heights[i0] : 0f;
                    float h1 = _heights[i1] > -50f ? _heights[i1] : 0f;
                    float h2 = _heights[i2] > -50f ? _heights[i2] : 0f;
                    float h3 = _heights[i3] > -50f ? _heights[i3] : 0f;

                    // Displaced corners for edge smoothing
                    float fwx0 = wx0, fwz0 = wz0;
                    float fwx1 = wx1, fwz1 = wz1;
                    float fwx2 = wx2, fwz2 = wz2;
                    float fwx3 = wx3, fwz3 = wz3;

                    bool b0 = d0 > 0f, b1 = d1 > 0f, b2 = d2 > 0f, b3 = d3 > 0f;
                    int caseIndex = (b0 ? 1 : 0) | (b1 ? 2 : 0) | (b2 ? 4 : 0) | (b3 ? 8 : 0);

                    if (caseIndex == 0) continue;

                    if (caseIndex == 15)
                    {
                        // Skip if in hole
                        if (inHole[i0] || inHole[i1] || inHole[i2] || inHole[i3])
                            continue;

                        float minD = Mathf.Min(d0, Mathf.Min(d1, Mathf.Min(d2, d3)));
                        if (minD < IceMeshUtility.EDGE_BEVEL_WIDTH)
                        {
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx0, ref fwz0, d0, wx0, wz0);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx1, ref fwz1, d1, wx1, wz1);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx2, ref fwz2, d2, wx2, wz2);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx3, ref fwz3, d3, wx3, wz3);
                        }

                        // Top face with height offsets + bevel
                        int v0 = verts.Count;
                        verts.Add(new Vector3(fwx0 - myPos.x, topY(d0) + h0, fwz0 - myPos.z));
                        verts.Add(new Vector3(fwx1 - myPos.x, topY(d1) + h1, fwz1 - myPos.z));
                        verts.Add(new Vector3(fwx2 - myPos.x, topY(d2) + h2, fwz2 - myPos.z));
                        verts.Add(new Vector3(fwx3 - myPos.x, topY(d3) + h3, fwz3 - myPos.z));
                        uvs.Add(new Vector2(wx0 * 0.1f, wz0 * 0.1f));
                        uvs.Add(new Vector2(wx1 * 0.1f, wz1 * 0.1f));
                        uvs.Add(new Vector2(wx2 * 0.1f, wz2 * 0.1f));
                        uvs.Add(new Vector2(wx3 * 0.1f, wz3 * 0.1f));
                        norms.Add(Vector3.up); norms.Add(Vector3.up);
                        norms.Add(Vector3.up); norms.Add(Vector3.up);
                        tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
                        tris.Add(v0 + 1); tris.Add(v0 + 2); tris.Add(v0 + 3);
                        continue;
                    }

                    // Boundary quad - emit with heights via IceMeshUtility
                    // Use topY with height offset for each corner
                    EmitBoundaryTopFace(verts, tris, uvs, norms,
                        wx0, wz0, d0, h0, wx1, wz1, d1, h1,
                        wx2, wz2, d2, h2, wx3, wz3, d3, h3,
                        myPos, b0, b1, b2, b3);

                    // Side walls along boundary
                    IceMeshUtility.EmitMarchingSquaresSideWalls(verts, tris, uvs, norms,
                        wx0, wz0, d0, wx1, wz1, d1, wx2, wz2, d2, wx3, wz3, d3,
                        myPos.x, myPos.z, topY, -ICE_THICKNESS);
                }
            }

            _iceMesh.Clear();
            if (verts.Count > 0 && tris.Count > 0)
            {
                _iceMesh.SetVertices(verts);
                _iceMesh.SetTriangles(tris, 0);
                _iceMesh.SetUVs(0, uvs);
                _iceMesh.SetNormals(norms);
                _iceMesh.RecalculateBounds();
            }
        }

        private void EmitBoundaryTopFace(
            List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Vector3> norms,
            float wx0, float wz0, float d0, float h0,
            float wx1, float wz1, float d1, float h1,
            float wx2, float wz2, float d2, float h2,
            float wx3, float wz3, float d3, float h3,
            Vector3 myPos, bool b0, bool b1, bool b2, bool b3)
        {
            bool hasBot = b0 != b1, hasLeft = b0 != b2, hasRight = b1 != b3, hasTop = b2 != b3;

            Vector2 eBot = default, eLeft = default, eRight = default, eTop = default;
            float hBot = 0, hLeft = 0, hRight = 0, hTop = 0;

            if (hasBot) { float t = Mathf.Clamp01(d0 / (d0 - d1)); eBot = new Vector2(wx0 + (wx1 - wx0) * t, wz0 + (wz1 - wz0) * t); hBot = h0 + (h1 - h0) * t; }
            if (hasLeft) { float t = Mathf.Clamp01(d0 / (d0 - d2)); eLeft = new Vector2(wx0 + (wx2 - wx0) * t, wz0 + (wz2 - wz0) * t); hLeft = h0 + (h2 - h0) * t; }
            if (hasRight) { float t = Mathf.Clamp01(d1 / (d1 - d3)); eRight = new Vector2(wx1 + (wx3 - wx1) * t, wz1 + (wz3 - wz1) * t); hRight = h1 + (h3 - h1) * t; }
            if (hasTop) { float t = Mathf.Clamp01(d2 / (d2 - d3)); eTop = new Vector2(wx2 + (wx3 - wx2) * t, wz2 + (wz3 - wz2) * t); hTop = h2 + (h3 - h2) * t; }

            var crossings = new List<Vector2>(4);
            if (hasBot) crossings.Add(eBot);
            if (hasRight) crossings.Add(eRight);
            if (hasTop) crossings.Add(eTop);
            if (hasLeft) crossings.Add(eLeft);

            var polyX = new List<float>(6); var polyZ = new List<float>(6);
            var polyH = new List<float>(6); var polyD = new List<float>(6);

            if (b0) { polyX.Add(wx0); polyZ.Add(wz0); polyH.Add(h0); polyD.Add(d0); }
            if (hasBot) { polyX.Add(eBot.x); polyZ.Add(eBot.y); polyH.Add(hBot); polyD.Add(0f); }
            if (b1) { polyX.Add(wx1); polyZ.Add(wz1); polyH.Add(h1); polyD.Add(d1); }
            if (hasRight) { polyX.Add(eRight.x); polyZ.Add(eRight.y); polyH.Add(hRight); polyD.Add(0f); }
            if (b3) { polyX.Add(wx3); polyZ.Add(wz3); polyH.Add(h3); polyD.Add(d3); }
            if (hasTop) { polyX.Add(eTop.x); polyZ.Add(eTop.y); polyH.Add(hTop); polyD.Add(0f); }
            if (b2) { polyX.Add(wx2); polyZ.Add(wz2); polyH.Add(h2); polyD.Add(d2); }
            if (hasLeft) { polyX.Add(eLeft.x); polyZ.Add(eLeft.y); polyH.Add(hLeft); polyD.Add(0f); }

            if (polyX.Count < 3) return;

            if (crossings.Count >= 2)
            {
                float segAx = crossings[0].x, segAz = crossings[0].y;
                float segBx = crossings[1].x, segBz = crossings[1].y;
                for (int p = 0; p < polyX.Count; p++)
                {
                    float pd = polyD[p];
                    if (pd <= 0f) continue;
                    if (pd < IceMeshUtility.EDGE_BEVEL_WIDTH)
                    {
                        float blend = pd / IceMeshUtility.EDGE_BEVEL_WIDTH;
                        blend = blend * blend;
                        Vector2 proj = IceMeshUtility.ProjectOntoSegment(polyX[p], polyZ[p], segAx, segAz, segBx, segBz);
                        polyX[p] = Mathf.Lerp(proj.x, polyX[p], blend);
                        polyZ[p] = Mathf.Lerp(proj.y, polyZ[p], blend);
                    }
                }
            }

            int fv = verts.Count;
            float bevel0 = -IceMeshUtility.ComputeBevel(polyD[0]);
            verts.Add(new Vector3(polyX[0] - myPos.x, bevel0 + polyH[0], polyZ[0] - myPos.z));
            uvs.Add(new Vector2(polyX[0] * 0.1f, polyZ[0] * 0.1f));
            norms.Add(Vector3.up);

            float bevel1 = -IceMeshUtility.ComputeBevel(polyD[1]);
            int prevV = verts.Count;
            verts.Add(new Vector3(polyX[1] - myPos.x, bevel1 + polyH[1], polyZ[1] - myPos.z));
            uvs.Add(new Vector2(polyX[1] * 0.1f, polyZ[1] * 0.1f));
            norms.Add(Vector3.up);

            for (int p = 2; p < polyX.Count; p++)
            {
                float bevelP = -IceMeshUtility.ComputeBevel(polyD[p]);
                int curV = verts.Count;
                verts.Add(new Vector3(polyX[p] - myPos.x, bevelP + polyH[p], polyZ[p] - myPos.z));
                uvs.Add(new Vector2(polyX[p] * 0.1f, polyZ[p] * 0.1f));
                norms.Add(Vector3.up);
                tris.Add(fv); tris.Add(prevV); tris.Add(curV);
                prevV = curV;
            }
        }

        private void BuildWalkColliderMesh(float[] edgeDist, bool[] inHole, Vector3 myPos)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var norms = new List<Vector3>();

            for (int gz = 0; gz < _gridHeight - 1; gz++)
            {
                for (int gx = 0; gx < _gridWidth - 1; gx++)
                {
                    int i0 = gz * _gridWidth + gx, i1 = i0 + 1;
                    int i2 = (gz + 1) * _gridWidth + gx, i3 = i2 + 1;

                    float d0 = edgeDist[i0], d1 = edgeDist[i1];
                    float d2 = edgeDist[i2], d3 = edgeDist[i3];

                    bool b0 = d0 > 0f, b1 = d1 > 0f, b2 = d2 > 0f, b3 = d3 > 0f;
                    int caseIndex = (b0 ? 1 : 0) | (b1 ? 2 : 0) | (b2 ? 4 : 0) | (b3 ? 8 : 0);
                    if (caseIndex == 0) continue;

                    float wx0 = _boundsMinX + gx * CELL_SIZE, wz0 = _boundsMinZ + gz * CELL_SIZE;
                    float wx1 = wx0 + CELL_SIZE, wz1 = wz0;
                    float wx2 = wx0, wz2 = wz0 + CELL_SIZE;
                    float wx3 = wx0 + CELL_SIZE, wz3 = wz0 + CELL_SIZE;

                    float h0 = _heights[i0] > -50f ? _heights[i0] : 0f;
                    float h1 = _heights[i1] > -50f ? _heights[i1] : 0f;
                    float h2 = _heights[i2] > -50f ? _heights[i2] : 0f;
                    float h3 = _heights[i3] > -50f ? _heights[i3] : 0f;

                    if (caseIndex == 15)
                    {
                        if (inHole[i0] || inHole[i1] || inHole[i2] || inHole[i3])
                            continue;

                        float fwx0 = wx0, fwz0 = wz0;
                        float fwx1 = wx1, fwz1 = wz1;
                        float fwx2 = wx2, fwz2 = wz2;
                        float fwx3 = wx3, fwz3 = wz3;

                        float minD = Mathf.Min(d0, Mathf.Min(d1, Mathf.Min(d2, d3)));
                        if (minD < IceMeshUtility.EDGE_BEVEL_WIDTH)
                        {
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx0, ref fwz0, d0, wx0, wz0);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx1, ref fwz1, d1, wx1, wz1);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx2, ref fwz2, d2, wx2, wz2);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx3, ref fwz3, d3, wx3, wz3);
                        }

                        int v0i = verts.Count;
                        verts.Add(new Vector3(fwx0 - myPos.x, h0, fwz0 - myPos.z));
                        verts.Add(new Vector3(fwx1 - myPos.x, h1, fwz1 - myPos.z));
                        verts.Add(new Vector3(fwx2 - myPos.x, h2, fwz2 - myPos.z));
                        verts.Add(new Vector3(fwx3 - myPos.x, h3, fwz3 - myPos.z));
                        norms.Add(Vector3.up); norms.Add(Vector3.up);
                        norms.Add(Vector3.up); norms.Add(Vector3.up);
                        tris.Add(v0i); tris.Add(v0i + 2); tris.Add(v0i + 1);
                        tris.Add(v0i + 1); tris.Add(v0i + 2); tris.Add(v0i + 3);
                        continue;
                    }

                    // Boundary collider quad (same as visual but no UVs, uses heights)
                    EmitColliderBoundaryQuad(verts, tris, norms,
                        wx0, wz0, d0, h0, wx1, wz1, d1, h1,
                        wx2, wz2, d2, h2, wx3, wz3, d3, h3,
                        myPos, b0, b1, b2, b3);
                }
            }

            _walkColliderMesh.Clear();
            if (verts.Count > 0 && tris.Count > 0)
            {
                _walkColliderMesh.SetVertices(verts);
                _walkColliderMesh.SetTriangles(tris, 0);
                _walkColliderMesh.SetNormals(norms);
                _walkColliderMesh.RecalculateBounds();
            }

            if (_walkCollider != null)
            {
                _walkCollider.sharedMesh = null;
                _walkCollider.sharedMesh = _walkColliderMesh;
            }
        }

        private void EmitColliderBoundaryQuad(
            List<Vector3> verts, List<int> tris, List<Vector3> norms,
            float wx0, float wz0, float d0, float h0,
            float wx1, float wz1, float d1, float h1,
            float wx2, float wz2, float d2, float h2,
            float wx3, float wz3, float d3, float h3,
            Vector3 myPos, bool b0, bool b1, bool b2, bool b3)
        {
            bool hasBot = b0 != b1, hasLeft = b0 != b2, hasRight = b1 != b3, hasTop = b2 != b3;

            Vector2 eBot = default, eLeft = default, eRight = default, eTop = default;
            float hBot = 0, hLeft = 0, hRight = 0, hTop = 0;

            if (hasBot) { float t = Mathf.Clamp01(d0 / (d0 - d1)); eBot = new Vector2(wx0 + (wx1 - wx0) * t, wz0 + (wz1 - wz0) * t); hBot = h0 + (h1 - h0) * t; }
            if (hasLeft) { float t = Mathf.Clamp01(d0 / (d0 - d2)); eLeft = new Vector2(wx0 + (wx2 - wx0) * t, wz0 + (wz2 - wz0) * t); hLeft = h0 + (h2 - h0) * t; }
            if (hasRight) { float t = Mathf.Clamp01(d1 / (d1 - d3)); eRight = new Vector2(wx1 + (wx3 - wx1) * t, wz1 + (wz3 - wz1) * t); hRight = h1 + (h3 - h1) * t; }
            if (hasTop) { float t = Mathf.Clamp01(d2 / (d2 - d3)); eTop = new Vector2(wx2 + (wx3 - wx2) * t, wz2 + (wz3 - wz2) * t); hTop = h2 + (h3 - h2) * t; }

            var crossings = new List<Vector2>(4);
            if (hasBot) crossings.Add(eBot);
            if (hasRight) crossings.Add(eRight);
            if (hasTop) crossings.Add(eTop);
            if (hasLeft) crossings.Add(eLeft);

            var pX = new List<float>(6); var pZ = new List<float>(6);
            var pH = new List<float>(6); var pD = new List<float>(6);

            if (b0) { pX.Add(wx0); pZ.Add(wz0); pH.Add(h0); pD.Add(d0); }
            if (hasBot) { pX.Add(eBot.x); pZ.Add(eBot.y); pH.Add(hBot); pD.Add(0f); }
            if (b1) { pX.Add(wx1); pZ.Add(wz1); pH.Add(h1); pD.Add(d1); }
            if (hasRight) { pX.Add(eRight.x); pZ.Add(eRight.y); pH.Add(hRight); pD.Add(0f); }
            if (b3) { pX.Add(wx3); pZ.Add(wz3); pH.Add(h3); pD.Add(d3); }
            if (hasTop) { pX.Add(eTop.x); pZ.Add(eTop.y); pH.Add(hTop); pD.Add(0f); }
            if (b2) { pX.Add(wx2); pZ.Add(wz2); pH.Add(h2); pD.Add(d2); }
            if (hasLeft) { pX.Add(eLeft.x); pZ.Add(eLeft.y); pH.Add(hLeft); pD.Add(0f); }
            if (pX.Count < 3) return;

            if (crossings.Count >= 2)
            {
                float segAx = crossings[0].x, segAz = crossings[0].y;
                float segBx = crossings[1].x, segBz = crossings[1].y;
                for (int p = 0; p < pX.Count; p++)
                {
                    float pd = pD[p];
                    if (pd <= 0f) continue;
                    if (pd < IceMeshUtility.EDGE_BEVEL_WIDTH)
                    {
                        float blend = pd / IceMeshUtility.EDGE_BEVEL_WIDTH;
                        blend = blend * blend;
                        Vector2 proj = IceMeshUtility.ProjectOntoSegment(pX[p], pZ[p], segAx, segAz, segBx, segBz);
                        pX[p] = Mathf.Lerp(proj.x, pX[p], blend);
                        pZ[p] = Mathf.Lerp(proj.y, pZ[p], blend);
                    }
                }
            }

            int fv = verts.Count;
            verts.Add(new Vector3(pX[0] - myPos.x, pH[0], pZ[0] - myPos.z)); norms.Add(Vector3.up);
            int prev = verts.Count;
            verts.Add(new Vector3(pX[1] - myPos.x, pH[1], pZ[1] - myPos.z)); norms.Add(Vector3.up);
            for (int p = 2; p < pX.Count; p++)
            {
                int cur = verts.Count;
                verts.Add(new Vector3(pX[p] - myPos.x, pH[p], pZ[p] - myPos.z)); norms.Add(Vector3.up);
                tris.Add(fv); tris.Add(prev); tris.Add(cur);
                prev = cur;
            }
        }

        private void BuildBottomMesh(float[] edgeDist, Vector3 myPos)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();
            var norms = new List<Vector3>();

            // Compute the max edge distance for center-weight normalization
            float maxEdgeDist = 0f;
            for (int i = 0; i < edgeDist.Length; i++)
            {
                if (edgeDist[i] > maxEdgeDist) maxEdgeDist = edgeDist[i];
            }
            if (maxEdgeDist < 0.1f) maxEdgeDist = 1f;

            // Bottom face: iceberg shape drops deep at center, shallow at edges
            // Uses flipped winding (normals face down)
            for (int gz = 0; gz < _gridHeight - 1; gz++)
            {
                for (int gx = 0; gx < _gridWidth - 1; gx++)
                {
                    int i0 = gz * _gridWidth + gx, i1 = i0 + 1;
                    int i2 = (gz + 1) * _gridWidth + gx, i3 = i2 + 1;

                    float d0 = edgeDist[i0], d1 = edgeDist[i1];
                    float d2 = edgeDist[i2], d3 = edgeDist[i3];

                    bool b0 = d0 > 0f, b1 = d1 > 0f, b2 = d2 > 0f, b3 = d3 > 0f;
                    int caseIndex = (b0 ? 1 : 0) | (b1 ? 2 : 0) | (b2 ? 4 : 0) | (b3 ? 8 : 0);
                    if (caseIndex == 0) continue;

                    float wx0 = _boundsMinX + gx * CELL_SIZE, wz0 = _boundsMinZ + gz * CELL_SIZE;
                    float wx1 = wx0 + CELL_SIZE, wz1 = wz0;
                    float wx2 = wx0, wz2 = wz0 + CELL_SIZE;
                    float wx3 = wx0 + CELL_SIZE, wz3 = wz0 + CELL_SIZE;

                    // Compute bottom Y for each corner based on centerWeight
                    float by0 = ComputeIcebergBottomY(d0, maxEdgeDist);
                    float by1 = ComputeIcebergBottomY(d1, maxEdgeDist);
                    float by2 = ComputeIcebergBottomY(d2, maxEdgeDist);
                    float by3 = ComputeIcebergBottomY(d3, maxEdgeDist);

                    // Displaced corners for edge smoothing
                    float fwx0 = wx0, fwz0 = wz0;
                    float fwx1 = wx1, fwz1 = wz1;
                    float fwx2 = wx2, fwz2 = wz2;
                    float fwx3 = wx3, fwz3 = wz3;

                    if (caseIndex == 15)
                    {
                        float minD = Mathf.Min(d0, Mathf.Min(d1, Mathf.Min(d2, d3)));
                        if (minD < IceMeshUtility.EDGE_BEVEL_WIDTH)
                        {
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx0, ref fwz0, d0, wx0, wz0);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx1, ref fwz1, d1, wx1, wz1);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx2, ref fwz2, d2, wx2, wz2);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx3, ref fwz3, d3, wx3, wz3);
                        }

                        // Flipped winding for downward-facing
                        int v0 = verts.Count;
                        verts.Add(new Vector3(fwx0 - myPos.x, by0, fwz0 - myPos.z));
                        verts.Add(new Vector3(fwx1 - myPos.x, by1, fwz1 - myPos.z));
                        verts.Add(new Vector3(fwx2 - myPos.x, by2, fwz2 - myPos.z));
                        verts.Add(new Vector3(fwx3 - myPos.x, by3, fwz3 - myPos.z));
                        uvs.Add(new Vector2(wx0 * 0.1f, wz0 * 0.1f));
                        uvs.Add(new Vector2(wx1 * 0.1f, wz1 * 0.1f));
                        uvs.Add(new Vector2(wx2 * 0.1f, wz2 * 0.1f));
                        uvs.Add(new Vector2(wx3 * 0.1f, wz3 * 0.1f));
                        norms.Add(Vector3.down); norms.Add(Vector3.down);
                        norms.Add(Vector3.down); norms.Add(Vector3.down);
                        tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2);
                        tris.Add(v0 + 1); tris.Add(v0 + 3); tris.Add(v0 + 2);
                        continue;
                    }

                    // Boundary bottom - use flipped emitter with iceberg depth
                    Func<float, float> botYFunc = (ed) => ComputeIcebergBottomY(ed, maxEdgeDist);
                    IceMeshUtility.EmitMarchingSquaresQuadFlipped(verts, tris, uvs, norms,
                        wx0, wz0, d0, wx1, wz1, d1, wx2, wz2, d2, wx3, wz3, d3,
                        myPos.x, myPos.z, botYFunc, Vector3.down,
                        null, 0, 0, 0, 0, false);
                }
            }

            _bottomMesh.Clear();
            if (verts.Count > 0 && tris.Count > 0)
            {
                _bottomMesh.SetVertices(verts);
                _bottomMesh.SetTriangles(tris, 0);
                _bottomMesh.SetUVs(0, uvs);
                _bottomMesh.SetNormals(norms);
                _bottomMesh.RecalculateBounds();
            }
        }

        private float ComputeIcebergBottomY(float edgeDist, float maxEdgeDist)
        {
            if (edgeDist <= 0f) return -ICE_THICKNESS;
            float centerWeight = Mathf.Clamp01(edgeDist / maxEdgeDist);
            centerWeight = centerWeight * centerWeight * (3f - 2f * centerWeight); // smoothstep
            return -ICE_THICKNESS - (centerWeight * ICEBERG_DEPTH);
        }

        private void BuildWaterUnderMesh(float[] edgeDist, Vector3 myPos)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();
            var norms = new List<Vector3>();

            Func<float, float> waterY = (ed) => -ICE_THICKNESS - 0.01f;

            for (int gz = 0; gz < _gridHeight - 1; gz++)
            {
                for (int gx = 0; gx < _gridWidth - 1; gx++)
                {
                    int i0 = gz * _gridWidth + gx, i1 = i0 + 1;
                    int i2 = (gz + 1) * _gridWidth + gx, i3 = i2 + 1;

                    float wx0 = _boundsMinX + gx * CELL_SIZE, wz0 = _boundsMinZ + gz * CELL_SIZE;
                    float wx1 = wx0 + CELL_SIZE, wz1 = wz0;
                    float wx2 = wx0, wz2 = wz0 + CELL_SIZE;
                    float wx3 = wx0 + CELL_SIZE, wz3 = wz0 + CELL_SIZE;

                    float d0 = edgeDist[i0], d1 = edgeDist[i1];
                    float d2 = edgeDist[i2], d3 = edgeDist[i3];

                    float fwx0 = wx0, fwz0 = wz0;
                    float fwx1 = wx1, fwz1 = wz1;
                    float fwx2 = wx2, fwz2 = wz2;
                    float fwx3 = wx3, fwz3 = wz3;

                    bool wb0 = d0 > 0f, wb1 = d1 > 0f, wb2 = d2 > 0f, wb3 = d3 > 0f;
                    int wCase = (wb0 ? 1 : 0) | (wb1 ? 2 : 0) | (wb2 ? 4 : 0) | (wb3 ? 8 : 0);
                    if (wCase == 15)
                    {
                        float minD = Mathf.Min(d0, Mathf.Min(d1, Mathf.Min(d2, d3)));
                        if (minD < IceMeshUtility.EDGE_BEVEL_WIDTH)
                        {
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx0, ref fwz0, d0, wx0, wz0);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx1, ref fwz1, d1, wx1, wz1);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx2, ref fwz2, d2, wx2, wz2);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx3, ref fwz3, d3, wx3, wz3);
                        }
                    }

                    IceMeshUtility.EmitMarchingSquaresQuad(verts, tris, uvs, norms,
                        fwx0, fwz0, d0, fwx1, fwz1, d1, fwx2, fwz2, d2, fwx3, fwz3, d3,
                        myPos.x, myPos.z, waterY, Vector3.up,
                        null, 0, 0, 0, 0, false);
                }
            }

            _waterMesh.Clear();
            if (verts.Count > 0 && tris.Count > 0)
            {
                _waterMesh.SetVertices(verts);
                _waterMesh.SetTriangles(tris, 0);
                _waterMesh.SetUVs(0, uvs);
                _waterMesh.SetNormals(norms);
                _waterMesh.RecalculateBounds();
            }
        }

        #endregion

        #region Hole Walls & Edge Walls

        private void DestroyHoleWalls()
        {
            for (int i = 0; i < _holeWalls.Count; i++)
                if (_holeWalls[i] != null) Destroy(_holeWalls[i]);
            _holeWalls.Clear();
        }

        private void DestroyEdgeWalls()
        {
            for (int i = 0; i < _edgeWalls.Count; i++)
                if (_edgeWalls[i] != null) Destroy(_edgeWalls[i]);
            _edgeWalls.Clear();
        }

        private void RebuildHoleWalls(float iceY)
        {
            DestroyHoleWalls();
            if (_holes.Count == 0) return;

            for (int h = 0; h < _holes.Count; h++)
            {
                var hole = _holes[h];
                var wallContainer = new GameObject($"IceHoleWall_{h}");
                wallContainer.transform.SetParent(transform);
                wallContainer.transform.position = new Vector3(hole.Center.x, iceY, hole.Center.z);
                wallContainer.layer = LayerMask.NameToLayer("piece");

                var climbPoint = wallContainer.AddComponent<IceHoleClimbPoint>();
                climbPoint.Setup(iceY, hole.Center, hole.Radius);

                float angleStep = 360f / HOLE_WALL_SEGMENTS;
                float chordLength = 2f * hole.Radius * Mathf.Sin(Mathf.PI / HOLE_WALL_SEGMENTS);

                for (int s = 0; s < HOLE_WALL_SEGMENTS; s++)
                {
                    float midAngleRad = ((s * angleStep) + angleStep * 0.5f) * Mathf.Deg2Rad;
                    float wx = hole.Center.x + Mathf.Cos(midAngleRad) * hole.Radius;
                    float wz = hole.Center.z + Mathf.Sin(midAngleRad) * hole.Radius;
                    float wallTopY = iceY - HOLE_WALL_TOP_INSET;
                    float wy = wallTopY - HOLE_WALL_DEPTH * 0.5f;

                    var wallSegGO = new GameObject($"WallSeg_{s}");
                    wallSegGO.transform.SetParent(wallContainer.transform);
                    wallSegGO.transform.position = new Vector3(wx, wy, wz);
                    float tangentAngle = midAngleRad + Mathf.PI * 0.5f;
                    wallSegGO.transform.rotation = Quaternion.Euler(0f, -tangentAngle * Mathf.Rad2Deg, 0f);
                    wallSegGO.layer = LayerMask.NameToLayer("piece");

                    var box = wallSegGO.AddComponent<BoxCollider>();
                    box.size = new Vector3(chordLength, HOLE_WALL_DEPTH, HOLE_WALL_THICKNESS);
                    box.center = Vector3.zero;
                }
                _holeWalls.Add(wallContainer);
            }
        }

        private void RebuildEdgeWalls(float iceY)
        {
            DestroyEdgeWalls();

            float cx = (_boundsMinX + _boundsMaxX) * 0.5f;
            float cz = (_boundsMinZ + _boundsMaxZ) * 0.5f;
            float approxR = Mathf.Max(_boundsMaxX - _boundsMinX, _boundsMaxZ - _boundsMinZ) * 0.5f;

            var edgeContainer = new GameObject("IceEdgeWalls");
            edgeContainer.transform.SetParent(transform);
            edgeContainer.transform.position = new Vector3(cx, iceY, cz);
            edgeContainer.layer = LayerMask.NameToLayer("piece");

            var edgeClimb = edgeContainer.AddComponent<IceHoleClimbPoint>();
            edgeClimb.Setup(iceY, new Vector3(cx, 0f, cz), approxR + 1f);

            foreach (var fp in _footprints)
            {
                int segments = Mathf.Max(8, Mathf.CeilToInt(fp.Radius * 2f));
                float angleStep = 360f / segments;
                float chordLen = 2f * fp.Radius * Mathf.Sin(Mathf.PI / segments);
                float edgeWallHeight = 1.5f;
                float edgeWallCenterY = (iceY - 0.05f) - edgeWallHeight * 0.5f;

                for (int s = 0; s < segments; s++)
                {
                    float midRad = ((s * angleStep) + angleStep * 0.5f) * Mathf.Deg2Rad;
                    float ewx = fp.Center.x + Mathf.Cos(midRad) * fp.Radius;
                    float ewz = fp.Center.z + Mathf.Sin(midRad) * fp.Radius;

                    if (IceMeshUtility.IsInsideAnyFootprintExcept(_footprints, ewx, ewz, fp))
                        continue;

                    var wallGO = new GameObject($"EdgeWall");
                    wallGO.transform.SetParent(edgeContainer.transform);
                    wallGO.transform.position = new Vector3(ewx, edgeWallCenterY, ewz);
                    float tangent = midRad + Mathf.PI * 0.5f;
                    wallGO.transform.rotation = Quaternion.Euler(0f, -tangent * Mathf.Rad2Deg, 0f);
                    wallGO.layer = LayerMask.NameToLayer("piece");

                    var box = wallGO.AddComponent<BoxCollider>();
                    box.size = new Vector3(chordLen, edgeWallHeight, HOLE_WALL_THICKNESS);
                    box.center = Vector3.zero;
                }
            }

            _edgeWalls.Add(edgeContainer);
        }

        #endregion

        #region ZDO Persistence

        private void SaveToZDO()
        {
            var nview = gameObject.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            var zdo = nview.GetZDO();
            if (zdo == null) return;

            zdo.Set(s_isFloatIceHash, true);
            zdo.Set(s_floatIceNoiseSeedHash, _noiseSeed);
            zdo.Set(s_floatIceWaterYHash, IceSurfaceY);
            zdo.Set(s_floatIceFootprintsHash, IceMeshUtility.PackFootprints(_footprints));

            byte[] gridParams = new byte[6 * 4];
            Buffer.BlockCopy(BitConverter.GetBytes(_gridWidth), 0, gridParams, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_gridHeight), 0, gridParams, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_boundsMinX), 0, gridParams, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_boundsMinZ), 0, gridParams, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_boundsMaxX), 0, gridParams, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_boundsMaxZ), 0, gridParams, 20, 4);
            zdo.Set(s_floatIceGridHash, gridParams);

            if (_heights != null && _heights.Length > 0)
            {
                byte[] heightData = new byte[_heights.Length * 4];
                Buffer.BlockCopy(_heights, 0, heightData, 0, heightData.Length);
                zdo.Set(s_floatIceHeightsHash, heightData);
            }
        }

        private bool LoadFromZDO()
        {
            var nview = gameObject.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            var zdo = nview.GetZDO();
            if (zdo == null) return false;

            if (!zdo.GetBool(s_isFloatIceHash, false)) return false;

            byte[] fpData = zdo.GetByteArray(s_floatIceFootprintsHash);
            _footprints = IceMeshUtility.UnpackFootprints(fpData);
            if (_footprints.Count == 0) return false;

            _noiseSeed = zdo.GetFloat(s_floatIceNoiseSeedHash, (s_instanceCounter++) * 137.5f);

            byte[] gridParams = zdo.GetByteArray(s_floatIceGridHash);
            if (gridParams != null && gridParams.Length >= 24)
            {
                _gridWidth = BitConverter.ToInt32(gridParams, 0);
                _gridHeight = BitConverter.ToInt32(gridParams, 4);
                _boundsMinX = BitConverter.ToSingle(gridParams, 8);
                _boundsMinZ = BitConverter.ToSingle(gridParams, 12);
                _boundsMaxX = BitConverter.ToSingle(gridParams, 16);
                _boundsMaxZ = BitConverter.ToSingle(gridParams, 20);
            }
            else
            {
                RecalcBounds();
                _gridWidth = Mathf.CeilToInt((_boundsMaxX - _boundsMinX) / CELL_SIZE) + 2;
                _gridHeight = Mathf.CeilToInt((_boundsMaxZ - _boundsMinZ) / CELL_SIZE) + 2;
            }

            byte[] heightData = zdo.GetByteArray(s_floatIceHeightsHash);
            if (heightData != null && heightData.Length == _gridWidth * _gridHeight * 4)
            {
                _heights = new float[_gridWidth * _gridHeight];
                Buffer.BlockCopy(heightData, 0, _heights, 0, heightData.Length);
            }
            else
            {
                InitializeHeightGrid();
            }

            StripOriginalIcePrefabVisuals();

            _iceMaterial = WaterMaterialManager.GetMaterial(CustomLiquidType.Ice);
            _waterMaterial = WaterMaterialManager.GetMaterial(CustomLiquidType.Water);

            float waterY = SampleWaterLevel(transform.position);
            if (waterY > -999f)
                IceSurfaceY = waterY + ICE_SURFACE_OFFSET;
            else
                IceSurfaceY = zdo.GetFloat(s_floatIceWaterYHash, transform.position.y);

            CreateGameObjects();
            SetupFloatingPhysics();
            RebuildMesh();

            if (!s_allFloatingIce.Contains(this))
                s_allFloatingIce.Add(this);

            return true;
        }

        private void StripOriginalIcePrefabVisuals()
        {
            // Set root to "piece" layer so IH placement ruler works over ice
            int pieceLayer = LayerMask.NameToLayer("piece");
            if (pieceLayer >= 0) gameObject.layer = pieceLayer;

            var renderers = gameObject.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var r in renderers)
                if (r != null) r.enabled = false;

            var lodGroup = gameObject.GetComponent<LODGroup>();
            if (lodGroup != null) lodGroup.enabled = false;

            var existingColliders = gameObject.GetComponentsInChildren<MeshCollider>(true);
            foreach (var c in existingColliders)
                if (c != null) c.enabled = false;

            // Destroy Piece component if present - prevents IH from
            // treating existing ice as a "piece" that blocks placement.
            var piece = gameObject.GetComponent<Piece>();
            if (piece != null)
                Destroy(piece);

            // Strip Hoverable/Interactable from the vanilla prefab
            // to prevent "[E] Climb" or other interaction prompts
            foreach (var comp in gameObject.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp == null) continue;
                if (comp is Hoverable || comp is Interactable)
                {
                    Destroy(comp);
                }
            }
        }

        #endregion

        #region Helpers

        private float SampleWaterLevel(Vector3 pos)
        {
            float bestLevel = -1000f;
            if (WaterVolume.Instances != null)
            {
                foreach (var wv in WaterVolume.Instances)
                {
                    if (wv == null) continue;
                    float wl = wv.GetWaterSurface(pos, 1f);
                    if (wl > bestLevel) bestLevel = wl;
                }
            }
            var combiner = WaterMeshCombiner.GetCombinerAt(pos);
            if (combiner != null)
            {
                float customLevel = combiner.GetWaterSurfaceAt(pos);
                if (customLevel > float.MinValue && customLevel > bestLevel)
                    bestLevel = customLevel;
            }
            return bestLevel;
        }

        #endregion
    }
}
