using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Persistence;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Testing
{
    // `admintools_test` (Tools\ADMINTOOLS_TEST.md), Fire 2026-09-28: every terrain tool, FAT's and vanilla's, applied on
    // voxel ground at fixed places, read back here, compared with what the test bot and the server read, checked again
    // after both walk away and come back (and after a server restart with `verify`), with its cost and a screenshot.
    // An F5 contract test (Tools\FIRES_F5_TESTS.md), on demand: it edits the world, so it runs on test worlds only unless
    // `anyworld`, and each run starts by resetting its own cells.
    internal static class AdminToolsTest
    {
        private const string Command = "admintools_test";
        private const string Tag = "[AdminToolsTest]";
        private const string ResultsFolder = "FiresTests";
        private const string ResultsFile = "admintools_test.txt";
        private const string ExpectFilePattern = "admintools_expect_{0}.txt";
        private const string Pass = "PASS", Fail = "FAIL", Skip = "SKIP";
        private const string ShotWord = "SHOT";
        private const string ModName = global::VerdantsAscent.FiresAdminTerrain.PluginName;
        private const string ModVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
        private const string TestWorldMark = "test";
        private const float HashWaitSeconds = 5f;
        // BEGIN's estimate, which FDT's runner doubles into its wait for END: pours and springs settle for up to 20 s.
        private const float SecondsPerCase = 6f;
        private const float ArriveSettleSeconds = 4f;
        private const float GroundWaitSeconds = 20f;
        private const float CaseSettleSeconds = 8f;
        private const float BeforeReadWait = 6f;
        private const float PeerSettleSeconds = 12f;
        private const float PeerRetrySeconds = 1f;
        private const float PeerLateSeconds = 30f;       // how long past the settle a lagging peer is still timed
        private const float ResetSettleSeconds = 3f;
        private const float AwayMetres = 300f;
        private const float AwayBearing = 90f;
        private const float AwaySeconds = 12f;
        private const float TeleportLift = 1.5f;
        private const float ResetRadius = 5.5f;
        // World Edit Commands' vanilla-terrain reset (the IH "Reset Terrain" tool); without it, the hoe levels the cell
        // back to the generator's height and the cultivator's grass resets its paint.
        private const string TerrainResetCommand = "terrain";
        // Each water, ice and snow tool's own undo, over a cell ([visual]'s WATER_TOOLS_REFERENCE.md): delete removes the
        // disks and the pour depth, two mops take what's left of a pour, and each ice and snow kind has its delete.
        private static readonly string[] WaterResetLines =
        {
            "va_brush_water_delete", "va_brush_water_mop", "va_brush_water_mop", "delete_ice", "delete_ice_land", "delete_land_snow",
        };
        private const float ResetHeight = 14f;
        private const float ShotLift = 1.5f;
        private const float ShotRadius = 6f;
        private const float ShotHoldSeconds = 1f;
        private const float ChangeRadius = 40f;
        private const double BytesPerKilobyte = 1024d;
        private const double MillisecondsPerSecond = 1000d;

        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        // FDT's runner ends a test that outlives its wait with `<command> stop` (Tools\FIRES_F5_TESTS.md).
        private const string StopArgument = "stop";

        private static bool s_registered;
        private static bool s_running;
        private static bool s_stop;
        private static int s_step, s_steps, s_passes, s_fails, s_skips;
        private static readonly Dictionary<long, int> s_peerErrors = new Dictionary<long, int>();

        // The tester's result for one case at one place: what to read again after unloading and after a restart.
        private sealed class Recorded
        {
            internal PlaceKind Place;
            internal string Case;
            internal List<ProbePoint> Points;
            internal float[] Values;
        }

        private sealed class Options
        {
            internal bool Find, Keep, AnyWorld, CleanOnly, Verify;
            internal Vector3? Near;
            // 'a' or 'b': both clients run at once at the same places, each its half of the cases (Fire, 2026-09-29: "run both
            // the player and the bot as bots"); '\0' = one tester runs them all.
            internal char Split;
            internal HashSet<PlaceKind> Places = new HashSet<PlaceKind>();
            internal HashSet<string> Families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // From FAT's one Terminal.InitTerminal hook (FatVanillaHooks).
        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                "[F5 test] every terrain tool (FAT tile brushes, voxel tools, vanilla hoe/cultivator/pickaxe, Infinity Hammer's "
                + "terrain line) on voxel ground at fixed places, compared with the bot and the server, after unload and restart. "
                + "Args: list | find [near <x> <z>] | at surface|cave|island | only tile|voxel|vanilla|ih | keep | clean | verify | anyworld.",
                args => Start(args.Context, args.Args.Skip(1).ToArray()));
        }

        private static void Start(Terminal console, string[] args)
        {
            var cases = AdminToolsCases.All();
            if (args.Contains("list"))
            {
                foreach (var place in Enum.GetValues(typeof(PlaceKind)))
                    foreach (var toolCase in cases) Say($"{Tag} {place.ToString().ToLowerInvariant()}/{toolCase.Name} ({toolCase.Family})");
                return;
            }
            if (args.Contains(StopArgument))
            {
                if (s_running) s_stop = true;
                Say(s_running ? $"{Tag} stopping: the remaining steps SKIP." : $"{Tag} no run to stop.");
                return;
            }
            if (s_running) { Say($"{Tag} a run is already going."); return; }
            string unknown = UnknownArgument(args);
            if (unknown != null) { Say($"{Tag} '{unknown}' is not an argument of {Command}; nothing started ('{Command} list' shows the steps)."); return; }
            if (ZNet.instance == null || Player.m_localPlayer == null) { Say($"{Tag} join a world first."); return; }
            var options = Parse(args);
            ZNet.instance.StartCoroutine(Run(options, cases));
        }

        private static readonly HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "find", "keep", "anyworld", "clean", "verify" };
        private static readonly HashSet<string> Families = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { AdminToolsCases.VoxelFamily, AdminToolsCases.VanillaFamily, AdminToolsCases.ToolsFamily, AdminToolsCases.InfinityFamily, AdminToolsWaterCases.WaterFamily };

        // Anything the parser would not use: a run that fell back to "everything" on a typo would edit the world for
        // many minutes, so an unknown word starts nothing.
        private static string UnknownArgument(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (Flags.Contains(arg)) continue;
                if (arg == "at" && i + 1 < args.Length && Enum.TryParse(args[i + 1], true, out PlaceKind _)) { i++; continue; }
                if (arg == "only" && i + 1 < args.Length && Families.Contains(args[i + 1])) { i++; continue; }
                if (arg == "split" && i + 1 < args.Length && (args[i + 1] == "a" || args[i + 1] == "b")) { i++; continue; }
                if (arg == "near" && i + 2 < args.Length
                    && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    && float.TryParse(args[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) { i += 2; continue; }
                return arg;
            }
            return null;
        }

        private static Options Parse(string[] args)
        {
            var options = new Options
            {
                Find = args.Contains("find"),
                Keep = args.Contains("keep"),
                AnyWorld = args.Contains("anyworld"),
                CleanOnly = args.Contains("clean"),
                Verify = args.Contains("verify"),
            };
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "at" && i + 1 < args.Length && Enum.TryParse(args[i + 1], true, out PlaceKind place)) options.Places.Add(place);
                if (args[i] == "only" && i + 1 < args.Length) options.Families.Add(args[i + 1]);
                if (args[i] == "split" && i + 1 < args.Length && args[i + 1].Length == 1) options.Split = args[i + 1][0];
                if (args[i] == "near" && i + 2 < args.Length
                    && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(args[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    options.Near = new Vector3(x, 0f, z);
            }
            if (options.Places.Count == 0)
                foreach (PlaceKind place in Enum.GetValues(typeof(PlaceKind))) options.Places.Add(place);
            return options;
        }

        private static IEnumerator Run(Options options, List<ToolCase> allCases)
        {
            s_running = true;
            s_stop = false;
            s_step = s_passes = s_fails = s_skips = 0;
            s_peerErrors.Clear();
            FatTestBot.Listen();
            var player = Player.m_localPlayer;
            var home = player.transform.position;
            var started = Time.realtimeSinceStartup;
            try
            {
                // The bot's tools run only when asked for (`only tools`): they need FDT 1.1.176 and take the bot's hands.
                var cases = allCases.Where(c => options.Families.Count == 0 ? c.Family != AdminToolsCases.ToolsFamily : options.Families.Contains(c.Family)).ToList();
                string world = ZNet.World != null ? ZNet.World.m_name : "?";
                var spots = AdminToolsPlaces.Load(world);
                string refusal = Refusal(options, world);
                int mine = cases.Where((c, i) => Mine(options, i, c)).Count();
                s_steps = refusal != null ? 1 : options.Find || options.Verify || options.CleanOnly ? options.Places.Count
                    : options.Places.Count * (mine + 1);
                float hashUntil = Time.realtimeSinceStartup + HashWaitSeconds;
                while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
                Say($"{Tag} BEGIN {ModName} {ModVersion} ({FatDllHash.Value}) on client '{player.GetPlayerName()}' world '{world}': "
                    + $"{s_steps} steps, ~{EstimateSeconds(options, cases.Count, refusal != null):0} s, perf");
                if (refusal != null)
                {
                    Step("run", Skip, refusal);
                    yield break;
                }

                // The tester only observes (teleports, edits, reads) and must not die doing it (Fire, 2026-09-29): caves and
                // islands have monsters. Guarded for the whole run; the previous state comes back in the finally.
                if (Player.m_localPlayer != null && !s_godBefore.HasValue)
                {
                    s_godBefore = Player.m_localPlayer.InGodMode();
                    Player.m_localPlayer.SetGodMode(true);
                    Record($"tester: observer (guarded: god mode on for the run, was {(s_godBefore.Value ? "on" : "off")})");
                }

                bool haveBot = false;
                s_split = options.Split;
                if (options.Split != '\0')
                    Record($"split {options.Split}: this client runs {mine} of {cases.Count} case(s) per place (the other tester runs split "
                           + $"{(options.Split == 'a' ? 'b' : 'a')} at the same places); no bot follow; the read-back asks every other peer");
                else
                {
                    yield return FatTestBot.Find();
                    haveBot = FatTestBot.LastReply != null;
                    Record(haveBot ? $"the bot answered 'bot follow' ({FatTestBot.BotUid})" : "no bot answered 'bot follow': bot comparisons SKIP");
                }

                // `find` only picks and saves the places (R69: it went on to run every case); run the cases separately.
                if (options.Find)
                {
                    var oldSpots = new Dictionary<PlaceKind, Vector3>(spots);
                    yield return FindPlaces(options, spots, cases.Count, options.Near ?? home);
                    AdminToolsPlaces.Save(world, spots);
                    // A new place has no expectations yet; the old place's would be read at the new one (R76 verify).
                    SaveExpect(world, new List<Recorded>(), spots.Keys.Where(p => !oldSpots.TryGetValue(p, out Vector3 was) || was != spots[p]));
                    yield break;
                }

                var recorded = options.Verify ? LoadExpect(world) : new List<Recorded>();
                // Places whose cells were reset this run: their old expectations are void even when every case SKIPs (R76: R74's
                // surface list outlived R75's clean and all-SKIP run, and verify read it against the cleaned ground).
                var reset = new HashSet<PlaceKind>();
                foreach (PlaceKind place in Enum.GetValues(typeof(PlaceKind)))
                {
                    if (!options.Places.Contains(place)) continue;
                    if (!spots.TryGetValue(place, out Vector3 spot))
                    {
                        int skipped = options.Verify || options.CleanOnly ? 1 : cases.Count + 1;
                        for (int i = 0; i < skipped; i++)
                            Step($"{Name(place)}/-", Skip, $"no {Name(place)} place for this world yet: run '{Command} find' first");
                        continue;
                    }
                    s_followBox = FollowBox(AdminToolsPlaces.Candidates(place, spot, cases.Count));
                    yield return Arrive(place, spot, haveBot);
                    if (options.Verify)
                    {
                        if (s_areaProblem != null) Step($"{Name(place)}/verify", Skip, s_areaProblem);
                        else yield return Verify(place, recorded.Where(r => r.Place == place).ToList(), haveBot);
                        continue;
                    }
                    List<Vector3> cells = new List<Vector3>();
                    if (s_areaProblem == null)
                    {
                        yield return UsableCells(place, spot, AdminToolsPlaces.Candidates(place, spot, cases.Count), usable => cells = usable);
                        yield return ResetCells(cells, cases);
                        reset.Add(place);
                    }
                    if (options.CleanOnly)
                    {
                        if (s_areaProblem != null) Step($"{Name(place)}/clean", Skip, s_areaProblem);
                        else Step($"{Name(place)}/clean", Pass, $"{cells.Count} cell(s) reset");
                        continue;
                    }
                    string arrival = s_areaProblem ?? AdminToolsPlaces.CheckArrival(place, player.transform.position);
                    var placeResults = new List<Recorded>();
                    bool stopRecorded = false;
                    for (int i = 0; i < cases.Count; i++)
                    {
                        // A case keeps its cell by its place in the shared list, so the two testers never edit the same cell.
                        if (!Mine(options, i, cases[i])) continue;
                        string name = $"{Name(place)}/{cases[i].Name}";
                        if (s_stop) { if (!stopRecorded) { Record($"{Name(place)}: stopped after case {i}/{cases.Count}"); stopRecorded = true; } Step(name, Skip, "stopped"); continue; }
                        if (arrival != null) { Step(name, Skip, arrival); continue; }
                        if (i >= cells.Count) { Step(name, Skip, $"no room: only {cells.Count} usable cell(s) at this place"); continue; }
                        // Vanilla's heightmap is the ground only on the surface: under an island it is the sea floor, over a cave
                        // the ground far above. R69's island ih_level raised it ~98 m, from the sea floor up to the island.
                        if (place != PlaceKind.Surface && VanillaGround(cases[i].Family))
                        { Step(name, Skip, $"vanilla-ground tool: its ground is not this {Name(place)}'s (surface only)"); continue; }
                        yield return RunCase(place, cases[i], cells[i], haveBot, placeResults);
                    }
                    if (s_stop || arrival != null) Step($"{Name(place)}/persist_unload", Skip, s_stop ? "stopped" : arrival);
                    else if (options.Split != '\0')
                        Step($"{Name(place)}/persist_unload", Skip, "split run: the other tester keeps this place loaded, so it can't unload; "
                             + "the verify after the next restart proves persistence");
                    else yield return PersistUnload(place, spot, placeResults, haveBot);
                    recorded.AddRange(placeResults);
                }
                if (!options.Verify) SaveExpect(world, recorded, reset);
            }
            finally
            {
                FatTestBot.VerifyFollowOff();
                if (s_split == '\0') FatTestBot.Follow();   // a split run never took the bot's follow
                s_split = '\0';
                FatTestBot.StopListening();
                if (Player.m_localPlayer != null) Player.m_localPlayer.TeleportTo(home, Player.m_localPlayer.transform.rotation, true);
                if (s_godBefore.HasValue)
                {
                    if (Player.m_localPlayer != null) Player.m_localPlayer.SetGodMode(s_godBefore.Value);
                    Record($"tester: guard lifted (god mode back {(s_godBefore.Value ? "on" : "off")})");
                    s_godBefore = null;
                }
                string verdict = s_fails > 0 ? Fail : Pass;
                Say($"{Tag} END {verdict}: {s_passes} pass, {s_fails} fail, {s_skips} skip in {Time.realtimeSinceStartup - started:0} s");
                s_running = false;
            }
        }

        private static string Refusal(Options options, string world)
        {
            if (!ZNet.instance.LocalPlayerIsAdminOrHost()) return "not admin: the tools edit the world";
            if (!options.AnyWorld && world.IndexOf(TestWorldMark, StringComparison.OrdinalIgnoreCase) < 0)
                return $"'{world}' is not a test world (add 'anyworld' to run anyway)";
            return null;
        }

        private static IEnumerator FindPlaces(Options options, Dictionary<PlaceKind, Vector3> spots, int cases, Vector3 near)
        {
            foreach (PlaceKind place in Enum.GetValues(typeof(PlaceKind)))
            {
                if (!options.Places.Contains(place)) continue;
                Vector3? found = null;
                string why = null;
                if (place == PlaceKind.Surface) yield return AdminToolsPlaces.FindSurface(near, cases, (surface, best) => { found = surface; why = best; });
                else if (place == PlaceKind.Island && AdminToolsPlaces.TryFindIsland(near, out Vector3 island)) found = island;
                else if (place == PlaceKind.Cave) yield return AdminToolsPlaces.FindCave(near, cases, (cave, best) => { found = cave; why = best; });
                if (found.HasValue)
                {
                    spots[place] = found.Value;
                    Step($"{Name(place)}/find", Pass, AdminToolsProbe.Where(found.Value));
                }
                else Step($"{Name(place)}/find", Fail, $"no {Name(place)} place near {AdminToolsProbe.Where(near)}{(why != null ? $" ({why})" : "")}");
            }
        }

        // ── A place ─────────────────────────────────────────────────────────────────────────────────────────────
        // A distant teleport lands on the ground at the spot's x,z (vanilla finds the floor from above): right for the
        // SURFACE; a cave floor or an island top is then reached with a second, local hop once the area has loaded.
        private static IEnumerator Arrive(PlaceKind place, Vector3 spot, bool haveBot)
        {
            var player = Player.m_localPlayer;
            yield return TeleportAccepted(spot + Vector3.up * TeleportLift, true);
            if (place != PlaceKind.Surface)
            {
                yield return new WaitForSecondsRealtime(ArriveSettleSeconds);
                yield return TeleportAccepted(spot + Vector3.up * TeleportLift, false);
            }
            if (haveBot) yield return PlaceBot(place, spot);
            yield return new WaitForSecondsRealtime(ArriveSettleSeconds);
            // Arrived for real: vanilla refuses a teleport within 2 s of the last one and says so only by returning false
            // (R74: the tester stayed 1.5 km away while the surface run read "nothing" and the bot waited at the place).
            var at = player != null ? player.transform.position : spot;
            float off = new Vector2(at.x - spot.x, at.z - spot.z).magnitude;
            if (off > ArrivedWithinMetres)
            {
                s_areaProblem = $"did not arrive: this client is at {AdminToolsProbe.Where(at)}, {off:0} m from the place";
                Record($"{Name(place)}: NOT at the place: at {AdminToolsProbe.Where(at)} for target ({spot.x:0.0},{spot.z:0.0}), {off:0.0} m off; bot: {FatTestBot.LastReply ?? "none"}");
                yield break;
            }
            // The place must be loaded HERE before anything is read: after a long teleport this client's zone, heightmap and
            // voxels can take longer than the settle. Past the cap the place is skipped rather than failed case by case.
            float waitFrom = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - waitFrom < AreaReadyCapSeconds && !AreaLoaded(spot) && !s_stop) yield return null;
            float waited = Time.realtimeSinceStartup - waitFrom;
            s_areaProblem = AreaLoaded(spot) ? null : $"the place is not loaded on this client after {AreaReadyCapSeconds:0} s (zone/heightmap/voxel ground)";
            Record($"{Name(place)}: at {AdminToolsProbe.Where(player.transform.position)} for target ({spot.x:0.0},{spot.z:0.0}), {off:0.0} m off; "
                   + $"area {(s_areaProblem == null ? "loaded" : "NOT loaded")} "
                   + $"{waited:0.0} s after the settle; bot: {FatTestBot.LastReply ?? "none"}");
        }

        // The tester's god mode before the run guarded it; null while no run holds the guard.
        private static bool? s_godBefore;
        private const float AreaReadyCapSeconds = 60f;
        private const float ArrivedWithinMetres = 5f;
        private const float TeleportRetrySeconds = 10f;
        private static string s_areaProblem;

        // Player.TeleportTo returns false while teleporting or within 2 s of the last teleport; retry until it is accepted
        // (or TeleportRetrySeconds pass), then wait for it to finish.
        private static IEnumerator TeleportAccepted(Vector3 target, bool distant)
        {
            var player = Player.m_localPlayer;
            float until = Time.realtimeSinceStartup + TeleportRetrySeconds;
            while (player != null && !player.TeleportTo(target, player.transform.rotation, distant) && Time.realtimeSinceStartup < until)
                yield return null;
            yield return null;
            while (player != null && player.IsTeleporting()) yield return null;
        }

        // This client has the spot's zone objects, its heightmap and settled voxel ground.
        private static bool AreaLoaded(Vector3 spot)
            => ZNetScene.instance != null && ZNetScene.instance.IsAreaReady(spot)
               && Heightmap.FindHeightmap(spot) != null
               && VerdantsAscent.Terrain.Voxel.FiresVoxelApi.IsGroundSettled(spot);

        // Near enough that the bot's client loads every cell, outside the cells themselves.
        private const float BotStandOff = 45f;
        // The case grid plus this margin is where a following bot never stands (brush radii and falloff reach ~4 m past a cell).
        private const float FollowBoxMargin = 4f;
        private static Rect s_followBox;

        private static Rect FollowBox(List<Vector3> cells)
        {
            if (cells == null || cells.Count == 0) return Rect.zero;
            float x0 = cells.Min(c => c.x), x1 = cells.Max(c => c.x), z0 = cells.Min(c => c.z), z1 = cells.Max(c => c.z);
            return Rect.MinMaxRect(x0 - FollowBoxMargin, z0 - FollowBoxMargin, x1 + FollowBoxMargin, z1 + FollowBoxMargin);
        }

        private static Heightmap.Biome BiomeAt(Vector3 spot)
            => WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(spot) : Heightmap.Biome.None;

        // A vanilla sword matched to each biome with predators; none for the Meadows (or no generator); iron elsewhere.
        private static string ObserverWeapon(Vector3 spot)
        {
            switch (BiomeAt(spot))
            {
                case Heightmap.Biome.BlackForest: return "SwordBronze";
                case Heightmap.Biome.Swamp:
                case Heightmap.Biome.Mountain: return "SwordIron";
                case Heightmap.Biome.Plains: return "SwordSilver";
                case Heightmap.Biome.Mistlands: return "SwordBlackmetal";
                case Heightmap.Biome.AshLands:
                case Heightmap.Biome.DeepNorth: return "SwordMistwalker";
                case Heightmap.Biome.Meadows:
                case Heightmap.Biome.None: return null;
                default: return "SwordIron";   // Ocean under a sky island (R77: a Hatchling there), anything else
            }
        }

        // The bot follows the tester outside the grid, so the stream sees it watching (Fire, R75); an FDT without
        // verify-follow (before 1.1.173) or a refusal parks it BotStandOff east of the place, as before.
        private static IEnumerator PlaceBot(PlaceKind place, Vector3 spot)
        {
            var player = Player.m_localPlayer;
            // The observer carries a weapon for the biome where it will stand: R77's mountain and island runs had it follow
            // unarmed while wolves and a Hatchling mauled it on stream. It is guarded, not tested; arming it keeps the reads alive.
            string weapon = ObserverWeapon(spot);
            if (weapon != null)
            {
                yield return FatTestBot.Equip(weapon);
                Record($"{Name(place)}: bot: armed for {BiomeAt(spot)} with {weapon}: {FatTestBot.LastReply ?? "no answer"}");
            }
            if (player != null && s_followBox.width > 0f)
            {
                yield return FatTestBot.VerifyFollow(player.GetPlayerName(), s_followBox);
                if (FatTestBot.Following)
                {
                    Record($"{Name(place)}: bot: following the tester at {FatTestBot.FollowMin:0}-{FatTestBot.FollowMax:0} m, outside the grid "
                           + $"({s_followBox.xMin:0},{s_followBox.yMin:0} to {s_followBox.xMax:0},{s_followBox.yMax:0}); {FatTestBot.LastReply}");
                    yield break;
                }
                Record($"{Name(place)}: bot: no verify-follow ({FatTestBot.LastReply ?? "no answer"}): parking it {BotStandOff:0} m east instead");
            }
            yield return FatTestBot.Goto(spot + Vector3.right * BotStandOff);
        }

        // The candidates that can hold a case, with this client's ground as y, once the place's voxels have wrapped.
        private static IEnumerator UsableCells(PlaceKind place, Vector3 spot, List<Vector3> candidates, Action<List<Vector3>> done)
        {
            float until = Time.realtimeSinceStartup + GroundWaitSeconds;
            while (Time.realtimeSinceStartup < until && candidates.Count > 0
                   && float.IsNaN(AdminToolsPlaces.CellGround(place, candidates[0], spot.y)))
                yield return null;
            var usable = new List<Vector3>(candidates.Count);
            foreach (var candidate in candidates)
            {
                float ground = AdminToolsPlaces.CellGround(place, candidate, spot.y);
                if (!float.IsNaN(ground)) usable.Add(new Vector3(candidate.x, ground, candidate.z));
            }
            Record($"{Name(place)}: {usable.Count} of {candidates.Count} candidate cell(s) usable");
            done(usable);
        }

        // Every family's own undo over each cell: tile clear, voxel restore to the generated ground, vanilla ground levelled
        // back to the generator's height and its paint reset.
        private static IEnumerator ResetCells(List<Vector3> cells, List<ToolCase> cases)
        {
            bool tile = cases.Any(c => c.Family == AdminToolsCases.TileFamily);
            bool voxel = cases.Any(c => c.Family == AdminToolsCases.VoxelFamily);
            bool vanilla = cases.Any(c => c.Family == AdminToolsCases.VanillaFamily || c.Family == AdminToolsCases.InfinityFamily);
            bool worldEdit = Terminal.commands.ContainsKey(TerrainResetCommand);
            bool water = cases.Any(c => c.Family == AdminToolsWaterCases.WaterFamily);
            foreach (var cell in cells)
            {
                if (water)
                    foreach (string line in WaterResetLines)
                        TryReset(() => AdminToolsCases.RunConsoleLine(string.Format(CultureInfo.InvariantCulture,
                            "{0} from={1:0.##},{2:0.##},{3:0.##} circle={4:0.##}", line, cell.x, cell.z, cell.y, ResetRadius)), cell);
                TryReset(() =>
                {
                    if (tile) TileBrushRPC.SendClear(cell, ResetRadius);
                    if (voxel) VoxelWorld.ApplyBrush(cell, ResetRadius, ResetHeight, VoxelBrushShape.Cylinder, VoxelBrushMode.Restore);
                    if (vanilla && worldEdit)
                        AdminToolsCases.RunConsoleLine(string.Format(CultureInfo.InvariantCulture, "{0} reset from={1:0.##},{2:0.##},{3:0.##} circle={4:0.##}",
                            TerrainResetCommand, cell.x, cell.z, cell.y, ResetRadius));
                    else if (vanilla)
                    {
                        float generated = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(cell.x, cell.z) : cell.y;
                        VanillaOps.Place(VanillaOps.HoeLevel(), new Vector3(cell.x, generated, cell.z));
                        VanillaOps.Place(VanillaOps.Replant(), new Vector3(cell.x, generated, cell.z));
                    }
                }, cell);
                yield return null;
            }
            yield return new WaitForSecondsRealtime(ResetSettleSeconds);
        }

        private static void TryReset(Action reset, Vector3 cell)
        {
            try { reset(); }
            catch (Exception ex) { Record($"reset at {AdminToolsProbe.Where(cell)} threw {ex.GetType().Name}: {ex.Message}"); }
        }

        // ── A case ──────────────────────────────────────────────────────────────────────────────────────────────
        private static IEnumerator RunCase(PlaceKind place, ToolCase toolCase, Vector3 cell, bool haveBot, List<Recorded> results)
        {
            string name = $"{Name(place)}/{toolCase.Name}";
            string missing = toolCase.Requires?.Invoke();
            if (missing != null)
            {
                Step(name, Skip, missing);
                yield break;
            }
            var points = toolCase.Points(cell);
            float[] before = AdminToolsProbe.ReadLocal(points);
            // 0.2.734: a voxel case's centre reading must exist BEFORE the tool runs. Run A 10-05 island/vcarve (the first
            // case after the teleport) read nothing before and 57.75 after, and failed "nothing to read": the column was
            // still loading. Wait for it a little; if it never comes, the case SKIPs instead of failing on a missing reading.
            if (toolCase.Family == AdminToolsCases.VoxelFamily && before.Length > 0 && float.IsNaN(before[0]))
            {
                float readUntil = Time.realtimeSinceStartup + BeforeReadWait;
                while (float.IsNaN(before[0]) && Time.realtimeSinceStartup < readUntil)
                {
                    yield return new WaitForSecondsRealtime(0.25f);
                    before = AdminToolsProbe.ReadLocal(points);
                }
                if (float.IsNaN(before[0]))
                {
                    Step(name, Skip, $"nothing to read at the centre before the tool ran ({AdminToolsProbe.Where(cell)}, waited {BeforeReadWait:0} s): the ground there is not loaded on this client");
                    yield break;
                }
            }
            int errorsBefore = AdminToolsProbe.LocalErrors;
            var zdosBefore = Revisions(cell);

            IEnumerator apply = null;
            string threw = null, absent = null;
            long mainThreadTicks = 0;
            try { apply = toolCase.Apply(cell); }
            catch (Exception ex) { threw = ex.Message; }
            while (apply != null)
            {
                bool more;
                long start = Stopwatch.GetTimestamp();
                try { more = apply.MoveNext(); }
                catch (MissingCommandException ex) { absent = ex.Message; more = false; }
                catch (Exception ex) { threw = ex.Message; more = false; }
                mainThreadTicks += Stopwatch.GetTimestamp() - start;
                if (!more) break;
                yield return apply.Current;
            }
            double applyMs = mainThreadTicks * MillisecondsPerSecond / Stopwatch.Frequency;
            if (absent != null)
            {
                Step(name, Skip, absent);
                yield break;
            }
            if (threw != null)
            {
                Step(name, Fail, $"the tool threw: {threw}");
                yield break;
            }

            float[] after = before;
            string verdict = null;
            float until = Time.realtimeSinceStartup + (toolCase.SettleSeconds > 0f ? toolCase.SettleSeconds : CaseSettleSeconds);
            do
            {
                yield return null;
                after = AdminToolsProbe.ReadLocal(points);
                verdict = toolCase.Check(cell, before, after);
            }
            while (verdict != null && Time.realtimeSinceStartup < until && !s_stop);

            // The peers get an edit later than this client: a voxel brush rides its chunk's anchor ZDO (a 0.75 s save debounce,
            // the sync, a 2 Hz re-apply, a remesh), a tile its RPC. Asking them once, right after the local check passed, read
            // every island voxel case as "the server kept the old surface" (R69); so ask again, re-reading here too, until they
            // agree or PeerSettleSeconds pass.
            List<PeerProbe> peers = null;
            string peerProblem, peerSummary;
            float peersUntil = Time.realtimeSinceStartup + PeerSettleSeconds;
            while (true)
            {
                yield return AdminToolsProbe.AskAll(points, answers => peers = answers);
                after = AdminToolsProbe.ReadLocal(points);
                verdict = toolCase.Check(cell, before, after);
                peerProblem = ComparePeers(points, after, peers, haveBot, out peerSummary);
                if ((verdict == null && peerProblem == null) || Time.realtimeSinceStartup >= peersUntil || s_stop) break;
                yield return new WaitForSecondsRealtime(PeerRetrySeconds);
            }
            // Still a FAIL when a peer lags past the settle, but measure by how much: R75's island vfill had the server 0.7 m
            // low after 12 s, yet it agreed by the persist case minutes later.
            if (verdict == null && peerProblem != null && !s_stop)
            {
                float peersFrom = peersUntil - PeerSettleSeconds, lateUntil = peersUntil + PeerLateSeconds;
                string lateNote = $"; still apart {PeerSettleSeconds + PeerLateSeconds:0} s after the edit";
                while (Time.realtimeSinceStartup < lateUntil && !s_stop)
                {
                    yield return new WaitForSecondsRealtime(PeerRetrySeconds);
                    List<PeerProbe> late = null;
                    yield return AdminToolsProbe.AskAll(points, answers => late = answers);
                    if (ComparePeers(points, AdminToolsProbe.ReadLocal(points), late, haveBot, out _) != null) continue;
                    lateNote = $"; the peers caught up {Time.realtimeSinceStartup - peersFrom:0} s after the edit";
                    break;
                }
                peerProblem += lateNote;
            }
            Changed(zdosBefore, cell, out int changedZdos, out int changedBytes);
            int errors = AdminToolsProbe.LocalErrors - errorsBefore + PeerErrorDelta(peers);

            string cost = $"{applyMs:0.0} ms, {changedZdos} ZDO {changedBytes / BytesPerKilobyte:0.0} KB";
            string standsOn = AdminToolsProbe.LastStandingHit != null ? $"; here stands on {AdminToolsProbe.LastStandingHit}" : "";
            string summary = $"here {Summary(points, after)}; {peerSummary}; {cost}{standsOn}";
            if (verdict != null) Step(name, Fail, $"{verdict} ({summary})");
            else if (peerProblem != null) Step(name, Fail, $"{peerProblem} ({summary})");
            else if (errors > 0) Step(name, Fail, $"{errors} error(s) logged during the case ({summary})");
            else Step(name, Pass, summary);
            yield return Shot(cell, name);
            results.Add(new Recorded { Place = place, Case = toolCase.Name, Points = points, Values = after });
        }

        private static string ComparePeers(List<ProbePoint> points, float[] mine, List<PeerProbe> peers, bool haveBot, out string summary)
        {
            var parts = new List<string>();
            string problem = null;
            bool botAnswered = false;
            long me = ZNet.GetUID();
            foreach (var peer in peers ?? new List<PeerProbe>())
            {
                if (peer.Sender == me || peer.Values == null) continue;
                if (peer.Role == "bot") botAnswered = true;
                // A peer that reads nothing where this client reads something doesn't have the place loaded: said, never passed.
                bool loaded = false, anyHere = false;
                for (int i = 0; i < points.Count && i < peer.Values.Length; i++)
                {
                    if (float.IsNaN(mine[i])) continue;
                    anyHere = true;
                    if (!float.IsNaN(peer.Values[i])) loaded = true;
                }
                if (anyHere && !loaded) { parts.Add($"{peer.Role}: not loaded here (nothing compared)"); continue; }
                parts.Add($"{peer.Role} {Summary(points, peer.Values)}");
                for (int i = 0; i < points.Count && i < peer.Values.Length; i++)
                {
                    if (float.IsNaN(mine[i]) || float.IsNaN(peer.Values[i])) continue;
                    if (Mathf.Abs(mine[i] - peer.Values[i]) <= Tolerance(points[i].Kind)) continue;
                    problem ??= $"the {peer.Role} reads {points[i].Kind} {peer.Values[i]:0.00} where this client reads {mine[i]:0.00}";
                }
            }
            if (haveBot && !botAnswered) problem ??= "the bot did not answer the probe";
            summary = parts.Count > 0 ? string.Join(", ", parts) : "no other peer answered";
            return problem;
        }

        // Ground to within the brushes' own tolerance; water and ice to within two peers' waves ([visual]); kinds,
        // flags and counts exactly.
        private static float Tolerance(ProbeKind kind)
        {
            switch (kind)
            {
                case ProbeKind.TileHeight:
                case ProbeKind.VoxelSurface:
                case ProbeKind.VanillaHeight:
                case ProbeKind.Standing:
                    return AdminToolsCases.HeightTolerance;
                case ProbeKind.WaterSurface:
                case ProbeKind.PourLevel:
                case ProbeKind.LandIce:
                case ProbeKind.FloatingIce:
                case ProbeKind.LandSnow:
                    return AdminToolsWaterCases.SurfaceTolerance;
                default:
                    return 0f;
            }
        }

        private static int PeerErrorDelta(List<PeerProbe> peers)
        {
            int delta = 0;
            long me = ZNet.GetUID();
            foreach (var peer in peers ?? new List<PeerProbe>())
            {
                if (peer.Sender == me) continue;
                if (s_peerErrors.TryGetValue(peer.Sender, out int previous)) delta += Math.Max(0, peer.Errors - previous);
                s_peerErrors[peer.Sender] = peer.Errors;
            }
            return delta;
        }

        private static string Summary(List<ProbePoint> points, float[] values)
        {
            var parts = new List<string>();
            for (int i = 0; i < points.Count && i < values.Length; i++)
            {
                if (float.IsNaN(values[i])) continue;
                parts.Add(points[i].Kind == ProbeKind.TilePaint || points[i].Kind == ProbeKind.PaintMask
                    ? $"{points[i].Kind}={AdminToolsCases.Colour(values[i])}" : $"{points[i].Kind}={values[i]:0.00}");
            }
            return parts.Count > 0 ? string.Join(" ", parts) : "nothing readable";
        }

        // ── Persistence ─────────────────────────────────────────────────────────────────────────────────────────
        private static IEnumerator PersistUnload(PlaceKind place, Vector3 spot, List<Recorded> results, bool haveBot)
        {
            var player = Player.m_localPlayer;
            var away = spot + new Vector3(AwayMetres, 0f, 0f);
            // A following bot leaves with the tester (it teleports after it), which unloads the place for both.
            if (haveBot && !FatTestBot.Following) FatTestBot.Send(string.Format(CultureInfo.InvariantCulture, "away {0:0} {1:0}", AwayMetres, AwayBearing));
            player.TeleportTo(away + Vector3.up * TeleportLift, player.transform.rotation, true);
            yield return null;
            while (player.IsTeleporting()) yield return null;
            yield return new WaitForSecondsRealtime(AwaySeconds);
            yield return Arrive(place, spot, haveBot);
            if (s_areaProblem != null) { Step($"{Name(place)}/persist_unload", Skip, s_areaProblem); yield break; }
            yield return Compare(place, "persist_unload", results, haveBot);
        }

        private static IEnumerator Verify(PlaceKind place, List<Recorded> expected, bool haveBot)
        {
            if (expected.Count == 0)
            {
                Step($"{Name(place)}/verify", Skip, "no expectations recorded for this place: run the test before the restart");
                yield break;
            }
            yield return Compare(place, "verify", expected, haveBot);
        }

        private static IEnumerator Compare(PlaceKind place, string step, List<Recorded> expected, bool haveBot)
        {
            var points = new List<ProbePoint>();
            var wanted = new List<float>();
            var owners = new List<string>();
            foreach (var record in expected)
                for (int i = 0; i < record.Points.Count; i++)
                {
                    points.Add(record.Points[i]);
                    wanted.Add(record.Values[i]);
                    owners.Add(record.Case);
                }
            float[] mine;
            List<string> lost;
            float until = Time.realtimeSinceStartup + ReloadSettleSeconds;
            do
            {
                yield return null;
                mine = AdminToolsProbe.ReadLocal(points);
                lost = Lost(points, wanted, owners, mine);
            }
            while (lost.Count > 0 && Time.realtimeSinceStartup < until);
            List<PeerProbe> peers = null;
            yield return AdminToolsProbe.AskAll(points, answers => peers = answers);
            string peerProblem = ComparePeers(points, mine, peers, haveBot, out _);
            if (lost.Count > 0) Step($"{Name(place)}/{step}", Fail, $"{lost.Count} of {expected.Count} case(s) changed: {string.Join(", ", lost)}");
            else if (peerProblem != null) Step($"{Name(place)}/{step}", Fail, peerProblem);
            else Step($"{Name(place)}/{step}", Pass, $"all {expected.Count} case(s) read back as left ({points.Count} points), the bot and the server agree");
            yield return Shot(Player.m_localPlayer.transform.position, $"{Name(place)}/{step}");
        }

        // A reloaded zone rebuilds its tiles and voxels from their ZDOs over a few seconds; each point is read until it
        // matches or this runs out.
        private const float ReloadSettleSeconds = 20f;

        // BEGIN's "~N s": the test runner stops a test after max(30 s, 2 x N), so N must cover the slow parts, not just
        // the cases (R70: verify took 52 s and clean 38 s against "~18 s" and "~6 s"). Per place: arriving (a teleport, and
        // a second local hop into a cave or onto an island), the cells' ground wait, then the mode's own work.
        private const float ArriveEstimate = 10f, VerifyPlaceEstimate = 32f, CleanPlaceEstimate = 45f, FindPlaceEstimate = 40f;
        private const float RunPlaceOverhead = 90f, CaseEstimate = 22f;   // R74: 26 surface cases took 588 s

        private static float EstimateSeconds(Options options, int cases, bool refused)
        {
            if (refused) return SecondsPerCase;
            int places = options.Places.Count;
            if (options.Find) return places * FindPlaceEstimate;
            if (options.Verify) return places * VerifyPlaceEstimate;
            if (options.CleanOnly) return places * CleanPlaceEstimate;
            // A run: arrive, find usable cells, reset them, the cases (each up to its settle + the peer wait), then
            // persist_unload's walk away and back.
            return places * (ArriveEstimate + RunPlaceOverhead + cases * CaseEstimate);
        }

        // Which tester runs case i: all of them without a split; with one, the bot's tools (`tools`) go to split b, the rest
        // alternate by their place in the shared list (a the even ones, b the odd), so each keeps its own cell.
        private static bool Mine(Options options, int i, ToolCase toolCase)
        {
            if (options.Split == '\0') return true;
            if (toolCase.Family == AdminToolsCases.ToolsFamily) return options.Split == 'b';
            return (i % 2 == 0) == (options.Split == 'a');
        }

        // The running split ('\0' none). In split b this client IS the bot, so it uses the tools itself.
        private static char s_split;
        internal static bool UsesToolsItself => s_split == 'b';

        private static bool VanillaGround(string family)
            => family == AdminToolsCases.VanillaFamily || family == AdminToolsCases.InfinityFamily
               || family == AdminToolsCases.ToolsFamily;   // the bot's tools run at the surface place: the hoe needs vanilla's ground

        // Each changed case names its first changed point, recorded -> read now, so a FAIL tells "back to the ground before the
        // edit" (the edit never reached the save) from a transient recorded mid-remesh (R69 island persist_unload).
        private static List<string> Lost(List<ProbePoint> points, List<float> wanted, List<string> owners, float[] mine)
        {
            var lost = new List<string>();
            var named = new HashSet<string>();
            for (int i = 0; i < points.Count; i++)
            {
                if (float.IsNaN(wanted[i])) continue;
                bool gone = float.IsNaN(mine[i]) || Mathf.Abs(mine[i] - wanted[i]) > Tolerance(points[i].Kind);
                if (!gone || !named.Add(owners[i])) continue;
                lost.Add(string.Format(CultureInfo.InvariantCulture, "{0} ({1} {2:0.00} -> {3})", owners[i], points[i].Kind, wanted[i],
                    float.IsNaN(mine[i]) ? "nothing" : mine[i].ToString("0.00", CultureInfo.InvariantCulture)));
            }
            return lost;
        }

        private static string ExpectPath(string world)
            => Path.Combine(Paths.BepInExRootPath, ResultsFolder, string.Format(CultureInfo.InvariantCulture, ExpectFilePattern, world));

        // Replaces only the places this run recorded, keeping every other place's expectations: a run of one place (or one
        // whose cases all SKIPped) used to rewrite the file and wipe the rest (R72's all-skip surface run erased the cave's
        // and the island's, so R73's verify had nothing to compare).
        // Replaces the expectations of every place in `recorded` and of every place in `voided` (reset or re-found: its old
        // expectations no longer describe its ground); other places keep theirs.
        private static void SaveExpect(string world, List<Recorded> recorded, IEnumerable<PlaceKind> voided = null)
        {
            var lines = new List<string>();
            var replaced = new HashSet<PlaceKind>(voided ?? Enumerable.Empty<PlaceKind>());
            foreach (var record in recorded) replaced.Add(record.Place);
            string path = ExpectPath(world);
            if (File.Exists(path))
                foreach (string line in File.ReadAllLines(path))
                {
                    int space = line.IndexOf(' ');
                    if (space > 0 && Enum.TryParse(line.Substring(0, space), out PlaceKind place) && !replaced.Contains(place)) lines.Add(line);
                }
            foreach (var record in recorded)
                for (int i = 0; i < record.Points.Count; i++)
                {
                    var at = record.Points[i].At;
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3:R} {4:R} {5:R} {6:R}",
                        record.Place, record.Case, record.Points[i].Kind, at.x, at.y, at.z, record.Values[i]));
                }
            Directory.CreateDirectory(Path.GetDirectoryName(ExpectPath(world)));
            File.WriteAllLines(ExpectPath(world), lines.ToArray());
        }

        private static List<Recorded> LoadExpect(string world)
        {
            var byCase = new Dictionary<string, Recorded>();
            string path = ExpectPath(world);
            if (!File.Exists(path)) return new List<Recorded>();
            foreach (string line in File.ReadAllLines(path))
            {
                var parts = line.Split(' ');
                if (parts.Length < 7 || !Enum.TryParse(parts[0], out PlaceKind place) || !Enum.TryParse(parts[2], out ProbeKind kind)) continue;
                float[] numbers = new float[4];
                bool ok = true;
                for (int i = 0; i < 4; i++) ok &= float.TryParse(parts[3 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]);
                if (!ok) continue;
                string key = parts[0] + "/" + parts[1];
                if (!byCase.TryGetValue(key, out var record))
                    byCase[key] = record = new Recorded { Place = place, Case = parts[1], Points = new List<ProbePoint>(), Values = Array.Empty<float>() };
                record.Points.Add(new ProbePoint(kind, new Vector3(numbers[0], numbers[1], numbers[2])));
                record.Values = record.Values.Concat(new[] { numbers[3] }).ToArray();
            }
            return byCase.Values.ToList();
        }

        // ── Cost: the ZDOs an operation changed near its cell, as they would be sent ───────────────────────────
        private static Dictionary<ZDOID, uint> Revisions(Vector3 around)
        {
            var revisions = new Dictionary<ZDOID, uint>();
            if (ZDOMan.instance == null) return revisions;
            float limit = ChangeRadius * ChangeRadius;
            foreach (var pair in ObjectsById(ZDOMan.instance))
            {
                var offset = pair.Value.GetPosition() - around;
                if (offset.x * offset.x + offset.z * offset.z <= limit) revisions[pair.Key] = pair.Value.DataRevision;
            }
            return revisions;
        }

        private static void Changed(Dictionary<ZDOID, uint> before, Vector3 around, out int count, out int bytes)
        {
            count = bytes = 0;
            if (ZDOMan.instance == null) return;
            var now = Revisions(around);
            var objects = ObjectsById(ZDOMan.instance);
            foreach (var pair in now)
            {
                if (before.TryGetValue(pair.Key, out uint revision) && revision == pair.Value) continue;
                if (!objects.TryGetValue(pair.Key, out ZDO zdo)) continue;
                var pkg = new ZPackage();
                zdo.Serialize(pkg);
                count++;
                bytes += pkg.Size();
            }
        }

        // ── Output ──────────────────────────────────────────────────────────────────────────────────────────────
        private static string Name(PlaceKind place) => place.ToString().ToLowerInvariant();

        private static void Step(string name, string result, string detail)
        {
            s_step++;
            if (result == Pass) s_passes++;
            else if (result == Fail) s_fails++;
            else s_skips++;
            Say($"{Tag} {s_step}/{Math.Max(s_steps, s_step)} {name} {result} - {detail}");
        }

        private static IEnumerator Shot(Vector3 at, string caption)
        {
            Say($"{Tag} {ShotWord} {ShotTarget(at + Vector3.up * ShotLift, ShotRadius)} {caption}");
            yield return new WaitForSecondsRealtime(ShotHoldSeconds);
        }

        private static string ShotTarget(Vector3 point, float radius) =>
            "{" + string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0},{3:0.0}", point.x, point.y, point.z, radius) + "}";

        private static void Say(string line)
        {
            global::VerdantsAscent.FiresAdminTerrain.Log?.LogMessage(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            Record(line);
        }

        // For the cases (the bot's tool replies) to put a line in the record.
        internal static void Note(string line) => Record(line);

        private static void Record(string line)
        {
            try
            {
                string folder = Path.Combine(Paths.BepInExRootPath, ResultsFolder);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, ResultsFile), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception) { }
        }
    }
}
