using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Proactive VATile spawner — bypasses ZNetScene's CreateObjectsSorted
    // queue for tile anchors so they spawn within a frame of the zone going
    // active, instead of waiting minutes for the default queue to reach them.
    //
    // The pop-in problem:
    //   ZNetScene processes spawn requests in chunks (m_zdoLoad default = 32
    //   per frame), sorted by ObjectType. Even with Prioritized type, on a
    //   megabase with thousands of pending ZDOs (FAP-registered pieces +
    //   loaded blueprint contents + vanilla rocks/trees) the queue can sit
    //   in front of us for 5+ minutes. Logged delta T+305s between
    //   ZoneSystem.SpawnZone success and TileAnchor.Awake on 2026-05-24.
    //
    // The fix:
    //   1. Postfix ZoneSystem.SpawnZone — fires when ANY zone (including
    //      distant area zones at 600m) finishes spawning.
    //   2. Walk ZDOMan for VATile ZDOs in that sector via FindObjects.
    //   3. For each one not already instantiated, enqueue for force-spawn.
    //   4. A background coroutine on the plugin root drains the queue at
    //      MAX_SPAWN_PER_FRAME items per frame (default 2) — bounds the
    //      per-frame hitch from MeshFilter+ZNetView Awake cascade. Cheap
    //      because tile spawn = clone an inactive prefab + set ZNetView.
    //
    // Bypasses the queue via reflection on ZNetScene.CreateObject(ZDO).
    // If reflection fails (Valheim API change), the patch silently no-ops
    // and the queue path still works — just slow as before.
    public static class TileProactiveSpawner
    {
        // How many anchors we instantiate per frame. Each instantiation is
        // cheap (~0.5-1ms) but we cap to keep frame budget predictable.
        // At 4/frame = ~4ms/frame budget, full pop-in of 100 anchors
        // completes in ~25 frames (under half a second at 60fps). Bumped
        // from 2 after user confirmed pop-in was "better but could be
        // faster" 2026-05-25.
        public const int MAX_SPAWN_PER_FRAME = 4;

        // De-dupe queue. ZoneSystem.SpawnZone can fire repeatedly for the
        // same zone (TTL pokes) so we keep a set of "already attempted"
        // ZDOIDs and skip them.
        private static readonly HashSet<ZDOID> s_alreadyAttempted = new HashSet<ZDOID>();
        private static readonly Queue<ZDO> s_pending = new Queue<ZDO>();
        private static Coroutine s_drainCoroutine;

        // Reflection handles. Looked up once on first use.
        private static MethodInfo s_miCreateObject;
        private static MethodInfo s_miFindObjects;
        private static bool s_reflectionAttempted;

        private static bool EnsureReflection()
        {
            if (s_reflectionAttempted)
                return s_miCreateObject != null && s_miFindObjects != null;
            s_reflectionAttempted = true;

            try
            {
                // ZNetScene.CreateObject(ZDO zdo) — internal, returns GameObject.
                // Some Valheim versions also have an overload with a bool param.
                s_miCreateObject = typeof(ZNetScene).GetMethod(
                    "CreateObject",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null,
                    types: new[] { typeof(ZDO) },
                    modifiers: null);

                // ZDOMan.FindObjects(Vector2i sector, List<ZDO> objects) — public.
                s_miFindObjects = typeof(ZDOMan).GetMethod(
                    "FindObjects",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: new[] { typeof(Vector2i), typeof(List<ZDO>) },
                    modifiers: null);

                if (s_miCreateObject == null)
                    FiresLogger.LogWarning("[TileProactiveSpawner] ZNetScene.CreateObject(ZDO) not found — proactive spawn disabled, falling back to vanilla queue.");
                if (s_miFindObjects == null)
                    FiresLogger.LogWarning("[TileProactiveSpawner] ZDOMan.FindObjects(Vector2i,List<ZDO>) not found — proactive spawn disabled.");

                return s_miCreateObject != null && s_miFindObjects != null;
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[TileProactiveSpawner] reflection setup threw: {ex}");
                return false;
            }
        }

        // ZoneSystem.SpawnZone signature: bool SpawnZone(Vector2i zoneID, SpawnMode mode, out GameObject root)
        // We piggyback on the existing ZoneStreamTiming patch site but with a
        // separate prefix class to keep concerns isolated.
        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        private static class ZoneSystem_SpawnZone_Proactive
        {
            private static void Postfix(Vector2i zoneID, GameObject root, bool __result)
            {
                if (!__result || root == null) return;
                if (!EnsureReflection()) return;
                if (ZDOMan.instance == null || ZNetScene.instance == null) return;
                if (!TileAnchorPrefab.IsRegistered) return;

                ScanSectorAndEnqueue(zoneID);
            }
        }

        // Find every VATile ZDO whose sector key == this zone's, enqueue
        // ones not already instantiated. Cheap O(N) over sector ZDOs.
        private static void ScanSectorAndEnqueue(Vector2i sector)
        {
            var list = new List<ZDO>();
            try
            {
                s_miFindObjects.Invoke(ZDOMan.instance, new object[] { sector, list });
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[TileProactiveSpawner] FindObjects threw for sector ({sector.x},{sector.y}): {ex.Message}");
                return;
            }

            int vaTileHash = TileAnchorPrefab.PrefabName.GetStableHashCode();
            int enqueued = 0;
            foreach (var zdo in list)
            {
                if (zdo == null) continue;
                if (zdo.GetPrefab() != vaTileHash) continue;
                if (!s_alreadyAttempted.Add(zdo.m_uid)) continue; // already tried
                if (ZNetScene.instance.FindInstance(zdo) != null) continue; // vanilla beat us to it

                s_pending.Enqueue(zdo);
                enqueued++;
            }

            if (enqueued > 0)
            {
                FiresLogger.LogInfo($"[TileProactiveSpawner] sector ({sector.x},{sector.y}): enqueued {enqueued} VATile ZDO(s) for proactive spawn. Total pending: {s_pending.Count}.");
                StartDrainIfNeeded();
            }
        }

        private static void StartDrainIfNeeded()
        {
            if (s_drainCoroutine != null) return;
            var host = FiresAdminTerrain.Instance;
            if (host == null) return;
            s_drainCoroutine = host.StartCoroutine(DrainCoroutine());
        }

        private static IEnumerator DrainCoroutine()
        {
            while (s_pending.Count > 0)
            {
                int spawnedThisFrame = 0;
                while (spawnedThisFrame < MAX_SPAWN_PER_FRAME && s_pending.Count > 0)
                {
                    var zdo = s_pending.Dequeue();
                    if (zdo == null) continue;

                    // Race-condition guard: vanilla might have spawned it
                    // between enqueue and drain.
                    if (ZNetScene.instance == null) break;
                    if (ZNetScene.instance.FindInstance(zdo) != null) continue;

                    try
                    {
                        var go = s_miCreateObject.Invoke(ZNetScene.instance, new object[] { zdo });
                        if (go != null) spawnedThisFrame++;
                    }
                    catch (Exception ex)
                    {
                        FiresLogger.LogWarning($"[TileProactiveSpawner] CreateObject failed for ZDO {zdo.m_uid}: {ex.Message}");
                    }
                }
                yield return null;
            }
            s_drainCoroutine = null;
        }

        // Reset on world unload — fresh session = fresh dedup set.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private static class ZNet_Shutdown_ResetProactive
        {
            private static void Prefix()
            {
                if (s_alreadyAttempted.Count == 0 && s_pending.Count == 0) return;
                FiresLogger.LogInfo($"[TileProactiveSpawner] ZNet.Shutdown — clearing {s_alreadyAttempted.Count} attempted-set entries, {s_pending.Count} pending.");
                s_alreadyAttempted.Clear();
                s_pending.Clear();
                s_drainCoroutine = null;
            }
        }
    }
}
