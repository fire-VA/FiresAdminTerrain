using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Ground a world's generated water keeps off: where its builds and placed locations stood the first time the world
    // loaded with its rivers and lakes on (a converted world: before anything is parked; parked zones count too). The
    // server freezes it on the hydrology grid as FiresWorldGen\firesgen_<world>_waterguards.bin and sends it in FAT's
    // handshake, so every peer plans the same water. The solve keeps channels, bowls, still water and springs off every
    // guarded cell; a river's carve closes back into the ground before one and opens again after it (VoxelRiverNetwork).
    internal static class FiresWaterGuards
    {
        internal sealed class ClientState
        {
            internal byte[] Packed;
            internal int Builds;
            internal int Locations;
        }

        internal const string FileSuffix = "_waterguards.bin";
        private const uint FileMagic = 0x44475746u;
        private const byte FileFormat = 1;
        private const float BuildMargin = 16f;
        private const float LocationMargin = 8f;
        private const uint HashOffset = 2166136261u;
        private const uint HashPrime = 16777619u;

        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsByIdRef =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        private static bool[] s_mask;
        private static byte[] s_packed;
        private static bool s_known;
        private static int s_cells;
        private static int s_builds;
        private static int s_locations;
        private static int s_hash;

        private static int Res => VoxelHydrology.GridRes;
        private static float Radius => VoxelHydrology.GridRadius;
        private static float Cell => VoxelHydrology.GridRadius * 2f / VoxelHydrology.GridRes;

        internal static bool Known => s_known;

        internal static int Hash => s_hash;

        internal static bool IsGuarded(int index)
        {
            var mask = s_mask;
            return mask != null && mask[index];
        }

        internal static string Describe()
            => !s_known ? "not known" : $"{s_cells} cell(s) around {s_builds} built piece(s) and {s_locations} location(s)";

        internal static void Reset()
        {
            s_mask = null;
            s_packed = null;
            s_known = false;
            s_cells = s_builds = s_locations = 0;
            s_hash = 0;
        }

        // Server: the world's frozen guards, when it has them. Clients take theirs from the handshake.
        internal static void OnWorldInitialize(World world, bool server)
        {
            Reset();
            if (world == null || world.m_menu || !server) return;
            TryLoad(world);
        }

        // Server, once the save's objects are loaded and before anything is parked: a world with no guards yet gets them
        // from what stands in it now, parked zones included, and keeps them.
        internal static void OnWorldLoaded()
        {
            var net = ZNet.instance;
            var world = ZNet.World;
            if (s_known || net == null || !net.IsServer() || world == null || ZDOMan.instance == null || ZoneSystem.instance == null) return;
            if (FiresWorldProfile.Current?.Hydrology == false) return;
            var clock = Stopwatch.StartNew();
            var mask = new bool[Res * Res];
            int builds = 0;
            foreach (var zdo in ObjectsByIdRef(ZDOMan.instance).Values)
            {
                if (!Zones.ZoneModeBuilds.IsBuilt(zdo)) continue;
                Mark(mask, zdo.GetPosition(), BuildMargin);
                builds++;
            }
            builds += Zones.ZoneModePark.ForEachParkedObject(zdo =>
            {
                if (!Zones.ZoneModeBuilds.IsBuilt(zdo)) return false;
                Mark(mask, zdo.GetPosition(), BuildMargin);
                return true;
            });
            int locations = 0;
            foreach (var instance in ZoneSystem.instance.m_locationInstances.Values)
            {
                if (!instance.m_placed || instance.m_location == null) continue;
                Mark(mask, instance.m_position, instance.m_location.m_exteriorRadius + LocationMargin);
                locations++;
            }
            Adopt(Pack(mask), builds, locations);
            Save(world);
            FiresLogger.LogInfo($"[FiresVoxel] water guards frozen for '{world.m_name}': {Describe()} ({clock.ElapsedMilliseconds} ms). "
                + "Generated rivers, lakes, ponds and springs keep off them for good.");
        }

        // Every hydrology cell a disc of `radius` around the point reaches. The grid is the source land's, so a point in a
        // zone opened on other land marks where that land came from.
        private static void Mark(bool[] mask, Vector3 point, float radius)
        {
            Zones.ZoneModeGate.TranslateToSource(ref point.x, ref point.z);
            float cell = Cell;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((point.x - radius + Radius) / cell));
            int x1 = Mathf.Min(Res - 1, Mathf.FloorToInt((point.x + radius + Radius) / cell));
            int z0 = Mathf.Max(0, Mathf.FloorToInt((point.z - radius + Radius) / cell));
            int z1 = Mathf.Min(Res - 1, Mathf.FloorToInt((point.z + radius + Radius) / cell));
            float radius2 = radius * radius;
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (DistanceSquaredToCell(point.x, point.z, x, z) > radius2) continue;
                    mask[z * Res + x] = true;
                }
            }
        }

        private static float DistanceSquaredToCell(float wx, float wz, int x, int z)
        {
            float cell = Cell;
            float minX = -Radius + x * cell, minZ = -Radius + z * cell;
            float dx = Mathf.Max(0f, Mathf.Max(minX - wx, wx - (minX + cell)));
            float dz = Mathf.Max(0f, Mathf.Max(minZ - wz, wz - (minZ + cell)));
            return dx * dx + dz * dz;
        }

        // How far a disc of `radius` around the point may reach before it touches a guarded cell: `radius` when clear,
        // 0 when the point's own cell is guarded.
        internal static float RoomAround(float wx, float wz, float radius)
        {
            var mask = s_mask;
            if (mask == null || s_cells == 0) return radius;
            float cell = Cell;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((wx - radius + Radius) / cell));
            int x1 = Mathf.Min(Res - 1, Mathf.FloorToInt((wx + radius + Radius) / cell));
            int z0 = Mathf.Max(0, Mathf.FloorToInt((wz - radius + Radius) / cell));
            int z1 = Mathf.Min(Res - 1, Mathf.FloorToInt((wz + radius + Radius) / cell));
            float room2 = radius * radius;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    if (mask[z * Res + x]) room2 = Mathf.Min(room2, DistanceSquaredToCell(wx, wz, x, z));
            return Mathf.Sqrt(room2);
        }

        // ── Packing: one bit per cell, deflated ─────────────────────────────
        private static byte[] Pack(bool[] mask)
        {
            var bits = new byte[(mask.Length + 7) / 8];
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) bits[i >> 3] |= (byte)(1 << (i & 7));
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress)) deflate.Write(bits, 0, bits.Length);
                return output.ToArray();
            }
        }

        private static bool[] Unpack(byte[] packed)
        {
            var bits = new byte[(Res * Res + 7) / 8];
            using (var input = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                int read = 0;
                while (read < bits.Length)
                {
                    int got = input.Read(bits, read, bits.Length - read);
                    if (got <= 0) throw new InvalidDataException($"water guards end after {read} of {bits.Length} bytes");
                    read += got;
                }
            }
            var mask = new bool[Res * Res];
            for (int i = 0; i < mask.Length; i++) mask[i] = (bits[i >> 3] & (1 << (i & 7))) != 0;
            return mask;
        }

        private static void Adopt(byte[] packed, int builds, int locations)
        {
            s_mask = Unpack(packed);
            s_packed = packed;
            s_builds = builds;
            s_locations = locations;
            s_cells = 0;
            foreach (bool guarded in s_mask) if (guarded) s_cells++;
            uint hash = HashOffset;
            foreach (byte b in packed) hash = (hash ^ b) * HashPrime;
            s_hash = unchecked((int)hash);
            s_known = true;
        }

        // ── File: FiresWorldGen\firesgen_<world>_waterguards.bin ────────────
        private static void TryLoad(World world)
        {
            string path = FiresWorldRecords.PathFor(world.m_name, FileSuffix);
            if (!File.Exists(path)) return;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.ReadUInt32() != FileMagic || reader.ReadByte() != FileFormat) throw new InvalidDataException("not a water guard file");
                    long uid = reader.ReadInt64();
                    if (uid != world.m_uid)
                    {
                        File.Delete(path);
                        FiresLogger.LogWarning($"[FiresVoxel] deleted {Path.GetFileName(path)}: it was frozen for a deleted world that had this name.");
                        return;
                    }
                    if (reader.ReadInt32() != Res) throw new InvalidDataException("frozen on another hydrology grid");
                    int builds = reader.ReadInt32();
                    int locations = reader.ReadInt32();
                    Adopt(reader.ReadBytes(reader.ReadInt32()), builds, locations);
                }
                FiresLogger.LogInfo($"[FiresVoxel] water guards of '{world.m_name}': {Describe()}.");
            }
            catch (Exception ex)
            {
                Reset();
                FiresLogger.LogWarning($"[FiresVoxel] {Path.GetFileName(path)} is unreadable ({ex.Message}); it is frozen again from the world.");
            }
        }

        private static void Save(World world)
        {
            string path = FiresWorldRecords.PathFor(world.m_name, FileSuffix);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    writer.Write(FileMagic);
                    writer.Write(FileFormat);
                    writer.Write(world.m_uid);
                    writer.Write(Res);
                    writer.Write(s_builds);
                    writer.Write(s_locations);
                    writer.Write(s_packed.Length);
                    writer.Write(s_packed);
                }
            }
            catch (Exception ex) { FiresLogger.LogError($"[FiresVoxel] could not write {Path.GetFileName(path)}: {ex.Message}"); }
        }

        // ── Handshake ───────────────────────────────────────────────────────
        internal static void WriteHandshake(ZPackage pkg)
        {
            pkg.Write(s_known);
            if (!s_known) return;
            pkg.Write(s_builds);
            pkg.Write(s_locations);
            pkg.Write(s_packed);
        }

        internal static ClientState ReadHandshake(ZPackage pkg)
        {
            if (!pkg.ReadBool()) return null;
            return new ClientState { Builds = pkg.ReadInt(), Locations = pkg.ReadInt(), Packed = pkg.ReadByteArray() };
        }

        // A server without guards plans its water without them, so its clients do the same.
        internal static void BindClient(ClientState state)
        {
            if (state == null)
            {
                Adopt(Pack(new bool[Res * Res]), 0, 0);
                return;
            }
            try { Adopt(state.Packed, state.Builds, state.Locations); }
            catch (Exception ex)
            {
                Reset();
                FiresLogger.LogError($"[FiresVoxel] the server's water guards are unreadable ({ex.Message}); this client plans water without them.");
            }
        }
    }
}
