using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using VerdantsAscent.Async;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Async mesh generation for water combiners.
    /// Moves expensive surface calculation, smoothing, depth computation, and
    /// vertex/triangle generation to a background thread.
    /// 
    /// ARCHITECTURE (mirrors DirtFloorAsyncMeshBuilder):
    /// 1. Main thread pre-samples terrain heights (Unity API - requires main thread)
    /// 2. Main thread snapshots all disk data + terrain heights into thread-safe input
    /// 3. Background thread computes: surface heights, smoothing, depth, vertices, triangles, UVs, colors
    /// 4. Main thread applies result to Unity Mesh (Unity API - requires main thread)
    /// 
    /// KEY CONSTRAINT: No Unity API calls on background thread.
    /// All off-thread work is pure math on plain arrays/structs.
    /// </summary>
    public static class WaterAsyncMeshBuilder
    {
        #region Job Data Structures

        /// <summary>
        /// Input data for water mesh generation job.
        /// All data is snapshot on the main thread before submission.
        /// </summary>
        public class WaterMeshJobInput
        {
            public int GridWidth;
            public int GridHeight;
            public float CellSize;
            public Vector3 GridOrigin;
            public Vector3 CombinerPosition;

            // Pre-sampled terrain heights (sampled on main thread via raycast/ZoneSystem)
            public float[] TerrainHeights;

            // Disk data (serialized for thread safety)
            public WaterDiskData[] Disks;

            // Water constants
            public float BlendDistance;
            public float EdgeFadeDistance;
            public float BankExtensionDistance;
            public float BankDepthBelowTerrain;
            public float WaterTerrainOffset;
            public float CenterChannelDepth;
            public float CenterChannelRadiusFraction;
            public int SurfaceSmoothPasses;
            public float SmoothStrength;

            // Pond/river mode (pre-computed on main thread)
            public bool IsPondMode;
            public float SettledWaterLevel;
            public float MinDiskY;
            public float MaxDiskY;

            // Wave parameters
            public float WaveIntensity;
            public float Time;
            public float WindStrength;
        }

        /// <summary>
        /// Thread-safe water disk data.
        /// </summary>
        public struct WaterDiskData
        {
            public float PosX, PosY, PosZ;
            public float Radius;
            public float RadiusPlusBlend;
            public float MaxDepth;
            public float EdgeFadeStart;
            public float CenterChannelRadius;
            // Shape support
            public int Shape; // 0=Circle, 1=Square, 2=Rectangle
            public float HalfWidth;   // Half extent in X
            public float HalfLength;  // Half extent in Z
        }

        /// <summary>
        /// Output data from water mesh generation job.
        /// </summary>
        public class WaterMeshJobOutput
        {
            public float[] SurfaceHeights;
            public float[] WaterDepths;
            public Vector3[] Vertices;
            public int[] Triangles;
            public Vector2[] UVs;
            public Vector3[] Normals;
            public Color[] Colors;
            public bool IsComplete;
            public bool HasError;
            public string ErrorMessage;
        }

        public delegate void WaterMeshJobCallback(WaterMeshJobOutput result);

        #endregion

        #region Worker

        private static BackgroundWorker<WaterMeshJobInput, WaterMeshJobOutput> _worker;

        #endregion

        #region Public API

        /// <summary>
        /// Queues a water mesh generation job for async processing.
        /// Call from main thread only.
        /// </summary>
        public static void QueueMeshJob(WaterMeshJobInput input, WaterMeshJobCallback callback)
        {
            if (input == null || callback == null) return;

            if (_worker == null)
                _worker = new BackgroundWorker<WaterMeshJobInput, WaterMeshJobOutput>("WaterMesh", ProcessWaterMeshJob);

            _worker.QueueJob(input, output => callback(output));
        }

        // Per-liquid-type vertical offset between terrain surface and the
        // liquid's resting Y. Positive = liquid sits ABOVE terrain (lift);
        // negative = liquid sits BELOW terrain (flush flood-fill, fall in).
        //
        //   Water: +0.02m   slight lift so the surface reads above ground
        //                   without Z-fighting. Vanilla water default.
        //   Lava:  -0.5m    sits half a meter INTO terrain so it flood-
        //                   fills low spots flush with the surrounding
        //                   tile edges. Player stepping onto a lava-painted
        //                   area drops in. User-requested behavior 2026-05-25
        //                   ("lower the terrain where placed and basically
        //                    flood fill to the level edge").
        //   Boiling: +0.02m same as water (visual treatment differs in shader)
        //   Freezing: +0.02m same
        //   Ice / Slime: +0.02m (no special drop)
        //
        // Centralised here so the bake job + any preview code agree on the
        // same offset.
        private static float GetLiquidTerrainOffset(CustomLiquidType liquidType)
        {
            switch (liquidType)
            {
                case CustomLiquidType.Lava: return -0.5f;
                default:                    return 0.02f;
            }
        }

        /// <summary>
        /// Creates input from a combiner's current state.
        /// MUST be called on main thread (snapshots terrain heights, disk positions, time, wind).
        /// Terrain heights must already be sampled into the provided array.
        /// </summary>
        public static WaterMeshJobInput CreateInput(
            WaterMeshCombiner combiner,
            IReadOnlyList<WaterDisk> waterDisks,
            int gridWidth, int gridHeight, float cellSize,
            Vector3 gridOrigin, Vector3 combinerPosition,
            float[] terrainHeights,
            float blendDistance,
            bool isPondMode, float settledWaterLevel,
            float minDiskY, float maxDiskY,
            float waveIntensity)
        {
            var input = new WaterMeshJobInput
            {
                GridWidth = gridWidth,
                GridHeight = gridHeight,
                CellSize = cellSize,
                GridOrigin = gridOrigin,
                CombinerPosition = combinerPosition,
                TerrainHeights = (float[])terrainHeights.Clone(),
                BlendDistance = blendDistance,
                EdgeFadeDistance = 1.0f,
                // Bank extension RESTORED (2026-05-25 round-2).
                //
                // Briefly zeroed earlier today for "no climb up banks", but
                // the resulting square mesh shape + lack of waterfall pull-
                // down is worse than the climb. Restored to the natural
                // 2m/0.5m vanilla-style tuning so disks form nice shores
                // and dragging the ruler up an elevation gives a waterfall.
                BankExtensionDistance = 0.01f,   // 0.2.752: placed water ends at its circles (was 2 m past them)
                BankDepthBelowTerrain = 0.5f,
                // Per-liquid offset. Water sits slightly ABOVE terrain
                // (small lift = clean read from above, no Z-fighting).
                // Lava sits BELOW terrain so it flood-fills low spots
                // flush with the surrounding terrain edge (player walking
                // toward a lava tile drops into it instead of standing on
                // a hovering sheet). User-requested behavior 2026-05-25.
                WaterTerrainOffset = GetLiquidTerrainOffset(combiner != null ? combiner.CustomLiquidType : CustomLiquidType.Water),
                CenterChannelDepth = 0.05f,
                CenterChannelRadiusFraction = 0.3f,
                SurfaceSmoothPasses = 3,
                SmoothStrength = 0.35f,
                IsPondMode = isPondMode,
                SettledWaterLevel = settledWaterLevel,
                MinDiskY = minDiskY,
                MaxDiskY = maxDiskY,
                WaveIntensity = waveIntensity,
                Time = UnityEngine.Time.time,
                WindStrength = 0.5f
            };

            // Snapshot wind
            if (EnvMan.instance != null)
            {
                EnvMan.instance.GetWindData(out Vector4 wind1, out _, out _);
                input.WindStrength = wind1.w;
            }

            // Serialize disk data
            var diskList = new List<WaterDiskData>();
            foreach (var disk in waterDisks)
            {
                if (disk == null) continue;
                float depth = disk.MaxDepth > 0.1f ? disk.MaxDepth : 2f;
                disk.GetHalfExtents(out float halfW, out float halfL);
                diskList.Add(new WaterDiskData
                {
                    PosX = disk.Position.x,
                    PosY = disk.Position.y,
                    PosZ = disk.Position.z,
                    Radius = disk.Radius,
                    RadiusPlusBlend = disk.Radius + blendDistance,
                    MaxDepth = depth,
                    EdgeFadeStart = disk.Radius - input.EdgeFadeDistance,
                    CenterChannelRadius = disk.Radius * input.CenterChannelRadiusFraction,
                    Shape = (int)disk.Shape,
                    HalfWidth = halfW,
                    HalfLength = halfL
                });
            }
            input.Disks = diskList.ToArray();

            return input;
        }

        /// <summary>
        /// Shuts down the worker thread (call on world unload).
        /// Also called automatically by AsyncShutdownManager.
        /// </summary>
        public static void Shutdown()
        {
            _worker?.Shutdown();
            _worker = null;
        }

        /// <summary>
        /// Returns true if there are pending jobs.
        /// </summary>
        public static bool HasPendingJobs => _worker?.HasPendingJobs ?? false;

        #endregion

        #region Background Processing

        /// <summary>
        /// Processes a water mesh generation job on a background thread.
        /// Called by BackgroundWorker - must NOT call Unity API.
        /// </summary>
        private static void ProcessWaterMeshJob(WaterMeshJobInput input, WaterMeshJobOutput output)
        {

            int totalCells = input.GridWidth * input.GridHeight;

            // Step 1: Calculate water surface with gravity settling
            float[] surfaceHeights = new float[totalCells];
            CalculateSurfaceWithGravity(input, surfaceHeights);

            // Step 2: Fill small surface gaps
            FillSurfaceGaps(surfaceHeights, input);

            // Step 2.5: Gravity settle edges - pull down floating edges, fix buried water
            GravitySettleEdges(surfaceHeights, input);

            // Step 2.6: Channel depth normalization - maintain consistent depth
            // along river/stream centerlines using pre-sampled terrain heights.
            NormalizeChannelDepth(surfaceHeights, input);

            // Step 2.7: Terrain clip - kill cells where terrain rises above the
            // computed water surface. These are hillside cells that should not
            // have water. Without this, the rectangular grid fills the entire
            // bounding box with water, creating flat sheets that cut through hills.
            TerrainClipPass(surfaceHeights, input);

            // Step 3: Smooth water surface
            SmoothSurface(surfaceHeights, input);

            // Step 4: Calculate water depths
            float[] waterDepths = new float[totalCells];
            for (int i = 0; i < totalCells; i++)
            {
                if (surfaceHeights[i] < -50f)
                    waterDepths[i] = 0f;
                else
                    waterDepths[i] = surfaceHeights[i] - input.TerrainHeights[i];
            }

            // Step 5: Build mesh data
            BuildMeshData(input, surfaceHeights, waterDepths, output);

            output.SurfaceHeights = surfaceHeights;
            output.WaterDepths = waterDepths;
            output.IsComplete = true;
        }

        private static void CalculateSurfaceWithGravity(WaterMeshJobInput input, float[] surfaceHeights)
        {
            for (int z = 0; z < input.GridHeight; z++)
            {
                for (int x = 0; x < input.GridWidth; x++)
                {
                    int idx = z * input.GridWidth + x;
                    float worldX = input.GridOrigin.x + x * input.CellSize;
                    float worldZ = input.GridOrigin.z + z * input.CellSize;
                    float terrainY = input.TerrainHeights[idx];

                    float totalWeight = 0f;
                    bool hasContribution = false;
                    float maxDepth = 0f;
                    float minDistToEdge = float.MaxValue;

                    // Track nearest disk Y as fallback for river mode
                    float nearestCenterDist = float.MaxValue;
                    float nearestDiskY = 0f;

                    for (int d = 0; d < input.Disks.Length; d++)
                    {
                        ref var disk = ref input.Disks[d];

                        float distToEdge = GetEdgeDistanceForDisk(worldX, worldZ, ref disk);

                        if (distToEdge < minDistToEdge)
                            minDistToEdge = distToEdge;

                        float maxExtendedDist = input.BlendDistance + input.BankExtensionDistance;
                        if (distToEdge > maxExtendedDist)
                            continue;

                        // 2026-05-26 round-22 (mirrors sync path): ALWAYS
                        // track the nearest disk for any disk within range,
                        // regardless of which weight branch the cell falls
                        // into. Previously only the `weight > 0.001f` branch
                        // updated nearestCenterDist, so bank-only overflow-
                        // ring cells fell through to terrainY+offset in
                        // paintedSurfaceY and neutered the spike-kill
                        // threshold. See WaterMeshCombiner.MeshGen.cs
                        // for the matching diagnosis comment.
                        if (!input.IsPondMode)
                        {
                            float cdx0 = worldX - disk.PosX;
                            float cdz0 = worldZ - disk.PosZ;
                            float centerDist0 = (float)Math.Sqrt(cdx0 * cdx0 + cdz0 * cdz0);

                            if (centerDist0 < nearestCenterDist)
                            {
                                nearestCenterDist = centerDist0;
                                nearestDiskY = disk.PosY;
                            }
                        }

                        float weight = CalculateBlendWeightFromEdge(distToEdge, input);

                        if (weight > 0.001f)
                        {
                            hasContribution = true;
                            totalWeight += weight;
                            maxDepth = Math.Max(maxDepth, disk.MaxDepth);
                        }
                        else if (distToEdge > 0f && distToEdge <= maxExtendedDist)
                        {
                            hasContribution = true;
                            float bankWeight = 0.1f * (1f - (distToEdge - input.BlendDistance) / input.BankExtensionDistance);
                            bankWeight = Math.Max(0f, bankWeight);
                            if (bankWeight > 0.001f)
                            {
                                totalWeight += bankWeight;
                                maxDepth = Math.Max(maxDepth, disk.MaxDepth);
                            }
                        }
                    }

                    if (!hasContribution || totalWeight < 0.001f)
                    {
                        surfaceHeights[idx] = -100f;
                        continue;
                    }

                    float surfaceY;
                    if (input.IsPondMode)
                        surfaceY = input.SettledWaterLevel;
                    else
                    {
                        // RIVER MODE: Inverse-distance weighted blending (matches sync path).
                        // Each disk's Y is authoritative at its center.
                        float weightedY = 0f;
                        float totalIDW = 0f;

                        for (int d = 0; d < input.Disks.Length; d++)
                        {
                            ref var disk2 = ref input.Disks[d];
                            float edgeDist2 = GetEdgeDistanceForDisk(worldX, worldZ, ref disk2);

                            if (edgeDist2 > input.BlendDistance + input.BankExtensionDistance)
                                continue;

                            float cdx2 = worldX - disk2.PosX;
                            float cdz2 = worldZ - disk2.PosZ;
                            float centerDist2 = (float)Math.Sqrt(cdx2 * cdx2 + cdz2 * cdz2);

                            float epsilon = disk2.Radius * 0.3f;
                            float idw = 1f / ((centerDist2 + epsilon) * (centerDist2 + epsilon));

                            weightedY += disk2.PosY * idw;
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
                            surfaceY = terrainY + input.WaterTerrainOffset;
                        }

                        // No terrain cap - disk Y is authoritative.
                        // Terrain clip pass will remove cells where terrain rises above surface.
                    }

                    // 2026-05-25 round-17: capture the IDW-blended PAINTED
                    // surface BEFORE edge-fade / bank-extension. Used as
                    // a ceiling so bank-extension never pushes the mesh
                    // UP onto cliff terrain.
                    //
                    // 2026-05-26 round-22 (mirrors sync path): prefer
                    // nearestDiskY over surfaceY. If surfaceY came from
                    // the third fallback (terrainY + WaterTerrainOffset
                    // at line ~425), the spike-kill threshold downstream
                    // becomes `terrainY > terrainY + 0.77f` (impossible)
                    // and the bank-extend then writes cliff-height
                    // vertices — the upward spike pathology. The round-22
                    // tracking lift above guarantees nearestDiskY is set
                    // for every in-range cell, so this branch always wins.
                    float paintedSurfaceY = (nearestCenterDist < float.MaxValue - 1f)
                        ? nearestDiskY
                        : surfaceY;

                    // Center channel depression
                    if (input.CenterChannelDepth > 0f && totalWeight > 0.5f && minDistToEdge < -input.EdgeFadeDistance)
                    {
                        float centeredness = Clamp01(-minDistToEdge / 3f);
                        centeredness = centeredness * centeredness;
                        surfaceY -= centeredness * input.CenterChannelDepth;
                    }

                    // 2026-05-26 round-26: EDGE FADE TO TERRAIN REMOVED.
                    // Mirrors WaterMeshCombiner.MeshGen.cs sync path. Edge
                    // fade was producing upward spike fins at boundaries
                    // where terrain rose above the disk surface. Bank-
                    // extension below + the round-22 nearestDiskY clamp
                    // handle the gentle-shore + cliff-base cases cleanly
                    // without needing the fade.

                    // Bank extension logic
                    float minWaterY = terrainY + input.WaterTerrainOffset;

                    if (surfaceY <= minWaterY)
                    {
                        // 2026-05-25 round-17: kill the cell entirely when
                        // terrain rises far above the painted water level
                        // (cliff face / rock / tree base). Bank-extending
                        // there produces the upward mesh slivers users keep
                        // complaining about.
                        const float MAX_BANK_RISE_ABOVE_PAINTED = 0.75f;
                        if (terrainY > paintedSurfaceY + MAX_BANK_RISE_ABOVE_PAINTED)
                        {
                            surfaceHeights[idx] = -100f;
                            continue;
                        }

                        bool isNearWater = minDistToEdge < input.BankExtensionDistance;
                        if (isNearWater)
                        {
                            float bankPenetration = Clamp01((input.BankExtensionDistance - minDistToEdge) / input.BankExtensionDistance);
                            float bankSurfaceY = terrainY - input.BankDepthBelowTerrain * bankPenetration;
                            float absoluteMinY = surfaceY - maxDepth;
                            bankSurfaceY = Math.Max(bankSurfaceY, absoluteMinY);
                            // Ceiling: never push above painted water level.
                            bankSurfaceY = Math.Min(bankSurfaceY, paintedSurfaceY);
                            surfaceHeights[idx] = bankSurfaceY;
                        }
                        else
                        {
                            surfaceHeights[idx] = -100f;
                        }
                    }
                    else
                    {
                        surfaceHeights[idx] = surfaceY;
                    }
                }
            }
        }

        private static float CalculateBlendWeight(float dist, float radius, WaterMeshJobInput input)
        {
            float edgeDist = dist - radius;
            return CalculateBlendWeightFromEdge(edgeDist, input);
        }

        /// <summary>
        /// Calculates blend weight from a signed edge distance.
        /// Works for both circle and rectangular shapes.
        /// </summary>
        private static float CalculateBlendWeightFromEdge(float edgeDist, WaterMeshJobInput input)
        {
            if (edgeDist <= -input.EdgeFadeDistance)
                return 1f;
            else if (edgeDist <= 0f)
            {
                float t = -edgeDist / input.EdgeFadeDistance;
                return t;
            }
            else if (edgeDist <= input.BlendDistance)
            {
                float t = 1f - (edgeDist / input.BlendDistance);
                return t * 0.5f;
            }
            return 0f;
        }

        /// <summary>
        /// Calculates signed edge distance from a point to a disk shape.
        /// Negative = inside, Positive = outside.
        /// Supports circle, square, and rectangle shapes.
        /// </summary>
        private static float GetEdgeDistanceForDisk(float worldX, float worldZ, ref WaterDiskData disk)
        {
            float dx = worldX - disk.PosX;
            float dz = worldZ - disk.PosZ;

            if (disk.Shape == 0) // Circle
            {
                return (float)Math.Sqrt(dx * dx + dz * dz) - disk.Radius;
            }
            else // Square or Rectangle
            {
                float edgeX = Math.Abs(dx) - disk.HalfWidth;
                float edgeZ = Math.Abs(dz) - disk.HalfLength;

                if (edgeX <= 0f && edgeZ <= 0f)
                {
                    return Math.Max(edgeX, edgeZ);
                }
                else if (edgeX > 0f && edgeZ > 0f)
                {
                    return (float)Math.Sqrt(edgeX * edgeX + edgeZ * edgeZ);
                }
                else
                {
                    return Math.Max(edgeX, edgeZ);
                }
            }
        }

        /// <summary>
        /// Fills gaps between water disks by lateral expansion using minimum
        /// neighbor height. Runs multiple iterations for multi-cell gaps.
        /// Never pulls the surface UP - only extends laterally at or below
        /// existing water height to preserve the channel depth profile.
        /// </summary>
        private static void FillSurfaceGaps(float[] surfaceHeights, WaterMeshJobInput input)
        {
            int gridWidth = input.GridWidth;
            int gridHeight = input.GridHeight;

            int[] ddx = { -1, 1, 0, 0 };
            int[] ddz = { 0, 0, -1, 1 };

            for (int iteration = 0; iteration < 3; iteration++)
            {
                bool anyFilled = false;

                for (int z = 1; z < gridHeight - 1; z++)
                {
                    for (int x = 1; x < gridWidth - 1; x++)
                    {
                        int idx = z * gridWidth + x;
                        if (surfaceHeights[idx] > -50f) continue;

                        int validCount = 0;
                        float minNeighborY = float.MaxValue;

                        for (int d = 0; d < 4; d++)
                        {
                            int nidx = (z + ddz[d]) * gridWidth + (x + ddx[d]);
                            if (surfaceHeights[nidx] > -50f)
                            {
                                validCount++;
                                if (surfaceHeights[nidx] < minNeighborY)
                                    minNeighborY = surfaceHeights[nidx];
                            }
                        }

                        // Fill if 2+ neighbors have water (bridges narrow channels)
                        // Use MINIMUM neighbor height so we never pull the surface up
                        if (validCount >= 2 && minNeighborY < float.MaxValue - 1f)
                        {
                            float terrainHere = input.TerrainHeights[idx];
                            if (minNeighborY > terrainHere + input.WaterTerrainOffset)
                            {
                                surfaceHeights[idx] = minNeighborY;
                                anyFilled = true;
                            }
                        }
                    }
                }

                if (!anyFilled) break;
            }
        }

        /// <summary>
        /// Gravity settle pass: fixes water surface issues relative to terrain.
        /// 
        /// Background-thread safe (no Unity API) - operates on plain arrays.
        /// Mirrors WaterMeshCombiner.GravitySettleEdges().
        /// 
        /// THREE RULES:
        /// 1. FLOATING EDGE PULLDOWN: Edge cells where water is >0.5m above
        ///    terrain AND no nearby bank terrain contains it -> pull down to terrain.
        /// 2. DISK-CENTER BURIED FIX: Only push water UP if the grid cell is
        ///    near a disk's CENTER position and the surface is below terrain.
        ///    Edge/bank cells below terrain stay put (clip through naturally).
        /// 3. EDGE ABOVE-TERRAIN PULLDOWN: Edge cells floating above terrain
        ///    without bank support get pulled down to meet the bank.
        /// </summary>
        private static void GravitySettleEdges(float[] surfaceHeights, WaterMeshJobInput input)
        {
            int gridWidth = input.GridWidth;
            int gridHeight = input.GridHeight;
            float cellSize = input.CellSize;

            int[] ddx = { -1, 1, 0, 0 };
            int[] ddz = { 0, 0, -1, 1 };

            // A cell is "at a disk center" if it's within CENTER_TOLERANCE_CELLS
            // of any disk's center position in grid space.
            const int CENTER_TOLERANCE_CELLS = 2;

            for (int z = 0; z < gridHeight; z++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    int idx = z * gridWidth + x;
                    float surfaceY = surfaceHeights[idx];
                    if (surfaceY < -50f) continue;

                    float terrainY = input.TerrainHeights[idx];

                    // Determine if this cell is near any disk's center
                    bool isNearDiskCenter = false;
                    float cellWorldX = input.GridOrigin.x + x * cellSize;
                    float cellWorldZ = input.GridOrigin.z + z * cellSize;
                    float toleranceDist = CENTER_TOLERANCE_CELLS * cellSize;
                    float toleranceDistSq = toleranceDist * toleranceDist;

                    for (int d = 0; d < input.Disks.Length && !isNearDiskCenter; d++)
                    {
                        ref var disk = ref input.Disks[d];
                        float dx = cellWorldX - disk.PosX;
                        float dz2 = cellWorldZ - disk.PosZ;
                        float distSq = dx * dx + dz2 * dz2;
                        if (distSq <= toleranceDistSq)
                            isNearDiskCenter = true;
                    }

                    // ---- RULE 2: Disk-center buried fix ----
                    // Only push water UP if the cell is at a disk center and
                    // the surface ended up below terrain. This ensures the center
                    // of each disk is always visible. Edge cells below terrain
                    // stay put - they clip through bank naturally.
                    if (surfaceY < terrainY + input.WaterTerrainOffset)
                    {
                        if (isNearDiskCenter)
                        {
                            surfaceHeights[idx] = terrainY + input.WaterTerrainOffset;
                        }
                        // Non-center cells below terrain: leave alone (bank extension / clip-through)
                        continue;
                    }

                    // ---- RULE 1 & 3: Edge cell checks ----
                    bool isEdge = false;
                    if (x == 0 || x == gridWidth - 1 || z == 0 || z == gridHeight - 1)
                    {
                        isEdge = true;
                    }
                    else
                    {
                        for (int d = 0; d < 4; d++)
                        {
                            int nidx = (z + ddz[d]) * gridWidth + (x + ddx[d]);
                            if (surfaceHeights[nidx] < -50f)
                            {
                                isEdge = true;
                                break;
                            }
                        }
                    }

                    if (!isEdge) continue;

                    float floatHeight = surfaceY - terrainY;
                    if (floatHeight < 0.5f) continue; // Only fix significant floating

                    bool hasBankSupport = false;
                    int bankScanRadius = 2;
                    for (int bz = -bankScanRadius; bz <= bankScanRadius && !hasBankSupport; bz++)
                    {
                        for (int bx = -bankScanRadius; bx <= bankScanRadius && !hasBankSupport; bx++)
                        {
                            if (bx == 0 && bz == 0) continue;
                            int bnx = x + bx;
                            int bnz = z + bz;
                            if (bnx < 0 || bnx >= gridWidth || bnz < 0 || bnz >= gridHeight)
                                continue;
                            int bnidx = bnz * gridWidth + bnx;

                            if (surfaceHeights[bnidx] < -50f && input.TerrainHeights[bnidx] >= surfaceY - 0.5f)
                            {
                                hasBankSupport = true;
                            }
                        }
                    }

                    // 2026-05-30 round-55 — DISABLED edge gravity-settle
                    // pull-down (see WaterMeshCombiner.MeshGen.cs for the
                    // full rationale). The visible water surface must
                    // respect the ruler Y. Shell wall handles the visual
                    // bridge down to terrain.
                    if (!hasBankSupport)
                    {
                        // No-op: leave surface at IDW-computed height.
                    }
                }
            }
        }

        /// <summary>
        /// Cross-section flattening: enforces flat water across the channel width.
        ///
        /// Background-thread safe (no Unity API) - operates on plain arrays.
        /// Mirrors WaterMeshCombiner.NormalizeChannelDepth().
        ///
        /// Builds an ordered disk chain, scans the full centerline for bed height
        /// per segment, projects each grid cell onto the nearest segment, and
        /// forces all cells at the same longitudinal position to share the same
        /// flat water surface height. Allows RAISING cells that were incorrectly
        /// pulled down by bank slopes in the initial interpolation pass.
        /// Where terrain rises above the flat level, cells become bank extensions.
        /// </summary>
        private static void NormalizeChannelDepth(float[] surfaceHeights, WaterMeshJobInput input)
        {
            if (input.IsPondMode) return;
            if (input.Disks == null || input.Disks.Length < 2) return;

            int gridWidth = input.GridWidth;
            int gridHeight = input.GridHeight;
            float cellSize = input.CellSize;

            const float CHANNEL_TARGET_DEPTH = 1.2f;
            const float DEPTH_NORM_STRENGTH = 0.85f;
            const float DEPTH_NORM_MIN = 0.3f;
            const float DEPTH_NORM_MAX = 3.0f;
            const float BANK_DEPTH_BELOW_TERRAIN = 0.5f;

            int diskCount = input.Disks.Length;

            // --- Step 1: Extract disk positions ---
            float[] diskWorldX = new float[diskCount];
            float[] diskWorldZ = new float[diskCount];
            float[] diskWorldY = new float[diskCount];
            float[] diskRadii = new float[diskCount];
            for (int d = 0; d < diskCount; d++)
            {
                diskWorldX[d] = input.Disks[d].PosX;
                diskWorldZ[d] = input.Disks[d].PosZ;
                diskWorldY[d] = input.Disks[d].PosY;
                diskRadii[d] = input.Disks[d].Radius;
            }

            int[] chainOrder = BuildDiskChainStatic(diskWorldX, diskWorldZ, diskCount);

            // --- Step 2: Compute flat level per chain segment ---
            // Walk grid cells along the FULL segment centerline to find bed height.
            int segCount = diskCount - 1;
            float[] segFlatLevel = new float[segCount];

            for (int s = 0; s < segCount; s++)
            {
                int dA = chainOrder[s];
                int dB = chainOrder[s + 1];
                float ax = diskWorldX[dA], az = diskWorldZ[dA];
                float bx = diskWorldX[dB], bz = diskWorldZ[dB];
                float avgRadius = (diskRadii[dA] + diskRadii[dB]) * 0.5f;

                float scanWidth = Math.Max(avgRadius * 0.4f, cellSize * 2f);
                int perpCells = Math.Max(1, (int)Math.Ceiling(scanWidth / cellSize));

                // Walk along the segment centerline in cell-sized steps
                float segDx = bx - ax;
                float segDz = bz - az;
                float segLen = (float)Math.Sqrt(segDx * segDx + segDz * segDz);
                int walkSteps = Math.Max(1, (int)Math.Ceiling(segLen / cellSize));

                float minBed = float.MaxValue;
                for (int ws = 0; ws <= walkSteps; ws++)
                {
                    float wt = (float)ws / walkSteps;
                    float sampleX = ax + segDx * wt;
                    float sampleZ = az + segDz * wt;

                    float localX = sampleX - input.GridOrigin.x;
                    float localZ = sampleZ - input.GridOrigin.z;
                    int centerGX = Clamp(RoundToInt(localX / cellSize), 0, gridWidth - 1);
                    int centerGZ = Clamp(RoundToInt(localZ / cellSize), 0, gridHeight - 1);

                    for (int sz = -perpCells; sz <= perpCells; sz++)
                    {
                        for (int sx = -perpCells; sx <= perpCells; sx++)
                        {
                            int gx = centerGX + sx;
                            int gz = centerGZ + sz;
                            if (gx < 0 || gx >= gridWidth || gz < 0 || gz >= gridHeight)
                                continue;
                            int gidx = gz * gridWidth + gx;
                            if (surfaceHeights[gidx] > -50f && surfaceHeights[gidx] >= input.TerrainHeights[gidx])
                            {
                                if (input.TerrainHeights[gidx] < minBed)
                                    minBed = input.TerrainHeights[gidx];
                            }
                        }
                    }
                }

                float interpY = (diskWorldY[dA] + diskWorldY[dB]) * 0.5f;
                float bedFloor = (minBed < float.MaxValue - 1f) ? (minBed + input.WaterTerrainOffset) : interpY;
                segFlatLevel[s] = Math.Max(interpY, bedFloor);
            }

            // --- Step 3: Project each grid cell and enforce flat level ---
            for (int z = 0; z < gridHeight; z++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    int idx = z * gridWidth + x;
                    float surfaceY = surfaceHeights[idx];
                    if (surfaceY < -50f) continue;

                    float terrainY = input.TerrainHeights[idx];
                    if (surfaceY < terrainY - 0.1f) continue;

                    float cellWX = input.GridOrigin.x + x * cellSize;
                    float cellWZ = input.GridOrigin.z + z * cellSize;

                    float bestT = 0f;
                    int bestSeg = -1;
                    float bestLatDistSq = float.MaxValue;

                    for (int s = 0; s < segCount; s++)
                    {
                        int dA = chainOrder[s];
                        int dB = chainOrder[s + 1];
                        float abx = diskWorldX[dB] - diskWorldX[dA];
                        float abz = diskWorldZ[dB] - diskWorldZ[dA];
                        float acx = cellWX - diskWorldX[dA];
                        float acz = cellWZ - diskWorldZ[dA];
                        float abLenSq = abx * abx + abz * abz;
                        float t = (abLenSq > 0.001f) ? ((acx * abx + acz * abz) / abLenSq) : 0f;
                        if (t < 0f) t = 0f;
                        if (t > 1f) t = 1f;

                        float projX = diskWorldX[dA] + abx * t;
                        float projZ = diskWorldZ[dA] + abz * t;
                        float dx = cellWX - projX;
                        float dz2 = cellWZ - projZ;
                        float latSq = dx * dx + dz2 * dz2;

                        if (latSq < bestLatDistSq)
                        {
                            bestLatDistSq = latSq;
                            bestSeg = s;
                            bestT = t;
                        }
                    }

                    if (bestSeg < 0) continue;

                    float flatLevel;
                    if (bestSeg > 0 && bestT < 0.3f)
                    {
                        float blend = bestT / 0.3f;
                        flatLevel = segFlatLevel[bestSeg - 1] + (segFlatLevel[bestSeg] - segFlatLevel[bestSeg - 1]) * blend;
                    }
                    else if (bestSeg < segCount - 1 && bestT > 0.7f)
                    {
                        float blend = (bestT - 0.7f) / 0.3f;
                        flatLevel = segFlatLevel[bestSeg] + (segFlatLevel[bestSeg + 1] - segFlatLevel[bestSeg]) * blend;
                    }
                    else
                    {
                        flatLevel = segFlatLevel[bestSeg];
                    }

                    int dA2 = chainOrder[bestSeg];
                    int dB2 = chainOrder[bestSeg + 1];
                    float avgR = (diskRadii[dA2] + diskRadii[dB2]) * 0.5f;
                    float lateralDist = (float)Math.Sqrt(bestLatDistSq);
                    float normLateral = lateralDist / Math.Max(1f, avgR);

                    // Full correction within 60% of radius, fading to 0 at 110%
                    float edgeFactor = Clamp01((1.1f - normLateral) / 0.5f);

                    if (edgeFactor < 0.01f) continue;

                    float localFloor = terrainY + input.WaterTerrainOffset;

                    // Bank: terrain above flat level -> convert to bank extension
                    if (terrainY > flatLevel + 0.1f)
                    {
                        surfaceHeights[idx] = terrainY - BANK_DEPTH_BELOW_TERRAIN;
                        continue;
                    }

                    float targetY = flatLevel;
                    if (targetY < localFloor)
                        targetY = localFloor;

                    // Allow both raising and lowering to the flat level.
                    // The flat level is already capped by disk heights
                    // (monotonic downhill profile from AutoWaterGenerator).
                    float strength = DEPTH_NORM_STRENGTH * edgeFactor;
                    float newSurface = surfaceY + (targetY - surfaceY) * strength;

                    if (newSurface < localFloor)
                        newSurface = localFloor;

                    surfaceHeights[idx] = newSurface;
                }
            }
        }

        /// <summary>
        /// Builds an ordered chain through disk centers via nearest-neighbor linking.
        /// Thread-safe static version (no Unity API).
        /// </summary>
        private static int[] BuildDiskChainStatic(float[] diskX, float[] diskZ, int count)
        {
            if (count <= 0) return new int[0];
            if (count == 1) return new int[] { 0 };

            float cx = 0f, cz = 0f;
            for (int i = 0; i < count; i++) { cx += diskX[i]; cz += diskZ[i]; }
            cx /= count; cz /= count;

            int startIdx = 0;
            float maxDistSq = 0f;
            for (int i = 0; i < count; i++)
            {
                float dx = diskX[i] - cx;
                float dz = diskZ[i] - cz;
                float dSq = dx * dx + dz * dz;
                if (dSq > maxDistSq) { maxDistSq = dSq; startIdx = i; }
            }

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
        /// and BEFORE SmoothSurface (which would blur killed cells into neighbors).
        ///
        /// Background-thread safe - pure array math.
        /// </summary>
        private static void TerrainClipPass(float[] surfaceHeights, WaterMeshJobInput input)
        {
            int totalCells = input.GridWidth * input.GridHeight;

            // Tolerance: terrain must be this much above water surface to clip.
            // Small value (0.05m) so even shallow water is preserved, but any
            // cell where terrain clearly rises above the water plane gets killed.
            const float CLIP_TOLERANCE = 0.05f;

            for (int i = 0; i < totalCells; i++)
            {
                float surfaceY = surfaceHeights[i];
                if (surfaceY < -50f) continue; // Already empty

                float terrainY = input.TerrainHeights[i];

                // Skip bank extensions - these are intentionally below terrain
                if (surfaceY < terrainY - 0.01f) continue;

                // If terrain rises above the water surface, this cell is on a
                // hillside or bank slope - remove the water entirely.
                if (terrainY > surfaceY + CLIP_TOLERANCE)
                {
                    surfaceHeights[i] = -100f;
                }
            }
        }

        /// <summary>
        /// Thread-safe integer rounding.
        /// </summary>
        private static int RoundToInt(float value)
        {
            return (int)Math.Round(value);
        }

        /// <summary>
        /// Thread-safe integer clamp.
        /// </summary>
        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static void SmoothSurface(float[] surfaceHeights, WaterMeshJobInput input)
        {
            int gridWidth = input.GridWidth;
            int gridHeight = input.GridHeight;
            int totalCells = gridWidth * gridHeight;
            float[] smoothed = new float[totalCells];

            for (int pass = 0; pass < input.SurfaceSmoothPasses; pass++)
            {
                Array.Copy(surfaceHeights, smoothed, totalCells);

                for (int z = 1; z < gridHeight - 1; z++)
                {
                    for (int x = 1; x < gridWidth - 1; x++)
                    {
                        int idx = z * gridWidth + x;
                        if (surfaceHeights[idx] < -50f) continue;

                        float currentY = surfaceHeights[idx];
                        float terrainY = input.TerrainHeights[idx];
                        bool isBankExtension = currentY < terrainY;

                        float sum = 0f;
                        float weightSum = 0f;

                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                int nz = z + dz;
                                if (nx < 0 || nx >= gridWidth || nz < 0 || nz >= gridHeight)
                                    continue;

                                int nidx = nz * gridWidth + nx;
                                if (surfaceHeights[nidx] > -50f)
                                {
                                    // Skip bank extension neighbors for non-bank
                                    // cells - their terrain-relative heights pull
                                    // the average toward bank elevation, causing
                                    // the "water riding up banks" artifact.
                                    float nTerrainY = input.TerrainHeights[nidx];
                                    if (surfaceHeights[nidx] < nTerrainY && !isBankExtension)
                                        continue;

                                    float dist = (float)Math.Sqrt(dx * dx + dz * dz);
                                    float weight = 1f / (1f + dist);
                                    sum += surfaceHeights[nidx] * weight;
                                    weightSum += weight;
                                }
                            }
                        }

                        if (weightSum > 0)
                        {
                            float neighborAvg = sum / weightSum;
                            float blended = currentY + (neighborAvg - currentY) * input.SmoothStrength;

                            if (isBankExtension)
                            {
                                blended = Math.Min(blended, terrainY - input.WaterTerrainOffset);
                            }
                            else
                            {
                                // Never raise surface during smoothing - preserves
                                // the monotonic downhill flow profile from AutoWaterGenerator
                                if (blended > currentY)
                                    blended = currentY;

                                if (blended < terrainY + input.WaterTerrainOffset)
                                {
                                    if (currentY > terrainY + input.WaterTerrainOffset)
                                        blended = currentY;
                                    else
                                        blended = terrainY - input.BankDepthBelowTerrain * 0.5f;
                                }
                            }

                            smoothed[idx] = blended;
                        }
                    }
                }

                Array.Copy(smoothed, surfaceHeights, totalCells);
            }

            // Final z-fighting validation
            for (int i = 0; i < totalCells; i++)
            {
                if (surfaceHeights[i] > -50f)
                {
                    float terrainY = input.TerrainHeights[i];
                    float distFromTerrain = Math.Abs(surfaceHeights[i] - terrainY);
                    if (distFromTerrain < input.WaterTerrainOffset * 2f)
                    {
                        if (surfaceHeights[i] >= terrainY)
                            surfaceHeights[i] = terrainY + input.WaterTerrainOffset;
                        else
                            surfaceHeights[i] = terrainY - input.WaterTerrainOffset;
                    }
                }
            }
        }

        private static void BuildMeshData(WaterMeshJobInput input, float[] surfaceHeights, float[] waterDepths, WaterMeshJobOutput output)
        {
            int gridWidth = input.GridWidth;
            int gridHeight = input.GridHeight;
            int totalCells = gridWidth * gridHeight;

            bool[] validCells = new bool[totalCells];
            for (int i = 0; i < totalCells; i++)
                validCells[i] = surfaceHeights[i] > -50f;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();

            int[] vertexIndices = new int[totalCells];
            for (int i = 0; i < totalCells; i++)
                vertexIndices[i] = -1;

            int vertexIndex = 0;

            for (int z = 0; z < gridHeight; z++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    int idx = z * gridWidth + x;
                    if (!validCells[idx]) continue;

                    // Quad validation
                    bool isPartOfQuad = false;
                    for (int dz = -1; dz <= 0 && !isPartOfQuad; dz++)
                    {
                        for (int dx = -1; dx <= 0 && !isPartOfQuad; dx++)
                        {
                            int qx = x + dx;
                            int qz = z + dz;
                            if (qx < 0 || qz < 0 || qx >= gridWidth - 1 || qz >= gridHeight - 1)
                                continue;

                            int i0 = qz * gridWidth + qx;
                            int i1 = qz * gridWidth + qx + 1;
                            int i2 = (qz + 1) * gridWidth + qx;
                            int i3 = (qz + 1) * gridWidth + qx + 1;

                            if (validCells[i0] && validCells[i1] && validCells[i2] && validCells[i3])
                                isPartOfQuad = true;
                        }
                    }

                    if (!isPartOfQuad) continue;

                    float worldX = input.GridOrigin.x + x * input.CellSize;
                    float worldZ = input.GridOrigin.z + z * input.CellSize;

                    float depth = waterDepths[idx];
                    float waveOffset = CalculateWaveHeight(worldX, worldZ, depth, input);

                    float localX = worldX - input.CombinerPosition.x;
                    float localY = surfaceHeights[idx] + waveOffset - input.CombinerPosition.y;
                    float localZ = worldZ - input.CombinerPosition.z;

                    vertices.Add(new Vector3(localX, localY, localZ));
                    uvs.Add(new Vector2(worldX * 0.1f, worldZ * 0.1f));
                    normals.Add(new Vector3(0f, 1f, 0f));

                    float depthNorm = Clamp01(depth / 5f);
                    colors.Add(new Color(depthNorm, depthNorm, 1f, 1f));

                    vertexIndices[idx] = vertexIndex++;
                }
            }

            // Build triangles
            for (int z = 0; z < gridHeight - 1; z++)
            {
                for (int x = 0; x < gridWidth - 1; x++)
                {
                    int i0 = z * gridWidth + x;
                    int i1 = z * gridWidth + x + 1;
                    int i2 = (z + 1) * gridWidth + x;
                    int i3 = (z + 1) * gridWidth + x + 1;

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

            output.Vertices = vertices.ToArray();
            output.Triangles = triangles.ToArray();
            output.UVs = uvs.ToArray();
            output.Normals = normals.ToArray();
            output.Colors = colors.ToArray();
        }

        /// <summary>
        /// Thread-safe wave height calculation (no Unity API calls).
        /// </summary>
        private static float CalculateWaveHeight(float worldX, float worldZ, float depth, WaterMeshJobInput input)
        {
            if (input.WaveIntensity <= 0.001f) return 0f;

            float time = input.Time;

            float wave1 = (float)Math.Sin(worldX * 0.08 + worldZ * 0.056 + time * 0.4) * 0.15f;
            float wave2 = (float)Math.Sin(worldX * 0.12 - worldZ * 0.15 + time * 0.6) * 0.08f;
            float wave3 = (float)Math.Sin(worldX * 0.25 + worldZ * 0.3 + time * 0.9) * 0.04f;

            float totalWave = (wave1 + wave2 + wave3) * Clamp01(input.WindStrength * 0.5f + 0.5f);

            float depthFactor = Clamp01(depth / 2f);

            return totalWave * depthFactor * input.WaveIntensity;
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        #endregion
    }
}
