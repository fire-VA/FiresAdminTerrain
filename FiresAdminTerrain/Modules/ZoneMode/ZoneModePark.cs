using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Unity.Profiling;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // A world cloned into one zone at a time keeps everything it had. At its first server load every generated zone is
    // PARKED: its objects are written in vanilla's own save format into FiresWorldGen\firesgen_<world>_park\zones.pack
    // (ZoneModeParkPack; parks written before D6c are one file per zone until their first load repacks them) and leave
    // the live world, and the zone stops being generated, so it is closed sea like any other. Opening a parked zone
    // RESTORES it: the objects load back the way a world load brings them (new ids, links re-made from their saved hashes)
    // and the zone is marked generated, so the land returns as it was, builds and all. Only a seed planted while wearing
    // the Eye of the Beholder, onto land it does not match, raises other land there and discards what was parked.
    // A restored zone's bytes wait in its Restored slot until a load finds the zone saved as generated; a load that finds
    // it not generated (the session ended before a save) restores it again.
    internal static partial class ZoneModePark
    {
        internal struct ParkedZone
        {
            internal int Pieces;
            internal int LocationHash;
            // The land a swapped zone stood on when it was taken back (its source zone and how that land was moved:
            // ZoneShift), so it returns on that land with its builds, not on the zone's own (format 2).
            internal bool Swapped;
            internal Vector2s Source;
            internal ZoneShift Shift;
        }

        internal sealed class ClientState
        {
            internal ZoneOpenSet Zones;
            internal readonly Dictionary<Vector2s, ParkedZone> Details = new Dictionary<Vector2s, ParkedZone>();
        }

        internal struct ZoneFileHeader
        {
            internal long WorldUid;
            internal global::Version.World WorldVersion;
            internal ParkedZone Parked;
            internal int Objects;
            internal int Portals;
        }

        private const string FolderSuffix = "_park";
        private const string MarkerSuffix = ".convert";
        private const string StagingSuffix = ".staging";
        private const string RejectedSuffix = ".rejected";
        private const string MarkerPark = "park";
        private const string MarkerParked = "parked";
        private const string MarkerFailed = "failed";
        private const int MarkerStateLine = 0;
        private const int MarkerUidLine = 1;
        private const int MarkerSourceLine = 2;
        private const uint ZoneFileMagic = 0x445A5046u;
        // Format 2 adds a swapped zone's land to the parked record; format 1 records read as never swapped.
        internal const byte FirstRecordFormat = 1;
        private const byte LandRecordFormat = 2;
        internal const byte RecordFormat = LandRecordFormat;
        private const double BytesPerMegabyte = 1024d * 1024d;
        private const int LoggedZoneSample = 8;

        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsByIdRef =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> ObjectsBySectorRef =
            AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>> PortalObjectsRef =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>>("m_portalObjects");
        private static readonly AccessTools.FieldRef<ZDOMan, ChunkSaveMapping> ChunkSaveMappingRef =
            AccessTools.FieldRefAccess<ZDOMan, ChunkSaveMapping>("m_chunkSaveMapping");
        private static readonly MethodInfo DirtyPortalObjectsSetter = AccessTools.PropertySetter(typeof(ZDOMan), "DirtyPortalObjects");
        private static readonly object[] TrueArgument = { true };
        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZonesRef =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");

        private static readonly Dictionary<Vector2s, ParkedZone> s_index = new Dictionary<Vector2s, ParkedZone>();

        // The park's phases as profiler scopes, so an allocation or timing capture attributes each one on its own.
        private static readonly ProfilerMarker CollectMarker = new ProfilerMarker("Fires.Park.Collect");
        private static readonly ProfilerMarker SerialiseMarker = new ProfilerMarker("Fires.Park.Serialise");
        private static readonly ProfilerMarker DiskWaitMarker = new ProfilerMarker("Fires.Park.DiskWait");
        private static readonly ProfilerMarker RemoveMarker = new ProfilerMarker("Fires.Park.Remove");
        private static World s_world;
        private static bool s_server;
        private static bool s_converted;
        private static bool s_restoredWaiting;
        private static string s_marker;
        private static ClientState s_client;
        private static IParkStore s_store;

        internal static bool Converted => s_converted;

        internal static int ParkedCount => s_index.Count;

        // Moves on at every change to the parked set, so the zone handshake built for one set is never sent for another.
        internal static int IndexGeneration { get; private set; }

        internal static bool WaitsForObjects
            => s_server && (s_marker == MarkerPark || s_marker == MarkerParked || s_index.Count > 0 || s_restoredWaiting);

        internal static string FolderFor(string worldName) => FiresWorldRecords.PathFor(worldName, FolderSuffix);

        internal static string MarkerPathFor(string worldName) => FiresWorldRecords.PathFor(worldName, MarkerSuffix);

        internal static void Reset()
        {
            s_index.Clear();
            IndexGeneration++;
            s_world = null;
            s_server = false;
            s_converted = false;
            s_restoredWaiting = false;
            s_marker = null;
            s_client = null;
            s_store = null;
            ZoneModeLandmarks.Reset();
        }

        // ── World lifecycle ─────────────────────────────────────────────────
        internal static void OnWorldInitialize(World world, bool server)
        {
            Reset();
            if (world == null || world.m_menu) return;
            s_world = world;
            s_server = server;
            s_converted = FiresWorldProfile.Current?.IsClone == true;
            if (!server) return;
            s_marker = ReadMarker(world);
            LoadStore(world);
            s_converted |= s_marker != null || s_index.Count > 0;
            ZoneModeLandmarks.OnWorldInitialize(FolderFor(world.m_name), world.m_uid);
        }

        // The save's objects are loaded and nothing is built yet: a fresh clone parks, and whatever the last session
        // left half done is settled.
        internal static void OnObjectsLoaded(ZoneSystem zoneSystem, bool zoneMode)
        {
            if (!s_server) return;
            if (!zoneMode) ReturnEverything();
            else if (s_marker == MarkerPark) ParkEverything(zoneSystem);
            else if (s_marker == MarkerParked) SettleFirstSave(zoneSystem);
            SettleRestored(zoneSystem);
            if (zoneMode) SettleParkedZones(zoneSystem);
        }

        private static void ReturnEverything()
        {
            if (s_marker == MarkerPark)
            {
                DeleteMarker();
                FiresLogger.LogWarning($"[FiresZones] '{s_world.m_name}' was cloned to be played one zone at a time but has no zone "
                    + "registry, so nothing is parked and it plays as a normal world.");
            }
            if (s_index.Count == 0) return;
            int count = s_index.Count;
            RestoreZones(new List<Vector2s>(s_index.Keys), "the world is no longer one zone at a time");
            FiresLogger.LogWarning($"[FiresZones] '{s_world.m_name}' has no zone registry any more, so all {count} parked zone(s) "
                + "came back and nothing it held is lost.");
        }

        // The first load of a converted world sets aside every explored zone in one main-thread loop (58,885 zones on
        // Fire's 10-03 world, minutes). Unity pumps no window messages meanwhile, so after ~5 s Windows marks the game
        // "Not Responding" and the player (or WER) closes it mid-way: AppHang 10-03 12:55. Peeking the queue (PM_NOREMOVE)
        // once a second tells Windows the thread is alive without handling any input, and a progress line every 15 s
        // shows the work moving. Windows players only; the dedi has no window.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PeekMessage(out NativeMsg msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeMsg { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

        private static long s_lastPumpMs, s_lastProgressMs;

        private static void KeepResponsive(Stopwatch clock, int done, int total)
        {
            long now = clock.ElapsedMilliseconds;
            if (now - s_lastPumpMs >= 1000)
            {
                s_lastPumpMs = now;
                if (Application.platform == RuntimePlatform.WindowsPlayer && !Application.isBatchMode)
                {
                    try { PeekMessage(out _, IntPtr.Zero, 0, 0, 0u); } catch { }
                }
            }
            if (now - s_lastProgressMs >= 15000)
            {
                s_lastProgressMs = now;
                FiresLogger.LogInfo($"[FiresZones] setting aside: {done:N0} of {total:N0} zone(s) after {now / 1000} s; the game is working, not frozen.");
            }
        }

        private static void ParkEverything(ZoneSystem zoneSystem)
        {
            s_lastPumpMs = 0;
            s_lastProgressMs = 0;
            var clock = Stopwatch.StartNew();
            var zdoMan = ZDOMan.instance;
            var generated = GeneratedZonesRef(zoneSystem);
            string folder = FolderFor(s_world.m_name);
            string staging = folder + StagingSuffix;
            var parked = new Dictionary<Vector2s, List<ZDO>>();
            var index = new Dictionary<Vector2s, ParkedZone>();
            int beyondGrid = 0;
            bool liveView = false;
            long snapshotMs = 0, writeMs = 0, indexMs = 0, diskWaitMs = 0, diskBytes = 0;
            int diskZones = 0;
            ZoneModeParkPack pack;
            FiresLogger.LogInfo($"[FiresZones] setting aside the {generated.Count:N0} explored zone(s) of '{s_world.m_name}' "
                + "(its first load since it was converted); a large world takes minutes.");
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                liveView = ZoneModeParkSnapshot.Begin();
                snapshotMs = clock.ElapsedMilliseconds;
                var serializer = new ZoneSerializer();
                using (var disk = new ZoneModeParkWriter(Path.Combine(staging, ZoneModeParkPack.PackFileName), s_world.m_uid))
                {
                    try
                    {
                        foreach (var zone in generated)
                        {
                            if (!ZoneOpenSet.InRange(zone.x, zone.y))
                            {
                                beyondGrid++;
                                continue;
                            }
                            var objects = new List<ZDO>();
                            var portals = new List<ZDO>();
                            ParkedZone entry;
                            byte[] bytes;
                            using (CollectMarker.Auto())
                            {
                                CollectZone(zdoMan, zone, objects, portals);
                                entry = new ParkedZone
                                {
                                    Pieces = CountBuiltPieces(objects) + CountBuiltPieces(portals),
                                    LocationHash = LocationHashAt(zoneSystem, zone),
                                };
                            }
                            using (SerialiseMarker.Auto()) bytes = serializer.Serialize(entry, objects, portals);
                            using (DiskWaitMarker.Auto()) disk.Enqueue(zone, entry, bytes);
                            ZoneModeLandmarks.Record(zone, objects, portals);
                            index[zone] = entry;
                            objects.AddRange(portals);
                            parked[zone] = objects;
                            KeepResponsive(clock, index.Count, generated.Count);
                        }
                    }
                    finally { ZDOExtraData.ClearSave(); }
                    using (DiskWaitMarker.Auto()) disk.Finish();
                    diskWaitMs = disk.WaitedMs;
                    diskZones = disk.ZonesWritten;
                    diskBytes = disk.BytesWritten;
                    pack = ZoneModeParkPack.FromWritten(staging, s_world.m_uid, disk.Written);
                }
                writeMs = clock.ElapsedMilliseconds - snapshotMs;
                pack.WriteIndex();
                ZoneModeLandmarks.Write(staging);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Directory.Move(staging, folder);
                pack.Rebase(folder);
                indexMs = clock.ElapsedMilliseconds - snapshotMs - writeMs;
            }
            catch (Exception ex)
            {
                WriteMarkerState(MarkerFailed);
                FiresLogger.LogError($"[FiresZones] could not park '{s_world.m_name}' ({ex.GetType().Name}: {ex.Message}). Nothing was "
                    + "removed: every zone it has explored stays open, as in a normal world.");
                return;
            }

            int objectCount = 0, pieceCount = 0;
            using (RemoveMarker.Auto())
            {
                foreach (var pair in parked)
                {
                    RemoveFromLiveWorld(zdoMan, pair.Key, pair.Value);
                    generated.Remove(pair.Key);
                    objectCount += pair.Value.Count;
                    pieceCount += index[pair.Key].Pieces;
                }
                ResetChunkStore(zdoMan);
            }
            long removeMs = clock.ElapsedMilliseconds - snapshotMs - writeMs - indexMs;
            foreach (var pair in index) s_index[pair.Key] = pair.Value;
            IndexGeneration++;
            s_store = pack;
            WriteMarkerState(MarkerParked);
            FiresLogger.LogInfo($"[FiresZones] PARKED '{s_world.m_name}': {parked.Count} explored zone(s) holding {objectCount:N0} "
                + $"object(s) ({pieceCount:N0} built piece(s)) are set aside in {Path.GetFileName(folder)} "
                + $"({clock.ElapsedMilliseconds} ms: snapshot {snapshotMs} ({(liveView ? "live view" : "cloned")}), collect+serialise "
                + $"{writeMs - diskWaitMs}, waiting on disk {diskWaitMs} ({diskZones:N0} zone(s), {diskBytes / BytesPerMegabyte:N0} MB "
                + $"written alongside into {ZoneModeParkPack.PackFileName}), index {indexMs}, remove {removeMs}). Each returns as it was "
                + "when its zone is opened."
                + (beyondGrid > 0 ? $" {beyondGrid} explored zone(s) beyond the zone grid (±{ZoneOpenSet.ZoneExtent}) stay as they are." : ""));
            ZoneModeParkMemory.ReleaseAfterPark(zdoMan);
        }

        // A parked zone returns as it was and is marked generated, so a location planned inside it but never placed can
        // never spawn. Run after Valheim has placed this world's locations again; a first park waits for that plan, so it
        // finds none, while a converted world re-planned later (a game update) can, and those are named.
        internal static void ReportUnspawnedLocations(ZoneSystem zoneSystem)
        {
            if (!s_server || zoneSystem == null || s_index.Count == 0) return;
            var unspawned = new List<Vector2s>();
            var names = new List<string>();
            foreach (var pair in zoneSystem.m_locationInstances)
            {
                if (pair.Value.m_placed || !s_index.ContainsKey(pair.Key)) continue;
                unspawned.Add(pair.Key);
                if (names.Count < LoggedZoneSample) names.Add($"{pair.Value.m_location?.m_prefabName ?? "?"} at ({pair.Key.x},{pair.Key.y})");
            }
            if (unspawned.Count == 0)
            {
                FiresLogger.LogInfo($"[FiresZones] park check: 0 planned locations inside the {s_index.Count} parked zone(s) are unspawned.");
                return;
            }
            FiresLogger.LogWarning($"[FiresZones] park check: {unspawned.Count} planned location(s) inside parked zones were never "
                + $"spawned and will not appear when their zone returns: {string.Join(", ", names.ToArray())}"
                + (unspawned.Count > names.Count ? " ..." : "") + ".");
        }

        // True when this world's next load parks everything it has explored: a clone whose convert marker still says so.
        internal static bool ConvertsOnNextLoad(World world)
        {
            if (world == null || string.IsNullOrEmpty(world.m_name)) return false;
            string path = MarkerPathFor(world.m_name);
            if (!File.Exists(path)) return false;
            try
            {
                string[] lines = File.ReadAllLines(path);
                return lines.Length > MarkerUidLine && lines[MarkerStateLine].Trim() == MarkerPark
                    && long.TryParse(lines[MarkerUidLine], NumberStyles.Integer, CultureInfo.InvariantCulture, out long uid)
                    && uid == world.m_uid;
            }
            catch (IOException) { return false; }
        }

        // The first save after parking has happened once the save lists no parked zone as generated. Until then the save
        // still holds what was parked, and those objects leave the live world again.
        private static void SettleFirstSave(ZoneSystem zoneSystem)
        {
            var generated = GeneratedZonesRef(zoneSystem);
            var stale = new List<Vector2s>();
            foreach (var zone in s_index.Keys)
                if (generated.Contains(zone)) stale.Add(zone);
            if (stale.Count == 0)
            {
                string source = DeleteMarker();
                FiresLogger.LogInfo($"[FiresZones] '{s_world.m_name}' is fully converted{(source != null ? $" from '{source}'" : "")}: "
                    + $"its first save after parking is on disk, {s_index.Count} zone(s) still parked.");
                return;
            }
            var zdoMan = ZDOMan.instance;
            int removed = 0;
            foreach (var zone in stale)
            {
                var objects = new List<ZDO>();
                var portals = new List<ZDO>();
                CollectZone(zdoMan, zone, objects, portals);
                objects.AddRange(portals);
                RemoveFromLiveWorld(zdoMan, zone, objects);
                generated.Remove(zone);
                removed += objects.Count;
            }
            ResetChunkStore(zdoMan);
            FiresLogger.LogWarning($"[FiresZones] '{s_world.m_name}' closed before its first save after parking, so the save still "
                + $"held {stale.Count} parked zone(s): their {removed:N0} object(s) left the live world again. The parked copies are kept.");
            ZoneModeParkMemory.ReleaseAfterPark(zdoMan);
        }

        // A zone's bytes stay in its Restored slot after it returns, until a load finds the zone saved as generated. A load
        // that finds it open but not generated (the session ended before a save) brings it back again; one that finds it
        // closed parks it again.
        private static void SettleRestored(ZoneSystem zoneSystem)
        {
            s_restoredWaiting = false;
            if (s_store == null) return;
            var generated = GeneratedZonesRef(zoneSystem);
            bool changed = false;
            foreach (var zone in s_store.ZonesIn(ParkSlot.Restored))
            {
                if (generated.Contains(zone))
                {
                    s_store.Remove(zone, ParkSlot.Restored);
                    changed = true;
                    continue;
                }
                if (ZoneModeRegistry.IsOpen(zone))
                {
                    changed |= !RestoreFromStore(zone, ParkSlot.Restored, zoneSystem, "its return was never saved");
                    continue;
                }
                if (!s_store.Move(zone, ParkSlot.Restored, ParkSlot.Parked)) continue;
                s_store.TryGetDetails(zone, ParkSlot.Parked, out var parked);
                s_index[zone] = parked;
                IndexGeneration++;
                changed = true;
            }
            if (changed) s_store.Commit(s_index);
        }

        private static void SettleParkedZones(ZoneSystem zoneSystem)
        {
            var generated = GeneratedZonesRef(zoneSystem);
            var openedEarlier = new List<Vector2s>();
            var alsoGenerated = new List<Vector2s>();
            foreach (var zone in s_index.Keys)
            {
                if (generated.Contains(zone)) alsoGenerated.Add(zone);
                else if (ZoneModeRegistry.IsOpen(zone)) openedEarlier.Add(zone);
            }
            if (alsoGenerated.Count > 0)
                FiresLogger.LogError($"[FiresZones] {alsoGenerated.Count} parked zone(s) of '{s_world.m_name}' are also generated in the save: "
                    + $"{Sample(alsoGenerated)}. Their parked copies are kept and not brought back; report this.");
            if (openedEarlier.Count > 0) RestoreZones(openedEarlier, "opened before the last shutdown");
        }

        // ── Restoring ───────────────────────────────────────────────────────
        internal static int RestoreZones(IList<Vector2s> zones, string context)
        {
            if (!s_server || s_index.Count == 0 || s_store == null) return 0;
            var zoneSystem = ZoneSystem.instance;
            int restored = 0;
            bool indexChanged = false;
            foreach (var zone in zones)
            {
                if (!s_index.Remove(zone)) continue;
                IndexGeneration++;
                indexChanged = true;
                if (!RestoreFromStore(zone, ParkSlot.Parked, zoneSystem, context)) continue;
                s_store.Move(zone, ParkSlot.Parked, ParkSlot.Restored);
                restored++;
            }
            if (indexChanged) s_store.Commit(s_index);
            return restored;
        }

        // False when the bytes could not be read or loaded: they move to the Unreadable slot, named, and the zone rises as
        // new land. The caller commits the store.
        private static bool RestoreFromStore(Vector2s zone, ParkSlot slot, ZoneSystem zoneSystem, string context)
        {
            var restored = new List<ZDO>();
            ZoneFileHeader header;
            try
            {
                if (!s_store.TryRead(zone, slot, out byte[] payload, out string problem)) throw new InvalidDataException(problem);
                var pkg = ReaderOver(payload);
                header = ReadHeader(pkg);
                int total = header.Objects + header.Portals;
                for (int i = 0; i < total; i++)
                {
                    var zdo = ZDOPool.Create();
                    restored.Add(zdo);
                    zdo.Load(pkg, header.WorldVersion);
                }
            }
            catch (Exception ex)
            {
                foreach (var zdo in restored) ZDOPool.Release(zdo);
                bool kept = s_store.Move(zone, slot, ParkSlot.Unreadable);
                FiresLogger.LogError($"[FiresZones] could not bring ({zone.x},{zone.y}) back as it was ({ex.GetType().Name}: {ex.Message}), "
                    + "so it rises as new land instead."
                    + (kept ? $" Its bytes are kept as unreadable in {s_store.Describe()}." : ""));
                return false;
            }
            var zdoMan = ZDOMan.instance;
            AddToLiveWorld(zdoMan, restored, header.Objects);
            ConnectRestored(restored);
            ZoneModeLandmarks.OnRestored(zone, restored, header.Objects);
            RaiseZoneRestored(zone, restored);
            GeneratedZonesRef(zoneSystem).Add(zone);
            FiresLogger.LogInfo($"[FiresZones] ({zone.x},{zone.y}) returned as it was: {restored.Count:N0} object(s), "
                + $"{header.Parked.Pieces:N0} built piece(s) ({context}).");
            return true;
        }

        // A zone's objects are live again. Load-time passes never saw them while they were parked (the chunk generator
        // swaps old biome machines here, [generator] 2026-09-27). Subscribers hold no ZDO past the call: pooled objects
        // are released after a park (ZoneModeParkMemory). A subscriber's fault is logged and never stops the restore.
        internal static event Action<Vector2s, List<ZDO>> ZoneRestored;

        private static void RaiseZoneRestored(Vector2s zone, List<ZDO> restored)
        {
            var handlers = ZoneRestored;
            if (handlers == null) return;
            foreach (Action<Vector2s, List<ZDO>> handler in handlers.GetInvocationList())
            {
                try { handler(zone, restored); }
                catch (Exception ex)
                {
                    FiresLogger.LogError($"[FiresZones] ({zone.x},{zone.y}) restored, but a ZoneRestored handler "
                        + $"({handler.Method.DeclaringType?.Name}.{handler.Method.Name}) threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ── What a park holds, read without bringing it back ─────────────────
        internal static List<Vector2s> ParkedZoneList() => new List<Vector2s>(s_index.Keys);

        // One parked zone, loaded one object at a time for `visit` and released again; `visit`'s second argument says
        // the object is a portal. Returns how many objects were read, or -1 if the file could not be read.
        internal static int TryReadZone(Vector2s zone, Action<ZDO, bool> visit)
        {
            if (!s_server || s_world == null || s_store == null || !s_store.Has(zone, ParkSlot.Parked)) return -1;
            int read = 0;
            try
            {
                if (!s_store.TryRead(zone, ParkSlot.Parked, out byte[] payload, out string problem)) throw new InvalidDataException(problem);
                var pkg = ReaderOver(payload);
                var header = ReadHeader(pkg);
                int total = header.Objects + header.Portals;
                for (; read < total; read++)
                {
                    var zdo = ZDOPool.Create();
                    try
                    {
                        zdo.Load(pkg, header.WorldVersion);
                        visit(zdo, read >= header.Objects);
                    }
                    finally { ZDOPool.Release(zdo); }
                }
                return read;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] could not read parked zone ({zone.x},{zone.y}) "
                    + $"({ex.GetType().Name}: {ex.Message}).");
                return -1;
            }
        }

        // Every object the parked zones hold, loaded one at a time for `visit` and released again; returns how many
        // `visit` counted. One pass over the store, in the order it reads fastest.
        internal static int ForEachParkedObject(Func<ZDO, bool> visit)
        {
            if (!s_server || s_index.Count == 0 || s_store == null) return 0;
            int counted = 0;
            foreach (var read in s_store.ReadAll(ParkSlot.Parked, s_index.Keys))
            {
                try
                {
                    if (read.Problem != null) throw new InvalidDataException(read.Problem);
                    var pkg = ReaderOver(read.Payload);
                    var header = ReadHeader(pkg);
                    int total = header.Objects + header.Portals;
                    for (int i = 0; i < total; i++)
                    {
                        var zdo = ZDOPool.Create();
                        try
                        {
                            zdo.Load(pkg, header.WorldVersion);
                            if (visit(zdo)) counted++;
                        }
                        finally { ZDOPool.Release(zdo); }
                    }
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresZones] could not read parked zone ({read.Zone.x},{read.Zone.y}) ({ex.GetType().Name}: {ex.Message}).");
                }
            }
            return counted;
        }

        private static void AddToLiveWorld(ZDOMan zdoMan, List<ZDO> restored, int objectCount)
        {
            var byId = ObjectsByIdRef(zdoMan);
            var portals = PortalObjectsRef(zdoMan);
            for (int i = 0; i < restored.Count; i++)
            {
                var zdo = restored[i];
                byId[zdo.m_uid] = zdo;
                var sector = zdo.GetSectorIndex();
                if (i < objectCount)
                {
                    zdoMan.AddToSector(zdo, sector);
                    continue;
                }
                if (!portals.TryGetValue(sector, out var inSector)) portals[sector] = inSector = new List<ZDO>();
                inSector.Add(zdo);
            }
            if (restored.Count > objectCount) DirtyPortalObjectsSetter.Invoke(zdoMan, TrueArgument);
        }

        // Vanilla's three link passes after a load (ZDOMan.ConnectPortals / ConnectSpawners / ConnectSyncTransforms),
        // run over the restored objects only. A portal whose partner is still parked pairs by tag once that zone returns.
        private static void ConnectRestored(List<ZDO> restored)
        {
            ConnectPortals(restored);
            ConnectSpawners(restored);
            ConnectSyncTransforms(restored);
        }

        private static void ConnectPortals(List<ZDO> restored)
        {
            const ZDOExtraData.ConnectionType target = ZDOExtraData.ConnectionType.Portal | ZDOExtraData.ConnectionType.Target;
            var targets = WithHashData(restored, target);
            foreach (var zdo in restored)
            {
                var source = ZDOExtraData.GetConnectionHashData(zdo.m_uid, ZDOExtraData.ConnectionType.Portal);
                if (source == null) continue;
                foreach (var partner in targets)
                {
                    if (partner == zdo || ZDOExtraData.GetConnectionType(partner.m_uid) != ZDOExtraData.ConnectionType.None) continue;
                    if (ZDOExtraData.GetConnectionHashData(partner.m_uid, target).m_hash != source.m_hash) continue;
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    partner.SetOwner(ZDOMan.GetSessionID());
                    zdo.SetConnection(ZDOExtraData.ConnectionType.Portal, partner.m_uid);
                    partner.SetConnection(ZDOExtraData.ConnectionType.Portal, zdo.m_uid);
                    break;
                }
            }
        }

        private static void ConnectSpawners(List<ZDO> restored)
        {
            const ZDOExtraData.ConnectionType target = ZDOExtraData.ConnectionType.Spawned | ZDOExtraData.ConnectionType.Target;
            var targets = WithHashData(restored, target);
            foreach (var zdo in restored)
            {
                var source = ZDOExtraData.GetConnectionHashData(zdo.m_uid, ZDOExtraData.ConnectionType.Spawned);
                if (source == null) continue;
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDO spawned = null;
                foreach (var partner in targets)
                {
                    if (partner == zdo || ZDOExtraData.GetConnectionHashData(partner.m_uid, target).m_hash != source.m_hash) continue;
                    spawned = partner;
                    break;
                }
                if (spawned == null)
                {
                    zdo.SetConnection(ZDOExtraData.ConnectionType.Spawned, ZDOID.None);
                    continue;
                }
                zdo.SetConnection(ZDOExtraData.ConnectionType.Spawned, spawned.m_uid);
                targets.Remove(spawned);
            }
            foreach (var orphan in targets) ZDOExtraData.RemoveConnectionHashData(orphan.m_uid, target);
        }

        private static void ConnectSyncTransforms(List<ZDO> restored)
        {
            const ZDOExtraData.ConnectionType target = ZDOExtraData.ConnectionType.SyncTransform | ZDOExtraData.ConnectionType.Target;
            var targets = WithHashData(restored, target);
            foreach (var zdo in restored)
            {
                var source = ZDOExtraData.GetConnectionHashData(zdo.m_uid, ZDOExtraData.ConnectionType.SyncTransform);
                if (source == null) continue;
                foreach (var partner in targets)
                {
                    if (ZDOExtraData.GetConnectionHashData(partner.m_uid, target).m_hash != source.m_hash) continue;
                    ZDOExtraData.SetConnection(zdo.m_uid, ZDOExtraData.ConnectionType.SyncTransform, partner.m_uid);
                    break;
                }
            }
        }

        private static List<ZDO> WithHashData(List<ZDO> zdos, ZDOExtraData.ConnectionType type)
        {
            var found = new List<ZDO>();
            foreach (var zdo in zdos)
                if (ZDOExtraData.GetConnectionHashData(zdo.m_uid, type) != null) found.Add(zdo);
            return found;
        }

        /// <summary>True when this zone has an overwritten chunk set aside that could be brought back.</summary>
        internal static bool HasOverridden(Vector2s zone)
            => s_server && s_world != null && s_store != null && s_store.Has(zone, ParkSlot.Overridden);

        // ── UNDO: the exact reverse of Discard ──────────────────────────────────────────────────────
        // Moves the Overridden bytes back to the Parked slot and puts the index entry back, which is all
        // "parked" means. Everything downstream then works unchanged: the zone reads as parked, the Eye
        // offers to bring it back, and the existing matching-seed restore path does the actual work.
        // Writing a second restore path would have meant two ways to un-park a zone that could disagree.
        //
        // The zone must be CLOSED afterwards for this to be visible - a zone that is already open has
        // the replacement land in it, and the caller closes it so the chunk can be planted again.
        internal static bool TryRecoverOverridden(Vector2s zone, out string report)
        {
            report = null;
            if (!s_server) { report = "Only the server holds the set-aside chunks."; return false; }
            if (s_world == null) { report = "No world is loaded."; return false; }

            if (s_store == null || !s_store.Has(zone, ParkSlot.Overridden))
            {
                report = $"({zone.x},{zone.y}) has no set-aside chunk - nothing was overwritten there, "
                    + "or it was overwritten before chunks were kept.";
                return false;
            }
            if (s_store.Has(zone, ParkSlot.Parked))
            {
                report = $"({zone.x},{zone.y}) is already parked, so there is nothing to undo.";
                return false;
            }
            if (!s_store.Move(zone, ParkSlot.Overridden, ParkSlot.Parked))
            {
                report = $"could not move the set-aside chunk of ({zone.x},{zone.y}) back into place.";
                return false;
            }

            s_store.TryGetDetails(zone, ParkSlot.Parked, out var parked);
            s_index[zone] = parked;
            IndexGeneration++;
            s_store.Commit(s_index);
            report = $"({zone.x},{zone.y}) is parked again with its {parked.Pieces:N0} built piece(s). "
                + "Plant the matching seed to bring it back.";
            FiresLogger.LogWarning($"[FiresZones] UNDO: {report}");
            return true;
        }

        // ── Taking an OPEN zone back out (the Chaos Hammer's remover) ───────
        // The same set-aside Discard leaves behind a wrong seed: everything the zone holds, voxel anchors included, is
        // written to its Overridden slot, outside the parked set, so fires_zones_undo can bring it back. Then every object
        // is destroyed the way vanilla destroys one, owned first, so every peer drops it too. The caller closes the zone.
        internal static bool TrySetAsideOpenZone(Vector2s zone, out int removed, out string problem)
        {
            removed = 0;
            problem = null;
            var zdoMan = ZDOMan.instance;
            if (!s_server || s_world == null || zdoMan == null) { problem = "only the server can take a chunk back"; return false; }

            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            CollectZone(zdoMan, zone, objects, portals);
            var entry = new ParkedZone
            {
                Pieces = CountBuiltPieces(objects) + CountBuiltPieces(portals),
                LocationHash = ZoneSystem.instance != null ? LocationHashAt(ZoneSystem.instance, zone) : 0,
            };
            if (ZoneModeRegistry.TryGetSource(zone, out var source))
            {
                entry.Swapped = true;
                entry.Source = source;
                entry.Shift = ZoneModeRegistry.ShiftOf(zone);
            }
            try
            {
                EnsureStore();
                byte[] bytes;
                ZDOExtraData.PrepareSave();
                try { bytes = new ZoneSerializer().Serialize(entry, objects, portals); }
                finally { ZDOExtraData.ClearSave(); }
                s_store.Write(zone, ParkSlot.Overridden, entry, bytes);
            }
            catch (Exception ex)
            {
                problem = $"could not be written ({ex.GetType().Name}: {ex.Message})";
                return false;
            }

            // A zone restored this session still has its pre-restore bytes waiting; a later load would park them again.
            if (s_store.Has(zone, ParkSlot.Restored)) s_store.Remove(zone, ParkSlot.Restored);
            s_store.Commit(s_index);

            long self = ZDOMan.GetSessionID();
            objects.AddRange(portals);
            foreach (var zdo in objects)
            {
                if (!zdo.IsOwner()) zdo.SetOwner(self);
                zdoMan.DestroyZDO(zdo);
                removed++;
            }
            FiresLogger.LogInfo($"[FiresZones] ({zone.x},{zone.y}) taken back: {removed:N0} object(s), {entry.Pieces:N0} built "
                + $"piece(s), set aside in {s_store.Describe()}"
                + (entry.Swapped ? $" with the land it stood on ({entry.Source.x},{entry.Source.y}), {Describe(entry.Shift)}" : "")
                + $". fires_zones_undo {zone.x} {zone.y} parks it again.");
            return true;
        }

        // A world that never parked gets its park (an empty pack) the first time something is set aside in it.
        private static void EnsureStore()
        {
            if (s_store != null) return;
            s_store = ZoneModeParkPack.CreateEmpty(FolderFor(s_world.m_name), s_world.m_uid);
        }

        // ── Raising other land over a parked zone ───────────────────────────
        internal static void Discard(Vector2s zone, string who)
        {
            if (!s_server || !s_index.TryGetValue(zone, out var parked)) return;
            s_index.Remove(zone);
            IndexGeneration++;

            // KEPT, NOT DELETED. The chunk used to be deleted here, which made a mis-swing permanently
            // destructive: no clear, undo or replant could ever bring it back. The index entry still goes,
            // so the zone stops reading as parked and the Eye does not offer land the player already
            // replaced, but the bytes survive in the Overridden slot, so clearing the zone and replanting
            // the right seed has something to put back.
            bool retained = s_store != null && s_store.Has(zone, ParkSlot.Parked) && s_store.Move(zone, ParkSlot.Parked, ParkSlot.Overridden);
            s_store?.Commit(s_index);

            FiresLogger.LogInfo($"[FiresZones] ({zone.x},{zone.y}) was raised as other land by {who}; what was parked there "
                + $"({parked.Pieces:N0} built piece(s)) is no longer in the world"
                + (retained
                    ? " but is KEPT in the park - clear the zone and plant the matching seed to bring it back."
                    : ". Its parked bytes could not be set aside, so it is gone."));
        }

        // ── Queries (server, host and client) ───────────────────────────────
        internal static bool TryGetParked(Vector2s zone, out ParkedZone parked)
        {
            parked = default;
            if (s_server) return s_index.TryGetValue(zone, out parked);
            var client = s_client;
            if (client?.Zones == null || !client.Zones.IsOpen(zone) || ZoneModeRegistry.IsOpen(zone)) return false;
            client.Details.TryGetValue(zone, out parked);
            return true;
        }

        // A seed matches a parked zone when it would raise the land that zone showed: the land it stood on when it was a
        // swapped zone, else its own planned biome, or the place it held.
        internal static bool SeedMatches(ChunkSeed seed, Vector2s zone, ParkedZone parked)
        {
            if (seed.RestoresOnly) return true;
            if (seed.IsLocation) return parked.LocationHash != 0 && parked.LocationHash == seed.Location.GetStableHashCode();
            ZoneModeChunkTiers.ZoneTier(parked.Swapped ? parked.Source : zone, out var planned);
            return planned == seed.Tier.Biome;
        }

        private static string Describe(in ZoneShift shift)
            => shift.IsZero ? "at its own height" : $"lift {shift.Lift:0.0} m, slide ({shift.Dx:0.0},{shift.Dz:0.0}) m";

        // ── Handshake ───────────────────────────────────────────────────────
        internal static void WriteHandshake(ZPackage pkg)
        {
            bool any = s_server && s_index.Count > 0;
            pkg.Write(any);
            if (!any) return;
            ulong[] words = ZoneOpenSet.Build(s_index.Keys, 0).CopyWords();
            var bytes = new byte[words.Length * sizeof(ulong)];
            Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
            pkg.Write(bytes);
            var detailed = new List<KeyValuePair<Vector2s, ParkedZone>>();
            foreach (var pair in s_index)
                if (pair.Value.Pieces > 0 || pair.Value.LocationHash != 0 || pair.Value.Swapped) detailed.Add(pair);
            pkg.Write(detailed.Count);
            foreach (var pair in detailed)
            {
                pkg.Write(pair.Key);
                WriteParked(pkg.Write, pkg.Write, pkg.Write, pkg.Write, pair.Value);
            }
        }

        internal static ClientState ReadHandshake(ZPackage pkg)
        {
            if (!pkg.ReadBool()) return null;
            byte[] bytes = pkg.ReadByteArray();
            if (bytes == null || bytes.Length != ZoneOpenSet.WordCount * sizeof(ulong))
                throw new InvalidDataException($"the parked set has {bytes?.Length ?? 0} bytes, expected {ZoneOpenSet.WordCount * sizeof(ulong)}");
            var words = new ulong[ZoneOpenSet.WordCount];
            Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
            var state = new ClientState { Zones = ZoneOpenSet.FromWords(words, 0) };
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++)
            {
                var zone = pkg.ReadVector2s();
                state.Details[zone] = ReadParked(pkg.ReadInt, pkg.ReadShort, pkg.ReadSingle, pkg.ReadBool, RecordFormat);
            }
            return state;
        }

        internal static void BindClient(ClientState state) => s_client = state;

        internal static string DescribeClient(ClientState state)
            => state?.Zones == null ? "" : $", {state.Zones.OpenCount} parked zone(s)";

        // ── Convert marker: FiresWorldGen\firesgen_<world>.convert ──────────
        internal static void WriteConvertMarker(string worldName, long worldUid, string sourceName)
        {
            File.WriteAllLines(MarkerPathFor(worldName), new[]
            {
                MarkerPark,
                worldUid.ToString(CultureInfo.InvariantCulture),
                sourceName,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        private static string ReadMarker(World world)
        {
            string path = MarkerPathFor(world.m_name);
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path);
            bool ours = lines.Length > MarkerUidLine && long.TryParse(lines[MarkerUidLine], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long uid) && uid == world.m_uid;
            if (ours) return lines[MarkerStateLine].Trim();
            File.Delete(path);
            FiresLogger.LogWarning($"[FiresZones] deleted {Path.GetFileName(path)}: it was written for a deleted world that had this name.");
            return null;
        }

        private static void WriteMarkerState(string state)
        {
            string path = MarkerPathFor(s_world.m_name);
            try
            {
                string[] lines = File.ReadAllLines(path);
                lines[MarkerStateLine] = state;
                File.WriteAllLines(path, lines);
                s_marker = state;
            }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] could not mark {Path.GetFileName(path)} as {state}: {ex.Message}"); }
        }

        // Returns the world the marker says this one was cloned from.
        private static string DeleteMarker()
        {
            string path = MarkerPathFor(s_world.m_name);
            string source = null;
            try
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length > MarkerSourceLine) source = lines[MarkerSourceLine];
                File.Delete(path);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] could not delete {Path.GetFileName(path)}: {ex.Message}"); }
            s_marker = null;
            return source;
        }

        // ── Loading the park: <park>\zones.index + zones.pack ───────────────
        // How long the last world load's index read and one-time repack took (ZoneModeRegistry's world-init line).
        internal static long IndexReadMs { get; private set; }
        internal static long RepackMs { get; private set; }
        internal static string StoreDescription => s_store?.Describe() ?? "no park";

        // A park still kept as zone files is repacked here, once (ZoneModeParkPack.TryRepack); if that cannot finish, the
        // world plays on its zone files as before and the next load tries again.
        private static void LoadStore(World world)
        {
            IndexReadMs = 0;
            RepackMs = 0;
            string folder = FolderFor(world.m_name);
            ZoneModeParkPack.CompleteInterruptedRepack(folder);
            if (!Directory.Exists(folder)) return;
            var clock = Stopwatch.StartNew();
            string setAside;
            if (ZoneModeParkPack.IsPacked(folder))
            {
                s_store = ZoneModeParkPack.Load(folder, world.m_uid, world.m_name, out setAside);
                IndexReadMs = clock.ElapsedMilliseconds;
            }
            else
            {
                var files = ZoneModeParkZoneFiles.Load(folder, world.m_uid, world.m_name, out setAside);
                IndexReadMs = clock.ElapsedMilliseconds;
                clock.Restart();
                if (files != null) s_store = (IParkStore)ZoneModeParkPack.TryRepack(files, world.m_name) ?? files;
                RepackMs = clock.ElapsedMilliseconds;
            }
            if (s_store == null)
            {
                SetAside(folder, setAside);
                return;
            }
            foreach (var pair in s_store.Parked()) s_index[pair.Key] = pair.Value;
            IndexGeneration++;
            s_restoredWaiting = s_store.ZonesIn(ParkSlot.Restored).Count > 0;
        }

        // A parked record, the same in the index, a zone file and the handshake, through each one's own reads and writes.
        internal static void WriteParked(Action<int> writeInt, Action<short> writeShort, Action<float> writeFloat, Action<bool> writeBool,
            in ParkedZone parked)
        {
            writeInt(parked.Pieces);
            writeInt(parked.LocationHash);
            writeBool(parked.Swapped);
            if (!parked.Swapped) return;
            writeShort((short)parked.Source.x);
            writeShort((short)parked.Source.y);
            writeFloat(parked.Shift.Lift);
            writeFloat(parked.Shift.Dx);
            writeFloat(parked.Shift.Dz);
        }

        internal static ParkedZone ReadParked(Func<int> readInt, Func<short> readShort, Func<float> readFloat, Func<bool> readBool, byte format)
        {
            var parked = new ParkedZone { Pieces = readInt(), LocationHash = readInt() };
            if (format < LandRecordFormat || !readBool()) return parked;
            parked.Swapped = true;
            parked.Source = new Vector2s(readShort(), readShort());
            parked.Shift = new ZoneShift(readFloat(), readFloat(), readFloat());
            return parked;
        }

        private static void SetAside(string folder, string why)
        {
            string rejected = folder + RejectedSuffix;
            if (Directory.Exists(rejected)) Directory.Delete(rejected, true);
            Directory.Move(folder, rejected);
            FiresLogger.LogWarning($"[FiresZones] set aside {Path.GetFileName(folder)} as {Path.GetFileName(rejected)}: {why}.");
        }

        // ── Zone files: <park>\<x>_<y>.zdos ─────────────────────────────────
        // Everything the server holds in one zone, for anything that saves a zone whole (ZoneModeChunkBlueprint).
        internal static void CollectZoneObjects(Vector2s zone, List<ZDO> objects, List<ZDO> portals)
        {
            if (ZDOMan.instance != null) CollectZone(ZDOMan.instance, zone, objects, portals);
        }

        // Everything in the zone, gone: what a chunk stamped over open land replaces. The voxel anchors stay: they are
        // the zone's LAND, not things standing on it, and the stamp that follows rewrites each one in place. Removing
        // them and letting the stamp write new ones raced on every client — the newcomer lost the duplicate check to the
        // anchor still being torn down, so the objects came back and the land did not.
        internal static int ClearZoneObjects(Vector2s zone)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return 0;
            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            CollectZone(zdoMan, zone, objects, portals);
            objects.AddRange(portals);
            objects.RemoveAll(zdo => zdo.GetPrefab() == Voxel.VoxelAnchorPrefab.PrefabHash);
            if (objects.Count == 0) return 0;
            RemoveFromLiveWorld(zdoMan, zone, objects);
            ResetChunkStore(zdoMan);
            return objects.Count;
        }

        // Objects read from a file join the live world the way a restored zone's do: added, then vanilla's link passes.
        internal static void AdoptObjects(List<ZDO> objects, int objectCount)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null || objects.Count == 0) return;
            AddToLiveWorld(zdoMan, objects, objectCount);
            ConnectRestored(objects);
        }

        private static void CollectZone(ZDOMan zdoMan, Vector2s zone, List<ZDO> objects, List<ZDO> portals)
        {
            var sector = ZoneSystem.SectorToIndex(zone);
            var inSector = ObjectsBySectorRef(zdoMan)[(int)sector.Sector];
            if (inSector != null)
                foreach (var zdo in inSector)
                    if (zdo.Persistent) objects.Add(zdo);
            if (PortalObjectsRef(zdoMan).TryGetValue(sector, out var inPortals))
                foreach (var zdo in inPortals)
                    if (zdo.Persistent) portals.Add(zdo);
        }

        private static void WriteZone(ZPackage pkg, ParkedZone entry, List<ZDO> objects, List<ZDO> portals)
        {
            pkg.Write(ZoneFileMagic);
            pkg.Write(RecordFormat);
            pkg.Write(s_world.m_uid);
            pkg.Write((short)global::Version.c_WorldVersion);
            WriteParked(pkg.Write, pkg.Write, pkg.Write, pkg.Write, entry);
            pkg.Write(objects.Count);
            pkg.Write(portals.Count);
            foreach (var zdo in objects) zdo.Save(pkg);
            foreach (var zdo in portals) zdo.Save(pkg);
        }

        // One zone file's bytes, built in a buffer that is reused from zone to zone.
        private sealed class ZoneSerializer
        {
            private readonly MemoryStream _buffer = new MemoryStream();
            private readonly BinaryWriter _writer;
            private readonly ZPackage _package = new ZPackage();

            internal ZoneSerializer()
            {
                _writer = new BinaryWriter(_buffer);
                _package.SetWriter(_writer);
            }

            internal byte[] Serialize(ParkedZone entry, List<ZDO> objects, List<ZDO> portals)
            {
                _buffer.SetLength(0);
                WriteZone(_package, entry, objects, portals);
                _writer.Flush();
                return _buffer.ToArray();
            }
        }

        internal static ZoneFileHeader ReadHeader(ZPackage pkg)
        {
            if (pkg.ReadUInt() != ZoneFileMagic) throw new InvalidDataException("not a parked zone");
            byte format = pkg.ReadByte();
            if (format < FirstRecordFormat || format > RecordFormat) throw new InvalidDataException($"parked zone format {format}, this build reads up to {RecordFormat}");
            return new ZoneFileHeader
            {
                WorldUid = pkg.ReadLong(),
                WorldVersion = (global::Version.World)pkg.ReadShort(),
                Parked = ReadParked(pkg.ReadInt, pkg.ReadShort, pkg.ReadSingle, pkg.ReadBool, format),
                Objects = pkg.ReadInt(),
                Portals = pkg.ReadInt(),
            };
        }

        // A zone's bytes as the park stores them: its record and the world it was written for, without loading an object.
        internal static bool TryReadRecordHeader(byte[] payload, out ParkedZone parked, out long worldUid)
        {
            parked = default;
            worldUid = 0;
            try
            {
                var header = ReadHeader(ReaderOver(payload));
                parked = header.Parked;
                worldUid = header.WorldUid;
                return true;
            }
            catch (Exception) { return false; }
        }

        // A package reading straight from a zone's bytes (ZPackage(byte[]) would copy them first).
        private static ZPackage ReaderOver(byte[] payload)
        {
            var pkg = new ZPackage();
            pkg.SetReader(new BinaryReader(new MemoryStream(payload, false)));
            return pkg;
        }

        private static void RemoveFromLiveWorld(ZDOMan zdoMan, Vector2s zone, List<ZDO> zdos)
        {
            foreach (var zdo in zdos) zdoMan.m_onZDODestroyed?.Invoke(zdo);
            var sector = ZoneSystem.SectorToIndex(zone);
            ObjectsBySectorRef(zdoMan)[(int)sector.Sector]?.RemoveAll(zdo => zdo.Persistent);
            var portals = PortalObjectsRef(zdoMan);
            if (portals.TryGetValue(sector, out var inPortals))
            {
                inPortals.RemoveAll(zdo => zdo.Persistent);
                if (inPortals.Count == 0) portals.Remove(sector);
            }
            var byId = ObjectsByIdRef(zdoMan);
            foreach (var zdo in zdos)
            {
                byId.Remove(zdo.m_uid);
                ZDOPool.Release(zdo);
            }
        }

        // An emptied save chunk is never rewritten, so its old file would bring the parked objects back on the next load.
        // A fresh chunk mapping with every remaining object dirty makes the next save write only what is live; the old
        // chunk files are orphans then, and vanilla's load removes them.
        private static void ResetChunkStore(ZDOMan zdoMan)
        {
            ChunkSaveMappingRef(zdoMan) = new ChunkSaveMapping();
            foreach (var zdo in ObjectsByIdRef(zdoMan).Values) zdoMan.SetDirtySector(zdo);
            DirtyPortalObjectsSetter.Invoke(zdoMan, TrueArgument);
        }

        private static int CountBuiltPieces(List<ZDO> zdos)
        {
            int count = 0;
            foreach (var zdo in zdos)
                if (ZoneModeBuilds.IsBuilt(zdo)) count++;
            return count;
        }

        private static int LocationHashAt(ZoneSystem zoneSystem, Vector2s zone)
            => zoneSystem.m_locationInstances.TryGetValue(zone, out var instance) && instance.m_placed && instance.m_location != null
                ? ZoneModeSwaps.NameOf(instance.m_location).GetStableHashCode()
                : 0;

        private static string Sample(List<Vector2s> zones)
        {
            var parts = new List<string>();
            for (int i = 0; i < zones.Count && i < LoggedZoneSample; i++) parts.Add($"({zones[i].x},{zones[i].y})");
            return string.Join(" ", parts) + (zones.Count > LoggedZoneSample ? " ..." : "");
        }
    }
}
