using System;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.BcImport
{
    // A stored map image's rows, whether it is still the PNG it arrived as (rows one after another) or re-stored in bands
    // (BcBandedImage, rows side by side). Either way the rows are the same bytes, top row first, 16-bit samples big-endian.
    internal sealed class BcMapPixels
    {
        private const int BitsPerByte = 8;

        private readonly byte[] _data;
        private readonly FiresPng.PngLayout _png;
        private readonly BcBandedImage.Layout _banded;

        public readonly int Width;
        public readonly int Height;
        public readonly int BitDepth;
        public readonly int Channels;

        internal BcMapPixels(byte[] data)
        {
            _data = data;
            if (BcBandedImage.IsBanded(data))
            {
                _banded = BcBandedImage.ReadLayout(data);
                Width = _banded.Width;
                Height = _banded.Height;
                BitDepth = _banded.BitDepth;
                Channels = _banded.Channels;
                return;
            }
            _png = FiresPng.ReadLayout(data);
            Width = _png.Width;
            Height = _png.Height;
            BitDepth = _png.BitDepth;
            Channels = _png.Channels;
        }

        public int RowBytes => Width * Channels * (BitDepth / BitsPerByte);

        // For a banded image `onRow` runs for several rows at once, each call with its own row; for a PNG, in order. The
        // buffer is reused after the call either way: copy out what is needed.
        internal void DecodeRows(Action<int, byte[]> onRow)
        {
            if (_banded != null) BcBandedImage.DecodeRows(_data, _banded, onRow);
            else FiresPng.DecodeRows(_data, _png, onRow);
        }

        internal static byte[] DecodeRaw(byte[] data, out int width, out int height, out int bitDepth, out int channels)
        {
            var pixels = new BcMapPixels(data);
            width = pixels.Width;
            height = pixels.Height;
            bitDepth = pixels.BitDepth;
            channels = pixels.Channels;
            int rowBytes = pixels.RowBytes;
            var raw = new byte[pixels.Height * rowBytes];
            pixels.DecodeRows((row, bytes) => Buffer.BlockCopy(bytes, 0, raw, row * rowBytes, rowBytes));
            return raw;
        }
    }
}
