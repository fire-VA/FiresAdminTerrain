using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── VOXEL CHUNK CACHE SYNC (server-authoritative) ────────────────────
    //
    // The cache was local-only: no RPC, no sync. Every client regenerated every chunk it walked through, and a
    // dedicated server - which has no client to build one for it - never had a cache at all. Measured on
    // BlueHillsTrueVox 2026-09-26, a first traverse left 22 zones showing vanilla heightmap, longest 38 s,
    // pendingGen=17 queued=68, while the voxel mesher fell behind a running player.
    //
    // WHY THE WHOLE CACHE GOES BEFORE SPAWN, not streamed while moving. Streaming ahead of the player has the
    // same failure as generating ahead of the player: whatever the pipeline, a sprint or a teleport outruns it
    // and you stand on terrain that is not there. Fire's call, and it is the right one - a transfer is also far
    // cheaper than the alternative it replaces, since generating this world's grid took 8m34s.
    //
    // WHAT MAKES THAT AFFORDABLE is content addressing, copied from BetterContinents: the client sends the
    // chunks it already holds and the server sends only the difference. A returning client transfers nothing.
    // The cache directory is already named after a hash of the generation settings (VoxelChunkCache.Key), so
    // two machines only ever exchange chunks generated the same way; per chunk, the .vcz header's genHash
    // distinguishes a chunk whose ground has since been edited.
    //
    // WHAT WE DO DIFFERENTLY FROM BC, and why:
    //   - BC serialises its whole payload into one array before chunking. At 205 MB across 2,368 files that
    //     would be a 205 MB allocation on both ends, so files are streamed one at a time and memory stays flat.
    //   - BC fails the join if a transfer breaks. Each chunk is committed to the client's cache as it lands, so
    //     a reconnect resumes: the manifest diff naturally asks only for what is still missing.
    internal static class VoxelCacheSync
    {
        internal const string RpcManifest = "FiresVoxelCacheManifest";
        internal const string RpcBegin = "FiresVoxelCacheBegin";
        internal const string RpcChunk = "FiresVoxelCacheChunk";
        internal const string RpcDone = "FiresVoxelCacheDone";
        internal const string RpcReady = "FiresVoxelCacheReady";

        // BC's size, for the same reason: big enough that per-packet overhead is noise, small enough that the
        // send queue can be drained between packets instead of saturating.
        internal const int SendChunkBytes = 128 * 1024;

        internal struct ChunkId : IEquatable<ChunkId>
        {
            public int X, Y, Z;
            public uint GenHash;

            public bool Equals(ChunkId other) => X == other.X && Y == other.Y && Z == other.Z && GenHash == other.GenHash;
            public override bool Equals(object obj) => obj is ChunkId other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + X; h = h * 31 + Y; h = h * 31 + Z;
                    return h * 31 + (int)GenHash;
                }
            }
            public Vector3Int Key => new Vector3Int(X, Y, Z);
            public override string ToString() => $"{X}_{Y}_{Z}";
        }

        /// <summary>
        /// Every chunk this machine holds for the current world and generation key. Reads only each file's
        /// 10-byte header, so 2,368 chunks cost 2,368 short reads rather than 205 MB of I/O.
        /// </summary>
        internal static List<ChunkId> BuildManifest()
        {
            var list = new List<ChunkId>();
            string root = VoxelChunkCache.Root;
            if (!VoxelChunkCache.Ready || string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;

            string[] files;
            try { files = Directory.GetFiles(root, "*.vcz", SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxelSync] could not read the cache at '{root}': {ex.Message}");
                return list;
            }

            for (int i = 0; i < files.Length; i++)
            {
                if (!TryParseKey(Path.GetFileNameWithoutExtension(files[i]), out int x, out int y, out int z)) continue;
                if (!VoxelChunkCache.TryReadHeaderHash(files[i], out uint genHash)) continue;
                list.Add(new ChunkId { X = x, Y = y, Z = z, GenHash = genHash });
            }
            return list;
        }

        // "x_y_z", and every part may be negative.
        private static bool TryParseKey(string name, out int x, out int y, out int z)
        {
            x = y = z = 0;
            if (string.IsNullOrEmpty(name)) return false;
            string[] parts = name.Split('_');
            return parts.Length == 3
                   && int.TryParse(parts[0], out x)
                   && int.TryParse(parts[1], out y)
                   && int.TryParse(parts[2], out z);
        }

        internal static ZPackage SerializeManifest(List<ChunkId> chunks)
        {
            var pkg = new ZPackage();
            pkg.Write(VoxelChunkCache.Key ?? string.Empty);
            pkg.Write(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                pkg.Write(c.X); pkg.Write(c.Y); pkg.Write(c.Z);
                pkg.Write((int)c.GenHash);
            }
            return pkg;
        }

        internal static HashSet<ChunkId> DeserializeManifest(ZPackage pkg, out string key)
        {
            var set = new HashSet<ChunkId>();
            key = pkg.ReadString();
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++)
            {
                set.Add(new ChunkId
                {
                    X = pkg.ReadInt(),
                    Y = pkg.ReadInt(),
                    Z = pkg.ReadInt(),
                    GenHash = (uint)pkg.ReadInt(),
                });
            }
            return set;
        }

        /// <summary>
        /// What the holder has that the asker does not. A chunk whose genHash differs counts as missing: the
        /// ground under it was edited, so the asker's copy is stale and must be replaced rather than kept.
        /// </summary>
        internal static List<ChunkId> Missing(List<ChunkId> held, HashSet<ChunkId> asked)
        {
            var missing = new List<ChunkId>();
            for (int i = 0; i < held.Count; i++)
                if (!asked.Contains(held[i])) missing.Add(held[i]);
            return missing;
        }

        internal static long TotalBytes(List<ChunkId> chunks)
        {
            long total = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                try
                {
                    var info = new FileInfo(VoxelChunkCache.FilePathFor(chunks[i].Key));
                    if (info.Exists) total += info.Length;
                }
                catch { }
            }
            return total;
        }

        /// <summary>Commits a received chunk straight to disk, so a broken transfer resumes instead of restarting.</summary>
        internal static bool WriteReceivedChunk(ChunkId id, byte[] payload)
        {
            if (payload == null || payload.Length < VoxelChunkCache.HeaderSize) return false;
            if (BitConverter.ToUInt32(payload, 0) != VoxelChunkCache.HeaderMagic) return false;
            if (payload[4] != VoxelChunkCache.HeaderFormat) return false;
            if (BitConverter.ToUInt32(payload, 6) != id.GenHash) return false;

            try
            {
                string path = VoxelChunkCache.FilePathFor(id.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Write-then-move: a chunk half-written when the process dies must not read back as valid.
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, payload);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxelSync] could not store chunk {id}: {ex.Message}");
                return false;
            }
        }

        internal static byte[] ReadChunk(ChunkId id)
        {
            try
            {
                string path = VoxelChunkCache.FilePathFor(id.Key);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch { return null; }
        }
    }
}
