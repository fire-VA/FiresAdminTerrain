using FiresCore.Sync;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Harmony patches for auto water generation support.
    /// 
    /// NOTE: Auto water does NOT run automatically on world load.
    /// The player must explicitly trigger it via the va_auto_water_generate console command.
    /// Admins on clients can use the command - it sends an RPC to the server to run generation.
    /// 
    /// Hooks:
    /// 1. WorldGenerator.Pregenerate() Postfix - caches river/stream/lake counts for status display
    /// 2. Game.Logout() - cancels any in-progress generation
    /// 3. ZNet.Start Postfix - registers the auto water generation RPC
    /// 4. ZoneSystem.SpawnZone Postfix - one-shot cleanup on first zone load after generation
    /// All four are called from FAT's one hook per vanilla method (FatVanillaHooks); this class patches nothing itself.
    /// </summary>
    public static class AutoWaterPatches
    {
        private const string RPC_REQUEST_AUTO_WATER = "FiresAdminTerrain_AutoWater_RequestGenerate";
        private const string RPC_REQUEST_CLEAR_WATER = "FiresAdminTerrain_AutoWater_RequestClear";
        private const string RPC_REQUEST_BUILD_CACHE = "FiresAdminTerrain_AutoWater_RequestBuildCache";
        private const string RPC_NOTIFY_GENERATION_COMPLETE = "FiresAdminTerrain_AutoWater_GenerationComplete";
        private const string RPC_STREAM_COMBINER = "FiresAdminTerrain_AutoWater_StreamCombiner";
        private static bool _rpcInitialized = false;

        /// <summary>
        /// How many combiners have been streamed to clients during the current generation.
        /// Reset when generation starts. Clients use this for progress display.
        /// </summary>
        private static int _streamedCombinerCount = 0;
        private static int _streamedDiskCount = 0;

        /// <summary>
        /// Tracks whether a post-generation zone-load cleanup has been scheduled.
        /// Set to true when generation completes. Reset on logout.
        /// </summary>
        private static bool _pendingZoneLoadCleanup = false;
        /// <summary>Frame counter since _pendingZoneLoadCleanup was set, to skip the initial
        /// burst of zone loads immediately at generation end.</summary>
        private static int _zoneLoadCleanupDelay = 0;

        /// <summary>
        /// True while the zone-load cleanup coroutine is running.
        /// Checked by WaterMeshCombiner.SpawnAmbientVFX() to suppress VFX creation
        /// during cleanup ForceRegenerate() calls (prevents particle warning spam).
        /// </summary>
        public static bool IsCleaningUp { get; private set; }
        /// <summary>
        /// After WorldGenerator.Pregenerate() completes, cache the data counts
        /// so va_auto_water_status can report them. Does NOT start generation.
        /// Generation is command-only via va_auto_water_generate (server-only).
        /// 
        /// Caching runs on ALL peers (server + clients) so the status command works everywhere.
        /// Only the actual generation (disk placement + anchor creation) is server-only.
        /// From FAT's one WorldGenerator.Pregenerate hook (FatVanillaHooks), after RiverSync, so a client that replayed the
        /// server's lists counts them.
        /// </summary>
        internal static void OnPregenerated(WorldGenerator __instance)
        {
            if (WorldGeneratorAccess.IsMenuWorld(__instance))
                return;

            var rivers = WorldGeneratorAccess.GetRivers(__instance);
            var streams = WorldGeneratorAccess.GetStreams(__instance);
            var lakes = WorldGeneratorAccess.GetLakes(__instance);

            AutoWaterGenerator.CachedRiverCount = rivers?.Count ?? 0;
            AutoWaterGenerator.CachedStreamCount = streams?.Count ?? 0;
            AutoWaterGenerator.CachedLakeCount = lakes?.Count ?? 0;

            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            FiresLogger.LogInfo($"[AutoWaterPatches] World data cached ({(isServer ? "server" : "client")}): " +
                       $"{AutoWaterGenerator.CachedRiverCount} rivers, " +
                       $"{AutoWaterGenerator.CachedStreamCount} streams, {AutoWaterGenerator.CachedLakeCount} lakes. " +
                       WorldGeneratorAccess.GetBCStatusString());
        }

        /// <summary>
        /// Register the auto water generation RPC when ZNet starts.
        /// This allows admin clients to request generation on the server.
        /// </summary>
        internal static void OnZNetStart(ZNet __instance)
        {
            if (Game.instance == null) return;
            InitializeRPC();
        }

        /// <summary>
        /// Registers the RPC handler for auto water generation requests.
        /// </summary>
        public static void InitializeRPC()
        {
            if (_rpcInitialized) return;
            if (ZRoutedRpc.instance == null) return;

            try
            {
                ZRoutedRpc.instance.Register(RPC_REQUEST_AUTO_WATER, new System.Action<long>(RPC_HandleRequestAutoWater));
                ZRoutedRpc.instance.Register(RPC_REQUEST_CLEAR_WATER, new System.Action<long>(RPC_HandleRequestClearWater));
                ZRoutedRpc.instance.Register(RPC_REQUEST_BUILD_CACHE, new System.Action<long>(RPC_HandleRequestBuildCache));
                ZRoutedRpc.instance.Register(RPC_NOTIFY_GENERATION_COMPLETE, new System.Action<long, int, int>(RPC_HandleGenerationComplete));
                ZRoutedRpc.instance.Register<ZPackage>(RPC_STREAM_COMBINER, RPC_HandleStreamCombiner);
                _rpcInitialized = true;
                FiresLogger.LogInfo("[AutoWaterPatches] Auto water generation + clear + build cache + stream + notify RPCs registered");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[AutoWaterPatches] Failed to register auto water RPC: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by clients/server to request auto water generation.
        /// If already on server, runs directly. Otherwise sends an RPC to the server.
        /// 
        /// NOTE: No client-side admin check here. The server authoritatively validates
        /// admin status in RPC_HandleRequestAutoWater via AdminSyncing.IsAdmin(sender),
        /// which checks the actual adminlist.txt. The client-side ConfigSync.IsAdmin
        /// (lockExempt) flag is unreliable - AdminSyncing only sends it when the admin
        /// list changes (polled every 30s), so it may still be false even for admins
        /// who just connected.
        /// </summary>
        public static void RequestAutoWaterGeneration()
        {
            if (ZNet.instance == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] Cannot request generation - ZNet not available");
                return;
            }

            // If we ARE the server, run directly (no need for RPC roundtrip)
            if (ZNet.instance.IsServer())
            {
                RPC_HandleRequestAutoWater(0L);
                return;
            }

            if (ZRoutedRpc.instance == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] Cannot request generation - ZRoutedRpc not available");
                return;
            }

            // Send RPC to server - server validates admin status authoritatively
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_REQUEST_AUTO_WATER);
            FiresLogger.LogInfo("[AutoWaterPatches] Sent auto water generation request to server");
        }

        /// <summary>
        /// Server-side RPC handler. When an admin client requests generation,
        /// the server clears existing water first, then reads WorldGenerator data
        /// and runs the generation coroutine.
        /// </summary>
        private static void RPC_HandleRequestAutoWater(long sender)
        {
            // Only the server handles this RPC
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return;

            // Validate sender is admin (skip check for local server calls where sender is 0)
            if (sender != 0L && !AdminSyncing.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[AutoWaterPatches] Non-admin peer {sender} tried to request auto water generation - rejected");
                return;
            }

            FiresLogger.LogInfo($"[AutoWaterPatches] Received auto water generation request from peer {sender}");

            if (WorldGenerator.instance == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] WorldGenerator not available - cannot generate");
                return;
            }

            // STEP 1: Clear ALL existing custom water first (clean slate)
            int existingCombiners = WaterMeshCombiner.AllCombiners.Count;
            if (existingCombiners > 0 || AutoWaterGenerator.IsGenerating)
            {
                int loadedDestroyed, zdoDestroyed;
                ClearAllWaterInternal(out loadedDestroyed, out zdoDestroyed);
                FiresLogger.LogInfo($"[AutoWaterPatches] Pre-generation clear: {loadedDestroyed} loaded, {zdoDestroyed} ZDOs destroyed");
            }

            // Also clear the file-based water cache (clean slate for new generation)
            WaterCombinerCache.ClearCache();

            // Reset streaming counters for incremental client sync
            _streamedCombinerCount = 0;
            _streamedDiskCount = 0;

            // STEP 2: Generate fresh water
            var lakes = WorldGeneratorAccess.GetLakes(WorldGenerator.instance);
            var rivers = WorldGeneratorAccess.GetRivers(WorldGenerator.instance);
            var streams = WorldGeneratorAccess.GetStreams(WorldGenerator.instance);

            AutoWaterGenerator.GenerateFromWorldData(lakes, rivers, streams);

            FiresLogger.LogInfo($"[AutoWaterPatches] Started auto water generation on server (clean slate): " +
                $"{rivers?.Count ?? 0} rivers, {streams?.Count ?? 0} streams, {lakes?.Count ?? 0} lakes");
        }

        /// <summary>
        /// Called by clients to request the SERVER build its own height cache.
        /// The cache is saved to the server's filesystem so it's available for
        /// future va_auto_water_generate calls. Client-built caches are on the
        /// client's machine and inaccessible to the server.
        /// </summary>
        public static void RequestBuildHeightCache()
        {
            if (ZNet.instance == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] Cannot request cache build - ZNet not available");
                return;
            }

            if (ZNet.instance.IsServer())
            {
                RPC_HandleRequestBuildCache(0L);
                return;
            }

            if (ZRoutedRpc.instance == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] Cannot request cache build - ZRoutedRpc not available");
                return;
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_REQUEST_BUILD_CACHE);
            FiresLogger.LogInfo("[AutoWaterPatches] Sent height cache build request to server");
        }

        /// <summary>
        /// Server-side RPC handler for building the WorldHeightCache.
        /// Builds the cache on the server's filesystem so it's available for generation.
        /// </summary>
        private static void RPC_HandleRequestBuildCache(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return;

            if (sender != 0L && !AdminSyncing.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[AutoWaterPatches] Non-admin peer {sender} tried to request cache build - rejected");
                return;
            }

            FiresLogger.LogInfo($"[AutoWaterPatches] Received height cache build request from peer {sender}");

            if (WorldGenerator.instance == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] WorldGenerator not available - cannot build cache");
                return;
            }

            if (WorldHeightCache.IsBuilding)
            {
                FiresLogger.LogInfo("[AutoWaterPatches] Height cache build already in progress");
                return;
            }

            if (WorldHeightCache.HasCache)
            {
                var existingSnap = WorldHeightCache.CachedSnapshot;
                if (existingSnap != null && existingSnap.Size == WorldHeightCache.GridSize)
                {
                    FiresLogger.LogInfo($"[AutoWaterPatches] Height cache already built at correct resolution ({existingSnap.Size}x{existingSnap.Size})");
                    return;
                }
                // Cache exists but at wrong resolution - clear it and rebuild
                FiresLogger.LogInfo($"[AutoWaterPatches] Height cache exists at {existingSnap?.Size}x{existingSnap?.Size} but configured GridSize is {WorldHeightCache.GridSize} - rebuilding");
                WorldHeightCache.ClearCache();
            }

            // Re-apply config
            try
            {
                var cfgMgr = VerdantsAscent.Managers.ConfigManager.Instance;
                if (cfgMgr?.configHeightCacheResolution != null)
                {
                    int cfgVal = cfgMgr.configHeightCacheResolution.Value;
                    WorldHeightCache.GridSize = UnityEngine.Mathf.Clamp(cfgVal, 1024, 20480);
                }
                if (cfgMgr?.configHeightCacheSamplesPerFrame != null)
                {
                    WorldHeightCache.SamplesPerFrame = UnityEngine.Mathf.Clamp(cfgMgr.configHeightCacheSamplesPerFrame.Value, 500, 20000);
                }
            }
            catch { }

            var host = Game.instance != null ? (MonoBehaviour)Game.instance : (MonoBehaviour)ZoneSystem.instance;
            if (host == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] No coroutine host available for cache build");
                return;
            }

            FiresLogger.LogInfo($"[AutoWaterPatches] Starting server-side height cache build ({WorldHeightCache.GridSize}x{WorldHeightCache.GridSize})...");
            host.StartCoroutine(WorldHeightCache.BuildCacheCoroutine(snap =>
            {
                if (snap != null)
                    FiresLogger.LogInfo($"[AutoWaterPatches] Server height cache built: {snap.Size}x{snap.Size}, saved to disk");
                else
                    FiresLogger.LogWarning("[AutoWaterPatches] Server height cache build failed");
            }));
        }

        /// <summary>
        /// Called by clients/server to request clearing all auto water.
        /// If already on server, runs directly. Otherwise sends an RPC to the server.
        /// </summary>
        public static void RequestClearAutoWater()
        {
            if (ZNet.instance == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] Cannot request clear - ZNet not available");
                return;
            }

            if (ZNet.instance.IsServer())
            {
                RPC_HandleRequestClearWater(0L);
                return;
            }

            if (ZRoutedRpc.instance == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] Cannot request clear - ZRoutedRpc not available");
                return;
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_REQUEST_CLEAR_WATER);
            FiresLogger.LogInfo("[AutoWaterPatches] Sent clear auto water request to server");
        }

        /// <summary>
        /// Server-side RPC handler for clearing all auto water.
        /// Destroys all loaded combiners/anchors, scans all ZDOs for water anchors,
        /// destroys them, and saves the world.
        /// </summary>
        private static void RPC_HandleRequestClearWater(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return;

            if (sender != 0L && !AdminSyncing.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[AutoWaterPatches] Non-admin peer {sender} tried to clear auto water - rejected");
                return;
            }

            FiresLogger.LogInfo($"[AutoWaterPatches] Received clear auto water request from peer {sender}");

            // Clear file-based cache too
            WaterCombinerCache.ClearCache();

            int loadedDestroyed, zdoDestroyed;
            ClearAllWaterInternal(out loadedDestroyed, out zdoDestroyed);

            _lastClearLoadedCount = loadedDestroyed;
            _lastClearZdoCount = zdoDestroyed;

            FiresLogger.LogInfo($"[AutoWaterPatches] Clear complete: {loadedDestroyed} loaded objects, {zdoDestroyed} ZDOs destroyed");
        }

        // Cached results from last clear for console feedback
        private static int _lastClearLoadedCount = 0;
        private static int _lastClearZdoCount = 0;

        /// <summary>
        /// Gets the results of the last clear operation for console feedback.
        /// </summary>
        public static void GetLastClearResults(out int loadedDestroyed, out int zdoDestroyed)
        {
            loadedDestroyed = _lastClearLoadedCount;
            zdoDestroyed = _lastClearZdoCount;
        }

        /// <summary>
        /// Performs the actual clearing of all custom water. Can be called directly
        /// (from generate command) or via RPC. Returns counts for feedback.
        /// 
        /// Phases:
        /// 1. Cancel in-progress generation
        /// 2. Destroy all loaded WaterMeshCombiners (calls DestroyPermanently which
        ///    clears the anchor ZDO data and destroys the anchor GameObject)
        /// 3. DestroyImmediate all remaining loaded anchors not linked to a combiner
        /// 4. Explicitly purge the AllCombiners list of null/destroyed entries
        /// 5. Scan ALL ZDOs world-wide for water anchor flags and destroy them
        /// 6. Clear anchor tracking caches
        /// 7. Save the world to commit ZDO deletions
        /// </summary>
        public static void ClearAllWaterInternal(out int loadedDestroyed, out int zdoDestroyed)
        {
            loadedDestroyed = 0;
            zdoDestroyed = 0;

            if (ZDOMan.instance == null)
            {
                FiresLogger.LogWarning("[AutoWaterPatches] ZDOMan not available - cannot clear");
                return;
            }

            // Cancel any in-progress generation
            if (AutoWaterGenerator.IsGenerating)
            {
                AutoWaterGenerator.CancelGeneration();
                FiresLogger.LogInfo("[AutoWaterPatches] Cancelled in-progress auto water generation");
            }

            // Phase 1: Destroy all loaded WaterMeshCombiners
            // Copy the list since DestroyPermanently modifies AllCombiners during OnDestroy
            var allCombiners = new System.Collections.Generic.List<WaterMeshCombiner>(WaterMeshCombiner.AllCombiners);
            foreach (var combiner in allCombiners)
            {
                if (combiner != null)
                {
                    combiner.DestroyPermanently();
                    loadedDestroyed++;
                }
            }

            // Phase 2: Force-purge the AllCombiners list of null/destroyed entries
            // Unity's Destroy() is deferred to end of frame but the object is already
            // "== null" in Unity's overloaded sense. Clean the list immediately so
            // subsequent generation doesn't see stale entries.
            WaterMeshCombiner.PurgeDestroyedCombiners();

            // Phase 3: Destroy all loaded anchors not linked to a combiner
            // (e.g., anchors that were mid-restoration or orphaned)
            var allAnchors = UnityEngine.Object.FindObjectsByType<WaterMeshCombinerAnchor>(UnityEngine.FindObjectsSortMode.None);
            foreach (var anchor in allAnchors)
            {
                if (anchor != null)
                {
                    anchor.DestroyPermanently();
                    loadedDestroyed++;
                }
            }

            // Phase 4: Scan ALL ZDOs for water anchor flags (unloaded zones)
            int anchorFlagHash = WaterMeshCombinerAnchor.IsWaterAnchorHash;
            long mySessionId = ZDOMan.GetSessionID();

            var allAnchorZDOs = new System.Collections.Generic.List<ZDO>();
            // Scan CustomRiver-based anchors AND old dirtfloor-based anchors (for legacy cleanup)
            // New anchors only use CustomRiver, but old saves may still have dirtfloor ZDOs
            string[] prefabNames = { "CustomRiver", "piece_dirtfloor_vad", "Piece_DirtFloor_vad", "piece_dirtfloor" };
            foreach (var prefabName in prefabNames)
            {
                var zdoList = new System.Collections.Generic.List<ZDO>();
                int index = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, zdoList, ref index))
                {
                }
                allAnchorZDOs.AddRange(zdoList);
            }

            var zdosToDestroy = new System.Collections.Generic.List<ZDO>();
            foreach (var zdo in allAnchorZDOs)
            {
                if (zdo != null && zdo.GetBool(anchorFlagHash, false))
                    zdosToDestroy.Add(zdo);
            }

            foreach (var zdo in zdosToDestroy)
            {
                try
                {
                    zdo.Set(anchorFlagHash, false);
                    zdo.Set("va_water_combiner_version".GetStableHashCode(), 0);
                    byte[] empty = new byte[0];
                    zdo.Set("va_water_grid_data".GetStableHashCode(), empty);
                    zdo.Set("va_water_surface_heights".GetStableHashCode(), empty);
                    zdo.Set("va_water_terrain_heights".GetStableHashCode(), empty);
                    zdo.Set("va_water_bounds".GetStableHashCode(), empty);
                    zdo.Set("va_water_disks".GetStableHashCode(), empty);
                    zdo.SetOwner(mySessionId);
                    ZDOMan.instance.DestroyZDO(zdo);
                    zdoDestroyed++;
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[AutoWaterPatches] Failed to destroy ZDO: {ex.Message}");
                }
            }

            // Phase 5: Clear caches
            WaterMeshCombinerAnchor.ClearAnchorTracking();
            WaterMeshCombinerAnchor.ClearConversionTracking();

            // Phase 6: Broadcast to all connected clients so they clear THEIR local file caches too.
            // Without this, clients log out, their stale .bin file persists, and on next login
            // WaterMeshCombinerAnchor.Start() hits the cache-first path and restores old water.
            WaterOperationRPC.BroadcastClearAllWater();

            // Phase 7: Save (async to avoid blocking main thread and disconnecting clients)
            if (zdoDestroyed > 0 || loadedDestroyed > 0)
            {
                ZNet.instance.Save(false);
            }
        }

        /// <summary>
        /// Called by the server after generation completes.
        /// 
        /// INCREMENTAL STREAMING: By this point, clients have already received
        /// all combiner data incrementally via <see cref="StreamCombinerToClients"/>
        /// which was called after each path completed during Phases 3/4.
        /// This final notification just tells clients "we're done" so they know
        /// no more data is coming, and saves the file cache + world.
        /// </summary>
        public static void NotifyClientsGenerationComplete(int combinerCount, int diskCount)
        {
            if (ZNet.instance == null) return;

            // Save all combiners to the file-based cache so the server (and local client)
            // can restore instantly without waiting for ZDO byte array sync.
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner != null && combiner.WaterDisks.Count > 0)
                {
                    WaterCombinerCache.UpdateCombinerCache(combiner);
                }
            }
            WaterCombinerCache.SaveCacheAsync();

            // Notify clients BEFORE the save - if the save takes a long time,
            // clients still get the notification. The data is already in ZDOs
            // and will be committed by the async save below.
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_NOTIFY_GENERATION_COMPLETE, combinerCount, diskCount);
                FiresLogger.LogInfo($"[AutoWaterPatches] Notified all clients: generation complete " +
                    $"({combinerCount} combiners, {diskCount} disks, " +
                    $"{_streamedCombinerCount} streamed incrementally). Cache saved.");
            }

            // Use async save (false) instead of sync save (true).
            // Save(true) blocks the main thread for the entire serialization + write
            // duration (37+ seconds with 7M ZDOs), which starves Steam networking
            // and disconnects all clients. Save(false) queues the save to run
            // without blocking, and the data is still committed to disk.
            ZNet.instance.Save(false);
        }

        /// <summary>
        /// Streams a single combiner's disk data to all connected clients via RPC.
        /// Called by AutoWaterGenerator after each path completes in Phase 3/4.
        /// 
        /// This enables INCREMENTAL water visibility on clients - they can see
        /// rivers and lakes appearing during the 30+ minute server-side generation
        /// instead of waiting until the end.
        /// 
        /// SERVER-ONLY. Sends a compact ZPackage containing just the disk positions,
        /// radii, depths, and liquid types - enough for the client to create a
        /// combiner and regenerate the mesh locally.
        /// 
        /// Each RPC payload is small (one combiner = ~40-60 bytes per disk - N disks).
        /// For a typical river with 200 disks, that's ~12KB per RPC - well within
        /// Valheim's network limits.
        /// </summary>
        public static void StreamCombinerToClients(WaterMeshCombiner combiner)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ZRoutedRpc.instance == null) return;
            if (combiner == null) return;

            var disks = combiner.WaterDisks;
            if (disks == null || disks.Count == 0) return;

            // Don't stream in single player - no clients to send to
            if (ZNet.IsSinglePlayer) return;

            var pkg = new ZPackage();

            // Header: combiner metadata
            pkg.Write(combiner.transform.position);
            pkg.Write((int)combiner.LiquidType);
            pkg.Write(combiner.IsPondMode);
            pkg.Write(disks.Count);

            // Compact disk data: position (3 floats) + radius + maxDepth + liquidType = 24 bytes each
            foreach (var disk in disks)
            {
                pkg.Write(disk.Position);
                pkg.Write(disk.Radius);
                pkg.Write(disk.MaxDepth);
                pkg.Write((int)disk.LiquidType);
                pkg.Write(disk.StableHash);
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_STREAM_COMBINER, pkg);

            _streamedCombinerCount++;
            _streamedDiskCount += disks.Count;
        }

        /// <summary>
        /// Client-side RPC handler. When the server notifies that generation is complete,
        /// clients log the result. The anchor ZDOs are already syncing via Valheim's normal
        /// ZDO protocol - ZNetScene will instantiate them automatically near the player.
        /// When a CustomRiver ZDO with the anchor flag is instantiated, the prefab's
        /// WaterMeshCombinerAnchor component detects isAnchor=true in Awake() and
        /// Start() triggers RestoreFromZDOAsync() -> visible water.
        /// 
        /// Players may need to move to new areas (or log out and back in) for water in
        /// distant zones to appear, as ZDOs only sync for zones near the player.
        /// </summary>
        private static void RPC_HandleGenerationComplete(long sender, int combinerCount, int diskCount)
        {
            // Server doesn't need to handle its own notification - it already has the combiners
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;
            if (!VerdantsAscent.Terrain.Lifecycle.AdminCheck.IsAdmin(sender)) return;

            FiresLogger.LogInfo($"[AutoWaterPatches] Server completed water generation: {combinerCount} combiners, {diskCount} disks. " +
                $"Client received {_streamedCombinerCount} combiners ({_streamedDiskCount} disks) incrementally during generation. " +
                "Remaining water anchors will appear as you explore (ZDO sync is automatic).");

            // Save the local file cache now that we have all data
            WaterCombinerCache.SaveCacheAsync();
        }

        /// <summary>
        /// CLIENT-SIDE: Handles incremental combiner data streamed from the server
        /// during generation. Creates a local combiner, populates it with disks,
        /// regenerates the mesh, and saves to the local file cache.
        /// 
        /// This runs on each client as the server processes each river/stream/lake,
        /// so water appears progressively instead of all-at-once after 30 minutes.
        /// </summary>
        private static void RPC_HandleStreamCombiner(long sender, ZPackage pkg)
        {
            // Server doesn't need to handle its own stream - it already has the combiners
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;
            if (pkg == null || !VerdantsAscent.Terrain.Lifecycle.AdminCheck.IsAdmin(sender)) return;

            try
            {
                // Read header
                Vector3 centerPos = pkg.ReadVector3();
                LiquidType liquidType = (LiquidType)pkg.ReadInt();
                bool isPondMode = pkg.ReadBool();
                int diskCount = pkg.ReadInt();

                if (diskCount <= 0) return;

                // Read compact disk data
                var disks = new List<WaterDisk>(diskCount);
                for (int i = 0; i < diskCount; i++)
                {
                    Vector3 pos = pkg.ReadVector3();
                    float radius = pkg.ReadSingle();
                    float maxDepth = pkg.ReadSingle();
                    LiquidType lt = (LiquidType)pkg.ReadInt();
                    int stableHash = pkg.ReadInt();

                    var disk = new WaterDisk(pos, radius, maxDepth, lt, stableHash);
                    disks.Add(disk);
                }

                // Check if a combiner already exists near this position
                // (from a previous stream or ZDO restoration)
                WaterMeshCombiner existingCombiner = null;
                foreach (var c in WaterMeshCombiner.AllCombiners)
                {
                    if (c != null && c.WouldOverlapWith(disks[0]))
                    {
                        existingCombiner = c;
                        break;
                    }
                }

                if (existingCombiner != null)
                {
                    // Merge into existing combiner
                    existingCombiner.AddWaterDisksBatch(disks, skipRegeneration: false);
                }
                else
                {
                    // Create new combiner
                    var firstDisk = disks[0];
                    existingCombiner = WaterMeshCombiner.GetOrCreateCombinerFor(firstDisk);
                    if (disks.Count > 1)
                    {
                        existingCombiner.AddWaterDisksBatch(
                            disks.GetRange(1, disks.Count - 1), skipRegeneration: false);
                    }
                }

                // Update file cache for faster future loads
                WaterCombinerCache.UpdateCombinerCache(existingCombiner);

                _streamedCombinerCount++;
                _streamedDiskCount += diskCount;

                // Log progress periodically (not every combiner - too spammy)
                if (_streamedCombinerCount % 10 == 0 || _streamedCombinerCount <= 3)
                {
                    FiresLogger.LogInfo($"[AutoWaterPatches] Received water data: {_streamedCombinerCount} combiners, " +
                        $"{_streamedDiskCount} disks so far (latest: {diskCount} disks at ({centerPos.x:F0},{centerPos.z:F0}))");
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[AutoWaterPatches] Error handling streamed combiner: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by AutoWaterGenerator at the end of generation to schedule
        /// a one-shot zone-load cleanup pass. The next time a zone spawns,
        /// all combiners in the loaded area are re-validated against real terrain.
        /// </summary>
        public static void ScheduleZoneLoadCleanup()
        {
            _pendingZoneLoadCleanup = true;
            _zoneLoadCleanupDelay = 0;
            FiresLogger.LogInfo("[AutoWaterPatches] Zone-load cleanup scheduled (will run on next zone spawn)");
        }

        /// <summary>
        /// One-shot postfix that fires when any zone spawns. If a post-generation
        /// cleanup is pending, it iterates ALL loaded combiners and:
        ///   1. Re-samples terrain under each disk to fix floating water
        ///   2. Removes disks that are majority-floating (surface &gt; terrain + threshold)
        ///   3. Forces regeneration on fixed combiners
        ///   4. Saves updated combiners
        /// 
        /// Runs ONCE then disables itself until the next generation.
        /// Uses a small frame delay so the zone's Heightmap data is fully loaded first.
        /// </summary>
        internal static void OnZoneSpawned()
        {
            if (!_pendingZoneLoadCleanup) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // Wait a few frames for terrain heightmaps to bake
            _zoneLoadCleanupDelay++;
            if (_zoneLoadCleanupDelay < 3) return;

            _pendingZoneLoadCleanup = false;

            // Run cleanup as a coroutine so we don't block the zone spawn
            var host = Game.instance != null ? (MonoBehaviour)Game.instance : (MonoBehaviour)ZoneSystem.instance;
            if (host != null)
            {
                host.StartCoroutine(ZoneLoadCleanupCoroutine());
            }
        }

        /// <summary>
        /// Coroutine that walks every loaded combiner, re-samples terrain under its disks,
        /// clamps floating disks to the ground, and saves. Segmented into phases to avoid
        /// frame spikes:
        /// 
        /// Phase 1 (SCAN): Walk all combiners, check disks, clamp floating ones.
        ///   - Cheap per-combiner (just disk height checks). Batched 20 per frame.
        ///   - Builds a list of combiners that need regeneration.
        /// 
        /// Phase 2 (REGENERATE): For each modified combiner, run async mesh regeneration.
        ///   - ONE combiner per frame (RegenerateMeshAsync spreads across multiple frames).
        ///   - Waits for async regen to finish before starting the next combiner.
        /// 
        /// Phase 3 (SAVE): Batch-save all modified combiners.
        ///   - Saves 5 per frame to avoid ZDO write spikes.
        /// </summary>
        private static System.Collections.IEnumerator ZoneLoadCleanupCoroutine()
        {
            // Wait a few frames for physics / heightmaps to settle
            yield return null;
            yield return null;

            if (WaterMeshCombiner.AllCombiners == null || WaterMeshCombiner.AllCombiners.Count == 0)
                yield break;

            IsCleaningUp = true;
            int totalCombiners = WaterMeshCombiner.AllCombiners.Count;
            FiresLogger.LogInfo($"[AutoWaterPatches] Zone-load cleanup starting - {totalCombiners} combiners (segmented)");

            // ---- PHASE 1: SCAN - check disk heights, clamp floating ones ----
            // Only clamp disks that are truly floating (ponds, lakes, springs).
            // Rivers and streams intentionally have their water plane sitting above
            // the terrain at disk edges - the cross-section is meant to be level,
            // and the combiner's TerrainClipPass handles bank clipping.
            const float MAX_FLOAT_POND = 2.5f;
            const float MAX_FLOAT_RIVER = 8.0f;  // Rivers can float higher at edges - bank clipping handles it
            const int SCAN_BATCH_SIZE = 20;
            int combinersChecked = 0;
            int disksClamped = 0;
            var modifiedCombiners = new List<WaterMeshCombiner>();

            // Snapshot the list to avoid issues if it changes during iteration
            var combinerSnapshot = new List<WaterMeshCombiner>(WaterMeshCombiner.AllCombiners);

            foreach (var combiner in combinerSnapshot)
            {
                if (combiner == null) continue;

                var disks = combiner.GetWaterDisks();
                if (disks == null || disks.Count == 0) continue;

                // Determine if this combiner is a river/stream (height variance > 3m = non-pond)
                // Ponds have flat surfaces; rivers can have significant height variance.
                bool isRiverLike = !combiner.IsPondMode;
                float maxFloat = isRiverLike ? MAX_FLOAT_RIVER : MAX_FLOAT_POND;

                bool modified = false;
                for (int i = 0; i < disks.Count; i++)
                {
                    var disk = disks[i];
                    if (disk == null) continue;

                    float terrainY;
                    Vector3 probePos = new Vector3(disk.Position.x, 500f, disk.Position.z);
                    if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(probePos, out float gh))
                        terrainY = gh;
                    else if (WorldGenerator.instance != null)
                        terrainY = WorldGenerator.instance.GetHeight(disk.Position.x, disk.Position.z);
                    else
                        continue;

                    float floatAmount = disk.Position.y - terrainY;
                    if (floatAmount > maxFloat)
                    {
                        float waterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
                        float newY = terrainY + 0.3f;
                        if (disk.Position.y >= waterLevel && newY < waterLevel)
                            newY = waterLevel;

                        disk.Position = new Vector3(disk.Position.x, newY, disk.Position.z);
                        disksClamped++;
                        modified = true;
                    }
                }

                if (modified)
                {
                    modifiedCombiners.Add(combiner);
                }

                combinersChecked++;
                if (combinersChecked % SCAN_BATCH_SIZE == 0)
                    yield return null;
            }

            FiresLogger.LogInfo($"[AutoWaterPatches] Phase 1 (scan) done: {combinersChecked} checked, " +
                $"{modifiedCombiners.Count} need regen ({disksClamped} disks clamped)");

            // ---- PHASE 2: REGENERATE - one combiner at a time, async ----
            int combinersFixed = 0;

            for (int i = 0; i < modifiedCombiners.Count; i++)
            {
                var combiner = modifiedCombiners[i];
                if (combiner == null) continue;

                try
                {
                    combiner.RegenerateMeshAsync();
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[AutoWaterPatches] Async regen failed for combiner {i}: {ex.Message}");
                    continue;
                }

                combinersFixed++;

                // Wait for async regeneration to finish before starting the next one
                // This keeps the per-frame cost low (one regen spread across frames)
                while (combiner != null && combiner.IsRegeneratingAsync)
                {
                    yield return null;
                }

                // Yield an extra frame between combiners to let the game breathe
                yield return null;
            }

            FiresLogger.LogInfo($"[AutoWaterPatches] Phase 2 (regen) done: {combinersFixed} combiners regenerated");

            // ---- PHASE 3: SAVE - batch save modified combiners ----
            const int SAVE_BATCH_SIZE = 5;
            int saved = 0;

            for (int i = 0; i < modifiedCombiners.Count; i++)
            {
                var combiner = modifiedCombiners[i];
                if (combiner == null) continue;

                try
                {
                    combiner.ForceImmediateSave();
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[AutoWaterPatches] Save failed for combiner {i}: {ex.Message}");
                }

                saved++;
                if (saved % SAVE_BATCH_SIZE == 0)
                    yield return null;
            }

            IsCleaningUp = false;

            FiresLogger.LogInfo($"[AutoWaterPatches] Zone-load cleanup done: {combinersChecked} checked, " +
                $"{combinersFixed} regenerated ({disksClamped} disks clamped), {saved} saved");
        }

        /// <summary>
        /// Cancel any in-progress generation on logout to prevent stale coroutines.
        /// </summary>
        internal static void OnLogout()
        {
            if (AutoWaterGenerator.IsGenerating)
            {
                AutoWaterGenerator.CancelGeneration();
            }
            WorldHeightCache.ClearCache();
            _rpcInitialized = false;
            _pendingZoneLoadCleanup = false;
            _streamedCombinerCount = 0;
            _streamedDiskCount = 0;
            IsCleaningUp = false;
        }
    }
}
