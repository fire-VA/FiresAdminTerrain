using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // Where a chunk world's guaranteed start objects are: the starter Chunk Generator and the chest holding the first
    // Chunk Hammer. The server scans its whole ZDO set (a client only holds what is near it), so "none" is proof of
    // absence, and each report says what the placer is still waiting on. Registered from the skyland command and RPC
    // registrations, which FAT's Terminal.InitTerminal and ZNet.Awake hooks already call.
    internal static class ZoneModeFinders
    {
        private const string Command = "fires_zones_find";
        private const string RpcRequest = "FiresAdminTerrain_ZoneFind";
        private const string RpcReply = "FiresAdminTerrain_ZoneFindReply";
        private const string ChestPrefab = "TreasureChest_trollcave";
        private const string TempleName = "StartTemple";
        private const string Tag = "[Find] ";
        private const float ReplyTimeoutSeconds = 20f;
        private const float SettleSeconds = 5f;
        private static readonly int StarterHash = "fires_chunk_starter".GetStableHashCode();
        private static ZRoutedRpc s_rpcOn;
        private static bool s_awaiting;

        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_rpcOn, rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcRequest, RPC_Request);
                rpc.Register<ZPackage>(RpcReply, RPC_Reply);
                s_rpcOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] find RPC register failed: {ex.Message}"); }
        }

        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand(Command,
                "Where this chunk world's starter Chunk Generator and first Chunk Hammer chest are, from a scan of the whole "
                + "world on the server: their positions and zones, or what is still holding their placement up.",
                args => Run(args.Context), isCheat: true);
        }

        private static void Run(Terminal context)
        {
            if (ZNet.instance == null || ZNet.World == null)
            {
                context.AddString(Tag + "Must be in a world.");
                return;
            }
            if (ZNet.instance.IsServer())
            {
                foreach (var line in Report()) context.AddString(line);
                return;
            }
            s_awaiting = true;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest, new ZPackage());
            context.AddString(Tag + "Asking the server...");
            var host = FiresAdminTerrain.Instance;
            if (host != null) host.StartCoroutine(WatchForReply(context));
        }

        private static IEnumerator WatchForReply(Terminal context)
        {
            float deadline = Time.realtimeSinceStartup + ReplyTimeoutSeconds;
            while (s_awaiting && Time.realtimeSinceStartup < deadline) yield return null;
            if (!s_awaiting) yield break;
            s_awaiting = false;
            context?.AddString($"{Tag}No reply from the server within {ReplyTimeoutSeconds:0}s: FiresAdminTerrain is probably missing or out of date there.");
        }

        private static void RPC_Request(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!AdminSyncing.IsAdmin(sender))
            {
                Reply(sender, Tag + "Permission denied: admin only.");
                return;
            }
            foreach (var line in Report()) Reply(sender, line);
        }

        private static void RPC_Reply(long sender, ZPackage pkg)
        {
            s_awaiting = false;
            string message = pkg.ReadString();
            try { if (Console.instance != null) Console.instance.AddString(message); } catch { }
            FiresLogger.LogInfo(message);
        }

        private static void Reply(long target, string message)
        {
            var pkg = new ZPackage();
            pkg.Write(message);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcReply, pkg);
        }

        // ── The scan (server) ───────────────────────────────────────────────────────────────────────
        private static List<string> Report()
        {
            var lines = new List<string>();
            if (!ZoneModeRegistry.ModeActive)
            {
                lines.Add(Tag + "This is not a one-zone-at-a-time world.");
                return lines;
            }
            if (ZDOMan.instance == null || ZoneSystem.instance == null)
            {
                lines.Add(Tag + "The world is not loaded yet.");
                return lines;
            }
            bool hasTemple = ZoneSystem.instance.FindClosestLocation(TempleName, Vector3.zero, out var temple);
            var templeAt = hasTemple ? temple.m_position : Vector3.zero;
            if (hasTemple)
            {
                var zone = ZoneSystem.GetZone(templeAt);
                lines.Add($"{Tag}{TempleName} at {At(templeAt)} in zone ({zone.x},{zone.y}); that zone is {(VoxelZoneStreamer.IsZoneGenerated(zone) ? "generated" : "NOT generated yet")}.");
            }
            else lines.Add($"{Tag}No {TempleName} is registered yet: location placement has not finished, and nothing can be placed before it does.");
            ReportGenerators(lines, hasTemple, templeAt);
            ReportHammer(lines, hasTemple, templeAt);
            return lines;
        }

        private static void ReportGenerators(List<string> lines, bool hasTemple, Vector3 templeAt)
        {
            var starterTier = ZoneModeChunkTiers.Starter;
            int starters = 0, machines = 0;
            foreach (var tier in ZoneModeChunkTiers.All)
                foreach (var zdo in AllWithPrefab(tier.MachinePrefab))
                {
                    machines++;
                    bool starter = zdo.GetBool(StarterHash, false);
                    if (starter) starters++;
                    lines.Add($"{Tag}{tier.MachineName} at {Where(zdo.GetPosition(), hasTemple, templeAt)}"
                              + (starter ? " — the indestructible STARTER."
                                  : tier.RestoresOnly ? " — an ordinary machine."
                                  : " — an OLD per-biome machine; it becomes the Chunk Generator when its zone next loads."));
                }
            if (starters > 0) return;
            bool loaded = ZoneModePrefabs.TryGet(starterTier.MachinePrefab, out _);
            bool zoneReady = hasTemple && VoxelZoneStreamer.IsZoneGenerated(ZoneSystem.GetZone(templeAt));
            lines.Add($"{Tag}NO starter {starterTier.MachineName} exists anywhere in the world ({machines} generator(s) in all). "
                      + $"The placer needs: its prefab '{starterTier.MachinePrefab}' loaded ({Ok(loaded, "the fireszones bundle did not supply it")}), "
                      + $"the {TempleName} registered ({Ok(hasTemple, "not yet")}), and the temple's zone generated ({Ok(zoneReady, "not yet")}). "
                      + "It looks for a spot 30-42 m from the temple and retries every few seconds.");
        }

        private static void ReportHammer(List<string> lines, bool hasTemple, Vector3 templeAt)
        {
            string marker = MarkerPath();
            bool recorded = File.Exists(marker);
            if (recorded)
            {
                string text;
                try { text = File.ReadAllText(marker).Trim(); }
                catch (Exception ex) { text = "unreadable: " + ex.Message; }
                lines.Add($"{Tag}Chunk Hammer chest record {Path.GetFileName(marker)}: \"{text}\" (x y z, then the time it was placed).");
            }
            else lines.Add($"{Tag}No Chunk Hammer chest record ({Path.GetFileName(marker)} is absent): the chest has not been placed in this world.");

            int chests = 0, holding = 0;
            foreach (var zdo in AllWithPrefab(ChestPrefab))
            {
                chests++;
                int hammers = HammersIn(zdo, out bool stored);
                if (hammers <= 0 && !stored) continue;
                holding++;
                lines.Add($"{Tag}Chunk Hammer x{hammers} in a {ChestPrefab} at {Where(zdo.GetPosition(), hasTemple, templeAt)}"
                          + (hammers > 0 ? "." : " (the stored inventory lists the hammer, but the game cannot read it back as an item here: "
                                                   + $"'{ZoneModePrefabs.ChunkHammerPrefab}' is missing from ObjectDB on this server)."));
            }
            lines.Add($"{Tag}{chests} {ChestPrefab} chest(s) in the world, {holding} holding a Chunk Hammer.");
            int loose = 0;
            foreach (var zdo in AllWithPrefab(ZoneModePrefabs.ChunkHammerPrefab))
            {
                loose++;
                lines.Add($"{Tag}A loose {ZoneModePrefabs.ChunkHammerPrefab} item lies at {Where(zdo.GetPosition(), hasTemple, templeAt)}.");
            }
            lines.Add($"{Tag}{loose} loose Chunk Hammer item(s) in the world. A hammer inside a player's inventory is not visible to a world scan.");
            if (holding > 0 || loose > 0) return;

            bool prefabLoaded = ZoneModePrefabs.TryGet(ZoneModePrefabs.ChunkHammerPrefab, out _);
            var placer = new List<string>();
            placer.Add($"hammer prefab loaded ({Ok(prefabLoaded, "the fireszones bundle did not supply it")})");
            placer.Add($"{TempleName} registered ({Ok(hasTemple, "not yet")})");
            if (ZoneModeStartCave.Active && FiresVoxelConfig.GeneratesCaves)
            {
                var cave = ZoneModeStartCave.ChamberZone;
                bool generated = VoxelZoneStreamer.IsZoneGenerated(cave), settled = generated && VoxelZoneStreamer.IsZoneSolidFor(cave, SettleSeconds);
                placer.Add($"start cave zone ({cave.x},{cave.y}) generated ({Ok(generated, "not yet")}) and settled for {SettleSeconds:0} s ({Ok(settled, "not yet")}); the chest goes on the cave floor");
            }
            else placer.Add($"no start cave in this world ({(FiresVoxelConfig.GeneratesCaves ? "none was sited" : "caves are off")}), so the chest goes in a cavern under the start, else beside the starter generator, else beside the temple");
            placer.Add($"placer state: done={Field<bool>("s_done")}, start zones scanned={Scanned()}, generated at t={Field<float>("s_generatedAt"):0}, incomplete since t={Field<float>("s_incompleteSince"):0} (now t={Time.time:0}, -1 = not started)");
            lines.Add($"{Tag}NO Chunk Hammer exists anywhere in the world. The placer needs: {string.Join("; ", placer)}.");
        }

        // Every ZDO of a prefab, each once. Only the server holds the whole set.
        private static IEnumerable<ZDO> AllWithPrefab(string prefab)
        {
            var found = new List<ZDO>();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, found, ref index)) { }
            var seen = new HashSet<ZDOID>();
            foreach (var zdo in found)
                if (zdo != null && seen.Add(zdo.m_uid)) yield return zdo;
        }

        // How many Chunk Hammers a chest's stored inventory holds, read the way Container.Load reads it (so it is what a
        // player opening the chest gets), and whether the raw payload lists the hammer at all: the game's reader drops an
        // item ObjectDB cannot resolve, and a proof must not mistake that for an empty chest.
        private static int HammersIn(ZDO chest, out bool stored)
        {
            stored = false;
            var bytes = chest.GetByteArray(ZDOVars.s_items);
            if (bytes == null || bytes.Length == 0) return 0;
            stored = ZoneModeHammerChest.HoldsHammer(bytes);
            try
            {
                var inventory = new Inventory(ChestPrefab, null, 8, 4);
                inventory.Load(new ZPackage(bytes));
                int count = 0;
                foreach (var item in inventory.GetAllItems())
                {
                    string name = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name;
                    if (name == ZoneModePrefabs.ChunkHammerPrefab) count += item.m_stack;
                }
                return count;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] could not read a {ChestPrefab} inventory at {At(chest.GetPosition())}: {ex.Message}");
                return 0;
            }
        }

        private static string MarkerPath()
        {
            try
            {
                var method = AccessTools.Method(typeof(ZoneModeHammerChest), "MarkerPath");
                if (method != null && method.Invoke(null, null) is string path) return path;
            }
            catch { }
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(ZNet.World?.m_name ?? "world", "_hammer.txt");
        }

        private static T Field<T>(string name)
        {
            try { return AccessTools.StaticFieldRefAccess<T>(typeof(ZoneModeHammerChest), name); }
            catch { return default; }
        }

        private static int Scanned()
        {
            try { return AccessTools.StaticFieldRefAccess<HashSet<Vector2s>>(typeof(ZoneModeHammerChest), "s_scanned")?.Count ?? -1; }
            catch { return -1; }
        }

        private static string Where(Vector3 p, bool hasTemple, Vector3 templeAt)
        {
            var zone = ZoneSystem.GetZone(p);
            string place = $"{At(p)} in zone ({zone.x},{zone.y})";
            return hasTemple ? $"{place}, {Vector3.Distance(p, templeAt):0} m from the {TempleName}" : place;
        }

        private static string At(Vector3 p) => $"({p.x:0},{p.y:0},{p.z:0})";

        private static string Ok(bool ok, string otherwise) => ok ? "ok" : otherwise.ToUpperInvariant();
    }
}
