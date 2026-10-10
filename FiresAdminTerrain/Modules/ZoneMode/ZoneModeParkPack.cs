using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace VerdantsAscent.Terrain.Zones
{
    // D6c: a world's park as ONE file. Each zone's bytes (exactly the zone file ZoneModePark.WriteZone makes) are a frame
    // appended to zones.pack, and zones.index says which frame holds each zone's slot. Creating Va's 54,319 zone files took
    // 44.6 s (2026-09-27) and a cold scan of such a folder 10.5 s at a server's start (R9a); one sequential file and one
    // index read cost neither.
    //
    // A frame is never rewritten: a move between slots is an index change, and bytes no zone needs any more stay behind as
    // dead space. The index is replaced whole (a temp file, then File.Replace keeping the one before as .previous), so a
    // crash leaves an index that still points at valid frames; a frame appended after the last index write is harmless.
    // If both indexes are unreadable, the frames themselves are walked: each names its zone and the slot it was written to.
    internal sealed class ZoneModeParkPack : IParkStore
    {
        internal const string PackFileName = "zones.pack";
        internal const string IndexFileName = "zones.index";
        internal const string PackingSuffix = ".packing";
        internal const string ZoneFilesSuffix = ".zonefiles";
        private const string PreviousSuffix = ".previous";
        private const string TempSuffix = ".tmp";
        private const string AsideStampFormat = "yyyyMMdd-HHmmss";

        private const uint PackMagic = 0x4B415046u;
        private const uint FrameMagic = 0x525A5046u;
        private const uint IndexMagic = 0x495A5046u;
        private const byte PackFormat = 1;
        private const byte IndexFormat = 1;
        private const int PackHeaderBytes = sizeof(uint) + sizeof(byte) + sizeof(long);

        // A frame: magic, zone x, zone y, slot, payload length, payload CRC-32, then the payload.
        private const int FrameMagicAt = 0;
        private const int FrameXAt = FrameMagicAt + sizeof(uint);
        private const int FrameYAt = FrameXAt + sizeof(short);
        private const int FrameSlotAt = FrameYAt + sizeof(short);
        private const int FrameLengthAt = FrameSlotAt + sizeof(byte);
        private const int FrameCrcAt = FrameLengthAt + sizeof(int);
        internal const int FrameHeaderBytes = FrameCrcAt + sizeof(uint);

        private const int StreamBufferBytes = 1 << 20;
        private const double BytesPerMegabyte = 1024d * 1024d;
        private const int LoggedZoneSample = 8;

        // A repack writes a copy of everything it reads before the old files step aside: it starts only with that much free
        // space, plus a margin for the index, and never fills the disk to its last bytes.
        private const double RepackSpaceFactor = 1.1;
        private const long RepackReserveBytes = 256L << 20;

        internal struct Record
        {
            internal ParkSlot Slot;
            internal ZoneModePark.ParkedZone Parked;
            internal long Offset;
            internal int Length;
            internal uint Crc;
        }

        private readonly Dictionary<Vector2s, Record> _main = new Dictionary<Vector2s, Record>();
        private readonly Dictionary<Vector2s, Record> _overridden = new Dictionary<Vector2s, Record>();
        private readonly Dictionary<Vector2s, Record> _unreadable = new Dictionary<Vector2s, Record>();

        public string Folder { get; private set; }
        internal long WorldUid { get; }

        private string PackPath => Path.Combine(Folder, PackFileName);
        private string IndexPath => Path.Combine(Folder, IndexFileName);

        private ZoneModeParkPack(string folder, long worldUid)
        {
            Folder = folder;
            WorldUid = worldUid;
        }

        internal static bool IsPacked(string folder) => File.Exists(Path.Combine(folder, IndexFileName));

        // ── Opening ─────────────────────────────────────────────────────────
        // An empty pack in a folder that has no index yet (a set-aside on a world that never parked). A pack file already
        // there without an index is kept and appended to: its frames stay readable by a walk.
        internal static ZoneModeParkPack CreateEmpty(string folder, long worldUid)
        {
            Directory.CreateDirectory(folder);
            var pack = new ZoneModeParkPack(folder, worldUid);
            using (FrameStream.OpenForAppend(pack.PackPath, worldUid)) { }
            pack.WriteIndex();
            return pack;
        }

        // The frames a park wrote into `folder` (ZoneModeParkWriter), as a pack; the caller commits it.
        internal static ZoneModeParkPack FromWritten(string folder, long worldUid, List<KeyValuePair<Vector2s, Record>> written)
        {
            var pack = new ZoneModeParkPack(folder, worldUid);
            foreach (var pair in written) pack.MapOf(pair.Value.Slot)[pair.Key] = pair.Value;
            return pack;
        }

        // Null, with `setAside` saying why, when the folder belongs to a deleted world that had this name or nothing in it
        // can be read; the caller moves the folder aside and the world plays without a park.
        internal static ZoneModeParkPack Load(string folder, long worldUid, string worldName, out string setAside)
        {
            setAside = null;
            var pack = new ZoneModeParkPack(folder, worldUid);
            if (!pack.TryReadPackHeader(out long packUid, out string packProblem))
            {
                setAside = $"its {PackFileName} could not be read ({packProblem})";
                return null;
            }
            if (packUid != worldUid)
            {
                setAside = $"it belongs to world uid {packUid}, a deleted world that had this name";
                return null;
            }
            if (pack.TryReadIndex(pack.IndexPath, out string problem)) return pack.DropPastEnd(worldName);
            if (pack.TryReadIndex(pack.IndexPath + PreviousSuffix, out string previousProblem))
            {
                FiresLogger.LogWarning($"[FiresZones] the park index of '{worldName}' was unreadable ({problem}); read the one before it "
                    + "instead, so the last change to the park may be undone.");
                pack.WriteIndex();
                return pack.DropPastEnd(worldName);
            }
            if (pack.TryWalkFrames(out string walkProblem))
            {
                FiresLogger.LogWarning($"[FiresZones] both park indexes of '{worldName}' were unreadable ({problem}; {previousProblem}); "
                    + $"rebuilt from the {pack.CountAll():N0} zone record(s) in {PackFileName} as each was written"
                    + (walkProblem != null ? $" (the walk stopped early: {walkProblem})" : "") + ". A zone returned since its record "
                    + "was written reads as parked again.");
                pack.WriteIndex();
                return pack;
            }
            setAside = $"neither its index nor its {PackFileName} could be read ({problem}; {walkProblem})";
            return null;
        }

        // A repack that stopped between its two renames left the pack beside the folder instead of in it: finish it. One
        // that stopped earlier left only a partial copy beside the zone files: remove it, and it runs again.
        internal static void CompleteInterruptedRepack(string folder)
        {
            string staging = folder + PackingSuffix;
            if (!Directory.Exists(staging)) return;
            if (!Directory.Exists(folder) && File.Exists(Path.Combine(staging, IndexFileName)))
            {
                Directory.Move(staging, folder);
                FiresLogger.LogWarning($"[FiresZones] finished the repack of {Path.GetFileName(folder)} that stopped before its last rename.");
                return;
            }
            Directory.Delete(staging, true);
            FiresLogger.LogWarning($"[FiresZones] removed an unfinished repack of {Path.GetFileName(folder)}; it runs again now.");
        }

        // ── IParkStore ──────────────────────────────────────────────────────
        public string Describe()
        {
            long live = 0;
            foreach (var map in new[] { _main, _overridden, _unreadable })
                foreach (var record in map.Values) live += record.Length;
            long total = File.Exists(PackPath) ? new FileInfo(PackPath).Length : 0;
            return $"{PackFileName} ({total / BytesPerMegabyte:N0} MB, {live / BytesPerMegabyte:N0} MB in use)";
        }

        public IEnumerable<KeyValuePair<Vector2s, ZoneModePark.ParkedZone>> Parked()
        {
            foreach (var pair in _main)
                if (pair.Value.Slot == ParkSlot.Parked) yield return new KeyValuePair<Vector2s, ZoneModePark.ParkedZone>(pair.Key, pair.Value.Parked);
        }

        public List<Vector2s> ZonesIn(ParkSlot slot)
        {
            var zones = new List<Vector2s>();
            foreach (var pair in MapOf(slot))
                if (pair.Value.Slot == slot) zones.Add(pair.Key);
            return zones;
        }

        public bool Has(Vector2s zone, ParkSlot slot) => TryGet(zone, slot, out _);

        public bool TryRead(Vector2s zone, ParkSlot slot, out byte[] payload, out string problem)
        {
            payload = null;
            if (!TryGet(zone, slot, out var record))
            {
                problem = $"({zone.x},{zone.y}) has nothing in the {slot} slot of {PackFileName}";
                return false;
            }
            try
            {
                using (var reader = new FrameReader(PackPath)) return reader.TryRead(zone, record, out payload, out problem);
            }
            catch (Exception ex)
            {
                problem = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        public IEnumerable<ParkRead> ReadAll(ParkSlot slot, ICollection<Vector2s> zones)
        {
            var ordered = new List<KeyValuePair<Vector2s, Record>>(zones.Count);
            foreach (var zone in zones)
                if (TryGet(zone, slot, out var record)) ordered.Add(new KeyValuePair<Vector2s, Record>(zone, record));
            ordered.Sort((a, b) => a.Value.Offset.CompareTo(b.Value.Offset));
            if (ordered.Count == 0) yield break;
            using (var reader = new FrameReader(PackPath))
            {
                foreach (var pair in ordered)
                {
                    reader.TryRead(pair.Key, pair.Value, out byte[] payload, out string problem);
                    yield return new ParkRead { Zone = pair.Key, Payload = payload, Problem = problem };
                }
            }
        }

        public bool TryGetDetails(Vector2s zone, ParkSlot slot, out ZoneModePark.ParkedZone parked)
        {
            parked = default;
            if (!TryGet(zone, slot, out var record)) return false;
            parked = record.Parked;
            return true;
        }

        public bool Move(Vector2s zone, ParkSlot from, ParkSlot to)
        {
            if (!TryGet(zone, from, out var record)) return false;
            MapOf(from).Remove(zone);
            record.Slot = to;
            MapOf(to)[zone] = record;
            return true;
        }

        public void Remove(Vector2s zone, ParkSlot slot)
        {
            if (TryGet(zone, slot, out _)) MapOf(slot).Remove(zone);
        }

        public void Write(Vector2s zone, ParkSlot slot, ZoneModePark.ParkedZone parked, byte[] payload)
        {
            Record record;
            using (var frames = FrameStream.OpenForAppend(PackPath, WorldUid))
            {
                record = frames.Write(zone, slot, parked, payload);
                frames.Flush();
            }
            MapOf(slot)[zone] = record;
        }

        public void Commit(IDictionary<Vector2s, ZoneModePark.ParkedZone> parked)
        {
            try { WriteIndex(); }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] could not write {IndexFileName} in {Path.GetFileName(Folder)}: {ex.Message}"); }
        }

        internal void Rebase(string folder) => Folder = folder;

        internal int CountAll() => _main.Count + _overridden.Count + _unreadable.Count;

        private Dictionary<Vector2s, Record> MapOf(ParkSlot slot)
            => slot == ParkSlot.Overridden ? _overridden : slot == ParkSlot.Unreadable ? _unreadable : _main;

        private bool TryGet(Vector2s zone, ParkSlot slot, out Record record)
            => MapOf(slot).TryGetValue(zone, out record) && record.Slot == slot;

        // ── Index ───────────────────────────────────────────────────────────
        internal void WriteIndex()
        {
            string path = IndexPath;
            string temp = path + TempSuffix;
            long packLength = File.Exists(PackPath) ? new FileInfo(PackPath).Length : 0;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferBytes))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(IndexMagic);
                writer.Write(IndexFormat);
                writer.Write(WorldUid);
                writer.Write(packLength);
                writer.Write(CountAll());
                foreach (var map in new[] { _main, _overridden, _unreadable })
                {
                    foreach (var pair in map)
                    {
                        writer.Write((short)pair.Key.x);
                        writer.Write((short)pair.Key.y);
                        writer.Write((byte)pair.Value.Slot);
                        ZoneModePark.WriteParked(writer.Write, writer.Write, writer.Write, writer.Write, pair.Value.Parked);
                        writer.Write(pair.Value.Offset);
                        writer.Write(pair.Value.Length);
                        writer.Write(pair.Value.Crc);
                    }
                }
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + PreviousSuffix);
            else File.Move(temp, path);
        }

        private bool TryReadIndex(string path, out string problem)
        {
            ClearAll();
            try
            {
                using (var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferBytes)))
                {
                    if (reader.ReadUInt32() != IndexMagic) throw new InvalidDataException("not a park pack index");
                    byte format = reader.ReadByte();
                    if (format > IndexFormat) throw new InvalidDataException($"pack index format {format}, this build reads up to {IndexFormat}");
                    long uid = reader.ReadInt64();
                    if (uid != WorldUid) throw new InvalidDataException($"it is for world uid {uid}");
                    reader.ReadInt64();
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var zone = new Vector2s(reader.ReadInt16(), reader.ReadInt16());
                        var slot = (ParkSlot)reader.ReadByte();
                        if (slot < ParkSlot.Parked || slot > ParkSlot.Unreadable) throw new InvalidDataException($"slot {(byte)slot} for ({zone.x},{zone.y})");
                        var record = new Record
                        {
                            Slot = slot,
                            Parked = ZoneModePark.ReadParked(reader.ReadInt32, reader.ReadInt16, reader.ReadSingle, reader.ReadBoolean,
                                ZoneModePark.RecordFormat),
                            Offset = reader.ReadInt64(),
                            Length = reader.ReadInt32(),
                            Crc = reader.ReadUInt32(),
                        };
                        MapOf(slot)[zone] = record;
                    }
                }
                problem = null;
                return true;
            }
            catch (Exception ex)
            {
                ClearAll();
                problem = $"{Path.GetFileName(path)}: {ex.Message}";
                return false;
            }
        }

        // Records whose bytes lie past the end of the pack (a pack cut short on disk) cannot be read by anything: they are
        // named and dropped, and those zones open as new land.
        private ZoneModeParkPack DropPastEnd(string worldName)
        {
            long length = new FileInfo(PackPath).Length;
            var dropped = new List<string>();
            foreach (var map in new[] { _main, _overridden, _unreadable })
            {
                var past = new List<Vector2s>();
                foreach (var pair in map)
                    if (pair.Value.Offset + pair.Value.Length > length) past.Add(pair.Key);
                foreach (var zone in past)
                {
                    if (dropped.Count < LoggedZoneSample) dropped.Add($"({zone.x},{zone.y}) {map[zone].Slot}");
                    map.Remove(zone);
                }
            }
            if (dropped.Count == 0) return this;
            FiresLogger.LogError($"[FiresZones] {PackFileName} of '{worldName}' is shorter than its index says ({length:N0} bytes): "
                + $"{string.Join(", ", dropped.ToArray())} could not be kept and those zones open as new land.");
            WriteIndex();
            return this;
        }

        private bool TryWalkFrames(out string problem)
        {
            ClearAll();
            problem = null;
            try
            {
                using (var reader = new FrameReader(PackPath))
                {
                    long at = PackHeaderBytes;
                    while (at < reader.Length)
                    {
                        if (!reader.TryReadFrameAt(at, out var zone, out var record, out byte[] payload, out string frameProblem))
                        {
                            problem = $"at byte {at:N0}: {frameProblem}";
                            break;
                        }
                        if (ZoneModePark.TryReadRecordHeader(payload, out var parked, out long uid) && uid == WorldUid)
                        {
                            record.Parked = parked;
                            MapOf(record.Slot)[zone] = record;
                        }
                        at = record.Offset + record.Length;
                    }
                }
                return CountAll() > 0 || problem == null;
            }
            catch (Exception ex)
            {
                problem = $"{PackFileName}: {ex.Message}";
                ClearAll();
                return false;
            }
        }

        private bool TryReadPackHeader(out long worldUid, out string problem)
        {
            worldUid = 0;
            problem = null;
            try
            {
                using (var reader = new BinaryReader(new FileStream(PackPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                {
                    if (reader.ReadUInt32() != PackMagic) throw new InvalidDataException("not a park pack");
                    byte format = reader.ReadByte();
                    if (format > PackFormat) throw new InvalidDataException($"pack format {format}, this build reads up to {PackFormat}");
                    worldUid = reader.ReadInt64();
                }
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return false;
            }
        }

        private void ClearAll()
        {
            _main.Clear();
            _overridden.Clear();
            _unreadable.Clear();
        }

        // ── The one-time repack of a park kept as zone files ─────────────────
        // Every zone file is read once, written as a frame, and read back from the pack with its checksum: only when every
        // record reads back as the bytes that were read does the pack replace the files. The zone files then step aside
        // whole as <park>.zonefiles, nothing deleted. Anything the pack does not take (a file of another world, a second copy
        // of a zone) stays in that folder too. Null, logged, when it could not finish: the world plays on its zone files.
        internal static ZoneModeParkPack TryRepack(ZoneModeParkZoneFiles files, string worldName)
        {
            string folder = files.Folder;
            string staging = folder + PackingSuffix;
            var clock = Stopwatch.StartNew();
            var report = new RepackReport();
            ZoneModeParkPack repacked;
            string aside;
            try
            {
                var zoneFiles = new List<KeyValuePair<FileInfo, ParkSlot>>();
                var otherFiles = new List<FileInfo>();
                foreach (var file in new DirectoryInfo(folder).GetFiles())
                {
                    if (ZoneModeParkZoneFiles.TryParseZoneFile(file.Name, out _, out var slot))
                    {
                        zoneFiles.Add(new KeyValuePair<FileInfo, ParkSlot>(file, slot));
                        report.SourceBytes += file.Length;
                    }
                    else if (CarriesAcross(file.Name)) otherFiles.Add(file);
                }
                report.SourceFiles = zoneFiles.Count;
                zoneFiles.Sort((a, b) => a.Value != b.Value ? a.Value.CompareTo(b.Value) : string.CompareOrdinal(a.Key.Name, b.Key.Name));
                if (!HasRoomFor(folder, report.SourceBytes, out string room))
                {
                    FiresLogger.LogWarning($"[FiresZones] '{worldName}' keeps its park as zone files for now: repacking {zoneFiles.Count:N0} "
                        + $"file(s) ({report.SourceBytes / BytesPerMegabyte:N0} MB) needs {room}. It tries again at the next load.");
                    return null;
                }

                FiresLogger.LogInfo($"[FiresZones] repacking the park of '{worldName}': {zoneFiles.Count:N0} zone file(s) "
                    + $"({report.SourceBytes / BytesPerMegabyte:N0} MB) become one file. This happens once; on a cold disk it can take a minute.");
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                var pack = new ZoneModeParkPack(staging, files.WorldUid);
                var parkedWithFile = new HashSet<Vector2s>();
                using (var frames = FrameStream.Create(pack.PackPath, files.WorldUid))
                {
                    foreach (var pair in zoneFiles)
                    {
                        ZoneModeParkZoneFiles.TryParseZoneFile(pair.Key.Name, out var zone, out var slot);
                        long readStart = clock.ElapsedMilliseconds;
                        byte[] payload = File.ReadAllBytes(pair.Key.FullName);
                        report.ReadMs += clock.ElapsedMilliseconds - readStart;
                        report.BytesRead += payload.Length;
                        bool headerRead = ZoneModePark.TryReadRecordHeader(payload, out var parked, out long uid);
                        if (headerRead && uid != files.WorldUid)
                        {
                            report.Foreign++;
                            continue;
                        }
                        if (!headerRead) report.BadHeaders++;
                        if (slot == ParkSlot.Parked)
                        {
                            if (files.Index.TryGetValue(zone, out var indexed))
                            {
                                parked = indexed;
                                parkedWithFile.Add(zone);
                            }
                            else
                            {
                                slot = ParkSlot.Restored;
                                report.Orphans++;
                            }
                        }
                        if (pack.MapOf(slot).ContainsKey(zone))
                        {
                            report.Duplicates++;
                            continue;
                        }
                        pack.MapOf(slot)[zone] = frames.Write(zone, slot, parked, payload);
                        report.Count(slot);
                    }
                    frames.Flush();
                }
                foreach (var zone in files.Index.Keys)
                    if (!parkedWithFile.Contains(zone)) report.Missing.Add(zone);

                long verifyStart = clock.ElapsedMilliseconds;
                pack.VerifyEveryRecord();
                report.VerifyMs = clock.ElapsedMilliseconds - verifyStart;
                foreach (var file in otherFiles) file.CopyTo(Path.Combine(staging, file.Name));
                pack.WriteIndex();

                aside = AsideFolderFor(folder);
                Directory.Move(folder, aside);
                try { Directory.Move(staging, folder); }
                catch
                {
                    Directory.Move(aside, folder);
                    throw;
                }
                pack.Rebase(folder);
                repacked = pack;
            }
            catch (Exception ex)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch (Exception cleanup) { FiresLogger.LogWarning($"[FiresZones] could not remove {Path.GetFileName(staging)}: {cleanup.Message}"); }
                FiresLogger.LogError($"[FiresZones] could not repack the park of '{worldName}' ({ex.GetType().Name}: {ex.Message}). "
                    + "It keeps its zone files, unchanged, and plays as before; the next load tries again.");
                return null;
            }
            report.Log(worldName, aside, clock.ElapsedMilliseconds);
            return repacked;
        }

        private void VerifyEveryRecord()
        {
            using (var reader = new FrameReader(PackPath))
            {
                foreach (var map in new[] { _main, _overridden, _unreadable })
                    foreach (var pair in map)
                        if (!reader.TryRead(pair.Key, pair.Value, out _, out string problem))
                            throw new InvalidDataException($"({pair.Key.x},{pair.Key.y}) did not read back from the new pack: {problem}");
            }
        }

        // A file of the park folder that is neither a zone file, an index nor a pack of its own (landmarks.bin) moves into
        // the packed folder with it.
        private static bool CarriesAcross(string name)
            => !name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, ZoneModeParkZoneFiles.IndexFileName, StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith(PackFileName, StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith(IndexFileName, StringComparison.OrdinalIgnoreCase);

        private static bool HasRoomFor(string folder, long sourceBytes, out string needed)
        {
            long required = (long)(sourceBytes * RepackSpaceFactor) + RepackReserveBytes;
            needed = $"{required / BytesPerMegabyte:N0} MB free";
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)));
                if (drive.AvailableFreeSpace >= required) return true;
                needed += $" and the disk has {drive.AvailableFreeSpace / BytesPerMegabyte:N0} MB";
                return false;
            }
            catch (Exception) { return true; }
        }

        private static string AsideFolderFor(string folder)
        {
            string aside = folder + ZoneFilesSuffix;
            return Directory.Exists(aside) ? aside + "-" + DateTime.Now.ToString(AsideStampFormat, CultureInfo.InvariantCulture) : aside;
        }

        private sealed class RepackReport
        {
            internal int SourceFiles, Parked, Restored, Overridden, Unreadable, Orphans, Duplicates, Foreign, BadHeaders;
            internal long SourceBytes, BytesRead, ReadMs, VerifyMs;
            internal readonly List<Vector2s> Missing = new List<Vector2s>();

            internal void Count(ParkSlot slot)
            {
                if (slot == ParkSlot.Parked) Parked++;
                else if (slot == ParkSlot.Restored) Restored++;
                else if (slot == ParkSlot.Overridden) Overridden++;
                else Unreadable++;
            }

            internal void Log(string worldName, string aside, long totalMs)
            {
                FiresLogger.LogInfo($"[FiresZones] REPACKED the park of '{worldName}': {SourceFiles:N0} zone file(s) ({SourceBytes / BytesPerMegabyte:N0} MB) "
                    + $"became {PackFileName} in {totalMs:N0} ms (reading the files {ReadMs:N0} ms, read-back check {VerifyMs:N0} ms): "
                    + $"{Parked:N0} parked, {Restored:N0} restored, {Overridden:N0} set aside, {Unreadable:N0} unreadable; every record "
                    + $"read back as the bytes it was written from. The zone files are kept, unchanged, in {Path.GetFileName(aside)}.");
                var notes = new List<string>();
                if (Orphans > 0) notes.Add($"{Orphans:N0} zone file(s) the index had already let go (a return whose rename failed) are kept as restored, so a return that was never saved comes back");
                if (Missing.Count > 0) notes.Add($"{Missing.Count:N0} parked zone(s) had no file and open as new land: {Sample(Missing)}");
                if (Duplicates > 0) notes.Add($"{Duplicates:N0} second copy(ies) of a zone were left in {Path.GetFileName(aside)}");
                if (Foreign > 0) notes.Add($"{Foreign:N0} file(s) of another world were left in {Path.GetFileName(aside)}");
                if (BadHeaders > 0) notes.Add($"{BadHeaders:N0} file(s) have an unreadable header and were packed as they are");
                if (notes.Count > 0) FiresLogger.LogWarning($"[FiresZones] repack of '{worldName}': {string.Join("; ", notes.ToArray())}.");
            }

            private static string Sample(List<Vector2s> zones)
            {
                var parts = new List<string>();
                for (int i = 0; i < zones.Count && i < LoggedZoneSample; i++) parts.Add($"({zones[i].x},{zones[i].y})");
                return string.Join(" ", parts.ToArray()) + (zones.Count > LoggedZoneSample ? " ..." : "");
            }
        }

        // ── Frames ──────────────────────────────────────────────────────────
        // Frames written in order to one file; where each lands is known as it is written.
        internal sealed class FrameStream : IDisposable
        {
            private readonly FileStream _stream;
            private readonly BinaryWriter _writer;
            private long _position;

            private FrameStream(FileStream stream, long position)
            {
                _stream = stream;
                _writer = new BinaryWriter(stream);
                _position = position;
            }

            internal static FrameStream Create(string path, long worldUid)
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, StreamBufferBytes);
                var frames = new FrameStream(stream, 0);
                frames._writer.Write(PackMagic);
                frames._writer.Write(PackFormat);
                frames._writer.Write(worldUid);
                frames._position = PackHeaderBytes;
                return frames;
            }

            internal static FrameStream OpenForAppend(string path, long worldUid)
            {
                if (File.Exists(path) && new FileInfo(path).Length < PackHeaderBytes) File.Delete(path);
                if (!File.Exists(path)) return Create(path, worldUid);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, StreamBufferBytes);
                long end = stream.Seek(0, SeekOrigin.End);
                return new FrameStream(stream, end);
            }

            internal Record Write(Vector2s zone, ParkSlot slot, ZoneModePark.ParkedZone parked, byte[] payload)
            {
                uint crc = Crc32.Of(payload, 0, payload.Length);
                _writer.Write(FrameMagic);
                _writer.Write((short)zone.x);
                _writer.Write((short)zone.y);
                _writer.Write((byte)slot);
                _writer.Write(payload.Length);
                _writer.Write(crc);
                _writer.Write(payload);
                long offset = _position + FrameHeaderBytes;
                _position = offset + payload.Length;
                return new Record { Slot = slot, Parked = parked, Offset = offset, Length = payload.Length, Crc = crc };
            }

            internal void Flush()
            {
                _writer.Flush();
                _stream.Flush(true);
            }

            public void Dispose() => _writer.Dispose();
        }

        // Reads frames back, checking each against its index entry and its checksum before handing its bytes out.
        private sealed class FrameReader : IDisposable
        {
            private readonly FileStream _stream;
            private readonly byte[] _header = new byte[FrameHeaderBytes];

            internal FrameReader(string path)
                => _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, StreamBufferBytes);

            internal long Length => _stream.Length;

            internal bool TryRead(Vector2s zone, in Record record, out byte[] payload, out string problem)
            {
                payload = null;
                if (!TryReadFrameAt(record.Offset - FrameHeaderBytes, out var frameZone, out var frame, out payload, out problem)) return false;
                if (frameZone.x != zone.x || frameZone.y != zone.y || frame.Length != record.Length || frame.Crc != record.Crc)
                {
                    payload = null;
                    problem = $"the record at byte {record.Offset:N0} is ({frameZone.x},{frameZone.y}), {frame.Length:N0} bytes, not the "
                        + $"({zone.x},{zone.y}), {record.Length:N0} bytes the index names";
                    return false;
                }
                return true;
            }

            internal bool TryReadFrameAt(long at, out Vector2s zone, out Record record, out byte[] payload, out string problem)
            {
                zone = default;
                record = default;
                payload = null;
                if (at < PackHeaderBytes || at + FrameHeaderBytes > _stream.Length)
                {
                    problem = "the pack ends before this record's header";
                    return false;
                }
                _stream.Position = at;
                if (!ReadFully(_header))
                {
                    problem = "the pack ends inside this record's header";
                    return false;
                }
                if (BitConverter.ToUInt32(_header, FrameMagicAt) != FrameMagic)
                {
                    problem = "no record starts here";
                    return false;
                }
                zone = new Vector2s(BitConverter.ToInt16(_header, FrameXAt), BitConverter.ToInt16(_header, FrameYAt));
                record.Slot = (ParkSlot)_header[FrameSlotAt];
                record.Length = BitConverter.ToInt32(_header, FrameLengthAt);
                record.Crc = BitConverter.ToUInt32(_header, FrameCrcAt);
                record.Offset = at + FrameHeaderBytes;
                if (record.Length < 0 || record.Offset + record.Length > _stream.Length)
                {
                    problem = $"its {record.Length:N0} bytes run past the end of the pack";
                    return false;
                }
                payload = new byte[record.Length];
                if (!ReadFully(payload))
                {
                    payload = null;
                    problem = "the pack ends inside this record";
                    return false;
                }
                if (Crc32.Of(payload, 0, payload.Length) != record.Crc)
                {
                    payload = null;
                    problem = "its checksum does not match: the bytes changed on disk";
                    return false;
                }
                problem = null;
                return true;
            }

            private bool ReadFully(byte[] buffer)
            {
                int read = 0;
                while (read < buffer.Length)
                {
                    int got = _stream.Read(buffer, read, buffer.Length - read);
                    if (got <= 0) return false;
                    read += got;
                }
                return true;
            }

            public void Dispose() => _stream.Dispose();
        }
    }
}
