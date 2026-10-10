using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Singleton manager for all custom water instances.
    /// 
    /// Supports TWO water systems:
    /// 1. NEW: Terrain-style painted water (WaterPaintController + WaterMeshCombiner)
    ///    - Paint water surface level, terrain acts as container
    ///    - Adjacent paints blend together
    ///    - Similar to DirtFloor terrain system
    /// 
    /// 2. LEGACY: Box-based water (CustomPondController, etc.)
    ///    - Static bounds-contained water
    ///    - Still works for specific use cases
    /// 
    /// Provides global queries for water level at positions, registration, and factory methods.
    /// </summary>
    public class CustomWaterManager : MonoBehaviour
    {
        #region Singleton

        private static CustomWaterManager s_instance;
        public static CustomWaterManager Instance
        {
            get
            {
                if (s_instance == null)
                {
                    var go = new GameObject("CustomWaterManager");
                    s_instance = go.AddComponent<CustomWaterManager>();
                    DontDestroyOnLoad(go);
                }
                return s_instance;
            }
        }

        #endregion

        #region Registry (Legacy Box-Based Water)

        private static List<CustomWaterBase> s_allWater = new List<CustomWaterBase>();
        private static List<CustomPondController> s_ponds = new List<CustomPondController>();
        private static List<CustomRiverController> s_rivers = new List<CustomRiverController>();
        private static List<CustomWaterfallController> s_waterfalls = new List<CustomWaterfallController>();

        public static IReadOnlyList<CustomWaterBase> AllWater => s_allWater;
        public static IReadOnlyList<CustomPondController> Ponds => s_ponds;
        public static IReadOnlyList<CustomRiverController> Rivers => s_rivers;
        public static IReadOnlyList<CustomWaterfallController> Waterfalls => s_waterfalls;

        public static void Register(CustomWaterBase water)
        {
            if (water == null || s_allWater.Contains(water)) return;

            s_allWater.Add(water);

            if (water is CustomPondController pond)
                s_ponds.Add(pond);
            else if (water is CustomRiverController river)
                s_rivers.Add(river);
            else if (water is CustomWaterfallController waterfall)
                s_waterfalls.Add(waterfall);

            FiresLogger.LogVerbose($"[CustomWaterManager] Registered {water.GetWaterType()} at {water.transform.position}");
        }

        public static void Unregister(CustomWaterBase water)
        {
            if (water == null) return;

            s_allWater.Remove(water);

            if (water is CustomPondController pond)
                s_ponds.Remove(pond);
            else if (water is CustomRiverController river)
                s_rivers.Remove(river);
            else if (water is CustomWaterfallController waterfall)
                s_waterfalls.Remove(waterfall);
        }

        #endregion

        #region Global Queries

        /// <summary>
        /// Gets the water surface level at a world position.
        /// Checks BOTH new terrain-style water (WaterMeshCombiner) AND legacy box-based water.
        /// Returns float.MinValue if no water at position.
        ///
        /// 2026-05-25 round-18: SOLID liquid types (Snow, Ice) are excluded
        /// from this query. They report a surface Y like everything else for
        /// internal mesh-gen purposes, but the consumers of this method are
        /// Character.InWater / InLiquid / IsSwimming / GetLiquidLevel
        /// patches in CustomWaterPatches — those treat any returned level as
        /// "player is swimming in water". For a snow drift that means
        /// walking UNDER it = treated as swimming, which is the bug the
        /// user kept reporting. Ice is similarly solid (you fall through
        /// holes for swim detection, but the ice surface itself isn't
        /// water). Lava/Tar/Water/BoilingWater/FreezingWater all stay in.
        /// </summary>
        private const float ShaftBedMargin = 0.3f;   // 0.2.814: just below the river bed, the shaft is dry
        public static float GetWaterLevelAt(Vector3 worldPos)
        {
            float highestLevel = float.MinValue;

            // Check NEW terrain-style water combiners first (priority)
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null) continue;
                if (combiner.CustomLiquidType.IsSolid()) continue;

                float level = combiner.GetWaterSurfaceAt(worldPos);
                if (level > highestLevel)
                    highestLevel = level;
            }

            // Check legacy box-based water
            foreach (var water in s_allWater)
            {
                if (water == null) continue;

                float level = water.GetWaterSurfaceAt(worldPos);
                if (level > highestLevel)
                    highestLevel = level;
            }

            // GENERATED river/lake water. It reports here like every other custom
            // water source, so vanilla's swim path (the Character.InWater /
            // InLiquid / IsSwimming / GetLiquidLevel patches) treats it EXACTLY
            // as it treats the ocean. It used to drive swimming through its own
            // trigger calling SetLiquidLevel, which fought vanilla's own
            // per-frame level writes and bounced the player between swim and
            // walk every few frames.
            float gen = VerdantsAscent.Terrain.Voxel.VoxelWaterSurface.SurfaceAt(worldPos);
            // 0.2.814 (Fire 10-06: swimming in mid-air in a big sinkhole's shaft): inside the shaft and below the river bed at
            // its lip, the river above is not this point's water; the pond's own sheet (a combiner, above) is.
            if (VerdantsAscent.Terrain.Voxel.FatSinkFallCuts.BelowShaftBed(worldPos, ShaftBedMargin)) gen = float.MinValue;
            if (gen > highestLevel)
                highestLevel = gen;

            return highestLevel;
        }

        /// <summary>
        /// Gets the custom water instance at a world position, if any.
        /// Checks both new and legacy water systems.
        /// </summary>
        public static CustomWaterBase GetWaterAt(Vector3 worldPos)
        {
            // Check legacy box-based water (returns CustomWaterBase)
            foreach (var water in s_allWater)
            {
                if (water == null) continue;

                if (water.IsInsideBounds(worldPos))
                    return water;
            }

            return null;
        }

        /// <summary>
        /// Gets the WaterMeshCombiner at a world position, if any (new system).
        /// </summary>
        public static WaterMeshCombiner GetWaterCombinerAt(Vector3 worldPos)
        {
            return WaterMeshCombiner.GetCombinerAt(worldPos);
        }

        /// <summary>
        /// Gets the river at a world position, if any.
        /// </summary>
        public static CustomRiverController GetRiverAt(Vector3 worldPos)
        {
            foreach (var river in s_rivers)
            {
                if (river == null) continue;

                if (river.IsInsideBounds(worldPos))
                    return river;
            }

            return null;
        }

        /// <summary>
        /// Finds all custom water within a radius of a position.
        /// </summary>
        public static List<CustomWaterBase> GetWaterInRadius(Vector3 center, float radius)
        {
            var result = new List<CustomWaterBase>();
            float radiusSq = radius * radius;

            foreach (var water in s_allWater)
            {
                if (water == null) continue;

                float distSq = (water.transform.position - center).sqrMagnitude;
                if (distSq <= radiusSq)
                    result.Add(water);
            }

            return result;
        }

        /// <summary>
        /// Checks if a position is inside any custom water body.
        /// </summary>
        public static bool IsPositionInWater(Vector3 worldPos)
        {
            // Check new terrain-style water. Skip SOLID liquid types (Snow,
            // Ice) — see GetWaterLevelAt comment. They are NOT liquid for
            // swim/wet/in-water character-state queries.
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null) continue;
                if (combiner.CustomLiquidType.IsSolid()) continue;
                if (combiner.IsPositionInWater(worldPos))
                    return true;
            }

            // Check legacy water
            foreach (var water in s_allWater)
            {
                if (water != null && water.IsInsideBounds(worldPos))
                {
                    float surface = water.GetWaterSurfaceAt(worldPos);
                    if (surface > float.MinValue && worldPos.y < surface)
                        return true;
                }
            }

            return false;
        }

        #endregion

        #region Factory Methods (New Terrain-Style Water)

        /// <summary>
        /// Creates a new water disk at the specified position (terrain-style).
        /// The disk will automatically register with a WaterMeshCombiner.
        /// </summary>
        public static WaterDisk CreateWaterDisk(Vector3 position, float radius, float maxDepth = 5f, LiquidType liquidType = LiquidType.Water)
        {
            var disk = new WaterDisk(position, radius, maxDepth, liquidType);

            // Register with combiner system, through the server so it persists (0.2.681, see Place)
            Place(disk);

            FiresLogger.LogVerbose($"[CustomWaterManager] Created water disk at {position}, radius={radius}");
            return disk;
        }

        /// <summary>
        /// Convenience wrapper that creates a CustomLiquidType.Lava disk.
        /// Used by LavaPlacement when the user paints AshLands biome — the
        /// disk's 1m-tall lava mesh sits flush with surrounding terrain
        /// (player falls in through the no-collision mesh).
        /// </summary>
        public static WaterDisk CreateLavaDisk(Vector3 position, float radius, float maxDepth = 5f)
        {
            return CreateDiskOfType(position, radius, maxDepth, CustomLiquidType.Lava);
        }

        /// <summary>
        /// Generic factory that creates a disk of the requested CustomLiquidType
        /// and routes it to a combiner of the matching type.
        ///
        /// 2026-05-25 round-2: With type-aware combiner separation in
        /// WaterMeshCombiner.GetOrCreateCombinerFor, a disk of type X is
        /// guaranteed to land in a combiner of type X — either a freshly
        /// created one or an existing one that already matched. No
        /// post-construction SetCustomLiquidType needed (and we explicitly
        /// don't want it: SetCustomLiquidType flips the WHOLE combiner's
        /// material, which is the exact bug we're avoiding now).
        /// </summary>
        public static WaterDisk CreateDiskOfType(Vector3 position, float radius, float maxDepth, CustomLiquidType liquid)
        {
            var disk = new WaterDisk(position, radius, maxDepth, liquid);
            Place(disk);
            FiresLogger.LogVerbose($"[CustomWaterManager] Created {liquid} disk at {position}, radius={radius}");
            return disk;
        }

        // 0.2.681 ([lead] HR3, 10-01: the H8 va_pool pond was gone after a reload, its bed still carved). Only the server
        // persists water (WaterMeshCombiner.PerformDebouncedSave / SaveToAnchor return on a client), and these factories
        // built the combiner locally only, so a disk placed by a client of a dedicated server was never sent to the server:
        // not saved (the dedi's WaterCache\HomesteadTest_water.bin was an empty 30-byte header) and never seen by other
        // players. They now go through WaterOperationRPC like WaterPlacementTool: applied locally, sent to the server, which
        // saves it to its anchor + cache and broadcasts it. Without a running world the old local-only path stays.
        private static void Place(WaterDisk disk)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                WaterMeshCombiner.GetOrCreateCombinerFor(disk);
                return;
            }
            WaterOperationRPC.RequestCreateWaterWithCustomType(disk.Position, disk, disk.CustomLiquidType);
        }

        #endregion

        #region Factory Methods (Legacy Box-Based Water)

        /// <summary>
        /// Spawns a new custom pond at the specified position with optional persistence.
        /// </summary>
        /// <param name="persist">If true, creates a ZDO anchor for world persistence.</param>
        public static CustomPondController SpawnPond(Vector3 position, Vector3 size, float waterLevel = 0.5f, bool persist = false)
        {
            var go = new GameObject("CustomPond");
            go.transform.position = position;

            var pond = go.AddComponent<CustomPondController>();
            pond.BoundsSize = size;
            pond.WaterLevel = waterLevel;

            // Create persistence anchor if requested
            if (persist && ZNetScene.instance != null)
            {
                WaterAnchor.CreateAnchor(position, pond);
            }

            return pond;
        }

        /// <summary>
        /// Spawns a new custom river at the specified position with optional persistence.
        /// NOTE: For terrain-style water, use CreateWaterDisk() instead.
        /// </summary>
        /// <param name="persist">If true, creates a ZDO anchor for world persistence.</param>
        public static CustomRiverController SpawnRiver(Vector3 position, Vector3 size, float flowDirection = 0f, float flowSpeed = 2f, bool persist = false)
        {
            var go = new GameObject("CustomRiver");
            go.transform.position = position;

            var river = go.AddComponent<CustomRiverController>();
            river.BoundsSize = size;
            river.FlowDirection = flowDirection;
            river.FlowSpeed = flowSpeed;

            // Create persistence anchor if requested
            if (persist && ZNetScene.instance != null)
            {
                WaterAnchor.CreateAnchor(position, river);
            }

            return river;
        }

        /// <summary>
        /// Spawns a new custom waterfall at the specified position with optional persistence.
        /// Usually called by rivers when they detect cliff edges.
        /// </summary>
        /// <param name="persist">If true, creates a ZDO anchor for world persistence.</param>
        public static CustomWaterfallController SpawnWaterfall(Vector3 position, float dropHeight, float flowRate, CustomRiverController parentRiver = null, bool persist = false)
        {
            var go = new GameObject("CustomWaterfall");
            go.transform.position = position;

            var waterfall = go.AddComponent<CustomWaterfallController>();
            waterfall.DropHeight = dropHeight;
            waterfall.FlowRate = flowRate;
            waterfall.ParentRiver = parentRiver;

            // Create persistence anchor if requested
            if (persist && ZNetScene.instance != null)
            {
                WaterAnchor.CreateAnchor(position, waterfall);
            }

            return waterfall;
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Removes all custom water in a zone.
        /// Also permanently destroys WaterMeshCombiners and their anchor ZDOs.
        /// </summary>
        public static void ClearZone(Vector2i zone)
        {
            int count = 0;

            // Clear legacy water
            var toRemove = new List<CustomWaterBase>();

            foreach (var water in s_allWater)
            {
                if (water == null) continue;

                // GetZone returns Vector2s on public test; convert back so the
                // == compare against the Vector2i `zone` parameter still type-checks.
                Vector2i waterZone = ZoneSystem.instance != null ? ZoneSystem.GetZone(water.transform.position).ToVector2i() : Vector2i.zero;
                if (waterZone == zone)
                    toRemove.Add(water);
            }

            foreach (var water in toRemove)
            {
                if (water != null)
                {
                    Destroy(water.gameObject);
                    count++;
                }
            }

            // Clear WaterMeshCombiners in this zone (permanently, with anchor ZDO cleanup)
            var combinersToRemove = new List<WaterMeshCombiner>();
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null) continue;

                // Same Vector2s → Vector2i conversion as waterZone above.
                Vector2i combinerZone = ZoneSystem.instance != null ? ZoneSystem.GetZone(combiner.transform.position).ToVector2i() : Vector2i.zero;
                if (combinerZone == zone)
                    combinersToRemove.Add(combiner);
            }

            foreach (var combiner in combinersToRemove)
            {
                if (combiner != null)
                {
                    combiner.DestroyPermanently();
                    count++;
                }
            }

            FiresLogger.LogVerbose($"[CustomWaterManager] Cleared {count} water instances from zone {zone}");
        }

        /// <summary>
        /// Removes all custom water in a radius.
        /// Also permanently destroys WaterMeshCombiners and their anchor ZDOs.
        /// </summary>
        public static int ClearRadius(Vector3 center, float radius)
        {
            int count = 0;

            // Clear legacy water
            var toRemove = GetWaterInRadius(center, radius);
            foreach (var water in toRemove)
            {
                if (water != null)
                {
                    Destroy(water.gameObject);
                    count++;
                }
            }

            // Clear terrain-style water paint controllers
            var paintsToRemove = new List<WaterPaintController>();
            foreach (var paint in WaterPaintController.AllWaterPaints)
            {
                if (paint == null) continue;
                float dist = Vector3.Distance(paint.transform.position, center);
                if (dist <= radius)
                    paintsToRemove.Add(paint);
            }

            foreach (var paint in paintsToRemove)
            {
                if (paint != null)
                {
                    Destroy(paint.gameObject);
                    count++;
                }
            }

            // Clear WaterMeshCombiners within radius (permanently, with anchor ZDO cleanup)
            var combinersToRemove = WaterMeshCombiner.GetCombinersInRadius(center, radius);
            foreach (var combiner in combinersToRemove)
            {
                if (combiner != null)
                {
                    combiner.DestroyPermanently();
                    count++;
                }
            }

            return count;
        }

        #endregion

        #region Water Stitching

        /// <summary>
        /// Triggers stitching for all rivers near a position.
        /// Call this after placing new water to ensure seamless connections.
        /// </summary>
        public static void StitchWaterNear(Vector3 position, float radius = 50f)
        {
            var nearbyWater = GetWaterInRadius(position, radius);

            foreach (var water in nearbyWater)
            {
                if (water is CustomRiverController river)
                {
                    river.StitchWithAdjacentWater();
                }
            }
        }

        /// <summary>
        /// Gets the combined water level at a position from all water sources.
        /// </summary>
        public static float GetCombinedWaterLevel(Vector3 worldPos)
        {
            float customLevel = GetWaterLevelAt(worldPos);

            // Also check vanilla water
            float vanillaLevel = -10000f;
            if (ZoneSystem.instance != null)
            {
                vanillaLevel = ZoneSystem.instance.m_waterLevel;
            }

            // Also check WaterVolume instances
            if (WaterVolume.Instances != null)
            {
                foreach (var wv in WaterVolume.Instances)
                {
                    if (wv == null) continue;
                    float level = wv.GetWaterSurface(worldPos, 1f);
                    if (level > vanillaLevel)
                        vanillaLevel = level;
                }
            }

            return Mathf.Max(customLevel, vanillaLevel);
        }

        #endregion
    }
}
