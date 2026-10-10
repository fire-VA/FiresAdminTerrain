using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Samples the REAL final terrain heights from WorldGenerator.GetHeight() into a
    /// grid that can be used for depression analysis on a background thread.
    /// 
    /// WHY NOT JUST READ BC'S HEIGHTMAP PNG?
    /// BC's ImageMapFloat.Map[] is only ONE input to the final terrain. The actual
    /// terrain height at any point goes through multiple additional processing steps:
    ///   1. ApplyHeightmap() - blend/amount/mask/add modifiers
    ///   2. BaseHeightNoise.Apply() - procedural noise layers stacked on top
    ///   3. GetBaseHeight() - sea level adjustment, edge dropoff, mountain distance
    ///   4. GetBiomeHeight() postfixes - roughmap, flatmap, paint overlays
    ///   5. AddRivers() - river channel carving (when not HeightmapOverrideAll)
    /// 
    /// Reading just the raw heightmap pixel and applying the simplified formula
    /// (pixel * Amount - 0.15 + SeaLevelAdj) * 200 MISSES steps 2-5 entirely.
    /// This causes inaccurate depression detection: rivers drawn where there are
    /// no actual terrain channels, or real channels missed because noise/roughmap
    /// shifted the terrain.
    /// 
    /// SOLUTION: Sample WorldGenerator.GetHeight(wx, wz) on a grid. This function
    /// returns the EXACT same height that Heightmap.Generate() uses to build terrain
    /// chunks - it includes ALL BC patches, noise layers, roughmap, flatmap, and
    /// river carving. The resulting grid is the ground truth.
    /// 
    /// BIOME SAMPLING:
    /// When IncludeBiomeData is true, also samples WorldGenerator.GetBiome(wx, wz)
    /// at each grid point. This produces a parallel byte[] array where each value
    /// is the Heightmap.Biome enum cast. Used for biome-colored map exports.
    /// 
    /// USAGE:
    ///   1. Call BuildCacheCoroutine() from the main thread (needs WorldGenerator)
    ///   2. This samples GetHeight() (and optionally GetBiome()) across the world
    ///   3. The result is a HeightmapSnapshot with the same interface as
    ///      BCHeightmapWaterDetector's snapshot - plug it into DetectWaterBodies()
    ///   4. Depression analysis runs on a background thread using the cached grid
    /// 
    /// GRID RESOLUTION:
    /// Default 4096x4096 for a 20km world = ~5m per pixel. This gives sub-channel
    /// accuracy for most rivers and is the recommended setting.
    /// At 5000 samples/frame, a 4096^2 grid takes ~3400 frames ~ 55 seconds.
    /// 
    /// For pixel-perfect river placement, use 8192x8192 (~2.5m/pixel).
    /// At 5000 samples/frame, an 8192^2 grid takes ~13,400 frames ~ 3-4 minutes.
    /// 
    /// For true 1m/pixel precision (matching Unity's 1m terrain increments), use
    /// 20480x20480. This gives 21000/20480 ~ 1.025m per pixel.
    /// At 5000 samples/frame, a 20480^2 grid takes ~83,886 frames ~ 23 minutes.
    /// Memory: ~1.6 GB for heights + ~400 MB for biome data. Build once, cache forever.
    /// 
    /// For fast/rough scans, use 1024x1024 (~20m/pixel, ~3 seconds at 5000/frame).
    /// </summary>
    public static class WorldHeightCache
    {
        #region Configuration

        /// <summary>
        /// Grid resolution. Controls meters-per-pixel accuracy:
        ///   1024  = ~20m/pixel   (fast, rough)
        ///   2048  = ~10m/pixel   (good for detection)
        ///   4096  = ~5m/pixel    (sub-channel accuracy, recommended)
        ///   8192  = ~2.5m/pixel  (pixel-perfect river channels)
        ///   20480 = ~1.025m/pixel (true 1m precision, matches Unity terrain increments)
        /// For a 20km world (TotalRadius=10500, TotalSize=21000).
        /// 
        /// MEMORY at 20480: ~1.6 GB (heights) + ~400 MB (biome data) = ~2 GB total.
        /// BUILD TIME at 20480 (5000 samples/frame @ 60fps): ~23 minutes.
        /// Once built and cached, no rebuild is needed until world changes.
        /// </summary>
        public static int GridSize = 4096;

        /// <summary>Number of GetHeight() samples per frame during cache building.</summary>
        public static int SamplesPerFrame = 5000;

        /// <summary>
        /// When true, also samples WorldGenerator.GetBiome() at each grid point.
        /// This produces a biome map that can be exported as a color-coded PNG.
        /// Adds ~5% overhead to cache build time and uses Size*Size bytes of extra memory.
        /// </summary>
        public static bool IncludeBiomeData = true;

        #endregion

        #region State

        private static bool _isBuilding = false;
        private static bool _hasCachedSnapshot = false;
        private static BCHeightmapWaterDetector.HeightmapSnapshot _cachedSnapshot;

        /// <summary>
        /// Cached biome data from the last build. Parallel array to _cachedSnapshot.Map.
        /// Each int is the (int)Heightmap.Biome enum value at that grid position.
        /// Null if IncludeBiomeData was false during the last build.
        /// Note: Heightmap.Biome is a flags enum with values up to 512 (Mistlands),
        /// so byte[] is insufficient - we use int[].
        /// </summary>
        private static int[] _cachedBiomes;

        /// <summary>Whether the cache is currently being built.</summary>
        public static bool IsBuilding => _isBuilding;

        /// <summary>Whether a cached snapshot is available for use.</summary>
        public static bool HasCache => _hasCachedSnapshot;

        /// <summary>Gets the cached snapshot, or null if not built yet.</summary>
        public static BCHeightmapWaterDetector.HeightmapSnapshot CachedSnapshot => _hasCachedSnapshot ? _cachedSnapshot : null;

        /// <summary>
        /// Gets the cached biome data, or null if not built or IncludeBiomeData was false.
        /// Each int is the (int)Heightmap.Biome enum value. Array is Size*Size, row-major.
        /// Values: Meadows=1, Swamp=2, Mountain=4, BlackForest=8, Plains=16, AshLands=32,
        /// DeepNorth=64, Ocean=256, Mistlands=512.
        /// </summary>
        public static int[] CachedBiomes => _hasCachedSnapshot ? _cachedBiomes : null;

        #endregion

        /// <summary>
        /// Coroutine that samples WorldGenerator.GetHeight() across the entire world
        /// and builds a HeightmapSnapshot grid. Must run on the main thread.
        /// 
        /// The resulting snapshot has:
        /// - Map[]: float array of world Y heights (NOT normalized 0-1 like BC's)
        /// - Size: grid dimension
        /// - WorldRadius/TotalRadius: from WorldGenerator or BC
        /// - HeightmapAmount: 1.0 (heights are already in world units)
        /// - SeaLevelAdjustment: 0.0 (already baked in)
        /// 
        /// IMPORTANT: Since the Map[] values are actual world Y heights (not 0-1),
        /// HeightmapToWorldY() must handle this. We set HeightmapAmount=1 and
        /// SeaLevelAdjustment=0 and store raw world Y directly - the conversion
        /// formula (val * 1.0 - 0.15 + 0.0) * 200 does NOT apply. Instead,
        /// DetectWaterBodies and other consumers should check the IsWorldHeightCache
        /// flag and skip the conversion, using Map[] values directly as world Y.
        /// </summary>
        /// <param name="onComplete">Called with the snapshot when done, or null on failure.</param>
        public static IEnumerator BuildCacheCoroutine(
            Action<BCHeightmapWaterDetector.HeightmapSnapshot> onComplete = null)
        {
            if (_isBuilding)
            {
                FiresLogger.LogWarning("[WorldHeightCache] Cache build already in progress");
                yield break;
            }

            // Try loading a previously saved cache from disk on a BACKGROUND THREAD.
            // This avoids blocking the main thread for 16+ seconds when reading large
            // cache files (1250MB at 20480x20480), which previously caused Steam networking
            // lock stalls ("SteamnetworkingSockets service thread waited 111ms for lock!").
            bool[] diskLoadResult = { false };
            yield return TryLoadFromDiskAsync(result => diskLoadResult[0] = result);
            if (diskLoadResult[0])
            {
                FiresLogger.LogInfo($"[WorldHeightCache] Using disk-cached snapshot ({_cachedSnapshot.Size}x{_cachedSnapshot.Size})");
                onComplete?.Invoke(_cachedSnapshot);
                yield break;
            }

            var wg = WorldGenerator.instance;
            if (wg == null)
            {
                FiresLogger.LogError("[WorldHeightCache] WorldGenerator.instance is null - cannot build cache");
                onComplete?.Invoke(null);
                yield break;
            }

            _isBuilding = true;

            int size = GridSize;
            float[] heights = new float[size * size];
            int[] biomes = IncludeBiomeData ? new int[size * size] : null;

            // Determine world bounds
            ResolveRadii(out float worldRadius, out float totalRadius);

            float totalSize = totalRadius * 2f;

            FiresLogger.LogInfo($"[WorldHeightCache] Building {size}x{size} height cache " +
                $"(world radius={worldRadius:F0}, total radius={totalRadius:F0}, " +
                $"meters/pixel={totalSize / size:F1}, samples/frame={SamplesPerFrame})");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int totalSamples = size * size;
            int samplesThisFrame = 0;
            int lastReportedPct = -1;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Convert pixel to world coords (same mapping as BC)
                    float nx = (float)x / (size - 1);
                    float ny = (float)y / (size - 1);
                    float wx = (nx - 0.5f) * totalSize;
                    float wz = (ny - 0.5f) * totalSize;

                    // GetHeight returns the FINAL terrain height including ALL modifications:
                    // BC heightmap, noise layers, roughmap, flatmap, river carving, biome heights
                    float worldY = wg.GetHeight(wx, wz);

                    // Store raw world Y directly
                    int idx = y * size + x;
                    heights[idx] = worldY;

                    // Sample biome at this world position (same function Heightmap uses)
                    if (biomes != null)
                    {
                        Heightmap.Biome biome = wg.GetBiome(wx, wz);
                        biomes[idx] = (int)biome;
                    }

                    samplesThisFrame++;
                    if (samplesThisFrame >= SamplesPerFrame)
                    {
                        samplesThisFrame = 0;

                        // Progress reporting
                        int completed = y * size + x + 1;
                        int pct = completed * 100 / totalSamples;
                        int pctBucket = pct / 10 * 10;
                        if (pctBucket > lastReportedPct)
                        {
                            lastReportedPct = pctBucket;
                            FiresLogger.LogInfo($"[WorldHeightCache] Sampling: {pctBucket}% ({completed}/{totalSamples}, {sw.ElapsedMilliseconds}ms)");
                        }

                        yield return null;
                    }
                }
            }

            sw.Stop();

            // Build snapshot with IsWorldHeightCache flag
            var snapshot = new BCHeightmapWaterDetector.HeightmapSnapshot
            {
                Map = heights,
                Size = size,
                WorldRadius = worldRadius,
                TotalRadius = totalRadius,
                // These are set so that HeightmapToWorldY(val, snap) returns val directly:
                //   (val * 1.0 - 0.15 + 0.0) * 200 != val, so we need a different approach.
                // Set a flag and have consumers check it.
                HeightmapAmount = 1f,
                SeaLevelAdjustment = 0f,
                IsWorldHeightCache = true // <-- Map[] values ARE world Y, skip conversion
            };

            _cachedSnapshot = snapshot;
            _cachedBiomes = biomes;
            _hasCachedSnapshot = true;
            _isBuilding = false;

            // Log stats
            float minH = float.MaxValue, maxH = float.MinValue;
            double sumH = 0;
            for (int i = 0; i < heights.Length; i++)
            {
                float h = heights[i];
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
                sumH += h;
            }
            float avgH = (float)(sumH / heights.Length);

            FiresLogger.LogInfo($"[WorldHeightCache] Cache built in {sw.ElapsedMilliseconds}ms - " +
                $"{size}x{size} grid, heights: min={minH:F1}, max={maxH:F1}, avg={avgH:F1}, " +
                $"waterLevel={ZoneSystem.instance?.m_waterLevel ?? 30f}, " +
                $"biomeData={biomes != null}");

            // Save to disk so we never have to build again for this world + resolution
            SaveToDiskAsync();

            onComplete?.Invoke(snapshot);
        }

        // The span the cache covers: BetterContinents' own radii when it runs, else the world's (0.2.721: FatWorldSize, the
        // profile's recorded size; 10000 / 10500 on a vanilla-size world, as the fixed defaults were). BC's fields are radii;
        // the old "over 15000 must be a diameter" guess halved a real Expand-World-sized radius, so on a non-vanilla world
        // it only applies to a value past 1.5x the world's edge.
        // The span a loaded cache must have, or false while the world's size is not applied yet (no world profile): then the
        // check is skipped rather than judged against vanilla defaults ([worldgen] 10-05: a good 20 km cache would be thrown
        // away and rebuilt for ~23 min, maybe at the wrong span).
        private static bool TryWantedRadii(out float worldRadius, out float totalRadius)
        {
            worldRadius = totalRadius = float.NaN;
            if (VerdantsAscent.Terrain.Voxel.FiresWorldProfile.Current == null) return false;
            ResolveRadii(out worldRadius, out totalRadius);
            return true;
        }

        private static void ResolveRadii(out float worldRadius, out float totalRadius)
        {
            totalRadius = VerdantsAscent.Terrain.Voxel.FatWorldSize.EdgeRadius;
            worldRadius = VerdantsAscent.Terrain.Voxel.FatWorldSize.Radius;
            if (!WorldGeneratorAccess.IsBetterContinentsLoaded || !WorldGeneratorAccess.IsBetterContinentsEnabled) return;
            float diameterGuess = VerdantsAscent.Terrain.Voxel.FatWorldSize.IsVanilla ? 15000f : 1.5f * totalRadius;
            try
            {
                var bcType = Type.GetType("BetterContinents.BetterContinents, BetterContinents");
                if (bcType == null) return;
                var trField = HarmonyLib.AccessTools.Field(bcType, "TotalRadius");
                var wrField = HarmonyLib.AccessTools.Field(bcType, "WorldRadius");
                if (trField != null)
                {
                    float bcTR = (float)trField.GetValue(null);
                    if (bcTR > 100f) totalRadius = bcTR > diameterGuess ? bcTR / 2f : bcTR;
                }
                if (wrField != null)
                {
                    float bcWR = (float)wrField.GetValue(null);
                    if (bcWR > 100f) worldRadius = bcWR > diameterGuess ? bcWR / 2f : bcWR;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WorldHeightCache] Failed to read BC world size: {ex.Message}");
            }
        }

        #region Disk Persistence

        // Binary format: VAHC v2
        //   [4 bytes]  Magic "VAHC"
        //   [4 bytes]  Version (int32) = 2
        //   [4 bytes]  Grid size (int32)
        //   [4 bytes]  WorldRadius (float32)
        //   [4 bytes]  TotalRadius (float32)
        //   [1 byte ]  HasBiomeData (0 or 1)
        //   [4 bytes]  CompressedHeightsLength (int32)
        //   [N bytes]  GZipped float[] heights (raw bytes = size*size*4)
        //   if HasBiomeData:
        //     [4 bytes]  CompressedBiomesLength (int32)
        //     [N bytes]  GZipped int[] biomes (raw bytes = size*size*4)

        private static readonly byte[] DiskMagic = { 0x56, 0x41, 0x48, 0x43 }; // "VAHC"
        private const int DiskVersion = 2;
        private static volatile bool _isSaving = false;

        /// <summary>
        /// Gets the cache file path for the current world and grid size.
        /// Returns null if the world name cannot be determined.
        /// File stored at: BepInEx/config/FiresAdminTerrain/Cache/{worldName}_heightcache_{size}.bin
        /// </summary>
        private static string GetCacheFilePath(int gridSize)
        {
            string worldName = null;

            // Method 1: ZNet.World property (same pattern as ClutterPersistence)
            if (ZNet.instance != null)
            {
                try
                {
                    var worldProp = typeof(ZNet).GetProperty("World");
                    if (worldProp != null)
                    {
                        var world = worldProp.GetValue(null) as World;
                        if (world != null)
                            worldName = world.m_name;
                    }
                }
                catch { }
            }

            // Method 2: ZNet.GetWorldName() instance method
            if (string.IsNullOrEmpty(worldName) && ZNet.instance != null)
            {
                try
                {
                    var method = typeof(ZNet).GetMethod("GetWorldName",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (method != null)
                        worldName = method.Invoke(ZNet.instance, null) as string;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(worldName))
                return null;

            string cacheDir = Path.Combine(BepInEx.Paths.ConfigPath, "FiresAdminTerrain", "Cache");
            Directory.CreateDirectory(cacheDir);
            return Path.Combine(cacheDir, $"{worldName}_heightcache_{gridSize}.bin");
        }

        /// <summary>
        /// Attempts to load a previously saved cache from disk.
        /// Returns true if a matching cache was loaded successfully.
        /// The loaded data is populated into _cachedSnapshot and _cachedBiomes.
        /// 
        /// WARNING: This method blocks the calling thread for the entire file read
        /// and decompression. For a 20480x20480 cache (~1250MB), this takes ~17 seconds.
        /// Prefer <see cref="TryLoadFromDiskAsync"/> from coroutines to avoid blocking
        /// the main thread and causing Steam networking lock stalls.
        /// </summary>
        public static bool TryLoadFromDisk()
        {
            // If we have a cached snapshot, verify it matches the current GridSize.
            // Config may have changed GridSize since the last load/build.
            if (_hasCachedSnapshot)
            {
                if (_cachedSnapshot != null && _cachedSnapshot.Size == GridSize)
                    return true; // Already loaded at correct resolution

                // Cached snapshot exists but at wrong resolution - invalidate it
                FiresLogger.LogInfo($"[WorldHeightCache] TryLoadFromDisk: cached snapshot size {_cachedSnapshot?.Size ?? 0} != configured GridSize {GridSize} - invalidating");
                ClearCache();
            }

            string path = GetCacheFilePath(GridSize);
            if (path == null)
            {
                FiresLogger.LogInfo($"[WorldHeightCache] TryLoadFromDisk: cannot determine cache file path " +
                    $"(GridSize={GridSize}, WorldGenerator={WorldGenerator.instance != null}, ZNet={ZNet.instance != null})");
                return false;
            }
            if (!File.Exists(path))
            {
                FiresLogger.LogInfo($"[WorldHeightCache] TryLoadFromDisk: cache file not found at {path}");
                return false;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(fs))
                {
                    // Validate magic
                    byte[] magic = reader.ReadBytes(4);
                    if (magic.Length < 4 || magic[0] != DiskMagic[0] || magic[1] != DiskMagic[1] ||
                        magic[2] != DiskMagic[2] || magic[3] != DiskMagic[3])
                    {
                        FiresLogger.LogWarning("[WorldHeightCache] Invalid cache file magic - rebuilding");
                        return false;
                    }

                    int version = reader.ReadInt32();
                    if (version != DiskVersion)
                    {
                        FiresLogger.LogWarning($"[WorldHeightCache] Cache file version {version} != expected {DiskVersion} - rebuilding");
                        return false;
                    }

                    int size = reader.ReadInt32();
                    if (size != GridSize)
                    {
                        FiresLogger.LogWarning($"[WorldHeightCache] Cache grid size {size} != configured {GridSize} - rebuilding");
                        return false;
                    }

                    float worldRadius = reader.ReadSingle();
                    float totalRadius = reader.ReadSingle();
                    // 0.2.721: a cache built for another world size covers the wrong span; it was reused anyway.
                    if (TryWantedRadii(out float wantRadius, out float wantTotal)
                        && (Mathf.Abs(worldRadius - wantRadius) > 1f || Mathf.Abs(totalRadius - wantTotal) > 1f))
                    {
                        FiresLogger.LogWarning($"[WorldHeightCache] Cache spans radius {worldRadius:F0}/{totalRadius:F0}, this world is "
                            + $"{wantRadius:F0}/{wantTotal:F0} - rebuilding");
                        return false;
                    }
                    bool hasBiomes = reader.ReadByte() != 0;

                    // Read compressed heights
                    int compressedHeightsLen = reader.ReadInt32();
                    byte[] compressedHeights = reader.ReadBytes(compressedHeightsLen);
                    float[] heights = DecompressFloatArray(compressedHeights, size * size);
                    if (heights == null)
                    {
                        FiresLogger.LogWarning("[WorldHeightCache] Failed to decompress height data - rebuilding");
                        return false;
                    }

                    // Read compressed biomes (optional)
                    int[] biomes = null;
                    if (hasBiomes)
                    {
                        int compressedBiomesLen = reader.ReadInt32();
                        byte[] compressedBiomes = reader.ReadBytes(compressedBiomesLen);
                        biomes = DecompressIntArray(compressedBiomes, size * size);
                    }

                    // Build snapshot
                    _cachedSnapshot = new BCHeightmapWaterDetector.HeightmapSnapshot
                    {
                        Map = heights,
                        Size = size,
                        WorldRadius = worldRadius,
                        TotalRadius = totalRadius,
                        HeightmapAmount = 1f,
                        SeaLevelAdjustment = 0f,
                        IsWorldHeightCache = true
                    };
                    _cachedBiomes = biomes;
                    _hasCachedSnapshot = true;

                    sw.Stop();

                    float minH = float.MaxValue, maxH = float.MinValue;
                    for (int i = 0; i < heights.Length; i++)
                    {
                        if (heights[i] < minH) minH = heights[i];
                        if (heights[i] > maxH) maxH = heights[i];
                    }

                    long fileSizeMB = new FileInfo(path).Length / (1024 * 1024);
                    FiresLogger.LogInfo($"[WorldHeightCache] Loaded from disk in {sw.ElapsedMilliseconds}ms - " +
                        $"{size}x{size} grid, heights: {minH:F1} to {maxH:F1}, " +
                        $"biomes={biomes != null}, file={fileSizeMB}MB, path={path}");

                    return true;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WorldHeightCache] Failed to load cache from disk: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Coroutine that loads the height cache from disk on a BACKGROUND THREAD.
        /// The main thread polls for completion via yield, preventing the 16+ second
        /// main-thread stall that TryLoadFromDisk() causes with large cache files
        /// (1250MB at 20480x20480).
        /// 
        /// That stall starved the Steam networking thread, causing:
        ///   "SteamnetworkingSockets service thread waited 111ms for lock!"
        /// 
        /// Returns via callback: true if loaded successfully, false otherwise.
        /// </summary>
        public static IEnumerator TryLoadFromDiskAsync(Action<bool> onComplete)
        {
            // Fast path: already loaded at correct resolution
            if (_hasCachedSnapshot && _cachedSnapshot != null && _cachedSnapshot.Size == GridSize)
            {
                onComplete?.Invoke(true);
                yield break;
            }

            // If cached at wrong resolution, invalidate
            if (_hasCachedSnapshot)
            {
                FiresLogger.LogInfo($"[WorldHeightCache] TryLoadFromDiskAsync: cached snapshot size {_cachedSnapshot?.Size ?? 0} != configured GridSize {GridSize} - invalidating");
                ClearCache();
            }

            string path = GetCacheFilePath(GridSize);
            if (path == null || !System.IO.File.Exists(path))
            {
                onComplete?.Invoke(false);
                yield break;
            }

            FiresLogger.LogInfo($"[WorldHeightCache] Loading cache from disk on background thread: {path}");

            // State shared with background thread
            BCHeightmapWaterDetector.HeightmapSnapshot loadedSnapshot = null;
            int[] loadedBiomes = null;
            int[] completionFlag = { 0 }; // 0 = running, 1 = success, -1 = failure
            string errorMsg = null;

            // Capture locals for the background thread
            int requestedGridSize = GridSize;
            bool checkRadii = TryWantedRadii(out float wantRadius, out float wantTotal);   // 0.2.721, read here on the main thread
            string cachePath = path;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using (var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read))
                    using (var reader = new BinaryReader(fs))
                    {
                        byte[] magic = reader.ReadBytes(4);
                        if (magic.Length < 4 || magic[0] != DiskMagic[0] || magic[1] != DiskMagic[1] ||
                            magic[2] != DiskMagic[2] || magic[3] != DiskMagic[3])
                        {
                            errorMsg = "Invalid cache file magic";
                            Interlocked.Exchange(ref completionFlag[0], -1);
                            return;
                        }

                        int version = reader.ReadInt32();
                        if (version != DiskVersion)
                        {
                            errorMsg = $"Cache file version {version} != expected {DiskVersion}";
                            Interlocked.Exchange(ref completionFlag[0], -1);
                            return;
                        }

                        int size = reader.ReadInt32();
                        if (size != requestedGridSize)
                        {
                            errorMsg = $"Cache grid size {size} != configured {requestedGridSize}";
                            Interlocked.Exchange(ref completionFlag[0], -1);
                            return;
                        }

                        float worldRadius = reader.ReadSingle();
                        float totalRadius = reader.ReadSingle();
                        if (checkRadii && (Math.Abs(worldRadius - wantRadius) > 1f || Math.Abs(totalRadius - wantTotal) > 1f))
                        {
                            errorMsg = $"Cache spans radius {worldRadius:F0}/{totalRadius:F0}, this world is {wantRadius:F0}/{wantTotal:F0}";
                            Interlocked.Exchange(ref completionFlag[0], -1);
                            return;
                        }
                        bool hasBiomes = reader.ReadByte() != 0;

                        int compressedHeightsLen = reader.ReadInt32();
                        byte[] compressedHeights = reader.ReadBytes(compressedHeightsLen);
                        float[] heights = DecompressFloatArray(compressedHeights, size * size);
                        if (heights == null)
                        {
                            errorMsg = "Failed to decompress height data";
                            Interlocked.Exchange(ref completionFlag[0], -1);
                            return;
                        }

                        int[] biomes = null;
                        if (hasBiomes)
                        {
                            int compressedBiomesLen = reader.ReadInt32();
                            byte[] compressedBiomes = reader.ReadBytes(compressedBiomesLen);
                            biomes = DecompressIntArray(compressedBiomes, size * size);
                        }

                        loadedSnapshot = new BCHeightmapWaterDetector.HeightmapSnapshot
                        {
                            Map = heights,
                            Size = size,
                            WorldRadius = worldRadius,
                            TotalRadius = totalRadius,
                            HeightmapAmount = 1f,
                            SeaLevelAdjustment = 0f,
                            IsWorldHeightCache = true
                        };
                        loadedBiomes = biomes;

                        sw.Stop();

                        float minH = float.MaxValue, maxH = float.MinValue;
                        for (int i = 0; i < heights.Length; i++)
                        {
                            if (heights[i] < minH) minH = heights[i];
                            if (heights[i] > maxH) maxH = heights[i];
                        }

                        long fileSizeMB = new FileInfo(cachePath).Length / (1024 * 1024);
                        FiresLogger.LogInfo($"[WorldHeightCache] Loaded from disk in {sw.ElapsedMilliseconds}ms - " +
                            $"{size}x{size} grid, heights: {minH:F1} to {maxH:F1}, " +
                            $"biomes={biomes != null}, file={fileSizeMB}MB, path={cachePath}");
                    }

                    Interlocked.Exchange(ref completionFlag[0], 1);
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                    Interlocked.Exchange(ref completionFlag[0], -1);
                }
            });

            // Poll for completion - yields control back to Unity each frame
            while (completionFlag[0] == 0)
            {
                yield return null;
            }

            if (completionFlag[0] == 1 && loadedSnapshot != null)
            {
                _cachedSnapshot = loadedSnapshot;
                _cachedBiomes = loadedBiomes;
                _hasCachedSnapshot = true;
                onComplete?.Invoke(true);
            }
            else
            {
                if (!string.IsNullOrEmpty(errorMsg))
                    FiresLogger.LogWarning($"[WorldHeightCache] Async disk load failed: {errorMsg} - rebuilding");
                onComplete?.Invoke(false);
            }
        }

        /// <summary>
        /// Saves the current in-memory cache to disk on a background thread.
        /// Thread-safe: snapshots array references before dispatching.
        /// </summary>
        public static void SaveToDiskAsync()
        {
            if (!_hasCachedSnapshot || _cachedSnapshot == null) return;
            if (_isSaving) return;

            string path = GetCacheFilePath(_cachedSnapshot.Size);
            if (path == null) return;

            // Snapshot references on main thread (arrays are not mutated after build)
            var snapshot = _cachedSnapshot;
            var biomes = _cachedBiomes;

            _isSaving = true;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // Compress heights
                    byte[] compressedHeights = CompressFloatArray(snapshot.Map);

                    // Compress biomes if present
                    byte[] compressedBiomes = null;
                    if (biomes != null)
                        compressedBiomes = CompressIntArray(biomes);

                    // Write file atomically (write to .tmp then rename)
                    string tmpPath = path + ".tmp";

                    using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
                    using (var writer = new BinaryWriter(fs))
                    {
                        writer.Write(DiskMagic);
                        writer.Write(DiskVersion);
                        writer.Write(snapshot.Size);
                        writer.Write(snapshot.WorldRadius);
                        writer.Write(snapshot.TotalRadius);
                        writer.Write((byte)(biomes != null ? 1 : 0));

                        writer.Write(compressedHeights.Length);
                        writer.Write(compressedHeights);

                        if (compressedBiomes != null)
                        {
                            writer.Write(compressedBiomes.Length);
                            writer.Write(compressedBiomes);
                        }
                    }

                    // Atomic rename
                    if (File.Exists(path))
                        File.Delete(path);
                    File.Move(tmpPath, path);

                    sw.Stop();
                    long fileSizeMB = new FileInfo(path).Length / (1024 * 1024);
                    FiresLogger.LogInfo($"[WorldHeightCache] Saved to disk in {sw.ElapsedMilliseconds}ms - " +
                        $"{snapshot.Size}x{snapshot.Size}, heights={compressedHeights.Length / (1024 * 1024)}MB compressed, " +
                        $"biomes={compressedBiomes?.Length / (1024 * 1024) ?? 0}MB compressed, " +
                        $"total file={fileSizeMB}MB, path={path}");
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[WorldHeightCache] Failed to save cache to disk: {ex.Message}");

                    // Clean up temp file on failure
                    try { File.Delete(path + ".tmp"); } catch { }
                }
                finally
                {
                    _isSaving = false;
                }
            });
        }

        /// <summary>Compresses a float[] to GZipped bytes.</summary>
        private static byte[] CompressFloatArray(float[] data)
        {
            // Convert float[] to byte[] via Buffer.BlockCopy (no per-element overhead)
            byte[] raw = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, raw, 0, raw.Length);

            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
                {
                    gzip.Write(raw, 0, raw.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>Compresses an int[] to GZipped bytes.</summary>
        private static byte[] CompressIntArray(int[] data)
        {
            byte[] raw = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, raw, 0, raw.Length);

            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
                {
                    gzip.Write(raw, 0, raw.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>Decompresses GZipped bytes back to a float[].</summary>
        private static float[] DecompressFloatArray(byte[] compressed, int expectedCount)
        {
            byte[] raw = new byte[expectedCount * 4];
            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                int totalRead = 0;
                while (totalRead < raw.Length)
                {
                    int bytesRead = gzip.Read(raw, totalRead, raw.Length - totalRead);
                    if (bytesRead == 0) break;
                    totalRead += bytesRead;
                }
                if (totalRead != raw.Length)
                    return null;
            }

            float[] result = new float[expectedCount];
            Buffer.BlockCopy(raw, 0, result, 0, raw.Length);
            return result;
        }

        /// <summary>Decompresses GZipped bytes back to an int[].</summary>
        private static int[] DecompressIntArray(byte[] compressed, int expectedCount)
        {
            byte[] raw = new byte[expectedCount * 4];
            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                int totalRead = 0;
                while (totalRead < raw.Length)
                {
                    int bytesRead = gzip.Read(raw, totalRead, raw.Length - totalRead);
                    if (bytesRead == 0) break;
                    totalRead += bytesRead;
                }
                if (totalRead != raw.Length)
                    return null;
            }

            int[] result = new int[expectedCount];
            Buffer.BlockCopy(raw, 0, result, 0, raw.Length);
            return result;
        }

        #endregion

        /// <summary>
        /// Clears the cached snapshot. Call on logout/world change.
        /// </summary>
        public static void ClearCache()
        {
            _cachedSnapshot = null;
            _cachedBiomes = null;
            _hasCachedSnapshot = false;
            _isBuilding = false;
        }
    }
}
