using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace VerdantsAscent.Terrain.Voxel
{
    // Minimal deterministic PNG reader for worldgen inputs — greyscale 8/16
    // bit (heightmaps, BC convention: L16 normalized 0..1) and RGB(A) 8-bit
    // (biome color maps). No interlace support. BetterContinents vendors all
    // of ImageSharp for this; we need exactly two pixel walks.
    public static class FiresPng
    {
        // Grey 0..1, row-major with row 0 = TOP of the image (north).
        public static float[] LoadGray01(string path, out int w, out int h)
        {
            var raw = Decode(path, out w, out h, out int bitDepth, out int channels);
            var outp = new float[w * h];
            if (channels == 1 && bitDepth == 16)
            {
                for (int i = 0; i < outp.Length; i++)
                    outp[i] = ((raw[i * 2] << 8) | raw[i * 2 + 1]) / 65535f;
            }
            else if (channels == 1)
            {
                for (int i = 0; i < outp.Length; i++) outp[i] = raw[i] / 255f;
            }
            else
            {
                int stride = channels * (bitDepth / 8);
                for (int i = 0; i < outp.Length; i++) outp[i] = raw[i * stride] / 255f;
            }
            return outp;
        }

        // (r,g,b) bytes per pixel, row 0 = top. For biome color maps.
        public static byte[] LoadRgb(string path, out int w, out int h)
        {
            var raw = Decode(path, out w, out h, out int bitDepth, out int channels);
            var outp = new byte[w * h * 3];
            int bytesPer = bitDepth / 8;
            int stride = channels * bytesPer;
            for (int i = 0; i < w * h; i++)
            {
                if (channels >= 3)
                {
                    outp[i * 3] = raw[i * stride];
                    outp[i * 3 + 1] = raw[i * stride + bytesPer];
                    outp[i * 3 + 2] = raw[i * stride + bytesPer * 2];
                }
                else
                {
                    byte g = raw[i * stride];
                    outp[i * 3] = g; outp[i * 3 + 1] = g; outp[i * 3 + 2] = g;
                }
            }
            return outp;
        }

        private static byte[] Decode(string path, out int w, out int h, out int bitDepth, out int channels)
        {
            return DecodeRaw(File.ReadAllBytes(path), out w, out h, out bitDepth, out channels);
        }

        // A PNG's size, format and where its compressed data sits inside the file, read without copying anything.
        public sealed class PngLayout
        {
            public int Width, Height, BitDepth, Channels;
            internal readonly List<KeyValuePair<int, int>> Idat = new List<KeyValuePair<int, int>>();
            public int RowBytes => Width * Channels * (BitDepth / 8);
        }

        private const int ZlibHeaderBytes = 2;

        public static PngLayout ReadLayout(byte[] bytes)
        {
            if (bytes.Length < 8 || bytes[1] != 'P' || bytes[2] != 'N' || bytes[3] != 'G')
                throw new Exception("not a PNG");
            var layout = new PngLayout();
            int colorType = 0;
            int pos = 8;
            while (pos + 8 <= bytes.Length)
            {
                int len = (bytes[pos] << 24) | (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3];
                string type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
                int dataAt = pos + 8;
                if (type == "IHDR")
                {
                    layout.Width = (bytes[dataAt] << 24) | (bytes[dataAt + 1] << 16) | (bytes[dataAt + 2] << 8) | bytes[dataAt + 3];
                    layout.Height = (bytes[dataAt + 4] << 24) | (bytes[dataAt + 5] << 16) | (bytes[dataAt + 6] << 8) | bytes[dataAt + 7];
                    layout.BitDepth = bytes[dataAt + 8];
                    colorType = bytes[dataAt + 9];
                    if (bytes[dataAt + 12] != 0) throw new Exception("interlaced PNG not supported");
                }
                else if (type == "IDAT") layout.Idat.Add(new KeyValuePair<int, int>(dataAt, len));
                else if (type == "IEND") break;
                pos = dataAt + len + 4;
            }
            layout.Channels = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 4 ? 2 : colorType == 6 ? 4 : -1;
            if (layout.Channels < 0) throw new Exception($"unsupported PNG color type {colorType}");
            if (layout.BitDepth != 8 && layout.BitDepth != 16) throw new Exception($"unsupported PNG bit depth {layout.BitDepth}");
            return layout;
        }

        // Rows top-first, one row of channels x (bitDepth/8) bytes per pixel, 16-bit samples big-endian.
        public static byte[] DecodeRaw(byte[] bytes, out int w, out int h, out int bitDepth, out int channels)
        {
            PngLayout layout = ReadLayout(bytes);
            w = layout.Width;
            h = layout.Height;
            bitDepth = layout.BitDepth;
            channels = layout.Channels;
            int rowBytes = layout.RowBytes;
            var outp = new byte[h * rowBytes];
            DecodeRows(bytes, layout, (y, row) => Buffer.BlockCopy(row, 0, outp, y * rowBytes, rowBytes));
            return outp;
        }

        // Each unfiltered row, top-first, handed to `onRow` and then reused: copy out what is needed, keep no reference.
        // The compressed data is read where it sits in `bytes`, and no whole-image buffer is ever built, so an 8192² map
        // costs two row buffers instead of three copies of itself.
        public static void DecodeRows(byte[] bytes, PngLayout layout, Action<int, byte[]> onRow)
        {
            int h = layout.Height;
            int bpp = layout.Channels * (layout.BitDepth / 8);
            int rowBytes = layout.RowBytes;
            var idat = new IdatStream(bytes, layout.Idat);
            var header = new byte[ZlibHeaderBytes];
            if (idat.Read(header, 0, ZlibHeaderBytes) != ZlibHeaderBytes) throw new EndOfStreamException();
            using (var ds = new DeflateStream(idat, CompressionMode.Decompress))
            {
                var prev = new byte[rowBytes];
                var cur = new byte[rowBytes];
                var one = new byte[1];
                for (int y = 0; y < h; y++)
                {
                    if (ds.Read(one, 0, 1) != 1) throw new EndOfStreamException();
                    int filter = one[0];
                    int got = 0;
                    while (got < rowBytes)
                    {
                        int r = ds.Read(cur, got, rowBytes - got);
                        if (r <= 0) throw new EndOfStreamException();
                        got += r;
                    }
                    for (int x = 0; x < rowBytes; x++)
                    {
                        int a = x >= bpp ? cur[x - bpp] : 0;
                        int b = prev[x];
                        int c = x >= bpp ? prev[x - bpp] : 0;
                        int v = cur[x];
                        switch (filter)
                        {
                            case 1: v += a; break;
                            case 2: v += b; break;
                            case 3: v += (a + b) >> 1; break;
                            case 4:
                                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                                v += pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
                                break;
                        }
                        cur[x] = (byte)v;
                    }
                    onRow(y, cur);
                    var t = prev; prev = cur; cur = t;
                }
            }
        }

        // The IDAT chunks read in order where they sit in the file, as one stream.
        private sealed class IdatStream : Stream
        {
            private readonly byte[] _bytes;
            private readonly List<KeyValuePair<int, int>> _segments;
            private int _segment;
            private int _offset;

            internal IdatStream(byte[] bytes, List<KeyValuePair<int, int>> segments)
            {
                _bytes = bytes;
                _segments = segments;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                while (_segment < _segments.Count)
                {
                    KeyValuePair<int, int> segment = _segments[_segment];
                    int left = segment.Value - _offset;
                    if (left <= 0)
                    {
                        _segment++;
                        _offset = 0;
                        continue;
                    }
                    int copied = Math.Min(left, count);
                    Buffer.BlockCopy(_bytes, segment.Key + _offset, buffer, offset, copied);
                    _offset += copied;
                    return copied;
                }
                return 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
