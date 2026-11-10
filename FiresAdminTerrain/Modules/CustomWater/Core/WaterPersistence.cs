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
                        if (combiner.TrySaveToAnchor()) anchorsSaved++;   // 0.2.1349 (Fire: "every time we log back in its broken"): real saves only; it said 386 with nearly none written
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
                RepairMissingAnchors();   // 0.2.1349
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

            RepairMissingAnchors();   // 0.2.1349
        }

        // 0.2.1349 (Fire, x-28 z937: "i have used v pour at least 3 times ... every time we log back in its broken"). Server only, once per world
        // load. A saved group no anchor ZDO stands for never reaches a client: it gets one. Anchors are matched by the disks they hold (not the
        // stamp: 0-tick groups are healthy; not the position: centres move), so the second boot finds its own anchors and adds none. Only adds
        // anchors: no anchor or group is deleted. A disk an anchor already holds leaves the new group's list, so it never loads twice; its anchor keeps it.
        private static void RepairMissingAnchors()
        {
            var key = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.WaterAnchorRepair;
            if (key != null && !key.Value)
            {
                FiresLogger.LogInfo("[WaterPersistence] water anchor repair is off in the config: groups with no anchor are not repaired");
                return;
            }
            var cache = WaterCombinerCache.GetCurrentCache();
            if (cache == null || cache.Groups == null || cache.Groups.Count == 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scans = WaterMeshCombinerAnchor.ScanAnchors();
            var groups = new List<WaterCombinerCache.WaterCombinerGroupData>(cache.Groups.Count);
            foreach (var g in cache.Groups)
                if (g != null && g.HasValidData) groups.Add(g);

            // 0.2.1352 (Fire: "theres basically no river mesh that doesnt end up broken on relog"): off = 1350.
            var writeBackKey = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.WaterAnchorWriteBack;
            bool writeBack = writeBackKey == null || writeBackKey.Value;
            if (!writeBack) FiresLogger.LogInfo("[WaterPersistence] writing cached disks back to anchors is off in the config: a group with an anchor keeps only the disks that anchor holds");
            var groupAnchor = new Dictionary<int, WaterMeshCombinerAnchor.AnchorScan>();
            var groupAnchorShared = new Dictionary<int, int>();
            HashSet<int> removedRounded = null;   // the list holds exact hashes; a v1/v2 group's hash is float-rounded
            if (writeBack)
            {
                removedRounded = new HashSet<int>();
                foreach (int h in WaterRemovedDisks.ServerList()) removedRounded.Add((int)(float)h);
            }
            int removedSkipped = 0;

            // Each anchor stands for the one group sharing most of its disks; a stray shared disk does not count (SameBody).
            var anchored = new HashSet<int>();
            var heldBy = new Dictionary<int, List<Vector3>>();
            var emptyScans = new List<WaterMeshCombinerAnchor.AnchorScan>();   // 0.2.1354 (review: one empty anchor took two groups' disks): decided after the anchors with disks
            foreach (var scan in scans)
            {
                if (scan.Disks == null || scan.Disks.Count == 0)
                {
                    emptyScans.Add(scan);
                    continue;
                }
                var hashes = WaterMeshCombinerAnchor.HashesOf(scan.Disks);
                foreach (var d in scan.Disks) AddHeld(heldBy, d.StableHash, d.Position);
                WaterCombinerCache.WaterCombinerGroupData best = null;
                int bestShared = 0;
                foreach (var g in groups)
                {
                    if (!WaterMeshCombinerAnchor.OverlapXZ(scan.Box.min, scan.Box.max, g.BoundsMin, g.BoundsMax, WaterMeshCombinerAnchor.BindDistance)) continue;
                    int count = g.WaterDiskData.Length / WaterCombinerCache.WaterCombinerGroupData.DISK_STRIDE, shared = 0;
                    for (int i = 0; i < count; i++)
                        if (hashes.Contains(WaterCombinerCache.HashAt(g, i))) shared++;
                    if (shared > bestShared && WaterMeshCombinerAnchor.SameBody(shared, scan.Disks.Count, count)) { best = g; bestShared = shared; }
                }
                if (best == null) continue;
                anchored.Add(best.GroupId);
                var live = WaterCombinerCache.GetInstantiatedCombinerForGroup(best.GroupId);
                if (live != null) live.NoteAnchorZdo(scan.Zdo.m_uid);
                int bestCount = best.WaterDiskData.Length / WaterCombinerCache.WaterCombinerGroupData.DISK_STRIDE;
                if (writeBack)
                {
                    // 0.2.1352: the anchor holding most of the group is the one that takes the rest.
                    if (!groupAnchorShared.TryGetValue(best.GroupId, out int had) || bestShared > had) { groupAnchor[best.GroupId] = scan; groupAnchorShared[best.GroupId] = bestShared; }
                }
                else if (bestShared < bestCount) FiresLogger.LogDebug($"[WaterAnchorRepair] group {best.GroupId} has anchor {scan.Zdo.m_uid} but {bestCount - bestShared} of its {bestCount} disks are not in it; left as is, the group's next stroke writes them");
            }

            // 0.2.1354 (review: an empty anchor within 2 m and 8 m height took two groups' disks): it binds the nearest one group no other anchor stands for; any other group near it gets its own anchor below.
            // A gelled group is not bound: this write would leave its ZDO with disks and no heights, and peers would rebuild its grid from them. Write-back off: 1350, nothing is written, position only.
            foreach (var scan in emptyScans)
            {
                WaterCombinerCache.WaterCombinerGroupData nearest = null;
                float nearestSq = WaterMeshCombinerAnchor.BindDistance * WaterMeshCombinerAnchor.BindDistance;
                foreach (var g in groups)
                {
                    float dx = scan.Real.x - g.CenterPosition.x, dz = scan.Real.z - g.CenterPosition.z, sq = dx * dx + dz * dz;
                    if (sq >= WaterMeshCombinerAnchor.BindDistance * WaterMeshCombinerAnchor.BindDistance || Mathf.Abs(scan.Real.y - g.CenterPosition.y) > 8f) continue;
                    if (!writeBack)
                    {
                        anchored.Add(g.GroupId);
                        FiresLogger.LogDebug($"[WaterAnchorRepair] group {g.GroupId} not repaired: an anchor with no disks stands at its centre ({scan.Zdo.m_uid})");
                        continue;
                    }
                    if (anchored.Contains(g.GroupId) || g.IsGelled) continue;
                    if (nearest == null || sq < nearestSq) { nearest = g; nearestSq = sq; }
                }
                if (nearest == null) continue;
                anchored.Add(nearest.GroupId);
                groupAnchor[nearest.GroupId] = scan;
                groupAnchorShared[nearest.GroupId] = 0;
                FiresLogger.LogDebug($"[WaterAnchorRepair] group {nearest.GroupId} not repaired: the anchor with no disks at its centre ({scan.Zdo.m_uid}) takes its disks (0.2.1354)");
            }

            int written = 0, writtenGroups = 0;
            if (writeBack) written = WriteBackDisks(groups, groupAnchor, heldBy, removedRounded, ref removedSkipped, out writtenGroups);

            // Largest first, so a disk two missing groups share stays with the larger.
            var missing = groups.FindAll(g => !anchored.Contains(g.GroupId));
            missing.Sort((a, b) => b.WaterDiskData.Length.CompareTo(a.WaterDiskData.Length));
            int repaired = 0, disksNow = 0, nothingLeft = 0, gelled = 0;
            foreach (var g in missing)
            {
                // A gelled volume's grid is authoritative and this ZDO has no heights: it waits for its own save.
                if (g.IsGelled)
                {
                    gelled++;
                    FiresLogger.LogDebug($"[WaterAnchorRepair] group {g.GroupId} at {g.CenterPosition} not repaired: gelled, its grid must not be rebuilt from disks");
                    continue;
                }
                var keep = new List<WaterDisk>();
                var sharedOut = new HashSet<int>();
                foreach (var d in WaterCombinerCache.DisksOf(g))
                {
                    if (WaterRemovedDisks.Contains(d.StableHash)) { removedSkipped++; continue; }   // a removal stays removed
                    if (removedRounded != null && removedRounded.Contains(d.StableHash)) { removedSkipped++; continue; }   // 0.2.1352: a v1/v2 group's rounded hash
                    if (IsHeld(heldBy, d.StableHash, d.Position)) sharedOut.Add(d.StableHash);
                    else keep.Add(d);
                }
                if (keep.Count == 0)
                {
                    nothingLeft++;
                    FiresLogger.LogDebug($"[WaterAnchorRepair] group {g.GroupId} at {g.CenterPosition} not repaired: all its disks are already in other anchors, or removed");
                    continue;
                }
                long ticks = System.Math.Max(System.DateTime.UtcNow.Ticks, g.AnchorSavedTicks + 1L);
                var zdo = WaterMeshCombinerAnchor.CreateAnchorZdoForGroup(g, keep, ticks);
                if (zdo == null)
                {
                    FiresLogger.LogWarning($"[WaterPersistence] no anchor could be made for water group {g.GroupId} at {g.CenterPosition}: its {keep.Count} disks still will not reach clients");
                    continue;
                }
                WaterCombinerCache.NoteRepaired(g, ticks, sharedOut);
                var live = WaterCombinerCache.GetInstantiatedCombinerForGroup(g.GroupId);
                if (live != null) live.NoteAnchorZdo(zdo.m_uid);
                foreach (var d in keep) AddHeld(heldBy, d.StableHash, d.Position);
                repaired++;
                disksNow += keep.Count;
                FiresLogger.LogDebug($"[WaterAnchorRepair] group {g.GroupId} at {g.CenterPosition}: no anchor among {scans.Count}, made {zdo.m_uid} with {keep.Count} disks ({sharedOut.Count} shared disk(s) left with the anchor holding them)");
            }

            // 0.2.1354 (review: a gelled group with no anchor skips its save silently): nothing is made for it, a ZDO without its heights would be rebuilt from disks. Its cache file stays whole; it reaches no client until a save with a live grid. Said once per load.
            if (gelled > 0) FiresLogger.LogWarning($"[WaterPersistence] {gelled} gelled water group(s) have no anchor: they stay in the cache file but do not reach clients (0.2.1354)");
            FiresLogger.LogDebug($"[WaterAnchorRepair] {scans.Count} anchors, {groups.Count} groups, {missing.Count} with no anchor: repaired {repaired}, nothing left {nothingLeft}, gelled {gelled}, {sw.ElapsedMilliseconds} ms");
            if (repaired > 0)
                FiresLogger.LogInfo($"[WaterPersistence] repaired {repaired} water groups with no anchor: {disksNow} disks now reach clients ({sw.ElapsedMilliseconds} ms)");
            // 0.2.1352: said at the first load even at 0: a restart reporting 0 written is the proof it held. Later loads only when they wrote.
            if (writeBack && (written > 0 || !s_writeBackSaid))
            {
                s_writeBackSaid = true;
                FiresLogger.LogInfo($"[WaterPersistence] {written} cached disks written back to anchors in {writtenGroups} groups; {removedSkipped} on the removed list skipped ({sw.ElapsedMilliseconds} ms)");
            }
            if (repaired > 0 || written > 0) WaterCombinerCache.SaveCacheAsync();
        }

        private static bool s_writeBackSaid;

        // 0.2.1352 (Fire: "theres basically no river mesh that doesnt end up broken on relog"). A client's river disks come only from anchor ZDOs, so a
        // cached disk no anchor holds never reaches one. Matched per disk: each such disk goes to its group's anchor, which is stamped and has its heights
        // blanked, so every peer rebuilds from the disks. Idempotent: the next boot finds them held. Only adds disks; nothing is deleted.
        // Largest group first, so a disk two groups share stays with the larger.
        private static int WriteBackDisks(List<WaterCombinerCache.WaterCombinerGroupData> groups, Dictionary<int, WaterMeshCombinerAnchor.AnchorScan> groupAnchor,
            Dictionary<int, List<Vector3>> heldBy, HashSet<int> removedRounded, ref int removedSkipped, out int writtenGroups)
        {
            writtenGroups = 0;
            int written = 0;
            var order = groups.FindAll(g => groupAnchor.ContainsKey(g.GroupId));
            order.Sort((a, b) => b.WaterDiskData.Length.CompareTo(a.WaterDiskData.Length));
            foreach (var g in order)
            {
                var scan = groupAnchor[g.GroupId];
                var live = WaterCombinerCache.GetInstantiatedCombinerForGroup(g.GroupId);
                if (live != null) live.NoteAnchorZdo(scan.Zdo.m_uid);   // the anchor that takes the group's disks is the one its strokes write to

                var add = new List<WaterDisk>();
                var addHere = new Dictionary<int, List<Vector3>>();   // 0.2.1354 (review: AddHeld before the write): a failed write must not leave these reading as held, so a smaller group or the next boot can still take them
                int removedHere = 0;
                foreach (var d in WaterCombinerCache.DisksOf(g))
                {
                    if (WaterRemovedDisks.Contains(d.StableHash) || removedRounded.Contains(d.StableHash)) { removedHere++; continue; }   // a removal stays removed
                    if (IsHeld(heldBy, d.StableHash, d.Position) || IsHeld(addHere, d.StableHash, d.Position)) continue;
                    add.Add(d);
                    AddHeld(addHere, d.StableHash, d.Position);
                }
                removedSkipped += removedHere;
                if (add.Count == 0) continue;

                int before = scan.Disks != null ? scan.Disks.Count : 0;
                try
                {
                    long ticks = WaterMeshCombinerAnchor.WriteBackDisks(scan, add, g);
                    foreach (var d in add) AddHeld(heldBy, d.StableHash, d.Position);   // 0.2.1354 (review: AddHeld before the write)
                    WaterCombinerCache.NoteRepaired(g, ticks, null);   // our own write never reads as "newer than the cache" here
                    written += add.Count;
                    writtenGroups++;
                    FiresLogger.LogDebug($"[WaterPersistence] group {g.GroupId} at {g.CenterPosition}: anchor {scan.Zdo.m_uid} held {before} of the group's {g.WaterDiskData.Length / WaterCombinerCache.WaterCombinerGroupData.DISK_STRIDE} disks, wrote {add.Count}, now holds {before + add.Count}; {removedHere} on the removed list skipped; heights blanked, peers rebuild from the disks");
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[WaterPersistence] could not write {add.Count} disks back to anchor {scan.Zdo.m_uid} of group {g.GroupId} at {g.CenterPosition}: {ex.Message}; they still will not reach clients");
                }
            }
            return written;
        }

        internal static void AddHeld(Dictionary<int, List<Vector3>> held, int hash, Vector3 at)
        {
            int rounded = (int)(float)hash;
            if (!held.TryGetValue(rounded, out var list)) held[rounded] = list = new List<Vector3>(1);
            list.Add(at);
        }

        // By rounded hash AND position: a v1/v2 group's hash is rounded, and rounded hashes can collide.
        // 0.2.1354 (review: IsHeld compared Y, so a height edit read as a different disk and the boot wrote it a second time): XZ only; a height edit never moves a disk across.
        internal static bool IsHeld(Dictionary<int, List<Vector3>> held, int hash, Vector3 at)
        {
            if (!held.TryGetValue((int)(float)hash, out var list)) return false;
            foreach (var p in list)
            {
                float dx = p.x - at.x, dz = p.z - at.z;
                if (dx * dx + dz * dz <= 0.0004f) return true;
            }
            return false;
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
