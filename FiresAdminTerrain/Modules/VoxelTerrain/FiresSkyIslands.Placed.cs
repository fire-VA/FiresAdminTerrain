using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Skylands laid down from blueprints (Fire, 2026-09-27, "Do the living refactor"; Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md).
    // A saved skyland is its registry record, and placing one adds that record again with an offset, so the copy is a real
    // skyland: its far view, water, falls, caves, biome and dressing rules all work, and it derives the same shape as the
    // island it was saved from because every field it derives from stays in that island's frame. ZoneModeSkylandBlueprint
    // carries the rest (the island's voxel edits and its objects).
    internal static partial class FiresSkyIslands
    {
        // How far above its top a skyland's captured space reaches, so a tower built on it travels whole.
        internal const float CaptureHeadroom = 48f;
        private const float CaptureBelow = 6f;
        private const float FrameShare = 2.2f;

        // The skyland whose space holds this point, when one does.
        internal static bool TrySkylandAt(Vector3 point, out int index)
        {
            index = -1;
            if (!Enabled) return false;
            for (int i = 0; i < s_islands.Count; i++)
            {
                if (!IsSkyland(s_islands[i].Kind) || !InSkylandCapture(i, point)) continue;
                index = i;
                return true;
            }
            return false;
        }

        // Everything a skyland blueprint takes with the island: its dressing's reach round the rim, down past its keel and
        // up to CaptureHeadroom over its top.
        internal static bool InSkylandCapture(int index, Vector3 point)
        {
            var isl = s_islands[index];
            point -= new Vector3(isl.OffX, isl.OffY, isl.OffZ);
            float dx = point.x - isl.X, dz = point.z - isl.Z;
            float reach = isl.R + DressingOverhang;
            if (dx * dx + dz * dz > reach * reach) return false;
            if (isl.Shape != null && isl.Shape.Sample(point.x, point.z, out float width) > 1f + 2f * DressingOverhang / Mathf.Max(1f, width))
                return false;
            return point.y < TopAt(in isl, point.x, point.z) + CaptureHeadroom && point.y > isl.Bot - CaptureBelow;
        }

        // The whole-metre box round a skyland's captured space, in the world.
        internal static void SkylandCaptureBox(int index, out Vector3Int min, out Vector3Int max)
        {
            var isl = s_islands[index];
            float reach = isl.R + DressingOverhang;
            float x = WorldX(in isl), z = WorldZ(in isl);
            min = new Vector3Int(Mathf.FloorToInt(x - reach), Mathf.FloorToInt(isl.Bot + isl.OffY - CaptureBelow), Mathf.FloorToInt(z - reach));
            max = new Vector3Int(Mathf.CeilToInt(x + reach), Mathf.CeilToInt(isl.Top + ReliefCeiling(in isl) + isl.OffY + CaptureHeadroom),
                Mathf.CeilToInt(z + reach));
        }

        // Where a camera should look to picture a skyland, and how wide a view takes it in.
        internal static void SkylandFrame(int index, out Vector3 focus, out float span)
        {
            focus = SkylandSummit(index);
            span = s_islands[index].R * FrameShare;
        }

        // The island's record as a blueprint keeps it: every stored field, the offset it stands at now, and the seeds and
        // biome it was grown with, so any world can raise it (TryPlaceRecord decides whether they are needed).
        internal static byte[] ExportRecord(int index)
        {
            var isl = s_islands[index];
            if (!isl.OwnSeeds)
            {
                isl.OwnSeeds = true;
                isl.NoiseSeed = isl.OwnShapeSeed = ShapeSeed;
                isl.FixedBiome = isl.Biome;
            }
            using (var stream = new MemoryStream(ForeignRecordBytes))
            using (var writer = new BinaryWriter(stream))
            {
                WriteIsland(writer, in isl, RegistryFormatForeign);
                writer.Flush();
                return stream.ToArray();
            }
        }

        // A record saved before the seeds travelled with it (73 bytes, FAT 0.2.573) is read as format 2.
        private static bool TryReadRecord(byte[] record, out Island isl)
        {
            isl = default;
            byte format = record == null ? RegistryFormat
                : record.Length == ForeignRecordBytes ? RegistryFormatForeign
                : record.Length == PlacedRecordBytes ? RegistryFormatPlaced
                : RegistryFormat;
            if (format == RegistryFormat) return false;
            using (var reader = new BinaryReader(new MemoryStream(record))) isl = ReadIsland(reader, format);
            return IsSkyland(isl.Kind);
        }

        // An island at home in this world is grown by this world's own generator, exactly as the one it was saved from
        // (caves included: where a cave mouth may open follows the home world's rivers, which only the home world knows).
        // From a world with another seed it keeps its home seeds and biome. A 0.2.573 record carries no seeds: the
        // blueprint's world seed stands in, and its biome is rolled here.
        private static void SettleSeeds(ref Island isl, int homeSeed)
        {
            if (!isl.OwnSeeds && homeSeed != ShapeSeed)
            {
                isl.OwnSeeds = true;
                isl.NoiseSeed = isl.OwnShapeSeed = homeSeed;
            }
            if (isl.OwnSeeds && isl.NoiseSeed == ShapeSeed && isl.OwnShapeSeed == ShapeSeed) isl.OwnSeeds = false;
        }

        // Where a saved record stands in the world: its centre, bounding radius and the lowest its keel reaches.
        internal static bool TryDescribeRecord(byte[] record, out Vector3 centre, out float radius, out float bottom)
        {
            centre = default;
            radius = bottom = 0f;
            if (!TryReadRecord(record, out var isl)) return false;
            centre = new Vector3(WorldX(in isl), isl.BaseY + isl.OffY, WorldZ(in isl));
            radius = isl.R;
            bottom = isl.Bot + isl.OffY;
            return true;
        }

        internal static bool TryIndexOf(uint id, out int index)
        {
            for (index = 0; index < s_islands.Count; index++)
                if (IslandHash(s_islands[index]) == id) return true;
            index = -1;
            return false;
        }

        internal static bool IsPlacedCopy(int index) => IsMoved(s_islands[index]);

        // Server: a saved skyland raised again `shift` whole metres from where it was saved, in this world or another.
        // Refused where it would crowd a skyland already in the sky, or where the very same copy already stands.
        internal static bool TryPlaceRecord(byte[] record, int homeSeed, Vector3Int shift, List<string> lines, out int index, out uint id,
            out string refusal)
        {
            index = -1;
            id = 0u;
            if (!TryReadRecord(record, out var isl))
            {
                refusal = "the saved skyland record could not be read.";
                return false;
            }
            SettleSeeds(ref isl, homeSeed);
            isl.OffX += shift.x;
            isl.OffY += shift.y;
            isl.OffZ += shift.z;
            id = IslandHash(in isl);
            if (TryIndexOf(id, out _))
            {
                refusal = "that skyland already stands exactly there.";
                return false;
            }
            if (OverlapsLive(in isl, null))
            {
                refusal = "it would crowd a skyland already in the sky there.";
                return false;
            }
            var world = ZNet.World;
            if (world == null)
            {
                refusal = "no world is loaded.";
                return false;
            }
            var before = new List<Island>(s_islands);
            AddIsland(in isl);
            index = s_islands.Count - 1;
            s_any = true;
            s_seeded = true;
            SaveAndPublish(world, before, lines);
            refusal = null;
            return true;
        }

        // Server: a placed copy taken out of the sky. Islands the world grew itself are not removed here.
        internal static bool TryRemovePlaced(uint id, List<string> lines, out string refusal)
        {
            if (!TryIndexOf(id, out int index))
            {
                refusal = "that skyland is no longer in the sky.";
                return false;
            }
            if (!IsMoved(s_islands[index]))
            {
                refusal = "only a skyland placed from a blueprint can be taken back.";
                return false;
            }
            var world = ZNet.World;
            if (world == null)
            {
                refusal = "no world is loaded.";
                return false;
            }
            var before = new List<Island>(s_islands);
            var next = new List<Island>(s_islands);
            next.RemoveAt(index);
            Reinstall(next, ClusterCount);
            SaveAndPublish(world, before, lines);
            refusal = null;
            return true;
        }
    }
}
