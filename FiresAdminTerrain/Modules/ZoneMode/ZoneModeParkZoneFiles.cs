using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VerdantsAscent.Terrain.Zones
{
    // The park as it was written before D6c: one file per zone slot (<x>_<y>.zdos parked, .restored, .override,
    // .unreadable) plus index.bin listing the parked zones. A world's first load on a pack build repacks these
    // (ZoneModeParkPack.TryRepack), reading them through here; if that repack cannot finish, the world keeps playing on
    // this store exactly as before and the next load tries again.
    internal sealed class ZoneModeParkZoneFiles : IParkStore
    {
        internal const string IndexFileName = "index.bin";
        private const uint IndexFileMagic = 0x58495046u;
        private const string TempSuffix = ".tmp";
        private const int FileBufferBytes = 1 << 20;

        private static readonly KeyValuePair<ParkSlot, string>[] SlotExtensions =
        {
            new KeyValuePair<ParkSlot, string>(ParkSlot.Parked, ".zdos"),
            new KeyValuePair<ParkSlot, string>(ParkSlot.Restored, ".restored"),
            new KeyValuePair<ParkSlot, string>(ParkSlot.Overridden, ".override"),
            new KeyValuePair<ParkSlot, string>(ParkSlot.Unreadable, ".unreadable"),
        };

        private Dictionary<Vector2s, ZoneModePark.ParkedZone> _index;

        public string Folder { get; }
        internal long WorldUid { get; }
        internal IReadOnlyDictionary<Vector2s, ZoneModePark.ParkedZone> Index => _index;

        private ZoneModeParkZoneFiles(string folder, long worldUid, Dictionary<Vector2s, ZoneModePark.ParkedZone> index)
        {
            Folder = folder;
            WorldUid = worldUid;
            _index = index;
        }

        // Null, with `foreign` saying why, when the folder belongs to a deleted world that had this name. An unreadable
        // index is rebuilt from the parked files and written again, as before.
        internal static ZoneModeParkZoneFiles Load(string folder, long worldUid, string worldName, out string foreign)
        {
            foreign = null;
            var index = new Dictionary<Vector2s, ZoneModePark.ParkedZone>();
            try
            {
                ReadIndex(folder, worldUid, index, out foreign);
                return foreign != null ? null : new ZoneModeParkZoneFiles(folder, worldUid, index);
            }
            catch (Exception ex)
            {
                index.Clear();
                if (!TryRebuildIndex(folder, worldUid, index, out foreign)) return null;
                FiresLogger.LogWarning($"[FiresZones] the park index of '{worldName}' was unreadable ({ex.Message}); rebuilt from its "
                    + $"{index.Count} parked zone file(s).");
                var files = new ZoneModeParkZoneFiles(folder, worldUid, index);
                files.Commit(index);
                return files;
            }
        }

        internal static bool TryParseZoneFile(string path, out Vector2s zone, out ParkSlot slot)
        {
            zone = default;
            slot = default;
            string extension = Path.GetExtension(path);
            bool known = false;
            foreach (var pair in SlotExtensions)
            {
                if (!string.Equals(pair.Value, extension, StringComparison.OrdinalIgnoreCase)) continue;
                slot = pair.Key;
                known = true;
                break;
            }
            if (!known) return false;
            string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return false;
            zone = new Vector2s(x, y);
            return true;
        }

        public string Describe() => $"{_index.Count:N0} parked zone file(s)";

        public IEnumerable<KeyValuePair<Vector2s, ZoneModePark.ParkedZone>> Parked() => _index;

        public List<Vector2s> ZonesIn(ParkSlot slot)
        {
            if (slot == ParkSlot.Parked) return new List<Vector2s>(_index.Keys);
            var zones = new List<Vector2s>();
            if (!Directory.Exists(Folder)) return zones;
            foreach (string path in Directory.GetFiles(Folder, "*" + ExtensionOf(slot)))
                if (TryParseZoneFile(path, out var zone, out _)) zones.Add(zone);
            return zones;
        }

        public bool Has(Vector2s zone, ParkSlot slot) => File.Exists(PathOf(zone, slot));

        public bool TryRead(Vector2s zone, ParkSlot slot, out byte[] payload, out string problem)
        {
            payload = null;
            problem = null;
            try
            {
                payload = File.ReadAllBytes(PathOf(zone, slot));
                return true;
            }
            catch (Exception ex)
            {
                problem = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        public IEnumerable<ParkRead> ReadAll(ParkSlot slot, ICollection<Vector2s> zones)
        {
            foreach (var zone in zones)
            {
                TryRead(zone, slot, out byte[] payload, out string problem);
                yield return new ParkRead { Zone = zone, Payload = payload, Problem = problem };
            }
        }

        public bool TryGetDetails(Vector2s zone, ParkSlot slot, out ZoneModePark.ParkedZone parked)
        {
            if (slot == ParkSlot.Parked && _index.TryGetValue(zone, out parked)) return true;
            parked = default;
            try
            {
                using (var stream = new FileStream(PathOf(zone, slot), FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferBytes))
                using (var reader = new BinaryReader(stream))
                {
                    var pkg = new ZPackage();
                    pkg.SetReader(reader);
                    parked = ZoneModePark.ReadHeader(pkg).Parked;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        public bool Move(Vector2s zone, ParkSlot from, ParkSlot to)
        {
            string source = PathOf(zone, from);
            string target = PathOf(zone, to);
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(source, target);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] could not rename {Path.GetFileName(source)} to {Path.GetFileName(target)}: {ex.Message}");
                return false;
            }
        }

        public void Remove(Vector2s zone, ParkSlot slot)
        {
            string path = PathOf(zone, slot);
            try { File.Delete(path); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] could not remove {Path.GetFileName(path)}: {ex.Message}"); }
        }

        public void Write(Vector2s zone, ParkSlot slot, ZoneModePark.ParkedZone parked, byte[] payload)
        {
            Directory.CreateDirectory(Folder);
            string path = PathOf(zone, slot);
            string temp = path + TempSuffix;
            File.WriteAllBytes(temp, payload);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public void Commit(IDictionary<Vector2s, ZoneModePark.ParkedZone> parked)
        {
            _index = new Dictionary<Vector2s, ZoneModePark.ParkedZone>(parked);
            try { WriteIndex(); }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] could not write the park index in {Path.GetFileName(Folder)}: {ex.Message}"); }
        }

        private void WriteIndex()
        {
            string path = Path.Combine(Folder, IndexFileName);
            string temp = path + TempSuffix;
            using (var writer = new BinaryWriter(File.Create(temp)))
            {
                writer.Write(IndexFileMagic);
                writer.Write(ZoneModePark.RecordFormat);
                writer.Write(WorldUid);
                writer.Write(_index.Count);
                foreach (var pair in _index)
                {
                    writer.Write((short)pair.Key.x);
                    writer.Write((short)pair.Key.y);
                    ZoneModePark.WriteParked(writer.Write, writer.Write, writer.Write, writer.Write, pair.Value);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static void ReadIndex(string folder, long worldUid, Dictionary<Vector2s, ZoneModePark.ParkedZone> index, out string foreign)
        {
            foreign = null;
            using (var reader = new BinaryReader(File.OpenRead(Path.Combine(folder, IndexFileName))))
            {
                if (reader.ReadUInt32() != IndexFileMagic) throw new InvalidDataException("not a park index");
                byte format = reader.ReadByte();
                if (format < ZoneModePark.FirstRecordFormat || format > ZoneModePark.RecordFormat)
                    throw new InvalidDataException($"park index format {format}, this build reads up to {ZoneModePark.RecordFormat}");
                long uid = reader.ReadInt64();
                if (uid != worldUid)
                {
                    foreign = $"it belongs to world uid {uid}, a deleted world that had this name";
                    return;
                }
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var zone = new Vector2s(reader.ReadInt16(), reader.ReadInt16());
                    index[zone] = ZoneModePark.ReadParked(reader.ReadInt32, reader.ReadInt16, reader.ReadSingle, reader.ReadBoolean, format);
                }
            }
        }

        private static bool TryRebuildIndex(string folder, long worldUid, Dictionary<Vector2s, ZoneModePark.ParkedZone> index, out string foreign)
        {
            foreign = null;
            foreach (string path in Directory.GetFiles(folder, "*" + ExtensionOf(ParkSlot.Parked)))
            {
                if (!TryParseZoneFile(path, out var zone, out _)) continue;
                ZoneModePark.ZoneFileHeader header;
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    var pkg = new ZPackage();
                    pkg.SetReader(reader);
                    header = ZoneModePark.ReadHeader(pkg);
                }
                if (header.WorldUid != worldUid)
                {
                    index.Clear();
                    foreign = $"its zones belong to world uid {header.WorldUid}, a deleted world that had this name";
                    return false;
                }
                index[zone] = header.Parked;
            }
            return true;
        }

        private string PathOf(Vector2s zone, ParkSlot slot)
            => Path.Combine(Folder, zone.x.ToString(CultureInfo.InvariantCulture) + "_" + zone.y.ToString(CultureInfo.InvariantCulture)
                + ExtensionOf(slot));

        private static string ExtensionOf(ParkSlot slot)
        {
            foreach (var pair in SlotExtensions)
                if (pair.Key == slot) return pair.Value;
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "no zone file extension for this slot");
        }
    }
}
