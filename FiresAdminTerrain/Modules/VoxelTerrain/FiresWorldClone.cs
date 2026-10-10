using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using Splatform;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    internal sealed class WorldCloneOptions
    {
        internal bool ChunkByChunk;
        // Null means "keep whatever the source world was built as". A caller that must not inherit this
        // installation's config - a BetterContinents conversion, whose whole promise is the same map on vanilla
        // terrain - sets Generator AND Terrain explicitly, because a source world with no profile falls through to
        // FiresWorldProfile.Infer, and Infer reads the local FiresVoxelConfig.
        internal ProfileGenerator? Generator;
        internal ProfileTerrain? Terrain;
        internal bool? SkyIslands, Hydrology, Caves;
        internal bool? SkyIslandWater;   // 0.2.727: false = the islands are born dry (New World's "Auto-place water on sky islands" off)
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
        private const int CopyChunkBytes = 4 * 1024 * 1024;
        private const float PercentScale = 100f;

        private const string BakeSuffix = "_baked.bin";
        private const string BakeFlowSuffix = "_baked.flow";
        private const string CavesSuffix = "_caves.txt";
        private const string IslandsSuffix = "_islands.bin";
        private const string SkylandsSuffix = "_skylands.txt";

        private static readonly string[] CarriedRecordSuffixes =
            { BakeSuffix, BakeFlowSuffix, CavesSuffix, IslandsSuffix, SkylandsSuffix, BcImport.BcWorldData.RecordsFolderSuffix };

        private static bool s_registered;

        internal static void RegisterCommands()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(CommandName,
                "Main menu: copy a world under a new name, leaving the original untouched. Usage: " + CommandName
                + " <world> <new name> [chunks] [vanilla|fires] [heightmap|voxelwrap|voxelonly] [sky=on|off] [skywater=on|off] [rivers=on|off] [caves=on|off]. "
                + "'chunks' makes the copy one zone at a time: what the world has explored is set aside zone by zone and returns "
                + "as it was when each zone is opened.",
                args =>
                {
                    if (args.Length < 3)
                    {
                        args.Context.AddString("Usage: " + CommandName + " <world> <new name> [chunks] [vanilla|fires] [heightmap|voxelwrap|voxelonly] "
                            + "[sky=on|off] [skywater=on|off] [rivers=on|off] [caves=on|off]");
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
                if (Enum.TryParse(word, true, out ProfileGenerator generator) && Enum.IsDefined(typeof(ProfileGenerator), generator))
                {
                    options.Generator = generator;
                    continue;
                }
                int eq = word.IndexOf('=');
                bool? value = eq > 0 ? ParseSwitch(word.Substring(eq + 1)) : null;
                string key = eq > 0 ? word.Substring(0, eq) : word;
                if (value.HasValue && key == "sky") { options.SkyIslands = value; continue; }
                if (value.HasValue && key == "skywater") { options.SkyIslandWater = value; continue; }
                if (value.HasValue && key == "rivers") { options.Hydrology = value; continue; }
                if (value.HasValue && key == "caves") { options.Caves = value; continue; }
                problem = $"'{words[i]}' is not an option. Options: chunks, vanilla, fires, heightmap, voxelwrap, voxelonly, sky=on|off, skywater=on|off, rivers=on|off, caves=on|off.";
                return false;
            }

            return true;
        }

        private static bool? ParseSwitch(string value)
            => value == OnWord ? true : value == OffWord ? false : (bool?)null;

        // `saveName` is the world's save file name; the console also takes the name inside a renamed world.
        /// <summary>Blocking clone, for the console command and anything else with no frame to give. Drains the
        /// same coroutine the menus use, so there is one implementation.</summary>
        internal static bool TryClone(string saveName, string cloneName, WorldCloneOptions options, out string report)
        {
            bool done = false;
            string message = null;
            var run = CloneAsync(saveName, cloneName, options, null, (ok, text) => { done = ok; message = text; });
            while (run.MoveNext()) { }
            report = message;
            return done;
        }

        // Copying a world used to be one blocking FileHelpers.Copy per file on the main thread. A 451 MB .db froze
        // the game for as long as the disk took, every time, which is indistinguishable from a hang. The copy is
        // now chunked and yields between chunks, and every step says where it is.
        internal static IEnumerator CloneAsync(string saveName, string cloneName, WorldCloneOptions options,
            Action<string> progress, Action<bool, string> done)
        {
            if (ZNet.instance != null)
            {
                done(false, "Leave the world first: worlds are cloned from the main menu.");
                yield break;
            }
            SaveWithBackups save;
            World source;
            string problem;
            if (!TryLoadSource(saveName, out save, out source, out problem)) { done(false, problem); yield break; }
            if (!IsUsableName(cloneName, out problem)) { done(false, problem); yield break; }
            if (File.Exists(ZoneModeRegistry.RegistryPath(source.m_name)))
            {
                done(false, $"'{source.m_name}' is already played one zone at a time; cloning such a world is not supported yet.");
                yield break;
            }
            var profile = ProfileFor(source.m_name, options);
            string conflict = profile.Conflict();
            if (conflict != null) { done(false, conflict); yield break; }

            long cloneUid = (long)cloneName.GetStableHashCode() + Utils.GenerateUID();
            string root = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
            string staging = Path.Combine(FiresWorldRecords.Folder, StagingFolderPrefix + FiresWorldRecords.SafeName(cloneName));
            var file = save.PrimaryFile;
            var staged = new StageCounts();

            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
            }
            catch (Exception ex)
            {
                done(false, $"Could not prepare a staging folder for '{cloneName}' ({ex.GetType().Name}: {ex.Message}).");
                yield break;
            }

            // MoveNext inside the try, yield OUTSIDE it: C# forbids `yield return` in a try that has a catch.
            var copy = StageSaveAsync(file, source, staging, cloneName, cloneUid, progress, staged);
            while (true)
            {
                bool moving;
                try
                {
                    moving = copy.MoveNext();
                }
                catch (Exception ex)
                {
                    Discard(staging);
                    done(false, $"Could not copy '{source.m_name}' ({ex.GetType().Name}: {ex.Message}). Nothing was "
                        + "made; the original is untouched.");
                    yield break;
                }
                if (!moving) break;
                yield return null;
            }

            try
            {
                if (file.IsChunked) Directory.Move(staging, Path.Combine(root, cloneName));
                else
                {
                    foreach (string path in Directory.GetFiles(staging)) File.Move(path, Path.Combine(root, Path.GetFileName(path)));
                    Directory.Delete(staging, true);
                }
            }
            catch (Exception ex)
            {
                Discard(staging);
                done(false, $"Could not put '{cloneName}' in place ({ex.GetType().Name}: {ex.Message}). Nothing was "
                    + "made; the original is untouched.");
                yield break;
            }
            SaveSystem.InvalidateCache(SaveDataType.World);

            int saveFiles = staged.Files;
            long saveBytes = staged.Bytes;
            int carried;
            string report;
            try
            {
                FiresWorldRecords.ClearLeftovers(cloneName);
                carried = CarryRecords(source, cloneName, profile);
                // BC's file is named after the SAVE ("<save name>.BetterContinents"), and a renamed world's save name is not
                // the name inside it: CratersAshlands.BetterContinents holds 'CratersAshlands', so looking it up by the inner
                // name found nothing and the clone silently lost its whole BC map (2026-09-29, -firesclone CratersBCWrap).
                string bcName = BcImport.BcSaveLocator.TryLocate(saveName, out _) ? saveName : source.m_name;
                if (!BcImport.BcConvert.TryCarryTerrainToClone(bcName, cloneName, out string terrain))
                    FiresLogger.LogError($"[FiresWorldGen] {terrain}");
                else if (terrain != null)
                    FiresLogger.LogInfo($"[FiresWorldGen] {terrain}");
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
                done(false, $"Copied '{source.m_name}' as '{cloneName}', but its records could not be written "
                    + $"({ex.GetType().Name}: {ex.Message}), so it loads as a plain copy. Delete it and clone again.");
                yield break;
            }

            report = $"Cloned '{source.m_name}' as '{cloneName}': {saveFiles} save file(s), {saveBytes / BytesPerMegabyte:0.#} MB"
                + (file.m_source == FileHelpers.FileSource.Cloud ? " copied out of Steam Cloud into local storage" : "")
                + $", {carried} record(s) carried; {profile.Describe()}."
                + (options.ChunkByChunk
                    ? $" The first time '{cloneName}' loads, everything it has explored is set aside zone by zone and returns as it was when each zone is opened."
                    : "");
            done(true, report);
        }

        private sealed class StageCounts
        {
            internal int Files;
            internal long Bytes;
        }

        private static void Discard(string staging)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresWorldGen] staging {staging} could not be cleared ({ex.Message})."); }
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
        // A world is named twice - m_name is what is inside the .fwl and m_worldName is the save file - and the two
        // differ on any renamed world. The old code matched only on m_name and then looked the save up by
        // m_worldName, so a world whose m_worldName is empty (a pre-1.0 flat save carries no 1.0 file name) failed
        // the second lookup and reported "there is no world called X" about a world sitting in the list.
        // Matching on EITHER name and falling back to m_name fixes that; when it still fails the message now says
        // what was actually in the list, because "no world called X" is not something anyone can act on.
        private static bool TryFindSave(string name, out SaveWithBackups save, out string problem)
        {
            problem = null;
            if (SaveSystem.TryGetSaveByName(name, SaveDataType.World, out save) && !save.IsDeleted) return true;

            save = null;
            var matches = new List<World>();
            var listed = new List<string>();
            foreach (var world in SaveSystem.GetWorldList())
            {
                if (world == null) continue;
                listed.Add(DescribeWorld(world));
                if (string.Equals(world.m_name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(world.m_worldName, name, StringComparison.OrdinalIgnoreCase))
                    matches.Add(world);
            }

            if (matches.Count > 1)
            {
                var names = new List<string>();
                foreach (var world in matches) names.Add($"'{SaveNameOf(world)}'");
                problem = $"More than one world is called '{name}'; name the one to clone by its file name "
                    + $"({string.Join(" or ", names.ToArray())}).";
                return false;
            }

            if (matches.Count == 1)
            {
                string fileName = SaveNameOf(matches[0]);
                if (SaveSystem.TryGetSaveByName(fileName, SaveDataType.World, out save) && !save.IsDeleted) return true;
                bool deleted = save != null && save.IsDeleted;
                save = null;
                problem = $"'{name}' is in the world list as {DescribeWorld(matches[0])}, but its save could not be "
                    + $"opened under '{fileName}' ({(deleted ? "the save system reports it as deleted" : "no save by that name")}).";
                return false;
            }

            problem = $"There is no world called '{name}'. The world list holds {listed.Count}: "
                + string.Join(", ", listed.ToArray()) + ".";
            return false;
        }

        /// <summary>The name the save system files a world under: its save file name, or the name inside the world
        /// file when there is no separate save name (every pre-1.0 flat save).</summary>
        private static string SaveNameOf(World world)
            => string.IsNullOrEmpty(world.m_worldName) ? world.m_name : world.m_worldName;

        private static string DescribeWorld(World world)
            => string.Equals(world.m_name, world.m_worldName, StringComparison.Ordinal)
                ? $"'{world.m_name}'"
                : $"'{world.m_name}' (file '{world.m_worldName}')";

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
                foreach (string suffix in CarriedRecordSuffixes)
                {
                    string leftover = FiresWorldRecords.PathFor(cloneName, suffix);
                    if (!File.Exists(leftover)) continue;
                    problem = $"{Path.GetFileName(leftover)} is already there from an earlier world called '{cloneName}' and "
                        + "would be used instead of the copied land; delete it or choose another name.";
                    break;
                }
            return problem == null;
        }

        // The save's own files, out of whichever storage holds them, into local staging: a chunked save's _main.<n>.* and
        // chunk files keep their names, an old save's .db and .fwl take the clone's. The world file is then rewritten
        // with the clone's name and id.
        // `yield return` is legal inside a try that has only a FINALLY, which is why the mount can still be
        // released the moment anything throws.
        private static IEnumerator StageSaveAsync(SaveFile file, World source, string staging, string cloneName,
            long cloneUid, Action<string> progress, StageCounts counts)
        {
            string mainPrefix = MainFilePrefix + source.SaveNumber().ToString(CultureInfo.InvariantCulture) + ".";
            string meta = file.IsChunked ? mainPrefix + ChunkedMetaExtension.TrimStart('.') : cloneName + OldMetaExtension;
            var grouping = file.IsChunked ? CloudStorageFileGrouping.SameFolder : CloudStorageFileGrouping.SameFileEnding;
            bool local = !file.m_source.IsCloud();
            long total = local ? TotalBytes(file) : 0L;
            long copied = 0L;

            if (!FileHelpers.Mount(SaveDataAccess.ReadWrite))
                throw new IOException($"{file.m_source} save storage could not be opened");
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

                    if (!local)
                    {
                        // Cloud storage has no stream to chunk: one call, and it blocks for as long as it blocks.
                        if (progress != null) progress($"Copying {name} out of {file.m_source} storage...");
                        if (!FileHelpers.Copy(path, file.m_source, target, grouping, FileHelpers.FileSource.Local))
                            throw new IOException($"{name} could not be copied out of {file.m_source} storage");
                    }
                    else
                    {
                        var chunks = CopyChunked(path, target, advanced =>
                        {
                            copied += advanced;
                            if (progress == null) return;
                            progress(total > 0L
                                ? $"Copying '{source.m_name}': {copied / BytesPerMegabyte:0.#} of {total / BytesPerMegabyte:0.#} MB "
                                    + $"({copied * PercentScale / total:0}%)"
                                : $"Copying '{source.m_name}': {copied / BytesPerMegabyte:0.#} MB");
                        });
                        while (chunks.MoveNext()) yield return null;
                    }

                    counts.Files++;
                    counts.Bytes += new FileInfo(target).Length;
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

        private static long TotalBytes(SaveFile file)
        {
            long total = 0L;
            foreach (string path in file.AllPaths)
            {
                try { if (File.Exists(path)) total += new FileInfo(path).Length; }
                catch { }
            }
            return total;
        }

        /// <summary>A local file copied a chunk at a time, handing a frame back between chunks. `using` compiles to
        /// try/finally, which is the one form of try a `yield return` may sit inside.</summary>
        private static IEnumerator CopyChunked(string from, string to, Action<long> advanced)
        {
            var buffer = new byte[CopyChunkBytes];
            using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, CopyChunkBytes))
            using (var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, CopyChunkBytes))
            {
                while (true)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                    if (advanced != null) advanced(read);
                    yield return null;
                }
            }
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

        // Only the records the copy's own profile can use. A copy asked for stock heightmap terrain must NOT inherit
        // the source's bake: a bake IS voxel terrain, and the generator prefers it over the land in the save, so
        // carrying it turns a plain copy of a map into whatever voxel world the source happened to be. Cave, sky
        // island and seed-grid records go the same way - they belong to features the copy was not asked for.
        private static int CarryRecords(World source, string cloneName, FiresWorldProfile profile)
        {
            int carried = 0;
            foreach (string suffix in CarriedRecordSuffixes)
            {
                string from = FiresWorldRecords.PathFor(source.m_name, suffix);
                if (!File.Exists(from)) continue;
                if (!ProfileWants(profile, suffix))
                {
                    FiresLogger.LogInfo($"[FiresWorldGen] '{cloneName}' was not given {Path.GetFileName(from)}: the copy was "
                        + "asked for terrain that has no use for it.");
                    continue;
                }
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
            if (profile.Generator != ProfileGenerator.Vanilla)
                foreach (string from in SeedCaches(source.m_name, source.m_seed))
                {
                    File.Copy(from, Path.Combine(FiresWorldRecords.WorldFolder(cloneName), clonePrefix + Path.GetFileName(from).Substring(sourcePrefix.Length)), true);
                    carried++;
                }
            return carried;
        }

        /// <summary>Which of a world's records a copy with this profile has any use for. Null means the option was
        /// never stated, which is the console's old behaviour: carry it.</summary>
        private static bool ProfileWants(FiresWorldProfile profile, string suffix)
        {
            if (suffix == BakeSuffix || suffix == BakeFlowSuffix) return profile.Terrain != ProfileTerrain.Heightmap;
            if (suffix == CavesSuffix) return profile.Caves ?? true;
            if (suffix == IslandsSuffix || suffix == SkylandsSuffix) return profile.SkyIslands ?? true;
            return true;
        }

        // firesgen_<world>_<seed>_r<res>[_vahc].bin: the generator's seed grid, minutes of work to rebuild and the same for
        // every world with that seed, so a leftover one under the clone's name is simply overwritten.
        private static IEnumerable<string> SeedCaches(string worldName, int seed)
        {
            string folder = FiresWorldRecords.WorldFolder(worldName);
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

        // A world being copied already exists, so FromWorldRecords, never the plain config read: the copy's ground must be
        // the ground the original had.
        internal static FiresWorldProfile OriginalProfile(string sourceName)
            => FiresWorldProfile.Read(sourceName) ?? FiresWorldProfile.FromWorldRecords(sourceName);

        // The profile a clone of `sourceName` gets: the original's, with the options laid over it.
        internal static FiresWorldProfile ProfileFor(string sourceName, WorldCloneOptions options)
        {
            var profile = OriginalProfile(sourceName).Copy();
            profile.ZoneMode = options.ChunkByChunk;
            if (options.Generator.HasValue) profile.Generator = options.Generator.Value;
            if (options.Terrain.HasValue) profile.Terrain = options.Terrain.Value;
            if (options.SkyIslands.HasValue) profile.SkyIslands = options.SkyIslands;
            if (options.SkyIslandWater.HasValue) profile.SkyIslandWater = options.SkyIslandWater;
            if (options.Hydrology.HasValue) profile.Hydrology = options.Hydrology;
            if (options.Caves.HasValue) profile.Caves = options.Caves;
            profile.Source = FiresWorldProfile.CloneSourcePrefix + sourceName;
            return profile;
        }

        // ── Launch argument: -firesclone <world> <new name> [options…] ──────────────
        // The same clone as the console command, run once at the main menu, after which the game quits: the rig makes a
        // converted test world with one unattended client launch (2026-09-29, the voxel-wrapped BC world). The words after
        // the argument, up to the next one starting with '-', are the console command's own words.
        private const string LaunchArgument = "-firesclone";
        private const int LaunchSettleFrames = 30;
        private static bool s_launchRan;

        [HarmonyLib.HarmonyPatch(typeof(FejdStartup), "Start")]
        private static class FejdStartup_Start_LaunchClone
        {
            private static void Postfix(FejdStartup __instance)
            {
                if (s_launchRan || __instance == null) return;
                string[] words = LaunchWords();
                if (words == null) return;
                s_launchRan = true;
                __instance.StartCoroutine(RunLaunchClone(words));
            }
        }

        // The console command's word layout: [0] the command, [1] the world, [2] the new name, [3..] options. Null when the
        // argument is absent.
        private static string[] LaunchWords()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.FindIndex(args, a => string.Equals(a, LaunchArgument, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return null;
            var words = new List<string> { CommandName };
            for (int i = at + 1; i < args.Length && !args[i].StartsWith("-", StringComparison.Ordinal); i++) words.Add(args[i]);
            return words.ToArray();
        }

        private static IEnumerator RunLaunchClone(string[] words)
        {
            for (int i = 0; i < LaunchSettleFrames; i++) yield return null;
            string report;
            bool ok = false;
            if (words.Length < 3) report = $"{LaunchArgument} needs <world> <new name> [options]; got '{string.Join(" ", words, 1, words.Length - 1)}'.";
            else if (!TryParseOptions(words, out var options, out string problem)) report = problem;
            else
            {
                FiresLogger.LogInfo($"[FiresWorldGen] {LaunchArgument}: cloning '{words[1]}' to '{words[2]}' ({string.Join(" ", words, 3, words.Length - 3)}).");
                string result = null;
                var run = CloneAsync(words[1], words[2], options,
                    step => FiresLogger.LogInfo($"[FiresWorldGen] {LaunchArgument}: {step}"),
                    (done, text) => { ok = done; result = text; });
                while (run.MoveNext()) yield return run.Current;
                report = result ?? "the clone ended without a report.";
            }
            if (ok) FiresLogger.LogInfo($"[FiresWorldGen] {LaunchArgument} DONE: {report} Quitting.");
            else FiresLogger.LogError($"[FiresWorldGen] {LaunchArgument} FAILED: {report} Quitting.");
            UnityEngine.Application.Quit();
        }
    }
}
