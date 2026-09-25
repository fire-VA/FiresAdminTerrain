using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Re-place vegetation in an already-generated world ────────────────
    // A converted or imported world arrives with its zones already marked
    // generated, and vanilla decides placement exactly once:
    //
    //     SpawnMode mode = ((!ZNet.instance.IsServer() || IsZoneGenerated(zoneID))
    //                        ? SpawnMode.Client : SpawnMode.Full);
    //
    // so PlaceVegetation never runs again and the ground stays bare no matter
    // what terrain later appears under it. Carving voxel caves into such a
    // world therefore produces caves nothing ever grows in — the vegetation
    // decision was taken before the cave existed.
    //
    // This calls PlaceVegetation directly with SpawnMode.Full for zones the
    // server currently has loaded, which is the one way to get the vanilla
    // placement rules to look at the terrain as it is now.
    //
    // Three constraints shape it, and each is load-bearing:
    //
    //  * SERVER ONLY. PlaceVegetation creates ZDOs; running it on a client
    //    would spawn objects the server never agreed to. The command asks,
    //    the server acts, exactly like the ZDO cleanup.
    //  * LOADED ZONES ONLY. It needs a live Heightmap to sample ground, and
    //    only a loaded zone has one. A radius beyond the server's active area
    //    silently covers nothing, so the report says how many were skipped.
    //  * IT NEVER ADDS TO A ZONE THAT ALREADY HAS VEGETATION. Vanilla has no
    //    idea this already ran, so a second pass would double every bush.
    //    Emptiness is read from the world itself rather than tracked in a
    //    side-file, because that cannot drift.
    internal static class FiresVegRegen
    {
        private const string RpcRequest = "FiresAdminTerrain_VegRegenRequest";
        private const string RpcResult = "FiresAdminTerrain_VegRegenResult";
        private const string CommandName = "fires_regen_veg";
        private const string ApplyToken = "apply";

        private const float DefaultRadiusMeters = 64f;
        private const float MaxRadiusMeters = 512f;
        private const float ServerReplyTimeoutSeconds = 20f;

        private static bool _registered;
        private static ZRoutedRpc _rpcsRegisteredOn;
        private static bool _running;
        private static bool _awaitingReply;

        private static FieldInfo _zonesField;
        private static FieldInfo _rootField;
        private static MethodInfo _placeVegetation;
        private static Type _clearAreaType;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class VegRegen_ZNetAwake_Patch
        {
            [HarmonyPostfix]
            private static void Postfix() => EnsureRpcsRegistered();
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class VegRegen_InitTerminal_Patch
        {
            [HarmonyPostfix]
            private static void Postfix() => Register();
        }

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand(CommandName,
                "Re-runs vanilla vegetation placement for loaded zones around you, for worlds whose zones were " +
                "generated before the terrain they now sit on. Usage: " + CommandName + " [radius] [apply] — without " +
                "'apply' it only reports what it would do.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (ZNet.instance == null || ZoneSystem.instance == null)
                    {
                        args.Context.AddString("[VegRegen] Must be in a world.");
                        return;
                    }
                    if (_running)
                    {
                        args.Context.AddString("[VegRegen] Already running.");
                        return;
                    }

                    float radius = DefaultRadiusMeters;
                    bool apply = false;
                    for (int i = 1; i < args.Length; i++)
                    {
                        string token = args[i]?.Trim();
                        if (string.IsNullOrEmpty(token)) continue;
                        if (string.Equals(token, ApplyToken, StringComparison.OrdinalIgnoreCase)) { apply = true; continue; }
                        if (float.TryParse(token, out float parsed)) radius = Mathf.Clamp(parsed, 0f, MaxRadiusMeters);
                    }

                    Vector3 center = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    Dispatch(args.Context, center, radius, apply);
                });
        }

        internal static void EnsureRpcsRegistered()
        {
            var current = ZRoutedRpc.instance;
            if (current == null || ReferenceEquals(_rpcsRegisteredOn, current)) return;
            try
            {
                current.Register<ZPackage>(RpcRequest, RPC_OnRequest);
                current.Register<ZPackage>(RpcResult, RPC_OnResult);
                _rpcsRegisteredOn = current;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[VegRegen] RPC register failed: {ex.Message}");
            }
        }

        private static void Dispatch(Terminal context, Vector3 center, float radius, bool apply)
        {
            if (ZNet.instance.IsServer())
            {
                context.AddString("[VegRegen] Running locally (you are the server)...");
                foreach (var line in Run(center, radius, apply)) context.AddString(line);
                return;
            }

            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write(apply);
            _awaitingReply = true;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest, pkg);
            context.AddString("[VegRegen] Sent to server...");
            var host = FiresAdminTerrain.Instance;
            if (host != null) host.StartCoroutine(WatchForReply(context));
        }

        private static IEnumerator WatchForReply(Terminal context)
        {
            float deadline = Time.realtimeSinceStartup + ServerReplyTimeoutSeconds;
            while (_awaitingReply && Time.realtimeSinceStartup < deadline) yield return null;
            if (!_awaitingReply) yield break;
            _awaitingReply = false;
            context?.AddString($"[VegRegen] No reply from the server within {ServerReplyTimeoutSeconds:0}s — " +
                               "FiresAdminTerrain is probably missing or out of date there. This runs server-side.");
        }

        private static void RPC_OnRequest(long sender, ZPackage pkg)
        {
            if (!ZNet.instance.IsServer()) return;
            if (!AdminSyncing.IsAdmin(sender))
            {
                SendResult(sender, "[VegRegen] Permission denied — admin only.");
                return;
            }

            Vector3 center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            bool apply = pkg.ReadBool();

            SendResult(sender, "[VegRegen] Server accepted the request — working.");
            foreach (var line in Run(center, radius, apply)) SendResult(sender, line);
        }

        private static void RPC_OnResult(long sender, ZPackage pkg)
        {
            _awaitingReply = false;
            string message = pkg.ReadString();
            try { if (Console.instance != null) Console.instance.AddString(message); } catch { }
            FiresLogger.LogInfo(message);
        }

        private static void SendResult(long target, string message)
        {
            var pkg = new ZPackage();
            pkg.Write(message);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcResult, pkg);
        }

        private static List<string> Run(Vector3 center, float radius, bool apply)
        {
            var results = new List<string>();
            _running = true;
            try
            {
                if (!ResolveReflection(results)) return results;

                var zs = ZoneSystem.instance;
                var zones = _zonesField.GetValue(zs) as IDictionary;
                if (zones == null)
                {
                    results.Add("[VegRegen] Could not read ZoneSystem.m_zones.");
                    return results;
                }

                var clearAreas = MakeEmptyClearAreaList();
                var centerZone = ZoneSystem.GetZone(center);
                int zoneRadius = Mathf.Max(0, Mathf.CeilToInt(radius / ZoneSystem.c_ZoneSize));

                int considered = 0, notLoaded = 0, alreadyPlanted = 0, regenerated = 0, spawned = 0;
                var vegetationNames = BuildVegetationNameSet(zs);

                for (int dz = -zoneRadius; dz <= zoneRadius; dz++)
                for (int dx = -zoneRadius; dx <= zoneRadius; dx++)
                {
                    var zoneId = new Vector2s(centerZone.x + dx, centerZone.y + dz);
                    considered++;

                    if (!zones.Contains(zoneId)) { notLoaded++; continue; }
                    var zoneData = zones[zoneId];
                    var root = _rootField.GetValue(zoneData) as GameObject;
                    if (root == null) { notLoaded++; continue; }
                    var hmap = root.GetComponentInChildren<Heightmap>();
                    if (hmap == null) { notLoaded++; continue; }

                    if (ZoneHasVegetation(zoneId, vegetationNames)) { alreadyPlanted++; continue; }

                    if (!apply) { regenerated++; continue; }

                    var spawnedObjects = new List<GameObject>();
                    try
                    {
                        _placeVegetation.Invoke(zs, new object[]
                        {
                            zoneId, ZoneSystem.GetZonePos(zoneId), root.transform, hmap,
                            clearAreas, ZoneSystem.SpawnMode.Full, spawnedObjects,
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add($"[VegRegen] zone ({zoneId.x},{zoneId.y}) threw: {ex.InnerException?.Message ?? ex.Message}");
                        continue;
                    }
                    regenerated++;
                    spawned += spawnedObjects.Count;
                }

                results.Add($"[VegRegen] {(apply ? "PLACED" : "DRY RUN")} — {considered} zone(s) in a {radius:0}m radius: " +
                            $"{regenerated} {(apply ? "regenerated" : "would be regenerated")}" +
                            (apply ? $" ({spawned} object(s) spawned)" : "") +
                            $", {alreadyPlanted} skipped (already have vegetation), {notLoaded} skipped (not loaded on the server).");
                if (notLoaded > 0)
                    results.Add("[VegRegen] Only zones the SERVER currently has loaded can be done — it needs their live " +
                                "Heightmap to sample ground. Move through the area and run it again for the rest.");
                if (!apply)
                    results.Add($"[VegRegen] Nothing was changed. Re-run as '{CommandName} {radius:0} {ApplyToken}' to place. Save the world first.");
                else
                    results.Add("[VegRegen] Changes persist on the next world save.");
            }
            finally { _running = false; }
            return results;
        }

        private static bool ResolveReflection(List<string> results)
        {
            _zonesField ??= AccessTools.Field(typeof(ZoneSystem), "m_zones");
            _placeVegetation ??= AccessTools.Method(typeof(ZoneSystem), "PlaceVegetation");
            _clearAreaType ??= AccessTools.Inner(typeof(ZoneSystem), "ClearArea");
            var zoneDataType = AccessTools.Inner(typeof(ZoneSystem), "ZoneData");
            _rootField ??= zoneDataType != null ? AccessTools.Field(zoneDataType, "m_root") : null;

            if (_zonesField != null && _placeVegetation != null && _clearAreaType != null && _rootField != null) return true;
            results.Add("[VegRegen] ZoneSystem internals not found — the game changed shape; this needs re-reading against the current build.");
            return false;
        }

        // PlaceVegetation walks the clear-area list unconditionally, so it gets a
        // real empty list of the private nested type rather than null.
        private static object MakeEmptyClearAreaList()
            => Activator.CreateInstance(typeof(List<>).MakeGenericType(_clearAreaType));

        private static HashSet<int> BuildVegetationNameSet(ZoneSystem zs)
        {
            var hashes = new HashSet<int>();
            if (zs.m_vegetation == null) return hashes;
            foreach (var veg in zs.m_vegetation)
                if (veg?.m_prefab != null) hashes.Add(veg.m_prefab.name.GetStableHashCode());
            return hashes;
        }

        // "Has vegetation" means a ZDO in this zone whose prefab is one the
        // vegetation table places. Anything else in the zone — pieces, creatures,
        // the player's own build — must not count, or a built-up zone would look
        // planted and never get its greenery.
        private static bool ZoneHasVegetation(Vector2s zoneId, HashSet<int> vegetationHashes)
        {
            if (vegetationHashes.Count == 0) return false;
            var found = new List<ZDO>();
            ZDOMan.instance.FindObjects(zoneId, found);
            foreach (var zdo in found)
                if (zdo != null && vegetationHashes.Contains(zdo.GetPrefab())) return true;
            return false;
        }
    }
}
