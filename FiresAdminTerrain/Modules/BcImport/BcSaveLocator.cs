using System.Collections.Generic;

namespace VerdantsAscent.Terrain.BcImport
{
    internal enum BcSaveKind
    {
        InsideWorldFolder,
        InsideWorldFolderNamed,
        BesideWorld,
        BesideWorldMeta,
        KeptByPortUpgrade,
        FatRecordsFolder,
        ReceivedFromServer,
    }

    internal struct BcSaveLocation
    {
        public string Path;
        public FileHelpers.FileSource Source;
        public BcSaveKind Kind;

        public string Describe() => $"{Path} ({Source}, {Kind})";
    }

    /// <summary>
    /// Finds a world's BetterContinents settings file. Four names are in use across BC versions and the 1.0
    /// port, and SaveSystem.CheckMove rewrites a world's file source before its first save, so every storage
    /// root is probed rather than the world's current one. Spec: Docs/BETTERCONTINENTS_IMPORT.md
    /// </summary>
    internal static class BcSaveLocator
    {
        private const string InsideWorldFolderFileName = "BetterContinents";
        // The layout every chunked world on this machine actually uses: the file sits INSIDE the world folder
        // and keeps the world-name prefix - `worlds_local\CratersAshlands\CratersAshlands.BetterContinents`.
        // Probing only the bare in-folder name missed it, which is why a converted-to-chunked BC world reported
        // "nothing to convert" while its 147 MB of map data sat right there (2026-09-24).
        private const string BesideWorldExtension = ".BetterContinents";
        private const string BesideWorldMetaExtension = ".fwl.BetterContinents";
        private const string KeptByPortUpgradeExtension = ".BetterContinents.pre10";

        public static readonly FileHelpers.FileSource[] SearchedSources =
        {
            FileHelpers.FileSource.Local,
            FileHelpers.FileSource.Cloud,
            FileHelpers.FileSource.Legacy,
        };

        /// <summary>The file BC itself would load: the port's in-folder copy wins over the older flat names,
        /// and the copy the upgrade set aside is the last resort.</summary>
        public static bool TryLocate(string worldName, out BcSaveLocation location)
        {
            List<BcSaveLocation> found = FindAll(worldName);
            if (found.Count == 0)
            {
                location = default(BcSaveLocation);
                return false;
            }
            location = found[0];
            return true;
        }

        public static List<BcSaveLocation> FindAll(string worldName)
        {
            var found = new List<BcSaveLocation>();
            if (string.IsNullOrEmpty(worldName)) return found;

            foreach (FileHelpers.FileSource source in SearchedSources)
            {
                string root = SaveSystem.GetWorldsSaveRootPath(source);
                AddIfPresent(found, $"{root}/{worldName}/{InsideWorldFolderFileName}", source, BcSaveKind.InsideWorldFolder);
                AddIfPresent(found, $"{root}/{worldName}/{worldName}{BesideWorldExtension}", source, BcSaveKind.InsideWorldFolderNamed);
                AddIfPresent(found, $"{root}/{worldName}{BesideWorldExtension}", source, BcSaveKind.BesideWorld);
                AddIfPresent(found, $"{root}/{worldName}{BesideWorldMetaExtension}", source, BcSaveKind.BesideWorldMeta);
                AddIfPresent(found, $"{root}/{worldName}{KeptByPortUpgradeExtension}", source, BcSaveKind.KeptByPortUpgrade);
            }
            found.Sort((first, second) => first.Kind.CompareTo(second.Kind));
            return found;
        }

        private static void AddIfPresent(List<BcSaveLocation> found, string path, FileHelpers.FileSource source, BcSaveKind kind)
        {
            if (!FileHelpers.Exists(path, source)) return;
            found.Add(new BcSaveLocation { Path = path, Source = source, Kind = kind });
        }
    }
}
