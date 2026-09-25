using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Splatform;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    internal sealed class WorldCloneOptions
    {
        internal bool ChunkByChunk;
        internal ProfileTerrain? Terrain;
        internal bool? SkyIslands, Hydrology, Caves;
    }

    // Copies a world under a new name and a new world id and never touches the original. The copy is told how to be
    // built by its own profile; for one zone at a time it also gets an unseeded zone registry and the convert marker that
    // parks everything it has explored the first time it loads (ZoneModePark). It carries the original's generator bakes
    // and its cave and sky-island records. Vanilla's minimap caches stay behind: they would show land that is parked.
    // A world is found by its save's file name, which a renamed world no longer shares with the name inside it (the list
    // shows "name (file name)"); FAT's own records follow the name inside, as they do in play. The save is copied out of
    // whichever storage holds it, local or Steam Cloud, and the clone is always local.
    internal static class FiresWorldClone
    {
        private const string CommandName = "fires_world_clone";
        private const int MinWorldNameLength = 5;
        private const string StagingFolderPrefix = "_cloning_";
        private const string ChunkFileExtension = ".chunk";
        private const string MainFilePrefix = "_main.";
        private const string ChunkedMetaExtension = ".fwl2";
        private const string OldMetaExtension = ".fwl";
        private const string OldDataExtension = ".db";
        private const string ChunkByChunkWord = "chunks";
        private const string OnWord = "on";
        private const string OffWord = "off";
        private const double BytesPerMegabyte = 1024d * 1024d;

        private static readonly string[] CarriedRecordSuffixes = { "_baked.bin", "_baked.flow", "_caves.txt", "_islands.bin", "_skylands.txt" };
        private static readonly string[] BakeSuffixes = { "_baked.bin", "_baked.flow" };

        private static bool s_registered;

        internal static void RegisterCommands()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(CommandName,
                "Main menu: copy a world under a new name, leaving the original untouched. Usage: " + CommandName
                + " <world> <new name> [chunks] [heightmap|voxelwrap|voxelonly] [sky=on|off] [rivers=on|off] [caves=on|off]. "
                + "'chunks' makes the copy one zone at a time: what the world has explored is set aside zone by zone and returns "
                + "as it was when each zone is opened.",
                args =>
                {
                    if (args.Length < 3)
                    {
                        args.Context.AddString("Usage: " + CommandName + " <world> <new name> [chunks] [heightmap|voxelwrap|voxelonly] "
                            + "[sky=on|off] [rivers=on|off] [caves=on|off]");
                        return;
                    }
                    if (!TryParseOptions(args.Args, out var options, out string problem))
                    {
                        args.Context.AddString(problem);
                        return;
                    }
                    TryClone(args.Args[1], args.Args[2], options, out string report);
                    args.Context.AddString(report);
                    FiresLogger.LogInfo("[FiresWorldGen] " + report);
                },
                optionsFetcher: WorldNames, alwaysRefreshTabOptions: true);
        }

        private static List<string> WorldNames()
        {
            var names = new List<string>();
            var saves = SaveSystem.GetSavesByType(SaveDataType.World);
            if (saves == null) return names;
            foreach (var save in saves)
                if (!save.IsDeleted) names.Add(save.Name);
            return names;
        }

        private static bool TryParseOptions(string[] words, out WorldCloneOptions options, out string problem)
        {
            options = new WorldCloneOptions();
            problem = null;
            for (int i = 3; i < words.Length; i++)
            {
                string word = words[i].ToLowerInvariant();
                if (word == ChunkByChunkWord) { options.ChunkByChunk = true; continue; }
                if (Enum.TryParse(word, true, out ProfileTerrain terrain) && Enum.IsDefined(typeof(ProfileTerrain), terrain))
                {
                    options.Terrain = terrain;
                    continue;
                }
                int eq = word.IndexOf('=');
                bool? value = eq > 0 ? ParseSwitch(word.Substring(eq + 1)) : null;
                string key = eq > 0 ? word.Substring(0, eq) : word;
                if (value.HasValue && key == "sky") { options.SkyIslands = value; continue; }
                if (value.HasValue && key == "rivers") { options.Hydrology = value; continue; }
                if (value.HasValue && key == "caves") { options.Caves = value; continue; }
                problem = $"'{words[i]}' is not an option. Options: chunks, heightmap, voxelwrap, voxelonly, sky=on|off, rivers=on|off, caves=on|off.";
                return false;
            }
            return true;
        }

        private static bool? ParseSwitch(string value)
            => value == OnWord ? true : value == OffWord ? false : (bool?)null;

        // `saveName` is the world's save file name; the console also takes the name inside a renamed world.
        internal static bool TryClone(string saveName, string cloneName, WorldCloneOptions options, out string report)
        {
            if (ZNet.instance != null)
            {
                report = "Leave the world first: worlds are cloned from the main menu.";
                return false;
            }
            if (!TryLoadSource(saveName, out var save, out var source, out report)) return false;
            if (!IsUsableName(cloneName, out report)) return false;
            if (File.Exists(ZoneModeRegistry.RegistryPath(source.m_name)))
            {
                report = $"'{source.m_name}' is already played one zone at a time; cloning such a world is not supported yet.";
                return false;
            }
            var profile = ProfileFor(source.m_name, options);
            report = profile.Conflict();
            if (report != null) return false;

            long cloneUid = (long)cloneName.GetStableHashCode() + Utils.GenerateUID();
            string root = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
            string staging = Path.Combine(FiresWorldRecords.Folder, StagingFolderPrefix + FiresWorldRecords.SafeName(cloneName));
            var file = save.PrimaryFile;
            int saveFiles;
            long saveBytes;
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                StageSave(file, source, staging, cloneName, cloneUid, out saveFiles, out saveBytes);
                if (file.IsChunked) Directory.Move(staging, Path.Combine(root, cloneName));
                else
                {
                    foreach (string staged in Directory.GetFiles(staging)) File.Move(staged, Path.Combine(root, Path.GetFileName(staged)));
                    Directory.Delete(staging, true);
                }
            }
            catch (Exception ex)
            {
                report = $"Could not copy '{source.m_name}' ({ex.GetType().Name}: {ex.Message}). Nothing was made; the original is untouched.";
                return false;
            }
            SaveSystem.InvalidateCache(SaveDataType.World);

            int carried;
            try
            {
                FiresWorldRecords.ClearLeftovers(cloneName);
                carried = CarryRecords(source, cloneName);
                profile.CreatedUtc = DateTime.UtcNow;
                profile.Write(cloneName);
                if (options.ChunkByChunk)
                {
                    var cloneWorld = new World { m_name = cloneName, m_worldName = cloneName, m_uid = cloneUid, m_seed = source.m_seed, m_seedName = source.m_seedName };
                    if (!ZoneModeRegistry.CreateForNewWorld(cloneWorld, out string registryPath))
                        throw new IOException($"could not write {Path.GetFileName(registryPath)}");
                    ZoneModePark.WriteConvertMarker(cloneName, cloneUid, source.m_name);
                }
            }
            catch (Exception ex)
            {
                report = $"Copied '{source.m_name}' as '{cloneName}', but its records could not be written ({ex.GetType().Name}: {ex.Message}), "
                    + "so it loads as a plain copy. Delete it and clone again.";
                return false;
            }

            report = $"Cloned '{source.m_name}' as '{cloneName}': {saveFiles} save file(s), {saveBytes / BytesPerMegabyte:0.#} MB"
                + (file.m_source == FileHelpers.FileSource.Cloud ? " copied out of Steam Cloud into local storage" : "")
                + $", {carried} record(s) carried; {profile.Describe()}."
                + (options.ChunkByChunk
                    ? $" The first time '{cloneName}' loads, everything it has explored is set aside zone by zone and returns as it was when each zone is opened."
                    : "");
            return true;
        }

        private static bool TryLoadSource(string name, out SaveWithBackups save, out World source, out string problem)
        {
            source = null;
            if (!TryFindSave(name, out save, out problem)) return false;
            source = World.LoadWorld(save);
            if (source == null || source.m_dataError != World.SaveDataError.None)
            {
                problem = $"'{name}' could not be read ({source?.m_dataError.ToString() ?? "no world file"}).";
                return false;
            }
            return true;
        }

        // By save file name first; otherwise the one world whose own name it is.
        private static bool TryFindSave(string name, out SaveWithBackups save, out string problem)
        {
            problem = null;
            if (SaveSystem.TryGetSaveByName(name, SaveDataType.World, out save) && !save.IsDeleted) return true;
            save = null;
            string fileName = null;
            foreach (var world in SaveSystem.GetWorldList())
            {
                if (world == null || !string.Equals(world.m_name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName != null)
                {
                    problem = $"More than one world is called '{name}'; name the one to clone by its file name ('{fileName}' or "
                        + $"'{world.m_worldName}').";
                    return false;
                }
                fileName = world.m_worldName;
            }
            if (fileName != null && SaveSystem.TryGetSaveByName(fileName, SaveDataType.World, out save) && !save.IsDeleted) return true;
            save = null;
            problem = $"There is no world called '{name}'.";
            return false;
        }

        internal static bool IsUsableName(string cloneName, out string problem)
        {
            problem = null;
            string root = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
            if (string.IsNullOrEmpty(cloneName) || cloneName.Length < MinWorldNameLength)
                problem = $"A world name needs at least {MinWorldNameLength} characters.";
            else if (cloneName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || cloneName.Trim() != cloneName)
                problem = $"'{cloneName}' cannot be a world name.";
            else if (World.HaveWorld(cloneName))
                problem = $"There is already a world called '{cloneName}'.";
            else if (Directory.Exists(Path.Combine(root, cloneName)) || File.Exists(Path.Combine(root, cloneName + OldDataExtension))
                     || File.Exists(Path.Combine(root, cloneName + OldMetaExtension)))
                problem = $"worlds_local already holds files called '{cloneName}' from an earlier world; delete them or choose another name.";
            else
                foreach (string suffix in BakeSuffixes)
                {
                    string bake = FiresWorldRecords.PathFor(cloneName, suffix);
                    if (!File.Exists(bake)) continue;
                    problem = $"A baked terrain cache {Path.GetFileName(bake)} already exists and would replace the copied land; "
                        + "delete it or choose another name.";
                    break;
                }
            return problem == null;
        }

        // The save's own files, out of whichever storage holds them, into local staging: a chunked save's _main.<n>.* and
        // chunk files keep their names, an old save's .db and .fwl take the clone's. The world file is then rewritten
        // with the clone's name and id.
        private static void StageSave(SaveFile file, World source, string staging, string cloneName, long cloneUid, out int files, out long bytes)
        {
            files = 0;
            bytes = 0;
            string mainPrefix = MainFilePrefix + source.SaveNumber().ToString(CultureInfo.InvariantCulture) + ".";
            string meta = file.IsChunked ? mainPrefix + ChunkedMetaExtension.TrimStart('.') : cloneName + OldMetaExtension;
            var grouping = file.IsChunked ? CloudStorageFileGrouping.SameFolder : CloudStorageFileGrouping.SameFileEnding;
            if (!FileHelpers.Mount(SaveDataAccess.ReadWrite)) throw new IOException($"{file.m_source} save storage could not be opened");
            try
            {
                foreach (string path in file.AllPaths)
                {
                    string name = Path.GetFileName(path);
                    string staged;
                    if (file.IsChunked)
                    {
                        if (!name.EndsWith(ChunkFileExtension, StringComparison.OrdinalIgnoreCase)
                            && !name.StartsWith(mainPrefix, StringComparison.Ordinal)) continue;
                        staged = name;
                    }
                    else
                    {
                        if (!name.StartsWith(source.m_worldName, StringComparison.OrdinalIgnoreCase)) continue;
                        staged = cloneName + name.Substring(source.m_worldName.Length);
                    }
                    string target = Path.Combine(staging, staged);
                    if (!FileHelpers.Copy(path, file.m_source, target, grouping, FileHelpers.FileSource.Local))
                        throw new IOException($"{name} could not be copied out of {file.m_source} storage");
                    files++;
                    bytes += new FileInfo(target).Length;
                }
            }
            finally
            {
                FileHelpers.Unmount();
            }
            string metaPath = Path.Combine(staging, meta);
            if (!File.Exists(metaPath)) throw new FileNotFoundException($"the world file of '{source.m_worldName}' is missing from its save");
            File.WriteAllBytes(metaPath, RenamedWorldMeta(File.ReadAllBytes(metaPath), cloneName, cloneUid));
        }

        // A world's meta file is an int32 length and a ZPackage: version, name, seed name, seed, world uid, then the rest
        // (generator version, starting keys, player history), which is kept byte for byte.
        private static byte[] RenamedWorldMeta(byte[] file, string cloneName, long cloneUid)
        {
            int length = BitConverter.ToInt32(file, 0);
            var payload = new byte[length];
            Buffer.BlockCopy(file, sizeof(int), payload, 0, length);
            var original = new ZPackage(payload);
            int version = original.ReadInt();
            original.ReadString();
            string seedName = original.ReadString();
            int seed = original.ReadInt();
            original.ReadLong();
            int restStart = original.GetPos();

            var head = new ZPackage();
            head.Write(version);
            head.Write(cloneName);
            head.Write(seedName);
            head.Write(seed);
            head.Write(cloneUid);
            byte[] headBytes = head.GetArray();
            int restLength = payload.Length - restStart;
            var renamed = new byte[sizeof(int) + headBytes.Length + restLength];
            Buffer.BlockCopy(BitConverter.GetBytes(headBytes.Length + restLength), 0, renamed, 0, sizeof(int));
            Buffer.BlockCopy(headBytes, 0, renamed, sizeof(int), headBytes.Length);
            Buffer.BlockCopy(payload, restStart, renamed, sizeof(int) + headBytes.Length, restLength);
            return renamed;
        }

        private static int CarryRecords(World source, string cloneName)
        {
            int carried = 0;
            foreach (string suffix in CarriedRecordSuffixes)
            {
                string from = FiresWorldRecords.PathFor(source.m_name, suffix);
                if (!File.Exists(from)) continue;
                File.Copy(from, FiresWorldRecords.PathFor(cloneName, suffix));
                carried++;
            }
            string paintedBiomes = Biome.BiomeFilePersistence.LocalPathFor(source.m_name);
            if (File.Exists(paintedBiomes))
            {
                File.Copy(paintedBiomes, Biome.BiomeFilePersistence.LocalPathFor(cloneName));
                carried++;
            }
            string sourcePrefix = FiresWorldRecords.FilePrefix + FiresWorldRecords.SafeName(source.m_name) + "_";
            string clonePrefix = FiresWorldRecords.FilePrefix + FiresWorldRecords.SafeName(cloneName) + "_";
            foreach (string from in SeedCaches(source.m_name, source.m_seed))
            {
                File.Copy(from, Path.Combine(FiresWorldRecords.Folder, clonePrefix + Path.GetFileName(from).Substring(sourcePrefix.Length)), true);
                carried++;
            }
            return carried;
        }

        // firesgen_<world>_<seed>_r<res>[_vahc].bin: the generator's seed grid, minutes of work to rebuild and the same for
        // every world with that seed, so a leftover one under the clone's name is simply overwritten.
        private static IEnumerable<string> SeedCaches(string worldName, int seed)
        {
            string folder = FiresWorldRecords.Folder;
            if (!Directory.Exists(folder)) yield break;
            string prefix = FiresWorldRecords.FilePrefix + FiresWorldRecords.SafeName(worldName) + "_";
            foreach (string path in Directory.GetFiles(folder, prefix + "*_r*.bin"))
            {
                string rest = Path.GetFileName(path).Substring(prefix.Length);
                int split = rest.IndexOf("_r", StringComparison.Ordinal);
                if (split <= 0 || !int.TryParse(rest.Substring(0, split), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileSeed)) continue;
                if (fileSeed == seed) yield return path;
            }
        }

        internal static FiresWorldProfile OriginalProfile(string sourceName)
            => FiresWorldProfile.Read(sourceName) ?? FiresWorldProfile.Infer(sourceName);

        // The profile a clone of `sourceName` gets: the original's, with the options laid over it.
        internal static FiresWorldProfile ProfileFor(string sourceName, WorldCloneOptions options)
        {
            var profile = OriginalProfile(sourceName).Copy();
            profile.ZoneMode = options.ChunkByChunk;
            if (options.Terrain.HasValue) profile.Terrain = options.Terrain.Value;
            if (options.SkyIslands.HasValue) profile.SkyIslands = options.SkyIslands;
            if (options.Hydrology.HasValue) profile.Hydrology = options.Hydrology;
            if (options.Caves.HasValue) profile.Caves = options.Caves;
            profile.Source = FiresWorldProfile.CloneSourcePrefix + sourceName;
            return profile;
        }
    }
}
