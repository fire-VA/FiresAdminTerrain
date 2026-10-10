using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// A child anchor ZDO that stores a portion of a water combiner's grid data.
    /// 
    /// Segment anchors are placed along the water body's extent so that at least
    /// one is always in a loaded zone near the player. Each segment stores a
    /// contiguous range of grid rows (surface + terrain heights).
    /// 
    /// Segment anchors use the same CustomRiver prefab as the primary anchor.
    /// They are invisible (Y=-10) and have no combiner of their own.
    /// 
    /// LIFECYCLE:
    /// - Created by WaterMeshCombinerAnchor during SaveToZDO when segmentation is needed
    /// - Awake/Start detect the va_is_water_segment flag and skip combiner restoration
    /// - SaveSegmentData/LoadSegmentData handle row-range height data
    /// - DestroyPermanently cleans up the ZDO when the parent anchor is destroyed
    /// </summary>
    public class WaterSegmentAnchor : MonoBehaviour
    {
        #region ZDO Keys

        // Segment identification
        internal static readonly int s_isSegmentHash = "va_is_water_segment".GetStableHashCode();
        internal static readonly int s_segmentIndexHash = "va_water_segment_index".GetStableHashCode();
        internal static readonly int s_segmentParentHash = "va_water_segment_parent_zdoid".GetStableHashCode();

        // Segment data
        internal static readonly int s_segmentRowStartHash = "va_water_segment_row_start".GetStableHashCode();
        internal static readonly int s_segmentRowCountHash = "va_water_segment_row_count".GetStableHashCode();
        internal static readonly int s_segmentSurfaceHash = "va_water_segment_surface".GetStableHashCode();
        internal static readonly int s_segmentTerrainHash = "va_water_segment_terrain".GetStableHashCode();

        // Hidden Y position for segment anchors (same as primary anchor)
        private const float ANCHOR_HIDDEN_Y = -10f;

        // Prefab name for segment anchors (same as primary anchor)
        private const string BASE_PREFAB_NAME = "CustomRiver";

        #endregion

        #region Fields

        private ZNetView _nview;
        private bool _isStripped = false;

        #endregion

        #region Properties

        /// <summary>
        /// Whether this segment's ZDO is valid and loaded.
        /// </summary>
        public bool IsLoaded => _nview != null && _nview.IsValid() && _nview.GetZDO() != null;

        /// <summary>
        /// Whether this segment's ZDO is owned by this peer (can write).
        /// </summary>
        public bool IsOwner => _nview != null && _nview.IsValid() && _nview.IsOwner();

        /// <summary>
        /// The ZDOID of this segment's ZDO.
        /// </summary>
        public ZDOID ZdoId
        {
            get
            {
                if (_nview != null && _nview.GetZDO() != null)
                    return _nview.GetZDO().m_uid;
                return ZDOID.None;
            }
        }

        /// <summary>
        /// The segment index within the parent anchor's segment list.
        /// </summary>
        public int SegmentIndex { get; private set; } = -1;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();

            if (_nview == null || _nview.GetZDO() == null)
                return;

            bool isSegment = _nview.GetZDO().GetBool(s_isSegmentHash, false);

            // If this is a segment anchor spawning from ZDO, strip it to invisible.
            // Segments don't create combiners - they only store grid row data.
            if (isSegment)
            {
                SegmentIndex = _nview.GetZDO().GetInt(s_segmentIndexHash, -1);
                StripToInvisible();
            }
        }

        private void Start()
        {
            // Segments are passive data containers - no Start logic needed.
            // The primary anchor's RestoreFromZDOAsync reads segment data directly
            // from ZDOMan by ZDOID, without needing the segment GameObject.
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes a newly created segment anchor with its metadata.
        /// Called by WaterMeshCombinerAnchor when creating segments during save.
        /// </summary>
        public void Initialize(int segmentIndex, ZDOID parentZdoid)
        {
            _nview = GetComponent<ZNetView>();
            SegmentIndex = segmentIndex;

            if (_nview == null || _nview.GetZDO() == null)
            {
                FiresLogger.LogError($"[WaterSegmentAnchor] Initialize: No ZDO available for segment {segmentIndex}!");
                return;
            }

            var zdo = _nview.GetZDO();
            zdo.Set(s_isSegmentHash, true);
            zdo.Set(s_segmentIndexHash, segmentIndex);

            // Store parent ZDOID as two longs (userID + id)
            var parentPkg = new ZPackage();
            parentPkg.Write(parentZdoid.UserID);
            parentPkg.Write(parentZdoid.ID);
            zdo.Set(s_segmentParentHash, parentPkg.GetArray());

            StripToInvisible();
        }

        #endregion

        #region Save/Load Segment Data

        /// <summary>
        /// Saves a range of grid rows (surface + terrain heights) to this segment's ZDO.
        /// Data is compressed with GZip to stay within ZDO byte array limits (~8KB target).
        /// </summary>
        /// <param name="rowStart">First row index to store</param>
        /// <param name="rowCount">Number of rows to store</param>
        /// <param name="gridWidth">Grid width (columns per row)</param>
        /// <param name="surfaceHeights">Full surface heights array from combiner</param>
        /// <param name="terrainHeights">Full terrain heights array from combiner</param>
        public void SaveSegmentData(int rowStart, int rowCount, int gridWidth,
            float[] surfaceHeights, float[] terrainHeights)
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner())
                return;

            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            zdo.Set(s_segmentRowStartHash, rowStart);
            zdo.Set(s_segmentRowCountHash, rowCount);

            // Save surface heights for this row range
            if (surfaceHeights != null && surfaceHeights.Length > 0)
            {
                byte[] surfaceData = PackRowRange(surfaceHeights, rowStart, rowCount, gridWidth);
                byte[] compressed = CompressData(surfaceData);
                zdo.Set(s_segmentSurfaceHash, compressed);
            }

            // Save terrain heights for this row range
            if (terrainHeights != null && terrainHeights.Length > 0)
            {
                byte[] terrainData = PackRowRange(terrainHeights, rowStart, rowCount, gridWidth);
                byte[] compressed = CompressData(terrainData);
                zdo.Set(s_segmentTerrainHash, compressed);
            }
        }

        /// <summary>
        /// Loads this segment's row data from its ZDO and writes it into the full-size arrays.
        /// Returns true if data was successfully loaded.
        /// </summary>
        /// <param name="surfaceHeights">Full surface heights array to write into</param>
        /// <param name="terrainHeights">Full terrain heights array to write into</param>
        /// <param name="gridWidth">Grid width (columns per row)</param>
        /// <param name="rowStart">Output: first row stored in this segment</param>
        /// <param name="rowCount">Output: number of rows stored</param>
        public bool LoadSegmentData(float[] surfaceHeights, float[] terrainHeights,
            int gridWidth, out int rowStart, out int rowCount)
        {
            rowStart = 0;
            rowCount = 0;

            if (_nview == null || _nview.GetZDO() == null)
                return false;

            var zdo = _nview.GetZDO();
            rowStart = zdo.GetInt(s_segmentRowStartHash, 0);
            rowCount = zdo.GetInt(s_segmentRowCountHash, 0);

            if (rowCount <= 0 || gridWidth <= 0) return false;

            // Load surface heights
            byte[] surfaceCompressed = zdo.GetByteArray(s_segmentSurfaceHash);
            if (surfaceCompressed != null && surfaceCompressed.Length > 0 && surfaceHeights != null)
            {
                byte[] surfaceData = DecompressData(surfaceCompressed);
                UnpackRowRange(surfaceData, surfaceHeights, rowStart, rowCount, gridWidth);
            }

            // Load terrain heights
            byte[] terrainCompressed = zdo.GetByteArray(s_segmentTerrainHash);
            if (terrainCompressed != null && terrainCompressed.Length > 0 && terrainHeights != null)
            {
                byte[] terrainData = DecompressData(terrainCompressed);
                UnpackRowRange(terrainData, terrainHeights, rowStart, rowCount, gridWidth);
            }

            return true;
        }

        /// <summary>
        /// Static version: loads segment data directly from a ZDO without needing
        /// a WaterSegmentAnchor component. Used during restore when we only have ZDOIDs.
        /// </summary>
        public static bool LoadSegmentDataFromZDO(ZDO zdo, float[] surfaceHeights,
            float[] terrainHeights, int gridWidth, out int rowStart, out int rowCount)
        {
            rowStart = 0;
            rowCount = 0;

            if (zdo == null) return false;

            rowStart = zdo.GetInt(s_segmentRowStartHash, 0);
            rowCount = zdo.GetInt(s_segmentRowCountHash, 0);

            if (rowCount <= 0 || gridWidth <= 0) return false;

            // Load surface heights
            byte[] surfaceCompressed = zdo.GetByteArray(s_segmentSurfaceHash);
            if (surfaceCompressed != null && surfaceCompressed.Length > 0 && surfaceHeights != null)
            {
                byte[] surfaceData = DecompressData(surfaceCompressed);
                UnpackRowRange(surfaceData, surfaceHeights, rowStart, rowCount, gridWidth);
            }

            // Load terrain heights
            byte[] terrainCompressed = zdo.GetByteArray(s_segmentTerrainHash);
            if (terrainCompressed != null && terrainCompressed.Length > 0 && terrainHeights != null)
            {
                byte[] terrainData = DecompressData(terrainCompressed);
                UnpackRowRange(terrainData, terrainHeights, rowStart, rowCount, gridWidth);
            }

            return true;
        }

        #endregion

        #region Row Packing

        /// <summary>
        /// Packs a contiguous range of rows from a full-size float array into a byte array.
        /// Format: raw float bytes (4 bytes per float, rowCount * gridWidth floats).
        /// </summary>
        private static byte[] PackRowRange(float[] fullArray, int rowStart, int rowCount, int gridWidth)
        {
            int startIdx = rowStart * gridWidth;
            int count = rowCount * gridWidth;

            // Clamp to array bounds
            if (startIdx + count > fullArray.Length)
                count = fullArray.Length - startIdx;
            if (count <= 0) return new byte[0];

            byte[] data = new byte[count * 4];
            Buffer.BlockCopy(fullArray, startIdx * 4, data, 0, count * 4);
            return data;
        }

        /// <summary>
        /// Unpacks a byte array of float data into the correct row range of a full-size array.
        /// </summary>
        private static void UnpackRowRange(byte[] data, float[] fullArray, int rowStart, int rowCount, int gridWidth)
        {
            if (data == null || data.Length == 0 || fullArray == null) return;

            int startIdx = rowStart * gridWidth;
            int count = rowCount * gridWidth;

            // Clamp to array bounds
            if (startIdx + count > fullArray.Length)
                count = fullArray.Length - startIdx;
            if (count <= 0) return;

            int bytesToCopy = count * 4;
            if (bytesToCopy > data.Length)
                bytesToCopy = data.Length;

            Buffer.BlockCopy(data, 0, fullArray, startIdx * 4, bytesToCopy);
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

        #region Stripping

        private void StripToInvisible()
        {
            if (_isStripped) return;
            _isStripped = true;

            gameObject.layer = 2; // Ignore Raycast

            // Strip MonoBehaviours except ZNetView and this segment anchor
            foreach (var mb in GetComponents<MonoBehaviour>())
            {
                if (mb is ZNetView) continue;
                if (mb is WaterSegmentAnchor) continue;
                if (mb is WaterMeshCombinerAnchor) continue; // Don't strip if on same prefab
                Destroy(mb);
            }

            // Disable renderers and colliders
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null) renderer.enabled = false;
            }
            foreach (var col in GetComponentsInChildren<Collider>(true))
            {
                if (col != null) col.enabled = false;
            }

            // Destroy children
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }
        }

        #endregion

        #region Destruction

        /// <summary>
        /// Permanently destroys this segment anchor and its ZDO.
        /// </summary>
        public void DestroyPermanently()
        {
            if (_nview != null && _nview.IsValid())
            {
                var zdo = _nview.GetZDO();
                if (zdo != null)
                {
                    // Clear segment data to prevent ghost restoration
                    zdo.Set(s_isSegmentHash, false);
                    zdo.Set(s_segmentSurfaceHash, new byte[0]);
                    zdo.Set(s_segmentTerrainHash, new byte[0]);
                }
            }

            if (ZNetScene.instance != null && _nview != null && _nview.IsValid())
            {
                try
                {
                    ZNetScene.instance.Destroy(gameObject);
                    return;
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[WaterSegmentAnchor] ZNetScene.Destroy failed: {ex.Message}");
                }
            }

            Destroy(gameObject);
        }

        #endregion

        #region Static Factory

        /// <summary>
        /// Cached prefab reference.
        /// </summary>
        private static GameObject s_cachedPrefab;

        /// <summary>
        /// Flag to prevent Awake self-destruction during instantiation.
        /// </summary>
        internal static bool s_nextInstantiateIsPending = false;

        /// <summary>
        /// Creates a new segment anchor at the given position.
        /// </summary>
        /// <param name="position">World position for the segment (placed at ANCHOR_HIDDEN_Y)</param>
        /// <param name="segmentIndex">Index of this segment</param>
        /// <param name="parentZdoid">ZDOID of the parent primary anchor</param>
        /// <returns>The new segment anchor, or null on failure</returns>
        public static WaterSegmentAnchor CreateSegmentAnchor(Vector3 position, int segmentIndex, ZDOID parentZdoid)
        {
            if (ZNetScene.instance == null)
            {
                FiresLogger.LogError("[WaterSegmentAnchor] Cannot create segment - ZNetScene not available");
                return null;
            }

            GameObject prefab = FindPrefab();
            if (prefab == null)
            {
                FiresLogger.LogError("[WaterSegmentAnchor] Cannot find prefab for segment anchor");
                return null;
            }

            Vector3 spawnPos = new Vector3(position.x, ANCHOR_HIDDEN_Y, position.z);

            // Set pending flag so WaterMeshCombinerAnchor.Awake() doesn't self-destruct
            // AND our own Awake doesn't see isSegment=false (not set yet)
            WaterMeshCombinerAnchor.SetNextInstantiateIsPending(true);
            s_nextInstantiateIsPending = true;
            var go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity);
            WaterMeshCombinerAnchor.SetNextInstantiateIsPending(false);
            s_nextInstantiateIsPending = false;

            // Remove the WaterMeshCombinerAnchor if it was added by the prefab
            // (segments are NOT primary anchors)
            var primaryAnchor = go.GetComponent<WaterMeshCombinerAnchor>();
            if (primaryAnchor != null)
            {
                Destroy(primaryAnchor);
            }

            // Add segment anchor component
            var segment = go.GetComponent<WaterSegmentAnchor>();
            if (segment == null)
            {
                segment = go.AddComponent<WaterSegmentAnchor>();
            }

            segment.Initialize(segmentIndex, parentZdoid);

            if (WaterMeshCombiner.s_verboseLogging)
            {
                FiresLogger.LogInfo($"[WaterSegmentAnchor] Created segment {segmentIndex} at {spawnPos} for parent {parentZdoid}");
            }

            return segment;
        }

        private static GameObject FindPrefab()
        {
            if (s_cachedPrefab != null)
                return s_cachedPrefab;

            string[] names = { "CustomRiver", "customriver" };
            foreach (var name in names)
            {
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab != null)
                {
                    s_cachedPrefab = prefab;
                    return prefab;
                }
            }

            FiresLogger.LogError("[WaterSegmentAnchor] FindPrefab: CustomRiver prefab NOT found!");
            return null;
        }

        /// <summary>
        /// Clears cached prefab. Call on world unload.
        /// </summary>
        public static void ClearPrefabCache()
        {
            s_cachedPrefab = null;
        }

        #endregion
    }
}
