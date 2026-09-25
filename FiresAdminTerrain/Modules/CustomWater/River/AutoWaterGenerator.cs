using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using VerdantsAscent.Async;


namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Generates custom water (rivers, streams, lakes) from vanilla WorldGenerator data.
    /// 
    /// ARCHITECTURE:
    /// 1. After WorldGenerator.Pregenerate() populates m_rivers/m_streams/m_lakes,
    ///    this class reads that data and converts it to WaterDisk placements.
    /// 2. River/stream path curve math runs on a BACKGROUND THREAD (pure math, no Unity API).
    /// 3. Terrain height sampling runs as a COROUTINE on the main thread (raycasts required).
    /// 4. WaterDisk creation and combiner registration happen on the main thread.
    /// 5. Anchors persist automatically - subsequent loads restore from ZDO, not re-generation.
    /// 
    /// THREAD SAFETY:
    /// - Path math (sin curves, width interpolation) -> background thread via ThreadPool
    /// - Terrain sampling (Physics.Raycast, ZoneSystem) -> main thread coroutine
    /// - Disk/Combiner creation (Unity API) -> main thread
    /// - Uses MainThreadDispatcher for background->main callbacks
    /// </summary>
    public static class AutoWaterGenerator
    {
        #region Configuration

        /// <summary>Enable auto-generation of rivers from WorldGenerator data.</summary>
        public static bool EnableRivers = true;

        /// <summary>Enable auto-generation of streams from WorldGenerator data.</summary>
        public static bool EnableStreams = true;

        /// <summary>Enable auto-generation of lakes from WorldGenerator data.</summary>
        public static bool EnableLakes = true;

        /// <summary>Enable auto-generation of ponds (small standing water, tar-pit sized).</summary>
        public static bool EnablePonds = true;

        /// <summary>Enable auto-generation of springs (tiny puddles at river origins and small depressions).</summary>
        public static bool EnableSprings = true;

        /// <summary>Fraction of disk radius used for spacing between disks along a path (0.6 = 60% overlap).</summary>
        public static float DiskSpacingFactor = 0.6f;

        /// <summary>Default max depth for river water disks in meters.</summary>
        public static float RiverMaxDepth = 2f;

        /// <summary>Default max depth for stream water disks in meters.</summary>
        public static float StreamMaxDepth = 1f;

        /// <summary>Default max depth for lake water disks in meters.</summary>
        public static float LakeMaxDepth = 8f;

        /// <summary>Default max depth for pond water disks in meters. Ponds are shallower than lakes.</summary>
        public static float PondMaxDepth = 3f;

        /// <summary>Default max depth for spring water disks in meters. Springs are very shallow puddles.</summary>
        public static float SpringMaxDepth = 0.5f;

        /// <summary>
        /// Small offset above terrain for water surface (meters).
        /// The water surface sits at terrainY + this offset along the entire river.
        /// At the ocean end, this blends smoothly down to meet sea level.
        /// Keep this small (0.1-0.3m) so water hugs the riverbed closely.
        /// </summary>
        public static float RiverSurfaceOffset = 0.15f;

        /// <summary>
        /// Step size in meters along the river guide path for dense sampling.
        /// 2m gives precise riverbed height tracking for smooth flow from origin to ocean.
        /// Smaller = more disks, better coverage, slower generation.
        /// </summary>
        public static float RiverSampleStep = 2f;

        /// <summary>
        /// Number of radial scan rings when detecting lake extent.
        /// More rings = more accurate lake boundary but slower.
        /// </summary>
        public static int LakeScanRings = 8;

        /// <summary>
        /// Number of angular samples per ring when scanning lake extent.
        /// </summary>
        public static int LakeScanRays = 16;

        /// <summary>
        /// Step size (meters) per ring when scanning lake extent outward from center.
        /// </summary>
        public static float LakeScanStep = 15f;

        /// <summary>Liquid type for auto-generated rivers.</summary>
        public static LiquidType RiverLiquidType = LiquidType.Water;

        /// <summary>Liquid type for auto-generated streams.</summary>
        public static LiquidType StreamLiquidType = LiquidType.Water;

        /// <summary>Liquid type for auto-generated lakes.</summary>
        public static LiquidType LakeLiquidType = LiquidType.Water;

        /// <summary>Liquid type for auto-generated ponds.</summary>
        public static LiquidType PondLiquidType = LiquidType.Water;

        /// <summary>Liquid type for auto-generated springs.</summary>
        public static LiquidType SpringLiquidType = LiquidType.Water;

        /// <summary>
        /// Maximum total number of water paths (rivers + streams + lakes) to process.
        /// Configurable via BepInEx config slider (250-10000, default 2000).
        /// </summary>
        public static int MaxPaths = 2000;

        /// <summary>Maximum disks per river. Set very high so rivers fill their full length
        /// from origin to ocean without truncation. The dense 2m sampling needs room.</summary>
        public static int MaxDisksPerRiver = 50000;

        /// <summary>Maximum disks per stream. Set very high so streams fill completely.</summary>
        public static int MaxDisksPerStream = 50000;

        /// <summary>How many disks to place per frame during the placement coroutine (rate limiter).</summary>
        public static int DisksPerFrame = 50;

        /// <summary>How many complete paths (rivers/streams/lakes) to process per frame.</summary>
        public static int PathsPerFrame = 2;

        /// <summary>Radius to check for existing water when detecting duplicates (meters).</summary>
        public static float DuplicateCheckRadius = 10f;

        /// <summary>
        /// When true and BC heightmap is available, use heightmap depression analysis
        /// instead of GetRiverWeight for water body detection. The heightmap shows the
        /// actual painted terrain - river channels are low pixels relative to neighbors.
        /// </summary>
        public static bool PreferHeightmapDetection = true;

        #endregion

        #region State

        private static bool _isGenerating = false;
        private static Coroutine _generationCoroutine;
        private static int _totalDisksPlaced = 0;
        private static int _riversProcessed = 0;
        private static int _streamsProcessed = 0;
        private static int _lakesProcessed = 0;
        private static int _pondsProcessed = 0;
        private static int _springsProcessed = 0;
        private static int _skippedDuplicates = 0;

        /// <summary>Cached world data counts from last Pregenerate (for status display).</summary>
        public static int CachedRiverCount;
        public static int CachedStreamCount;
        public static int CachedLakeCount;
        public static int CachedPondCount;
        public static int CachedSpringCount;

        /// <summary>Whether generation is currently in progress.</summary>
        public static bool IsGenerating => _isGenerating;

        /// <summary>Whether the last generation used BC heightmap detection.</summary>
        public static bool UsedHeightmapDetection { get; private set; }

        #endregion

        #region Path Point Data (Thread-Safe)

        /// <summary>
        /// A single point along a river/stream path. Pure data - no Unity types except Vector3 (struct).
        /// Computed on background thread, consumed on main thread.
        /// 
        /// THREE HEIGHT REFERENCES (heightmap mode):
        /// 1. TerrainY - absolute bed height from heightmap (BC's exact formula)
        /// 2. DepressionDepth - how far below the banks the bed is (from depression map,
        ///    in world-space meters). Darkest depression pixels at high elevations are
        ///    river origin points; they flow downhill to lower elevations.
        /// 3. BankY - the local bank/edge height that water must NOT exceed (from the
        ///    non-depressed edge pixels of the cross-section in the heightmap).
        /// 
        /// AutoWaterGenerator's cross-reference uses all three:
        ///   bedY = min(heightmapTerrainY, realTerrainY)       - never above real ground
        ///   surfaceY = min(bedY + offset, bankY - margin)     - never above the banks
        /// </summary>
        public struct PathPoint
        {
            public float WorldX;
            public float WorldZ;
            public float Width;
            // TerrainY and SurfaceY are filled on main thread after terrain sampling
            public float TerrainY;
            public float SurfaceY;
            /// <summary>
            /// Depression depth in world-space meters: how far below the local banks
            /// the deepest bed pixel is at this cross-section. Computed from the
            /// depression map (neighborhoodAvg - localHeight) converted to meters.
            /// Zero for non-heightmap mode points.
            /// </summary>
            public float DepressionDepth;
            /// <summary>
            /// Bank height in world Y at this cross-section: the height of the lowest
            /// adjacent non-depressed pixel (the channel edge). Water must never exceed
            /// this minus a small margin. Zero/unset for non-heightmap mode points.
            /// </summary>
            public float BankY;
        }

        /// <summary>
        /// Complete set of path points for one river/stream, computed on background thread.
        /// </summary>
        public class ComputedPath
        {
            public PathPoint[] Points;
            public float MaxDepth;
            public LiquidType LiquidType;
            public string SourceType; // "river", "stream", "lake"
            public float P0X, P0Z, P1X, P1Z; // Original endpoints for duplicate detection
        }

        #endregion

        #region Public API

        /// <summary>
        /// Main entry point. Called from va_auto_water_generate console command.
        /// The player is in-game so Game.instance always exists as a coroutine host.
        /// 
        /// SERVER-ONLY: Only the server/host should generate auto water.
        /// Clients receive the resulting anchor ZDOs via normal ZDO sync and
        /// restore water from them automatically (WaterMeshCombinerAnchor.RestoreFromZDOAsync).
        /// 
        /// Why server-only:
        /// - Prevents duplicate anchors when multiple clients run generation independently
        /// - ZDO ownership: server-created ZDOs have proper ownership for SaveToZDO()
        /// - Guarantees all clients see identical water (deterministic single source)
        /// - Clients joining later load from persisted ZDO anchors automatically
        /// 
        /// 1. Reads WorldGenerator data via reflection
        /// 2. Computes path curves on background thread
        /// 3. Terrain sampling + disk placement on main thread coroutine
        /// </summary>
        public static void GenerateFromWorldData(
            List<Vector2> lakes,
            List<WorldGenerator.River> rivers,
            List<WorldGenerator.River> streams)
        {
            // SERVER-ONLY: Only the server/host creates water anchors.
            // Clients receive them via ZDO sync and restore automatically.
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                FiresLogger.LogWarning("[AutoWaterGenerator] Skipping - only the server/host generates auto water. " +
                    "Clients receive water via ZDO sync automatically.");
                return;
            }

            if (_isGenerating)
            {
                FiresLogger.LogWarning("[AutoWaterGenerator] Generation already in progress");
                return;
            }

            var host = GetOrCreateHost();
            if (host == null)
            {
                FiresLogger.LogError("[AutoWaterGenerator] No coroutine host available (not in-game?)");
                return;
            }

            _totalDisksPlaced = 0;
            _riversProcessed = 0;
            _streamsProcessed = 0;
            _lakesProcessed = 0;
            _pondsProcessed = 0;
            _springsProcessed = 0;
            _skippedDuplicates = 0;
            _isGenerating = true;
            UsedHeightmapDetection = false;

            // Snapshot the data (lists could change if world regenerates)
            var riversCopy = rivers != null ? new List<WorldGenerator.River>(rivers) : new List<WorldGenerator.River>();
            var streamsCopy = streams != null ? new List<WorldGenerator.River>(streams) : new List<WorldGenerator.River>();
            var lakesCopy = lakes != null ? new List<Vector2>(lakes) : new List<Vector2>();

            int totalSources = riversCopy.Count + streamsCopy.Count + lakesCopy.Count;
            FiresLogger.LogInfo($"[AutoWaterGenerator] Starting: {riversCopy.Count} rivers, {streamsCopy.Count} streams, {lakesCopy.Count} lakes ({totalSources} total)");
            FiresLogger.LogInfo($"[AutoWaterGenerator] {WorldGeneratorAccess.GetBCStatusString()}");

            // Better Continents heightmap detection:
            // If BC has a heightmap, the creator painted the terrain INCLUDING rivers.
            // The heightmap IS the ground truth. We read it directly and find channels
            // via relative depression analysis - much more accurate than GetRiverWeight
            // which only knows about vanilla-generated river paths.
            //
            // This is especially critical for HeightmapOverrideAll where AddRivers() is
            // bypassed and GetRiverWeight data doesn't match the actual terrain.
            //
            // IMPORTANT: The raw BC heightmap PNG is just ONE input - the final terrain
            // also includes roughmap, flatmap, noise layers, biome heights, etc. When
            // we can't read BC's in-memory float[], we fall back to WorldHeightCache
            // which samples WorldGenerator.GetHeight() across a grid. This gives us
            // the REAL final terrain with ALL modifications applied.
            //
            // PRIORITY ORDER:
            // 1. WorldHeightCache in-memory (if already loaded at correct resolution)
            //    - This is the BEST source: real final terrain at up to 20480x20480,
            //    includes ALL BC patches, noise layers, roughmap, flatmap, river carving.
            // 2. WorldHeightCache from disk (async, resolved in the coroutine)
            //    - Same quality as #1, just needs to be read from disk first.
            // 3. Build WorldHeightCache on the fly (slow but comprehensive)
            // 4. BC's raw heightmap via reflection (8192x8192, missing roughmap/noise/etc.)
            //    - ONLY used if WorldHeightCache is unavailable AND BC heightmap exists.
            // 5. Vanilla GetRiverWeight mode (no heightmap at all)
            //
            // NOTE: We no longer call TryLoadFromDisk() here synchronously because
            // reading a 1250MB cache file blocks the main thread for ~17 seconds,
            // causing Steam networking lock stalls. The coroutine handles async loading.
            BCHeightmapWaterDetector.HeightmapSnapshot heightmapSnapshot = null;

            // First priority: check if WorldHeightCache is already loaded in memory
            // at the correct resolution. This is instant (no I/O) and gives us the
            // highest-quality data (real terrain with ALL modifications).
            if (PreferHeightmapDetection && WorldHeightCache.HasCache)
            {
                var existingSnap = WorldHeightCache.CachedSnapshot;
                if (existingSnap != null && existingSnap.Size == WorldHeightCache.GridSize)
                {
                    heightmapSnapshot = existingSnap;
                    UsedHeightmapDetection = true;
                    FiresLogger.LogInfo($"[AutoWaterGenerator] Using in-memory WorldHeightCache ({heightmapSnapshot.Size}x{heightmapSnapshot.Size}) - " +
                        "real terrain with all modifications applied");
                }
                else if (existingSnap != null)
                {
                    FiresLogger.LogInfo($"[AutoWaterGenerator] In-memory WorldHeightCache ({existingSnap.Size}x{existingSnap.Size}) " +
                        $"does not match configured GridSize ({WorldHeightCache.GridSize}) - will try disk/rebuild");
                    WorldHeightCache.ClearCache();
                }
            }

            // The coroutine will handle:
            //   - WorldHeightCache disk loading (async I/O, priorities #2/#3)
            //   - Falling back to BC's raw heightmap (priority #4) if no cache available
            //   - Falling through to vanilla mode (priority #5) as last resort
            bool needsWorldHeightCache = false;
            if (heightmapSnapshot == null && PreferHeightmapDetection)
            {
                needsWorldHeightCache = true;
            }

            _generationCoroutine = host.StartCoroutine(
                GenerateCoroutine(lakesCopy, riversCopy, streamsCopy, heightmapSnapshot, needsWorldHeightCache));
        }

        /// <summary>
        /// Cancels any in-progress generation.
        /// </summary>
        public static void CancelGeneration()
        {
            if (_generationCoroutine != null)
            {
                var host = GetOrCreateHost();
                if (host != null)
                {
                    host.StopCoroutine(_generationCoroutine);
                }
                _generationCoroutine = null;
            }
            _isGenerating = false;
        }

        /// <summary>
        /// Returns a summary string of the last generation run.
        /// </summary>
        public static string GetGenerationSummary()
        {
            string mode = UsedHeightmapDetection ? "BC heightmap depression" : "vanilla GetRiverWeight";
            return $"Mode: {mode}, Rivers: {_riversProcessed}, Streams: {_streamsProcessed}, " +
                   $"Lakes: {_lakesProcessed}, Ponds: {_pondsProcessed}, Springs: {_springsProcessed}, " +
                   $"Total disks: {_totalDisksPlaced}, Skipped (duplicates): {_skippedDuplicates}";
        }

        #endregion

        #region Coroutine Host

        private static MonoBehaviour GetOrCreateHost()
        {
            // Use Game.instance as coroutine host - it persists for the session
            if (Game.instance != null) return Game.instance;

            // Fallback: use ZoneSystem
            if (ZoneSystem.instance != null) return ZoneSystem.instance;

            return null;
        }

        #endregion

        #region Main Generation Coroutine

        /// <summary>
        /// Main generation coroutine. Called directly from GenerateFromWorldData.
        /// Phase 1: Background thread computes path curves (pure math).
        /// Phase 2: Main thread samples terrain heights (batched, with progress %).
        /// Phase 3: Main thread places disks into combiners (batched per path, with progress %).
        /// </summary>
        private static IEnumerator GenerateCoroutine(
            List<Vector2> lakes,
            List<WorldGenerator.River> rivers,
            List<WorldGenerator.River> streams,
            BCHeightmapWaterDetector.HeightmapSnapshot heightmapSnapshot = null,
            bool needsWorldHeightCache = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ---- PHASE 0: Load or build WorldHeightCache ----
            // When needsWorldHeightCache is true, we haven't found a heightmap snapshot yet.
            // Try loading the cache from disk ASYNCHRONOUSLY (background thread I/O) to
            // avoid blocking the main thread for 16+ seconds on large cache files.
            // If no disk cache exists, build it from scratch via the sampling coroutine.
            if (needsWorldHeightCache && heightmapSnapshot == null)
            {
                // Step 1: Try loading from disk asynchronously
                FiresLogger.LogInfo("[AutoWaterGenerator] Phase 0: Loading WorldHeightCache from disk (async)...");

                // Validate existing in-memory cache matches configured GridSize
                if (WorldHeightCache.HasCache)
                {
                    var existingSnap = WorldHeightCache.CachedSnapshot;
                    if (existingSnap != null && existingSnap.Size != WorldHeightCache.GridSize)
                    {
                        FiresLogger.LogInfo($"[AutoWaterGenerator] In-memory cache size {existingSnap.Size} != configured GridSize {WorldHeightCache.GridSize} - invalidating");
                        WorldHeightCache.ClearCache();
                    }
                }

                if (WorldHeightCache.HasCache)
                {
                    // Already in memory at correct resolution
                    heightmapSnapshot = WorldHeightCache.CachedSnapshot;
                    UsedHeightmapDetection = true;
                    FiresLogger.LogInfo($"[AutoWaterGenerator] Using in-memory WorldHeightCache ({heightmapSnapshot.Size}x{heightmapSnapshot.Size})");
                }
                else
                {
                    // Try async disk load - yields each frame while background thread reads file
                    bool[] diskLoadResult = { false };
                    yield return WorldHeightCache.TryLoadFromDiskAsync(result => diskLoadResult[0] = result);

                    if (diskLoadResult[0] && WorldHeightCache.HasCache)
                    {
                        heightmapSnapshot = WorldHeightCache.CachedSnapshot;
                        UsedHeightmapDetection = true;
                        FiresLogger.LogInfo($"[AutoWaterGenerator] Using WorldHeightCache ({heightmapSnapshot.Size}x{heightmapSnapshot.Size}) " +
                            $"for depression analysis (configured GridSize={WorldHeightCache.GridSize}, real terrain with all modifications)");
                    }
                }

                // Step 2: If no disk cache, build from scratch
                if (heightmapSnapshot == null)
                {
                    FiresLogger.LogInfo("[AutoWaterGenerator] Phase 0: No cached height data available on server - " +
                        "building WorldHeightCache from real terrain (this may take several minutes " +
                        "for high-resolution grids, but only needs to happen once per world)...");

                    BCHeightmapWaterDetector.HeightmapSnapshot builtSnapshot = null;
                    yield return WorldHeightCache.BuildCacheCoroutine(snap => builtSnapshot = snap);

                    if (builtSnapshot != null)
                    {
                        heightmapSnapshot = builtSnapshot;
                        UsedHeightmapDetection = true;
                        FiresLogger.LogInfo("[AutoWaterGenerator] WorldHeightCache built - proceeding with " +
                            "real-terrain depression analysis");
                    }
                    else
                    {
                        FiresLogger.LogWarning("[AutoWaterGenerator] WorldHeightCache build failed - " +
                            "trying BC raw heightmap fallback");
                    }
                }

                // Step 3: If WorldHeightCache failed entirely, fall back to BC's raw
                // heightmap via reflection. This is lower quality (8192x8192, missing
                // roughmap/noise/etc.) but better than vanilla GetRiverWeight mode.
                if (heightmapSnapshot == null &&
                    BCHeightmapWaterDetector.CanReadHeightmap && WorldGeneratorAccess.IsBetterContinentsEnabled)
                {
                    heightmapSnapshot = BCHeightmapWaterDetector.ReadHeightmap();
                    if (heightmapSnapshot != null)
                    {
                        UsedHeightmapDetection = true;
                        FiresLogger.LogInfo($"[AutoWaterGenerator] Falling back to BC raw heightmap ({heightmapSnapshot.Size}x{heightmapSnapshot.Size}) - " +
                            "note: this misses roughmap/noise/flatmap modifications. " +
                            "Run 'va_build_height_cache' first for best results.");
                    }
                    else
                    {
                        FiresLogger.LogWarning("[AutoWaterGenerator] BC raw heightmap also unavailable - " +
                            "falling back to vanilla GetRiverWeight mode");
                    }
                }
            }

            // ---- PHASE 1: Compute path curves on background thread ----
            string heightmapSource = heightmapSnapshot == null ? "none (vanilla GetRiverWeight)" :
                heightmapSnapshot.IsWorldHeightCache ? $"WorldHeightCache ({heightmapSnapshot.Size}x{heightmapSnapshot.Size})" :
                $"BC raw heightmap ({heightmapSnapshot.Size}x{heightmapSnapshot.Size})";
            FiresLogger.LogInfo($"[AutoWaterGenerator] Phase 1/4: Computing path curves... (heightmap source: {heightmapSource})");


            var allPaths = new List<ComputedPath>();
            int[] completionFlag = { 0 }; // 0 = running, 1 = done (array for thread visibility)

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (heightmapSnapshot != null)
                    {
                        // --- BC HEIGHTMAP MODE ---
                        // Detect water bodies directly from the heightmap image
                        // using relative depression analysis. The heightmap creator
                        // painted rivers as low channels - we find them by comparing
                        // each pixel to its neighborhood average.
                        var waterBodies = BCHeightmapWaterDetector.DetectWaterBodies(heightmapSnapshot);

                        // Count by type for logging
                        int hmRivers = 0, hmStreams = 0, hmLakes = 0, hmPonds = 0, hmSprings = 0, hmUnknown = 0;
                        foreach (var wb2 in waterBodies)
                        {
                            switch (wb2.Type)
                            {
                                case BCHeightmapWaterDetector.WaterBodyType.River: hmRivers++; break;
                                case BCHeightmapWaterDetector.WaterBodyType.Stream: hmStreams++; break;
                                case BCHeightmapWaterDetector.WaterBodyType.Lake: hmLakes++; break;
                                case BCHeightmapWaterDetector.WaterBodyType.Pond: hmPonds++; break;
                                case BCHeightmapWaterDetector.WaterBodyType.Spring: hmSprings++; break;
                                default: hmUnknown++; break;
                            }
                        }

                        MainThreadDispatcher.Enqueue(() =>
                            FiresLogger.LogInfo($"[AutoWaterGenerator] Heightmap detected {waterBodies.Count} water bodies " +
                                $"(rivers: {hmRivers}, streams: {hmStreams}, lakes: {hmLakes}, ponds: {hmPonds}, springs: {hmSprings}, unknown: {hmUnknown})"));

                        // --- TRIBUTARY/RUNOFF FILTERING ---
                        // Compute the average height of all river/stream bodies to establish
                        // the "main river network" baseline. Then filter out bodies that are
                        // BOTH steep (slope > TRIBUTARY_SLOPE_THRESHOLD) AND significantly
                        // higher than the average (> TRIBUTARY_HEIGHT_THRESHOLD above baseline).
                        // These are snowmelt runoffs / seasonal tributaries that shouldn't
                        // be filled with permanent water.
                        float mainRiverHeightSum = 0f;
                        int mainRiverPointCount = 0;
                        foreach (var wb2 in waterBodies)
                        {
                            if (wb2.Type != BCHeightmapWaterDetector.WaterBodyType.River &&
                                wb2.Type != BCHeightmapWaterDetector.WaterBodyType.Stream)
                                continue;
                            if (wb2.PathPoints == null || wb2.PathPoints.Count < 2) continue;
                            foreach (var pp in wb2.PathPoints)
                            {
                                if (pp.TerrainY > 0.1f)
                                {
                                    mainRiverHeightSum += pp.TerrainY;
                                    mainRiverPointCount++;
                                }
                            }
                        }
                        float mainRiverAvgHeight = mainRiverPointCount > 0
                            ? mainRiverHeightSum / mainRiverPointCount
                            : 30f; // fallback to sea level

                        int tributariesFiltered = 0;

                        foreach (var wb in waterBodies)
                        {
                            if (wb.PathPoints == null || wb.PathPoints.Count == 0) continue;

                            string sourceType;
                            float maxDepth;
                            LiquidType liquidType;

                            switch (wb.Type)
                            {
                                case BCHeightmapWaterDetector.WaterBodyType.River:
                                    if (!EnableRivers) continue;
                                    sourceType = "river";
                                    maxDepth = RiverMaxDepth;
                                    liquidType = RiverLiquidType;
                                    break;
                                case BCHeightmapWaterDetector.WaterBodyType.Stream:
                                    if (!EnableStreams) continue;
                                    sourceType = "stream";
                                    maxDepth = StreamMaxDepth;
                                    liquidType = StreamLiquidType;
                                    break;
                                case BCHeightmapWaterDetector.WaterBodyType.Lake:
                                    if (!EnableLakes) continue;
                                    sourceType = "lake";
                                    maxDepth = LakeMaxDepth;
                                    liquidType = LakeLiquidType;
                                    break;
                                case BCHeightmapWaterDetector.WaterBodyType.Pond:
                                    if (!EnablePonds) continue;
                                    sourceType = "pond";
                                    maxDepth = PondMaxDepth;
                                    liquidType = PondLiquidType;
                                    break;
                                case BCHeightmapWaterDetector.WaterBodyType.Spring:
                                    if (!EnableSprings) continue;
                                    sourceType = "spring";
                                    maxDepth = SpringMaxDepth;
                                    liquidType = SpringLiquidType;
                                    break;
                                default:
                                    continue;
                            }

                            // Use the generous per-type limits (rivers and streams fill fully)
                            int maxPts = sourceType == "spring" ? 5 :
                                         sourceType == "pond" ? 50 :
                                         sourceType == "lake" ? 200 :
                                         sourceType == "stream" ? MaxDisksPerStream : MaxDisksPerRiver;

                            // TRIBUTARY FILTERING: For rivers/streams, check if this body
                            // is a steep offshoot at a significantly higher elevation than
                            // the main river network. If so, it's likely a snowmelt runoff
                            // that shouldn't have permanent water.
                            if ((sourceType == "river" || sourceType == "stream") && wb.PathPoints != null && wb.PathPoints.Count >= 2)
                            {
                                // Compute average height and max slope of this body
                                float bodyHeightSum = 0f;
                                int bodyHeightCount = 0;
                                float maxSlope = 0f;

                                for (int pi = 0; pi < wb.PathPoints.Count; pi++)
                                {
                                    if (wb.PathPoints[pi].TerrainY > 0.1f)
                                    {
                                        bodyHeightSum += wb.PathPoints[pi].TerrainY;
                                        bodyHeightCount++;
                                    }
                                    if (pi > 0)
                                    {
                                        float dy = Math.Abs(wb.PathPoints[pi].TerrainY - wb.PathPoints[pi - 1].TerrainY);
                                        float dx = wb.PathPoints[pi].WorldX - wb.PathPoints[pi - 1].WorldX;
                                        float dz = wb.PathPoints[pi].WorldZ - wb.PathPoints[pi - 1].WorldZ;
                                        float hDist = (float)Math.Sqrt(dx * dx + dz * dz);
                                        if (hDist > 0.5f)
                                        {
                                            float slope = dy / hDist;
                                            if (slope > maxSlope) maxSlope = slope;
                                        }
                                    }
                                }

                                float bodyAvgHeight = bodyHeightCount > 0
                                    ? bodyHeightSum / bodyHeightCount
                                    : 0f;
                                float heightAboveMain = bodyAvgHeight - mainRiverAvgHeight;

                                // Filter: steep AND significantly higher than main rivers
                                if (maxSlope > TRIBUTARY_SLOPE_THRESHOLD &&
                                    heightAboveMain > TRIBUTARY_HEIGHT_THRESHOLD)
                                {
                                    tributariesFiltered++;
                                    continue;
                                }
                            }
                            var pts = wb.PathPoints;
                            if (pts.Count > maxPts)
                            {
                                // Subsample to stay within limits
                                var subsampled = new List<PathPoint>();
                                float step = (float)pts.Count / maxPts;
                                for (float fi = 0; fi < pts.Count; fi += step)
                                    subsampled.Add(pts[(int)fi]);
                                pts = subsampled;
                            }

                            // Compute bounding box for duplicate detection endpoints
                            float minX = float.MaxValue, maxX = float.MinValue;
                            float minZ = float.MaxValue, maxZ = float.MinValue;
                            foreach (var pt in pts)
                            {
                                if (pt.WorldX < minX) minX = pt.WorldX;
                                if (pt.WorldX > maxX) maxX = pt.WorldX;
                                if (pt.WorldZ < minZ) minZ = pt.WorldZ;
                                if (pt.WorldZ > maxZ) maxZ = pt.WorldZ;
                            }

                            lock (allPaths)
                            {
                                allPaths.Add(new ComputedPath
                                {
                                    Points = pts.ToArray(),
                                    MaxDepth = maxDepth,
                                    LiquidType = liquidType,
                                    SourceType = sourceType,
                                    P0X = minX,
                                    P0Z = minZ,
                                    P1X = maxX,
                                    P1Z = maxZ
                                });
                            }
                        }
                        if (tributariesFiltered > 0)
                        {
                            int filteredCount = tributariesFiltered;
                            MainThreadDispatcher.Enqueue(() =>
                                FiresLogger.LogInfo($"[AutoWaterGenerator] Filtered {filteredCount} steep tributary/runoff water bodies " +
                                    $"(slope > {TRIBUTARY_SLOPE_THRESHOLD:P0}, height > {TRIBUTARY_HEIGHT_THRESHOLD:F0}m above main river avg {mainRiverAvgHeight:F0}m)"));
                        }
                    }
                    else
                    {
                        // --- VANILLA / GetRiverWeight MODE ---
                        // Use WorldGenerator river/stream/lake lists
                        if (EnableRivers)
                        {
                            foreach (var river in rivers)
                            {
                                var path = ComputeRiverPath(river, RiverMaxDepth, RiverLiquidType, "river", MaxDisksPerRiver);
                                if (path != null && path.Points.Length > 0)
                                    lock (allPaths) { allPaths.Add(path); }
                            }
                        }
                        if (EnableStreams)
                        {
                            foreach (var stream in streams)
                            {
                                var path = ComputeRiverPath(stream, StreamMaxDepth, StreamLiquidType, "stream", MaxDisksPerStream);
                                if (path != null && path.Points.Length > 0)
                                    lock (allPaths) { allPaths.Add(path); }
                            }
                        }
                        if (EnableLakes)
                        {
                            foreach (var lake in lakes)
                            {
                                var path = ComputeLakePath(lake, LakeMaxDepth, LakeLiquidType);
                                if (path != null && path.Points.Length > 0)
                                    lock (allPaths) { allPaths.Add(path); }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MainThreadDispatcher.Enqueue(() =>
                        FiresLogger.LogError($"[AutoWaterGenerator] Path computation failed: {ex.Message}"));
                }
                finally
                {
                    Interlocked.Exchange(ref completionFlag[0], 1);
                }
            });

            // Wait for background thread, logging progress every ~30 seconds
            float lastBgProgressLog = Time.realtimeSinceStartup;
            while (completionFlag[0] == 0)
            {
                if (Time.realtimeSinceStartup - lastBgProgressLog >= 30f)
                {
                    lastBgProgressLog = Time.realtimeSinceStartup;
                    int pathsSoFar;
                    lock (allPaths) { pathsSoFar = allPaths.Count; }
                    FiresLogger.LogInfo($"[AutoWaterGenerator] Phase 1 still running... {pathsSoFar} paths computed so far ({sw.ElapsedMilliseconds}ms elapsed)");
                }
                yield return null;
            }

            if (allPaths.Count == 0)
            {
                FiresLogger.LogInfo("[AutoWaterGenerator] No paths found - nothing to generate");
                _isGenerating = false;
                _generationCoroutine = null;
                yield break;
            }

            // Sort paths center-out so rivers near world center are processed first.
            // Without this, edge-of-world puddles fill the MaxPaths budget before
            // central rivers are reached.
            allPaths.Sort((a, b) =>
            {
                // Rivers and streams get top priority (sorted by distance from center)
                int aPriority = (a.SourceType == "river") ? 0 : (a.SourceType == "stream") ? 1 :
                                (a.SourceType == "lake") ? 2 : 3;
                int bPriority = (b.SourceType == "river") ? 0 : (b.SourceType == "stream") ? 1 :
                                (b.SourceType == "lake") ? 2 : 3;
                if (aPriority != bPriority) return aPriority.CompareTo(bPriority);

                // Within same type, sort by distance from world center (0,0)
                float aCenterX = (a.P0X + a.P1X) * 0.5f;
                float aCenterZ = (a.P0Z + a.P1Z) * 0.5f;
                float bCenterX = (b.P0X + b.P1X) * 0.5f;
                float bCenterZ = (b.P0Z + b.P1Z) * 0.5f;
                float aDist = aCenterX * aCenterX + aCenterZ * aCenterZ;
                float bDist = bCenterX * bCenterX + bCenterZ * bCenterZ;
                return aDist.CompareTo(bDist);
            });

            // Enforce MaxPaths limit
            if (allPaths.Count > MaxPaths)
            {
                FiresLogger.LogInfo($"[AutoWaterGenerator] Trimming {allPaths.Count} paths to MaxPaths limit of {MaxPaths}");
                allPaths.RemoveRange(MaxPaths, allPaths.Count - MaxPaths);
            }

            FiresLogger.LogInfo($"[AutoWaterGenerator] Phase 1 done: {allPaths.Count} paths ({sw.ElapsedMilliseconds}ms)");

            // ---- PHASE 2: Drop-to-terrain height calculation (main thread, batched) ----
            // Simple approach: GetHeight() at each point, clamp to waterLevel, monotonic pass.
            // The combiner handles bank clipping, depth, smoothing independently in its mesh gen.
            FiresLogger.LogInfo("[AutoWaterGenerator] Phase 2/4: Drop-to-terrain + filtering...");

            int totalPoints = 0;
            foreach (var path in allPaths)
                totalPoints += path.Points.Length;

            int pointsProcessed = 0;
            int pointsPerFrame = 200; // Much faster now - just GetHeight() calls, no cross-sections
            int lastReportedPct = -1;

            float waterLevel = GetWaterLevel();

            FiresLogger.LogInfo($"[AutoWaterGenerator] Using global water level: {waterLevel} (heightmap mode: {UsedHeightmapDetection})");

            int skippedOcean = 0;
            int skippedSlope = 0;

            foreach (var path in allPaths)
            {
                bool isLake = path.SourceType == "lake" || path.SourceType == "pond" || path.SourceType == "spring";

                if (isLake)
                {
                    DropLakeToTerrain(path, waterLevel);
                }
                else
                {
                    // For rivers/streams: drop each point to terrain, enforce monotonic flow
                    DropRiverToTerrain(path, waterLevel);
                    path.Points = TrimInvalidPoints(path.Points);
                }

                // Skip water bodies entirely below ocean level - the ocean already covers them.
                // Only keep paths where at least some points are above the ocean surface.
                if (path.Points != null && path.Points.Length > 0)
                {
                    bool anyAboveOcean = false;
                    for (int pi = 0; pi < path.Points.Length; pi++)
                    {
                        if (path.Points[pi].SurfaceY > waterLevel + 0.5f)
                        {
                            anyAboveOcean = true;
                            break;
                        }
                    }
                    if (!anyAboveOcean)
                    {
                        path.Points = new PathPoint[0];
                        skippedOcean++;
                    }
                }

                // Skip ponds/springs on slopes - they can't produce a flat water surface.
                // Measure the height range across all points; if the slope is too steep
                // for the body size, it would just be a tilted plane.
                if (path.Points != null && path.Points.Length > 1 &&
                    (path.SourceType == "pond" || path.SourceType == "spring"))
                {
                    float minTerrain = float.MaxValue, maxTerrain = float.MinValue;
                    float maxExtent = 0f;
                    for (int pi = 0; pi < path.Points.Length; pi++)
                    {
                        float ty = path.Points[pi].TerrainY;
                        if (ty < minTerrain) minTerrain = ty;
                        if (ty > maxTerrain) maxTerrain = ty;
                        float dx = path.Points[pi].WorldX - path.Points[0].WorldX;
                        float dz = path.Points[pi].WorldZ - path.Points[0].WorldZ;
                        float ext = Mathf.Sqrt(dx * dx + dz * dz);
                        if (ext > maxExtent) maxExtent = ext;
                    }
                    float terrainRange = maxTerrain - minTerrain;
                    // If terrain across the pond rises more than 2m, or slope > 15%,
                    // skip - flat water would look wrong on this terrain.
                    if (terrainRange > 2f || (maxExtent > 1f && terrainRange / maxExtent > 0.15f))
                    {
                        path.Points = new PathPoint[0];
                        skippedSlope++;
                    }
                }

                pointsProcessed += path.Points.Length;

                // Progress update every 10%
                int pct = totalPoints > 0 ? (pointsProcessed * 100 / totalPoints) : 100;
                int pctBucket = pct / 10 * 10;
                if (pctBucket > lastReportedPct)
                {
                    lastReportedPct = pctBucket;
                    FiresLogger.LogInfo($"[AutoWaterGenerator] Terrain sampling: {pctBucket}% ({pointsProcessed}/{totalPoints})");
                }

                // Yield periodically to avoid blocking the main thread
                if (pointsProcessed % pointsPerFrame < path.Points.Length)
                    yield return null;
            }

            if (skippedOcean > 0 || skippedSlope > 0)
            {
                FiresLogger.LogInfo($"[AutoWaterGenerator] Phase 2 filtering: skipped {skippedOcean} below ocean, {skippedSlope} on slopes");
            }

            // ---- PHASE 3: Place WaterDisks (main thread, rate-limited) ----
            FiresLogger.LogInfo("[AutoWaterGenerator] Phase 3/4: Placing water...");

            bool prevVerbose = WaterMeshCombiner.s_verboseLogging;
            WaterMeshCombiner.s_verboseLogging = false;

            int pathsProcessed = 0;
            int totalPaths = allPaths.Count;
            int pathsThisFrame = 0;
            lastReportedPct = -1;

            foreach (var path in allPaths)
            {
                pathsProcessed++;

                if (HasExistingWaterAlongPath(path))
                {
                    _skippedDuplicates++;
                    continue;
                }

                var disks = new List<WaterDisk>(path.Points.Length);

                // SIMPLE DISK PLACEMENT:
                // Place a circle disk at each checkpoint with radius = width * 0.7
                // (wide enough to clip through both banks). The combiner's auto-combine
                // logic merges overlapping disks. Spacing uses DiskSpacingFactor.
                bool isRiverOrStream = (path.SourceType == "river" || path.SourceType == "stream");
                float accumulatedDist = 0f;
                float nextDiskDist = 0f;

                for (int i = 0; i < path.Points.Length; i++)
                {
                    var pt = path.Points[i];

                    if (i > 0)
                    {
                        var prev = path.Points[i - 1];
                        float ddx = pt.WorldX - prev.WorldX;
                        float ddz = pt.WorldZ - prev.WorldZ;
                        accumulatedDist += Mathf.Sqrt(ddx * ddx + ddz * ddz);
                    }

                    // Radius = 70% of channel width - wide enough to clip through banks
                    float radius = pt.Width * 0.7f;
                    if (radius < 3f) radius = 3f;

                    // Cap radius per type
                    if (path.SourceType == "spring")
                    {
                        if (radius > 8f) radius = 8f;
                    }
                    else if (path.SourceType != "lake" && path.SourceType != "pond")
                    {
                        float maxRadius = (path.SourceType == "stream") ? 25f : 50f;
                        if (radius > maxRadius) radius = maxRadius;
                    }

                    // Spacing: DiskSpacingFactor * radius between disks
                    float spacing = radius * DiskSpacingFactor;
                    if (spacing < 2f) spacing = 2f;

                    // Always place first and last points
                    if (i > 0 && i < path.Points.Length - 1 && accumulatedDist < nextDiskDist)
                        continue;

                    nextDiskDist = accumulatedDist + spacing;

                    disks.Add(new WaterDisk(
                        new Vector3(pt.WorldX, pt.SurfaceY, pt.WorldZ),
                        radius,
                        path.MaxDepth,
                        path.LiquidType
                    ));
                }

                if (disks.Count == 0)
                {
                    // Only warn for rivers/streams - other types not being placed is expected
                    if (path.SourceType == "river" || path.SourceType == "stream")
                    {
                        FiresLogger.LogWarning($"[AutoWaterGenerator] Path '{path.SourceType}' at ({path.P0X:F0},{path.P0Z:F0}) has 0 valid points after terrain sampling - skipped");
                    }
                    continue;
                }

                WaterMeshCombiner combiner = null;
                foreach (var existing in WaterMeshCombiner.AllCombiners)
                {
                    if (existing != null && existing.WouldOverlapWith(disks[0]))
                    {
                        combiner = existing;
                        break;
                    }
                }

                if (combiner != null)
                {
                    combiner.AddWaterDisksBatch(disks);
                    combiner.QueueDebouncedSave();
                }
                else
                {
                    combiner = WaterMeshCombiner.GetOrCreateCombinerFor(disks[0]);
                    if (disks.Count > 1)
                    {
                        combiner.AddWaterDisksBatch(disks.GetRange(1, disks.Count - 1));
                        combiner.QueueDebouncedSave();
                    }
                }
                _totalDisksPlaced += disks.Count;

                // Stream this combiner's data to clients incrementally.
                // Clients receive each river/stream/lake as it's completed,
                // so water appears progressively during the 30+ minute generation.
                AutoWaterPatches.StreamCombinerToClients(combiner);

                switch (path.SourceType)
                {
                    case "river": _riversProcessed++; break;
                    case "stream": _streamsProcessed++; break;
                    case "lake": _lakesProcessed++; break;
                    case "pond": _pondsProcessed++; break;
                    case "spring": _springsProcessed++; break;
                }

                // Progress update every 10%
                int pct = totalPaths > 0 ? (pathsProcessed * 100 / totalPaths) : 100;
                int pctBucket = pct / 10 * 10;
                if (pctBucket > lastReportedPct)
                {
                    lastReportedPct = pctBucket;
                    FiresLogger.LogInfo($"[AutoWaterGenerator] Placing water: {pctBucket}% ({pathsProcessed}/{totalPaths} paths)");
                }

                pathsThisFrame++;
                if (pathsThisFrame >= PathsPerFrame)
                {
                    pathsThisFrame = 0;
                    yield return null;
                }
            }

            // ---- PHASE 4: CLEANUP - Fix floating disks, remove empty combiners ----
            // After all disks are placed and combiners regenerated, do a final sanity pass:
            //   1. Drop floating disks to terrain (surface should not be far above ground)
            //   2. Remove empty or trivially small combiners
            //   3. Force-save all surviving combiners
            FiresLogger.LogInfo("[AutoWaterGenerator] Phase 4/4: Cleanup - fixing floating disks and empty combiners...");

            int floatingFixed = 0;
            int emptyRemoved = 0;
            int disksClamped = 0;

            // Maximum height a disk surface can be above terrain before being clamped.
            // With cross-section bed scanning in DropRiverToTerrain, disks should be
            // within ~1.5m of the bed. Anything higher is floating.
            const float MAX_FLOAT_ABOVE_TERRAIN = 2.0f;
            // Minimum disk count for a combiner to survive cleanup
            const int MIN_DISKS_TO_KEEP = 1;

            // Copy the list since we may modify it during iteration
            var cleanupCombiners = new List<WaterMeshCombiner>(WaterMeshCombiner.AllCombiners);
            var combinersToDestroy = new List<WaterMeshCombiner>();
            var combinersNeedingRegen = new HashSet<WaterMeshCombiner>();

            foreach (var combiner in cleanupCombiners)
            {
                if (combiner == null) continue;

                var disks = combiner.GetWaterDisks();
                if (disks == null || disks.Count < MIN_DISKS_TO_KEEP)
                {
                    combinersToDestroy.Add(combiner);
                    emptyRemoved++;
                    continue;
                }

                // Check each disk: if its Y is far above the real terrain at that XZ,
                // clamp it down so the water hugs the ground.
                // Use a small cross-section scan to find the actual bed, not just the
                // center point (which may be on a bank slope).
                bool anyDiskModified = false;
                for (int di = 0; di < disks.Count; di++)
                {
                    var disk = disks[di];
                    if (disk == null) continue;

                    // Scan a small cross pattern to find the lowest nearby terrain
                    float terrainY = GetTerrainHeight(disk.Position.x, disk.Position.z);
                    float scanR = Mathf.Min(disk.Radius * 0.3f, 10f);
                    if (scanR > 1f)
                    {
                        for (float off = -scanR; off <= scanR; off += Mathf.Max(1f, scanR / 3f))
                        {
                            float h1 = GetTerrainHeight(disk.Position.x + off, disk.Position.z);
                            float h2 = GetTerrainHeight(disk.Position.x, disk.Position.z + off);
                            if (h1 < terrainY) terrainY = h1;
                            if (h2 < terrainY) terrainY = h2;
                        }
                    }
                    float floatAmount = disk.Position.y - terrainY;

                    // If the disk surface is significantly above terrain, clamp it down.
                    // Allow some margin (MAX_FLOAT_ABOVE_TERRAIN) because rivers on slopes
                    // legitimately sit a bit above the lowest terrain sample.
                    if (floatAmount > MAX_FLOAT_ABOVE_TERRAIN)
                    {
                        float newY = terrainY + RiverSurfaceOffset;
                        // Don't push below the global water level if the disk was placed there
                        float wLevel = GetWaterLevel();
                        if (disk.Position.y >= wLevel && newY < wLevel)
                            newY = wLevel;

                        disk.Position = new Vector3(disk.Position.x, newY, disk.Position.z);
                        disksClamped++;
                        anyDiskModified = true;
                    }
                }

                if (anyDiskModified)
                {
                    floatingFixed++;
                    combinersNeedingRegen.Add(combiner);
                }
            }

            // Destroy empty / trivial combiners
            foreach (var combiner in combinersToDestroy)
            {
                if (combiner != null)
                {
                    combiner.DestroyPermanently();
                }
            }

            // Regenerate combiners whose disks were clamped, then force-save
            int cleanupRegenCount = 0;
            foreach (var combiner in combinersNeedingRegen)
            {
                if (combiner == null) continue;
                try
                {
                    combiner.ForceRegenerate();
                }
                catch (Exception regenEx)
                {
                    FiresLogger.LogWarning($"[AutoWaterGenerator] Cleanup regen failed: {regenEx.Message}");
                }
                cleanupRegenCount++;
                if (cleanupRegenCount % 5 == 0)
                    yield return null;
            }

            // Force immediate save on ALL surviving combiners so nothing is lost
            int savedCount = 0;
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null) continue;
                try
                {
                    combiner.ForceImmediateSave();
                }
                catch (Exception saveEx)
                {
                    FiresLogger.LogWarning($"[AutoWaterGenerator] Cleanup save failed: {saveEx.Message}");
                }
                savedCount++;
                if (savedCount % 10 == 0)
                    yield return null;
            }

            FiresLogger.LogInfo($"[AutoWaterGenerator] Phase 4 COMPLETE: {floatingFixed} combiners had floating disks " +
                $"({disksClamped} disks clamped), {emptyRemoved} empty combiners removed, " +
                $"{savedCount} combiners saved");

            // Purge destroyed entries from the static list
            WaterMeshCombiner.PurgeDestroyedCombiners();

            // ALWAYS save the world after generation so anchor ZDOs are committed to disk.
            // Without this, a server crash before the next auto-save would lose all generated water.
            // Use async save (false) to avoid blocking the main thread for 37+ seconds with
            // large worlds (7M+ ZDOs), which starves Steam networking and disconnects clients.
            if (ZNet.instance != null)
            {
                ZNet.instance.Save(false);
                FiresLogger.LogInfo("[AutoWaterGenerator] World save queued (async) after generation");
            }

            // Schedule a one-shot zone-load cleanup pass.
            // When the next zone spawns (i.e. player moves or zones refresh),
            // it will re-sample real loaded terrain under every combiner's disks
            // and clamp any that are still floating.
            AutoWaterPatches.ScheduleZoneLoadCleanup();

            WaterMeshCombiner.s_verboseLogging = prevVerbose;

            sw.Stop();
            _isGenerating = false;
            _generationCoroutine = null;

            int combinerCount = WaterMeshCombiner.AllCombiners != null ? WaterMeshCombiner.AllCombiners.Count : 0;

            FiresLogger.LogInfo($"[AutoWaterGenerator] Complete! {sw.ElapsedMilliseconds}ms - {GetGenerationSummary()}, Active combiners: {combinerCount}");

            // Log per-path summaries (one line per river/stream/lake - not per combiner)
            // Format: type #N: (startX,startZ)->(endX,endZ) pts=N disks=N
            if (allPaths.Count > 0)
            {
                FiresLogger.LogInfo($"[AutoWaterGenerator] === WATER BODY SUMMARY ({allPaths.Count} paths) ===");

                // Count by type for summary
                int logRivers = 0, logStreams = 0, logLakes = 0, logPonds = 0, logSprings = 0;

                foreach (var path in allPaths)
                {
                    if (path.Points == null || path.Points.Length == 0) continue;

                    int typeIdx;
                    switch (path.SourceType)
                    {
                        case "river": typeIdx = ++logRivers; break;
                        case "stream": typeIdx = ++logStreams; break;
                        case "lake": typeIdx = ++logLakes; break;
                        case "pond": typeIdx = ++logPonds; break;
                        case "spring": typeIdx = ++logSprings; break;
                        default: typeIdx = 0; break;
                    }

                    var first = path.Points[0];
                    var last = path.Points[path.Points.Length - 1];
                    float avgWidth = 0f;
                    for (int pi = 0; pi < path.Points.Length; pi++)
                        avgWidth += path.Points[pi].Width;
                    avgWidth /= path.Points.Length;

                    float dx = last.WorldX - first.WorldX;
                    float dz = last.WorldZ - first.WorldZ;
                    float length = Mathf.Sqrt(dx * dx + dz * dz);

                    FiresLogger.LogInfo($"[AutoWater] {path.SourceType} #{typeIdx}: ({first.WorldX:F0},{first.WorldZ:F0})?({last.WorldX:F0},{last.WorldZ:F0}) " +
                        $"len={length:F0}m avgW={avgWidth:F0}m pts={path.Points.Length} Y={first.SurfaceY:F1}?{last.SurfaceY:F1}");
                }

                FiresLogger.LogInfo($"[AutoWaterGenerator] === END SUMMARY (rivers={logRivers} streams={logStreams} lakes={logLakes} ponds={logPonds} springs={logSprings}) ===");
            }

            // Notify all connected clients to refresh their zones so water becomes visible
            AutoWaterPatches.NotifyClientsGenerationComplete(combinerCount, _totalDisksPlaced);
        }

        #endregion

        #region Path Computation (Background Thread - Pure Math)

        /// <summary>
        /// Computes a dense "guide path" along a river or stream corridor.
        /// ALL PURE MATH - safe to call from background thread.
        /// 
        /// This generates closely-spaced points (every RiverSampleStep meters) along the
        /// sinusoidal curve from the River struct. These serve as GUIDE positions for
        /// Phase 2's GetRiverWeight()-based channel detection - the actual channel center
        /// and width are refined on the main thread by querying the real river data.
        /// 
        /// Uses the same sinusoidal curve formula as vanilla WorldGenerator:
        ///   offset = curveWidth * sin(distance / curveWavelength)
        ///   width = lerp(widthMin, widthMax, sinusoidal variation)
        /// </summary>
        private static ComputedPath ComputeRiverPath(
            WorldGenerator.River river, float maxDepth, LiquidType liquidType, string sourceType, int maxDisks)
        {
            float dx = river.p1.x - river.p0.x;
            float dz = river.p1.y - river.p0.y; // Vector2 y = world Z
            float totalLength = (float)Math.Sqrt(dx * dx + dz * dz);

            if (totalLength < 1f) return null;

            float normX = dx / totalLength;
            float normZ = dz / totalLength;

            // Perpendicular direction for curve offset
            float perpX = -normZ;
            float perpZ = normX;

            // Use dense sampling - every RiverSampleStep meters
            float stepSize = RiverSampleStep;
            if (stepSize < 1f) stepSize = 1f;

            int numSteps = (int)Math.Ceiling(totalLength / stepSize);
            if (numSteps > maxDisks) numSteps = maxDisks;
            if (numSteps < 2) numSteps = 2;

            var points = new PathPoint[numSteps + 1];

            for (int i = 0; i <= numSteps; i++)
            {
                float t = (float)i / numSteps;
                float distAlongPath = t * totalLength;

                // Sinusoidal curve offset (matches vanilla PlaceRivers)
                float curveOffset = 0f;
                if (river.curveWidth > 0.01f && river.curveWavelength > 0.01f)
                {
                    curveOffset = (float)(river.curveWidth * Math.Sin(distAlongPath / river.curveWavelength));
                }

                // Base position (straight line interpolation)
                float baseX = river.p0.x + dx * t;
                float baseZ = river.p0.y + dz * t; // Vector2 y = world Z

                // Apply curve
                float worldX = baseX + perpX * curveOffset;
                float worldZ = baseZ + perpZ * curveOffset;

                // Width hint from the River struct - Phase 2 will refine using GetRiverWeight
                float widthT = 0.5f + 0.5f * (float)Math.Sin(distAlongPath * 0.1f);
                float width = river.widthMin + (river.widthMax - river.widthMin) * widthT;

                points[i] = new PathPoint
                {
                    WorldX = worldX,
                    WorldZ = worldZ,
                    Width = width,
                    TerrainY = 0f, // Filled on main thread
                    SurfaceY = 0f  // Filled on main thread
                };
            }

            return new ComputedPath
            {
                Points = points,
                MaxDepth = maxDepth,
                LiquidType = liquidType,
                SourceType = sourceType,
                P0X = river.p0.x,
                P0Z = river.p0.y,
                P1X = river.p1.x,
                P1Z = river.p1.y
            };
        }

        /// <summary>
        /// Computes lake disk positions as a grid of candidate points.
        /// Actual terrain sampling and extent detection happens on the main thread
        /// in SampleLakeTerrainCoroutine since it needs Unity API.
        /// Pure math - background thread safe.
        /// </summary>
        private static ComputedPath ComputeLakePath(Vector2 lakeCenter, float maxDepth, LiquidType liquidType)
        {
            // Generate a grid of candidate points around the lake center.
            // The main thread coroutine will scan terrain, find the actual extent,
            // determine the fill level, and trim points outside the depression.

            float scanRadius = LakeScanRings * LakeScanStep;

            var points = new List<PathPoint>();

            // Center point
            points.Add(new PathPoint
            {
                WorldX = lakeCenter.x,
                WorldZ = lakeCenter.y,
                Width = LakeScanStep * 2f,
                TerrainY = 0f,
                SurfaceY = 0f
            });

            // Concentric rings of points outward from center
            for (int ring = 1; ring <= LakeScanRings; ring++)
            {
                float r = ring * LakeScanStep;
                int rayCount = LakeScanRays;

                for (int ray = 0; ray < rayCount; ray++)
                {
                    float angle = (float)(ray * 2.0 * Math.PI / rayCount);
                    float x = lakeCenter.x + r * (float)Math.Cos(angle);
                    float z = lakeCenter.y + r * (float)Math.Sin(angle);

                    // Disk radius sized for good overlap between ring points
                    float diskWidth = LakeScanStep * 1.5f;

                    points.Add(new PathPoint
                    {
                        WorldX = x,
                        WorldZ = z,
                        Width = diskWidth,
                        TerrainY = 0f,
                        SurfaceY = 0f
                    });
                }
            }

            return new ComputedPath
            {
                Points = points.ToArray(),
                MaxDepth = maxDepth,
                LiquidType = liquidType,
                SourceType = "lake",
                P0X = lakeCenter.x,
                P0Z = lakeCenter.y,
                P1X = lakeCenter.x,
                P1Z = lakeCenter.y
            };
        }

        #endregion

        #region Terrain Sampling (Main Thread Only) - Drop-To-Terrain

        /// <summary>
        /// Gets terrain height at an XZ position using WorldGenerator.GetHeight().
        /// This is the authoritative procedural terrain including river carving
        /// (AddRivers), biome height, and BC modifications.
        /// </summary>
        private static float GetTerrainHeight(float worldX, float worldZ)
        {
            if (WorldGenerator.instance != null)
                return WorldGenerator.instance.GetHeight(worldX, worldZ);
            return 30f; // Fallback to water level
        }

        /// <summary>
        /// Gets the global water level (vanilla default = 30).
        /// Respects EWR/modded water level if ZoneSystem has been patched.
        /// </summary>
        private static float GetWaterLevel()
        {
            if (ZoneSystem.instance != null)
                return ZoneSystem.instance.m_waterLevel;
            return 30f;
        }

        #endregion

        #region River/Lake Surface - Simple Drop-To-Terrain

        /// <summary>
        /// Height offset above the riverbed for the water surface (meters).
        /// Each disk is placed at terrainY + SURFACE_OFFSET. The combiner's
        /// IDW blending smoothly interpolates between disks and its terrain
        /// clip pass handles bank clipping automatically.
        /// </summary>
        private const float SURFACE_OFFSET = 1.5f;

        /// <summary>
        /// Minimum offset above bed so water is always visible and doesn't
        /// z-fight with the terrain mesh.
        /// </summary>
        private const float MIN_BED_OFFSET = 0.15f;

        /// <summary>
        /// Distance over which the river surface blends from terrain-hugging
        /// up to waterLevel at the ocean end (meters along path).
        /// </summary>
        private const float OCEAN_BLEND_DISTANCE = 50f;

        /// <summary>
        /// Maximum slope (dY/dDist) for tributary filtering.
        /// Offshoots steeper than this AND significantly higher than the main
        /// river's average height are considered snowmelt runoffs and filtered.
        /// 0.15 = 15% grade (~8.5 degrees). Normal rivers are 0.5-5% grade.
        /// </summary>
        private const float TRIBUTARY_SLOPE_THRESHOLD = 0.15f;

        /// <summary>
        /// Minimum height above the main river's average surface for a tributary
        /// to be filtered. Must be BOTH steep AND this much higher.
        /// </summary>
        private const float TRIBUTARY_HEIGHT_THRESHOLD = 5.0f;

        /// <summary>
        /// Simple drop-to-terrain for rivers/streams.
        /// 
        /// Sets each point's surface at terrain + SURFACE_OFFSET (1.5m).
        /// The disk radius is set wide enough to clip through both banks
        /// (width * 0.7) so the combiner's auto-combine logic merges them.
        /// The combiner's IDW blending, terrain clip, and smoothing passes
        /// handle bank clipping, smooth transitions, and edge fading.
        /// 
        /// Also blends to ocean level at the downstream end.
        /// </summary>
        private static void DropRiverToTerrain(ComputedPath path, float waterLevel)
        {
            if (path.Points == null || path.Points.Length == 0) return;

            int len = path.Points.Length;

            // Step 1: Sample terrain at each checkpoint using a CROSS-SECTION SCAN.
            // The heightmap centerline can be off-center, landing on the bank slope
            // instead of the actual bed. We sample multiple points across the channel
            // width perpendicular to the path and use the MINIMUM height as the bed.
            // This ensures water sits at the true bottom of the channel.
            for (int i = 0; i < len; i++)
            {
                var pt = path.Points[i];

                // Compute path tangent for perpendicular scan direction
                float tangentX, tangentZ;
                if (len < 2)
                {
                    tangentX = 1f; tangentZ = 0f;
                }
                else if (i == 0)
                {
                    tangentX = path.Points[1].WorldX - path.Points[0].WorldX;
                    tangentZ = path.Points[1].WorldZ - path.Points[0].WorldZ;
                }
                else if (i == len - 1)
                {
                    tangentX = path.Points[len - 1].WorldX - path.Points[len - 2].WorldX;
                    tangentZ = path.Points[len - 1].WorldZ - path.Points[len - 2].WorldZ;
                }
                else
                {
                    tangentX = path.Points[i + 1].WorldX - path.Points[i - 1].WorldX;
                    tangentZ = path.Points[i + 1].WorldZ - path.Points[i - 1].WorldZ;
                }
                float tangentLen = Mathf.Sqrt(tangentX * tangentX + tangentZ * tangentZ);
                float perpX = 0f, perpZ = 0f;
                if (tangentLen > 0.01f)
                {
                    perpX = -tangentZ / tangentLen;
                    perpZ = tangentX / tangentLen;
                }

                // Sample the center point
                float centerY = GetTerrainHeight(pt.WorldX, pt.WorldZ);
                float bedY = centerY;

                // Scan across the channel width in 1m steps (center 60%)
                // to find the actual lowest point (the bed)
                float halfScan = pt.Width * 0.3f;
                if (halfScan > 1f)
                {
                    float scanStep = Mathf.Max(1f, halfScan / 5f);
                    for (float off = -halfScan; off <= halfScan; off += scanStep)
                    {
                        float sx = pt.WorldX + perpX * off;
                        float sz = pt.WorldZ + perpZ * off;
                        float h = GetTerrainHeight(sx, sz);
                        if (h < bedY) bedY = h;
                    }
                }

                // Cross-reference: if heightmap provided a bed height, use the lower
                if (pt.TerrainY > 0.1f)
                    pt.TerrainY = Mathf.Min(pt.TerrainY, bedY);
                else
                    pt.TerrainY = bedY;

                // Surface = terrain + offset. The disk's Y IS the water level.
                // The combiner's IDW blending smoothly interpolates between disks.
                pt.SurfaceY = pt.TerrainY + SURFACE_OFFSET;

                path.Points[i] = pt;
            }

            if (len < 2) return;

            // Step 2: Ocean blend at downstream end
            float startTerrainY = path.Points[0].TerrainY;
            float endTerrainY = path.Points[len - 1].TerrainY;
            bool startIsDownstream = (startTerrainY <= endTerrainY);
            int downstreamIdx = startIsDownstream ? 0 : len - 1;
            int upstreamIdx = startIsDownstream ? len - 1 : 0;
            int uphillStep = startIsDownstream ? 1 : -1;

            // Compute cumulative distance from downstream end
            float[] distFromDownstream = new float[len];
            distFromDownstream[downstreamIdx] = 0f;
            for (int i = downstreamIdx + uphillStep; ; i += uphillStep)
            {
                int prev = i - uphillStep;
                float ddx = path.Points[i].WorldX - path.Points[prev].WorldX;
                float ddz = path.Points[i].WorldZ - path.Points[prev].WorldZ;
                distFromDownstream[i] = distFromDownstream[prev] + Mathf.Sqrt(ddx * ddx + ddz * ddz);
                if (i == upstreamIdx) break;
            }

            // Find how far the below-water-level section extends
            float belowWaterExtent = 0f;
            for (int i = downstreamIdx; ; i += uphillStep)
            {
                if (path.Points[i].TerrainY >= waterLevel)
                {
                    belowWaterExtent = distFromDownstream[i];
                    break;
                }
                if (i == upstreamIdx)
                {
                    belowWaterExtent = distFromDownstream[upstreamIdx];
                    break;
                }
            }

            float blendDist = Mathf.Min(OCEAN_BLEND_DISTANCE, belowWaterExtent);

            if (blendDist > 1f)
            {
                for (int i = 0; i < len; i++)
                {
                    var pt = path.Points[i];
                    float dist = distFromDownstream[i];

                    if (pt.TerrainY < waterLevel && dist < blendDist)
                    {
                        float t = dist / blendDist;
                        float terrainSurface = pt.SurfaceY;
                        pt.SurfaceY = Mathf.Lerp(waterLevel, terrainSurface, t * t);
                        pt.SurfaceY = Mathf.Max(pt.SurfaceY, pt.TerrainY + MIN_BED_OFFSET);
                    }

                    path.Points[i] = pt;
                }
            }
        }

        /// <summary>
        /// Computes disk Y for lake points.
        /// 
        /// For heightmap-detected lakes, the fill level is already set by BCHeightmapWaterDetector.
        /// For vanilla lakes, we determine the fill level dynamically:
        ///   1. Sample terrain at the center - this is the lake bed
        ///   2. Sample terrain at each ring point - terrain that rises above center is the bank
        ///   3. Fill level = lowest bank terrain - margin - fill up to the lip
        ///   4. Validate flat fill - if water would spill over >25% of banks, skip it
        ///   5. Only place disks where terrain is below the fill level
        /// 
        /// Lakes, puddles, and springs are only filled if the water level can remain
        /// flat across the entire surface without spilling over a bank (unless running
        /// into another river or stream via combiner overlap).
        /// </summary>
        private static void DropLakeToTerrain(ComputedPath path, float waterLevel)
        {
            if (path.Points == null || path.Points.Length == 0) return;

            // If heightmap mode already set a fill level (SurfaceY > 0 on first point),
            // use that. Otherwise compute from terrain dynamically.
            bool hasHeightmapFill = path.Points[0].SurfaceY > -900f && path.Points[0].SurfaceY > 0f;

            // Step 1: Sample terrain at ALL points first
            for (int i = 0; i < path.Points.Length; i++)
            {
                var pt = path.Points[i];
                pt.TerrainY = GetTerrainHeight(pt.WorldX, pt.WorldZ);
                path.Points[i] = pt;
            }

            float fillLevel;

            if (hasHeightmapFill)
            {
                fillLevel = path.Points[0].SurfaceY;
            }
            else
            {
                // Step 2: Find the center terrain height (lowest point = lake bed)
                float centerTerrainY = path.Points[0].TerrainY;

                // Step 3: Find the bank height by looking at the outermost ring points.
                // The outermost points (last LakeScanRays entries) represent the lake boundary.
                // The lowest bank terrain tells us where water would "spill over" - that's the fill level.
                float lowestBankY = float.MaxValue;
                int outerRingStart = path.Points.Length - LakeScanRays;
                if (outerRingStart < 1) outerRingStart = 1;

                for (int i = outerRingStart; i < path.Points.Length; i++)
                {
                    float ty = path.Points[i].TerrainY;
                    // Only consider points that are ABOVE the center as "bank" terrain
                    if (ty > centerTerrainY + 0.5f && ty < lowestBankY)
                    {
                        lowestBankY = ty;
                    }
                }

                if (lowestBankY < float.MaxValue - 1f)
                {
                    // Fill to the lowest bank lip, but no higher than the global water level
                    // (we don't want to create lakes above sea level that float in the air)
                    // Actually, for elevated inland lakes we DO want to fill above sea level.
                    // The fill level is the bank lip minus a small margin.
                    fillLevel = lowestBankY - 0.3f;
                }
                else
                {
                    // No clear bank found - this is likely a coastal area at sea level.
                    // Use the global water level as fallback.
                    fillLevel = waterLevel;
                }

                // The fill level should be at least above the center terrain
                // (otherwise there's no depression to fill)
                if (fillLevel <= centerTerrainY + 0.2f)
                {
                    // No real depression here - skip this lake entirely
                    path.Points = new PathPoint[0];
                    return;
                }
            }

            // Step 4: Validate flat fill - check ALL banks hold the water.
            // If the fill level would spill over any bank (terrain at bank is below
            // fill level), this isn't a valid containment. Skip unless the spill
            // point connects to a known river/stream (handled via combiner overlap).
            if (!hasHeightmapFill && path.Points.Length > LakeScanRays + 1)
            {
                int spillOuterStart = path.Points.Length - LakeScanRays;
                int spillCount = 0;
                for (int i = spillOuterStart; i < path.Points.Length; i++)
                {
                    if (path.Points[i].TerrainY < fillLevel - 0.5f)
                        spillCount++;
                }
                // If more than 25% of bank samples are below fill level,
                // the depression doesn't contain water - skip it.
                float spillRatio = (float)spillCount / LakeScanRays;
                if (spillRatio > 0.25f)
                {
                    path.Points = new PathPoint[0];
                    return;
                }
            }

            // Step 5: Keep only points where terrain is below the fill level
            var validPoints = new List<PathPoint>();

            for (int i = 0; i < path.Points.Length; i++)
            {
                var pt = path.Points[i];

                // Only place water where terrain is below the fill level
                if (pt.TerrainY < fillLevel - 0.1f)
                {
                    pt.SurfaceY = fillLevel;
                    validPoints.Add(pt);
                }
            }

            path.Points = validPoints.ToArray();
        }

        /// <summary>
        /// Removes points with invalid surface height.
        /// </summary>
        private static PathPoint[] TrimInvalidPoints(PathPoint[] points)
        {
            var valid = new List<PathPoint>();
            foreach (var pt in points)
            {
                if (pt.SurfaceY > -900f)
                    valid.Add(pt);
            }
            return valid.ToArray();
        }

        #endregion

        #region Duplicate Detection (Main Thread)

        /// <summary>
        /// Checks if water already exists along a computed path.
        /// Samples a few points along the path endpoints and midpoint.
        /// MUST be called on main thread (accesses WaterMeshCombiner).
        /// </summary>
        private static bool HasExistingWaterAlongPath(ComputedPath path)
        {
            if (path.Points == null || path.Points.Length == 0) return false;

            // Check 3 positions: start, middle, end
            int[] checkIndices = { 0, path.Points.Length / 2, path.Points.Length - 1 };
            int hits = 0;

            foreach (int idx in checkIndices)
            {
                if (idx >= path.Points.Length) continue;

                var pt = path.Points[idx];
                Vector3 worldPos = new Vector3(pt.WorldX, pt.SurfaceY, pt.WorldZ);

                var existingCombiner = WaterMeshCombiner.GetCombinerAt(worldPos);
                if (existingCombiner != null)
                {
                    hits++;
                }
            }

            // If 2 or more sample points already have water, this path is a duplicate
            return hits >= 2;
        }

        #endregion
    }
}
