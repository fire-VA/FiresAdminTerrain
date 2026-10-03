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

            // BETTERCONTINENTS IS AN EXTRA TO CARRY, NOT A PRECONDITION.
            // This used to open with CanConvert, which refuses any world without a BetterContinents
            // file beside it - so a plain world, the common case, could not be cloned or updated at
            // all. The clone, the 1.0 save format and the terrain options are the same work either
            // way; BC data is one more thing to hand the copy WHEN the source has some.
            // Re-converting a world that already carries the data is still refused: that is a no-op.
            if (BcWorldData.Exists(sourceName, out BcSaveLocation already))
            {
                report = $"'{sourceName}' is already converted ({already.Path}).";
                return false;
            }
            if (!FiresWorldClone.IsUsableName(cloneName, out string nameProblem))
            {
                report = nameProblem;
                return false;
            }

            var timer = Stopwatch.StartNew();

            // A source with no BC file, or with BC switched off for it, converts as a normal world:
            // hasBcTerrain stays false and the hand-over below is skipped. Neither is an error now.
            bool hasBcTerrain = BcSaveLocator.TryLocate(sourceName, out BcSaveLocation source);
            BcWorldSettings settings = default(BcWorldSettings);
            if (hasBcTerrain)
            {
                if (!BcSaveReader.TryReadFile(source, out settings, out string readFailure))
                {
                    // Unreadable BC data IS an error: the source visibly has terrain we would drop
                    // silently, and the copy would come out on vanilla heights looking nothing like it.
                    report = $"{source.Describe()} was refused, so nothing was cloned: {readFailure}";
                    return false;
                }
                if (!settings.EnabledForThisWorld) hasBcTerrain = false;
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

            string ownership = null;
            if (hasBcTerrain && BcWorldData.Exists(cloneName, out BcSaveLocation carried))
                ownership = $"'{cloneName}' carries FAT's own terrain data ({carried.Path}).";
            else if (hasBcTerrain && !TryGiveCloneTheData(cloneName, settings, out ownership))
            {
                report = $"Cloned '{sourceName}' as '{cloneName}', but {ownership} The original is untouched.";
                return false;
            }

            report = $"Converted '{sourceName}' to '{cloneName}' in {timer.ElapsedMilliseconds} ms. "
                + (hasBcTerrain ? ownership + " " : string.Empty)
                + $"{DescribeTerrain(options)} "
                + "The original is untouched - open it any time and it is exactly what it was.";
            return true;
        }

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

            if (!TryGiveCloneTheData(cloneName, settings, out string ownership))
            {
                report = $"'{cloneName}' did not get the BetterContinents terrain: {ownership}";
                return false;
            }
            report = ownership;
            return true;
        }

        // FAT's own record is the ONLY thing a load reads: no load ever converts, so there is no fallback. Copying the
        // BetterContinents file beside the clone used to be one, logged as "takes full ownership the first time it
        // loads" - nothing did, and the clone generated as vanilla (2026-09-29 04:21, CratersBCWrap on the dedi).
        private static bool TryGiveCloneTheData(string cloneName, BcWorldSettings settings, out string ownership)
        {
            if (BcWorldData.TryWrite(cloneName, FileHelpers.FileSource.Local, settings, out string writeFailure))
            {
                ownership = $"'{cloneName}' now carries FAT's own terrain data.";
                return true;
            }
            ownership = $"FAT's own terrain data could not be written ({writeFailure}), so it would generate as VANILLA "
                + "and must not be played.";
            return false;
        }
    }
}
