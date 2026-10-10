using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;
using VerdantsAscent.Terrain.Lifecycle;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Handles multiplayer RPC synchronization for ALL custom water operations.
    /// 
    /// ARCHITECTURE (Server Authority - mirrors Valheim terrain):
    /// 1. Client applies locally for instant visual feedback
    /// 2. Client sends _Request RPC to SERVER ONLY
    /// 3. Server validates, stores in cache, persists to ZDO anchor
    /// 4. Server broadcasts _Confirmed RPC to ALL clients
    /// 5. Clients receiving _Confirmed skip if they are the origin (already applied)
    /// 6. On player join, server sends full cache sync via RPC_SYNC_WATER_CACHE
    /// 
    /// STABLE HASH IDS:
    /// - Each WaterDisk has a stable hash ID generated from position + timestamp + random
    /// - This prevents ID collisions when multiple clients place water simultaneously
    /// - Server is the single source of truth for disk existence
    /// </summary>
    public static class WaterOperationRPC
    {
        private static bool _initialized = false;

        // RPC names - Client -> Server (requests)
        private const string RPC_CREATE_WATER = "FiresAdminTerrain_Water_Create";
        private const string RPC_REMOVE_WATER = "FiresAdminTerrain_Water_Remove";
        private const string RPC_MODIFY_WATER = "FiresAdminTerrain_Water_Modify";
        private const string RPC_CLEAR_ZONE_WATER = "FiresAdminTerrain_Water_ClearZone";

        // RPC names - Server -> All Clients (confirmed broadcasts)
        private const string RPC_CREATE_CONFIRMED = "FiresAdminTerrain_Water_CreateOK";
        private const string RPC_REMOVE_CONFIRMED = "FiresAdminTerrain_Water_RemoveOK";
        private const string RPC_MODIFY_CONFIRMED = "FiresAdminTerrain_Water_ModifyOK";
        private const string RPC_CLEAR_ZONE_CONFIRMED = "FiresAdminTerrain_Water_ClearZoneOK";

        // RPC names - Sync (server to client on join)
        private const string RPC_REQUEST_SYNC = "FiresAdminTerrain_Water_RequestSync";
        private const string RPC_SYNC_CACHE = "FiresAdminTerrain_Water_SyncCache";
        private const string RPC_REQUEST_DISK_SYNC = "FiresAdminTerrain_Water_RequestDiskSync";
        private const string RPC_DISK_SYNC_RESPONSE = "FiresAdminTerrain_Water_DiskSyncResponse";
        private const string RPC_CLEAR_ALL_WATER = "FiresAdminTerrain_Water_ClearAll";

        // Track operations we've already applied locally (to prevent double-apply)
        private static HashSet<int> _appliedDiskHashes = new HashSet<int>();

        // Server-side cache of all water disks (authoritative)
        private static Dictionary<int, WaterDisk> _serverWaterCache = new Dictionary<int, WaterDisk>();

        /// <summary>
        /// Initialize the RPC handlers. Called during game startup.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            if (ZRoutedRpc.instance == null) return;

            try
            {
                // Client -> Server requests
                ZRoutedRpc.instance.Register<ZPackage>(RPC_CREATE_WATER, RPC_HandleCreateWater);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_REMOVE_WATER, RPC_HandleRemoveWater);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_MODIFY_WATER, RPC_HandleModifyWater);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_CLEAR_ZONE_WATER, RPC_HandleClearZoneWater);

                // Server -> All Clients confirmed broadcasts
                ZRoutedRpc.instance.Register<ZPackage>(RPC_CREATE_CONFIRMED, RPC_HandleCreateConfirmed);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_REMOVE_CONFIRMED, RPC_HandleRemoveConfirmed);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_MODIFY_CONFIRMED, RPC_HandleModifyConfirmed);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_CLEAR_ZONE_CONFIRMED, RPC_HandleClearZoneConfirmed);

                // Sync operations
                ZRoutedRpc.instance.Register<ZPackage>(RPC_REQUEST_SYNC, RPC_HandleRequestSync);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_SYNC_CACHE, RPC_HandleSyncCache);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_REQUEST_DISK_SYNC, RPC_HandleRequestDiskSync);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_DISK_SYNC_RESPONSE, RPC_HandleDiskSyncResponse);
                ZRoutedRpc.instance.Register<ZPackage>(RPC_CLEAR_ALL_WATER, RPC_HandleClearAllWater);

                _initialized = true;
                FiresLogger.LogInfo("[WaterOperationRPC] Initialized all water operation RPCs");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Failed to register RPCs: {ex.Message}");
            }
        }

        #region Public API - Water Operations


        /// <summary>
        /// Requests circular water disk creation at a position.
        /// Uses server-authority model in multiplayer.
        /// </summary>
        /// <returns>The stable hash of the created disk</returns>
        public static int RequestCreateWater(Vector3 position, float radius, float maxDepth, LiquidType liquidType)
        {
            if (!_initialized) Initialize();

            // Create disk with stable hash
            var disk = new WaterDisk(position, radius, maxDepth, liquidType);
            return RequestCreateWaterInternal(disk);
        }

        /// <summary>
        /// Requests shaped water disk creation at a position (square or rectangle).
        /// Uses server-authority model in multiplayer.
        /// </summary>
        /// <returns>The stable hash of the created disk</returns>
        public static int RequestCreateWater(Vector3 position, WaterDiskShape shape, float width, float length, float maxDepth, LiquidType liquidType)
        {
            if (!_initialized) Initialize();

            // Create shaped disk
            var disk = new WaterDisk(position, shape, width, length, maxDepth, liquidType);
            return RequestCreateWaterInternal(disk);
        }

        /// <summary>
        /// Internal implementation for creating water disks (shared by circle and shaped overloads).
        /// Client applies locally for instant feedback, then sends request to server.
        /// Server validates, caches, persists, and broadcasts confirmed to all clients.
        /// </summary>
        private static int RequestCreateWaterInternal(WaterDisk disk)
        {
            int stableHash = disk.StableHash;

            // Mark as locally applied so we skip our own confirmed broadcast
            _appliedDiskHashes.Add(stableHash);

            // Apply locally first for immediate visual feedback
            var combiner = WaterMeshCombiner.GetOrCreateCombinerFor(disk);

            // Send request to server (server will validate and broadcast to all)
            var pkg = new ZPackage();
            pkg.Write(disk.Serialize());
            pkg.Write(GetLocalPlayerId());

            if (IsServer())
            {
                // We ARE the server - handle directly and broadcast to clients
                ServerHandleCreateAndBroadcast(disk, GetLocalPlayerId());
            }
            else
            {
                // Send to server only (NOT Everybody)
                ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RPC_CREATE_WATER, pkg);
            }

            return stableHash;
        }

        /// <summary>
        /// Requests creation of a water disk with a custom liquid type already set.
        /// The custom type is applied to both the disk and the combiner BEFORE mesh generation,
        /// so the correct material/behavior is used from the first frame - no "flash of water then swap".
        /// </summary>
        /// <returns>The stable hash of the created disk</returns>
        public static int RequestCreateWaterWithCustomType(Vector3 position, WaterDisk disk, CustomLiquidType customType)
        {
            if (!_initialized) Initialize();

            int stableHash = disk.StableHash;

            // Mark as locally applied so we skip our own confirmed broadcast
            _appliedDiskHashes.Add(stableHash);

            // Ensure the disk has the custom type set
            disk.CustomLiquidType = customType;

            // Apply locally - GetOrCreateCombinerFor adds the disk to a combiner
            var combiner = WaterMeshCombiner.GetOrCreateCombinerFor(disk);

            // Set the custom liquid type on the combiner IMMEDIATELY (before mesh regen)
            if (combiner != null && combiner.CustomLiquidType != customType)
            {
                combiner.SetCustomLiquidType(customType);
            }

            // Send request to server
            var pkg = new ZPackage();
            pkg.Write(disk.Serialize());
            pkg.Write(GetLocalPlayerId());

            if (IsServer())
            {
                ServerHandleCreateAndBroadcast(disk, GetLocalPlayerId());
            }
            else
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RPC_CREATE_WATER, pkg);
            }

            return stableHash;
        }

        /// <summary>
        /// Requests water disk removal by stable hash.
        /// </summary>
        public static void RequestRemoveWater(int stableHash)
        {
            if (!_initialized) Initialize();

            // Apply locally first for instant feedback
            RemoveWaterDiskByHash(stableHash);
            _appliedDiskHashes.Add(stableHash);

            if (IsServer())
            {
                ServerHandleRemoveAndBroadcast(stableHash, GetLocalPlayerId());
            }
            else
            {
                var pkg = new ZPackage();
                pkg.Write(stableHash);
                pkg.Write(GetLocalPlayerId());
                ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RPC_REMOVE_WATER, pkg);
            }
        }

        /// <summary>
        /// Requests water disk removal in a radius.
        /// Also permanently destroys combiners (and their anchor ZDOs) when all their disks are removed.
        /// </summary>
        public static void RequestRemoveWaterInRadius(Vector3 center, float radius)
        {
            if (!_initialized) Initialize();

            // Work on a snapshot of combiners to avoid modification during iteration
            var combinersSnapshot = new List<WaterMeshCombiner>(WaterMeshCombiner.AllCombiners);

            foreach (var combiner in combinersSnapshot)
            {
                if (combiner == null) continue;

                var hashesToRemove = new List<int>();

                foreach (var disk in combiner.WaterDisks)
                {
                    if (Vector3.Distance(disk.Position, center) <= radius)
                    {
                        hashesToRemove.Add(disk.StableHash);
                    }
                }

                if (hashesToRemove.Count == 0) continue;

                // Remove each disk via the proper server-authority flow
                foreach (int hash in hashesToRemove)
                {
                    RequestRemoveWater(hash);
                }
            }
        }

        /// <summary>
        /// Requests modification of an existing water disk.
        /// </summary>
        public static void RequestModifyWater(int stableHash, float? newRadius = null, float? newDepth = null, float? newSurfaceY = null)
        {
            if (!_initialized) Initialize();

            // Apply locally first for instant feedback
            ModifyWaterDiskByHash(stableHash, newRadius, newDepth, newSurfaceY);
            _appliedDiskHashes.Add(stableHash);

            if (IsServer())
            {
                ServerHandleModifyAndBroadcast(stableHash, newRadius, newDepth, newSurfaceY, GetLocalPlayerId());
            }
            else
            {
                var pkg = new ZPackage();
                pkg.Write(stableHash);
                pkg.Write(newRadius.HasValue);
                if (newRadius.HasValue) pkg.Write(newRadius.Value);
                pkg.Write(newDepth.HasValue);
                if (newDepth.HasValue) pkg.Write(newDepth.Value);
                pkg.Write(newSurfaceY.HasValue);
                if (newSurfaceY.HasValue) pkg.Write(newSurfaceY.Value);
                pkg.Write(GetLocalPlayerId());
                ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RPC_MODIFY_WATER, pkg);
            }
        }

        /// <summary>
        /// Requests clearing all custom water in a zone.
        /// Also updates the local file cache so stale water doesn't restore on next login.
        /// </summary>
        public static void RequestClearZoneWater(Vector3 center, float radius)
        {
            if (!_initialized) Initialize();

            // Apply locally first for instant feedback
            ClearWaterInRadius(center, radius);

            if (IsServer())
            {
                ServerHandleClearZoneAndBroadcast(center, radius, GetLocalPlayerId());
            }
            else
            {
                var pkg = new ZPackage();
                pkg.Write(center);
                pkg.Write(radius);
                pkg.Write(GetLocalPlayerId());
                ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RPC_CLEAR_ZONE_WATER, pkg);
            }
        }

        /// <summary>
        /// Requests full water cache sync from server (called on player join).
        /// 
        /// NOTE: This is DEPRECATED - water should be restored from ZDO anchors like terrain,
        /// not from server cache sync. The anchor system handles persistence directly.
        /// 
        /// This method is kept for compatibility but does nothing - anchors handle restoration.
        /// </summary>
        public static void RequestCacheSync()
        {
            // DEPRECATED: Water anchors restore from ZDO automatically.
            // No need to request cache sync - this was causing duplicate combiner creation.
            // Anchors are the source of truth, not the server cache.
            FiresLogger.LogInfo("[WaterOperationRPC] Cache sync disabled - anchors restore from ZDO directly");
        }

        #endregion

        #region RPC Handlers - Client -> Server (request handlers, server-only)

        /// <summary>
        /// SERVER: Receives create request from a client, validates, caches, persists, broadcasts.
        /// </summary>
        private static void RPC_HandleCreateWater(long sender, ZPackage pkg)
        {
            if (pkg == null || !IsServer()) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "create"); return; }

            try
            {
                byte[] diskData = pkg.ReadByteArray();
                long originPlayerId = pkg.ReadLong();

                var disk = WaterDisk.Deserialize(diskData);
                ServerHandleCreateAndBroadcast(disk, originPlayerId);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling create water request: {ex.Message}");
            }
        }

        /// <summary>
        /// SERVER: Receives remove request from a client.
        /// </summary>
        private static void RPC_HandleRemoveWater(long sender, ZPackage pkg)
        {
            if (pkg == null || !IsServer()) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "remove"); return; }

            try
            {
                int stableHash = pkg.ReadInt();
                long originPlayerId = pkg.ReadLong();
                ServerHandleRemoveAndBroadcast(stableHash, originPlayerId);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling remove water request: {ex.Message}");
            }
        }

        /// <summary>
        /// SERVER: Receives modify request from a client.
        /// </summary>
        private static void RPC_HandleModifyWater(long sender, ZPackage pkg)
        {
            if (pkg == null || !IsServer()) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "modify"); return; }

            try
            {
                int stableHash = pkg.ReadInt();
                bool hasRadius = pkg.ReadBool();
                float? newRadius = hasRadius ? pkg.ReadSingle() : (float?)null;
                bool hasDepth = pkg.ReadBool();
                float? newDepth = hasDepth ? pkg.ReadSingle() : (float?)null;
                bool hasSurface = pkg.ReadBool();
                float? newSurfaceY = hasSurface ? pkg.ReadSingle() : (float?)null;
                long originPlayerId = pkg.ReadLong();

                ServerHandleModifyAndBroadcast(stableHash, newRadius, newDepth, newSurfaceY, originPlayerId);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling modify water request: {ex.Message}");
            }
        }

        /// <summary>
        /// SERVER: Receives clear-zone request from a client.
        /// </summary>
        private static void RPC_HandleClearZoneWater(long sender, ZPackage pkg)
        {
            if (pkg == null || !IsServer()) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "clear zone"); return; }

            try
            {
                Vector3 center = pkg.ReadVector3();
                float radius = pkg.ReadSingle();
                long originPlayerId = pkg.ReadLong();
                ServerHandleClearZoneAndBroadcast(center, radius, originPlayerId);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling clear zone water request: {ex.Message}");
            }
        }

        #endregion

        #region Server-side: validate, cache, persist, broadcast

        private static void ServerHandleCreateAndBroadcast(WaterDisk disk, long originPlayerId)
        {
            // Server applies locally (creates combiner on dedicated server for anchor persistence)
            if (!_appliedDiskHashes.Contains(disk.StableHash))
            {
                WaterMeshCombiner.GetOrCreateCombinerFor(disk);
            }
            else
            {
                _appliedDiskHashes.Remove(disk.StableHash);
            }

            // Cache + persist
            _serverWaterCache[disk.StableHash] = disk;
            TriggerServerSave(disk.Position);

            // Broadcast confirmed to every client EXCEPT the origin - they already
            // applied the disk locally before sending the request (see _appliedDiskHashes
            // fast-path below, which is now only hit by late-arriving ZDO syncs).
            var pkg = new ZPackage();
            pkg.Write(disk.Serialize());
            pkg.Write(originPlayerId);
            VerdantsAscent.Async.SafeRoutedRpc.InvokeSafeToAllExcept(originPlayerId, RPC_CREATE_CONFIRMED, pkg);

            FiresLogger.LogInfo($"[WaterOperationRPC] Server confirmed create disk hash={disk.StableHash}, broadcasting to all");
        }

        private static void ServerHandleRemoveAndBroadcast(int stableHash, long originPlayerId)
        {
            if (!_appliedDiskHashes.Contains(stableHash))
            {
                RemoveWaterDiskByHash(stableHash);
            }
            else
            {
                _appliedDiskHashes.Remove(stableHash);
            }

            _serverWaterCache.Remove(stableHash);

            var pkg = new ZPackage();
            pkg.Write(stableHash);
            pkg.Write(originPlayerId);
            VerdantsAscent.Async.SafeRoutedRpc.InvokeSafeToAllExcept(originPlayerId, RPC_REMOVE_CONFIRMED, pkg);
        }

        private static void ServerHandleModifyAndBroadcast(int stableHash, float? newRadius, float? newDepth, float? newSurfaceY, long originPlayerId)
        {
            if (!_appliedDiskHashes.Contains(stableHash))
            {
                ModifyWaterDiskByHash(stableHash, newRadius, newDepth, newSurfaceY);
            }
            else
            {
                _appliedDiskHashes.Remove(stableHash);
            }

            // Update server cache
            if (_serverWaterCache.TryGetValue(stableHash, out var disk))
            {
                if (newRadius.HasValue) disk.Radius = newRadius.Value;
                if (newDepth.HasValue) disk.MaxDepth = newDepth.Value;
                if (newSurfaceY.HasValue)
                {
                    var pos = disk.Position;
                    pos.y = newSurfaceY.Value;
                    disk.Position = pos;
                }
                TriggerServerSave(disk.Position);
            }

            var pkg = new ZPackage();
            pkg.Write(stableHash);
            pkg.Write(newRadius.HasValue);
            if (newRadius.HasValue) pkg.Write(newRadius.Value);
            pkg.Write(newDepth.HasValue);
            if (newDepth.HasValue) pkg.Write(newDepth.Value);
            pkg.Write(newSurfaceY.HasValue);
            if (newSurfaceY.HasValue) pkg.Write(newSurfaceY.Value);
            pkg.Write(originPlayerId);
            VerdantsAscent.Async.SafeRoutedRpc.InvokeSafeToAllExcept(originPlayerId, RPC_MODIFY_CONFIRMED, pkg);
        }

        private static void ServerHandleClearZoneAndBroadcast(Vector3 center, float radius, long originPlayerId)
        {
            ClearWaterInRadius(center, radius);

            var hashesToRemove = new List<int>();
            foreach (var kvp in _serverWaterCache)
            {
                if (Vector3.Distance(kvp.Value.Position, center) <= radius)
                    hashesToRemove.Add(kvp.Key);
            }
            foreach (int hash in hashesToRemove)
                _serverWaterCache.Remove(hash);

            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write(originPlayerId);
            VerdantsAscent.Async.SafeRoutedRpc.InvokeSafeToAllExcept(originPlayerId, RPC_CLEAR_ZONE_CONFIRMED, pkg);
        }

        #endregion

        #region RPC Handlers - Server -> Client (confirmed broadcasts)

        /// <summary>
        /// CLIENT: Server confirmed a water create - apply if not already applied locally.
        /// </summary>
        private static void RPC_HandleCreateConfirmed(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "create (confirmed)"); return; }

            try
            {
                byte[] diskData = pkg.ReadByteArray();
                long originPlayerId = pkg.ReadLong();

                var disk = WaterDisk.Deserialize(diskData);

                // Skip if we already applied this locally (we are the origin)
                if (_appliedDiskHashes.Contains(disk.StableHash))
                {
                    _appliedDiskHashes.Remove(disk.StableHash);
                    return;
                }

                // Apply the confirmed disk
                WaterMeshCombiner.GetOrCreateCombinerFor(disk);
                FiresLogger.LogInfo($"[WaterOperationRPC] Applied confirmed create from server: hash={disk.StableHash}");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling create confirmed: {ex.Message}");
            }
        }

        /// <summary>
        /// CLIENT: Server confirmed a water remove.
        /// </summary>
        private static void RPC_HandleRemoveConfirmed(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "remove (confirmed)"); return; }

            try
            {
                int stableHash = pkg.ReadInt();
                long originPlayerId = pkg.ReadLong();

                if (_appliedDiskHashes.Contains(stableHash))
                {
                    _appliedDiskHashes.Remove(stableHash);
                    return;
                }

                RemoveWaterDiskByHash(stableHash);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling remove confirmed: {ex.Message}");
            }
        }

        /// <summary>
        /// CLIENT: Server confirmed a water modify.
        /// </summary>
        private static void RPC_HandleModifyConfirmed(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "modify (confirmed)"); return; }

            try
            {
                int stableHash = pkg.ReadInt();
                bool hasRadius = pkg.ReadBool();
                float? newRadius = hasRadius ? pkg.ReadSingle() : (float?)null;
                bool hasDepth = pkg.ReadBool();
                float? newDepth = hasDepth ? pkg.ReadSingle() : (float?)null;
                bool hasSurface = pkg.ReadBool();
                float? newSurfaceY = hasSurface ? pkg.ReadSingle() : (float?)null;
                long originPlayerId = pkg.ReadLong();

                if (_appliedDiskHashes.Contains(stableHash))
                {
                    _appliedDiskHashes.Remove(stableHash);
                    return;
                }

                ModifyWaterDiskByHash(stableHash, newRadius, newDepth, newSurfaceY);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling modify confirmed: {ex.Message}");
            }
        }

        /// <summary>
        /// CLIENT: Server confirmed a zone clear.
        /// </summary>
        private static void RPC_HandleClearZoneConfirmed(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "clear zone (confirmed)"); return; }

            try
            {
                Vector3 center = pkg.ReadVector3();
                float radius = pkg.ReadSingle();
                long originPlayerId = pkg.ReadLong();

                // Origin client already cleared locally - skip
                if (originPlayerId == GetLocalPlayerId()) return;

                ClearWaterInRadius(center, radius);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling clear zone confirmed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles global water clear RPC from the server.
        /// Destroys all local combiners and clears the local file cache so stale water
        /// doesn't restore on next login.
        /// </summary>
        private static void RPC_HandleClearAllWater(long sender, ZPackage pkg)
        {
            // Server already handled this locally - only clients need to process
            if (IsServer()) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "clear all"); return; }

            try
            {
                FiresLogger.LogInfo("[WaterOperationRPC] Received global water clear from server - destroying all local water and cache");

                // Destroy all local combiners
                var allCombiners = new List<WaterMeshCombiner>(WaterMeshCombiner.AllCombiners);
                foreach (var combiner in allCombiners)
                {
                    if (combiner != null && combiner.gameObject != null)
                    {
                        combiner.ClearAllDisks();
                        // Route via ZNetScene so the ZNetView on the combiner / its
                        // anchor parent is properly unregistered. Raw Object.Destroy
                        // here used to leave stale m_instances entries that NRE'd in
                        // ZNetScene.RemoveObjects on the next zone tick.
                        NetworkObjectHelper.SafeDestroy(combiner.gameObject);
                    }
                }
                WaterMeshCombiner.PurgeDestroyedCombiners();

                // Destroy any loaded anchors (they'll be gone from the server ZDOs too)
                var allAnchors = UnityEngine.Object.FindObjectsByType<WaterMeshCombinerAnchor>(UnityEngine.FindObjectsSortMode.None);
                foreach (var anchor in allAnchors)
                {
                    if (anchor != null)
                    {
                        NetworkObjectHelper.SafeDestroy(anchor.gameObject);
                    }
                }

                // CRITICAL: Clear the local file cache so stale water doesn't restore on next login
                WaterCombinerCache.ClearCache();

                // Clear anchor tracking
                WaterMeshCombinerAnchor.ClearAnchorTracking();
                WaterMeshCombinerAnchor.ClearConversionTracking();

                // Clear the local server cache (RPC memory cache)
                _appliedDiskHashes.Clear();
                _serverWaterCache.Clear();

                FiresLogger.LogInfo($"[WaterOperationRPC] Global water clear complete - {allCombiners.Count} combiners, {allAnchors.Length} anchors destroyed, file cache cleared");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling global water clear: {ex.Message}");
            }
        }

        private static void RPC_HandleRequestSync(long sender, ZPackage pkg)
        {
            // DEPRECATED: Water anchors restore from ZDO automatically.
            // No need for server cache sync - this was causing duplicate combiner creation.
            // Anchors are the source of truth, not the server cache.
            FiresLogger.LogInfo("[WaterOperationRPC] Ignoring sync request - anchors restore from ZDO directly");
        }

        private static void RPC_HandleSyncCache(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (IsServer()) return; // Server doesn't receive sync, only sends
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "cache sync"); return; }

            try
            {
                int diskCount = pkg.ReadInt();

                FiresLogger.LogInfo($"[WaterOperationRPC] Receiving water cache sync: {diskCount} disks");

                // STEP 1: Deserialize all disks first
                var allDisks = new List<WaterDisk>(diskCount);
                var existingHashes = new HashSet<int>();

                // Collect existing disk hashes
                foreach (var combiner in WaterMeshCombiner.AllCombiners)
                {
                    if (combiner == null) continue;
                    foreach (var existingDisk in combiner.WaterDisks)
                    {
                        existingHashes.Add(existingDisk.StableHash);
                    }
                }

                for (int i = 0; i < diskCount; i++)
                {
                    byte[] diskData = pkg.ReadByteArray();
                    var disk = WaterDisk.Deserialize(diskData);

                    // Skip if we already have this disk
                    if (!existingHashes.Contains(disk.StableHash))
                    {
                        allDisks.Add(disk);
                    }
                }

                FiresLogger.LogInfo($"[WaterOperationRPC] Received {allDisks.Count} new disks to process");

                if (allDisks.Count == 0)
                {
                    FiresLogger.LogInfo($"[WaterOperationRPC] Water cache sync complete (all disks already exist)");
                    return;
                }

                // STEP 2: Group disks by overlap using union-find approach
                // This ensures all overlapping disks end up in the same group
                var diskGroups = GroupDisksByOverlap(allDisks);

                FiresLogger.LogInfo($"[WaterOperationRPC] Grouped into {diskGroups.Count} water bodies");

                // STEP 3: Create combiners for each group with all disks at once
                int combinerCount = 0;
                foreach (var group in diskGroups)
                {
                    if (group.Count == 0) continue;

                    // Check if any existing combiner overlaps with this group
                    WaterMeshCombiner targetCombiner = null;
                    foreach (var combiner in WaterMeshCombiner.AllCombiners)
                    {
                        if (combiner == null) continue;

                        foreach (var disk in group)
                        {
                            if (combiner.WouldOverlapWith(disk))
                            {
                                targetCombiner = combiner;
                                break;
                            }
                        }
                        if (targetCombiner != null) break;
                    }

                    if (targetCombiner != null)
                    {
                        // Add all group disks to existing combiner
                        targetCombiner.AddWaterDisksBatch(group, skipRegeneration: false);
                    }
                    else
                    {
                        // Create new combiner with all group disks
                        var firstDisk = group[0];
                        var go = new GameObject("WaterMeshCombiner_Synced");
                        go.transform.position = firstDisk.Position;

                        var newCombiner = go.AddComponent<WaterMeshCombiner>();
                        newCombiner.LiquidType = firstDisk.LiquidType;
                        newCombiner.SetRestoredFromPersistence(true); // Don't recombine

                        // Add all disks in batch
                        newCombiner.AddWaterDisksBatch(group, skipRegeneration: false);

                        // Only server creates anchors for persistence
                        if (IsServer())
                        {
                            newCombiner.EnsureAnchorExists();
                        }

                        combinerCount++;
                    }
                }

                FiresLogger.LogInfo($"[WaterOperationRPC] Water cache sync complete: created {combinerCount} new combiners from {allDisks.Count} disks");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling cache sync: {ex.Message}");
            }
        }

        /// <summary>
        /// Groups water disks by overlap using union-find algorithm.
        /// Disks that overlap (including transitive overlap) end up in the same group.
        /// </summary>
        private static List<List<WaterDisk>> GroupDisksByOverlap(List<WaterDisk> disks)
        {
            if (disks == null || disks.Count == 0)
                return new List<List<WaterDisk>>();

            int n = disks.Count;

            // Union-find parent array
            int[] parent = new int[n];
            for (int i = 0; i < n; i++)
                parent[i] = i;

            // Find with path compression
            int Find(int x)
            {
                if (parent[x] != x)
                    parent[x] = Find(parent[x]);
                return parent[x];
            }

            // Union
            void Union(int x, int y)
            {
                int px = Find(x);
                int py = Find(y);
                if (px != py)
                    parent[px] = py;
            }

            // Check all pairs for overlap
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (disks[i].OverlapsWith(disks[j], WaterMeshCombiner.BlendDistance))
                    {
                        // Also check terrain blocking
                        if (!WaterMeshCombiner.IsTerrainBlockingBetweenDisks(disks[i], disks[j]))
                        {
                            Union(i, j);
                        }
                    }
                }
            }

            // Group disks by root parent
            var groups = new Dictionary<int, List<WaterDisk>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                if (!groups.ContainsKey(root))
                    groups[root] = new List<WaterDisk>();
                groups[root].Add(disks[i]);
            }

            return new List<List<WaterDisk>>(groups.Values);
        }

        /// <summary>
        /// Called by clients when ZDO byte arrays fail to sync after retries.
        /// Sends an RPC to the server requesting disk data for a specific water body
        /// at the given position. The server responds with all disks from its cache
        /// or loaded combiners near that position, and the client regenerates locally.
        /// </summary>
        /// <param name="position">The world position of the water body</param>
        /// <param name="targetCombiner">The empty combiner waiting for data</param>
        public static void RequestDiskSyncForPosition(Vector3 position, WaterMeshCombiner targetCombiner)
        {
            if (!_initialized) Initialize();
            if (ZRoutedRpc.instance == null) return;

            // Store the combiner reference for the response handler
            lock (_pendingSyncLock)
            {
                // Key by rounded position to match responses
                var posKey = SyncKey(position);
                _pendingSyncCombiners[posKey] = targetCombiner;
            }

            var pkg = new ZPackage();
            pkg.Write(position);
            pkg.Write(GetLocalPlayerId());

            ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RPC_REQUEST_DISK_SYNC, pkg);
            FiresLogger.LogInfo($"[WaterOperationRPC] Requested disk sync from server for position {position:F1}");
        }

        // Pending sync requests: maps position key to the waiting combiner
        private static readonly object _pendingSyncLock = new object();
        private static Dictionary<Vector3Int, WaterMeshCombiner> _pendingSyncCombiners = new Dictionary<Vector3Int, WaterMeshCombiner>();

        // 0.2.715: the server echoes the requested position, so a 3D key matches exactly. The XZ key let a sky-island
        // water body's request and the ground water's below overwrite each other's waiting combiner.
        private static Vector3Int SyncKey(Vector3 position)
            => new Vector3Int(Mathf.RoundToInt(position.x), Mathf.RoundToInt(position.y), Mathf.RoundToInt(position.z));

        /// <summary>
        /// SERVER-SIDE: Handles a client requesting disk data for a specific position.
        /// Finds all disks near the position from loaded combiners and sends them back.
        /// </summary>
        private static void RPC_HandleRequestDiskSync(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (!IsServer()) return;

            try
            {
                Vector3 position = pkg.ReadVector3();
                long requesterId = pkg.ReadLong();

                FiresLogger.LogInfo($"[WaterOperationRPC] Server received disk sync request from peer {requesterId} for position {position:F1}");

                // Find all disks near this position from loaded combiners
                var nearbyCombiners = WaterMeshCombiner.GetCombinersInRadius(position, 100f);
                var allDisks = new List<WaterDisk>();

                foreach (var combiner in nearbyCombiners)
                {
                    if (combiner == null) continue;

                    // Check if this combiner's bounds are near the requested position
                    var bounds = combiner.GetCombinedBounds();
                    float dist = Vector3.Distance(bounds.center, position);
                    if (dist < bounds.extents.magnitude + 50f)
                    {
                        foreach (var disk in combiner.WaterDisks)
                        {
                            allDisks.Add(disk);
                        }
                    }
                }

                // Also check server cache for disks not in loaded combiners
                foreach (var kvp in _serverWaterCache)
                {
                    if (Vector3.Distance(kvp.Value.Position, position) < 100f)
                    {
                        // Check if this disk is already in our list
                        bool found = false;
                        foreach (var existing in allDisks)
                        {
                            if (existing.StableHash == kvp.Key)
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            allDisks.Add(kvp.Value);
                        }
                    }
                }

                if (allDisks.Count == 0)
                {
                    FiresLogger.LogInfo($"[WaterOperationRPC] No disks found near {position:F1} to sync");
                    return;
                }

                // Build response package
                var responsePkg = new ZPackage();
                responsePkg.Write(position); // Echo back position for matching
                responsePkg.Write(allDisks.Count);

                foreach (var disk in allDisks)
                {
                    responsePkg.Write(disk.Serialize());
                }

                // Send response to the requesting client only
                ZRoutedRpc.instance.InvokeRoutedRPC(requesterId, RPC_DISK_SYNC_RESPONSE, responsePkg);
                FiresLogger.LogInfo($"[WaterOperationRPC] Sent {allDisks.Count} disks to peer {requesterId} for position {position:F1}");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling disk sync request: {ex.Message}");
            }
        }

        /// <summary>
        /// CLIENT-SIDE: Handles the server's response with disk data.
        /// Populates the waiting combiner with disks and triggers regeneration.
        /// </summary>
        private static void RPC_HandleDiskSyncResponse(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (IsServer()) return;
            if (!AdminCheck.IsAdmin(sender)) { RejectNonAdmin(sender, "disk sync"); return; }

            try
            {
                Vector3 position = pkg.ReadVector3();
                int diskCount = pkg.ReadInt();

                FiresLogger.LogInfo($"[WaterOperationRPC] Received disk sync response: {diskCount} disks for position {position:F1}");

                if (diskCount == 0) return;

                // Find the pending combiner for this position
                var posKey = SyncKey(position);
                WaterMeshCombiner targetCombiner = null;

                lock (_pendingSyncLock)
                {
                    if (_pendingSyncCombiners.TryGetValue(posKey, out targetCombiner))
                    {
                        _pendingSyncCombiners.Remove(posKey);
                    }
                }

                // Deserialize disks
                var disks = new List<WaterDisk>(diskCount);
                for (int i = 0; i < diskCount; i++)
                {
                    byte[] diskData = pkg.ReadByteArray();
                    var disk = WaterDisk.Deserialize(diskData);
                    disks.Add(disk);
                }

                if (targetCombiner != null && targetCombiner.WaterDisks.Count == 0)
                {
                    // Populate the waiting combiner with disks and regenerate
                    targetCombiner.AddWaterDisksBatch(disks, skipRegeneration: false);
                    targetCombiner.LiquidType = disks[0].LiquidType;
                    targetCombiner.ForceRegenerate();

                    // Cache the result for faster future loads
                    WaterCombinerCache.UpdateCombinerCache(targetCombiner);

                    FiresLogger.LogInfo($"[WaterOperationRPC] Client restored {diskCount} disks at {position:F1} via RPC sync");
                }
                else if (targetCombiner == null)
                {
                    // No pending combiner - create one from the received disks
                    // Group disks and create combiners
                    var diskGroups = GroupDisksByOverlap(disks);
                    foreach (var group in diskGroups)
                    {
                        if (group.Count == 0) continue;
                        var firstDisk = group[0];
                        var combiner = WaterMeshCombiner.GetOrCreateCombinerFor(firstDisk);
                        if (group.Count > 1)
                        {
                            combiner.AddWaterDisksBatch(group.GetRange(1, group.Count - 1), skipRegeneration: false);
                        }
                    }
                    FiresLogger.LogInfo($"[WaterOperationRPC] Client created combiners from {diskCount} RPC-synced disks at {position:F1}");
                }
                else
                {
                    FiresLogger.LogInfo($"[WaterOperationRPC] Target combiner at {position:F1} already has {targetCombiner.WaterDisks.Count} disks, skipping RPC sync");
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[WaterOperationRPC] Error handling disk sync response: {ex.Message}");
            }
        }

        #endregion

        #region Internal Operations

        /// <summary>
        /// Removes a water disk by its stable hash.
        /// </summary>
        private static void RemoveWaterDiskByHash(int stableHash)
        {
            foreach (var combiner in WaterMeshCombiner.AllCombiners.ToArray())
            {
                if (combiner == null) continue;

                WaterDisk diskToRemove = null;
                foreach (var disk in combiner.WaterDisks)
                {
                    if (disk.StableHash == stableHash)
                    {
                        diskToRemove = disk;
                        break;
                    }
                }

                if (diskToRemove != null)
                {
                    combiner.RemoveWaterDisk(diskToRemove);
                    break;
                }
            }
        }

        /// <summary>
        /// Modifies a water disk by its stable hash.
        /// Updates the combiner's dirty state and file cache so changes persist.
        /// </summary>
        private static void ModifyWaterDiskByHash(int stableHash, float? newRadius, float? newDepth, float? newSurfaceY)
        {
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null) continue;

                foreach (var disk in combiner.WaterDisks)
                {
                    if (disk.StableHash == stableHash)
                    {
                        if (newRadius.HasValue) disk.Radius = newRadius.Value;
                        if (newDepth.HasValue) disk.MaxDepth = newDepth.Value;
                        if (newSurfaceY.HasValue)
                        {
                            var pos = disk.Position;
                            pos.y = newSurfaceY.Value;
                            disk.Position = pos;
                        }
                        combiner.MarkDirty();
                        WaterCombinerCache.UpdateCombinerCache(combiner);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Clears all water in a radius.
        /// Properly destroys anchor ZDOs so water won't be restored on next login.
        /// Also updates the local file cache so stale water doesn't persist.
        /// </summary>
        private static void ClearWaterInRadius(Vector3 center, float radius)
        {
            var combinersToCheck = WaterMeshCombiner.GetCombinersInRadius(center, radius + 50f);

            foreach (var combiner in combinersToCheck)
            {
                if (combiner == null) continue;

                // Check if ALL disks in this combiner are within the radius
                bool allDisksInRadius = true;
                foreach (var disk in combiner.WaterDisks)
                {
                    if (Vector3.Distance(disk.Position, center) > radius)
                    {
                        allDisksInRadius = false;
                        break;
                    }
                }

                if (allDisksInRadius)
                {
                    // Remove from file cache BEFORE destroying (needs valid combiner reference)
                    WaterCombinerCache.OnCombinerPermanentlyDeleted(combiner);

                    // All disks are within radius - destroy entire combiner permanently
                    combiner.DestroyPermanently();
                }
                else
                {
                    // Only some disks are within radius - remove them individually
                    var disksToRemove = new List<WaterDisk>();
                    foreach (var disk in combiner.WaterDisks)
                    {
                        if (Vector3.Distance(disk.Position, center) <= radius)
                        {
                            disksToRemove.Add(disk);
                        }
                    }

                    foreach (var disk in disksToRemove)
                    {
                        combiner.RemoveWaterDisk(disk);
                    }

                    // Update file cache with the modified combiner (fewer disks)
                    WaterCombinerCache.UpdateCombinerCache(combiner);
                }
            }

            // Persist the cache changes to disk immediately so they survive logout
            if (WaterCombinerCache.HasDirtyGroups)
            {
                WaterCombinerCache.SaveCacheAsync();
            }
        }

        /// <summary>
        /// Sends the full water cache to a specific player.
        /// </summary>
        private static void SendCacheToPlayer(long playerId)
        {
            if (!IsServer()) return;

            var pkg = new ZPackage();
            pkg.Write(_serverWaterCache.Count);

            foreach (var disk in _serverWaterCache.Values)
            {
                pkg.Write(disk.Serialize());
            }

            // Send to specific player
            ZRoutedRpc.instance.InvokeRoutedRPC(playerId, RPC_SYNC_CACHE, pkg);
        }

        /// <summary>
        /// Triggers server-side save for combiners near a position.
        /// Updates both the ZDO anchor (via MarkDirty -> debounced save) and
        /// the file cache (via UpdateCombinerCache -> periodic async save).
        /// </summary>
        private static void TriggerServerSave(Vector3 position)
        {
            if (!IsServer()) return;

            var combiners = WaterMeshCombiner.GetCombinersInRadius(position, 50f);
            foreach (var combiner in combiners)
            {
                if (combiner != null)
                {
                    combiner.MarkDirty();
                    WaterCombinerCache.UpdateCombinerCache(combiner);
                }
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Gets the local player's unique ID.
        /// </summary>
        private static long GetLocalPlayerId()
        {
            if (ZNet.instance == null)
                return 0;

            return ZNet.GetUID();
        }

        /// <summary>
        /// Gets the server's peer ID for sending RPCs to server only.
        /// In Valheim, the server is always peer ID 0 when using ZRoutedRpc on a client.
        /// ZRoutedRpc routes target=server via the m_server connection.
        /// </summary>
        private static void RejectNonAdmin(long sender, string operation)
            => FiresLogger.LogWarning($"[WaterOperationRPC] rejected water {operation} from non-admin peer {sender}.");

        private static long GetServerPeerId()
        {
            // In Valheim's ZRoutedRpc, sending to the server from a client
            // uses the server's UID. ZNet.GetServerPeer() gives us that.
            if (ZNet.instance == null) return 0;
            var serverPeer = ZNet.instance.GetServerPeer();
            return serverPeer?.m_uid ?? 0;
        }

        /// <summary>
        /// Checks if we are the server or single player.
        /// IMPORTANT: Returns FALSE if ZNet is not ready yet, to prevent clients acting as servers during load.
        /// </summary>
        public static bool IsServer()
        {
            // During loading, ZNet.instance is null - we must NOT assume we're the server
            // A dedicated server will have ZNet.instance available and IsServer() = true
            // A client will have ZNet.instance available and IsServer() = false
            if (ZNet.instance == null)
                return false;

            return ZNet.instance.IsServer();
        }

        /// <summary>
        /// Checks if multiplayer is active.
        /// </summary>
        public static bool IsMultiplayer()
        {
            return ZNet.instance != null && !ZNet.IsSinglePlayer;
        }

        /// <summary>
        /// Clears all caches. Called on world unload.
        /// Also resets _initialized so RPCs re-register with the new ZRoutedRpc
        /// instance on the next session (logout -> reconnect creates a new ZRoutedRpc).
        /// </summary>
        public static void ClearCaches()
        {
            _appliedDiskHashes.Clear();
            _serverWaterCache.Clear();
            _initialized = false;
            lock (_pendingSyncLock)
            {
                _pendingSyncCombiners.Clear();
            }
        }

        /// <summary>
        /// Broadcasts a global water clear to all connected clients.
        /// Called by the server after ClearAllWaterInternal to ensure clients
        /// clear their local file caches and don't restore stale water on next login.
        /// </summary>
        public static void BroadcastClearAllWater()
        {
            if (!_initialized) Initialize();
            if (ZRoutedRpc.instance == null) return;

            var pkg = new ZPackage();
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_CLEAR_ALL_WATER, pkg);
            FiresLogger.LogInfo("[WaterOperationRPC] Broadcast global water clear to all clients");
        }

        /// <summary>
        /// Adds a disk to the server cache (used during world load).
        /// </summary>
        public static void AddToServerCache(WaterDisk disk)
        {
            if (disk == null) return;
            _serverWaterCache[disk.StableHash] = disk;
        }

        /// <summary>
        /// Gets the server cache (for persistence).
        /// </summary>
        public static IReadOnlyDictionary<int, WaterDisk> GetServerCache()
        {
            return _serverWaterCache;
        }

        #endregion
    }
}
