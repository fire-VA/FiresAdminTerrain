using System;
using System.IO;
using HarmonyLib;
using Splatform;

namespace VerdantsAscent.Terrain.BcImport
{
    // Carrying FAT's migrated-world record through Valheim's own save plumbing. 1.0 handles only its five save
    // extensions, so an extension-less file is ignored by a copy, left behind by a cloud rename, and DELETED by a
    // local one. Nothing in FAT hooked any of this before.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal static class BcSaveSystemPaths
    {
        public static readonly char[] DirectorySeparators = { '/', '\\' };

        public static string RecordIn(string directory) =>
            string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, BcWorldData.FileName);

        public static string StashPathFor(string directory)
        {
            string parent = Path.GetDirectoryName(directory?.TrimEnd(BcSaveSystemPaths.DirectorySeparators));
            return string.IsNullOrEmpty(parent)
                ? null
                : Path.Combine(parent, BcWorldData.FileName + ".carrying-" + Guid.NewGuid().ToString("N"));
        }

        /// <summary>The record inside a SIBLING folder of <paramref name="directory"/>, built in the forward-slash
        /// form Valheim's cloud paths use (vanilla's own Rename composes its cloud destinations the same way).</summary>
        public static string RecordInSibling(string directory, string folderName)
        {
            string parent = Path.GetDirectoryName(directory?.TrimEnd(DirectorySeparators));
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folderName)) return null;
            return parent.Replace('\\', '/').TrimEnd('/') + "/" + folderName + "/" + BcWorldData.FileName;
        }
    }

    // Every rename in the save system comes through SaveSystem.Rename, which has TWO branches that each lose the
    // record a different way:
    //   chunked + NOT cloud -> RenameDirectory, which Directory.Moves the folder and then File.Delete()s every
    //                          file in it whose extension is not in s_saveFileExtensions. The record must be
    //                          parked OUTSIDE the folder before the call and put back after.
    //   chunked + cloud     -> the files are CloudMoved one at a time from SaveFile.AllPaths, which the record is
    //                          not a member of, so vanilla simply leaves it in the old folder.
    // Hooked here rather than on RenameDirectory so ONE hook covers both. This is also the backup path:
    // MoveToBackup is just Rename(file, "<name>_backup_<date>"), and RestoreBackup renames the live world aside
    // through the same method before copying the backup over it.
    [HarmonyPatch(typeof(SaveSystem), "Rename")]
    internal static class Patch_SaveSystem_Rename_BcImport
    {
        internal struct Carry
        {
            public string Stash;        // local: the record parked outside the folder
            public string CloudRecord;  // cloud: the record still sitting in the old folder
            public string Destination;  // cloud: where it must end up
        }

        [HarmonyPrefix]
        private static void Prefix(SaveFile file, string newName, out Carry __state)
        {
            __state = default(Carry);
            try
            {
                if (file == null || !file.IsChunked) return;
                string record = BcSaveSystemPaths.RecordIn(file.ChunkedDirectory);
                if (record == null || !FileHelpers.Exists(record, file.m_source)) return;

                if (file.m_source.IsCloud())
                {
                    __state.CloudRecord = record;
                    __state.Destination = BcSaveSystemPaths.RecordInSibling(file.ChunkedDirectory, newName);
                    return;
                }

                string stash = BcSaveSystemPaths.StashPathFor(file.ChunkedDirectory);
                if (stash == null) return;
                File.Move(record, stash);
                __state.Stash = stash;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BcImport] could not set the migrated record aside before a rename, so the rename "
                    + $"may drop it: {ex.Message}");
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer(SaveFile file, string newName, bool __result, Carry __state)
        {
            if (__state.CloudRecord != null) { CarryInCloud(__result, __state); return; }
            if (string.IsNullOrEmpty(__state.Stash) || !File.Exists(__state.Stash)) return;
            try
            {
                string parent = Path.GetDirectoryName(file.ChunkedDirectory.TrimEnd(BcSaveSystemPaths.DirectorySeparators));
                string renamed = Path.Combine(parent, newName);
                string destination = Directory.Exists(renamed)
                    ? BcSaveSystemPaths.RecordIn(renamed)
                    : BcSaveSystemPaths.RecordIn(file.ChunkedDirectory);

                if (destination == null || !Directory.Exists(Path.GetDirectoryName(destination)))
                {
                    FiresLogger.LogWarning($"[BcImport] the migrated record is parked at {__state.Stash}: neither the old nor "
                        + "the new world folder exists to put it back into. Move it into the world folder by hand.");
                    return;
                }
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(__state.Stash, destination);
                FiresLogger.LogInfo($"[BcImport] carried the migrated record through the rename to {destination}");
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[BcImport] the migrated record is parked at {__state.Stash} and could not be put back "
                    + $"({ex.Message}). Move it into the world folder by hand or the world generates as vanilla.");
            }
        }

        /// <summary>Only moved when the rename actually succeeded: on failure the world keeps its old folder, and
        /// the record is already in it.</summary>
        private static void CarryInCloud(bool renamed, Carry state)
        {
            if (!renamed || state.Destination == null) return;
            try
            {
                if (!FileHelpers.Mount(SaveDataAccess.ReadWrite)) return;
                try
                {
                    if (FileHelpers.CloudMove(state.CloudRecord, state.Destination, CloudStorageFileGrouping.SameFolder))
                        FiresLogger.LogInfo($"[BcImport] carried the migrated record through the cloud rename to {state.Destination}");
                    else
                        FiresLogger.LogWarning($"[BcImport] the cloud rename left the migrated record at {state.CloudRecord} "
                            + $"instead of {state.Destination}, so that world generates as vanilla until it is converted "
                            + "again. Move it by hand if you want it back.");
                }
                finally
                {
                    FileHelpers.Unmount();
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[BcImport] the migrated record could not follow a cloud rename ({ex.Message}); it is "
                    + $"still at {state.CloudRecord}.");
            }
        }
    }

    // A copy filters by save extension, so the record is left behind. Copied after the fact, through
    // FileHelpers and not File.Copy: a copy's destination can be the cloud (SaveSystem.Copy passes
    // FileSource.Cloud when a backup is copied into it), so either side can be a cloud path.
    [HarmonyPatch(typeof(SaveSystem), "CopyDirectory")]
    internal static class Patch_SaveSystem_CopyDirectory_BcImport
    {
        [HarmonyPostfix]
        private static void Postfix(SaveFile file, string newName, FileHelpers.FileSource destinationLocation, bool __result)
        {
            if (!__result || file == null || !file.IsChunked) return;
            try
            {
                string source = BcSaveSystemPaths.RecordIn(file.ChunkedDirectory);
                if (source == null || !FileHelpers.Exists(source, file.m_source)) return;

                string destination = BcSaveSystemPaths.RecordIn(newName.TrimEnd(BcSaveSystemPaths.DirectorySeparators));
                if (destination == null) return;

                FileHelpers.FileSource destinationSource =
                    destinationLocation.IsAuto() ? file.m_source : destinationLocation;

                if (!FileHelpers.Mount(SaveDataAccess.ReadWrite)) return;
                try
                {
                    if (FileHelpers.Copy(source, file.m_source, destination,
                            CloudStorageFileGrouping.SameFolder, destinationSource))
                        FiresLogger.LogInfo($"[BcImport] carried the migrated record into the copy at {destination}");
                    else
                        FiresLogger.LogWarning($"[BcImport] the copy at {destination} did NOT get the migrated record, so it "
                            + "generates as vanilla until it is converted again.");
                }
                finally
                {
                    FileHelpers.Unmount();
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BcImport] the copied world did not get the migrated record ({ex.Message}), so it "
                    + "generates as vanilla until it is converted again.");
            }
        }
    }

    // Local <-> Cloud. The record is not in SaveFile.AllPaths, so it is copied through FileHelpers like the rest.
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.MoveSource))]
    internal static class Patch_SaveSystem_MoveSource_BcImport
    {
        [HarmonyPostfix]
        private static void Postfix(SaveFile file, FileHelpers.FileSource destinationSource, bool __result)
        {
            if (!__result || file == null || !file.IsChunked) return;
            try
            {
                string source = BcSaveSystemPaths.RecordIn(file.ChunkedDirectory);
                if (source == null || !FileHelpers.Exists(source, file.m_source)) return;

                string folderName = Path.GetFileName(file.ChunkedDirectory.TrimEnd(BcSaveSystemPaths.DirectorySeparators));
                string destination = $"{SaveSystem.GetWorldsSaveRootPath(destinationSource)}/{folderName}/{BcWorldData.FileName}";

                if (!FileHelpers.Mount(SaveDataAccess.ReadWrite)) return;
                try
                {
                    bool copied = FileHelpers.Copy(source, file.m_source, destination,
                        CloudStorageFileGrouping.SameFolder, destinationSource);
                    if (copied) FiresLogger.LogInfo($"[BcImport] carried the migrated record to {destination}");
                    else
                        FiresLogger.LogWarning($"[BcImport] the world moved to {destinationSource} WITHOUT its migrated "
                            + "record, so it generates as vanilla there until it is converted again.");
                }
                finally
                {
                    FileHelpers.Unmount();
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BcImport] the migrated record did not follow the world to {destinationSource} "
                    + $"({ex.Message}).");
            }
        }
    }
}
