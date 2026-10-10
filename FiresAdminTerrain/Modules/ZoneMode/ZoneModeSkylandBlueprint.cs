using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // A SKYLAND in a file (Fire, 2026-09-27, by popup: "Do the living refactor"; Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md "Save
    // skyland"). It holds the island's registry record, its voxel edits as brush bounds, and every object in its space,
    // builds and felled trees included. Saved as FiresWorldGen\skylands\<name>.skybp.
    //
    // Placing adds the record again as a new skyland standing somewhere else (FiresSkyIslands.Placed.cs), so the copy is a
    // real skyland that re-derives the same shape, waters, caves, biome and falls. The edits and objects follow it by the
    // same whole-metre shift, and the copy is recorded as dressed so the dressing pass does not dress it a second time.
    // A living skyland derives from its world, so one is placed only in a world with the seed it was saved in.
    internal static class ZoneModeSkylandBlueprint
    {
        internal const string Extension = ".skybp";
        private const string FolderName = "skylands";
        private const uint FileMagic = 0x42594B53u;
        private const byte FileFormat = 1;
        private const float BytesPerKilobyte = 1024f;
        private const int BytesPerEdit = 3 * sizeof(int) + 2 * sizeof(sbyte);
        private const string CompilerPrefabName = "_TerrainCompiler";
        private const float GroundClearance = 30f;
        private const int GroundRingSamples = 16;
        private static readonly float[] GroundRingShares = { 0.5f, 0.95f };

        private static readonly int CompilerPrefabHash = CompilerPrefabName.GetStableHashCode();

        internal struct Skyland
        {
            internal string World;
            internal int Seed;
            internal long CreatedUtcTicks;
            internal global::Version.World WorldVersion;
            internal byte[] Record;
            internal int Pieces;
            internal int Objects;
            internal int Portals;
            internal List<VoxelWorld.EditBound> Edits;
        }

        internal static string Folder => Path.Combine(FiresWorldRecords.Folder, FolderName);

        internal static string PathFor(string name) => Path.Combine(Folder, FiresWorldRecords.SafeName(name) + Extension);

        internal static IEnumerable<string> Names()
        {
            if (!Directory.Exists(Folder)) yield break;
            foreach (string path in Directory.GetFiles(Folder, "*" + Extension)) yield return Path.GetFileNameWithoutExtension(path);
        }

        // ── Saving ──────────────────────────────────────────────────────────
        // `aim` is any point in the skyland's space; `snapshot` the saving client's own picture of it, or null.
        internal static bool TrySave(Vector3 aim, string name, bool overwrite, byte[] snapshot, out string report)
        {
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            if (!ZoneModeChunkBlueprint.IsUsableName(name, out report)) return false;
            if (!FiresSkyIslands.TrySkylandAt(aim, out int index))
            {
                report = "There is no skyland there. Aim at the skyland itself, its land or something standing on it.";
                return false;
            }
            string path = PathFor(name);
            if (!overwrite && File.Exists(path))
            {
                report = $"'{name}' already exists. Tick 'Replace' to save over it.";
                return false;
            }
            if (!TryCapture(index, path, out var saved, out report)) return false;
            ZoneModeBlueprintIcons.Delete(path);
            bool snapped = ZoneModeBlueprintIcons.IsAcceptableSnapshot(snapshot);
            if (snapped) ZoneModeBlueprintIcons.Write(path, ZoneModeBlueprintIcons.Kind.Snapshot, snapshot);
            FiresSkyIslands.IslandFootprint(index, out var centre, out float radius);
            report = $"Saved skyland '{name}': the {FiresSkyIslands.SkylandBiome(index)} skyland at ({centre.x:0},{centre.z:0}), "
                + $"{radius:0} m across its widest reach, {saved.Edits.Count} edited voxel(s), {saved.Objects + saved.Portals} object(s) "
                + $"({saved.Pieces} built piece(s)), {new FileInfo(path).Length / BytesPerKilobyte:0} KB" + (snapped ? ", pictured by a snapshot." : ".");
            return true;
        }

        private static bool TryCapture(int index, string path, out Skyland saved, out string problem)
        {
            problem = null;
            saved = new Skyland
            {
                World = ZNet.World?.m_name ?? "world",
                Seed = ZNet.World?.m_seed ?? 0,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                WorldVersion = global::Version.c_WorldVersion,
                Record = FiresSkyIslands.ExportRecord(index),
                Edits = new List<VoxelWorld.EditBound>(),
            };
            FiresSkyIslands.SkylandCaptureBox(index, out var min, out var max);
            VoxelWorld.CaptureEditBounds(min, max, (x, y, z) => FiresSkyIslands.InSkylandCapture(index, new Vector3(x, y, z)), saved.Edits);
            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            CollectObjects(index, objects, portals);
            saved.Objects = objects.Count;
            saved.Portals = portals.Count;
            foreach (var zdo in objects) if (ZoneModeBuilds.IsBuilt(zdo)) saved.Pieces++;
            foreach (var zdo in portals) if (ZoneModeBuilds.IsBuilt(zdo)) saved.Pieces++;
            objects.AddRange(portals);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Write(path, saved, objects);
            }
            catch (Exception ex)
            {
                problem = $"The skyland could not be written ({ex.GetType().Name}: {ex.Message}).";
                return false;
            }
            return true;
        }

        // Every persistent object in the skyland's space, except the land's own bookkeeping: voxel anchors (the land travels
        // in the edits) and vanilla's terrain compilers. Portals stay a list of their own and go back after everything else.
        private static void CollectObjects(int index, List<ZDO> objects, List<ZDO> portals)
        {
            var zones = new List<Vector2s>();
            FiresSkyIslands.SkylandZones(index, zones, FiresSkyIslands.DressingOverhang);
            var inObjects = new List<ZDO>();
            var inPortals = new List<ZDO>();
            foreach (var zone in zones) ZoneModePark.CollectZoneObjects(zone, inObjects, inPortals);
            var seen = new HashSet<ZDOID>();
            Keep(index, inPortals, portals, seen);
            Keep(index, inObjects, objects, seen);
        }

        private static void Keep(int index, List<ZDO> from, List<ZDO> into, HashSet<ZDOID> seen)
        {
            foreach (var zdo in from)
            {
                int prefab = zdo.GetPrefab();
                if (prefab == VoxelAnchorPrefab.PrefabHash || prefab == CompilerPrefabHash) continue;
                if (FiresSkyIslands.InSkylandCapture(index, zdo.GetPosition()) && seen.Add(zdo.m_uid)) into.Add(zdo);
            }
        }

        // ── Placing ─────────────────────────────────────────────────────────
        // The saved skyland's centre goes over the spot the admin aims at, at the height it was saved at, raised when the
        // ground there stands too high for its keel.
        internal static bool TryPlace(string name, Vector3 target, out string report)
        {
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            if (!FiresVoxelConfig.StreamerRuns || !FiresVoxelConfig.BuildsSkyIslands)
            {
                report = "This world has no voxel sky islands, so a skyland cannot be raised here.";
                return false;
            }
            string path = PathFor(name);
            if (!File.Exists(path))
            {
                report = $"There is no skyland blueprint called '{name}'.";
                return false;
            }
            string what = $"skyland '{name}'";
            var objects = new List<ZDO>();
            if (!TryRead(path, objects, out var saved, out report, what)) return false;
            if (!FiresSkyIslands.TryDescribeRecord(saved.Record, out var centre, out _, out _))
            {
                Release(objects);
                report = $"{what} holds no skyland record this build can read. Nothing was placed.";
                return false;
            }
            var shift = new Vector3Int(Mathf.RoundToInt(target.x - centre.x), 0, Mathf.RoundToInt(target.z - centre.z));
            shift.y = Lift(saved.Record, shift);
            if (!TryApply(saved, objects, shift, what, out uint id, out report)) return false;
            ZoneModeChunkBlueprint.RecordSkylandPlacement(id, what);
            return true;
        }

        // How far a saved skyland must rise, shifted this far, for its keel to clear the ground under it as a grown one does.
        private static int Lift(byte[] record, Vector3Int shift)
        {
            FiresSkyIslands.TryDescribeRecord(record, out var centre, out float radius, out float bottom);
            float x = centre.x + shift.x, z = centre.z + shift.z;
            float ground = PlannedGround(x, z);
            foreach (float share in GroundRingShares)
                for (int k = 0; k < GroundRingSamples; k++)
                {
                    float angle = k * Mathf.PI * 2f / GroundRingSamples;
                    ground = Mathf.Max(ground, PlannedGround(x + Mathf.Cos(angle) * radius * share, z + Mathf.Sin(angle) * radius * share));
                }
            float needed = ground + GroundClearance - bottom;
            return needed > 0f ? Mathf.CeilToInt(needed) : 0;
        }

        // The ground the world plans, read in a planning scope: the zone gate shows closed land as sea.
        private static float PlannedGround(float x, float z)
        {
            var generator = WorldGenerator.instance;
            if (generator == null) return FiresWaterfalls.SeaLevel;
            ZoneModeGate.EnterPlanning();
            try { return generator.GetHeight(x, z); }
            finally { ZoneModeGate.ExitPlanning(); }
        }

        private static bool TryApply(Skyland saved, List<ZDO> objects, Vector3Int shift, string what, out uint id, out string report)
        {
            var lines = new List<string>();
            if (!FiresSkyIslands.TryPlaceRecord(saved.Record, saved.Seed, shift, lines, out int index, out id, out string refusal))
            {
                Release(objects);
                report = $"{what} was not placed: {refusal}";
                return false;
            }
            int worldBefore = ZDOMan.instance.NrOfObjects();
            int touched = VoxelWorld.StampEditBounds(saved.Edits, shift, false);
            var move = new Vector3(shift.x, shift.y, shift.z);
            if (move.sqrMagnitude > 0f)
                foreach (var zdo in objects) zdo.SetPosition(zdo.GetPosition() + move);
            ZoneModePark.AdoptObjects(objects, saved.Objects);
            ZoneModeSkylands.MarkDressed(id, FiresSkyIslands.SkylandBiome(index));
            FiresSkyIslands.IslandFootprint(index, out var centre, out _);
            FiresLogger.LogInfo($"[FiresZones] {what} raised at ({centre.x:0},{centre.y:0},{centre.z:0}) as skyland {id:x8}: world "
                + $"{worldBefore:N0} -> {ZDOMan.instance.NrOfObjects():N0} object(s), {objects.Count:N0} placed, {touched} chunk(s) of edits. "
                + string.Join(" ", lines));
            string height = shift.y == 0 ? "at the height it was saved at" : $"{shift.y} m higher than it was saved, to clear the ground";
            report = $"Raised {what} at ({centre.x:0},{centre.z:0}) {height}, with {objects.Count} object(s) and {saved.Edits.Count} edited voxel(s).";
            return true;
        }

        // ── Undo and redo ───────────────────────────────────────────────────
        // A placed skyland taken back whole: kept in a file first, so redo raises it again exactly where it stood.
        internal static bool TryTakeBack(uint id, string what, out string kept, out string report)
        {
            kept = null;
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            if (!FiresSkyIslands.TryIndexOf(id, out int index))
            {
                report = $"{what} is no longer in the sky.";
                return false;
            }
            string path = ZoneModeChunkBlueprint.NewUndoPath(Extension);
            if (!TryCapture(index, path, out var saved, out report)) return false;
            int removed = ClearObjects(index);
            int touched = VoxelWorld.StampEditBounds(saved.Edits, Vector3Int.zero, true);
            var lines = new List<string>();
            if (!FiresSkyIslands.TryRemovePlaced(id, lines, out string refusal))
            {
                report = $"{what} could not be taken out of the sky: {refusal}";
                return false;
            }
            kept = path;
            FiresLogger.LogInfo($"[FiresZones] {what} ({id:x8}) taken back: {removed} object(s) removed, {touched} chunk(s) of edits cleared. "
                + string.Join(" ", lines));
            report = $"{what} is gone from the sky, with the {removed} object(s) on it.";
            return true;
        }

        internal static bool TryPutBack(string path, string what, out uint id, out string report)
        {
            id = 0u;
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            var objects = new List<ZDO>();
            if (!TryRead(path, objects, out var saved, out report, what)) return false;
            return TryApply(saved, objects, Vector3Int.zero, what, out id, out report);
        }

        // What stands in the skyland's space goes, the way vanilla removes an object, so every peer drops it. The chunk
        // world's protected pieces stay.
        private static int ClearObjects(int index)
        {
            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            CollectObjects(index, objects, portals);
            objects.AddRange(portals);
            long self = ZDOMan.GetSessionID();
            int removed = 0;
            foreach (var zdo in objects)
            {
                if (ZoneModeChunkMachines.IsProtected(zdo)) continue;
                if (!zdo.IsOwner()) zdo.SetOwner(self);
                ZDOMan.instance.DestroyZDO(zdo);
                removed++;
            }
            return removed;
        }

        // ── File ────────────────────────────────────────────────────────────
        private static void Write(string path, Skyland saved, List<ZDO> objects)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                var pkg = new ZPackage();
                pkg.SetWriter(writer);
                pkg.Write(FileMagic);
                pkg.Write(FileFormat);
                pkg.Write(saved.World);
                pkg.Write(saved.Seed);
                pkg.Write((short)saved.WorldVersion);
                pkg.Write(saved.CreatedUtcTicks);
                pkg.Write(saved.Record);
                pkg.Write(saved.Pieces);
                pkg.Write(saved.Objects);
                pkg.Write(saved.Portals);
                pkg.Write(saved.Edits.Count);
                pkg.Write(PackEdits(saved.Edits));
                ZDOExtraData.PrepareSave();
                try { foreach (var zdo in objects) zdo.Save(pkg); }
                finally { ZDOExtraData.ClearSave(); }
            }
        }

        private static Skyland ReadHead(ZPackage pkg)
        {
            if (pkg.ReadUInt() != FileMagic) throw new InvalidDataException("not a skyland blueprint");
            byte format = pkg.ReadByte();
            if (format != FileFormat) throw new InvalidDataException($"skyland format {format}, this build reads {FileFormat}");
            return new Skyland
            {
                World = pkg.ReadString(),
                Seed = pkg.ReadInt(),
                WorldVersion = (global::Version.World)pkg.ReadShort(),
                CreatedUtcTicks = pkg.ReadLong(),
                Record = pkg.ReadByteArray(),
                Pieces = pkg.ReadInt(),
                Objects = pkg.ReadInt(),
                Portals = pkg.ReadInt(),
            };
        }

        // The whole file. A skyland saved in a world with another seed is raised too (Fire, 2026-09-27: "Make them work
        // anywhere"): its record carries its home world's seeds and biome (FiresSkyIslands.TryPlaceRecord).
        private static bool TryRead(string path, List<ZDO> objects, out Skyland saved, out string report, string what)
        {
            saved = default;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    var pkg = new ZPackage();
                    pkg.SetReader(reader);
                    saved = ReadHead(pkg);
                    int edits = pkg.ReadInt();
                    saved.Edits = UnpackEdits(pkg.ReadByteArray(), edits);
                    for (int i = 0; i < saved.Objects + saved.Portals; i++)
                    {
                        var zdo = ZDOPool.Create();
                        objects.Add(zdo);
                        zdo.Load(pkg, saved.WorldVersion);
                    }
                }
            }
            catch (Exception ex)
            {
                Release(objects);
                report = $"{what} could not be read ({ex.GetType().Name}: {ex.Message}). Nothing was placed.";
                return false;
            }
            report = null;
            return true;
        }

        private static void Release(List<ZDO> objects)
        {
            foreach (var zdo in objects) ZDOPool.Release(zdo);
            objects.Clear();
        }

        // One line per saved skyland, for the console list.
        internal static IEnumerable<string> List()
        {
            foreach (string name in Names())
            {
                string path = PathFor(name);
                string line;
                try
                {
                    Skyland saved;
                    using (var reader = new BinaryReader(File.OpenRead(path)))
                    {
                        var pkg = new ZPackage();
                        pkg.SetReader(reader);
                        saved = ReadHead(pkg);
                    }
                    FiresSkyIslands.TryDescribeRecord(saved.Record, out var centre, out float radius, out _);
                    line = $"{name}: skyland of '{saved.World}' saved at ({centre.x:0},{centre.y:0},{centre.z:0}), {radius:0} m reach, "
                        + $"{saved.Objects + saved.Portals} object(s), {saved.Pieces} built piece(s), {new FileInfo(path).Length / BytesPerKilobyte:0} KB";
                }
                catch (Exception ex) { line = $"{name}: unreadable ({ex.Message})"; }
                yield return line;
            }
        }

        private static byte[] PackEdits(List<VoxelWorld.EditBound> edits)
        {
            var bytes = new byte[edits.Count * BytesPerEdit];
            int o = 0;
            foreach (var edit in edits)
            {
                WriteInt(bytes, ref o, edit.X);
                WriteInt(bytes, ref o, edit.Y);
                WriteInt(bytes, ref o, edit.Z);
                bytes[o++] = unchecked((byte)edit.Lo);
                bytes[o++] = unchecked((byte)edit.Hi);
            }
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress)) deflate.Write(bytes, 0, bytes.Length);
                return output.ToArray();
            }
        }

        private static List<VoxelWorld.EditBound> UnpackEdits(byte[] packed, int count)
        {
            var bytes = new byte[count * BytesPerEdit];
            using (var input = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                int read = 0;
                while (read < bytes.Length)
                {
                    int got = input.Read(bytes, read, bytes.Length - read);
                    if (got <= 0) throw new InvalidDataException($"the edits end after {read / BytesPerEdit} of {count}");
                    read += got;
                }
            }
            var edits = new List<VoxelWorld.EditBound>(count);
            int o = 0;
            for (int i = 0; i < count; i++)
            {
                var edit = new VoxelWorld.EditBound { X = ReadInt(bytes, ref o), Y = ReadInt(bytes, ref o), Z = ReadInt(bytes, ref o) };
                edit.Lo = unchecked((sbyte)bytes[o++]);
                edit.Hi = unchecked((sbyte)bytes[o++]);
                edits.Add(edit);
            }
            return edits;
        }

        private static void WriteInt(byte[] into, ref int at, int value)
        {
            BitConverter.GetBytes(value).CopyTo(into, at);
            at += sizeof(int);
        }

        private static int ReadInt(byte[] from, ref int at)
        {
            int value = BitConverter.ToInt32(from, at);
            at += sizeof(int);
            return value;
        }
    }
}
