using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Testing
{
    // ── `waterlife_test <x> <z> [radius=25]`: what actually lives in and around a pond (Fire 10-01, watching the stream: "it's
    // hard to tell … whether there are actually any fish, grass or lily pads spawning around this water") ────────────────
    // Read-only, on-demand F5 contract test (without arguments every step SKIPs); it changes nothing. Within radius of the
    // point, on this client:
    //   1 water       the water there (FatWaterChecks: generated field, tool ponds, every FAT and vanilla water)
    //   2 fish        live Fish objects: prefab, in water or not
    //   3 clutter     vanilla grass clutter INSTANCES (InstanceRenderer) and non-instanced clutter objects by prefab, the
    //                 water-edge ones flagged (lily, vass, reed, rush, cattail, seaweed, swamp), and what the FAT clutter
    //                 ecology does for a patch centred there
    //   4 vegetation  placed objects (ZNetView) by prefab, the water-edge ones flagged
    // Steps 2-4 PASS when at least one of their kind is there. Then a SHOT of the water. Clutter exists only around the
    // camera (m_distance), so the tester should stand within that of the point (the test does not move them).
    internal static class WaterLifeTest
    {
        private const string Command = "waterlife_test", Tag = "[WaterLifeTest]";
        private const string Pass = "PASS", Fail = "FAIL", Skip = "SKIP";
        private const int Steps = 4, ListTop = 12;
        private const float DefaultRadius = 25f, EstimateSeconds = 10f;
        private const float ShotLift = 15f, ShotHoldSeconds = 1f;
        private static readonly string[] WaterWords = { "lily", "lilies", "vass", "reed", "rush", "cattail", "seaweed", "swamp", "water" };
        private static readonly AccessTools.FieldRef<InstanceRenderer, Matrix4x4[]> Instances = AccessTools.FieldRefAccess<InstanceRenderer, Matrix4x4[]>("m_instances");
        private static readonly AccessTools.FieldRef<InstanceRenderer, int> InstanceCount = AccessTools.FieldRefAccess<InstanceRenderer, int>("m_instanceCount");
        private static readonly AccessTools.FieldRef<ClutterSystem, GameObject> GrassRoot = AccessTools.FieldRefAccess<ClutterSystem, GameObject>("m_grassRoot");
        private static bool s_registered, s_running;
        private static int s_step, s_passes, s_fails, s_skips;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                "[F5 test] [no relaunch] read-only census of a pond: its water, live fish, grass clutter (lilies, vass, reeds …) and placed "
                + "vegetation within radius, then a SHOT. Args: <x> <z> [radius=25] (on demand; without them every step SKIPs).",
                args => Start(args.Args.Skip(1).ToArray()));
        }

        private static void Start(string[] args)
        {
            if (s_running) { Say($"{Tag} a run is already going."); return; }
            if (ZNet.instance == null || Player.m_localPlayer == null) { Say($"{Tag} join a world first."); return; }
            var inv = CultureInfo.InvariantCulture;
            if (args.Length == 0) { ZNet.instance.StartCoroutine(Run(0f, 0f, 0f)); return; }
            float r = DefaultRadius;
            if (args.Length < 2 || !float.TryParse(args[0], NumberStyles.Float, inv, out float x) || !float.TryParse(args[1], NumberStyles.Float, inv, out float z)
                || args.Length > 2 && !float.TryParse(args[2], NumberStyles.Float, inv, out r))
            { Say($"{Tag} usage: {Command} <x> <z> [radius]; nothing started."); return; }
            ZNet.instance.StartCoroutine(Run(x, z, Mathf.Clamp(r, 2f, 100f)));
        }

        private static IEnumerator Run(float x, float z, float radius)
        {
            s_running = true;
            s_step = s_passes = s_fails = s_skips = 0;
            float started = Time.realtimeSinceStartup;
            try
            {
                float hashUntil = Time.realtimeSinceStartup + 5f;
                while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
                string world = ZNet.World != null ? ZNet.World.m_name : "?";
                const string mod = global::VerdantsAscent.FiresAdminTerrain.PluginName, version = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
                Say($"{Tag} BEGIN {mod} {version} ({FatDllHash.Value}) on client '{Player.m_localPlayer.GetPlayerName()}' world '{world}' "
                    + (radius > 0f ? $"at ({x:0}, {z:0}) r={radius:0}" : "no point") + $": {Steps} steps, ~{EstimateSeconds:0} s");
                if (radius <= 0f)
                {
                    foreach (string s in new[] { "water", "fish", "clutter", "vegetation" }) Step(s, Skip, $"on demand: run {Command} <x> <z> [radius]");
                    yield break;
                }
                var at = new Vector3(x, 0f, z);
                float fromTester = Utils.DistanceXZ(Player.m_localPlayer.transform.position, at);

                // 1 water
                string water = FishCheckCommand.WaterLine(x, z, out bool wet);
                bool anyWater = FatWaterChecks.TryWaterAt(x, z, 0f, true, out float surface, out _);
                Step("water", anyWater ? Pass : Fail, water + (wet ? "" : anyWater ? $"; (water at {surface:0.0}, shallower than 1 m or the sea)" : ""));

                // 2 fish
                var fish = Object.FindObjectsOfType<Fish>().Where(f => f != null && Utils.DistanceXZ(f.transform.position, at) <= radius).ToList();
                string fishList = string.Join(", ", fish.GroupBy(f => Utils.GetPrefabName(f.gameObject))
                    .Select(g => $"{g.Key} x{g.Count()} ({g.Count(f => !f.IsOutOfWater())} in water)"));
                Step("fish", fish.Count > 0 ? Pass : Fail, $"{fish.Count} live fish within {radius:0} m" + (fish.Count > 0 ? ": " + fishList : ""));
                // 0.2.680 (H8 "Fish2 x1 (0 in water)"): each fish, where it is, the liquid level it was last given (Fish.m_inWater,
                // -10000 = never set) against vanilla's IsOutOfWater test (Fish.cs:222-225: level < y - m_height), and FAT's surface there.
                foreach (var f in fish.Take(ListTop))
                {
                    Vector3 fp = f.transform.position;
                    float given = FishInWater(f), fat = FatWaterChecks.SurfaceAt(fp + Vector3.up * 0.1f);
                    Say($"{Tag}   {Utils.GetPrefabName(f.gameObject)} at ({fp.x:0.0}, {fp.y:0.00}, {fp.z:0.0}): given level "
                        + (given <= -9999f ? "never set" : given.ToString("0.00", CultureInfo.InvariantCulture))
                        + $", out of water when that is under y - height ({fp.y - f.m_height:0.00}) → {(f.IsOutOfWater() ? "OUT of water" : "in water")}; "
                        + $"FAT surface here {(fat > -9999f ? fat.ToString("0.00", CultureInfo.InvariantCulture) : "none")}, "
                        + $"owner {(f.GetComponent<ZNetView>()?.IsOwner() == true ? "this client" : "another peer")}");
                }
                // How vanilla spawns them (SpawnSystem.UpdateSpawnList / FindBaseSpawnPoint): per entry one attempt every
                // m_spawnInterval s, m_spawnChance %, none while m_maxSpawned are near, at 20 random points m_spawnRadiusMin..Max
                // (default 40..80 m) from a random player. A pond nearer than that to every player is never tried.
                var system = FishCheckCommand.NearestSystem(at);
                if (system != null)
                {
                    var entries = system.m_spawnLists.Where(l => l != null).SelectMany(l => l.m_spawners)
                        .Where(e => e != null && e.m_enabled && InlandFishSpawns.IsFish(e)).GroupBy(e => e.m_name).Select(g => g.First()).ToList();
                    Say($"{Tag}   fish spawn rules ({entries.Count}): " + string.Join(" | ", entries.Select(e => string.Format(CultureInfo.InvariantCulture,
                        "{0}: every {1:0} s at {2:0}%, max {3} near, group {4}-{5}, {6:0}-{7:0} m from a player, biome {8}, alt {9:0.##}..{10:0.##}{11}",
                        e.m_name, e.m_spawnInterval, e.m_spawnChance, e.m_maxSpawned, e.m_groupSizeMin, e.m_groupSizeMax,
                        e.m_spawnRadiusMin > 0f ? e.m_spawnRadiusMin : 40f, e.m_spawnRadiusMax > 0f ? e.m_spawnRadiusMax : 80f, e.m_biome,
                        e.m_minAltitude, e.m_maxAltitude, e.m_spawnAtDay && e.m_spawnAtNight ? "" : e.m_spawnAtDay ? ", day only" : ", night only"))));
                    Say($"{Tag}   the tester is {fromTester:0} m from the pond: vanilla tries spawn points 40-80 m (or each rule's own range) "
                        + $"from a player, so a pond {(fromTester < 40f ? "THIS CLOSE is never tried while the tester stands here" : fromTester > 80f ? "this far is outside the default ring" : "at this distance can be tried")}.");
                }

                // 3 clutter
                var clutter = new Dictionary<string, int>();
                var cs = ClutterSystem.instance;
                var root = cs != null ? GrassRoot(cs) : null;
                if (root != null)
                    foreach (Transform child in root.transform)
                    {
                        if (child == null) continue;
                        string name = child.name.Replace("(Clone)", "").Trim();
                        var ir = child.GetComponent<InstanceRenderer>();
                        int n = 0;
                        if (ir != null)
                        {
                            var m = Instances(ir);
                            int count = Mathf.Min(InstanceCount(ir), m?.Length ?? 0);
                            for (int i = 0; i < count; i++)
                            {
                                Vector3 p = m[i].GetColumn(3);
                                if (Utils.DistanceXZ(p, at) <= radius) n++;
                            }
                        }
                        else if (Utils.DistanceXZ(child.position, at) <= radius) n = 1;
                        if (n > 0) { clutter.TryGetValue(name, out int had); clutter[name] = had + n; }
                    }
                string ecology = FishCheckCommand.ClutterLine(x, z, out bool grows);
                int clutterWater = clutter.Where(kv => IsWaterEdge(kv.Key)).Sum(kv => kv.Value);
                Step("clutter", clutterWater > 0 ? Pass : Fail,
                    $"{clutter.Values.Sum()} clutter instance(s) within {radius:0} m ({clutterWater} water-edge){Listing(clutter)}; {ecology}"
                    + (cs == null ? " No ClutterSystem on this client." : fromTester > (cs?.m_distance ?? 0f) ? $" The tester is {fromTester:0} m away, beyond clutter range {cs.m_distance:0} m: walk closer and run again." : ""));
                // Which water clutter vanilla HAS, and where it may grow (0.2.675: R46 showed none; is there any for this biome?).
                if (cs != null)
                {
                    const float gap = VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.VanillaClutterGap;
                    Say($"{Tag}   ClutterSystem.m_waterLevel = {cs.m_waterLevel:0.00} now (the sea's, tide included); a patch over inland water "
                        + $"is built against surface - {gap:0.00} (vanilla's 30 vs 27 gap, no tide), so a bed d m under the surface has alt {gap:0.00} - d.");
                    var rules = cs.m_clutter.Where(c => c != null && c.m_prefab != null
                        && (c.m_snapToWater || c.m_minOceanDepth != c.m_maxOceanDepth || IsWaterEdge(c.m_prefab.name))).ToList();
                    // 0.2.680 (H8: vass x1, lilies 0): vanilla's own gates for each snapping rule, in GenerateVegPatch's order
                    // (ClutterSystem.cs:283-324), on a GateStep grid across the water within radius, against the level the FAT
                    // ecology moves the patch to (surface - gap). Each point counts under the FIRST gate that refuses it.
                    if (anyWater)
                        foreach (var c in rules.Where(r => r.m_snapToWater))
                            Say($"{Tag}   {c.m_prefab.name} gates over this water: {GateCensus(cs, c, at, radius, surface)}");
                    Say($"{Tag}   water clutter rules ({rules.Count}): " + (rules.Count == 0 ? "none"
                        : string.Join(" | ", rules.Select(c => string.Format(CultureInfo.InvariantCulture,
                            "{0}{1}: biome {2}, alt {3:0.##}..{4:0.##}, ocean depth {5:0.##}..{6:0.##}{7}{8}",
                            c.m_prefab.name, c.m_enabled ? "" : " (disabled)", c.m_biome, c.m_minAlt, c.m_maxAlt, c.m_minOceanDepth, c.m_maxOceanDepth,
                            c.m_snapToWater ? ", snaps to water" : "", c.m_inForest ? ", forest only" : "")))));
                }

                // 4 vegetation
                var placed = new Dictionary<string, int>();
                var drowned = new Dictionary<string, int>();
                var allowedIn = new Dictionary<string, int>();
                var otherIn = new Dictionary<string, int>();
                foreach (var nv in Object.FindObjectsOfType<ZNetView>())
                {
                    if (nv == null || !nv.IsValid() || nv.GetComponent<Character>() != null || nv.GetComponent<Fish>() != null) continue;
                    Vector3 bp = nv.transform.position;
                    if (Utils.DistanceXZ(bp, at) > radius) continue;
                    string name = Utils.GetPrefabName(nv.gameObject);
                    placed.TryGetValue(name, out int had);
                    placed[name] = had + 1;
                    // 0.2.686 (Fire 10-01: only stumps, logs and rocks under the water): any base under the surface by more
                    // than the drowned sweep's tolerance, read at the base as the sweep reads it, sorted by the same rule
                    // (FatUnderWaterPlacement): refused kinds FAIL the step, allowed kinds and other objects are listed.
                    if (IsWaterAnchor(name) || FatWaterChecks.SurfaceAt(bp + Vector3.up * 0.1f) <= bp.y + global::VerdantsAscent.Terrain.Voxel.FatUnderWaterPlacement.SurfaceTolerance) continue;
                    var bucket = global::VerdantsAscent.Terrain.Voxel.FatUnderWaterPlacement.IsRefusedInstance(nv.gameObject) ? drowned
                        : global::VerdantsAscent.Terrain.Voxel.FatUnderWaterPlacement.IsAllowedUnderWater(nv.gameObject) ? allowedIn : otherIn;
                    bucket.TryGetValue(name, out int d);
                    bucket[name] = d + 1;
                }
                int placedWater = placed.Where(kv => IsWaterEdge(kv.Key)).Sum(kv => kv.Value);
                // 0.2.680 ([lead] H8): water-edge life counts from clutter OR placed objects (vass is clutter, never placed).
                // 0.2.686: the step PASSes unless a tree, Tree-type destructible, plant, pickable, bush or sapling stands in the
                // water; water-edge life is reported, steps 2 and 3 judge it.
                int edge = placedWater + clutterWater, inWater = drowned.Values.Sum();
                Step("vegetation", inWater == 0 ? Pass : Fail,
                    $"{inWater} tree/bush/plant/pickable(s) standing IN the water (base under the surface){Listing(drowned)}; "
                    + $"allowed in the water (rocks, logs, stumps, water plants): {allowedIn.Values.Sum()}{Listing(allowedIn)}; "
                    + $"other objects in it: {otherIn.Values.Sum()}{Listing(otherIn)}; "
                    + $"{edge} water-edge plant(s) within {radius:0} m ({clutterWater} clutter, {placedWater} placed); "
                    + $"{placed.Values.Sum()} placed object(s){Listing(placed)}");

                Vector3 shot = new Vector3(x, (anyWater ? surface : Player.m_localPlayer.transform.position.y) + ShotLift, z);
                Say($"{Tag} SHOT " + "{" + string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0},{3:0.0}", shot.x, shot.y, shot.z, radius) + "} the pond and its edge");
                yield return new WaitForSecondsRealtime(ShotHoldSeconds);
            }
            finally
            {
                Say($"{Tag} END {(s_fails > 0 ? Fail : Pass)}: {s_passes} pass, {s_fails} fail, {s_skips} skip in {Time.realtimeSinceStartup - started:0} s");
                s_running = false;
            }
        }

        private const float GateStep = 1f;

        // Vanilla GenerateVegPatch's per-instance gates (forest, fractal, ground+biome, altitude+tilt, ocean depth, vegetation
        // mask, cleared paint) at every water point of a grid, with altitude measured against surface - gap as the FAT
        // ecology sets it. Returns "N water point(s): K pass all; refused first by <gate> xM, …".
        private static string GateCensus(ClutterSystem cs, ClutterSystem.Clutter c, Vector3 at, float radius, float surface)
        {
            float level = surface - VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.VanillaClutterGap;
            float cosMax = Mathf.Cos(Mathf.Deg2Rad * c.m_maxTilt), cosMin = Mathf.Cos(Mathf.Deg2Rad * c.m_minTilt);
            var refused = new Dictionary<string, int>();
            int points = 0, passed = 0;
            void Refuse(string gate) { refused.TryGetValue(gate, out int n); refused[gate] = n + 1; }
            for (float dz = -radius; dz <= radius; dz += GateStep)
                for (float dx = -radius; dx <= radius; dx += GateStep)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    var p = new Vector3(at.x + dx, 0f, at.z + dz);
                    if (!FatWaterChecks.TryWaterAt(p.x, p.z, 0.05f, false, out _, out _)) continue;
                    points++;
                    if (c.m_inForest)
                    {
                        float ff = WorldGenerator.GetForestFactor(p);
                        if (ff < c.m_forestTresholdMin || ff > c.m_forestTresholdMax) { Refuse("forest factor"); continue; }
                    }
                    if (c.m_fractalScale > 0f)
                    {
                        float fb = Utils.Fbm(p * 0.01f * c.m_fractalScale + new Vector3(c.m_fractalOffset, 0f, 0f), 3, 1.6f, 0.7f);
                        if (fb < c.m_fractalTresholdMin || fb > c.m_fractalTresholdMax) { Refuse("fractal noise"); continue; }
                    }
                    if (!cs.GetGroundInfo(p, out Vector3 point, out Vector3 normal, out Heightmap hmap, out Heightmap.Biome biome)) { Refuse("no ground"); continue; }
                    if ((c.m_biome & biome) == Heightmap.Biome.None) { Refuse($"biome {biome}"); continue; }
                    float alt = point.y - level;
                    if (alt < c.m_minAlt || alt > c.m_maxAlt) { Refuse(alt < c.m_minAlt ? "altitude (too deep)" : "altitude (too shallow)"); continue; }
                    if (normal.y < cosMax || normal.y > cosMin) { Refuse("tilt"); continue; }
                    // Vanilla calls hmap below unguarded; on ground without a Heightmap that patch would throw.
                    if (hmap == null && (c.m_minOceanDepth != c.m_maxOceanDepth || c.m_minVegetation != c.m_maxVegetation || !c.m_onCleared || !c.m_onUncleared))
                    { Refuse("no heightmap"); continue; }
                    if (c.m_minOceanDepth != c.m_maxOceanDepth)
                    {
                        float od = hmap.GetOceanDepth(p);
                        if (od < c.m_minOceanDepth || od > c.m_maxOceanDepth) { Refuse(string.Format(CultureInfo.InvariantCulture, "zone ocean depth {0:0.0}", od)); continue; }
                    }
                    if (c.m_minVegetation != c.m_maxVegetation)
                    {
                        float vm = hmap.GetVegetationMask(point);
                        if (vm > c.m_maxVegetation || vm < c.m_minVegetation) { Refuse("vegetation mask"); continue; }
                    }
                    if (!c.m_onCleared || !c.m_onUncleared)
                    {
                        bool cleared = hmap.IsCleared(point);
                        if (c.m_onCleared && !cleared || c.m_onUncleared && cleared) { Refuse(cleared ? "cleared paint" : "uncleared"); continue; }
                    }
                    passed++;
                }
            return $"{points} water point(s) on a {GateStep:0} m grid: {passed} pass every gate"
                + (refused.Count > 0 ? "; refused first by " + string.Join(", ", refused.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")) : "")
                + $" (density {c.m_amount} per patch, so a point that passes still grows one only where the patch's random sample lands)";
        }

        private static readonly AccessTools.FieldRef<Fish, float> FishInWater = AccessTools.FieldRefAccess<Fish, float>("m_inWater");
        // VoxelZoneStreamer's drowned-sweep keep list, plus VFX, FAT's own voxel objects and its water anchors (CustomRiver) and zone controls.
        private static readonly string[] AnchorWords = { "rock", "stone", "cliff", "mine", "lily", "lilies", "vass", "reed", "cattail", "seaweed" };

        private static bool IsWaterAnchor(string name)
        {
            string n = name.ToLowerInvariant();
            return n.StartsWith("vfx_") || n.StartsWith("sfx_") || n.StartsWith("vavoxel") || n == "customriver" || n == "_zonectrl" || AnchorWords.Any(n.Contains);
        }

        private static bool IsWaterEdge(string name)
        {
            string n = name.ToLowerInvariant();
            return WaterWords.Any(w => n.Contains(w));
        }

        // ": name xN [water-edge], …" for the most common ListTop names.
        private static string Listing(Dictionary<string, int> counts)
            => counts.Count == 0 ? "" : ": " + string.Join(", ", counts.OrderByDescending(kv => IsWaterEdge(kv.Key)).ThenByDescending(kv => kv.Value).Take(ListTop)
                .Select(kv => $"{kv.Key} x{kv.Value}{(IsWaterEdge(kv.Key) ? " [water-edge]" : "")}")) + (counts.Count > ListTop ? $" (+{counts.Count - ListTop} more)" : "");

        private static void Step(string name, string result, string detail)
        {
            s_step++;
            if (result == Pass) s_passes++; else if (result == Fail) s_fails++; else s_skips++;
            Say($"{Tag} {s_step}/{Steps} {name} {result} - {detail}");
        }

        // The log line, the console, and BepInEx\FiresTests\waterlife_test.txt, which FDT's runner also reads.
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
