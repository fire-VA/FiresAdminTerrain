using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FiresCore.Sync;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // ── va_auto_water_undo: the whole-run undo va_auto_water_generate never had ([ghost], lead 11-17 rivers diff, owned by [ghost] with
    // [worldgen]'s agreement) ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    // va_auto_water_generate wipes EVERY water anchor in the world (AutoWaterPatches.ClearAllWaterInternal) before it generates, and that
    // wipe had no way back. Now, on the server, just before either wipe path runs (the local command and the client request), every water
    // anchor's own payload (WaterMeshCombinerAnchor.WriteBackupEntry: position, disks, grid, bounds, liquid kind; no heights, no segments)
    // is written to BepInEx\cache\FiresAdminTerrain\autowater_backup_<world>_<time>.bin. The bytes are built on the main thread and the
    // file written on a worker (a main-thread disk write is a stall on this rig). Backups are never deleted by FAT.
    //
    // va_auto_water_undo (admin; a client sends a request the server admin-checks, like the generate request): the newest backup of this
    // world is read on a worker, then on the main thread: ClearAllWaterInternal (cancels a running generation, removes the generated water,
    // tells every peer to clear its file cache, saves), then each saved anchor is made again by WaterMeshCombinerAnchor.
    // CreateAnchorZdoFromBackup (the world-load repair's own way of making an anchor), then the world is saved. Peers restore the water
    // from the anchors as after any world load. Deliberately NOT on Numpad 7: a world-wide water change is never one key away.
    internal static class AutoWaterBackup
    {
        private const string RpcUndo = "FiresAdminTerrain_AutoWater_RequestUndo";
        private const int Magic = 0x42574146;   // "FAWB"
        private const int FileVersion = 1;
        private const int RestoresPerFrame = 200;
        private static readonly string[] AnchorPrefabs = { "CustomRiver", "piece_dirtfloor_vad", "Piece_DirtFloor_vad", "piece_dirtfloor" };   // = ClearAllWaterInternal's scan
        private static bool s_undoRunning;

        private static string Dir => Path.Combine(BepInEx.Paths.CachePath, "FiresAdminTerrain");
        private static string WorldTag()
        {
            string w = ZNet.World != null ? ZNet.World.m_name + "_" + ZNet.World.m_seed : "unknown";
            foreach (char c in Path.GetInvalidFileNameChars()) w = w.Replace(c, '_');
            return w;
        }

        /// <summary>From AutoWaterPatches.InitializeRPC (each new ZRoutedRpc).</summary>
        internal static void RegisterRpc()
        {
            try { ZRoutedRpc.instance?.Register(RpcUndo, new Action<long>(HandleUndoRequest)); }
            catch (Exception ex) { FiresLogger.LogWarning($"[AutoWaterBackup] undo RPC not registered: {ex.Message}"); }
        }

        /// <summary>Server, just before a generate wipes every water anchor. Returns the number of anchors kept (0 = none existed).</summary>
        internal static int TakeBeforeWipe(string why)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return 0;
            try
            {
                var pkg = new ZPackage();
                var entries = new List<ZDO>();
                foreach (var name in AnchorPrefabs)
                {
                    var list = new List<ZDO>();
                    int index = 0;
                    while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(name, list, ref index)) { }
                    foreach (var zdo in list) if (WaterMeshCombinerAnchor.IsAnchorZdo(zdo)) entries.Add(zdo);
                }
                if (entries.Count == 0) { FiresLogger.LogInfo($"[AutoWaterBackup] {why}: no water anchors to keep before the wipe"); return 0; }
                pkg.Write(Magic);
                pkg.Write(FileVersion);
                pkg.Write(WaterMeshCombinerAnchor.BackupEntryVersion);
                pkg.Write(entries.Count);
                foreach (var zdo in entries) WaterMeshCombinerAnchor.WriteBackupEntry(zdo, pkg);
                byte[] bytes = pkg.GetArray();
                string path = Path.Combine(Dir, $"autowater_backup_{WorldTag()}_{DateTime.Now:yyyyMMdd-HHmmss}.bin");
                Task.Run(() =>
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
                    catch (Exception ex) { FiresLogger.LogWarning($"[AutoWaterBackup] backup NOT written to {path}: {ex.Message}"); }
                });
                FiresLogger.LogInfo($"[AutoWaterBackup] {why}: {entries.Count} water anchor(s) kept ({bytes.Length / 1024} KB) in {path}; va_auto_water_undo puts them back");
                return entries.Count;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[AutoWaterBackup] {why}: backup failed ({ex.Message}); the generate goes on WITHOUT an undo");
                return 0;
            }
        }

        /// <summary>For va_auto_water_status (the hand check's counts): water anchor ZDOs this peer knows (a server / host knows the whole
        /// world), and this world's backups (newest name).</summary>
        internal static string StatusLines()
        {
            int anchors = 0;
            if (ZDOMan.instance != null)
                foreach (var name in AnchorPrefabs)
                {
                    var list = new List<ZDO>();
                    int index = 0;
                    while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(name, list, ref index)) { }
                    foreach (var zdo in list) if (WaterMeshCombinerAnchor.IsAnchorZdo(zdo)) anchors++;
                }
            int backups = 0; string newest = "none"; DateTime best = DateTime.MinValue;
            try
            {
                if (Directory.Exists(Dir))
                    foreach (var f in Directory.GetFiles(Dir, $"autowater_backup_{WorldTag()}_*.bin"))
                    {
                        backups++;
                        var t = File.GetLastWriteTimeUtc(f);
                        if (t > best) { best = t; newest = Path.GetFileName(f); }
                    }
            }
            catch { }
            bool server = ZNet.instance != null && ZNet.instance.IsServer();
            return $"  Water anchors {(server ? "in the world" : "this client knows (run on the server/host for the whole world)")}: {anchors}\n"
                 + $"  Loaded water bodies here: {(WaterMeshCombiner.AllCombiners != null ? WaterMeshCombiner.AllCombiners.Count : 0)}\n"
                 + $"  Undo backups for this world: {backups} (newest {newest})";
        }

        // ── the command ────────────────────────────────────────────────────────────────────────────────────────────────────────────
        internal static void UndoCommand(Terminal.ConsoleEventArgs args)
        {
            if (ZNet.instance == null) { args.Context.AddString("Not in a world"); return; }
            if (!ZNet.instance.IsServer())
            {
                ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcUndo);   // registered on every peer by AutoWaterPatches.InitializeRPC
                args.Context.AddString("Sent the auto water undo request to the server (admins only). The server log says what was restored.");
                return;
            }
            args.Context.AddString(StartUndo(0L));
        }

        private static void HandleUndoRequest(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (sender != 0L && sender != ZDOMan.GetSessionID() && !AdminSyncing.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[AutoWaterBackup] non-admin peer {sender} asked for the auto water undo - rejected");
                return;
            }
            FiresLogger.LogInfo("[AutoWaterBackup] " + StartUndo(sender));
        }

        private static string StartUndo(long sender)
        {
            if (s_undoRunning) return "auto water undo already running";
            string newest = null;
            try
            {
                if (Directory.Exists(Dir))
                {
                    DateTime best = DateTime.MinValue;
                    foreach (var f in Directory.GetFiles(Dir, $"autowater_backup_{WorldTag()}_*.bin"))
                    {
                        var t = File.GetLastWriteTimeUtc(f);
                        if (t > best) { best = t; newest = f; }
                    }
                }
            }
            catch (Exception ex) { return "auto water undo: could not look for backups: " + ex.Message; }
            if (newest == null) return "auto water undo: no backup for this world (one is made by va_auto_water_generate, from FAT 0.2.1459 on)";
            if (Game.instance == null) return "auto water undo: no game";
            s_undoRunning = true;
            Game.instance.StartCoroutine(Undo(newest, sender));
            return $"auto water undo started from {Path.GetFileName(newest)}";
        }

        private static IEnumerator Undo(string path, long sender)
        {
            var read = Task.Run(() => File.ReadAllBytes(path));
            while (!read.IsCompleted) yield return null;
            string failure = Open(read, path);   // no yield inside a try with a catch (C#)
            if (failure != null) { Done(failure); yield break; }

            AutoWaterPatches.ClearAllWaterInternal(out int loaded, out int zdos);   // cancels a run, clears the generated water + every peer's cache, saves
            FiresLogger.LogInfo($"[AutoWaterBackup] undo: generated water cleared ({loaded} loaded, {zdos} anchor ZDO(s)); laying back {s_count} saved anchor(s)");
            int made = 0, failed = 0;
            for (int i = 0; i < s_count; i++)
            {
                try { if (WaterMeshCombinerAnchor.CreateAnchorZdoFromBackup(s_pending) != null) made++; else failed++; }
                catch (Exception ex) { failed++; FiresLogger.LogWarning($"[AutoWaterBackup] undo: saved anchor {i} not made: {ex.Message}"); break; }   // a bad entry ends the read
                if ((i + 1) % RestoresPerFrame == 0) yield return null;
            }
            s_pending = null;
            ZNet.instance?.Save(false);
            Done($"auto water undo done: {made} of {s_count} water anchor(s) laid back from {Path.GetFileName(path)}{(failed > 0 ? $", {failed} failed" : "")}; the world is saved, and every peer rebuilds the water as after a load");
        }

        private static ZPackage s_pending;
        private static int s_count;

        // the read backup checked and opened (s_pending at its first entry, s_count set); null = fine, else why not
        private static string Open(Task<byte[]> read, string path)
        {
            try
            {
                if (read.IsFaulted || read.Result == null) return $"auto water undo FAILED: could not read {path}: {read.Exception?.GetBaseException().Message}";
                var pkg = new ZPackage(read.Result);
                if (pkg.ReadInt() != Magic || pkg.ReadInt() != FileVersion || pkg.ReadInt() != WaterMeshCombinerAnchor.BackupEntryVersion)
                    return $"auto water undo FAILED: {Path.GetFileName(path)} is not a backup this FAT can read";
                s_count = pkg.ReadInt();
                s_pending = pkg;
                return null;
            }
            catch (Exception ex) { return "auto water undo FAILED reading the backup: " + ex.Message; }
        }

        private static void Done(string line)
        {
            s_undoRunning = false;
            FiresLogger.LogInfo("[AutoWaterBackup] " + line);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, line, 0, null, false);
        }
    }
}
