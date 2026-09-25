using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.BcImport
{
    /// <summary>
    /// Decodes the maps a BetterContinents save carries, matching what ImageSharp hands BC pixel for pixel:
    /// the vertical flip, the L16 / La16 / legacy-Rgba32 conversions, the biome bit-index table and the
    /// colour-table maps. Spec: Docs/BETTERCONTINENTS_IMPORT.md
    /// </summary>
    internal static class BcImageMaps
    {
        private const int GreyChannels = 1;
        private const int GreyAlphaChannels = 2;
        private const int RgbChannels = 3;
        private const int RgbaChannels = 4;
        private const int EightBit = 8;
        private const int SixteenBit = 16;
        private const int MaxBiomeIndices = 32;
        private const string DefaultBiomeColors =
            "None: 000000|Meadows: 00FF00|BlackForest: 007F00|Swamp: 7F7F00|Mountain: FFFFFF"
            + "|Plains: FFFF00|Mistlands: 7F7F7F|AshLands: FF0000|DeepNorth: 00FFFF|Ocean: 0000FF";
        private const string DefaultTerrainColors =
            "Default: 000000|Meadows: 00FF00|BlackForest: 007F00|Swamp: 7F7F00|Mountain: FFFFFF"
            + "|Plains: FFFF00|Mistlands: 7F7F7F|AshLands: FF0000|DeepNorth: 00FFFF|Ocean: 0000FF";

        private static readonly Dictionary<string, Color32?> TerrainGrounds = new Dictionary<string, Color32?>
        {
            { "default", null },
            { "meadows", new Color32(0, 0, 0, 0) },
            { "blackforest", new Color32(0, 0, 255, 0) },
            { "swamp", new Color32(255, 0, 0, 0) },
            { "mountain", new Color32(0, 255, 0, 0) },
        };

        public static bool TryDecode(BcWorldSettings settings, out BcDecodedMaps maps, out string failure)
        {
            maps = new BcDecodedMaps();
            failure = null;
            try
            {
                maps.Height = DecodeFloatMap(settings.HeightMap);
                maps.Rough = DecodeFloatMap(settings.RoughMap);
                maps.Flat = DecodeFloatMap(settings.FlatMap);
                maps.Forest = DecodeFloatMap(settings.ForestMap);
                maps.Heat = DecodeFloatMap(settings.HeatMap);
                maps.Lava = DecodeFloatMap(settings.LavaMap);
                maps.Moss = DecodeFloatMap(settings.MossMap);
                maps.Terrain = DecodeColorMap(settings.TerrainMap, DefaultTerrainColors, TerrainGrounds);
                maps.Paint = DecodeColorMap(settings.PaintMap, "", null);
                maps.Biome = settings.BiomeIndexMap != null
                    ? DecodeBiomeIndexMap(settings.BiomeIndexMap)
                    : DecodeLegacyBiomeImage(settings.LegacyBiomeImage);
                maps.Spawn = BcSpawnLookup.From(settings.SpawnMap);
                maps.Vegetation = BcSpawnLookup.From(settings.VegetationMap);
                maps.Location = settings.LocationMap;
                return true;
            }
            catch (Exception ex)
            {
                maps = null;
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>BC asks ImageSharp for L16, or La16 when HeightMapAlpha is set - and ImageSharp's La16 is
        /// EIGHT bits of luminance, so an alpha heightmap really is quantised to 256 steps.</summary>
        private static BcFloatMap DecodeFloatMap(BcMapImage image)
        {
            if (image == null || image.Data == null || image.Data.Length == 0) return null;

            byte[] raw = FiresPng.DecodeRaw(image.Data, out int width, out int height, out int bitDepth, out int channels);
            RequireSquare(width, height, image);
            RejectSixteenBitGreyAlpha(bitDepth, channels, image);

            var values = new float[width * height];
            int stride = channels * (bitDepth / EightBit);
            for (int row = 0; row < height; row++)
            {
                int sourceRow = (height - 1 - row) * width * stride;
                int targetRow = row * width;
                for (int column = 0; column < width; column++)
                {
                    int at = sourceRow + column * stride;
                    values[targetRow + column] = image.Legacy8BitRed
                        ? RedChannel01(raw, at, bitDepth)
                        : image.HasAlphaChannel
                            ? EightBitLuminance(raw, at, bitDepth, channels) / (float)byte.MaxValue
                            : SixteenBitLuminance(raw, at, bitDepth, channels) / (float)ushort.MaxValue;
                }
            }
            return new BcFloatMap { Values = values, Size = width };
        }

        private static BcBiomeMap DecodeBiomeIndexMap(BcBiomeIndexMap indexMap)
        {
            int size = indexMap.Size;
            if (size * size != indexMap.Indices.Length)
                throw new Exception($"the biome map holds {indexMap.Indices.Length} bytes, which is not a square");

            var biomes = new Heightmap.Biome[indexMap.Indices.Length];
            for (int at = 0; at < biomes.Length; at++) biomes[at] = BiomeFromIndex(indexMap.Indices[at]);
            return new BcBiomeMap { Biomes = biomes, Size = size };
        }

        /// <summary>Index 0 is None, index i is the flag 1 &lt;&lt; (i - 1). Heightmap.Biome's flag values are
        /// unchanged in 1.0; Heightmap.BiomeIndex numbers biomes differently and must not be used here.</summary>
        public static Heightmap.Biome BiomeFromIndex(byte index)
        {
            if (index == 0 || index >= MaxBiomeIndices) return Heightmap.Biome.None;
            return (Heightmap.Biome)(1 << (index - 1));
        }

        private static BcBiomeMap DecodeLegacyBiomeImage(BcMapImage image)
        {
            if (image == null || image.Data == null || image.Data.Length == 0) return null;

            byte[] raw = FiresPng.DecodeRaw(image.Data, out int width, out int height, out int bitDepth, out int channels);
            RequireSquare(width, height, image);

            List<KeyValuePair<Heightmap.Biome, Color32>> palette = ParseBiomeColors(
                string.IsNullOrEmpty(image.ColorTable) ? DefaultBiomeColors : image.ColorTable);

            var biomes = new Heightmap.Biome[width * height];
            var seen = new Dictionary<int, Heightmap.Biome>();
            int stride = channels * (bitDepth / EightBit);
            for (int row = 0; row < height; row++)
            {
                int sourceRow = (height - 1 - row) * width * stride;
                int targetRow = row * width;
                for (int column = 0; column < width; column++)
                {
                    Color32 pixel = PixelAsRgba32(raw, sourceRow + column * stride, bitDepth, channels);
                    int key = (pixel.r << EightBit * 2) | (pixel.g << EightBit) | pixel.b;
                    if (!seen.TryGetValue(key, out Heightmap.Biome biome))
                    {
                        biome = NearestBiome(palette, pixel);
                        seen[key] = biome;
                    }
                    biomes[targetRow + column] = biome;
                }
            }
            return new BcBiomeMap { Biomes = biomes, Size = width };
        }

        private static Heightmap.Biome NearestBiome(List<KeyValuePair<Heightmap.Biome, Color32>> palette, Color32 pixel)
        {
            Heightmap.Biome nearest = Heightmap.Biome.None;
            int nearestDistance = int.MaxValue;
            foreach (KeyValuePair<Heightmap.Biome, Color32> candidate in palette)
            {
                int distance = SquaredRgbDistance(pixel, candidate.Value);
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = candidate.Key;
            }
            return nearest;
        }

        private static int SquaredRgbDistance(Color32 first, Color32 second)
        {
            int red = first.r - second.r;
            int green = first.g - second.g;
            int blue = first.b - second.b;
            return red * red + green * green + blue * blue;
        }

        /// <summary>A pixel whose colour is in the table becomes the table's value, which may be "no value" and
        /// makes a sample fail so vanilla keeps its own colour there. Anything else passes through unchanged.</summary>
        private static BcColorMap DecodeColorMap(BcMapImage image, string defaultColors, Dictionary<string, Color32?> namedColors)
        {
            if (image == null || image.Data == null || image.Data.Length == 0) return null;

            byte[] raw = FiresPng.DecodeRaw(image.Data, out int width, out int height, out int bitDepth, out int channels);
            RequireSquare(width, height, image);

            Dictionary<int, Color32?> table = ParseColorTable(
                image.ColorTable == "" ? defaultColors : image.ColorTable, namedColors);

            var colors = new Color32?[width * height];
            int stride = channels * (bitDepth / EightBit);
            for (int row = 0; row < height; row++)
            {
                int sourceRow = (height - 1 - row) * width * stride;
                int targetRow = row * width;
                for (int column = 0; column < width; column++)
                {
                    Color32 pixel = PixelAsRgba32(raw, sourceRow + column * stride, bitDepth, channels);
                    colors[targetRow + column] = table.TryGetValue(RgbaKey(pixel), out Color32? mapped) ? mapped : pixel;
                }
            }
            return new BcColorMap { Colors = colors, Size = width };
        }

        private static List<KeyValuePair<Heightmap.Biome, Color32>> ParseBiomeColors(string colors)
        {
            var palette = new List<KeyValuePair<Heightmap.Biome, Color32>>();
            foreach (string entry in colors.Split('|'))
            {
                string[] parts = entry.Trim().Split(':');
                if (parts.Length != 2) continue;
                if (!TryParseBiome(parts[0].Trim(), out Heightmap.Biome biome))
                    throw new Exception("invalid biome name " + parts[0]);
                palette.Add(new KeyValuePair<Heightmap.Biome, Color32>(biome, ParseColor(parts[1])));
            }
            return palette;
        }

        private static bool TryParseBiome(string name, out Heightmap.Biome biome)
        {
            foreach (Heightmap.Biome candidate in Enum.GetValues(typeof(Heightmap.Biome)))
            {
                if (!candidate.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                biome = candidate;
                return true;
            }
            biome = Heightmap.Biome.None;
            return false;
        }

        private static Dictionary<int, Color32?> ParseColorTable(string colors, Dictionary<string, Color32?> namedColors)
        {
            var table = new Dictionary<int, Color32?>();
            foreach (string entry in colors.Split('|'))
            {
                string[] parts = entry.Trim().Split(':');
                if (parts.Length != 2) continue;
                int sourceColor = RgbaKey(ParseColor(parts[1]));
                if (table.ContainsKey(sourceColor)) continue;
                string name = parts[0].Trim();
                Color32? target = namedColors != null && namedColors.TryGetValue(name.ToLowerInvariant(), out Color32? ground)
                    ? ground
                    : ParseColor(name);
                table[sourceColor] = target;
            }
            return table;
        }

        /// <summary>BC's own colour grammar: a hex string, or "r,g,b" / "r,g,b,a" decimal bytes.</summary>
        private static Color32 ParseColor(string color)
        {
            string text = color.Trim();
            string[] parts = text.Split(',');
            if (parts.Length == 1)
            {
                string hex = text.StartsWith("#") ? text.Substring(1) : text;
                if (hex.Length == 6 || hex.Length == 8)
                {
                    byte red = ParseHexByte(hex, 0);
                    byte green = ParseHexByte(hex, 2);
                    byte blue = ParseHexByte(hex, 4);
                    byte alpha = hex.Length == 8 ? ParseHexByte(hex, 6) : byte.MaxValue;
                    return new Color32(red, green, blue, alpha);
                }
                return new Color32(0, 0, 0, 0);
            }
            if (parts.Length < RgbChannels) return new Color32(0, 0, 0, 0);
            return new Color32(
                byte.Parse(parts[0], CultureInfo.InvariantCulture),
                byte.Parse(parts[1], CultureInfo.InvariantCulture),
                byte.Parse(parts[2], CultureInfo.InvariantCulture),
                parts.Length == RgbChannels ? byte.MaxValue : byte.Parse(parts[3], CultureInfo.InvariantCulture));
        }

        private static byte ParseHexByte(string hex, int at) =>
            byte.Parse(hex.Substring(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        private static int RgbaKey(Color32 color) =>
            (color.r << EightBit * 3) | (color.g << EightBit * 2) | (color.b << EightBit) | color.a;

        private static Color32 PixelAsRgba32(byte[] raw, int at, int bitDepth, int channels)
        {
            if (bitDepth == EightBit)
            {
                if (channels >= RgbChannels)
                    return new Color32(raw[at], raw[at + 1], raw[at + 2],
                        channels == RgbaChannels ? raw[at + 3] : byte.MaxValue);
                return new Color32(raw[at], raw[at], raw[at],
                    channels == GreyAlphaChannels ? raw[at + 1] : byte.MaxValue);
            }
            if (channels >= RgbChannels)
                return new Color32(
                    DownscaleTo8Bit(BigEndian16(raw, at)),
                    DownscaleTo8Bit(BigEndian16(raw, at + 2)),
                    DownscaleTo8Bit(BigEndian16(raw, at + 4)),
                    channels == RgbaChannels ? DownscaleTo8Bit(BigEndian16(raw, at + 6)) : byte.MaxValue);
            byte grey = DownscaleTo8Bit(BigEndian16(raw, at));
            return new Color32(grey, grey, grey,
                channels == GreyAlphaChannels ? DownscaleTo8Bit(BigEndian16(raw, at + 2)) : byte.MaxValue);
        }

        private static ushort SixteenBitLuminance(byte[] raw, int at, int bitDepth, int channels)
        {
            if (channels <= GreyAlphaChannels)
                return bitDepth == SixteenBit ? BigEndian16(raw, at) : UpscaleTo16Bit(raw[at]);
            if (bitDepth == SixteenBit)
                return Bt709Luminance(BigEndian16(raw, at), BigEndian16(raw, at + 2), BigEndian16(raw, at + 4));
            return Bt709Luminance(UpscaleTo16Bit(raw[at]), UpscaleTo16Bit(raw[at + 1]), UpscaleTo16Bit(raw[at + 2]));
        }

        private static byte EightBitLuminance(byte[] raw, int at, int bitDepth, int channels)
        {
            if (channels <= GreyAlphaChannels)
                return bitDepth == SixteenBit ? DownscaleTo8Bit(BigEndian16(raw, at)) : raw[at];
            if (bitDepth == SixteenBit)
                return (byte)Bt709Luminance(
                    DownscaleTo8Bit(BigEndian16(raw, at)),
                    DownscaleTo8Bit(BigEndian16(raw, at + 2)),
                    DownscaleTo8Bit(BigEndian16(raw, at + 4)));
            return (byte)Bt709Luminance(raw[at], raw[at + 1], raw[at + 2]);
        }

        private static float RedChannel01(byte[] raw, int at, int bitDepth) =>
            (bitDepth == SixteenBit ? DownscaleTo8Bit(BigEndian16(raw, at)) : raw[at]) / (float)byte.MaxValue;

        private static ushort Bt709Luminance(ushort red, ushort green, ushort blue) =>
            (ushort)(red * 0.2125999927520752 + green * 0.71520000696182251 + blue * 0.0722000002861023 + 0.5);

        private static ushort UpscaleTo16Bit(byte component) => (ushort)(component * 257);

        private static byte DownscaleTo8Bit(ushort component) => (byte)((component * byte.MaxValue + 32895) >> SixteenBit);

        private static ushort BigEndian16(byte[] raw, int at) => (ushort)((raw[at] << EightBit) | raw[at + 1]);

        private static void RequireSquare(int width, int height, BcMapImage image)
        {
            if (width == height) return;
            throw new Exception($"{DescribeSource(image)} is {width}x{height}; BetterContinents only accepts square maps");
        }

        private static void RejectSixteenBitGreyAlpha(int bitDepth, int channels, BcMapImage image)
        {
            if (bitDepth != SixteenBit || channels != GreyAlphaChannels) return;
            throw new Exception($"{DescribeSource(image)} is 16-bit greyscale+alpha, which FAT cannot yet convert the way ImageSharp does");
        }

        private static string DescribeSource(BcMapImage image) =>
            string.IsNullOrEmpty(image.FilePath) ? "an embedded map" : image.FilePath;
    }

    internal sealed class BcDecodedMaps
    {
        public BcFloatMap Height;
        public BcFloatMap Rough;
        public BcFloatMap Flat;
        public BcFloatMap Forest;
        public BcFloatMap Heat;
        public BcFloatMap Lava;
        public BcFloatMap Moss;
        public BcBiomeMap Biome;
        public BcColorMap Terrain;
        public BcColorMap Paint;
        public BcSpawnLookup Spawn;
        public BcSpawnLookup Vegetation;
        public BcLocationMap Location;
    }

    internal sealed class BcFloatMap
    {
        public float[] Values;
        public int Size;

        public float GetValue(float x, float y)
        {
            float column = x * (Size - 1);
            float row = (float)((double)y * (Size - 1));
            int columnFloor = Mathf.FloorToInt(column);
            int rowFloor = Mathf.FloorToInt(row);
            float columnFraction = column - columnFloor;
            float rowFraction = row - rowFloor;
            int left = Mathf.Clamp(columnFloor, 0, Size - 1);
            int right = Mathf.Clamp(columnFloor + 1, 0, Size - 1);
            int bottom = Mathf.Clamp(rowFloor, 0, Size - 1);
            int top = Mathf.Clamp(rowFloor + 1, 0, Size - 1);
            return Mathf.Lerp(
                Mathf.Lerp(Values[bottom * Size + left], Values[bottom * Size + right], columnFraction),
                Mathf.Lerp(Values[top * Size + left], Values[top * Size + right], columnFraction),
                rowFraction);
        }
    }

    /// <summary>Biomes are not interpolated: the four neighbours are weighted by the bilinear factors and the
    /// heaviest biome wins.</summary>
    internal sealed class BcBiomeMap
    {
        private const int Corners = 4;

        public Heightmap.Biome[] Biomes;
        public int Size;

        public Heightmap.Biome GetValue(float x, float y)
        {
            float column = x * (Size - 1);
            float row = (float)((double)y * (Size - 1));
            int columnFloor = Mathf.FloorToInt(column);
            int rowFloor = Mathf.FloorToInt(row);
            float columnFraction = column - columnFloor;
            float rowFraction = row - rowFloor;

            var found = new Heightmap.Biome[Corners];
            var weights = new float[Corners];
            int count = 0;
            int heaviest = 0;
            Accumulate(columnFloor, rowFloor, (float)((1.0 - columnFraction) * (1.0 - rowFraction)), found, weights, ref count, ref heaviest);
            Accumulate(columnFloor + 1, rowFloor, columnFraction * (1f - rowFraction), found, weights, ref count, ref heaviest);
            Accumulate(columnFloor, rowFloor + 1, (1f - columnFraction) * rowFraction, found, weights, ref count, ref heaviest);
            Accumulate(columnFloor + 1, rowFloor + 1, columnFraction * rowFraction, found, weights, ref count, ref heaviest);
            return found[heaviest];
        }

        private void Accumulate(int column, int row, float weight, Heightmap.Biome[] found, float[] weights, ref int count, ref int heaviest)
        {
            Heightmap.Biome biome = Biomes[Mathf.Clamp(row, 0, Size - 1) * Size + Mathf.Clamp(column, 0, Size - 1)];
            for (int at = 0; at < count; at++)
            {
                if (found[at] != biome) continue;
                if (weights[at] + weight > weights[heaviest]) heaviest = at;
                weights[at] += weight;
                return;
            }
            if (weights[count] + weight > weights[heaviest]) heaviest = count;
            found[count] = biome;
            weights[count++] = weight;
        }
    }

    internal sealed class BcColorMap
    {
        public Color32?[] Colors;
        public int Size;

        public bool TryGetValue(float x, float y, out Color color)
        {
            float column = x * (Size - 1);
            float row = (float)((double)y * (Size - 1));
            int columnFloor = Mathf.FloorToInt(column);
            int rowFloor = Mathf.FloorToInt(row);
            float columnFraction = column - columnFloor;
            float rowFraction = row - rowFloor;
            int left = Mathf.Clamp(columnFloor, 0, Size - 1);
            int right = Mathf.Clamp(columnFloor + 1, 0, Size - 1);
            int bottom = Mathf.Clamp(rowFloor, 0, Size - 1);
            int top = Mathf.Clamp(rowFloor + 1, 0, Size - 1);

            Color32? bottomLeft = Colors[bottom * Size + left];
            Color32? bottomRight = Colors[bottom * Size + right];
            Color32? topLeft = Colors[top * Size + left];
            Color32? topRight = Colors[top * Size + right];
            if (!bottomLeft.HasValue || !bottomRight.HasValue || !topLeft.HasValue || !topRight.HasValue)
            {
                color = Color.black;
                return false;
            }

            Color32 blended = Color32.Lerp(
                Color32.Lerp(bottomLeft.Value, bottomRight.Value, columnFraction),
                Color32.Lerp(topLeft.Value, topRight.Value, columnFraction),
                rowFraction);
            color = new Color(
                blended.r / (float)byte.MaxValue,
                blended.g / (float)byte.MaxValue,
                blended.b / (float)byte.MaxValue,
                blended.a / (float)byte.MaxValue);
            return true;
        }
    }

    /// <summary>Spawn and vegetation maps are nearest-pixel, not interpolated, and an index past the palette
    /// means "nothing here".</summary>
    internal sealed class BcSpawnLookup
    {
        public List<BcSpawnPaletteEntry> Palette;
        public byte[] Indices;
        public int Size;

        public static BcSpawnLookup From(BcSpawnMap map)
        {
            if (map == null || map.Indices == null || map.Indices.Length == 0) return null;
            return new BcSpawnLookup { Palette = map.Palette, Indices = map.Indices, Size = map.Size };
        }

        public bool TryGetEntry(float x, float y, out string data)
        {
            int column = Mathf.RoundToInt(x * (Size - 1));
            int row = Mathf.RoundToInt((float)((double)y * (Size - 1)));
            byte index = Indices[row * Size + column];
            if (index >= Palette.Count)
            {
                data = null;
                return false;
            }
            data = Palette[index].Data;
            return true;
        }
    }
}
