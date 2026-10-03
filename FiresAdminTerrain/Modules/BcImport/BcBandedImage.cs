using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.BcImport
{
    // A map image kept as independent bands of rows (D11 part 2, Tools\BC_CHUNK_CONVERSION.md). It holds the PNG's own
    // scanlines, filtered the way PNG filters them, but each band starts from an empty row above and is deflated on its
    // own, so every band decodes on its own thread. A PNG only decodes one row after another, because each row and the
    // whole deflate stream lean on what came before: an 8192² 16-bit map took 3.5 s on one core at every load and join.
    // Layout: magic, version, width, height, bit depth, channels, rows per band, band count, each band's compressed
    // length, then the bands as raw deflate.
    internal static class BcBandedImage
    {
        private const int Magic = 0x444E4246;
        private const int FormatVersion = 1;
        private const int RowsPerBand = 128;
        private const int BitsPerByte = 8;
        private const int FilterNone = 0;
        private const int FilterSub = 1;
        private const int FilterUp = 2;
        private const int FilterAverage = 3;
        private const int FilterPaeth = 4;
        private const int FilterCount = 5;

        internal sealed class Layout
        {
            public int Width, Height, BitDepth, Channels, RowsPerBand, Bands;
            internal int[] BandStarts, BandLengths;

            public int BytesPerPixel => Channels * (BitDepth / BitsPerByte);
            public int RowBytes => Width * BytesPerPixel;
        }

        internal static bool IsBanded(byte[] data) => data != null && data.Length >= sizeof(int) && BitConverter.ToInt32(data, 0) == Magic;

        internal static Layout ReadLayout(byte[] data)
        {
            using (var reader = new BinaryReader(new MemoryStream(data, false)))
            {
                if (reader.ReadInt32() != Magic) throw new InvalidDataException("not a banded map image");
                int version = reader.ReadInt32();
                if (version != FormatVersion)
                    throw new InvalidDataException($"a banded map image is version {version}, this build reads {FormatVersion}");
                var layout = new Layout
                {
                    Width = reader.ReadInt32(),
                    Height = reader.ReadInt32(),
                    BitDepth = reader.ReadInt32(),
                    Channels = reader.ReadInt32(),
                    RowsPerBand = reader.ReadInt32(),
                    Bands = reader.ReadInt32(),
                };
                layout.BandLengths = new int[layout.Bands];
                layout.BandStarts = new int[layout.Bands];
                for (int band = 0; band < layout.Bands; band++) layout.BandLengths[band] = reader.ReadInt32();
                int start = (int)reader.BaseStream.Position;
                for (int band = 0; band < layout.Bands; band++)
                {
                    layout.BandStarts[band] = start;
                    start += layout.BandLengths[band];
                }
                if (start != data.Length) throw new InvalidDataException("a banded map image is cut short or has bytes left over");
                return layout;
            }
        }

        // The PNG's rows re-stored in bands, then read back and compared with the PNG byte for byte: null when any row differs.
        internal static byte[] FromPng(byte[] png, out string failure)
        {
            failure = null;
            byte[] raw = FiresPng.DecodeRaw(png, out int width, out int height, out int bitDepth, out int channels);
            byte[] banded = Encode(raw, width, height, bitDepth, channels);
            int mismatched = CountMismatchedRows(banded, raw);
            if (mismatched == 0) return banded;
            failure = $"{mismatched} of {height} row(s) read back different from the PNG";
            return null;
        }

        // Every band decodes on its own thread, so `onRow` runs for several rows at once, each call with its own row (row 0
        // is the image's top). The buffer is reused after the call: copy out what is needed.
        internal static void DecodeRows(byte[] data, Layout layout, Action<int, byte[]> onRow) =>
            Parallel.For(0, layout.Bands, band => DecodeBand(data, layout, band, onRow));

        // Each worker thread keeps its band buffers (about 2 MB for a 16-bit 8192² map) instead of allocating them per band:
        // a world's decode was about 450 MB of short-lived arrays for the game's garbage collector.
        [ThreadStatic] private static byte[] t_filtered;
        [ThreadStatic] private static byte[] t_above;
        [ThreadStatic] private static byte[] t_row;

        private static byte[] Scratch(ref byte[] buffer, int length)
        {
            if (buffer == null || buffer.Length < length) buffer = new byte[length];
            return buffer;
        }

        private static void DecodeBand(byte[] data, Layout layout, int band, Action<int, byte[]> onRow)
        {
            int rowBytes = layout.RowBytes;
            int stride = rowBytes + 1;
            int first = band * layout.RowsPerBand;
            int rows = Math.Min(layout.RowsPerBand, layout.Height - first);
            var filtered = Scratch(ref t_filtered, rows * stride);
            using (var inflater = new DeflateStream(new MemoryStream(data, layout.BandStarts[band], layout.BandLengths[band], false),
                CompressionMode.Decompress))
                ReadFully(inflater, filtered, rows * stride);
            var above = Scratch(ref t_above, rowBytes);
            var row = Scratch(ref t_row, rowBytes);
            Array.Clear(above, 0, rowBytes);
            for (int at = 0; at < rows; at++)
            {
                Unfilter(filtered, at * stride, above, row, rowBytes, layout.BytesPerPixel);
                onRow(first + at, row);
                var finished = above;
                above = row;
                row = finished;
            }
        }

        private static void ReadFully(Stream stream, byte[] into, int length)
        {
            for (int got = 0; got < length;)
            {
                int read = stream.Read(into, got, length - got);
                if (read <= 0) throw new EndOfStreamException("a band of a banded map image ended early");
                got += read;
            }
        }

        // One loop per filter, so the per-byte work carries no filter switch; the first pixel of a row has nothing to its
        // left, which is why each filter starts with its own short loop.
        private static void Unfilter(byte[] filtered, int at, byte[] above, byte[] row, int rowBytes, int bpp)
        {
            int filter = filtered[at];
            int from = at + 1;
            int lead = Math.Min(bpp, rowBytes);
            switch (filter)
            {
                case FilterNone:
                    Buffer.BlockCopy(filtered, from, row, 0, rowBytes);
                    return;
                case FilterSub:
                    for (int x = 0; x < lead; x++) row[x] = filtered[from + x];
                    for (int x = bpp; x < rowBytes; x++) row[x] = (byte)(filtered[from + x] + row[x - bpp]);
                    return;
                case FilterUp:
                    for (int x = 0; x < rowBytes; x++) row[x] = (byte)(filtered[from + x] + above[x]);
                    return;
                case FilterAverage:
                    for (int x = 0; x < lead; x++) row[x] = (byte)(filtered[from + x] + (above[x] >> 1));
                    for (int x = bpp; x < rowBytes; x++) row[x] = (byte)(filtered[from + x] + ((row[x - bpp] + above[x]) >> 1));
                    return;
                case FilterPaeth:
                    for (int x = 0; x < lead; x++) row[x] = (byte)(filtered[from + x] + above[x]);
                    for (int x = bpp; x < rowBytes; x++) row[x] = (byte)(filtered[from + x] + Paeth(row[x - bpp], above[x], above[x - bpp]));
                    return;
                default:
                    throw new InvalidDataException($"a banded map image has a row with unknown filter {filter}");
            }
        }

        private static byte[] Encode(byte[] raw, int width, int height, int bitDepth, int channels)
        {
            var layout = new Layout
            {
                Width = width, Height = height, BitDepth = bitDepth, Channels = channels,
                RowsPerBand = RowsPerBand, Bands = (height + RowsPerBand - 1) / RowsPerBand,
            };
            var bands = new byte[layout.Bands][];
            Parallel.For(0, layout.Bands, band => bands[band] = EncodeBand(raw, layout, band));
            var output = new MemoryStream();
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(width);
                writer.Write(height);
                writer.Write(bitDepth);
                writer.Write(channels);
                writer.Write(layout.RowsPerBand);
                writer.Write(layout.Bands);
                foreach (byte[] band in bands) writer.Write(band.Length);
                foreach (byte[] band in bands) writer.Write(band);
            }
            return output.ToArray();
        }

        // Each row takes the filter that leaves the smallest signed residuals (libpng's own heuristic), which is what keeps
        // a banded map about the size of its PNG.
        private static byte[] EncodeBand(byte[] raw, Layout layout, int band)
        {
            int rowBytes = layout.RowBytes;
            int bpp = layout.BytesPerPixel;
            int first = band * layout.RowsPerBand;
            int end = Math.Min(layout.Height, first + layout.RowsPerBand);
            var emptyRow = new byte[rowBytes];
            var trial = new byte[rowBytes + 1];
            var best = new byte[rowBytes + 1];
            var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
            {
                for (int row = first; row < end; row++)
                {
                    byte[] above = row == first ? emptyRow : raw;
                    int aboveAt = row == first ? 0 : (row - 1) * rowBytes;
                    long bestScore = long.MaxValue;
                    for (int filter = FilterNone; filter < FilterCount; filter++)
                    {
                        long score = Filter(filter, raw, row * rowBytes, above, aboveAt, rowBytes, bpp, trial);
                        if (score >= bestScore) continue;
                        bestScore = score;
                        var kept = best;
                        best = trial;
                        trial = kept;
                    }
                    deflate.Write(best, 0, best.Length);
                }
            }
            return output.ToArray();
        }

        private static long Filter(int filter, byte[] raw, int at, byte[] above, int aboveAt, int rowBytes, int bpp, byte[] into)
        {
            into[0] = (byte)filter;
            long score = 0;
            for (int x = 0; x < rowBytes; x++)
            {
                int left = x >= bpp ? raw[at + x - bpp] : 0;
                int up = above[aboveAt + x];
                int upLeft = x >= bpp ? above[aboveAt + x - bpp] : 0;
                byte residual = (byte)(raw[at + x] - Predict(filter, left, up, upLeft));
                into[x + 1] = residual;
                score += Math.Abs((int)(sbyte)residual);
            }
            return score;
        }

        private static int Predict(int filter, int left, int up, int upLeft)
        {
            switch (filter)
            {
                case FilterSub: return left;
                case FilterUp: return up;
                case FilterAverage: return (left + up) >> 1;
                case FilterPaeth: return Paeth(left, up, upLeft);
                default: return 0;
            }
        }

        private static int Paeth(int left, int up, int upLeft)
        {
            int estimate = left + up - upLeft;
            int toLeft = Math.Abs(estimate - left);
            int toUp = Math.Abs(estimate - up);
            int toUpLeft = Math.Abs(estimate - upLeft);
            return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
        }

        private static int CountMismatchedRows(byte[] banded, byte[] raw)
        {
            Layout layout = ReadLayout(banded);
            int rowBytes = layout.RowBytes;
            int mismatched = 0;
            DecodeRows(banded, layout, (row, bytes) =>
            {
                if (!SameBytes(bytes, raw, row * rowBytes, rowBytes)) Interlocked.Increment(ref mismatched);
            });
            return mismatched;
        }

        private static bool SameBytes(byte[] row, byte[] raw, int at, int count)
        {
            for (int x = 0; x < count; x++)
                if (row[x] != raw[at + x]) return false;
            return true;
        }
    }
}
