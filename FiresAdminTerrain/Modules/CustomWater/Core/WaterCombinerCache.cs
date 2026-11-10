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
            // Cache v2: gelled water-volume flag. When true the
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
            // 0.2.1331 (cache v3): per disk, parallel to WaterDiskData. A float cannot hold a hash above 2^24 (by-hash removal missed),
            // and the Kind was never written, so a restored river read as a pond and terraced. Null on a v1/v2 group: its float hash
            // is rounded and its kinds are unknown; the anchor's ZDO supplies both (WaterMeshCombinerAnchor.ApplyZdoKinds).
            public int[] DiskHashes;
            public byte[] DiskKinds;
            // The anchor's va_water_saved_ticks this data was last in step with (0 = unknown): the anchor wins when its own is newer.
            public long AnchorSavedTicks;

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

        // 0.2.1349 (Fire: "every time we log back in its broken"): the group an anchor stands for is the one holding its disks; the first
        // group within 5 m in XZ could be another floor's. An anchor with no disks readable falls back to position.
        internal static WaterCombinerGroupData GetCachedDataForAnchor(Vector3 position, List<WaterDisk> anchorDisks)
        {
            if (anchorDisks == null || anchorDisks.Count == 0) return GetCachedDataForPosition(position);
            if (!_cacheLoaded || _currentCache == null) return null;
            var hashes = WaterMeshCombinerAnchor.HashesOf(anchorDisks);
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var d in anchorDisks)
            {
                minX = Mathf.Min(minX, d.Position.x - d.Radius); maxX = Mathf.Max(maxX, d.Position.x + d.Radius);
                minZ = Mathf.Min(minZ, d.Position.z - d.Radius); maxZ = Mathf.Max(maxZ, d.Position.z + d.Radius);
            }
            var lo = new Vector3(minX, 0f, minZ);
            var hi = new Vector3(maxX, 0f, maxZ);
            WaterCombinerGroupData best = null;
            int bestShared = 0;
            foreach (var group in _currentCache.Groups)
            {
                if (group == null || !group.HasValidData || !WaterMeshCombinerAnchor.OverlapXZ(lo, hi, group.BoundsMin, group.BoundsMax, WaterMeshCombinerAnchor.BindDistance)) continue;
                int count = group.WaterDiskData.Length / WaterCombinerGroupData.DISK_STRIDE, shared = 0;
                for (int i = 0; i < count; i++)
                    if (hashes.Contains(HashAt(group, i))) shared++;
                if (shared > bestShared && WaterMeshCombinerAnchor.SameBody(shared, anchorDisks.Count, count)) { best = group; bestShared = shared; }
            }
            return best;
        }

        // 0.2.1352 (Fire: "theres basically no river mesh that doesnt end up broken on relog"): true = this anchor shares under half of the group's disks, not counting disks on the
        // removed list. Binding it would shrink the group to the anchor's list when the anchor's stamp is newer.
        internal static bool AnchorTooSmallFor(WaterCombinerGroupData group, List<WaterDisk> anchorDisks, out int shared, out int live)
        {
            shared = live = 0;
            if (anchorDisks == null || anchorDisks.Count == 0 || group.WaterDiskData == null) return false;
            var hashes = WaterMeshCombinerAnchor.HashesOf(anchorDisks);
            int count = group.WaterDiskData.Length / WaterCombinerGroupData.DISK_STRIDE;
            for (int i = 0; i < count; i++)
            {
                int h = HashAt(group, i);
                if (hashes.Contains(h)) { shared++; live++; }
                else if (!WaterRemovedDisks.Contains(h)) live++;
            }
            return shared * 2 < live;
        }

        internal static int HashAt(WaterCombinerGroupData group, int i)
            => group.DiskHashes != null && i < group.DiskHashes.Length ? group.DiskHashes[i] : (int)group.WaterDiskData[i * WaterCombinerGroupData.DISK_STRIDE];

        // The group's disks as objects, for the anchor repair (the same reading as RestoreFromCache).
        internal static List<WaterDisk> DisksOf(WaterCombinerGroupData group)
        {
            int stride = WaterCombinerGroupData.DISK_STRIDE, count = group.WaterDiskData.Length / stride;
            var disks = new List<WaterDisk>(count);
            for (int i = 0; i < count; i++)
            {
                int o = i * stride;
                var data = group.WaterDiskData;
                var disk = new WaterDisk(new Vector3(data[o + 1], data[o + 2], data[o + 3]), data[o + 4], data[o + 5], (LiquidType)(int)data[o + 6], HashAt(group, i));
                disk.CustomLiquidType = (CustomLiquidType)(int)data[o + 7];
                if (group.DiskKinds != null && i < group.DiskKinds.Length) disk.Kind = KindOf(group.DiskKinds[i]);
                disks.Add(disk);
            }
            return disks;
        }

        // Anchor repair: this group now matches its new anchor; a shared disk another anchor holds leaves this group (and its live combiner).
        internal static void NoteRepaired(WaterCombinerGroupData group, long ticks, HashSet<int> sharedOut)
        {
            if (sharedOut != null && sharedOut.Count > 0)
            {
                StripDisks(group, (h, exact) => sharedOut.Contains(h));
                var live = GetInstantiatedCombinerForGroup(group.GroupId);
                if (live != null) live.DropDisksHeldElsewhere(sharedOut);
            }
            group.AnchorSavedTicks = ticks;
            _dirtyGroupIds.Add(group.GroupId);
        }

        internal static int GroupIdOf(WaterMeshCombiner combiner)
        {
            foreach (var kvp in _groupToCombiners)
                if (kvp.Value == combiner) return kvp.Key;
            return -1;
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
                groupData.DiskHashes = new int[disks.Count];   // 0.2.1331
                groupData.DiskKinds = new byte[disks.Count];
                for (int i = 0; i < disks.Count; i++)
                {
                    var disk = disks[i];
                    int offset = i * WaterCombinerGroupData.DISK_STRIDE;
                    groupData.DiskHashes[i] = disk.StableHash;
                    groupData.DiskKinds[i] = (byte)disk.Kind;
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

            if (combiner.Anchor != null) groupData.AnchorSavedTicks = combiner.Anchor.SavedTicks;   // 0.2.1331

            _dirtyGroupIds.Add(groupId);
        }

        // 0.2.1331: the anchor stamp of the cache group this combiner came from (-1 = it did not come from the cache).
        internal static long AnchorTicksOf(WaterMeshCombiner combiner)
        {
            foreach (var kvp in _groupToCombiners)
                if (kvp.Value == combiner)
                {
                    var g = _currentCache?.Groups.FirstOrDefault(x => x.GroupId == kvp.Key);
                    return g != null ? g.AnchorSavedTicks : -1L;
                }
            return -1L;
        }

        internal static void NoteAnchorSaved(WaterMeshCombiner combiner, long ticks)
        {
            foreach (var kvp in _groupToCombiners)
                if (kvp.Value == combiner)
                {
                    var g = _currentCache?.Groups.FirstOrDefault(x => x.GroupId == kvp.Key);
                    if (g != null) { g.AnchorSavedTicks = ticks; _dirtyGroupIds.Add(kvp.Key); }
                    return;
                }
        }

        // "disk kinds restored: rivers N, ponds M, unknown U (source ...)", Debug, at most KindLinesPerLoad a load.
        private const int KindLinesPerLoad = 40;
        private static int s_kindLines;
        private static bool s_kindInfoSaid;
        internal static void NoteKinds(IReadOnlyList<WaterDisk> disks, string source)
        {
            if (!s_kindInfoSaid) { s_kindInfoSaid = true; FiresLogger.LogInfo("[WaterCombinerCache] restored water keeps its river / pond kinds and exact hashes (cache v3, or the anchor's ZDO) (0.2.1331)"); }
            if (!FiresLogger.DebugEnabled || disks == null || s_kindLines >= KindLinesPerLoad) return;
            s_kindLines++;
            int rivers = 0, ponds = 0, unknown = 0;
            foreach (var d in disks)
            {
                if (d == null) continue;
                if (d.Kind == WaterDiskKind.River) rivers++; else if (d.Kind == WaterDiskKind.Pond) ponds++; else unknown++;
            }
            FiresLogger.LogDebug($"[WaterCombinerCache] disk kinds restored: rivers {rivers}, ponds {ponds}, unknown {unknown} (source {source})");
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

        // 0.2.1336: takes a removed disk out of the groups no live combiner stands for. A v1/v2 group's hash is float-rounded, so that form matches too.
        internal static bool PurgeRemovedDisk(int hash)
        {
            if (!_cacheLoaded || _currentCache == null) return false;
            bool any = false;
            int rounded = (int)(float)hash;
            for (int g = _currentCache.Groups.Count - 1; g >= 0; g--)
            {
                var group = _currentCache.Groups[g];
                if (group == null || GetInstantiatedCombinerForGroup(group.GroupId) != null) continue;
                if (!StripDisks(group, (h, exact) => exact ? h == hash : h == rounded)) continue;
                any = true;
                if (group.HasValidData) _dirtyGroupIds.Add(group.GroupId);
                else { _groupToCombiners.Remove(group.GroupId); _currentCache.Groups.RemoveAt(g); _structureDirty = true; }
            }
            return any;
        }

        // Removes the disks `removed` names (hash, is it exact). Non-gelled heights go too (they still draw the disk), so the restore builds from the disks.
        private static bool StripDisks(WaterCombinerGroupData group, Func<int, bool, bool> removed)
        {
            var data = group.WaterDiskData;
            if (data == null) return false;
            int stride = WaterCombinerGroupData.DISK_STRIDE, count = data.Length / stride, kept = 0;
            var keep = new bool[count];
            for (int i = 0; i < count; i++)
            {
                bool exact = group.DiskHashes != null && i < group.DiskHashes.Length;
                keep[i] = !removed(exact ? group.DiskHashes[i] : (int)data[i * stride], exact);
                if (keep[i]) kept++;
            }
            if (kept == count) return false;
            var newData = new float[kept * stride];
            var newHashes = group.DiskHashes != null ? new int[kept] : null;
            var newKinds = group.DiskKinds != null ? new byte[kept] : null;
            for (int i = 0, n = 0; i < count; i++)
            {
                if (!keep[i]) continue;
                Array.Copy(data, i * stride, newData, n * stride, stride);
                if (newHashes != null && i < group.DiskHashes.Length) newHashes[n] = group.DiskHashes[i];
                if (newKinds != null && i < group.DiskKinds.Length) newKinds[n] = group.DiskKinds[i];
                n++;
            }
            group.WaterDiskData = kept > 0 ? newData : null;
            group.DiskHashes = newHashes;
            group.DiskKinds = newKinds;
            if (!group.IsGelled) { group.SurfaceHeights = null; group.TerrainHeights = null; }
            return true;
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
            s_kindLines = 0;
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
        // Skipped-empty count for the per-load summary line (TakeSkippedEmpty, read by the two bulk restores).
        private static int s_skippedEmpty;

        internal static int TakeSkippedEmpty() { int n = s_skippedEmpty; s_skippedEmpty = 0; return n; }

        // A cell holds water when its cached surface stands MinWaterDepth or more above its cached terrain (as the combiner's
        // own depth grid is built from them).
        internal static bool HasWetCell(WaterCombinerGroupData g)
        {
            var s = g.SurfaceHeights; var t = g.TerrainHeights;
            if (s == null || t == null) return true;   // unknown: restore as before
            int n = Mathf.Min(s.Length, t.Length);
            for (int i = 0; i < n; i++)
                if (s[i] - t[i] >= WaterMeshCombiner.MinWaterDepth) return true;
            return false;
        }

        public static WaterMeshCombiner RestoreFromCache(WaterCombinerGroupData groupData, WaterMeshCombinerAnchor anchor)
        {
            if (groupData == null || !groupData.HasValidData)
                return null;
            // 0.2.1336: the removed list can arrive before this restore. The caller is walking Groups, so an emptied group stays in the list, without data.
            // (Exact hashes only here: the list is per hash, a v2 group's rounded one is caught by PurgeRemovedDisk when the list arrives.)
            if (StripDisks(groupData, (h, exact) => exact && WaterRemovedDisks.Contains(h)))
            {
                _dirtyGroupIds.Add(groupData.GroupId);
                FiresLogger.LogDebug($"[WaterCombinerCache] group {groupData.GroupId} at {groupData.CenterPosition}: removed disk(s) dropped before its restore");
                if (!groupData.HasValidData) return null;
            }

            // Skip empty cached groups on load (most restored combiners can hold 0/1024 wet cells). Only the bulk world-load restore (no anchor); a placed-water anchor always
            // gets its combiner. Nothing is removed from the cache file or the save.
            if (false)   // disabled: skipping hid the dry-pond symptom; the reseat fix replaced it
            {
                s_skippedEmpty++;
                FiresLogger.LogDebug($"[FiresWaterCache] skipped empty cached group {groupData.GroupId} at ({groupData.CenterPosition.x:F0},{groupData.CenterPosition.y:F1},{groupData.CenterPosition.z:F0}) (no cell >= {WaterMeshCombiner.MinWaterDepth} m)");
                return null;
            }

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
            // BEFORE any restore/regen so gelled volumes route to
            // the grid-authoritative rebuild, never disk recomputation.
            combiner.IsGelled = groupData.IsGelled;
            if (groupData.IsGelled && ++s_gelledRestoreLogs <= 8) FiresLogger.LogDebug($"[WaterCombinerCache] gelled volume restored from cache at {groupData.CenterPosition}: routed to the grid rebuild (the save snapshot keeps the flag since 0.2.1332)");

            // Restore water disks from cache
            if (groupData.WaterDiskData != null && groupData.WaterDiskData.Length >= WaterCombinerGroupData.DISK_STRIDE)
            {
                int diskCount = groupData.WaterDiskData.Length / WaterCombinerGroupData.DISK_STRIDE;
                var disks = new List<WaterDisk>(diskCount);

                for (int i = 0; i < diskCount; i++)
                {
                    int offset = i * WaterCombinerGroupData.DISK_STRIDE;
                    bool v3 = groupData.DiskHashes != null && i < groupData.DiskHashes.Length;   // 0.2.1331: exact; a v2 group's float is rounded
                    int stableHash = v3 ? groupData.DiskHashes[i] : (int)groupData.WaterDiskData[offset];
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
                    if (groupData.DiskKinds != null && i < groupData.DiskKinds.Length) disk.Kind = KindOf(groupData.DiskKinds[i]);   // 0.2.1331
                    disks.Add(disk);
                }

                combiner.AddWaterDisksBatch(disks, skipRegeneration: true);
                if (groupData.DiskKinds != null) NoteKinds(disks, "cache v3");   // v1/v2: kinds come from the ZDO when the anchor links
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
                // The combiner's field default
                // _needsRegeneration = true would make Update() re-sample terrain 0.1 s after this restore, and far from the player
                // unloaded ground reads dry. Under "Reseat waits for the ground" the restored grid stands; the reseat decides.
                combiner.KeepRestoredGrid();   // always
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
            // Caches never live under BepInEx config: BepInEx/cache/FiresAdminTerrain/WaterCache (moved once in Awake)
            return Path.Combine(global::VerdantsAscent.FiresCachePaths.For("FiresAdminTerrain", "WaterCache"), $"{worldName}_water.bin");
        }

        private static int s_gelledRestoreLogs;   // 0.2.1332
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
                    IsGelled = group.IsGelled,   // 0.2.1332: the async save snapshot dropped it, so a gelled volume reloaded as not gelled
                    AnchorSavedTicks = group.AnchorSavedTicks,   // 0.2.1331
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
                if (group.DiskHashes != null) copy.DiskHashes = (int[])group.DiskHashes.Clone();   // 0.2.1331
                if (group.DiskKinds != null) copy.DiskKinds = (byte[])group.DiskKinds.Clone();

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
                    writer.Write(CacheVersion);
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

                        // v3 (0.2.1331). A group still in v2 state (no anchor linked yet) writes count 0: still untrusted when read back.
                        int kindCount = group.DiskHashes != null && group.DiskKinds != null && group.DiskHashes.Length == group.DiskKinds.Length ? group.DiskHashes.Length : 0;
                        writer.Write(kindCount);
                        for (int k = 0; k < kindCount; k++) { writer.Write(group.DiskHashes[k]); writer.Write(group.DiskKinds[k]); }
                        writer.Write(group.AnchorSavedTicks);
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

        // Cache v3 (0.2.1331): each group ends with its disks' exact hashes and kinds and the anchor stamp. v1/v2 stay readable.
        private const int CacheVersion = 3;
        // Size limits for a file read back: a bad length refuses the whole file (the format is sequential), and the anchors restore from their ZDOs.
        private const int MaxArrayLength = 16 * 1024 * 1024;
        private const int MinGroupBytes = 80;   // id + 3 vectors + grid + flags + properties + 3 array counts + ticks, at the very least

        private static int CheckedCount(BinaryReader reader, int elementBytes, string what)
        {
            int count = reader.ReadInt32();
            long left = reader.BaseStream.Length - reader.BaseStream.Position;
            if (count < 0 || count > MaxArrayLength || count * (long)elementBytes > left)
                throw new InvalidDataException($"{what} count {count} does not fit ({left} bytes left)");
            return count;
        }

        internal static WaterDiskKind KindOf(byte b) => b <= (byte)WaterDiskKind.Pond ? (WaterDiskKind)b : WaterDiskKind.Unknown;

        private static WorldWaterCacheData LoadCacheFromFile(string path)
        {
            var cache = new WorldWaterCacheData();
            cache.Groups = new List<WaterCombinerGroupData>();

            using (var stream = new FileStream(path, FileMode.Open))
            using (var reader = new BinaryReader(stream))
            {
                int version = reader.ReadInt32();
                if (version < 1 || version > CacheVersion)
                {
                    FiresLogger.LogWarning($"[WaterCombinerCache] Unknown version {version}");
                    return cache;
                }

                cache.WorldName = reader.ReadString();
                cache.WorldSeed = reader.ReadInt64();

                int groupCount = CheckedCount(reader, MinGroupBytes, "group");
                for (int i = 0; i < groupCount; i++)
                {
                    var group = new WaterCombinerGroupData();
                    group.GroupId = reader.ReadInt32();

                    group.CenterPosition = ReadVector3(reader);
                    group.BoundsMin = ReadVector3(reader);
                    group.BoundsMax = ReadVector3(reader);

                    group.GridWidth = reader.ReadInt32();
                    group.GridHeight = reader.ReadInt32();
                    if (group.GridWidth < 0 || group.GridHeight < 0 || group.GridWidth * (long)group.GridHeight > MaxArrayLength)
                        throw new InvalidDataException($"group {group.GroupId} grid {group.GridWidth} x {group.GridHeight}");
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

                    int stride = WaterCombinerGroupData.DISK_STRIDE;
                    int disksHere = group.WaterDiskData != null ? group.WaterDiskData.Length / stride : 0;
                    if (group.WaterDiskData != null && group.WaterDiskData.Length % stride != 0)
                        throw new InvalidDataException($"group {group.GroupId} disk data length {group.WaterDiskData.Length}");
                    if (version >= 3)
                    {
                        int kinds = CheckedCount(reader, 5, "disk kind");
                        if (kinds != 0 && kinds != disksHere) throw new InvalidDataException($"group {group.GroupId}: {kinds} kinds for {disksHere} disks");
                        if (kinds > 0)
                        {
                            group.DiskHashes = new int[kinds];
                            group.DiskKinds = new byte[kinds];
                            for (int k = 0; k < kinds; k++) { group.DiskHashes[k] = reader.ReadInt32(); group.DiskKinds[k] = (byte)KindOf(reader.ReadByte()); }
                        }
                        group.AnchorSavedTicks = reader.ReadInt64();
                    }

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
            int count = CheckedCount(reader, 4, "float array");   // 0.2.1331: a bad length refuses the file, never a huge allocation
            if (count == 0) return null;
            var arr = new float[count];
            for (int i = 0; i < count; i++)
                arr[i] = reader.ReadSingle();
            return arr;
        }

        #endregion
    }
}
