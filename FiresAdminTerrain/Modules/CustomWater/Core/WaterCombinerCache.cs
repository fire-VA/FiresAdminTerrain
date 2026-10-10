using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using VerdantsAscent.Async;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// File-based cache for WaterMeshCombiner data.
    /// Mirrors DirtFloorCombinerCache - stores water disk and grid data locally so
    /// clients can restore water without relying on ZDO byte array sync (which is
    /// unreliable for large payloads).
    ///
    /// KEY INSIGHT (from terrain system):
    ///   Terrain works on clients because TerrainCombinerAnchor.Start() checks the
    ///   file cache FIRST and only falls back to ZDO restoration if no cache exists.
    ///   The file cache is written on the peer that generates the data (server) AND
    ///   on any peer that successfully restores from ZDO. So after the first
    ///   successful ZDO restore, subsequent loads use the fast cache path.
    ///
    /// For water, the server generates combiners and saves them to ZDO anchors.
    /// On the server, this cache is written immediately after generation.
    /// On clients, ZDO restoration populates the cache for future loads.
    /// </summary>
    public static class WaterCombinerCache
    {
        #region Cache Data Structures

        /// <summary>
        /// Cached data for a single water combiner.
        /// </summary>
        [Serializable]
        public class WaterCombinerGroupData
        {
            public int GroupId;
            public Vector3 CenterPosition;
            public Vector3 BoundsMin;
            public Vector3 BoundsMax;

            // Grid configuration
            public int GridWidth;
            public int GridHeight;
            public float CellSize;
            public float SettledWaterLevel;
            public bool IsPondMode;
            // Round-56 (cache v2): gelled water-volume flag. When true the
            // SurfaceHeights grid is authoritative and the combiner must
            // never recompute it from WaterDiskData.
            public bool IsGelled;

            // Water properties
            public int LiquidType;
            public int CustomLiquidType;
            public float WaveIntensity;

            // Surface and terrain heights (the combined mesh state)
            public float[] SurfaceHeights;
            public float[] TerrainHeights;

            // Water disks (source of truth for regeneration)
            public float[] WaterDiskData; // Flattened: [hash, posX, posY, posZ, radius, maxDepth, liquidType, customLiquidType] * N
            public const int DISK_STRIDE = 8;

            public long LastModifiedTicks;

            // Runtime-only (not serialized)
            [NonSerialized] public bool IsRestored;

            /// <summary>
            /// Returns true if this cache has valid data for restoration.
            /// </summary>
            public bool HasValidData =>
                WaterDiskData != null && WaterDiskData.Length >= DISK_STRIDE;
        }

        /// <summary>
        /// Full cache for a world.
        /// </summary>
        [Serializable]
        public class WorldWaterCacheData
        {
            public string WorldName;
            public long WorldSeed;
            public List<WaterCombinerGroupData> Groups = new List<WaterCombinerGroupData>();
        }

        #endregion

        #region Static State

        private static WorldWaterCacheData _currentCache;
        private static string _currentWorldName;
        private static bool _cacheLoaded = false;
        private static float _lastSaveTime = 0f;
        private const float MIN_SAVE_INTERVAL = 60f;

        /// <summary>
        /// Per-group dirty tracking. Only groups in this set have changed since
        /// the last save. This prevents rewriting the entire cache file (potentially
        /// hundreds of groups) when only one combiner changes.
        /// 
        /// The full file still gets rewritten on save (binary format requires it),
        /// but this set controls:
        ///   1. Whether a save is needed at all (Count > 0)
        ///   2. Diagnostic visibility into what changed
        ///   3. Future incremental save optimization (append-only journal)
        /// </summary>
        private static readonly HashSet<int> _dirtyGroupIds = new HashSet<int>();

        /// <summary>
        /// Set when a structural change happened (group added/removed) that requires
        /// a full save regardless of per-group dirty state. Cleared after save.
        /// </summary>
        private static bool _structureDirty = false;

        // Track which groups have been instantiated this session
        private static Dictionary<int, WaterMeshCombiner> _groupToCombiners = new Dictionary<int, WaterMeshCombiner>();
        private static int _nextGroupId = 1;

        // Async save state
        private static volatile bool _isSaving = false;
        private static readonly object _saveLock = new object();

        /// <summary>
        /// True if any groups have been modified since the last save.
        /// </summary>
        public static bool HasDirtyGroups => _dirtyGroupIds.Count > 0 || _structureDirty;

        /// <summary>
        /// Number of individual groups that have unsaved changes.
        /// </summary>
        public static int DirtyGroupCount => _dirtyGroupIds.Count;

        /// <summary>
        /// Returns the current cache data, or null if not loaded.
        /// Used by WaterPersistence.LoadWaterData() for proactive restoration.
        /// </summary>
        public static WorldWaterCacheData GetCurrentCache() => _cacheLoaded ? _currentCache : null;

        #endregion

        #region Public API

        /// <summary>
        /// Initializes the cache for a world. Called on world load.
        /// </summary>
        public static void InitializeForWorld(string worldName, long worldSeed)
        {
            if (_currentWorldName == worldName && _cacheLoaded)
                return;

            _currentWorldName = worldName;
            _cacheLoaded = false;
            _dirtyGroupIds.Clear();
            _structureDirty = false;
            _groupToCombiners.Clear();
            _nextGroupId = 1;

            string cachePath = GetCachePath(worldName);
            if (File.Exists(cachePath))
            {
                try
                {
                    _currentCache = LoadCacheFromFile(cachePath);
                    foreach (var group in _currentCache.Groups)
                    {
                        if (group.GroupId >= _nextGroupId)
                            _nextGroupId = group.GroupId + 1;
                    }
                    _cacheLoaded = true;
                    if (FiresLogger.VerboseEnabled) FiresLogger.LogVerbose($"[WaterCombinerCache] Loaded cache for {worldName}: {_currentCache.Groups.Count} groups");
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[WaterCombinerCache] Failed to load cache: {ex.Message}");
                    _currentCache = new WorldWaterCacheData { WorldName = worldName, WorldSeed = worldSeed };
                }
            }
            else
            {
                _currentCache = new WorldWaterCacheData { WorldName = worldName, WorldSeed = worldSeed };
            }

            _cacheLoaded = true;
        }

        /// <summary>
        /// Gets cached data for an anchor at a given position.
        /// Used for cache-first loading - check cache before ZDO restoration.
        /// </summary>
        public static WaterCombinerGroupData GetCachedDataForPosition(Vector3 position, float searchRadius = 5f)
        {
            if (!_cacheLoaded || _currentCache == null)
                return null;

            foreach (var group in _currentCache.Groups)
            {
                float dx = position.x - group.CenterPosition.x;
                float dz = position.z - group.CenterPosition.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist < searchRadius)
                    return group;
            }

            return null;
        }

        /// <summary>
        /// Gets the combiner for a cached group if it's already instantiated.
        /// </summary>
        public static WaterMeshCombiner GetInstantiatedCombinerForGroup(int groupId)
        {
            if (_groupToCombiners.TryGetValue(groupId, out var combiner) && combiner != null)
                return combiner;
            return null;
        }

        /// <summary>
        /// Marks a combiner as instantiated for a cache group.
        /// </summary>
        public static void MarkCombinerInstantiated(int groupId, WaterMeshCombiner combiner)
        {
            if (combiner == null) return;
            _groupToCombiners[groupId] = combiner;
        }

        /// <summary>
        /// Registers or updates a combiner in the cache.
        /// Called after mesh regeneration or disk changes.
        /// </summary>
        public static void UpdateCombinerCache(WaterMeshCombiner combiner)
        {
            if (!_cacheLoaded || _currentCache == null || combiner == null)
                return;

            // Find existing group for this combiner
            int groupId = -1;
            foreach (var kvp in _groupToCombiners)
            {
                if (kvp.Value == combiner)
                {
                    groupId = kvp.Key;
                    break;
                }
            }

            WaterCombinerGroupData groupData;

            if (groupId < 0)
            {
                // New combiner - assign a group
                groupId = _nextGroupId++;
                _groupToCombiners[groupId] = combiner;
                groupData = new WaterCombinerGroupData { GroupId = groupId };
                _currentCache.Groups.Add(groupData);
            }
            else
            {
                groupData = _currentCache.Groups.FirstOrDefault(g => g.GroupId == groupId);
                if (groupData == null)
                {
                    groupData = new WaterCombinerGroupData { GroupId = groupId };
                    _currentCache.Groups.Add(groupData);
                }
            }

            // Update group data from combiner state
            groupData.CenterPosition = combiner.transform.position;
            var bounds = combiner.GetCombinedBounds();
            groupData.BoundsMin = bounds.min;
            groupData.BoundsMax = bounds.max;
            groupData.GridWidth = combiner.GridWidth;
            groupData.GridHeight = combiner.GridHeight;
            groupData.CellSize = combiner.CellSize;
            groupData.SettledWaterLevel = combiner.SettledWaterLevel;
            groupData.IsPondMode = combiner.IsPondMode;
            groupData.IsGelled = combiner.IsGelled;
            groupData.LiquidType = (int)combiner.LiquidType;
            groupData.CustomLiquidType = (int)combiner.CustomLiquidType;
            groupData.WaveIntensity = combiner.WaveIntensity;
            groupData.LastModifiedTicks = DateTime.UtcNow.Ticks;

            // Cache surface and terrain heights
            var surfaceHeights = combiner.GetSurfaceHeights();
            if (surfaceHeights != null && surfaceHeights.Length > 0)
                groupData.SurfaceHeights = (float[])surfaceHeights.Clone();

            var terrainHeights = combiner.GetTerrainHeights();
            if (terrainHeights != null && terrainHeights.Length > 0)
                groupData.TerrainHeights = (float[])terrainHeights.Clone();

            // Cache water disks
            var disks = combiner.WaterDisks;
            if (disks != null && disks.Count > 0)
            {
                groupData.WaterDiskData = new float[disks.Count * WaterCombinerGroupData.DISK_STRIDE];
                for (int i = 0; i < disks.Count; i++)
                {
                    var disk = disks[i];
                    int offset = i * WaterCombinerGroupData.DISK_STRIDE;
                    groupData.WaterDiskData[offset] = disk.StableHash;
                    groupData.WaterDiskData[offset + 1] = disk.Position.x;
                    groupData.WaterDiskData[offset + 2] = disk.Position.y;
                    groupData.WaterDiskData[offset + 3] = disk.Position.z;
                    groupData.WaterDiskData[offset + 4] = disk.Radius;
                    groupData.WaterDiskData[offset + 5] = disk.MaxDepth;
                    groupData.WaterDiskData[offset + 6] = (int)disk.LiquidType;
                    groupData.WaterDiskData[offset + 7] = (int)disk.CustomLiquidType;
                }
            }

            _dirtyGroupIds.Add(groupId);
        }

        /// <summary>
        /// Called when a combiner is destroyed (zone unload).
        /// Keeps cache data for recreation.
        /// </summary>
        public static void OnCombinerDestroyed(WaterMeshCombiner combiner)
        {
            if (combiner == null) return;

            int groupIdToRemove = -1;
            foreach (var kvp in _groupToCombiners)
            {
                if (kvp.Value == combiner)
                {
                    groupIdToRemove = kvp.Key;
                    break;
                }
            }

            if (groupIdToRemove >= 0)
            {
                // Update cache with final state before removing runtime reference.
                // Do NOT trigger an immediate save - zone unloads are frequent and
                // saving the full cache for each one is wasteful. The periodic
                // Update() timer will batch-save all dirty groups together.
                UpdateCombinerCache(combiner);
                _groupToCombiners.Remove(groupIdToRemove);
            }
        }

        /// <summary>
        /// Called when a combiner is permanently deleted.
        /// Removes from both runtime tracking AND persistent cache.
        /// </summary>
        public static void OnCombinerPermanentlyDeleted(WaterMeshCombiner combiner)
        {
            if (combiner == null) return;

            int groupIdToRemove = -1;
            foreach (var kvp in _groupToCombiners)
            {
                if (kvp.Value == combiner)
                {
                    groupIdToRemove = kvp.Key;
                    break;
                }
            }

            if (groupIdToRemove >= 0)
            {
                _groupToCombiners.Remove(groupIdToRemove);
                _dirtyGroupIds.Remove(groupIdToRemove);

                if (_currentCache != null)
                {
                    _currentCache.Groups.RemoveAll(g => g.GroupId == groupIdToRemove);
                    _structureDirty = true;
                }
            }
        }

        /// <summary>
        /// Clears all cached water data. Called before auto-generation clear.
        /// </summary>
        public static void ClearCache()
        {
            if (_currentCache != null)
            {
                _currentCache.Groups.Clear();
            }

            _groupToCombiners.Clear();
            _dirtyGroupIds.Clear();
            _nextGroupId = 1;
            _structureDirty = true;
            SaveCache();
        }

        /// <summary>
        /// Forces immediate cache save.
        /// Acquires <see cref="_saveLock"/> so it cannot collide with a
        /// background <see cref="SaveCacheAsync"/> that is still writing.
        /// </summary>
        public static void SaveCache()
        {
            if (_currentCache == null || string.IsNullOrEmpty(_currentWorldName))
                return;

            try
            {
                string cachePath = GetCachePath(_currentWorldName);
                string directory = Path.GetDirectoryName(cachePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                lock (_saveLock)
                {
                    SaveCacheToFile(cachePath, _currentCache);
                }
                _dirtyGroupIds.Clear();
                _structureDirty = false;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterCombinerCache] Failed to save cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves cache on a background thread.
        /// </summary>
        public static void SaveCacheAsync()
        {
            if (_currentCache == null || string.IsNullOrEmpty(_currentWorldName)) return;
            if (!HasDirtyGroups) return;
            if (_isSaving) return;

            int dirtyCount = _dirtyGroupIds.Count;
            WorldWaterCacheData snapshot;
            try
            {
                snapshot = SnapshotCacheData(_currentCache);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterCombinerCache] Snapshot failed, sync save: {ex.Message}");
                SaveCache();
                return;
            }

            string cachePath = GetCachePath(_currentWorldName);
            _isSaving = true;
            _dirtyGroupIds.Clear();
            _structureDirty = false;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (_saveLock)
                    {
                        string directory = Path.GetDirectoryName(cachePath);
                        if (!Directory.Exists(directory))
                            Directory.CreateDirectory(directory);
                        SaveCacheToFile(cachePath, snapshot);
                    }
                }
                catch (Exception ex)
                {
                    MainThreadDispatcher.Enqueue(() =>
                        FiresLogger.LogWarning($"[WaterCombinerCache] Async save failed: {ex.Message}"));
                }
                finally
                {
                    _isSaving = false;
                }
            });
        }

        /// <summary>
        /// Periodically saves dirty cache. Called from WaterMeshCombiner.Update or a manager.
        /// </summary>
        public static void Update()
        {
            if (!HasDirtyGroups || _currentCache == null) return;

            float timeSinceLastSave = Time.time - _lastSaveTime;
            if (timeSinceLastSave >= MIN_SAVE_INTERVAL)
            {
                SaveCacheAsync();
                _lastSaveTime = Time.time;
            }
        }

        /// <summary>
        /// Called when world is unloaded.
        /// </summary>
        public static void OnWorldUnload()
        {
            if (HasDirtyGroups)
                SaveCache();

            _currentCache = null;
            _currentWorldName = null;
            _cacheLoaded = false;
            _dirtyGroupIds.Clear();
            _structureDirty = false;
            _groupToCombiners.Clear();
        }

        #endregion

        #region Restoration

        /// <summary>
        /// Restores a WaterMeshCombiner from cached data.
        /// Returns the restored combiner, or null if cache data is invalid.
        /// </summary>
        public static WaterMeshCombiner RestoreFromCache(WaterCombinerGroupData groupData, WaterMeshCombinerAnchor anchor)
        {
            if (groupData == null || !groupData.HasValidData)
                return null;

            // Check if already instantiated
            var existing = GetInstantiatedCombinerForGroup(groupData.GroupId);
            if (existing != null)
            {
                if (anchor != null)
                {
                    existing.Anchor = anchor;
                }
                return existing;
            }

            // Create combiner GameObject
            var combinerGO = new GameObject($"WaterMeshCombiner_Cached_{groupData.GroupId}");
            combinerGO.transform.position = groupData.CenterPosition;

            var combiner = combinerGO.AddComponent<WaterMeshCombiner>();
            combiner.SetRestoredFromPersistence(true);

            if (anchor != null)
            {
                combiner.Anchor = anchor;
            }

            // Restore properties
            combiner.LiquidType = (LiquidType)groupData.LiquidType;
            combiner.CustomLiquidType = (CustomLiquidType)groupData.CustomLiquidType;
            combiner.WaveIntensity = groupData.WaveIntensity;
            // Round-56: BEFORE any restore/regen so gelled volumes route to
            // the grid-authoritative rebuild, never disk recomputation.
            combiner.IsGelled = groupData.IsGelled;

            // Restore water disks from cache
            if (groupData.WaterDiskData != null && groupData.WaterDiskData.Length >= WaterCombinerGroupData.DISK_STRIDE)
            {
                int diskCount = groupData.WaterDiskData.Length / WaterCombinerGroupData.DISK_STRIDE;
                var disks = new List<WaterDisk>(diskCount);

                for (int i = 0; i < diskCount; i++)
                {
                    int offset = i * WaterCombinerGroupData.DISK_STRIDE;
                    int stableHash = (int)groupData.WaterDiskData[offset];
                    Vector3 pos = new Vector3(
                        groupData.WaterDiskData[offset + 1],
                        groupData.WaterDiskData[offset + 2],
                        groupData.WaterDiskData[offset + 3]
                    );
                    float radius = groupData.WaterDiskData[offset + 4];
                    float maxDepth = groupData.WaterDiskData[offset + 5];
                    LiquidType lt = (LiquidType)(int)groupData.WaterDiskData[offset + 6];
                    CustomLiquidType clt = (CustomLiquidType)(int)groupData.WaterDiskData[offset + 7];

                    var disk = new WaterDisk(pos, radius, maxDepth, lt, stableHash);
                    disk.CustomLiquidType = clt;
                    disks.Add(disk);
                }

                combiner.AddWaterDisksBatch(disks, skipRegeneration: true);
            }

            // Restore grid configuration
            if (groupData.GridWidth > 0 && groupData.GridHeight > 0)
            {
                combiner.SetGridConfiguration(
                    groupData.GridWidth, groupData.GridHeight,
                    groupData.CellSize, groupData.SettledWaterLevel, groupData.IsPondMode);
            }

            // Restore surface heights
            if (groupData.SurfaceHeights != null && groupData.SurfaceHeights.Length > 0)
                combiner.SetSurfaceHeights((float[])groupData.SurfaceHeights.Clone());

            // Restore terrain heights
            if (groupData.TerrainHeights != null && groupData.TerrainHeights.Length > 0)
                combiner.SetTerrainHeights((float[])groupData.TerrainHeights.Clone());

            // Restore bounds
            combiner.SetBoundsData(groupData.BoundsMin, groupData.BoundsMax);

            // Decide restoration path
            bool hasGridData = groupData.SurfaceHeights != null && groupData.SurfaceHeights.Length > 0;

            if (hasGridData)
            {
                combiner.RestoreFromAnchorData();
            }
            else if (combiner.WaterDisks.Count > 0)
            {
                combiner.ForceRegenerate();
            }

            MarkCombinerInstantiated(groupData.GroupId, combiner);

            if (FiresLogger.VerboseEnabled) FiresLogger.LogVerbose($"[WaterCombinerCache] Restored combiner from cache at {groupData.CenterPosition} ({combiner.WaterDisks.Count} disks)");

            return combiner;
        }

        #endregion

        #region Private Helpers

        private static string GetCachePath(string worldName)
        {
            string configPath = BepInEx.Paths.ConfigPath;
            return Path.Combine(configPath, "VerdantsAscent", "WaterCache", $"{worldName}_water.bin");
        }

        private static WorldWaterCacheData SnapshotCacheData(WorldWaterCacheData source)
        {
            var snapshot = new WorldWaterCacheData
            {
                WorldName = source.WorldName,
                WorldSeed = source.WorldSeed,
                Groups = new List<WaterCombinerGroupData>(source.Groups.Count)
            };

            foreach (var group in source.Groups)
            {
                var copy = new WaterCombinerGroupData
                {
                    GroupId = group.GroupId,
                    CenterPosition = group.CenterPosition,
                    BoundsMin = group.BoundsMin,
                    BoundsMax = group.BoundsMax,
                    GridWidth = group.GridWidth,
                    GridHeight = group.GridHeight,
                    CellSize = group.CellSize,
                    SettledWaterLevel = group.SettledWaterLevel,
                    IsPondMode = group.IsPondMode,
                    LiquidType = group.LiquidType,
                    CustomLiquidType = group.CustomLiquidType,
                    WaveIntensity = group.WaveIntensity,
                    LastModifiedTicks = group.LastModifiedTicks,
                };

                if (group.SurfaceHeights != null)
                    copy.SurfaceHeights = (float[])group.SurfaceHeights.Clone();
                if (group.TerrainHeights != null)
                    copy.TerrainHeights = (float[])group.TerrainHeights.Clone();
                if (group.WaterDiskData != null)
                    copy.WaterDiskData = (float[])group.WaterDiskData.Clone();

                snapshot.Groups.Add(copy);
            }

            return snapshot;
        }

        #endregion

        #region Binary Serialization

        private static void SaveCacheToFile(string path, WorldWaterCacheData cache)
        {
            // Write to a temp file first, then atomically rename.
            // This prevents sharing violations when a background SaveCacheAsync
            // and a synchronous SaveCache overlap, and avoids leaving a
            // half-written file if the process crashes mid-write.
            string tmpPath = path + ".tmp";
            try
            {
                using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(2); // Version (v2: + per-group IsGelled)
                    writer.Write(cache.WorldName ?? "");
                    writer.Write(cache.WorldSeed);

                    writer.Write(cache.Groups.Count);
                    foreach (var group in cache.Groups)
                    {
                        writer.Write(group.GroupId);

                        // Position / bounds
                        WriteVector3(writer, group.CenterPosition);
                        WriteVector3(writer, group.BoundsMin);
                        WriteVector3(writer, group.BoundsMax);

                        // Grid config
                        writer.Write(group.GridWidth);
                        writer.Write(group.GridHeight);
                        writer.Write(group.CellSize);
                        writer.Write(group.SettledWaterLevel);
                        writer.Write(group.IsPondMode);
                        writer.Write(group.IsGelled); // v2

                        // Water properties
                        writer.Write(group.LiquidType);
                        writer.Write(group.CustomLiquidType);
                        writer.Write(group.WaveIntensity);

                        // Surface heights
                        WriteFloatArray(writer, group.SurfaceHeights);

                        // Terrain heights
                        WriteFloatArray(writer, group.TerrainHeights);

                        // Water disk data
                        WriteFloatArray(writer, group.WaterDiskData);

                        writer.Write(group.LastModifiedTicks);
                    }
                }

                // Atomic rename: delete old file, move temp into place
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmpPath, path);
            }
            catch
            {
                // Clean up temp file on failure
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        private static WorldWaterCacheData LoadCacheFromFile(string path)
        {
            var cache = new WorldWaterCacheData();
            cache.Groups = new List<WaterCombinerGroupData>();

            using (var stream = new FileStream(path, FileMode.Open))
            using (var reader = new BinaryReader(stream))
            {
                int version = reader.ReadInt32();
                if (version < 1 || version > 2)
                {
                    FiresLogger.LogWarning($"[WaterCombinerCache] Unknown version {version}");
                    return cache;
                }

                cache.WorldName = reader.ReadString();
                cache.WorldSeed = reader.ReadInt64();

                int groupCount = reader.ReadInt32();
                for (int i = 0; i < groupCount; i++)
                {
                    var group = new WaterCombinerGroupData();
                    group.GroupId = reader.ReadInt32();

                    group.CenterPosition = ReadVector3(reader);
                    group.BoundsMin = ReadVector3(reader);
                    group.BoundsMax = ReadVector3(reader);

                    group.GridWidth = reader.ReadInt32();
                    group.GridHeight = reader.ReadInt32();
                    group.CellSize = reader.ReadSingle();
                    group.SettledWaterLevel = reader.ReadSingle();
                    group.IsPondMode = reader.ReadBoolean();
                    group.IsGelled = version >= 2 && reader.ReadBoolean();

                    group.LiquidType = reader.ReadInt32();
                    group.CustomLiquidType = reader.ReadInt32();
                    group.WaveIntensity = reader.ReadSingle();

                    group.SurfaceHeights = ReadFloatArray(reader);
                    group.TerrainHeights = ReadFloatArray(reader);
                    group.WaterDiskData = ReadFloatArray(reader);

                    group.LastModifiedTicks = reader.ReadInt64();

                    cache.Groups.Add(group);
                }
            }

            return cache;
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.x);
            writer.Write(v.y);
            writer.Write(v.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static void WriteFloatArray(BinaryWriter writer, float[] arr)
        {
            int count = arr?.Length ?? 0;
            writer.Write(count);
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                    writer.Write(arr[i]);
            }
        }

        private static float[] ReadFloatArray(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count <= 0) return null;
            var arr = new float[count];
            for (int i = 0; i < count; i++)
                arr[i] = reader.ReadSingle();
            return arr;
        }

        #endregion
    }
}
