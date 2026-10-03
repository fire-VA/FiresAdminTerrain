using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Zones
{
    // `generator_test`, the Chunk Generator's F5 test (Tools\FIRES_F5_TESTS.md; steps in Tools\CHUNK_GENERATOR.md, "F5
    // test"). On the tester's client it places a generator in front of the player and checks it end to end: the prefab and
    // its bound dressing, the rune halo keeping its runes through a hammer highlight, the plain recipe's colour cycle, both
    // windows, the test bot (client B) adding fuel and reading the machine back, a fill past capacity, burning, a batch,
    // and a recipe switch with its refund. Everything it spawns is removed at the end, also after a throw; each object also
    // carries a marker saved with the world, and the server removes marked leftovers of a crashed run before the next run
    // starts (BepInEx\FiresTests\generator_spawned_<world>.txt is only a record). It never sets a key:
    // it switches to a biome recipe only where the world already knows one. `see` and `add` are the halves the bot runs
    // through `bot cmd`.
    internal static class ZoneModeChunkGeneratorTest
    {
        private const string Command = "generator_test";
        private const string Tag = "[GeneratorTest]";
        private const string ResultsFolder = "FiresTests";
        private const string ResultsFile = "generator_test.txt";
        private const string SpawnedFile = "generator_spawned_{0}.txt";
        private const string RecordSeparator = "|";
        private const string CleanArg = "clean";
        private const string CleanRpc = "FiresChunkGen_TestClean";
        private const string CleanedRpc = "FiresChunkGen_TestCleaned";
        private const int SweepPerFrame = 5000;
        private const float CleanWaitSeconds = 15f;
        private const string ListArg = "list";
        private const string KeepArg = "keep";
        private const string SeeArg = "see";
        private const string AddArg = "add";
        private const string BotVerbPrefix = "bot ";
        private const string BotReplyPrefix = "bot: ";
        private const string Passed = "PASS";
        private const string Failed = "FAIL";
        private const string Skipped = "SKIP";
        private const int EstimateSeconds = 60;
        private const int SettleFrames = 3;
        private const float PlaceDistance = 4f;
        private const float HighlightSeconds = 0.5f;
        private const float CycleWatchSeconds = 2f;
        private const float PollSeconds = 0.25f;
        private const float CycleMinChange = 0.05f;
        private const float SteadyWatchSeconds = 0.6f;
        private const float SteadyMaxChange = 0.001f;
        private const float LookWaitSeconds = 2f;
        private const float AnswerSeconds = 10f;
        private const int RemoteAddCount = 5;
        private const int SeeAttempts = 2;
        private const float SeeRetrySeconds = 1f;
        private const string FoundKey = "found=";
        private const string NotFound = "found=no";
        private const string AddedKey = " added=";
        private const string RefusedKey = " refused=";
        private const float RemoteReach = 10f;
        private const float StateWaitSeconds = 8f;
        private const int OverflowExtra = 7;
        private const int BurnAtLeast = 2;
        private const float BurnWaitSeconds = 10f;
        private const int TopUp = 3;
        private const float BatchWaitSeconds = 8f;
        private const float RecipeWaitSeconds = 4f;
        private const float DropRadius = 4f;
        // A SHOT line asks FDT's watcher for a screenshot of this moment (Tools\FIRES_TEST_SCREENSHOTS.md); the test holds
        // still long enough for the capture to land.
        private const string ShotWord = "SHOT";
        private const float ShotHoldSeconds = 1f;
        // What FDT's framing holds for each moment ({x,y,z,r}): the machine's runes round its core, and the output's drops.
        private const float MachineShotLift = 1.5f;
        private const float MachineShotRadius = 2.5f;
        private const float OutputShotRadius = 1.5f;
        private const float HashWaitSeconds = 5f;
        private const char IdSeparator = ':';

        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(VerdantsAscent.FiresAdminTerrain.PluginName);
        private static readonly Regex Field = new Regex(@"(\w+)=(\S+)");
        private static readonly List<KeyValuePair<long, string>> s_replies = new List<KeyValuePair<long, string>>();
        private static readonly List<ZDOID> s_spawned = new List<ZDOID>();
        private static readonly List<string> s_records = new List<string>();
        private static readonly int MarkerHash = "generator_test_object".GetStableHashCode();
        private static ZRoutedRpc s_rpcHost;
        private static volatile int s_cleaned = -1;
        private static bool s_registered;
        private static bool s_running;
        private static bool s_listening;
        private static Smelter s_machine;
        private static long s_botUid;
        private static string s_got;
        private static long s_gotFrom;

        private sealed class Step
        {
            internal readonly string Name;
            internal readonly Func<Step, IEnumerator> Run;
            internal readonly bool Always;
            internal string Outcome;
            internal string Detail;

            internal Step(string name, Func<Step, IEnumerator> run, bool always = false)
            {
                Name = name;
                Run = run;
                Always = always;
            }

            internal void Pass(string detail) { Outcome = Passed; Detail = detail; }
            internal void Fail(string detail) { Outcome = Failed; Detail = detail; }
            internal void Skip(string detail) { Outcome = Skipped; Detail = detail; }
        }

        private static List<Step> Steps() => new List<Step>
        {
            new Step("prefab", Prefab, true),
            new Step("place", Place, true),
            new Step("look", Look),
            new Step("cycle", Cycle),
            new Step("windows", Windows),
            new Step("two_clients", TwoClients),
            new Step("fuel", Fuel),
            new Step("burn", Burn),
            new Step("batch", Batch),
            new Step("recipes", Recipes),
        };

        // Not a cheat command: a cheat is refused on a dedi's client in 1.0, so the admin gate is inside.
        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                $"Chunk Generator checks ({ListArg} | {KeepArg} | {CleanArg} | <step...>); PASS/FAIL per step, results in BepInEx/{ResultsFolder}/{ResultsFile}. "
                + $"'{SeeArg} <id>' and '{AddArg} <id> <n>' are the halves the test bot runs.",
                args => Run(args), isCheat: false);
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            var words = new List<string>();
            for (int i = 1; args != null && i < args.Length; i++) words.Add(args[i]);
            if (words.Count > 0 && Is(words[0], SeeArg)) { See(words); return; }
            if (words.Count > 0 && Is(words[0], AddArg)) { Add(words); return; }
            if (words.Count > 0 && Is(words[0], CleanArg))
            {
                if (s_running) { Say($"{Tag} already running."); return; }
                VerdantsAscent.FiresAdminTerrain.Instance?.StartCoroutine(CleanOnly());
                return;
            }
            var steps = Steps();
            if (words.Exists(w => Is(w, ListArg)))
            {
                Say($"{Tag} steps: " + string.Join(", ", steps.ConvertAll(s => s.Name)));
                return;
            }
            bool keep = words.Exists(w => Is(w, KeepArg));
            var wanted = new HashSet<string>(words.FindAll(w => !Is(w, KeepArg)), StringComparer.OrdinalIgnoreCase);
            if (wanted.Count > 0) steps.RemoveAll(s => !s.Always && !wanted.Contains(s.Name));
            if (s_running)
            {
                Say($"{Tag} already running.");
                return;
            }
            var host = VerdantsAscent.FiresAdminTerrain.Instance;
            if (host != null) host.StartCoroutine(RunAll(steps, keep));
        }

        private static IEnumerator RunAll(List<Step> steps, bool keep)
        {
            s_running = true;
            s_listening = true;
            s_replies.Clear();
            s_spawned.Clear();
            s_records.Clear();
            s_machine = null;
            s_botUid = 0;
            // Hashing the 9 MB DLL on the main thread stalled the first run 100 ms (R3 LoginDiag); FAT hashes it once off the
            // main thread at load, and BEGIN waits a moment for it.
            float started = Time.realtimeSinceStartup;
            while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < started + HashWaitSeconds) yield return null;
            var me = Player.m_localPlayer;
            string player = me != null ? me.GetPlayerName() : "none";
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "none";
            Say($"{Tag} BEGIN {VerdantsAscent.FiresAdminTerrain.PluginName} {VerdantsAscent.FiresAdminTerrain.PluginVersion} ({FatDllHash.Value}) "
                + $"on client '{player}' world '{world}': {steps.Count} steps, ~{EstimateSeconds} s");
            string gate = Gate();
            int pass = 0, fail = 0, skip = 0;
            try
            {
                if (gate == null) yield return RequestClean();
                for (int i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    if (gate != null) step.Skip(gate);
                    else
                    {
                        // Nested routines are driven here rather than handed to Unity, so a throw anywhere in a step is
                        // that step's FAIL and the run goes on.
                        var stack = new Stack<IEnumerator>();
                        stack.Push(step.Run(step));
                        while (stack.Count > 0)
                        {
                            var top = stack.Peek();
                            bool more;
                            try { more = top.MoveNext(); }
                            catch (Exception ex)
                            {
                                step.Fail($"threw {ex.GetType().Name}: {ex.Message}");
                                Record($"{step.Name}: {ex}");
                                break;
                            }
                            if (!more) { stack.Pop(); continue; }
                            if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                            yield return top.Current;
                        }
                    }
                    if (step.Outcome == null) step.Fail("finished without a result");
                    if (step.Outcome == Passed) pass++;
                    else if (step.Outcome == Failed) fail++;
                    else skip++;
                    Say($"{Tag} {i + 1}/{steps.Count} {step.Name} {step.Outcome} - {step.Detail}");
                }
            }
            finally
            {
                if (ZoneModeChunkGeneratorMenu.IsOpen) ZoneModeChunkGeneratorMenu.Close();
                if (s_botUid != 0)
                {
                    Send("auto on");
                    Send("follow");
                }
                if (gate == null)
                {
                    if (!keep) RemoveSpawned();
                    SaveSpawned();
                }
                s_listening = false;
                s_running = false;
            }
            float seconds = Time.realtimeSinceStartup - started;
            Say($"{Tag} END {(fail > 0 ? Failed : Passed)}: {pass} pass, {fail} fail, {skip} skip in {seconds:0.0} s");
        }

        private static IEnumerator CleanOnly()
        {
            string gate = Gate();
            if (gate != null) { Say($"{Tag} {CleanArg}: {gate}"); yield break; }
            yield return RequestClean();
            Say(s_cleaned < 0
                ? $"{Tag} {CleanArg}: the server did not answer within {CleanWaitSeconds:0} s"
                : $"{Tag} {CleanArg}: the server removed {s_cleaned} marked object(s) left by earlier runs");
        }

        private static string Gate()
        {
            if (Player.m_localPlayer == null || ZNet.instance == null || ZNetScene.instance == null || ZDOMan.instance == null)
                return "no local player (run it in a world)";
            return ZNet.instance.LocalPlayerIsAdminOrHost() ? null : "admins only: it places, fuels and removes a machine";
        }

        // ── Steps ────────────────────────────────────────────────────────────────────────────────────────────────
        private static IEnumerator Prefab(Step step)
        {
            if (!ZoneModeChunkGenerator.TryGetPrefab(out var prefab))
            {
                step.Fail("no Chunk Generator prefab: its bundle or wiring failed (see the [FiresZones] warnings)");
                yield break;
            }
            var smelter = prefab.GetComponent<Smelter>();
            bool door = SwitchOf(prefab, ZoneModeChunkGenerator.FuelSwitch) != null;
            bool hopper = SwitchOf(prefab, ZoneModeChunkGenerator.RecipeSwitch) != null;
            int listed = 0, known = 0;
            foreach (string name in ZoneModeChunkTiers.Burns(ZoneModeChunkTiers.Starter))
            {
                listed++;
                if (ZoneModeChunkGenerator.TryGetFuel(name, out _)) known++;
            }
            int dressed = 0, bare = 0;
            var dress = prefab.transform.Find(ZoneModeChunkGeneratorLook.DressRoot);
            if (dress != null)
                foreach (var renderer in dress.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (renderer.sharedMaterial == null || filter == null || filter.sharedMesh == null) bare++;
                    else dressed++;
                }
            var mask = ZoneModeChunkGeneratorLook.RuneMask;
            if (smelter == null || !door || !hopper)
                step.Fail("the prefab lacks " + (smelter == null ? "its Smelter" : !door ? "its fuel door" : "its recipe hopper"));
            else if (smelter.m_maxOre != ZoneModeChunkGenerator.Capacity)
                step.Fail($"it holds {smelter.m_maxOre}, not {ZoneModeChunkGenerator.Capacity}");
            else if (known == 0)
                step.Fail("no fuel the plain recipe burns exists in this game");
            else if (dressed == 0 || bare > 0)
                step.Fail($"dressing: {dressed} piece(s) bound, {bare} bare (see 'Chunk Generator dressing' in the log)");
            else if (!NullGpu && mask == null)
                step.Fail("no rune mask was built, so the halo is hidden (see 'Chunk Generator dressing' in the log)");
            else
                step.Pass($"{known}/{listed} fuels in this game, {dressed} dressing piece(s) bound"
                          + (mask != null ? $", rune mask {mask.width}x{mask.height}" : ", no GPU here"));
        }

        private static IEnumerator Place(Step step)
        {
            if (!ZoneModeChunkGenerator.TryGetPrefab(out var prefab))
            {
                step.Skip("no prefab (see prefab)");
                yield break;
            }
            var me = Player.m_localPlayer;
            var forward = Vector3.ProjectOnPlane(me.transform.forward, Vector3.up).normalized;
            if (forward == Vector3.zero) forward = Vector3.forward;
            var at = me.transform.position + forward * PlaceDistance;
            at.y = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(at) : at.y;
            var machine = UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(-forward));
            var nview = machine.GetComponent<ZNetView>();
            Remember(nview != null ? nview.GetZDO() : null);
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
            if (nview == null || !nview.IsValid())
            {
                step.Fail("the machine got no ZDO");
                yield break;
            }
            s_machine = machine.GetComponent<Smelter>();
            var door = SwitchOf(machine, ZoneModeChunkGenerator.FuelSwitch);
            if (!ZoneModeChunkGenerator.IsOurs(nview)) step.Fail("the placed object is not the Chunk Generator");
            else if (!nview.IsOwner()) step.Fail("placed, but this client does not own it");
            else if (machine.GetComponent<ZoneModeChunkGeneratorLook.GeneratorGlow>() == null || door == null || door.m_onUse == null)
                step.Fail("its Awake hook did not run (no glow driver or door hook)");
            else step.Pass($"at ({at.x:0},{at.z:0}), id {Id(nview.GetZDO().m_uid)}, on {Recipe().RecipeName}");
        }

        // The white-disk regression (FAT 0.2.595): a hammer highlight rewrites every renderer's property block on the
        // piece, so the runes must live in the halo's material, never in a block.
        private static IEnumerator Look(Step step)
        {
            if (!HaveMachine(step)) yield break;
            if (NullGpu)
            {
                step.Skip("no GPU here: nothing is drawn");
                yield break;
            }
            var glow = Glow();
            var recipe = Recipe();
            string suffix = "_" + recipe.Id;
            int wrong = 0;
            foreach (var renderer in glow.Renderers)
                if (renderer != null && (renderer.sharedMaterial == null || !renderer.sharedMaterial.name.EndsWith(suffix, StringComparison.Ordinal))) wrong++;
            var halo = Halo(glow);
            if (glow.Renderers.Count == 0) { step.Fail("the machine has no dressing renderers"); yield break; }
            if (wrong > 0) { step.Fail($"{wrong} of {glow.Renderers.Count} dressing renderer(s) are not on {recipe.Id}'s shared copies"); yield break; }
            if (!HasRunes(halo)) { step.Fail("the rune halo is missing, hidden, or its material lacks the rune mask"); yield break; }
            var wear = s_machine.GetComponent<WearNTear>();
            if (wear != null) wear.Highlight();
            yield return new WaitForSeconds(HighlightSeconds);
            var block = new MaterialPropertyBlock();
            halo.GetPropertyBlock(block);
            if (!HasRunes(halo) || block.GetTexture(ZoneModeChunkGeneratorLook.MainTex) != null)
                step.Fail("after a hammer highlight the halo lost its runes (the white-disk bug)");
            else
                step.Pass($"{glow.Renderers.Count} dressing renderer(s) on {recipe.Id}'s shared copies; the halo keeps its runes "
                          + (wear != null ? "through a hammer highlight" : "(no WearNTear to highlight)"));
        }

        private static IEnumerator Cycle(Step step)
        {
            if (!HaveMachine(step)) yield break;
            if (NullGpu)
            {
                step.Skip("no GPU here: nothing is drawn");
                yield break;
            }
            var recipe = Recipe();
            if (recipe != ZoneModeChunkTiers.Starter)
            {
                step.Skip($"the machine is on {recipe.RecipeName}; only the plain recipe cycles");
                yield break;
            }
            var halo = Halo(Glow());
            var material = halo != null ? halo.sharedMaterial : null;
            if (material == null)
            {
                step.Fail("no halo material to watch");
                yield break;
            }
            var first = material.GetColor(ZoneModeChunkGeneratorLook.EmissionColor);
            Say($"{Tag} {ShotWord} {ShotTarget(s_machine.transform.position + Vector3.up * MachineShotLift, MachineShotRadius)} the plain recipe's colour cycle");
            float moved = 0f;
            for (float watched = 0f; watched < CycleWatchSeconds; watched += PollSeconds)
            {
                yield return new WaitForSeconds(PollSeconds);
                moved = Mathf.Max(moved, Difference(first, material.GetColor(ZoneModeChunkGeneratorLook.EmissionColor)));
            }
            if (moved >= CycleMinChange) step.Pass($"the plain glow moved {moved:0.00} over {CycleWatchSeconds:0} s");
            else step.Fail($"the plain glow held still for {CycleWatchSeconds:0} s (moved {moved:0.000}): the colour cycle is not running");
        }

        private static IEnumerator Windows(Step step)
        {
            if (!HaveMachine(step)) yield break;
            var me = Player.m_localPlayer;
            bool recipes = ZoneModeChunkGeneratorMenu.OpenRecipes(s_machine, me);
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
            bool recipesOpen = ZoneModeChunkGeneratorMenu.IsOpen;
            ZoneModeChunkGeneratorMenu.Close();
            yield return null;
            if (!recipes || !recipesOpen)
            {
                step.Fail("the recipe window did not open (the log names the vanilla part it could not borrow)");
                yield break;
            }
            string refusal = ZoneModeChunkGeneratorWindows.FuelRefusal(Recipe(), me, ZoneModeChunkGenerator.Loaded(Zdo()));
            if (refusal != null)
            {
                step.Pass($"the recipe window opened; the fuel door answers '{refusal}' (nothing to add)");
                yield break;
            }
            bool fuel = ZoneModeChunkGeneratorMenu.OpenFuel(s_machine, me, null);
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
            var grid = ZoneModeChunkGeneratorWindows.FuelGrid;
            bool fuelOpen = ZoneModeChunkGeneratorMenu.IsOpen && grid != null;
            int used = 0, stray = 0;
            if (grid != null)
                foreach (var element in grid.m_elements)
                {
                    if (element.m_used) used++;
                    else if (element.m_icon != null && element.m_icon.enabled) stray++;
                }
            ZoneModeChunkGeneratorMenu.Close();
            if (!fuel || !fuelOpen) step.Fail("the fuel window did not open (the log names the vanilla part it could not borrow)");
            else if (stray > 0) step.Fail($"{stray} empty fuel square(s) show an icon: the grid stopped drawing after its first item");
            else step.Pass($"both windows opened; {used} fuel square(s) filled, the empty ones clean");
        }

        // Client B is the test bot, driven through its chat contract: it adds fuel to this exact machine and reports what
        // its own copy of the machine holds, which must match this client's.
        private static IEnumerator TwoClients(Step step)
        {
            if (!HaveMachine(step)) yield break;
            Send("follow");
            yield return AwaitReply(r => r.StartsWith("following", StringComparison.OrdinalIgnoreCase));
            if (s_got == null)
            {
                step.Skip($"no bot answered 'bot follow' within {AnswerSeconds:0} s (none follows you)");
                yield break;
            }
            s_botUid = s_gotFrom;
            Send("auto off");
            yield return AwaitReply(r => r.StartsWith("auto replies", StringComparison.OrdinalIgnoreCase));
            var zdo = Zdo();
            string id = Id(zdo.m_uid);
            int before = Held(zdo);
            Send($"cmd {Command} {AddArg} {id} {RemoteAddCount}");
            yield return AwaitReply(r => r.IndexOf(AddedKey, StringComparison.Ordinal) >= 0 || r.IndexOf(RefusedKey, StringComparison.Ordinal) >= 0
                                         || r.IndexOf(NotFound, StringComparison.Ordinal) >= 0);
            var added = Fields(s_got);
            float until = Time.realtimeSinceStartup + StateWaitSeconds;
            while (Held(zdo) < before + RemoteAddCount && Time.realtimeSinceStartup < until) yield return null;
            if (Held(zdo) < before + RemoteAddCount)
            {
                step.Fail($"the bot's add never reached the machine: this client sees {Held(zdo)}, expected {before + RemoteAddCount} "
                          + $"(bot: {s_got ?? "no reply"})");
                yield break;
            }
            Dictionary<string, string> seen = null;
            for (int attempt = 0; attempt < SeeAttempts; attempt++)
            {
                Send($"cmd {Command} {SeeArg} {id}");
                yield return AwaitReply(r => r.IndexOf(FoundKey, StringComparison.Ordinal) >= 0);
                seen = Fields(s_got);
                if (Matches(seen, zdo)) break;
                yield return new WaitForSeconds(SeeRetrySeconds);
            }
            string look = Value(seen, "look");
            string sees = $"B sees {Value(seen, "recipe")} holding {Sum(seen)} owned by {Value(seen, "owner")}";
            string ours = $"this client {Recipe().Id} holding {Held(zdo)} owned by {zdo.GetOwner()}";
            if (seen == null || Value(seen, "found") != "yes") step.Fail("the bot does not see the machine (bot: " + (s_got ?? "no reply") + ")");
            else if (!Matches(seen, zdo)) step.Fail($"{sees}; {ours}");
            else if (look == "hidden" || look == "none") step.Fail($"{sees}, but its halo is {look}");
            else step.Pass($"B added {Value(added, "added")} {Value(added, "item")} ({Value(added, "via")}); {sees}, halo {look}");
        }

        private static IEnumerator Fuel(Step step)
        {
            if (!HaveMachine(step) || !HaveFuel(step, out string fuel)) yield break;
            var zdo = Zdo();
            var beside = ZoneModeChunkGenerator.Beside(s_machine);
            var before = DropsNear(beside);
            int loaded = ZoneModeChunkGenerator.Loaded(zdo);
            int send = ZoneModeChunkGenerator.Capacity - loaded + OverflowExtra;
            ZoneModeChunkGenerator.LoadForTest(s_machine, fuel, send);
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
            int dropped = Stacks(NewDrops(before, beside));
            int now = ZoneModeChunkGenerator.Loaded(zdo);
            if (now < ZoneModeChunkGenerator.Capacity - 1)
                step.Fail($"{now} loaded after adding {send} to {loaded}; expected {ZoneModeChunkGenerator.Capacity}");
            else if (dropped != OverflowExtra)
                step.Fail($"{dropped} set beside it; expected the {OverflowExtra} that did not fit");
            else
                step.Pass($"{loaded} + {send} {fuel} -> {now}/{ZoneModeChunkGenerator.Capacity}, the {dropped} that did not fit set beside it");
        }

        private static IEnumerator Burn(Step step)
        {
            if (!HaveMachine(step) || !HaveFuel(step, out string fuel)) yield break;
            var zdo = Zdo();
            if (ZoneModeChunkGenerator.Loaded(zdo) <= BurnAtLeast)
            {
                ZoneModeChunkGenerator.LoadForTest(s_machine, fuel, BurnAtLeast + TopUp);
                yield return null;
            }
            int held = Held(zdo), burned = ZoneModeChunkGenerator.Burned(zdo);
            float started = Time.realtimeSinceStartup;
            while (ZoneModeChunkGenerator.Burned(zdo) < burned + BurnAtLeast && Time.realtimeSinceStartup < started + BurnWaitSeconds) yield return null;
            int count = ZoneModeChunkGenerator.Burned(zdo) - burned;
            if (count < BurnAtLeast) step.Fail($"burned {count} in {BurnWaitSeconds:0} s; the owner burns one every 2 s");
            else if (Held(zdo) != held) step.Fail($"it held {held} before burning and {Held(zdo)} after: fuel appeared or vanished");
            else step.Pass($"burned {count} in {Time.realtimeSinceStartup - started:0.0} s, still holding {held}");
        }

        private static IEnumerator Batch(Step step)
        {
            if (!HaveMachine(step) || !HaveFuel(step, out string fuel)) yield break;
            var zdo = Zdo();
            if (ZoneModeChunkGenerator.Loaded(zdo) < TopUp)
            {
                ZoneModeChunkGenerator.LoadForTest(s_machine, fuel, TopUp);
                yield return null;
            }
            var recipe = Recipe();
            var output = s_machine.m_outputPoint != null ? s_machine.m_outputPoint.position : s_machine.transform.position;
            var before = DropsNear(output);
            ZoneModeChunkGenerator.PrimeBatchForTest(zdo, fuel, ZoneModeChunkTiers.BatchFuel - 1);
            float started = Time.realtimeSinceStartup;
            int seeds = 0;
            while (seeds == 0 && Time.realtimeSinceStartup < started + BatchWaitSeconds)
            {
                yield return new WaitForSeconds(PollSeconds);
                seeds = Stacks(NewDrops(before, output).FindAll(d => Utils.GetPrefabName(d.gameObject) == recipe.SpawnerPrefab));
            }
            if (seeds > 0)
            {
                Say($"{Tag} {ShotWord} {ShotTarget(output, OutputShotRadius)} a {recipe.RecipeName} batch's seeds at the output");
                yield return new WaitForSeconds(ShotHoldSeconds);
            }
            if (seeds == 0) step.Fail($"no {recipe.SpawnerName} at the output {BatchWaitSeconds:0} s after the batch's last fuel");
            else if (seeds != recipe.SeedsPerBatch) step.Fail($"{seeds} seed(s) at the output; a {recipe.RecipeName} batch makes {recipe.SeedsPerBatch}");
            else if (ZoneModeChunkGenerator.Burned(zdo) >= ZoneModeChunkTiers.BatchFuel) step.Fail("the seeds dropped but the batch's fuel stayed counted");
            else step.Pass($"a {recipe.RecipeName} batch dropped {seeds} {recipe.SpawnerName}(s) at the output in {Time.realtimeSinceStartup - started:0.0} s");
        }

        private static IEnumerator Recipes(Step step)
        {
            if (!HaveMachine(step)) yield break;
            // The known biome recipe that sets the most beside the machine, so the refund is really exercised.
            var zdo = Zdo();
            ChunkTier target = null;
            int predicted = -1;
            foreach (var tier in ZoneModeChunkTiers.All)
            {
                if (tier.RestoresOnly || !ZoneModeChunkGenerator.IsKnown(tier)) continue;
                int refund = ZoneModeChunkGenerator.WouldRefund(zdo, tier);
                if (refund > predicted) { target = tier; predicted = refund; }
            }
            if (target == null)
            {
                step.Skip("only the plain recipe is known in this world (no boss keys; the test never sets any)");
                yield break;
            }
            var from = Recipe();
            var beside = ZoneModeChunkGenerator.Beside(s_machine);
            var before = DropsNear(beside);
            ZoneModeChunkGenerator.ChooseRecipe(s_machine, target);
            yield return WaitFor(() => Recipe() == target, RecipeWaitSeconds);
            yield return new WaitForSeconds(PollSeconds);
            int refunded = Stacks(NewDrops(before, beside));
            if (Recipe() != target)
            {
                step.Fail($"still on {Recipe().RecipeName} {RecipeWaitSeconds:0} s after choosing {target.RecipeName}");
                yield break;
            }
            string glowNote = "no GPU here";
            if (!NullGpu)
            {
                var halo = Halo(Glow());
                string suffix = "_" + target.Id;
                yield return WaitFor(() => halo != null && halo.sharedMaterial != null && halo.sharedMaterial.name.EndsWith(suffix, StringComparison.Ordinal), LookWaitSeconds);
                var material = halo != null ? halo.sharedMaterial : null;
                if (material == null || !material.name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    step.Fail($"the halo still wears {(material != null ? material.name : "nothing")} after the switch to {target.Id}");
                    yield break;
                }
                var first = material.GetColor(ZoneModeChunkGeneratorLook.EmissionColor);
                yield return new WaitForSeconds(SteadyWatchSeconds);
                float moved = Difference(first, material.GetColor(ZoneModeChunkGeneratorLook.EmissionColor));
                if (moved > SteadyMaxChange)
                {
                    step.Fail($"{target.Id}'s glow moved {moved:0.000}: only the plain recipe cycles");
                    yield break;
                }
                glowNote = $"steady {target.Id} glow";
            }
            ZoneModeChunkGenerator.ChooseRecipe(s_machine, from);
            yield return WaitFor(() => Recipe() == from, RecipeWaitSeconds);
            if (refunded != predicted) step.Fail($"{from.RecipeName} -> {target.RecipeName} set {refunded} beside it; {predicted} could not be burned");
            else step.Pass($"{from.RecipeName} -> {target.RecipeName}: {refunded} refunded as predicted, {glowNote}; back on {Recipe().RecipeName}");
        }

        // ── The bot's halves ─────────────────────────────────────────────────────────────────────────────────────
        // What this client's copy of a machine holds, one line of key=value words for the tester to read back.
        private static void See(List<string> words)
        {
            if (words.Count < 2 || !TryParseId(words[1], out var id) || ZDOMan.instance == null)
            {
                Say($"{Tag} {SeeArg} needs a machine id (uid{IdSeparator}id), in a world.");
                return;
            }
            var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || zdo.GetPrefab() != ZoneModeChunkGenerator.PrefabHash)
            {
                Say($"{Tag} {SeeArg} {words[1]} found=no");
                return;
            }
            var nview = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            Say($"{Tag} {SeeArg} {words[1]} found=yes recipe={ZoneModeChunkGenerator.CurrentRecipe(zdo).Id} "
                + $"loaded={ZoneModeChunkGenerator.Loaded(zdo)} burned={ZoneModeChunkGenerator.Burned(zdo)} owner={zdo.GetOwner()} look={LookOf(nview)}");
        }

        // Adds fuel the machine's recipe burns to that exact machine: from this player's own stacks through the fuel
        // window's call, or, for an admin carrying none, the same request with test fuel.
        private static void Add(List<string> words)
        {
            var me = Player.m_localPlayer;
            if (me == null || ZDOMan.instance == null || words.Count < 3 || !TryParseId(words[1], out var id)
                || !int.TryParse(words[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
            {
                Say($"{Tag} {AddArg} needs a machine id and a count, in a world.");
                return;
            }
            var zdo = ZDOMan.instance.GetZDO(id);
            var nview = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            var machine = nview != null && ZoneModeChunkGenerator.IsOurs(nview) ? nview.GetComponent<Smelter>() : null;
            if (machine == null)
            {
                Say($"{Tag} {AddArg} {words[1]} found=no");
                return;
            }
            if (Vector3.Distance(me.transform.position, machine.transform.position) > RemoteReach)
            {
                Say($"{Tag} {AddArg} {words[1]} refused=farther_than_{RemoteReach:0}m");
                return;
            }
            foreach (var carried in ZoneModeChunkGenerator.CarriedFuel(ZoneModeChunkGenerator.CurrentRecipe(zdo), me))
            {
                if (carried.Value < amount) continue;
                int added = ZoneModeChunkGenerator.AddFuel(machine, me, carried.Key, amount);
                Say($"{Tag} {AddArg} {words[1]} added={added} item={carried.Key.name} via=inventory");
                return;
            }
            if (ZNet.instance == null || !ZNet.instance.LocalPlayerIsAdminOrHost() || !TryTestFuel(out string fuel))
            {
                Say($"{Tag} {AddArg} {words[1]} refused=carry_{amount}_fuel_or_be_an_admin");
                return;
            }
            ZoneModeChunkGenerator.LoadForTest(machine, fuel, amount);
            Say($"{Tag} {AddArg} {words[1]} added={amount} item={fuel} via=admin_test_fuel");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────
        // A batch-mode client (the headless autoplay bot) draws nothing either, and the look builds no rune mask there.
        private static bool NullGpu => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || Application.isBatchMode;

        private static bool Is(string word, string arg) => string.Equals(word, arg, StringComparison.OrdinalIgnoreCase);

        private static ZDO Zdo() => s_machine != null ? s_machine.GetComponent<ZNetView>()?.GetZDO() : null;

        private static ChunkTier Recipe() => ZoneModeChunkGenerator.CurrentRecipe(Zdo());

        private static int Held(ZDO zdo) => ZoneModeChunkGenerator.Loaded(zdo) + ZoneModeChunkGenerator.Burned(zdo);

        private static ZoneModeChunkGeneratorLook.GeneratorGlow Glow() => s_machine.GetComponent<ZoneModeChunkGeneratorLook.GeneratorGlow>();

        private static Switch SwitchOf(GameObject machine, string child) => machine.transform.Find(child)?.GetComponent<Switch>();

        private static bool HaveMachine(Step step)
        {
            if (s_machine != null && Zdo() != null) return true;
            step.Skip("no test machine (place did not pass)");
            return false;
        }

        private static bool HaveFuel(Step step, out string fuel)
        {
            if (TryTestFuel(out fuel)) return true;
            step.Fail("no fuel the plain recipe burns exists in this game");
            return false;
        }

        // The first fuel of the earliest biome that this game has: every recipe up to that biome's burns it.
        private static bool TryTestFuel(out string fuel)
        {
            foreach (string name in ZoneModeChunkTiers.Burns(ZoneModeChunkTiers.Starter))
                if (ZoneModeChunkGenerator.TryGetFuel(name, out _)) { fuel = name; return true; }
            fuel = null;
            return false;
        }

        private static Renderer Halo(ZoneModeChunkGeneratorLook.GeneratorGlow glow)
        {
            if (glow != null)
                foreach (var renderer in glow.Halos)
                    if (renderer != null) return renderer;
            return null;
        }

        private static bool HasRunes(Renderer halo)
            => halo != null && halo.enabled && halo.sharedMaterial != null && ZoneModeChunkGeneratorLook.RuneMask != null
               && halo.sharedMaterial.GetTexture(ZoneModeChunkGeneratorLook.MainTex) == ZoneModeChunkGeneratorLook.RuneMask;

        private static string LookOf(ZNetView nview)
        {
            if (NullGpu) return "nogpu";
            var halo = nview != null ? Halo(nview.GetComponent<ZoneModeChunkGeneratorLook.GeneratorGlow>()) : null;
            if (halo == null) return "none";
            if (!halo.enabled) return "hidden";
            return halo.sharedMaterial != null ? halo.sharedMaterial.name.Replace(' ', '_') : "bare";
        }

        private static float Difference(Color a, Color b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));

        private static IEnumerator WaitFor(Func<bool> done, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!done() && Time.realtimeSinceStartup < until) yield return null;
        }

        // ── Drops the machine sets down (refunds, seeds), remembered so they are removed ─────────────────────────
        private static HashSet<ZDOID> DropsNear(Vector3 at)
        {
            var ids = new HashSet<ZDOID>();
            foreach (var drop in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
            {
                var zdo = drop.GetComponent<ZNetView>()?.GetZDO();
                if (zdo != null && (drop.transform.position - at).sqrMagnitude <= DropRadius * DropRadius) ids.Add(zdo.m_uid);
            }
            return ids;
        }

        private static List<ItemDrop> NewDrops(HashSet<ZDOID> before, Vector3 at)
        {
            var fresh = new List<ItemDrop>();
            foreach (var drop in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
            {
                var zdo = drop.GetComponent<ZNetView>()?.GetZDO();
                if (zdo == null || before.Contains(zdo.m_uid) || (drop.transform.position - at).sqrMagnitude > DropRadius * DropRadius) continue;
                fresh.Add(drop);
                Remember(zdo);
            }
            return fresh;
        }

        private static int Stacks(List<ItemDrop> drops)
        {
            int total = 0;
            foreach (var drop in drops) total += drop.m_itemData.m_stack;
            return total;
        }

        // ── Chat with the bot ────────────────────────────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Chat), nameof(Chat.OnNewChatMessage))]
        private static class Chat_OnNewChatMessage_GeneratorTest
        {
            private static void Postfix(long senderID, string text)
            {
                if (!s_listening || string.IsNullOrEmpty(text)) return;
                string line = text.Trim();
                if (line.StartsWith(BotReplyPrefix, StringComparison.OrdinalIgnoreCase))
                    s_replies.Add(new KeyValuePair<long, string>(senderID, line.Substring(BotReplyPrefix.Length).Trim()));
            }
        }

        private static void Send(string line)
        {
            s_replies.Clear();
            try { Chat.instance?.SendText(Talker.Type.Shout, BotVerbPrefix + line); }
            catch (Exception ex) { Record($"could not shout '{line}': {ex.Message}"); }
        }

        private static IEnumerator AwaitReply(Func<string, bool> match)
        {
            s_got = null;
            s_gotFrom = 0;
            float until = Time.realtimeSinceStartup + AnswerSeconds;
            while (Time.realtimeSinceStartup < until)
            {
                foreach (var reply in s_replies)
                {
                    if (s_botUid != 0 && reply.Key != s_botUid) continue;
                    if (!match(reply.Value)) continue;
                    s_got = reply.Value;
                    s_gotFrom = reply.Key;
                    Record($"bot: {reply.Value}");
                    yield break;
                }
                yield return null;
            }
        }

        private static Dictionary<string, string> Fields(string line)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (line == null) return fields;
            foreach (Match match in Field.Matches(line)) fields[match.Groups[1].Value] = match.Groups[2].Value;
            return fields;
        }

        private static string Value(Dictionary<string, string> fields, string key)
            => fields != null && fields.TryGetValue(key, out string value) ? value : "?";

        private static int Sum(Dictionary<string, string> seen)
            => int.TryParse(Value(seen, "loaded"), out int loaded) && int.TryParse(Value(seen, "burned"), out int burned) ? loaded + burned : -1;

        private static bool Matches(Dictionary<string, string> seen, ZDO zdo)
            => seen != null && Value(seen, "found") == "yes" && Value(seen, "recipe") == Recipe().Id && Sum(seen) == Held(zdo)
               && Value(seen, "owner") == zdo.GetOwner().ToString(CultureInfo.InvariantCulture);

        // ── Spawned objects: marked, recorded, and swept server-side before the next run ─────────────────────────
        private static string Id(ZDOID id) => $"{id.UserID.ToString(CultureInfo.InvariantCulture)}{IdSeparator}{id.ID.ToString(CultureInfo.InvariantCulture)}";

        private static bool TryParseId(string text, out ZDOID id)
        {
            id = ZDOID.None;
            int split = text.IndexOf(IdSeparator);
            if (split <= 0 || !long.TryParse(text.Substring(0, split), NumberStyles.Integer, CultureInfo.InvariantCulture, out long user)
                || !uint.TryParse(text.Substring(split + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number)) return false;
            id = new ZDOID(user, number);
            return true;
        }

        // Every object the test spawns or has set down carries a marker saved with the world: ZDO ids are renumbered at
        // each world load on 1.0, so after a crash and a dedi restart only the marker still finds them.
        private static void Remember(ZDO zdo)
        {
            if (zdo == null) return;
            if (zdo.IsOwner()) zdo.Set(MarkerHash, true);
            if (s_spawned.Contains(zdo.m_uid)) return;
            s_spawned.Add(zdo.m_uid);
            var at = zdo.GetPosition();
            string prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab())?.name : null;
            s_records.Add(string.Join(RecordSeparator, Id(zdo.m_uid), prefab ?? zdo.GetPrefab().ToString(CultureInfo.InvariantCulture),
                at.x.ToString("0", CultureInfo.InvariantCulture), at.z.ToString("0", CultureInfo.InvariantCulture)));
            SaveSpawned();
        }

        private static string SpawnedPath()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "none";
            foreach (char bad in Path.GetInvalidFileNameChars()) world = world.Replace(bad, '_');
            return Path.Combine(Paths.BepInExRootPath, ResultsFolder, string.Format(CultureInfo.InvariantCulture, SpawnedFile, world));
        }

        // Registered on each new ZRoutedRpc (every world load), on the server and every client.
        internal static void RegisterRpc()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == s_rpcHost) return;
            s_rpcHost = rpc;
            rpc.Register(CleanRpc, RPC_Clean);
            rpc.Register<int>(CleanedRpc, (sender, removed) => s_cleaned = removed);
        }

        // Before anything is placed the server removes every marked object from earlier runs, loaded zone or not, and
        // answers with the count; the run waits for it, so the sweep never meets this run's machine.
        private static IEnumerator RequestClean()
        {
            s_cleaned = -1;
            if (ZRoutedRpc.instance == null) yield break;
            ZRoutedRpc.instance.InvokeRoutedRPC(CleanRpc);
            float until = Time.realtimeSinceStartup + CleanWaitSeconds;
            while (s_cleaned < 0 && Time.realtimeSinceStartup < until) yield return null;
            Record(s_cleaned < 0
                ? $"clean: the server did not answer within {CleanWaitSeconds:0} s (its FAT may predate the marker)"
                : $"clean: the server removed {s_cleaned} marked object(s) left by earlier runs");
        }

        private static void RPC_Clean(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;
            if (sender != ZDOMan.GetSessionID() && !AdminSyncing.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[FiresZones] {Command} clean from a non-admin ({sender}) refused.");
                return;
            }
            var host = VerdantsAscent.FiresAdminTerrain.Instance;
            if (host != null) host.StartCoroutine(Sweep(sender));
        }

        // A few thousand objects a frame, so a big world's server never stalls on it.
        private static IEnumerator Sweep(long requester)
        {
            var all = new List<ZDO>(ZDOMan.instance.m_objectsByID.Values);
            int removed = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var zdo = all[i];
                if (zdo != null && zdo.IsValid() && zdo.GetBool(MarkerHash))
                {
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    ZDOMan.instance.DestroyZDO(zdo);
                    removed++;
                }
                if ((i + 1) % SweepPerFrame == 0) yield return null;
            }
            if (removed > 0) FiresLogger.LogInfo($"[FiresZones] {Command} clean: {removed} marked object(s) from earlier runs removed.");
            ZRoutedRpc.instance?.InvokeRoutedRPC(requester, CleanedRpc, removed);
        }

        private static void RemoveSpawned()
        {
            var zdo = Zdo();
            if (zdo != null && zdo.IsOwner()) ZoneModeChunkGenerator.EmptyForTest(zdo);
            int removed = 0;
            foreach (var id in s_spawned) if (Remove(id)) removed++;
            s_spawned.Clear();
            s_machine = null;
            Record($"removed {removed} object(s) this run spawned or had set down");
        }

        private static bool Remove(ZDOID id)
        {
            var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            if (zdo == null) return false;
            var nview = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (nview != null)
            {
                nview.ClaimOwnership();
                ZNetScene.instance.Destroy(nview.gameObject);
            }
            else
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            return true;
        }

        private static void SaveSpawned()
        {
            try
            {
                string path = SpawnedPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, s_records);
            }
            catch (Exception ex) { Record($"could not write the spawned list: {ex.Message}"); }
        }

        // ── Output ───────────────────────────────────────────────────────────────────────────────────────────────
        private static string ShotTarget(Vector3 point, float radius) =>
            "{" + string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0},{3:0.0}", point.x, point.y, point.z, radius) + "}";

        private static void Say(string line)
        {
            Log.LogMessage(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            Record(line);
        }

        private static void Record(string text)
        {
            try
            {
                string folder = Path.Combine(Paths.BepInExRootPath, ResultsFolder);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, ResultsFile), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
            catch (Exception ex) { Log.LogWarning($"{Tag} could not write {ResultsFile}: {ex.Message}"); }
        }

    }
}
