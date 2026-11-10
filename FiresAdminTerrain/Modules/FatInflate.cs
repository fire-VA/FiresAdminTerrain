using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace VerdantsAscent
{
    // 0.2.1333 (RPC/size rule): the ONE inflate for bytes another peer can send (ZDO byte arrays, RPC packages, files from the join sync). A small
    // gzip / deflate can inflate without limit on every peer, so every read goes through Capped, which stops at its limit. Moved here from
    // WaterMeshCombinerAnchor.DecompressData (0.2.1332). Bytes answers null on a refused or corrupt block, it never throws; the stream readers
    // throw InvalidDataException past their limit, inside callers that already catch a corrupt file.
    internal static class FatInflate
    {
        // Largest FAT-owned ZDO blob in the VA world on 11-02: 14,895 bytes (va_clutter_paints). A foreign key reached 16.8 MB on a rig world.
        internal const int DefaultMaxBytes = 64 * 1024 * 1024;
        private const int BufferBytes = 81920;
        private const double MaxLogEverySeconds = 1d;

        private static int s_infoSaid;
        private static readonly Stopwatch s_clock = Stopwatch.StartNew();
        private static readonly Dictionary<string, KeyValuePair<long, double>> s_largest = new Dictionary<string, KeyValuePair<long, double>>();

        /// <summary>A gzip block (vanilla Utils.Compress is gzip too), null when corrupt or past maxBytes. Null in = null out, empty in = empty out.</summary>
        internal static byte[] Gzip(byte[] compressed, string what, int maxBytes = DefaultMaxBytes)
        {
            if (compressed == null || compressed.Length == 0) return compressed;
            return Bytes(compressed, 0, compressed.Length, true, what, maxBytes);
        }

        /// <summary>A raw deflate block, null when corrupt or past maxBytes.</summary>
        internal static byte[] Deflate(byte[] compressed, int offset, int count, string what, int maxBytes = DefaultMaxBytes)
        {
            if (compressed == null) return null;
            if (count <= 0) return Array.Empty<byte>();
            return Bytes(compressed, offset, count, false, what, maxBytes);
        }

        private static byte[] Bytes(byte[] compressed, int offset, int count, bool gzip, string what, int maxBytes)
        {
            try
            {
                var source = new MemoryStream(compressed, offset, count, false);
                using (var input = gzip ? GzipReader(source, what, maxBytes) : DeflateReader(source, what, maxBytes))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[BufferBytes];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
                    return output.ToArray();
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                FatNetGuard.Refuse("inflate " + what, $"{count} compressed bytes: {ex.Message}; that block is skipped");
                return null;
            }
        }

        internal static Capped GzipReader(Stream source, string what, long maxBytes, bool leaveOpen = false) =>
            new Capped(new GZipStream(source, CompressionMode.Decompress, leaveOpen), what, maxBytes);

        internal static Capped DeflateReader(Stream source, string what, long maxBytes, bool leaveOpen = false) =>
            new Capped(new DeflateStream(source, CompressionMode.Decompress, leaveOpen), what, maxBytes);

        // Debug: the largest size inflated per kind of data this session, only on a new maximum, at most one line a second per kind.
        private static void NoteSize(string what, long size)
        {
            if (!FiresLogger.DebugEnabled) return;
            lock (s_largest)
            {
                double now = s_clock.Elapsed.TotalSeconds;
                bool known = s_largest.TryGetValue(what, out var seen);
                if (known && size <= seen.Key) return;
                bool log = !known || now - seen.Value >= MaxLogEverySeconds;
                s_largest[what] = new KeyValuePair<long, double>(size, log ? now : seen.Value);
                if (log) FiresLogger.LogDebug($"[FatInflate] largest inflated this session, {what}: {size:N0} bytes (0.2.1333)");
            }
        }

        internal sealed class Capped : Stream
        {
            private readonly Stream _inner;
            private readonly string _what;
            private long _total;

            /// <summary>Raised by a reader once a header it has checked tells it the real size.</summary>
            internal long Limit;

            internal Capped(Stream inner, string what, long maxBytes)
            {
                _inner = inner;
                _what = what;
                Limit = maxBytes;
                if (Interlocked.Exchange(ref s_infoSaid, 1) == 0)
                    FiresLogger.LogInfo($"[FatInflate] data from other peers inflates through one capped path, {DefaultMaxBytes / (1024 * 1024)} MB unless its reader computes a size (0.2.1333)");
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = _inner.Read(buffer, offset, count);
                _total += read;
                if (_total > Limit) throw new InvalidDataException($"{_what} inflates past its limit of {Limit:N0} bytes");
                return read;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    NoteSize(_what, _total);
                    _inner.Dispose();
                }
                base.Dispose(disposing);
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
