using System;
using System.IO;
using HarmonyLib;

namespace VerdantsAscent.Terrain.BcImport
{
    // Catches whatever is deleting world saves, red-handed, with the stack that asked for it.
    //
    // A flat pre-1.0 world lost its .db and .fwl three times while its .BetterContinents sidecar survived - the
    // signature of SaveSystem.Delete on a non-chunked save, which removes file.AllPaths and nothing else. Neither
    // the game nor FAT logs a line when it happens, so four rounds of guessing at the cause produced four
    // plausible culprits and no proof. This produces proof: every delete of a world file is logged with its path
    // and the managed stack that called it.
    //
    // Deliberately a Prefix that does NOT skip the original - this watches, it does not block. Under HarmonyX a
    // prefix returning false would not stop other prefixes anyway, and silently refusing a delete the game meant
    // to do would corrupt a save set rather than protect it.
    [HarmonyPatch]
    internal static class BcSaveDeleteWatch
    {
        private const string WorldsFolderMarker = "worlds";
        private const string AutoBackupRotation = "vanilla's auto-backup rotation";
        private const string ChunkRotation = "vanilla's chunk save rotation";
        private const string OrphanChunkCleanup = "vanilla's orphan chunk cleanup at load";

        private static string s_expectedBy;
        private static int s_quietDeletes;
        private static int s_previousSaveFiles;

        /// <summary>A caller that deletes saves on purpose and logs each one itself (fires_worlds_off_cloud) opens this
        /// scope, so the watch stays quiet instead of printing a full stack for every file. Anything else still reports.</summary>
        internal readonly struct ExpectedScope : IDisposable
        {
            private readonly string _previous;

            internal ExpectedScope(string caller)
            {
                _previous = s_expectedBy;
                s_expectedBy = caller;
            }

            public void Dispose() => s_expectedBy = _previous;
        }

