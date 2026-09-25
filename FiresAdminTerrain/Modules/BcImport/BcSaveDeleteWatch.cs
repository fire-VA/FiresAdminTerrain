using System;
using System.IO;
using HarmonyLib;

namespace VerdantsAscent.Terrain.BcImport
{
    // Catches whatever is deleting world saves, red-handed, with the stack that asked for it.
    //
    // A flat pre-1.0 world lost its .db and .fwl three times while its .BetterContinents sidecar survived - the
    // signature of SaveSystem.Delete on a non-chunked save, which removes file.AllPaths and nothing else. Neither
    // the game nor FAT logs a line when it happens, so four rounds of reading decompiled code produced four
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

        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Delete))]
        [HarmonyPrefix]
        private static void SaveSystemDelete(SaveFile file)
        {
            if (file == null) return;
            string what = file.IsChunked ? $"the whole folder {file.ChunkedDirectory}" : Paths(file);
            Report($"SaveSystem.Delete('{file.Name}') is about to remove {what}");
        }

        [HarmonyPatch(typeof(FileHelpers), nameof(FileHelpers.Delete), new[] { typeof(string), typeof(FileHelpers.FileSource) })]
        [HarmonyPrefix]
        // The parameter name must MATCH the target's - Harmony binds by name, and "file" failed to patch with
        // 'Parameter "file" not found'. FileHelpers.Delete and DeleteDirectory both call theirs `path`.
        private static void FileHelpersDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (path.IndexOf(WorldsFolderMarker, StringComparison.OrdinalIgnoreCase) < 0) return;
            Report($"FileHelpers.Delete is about to remove {path}");
        }

        [HarmonyPatch(typeof(FileHelpers), nameof(FileHelpers.DeleteDirectory), new[] { typeof(string), typeof(FileHelpers.FileSource) })]
        [HarmonyPrefix]
        private static void FileHelpersDeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            Report($"FileHelpers.DeleteDirectory is about to remove {path}");
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
            FiresLogger.LogWarning($"[SaveWatch] {what}\n{Environment.StackTrace}");
        }
    }
}
