using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.HeightmapSnow
{
    // 0.2.1288 ([lead] 10-30, Fire "get the snow trails working"; three hand-picked trail spots were a road, a road, a dry basin):
    // fat_trail_field_test picks and proves its OWN spot. Around the player it scans 10 m lines (4 m grid, 4 headings, 1 m samples)
    // and keeps the nearest one where EVERY sample has Core depth >= 0.4 m (Core's paved / piece rules are inside that depth),
    // biome Meadows, ground >= water + 1 m and no piece / static collider within 12 m. It logs the line, takes a pose SHOT looking
    // down at it from the side, WALKS it with real input (the SnowshoeTest walk: PlayerController paused, SetLookDir + SetControls
    // every frame - a route did not move the player on 10-30), waits for FAT's own "[SnowTrail] stamp at ... drawn now" proof lines,
    // and takes the same SHOT again. PASS = at least 3 stamps on the line drawn down to the packed layer (cut >= 0.5, drawn now at
    // least 0.1 m under drawn before). Reused: Core DepthAt (VoxelSnowDepth), the SnowshoeTest walk, FDT's SHOT {pose} + settle line.
    internal static class FatTrailFieldTest
    {
        private const string Tag = "[TrailFieldTest]";
        private static bool s_running;
        private static string s_shot;
        private static volatile bool s_settled;
        private static readonly List<string> s_stamps = new List<string>();
        private sealed class Runner : MonoBehaviour { }
        private static Runner s_runner;
        private sealed class Listener : BepInEx.Logging.ILogListener
        {
            public void LogEvent(object sender, BepInEx.Logging.LogEventArgs e)
            {
                var m = e?.Data?.ToString();
                if (m == null) return;
                if (s_shot != null && m.Contains("[FiresTestShots] SHOT '" + s_shot) && m.Contains("settled")) s_settled = true;
                if (m.Contains("[SnowTrail] stamp at (") && m.Contains("drawn now")) lock (s_stamps) s_stamps.Add(m);
            }
            public void Dispose() { }
        }
        private static Listener s_listener;
        private static readonly Regex StampRx = new Regex(@"stamp at \((?<x>-?[\d.]+),(?<z>-?[\d.]+)\).*?drawn before (?<b>-?[\d.]+).*?cut now (?<c>-?[\d.]+), drawn now (?<d>[+-]?[\d.]+)", RegexOptions.Compiled);

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fat_trail_field_test", "[F5 test] FAT snow trail in an open field: picks a valid 10 m line round you, shots before / after a real walk, PASS on drawn trail stamps (admin)",
                args =>
                {
                    if (s_running) return;
                    if (Player.m_localPlayer == null || ZoneSystem.instance == null || WorldGenerator.instance == null) { FiresLogger.LogWarning($"{Tag} refused: no player / world"); return; }
                    if (s_runner == null) { var go = new GameObject("FatTrailFieldTest"); UnityEngine.Object.DontDestroyOnLoad(go); s_runner = go.AddComponent<Runner>(); }
                    if (s_listener == null) { s_listener = new Listener(); BepInEx.Logging.Logger.Listeners.Add(s_listener); }
                    s_runner.StartCoroutine(Run());
                }, isCheat: true);
        }

        private static bool Clear(Vector3 p, float water, out string why)
        {
            why = null;
            if (WorldGenerator.instance.GetBiome(p.x, p.z) != Heightmap.Biome.Meadows) { why = "not Meadows"; return false; }
            float g = ZoneSystem.instance.GetGroundHeight(p);
            if (g < water + 1f) { why = $"ground {g:0.0} under water + 1"; return false; }
            float d = VoxelSnowDepth.DepthMetresAt(new Vector3(p.x, g, p.z));
            if (d < 0.4f) { why = $"depth {d:0.00}"; return false; }
            return true;
        }
        private static readonly int Blockers = LayerMask.GetMask("piece", "piece_nonsolid", "static_solid", "Default_small", "vehicle");

        private static IEnumerator Shot(string caption)
        {
            s_shot = caption; s_settled = false;
            yield return null;
            float until = Time.realtimeSinceStartup + 12f;
            while (!s_settled && Time.realtimeSinceStartup < until) yield return null;
            if (!s_settled) FiresLogger.LogWarning($"{Tag} shot '{caption}': no FDT confirmation in 12 s");
            yield return new WaitForSecondsRealtime(0.5f);
            s_shot = null;
        }

        private static IEnumerator Run()
        {
            s_running = true;
            var inv = CultureInfo.InvariantCulture;
            var me = Player.m_localPlayer;
            float water = ZoneSystem.instance.m_waterLevel;
            FiresLogger.LogInfo($"{Tag} BEGIN FAT {VerdantsAscent.FiresAdminTerrain.PluginVersion}: scan, before shot, walk, after shot (~60 s)");
            string verdict = "FAIL", detail = "";
            PlayerController pc = null; bool pcWas = false;
            try
            {
                // 1 the line: nearest valid 10 m line on a 4 m grid within 40 m, headings N / E / S / W
                Vector3 c = me.transform.position, bestA = Vector3.zero, bestDir = Vector3.zero; float bestDist = float.MaxValue; int tried = 0, valid = 0;
                var dirs = new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
                for (float dx = -40f; dx <= 40f; dx += 4f)
                    for (float dz = -40f; dz <= 40f; dz += 4f)
                        foreach (var dir in dirs)
                        {
                            var a = new Vector3(c.x + dx, 0f, c.z + dz); tried++;
                            bool ok = true;
                            for (int s = -2; s <= 10 && ok; s++) ok = Clear(a + dir * s, water, out _);
                            if (!ok) continue;
                            var mid = a + dir * 5f; mid.y = ZoneSystem.instance.GetGroundHeight(mid);
                            if (Physics.CheckSphere(mid, 12f, Blockers)) continue;
                            valid++;
                            float dist = new Vector2(dx, dz).magnitude;
                            if (dist < bestDist) { bestDist = dist; bestA = a; bestDir = dir; }
                        }
                if (bestDist == float.MaxValue) { detail = $"no valid 10 m line within 40 m of ({c.x:0},{c.z:0}) ({tried} tried): needs Meadows, depth >= 0.4, ground >= water + 1, nothing within 12 m"; yield break; }
                Vector3 A = bestA, B = bestA + bestDir * 10f, M = bestA + bestDir * 5f;
                A.y = ZoneSystem.instance.GetGroundHeight(A); B.y = ZoneSystem.instance.GetGroundHeight(B); M.y = ZoneSystem.instance.GetGroundHeight(M);
                FiresLogger.LogInfo(string.Format(inv, "{0} line ({1:0.0},{2:0.0}) -> ({3:0.0},{4:0.0}), {5} valid of {6} tried, depth at the middle {7:0.00} m, ground {8:0.0} (water {9:0.0})",
                    Tag, A.x, A.z, B.x, B.z, valid, tried, VoxelSnowDepth.DepthMetresAt(M), M.y, water));

                // 2 stand 2 m before the line, facing along it
                var start = A - bestDir * 2f; start.y = ZoneSystem.instance.GetGroundHeight(start) + 0.3f;
                me.TeleportTo(start, Quaternion.LookRotation(bestDir), false);
                yield return new WaitForSeconds(4f);

                // 3 before shot: 6 m to the side, 5 m up, looking at the middle
                Vector3 side = Vector3.Cross(Vector3.up, bestDir).normalized, cam = M + side * 6f + Vector3.up * 5f, look = M - cam;
                float yaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, pitch = Mathf.Atan2(-look.y, new Vector2(look.x, look.z).magnitude) * Mathf.Rad2Deg;
                string pose = string.Format(inv, "{{pose {0:0.0},{1:0.0},{2:0.0},{3:0},{4:0}}}", cam.x, cam.y, cam.z, yaw, pitch);
                FiresLogger.LogInfo($"{Tag} SHOT {pose} trailfield_before");
                yield return Shot("trailfield_before");

                // 4 the walk: 14 m along the line with real input (SnowshoeTest's walk)
                lock (s_stamps) s_stamps.Clear();
                pc = me.GetComponent<PlayerController>(); pcWas = pc != null && pc.enabled; if (pc != null) pc.enabled = false;
                Vector3 w0 = me.transform.position; float t = 0f;
                while (t < 15f && new Vector2(me.transform.position.x - w0.x, me.transform.position.z - w0.z).magnitude < 14f)
                {
                    me.SetLookDir(bestDir); me.SetControls(Vector3.forward, false, false, false, false, false, false, false, false, false, false);
                    t += Time.deltaTime; yield return null;
                }
                me.SetControls(Vector3.zero, false, false, false, false, false, false, false, false, false, false);
                if (pc != null) pc.enabled = pcWas; pc = null;
                float walked = new Vector2(me.transform.position.x - w0.x, me.transform.position.z - w0.z).magnitude;
                FiresLogger.LogInfo($"{Tag} walked {walked:0.0} m in {t:0.0} s");

                // 5 step off the line (so the after shot sees it) and wait for the stamp proof lines (logged after the skin rebuild, <= 20 s)
                me.TeleportTo(M + side * 8f + Vector3.up * 0.5f, Quaternion.LookRotation(-side), false);
                float until = Time.realtimeSinceStartup + 30f;
                while (Time.realtimeSinceStartup < until) { lock (s_stamps) if (s_stamps.Count >= 12) break; yield return null; }
                int onLine = 0, drawn = 0;
                lock (s_stamps)
                    foreach (var line in s_stamps)
                    {
                        var m = StampRx.Match(line);
                        if (!m.Success) continue;
                        var p = new Vector3(float.Parse(m.Groups["x"].Value, inv), 0f, float.Parse(m.Groups["z"].Value, inv));
                        var ap = p - new Vector3(A.x, 0f, A.z); float along = Vector3.Dot(ap, bestDir), off = (ap - bestDir * along).magnitude;
                        if (along < -1f || along > 11f || off > 1.5f) continue;
                        onLine++;
                        float b = float.Parse(m.Groups["b"].Value, inv), cut = float.Parse(m.Groups["c"].Value, inv), d = float.Parse(m.Groups["d"].Value.TrimStart('+'), inv);
                        if (cut >= 0.5f && d <= b - 0.1f) drawn++;
                    }

                // 6 after shot, same pose
                FiresLogger.LogInfo($"{Tag} SHOT {pose} trailfield_after");
                yield return Shot("trailfield_after");
                verdict = drawn >= 3 ? "PASS" : "FAIL";
                detail = $"{drawn} of {onLine} stamp(s) on the line drawn down (want >= 3; {s_stamps.Count} stamp line(s) in all)";
            }
            finally
            {
                if (pc != null) { me.SetControls(Vector3.zero, false, false, false, false, false, false, false, false, false, false); pc.enabled = pcWas; }
                FiresLogger.LogInfo($"{Tag} END {verdict} - {detail}");
                s_running = false;
            }
        }
    }
}
