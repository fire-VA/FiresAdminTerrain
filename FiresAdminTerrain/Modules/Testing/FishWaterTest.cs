using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Testing
{
    // ── `fishwater_test <x> <z>`: the inland-water ecology contract ([lead] 10-01, for the unattended runner, which only
    // accepts "[F5 test]" commands). On demand: without x z every step SKIPs. Four steps, AdminToolsTest's lines:
    //   1 water   FAT inland water at least 1 m deep at the point (every source printed)
    //   2 clutter a grass patch centred there grows against that water, not vanilla's sea level
    //   3 gate    at least one fish entry passes the inland gate there (each entry's vanilla verdict and reason printed;
    //             "player within N m" counts as passing, because the tester stands near)
    //   4 spawn   one fish through vanilla's SpawnSystem.Spawn; PASS = a new live fish ZDO within SpawnReach (XZ) of the bed, in water
    // The same judging as va_fish_check / va_fish_spawn (FishCheckCommand). It spawns one fish and changes nothing else.
    internal static class FishWaterTest
    {
        private const string Command = "fishwater_test";
        private const string Tag = "[FishWaterTest]";
        private const string Pass = "PASS", Fail = "FAIL", Skip = "SKIP";
        private const string ShotWord = "SHOT";
        private const string ModName = global::VerdantsAscent.FiresAdminTerrain.PluginName;
        private const string ModVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
        private const int Steps = 4;
        private const float EstimateSeconds = 15f;
        // `near [radius]` (0.2.668): a find step first, then a teleport to the water it finds.
        private const string NearWord = "near";
        private const float NearRadius = 600f, MaxNearRadius = 2000f, FindStep = 4f, NearEstimateSeconds = 90f;
        private const int ProbesPerFrame = 1500;
        private const float ArriveGiveUpSeconds = 60f, ArriveSettleSeconds = 5f;
        private static int s_total = Steps;
        private const float HashWaitSeconds = 5f;
        private const float SpawnSettleSeconds = 2f;
        private const float SpawnReach = 8f;
        private const float ShotLift = 1.5f, ShotRadius = 6f, ShotHoldSeconds = 1f;

        private static bool s_registered, s_running;
        private static int s_step, s_passes, s_fails, s_skips;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                "[F5 test] FAT inland water ecology at a point: the water, its clutter, every fish "
                + "entry's spawn gate, and one fish spawned through vanilla's SpawnSystem. Args: <x> <z> | near [radius=600] "
                + "(finds the nearest inland water a fish entry fits and goes there). On demand: without them every step SKIPs.",
                args => Start(args.Args.Skip(1).ToArray()));
        }

        private static void Start(string[] args)
        {
            if (s_running) { Say($"{Tag} a run is already going."); return; }
            if (ZNet.instance == null || Player.m_localPlayer == null || ZoneSystem.instance == null) { Say($"{Tag} join a world first."); return; }
            // On demand only: a full fires_test_all calls every marker test without arguments, and this one would spawn a fish
            // wherever the tester stands, or FAIL on dry land. Without x z every step SKIPs ([lead] 10-01, until FDT lists it
            // in OnDemandTests).
            if (args.Length == 0) { ZNet.instance.StartCoroutine(SkipAll()); return; }
            var inv = CultureInfo.InvariantCulture;
            if (args[0] == NearWord)
            {
                float radius = NearRadius;
                if (args.Length > 1 && !float.TryParse(args[1], NumberStyles.Float, inv, out radius))
                { Say($"{Tag} usage: {Command} near [radius]; nothing started."); return; }
                ZNet.instance.StartCoroutine(Run(0f, 0f, Mathf.Clamp(radius, 50f, MaxNearRadius)));
                return;
            }
            if (args.Length < 2 || !float.TryParse(args[0], NumberStyles.Float, inv, out float x) || !float.TryParse(args[1], NumberStyles.Float, inv, out float z))
            { Say($"{Tag} usage: {Command} <x> <z> | near [radius]; nothing started."); return; }
            ZNet.instance.StartCoroutine(Run(x, z, 0f));
        }

        private sealed class Found
        {
            public bool Ok;
            public float X, Z, Level, Depth;
            public Heightmap.Biome Biome;
            public int Probed;
            public string Why;
        }

        // Rings outward from the tester, FindStep apart: the first inland water above the sea (FatWaterChecks.TryWaterAt:
        // generated water, or a tool pond and any other FAT water over solid ground) whose depth fits a fish entry's band
        // (altitude -max..-min below the surface) in that entry's biome. Tool water is found only in loaded zones.
        private static IEnumerator Find(Vector3 from, float radius, Found r)
        {
            var system = FishCheckCommand.NearestSystem(from);
            var bands = new List<(Heightmap.Biome biome, float min, float max)>();
            if (system != null)
                foreach (var list in system.m_spawnLists)
                    if (list != null)
                        foreach (var e in list.m_spawners)
                            if (e != null && e.m_enabled && InlandFishSpawns.IsFish(e)) bands.Add((e.m_biome, -e.m_maxAltitude, -e.m_minAltitude));
            if (bands.Count == 0) { r.Why = "no fish spawn entry loaded where the tester stands"; yield break; }
            var gen = WorldGenerator.instance;
            int sinceYield = 0;
            for (float rad = 0f; rad <= radius; rad += FindStep)
            {
                int points = Mathf.Max(1, Mathf.CeilToInt(2f * Mathf.PI * rad / FindStep));
                for (int k = 0; k < points; k++)
                {
                    float a = k * 2f * Mathf.PI / points;
                    float x = from.x + Mathf.Cos(a) * rad, z = from.z + Mathf.Sin(a) * rad;
                    r.Probed++;
                    if (FatWaterChecks.TryWaterAt(x, z, 0f, false, out float level, out float floor))
                    {
                        float depth = level - floor;
                        var biome = gen.GetBiome(x, z);
                        foreach (var b in bands)
                            if ((b.biome & biome) != 0 && depth >= b.min && depth <= b.max)
                            {
                                r.Ok = true; r.X = x; r.Z = z; r.Level = level; r.Depth = depth; r.Biome = biome;
                                yield break;
                            }
                    }
                    if (++sinceYield >= ProbesPerFrame) { sinceYield = 0; yield return null; }
                }
            }
            r.Why = $"no inland water a fish entry fits within {radius:0} m of ({from.x:0}, {from.z:0}) ({r.Probed} points probed)";
        }

        private static IEnumerator SkipAll()
        {
            s_running = true;
            s_step = s_passes = s_fails = s_skips = 0;
            s_total = Steps;
            try
            {
                yield return Begin("no point");
                foreach (string step in new[] { "water", "clutter", "gate", "spawn" })
                    Step(step, Skip, $"on demand: run {Command} <x> <z>");
            }
            finally
            {
                Say($"{Tag} END {Pass}: {s_passes} pass, {s_fails} fail, {s_skips} skip in 0 s");
                s_running = false;
            }
        }

        private static IEnumerator Begin(string where)
        {
            float hashUntil = Time.realtimeSinceStartup + HashWaitSeconds;
            while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
            string world = ZNet.World != null ? ZNet.World.m_name : "?";
            Say($"{Tag} BEGIN {ModName} {ModVersion} ({FatDllHash.Value}) on client '{Player.m_localPlayer.GetPlayerName()}' world '{world}' "
                + $"{where}: {s_total} steps, ~{(s_total > Steps ? NearEstimateSeconds : EstimateSeconds):0} s");
        }

        // nearRadius > 0: find the water first (x, z unused), then go there.
        private static IEnumerator Run(float x, float z, float nearRadius)
        {
            s_running = true;
            s_step = s_passes = s_fails = s_skips = 0;
            s_total = nearRadius > 0f ? Steps + 1 : Steps;
            float started = Time.realtimeSinceStartup;
            try
            {
                if (nearRadius > 0f)
                {
                    Vector3 from = Player.m_localPlayer.transform.position;
                    yield return Begin($"near ({from.x:0}, {from.z:0}) within {nearRadius:0} m");
                    var site = new Found();
                    yield return Find(from, nearRadius, site);
                    if (!site.Ok)
                    {
                        Step("find", Fail, site.Why);
                        foreach (string step in new[] { "water", "clutter", "gate", "spawn" }) Step(step, Skip, "no water found");
                        yield break;
                    }
                    Step("find", Pass, $"inland water at ({site.X:0.0}, {site.Z:0.0}), {Utils.DistanceXZ(from, new Vector3(site.X, 0f, site.Z)):0} m away: "
                        + $"surface {site.Level:0.0}, depth {site.Depth:0.0}, biome {site.Biome} ({site.Probed} points probed); going there");
                    x = site.X; z = site.Z;
                    var player = Player.m_localPlayer;
                    player.TeleportTo(new Vector3(x, site.Level + 1f, z), player.transform.rotation, true);
                    float giveUp = Time.realtimeSinceStartup + ArriveGiveUpSeconds;
                    while (Time.realtimeSinceStartup < giveUp && (Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting()
                        || !ZoneSystem.instance.IsZoneLoaded(new Vector3(x, 0f, z)))) yield return null;
                    yield return new WaitForSecondsRealtime(ArriveSettleSeconds);
                }
                else yield return Begin($"at ({x:0}, {z:0})");

                string water = FishCheckCommand.WaterLine(x, z, out bool wet);
                Step("water", wet ? Pass : Fail, water);

                string clutter = FishCheckCommand.ClutterLine(x, z, out bool grows);
                Step("clutter", grows ? Pass : Fail, clutter);
                yield return Shot(new Vector3(x, Player.m_localPlayer.transform.position.y, z), "inland water and its clutter");

                var system = FishCheckCommand.NearestSystem(new Vector3(x, 0f, z));
                if (system == null)
                {
                    Step("gate", Fail, "no SpawnSystem loaded at the point (the tester must stand within a loaded zone)");
                    Step("spawn", Skip, "no gate passed");
                    yield break;
                }
                var verdicts = FishCheckCommand.Judge(system, x, z);
                var spawnable = verdicts.Where(v => v.Spawnable).ToList();
                string each = verdicts.Count == 0 ? "no fish spawn entry in the lists" : string.Join(" | ", verdicts.Select(FishCheckCommand.Describe));
                Step("gate", spawnable.Count > 0 ? Pass : Fail, $"{spawnable.Count} of {verdicts.Count} fish entr(ies) can spawn here: {each}");
                if (spawnable.Count == 0) { Step("spawn", Skip, "no fish entry passes the gate here"); yield break; }

                var chosen = spawnable[0];
                var before = new HashSet<Fish>(Object.FindObjectsOfType<Fish>());
                FishCheckCommand.Spawn(system, chosen);
                yield return new WaitForSecondsRealtime(SpawnSettleSeconds);
                Vector3 bed = chosen.SpawnAt;
                Fish found = null;
                float nearest = float.MaxValue;
                foreach (var fish in Object.FindObjectsOfType<Fish>())
                {
                    if (fish == null || before.Contains(fish)) continue;
                    var nview = fish.GetComponent<ZNetView>();
                    if (nview == null || !nview.IsValid()) continue;
                    float d = Utils.DistanceXZ(fish.transform.position, bed);
                    if (d < nearest) { nearest = d; found = fish; }
                }
                if (found == null)
                    Step("spawn", Fail, $"{chosen.Entry.m_name}: no new live fish after {SpawnSettleSeconds:0} s");
                else
                {
                    // A spawned fish swims off at once (R32: 3.0 m in the 2 s wait), so the reach allows for it; it must
                    // also be IN the water, which is the point of the test.
                    Vector3 f = found.transform.position;
                    bool inWater = !found.IsOutOfWater();
                    string what = $"new fish {Utils.GetPrefabName(found.gameObject)} ZDO {found.GetComponent<ZNetView>().GetZDO().m_uid} "
                        + $"at ({f.x:0.0}, {f.y:0.0}, {f.z:0.0}), {nearest:0.0} m (XZ) from the bed ({bed.x:0.0}, {bed.y:0.0}, {bed.z:0.0}), "
                        + $"in water {(inWater ? "yes" : "NO")}";
                    if (nearest <= SpawnReach && inWater)
                    {
                        Step("spawn", Pass, $"{chosen.Entry.m_name} spawned through SpawnSystem.Spawn"
                            + (chosen.Good ? "" : $" (set aside: {chosen.Reason})") + $"; {what}");
                        yield return Shot(f, "the spawned fish");
                    }
                    else
                        Step("spawn", Fail, $"{chosen.Entry.m_name}: {what}"
                            + (nearest > SpawnReach ? $"; more than {SpawnReach:0} m from the bed" : "; not in the water"));
                }
            }
            finally
            {
                Say($"{Tag} END {(s_fails > 0 ? Fail : Pass)}: {s_passes} pass, {s_fails} fail, {s_skips} skip in {Time.realtimeSinceStartup - started:0} s");
                s_running = false;
            }
        }

        private static void Step(string name, string result, string detail)
        {
            s_step++;
            if (result == Pass) s_passes++;
            else if (result == Fail) s_fails++;
            else s_skips++;
            Say($"{Tag} {s_step}/{s_total} {name} {result} - {detail}");
        }

        private static IEnumerator Shot(Vector3 at, string caption)
        {
            Vector3 p = at + Vector3.up * ShotLift;
            Say($"{Tag} {ShotWord} " + "{" + string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0},{3:0.0}", p.x, p.y, p.z, ShotRadius) + "} " + caption);
            yield return new WaitForSecondsRealtime(ShotHoldSeconds);
        }

        // The log line, the console, and BepInEx\FiresTests\fishwater_test.txt, which FDT's runner also reads.
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
