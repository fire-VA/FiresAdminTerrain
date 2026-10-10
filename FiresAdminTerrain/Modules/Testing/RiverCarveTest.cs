using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Water.Core;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Terrain.Testing
{
    // ── `va_pool <x> <z> [radius] | end` and `river_carve_test new|existing <ax> <az> <bx> <bz> [radius]` (0.2.673) ────────
    // Fire 10-01: "a terrain tool … click on two different water sources and automatically carve and fill a river between
    // them. We will want to test that." The tool is the Vpool hammer tool; its click is WaterShapingTools.PoolClick, which
    // va_pool calls at the solid ground of (x, z), so a drill or a test drives exactly what a click does, minus the cursor:
    //   a click on dry ground levels, carves and fills a pool; a click ON water (generated, a FAT pond, a lake, the sea)
    //   adopts that water; a second click connects the two with a graded, filled channel.
    // va_pool is admin-only and changes the world like the tool (voxel anchor ZDOs + WaterDisk anchors).
    // river_carve_test is an on-demand F5 contract test (without arguments every step SKIPs). It goes to the midpoint, then:
    //   1 before   both ends' water (existing: both must be water; new: reported)
    //   2 tool     two PoolClicks, A then B (existing: both must adopt water)
    //   3 channel  a 2 m depth profile A→B: no dry sample, every depth >= MinChannelDepth, every surface between the two
    //              ends' levels (no floating water, no sea-level water)
    //   4 ends     both ends still hold water (existing: at their old level)
    //   5 inland   the channel's midpoint counts as inland water: the clutter ecology grows against it, a fish entry may
    //              spawn there, and swimmers and floats read the same surface
    internal static class RiverCarveTest
    {
        private const string PoolCommand = "va_pool", Command = "river_carve_test", Tag = "[RiverCarveTest]";
        private const string Pass = "PASS", Fail = "FAIL", Skip = "SKIP";
        private const int Steps = 5;
        private const float EstimateSeconds = 45f, DefaultRadius = 4f;
        private const float SampleStep = 2f, MinChannelDepth = 0.5f, WaterMinDepth = 0.3f, LevelSlack = 0.3f;
        private const float ArriveGiveUpSeconds = 60f, ArriveSettleSeconds = 5f, CarveSettleSeconds = 6f, ClickGapSeconds = 1f;
        private const float ShotLift = 2f, ShotRadius = 12f, ShotHoldSeconds = 1f;
        private const int ProfileEntries = 40;
        private static bool s_registered, s_running;
        private static int s_step, s_passes, s_fails, s_skips;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(PoolCommand,
                "<x> <z> [radius=4] | end - a Vpool click at the solid ground of (x, z): pool, or adopt the water there; the next one connects (admin)",
                args =>
                {
                    void Say(string line) { FiresLogger.LogInfo(line); args.Context.AddString(line); }
                    if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) { Say($"[FiresWater] {PoolCommand} refused: not an admin"); return; }
                    if (args.Args.Length == 2 && args.Args[1] == "end") { WaterShapingTools.ClearAnchor(); return; }
                    var inv = CultureInfo.InvariantCulture;
                    float r = DefaultRadius;
                    if (args.Args.Length < 3 || !float.TryParse(args.Args[1], NumberStyles.Float, inv, out float x)
                        || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out float z)
                        || args.Args.Length > 3 && !float.TryParse(args.Args[3], NumberStyles.Float, inv, out r))
                    { Say($"[FiresWater] usage: {PoolCommand} <x> <z> [radius] | end"); return; }
                    if (!Click(x, z, r, out string why)) Say($"[FiresWater] {PoolCommand} refused: {why}");
                });
            new Terminal.ConsoleCommand(Command,
                "[F5 test] the Vpool river tool: two clicks (new pools, or two existing waters), then the channel's depth profile, "
                + "the ends' water and the channel as inland water. Args: new|existing <ax> <az> <bx> <bz> [radius=4] (on demand; without them every step SKIPs).",
                args => Start(args.Args.Skip(1).ToArray()));
        }

        // The tool's click at the solid ground of (x, z), as the crosshair would hit it.
        private static bool Click(float x, float z, float radius, out string why)
        {
            why = null;
            if (!FatWaterChecks.SolidGround(x, z, out float ground)) { why = $"no solid ground at ({x:0}, {z:0}) (zone not loaded?)"; return false; }
            WaterShapingTools.PoolClick(new Vector3(x, ground, z), radius);
            return true;
        }

        private static void Start(string[] args)
        {
            if (s_running) { Say($"{Tag} a run is already going."); return; }
            if (ZNet.instance == null || Player.m_localPlayer == null || ZoneSystem.instance == null) { Say($"{Tag} join a world first."); return; }
            if (args.Length == 0) { ZNet.instance.StartCoroutine(SkipAll()); return; }
            var inv = CultureInfo.InvariantCulture;
            var f = new float[5];
            f[4] = DefaultRadius;
            bool mode = args[0] == "new" || args[0] == "existing";
            bool ok = mode && args.Length >= 5;
            for (int i = 1; ok && i < args.Length && i <= 5; i++) ok = float.TryParse(args[i], NumberStyles.Float, inv, out f[i - 1]);
            if (!ok) { Say($"{Tag} usage: {Command} new|existing <ax> <az> <bx> <bz> [radius]; nothing started."); return; }
            ZNet.instance.StartCoroutine(Run(args[0] == "existing", new Vector2(f[0], f[1]), new Vector2(f[2], f[3]), Mathf.Clamp(f[4], 2f, 12f)));
        }

        private static IEnumerator SkipAll()
        {
            s_running = true;
            s_step = s_passes = s_fails = s_skips = 0;
            try
            {
                yield return Begin("no ends");
                foreach (string step in new[] { "before", "tool", "channel", "ends", "inland" })
                    Step(step, Skip, $"on demand: run {Command} new|existing <ax> <az> <bx> <bz> [radius]");
            }
            finally { End(Time.realtimeSinceStartup); }
        }

        private static IEnumerator Begin(string where)
        {
            float hashUntil = Time.realtimeSinceStartup + 5f;
            while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
            string world = ZNet.World != null ? ZNet.World.m_name : "?";
            const string mod = global::VerdantsAscent.FiresAdminTerrain.PluginName, version = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
            Say($"{Tag} BEGIN {mod} {version} ({FatDllHash.Value}) on client '{Player.m_localPlayer.GetPlayerName()}' world '{world}' {where}: "
                + $"{Steps} steps, ~{EstimateSeconds:0} s");
        }

        private static IEnumerator Run(bool existing, Vector2 a, Vector2 b, float radius)
        {
            s_running = true;
            s_step = s_passes = s_fails = s_skips = 0;
            float started = Time.realtimeSinceStartup;
            try
            {
                yield return Begin($"{(existing ? "existing" : "new")} ({a.x:0}, {a.y:0}) -> ({b.x:0}, {b.y:0}) r={radius:0.#}");
                Vector2 mid = (a + b) * 0.5f;
                var player = Player.m_localPlayer;
                FatWaterChecks.SolidGround(mid.x, mid.y, out float midGround);
                player.TeleportTo(new Vector3(mid.x + 6f, Mathf.Max(midGround, 30f) + 3f, mid.y), player.transform.rotation, true);
                float giveUp = Time.realtimeSinceStartup + ArriveGiveUpSeconds;
                while (Time.realtimeSinceStartup < giveUp && (Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting()
                    || !ZoneSystem.instance.IsZoneLoaded(new Vector3(a.x, 0f, a.y)) || !ZoneSystem.instance.IsZoneLoaded(new Vector3(b.x, 0f, b.y))))
                    yield return null;
                yield return new WaitForSecondsRealtime(ArriveSettleSeconds);

                // 1 before
                bool aWet = FatWaterChecks.TryWaterAt(a.x, a.y, WaterMinDepth, true, out float aLevel, out float aBed);
                bool bWet = FatWaterChecks.TryWaterAt(b.x, b.y, WaterMinDepth, true, out float bLevel, out float bBed);
                string ends = $"A ({a.x:0}, {a.y:0}) {Describe(aWet, aLevel, aBed)}; B ({b.x:0}, {b.y:0}) {Describe(bWet, bLevel, bBed)}";
                if (existing && !(aWet && bWet))
                {
                    Step("before", Fail, $"existing mode needs water at both ends: {ends}");
                    foreach (string s in new[] { "tool", "channel", "ends", "inland" }) Step(s, Skip, "no water to connect");
                    yield break;
                }
                Step("before", Pass, ends);
                yield return Shot(new Vector3(mid.x, Mathf.Max(midGround, 30f), mid.y), "before the river");

                // 2 tool
                WaterShapingTools.ClearAnchor();
                if (!Click(a.x, a.y, radius, out string whyA)) { Step("tool", Fail, "click A: " + whyA); SkipRest(); yield break; }
                bool adoptedA = WaterShapingTools.LastClickAdopted;
                WaterShapingTools.TryGetAnchor(out Vector3 anchorA, out _);
                yield return new WaitForSecondsRealtime(ClickGapSeconds);
                if (!Click(b.x, b.y, radius, out string whyB)) { Step("tool", Fail, "click B: " + whyB); SkipRest(); yield break; }
                bool adoptedB = WaterShapingTools.LastClickAdopted;
                WaterShapingTools.TryGetAnchor(out Vector3 anchorB, out _);
                WaterShapingTools.ClearAnchor();
                string clicks = $"A {(adoptedA ? "adopted water" : "new pool")} at y {anchorA.y:0.0}, B {(adoptedB ? "adopted water" : "new pool")} at y {anchorB.y:0.0}";
                Step("tool", !existing || (adoptedA && adoptedB) ? Pass : Fail, existing && !(adoptedA && adoptedB) ? $"{clicks}: existing mode must adopt both" : clicks);
                yield return new WaitForSecondsRealtime(CarveSettleSeconds);

                // 3 channel
                float lo = Mathf.Min(anchorA.y, anchorB.y) - LevelSlack, hi = Mathf.Max(anchorA.y, anchorB.y) + LevelSlack;
                float span = Vector2.Distance(a, b);
                int samples = Mathf.Max(2, Mathf.CeilToInt(span / SampleStep));
                var profile = new List<string>();
                int dry = 0, shallow = 0, outside = 0;
                float minDepth = float.MaxValue;
                for (int i = 0; i <= samples; i++)
                {
                    float t = i / (float)samples;
                    Vector2 p = Vector2.Lerp(a, b, t);
                    float d = t * span;
                    if (!FatWaterChecks.TryWaterAt(p.x, p.y, 0f, true, out float s, out float bed)) { dry++; profile.Add($"{d:0}m dry"); continue; }
                    float depth = s - bed;
                    minDepth = Mathf.Min(minDepth, depth);
                    if (depth < MinChannelDepth) shallow++;
                    if (s < lo || s > hi) outside++;
                    profile.Add($"{d:0}m {s:0.0}/{depth:0.0}");
                }
                string shown = profile.Count <= ProfileEntries ? string.Join(", ", profile)
                    : string.Join(", ", profile.Where((_, i) => i % Mathf.CeilToInt(profile.Count / (float)ProfileEntries) == 0));
                bool channelOk = dry == 0 && shallow == 0 && outside == 0;
                Step("channel", channelOk ? Pass : Fail, $"{samples + 1} samples over {span:0} m (surface/depth): {shown}; "
                    + $"{dry} dry, {shallow} shallower than {MinChannelDepth:0.0} m, {outside} outside the ends' levels {lo:0.0}..{hi:0.0}"
                    + (minDepth < float.MaxValue ? $", min depth {minDepth:0.0} m" : ""));

                // 4 ends
                bool aAfter = FatWaterChecks.TryWaterAt(a.x, a.y, WaterMinDepth, true, out float aLevel2, out float aBed2);
                bool bAfter = FatWaterChecks.TryWaterAt(b.x, b.y, WaterMinDepth, true, out float bLevel2, out float bBed2);
                bool kept = aAfter && bAfter && (!existing || Mathf.Abs(aLevel2 - aLevel) <= LevelSlack && Mathf.Abs(bLevel2 - bLevel) <= LevelSlack);
                Step("ends", kept ? Pass : Fail, $"A {Describe(aAfter, aLevel2, aBed2)}, B {Describe(bAfter, bLevel2, bBed2)}"
                    + (existing ? $" (before: A {aLevel:0.0}, B {bLevel:0.0})" : ""));

                // 5 inland
                string clutter = FishCheckCommand.ClutterLine(mid.x, mid.y, out bool grows);
                var system = FishCheckCommand.NearestSystem(new Vector3(mid.x, 0f, mid.y));
                var verdicts = system != null ? FishCheckCommand.Judge(system, mid.x, mid.y) : new List<FishCheckCommand.Verdict>();
                int fish = verdicts.Count(v => v.Spawnable);
                bool midWet = FatWaterChecks.TryWaterAt(mid.x, mid.y, 0f, true, out float midLevel, out float midBed);
                float felt = midWet ? FatWaterChecks.SurfaceAt(new Vector3(mid.x, midBed + 0.1f, mid.y)) : -10000f;
                bool swim = midWet && Mathf.Abs(felt - midLevel) <= LevelSlack;
                Step("inland", grows && fish > 0 && swim ? Pass : Fail,
                    $"midpoint ({mid.x:0}, {mid.y:0}): {clutter} Fish entries that may spawn: {fish} of {verdicts.Count}"
                    + (verdicts.Count > 0 ? " (" + string.Join(" | ", verdicts.Select(v => $"{v.Entry.m_name}: {v.Reason}")) + ")" : "")
                    + $"; swimmers/floats read {(felt > -9999f ? felt.ToString("0.0") : "no water")} against the channel's {(midWet ? midLevel.ToString("0.0") : "none")}");
                yield return Shot(new Vector3(mid.x, midWet ? midLevel : Mathf.Max(midGround, 30f), mid.y), "after the river");
            }
            finally { End(started); }
        }

        private static string Describe(bool wet, float level, float bed)
            => wet ? $"water at {level:0.0}, {level - bed:0.0} m deep" : "dry";

        private static void SkipRest()
        {
            while (s_step < Steps) Step(new[] { "before", "tool", "channel", "ends", "inland" }[s_step], Skip, "the tool did not run");
        }

        private static void End(float started)
        {
            Say($"{Tag} END {(s_fails > 0 ? Fail : Pass)}: {s_passes} pass, {s_fails} fail, {s_skips} skip in {Time.realtimeSinceStartup - started:0} s");
            s_running = false;
        }

        private static void Step(string name, string result, string detail)
        {
            s_step++;
            if (result == Pass) s_passes++; else if (result == Fail) s_fails++; else s_skips++;
            Say($"{Tag} {s_step}/{Steps} {name} {result} - {detail}");
        }

        private static IEnumerator Shot(Vector3 at, string caption)
        {
            Vector3 p = at + Vector3.up * ShotLift;
            Say($"{Tag} SHOT " + "{" + string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0},{3:0.0}", p.x, p.y, p.z, ShotRadius) + "} " + caption);
            yield return new WaitForSecondsRealtime(ShotHoldSeconds);
        }

        // The log line, the console, and BepInEx\FiresTests\river_carve_test.txt, which FDT's runner also reads.
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
