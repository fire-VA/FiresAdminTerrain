using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Testing
{
    // ── `wavematch_test [radius=40]`: does the body float on the wave that is drawn? (0.2.698; Fire 10-01: "our swim
    // trigger/float trigger doesn't actually match the waves") ─────────────────────────────────────────────────────────────
    // Read-only, on-demand F5 contract test. On a Points x Points grid within radius of the tester, at every point inside a
    // SEA WaterVolume (global wind, surface at the sea plane): the PHYSICS surface (WaterVolume.GetWaterSurface, what
    // swimming, Floating and boats use) against the SHADER's (the FiresWater height: the volume's plane + CalcWave with
    // FiresTossinShade's shore-field R as the depth factor, at the same moment). PASS when every point agrees within
    // Tolerance and at least MinPoints were compared. Without FTS's FiresShoreField.TrySampleWave the step SKIPs.
    internal static class WaveMatchTest
    {
        private const string Command = "wavematch_test", Tag = "[WaveMatchTest]";
        private const float DefaultRadius = 40f, Tolerance = 0.05f, ShotLift = 1.7f, ShotHoldSeconds = 7.5f, ServerWaitSeconds = 5f;
        private const int Points = 7, MinPoints = 20, ListWorst = 5;
        private static readonly AccessTools.FieldRef<WaterVolume, float> SurfaceOffset = AccessTools.FieldRefAccess<WaterVolume, float>("m_surfaceOffset");
        private static readonly AccessTools.FieldRef<float> WrappedTime = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(WaterVolume), "s_wrappedDayTimeSeconds"));
        private static bool s_registered, s_running;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                "[F5 test] [no relaunch] read-only: compares the swim/float wave surface (physics) with the drawn FiresWater "
                + "wave (shore-field depth) at sea points around you, within 0.05 m. Args: [radius=40].",
                args => Start(args.Args.Skip(1).ToArray()));
        }

        private static void Start(string[] args)
        {
            if (s_running) { Say($"{Tag} a run is already going."); return; }
            if (ZNet.instance == null || Player.m_localPlayer == null) { Say($"{Tag} join a world first."); return; }
            float r = DefaultRadius;
            if (args.Length > 0 && !float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r))
            { Say($"{Tag} usage: {Command} [radius]; nothing started."); return; }
            ZNet.instance.StartCoroutine(Run(Mathf.Clamp(r, 5f, 200f)));
        }

        private static IEnumerator Run(float radius)
        {
            s_running = true;
            float started = Time.realtimeSinceStartup;
            int pass = 0, fail = 0, skip = 0;
            try
            {
                float hashUntil = Time.realtimeSinceStartup + 5f;
                while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
                const string mod = global::VerdantsAscent.FiresAdminTerrain.PluginName, version = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
                var me = Player.m_localPlayer;
                Vector3 at = me.transform.position;
                string world = ZNet.World != null ? ZNet.World.m_name : "?";
                Say($"{Tag} BEGIN {mod} {version} ({FatDllHash.Value}) on client '{me.GetPlayerName()}' world '{world}' at ({at.x:0}, {at.z:0}) r={radius:0}: 2 steps, ~12 s");
                var inv = CultureInfo.InvariantCulture;

                if (!global::VerdantsAscent.Water.Core.FiresWaveDepthMatch.HasFtsSampler)
                {
                    skip++;
                    Say($"{Tag} 1/2 wavematch SKIP - FiresTossinShade's FiresShoreField.TrySampleWave is not available here (FTS missing, older than "
                        + "the sampler, or its field is off / not baked around you): nothing to compare the physics wave with");
                    yield break;
                }

                float time = WrappedTime();
                var rows = new List<(Vector3 p, float phys, float shader, float vanillaDepth, float r)>();
                int outside = 0;
                for (int iz = 0; iz < Points; iz++)
                    for (int ix = 0; ix < Points; ix++)
                    {
                        float fx = -radius + 2f * radius * ix / (Points - 1), fz = -radius + 2f * radius * iz / (Points - 1);
                        var p = new Vector3(at.x + fx, ZoneSystem.c_WaterLevel, at.z + fz);
                        WaterVolume v = null;
                        Floating.GetWaterLevel(p, ref v);
                        if (v == null || Mathf.Abs(v.transform.position.y - ZoneSystem.c_WaterLevel) > 1f) { outside++; continue; }
                        if (!global::VerdantsAscent.Water.Core.FiresWaveDepthMatch.TrySampleFts(p.x, p.z, out float rr)) { outside++; continue; }
                        float phys = v.GetWaterSurface(p);
                        // The shader's height ([visual], FiresWater BuildOutput): CalcWave(R, waveFactor 1) x _WaveAmplitude, with NO
                        // Deep North fade on the swell (waveFactorBig 1); tide/river and whirl offsets are reported apart.
                        float amp = global::VerdantsAscent.Water.Core.FiresWaveAmplitudeMatch.AmplitudeOf(v);
                        // 0.2.868: no tide here (0.2.867 added Galaxies' raw SurfaceOffset, but its physics takes it x
                        // TideRiverField.TideInfluence: -4.85 vs -0.49 at the shore); the shared shift is reported apart.
                        float shader = v.transform.position.y + SurfaceOffset(v) +(rr == 0f ? 0f : v.CalcWave(p, rr, time, 1f, 1f) * amp);
                        float vd = VanillaDepth(v, p);
                        rows.Add((p, phys, shader, vd, rr));
                    }
                // 0.2.868: the WAVE is judged; a shift shared by every point (the tide, Galaxies' to match) is taken out and named.
                var deltas = rows.Select(w => w.phys - w.shader).OrderBy(d => d).ToList();
                float shift = deltas.Count > 0 ? deltas[deltas.Count / 2] : 0f;
                rows = rows.Select(w => (w.p, w.phys - shift, w.shader, w.vanillaDepth, w.r)).ToList();
                Say(string.Format(inv, "{0} tide shift {1:0.000} m taken out (physics minus drawn, median); Galaxies tide {2:0.000} m (_FiresTideOffset {3:0.000})",
                    Tag, shift, global::VerdantsAscent.Water.Core.FiresWaveDepthMatch.Tide(), Shader.GetGlobalFloat("_FiresTideOffset")));
                var worst = rows.OrderByDescending(w => Mathf.Abs(w.phys - w.shader)).ToList();
                float max = worst.Count > 0 ? Mathf.Abs(worst[0].phys - worst[0].shader) : 0f;
                bool ok = rows.Count >= MinPoints && max <= Tolerance;
                if (ok) pass++; else fail++;
                string list = string.Join(" | ", worst.Take(ListWorst).Select(w => string.Format(inv,
                    "({0:0},{1:0}) physics {2:0.000} vs drawn {3:0.000} (Δ {4:0.000}; shore R {5:0.00}, vanilla corner depth {6:0.00})",
                    w.p.x, w.p.z, w.phys, w.shader, w.phys - w.shader, w.r, w.vanillaDepth)));
                Say($"{Tag} 1/2 wavematch {(ok ? "PASS" : "FAIL")} - " + string.Format(inv,
                    "{0} sea point(s) compared ({1} outside a sea volume or the shore field), worst Δ {2:0.000} m (tolerance {3:0.00}, needs {4}+ points); worst: {5}",
                    rows.Count, outside, max, Tolerance, MinPoints, rows.Count > 0 ? list : "none"));
                // 2 server (0.2.699, [lead]: server-owned floaters near coasts must match too): the DEDICATED SERVER's depth
                // factor at the same points (its WaterVolume.Depth, FAT's own evaluation of the shore field there) against the
                // client's drawn R. A depth gap dR moves the surface by at most dR x the full-strength wave there, which is
                // what is judged (same tolerance).
                if (rows.Count == 0) { skip++; Say($"{Tag} 2/2 server SKIP - no sea point to ask the server about"); }
                else
                {
                    float[] server = null;
                    bool answered = false;
                    global::VerdantsAscent.Water.Core.FiresWaveDepthMatch.ServerDepths(rows.Select(w => new Vector2(w.p.x, w.p.z)).ToArray(),
                        d => { server = d; answered = true; });
                    float until = Time.realtimeSinceStartup + ServerWaitSeconds;
                    while (!answered && Time.realtimeSinceStartup < until) yield return null;
                    if (!answered || server == null || server.Length != rows.Count)
                    { fail++; Say($"{Tag} 2/2 server FAIL - no answer from the server within {ServerWaitSeconds:0} s (FAT older than 0.2.699 on the dedi?)"); }
                    else
                    {
                        var diffs = new List<(Vector3 p, float client, float srv, float height)>();
                        int noVolume = 0;
                        for (int i = 0; i < rows.Count; i++)
                        {
                            if (server[i] < 0f) { noVolume++; continue; }
                            var row = rows[i];
                            WaterVolume v = null;
                            Floating.GetWaterLevel(row.p, ref v);
                            float full = v != null ? Mathf.Abs(v.CalcWave(row.p, 1f, time, 1f, 1f) * global::VerdantsAscent.Water.Core.FiresWaveAmplitudeMatch.AmplitudeOf(v)) : 0f;
                            diffs.Add((row.p, row.r, server[i], Mathf.Abs(server[i] - row.r) * full));
                        }
                        var sw = diffs.OrderByDescending(w => w.height).ToList();
                        float smax = sw.Count > 0 ? sw[0].height : 0f;
                        bool sok = sw.Count >= MinPoints && smax <= Tolerance;
                        if (sok) pass++; else fail++;
                        // 0.2.872 ([lead] 10-09, a 0.151 m gap with client R 0 vs server 0.18-0.23): both evaluate the same field
                        // from the same ground, so a gap is the tide each side bakes it with; name both.
                        Say(string.Format(inv, "{0} tides: client {1:0.000} m, server {2} (the shore field's depth is sea + tide - ground on each side)",
                            Tag, global::VerdantsAscent.Water.Core.FiresWaveDepthMatch.Tide(),
                            float.IsNaN(global::VerdantsAscent.Water.Core.FiresWaveDepthMatch.LastServerTide) ? "not sent (older FAT)"
                                : global::VerdantsAscent.Water.Core.FiresWaveDepthMatch.LastServerTide.ToString("0.000", inv) + " m"));
                        string slist = string.Join(" | ", sw.Take(ListWorst).Select(w => string.Format(inv,
                            "({0:0},{1:0}) client R {2:0.000} vs server {3:0.000} → {4:0.000} m", w.p.x, w.p.z, w.client, w.srv, w.height)));
                        Say($"{Tag} 2/2 server {(sok ? "PASS" : "FAIL")} - " + string.Format(inv,
                            "{0} point(s) compared with the server's depth factor ({1} with no sea volume on the server), worst surface gap {2:0.000} m (tolerance {3:0.00}); worst: {4}",
                            sw.Count, noVolume, smax, Tolerance, sw.Count > 0 ? slist : "none"));
                    }
                }

                Vector3 eye = at + Vector3.up * ShotLift;
                Say($"{Tag} SHOT " + "{pose " + string.Format(inv, "{0:0.0},{1:0.0},{2:0.0},{3:0},5", eye.x, eye.y, eye.z, me.transform.eulerAngles.y) + "} the sea where the swim wave was compared");
                yield return new WaitForSecondsRealtime(ShotHoldSeconds);
            }
            finally
            {
                Say($"{Tag} END {(fail > 0 ? "FAIL" : "PASS")}: {pass} pass, {fail} fail, {skip} skip in {Time.realtimeSinceStartup - started:0} s");
                s_running = false;
            }
        }

        // Vanilla's own corner blend (WaterVolume.Depth before FAT's postfix), for the report: what physics used to read.
        private static readonly AccessTools.FieldRef<WaterVolume, float[]> NormalizedDepth = AccessTools.FieldRefAccess<WaterVolume, float[]>("m_normalizedDepth");
        private static readonly AccessTools.FieldRef<WaterVolume, Collider> VolumeCollider = AccessTools.FieldRefAccess<WaterVolume, Collider>("m_collider");

        private static float VanillaDepth(WaterVolume v, Vector3 point)
        {
            var d = NormalizedDepth(v);
            var c = VolumeCollider(v);
            if (d == null || d.Length < 4 || c == null) return -1f;
            Vector3 local = v.transform.InverseTransformPoint(point);
            Vector3 size = c.bounds.size;
            float t1 = (local.x + size.x / 2f) / size.x, t2 = (local.z + size.z / 2f) / size.z;
            return Mathf.Lerp(Mathf.Lerp(d[3], d[2], t1), Mathf.Lerp(d[0], d[1], t1), t2);
        }

        // The log line, the console, and BepInEx\FiresTests\wavematch_test.txt, which FDT's runner also reads.
        private static void Say(string line)
        {
            global::VerdantsAscent.FiresAdminTerrain.Log?.LogMessage(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, Command + ".txt"),
                    $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{System.Environment.NewLine}");
            }
            catch (System.Exception) { }
        }
    }
}
