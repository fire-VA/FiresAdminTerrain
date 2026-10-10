using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    internal enum ZoneOpenReason : byte
    {
        Start = 1,
        Bought = 2,
        LocationReveal = 3,
        Admin = 4,
        SaveHeal = 5,
        PortalPartner = 6,
        Wayshrine = 7,
        Quest = 8,       // a quest reward opened it (FiresZonesApi.OpenForQuest / fires_chunk_open)
        FullWorld = 9,   // open since the world was made closeable (fires_zones_make_closeable): every zone of a full world
    }

    // ── THE OPEN-ZONE REGISTRY ──────────────────────────────────────────────
    // One per zone-mode world, owned by the server. Its FILE is the mode switch: a world is played
    // one zone at a time exactly when FiresWorldGen\firesgen_<world>_zones.bin exists, written by the
    // creation popup. Every entry records why its zone opened (start area, bought, location reveal,
    // admin, or healed from the save).
    //
    // The file is bound to its world by UID and seed. A mismatched UID means the file belongs to a
    // deleted world of the same name: it is set aside and this world is NOT zone mode. Any other
    // defect fails CLOSED: the world stays zone mode and its open set is rebuilt from the save, since
    // only open zones are ever generated. Every load also re-checks that invariant, so a generated
    // zone missing from the registry is re-opened and reported instead of vanishing under the sea.
    //
    // Clients never read or write the file. They receive the open set inside the connection
    // handshake, before the server's PeerInfo, so it is known before their worldgen starts.
    internal static class ZoneModeRegistry
    {
        private const uint FileMagic = 0x4E4F5A46u;
        private const byte FileFormat = 1;
        private const byte SectionOpenZones = 1;
        private const byte SectionZoneSources = 2;
        private const byte SectionStartCave = 3;
        // Written only when a swapped zone carries a shift (ZoneShift: its lift and its location centring), so a world
        // without one reads in any build.
        private const byte SectionZoneShifts = 4;
        // Zones an admin CLOSED (0.2.718): only a Chunk Generator seed, an admin or a quest opens them, never an automatic
        // opener (location reveal, portal partner, wayshrine, save heal). Written only when one exists, so a world without
        // one reads in any build.
        private const byte SectionLockedZones = 5;
        private const int ShiftEntryBytes = 2 + 2 + 4 + 4 + 4;
        private const byte FlagSeeded = 1;
        private const int HeaderBytes = 4 + 1 + 1 + 8 + 4 + 4 + 8 + 1;
        private const int SectionHeaderBytes = 1 + 4;
        private const int ZoneEntryBytes = 2 + 2 + 1 + 8 + 8;
        private const int SourceEntryBytes = 2 + 2 + 2 + 2;
        private const int MaxZoneEntries = (ZoneOpenSet.ZoneExtent * 2 + 1) * (ZoneOpenSet.ZoneExtent * 2 + 1);
        private const string FileSuffix = "_zones.bin";
        private const string TempSuffix = ".tmp";
        private const string RejectedSuffix = ".rejected";
        private const string DefaultStartLocation = "StartTemple";
        private const float WaterLevel = 30f;
        private const byte HandshakeProtocol = 8;
        private const int HealSampleCount = 8;

        private struct ZoneEntry
        {
            public ZoneOpenReason Reason;
            public long OpenedUtcTicks;
            public long ActorId;
            public bool Swapped;
            public Vector2s Source;
            public ZoneShift Shift;
        }

        private sealed class HandshakeState
        {
            public long WorldUid;
            public bool ZoneMode;
            public bool Seeded;
            public int Version;
            public float CoastWidth;
            public float SeaFloorY;
            public int OpenCount;
            public ulong[] Words;
            public List<KeyValuePair<Vector2s, Vector2s>> Sources;
            public List<KeyValuePair<Vector2s, ZoneShift>> Shifts;
            public ZoneModeStartCave.Site StartCave;
            public ZoneModePark.ClientState Park;
            public Voxel.FiresWorldProfile Profile;
            public Voxel.FiresWaterGuards.ClientState WaterGuards;
        }

        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZonesRef =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");

        private static readonly Dictionary<Vector2s, ZoneEntry> s_entries = new Dictionary<Vector2s, ZoneEntry>();
        private static readonly HashSet<Vector2s> s_locked = new HashSet<Vector2s>();

        internal static bool IsLocked(Vector2s zone) => s_locked.Contains(zone);

        // A reason that may open a locked zone: the ones a player or an admin chooses on purpose.
        private static bool OpensLocked(ZoneOpenReason reason)
            => reason == ZoneOpenReason.Bought || reason == ZoneOpenReason.Admin || reason == ZoneOpenReason.Quest;

        // Server: zones an admin closed stay closed until a Chunk Generator seed, an admin or a quest opens them.
        internal static void Lock(IEnumerable<Vector2s> zones)
        {
            if (!s_modeActive || !s_authority) return;
            bool changed = false;
            foreach (var zone in zones) changed |= s_locked.Add(zone);
            if (!changed) return;
            s_version++;
            Save();
        }
        private static World s_world;
        private static bool s_modeActive;
        private static bool s_seeded;
        private static bool s_authority;
        private static bool s_recovering;
        private static bool s_seedingSubscribed;
        private static bool s_settlesAfterReplan;
        private static int s_version;
        private static long s_createdUtcTicks;
        private static float s_coastWidth;
        private static float s_seaFloorY;
        private static ZoneOpenSet s_clientOpenSet;
        private static HandshakeState s_pendingHandshake;
        private static bool s_awaitingMirror;

        internal static bool ModeActive => s_modeActive;
        internal static bool Seeded => s_seeded;

        // True once ZNet.Start has loaded the world save (a server loads it there, after this world's
        // generator initialised in Awake) - server work that reads or writes ZDOs waits for it.
        internal static bool WorldLoaded { get; private set; }

        // The save's objects are loaded and no terrain is built yet: a zone world whose registry file went missing is
        // restored from the copy in its save, a world with parked zones settles them and only then checks its save, and
        // every zone world's copy is brought up to date.
        // An older save has its locations placed again first (ZNet.ServerLoadWorld). Parking before that would show
        // Valheim every explored zone as never generated, and it would plan new locations into land that returns as it
        // was and never spawns them, so a world that is being re-planned parks when the plan is finished.
        internal static void MarkWorldLoaded()
        {
            WorldLoaded = true;
            if (!s_authority || s_world == null || FiresWorldLoadGuard.Refused) return;
            if (s_awaitingMirror) RestoreFromMirror();
            if (ZoneModePark.WaitsForObjects)
            {
                var zoneSystem = ZoneSystem.instance;
                if (zoneSystem != null && !zoneSystem.LocationsGenerated)
                {
                    s_settlesAfterReplan = true;
                    zoneSystem.GenerateLocationsCompleted += SettleAfterReplan;
                    FiresLogger.LogInfo($"[FiresZones] '{s_world.m_name}' is an older save whose locations Valheim places again now; "
                        + "its explored zones are set aside once that is done, so no new location lands in them.");
                    return;
                }
                SettleParkAndCheck(zoneSystem);
            }
            if (s_modeActive) ZoneModeRegistryMirror.Write(CurrentBytes(), s_world.m_uid);
        }

        private static void SettleAfterReplan()
        {
            s_settlesAfterReplan = false;
            if (!s_authority || s_world == null) return;
            SettleParkAndCheck(ZoneSystem.instance);
            ZoneModePark.ReportUnspawnedLocations(ZoneSystem.instance);
            if (s_modeActive) ZoneModeRegistryMirror.Write(CurrentBytes(), s_world.m_uid);
        }

        private static void SettleParkAndCheck(ZoneSystem zoneSystem)
        {
            try { ZoneModePark.OnObjectsLoaded(zoneSystem, s_modeActive); }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] parked zones could not be settled: {ex}"); }
            if (s_modeActive) CheckGeneratedZones(zoneSystem);
        }

        private static void RestoreFromMirror()
        {
            s_awaitingMirror = false;
            var world = s_world;
            if (!ZoneModeRegistryMirror.TryRead(world.m_uid, out byte[] bytes)) return;
            string file = Path.GetFileName(RegistryPath(world.m_name));
            if (!TryParse(bytes, world, out string failure, out _))
            {
                FiresLogger.LogError($"[FiresZones] '{world.m_name}' has no {file}, and the copy of it inside the save is unreadable "
                    + $"({failure}). The world plays as a normal world.");
                return;
            }
            s_modeActive = true;
            Publish();
            if (!TryWriteAtomically(RegistryPath(world.m_name), bytes, out failure))
                FiresLogger.LogError($"[FiresZones] could not write the restored {file} ({failure}); the copy inside the save stays the record.");
            FiresLogger.LogWarning($"[FiresZones] '{world.m_name}' had no {file}, but its save carries a copy: restored "
                + $"{s_entries.Count} open zone(s), registry v{s_version}. The world stays ONE ZONE AT A TIME.");
            ZoneModeSkylands.OnWorldInitialized(world);
            OnSaveLoaded(ZoneSystem.instance);
        }
        internal static bool IsAuthority => s_authority;
        internal static int Version => s_version;
        internal static float CoastWidth => s_coastWidth;
        internal static float SeaFloorY => s_seaFloorY;
        internal static string WorldName => s_world?.m_name;

        internal static int OpenCount => s_authority ? s_entries.Count : (s_clientOpenSet?.OpenCount ?? 0);

        // Server: a zone vanilla may populate. Unseeded zone-mode worlds populate nothing, and a zone
        // that just opened waits until every peer shows its new terrain.
        internal static bool IsOpenForPopulation(Vector2s zone)
            => s_seeded && s_entries.ContainsKey(zone) && !ZoneModeOpening.IsAwaitingPeers(zone);

        internal static bool IsOpen(Vector2s zone)
        {
            if (!s_modeActive) return true;
            if (s_authority) return s_entries.ContainsKey(zone);
            return s_clientOpenSet != null && s_clientOpenSet.IsOpen(zone);
        }

        // True when any of the zone's points shows real terrain: it is open, or it shares an edge or
        // corner with an open zone (those shared points read real).
        internal static bool HasRealTerrain(Vector2s zone)
        {
            if (!s_modeActive) return true;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (IsOpen(new Vector2s(zone.x + dx, zone.y + dy))) return true;
            return false;
        }

        internal const string LaunchFlag = "-fireszones";

        // A dedicated server has no creation popup: this flag makes a world it has never saved a
        // zone-mode world. An existing, played world is never converted.
        internal static bool LaunchFlagPresent()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, LaunchFlag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The flag decides only for a world with no profile chosen for it; a chosen profile says it itself.
        internal static bool LaunchFlagDecides(Voxel.FiresWorldProfile profile)
            => LaunchFlagPresent() && (profile == null || !profile.Chosen);

        internal static string RegistryPath(string worldName)
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(worldName, FileSuffix);
        }

        // ── World lifecycle ─────────────────────────────────────────────────
        internal static void OnWorldInitialize(World world)
        {
            ResetState();
            if (world == null || world.m_menu)
            {
                s_pendingHandshake = null;
                Voxel.FiresWorldProfile.Clear();
                return;
            }
            var net = ZNet.instance;
            if (net == null) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Voxel.FiresWorldProfile.OnWorldInitialize(world);
            long profileMs = clock.ElapsedMilliseconds;
            Voxel.FiresWaterGuards.OnWorldInitialize(world, net.IsServer());
            long guardsMs = clock.ElapsedMilliseconds;
            ZoneModePark.OnWorldInitialize(world, net.IsServer());
            long parkMs = clock.ElapsedMilliseconds;
            if (net.IsServer()) InitializeAuthoritative(world);
            else BindHandshake(world);
            if (!net.IsServer()) return;
            // A server's first start after a reboot met a cold file cache here: 10.5 s (R9a, 2026-09-28). Which step, is this line.
            FiresLogger.LogInfo($"[FiresZones] world init: profile {profileMs} ms, water guards {guardsMs - profileMs} ms, park "
                + $"{parkMs - guardsMs} ms ({ZoneModePark.ParkedCount:N0} parked in {ZoneModePark.StoreDescription}; index "
                + $"{ZoneModePark.IndexReadMs} ms" + (ZoneModePark.RepackMs > 0 ? $", one-time repack {ZoneModePark.RepackMs:N0} ms" : "")
                + $"), registry {clock.ElapsedMilliseconds - parkMs} ms.");
        }

        internal static void OnWorldTeardown()
        {
            ResetState();
            s_pendingHandshake = null;
        }

        private static void ResetState()
        {
            s_handshake = null;
            s_entries.Clear();
            s_locked.Clear();
            WorldLoaded = false;
            s_world = null;
            s_modeActive = false;
            s_seeded = false;
            s_authority = false;
            s_recovering = false;
            s_seedingSubscribed = false;
            s_settlesAfterReplan = false;
            s_version = 0;
            s_createdUtcTicks = 0;
            s_clientOpenSet = null;
            s_awaitingMirror = false;
            ZoneModeStartCave.Set(null);
            ZoneModePark.Reset();
            ZoneModeChunkBlueprint.Reset();
            ZoneModeChunkTools.Reset();
            ZoneModeChaosHammer.Reset();
            Voxel.FiresWaterGuards.Reset();
            ZoneModeRegistryMirror.Reset();
            ZoneModeGate.ClearView();
            ZoneModeDiagnostics.Reset();
        }

        // Why zone mode stood down for this world's size (0.2.721), shown once to the local player when they spawn.
        private static string s_sizeRefusal;

        internal static void OnPlayerSpawned(Player player)
        {
            if (s_sizeRefusal == null || player == null || player != Player.m_localPlayer) return;
            player.Message(MessageHud.MessageType.Center, s_sizeRefusal, 0, null, false);
            s_sizeRefusal = null;
        }

        private static void InitializeAuthoritative(World world)
        {
            s_sizeRefusal = null;
            s_world = world;
            s_authority = true;
            s_coastWidth = ZoneModeConfig.CoastWidth?.Value ?? 1f;
            s_seaFloorY = WaterLevel - (ZoneModeConfig.SeaFloorDepth?.Value ?? 50f);

            string path = RegistryPath(world.m_name);
            string file = Path.GetFileName(path);
            // 0.2.721 (Fire 10-05: "Refuse for now, widen later"): the open-zone grid covers +-ZoneExtent zones, about
            // 10.9 km, and its length is part of the handshake and the registry. A world whose edge lies beyond it (Expand
            // World Size) plays as a normal world: nothing is created, loaded or changed on disk, so the registry of a
            // world that once was zone-mode stays as it was.
            float gridReach = (ZoneOpenSet.ZoneExtent + 0.5f) * ZoneOpenSet.ZoneSize;
            if (Voxel.FatWorldSize.EdgeRadius > gridReach)
            {
                s_sizeRefusal = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "This world reaches {0:0} m from its centre; one zone at a time supports worlds up to {1:0} m. It plays as a normal world.",
                    Voxel.FatWorldSize.EdgeRadius, gridReach);
                FiresLogger.LogWarning($"[FiresZones] '{world.m_name}': {s_sizeRefusal} ({Voxel.FatWorldSize.Describe()}; "
                    + $"{(File.Exists(path) ? $"{file} left untouched" : "no zone registry")}.)");
                return;
            }
            bool profiled = Voxel.FiresWorldProfile.Current != null && Voxel.FiresWorldProfile.Current.ZoneMode;
            if (!File.Exists(path) && (LaunchFlagDecides(Voxel.FiresWorldProfile.Current) || profiled) && IsNeverSaved(world))
            {
                if (CreateForNewWorld(world, out _))
                    FiresLogger.LogInfo($"[FiresZones] {(profiled ? "its profile" : LaunchFlag)}: '{world.m_name}' has never been saved, so it "
                        + "becomes a ONE ZONE AT A TIME world.");
            }
            if (!File.Exists(path))
            {
                s_awaitingMirror = true;
                FiresLogger.LogVerbose($"[FiresZones] '{world.m_name}' has no zone registry — played as a normal world unless its "
                    + "save carries a copy.");
                return;
            }

            if (TryLoad(path, world, out string failure, out bool belongsToAnotherWorld))
            {
                s_modeActive = true;
                if (s_seeded) Publish();
                FiresLogger.LogInfo($"[FiresZones] '{world.m_name}' is a ONE ZONE AT A TIME world — "
                    + (s_seeded ? $"{s_entries.Count} zone(s) open ({DescribeReasons()}), registry v{s_version}."
                                : "not seeded yet; the start area opens when location placement finishes.")
                    + $" Coast {s_coastWidth:0.#} m, sea floor y={s_seaFloorY:0.#}. ({file})");
                return;
            }

            Quarantine(path);
            if (belongsToAnotherWorld)
            {
                s_awaitingMirror = true;
                FiresLogger.LogWarning($"[FiresZones] {file} belongs to a deleted world that had this name ({failure}). "
                    + $"Set aside as {file}{RejectedSuffix}; '{world.m_name}' is played as a normal world unless its save "
                    + "carries a copy of its own registry.");
                return;
            }

            s_modeActive = true;
            s_seeded = true;
            s_recovering = true;
            s_version = 1;
            s_createdUtcTicks = DateTime.UtcNow.Ticks;
            Publish();
            FiresLogger.LogError($"[FiresZones] the zone registry {file} is unreadable ({failure}); set aside as "
                + $"{file}{RejectedSuffix}. '{world.m_name}' STAYS a zone-mode world: every zone is closed until the "
                + "save loads, then every zone the save has generated is re-opened. Zones that were opened but never "
                + "visited are lost and must be opened again.");
        }

        private static readonly string[] SaveDataExtensions = { ".db", ".db2", ".chunk", ".chunks" };

        // m_needsDB alone is not proof: worlds older than the NeedsDB format read it as false even
        // though they have saves. So the world must also have no save data on disk at all.
        // Also the one authority for "is this world being created" - FiresWorldProfile asks it before letting a config
        // decide a world's generator, because only a world with no save data has no ground to contradict.
        internal static bool IsNeverSaved(World world)
        {
            if (world.m_needsDB || File.Exists(world.GetDBPath())) return false;
            try
            {
                string dir = world.GetSaveDirectory(world.m_fileSource);
                if (!Directory.Exists(dir)) return true;
                foreach (string file in Directory.GetFiles(dir))
                    if (Array.IndexOf(SaveDataExtensions, Path.GetExtension(file).ToLowerInvariant()) >= 0) return false;
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] could not inspect '{world.m_name}''s save folder ({ex.Message}); it is "
                    + $"treated as a world that HAS been saved, so {LaunchFlag} is ignored for it and no config may choose "
                    + "its generator.");
                return false;
            }
        }

        // A new world created with the popup's "yes". Runs at the main menu, outside any world.
        internal static bool CreateForNewWorld(World world, out string path)
        {
            path = RegistryPath(world.m_name);
            var entries = new Dictionary<Vector2s, ZoneEntry>();
            byte[] bytes = Serialize(world.m_uid, world.m_seed, seeded: false, version: 0,
                createdUtcTicks: DateTime.UtcNow.Ticks, entries);
            return TryWriteAtomically(path, bytes, out _);
        }

        // A FULL world made closeable (fires_zones_make_closeable, 0.2.718): a registry with every zone of the grid open
        // (FullWorld), seeded, so from its next load it is a zone-mode world where nothing is closed until an admin closes it.
        // Nothing else changes: open zones are played as they are, and an older FiresAdminTerrain reads the file the same.
        internal static bool WriteFullOpenRegistry(World world, out string path, out int zones, out string failure)
        {
            path = RegistryPath(world.m_name);
            zones = 0;
            failure = null;
            if (File.Exists(path)) { failure = $"{Path.GetFileName(path)} already exists: the world is a zone-mode world already"; return false; }
            var entries = new Dictionary<Vector2s, ZoneEntry>();
            long now = DateTime.UtcNow.Ticks;
            for (int y = -ZoneOpenSet.ZoneExtent; y <= ZoneOpenSet.ZoneExtent; y++)
                for (int x = -ZoneOpenSet.ZoneExtent; x <= ZoneOpenSet.ZoneExtent; x++)
                    entries[new Vector2s(x, y)] = new ZoneEntry { Reason = ZoneOpenReason.FullWorld, OpenedUtcTicks = now };
            zones = entries.Count;
            byte[] bytes = Serialize(world.m_uid, world.m_seed, seeded: true, version: 1, createdUtcTicks: now, entries);
            return TryWriteAtomically(path, bytes, out failure);
        }

        // ── Server: save loaded ─────────────────────────────────────────────
        // ZoneSystem's part of the save loads before its objects do; a world with parked zones checks it in
        // MarkWorldLoaded instead, once the parked zones are settled.
        internal static void OnSaveLoaded(ZoneSystem zoneSystem)
        {
            if (!s_modeActive || !s_authority || zoneSystem == null || ZoneModePark.WaitsForObjects) return;
            CheckGeneratedZones(zoneSystem);
        }

        // Zones beyond the zone grid can never open, so the check leaves them as the save has them. Healing never counts as
        // seeding: a world whose start area has not been opened still gets it afterwards.
        private static void CheckGeneratedZones(ZoneSystem zoneSystem)
        {
            var generated = GeneratedZonesRef(zoneSystem);
            int generatedCount = 0;
            int beyondGrid = 0;
            var healed = new List<Vector2s>();
            if (generated != null)
            {
                foreach (var zone in generated)
                {
                    if (!ZoneOpenSet.InRange(zone.x, zone.y))
                    {
                        beyondGrid++;
                        continue;
                    }
                    generatedCount++;
                    if (s_entries.ContainsKey(zone)) continue;
                    s_entries[zone] = new ZoneEntry { Reason = ZoneOpenReason.SaveHeal, OpenedUtcTicks = DateTime.UtcNow.Ticks };
                    healed.Add(zone);
                }
            }
            if (beyondGrid > 0)
                FiresLogger.LogInfo($"[FiresZones] save check: {beyondGrid} generated zone(s) lie beyond the zone grid "
                    + $"(±{ZoneOpenSet.ZoneExtent}); they can never open, so they stay as the save has them.");

            if (healed.Count == 0)
            {
                FiresLogger.LogInfo($"[FiresZones] save check: {generatedCount} generated zone(s), every one of them open — "
                    + "no closed zone has ever been generated.");
                if (s_recovering && s_entries.Count == 0) s_seeded = false;
                SubscribeSeeding();
                return;
            }

            s_version++;
            string sample = string.Join(" ", healed.GetRange(0, Math.Min(HealSampleCount, healed.Count)).ConvertAll(z => $"({z.x},{z.y})"));
            if (s_recovering)
                FiresLogger.LogWarning($"[FiresZones] recovery: re-opened {healed.Count} zone(s) the save has generated: {sample}"
                    + (healed.Count > HealSampleCount ? " ..." : ""));
            else
                FiresLogger.LogError($"[FiresZones] INVARIANT BROKEN: {healed.Count} zone(s) are generated in the save but were "
                    + $"not open: {sample}" + (healed.Count > HealSampleCount ? " ..." : "")
                    + ". Re-opened them so their content stays on land. Something generated them while closed — report this.");
            Save();
            Publish();
            SubscribeSeeding();
        }

        // ── Server: seeding the start area ──────────────────────────────────
        // A loaded save sets ZoneSystem's generated flag directly, so the completion event never fires
        // for it: seed straight away when the plan already exists, otherwise wait for the event once.
        internal static void SubscribeSeeding()
        {
            if (!s_modeActive || !s_authority || s_seeded) return;
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return;
            if (zoneSystem.LocationsGenerated)
            {
                SeedStartArea();
                return;
            }
            if (s_seedingSubscribed) return;
            s_seedingSubscribed = true;
            zoneSystem.GenerateLocationsCompleted += SeedStartArea;
        }

        // An older save's ZoneSystem.LoadOld raises "locations generated" BEFORE it reads the locations, so the start
        // temple is not in the list yet; and a world still to be parked settles first. Either way the seeding goes
        // back to waiting, and the settled load subscribes it again.
        private static bool StartAreaMustWait(ZoneSystem zoneSystem)
        {
            bool locationsNotRead = !WorldLoaded && zoneSystem.m_locationInstances.Count == 0;
            if (!locationsNotRead && !s_settlesAfterReplan) return false;
            s_seedingSubscribed = false;
            FiresLogger.LogInfo(locationsNotRead
                ? "[FiresZones] the start area waits: Valheim reported this save's locations before reading them."
                : "[FiresZones] the start area waits until the explored zones are set aside.");
            return true;
        }

        private static void SeedStartArea()
        {
            if (!s_modeActive || !s_authority || s_seeded) return;
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null || StartAreaMustWait(zoneSystem)) return;

            Vector3 spawn;
            string startName = Game.instance != null && !string.IsNullOrEmpty(Game.instance.m_StartLocation)
                ? Game.instance.m_StartLocation : DefaultStartLocation;
            if (!TryFindLocation(zoneSystem, startName, out spawn))
            {
                spawn = Vector3.zero;
                FiresLogger.LogError($"[FiresZones] no '{startName}' location in this world's plan — seeding the start area "
                    + "around the world centre instead. Players spawn at the start location, so this world may be unplayable.");
            }

            var center = ZoneSystem.GetZone(spawn);
            int radius = ZoneModeConfig.StartAreaRadius?.Value ?? 1;
            long now = DateTime.UtcNow.Ticks;
            var startArea = new List<Vector2s>();
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var zone = new Vector2s(center.x + dx, center.y + dy);
                    if (!ZoneOpenSet.InRange(zone.x, zone.y)) continue;
                    s_entries[zone] = new ZoneEntry { Reason = ZoneOpenReason.Start, OpenedUtcTicks = now };
                    startArea.Add(zone);
                }
            }

            // The start cave is chosen here, inside location placement's planning scope, so it reads the real ground,
            // and before the view is published, so no terrain is ever built without its hill. A converted world's land
            // is already built on, so it gets no hill.
            if (Voxel.FiresVoxelConfig.GeneratesCaves && !ZoneModePark.Converted)
                ZoneModeStartCave.Set(ZoneModeStartCave.Choose(zoneSystem, spawn, center, radius, s_world?.m_seed ?? 0));

            s_seeded = true;
            s_version++;
            int returned = ZoneModePark.RestoreZones(startArea, "the start area");
            Save();
            Publish();
            int side = radius * 2 + 1;
            FiresLogger.LogInfo($"[FiresZones] SEEDED '{s_world?.m_name}': {startName} at ({spawn.x:0},{spawn.z:0}) in zone "
                + $"({center.x},{center.y}); opened the {side}x{side} start area ({s_entries.Count} zone(s))"
                + (returned > 0 ? $", {returned} of them returned as they were" : "") + ". Everything else is sea until it is opened.");
            var cave = ZoneModeStartCave.Current;
            if (cave != null) FiresLogger.LogInfo($"[FiresZones] start cave: {ZoneModeStartCave.Describe(cave)}.");
            else if (ZoneModePark.Converted)
                FiresLogger.LogInfo("[FiresZones] start cave: none, since this world was converted and its land is already built on; "
                    + "the first Chunk Hammer's chest is placed near the spawn and pinned on the map.");
            else if (Voxel.FiresVoxelConfig.GeneratesCaves)
                FiresLogger.LogError("[FiresZones] start cave: no site fits inside the start area; this world has no guaranteed cave, and "
                    + "the first Chunk Hammer's chest stands in the open near the StartTemple and is pinned on the map.");
            else
                FiresLogger.LogWarning("[FiresZones] start cave: caves are off ([VoxelTerrain World] Generate Caves), so this world gets no "
                    + "start cave; the first Chunk Hammer's chest stands in the open near the StartTemple and is pinned on the map.");
            // Seeding runs inside location placement's planning scope; the refresh must see the gate.
            int suspended = ZoneModeGate.SuspendPlanning();
            try { ZoneModeTerrainRefresh.AfterViewChanged("seeded"); }
            finally { ZoneModeGate.ResumePlanning(suspended); }
        }

        private static bool TryFindLocation(ZoneSystem zoneSystem, string name, out Vector3 position)
        {
            foreach (var instance in zoneSystem.m_locationInstances.Values)
            {
                var location = instance.m_location;
                if (location == null) continue;
                if (location.m_prefabName == name || location.m_name == name)
                {
                    position = instance.m_position;
                    return true;
                }
            }
            position = Vector3.zero;
            return false;
        }

        private static void Publish()
        {
            ZoneModeGate.SetView(ZoneOpenSet.Build(s_entries.Keys, s_version, SwappedSources(), EntryShifts()), s_coastWidth, s_seaFloorY);
        }

        private static IEnumerable<KeyValuePair<Vector2s, Vector2s>> SwappedSources()
        {
            foreach (var pair in s_entries)
                if (pair.Value.Swapped) yield return new KeyValuePair<Vector2s, Vector2s>(pair.Key, pair.Value.Source);
        }

        private static IEnumerable<KeyValuePair<Vector2s, ZoneShift>> EntryShifts()
        {
            foreach (var pair in s_entries)
                if (pair.Value.Swapped && !pair.Value.Shift.IsZero) yield return new KeyValuePair<Vector2s, ZoneShift>(pair.Key, pair.Value.Shift);
        }

        // The zone whose land an open zone shows: its own, or the source a chunk seed of another biome gave it.
        internal static bool TryGetSource(Vector2s zone, out Vector2s source)
        {
            source = zone;
            if (!s_modeActive) return false;
            if (s_authority)
            {
                if (!s_entries.TryGetValue(zone, out var entry) || !entry.Swapped) return false;
                source = entry.Source;
                return true;
            }
            var open = s_clientOpenSet;
            if (open == null || !open.TryGetSource(zone.x, zone.y, out int sx, out int sy)) return false;
            source = new Vector2s(sx, sy);
            return true;
        }

        // How far a swapped zone's land was raised (or lowered) as a whole when it opened; 0 for any other zone.
        internal static float LiftOf(Vector2s zone) => ShiftOf(zone).Lift;

        // How a swapped zone's land is moved beyond the whole-zone offset to its source (ZoneShift).
        internal static ZoneShift ShiftOf(Vector2s zone)
        {
            if (!s_modeActive) return default;
            if (s_authority) return s_entries.TryGetValue(zone, out var entry) && entry.Swapped ? entry.Shift : default;
            return s_clientOpenSet?.ShiftOf(zone.x, zone.y) ?? default;
        }

        // Server: records the zones as open, saves, and republishes the gate view. Returns the zones
        // that were actually closed before (already-open and out-of-world zones are skipped). A zone listed in
        // `sources` with another zone shows that zone's land, moved by its entry in `shifts`.
        internal static List<Vector2s> AddOpenZones(IEnumerable<Vector2s> zones, ZoneOpenReason reason, long actorId,
            IDictionary<Vector2s, Vector2s> sources = null, IDictionary<Vector2s, ZoneShift> shifts = null)
        {
            var opened = new List<Vector2s>();
            if (!s_modeActive || !s_authority || !s_seeded) return opened;
            long now = DateTime.UtcNow.Ticks;
            foreach (var zone in zones)
            {
                if (!ZoneOpenSet.InRange(zone.x, zone.y) || s_entries.ContainsKey(zone)) continue;
                if (s_locked.Contains(zone))
                {
                    if (!OpensLocked(reason))
                    {
                        FiresLogger.LogDebug($"[FiresZones] ({zone.x},{zone.y}) stays closed: an admin closed it, and {reason} does not open a closed chunk.");
                        continue;
                    }
                    s_locked.Remove(zone);
                }
                var entry = new ZoneEntry { Reason = reason, OpenedUtcTicks = now, ActorId = actorId };
                if (sources != null && sources.TryGetValue(zone, out var source) && ZoneOpenSet.InRange(source.x, source.y)
                    && (source.x != zone.x || source.y != zone.y))
                {
                    entry.Swapped = true;
                    entry.Source = source;
                    if (shifts != null && shifts.TryGetValue(zone, out var shift) && shift.IsFinite) entry.Shift = shift;
                }
                s_entries[zone] = entry;
                opened.Add(zone);
            }
            if (opened.Count == 0) return opened;
            s_version++;
            Save();
            Publish();
            return opened;
        }

        // The other direction: zones taken back to closed sea, so a chunk can be laid down there again. Returns the
        // zones that were actually open.
        internal static List<Vector2s> RemoveOpenZones(IEnumerable<Vector2s> zones)
        {
            var closed = new List<Vector2s>();
            if (!s_modeActive || !s_authority) return closed;
            foreach (var zone in zones)
                if (s_entries.Remove(zone)) closed.Add(zone);
            if (closed.Count == 0) return closed;
            s_version++;
            Save();
            Publish();
            return closed;
        }

        internal static ulong[] CurrentWords() => (ZoneModeGate.Current?.Open ?? ZoneOpenSet.Empty).CopyWords();

        internal static List<KeyValuePair<Vector2s, Vector2s>> CurrentSources()
            => new List<KeyValuePair<Vector2s, Vector2s>>((ZoneModeGate.Current?.Open ?? ZoneOpenSet.Empty).Sources());

        internal static List<KeyValuePair<Vector2s, ZoneShift>> CurrentShifts()
            => new List<KeyValuePair<Vector2s, ZoneShift>>((ZoneModeGate.Current?.Open ?? ZoneOpenSet.Empty).Shifts());

        internal static void WriteSources(ZPackage pkg, List<KeyValuePair<Vector2s, Vector2s>> sources)
        {
            pkg.Write(sources.Count);
            foreach (var pair in sources)
            {
                pkg.Write(pair.Key);
                pkg.Write(pair.Value);
            }
        }

        internal static List<KeyValuePair<Vector2s, Vector2s>> ReadSources(ZPackage pkg)
        {
            int count = pkg.ReadInt();
            if (count < 0 || count > MaxZoneEntries) throw new InvalidDataException($"{count} zone sources");
            var sources = new List<KeyValuePair<Vector2s, Vector2s>>(count);
            for (int i = 0; i < count; i++) sources.Add(new KeyValuePair<Vector2s, Vector2s>(pkg.ReadVector2s(), pkg.ReadVector2s()));
            return sources;
        }

        internal static void WriteShifts(ZPackage pkg, List<KeyValuePair<Vector2s, ZoneShift>> shifts)
        {
            pkg.Write(shifts.Count);
            foreach (var pair in shifts)
            {
                pkg.Write(pair.Key);
                pkg.Write(pair.Value.Lift);
                pkg.Write(pair.Value.Dx);
                pkg.Write(pair.Value.Dz);
            }
        }

        internal static List<KeyValuePair<Vector2s, ZoneShift>> ReadShifts(ZPackage pkg)
        {
            int count = pkg.ReadInt();
            if (count < 0 || count > MaxZoneEntries) throw new InvalidDataException($"{count} zone shifts");
            var shifts = new List<KeyValuePair<Vector2s, ZoneShift>>(count);
            for (int i = 0; i < count; i++)
            {
                var zone = pkg.ReadVector2s();
                float lift = pkg.ReadSingle(), dx = pkg.ReadSingle(), dz = pkg.ReadSingle();
                shifts.Add(new KeyValuePair<Vector2s, ZoneShift>(zone, new ZoneShift(lift, dx, dz)));
            }
            return shifts;
        }

        // Client: a full open set from the server replaces the local one.
        internal static bool ApplyClientUpdate(ulong[] words, int version, List<KeyValuePair<Vector2s, Vector2s>> sources,
            List<KeyValuePair<Vector2s, ZoneShift>> shifts)
        {
            if (!s_modeActive || s_authority) return false;
            if (version <= s_version)
            {
                FiresLogger.LogVerbose($"[FiresZones] ignored zone update v{version} — already at v{s_version}.");
                return false;
            }
            var open = ZoneOpenSet.FromWords(words, version, sources, shifts);
            if (open == null)
            {
                FiresLogger.LogError($"[FiresZones] zone update v{version} has a malformed open set — ignored.");
                return false;
            }
            s_clientOpenSet = open;
            s_version = version;
            s_seeded = true;
            ZoneModeGate.SetView(open, s_coastWidth, s_seaFloorY);
            return true;
        }

        // ── Handshake (server → client, before PeerInfo) ────────────────────
        // Every joining peer gets the same handshake until the zone state changes. On a converted world it is large (R20:
        // 262,495 bytes for 54,302 parked zones) and building it took 21 ms of the join frame, so it is built once per
        // state and the same package goes to every peer (ZRpc.Invoke copies it out and never reads it). The stamp covers
        // everything BuildHandshake reads.
        private static ZPackage s_handshake;
        private static string s_handshakeSummary;
        private static HandshakeStamp s_handshakeStamp;

        private struct HandshakeStamp
        {
            internal World World;
            internal Voxel.FiresWorldProfile Profile;
            internal ZoneModeStartCave.Site Cave;
            internal bool ModeActive, Seeded, GuardsKnown;
            internal int Version, Epoch, ParkGeneration, GuardsHash;
            internal string Guards;

            internal bool Matches(in HandshakeStamp other)
                => ReferenceEquals(World, other.World) && ReferenceEquals(Profile, other.Profile) && ReferenceEquals(Cave, other.Cave)
                   && ModeActive == other.ModeActive && Seeded == other.Seeded && GuardsKnown == other.GuardsKnown
                   && Version == other.Version && Epoch == other.Epoch && ParkGeneration == other.ParkGeneration
                   && GuardsHash == other.GuardsHash && Guards == other.Guards;
        }

        internal static ZPackage HandshakeFor(out string summary, out bool reused)
        {
            var stamp = new HandshakeStamp
            {
                World = s_world,
                Profile = Voxel.FiresWorldProfile.Current,
                Cave = ZoneModeStartCave.Current,
                ModeActive = s_modeActive,
                Seeded = s_seeded,
                GuardsKnown = Voxel.FiresWaterGuards.Known,
                Version = s_version,
                Epoch = ZoneModeGate.CurrentEpoch,
                ParkGeneration = ZoneModePark.IndexGeneration,
                GuardsHash = Voxel.FiresWaterGuards.Hash,
                Guards = Voxel.FiresWaterGuards.Describe(),
            };
            reused = s_handshake != null && stamp.Matches(s_handshakeStamp);
            if (!reused)
            {
                s_handshake = BuildHandshake(out s_handshakeSummary);
                s_handshakeStamp = stamp;
            }
            summary = s_handshakeSummary;
            return s_handshake;
        }

        private static ZPackage BuildHandshake(out string summary)
        {
            var pkg = new ZPackage();
            pkg.Write(HandshakeProtocol);
            pkg.Write(s_world?.m_uid ?? 0L);
            // The world's profile rides every handshake, zone mode or not, so clients build the world its way.
            var profile = Voxel.FiresWorldProfile.Current;
            pkg.Write(profile != null);
            if (profile != null) profile.Write(pkg);
            Voxel.FiresWaterGuards.WriteHandshake(pkg);
            pkg.Write(s_modeActive);
            if (!s_modeActive)
            {
                summary = "zone mode off";
                return pkg;
            }
            var view = ZoneModeGate.Current;
            var open = view?.Open ?? ZoneOpenSet.Empty;
            ulong[] words = open.CopyWords();
            var bytes = new byte[words.Length * sizeof(ulong)];
            Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
            pkg.Write(s_seeded);
            pkg.Write(s_version);
            pkg.Write(s_coastWidth);
            pkg.Write(s_seaFloorY);
            pkg.Write(open.OpenCount);
            pkg.Write(bytes);
            WriteSources(pkg, new List<KeyValuePair<Vector2s, Vector2s>>(open.Sources()));
            WriteShifts(pkg, new List<KeyValuePair<Vector2s, ZoneShift>>(open.Shifts()));
            var cave = ZoneModeStartCave.Current;
            pkg.Write(cave != null);
            if (cave != null) ZoneModeStartCave.Write(pkg, cave);
            ZoneModePark.WriteHandshake(pkg);
            summary = $"zone mode, v{s_version}, {open.OpenCount} open ({open.SourceCount} swapped, {open.ShiftCount} shifted)"
                + (cave != null ? ", start cave" : "") + (ZoneModePark.ParkedCount > 0 ? $", {ZoneModePark.ParkedCount} parked" : "")
                + $", {pkg.Size()} bytes";
            return pkg;
        }

        private static readonly System.Reflection.FieldInfo ConnectionStatusField =
            HarmonyLib.AccessTools.Field(typeof(ZNet), "m_connectionStatus");

        // Leave the server the way vanilla leaves one on a version mismatch: the connection status says so (the main menu
        // shows it), then the server peer is dropped.
        private static void RefuseJoin(string why)
        {
            FiresLogger.LogError($"[FiresZones] leaving the server: {why}");
            try
            {
                ConnectionStatusField?.SetValue(null, ZNet.ConnectionStatus.ErrorVersion);
                var net = ZNet.instance;
                var server = net != null ? net.GetServerPeer() : null;
                if (server != null) net.Disconnect(server);
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[FiresZones] could not leave the server cleanly ({ex.Message}); quit to the menu and update FiresAdminTerrain.");
            }
        }

        internal static void ReceiveHandshake(ZPackage pkg)
        {
            try
            {
                byte protocol = pkg.ReadByte();
                if (protocol != HandshakeProtocol)
                {
                    FiresLogger.LogError($"[FiresZones] the server speaks zone protocol {protocol}, this build speaks "
                        + $"{HandshakeProtocol}. FiresAdminTerrain versions differ; closed zones will not match the server.");
                    return;
                }
                var state = new HandshakeState { WorldUid = pkg.ReadLong() };
                if (pkg.ReadBool())
                {
                    // 0.2.721: a profile this build can't read (a newer FAT's world size, say) must not leave the client to
                    // build the world from its own config, and the zone block after it depends on the size: refuse the join.
                    try { state.Profile = Voxel.FiresWorldProfile.Read(pkg); }
                    catch (System.IO.InvalidDataException ex)
                    {
                        RefuseJoin($"the server's world profile can't be read by this FiresAdminTerrain ({ex.Message}). Update FiresAdminTerrain to the server's version.");
                        return;
                    }
                }
                state.WaterGuards = Voxel.FiresWaterGuards.ReadHandshake(pkg);
                state.ZoneMode = pkg.ReadBool();
                if (state.ZoneMode)
                {
                    state.Seeded = pkg.ReadBool();
                    state.Version = pkg.ReadInt();
                    state.CoastWidth = pkg.ReadSingle();
                    state.SeaFloorY = pkg.ReadSingle();
                    state.OpenCount = pkg.ReadInt();
                    byte[] bytes = pkg.ReadByteArray();
                    if (bytes == null || bytes.Length != ZoneOpenSet.WordCount * sizeof(ulong))
                    {
                        FiresLogger.LogError($"[FiresZones] the server's zone state has {bytes?.Length ?? 0} bytes of open set, "
                            + $"expected {ZoneOpenSet.WordCount * sizeof(ulong)}. Ignored.");
                        return;
                    }
                    state.Words = new ulong[ZoneOpenSet.WordCount];
                    Buffer.BlockCopy(bytes, 0, state.Words, 0, bytes.Length);
                    state.Sources = ReadSources(pkg);
                    state.Shifts = ReadShifts(pkg);
                    if (pkg.ReadBool()) state.StartCave = ZoneModeStartCave.Read(pkg);
                    state.Park = ZoneModePark.ReadHandshake(pkg);
                }
                s_pendingHandshake = state;
                FiresLogger.LogInfo(state.ZoneMode
                    ? $"[FiresZones] zone state received before worldgen: v{state.Version}, {state.OpenCount} open zone(s)"
                      + ZoneModePark.DescribeClient(state.Park) + (state.Seeded ? "." : ", not seeded yet.")
                    : "[FiresZones] server says this world is not a zone-mode world.");
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[FiresZones] could not read the server's zone state: {ex.Message}");
            }
        }

        private static void BindHandshake(World world)
        {
            s_world = world;
            s_authority = false;
            var state = s_pendingHandshake;
            s_pendingHandshake = null;
            if (state == null)
            {
                Voxel.FiresWaterGuards.BindClient(null);
                FiresLogger.LogVerbose("[FiresZones] no zone state arrived with the handshake — the server has no zone mode.");
                return;
            }
            if (state.WorldUid != world.m_uid)
            {
                Voxel.FiresWaterGuards.BindClient(null);
                FiresLogger.LogError($"[FiresZones] the zone state was for world uid {state.WorldUid}, but this world is "
                    + $"uid {world.m_uid}. Ignored; closed zones will not match the server.");
                return;
            }
            Voxel.FiresWorldProfile.Set(state.Profile);
            Voxel.FiresWaterGuards.BindClient(state.WaterGuards);
            if (state.Profile != null)
                FiresLogger.LogInfo($"[FiresWorldGen] the server's profile for '{world.m_name}': {state.Profile.Describe()}.");
            if (!state.ZoneMode) return;

            s_modeActive = true;
            s_seeded = state.Seeded;
            s_version = state.Version;
            s_coastWidth = state.CoastWidth;
            s_seaFloorY = state.SeaFloorY;
            s_clientOpenSet = ZoneOpenSet.FromWords(state.Words, state.Version, state.Sources, state.Shifts);
            if (s_clientOpenSet == null || s_clientOpenSet.OpenCount != state.OpenCount)
            {
                FiresLogger.LogError($"[FiresZones] the server's open set failed its count check "
                    + $"({s_clientOpenSet?.OpenCount ?? -1} decoded, {state.OpenCount} sent). Every zone stays closed here.");
                s_clientOpenSet = ZoneOpenSet.Empty;
            }
            ZoneModeStartCave.Set(state.StartCave);
            ZoneModePark.BindClient(state.Park);
            if (s_seeded) ZoneModeGate.SetView(s_clientOpenSet, s_coastWidth, s_seaFloorY);
            FiresLogger.LogInfo($"[FiresZones] ONE ZONE AT A TIME world: gate active before any terrain was built — "
                + $"{s_clientOpenSet.OpenCount} open zone(s), v{s_version}, coast {s_coastWidth:0.#} m, sea floor y={s_seaFloorY:0.#}.");
        }

        // ── Load / save ─────────────────────────────────────────────────────
        private static bool TryLoad(string path, World world, out string failure, out bool belongsToAnotherWorld)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                failure = $"unreadable ({ex.GetType().Name}: {ex.Message})";
                belongsToAnotherWorld = false;
                return false;
            }
            return TryParse(bytes, world, out failure, out belongsToAnotherWorld);
        }

        private static bool TryParse(byte[] bytes, World world, out string failure, out bool belongsToAnotherWorld)
        {
            failure = null;
            belongsToAnotherWorld = false;
            if (bytes == null || bytes.Length < HeaderBytes) { failure = $"truncated at {bytes?.Length ?? 0} bytes"; return false; }

            var loaded = new Dictionary<Vector2s, ZoneEntry>();
            var lockedLoaded = new HashSet<Vector2s>();
            bool seeded;
            int version;
            long createdUtcTicks;
            ZoneModeStartCave.Site startCave = null;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(bytes)))
                {
                    uint magic = reader.ReadUInt32();
                    if (magic != FileMagic) { failure = $"not a zone registry (magic 0x{magic:X8})"; return false; }
                    byte format = reader.ReadByte();
                    if (format != FileFormat) { failure = $"format {format}, this build reads {FileFormat}"; return false; }
                    byte flags = reader.ReadByte();
                    long uid = reader.ReadInt64();
                    int seed = reader.ReadInt32();
                    if (uid != world.m_uid)
                    {
                        failure = $"written for world uid {uid}, this world is uid {world.m_uid}";
                        belongsToAnotherWorld = true;
                        return false;
                    }
                    if (seed != world.m_seed) { failure = $"seed {seed} does not match this world's seed {world.m_seed}"; return false; }
                    version = reader.ReadInt32();
                    createdUtcTicks = reader.ReadInt64();
                    int sectionCount = reader.ReadByte();
                    seeded = (flags & FlagSeeded) != 0;

                    bool sawZones = false;
                    var sources = new List<KeyValuePair<Vector2s, Vector2s>>();
                    var shifts = new List<KeyValuePair<Vector2s, ZoneShift>>();
                    for (int section = 0; section < sectionCount; section++)
                    {
                        byte id = reader.ReadByte();
                        int length = reader.ReadInt32();
                        long start = reader.BaseStream.Position;
                        if (length < 0 || start + length > bytes.Length) { failure = $"section {id} overruns the file"; return false; }
                        if (id == SectionStartCave)
                        {
                            if (length != ZoneModeStartCave.SerializedBytes)
                            {
                                failure = $"start-cave section is {length} bytes, expected {ZoneModeStartCave.SerializedBytes}";
                                return false;
                            }
                            startCave = ZoneModeStartCave.Read(reader);
                            continue;
                        }
                        if (id == SectionZoneShifts)
                        {
                            int shiftCount = reader.ReadInt32();
                            if (shiftCount < 0 || shiftCount > MaxZoneEntries || length != 4 + (long)shiftCount * ShiftEntryBytes)
                            {
                                failure = $"zone-shift section claims {shiftCount} entries in {length} bytes";
                                return false;
                            }
                            for (int i = 0; i < shiftCount; i++)
                            {
                                short zx = reader.ReadInt16(), zy = reader.ReadInt16();
                                float lift = reader.ReadSingle(), dx = reader.ReadSingle(), dz = reader.ReadSingle();
                                shifts.Add(new KeyValuePair<Vector2s, ZoneShift>(new Vector2s(zx, zy), new ZoneShift(lift, dx, dz)));
                            }
                            continue;
                        }
                        if (id == SectionLockedZones)
                        {
                            int lockedCount = reader.ReadInt32();
                            if (lockedCount < 0 || lockedCount > MaxZoneEntries || length != 4 + (long)lockedCount * 4)
                            {
                                failure = $"locked-zone section claims {lockedCount} entries in {length} bytes";
                                return false;
                            }
                            for (int i = 0; i < lockedCount; i++)
                            {
                                short zx = reader.ReadInt16(), zy = reader.ReadInt16();
                                if (!ZoneOpenSet.InRange(zx, zy)) { failure = $"locked zone ({zx},{zy}) is outside the world"; return false; }
                                lockedLoaded.Add(new Vector2s(zx, zy));
                            }
                            continue;
                        }
                        if (id == SectionZoneSources)
                        {
                            int sourceCount = reader.ReadInt32();
                            if (sourceCount < 0 || sourceCount > MaxZoneEntries || length != 4 + (long)sourceCount * SourceEntryBytes)
                            {
                                failure = $"zone-source section claims {sourceCount} entries in {length} bytes";
                                return false;
                            }
                            for (int i = 0; i < sourceCount; i++)
                            {
                                short zx = reader.ReadInt16(), zy = reader.ReadInt16(), sx = reader.ReadInt16(), sy = reader.ReadInt16();
                                sources.Add(new KeyValuePair<Vector2s, Vector2s>(new Vector2s(zx, zy), new Vector2s(sx, sy)));
                            }
                            continue;
                        }
                        if (id != SectionOpenZones) { failure = $"unknown section {id} — written by a newer FiresAdminTerrain"; return false; }
                        int count = reader.ReadInt32();
                        if (count < 0 || count > MaxZoneEntries || length != 4 + (long)count * ZoneEntryBytes)
                        {
                            failure = $"open-zone section claims {count} entries in {length} bytes";
                            return false;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            short x = reader.ReadInt16(), y = reader.ReadInt16();
                            var entry = new ZoneEntry
                            {
                                Reason = (ZoneOpenReason)reader.ReadByte(),
                                OpenedUtcTicks = reader.ReadInt64(),
                                ActorId = reader.ReadInt64(),
                            };
                            if (!ZoneOpenSet.InRange(x, y)) { failure = $"zone ({x},{y}) is outside the world"; return false; }
                            loaded[new Vector2s(x, y)] = entry;
                        }
                        sawZones = true;
                    }
                    if (!sawZones) { failure = "no open-zone section"; return false; }
                    foreach (var pair in sources)
                    {
                        if (!loaded.TryGetValue(pair.Key, out var entry)) { failure = $"zone source for ({pair.Key.x},{pair.Key.y}), which is not open"; return false; }
                        if (!ZoneOpenSet.InRange(pair.Value.x, pair.Value.y)) { failure = $"zone source ({pair.Value.x},{pair.Value.y}) is outside the world"; return false; }
                        entry.Swapped = true;
                        entry.Source = pair.Value;
                        loaded[pair.Key] = entry;
                    }
                    foreach (var pair in shifts)
                    {
                        if (!loaded.TryGetValue(pair.Key, out var entry) || !entry.Swapped)
                        {
                            failure = $"zone shift for ({pair.Key.x},{pair.Key.y}), which is not a swapped open zone";
                            return false;
                        }
                        if (!pair.Value.IsFinite) { failure = $"zone ({pair.Key.x},{pair.Key.y}) has a shift that is not a number"; return false; }
                        entry.Shift = pair.Value;
                        loaded[pair.Key] = entry;
                    }
                    if (reader.BaseStream.Position != bytes.Length)
                    {
                        failure = $"{bytes.Length - reader.BaseStream.Position} trailing byte(s)";
                        return false;
                    }
                    if (!seeded && loaded.Count != 0) { failure = $"marked unseeded yet holds {loaded.Count} zone(s)"; return false; }
                    foreach (var zone in lockedLoaded)
                        if (loaded.ContainsKey(zone)) { failure = $"zone ({zone.x},{zone.y}) is both open and locked closed"; return false; }
                }
            }
            catch (Exception ex) { failure = $"corrupt ({ex.GetType().Name}: {ex.Message})"; return false; }

            s_entries.Clear();
            foreach (var pair in loaded) s_entries[pair.Key] = pair.Value;
            s_locked.Clear();
            foreach (var zone in lockedLoaded) s_locked.Add(zone);
            s_seeded = seeded;
            s_version = version;
            s_createdUtcTicks = createdUtcTicks;
            ZoneModeStartCave.Set(startCave);
            return true;
        }

        private static void Save()
        {
            if (!s_authority || s_world == null) return;
            string path = RegistryPath(s_world.m_name);
            byte[] bytes = CurrentBytes();
            ZoneModeRegistryMirror.Write(bytes, s_world.m_uid);
            if (TryWriteAtomically(path, bytes, out string failure)) return;
            FiresLogger.LogError($"[FiresZones] could not save the zone registry ({failure}). The change is live this session and "
                + "kept in the world save's copy of the registry.");
        }

        private static byte[] CurrentBytes()
            => Serialize(s_world.m_uid, s_world.m_seed, s_seeded, s_version,
                s_createdUtcTicks != 0 ? s_createdUtcTicks : DateTime.UtcNow.Ticks, s_entries, ZoneModeStartCave.Current, s_locked);

        private static byte[] Serialize(long uid, int seed, bool seeded, int version, long createdUtcTicks,
            Dictionary<Vector2s, ZoneEntry> entries, ZoneModeStartCave.Site startCave = null, ICollection<Vector2s> locked = null)
        {
            int lockedCount = locked?.Count ?? 0;
            using (var ms = new MemoryStream(HeaderBytes + SectionHeaderBytes + 4 + entries.Count * ZoneEntryBytes))
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(FileMagic);
                writer.Write(FileFormat);
                writer.Write(seeded ? FlagSeeded : (byte)0);
                writer.Write(uid);
                writer.Write(seed);
                writer.Write(version);
                writer.Write(createdUtcTicks);
                int swapped = 0, shifted = 0;
                foreach (var pair in entries)
                {
                    if (!pair.Value.Swapped) continue;
                    swapped++;
                    if (!pair.Value.Shift.IsZero) shifted++;
                }
                writer.Write((byte)(1 + (swapped > 0 ? 1 : 0) + (shifted > 0 ? 1 : 0) + (startCave != null ? 1 : 0) + (lockedCount > 0 ? 1 : 0)));
                writer.Write(SectionOpenZones);
                writer.Write(4 + entries.Count * ZoneEntryBytes);
                writer.Write(entries.Count);
                foreach (var pair in entries)
                {
                    writer.Write(pair.Key.x);
                    writer.Write(pair.Key.y);
                    writer.Write((byte)pair.Value.Reason);
                    writer.Write(pair.Value.OpenedUtcTicks);
                    writer.Write(pair.Value.ActorId);
                }
                if (swapped > 0)
                {
                    writer.Write(SectionZoneSources);
                    writer.Write(4 + swapped * SourceEntryBytes);
                    writer.Write(swapped);
                    foreach (var pair in entries)
                    {
                        if (!pair.Value.Swapped) continue;
                        writer.Write(pair.Key.x);
                        writer.Write(pair.Key.y);
                        writer.Write(pair.Value.Source.x);
                        writer.Write(pair.Value.Source.y);
                    }
                }
                if (shifted > 0)
                {
                    writer.Write(SectionZoneShifts);
                    writer.Write(4 + shifted * ShiftEntryBytes);
                    writer.Write(shifted);
                    foreach (var pair in entries)
                    {
                        if (!pair.Value.Swapped || pair.Value.Shift.IsZero) continue;
                        writer.Write(pair.Key.x);
                        writer.Write(pair.Key.y);
                        writer.Write(pair.Value.Shift.Lift);
                        writer.Write(pair.Value.Shift.Dx);
                        writer.Write(pair.Value.Shift.Dz);
                    }
                }
                if (startCave != null)
                {
                    writer.Write(SectionStartCave);
                    writer.Write(ZoneModeStartCave.SerializedBytes);
                    ZoneModeStartCave.Write(writer, startCave);
                }
                if (lockedCount > 0)
                {
                    writer.Write(SectionLockedZones);
                    writer.Write(4 + lockedCount * 4);
                    writer.Write(lockedCount);
                    foreach (var zone in locked)
                    {
                        writer.Write(zone.x);
                        writer.Write(zone.y);
                    }
                }
                writer.Flush();
                return ms.ToArray();
            }
        }

        private static bool TryWriteAtomically(string path, byte[] bytes, out string failure)
        {
            failure = null;
            string temp = path + TempSuffix;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return false;
            }
        }

        private static void Quarantine(string path)
        {
            string target = path + RejectedSuffix;
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(path, target);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] could not set aside {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        // ── Diagnostics ─────────────────────────────────────────────────────
        internal static string DescribeReasons()
        {
            if (!s_authority) return "reasons are server-side";
            var counts = new Dictionary<ZoneOpenReason, int>();
            foreach (var entry in s_entries.Values)
            {
                counts.TryGetValue(entry.Reason, out int count);
                counts[entry.Reason] = count + 1;
            }
            var parts = new List<string>();
            foreach (var pair in counts) parts.Add($"{pair.Value} {pair.Key}");
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        internal static bool TryDescribeZone(Vector2s zone, out string description)
        {
            if (!s_modeActive) { description = "normal world"; return false; }
            if (!s_authority)
            {
                description = IsOpen(zone) ? "open" : "closed";
                return true;
            }
            if (s_entries.TryGetValue(zone, out var entry))
            {
                description = $"open ({entry.Reason}, {new DateTime(entry.OpenedUtcTicks, DateTimeKind.Utc):yyyy-MM-dd HH:mm} UTC"
                    + (entry.ActorId != 0 ? $", by {entry.ActorId}" : "") + ")";
                return true;
            }
            description = "closed";
            return true;
        }

        internal static HashSet<Vector2s> GeneratedZones(ZoneSystem zoneSystem)
            => zoneSystem != null ? GeneratedZonesRef(zoneSystem) : null;

        // Folded into FAT's minimap cache stamp, so a map drawn over one open set is never reused
        // for another. Null for normal worlds, which keeps their stamps unchanged.
        internal static string MapIdentity()
        {
            if (!s_modeActive) return null;
            var view = ZoneModeGate.Current;
            if (view == null) return $"zones:unseeded:{s_coastWidth:0.##}:{s_seaFloorY:0.##}";
            return $"zones:{view.Open.OpenCount}:{view.Open.ContentHash():x8}:{s_coastWidth:0.##}:{s_seaFloorY:0.##}";
        }
    }
}
