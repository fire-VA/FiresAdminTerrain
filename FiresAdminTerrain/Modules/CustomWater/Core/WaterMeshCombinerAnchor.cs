using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;


namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// ZDO-backed anchor for WaterMeshCombiner persistence.
    /// 
    /// ARCHITECTURE (like TerrainCombinerAnchor):
    /// - ONE invisible anchor ZDO per combiner (NOT per disk!)
    /// - Anchor stores the COMBINED mesh state: surface heights, terrain heights, bounds, etc.
    /// - On world load, anchor recreates the combiner mesh DIRECTLY (no disk recombination!)
    /// 
    /// KEY DIFFERENCE FROM OLD SYSTEM:
    /// - OLD: Save disks individually, reload and recombine
    /// - NEW: Save COMBINED state, restore mesh directly
    /// 
    /// This matches how terrain works - disks are "absorbed" into the combiner
    /// and the combined result is what gets persisted.
    /// 
    /// DATA FLOW:
    /// 1. Disk placed -> Absorbed into combiner -> Disk destroyed
    /// 2. Combiner generates mesh from accumulated disk data
    /// 3. World saves -> Anchor saves COMBINED mesh state (not disks!)
    /// 4. World loads -> Anchor restores mesh DIRECTLY (no recombination!)
    /// </summary>
    public class WaterMeshCombinerAnchor : MonoBehaviour
    {
        #region ZDO Data Keys

        private static readonly int s_isAnchorHash = "va_is_water_combiner_anchor".GetStableHashCode();
        private static readonly int s_versionHash = "va_water_combiner_version".GetStableHashCode();
        private static readonly int s_realPositionHash = "va_water_real_position".GetStableHashCode();
        private static readonly int s_liquidTypeHash = "va_water_liquid_type".GetStableHashCode();
        private static readonly int s_customLiquidTypeHash = "va_water_custom_liquid_type".GetStableHashCode();
        private static readonly int s_waveIntensityHash = "va_water_wave_intensity".GetStableHashCode();
        private static readonly int s_gridDataHash = "va_water_grid_data".GetStableHashCode();
        private static readonly int s_surfaceHeightsHash = "va_water_surface_heights".GetStableHashCode();
        private static readonly int s_terrainHeightsHash = "va_water_terrain_heights".GetStableHashCode();
        private static readonly int s_boundsDataHash = "va_water_bounds".GetStableHashCode();
        private static readonly int s_settledLevelHash = "va_water_settled_level".GetStableHashCode();
        private static readonly int s_isPondModeHash = "va_water_pond_mode".GetStableHashCode();
        private static readonly int s_waterDisksHash = "va_water_disks".GetStableHashCode();
        // Round-56: gelled-volume flag — the combiner's surface grid is
        // authoritative (settled water volume), regen must never recompute
        // it from the disk list. Independent key: absent on old anchors
        // reads as false, no version bump needed.
        private static readonly int s_isGelledHash = "va_water_is_gelled".GetStableHashCode();

        // Segment anchor keys (v3)
        private static readonly int s_segmentCountHash = "va_water_segment_count".GetStableHashCode();
        private static readonly int s_segmentZdoidsHash = "va_water_segment_zdoids".GetStableHashCode();

        private const int CURRENT_VERSION = 3; // v3: segmented grid data across multiple ZDOs

        // Monolithic storage threshold: grids with uncompressed height data <= this size
        // use the legacy single-ZDO approach. Larger grids use segment anchors.
        // 8KB / 8 bytes per cell (surface + terrain, 4 bytes each) = 1024 cells = ~32x32
        private const int MONOLITHIC_CELL_THRESHOLD = 1024;

        // Maximum segment anchors per combiner to prevent ZDO spam
        private const int MAX_SEGMENTS = 20;

        // Place segments every ~128m along the combiner's extent (2 Valheim zones)
        private const float SEGMENT_SPACING = 128f;

        // Prefab name to use for ZDO persistence
        // Uses CustomRiver - it's on the "piece" layer (not terrain), so it won't
        // interfere with terrain raycasts or pull river water down to anchor Y.
        // The dirtfloor prefab was on the "terrain" layer which caused river surface
        // heights to get pulled down to the anchor's hidden Y=-10 position.
        private const string BASE_PREFAB_NAME = "CustomRiver";

        // Anchor spawn position - underground to stay hidden
        private const float ANCHOR_HIDDEN_Y = -10f;

        #endregion

        #region Fields

        private ZNetView _nview;
        private WaterMeshCombiner _combiner;
        private bool _dataLoaded = false;
        private bool _isStripped = false;
        private bool _isNewlyCreated = false;

        // The REAL water position (anchor spawns at Y=-10 to stay hidden)
        private Vector3 _realWaterPosition;

        // Segment anchor management
        private List<WaterSegmentAnchor> _segments = new List<WaterSegmentAnchor>();
        private List<ZDOID> _segmentZdoids = new List<ZDOID>();
        private int _segmentCount;

        // Pending anchors set to prevent Awake self-destruction
        private static System.Collections.Generic.HashSet<GameObject> s_pendingAnchors = new System.Collections.Generic.HashSet<GameObject>();

        // Static flag: when true, the next WaterMeshCombinerAnchor.Awake() call should
        // NOT self-destruct even if isAnchor is false in the ZDO (because we haven't
        // set it yet - the ZDO flags get set after Instantiate returns).
        private static bool s_nextInstantiateIsPending = false;

        /// <summary>
        /// Sets the pending instantiation flag. Called by WaterSegmentAnchor to prevent
        /// Awake self-destruction when creating segment anchors from the same prefab.
        /// </summary>
        public static void SetNextInstantiateIsPending(bool value)
        {
            s_nextInstantiateIsPending = value;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the real water position (not the anchor's Y=0 position).
        /// </summary>
        public Vector3 RealWaterPosition
        {
            get
            {
                if (_realWaterPosition.sqrMagnitude > 0.01f)
                    return _realWaterPosition;

                if (_nview != null && _nview.GetZDO() != null)
                {
                    _realWaterPosition = _nview.GetZDO().GetVec3(s_realPositionHash, transform.position);
                    if (_realWaterPosition.sqrMagnitude > 0.01f)
                        return _realWaterPosition;
                }

                return transform.position;
            }
        }

        /// <summary>
        /// The combiner this anchor manages.
        /// </summary>
        public WaterMeshCombiner Combiner => _combiner;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();

            if (_nview == null)
            {
                FiresLogger.LogError("[WaterMeshCombinerAnchor] No ZNetView found!");
                Destroy(this);
                return;
            }

            if (_nview.GetZDO() == null)
            {
                return;
            }

            bool isPendingCreation = s_pendingAnchors.Contains(gameObject) || s_nextInstantiateIsPending;
            bool isAnchor = _nview.GetZDO().GetBool(s_isAnchorHash, false);
            bool isSegment = _nview.GetZDO().GetBool(WaterSegmentAnchor.s_isSegmentHash, false);

            // Segment anchors are handled by WaterSegmentAnchor, not this class
            if (isSegment && !isPendingCreation)
            {
                Destroy(this);
                return;
            }

            if (!isAnchor && !isPendingCreation)
            {
                Destroy(this);
                return;
            }

            s_pendingAnchors.Remove(gameObject);
            StripToInvisible();
        }

        // Static tracking of anchors by ZDO ID to prevent duplicate restoration
        private static System.Collections.Generic.Dictionary<ZDOID, WaterMeshCombinerAnchor> s_anchorsByZdoId = new System.Collections.Generic.Dictionary<ZDOID, WaterMeshCombinerAnchor>();

        // Track anchors by approximate position to detect ZDO duplicates at similar positions
        private static System.Collections.Generic.Dictionary<Vector3Int, WaterMeshCombinerAnchor> s_anchorsByPosition = new System.Collections.Generic.Dictionary<Vector3Int, WaterMeshCombinerAnchor>();

        // 0.2.715 (Fire 10-05: our own water on the sky islands): the key was XZ only, so a sky-island pool's anchor and the
        // ground water's anchor in the same column read as duplicates and one was DestroyPermanently'd. Height now counts,
        // in 8 m bands: two copies of one water body share a band, an island and the ground below never do.
        private const float PositionKeyHeightBand = 8f;

        /// <summary>
        /// Gets a position key for deduplication (1 m grid across, 8 m bands up).
        /// </summary>
        private static Vector3Int GetPositionKey(Vector3 pos)
        {
            return new Vector3Int(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.y / PositionKeyHeightBand), Mathf.RoundToInt(pos.z));
        }

        private void Start()
        {
            if (_nview == null || _nview.GetZDO() == null)
            {
                return;
            }

            var zdoId = _nview.GetZDO().m_uid;
            bool isAnchor = _nview.GetZDO().GetBool(s_isAnchorHash, false);

            // Reduced logging - only log non-obvious cases
            if (!isAnchor)
            {
                Destroy(this);
                return;
            }

            // CRITICAL DEDUPLICATION #1: Check if we already have an anchor for this ZDO ID
            // This handles the case where the same ZDO spawns multiple times during zone reload
            if (s_anchorsByZdoId.TryGetValue(zdoId, out var existingAnchorById) && existingAnchorById != null && existingAnchorById != this)
            {
                // Another anchor instance already handles this ZDO - destroy this duplicate
                if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Start: Duplicate anchor for ZDO {zdoId} - destroying self");
                _dataLoaded = true;
                Destroy(gameObject); // Not DestroyPermanently - don't touch the ZDO
                return;
            }

            // CRITICAL DEDUPLICATION #2: Check if we already have an anchor at this POSITION
            // This handles the case where multiple different ZDOs exist at similar positions (legacy duplicates)
            Vector3 myPos = RealWaterPosition;
            Vector3Int posKey = GetPositionKey(myPos);

            if (s_anchorsByPosition.TryGetValue(posKey, out var existingAnchorByPos) && existingAnchorByPos != null && existingAnchorByPos != this)
            {
                // Another anchor already exists at this position - this is a DUPLICATE ZDO
                // Keep the one with more data, destroy this one if it has less
                int myVersion = _nview.GetZDO().GetInt(s_versionHash, 0);
                var otherNview = existingAnchorByPos.GetComponent<ZNetView>();
                int otherVersion = otherNview?.GetZDO()?.GetInt(s_versionHash, 0) ?? 0;

                // Compare data sizes to determine which to keep
                byte[] myGridData = _nview.GetZDO().GetByteArray(s_gridDataHash);
                byte[] otherGridData = otherNview?.GetZDO()?.GetByteArray(s_gridDataHash);

                int myDataSize = (myGridData?.Length ?? 0);
                int otherDataSize = (otherGridData?.Length ?? 0);

                if (otherDataSize >= myDataSize)
                {
                    // The existing anchor has more or equal data - destroy this one
                    if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Start: Duplicate anchor at position {myPos:F1} (ZDO {zdoId}). " +
                             $"Existing anchor has {otherDataSize} bytes, this has {myDataSize} bytes. Destroying this duplicate.");
                    _dataLoaded = true;
                    DestroyPermanently(); // Clear the ZDO data to prevent it from coming back
                    return;
                }
                else
                {
                    // This anchor has more data - destroy the existing one instead
                    if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Start: Duplicate anchor at position {myPos:F1}. " +
                             $"This anchor has {myDataSize} bytes, existing has {otherDataSize} bytes. Destroying existing duplicate.");
                    existingAnchorByPos.DestroyPermanently();
                    // Continue with this anchor's initialization
                }
            }

            // Register this anchor for this ZDO ID and position
            s_anchorsByZdoId[zdoId] = this;
            s_anchorsByPosition[posKey] = this;

            if (!_isStripped)
            {
                StripToInvisible();
            }

            // If newly created with combiner already linked, don't restore
            if (_isNewlyCreated || _combiner != null)
            {
                _dataLoaded = true;
                return;
            }

            if (_dataLoaded)
            {
                return;
            }

            // Check for existing combiner at this position
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null) continue;

                float dist = Vector3.Distance(combiner.transform.position, myPos);
                if (dist < 2f)
                {
                    // Combiner already exists at this position
                    if (combiner.Anchor != null && combiner.Anchor != this)
                    {
                        // Combiner already has a DIFFERENT anchor - this is a stale anchor
                        // DESTROY THIS STALE ANCHOR - only ONE anchor per combiner!
                        if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Start: Combiner at {combiner.transform.position} already has anchor. This is a STALE anchor - destroying self.");
                        _dataLoaded = true;
                        DestroyPermanently();
                        return;
                    }

                    // Combiner exists and needs this anchor - link to it
                    _combiner = combiner;
                    _combiner.Anchor = this;
                    _dataLoaded = true;
                    return;
                }
            }

            // CACHE-FIRST RESTORATION (mirrors terrain system)
            // Try file-based cache BEFORE ZDO restoration. The cache is local and
            // always available, while ZDO byte arrays may not have synced to clients yet.
            var cachedData = WaterCombinerCache.GetCachedDataForPosition(myPos);
            if (cachedData != null && cachedData.HasValidData)
            {
                // Check if a combiner for this cache group already exists
                var existingCombiner = WaterCombinerCache.GetInstantiatedCombinerForGroup(cachedData.GroupId);
                if (existingCombiner != null)
                {
                    _combiner = existingCombiner;
                    if (_combiner.Anchor == null)
                        _combiner.Anchor = this;
                    _dataLoaded = true;
                    if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Start: Linked to existing cached combiner at {myPos}");
                    return;
                }

                // Restore from file cache (fast, no ZDO dependency)
                _dataLoaded = true;
                _combiner = WaterCombinerCache.RestoreFromCache(cachedData, this);
                if (_combiner != null)
                {
                    if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Start: Restored from file cache at {myPos} ({cachedData.WaterDiskData?.Length / WaterCombinerCache.WaterCombinerGroupData.DISK_STRIDE ?? 0} disks)");
                    return;
                }
                // Cache restore failed - fall through to ZDO restoration
            }

            // Check if we have saved data to restore from ZDO (fallback)
            int version = _nview.GetZDO().GetInt(s_versionHash, 0);

            if (version > 0 && !_dataLoaded)
            {
                if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Start: Beginning ZDO restoration for ZDO {zdoId} at {myPos} (version={version}, isOwner={_nview.IsOwner()}, noCache=true)");
                StartCoroutine(RestoreFromZDOAsync());
            }
            else if (version == 0 && !_dataLoaded)
            {
                // No ZDO data AND no cache - check if cache has anything at all for diagnostic
                FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] Start: ZDO {zdoId} has isAnchor=true but version=0 and no cache! " +
                    $"Anchor data was never saved. Pos={myPos}, isOwner={_nview.IsOwner()}");
            }
        }

        /// <summary>
        /// Clears static anchor tracking. Call on world unload.
        /// </summary>
        public static void ClearAnchorTracking()
        {
            s_anchorsByZdoId.Clear();
            s_anchorsByPosition.Clear();
        }

        private void OnDestroy()
        {
            if (_combiner != null && _combiner.Anchor == this)
            {
                _combiner.Anchor = null;
            }

            // Remove from tracking
            if (_nview != null && _nview.GetZDO() != null)
            {
                var zdoId = _nview.GetZDO().m_uid;
                if (s_anchorsByZdoId.TryGetValue(zdoId, out var tracked) && tracked == this)
                {
                    s_anchorsByZdoId.Remove(zdoId);
                }
            }

            // Remove from position tracking
            Vector3 myPos = _realWaterPosition.sqrMagnitude > 0.01f ? _realWaterPosition : transform.position;
            Vector3Int posKey = GetPositionKey(myPos);
            if (s_anchorsByPosition.TryGetValue(posKey, out var trackedByPos) && trackedByPos == this)
            {
                s_anchorsByPosition.Remove(posKey);
            }
        }

        #endregion

        #region Stripping

        /// <summary>
        /// Ensures this anchor instance is invisible and stripped.
        /// The CustomRiver prefab is already set up as a clean ZDO container
        /// (no renderers, no colliders, no children). This method handles
        /// LEGACY anchors that used the old dirtfloor prefab and still have
        /// leftover components, plus any edge cases.
        /// </summary>
        private void StripToInvisible()
        {
            if (_isStripped) return;
            _isStripped = true;

            gameObject.layer = 2; // Ignore Raycast

            // Strip any MonoBehaviours that aren't ZNetView or this anchor.
            // For new CustomRiver prefabs this is a no-op (already clean).
            // For legacy dirtfloor anchors this removes DirtFloorController etc.
            foreach (var mb in GetComponents<MonoBehaviour>())
            {
                if (mb is ZNetView) continue;
                if (mb is WaterMeshCombinerAnchor) continue;
                Destroy(mb);
            }

            // Disable renderers and colliders (legacy anchors may have them)
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null) renderer.enabled = false;
            }
            foreach (var col in GetComponentsInChildren<Collider>(true))
            {
                if (col != null) col.enabled = false;
            }

            // Destroy children (legacy anchors may have visual meshes)
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }
        }

        #endregion

        #region Linking

        /// <summary>
        /// Marks this anchor as newly created (skip restoration in Start).
        /// </summary>
        public void MarkAsNewAnchor()
        {
            _isNewlyCreated = true;
            _dataLoaded = true;
        }

        /// <summary>
        /// Links this anchor to a combiner.
        /// </summary>
        public void LinkToCombiner(WaterMeshCombiner combiner)
        {
            _combiner = combiner;
            combiner.Anchor = this;

            if (_nview != null && _nview.IsValid() && _nview.IsOwner())
            {
                var zdo = _nview.GetZDO();
                if (zdo != null)
                {
                    zdo.Set(s_isAnchorHash, true);
                    zdo.Set(s_versionHash, CURRENT_VERSION);
                }
            }

            if (WaterMeshCombiner.s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Linked to combiner at {combiner.transform.position}");
            }
        }

        #endregion

        #region Save To ZDO

        /// <summary>
        /// Saves all combiner data to the ZDO.
        /// 
        /// For small water bodies (grid <= MONOLITHIC_CELL_THRESHOLD), saves everything
        /// in the primary anchor ZDO (monolithic, backward compatible).
        /// 
        /// For large water bodies, distributes grid height data across segment anchors
        /// placed along the combiner's extent. Each segment stores a contiguous range
        /// of grid rows, keeping each ZDO's byte arrays under ~8KB.
        /// </summary>
        public void SaveToZDO()
        {
            if (_nview == null || !_nview.IsValid())
            {
                return;
            }

            if (!_nview.IsOwner())
            {
                // Not owner - skip save but don't warn (this is expected for clients)
                return;
            }

            if (_combiner == null)
            {
                FiresLogger.LogWarning("[WaterMeshCombinerAnchor] Cannot save - no combiner linked");
                return;
            }

            var zdo = _nview.GetZDO();
            if (zdo == null)
            {
                return;
            }

            try
            {
                // Ensure anchor flags are set
                zdo.Set(s_isAnchorHash, true);
                zdo.Set(s_versionHash, CURRENT_VERSION);

                // Store the REAL water position
                zdo.Set(s_realPositionHash, _combiner.transform.position);

                // Save common properties
                zdo.Set(s_liquidTypeHash, (int)(_combiner.SeasonalIce ? CustomLiquidType.Water.ToVanillaLiquidType() : _combiner.LiquidType));
                zdo.Set(s_customLiquidTypeHash, (int)_combiner.SavedCustomLiquidType);   // 0.2.873: seasonal ice saves as water
                zdo.Set(s_waveIntensityHash, _combiner.WaveIntensity);
                // Round-56: gelled volumes restore grid-authoritative.
                zdo.Set(s_isGelledHash, _combiner.IsGelled);

                // CRITICAL: Save water disks FIRST - this is the source of truth
                SaveWaterDisks(zdo);

                // Save grid configuration
                SaveGridData(zdo);

                // Save bounds
                SaveBoundsData(zdo);

                // Determine storage mode: monolithic vs segmented
                int gridCells = _combiner.GridWidth * _combiner.GridHeight;
                bool useSegments = gridCells > MONOLITHIC_CELL_THRESHOLD;

                if (useSegments)
                {
                    // SEGMENTED STORAGE: distribute height data across segment anchors
                    SaveSegmentedHeightData(zdo);

                    // Clear monolithic height arrays from primary ZDO (they're in segments now)
                    zdo.Set(s_surfaceHeightsHash, new byte[0]);
                    zdo.Set(s_terrainHeightsHash, new byte[0]);
                }
                else
                {
                    // MONOLITHIC STORAGE: everything in primary anchor (small water body)
                    SaveSurfaceHeights(zdo);
                    SaveTerrainHeights(zdo);

                    // No segments needed
                    zdo.Set(s_segmentCountHash, 0);
                    zdo.Set(s_segmentZdoidsHash, new byte[0]);
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[WaterMeshCombinerAnchor] Failed to save to ZDO: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves all water disks to the ZDO.
        /// This preserves the original disk data for proper restoration.
        /// </summary>
        private void SaveWaterDisks(ZDO zdo)
        {
            var disks = _combiner.WaterDisks;
            if (disks == null || disks.Count == 0)
            {
                zdo.Set(s_waterDisksHash, new byte[0]);
                return;
            }

            var pkg = new ZPackage();
            pkg.Write(disks.Count);

            foreach (var disk in disks)
            {
                pkg.Write(disk.StableHash);
                pkg.Write(disk.Position);
                pkg.Write(disk.Radius);
                pkg.Write(disk.MaxDepth);
                pkg.Write((int)disk.LiquidType);
                pkg.Write((int)disk.CustomLiquidType);
            }

            byte[] data = pkg.GetArray();
            byte[] compressed = CompressData(data);
            zdo.Set(s_waterDisksHash, compressed);
        }

        private void SaveGridData(ZDO zdo)
        {
            var pkg = new ZPackage();
            pkg.Write(_combiner.GridWidth);
            pkg.Write(_combiner.GridHeight);
            pkg.Write(_combiner.CellSize);
            pkg.Write(_combiner.SettledWaterLevel);
            pkg.Write(_combiner.IsPondMode);

            byte[] data = pkg.GetArray();
            byte[] compressed = CompressData(data);
            zdo.Set(s_gridDataHash, compressed);
        }

        private void SaveSurfaceHeights(ZDO zdo)
        {
            var heights = _combiner.GetSurfaceHeights();
            if (heights == null || heights.Length == 0) return;

            var pkg = new ZPackage();
            pkg.Write(heights.Length);

            foreach (var h in heights)
            {
                pkg.Write(h);
            }

            byte[] data = pkg.GetArray();
            byte[] compressed = CompressData(data);
            zdo.Set(s_surfaceHeightsHash, compressed);
        }

        private void SaveTerrainHeights(ZDO zdo)
        {
            var heights = _combiner.GetTerrainHeights();
            if (heights == null || heights.Length == 0) return;

            var pkg = new ZPackage();
            pkg.Write(heights.Length);

            foreach (var h in heights)
            {
                pkg.Write(h);
            }

            byte[] data = pkg.GetArray();
            byte[] compressed = CompressData(data);
            zdo.Set(s_terrainHeightsHash, compressed);
        }

        private void SaveBoundsData(ZDO zdo)
        {
            var bounds = _combiner.GetCombinedBounds();

            var pkg = new ZPackage();
            pkg.Write(bounds.min);
            pkg.Write(bounds.max);

            zdo.Set(s_boundsDataHash, pkg.GetArray());
        }

        #endregion

        #region Segmented Storage

        /// <summary>
        /// Calculates the number of segments needed for the current combiner grid.
        /// Based on combiner bounds extent and grid row sizing constraints.
        /// </summary>
        private int CalculateSegmentCount()
        {
            if (_combiner == null) return 1;

            var bounds = _combiner.GetCombinedBounds();
            float combinerLength = Mathf.Max(bounds.size.x, bounds.size.z);

            // At least 1 segment, space segments every SEGMENT_SPACING meters
            int countFromSpacing = Mathf.Max(1, Mathf.CeilToInt(combinerLength / SEGMENT_SPACING));

            // Also ensure each segment's data stays under ~8KB
            // Raw: gridWidth * rowCount * 4 bytes * 2 arrays (surface + terrain)
            // Target: 8192 bytes compressed, assume 2x compression ratio = 16384 raw
            int gridWidth = _combiner.GridWidth;
            int gridHeight = _combiner.GridHeight;
            int bytesPerRow = gridWidth * 4 * 2; // surface + terrain
            int maxRowsPerSegment = bytesPerRow > 0 ? Mathf.Max(1, 16384 / bytesPerRow) : gridHeight;
            int countFromSize = Mathf.Max(1, Mathf.CeilToInt((float)gridHeight / maxRowsPerSegment));

            int segmentCount = Mathf.Max(countFromSpacing, countFromSize);
            return Mathf.Clamp(segmentCount, 1, MAX_SEGMENTS);
        }

        /// <summary>
        /// Saves height data distributed across segment anchors.
        /// Creates segments if they don't exist, or reuses existing ones.
        /// </summary>
        private void SaveSegmentedHeightData(ZDO primaryZdo)
        {
            var surfaceHeights = _combiner.GetSurfaceHeights();
            var terrainHeights = _combiner.GetTerrainHeights();

            if (surfaceHeights == null || surfaceHeights.Length == 0)
            {
                primaryZdo.Set(s_segmentCountHash, 0);
                primaryZdo.Set(s_segmentZdoidsHash, new byte[0]);
                return;
            }

            int gridWidth = _combiner.GridWidth;
            int gridHeight = _combiner.GridHeight;
            int segmentCount = CalculateSegmentCount();
            int rowsPerSegment = Mathf.CeilToInt((float)gridHeight / segmentCount);

            // Ensure we have the right number of segment anchors
            EnsureSegmentAnchors(segmentCount);

            // Distribute rows across segments
            for (int i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                if (segment == null || !segment.IsLoaded) continue;

                int rowStart = i * rowsPerSegment;
                int rowCount = Mathf.Min(rowsPerSegment, gridHeight - rowStart);
                if (rowCount <= 0) continue;

                // Only save segments that we own (can write ZDO)
                if (segment.IsOwner)
                {
                    segment.SaveSegmentData(rowStart, rowCount, gridWidth, surfaceHeights, terrainHeights);
                }
            }

            // Save segment ZDOID index to primary anchor
            SaveSegmentIndex(primaryZdo);

            if (WaterMeshCombiner.s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Saved {segmentCount} segments for grid {gridWidth}x{gridHeight} " +
                    $"({rowsPerSegment} rows/segment)");
            }
        }

        /// <summary>
        /// Ensures the correct number of segment anchors exist.
        /// Creates new ones or destroys excess ones as needed.
        /// </summary>
        private void EnsureSegmentAnchors(int requiredCount)
        {
            ZDOID parentZdoid = _nview.GetZDO().m_uid;
            var bounds = _combiner.GetCombinedBounds();

            // Determine the axis to distribute segments along (longer axis)
            bool distributeAlongX = bounds.size.x >= bounds.size.z;

            // Create new segments if we need more
            while (_segments.Count < requiredCount)
            {
                int idx = _segments.Count;
                float t = requiredCount > 1 ? (float)idx / (requiredCount - 1) : 0.5f;

                Vector3 segmentPos;
                if (distributeAlongX)
                {
                    segmentPos = new Vector3(
                        Mathf.Lerp(bounds.min.x, bounds.max.x, t),
                        0f,
                        (bounds.min.z + bounds.max.z) * 0.5f
                    );
                }
                else
                {
                    segmentPos = new Vector3(
                        (bounds.min.x + bounds.max.x) * 0.5f,
                        0f,
                        Mathf.Lerp(bounds.min.z, bounds.max.z, t)
                    );
                }

                var segment = WaterSegmentAnchor.CreateSegmentAnchor(segmentPos, idx, parentZdoid);
                if (segment != null)
                {
                    _segments.Add(segment);
                }
                else
                {
                    FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] Failed to create segment {idx}");
                    break;
                }
            }

            // Destroy excess segments
            while (_segments.Count > requiredCount)
            {
                int lastIdx = _segments.Count - 1;
                var excess = _segments[lastIdx];
                _segments.RemoveAt(lastIdx);

                if (excess != null)
                {
                    excess.DestroyPermanently();
                }
            }
        }

        /// <summary>
        /// Saves the list of segment ZDOIDs to the primary anchor ZDO.
        /// </summary>
        private void SaveSegmentIndex(ZDO primaryZdo)
        {
            primaryZdo.Set(s_segmentCountHash, _segments.Count);

            if (_segments.Count == 0)
            {
                primaryZdo.Set(s_segmentZdoidsHash, new byte[0]);
                return;
            }

            var pkg = new ZPackage();
            pkg.Write(_segments.Count);

            foreach (var segment in _segments)
            {
                ZDOID zdoid = segment != null ? segment.ZdoId : ZDOID.None;
                pkg.Write(zdoid.UserID);
                pkg.Write(zdoid.ID);
            }

            primaryZdo.Set(s_segmentZdoidsHash, pkg.GetArray());
        }

        /// <summary>
        /// Loads the segment ZDOID list from the primary anchor ZDO.
        /// </summary>
        private List<ZDOID> LoadSegmentIndex(ZDO primaryZdo)
        {
            var result = new List<ZDOID>();

            byte[] data = primaryZdo.GetByteArray(s_segmentZdoidsHash);
            if (data == null || data.Length == 0) return result;

            try
            {
                var pkg = new ZPackage(data);
                int count = pkg.ReadInt();

                for (int i = 0; i < count; i++)
                {
                    long userId = pkg.ReadLong();
                    uint id = pkg.ReadUInt();
                    result.Add(new ZDOID(userId, id));
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] Failed to load segment index: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Restores height data from segment anchors.
        /// For any segments whose ZDOs are not loaded (distant zones), fills
        /// those rows with -100 (no water) so the combiner has valid array sizes.
        /// The combiner can later re-sample terrain for missing rows if needed.
        /// </summary>
        private void RestoreFromSegments(ZDO primaryZdo)
        {
            int gridWidth = _combiner.GridWidth;
            int gridHeight = _combiner.GridHeight;
            int gridSize = gridWidth * gridHeight;

            if (gridSize <= 0) return;

            // Initialize full arrays with "no water" defaults
            float[] surfaceHeights = new float[gridSize];
            float[] terrainHeights = new float[gridSize];
            for (int i = 0; i < gridSize; i++)
            {
                surfaceHeights[i] = -100f;
                terrainHeights[i] = 0f;
            }

            // Load segment ZDOID list
            var zdoids = LoadSegmentIndex(primaryZdo);
            int segmentCount = primaryZdo.GetInt(s_segmentCountHash, 0);

            if (zdoids.Count == 0 && segmentCount == 0)
            {
                // No segments - this shouldn't happen for v3 with large grids
                return;
            }

            int loadedSegments = 0;
            int totalRows = 0;

            foreach (var zdoid in zdoids)
            {
                if (zdoid.IsNone()) continue;

                var segmentZdo = ZDOMan.instance.GetZDO(zdoid);
                if (segmentZdo == null) continue; // Segment's zone not loaded yet

                int rowStart, rowCount;
                if (WaterSegmentAnchor.LoadSegmentDataFromZDO(segmentZdo, surfaceHeights, terrainHeights,
                    gridWidth, out rowStart, out rowCount))
                {
                    loadedSegments++;
                    totalRows += rowCount;
                }
            }

            _combiner.SetSurfaceHeights(surfaceHeights);
            _combiner.SetTerrainHeights(terrainHeights);

            if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Restored {loadedSegments}/{zdoids.Count} segments " +
                $"({totalRows}/{gridHeight} rows) for grid {gridWidth}x{gridHeight}");
        }

        /// <summary>
        /// Destroys all segment anchors owned by this primary anchor.
        /// Called during permanent deletion.
        /// </summary>
        private void DestroyAllSegments()
        {
            // Destroy live segment instances
            foreach (var segment in _segments)
            {
                if (segment != null)
                {
                    segment.DestroyPermanently();
                }
            }
            _segments.Clear();

            // Also destroy segments referenced by ZDOID (may not have live instances)
            if (_nview != null && _nview.GetZDO() != null)
            {
                var zdoids = LoadSegmentIndex(_nview.GetZDO());
                foreach (var zdoid in zdoids)
                {
                    if (zdoid.IsNone()) continue;

                    var segmentZdo = ZDOMan.instance.GetZDO(zdoid);
                    if (segmentZdo != null)
                    {
                        // Clear segment data
                        segmentZdo.Set(WaterSegmentAnchor.s_isSegmentHash, false);
                        segmentZdo.Set(WaterSegmentAnchor.s_segmentSurfaceHash, new byte[0]);
                        segmentZdo.Set(WaterSegmentAnchor.s_segmentTerrainHash, new byte[0]);
                    }
                }
            }

            _segmentZdoids.Clear();
        }

        #endregion

        #region Restore From ZDO

        /// <summary>
        /// Restores the combiner from ZDO data asynchronously.
        /// 
        /// Supports both v2 (monolithic) and v3 (segmented) formats:
        /// - v2: All height data in the primary anchor ZDO (legacy)
        /// - v3: Height data distributed across segment anchor ZDOs
        ///       (small water bodies with segment_count=0 still use monolithic)
        /// 
        /// v2 anchors are auto-upgraded to v3 on the first save after restore.
        /// </summary>
        private IEnumerator RestoreFromZDOAsync()
        {
            if (_dataLoaded)
            {
                yield break;
            }

            var zdo = _nview?.GetZDO();
            if (zdo == null)
            {
                FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] RestoreFromZDOAsync: ZDO is null!");
                yield break;
            }

            _dataLoaded = true;

            // Load the REAL water position
            _realWaterPosition = zdo.GetVec3(s_realPositionHash, transform.position);
            Vector3 combinerPosition = _realWaterPosition.sqrMagnitude > 0.01f ? _realWaterPosition : transform.position;
            int version = zdo.GetInt(s_versionHash, 0);

            if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] RestoreFromZDOAsync: Starting restoration at {combinerPosition} " +
                $"(anchor at {transform.position}, ZDO {zdo.m_uid}, version={version})");

            // Check if a combiner already exists at this position
            foreach (var existingCombiner in WaterMeshCombiner.AllCombiners)
            {
                if (existingCombiner == null) continue;

                float dist = Vector3.Distance(existingCombiner.transform.position, combinerPosition);
                if (dist < 2f)
                {
                    if (existingCombiner.Anchor != null && existingCombiner.Anchor != this)
                    {
                        if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] RestoreFromZDOAsync: Combiner at {combinerPosition} already has anchor. This is STALE - destroying self.");
                        DestroyPermanently();
                        yield break;
                    }

                    _combiner = existingCombiner;
                    if (_combiner.Anchor == null)
                    {
                        _combiner.Anchor = this;
                    }
                    if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] RestoreFromZDOAsync: Linked to existing combiner at {combinerPosition}");
                    yield break;
                }
            }

            yield return null;

            // Create combiner GameObject
            var combinerGO = new GameObject($"WaterMeshCombiner_Restored_{zdo.m_uid}");
            combinerGO.transform.position = combinerPosition;

            _combiner = combinerGO.AddComponent<WaterMeshCombiner>();
            _combiner.Anchor = this;
            _combiner.SetRestoredFromPersistence(true);

            // Load common properties
            _combiner.LiquidType = (LiquidType)zdo.GetInt(s_liquidTypeHash, 0);
            _combiner.CustomLiquidType = (CustomLiquidType)zdo.GetInt(s_customLiquidTypeHash, 0);
            _combiner.WaveIntensity = zdo.GetFloat(s_waveIntensityHash, 0.5f);
            // Round-56: set the gelled flag BEFORE RestoreFromAnchorData /
            // any regen so a later MarkDirty routes to the grid rebuild
            // instead of recomputing the settled surface from disks.
            // Old anchors have no key → false → unchanged behaviour.
            _combiner.IsGelled = zdo.GetBool(s_isGelledHash, false);

            yield return null;

            // Load water disks (source of truth, all versions)
            if (version >= 2)
            {
                LoadWaterDisks(zdo);
            }

            yield return null;

            // Load grid configuration and bounds (same for v2 and v3)
            LoadGridData(zdo);

            yield return null;

            LoadBoundsData(zdo);

            yield return null;

            // VERSION-DEPENDENT HEIGHT DATA LOADING
            int segmentCount = zdo.GetInt(s_segmentCountHash, 0);
            bool isSegmented = version >= 3 && segmentCount > 0;

            if (isSegmented)
            {
                // V3 SEGMENTED: load height data from segment anchors
                RestoreFromSegments(zdo);
            }
            else
            {
                // V2 MONOLITHIC (or v3 small water with segment_count=0): load from primary ZDO
                LoadSurfaceHeights(zdo);
                LoadTerrainHeights(zdo);
            }

            yield return null;

            // Decide how to restore based on what data we have
            bool hasGridData = _combiner.GetSurfaceHeights() != null && _combiner.GetSurfaceHeights().Length > 0;
            int diskCount = _combiner.WaterDisks.Count;

            // Diagnostic logging
            byte[] gridDataBytes = zdo.GetByteArray(s_gridDataHash);
            byte[] disksBytes = zdo.GetByteArray(s_waterDisksHash);
            if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] RestoreFromZDOAsync: ZDO data - " +
                $"gridData={gridDataBytes?.Length ?? 0}, disks={disksBytes?.Length ?? 0} | " +
                $"hasGridData={hasGridData}, diskCount={diskCount}, version={version}, " +
                $"segmented={isSegmented}, segments={segmentCount}, " +
                $"gridW={_combiner.GridWidth}, gridH={_combiner.GridHeight}");

            if (hasGridData)
            {
                _combiner.RestoreFromAnchorData();
                _combiner.SetRestoredFromPersistence(true);
                WaterCombinerCache.UpdateCombinerCache(_combiner);

                // V2 -> V3 MIGRATION: next save will use v3 format automatically
                if (version < 3 && _nview.IsOwner())
                {
                    if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] v{version}?v3 migration queued for next save at {combinerPosition}");
                }

                if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] RestoreFromZDOAsync: Restored from " +
                    $"{(isSegmented ? "segmented" : "monolithic")} data at {combinerPosition}");
            }
            else if (diskCount > 0)
            {
                if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Regenerating from {diskCount} disks at {combinerPosition} (no saved grid data)");
                _combiner.ForceRegenerate();
                WaterCombinerCache.UpdateCombinerCache(_combiner);
            }
            else
            {
                bool isOwner = _nview != null && _nview.IsOwner();
                FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] No valid data to restore at {combinerPosition} - " +
                    $"no grid data AND no disks! isOwner={isOwner}, version={version}. " +
                    (isOwner
                        ? "This anchor ZDO was not saved properly during generation."
                        : "ZDO byte arrays have not synced to this client. Scheduling retry."));

                if (!isOwner)
                {
                    yield return StartCoroutine(RetryRestoreFromZDO(combinerPosition, zdo.m_uid));
                }
                else
                {
                    FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] Owner has no data for anchor at {combinerPosition}. Destroying empty combiner.");
                    if (_combiner != null)
                    {
                        Destroy(_combiner.gameObject);
                        _combiner = null;
                    }
                }
            }
        }

        /// <summary>
        /// Retry coroutine for clients where ZDO byte arrays haven't synced yet.
        /// Waits progressively longer between attempts (3s, 6s, 12s) then gives up.
        /// If data arrives on any retry, restores the combiner normally.
        /// If all retries fail, destroys the empty combiner to avoid invisible objects.
        /// </summary>
        private IEnumerator RetryRestoreFromZDO(Vector3 combinerPosition, ZDOID zdoId)
        {
            float[] retryDelays = { 3f, 6f, 12f };

            for (int attempt = 0; attempt < retryDelays.Length; attempt++)
            {
                yield return new WaitForSeconds(retryDelays[attempt]);

                // Check if combiner was destroyed or data loaded by another path
                if (_combiner == null || this == null) yield break;

                var zdo = _nview?.GetZDO();
                if (zdo == null) continue;

                // Re-check byte arrays
                byte[] gridBytes = zdo.GetByteArray(s_gridDataHash);
                byte[] disksBytes = zdo.GetByteArray(s_waterDisksHash);
                byte[] surfaceBytes = zdo.GetByteArray(s_surfaceHeightsHash);

                if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Retry {attempt + 1}/{retryDelays.Length} for ZDO {zdoId} at {combinerPosition}: " +
                    $"grid={gridBytes?.Length ?? 0}, disks={disksBytes?.Length ?? 0}, surface={surfaceBytes?.Length ?? 0}");

                // Try loading disks
                if (disksBytes != null && disksBytes.Length > 0 && _combiner.WaterDisks.Count == 0)
                {
                    LoadWaterDisks(zdo);
                }

                // Try loading grid data
                if (gridBytes != null && gridBytes.Length > 0 && _combiner.GridWidth == 0)
                {
                    LoadGridData(zdo);
                    LoadSurfaceHeights(zdo);
                    LoadTerrainHeights(zdo);
                    LoadBoundsData(zdo);
                }

                // Check if we now have data
                bool hasGridData = _combiner.GetSurfaceHeights() != null && _combiner.GetSurfaceHeights().Length > 0;
                int diskCount = _combiner.WaterDisks.Count;

                if (hasGridData)
                {
                    _combiner.RestoreFromAnchorData();
                    _combiner.SetRestoredFromPersistence(true);
                    WaterCombinerCache.UpdateCombinerCache(_combiner);
                    if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Retry {attempt + 1} SUCCESS: Restored from grid data at {combinerPosition}");
                    yield break;
                }
                else if (diskCount > 0)
                {
                    if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Retry {attempt + 1} SUCCESS: Regenerating from {diskCount} disks at {combinerPosition}");
                    _combiner.ForceRegenerate();
                    WaterCombinerCache.UpdateCombinerCache(_combiner);
                    yield break;
                }
            }

            // All retries exhausted - ZDO byte arrays will never sync to non-owners.
            // FALLBACK: Request disk data from server via RPC. The server has the
            // authoritative disk list in its WaterOperationRPC cache. The client
            // receives the disks and regenerates the mesh locally via terrain sampling.
            // This is slower than grid restoration but produces visible water.
            FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] All retries exhausted for ZDO {zdoId} at {combinerPosition}. " +
                $"Requesting disk data from server via RPC fallback.");

            if (_combiner != null && WaterOperationRPC.IsMultiplayer())
            {
                // Request disks from server for this specific anchor position
                WaterOperationRPC.RequestDiskSyncForPosition(combinerPosition, _combiner);
                // Don't destroy the combiner - the RPC callback will populate it
            }
            else if (_combiner != null)
            {
                // Single player with no data - genuinely broken
                FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] Single player with no data for anchor at {combinerPosition}. Destroying empty combiner.");
                Destroy(_combiner.gameObject);
                _combiner = null;
            }
        }

        /// <summary>
        /// Loads water disks from ZDO.
        /// This restores the original disk data for proper mesh generation.
        /// </summary>
        private void LoadWaterDisks(ZDO zdo)
        {
            byte[] compressed = zdo.GetByteArray(s_waterDisksHash);
            if (compressed == null || compressed.Length == 0)
            {
                // This is normal for water bodies that have been mopped away
                return;
            }

            try
            {
                byte[] data = DecompressData(compressed);
                if (data == null || data.Length == 0)
                {
                    FiresLogger.LogWarning("[WaterMeshCombinerAnchor] LoadWaterDisks: Decompressed data is empty");
                    return;
                }

                var pkg = new ZPackage(data);

                int count = pkg.ReadInt();

                // Sanity check: each disk needs at minimum:
                //   stableHash(4) + position(12) + radius(4) + maxDepth(4) + liquidType(4) = 28 bytes
                // If count is absurdly large or data is too small, the data is corrupt.
                if (count < 0 || count > 10000)
                {
                    FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] LoadWaterDisks: Invalid disk count {count}, skipping");
                    return;
                }

                int minBytesPerDisk = 28; // 4+12+4+4+4
                int remainingBytes = data.Length - 4; // subtract the count int we already read
                if (count > 0 && remainingBytes < count * minBytesPerDisk)
                {
                    FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] LoadWaterDisks: Data too small for {count} disks " +
                                   $"(need {count * minBytesPerDisk} bytes, have {remainingBytes}). Data may be corrupt or from an older version.");
                    // Don't return - try to read what we can, the loop will catch individual failures
                }

                var disks = new System.Collections.Generic.List<WaterDisk>(count);

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        int stableHash = pkg.ReadInt();
                        Vector3 position = pkg.ReadVector3();
                        float radius = pkg.ReadSingle();
                        float maxDepth = pkg.ReadSingle();
                        LiquidType liquidType = (LiquidType)pkg.ReadInt();

                        // Read CustomLiquidType if available (v3+ data)
                        CustomLiquidType customType = liquidType.ToCustomLiquidType();
                        try { customType = (CustomLiquidType)pkg.ReadInt(); } catch { }

                        var disk = new WaterDisk(position, radius, maxDepth, liquidType, stableHash);
                        disk.CustomLiquidType = customType;
                        disks.Add(disk);
                    }
                    catch (Exception diskEx)
                    {
                        // Truncated data mid-disk - stop reading further disks
                        FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] LoadWaterDisks: Failed to read disk {i + 1}/{count}: {diskEx.Message}. Loading {disks.Count} disks that were read successfully.");
                        break;
                    }
                }

                // Add all successfully loaded disks in batch
                if (disks.Count > 0)
                {
                    _combiner.AddWaterDisksBatch(disks, skipRegeneration: true);
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] LoadWaterDisks: Failed to load water disks: {ex.Message}");
            }
        }

        private void LoadGridData(ZDO zdo)
        {
            byte[] compressed = zdo.GetByteArray(s_gridDataHash);
            if (compressed == null || compressed.Length == 0) return;

            byte[] data = DecompressData(compressed);
            var pkg = new ZPackage(data);

            int gridWidth = pkg.ReadInt();
            int gridHeight = pkg.ReadInt();
            float cellSize = pkg.ReadSingle();
            float settledLevel = pkg.ReadSingle();
            bool isPondMode = pkg.ReadBool();

            _combiner.SetGridConfiguration(gridWidth, gridHeight, cellSize, settledLevel, isPondMode);
        }

        private void LoadSurfaceHeights(ZDO zdo)
        {
            byte[] compressed = zdo.GetByteArray(s_surfaceHeightsHash);
            if (compressed == null || compressed.Length == 0) return;

            byte[] data = DecompressData(compressed);
            var pkg = new ZPackage(data);

            int count = pkg.ReadInt();
            float[] heights = new float[count];

            for (int i = 0; i < count; i++)
            {
                heights[i] = pkg.ReadSingle();
            }

            _combiner.SetSurfaceHeights(heights);
        }

        private void LoadTerrainHeights(ZDO zdo)
        {
            byte[] compressed = zdo.GetByteArray(s_terrainHeightsHash);
            if (compressed == null || compressed.Length == 0) return;

            byte[] data = DecompressData(compressed);
            var pkg = new ZPackage(data);

            int count = pkg.ReadInt();
            float[] heights = new float[count];

            for (int i = 0; i < count; i++)
            {
                heights[i] = pkg.ReadSingle();
            }

            _combiner.SetTerrainHeights(heights);
        }

        private void LoadBoundsData(ZDO zdo)
        {
            byte[] data = zdo.GetByteArray(s_boundsDataHash);
            if (data == null || data.Length == 0) return;

            var pkg = new ZPackage(data);
            Vector3 min = pkg.ReadVector3();
            Vector3 max = pkg.ReadVector3();

            _combiner.SetBoundsData(min, max);
        }

        #endregion

        #region Compression

        private static byte[] CompressData(byte[] data)
        {
            if (data == null || data.Length == 0) return data;

            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress))
                {
                    gzip.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        private static byte[] DecompressData(byte[] compressed)
        {
            if (compressed == null || compressed.Length == 0) return compressed;

            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        #endregion

        #region Permanent Deletion

        /// <summary>
        /// Permanently destroys this anchor and its ZDO.
        /// 
        /// Uses ZNetScene.Destroy() for proper ZDO cleanup that works with other mods.
        /// Falls back to manual cleanup if ZNetScene is unavailable.
        /// </summary>
        public void DestroyPermanently()
        {
            if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Permanently destroying anchor at {transform.position}");

            // Destroy all segment anchors first
            DestroyAllSegments();

            // Unlink from combiner first
            if (_combiner != null && _combiner.Anchor == this)
            {
                _combiner.Anchor = null;
            }
            _combiner = null;

            // Clear ZDO data to prevent it from being restored
            if (_nview != null && _nview.IsValid())
            {
                var zdo = _nview.GetZDO();
                if (zdo != null)
                {
                    // Clear the anchor flags and data so it won't restore
                    zdo.Set(s_versionHash, 0);
                    zdo.Set(s_isAnchorHash, false);

                    // Clear saved data
                    byte[] emptyData = new byte[0];
                    zdo.Set(s_gridDataHash, emptyData);
                    zdo.Set(s_surfaceHeightsHash, emptyData);
                    zdo.Set(s_terrainHeightsHash, emptyData);
                    zdo.Set(s_boundsDataHash, emptyData);
                    zdo.Set(s_waterDisksHash, emptyData);
                    zdo.Set(s_segmentZdoidsHash, emptyData);
                    zdo.Set(s_segmentCountHash, 0);
                }
            }

            // Use ZNetScene.Destroy() for proper networked destruction
            // This handles ZDO destruction in the correct order and notifies other mods
            if (ZNetScene.instance != null && _nview != null && _nview.IsValid())
            {
                try
                {
                    ZNetScene.instance.Destroy(gameObject);
                    return; // ZNetScene.Destroy handles everything
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[WaterMeshCombinerAnchor] ZNetScene.Destroy failed: {ex.Message}");
                    // Fall through to manual cleanup
                }
            }

            // Fallback: Manual destruction if ZNetScene isn't available
            // This can happen during scene unload or if ZNetScene is already destroyed
            Destroy(gameObject);
        }

        #endregion

        #region Static Factory

        /// <summary>
        /// ZDO hash for checking if a prefab is a water anchor.
        /// Public so it can be checked from DirtFloorController.Awake.
        /// </summary>
        public static int IsWaterAnchorHash => s_isAnchorHash;

        /// <summary>
        /// Called from DirtFloorController.Awake to check if this instance should become a water anchor.
        /// Returns true if this is a water anchor (so DirtFloorController should destroy itself).
        /// This enables water anchor restoration on zone reload.
        /// 
        /// DEDUPLICATION: Uses static tracking to prevent double-conversion of the same GameObject.
        /// </summary>
        private static System.Collections.Generic.HashSet<int> s_convertedInstanceIds = new System.Collections.Generic.HashSet<int>();

        // Phase F (2026-05-25): CheckAndConvertToWaterAnchor(DirtFloorController)
        // legacy fallback removed. With the DirtFloorController class deleted
        // there are no longer any objects spawning under that prefab to
        // convert. Water anchors are now created exclusively via the
        // CustomRiver prefab through CreateAnchor below.

        /// <summary>
        /// Shared conversion logic for LEGACY anchors (dirtfloor prefab).
        /// Adds WaterMeshCombinerAnchor component if not already present.
        /// </summary>
        private static void ConvertGameObjectToWaterAnchor(GameObject go)
        {
            int instanceId = go.GetInstanceID();
            if (s_convertedInstanceIds.Contains(instanceId))
            {
                return;
            }
            s_convertedInstanceIds.Add(instanceId);

            var existingAnchor = go.GetComponent<WaterMeshCombinerAnchor>();
            if (existingAnchor == null)
            {
                if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Converting legacy {go.name} to water anchor at {go.transform.position}");
                go.AddComponent<WaterMeshCombinerAnchor>();
            }
        }

        /// <summary>
        /// Clears the conversion tracking set. Call on world unload.
        /// </summary>
        public static void ClearConversionTracking()
        {
            s_convertedInstanceIds.Clear();

            // Clear prefab cache so stale references from a previous session
            // don't persist across world loads
            s_cachedPrefab = null;

            // Clear segment anchor prefab cache
            WaterSegmentAnchor.ClearPrefabCache();
        }

        /// <summary>
        /// Creates a new invisible anchor for a water combiner.
        /// Uses the CustomRiver prefab (a clean ZDO container with only ZNetView +
        /// WaterMeshCombinerAnchor). Spawned at Y=-10 to stay hidden.
        /// </summary>
        public static WaterMeshCombinerAnchor CreateAnchor(Vector3 position)
        {
            if (ZNetScene.instance == null)
            {
                FiresLogger.LogError("[WaterMeshCombinerAnchor] Cannot create anchor - ZNetScene not available");
                return null;
            }

            GameObject prefab = FindPrefab();
            if (prefab == null)
            {
                FiresLogger.LogError("[WaterMeshCombinerAnchor] Cannot find prefab for anchor - water will NOT persist!");
                return null;
            }

            Vector3 anchorSpawnPos = new Vector3(position.x, ANCHOR_HIDDEN_Y, position.z);

            // Set flag BEFORE Instantiate so the prefab's Awake() doesn't self-destruct
            // (the ZDO isAnchor flag hasn't been set yet at Instantiate time).
            s_nextInstantiateIsPending = true;
            var go = UnityEngine.Object.Instantiate(prefab, anchorSpawnPos, Quaternion.identity);
            s_nextInstantiateIsPending = false;

            // Get the anchor component (already on the prefab)
            var anchor = go.GetComponent<WaterMeshCombinerAnchor>();
            if (anchor == null)
            {
                anchor = go.AddComponent<WaterMeshCombinerAnchor>();
            }
            anchor._realWaterPosition = position;
            anchor.MarkAsNewAnchor();

            // Set ZDO flags so the anchor is recognized on zone reload
            var nview = go.GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null)
            {
                var zdo = nview.GetZDO();
                zdo.Set(s_isAnchorHash, true);
                zdo.Set(s_versionHash, CURRENT_VERSION);
                zdo.Set(s_realPositionHash, position);
            }

            if (WaterMeshCombiner.s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Created anchor at Y={ANCHOR_HIDDEN_Y} for water at {position}");
            }

            return anchor;
        }

        /// <summary>
        /// Cached prefab reference to avoid repeated lookups.
        /// Cleared on logout to prevent stale references across sessions.
        /// </summary>
        private static GameObject s_cachedPrefab;

        /// <summary>
        /// Finds a prefab to use for anchor ZDO.
        /// Prefers CustomRiver - it's on the "piece" layer so it won't interfere
        /// with terrain raycasts (the old dirtfloor prefab was on "terrain" layer
        /// which caused river surfaces to get pulled down to anchor Y=-10).
        /// Does NOT fall back to dirtfloor - using dirtfloor caused water anchors to
        /// spawn as terrain pieces and interfere with the terrain system.
        /// Result is cached after first successful lookup.
        /// </summary>
        private static GameObject FindPrefab()
        {
            // Return cached prefab if still valid
            if (s_cachedPrefab != null)
                return s_cachedPrefab;

            // Water anchors MUST use CustomRiver prefab (piece layer).
            // Using dirtfloor prefab causes water anchors to spawn as terrain pieces
            // and interfere with the terrain system.
            string[] names = {
                "CustomRiver",
                "customriver",
            };

            foreach (var name in names)
            {
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab != null)
                {
                    s_cachedPrefab = prefab;
                    if (WaterMeshCombiner.s_verboseLogging) FiresLogger.LogInfo($"[WaterMeshCombinerAnchor] Using '{prefab.name}' prefab for water anchors (piece layer)");
                    return prefab;
                }
            }

            FiresLogger.LogError("[WaterMeshCombinerAnchor] FindPrefab: CustomRiver prefab NOT found in ZNetScene! " +
                "Water persistence will FAIL! Ensure CustomRiver is registered in VAPieceManager and ZNetScene.");
            return null;
        }

        #endregion
    }
}
