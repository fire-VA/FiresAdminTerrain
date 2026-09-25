using System.IO;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // Where a world's own records live: FiresWorldGen\firesgen_<world><suffix>, beside the save folders.
    internal static class FiresWorldRecords
    {
        internal const string FolderName = "FiresWorldGen";
        internal const string FilePrefix = "firesgen_";

        // What a world's play leaves behind. Bakes and seed grids are not here: a bake may be made for a world before it
        // exists, and a seed grid is the same for every world with that seed.
        private static readonly string[] PlayRecordSuffixes =
        {
            ".profile", ".mode", ".convert", "_zones.bin", "_zones.bin.rejected", "_hammer.txt", "_caves.txt", "_islands.bin", "_skylands.txt", BcImport.BcWorldData.RecordsFolderSuffix,
            FiresWaterGuards.FileSuffix,
        };

        internal static string Folder => Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), FolderName);

        internal static string SafeName(string worldName) => string.Concat((worldName ?? "world").Split(Path.GetInvalidFileNameChars()));

        internal static string PathFor(string worldName, string suffix) => Path.Combine(Folder, FilePrefix + SafeName(worldName) + suffix);

        // A world made under a name that a deleted world had would otherwise inherit that world's records.
        internal static void ClearLeftovers(string worldName)
        {
            foreach (string suffix in PlayRecordSuffixes)
            {
                string path = PathFor(worldName, suffix);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                ReportCleared(Path.GetFileName(path), worldName);
            }
            string park = ZoneModePark.FolderFor(worldName);
            foreach (string folder in new[] { park, park + ".rejected", park + ".staging" })
            {
                if (!Directory.Exists(folder)) continue;
                Directory.Delete(folder, true);
                ReportCleared(Path.GetFileName(folder), worldName);
            }
            string paintedBiomes = Biome.BiomeFilePersistence.LocalPathFor(worldName);
            if (!File.Exists(paintedBiomes)) return;
            File.Delete(paintedBiomes);
            ReportCleared(Path.GetFileName(paintedBiomes), worldName);
        }

        private static void ReportCleared(string record, string worldName)
            => FiresLogger.LogInfo($"[FiresWorldGen] removed {record}, left by a deleted world that was called '{worldName}'.");
    }
}
