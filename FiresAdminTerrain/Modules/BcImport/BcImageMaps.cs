using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
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
        private const int SnapReportDistance = 4096;   // RGB distance 64 - past this a colour is not a near miss
        private const int FarColoursListed = 6;
        private const float FarWarnPercent = 1f;
        private const float PercentScale = 100f;
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

        /// <summary>The float maps are independent PNGs, so they decode side by side on worker threads.</summary>
        public static bool TryDecode(BcWorldSettings settings, out BcDecodedMaps maps, out string failure)
        {
            maps = new BcDecodedMaps();
            failure = null;
            var decoded = maps;
            try
            {
                Parallel.Invoke(
                    () => decoded.Height = DecodeFloatMap(settings.HeightMap),
                    () => decoded.Rough = DecodeFloatMap(settings.RoughMap),
                    () => decoded.Flat = DecodeFloatMap(settings.FlatMap),
                    () => decoded.Forest = DecodeFloatMap(settings.ForestMap),
                    () => decoded.Heat = DecodeFloatMap(settings.HeatMap),
                    () => decoded.Lava = DecodeFloatMap(settings.LavaMap),
                    () => decoded.Moss = DecodeFloatMap(settings.MossMap));
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
                Exception cause = ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : ex;
                failure = $"{cause.GetType().Name}: {cause.Message}";
                return false;
            }
        }

        /// <summary>BC asks ImageSharp for L16, or La16 when HeightMapAlpha is set - and ImageSharp's La16 is
        /// EIGHT bits of luminance, so an alpha heightmap really is quantised to 256 steps.</summary>
        // Every value is one of 256 or 65,536 levels, so a map keeps the LEVEL (2 bytes) and one shared table turns it into
        // the float BC computes, by the same division, bit for bit. Rows decode straight into the levels, so an 8192² map
        // costs 128 MB kept instead of 256 MB kept plus a 128 MB raw copy and three copies of its compressed bytes
        // (a join allocated ~3 GB, [perf] 2026-09-27). A banded image hands over its rows from several threads at once, and
        // each call writes only its own row of levels.
        private static BcFloatMap DecodeFloatMap(BcMapImage image)
        {
            if (image == null || image.Data == null || image.Data.Length == 0) return null;

            var pixels = new BcMapPixels(image.Data);
            int width = pixels.Width, height = pixels.Height, bitDepth = pixels.BitDepth, channels = pixels.Channels;
            RequireSquare(width, height, image);
            RejectSixteenBitGreyAlpha(bitDepth, channels, image);

            bool eightBitLevels = image.Legacy8BitRed || image.HasAlphaChannel;
            var levels = new ushort[width * height];
            int stride = channels * (bitDepth / EightBit);
            bool plainGrey = !eightBitLevels && channels == GreyChannels;
            pixels.DecodeRows((sourceRow, raw) =>
            {
                int targetRow = (height - 1 - sourceRow) * width;
                if (plainGrey && bitDepth == SixteenBit) SixteenBitGreyRow(raw, levels, targetRow, width);
                else if (plainGrey) EightBitGreyRow(raw, levels, targetRow, width);
                else
                    for (int column = 0; column < width; column++)
                    {
                        int at = column * stride;
                        levels[targetRow + column] = image.Legacy8BitRed
                            ? RedChannel8(raw, at, bitDepth)
                            : image.HasAlphaChannel
                                ? EightBitLuminance(raw, at, bitDepth, channels)
                                : SixteenBitLuminance(raw, at, bitDepth, channels);
                    }
            });
            return new BcFloatMap { Levels = levels, Scale = eightBitLevels ? BcFloatMap.EightBitScale : BcFloatMap.SixteenBitScale, Size = width };
        }

        // Plain greyscale, the maps BC worlds carry, each without a per-pixel branch: the same values SixteenBitLuminance gives
        // (a 16-bit sample as is, an 8-bit one widened by 257), about 2 ns a pixel instead of the generic path's branches on
        // every one of a world's 335 M pixels.
        private static void SixteenBitGreyRow(byte[] raw, ushort[] levels, int targetRow, int width)
        {
            for (int column = 0, at = 0; column < width; column++, at += 2)
                levels[targetRow + column] = (ushort)((raw[at] << EightBit) | raw[at + 1]);
        }

        private static void EightBitGreyRow(byte[] raw, ushort[] levels, int targetRow, int width)
        {
            for (int column = 0; column < width; column++)
                levels[targetRow + column] = UpscaleTo16Bit(raw[column]);
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

            byte[] raw = BcMapPixels.DecodeRaw(image.Data, out int width, out int height, out int bitDepth, out int channels);
            RequireSquare(width, height, image);

            List<KeyValuePair<Heightmap.Biome, Color32>> palette = ParseBiomeColors(
                string.IsNullOrEmpty(image.ColorTable) ? DefaultBiomeColors : image.ColorTable);

            var biomes = new Heightmap.Biome[width * height];
            var seen = new Dictionary<int, Heightmap.Biome>();
            var exact = new Dictionary<int, bool>();
            var far = new Dictionary<int, long>();
            long snapped = 0;
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
                        biome = NearestBiome(palette, pixel, out int distance);
                        seen[key] = biome;
                        exact[key] = distance == 0;
                        if (distance > SnapReportDistance) far[key] = 0L;
                    }
                    if (!exact[key]) snapped++;
                    long farSeen;
                    if (far.TryGetValue(key, out farSeen)) far[key] = farSeen + 1L;
                    biomes[targetRow + column] = biome;
                }
            }

            // Worth saying out loud, because NearestBiome has no distance cutoff: a colour that is nowhere near any
            // palette entry still becomes SOME biome rather than an error. A map saved through a lossy editor comes
            // back with thousands of near-miss colours - Craters' own map is 744 colours, about half of its pixels
            // not an exact match - and those all resolve correctly. A high figure here with biomes landing WRONG is
            // the signal that the palette and the image disagree about what a biome's colour is.
            long total = (long)width * height;
            FiresLogger.LogInfo($"[BcImport] biome image {width}x{height}: {seen.Count} distinct colour(s), "
                + $"{snapped * PercentScale / total:0.0}% of pixels not an exact palette colour and snapped to the "
                + "nearest. Black means 'leave the biome here to whatever is underneath' and is not a miss.");
            ReportFarColours(far, seen, total);
            return new BcBiomeMap { Biomes = biomes, Size = width };
        }

        // The black entry is a SENTINEL, not a colour to compete on distance. It means "leave the biome here to
        // whatever is underneath", which is how an Expand World biome such as RedThicket shows through a
        // BetterContinents map, and it is the one palette entry where being near-miss is meaningful rather than
        // sloppy. Letting it into the nearest-colour pool turns an anti-aliased dark edge - a BlackForest border
        // pixel like 013000 sits closer to 000000 than to 007F00 - into a one-pixel hole in the biome map, which
        // reads in game as a speck of the wrong biome and in a census as fall-through the author never asked for.
        // That was FAT's own rule (black only within RGB 30 of itself). BetterContinents (0.7.30 and 0.9.4 alike) has no such
        // rule: black is an ordinary legend colour, and a pixel takes the nearest legend colour of all, the first one in legend
        // order on a tie (OrderBy(ColorDistance).First()). The BcRef test (2026-10-05) showed the difference: dark pixels at
        // the edges of a map painted with black areas came out a biome here and None in BetterContinents, so scattered points
        // read a different biome. Now it is BetterContinents' rule exactly; matching it is the point of the import.
        private static Heightmap.Biome NearestBiome(List<KeyValuePair<Heightmap.Biome, Color32>> palette, Color32 pixel,
            out int distance)
        {
            Heightmap.Biome nearest = Heightmap.Biome.None;
            int nearestDistance = int.MaxValue;
            foreach (KeyValuePair<Heightmap.Biome, Color32> candidate in palette)
            {
                int candidateDistance = SquaredRgbDistance(pixel, candidate.Value);
                if (candidateDistance >= nearestDistance) continue;   // strict: the first of equals wins, as OrderBy().First()
                nearestDistance = candidateDistance;
                nearest = candidate.Key;
            }
            distance = nearestDistance;
            return nearest;
        }

        /// <summary>Names the colours that were nowhere near a palette entry, worst first. A map painted with the
        /// right colours produces none of these; a long list is the thing to correct in the source image.</summary>
        private static void ReportFarColours(Dictionary<int, long> far, Dictionary<int, Heightmap.Biome> seen, long total)
        {
            if (far.Count == 0) return;
            var ordered = new List<KeyValuePair<int, long>>(far);
            ordered.Sort((left, right) => right.Value.CompareTo(left.Value));

            var text = new StringBuilder();
            long carried = 0L;
            for (int index = 0; index < ordered.Count; index++)
            {
                carried += ordered[index].Value;
                if (index >= FarColoursListed) continue;
                if (text.Length > 0) text.Append(", ");
                text.Append($"#{ordered[index].Key:X6}->{seen[ordered[index].Key]} {ordered[index].Value * PercentScale / total:0.00}%");
            }
            // Anti-aliased edges alone produce hundreds of these - Craters' map has 566 of them across 0.22% of its
            // pixels, every one a blend between two neighbouring biomes. That is not worth a warning on every load.
            // A map whose colours genuinely disagree with the palette shows up as a large fraction, not a long list.
            float fraction = carried * PercentScale / total;
            string report = $"[BcImport] {far.Count} colour(s) in the biome image are not close to ANY palette entry "
                + $"({fraction:0.00}% of pixels) and were snapped to the nearest anyway. Worst: {text}.";
            if (fraction < FarWarnPercent)
            {
                FiresLogger.LogInfo(report + " That is edge blending, not a palette mismatch.");
                return;
            }
            FiresLogger.LogWarning(report + " That is too much of the map to be edge blending - check that the image "
                + "was painted with the palette's own colours, and repaint any whose biome landed wrong.");
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

            byte[] raw = BcMapPixels.DecodeRaw(image.Data, out int width, out int height, out int bitDepth, out int channels);
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

        // A biome legend the way BetterContinents 0.9.3+ reads it (Fire 10-05: converted maps erred with Expand World Data's
        // 1.0 biomes). Before, one name that was not a vanilla biome ("DeadWastes: 8B4513", an Expand World Data biome) threw,
        // the whole decode failed, and the world was not reproduced at all. Now:
        //  - names may be written with spaces, underscores or hyphens, in any case ("Black Forest", "deep_north");
        //  - a name Expand World Data defines (when it is loaded) is that biome, by Expand World Data's own number;
        //  - a line that cannot be used (no such biome here, a colour that does not parse, not "name: colour") is reported
        //    and skipped, and the rest of the legend still applies. A skipped name's colour is kept as None, so its pixels
        //    are left to the default generation instead of snapping to the nearest other biome; a vanilla biome whose colour
        //    does not parse keeps its default colour.
        private static List<KeyValuePair<Heightmap.Biome, Color32>> ParseBiomeColors(string colors)
        {
            var palette = new List<KeyValuePair<Heightmap.Biome, Color32>>();
            var skipped = new List<string>();
            foreach (string raw in colors.Split('|', '\n'))
            {
                string entry = raw.Trim();
                if (entry.Length == 0 || entry.StartsWith("#")) continue;
                int colon = entry.LastIndexOf(':');
                if (colon <= 0) { skipped.Add($"'{entry}' (not 'name: colour')"); continue; }
                string name = entry.Substring(0, colon).Trim();
                bool known = TryParseBiome(name, out Heightmap.Biome biome);
                Color32 color;
                try { color = ParseColor(entry.Substring(colon + 1)); }
                catch (Exception)
                {
                    if (known && DefaultBiomeColor(biome, out Color32 fallback))
                    {
                        palette.Add(new KeyValuePair<Heightmap.Biome, Color32>(biome, fallback));
                        skipped.Add($"'{entry}' (colour does not parse; {biome} keeps its default colour)");
                    }
                    else skipped.Add($"'{entry}' (colour does not parse)");
                    continue;
                }
                if (!known)
                {
                    palette.Add(new KeyValuePair<Heightmap.Biome, Color32>(Heightmap.Biome.None, color));
                    skipped.Add($"'{entry}' (no biome '{name}' here: Expand World Data not loaded or not defining it; its pixels keep the default generation)");
                    continue;
                }
                palette.Add(new KeyValuePair<Heightmap.Biome, Color32>(biome, color));
            }
            if (skipped.Count > 0)
                FiresLogger.LogWarning($"[BcImport] biome legend: {skipped.Count} line(s) skipped, the rest applies: {string.Join("; ", skipped)}");
            bool anyBiome = false;
            foreach (var p in palette) if (p.Key != Heightmap.Biome.None) { anyBiome = true; break; }
            return anyBiome || ReferenceEquals(colors, DefaultBiomeColors) ? palette : ParseBiomeColors(DefaultBiomeColors);
        }

        private static bool DefaultBiomeColor(Heightmap.Biome biome, out Color32 color)
        {
            foreach (var p in ParseBiomeColors(DefaultBiomeColors))
                if (p.Key == biome) { color = p.Value; return true; }
            color = default;
            return false;
        }

        private static bool TryParseBiome(string name, out Heightmap.Biome biome)
        {
            string key = SquashName(name);
            foreach (Heightmap.Biome candidate in Enum.GetValues(typeof(Heightmap.Biome)))
            {
                if (!SquashName(candidate.ToString()).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                biome = candidate;
                return true;
            }
            return ExpandWorldDataBiome(name, out biome) || ExpandWorldDataBiome(key, out biome);
        }

        private static string SquashName(string name) => name.Replace(" ", "").Replace("_", "").Replace("-", "");

        // Expand World Data's own name lookup (ExpandWorldData.BiomeManager.TryGetBiome(string, out Heightmap.Biome)), by
        // reflection: FAT does not reference it, and without it no added biome exists.
        private static System.Reflection.MethodInfo s_ewdTryGetBiome;
        private static bool s_ewdLooked;

        private static bool ExpandWorldDataBiome(string name, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (!s_ewdLooked)
            {
                s_ewdLooked = true;
                try
                {
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("expand_world_data", out var info) && info?.Instance != null)
                        s_ewdTryGetBiome = HarmonyLib.AccessTools.Method(info.Instance.GetType().Assembly.GetType("ExpandWorldData.BiomeManager"),
                            "TryGetBiome", new[] { typeof(string), typeof(Heightmap.Biome).MakeByRefType() });
                }
                catch (Exception) { s_ewdTryGetBiome = null; }
            }
            if (s_ewdTryGetBiome == null) return false;
            try
            {
                var call = new object[] { name, Heightmap.Biome.None };
                if (!(s_ewdTryGetBiome.Invoke(null, call) is bool found) || !found) return false;
                biome = (Heightmap.Biome)call[1];
                return biome != Heightmap.Biome.None;
            }
            catch (Exception) { return false; }
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

        private static byte RedChannel8(byte[] raw, int at, int bitDepth) =>
            bitDepth == SixteenBit ? DownscaleTo8Bit(BigEndian16(raw, at)) : raw[at];

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
        // level -> the float BC gives it: level / 255f for 8-bit sources, level / 65535f for 16-bit ones.
        public static readonly float[] EightBitScale = BuildScale(byte.MaxValue);
        public static readonly float[] SixteenBitScale = BuildScale(ushort.MaxValue);

        public ushort[] Levels;
        public float[] Scale;
        public int Size;

        public int Count => Levels.Length;

        public float this[int index] => Scale[Levels[index]];

        private static float[] BuildScale(int maxLevel)
        {
            var scale = new float[maxLevel + 1];
            for (int level = 0; level <= maxLevel; level++) scale[level] = level / (float)maxLevel;
            return scale;
        }

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
                Mathf.Lerp(this[bottom * Size + left], this[bottom * Size + right], columnFraction),
                Mathf.Lerp(this[top * Size + left], this[top * Size + right], columnFraction),
                rowFraction);
        }
    }

    /// <summary>Biomes are not interpolated: the four neighbours are weighted by the bilinear factors and the
    /// heaviest biome wins.</summary>
    internal sealed class BcBiomeMap
    {
        private const int Corners = 4;

        // One scratch pair per thread: a join samples this 4.2 M times across the worker threads (GenerateBiomePoints), and
        // two small arrays per call were 8.4 M allocations for the game's garbage collector.
        [ThreadStatic] private static Heightmap.Biome[] t_found;
        [ThreadStatic] private static float[] t_weights;

        public Heightmap.Biome[] Biomes;
        public int Size;

        public Heightmap.Biome GetValue(float x, float y)
        {
            float column = x * (Size - 1);
            float row = y * (Size - 1);   // float, as BetterContinents' biome map computes it (a double product can floor differently)
            int columnFloor = Mathf.FloorToInt(column);
            int rowFloor = Mathf.FloorToInt(row);
            float columnFraction = column - columnFloor;
            float rowFraction = row - rowFloor;

            var found = t_found ?? (t_found = new Heightmap.Biome[Corners]);
            var weights = t_weights ?? (t_weights = new float[Corners]);
            Array.Clear(found, 0, Corners);
            Array.Clear(weights, 0, Corners);
            int count = 0;
            int heaviest = 0;
            Accumulate(columnFloor, rowFloor, (1f - columnFraction) * (1f - rowFraction), found, weights, ref count, ref heaviest);
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
