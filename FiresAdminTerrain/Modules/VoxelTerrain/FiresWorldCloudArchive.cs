using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FiresCore.UI.ContextMenu;
using Splatform;
using VerdantsAscent.Async;

namespace VerdantsAscent.Terrain.Voxel
{
    // Steam Cloud worlds cannot be cleared by deleting files: Steam restores anything removed from its userdata folder
    // behind its back. This moves them off the cloud the way Valheim's own Manage saves "Move" does (copy while the
    // storage is mounted, then SaveSystem.Delete the cloud copy), except that the copy lands in an archive folder
    // instead of the local world list. Nothing is deleted until its bytes are on disk; the archive is Fire's to delete.
    // Main menu only. A bare run lists what would move; "confirm" moves it.
    internal static class FiresWorldCloudArchive
    {
        private const string CommandName = "fires_worlds_off_cloud";
        private const string ConfirmWord = "confirm";
        private const string KeepPrefix = "keep=";
        private const char KeepSeparator = ',';
        private const string OldArchivePattern = "_DELETE_ME_old_test_saves_*";
        private const string OwnArchivePrefix = "_DELETE_ME_cloud_worlds_";
        private const string ArchiveSubfolderPrefix = "steam_cloud_worlds_";
        private const string StampFormat = "yyyyMMdd-HHmmss";
        private const string WorldsSegment = "worlds/";
        private const double BytesPerMegabyte = 1024d * 1024d;

        private static bool s_registered;
        private static bool s_running;

        private sealed class Plan
        {
            internal readonly List<SaveWithBackups> Move = new List<SaveWithBackups>();
            internal readonly List<string> Kept = new List<string>();
            internal int Files;
            internal ulong Bytes;
        }

        internal static void RegisterCommands()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(CommandName,
                "Main menu: move Steam Cloud worlds off the world list into an archive folder (nothing is deleted). "
                + "Usage: " + CommandName + " [confirm] [keep=world1,world2]. Without confirm it only lists what would move.",
                args =>
                {
                    string report = Run(args.Args);
                    args.Context.AddString(report);
                    FiresLogger.LogInfo("[FiresWorldGen] " + report);
                });
        }

        private static string Run(string[] words)
        {
            if (ZNet.instance != null) return "Leave the world first: cloud worlds are moved from the main menu.";
            if (s_running) return "A cloud move is already running.";
            bool confirm = words.Skip(1).Any(word => string.Equals(word, ConfirmWord, StringComparison.OrdinalIgnoreCase));
            var keep = new HashSet<string>(words.Skip(1)
                .Where(word => word.StartsWith(KeepPrefix, StringComparison.OrdinalIgnoreCase))
                .SelectMany(word => word.Substring(KeepPrefix.Length).Split(KeepSeparator))
                .Select(name => name.Trim())
                .Where(name => name.Length > 0), StringComparer.OrdinalIgnoreCase);

            Plan plan = PlanFor(keep);
            string summary = $"{plan.Move.Count} cloud world(s), {plan.Files} file set(s) with backups, "
                + $"{plan.Bytes / BytesPerMegabyte:0.#} MB; kept on the cloud: "
                + (plan.Kept.Count > 0 ? string.Join(", ", plan.Kept.ToArray()) : "none");
            if (!confirm)
                return $"Would move {summary}. To move: {CommandName} {ConfirmWord}"
                    + (keep.Count > 0 ? $" {KeepPrefix}{string.Join(KeepSeparator.ToString(), keep.ToArray())}" : "")
                    + $". Worlds: {string.Join(", ", plan.Move.Select(save => save.Name).ToArray())}";
            if (plan.Move.Count == 0) return "No cloud worlds to move: " + summary + ".";

            string archive = ArchiveFolder();
            s_running = true;
            MainThreadDispatcher.StartRoutine(MoveAll(plan, archive));
            return $"Moving {summary} into {archive}. Progress and the result are in the log; the world list refreshes when it is done.";
        }

        private static Plan PlanFor(HashSet<string> keep)
        {
            var plan = new Plan();
            SaveWithBackups[] saves = SaveSystem.GetSavesByType(SaveDataType.World);
            if (saves == null) return plan;
            foreach (SaveWithBackups save in saves)
            {
                SaveFile[] cloud = save.AllFiles.Where(file => file.m_source.IsCloud()).ToArray();
                if (cloud.Length == 0) continue;
                if (keep.Contains(save.Name))
                {
                    plan.Kept.Add(save.Name);
                    continue;
                }
                plan.Move.Add(save);
                plan.Files += cloud.Length;
                foreach (SaveFile file in cloud) plan.Bytes += file.Size;
            }
            return plan;
        }

        // Beside the earlier wipe's archive when there is one, so everything Fire may delete sits in one place.
        private static string ArchiveFolder()
        {
            string root = Utils.GetSaveDataPath(FileHelpers.FileSource.Local);
            string stamp = DateTime.Now.ToString(StampFormat);
            string oldArchive = Directory.GetDirectories(root, OldArchivePattern).OrderByDescending(path => path).FirstOrDefault();
            return oldArchive != null
                ? Path.Combine(oldArchive, ArchiveSubfolderPrefix + stamp)
                : Path.Combine(root, OwnArchivePrefix + stamp);
        }

