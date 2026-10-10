using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;

namespace VerdantsAscent.Terrain.BcImport
{
    // The server's biome grid for a joining client (D12, Tools\BC_CHUNK_CONVERSION.md). Valheim 1.0's VerifyBiomeData rebuilds
    // a 2048² grid of GetBiome + GetBiomeHeight on EVERY join, because a joined client's World carries no world version:
    // 1.6 s even in parallel, plus a 20 MB cache write. The client needs the result (terrain colour, environments, the
    // minimap's biome name, spawn levels), so it cannot be skipped. The server already holds the same grid, so it hands its
    // own over: serialised and deflated once per world load off the main thread, streamed by BcJoinSync right after the
    // BetterContinents data, content-addressed like it. The client loads it instead of sampling and then runs vanilla's
    // GenerateSectors on it, so its sectors and alt biomes are the server's own. Anything off falls back to vanilla, with
    // its own line.
    internal static class BiomeGridSync
    {
        private const int Magic = 0x44474246;
        private const int FormatVersion = 1;
        private const int MaxGridSize = 8192;
        private const int HeightBytes = sizeof(float);
        private const double BytesPerMegabyte = 1024d * 1024d;

        private static readonly AccessTools.FieldRef<AltBiomeWorldData, World> WorldOf =
            AccessTools.FieldRefAccess<AltBiomeWorldData, World>("m_world");

        private static Task<Blob> s_blob;

        internal sealed class Blob
        {
            internal byte[] Data;
            internal string Id;
            internal int Size;
            internal long RawBytes;
        }

        /// <summary>The server's grid for this world, deflated once; null when this peer has none to give.</summary>
        internal static Task<Blob> ServerBlob => s_blob;

        // Server, after its own VerifyBiomeData (FatVanillaHooks): the grid's arrays are final by then, so a worker reads them.
        internal static void OfferServerGrid(World world)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var data = world?.m_biomeData;
            if (data == null || !data.PointsGenerated)
            {
                s_blob = null;
                return;
            }
            int size = data.Size;
            float[,] heights = data.PointHeights;
            Heightmap.BiomeIndex[,] biomes = data.PointBiomes;
            s_blob = Task.Run(() => Serialise(size, heights, biomes));
        }

        private static Blob Serialise(int size, float[,] heights, Heightmap.BiomeIndex[,] biomes)
        {
            int cells = size * size;
            var heightBytes = new byte[cells * HeightBytes];
            Buffer.BlockCopy(heights, 0, heightBytes, 0, heightBytes.Length);
            var biomeBytes = new byte[cells];
            int at = 0;
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                    biomeBytes[at++] = (byte)biomes[x, y];
            var output = new MemoryStream();
            using (var header = new BinaryWriter(output, Encoding.UTF8, true))
            {
                header.Write(Magic);
                header.Write(FormatVersion);
                header.Write(size);
            }
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
            {
                deflate.Write(heightBytes, 0, heightBytes.Length);
                deflate.Write(biomeBytes, 0, biomeBytes.Length);
            }
            byte[] blob = output.ToArray();
            return new Blob { Data = blob, Id = BcJoinSync.IdOf(blob), Size = size, RawBytes = heightBytes.LongLength + biomeBytes.LongLength };
        }

        // Client, first thing in VerifyBiomeData (FatVanillaHooks): true when the server's grid now stands in for vanilla's build.
        internal static bool TryTakeServerGrid(World world)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || world == null) return false;
            switch (BcJoinSync.BiomeGridAnswer(out string path))
            {
                case BcJoinSync.ServerAnswer.Data:
                    break;
                case BcJoinSync.ServerAnswer.Failed:
                    FiresLogger.LogWarning("[BcImport] biome grid: the server's grid did not arrive intact, so this client builds its own (vanilla, ~1.6 s).");
                    return false;
                default:
                    FiresLogger.LogInfo("[BcImport] biome grid: the server sent none this join (not ready, or its FAT predates it), so this "
                        + "client builds its own (vanilla).");
                    return false;
            }
            var clock = Stopwatch.StartNew();
            AltBiomeWorldData data;
            string problem;
            try { data = Load(path, out problem); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BcImport] biome grid: loading {path} threw ({ex.GetType().Name}: {ex.Message}), so this client "
                    + "builds its own (vanilla).");
                return false;
            }
            if (data == null)
            {
                FiresLogger.LogWarning($"[BcImport] biome grid: {path} {problem}, so this client builds its own (vanilla).");
                return false;
            }
            long loadMs = clock.ElapsedMilliseconds;
            WorldOf(data) = world;
            world.m_biomeData = data;
            data.GenerateSectors();
            FiresLogger.LogInfo($"[BcImport] biome grid: took the server's {data.Size}² grid instead of sampling it: load {loadMs} ms, "
                + $"sectors {clock.ElapsedMilliseconds - loadMs} ms.");
            return true;
        }

        private static AltBiomeWorldData Load(string path, out string problem)
        {
            problem = null;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var header = new BinaryReader(file, Encoding.UTF8, true))
            {
                if (header.ReadInt32() != Magic) { problem = "is not a biome grid"; return null; }
                int version = header.ReadInt32();
                if (version != FormatVersion) { problem = $"is biome grid format {version}, this build reads {FormatVersion}"; return null; }
                int size = header.ReadInt32();
                if (size <= 0 || size > MaxGridSize) { problem = $"says its grid is {size}²"; return null; }
                int cells = size * size;
                var heightBytes = new byte[cells * HeightBytes];
                var biomeBytes = new byte[cells];
                using (var inflate = new DeflateStream(file, CompressionMode.Decompress, true))
                {
                    if (!ReadFully(inflate, heightBytes) || !ReadFully(inflate, biomeBytes))
                    {
                        problem = $"ends before its {cells:N0} cells";
                        return null;
                    }
                }
                var data = new AltBiomeWorldData(size);
                Buffer.BlockCopy(heightBytes, 0, data.PointHeights, 0, heightBytes.Length);
                int at = 0;
                for (int x = 0; x < size; x++)
                    for (int y = 0; y < size; y++)
                        data.PointBiomes[x, y] = (Heightmap.BiomeIndex)biomeBytes[at++];
                data.PointsGenerated = true;
                return data;
            }
        }

        private static bool ReadFully(Stream stream, byte[] into)
        {
            for (int got = 0; got < into.Length;)
            {
                int read = stream.Read(into, got, into.Length - got);
                if (read <= 0) return false;
                got += read;
            }
            return true;
        }

        internal static string Describe(Blob blob) =>
            $"{blob.Size}² biome grid, {blob.Data.Length / BytesPerMegabyte:0.#} MB deflated from {blob.RawBytes / BytesPerMegabyte:0.#} MB";
    }
}
