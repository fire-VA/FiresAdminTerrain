using System;
using System.Diagnostics;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.BcImport
{
    // A world that came from BetterContinents. On load: read FAT's own copy if it exists, otherwise find BC's
    // file, convert it and write FAT's copy - automatically, once, with no command to run. After that FAT owns
    // the data and generates the terrain itself; BC's file is never read again and never deleted.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal static class BcMigratedWorld
    {
        // What the last world load found and how long it took, for worldgen_test (FatWorldgenTest).
        internal sealed class LoadFacts
        {
            internal string Source = "none";
            internal int FileFormat;
            internal bool RestoredInBands;
            internal int BandedMaps;
            internal int PngMaps;
            internal bool Ready;
            internal long ReadMs;
            internal long DecodeMs;
        }

        private static string _worldName;
        private static BcWorldGen _generator;
        private static bool _checked;
        private static BcSaveLocation? _ownData;
        private static LoadFacts _lastLoad = new LoadFacts();

        internal static LoadFacts LastLoad => _lastLoad;

        public static bool Active => _generator != null;
        public static BcWorldGen Generator => _generator;
        public static string WorldName => _worldName;

        /// <summary>Where this server's own migrated data lives, for BcJoinSync to hand to joining clients; null when
        /// this world is not a migrated BetterContinents world or this machine is a client.</summary>
        public static BcSaveLocation? ServerDataLocation => _generator != null ? _ownData : null;

        public static void Reset()
        {
            _worldName = null;
            _generator = null;
            _checked = false;
            _ownData = null;
            _lastLoad = new LoadFacts();
            s_usable.Clear();   // Expand World Data's biomes can change between worlds
        }

        /// <summary>Runs from FAT's one WorldGenerator.Initialize hook. Menu terrain has no world file, and a
        /// world BetterContinents itself is handling is left to BetterContinents.</summary>
        public static void OnWorldInitialize(World world)
        {
            Reset();
            if (world == null || world.m_menu) return;

            string worldName = world.m_worldName;
            if (string.IsNullOrEmpty(worldName)) worldName = world.m_name;
            if (string.IsNullOrEmpty(worldName)) return;
            _worldName = worldName;
            _checked = true;

            if (WorldGeneratorAccess.IsBetterContinentsLoaded)
            {
                FiresLogger.LogInfo($"[BcImport] '{worldName}': BetterContinents is installed, so FAT leaves its worlds alone.");
                return;
            }

            try
            {
                // Loading a world NEVER converts it. A world is only migrated by an explicit Convert, which clones
                // it first (BcConvert), so an original can never be changed by opening it.
                var clock = Stopwatch.StartNew();
                bool joinedClient = ZNet.instance != null && !ZNet.instance.IsServer();
                BcWorldSettings settings = joinedClient ? SettingsFromServer(worldName) : LoadOwnCopy(worldName);
                if (settings == null)
                {
                    if (!joinedClient) WarnIfUnconverted(worldName);
                    return;
                }
                if (!settings.EnabledForThisWorld) return;
                long readMs = clock.ElapsedMilliseconds;
                BcWorldData.CountImages(settings, out _lastLoad.BandedMaps, out _lastLoad.PngMaps);
                _lastLoad.ReadMs = readMs;

                if (!BcImageMaps.TryDecode(settings, out BcDecodedMaps maps, out string decodeFailure))
                {
                    FiresLogger.LogError($"[BcImport] '{worldName}': maps could not be decoded, so terrain is NOT being "
                        + $"reproduced: {decodeFailure}");
                    return;
                }
                long decodeMs = clock.ElapsedMilliseconds - readMs;
                _lastLoad.DecodeMs = decodeMs;

                _generator = new BcWorldGen(settings, maps, world.m_seed);
                _lastLoad.Ready = true;
                long buildMs = clock.ElapsedMilliseconds - readMs - decodeMs;
                BcMapReport.Log(worldName, settings, maps, _generator);
                long reportMs = clock.ElapsedMilliseconds - readMs - decodeMs - buildMs;
                FiresLogger.LogInfo($"[BcImport] '{worldName}' generates as a BetterContinents world: settings v{settings.Version}, "
                    + $"{BcWorldGen.VariantFor(settings)} base height, world {settings.WorldSize} + edge {settings.EdgeSize}, "
                    + $"seed {world.m_seed}. Ready in {clock.ElapsedMilliseconds} ms: read {readMs}, decode {decodeMs}, "
                    + $"generator {buildMs}, map report {reportMs}.");
            }
            catch (Exception ex)
            {
                _generator = null;
                FiresLogger.LogError($"[BcImport] '{worldName}': migration threw, so terrain is NOT being reproduced "
                    + $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>A joined client builds the world the server builds: from the data the server announced at join
        /// (BcJoinSync), vanilla when it said the world has none, and never from a local copy of a same-named world. Only
        /// a server too old to announce anything leaves the client to its own copy.</summary>
        private static BcWorldSettings SettingsFromServer(string worldName)
        {
            switch (BcJoinSync.Answer(out string cachedPath))
            {
                case BcJoinSync.ServerAnswer.NotBetterContinents:
                    return null;
                case BcJoinSync.ServerAnswer.Data:
                    var received = new BcSaveLocation
                    {
                        Path = cachedPath, Source = FileHelpers.FileSource.Local, Kind = BcSaveKind.ReceivedFromServer,
                    };
                    if (BcWorldData.TryRead(received, out BcWorldSettings settings, out _lastLoad.FileFormat, out string failure))
                    {
                        _lastLoad.Source = "the server's data (join sync)";
                        return settings;
                    }
                    FiresLogger.LogError($"[BcImport] '{worldName}': the server's BetterContinents data arrived but could not be read "
                        + $"({failure}), so this game draws vanilla terrain that will not match the server's.");
                    return null;
                case BcJoinSync.ServerAnswer.Failed:
                    FiresLogger.LogError($"[BcImport] '{worldName}': the server's BetterContinents data did not arrive intact, so this "
                        + "game draws vanilla terrain that will not match the server's. Rejoin to fetch it again.");
                    return null;
                default:
                    FiresLogger.LogWarning($"[BcImport] '{worldName}': the server announced no BetterContinents data (a FAT without "
                        + "join sync?), so this game reads its own copy if it has one.");
                    return LoadOwnCopy(worldName);
            }
        }

        private static BcWorldSettings LoadOwnCopy(string worldName)
        {
            if (!BcWorldData.Exists(worldName, out BcSaveLocation own)) return null;
            _ownData = own;
            if (BcWorldData.TryRead(own, out BcWorldSettings settings, out _lastLoad.FileFormat, out string failure))
            {
                _lastLoad.Source = $"its own file ({own.Kind})";
                _lastLoad.RestoredInBands = BcWorldData.StoreInBands(worldName, own, settings);
                return settings;
            }
            _ownData = null;

            FiresLogger.LogError($"[BcImport] '{worldName}': FAT's own migrated data is unreadable and is being ignored "
                + $"rather than guessed at: {failure}");
            return null;
        }
        /// <summary>A world that still has a BetterContinents file but no FAT record generates as VANILLA: its
        /// terrain will not match what it was built on until it is converted. Said once, loudly, rather than
        /// quietly producing the wrong ground.</summary>
        private static void WarnIfUnconverted(string worldName)
        {
            if (!BcSaveLocator.TryLocate(worldName, out BcSaveLocation source)) return;
            FiresLogger.LogWarning($"[BcImport] '{worldName}' carries BetterContinents data ({source.Describe()}) but has "
                + "NOT been converted, so it is generating as a vanilla world and its terrain will not match what its "
                + "builds sit on. Right-click the world in the world list and choose Convert - that clones it first and "
                + "never touches this original.");
        }

        // Counted without a lock: an estimate for PregenerationTiming's line, which only reads it around main-thread passes.
        internal static int BaseHeightSamples;

        public static bool TryGetBaseHeight(float wx, float wy, float offset0, float offset1, float minMountainDistance, out float height)
        {
            height = 0f;
            if (_generator == null) return false;
            BaseHeightSamples++;
            height = _generator.GetBaseHeight(wx, wy, offset0, offset1, minMountainDistance);
            return true;
        }

        public static bool TryGetBiome(float wx, float wy, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (_generator == null) return false;
            biome = _generator.MapBiomeAt(_generator.Settings.Normalize(wx), _generator.Settings.Normalize(wy));
            if (biome != Heightmap.Biome.None && !Usable(biome)) biome = Heightmap.Biome.None;
            return biome != Heightmap.Biome.None;
        }

        // BetterContinents' "usable" rule (ImageMapBiome.Usable, 0.9.3): a biome the map holds but nothing here defines now (an
        // Expand World Data biome with EWD not installed, or its yaml changed) reads as None, so the default generation decides
        // there and the map keeps the value for when it is defined again. A vanilla biome is always usable; another single bit
        // only while Heightmap.Biome lists it (Expand World Data adds its biomes to the enum's values). Said once per biome.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Heightmap.Biome, bool> s_usable =
            new System.Collections.Concurrent.ConcurrentDictionary<Heightmap.Biome, bool>();
        private const Heightmap.Biome VanillaBiomes = Heightmap.Biome.Meadows | Heightmap.Biome.Swamp | Heightmap.Biome.Mountain
            | Heightmap.Biome.BlackForest | Heightmap.Biome.Plains | Heightmap.Biome.AshLands | Heightmap.Biome.DeepNorth
            | Heightmap.Biome.Ocean | Heightmap.Biome.Mistlands;

        private static bool Usable(Heightmap.Biome biome) => s_usable.GetOrAdd(biome, b =>
        {
            bool usable = (VanillaBiomes & b) == b || Array.IndexOf(Enum.GetValues(typeof(Heightmap.Biome)), b) >= 0;
            if (!usable)
                FiresLogger.LogWarning($"[BcImport] '{_worldName}': the biome map holds biome {(int)b}, which nothing here defines "
                    + "(an Expand World Data biome without Expand World Data, or with a changed yaml); the default generation decides there.");
            return usable;
        });

        public static bool RiversDisabled => _generator != null && !_generator.Settings.RiversEnabled;

        public static bool TryApplyForest(Vector3 position, ref float forestFactor)
        {
            if (_generator == null) return false;
            BcWorldSettings settings = _generator.Settings;
            forestFactor = _generator.ForestFactorAt(settings.Normalize(position.x), settings.Normalize(position.z), forestFactor);
            return true;
        }

        public static string Describe() =>
            !_checked ? "no world initialised yet"
            : _generator == null ? $"'{_worldName}' is not a BetterContinents world"
            : $"'{_worldName}': {BcWorldGen.VariantFor(_generator.Settings)} base height from migrated BetterContinents data";
    }
}
