using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Combines multiple WaterDisks into a single water mesh (like DirtFloorMeshCombiner for terrain).
    /// 
    /// KEY CONCEPT: Water is painted like terrain, but inverted:
    /// - Terrain: You paint solid surface, mesh goes DOWN from painted height
    /// - Water: You paint water SURFACE level, water fills DOWN to terrain (container)
    /// 
    /// The combiner:
    /// 1. Takes WaterDisk positions (painted surface levels)
    /// 2. Samples terrain height at each grid point (the container)
    /// 3. Where surface > terrain, water exists (depth = surface - terrain)
    /// 4. Generates mesh for water surface with waves
    /// 5. Creates collision trigger for swimming
    /// 
    /// This approach allows water to naturally "fill" terrain containers without
    /// complex fluid simulation - just paint the surface level you want!
    /// </summary>
    public partial class WaterMeshCombiner : MonoBehaviour
    {
        #region Constants

        /// <summary>
        /// Maximum grid size to prevent memory issues.
        /// </summary>
        public const int MAX_GRID_SIZE = 128;

        /// <summary>
        /// Distance for blending water edges between disks.
        /// </summary>
        public const float BlendDistance = 0.25f;   // 0.2.752: was 1.5 m past every disk's edge (Fire: the mesh larger than the ruler circle)

        /// <summary>
        /// Padding around bounds for grid generation.
        /// </summary>
        public const float GridPadding = 0.005f;

        /// <summary>
        /// Minimum water depth to render (avoids micro-puddles).
        /// </summary>
        public const float MinWaterDepth = 0.1f;

        #endregion

        #region Water Disks

        /// <summary>
        /// All water disks combined by this combiner.
        /// </summary>
        private List<WaterDisk> _waterDisks = new List<WaterDisk>();

        /// <summary>
        /// Read-only access to water disks.
        /// </summary>
        public IReadOnlyList<WaterDisk> WaterDisks => _waterDisks;

        #endregion

        #region Grid Data

        /// <summary>
        /// Combined bounds of all water disks.
        /// </summary>
        private Bounds _combinedBounds;

        /// <summary>
        /// Grid width (X axis).
        /// </summary>
        private int _gridWidth;

        /// <summary>
        /// Grid height (Z axis).
        /// </summary>
        private int _gridHeight;

        /// <summary>
        /// Size of each grid cell in meters.
        /// </summary>
        private float _cellSize;

        /// <summary>
        /// Origin of the grid in world space.
        /// </summary>
        private Vector3 _gridOrigin;

        /// <summary>
        /// Water SURFACE heights at each grid point.
        /// This is the painted/blended surface level.
        /// -100 means no water at this point.
        /// </summary>
        private float[] _surfaceHeights;

        /// <summary>
        /// Terrain (container) heights at each grid point.
        /// Water exists where _surfaceHeights > _terrainHeights.
        /// </summary>
        private float[] _terrainHeights;

        /// <summary>
        /// Water depth at each grid point (surface - terrain).
        /// Only valid where > MinWaterDepth.
        /// </summary>
        private float[] _waterDepths;

        #endregion

        #region Mesh Components

        /// <summary>
        /// The combined water surface mesh.
        /// </summary>
        private Mesh _waterMesh;

        /// <summary>
        /// Mesh filter component.
        /// </summary>
        private MeshFilter _meshFilter;

        /// <summary>
        /// Mesh renderer component.
        /// </summary>
        private MeshRenderer _meshRenderer;

        /// <summary>
        /// Water material (captured from vanilla).
        /// </summary>
        private Material _waterMaterial;

        /// <summary>
        /// Trigger handler for swim detection.
        /// Child GameObject with segmented BoxColliders capped at the resting water surface.
        /// </summary>
        private GameObject _swimTriggerGO;

        #endregion

        #region State

        /// <summary>
        /// Whether mesh needs regeneration.
        /// </summary>
        private bool _needsRegeneration = true;

        /// <summary>
        /// Time since last mesh update.
        /// </summary>
        private float _regenerationTimer;

        /// <summary>
        /// Regeneration delay to batch changes.
        /// </summary>
        private const float REGENERATION_DELAY = 0.1f;

        /// <summary>
        /// Whether this combiner is restored from persistence.
        /// When true, disks have already been grouped correctly and don't need recombination.
        /// </summary>
        private bool _restoredFromPersistence;

        /// <summary>
        /// Whether a save is pending (debounce timer active).
        /// During rapid placement, we batch all disks and save once.
        /// </summary>
        private bool _pendingSave;

        /// <summary>
        /// Countdown timer for debounced save. Resets on each new disk.
        /// </summary>
        private float _saveDebounceTimer;

        /// <summary>
        /// Debounce delay before saving. Allows rapid disk placement to batch.
        /// </summary>
        private const float SAVE_DEBOUNCE_TIME = 0.3f;

        /// <summary>
        /// Tracks which grid rows have been modified since the last save.
        /// Used for partial segment saves - only dirty segments are written.
        /// -1 means no dirty rows. Updated by MopWaterAt and ForceRegenerate.
        /// </summary>
        private int _dirtyRowMin = -1;
        private int _dirtyRowMax = -1;

        /// <summary>
        /// Whether ALL rows are dirty (full save needed).
        /// Set after ForceRegenerate or initial generation.
        /// </summary>
        private bool _allRowsDirty = false;

        /// <summary>
        /// Liquid type for this water body (vanilla enum).
        /// </summary>
        public LiquidType LiquidType { get; set; } = LiquidType.Water;

        /// <summary>
        /// Extended liquid type for new liquid types (ice, lava, boiling, freezing).
        /// Takes precedence over <see cref="LiquidType"/> for material and behavior selection.
        /// </summary>
        public CustomLiquidType CustomLiquidType { get; set; } = CustomLiquidType.Water;

        /// <summary>
        /// 0.2.873 (Fire via [lead] 10-09, ported from Galaxies 0.1.39): true while this water is iced only for Vedr's winter.
        /// Seasonal ice is never saved: the anchor stores plain water, so it can't outlast winter or Vedr.
        /// </summary>
        public bool SeasonalIce { get; private set; }

        /// <summary>The type the anchor saves: plain water while the ice is only seasonal.</summary>
        public CustomLiquidType SavedCustomLiquidType => SeasonalIce ? CustomLiquidType.Water : CustomLiquidType;

        /// <summary>Seasonal freeze/thaw (SeasonalWaterFreeze). Freezes only plain water; thaws only its own seasonal ice.</summary>
        public void SetSeasonalIce(bool frozen)
        {
            if (frozen == SeasonalIce) return;
            if (frozen && CustomLiquidType != CustomLiquidType.Water) return;
            SeasonalIce = frozen;
            ApplyCustomLiquidType(frozen ? CustomLiquidType.Ice : CustomLiquidType.Water, false);
        }

        /// <summary>
        /// Damage handler for hazardous liquids. Null for safe liquids (water, tar, ice).
        /// </summary>
        private LiquidDamageHandler _damageHandler;

        /// <summary>
        /// Ambient VFX controller (shoreline splash, waterfall spray, surface debris, audio).
        /// Ephemeral - no ZDO, no persistence. Rebuilt after each mesh regeneration.
        /// </summary>
        private WaterAmbientVFX _ambientVFX;

        /// <summary>
        /// Lava water overlay - thin water layer rendered above the lava surface.
        /// The water shader's refraction over the opaque lava material produces a
        /// phenomenal molten-glass look. Only created when CustomLiquidType == Lava.
        /// </summary>
        private LavaWaterOverlayController _lavaOverlay;

        /// <summary>
        /// Wave intensity multiplier.
        /// </summary>
        public float WaveIntensity { get; set; } = 0.5f;

        /// <summary>
        /// Reference to the ZDO-backed anchor that persists this combiner.
        /// The anchor saves the COMBINED mesh state, not individual disks.
        /// </summary>
        public WaterMeshCombinerAnchor Anchor { get; set; }

        /// <summary>
        /// Gets the grid width.
        /// </summary>
        public int GridWidth => _gridWidth;

        /// <summary>
        /// Gets the grid height.
        /// </summary>
        public int GridHeight => _gridHeight;

        /// <summary>
        /// Gets the cell size.
        /// </summary>
        public float CellSize => _cellSize;

        /// <summary>
        /// Gets the settled water level (for pond mode).
        /// Returns -1 in river mode.
        /// </summary>
        public float SettledWaterLevel => _settledWaterLevel;

        /// <summary>
        /// Gets whether this is pond mode (flat water level) or river mode (sloped).
        /// </summary>
        public bool IsPondMode => _isPondMode;

        #endregion

        #region Static Registry

        /// <summary>
        /// All active water combiners in the scene.
        /// </summary>
        private static List<WaterMeshCombiner> s_allCombiners = new List<WaterMeshCombiner>();

        /// <summary>
        /// Read-only access to all combiners.
        /// </summary>
        public static IReadOnlyList<WaterMeshCombiner> AllCombiners => s_allCombiners;

        /// <summary>
        /// When true, enables verbose debug logging. Set to false to reduce log spam during world load.
        /// </summary>
        public static bool s_verboseLogging = false;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            // Create mesh
            _waterMesh = new Mesh();
            _waterMesh.name = "WaterCombinedMesh";

            // Setup components
            _meshFilter = gameObject.AddComponent<MeshFilter>();
            _meshFilter.sharedMesh = _waterMesh;

            _meshRenderer = gameObject.AddComponent<MeshRenderer>();

            // Set layer for water
            gameObject.layer = LayerMask.NameToLayer("Water");
        }

        private void Start()
        {
            // Register if not already added (GetOrCreateCombinerFor pre-registers for same-frame lookups)
            if (!s_allCombiners.Contains(this))
            {
                s_allCombiners.Add(this);
            }
            SetupMaterial();
        }

        private void OnDestroy()
        {
            // Flush any pending save before destruction
            ForceImmediateSave();

            s_allCombiners.Remove(this);

            // CRITICAL: Disconnect mesh from components BEFORE destroying it.
            // During shutdown, Unity's RuntimeNavMeshBuilder tries to process meshes
            // referenced by MeshFilter. If the mesh data is partially destroyed,
            // it causes "invalid vertex data" error spam.
            if (_meshFilter != null)
                _meshFilter.sharedMesh = null;
            if (_meshRenderer != null)
                _meshRenderer.enabled = false;

            if (_waterMesh != null)
            {
                _waterMesh.Clear();
                Destroy(_waterMesh);
            }

            // CRITICAL: Force cleanup the swim trigger BEFORE destroying it
            // This ensures all characters are properly removed from water state
            if (_swimTriggerGO != null)
            {
                var trigger = _swimTriggerGO.GetComponent<WaterCombinerTrigger>();
                if (trigger != null)
                {
                    trigger.ForceCleanup();
                }
                Destroy(_swimTriggerGO);
            }

            // Clean up lava water overlay
            if (_lavaOverlay != null)
            {
                _lavaOverlay.Cleanup();
                Destroy(_lavaOverlay);
                _lavaOverlay = null;
            }

            // Clean up frozen water controller
            var frozenController = GetComponent<FrozenWaterController>();
            if (frozenController != null)
            {
                frozenController.Thaw();
            }

            // Clean up ambient VFX
            if (_ambientVFX != null)
            {
                _ambientVFX.CleanupVFX();
                _ambientVFX = null;
            }

            if (s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombiner] OnDestroy at {transform.position}: Cleaned up all water components");
        }

        private void Update()
        {
            if (_needsRegeneration)
            {
                _regenerationTimer += Time.deltaTime;
                if (_regenerationTimer >= REGENERATION_DELAY)
                {
                    RegenerateMesh();
                    _needsRegeneration = false;
                    _regenerationTimer = 0f;
                }
            }

            // Debounced save: wait for rapid placement to settle before creating anchor + saving
            if (_pendingSave)
            {
                _saveDebounceTimer -= Time.deltaTime;
                if (_saveDebounceTimer <= 0f)
                {
                    PerformDebouncedSave();
                }
            }

            if (_reseatPending) TickReseat();
        }

        // 0.2.682 ([lead] H10, 10-01: a va_pool pond saved by the server came back DRY after a restart, its anchors present).
        // A restored combiner replays the surface/terrain grid of whoever saved it, and only the server saves. A dedicated
        // server samples terrain as min(WorldGenerator height, GetGroundHeight) with no voxel colliders (VoxelWrap), so its
        // grid never sees a carved bed and puts the pond above the ground. A peer with real terrain rebuilds a restored
        // combiner from its disks once, when the bed under every disk is loaded (a terrain ray at the disk centre lands
        // below its surface, the test the pool tool uses before laying water), or after ReseatCapSeconds anyway.
        // Gelled / settling volumes keep their grid (Round-56: it is authoritative data).
        private const float ReseatPollSeconds = 0.5f, ReseatCapSeconds = 30f, ReseatBedMargin = 0.3f;
        private const int ReseatProbes = 8;
        private bool _reseatPending;
        private float _reseatSince, _reseatNextPoll;
        private static int s_reseatTerrainMask;

        // 0.2.683: the dedicated server reseats too, against VoxelWorld's density (TryVoxelGround) instead of colliders. Its
        // zone data exists only near players, so there it waits, uncapped and polling slowly, until the density under every
        // probed disk is loaded.
        private const float ReseatPollSecondsServer = 2f;

        private bool ReseatGroundReady(Vector3 p, float maxDepth, int mask)
        {
            if (ZNet.instance != null && ZNet.instance.IsDedicated())
                return VerdantsAscent.Terrain.Voxel.VoxelWorld.TrySampleSurfaceY(p.x, p.z, p.y + 2f, p.y - 30f - maxDepth, out _, out bool wall) || wall;
            return Physics.Raycast(new Vector3(p.x, p.y + 20f, p.z), Vector3.down, out RaycastHit hit, 30f + maxDepth, mask)
                && hit.point.y <= p.y - ReseatBedMargin;
        }

        private void ArmReseat()
        {
            if (ZNet.instance == null) return;
            // A dedicated server without voxel terrain has no density to wait for; it keeps vanilla's sample as before.
            if (ZNet.instance.IsDedicated() && !VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.StreamerRuns) return;
            _reseatPending = true;
            _reseatSince = Time.realtimeSinceStartup;
            _reseatNextPoll = 0f;
        }

        private void TickReseat()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _reseatNextPoll) return;
            bool server = ZNet.instance != null && ZNet.instance.IsDedicated();
            _reseatNextPoll = now + (server ? ReseatPollSecondsServer : ReseatPollSeconds);
            if (_isGelled || _isSettling) { _reseatPending = false; return; }
            float waited = now - _reseatSince;
            if (_waterDisks.Count == 0) { if (waited >= ReseatCapSeconds) _reseatPending = false; return; }
            // The dedicated server has no colliders to fall back on: it waits for the density, however long that takes.
            float cap = server ? float.PositiveInfinity : ReseatCapSeconds;
            if (s_reseatTerrainMask == 0) s_reseatTerrainMask = LayerMask.GetMask("terrain");
            // At most ReseatProbes disks, spread along the list (an auto river can hold hundreds).
            int stride = Mathf.Max(1, _waterDisks.Count / ReseatProbes), probed = 0, seen = 0;
            for (int i = 0; i < _waterDisks.Count; i += stride)
            {
                probed++;
                if (ReseatGroundReady(_waterDisks[i].Position, _waterDisks[i].MaxDepth, s_reseatTerrainMask)) seen++;
            }
            if (seen < probed && waited < cap) return;
            _reseatPending = false;
            // Only a grid that is dry where its own disks stand is rebuilt: restored water that already reads wet (hydrology,
            // auto water, anything saved on a peer that saw the same terrain) keeps its grid, so a world load costs nothing.
            int dry = 0;
            for (int i = 0; i < _waterDisks.Count; i += stride)
                if (GetWaterSurfaceAt(_waterDisks[i].Position) < _waterDisks[i].Position.y - ReseatBedMargin) dry++;
            if (dry == 0) return;
            ForceRegenerate();
            FiresLogger.LogDebug($"[FiresWater] restored water at ({transform.position.x:F0},{transform.position.y:F1},{transform.position.z:F0}) "
                + $"read dry at {dry}/{probed} probed disk centre(s) of {_waterDisks.Count} on its saved grid; rebuilt from its disks on {(server ? "the server's voxel data" : "this peer's terrain")} after {waited:F1} s "
                + (seen == probed ? "(bed loaded under every probed disk)" : $"(the {ReseatCapSeconds:F0} s cap; bed seen under {seen}/{probed})"));
        }

        #endregion

        #region Public API

        /// <summary>
        /// Sets whether this combiner was restored from persistence.
        /// When true, the disks are already correctly grouped and don't need recombination.
        /// </summary>
        public void SetRestoredFromPersistence(bool restored)
        {
            _restoredFromPersistence = restored;
            if (restored) ArmReseat();
        }

        /// <summary>
        /// Adds a new water disk to this combiner (internal - no auto-save).
        /// Used by GetOrCreateCombinerFor and AutoWaterGenerator infill pass
        /// to add disks without triggering individual saves.
        /// </summary>
        internal void AddWaterDiskInternal(WaterDisk disk)
        {
            if (disk == null) return;
            if (_waterDisks.Contains(disk)) return;

            _waterDisks.Add(disk);
            MarkDirty();

            if (s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombiner] AddWaterDiskInternal: Added disk at {disk.Position}, radius={disk.Radius} (total: {_waterDisks.Count})");
            }
        }

        /// <summary>
        /// Adds a new water disk to this combiner.
        /// Saves to anchor after adding for persistence.
        /// NOTE: For bulk operations, use AddWaterDisksBatch instead.
        /// </summary>
        public void AddWaterDisk(WaterDisk disk)
        {
            AddWaterDiskInternal(disk);

            // Save to anchor for persistence (like terrain does)
            SaveToAnchor();
        }

        /// <summary>
        /// Adds multiple water disks in a batch without triggering regeneration for each one.
        /// Used during persistence load to efficiently restore combiners.
        /// </summary>
        /// <param name="disks">The disks to add</param>
        /// <param name="skipRegeneration">If true, doesn't mark dirty (for restoration where ForceRegenerate will be called)</param>
        public void AddWaterDisksBatch(IEnumerable<WaterDisk> disks, bool skipRegeneration = false)
        {
            if (disks == null) return;

            int addedCount = 0;
            foreach (var disk in disks)
            {
                if (disk == null) continue;
                if (_waterDisks.Contains(disk)) continue;

                _waterDisks.Add(disk);
                addedCount++;
            }

            if (addedCount > 0)
            {
                if (!skipRegeneration)
                {
                    // Mark dirty once for all added disks
                    MarkDirty();
                }
                if (s_verboseLogging)
                {
                    FiresLogger.LogInfo($"[WaterMeshCombiner] Batch added {addedCount} water disks (skipRegen={skipRegeneration})");
                }
            }
        }

        /// <summary>
        /// Removes a water disk from this combiner.
        /// When the last disk is removed, permanently destroys the anchor ZDO
        /// so the water won't be restored on next login.
        /// </summary>
        public void RemoveWaterDisk(WaterDisk disk)
        {
            if (disk == null) return;

            if (_waterDisks.Remove(disk))
            {
                MarkDirty();

                // Destroy if no disks left
                if (_waterDisks.Count == 0)
                {
                    // 2026-05-25 round-4: use DestroyPermanently() so the
                    // file-based WaterCombinerCache also gets cleared via
                    // its OnCombinerPermanentlyDeleted hook. Previously this
                    // branch destroyed the anchor ZDO + GameObject but left
                    // a stale cache entry behind — on next login the cache
                    // restoration spawned the water back even though the
                    // anchor was gone (user complaint: "deleted water
                    // doesnt properly remove its data from the anchor and
                    // save, causing it to be restored when we log back in
                    // sometimes").
                    DestroyPermanently();
                }
                else
                {
                    // Still have disks remaining - save updated state to anchor
                    SaveToAnchor();
                }
            }
        }

        /// <summary>
        /// Gets all water disks in this combiner.
        /// </summary>
        public IReadOnlyList<WaterDisk> GetWaterDisks()
        {
            return _waterDisks;
        }

        /// <summary>
        /// Clears all water disks from this combiner.
        /// </summary>
        public void ClearAllDisks()
        {
            _waterDisks.Clear();
            MarkDirty();

            // Clear arrays
            _surfaceHeights = null;
            _terrainHeights = null;
            _waterDepths = null;

            // Clear mesh
            if (_waterMesh != null)
            {
                _waterMesh.Clear();
            }

            if (s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombiner] Cleared all disks from combiner at {transform.position}");
            }
        }

        /// <summary>
        /// Permanently destroys this combiner AND its anchor ZDO.
        /// This ensures the water won't be restored on next login.
        /// Use this instead of Destroy(gameObject) when deleting water.
        /// </summary>
        public void DestroyPermanently()
        {
            if (s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombiner] DestroyPermanently at {transform.position}: {_waterDisks.Count} disks");

            // Notify file cache of permanent deletion
            WaterCombinerCache.OnCombinerPermanentlyDeleted(this);

            // CRITICAL: Destroy anchor ZDO first so it won't restore on next login
            if (Anchor != null)
            {
                Anchor.DestroyPermanently();
                Anchor = null;
            }

            _waterDisks.Clear();

            // Clear arrays
            _surfaceHeights = null;
            _terrainHeights = null;
            _waterDepths = null;

            // IMMEDIATE VISUAL REMOVAL: Destroy() is deferred to end of frame,
            // so the mesh renderer stays active and the water remains visible until
            // then. We disable/clear everything NOW so the water vanishes the same
            // frame ClearAllWaterInternal runs. Without this, consecutive
            // clear + generate leaves stale meshes visible during generation.
            if (_meshRenderer != null)
                _meshRenderer.enabled = false;
            if (_meshFilter != null)
                _meshFilter.sharedMesh = null;
            if (_waterMesh != null)
            {
                _waterMesh.Clear();
            }
            if (_swimTriggerGO != null)
            {
                var trigger = _swimTriggerGO.GetComponent<WaterCombinerTrigger>();
                if (trigger != null)
                    trigger.ForceCleanup();
                Destroy(_swimTriggerGO);
                _swimTriggerGO = null;
            }

            // Clean up frozen water controller if present
            var frozenController = GetComponent<FrozenWaterController>();
            if (frozenController != null)
            {
                frozenController.Thaw();
                Destroy(frozenController);
            }

            // Prevent any pending save from running after we're gone
            _pendingSave = false;
            _needsRegeneration = false;

            Destroy(gameObject);
        }

        /// <summary>
        /// Marks the mesh as needing regeneration.
        /// </summary>
        public void MarkDirty()
        {
            _needsRegeneration = true;
            _regenerationTimer = 0f;
        }

        /// <summary>
        /// Forces immediate mesh regeneration.
        /// Saves to anchor after regeneration to persist combined mesh state.
        /// </summary>
        public void ForceRegenerate()
        {
            _needsRegeneration = false;
            _regenerationTimer = 0f;
            RegenerateMesh();

            // Mark all rows dirty since full regeneration occurred
            MarkAllRowsDirty();

            // Save the combined mesh state to anchor (like terrain does)
            SaveToAnchor();
        }

        /// <summary>
        /// Gets the water surface level at a world position.
        /// Returns float.MinValue if no water at position.
        /// </summary>
        public float GetWaterSurfaceAt(Vector3 worldPos)
        {
            if (_surfaceHeights == null || _waterDepths == null)
                return float.MinValue;

            // XZ bounds check (ignore Y - we do our own vertical check below)
            if (!_combinedBounds.Contains(new Vector3(worldPos.x, _combinedBounds.center.y, worldPos.z)))
                return float.MinValue;

            // CRITICAL: Check if position is actually within a water disk's geometry.
            // The bounding box is rectangular but water disks are circular - without this
            // check, players in the corners of the bounding box (far from any disk) would
            // still get a valid water level and swim in the air.
            if (!IsWithinAnyDiskRadius(worldPos))
                return float.MinValue;

            // Convert to grid coordinates
            if (!WorldToGrid(worldPos, out int x, out int z))
                return float.MinValue;

            int idx = z * _gridWidth + x;
            if (idx < 0 || idx >= _waterDepths.Length)
                return float.MinValue;

            // Check if water exists at this grid cell
            if (_waterDepths[idx] < MinWaterDepth)
                return float.MinValue;

            float baseHeight = _surfaceHeights[idx];
            float wave = CalculateWaveHeight(worldPos, _waterDepths[idx]);
            float surfaceLevel = baseHeight + wave;

            // CRITICAL Y-PROXIMITY CHECK:
            // Only return a water level if the query position is vertically near the water.
            // Without this, a player in a cave 30m below a pond on the surface would still
            // get a valid water level, causing them to swim in mid-air.
            //
            // Allow: player is above water bottom (surface - depth) and below surface + 0.8m margin.
            // The 0.8m margin above surface handles wading into water but prevents standing
            // on banks 1-2m above the water from triggering swim state.
            float waterBottom = surfaceLevel - _waterDepths[idx];
            if (worldPos.y > surfaceLevel + 0.8f || worldPos.y < waterBottom - 1f)
                return float.MinValue;

            return surfaceLevel;
        }

        /// <summary>
        /// Gets the water depth at a world position.
        /// Returns 0 if no water at position.
        /// </summary>
        public float GetWaterDepthAt(Vector3 worldPos)
        {
            if (_waterDepths == null)
                return 0f;

            if (!WorldToGrid(worldPos, out int x, out int z))
                return 0f;

            int idx = z * _gridWidth + x;
            if (idx < 0 || idx >= _waterDepths.Length)
                return 0f;

            return Mathf.Max(0f, _waterDepths[idx]);
        }

        /// <summary>
        /// Checks if a world position is inside this water body.
        /// </summary>
        public bool IsPositionInWater(Vector3 worldPos)
        {
            float surface = GetWaterSurfaceAt(worldPos);
            return surface > float.MinValue && worldPos.y < surface;
        }

        /// <summary>
        /// Checks if this combiner's bounds contain a position (horizontal only).
        /// Uses actual disk geometry rather than just the rectangular bounding box.
        /// </summary>
        public bool ContainsPosition(Vector3 worldPos)
        {
            // XZ check: is the position within the horizontal footprint?
            if (!_combinedBounds.Contains(new Vector3(worldPos.x, _combinedBounds.center.y, worldPos.z)))
                return false;

            // CRITICAL: Check actual disk geometry, not just bounding box.
            // The bounding box is rectangular but disks are circular - corners of the
            // bounding box extend far beyond any actual water.
            if (!IsWithinAnyDiskRadius(worldPos))
                return false;

            // Y check: is the position vertically near the water?
            // Allow a generous range: from (lowest surface - max depth - 1m) to (highest surface + 2m)
            float yMin = _combinedBounds.min.y - 1f; // bounds.min.y is roughly water bottom
            float yMax = _combinedBounds.max.y + 2f;  // bounds.max.y is roughly water surface
            return worldPos.y >= yMin && worldPos.y <= yMax;
        }

        /// <summary>
        /// Gets the combined bounds of this water body.
        /// </summary>
        public Bounds GetCombinedBounds()
        {
            return _combinedBounds;
        }

        /// <summary>
        /// Checks if a world position is within ANY water disk's radius (+ blend distance).
        /// This is the true geometry check - water disks are circular, not rectangular.
        /// Used to prevent false positives from the rectangular bounding box.
        /// </summary>
        internal bool IsWithinAnyDiskRadius(Vector3 worldPos)   // 0.2.873: internal for SeasonalWaterFreeze.OwnsWater
        {
            for (int i = 0; i < _waterDisks.Count; i++)
            {
                if (_waterDisks[i].ContainsPositionWithBlend(worldPos, BlendDistance))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Tiny XZ margin used by the BuildWaterMesh strict-footprint cull
        /// so adjacent disks placed within a sliver of each other (drag-fire,
        /// same-ruler-pass) still merge into one pond. Kept at 0.1m so the
        /// rendered footprint is effectively flush with the ruler radius —
        /// user has asked repeatedly for the visible mesh to match the
        /// ruler's circle, not extend into a square bounding box.
        /// </summary>
        private const float DISK_FOOTPRINT_CULL_MARGIN = 0.1f;

        /// <summary>
        /// Strict disk-shape check used by BuildWaterMesh to keep the rendered
        /// mesh inside the ruler's footprint. Without this cull, FillSurfaceGaps's
        /// 3-iteration outward bleed and the rectangular bounding box make small
        /// circular disks render as squares — user complaint 2026-05-25 (round 5):
        /// "our custom water still always draws as a square. cant we fix this?
        /// cant we make it draw as a circular shape like our terrain tools do?
        /// ive asked many times today for this to be fixed".
        ///
        /// Bank extension still works WITHIN the cull boundary (the per-cell
        /// pull-down logic in CalculateSurfaceWithGravity runs on cells inside
        /// the disk; this cull only removes cells outside the disk shape).
        /// Waterfall behaviour is unaffected — each disk has its own circular
        /// footprint and inter-disk Y-interpolation in river mode handles the
        /// vertical blending between disk centers.
        /// </summary>
        internal bool IsWithinAnyDiskShape(Vector3 worldPos)
        {
            for (int i = 0; i < _waterDisks.Count; i++)
            {
                if (_waterDisks[i].ContainsPositionWithBlend(worldPos, DISK_FOOTPRINT_CULL_MARGIN))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Public data structure for cliff cells (for VFX system).
        /// </summary>
        public struct CliffCellDataPublic
        {
            public Vector3 WorldPos;
            public Vector3 CliffDirection;
            public float StretchDistance;
            public float CliffAngle;
        }

        /// <summary>
        /// Gets cliff cell data for VFX spawning.
        /// Returns null if no cliff cells exist.
        /// </summary>
        public List<CliffCellDataPublic> GetCliffCellData()
        {
            if (_cliffCells == null || _cliffCells.Count == 0)
                return null;

            var result = new List<CliffCellDataPublic>(_cliffCells.Count);
            foreach (var cliff in _cliffCells)
            {
                result.Add(new CliffCellDataPublic
                {
                    WorldPos = cliff.WorldPos,
                    CliffDirection = cliff.CliffDirection,
                    StretchDistance = cliff.StretchDistance,
                    CliffAngle = cliff.CliffAngle
                });
            }
            return result;
        }

        /// <summary>
        /// Checks if a new disk would overlap with this combiner's disks.
        /// 
        /// IMPORTANT: Overlapping in XZ is not enough for combining.
        /// If there is TERRAIN BLOCKING between the disks (terrain at a height
        /// between the two disk heights), they should NOT combine.
        /// This allows separate water bodies on different cave levels.
        /// </summary>
        public bool WouldOverlapWith(WaterDisk newDisk)
        {
            if (newDisk == null) return false;

            foreach (var disk in _waterDisks)
            {
                if (disk.OverlapsWith(newDisk, BlendDistance))
                {
                    // Check for terrain blocking between the two disks
                    if (!IsTerrainBlockingBetweenDisks(disk, newDisk))
                    {
                        return true;
                    }
                    // If terrain is blocking, they shouldn't combine even if they overlap in XZ
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if two water disks belong on different "layers" (cave vs surface)
        /// and should NOT combine even when their XZ footprints overlap.
        ///
        /// 2026-05-25 round-10 REWRITE. The old version sampled terrain along the
        /// straight line between disk centers and blocked the combine if any
        /// sample's Y fell between the two disk Y values. That correctly
        /// separated caves from surface ponds — but it ALSO killed every
        /// waterfall (drag ruler up a cliff → cliff terrain between top and
        /// bottom disk Y → "blocked" → separate combiners → no waterfall mesh).
        ///
        /// New heuristic: a disk is "under a roof" if terrain DIRECTLY ABOVE
        /// its own XZ rises above its water surface by more than ROOF_CLEARANCE.
        /// If exactly one of the two disks has a roof, they're on different
        /// layers (one cave, one surface) and shouldn't combine. If both have
        /// no roof (both surface), they CAN combine even if the cliff face
        /// between them spans a huge Y range — that's a waterfall, not a block.
        /// </summary>
        public static bool IsTerrainBlockingBetweenDisks(WaterDisk diskA, WaterDisk diskB)
        {
            if (diskA == null || diskB == null) return false;

            // 2026-05-28: tightened the cave/surface heuristic so adjacent
            // disks placed in the same drag/click pass no longer get split
            // into separate combiners. The previous version raycast against
            // terrain+Default+static_solid+piece with a 0.5m clearance, which
            // flagged any built piece (wood floor, stair, ceiling beam)
            // sitting above the water as a "cave roof". Two disks placed
            // 0.3m apart in Y could disagree on roof status and end up in
            // different combiners — exactly the "multiple disks overlap and
            // sit at the same spot rather than combining" bug the user
            // reported with the lake/staircase screenshot.
            //
            // Fix:
            //   1. Use a realistic cave clearance (3m) — only block when there
            //      is a *real* ceiling overhead, not a flooring piece.
            //   2. Sample TERRAIN ONLY (ZoneSystem ground height). Built
            //      pieces should not vote on whether two water disks belong
            //      to the same body of water.
            //   3. Compare both samples against the HIGHER of the two disk
            //      Ys. This makes the check symmetric — small Y jitter
            //      between drag-fired disks cannot flip the result.
            const float ROOF_CLEARANCE = 3.0f;

            float terrainAtA = SampleTerrainHeightOnly(diskA.Position);
            float terrainAtB = SampleTerrainHeightOnly(diskB.Position);

            float referenceY = Mathf.Max(diskA.Position.y, diskB.Position.y) + ROOF_CLEARANCE;

            // 0.2.715 (Fire 10-05: our own water on the sky islands): ground height never sees a sky island, so a pool on
            // an island and the lake right under it both read "no roof" and merged into one combiner. A disk with an
            // island's underside above it is under a roof too.
            bool aHasRoof = terrainAtA > referenceY || UnderSkyIsland(diskA.Position, ROOF_CLEARANCE);
            bool bHasRoof = terrainAtB > referenceY || UnderSkyIsland(diskB.Position, ROOF_CLEARANCE);

            // Only block when BOTH disks have terrain above them but one
            // is genuinely in a different vertical pocket — i.e. cave vs
            // surface. With the symmetric reference Y, this collapses to
            // the same "different layer" intent as before, but is no
            // longer fooled by sub-meter Y jitter or by built pieces.
            return aHasRoof != bHasRoof;
        }

        /// <summary>
        /// Samples the natural terrain height at a position WITHOUT considering
        /// built pieces (floors, walls, ceilings). Used by the cave/surface
        /// classifier so player builds never split a single water body in two.
        /// </summary>
        internal static bool UnderSkyIsland(Vector3 worldPos, float clearance)
            => VerdantsAscent.Terrain.Voxel.FiresSkyIslands.TryBottomAt(worldPos.x, worldPos.z, out float bottom)
               && bottom > worldPos.y + clearance;

        private static float SampleTerrainHeightOnly(Vector3 worldPos)
        {
            if (ZoneSystem.instance != null
                && ZoneSystem.instance.GetGroundHeight(worldPos, out float groundHeight))
            {
                return groundHeight;
            }
            return -1000f;
        }

        /// <summary>
        /// Static terrain height sampling for blocking checks.
        /// Uses ZoneSystem and raycast fallback.
        /// </summary>
        private static float SampleTerrainHeightStatic(Vector3 worldPos)
        {
            float terrainHeight = -1000f; // Default far below

            // Try ZoneSystem for ground height (most reliable)
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(worldPos, out groundHeight))
                {
                    terrainHeight = groundHeight;
                }
            }

            // Raycast fallback for custom terrain, built structures, etc.
            Vector3 rayStart = new Vector3(worldPos.x, worldPos.y + 100f, worldPos.z);
            int layerMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");

            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 200f, layerMask))
            {
                // Use the higher of ZoneSystem or raycast
                terrainHeight = Mathf.Max(terrainHeight, hit.point.y);
            }

            return terrainHeight;
        }

        #endregion

        #region Static Helpers

        /// <summary>
        /// Gets or creates a combiner for a new water disk.
        /// 
        /// ARCHITECTURE (like terrain):
        /// - Creates a combiner GameObject immediately for same-frame disk lookups
        /// - Anchor creation + ZDO save is DEFERRED via debounce timer
        /// - During rapid click-and-hold, all disks go to the same combiner
        /// - After placement settles (0.3s), anchor is created and state is saved once
        /// 
        /// CRITICAL: The combiner is registered in s_allCombiners IMMEDIATELY (not in Start)
        /// so that subsequent disks in the same frame find it via overlap check.
        /// </summary>
        public static WaterMeshCombiner GetOrCreateCombinerFor(WaterDisk disk)
        {
            if (disk == null) return null;

            // Clean up any null entries from the list first
            s_allCombiners.RemoveAll(c => c == null);

            // Check for existing overlapping combiner.
            //
            // 2026-05-25 round-2: also require CustomLiquidType to match.
            // Without the type gate, painting lava over an existing water
            // pond would join the water combiner — and because each combiner
            // renders with ONE material, the whole pond's shader flipped to
            // lava instead of just the painted footprint. With the gate, a
            // different-typed disk falls through to the "create new combiner"
            // path below and gets its own mesh + material. Visually the new
            // disk renders on top of (or inside) the existing pond, but only
            // where the ruler outlined it. User complaint 2026-05-25:
            // "changing the water liquid type applys the new shader and
            // material to the ENTIRE surface rather than JUST where our
            // ruler is outlining".
            foreach (var combiner in s_allCombiners)
            {
                // Round-56: settling/gelled VOLUME combiners never absorb
                // new disks — their surface grid is authoritative data and
                // an added disk would trigger the IDW recompute that
                // clobbers the settled shape. A new disk near a gelled
                // volume gets its own combiner (stencil handles overlap).
                if (combiner != null && (combiner._isGelled || combiner._isSettling)) continue;

                if (combiner != null
                    && combiner.CustomLiquidType == disk.CustomLiquidType
                    && combiner.WouldOverlapWith(disk))
                {
                    // Add disk to EXISTING combiner - use its existing anchor
                    combiner.AddWaterDiskInternal(disk);

                    // Queue debounced save (resets timer on each new disk)
                    combiner.QueueDebouncedSave();

                    if (s_verboseLogging)
                    {
                        FiresLogger.LogInfo($"[WaterMeshCombiner] Added disk to existing combiner at {combiner.transform.position} (now has {combiner._waterDisks.Count} disks)");
                    }

                    return combiner;
                }
            }

            // Create new combiner ONLY if no overlapping combiner found
            var go = new GameObject("WaterMeshCombiner");
            go.transform.position = disk.Position;

            var newCombiner = go.AddComponent<WaterMeshCombiner>();
            newCombiner.LiquidType = disk.LiquidType;
            // 2026-05-25: also propagate CustomLiquidType. Without this, a disk
            // created with CustomLiquidType.Lava (whose vanilla mapping is Water)
            // lost the Lava info — combiner stayed default Water and SetupMaterial
            // picked the water material. Same cascade hit Boiling/Freezing/Ice.
            newCombiner.CustomLiquidType = disk.CustomLiquidType;
            newCombiner.AddWaterDiskInternal(disk);

            // CRITICAL: Register immediately so same-frame disks find this combiner!
            // Start() will skip the add if already present.
            s_allCombiners.Add(newCombiner);

            // DON'T create anchor yet - defer until debounce timer fires.
            // This prevents creating an anchor that immediately gets more disks added
            // during rapid click-and-hold placement.
            newCombiner.QueueDebouncedSave();

            // Setup hazard damage handler if needed
            newCombiner.SetupDamageHandler();

            // 2026-05-25 round-9: attach type-specific controllers for NEW
            // combiners. Without this, only SAVED (restored) combiners got
            // FrozenWaterController / LavaWaterOverlayController — fresh
            // placements rendered the raw water mesh with the wrong-type
            // material and no collider. Bug repro: place a snow disk, see
            // the underlying water mesh with wave spikes; place an ice
            // disk, no collider.
            //
            // SetupFrozenController is safe to call before the first
            // RegenerateMesh — the controller's Initialize() short-circuits
            // when surface heights are empty, and RegenerateMesh now calls
            // back into the controller to fill the mesh once data is ready
            // (see RegenerateMesh tail).
            if (newCombiner.CustomLiquidType.IsSolid())
            {
                newCombiner.SetupFrozenController();
            }
            else if (newCombiner.CustomLiquidType == CustomLiquidType.Lava)
            {
                newCombiner.SetupLavaWaterOverlay();
            }

            if (s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombiner] Created NEW combiner at {disk.Position} (anchor deferred, type={newCombiner.CustomLiquidType})");
            }

            return newCombiner;
        }

        /// <summary>
        /// Gets the combiner at a world position, if any.
        /// </summary>
        public static WaterMeshCombiner GetCombinerAt(Vector3 worldPos)
        {
            // Clean up null entries
            s_allCombiners.RemoveAll(c => c == null);

            foreach (var combiner in s_allCombiners)
            {
                if (combiner != null && combiner.ContainsPosition(worldPos))
                {
                    if (combiner.GetWaterSurfaceAt(worldPos) > float.MinValue)
                        return combiner;
                }
            }
            return null;
        }

        /// <summary>
        /// Gets all combiners within a radius.
        /// </summary>
        public static List<WaterMeshCombiner> GetCombinersInRadius(Vector3 center, float radius)
        {
            var result = new List<WaterMeshCombiner>();
            float radiusSq = radius * radius;

            // Clean up null entries
            s_allCombiners.RemoveAll(c => c == null);

            foreach (var combiner in s_allCombiners)
            {
                if (combiner == null) continue;

                // Use XZ distance only - water bodies at different elevations
                // (e.g. rivers at Y=30 vs mountains at Y=5000) should still be
                // found by horizontal radius searches.
                Vector3 pos = combiner.transform.position;
                float dx = pos.x - center.x;
                float dz = pos.z - center.z;
                float distSq = dx * dx + dz * dz;
                if (distSq <= radiusSq)
                    result.Add(combiner);
            }

            return result;
        }

        /// <summary>
        /// Clears all water combiners in the scene.
        /// Call this before loading water data or on logout.
        /// </summary>
        public static void ClearAllCombiners()
        {
            // Copy list to avoid modification during iteration
            var combiners = new List<WaterMeshCombiner>(s_allCombiners);

            foreach (var combiner in combiners)
            {
                if (combiner != null)
                {
                    combiner._waterDisks.Clear();
                    Destroy(combiner.gameObject);
                }
            }

            s_allCombiners.Clear();

            WaterAmbientVFX.ResetStaticCache();

            if (s_verboseLogging) FiresLogger.LogInfo("[WaterMeshCombiner] Cleared all water combiners");
        }

        /// <summary>
        /// Removes all null/destroyed entries from AllCombiners.
        /// 
        /// Unity's Destroy() is deferred to end-of-frame, but the destroyed object
        /// immediately returns true for "== null" via Unity's overloaded operator.
        /// Call this after DestroyPermanently() on all combiners to ensure the list
        /// is clean for subsequent operations (e.g., auto water generation) that run
        /// in the same frame.
        /// </summary>
        public static void PurgeDestroyedCombiners()
        {
            int before = s_allCombiners.Count;
            s_allCombiners.RemoveAll(c => c == null);
            int removed = before - s_allCombiners.Count;
            if (removed > 0)
            {
                if (s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombiner] PurgeDestroyedCombiners: Removed {removed} null entries, {s_allCombiners.Count} remaining");
            }
        }

        #endregion

        #region Grid Utilities

        /// <summary>
        /// Converts grid coordinates to world position.
        /// </summary>
        public Vector3 GridToWorld(int x, int z)
        {
            return _gridOrigin + new Vector3(
                x * _cellSize,
                0f,
                z * _cellSize
            );
        }

        /// <summary>
        /// Converts world position to grid coordinates.
        /// </summary>
        public bool WorldToGrid(Vector3 worldPos, out int x, out int z)
        {
            Vector3 local = worldPos - _gridOrigin;
            x = Mathf.FloorToInt(local.x / _cellSize);
            z = Mathf.FloorToInt(local.z / _cellSize);

            return x >= 0 && x < _gridWidth && z >= 0 && z < _gridHeight;
        }

        #endregion

        #region Mop Tool - Precision Water Removal

        /// <summary>
        /// Removes water from a circular area without triggering full mesh regeneration.
        /// This is the "Mop" tool - used for cleaning up edges of ponds/rivers.
        /// 
        /// Unlike disk deletion which triggers recombination, this directly modifies
        /// the surface height array and rebuilds only the mesh (no terrain re-sampling).
        /// 
        /// Also syncs the disk list to remove any disks that no longer have coverage.
        /// </summary>
        /// <param name="worldPos">Center position to remove water from</param>
        /// <param name="radius">Radius of the removal area</param>
        /// <returns>True if any water was removed</returns>
        public bool MopWaterAt(Vector3 worldPos, float radius)
        {
            if (_surfaceHeights == null || _surfaceHeights.Length == 0)
            {
                if (s_verboseLogging) FiresLogger.LogInfo("[WaterMeshCombiner] MopWaterAt: No surface heights to modify");
                return false;
            }

            bool anyRemoved = false;
            float radiusSq = radius * radius;
            int modifiedRowMin = _gridHeight;
            int modifiedRowMax = -1;

            // Find all grid cells within radius and set their surface height to -100 (no water)
            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;

                    // Skip cells that already have no water
                    if (_surfaceHeights[idx] < -50f) continue;

                    Vector3 cellWorld = GridToWorld(x, z);
                    float dx = cellWorld.x - worldPos.x;
                    float dz = cellWorld.z - worldPos.z;
                    float distSq = dx * dx + dz * dz;

                    if (distSq <= radiusSq)
                    {
                        // Remove water from this cell
                        _surfaceHeights[idx] = -100f;
                        _waterDepths[idx] = 0f;
                        anyRemoved = true;

                        // Track dirty row range
                        if (z < modifiedRowMin) modifiedRowMin = z;
                        if (z > modifiedRowMax) modifiedRowMax = z;
                    }
                }
            }

            if (anyRemoved)
            {
                // Track dirty rows for partial segment saves
                if (modifiedRowMin <= modifiedRowMax)
                {
                    MarkRowsDirty(modifiedRowMin, modifiedRowMax);
                }

                // Rebuild mesh WITHOUT regenerating from disks (fast!)
                BuildWaterMesh();
                SetupSwimTrigger();

                // CRITICAL: Revalidate tracked interactables to clear swim state from mopped areas
                if (_swimTriggerGO != null)
                {
                    var trigger = _swimTriggerGO.GetComponent<WaterCombinerTrigger>();
                    if (trigger != null)
                    {
                        trigger.RevalidateTrackedInteractables();
                    }
                }

                // Check if ALL water was removed
                bool hasAnyWater = false;
                for (int i = 0; i < _surfaceHeights.Length; i++)
                {
                    if (_surfaceHeights[i] > -50f)
                    {
                        hasAnyWater = true;
                        break;
                    }
                }

                if (!hasAnyWater)
                {
                    if (s_verboseLogging) FiresLogger.LogInfo("[WaterMeshCombiner] MopWaterAt: All water removed, destroying combiner");
                    // All water removed - destroy anchor and combiner
                    // Don't save here - we're about to destroy everything anyway
                    if (Anchor != null)
                    {
                        Anchor.DestroyPermanently();
                        Anchor = null; // Prevent SaveToAnchor from being called
                    }
                    Destroy(gameObject);
                    return true;
                }

                // CRITICAL: Sync disk list with grid state
                // Remove disks that no longer have any water coverage
                SyncDisksWithGridState();

                // Save the new state to anchor (only if we still have water)
                SaveToAnchor();

                if (s_verboseLogging)
                {
                    FiresLogger.LogInfo($"[WaterMeshCombiner] MopWaterAt: Removed water at {worldPos}, radius={radius}");
                }
            }

            return anyRemoved;
        }

        /// <summary>
        /// Synchronizes the disk list with the current grid state.
        /// Removes any disks that no longer have water coverage in the grid.
        /// This ensures the persisted disk list matches the actual water state.
        /// </summary>
        private void SyncDisksWithGridState()
        {
            if (_surfaceHeights == null || _waterDisks.Count == 0) return;

            var disksToRemove = new List<WaterDisk>();

            foreach (var disk in _waterDisks)
            {
                // Check if this disk has ANY water coverage remaining
                bool hasWaterCoverage = false;

                // Sample multiple points within the disk
                int samples = 0;
                int samplesWithWater = 0;

                // Check center
                if (WorldToGrid(disk.Position, out int cx, out int cz))
                {
                    int cidx = cz * _gridWidth + cx;
                    if (cidx >= 0 && cidx < _surfaceHeights.Length && _surfaceHeights[cidx] > -50f)
                    {
                        samplesWithWater++;
                    }
                    samples++;
                }

                // Check 8 points around the disk edge
                float checkRadius = disk.Radius * 0.7f; // Check at 70% of radius
                for (int angle = 0; angle < 8; angle++)
                {
                    float a = angle * Mathf.PI * 2f / 8f;
                    Vector3 checkPos = disk.Position + new Vector3(
                        Mathf.Cos(a) * checkRadius,
                        0,
                        Mathf.Sin(a) * checkRadius
                    );

                    if (WorldToGrid(checkPos, out int gx, out int gz))
                    {
                        int idx = gz * _gridWidth + gx;
                        if (idx >= 0 && idx < _surfaceHeights.Length && _surfaceHeights[idx] > -50f)
                        {
                            samplesWithWater++;
                        }
                        samples++;
                    }
                }

                // Disk has water coverage if at least 30% of samples have water
                hasWaterCoverage = samples > 0 && (float)samplesWithWater / samples >= 0.3f;

                if (!hasWaterCoverage)
                {
                    disksToRemove.Add(disk);
                    if (s_verboseLogging)
                    {
                        FiresLogger.LogInfo($"[WaterMeshCombiner] SyncDisksWithGridState: Removing disk at {disk.Position} - no water coverage ({samplesWithWater}/{samples} samples)");
                    }
                }
            }

            // Remove disks that no longer have coverage
            foreach (var disk in disksToRemove)
            {
                _waterDisks.Remove(disk);
            }

            if (disksToRemove.Count > 0)
            {
                if (s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombiner] SyncDisksWithGridState: Removed {disksToRemove.Count} disks with no water coverage. {_waterDisks.Count} disks remaining.");
            }
        }

        /// <summary>
        /// Gets the grid origin for external use.
        /// </summary>
        public Vector3 GetGridOrigin()
        {
            return _gridOrigin;
        }

        #endregion

        #region Ambient VFX

        /// <summary>
        /// Spawns or rebuilds ambient VFX (shoreline splash, waterfall spray,
        /// surface debris, ambient audio) after mesh regeneration.
        /// </summary>
        private void SpawnAmbientVFX()
        {
            // Skip VFX during mass generation or post-generation cleanup to avoid
            // per-combiner overhead while hundreds of combiners are being processed.
            // VFX will be added when the combiner restores from anchor on subsequent loads.
            if (AutoWaterGenerator.IsGenerating) return;
            if (AutoWaterPatches.IsCleaningUp) return;

            // 2026-05-25 round-7: snow has no shoreline splash / surface
            // debris / audio — it's a dry visual patch. Skip VFX so the
            // "Snow" tool doesn't drop water splash particles around its
            // perimeter. User: "no water effects".
            if (CustomLiquidType == CustomLiquidType.Snow)
            {
                if (_ambientVFX != null)
                {
                    _ambientVFX.CleanupVFX();
                    _ambientVFX = null;
                }
                return;
            }

            if (_ambientVFX == null)
                _ambientVFX = gameObject.AddComponent<WaterAmbientVFX>();

            _ambientVFX.Initialize(this);
        }

        #endregion

        #region Wave Calculation

        // Fixed wave parameters - scale independent
        private const float WAVE_FREQ_1 = 0.08f;
        private const float WAVE_FREQ_2 = 0.15f;
        private const float WAVE_FREQ_3 = 0.25f;
        private const float WAVE_SPEED_1 = 0.4f;
        private const float WAVE_SPEED_2 = 0.6f;
        private const float WAVE_SPEED_3 = 0.9f;
        private const float WAVE_AMP_1 = 0.15f;
        private const float WAVE_AMP_2 = 0.08f;
        private const float WAVE_AMP_3 = 0.04f;

        /// <summary>
        /// Calculates wave height at a position using fixed world-space frequencies.
        /// Waves are scale-independent - a small pond has the same wave density as a large lake.
        /// </summary>
        private float CalculateWaveHeight(Vector3 worldPos, float depth)
        {
            if (WaveIntensity <= 0.001f) return 0f;

            float time = Time.time;

            // Get wind data if available
            float windStrength = 0.5f;
            if (EnvMan.instance != null)
            {
                EnvMan.instance.GetWindData(out Vector4 wind1, out _, out _);
                windStrength = wind1.w;
            }

            // Large slow waves
            float wave1 = Mathf.Sin(worldPos.x * WAVE_FREQ_1 + worldPos.z * WAVE_FREQ_1 * 0.7f + time * WAVE_SPEED_1) * WAVE_AMP_1;

            // Medium cross-waves
            float wave2 = Mathf.Sin(worldPos.x * WAVE_FREQ_2 * 0.8f - worldPos.z * WAVE_FREQ_2 + time * WAVE_SPEED_2) * WAVE_AMP_2;

            // Small ripples
            float wave3 = Mathf.Sin(worldPos.x * WAVE_FREQ_3 + worldPos.z * WAVE_FREQ_3 * 1.2f + time * WAVE_SPEED_3) * WAVE_AMP_3;

            float totalWave = (wave1 + wave2 + wave3) * Mathf.Clamp01(windStrength * 0.5f + 0.5f);

            // Reduce waves in shallow water
            float depthFactor = Mathf.Clamp01(depth / 2f);

            return totalWave * depthFactor * WaveIntensity;
        }

        /// <summary>
        /// Changes the custom liquid type and updates material/behavior accordingly.
        /// </summary>
        public void SetCustomLiquidType(CustomLiquidType newType)
        {
            // 0.2.873 (from Galaxies 0.1.39): a type someone chose (tool, command) replaces seasonal ice and is saved.
            if (SeasonalIce)
            {
                SeasonalIce = false;
                if (newType == CustomLiquidType) { SaveToAnchor(); return; }
            }
            ApplyCustomLiquidType(newType, true);
        }

        private void ApplyCustomLiquidType(CustomLiquidType newType, bool save)
        {
            if (CustomLiquidType == newType) return;

            var oldType = CustomLiquidType;
            CustomLiquidType = newType;
            LiquidType = newType.ToVanillaLiquidType();

            // Update material
            _waterMaterial = WaterMaterialManager.GetMaterial(newType);
            if (_meshRenderer != null && _waterMaterial != null)
            {
                _meshRenderer.material = _waterMaterial;
            }

            // Update damage handler
            SetupDamageHandler();

            // Handle ice/snow controller lifecycle. Snow shares the same
            // FrozenWaterController class as ice (with IsSnowMode flag set
            // automatically from CustomLiquidType inside Initialize). Both
            // types hide the underlying water mesh and replace it with a
            // walkable solid surface.
            bool oldIsSolid = oldType.IsSolid();
            bool newIsSolid = newType.IsSolid();
            if (oldIsSolid && !newIsSolid)
            {
                // Melt/thaw: remove solid-surface controller, restore water mesh
                var frozen = GetComponent<FrozenWaterController>();
                if (frozen != null)
                {
                    frozen.Thaw();
                    Destroy(frozen);
                }
                if (_meshRenderer != null)
                    _meshRenderer.enabled = true;
            }
            else if (newIsSolid && !oldIsSolid)
            {
                // Freeze / snow-cover: add controller, hide water mesh
                SetupFrozenController();
            }
            else if (oldIsSolid && newIsSolid && oldType != newType)
            {
                // Switching between Ice <-> Snow: tear down + rebuild so
                // the controller's IsSnowMode flag and material refresh.
                var frozen = GetComponent<FrozenWaterController>();
                if (frozen != null)
                {
                    frozen.Thaw();
                    Destroy(frozen);
                }
                SetupFrozenController();
            }

            // Handle lava water overlay lifecycle
            if (oldType == CustomLiquidType.Lava && newType != CustomLiquidType.Lava)
            {
                // Leaving lava: destroy water overlay
                if (_lavaOverlay != null)
                {
                    _lavaOverlay.Cleanup();
                    Destroy(_lavaOverlay);
                    _lavaOverlay = null;
                }
            }
            else if (newType == CustomLiquidType.Lava && oldType != CustomLiquidType.Lava)
            {
                // Entering lava: create water overlay
                SetupLavaWaterOverlay();
            }

            // Update swim trigger liquid type
            if (_swimTriggerGO != null)
            {
                var trigger = _swimTriggerGO.GetComponent<WaterCombinerTrigger>();
                if (trigger != null)
                {
                    trigger.Initialize(this);
                }
            }

            // Save new state (0.2.873: seasonal ice is never saved)
            if (save) SaveToAnchor();
        }

        /// <summary>
        /// Sets up the lava water overlay — a thin water-shader mesh ~0.002m
        /// above the lava surface. The water shader's refraction over the
        /// opaque lava produces the molten-glass look the user wanted for the
        /// swimmable-lava tool. The 0.002m offset (down from a previous
        /// 0.25f) keeps the overlay co-planar with the lava so there's no
        /// visible edge halo.
        /// </summary>
        internal void SetupLavaWaterOverlay()
        {
            if (_lavaOverlay == null)
                _lavaOverlay = gameObject.AddComponent<LavaWaterOverlayController>();

            _lavaOverlay.Initialize();

            if (_waterMesh != null && _waterMesh.vertexCount > 0)
                _lavaOverlay.RebuildOverlay(_waterMesh);
        }

        /// <summary>
        /// Refreshes the lava water overlay after a mesh rebuild. No-op if
        /// the combiner is not in lava mode (and proactively tears down any
        /// stale overlay if the type was changed away from Lava).
        /// </summary>
        private void UpdateLavaWaterOverlay()
        {
            if (CustomLiquidType != CustomLiquidType.Lava)
            {
                if (_lavaOverlay != null)
                {
                    _lavaOverlay.Cleanup();
                    Destroy(_lavaOverlay);
                    _lavaOverlay = null;
                }
                return;
            }

            if (_lavaOverlay == null)
                SetupLavaWaterOverlay();
            else if (_waterMesh != null)
                _lavaOverlay.RebuildOverlay(_waterMesh);
        }

        /// <summary>
        /// Sets up the frozen water controller for ice liquid type.
        /// Hides the water mesh and creates a solid ice surface.
        /// </summary>
        internal void SetupFrozenController()
        {
            var frozen = GetComponent<FrozenWaterController>();
            if (frozen == null)
            {
                frozen = gameObject.AddComponent<FrozenWaterController>();
            }
            frozen.Initialize(this);

            // Hide the water mesh - ice/snow replaces it visually
            if (_meshRenderer != null)
                _meshRenderer.enabled = false;
        }

        /// <summary>
        /// Called at the tail of RegenerateMesh so the attached FrozenWater /
        /// LavaWaterOverlay controllers can refresh their secondary meshes
        /// from the freshly-computed surface heights. Without this, a NEW
        /// solid-type combiner (Ice/Snow) ends up with a controller that
        /// initialised against empty data — the visual ice/snow mesh stays
        /// empty and no collider gets built. Similarly Lava's overlay would
        /// not pick up newly-added cells.
        /// </summary>
        private void RefreshSecondaryMeshes()
        {
            var frozen = GetComponent<FrozenWaterController>();
            if (frozen != null && frozen.IsFrozen)
            {
                frozen.OnCombinerMeshUpdated();
            }
            // Lava overlay refresh is already handled inside SetupMaterial
            // via UpdateLavaWaterOverlay — no extra call needed here.
        }

        /// <summary>
        /// Sets up the damage handler for hazardous liquid types.
        /// </summary>
        private void SetupDamageHandler()
        {
            if (CustomLiquidType.IsHazardous())
            {
                if (_damageHandler == null)
                {
                    _damageHandler = gameObject.AddComponent<LiquidDamageHandler>();
                }
                _damageHandler.Initialize(this, CustomLiquidType);
            }
            else
            {
                if (_damageHandler != null)
                {
                    Destroy(_damageHandler);
                    _damageHandler = null;
                }
            }
        }

        /// <summary>
        /// Gets the damage handler (for swim trigger to call). May be null.
        /// </summary>
        public LiquidDamageHandler GetDamageHandler()
        {
            return _damageHandler;
        }

        #endregion

        #region Anchor Persistence Support

        /// <summary>
        /// Gets the surface heights array for persistence.
        /// This is the COMBINED mesh state, not individual disk data.
        /// </summary>
        public float[] GetSurfaceHeights()
        {
            return _surfaceHeights;
        }

        /// <summary>
        /// Gets the terrain heights array for persistence.
        /// </summary>
        public float[] GetTerrainHeights()
        {
            return _terrainHeights;
        }

        /// <summary>
        /// Sets the surface heights directly (for restoration from anchor).
        /// </summary>
        public void SetSurfaceHeights(float[] heights)
        {
            _surfaceHeights = heights;
        }

        /// <summary>
        /// Sets the terrain heights directly (for restoration from anchor).
        /// </summary>
        public void SetTerrainHeights(float[] heights)
        {
            _terrainHeights = heights;
        }

        /// <summary>
        /// Sets the grid configuration (for restoration from anchor).
        /// NOTE: _gridOrigin is calculated in RestoreFromAnchorData or SetBoundsData after bounds are set.
        /// </summary>
        public void SetGridConfiguration(int width, int height, float cellSize, float settledLevel, bool isPondMode)
        {
            _gridWidth = width;
            _gridHeight = height;
            _cellSize = cellSize;
            _settledWaterLevel = settledLevel;
            _isPondMode = isPondMode;

            // NOTE: Do NOT calculate _gridOrigin here!
            // The _gridOrigin must be calculated from _combinedBounds which is set via SetBoundsData
            // AFTER this method is called during restoration.
            // _gridOrigin is properly calculated in RestoreFromAnchorData after bounds are available.
        }

        /// <summary>
        /// Sets the bounds data (for restoration from anchor).
        /// </summary>
        public void SetBoundsData(Vector3 min, Vector3 max)
        {
            _combinedBounds = new Bounds();
            _combinedBounds.SetMinMax(min, max);
        }

        /// <summary>
        /// Restores the mesh from anchor data.
        /// This rebuilds the mesh directly from stored grid data - NO disk recombination!
        /// Call this after setting grid configuration, surface heights, terrain heights, and bounds.
        /// </summary>
        public void RestoreFromAnchorData()
        {
            if (_surfaceHeights == null || _surfaceHeights.Length == 0)
            {
                FiresLogger.LogWarning("[WaterMeshCombiner] Cannot restore - no surface height data");
                return;
            }

            // CRITICAL: Validate grid dimensions match array sizes
            int expectedGridSize = _gridWidth * _gridHeight;
            if (expectedGridSize <= 0)
            {
                FiresLogger.LogWarning($"[WaterMeshCombiner] Invalid grid dimensions: {_gridWidth}x{_gridHeight}");
                // Try to infer dimensions from surface heights array
                int surfaceCount = _surfaceHeights.Length;
                int inferredSize = (int)Mathf.Sqrt(surfaceCount);
                if (inferredSize * inferredSize == surfaceCount)
                {
                    _gridWidth = inferredSize;
                    _gridHeight = inferredSize;
                    expectedGridSize = surfaceCount;
                    if (s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombiner] Inferred grid size: {_gridWidth}x{_gridHeight}");
                }
                else
                {
                    FiresLogger.LogError($"[WaterMeshCombiner] Cannot infer grid dimensions from {surfaceCount} surface heights");
                    return;
                }
            }

            // Ensure surface heights array matches expected size
            if (_surfaceHeights.Length != expectedGridSize)
            {
                FiresLogger.LogWarning($"[WaterMeshCombiner] Surface heights array mismatch: got {_surfaceHeights.Length}, expected {expectedGridSize} ({_gridWidth}x{_gridHeight}). Regenerating from disks.");
                // Fall back to disk regeneration
                if (_waterDisks.Count > 0)
                {
                    ForceRegenerate();
                }
                return;
            }

            // Calculate water depths from restored data
            int gridSize = expectedGridSize;
            if (_waterDepths == null || _waterDepths.Length != gridSize)
            {
                _waterDepths = new float[gridSize];
            }

            // Ensure terrain heights array is valid
            bool hasValidTerrainHeights = _terrainHeights != null && _terrainHeights.Length == gridSize;

            for (int i = 0; i < gridSize; i++)
            {
                float surface = _surfaceHeights[i];
                float terrain = hasValidTerrainHeights ? _terrainHeights[i] : surface - 5f;

                if (surface < -50f)
                {
                    _waterDepths[i] = 0f;
                }
                else
                {
                    _waterDepths[i] = Mathf.Max(0f, surface - terrain);
                }
            }

            // CRITICAL FIX: Calculate grid origin from bounds FIRST
            // This is essential for rivers/ponds to restore at correct positions
            // The bounds are set via SetBoundsData before this method is called
            if (_combinedBounds.size.sqrMagnitude > 0.01f)
            {
                // Use bounds min as origin - this is the correct way
                _gridOrigin = new Vector3(_combinedBounds.min.x, 0, _combinedBounds.min.z);
                if (s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombiner] RestoreFromAnchorData: Set gridOrigin from bounds: {_gridOrigin}");
            }
            else
            {
                // Estimate from position and grid size as fallback
                float gridWidthWorld = (_gridWidth - 1) * _cellSize;
                float gridHeightWorld = (_gridHeight - 1) * _cellSize;
                _gridOrigin = transform.position - new Vector3(gridWidthWorld * 0.5f, 0, gridHeightWorld * 0.5f);
                FiresLogger.LogWarning($"[WaterMeshCombiner] RestoreFromAnchorData: No bounds available, estimated gridOrigin: {_gridOrigin}");
            }

            // 0.2.715 Vmop: the saved grid may predate a mop; the mask wins.
            DryMoppedCells();

            // Build mesh from restored grid data
            BuildWaterMesh();

            // Setup material
            SetupMaterial();

            // Setup swim trigger
            SetupSwimTrigger();

            // Detect cliffs (kept for future use)
            DetectCliffEdges();

            // Spawn ambient VFX
            SpawnAmbientVFX();

            // If liquid type is Ice, set up frozen controller after mesh restoration
            // Solid surfaces (Ice / Snow) both use FrozenWaterController; it
            // branches on combiner.CustomLiquidType to pick its material +
            // skip the under-ice walls for snow.
            if (CustomLiquidType.IsSolid())
            {
                SetupFrozenController();
            }

            // If liquid type is Lava, set up water overlay after mesh restoration
            if (CustomLiquidType == CustomLiquidType.Lava)
            {
                SetupLavaWaterOverlay();
            }

            if (s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombiner] Restored mesh from anchor data: grid {_gridWidth}x{_gridHeight}");
            }
        }

        /// <summary>
        /// Ensures this combiner has an anchor for persistence.
        /// Creates one if it doesn't exist.
        /// 
        /// CRITICAL: Only call this when creating a NEW combiner!
        /// Adding disks to existing combiners should use the combiner's existing anchor.
        /// 
        /// Each combiner gets its own anchor. Deduplication of stale/orphaned anchors
        /// is handled in WaterMeshCombinerAnchor.Start() via ZDO ID and position tracking.
        /// </summary>
        public void EnsureAnchorExists()
        {
            if (Anchor != null)
            {
                // Already have an anchor - verify it's linked properly
                if (Anchor.Combiner != this)
                {
                    Anchor.LinkToCombiner(this);
                }
                return;
            }

            // Create anchor
            Anchor = WaterMeshCombinerAnchor.CreateAnchor(transform.position);
            if (Anchor != null)
            {
                Anchor.LinkToCombiner(this);
                if (s_verboseLogging)
                {
                    FiresLogger.LogInfo($"[WaterMeshCombiner] EnsureAnchorExists: Anchor created for combiner at {transform.position}");
                }
            }
            else
            {
                FiresLogger.LogError($"[WaterMeshCombiner] EnsureAnchorExists: FAILED to create anchor! Water will NOT persist!");
            }
        }

        /// <summary>
        /// Queues a debounced save. Each call resets the timer.
        /// After SAVE_DEBOUNCE_TIME with no new calls, performs the actual save.
        /// Creates the anchor on first save if needed.
        /// </summary>
        public void QueueDebouncedSave()
        {
            _pendingSave = true;
            _saveDebounceTimer = SAVE_DEBOUNCE_TIME;
        }

        /// <summary>
        /// Forces an immediate save, bypassing the debounce timer.
        /// Called on destroy, tool deselect, or other critical moments.
        /// </summary>
        public void ForceImmediateSave()
        {
            if (_pendingSave)
            {
                PerformDebouncedSave();
            }
        }

        /// <summary>
        /// Performs the actual save after debounce settles.
        /// Creates anchor if this is the first save for a new combiner.
        /// </summary>
        private void PerformDebouncedSave()
        {
            _pendingSave = false;

            if (_waterDisks.Count == 0) return;

            // Only the server/host persists water. Remote clients just have the
            // visual combiner - they don't own the anchor ZDO and the CustomRiver
            // prefab lookup would fail because registration timing differs.
            if (ZNet.instance != null && !ZNet.instance.IsServer()) return;

            // Create anchor on first save (deferred from GetOrCreateCombinerFor)
            if (Anchor == null && !_restoredFromPersistence)
            {
                EnsureAnchorExists();
            }

            SaveToAnchor();
        }

        /// <summary>
        /// Saves this combiner's state to its anchor.
        /// Call this after mesh modifications to persist the combined state.
        /// 
        /// For rapid placement, prefer QueueDebouncedSave() instead.
        /// </summary>
        public void SaveToAnchor()
        {
            // Only the server/host persists water to ZDO anchors.
            if (ZNet.instance != null && !ZNet.instance.IsServer()) return;

            if (Anchor == null)
            {
                if (!_restoredFromPersistence)
                {
                    EnsureAnchorExists();
                }

                if (Anchor == null)
                {
                    if (!_restoredFromPersistence)
                    {
                        FiresLogger.LogWarning($"[WaterMeshCombiner] SaveToAnchor: No anchor for combiner at {transform.position}. Water will NOT persist!");
                    }
                    return;
                }
            }

            if (s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombiner] SaveToAnchor: Saving {_waterDisks.Count} disks to anchor at {transform.position}");
            }
            Anchor.SaveToZDO();

            // Clear dirty row tracking after successful save
            _dirtyRowMin = -1;
            _dirtyRowMax = -1;
            _allRowsDirty = false;

            // Also update file-based cache for reliable client-side restoration
            // (ZDO byte arrays may not sync to remote clients reliably)
            WaterCombinerCache.UpdateCombinerCache(this);
        }

        #endregion

        #region Dirty Row Tracking

        /// <summary>
        /// Marks a range of grid rows as dirty (modified since last save).
        /// Used for partial segment saves - only segments covering dirty rows are written.
        /// </summary>
        public void MarkRowsDirty(int rowMin, int rowMax)
        {
            if (_dirtyRowMin < 0 || rowMin < _dirtyRowMin)
                _dirtyRowMin = rowMin;
            if (rowMax > _dirtyRowMax)
                _dirtyRowMax = rowMax;
        }

        /// <summary>
        /// Marks ALL rows as dirty (full save needed).
        /// Called after ForceRegenerate or initial mesh generation.
        /// </summary>
        public void MarkAllRowsDirty()
        {
            _allRowsDirty = true;
            _dirtyRowMin = 0;
            _dirtyRowMax = _gridHeight > 0 ? _gridHeight - 1 : 0;
        }

        /// <summary>
        /// Gets the dirty row range. Returns false if no rows are dirty.
        /// </summary>
        public bool GetDirtyRowRange(out int rowMin, out int rowMax)
        {
            if (_allRowsDirty)
            {
                rowMin = 0;
                rowMax = _gridHeight > 0 ? _gridHeight - 1 : 0;
                return true;
            }

            if (_dirtyRowMin >= 0)
            {
                rowMin = _dirtyRowMin;
                rowMax = _dirtyRowMax;
                return true;
            }

            rowMin = 0;
            rowMax = 0;
            return false;
        }

        /// <summary>
        /// Whether all rows need saving (full regeneration occurred).
        /// </summary>
        public bool AreAllRowsDirty => _allRowsDirty;

        #endregion
    }
}
