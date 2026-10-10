using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── CONSOLE COMMANDS ────────────────────────────────────────────────────
    //   fires_zones                     status, and the zone you stand in
    //   fires_zones_open [x y] [force]  admin: open the square you look at (or zone x,y)
    //   fires_zones_verify              check every loaded heightmap on THIS peer against the gate
    //                                   and, for open zones, against vanilla generation
    //   fires_zones_dump plan|zones [r] server: CSV of the location plan, or of every object in the
    //                                   zones within r of you — for diffing against a vanilla world
    //                                   made from the same seed
    //   fires_skylands                  the skylands nearest you
    //   fires_skylands_goto [n]         cheat: stand on top of the nth nearest skyland
    // Edits and dumps run on the server; a client sends them there and prints the reply.
    internal static class ZoneModeCommands
    {
        private const string CmdStatus = "fires_zones";
        private const string CmdOpen = "fires_zones_open";
        private const string CmdVerify = "fires_zones_verify";
        private const string CmdUndoChunk = "fires_zones_undo";
        private const string CmdDump = "fires_zones_dump";
        private const string CmdChunkSave = "fires_chunk_save";
        private const string CmdChunkList = "fires_chunk_list";
        private const string CmdChunkPlace = "fires_chunk_place";
        private const string CmdChunkUndo = "fires_chunk_undo";
        private const string CmdChunkRedo = "fires_chunk_redo";
        private const string CmdSkylands = "fires_skylands";
        private const string CmdSkylandGoto = "fires_skylands_goto";
        private const float SkylandListRange = 3000f;
        private const int SkylandListCount = 8;
        private const string RpcDumpRequest = "FiresAdminTerrain_ZoneDump";
        private const string RpcDumpReply = "FiresAdminTerrain_ZoneDumpReply";
        private const float WaterLevel = 30f;
        private const float LookReach = 600f;
        private const int DefaultDumpRadius = 1;
        private const int MaxDumpRadius = 6;
        private const string DiagFolder = "_diag";

        private static readonly AccessTools.FieldRef<Heightmap, HeightmapBuilder.HMBuildData> BuildDataRef =
            AccessTools.FieldRefAccess<Heightmap, HeightmapBuilder.HMBuildData>("m_buildData");
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsByIdRef =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        private static bool s_registered;
        private static ZRoutedRpc s_rpcOn;

        // From FAT's one ZNet.Awake hook (FatVanillaHooks).
        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_rpcOn, rpc)) return;
            rpc.Register<ZPackage>(RpcDumpRequest, RPC_DumpRequest);
            rpc.Register<string>(RpcDumpReply, (sender, message) =>
            {
                try { Console.instance?.AddString(message); } catch { }
                FiresLogger.LogInfo(message);
            });
            s_rpcOn = rpc;
        }

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;

            new Terminal.ConsoleCommand(CmdStatus, "One-zone-at-a-time status for this world, and the zone you stand in.",
                args => { foreach (string line in Status()) args.Context.AddString(line); });

            new Terminal.ConsoleCommand(CmdChunkSave,
                "Server: save the chunk you stand in - its land and everything on it - as a blueprint. Usage: "
                + CmdChunkSave + " <name> [x y] [overwrite].",
                args =>
                {
                    if (args.Args.Length < 2)
                    {
                        args.Context.AddString("[Zones] Usage: " + CmdChunkSave + " <name> [x y] [overwrite]");
                        return;
                    }
                    bool overwrite = args.Args.Any(a => string.Equals(a, "overwrite", StringComparison.OrdinalIgnoreCase));
                    Vector2s zone = args.Args.Length >= 4 && int.TryParse(args.Args[2], out int zx) && int.TryParse(args.Args[3], out int zy)
                        ? new Vector2s(zx, zy)
                        : ZoneSystem.GetZone(Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero);
                    ZoneModeChunkBlueprint.TrySave(zone, args.Args[1], overwrite, null, out string report);
                    args.Context.AddString("[Zones] " + report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                });

            new Terminal.ConsoleCommand(CmdChunkPlace,
                "Server, admin: drop a saved chunk on the zone you stand in, land and all. Everything already there is "
                + "removed first, and the land keeps the height it was saved at. Usage: " + CmdChunkPlace + " <name> [x y].",
                args =>
                {
                    if (args.Args.Length < 2)
                    {
                        args.Context.AddString("[Zones] Usage: " + CmdChunkPlace + " <name> [x y]");
                        return;
                    }
                    Vector2s zone = args.Args.Length >= 4 && int.TryParse(args.Args[2], out int zx) && int.TryParse(args.Args[3], out int zy)
                        ? new Vector2s(zx, zy)
                        : ZoneSystem.GetZone(Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero);
                    ZoneModeChunkBlueprint.TryPlace(zone, args.Args[1], out string report);
                    args.Context.AddString("[Zones] " + report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                }, isCheat: true);

            new Terminal.ConsoleCommand(CmdChunkUndo, "Server, admin: put the last chunk placement's zone back as it was.",
                args =>
                {
                    ZoneModeChunkBlueprint.TryUndo(out string report);
                    args.Context.AddString("[Zones] " + report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                }, isCheat: true);

            new Terminal.ConsoleCommand(CmdChunkRedo, "Server, admin: place again what the last undo took back.",
                args =>
                {
                    ZoneModeChunkBlueprint.TryRedo(out string report);
                    args.Context.AddString("[Zones] " + report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                }, isCheat: true);

            new Terminal.ConsoleCommand(CmdChunkList, "The chunk blueprints saved on this machine.",
                args =>
                {
                    int listed = 0;
                    foreach (string line in ZoneModeChunkBlueprint.List()) { args.Context.AddString("[Zones] " + line); listed++; }
                    if (listed == 0) args.Context.AddString("[Zones] No chunk blueprints saved yet (" + ZoneModeChunkBlueprint.Folder + ").");
                });

            new Terminal.ConsoleCommand(CmdSkylands, "The skylands nearest you: distance, height, size and whether a fall spills off the rim.",
                args => { foreach (string line in Skylands()) args.Context.AddString(line); });

            new Terminal.ConsoleCommand(CmdSkylandGoto, "Cheat: stand on top of the nth nearest skyland (default 1). Usage: " + CmdSkylandGoto + " [n]",
                args =>
                {
                    int nth = args.Length > 1 && int.TryParse(args[1], out int parsed) ? Mathf.Max(1, parsed) : 1;
                    args.Context.AddString(GotoSkyland(nth));
                }, isCheat: true);

            new Terminal.ConsoleCommand(CmdOpen,
                "Admin: open a closed zone. With no arguments, the square of sea you are looking at. Usage: "
                + CmdOpen + " [x y] [force] — force skips the must-border-an-open-zone rule.",
                args =>
                {
                    bool force = args.Args.Any(a => string.Equals(a, "force", StringComparison.OrdinalIgnoreCase));
                    Vector2s zone;
                    if (args.Args.Length >= 3 && int.TryParse(args.Args[1], out int zx) && int.TryParse(args.Args[2], out int zy))
                        zone = new Vector2s(zx, zy);
                    else if (!TryGetLookedAtZone(out zone))
                    {
                        args.Context.AddString("[Zones] Look at a square of sea, or give its zone: " + CmdOpen + " x y");
                        return;
                    }
                    ZoneModeOpening.RequestAdminOpen(args.Context, zone, force);
                });

            // UNDO A WRONG SEED. Two steps, because "parked" and "closed" are separate states and the
            // chunk only becomes visible again once BOTH are true: bring the set-aside chunk back to
            // parked, then close the zone so the replacement land is gone and it can be planted again.
            // The actual restoring is the ordinary matching-seed path - this just puts the world back
            // into the state where that path applies.
            new Terminal.ConsoleCommand(CmdUndoChunk,
                "Admin: undo a chunk overwritten by the wrong seed. Brings the set-aside chunk back and closes the "
                + "zone so you can plant the matching seed. With no arguments, the zone you are looking at. Usage: "
                + CmdUndoChunk + " [x y]",
                args =>
                {
                    if (!ZoneModeRegistry.ModeActive)
                    {
                        args.Context.AddString("[Zones] Only a one-zone-at-a-time world sets chunks aside.");
                        return;
                    }
                    Vector2s zone;
                    if (args.Args.Length >= 3 && int.TryParse(args.Args[1], out int ux) && int.TryParse(args.Args[2], out int uy))
                        zone = new Vector2s(ux, uy);
                    else if (!TryGetLookedAtZone(out zone))
                    {
                        args.Context.AddString("[Zones] Look at the zone you want back, or give it: " + CmdUndoChunk + " x y");
                        return;
                    }

                    if (!ZoneModePark.TryRecoverOverridden(zone, out string report))
                    {
                        args.Context.AddString("[Zones] " + report);
                        return;
                    }
                    // Closing is what removes the land that was raised over it. Without this the zone stays
                    // open with the wrong biome in it and the recovered chunk would never be seen.
                    ZoneModeOpening.Close(new[] { zone }, "undo of an overwritten chunk");
                    args.Context.AddString("[Zones] " + report);
                });

            new Terminal.ConsoleCommand(CmdVerify,
                "Check every loaded heightmap on this peer: open zones must equal vanilla generation exactly, closed "
                + "zones must be sea floor and coast. On the server, also checks that no closed zone was ever generated.",
                args => { foreach (string line in Verify()) args.Context.AddString(line); });

            new Terminal.ConsoleCommand(CmdDump,
                "Server: write a CSV of the location plan (" + CmdDump + " plan) or of every object in the zones "
                + "around you (" + CmdDump + " zones [radius]) to FiresWorldGen\\_diag, for diffing against a vanilla "
                + "world made from the same seed.",
                args =>
                {
                    string what = args.Args.Length >= 2 ? args.Args[1].ToLowerInvariant() : "";
                    if (what != "plan" && what != "zones")
                    {
                        args.Context.AddString("[Zones] Usage: " + CmdDump + " plan | zones [radius]");
                        return;
                    }
                    int radius = DefaultDumpRadius;
                    if (args.Args.Length >= 3 && int.TryParse(args.Args[2], out int parsed)) radius = Mathf.Clamp(parsed, 0, MaxDumpRadius);
                    Vector3 around = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    RequestDump(args.Context, what == "plan", ZoneSystem.GetZone(around), radius);
                });
        }

        // ── Status ──────────────────────────────────────────────────────────
        private static List<string> Status()
        {
            var lines = new List<string>();
            if (ZNet.World == null) { lines.Add("[Zones] Not in a world."); return lines; }
            if (!ZoneModeRegistry.ModeActive)
            {
                lines.Add($"[Zones] '{ZNet.World.m_name}' is a normal world, not one zone at a time.");
                return lines;
            }
            lines.Add($"[Zones] '{ZNet.World.m_name}': ONE ZONE AT A TIME, {(ZoneModeRegistry.Seeded ? "seeded" : "NOT seeded yet")}, "
                + $"{ZoneModeRegistry.OpenCount} zone(s) open, registry v{ZoneModeRegistry.Version}.");
            if (ZoneModeRegistry.IsAuthority)
            {
                lines.Add($"[Zones] Opened by: {ZoneModeRegistry.DescribeReasons()}. Location islands: "
                    + $"{ZoneModeIslands.IndexedLocations} planned, {ZoneModeIslands.RevealedLocations} revealed this session; "
                    + $"{ZoneModeOpening.PendingCount} opening(s) waiting for clients.");
                lines.Add($"[Zones] Registry file: {ZoneModeRegistry.RegistryPath(ZNet.World.m_name)}");
            }
            lines.Add($"[Zones] Coast {ZoneModeRegistry.CoastWidth:0.#} m, sea floor y={ZoneModeRegistry.SeaFloorY:0.#}; "
                + $"gate {(ZoneModeGate.Active ? "ACTIVE" : "inactive")}.");
            var player = Player.m_localPlayer;
            if (player != null)
            {
                var zone = ZoneSystem.GetZone(player.transform.position);
                ZoneModeRegistry.TryDescribeZone(zone, out string state);
                lines.Add($"[Zones] You are in zone ({zone.x},{zone.y}): {state}.");
            }
            return lines;
        }

        // ── Skylands ────────────────────────────────────────────────────────
        private static List<int> NearestSkylands(Vector3 from)
        {
            var ids = new List<uint>();
            var indices = new List<int>();
            VerdantsAscent.Terrain.Voxel.FiresSkyIslands.SkylandsNear(from, SkylandListRange, ids, indices);
            indices.Sort((a, b) =>
            {
                Vector3 pa = VerdantsAscent.Terrain.Voxel.FiresSkyIslands.SkylandSummit(a);
                Vector3 pb = VerdantsAscent.Terrain.Voxel.FiresSkyIslands.SkylandSummit(b);
                float da = (pa.x - from.x) * (pa.x - from.x) + (pa.z - from.z) * (pa.z - from.z);
                float db = (pb.x - from.x) * (pb.x - from.x) + (pb.z - from.z) * (pb.z - from.z);
                return da.CompareTo(db);
            });
            return indices;
        }

        private static List<string> Skylands()
        {
            var lines = new List<string>();
            var player = Player.m_localPlayer;
            if (player == null) { lines.Add("[Skylands] No local player."); return lines; }
            var nearest = NearestSkylands(player.transform.position);
            if (nearest.Count == 0)
            {
                lines.Add($"[Skylands] None within {SkylandListRange:0} m (the set is empty until the server sends it).");
                return lines;
            }
            lines.Add($"[Skylands] {nearest.Count} within {SkylandListRange:0} m, nearest first:");
            for (int i = 0; i < nearest.Count && i < SkylandListCount; i++)
            {
                int index = nearest[i];
                Vector3 top = VerdantsAscent.Terrain.Voxel.FiresSkyIslands.SkylandSummit(index);
                VerdantsAscent.Terrain.Voxel.FiresSkyIslands.IslandFootprint(index, out _, out float radius);
                float distance = new Vector2(top.x - player.transform.position.x, top.z - player.transform.position.z).magnitude;
                bool fall = VerdantsAscent.Terrain.Voxel.FiresSkyIslands.SkylandHasFall(index);
                lines.Add($"  {i + 1}. {distance:0} m away at ({top.x:0},{top.z:0}), top y={top.y:0}, {radius * 2f:0} m across{(fall ? ", waterfall" : "")}");
            }
            return lines;
        }

        private static string GotoSkyland(int nth)
        {
            var player = Player.m_localPlayer;
            if (player == null) return "[Skylands] No local player.";
            var nearest = NearestSkylands(player.transform.position);
            if (nearest.Count == 0) return "[Skylands] No skyland known yet.";
            int index = nearest[Mathf.Min(nth, nearest.Count) - 1];
            Vector3 top = VerdantsAscent.Terrain.Voxel.FiresSkyIslands.SkylandSummit(index);
            player.TeleportTo(top + Vector3.up * 2f, player.transform.rotation, true);
            return $"[Skylands] Going to the skyland at ({top.x:0},{top.z:0}), top y={top.y:0}.";
        }

        // 0.2.753 (Fire 10-06: Close chunk "sometimes just completely skips the chunk you're looking at and closes the next
        // one"): the zone under the crosshair's HIT (ZoneModeAreaTool.TryGetAim: land, rock or a build, else where the view
        // meets the sea). It used to intersect the view with the flat sea plane even over land, which lands past a hill you
        // look at (often in the next zone), and fell back to "the neighbour in the look direction" when the view never
        // reached sea level.
        internal static bool TryGetLookedAtZone(out Vector2s zone)
        {
            zone = default;
            if (!ZoneModeAreaTool.TryGetAim(out var point)) return false;
            zone = ZoneSystem.GetZone(point);
            return true;
        }

        // ── Verify ──────────────────────────────────────────────────────────
        private static List<string> Verify()
        {
            var lines = new List<string>();
            var generator = WorldGenerator.instance;
            if (!ZoneModeRegistry.ModeActive || generator == null)
            {
                lines.Add("[Zones] Nothing to verify — not a zone-mode world.");
                return lines;
            }
            var clock = Stopwatch.StartNew();
            int openMaps = 0, closedMaps = 0, openVertices = 0, closedVertices = 0;
            int vanillaMismatches = 0, builtMismatches = 0, closedAboveReal = 0, seaFloorVertices = 0, cornerErrors = 0;
            float worstVanillaDelta = 0f;
            var firstProblems = new List<string>();

            foreach (var heightmap in Heightmap.GetAllHeightmaps())
            {
                if (heightmap == null || heightmap.IsDistantLod) continue;
                var data = BuildDataRef(heightmap);
                if (data?.m_baseHeights == null) continue;
                var zone = ZoneSystem.GetZone(heightmap.transform.position);
                bool open = ZoneModeRegistry.IsOpen(zone);
                int count = data.m_baseHeights.Count;
                var asGated = new float[count];
                var asVanilla = new float[count];
                ComputeBaseHeights(generator, data, asGated, out Heightmap.Biome[] gatedCorners);
                ZoneModeGate.EnterPlanning();
                try { ComputeBaseHeights(generator, data, asVanilla, out _); }
                finally { ZoneModeGate.ExitPlanning(); }

                for (int i = 0; i < count; i++)
                {
                    float stored = data.m_baseHeights[i];
                    if (stored != asGated[i])
                    {
                        builtMismatches++;
                        if (firstProblems.Count < 4) firstProblems.Add($"({zone.x},{zone.y}) vertex {i}: stored {stored:0.###} vs gate {asGated[i]:0.###}");
                    }
                    if (open)
                    {
                        float delta = Mathf.Abs(stored - asVanilla[i]);
                        if (delta > 0f) vanillaMismatches++;
                        if (delta > worstVanillaDelta) worstVanillaDelta = delta;
                    }
                    else
                    {
                        if (stored > asVanilla[i] + 0.001f) closedAboveReal++;
                        if (Mathf.Approximately(stored, ZoneModeRegistry.SeaFloorY)) seaFloorVertices++;
                    }
                }
                cornerErrors += CheckCorners(data, open, gatedCorners);
                if (open) { openMaps++; openVertices += count; } else { closedMaps++; closedVertices += count; }
            }

            lines.Add($"[Zones] OPEN zones: {openMaps} loaded heightmap(s), {openVertices:N0} vertices — {vanillaMismatches} differ from "
                + $"vanilla generation (max {worstVanillaDelta:0.####} m). Expected 0.");
            lines.Add($"[Zones] CLOSED zones: {closedMaps} loaded heightmap(s), {closedVertices:N0} vertices — {seaFloorVertices:N0} at the "
                + $"sea floor, {closedAboveReal} above the real ground (expected 0).");
            lines.Add($"[Zones] Built data vs the gate now: {builtMismatches} mismatching vertices; corner biome errors: {cornerErrors}. "
                + "Expected 0 for both (a zone rebuilding right now can briefly differ).");
            foreach (string problem in firstProblems) lines.Add("[Zones]   " + problem);

            if (ZoneModeRegistry.IsAuthority)
            {
                var generated = ZoneModeRegistry.GeneratedZones(ZoneSystem.instance);
                int closedGenerated = generated?.Count(zone => !ZoneModeRegistry.IsOpen(zone)) ?? 0;
                lines.Add($"[Zones] Server: {generated?.Count ?? 0} generated zone(s), {closedGenerated} of them closed (expected 0).");
            }
            lines.Add($"[Zones] Verified in {clock.ElapsedMilliseconds} ms.");
            foreach (string line in lines) FiresLogger.LogInfo(line);
            return lines;
        }

        // Replays HeightmapBuilder.Build for a zone heightmap: corner biomes pick the height functions,
        // and vertices blend them bilinearly unless all four corners agree.
        private static void ComputeBaseHeights(WorldGenerator generator, HeightmapBuilder.HMBuildData data, float[] into,
            out Heightmap.Biome[] corners)
        {
            int width = data.m_width;
            float scale = data.m_scale;
            int side = width + 1;
            Vector3 origin = data.m_center + new Vector3((float)(width * (double)scale * -0.5), 0f, (float)(width * (double)scale * -0.5));
            float span = width * scale;
            var biome1 = generator.GetBiome(origin.x, origin.z);
            var biome2 = generator.GetBiome(origin.x + span, origin.z);
            var biome3 = generator.GetBiome(origin.x, origin.z + span);
            var biome4 = generator.GetBiome(origin.x + span, origin.z + span);
            corners = new[] { biome1, biome2, biome3, biome4 };
            bool uniform = biome1 == biome2 && biome1 == biome3 && biome1 == biome4;
            for (int row = 0; row < side; row++)
            {
                float wy = origin.z + row * scale;
                float t1 = DUtils.SmoothStep(0f, 1f, row / (float)width);
                for (int column = 0; column < side; column++)
                {
                    float wx = origin.x + column * scale;
                    float t2 = DUtils.SmoothStep(0f, 1f, column / (float)width);
                    float height;
                    if (uniform)
                        height = generator.GetBiomeHeight(biome1, wx, wy, out Color _);
                    else
                    {
                        float h1 = generator.GetBiomeHeight(biome1, wx, wy, out Color _);
                        float h2 = generator.GetBiomeHeight(biome2, wx, wy, out Color _);
                        float h3 = generator.GetBiomeHeight(biome3, wx, wy, out Color _);
                        float h4 = generator.GetBiomeHeight(biome4, wx, wy, out Color _);
                        height = DUtils.Lerp(DUtils.Lerp(h1, h2, t2), DUtils.Lerp(h3, h4, t2), t1);
                    }
                    if (row * side + column < into.Length) into[row * side + column] = height;
                }
            }
        }

        // Open zones must carry their real corner sectors; closed zones read Ocean at every corner
        // that is not on an open zone's edge.
        private static int CheckCorners(HeightmapBuilder.HMBuildData data, bool open, Heightmap.Biome[] gatedCorners)
        {
            if (data.m_cornerBiomes == null || data.m_cornerBiomes.Length < 4) return 0;
            int errors = 0;
            for (int i = 0; i < 4; i++)
            {
                var sector = data.m_cornerBiomes[i];
                if (sector == null) { errors++; continue; }
                if (gatedCorners[i] == Heightmap.Biome.Ocean && !open && sector.Biome != Heightmap.Biome.Ocean) errors++;
            }
            return errors;
        }

        // ── Dumps (server) ──────────────────────────────────────────────────
        private static void RequestDump(Terminal context, bool plan, Vector2s around, int radius)
        {
            if (ZNet.instance == null || ZNet.World == null) { context.AddString("[Zones] Must be in a world."); return; }
            if (ZNet.instance.IsServer())
            {
                context.AddString(plan ? DumpPlan() : DumpZones(around, radius));
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(plan);
            pkg.Write(around);
            pkg.Write(radius);
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcDumpRequest, pkg);
            context.AddString("[Zones] Asked the server to write the dump...");
        }

        private static void RPC_DumpRequest(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            bool plan = pkg.ReadBool();
            var around = pkg.ReadVector2s();
            int radius = Mathf.Clamp(pkg.ReadInt(), 0, MaxDumpRadius);
            string reply = AdminSyncing.IsAdmin(sender)
                ? (plan ? DumpPlan() : DumpZones(around, radius))
                : "[Zones] Permission denied — admin only.";
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcDumpReply, reply);
        }

        private static string DiagPath(string fileName)
        {
            string dir = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "FiresWorldGen", DiagFolder);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        private static string SafeWorldName()
            => string.Concat((ZNet.World?.m_name ?? "world").Split(Path.GetInvalidFileNameChars()));

        private static string DumpPlan()
        {
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return "[Zones] No ZoneSystem.";
            var rows = new List<string>();
            foreach (var pair in zoneSystem.m_locationInstances)
            {
                var instance = pair.Value;
                string name = instance.m_location?.m_prefabName ?? instance.m_location?.m_name ?? "?";
                rows.Add(string.Join(",", name, pair.Key.x, pair.Key.y, F(instance.m_position.x, "0.000"),
                    F(instance.m_position.y, "0.000"), F(instance.m_position.z, "0.000"), instance.m_placed ? "placed" : "planned"));
            }
            rows.Sort(StringComparer.Ordinal);
            string path = DiagPath($"{SafeWorldName()}_seed{ZNet.World.m_seedName}_plan.csv");
            File.WriteAllLines(path, new[] { "location,zone_x,zone_y,x,y,z,state" }.Concat(rows));
            return $"[Zones] Wrote {rows.Count} planned location(s) to {path}";
        }

        private static string DumpZones(Vector2s around, int radius)
        {
            var zdoMan = ZDOMan.instance;
            var scene = ZNetScene.instance;
            if (zdoMan == null || scene == null) return "[Zones] No ZDOMan/ZNetScene.";
            var rows = new List<string>();
            var names = new Dictionary<int, string>();
            var kinds = new Dictionary<int, string>();
            foreach (var zdo in ObjectsByIdRef(zdoMan).Values)
            {
                Vector3 position = zdo.GetPosition();
                var zone = ZoneSystem.GetZone(position);
                if (Math.Abs(zone.x - around.x) > radius || Math.Abs(zone.y - around.y) > radius) continue;
                int hash = zdo.GetPrefab();
                if (!names.TryGetValue(hash, out string name))
                {
                    var prefab = scene.GetPrefab(hash);
                    names[hash] = name = prefab != null ? prefab.name : hash.ToString(CultureInfo.InvariantCulture);
                    kinds[hash] = KindOf(prefab);
                }
                Vector3 euler = zdo.GetRotation().eulerAngles;
                rows.Add(string.Join(",", kinds[hash], name, zone.x, zone.y, F(position.x, "0.00"), F(position.y, "0.00"),
                    F(position.z, "0.00"), F(euler.y, "0.0")));
            }
            rows.Sort(StringComparer.Ordinal);
            string path = DiagPath($"{SafeWorldName()}_seed{ZNet.World.m_seedName}_zones_{around.x}_{around.y}_r{radius}.csv");
            File.WriteAllLines(path, new[] { "kind,prefab,zone_x,zone_y,x,y,z,yaw" }.Concat(rows));
            return $"[Zones] Wrote {rows.Count} object(s) in the zones within {radius} of ({around.x},{around.y}) to {path}";
        }

        private static string KindOf(GameObject prefab)
        {
            if (prefab == null) return "unknown";
            if (prefab.GetComponent<Player>() != null) return "player";
            if (prefab.GetComponent<Character>() != null) return "creature";
            if (prefab.GetComponent<ItemDrop>() != null) return "item";
            if (prefab.GetComponent<Piece>() != null && prefab.GetComponent<WearNTear>() != null) return "piece";
            return "world";
        }

        private static string F(float value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
    }
}
