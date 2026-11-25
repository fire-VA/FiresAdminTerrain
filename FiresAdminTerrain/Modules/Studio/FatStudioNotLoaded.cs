using UnityEngine;

namespace VerdantsAscent.Terrain.Studio
{
    // 0.2.1493 (Fire 11-19 on the God Complex dedi audit, verbatim: "yes do the log line then D, and fix the studio"; [ghost] leads GC / Studio).
    // Every Studio "not loaded:" refusal goes through here: it logs WHY the ground was not there (one Info line, at most one a second, the
    // rest counted) and returns the reason UNCHANGED, so God Complex's "not loaded:" prefix test (StudioOps.cs:531) keeps working.
    // The line answers the audit's open question per refusal: is the zone in ZoneSystem's loaded set, does a heightmap exist at the spot,
    // does the ground raycast (ZoneSystem.GetGroundHeight, what the water / location ops use) hit, and on which kind of peer.
    internal static class FatStudioNotLoaded
    {
        private static float s_nextAt;
        private static int s_suppressed;

        internal static Vector3 ZoneCentre(int zx, int zz) => new Vector3(zx * 64f, 0f, zz * 64f);

        internal static string Log(string reason, Vector3 pos)
        {
            try
            {
                if (Time.unscaledTime < s_nextAt) { s_suppressed++; return reason; }
                s_nextAt = Time.unscaledTime + 1f;
                var zs = ZoneSystem.instance;
                var zone = ZoneSystem.GetZone(pos);
                bool loaded = zs != null && zs.IsZoneLoaded(pos);
                bool generated = zs != null && zs.IsZoneGenerated(zone);
                bool map = Heightmap.FindHeightmap(pos) != null;
                float gy = 0f;
                bool hit = zs != null && zs.GetGroundHeight(pos, out gy);
                string peer = ZNet.instance == null ? "no ZNet" : ZNet.instance.IsDedicated() ? "dedicated server" : ZNet.instance.IsServer() ? "host" : "client";
                FiresLogger.LogInfo($"[StudioRefusal] {reason} | at ({pos.x:0},{pos.z:0}) zone {zone.x},{zone.y}: IsZoneLoaded {loaded}, IsZoneGenerated {generated}, "
                    + $"FindHeightmap {(map ? "found" : "none")}, ground raycast {(hit ? $"hit y {gy:0.0}" : "miss")}; {peer}"
                    + (s_suppressed > 0 ? $" (+{s_suppressed} refusal(s) in the last second not logged)" : "") + " (0.2.1493)");
                s_suppressed = 0;
            }
            catch (System.Exception ex) { FiresLogger.LogDebug($"[StudioRefusal] the diagnostic itself threw: {ex.Message}"); }
            return reason;
        }
    }
}
