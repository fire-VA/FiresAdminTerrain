using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    // FAT's own copy of a migrated BetterContinents world, written into the world folder as an EXTENSION-LESS
    // file. Valheim 1.0's save plumbing carries only .fwl2/.db2/.chunks/.ok/.chunk and deletes anything else on
    // rename, so an extension-less name is what the save scanner ignores rather than destroys - the same trick
    // the game's own cacheMinimap files use.
    //
    // Once this file exists FAT owns the data: BC's file is never read again, never deleted, and never needed.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal static class BcWorldData
    {
        public const string FileName = "FiresAdminTerrain";

        // Format 2 stores the map images in bands (BcBandedImage) so they decode side by side; format 1 held the PNGs as
        // they came. Both read, and a format 1 file is re-stored in bands once, on its first load (StoreInBands).
        private const int Magic = 0x57434246;
        private const int FormatVersion = 2;
        private const int FirstFormatVersion = 1;
        private const string ReplacementSuffix = "_writing";
        private const double BytesPerMegabyte = 1024d * 1024d;

        private enum MapSlot
        {
            Height = 0,
            Rough = 1,
            Flat = 2,
            Forest = 3,
            Heat = 4,
            Lava = 5,
            Moss = 6,
            Terrain = 7,
            Paint = 8,
            LegacyBiomeImage = 9,
        }

        /// <summary>The suffix FAT's own records folder uses for a world with no world folder of its own.</summary>
        public const string RecordsFolderSuffix = ".bcdata";

        public static string PathFor(string worldName, FileHelpers.FileSource source) =>
            $"{SaveSystem.GetWorldsSaveRootPath(source)}/{worldName}/{FileName}";

        /// <summary>FiresWorldGen\firesgen_&lt;world&gt;.bcdata - where the data goes when the world is still a flat
        /// pre-1.0 save and has no folder to put it in. FAT's own records folder always exists, is never touched by
        /// Valheim's save plumbing, and needs no save-format conversion to appear.</summary>
        public static string RecordsFolderPathFor(string worldName) =>
            Voxel.FiresWorldRecords.PathFor(worldName, RecordsFolderSuffix);

        public static bool Exists(string worldName, out BcSaveLocation location)
        {
            foreach (FileHelpers.FileSource source in BcSaveLocator.SearchedSources)
            {
                string path = PathFor(worldName, source);
                if (!FileHelpers.Exists(path, source)) continue;
                location = new BcSaveLocation { Path = path, Source = source, Kind = BcSaveKind.InsideWorldFolder };
                return true;
            }

            string records = RecordsFolderPathFor(worldName);
            if (File.Exists(records))
            {
                location = new BcSaveLocation
                {
                    Path = records, Source = FileHelpers.FileSource.Local, Kind = BcSaveKind.FatRecordsFolder,
                };
                return true;
            }

            location = default(BcSaveLocation);
            return false;
        }

        /// <summary>Writes FAT's copy beside the world it belongs to. Into the world folder when there is one -
        /// never branching on World.IsChunkedSave(), which reports how the world was READ and orphaned a real
        /// world's maps in the BetterContinents 1.0 port - and into FAT's own records folder when there is not.
        ///
        /// A pre-1.0 world is still flat files beside the root and has no folder. Creating one here would leave a
        /// directory with no .fwl2 in it, which SaveSystem reads as a world with a missing meta file. This used to
        /// give up at that point and wait for Valheim to convert the save, on the theory that BetterContinents'
        /// own file would carry the terrain until then. It does not: the whole point of converting is that
        /// BetterContinents is NOT installed, and nothing else ever reads that file. A converted world with no
        /// record generated as plain vanilla and FAT warned that the world it had just converted was
        /// unconverted.</summary>
        public static bool TryWrite(string worldName, FileHelpers.FileSource source, BcWorldSettings settings, out string failure)
        {
            failure = null;
            try
            {
                StoreImagesInBands(worldName, settings);
                byte[] bytes = Serialise(settings);

                string folder = $"{SaveSystem.GetWorldsSaveRootPath(source)}/{worldName}";
                if (source.IsLocal() && !Directory.Exists(folder))
                {
                    string records = RecordsFolderPathFor(worldName);
                    Directory.CreateDirectory(Path.GetDirectoryName(records));
                    using (var stream = new FileStream(records, FileMode.Create, FileAccess.Write))
                    using (var binary = new BinaryWriter(stream))
                    {
                        binary.Write(bytes.Length);
                        binary.Write(bytes);
                    }
                    FiresLogger.LogInfo($"[BcImport] '{worldName}' is a flat pre-1.0 save with no world folder, so its "
                        + $"{bytes.Length} byte(s) of migrated BetterContinents data went to {records} instead. FAT owns "
                        + "the data from now on either way.");
                    return true;
                }

                string path = PathFor(worldName, source);
                var writer = new FileWriter(path, Splatform.CloudStorageFileGrouping.Individual, fileSource: source);
                writer.m_binary.Write(bytes.Length);
                writer.m_binary.Write(bytes);
                writer.Finish();

                FiresLogger.LogInfo($"[BcImport] wrote {bytes.Length} byte(s) of migrated BetterContinents data to {path}");
                return true;
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>The package reads straight from the file: a whole-file byte[] and the ZPackage's own copy of it
        /// were two needless copies of 150+ MB on every load and every join.</summary>
        public static bool TryRead(BcSaveLocation location, out BcWorldSettings settings, out string failure) =>
            TryRead(location, out settings, out int _, out failure);

        /// <summary>As TryRead, also saying which format the file was in (worldgen_test reports it).</summary>
        public static bool TryRead(BcSaveLocation location, out BcWorldSettings settings, out int format, out string failure)
        {
            settings = null;
            format = 0;
            failure = null;
            try
            {
                var reader = new FileReader(location.Path, location.Source);
                try
                {
                    BinaryReader binary = reader.m_binary;
                    binary.ReadInt32();
                    var package = new ZPackage();
                    package.SetReader(binary);
                    if (package.ReadInt() != Magic)
                    {
                        failure = $"{location.Path} is not a FiresAdminTerrain world file";
                        return false;
                    }
                    format = package.ReadInt();
                    if (format < FirstFormatVersion || format > FormatVersion)
                    {
                        failure = $"{location.Path} is format {format}, this build reads {FirstFormatVersion} to {FormatVersion}";
                        return false;
                    }
                    settings = Read(package);
                    return true;
                }
                finally
                {
                    reader.Dispose();
                }
            }
            catch (Exception ex)
            {
                failure = $"{location.Path} could not be read ({ex.GetType().Name}: {ex.Message})";
                return false;
            }
        }

        /// <summary>A format 1 file's maps are PNGs, which decode one row after another: 3.5 s at every load and join for
        /// an 8192² map. The first load of a world's OWN file re-stores them in bands and writes the file back in place,
        /// once. Each map is read back and matched against its PNG byte for byte before it is used, and the new file is
        /// swapped in whole, so the old one stands until the new one is complete. A client's copy of a server's data is
        /// the server's content by hash and is never rewritten, and storage other than local disk is left alone. True when
        /// the file was rewritten.</summary>
        public static bool StoreInBands(string worldName, BcSaveLocation location, BcWorldSettings settings)
        {
            bool ownLocalFile = location.Source == FileHelpers.FileSource.Local
                && (location.Kind == BcSaveKind.InsideWorldFolder || location.Kind == BcSaveKind.FatRecordsFolder);
            if (!ownLocalFile || !StoreImagesInBands(worldName, settings)) return false;
            try
            {
                FiresLogger.LogInfo($"[BcImport] '{worldName}': writing the banded maps to {location.Path}{ReplacementSuffix}, then "
                    + $"swapping it in for {location.Path}. Until the next line says it is done, the old file stands.");
                ReplaceLocalFile(location.Path, Serialise(settings));
                FiresLogger.LogInfo($"[BcImport] '{worldName}': {location.Path} now holds its maps in bands (format {FormatVersion}).");
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BcImport] '{worldName}': the banded maps could not be written back to {location.Path} "
                    + $"({ex.GetType().Name}: {ex.Message}). The old file stands, this load decodes the bands from memory, and "
                    + "the next load tries again.");
                return false;
            }
        }

        /// <summary>How many of the settings' map images are stored in bands and how many are still PNGs.</summary>
        internal static void CountImages(BcWorldSettings settings, out int banded, out int png)
        {
            banded = 0;
            png = 0;
            foreach (KeyValuePair<MapSlot, BcMapImage> entry in PresentImages(settings))
            {
                if (BcBandedImage.IsBanded(entry.Value.Data)) banded++;
                else png++;
            }
        }

        // Every PNG map is re-stored in bands; one whose bands do not read back as its PNG keeps the PNG. True when any changed.
        private static bool StoreImagesInBands(string worldName, BcWorldSettings settings)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int banded = 0;
            long pngBytes = 0L, bandedBytes = 0L;
            foreach (KeyValuePair<MapSlot, BcMapImage> entry in PresentImages(settings))
            {
                BcMapImage image = entry.Value;
                if (BcBandedImage.IsBanded(image.Data)) continue;
                byte[] bands;
                string failure;
                try { bands = BcBandedImage.FromPng(image.Data, out failure); }
                catch (Exception ex)
                {
                    bands = null;
                    failure = $"{ex.GetType().Name}: {ex.Message}";
                }
                if (bands == null)
                {
                    FiresLogger.LogError($"[BcImport] '{worldName}': the {entry.Key} map was NOT re-stored in bands ({failure}); "
                        + "it stays a PNG and decodes the slow way.");
                    continue;
                }
                pngBytes += image.Data.Length;
                bandedBytes += bands.Length;
                image.Data = bands;
                banded++;
            }
            if (banded == 0) return false;
            FiresLogger.LogInfo($"[BcImport] '{worldName}': {banded} map(s) re-stored in bands, once ({pngBytes / BytesPerMegabyte:0.#} MB "
                + $"as PNG, {bandedBytes / BytesPerMegabyte:0.#} MB in bands), each read back and matched against its PNG byte for "
                + $"byte, in {clock.ElapsedMilliseconds} ms. From now on they decode side by side.");
            return true;
        }

        private static byte[] Serialise(BcWorldSettings settings)
        {
            var package = new ZPackage();
            Write(package, settings);
            return package.GetArray();
        }

        // Written beside the old file under an extension-less name (which Valheim's save plumbing leaves alone), then
        // swapped in whole, so a crash mid-write leaves the old file as it was.
        private static void ReplaceLocalFile(string path, byte[] bytes)
        {
            string replacement = path + ReplacementSuffix;
            using (var stream = new FileStream(replacement, FileMode.Create, FileAccess.Write))
            using (var binary = new BinaryWriter(stream))
            {
                binary.Write(bytes.Length);
                binary.Write(bytes);
            }
            File.Replace(replacement, path, null);
        }

        private static void Write(ZPackage package, BcWorldSettings settings)
        {
            package.Write(Magic);
            package.Write(FormatVersion);

            package.Write(settings.Version);
            package.Write(settings.EnabledForThisWorld);

            package.Write(settings.GlobalScale);
            package.Write(settings.MountainsAmount);
            package.Write(settings.SeaLevelAdjustment);
            package.Write(settings.MaxRidgeHeight);
            package.Write(settings.RidgeScale);
            package.Write(settings.RidgeBlendSigmoidB);
            package.Write(settings.RidgeBlendSigmoidXOffset);

            package.Write(settings.HeightmapAmount);
            package.Write(settings.HeightmapBlend);
            package.Write(settings.HeightmapAdd);
            package.Write(settings.HeightmapMask);
            package.Write(settings.HeightmapOverrideAll);
            package.Write(settings.HeightMapAlpha);

            package.Write(settings.OceanChannelsEnabled);
            package.Write(settings.RiversEnabled);
            package.Write(settings.AshlandsGapEnabled);
            package.Write(settings.DeepNorthGapEnabled);

            package.Write(settings.ForestScale);
            package.Write(settings.ForestAmountOffset);
            package.Write(settings.ForestmapMultiply);
            package.Write(settings.ForestmapAdd);
            package.Write(settings.ForestFactorOverrideAllTrees);

            package.Write(settings.RoughmapBlend);
            package.Write(settings.FlatmapBlend);
            package.Write(settings.UseRoughInvertedAsFlat);

            package.Write(settings.BiomePrecision);
            package.Write(settings.OverrideStartPosition);
            package.Write(settings.StartPositionX);
            package.Write(settings.StartPositionY);
            package.Write(settings.SkipDefaultLocations);

            package.Write(settings.DisableMapEdgeDropoff);
            package.Write(settings.MountainsAllowedAtCenter);

            package.Write(settings.HeatMapScale);
            package.Write(settings.WorldSize);
            package.Write(settings.EdgeSize);
            package.Write(settings.FixWaterColor);

            WriteNoiseStack(package, settings.BaseHeightNoise);
            WriteImages(package, settings);
            WriteBiomeIndices(package, settings.BiomeIndexMap);
            WriteLocations(package, settings.LocationMap);
            WriteSpawnMap(package, settings.SpawnMap);
            WriteSpawnMap(package, settings.VegetationMap);
        }

        private static BcWorldSettings Read(ZPackage package)
        {
            var settings = new BcWorldSettings
            {
                Version = package.ReadInt(),
                EnabledForThisWorld = package.ReadBool(),

                GlobalScale = package.ReadSingle(),
                MountainsAmount = package.ReadSingle(),
                SeaLevelAdjustment = package.ReadSingle(),
                MaxRidgeHeight = package.ReadSingle(),
                RidgeScale = package.ReadSingle(),
                RidgeBlendSigmoidB = package.ReadSingle(),
                RidgeBlendSigmoidXOffset = package.ReadSingle(),

                HeightmapAmount = package.ReadSingle(),
                HeightmapBlend = package.ReadSingle(),
                HeightmapAdd = package.ReadSingle(),
                HeightmapMask = package.ReadSingle(),
                HeightmapOverrideAll = package.ReadBool(),
                HeightMapAlpha = package.ReadBool(),

                OceanChannelsEnabled = package.ReadBool(),
                RiversEnabled = package.ReadBool(),
                AshlandsGapEnabled = package.ReadBool(),
                DeepNorthGapEnabled = package.ReadBool(),

                ForestScale = package.ReadSingle(),
                ForestAmountOffset = package.ReadSingle(),
                ForestmapMultiply = package.ReadSingle(),
                ForestmapAdd = package.ReadSingle(),
                ForestFactorOverrideAllTrees = package.ReadBool(),

                RoughmapBlend = package.ReadSingle(),
                FlatmapBlend = package.ReadSingle(),
                UseRoughInvertedAsFlat = package.ReadBool(),

                BiomePrecision = package.ReadInt(),
                OverrideStartPosition = package.ReadBool(),
                StartPositionX = package.ReadSingle(),
                StartPositionY = package.ReadSingle(),
                SkipDefaultLocations = package.ReadBool(),

                DisableMapEdgeDropoff = package.ReadBool(),
                MountainsAllowedAtCenter = package.ReadBool(),

                HeatMapScale = package.ReadSingle(),
                WorldSize = package.ReadSingle(),
                EdgeSize = package.ReadSingle(),
                FixWaterColor = package.ReadBool(),
            };

            settings.BaseHeightNoise = ReadNoiseStack(package);
            ReadImages(package, settings);
            settings.BiomeIndexMap = ReadBiomeIndices(package);
            settings.LocationMap = ReadLocations(package);
            settings.SpawnMap = ReadSpawnMap(package);
            settings.VegetationMap = ReadSpawnMap(package);
            return settings;
        }

        private static List<KeyValuePair<MapSlot, BcMapImage>> PresentImages(BcWorldSettings settings)
        {
            var present = new List<KeyValuePair<MapSlot, BcMapImage>>();
            AddIfPresent(present, MapSlot.Height, settings.HeightMap);
            AddIfPresent(present, MapSlot.Rough, settings.RoughMap);
            AddIfPresent(present, MapSlot.Flat, settings.FlatMap);
            AddIfPresent(present, MapSlot.Forest, settings.ForestMap);
            AddIfPresent(present, MapSlot.Heat, settings.HeatMap);
            AddIfPresent(present, MapSlot.Lava, settings.LavaMap);
            AddIfPresent(present, MapSlot.Moss, settings.MossMap);
            AddIfPresent(present, MapSlot.Terrain, settings.TerrainMap);
            AddIfPresent(present, MapSlot.Paint, settings.PaintMap);
            AddIfPresent(present, MapSlot.LegacyBiomeImage, settings.LegacyBiomeImage);
            return present;
        }

        private static void WriteImages(ZPackage package, BcWorldSettings settings)
        {
            List<KeyValuePair<MapSlot, BcMapImage>> present = PresentImages(settings);
            package.Write(present.Count);
            foreach (KeyValuePair<MapSlot, BcMapImage> entry in present)
            {
                package.Write((int)entry.Key);
                package.Write(entry.Value.FilePath ?? string.Empty);
                package.Write(entry.Value.ColorTable ?? string.Empty);
                package.Write(entry.Value.Legacy8BitRed);
                package.Write(entry.Value.HasAlphaChannel);
                package.Write(entry.Value.Data);
            }
        }

        private static void ReadImages(ZPackage package, BcWorldSettings settings)
        {
            int count = package.ReadInt();
            for (int at = 0; at < count; at++)
            {
                var slot = (MapSlot)package.ReadInt();
                var image = new BcMapImage
                {
                    FilePath = package.ReadString(),
                    ColorTable = package.ReadString(),
                    Legacy8BitRed = package.ReadBool(),
                    HasAlphaChannel = package.ReadBool(),
                    Data = package.ReadByteArray(),
                };
                switch (slot)
                {
                    case MapSlot.Height: settings.HeightMap = image; break;
                    case MapSlot.Rough: settings.RoughMap = image; break;
                    case MapSlot.Flat: settings.FlatMap = image; break;
                    case MapSlot.Forest: settings.ForestMap = image; break;
                    case MapSlot.Heat: settings.HeatMap = image; break;
                    case MapSlot.Lava: settings.LavaMap = image; break;
                    case MapSlot.Moss: settings.MossMap = image; break;
                    case MapSlot.Terrain: settings.TerrainMap = image; break;
                    case MapSlot.Paint: settings.PaintMap = image; break;
                    case MapSlot.LegacyBiomeImage: settings.LegacyBiomeImage = image; break;
                }
            }
        }

        private static void AddIfPresent(List<KeyValuePair<MapSlot, BcMapImage>> into, MapSlot slot, BcMapImage image)
        {
            if (image?.Data == null || image.Data.Length == 0) return;
            into.Add(new KeyValuePair<MapSlot, BcMapImage>(slot, image));
        }

        private static void WriteBiomeIndices(ZPackage package, BcBiomeIndexMap map)
        {
            package.Write(map?.Indices != null && map.Indices.Length > 0);
            if (map?.Indices == null || map.Indices.Length == 0) return;
            package.Write(map.FilePath ?? string.Empty);
            package.Write(map.Size);
            package.Write(map.Indices);
        }

        private static BcBiomeIndexMap ReadBiomeIndices(ZPackage package)
        {
            if (!package.ReadBool()) return null;
            return new BcBiomeIndexMap
            {
                FilePath = package.ReadString(),
                Size = package.ReadInt(),
                Indices = package.ReadByteArray(),
            };
        }

        private static void WriteLocations(ZPackage package, BcLocationMap map)
        {
            package.Write(map != null);
            if (map == null) return;
            package.Write(map.FilePath ?? string.Empty);
            package.Write(map.RemainingAreas.Count);
            foreach (KeyValuePair<string, List<Vector2>> group in map.RemainingAreas)
            {
                package.Write(group.Key);
                package.Write(group.Value.Count);
                foreach (Vector2 spot in group.Value)
                {
                    package.Write(spot.x);
                    package.Write(spot.y);
                }
            }
        }

        private static BcLocationMap ReadLocations(ZPackage package)
        {
            if (!package.ReadBool()) return null;
            var map = new BcLocationMap { FilePath = package.ReadString() };
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

        private static void WriteSpawnMap(ZPackage package, BcSpawnMap map)
        {
            package.Write(map?.Indices != null && map.Indices.Length > 0);
            if (map?.Indices == null || map.Indices.Length == 0) return;
            package.Write(map.FilePath ?? string.Empty);
            package.Write(map.Size);
            package.Write(map.Palette.Count);
            foreach (BcSpawnPaletteEntry entry in map.Palette)
            {
                package.Write(entry.Color.r);
                package.Write(entry.Color.g);
                package.Write(entry.Color.b);
                package.Write(entry.Color.a);
                package.Write(entry.Data ?? string.Empty);
            }
            package.Write(map.Indices);
        }

        private static BcSpawnMap ReadSpawnMap(ZPackage package)
        {
            if (!package.ReadBool()) return null;
            var map = new BcSpawnMap { FilePath = package.ReadString(), Size = package.ReadInt() };
            int entries = package.ReadInt();
            for (int at = 0; at < entries; at++)
            {
                var colour = new Color32(package.ReadByte(), package.ReadByte(), package.ReadByte(), package.ReadByte());
                map.Palette.Add(new BcSpawnPaletteEntry { Color = colour, Data = package.ReadString() });
            }
            map.Indices = package.ReadByteArray();
            return map;
        }

        private static void WriteNoiseStack(ZPackage package, BcNoiseStack stack)
        {
            int layers = stack?.Layers?.Count ?? 0;
            package.Write(layers);
            for (int at = 0; at < layers; at++)
            {
                BcNoiseLayer layer = stack.Layers[at];
                package.Write(layer.Version);
                WriteNoiseSettings(package, layer.Noise);
                WriteOptionalNoiseSettings(package, layer.NoiseWarp);
                WriteOptionalNoiseSettings(package, layer.Mask);
                WriteOptionalNoiseSettings(package, layer.MaskWarp);
            }
        }

        private static BcNoiseStack ReadNoiseStack(ZPackage package)
        {
            var stack = new BcNoiseStack();
            int layers = package.ReadInt();
            for (int at = 0; at < layers; at++)
            {
                var layer = new BcNoiseLayer { Version = package.ReadInt(), Noise = ReadNoiseSettings(package) };
                if (package.ReadBool()) layer.NoiseWarp = ReadNoiseSettings(package);
                if (package.ReadBool()) layer.Mask = ReadNoiseSettings(package);
                if (package.ReadBool()) layer.MaskWarp = ReadNoiseSettings(package);
                stack.Layers.Add(layer);
            }
            return stack;
        }

        private static void WriteOptionalNoiseSettings(ZPackage package, BcNoiseSettings settings)
        {
            package.Write(settings != null);
            if (settings != null) WriteNoiseSettings(package, settings);
        }

        private static void WriteNoiseSettings(ZPackage package, BcNoiseSettings settings)
        {
            package.Write(settings.Version);
            package.Write(settings.NoiseType);
            package.Write(settings.Frequency);
            package.Write(settings.Aspect);
            package.Write(settings.FractalType);
            package.Write(settings.FractalOctaves);
            package.Write(settings.FractalLacunarity);
            package.Write(settings.FractalGain);
            package.Write(settings.FractalWeightedStrength);
            package.Write(settings.FractalPingPongStrength);
            package.Write(settings.CellularDistanceFunction);
            package.Write(settings.CellularReturnType);
            package.Write(settings.CellularJitter);
            package.Write(settings.DomainWarpType);
            package.Write(settings.DomainWarpAmp);
            package.Write(settings.Invert);
            package.Write(settings.UseSmoothThreshold);
            package.Write(settings.SmoothThresholdStart);
            package.Write(settings.SmoothThresholdEnd);
            package.Write(settings.UseThreshold);
            package.Write(settings.Threshold);
            package.Write(settings.UseRange);
            package.Write(settings.RangeStart);
            package.Write(settings.RangeEnd);
            package.Write(settings.UseOpacity);
            package.Write(settings.Opacity);
            package.Write(settings.BlendMode);
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
    }
}
