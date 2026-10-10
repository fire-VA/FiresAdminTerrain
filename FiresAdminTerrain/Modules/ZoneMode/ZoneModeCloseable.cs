using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FiresCore.Sync;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Makes a full (non chunk-by-chunk) voxel world closeable, once, at its owner's word: the save is copied aside first,
    // then a zone registry with every zone open is written. Nothing changes until the server restarts; after that the
    // Chaos Hammer can close chunks and they stay shut until a Chunk Generator, an admin or a quest opens them.
    // Also the quest route: `fires_chunk_open x z [quest]` and FiresZonesApi.OpenForQuest for FiresNPCs.
    internal static class ZoneModeCloseable
    {
        internal const string CmdMakeCloseable = "fires_zones_make_closeable";
        internal const string CmdChunkOpen = "fires_chunk_open";
        private const string RpcRequest = "FiresAdminTerrain_CloseableRequest";
        private const string RpcReply = "FiresAdminTerrain_CloseableReply";
        private const string BackupFolderName = "FiresWorldBackups";

        private static ZRoutedRpc s_rpcOn;
        private static bool s_registered;
        private static Task<string> s_snapshot;
        private static World s_snapshotWorld;
        private static long s_snapshotFor;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;

            new Terminal.ConsoleCommand(CmdMakeCloseable,
                "Admin: let this full voxel world close chunks. Copies the save aside first, then every zone is marked open; "
                + "it takes effect when the server restarts. Run it once per world.",
                args => Request(args.Context, 0, 0, null, makeCloseable: true));

            new Terminal.ConsoleCommand(CmdChunkOpen,
                "Admin: open a closed chunk as a quest reward would, borders or not. Usage: " + CmdChunkOpen + " x z [quest]",
                args =>
                {
                    if (args.Args.Length < 3 || !int.TryParse(args.Args[1], out int x) || !int.TryParse(args.Args[2], out int z))
                    {
                        args.Context.AddString("[Zones] Usage: " + CmdChunkOpen + " x z [quest]");
                        return;
                    }
                    Request(args.Context, x, z, args.Args.Length > 3 ? args.Args[3] : "console", makeCloseable: false);
                });
        }

        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_rpcOn, rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcRequest, RPC_Request);
                rpc.Register<string>(RpcReply, (sender, message) =>
                {
                    try { Console.instance?.AddString(message); } catch { }
                    FiresLogger.LogInfo(message);
                });
                s_rpcOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] closeable RPC register failed: {ex.Message}"); }
        }

        private static void Request(Terminal context, int x, int z, string quest, bool makeCloseable)
        {
            if (ZNet.instance == null) { context?.AddString("[Zones] No world is loaded."); return; }
            if (ZNet.instance.IsServer())
            {
                context?.AddString(makeCloseable ? MakeCloseable(0L, "console") : OpenForQuest(new Vector2s(x, z), quest, 0L));
                return;
            }
            EnsureRpcs();
            var pkg = new ZPackage();
            pkg.Write(makeCloseable);
            pkg.Write(x);
            pkg.Write(z);
            pkg.Write(quest ?? "");
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest, pkg);
            context?.AddString("[Zones] asked the server...");
        }

        private static void RPC_Request(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            bool makeCloseable = pkg.ReadBool();
            int x = pkg.ReadInt();
            int z = pkg.ReadInt();
            string quest = pkg.ReadString();
            string reply;
            if (!AdminSyncing.IsAdmin(sender)) reply = "[Zones] Permission denied — admin only.";
            else if (makeCloseable) reply = MakeCloseable(sender, NameOf(sender));
            else reply = OpenForQuest(new Vector2s(x, z), string.IsNullOrEmpty(quest) ? "admin " + NameOf(sender) : quest, sender);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcReply, reply);
        }

        // ── Make closeable ──────────────────────────────────────────────────
        private static string MakeCloseable(long sender, string who)
        {
            var world = ZoneModePark.ServerWorld;
            if (world == null) return "[Zones] Only the server can make its world closeable.";
            if (ZoneModeRegistry.ModeActive) return $"[Zones] '{world.m_name}' already opens and closes by zone.";
            var profile = Voxel.FiresWorldProfile.Current;
            if (profile == null || profile.Terrain == Voxel.ProfileTerrain.Heightmap)
                return "[Zones] Closing chunks needs a voxel-wrap or full-voxel world.";
            if (world.m_fileSource != FileHelpers.FileSource.Local) return "[Zones] Only a local save can be made closeable.";
            if (s_snapshot != null) return "[Zones] A closeable snapshot is already being written.";
            if (File.Exists(ZoneModeRegistry.RegistryPath(world.m_name)))
                return $"[Zones] '{world.m_name}' already has a zone registry; restart the server to use it.";

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string target = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), BackupFolderName,
                Voxel.FiresWorldRecords.SafeName(world.m_name) + "_closeable-" + stamp);
            string saveDir = world.GetSaveDirectory(FileHelpers.FileSource.Local);
            string db = world.GetDBPath();
            string fwl = Path.ChangeExtension(db, ".fwl");
            s_snapshotWorld = world;
            s_snapshotFor = sender;
            s_snapshot = Task.Run(() => CopySave(saveDir, db, fwl, target));
            FiresLogger.LogInfo($"[FiresZones] {who} asked to make '{world.m_name}' closeable; copying its save to {target} first.");
            return $"[Zones] Copying '{world.m_name}' aside first ({target}); the zone registry is written when that is done.";
        }

        // Background: copies the world's save folder (1.0 folder world) and/or its flat .db/.fwl. Returns null or a failure.
        private static string CopySave(string saveDir, string db, string fwl, string target)
        {
            try
            {
                int files = 0;
                Directory.CreateDirectory(target);
                if (!string.IsNullOrEmpty(saveDir) && Directory.Exists(saveDir))
                {
                    string root = saveDir.TrimEnd('/', '\\');
                    foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        string to = Path.Combine(target, Path.GetFileName(root), file.Substring(root.Length).TrimStart('/', '\\'));
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.Copy(file, to, false);
                        files++;
                    }
                }
                foreach (string flat in new[] { db, fwl })
                {
                    if (string.IsNullOrEmpty(flat) || !File.Exists(flat)) continue;
                    File.Copy(flat, Path.Combine(target, Path.GetFileName(flat)), false);
                    files++;
                }
                return files == 0 ? "no save files were found to copy" : null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Main thread: finishes a make-closeable once its snapshot is on disk.
        internal static void Tick()
        {
            if (s_snapshot == null || !s_snapshot.IsCompleted) return;
            var world = s_snapshotWorld;
            long sender = s_snapshotFor;
            string failure = s_snapshot.IsFaulted ? s_snapshot.Exception?.GetBaseException().Message : s_snapshot.Result;
            s_snapshot = null;
            s_snapshotWorld = null;
            string reply;
            if (failure != null)
                reply = $"[Zones] '{world?.m_name}' was NOT made closeable: its save could not be copied aside ({failure}).";
            else if (world == null || !ReferenceEquals(world, ZoneModePark.ServerWorld))
                reply = "[Zones] The world changed while its save was copied; nothing was written.";
            else if (!ZoneModeRegistry.WriteFullOpenRegistry(world, out string path, out int zones, out string why))
                reply = $"[Zones] '{world.m_name}' was NOT made closeable: {why}.";
            else
                reply = $"[Zones] '{world.m_name}' is closeable from its next start ({zones:N0} zones open, {Path.GetFileName(path)}). "
                    + "Restart the server; then the Chaos Hammer can close chunks.";
            FiresLogger.LogInfo("[FiresZones] " + reply.Substring("[Zones] ".Length));
            if (sender == 0L) { try { Console.instance?.AddString(reply); } catch { } }
            else ZRoutedRpc.instance?.InvokeRoutedRPC(sender, RpcReply, reply);
        }

        // ── Quest open ──────────────────────────────────────────────────────
        internal static string OpenForQuest(Vector2s zone, string quest, long actor)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return "[Zones] Only the server can open a chunk.";
            if (!ZoneModeRegistry.ModeActive) return "[Zones] This is not a zone-mode world.";
            if (!ZoneOpenSet.InRange(zone.x, zone.y)) return $"[Zones] ({zone.x},{zone.y}) is outside the world.";
            if (ZoneModeRegistry.IsOpen(zone)) return $"[Zones] ({zone.x},{zone.y}) is already open.";
            var opened = ZoneModeOpening.Open(new[] { zone }, ZoneOpenReason.Quest, actor, "quest " + quest);
            return opened.Count == 1
                ? $"[Zones] Opened ({zone.x},{zone.y}) for quest '{quest}'."
                : $"[Zones] ({zone.x},{zone.y}) could not be opened.";
        }

        internal static void Reset()
        {
            s_snapshotWorld = null;
            s_snapshot = null;
        }

        private static string NameOf(long uid)
        {
            var peer = ZNet.instance?.GetPeer(uid);
            return peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : uid.ToString();
        }
    }

    // For other mods (FiresNPCs quest rewards): open one closed zone on the server, whatever its neighbours.
    public static class FiresZonesApi
    {
        // True when the zone is open afterwards. Server only; `message` says what happened.
        public static bool OpenForQuest(int x, int z, string questId, out string message)
        {
            var zone = new Vector2s(x, z);
            message = ZoneModeCloseable.OpenForQuest(zone, string.IsNullOrEmpty(questId) ? "quest" : questId, 0L);
            return ZoneModeRegistry.ModeActive && ZoneModeRegistry.IsOpen(zone);
        }
    }
}
