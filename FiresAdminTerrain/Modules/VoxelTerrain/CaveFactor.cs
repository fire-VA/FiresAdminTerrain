using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.844 (Fire 10-07, 121/122.webp at a tunnel mouth: "cave env lighting now flickers constantly as we move ... the fog
    // fades in and out"): the in-cave test was binary (a roof ray + a depth margin), and at a mouth it flipped on 1 m steps; the
    // 0.2.824 timing holds only spaced the flips out, and each started a 10 s env fade and an FTS fog fade.
    // Now one CONTINUOUS factor for the camera, 0 open sky .. 1 deep inside:
    //   cover = share of Rays rays (straight up, a ring at 45 deg, a ring at 20 deg above level, RayLength m) that hit terrain,
    //   depth = smoothstep(0, DepthFull, generated ground over the camera - camera y),
    //   f = cover * depth, smoothed with time constant Smooth s (a moving camera's ray jitter cannot flicker it).
    // Walking out of a mouth ramps f from ~1 to 0. One decision comes from it with wide hysteresis (in at EnterAt, out at
    // LeaveAt): clutter, the ocean cull and the tunnel environment all read InCave, so they cannot disagree. FTS reads Factor.
    // 0.2.844 (Fire 10-07: "our weather cover is constantly triggering on and off too"): vanilla recomputes the local player's
    // cover every second from rays (Player.UpdateCover -> Cover.GetCoverForPoint; Shelter = cover >= 0.8 and under a roof), and
    // in a cave those flip near every opening. While CaveFactor says the camera is in a cave, the local player is fully covered
    // and under a roof: the same decision as the environment, fog, clutter and the ocean cull.
    [HarmonyLib.HarmonyPatch(typeof(Player), "UpdateCover")]
    internal static class Player_UpdateCover_Cave
    {
        private static readonly HarmonyLib.AccessTools.FieldRef<Player, float> f_cover = HarmonyLib.AccessTools.FieldRefAccess<Player, float>("m_coverPercentage");
        private static readonly HarmonyLib.AccessTools.FieldRef<Player, bool> f_roof = HarmonyLib.AccessTools.FieldRefAccess<Player, bool>("m_underRoof");

        [HarmonyLib.HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            if (__instance == null || __instance != Player.m_localPlayer || !CaveFactor.InCave) return;
            f_cover(__instance) = 1f;
            f_roof(__instance) = true;
        }
    }

    internal static class CaveFactor
    {
        private const float RayLength = 40f, DepthFull = 8f, Smooth = 1.5f, TickSeconds = 0.1f, EnterAt = 0.7f, LeaveAt = 0.3f, EyeLift = 0.2f;
        private const int Ring = 8;
        private static readonly Vector3[] s_dirs = BuildDirs();
        private static int s_mask = -1;
        private static float s_next, s_factor, s_cover, s_depth;
        private static bool s_in, s_seeded, s_costLogged;

        internal static float Factor { get { Update(); return s_factor; } }
        internal static bool InCave { get { Update(); return s_in; } }

        private static Vector3[] BuildDirs()
        {
            var dirs = new Vector3[1 + Ring * 2];
            dirs[0] = Vector3.up;
            for (int i = 0; i < Ring; i++)
            {
                float a = i * Mathf.PI * 2f / Ring, c = Mathf.Cos(a), s = Mathf.Sin(a);
                dirs[1 + i] = new Vector3(c, 1f, s).normalized;                                  // 45 deg
                dirs[1 + Ring + i] = new Vector3(c, Mathf.Tan(20f * Mathf.Deg2Rad), s).normalized; // 20 deg
            }
            return dirs;
        }

        private static void Update()
        {
            float now = Time.time;
            if (now < s_next) return;
            float dt = s_seeded ? Mathf.Max(0.01f, now - (s_next - TickSeconds)) : 0f;
            s_next = now + TickSeconds;
            var camera = global::Utils.GetMainCamera();
            if (camera == null || VoxelWorld.ChunkCount <= 0) { s_factor = 0f; s_in = false; return; }
            Vector3 eye = camera.transform.position + Vector3.up * EyeLift;
            if (s_mask < 0) s_mask = LayerMask.GetMask("terrain");
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int hits = 0;
            for (int i = 0; i < s_dirs.Length; i++)
                if (Physics.Raycast(eye, s_dirs[i], RayLength, s_mask, QueryTriggerInteraction.Ignore)) hits++;
            s_cover = hits / (float)s_dirs.Length;
            if (!s_costLogged)
            {
                s_costLogged = true;
                double us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
                FiresLogger.LogDebug($"[CaveFactor] {s_dirs.Length} rays a tick took {us:0} us (first tick; no allocation, {1f / TickSeconds:0} ticks a second)");
            }
            float ground = FiresWorldGenBase.Active ? FiresWorldGenBase.SampleFinalMeters(eye.x, eye.z) : eye.y;
            s_depth = ground - eye.y;
            float raw = s_cover * Mathf.SmoothStep(0f, 1f, s_depth / DepthFull);
            if (FiresSkyIslands.IsOnIsland(eye)) raw = 0f;   // a sky island's underside is open air
            s_factor = s_seeded ? Mathf.Lerp(s_factor, raw, 1f - Mathf.Exp(-dt / Smooth)) : raw;
            s_seeded = true;
            bool was = s_in;
            if (!s_in && s_factor >= EnterAt) s_in = true;
            else if (s_in && s_factor <= LeaveAt) s_in = false;
            if (s_in != was)
                FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[CaveFactor] {0} (f {1:0.00}, cover {2:0.00}, depth {3:0.0} m) at ({4:0},{5:0},{6:0})",
                    s_in ? "entered" : "left", s_factor, s_cover, s_depth, eye.x, eye.y, eye.z));
        }
    }
}
