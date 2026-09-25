using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // A world cloned into one zone at a time keeps everything it had. At its first server load every generated zone is
    // PARKED: its objects are written in vanilla's own save format to FiresWorldGen\firesgen_<world>_park\<x>_<y>.zdos and
    // leave the live world, and the zone stops being generated, so it is closed sea like any other. Opening a parked zone
    // RESTORES it: the objects load back the way a world load brings them (new ids, links re-made from their saved hashes)
    // and the zone is marked generated, so the land returns as it was, builds and all. Only a seed planted while wearing
    // the Eye of the Beholder, onto land it does not match, raises other land there and discards what was parked.
    // A restored zone's file waits as <x>_<y>.restored until a load finds the zone saved as generated; a load that finds
    // it not generated (the session ended before a save) restores it again.
    internal static class ZoneModePark
    {
        internal struct ParkedZone
        {
            internal int Pieces;
            internal int LocationHash;
        }

        internal sealed class ClientState
        {
            internal ZoneOpenSet Zones;
            internal readonly Dictionary<Vector2s, ParkedZone> Details = new Dictionary<Vector2s, ParkedZone>();
        }

        private struct ZoneFileHeader
        {
            internal long WorldUid;
            internal global::Version.World WorldVersion;
            internal ParkedZone Parked;
            internal int Objects;
            internal int Portals;
        }

        private const string FolderSuffix = "_park";
        private const string MarkerSuffix = ".convert";
        private const string ParkedExtension = ".zdos";
        private const string RestoredExtension = ".restored";
        private const string UnreadableExtension = ".unreadable";
        private const string IndexFileName = "index.bin";
        private const string StagingSuffix = ".staging";
        private const string RejectedSuffix = ".rejected";
        private const string TempSuffix = ".tmp";
        private const string MarkerPark = "park";
        private const string MarkerParked = "parked";
        private const string MarkerFailed = "failed";
        private const int MarkerStateLine = 0;
        private const int MarkerUidLine = 1;
        private const int MarkerSourceLine = 2;
        private const uint ZoneFileMagic = 0x445A5046u;
        private const uint IndexFileMagic = 0x58495046u;
        private const byte FileFormat = 1;
        private const int FileBufferBytes = 1 << 20;
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
        private static World s_world;
        private static bool s_server;
        private static bool s_converted;
        private static bool s_restoredFilesWaiting;
        private static string s_marker;
        private static ClientState s_client;

        internal static bool Converted => s_converted;

        internal static int ParkedCount => s_index.Count;

        internal static bool WaitsForObjects
            => s_server && (s_marker == MarkerPark || s_marker == MarkerParked || s_index.Count > 0 || s_restoredFilesWaiting);

        internal static string FolderFor(string worldName) => FiresWorldRecords.PathFor(worldName, FolderSuffix);

        internal static string MarkerPathFor(string worldName) => FiresWorldRecords.PathFor(worldName, MarkerSuffix);

        internal static void Reset()
        {
            s_index.Clear();
            s_world = null;
            s_server = false;
            s_converted = false;
            s_restoredFilesWaiting = false;
            s_marker = null;
            s_client = null;
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
            LoadIndex(world);
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
            SettleRestoredFiles(zoneSystem);
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

        private static void ParkEverything(ZoneSystem zoneSystem)
        {
            var clock = Stopwatch.StartNew();
            var zdoMan = ZDOMan.instance;
            var generated = GeneratedZonesRef(zoneSystem);
            string folder = FolderFor(s_world.m_name);
            string staging = folder + StagingSuffix;
            var parked = new Dictionary<Vector2s, List<ZDO>>();
            var index = new Dictionary<Vector2s, ParkedZone>();
            int beyondGrid = 0;
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                ZDOExtraData.PrepareSave();
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
                        CollectZone(zdoMan, zone, objects, portals);
                        var entry = new ParkedZone
                        {
                            Pieces = CountBuiltPieces(objects) + CountBuiltPieces(portals),
                            LocationHash = LocationHashAt(zoneSystem, zone),
                        };
                        WriteZoneFile(ZoneFile(staging, zone, ParkedExtension), entry, objects, portals);
                        ZoneModeLandmarks.Record(zone, objects, portals);
                        index[zone] = entry;
                        objects.AddRange(portals);
                        parked[zone] = objects;
                    }
                }
                finally { ZDOExtraData.ClearSave(); }
                WriteIndex(staging, index);
                ZoneModeLandmarks.Write(staging);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Directory.Move(staging, folder);
            }
            catch (Exception ex)
            {
                WriteMarkerState(MarkerFailed);
                FiresLogger.LogError($"[FiresZones] could not park '{s_world.m_name}' ({ex.GetType().Name}: {ex.Message}). Nothing was "
                    + "removed: every zone it has explored stays open, as in a normal world.");
                return;
            }

            int objectCount = 0, pieceCount = 0;
            foreach (var pair in parked)
            {
                RemoveFromLiveWorld(zdoMan, pair.Key, pair.Value);
                generated.Remove(pair.Key);
                objectCount += pair.Value.Count;
                pieceCount += index[pair.Key].Pieces;
            }
            ResetChunkStore(zdoMan);
            foreach (var pair in index) s_index[pair.Key] = pair.Value;
            WriteMarkerState(MarkerParked);
            FiresLogger.LogInfo($"[FiresZones] PARKED '{s_world.m_name}': {parked.Count} explored zone(s) holding {objectCount:N0} "
                + $"object(s) ({pieceCount:N0} built piece(s)) are set aside in {Path.GetFileName(folder)} "
                + $"({clock.ElapsedMilliseconds} ms). Each returns as it was when its zone is opened."
                + (beyondGrid > 0 ? $" {beyondGrid} explored zone(s) beyond the zone grid (±{ZoneOpenSet.ZoneExtent}) stay as they are." : ""));
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
        }

        private static void SettleRestoredFiles(ZoneSystem zoneSystem)
        {
            string folder = FolderFor(s_world.m_name);
            if (!Directory.Exists(folder)) return;
            var generated = GeneratedZonesRef(zoneSystem);
            bool indexChanged = false;
            foreach (string path in Directory.GetFiles(folder, "*" + RestoredExtension))
            {
                if (!TryParseZone(path, out var zone)) continue;
                if (generated.Contains(zone))
                {
                    File.Delete(path);
                    continue;
                }
                if (ZoneModeRegistry.IsOpen(zone))
                {
                    RestoreFromFile(zone, path, zoneSystem, "its return was never saved");
                    continue;
                }
                string parkedPath = ZoneFile(folder, zone, ParkedExtension);
                if (!TryMove(path, parkedPath)) continue;
                s_index[zone] = ReadHeader(parkedPath).Parked;
                indexChanged = true;
            }
            s_restoredFilesWaiting = false;
            if (indexChanged) WriteIndexSafely();
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
            if (!s_server || s_index.Count == 0) return 0;
            var zoneSystem = ZoneSystem.instance;
            string folder = FolderFor(s_world.m_name);
            int restored = 0;
            bool indexChanged = false;
            foreach (var zone in zones)
            {
                if (!s_index.Remove(zone)) continue;
                indexChanged = true;
                string path = ZoneFile(folder, zone, ParkedExtension);
                if (!RestoreFromFile(zone, path, zoneSystem, context)) continue;
                TryMove(path, ZoneFile(folder, zone, RestoredExtension));
                restored++;
            }
            if (indexChanged) WriteIndexSafely();
            return restored;
        }

        private static bool RestoreFromFile(Vector2s zone, string path, ZoneSystem zoneSystem, string context)
        {
            var restored = new List<ZDO>();
            ZoneFileHeader header;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferBytes))
                using (var reader = new BinaryReader(stream))
                {
                    var pkg = new ZPackage();
                    pkg.SetReader(reader);
                    header = ReadHeader(pkg);
                    int total = header.Objects + header.Portals;
                    for (int i = 0; i < total; i++)
                    {
                        var zdo = ZDOPool.Create();
                        restored.Add(zdo);
                        zdo.Load(pkg, header.WorldVersion);
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (var zdo in restored) ZDOPool.Release(zdo);
                string kept = Path.ChangeExtension(path, UnreadableExtension);
                TryMove(path, kept);
                FiresLogger.LogError($"[FiresZones] could not bring ({zone.x},{zone.y}) back as it was ({ex.GetType().Name}: {ex.Message}), "
                    + $"so it rises as new land instead. The file is kept as {Path.GetFileName(kept)}.");
                return false;
            }
            var zdoMan = ZDOMan.instance;
            AddToLiveWorld(zdoMan, restored, header.Objects);
            ConnectRestored(restored);
            ZoneModeLandmarks.OnRestored(zone, restored, header.Objects);
            GeneratedZonesRef(zoneSystem).Add(zone);
            FiresLogger.LogInfo($"[FiresZones] ({zone.x},{zone.y}) returned as it was: {restored.Count:N0} object(s), "
                + $"{header.Parked.Pieces:N0} built piece(s) ({context}).");
            return true;
        }

        // ── What a park holds, read without bringing it back ─────────────────
        internal static List<Vector2s> ParkedZoneList() => new List<Vector2s>(s_index.Keys);

        // One parked zone, loaded one object at a time for `visit` and released again; `visit`'s second argument says
        // the object is a portal. Returns how many objects were read, or -1 if the file could not be read.
        internal static int TryReadZone(Vector2s zone, Action<ZDO, bool> visit)
        {
            if (!s_server || s_world == null) return -1;
            string path = ZoneFile(FolderFor(s_world.m_name), zone, ParkedExtension);
            if (!File.Exists(path)) return -1;
            int read = 0;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferBytes))
                using (var reader = new BinaryReader(stream))
                {
                    var pkg = new ZPackage();
                    pkg.SetReader(reader);
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
        // `visit` counted.
        internal static int ForEachParkedObject(Func<ZDO, bool> visit)
        {
            if (!s_server || s_index.Count == 0) return 0;
            string folder = FolderFor(s_world.m_name);
            int counted = 0;
            foreach (var zone in s_index.Keys)
            {
                string path = ZoneFile(folder, zone, ParkedExtension);
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferBytes))
                    using (var reader = new BinaryReader(stream))
                    {
                        var pkg = new ZPackage();
                        pkg.SetReader(reader);
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
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresZones] could not read parked zone ({zone.x},{zone.y}) ({ex.GetType().Name}: {ex.Message}).");
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

        // ── Raising other land over a parked zone ───────────────────────────
        internal static void Discard(Vector2s zone, string who)
        {
            if (!s_server || !s_index.TryGetValue(zone, out var parked)) return;
            s_index.Remove(zone);
            string path = ZoneFile(FolderFor(s_world.m_name), zone, ParkedExtension);
            try { File.Delete(path); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] could not delete {Path.GetFileName(path)}: {ex.Message}"); }
            WriteIndexSafely();
            FiresLogger.LogInfo($"[FiresZones] ({zone.x},{zone.y}) was raised as other land by {who}; what was parked there "
                + $"({parked.Pieces:N0} built piece(s)) is gone.");
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

        // A seed matches a parked zone when it would raise that zone's own land: its planned biome, or the place it held.
        internal static bool SeedMatches(ChunkSeed seed, Vector2s zone, ParkedZone parked)
        {
            if (seed.IsLocation) return parked.LocationHash != 0 && parked.LocationHash == seed.Location.GetStableHashCode();
            ZoneModeChunkTiers.ZoneTier(zone, out var planned);
            return planned == seed.Tier.Biome;
        }

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
                if (pair.Value.Pieces > 0 || pair.Value.LocationHash != 0) detailed.Add(pair);
            pkg.Write(detailed.Count);
            foreach (var pair in detailed)
            {
                pkg.Write(pair.Key);
                pkg.Write(pair.Value.Pieces);
                pkg.Write(pair.Value.LocationHash);
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
                state.Details[zone] = new ParkedZone { Pieces = pkg.ReadInt(), LocationHash = pkg.ReadInt() };
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

        // ── Index: <park>\index.bin ─────────────────────────────────────────
        private static void LoadIndex(World world)
        {
            string folder = FolderFor(world.m_name);
            if (!Directory.Exists(folder)) return;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(Path.Combine(folder, IndexFileName))))
                {
                    if (reader.ReadUInt32() != IndexFileMagic || reader.ReadByte() != FileFormat) throw new InvalidDataException("not a park index");
                    long uid = reader.ReadInt64();
                    if (uid != world.m_uid)
                    {
                        SetAside(folder, $"it belongs to world uid {uid}, a deleted world that had this name");
                        return;
                    }
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var zone = new Vector2s(reader.ReadInt16(), reader.ReadInt16());
                        s_index[zone] = new ParkedZone { Pieces = reader.ReadInt32(), LocationHash = reader.ReadInt32() };
                    }
                }
            }
            catch (Exception ex)
            {
                s_index.Clear();
                if (!TryRebuildIndex(world, folder)) return;
                FiresLogger.LogWarning($"[FiresZones] the park index of '{world.m_name}' was unreadable ({ex.Message}); rebuilt from its "
                    + $"{s_index.Count} parked zone file(s).");
                WriteIndexSafely();
            }
            s_restoredFilesWaiting = Directory.GetFiles(folder, "*" + RestoredExtension).Length > 0;
        }

        private static bool TryRebuildIndex(World world, string folder)
        {
            foreach (string path in Directory.GetFiles(folder, "*" + ParkedExtension))
            {
                if (!TryParseZone(path, out var zone)) continue;
                var header = ReadHeader(path);
                if (header.WorldUid != world.m_uid)
                {
                    s_index.Clear();
                    SetAside(folder, $"its zones belong to world uid {header.WorldUid}, a deleted world that had this name");
                    return false;
                }
                s_index[zone] = header.Parked;
            }
            return true;
        }

        private static void WriteIndex(string folder, Dictionary<Vector2s, ParkedZone> index)
        {
            string path = Path.Combine(folder, IndexFileName);
            string temp = path + TempSuffix;
            using (var writer = new BinaryWriter(File.Create(temp)))
            {
                writer.Write(IndexFileMagic);
                writer.Write(FileFormat);
                writer.Write(s_world.m_uid);
                writer.Write(index.Count);
                foreach (var pair in index)
                {
                    writer.Write((short)pair.Key.x);
                    writer.Write((short)pair.Key.y);
                    writer.Write(pair.Value.Pieces);
                    writer.Write(pair.Value.LocationHash);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static void WriteIndexSafely()
        {
            try { WriteIndex(FolderFor(s_world.m_name), s_index); }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] could not write the park index of '{s_world.m_name}': {ex.Message}"); }
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

        private static void WriteZoneFile(string path, ParkedZone entry, List<ZDO> objects, List<ZDO> portals)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferBytes))
            using (var writer = new BinaryWriter(stream))
            {
                var pkg = new ZPackage();
                pkg.SetWriter(writer);
                pkg.Write(ZoneFileMagic);
                pkg.Write(FileFormat);
                pkg.Write(s_world.m_uid);
                pkg.Write((short)global::Version.c_WorldVersion);
                pkg.Write(entry.Pieces);
                pkg.Write(entry.LocationHash);
                pkg.Write(objects.Count);
                pkg.Write(portals.Count);
                foreach (var zdo in objects) zdo.Save(pkg);
                foreach (var zdo in portals) zdo.Save(pkg);
            }
        }

        private static ZoneFileHeader ReadHeader(ZPackage pkg)
        {
            if (pkg.ReadUInt() != ZoneFileMagic) throw new InvalidDataException("not a parked zone");
            byte format = pkg.ReadByte();
            if (format != FileFormat) throw new InvalidDataException($"parked zone format {format}, this build reads {FileFormat}");
            return new ZoneFileHeader
            {
                WorldUid = pkg.ReadLong(),
                WorldVersion = (global::Version.World)pkg.ReadShort(),
                Parked = new ParkedZone { Pieces = pkg.ReadInt(), LocationHash = pkg.ReadInt() },
                Objects = pkg.ReadInt(),
                Portals = pkg.ReadInt(),
            };
        }

        private static ZoneFileHeader ReadHeader(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                var pkg = new ZPackage();
                pkg.SetReader(reader);
                return ReadHeader(pkg);
            }
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

        private static string ZoneFile(string folder, Vector2s zone, string extension)
            => Path.Combine(folder, zone.x.ToString(CultureInfo.InvariantCulture) + "_" + zone.y.ToString(CultureInfo.InvariantCulture) + extension);

        private static bool TryParseZone(string path, out Vector2s zone)
        {
            zone = default;
            string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return false;
            zone = new Vector2s(x, y);
            return true;
        }

        private static bool TryMove(string from, string to)
        {
            try
            {
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] could not rename {Path.GetFileName(from)} to {Path.GetFileName(to)}: {ex.Message}");
                return false;
            }
        }

        private static string Sample(List<Vector2s> zones)
        {
            var parts = new List<string>();
            for (int i = 0; i < zones.Count && i < LoggedZoneSample; i++) parts.Add($"({zones[i].x},{zones[i].y})");
            return string.Join(" ", parts) + (zones.Count > LoggedZoneSample ? " ..." : "");
        }
    }
}
