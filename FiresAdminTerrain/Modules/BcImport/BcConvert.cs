using System;
using System.Diagnostics;
using System.IO;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.BcImport
{
    // Converting a BetterContinents world to 1.0 NEVER touches the original. It clones the world first through
    // FAT's own cloner, then gives the clone FAT's migrated record, so the source can be opened afterwards and is
    // byte for byte what it was. Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal static class BcConvert
    {
        public const string CloneSuffix = "-1.0";

        public static string ProposeCloneName(string sourceName)
        {
            string candidate = sourceName + CloneSuffix;
            int attempt = 2;
            while (World.HaveWorld(candidate)) candidate = $"{sourceName}{CloneSuffix}-{attempt++}";
            return candidate;
        }

        public static bool CanConvert(string worldName, out BcSaveLocation source, out string reason)
        {
            reason = null;
            source = default(BcSaveLocation);
            if (BcWorldData.Exists(worldName, out BcSaveLocation existing))
            {
                reason = $"'{worldName}' is already converted ({existing.Path}).";
                return false;
            }
            if (!BcSaveLocator.TryLocate(worldName, out source))
            {
                reason = $"'{worldName}' has no BetterContinents data beside it, so there is nothing to convert.";
                return false;
            }
            return true;
        }

        /// <summary>Clone, then convert the clone. Every failure leaves the source alone and says what happened.</summary>
        public static bool TryConvert(string sourceName, string cloneName, out string report)
        {
            report = null;
            if (!CanConvert(sourceName, out BcSaveLocation source, out string reason))
            {
                report = reason;
                return false;
            }
            if (!FiresWorldClone.IsUsableName(cloneName, out string nameProblem))
            {
                report = nameProblem;
                return false;
            }

            var timer = Stopwatch.StartNew();
            if (!BcSaveReader.TryReadFile(source, out BcWorldSettings settings, out string readFailure))
            {
                report = $"{source.Describe()} was refused, so nothing was cloned: {readFailure}";
                return false;
            }
            if (!settings.EnabledForThisWorld)
            {
                report = $"{source.Describe()} says BetterContinents was switched off for '{sourceName}', so there is "
                    + "nothing to convert.";
                return false;
            }

            if (!FiresWorldClone.TryClone(sourceName, cloneName, new WorldCloneOptions(), out string cloneReport))
            {
                report = $"'{sourceName}' was NOT cloned, so nothing was converted: {cloneReport}";
                return false;
            }

            if (!TryGiveCloneTheData(cloneName, source, settings, out string ownership))
            {
                report = $"Cloned '{sourceName}' as '{cloneName}', but {ownership} The original is untouched.";
                return false;
            }

            report = $"Converted '{sourceName}' to '{cloneName}' in {timer.ElapsedMilliseconds} ms. {ownership} "
                + "The original is untouched - open it any time and it is exactly what it was.";
            return true;
        }

        /// <summary>The clone gets FAT's record when it already has a world folder; a clone of a pre-1.0 world is
        /// still flat, so it gets its own copy of the BetterContinents file instead and takes ownership the first
        /// time Valheim converts the save. Either way the clone is self-contained.</summary>
        private static bool TryGiveCloneTheData(string cloneName, BcSaveLocation source, BcWorldSettings settings, out string ownership)
        {
            if (BcWorldData.TryWrite(cloneName, FileHelpers.FileSource.Local, settings, out string writeFailure))
            {
                ownership = $"'{cloneName}' now carries FAT's own terrain data ({BcWorldData.FileName} in its world folder).";
                return true;
            }

            if (!TryCopyBetterContinentsFile(cloneName, source, out string copyFailure))
            {
                ownership = $"the clone could not be given the terrain data ({writeFailure}; {copyFailure}), so it would "
                    + "generate as vanilla.";
                return false;
            }

            ownership = $"'{cloneName}' carries its own copy of the BetterContinents data and takes full ownership the "
                + "first time it loads, once Valheim has converted the save to the 1.0 format.";
            return true;
        }

        private static bool TryCopyBetterContinentsFile(string cloneName, BcSaveLocation source, out string failure)
        {
            failure = null;
            try
            {
                string root = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
                string destination = $"{root}/{cloneName}.BetterContinents";
                File.Copy(source.Path, destination, true);
                FiresLogger.LogInfo($"[BcImport] copied {source.Path} to {destination} so the clone is self-contained.");
                return true;
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
    }
}
