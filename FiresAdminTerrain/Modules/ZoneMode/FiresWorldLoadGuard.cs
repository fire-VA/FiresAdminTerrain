using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // A server refuses a world the same way vanilla refuses a corrupt save (ZNet.m_loadError: exit to the menu, never save)
    // when its chunked save went missing while older saves of it are still on disk, which vanilla would otherwise load as an
    // EMPTY world and save over the real one; when another live Valheim process on this machine already runs it; or when a
    // dedicated server's save does not load, where vanilla would create a NEW world under its name. Before loading, a save
    // folder left with several generations by a crash is sorted down to its newest complete one (FiresSaveGenerations).
    // FAT's park and zone writes stand down for a refused load. Tools\WORLD_LOSS_20260927_RPGWORLD_CHUNKS.md,
    // Tools\SAVE_SAFETY_AUDIT.md
    internal static class FiresWorldLoadGuard
    {
        private const string LockSuffix = ".lock";
        private const string SaveMetaPattern = "_main.*.db2";
        private const string ChunkFilePattern = "*.chunk";
        private const string PidKey = "pid=";
        private const string StartKey = "startTicks=";
        private const string ProcessKey = "process=";
        private const string ClientProcessName = "valheim";
        private const string ServerProcessName = "valheim_server";
        private const string SnapshotSuffix = ".saves-";
        private const string SnapshotStampFormat = "yyyyMMdd-HHmmss";

        private static readonly FieldInfo WorldField = AccessTools.Field(typeof(ZNet), "m_world");
        private static readonly AccessTools.FieldRef<World, uint> SaveNumberRef = AccessTools.FieldRefAccess<World, uint>("m_saveNumber");

        internal static bool Refused { get; private set; }
        internal static string Reason { get; private set; }

        // SaveSystem.IsWorldBackup's own test for a dated backup folder.
        private static readonly Regex BackupFolder = new Regex(@".+(\d{4})-*(\d{2})-*(\d{2})-*(\d{2})-*(\d{2})-*(\d{2})", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> s_listWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string s_ownedLockPath;

        // 0.2.736 (fat_load_test): this process holds the loaded world's lock.
        internal static bool OwnsLock => s_ownedLockPath != null;
        private static string s_pendingRefusalWorld;
        private static string s_pendingRefusal;

        private static void BeforeServerLoad()
        {
            Refused = false;
            Reason = null;
            var world = WorldField?.GetValue(null) as World;
            if (world == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (s_pendingRefusal != null && s_pendingRefusalWorld == world.m_worldName)
            {
                string pending = s_pendingRefusal;
                s_pendingRefusal = null;
                Refuse(pending, world);
                return;
            }
            TakeWorldLock(world);
            if (Refused || !world.IsChunkedSave() || world.m_fileSource != FileHelpers.FileSource.Local) return;
            if (!SortGenerations(world.m_worldName, world.GetSaveDirectory(world.m_fileSource), out uint? kept, out string why))
            {
                Refuse(why, world);
                return;
            }
            if (kept == null || kept == world.SaveNumber()) return;
            FiresLogger.LogWarning($"[FiresWorldLoadGuard] '{world.m_name}': loading save _main.{kept} instead of _main.{world.SaveNumber()}.");
            SaveNumberRef(world) = kept.Value;
        }

        // The dedicated server's only way into a world (FejdStartup.ParseServerArguments). True lets vanilla's GetCreateWorld
        // run: the name is new, or its save loads. Otherwise vanilla would log the failure, create a new world with a new
        // seed under the name and write its meta beside the real one, so this returns the failed world instead, refuses it
        // and quits the server with nothing written.
        internal static bool BeforeCreateWorld(string name, ref World result)
        {
            if (!SaveSystem.TryGetSaveByName(name, SaveDataType.World, out var save) || save.IsDeleted) return true;
            var primary = save.PrimaryFile;
            string why = null;
            if (TryReadLiveOwner(FiresWorldRecords.PathFor(name, LockSuffix), out string owner))
                why = $"it is already running in {owner} on this machine. Close the other one first.";
            else if (primary.IsChunked && primary.m_source == FileHelpers.FileSource.Local
                && SortGenerations(name, primary.ChunkedDirectory, out _, out why)
                && SaveSystem.TryGetSaveByName(name, SaveDataType.World, out var sorted) && !sorted.IsDeleted)
                save = sorted;
            World loaded = why == null ? World.LoadWorld(save) : null;
            if (why == null && loaded != null && loaded.m_dataError == World.SaveDataError.None) return true;
            why = why ?? $"its save does not load ({(loaded == null ? "no main files" : loaded.m_dataError.ToString())}). Vanilla would "
                + "create a NEW world with a new seed under this name. Restore a backup of it, or move the folder away to start fresh.";
            result = loaded ?? new World(save, World.SaveDataError.LoadError);
            s_pendingRefusalWorld = result.m_worldName;
            s_pendingRefusal = why;
            FiresLogger.LogError($"[FiresWorldLoadGuard] REFUSED world '{name}' before loading: {why} Nothing was written.");
            if (FiresCore.Lifecycle.FiresMod.IsDedicatedServer)
            {
                FiresLogger.LogError("[FiresWorldLoadGuard] the dedicated server quits instead of running a world it cannot save.");
                UnityEngine.Application.Quit();
            }
            return false;
        }

        // Kept is null when the folder needed no sorting.
        private static bool SortGenerations(string worldName, string saveDirectory, out uint? kept, out string why) =>
            SortGenerations(worldName, saveDirectory, FiresSaveGenerations.PlanFor(FiresSaveGenerations.Scan(saveDirectory)), out kept, out why);

        private static bool SortGenerations(string worldName, string saveDirectory, FiresSaveGenerations.Plan plan, out uint? kept, out string why)
        {
            kept = null;
            why = null;
            if (plan == null) return true;
            if (plan.Keep == null)
            {
                why = $"{plan.Why}, and none of them has a whole meta to load.";
                return false;
            }
            string snapshot = FiresWorldRecords.PathFor(worldName, SnapshotSuffix + DateTime.Now.ToString(SnapshotStampFormat, CultureInfo.InvariantCulture));
            if (!FiresSaveGenerations.Apply(plan, saveDirectory, snapshot, out int copied, out int moved, out string error))
            {
                why = $"{plan.Why}; setting the others aside into {snapshot} failed ({error}).";
                return false;
            }
            SaveSystem.InvalidateCache(SaveDataType.World);
            kept = plan.Keep.Number;
            FiresLogger.LogWarning($"[FiresWorldLoadGuard] '{worldName}': {plan.Why}. A crash during a save leaves this. Keeping "
                + $"_main.{kept}; moved {moved} main file(s) of the others and copied {copied} chunk file(s) the kept save does not use to {snapshot}.");
            return true;
        }

        // A listen host picks its world from vanilla's list, which reads each folder's ordinally first meta, so after a crash
        // mid-save the world shows as broken there and will not start. Before the list is read, every local chunked world
        // folder is sorted the way a server's is at load. Backup folders, and a world a live Valheim process runs, are left alone.
        internal static void BeforeWorldList()
        {
            if (FiresCore.Lifecycle.FiresMod.IsDedicatedServer || ZNet.instance != null) return;
            string root = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
            if (!Directory.Exists(root)) return;
            foreach (string folder in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(folder);
                if (BackupFolder.IsMatch(name)) continue;
                var plan = FiresSaveGenerations.PlanFor(FiresSaveGenerations.Scan(folder));
                if (plan == null) continue;
                if (TryReadLiveOwner(FiresWorldRecords.PathFor(name, LockSuffix), out string owner))
                {
                    if (s_listWarned.Add(folder))
                        FiresLogger.LogWarning($"[FiresWorldLoadGuard] world list: '{name}' {plan.Why}, but it is running in {owner}; left as it is.");
                    continue;
                }
                SetAsideBeforeVanilla(name, folder, "world list: ");
            }
        }

        // A dedicated server's first save lookup is World.HaveWorld / SaveSystem.TryGetSaveByName on its -world, and vanilla's
        // SaveCollection.Reload behind it (KeepOnlyNewest) DELETES every generation but the newest with all four main files.
        // The crash series (2026-09-29, OPEN_BUGS #44) caught that lookup running before the sort, so nothing was set aside.
        // This runs first, from the GetCreateWorld prefix.
        internal static void BeforeFirstLookup(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            string folder = Path.Combine(SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local), name);
            if (!Directory.Exists(folder) || TryReadLiveOwner(FiresWorldRecords.PathFor(name, LockSuffix), out _)) return;
            SetAsideBeforeVanilla(name, folder, string.Empty);
        }

        // Leaves the folder as vanilla's lookup will keep it, with everything it would delete set aside first: the newest
        // complete generation stays and the others move into a snapshot. With no complete generation, vanilla keeps one only
        // when it has all four files (a single torn one: the load is refused later and nothing is deleted); otherwise it
        // deletes every main file, so all of them are copied aside first and the loss is said loudly.
        private static void SetAsideBeforeVanilla(string name, string folder, string context)
        {
            var generations = FiresSaveGenerations.Scan(folder);
            var plan = FiresSaveGenerations.PlanFor(generations);
            bool vanillaKeepsOne = generations.Exists(g => g.MetaPresent && g.HasData && g.HasChunkMap && g.HasOk);
            if (plan == null && vanillaKeepsOne) return;
            if (plan != null && plan.Keep != null && plan.Keep.Complete)
            {
                if (!SortGenerations(name, folder, plan, out _, out string why))
                    FiresLogger.LogError($"[FiresWorldLoadGuard] {context}'{name}' cannot be sorted: {why} Vanilla's save list will "
                        + "delete every generation but its newest whole one.");
                return;
            }
            if (generations.Count == 1 && vanillaKeepsOne) return;
            string snapshot = FiresWorldRecords.PathFor(name, SnapshotSuffix + DateTime.Now.ToString(SnapshotStampFormat, CultureInfo.InvariantCulture));
            string state = string.Join(", ", generations.ConvertAll(g => g.Describe()));
            if (FiresSaveGenerations.CopyAside(generations, snapshot, out int copied, out string error))
                FiresLogger.LogError($"[FiresWorldLoadGuard] {context}'{name}': no save generation is complete ({state}), so "
                    + $"vanilla's save list deletes {(vanillaKeepsOne ? "all but one" : "ALL")} of them. Copied {copied} main file(s) to {snapshot} first.");
            else
                FiresLogger.LogError($"[FiresWorldLoadGuard] {context}'{name}': no save generation is complete ({state}), and "
                    + $"copying them aside into {snapshot} failed ({error}); vanilla's save list will delete them.");
        }

        private static void AfterChunkedLoad(bool loaded)
        {
            if (loaded || ZNet.m_loadError) return;
            var world = WorldField?.GetValue(null) as World;
            if (world == null || world.m_fileSource != FileHelpers.FileSource.Local) return;
            string saveDirectory = world.GetSaveDirectory(world.m_fileSource);
            string expected = saveDirectory + SaveSystem.MainDb2FileName();
            if (File.Exists(expected) || !Directory.Exists(saveDirectory)) return;
            int olderSaves = Directory.GetFiles(saveDirectory, SaveMetaPattern).Length;
            int chunkFiles = Directory.GetFiles(saveDirectory, ChunkFilePattern).Length;
            if (olderSaves + chunkFiles == 0) return;
            Refuse($"its save file {Path.GetFileName(expected)} is missing while {olderSaves} other save file(s) and {chunkFiles} "
                + $"chunk file(s) of it are still in {saveDirectory}. Vanilla would load it EMPTY and save that over the real world. "
                + "Another process may have saved it since this game listed it: restart the game so the world list is re-read.", world);
        }

        private static void TakeWorldLock(World world)
        {
            string lockPath = FiresWorldRecords.PathFor(world.m_name, LockSuffix);
            if (TryReadLiveOwner(lockPath, out string owner))
            {
                Refuse($"it is already running in {owner} on this machine. Two processes on one save folder overwrite each other's "
                    + "saves. Close the other one first.", world);
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath));
                if (File.Exists(lockPath))
                    FiresLogger.LogWarning($"[FiresWorldLoadGuard] '{world.m_name}': taking over a stale lock left by a process that is gone.");
                var self = Process.GetCurrentProcess();
                File.WriteAllText(lockPath, PidKey + self.Id.ToString(CultureInfo.InvariantCulture) + "\n"
                    + StartKey + self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "\n"
                    + ProcessKey + self.ProcessName + "\n");
                s_ownedLockPath = lockPath;
                FiresLogger.LogInfo($"[FiresWorldLoadGuard] '{world.m_name}' locked to this process (pid {self.Id}).");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldLoadGuard] could not write the world lock {lockPath} ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static bool TryReadLiveOwner(string lockPath, out string owner)
        {
            owner = null;
            if (!File.Exists(lockPath)) return false;
            int pid = 0;
            long startTicks = 0;
            string processName = null;
            try
            {
                foreach (string line in File.ReadAllLines(lockPath))
                {
                    if (line.StartsWith(PidKey, StringComparison.Ordinal)) int.TryParse(line.Substring(PidKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
                    else if (line.StartsWith(StartKey, StringComparison.Ordinal)) long.TryParse(line.Substring(StartKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out startTicks);
                    else if (line.StartsWith(ProcessKey, StringComparison.Ordinal)) processName = line.Substring(ProcessKey.Length);
                }
            }
            catch (IOException) { return false; }
            if (pid == 0 || pid == Process.GetCurrentProcess().Id) return false;
            Process other;
            try { other = Process.GetProcessById(pid); }
            catch (ArgumentException) { return false; }
            if (!IsValheimProcess(other)) return false;
            owner = $"{processName ?? other.ProcessName} (pid {pid})";
            try { return !other.HasExited && other.StartTime.ToUniversalTime().Ticks == startTicks; }
            catch (Win32Exception) { return true; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool IsValheimProcess(Process process)
        {
            try
            {
                return string.Equals(process.ProcessName, ClientProcessName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(process.ProcessName, ServerProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch (InvalidOperationException) { return false; }
        }

        private static void Refuse(string why, World world)
        {
            Refused = true;
            Reason = why;
            ZNet.m_loadError = true;
            FiresLogger.LogError($"[FiresWorldLoadGuard] REFUSED world '{world.m_name}': {why} Nothing is saved and FAT leaves its zone and "
                + "park files alone.");
        }

        internal static void ReleaseWorldLock()
        {
            Refused = false;
            Reason = null;
            s_pendingRefusal = null;
            if (s_ownedLockPath == null) return;
            string lockPath = s_ownedLockPath;
            s_ownedLockPath = null;
            try
            {
                if (!TryReadOwnPid(lockPath))
                {
                    FiresLogger.LogWarning($"[FiresWorldLoadGuard] world lock {lockPath} no longer names this process; left as it is.");
                    return;
                }
                File.Delete(lockPath);
                FiresLogger.LogInfo($"[FiresWorldLoadGuard] world lock released ({Path.GetFileName(lockPath)}).");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldLoadGuard] could not release the world lock {lockPath} ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static bool TryReadOwnPid(string lockPath)
        {
            if (!File.Exists(lockPath)) return false;
            string ownPid = PidKey + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            foreach (string line in File.ReadAllLines(lockPath))
                if (line == ownPid) return true;
            return false;
        }

        [HarmonyPatch(typeof(ZNet), "LoadWorld")]
        private static class ZNet_LoadWorld_Guard
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() => BeforeServerLoad();

            private static void Postfix(bool __result) => AfterChunkedLoad(__result);
        }

        [HarmonyPatch(typeof(ZNet), "LoadOldWorld")]
        private static class ZNet_LoadOldWorld_Guard
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() => BeforeServerLoad();
        }

    }
}
