using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── SKY ISLAND REGISTRY ─────────────────────────────────────────────────
    // Islands used to be recomputed on every world load, and Build(seed) reads
    // far more than the seed:
    //   * config   — SkyIslands, SkyIslandRarity
    //   * ground   — TryGetGenGround, sampled across each footprint for clearance
    //   * water    — IsWetAt, which vetoes ponds over wet base ground
    // Change any of them (a rarity tweak, a generator revision, a water-plan fix)
    // and every island in every existing world moved, resized or vanished, with
    // nothing to recover the old layout from.
    //
    // The registry freezes that output as data, the way ZoneSystem freezes
    // location placement in m_locationInstances: the generator SEEDS a world's
    // islands once, and from then on the stored set is the truth. The config
    // toggle still works as a runtime kill switch (Enabled), it just no longer
    // rewrites what exists.
    //
    // Only the FILL changes. Every consumer — Sdf, TryGetBand, WaterLevelAt,
    // CollectProps — reads s_islands and never cared how it got there.
    //
    // THE CLIENT PROBLEM. A client cannot compute the set (it may legitimately
    // differ from what the generator says today), and it cannot have heard from
    // the server when this runs: WorldGenerator.Initialize is called inside
    // RPC_PeerInfo, before the connection's other RPCs are even registered. So a
    // client starts with islands held OFF and turns them on when Core's
    // WorldCacheSync confirms its copy matches the server's. While held off,
    // Enabled is false (the set is empty), so TryGetBand/ChunkOverlaps/Sdf add
    // nothing and no island density can be generated or cached.
    //
    // THE SERVER ALWAYS ANSWERS. WorldCacheSync only offers files that exist, so
    // a world with islands disabled would otherwise never produce a file, and its
    // clients would wait forever. It writes an explicit empty UNSEEDED record
    // instead, which seeds for real the first time the world loads with islands
    // enabled. A timeout covers servers that predate this entirely.
    //
    // The file is bound to its world by UID and seed, not just name: a world
    // deleted and recreated under the same name must not inherit the old one's
    // islands. Reads validate magic, format, identity and exact length before
    // touching live state, so a bad file is rejected whole rather than
    // half-loaded; writes go through a temp file and an atomic replace.
    internal static partial class FiresSkyIslands
    {
        private const uint RegistryMagic = 0x53494953u;
        // Format 2 adds each island's placement offset (OffX, OffY, OffZ). It is written only once the world holds a skyland
        // placed from a blueprint, so every other world keeps writing format 1 byte for byte, and older builds still read it.
        // Format 3 adds an island's own seeds and biome: a skyland raised from another world's blueprint keeps its home
        // world's noise, shape and biome. It is written only while such an island stands.
        private const byte RegistryFormat = 1;
        private const byte RegistryFormatPlaced = 2;
        private const byte RegistryFormatForeign = 3;
        private const int RegistryHeaderBytes = 4 + 1 + 1 + 8 + 4 + 4 + 4 + 4;
        private const int IslandRecordBytes = 15 * 4 + 1;
        private const int PlacedRecordBytes = IslandRecordBytes + 3 * 4;
        private const int ForeignRecordBytes = PlacedRecordBytes + 1 + 3 * 4;

        private static int RecordBytes(byte format)
            => format == RegistryFormatForeign ? ForeignRecordBytes : format == RegistryFormatPlaced ? PlacedRecordBytes : IslandRecordBytes;

        private static bool KnownFormat(byte format)
            => format == RegistryFormat || format == RegistryFormatPlaced || format == RegistryFormatForeign;

        private static byte FormatFor(List<Island> islands)
        {
            byte format = RegistryFormat;
            foreach (var isl in islands)
            {
                if (isl.OwnSeeds) return RegistryFormatForeign;
                if (IsMoved(in isl)) format = RegistryFormatPlaced;
            }
            return format;
        }
        private const int RegistryMaxIslands = 100000;
        private const float AwaitServerTimeoutSeconds = 60f;
        private const string RegistrySyncKey = "firesgen-islands";
        private const string RegistrySyncName = "FiresWorldGen sky island set";
        private const string RegistryFileSuffix = "_islands.bin";
        private const string RejectedSuffix = ".rejected";
        private const string TempSuffix = ".tmp";
        internal const string NoIslandsHash = "none";

        // Identity of the ACTIVE island set, or NoIslandsHash when the active set
        // is empty or none is known yet. The chunk cache folds this into its key,
        // so density generated against one set can never be served against
        // another — and every empty set shares one key, since their density is
        // identical.
        internal static string ActiveSetHash { get; private set; } = NoIslandsHash;

        private static bool s_seeded;
        private static float s_seededRarity;

        private static bool s_awaitingServer;
        private static float s_awaitingSince;
        private static World s_awaitingWorld;

        // Reset on every world init (Clear runs first thing in OnWorldInitialize),
        // so a set left pending by one world can never gate the next.
        private static void ResetRegistryState()
        {
            ActiveSetHash = NoIslandsHash;
            s_seeded = false;
            s_seededRarity = 0f;
            s_awaitingServer = false;
            s_awaitingWorld = null;
        }

        // True while a client is holding islands off for the server's answer.
        // Checked lazily (the chunk cache asks every streamer pass), so the
        // timeout needs no tick of its own.
        internal static bool AwaitingServer
        {
            get
            {
                if (!s_awaitingServer) return false;
                if (UnityEngine.Time.realtimeSinceStartup - s_awaitingSince < AwaitServerTimeoutSeconds) return true;
                s_awaitingServer = false;
                FiresLogger.LogWarning(
                    $"[FiresSkyIslands] no island set from the server after {AwaitServerTimeoutSeconds:0}s — carrying "
                    + "on with islands OFF. One that arrives later is still applied. (A server without this build "
                    + "never sends one.)");
                return false;
            }
        }

        internal static string RegistryPath(string worldName)
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(worldName, RegistryFileSuffix);
        }

        // Client delivery goes through Core. Registered alongside the baked-terrain
        // cache; the server path and client path are the same per-world file.
        internal static void RegisterSync()
        {
            FiresCore.Net.WorldCacheSync.Register(
                RegistrySyncKey,
                RegistrySyncName,
                CurrentWorldRegistryPath,
                CurrentWorldRegistryPath,
                ActivateFromSync);
        }

        private static string CurrentWorldRegistryPath()
            => ZNet.instance?.GetWorldName() is string w && !string.IsNullOrEmpty(w) ? RegistryPath(w) : null;

        // World-init entry point (FiresWorldGenBase.OnWorldInitialize). ZNet.instance
        // is already set when the server initializes (ZNet.Awake assigns it before
        // calling WorldGenerator.Initialize), and on a client this runs inside
        // RPC_PeerInfo, so authority is always knowable here. No ZNet at all means
        // offline tooling, which keeps the old recompute-in-place behaviour and
        // writes nothing, since there is no world save to bind a registry to.
        internal static void Initialize(World world)
        {
            var net = ZNet.instance;
            if (net == null)
            {
                Build(world.m_seed);
                SetActiveHash(Serialize(world, CurrentRarity()));
                return;
            }
            if (net.IsServer()) InitializeAuthoritative(world);
            else InitializeAwaitingServer(world);
        }

        // ── Server / host ───────────────────────────────────────────────────
        // The authority owns the registry and ALWAYS leaves one on disk, so every
        // client gets a definitive answer.
        private static void InitializeAuthoritative(World world)
        {
            string path = RegistryPath(world.m_name);
            string file = Path.GetFileName(path);

            if (File.Exists(path))
            {
                if (TryLoadRegistry(path, world, out string failure))
                {
                    if (!s_seeded && BuildsSkyIslandsNow())
                    {
                        FiresLogger.LogInfo(
                            $"[FiresSkyIslands] '{world.m_name}' was recorded unseeded while islands were off; "
                            + "islands are on now, so seeding it.");
                        SeedAndSave(world, path);
                        return;
                    }
                    if (s_seeded)
                    {
                        FiresLogger.LogInfo(
                            $"[FiresSkyIslands] registry loaded — {s_islands.Count} island(s), {ClusterCount} "
                            + $"cluster(s), frozen; the generator was not consulted. ({file})");
                        NoteRarityDrift();
                    }
                    else
                    {
                        FiresLogger.LogInfo(
                            $"[FiresSkyIslands] '{world.m_name}' is recorded unseeded and islands are still off. ({file})");
                    }
                    return;
                }
                // 0.2.734 (the lead 10-05, a clean log): a registry of another world with THIS seed is what a recreate or a
                // clone (new uid, same seed) leaves; rebuilding it is expected, so it is said at Info. Anything else stays a Warning.
                string rejected = $"[FiresSkyIslands] registry {file} rejected: {failure}. Kept as {file}{RejectedSuffix} and rebuilding it.";
                if (s_rejectedSameSeed) FiresLogger.LogInfo(rejected + " (Same seed: expected after a recreate or a clone.)");
                else FiresLogger.LogWarning(rejected);
                s_rejectedSameSeed = false;
                Quarantine(path);
            }

            if (BuildsSkyIslandsNow()) SeedAndSave(world, path);
            else RecordUnseeded(world, path);
        }

        private static void SeedAndSave(World world, string path)
        {
            Build(world.m_seed);
            s_seeded = true;
            if (TrySaveRegistry(path, world, out string failure))
            {
                FiresLogger.LogInfo(
                    $"[FiresSkyIslands] registry SEEDED for '{world.m_name}' — {s_islands.Count} island(s) frozen to "
                    + $"{Path.GetFileName(path)}. Later generator or config changes will not move them.");
                return;
            }
            // Live even though unwritten, so its identity must still reach the
            // chunk cache: left stale, island density would be cached under the
            // wrong key.
            SetActiveHash(Serialize(world, CurrentRarity()));
            FiresLogger.LogError(
                $"[FiresSkyIslands] could not write the island registry ({failure}). Islands are live this "
                + "session but NOT frozen, and clients cannot be sent them: the next load recomputes them.");
        }

        // Islands are off and the world was never seeded. Freezing an EMPTY seeded
        // set would pin "no islands" forever; writing nothing would leave clients
        // waiting. An explicit unseeded record answers them without committing.
        private static void RecordUnseeded(World world, string path)
        {
            Clear();
            s_seeded = false;
            if (TrySaveRegistry(path, world, out string failure))
            {
                FiresLogger.LogInfo(
                    $"[FiresSkyIslands] islands are off — recorded '{world.m_name}' as unseeded so clients get a "
                    + "definitive answer. It seeds the first time it loads with islands on.");
                return;
            }
            FiresLogger.LogWarning(
                $"[FiresSkyIslands] could not record '{world.m_name}' as unseeded ({failure}). Clients will wait "
                + $"{AwaitServerTimeoutSeconds:0}s for an island set before carrying on without one.");
        }

        // ── Client ──────────────────────────────────────────────────────────
        private static void InitializeAwaitingServer(World world)
        {
            s_awaitingServer = true;
            s_awaitingSince = UnityEngine.Time.realtimeSinceStartup;
            s_awaitingWorld = world;
            FiresLogger.LogInfo(
                $"[FiresSkyIslands] client: islands held OFF for '{world.m_name}' until the server confirms this "
                + "world's island set.");
        }

        // Core's WorldCacheSync calls this once the client's copy is confirmed to
        // match the server's — immediately when it already did, or after a
        // transfer lands and verifies.
        private static void ActivateFromSync(string path)
        {
            // Keyed off the LOADED world, not the one that was waiting: the server
            // re-offers this file whenever an admin edits the set, so deliveries
            // after the first are updates, not strays.
            var world = ZNet.World;
            if (world == null)
            {
                FiresLogger.LogWarning("[FiresSkyIslands] an island set arrived with no world loaded — ignored.");
                return;
            }

            bool firstDelivery = s_awaitingServer || s_awaitingWorld != null;
            float waited = firstDelivery ? UnityEngine.Time.realtimeSinceStartup - s_awaitingSince : 0f;
            bool timedOut = firstDelivery && !s_awaitingServer;

            var before = new List<Island>(s_islands);
            if (!TryLoadRegistry(path, world, out string failure))
            {
                FiresLogger.LogError(
                    $"[FiresSkyIslands] the server's island set failed validation on this client ({failure}). "
                    + (firstDelivery
                        ? "Islands stay OFF rather than showing a set the server does not have."
                        : "Keeping the set already in use rather than a half-applied update."));
                return;
            }

            if (!firstDelivery)
            {
                int updated = MarkChangedZonesDirty(before, s_islands);
                Zones.ZoneModeSkylands.RisingArrived();
                FiresLogger.LogInfo(
                    $"[FiresSkyIslands] island set UPDATED from the server — {s_islands.Count} island(s); "
                    + $"{updated} loaded zone(s) re-armed for in-place regeneration.");
                return;
            }

            s_awaitingServer = false;
            s_awaitingWorld = null;
            // No cache re-key needed: island density is keyed per chunk, so any
            // chunk inside a band now hashes differently and regenerates through
            // the streamer's change-gate on its next pass.
            MarkChangedZonesDirty(before, s_islands);

            FiresLogger.LogInfo(
                $"[FiresSkyIslands] client: server's island set active after {waited:0.00}s"
                + (timedOut ? " (after the wait had timed out)" : "")
                + $" — {s_islands.Count} island(s), {ClusterCount} cluster(s)"
                + (s_seeded ? "." : ", world unseeded."));
            ReportChunksBuiltBeforeActivation();
        }

        // Re-arm every wrapped zone whose islands differ between two sets, using the
        // streamer's existing terraform-dirty path: one zone per tick, brush edits
        // re-applied on the fresh ground, and the old meshes and colliders stay live
        // until each new one swaps in. Nothing is dropped, so the anchors that hold
        // player edits are never asked to re-apply.
        //
        // A REMOVED island matters as much as an added one, so its OLD footprint is
        // marked too — otherwise the zone would happily keep the island that was
        // just deleted. Returns the number of discs marked.
        private static int MarkChangedZonesDirty(List<Island> before, List<Island> after)
        {
            var beforeIds = new HashSet<uint>();
            foreach (var isl in before) beforeIds.Add(IslandHash(in isl));
            var afterIds = new HashSet<uint>();
            foreach (var isl in after) afterIds.Add(IslandHash(in isl));

            int marked = 0;
            foreach (var isl in before)
                if (!afterIds.Contains(IslandHash(in isl))) { MarkOne(in isl); marked++; }
            foreach (var isl in after)
                if (!beforeIds.Contains(IslandHash(in isl))) { MarkOne(in isl); marked++; }
            return marked;
        }

        private static void MarkOne(in Island isl)
            => VoxelZoneStreamer.MarkAreaDirty(new UnityEngine.Vector3(WorldX(in isl), 0f, WorldZ(in isl)), isl.R);

        // ── Load / save ─────────────────────────────────────────────────────
        // Set by TryLoadRegistry when the file was refused only for another world's uid with the same seed.
        private static bool s_rejectedSameSeed;

        private static bool TryLoadRegistry(string path, World world, out string failure)
        {
            failure = null;
            s_rejectedSameSeed = false;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { failure = $"unreadable ({ex.GetType().Name}: {ex.Message})"; return false; }

            if (bytes.Length < RegistryHeaderBytes)
            {
                failure = $"truncated at {bytes.Length} bytes";
                return false;
            }

            var loaded = new List<Island>();
            bool seeded;
            float seededRarity;
            int clusters;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes)))
                {
                    uint magic = r.ReadUInt32();
                    if (magic != RegistryMagic) { failure = $"not an island registry (magic 0x{magic:X8})"; return false; }

                    byte format = r.ReadByte();
                    if (!KnownFormat(format))
                    {
                        failure = $"format {format}, this build reads {RegistryFormat} to {RegistryFormatForeign}";
                        return false;
                    }

                    seeded = r.ReadByte() != 0;
                    long uid = r.ReadInt64();
                    int seed = r.ReadInt32();
                    if (uid != world.m_uid || seed != world.m_seed)
                    {
                        s_rejectedSameSeed = seed == world.m_seed;
                        failure = $"belongs to another world (uid {uid}, seed {seed}; this world is uid "
                                  + $"{world.m_uid}, seed {world.m_seed}) — a world by this name was recreated";
                        return false;
                    }

                    seededRarity = r.ReadSingle();
                    int count = r.ReadInt32();
                    clusters = r.ReadInt32();
                    if (count < 0 || count > RegistryMaxIslands) { failure = $"implausible island count {count}"; return false; }
                    if (!seeded && count != 0) { failure = $"marked unseeded yet holds {count} island(s)"; return false; }

                    long expected = RegistryHeaderBytes + (long)count * RecordBytes(format);
                    if (bytes.Length != expected)
                    {
                        failure = $"length {bytes.Length}, expected {expected} for {count} island(s)";
                        return false;
                    }
                    for (int i = 0; i < count; i++) loaded.Add(ReadIsland(r, format));
                }
            }
            catch (Exception ex) { failure = $"corrupt ({ex.GetType().Name}: {ex.Message})"; return false; }

            // Validated in full before live state is touched. Clear() would reset
            // the pending-client state too, so keep the parts that must survive it.
            var awaitingWorld = s_awaitingWorld;
            bool awaiting = s_awaitingServer;
            float since = s_awaitingSince;
            Clear();
            s_awaitingWorld = awaitingWorld;
            s_awaitingServer = awaiting;
            s_awaitingSince = since;

            foreach (var isl in loaded) AddIsland(in isl);
            s_any = s_islands.Count > 0;
            ClusterCount = clusters;
            s_seeded = seeded;
            s_seededRarity = seededRarity;
            SetActiveHash(bytes);
            return true;
        }

        private static bool TrySaveRegistry(string path, World world, out string failure)
        {
            failure = null;
            float rarity = s_seeded ? CurrentRarity() : 0f;
            byte[] bytes = Serialize(world, rarity);
            string temp = path + TempSuffix;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                TryDelete(temp);
                return false;
            }
            s_seededRarity = rarity;
            SetActiveHash(bytes);
            return true;
        }

        private static void SetActiveHash(byte[] serialized)
            => ActiveSetHash = s_islands.Count == 0 ? NoIslandsHash : Sha256Hex(serialized);

        private static byte[] Serialize(World world, float rarity)
        {
            byte format = FormatFor(s_islands);
            using (var ms = new MemoryStream(RegistryHeaderBytes + s_islands.Count * RecordBytes(format)))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(RegistryMagic);
                w.Write(format);
                w.Write((byte)(s_seeded ? 1 : 0));
                w.Write(world.m_uid);
                w.Write(world.m_seed);
                w.Write(rarity);
                w.Write(s_islands.Count);
                w.Write(ClusterCount);
                foreach (var isl in s_islands) WriteIsland(w, in isl, format);
                w.Flush();
                return ms.ToArray();
            }
        }

        // Field order is the file format. A new field means a new RegistryFormat,
        // never a reshuffle.
        private static void WriteIsland(BinaryWriter w, in Island isl, byte format)
        {
            w.Write(isl.X); w.Write(isl.Z); w.Write(isl.R); w.Write(isl.BaseY); w.Write(isl.Seed);
            w.Write(isl.Top); w.Write(isl.Bot); w.Write(isl.MaxDepth);
            w.Write(isl.Kind);
            w.Write(isl.PondX); w.Write(isl.PondZ); w.Write(isl.PondR); w.Write(isl.PondLvl);
            w.Write(isl.RillAng); w.Write(isl.RillLen); w.Write(isl.RillEndLvl);
            if (format == RegistryFormat) return;
            w.Write(isl.OffX); w.Write(isl.OffY); w.Write(isl.OffZ);
            if (format != RegistryFormatForeign) return;
            w.Write(isl.OwnSeeds); w.Write(isl.NoiseSeed); w.Write(isl.OwnShapeSeed); w.Write((int)isl.FixedBiome);
        }

        private static Island ReadIsland(BinaryReader r, byte format)
        {
            var isl = new Island
            {
                X = r.ReadSingle(), Z = r.ReadSingle(), R = r.ReadSingle(), BaseY = r.ReadSingle(), Seed = r.ReadSingle(),
                Top = r.ReadSingle(), Bot = r.ReadSingle(), MaxDepth = r.ReadSingle(),
                Kind = r.ReadByte(),
            };
            isl.PondX = r.ReadSingle(); isl.PondZ = r.ReadSingle(); isl.PondR = r.ReadSingle(); isl.PondLvl = r.ReadSingle();
            isl.RillAng = r.ReadSingle(); isl.RillLen = r.ReadSingle(); isl.RillEndLvl = r.ReadSingle();
            if (format == RegistryFormat) return isl;
            isl.OffX = r.ReadSingle(); isl.OffY = r.ReadSingle(); isl.OffZ = r.ReadSingle();
            if (format != RegistryFormatForeign) return isl;
            isl.OwnSeeds = r.ReadBoolean(); isl.NoiseSeed = r.ReadInt32(); isl.OwnShapeSeed = r.ReadInt32();
            isl.FixedBiome = (Heightmap.Biome)r.ReadInt32();
            return isl;
        }

        // ── Diagnostics ─────────────────────────────────────────────────────
        // A rarity change no longer re-rolls a seeded world, which is correct but
        // silent — say so, so the setting does not look broken.
        private static void NoteRarityDrift()
        {
            if (FiresVoxelConfig.SkyIslandRarity == null) return;
            float now = FiresVoxelConfig.SkyIslandRarity.Value;
            if (UnityEngine.Mathf.Approximately(now, s_seededRarity)) return;
            FiresLogger.LogInfo(
                $"[FiresSkyIslands] rarity is now {now:0.00} but this world was seeded at {s_seededRarity:0.00}. "
                + "Its islands are frozen, so the new value does not apply here.");
        }

        // Late activation is correct but can leave work behind: a chunk built
        // while islands were held off was built WITHOUT them, and one that overlaps
        // an island keeps that island-less density until its zone reloads. This
        // counts them so the log says whether that actually happens, before anyone
        // builds machinery to repair it.
        private static void ReportChunksBuiltBeforeActivation()
        {
            int loaded = 0, overlapping = 0;
            foreach (var chunk in VoxelWorld.AllChunks)
            {
                if (chunk == null) continue;
                loaded++;
                if (ChunkOverlaps(chunk.WorldOrigin)) overlapping++;
            }
            if (overlapping == 0)
            {
                FiresLogger.LogInfo(
                    $"[FiresSkyIslands] activation landed before any island chunk was built ({loaded} chunk(s) "
                    + "loaded, none overlap an island) — nothing to repair.");
                return;
            }
            FiresLogger.LogWarning(
                $"[FiresSkyIslands] LATE ACTIVATION: {overlapping} of {loaded} loaded chunk(s) overlap an island but "
                + "were built before the island set arrived, so they lack island density until their zone "
                + "reloads. Report this — it means the sync lost the race against zone streaming.");
        }

        private static float CurrentRarity()
            => FiresVoxelConfig.SkyIslandRarity != null ? FiresVoxelConfig.SkyIslandRarity.Value : 0f;

        private static bool BuildsSkyIslandsNow()
            => (FiresVoxelConfig.SkyIslands == null || FiresVoxelConfig.SkyIslands.Value)
               && CurrentRarity() > 0f;

        private static void Quarantine(string path)
        {
            string target = path + RejectedSuffix;
            try
            {
                TryDelete(target);
                File.Move(path, target);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning(
                    $"[FiresSkyIslands] could not set aside the rejected registry ({ex.Message}); it will be overwritten.");
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