        private enum Outcome
        {
            Moved,
            NotDownloaded,
            Failed,
        }

        private sealed class Tally
        {
            internal int Moved;
            internal int NotDownloaded;
            internal ulong MovedBytes;
            internal readonly List<string> Failed = new List<string>();
            internal readonly List<string> LeftForLater = new List<string>();
        }

        private static IEnumerator MoveAll(Plan plan, string archive)
        {
            var tally = new Tally();
            foreach (SaveWithBackups save in plan.Move)
            {
                int moved = 0, notDownloaded = 0, failed = 0;
                foreach (SaveFile file in save.AllFiles.Where(candidate => candidate.m_source.IsCloud()).ToArray())
                {
                    Outcome outcome = MoveOne(file, Path.Combine(archive, FiresWorldRecords.SafeName(save.Name)), out string problem);
                    if (outcome == Outcome.Moved)
                    {
                        moved++;
                        tally.MovedBytes += file.Size;
                    }
                    else if (outcome == Outcome.NotDownloaded) notDownloaded++;
                    else
                    {
                        failed++;
                        tally.Failed.Add($"{save.Name}: {problem}");
                    }
                    yield return null;
                }
                tally.Moved += moved;
                tally.NotDownloaded += notDownloaded;
                if (notDownloaded > 0) tally.LeftForLater.Add(save.Name);
                LogWorld(save.Name, moved, notDownloaded, failed);
            }
            SaveSystem.InvalidateCache(SaveDataType.World);
            WorldListContextMenu.RefreshWorldList();
            s_running = false;
            FiresLogger.LogInfo($"[FiresWorldGen] cloud worlds off the list: {tally.Moved} file set(s), "
                + $"{tally.MovedBytes / BytesPerMegabyte:0.#} MB archived in {archive}. "
                + (tally.NotDownloaded > 0
                    ? $"{tally.NotDownloaded} file set(s) are listed by Steam but not downloaded to this machine, so they stay on the "
                        + $"cloud until Steam brings them down; run the command again after a restart ({string.Join(", ", tally.LeftForLater.ToArray())}). "
                    : "")
                + (tally.Failed.Count > 0 ? $"{tally.Failed.Count} failed and stay on the cloud ({string.Join("; ", tally.Failed.ToArray())}). " : "")
                + "Nothing was deleted from disk.");
        }

        private static void LogWorld(string name, int moved, int notDownloaded, int failed)
        {
            if (notDownloaded == 0 && failed == 0)
            {
                FiresLogger.LogInfo($"[FiresWorldGen] cloud world '{name}' moved to the archive ({moved} file set(s)).");
                return;
            }
            FiresLogger.LogWarning($"[FiresWorldGen] cloud world '{name}' partly moved: {moved} file set(s) archived, "
                + $"{notDownloaded} listed by Steam but not on this machine (left on the cloud), {failed} failed.");
        }

        // One save file (a world or one of its backups): every path copied out of the cloud and checked on disk, and only
        // then the cloud copy removed through SaveSystem.Delete, so Steam drops it for good. A file Steam lists but has not
        // downloaded cannot be copied, so it is never removed: whatever cannot be archived stays where it is.
        private static Outcome MoveOne(SaveFile file, string folder, out string problem)
        {
            problem = null;
            var grouping = file.IsChunked ? CloudStorageFileGrouping.SameFolder : CloudStorageFileGrouping.SameFileEnding;
            var copied = new List<string>();
            if (!FileHelpers.Mount(SaveDataAccess.ReadWrite))
            {
                problem = "cloud storage could not be opened";
                return Outcome.Failed;
            }
            try
            {
                foreach (string path in file.AllPaths)
                {
                    string target = Path.Combine(folder, RelativeToWorlds(path));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (!FileHelpers.Copy(path, file.m_source, target, grouping, FileHelpers.FileSource.Local))
                    {
                        problem = $"{path} could not be copied";
                        return Outcome.Failed;
                    }
                    copied.Add(target);
                }
            }
            catch (FileNotFoundException)
            {
                return Outcome.NotDownloaded;
            }
            catch (Exception ex)
            {
                problem = $"{ex.GetType().Name}: {ex.Message}";
                return Outcome.Failed;
            }
            finally
            {
                FileHelpers.Unmount();
            }
            string missing = copied.FirstOrDefault(target => !File.Exists(target));
            if (missing != null) problem = $"{missing} is not on disk after the copy";
            else if ((ulong)copied.Sum(target => new FileInfo(target).Length) < file.Size) problem = "fewer bytes reached the disk than the cloud holds";
            if (problem != null) return Outcome.Failed;
            using (BcImport.BcSaveDeleteWatch.Expect(CommandName))
            {
                if (SaveSystem.Delete(file)) return Outcome.Moved;
            }
            problem = "the cloud copy could not be removed (the archived copy is kept)";
            return Outcome.Failed;
        }

        private static string RelativeToWorlds(string path)
        {
            string normal = path.Replace('\\', '/');
            int at = normal.LastIndexOf(WorldsSegment, StringComparison.OrdinalIgnoreCase);
            string relative = at >= 0 ? normal.Substring(at + WorldsSegment.Length) : Path.GetFileName(normal);
            return relative.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
