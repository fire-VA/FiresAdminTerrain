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
        private static string _worldName;
        private static BcWorldGen _generator;
        private static bool _checked;

        public static bool Active => _generator != null;
        public static BcWorldGen Generator => _generator;
        public static string WorldName => _worldName;

        public static void Reset()
        {
            _worldName = null;
            _generator = null;
            _checked = false;
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
                BcWorldSettings settings = LoadOwnCopy(worldName);
                if (settings == null)
                {
                    WarnIfUnconverted(worldName);
                    return;
                }
                if (!settings.EnabledForThisWorld) return;

                if (!BcImageMaps.TryDecode(settings, out BcDecodedMaps maps, out string decodeFailure))
                {
                    FiresLogger.LogError($"[BcImport] '{worldName}': maps could not be decoded, so terrain is NOT being "
                        + $"reproduced: {decodeFailure}");
                    return;
                }

                _generator = new BcWorldGen(settings, maps, world.m_seed);
                FiresLogger.LogInfo($"[BcImport] '{worldName}' generates as a BetterContinents world: settings v{settings.Version}, "
                    + $"{BcWorldGen.VariantFor(settings)} base height, world {settings.WorldSize} + edge {settings.EdgeSize}, "
                    + $"seed {world.m_seed}.");
            }
            catch (Exception ex)
            {
                _generator = null;
                FiresLogger.LogError($"[BcImport] '{worldName}': migration threw, so terrain is NOT being reproduced "
                    + $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static BcWorldSettings LoadOwnCopy(string worldName)
        {
            if (!BcWorldData.Exists(worldName, out BcSaveLocation own)) return null;
            if (BcWorldData.TryRead(own, out BcWorldSettings settings, out string failure)) return settings;

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

        public static bool TryGetBaseHeight(float wx, float wy, float offset0, float offset1, float minMountainDistance, out float height)
        {
            height = 0f;
            if (_generator == null) return false;
            height = _generator.GetBaseHeight(wx, wy, offset0, offset1, minMountainDistance);
            return true;
        }

        public static bool TryGetBiome(float wx, float wy, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (_generator == null) return false;
            biome = _generator.GetBiomeOverride(_generator.Settings.Normalize(wx), _generator.Settings.Normalize(wy));
            return biome != Heightmap.Biome.None;
        }

        public static bool RiversDisabled => _generator != null && !_generator.Settings.RiversEnabled;

        public static bool TryApplyForest(Vector3 position, ref float forestFactor)
        {
            if (_generator == null) return false;
            BcWorldSettings settings = _generator.Settings;
            forestFactor = _generator.ApplyForest(settings.Normalize(position.x), settings.Normalize(position.z), forestFactor);
            return true;
        }

        public static string Describe() =>
            !_checked ? "no world initialised yet"
            : _generator == null ? $"'{_worldName}' is not a BetterContinents world"
            : $"'{_worldName}': {BcWorldGen.VariantFor(_generator.Settings)} base height from migrated BetterContinents data";
    }
}
