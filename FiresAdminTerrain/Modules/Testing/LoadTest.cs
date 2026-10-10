using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Testing
{
    // fat_load_test (FAT 0.2.736; Fire 10-05 22:3x via the lead: "automated tests for the easy stuff", every round). Read-only,
    // a few seconds, on the tester's client; the server answers over FiresLoad_TestAsk with the same snapshot of its state:
    //   load    the server applied a world profile, froze its water guards (unless the profile has no rivers/lakes) and holds
    //           an island set; the cave content version and the zone mode are listed
    //   log     FAT said no Warning and no Error this process, on the server and on this client (the first ones are quoted)
    //   config  FAT's config is bound on both (entries, how many differ from their defaults)
    //   save    the server holds no object past the dedi's parking radius (100 km) and owns the world's lock
    //   sync    this client agrees with the server: FAT version and DLL hash, profile, island set, water guards, cave content
    //           version, zone-mode open count
    // It changes nothing.  fat_load_test run   (bare: every step SKIPs)
    internal static class LoadTest
    {
        private const string Command = "fat_load_test";
        private const string Tag = "[FatLoadTest]";
        private const string Pass = "PASS", Fail = "FAIL", Skip = "SKIP";
        private const string ModName = global::VerdantsAscent.FiresAdminTerrain.PluginName;
        private const string ModVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
        private const string AskRpc = "FiresLoad_TestAsk", ReplyRpc = "FiresLoad_TestReply";
        private const float ReplyWait = 8f, HashWaitSeconds = 5f;
        private static readonly string[] Steps = { "load", "log", "config", "save", "sync" };
        private static readonly string[] SyncKeys = { "version", "profile", "islands", "waterguards", "caves", "zones" };

        private static bool s_registered, s_running;
        private static int s_step, s_passes, s_fails, s_skips;
        private static ZRoutedRpc s_rpcOn;
        private static Dictionary<string, string> s_server;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                "[F5 test] FAT's load, log, config, save and client/server sync checks: read-only, a few seconds. Args: run. "
                + "Bare: every step SKIPs.",
                args =>
                {
                    if (s_running) { Say($"{Tag} a run is already going."); return; }
                    if (ZNet.instance == null || Player.m_localPlayer == null) { Say($"{Tag} join a world first."); return; }
                    bool go = args.Length > 1 && string.Equals(args[1], "run", StringComparison.OrdinalIgnoreCase);
                    ZNet.instance.StartCoroutine(Run(go));
                });
        }

        // From FAT's ZNet.Awake hook on every peer: the server answers with its snapshot.
        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_rpcOn, rpc)) return;
            try
            {
                rpc.Register(AskRpc, new Action<long>(RPC_Ask));
                rpc.Register<ZPackage>(ReplyRpc, RPC_Reply);
                s_rpcOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogDebug($"[FiresZones] fat_load_test RPC register failed: {ex.Message}"); }
        }

        private static void RPC_Ask(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var pkg = new ZPackage();
            var snapshot = Snapshot(true);
            pkg.Write(snapshot.Count);
            foreach (var kv in snapshot) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyRpc, pkg);
        }

        private static void RPC_Reply(long sender, ZPackage pkg)
        {
            int n = pkg.ReadInt();
            var map = new Dictionary<string, string>(n);
            for (int i = 0; i < n; i++) map[pkg.ReadString()] = pkg.ReadString();
            s_server = map;
        }

        // The same keys on every peer; the server adds what only it holds.
        private static Dictionary<string, string> Snapshot(bool server)
        {
            var inv = CultureInfo.InvariantCulture;
            var profile = FiresWorldProfile.Current;
            var s = new Dictionary<string, string>
            {
                ["version"] = $"{ModVersion} {FatDllHash.Value}",
                ["profile"] = profile != null ? profile.Describe() : "none",
                ["hydrology"] = profile?.Hydrology == false ? "off" : "on",
                ["islands"] = $"{FiresSkyIslands.IslandCount} {FiresSkyIslands.ActiveSetHash}",
                ["waterguards"] = FiresWaterGuards.Known ? FiresWaterGuards.Hash.ToString("X8", inv) : "none",
                ["caves"] = VoxelCaveContent.ContentVersion.ToString(inv),
                ["zones"] = ZoneModeRegistry.ModeActive ? ZoneModeRegistry.OpenCount.ToString(inv) + " open" : "whole world",
                ["warnings"] = FiresLogger.Warnings.ToString(inv),
                ["errors"] = FiresLogger.Errors.ToString(inv),
                ["first"] = string.Join(" || ", FiresLogger.FirstProblems().Select(Short).ToArray()),
            };
            var cfg = global::VerdantsAscent.FiresAdminTerrain.Instance?.Config;
            if (cfg != null)
            {
                int changed = 0;
                foreach (var kv in cfg) if (kv.Value != null && !Equals(kv.Value.BoxedValue, kv.Value.DefaultValue)) changed++;
                s["config"] = $"{cfg.Count} {changed}";
            }
            else s["config"] = "none";
            if (server)
            {
                s["parked"] = OffMapZones.ParkedCount().ToString(inv);
                s["lock"] = FiresWorldLoadGuard.OwnsLock ? "owned" : "not owned";
                s["dedicated"] = FiresCore.Lifecycle.FiresMod.IsDedicatedServer ? "yes" : "no";
            }
            return s;
        }

        private static IEnumerator Run(bool go)
        {
            s_running = true;
            s_step = s_passes = s_fails = s_skips = 0;
            float started = Time.realtimeSinceStartup;
            try
            {
                float hashUntil = Time.realtimeSinceStartup + HashWaitSeconds;
                while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
                var me = Player.m_localPlayer;
                string world = ZNet.World != null ? ZNet.World.m_name : "?";
                Say($"{Tag} BEGIN {ModName} {ModVersion} ({FatDllHash.Value}) on client '{me?.GetPlayerName()}' world '{world}' "
                    + $"{(go ? "run" : "bare")}: {Steps.Length} steps, ~{(go ? 10 : 1)} s");
                if (!go) { foreach (string st in Steps) Step(st, Skip, $"on demand: '{Command} run'"); yield break; }

                var mine = Snapshot(ZNet.instance.IsServer());
                s_server = null;
                if (ZNet.instance.IsServer()) s_server = Snapshot(true);
                else
                {
                    EnsureRpcs();
                    ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, AskRpc);
                    float until = Time.realtimeSinceStartup + ReplyWait;
                    while (s_server == null && Time.realtimeSinceStartup < until) yield return null;
                }
                var sv = s_server;
                if (sv == null)
                {
                    string why = $"the server sent no snapshot in {ReplyWait:0} s (is FAT {ModVersion} on it?)";
                    Step("load", Fail, why);
                    Step("log", mine["warnings"] == "0" && mine["errors"] == "0" ? Pass : Fail, "client: " + LogDetail(mine) + "; server: no reply");
                    foreach (string st in new[] { "config", "save", "sync" }) Step(st, Skip, "no server snapshot");
                    yield break;
                }

                // load
                bool profiled = Get(sv, "profile") != "none";
                bool guards = Get(sv, "hydrology") == "off" || Get(sv, "waterguards") != "none";
                Step("load", profiled && guards ? Pass : Fail,
                    $"server: profile {Get(sv, "profile")}; water guards {Get(sv, "waterguards")}{(Get(sv, "hydrology") == "off" ? " (no rivers/lakes in this profile)" : "")}; "
                    + $"islands {Get(sv, "islands")}; cave content v{Get(sv, "caves")}; zones {Get(sv, "zones")}");

                // log
                bool clean = Get(sv, "warnings") == "0" && Get(sv, "errors") == "0" && mine["warnings"] == "0" && mine["errors"] == "0";
                Step("log", clean ? Pass : Fail, "server: " + LogDetail(sv) + "; client: " + LogDetail(mine));

                // config
                bool bound = Get(sv, "config") != "none" && mine["config"] != "none" && !Get(sv, "config").StartsWith("0 ") && !mine["config"].StartsWith("0 ");
                Step("config", bound ? Pass : Fail, $"server: {ConfigDetail(Get(sv, "config"))}; client: {ConfigDetail(mine["config"])}");

                // save
                bool noParked = Get(sv, "parked") == "0";
                bool locked = Get(sv, "lock") == "owned";
                Step("save", noParked && locked ? Pass : Fail,
                    $"server: {Get(sv, "parked")} object(s) past the parking radius (want 0); world lock {Get(sv, "lock")} (dedicated {Get(sv, "dedicated")})");

                // sync
                var differ = SyncKeys.Where(k => Get(sv, k) != Get(mine, k)).ToList();
                Step("sync", differ.Count == 0 ? Pass : Fail, differ.Count == 0
                    ? "client = server on " + string.Join(", ", SyncKeys)
                    : string.Join("; ", differ.Select(k => $"{k}: client '{Get(mine, k)}' vs server '{Get(sv, k)}'").ToArray()));
            }
            finally
            {
                Say($"{Tag} END {(s_fails > 0 ? Fail : Pass)}: {s_passes} pass, {s_fails} fail, {s_skips} skip in {Time.realtimeSinceStartup - started:0.0} s");
                s_running = false;
            }
        }

        private static string Get(Dictionary<string, string> map, string key) => map.TryGetValue(key, out string v) ? v : "?";

        private static string LogDetail(Dictionary<string, string> s)
        {
            string first = Get(s, "first");
            return $"{Get(s, "warnings")} warning(s), {Get(s, "errors")} error(s)" + (string.IsNullOrEmpty(first) ? "" : $" [{first}]");
        }

        private static string ConfigDetail(string value)
        {
            var parts = value.Split(' ');
            return parts.Length == 2 ? $"{parts[0]} entries bound, {parts[1]} changed from default" : value;
        }

        private static string Short(string line)
        {
            string s = (line ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > 140 ? s.Substring(0, 140) + "..." : s;
        }

        private static void Step(string name, string result, string detail)
        {
            s_step++;
            if (result == Pass) s_passes++;
            else if (result == Fail) s_fails++;
            else s_skips++;
            Say($"{Tag} {s_step}/{Steps.Length} {name} {result} - {detail}");
        }

        private static void Say(string line)
        {
            global::VerdantsAscent.FiresAdminTerrain.Log?.LogMessage(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, Command + ".txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture) + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
