using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1078 (Fire's skyland stalls: "[Support] 22 piece(s) destroyed ... 2 cascading" as skylands came in): a location's pieces
    // ran vanilla WearNTear.UpdateSupport before the voxel ground under them had a collider (an island zone still wrapping, a chunk
    // awaiting its first collider), found no support and collapsed, cascading through the build. BodyHold (1054) only holds
    // rigidbodies. Here a piece on voxel ground skips its support pass (no damage, no collapse) while the ground under it is not
    // ready, and runs normally the pass after it is. Vanilla ground and ready voxel ground are untouched.
    internal static class VoxelSupportHold
    {
        private const float ZoneSettleSeconds = 2f;
        private const float GroundProbe = 12f;
        private const float SummarySeconds = 30f;

        private static readonly HashSet<int> s_held = new HashSet<int>();
        private static readonly Dictionary<string, int> s_heldByLocation = new Dictionary<string, int>();
        private static int s_released;
        private static float s_nextSummary;
        private static int s_terrainMask = -1;

        // 0.2.1410 ([fgn] WearNTear audit #10, via [lead] 11-08: a terrain raycast on EVERY UpdateSupport of every island piece): a piece whose
        // island ground was found keeps that answer for IslandReadySeconds (pieces don't move); a hold, or a chunk awaiting its collider
        // (BodyHold, still checked every call), clears it. The voxel-zone and BodyHold checks are unchanged.
        private const float IslandReadySeconds = 5f;
        private static readonly Dictionary<int, float> s_islandReadyUntil = new Dictionary<int, float>();

        private static bool GroundNotReady(Vector3 p, int id)
        {
            if (!FiresVoxelConfig.StreamerRuns) return false;
            Vector2s zone = ZoneSystem.GetZone(p);
            bool voxelZone = VoxelOnlyMode.Active || VoxelZoneStreamer.ZoneSwapped(zone);
            bool island = FiresSkyIslands.Enabled && FiresSkyIslands.IslandAt(p.x, p.z);
            if (!voxelZone && !island) return false;   // vanilla ground: vanilla decides
            if (voxelZone && !VoxelOnlyMode.Active && !VoxelZoneStreamer.IsZoneSolidFor(zone, ZoneSettleSeconds)) return true;
            if (BodyHold.PendingAt(p)) return true;   // a chunk here has a surface but no collider yet
            if (island)
            {
                // An island piece with no terrain collider under it: the island's chunks have not arrived yet.
                if (s_islandReadyUntil.TryGetValue(id, out float until) && Time.time < until) return false;
                if (s_terrainMask == -1) s_terrainMask = LayerMask.GetMask("terrain");
                if (!Physics.Raycast(p + Vector3.up * 1f, Vector3.down, GroundProbe, s_terrainMask, QueryTriggerInteraction.Ignore)) { s_islandReadyUntil.Remove(id); return true; }
                if (s_islandReadyUntil.Count > 20000) s_islandReadyUntil.Clear();   // bound: destroyed pieces' ids
                s_islandReadyUntil[id] = Time.time + IslandReadySeconds;
            }
            return false;
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
        private static class Patch_WearNTear_UpdateSupport
        {
            private static bool Prefix(WearNTear __instance, bool __runOriginal)
            {
                if (!__runOriginal) return false;   // 0.2.1410: an earlier prefix already skipped this pass; nothing to hold
                if (__instance == null) return true;
                int id = __instance.GetInstanceID();
                bool hold = GroundNotReady(__instance.transform.position, id);
                if (hold)
                {
                    if (s_held.Add(id))
                    {
                        string loc = LocationOf(__instance);
                        s_heldByLocation.TryGetValue(loc, out int c);
                        s_heldByLocation[loc] = c + 1;
                    }
                }
                else if (s_held.Remove(id)) s_released++;
                Summary();
                return !hold;
            }
        }

        private static string LocationOf(WearNTear w)
        {
            var loc = w.GetComponentInParent<Location>();
            return loc != null ? Utils.GetPrefabName(loc.gameObject) : "(none)";
        }

        private static void Summary()
        {
            if (!FiresLogger.DebugEnabled || Time.time < s_nextSummary) return;
            s_nextSummary = Time.time + SummarySeconds;
            if (s_held.Count == 0 && s_released == 0) return;
            string largest = "(none)"; int most = 0;
            foreach (var kv in s_heldByLocation) if (kv.Value > most) { most = kv.Value; largest = kv.Key; }
            FiresLogger.LogDebug($"[FiresVoxel] support hold: {s_held.Count} piece(s) held, {s_released} released (largest location {largest}, {most} held)");
            s_released = 0;
            s_heldByLocation.Clear();
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class Patch_ZNet_OnDestroy
        {
            private static void Postfix() { s_held.Clear(); s_heldByLocation.Clear(); s_released = 0; }
        }
    }
}
