using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── `va_homestead_scan [radius=800] [top=5] [x z]`: where to build Fire's homestead test base (10-01: smelter, kiln,
    // forge, workbench, crops, archery target, a fishing pond, copper and iron nearby, pickables) ─────────────────────────
    // Read-only and admin-only, like va_site_scan, which it extends. Candidates on a 10 m grid round the centre (default: where
    // the admin stands), each needing:
    //   yard     a 30 x 30 m square of Meadows, relief at most MaxRelief, its lowest point above the water level
    //   pond     generated water (GenWaterField: the surface every water reader uses) at least PondDepth deep within PondReach
    //   forest   Black Forest within ForestReach (copper deposits, blueberries, thistle, carrot seeds and mushrooms grow there)
    // and ranked on flatness, pond distance, how many of 25 points within ResourceReach a vanilla copper rule and a pickable
    // rule allow (biome, altitude, forest factor, as ZoneSystem.PlaceVegetation tests them: where they CAN grow, not where each
    // one stands), and distance from the centre. The nearest Swamp is reported for iron: vanilla grows no iron in Meadows or
    // Black Forest (scrap piles are Swamp vegetation), so iron near the yard has to be placed.
    // Locations (camps, spawners, dungeons) live on the server and are NOT judged. One line per spot:
    //   [FiresHomesteadScan] #k yard (x, z) …, pond (x, z) surface S depth D at R m, Black Forest R m, copper k/25, pickables k/25 (kinds), swamp R m
    internal static class HomesteadScanCommand
    {
        private const string Command = "va_homestead_scan";
        private const string Tag = "[FiresHomesteadScan]";
        private const float Grid = 10f, Half = 15f, Sample = 3f, CentreKeepOut = 40f;
        private const float MaxRelief = 2.5f, MinAboveWater = 1f;
        private const float PondDepth = 1.5f, PondReach = 40f, PondStep = 4f;
        // The radius of the pool the homestead build lays (va_pool r 6), for the lily / vass share.
        private const float PondProbe = 6f;
        private static string Pct(float share) => share < 0f ? "n/a" : (share * 100f).ToString("0", CultureInfo.InvariantCulture) + " %";
        private const float ForestReach = 60f, ResourceReach = 60f, SwampReach = 600f;
        private const float SeaSheetGate = 30.3f;
        private const float MinSpacing = 80f;
        private const int Directions = 24;

        internal static void Register()
        {
            new Terminal.ConsoleCommand(Command, "[radius=800] [top=5] [x z] - rank homestead test-base sites: flat Meadows yard, a pond >= 1.5 m, Black Forest, copper and pickables near (admin, read-only)",
                args =>
                {
                    void Say(string line) { FiresLogger.LogInfo(line); args.Context.AddString(line); }
                    if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) { Say($"{Tag} refused: not an admin"); return; }
                    var gen = WorldGenerator.instance;
                    var zs = ZoneSystem.instance;
                    if (gen == null || zs == null) { Say($"{Tag} refused: no world generator"); return; }
                    if (!GenWaterField.SurfaceReady)
                    {
                        Say($"{Tag} refused: the generated water is not queryable yet (hydrology still solving, or hydrology off on this world). "
                            + "Wait for '[FiresVoxel] hydrology READY' and run it again.");
                        return;
                    }
                    var inv = CultureInfo.InvariantCulture;
                    var style = NumberStyles.Float;
                    float radius = 800f; int top = 5;
                    if (args.Args.Length > 1) float.TryParse(args.Args[1], style, inv, out radius);
                    if (args.Args.Length > 2) int.TryParse(args.Args[2], out top);
                    Vector3 centre = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    if (args.Args.Length > 4 && float.TryParse(args.Args[3], style, inv, out float cx) && float.TryParse(args.Args[4], style, inv, out float cz))
                        centre = new Vector3(cx, 0f, cz);
                    radius = Mathf.Clamp(radius, 100f, 2000f);
                    top = Mathf.Clamp(top, 1, 15);
                    foreach (string line in Scan(gen, zs, centre, radius, top)) Say(line);
                });
            RegisterTest();
        }

        // ── `homestead_scan_test [radius] [top]`: the same scan as an on-demand F5 contract test, for the unattended chain
        // ([lead] 10-01: the runner only takes "[F5 test]" commands). Without arguments both steps SKIP, so a full
        // fires_test_all is harmless. Step 1 waits for the generated water to be queryable (the hydrology solve finishes
        // after the join); step 2 is the scan, its [FiresHomesteadScan] lines printed as the body.
        private const string TestCommand = "homestead_scan_test";
        private const string TestTag = "[HomesteadScanTest]";
        private const int TestSteps = 2;
        private const float WaterWaitSeconds = 180f;
        private static bool s_testRunning;

        private static void RegisterTest()
        {
            Terrain.FatDllHash.Start();
            new Terminal.ConsoleCommand(TestCommand,
                "[F5 test] rank homestead test-base sites around the tester (va_homestead_scan as a test). Args: <radius> [top] (on demand; without them every step SKIPs).",
                args =>
                {
                    if (s_testRunning) { TestSay($"{TestTag} a run is already going."); return; }
                    if (ZNet.instance == null || Player.m_localPlayer == null) { TestSay($"{TestTag} join a world first."); return; }
                    float radius = 0f; int top = 5;
                    var inv = CultureInfo.InvariantCulture;
                    if (args.Args.Length > 1 && !float.TryParse(args.Args[1], NumberStyles.Float, inv, out radius))
                    { TestSay($"{TestTag} usage: {TestCommand} <radius> [top]; nothing started."); return; }
                    if (args.Args.Length > 2) int.TryParse(args.Args[2], out top);
                    ZNet.instance.StartCoroutine(RunTest(radius, top));
                });
        }

        private static System.Collections.IEnumerator RunTest(float radius, int top)
        {
            s_testRunning = true;
            int step = 0, passes = 0, fails = 0, skips = 0;
            float started = Time.realtimeSinceStartup;
            void Step(string name, string result, string detail)
            {
                step++;
                if (result == "PASS") passes++; else if (result == "FAIL") fails++; else skips++;
                TestSay($"{TestTag} {step}/{TestSteps} {name} {result} - {detail}");
            }
            try
            {
                float hashUntil = Time.realtimeSinceStartup + 5f;
                while (Terrain.FatDllHash.Value == Terrain.FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
                string world = ZNet.World != null ? ZNet.World.m_name : "?";
                const string mod = global::VerdantsAscent.FiresAdminTerrain.PluginName, version = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
                TestSay($"{TestTag} BEGIN {mod} {version} "
                    + $"({Terrain.FatDllHash.Value}) on client '{Player.m_localPlayer.GetPlayerName()}' world '{world}': {TestSteps} steps, ~{WaterWaitSeconds + 30f:0} s");
                if (radius <= 0f)
                {
                    Step("water", "SKIP", $"on demand: run {TestCommand} <radius> [top]");
                    Step("scan", "SKIP", $"on demand: run {TestCommand} <radius> [top]");
                    yield break;
                }
                float until = Time.realtimeSinceStartup + WaterWaitSeconds;
                while (!GenWaterField.SurfaceReady && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(1f);
                if (!GenWaterField.SurfaceReady)
                {
                    Step("water", "FAIL", $"the generated water is not queryable after {WaterWaitSeconds:0} s (hydrology off on this world, or still solving)");
                    Step("scan", "SKIP", "no generated water to judge ponds against");
                    yield break;
                }
                Step("water", "PASS", $"generated water queryable after {Time.realtimeSinceStartup - started:0} s");
                var gen = WorldGenerator.instance;
                Vector3 centre = Player.m_localPlayer.transform.position;
                var lines = Scan(gen, ZoneSystem.instance, centre, Mathf.Clamp(radius, 100f, 2000f), Mathf.Clamp(top, 1, 15));
                foreach (string line in lines) TestSay(line);
                int sites = lines.Count - 1;
                Step("scan", sites > 0 ? "PASS" : "FAIL", sites > 0
                    ? $"{sites} homestead site(s) ranked around ({centre.x:0}, {centre.z:0}); #1 is the first [FiresHomesteadScan] # line"
                    : $"no site within {radius:0} m of ({centre.x:0}, {centre.z:0}) has a flat Meadows yard, Black Forest within {ForestReach:0} m and a pond >= {PondDepth:0.0} m");
            }
            finally
            {
                TestSay($"{TestTag} END {(fails > 0 ? "FAIL" : "PASS")}: {passes} pass, {fails} fail, {skips} skip in {Time.realtimeSinceStartup - started:0} s");
                s_testRunning = false;
            }
        }

        // The log line, the console, and BepInEx\FiresTests\homestead_scan_test.txt, which FDT's runner also reads.
        private static void TestSay(string line)
        {
            global::VerdantsAscent.FiresAdminTerrain.Log?.LogMessage(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, TestCommand + ".txt"),
                    $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{System.Environment.NewLine}");
            }
            catch (System.Exception) { }
        }

        private struct Spot
        {
            public float X, Z, Lo, Hi, Dist, Score;
            public float PondX, PondZ, PondLevel, PondDepth, PondDist, Forest, Swamp, Lily, Vass;
            public int Copper, Pickables;
        }

        private static List<string> Scan(WorldGenerator gen, ZoneSystem zs, Vector3 centre, float radius, int top)
        {
            float water = zs.m_waterLevel;
            var copper = new List<ZoneSystem.ZoneVegetation>();
            var pickables = new List<ZoneSystem.ZoneVegetation>();
            var kinds = new SortedSet<string>();
            foreach (var veg in zs.m_vegetation)
            {
                if (veg == null || !veg.m_enable || veg.m_max <= 0f || veg.m_prefab == null) continue;
                if ((veg.m_biome & (Heightmap.Biome.Meadows | Heightmap.Biome.BlackForest)) == 0) continue;
                string name = veg.m_prefab.name;
                if (name.IndexOf("copper", System.StringComparison.OrdinalIgnoreCase) >= 0) copper.Add(veg);
                else if (veg.m_prefab.GetComponent<Pickable>() != null) { pickables.Add(veg); kinds.Add(name); }
            }

            var spots = new List<Spot>();
            int judged = 0, flat = 0, withPond = 0;
            for (float z = centre.z - radius; z <= centre.z + radius; z += Grid)
                for (float x = centre.x - radius; x <= centre.x + radius; x += Grid)
                {
                    float dist = new Vector2(x - centre.x, z - centre.z).magnitude;
                    if (dist > radius || dist < CentreKeepOut) continue;
                    judged++;
                    if (!Is(gen, x, z, Heightmap.Biome.Meadows) || !Is(gen, x - Half, z - Half, Heightmap.Biome.Meadows)
                        || !Is(gen, x + Half, z - Half, Heightmap.Biome.Meadows) || !Is(gen, x - Half, z + Half, Heightmap.Biome.Meadows)
                        || !Is(gen, x + Half, z + Half, Heightmap.Biome.Meadows)) continue;
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (float dz = -Half; dz <= Half; dz += Sample)
                        for (float dx = -Half; dx <= Half; dx += Sample)
                        {
                            float h = gen.GetHeight(x + dx, z + dz);
                            lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h);
                        }
                    if (hi - lo > MaxRelief || lo < water + MinAboveWater) continue;
                    flat++;
                    float forest = Nearest(gen, x, z, ForestReach, Heightmap.Biome.BlackForest);
                    if (forest > ForestReach) continue;
                    if (!FindPond(x, z, out var s)) continue;
                    withPond++;
                    s.X = x; s.Z = z; s.Lo = lo; s.Hi = hi; s.Dist = dist; s.Forest = forest;
                    s.Copper = RulePoints(gen, copper, x, z, water);
                    s.Pickables = RulePoints(gen, pickables, x, z, water);
                    // 0.2.683 (Fire 10-01, lilies "Stay vanilla"): the share of a PondProbe-radius pool at the pond that passes
                    // vanilla's lily / vass position gates (FatWaterLife); -1 = no ClutterSystem here (a dedicated server).
                    s.Lily = global::VerdantsAscent.Water.Core.FatWaterLife.LilyFractalPass(s.PondX, s.PondZ, PondProbe);
                    s.Vass = global::VerdantsAscent.Water.Core.FatWaterLife.VassFractalPass(s.PondX, s.PondZ, PondProbe);
                    // Flat yard and a near pond first, then copper and pickables, then near the centre.
                    s.Score = (hi - lo) * 2f + s.PondDist / 10f + (25 - s.Copper) * 0.2f + (25 - s.Pickables) * 0.1f + dist / 200f
                        + (1f - Mathf.Max(0f, s.Lily)) * 3f;
                    spots.Add(s);
                }
            spots.Sort((a, b) => a.Score.CompareTo(b.Score));

            var lines = new List<string>();
            var picked = new List<Spot>();
            foreach (var s0 in spots)
            {
                if (picked.Count >= top) break;
                bool close = false;
                foreach (var p in picked) if (new Vector2(p.X - s0.X, p.Z - s0.Z).magnitude < MinSpacing) { close = true; break; }
                if (close) continue;
                var s = s0;
                s.Swamp = Nearest(gen, s.X, s.Z, SwampReach, Heightmap.Biome.Swamp);
                picked.Add(s);
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} #{1} yard ({2:0}, {3:0}) ground y {4:0.0}-{5:0.0} (relief {6:0.0} m), {7:0} m from the centre; pond ({8:0.0}, {9:0.0}) surface {10:0.0} depth {11:0.0} at {12:0} m; "
                    + "Black Forest {13:0} m; copper {14}/25 and pickables {15}/25 within {16:0} m; swamp (iron) {17}; vanilla lets lilies grow at {18} and vass at {19} of a {20:0} m pool there",
                    Tag, picked.Count, s.X, s.Z, s.Lo, s.Hi, s.Hi - s.Lo, s.Dist, s.PondX, s.PondZ, s.PondLevel, s.PondDepth, s.PondDist,
                    s.Forest, s.Copper, s.Pickables, ResourceReach, s.Swamp > SwampReach ? $"none within {SwampReach:0} m" : $"{s.Swamp:0} m",
                    Pct(s.Lily), Pct(s.Vass), PondProbe));
            }
            lines.Insert(0, string.Format(CultureInfo.InvariantCulture,
                "{0} centre ({1:0}, {2:0}), radius {3:0} m: {4} spot(s) judged, {5} flat dry Meadows yards, {6} with Black Forest within {7:0} m and a pond >= {8:0.0} m within {9:0} m; "
                + "top {10}. Rules: {11} copper, {12} pickable ({13}). Locations (camps, spawners, dungeons) are NOT checked here.",
                Tag, centre.x, centre.z, radius, judged, flat, withPond, ForestReach, PondDepth, PondReach, picked.Count,
                copper.Count, pickables.Count, string.Join(", ", kinds)));
            return lines;
        }

        private static bool Is(WorldGenerator gen, float x, float z, Heightmap.Biome biome) => gen.GetBiome(x, z) == biome;

        // The nearest point of the biome within reach (10 m rings, 24 directions), or MaxValue.
        private static float Nearest(WorldGenerator gen, float x, float z, float reach, Heightmap.Biome biome)
        {
            for (float r = 10f; r <= reach; r += 10f)
                for (int a = 0; a < Directions; a++)
                {
                    float ang = a * Mathf.PI * 2f / Directions;
                    if (gen.GetBiome(x + Mathf.Cos(ang) * r, z + Mathf.Sin(ang) * r) == biome) return r;
                }
            return float.MaxValue;
        }

        // The nearest generated water at least PondDepth deep within PondReach of the yard centre.
        private static bool FindPond(float x, float z, out Spot s)
        {
            s = default;
            for (float r = 0f; r <= PondReach; r += PondStep)
            {
                int dirs = r <= 0f ? 1 : Directions;
                for (int a = 0; a < dirs; a++)
                {
                    float ang = a * Mathf.PI * 2f / Directions;
                    float px = x + Mathf.Cos(ang) * r, pz = z + Mathf.Sin(ang) * r;
                    if (GenWaterField.TryWaterSurfaceSmooth(px, pz, out float level, out float depth) && level > SeaSheetGate && depth >= PondDepth)
                    {
                        s.PondX = px; s.PondZ = pz; s.PondLevel = level; s.PondDepth = depth; s.PondDist = r;
                        return true;
                    }
                }
            }
            return false;
        }

        // Of 25 points within ResourceReach (a 5 x 5 grid), how many at least one of the rules allows (biome, altitude, forest).
        private static int RulePoints(WorldGenerator gen, List<ZoneSystem.ZoneVegetation> rules, float x, float z, float water)
        {
            if (rules.Count == 0) return 0;
            int ok = 0;
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                {
                    float px = x - ResourceReach + i * ResourceReach / 2f, pz = z - ResourceReach + j * ResourceReach / 2f;
                    var biome = gen.GetBiome(px, pz);
                    float alt = gen.GetHeight(px, pz) - water;
                    var p = new Vector3(px, 0f, pz);
                    foreach (var veg in rules)
                    {
                        if ((veg.m_biome & biome) == 0 || alt < veg.m_minAltitude || alt > veg.m_maxAltitude) continue;
                        if (veg.m_inForest)
                        {
                            float f = WorldGenerator.GetForestFactor(p);
                            if (f < veg.m_forestTresholdMin || f > veg.m_forestTresholdMax) continue;
                        }
                        ok++;
                        break;
                    }
                }
            return ok;
        }
    }
}