        internal static ExpectedScope Expect(string caller) => new ExpectedScope(caller);

        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Delete))]
        [HarmonyPrefix]
        private static void SaveSystemDelete(SaveFile file, out string __state)
        {
            __state = s_expectedBy;
            if (file == null) return;
            string what = file.IsChunked ? $"the whole folder {file.ChunkedDirectory}" : Paths(file);
            if (IsAutoBackup(PrimaryPath(file)))
            {
                if (s_expectedBy == null)
                    FiresLogger.LogInfo($"[SaveWatch] the game is rotating out an old auto-backup: '{file.Name}' ({what}).");
                s_expectedBy = AutoBackupRotation;
                return;
            }
            Report($"SaveSystem.Delete('{file.Name}') is about to remove {what}");
        }

        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Delete))]
        [HarmonyFinalizer]
        private static Exception SaveSystemDeleteDone(Exception __exception, string __state)
        {
            s_expectedBy = __state;
            return __exception;
        }

        // A 1.0 save writes each changed chunk as a new version, then deletes the version before it (ZDOMan.DeleteOldChunks);
        // a load removes chunk files no save mapping names (ZDOMan.LoadChunks; the park's ResetChunkStore leaves them on
        // purpose). Both are the game's own housekeeping: one line with the count, no stack (R24 2026-09-28: two stacks at
        // every dedi save). A failed save's revert (DeleteCurrentChunksAndRevert) still reports every file.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DeleteOldChunks))]
        [HarmonyPrefix]
        private static void ChunkRotationStart(out string __state)
        {
            SaveWatchStages.BeforeOldChunkRotation();
            __state = BeginQuiet(ChunkRotation);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DeleteOldChunks))]
        [HarmonyFinalizer]
        private static Exception ChunkRotationDone(Exception __exception, string __state) => EndQuiet(__exception, __state, ChunkRotation);

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.LoadChunks))]
        [HarmonyPrefix]
        private static void OrphanCleanupStart(out string __state) => __state = BeginQuiet(OrphanChunkCleanup);

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.LoadChunks))]
        [HarmonyFinalizer]
        private static Exception OrphanCleanupDone(Exception __exception, string __state) => EndQuiet(__exception, __state, OrphanChunkCleanup);

        private static string BeginQuiet(string scope)
        {
            string previous = s_expectedBy;
            if (previous != null) return previous;
            s_expectedBy = scope;
            s_quietDeletes = 0;
            return null;
        }

        private static Exception EndQuiet(Exception exception, string previous, string scope)
        {
            if (previous == null && (s_quietDeletes > 0 || s_previousSaveFiles > 0))
                FiresLogger.LogInfo($"[SaveWatch] {scope} removed "
                    + (s_previousSaveFiles > 0 ? $"the previous save's {s_previousSaveFiles} main file(s) and " : "")
                    + $"{s_quietDeletes} chunk file(s) of this world.");
            if (previous == null) s_previousSaveFiles = 0;
            s_expectedBy = previous;
            return exception;
        }

        [HarmonyPatch(typeof(FileHelpers), nameof(FileHelpers.Delete), new[] { typeof(string), typeof(FileHelpers.FileSource) })]
        [HarmonyPrefix]
        // The parameter name must MATCH the target's - Harmony binds by name, and "file" failed to patch with
        // 'Parameter "file" not found'. FileHelpers.Delete and DeleteDirectory both call theirs `path`.
        private static void FileHelpersDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (path.IndexOf(WorldsFolderMarker, StringComparison.OrdinalIgnoreCase) < 0) return;
            if (IsPreviousSaveFile(path))
            {
                s_previousSaveFiles++;
                return;
            }
            Report($"FileHelpers.Delete is about to remove {path}");
        }

        // A world save that succeeded removes the save before it (_main.<previous>.fwl2 / .db2 / .chunks / .ok), then the
        // old chunk versions (R25 2026-09-28: two stacks at every dedi save); the chunk rotation's line counts both. While a
        // save runs, the previous save's names differ from the current one's, so a failed save deleting its OWN files, or
        // any delete outside a save, still reports.
        private static bool IsPreviousSaveFile(string path)
        {
            try
            {
                string previous = SaveSystem.MainFwl2FileName(old: true);
                if (previous == SaveSystem.MainFwl2FileName()) return false;
                string name = Path.GetFileName(path);
                return name == previous || name == SaveSystem.MainDb2FileName(old: true)
                    || name == SaveSystem.MainChunksFileName(old: true) || name == SaveSystem.MainOKFileName(old: true);
            }
            catch (Exception) { return false; }
        }

        [HarmonyPatch(typeof(FileHelpers), nameof(FileHelpers.DeleteDirectory), new[] { typeof(string), typeof(FileHelpers.FileSource) })]
        [HarmonyPrefix]
        private static void FileHelpersDeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            Report($"FileHelpers.DeleteDirectory is about to remove {path}");
        }

        // Vanilla's own rotation (SaveSystem.ConsiderBackup) deletes the oldest "_backup_auto-" copy of a character or
        // world at every save or quit, picked with this same test. The watch names it in one line, without a stack, and
        // the file and folder deletes inside that SaveSystem.Delete stay quiet.
        private static bool IsAutoBackup(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                return SaveSystem.GetSaveInfo(path, out _, out SaveFileType type, out _, out _) && type == SaveFileType.AutoBackup;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string PrimaryPath(SaveFile file)
        {
            try
            {
                return file.PathPrimary;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Paths(SaveFile file)
        {
            try
            {
                string[] all = file.AllPaths;
                return all == null || all.Length == 0 ? "(no paths)" : string.Join(", ", all);
            }
            catch (Exception ex)
            {
                return $"(its paths could not be read: {ex.GetType().Name})";
            }
        }

        private static void Report(string what)
        {
            if (s_expectedBy != null)
            {
                s_quietDeletes++;
                return;
            }
            FiresLogger.LogWarning($"[SaveWatch] {what}\n{Environment.StackTrace}");
        }
    }
}
