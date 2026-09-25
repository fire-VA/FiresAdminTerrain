using System;
using System.Collections.Generic;
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
                reason = $"'{worldName}' has no BetterContinents data beside it, so there is nothing to convert."
                    + DescribeConvertibleSibling(worldName);
                return false;
            }
            return true;
        }

        /// <summary>
        /// The row that was right-clicked has no BC data. Valheim keeps the ORIGINAL world's own name in every
        /// backup's metadata and renders a row as "name (fileName)", so a backup can say which world it came
        /// from - and on 2026-09-24 that is exactly what went wrong: the two visible Craters rows were both
        /// backups, the refusal named the backup's file, and the world that did carry the data was never
        /// mentioned. Returns "" when there is nothing useful to add, so the message is unchanged in the
        /// ordinary case of a world that simply never had BetterContinents.
        /// </summary>
        private static string DescribeConvertibleSibling(string worldName)
        {
            try
            {
                List<World> worlds = SaveSystem.GetWorldList();
                if (worlds == null) return string.Empty;

                string internalName = null;
                foreach (World world in worlds)
                {
                    if (world != null && world.m_worldName == worldName) { internalName = world.m_name; break; }
                }

                World best = null;
                BcSaveLocation bestSource = default(BcSaveLocation);
                int bestRank = int.MaxValue;
                foreach (World world in worlds)
                {
                    if (world == null || string.IsNullOrEmpty(world.m_worldName)) continue;
                    if (world.m_worldName == worldName) continue;

                    int rank = RankAsSibling(world, worldName, internalName);
                    if (rank >= bestRank) continue;
                    if (!BcSaveLocator.TryLocate(world.m_worldName, out BcSaveLocation candidate)) continue;

                    best = world;
                    bestSource = candidate;
                    bestRank = rank;
                }

                if (best == null || bestRank == int.MaxValue) return string.Empty;

                string relationship = bestRank == SiblingRankIsTheNamedWorld
                    ? $"This row is a backup or a copy - its own metadata still calls it '{internalName}'"
                    : "This row is named as a variant of it";
                return $"\n\n'{best.m_worldName}' DOES carry it:\n{bestSource.Path}\n\n{relationship}, so converting "
                    + $"THIS one would give you a world that generates as vanilla. Right-click '{best.m_worldName}' instead.";
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BcImport] could not check whether another world carries '{worldName}'s "
                    + $"BetterContinents data: {ex.Message}");
                return string.Empty;
            }
        }

        private const int SiblingRankIsTheNamedWorld = 0;
        private const int SiblingRankSharesInternalName = 1;
        private const int SiblingRankNamePrefix = 2;

        /// <summary>Lower is a better answer. The world the backup's metadata names beats another file of the
        /// same world, which beats a plain file-name stem match.</summary>
        private static int RankAsSibling(World world, string worldName, string internalName)
        {
            if (!string.IsNullOrEmpty(internalName))
            {
                if (world.m_worldName == internalName) return SiblingRankIsTheNamedWorld;
                if (world.m_name == internalName) return SiblingRankSharesInternalName;
            }
            if (worldName.StartsWith(world.m_worldName, StringComparison.OrdinalIgnoreCase)) return SiblingRankNamePrefix;
            return int.MaxValue;
        }


        // A converted world is a 1.0 update of a map that already exists, not a new Fires world, so the world
        // dictates the terrain and this installation's config gets no vote. Without extras the clone is told, in
        // full, to be stock Valheim: vanilla generator, heightmap terrain, one whole world at a time, and none of
        // the voxel features. Saying it in full is the point - a field left null falls through to
        // FiresWorldProfile.Infer, and a source world with no profile of its own (every pre-1.0 BetterContinents
        // world) then gets whatever FiresVoxelConfig happens to say on the machine doing the conversion.
        // Spec: Docs/BETTERCONTINENTS_IMPORT.md
        public static WorldCloneOptions VanillaTerrain()
        {
            return new WorldCloneOptions
            {
                ChunkByChunk = false,
                Generator = ProfileGenerator.Vanilla,
                Terrain = ProfileTerrain.Heightmap,
                SkyIslands = false,
                Hydrology = false,
                Caves = false,
            };
        }

        /// <summary>Fills whatever a caller left unsaid with the vanilla answer, so no path through a conversion
        /// can reach FiresWorldProfile.Infer no matter which toggles the caller thought about.</summary>
        private static WorldCloneOptions WithVanillaDefaults(WorldCloneOptions extras)
        {
            if (extras == null) return VanillaTerrain();
            if (!extras.Generator.HasValue) extras.Generator = ProfileGenerator.Vanilla;
            if (!extras.Terrain.HasValue) extras.Terrain = ProfileTerrain.Heightmap;
            if (!extras.SkyIslands.HasValue) extras.SkyIslands = false;
            if (!extras.Hydrology.HasValue) extras.Hydrology = false;
            if (!extras.Caves.HasValue) extras.Caves = false;
            return extras;
        }

        /// <summary>What the clone was asked to be, for the report: the plain case says so plainly.</summary>
        private static string DescribeTerrain(WorldCloneOptions options)
        {
            bool plain = options.Terrain.Value == ProfileTerrain.Heightmap
                && options.Generator.Value == ProfileGenerator.Vanilla
                && !options.SkyIslands.Value && !options.Hydrology.Value && !options.Caves.Value
                && !options.ChunkByChunk;
            if (plain) return "Stock Valheim terrain, the same map you had.";

            var extraNames = new List<string>();
            if (options.Terrain.Value != ProfileTerrain.Heightmap) extraNames.Add("voxel terrain");
            if (options.Caves.Value) extraNames.Add("caves");
            if (options.SkyIslands.Value) extraNames.Add("sky islands");
            if (options.Hydrology.Value) extraNames.Add("flowing water");
            if (options.ChunkByChunk) extraNames.Add("one zone at a time");
            return "Asked for on top of the map: " + string.Join(", ", extraNames.ToArray()) + ".";
        }

        /// <summary>Clone, then convert the clone. Every failure leaves the source alone and says what happened.
        /// <paramref name="extras"/> is null for a plain 1.0 update: the same map on stock Valheim terrain.</summary>
        public static bool TryConvert(string sourceName, string cloneName, WorldCloneOptions extras, out string report)
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

            WorldCloneOptions options = WithVanillaDefaults(extras);
            string conflict = FiresWorldClone.ProfileFor(sourceName, options).Conflict();
            if (conflict != null)
            {
                report = $"Nothing was cloned: those terrain options do not go together. {conflict}";
                return false;
            }

            if (!FiresWorldClone.TryClone(sourceName, cloneName, options, out string cloneReport))
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
                + $"{DescribeTerrain(options)} "
                + "The original is untouched - open it any time and it is exactly what it was.";
            return true;
        }

        /// <summary>The clone gets FAT's record when it already has a world folder; a clone of a pre-1.0 world is
        /// still flat, so it gets its own copy of the BetterContinents file instead and takes ownership the first
        /// time Valheim converts the save. Either way the clone is self-contained.</summary>
        /// <summary>Hands a copy the BetterContinents terrain of the world it was copied from, for callers that
        /// clone WITHOUT converting. A plain clone used to carry only FAT's own records, and an unconverted source
        /// has none of those - its terrain lives in BetterContinents' file, which nothing reads once BC is gone -
        /// so the copy came out on vanilla heights while the original still looked right. Silent when the source
        /// has no BetterContinents data: that is simply a normal world being copied.</summary>
        internal static bool TryCarryTerrainToClone(string sourceName, string cloneName, out string report)
        {
            report = null;
            if (BcWorldData.Exists(cloneName, out BcSaveLocation _)) return true;
            if (!BcSaveLocator.TryLocate(sourceName, out BcSaveLocation source)) return true;

            if (!BcSaveReader.TryReadFile(source, out BcWorldSettings settings, out string readFailure))
            {
                report = $"'{sourceName}' carries BetterContinents data that could not be read ({readFailure}), so "
                    + $"'{cloneName}' will generate on vanilla heights.";
                return false;
            }
            if (!settings.EnabledForThisWorld) return true;

            if (!TryGiveCloneTheData(cloneName, source, settings, out string ownership))
            {
                report = $"'{cloneName}' did not get the BetterContinents terrain: {ownership}";
                return false;
            }
            report = ownership;
            return true;
        }

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
