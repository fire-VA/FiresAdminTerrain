using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Mesh generation for WaterMeshCombiner.
    /// Handles terrain sampling, surface calculation, and mesh building.
    /// 
    /// v4 REWRITE: TRUE WATER PLANE with gravity settling.
    /// 
    /// KEY PHILOSOPHY:
    /// - Water naturally seeks a level surface (gravity settling)
    /// - Ponds: All connected water disks settle to a SINGLE level
    /// - Rivers: Can have gradual slopes but still maintain smooth surface
    /// - Banks: Water clips THROUGH terrain at banks, not conforms to it
    /// - No "edge curl" - water surface is flat, period
    /// 
    /// HOW IT WORKS:
    /// 1. Calculate the TARGET water level for all connected disks
    ///    - For ponds: Average Y of all disks weighted by area
    ///    - The water "settles" to this level like real water
    /// 2. Create a FLAT plane at this level
    /// 3. Only create vertices where water is ABOVE terrain
    /// 4. Let water clip through banks - this is correct behavior
    /// 
    /// This creates proper-looking water bodies without the bumpy
    /// "stacked pancake" appearance of the old edge-curl system.
    /// </summary>
    public partial class WaterMeshCombiner
    {
        #region Constants for Water Surface

        /// <summary>
        /// How far above terrain the water surface sits at minimum (prevents z-fighting).
        /// </summary>
        private const float WATER_TERRAIN_OFFSET = 0.02f;

        /// <summary>
        /// Target vertices per meter for water mesh (higher = smoother edges).
        /// </summary>
        private const float WATER_VERTS_PER_METER = 3f;

        /// <summary>
        /// Number of smoothing passes to apply to the water surface.
        /// Kept low (3) to avoid pulling the mesh away from disk-defined heights.
        /// </summary>
        private const int SURFACE_SMOOTH_PASSES = 3;

        /// <summary>
        /// Smoothing strength per pass (0-1, higher = more smoothing).
        /// Kept gentle (0.35) so the surface blends without distorting depth.
        /// </summary>
        private const float SMOOTH_STRENGTH = 0.35f;

        /// <summary>
        /// Default depth/thickness for water if not specified (meters).
        /// 2m gives natural-looking rivers without excessive depth.
        /// </summary>
        private const float DEFAULT_MAX_DEPTH = 2f;

        /// <summary>
        /// Edge fade distance - how far from disk edge water fades out.
        /// This creates soft edges rather than hard cutoffs.
        /// </summary>
        private const float EDGE_FADE_DISTANCE = 1.0f;

        /// <summary>
        /// Minimum water depth to render (avoids paper-thin water).
        /// </summary>
        private const float MIN_RENDER_DEPTH = 0.05f;

        /// <summary>
        /// How far to extend water mesh UNDER terrain at bank edges (meters).
        /// This allows the water to clip through terrain banks, hiding the
        /// sharp "staircase" edge that would otherwise be visible.
        ///
        /// 2026-05-25 round-2: RESTORED to 2.0f (briefly zeroed earlier this
        /// session). With the type-aware combiner fix in place — different
        /// CustomLiquidTypes get their own combiners — the bank extension can
        /// safely give natural-looking shores again and the waterfall effect
        /// from dragging the ruler up an elevation works as before.
        /// </summary>
        // 0.2.752 (Fire 10-05: Vpour "draws a much larger mesh than the circle of our ruler"): placed water ends at its
        // circles. A token width keeps the bank formulas' divisions finite; nothing reaches past the disk any more.
        private const float BANK_EXTENSION_DISTANCE = 0.01f;

        /// <summary>
        /// How far below terrain to push water vertices in bank areas.
        /// This ensures the water surface dips below terrain so it's clipped.
        ///
        /// 2026-05-25 round-2: RESTORED to 0.5f — see BANK_EXTENSION_DISTANCE.
        /// </summary>
        private const float BANK_DEPTH_BELOW_TERRAIN = 0.5f;

        /// <summary>
        /// Height variance tolerance for "pond mode" (meters).
        /// If all disks are within this variance, treat as pond (single level).
        /// INCREASED: Even water painted on slopes should settle to a single level
        /// unless intentionally creating rivers with large height changes.
        /// </summary>
        private const float POND_HEIGHT_TOLERANCE = 3.0f;

        /// <summary>
        /// Depth of the center channel depression for boat navigation (meters).
        /// Creates a subtle V-channel that guides boats along the center.
        /// Set to 0 to disable channel depression.
        /// </summary>
        private const float CENTER_CHANNEL_DEPTH = 0.05f;

        /// <summary>
        /// What fraction of the radius the center channel extends to.
        /// 0.3 = channel covers the center 30% of the water width.
        /// </summary>
        private const float CENTER_CHANNEL_RADIUS_FRACTION = 0.3f;

        /// <summary>
        /// Distance from terrain where water starts to blend/fade.
        /// Creates smooth shoreline transition instead of hard edge.
        /// </summary>
        private const float SHORELINE_BLEND_DISTANCE = 0.8f;

        /// <summary>
        /// Minimum depth for water to be fully opaque at shoreline.
        /// Below this depth, water fades out smoothly.
        /// </summary>
        private const float SHORELINE_FULL_DEPTH = 0.3f;

        /// <summary>
        /// Target water depth above the channel bed (meters).
        /// The normalization pass adjusts the surface so the depth along the
        /// channel centerline stays close to this value. Keeps water visually
        /// consistent instead of varying wildly with noisy terrain.
        /// </summary>
        private const float CHANNEL_TARGET_DEPTH = 1.2f;

        /// <summary>
        /// How aggressively depth normalization corrects inconsistencies (0-1).
        /// 0 = no correction, 1 = snap to target depth everywhere.
        /// 0.85 snaps almost fully to the flat cross-section level - real water
        /// is flat, so we enforce it strongly. The remaining 0.15 keeps a tiny
        /// amount of natural variation so the surface doesn't look machined.
        /// </summary>
        private const float DEPTH_NORM_STRENGTH = 0.85f;

        /// <summary>
        /// Minimum depth the normalization will enforce above the centerline bed.
        /// Water never thins out below this - prevents disappearing in shallow dips.
        /// </summary>
        private const float DEPTH_NORM_MIN = 0.3f;

        /// <summary>
        /// Maximum depth the normalization will allow above the centerline bed.
        /// Prevents overly deep pools where terrain dips create outlier depths.
        /// </summary>
        private const float DEPTH_NORM_MAX = 3.0f;

        /// <summary>
        /// Radius (in grid cells) for the centerline bed height scan.
        /// At each water cell we look +/- this many cells along the disk chain
        /// to find the representative bed height for that segment.
        /// 4 cells x ~0.33m/cell = ~1.3m scan radius - tight to local bed.
        /// </summary>
        private const int DEPTH_NORM_BED_SCAN_RADIUS = 4;

        #endregion

        #region Async Generation State

        /// <summary>
        /// Track if we're currently running async regeneration.
        /// </summary>
        private Coroutine _asyncRegenCoroutine;
        private bool _isRegeneratingAsync = false;

        /// <summary>
        /// Public property to check if async generation is in progress.
        /// </summary>
        public bool IsRegeneratingAsync => _isRegeneratingAsync;

        /// <summary>
        /// The calculated "settled" water level after gravity simulation.
        /// For ponds, this is the average Y of all disks.
        /// For rivers (high variance), this stays at -1 to use per-disk interpolation.
        /// </summary>
        private float _settledWaterLevel = -1f;

        /// <summary>
        /// Whether this water body is in "pond mode" (all disks at similar height).
        /// </summary>
        private bool _isPondMode = true;

        /// <summary>
        /// Minimum Y across all disks (for river mode interpolation).
        /// </summary>
        private float _minDiskY = float.MaxValue;

        /// <summary>
        /// Maximum Y across all disks (for river mode interpolation).
        /// </summary>
        private float _maxDiskY = float.MinValue;

        #endregion

        #region Cliff Data (Simplified)

        /// <summary>
        /// Cached data about cliff cells where water should stretch down.
        /// </summary>
        private struct CliffCellData
        {
            public int GridIndex;
            public Vector3 WorldPos;
            public Vector3 CliffDirection;
            public float StretchDistance;
            public float CliffAngle;
            public float TopY;
            public float BottomY;
            public bool IsInterDiskStretch;
        }

        private List<CliffCellData> _cliffCells = new List<CliffCellData>();

        #endregion

        #region Mesh Generation

        /// <summary>
        /// Regenerates the water mesh from all water disks.
        /// 
        /// v3 PROCESS (proper depth with edge curl):
        /// 1. Calculate combined bounds from all disks
        /// 2. Setup high-resolution grid
        /// 3. Sample TERRAIN heights at each grid point
        /// 4. Calculate water surface with edge curl effect
        /// 5. Apply smoothing passes
        /// 6. Build mesh
        /// </summary>
        private void RegenerateMesh()
        {
            // ── Volume settle/gel guards (round-56) ─────────────────────
            // Settling: the pour coroutine owns the grid + mesh; any regen
            // here would fight it mid-animation.
            // Gelled: the stored surface grid IS the data — rebuilding
            // through the IDW pipeline would clobber the settled shape
            // with disk-derived values. Route to the grid-authoritative
            // rebuild instead. See Docs/PLAN_WaterVolumePlacement.md.
            if (_isSettling) return;
            if (_isGelled)
            {
                RebuildFromStoredGrid();
                return;
            }

            if (_waterDisks.Count == 0)
            {
                _waterMesh?.Clear();
                return;
            }

            // Step 1: Calculate bounds
            CalculateCombinedBounds();

            // Step 2: Setup grid
            SetupGrid();

            // Step 3: Sample terrain heights
            SampleTerrainHeights();

            // Step 4: Calculate water surface with proper depth
            CalculateWaterSurfaceWithDepth();

            // Step 4.5: Gravity settle - pull down floating edges, fix buried water
            GravitySettleEdges();

            // Step 4.6: Channel depth normalization - maintain consistent depth
            // along river/stream centerlines using pre-sampled terrain heights.
            // Only active in river mode (ponds are already flat).
            NormalizeChannelDepth();

            // Step 4.7: Terrain clip - kill cells where terrain rises above the
            // computed water surface. Prevents flat sheets cutting through hills.
            TerrainClipPass();

            // Step 5: Apply smoothing passes
            SmoothWaterSurface();

            // Step 6: Calculate final depths
            CalculateWaterDepths();

            // Step 7: Build mesh
            BuildWaterMesh();

            // Step 8: Setup material
            SetupMaterial();

            // Step 9: Setup swim trigger
            SetupSwimTrigger();

            // Step 10: Detect cliff edges for VFX
            DetectCliffEdges();

            // Step 11: Spawn ambient VFX (shoreline, waterfall, debris, audio)
            SpawnAmbientVFX();

            // Step 12: refresh attached solid-surface controllers (ice/snow)
            // from the freshly-computed surface heights. Without this, NEW
            // Ice/Snow combiners get a controller attached in
            // GetOrCreateCombinerFor BEFORE RegenerateMesh runs, so the
            // controller's first Initialize() sees empty arrays and builds
            // no mesh / no collider.
            RefreshSecondaryMeshes();

            // VFX manager removed - auto VFX spawning disabled
            // Use manual VFX placement commands instead

            // Only log in debug mode to avoid spam during world load
            if (s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombiner] Regenerated mesh: {_waterDisks.Count} disks, grid {_gridWidth}x{_gridHeight}");
            }
        }

        /// <summary>
        /// Regenerates the mesh asynchronously over multiple frames.
        /// </summary>
        public void RegenerateMeshAsync()
        {
            // Round-56 volume guards — same rationale as RegenerateMesh:
            // settling combiners are owned by the pour coroutine; gelled
            // combiners rebuild straight from their stored grid.
            if (_isSettling) return;
            if (_isGelled)
            {
                RebuildFromStoredGrid();
                return;
            }

            if (_isRegeneratingAsync) return;

            if (_asyncRegenCoroutine != null)
            {
                StopCoroutine(_asyncRegenCoroutine);
            }

            _asyncRegenCoroutine = StartCoroutine(RegenerateMeshAsyncCoroutine());
        }

        /// <summary>
        /// Coroutine that spreads mesh regeneration across multiple frames.
        /// 
        /// ASYNC ARCHITECTURE (Phase 5):
        /// 1. Main thread: CalculateCombinedBounds + SetupGrid (fast)
        /// 2. Main thread coroutine: SampleTerrainHeightsAsync (requires Unity Physics/ZoneSystem)
        /// 3. Background thread: Surface calc + smoothing + depth + mesh building (pure math)
        /// 4. Main thread: Apply mesh + SetupMaterial + SetupSwimTrigger + DetectCliffEdges
        /// </summary>
        private IEnumerator RegenerateMeshAsyncCoroutine()
        {
            _isRegeneratingAsync = true;

            try
            {
                if (_waterDisks.Count == 0)
                {
                    _waterMesh?.Clear();
                    _isRegeneratingAsync = false;
                    yield break;
                }

                // Phase 1: Bounds + grid setup (fast, main thread)
                CalculateCombinedBounds();
                yield return null;

                SetupGrid();
                yield return null;

                // Phase 2: Terrain sampling (main thread coroutine - requires Unity API)
                yield return SampleTerrainHeightsAsync();

                // Phase 3: Submit surface calc + gravity settle + smoothing + mesh building to background thread
                // GravitySettleEdges is integrated into WaterAsyncMeshBuilder.ProcessWaterMeshJob
                var meshInput = WaterAsyncMeshBuilder.CreateInput(
                    this, _waterDisks,
                    _gridWidth, _gridHeight, _cellSize,
                    _gridOrigin, transform.position,
                    _terrainHeights,
                    BlendDistance,
                    _isPondMode, _settledWaterLevel,
                    _minDiskY, _maxDiskY,
                    WaveIntensity);

                bool jobComplete = false;
                WaterAsyncMeshBuilder.WaterMeshJobOutput meshResult = null;

                WaterAsyncMeshBuilder.QueueMeshJob(meshInput, output =>
                {
                    meshResult = output;
                    jobComplete = true;
                });

                // Wait for background thread to complete
                while (!jobComplete)
                    yield return null;

                // Phase 4: Apply result on main thread
                if (meshResult != null && !meshResult.HasError && meshResult.IsComplete)
                {
                    // Copy computed data back to combiner state
                    if (meshResult.SurfaceHeights != null)
                        System.Array.Copy(meshResult.SurfaceHeights, _surfaceHeights, _surfaceHeights.Length);
                    if (meshResult.WaterDepths != null)
                        System.Array.Copy(meshResult.WaterDepths, _waterDepths, _waterDepths.Length);

                    // Apply to Unity Mesh
                    _waterMesh.Clear();
                    if (FiresLogger.VerboseEnabled) FiresLogger.LogInfo($"[WaterMeshCombiner] PATH 1 (async-worker): mesh result has verts={meshResult.Vertices?.Length ?? -1} tris={meshResult.Triangles?.Length ?? -1} shellEnabled={WaterBalloonShell.Enabled}");
                    if (meshResult.Vertices != null && meshResult.Vertices.Length > 0 && meshResult.Triangles.Length > 0)
                    {
                        // Round-W-4 — "balloon shell" pass. Expands the
                        // top surface into a closed volume (top + bottom
                        // + perimeter walls) so the water reads as a 3D
                        // body of water rather than a flat sheet. The
                        // bottom face follows per-vertex water depths so
                        // the shell tightens to nothing at the shore and
                        // bulges out where the surface is far above
                        // terrain. Waterfalls (small thin disks on a
                        // slope) get a back face that makes them
                        // viewable from all angles instead of a
                        // single-sided sheet.
                        //
                        // Toggleable via `va_water_balloon_shell 0/1` in
                        // case it causes regressions on a megabase.
                        if (WaterBalloonShell.Enabled && meshResult.Vertices.Length > 0)
                        {
                            var shell = WaterBalloonShell.Expand(
                                meshResult.Vertices,
                                meshResult.Triangles,
                                meshResult.UVs,
                                meshResult.Normals,
                                meshResult.Colors,
                                meshResult.WaterDepths);
                            _waterMesh.SetVertices(shell.Vertices);
                            _waterMesh.SetTriangles(shell.Triangles, 0);
                            _waterMesh.SetUVs(0, shell.UVs);
                            _waterMesh.SetNormals(shell.Normals);
                            _waterMesh.SetColors(shell.Colors);
                            _waterMesh.RecalculateBounds();
                            // Skip RecalculateNormals — the shell already
                            // authored consistent top/bottom/wall normals.
                        }
                        else
                        {
                            _waterMesh.SetVertices(meshResult.Vertices);
                            _waterMesh.SetTriangles(meshResult.Triangles, 0);
                            _waterMesh.SetUVs(0, meshResult.UVs);
                            _waterMesh.SetNormals(meshResult.Normals);
                            _waterMesh.SetColors(meshResult.Colors);
                            _waterMesh.RecalculateBounds();
                            _waterMesh.RecalculateNormals();
                        }
                    }
                }
                else
                {
                    // Fallback: sync path if background job failed
                    string error = meshResult?.ErrorMessage ?? "unknown";
                    FiresLogger.LogWarning($"[WaterMeshCombiner] Background water mesh job failed ({error}), falling back to sync path");
                    CalculateWaterSurfaceWithDepth();
                    GravitySettleEdges();
                    NormalizeChannelDepth();
                    TerrainClipPass();
                    SmoothWaterSurface();
                    CalculateWaterDepths();
                    BuildWaterMesh();
                }

                yield return null;

                SetupMaterial();
                yield return null;

                SetupSwimTrigger();

                DetectCliffEdges();
                yield return null;

                // Spawn ambient VFX (shoreline, waterfall, debris, audio)
                SpawnAmbientVFX();

                // Refresh attached solid-surface controllers (ice/snow) —
                // mirrors the sync RegenerateMesh tail.
                RefreshSecondaryMeshes();

                if (s_verboseLogging)
                {
                    FiresLogger.LogInfo($"[WaterMeshCombiner] Async mesh complete: {_waterDisks.Count} disks, grid {_gridWidth}x{_gridHeight}");
                }
            }
            finally
            {
                _isRegeneratingAsync = false;
                _asyncRegenCoroutine = null;
            }
        }

        /// <summary>
        /// Calculates combined bounds from all water disks.
        /// Also determines the "settled" water level (gravity simulation).
        /// </summary>
        private void CalculateCombinedBounds()
        {
            if (_waterDisks.Count == 0) return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            // Calculate area-weighted average Y (water level settling)
            float totalArea = 0f;
            float weightedYSum = 0f;
            _minDiskY = float.MaxValue;
            _maxDiskY = float.MinValue;

            foreach (var disk in _waterDisks)
            {
                Vector3 pos = disk.Position;

                // Get shape-aware extents for bounds calculation
                disk.GetHalfExtents(out float halfW, out float halfL);
                float area;

                if (disk.Shape == WaterDiskShape.Circle)
                {
                    area = Mathf.PI * disk.Radius * disk.Radius;
                }
                else
                {
                    area = disk.Width * disk.Length;
                }

                minX = Mathf.Min(minX, pos.x - halfW);
                maxX = Mathf.Max(maxX, pos.x + halfW);
                minZ = Mathf.Min(minZ, pos.z - halfL);
                maxZ = Mathf.Max(maxZ, pos.z + halfL);
                minY = Mathf.Min(minY, pos.y - disk.MaxDepth);
                maxY = Mathf.Max(maxY, pos.y + 2f);

                // Track for water level settling
                weightedYSum += pos.y * area;
                totalArea += area;
                _minDiskY = Mathf.Min(_minDiskY, pos.y);
                _maxDiskY = Mathf.Max(_maxDiskY, pos.y);
            }

            // 2026-05-27 round-W-5 — RULER-Y AUTHORITY. User spec:
            //   "we should be able to set the height of our water with
            //    our infinity hammer ruler and thats the height it
            //    should place at period, the settle with gravity is
            //    only meant to be for letting the skirts settle and
            //    for it to smooth out over slopes"
            //
            // Previously POND MODE averaged every disk's Y into a
            // single _settledWaterLevel — so if the player placed two
            // disks at different ruler heights, both moved to the
            // average. That broke "place at exactly Y" semantics.
            //
            // Now always use per-point interpolation (formerly RIVER
            // MODE): the surface at each grid cell is an IDW-weighted
            // blend of the nearest disks' own Ys, so a single disk
            // renders at its placed Y, two disks at different heights
            // give a natural slope between them, and the bank/skirt
            // settling (in TerrainClipPass / GravitySettleEdges below)
            // still handles the meet-the-terrain rounding the user
            // does want.
            float heightVariance = _maxDiskY - _minDiskY;
            _settledWaterLevel = -1f;
            _isPondMode = false;
            if (s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombiner] RULER-Y MODE: Y range {_minDiskY:F2} to {_maxDiskY:F2} (variance={heightVariance:F2}m) — per-disk Y respected, no pond averaging.");
            }

            // Add padding
            minX -= GridPadding;
            maxX += GridPadding;
            minZ -= GridPadding;
            maxZ += GridPadding;

            _combinedBounds = new Bounds();
            _combinedBounds.SetMinMax(
                new Vector3(minX, minY, minZ),
                new Vector3(maxX, maxY, maxZ)
            );

            // Position combiner at center, Y at settled water level or average
            float combinerY = _isPondMode ? _settledWaterLevel : (_minDiskY + _maxDiskY) * 0.5f;
            transform.position = new Vector3(
                _combinedBounds.center.x,
                combinerY,
                _combinedBounds.center.z
            );
        }

        /// <summary>
        /// Sets up grid dimensions based on bounds.
        /// </summary>
        private void SetupGrid()
        {
            float width = _combinedBounds.size.x;
            float height = _combinedBounds.size.z;

            int idealWidth = Mathf.CeilToInt(width * WATER_VERTS_PER_METER) + 1;
            int idealHeight = Mathf.CeilToInt(height * WATER_VERTS_PER_METER) + 1;

            if (idealWidth > MAX_GRID_SIZE || idealHeight > MAX_GRID_SIZE)
            {
                float requiredCellSizeX = width / (MAX_GRID_SIZE - 1);
                float requiredCellSizeZ = height / (MAX_GRID_SIZE - 1);
                _cellSize = Mathf.Max(requiredCellSizeX, requiredCellSizeZ);

                _gridWidth = Mathf.Min(Mathf.CeilToInt(width / _cellSize) + 1, MAX_GRID_SIZE);
                _gridHeight = Mathf.Min(Mathf.CeilToInt(height / _cellSize) + 1, MAX_GRID_SIZE);
            }
            else
            {
                _cellSize = 1f / WATER_VERTS_PER_METER;
                _gridWidth = idealWidth;
                _gridHeight = idealHeight;
            }

            _gridOrigin = new Vector3(_combinedBounds.min.x, 0, _combinedBounds.min.z);

            int gridSize = _gridWidth * _gridHeight;
            _surfaceHeights = new float[gridSize];
            _terrainHeights = new float[gridSize];
            _waterDepths = new float[gridSize];
        }

        #endregion

        #region Terrain Sampling

        // 0.2.683 ([lead] H10 follow-up, 10-01): the terrain under the water, from VoxelWorld's EDITED density where it is
        // loaded. A VoxelWrap dedicated server builds voxel data (VAVoxel edits imported per zone) but no voxel colliders,
        // so GetGroundHeight there is vanilla's uncarved ground and a carved pond read dry in the server's grid. The
        // density read is a dictionary lookup plus one column scan: it never builds a zone, only answers where a column is
        // already loaded, and falls back to the old sample otherwise. The window runs from just above this combiner's
        // highest disk surface (so an overhang above the water is ignored) down VoxelProbeBelow metres.
        private const float VoxelProbeAbove = 2f, VoxelProbeBelow = 80f;
        private float _voxelProbeTop = float.NaN;
        private static bool s_voxelSampleLogged;

        // 0.2.685 (H12: no "water grids sample the edited voxel ground" line on the dedi): on a dedicated server, every grid
        // build reports how many of its cells the voxel density answered, so "the server never builds a grid" and "it builds
        // one but no column is loaded" can be told apart. One line per build; builds are rare (create, restore, reseat).
        private int _voxelAnswered, _voxelAsked;

        private void ReportServerSample()
        {
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return;
            FiresLogger.LogDebug($"[FiresWater] server water grid at ({transform.position.x:F0},{transform.position.y:F1},{transform.position.z:F0}): "
                + $"{_waterDisks.Count} disk(s), voxel density answered {_voxelAnswered}/{_voxelAsked} cell(s)"
                + (float.IsNaN(_voxelProbeTop) ? " (no disk to set the probe window)" : $" in y {_voxelProbeTop - VoxelProbeBelow:F0}..{_voxelProbeTop:F0}")
                + (_voxelAnswered == 0 ? "; the rest use the vanilla ground (no voxel column loaded here)" : "") + ".");
        }

        private void SetVoxelProbeTop()
        {
            _voxelAnswered = _voxelAsked = _rimCells = _moppedCells = 0;
            _voxelProbeTop = float.NaN;
            foreach (var d in _waterDisks)
                if (d != null && (float.IsNaN(_voxelProbeTop) || d.Position.y + VoxelProbeAbove > _voxelProbeTop))
                    _voxelProbeTop = d.Position.y + VoxelProbeAbove;
        }

        private bool TryVoxelGround(Vector3 worldPos, out float ground)
        {
            ground = 0f;
            if (float.IsNaN(_voxelProbeTop)) return false;
            _voxelAsked++;
            if (!VerdantsAscent.Terrain.Voxel.VoxelWorld.TrySampleSurfaceY(worldPos.x, worldPos.z, _voxelProbeTop, _voxelProbeTop - VoxelProbeBelow,
                    out float surf, out bool solidAtTop))
            {
                // Solid at the top of the window: the bank stands above the water there (a dry cell).
                if (!solidAtTop) return false;
                surf = _voxelProbeTop;
            }
            _voxelAnswered++;
            ground = surf;
            if (!s_voxelSampleLogged)
            {
                s_voxelSampleLogged = true;
                float gen = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(worldPos.x, worldPos.z) : float.NaN;
                FiresLogger.LogInfo($"[FiresWater] water grids sample the edited voxel ground (first at ({worldPos.x:F0},{worldPos.z:F0}): "
                    + $"y {surf:F1}{(solidAtTop ? " (bank above the water)" : "")}, generated {gen:F1}; "
                    + $"{(ZNet.instance != null && ZNet.instance.IsDedicated() ? "dedicated server" : "this peer")}); first of this session.");
            }
            return true;
        }

        private void SampleTerrainHeights()
        {
            SetVoxelProbeTop();
            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    Vector3 worldPos = GridToWorld(x, z);
                    int idx = z * _gridWidth + x;
                    _terrainHeights[idx] = SampleTerrainHeightAt(worldPos);
                }
            }
            ReportServerSample();
        }

        private IEnumerator SampleTerrainHeightsAsync()
        {
            const int BATCH_SIZE = 500;
            int processed = 0;
            SetVoxelProbeTop();

            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    Vector3 worldPos = GridToWorld(x, z);
                    int idx = z * _gridWidth + x;
                    _terrainHeights[idx] = SampleTerrainHeightAt(worldPos);

                    processed++;
                    if (processed % BATCH_SIZE == 0)
                    {
                        yield return null;
                    }
                }
            }
            ReportServerSample();
        }

        /// <summary>
        /// Cached terrain-only layer mask (excludes buildings, vegetation, objects).
        /// </summary>
        private static int s_terrainOnlyMask = -1;

        // 0.2.715 (Fire 10-05: our own rivers and pools on the sky islands): past a sky island's rim the voxel window finds
        // nothing and the old fallback answered the world ground hundreds of metres down, so a pour or pool draped its sheet
        // down to it. Ground deeper than the whole probe window under the water is no pond floor: that cell is the edge,
        // and it reads dry (as high as the window top). A fall over the edge is the Vfall tool's curtain, not a drape.
        private int _rimCells, _moppedCells;

        // The saved-grid restore builds without sampling: dry the mopped cells of the restored surface the same way.
        private int DryMoppedCells()
        {
            if (!VerdantsAscent.Terrain.Voxel.VoxelWorld.AnyMops || _surfaceHeights == null) return 0;
            int dried = 0;
            for (int z = 0; z < _gridHeight; z++)
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    if (idx >= _surfaceHeights.Length || _surfaceHeights[idx] <= -50f) continue;
                    Vector3 p = GridToWorld(x, z);
                    if (!VerdantsAscent.Terrain.Voxel.VoxelWorld.IsMoppedAt(p.x, _surfaceHeights[idx], p.z)) continue;
                    _surfaceHeights[idx] = -100f;
                    if (_waterDepths != null && idx < _waterDepths.Length) _waterDepths[idx] = 0f;
                    dried++;
                }
            return dried;
        }

        // Vmop on this peer, or a chunk whose mop mask changed arriving from another: rebuild this water from its disks
        // (the mask is read while it samples), locally only - no anchor save. Returns how many it rebuilt.
        internal static int RefreshMopsIn(Vector3 center, float radius)
        {
            int n = 0;
            foreach (var c in new List<WaterMeshCombiner>(s_allCombiners))
            {
                if (c == null || c.WaterDisks == null || c.WaterDisks.Count == 0) continue;
                var b = c.GetCombinedBounds();
                float dx = Mathf.Max(b.min.x - center.x, 0f, center.x - b.max.x);
                float dz = Mathf.Max(b.min.z - center.z, 0f, center.z - b.max.z);
                if (dx * dx + dz * dz > radius * radius) continue;
                // A sky island's water and the ground's below share XZ: only the one near the mop's height.
                float dy = Mathf.Max(b.min.y - center.y, 0f, center.y - b.max.y);
                if (dy > radius + 32f) continue;
                c.RegenerateMesh();
                n++;
            }
            return n;
        }

        private float SampleTerrainHeightAt(Vector3 worldPos)
        {
            // 0.2.715 Vmop: a column the mop mask covers at this water's height is bank, never water, so hand-drawn water
            // stays out of it on every rebuild, on every peer (the mask persists on the voxel chunk anchors).
            if (!float.IsNaN(_voxelProbeTop)
                && VerdantsAscent.Terrain.Voxel.VoxelWorld.IsMoppedAt(worldPos.x, _voxelProbeTop - VoxelProbeAbove, worldPos.z))
            {
                _moppedCells++;
                return _voxelProbeTop;
            }
            float h = SampleTerrainHeightBelow(worldPos);
            if (!float.IsNaN(_voxelProbeTop) && h < _voxelProbeTop - VoxelProbeBelow)
            {
                if (_rimCells++ == 0)
                    FiresLogger.LogDebug($"[FiresWater] water grid at ({transform.position.x:F0},{transform.position.y:F1},{transform.position.z:F0}): "
                        + $"ground at ({worldPos.x:F0},{worldPos.z:F0}) is {h:F1}, below the {VoxelProbeBelow:F0} m window under the water "
                        + $"(top {_voxelProbeTop:F1}) - an island rim, read dry instead of draping.");
                return _voxelProbeTop;
            }
            return h;
        }

        private float SampleTerrainHeightBelow(Vector3 worldPos)
        {
            // PHASE F+: custom-tile FIRST, with a small XZ search radius
            // so that grid samples just past the modified part of a tile
            // (or just past the platform edge) STILL pick up the tile Y
            // rather than draping down to vanilla. Without this widen,
            // water disks placed on a sky-built platform tried to drape
            // down to vanilla terrain 30-50m below at the edges, producing
            // huge waterfalls instead of a clean flood-fill pool.
            //
            // 2026-05-25 user complaint: "make sure that the water mesh
            // doesnt try to automatically pull down to meet vanilla
            // terrain when placed on custom".
            //
            // Search radius is small (~2m) so the effect only kicks in
            // when there's a custom tile RIGHT THERE — actually-off-tile
            // samples still pick up vanilla and the water gets a clean
            // cliff transition at the platform's true edge.
            if (TryGetNearestCustomHeight(worldPos, searchRadius: 2f, out float customY))
            {
                return customY;
            }

            // 0.2.683: the edited voxel ground where it is loaded (see TryVoxelGround).
            if (TryVoxelGround(worldPos, out float voxelY))
                return voxelY;

            float baseTerrainHeight;

            // PRIMARY: Use WorldGenerator.GetHeight() for pure procedural terrain.
            // This returns the exact terrain height including river carving (AddRivers),
            // biome height, and Better Continents modifications - but WITHOUT any
            // player-placed objects, rocks, buildings, or vegetation that would
            // inflate the height and cause water to ride up bank edges.
            if (WorldGenerator.instance != null)
            {
                float procHeight = WorldGenerator.instance.GetHeight(worldPos.x, worldPos.z);

                // Cross-reference with ZoneSystem for terrain modifications (flatten, raise)
                // that players may have applied. Use the LOWER of the two so water
                // always sits in the deepest available channel.
                if (ZoneSystem.instance != null &&
                    ZoneSystem.instance.GetGroundHeight(worldPos, out float groundHeight))
                {
                    baseTerrainHeight = Mathf.Min(procHeight, groundHeight);
                }
                else
                {
                    baseTerrainHeight = procHeight;
                }
            }
            else
            {
                // FALLBACK: ZoneSystem ground height (includes terrain mods)
                baseTerrainHeight = worldPos.y - 100f;

                if (ZoneSystem.instance != null &&
                    ZoneSystem.instance.GetGroundHeight(worldPos, out float groundHeight))
                {
                    baseTerrainHeight = groundHeight;
                }

                // LAST RESORT: Terrain-only raycast (no buildings/objects/vegetation).
                // Only hits the "terrain" layer to avoid false height from placed objects.
                if (s_terrainOnlyMask == -1)
                    s_terrainOnlyMask = LayerMask.GetMask("terrain");

                Vector3 rayStart = new Vector3(worldPos.x, worldPos.y + 50f, worldPos.z);
                if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 100f, s_terrainOnlyMask))
                {
                    baseTerrainHeight = Mathf.Max(baseTerrainHeight, hit.point.y);
                }
            }

            return baseTerrainHeight;
        }

        /// <summary>
        /// Searches a small XZ radius around `worldPos` for any custom tile
        /// height. Returns true + the highest tile Y found within the
        /// search radius. False if no tile data is anywhere nearby.
        ///
        /// The widen lets water sampling stay on a custom platform even
        /// when the disk's grid points fall slightly outside the modified
        /// part of a tile (or just past the platform's true edge). Without
        /// it the water mesh tried to drape down to vanilla at the edges.
        ///
        /// Cost is bounded: at radius=2m we sample 5 points (center +
        /// 4 cardinal). Each sample is a tile-registry lookup (O(1)).
        /// </summary>
        private static bool TryGetNearestCustomHeight(Vector3 worldPos, float searchRadius, out float height)
        {
            height = float.MinValue;
            try
            {
                // Direct hit first — most common case.
                if (VerdantsAscent.Terrain.Persistence.TileAnchorRegistry
                        .TrySampleHeightAtWorld(worldPos, out float direct))
                {
                    height = direct;
                    return true;
                }

                // Small ring of 4 samples around the point. Use the HIGHEST
                // tile Y found — water should pool ABOVE the platform, so
                // when in doubt we prefer "platform is here" over "vanilla
                // is here", matching the user's flood-fill intent.
                float r = Mathf.Max(searchRadius, 0.5f);
                var samples = new[]
                {
                    new Vector3(worldPos.x + r, worldPos.y, worldPos.z),
                    new Vector3(worldPos.x - r, worldPos.y, worldPos.z),
                    new Vector3(worldPos.x,     worldPos.y, worldPos.z + r),
                    new Vector3(worldPos.x,     worldPos.y, worldPos.z - r),
                };
                bool found = false;
                foreach (var s in samples)
                {
                    if (VerdantsAscent.Terrain.Persistence.TileAnchorRegistry
                            .TrySampleHeightAtWorld(s, out float h))
                    {
                        if (!found || h > height) { height = h; found = true; }
                    }
                }
                return found;
            }
            catch
            {
                // Defensive: never let custom-terrain sampling break water mesh gen.
                return false;
            }
        }

        #endregion

        #region Water Surface with Gravity Settling (v4)

        /// <summary>
        /// Calculates water surface using gravity settling model.
        /// 
        /// KEY BEHAVIOR (v4):
        /// - POND MODE: All water at a single settled level (like a real pond)
        /// - RIVER MODE: Water level interpolates smoothly between disk heights
        /// - Water CLIPS through terrain at banks (correct behavior)
        /// - No more "edge curl" that caused bumpy stacked pancakes
        /// 
        /// The surface is essentially a FLAT plane (or smooth gradient for rivers)
        /// that only renders where it's above terrain.
        /// </summary>
        private void CalculateWaterSurfaceWithDepth()
        {
            if (_waterDisks.Count == 0) return;

            var diskData = CacheDiskData();

            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    Vector3 worldPos = GridToWorld(x, z);

                    _surfaceHeights[idx] = CalculateSurfaceWithGravity(worldPos, idx, diskData);
                }
            }

            // Only fill very small gaps (1-2 cells), not larger areas
            FillSurfaceGaps();
        }

        private IEnumerator CalculateWaterSurfaceWithDepthAsync()
        {
            if (_waterDisks.Count == 0) yield break;

            var diskData = CacheDiskData();

            const int BATCH_SIZE = 500;
            int processed = 0;

            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    Vector3 worldPos = GridToWorld(x, z);

                    _surfaceHeights[idx] = CalculateSurfaceWithGravity(worldPos, idx, diskData);

                    processed++;
                    if (processed % BATCH_SIZE == 0)
                    {
                        yield return null;
                    }
                }
            }

            yield return null;
            FillSurfaceGaps();
        }

        /// <summary>
        /// Caches disk data for fast sampling.
        /// </summary>
        private CachedWaterDiskData[] CacheDiskData()
        {
            var diskData = new CachedWaterDiskData[_waterDisks.Count];
            for (int i = 0; i < _waterDisks.Count; i++)
            {
                var disk = _waterDisks[i];
                float depth = disk.MaxDepth > 0.1f ? disk.MaxDepth : DEFAULT_MAX_DEPTH;

                disk.GetHalfExtents(out float halfW, out float halfL);

                diskData[i] = new CachedWaterDiskData
                {
                    PosX = disk.Position.x,
                    PosY = disk.Position.y,
                    PosZ = disk.Position.z,
                    Radius = disk.Radius,
                    RadiusPlusBlend = disk.Radius + BlendDistance,
                    MaxDepth = depth,
                    EdgeFadeStart = disk.Radius - EDGE_FADE_DISTANCE,
                    CenterChannelRadius = disk.Radius * CENTER_CHANNEL_RADIUS_FRACTION,
                    Shape = disk.Shape,
                    HalfWidth = halfW,
                    HalfLength = halfL
                };
            }
            return diskData;
        }

        /// <summary>
        /// Calculates water surface at a point using gravity settling model.
        /// 
        /// v4.2 LOGIC (with bank extension + shape support + pond-river curling):
        /// 1. Check if point is within any disk's shape bounds (with soft edge fade)
        /// 2. Calculate the water level at this point:
        ///    - POND MODE: Use the single settled level
        ///    - RIVER MODE: Interpolate based on nearby disk heights
        /// 3. POND-RIVER CURL: If a higher disk overhangs a lower one nearby,
        ///    curl the higher water surface down to meet the lower water body
        /// 4. BANK EXTENSION: When terrain is above water, still create vertices
        ///    that extend under the terrain - this hides the sharp "staircase" edge
        /// 5. Water CLIPS through banks - terrain naturally hides the mesh edge
        /// </summary>
        private float CalculateSurfaceWithGravity(Vector3 worldPos, int gridIdx, CachedWaterDiskData[] diskData)
        {
            float terrainY = _terrainHeights[gridIdx];

            // Find contribution from all disks
            float totalWeight = 0f;
            bool hasContribution = false;
            float maxDepth = 0f;
            float minDistToEdge = float.MaxValue;

            // Track the nearest disk Y as a fallback for river mode
            float nearestCenterDist = float.MaxValue;
            float nearestDiskY = 0f;

            for (int d = 0; d < diskData.Length; d++)
            {
                ref var cached = ref diskData[d];

                float distToEdge = GetEdgeDistance(worldPos.x, worldPos.z, ref cached);

                if (distToEdge < minDistToEdge)
                    minDistToEdge = distToEdge;

                float maxExtendedDist = BlendDistance + BANK_EXTENSION_DISTANCE;
                if (distToEdge > maxExtendedDist)
                    continue;

                // 2026-05-26 round-22: ALWAYS track the nearest disk for
                // any disk within maxExtendedDist, regardless of which
                // weight branch the cell falls into. Previously only
                // the `weight > 0.001f` branch updated nearestCenterDist;
                // cells in the bank-extension overflow ring (where the
                // disk passed only the bank-weight branch) left
                // nearestCenterDist=float.MaxValue. That cascaded into
                // paintedSurfaceY falling through to terrainY+0.02 below,
                // which neutered the round-17 kill threshold and let
                // bank-extension write cliff-height vertices — the
                // upward mesh spikes the user has reported multiple times.
                if (!_isPondMode)
                {
                    float cdx0 = worldPos.x - cached.PosX;
                    float cdz0 = worldPos.z - cached.PosZ;
                    float centerDist0 = Mathf.Sqrt(cdx0 * cdx0 + cdz0 * cdz0);

                    if (centerDist0 < nearestCenterDist)
                    {
                        nearestCenterDist = centerDist0;
                        nearestDiskY = cached.PosY;
                    }
                }

                float weight = CalculateBlendWeight(distToEdge);

                if (weight > 0.001f)
                {
                    hasContribution = true;
                    totalWeight += weight;
                    maxDepth = Mathf.Max(maxDepth, cached.MaxDepth);
                }
                else if (distToEdge > 0f && distToEdge <= maxExtendedDist)
                {
                    hasContribution = true;
                    float bankWeight = 0.1f * (1f - (distToEdge - BlendDistance) / BANK_EXTENSION_DISTANCE);
                    bankWeight = Mathf.Max(0f, bankWeight);
                    if (bankWeight > 0.001f)
                    {
                        totalWeight += bankWeight;
                        maxDepth = Mathf.Max(maxDepth, cached.MaxDepth);
                    }
                }
            }

            if (!hasContribution || totalWeight < 0.001f)
            {
                return -100f;
            }

            float surfaceY;

            if (_isPondMode)
            {
                surfaceY = _settledWaterLevel;
            }
            else
            {
                // RIVER MODE: Inverse-distance weighted blending.
                // Each disk's Y is authoritative at its center. In overlap zones,
                // the surface smoothly blends between disk heights using inverse
                // distance weighting. This respects the placement height set by
                // the Infinity Hammer ruler - a disk placed at Y=40 stays at Y=40
                // near its center, even if a neighboring disk is at Y=35.
                float weightedY = 0f;
                float totalIDW = 0f;

                for (int d = 0; d < diskData.Length; d++)
                {
                    ref var cached = ref diskData[d];
                    float edgeDist = GetEdgeDistance(worldPos.x, worldPos.z, ref cached);

                    // Only consider disks that contribute to this cell
                    if (edgeDist > BlendDistance + BANK_EXTENSION_DISTANCE)
                        continue;

                    float cdx = worldPos.x - cached.PosX;
                    float cdz = worldPos.z - cached.PosZ;
                    float centerDist = Mathf.Sqrt(cdx * cdx + cdz * cdz);

                    // Inverse-distance weight: w = 1 / (dist + epsilon)^2
                    // The squared falloff gives strong authority at the center
                    // and smooth blending at the edges.
                    float epsilon = cached.Radius * 0.3f; // Soften near-center singularity
                    float idw = 1f / ((centerDist + epsilon) * (centerDist + epsilon));

                    weightedY += cached.PosY * idw;
                    totalIDW += idw;
                }

                if (totalIDW > 0.001f)
                {
                    surfaceY = weightedY / totalIDW;
                }
                else if (nearestCenterDist < float.MaxValue - 1f)
                {
                    surfaceY = nearestDiskY;
                }
                else
                {
                    surfaceY = terrainY + WATER_TERRAIN_OFFSET;
                }

                // NOTE: No terrain cap - the disk Y IS the intended water level.
                // The user set this height via the IH ruler; we must respect it.
                // Terrain clip pass (step 4.7) will remove cells where terrain
                // actually rises above the surface.
            }

            // 2026-05-25 round-17: capture the IDW-blended PAINTED water
            // surface BEFORE any modifications (edge-fade, bank-extension,
            // pond-river curl). This is the user's intended water height
            // from the IH ruler — bank-extension uses it as a CEILING so
            // it never pushes the mesh UP onto cliff terrain.
            //
            // 2026-05-26 round-22: paintedSurfaceY MUST be the disk's
            // intended height. If surfaceY came from the third fallback
            // at line 980 (`terrainY + WATER_TERRAIN_OFFSET`), the
            // downstream kill threshold (`terrainY > paintedSurfaceY +
            // 0.75f`) becomes `terrainY > terrainY + 0.77f` (impossible),
            // so the spike rejection never fires for the exact cells
            // that produce the spikes. Prefer nearestDiskY — now always
            // set when hasContribution=true thanks to the round-22
            // tracking lift above. Only fall back to surfaceY in the
            // (now-extremely-rare) case nearestCenterDist is still
            // unset, which means a non-pond cell with no disks in range
            // at all — and that path returned -100f earlier.
            float paintedSurfaceY = (nearestCenterDist < float.MaxValue - 1f)
                ? nearestDiskY
                : surfaceY;

            // POND-RIVER CURL: Check if other water combiners at a different height
            // overlap near this point. If so, curl the surface down to meet them.
            // This handles the case where a pond overhangs a river at a lower elevation.
            surfaceY = ApplyPondRiverCurl(worldPos, surfaceY, minDistToEdge);

            // 2026-05-26 round-26: EDGE FADE TO TERRAIN REMOVED.
            //
            // The fade attempted to smoothly slope the water surface down
            // to terrain at the boundary, but it interacted badly with
            // the marching-squares interpolation between inside and
            // outside cells — the inside cell ended up faded to terrain,
            // the outside was kill (-100), and the interpolated boundary
            // crossing's Y had no relationship to terrain at the crossing's
            // actual XZ. When terrain at the crossing was higher than the
            // crossing's interpolated Y, the bank-extension pass then wrote
            // a vertex at terrain-height — producing the upward fin
            // spikes the user kept reporting (same root cause as the
            // LandSnow edge-fade pathology, fixed by removing the fade).
            //
            // The water mesh now ends flat at the disk surface; the
            // bank-extension pass below still tucks the mesh UNDER terrain
            // when terrain rises slightly above water (sloped shore),
            // and the kill threshold drops the cell entirely when terrain
            // rises sharply (cliff). Terrain (opaque) visually clips
            // the boundary. Same approach FloatingIce uses — which the
            // user confirmed works.

            // Optional: Add very subtle center channel for boat navigation
            // Only if enabled and we have good coverage
            if (CENTER_CHANNEL_DEPTH > 0f && totalWeight > 0.5f && minDistToEdge < -EDGE_FADE_DISTANCE)
            {
                // We're well inside the water (negative minDistToEdge means inside disk)
                float centeredness = Mathf.Clamp01(-minDistToEdge / 3f);
                centeredness = centeredness * centeredness; // Ease in
                surfaceY -= centeredness * CENTER_CHANNEL_DEPTH;
            }

            // BANK EXTENSION LOGIC:
            // Instead of cutting off water where terrain is above it (creates staircase edge),
            // we extend the water mesh UNDER the terrain. The terrain naturally clips/hides it.

            float minWaterY = terrainY + WATER_TERRAIN_OFFSET;

            if (surfaceY <= minWaterY)
            {
                // 2026-05-25 round-17: if terrain rises FAR above the
                // PAINTED water level (cliff face, tall rock, tree base),
                // don't try to bank-extend up — kill the cell so the mesh
                // ends cleanly instead of poking up as a vertical sliver.
                // Tolerance is small so we still bank-extend across normal
                // shoreline bumps, but reject cliff-scale terrain.
                const float MAX_BANK_RISE_ABOVE_PAINTED = 0.75f;
                if (terrainY > paintedSurfaceY + MAX_BANK_RISE_ABOVE_PAINTED)
                {
                    return -100f;
                }

                // Terrain is above the water level here (but not by much).
                // Check if we're close enough to water to extend the mesh
                // under terrain.

                // minDistToEdge < 0 means we're INSIDE a disk
                // minDistToEdge > 0 means we're OUTSIDE all disks
                bool isNearWater = minDistToEdge < BANK_EXTENSION_DISTANCE;

                if (isNearWater)
                {
                    // BANK EXTENSION: Create a water vertex that dips below terrain
                    // This extends the mesh under the bank so terrain clips it naturally
                    // The further into the bank, the lower we push it
                    float bankPenetration = Mathf.Clamp01((BANK_EXTENSION_DISTANCE - minDistToEdge) / BANK_EXTENSION_DISTANCE);

                    // Push water below terrain - deeper as we go further into bank
                    float bankSurfaceY = terrainY - BANK_DEPTH_BELOW_TERRAIN * bankPenetration;

                    // But don't go below the actual water depth limit
                    float absoluteMinY = surfaceY - maxDepth;
                    bankSurfaceY = Mathf.Max(bankSurfaceY, absoluteMinY);

                    // 2026-05-25 round-17: CEILING — never push the bank
                    // vertex ABOVE the painted water surface. Otherwise a
                    // slight slope above water level produces a vertex
                    // higher than the painted Y → spike. Min() against
                    // paintedSurfaceY keeps the bank mesh flat-or-below.
                    bankSurfaceY = Mathf.Min(bankSurfaceY, paintedSurfaceY);

                    return bankSurfaceY;
                }
                else
                {
                    // Too far from water - no water here
                    return -100f;
                }
            }

            // Normal case: water surface is above terrain, return it directly
            return surfaceY;
        }

        /// <summary>
        /// Curls the water surface down at edges where this combiner's water body
        /// overhangs another water body at a lower elevation.
        /// 
        /// This creates a smooth transition between pond and river meshes,
        /// preventing the visual gap seen when a pond at a higher elevation
        /// has its edge directly above a river at a lower elevation.
        /// </summary>
        private float ApplyPondRiverCurl(Vector3 worldPos, float surfaceY, float distToEdge)
        {
            // Only curl near the edge of the water body (within 3m of edge)
            const float CURL_DETECTION_RANGE = 3f;
            if (distToEdge < -CURL_DETECTION_RANGE)
                return surfaceY; // Too far inside - no curling needed

            // Search for other water combiners that overlap at a different height
            float curlTargetY = surfaceY;
            bool foundLowerWater = false;

            foreach (var otherCombiner in s_allCombiners)
            {
                if (otherCombiner == null || otherCombiner == this) continue;

                // Quick bounds check - is the other combiner anywhere near this point?
                if (!otherCombiner._combinedBounds.Contains(new Vector3(worldPos.x, otherCombiner._combinedBounds.center.y, worldPos.z)))
                    continue;

                // Check the other combiner's water level at this position
                float otherSurface = otherCombiner.GetWaterSurfaceAt(worldPos);
                if (otherSurface <= -50f) continue; // No water at this point in other combiner

                // Only curl DOWN (high water meeting low water)
                float heightDiff = surfaceY - otherSurface;
                if (heightDiff < 0.3f) continue; // Need at least 0.3m difference to curl

                foundLowerWater = true;
                if (otherSurface < curlTargetY)
                {
                    curlTargetY = otherSurface;
                }
            }

            if (!foundLowerWater) return surfaceY;

            // Calculate curl factor based on how close we are to the edge
            // At edge (distToEdge=0): full curl to lower water
            // At CURL_DETECTION_RANGE inside: no curl at all
            float curlFactor;
            if (distToEdge >= 0f)
            {
                // Outside or exactly at edge - full curl
                curlFactor = 1f;
            }
            else
            {
                // Inside edge - fade the curl
                curlFactor = 1f - Mathf.Clamp01(-distToEdge / CURL_DETECTION_RANGE);
                curlFactor = curlFactor * curlFactor; // Ease in for smoother transition
            }

            // Blend toward the lower water level
            return Mathf.Lerp(surfaceY, curlTargetY, curlFactor);
        }

        /// <summary>
        /// Fills gaps between water disks by lateral expansion.
        /// 
        /// KEY CHANGE from old approach: uses MINIMUM neighbor height (not average)
        /// so the fill never pulls the surface UP - it only extends water laterally
        /// at or below the height of existing water. This preserves the channel depth
        /// profile set by the disk placement pass.
        /// 
        /// Runs multiple iterations so gaps wider than 1 cell get filled progressively.
        /// Only fills where terrain is below the candidate water level.
        /// </summary>
        private void FillSurfaceGaps()
        {
            int[] ddx = { -1, 1, 0, 0 };
            int[] ddz = { 0, 0, -1, 1 };

            // Multiple iterations to bridge multi-cell gaps between circular disks
            for (int iteration = 0; iteration < 3; iteration++)
            {
                bool anyFilled = false;

                for (int z = 1; z < _gridHeight - 1; z++)
                {
                    for (int x = 1; x < _gridWidth - 1; x++)
                    {
                        int idx = z * _gridWidth + x;

                        if (_surfaceHeights[idx] > -50f) continue;

                        int validCount = 0;
                        float minNeighborY = float.MaxValue;

                        for (int d = 0; d < 4; d++)
                        {
                            int nidx = (z + ddz[d]) * _gridWidth + (x + ddx[d]);

                            if (_surfaceHeights[nidx] > -50f)
                            {
                                validCount++;
                                if (_surfaceHeights[nidx] < minNeighborY)
                                    minNeighborY = _surfaceHeights[nidx];
                            }
                        }

                        // Fill if 2+ neighbors have water (bridges narrow channels)
                        // Use MINIMUM neighbor height so we never pull the surface up
                        if (validCount >= 2 && minNeighborY < float.MaxValue - 1f)
                        {
                            float terrainHere = _terrainHeights[idx];

                            // Only fill if water would be above terrain
                            if (minNeighborY > terrainHere + WATER_TERRAIN_OFFSET)
                            {
                                _surfaceHeights[idx] = minNeighborY;
                                anyFilled = true;
                            }
                        }
                    }
                }

                if (!anyFilled) break; // No more gaps to fill
            }
        }

        #endregion

        #region Surface Smoothing

        /// <summary>
        /// GRAVITY SETTLE PASS: Fixes water surface issues relative to terrain.
        /// 
        /// Runs AFTER surface calc, BEFORE smoothing.
        /// 
        /// THREE RULES:
        /// 
        /// 1. FLOATING EDGE PULLDOWN: Edge cells (have empty neighbors) where water
        ///    is >0.5m above terrain AND no nearby bank terrain contains it ?
        ///    pull down to terrain + offset. Water only stays high where banks hold it.
        /// 
        /// 2. DISK-CENTER BURIED FIX: If a grid cell is near a disk's CENTER position
        ///    and the water surface is below terrain there, push up to terrain + offset.
        ///    Only applies within a small radius of the disk center - edge/bank cells
        ///    below terrain stay put (they clip through terrain naturally, which is
        ///    correct behavior for hiding mesh edges at shorelines).
        /// 
        /// 3. EDGE ABOVE-TERRAIN PULLDOWN: Edge cells where water is floating above
        ///    terrain (but not at a disk center) get pulled down toward terrain so
        ///    the mesh meets the bank smoothly instead of hovering.
        /// </summary>
        private void GravitySettleEdges()
        {
            if (_surfaceHeights == null || _terrainHeights == null) return;

            int[] ddx = { -1, 1, 0, 0 };
            int[] ddz = { 0, 0, -1, 1 };

            // Pre-compute disk center grid positions for Rule 2
            // A cell is "at a disk center" if it's within CENTER_TOLERANCE_CELLS
            // of any disk's center position in grid space.
            const int CENTER_TOLERANCE_CELLS = 2;

            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    float surfaceY = _surfaceHeights[idx];
                    if (surfaceY < -50f) continue;

                    float terrainY = _terrainHeights[idx];

                    // Determine if this cell is near any disk's center
                    bool isNearDiskCenter = false;
                    Vector3 cellWorld = GridToWorld(x, z);
                    for (int d = 0; d < _waterDisks.Count && !isNearDiskCenter; d++)
                    {
                        var disk = _waterDisks[d];
                        float dx = cellWorld.x - disk.Position.x;
                        float dz2 = cellWorld.z - disk.Position.z;
                        float distSq = dx * dx + dz2 * dz2;
                        float tolerance = CENTER_TOLERANCE_CELLS * _cellSize;
                        if (distSq <= tolerance * tolerance)
                            isNearDiskCenter = true;
                    }

                    // ---- RULE 2: Disk-center buried fix ----
                    // Only push water UP if the cell is at a disk center and
                    // the surface ended up below terrain. This ensures the center
                    // of each disk is always visible. Edge cells below terrain
                    // stay put - they clip through bank naturally.
                    if (surfaceY < terrainY + WATER_TERRAIN_OFFSET)
                    {
                        if (isNearDiskCenter)
                        {
                            _surfaceHeights[idx] = terrainY + WATER_TERRAIN_OFFSET;
                        }
                        // Non-center cells below terrain: leave alone (bank extension / clip-through)
                        continue;
                    }

                    // ---- RULE 1 & 3: Edge cell checks ----
                    bool isEdge = false;
                    if (x == 0 || x == _gridWidth - 1 || z == 0 || z == _gridHeight - 1)
                    {
                        isEdge = true;
                    }
                    else
                    {
                        for (int d = 0; d < 4; d++)
                        {
                            int nidx = (z + ddz[d]) * _gridWidth + (x + ddx[d]);
                            if (_surfaceHeights[nidx] < -50f)
                            {
                                isEdge = true;
                                break;
                            }
                        }
                    }

                    if (!isEdge) continue;

                    // This is an edge cell with water above terrain.
                    float floatHeight = surfaceY - terrainY;
                    if (floatHeight < 0.5f) continue; // Only fix significant floating (>0.5m)

                    // Check for bank support - nearby terrain that contains the water
                    bool hasBankSupport = false;
                    int bankScanRadius = 2;
                    for (int bz = -bankScanRadius; bz <= bankScanRadius && !hasBankSupport; bz++)
                    {
                        for (int bx = -bankScanRadius; bx <= bankScanRadius && !hasBankSupport; bx++)
                        {
                            if (bx == 0 && bz == 0) continue;
                            int bnx = x + bx;
                            int bnz = z + bz;
                            if (bnx < 0 || bnx >= _gridWidth || bnz < 0 || bnz >= _gridHeight)
                                continue;
                            int bnidx = bnz * _gridWidth + bnx;

                            if (_surfaceHeights[bnidx] < -50f && _terrainHeights[bnidx] >= surfaceY - 0.5f)
                            {
                                hasBankSupport = true;
                            }
                        }
                    }

                    // 2026-05-30 round-55 — DISABLED edge gravity-settle
                    // pull-down. User spec (round-W-5):
                    //   "the settle with gravity is only meant to be for
                    //    letting the skirts settle and for it to smooth
                    //    out over slopes"
                    //
                    // Previously: every edge cell >0.5m above terrain
                    // with no nearby bank support got yanked DOWN to
                    // `terrainY + WATER_TERRAIN_OFFSET` (~terrain Y).
                    // For a small ruler-placed disk in mid-air, EVERY
                    // cell is an edge cell with no bank support, so the
                    // entire visible surface dropped 10m below the
                    // ruler Y while the swim trigger stayed at the
                    // ruler Y. User saw "water mesh ~10m below the
                    // swim plane".
                    //
                    // The balloon-shell wall (round-49/52b) is what
                    // visually bridges floating water down to terrain
                    // now — we don't need GravitySettleEdges to also
                    // be dragging the surface down. Rule 2 above (push
                    // buried disk-center cells UP) is still active for
                    // the case where IDW interpolation underestimated
                    // surface height inside terrain.
                    if (!hasBankSupport)
                    {
                        // No-op: leave the surface at its IDW-computed
                        // height. The shell wall handles the visual
                        // drop to terrain.
                    }
                }
            }
        }

        /// <summary>
        /// CROSS-SECTION FLATTENING: Enforces flat water across the channel width.
        ///
        /// PROBLEM SOLVED: Without this, water rides up steep banks because the
        /// per-cell surface interpolation follows the terrain slope. Real water
        /// is FLAT across a channel cross-section - it doesn't climb the banks.
        ///
        /// ALGORITHM:
        /// 1. Build an ordered disk chain by nearest-neighbor linking.
        /// 2. For each chain segment, compute the centerline bed height by
        ///    walking grid cells along the segment (not just the midpoint).
        /// 3. Flat level = min(interpolated disk height, bed + target depth).
        /// 4. For each grid cell, project onto the nearest chain segment to find
        ///    its longitudinal position and lateral offset.
        /// 5. Force ALL cells at that longitudinal position to the flat level,
        ///    regardless of whether that raises or lowers the current surface.
        ///    (The flat level is already capped by disk heights and bed depth.)
        /// 6. Where terrain exceeds the flat level, convert to bank extension.
        ///
        /// KEY FIX vs previous version: The old code had "never raise above
        /// original surface" which prevented fixing cells that were incorrectly
        /// pulled DOWN by bank-slope terrain in the initial interpolation pass.
        /// Now we allow raising to the flat level because that IS the correct
        /// height - the flat level is already capped by the disk chain heights
        /// which encode the monotonic downhill profile.
        ///
        /// THREAD SAFETY: Uses only _surfaceHeights, _terrainHeights, and
        /// _waterDisks (read-only). The async path mirrors this in
        /// WaterAsyncMeshBuilder.NormalizeChannelDepth.
        /// </summary>
        private void NormalizeChannelDepth()
        {
            if (_isPondMode) return;
            if (_surfaceHeights == null || _terrainHeights == null) return;
            if (_waterDisks.Count < 2) return;

            int diskCount = _waterDisks.Count;

            // --- Step 1: Build ordered disk chain via nearest-neighbor linking ---
            float[] diskWorldX = new float[diskCount];
            float[] diskWorldZ = new float[diskCount];
            float[] diskWorldY = new float[diskCount];
            float[] diskRadius = new float[diskCount];
            for (int d = 0; d < diskCount; d++)
            {
                var disk = _waterDisks[d];
                diskWorldX[d] = disk.Position.x;
                diskWorldZ[d] = disk.Position.z;
                diskWorldY[d] = disk.Position.y;
                diskRadius[d] = disk.Radius;
            }

            int[] chainOrder = BuildDiskChain(diskWorldX, diskWorldZ, diskCount);

            // --- Step 2: For each chain segment, find the centerline bed height ---
            // Walk grid cells along the FULL segment centerline (not just the midpoint)
            // to find the minimum terrain = the channel bed. This catches narrow channels
            // where the midpoint might miss the deepest point.
            int segCount = diskCount - 1;
            float[] segBedY = new float[segCount];
            float[] segFlatLevel = new float[segCount];

            for (int s = 0; s < segCount; s++)
            {
                int dA = chainOrder[s];
                int dB = chainOrder[s + 1];
                float ax = diskWorldX[dA], az = diskWorldZ[dA];
                float bx = diskWorldX[dB], bz = diskWorldZ[dB];
                float avgRadius = (diskRadius[dA] + diskRadius[dB]) * 0.5f;

                // Scan width perpendicular to centerline: narrow band (40% of radius)
                float scanWidth = Mathf.Max(avgRadius * 0.4f, _cellSize * 2f);
                int perpCells = Mathf.Max(1, Mathf.CeilToInt(scanWidth / _cellSize));

                // Walk along the segment centerline in cell-sized steps
                float segDx = bx - ax;
                float segDz = bz - az;
                float segLen = Mathf.Sqrt(segDx * segDx + segDz * segDz);
                int walkSteps = Mathf.Max(1, Mathf.CeilToInt(segLen / _cellSize));

                float minBed = float.MaxValue;
                for (int ws = 0; ws <= walkSteps; ws++)
                {
                    float wt = (float)ws / walkSteps;
                    float sampleX = ax + segDx * wt;
                    float sampleZ = az + segDz * wt;

                    float localX = sampleX - _gridOrigin.x;
                    float localZ = sampleZ - _gridOrigin.z;
                    int centerGX = Mathf.Clamp(Mathf.RoundToInt(localX / _cellSize), 0, _gridWidth - 1);
                    int centerGZ = Mathf.Clamp(Mathf.RoundToInt(localZ / _cellSize), 0, _gridHeight - 1);

                    for (int sz = -perpCells; sz <= perpCells; sz++)
                    {
                        for (int sx = -perpCells; sx <= perpCells; sx++)
                        {
                            int gx = centerGX + sx;
                            int gz = centerGZ + sz;
                            if (gx < 0 || gx >= _gridWidth || gz < 0 || gz >= _gridHeight)
                                continue;
                            int gidx = gz * _gridWidth + gx;
                            // Only consider cells that have water and are above terrain
                            // (i.e. actual channel cells, not bank extensions)
                            if (_surfaceHeights[gidx] > -50f && _surfaceHeights[gidx] >= _terrainHeights[gidx])
                            {
                                if (_terrainHeights[gidx] < minBed)
                                    minBed = _terrainHeights[gidx];
                            }
                        }
                    }
                }

                segBedY[s] = minBed;

                // Flat level = interpolated disk height.
                // Disk heights are authoritative - they encode the water level
                // the user or auto-generator intended. We only use the bed as a
                // FLOOR (water can't be below terrain), never as a ceiling that
                // pulls the surface down to terrain + 1.2m.
                float interpY = (diskWorldY[dA] + diskWorldY[dB]) * 0.5f;
                float bedFloor = (minBed < float.MaxValue - 1f) ? (minBed + WATER_TERRAIN_OFFSET) : interpY;
                segFlatLevel[s] = Mathf.Max(interpY, bedFloor);
            }

            // --- Step 3: For each grid cell, project onto nearest chain segment ---
            // and assign the flat cross-section level.
            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    float surfaceY = _surfaceHeights[idx];
                    if (surfaceY < -50f) continue;

                    float terrainY = _terrainHeights[idx];
                    // Skip bank extensions that are already below terrain
                    if (surfaceY < terrainY - 0.1f) continue;

                    float cellWX = _gridOrigin.x + x * _cellSize;
                    float cellWZ = _gridOrigin.z + z * _cellSize;

                    // Find nearest chain segment and compute projection
                    float bestT = 0f;
                    int bestSeg = -1;
                    float bestLateralDistSq = float.MaxValue;

                    for (int s = 0; s < segCount; s++)
                    {
                        int dA = chainOrder[s];
                        int dB = chainOrder[s + 1];
                        float axx = diskWorldX[dA], azz = diskWorldZ[dA];
                        float bxx = diskWorldX[dB], bzz = diskWorldZ[dB];

                        // Project cell onto segment AB
                        float abx = bxx - axx, abz = bzz - azz;
                        float acx = cellWX - axx, acz = cellWZ - azz;
                        float abLenSq = abx * abx + abz * abz;
                        float t = (abLenSq > 0.001f) ? ((acx * abx + acz * abz) / abLenSq) : 0f;
                        t = Mathf.Clamp01(t);

                        float projX = axx + abx * t;
                        float projZ = azz + abz * t;
                        float dx = cellWX - projX;
                        float dz2 = cellWZ - projZ;
                        float latDistSq = dx * dx + dz2 * dz2;

                        if (latDistSq < bestLateralDistSq)
                        {
                            bestLateralDistSq = latDistSq;
                            bestSeg = s;
                            bestT = t;
                        }
                    }

                    if (bestSeg < 0) continue;

                    // Compute the flat level for this position by interpolating
                    // between this segment's endpoints and adjacent segments.
                    float flatLevel;
                    if (bestSeg > 0 && bestT < 0.3f)
                    {
                        float blend = bestT / 0.3f;
                        flatLevel = Mathf.Lerp(segFlatLevel[bestSeg - 1], segFlatLevel[bestSeg], blend);
                    }
                    else if (bestSeg < segCount - 1 && bestT > 0.7f)
                    {
                        float blend = (bestT - 0.7f) / 0.3f;
                        flatLevel = Mathf.Lerp(segFlatLevel[bestSeg], segFlatLevel[bestSeg + 1], blend);
                    }
                    else
                    {
                        flatLevel = segFlatLevel[bestSeg];
                    }

                    // Edge tapering: reduce correction near disk edges.
                    int dA2 = chainOrder[bestSeg];
                    int dB2 = chainOrder[bestSeg + 1];
                    float avgR = (diskRadius[dA2] + diskRadius[dB2]) * 0.5f;
                    float lateralDist = Mathf.Sqrt(bestLateralDistSq);
                    float normLateral = lateralDist / Mathf.Max(1f, avgR);

                    // Full correction within 60% of radius, fading to 0 at 110%
                    // (wider than before to catch more of the channel cross-section)
                    float edgeFactor = Mathf.Clamp01((1.1f - normLateral) / 0.5f);

                    // Skip cells far from centerline - they're bank/edge cells
                    if (edgeFactor < 0.01f) continue;

                    float localFloor = terrainY + WATER_TERRAIN_OFFSET;

                    // If terrain is ABOVE the flat level at this cell, the water
                    // should clip through (bank extension) - push below terrain.
                    if (terrainY > flatLevel + 0.1f)
                    {
                        float bankSurface = terrainY - BANK_DEPTH_BELOW_TERRAIN;
                        _surfaceHeights[idx] = bankSurface;
                        continue;
                    }

                    // The target is the flat level, but never below local terrain
                    float targetY = flatLevel;
                    if (targetY < localFloor)
                        targetY = localFloor;

                    // Blend current surface toward the flat target.
                    // KEY: We allow RAISING the surface here. The flat level is
                    // already capped by disk heights (which encode the monotonic
                    // downhill profile from AutoWaterGenerator). If the initial
                    // interpolation pulled a cell too low (e.g. because terrain
                    // on a bank slope pulled the nearest-disk average down),
                    // we need to raise it back to the flat cross-section level.
                    // Real water is flat - every cell at the same longitudinal
                    // position must be at the same height.
                    float strength = DEPTH_NORM_STRENGTH * edgeFactor;
                    float newSurface = surfaceY + (targetY - surfaceY) * strength;

                    // Never below local terrain
                    if (newSurface < localFloor)
                        newSurface = localFloor;

                    _surfaceHeights[idx] = newSurface;
                }
            }
        }

        /// <summary>
        /// Builds an ordered chain through disk centers via nearest-neighbor linking.
        /// Starts from the disk farthest from the centroid (likely one end of the river),
        /// then greedily picks the nearest unvisited disk at each step.
        /// Returns an array of disk indices in chain order.
        /// </summary>
        private static int[] BuildDiskChain(float[] diskX, float[] diskZ, int count)
        {
            if (count <= 0) return new int[0];
            if (count == 1) return new int[] { 0 };

            // Find centroid
            float cx = 0f, cz = 0f;
            for (int i = 0; i < count; i++) { cx += diskX[i]; cz += diskZ[i]; }
            cx /= count; cz /= count;

            // Start from the disk farthest from centroid (one river end)
            int startIdx = 0;
            float maxDistSq = 0f;
            for (int i = 0; i < count; i++)
            {
                float dx = diskX[i] - cx;
                float dz = diskZ[i] - cz;
                float dSq = dx * dx + dz * dz;
                if (dSq > maxDistSq) { maxDistSq = dSq; startIdx = i; }
            }

            // Greedy nearest-neighbor chain
            int[] chain = new int[count];
            bool[] used = new bool[count];
            chain[0] = startIdx;
            used[startIdx] = true;

            for (int step = 1; step < count; step++)
            {
                int prev = chain[step - 1];
                float bestDist = float.MaxValue;
                int bestNext = -1;
                for (int i = 0; i < count; i++)
                {
                    if (used[i]) continue;
                    float dx = diskX[i] - diskX[prev];
                    float dz = diskZ[i] - diskZ[prev];
                    float d = dx * dx + dz * dz;
                    if (d < bestDist) { bestDist = d; bestNext = i; }
                }
                if (bestNext < 0) break;
                chain[step] = bestNext;
                used[bestNext] = true;
            }

            return chain;
        }

        /// <summary>
        /// Terrain clip pass: removes water from grid cells where terrain is above
        /// the computed water surface. This is the critical step that prevents water
        /// from extending as a flat sheet through hillsides.
        ///
        /// Without this, the rectangular combiner grid fills the entire bounding box
        /// with water - cells on steep valley walls get a surface Y interpolated from
        /// nearby river disks even though the real terrain is meters above the water.
        ///
        /// ALGORITHM:
        /// For each cell with water (surfaceHeight > -50):
        ///   If terrain > surfaceY + small tolerance -> kill cell (set to -100)
        ///   UNLESS the cell is a bank extension (surfaceY < terrainY, intentional)
        ///
        /// This runs AFTER NormalizeChannelDepth (which sets flat cross-section levels)
        /// and BEFORE SmoothWaterSurface (which would blur killed cells into neighbors).
        /// </summary>
        private void TerrainClipPass()
        {
            if (_surfaceHeights == null || _terrainHeights == null) return;

            const float CLIP_TOLERANCE = 0.05f;

            for (int i = 0; i < _surfaceHeights.Length; i++)
            {
                float surfaceY = _surfaceHeights[i];
                if (surfaceY < -50f) continue;

                float terrainY = _terrainHeights[i];

                // Skip bank extensions - these are intentionally below terrain
                if (surfaceY < terrainY - 0.01f) continue;

                // If terrain rises above the water surface, this cell is on a
                // hillside or bank slope - remove the water entirely.
                if (terrainY > surfaceY + CLIP_TOLERANCE)
                {
                    _surfaceHeights[i] = -100f;
                }
            }
        }

        /// <summary>
        /// Applies heavy smoothing to create a consistent water plane.
        /// v4.1: Updated to handle bank extension vertices properly.
        /// Bank extension vertices (below terrain) should still be smoothed
        /// but not removed - they're intentionally there to hide edges.
        /// </summary>
        private void SmoothWaterSurface()
        {
            float[] smoothed = new float[_surfaceHeights.Length];

            for (int pass = 0; pass < SURFACE_SMOOTH_PASSES; pass++)
            {
                Array.Copy(_surfaceHeights, smoothed, _surfaceHeights.Length);

                for (int z = 1; z < _gridHeight - 1; z++)
                {
                    for (int x = 1; x < _gridWidth - 1; x++)
                    {
                        int idx = z * _gridWidth + x;

                        // Skip cells with no water at all
                        if (_surfaceHeights[idx] < -50f) continue;

                        float currentY = _surfaceHeights[idx];
                        float terrainY = _terrainHeights[idx];
                        bool isBankExtension = currentY < terrainY; // Below terrain = bank extension

                        float sum = 0f;
                        float weightSum = 0f;

                        // Tight +/-1 cell neighborhood - prevents pulling from distant cells.
                        // CRITICAL: Skip bank extension neighbors (below terrain) - they
                        // are NOT part of the water surface and their terrain-relative
                        // heights pull the average toward bank terrain elevation,
                        // causing the visible "water riding up the banks" artifact.
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                int nz = z + dz;

                                if (nx < 0 || nx >= _gridWidth || nz < 0 || nz >= _gridHeight)
                                    continue;

                                int nidx = nz * _gridWidth + nx;

                                if (_surfaceHeights[nidx] > -50f)
                                {
                                    // Skip bank extension neighbors - they're below
                                    // terrain intentionally for clipping, not real surface
                                    float nTerrainY = _terrainHeights[nidx];
                                    if (_surfaceHeights[nidx] < nTerrainY && !isBankExtension)
                                        continue;

                                    // Weight by distance (closer = more weight)
                                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                                    float weight = 1f / (1f + dist);

                                    sum += _surfaceHeights[nidx] * weight;
                                    weightSum += weight;
                                }
                            }
                        }

                        if (weightSum > 0)
                        {
                            float neighborAvg = sum / weightSum;
                            float blended = Mathf.Lerp(currentY, neighborAvg, SMOOTH_STRENGTH);

                            if (isBankExtension)
                            {
                                // Bank extension: keep below terrain
                                smoothed[idx] = Mathf.Min(blended, terrainY - WATER_TERRAIN_OFFSET);
                            }
                            else
                            {
                                // KEY CONSTRAINT: smoothing may never RAISE the surface.
                                // This preserves the monotonic downhill flow profile that
                                // AutoWaterGenerator computed. Without this, smoothing
                                // averages with higher neighbors and inflates the entire
                                // river plane upward.
                                if (blended > currentY)
                                    blended = currentY;

                                // Also keep above terrain
                                if (blended < terrainY + WATER_TERRAIN_OFFSET)
                                {
                                    if (currentY > terrainY + WATER_TERRAIN_OFFSET)
                                        blended = currentY;
                                    else
                                        blended = terrainY - BANK_DEPTH_BELOW_TERRAIN * 0.5f;
                                }

                                smoothed[idx] = blended;
                            }
                        }
                    }
                }

                Array.Copy(smoothed, _surfaceHeights, _surfaceHeights.Length);
            }

            // Final pass: validate all vertices
            // - Normal vertices should be above terrain
            // - Bank extension vertices should be below terrain
            // - No vertices should be in the "z-fighting zone" right at terrain level
            for (int i = 0; i < _surfaceHeights.Length; i++)
            {
                if (_surfaceHeights[i] > -50f)
                {
                    float terrainY = _terrainHeights[i];
                    float waterY = _surfaceHeights[i];

                    // Check for z-fighting zone (too close to terrain level)
                    float distFromTerrain = Mathf.Abs(waterY - terrainY);
                    if (distFromTerrain < WATER_TERRAIN_OFFSET * 2f)
                    {
                        // Push it one way or the other to avoid z-fighting
                        if (waterY >= terrainY)
                        {
                            _surfaceHeights[i] = terrainY + WATER_TERRAIN_OFFSET;
                        }
                        else
                        {
                            _surfaceHeights[i] = terrainY - WATER_TERRAIN_OFFSET;
                        }
                    }
                }
            }
        }

        private IEnumerator SmoothWaterSurfaceAsync()
        {
            float[] smoothed = new float[_surfaceHeights.Length];

            for (int pass = 0; pass < SURFACE_SMOOTH_PASSES; pass++)
            {
                Array.Copy(_surfaceHeights, smoothed, _surfaceHeights.Length);

                const int BATCH_SIZE = 500;
                int processed = 0;

                for (int z = 1; z < _gridHeight - 1; z++)
                {
                    for (int x = 1; x < _gridWidth - 1; x++)
                    {
                        int idx = z * _gridWidth + x;

                        if (_surfaceHeights[idx] < -50f) continue;

                        float currentY = _surfaceHeights[idx];
                        float terrainY = _terrainHeights[idx];
                        bool isBankExtension = currentY < terrainY;

                        float sum = 0f;
                        float weightSum = 0f;

                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                int nz = z + dz;

                                if (nx < 0 || nx >= _gridWidth || nz < 0 || nz >= _gridHeight)
                                    continue;

                                int nidx = nz * _gridWidth + nx;

                                if (_surfaceHeights[nidx] > -50f)
                                {
                                    // Skip bank extension neighbors for non-bank cells
                                    float nTerrainY = _terrainHeights[nidx];
                                    if (_surfaceHeights[nidx] < nTerrainY && !isBankExtension)
                                        continue;

                                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                                    float weight = 1f / (1f + dist);

                                    sum += _surfaceHeights[nidx] * weight;
                                    weightSum += weight;
                                }
                            }
                        }

                        if (weightSum > 0)
                        {
                            float neighborAvg = sum / weightSum;
                            float blended = Mathf.Lerp(currentY, neighborAvg, SMOOTH_STRENGTH);

                            if (isBankExtension)
                            {
                                smoothed[idx] = Mathf.Min(blended, terrainY - WATER_TERRAIN_OFFSET);
                            }
                            else
                            {
                                // Never raise surface during smoothing
                                if (blended > currentY)
                                    blended = currentY;

                                if (blended < terrainY + WATER_TERRAIN_OFFSET)
                                {
                                    if (currentY > terrainY + WATER_TERRAIN_OFFSET)
                                        blended = currentY;
                                    else
                                        blended = terrainY - BANK_DEPTH_BELOW_TERRAIN * 0.5f;
                                }

                                smoothed[idx] = blended;
                            }
                        }

                        processed++;
                        if (processed % BATCH_SIZE == 0)
                        {
                            yield return null;
                        }
                    }
                }

                Array.Copy(smoothed, _surfaceHeights, _surfaceHeights.Length);
                yield return null;
            }

            // Final validation pass
            for (int i = 0; i < _surfaceHeights.Length; i++)
            {
                if (_surfaceHeights[i] > -50f)
                {
                    float terrainY = _terrainHeights[i];
                    float waterY = _surfaceHeights[i];

                    float distFromTerrain = Mathf.Abs(waterY - terrainY);
                    if (distFromTerrain < WATER_TERRAIN_OFFSET * 2f)
                    {
                        if (waterY >= terrainY)
                        {
                            _surfaceHeights[i] = terrainY + WATER_TERRAIN_OFFSET;
                        }
                        else
                        {
                            _surfaceHeights[i] = terrainY - WATER_TERRAIN_OFFSET;
                        }
                    }
                }
            }
        }

        #endregion

        #region Water Depth Calculation

        private void CalculateWaterDepths()
        {
            for (int i = 0; i < _surfaceHeights.Length; i++)
            {
                float surface = _surfaceHeights[i];
                float terrain = _terrainHeights[i];

                if (surface < -50f)
                {
                    _waterDepths[i] = 0f;
                }
                else
                {
                    // Depth can be negative for bank extension vertices (below terrain)
                    // This is intentional - we use it to identify bank extension cells
                    _waterDepths[i] = surface - terrain;
                }
            }
        }

        #endregion

        #region Mesh Building

        private void BuildWaterMesh()
        {
            if (_surfaceHeights == null || _waterDepths == null) return;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();

            // Valid cells include:
            // - Normal water (surface > -50 and depth > 0)
            // - Bank extension (surface > -50 and depth <= 0, meaning surface is below terrain)
            //
            // 2026-05-25 round-5: STRICT FOOTPRINT CULL re-added (tight 0.1m
            // margin). Without this, FillSurfaceGaps's outward bleed + the
            // rectangular bounding box make small circular disks render as
            // squares. User has asked multiple times for the mesh to match
            // the ruler's circle. Bank pull-down still works for cells INSIDE
            // the disk (where terrain rises above water → bank extension
            // logic in CalculateSurfaceWithGravity handles it); we just don't
            // render the cells that fall outside the disk footprint.
            var validCells = new bool[_gridWidth * _gridHeight];
            for (int i = 0; i < _surfaceHeights.Length; i++)
            {
                if (_surfaceHeights[i] <= -50f) { validCells[i] = false; continue; }
                // Round-56: settling/gelled volumes legitimately spread
                // OUTSIDE the original ruler circle (that's the settle) —
                // the strict disk-footprint cull would clip the spread.
                // The -50 sentinel above already culls dry cells for them.
                if (_isGelled || _isSettling) { validCells[i] = true; continue; }
                int gx = i % _gridWidth;
                int gz = i / _gridWidth;
                Vector3 cellWorld = GridToWorld(gx, gz);
                validCells[i] = IsWithinAnyDiskShape(cellWorld);
            }

            var vertexIndices = new int[_gridWidth * _gridHeight];
            for (int i = 0; i < vertexIndices.Length; i++)
                vertexIndices[i] = -1;

            int vertexIndex = 0;

            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    if (!validCells[idx]) continue;

                    bool isPartOfQuad = false;
                    for (int dz = -1; dz <= 0 && !isPartOfQuad; dz++)
                    {
                        for (int dx = -1; dx <= 0 && !isPartOfQuad; dx++)
                        {
                            int qx = x + dx;
                            int qz = z + dz;

                            if (qx < 0 || qz < 0 || qx >= _gridWidth - 1 || qz >= _gridHeight - 1)
                                continue;

                            int i0 = qz * _gridWidth + qx;
                            int i1 = qz * _gridWidth + qx + 1;
                            int i2 = (qz + 1) * _gridWidth + qx;
                            int i3 = (qz + 1) * _gridWidth + qx + 1;

                            if (validCells[i0] && validCells[i1] && validCells[i2] && validCells[i3])
                            {
                                isPartOfQuad = true;
                            }
                        }
                    }

                    if (!isPartOfQuad) continue;

                    Vector3 worldPos = GridToWorld(x, z);

                    float depth = _waterDepths[idx];
                    float waveOffset = CalculateWaveHeight(worldPos, depth);

                    Vector3 localPos = worldPos - transform.position;
                    localPos.y = _surfaceHeights[idx] + waveOffset - transform.position.y;

                    vertices.Add(localPos);

                    float u = worldPos.x * 0.1f;
                    float v = worldPos.z * 0.1f;
                    uvs.Add(new Vector2(u, v));

                    normals.Add(Vector3.up);

                    float depthNorm = Mathf.Clamp01(depth / 5f);
                    colors.Add(new Color(depthNorm, depthNorm, 1f, 1f));

                    vertexIndices[idx] = vertexIndex++;
                }
            }

            for (int z = 0; z < _gridHeight - 1; z++)
            {
                for (int x = 0; x < _gridWidth - 1; x++)
                {
                    int i0 = z * _gridWidth + x;
                    int i1 = z * _gridWidth + x + 1;
                    int i2 = (z + 1) * _gridWidth + x;
                    int i3 = (z + 1) * _gridWidth + x + 1;

                    if (!validCells[i0] || !validCells[i1] || !validCells[i2] || !validCells[i3])
                        continue;

                    int v0 = vertexIndices[i0];
                    int v1 = vertexIndices[i1];
                    int v2 = vertexIndices[i2];
                    int v3 = vertexIndices[i3];

                    if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                        continue;

                    triangles.Add(v0);
                    triangles.Add(v2);
                    triangles.Add(v1);

                    triangles.Add(v1);
                    triangles.Add(v2);
                    triangles.Add(v3);
                }
            }

            _waterMesh.Clear();
            if (FiresLogger.VerboseEnabled) FiresLogger.LogInfo($"[WaterMeshCombiner] PATH 2 (sync BuildWaterMesh): verts={vertices.Count} tris={triangles.Count} shellEnabled={WaterBalloonShell.Enabled}");

            if (vertices.Count > 0 && triangles.Count > 0)
            {
                // Round-W-4 — balloon shell pass on the sync path too,
                // so the closed-volume mesh shows up regardless of
                // whether the async worker fired or we fell back to
                // sync. Without this wrapper the sync path was a
                // single-sided flat sheet and the user's "drop / pool
                // / tube" shape never appeared.
                if (WaterBalloonShell.Enabled)
                {
                    // Build per-vertex depths the shell needs. The
                    // sync path's `vertices` list holds the surface
                    // verts in submitted order, NOT grid order, so we
                    // can't directly use _waterDepths[grid_idx]. Walk
                    // each vert, reverse-map to grid coord, look up
                    // its depth.
                    var depthArray = new float[vertices.Count];
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        Vector3 wv = vertices[i];
                        int gx = Mathf.RoundToInt((wv.x - _gridOrigin.x) / _cellSize);
                        int gz = Mathf.RoundToInt((wv.z - _gridOrigin.z) / _cellSize);
                        if (gx >= 0 && gx < _gridWidth && gz >= 0 && gz < _gridHeight)
                        {
                            int gi = gz * _gridWidth + gx;
                            if (gi >= 0 && gi < _waterDepths.Length)
                                depthArray[i] = _waterDepths[gi];
                        }
                    }

                    var shell = WaterBalloonShell.Expand(
                        vertices.ToArray(),
                        triangles.ToArray(),
                        uvs.ToArray(),
                        normals.ToArray(),
                        colors.ToArray(),
                        depthArray);
                    _waterMesh.SetVertices(shell.Vertices);
                    _waterMesh.SetTriangles(shell.Triangles, 0);
                    _waterMesh.SetUVs(0, shell.UVs);
                    _waterMesh.SetNormals(shell.Normals);
                    _waterMesh.SetColors(shell.Colors);
                    _waterMesh.RecalculateBounds();
                }
                else
                {
                    _waterMesh.SetVertices(vertices);
                    _waterMesh.SetTriangles(triangles, 0);
                    _waterMesh.SetUVs(0, uvs);
                    _waterMesh.SetNormals(normals);
                    _waterMesh.SetColors(colors);
                    _waterMesh.RecalculateBounds();
                    _waterMesh.RecalculateNormals();
                }
            }
        }

        private IEnumerator BuildWaterMeshAsync()
        {
            if (_surfaceHeights == null || _waterDepths == null) yield break;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();

            var validCells = new bool[_gridWidth * _gridHeight];
            for (int i = 0; i < _waterDepths.Length; i++)
            {
                if (_surfaceHeights[i] <= -50f) { validCells[i] = false; continue; }
                // Round-56: settled volumes spread outside the ruler circle;
                // skip the footprint cull for them (see sync path comment).
                if (_isGelled || _isSettling) { validCells[i] = true; continue; }
                int gx = i % _gridWidth;
                int gz = i / _gridWidth;
                Vector3 cellWorld = GridToWorld(gx, gz);
                // Strict disk-footprint cull — see sync BuildWaterMesh comment.
                validCells[i] = IsWithinAnyDiskShape(cellWorld);
            }

            var vertexIndices = new int[_gridWidth * _gridHeight];
            for (int i = 0; i < vertexIndices.Length; i++)
                vertexIndices[i] = -1;

            const int BATCH_SIZE = 500;
            int processed = 0;
            int vertexIndex = 0;

            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    if (!validCells[idx]) continue;

                    bool isPartOfQuad = false;
                    for (int dz = -1; dz <= 0 && !isPartOfQuad; dz++)
                    {
                        for (int dx = -1; dx <= 0 && !isPartOfQuad; dx++)
                        {
                            int qx = x + dx;
                            int qz = z + dz;

                            if (qx < 0 || qz < 0 || qx >= _gridWidth - 1 || qz >= _gridHeight - 1)
                                continue;

                            int i0 = qz * _gridWidth + qx;
                            int i1 = qz * _gridWidth + qx + 1;
                            int i2 = (qz + 1) * _gridWidth + qx;
                            int i3 = (qz + 1) * _gridWidth + qx + 1;

                            if (validCells[i0] && validCells[i1] && validCells[i2] && validCells[i3])
                            {
                                isPartOfQuad = true;
                            }
                        }
                    }

                    if (!isPartOfQuad) continue;

                    Vector3 worldPos = GridToWorld(x, z);
                    float depth = _waterDepths[idx];
                    float waveOffset = CalculateWaveHeight(worldPos, depth);

                    Vector3 localPos = worldPos - transform.position;
                    localPos.y = _surfaceHeights[idx] + waveOffset - transform.position.y;

                    vertices.Add(localPos);
                    uvs.Add(new Vector2(worldPos.x * 0.1f, worldPos.z * 0.1f));
                    normals.Add(Vector3.up);

                    float depthNorm = Mathf.Clamp01(depth / 5f);
                    colors.Add(new Color(depthNorm, depthNorm, 1f, 1f));

                    vertexIndices[idx] = vertexIndex++;

                    processed++;
                    if (processed % BATCH_SIZE == 0)
                    {
                        yield return null;
                    }
                }
            }

            yield return null;

            for (int z = 0; z < _gridHeight - 1; z++)
            {
                for (int x = 0; x < _gridWidth - 1; x++)
                {
                    int i0 = z * _gridWidth + x;
                    int i1 = z * _gridWidth + x + 1;
                    int i2 = (z + 1) * _gridWidth + x;
                    int i3 = (z + 1) * _gridWidth + x + 1;

                    if (!validCells[i0] || !validCells[i1] || !validCells[i2] || !validCells[i3])
                        continue;

                    int v0 = vertexIndices[i0];
                    int v1 = vertexIndices[i1];
                    int v2 = vertexIndices[i2];
                    int v3 = vertexIndices[i3];

                    if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                        continue;

                    triangles.Add(v0);
                    triangles.Add(v2);
                    triangles.Add(v1);

                    triangles.Add(v1);
                    triangles.Add(v2);
                    triangles.Add(v3);
                }
            }

            yield return null;

            _waterMesh.Clear();
            if (FiresLogger.VerboseEnabled) FiresLogger.LogInfo($"[WaterMeshCombiner] PATH 3 (legacy coroutine BuildWaterMeshAsync): verts={vertices.Count} tris={triangles.Count} shellEnabled={WaterBalloonShell.Enabled}");

            if (vertices.Count > 0 && triangles.Count > 0)
            {
                // Round-W-4 — third mesh-build path (the legacy
                // coroutine async); wrap with shell so all three
                // build paths produce the closed-volume shape.
                if (WaterBalloonShell.Enabled)
                {
                    var depthArray = new float[vertices.Count];
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        Vector3 wv = vertices[i];
                        int gx = Mathf.RoundToInt((wv.x - _gridOrigin.x) / _cellSize);
                        int gz = Mathf.RoundToInt((wv.z - _gridOrigin.z) / _cellSize);
                        if (gx >= 0 && gx < _gridWidth && gz >= 0 && gz < _gridHeight)
                        {
                            int gi = gz * _gridWidth + gx;
                            if (gi >= 0 && gi < _waterDepths.Length)
                                depthArray[i] = _waterDepths[gi];
                        }
                    }

                    var shell = WaterBalloonShell.Expand(
                        vertices.ToArray(), triangles.ToArray(),
                        uvs.ToArray(), normals.ToArray(), colors.ToArray(),
                        depthArray);
                    _waterMesh.SetVertices(shell.Vertices);
                    _waterMesh.SetTriangles(shell.Triangles, 0);
                    _waterMesh.SetUVs(0, shell.UVs);
                    _waterMesh.SetNormals(shell.Normals);
                    _waterMesh.SetColors(shell.Colors);
                    _waterMesh.RecalculateBounds();
                }
                else
                {
                    _waterMesh.SetVertices(vertices);
                    _waterMesh.SetTriangles(triangles, 0);
                    _waterMesh.SetUVs(0, uvs);
                    _waterMesh.SetNormals(normals);
                    _waterMesh.SetColors(colors);
                    _waterMesh.RecalculateBounds();
                    _waterMesh.RecalculateNormals();
                }
            }
        }

        #endregion

        #region Cliff Detection (for VFX)

        private void DetectCliffEdges()
        {
            _cliffCells.Clear();

            for (int z = 1; z < _gridHeight - 1; z++)
            {
                for (int x = 1; x < _gridWidth - 1; x++)
                {
                    int idx = z * _gridWidth + x;

                    if (_surfaceHeights[idx] < -50f) continue;

                    float ourY = _surfaceHeights[idx];
                    Vector3 edgeDir = Vector3.zero;
                    float maxDrop = 0f;

                    int[] dx = { -1, 1, 0, 0 };
                    int[] dz = { 0, 0, -1, 1 };

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + dx[d];
                        int nz = z + dz[d];
                        int nidx = nz * _gridWidth + nx;

                        float neighborY = _surfaceHeights[nidx];
                        float neighborTerrain = _terrainHeights[nidx];

                        float drop = 0f;
                        if (neighborY < -50f)
                        {
                            drop = ourY - neighborTerrain;
                        }
                        else
                        {
                            drop = ourY - neighborY;
                        }

                        if (drop > maxDrop && drop > 2f)
                        {
                            maxDrop = drop;
                            edgeDir = new Vector3(dx[d], 0, dz[d]);
                        }
                    }

                    if (maxDrop > 2f)
                    {
                        Vector3 worldPos = GridToWorld(x, z);
                        _cliffCells.Add(new CliffCellData
                        {
                            GridIndex = idx,
                            WorldPos = worldPos,
                            CliffDirection = edgeDir.normalized,
                            StretchDistance = maxDrop,
                            CliffAngle = Mathf.Atan2(maxDrop, _cellSize) * Mathf.Rad2Deg,
                            TopY = ourY,
                            BottomY = ourY - maxDrop,
                            IsInterDiskStretch = false
                        });
                    }
                }
            }
        }

        #endregion

        #region Material Setup

        // Round-65/66 — material A/B debug switch (`va_water_mat_debug`).
        //   0 = normal (wrapped FiresWater when FTS installed)
        //   1 = vanilla `water_liquid` (Custom/Tar family, the tar-pit
        //       shader tinted for water) — a shader PROVEN to render in
        //       this world (tar pits use it). Round-66: was "raw captured
        //       vanilla water", but FTS swaps zone water IN PLACE before
        //       FAT captures, so the captured material is ALREADY
        //       FiresWater — mode 1 was comparing FiresWater to itself.
        //   2 = bright debug material — ground truth for "is the MESH
        //       visible at all". Round-66: Shader.Find("Standard")
        //       returned null in-game (no per-combiner log lines after
        //       ReapplyMaterialsAll mode=2), so we now walk a candidate
        //       list and LOG the outcome instead of failing silently.
        public static int DebugMaterialMode;
        private static Material s_debugBrightMaterial;
        private static Material s_debugTarWaterMaterial;

        private static readonly string[] DEBUG_SHADER_CANDIDATES =
        {
            "Standard", "Sprites/Default", "Unlit/Color", "GUI/Text Shader",
        };

        private static Material GetDebugOverrideMaterial()
        {
            if (DebugMaterialMode == 1)
            {
                if (s_debugTarWaterMaterial == null)
                {
                    foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                    {
                        if (m != null && m.name == "water_liquid")
                        {
                            s_debugTarWaterMaterial = m;
                            break;
                        }
                    }
                    FiresLogger.LogInfo(s_debugTarWaterMaterial != null
                        ? $"[WaterMeshCombiner] mat_debug 1: using vanilla 'water_liquid' ({s_debugTarWaterMaterial.shader?.name})"
                        : "[WaterMeshCombiner] mat_debug 1: vanilla 'water_liquid' NOT FOUND — falling back to captured water material");
                }
                return s_debugTarWaterMaterial != null
                    ? s_debugTarWaterMaterial
                    : WaterMaterialManager.GetRawWaterMaterial();
            }
            if (DebugMaterialMode == 2)
            {
                if (s_debugBrightMaterial == null)
                {
                    Shader shader = null;
                    foreach (var name in DEBUG_SHADER_CANDIDATES)
                    {
                        shader = Shader.Find(name);
                        if (shader != null) break;
                    }
                    if (shader == null)
                    {
                        FiresLogger.LogError($"[WaterMeshCombiner] mat_debug 2: NO debug shader found (tried {string.Join(", ", DEBUG_SHADER_CANDIDATES)}) — mode 2 unavailable.");
                        return null;
                    }
                    s_debugBrightMaterial = new Material(shader)
                    {
                        name = "VA_WaterDebug_Bright",
                        color = new Color(0.1f, 0.6f, 1f, 1f),
                    };
                    FiresLogger.LogInfo($"[WaterMeshCombiner] mat_debug 2: built bright debug material on shader '{shader.name}'");
                }
                return s_debugBrightMaterial;
            }
            return null;
        }

        // Round-65 — force every live combiner to re-resolve its material
        // through SetupMaterial. Clears the cached _waterMaterial first so
        // switching debug modes (including back to 0) actually swaps the
        // renderer material instead of hitting the early-return cache.
        public static int ReapplyMaterialsAll()
        {
            int n = 0;
            foreach (var c in AllCombiners)
            {
                if (c == null || c._meshRenderer == null) continue;
                c._waterMaterial = null;
                c.SetupMaterial();
                n++;
            }
            FiresLogger.LogInfo($"[WaterMeshCombiner] ReapplyMaterialsAll mode={DebugMaterialMode} → {n} combiner(s)");
            return n;
        }

        private void SetupMaterial()
        {
            // Debug override wins over everything (round-65).
            var dbg = GetDebugOverrideMaterial();
            if (dbg != null)
            {
                _waterMaterial = dbg;
                _meshRenderer.material = dbg;
                FiresLogger.LogInfo($"[WaterMeshCombiner] SetupMaterial DEBUG override mode={DebugMaterialMode} mat={dbg.name} shader={dbg.shader?.name} queue={dbg.renderQueue}");
                return;
            }

            // Placed fresh water is calm every build, also after an anchor restored a saved wave strength.
            if (CustomLiquidType == CustomLiquidType.Water && LiquidType == LiquidType.Water) WaveIntensity = 0f;
            // 0.2.766: placed fresh water is drawn by PlacedWaterSheets with the skyland sheet builder; this grid mesh would
            // stack a second, stepped surface on it. The swim trigger is a separate object and stays.
            if (_meshRenderer != null)
                _meshRenderer.enabled = !(CustomLiquidType == CustomLiquidType.Water && LiquidType == LiquidType.Water && PlacedWaterSheets.Active);

            if (_waterMaterial != null)
            {
                _meshRenderer.material = _waterMaterial;
                UpdateLavaWaterOverlay();
                return;
            }

            // Use CustomLiquidType if set to a non-default value, otherwise fall back to vanilla
            if (CustomLiquidType != CustomLiquidType.Water || LiquidType != LiquidType.Water)
            {
                _waterMaterial = WaterMaterialManager.GetMaterial(CustomLiquidType);
            }
            else
            {
                // 0.2.752 (Fire 10-05: placed water "is affected by the wind and gets ocean waves, our placed water should be calm
                // like inland rivers"): plain placed water wears the generated rivers' calm Custom/FiresInlandWater sheet
                // material and has no gameplay waves, so its surface stays exactly at the swim trigger's height. The ocean
                // wrap is the fallback when the inland shader is not loaded.
                _waterMaterial = VerdantsAscent.Terrain.Voxel.VoxelChunkView.ResolveSheetMaterial()
                    ?? WaterMaterialManager.GetWaterMaterial(LiquidType);
                WaveIntensity = 0f;
            }

            if (_waterMaterial != null)
            {
                _meshRenderer.material = _waterMaterial;
            }
            else
            {
                CreateFallbackMaterial();
            }

            // Rebuild the lava water overlay after material setup
            UpdateLavaWaterOverlay();
        }

        private void CreateFallbackMaterial()
        {
            var shader = Shader.Find("Standard");
            if (shader == null) return;

            _waterMaterial = new Material(shader);
            _waterMaterial.color = new Color(0.2f, 0.4f, 0.8f, 0.7f);
            _waterMaterial.SetFloat("_Mode", 3);
            _waterMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _waterMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _waterMaterial.SetInt("_ZWrite", 0);
            _waterMaterial.DisableKeyword("_ALPHATEST_ON");
            _waterMaterial.EnableKeyword("_ALPHABLEND_ON");
            _waterMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            _waterMaterial.renderQueue = 3000;

            _meshRenderer.material = _waterMaterial;
        }

        #endregion

        #region Swim Trigger

        /// <summary>
        /// Maximum depth below the water surface that the swim trigger extends.
        /// Players must be within this distance below the water surface to trigger swimming.
        /// </summary>
        private const float SWIM_TRIGGER_MAX_DEPTH = 3f;
        // 0.2.683: below that, down to the bed (minus a margin), but never deeper than this under the surface.
        private const float SWIM_TRIGGER_BED_MARGIN = 0.5f, SWIM_TRIGGER_BED_LIMIT = 30f;

        /// <summary>
        /// Maximum size of a single swim trigger BoxCollider segment (meters per axis).
        ///
        /// 2026-05-26 round-27 — reduced from 340m → 2m. The old size meant
        /// a single 20m circular disk got ONE 20×20m BoxCollider covering
        /// its entire bounding box, so the trigger fired in the CORNERS of
        /// the square where there's no actual water. User: "we sometimes
        /// extend our swim trigger out past the actual water".
        ///
        /// At 2m, the segments approximate the circular disk shape closely
        /// enough that the bounding-box overshoot is bounded to ~1m around
        /// the disk edge — and since `segHasWater` skips empty segments,
        /// only segments containing actual water cells emit a collider.
        ///
        /// PhysX 500-unit safety: at 2m × 2m × 3m a single segment's
        /// diagonal is ~4m. Plenty of headroom.
        /// </summary>
        private const float SWIM_TRIGGER_MAX_SEGMENT = 2f;

        /// <summary>
        /// Sets up the swim trigger using segmented BoxColliders.
        /// 
        /// KEY FIX: The trigger top is capped at the resting water surface height,
        /// NOT above it. Waves animate on the visual mesh only - the trigger stays
        /// at the still water level so players on banks above the water never
        /// intersect the trigger. OnTriggerStay + GetWaterSurfaceAt() handles
        /// precise wave-adjusted water level reporting.
        /// 
        /// For small/pond-mode water: a single BoxCollider on the trigger GO.
        /// For large/river water: child segment GameObjects with BoxColliders,
        /// each with per-segment Y ranges from the grid data.
        /// </summary>
        private void SetupSwimTrigger()
        {
            // 2026-05-25 round-7: Snow has no water beneath it (no _waterGO
            // either) — skip the swim trigger entirely. User: "snow should
            // not have a swim trigger or water effects". Also tear down any
            // pre-existing trigger if the combiner was previously something
            // else (water → snow type-switch).
            if (CustomLiquidType == CustomLiquidType.Snow)
            {
                if (_swimTriggerGO != null)
                {
                    var oldTrigger = _swimTriggerGO.GetComponent<WaterCombinerTrigger>();
                    if (oldTrigger != null) oldTrigger.ForceCleanup();
                    Destroy(_swimTriggerGO);
                    _swimTriggerGO = null;
                }
                return;
            }

            if (_swimTriggerGO == null)
            {
                _swimTriggerGO = new GameObject("WaterSwimTrigger");
                _swimTriggerGO.transform.SetParent(transform);
                _swimTriggerGO.transform.localPosition = Vector3.zero;
                _swimTriggerGO.transform.localRotation = Quaternion.identity;
                _swimTriggerGO.transform.localScale = Vector3.one;
                _swimTriggerGO.layer = LayerMask.NameToLayer("Water");

                var trigger = _swimTriggerGO.AddComponent<WaterCombinerTrigger>();
                trigger.Initialize(this);
            }

            // Clean up any legacy MeshCollider from the previous (broken) approach
            var oldMeshCol = _swimTriggerGO.GetComponent<MeshCollider>();
            if (oldMeshCol != null) Destroy(oldMeshCol);
            // Clean up any previous BoxCollider on the root trigger GO
            var oldBoxCol = _swimTriggerGO.GetComponent<BoxCollider>();
            if (oldBoxCol != null) Destroy(oldBoxCol);
            // Clean up child segment GameObjects from previous setups
            for (int ci = _swimTriggerGO.transform.childCount - 1; ci >= 0; ci--)
                Destroy(_swimTriggerGO.transform.GetChild(ci).gameObject);

            // Find water extents from grid data
            float minSurfaceY = float.MaxValue, maxSurfaceY = float.MinValue;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            bool hasValidWater = false;

            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    if (_surfaceHeights[idx] > -50f && _waterDepths[idx] > MIN_RENDER_DEPTH)
                    {
                        hasValidWater = true;
                        Vector3 worldPos = GridToWorld(x, z);
                        minX = Mathf.Min(minX, worldPos.x);
                        maxX = Mathf.Max(maxX, worldPos.x);
                        minZ = Mathf.Min(minZ, worldPos.z);
                        maxZ = Mathf.Max(maxZ, worldPos.z);
                        minSurfaceY = Mathf.Min(minSurfaceY, _surfaceHeights[idx]);
                        maxSurfaceY = Mathf.Max(maxSurfaceY, _surfaceHeights[idx]);
                    }
                }
            }

            if (!hasValidWater) return;

            float totalX = maxX - minX + _cellSize;
            float totalZ = maxZ - minZ + _cellSize;
            // KEY FIX: trigger top is at the resting water surface - NOT above it.
            // Waves on the visual mesh rise above this, but the trigger stays put.
            float triggerBottom = minSurfaceY - SWIM_TRIGGER_MAX_DEPTH;
            float triggerTop = maxSurfaceY;
            float totalY = triggerTop - triggerBottom;

            // 2026-05-26 round-27: REMOVED the single-BoxCollider fast path.
            // It was emitting ONE BoxCollider covering the disk's bounding
            // box — for a 20m circular disk that's a 20×20m square that
            // triggered swimming in the corners where there's no water.
            // Now ALL disks (pond or river) flow through the segmented
            // path below so the trigger area approximates the actual
            // circular shape via 2m segments.

            // Large water body or river - split into multiple BoxCollider segments
            int segsX = Mathf.Max(1, Mathf.CeilToInt(totalX / SWIM_TRIGGER_MAX_SEGMENT));
            int segsZ = Mathf.Max(1, Mathf.CeilToInt(totalZ / SWIM_TRIGGER_MAX_SEGMENT));
            float segSizeX = totalX / segsX;
            float segSizeZ = totalZ / segsZ;
            float baseX = minX - _cellSize * 0.5f;
            float baseZ = minZ - _cellSize * 0.5f;
            int segCount = 0;

            for (int sz = 0; sz < segsZ; sz++)
            {
                for (int sx = 0; sx < segsX; sx++)
                {
                    float segMinX = baseX + sx * segSizeX;
                    float segMaxX = segMinX + segSizeX;
                    float segMinZ = baseZ + sz * segSizeZ;
                    float segMaxZ = segMinZ + segSizeZ;

                    // Find local Y range within this segment
                    float segMinSurfY = float.MaxValue, segMaxSurfY = float.MinValue, segMinBed = float.MaxValue;
                    bool segHasWater = false;

                    for (int gz = 0; gz < _gridHeight; gz++)
                    {
                        for (int gx = 0; gx < _gridWidth; gx++)
                        {
                            int idx = gz * _gridWidth + gx;
                            if (_surfaceHeights[idx] <= -50f || _waterDepths[idx] <= MIN_RENDER_DEPTH)
                                continue;
                            Vector3 wp = GridToWorld(gx, gz);
                            if (wp.x < segMinX || wp.x > segMaxX || wp.z < segMinZ || wp.z > segMaxZ)
                                continue;
                            segHasWater = true;
                            float sy = _surfaceHeights[idx];
                            if (_terrainHeights != null && idx < _terrainHeights.Length && _terrainHeights[idx] < segMinBed)
                                segMinBed = _terrainHeights[idx];
                            if (sy < segMinSurfY) segMinSurfY = sy;
                            if (sy > segMaxSurfY) segMaxSurfY = sy;
                        }
                    }

                    if (!segHasWater) continue;

                    // KEY FIX: segment top is at the resting water surface for this segment
                    // 0.2.683 (H11, 10-01: a Fish2 lay on a 3.8 m pool well "given level never set"): the trigger reaches the
                    // deepest bed in the segment, so a fish or a drop on the floor of water deeper than SWIM_TRIGGER_MAX_DEPTH
                    // is still in it (vanilla's WaterVolume reaches the seabed too). Bounded to SWIM_TRIGGER_BED_LIMIT.
                    float segBottom = segMinSurfY - SWIM_TRIGGER_MAX_DEPTH;
                    if (segMinBed < float.MaxValue)
                        segBottom = Mathf.Min(segBottom, Mathf.Max(segMinBed - SWIM_TRIGGER_BED_MARGIN, segMinSurfY - SWIM_TRIGGER_BED_LIMIT));
                    float segTop = segMaxSurfY;
                    Vector3 segCenter = new Vector3(
                        (segMinX + segMaxX) * 0.5f,
                        (segBottom + segTop) * 0.5f,
                        (segMinZ + segMaxZ) * 0.5f);
                    Vector3 segSize = new Vector3(
                        segMaxX - segMinX,
                        segTop - segBottom,
                        segMaxZ - segMinZ);

                    var segGO = new GameObject($"TriggerSeg_{sx}_{sz}");
                    segGO.transform.SetParent(_swimTriggerGO.transform, false);
                    segGO.transform.position = segCenter;
                    segGO.layer = _swimTriggerGO.layer;

                    var segBox = segGO.AddComponent<BoxCollider>();
                    segBox.center = Vector3.zero;
                    segBox.size = segSize;
                    segBox.isTrigger = true;
                    segCount++;
                }
            }

            if (s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombiner] SetupSwimTrigger: {segCount} BoxCollider segments " +
                    $"({segsX}x{segsZ} grid, seg size {segSizeX:F0}x{segSizeZ:F0}m), " +
                    $"trigger top capped at resting surface (no +1.5m overshoot)");
            }
        }

        #endregion

        #region Helper Structs


        private struct CachedWaterDiskData
        {
            public float PosX, PosY, PosZ;
            public float Radius;
            public float RadiusPlusBlend;
            public float MaxDepth;
            public float EdgeFadeStart;
            public float CenterChannelRadius;
            // Shape support
            public WaterDiskShape Shape;
            public float HalfWidth;   // Half extent in X
            public float HalfLength;  // Half extent in Z
        }

        /// <summary>
        /// Calculates the signed edge distance from a point to a cached disk.
        /// Negative = inside, Positive = outside.
        /// Supports circle, square, and rectangle shapes.
        /// </summary>
        private float GetEdgeDistance(float worldX, float worldZ, ref CachedWaterDiskData cached)
        {
            float dx = worldX - cached.PosX;
            float dz = worldZ - cached.PosZ;

            if (cached.Shape == WaterDiskShape.Circle)
            {
                return Mathf.Sqrt(dx * dx + dz * dz) - cached.Radius;
            }
            else
            {
                // Signed distance to axis-aligned rectangle
                float edgeX = Mathf.Abs(dx) - cached.HalfWidth;
                float edgeZ = Mathf.Abs(dz) - cached.HalfLength;

                if (edgeX <= 0f && edgeZ <= 0f)
                {
                    // Inside: return negative distance to nearest edge
                    return Mathf.Max(edgeX, edgeZ);
                }
                else if (edgeX > 0f && edgeZ > 0f)
                {
                    // Outside corner: Euclidean distance to corner
                    return Mathf.Sqrt(edgeX * edgeX + edgeZ * edgeZ);
                }
                else
                {
                    // Outside one edge only
                    return Mathf.Max(edgeX, edgeZ);
                }
            }
        }

        /// <summary>
        /// Calculates blend weight for a point based on edge distance.
        /// Works for both circle and rectangular shapes via GetEdgeDistance.
        /// </summary>
        private float CalculateBlendWeight(float edgeDist)
        {
            if (edgeDist <= -EDGE_FADE_DISTANCE)
            {
                // Fully inside disk
                return 1f;
            }
            else if (edgeDist <= 0f)
            {
                // In the edge fade zone (near the edge but still inside)
                float t = -edgeDist / EDGE_FADE_DISTANCE;
                return t;
            }
            else if (edgeDist <= BlendDistance)
            {
                // Outside the disk but within blend zone (for disk-to-disk blending)
                float t = 1f - (edgeDist / BlendDistance);
                return t * 0.5f; // Half strength in blend zone
            }
            else
            {
                return 0f;
            }
        }

        /// <summary>
        /// Legacy overload for circle-only distance-based weight calculation.
        /// </summary>
        private float CalculateBlendWeight(float dist, float radius)
        {
            float edgeDist = dist - radius;
            return CalculateBlendWeight(edgeDist);
        }

        #endregion
    }

    /// <summary>
    /// Trigger handler for swim detection.
    /// Tracks characters in water and properly clears their state when water is destroyed.
    /// </summary>
    public class WaterCombinerTrigger : MonoBehaviour
    {
        private WaterMeshCombiner _combiner;
        private HashSet<IWaterInteractable> _inWater = new HashSet<IWaterInteractable>();
        private LiquidType _liquidType = LiquidType.Water;

        /// <summary>
        /// Layer mask for terrain blocking raycasts. Cached for performance.
        /// </summary>
        private static int s_blockingLayerMask = -1;

        public void Initialize(WaterMeshCombiner combiner)
        {
            _combiner = combiner;
            _liquidType = combiner != null ? combiner.LiquidType : LiquidType.Water;

            if (s_blockingLayerMask == -1)
            {
                s_blockingLayerMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            }
        }

        /// <summary>
        /// Checks if solid terrain/geometry blocks the path between the interactable and the water surface.
        /// This prevents characters in caves from being affected by water far above them.
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
            if (_combiner == null) return;

            var interactable = GetWaterInteractable(other);
            if (interactable == null) return;

            Transform t = interactable.GetTransform();
            if (t == null) return;

            float waterLevel = _combiner.GetWaterSurfaceAt(t.position);
            if (waterLevel > float.MinValue)
            {
                // CAVE FIX: Check if terrain blocks the path to water
                if (IsBlockedByTerrain(t.position, waterLevel))
                    return;

                // Track this interactable
                if (!_inWater.Contains(interactable))
                {
                    _inWater.Add(interactable);
                    // Increment water count so it properly tracks overlapping water volumes
                    interactable.Increment(_liquidType);
                }

                interactable.SetLiquidLevel(waterLevel, _liquidType, null);
            }
        }

        private void OnTriggerStay(Collider other)
        {
            if (_combiner == null) return;

            var interactable = GetWaterInteractable(other);
            if (interactable == null) return;

            Transform t = interactable.GetTransform();
            if (t == null) return;

            float waterLevel = _combiner.GetWaterSurfaceAt(t.position);
            if (waterLevel > float.MinValue)
            {
                // CAVE FIX: Check if terrain blocks the path to water
                if (IsBlockedByTerrain(t.position, waterLevel))
                {
                    // Blocked - if they were previously tracked, remove them
                    if (_inWater.Remove(interactable))
                    {
                        if (interactable.Decrement(_liquidType) == 0)
                        {
                            interactable.SetLiquidLevel(-10000f, _liquidType, null);
                        }
                    }
                    return;
                }

                // Still in valid water - update level
                if (!_inWater.Contains(interactable))
                {
                    // Entered water area (can happen if they walked into disk range
                    // while already inside the box trigger)
                    _inWater.Add(interactable);
                    interactable.Increment(_liquidType);
                }
                interactable.SetLiquidLevel(waterLevel, _liquidType, null);

                // Apply damage for hazardous liquids
                var damageHandler = _combiner.GetDamageHandler();
                if (damageHandler != null)
                {
                    var character = t.GetComponentInParent<Character>();
                    if (character != null && t.position.y < waterLevel)
                    {
                        damageHandler.OnCharacterInLiquid(character);
                    }
                }
            }
            else if (_inWater.Contains(interactable))
            {
                // Was in water but moved outside disk geometry while still inside
                // the box trigger - clear their water state immediately.
                _inWater.Remove(interactable);
                if (interactable.Decrement(_liquidType) == 0)
                {
                    interactable.SetLiquidLevel(-10000f, _liquidType, null);
                }
            }
        }

        private void OnTriggerExit(Collider other)
        {
            var interactable = GetWaterInteractable(other);
            if (interactable == null) return;

            // Remove from tracking
            if (_inWater.Remove(interactable))
            {
                // Apply lingering damage effects (burning from lava, etc.)
                if (_combiner != null)
                {
                    var damageHandler = _combiner.GetDamageHandler();
                    if (damageHandler != null)
                    {
                        Transform t = interactable.GetTransform();
                        if (t != null)
                        {
                            var character = t.GetComponentInParent<Character>();
                            if (character != null)
                            {
                                damageHandler.OnCharacterExitLiquid(character);
                            }
                        }
                    }
                }

                // Decrement water count
                if (interactable.Decrement(_liquidType) == 0)
                {
                    // No longer in any water of this type - clear level
                    interactable.SetLiquidLevel(-10000f, _liquidType, null);
                }
            }
        }

        private void OnDestroy()
        {
            // CRITICAL: Clean up all tracked characters when this trigger is destroyed
            // This ensures they stop swimming when water is deleted
            foreach (var interactable in _inWater)
            {
                if (interactable != null)
                {
                    try
                    {
                        // Decrement water count
                        if (interactable.Decrement(_liquidType) == 0)
                        {
                            // Set water level far below to trigger exit
                            interactable.SetLiquidLevel(-10000f, _liquidType, null);
                        }
                    }
                    catch (System.Exception)
                    {
                        // Ignore errors from already-destroyed objects
                    }
                }
            }
            _inWater.Clear();
            _combiner = null;

            FiresLogger.LogDebug("[WaterCombinerTrigger] OnDestroy: Cleaned up all tracked water interactables");
        }

        /// <summary>
        /// Gets the IWaterInteractable from a collider, checking both the collider's 
        /// GameObject and the attached Rigidbody's GameObject.
        /// </summary>
        private IWaterInteractable GetWaterInteractable(Collider collider)
        {
            if (collider == null) return null;

            // Check attached rigidbody first (for characters with multiple colliders)
            if (collider.attachedRigidbody != null)
            {
                var interactable = collider.attachedRigidbody.GetComponent<IWaterInteractable>();
                if (interactable != null)
                    return interactable;
            }

            // Check parent hierarchy (for nested colliders)
            var parentInteractable = collider.GetComponentInParent<IWaterInteractable>();
            if (parentInteractable != null)
                return parentInteractable;

            // Fallback to collider's gameobject
            return collider.GetComponent<IWaterInteractable>();
        }

        /// <summary>
        /// Force clears all tracked interactables. Call this before destroying the combiner.
        /// </summary>
        public void ForceCleanup()
        {
            foreach (var interactable in _inWater)
            {
                if (interactable != null)
                {
                    try
                    {
                        // Force decrement and clear
                        interactable.Decrement(_liquidType);
                        interactable.SetLiquidLevel(-10000f, _liquidType, null);
                    }
                    catch (System.Exception)
                    {
                        // Ignore errors
                    }
                }
            }
            _inWater.Clear();
        }

        /// <summary>
        /// Re-validates all tracked interactables against current water state.
        /// Call this after mopping water to remove interactables that are no longer in water.
        /// </summary>
        public void RevalidateTrackedInteractables()
        {
            if (_combiner == null)
            {
                ForceCleanup();
                return;
            }

            var toRemove = new List<IWaterInteractable>();

            foreach (var interactable in _inWater)
            {
                if (interactable == null)
                {
                    toRemove.Add(interactable);
                    continue;
                }

                Transform t = interactable.GetTransform();
                if (t == null)
                {
                    toRemove.Add(interactable);
                    continue;
                }

                // Check if there's still water at this position
                float waterLevel = _combiner.GetWaterSurfaceAt(t.position);

                if (waterLevel <= float.MinValue)
                {
                    // No water here anymore - remove from tracking
                    toRemove.Add(interactable);

                    try
                    {
                        // Decrement water count and clear level if no longer in any water
                        if (interactable.Decrement(_liquidType) == 0)
                        {
                            interactable.SetLiquidLevel(-10000f, _liquidType, null);
                        }
                    }
                    catch (System.Exception)
                    {
                        // Ignore errors from invalid objects
                    }
                }
                else
                {
                    // Still in water - update level in case surface changed
                    try
                    {
                        interactable.SetLiquidLevel(waterLevel, _liquidType, null);
                    }
                    catch (System.Exception)
                    {
                        // Ignore errors
                    }
                }
            }

            // Remove invalid entries
            foreach (var interactable in toRemove)
            {
                _inWater.Remove(interactable);
            }

            if (toRemove.Count > 0)
            {
                FiresLogger.LogDebug($"[WaterCombinerTrigger] RevalidateTrackedInteractables: Removed {toRemove.Count} interactables no longer in water");
            }
        }
    }
}
