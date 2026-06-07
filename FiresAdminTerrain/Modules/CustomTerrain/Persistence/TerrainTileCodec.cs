using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Pure-C# encode/decode for a single tile's persistent state.
    // No Unity main-thread deps (no Mesh / Texture2D / etc.) — safe to call
    // from BackgroundWorker per the async plan in TERRAIN_PERSISTENCE_REDESIGN_PLAN.md.
    //
    // Wire format (post-gzip):
    //   u8  schema_version            (current: 1)
    //   i32 tile_x, i32 tile_z        (Vector2i)
    //   u16 height_modified_count
    //     for each: u16 index, f32 height
    //   u16 paint_modified_count
    //     for each: u16 index, u8 r, u8 g, u8 b, u8 a
    //   u16 disk_count
    //     for each: DiskRecord (see WriteDisk / ReadDisk)
    //
    // The whole stream is gzip-compressed in Encode / decompressed in Decode.
    // Sparse encoding (only modified vertices) matches vanilla TerrainComp's
    // m_modifiedHeight / m_modifiedPaint pattern. Typical edited tile is
    // 3-8 KB compressed; worst case (heavily edited 64x64m with 100+ disks)
    // stays under the ZDO 32 KB practical limit.
    public static class TerrainTileCodec
    {
        // Vertex grid is 65x65 (one ring of border vertices each side).
        // Border vertices coincide with neighbor tiles' borders by construction
        // so the combiner can dedup at world position on weld.
        public const int VERTEX_GRID_SIZE = 65;
        public const int VERTEX_COUNT = VERTEX_GRID_SIZE * VERTEX_GRID_SIZE; // 4225

        // v2 added per-tile BiomeOverride (ushort, 0 = no override, otherwise
        // a Heightmap.Biome enum value). v1 files load with BiomeOverride=0.
        // v3 added per-vertex biome (sparse encoding like Heights/Paint).
        // v2 files load with Biome[] empty; TerrainTile.Initialize promotes
        // BiomeOverride into the per-vertex array if no per-vertex data
        // exists (full-tile-uniform upgrade), so v2 saves visually match.
        // v4 (2026-05-26) added FloatingIslandMode bool — toggles "auto-round
        // underside when ≥50m above terrain" vs "always stretch underside
        // down to terrain" (default false = stretch). v3 files load with
        // FloatingIslandMode=false so the existing visual is preserved.
        public const byte SCHEMA_VERSION = 4;

        public static byte[] Encode(TileData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Heights == null || data.Heights.Length != VERTEX_COUNT)
                throw new ArgumentException($"Heights must be length {VERTEX_COUNT}", nameof(data));
            if (data.HeightModified == null || data.HeightModified.Length != VERTEX_COUNT)
                throw new ArgumentException($"HeightModified must be length {VERTEX_COUNT}", nameof(data));
            if (data.Paint == null || data.Paint.Length != VERTEX_COUNT)
                throw new ArgumentException($"Paint must be length {VERTEX_COUNT}", nameof(data));
            if (data.PaintModified == null || data.PaintModified.Length != VERTEX_COUNT)
                throw new ArgumentException($"PaintModified must be length {VERTEX_COUNT}", nameof(data));

            using (var raw = new MemoryStream())
            {
                using (var gz = new GZipStream(raw, CompressionMode.Compress, leaveOpen: true))
                using (var w = new BinaryWriter(gz))
                {
                    w.Write(SCHEMA_VERSION);
                    w.Write(data.TileXZ.x);
                    w.Write(data.TileXZ.y);

                    WriteSparseHeights(w, data.Heights, data.HeightModified);
                    WriteSparsePaint(w, data.Paint, data.PaintModified);
                    WriteDiskList(w, data.Disks);
                    // v2: per-tile biome override. 0 = no override, otherwise
                    // a Heightmap.Biome value (Meadows=1, Plains=16, etc.).
                    w.Write((ushort)data.BiomeOverride);
                    // v3: per-vertex biome array, sparse (only modified verts).
                    WriteSparseBiome(w, data.Biome, data.BiomeModified);
                    // v4: FloatingIslandMode flag. 1 byte. Tail-appended so
                    // v3 readers can ignore it (they EOF before reaching here).
                    w.Write((byte)(data.FloatingIslandMode ? 1 : 0));
                }
                return raw.ToArray();
            }
        }

        public static TileData Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;

            using (var src = new MemoryStream(bytes, writable: false))
            using (var gz = new GZipStream(src, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                byte ver = r.ReadByte();
                if (ver < 1 || ver > SCHEMA_VERSION)
                {
                    throw new InvalidDataException($"Unsupported tile schema version: {ver}");
                }

                int tx = r.ReadInt32();
                int tz = r.ReadInt32();

                var data = new TileData
                {
                    TileXZ = new Vector2i(tx, tz),
                    Heights = new float[VERTEX_COUNT],
                    HeightModified = new bool[VERTEX_COUNT],
                    Paint = new Color32[VERTEX_COUNT],
                    PaintModified = new bool[VERTEX_COUNT],
                    Disks = new List<DiskRecord>(),
                    BiomeOverride = 0,                  // v1 default; v2 reads below
                    Biome = new ushort[VERTEX_COUNT],   // v3 default empty
                    BiomeModified = new bool[VERTEX_COUNT],
                };

                ReadSparseHeights(r, data.Heights, data.HeightModified);
                ReadSparsePaint(r, data.Paint, data.PaintModified);
                ReadDiskList(r, data.Disks);

                // v2+: per-tile biome override. v1 files leave it 0 (no override).
                if (ver >= 2)
                {
                    data.BiomeOverride = r.ReadUInt16();
                }

                // v3+: per-vertex biome override. v2 files leave Biome[] empty
                // (TerrainTile.Initialize promotes BiomeOverride into per-vertex
                // so the visual is preserved).
                if (ver >= 3)
                {
                    ReadSparseBiome(r, data.Biome, data.BiomeModified);
                }

                // v4+: FloatingIslandMode flag. v1-v3 files default to false
                // (existing "stretch underside to terrain" behavior).
                if (ver >= 4)
                {
                    try { data.FloatingIslandMode = r.ReadByte() != 0; }
                    catch (EndOfStreamException) { data.FloatingIslandMode = false; }
                }

                return data;
            }
        }

        private static void WriteSparseHeights(BinaryWriter w, float[] heights, bool[] modified)
        {
            int count = CountModified(modified);
            if (count > ushort.MaxValue)
                throw new InvalidDataException($"Modified height count {count} exceeds u16 range");
            w.Write((ushort)count);
            for (int i = 0; i < VERTEX_COUNT; i++)
            {
                if (!modified[i]) continue;
                w.Write((ushort)i);
                w.Write(heights[i]);
            }
        }

        private static void ReadSparseHeights(BinaryReader r, float[] heights, bool[] modified)
        {
            int count = r.ReadUInt16();
            for (int n = 0; n < count; n++)
            {
                int idx = r.ReadUInt16();
                float v = r.ReadSingle();
                if (idx < 0 || idx >= VERTEX_COUNT) continue; // defensive
                heights[idx] = v;
                modified[idx] = true;
            }
        }

        private static void WriteSparsePaint(BinaryWriter w, Color32[] paint, bool[] modified)
        {
            int count = CountModified(modified);
            if (count > ushort.MaxValue)
                throw new InvalidDataException($"Modified paint count {count} exceeds u16 range");
            w.Write((ushort)count);
            for (int i = 0; i < VERTEX_COUNT; i++)
            {
                if (!modified[i]) continue;
                w.Write((ushort)i);
                var c = paint[i];
                w.Write(c.r);
                w.Write(c.g);
                w.Write(c.b);
                w.Write(c.a);
            }
        }

        private static void ReadSparsePaint(BinaryReader r, Color32[] paint, bool[] modified)
        {
            int count = r.ReadUInt16();
            for (int n = 0; n < count; n++)
            {
                int idx = r.ReadUInt16();
                byte rr = r.ReadByte();
                byte gg = r.ReadByte();
                byte bb = r.ReadByte();
                byte aa = r.ReadByte();
                if (idx < 0 || idx >= VERTEX_COUNT) continue;
                paint[idx] = new Color32(rr, gg, bb, aa);
                modified[idx] = true;
            }
        }

        private static void WriteSparseBiome(BinaryWriter w, ushort[] biome, bool[] modified)
        {
            int count = CountModified(modified);
            if (count > ushort.MaxValue)
                throw new InvalidDataException($"Modified biome count {count} exceeds u16 range");
            w.Write((ushort)count);
            for (int i = 0; i < VERTEX_COUNT; i++)
            {
                if (!modified[i]) continue;
                w.Write((ushort)i);
                w.Write(biome[i]);
            }
        }

        private static void ReadSparseBiome(BinaryReader r, ushort[] biome, bool[] modified)
        {
            int count = r.ReadUInt16();
            for (int n = 0; n < count; n++)
            {
                int idx = r.ReadUInt16();
                ushort v = r.ReadUInt16();
                if (idx < 0 || idx >= VERTEX_COUNT) continue;
                biome[idx] = v;
                modified[idx] = true;
            }
        }

        private static void WriteDiskList(BinaryWriter w, List<DiskRecord> disks)
        {
            int count = disks?.Count ?? 0;
            if (count > ushort.MaxValue)
                throw new InvalidDataException($"Disk count {count} exceeds u16 range");
            w.Write((ushort)count);
            for (int i = 0; i < count; i++)
            {
                WriteDisk(w, disks[i]);
            }
        }

        private static void ReadDiskList(BinaryReader r, List<DiskRecord> disks)
        {
            int count = r.ReadUInt16();
            disks.Capacity = count;
            for (int i = 0; i < count; i++)
            {
                disks.Add(ReadDisk(r));
            }
        }

        private static void WriteDisk(BinaryWriter w, DiskRecord d)
        {
            w.Write(d.StableId);
            w.Write(d.Center.x);
            w.Write(d.Center.y);
            w.Write(d.Center.z);
            w.Write(d.Radius);
            w.Write(d.BaseHeight);
            w.Write(d.PaintColor.r);
            w.Write(d.PaintColor.g);
            w.Write(d.PaintColor.b);
            w.Write(d.PaintColor.a);
            w.Write(d.MaterialId);
            w.Write(d.Flags);
        }

        private static DiskRecord ReadDisk(BinaryReader r)
        {
            return new DiskRecord
            {
                StableId = r.ReadInt32(),
                Center = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                Radius = r.ReadSingle(),
                BaseHeight = r.ReadSingle(),
                PaintColor = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()),
                MaterialId = r.ReadInt32(),
                Flags = r.ReadUInt32(),
            };
        }

        private static int CountModified(bool[] modified)
        {
            int c = 0;
            for (int i = 0; i < modified.Length; i++)
            {
                if (modified[i]) c++;
            }
            return c;
        }
    }

    // Plain data object. Heights/Paint arrays are length VERTEX_COUNT (4225),
    // matching the 65x65 vertex grid of one 64m tile at 1m resolution.
    // HeightModified / PaintModified track which entries have been touched
    // (matches vanilla TerrainComp's m_modifiedHeight / m_modifiedPaint).
    // Unmodified entries are restored from the runtime baseline (vanilla
    // heightmap height + default paint) at mesh-rebuild time.
    public sealed class TileData
    {
        public Vector2i TileXZ;
        public float[] Heights;
        public bool[] HeightModified;
        public Color32[] Paint;
        public bool[] PaintModified;
        public List<DiskRecord> Disks;
        // v2: per-tile biome override. ushort, 0 = no override (vanilla
        // biome wins), otherwise a Heightmap.Biome enum value cast to
        // ushort. Kept as a fast "dominant biome" hint + the v2→v3
        // upgrade source (TerrainTile.Initialize promotes this into the
        // per-vertex Biome[] array when no per-vertex data exists).
        public ushort BiomeOverride;
        // v3: per-vertex biome override. Length = VERTEX_COUNT (4225).
        // Sparse-encoded on disk via WriteSparseBiome/ReadSparseBiome.
        // 0 entries fall back to vanilla biome at query time.
        public ushort[] Biome;
        public bool[] BiomeModified;
        // v4 (2026-05-26): per-tile floating-island mode toggle.
        // false (default, matches v3 saves) → underside stretches down to
        //   meet vanilla terrain no matter how high the top sits.
        // true → underside auto-rounds into a domed teardrop bottom when
        //   the tile's top sits ≥50m above local terrain. Below that
        //   threshold it still stretches like default mode (so a hill
        //   placed close to ground keeps its skirt). Set by the
        //   "Floor (Floating Island)" / "Raise (Floating Island)" tool
        //   variants at brush time.
        public bool FloatingIslandMode;
    }

    // Disk metadata for re-edit support and disk-level operations.
    // Height/paint grids are the authoritative rendering source — disk records
    // are denormalized across overlapping tiles for self-contained per-tile loads.
    public struct DiskRecord
    {
        public int StableId;
        public Vector3 Center;
        public float Radius;
        public float BaseHeight;
        public Color32 PaintColor;
        public int MaterialId;
        public uint Flags;
    }
}
