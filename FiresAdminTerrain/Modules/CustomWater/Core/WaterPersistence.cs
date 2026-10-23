using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Handles persistence for terrain-style custom water (WaterDisk/WaterMeshCombiner).
    /// 
    /// PERSISTENCE STRATEGY v6 - FILE CACHE + ZDO ANCHOR (like terrain):
    /// 
    /// TWO persistence layers work together:
    /// 
    /// 1. FILE CACHE (WaterCombinerCache - primary for fast restoration):
    ///    - Local .bin file stores all combiner grid data per world
    ///    - On server start, LoadWaterData() proactively restores all combiners from cache
    ///    - No zone loading required - water is available immediately
    ///    - Saved on world save, shutdown, and logout
    /// 
    /// 2. ZDO ANCHORS (WaterMeshCombinerAnchor - secondary, source of truth):
    ///    - Each combiner has an invisible anchor ZDO (CustomRiver prefab at Y=-10)
    ///    - When zones load, anchors spawn and link to existing cached combiners
    ///    - If cache is missing, anchors restore from their own ZDO data (fallback)
    ///    - Handles multiplayer sync - clients get water from anchor ZDOs
    /// 
    /// DATA FLOW:
    /// 1. Auto-gen or player places water -> Absorbed into combiner -> Anchor + Cache saved
    /// 2. World saves -> SaveWaterData() writes anchors, SaveCacheAsync() writes file
    /// 3. Server restart -> LoadWaterData() restores from file cache (immediate)
    /// 4. Zone loads -> Anchors spawn, link to cached combiners (no duplicates)
    /// 5. Client joins -> Anchors in their zones restore automatically
    /// 6. Real-time edits -> RPC broadcasts + cache/anchor updated
    /// </summary>
    public static class WaterPersistence
    {
        private static bool _initialized = false;

        /// <summary>
        /// Initializes the persistence system.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            if (FiresLogger.VerboseEnabled) FiresLogger.LogVerbose("[WaterPersistence] Initialized v5 (anchor-only persistence)");
        }

        /// <summary>
        /// Resets the initialized flag so Initialize() will run again on the next session.
        /// Called on logout to ensure proper reinitialization on reconnect.
        /// </summary>
        public static void ResetInitialized()
        {
            _initialized = false;
        }

        /// <summary>
        /// Saves all water data.
        /// v5: Anchors save combined mesh state to ZDO - this ensures they're all saved.
        /// </summary>
        public static void SaveWaterData()
        {
            // Only save if we're definitely the server
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            // Ensure all combiners have anchors and save combined mesh state to them
            int anchorsSaved = 0;
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner != null && combiner.WaterDisks.Count > 0)
                {
                    try
                    {
                        combiner.SaveToAnchor();
                        anchorsSaved++;
                    }
                    catch (Exception ex)
                    {
                        FiresLogger.LogWarning($"[WaterPersistence] Failed to save combiner anchor: {ex.Message}");
                    }
                }
            }

            if (anchorsSaved > 0)
            {
                FiresLogger.LogInfo($"[WaterPersistence] Saved {anchorsSaved} combiner anchors to ZDO (prefab: CustomRiver)");
            }
        }

        /// <summary>
        /// Loads water data from the file-based WaterCombinerCache.
        /// 
        /// v5 relied entirely on ZDO anchors, but anchors only spawn when their zone loads
        /// (which requires a player nearby). On a fresh server restart with no players yet,
        /// no zones load and no anchors spawn - water was lost until someone walked nearby.
        /// 
        /// v6 adds file-cache-first restoration (mirroring TerrainPersistence.LoadTerrainData):
        /// 1. Read cached groups from the local .bin file
        /// 2. For each group, restore the WaterMeshCombiner from cached grid data
        /// 3. When anchor ZDOs eventually spawn (zone load), they link to the existing combiner
        ///    instead of creating duplicates (deduplication by position in WaterMeshCombinerAnchor.Start)
        /// </summary>
        public static void LoadWaterData()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            // If combiners already exist, anchors have already restored them
            if (WaterMeshCombiner.AllCombiners.Count > 0)
            {
                if (FiresLogger.VerboseEnabled) FiresLogger.LogVerbose($"[WaterPersistence] {WaterMeshCombiner.AllCombiners.Count} combiners already restored from anchors");
                return;
            }

            // Proactively restore from file cache
            var cache = WaterCombinerCache.GetCurrentCache();
            if (cache == null || cache.Groups == null || cache.Groups.Count == 0)
            {
                if (FiresLogger.VerboseEnabled) FiresLogger.LogVerbose("[WaterPersistence] No water cache data found - starting fresh");
                return;
            }

            int restoredCount = 0;
            foreach (var group in cache.Groups)
            {
                if (group == null || !group.HasValidData) continue;

                // Skip if combiner already exists at this position (e.g., anchor already restored it)
                var existing = WaterMeshCombiner.GetCombinerAt(group.CenterPosition);
                if (existing != null) continue;

                try
                {
                    var combiner = WaterCombinerCache.RestoreFromCache(group, null);
                    if (combiner != null)
                    {
                        restoredCount++;
                    }
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[WaterPersistence] Failed to restore water group {group.GroupId} at {group.CenterPosition}: {ex.Message}");
                }
            }

            if (restoredCount > 0)
            {
                FiresLogger.LogInfo($"[WaterPersistence] Restored {restoredCount} water combiners from file cache ({cache.Groups.Count} total cached groups)");
            }
            else
            {
                FiresLogger.LogInfo("[WaterPersistence] No water combiners to restore from cache");
            }
        }

        /// <summary>
        /// Called when world is about to save.
        /// </summary>
        public static void OnWorldSave()
        {
            SaveWaterData();
        }

        /// <summary>
        /// Called when world is loaded and ready.
        /// </summary>
        public static void OnWorldLoad()
        {
            LoadWaterData();
        }

        /// <summary>
        /// Clears all existing water combiners in the scene.
        /// Called on logout to prevent stale data.
        /// NOTE: This does NOT destroy anchors permanently - it's for scene cleanup only.
        /// For permanent deletion, use WaterMeshCombiner.DestroyPermanently().
        /// </summary>
        public static void ClearAllWaterCombiners()
        {
            var combiners = new List<WaterMeshCombiner>(WaterMeshCombiner.AllCombiners);
            int count = combiners.Count;

            foreach (var combiner in combiners)
            {
                if (combiner != null && combiner.gameObject != null)
                {
                    // On logout, just clear the scene objects - don't destroy anchor ZDOs
                    // (they need to persist so water restores on next login).
                    // SafeDestroy routes through ZNetScene if a ZNetView is present so
                    // m_instances does not end up with a stale entry that NREs in
                    // ZNetScene.RemoveObjects on the next zone tick.
                    combiner.ClearAllDisks();
                    NetworkObjectHelper.SafeDestroy(combiner.gameObject);
                }
            }

            WaterOperationRPC.ClearCaches();

            if (count > 0)
            {
                FiresLogger.LogInfo($"[WaterPersistence] Cleared {count} water combiners (scene cleanup)");
            }
        }
    }
}
