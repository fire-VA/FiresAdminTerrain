using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Water.Ice;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Land-based ice controller - terrain-conforming ice overlay.
    /// 
    /// TERRAIN-CONFORMING BEHAVIOR:
    /// - At zero offset: Ice conforms exactly to terrain shape (like vertex paint)
    /// - With height offset: Core of disk is a flat elevated plane with noise
    /// - Overlapping disks: the HIGHEST disk wins at each vertex
    ///   Lower disks are never pulled upward.
    /// 
    /// DUAL EDGE BEHAVIOR (raised disks):
    /// - OVERLAP EDGES: Where a raised disk's blend zone reaches another
    ///   footprint, the height smoothly slopes from the raised surface
    ///   down to the lower disk's height. No cliff, no double wall.
    /// - NON-OVERLAP EDGES: Where no other footprint exists, the mesh
    ///   boundary generates a vertical skirt that drops down to terrain
    ///   height (plus a small penetration depth) at each boundary point,
    ///   guaranteeing intersection with terrain below.
    /// 
    /// Uses a height grid with terrain sampling at each vertex:
    /// - Each grid vertex samples ZoneSystem.GetGroundHeight for the terrain Y
    /// - Each footprint computes its desired height; the highest one wins
    /// - Where disks overlap, the highest desired height is used
    /// - Supports raise/lower/level/smooth height modifications via Infinity Hammer
    /// - Persists to anchor ZDO (ice1 prefab clone)
    /// - Slippery physics via IceSlidingPatches
    /// - No floating, no holes, no climb-out handler
    /// </summary>
    public class LandIceController : MonoBehaviour
    {
        #region Constants

        private const float CELL_SIZE = 0.25f;
        private const float BLEND_DISTANCE = 2.0f;
        private const float GRID_PADDING = 2.5f;
        private const float ICE_TERRAIN_OFFSET = 0.10f;

        /// <summary>
        /// How far below terrain the skirt extends to guarantee
        /// full coverage and intersection with the ground.
        /// </summary>
        private const float SKIRT_TERRAIN_PENETRATION = 1.0f;

        /// <summary>
        /// Distance beyond footprint radius where overlap-slope blending occurs.
        /// </summary>
        private const float SLOPE_DISTANCE = 3.0f;

        #endregion

        #region ZDO Keys (LEGACY — kept for ZdoHasLandIceData migration check)

        // 2026-05-26 round-26 plan §1.2: persistence moved to LandIceAnchor
        // with new `va_landice_*` keys. These legacy keys remain only for
        // detecting orphan ice1 ZDOs from pre-migration saves so a cleanup
        // pass can skip them. The controller no longer reads/writes these.
        private static readonly int s_isLandIceHash = "va_land_ice".GetStableHashCode();

        #endregion

        #region State

        private List<IceDiskFootprint> _footprints = new List<IceDiskFootprint>();
        private float _noiseSeed;
        private static int s_instanceCounter = 0;
        private bool _initialized = false;

        // 2026-05-26 round-26 plan §1.2 — persistence anchor.
        // Bound by LandIceAnchor.SpawnNew at placement time, or by
        // LandIceAnchor.Awake when the world reloads a persisted VALandIce.
        private LandIceAnchor _anchor;

        // Height grid
        private float[] _heights;
        private int _gridWidth;
        private int _gridHeight;
        private float _boundsMinX, _boundsMaxX, _boundsMinZ, _boundsMaxZ;

        // Mesh components
        private GameObject _iceGO;
        private MeshFilter _iceMeshFilter;
        private MeshRenderer _iceRenderer;
        private MeshCollider _iceCollider;
        private Mesh _iceMesh;
        private Mesh _colliderMesh;
        private Material _iceMaterial;

        /// <summary>The Y level used as the base surface position.</summary>
        public float IceSurfaceY { get; private set; }

        #endregion

        #region Static Registry

        private static List<LandIceController> s_allLandIce = new List<LandIceController>();
        public static IReadOnlyList<LandIceController> AllLandIce => s_allLandIce;

        #endregion

        #region Properties

        public IReadOnlyList<IceDiskFootprint> Footprints => _footprints;

        #endregion

        #region Public API - Initialization

        /// <summary>
        /// Initializes a terrain overlay ice surface with an initial disk footprint.
        /// Samples terrain height at each grid vertex and stores in the height grid.
        /// The footprint Center.y is the DESIRED ice surface Y (terrain + offset from IH).
        /// </summary>
        public void Initialize(Vector3 center, float radius)
        {
            _noiseSeed = (s_instanceCounter++) * 137.5f;
            IceSurfaceY = center.y;
            _iceMaterial = WaterMaterialManager.GetMaterial(CustomLiquidType.Ice);

            transform.position = center;
            StripOriginalIcePrefabVisuals();
            DisableFloatingPhysics();

            _footprints.Add(new IceDiskFootprint { Center = center, Radius = radius });
            RecalcBounds();
            InitializeHeightGrid();

            CreateGameObjects();
            RebuildMesh();

            _initialized = true;
            if (!s_allLandIce.Contains(this))
                s_allLandIce.Add(this);

            SaveToZDO();
            FiresLogger.LogInfo($"[LandIceController] Initialized: r={radius}, Y={IceSurfaceY:F2}");
        }

        #endregion

        #region Public API - Footprints

        /// <summary>
        /// Adds a new circular footprint and rebuilds the mesh.
        /// The footprint Center.y is the desired ice surface Y for the new disk.
        /// Overlapping regions will use the highest contributing disk height.
        /// </summary>
        public void AddFootprint(Vector3 center, float radius)
        {
            _footprints.Add(new IceDiskFootprint { Center = center, Radius = radius });
            ExpandHeightGrid();
            RebuildMesh();

            SaveToZDO();
            FiresLogger.LogInfo($"[LandIceController] Added footprint at {center}, r={radius}, total={_footprints.Count}");
        }

        /// <summary>
        /// Checks if a disk at (center, radius) overlaps with any footprint.
        /// </summary>
        public bool OverlapsWithDisk(Vector3 center, float radius)
        {
            return IceMeshUtility.OverlapsWithAnyFootprint(_footprints, center, radius);
        }

        /// <summary>
        /// Checks if a world position is inside any footprint.
        /// </summary>
        public bool ContainsPosition(Vector3 worldPos)
        {
            return IceMeshUtility.IsInsideAnyFootprint(_footprints, worldPos.x, worldPos.z);
        }

        #endregion

        #region Public API - Height Modification

        /// <summary>
        /// Modifies heights directly in the height grid.
        /// </summary>
        public void ModifyHeights(Vector3 worldPos, float delta, float radius)
        {
            if (_heights == null || _heights.Length == 0) return;

            bool modified = false;
            Vector3 myPos = transform.position;

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

        /// <summary>
        /// Levels heights to a target within radius.
        /// </summary>
        public void LevelHeights(Vector3 worldPos, float radius, float targetHeight)
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
                        t = t * t * (3f - 2f * t);
                        _heights[idx] = Mathf.Lerp(_heights[idx], targetHeight, t);
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

        /// <summary>
        /// Smooths heights within radius.
        /// </summary>
        public void SmoothHeights(Vector3 worldPos, float radius, int passes = 3)
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

        #region Public API - Queries & Removal

        public static LandIceController GetLandIceAt(Vector3 worldPos)
        {
            s_allLandIce.RemoveAll(c => c == null);
            foreach (var ctrl in s_allLandIce)
            {
                if (ctrl != null && ctrl.ContainsPosition(worldPos))
                    return ctrl;
            }
            return null;
        }

        public static List<LandIceController> GetLandIceInRadius(Vector3 center, float radius)
        {
            s_allLandIce.RemoveAll(c => c == null);
            var result = new List<LandIceController>();
            foreach (var ctrl in s_allLandIce)
            {
                if (ctrl == null) continue;
                if (ctrl.OverlapsWithDisk(center, radius))
                    result.Add(ctrl);
            }
            return result;
        }

        /// <summary>
        /// Destroys this ice surface and cleans up ZDO via the anchor.
        /// </summary>
        public void Remove()
        {
            s_allLandIce.Remove(this);
            Cleanup();

            if (_anchor != null)
            {
                _anchor.DestroyPermanently();
            }
            else
            {
                // Legacy / orphan path — destroy the host GO directly.
                IcePlacementTool.SafeDestroyIcePrefab(gameObject);
            }
        }

        /// <summary>
        /// LEGACY check for the pre-migration ice1-clone ZDOs. Round-26 §1.2
        /// moved persistence to `va_landice_*` keys on a dedicated VALandIce
        /// prefab; this static survives only so cleanup passes can identify
        /// stale ice1-clone ZDOs that still carry the old `va_land_ice` flag.
        /// </summary>
        public static bool ZdoHasLandIceData(ZDO zdo)
        {
            return zdo != null && zdo.GetBool(s_isLandIceHash, false);
        }

        #endregion

        #region Lifecycle

        // Round-26 §1.2: no auto-restore here. LandIceAnchor.Start drives
        // the restore via RestoreFromAnchor (below), so the controller
        // only acts as a behaviour + mesh container.
        private void Start() { /* anchor-driven */ }

        /// <summary>
        /// Called by LandIceAnchor.SpawnNew (placement) and
        /// LandIceAnchor.EnsureLinkedController (restore) to attach
        /// this controller to its persistent anchor.
        /// </summary>
        public void BindAnchor(LandIceAnchor anchor)
        {
            _anchor = anchor;
        }

        /// <summary>
        /// Called by LandIceAnchor.Start after the ZDO has spawned to
        /// rebuild the disk from persisted data (footprints + heights +
        /// grid bounds + noise seed). No-op if already initialized
        /// (e.g. caller went through Initialize() first).
        /// </summary>
        public void RestoreFromAnchor(LandIceAnchor anchor)
        {
            if (_initialized) return;
            _anchor = anchor;

            if (!anchor.ReadData(out float noiseSeed, out byte[] fpData, out byte[] gridData, out byte[] heightData))
                return;
            if (fpData == null || fpData.Length < 16) return;

            _footprints = UnpackFootprintsWithY(fpData);
            if (_footprints.Count == 0) return;

            _noiseSeed = noiseSeed;

            if (gridData != null && gridData.Length >= 24)
            {
                _gridWidth = BitConverter.ToInt32(gridData, 0);
                _gridHeight = BitConverter.ToInt32(gridData, 4);
                _boundsMinX = BitConverter.ToSingle(gridData, 8);
                _boundsMinZ = BitConverter.ToSingle(gridData, 12);
                _boundsMaxX = BitConverter.ToSingle(gridData, 16);
                _boundsMaxZ = BitConverter.ToSingle(gridData, 20);
            }
            else
            {
                RecalcBounds();
                _gridWidth = Mathf.CeilToInt((_boundsMaxX - _boundsMinX) / CELL_SIZE) + 1;
                _gridHeight = Mathf.CeilToInt((_boundsMaxZ - _boundsMinZ) / CELL_SIZE) + 1;
            }

            if (heightData != null && heightData.Length == _gridWidth * _gridHeight * 4)
            {
                _heights = new float[_gridWidth * _gridHeight];
                Buffer.BlockCopy(heightData, 0, _heights, 0, heightData.Length);
            }
            else
            {
                InitializeHeightGrid();
            }

            _iceMaterial = WaterMaterialManager.GetMaterial(CustomLiquidType.Ice);
            IceSurfaceY = transform.position.y;

            // VALandIce prefab is already stripped invisible by
            // LandIceAnchorPrefab, so no per-instance strip needed.
            CreateGameObjects();
            RebuildMesh();

            _initialized = true;
            if (!s_allLandIce.Contains(this))
                s_allLandIce.Add(this);

            FiresLogger.LogInfo($"[LandIceController] Restored from anchor at {transform.position} with {_footprints.Count} footprints");
        }

        private void OnDestroy()
        {
            s_allLandIce.Remove(this);
            Cleanup();
        }

        private void Cleanup()
        {
            if (_iceGO != null) Destroy(_iceGO);
            if (_iceMesh != null) Destroy(_iceMesh);
            if (_colliderMesh != null) Destroy(_colliderMesh);
        }

        #endregion

        #region Height Grid

        private void RecalcBounds()
        {
            if (_footprints.Count == 0)
            {
                _boundsMinX = _boundsMaxX = _boundsMinZ = _boundsMaxZ = 0f;
                return;
            }

            var fp0 = _footprints[0];
            float pad = GRID_PADDING + BLEND_DISTANCE + SLOPE_DISTANCE;
            _boundsMinX = fp0.Center.x - fp0.Radius - pad;
            _boundsMaxX = fp0.Center.x + fp0.Radius + pad;
            _boundsMinZ = fp0.Center.z - fp0.Radius - pad;
            _boundsMaxZ = fp0.Center.z + fp0.Radius + pad;

            for (int i = 1; i < _footprints.Count; i++)
            {
                var fp = _footprints[i];
                _boundsMinX = Mathf.Min(_boundsMinX, fp.Center.x - fp.Radius - pad);
                _boundsMaxX = Mathf.Max(_boundsMaxX, fp.Center.x + fp.Radius + pad);
                _boundsMinZ = Mathf.Min(_boundsMinZ, fp.Center.z - fp.Radius - pad);
                _boundsMaxZ = Mathf.Max(_boundsMaxZ, fp.Center.z + fp.Radius + pad);
            }
        }

        /// <summary>
        /// Creates the initial height grid. Samples terrain at each vertex
        /// and applies offset from each footprint.
        /// </summary>
        private void InitializeHeightGrid()
        {
            _gridWidth = Mathf.CeilToInt((_boundsMaxX - _boundsMinX) / CELL_SIZE) + 1;
            _gridHeight = Mathf.CeilToInt((_boundsMaxZ - _boundsMinZ) / CELL_SIZE) + 1;
            _heights = new float[_gridWidth * _gridHeight];

            SampleHeightsFromFootprints();
            FillHeightGaps();
            SmoothHeightEdges();
        }

        /// <summary>
        /// Expands the height grid when new footprints are added.
        /// Re-samples all heights from all footprints with terrain conformance.
        /// </summary>
        private void ExpandHeightGrid()
        {
            RecalcBounds();
            _gridWidth = Mathf.CeilToInt((_boundsMaxX - _boundsMinX) / CELL_SIZE) + 1;
            _gridHeight = Mathf.CeilToInt((_boundsMaxZ - _boundsMinZ) / CELL_SIZE) + 1;
            _heights = new float[_gridWidth * _gridHeight];

            SampleHeightsFromFootprints();
            FillHeightGaps();
            SmoothHeightEdges();
        }

        /// <summary>
        /// Samples terrain ground height at the given world XZ position.
        /// Uses ZoneSystem first, then raycast fallback.
        /// </summary>
        private static float SampleTerrainHeight(float wx, float wz)
        {
            Vector3 pos = new Vector3(wx, 0f, wz);

            if (ZoneSystem.instance != null)
            {
                if (ZoneSystem.instance.GetGroundHeight(pos, out float gh))
                    return gh;
            }

            int mask = LayerMask.GetMask("terrain", "Default", "static_solid");
            if (Physics.Raycast(new Vector3(wx, 1000f, wz), Vector3.down, out RaycastHit hit, 2000f, mask))
                return hit.point.y;

            return 0f;
        }

        /// <summary>
        /// Threshold for deciding ground-conform vs raised-disk mode.
        /// If a footprint's offset from terrain at its center is below this,
        /// it hugs the terrain. Above this, it's a raised flat disk.
        /// </summary>
        private const float RAISED_THRESHOLD = 0.5f;

        /// <summary>
        /// Samples heights at all grid points from all footprints.
        /// 
        /// HIGHEST-WINS with DUAL EDGE BEHAVIOR:
        /// 
        /// CORE (inside radius): full footprint height (highest wins).
        /// 
        /// OVERLAP EDGE (in blend zone AND another footprint is nearby):
        ///   Smoothly slopes from the raised disk's height down to the
        ///   lower disk's height. The slope uses smoothstep over
        ///   SLOPE_DISTANCE beyond the footprint radius.
        /// 
        /// NON-OVERLAP EDGE (in blend zone, no other footprint nearby):
        ///   Height stays at the footprint's full height. The skirt
        ///   pass in RebuildMesh will drop boundary vertices to
        ///   SKIRT_BOTTOM_Y for a clean cliff.
        /// </summary>
        private void SampleHeightsFromFootprints()
        {
            if (_footprints.Count == 0) return;

            // Pre-compute each footprint's offset from terrain at its center.
            float[] fpOffsets = new float[_footprints.Count];
            bool[] fpRaised = new bool[_footprints.Count];

            for (int d = 0; d < _footprints.Count; d++)
            {
                var fp = _footprints[d];
                float terrainAtCenter = SampleTerrainHeight(fp.Center.x, fp.Center.z);
                fpOffsets[d] = fp.Center.y - terrainAtCenter;
                fpRaised[d] = Mathf.Abs(fpOffsets[d]) >= RAISED_THRESHOLD;
            }

            for (int gz = 0; gz < _gridHeight; gz++)
            {
                for (int gx = 0; gx < _gridWidth; gx++)
                {
                    float wx = _boundsMinX + gx * CELL_SIZE;
                    float wz = _boundsMinZ + gz * CELL_SIZE;

                    float terrainY = SampleTerrainHeight(wx, wz);

                    float bestHeight = -200f;
                    float bestWeight = 0f;
                    int bestIdx = -1;
                    bool hasContribution = false;

                    // Track the height of the best OTHER footprint (for overlap slope target).
                    float secondBestHeight = -200f;
                    bool hasSecondContribution = false;

                    for (int d = 0; d < _footprints.Count; d++)
                    {
                        var fp = _footprints[d];
                        float dx = wx - fp.Center.x;
                        float dz = wz - fp.Center.z;
                        float dist = Mathf.Sqrt(dx * dx + dz * dz);

                        // Extended reach for raised disks so slope zone can find overlap targets.
                        float maxReach = fpRaised[d]
                            ? fp.Radius + BLEND_DISTANCE + SLOPE_DISTANCE
                            : fp.Radius + BLEND_DISTANCE;
                        if (dist > maxReach)
                            continue;

                        float weight = CalculateBlendWeight(dist, fp.Radius);

                        // Desired height for this footprint at this vertex.
                        float desiredH;
                        if (fpRaised[d])
                        {
                            float noise = Mathf.PerlinNoise(
                                (wx + _noiseSeed) * 0.15f,
                                (wz + _noiseSeed) * 0.15f) * 0.15f - 0.075f;
                            desiredH = fp.Center.y + noise;
                        }
                        else
                        {
                            desiredH = terrainY + fpOffsets[d] + ICE_TERRAIN_OFFSET;
                        }

                        if (desiredH > bestHeight)
                        {
                            // Demote current best to second-best.
                            if (hasContribution)
                            {
                                secondBestHeight = Mathf.Max(secondBestHeight, bestHeight);
                                hasSecondContribution = true;
                            }
                            bestHeight = desiredH;
                            bestWeight = weight;
                            bestIdx = d;
                            hasContribution = true;
                        }
                        else
                        {
                            // This is a lower disk - track as potential slope target.
                            secondBestHeight = Mathf.Max(secondBestHeight, desiredH);
                            hasSecondContribution = true;
                        }
                    }

                    int idx = gz * _gridWidth + gx;

                    if (!hasContribution)
                    {
                        _heights[idx] = -100f;
                        continue;
                    }

                    // If the winning footprint is raised and we're beyond its radius,
                    // check for overlap with another footprint.
                    if (bestIdx >= 0 && fpRaised[bestIdx])
                    {
                        var winnerFp = _footprints[bestIdx];
                        float winnerDx = wx - winnerFp.Center.x;
                        float winnerDz = wz - winnerFp.Center.z;
                        float winnerDist = Mathf.Sqrt(winnerDx * winnerDx + winnerDz * winnerDz);

                        if (winnerDist > winnerFp.Radius)
                        {
                            // We're in the blend/slope zone of the winning raised disk.
                            float distBeyond = winnerDist - winnerFp.Radius;
                            float slopeRange = BLEND_DISTANCE + SLOPE_DISTANCE;

                            // Check if another footprint is nearby (overlap edge).
                            if (hasSecondContribution && secondBestHeight > -100f)
                            {
                                // OVERLAP EDGE: Smoothly slope from raised height
                                // down to the lower disk's height.
                                float slopeT = Mathf.Clamp01(distBeyond / slopeRange);
                                slopeT = slopeT * slopeT * (3f - 2f * slopeT); // smoothstep
                                _heights[idx] = Mathf.Lerp(bestHeight, secondBestHeight, slopeT);
                            }
                            else
                            {
                                // NON-OVERLAP EDGE: No other disk nearby.
                                // Only emit height within the tight blend zone;
                                // beyond that, mark invalid so the mesh stops
                                // and the skirt handles the cliff.
                                if (distBeyond > BLEND_DISTANCE)
                                {
                                    _heights[idx] = -100f;
                                }
                                else
                                {
                                    _heights[idx] = bestHeight;
                                }
                            }
                            continue;
                        }
                    }

                    // Core vertex - full height.
                    _heights[idx] = bestHeight;
                }
            }
        }

        /// <summary>
        /// Calculates blend weight based on distance from disk center.
        /// Matches DirtFloorMeshCombiner.CalculateBlendWeight exactly.
        /// </summary>
        private float CalculateBlendWeight(float dist, float radius)
        {
            float edgeDist = radius - dist;

            if (edgeDist >= BLEND_DISTANCE)
            {
                return 1f;
            }
            else if (edgeDist > 0f)
            {
                float t = edgeDist / BLEND_DISTANCE;
                return t * t * (3f - 2f * t);
            }
            else if (edgeDist > -BLEND_DISTANCE)
            {
                float t = (edgeDist + BLEND_DISTANCE) / BLEND_DISTANCE;
                return t * t * (3f - 2f * t) * 0.5f;
            }
            else
            {
                return 0f;
            }
        }

        /// <summary>
        /// Fills small gaps in the height grid where isolated invalid cells
        /// are surrounded by valid neighbors. Same as DirtFloor FillHeightGaps.
        /// </summary>
        private void FillHeightGaps()
        {
            if (_heights == null || _heights.Length == 0) return;

            for (int pass = 0; pass < 3; pass++)
            {
                bool anyFilled = false;

                for (int z = 1; z < _gridHeight - 1; z++)
                {
                    for (int x = 1; x < _gridWidth - 1; x++)
                    {
                        int idx = z * _gridWidth + x;
                        if (_heights[idx] > -50f) continue;

                        float wx = _boundsMinX + x * CELL_SIZE;
                        float wz = _boundsMinZ + z * CELL_SIZE;

                        // Only fill if close to a footprint (including slope zone)
                        float closestDist = float.MaxValue;
                        for (int d = 0; d < _footprints.Count; d++)
                        {
                            var fp = _footprints[d];
                            float ddx = wx - fp.Center.x;
                            float ddz = wz - fp.Center.z;
                            float dist = Mathf.Sqrt(ddx * ddx + ddz * ddz) - fp.Radius;
                            closestDist = Mathf.Min(closestDist, dist);
                        }
                        if (closestDist > (BLEND_DISTANCE + SLOPE_DISTANCE) * 1.5f) continue;

                        float sum = 0f;
                        int validCount = 0;

                        for (int nz = -1; nz <= 1; nz++)
                        {
                            for (int nx = -1; nx <= 1; nx++)
                            {
                                if (nx == 0 && nz == 0) continue;
                                int ni = (z + nz) * _gridWidth + (x + nx);
                                if (ni >= 0 && ni < _heights.Length && _heights[ni] > -50f)
                                {
                                    sum += _heights[ni];
                                    validCount++;
                                }
                            }
                        }

                        if (validCount >= 3)
                        {
                            _heights[idx] = sum / validCount;
                            anyFilled = true;
                        }
                    }
                }

                if (!anyFilled) break;
            }
        }

        /// <summary>
        /// Smooths height edges to create gentle slopes.
        /// Matches DirtFloorMeshCombiner.SmoothHeightEdges exactly -
        /// the weighted average from SampleHeightsFromFootprints already
        /// handles blending between different-height disks, so this just
        /// needs a light 2-pass polish to remove any remaining jaggedness.
        /// </summary>
        private void SmoothHeightEdges()
        {
            if (_heights == null || _heights.Length == 0) return;

            float[] smoothed = new float[_heights.Length];
            Array.Copy(_heights, smoothed, _heights.Length);

            for (int pass = 0; pass < 2; pass++)
            {
                for (int z = 1; z < _gridHeight - 1; z++)
                {
                    for (int x = 1; x < _gridWidth - 1; x++)
                    {
                        int idx = z * _gridWidth + x;
                        if (_heights[idx] < -50f) continue;

                        float centerWeight = 4f;
                        float sum = _heights[idx] * centerWeight;
                        float weightSum = centerWeight;

                        for (int nz = -1; nz <= 1; nz++)
                        {
                            for (int nx = -1; nx <= 1; nx++)
                            {
                                if (nx == 0 && nz == 0) continue;
                                int ni = (z + nz) * _gridWidth + (x + nx);
                                if (_heights[ni] > -50f)
                                {
                                    float w = (nx == 0 || nz == 0) ? 1f : 0.5f;
                                    sum += _heights[ni] * w;
                                    weightSum += w;
                                }
                            }
                        }

                        float avgHeight = sum / weightSum;
                        float diff = Mathf.Abs(_heights[idx] - avgHeight);

                        if (diff > 0.05f)
                        {
                            float blendFactor = Mathf.Clamp01(diff * 0.5f);
                            smoothed[idx] = Mathf.Lerp(_heights[idx], avgHeight, blendFactor);
                        }
                    }
                }

                Array.Copy(smoothed, _heights, _heights.Length);
            }
        }

        #endregion

        #region Game Object Setup

        private void CreateGameObjects()
        {
            if (_iceGO != null) Destroy(_iceGO);
            _iceGO = new GameObject("IceOverlay");
            _iceGO.transform.SetParent(transform);
            _iceGO.transform.localPosition = Vector3.zero;
            _iceGO.transform.localRotation = Quaternion.identity;
            _iceGO.transform.localScale = Vector3.one;
            // Use "piece" layer so Infinity Hammer's placement ruler can hover
            // over existing ice (same as DirtFloorMeshCombiner). The "Default" layer
            // blocks IH's raycast and shows "invalid placement".
            int iceLayer = LayerMask.NameToLayer("piece");
            if (iceLayer < 0) iceLayer = LayerMask.NameToLayer("Default");
            _iceGO.layer = iceLayer;

            _iceMesh = new Mesh { name = "LandIceMesh" };
            _iceMeshFilter = _iceGO.AddComponent<MeshFilter>();
            _iceMeshFilter.sharedMesh = _iceMesh;
            _iceRenderer = _iceGO.AddComponent<MeshRenderer>();
            if (_iceMaterial != null) _iceRenderer.material = _iceMaterial;

            var overlayRb = _iceGO.AddComponent<Rigidbody>();
            overlayRb.isKinematic = true;
            overlayRb.useGravity = false;

            _iceCollider = _iceGO.AddComponent<MeshCollider>();
            _iceCollider.convex = false;
        }

        private void DisableFloatingPhysics()
        {
            var floating = gameObject.GetComponent<Floating>();
            if (floating != null)
                floating.enabled = false;

            var rb = gameObject.GetComponent<Rigidbody>();
            if (rb != null)
            {
                if (!rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            // Destroy ALL BoxColliders on the root - trigger or not.
            // The vanilla ice1 prefab has a BoxCollider that blocks IH
            // placement raycast, preventing placing ice on top of ice.
            var boxes = gameObject.GetComponents<BoxCollider>();
            foreach (var box in boxes)
            {
                if (box != null)
                    Destroy(box);
            }

            // Destroy Piece component if present - prevents IH from
            // treating existing ice as a "piece" that blocks placement.
            var piece = gameObject.GetComponent<Piece>();
            if (piece != null)
                Destroy(piece);
        }

        #endregion

        #region Mesh Generation

        /// <summary>
        /// Rebuilds the visual and collider meshes from the height grid using
        /// marching-squares boundary smoothing with noise-randomized edges.
        /// 
        /// This uses the same technique as <see cref="FloatingIceController"/>:
        /// - Noise-randomized signed distance field per vertex
        /// - Marching squares interpolation at boundary cells
        /// - Edge displacement (vertices near the boundary are pulled toward it)
        /// - Edge bevel (slight height dip at the boundary for a natural look)
        /// 
        /// TOP FACE: Marching-squares quad grid with smooth, organic edges.
        /// 
        /// SKIRT: For each boundary crossing segment, a vertical quad drops
        /// from the top surface down to terrain height (sampled per-crossing),
        /// following the smooth marching-squares boundary line instead of
        /// stair-stepping.
        /// 
        /// The collider mesh includes only the top face so the player
        /// walks on the top surface.
        /// </summary>
        private void RebuildMesh()
        {
            if (_heights == null || _heights.Length == 0) return;
            if (_gridWidth <= 0 || _gridHeight <= 0) return;

            Vector3 myPos = transform.position;

            // --- Compute signed distance field per vertex ---
            // The SDF from IceMeshUtility.GetEdgeDistance is the SOLE authority
            // for where the mesh boundary is. The height grid only provides Y
            // values - it does NOT determine the mesh shape. This prevents
            // blend/slope zone cells (which have valid heights but are outside
            // the noisy footprint boundary) from creating an oversized outer shape.
            int totalVerts = _gridWidth * _gridHeight;
            var edgeDist = new float[totalVerts];

            for (int gz = 0; gz < _gridHeight; gz++)
            {
                for (int gx = 0; gx < _gridWidth; gx++)
                {
                    float wx = _boundsMinX + gx * CELL_SIZE;
                    float wz = _boundsMinZ + gz * CELL_SIZE;
                    int idx = gz * _gridWidth + gx;

                    edgeDist[idx] = IceMeshUtility.GetEdgeDistance(_footprints, wx, wz, _noiseSeed);
                }
            }

            // --- Build top face with marching squares ---
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();
            var norms = new List<Vector3>();

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

                    // Heights from grid (world Y)
                    float h0 = _heights[i0] > -50f ? _heights[i0] - myPos.y : 0f;
                    float h1 = _heights[i1] > -50f ? _heights[i1] - myPos.y : 0f;
                    float h2 = _heights[i2] > -50f ? _heights[i2] - myPos.y : 0f;
                    float h3 = _heights[i3] > -50f ? _heights[i3] - myPos.y : 0f;

                    bool b0 = d0 > 0f, b1 = d1 > 0f, b2 = d2 > 0f, b3 = d3 > 0f;
                    int caseIndex = (b0 ? 1 : 0) | (b1 ? 2 : 0) | (b2 ? 4 : 0) | (b3 ? 8 : 0);

                    if (caseIndex == 0) continue;

                    // Displaced corners for edge smoothing
                    float fwx0 = wx0, fwz0 = wz0;
                    float fwx1 = wx1, fwz1 = wz1;
                    float fwx2 = wx2, fwz2 = wz2;
                    float fwx3 = wx3, fwz3 = wz3;

                    if (caseIndex == 15)
                    {
                        // Fully inside - apply edge displacement near boundary
                        float minD = Mathf.Min(d0, Mathf.Min(d1, Mathf.Min(d2, d3)));
                        if (minD < IceMeshUtility.EDGE_BEVEL_WIDTH)
                        {
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx0, ref fwz0, d0, wx0, wz0);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx1, ref fwz1, d1, wx1, wz1);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx2, ref fwz2, d2, wx2, wz2);
                            IceMeshUtility.DisplaceCornerTowardBoundary(_footprints, _noiseSeed, ref fwx3, ref fwz3, d3, wx3, wz3);
                        }

                        // Apply bevel at edges
                        float bev0 = -IceMeshUtility.ComputeBevel(d0);
                        float bev1 = -IceMeshUtility.ComputeBevel(d1);
                        float bev2 = -IceMeshUtility.ComputeBevel(d2);
                        float bev3 = -IceMeshUtility.ComputeBevel(d3);

                        int v0 = verts.Count;
                        verts.Add(new Vector3(fwx0 - myPos.x, h0 + bev0, fwz0 - myPos.z));
                        verts.Add(new Vector3(fwx1 - myPos.x, h1 + bev1, fwz1 - myPos.z));
                        verts.Add(new Vector3(fwx2 - myPos.x, h2 + bev2, fwz2 - myPos.z));
                        verts.Add(new Vector3(fwx3 - myPos.x, h3 + bev3, fwz3 - myPos.z));
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

                    // Boundary quad - use marching squares with heights
                    EmitLandBoundaryTopFace(verts, tris, uvs, norms,
                        wx0, wz0, d0, h0, wx1, wz1, d1, h1,
                        wx2, wz2, d2, h2, wx3, wz3, d3, h3,
                        myPos, b0, b1, b2, b3);
                }
            }

            int topFaceVertCount = verts.Count;
            int topFaceTriCount = tris.Count;

            // --- SKIRT: vertical walls along marching-squares boundary ---
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

                    // Skirt only on boundary quads (not fully inside or outside)
                    if (caseIndex == 0 || caseIndex == 15) continue;

                    float wx0 = _boundsMinX + gx * CELL_SIZE, wz0 = _boundsMinZ + gz * CELL_SIZE;
                    float wx1 = wx0 + CELL_SIZE, wz1 = wz0;
                    float wx2 = wx0, wz2 = wz0 + CELL_SIZE;
                    float wx3 = wx0 + CELL_SIZE, wz3 = wz0 + CELL_SIZE;

                    float h0 = _heights[i0] > -50f ? _heights[i0] - myPos.y : 0f;
                    float h1 = _heights[i1] > -50f ? _heights[i1] - myPos.y : 0f;
                    float h2 = _heights[i2] > -50f ? _heights[i2] - myPos.y : 0f;
                    float h3 = _heights[i3] > -50f ? _heights[i3] - myPos.y : 0f;

                    // Interpolate height at boundary crossings
                    EmitLandBoundarySkirt(verts, tris, uvs, norms,
                        wx0, wz0, d0, h0, wx1, wz1, d1, h1,
                        wx2, wz2, d2, h2, wx3, wz3, d3, h3,
                        myPos, b0, b1, b2, b3);
                }
            }

            _iceMesh.Clear();
            if (verts.Count > 0 && tris.Count > 0)
            {
                _iceMesh.SetVertices(verts);
                _iceMesh.SetTriangles(tris, 0);
                _iceMesh.SetUVs(0, uvs);
                _iceMesh.SetNormals(norms);
                _iceMesh.RecalculateNormals();
                _iceMesh.RecalculateBounds();
            }

            // Collider uses only the top face (no skirt)
            if (_colliderMesh == null) _colliderMesh = new Mesh { name = "LandIceCollider" };
            _colliderMesh.Clear();
            if (topFaceVertCount > 0)
            {
                var collVerts = new List<Vector3>(topFaceVertCount);
                var collNorms = new List<Vector3>(topFaceVertCount);
                for (int i = 0; i < topFaceVertCount; i++)
                {
                    collVerts.Add(verts[i]);
                    collNorms.Add(norms[i]);
                }

                var collTris = new List<int>();
                for (int t = 0; t < topFaceTriCount; t += 3)
                {
                    if (tris[t] < topFaceVertCount && tris[t + 1] < topFaceVertCount && tris[t + 2] < topFaceVertCount)
                    {
                        collTris.Add(tris[t]);
                        collTris.Add(tris[t + 1]);
                        collTris.Add(tris[t + 2]);
                    }
                }

                _colliderMesh.SetVertices(collVerts);
                _colliderMesh.SetTriangles(collTris, 0);
                _colliderMesh.SetNormals(collNorms);
                _colliderMesh.RecalculateBounds();
            }

            if (_iceCollider != null)
            {
                _iceCollider.sharedMesh = null;
                _iceCollider.sharedMesh = _colliderMesh;
            }
        }

        /// <summary>
        /// Emits marching-squares triangles for a boundary quad on the land ice top face.
        /// Matches the floating ice approach: interpolates edge crossings, applies
        /// edge displacement, and fans triangles from the polygon.
        /// </summary>
        private void EmitLandBoundaryTopFace(
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

            // Edge displacement: pull near-boundary corners toward the boundary line
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

            // Fan triangulate the polygon
            float bevel0 = -IceMeshUtility.ComputeBevel(polyD[0]);
            int fv = verts.Count;
            verts.Add(new Vector3(polyX[0] - myPos.x, polyH[0] + bevel0, polyZ[0] - myPos.z));
            uvs.Add(new Vector2(polyX[0] * 0.1f, polyZ[0] * 0.1f));
            norms.Add(Vector3.up);

            float bevel1 = -IceMeshUtility.ComputeBevel(polyD[1]);
            int prevV = verts.Count;
            verts.Add(new Vector3(polyX[1] - myPos.x, polyH[1] + bevel1, polyZ[1] - myPos.z));
            uvs.Add(new Vector2(polyX[1] * 0.1f, polyZ[1] * 0.1f));
            norms.Add(Vector3.up);

            for (int p = 2; p < polyX.Count; p++)
            {
                float bevelP = -IceMeshUtility.ComputeBevel(polyD[p]);
                int curV = verts.Count;
                verts.Add(new Vector3(polyX[p] - myPos.x, polyH[p] + bevelP, polyZ[p] - myPos.z));
                uvs.Add(new Vector2(polyX[p] * 0.1f, polyZ[p] * 0.1f));
                norms.Add(Vector3.up);
                tris.Add(fv); tris.Add(prevV); tris.Add(curV);
                prevV = curV;
            }
        }

        /// <summary>
        /// Emits vertical skirt quads along the marching-squares boundary of
        /// a boundary cell. Only emits quads between PAIRS of crossing points
        /// on the cell edges - this is the same paired-crossing approach used
        /// by <see cref="IceMeshUtility.EmitMarchingSquaresSideWalls"/>.
        ///
        /// The skirt bottom at each crossing is sampled from terrain height
        /// (plus penetration depth) so it reaches the ground.
        /// </summary>
        private void EmitLandBoundarySkirt(
            List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Vector3> norms,
            float wx0, float wz0, float d0, float h0,
            float wx1, float wz1, float d1, float h1,
            float wx2, float wz2, float d2, float h2,
            float wx3, float wz3, float d3, float h3,
            Vector3 myPos, bool b0, bool b1, bool b2, bool b3)
        {
            bool hasBot = b0 != b1, hasLeft = b0 != b2, hasRight = b1 != b3, hasTop = b2 != b3;

            // Collect crossing points in perimeter order: bot, right, top, left.
            // Marching squares guarantees an even number of crossings (0, 2, or 4).
            var crossings = new List<Vector3>(4);

            if (hasBot)
            {
                float t = Mathf.Clamp01(d0 / (d0 - d1));
                crossings.Add(new Vector3(
                    wx0 + (wx1 - wx0) * t,
                    wz0 + (wz1 - wz0) * t,
                    h0 + (h1 - h0) * t));
            }
            if (hasRight)
            {
                float t = Mathf.Clamp01(d1 / (d1 - d3));
                crossings.Add(new Vector3(
                    wx1 + (wx3 - wx1) * t,
                    wz1 + (wz3 - wz1) * t,
                    h1 + (h3 - h1) * t));
            }
            if (hasTop)
            {
                float t = Mathf.Clamp01(d2 / (d2 - d3));
                crossings.Add(new Vector3(
                    wx2 + (wx3 - wx2) * t,
                    wz2 + (wz3 - wz2) * t,
                    h2 + (h3 - h2) * t));
            }
            if (hasLeft)
            {
                float t = Mathf.Clamp01(d0 / (d0 - d2));
                crossings.Add(new Vector3(
                    wx0 + (wx2 - wx0) * t,
                    wz0 + (wz2 - wz0) * t,
                    h0 + (h2 - h0) * t));
            }

            // Need at least 2 crossings (one pair) to emit a skirt quad
            if (crossings.Count < 2) return;

            // Compute center of inside vertices for outward normal calculation
            float insideCX = 0f, insideCZ = 0f;
            int insideCount = 0;
            if (b0) { insideCX += wx0; insideCZ += wz0; insideCount++; }
            if (b1) { insideCX += wx1; insideCZ += wz1; insideCount++; }
            if (b2) { insideCX += wx2; insideCZ += wz2; insideCount++; }
            if (b3) { insideCX += wx3; insideCZ += wz3; insideCount++; }
            if (insideCount > 0) { insideCX /= insideCount; insideCZ /= insideCount; }

            // Emit one skirt quad per pair of crossings
            for (int c = 0; c + 1 < crossings.Count; c += 2)
            {
                EmitSkirtQuad(verts, tris, uvs, norms,
                    crossings[c], crossings[c + 1], myPos, insideCX, insideCZ);
            }
        }

        /// <summary>
        /// Emits a single skirt quad between two boundary strip points.
        /// The bottom of the skirt is sampled from terrain height at each
        /// point's world XZ position, ensuring the skirt just reaches
        /// the ground rather than dropping to a fixed absolute Y.
        /// </summary>
        private void EmitSkirtQuad(
            List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Vector3> norms,
            Vector3 ca, Vector3 cb, Vector3 myPos, float insideCX, float insideCZ)
        {
            float lxA = ca.x - myPos.x, lzA = ca.y - myPos.z;
            float lxB = cb.x - myPos.x, lzB = cb.y - myPos.z;
            float topHA = ca.z; // already local Y
            float topHB = cb.z;

            // Sample terrain at each point to compute skirt bottom
            float terrainA = SampleTerrainHeight(ca.x, ca.y);
            float terrainB = SampleTerrainHeight(cb.x, cb.y);
            float botA = terrainA - SKIRT_TERRAIN_PENETRATION - myPos.y;
            float botB = terrainB - SKIRT_TERRAIN_PENETRATION - myPos.y;

            // Skip degenerate quads where top and bottom are at the same height
            if (Mathf.Abs(topHA - botA) < 0.01f && Mathf.Abs(topHB - botB) < 0.01f)
                return;

            // Compute outward normal
            float edx = cb.x - ca.x, edz = cb.y - ca.y;
            float len = Mathf.Sqrt(edx * edx + edz * edz);
            Vector3 wallNormal;
            if (len > 0.001f)
            {
                float midX = (ca.x + cb.x) * 0.5f;
                float midZ = (ca.y + cb.y) * 0.5f;
                float candNx = -edz / len, candNz = edx / len;
                float toInsideX = insideCX - midX, toInsideZ = insideCZ - midZ;
                float dot = candNx * toInsideX + candNz * toInsideZ;
                if (dot > 0f) { candNx = -candNx; candNz = -candNz; }
                wallNormal = new Vector3(candNx, 0f, candNz);
            }
            else
                wallNormal = Vector3.forward;

            int sv = verts.Count;
            verts.Add(new Vector3(lxA, topHA, lzA));
            verts.Add(new Vector3(lxB, topHB, lzB));
            verts.Add(new Vector3(lxA, botA, lzA));
            verts.Add(new Vector3(lxB, botB, lzB));

            uvs.Add(new Vector2(ca.x * 0.1f, topHA * 0.1f));
            uvs.Add(new Vector2(cb.x * 0.1f, topHB * 0.1f));
            uvs.Add(new Vector2(ca.x * 0.1f, botA * 0.1f));
            uvs.Add(new Vector2(cb.x * 0.1f, botB * 0.1f));

            norms.Add(wallNormal); norms.Add(wallNormal);
            norms.Add(wallNormal); norms.Add(wallNormal);

            tris.Add(sv); tris.Add(sv + 1); tris.Add(sv + 2);
            tris.Add(sv + 1); tris.Add(sv + 3); tris.Add(sv + 2);
        }

        #endregion

        #region ZDO Persistence

        /// <summary>
        /// Packs footprints with full Y coordinate for land ice persistence.
        /// Unlike the shared IceMeshUtility.PackFootprints (which omits Y),
        /// land ice needs Y to reconstruct the terrain offset on reload.
        /// Format: [cx, cy, cz, radius] per footprint (16 bytes each).
        /// </summary>
        private static byte[] PackFootprintsWithY(IReadOnlyList<IceDiskFootprint> footprints)
        {
            byte[] data = new byte[footprints.Count * 4 * 4];
            for (int i = 0; i < footprints.Count; i++)
            {
                var fp = footprints[i];
                int offset = i * 16;
                Buffer.BlockCopy(BitConverter.GetBytes(fp.Center.x), 0, data, offset, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(fp.Center.y), 0, data, offset + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(fp.Center.z), 0, data, offset + 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(fp.Radius), 0, data, offset + 12, 4);
            }
            return data;
        }

        /// <summary>
        /// Unpacks footprints with full Y coordinate.
        /// </summary>
        private static List<IceDiskFootprint> UnpackFootprintsWithY(byte[] data)
        {
            var footprints = new List<IceDiskFootprint>();
            if (data == null || data.Length < 16) return footprints;

            int count = data.Length / 16;
            for (int i = 0; i < count; i++)
            {
                int offset = i * 16;
                float cx = BitConverter.ToSingle(data, offset);
                float cy = BitConverter.ToSingle(data, offset + 4);
                float cz = BitConverter.ToSingle(data, offset + 8);
                float r = BitConverter.ToSingle(data, offset + 12);
                footprints.Add(new IceDiskFootprint { Center = new Vector3(cx, cy, cz), Radius = r });
            }
            return footprints;
        }

        // Round-26 §1.2: anchor-routed save. Packs the controller's
        // current state and hands it to LandIceAnchor.WriteData, which
        // owns the ZDO writes (new `va_landice_*` key namespace).
        private void SaveToZDO()
        {
            if (_anchor == null) return;

            byte[] gridParams = new byte[6 * 4];
            Buffer.BlockCopy(BitConverter.GetBytes(_gridWidth), 0, gridParams, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_gridHeight), 0, gridParams, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_boundsMinX), 0, gridParams, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_boundsMinZ), 0, gridParams, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_boundsMaxX), 0, gridParams, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_boundsMaxZ), 0, gridParams, 20, 4);

            byte[] heightData = null;
            if (_heights != null && _heights.Length > 0)
            {
                heightData = new byte[_heights.Length * 4];
                Buffer.BlockCopy(_heights, 0, heightData, 0, heightData.Length);
            }

            byte[] fpData = PackFootprintsWithY(_footprints);
            _anchor.WriteData(_noiseSeed, fpData, gridParams, heightData);
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

            // Strip any Hoverable/Interactable components from the vanilla prefab
            // to prevent "[E] Climb" or other interaction prompts on land ice
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
    }
}
