using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Async;


namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Reads Better Continents' heightmap image directly via reflection and uses the
    /// EXACT SAME height formula BC uses to convert pixel values -> world terrain Y.
    /// 
    /// KEY INSIGHT: We read BC's ImageMapFloat.Map (the same float[] that GetValue()
    /// bilinear-interpolates from) and apply BC's own height pipeline formula:
    ///   worldY = (pixelValue * HeightmapAmount - 0.15 + SeaLevelAdjustment) * 200
    /// 
    /// This gives us EXACT terrain heights at every pixel - identical to what BC's
    /// GetBiomeHeight postfix returns for HeightmapOverrideAll mode. We use these for:
    /// - Depression detection: find channels painted into the heightmap at ANY elevation
    /// - XZ positions and widths: pixel-accurate channel geometry
    /// - Primary terrain heights: the heightmap IS the ground truth
    /// 
    /// AutoWaterGenerator then cross-references these heights against real terrain
    /// sampling (WorldGenerator.GetHeight) as a safety net, using min(heightmapY, realY)
    /// so water NEVER floats above the actual terrain surface. This catches any drift
    /// from BaseHeightNoise layers or other BC postfixes beyond the base heightmap.
    /// 
    /// ALGORITHM (direct height + depression hybrid):
    /// 1. Read BC's ImageMapFloat.Map (float[], 0-1 grayscale) + Size via reflection
    /// 2. Convert each pixel to world Y using BC's exact formula
    /// 3. Compute neighborhood averages for relative depression detection
    ///    (catches elevated rivers above sea level where absolute threshold fails)
    /// 4. Connected depressed/underwater regions = water bodies
    /// 5. Classify by shape, trace centerlines with exact heights from the heightmap
    /// 6. Heights cross-referenced in AutoWaterGenerator with real terrain sampling
    /// 
    /// COORDINATE SYSTEM (how a BC height map lays over the world, as observed in its saves and in game):
    ///   normalized = clamp(worldCoord / (TotalRadius * 2) + 0.5, 0, 1)
    ///   pixel = normalized * (Size - 1)
    ///   Map[y * Size + x], row-major, image flipped vertically on load
    ///
    /// HEIGHT (a height map that overrides everything, default blend/add/mask):
    ///   the base height is pixelValue * HeightmapAmount - 0.15 + SeaLevelAdjustment, and the ground is that x 200
    ///   (FAT's own reproduction: BcImport\BcWorldGen, proven against BC by Tools\BcRefTest).
    ///   At default settings (Amount=1, SeaLevelAdj=0):
    ///     worldY = (pixelValue - 0.15) * 200 = pixelValue * 200 - 30
    ///     Sea level (worldY=30) = pixelValue 0.30
    /// 
    /// THREAD SAFETY:
    /// - ReadHeightmap() copies the float[] once on main thread (reflection)
    /// - All analysis methods operate on the copied snapshot - background thread safe
    /// - Output is List&lt;DetectedWaterBody&gt; consumed by AutoWaterGenerator
    /// </summary>
    public static class BCHeightmapWaterDetector
    {
        #region Configuration

        /// <summary>
        /// Neighborhood radius in pixels for computing local average height.
        /// Used for relative depression detection to catch elevated rivers above sea level.
        /// 
        /// AUTO-SCALED: When DetectWaterBodies() runs, this is automatically scaled
        /// proportionally to the snapshot resolution so the physical neighborhood
        /// size (~160m) stays constant regardless of grid resolution:
        ///   1024px -> 8,  2048px -> 16,  4096px -> 32,  8192px -> 64,  20480px -> 160
        /// 
        /// This value is the base for a 2048px map. Set to 0 to disable auto-scaling.
        /// </summary>
        public static int NeighborhoodRadius = 16;

        /// <summary>
        /// Minimum depression depth (in normalized 0-1 height units) for a pixel to
        /// qualify as part of a water feature relative to its neighborhood average.
        /// This catches rivers at ANY elevation - not just below sea level.
        /// 0.005 = ~1m at 200m scale. 0.008 = ~1.6m. 0.01 = ~2m.
        /// </summary>
        public static float MinDepressionDepth = 0.008f;

        /// <summary>
        /// Minimum number of connected depressed pixels to form a water body.
        /// Filters out noise specks. This is the base for a 2048px map.
        /// AUTO-SCALED proportionally to (size/2048)^2 in DetectWaterBodies()
        /// to maintain ~1000 sq meter physical area threshold at any resolution.
        /// </summary>
        public static int MinWaterBodyPixels = 10;

        /// <summary>
        /// Maximum number of water bodies to detect (safety limit).
        /// </summary>
        public static int MaxWaterBodies = 5000;

        /// <summary>
        /// Aspect ratio threshold to classify a water body as a river vs lake.
        /// River = elongated (aspect ratio > this). Lake = roughly circular.
        /// </summary>
        public static float RiverAspectRatio = 2.5f;

        /// <summary>
        /// Step size along the channel skeleton for generating path points (in pixels).
        /// 1 = every pixel sampled for maximum heightmap resolution. Sub-pixel interpolation
        /// in TraceCenterline then densifies to ~2m world-space spacing for smooth flow.
        /// </summary>
        public static int ChannelTraceStep = 1;

        /// <summary>
        /// Maximum number of pixels a single water body can have before being split
        /// into smaller strips. At 20480x20480, a body with 2M pixels covers ~20 km^2.
        /// Bodies larger than this are split along their longer bounding-box axis into
        /// overlapping strips, each containing at most MaxPixelsPerBody pixels.
        /// Each strip is then classified and traced independently.
        /// </summary>
        public static int MaxPixelsPerBody = 2000000;

        /// <summary>
        /// Maximum time in milliseconds to spend on a single water body in Step 4.
        /// If a body exceeds this, its tracing is aborted and it's skipped.
        /// Default 30 seconds - most bodies finish in under 1 second.
        /// </summary>
        public static long MaxMillisecondsPerBody = 30000;

        #endregion

        #region Reflection Cache

        private static bool _reflectionInitialized;
        private static bool _canReadHeightmap;
        private static FieldInfo _bcSettingsField;
        private static FieldInfo _heightmapField;        // BetterContinentsSettings.HeightMap (ImageMapFloat)
        private static FieldInfo _imageMapField;          // ImageMapFloat.Map (float[])
        private static FieldInfo _imageMapSizeField;      // ImageMapBase.Size (int)
        private static FieldInfo _heightmapAmountField;   // BetterContinentsSettings.HeightmapAmount (float)
        private static FieldInfo _seaLevelField;          // BetterContinentsSettings.SeaLevelAdjustment (float)

        // BC stores world size on the main BetterContinents class as static fields
        private static FieldInfo _bcWorldRadiusField;
        private static FieldInfo _bcTotalRadiusField;

        private static void EnsureReflectionInitialized()
        {
            if (_reflectionInitialized) return;
            _reflectionInitialized = true;
            _canReadHeightmap = false;

            try
            {
                var bcType = Type.GetType("BetterContinents.BetterContinents, BetterContinents");
                if (bcType == null)
                {
                    FiresLogger.LogInfo("[BCHeightmapWaterDetector] BetterContinents not loaded");
                    return;
                }

                _bcSettingsField = AccessTools.Field(bcType, "Settings");
                if (_bcSettingsField == null)
                {
                    FiresLogger.LogWarning("[BCHeightmapWaterDetector] Could not find BetterContinents.Settings");
                    return;
                }

                // World size fields - BC has static float fields on the main class
                _bcWorldRadiusField = AccessTools.Field(bcType, "WorldRadius");
                _bcTotalRadiusField = AccessTools.Field(bcType, "TotalRadius");

                var settingsType = _bcSettingsField.FieldType;

                // HeightMap field - BC v0.7.30 uses "HeightMap" (private ImageMapFloat)
                _heightmapField = AccessTools.Field(settingsType, "HeightMap");
                if (_heightmapField == null)
                    _heightmapField = AccessTools.Field(settingsType, "Heightmap");
                if (_heightmapField == null)
                    _heightmapField = AccessTools.Field(settingsType, "heightmap");

                if (_heightmapField == null)
                {
                    FiresLogger.LogWarning("[BCHeightmapWaterDetector] Could not find heightmap field on BC Settings");
                    return;
                }

                _heightmapAmountField = AccessTools.Field(settingsType, "HeightmapAmount");
                _seaLevelField = AccessTools.Field(settingsType, "SeaLevelAdjustment");

                // Where an installed BC keeps its height map at run time (field names only, read by reflection):
                //   Map  - the pixels, a float[] on the map object
                //   Size - its edge length, on the map's base class
                var imageMapType = _heightmapField.FieldType;
                if (imageMapType.IsGenericType && imageMapType.GetGenericTypeDefinition() == typeof(Nullable<>))
                    imageMapType = Nullable.GetUnderlyingType(imageMapType);

                if (imageMapType != null)
                {
                    _imageMapField = AccessTools.Field(imageMapType, "Map");
                    if (_imageMapField == null)
                        _imageMapField = AccessTools.Field(imageMapType, "map");

                    // Size is inherited from ImageMapBase
                    _imageMapSizeField = AccessTools.Field(imageMapType, "Size");
                    if (_imageMapSizeField == null)
                    {
                        // Try parent class
                        var baseType = imageMapType.BaseType;
                        if (baseType != null)
                            _imageMapSizeField = AccessTools.Field(baseType, "Size");
                    }
                }

                if (_imageMapField != null && _imageMapSizeField != null)
                {
                    _canReadHeightmap = true;
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Reflection initialized - can read BC heightmap. " +
                        $"MapField={_imageMapField.Name} ({_imageMapField.DeclaringType.Name}), " +
                        $"SizeField={_imageMapSizeField.Name} ({_imageMapSizeField.DeclaringType.Name})");
                }
                else
                {
                    FiresLogger.LogWarning($"[BCHeightmapWaterDetector] Could not find ImageMapFloat internals. " +
                        $"MapField={((_imageMapField != null) ? _imageMapField.Name : "null")}, " +
                        $"SizeField={((_imageMapSizeField != null) ? _imageMapSizeField.Name : "null")}");
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BCHeightmapWaterDetector] Reflection init failed: {ex.Message}");
            }
        }

        /// <summary>Whether we can read BC's heightmap data.</summary>
        public static bool CanReadHeightmap
        {
            get
            {
                EnsureReflectionInitialized();
                return _canReadHeightmap;
            }
        }

        #endregion

        #region Heightmap Snapshot

        /// <summary>
        /// Thread-safe snapshot of BC's heightmap data + conversion parameters.
        /// </summary>
        public class HeightmapSnapshot
        {
            /// <summary>
            /// Heightmap float values, row-major: Map[y * Size + x].
            /// When IsWorldHeightCache=false: normalized 0-1 from BC's ImageMapFloat.
            /// When IsWorldHeightCache=true: actual world Y heights from WorldGenerator.GetHeight().
            /// </summary>
            public float[] Map;
            /// <summary>Square image dimensions (e.g. 2048, 4096).</summary>
            public int Size;
            /// <summary>Playable world radius in meters (default 10000).</summary>
            public float WorldRadius;
            /// <summary>Total radius including edge buffer (default 10500).</summary>
            public float TotalRadius;
            /// <summary>Height multiplier from BC config (default 1.0).</summary>
            public float HeightmapAmount;
            /// <summary>Sea level adjustment from BC config (default 0.0 at slider 0.5).</summary>
            public float SeaLevelAdjustment;
            /// <summary>
            /// When true, Map[] contains actual world Y heights sampled from
            /// WorldGenerator.GetHeight() - the FINAL terrain including all BC
            /// modifications (roughmap, flatmap, noise layers, river carving, etc.).
            /// HeightmapToWorldY() returns the value as-is (identity passthrough).
            /// Depression thresholds are in meters, not normalized 0-1.
            /// </summary>
            public bool IsWorldHeightCache;
        }

        /// <summary>
        /// Reads BC's heightmap into a thread-safe snapshot.
        /// MUST be called on the main thread (reflection on live objects).
        /// Returns null if BC is not loaded or has no heightmap.
        /// </summary>
        public static HeightmapSnapshot ReadHeightmap()
        {
            EnsureReflectionInitialized();
            if (!_canReadHeightmap) return null;

            try
            {
                var settings = _bcSettingsField.GetValue(null);
                if (settings == null) return null;

                var heightmapObj = _heightmapField.GetValue(settings);
                if (heightmapObj == null) return null;

                var mapArray = _imageMapField.GetValue(heightmapObj) as float[];
                if (mapArray == null || mapArray.Length == 0) return null;

                int size;
                var sizeObj = _imageMapSizeField.GetValue(heightmapObj);
                if (sizeObj is int si)
                    size = si;
                else
                    size = (int)Math.Sqrt(mapArray.Length);

                if (size < 32 || size > 16384)
                {
                    FiresLogger.LogWarning($"[BCHeightmapWaterDetector] Unexpected heightmap size: {size}");
                    return null;
                }

                // Copy the array for thread safety
                float[] mapCopy = new float[mapArray.Length];
                Array.Copy(mapArray, mapCopy, mapArray.Length);

                // Read world size - BC static fields: WorldRadius=10000, TotalRadius=10500. Without them, the world's own
                // size (0.2.721: FatWorldSize, 10000 / 10500 on a vanilla-size world).
                float worldRadius = VerdantsAscent.Terrain.Voxel.FatWorldSize.Radius;
                float totalRadius = VerdantsAscent.Terrain.Voxel.FatWorldSize.EdgeRadius;

                if (_bcWorldRadiusField != null)
                {
                    var wrObj = _bcWorldRadiusField.GetValue(null);
                    if (wrObj is float wrf) worldRadius = wrf;
                }
                if (_bcTotalRadiusField != null)
                {
                    var trObj = _bcTotalRadiusField.GetValue(null);
                    if (trObj is float trf) totalRadius = trf;
                }
                if (totalRadius < worldRadius) totalRadius = worldRadius + 500f;

                float heightmapAmount = 1f;
                if (_heightmapAmountField != null)
                {
                    var haObj = _heightmapAmountField.GetValue(settings);
                    if (haObj is float haf) heightmapAmount = haf;
                }

                float seaLevelAdj = 0f;
                if (_seaLevelField != null)
                {
                    var slObj = _seaLevelField.GetValue(settings);
                    if (slObj is float slf) seaLevelAdj = slf;
                }

                // Compute diagnostic values using BC's exact formula
                float seaLevelPixel = (30f / 200f + 0.15f - seaLevelAdj) / heightmapAmount;
                float testWorldY_at_030 = HeightmapToWorldY_Static(0.30f, heightmapAmount, seaLevelAdj);
                float testWorldY_at_015 = HeightmapToWorldY_Static(0.15f, heightmapAmount, seaLevelAdj);

                FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Read heightmap: {size}x{size}, " +
                    $"worldRadius={worldRadius}, totalRadius={totalRadius}, " +
                    $"heightmapAmount={heightmapAmount}, seaLevelAdj={seaLevelAdj:F4}, " +
                    $"seaLevelPixelThreshold={seaLevelPixel:F4}, " +
                    $"pixel0.15?Y={testWorldY_at_015:F1}, pixel0.30?Y={testWorldY_at_030:F1}");

                return new HeightmapSnapshot
                {
                    Map = mapCopy,
                    Size = size,
                    WorldRadius = worldRadius,
                    TotalRadius = totalRadius,
                    HeightmapAmount = heightmapAmount,
                    SeaLevelAdjustment = seaLevelAdj
                };
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BCHeightmapWaterDetector] Failed to read heightmap: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Coordinate & Height Conversion

        /// <summary>
        /// Converts heightmap pixel coordinates to world XZ coordinates.
        /// Inverse of BC's Normalize(): worldCoord = (normalized - 0.5) * TotalSize
        /// </summary>
        public static void PixelToWorld(int px, int py, HeightmapSnapshot snap, out float worldX, out float worldZ)
        {
            float nx = (float)px / (snap.Size - 1);
            float ny = (float)py / (snap.Size - 1);
            float totalSize = snap.TotalRadius * 2f;
            worldX = (nx - 0.5f) * totalSize;
            worldZ = (ny - 0.5f) * totalSize;
        }

        /// <summary>
        /// Converts a raw heightmap pixel value (0-1) to world Y height, as a BC world's ground comes out when its
        /// height map overrides everything with the default blend (1), add (0) and mask (0):
        ///   worldY = (pixelValue * HeightmapAmount - 0.15 + SeaLevelAdjustment) * 200
        /// (the map scaled by its amount, the format's 0.15 water line and the sea-level setting, in Valheim's x200 units).
        /// </summary>
        public static float HeightmapToWorldY(float normalizedHeight, HeightmapSnapshot snap)
        {
            if (snap.IsWorldHeightCache)
                return normalizedHeight; // Map[] already contains world Y
            return (normalizedHeight * snap.HeightmapAmount - 0.15f + snap.SeaLevelAdjustment) * 200f;
        }

        /// <summary>Static version for use before snapshot is constructed.</summary>
        private static float HeightmapToWorldY_Static(float normalizedHeight, float heightmapAmount, float seaLevelAdj)
        {
            return (normalizedHeight * heightmapAmount - 0.15f + seaLevelAdj) * 200f;
        }

        /// <summary>
        /// Inverse: converts world Y back to the heightmap pixel value (0-1).
        /// </summary>
        public static float WorldYToHeightmap(float worldY, HeightmapSnapshot snap)
        {
            if (snap.IsWorldHeightCache)
                return worldY; // Map[] already contains world Y
            return (worldY / 200f + 0.15f - snap.SeaLevelAdjustment) / snap.HeightmapAmount;
        }

        /// <summary>
        /// Samples the heightmap value at a pixel coordinate (clamped to bounds).
        /// Returns raw normalized height (0-1).
        /// </summary>
        public static float SampleHeightmapAt(int px, int py, HeightmapSnapshot snap)
        {
            if (px < 0) px = 0;
            if (py < 0) py = 0;
            if (px >= snap.Size) px = snap.Size - 1;
            if (py >= snap.Size) py = snap.Size - 1;
            return snap.Map[py * snap.Size + px];
        }

        /// <summary>
        /// Converts world XZ coordinates to heightmap pixel coordinates.
        /// Matches BC's Normalize(): normalized = clamp(worldCoord / TotalSize + 0.5, 0, 1)
        /// </summary>
        public static void WorldToPixel(float worldX, float worldZ, HeightmapSnapshot snap, out int px, out int py)
        {
            float totalSize = snap.TotalRadius * 2f;
            float nx = (worldX / totalSize) + 0.5f;
            float nz = (worldZ / totalSize) + 0.5f;
            px = Mathf.Clamp(Mathf.RoundToInt(nx * (snap.Size - 1)), 0, snap.Size - 1);
            py = Mathf.Clamp(Mathf.RoundToInt(nz * (snap.Size - 1)), 0, snap.Size - 1);
        }

        /// <summary>Returns world-space meters per pixel for the heightmap.</summary>
        public static float MetersPerPixel(HeightmapSnapshot snap)
        {
            return (snap.TotalRadius * 2f) / snap.Size;
        }

        #endregion

        #region Water Body Detection (Background Thread Safe)

        /// <summary>
        /// Result of water body detection - a connected region of water pixels.
        /// </summary>
        public class DetectedWaterBody
        {
            public WaterBodyType Type;
            public List<int> PixelXs;
            public List<int> PixelYs;
            public List<float> DepressionDepths;
            public int MinPX, MaxPX, MinPY, MaxPY;
            public List<AutoWaterGenerator.PathPoint> PathPoints;
            public float AvgDepression;
            public float MaxDepression;
        }

        public enum WaterBodyType
        {
            River,
            Stream,
            Lake,
            /// <summary>
            /// Small standing water body (tar-pit sized, ~20-200m across).
            /// Same flat-fill logic as lakes but shallower (default 3m max depth).
            /// </summary>
            Pond,
            /// <summary>
            /// Tiny standing water body (puddle sized, &lt;20m across).
            /// Appears at river/stream origin points or in small terrain depressions.
            /// Very shallow (default 0.5m max depth), 1-3 disks.
            /// </summary>
            Spring,
            Unknown
        }

        /// <summary>
        /// Main detection pipeline. ALL PURE MATH - background thread safe.
        /// 
        /// Uses the heightmap grayscale VALUES directly to compute exact terrain heights,
        /// then combines two detection strategies:
        /// 
        /// A) Relative depression analysis (neighborhood average comparison):
        ///    Finds channels that are lower than their surroundings regardless of absolute
        ///    elevation. This catches mountain rivers, highland streams, etc.
        /// 
        /// B) Both strategies feed into the same flood-fill + classification pipeline.
        /// 
        /// Heights computed from the heightmap use BC's exact formula:
        ///   worldY = (pixelValue * HeightmapAmount - 0.15 + SeaLevelAdjustment) * 200
        /// 
        /// CENTER-OUT ORDERING:
        /// Water bodies are sorted by distance from world center before being returned.
        /// This ensures rivers near the player spawn (world center) are generated first,
        /// and if generation limits are hit, edge-of-world rivers are trimmed - not
        /// the important central ones.
        /// </summary>
        public static List<DetectedWaterBody> DetectWaterBodies(HeightmapSnapshot snap)
        {
            if (snap == null || snap.Map == null || snap.Size < 32)
                return new List<DetectedWaterBody>();

            int size = snap.Size;
            float[] map = snap.Map;

            // Stopwatch for periodic progress logging (~30s intervals)
            var progressSW = System.Diagnostics.Stopwatch.StartNew();
            long lastProgressMs = 0;
            const long PROGRESS_INTERVAL_MS = 30000;

            // Auto-scale neighborhood radius proportionally to resolution.
            // Base: 16px at 2048px = ~160m physical neighborhood.
            // At 4096px -> 32px, at 8192px -> 64px. Same physical area.
            int scaledNeighborhoodRadius = NeighborhoodRadius;
            if (NeighborhoodRadius > 0)
            {
                scaledNeighborhoodRadius = Math.Max(2, (int)(NeighborhoodRadius * (size / 2048f)));
            }

            // Auto-scale minimum water body pixel count proportionally to area.
            // At 2048px, 10 pixels * ~100m^2/pixel = ~1000m^2.
            // At 8192px, 160 pixels * ~6.25m^2/pixel = ~1000m^2.
            float resScale = size / 2048f;
            int scaledMinBodyPixels = Math.Max(3, (int)(MinWaterBodyPixels * resScale * resScale));

            // Step 0: Build circular world mask to exclude edge garbage
            MainThreadDispatcher.Enqueue(() =>
                FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 0/{size}x{size}: Building world mask..."));

            bool[] worldMask = new bool[size * size];
            float halfSize = (size - 1) * 0.5f;
            float totalSize = snap.TotalRadius * 2f;
            float worldRadiusPixels = snap.WorldRadius / (totalSize / size);
            float worldRadiusSq = worldRadiusPixels * worldRadiusPixels;

            for (int y = 0; y < size; y++)
            {
                float dy = y - halfSize;
                for (int x = 0; x < size; x++)
                {
                    float dx = x - halfSize;
                    if (dx * dx + dy * dy <= worldRadiusSq)
                        worldMask[y * size + x] = true;
                }
            }

            {
                long elapsed = progressSW.ElapsedMilliseconds;
                MainThreadDispatcher.Enqueue(() =>
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 0 complete: world mask built ({elapsed}ms)"));
            }

            // Step 1: Compute neighborhood average via separable box blur
            {
                long elapsed = progressSW.ElapsedMilliseconds;
                int blurRadius = Math.Max(2, scaledNeighborhoodRadius);
                MainThreadDispatcher.Enqueue(() =>
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 1/{size}x{size}: Box blur (radius={blurRadius}, {(long)size * size * 2} ops)... this is the slowest step"));
            }
            float[] neighborhoodAvg = BoxBlur2Pass(map, size, Math.Max(2, scaledNeighborhoodRadius), progressSW, PROGRESS_INTERVAL_MS);

            {
                long elapsed = progressSW.ElapsedMilliseconds;
                MainThreadDispatcher.Enqueue(() =>
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 1 complete: box blur done ({elapsed}ms)"));
            }

            // Step 2: Find depressed pixels - lower than neighborhood average by threshold
            // When using world height cache, Map[] values are in meters (world Y) so
            // depression differences are already in meters. The normalized threshold
            // (0.008 ~ 1.6m at 200m height scale) must be converted to meters.
            // When using BC heightmap, values are 0-1 normalized and the threshold
            // is used directly.
            float effectiveDepressionThreshold = snap.IsWorldHeightCache
                ? MinDepressionDepth * snap.HeightmapAmount * 200f  // Convert to meters
                : MinDepressionDepth;

            MainThreadDispatcher.Enqueue(() =>
                FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 2: Finding depressed pixels (threshold={effectiveDepressionThreshold:F4})..."));

            bool[] isDepressed = new bool[size * size];
            float[] depressionMap = new float[size * size];
            int depressedCount = 0;

            for (int i = 0; i < size * size; i++)
            {
                if (!worldMask[i]) continue;

                float depth = neighborhoodAvg[i] - map[i];
                if (depth > effectiveDepressionThreshold)
                {
                    isDepressed[i] = true;
                    depressionMap[i] = depth;
                    depressedCount++;
                }
            }

            {
                long elapsed = progressSW.ElapsedMilliseconds;
                int dc = depressedCount;
                MainThreadDispatcher.Enqueue(() =>
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 2 complete: {dc} depressed pixels found ({elapsed}ms)"));
            }

            if (depressedCount == 0)
                return new List<DetectedWaterBody>();

            // Step 3: Flood-fill connected depressed regions
            {
                long elapsed = progressSW.ElapsedMilliseconds;
                int dc = depressedCount;
                int smbp = scaledMinBodyPixels;
                MainThreadDispatcher.Enqueue(() =>
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 3: Flood-filling {dc} depressed pixels (minBody={smbp})..."));
            }
            var waterBodies = FloodFillRegions(isDepressed, depressionMap, size, scaledMinBodyPixels, progressSW, PROGRESS_INTERVAL_MS);

            {
                long elapsed = progressSW.ElapsedMilliseconds;
                int wbc = waterBodies.Count;
                MainThreadDispatcher.Enqueue(() =>
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 3 complete: {wbc} water bodies found ({elapsed}ms)"));
            }

            // Step 3.5: Sort water bodies center-out - prioritize rivers near world center
            // so that if MaxWaterBodies or MaxPaths clips the list, edge rivers are
            // trimmed rather than the important central ones.
            float pixelCenter = halfSize;
            waterBodies.Sort((a, b) =>
            {
                float aCenterPX = (a.MinPX + a.MaxPX) * 0.5f;
                float aCenterPY = (a.MinPY + a.MaxPY) * 0.5f;
                float aDist = (aCenterPX - pixelCenter) * (aCenterPX - pixelCenter) +
                              (aCenterPY - pixelCenter) * (aCenterPY - pixelCenter);

                float bCenterPX = (b.MinPX + b.MaxPX) * 0.5f;
                float bCenterPY = (b.MinPY + b.MaxPY) * 0.5f;
                float bDist = (bCenterPX - pixelCenter) * (bCenterPX - pixelCenter) +
                              (bCenterPY - pixelCenter) * (bCenterPY - pixelCenter);

                return aDist.CompareTo(bDist);
            });

            // Step 4: Classify and generate path points with EXACT heights from heightmap
            {
                int wbc = waterBodies.Count;
                MainThreadDispatcher.Enqueue(() =>
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 4: Classifying and tracing {wbc} water bodies..."));
            }
            float mpp = MetersPerPixel(snap);
            long step4LastProgressMs = progressSW.ElapsedMilliseconds;
            int step4Processed = 0;
            int step4TimedOut = 0;

            // Collect any fallback water bodies produced when lakes fail
            var fallbackBodies = new List<DetectedWaterBody>();
            int step4Split = 0;

            foreach (var wb in waterBodies)
            {
                int bodyPixelCount = wb.PixelXs.Count;
                long bodyStartMs = progressSW.ElapsedMilliseconds;

                ClassifyWaterBody(wb, mpp);
                step4Processed++;

                // SPLIT oversized water bodies into manageable strips.
                // Instead of skipping 18km rivers, split them along the longer
                // bounding-box axis into overlapping strips that each fit within
                // MaxPixelsPerBody. Each strip is classified and traced independently.
                if (bodyPixelCount > MaxPixelsPerBody)
                {
                    step4Split++;
                    int bboxW = wb.MaxPX - wb.MinPX + 1;
                    int bboxH = wb.MaxPY - wb.MinPY + 1;
                    float bboxMetersW = bboxW * mpp;
                    float bboxMetersH = bboxH * mpp;

                    var strips = SplitOversizedBody(wb, map, snap, mpp, depressionMap, scaledMinBodyPixels);

                    int splitNum = step4Split;
                    int pxCount = bodyPixelCount;
                    int stripCount = strips != null ? strips.Count : 0;
                    string typeName = wb.Type.ToString();
                    MainThreadDispatcher.Enqueue(() =>
                        FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 4: SPLIT oversized {typeName} #{splitNum} " +
                            $"({pxCount} pixels, {bboxMetersW:F0}x{bboxMetersH:F0}m) into {stripCount} strips"));

                    if (strips != null && strips.Count > 0)
                        fallbackBodies.AddRange(strips);
                    continue;
                }

                // Periodic progress logging (~30s intervals) with per-body details
                if (progressSW.ElapsedMilliseconds - step4LastProgressMs >= PROGRESS_INTERVAL_MS)
                {
                    step4LastProgressMs = progressSW.ElapsedMilliseconds;
                    int processed = step4Processed;
                    int total = waterBodies.Count;
                    int split = step4Split;
                    int timedOut = step4TimedOut;
                    long elapsed = progressSW.ElapsedMilliseconds;
                    int pxCount = bodyPixelCount;
                    string typeName = wb.Type.ToString();
                    MainThreadDispatcher.Enqueue(() =>
                        FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Step 4 progress: {processed}/{total} bodies " +
                            $"(split={split}, timedOut={timedOut}), current={typeName} {pxCount}px ({elapsed}ms)"));
                }

                if (wb.Type == WaterBodyType.River || wb.Type == WaterBodyType.Stream)
                {
                    wb.PathPoints = TraceCenterline(wb, map, snap, mpp, depressionMap);

                    // Check per-body timeout
                    long bodyElapsed = progressSW.ElapsedMilliseconds - bodyStartMs;
                    if (bodyElapsed > MaxMillisecondsPerBody)
                    {
                        step4TimedOut++;
                        int pxCount = bodyPixelCount;
                        long be = bodyElapsed;
                        MainThreadDispatcher.Enqueue(() =>
                            FiresLogger.LogWarning($"[BCHeightmapWaterDetector] Step 4: Body took {be}ms ({pxCount} pixels) - continuing"));
                    }

                    // AUTO-GENERATE SPRINGS at the origin point of each river/stream.
                    // The origin is the highest elevation end - where water emerges
                    // from the ground. A small puddle at the headwaters looks natural.
                    if (wb.PathPoints != null && wb.PathPoints.Count >= 2)
                    {
                        var firstPt = wb.PathPoints[0];
                        var lastPt = wb.PathPoints[wb.PathPoints.Count - 1];

                        // Origin = whichever end is higher
                        var originPt = (firstPt.TerrainY >= lastPt.TerrainY) ? firstPt : lastPt;

                        float springRadius = Math.Min(originPt.Width * 0.5f, 8f);
                        if (springRadius < 3f) springRadius = 3f;

                        var springBody = new DetectedWaterBody
                        {
                            Type = WaterBodyType.Spring,
                            PixelXs = new List<int>(),
                            PixelYs = new List<int>(),
                            DepressionDepths = new List<float>(),
                            MinPX = 0,
                            MaxPX = 0,
                            MinPY = 0,
                            MaxPY = 0,
                            AvgDepression = wb.AvgDepression * 0.3f,
                            MaxDepression = wb.MaxDepression * 0.3f,
                            PathPoints = new List<AutoWaterGenerator.PathPoint>
                            {
                                new AutoWaterGenerator.PathPoint
                                {
                                    WorldX = originPt.WorldX,
                                    WorldZ = originPt.WorldZ,
                                    Width = springRadius * 2f,
                                    TerrainY = originPt.TerrainY,
                                    SurfaceY = originPt.SurfaceY,
                                    DepressionDepth = originPt.DepressionDepth * 0.3f,
                                    BankY = originPt.BankY
                                }
                            }
                        };
                        fallbackBodies.Add(springBody);
                    }
                }
                else if (wb.Type == WaterBodyType.Lake || wb.Type == WaterBodyType.Pond)
                {
                    // Lakes and ponds use the same flat-fill logic.
                    // Ponds are just smaller - GenerateLakePoints handles both.
                    wb.PathPoints = GenerateLakePoints(wb, map, snap, mpp);

                    // Check per-body timeout
                    long bodyElapsed = progressSW.ElapsedMilliseconds - bodyStartMs;
                    if (bodyElapsed > MaxMillisecondsPerBody)
                    {
                        step4TimedOut++;
                        int pxCount = bodyPixelCount;
                        long be = bodyElapsed;
                        MainThreadDispatcher.Enqueue(() =>
                            FiresLogger.LogWarning($"[BCHeightmapWaterDetector] Step 4: Lake/Pond took {be}ms ({pxCount} pixels) - skipping decompose"));
                        // Skip the expensive decompose step for timed-out bodies
                        continue;
                    }

                    // LAKE FALLBACK: If a lake produced 0 points (no clear depression,
                    // fill level too low, etc.), re-analyze its pixel region for
                    // elongated sub-features (river channels) or small depressions
                    // (ponds) that can be placed instead. The lake's pixels contain
                    // real depressed terrain - the failure was in finding a single
                    // fill level, not in the depression itself.
                    // Only attempt decompose for reasonably-sized lakes to avoid
                    // creating huge HashSets and bool arrays for massive bodies.
                    if ((wb.PathPoints == null || wb.PathPoints.Count == 0) && wb.PixelXs.Count >= scaledMinBodyPixels
                        && wb.PixelXs.Count <= MaxPixelsPerBody / 2)
                    {
                        var fallbacks = DecomposeLakeIntoSubFeatures(wb, map, snap, mpp, depressionMap, scaledMinBodyPixels);
                        if (fallbacks != null && fallbacks.Count > 0)
                            fallbackBodies.AddRange(fallbacks);
                    }
                }
                else if (wb.Type == WaterBodyType.Spring)
                {
                    // Springs detected from heightmap: tiny depressions (<20m across).
                    // Use GenerateLakePoints - it produces a small flat-fill grid.
                    wb.PathPoints = GenerateLakePoints(wb, map, snap, mpp);
                }
            }

            // Add fallback bodies to the result
            if (fallbackBodies.Count > 0)
                waterBodies.AddRange(fallbackBodies);

            {
                long elapsed = progressSW.ElapsedMilliseconds;
                int wbc = waterBodies.Count;
                int fbc = fallbackBodies.Count;
                int split = step4Split;
                int timedOut = step4TimedOut;
                MainThreadDispatcher.Enqueue(() =>
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Detection complete: {wbc} water bodies ({fbc} fallbacks, " +
                        $"{split} split oversized, {timedOut} timed out) in {elapsed}ms"));
            }

            return waterBodies;
        }

        /// <summary>
        /// Two-pass separable box blur. O(n) per pixel regardless of radius.
        /// </summary>
        private static float[] BoxBlur2Pass(float[] input, int size, int radius,
            System.Diagnostics.Stopwatch progressSW = null, long progressIntervalMs = 30000)
        {
            float[] temp = new float[size * size];
            float[] output = new float[size * size];
            long lastProgressMs = progressSW != null ? progressSW.ElapsedMilliseconds : 0;

            // Horizontal pass
            for (int y = 0; y < size; y++)
            {
                float sum = 0f;
                int count = 0;
                int rowOffset = y * size;

                for (int x = 0; x <= radius && x < size; x++)
                {
                    sum += input[rowOffset + x];
                    count++;
                }
                temp[rowOffset] = sum / count;

                for (int x = 1; x < size; x++)
                {
                    int addX = x + radius;
                    if (addX < size) { sum += input[rowOffset + addX]; count++; }
                    int removeX = x - radius - 1;
                    if (removeX >= 0) { sum -= input[rowOffset + removeX]; count--; }
                    temp[rowOffset + x] = sum / count;
                }

                // Periodic progress during horizontal pass
                if (progressSW != null && (y & 0xFF) == 0) // Check every 256 rows
                {
                    long now = progressSW.ElapsedMilliseconds;
                    if (now - lastProgressMs >= progressIntervalMs)
                    {
                        lastProgressMs = now;
                        int row = y;
                        int totalRows = size;
                        MainThreadDispatcher.Enqueue(() =>
                            FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Box blur horizontal pass: {row}/{totalRows} rows ({now}ms)"));
                    }
                }
            }

            if (progressSW != null)
            {
                long elapsed = progressSW.ElapsedMilliseconds;
                MainThreadDispatcher.Enqueue(() =>
                    FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Box blur horizontal pass complete ({elapsed}ms), starting vertical pass..."));
            }

            // Vertical pass
            for (int x = 0; x < size; x++)
            {
                float sum = 0f;
                int count = 0;

                for (int y = 0; y <= radius && y < size; y++)
                {
                    sum += temp[y * size + x];
                    count++;
                }
                output[x] = sum / count;

                for (int y = 1; y < size; y++)
                {
                    int addY = y + radius;
                    if (addY < size) { sum += temp[addY * size + x]; count++; }
                    int removeY = y - radius - 1;
                    if (removeY >= 0) { sum -= temp[removeY * size + x]; count--; }
                    output[y * size + x] = sum / count;
                }

                // Periodic progress during vertical pass
                if (progressSW != null && (x & 0xFF) == 0)
                {
                    long now = progressSW.ElapsedMilliseconds;
                    if (now - lastProgressMs >= progressIntervalMs)
                    {
                        lastProgressMs = now;
                        int col = x;
                        int totalCols = size;
                        MainThreadDispatcher.Enqueue(() =>
                            FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Box blur vertical pass: {col}/{totalCols} columns ({now}ms)"));
                    }
                }
            }

            return output;
        }

        /// <summary>
        /// Flood-fills connected depressed pixels into distinct water body regions.
        /// Uses iterative BFS to avoid stack overflow on large regions.
        /// </summary>
        private static List<DetectedWaterBody> FloodFillRegions(bool[] isDepressed, float[] depressionMap, int size, int minBodyPixels,
            System.Diagnostics.Stopwatch progressSW = null, long progressIntervalMs = 30000)
        {
            var result = new List<DetectedWaterBody>();
            bool[] visited = new bool[size * size];
            var queue = new Queue<int>();
            long lastProgressMs = progressSW != null ? progressSW.ElapsedMilliseconds : 0;
            int pixelsVisited = 0;
            int totalPixels = size * size;

            for (int startIdx = 0; startIdx < size * size; startIdx++)
            {
                if (!isDepressed[startIdx] || visited[startIdx]) continue;

                var pxs = new List<int>();
                var pys = new List<int>();
                var depths = new List<float>();
                int minPX = size, maxPX = 0, minPY = size, maxPY = 0;
                float totalDepth = 0f;
                float maxDepth = 0f;

                queue.Clear();
                queue.Enqueue(startIdx);
                visited[startIdx] = true;

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    int x = idx % size;
                    int y = idx / size;
                    pixelsVisited++;

                    pxs.Add(x);
                    pys.Add(y);
                    float d = depressionMap[idx];
                    depths.Add(d);
                    totalDepth += d;
                    if (d > maxDepth) maxDepth = d;

                    if (x < minPX) minPX = x;
                    if (x > maxPX) maxPX = x;
                    if (y < minPY) minPY = y;
                    if (y > maxPY) maxPY = y;

                    // 8-connected neighbors
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx;
                            int ny = y + dy;
                            if (nx < 0 || nx >= size || ny < 0 || ny >= size) continue;
                            int ni = ny * size + nx;
                            if (!visited[ni] && isDepressed[ni])
                            {
                                visited[ni] = true;
                                queue.Enqueue(ni);
                            }
                        }
                    }

                    // Periodic progress logging
                    if (progressSW != null && (pixelsVisited & 0xFFFF) == 0) // Check every 65536 pixels
                    {
                        long now = progressSW.ElapsedMilliseconds;
                        if (now - lastProgressMs >= progressIntervalMs)
                        {
                            lastProgressMs = now;
                            int pv = pixelsVisited;
                            int rc = result.Count;
                            int currentRegionSize = pxs.Count;
                            MainThreadDispatcher.Enqueue(() =>
                                FiresLogger.LogInfo($"[BCHeightmapWaterDetector] Flood fill: {pv} pixels visited, {rc} bodies found, current region: {currentRegionSize} pixels ({now}ms)"));
                        }
                    }
                }

                if (pxs.Count < minBodyPixels) continue;

                result.Add(new DetectedWaterBody
                {
                    PixelXs = pxs,
                    PixelYs = pys,
                    DepressionDepths = depths,
                    MinPX = minPX,
                    MaxPX = maxPX,
                    MinPY = minPY,
                    MaxPY = maxPY,
                    AvgDepression = totalDepth / pxs.Count,
                    MaxDepression = maxDepth
                });

                if (result.Count >= MaxWaterBodies) break;
            }

            return result;
        }

        /// <summary>
        /// Classifies a water body as river, stream, or lake based on shape metrics.
        /// Uses fill ratio, bounding box aspect ratio, and average width analysis.
        /// </summary>
        private static void ClassifyWaterBody(DetectedWaterBody wb, float metersPerPixel)
        {
            int bboxW = wb.MaxPX - wb.MinPX + 1;
            int bboxH = wb.MaxPY - wb.MinPY + 1;
            int pixelCount = wb.PixelXs.Count;

            float bboxArea = (float)bboxW * bboxH;
            float fillRatio = pixelCount / bboxArea;

            float longer = Math.Max(bboxW, bboxH);
            float shorter = Math.Max(1, Math.Min(bboxW, bboxH));
            float bboxAspect = longer / shorter;

            float longerMeters = longer * metersPerPixel;
            float avgWidthPixels = (float)pixelCount / longer;
            float avgWidthMeters = avgWidthPixels * metersPerPixel;

            bool isElongated = bboxAspect >= RiverAspectRatio;
            bool isThinFill = fillRatio < 0.3f && longerMeters > 80f;
            bool isNarrowChannel = avgWidthMeters < (longerMeters * 0.15f) && longerMeters > 60f;

            if (isElongated || isThinFill || isNarrowChannel)
            {
                if (longerMeters < 200f || avgWidthMeters < 12f)
                    wb.Type = WaterBodyType.Stream;
                else
                    wb.Type = WaterBodyType.River;
            }
            else
            {
                // Compact shape - classify by size:
                //   Spring: <20m across (puddle at a depression or origin point)
                //   Pond:   20-200m across (tar-pit sized standing water)
                //   Lake:   200m+ across (large standing water body)
                if (longerMeters < 20f)
                    wb.Type = WaterBodyType.Spring;
                else if (longerMeters < 200f)
                    wb.Type = WaterBodyType.Pond;
                else
                    wb.Type = WaterBodyType.Lake;
            }
        }

        #endregion

        #region Centerline Tracing with Exact Heights

        /// <summary>
        /// Traces the centerline of a river/stream channel using the heightmap's
        /// exact grayscale values for terrain height computation.
        /// 
        /// At each cross-section, computes THREE height references:
        /// 
        /// 1. TerrainY - EXACT world height from heightmap at the lowest bed pixel
        ///    (using BC's formula: worldY = (pixelValue * Amount - 0.15 + SeaLevelAdj) * 200)
        ///    The darkest pixels at the highest elevations are origin springs/headwaters.
        /// 
        /// 2. DepressionDepth - how far below the banks the bed is, in world meters.
        ///    Computed from the depression map (neighborhoodAvg - localHeight) scaled to
        ///    meters. This tells us the channel's relative depth at each cross-section.
        /// 
        /// 3. BankY - the height of the lowest non-depressed (bank) pixel adjacent to
        ///    the channel edges. Found by scanning 1-3 pixels outside minY/maxY (or
        ///    minX/maxX). Water must NEVER exceed this minus a small margin.
        /// 
        /// Also computed:
        /// - Center = depression-depth-weighted centroid (deepest pixels pull harder)
        /// - Width = extent of depressed pixels in the cross-section
        /// - SurfaceY = TerrainY + small offset (initial estimate, refined later)
        /// 
        /// AutoWaterGenerator cross-references all three against real terrain via
        /// WorldGenerator.GetHeight() on the main thread:
        ///   bedY = min(heightmapTerrainY, realTerrainY)
        ///   surfaceY = min(bedY + offset, bankY - margin)
        /// </summary>
        private static List<AutoWaterGenerator.PathPoint> TraceCenterline(
            DetectedWaterBody wb, float[] map, HeightmapSnapshot snap, float mpp,
            float[] depressionMap = null)
        {
            var points = new List<AutoWaterGenerator.PathPoint>();

            int size = snap.Size;
            int bboxW = wb.MaxPX - wb.MinPX + 1;
            int bboxH = wb.MaxPY - wb.MinPY + 1;
            bool walkX = bboxW >= bboxH;

            // For large water bodies, use a bounding-box-local bool[] grid instead of
            // HashSet<long>. This eliminates hash overhead - O(1) array lookup is ~10x
            // faster than HashSet.Contains() for millions of entries.
            // Also use a local float[] for depression depth instead of Dictionary.
            bool useBBoxGrid = (long)bboxW * bboxH <= 50_000_000 && wb.PixelXs.Count > 50000;
            bool[] bboxGrid = null;
            float[] bboxDepth = null;
            HashSet<long> pixelSet = null;
            Dictionary<long, float> depthLookup = null;

            if (useBBoxGrid)
            {
                bboxGrid = new bool[bboxW * bboxH];
                bboxDepth = new float[bboxW * bboxH];
                for (int i = 0; i < wb.PixelXs.Count; i++)
                {
                    int lx = wb.PixelXs[i] - wb.MinPX;
                    int ly = wb.PixelYs[i] - wb.MinPY;
                    int li = ly * bboxW + lx;
                    bboxGrid[li] = true;
                    bboxDepth[li] = wb.DepressionDepths[i];
                }
            }
            else
            {
                // Small body - HashSet is fine
                pixelSet = new HashSet<long>();
                depthLookup = new Dictionary<long, float>();
                for (int i = 0; i < wb.PixelXs.Count; i++)
                {
                    long key = ((long)wb.PixelYs[i] << 32) | (uint)wb.PixelXs[i];
                    pixelSet.Add(key);
                    depthLookup[key] = wb.DepressionDepths[i];
                }
            }

            // Dynamic step scaling: for huge bodies, increase trace step to avoid
            // spending minutes on a single water body. At 20480px, a body spanning
            // 5000 pixels wide would normally trace 5000 cross-sections - we cap at ~2000.
            int longerDim = Math.Max(bboxW, bboxH);
            int step = Math.Max(1, ChannelTraceStep);
            if (longerDim > 2000)
                step = Math.Max(step, longerDim / 2000);

            // Height formula scale factor: depression values need conversion to world meters.
            // When IsWorldHeightCache=true, Map[] values and depression depths are already
            // in meters, so heightScale=1. When using BC heightmap (normalized 0-1),
            // multiply by HeightmapAmount * 200 to get meters.
            float heightScale = snap.IsWorldHeightCache ? 1f : snap.HeightmapAmount * 200f;

            // IMPORTANT: TraceCenterline provides RAW data - bed height, bank height,
            // depression depth. It does NOT compute the final water surface level.
            // That's DropRiverToTerrain's job (single authority for surface computation).
            // SurfaceY is set to 0 here so DropRiverToTerrain knows it needs to compute it.

            // Helper lambdas for pixel membership and depth lookup - unified for both grid and HashSet paths
            Func<int, int, bool> containsPixel;
            Func<int, int, float> getDepth;
            if (useBBoxGrid)
            {
                containsPixel = (px2, py2) =>
                {
                    int lx = px2 - wb.MinPX;
                    int ly = py2 - wb.MinPY;
                    if (lx < 0 || lx >= bboxW || ly < 0 || ly >= bboxH) return false;
                    return bboxGrid[ly * bboxW + lx];
                };
                getDepth = (px2, py2) =>
                {
                    int lx = px2 - wb.MinPX;
                    int ly = py2 - wb.MinPY;
                    if (lx < 0 || lx >= bboxW || ly < 0 || ly >= bboxH) return 0f;
                    return bboxDepth[ly * bboxW + lx];
                };
            }
            else
            {
                containsPixel = (px2, py2) =>
                {
                    long key = ((long)py2 << 32) | (uint)px2;
                    return pixelSet.Contains(key);
                };
                getDepth = (px2, py2) =>
                {
                    long key = ((long)py2 << 32) | (uint)px2;
                    float d;
                    return depthLookup.TryGetValue(key, out d) ? d : 0f;
                };
            }

            if (walkX)
            {
                for (int px = wb.MinPX; px <= wb.MaxPX; px += step)
                {
                    float weightedY = 0f;
                    float totalWeight = 0f;
                    int minY = size, maxY = 0;
                    float lowestBedPixelValue = float.MaxValue;
                    float maxDepressionNorm = 0f;

                    for (int py = wb.MinPY; py <= wb.MaxPY; py++)
                    {
                        if (!containsPixel(px, py)) continue;

                        float depth = getDepth(px, py);

                        // Weight by depth SQUARED so the deepest pixels dominate the centroid.
                        // Linear weighting lets shallow bank pixels pull the center
                        // off the actual bed, causing floating water when GetHeight()
                        // samples a bank slope instead of the channel floor.
                        float w = (depth + 0.001f) * (depth + 0.001f);
                        weightedY += py * w;
                        totalWeight += w;

                        if (py < minY) minY = py;
                        if (py > maxY) maxY = py;

                        // Track the lowest pixel value (darkest = deepest riverbed)
                        float pixVal = map[py * size + px];
                        if (pixVal < lowestBedPixelValue) lowestBedPixelValue = pixVal;

                        // Track max depression depth at this cross-section
                        if (depth > maxDepressionNorm) maxDepressionNorm = depth;
                    }

                    if (totalWeight < 0.0001f) continue;

                    float centerPY = weightedY / totalWeight;
                    float widthMeters = (maxY - minY + 1) * mpp;
                    if (widthMeters < 4f) widthMeters = 4f;

                    // Convert the lowest bed pixel value to exact world height
                    float bedY = HeightmapToWorldY(lowestBedPixelValue, snap);

                    // Depression depth in world meters
                    float depressionDepthMeters = maxDepressionNorm * heightScale;

                    // Bank height: find the lowest non-depressed pixel adjacent to
                    // the channel edges on EACH side independently, then use the
                    // lower of the two. This avoids a gulch on one side pulling the
                    // entire bank height down when the other side is a solid wall.
                    float leftBankVal = float.MaxValue;
                    float rightBankVal = float.MaxValue;
                    int bankSearchRange = 3;
                    for (int offset = 1; offset <= bankSearchRange; offset++)
                    {
                        // "Left" bank = minY side (low pixel Y)
                        int bankPY = minY - offset;
                        if (bankPY >= 0 && bankPY < size)
                        {
                            if (!containsPixel(px, bankPY))
                            {
                                float bv = map[bankPY * size + px];
                                if (bv < leftBankVal)
                                    leftBankVal = bv;
                            }
                        }
                        // "Right" bank = maxY side (high pixel Y)
                        bankPY = maxY + offset;
                        if (bankPY >= 0 && bankPY < size)
                        {
                            if (!containsPixel(px, bankPY))
                            {
                                float bv = map[bankPY * size + px];
                                if (bv < rightBankVal)
                                    rightBankVal = bv;
                            }
                        }
                    }

                    // Use the LOWER of the two banks - this is where water would spill
                    float bankPixelVal = Math.Min(leftBankVal, rightBankVal);

                    float bankY;
                    if (bankPixelVal < float.MaxValue - 1f)
                        bankY = HeightmapToWorldY(bankPixelVal, snap);
                    else
                        bankY = bedY + depressionDepthMeters;

                    // SurfaceY = 0 signals DropRiverToTerrain to compute it fresh.
                    // We only provide raw data here: bed, bank, depression depth.

                    float worldX, worldZ;
                    PixelToWorld(px, (int)(centerPY + 0.5f), snap, out worldX, out worldZ);

                    points.Add(new AutoWaterGenerator.PathPoint
                    {
                        WorldX = worldX,
                        WorldZ = worldZ,
                        Width = widthMeters,
                        TerrainY = bedY,
                        SurfaceY = 0f,
                        DepressionDepth = depressionDepthMeters,
                        BankY = bankY
                    });
                }
            }
            else
            {
                for (int py = wb.MinPY; py <= wb.MaxPY; py += step)
                {
                    float weightedX = 0f;
                    float totalWeight = 0f;
                    int minX = size, maxX = 0;
                    float lowestBedPixelValue = float.MaxValue;
                    float maxDepressionNorm = 0f;

                    for (int px = wb.MinPX; px <= wb.MaxPX; px++)
                    {
                        if (!containsPixel(px, py)) continue;

                        float depth = getDepth(px, py);

                        // Weight by depth SQUARED - same as walkX branch
                        float w = (depth + 0.001f) * (depth + 0.001f);
                        weightedX += px * w;
                        totalWeight += w;

                        if (px < minX) minX = px;
                        if (px > maxX) maxX = px;

                        float pixVal = map[py * size + px];
                        if (pixVal < lowestBedPixelValue) lowestBedPixelValue = pixVal;

                        if (depth > maxDepressionNorm) maxDepressionNorm = depth;
                    }

                    if (totalWeight < 0.0001f) continue;

                    float centerPX = weightedX / totalWeight;
                    float widthMeters = (maxX - minX + 1) * mpp;
                    if (widthMeters < 4f) widthMeters = 4f;

                    float bedY = HeightmapToWorldY(lowestBedPixelValue, snap);

                    float depressionDepthMeters = maxDepressionNorm * heightScale;

                    // Bank height from edge pixels (left/right of channel for Y-walk)
                    // Same per-side logic as walkX branch
                    float leftBankVal = float.MaxValue;
                    float rightBankVal = float.MaxValue;
                    int bankSearchRange = 3;
                    for (int offset = 1; offset <= bankSearchRange; offset++)
                    {
                        // "Left" bank = minX side
                        int bankPX = minX - offset;
                        if (bankPX >= 0 && bankPX < size)
                        {
                            if (!containsPixel(bankPX, py))
                            {
                                float bv = map[py * size + bankPX];
                                if (bv < leftBankVal)
                                    leftBankVal = bv;
                            }
                        }
                        // "Right" bank = maxX side
                        bankPX = maxX + offset;
                        if (bankPX >= 0 && bankPX < size)
                        {
                            if (!containsPixel(bankPX, py))
                            {
                                float bv = map[py * size + bankPX];
                                if (bv < rightBankVal)
                                    rightBankVal = bv;
                            }
                        }
                    }

                    float bankPixelVal = Math.Min(leftBankVal, rightBankVal);

                    float bankY;
                    if (bankPixelVal < float.MaxValue - 1f)
                        bankY = HeightmapToWorldY(bankPixelVal, snap);
                    else
                        bankY = bedY + depressionDepthMeters;

                    // SurfaceY = 0 signals DropRiverToTerrain to compute it fresh.

                    float worldX, worldZ;
                    PixelToWorld((int)(centerPX + 0.5f), py, snap, out worldX, out worldZ);

                    points.Add(new AutoWaterGenerator.PathPoint
                    {
                        WorldX = worldX,
                        WorldZ = worldZ,
                        Width = widthMeters,
                        TerrainY = bedY,
                        SurfaceY = 0f,
                        DepressionDepth = depressionDepthMeters,
                        BankY = bankY
                    });
                }
            }

            // Sub-pixel interpolation: heightmap pixels may be far apart (e.g. ~10m at 2048px).
            // We need ~2m spacing for smooth riverbed following. Insert linearly interpolated
            // points between each pixel pair so DropRiverToTerrain gets dense input for its
            // checkpoint/smoothing pipeline.
            //
            // At high resolutions (8192px = ~2.5m/pixel), pixels are already close to
            // the target spacing so fewer interpolated points are needed. We use the
            // min of target spacing and the actual pixel spacing to avoid over-densifying.
            if (points.Count >= 2)
            {
                float targetSpacing = Math.Min(2f, mpp * 0.8f); // At 8K, mpp~2.5 -> target~2.0
                var dense = new List<AutoWaterGenerator.PathPoint>();
                dense.Add(points[0]);

                for (int pi = 1; pi < points.Count; pi++)
                {
                    var prev = points[pi - 1];
                    var curr = points[pi];
                    float segDx = curr.WorldX - prev.WorldX;
                    float segDz = curr.WorldZ - prev.WorldZ;
                    float segLen = (float)Math.Sqrt(segDx * segDx + segDz * segDz);

                    int subSteps = Math.Max(1, (int)(segLen / targetSpacing));

                    for (int si = 1; si <= subSteps; si++)
                    {
                        float st = (float)si / subSteps;
                        dense.Add(new AutoWaterGenerator.PathPoint
                        {
                            WorldX = prev.WorldX + segDx * st,
                            WorldZ = prev.WorldZ + segDz * st,
                            Width = prev.Width + (curr.Width - prev.Width) * st,
                            TerrainY = prev.TerrainY + (curr.TerrainY - prev.TerrainY) * st,
                            SurfaceY = prev.SurfaceY + (curr.SurfaceY - prev.SurfaceY) * st,
                            DepressionDepth = prev.DepressionDepth + (curr.DepressionDepth - prev.DepressionDepth) * st,
                            BankY = prev.BankY + (curr.BankY - prev.BankY) * st
                        });
                    }
                }

                points = dense;
            }

            return points;
        }

        /// <summary>
        /// Generates disk placement points for a lake water body.
        /// 
        /// Uses the heightmap to find the EXACT fill level: the lowest bank pixel value
        /// around the perimeter determines how high water can fill before spilling.
        /// Each disk's SurfaceY is set to this fill level (converted to world Y).
        /// Disks where terrain is AT or ABOVE the fill level are excluded (bank/shore).
        /// </summary>
        private static List<AutoWaterGenerator.PathPoint> GenerateLakePoints(
            DetectedWaterBody wb, float[] map, HeightmapSnapshot snap, float mpp)
        {
            var points = new List<AutoWaterGenerator.PathPoint>();

            float diskRadius = Math.Max(5f, mpp * 2f);
            int gridStep = Math.Max(1, (int)(diskRadius * 0.8f / mpp));

            // Build pixel lookup - use bounding-box-local bool[] for large bodies,
            // HashSet for small ones. The grid gives O(1) lookups without hash overhead.
            int bboxW = wb.MaxPX - wb.MinPX + 1;
            int bboxH = wb.MaxPY - wb.MinPY + 1;
            long bboxArea = (long)bboxW * bboxH;
            bool useBBoxGrid = bboxArea <= 50_000_000 && wb.PixelXs.Count > 10000;
            bool[] bboxGrid = null;
            HashSet<long> pixelSet = null;

            Func<int, int, bool> containsPixel;
            if (useBBoxGrid)
            {
                bboxGrid = new bool[bboxArea];
                for (int i = 0; i < wb.PixelXs.Count; i++)
                {
                    int lx = wb.PixelXs[i] - wb.MinPX;
                    int ly = wb.PixelYs[i] - wb.MinPY;
                    bboxGrid[ly * bboxW + lx] = true;
                }
                containsPixel = (px2, py2) =>
                {
                    int lx = px2 - wb.MinPX;
                    int ly = py2 - wb.MinPY;
                    if (lx < 0 || lx >= bboxW || ly < 0 || ly >= bboxH) return false;
                    return bboxGrid[ly * bboxW + lx];
                };
            }
            else
            {
                pixelSet = new HashSet<long>();
                for (int i = 0; i < wb.PixelXs.Count; i++)
                {
                    long key = ((long)wb.PixelYs[i] << 32) | (uint)wb.PixelXs[i];
                    pixelSet.Add(key);
                }
                containsPixel = (px2, py2) =>
                {
                    long key = ((long)py2 << 32) | (uint)px2;
                    return pixelSet.Contains(key);
                };
            }

            // Find the fill level: scan the perimeter of the bounding box
            // to find the lowest bank height. Water fills to just below that.
            float lowestBankPixelVal = float.MaxValue;
            float deepestBedPixelVal = float.MaxValue;

            // Also scan all region pixels to find the deepest bed
            for (int i = 0; i < wb.PixelXs.Count; i++)
            {
                float pv = map[wb.PixelYs[i] * snap.Size + wb.PixelXs[i]];
                if (pv < deepestBedPixelVal) deepestBedPixelVal = pv;
            }

            // Scan 1px border around the bounding box for bank heights
            int borderExpand = 2;
            int bMinX = Math.Max(0, wb.MinPX - borderExpand);
            int bMaxX = Math.Min(snap.Size - 1, wb.MaxPX + borderExpand);
            int bMinY = Math.Max(0, wb.MinPY - borderExpand);
            int bMaxY = Math.Min(snap.Size - 1, wb.MaxPY + borderExpand);

            for (int py = bMinY; py <= bMaxY; py++)
            {
                for (int px = bMinX; px <= bMaxX; px++)
                {
                    // Only look at pixels OUTSIDE the water body (the banks)
                    if (containsPixel(px, py)) continue;

                    // Check if this bank pixel is adjacent to a water pixel
                    bool adjToWater = false;
                    for (int dy = -1; dy <= 1 && !adjToWater; dy++)
                    {
                        for (int dx = -1; dx <= 1 && !adjToWater; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            if (containsPixel(px + dx, py + dy)) adjToWater = true;
                        }
                    }
                    if (!adjToWater) continue;

                    float bankVal = map[py * snap.Size + px];
                    if (bankVal < lowestBankPixelVal) lowestBankPixelVal = bankVal;
                }
            }

            // Fill level = just below the lowest bank
            float fillPixelVal;
            if (lowestBankPixelVal < float.MaxValue - 1f)
                fillPixelVal = lowestBankPixelVal - 0.001f;
            else
                fillPixelVal = deepestBedPixelVal + 0.005f; // No clear bank found

            float fillWorldY = HeightmapToWorldY(fillPixelVal, snap);

            // Generate grid points - only where terrain is below fill level
            for (int py = wb.MinPY; py <= wb.MaxPY; py += gridStep)
            {
                for (int px = wb.MinPX; px <= wb.MaxPX; px += gridStep)
                {
                    // Check if this grid cell has any depressed pixels
                    bool hasDepressed = false;
                    for (int dy = 0; dy < gridStep && !hasDepressed; dy++)
                    {
                        for (int dx = 0; dx < gridStep && !hasDepressed; dx++)
                        {
                            if (containsPixel(px + dx, py + dy))
                                hasDepressed = true;
                        }
                    }
                    if (!hasDepressed) continue;

                    int centerPX = px + gridStep / 2;
                    int centerPY = py + gridStep / 2;
                    float terrainPixelVal = SampleHeightmapAt(centerPX, centerPY, snap);

                    // Only place water where terrain is below fill level
                    if (terrainPixelVal >= fillPixelVal) continue;

                    float terrainY = HeightmapToWorldY(terrainPixelVal, snap);

                    float worldX, worldZ;
                    PixelToWorld(centerPX, centerPY, snap, out worldX, out worldZ);

                    points.Add(new AutoWaterGenerator.PathPoint
                    {
                        WorldX = worldX,
                        WorldZ = worldZ,
                        Width = diskRadius * 2f,
                        TerrainY = terrainY,
                        SurfaceY = fillWorldY
                    });
                }
            }

            return points;
        }

        #endregion

        #region Split Oversized Bodies

        /// <summary>
        /// Splits an oversized water body into overlapping strips along its longer
        /// bounding-box axis. Each strip contains a subset of the original pixels
        /// and is independently classified and traced.
        /// 
        /// WHY: A 51M pixel river spanning 18kmx12km would take hours to trace as
        /// a single body. By splitting into strips of ~MaxPixelsPerBody pixels each,
        /// we can trace each strip in seconds. The strips overlap by 50 pixels to
        /// ensure continuity at boundaries.
        /// 
        /// ALGORITHM:
        /// 1. Determine the split axis (longer bounding-box dimension)
        /// 2. Calculate strip width to target ~MaxPixelsPerBody pixels per strip
        /// 3. Partition pixels into strips with 50px overlap
        /// 4. Discard strips below MinWaterBodyPixels
        /// 5. Classify and trace each strip independently
        /// 
        /// THREAD SAFETY: All pure math on snapshot data.
        /// </summary>
        private static List<DetectedWaterBody> SplitOversizedBody(
            DetectedWaterBody wb, float[] map, HeightmapSnapshot snap,
            float mpp, float[] depressionMap, int minBodyPixels)
        {
            var result = new List<DetectedWaterBody>();

            int bboxW = wb.MaxPX - wb.MinPX + 1;
            int bboxH = wb.MaxPY - wb.MinPY + 1;
            bool splitAlongX = bboxW >= bboxH;

            // Calculate how many strips we need.
            // Target: each strip has roughly MaxPixelsPerBody pixels.
            // Estimate strip pixel density from the body's fill ratio.
            int longerDim = splitAlongX ? bboxW : bboxH;
            int shorterDim = splitAlongX ? bboxH : bboxW;

            // Estimate pixels per unit length along the split axis
            float pixelsPerSlice = (float)wb.PixelXs.Count / longerDim;

            // Strip width in pixels to get ~MaxPixelsPerBody pixels per strip
            int stripWidth = Math.Max(100, (int)(MaxPixelsPerBody / Math.Max(1f, pixelsPerSlice)));

            // Overlap between strips (50 pixels ensures river channels crossing
            // boundaries get traced from both sides)
            int overlapPixels = 50;
            int effectiveStride = Math.Max(50, stripWidth - overlapPixels);

            int splitStart = splitAlongX ? wb.MinPX : wb.MinPY;
            int splitEnd = splitAlongX ? wb.MaxPX : wb.MaxPY;
            int numStrips = Math.Max(1, (splitEnd - splitStart) / effectiveStride + 1);

            // Safety cap on total strips
            if (numStrips > 200) numStrips = 200;

            // Pre-sort pixels by the split axis coordinate for efficient partitioning.
            // Build an index array sorted by the split-axis coordinate.
            int pixelCount = wb.PixelXs.Count;
            int[] sortedIndices = new int[pixelCount];
            for (int i = 0; i < pixelCount; i++) sortedIndices[i] = i;

            if (splitAlongX)
            {
                Array.Sort(sortedIndices, (a, b) => wb.PixelXs[a].CompareTo(wb.PixelXs[b]));
            }
            else
            {
                Array.Sort(sortedIndices, (a, b) => wb.PixelYs[a].CompareTo(wb.PixelYs[b]));
            }

            // Extract sorted axis values for binary search
            int[] sortedAxisValues = new int[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                sortedAxisValues[i] = splitAlongX
                    ? wb.PixelXs[sortedIndices[i]]
                    : wb.PixelYs[sortedIndices[i]];
            }

            for (int stripIdx = 0; stripIdx < numStrips; stripIdx++)
            {
                int stripLo = splitStart + stripIdx * effectiveStride;
                int stripHi = Math.Min(stripLo + stripWidth - 1, splitEnd);

                // Binary search for first and last index in the sorted array
                int firstSorted = LowerBound(sortedAxisValues, stripLo);
                int lastSorted = UpperBound(sortedAxisValues, stripHi) - 1;

                if (firstSorted > lastSorted) continue;
                int stripPixelCount = lastSorted - firstSorted + 1;
                if (stripPixelCount < minBodyPixels) continue;

                // Build the strip's pixel lists
                var pxs = new List<int>(stripPixelCount);
                var pys = new List<int>(stripPixelCount);
                var depths = new List<float>(stripPixelCount);
                int minPX = int.MaxValue, maxPX = 0, minPY = int.MaxValue, maxPY = 0;
                float totalDepth = 0f;
                float maxDepth = 0f;

                for (int si = firstSorted; si <= lastSorted; si++)
                {
                    int origIdx = sortedIndices[si];
                    int px = wb.PixelXs[origIdx];
                    int py = wb.PixelYs[origIdx];
                    float d = wb.DepressionDepths[origIdx];

                    pxs.Add(px);
                    pys.Add(py);
                    depths.Add(d);
                    totalDepth += d;
                    if (d > maxDepth) maxDepth = d;

                    if (px < minPX) minPX = px;
                    if (px > maxPX) maxPX = px;
                    if (py < minPY) minPY = py;
                    if (py > maxPY) maxPY = py;
                }

                var stripBody = new DetectedWaterBody
                {
                    PixelXs = pxs,
                    PixelYs = pys,
                    DepressionDepths = depths,
                    MinPX = minPX,
                    MaxPX = maxPX,
                    MinPY = minPY,
                    MaxPY = maxPY,
                    AvgDepression = totalDepth / pxs.Count,
                    MaxDepression = maxDepth
                };

                // Classify the strip
                ClassifyWaterBody(stripBody, mpp);

                // Trace based on classification
                if (stripBody.Type == WaterBodyType.River || stripBody.Type == WaterBodyType.Stream)
                {
                    stripBody.PathPoints = TraceCenterline(stripBody, map, snap, mpp, depressionMap);
                }
                else if (stripBody.Type == WaterBodyType.Lake || stripBody.Type == WaterBodyType.Pond)
                {
                    stripBody.PathPoints = GenerateLakePoints(stripBody, map, snap, mpp);
                }
                else
                {
                    stripBody.PathPoints = GenerateLakePoints(stripBody, map, snap, mpp);
                }

                if (stripBody.PathPoints != null && stripBody.PathPoints.Count > 0)
                    result.Add(stripBody);
            }

            return result;
        }

        /// <summary>Returns the index of the first element >= value.</summary>
        private static int LowerBound(int[] sortedArray, int value)
        {
            int lo = 0, hi = sortedArray.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (sortedArray[mid] < value)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        /// <summary>Returns the index of the first element > value (i.e., one past the last element <= value).</summary>
        private static int UpperBound(int[] sortedArray, int value)
        {
            int lo = 0, hi = sortedArray.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (sortedArray[mid] <= value)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        #endregion

        #region Lake Fallback - Decompose Into Sub-Features

        /// <summary>
        /// When a detected lake cannot be placed (GenerateLakePoints returns 0 points),
        /// this method re-analyzes the lake's pixel region to find sub-features:
        /// - Elongated channels within the lake bounds -> rivers/streams
        /// - Small isolated depressions -> ponds with their own local fill levels
        ///
        /// WHY THIS HAPPENS: A large lake region may have no single clear fill level
        /// because the "lake" is actually a wide valley with multiple channels at
        /// different elevations, or the lowest bank pixel is barely above the deepest
        /// bed pixel (no real depression). But within that region, there ARE carved
        /// channels and small depressions that can be rendered as rivers/ponds.
        ///
        /// ALGORITHM:
        /// 1. Within the lake's pixel region, find pixels that are significantly
        ///    deeper than the lake region's average (a tighter depression threshold).
        /// 2. Flood-fill those deeper pixels into sub-regions.
        /// 3. Classify each sub-region (river/stream/pond) by shape.
        /// 4. Generate path points for each sub-region.
        ///
        /// THREAD SAFETY: All pure math on snapshot data.
        /// </summary>
        private static List<DetectedWaterBody> DecomposeLakeIntoSubFeatures(
            DetectedWaterBody lakeBounds, float[] map, HeightmapSnapshot snap,
            float mpp, float[] depressionMap, int minBodyPixels)
        {
            var result = new List<DetectedWaterBody>();

            int size = snap.Size;
            int pixelCount = lakeBounds.PixelXs.Count;
            if (pixelCount < minBodyPixels * 2) return result;

            // Build a pixel lookup - use bounding-box-local grid for large bodies
            int lBboxW = lakeBounds.MaxPX - lakeBounds.MinPX + 1;
            int lBboxH = lakeBounds.MaxPY - lakeBounds.MinPY + 1;
            long lBboxArea = (long)lBboxW * lBboxH;
            bool useLakeBBoxGrid = lBboxArea <= 50_000_000 && pixelCount > 10000;
            bool[] lakeBBoxGrid = null;
            HashSet<long> lakePixelSet = null;

            Func<int, int, bool> lakeContains;
            if (useLakeBBoxGrid)
            {
                lakeBBoxGrid = new bool[lBboxArea];
                for (int i = 0; i < pixelCount; i++)
                {
                    int lx = lakeBounds.PixelXs[i] - lakeBounds.MinPX;
                    int ly = lakeBounds.PixelYs[i] - lakeBounds.MinPY;
                    lakeBBoxGrid[ly * lBboxW + lx] = true;
                }
                lakeContains = (px2, py2) =>
                {
                    int lx = px2 - lakeBounds.MinPX;
                    int ly = py2 - lakeBounds.MinPY;
                    if (lx < 0 || lx >= lBboxW || ly < 0 || ly >= lBboxH) return false;
                    return lakeBBoxGrid[ly * lBboxW + lx];
                };
            }
            else
            {
                lakePixelSet = new HashSet<long>();
                for (int i = 0; i < pixelCount; i++)
                {
                    long key = ((long)lakeBounds.PixelYs[i] << 32) | (uint)lakeBounds.PixelXs[i];
                    lakePixelSet.Add(key);
                }
                lakeContains = (px2, py2) =>
                {
                    long key = ((long)py2 << 32) | (uint)px2;
                    return lakePixelSet.Contains(key);
                };
            }

            // Compute the average heightmap value within the lake region
            double sumHeight = 0.0;
            for (int i = 0; i < pixelCount; i++)
            {
                int px = lakeBounds.PixelXs[i];
                int py = lakeBounds.PixelYs[i];
                sumHeight += map[py * size + px];
            }
            float avgHeight = (float)(sumHeight / pixelCount);

            // Find pixels within the lake that are significantly below the region average.
            // Use a tighter threshold than the global MinDepressionDepth since we're
            // looking for channels within an already-depressed area.
            // Scale threshold for world height cache (values in meters vs normalized 0-1).
            float baseSubThreshold = MinDepressionDepth * 0.6f;
            float subThreshold = snap.IsWorldHeightCache
                ? baseSubThreshold * snap.HeightmapAmount * 200f
                : baseSubThreshold;

            // Use bounding-box-local array instead of full-size array to avoid
            // allocating a 419M-element array at 20480x20480 resolution.
            bool[] isSubDepressedLocal = new bool[lBboxArea];
            for (int i = 0; i < pixelCount; i++)
            {
                int px = lakeBounds.PixelXs[i];
                int py = lakeBounds.PixelYs[i];
                int idx = py * size + px;
                float localDepth = avgHeight - map[idx];
                if (localDepth > subThreshold)
                {
                    int lx = px - lakeBounds.MinPX;
                    int ly = py - lakeBounds.MinPY;
                    isSubDepressedLocal[ly * lBboxW + lx] = true;
                }
            }

            // Flood-fill the sub-depressed pixels into distinct sub-regions
            // Use bounding-box-local visited array to avoid allocating full-size array
            bool[] visitedLocal = new bool[lBboxArea];
            var queue = new Queue<int>();
            int maxSubFeatures = 50;

            for (int i = 0; i < pixelCount && result.Count < maxSubFeatures; i++)
            {
                int startPX = lakeBounds.PixelXs[i];
                int startPY = lakeBounds.PixelYs[i];
                int startLX = startPX - lakeBounds.MinPX;
                int startLY = startPY - lakeBounds.MinPY;
                int startLocalIdx = startLY * lBboxW + startLX;

                if (!isSubDepressedLocal[startLocalIdx] || visitedLocal[startLocalIdx]) continue;

                var pxs = new List<int>();
                var pys = new List<int>();
                var depths = new List<float>();
                int minPX = size, maxPX = 0, minPY = size, maxPY = 0;
                float totalDepth = 0f;
                float maxDepth = 0f;

                queue.Clear();
                // Queue stores local indices within the bounding box
                queue.Enqueue(startLocalIdx);
                visitedLocal[startLocalIdx] = true;

                while (queue.Count > 0)
                {
                    int localIdx = queue.Dequeue();
                    int lx = localIdx % lBboxW;
                    int ly = localIdx / lBboxW;
                    int x = lx + lakeBounds.MinPX;
                    int y = ly + lakeBounds.MinPY;
                    int globalIdx = y * size + x;

                    pxs.Add(x);
                    pys.Add(y);
                    float d = depressionMap != null ? depressionMap[globalIdx] : (avgHeight - map[globalIdx]);
                    depths.Add(d);
                    totalDepth += d;
                    if (d > maxDepth) maxDepth = d;

                    if (x < minPX) minPX = x;
                    if (x > maxPX) maxPX = x;
                    if (y < minPY) minPY = y;
                    if (y > maxPY) maxPY = y;

                    // 8-connected neighbors within the lake region
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nlx = lx + dx;
                            int nly = ly + dy;
                            if (nlx < 0 || nlx >= lBboxW || nly < 0 || nly >= lBboxH) continue;
                            int nLocalIdx = nly * lBboxW + nlx;
                            if (!visitedLocal[nLocalIdx] && isSubDepressedLocal[nLocalIdx])
                            {
                                // Must be within the original lake's pixel set
                                if (lakeContains(nlx + lakeBounds.MinPX, nly + lakeBounds.MinPY))
                                {
                                    visitedLocal[nLocalIdx] = true;
                                    queue.Enqueue(nLocalIdx);
                                }
                            }
                        }
                    }
                }

                // Filter out too-small sub-regions
                if (pxs.Count < Math.Max(3, minBodyPixels / 2)) continue;

                var subBody = new DetectedWaterBody
                {
                    PixelXs = pxs,
                    PixelYs = pys,
                    DepressionDepths = depths,
                    MinPX = minPX,
                    MaxPX = maxPX,
                    MinPY = minPY,
                    MaxPY = maxPY,
                    AvgDepression = totalDepth / pxs.Count,
                    MaxDepression = maxDepth
                };

                // Classify the sub-feature
                ClassifyWaterBody(subBody, mpp);

                // Generate path points based on classification
                if (subBody.Type == WaterBodyType.River || subBody.Type == WaterBodyType.Stream)
                {
                    subBody.PathPoints = TraceCenterline(subBody, map, snap, mpp, depressionMap);
                }
                else
                {
                    // Small pond within the failed lake - try GenerateLakePoints
                    // with the sub-region's tighter bounds
                    subBody.PathPoints = GenerateLakePoints(subBody, map, snap, mpp);
                }

                if (subBody.PathPoints != null && subBody.PathPoints.Count > 0)
                    result.Add(subBody);
            }

            return result;
        }

        #endregion

        #region Debug / Diagnostics

        /// <summary>
        /// Returns a diagnostic string about the detector's state.
        /// </summary>
        public static string GetStatusString()
        {
            EnsureReflectionInitialized();

            if (!_canReadHeightmap)
                return "BC Heightmap Reader: not available (BC not loaded or heightmap fields not found)";

            return $"BC Heightmap Reader: ready (direct height mode), " +
                   $"neighborhoodRadius={NeighborhoodRadius}px, " +
                   $"minDepression={MinDepressionDepth}, minBodySize={MinWaterBodyPixels}px, " +
                   $"formula: worldY = (pixel * Amount - 0.15 + SeaLevelAdj) * 200";
        }

        /// <summary>Resets the reflection cache.</summary>
        public static void ResetCache()
        {
            _reflectionInitialized = false;
            _canReadHeightmap = false;
        }

        #endregion
    }
}
