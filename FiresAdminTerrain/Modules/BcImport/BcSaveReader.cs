using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    /// <summary>
    /// The setting keys of a BetterContinents save, in the order its format numbers them. A key is stored as its
    /// NUMBER, so this order is part of the file format (data, as a save carries it) and may only ever grow at the end.
    /// </summary>
    internal enum BcDataKey
    {
        GlobalScale, MountainsAmount, SeaLevelAdjustment, MaxRidgeHeight, RidgeScale,
        RidgeBlendSigmoidB, RidgeBlendSigmoidXOffset, HeightMap, HeightMapPath, HeightMapAmount,
        HeightMapBlend, HeightMapAdd, OceanChannelsEnabled, RiversEnabled, BiomeMap,
        BiomeMapPath, ForestScale, ForestAmountOffset, OverrideStartPosition, LocationMap,
        LocationMapPath, RoughMap, RoughMapPath, RoughMapBlend, UseRoughInvertedAsFlat,
        FlatMapBlend, FlatMap, FlatMapPath, ForestMap, ForestMapPath,
        ForestMapMultiply, ForestMapAdd, DisableMapEdgeDropoff, MountainsAllowedAtCenter,
        ForestFactorOverrideAllTrees, HeightMapOverrideAll, HeightMapMask, BaseHeightNoise,
        BiomePrecision, PaintMapLegacy, PaintMapPath, HeatMap, HeatMapPath,
        HeatMapScale, LavaMap, LavaMapPath, MossMap, MossMapPath,
        AshlandGapEnabled, DeepNorthGapEnabled, TerrainMapLegacy, TerrainMapPath,
        TerrainMapColorLegacy, TerrainMap, PaintMap, WorldSize,
        EdgeSize, SpawnMapPath, SpawnMap, HeightMapAlpha,
        FixWaterColor, VegetationMap, VegetationMapPath, SkipDefaultLocations,
    }

    /// <summary>
    /// Parses a BetterContinents settings file into FAT's own settings object. Implements a published external
    /// format; every rule is sourced in Docs/BETTERCONTINENTS_IMPORT.md. Refuses the whole world rather than
    /// half-loading it, exactly as BC does.
    /// </summary>
    internal static class BcSaveReader
    {
        private const int LegacyRedChannelMaxVersion = 4;
        private const int ColouredBiomeMapMinVersion = 8;
        private const int RoughAndFlatMinVersion = 5;
        private const int RiversAndBiomeMapMinVersion = 2;
        private const int LocationMapMinVersion = 3;
        private const int HeightmapOverrideMinVersion = 6;
        private const int NoiseStackMinVersion = 7;
        private const int BiomePrecisionMinVersion = 9;
        private const int PaintMapMinVersion = 10;
        private const int DiscardedLegacyTerrainColourBytes = 4;

        public static bool TryReadFile(BcSaveLocation location, out BcWorldSettings settings, out string failure)
        {
            settings = null;
            failure = null;
            byte[] package;
            try
            {
                package = ReadPackageBytes(location);
            }
            catch (Exception ex)
            {
                failure = $"{location.Describe()} could not be read ({ex.GetType().Name}: {ex.Message})";
                return false;
            }
            return TryReadPackage(package, out settings, out failure);
        }

        public static bool TryReadPackage(byte[] packageBytes, out BcWorldSettings settings, out string failure)
        {
            settings = null;
            failure = null;
            if (packageBytes == null || packageBytes.Length < sizeof(int))
            {
                failure = "the settings package is empty";
                return false;
            }

            var parsed = new BcWorldSettings();
            var package = new ZPackage(packageBytes);
            try
            {
                parsed.Version = package.ReadInt();
                if (parsed.Version == BcWorldSettings.DisabledVersion)
                {
                    parsed.EnabledForThisWorld = false;
                    settings = parsed;
                    return true;
                }
                if (parsed.Version < BcWorldSettings.FirstKeyedVersion)
                {
                    if (!ReadLegacy(package, parsed, out failure)) return false;
                }
                else if (!ReadKeyed(package, parsed, out failure)) return false;
            }
            catch (EndOfStreamException)
            {
                failure = $"the settings package ended early at byte {package.GetPos()} of {package.Size()}"
                    + $" (version {parsed.Version})";
                return false;
            }
            catch (Exception ex)
            {
                failure = $"the settings package could not be parsed ({ex.GetType().Name}: {ex.Message})";
                return false;
            }

            settings = parsed;
            return true;
        }

        private static byte[] ReadPackageBytes(BcSaveLocation location)
        {
            var reader = new FileReader(location.Path, location.Source);
            try
            {
                BinaryReader binary = reader.m_binary;
                return binary.ReadBytes(binary.ReadInt32());
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static bool ReadKeyed(ZPackage package, BcWorldSettings settings, out string failure)
        {
            failure = null;
            settings.EnabledForThisWorld = true;
            while (package.GetPos() < package.Size())
            {
                int ordinal = package.ReadInt();
                if (!Enum.IsDefined(typeof(BcDataKey), ordinal))
                {
                    failure = $"unknown feature {ordinal} in a version {settings.Version} settings file."
                        + " This world was made with a newer BetterContinents than FAT knows.";
                    return false;
                }
                ApplyKey(package, settings, (BcDataKey)ordinal);
            }
            return true;
        }

        private static void ApplyKey(ZPackage package, BcWorldSettings settings, BcDataKey key)
        {
            switch (key)
            {
                case BcDataKey.GlobalScale: settings.GlobalScale = package.ReadSingle(); return;
                case BcDataKey.MountainsAmount: settings.MountainsAmount = package.ReadSingle(); return;
                case BcDataKey.SeaLevelAdjustment: settings.SeaLevelAdjustment = package.ReadSingle(); return;
                case BcDataKey.MaxRidgeHeight: settings.MaxRidgeHeight = package.ReadSingle(); return;
                case BcDataKey.RidgeScale: settings.RidgeScale = package.ReadSingle(); return;
                case BcDataKey.RidgeBlendSigmoidB: settings.RidgeBlendSigmoidB = package.ReadSingle(); return;
                case BcDataKey.RidgeBlendSigmoidXOffset: settings.RidgeBlendSigmoidXOffset = package.ReadSingle(); return;

                case BcDataKey.HeightMap:
                    settings.HeightMap = new BcMapImage
                    {
                        Data = package.ReadByteArray(),
                        HasAlphaChannel = settings.HeightMapAlpha,
                    };
                    return;
                case BcDataKey.HeightMapAmount: settings.HeightmapAmount = package.ReadSingle(); return;
                case BcDataKey.HeightMapBlend: settings.HeightmapBlend = package.ReadSingle(); return;
                case BcDataKey.HeightMapAdd: settings.HeightmapAdd = package.ReadSingle(); return;
                case BcDataKey.HeightMapMask: settings.HeightmapMask = package.ReadSingle(); return;

                case BcDataKey.BiomeMap:
                    settings.BiomeIndexMap = ReadBiomeIndexMap(package.ReadByteArray());
                    return;
                case BcDataKey.RoughMap: settings.RoughMap = ReadImage(package); return;
                case BcDataKey.RoughMapBlend: settings.RoughmapBlend = package.ReadSingle(); return;
                case BcDataKey.FlatMap: settings.FlatMap = ReadImage(package); return;
                case BcDataKey.FlatMapBlend: settings.FlatmapBlend = package.ReadSingle(); return;
                case BcDataKey.ForestMap: settings.ForestMap = ReadImage(package); return;
                case BcDataKey.ForestMapMultiply: settings.ForestmapMultiply = package.ReadSingle(); return;
                case BcDataKey.ForestMapAdd: settings.ForestmapAdd = package.ReadSingle(); return;
                case BcDataKey.ForestScale: settings.ForestScale = package.ReadSingle(); return;
                case BcDataKey.ForestAmountOffset: settings.ForestAmountOffset = package.ReadSingle(); return;
                case BcDataKey.HeatMap: settings.HeatMap = ReadImage(package); return;
                case BcDataKey.HeatMapScale: settings.HeatMapScale = package.ReadSingle(); return;
                case BcDataKey.LavaMap: settings.LavaMap = ReadImage(package); return;
                case BcDataKey.MossMap: settings.MossMap = ReadImage(package); return;

                case BcDataKey.PaintMapLegacy: settings.PaintMap = ReadImage(package); return;
                case BcDataKey.TerrainMapLegacy: settings.TerrainMap = ReadImage(package); return;
                case BcDataKey.TerrainMapColorLegacy: package.ReadByteArray(DiscardedLegacyTerrainColourBytes); return;
                case BcDataKey.TerrainMap: settings.TerrainMap = ReadColouredImage(package); return;
                case BcDataKey.PaintMap: settings.PaintMap = ReadColouredImage(package); return;

                case BcDataKey.LocationMap: settings.LocationMap = ReadLocationMap(package); return;
                case BcDataKey.SpawnMap: settings.SpawnMap = ReadSpawnMap(package); return;
                case BcDataKey.VegetationMap: settings.VegetationMap = ReadSpawnMap(package); return;

                case BcDataKey.BaseHeightNoise: settings.BaseHeightNoise = ReadNoiseStack(package); return;
                case BcDataKey.BiomePrecision: settings.BiomePrecision = package.ReadInt(); return;
                case BcDataKey.WorldSize: settings.WorldSize = package.ReadSingle(); return;
                case BcDataKey.EdgeSize: settings.EdgeSize = package.ReadSingle(); return;

                case BcDataKey.OverrideStartPosition:
                    settings.OverrideStartPosition = true;
                    settings.StartPositionX = package.ReadSingle();
                    settings.StartPositionY = package.ReadSingle();
                    return;

                case BcDataKey.OceanChannelsEnabled: settings.OceanChannelsEnabled = false; return;
                case BcDataKey.RiversEnabled: settings.RiversEnabled = false; return;
                case BcDataKey.HeightMapOverrideAll: settings.HeightmapOverrideAll = false; return;
                case BcDataKey.FixWaterColor: settings.FixWaterColor = false; return;
                case BcDataKey.UseRoughInvertedAsFlat: settings.UseRoughInvertedAsFlat = true; return;
                case BcDataKey.DisableMapEdgeDropoff: settings.DisableMapEdgeDropoff = true; return;
                case BcDataKey.MountainsAllowedAtCenter: settings.MountainsAllowedAtCenter = true; return;
                case BcDataKey.ForestFactorOverrideAllTrees: settings.ForestFactorOverrideAllTrees = true; return;
                case BcDataKey.AshlandGapEnabled: settings.AshlandsGapEnabled = true; return;
                case BcDataKey.DeepNorthGapEnabled: settings.DeepNorthGapEnabled = true; return;
                case BcDataKey.HeightMapAlpha: settings.HeightMapAlpha = true; return;
                case BcDataKey.SkipDefaultLocations: settings.SkipDefaultLocations = true; return;

                case BcDataKey.HeightMapPath: AssignPath(package, settings.HeightMap); return;
                case BcDataKey.BiomeMapPath: AssignPath(package, settings.BiomeIndexMap); return;
                case BcDataKey.RoughMapPath: AssignPath(package, settings.RoughMap); return;
                case BcDataKey.FlatMapPath: AssignPath(package, settings.FlatMap); return;
                case BcDataKey.ForestMapPath: AssignPath(package, settings.ForestMap); return;
                case BcDataKey.HeatMapPath: AssignPath(package, settings.HeatMap); return;
                case BcDataKey.LavaMapPath: AssignPath(package, settings.LavaMap); return;
                case BcDataKey.MossMapPath: AssignPath(package, settings.MossMap); return;
                case BcDataKey.PaintMapPath: AssignPath(package, settings.PaintMap); return;
                case BcDataKey.TerrainMapPath: AssignPath(package, settings.TerrainMap); return;
                case BcDataKey.LocationMapPath: AssignPath(package, settings.LocationMap); return;
                case BcDataKey.SpawnMapPath: AssignPath(package, settings.SpawnMap); return;
                case BcDataKey.VegetationMapPath: AssignPath(package, settings.VegetationMap); return;
            }
        }

        private static bool ReadLegacy(ZPackage package, BcWorldSettings settings, out string failure)
        {
            failure = null;
            package.ReadLong();
            settings.EnabledForThisWorld = package.ReadBool();
            if (!settings.EnabledForThisWorld) return true;

            settings.GlobalScale = package.ReadSingle();
            settings.MountainsAmount = package.ReadSingle();
            settings.SeaLevelAdjustment = package.ReadSingle();
            settings.MaxRidgeHeight = package.ReadSingle();
            settings.RidgeScale = package.ReadSingle();
            settings.RidgeBlendSigmoidB = package.ReadSingle();
            settings.RidgeBlendSigmoidXOffset = package.ReadSingle();

            string heightPath = package.ReadString();
            if (!string.IsNullOrEmpty(heightPath))
            {
                settings.HeightMap = new BcMapImage
                {
                    FilePath = heightPath,
                    Data = package.ReadByteArray(),
                    Legacy8BitRed = settings.Version <= LegacyRedChannelMaxVersion,
                };
                settings.HeightmapAmount = package.ReadSingle();
                settings.HeightmapBlend = package.ReadSingle();
                settings.HeightmapAdd = package.ReadSingle();
            }
            settings.OceanChannelsEnabled = package.ReadBool();
            settings.ApplyLegacyBaseline();

            if (settings.Version >= RiversAndBiomeMapMinVersion)
            {
                settings.RiversEnabled = package.ReadBool();
                settings.LegacyBiomeImage = ReadLegacyBiomeImage(package, settings.Version);
                settings.ForestScale = package.ReadSingle();
                settings.ForestAmountOffset = package.ReadSingle();
                settings.OverrideStartPosition = package.ReadBool();
                settings.StartPositionX = package.ReadSingle();
                settings.StartPositionY = package.ReadSingle();
            }

            if (settings.Version >= LocationMapMinVersion)
                settings.LocationMap = ReadLegacyLocationMap(package, settings.Version);

            if (settings.Version >= RoughAndFlatMinVersion)
            {
                string roughPath = package.ReadString();
                if (!string.IsNullOrEmpty(roughPath))
                {
                    settings.RoughMap = new BcMapImage { FilePath = roughPath, Data = package.ReadByteArray() };
                    settings.RoughmapBlend = package.ReadSingle();
                }
                settings.UseRoughInvertedAsFlat = package.ReadBool();
                settings.FlatmapBlend = package.ReadSingle();
                if (!settings.UseRoughInvertedAsFlat)
                {
                    string flatPath = package.ReadString();
                    if (!string.IsNullOrEmpty(flatPath))
                        settings.FlatMap = new BcMapImage { FilePath = flatPath, Data = package.ReadByteArray() };
                }
                string forestPath = package.ReadString();
                if (!string.IsNullOrEmpty(forestPath))
                {
                    settings.ForestMap = new BcMapImage { FilePath = forestPath, Data = package.ReadByteArray() };
                    settings.ForestmapMultiply = package.ReadSingle();
                    settings.ForestmapAdd = package.ReadSingle();
                }
                settings.DisableMapEdgeDropoff = package.ReadBool();
                settings.MountainsAllowedAtCenter = package.ReadBool();
                settings.ForestFactorOverrideAllTrees = package.ReadBool();
            }

            if (settings.Version >= HeightmapOverrideMinVersion)
            {
                settings.HeightmapOverrideAll = package.ReadBool();
                settings.HeightmapMask = package.ReadSingle();
            }
            if (settings.Version >= NoiseStackMinVersion)
                settings.BaseHeightNoise = ReadNoiseStack(package);
            if (settings.Version >= BiomePrecisionMinVersion)
                settings.BiomePrecision = package.ReadInt();
            if (settings.Version >= PaintMapMinVersion)
            {
                string colours = package.ReadString();
                if (!string.IsNullOrEmpty(colours))
                    settings.PaintMap = new BcMapImage { ColorTable = colours, Data = package.ReadByteArray() };
            }
            return true;
        }

        private static BcMapImage ReadImage(ZPackage package) =>
            new BcMapImage { Data = package.ReadByteArray() };

        private static BcMapImage ReadColouredImage(ZPackage package)
        {
            string colours = package.ReadString();
            return new BcMapImage { ColorTable = colours, Data = package.ReadByteArray() };
        }

        private static BcMapImage ReadLegacyBiomeImage(ZPackage package, int version)
        {
            string path = package.ReadString();
            if (string.IsNullOrEmpty(path)) return null;
            var image = new BcMapImage { FilePath = path, Data = package.ReadByteArray() };
            if (version >= ColouredBiomeMapMinVersion) image.ColorTable = package.ReadString();
            return image;
        }

        private static BcBiomeIndexMap ReadBiomeIndexMap(byte[] indices) =>
            new BcBiomeIndexMap
            {
                Indices = indices,
                Size = (int)Math.Sqrt(indices.Length),
            };

        private static BcLocationMap ReadLegacyLocationMap(ZPackage package, int version)
        {
            string path = package.ReadString();
            if (string.IsNullOrEmpty(path)) return null;
            BcLocationMap map = ReadLocationMap(package);
            map.FilePath = path;
            if (version >= ColouredBiomeMapMinVersion) package.ReadString();
            return map;
        }

        private static BcLocationMap ReadLocationMap(ZPackage package)
        {
            var map = new BcLocationMap();
            int groups = package.ReadInt();
            for (int group = 0; group < groups; group++)
            {
                string name = package.ReadString();
                int spots = package.ReadInt();
                var positions = new List<Vector2>(spots);
                for (int spot = 0; spot < spots; spot++)
                    positions.Add(new Vector2(package.ReadSingle(), package.ReadSingle()));
                map.RemainingAreas[name] = positions;
            }
            return map;
        }

        private static BcSpawnMap ReadSpawnMap(ZPackage package)
        {
            var map = new BcSpawnMap();
            int entries = package.ReadInt();
            for (int entry = 0; entry < entries; entry++)
            {
                var colour = new Color32(package.ReadByte(), package.ReadByte(), package.ReadByte(), package.ReadByte());
                map.Palette.Add(new BcSpawnPaletteEntry { Color = colour, Data = package.ReadString() });
            }
            map.Indices = package.ReadByteArray();
            map.Size = (int)Math.Sqrt(map.Indices.Length);
            return map;
        }

        private static BcNoiseStack ReadNoiseStack(ZPackage package)
        {
            var stack = new BcNoiseStack();
            int layers = package.ReadInt();
            for (int layer = 0; layer < layers; layer++) stack.Layers.Add(ReadNoiseLayer(package));
            return stack;
        }

        private static BcNoiseLayer ReadNoiseLayer(ZPackage package)
        {
            var layer = new BcNoiseLayer { Version = package.ReadInt(), Noise = ReadNoiseSettings(package) };
            if (package.ReadBool()) layer.NoiseWarp = ReadNoiseSettings(package);
            if (package.ReadBool()) layer.Mask = ReadNoiseSettings(package);
            if (package.ReadBool()) layer.MaskWarp = ReadNoiseSettings(package);
            return layer;
        }

        private static BcNoiseSettings ReadNoiseSettings(ZPackage package) =>
            new BcNoiseSettings
            {
                Version = package.ReadInt(),
                NoiseType = package.ReadInt(),
                Frequency = package.ReadSingle(),
                Aspect = package.ReadSingle(),
                FractalType = package.ReadInt(),
                FractalOctaves = package.ReadInt(),
                FractalLacunarity = package.ReadSingle(),
                FractalGain = package.ReadSingle(),
                FractalWeightedStrength = package.ReadSingle(),
                FractalPingPongStrength = package.ReadSingle(),
                CellularDistanceFunction = package.ReadInt(),
                CellularReturnType = package.ReadInt(),
                CellularJitter = package.ReadSingle(),
                DomainWarpType = package.ReadInt(),
                DomainWarpAmp = package.ReadSingle(),
                Invert = package.ReadBool(),
                UseSmoothThreshold = package.ReadBool(),
                SmoothThresholdStart = package.ReadSingle(),
                SmoothThresholdEnd = package.ReadSingle(),
                UseThreshold = package.ReadBool(),
                Threshold = package.ReadSingle(),
                UseRange = package.ReadBool(),
                RangeStart = package.ReadSingle(),
                RangeEnd = package.ReadSingle(),
                UseOpacity = package.ReadBool(),
                Opacity = package.ReadSingle(),
                BlendMode = package.ReadInt(),
            };

        private static void AssignPath(ZPackage package, BcMapImage image)
        {
            string path = package.ReadString();
            if (image != null) image.FilePath = path;
        }

        private static void AssignPath(ZPackage package, BcBiomeIndexMap map)
        {
            string path = package.ReadString();
            if (map != null) map.FilePath = path;
        }

        private static void AssignPath(ZPackage package, BcLocationMap map)
        {
            string path = package.ReadString();
            if (map != null) map.FilePath = path;
        }

        private static void AssignPath(ZPackage package, BcSpawnMap map)
        {
            string path = package.ReadString();
            if (map != null) map.FilePath = path;
        }
    }
}
