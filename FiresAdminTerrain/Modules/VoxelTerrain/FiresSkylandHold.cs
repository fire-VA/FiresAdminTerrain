using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Utilities;

namespace VerdantsAscent.Terrain.Voxel
{
    // Enemies standing on a skyland fell through while its mesh was gone. A zone skyland has no stored mesh: every
    // time its zone loads, the streamer rebuilds its voxel chunks and colliders, and vanilla instantiates the creatures (and
    // the player arriving) straight away. Until the zone's colliders are live, a character on or over a skyland fell through
    // and landed on the ground 150 m below (VoidRescue only ever finds that lower floor). Static props already wait for the
    // ground (Patch_StaticPhysics_WaitForVoxelGround); characters did not.
    //
    // While the zone a character stands over is a zone skyland that is not solid yet, the body the owner simulates is held:
    // no velocity, and never below the island's top (its carved column, read from the shape, which needs no collider). It is
    // released the frame the zone is solid, or after HoldMaxSeconds as a safety net. One line per release, at Debug.
    internal static class FiresSkylandHold
    {
        private const float HoldMaxSeconds = 45f;
        private static readonly Dictionary<int, float> s_heldSince = new Dictionary<int, float>();
        private static readonly HashSet<int> s_capped = new HashSet<int>();   // let go at the cap: not held again
        private static bool s_skipSaid;

        [HarmonyPatch(typeof(Character), nameof(Character.CustomFixedUpdate))]
        private static class Patch_Character_HoldOnUnsolidSkyland
        {
            private static bool Prefix(Character __instance)
            {
                long t0 = FatHooks.Start();
                bool run = true;
                try { run = Hold(__instance); }
                finally { FatHooks.Stop(FatHooks.Hook.SkylandHold, t0, !run, __instance == Player.m_localPlayer); }
                return run;
            }

            private static bool Hold(Character __instance)
            {
                ReportIslandChunks();
                // 0.2.1369 (winter audit: 0.45 ms/s, 18k calls/17 s on a world with no skylands): nothing can be held with no island and no voxel ground. Voxel worlds keep the
                // full path below, because its zone-solid test stamps the swap time other callers read.
                var skipKey = FiresVoxelConfig.SkylandHoldSkipNoIslands;
                if ((skipKey == null || skipKey.Value) && s_heldSince.Count == 0 && !FiresSkyIslands.Enabled && VoxelWorld.ChunkCount <= 0)
                {
                    if (!s_skipSaid) { s_skipSaid = true; FiresLogger.LogInfo("[FiresSkyIslands] no skylands and no voxel ground on this world: the character hold answers at once (0.2.1369)"); }
                    return true;
                }
                // 0.2.1107: timed only. Its common path is already a few dictionary lookups (the zone-solid test runs first and has a
                // side effect - the lazy swap timestamp other callers read - so no earlier exit is added here: same behaviour).
                if (__instance == null || __instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()) return true;
                Vector3 p = __instance.transform.position;
                int id = __instance.GetInstanceID();
                if (s_capped.Contains(id)) return true;
                if (FatNoClip.IsOn(__instance))
                {
                    if (s_heldSince.ContainsKey(id)) FatNoClip.Skips(__instance, "the skyland hold");
                    Release(__instance, id, "noclip flight");
                    return true;
                }
                // The cheap test first: a solid zone (the usual case) never pays for the island lookup.
                if (VoxelZoneStreamer.IsZoneSolidFor(ZoneSystem.GetZone(p), 0f))
                {
                    Release(__instance, id, "its zone is solid");
                    return true;
                }
                if (!FiresSkyIslands.IsOnZoneSkyland(p) || !FiresSkyIslands.TrySkylandTopAt(p, out _))
                {
                    Release(__instance, id, null);
                    return true;
                }
                float now = Time.time;
                if (!s_heldSince.TryGetValue(id, out float since)) { s_heldSince[id] = since = now; }
                if (now - since > HoldMaxSeconds)
                {
                    s_capped.Add(id);
                    Release(__instance, id, $"the {HoldMaxSeconds:0} s cap (its zone is still not solid)");
                    return true;
                }
                Rigidbody body = __instance.m_body;
                if (body != null) body.linearVelocity = Vector3.zero;
                if (FiresSkyIslands.TrySkylandTopAt(p, out float top) && p.y < top)
                    __instance.transform.position = new Vector3(p.x, top, p.z);
                return false;   // no physics step, no fall, until the island's colliders exist
            }

            private static void Release(Character c, int id, string why)
            {
                if (!s_heldSince.TryGetValue(id, out float since)) return;
                s_heldSince.Remove(id);
                if (why == null) return;
                Vector3 p = c.transform.position;
                FiresLogger.LogDebug($"[FiresSkyIslands] held {Utils.GetPrefabName(c.gameObject)} on the skyland at ({p.x:0}, {p.y:0}, {p.z:0}) "
                    + $"for {Time.time - since:0.0} s until {why}");
            }
        }

        // Every IslandReportSeconds when the counts moved: island chunks restored from the cache vs generated from the shape.
        private const float IslandReportSeconds = 30f;
        private static float s_nextIslandReport;
        private static int s_lastRestored = -1, s_lastGenerated = -1;

        private static void ReportIslandChunks()
        {
            float now = Time.time;
            if (now < s_nextIslandReport) return;
            s_nextIslandReport = now + IslandReportSeconds;
            int restored = VoxelChunk.IslandChunksRestored, generated = VoxelChunk.IslandChunksGenerated;
            // 0.2.1069 (Fire 10-26, SuperFlatSkyCaves): on super flat, say where the nearest island is every report, so
            // "0 island chunks" is read as "none within the wrap radius", not "islands never generate".
            if (FatSuperFlat.On && FiresSkyIslands.Enabled && Player.m_localPlayer != null
                && FiresSkyIslands.TryNearest(Player.m_localPlayer.transform.position, out float nx, out float nz, out float ntop, out float nd))
                FiresLogger.LogDebug($"[FiresSkyIslands] super flat: island chunks built {restored + generated} (nearest island at ({nx:0},{nz:0}) top y {ntop:0}, {nd:0} m away; islands build when their zone wraps, ~{FiresVoxelConfig.WrapKeepRadius?.Value ?? 128f:0}-{VoxelZoneStreamer.GovernorRadius:0} m)");
            if (restored == s_lastRestored && generated == s_lastGenerated) return;
            s_lastRestored = restored;
            s_lastGenerated = generated;
            FiresLogger.LogDebug($"[FiresSkyIslands] island chunks this session: {restored} restored from the chunk cache (generated once, saved), "
                + $"{generated} generated from the island shape; {s_heldSince.Count} character(s) held on unsolid skyland ground now");
        }

        [HarmonyPatch(typeof(Character), "OnDestroy")]
        private static class Patch_Character_ForgetHold
        {
            private static void Prefix(Character __instance)
            {
                if (__instance == null) return;
                s_heldSince.Remove(__instance.GetInstanceID());
                s_capped.Remove(__instance.GetInstanceID());
            }
        }
    }
}
