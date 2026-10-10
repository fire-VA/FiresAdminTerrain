using System;
using System.Collections.Generic;
using System.IO;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // Where a world's own records live: FiresWorldGen\<world>\firesgen_<world><suffix>, beside the save folders.
    // One folder per world (Fire 10-03: "one folder in each of those 3 places so we just zip the three to share"), so a
    // world is shared as worlds_local\<world>, FiresWorldGen\<world> and FiresVoxelCache\<world>. Records written loose by
    // an older build (FiresWorldGen\firesgen_<world>...) move into the world's folder the first time that world is used.
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

        private static readonly object s_migrateGate = new object();
        private static readonly HashSet<string> s_migrated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static string Folder => Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), FolderName);

        internal static string SafeName(string worldName) => string.Concat((worldName ?? "world").Split(Path.GetInvalidFileNameChars()));

        // The world's own folder, created on demand, with any loose records from an older build moved into it first.
        internal static string WorldFolder(string worldName)
        {
            string safe = SafeName(worldName);
            string dir = Path.Combine(Folder, safe);
            lock (s_migrateGate)
            {
                if (s_migrated.Add(safe)) MigrateLoose(safe, dir);
                else Directory.CreateDirectory(dir);
            }
            return dir;
        }

        internal static string PathFor(string worldName, string suffix)
            => Path.Combine(WorldFolder(worldName), FilePrefix + SafeName(worldName) + suffix);

        // Moves FiresWorldGen\firesgen_<safe>.* / firesgen_<safe>_* (files and folders) into FiresWorldGen\<safe>\. A name
        // that belongs to a LONGER world name (ATomTest vs ATomTest_X) is left for that world. When both a loose and a
        // folder copy exist, the newer one wins: rig scripts may still write a loose profile between sessions.
        private static void MigrateLoose(string safe, string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string root = Folder;
                if (!Directory.Exists(root)) return;
                string prefix = FilePrefix + safe;
                var longer = LongerWorldNames(safe);
                int moved = 0;
                foreach (string entry in Directory.GetFileSystemEntries(root))
                {
                    string name = Path.GetFileName(entry);
                    if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length) continue;
                    char next = name[prefix.Length];
                    if (next != '.' && next != '_') continue;
                    if (BelongsToLonger(name, longer)) continue;
                    string target = Path.Combine(dir, name);
                    try
                    {
                        if (Directory.Exists(entry))
                        {
                            if (Directory.Exists(target)) continue;
                            Directory.Move(entry, target);
                        }
                        else
                        {
                            if (File.Exists(target))
                            {
                                if (File.GetLastWriteTimeUtc(entry) <= File.GetLastWriteTimeUtc(target)) continue;
                                File.Delete(target);
                            }
                            File.Move(entry, target);
                        }
                        moved++;
                    }
                    catch (Exception ex) { FiresLogger.LogWarning($"[FiresWorldGen] could not move {name} into {safe}\\: {ex.Message}"); }
                }
                if (moved > 0)
                    FiresLogger.LogInfo($"[FiresWorldGen] moved {moved} loose record(s) of '{safe}' into FiresWorldGen\\{safe}\\ (one folder per world).");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresWorldGen] record folder for '{safe}': {ex.Message}"); }
        }

        // Other worlds whose safe name extends this one with '_' or '.', from the save folder and FiresWorldGen itself.
        private static List<string> LongerWorldNames(string safe)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string worlds = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
                if (Directory.Exists(worlds))
                {
                    foreach (string d in Directory.GetDirectories(worlds)) names.Add(SafeName(Path.GetFileName(d)));
                    foreach (string f in Directory.GetFiles(worlds, "*.fwl")) names.Add(SafeName(Path.GetFileNameWithoutExtension(f)));
                }
                if (Directory.Exists(Folder))
                    foreach (string d in Directory.GetDirectories(Folder))
                    {
                        string n = Path.GetFileName(d);
                        if (!n.StartsWith(FilePrefix, StringComparison.Ordinal)) names.Add(n);
                    }
            }
            catch { }
            var longer = new List<string>();
            foreach (string n in names)
                if (n.Length > safe.Length && n.StartsWith(safe, StringComparison.Ordinal) && (n[safe.Length] == '_' || n[safe.Length] == '.'))
                    longer.Add(n);
            longer.Sort((a, b) => b.Length.CompareTo(a.Length));
            return longer;
        }

        private static bool BelongsToLonger(string fileName, List<string> longer)
        {
            foreach (string n in longer)
            {
                string p = FilePrefix + n;
                if (fileName.StartsWith(p, StringComparison.Ordinal)
                    && (fileName.Length == p.Length || fileName[p.Length] == '.' || fileName[p.Length] == '_'))
                    return true;
            }
            return false;
        }

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
            var leftovers = new List<string> { park };
            string parkParent = Path.GetDirectoryName(park);
            if (Directory.Exists(parkParent)) leftovers.AddRange(Directory.GetDirectories(parkParent, Path.GetFileName(park) + ".*"));
            foreach (string folder in leftovers)
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
