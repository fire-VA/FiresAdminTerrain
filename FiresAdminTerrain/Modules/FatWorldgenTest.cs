using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;
using VerdantsAscent.Terrain.BcImport;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain
{
    // `worldgen_test`, FAT's part of Fire's F5 test contract (Tools\FIRES_F5_TESTS.md): the BetterContinents data and its
    // decode, the zone state handshake, the ground under the tester and one parked zone read back. Every step only READS,
    // so it spawns nothing and needs no cleanup. An open -> close round trip is not one of them: opening runs vanilla zone
    // generation for good and closing parks what spawned, which cannot be undone (Tools\FIRES_TEAM_COORDINATION.md,
    // decisions for Fire). The tester asks every peer at once through one routed RPC. Each answers with its own facts, the
    // server also reads a parked zone back and files its facts in its own results file, and the tester compares them all
    // (the server, itself and every other player, e.g. the test bot) and prints and files every step line.
    internal static class FatWorldgenTest
    {
        private const string Command = "worldgen_test";
        private const string Tag = "[WorldgenTest]";
        private const string ResultsFolder = "FiresTests";
        private const string ResultsFile = "worldgen_test.txt";
        private const string RpcAsk = "FiresAdminTerrain_WorldgenTestAsk";
        private const string RpcAnswer = "FiresAdminTerrain_WorldgenTestAnswer";
        private const string BotArgument = "-firesbot";
        private const string ServerRole = "server";
        private const string ListArgument = "list";
        private const string Pass = "PASS", Fail = "FAIL", Skip = "SKIP";
        private const float EstimateSeconds = 15f;
        private const float ServerWaitSeconds = 10f;
        private const float SettleWaitSeconds = 20f;
        private const int AnswerFormat = 3;
        private const string ModName = global::VerdantsAscent.FiresAdminTerrain.PluginName;
        private const string ModVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;

        private static readonly string[] Steps =
            { "bc_data", "bc_decode", "server_bc", "peers_bc", "zone_state", "zone_terrain", "park_read" };

        private static readonly Dictionary<long, PeerFacts> s_answers = new Dictionary<long, PeerFacts>();
        private static bool s_registered;
        private static bool s_running;
        private static ZRoutedRpc s_rpcOn;
        private static long s_serverId;

        // What one peer knows, sent back in one package; only the server fills the park fields.
        private sealed class PeerFacts
        {
            internal string Role;
            internal string PlayerName;
            internal int ReadyPeers;
            internal string Version;
            internal bool BetterContinents;
            internal string Source;
            internal int FileFormat;
            internal bool RestoredInBands;
            internal int BandedMaps;
            internal int PngMaps;
            internal bool Ready;
            internal long ReadMs;
            internal long DecodeMs;
            internal bool ZoneMode;
            internal int ZoneVersion;
            internal int OpenCount;
            internal int ParkedCount;
            internal Vector2s ParkZone;
            internal int ParkRead;
            internal int ParkPiecesRead;
            internal int ParkPiecesIndexed;
            internal long ParkReadMs;
        }

        // From FAT's one Terminal.InitTerminal hook (FatVanillaHooks).
        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                "FAT's F5 test, read-only: BetterContinents data + decode, zone handshake, the ground you stand on, a parked zone "
                + "read back on the server. Args: list | <step..>.",
                args => Start(args.Context, args.Args.Skip(1).ToArray()));
        }

        // From FAT's one ZNet.Awake hook (FatVanillaHooks).
        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_rpcOn, rpc)) return;
            rpc.Register<ZPackage>(RpcAsk, OnAsk);
            rpc.Register<ZPackage>(RpcAnswer, (sender, pkg) =>
            {
                var facts = ReadFacts(pkg);
                if (facts != null) s_answers[sender] = facts;
            });
            s_rpcOn = rpc;
        }

        private static void Start(Terminal console, string[] args)
        {
            if (args.Contains(ListArgument))
            {
                for (int i = 0; i < Steps.Length; i++) Say(console, $"{Tag} {i + 1}/{Steps.Length} {Steps[i]}");
                return;
            }
            if (s_running) { Say(console, $"{Tag} a run is already going."); return; }
            if (ZNet.instance == null || Player.m_localPlayer == null) { Say(console, $"{Tag} join a world first."); return; }
            var chosen = Steps.Where(step => args.Length == 0 || args.Contains(step)).ToArray();
            if (chosen.Length == 0) { Say(console, $"{Tag} no such step; '{Command} {ListArgument}' lists them."); return; }
            ZNet.instance.StartCoroutine(Run(console, chosen));
        }

        private static IEnumerator Run(Terminal console, string[] steps)
        {
            s_running = true;
            s_answers.Clear();
            float started = Time.realtimeSinceStartup;
            int index = 0, passed = 0, failed = 0, skipped = 0;
            void Report(string step, string result, string detail)
            {
                index++;
                if (result == Pass) passed++;
                else if (result == Fail) failed++;
                else skipped++;
                Write(console, $"{Tag} {index}/{steps.Length} {step} {result} - {detail}");
            }

            try
            {
                Write(console, $"{Tag} BEGIN {ModName} {ModVersion} ({FatDllHash.Value}) on {Role()} "
                    + $"'{Player.m_localPlayer?.GetPlayerName() ?? "?"}' world '{ZNet.instance?.GetWorldName() ?? "?"}': "
                    + $"{steps.Length} steps, ~{EstimateSeconds:0} s");
                AskPeers();
                foreach (string step in steps)
                {
                    if (NeedsAnswers(step))
                        while ((Server() == null || OtherClients().Count < ExpectedOtherClients())
                               && Time.realtimeSinceStartup - started < ServerWaitSeconds)
                            yield return null;
                    if (step == "zone_terrain")
                    {
                        float waited = Time.realtimeSinceStartup;
                        Vector2s zone = ZoneSystem.GetZone(Player.m_localPlayer.transform.position);
                        while (!VoxelZoneStreamer.IsZoneTerrainSettled(zone) && Time.realtimeSinceStartup - waited < SettleWaitSeconds)
                            yield return null;
                    }
                    string result, detail;
                    try { Check(step, out result, out detail); }
                    catch (Exception ex)
                    {
                        result = Fail;
                        detail = $"threw {ex.GetType().Name}: {ex.Message}";
                    }
                    Report(step, result, detail);
                }
            }
            finally
            {
                Write(console, $"{Tag} END {(failed > 0 ? Fail : Pass)}: {passed} pass, {failed} fail, {skipped} skip in "
                    + $"{Time.realtimeSinceStartup - started:0} s");
                s_running = false;
            }
        }

        private static bool NeedsAnswers(string step) => step == "server_bc" || step == "peers_bc" || step == "zone_state" || step == "park_read";

        private static PeerFacts Server() => s_answers.TryGetValue(s_serverId, out var facts) ? facts : null;

        // The other players' own answers: every answer but the server's and this peer's.
        private static List<KeyValuePair<long, PeerFacts>> OtherClients()
        {
            long me = ZNet.GetUID();
            return s_answers.Where(answer => answer.Key != s_serverId && answer.Key != me).ToList();
        }

        // The server's own count of joined connections, less this peer when it is one of them. The client's player list was the
        // first try and counted one too many (R2, 04:2x: "1 of 2 other player(s) did not answer" with only the bot there).
        private static int ExpectedOtherClients()
        {
            var server = Server();
            if (server == null || ZNet.instance == null) return 0;
            return Math.Max(0, server.ReadyPeers - (ZNet.instance.IsServer() ? 0 : 1));
        }

        private static void Check(string step, out string result, out string detail)
        {
            switch (step)
            {
                case "bc_data": CheckBcData(BcMigratedWorld.LastLoad, "", out result, out detail); return;
                case "bc_decode": CheckBcDecode(out result, out detail); return;
                case "server_bc": CheckServerBc(out result, out detail); return;
                case "peers_bc": CheckPeersBc(out result, out detail); return;
                case "zone_state": CheckZoneState(out result, out detail); return;
                case "zone_terrain": CheckZoneTerrain(out result, out detail); return;
                default: CheckParkRead(out result, out detail); return;
            }
        }

        private static void CheckBcData(BcMigratedWorld.LoadFacts facts, string who, out string result, out string detail)
        {
            if (facts.Source == "none") { result = Skip; detail = $"{who}not a BetterContinents world (or BetterContinents is installed)"; return; }
            string where = $"format {facts.FileFormat}{(facts.RestoredInBands ? ", re-stored in bands this load" : "")}, from {facts.Source}";
            if (facts.PngMaps > 0)
            {
                result = Fail;
                detail = $"{who}{facts.PngMaps} map(s) still PNG, {facts.BandedMaps} banded ({where}); a PNG map decodes on one core";
                return;
            }
            result = Pass;
            detail = $"{who}{facts.BandedMaps} map(s) banded ({where})";
        }

        private static void CheckBcDecode(out string result, out string detail)
        {
            var facts = BcMigratedWorld.LastLoad;
            if (facts.Source == "none") { result = Skip; detail = "not a BetterContinents world"; return; }
            if (!facts.Ready || !BcMigratedWorld.Active)
            {
                result = Fail;
                detail = "the maps did not decode, so this peer draws vanilla heights (see the [BcImport] errors)";
                return;
            }
            result = Pass;
            detail = $"read {facts.ReadMs} ms, decode {facts.DecodeMs} ms";
        }

        private static void CheckServerBc(out string result, out string detail)
        {
            var server = Server();
            if (server == null) { result = Skip; detail = NoAnswer(); return; }
            CheckPeerBc(server, "server: ", out result, out detail);
        }

        // Every other player's own BetterContinents data and decode, as that peer answered.
        private static void CheckPeersBc(out string result, out string detail)
        {
            if (Server() == null) { result = Skip; detail = NoAnswer(); return; }
            var others = OtherClients();
            int expected = ExpectedOtherClients();
            if (expected == 0 && others.Count == 0) { result = Skip; detail = "no other player is in this world"; return; }
            var lines = new List<string>();
            var results = new List<string>();
            if (others.Count < expected) lines.Add($"{expected - others.Count} of {expected} other player(s) did not answer");
            foreach (var other in others)
            {
                CheckPeerBc(other.Value, $"{Who(other.Value)}: ", out string peerResult, out string peerDetail);
                results.Add(peerResult);
                lines.Add(peerDetail);
            }
            result = others.Count < expected || results.Contains(Fail) ? Fail : results.All(r => r == Skip) ? Skip : Pass;
            detail = string.Join("; ", lines);
        }

        private static void CheckPeerBc(PeerFacts peer, string who, out string result, out string detail)
        {
            var facts = new BcMigratedWorld.LoadFacts
            {
                Source = peer.BetterContinents ? peer.Source : "none",
                FileFormat = peer.FileFormat,
                RestoredInBands = peer.RestoredInBands,
                BandedMaps = peer.BandedMaps,
                PngMaps = peer.PngMaps,
            };
            CheckBcData(facts, who, out result, out detail);
            if (result != Pass) return;
            if (!peer.Ready)
            {
                result = Fail;
                detail += ", but its maps did not decode (vanilla heights there)";
                return;
            }
            detail += $", read {peer.ReadMs} ms, decode {peer.DecodeMs} ms";
        }

        // The zone state on this peer and every other player must equal the server's: the same version, the same open count.
        private static void CheckZoneState(out string result, out string detail)
        {
            var server = Server();
            bool here = ZoneModeRegistry.ModeActive;
            if (server == null)
            {
                result = Skip;
                detail = here ? $"v{ZoneModeRegistry.Version}, {ZoneModeRegistry.OpenCount} open here; {NoAnswer()}" : NoAnswer();
                return;
            }
            if (!here && !server.ZoneMode) { result = Skip; detail = "not a one-zone-at-a-time world"; return; }
            var peers = new List<KeyValuePair<string, PeerFacts>>
            {
                new KeyValuePair<string, PeerFacts>("this peer", new PeerFacts
                {
                    ZoneMode = here, ZoneVersion = ZoneModeRegistry.Version, OpenCount = ZoneModeRegistry.OpenCount,
                }),
            };
            peers.AddRange(OtherClients().Select(other => new KeyValuePair<string, PeerFacts>(Who(other.Value), other.Value)));
            var wrong = peers.Where(peer => peer.Value.ZoneMode != server.ZoneMode || peer.Value.ZoneVersion != server.ZoneVersion
                                            || peer.Value.OpenCount != server.OpenCount)
                             .Select(peer => $"{peer.Key} {(peer.Value.ZoneMode ? $"v{peer.Value.ZoneVersion}, {peer.Value.OpenCount} open" : "no zone mode")}")
                             .ToList();
            int expected = ExpectedOtherClients();
            string serverState = $"v{server.ZoneVersion}, {server.OpenCount} open";
            if (wrong.Count > 0)
            {
                result = Fail;
                detail = $"the server {serverState}, but {string.Join(", ", wrong)}";
                return;
            }
            result = Pass;
            detail = $"{serverState} on the server and on {peers.Count} player(s): {string.Join(", ", peers.Select(peer => peer.Key))}"
                + (peers.Count - 1 < expected ? $" ({expected - (peers.Count - 1)} other player(s) did not answer)" : "")
                + (ZoneModeRegistry.IsAuthority ? " (this peer is the server)" : "");
        }

        private static string Who(PeerFacts peer) => $"{peer.Role} '{peer.PlayerName}'";

        private static void CheckZoneTerrain(out string result, out string detail)
        {
            Vector3 at = Player.m_localPlayer.transform.position;
            Vector2s zone = ZoneSystem.GetZone(at);
            string where = $"zone ({zone.x},{zone.y})";
            if (ZoneModeRegistry.ModeActive && !ZoneModeRegistry.IsOpen(zone))
            {
                result = Skip;
                detail = $"you stand in closed {where}; stand on open land";
                return;
            }
            string ground = VoxelZoneStreamer.OwnsGround(at) ? "voxel ground" : "vanilla ground";
            if (!VoxelZoneStreamer.IsZoneTerrainSettled(zone))
            {
                result = Fail;
                detail = $"{where} ({ground}) is not settled after {SettleWaitSeconds:0} s: chunks still generating, meshing or without colliders";
                return;
            }
            result = Pass;
            detail = $"{where} is open and settled ({ground})";
        }

        private static void CheckParkRead(out string result, out string detail)
        {
            var server = Server();
            if (server == null) { result = Skip; detail = NoAnswer(); return; }
            if (!server.ZoneMode || server.ParkedCount == 0) { result = Skip; detail = "the server has no parked zones"; return; }
            string zone = $"({server.ParkZone.x},{server.ParkZone.y})";
            if (server.ParkRead < 0)
            {
                result = Fail;
                detail = $"the server could not read parked zone {zone} back ({server.ParkedCount} parked; see its [FiresZones] warning)";
                return;
            }
            if (server.ParkPiecesRead != server.ParkPiecesIndexed)
            {
                result = Fail;
                detail = $"parked zone {zone}: {server.ParkPiecesRead} built piece(s) read back, the index says {server.ParkPiecesIndexed}";
                return;
            }
            result = Pass;
            detail = $"{server.ParkedCount} parked; nearest {zone} read back {server.ParkRead} object(s), {server.ParkPiecesRead} built "
                + $"piece(s) as indexed, in {server.ParkReadMs} ms";
        }

        private static string NoAnswer() => $"the server did not answer in {ServerWaitSeconds:0} s (a FAT without worldgen_test there?)";

        // ── Every peer answers; the server also reads a parked zone back ────
        private static void AskPeers()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            s_serverId = rpc.GetServerPeerID();
            Vector3 at = Player.m_localPlayer.transform.position;
            var pkg = new ZPackage();
            pkg.Write(AnswerFormat);
            pkg.Write(at.x);
            pkg.Write(at.z);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcAsk, pkg);
        }

        private static void OnAsk(long sender, ZPackage ask)
        {
            if (ZNet.instance == null || ask.ReadInt() != AnswerFormat) return;
            var from = new Vector3(ask.ReadSingle(), 0f, ask.ReadSingle());
            var facts = Gather(from);
            if (ZNet.instance.IsServer())
                AppendResults($"{Tag} server: asked by {sender}; BetterContinents {(facts.BetterContinents ? $"{facts.BandedMaps} banded / {facts.PngMaps} PNG, format {facts.FileFormat}, read {facts.ReadMs} ms, decode {facts.DecodeMs} ms" : "no")}; "
                    + $"zone mode {(facts.ZoneMode ? $"v{facts.ZoneVersion}, {facts.OpenCount} open, {facts.ParkedCount} parked" : "no")}"
                    + (facts.ParkedCount > 0 ? $"; park read ({facts.ParkZone.x},{facts.ParkZone.y}): {facts.ParkRead} object(s), {facts.ParkPiecesRead}/{facts.ParkPiecesIndexed} pieces, {facts.ParkReadMs} ms" : ""));
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcAnswer, WriteFacts(facts));
        }

        private static PeerFacts Gather(Vector3 near)
        {
            var load = BcMigratedWorld.LastLoad;
            var facts = new PeerFacts
            {
                Role = Role(),
                PlayerName = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "",
                ReadyPeers = ZNet.instance.IsServer() ? ZNet.instance.GetPeers().Count(peer => peer.IsReady()) : 0,
                Version = ModVersion,
                BetterContinents = load.Source != "none",
                Source = load.Source,
                FileFormat = load.FileFormat,
                RestoredInBands = load.RestoredInBands,
                BandedMaps = load.BandedMaps,
                PngMaps = load.PngMaps,
                Ready = load.Ready && BcMigratedWorld.Active,
                ReadMs = load.ReadMs,
                DecodeMs = load.DecodeMs,
                ZoneMode = ZoneModeRegistry.ModeActive,
                ZoneVersion = ZoneModeRegistry.Version,
                OpenCount = ZoneModeRegistry.OpenCount,
                ParkedCount = ZoneModePark.ParkedCount,
            };
            if (facts.ParkedCount > 0 && ZNet.instance.IsServer()) ReadNearestParked(near, facts);
            return facts;
        }

        // The parked zone nearest the tester, read back one object at a time; its built pieces must match what the park's
        // index recorded when it was parked.
        private static void ReadNearestParked(Vector3 near, PeerFacts facts)
        {
            Vector2s origin = ZoneSystem.GetZone(near);
            Vector2s nearest = default;
            long best = long.MaxValue;
            foreach (var zone in ZoneModePark.ParkedZoneList())
            {
                long dx = zone.x - origin.x, dy = zone.y - origin.y;
                long distance = dx * dx + dy * dy;
                if (distance >= best) continue;
                best = distance;
                nearest = zone;
            }
            facts.ParkZone = nearest;
            facts.ParkPiecesIndexed = ZoneModePark.TryGetParked(nearest, out var parked) ? parked.Pieces : -1;
            int pieces = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            facts.ParkRead = ZoneModePark.TryReadZone(nearest, (zdo, portal) => { if (ZoneModeBuilds.IsBuilt(zdo)) pieces++; });
            facts.ParkReadMs = clock.ElapsedMilliseconds;
            facts.ParkPiecesRead = pieces;
        }

        private static ZPackage WriteFacts(PeerFacts facts)
        {
            var pkg = new ZPackage();
            pkg.Write(AnswerFormat);
            pkg.Write(facts.Role ?? "");
            pkg.Write(facts.PlayerName ?? "");
            pkg.Write(facts.ReadyPeers);
            pkg.Write(facts.Version ?? "");
            pkg.Write(facts.BetterContinents);
            pkg.Write(facts.Source ?? "");
            pkg.Write(facts.FileFormat);
            pkg.Write(facts.RestoredInBands);
            pkg.Write(facts.BandedMaps);
            pkg.Write(facts.PngMaps);
            pkg.Write(facts.Ready);
            pkg.Write(facts.ReadMs);
            pkg.Write(facts.DecodeMs);
            pkg.Write(facts.ZoneMode);
            pkg.Write(facts.ZoneVersion);
            pkg.Write(facts.OpenCount);
            pkg.Write(facts.ParkedCount);
            pkg.Write(facts.ParkZone.x);
            pkg.Write(facts.ParkZone.y);
            pkg.Write(facts.ParkRead);
            pkg.Write(facts.ParkPiecesRead);
            pkg.Write(facts.ParkPiecesIndexed);
            pkg.Write(facts.ParkReadMs);
            return pkg;
        }

        private static PeerFacts ReadFacts(ZPackage pkg)
        {
            if (pkg.ReadInt() != AnswerFormat) return null;
            return new PeerFacts
            {
                Role = pkg.ReadString(),
                PlayerName = pkg.ReadString(),
                ReadyPeers = pkg.ReadInt(),
                Version = pkg.ReadString(),
                BetterContinents = pkg.ReadBool(),
                Source = pkg.ReadString(),
                FileFormat = pkg.ReadInt(),
                RestoredInBands = pkg.ReadBool(),
                BandedMaps = pkg.ReadInt(),
                PngMaps = pkg.ReadInt(),
                Ready = pkg.ReadBool(),
                ReadMs = pkg.ReadLong(),
                DecodeMs = pkg.ReadLong(),
                ZoneMode = pkg.ReadBool(),
                ZoneVersion = pkg.ReadInt(),
                OpenCount = pkg.ReadInt(),
                ParkedCount = pkg.ReadInt(),
                ParkZone = new Vector2s(pkg.ReadShort(), pkg.ReadShort()),
                ParkRead = pkg.ReadInt(),
                ParkPiecesRead = pkg.ReadInt(),
                ParkPiecesIndexed = pkg.ReadInt(),
                ParkReadMs = pkg.ReadLong(),
            };
        }

        // ── Lines ───────────────────────────────────────────────────────────
        // Message level (the client's quiet mode drops Info from other mods), echoed to F5, and filed with a timestamp.
        private static void Write(Terminal console, string line)
        {
            Say(console, line);
            AppendResults(line);
        }

        private static void Say(Terminal console, string line)
        {
            global::VerdantsAscent.FiresAdminTerrain.Log?.LogMessage(line);
            try { console?.AddString(line); } catch { }
        }

        private static void AppendResults(string line)
        {
            try
            {
                string folder = Path.Combine(Paths.BepInExRootPath, ResultsFolder);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, ResultsFile), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{Tag} could not write {ResultsFile}: {ex.Message}"); }
        }

        private static string Role()
        {
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return ServerRole;
            return Array.IndexOf(Environment.GetCommandLineArgs(), BotArgument) >= 0 ? "bot" : "client";
        }
    }
}
