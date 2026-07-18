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
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 || bytes[1] != 'P' || bytes[2] != 'N' || bytes[3] != 'G')
                throw new Exception("not a PNG");
            w = h = bitDepth = 0;
            int colorType = 0;
            var idat = new List<byte[]>();
            int pos = 8;
            while (pos + 8 <= bytes.Length)
            {
                int len = (bytes[pos] << 24) | (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3];
                string type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
                int dataAt = pos + 8;
                if (type == "IHDR")
                {
                    w = (bytes[dataAt] << 24) | (bytes[dataAt + 1] << 16) | (bytes[dataAt + 2] << 8) | bytes[dataAt + 3];
                    h = (bytes[dataAt + 4] << 24) | (bytes[dataAt + 5] << 16) | (bytes[dataAt + 6] << 8) | bytes[dataAt + 7];
                    bitDepth = bytes[dataAt + 8];
                    colorType = bytes[dataAt + 9];
                    if (bytes[dataAt + 12] != 0) throw new Exception("interlaced PNG not supported");
                }
                else if (type == "IDAT")
                {
                    var seg = new byte[len];
                    Buffer.BlockCopy(bytes, dataAt, seg, 0, len);
                    idat.Add(seg);
                }
                else if (type == "IEND") break;
                pos = dataAt + len + 4;
            }
            channels = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 4 ? 2 : colorType == 6 ? 4 : -1;
            if (channels < 0) throw new Exception($"unsupported PNG color type {colorType}");
            if (bitDepth != 8 && bitDepth != 16) throw new Exception($"unsupported PNG bit depth {bitDepth}");

            int total = 0;
            foreach (var seg in idat) total += seg.Length;
            var z = new byte[total];
            int o = 0;
            foreach (var seg in idat) { Buffer.BlockCopy(seg, 0, z, o, seg.Length); o += seg.Length; }

            int bpp = channels * (bitDepth / 8);
            int rowBytes = w * bpp;
            var outp = new byte[h * rowBytes];
            using (var ms = new MemoryStream(z, 2, z.Length - 2))   // skip zlib header
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
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
                    Buffer.BlockCopy(cur, 0, outp, y * rowBytes, rowBytes);
                    var t = prev; prev = cur; cur = t;
                }
            }
            return outp;
        }
    }
}
