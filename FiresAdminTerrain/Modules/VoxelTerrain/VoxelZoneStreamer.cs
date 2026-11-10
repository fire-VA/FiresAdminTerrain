using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // W1 — the WHOLE-WORLD voxel wrap (Stage W, wrap-and-subtract strategy).
    //
    // For each vanilla zone near the player (VoxelWrap mode only):
    //   1. Gather ground + biome per column from the LIVE heightmap DATA
    //      (main thread, array reads — fast).
    //   2. Create the zone's chunk stack (2×2 XZ, surface band ± cave depth in
    //      Y) with MeshAll=true and a deferred generate payload; the WORKER
    //      fills density + carves the deterministic cave noise + meshes.
    //   3. When every chunk's collider is live, hide the vanilla heightmap's
    //      RENDERER + COLLIDER — the voxel mesh IS the ground now. The
    //      Heightmap component stays alive as the data API (heights, biomes,
    //      minimap, placement all keep reading it). Swap order matters: never
    //      hide vanilla before voxel colliders exist or players fall through.
    //   4. Leaving the radius (or toggling mode to Vanilla) reverses it:
    //      re-enable vanilla FIRST, then drop the chunks. Everything
    //      regenerates deterministically from the world seed on return.
    //
    // Terrain ops (hoe, cultivator) still write heightmap data; Regenerate on a
    // wrapped zone re-hides vanilla and queues a full re-wrap so the voxel
    // surface follows the edit. Brush edits in that zone re-apply on top of
    // the regenerated ground.
    public static class VoxelZoneStreamer
    {
        private sealed class ZoneWrap
        {
            public Vector2s Zone;
            public Heightmap Hm;
            public readonly List<Vector3Int> Keys = new List<Vector3Int>();
            public bool Swapped;

            // Identity of what was hidden at swap time. Vanilla's only writer of
            // this MeshRenderer.enabled is the heightmap's RenderGroupSubscriber,
            // and HideVanilla suspends it. So the same renderer visible again means
            // another mod switched it back on (a genuine fight), while a different
            // renderer means the old one was replaced. Those need opposite fixes,
            // and only the instance id tells them apart.
            public int HiddenHmId;
            public int HiddenRendererId;
            // A swapped zone with a Full chunk that lost its view: vanilla's collider stands in until the view is back
            // (KeepGroundUnderSwappedZone).
            public bool VanillaColliderBack;
            public float NextGroundCheckAt;
            public float NextGroundLogAt;
            public float StartedAt;      // wrap start (stuck-swap diagnostics)
            // 0.2.1346 per-zone stage proof (Stopwatch ticks; ms = worker mesh sum) + the reveal's paint / snow wait
            public long T0, TGathered, TApply, TCollider, TPrep;
            public int NApply; public double MeshMs;
            public float PrepSince = -1f; public bool PrepCapped;
            // 0.2.1353 proof (Debug): this wrap's applies per chunk before its swap, the remeshes it held, and its own paint columns still to land after the swap
            public Dictionary<Vector3Int, int> AppliesByChunk;
            public int HeldChunks, HeldAsks; public bool HeldGaveUp;
            public HashSet<Vector2Int> PaintLeft; public long TSwapDone;
            public int GenWaitSaid;      // 0.2.1350 (proof: generate wait lines said for this wrap)
            public float NextDiagAt;     // throttle for blocked-swap logging
            public float LastRewrapAt;   // debounce for terraform rewraps
            // Round-91 — true when any border/apron column had to fall back to
            // WorldGenerator math because a neighbour heightmap wasn't loaded
            // yet. Such borders DISAGREE with the neighbour once it loads (no
            // TerrainComp + different height path) → the persistent zone-edge
            // split. Healed by a targeted in-place regen when the neighbour
            // wraps (see StartWrap).
            public bool UsedFallback;

            // Server copy only: generated density, never meshed, no collider, vanilla left alone. Swapped means
            // every chunk has generated. OpenMask is the zone-mode open state of the 3x3 it was built under.
            public bool DataOnly;
            public int OpenMask;
            public bool AwaitingRegen;
            public readonly Dictionary<Vector3Int, uint> ImportedEdits = new Dictionary<Vector3Int, uint>();
        }

        private static readonly Dictionary<Vector2s, ZoneWrap> s_zones = new Dictionary<Vector2s, ZoneWrap>();
        private static readonly List<Vector2s> s_scratchDrop = new List<Vector2s>();
        // Zones whose heightmap DATA changed (terrain ops) — rewrapped one per
        // tick. Includes neighbours: their border/apron samples read into the
        // regenerated zone, so skipping them would leave hairline border lips.
        private static readonly HashSet<Vector2s> s_dirty = new HashSet<Vector2s>();
        private static readonly List<(Vector2s zone, float due)> s_waterQueue = new List<(Vector2s, float)>();
        private static readonly HashSet<Vector2s> s_waterDone = new HashSet<Vector2s>();
        private static readonly List<Vector3> s_springScratch = new List<Vector3>();
        private static readonly List<Vector3> s_springSpots = new List<Vector3>();
        private static float s_nextSummary;
        private static float s_nextMemory = 60f;
        private static GameObject s_driverGO;
        internal static bool HasDriver => s_driverGO != null;   // 0.2.1319: spread jobs pumped from the driver fall back to one frame without it

        private static readonly AccessTools.FieldRef<Heightmap, bool> s_isDistantLod =
            AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");
        private static readonly AccessTools.FieldRef<Heightmap, MeshCollider> s_hmCollider =
            AccessTools.FieldRefAccess<Heightmap, MeshCollider>("m_collider");

        public static int WrappedZoneCount => s_zones.Count;
        // Read by the hub Stats card (FatStatsSection)
        internal static float GovernorRadius => s_governorRadius;
        internal static int PriorityHealCount => s_priorityHeal.Count;
        internal static int GhostCount => s_ghostCount;
        internal static double GhostMs => s_ghostMs;
        internal static double GhostMaxMs => s_ghostMaxMs;

        // Spawn gate: a zone is SOLID once its voxel colliders are live
        // (Swapped flips exactly then). The area check mirrors vanilla's
        // IsAreaReady footprint so first spawns/teleports never land on
        // ground that hasn't meshed yet.
        public static bool IsZoneSolid(Vector2s zone)
            => s_zones.TryGetValue(zone, out var w) && w.Swapped;

        // Zone is voxel TERRITORY (wrapping OR wrapped). The quilt must hide
        // its near tile the moment wrapping starts: in VoxelOnly the voxel
        // meshes render immediately, and the not-yet-Swapped window had the
        // analytic tile (no carves, no collision, −0.8m) drawn OVER the real
        // ground — the phantom sheets covering carves and cave mouths.
        public static bool IsZoneKnown(Vector2s zone) => s_zones.ContainsKey(zone);

        internal static bool IsZoneDataOnly(Vector2s zone) => s_zones.TryGetValue(zone, out var w) && w.DataOnly;

        internal static bool HoldsDataOnly(Vector3Int chunkKey) => IsZoneDataOnly(ZoneOfChunk(chunkKey));

        // Core's wear gate holds every piece's wear until collision is built under it; FAT answers for the zones its
        // voxel terrain builds. Bound by name, so FAT still builds and runs against a Core without the gate.
        internal static void RegisterWearGround()
        {
            var gate = AccessTools.TypeByName("FiresCore.Pieces.WearGate");
            var register = gate != null ? AccessTools.Method(gate, "RegisterGround") : null;
            if (register == null)
            {
                FiresLogger.LogInfo("[FiresVoxel] this Core has no wear gate: pieces on voxel ground keep vanilla's wear timing.");
                return;
            }
            register.Invoke(null, new object[] { new System.Func<Vector2s, bool?>(WearGroundReady) });
            FiresLogger.LogInfo("[FiresVoxel] voxel ground answers Core's wear gate.");
        }

        // Whether a zone's voxel collision is built on this machine; null where its ground is vanilla terrain. A
        // data-only zone (a dedicated server's cave and skyland density) builds no colliders, so it never counts. Nor
        // does a zone whose terrain is not settled (a skyland rising over it, an island set arriving from the server, any
        // in-place rewrap), or one a skyland is announced over that this client has not received yet.
        private static bool? WearGroundReady(Vector2s zone)
        {
            if (Zones.ZoneModeSkylands.RisingOver(zone)) return false;
            if (s_zones.TryGetValue(zone, out var wrap) && wrap != null) return !wrap.DataOnly && IsZoneTerrainSettled(zone);
            if (s_driverGO == null) return null;
            return ShouldWrapZone(zone, ZoneSystem.GetZonePos(zone)) ? false : (bool?)null;
        }

        // Whether voxel terrain is (or will become) the ground at a point: Vedr keeps quake scars, craters and lake ice off it.
        internal static bool OwnsGround(Vector3 point)
        {
            Vector2s zone = ZoneSystem.GetZone(point);
            return s_zones.ContainsKey(zone) || AwaitsWrap(zone);
        }

        // Vedr waits for this before lifting what a quake scar's ground rose over: the voxel ground at a point is
        // current once its zone's rewrap has finished and the new chunks are collidable. A zone the streamer will take
        // up but has not yet is not current either: Vedr already leaves its cracks to voxels that have not cut them.
        internal static bool IsGroundSettled(Vector3 point)
        {
            Vector2s zone = ZoneSystem.GetZone(point);
            return !AwaitsWrap(zone) && IsZoneTerrainSettled(zone);
        }

        private static bool AwaitsWrap(Vector2s zone)
            => !s_zones.ContainsKey(zone) && s_driverGO != null && ShouldWrapZone(zone, ZoneSystem.GetZonePos(zone));

        private const float GroundProbeEpsilon = 0.013f;

        // THE ground height a voxel column is built on: the live heightmap where one exists, otherwise the zone's
        // heightmap build data (a dedicated server keeps no heightmaps near its players), plus any location pad.
        // Always the ground, never a skyland top.
        private static bool s_dataReported;

        internal static bool TryGroundHeight(float wx, float wz, out float ground)
        {
            if (!ZoneHeight(new Vector3(wx + GroundProbeEpsilon, 0f, wz + GroundProbeEpsilon), out ground)
                && !HeightmapBuildCache.TryHeight(wx, wz, out ground))
                return false;
            if (float.IsNaN(ground) || float.IsInfinity(ground))
            {
                WarnNonFiniteGround(wx, wz);
                ground = GeneratedGround(wx, wz);
            }
            if (FiresLocationPads.TrySample(wx, wz, ground, out float padY)) ground = padY;
            ground = FiresSkyIslandTerrainEdit.KeepUnderIsland(wx, wz, ground);   // an edit below an island never reaches up into it
            return true;
        }

        // FindHeightmap cost 76 ms/s at 9.8k calls/s, all from here. Vanilla's static
        // Heightmap.GetHeight walks every loaded heightmap; this resolves it once per zone and re-checks the cached one (alive, enabled,
        // still covering the point) on use, so a destroyed or disabled heightmap just falls back to the vanilla walk and is re-cached.
        private static readonly System.Collections.Generic.Dictionary<Vector2s, Heightmap> s_zoneHm = new System.Collections.Generic.Dictionary<Vector2s, Heightmap>();
        private static int s_hmHits, s_hmWalks; private static float s_hmLogAt;

        private static int s_heightNotReady;   // 0.2.1272: GetWorldHeight on a not-yet-generated heightmap
        private static bool ZoneHeight(Vector3 p, out float h)
        {
            var hm = ZoneHeightmap(p);
            // 0.2.1272 ([ghost]'s sweep of Fire's 02:06 log: "[Biome] GetBiome(Vector3) threw OUTSIDE every FAT patch at (34,10000,-542):
            // ArgumentOutOfRangeException" via FiresCaveBiome.InCave -> TryGroundHeight -> ZoneHeight -> Heightmap.GetWorldHeight, and
            // the unified biome resolver then disabled itself for the session): a heightmap that is active but whose height list is not
            // generated yet (a zone still loading) throws in vanilla's index. Treated as "no height here yet", counted, logged once.
            try { if (hm != null && hm.GetWorldHeight(p, out h)) return true; }
            catch (System.ArgumentOutOfRangeException)
            {
                if (s_heightNotReady++ == 0) FiresLogger.LogDebug($"[VoxelStream] ZoneHeight: heightmap '{hm?.name}' at ({p.x:0},{p.z:0}) has no heights yet (zone still generating); answered 'no height' instead of throwing (0.2.1272)");
            }
            h = 0f; return false;
        }

        // The loaded heightmap covering p (vanilla FindHeightmap's answer), from the per-zone cache. Off the main thread: vanilla's walk.
        internal static Heightmap ZoneHeightmap(Vector3 p)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != FiresCaveBiome.s_mainThread) return Heightmap.FindHeightmap(p);
            var zone = ZoneSystem.GetZone(p);
            if (s_zoneHm.TryGetValue(zone, out var hm) && hm != null && hm.isActiveAndEnabled && hm.IsPointInside(p)) s_hmHits++;
            else
            {
                s_hmWalks++;
                hm = Heightmap.FindHeightmap(p);
                if (hm != null) s_zoneHm[zone] = hm; else s_zoneHm.Remove(zone);
            }
            if (Time.unscaledTime >= s_hmLogAt)
            {
                if (s_hmLogAt > 0f) FiresLogger.LogDebug($"[VoxelZoneStreamer] ground height lookups /60s: {s_hmHits} zone-cached, {s_hmWalks} vanilla walks ({s_zoneHm.Count} zones)");
                s_hmHits = s_hmWalks = 0; s_hmLogAt = Time.unscaledTime + 60f;
                if (s_zoneHm.Count > 512) s_zoneHm.Clear();
            }
            return hm;
        }

        private const float FallbackGroundY = 30f;
        private static bool s_warnedNonFiniteGround;

        // The analytic generator's ground, for columns the heightmap cannot answer.
        internal static float GeneratedGround(float wx, float wz)
        {
            var wg = WorldGenerator.instance;
            return wg != null ? wg.GetHeight(wx, wz) : FallbackGroundY;
        }

        // A non-finite height never resolves, so it must NOT count as a provisional
        // border: the zone would rewrap forever.
        private static void WarnNonFiniteGround(float wx, float wz)
        {
            if (s_warnedNonFiniteGround) return;
            s_warnedNonFiniteGround = true;
            FiresLogger.LogWarning($"[FiresVoxel] Non-finite heightmap height at ({wx:F0}, {wz:F0}) - a terrain void "
                + "or another mod wrote NaN. Those columns build on generated ground; voxel worlds do not mirror terrain holes.");
        }

        // A dedicated server keeps the generated density of the zones around its players, unrendered, so whatever
        // it places or checks underground or on skylands reads the same terrain its clients build.
        private static bool ServerHoldsVoxelData => FiresVoxelConfig.GeneratesCaves || FiresVoxelConfig.BuildsSkyIslands;

        // The dedicated server executable, not "no graphics device": a -nographics client (the headless test bot) streams
        // around its own player like any client, its chunks colliders only.
        private static bool IsDedicatedServer => FiresCore.Lifecycle.FiresMod.IsDedicatedServer;

        private static bool WantsServerData(Vector2s zone)
        {
            if (FiresVoxelConfig.GeneratesCaves && Zones.ZoneModeRegistry.HasRealTerrain(zone)) return true;
            float minX = zone.x * 64f - 32f, minZ = zone.y * 64f - 32f;
            return FiresVoxelConfig.BuildsSkyIslands && FiresSkyIslands.TryGetBand(minX, minZ, minX + 64f, minZ + 64f, out _, out _);
        }

        private static int OpenMaskOf(Vector2s zone)
        {
            if (!Zones.ZoneModeRegistry.ModeActive) return 0;
            int mask = 0, bit = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++, bit++)
                    if (Zones.ZoneModeRegistry.IsOpen(new Vector2s(zone.x + dx, zone.y + dy))) mask |= 1 << bit;
            return mask;
        }

        public static bool AreaSolid(Vector3 point)
        {
            var zone = ZoneSystem.GetZone(point);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var neighbour = new Vector2s(zone.x + dx, zone.y + dz);
                    if (IsZoneSolid(neighbour) || ((dx != 0 || dz != 0) && NeverWraps(neighbour))) continue;
                    return false;
                }
            return true;
        }

        // The spawn/teleport gate for every voxel world, not only VoxelOnly (on a converted voxel world the
        // login dropped the player at heightmap height, then the voxel ground built over them). A zone is ready when its
        // voxel ground is swapped in, or when it never becomes voxel at all (ZoneReplace/PlacedOnly ground the streamer
        // won't wrap), so a heightmap neighbour is never waited on.
        public static bool SpawnAreaReady(Vector3 point)
        {
            var zone = ZoneSystem.GetZone(point);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var z = new Vector2s(zone.x + dx, zone.y + dz);
                    if (IsZoneSolid(z) || ((dx != 0 || dz != 0) && NeverWraps(z))) continue;
                    if (!IsZoneKnown(z) && !ShouldWrapZone(z, ZoneSystem.GetZonePos(z))) continue;
                    return false;
                }
            return ArrivalCollidersReady(point);
        }

        // After each TeleportTo a tester stood 4-8 m UNDER the surface
        // it was sent to: a swapped zone can still have chunks under the arrival point whose edit anchor's latest revision is not
        // applied yet, or whose collider is being (re)baked - close chunks being placed or restored are exactly that - and the
        // player was released onto ground with no collider and fell through. The arrival also waits for every chunk in the column
        // under the point (ArrivalReach round it, ArrivalDepth down, ArrivalRise up: the point may be the asked height, not the
        // ground's) to have its anchor applied and its collider assigned with no bake in flight (FatGroundProbe.CollidersReady's
        // tests). EBM's RespawnNet fail-open stays the safety net.
        private const float ArrivalReach = 16f, ArrivalDepth = 64f, ArrivalUnder = 8f, ArrivalRise = 16f, ArrivalLogSeconds = 2f;
        private static float s_nextArrivalLog;
        private static readonly List<Vector3Int> s_arrivalWaiting = new List<Vector3Int>();

        private static bool ArrivalCollidersReady(Vector3 p)
        {
            s_arrivalWaiting.Clear();
            int x0 = Mathf.FloorToInt((p.x - ArrivalReach) / 32f), x1 = Mathf.FloorToInt((p.x + ArrivalReach) / 32f);
            // The arrival's own layer (a surface arrival must not wait on cave chunks far below it): from ArrivalUnder
            // beneath the lower of the point and the ground there (a surface arrival stops just under the surface; one inside a cave,
            // whose point is under the ground, covers its own floor) up to ArrivalRise over the higher. No ground known: ArrivalDepth.
            float low = p.y - ArrivalDepth, high = p.y + ArrivalRise;
            if (TryGroundHeight(p.x, p.z, out float ground))
            {
                low = Mathf.Min(p.y, ground) - ArrivalUnder;
                high = Mathf.Max(p.y, ground) + ArrivalRise;
            }
            int y0 = Mathf.FloorToInt(low / 32f), y1 = Mathf.FloorToInt(high / 32f);
            int z0 = Mathf.FloorToInt((p.z - ArrivalReach) / 32f), z1 = Mathf.FloorToInt((p.z + ArrivalReach) / 32f);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        var key = new Vector3Int(x, y, z);
                        bool waits = VoxelChunkAnchor.TryGet(key, out var anchor) && anchor != null && !anchor.Applied;
                        if (!waits && VoxelWorld.TryGetView(key, out var view) && view != null && view.VertexCount > 0)
                            waits = view.BakeInFlight || !view.ColliderReady;
                        if (waits) s_arrivalWaiting.Add(key);
                    }
            if (s_arrivalWaiting.Count == 0) return true;
            if (FiresLogger.DebugEnabled && Time.realtimeSinceStartup >= s_nextArrivalLog)
            {
                s_nextArrivalLog = Time.realtimeSinceStartup + ArrivalLogSeconds;
                FiresLogger.LogDebug($"[FiresVoxel] arrival at ({p.x:0.0},{p.y:0.0},{p.z:0.0}) waits only on the colliders under it: "
                    + $"{s_arrivalWaiting.Count} chunk(s) {string.Join(" ", s_arrivalWaiting)} (anchor not applied, or collider not assigned / baking).");
            }
            return false;
        }

        // A neighbour the zone gate holds closed, and the streamer has not taken up, never wraps: nothing loads there. A spawn
        // or teleport beside closed land waited on it to the 45 s fail-open, every time. The zone stood on is always awaited.
        private static bool NeverWraps(Vector2s zone)
            => Zones.ZoneModeRegistry.ModeActive && !Zones.ZoneModeRegistry.IsOpen(zone) && !IsZoneKnown(zone);

        // Spawn-gate diagnostics: which of the 3x3 zones are not solid, and
        // exactly what each is waiting on — the same accounting the swap-block
        // warning uses. The gate burned its full 45s timeout on every login
        // with no way to see why; this makes the blocker one log line.
        // [Voxel Terrain] "Spawn gate re-queue watchdog" (default OFF): a chunk around the gated point that is pending a
        // generate but in no queue for more than StrandedSeconds is put back on the mesher queue (the worker generates a pending
        // chunk it dequeues), one Warning line each. Returns how many were re-queued.
        private const float StrandedSeconds = 5f;

        internal static int RequeueStranded(Vector3 point)
        {
            var zone = ZoneSystem.GetZone(point);
            int n = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!s_zones.TryGetValue(new Vector2s(zone.x + dx, zone.y + dz), out var w)) continue;
                    foreach (var key in w.Keys)
                    {
                        if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk.Kind != ChunkKind.Full) continue;
                        if (!chunk.PendingGenerate || chunk.Queued) continue;
                        float since = Time.realtimeSinceStartup - chunk.PendingSince;
                        if (since < StrandedSeconds) continue;
                        VoxelMeshPipeline.EnqueueRemesh(chunk);
                        n++;
                        FiresLogger.LogWarning($"[FiresVoxel] spawn gate watchdog: chunk {key} in zone ({zone.x + dx},{zone.y + dz}) was pending a generate in no queue for {since:0} s; re-queued ({chunk.GenFailCount} earlier fail(s))");
                    }
                }
            return n;
        }

        public static string DescribeArea(Vector3 point)
        {
            var zone = ZoneSystem.GetZone(point);
            var sb = new System.Text.StringBuilder(192);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var z = new Vector2s(zone.x + dx, zone.y + dz);
                    if (IsZoneSolid(z)) continue;
                    if ((dx != 0 || dz != 0) && NeverWraps(z)) { sb.Append($"({z.x},{z.y}):CLOSED(not awaited) "); continue; }
                    if (!s_zones.TryGetValue(z, out var w))
                    { sb.Append($"({z.x},{z.y}):UNWRAPPED "); continue; }
                    int pendingGen = 0, queued = 0, noView = 0, noCollider = 0, full = 0;
                    // A "(3,-53):gen=1 q=0" once held the gate 45 s: a pending chunk counts under gen whether or not it
                    // is queued, so name each one with its real state (queued or stranded, generate failures, how long pending).
                    System.Text.StringBuilder pend = null;
                    foreach (var key in w.Keys)
                    {
                        if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk.Kind != ChunkKind.Full) continue;
                        full++;
                        if (chunk.PendingGenerate)
                        {
                            pendingGen++;
                            // "STRANDED, pending 1 s" misread as a strand: under 5 s unqueued is a fresh re-prep (the watchdog's own 5 s rule).
                            (pend ??= new System.Text.StringBuilder()).Append($" [{key} {(chunk.Queued ? "queued" : Time.realtimeSinceStartup - chunk.PendingSince < 5f ? "re-prepped, not queued yet" : "STRANDED")}, {chunk.GenFailCount} fail(s), pending {Time.realtimeSinceStartup - chunk.PendingSince:0} s]");
                        }
                        else if (chunk.Queued) queued++;
                        else if (!VoxelWorld.TryGetView(key, out var view) || view == null) noView++;
                        else if (!view.ColliderReady) noCollider++;
                    }
                    sb.Append($"({z.x},{z.y}):gen={pendingGen} q={queued} noView={noView} noCol={noCollider}/{full}{pend} ");
                }
            return sb.Length == 0 ? "all solid" : sb.ToString().TrimEnd();
        }

        // Streamer runs (meshes voxel content) in every Terrain Mode except
        // Off. WHICH zones actually wrap is decided per-zone by ShouldWrapZone
        // — FullVoxel wraps everything near a center, PlacedOnly/ZoneReplace
        // only wrap voxel-brushed (and sky-island) zones.
        private static bool ModeActive => FiresVoxelConfig.StreamerRuns;

        internal static bool IsVoxelWorldActive => ModeActive;

        // Zones holding voxel brush edits (ZoneReplace/PlacedOnly) — they wrap
        // on demand and stay wrapped while near a center. Armed by the brush
        // and by every anchor restore, so a carved zone converts again on
        // reload. Pickaxe digs arm it too (they are voxel carves); hoe and
        // cultivator edits never do.
        private static readonly HashSet<Vector2s> s_brushedZones = new HashSet<Vector2s>();

        // Zone (zx,zy) covers chunk columns 2zx−1..2zx.
        private static Vector2s ZoneOfChunk(Vector3Int chunkKey)
            => new Vector2s(FloorDiv(chunkKey.x + 1, 2), FloorDiv(chunkKey.z + 1, 2));

        // 0.2.1346: who first brushed a zone, for the "wraps because" line.
        private static readonly Dictionary<Vector2s, string> s_brushedWhy = new Dictionary<Vector2s, string>();

        internal static void MarkZoneBrushed(Vector3Int chunkKey, string why = "a live edit")
        {
            var zone = ZoneOfChunk(chunkKey);
            if (!s_brushedWhy.ContainsKey(zone)) s_brushedWhy[zone] = why + " in chunk " + chunkKey;
            if (s_brushedZones.Add(zone) && FiresVoxelConfig.WrapsBrushedZones)
            {
                if (FiresVoxelConfig.ServerConvertsEdits)
                    FiresLogger.LogInfo($"[FiresVoxel] server: zone ({zone.x},{zone.y}) holds voxel edits -> converting (this world swaps zones only on edit)");
                else
                    FiresLogger.LogInfo($"[FiresVoxel] zone ({zone.x},{zone.y}) holds voxel edits → converting to voxel (mode {FiresVoxelConfig.Mode} from the world's profile).");
            }
        }

        // After a Restore brush cleared a chunk: a zone with no brush edits
        // left returns to vanilla terrain (FullVoxel keeps wrapping everything).
        internal static void RefreshBrushedZone(Vector3Int chunkKey)
        {
            var zone = ZoneOfChunk(chunkKey);
            if (!s_brushedZones.Contains(zone)) return;
            int kx0 = zone.x * 2 - 1, kz0 = zone.y * 2 - 1;
            foreach (var chunk in VoxelWorld.AllChunks)
            {
                if (chunk == null || !chunk.HasBrushEdits) continue;
                if (chunk.Key.x >= kx0 && chunk.Key.x <= kx0 + 1 && chunk.Key.z >= kz0 && chunk.Key.z <= kz0 + 1)
                    return;
            }
            s_brushedZones.Remove(zone);
            s_brushedWhy.Remove(zone);   // 0.2.1346
            if (FiresVoxelConfig.WrapsAllTerrain || !s_zones.ContainsKey(zone)) return;
            FiresLogger.LogInfo($"[FiresVoxel] zone ({zone.x},{zone.y}) has no voxel brush edits left → back to vanilla terrain.");
            Unwrap(zone);
        }

        // ---- 0.2.1334 (Fire: never two meshes; a first edit may wait for the swap)
        // Only BuildZoneChunks' new chunks waited for the swap. A chunk made by an edit (brush, or a peer's record through
        // VoxelChunkAnchor) showed as soon as it meshed, on top of vanilla's ground. Now any chunk of a zone with a swap coming is
        // hidden: at view creation (HideEditChunkUntilSwap) and when a wrap picks up chunks that already have a view
        // (HideExistingChunksAtWrap). TrySwap shows them in the swap's frame. TickEditHides is the safety: no swap coming, or one
        // that fails to land while the player is in reach, shows them again (a hidden edit forever is worse than a double mesh).
        private sealed class HiddenEdits
        {
            public readonly List<Vector3Int> Keys = new List<Vector3Int>();
            public float Since;
            public float NearSince = -1f;
        }

        private static readonly Dictionary<Vector2s, HiddenEdits> s_hiddenEdits = new Dictionary<Vector2s, HiddenEdits>();
        private static readonly HashSet<Vector2s> s_hideGaveUp = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_scratchHidden = new List<Vector2s>();
        private const float EditHideMaxSeconds = 60f;   // in reach of the player with the swap still not landed
        private static bool s_hideInfoDone, s_offRuleInfoDone;

        // Will this zone swap? False for: mode off, a dedicated server (nothing renders), a swapped zone, a closed zone-mode zone with no wrap
        // (nothing loads there), and a zone that would not wrap (no edits, not auto-swap).
        private static bool SwapComing(Vector2s zone)
        {
            if (s_driverGO == null || !ModeActive || IsDedicatedServer) return false;
            if (s_zones.TryGetValue(zone, out var wrap)) return !wrap.DataOnly && !wrap.Swapped && wrap.Hm != null;
            if (Zones.ZoneModeRegistry.ModeActive && !Zones.ZoneModeRegistry.IsOpen(zone)) return false;
            return ShouldWrapZone(zone, ZoneSystem.GetZonePos(zone));
        }

        // The same rule as a new streamed chunk: VoxelOnly has no vanilla ground to double, except a skyland's far piece.
        private static bool HidesForSwap(VoxelChunk chunk)
            => !VoxelOnlyMode.Active || FiresSkyIslands.ChunkOverlaps(chunk.WorldOrigin);

        private static void TrackHidden(Vector2s zone, Vector3Int key)
        {
            if (!s_hiddenEdits.TryGetValue(zone, out var entry))
                s_hiddenEdits[zone] = entry = new HiddenEdits { Since = Time.time };
            if (!entry.Keys.Contains(key)) entry.Keys.Add(key);
            FiresLogger.LogDebug($"[FiresVoxel] edit chunk ({key.x},{key.y},{key.z}) hidden until swap of zone ({zone.x},{zone.y})");
            if (!s_hideInfoDone)
            {
                s_hideInfoDone = true;
                FiresLogger.LogInfo("[FiresVoxel] edit chunks wait for their zone's swap (voxel on, vanilla off in one frame)");
            }
        }

        // From VoxelWorld's view creation, so a brush edit and a peer's record take the same way.
        internal static void HideEditChunkUntilSwap(VoxelChunk chunk)
        {
            if (chunk == null || chunk.SuppressRenderUntilSwap) return;
            var zone = ZoneOfChunk(chunk.Key);
            if (s_hideGaveUp.Contains(zone) || !HidesForSwap(chunk) || !SwapComing(zone)) return;
            chunk.SuppressRenderUntilSwap = true;
            TrackHidden(zone, chunk.Key);
        }

        // A wrap picks up every chunk already in its columns, in or out of its key range (an edit can sit above the ground band),
        // so TrySwap's release covers them through s_hiddenEdits.
        private static void HideExistingChunksAtWrap(ZoneWrap wrap)
        {
            if (wrap.DataOnly || wrap.Swapped || IsDedicatedServer || s_hideGaveUp.Contains(wrap.Zone)) return;
            int kx0 = wrap.Zone.x * 2 - 1, kz0 = wrap.Zone.y * 2 - 1;
            for (int dz = 0; dz < 2; dz++)
                for (int dx = 0; dx < 2; dx++)
                {
                    var stack = VoxelWorld.ColumnChunks(new Vector2Int(kx0 + dx, kz0 + dz));
                    if (stack == null) continue;
                    foreach (var chunk in stack)
                    {
                        if (chunk == null || chunk.SuppressRenderUntilSwap || chunk.Kind != ChunkKind.Full || !HidesForSwap(chunk)) continue;
                        chunk.SuppressRenderUntilSwap = true;
                        if (VoxelWorld.TryGetView(chunk.Key, out var view) && view != null) view.SetRenderSuppressed(true);
                        TrackHidden(wrap.Zone, chunk.Key);
                    }
                }
        }

        private static int ShowHidden(HiddenEdits entry)
        {
            int shown = 0;
            foreach (var key in entry.Keys)
            {
                if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null) continue;
                chunk.SuppressRenderUntilSwap = false;
                if (VoxelWorld.TryGetView(key, out var view) && view != null) view.SetRenderSuppressed(false);
                shown++;
            }
            return shown;
        }

        // Every tick, ahead of Tick's early returns (mode off, no player, hydrology building).
        private static void TickEditHides()
        {
            ZoneReveal.Tick();   // 0.2.1346
            // 0.2.1346: only with the world's profile known. r22 printed this at the menu ("its .profile: heightmap") from Mode's no-profile default, 400 lines
            // before the server's profile said VoxelWrap, which is why SFSC3 looked like a swap-on-edit world.
            if (!s_offRuleInfoDone && FiresWorldProfile.Current != null && FiresVoxelConfig.Mode == FiresTerrainMode.ZoneReplace)
            {
                s_offRuleInfoDone = true;
                FiresLogger.LogInfo("[FiresVoxel] this world's zones swap to voxel only on edit, on every peer (its .profile: heightmap) (0.2.1336)");
            }
            if (s_hiddenEdits.Count == 0) return;
            s_scratchHidden.Clear();
            s_scratchHidden.AddRange(s_hiddenEdits.Keys);
            foreach (var zone in s_scratchHidden)
            {
                var entry = s_hiddenEdits[zone];
                entry.Keys.RemoveAll(k => !VoxelWorld.TryGetChunk(k, out var c) || c == null);
                if (entry.Keys.Count == 0) { s_hiddenEdits.Remove(zone); continue; }
                string why = null;
                if (!SwapComing(zone)) why = "no swap is coming";
                else if (s_centers.Count > 0 && NearAnyCenter(ZoneSystem.GetZonePos(zone), GovernorBound(FiresVoxelConfig.WrapRadius.Value)))
                {
                    if (entry.NearSince < 0f) entry.NearSince = Time.time;
                    if (Time.time - entry.NearSince > EditHideMaxSeconds)
                    {
                        why = $"its swap has not landed {EditHideMaxSeconds:0} s with the player in reach";
                        s_hideGaveUp.Add(zone);   // not hidden again until the zone swaps or unwraps
                    }
                }
                else entry.NearSince = -1f;   // out of reach: vanilla's ground stands until the player comes back and the zone wraps
                if (why == null) continue;
                int shown = ShowHidden(entry);
                s_hiddenEdits.Remove(zone);
                FiresLogger.LogDebug($"[FiresVoxel] zone ({zone.x},{zone.y}): {shown} hidden edit chunk(s) shown after {(Time.time - entry.Since) * 1000f:0} ms without a swap: {why}");
            }
        }

        // ---- 0.2.1346 ZoneReveal's questions. A zone out of wrap reach keeps drawing (far water over far ground); before the first centre (login) it holds.
        internal static float EditHideSeconds => EditHideMaxSeconds;
        internal static bool RevealHasCentre => s_centers.Count > 0;   // 0.2.1354 (review: water hidden forever outside wrap reach)
        internal static void GiveUpHide(Vector2s zone) => s_hideGaveUp.Add(zone);
        internal static bool RevealInReach(Vector2s zone)
            => s_centers.Count > 0 && NearAnyCenter(ZoneSystem.GetZonePos(zone), GovernorBound(FiresVoxelConfig.WrapRadius.Value));
        internal static bool RevealHolds(Vector2s zone)
        {
            if (VoxelOnlyMode.Active || s_hideGaveUp.Contains(zone) || !SwapComing(zone)) return false;
            return s_zones.ContainsKey(zone) || s_centers.Count == 0 || RevealInReach(zone);
        }
        // 0.2.1353 (Fire: "get this terrain loading thing fixed"; 45 "no swap is coming" releases a log): RevealHolds says false for a zone that fell out of wrap reach unwrapped, though
        // SwapComing is true and the zone swaps on the player's return. Anything already held there stays held until that swap, or until the zone unloads.
        internal static bool RevealSwapStillComing(Vector2s zone)
            => !VoxelOnlyMode.Active && !s_hideGaveUp.Contains(zone) && SwapComing(zone);
        internal static bool RevealZoneLoaded(Vector2s zone)
            => ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(zone);

        // 0.2.1346: anything of this zone still on its way to ground a ray can hit: its swap, or an edit's remesh / collider.
        internal static bool ZoneEditBusy(Vector2s zone)
        {
            if (ZoneMidSwap(zone) || AwaitsWrap(zone)) return true;
            int kx0 = zone.x * 2 - 1, kz0 = zone.y * 2 - 1;
            for (int dz = 0; dz < 2; dz++)
                for (int dx = 0; dx < 2; dx++)
                {
                    var stack = VoxelWorld.ColumnChunks(new Vector2Int(kx0 + dx, kz0 + dz));
                    if (stack == null) continue;
                    foreach (var chunk in stack)
                    {
                        if (chunk == null || chunk.Kind != ChunkKind.Full) continue;
                        if (chunk.PendingGenerate || chunk.Queued) return true;
                        if (VoxelWorld.TryGetView(chunk.Key, out var view) && view != null && (!view.ColliderReady || view.BakeInFlight)) return true;
                    }
                }
            return false;
        }

        // 0.2.1346: the swap waits a bounded moment for the zone's own paint columns and snow, so they land with it. True = go.
        private const float RevealPrepCapSeconds = 3f;
        private static bool s_swapBeforePaintProof;   // 0.2.1353
        private static bool RevealPrepReady(ZoneWrap wrap)
        {
            if (!ZoneReveal.On || IsDedicatedServer) return true;
            // 0.2.1353 (Fire: "get this terrain loading thing fixed"): the 1327 order. No wait: PrepSince stays unset, so TrySwap's last step marks the zone's paint and it lands in the
            // following frames (the set keeps every column until it is composed). Snow's wait had no reason of its own: the 1346 line above names both "so they land with it", and
            // both are textures on the swapped chunks, not meshes, so "never two meshes" is untouched. Snow keeps draining on its own budget with no swap in the way.
            if (FiresVoxelConfig.SwapBeforePaint != null && FiresVoxelConfig.SwapBeforePaint.Value)
            {
                if (!s_swapBeforePaintProof) { s_swapBeforePaintProof = true; FiresLogger.LogInfo("[FiresVoxel] a zone swaps without waiting for its paint and snow; they land right after (0.2.1353)"); }
                return true;
            }
            float now = Time.realtimeSinceStartup;
            if (wrap.PrepSince < 0f) { wrap.PrepSince = now; MarkZonePaintDirty(wrap.Zone); }
            int cx0 = wrap.Zone.x * 2 - 1, cz0 = wrap.Zone.y * 2 - 1;
            bool waiting = VoxelSnowDepth.ZoneWorkPending(wrap.Zone);
            for (int dz = 0; dz < 2 && !waiting; dz++)
                for (int dx = 0; dx < 2 && !waiting; dx++)
                    if (VoxelWorld.PaintColumnPending(new Vector2Int(cx0 + dx, cz0 + dz))) waiting = true;
            if (!waiting) return true;
            if (now - wrap.PrepSince < RevealPrepCapSeconds) return false;
            wrap.PrepCapped = true;
            return true;
        }

        // 0.2.1346: stage marks from the mesh driver (main thread). Debug only.
        internal static void NoteStageApply(Vector3Int key, float meshMs)
        {
            if (!FiresLogger.DebugEnabled || !s_zones.TryGetValue(ZoneOfChunk(key), out var w) || w.Swapped) return;
            w.TApply = System.Diagnostics.Stopwatch.GetTimestamp(); w.NApply++; w.MeshMs += meshMs;
            if (w.AppliesByChunk == null) w.AppliesByChunk = new Dictionary<Vector3Int, int>();   // 0.2.1353
            w.AppliesByChunk.TryGetValue(key, out int n);
            w.AppliesByChunk[key] = n + 1;
        }
        // 0.2.1353: VoxelMeshPipeline's hold asks this.
        internal static bool ZoneHoldsRemeshes(Vector3Int key, out Vector2s zone)
        {
            zone = ZoneOfChunk(key);
            return ZoneHoldsRemeshes(zone);
        }
        internal static bool ZoneHoldsRemeshes(Vector2s zone) => !IsDedicatedServer && ZoneMidSwap(zone);
        // 0.2.1353 (proof, Debug): the swap's paint landing, per zone. A column dropped meanwhile leaves no line.
        internal static void NotePaintColumnLanded(Vector2Int col)
        {
            if (!FiresLogger.DebugEnabled) return;
            var zone = ZoneOfChunk(new Vector3Int(col.x, 0, col.y));
            if (!s_zones.TryGetValue(zone, out var w) || w.PaintLeft == null || !w.PaintLeft.Remove(col) || w.PaintLeft.Count > 0) return;
            w.PaintLeft = null;
            FiresLogger.LogDebug($"[FiresVoxel] zone ({zone.x},{zone.y}) paint landed {(System.Diagnostics.Stopwatch.GetTimestamp() - w.TSwapDone) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms after its swap (0.2.1353)");
        }
        private static void NotePaintLeft(ZoneWrap w)
        {
            if (!FiresLogger.DebugEnabled) return;
            w.TSwapDone = System.Diagnostics.Stopwatch.GetTimestamp();
            w.PaintLeft = null;
            for (int dz = 0; dz < 2; dz++)
                for (int dx = 0; dx < 2; dx++)
                {
                    var col = new Vector2Int(w.Zone.x * 2 - 1 + dx, w.Zone.y * 2 - 1 + dz);
                    if (!VoxelWorld.PaintColumnPending(col)) continue;
                    if (w.PaintLeft == null) w.PaintLeft = new HashSet<Vector2Int>();
                    w.PaintLeft.Add(col);
                }
        }
        // 0.2.1353 proof: the stages line's new clause.
        private static string SwapApplyProof(ZoneWrap w)
        {
            int chunks = 0, most = 0, total = 0;
            if (w.AppliesByChunk != null)
                foreach (var kv in w.AppliesByChunk) { chunks++; total += kv.Value; if (kv.Value > most) most = kv.Value; }
            w.AppliesByChunk = null;
            int paint = 0;
            for (int dz = 0; dz < 2; dz++)
                for (int dx = 0; dx < 2; dx++)
                    if (VoxelWorld.PaintColumnPending(new Vector2Int(w.Zone.x * 2 - 1 + dx, w.Zone.y * 2 - 1 + dz))) paint++;
            return $"; applies per chunk {(chunks > 0 ? (double)total / chunks : 0):0.0} (worst {most}, {chunks} chunk(s)); remeshes held {w.HeldAsks} ask(s) on {w.HeldChunks} chunk(s) run after the swap{(w.HeldGaveUp ? ", gave up holding" : "")}; at the swap {paint} paint column(s) and {(VoxelSnowDepth.ZoneWorkPending(w.Zone) ? "snow work" : "no snow work")} pending (0.2.1353)";
        }
        internal static void NoteStageCollider(Vector3Int key)
        {
            if (FiresLogger.DebugEnabled && s_zones.TryGetValue(ZoneOfChunk(key), out var w) && !w.Swapped) w.TCollider = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        private static void LogSwapStages(ZoneWrap w)
        {
            if (!FiresLogger.DebugEnabled || w.T0 == 0) return;
            long gen = 0, mesh = 0;
            foreach (var key in w.Keys)
                if (VoxelWorld.TryGetChunk(key, out var c) && c != null)
                {
                    if (c.GenDoneTicks > gen) gen = c.GenDoneTicks;
                    if (c.MeshDoneTicks > mesh) mesh = c.MeshDoneTicks;
                }
            long now = System.Diagnostics.Stopwatch.GetTimestamp(), last = w.T0;
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string Step(long t) { if (t <= last) return "0"; double ms = (t - last) * f; last = t; return ms.ToString("0"); }
            string g = Step(w.TGathered), ge = Step(gen), m = Step(mesh), a = Step(w.TApply), co = Step(w.TCollider);
            string pr = Step(w.TPrep), sw = Step(now);
            FiresLogger.LogDebug($"[FiresVoxel] zone ({w.Zone.x},{w.Zone.y}) stages ms: wrap start -> gather {g} -> generate {ge} -> mesh {m} (worker sum {w.MeshMs:0}) -> apply {a} ({w.NApply} applies) -> collider {co} -> paint+snow {pr}{(w.PrepCapped ? " (capped)" : "")} -> swap {sw}; total {(now - w.T0) * f:0} (0.2.1346){ZoneProofStages(w.Zone, w.T0, now)}{SwapApplyProof(w)}");
        }

        // 0.2.1350 (proof only, client, Debug): a zone's timeline against its heightmap's spawn. Every stamp is a one-off event for the zone, none per frame.
        private sealed class ZoneProof
        {
            public long THm, TAnchorBorn, TAnchorKnown, TBlobFirst, TBlobLast, TWater, TSwap;
            public int ZdosAtHm = -1, ZdosAtWrap = -1, AnchorsKnown, Blobs, Disks;
            public bool LateAnchorSaid, WaterSaid;
        }
        private static readonly Dictionary<Vector2s, ZoneProof> s_zoneProof = new Dictionary<Vector2s, ZoneProof>();
        private static ZoneProof ProofOf(Vector2s zone)
        {
            if (!s_zoneProof.TryGetValue(zone, out var p)) { p = new ZoneProof(); s_zoneProof[zone] = p; }
            return p;
        }
        private static long ProofNow() => System.Diagnostics.Stopwatch.GetTimestamp();
        private static string ProofMs(long from, long to)
            => from == 0 || to == 0 ? "none" : ((to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("+0;-0;0");

        // FatVanillaHooks' Heightmap.OnEnable postfix: the zone's spawn.
        internal static void NoteHeightmapEnabled(Heightmap hm)
        {
            if (IsDedicatedServer || hm == null || s_isDistantLod(hm)) return;
            var zone = ZoneSystem.GetZone(hm.transform.position);
            var p = ProofOf(zone);
            p.THm = ProofNow(); p.TSwap = 0; p.WaterSaid = false; p.LateAnchorSaid = false;
            p.ZdosAtHm = VoxelChunkAnchor.CountZoneAnchors(zone);
            SayZoneWater(zone, p);
        }
        private static void NoteWrapProof(Vector2s zone)
        {
            if (!FiresLogger.DebugEnabled || IsDedicatedServer) return;
            ProofOf(zone).ZdosAtWrap = VoxelChunkAnchor.CountZoneAnchors(zone);
        }
        // VoxelChunkAnchor: an anchor instance read its chunk key (its 0.5 s poll); ageSeconds = since the instance's Awake.
        internal static void NoteAnchorKnown(Vector3Int key, float ageSeconds)
        {
            if (!FiresLogger.DebugEnabled || IsDedicatedServer) return;
            var zone = ZoneOfChunk(key);
            var p = ProofOf(zone);
            long now = ProofNow();
            if (p.TAnchorKnown == 0)
            {
                p.TAnchorKnown = now;
                p.TAnchorBorn = now - (long)(ageSeconds * System.Diagnostics.Stopwatch.Frequency);
            }
            p.AnchorsKnown++;
            SayLateAnchor(zone, p, "anchor known", now);
        }
        // VoxelChunkAnchor: an anchor's edit blob applied to its chunk.
        internal static void NoteAnchorApplied(Vector3Int key)
        {
            if (!FiresLogger.DebugEnabled || IsDedicatedServer) return;
            var zone = ZoneOfChunk(key);
            var p = ProofOf(zone);
            long now = ProofNow();
            if (p.TBlobFirst == 0) p.TBlobFirst = now;
            p.TBlobLast = now; p.Blobs++;
            SayLateAnchor(zone, p, "blob applied", now);
        }
        // An edit that reaches a zone after its swap means the zone showed its pre-edit ground. Once per zone spawn.
        private static void SayLateAnchor(Vector2s zone, ZoneProof p, string what, long now)
        {
            if (p.TSwap == 0 || p.LateAnchorSaid) return;
            p.LateAnchorSaid = true;
            FiresLogger.LogDebug($"[FiresVoxel] zone ({zone.x},{zone.y}) {what} {ProofMs(p.TSwap, now)} ms AFTER its swap (0.2.1350)");
        }
        // PlacedWaterSheets: a disk this client did not know before. Every zone the disk overlaps.
        internal static void NoteWaterDisk(float x, float z, float r)
        {
            if (!FiresLogger.DebugEnabled || IsDedicatedServer) return;
            var a = ZoneSystem.GetZone(new Vector3(x - r, 0f, z - r));
            var b = ZoneSystem.GetZone(new Vector3(x + r, 0f, z + r));
            long now = ProofNow();
            for (int zy = a.y; zy <= b.y; zy++)
                for (int zx = a.x; zx <= b.x; zx++)
                {
                    var zone = new Vector2s(zx, zy);
                    var p = ProofOf(zone);
                    if (p.TWater == 0) p.TWater = now;
                    p.Disks++;
                    SayZoneWater(zone, p);
                }
        }
        private static void SayZoneWater(Vector2s zone, ZoneProof p)
        {
            if (p.WaterSaid || p.THm == 0 || p.TWater == 0) return;
            p.WaterSaid = true;
            FiresLogger.LogDebug($"[FiresVoxel] zone ({zone.x},{zone.y}) water: first disk known {ProofMs(p.THm, p.TWater)} ms against its heightmap spawn (minus = known before the spawn; 0.2.1350)");
        }
        // The tail of the stage line.
        private static string ZoneProofStages(Vector2s zone, long tWrap, long now)
        {
            if (IsDedicatedServer) return "";
            var p = ProofOf(zone);
            p.TSwap = now;
            int zdos = VoxelChunkAnchor.CountZoneAnchors(zone);
            return $"; from heightmap spawn ms: anchor instance {ProofMs(p.THm, p.TAnchorBorn)}, key known {ProofMs(p.THm, p.TAnchorKnown)} ({p.AnchorsKnown} anchors), blob first {ProofMs(p.THm, p.TBlobFirst)} last {ProofMs(p.THm, p.TBlobLast)} ({p.Blobs} blobs), wrap start {ProofMs(p.THm, tWrap)}, swap {ProofMs(p.THm, now)}; anchor ZDOs in the zone at spawn {p.ZdosAtHm}, wrap {p.ZdosAtWrap}, swap {zdos}; water disks {p.Disks}, first {ProofMs(p.THm, p.TWater)} (0.2.1350)";
        }

        // 0.2.1350 (proof only): what a zone waiting on generate stands behind, at 1 s and 10 s of its wait, at most one line a second overall.
        private static float s_genWaitLogAt;
        private static void LogGenerateWait(ZoneWrap w, int pendingGen, int queued, int noView, int noCollider)
        {
            float waited = Time.time - w.StartedAt;
            int want = waited >= 10f ? 2 : waited >= 1f ? 1 : 0;
            if (want <= w.GenWaitSaid || Time.realtimeSinceStartup < s_genWaitLogAt) return;
            w.GenWaitSaid = want;
            s_genWaitLogAt = Time.realtimeSinceStartup + 1f;
            FiresLogger.LogDebug($"[FiresVoxel] generate wait: zone ({w.Zone.x},{w.Zone.y}) {Mathf.Sqrt(CenterDistSq(w.Hm.transform.position)):0} m away, {waited:0.0} s since wrap start, its chunks pending generate {pendingGen} queued {queued} no view {noView} no collider {noCollider} | {VoxelMeshPipeline.GenerateWaitProof()} (0.2.1350)");
        }

        // 0.2.1350 (proof only, client, every 30 s): the near heightmaps actually drawn against wrap reach and the active area. A drawn one outside
        // reach can never wrap while it stays there; isVisible says whether a camera (or shadow pass) took it in the last frame.
        private static float s_drawnReachLogAt;
        private static void LogDrawnReach(float reach)
        {
            if (Time.realtimeSinceStartup < s_drawnReachLogAt || s_centers.Count == 0 || ZoneSystem.instance == null) return;
            s_drawnReachLogAt = Time.realtimeSinceStartup + 30f;
            var zs = ZoneSystem.instance;
            var centreZone = ZoneSystem.GetZone(s_centers[0]);
            int area = zs.m_simulationDistance.NearSimulationDistance;
            int near = 0, drawn = 0, drawnVisible = 0, outReach = 0, outReachVisible = 0, outActive = 0, outActiveVisible = 0, inReachVanilla = 0, wrapped = 0, swapped = 0, anchoredNoWrap = 0;
            float nearestOut = float.MaxValue;
            var sample = new System.Text.StringBuilder();
            int sampled = 0;
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || s_isDistantLod(hm)) continue;
                near++;
                Vector3 c = hm.transform.position;
                var zone = ZoneSystem.GetZone(c);
                bool inReach = NearAnyCenter(c, reach);
                bool hasWrap = s_zones.TryGetValue(zone, out var w);
                if (hasWrap) { wrapped++; if (w.Swapped) swapped++; }
                else if (s_zoneProof.TryGetValue(zone, out var pr) && (pr.AnchorsKnown > 0 || pr.ZdosAtHm > 0)) anchoredNoWrap++;
                var rend = hm.GetComponent<MeshRenderer>();
                if (rend == null || !rend.enabled || !hm.gameObject.activeInHierarchy || hm.m_renderMesh == null) continue;
                drawn++;
                bool visible = rend.isVisible;
                if (visible) drawnVisible++;
                bool inActive = Mathf.Abs(zone.x - centreZone.x) <= area && Mathf.Abs(zone.y - centreZone.y) <= area;
                if (!inActive) { outActive++; if (visible) outActiveVisible++; }
                if (inReach) { if (!hasWrap || !w.Swapped) inReachVanilla++; continue; }
                outReach++;
                if (visible) outReachVisible++;
                float d = Mathf.Sqrt(CenterDistSq(c));
                if (d < nearestOut) nearestOut = d;
                if (sampled < 6) { sampled++; sample.Append($" ({zone.x},{zone.y}){(visible ? "v" : "")}@{d:0}m"); }
            }
            FiresLogger.LogDebug($"[FiresVoxel] drawn heightmaps: loaded zones {zs.m_zones.Count} (the HUD's count), near heightmaps {near}, drawn {drawn} ({drawnVisible} visible), wrapped {wrapped}, swapped {swapped}; wrap reach {reach:0} m: drawn in reach still vanilla {inReachVanilla}, drawn outside reach {outReach} ({outReachVisible} visible{(outReach > 0 ? $", nearest {nearestOut:0} m" : "")}{sample}); outside active area {area * 2 + 1}x{area * 2 + 1} (simulation distance near {area}, far {zs.m_simulationDistance.FarSimulationDistance}, classic {zs.m_simulationDistance.IsClassic}): {outActive} ({outActiveVisible} visible); not wrapped with anchors known {anchoredNoWrap} (0.2.1350)");
        }

        private static void ShowEditChunksAtSwap(ZoneWrap wrap)
        {
            s_hideGaveUp.Remove(wrap.Zone);
            if (!s_hiddenEdits.TryGetValue(wrap.Zone, out var entry)) return;
            int shown = ShowHidden(entry);
            s_hiddenEdits.Remove(wrap.Zone);
            FiresLogger.LogDebug($"[FiresVoxel] zone swapped: {shown} edit chunks shown after {(Time.time - entry.Since) * 1000f:0} ms (zone ({wrap.Zone.x},{wrap.Zone.y}))");
        }

        // Should a NOT-yet-wrapped zone convert to voxel under the current mode?
        // 0.2.1340: why a zone on a heightmap world swaps, once per zone.
        private static readonly HashSet<Vector2s> s_swapReasonSaid = new HashSet<Vector2s>();
        private static bool NoteSwapReason(Vector2s zone, string why)
        {
            if (s_swapReasonSaid.Add(zone)) FiresLogger.LogDebug($"[FiresVoxel] zone ({zone.x},{zone.y}) swaps: {why}");
            return true;
        }

        private static bool ShouldWrapZone(Vector2s zone, Vector3 zoneCenter)
        {
            if (FiresVoxelConfig.WrapsAllTerrain) return true;                 // FullVoxel (LogWrapWhy names it: this branch is why r22 had 0 "swaps:" lines)
            if (s_brushedZones.Contains(zone)) return NoteSwapReason(zone, "edit");                    // voxel-brushed (ZoneReplace + PlacedOnly)
            // A channel or lake runs through it: convert this zone alone, not
            // the world (Tools/FAT_RIVER_CARVE_SPEC.md stage 5).
            if (FiresVoxelConfig.CarvesRiverBeds && GenWaterField.TouchesZone(zone)) return NoteSwapReason(zone, "river carve");
            if (FiresVoxelConfig.WrapsIslandZones)                             // PlacedOnly: sky-island zones
            {
                float bx = zone.x * 64f - 32f, bz = zone.y * 64f - 32f;
                for (int cz = 0; cz < 64; cz += 32)
                    for (int cx = 0; cx < 64; cx += 32)
                        if (FiresSkyIslands.ChunkOverlaps(new Vector3(bx + cx, 0f, bz + cz))) return true;
            }
            return false;
        }

        // 0.2.1346 proof: which ShouldWrapZone branch took this zone and what made it true. Same order as ShouldWrapZone.
        private static void LogWrapWhy(Vector2s zone)
        {
            if (!FiresLogger.DebugEnabled) return;
            string why;
            if (s_wrapAllCommand) why = "the va_voxel_wrapall command (no ShouldWrapZone)";
            else if (FiresVoxelConfig.WrapsAllTerrain)
            {
                var p = FiresWorldProfile.Current;
                why = $"branch FullVoxel: every near zone wraps; mode {FiresVoxelConfig.Mode} from the world's profile, terrain {(p != null ? p.Terrain.ToString() : "no profile")} (anything but Heightmap = auto swap)";
            }
            else if (s_brushedZones.Contains(zone)) why = "branch edit: brushed by " + (s_brushedWhy.TryGetValue(zone, out var b) ? b : "unknown");
            else if (FiresVoxelConfig.CarvesRiverBeds && GenWaterField.TouchesZone(zone)) why = "branch river carve: a generated channel or lake touches it";
            else why = "no branch true now";
            FiresLogger.LogDebug($"[FiresVoxel] zone ({zone.x},{zone.y}) wraps because {why} (0.2.1346)");
        }

        // ── Lifecycle ────────────────────────────────────────────────────────
        // From FAT's one ZoneSystem.Start hook (FatVanillaHooks), after FarQuilt.
        internal static void OnZoneSystemStart()
        {
            // A dedicated server streams when it builds voxel colliders around its peers (VoxelOnly, and VoxelWrap unless
            // 'Server builds voxel collision' is off) and whenever caves or skylands are
            // generated (their density around its peers, unrendered).
            if (IsDedicatedServer
                && !ServerHoldsVoxelData
                && !FiresVoxelConfig.ServerCollides) return;
            if (s_driverGO != null) return;
            s_driverGO = new GameObject("FiresVoxelZoneStreamer");
            s_driverGO.AddComponent<StreamerDriver>();
            FiresLogger.LogInfo($"[FiresVoxel] zone streamer up (mode {FiresVoxelConfig.Mode} from the world's profile, World Terrain Mode={FiresVoxelConfig.WorldKind}).");
        }

        private sealed class StreamerDriver : MonoBehaviour
        {
            private float _nextTick;

            private void FixedUpdate()
            {
                try { VoxelCavePopGuard.FixedTick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] pop guard threw: {ex.Message}"); }
            }

            // 0.2.1163 (FDT F8: StreamerDriver.Update 13,771 ms over 1,714 stall frames): per-stage ms, Debug every 10 s.
            private static readonly string[] s_sdNames = { "arm+halo", "snow tick", "far impostors", "cave clutter", "underwater clutter", "terraform drain", "streamer tick" };
            private static readonly double[] s_sdMs = new double[7], s_sdMax = new double[7];
            private static long s_sdMark; private static float s_sdLogAt; private static int s_sdFrames;
            private static void SdMark(int i)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double ms = (now - s_sdMark) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                s_sdMs[i] += ms; if (ms > s_sdMax[i]) s_sdMax[i] = ms;
                s_sdMark = now;
            }
            private static void SdReport()
            {
                s_sdFrames++;
                if (!FiresLogger.DebugEnabled || Time.realtimeSinceStartup < s_sdLogAt) return;
                s_sdLogAt = Time.realtimeSinceStartup + 10f;
                var sb = new System.Text.StringBuilder("[FiresVoxel] streamer driver 10 s (ms total / worst one frame): ");
                for (int i = 0; i < s_sdMs.Length; i++) sb.Append(s_sdNames[i]).Append(' ').Append(s_sdMs[i].ToString("0.0")).Append(" / ").Append(s_sdMax[i].ToString("0.0")).Append("; ");
                sb.Append(s_sdFrames).Append(" frame(s) (0.2.1163)");
                FiresLogger.LogDebug(sb.ToString());
                System.Array.Clear(s_sdMs, 0, s_sdMs.Length); System.Array.Clear(s_sdMax, 0, s_sdMax.Length); s_sdFrames = 0;
            }

            private void Update()
            {
                // Retries until the scene's RenderGroupSystem exists; a no-op
                // once subscribed. Armed here rather than in the wrap path
                // because brush chunks render in Terrain Mode Off too.
                s_sdMark = System.Diagnostics.Stopwatch.GetTimestamp();
                try { UpdateStages(); } finally { SdReport(); }
            }

            private void UpdateStages()
            {
                VoxelRenderGroup.Arm();
                try { FatTrailBatch.Tick(); }   // [saddles] trail op batching: flush queued stamps (FatTrailBatch)
                catch (System.Exception ex) { FiresLogger.LogDebug($"[TrailBatch] tick threw: {ex.Message}"); }
                try { FatHaloArc.Tick(); }   // 0.2.1027: the halo spike arc (a global key) arrived or changed
                catch (System.Exception ex) { FiresLogger.LogDebug($"[FatHalo] tick threw: {ex.Message}"); }
                SdMark(0);
                try { VoxelSnowDepth.Tick(); }   // 0.2.1027: SnowField cover globals + the zones it changed
                catch (System.Exception ex) { FiresLogger.LogDebug($"[FiresVoxel] snow tick threw: {ex.Message}"); }
                SdMark(1);

                // Impostors draw per frame (DrawMeshInstanced is a one-frame
                // submission) — outside the 0.25s streamer throttle.
                try { FarVegImpostors.Tick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] far impostors threw: {ex.Message}"); }
                try { Paint.DistantPaintCache.PumpLodJobs(); }   // 0.2.1319: spread distant LOD paint compose (no-op with no job)
                catch (System.Exception ex) { FiresLogger.LogWarning($"[DistantPaint] LOD compose pump threw: {ex.Message}"); }
                SdMark(2);
                try { VoxelCaveClutter.Tick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] cave clutter threw: {ex.Message}"); }
                SdMark(3);
                try { VoxelUnderwaterClutter.Tick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] underwater clutter threw: {ex.Message}"); }
                SdMark(4);

                // A terrain edit doesn't wait for the 0.25 s tick: its zone is rebuilt this frame (hoe, pickaxe and
                // Infinity Hammer edits took seconds to show).
                if (s_terraformKick)
                {
                    s_terraformKick = false;
                    try { DrainTerraform(true); }
                    catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] terraform drain threw: {ex}"); }
                }
                SdMark(5);

                // 0.2.1319: the next slice of a spread zone scan (no-op when none is running or the tick below already ran one)
                try { ContinueScan(); }
                catch (System.Exception ex) { s_scanActive = false; FiresLogger.LogWarning($"[FiresVoxel] zone scan slice threw: {ex}"); }

                if (Time.time < _nextTick) return;
                _nextTick = Time.time + 0.25f;
                try { Tick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] streamer tick threw: {ex}"); }
                SdMark(6);
            }

            private void OnDestroy()
            {
                VoxelRenderGroup.Disarm();
                VerdantsAscent.Water.Tools.WaterShapingTools.ForgetSession();   // 0.2.1354 (review: statics surviving logout); also from Game.Logout (CustomWaterPatches.OnLogout)
                s_zones.Clear();
                s_brushedZones.Clear();
                s_hiddenEdits.Clear();   // 0.2.1334
                s_hideGaveUp.Clear();
                // RAM audit: these are per-session zone sets ("once per session per zone"); they used to outlive the
                // world, and a second world with the same zone coords skipped their water.
                s_waterDone.Clear();
                s_swapAt.Clear();
                s_offMapLogged.Clear();
                FatCracks.Forget();
                FiresLocationPads.Clear();
                HeightmapBuildCache.Clear();
                VoxelCaveClutter.Clear();
                VoxelUnderwaterClutter.Clear();
                s_driverGO = null;
            }
        }

        // ── Main tick ────────────────────────────────────────────────────────
        private const float TickPhaseBudgetMs = 4f;   // per phase of Tick (water builds, unwraps)

        private static void Tick()
        {
            // 0.2.1319: any early return below cancels a running spread scan (it is resumed only when this tick reaches the scan again)
            bool scanWas = s_scanActive; s_scanActive = false;
            var player = Player.m_localPlayer;
            TickEditHides();   // 0.2.1334

            // Mode off → unwind every wrapped zone.
            if (!ModeActive)
            {
                if (s_zones.Count > 0) UnwrapAll();
                return;
            }

            VoxelCaveChests.ServerTick();
            VoxelCaveSpawners.ServerTick();

            bool voxelOnly = FiresVoxelConfig.WorldKind == VoxelWorldMode.VoxelOnly;
            bool dedicated = IsDedicatedServer;

            // VoxelOnly is only meaningful on a Fires world (the A1 patches
            // gate on the generator too) — on anything else, stand down.
            if (voxelOnly && !FiresWorldGenBase.Active)
            {
                if (s_zones.Count > 0) UnwrapAll();
                return;
            }

            Vector3 preSpawnCenter = Vector3.zero;
            if (dedicated)
            {
                // The dedi streams COLLIDERS around its peers. Zone
                // discovery below is heightmap-driven, and vanilla only keeps
                // zones alive near peer refpositions, so WrapAllLoaded IS the
                // per-peer coverage. No peers → free everything.
                if (ZNet.instance == null || ZNet.instance.GetPeers().Count == 0)
                {
                    if (s_zones.Count > 0) UnwrapAll();
                    return;
                }
            }
            else
            {
                // In VoxelOnly there is no vanilla ground, so the spawn
                // area must wrap BEFORE the player exists — center on the
                // network reference position (vanilla points it at the pending
                // spawn during the respawn wait). Never unwind while waiting.
                // A spawn-wait breakdown showed ~30 of the ~40 s spawn gate passed before the player object existed,
                // with this tick returning here on every non-VoxelOnly world, so no hydrology, no wraps, no cache until then. So
                // ANY client voxel world wraps ahead of the spawn while the spawn gate is holding a point.
                if (player == null && !voxelOnly && Patch_ZNetScene_IsAreaReady_VoxelOnly.PendingSpawnArea(10f) == Vector3.zero)
                {
                    if (s_zones.Count > 0) UnwrapAll();
                    return;
                }
                if (player == null)
                {
                    // Ground truth first: the exact point the spawn gate is
                    // holding on. Refposition is only the fallback — it is
                    // set by vanilla's spawn flow, not by us, and trusting it
                    // alone let the wrap center drift from the gate's area
                    // (45s FAIL-OPEN on every login, zones=0 the whole wait).
                    preSpawnCenter = Patch_ZNetScene_IsAreaReady_VoxelOnly.PendingSpawnArea(10f);
                    if (preSpawnCenter == Vector3.zero && ZNet.instance != null)
                        preSpawnCenter = ZNet.instance.GetReferencePosition();
                    if (preSpawnCenter == Vector3.zero) return;   // not waiting on a spawn yet
                }
            }

            if (WorldGenerator.instance != null)
                VoxelCaveNoise.SetSeed(WorldGenerator.instance.GetSeed());

            // Hydrology must exist before any zone wraps so river beds carve in from the very first chunk (a 2-4 s
            // worker build at login). A dedicated server needs it only where it holds voxel data on a vanilla-generated
            // world: Fires worlds carry their rivers in the base, and water placement is client-side.
            bool serverCarvesRivers = dedicated && ServerHoldsVoxelData
                && FiresVoxelConfig.CarvesRiverBeds && !FiresWorldGenBase.Active;
            if (serverCarvesRivers
                || (!dedicated && (FiresVoxelConfig.CarvesRiverBeds || FiresVoxelConfig.PlacesHydrologyWater)))
            {
                VoxelHydrology.EnsureBuilding();
                VoxelHydrology.FlushReport();
                if (!VoxelHydrology.Ready) return;
            }

            // Clear what the carve drowned, whenever it streams in (client only
            // — the dedi has no water field of its own).
            if (!dedicated)
            {
                try { DrownedVegTick(); }
                catch (System.Exception ex)
                { FiresLogger.LogWarning($"[FiresVoxel] drowned-veg sweep threw: {ex.Message}"); }
            }

            // Drain due water placements (2s after a zone's swap, so the
            // zone's own pre-existing pours have loaded for the idempotency
            // check). Client-side world furniture — never on the dedi.
            // At most TickPhaseBudgetMs of water builds per tick (the rest stay queued for the next tick,
            // 0.25 s later; at least one per tick), not every due zone in one frame.
            var swWaterPhase = System.Diagnostics.Stopwatch.StartNew();
            int waterBuilt = 0;
            for (int i = s_waterQueue.Count - 1; i >= 0; i--)
            {
                if (waterBuilt > 0 && swWaterPhase.Elapsed.TotalMilliseconds >= TickPhaseBudgetMs) break;
                if (Time.time < s_waterQueue[i].due) continue;
                waterBuilt++;
                var zq = s_waterQueue[i].zone;
                s_waterQueue.RemoveAt(i);
                if (dedicated) continue;
                var swWater = System.Diagnostics.Stopwatch.StartNew();
                try { PlaceWaterForZone(zq); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] water placement threw for ({zq.x},{zq.y}): {ex.Message}"); }
                if (swWater.Elapsed.TotalMilliseconds >= 25.0)
                    FiresLogger.LogInfo($"[FiresVoxel] zone ({zq.x},{zq.y}) water build took {swWater.Elapsed.TotalMilliseconds:0} ms.");
            }

            Vector3 pp = player != null ? player.transform.position : preSpawnCenter;
            float radius = FiresVoxelConfig.WrapRadius.Value;
            // wrapAll (the whole-active-area envelope) only applies in FullVoxel.
            // PlacedOnly/ZoneReplace wrap a SPARSE set of zones, so they stay
            // radius-bounded regardless of the dedicated/WrapAllLoaded levers.
            bool wrapAll = FiresVoxelConfig.WrapsAllTerrain
                && (dedicated || (FiresVoxelConfig.WrapAllLoaded != null && FiresVoxelConfig.WrapAllLoaded.Value));
            // Active centers the footprint is bounded around: the local player on
            // a client; every connected peer's ref position on a dedicated server.
            s_centers.Clear();
            if (player != null) s_centers.Add(player.transform.position);
            else if (dedicated && ZNet.instance != null)
            {
                // Peer refpositions are the DEDI's centers. On a client they
                // are the server peer's refposition — nowhere near our pending
                // spawn — and preferring them over pp is exactly how the
                // pre-spawn wrap centered on the wrong spot.
                var peers = ZNet.instance.GetPeers();
                for (int pi = 0; pi < peers.Count; pi++)
                    if (peers[pi] != null && peers[pi].m_refPos != Vector3.zero && !OffMap(peers[pi].m_refPos)) s_centers.Add(peers[pi].m_refPos);
            }
            if (s_centers.Count == 0 && pp != Vector3.zero) s_centers.Add(pp);

            // RAM ceiling — ease the shared wrap radius down/up so the live
            // Full-chunk count tracks MaxLoadedChunks. Both bounds below read it.
            UpdateGovernorRadius();
            VoxelChunkCache.EnsureReady();
            VoxelChunkCache.Tick();

            // AUTO-HEAL — the zero-seams guarantee is EVENT-DRIVEN now (mirror-
            // before-mesh + neighbour pokes on apply). This drains the priority
            // queue those events feed and runs one paced background re-verify.
            AutoHealTick();
            if (FiresVoxelConfig.LegacyMeshWater != null && FiresVoxelConfig.LegacyMeshWater.Value)
                VoxelGenWaterMesh.Tick();
            else
                VoxelGenLiquid.Tick();
            // Wet-mask stamp sweep: any loaded heightmap not blended yet
            // (built before the water field was ready) gets its paint mask
            // stamped DIRECTLY, once — no Regenerate, no pokes, no rebuild
            // churn. Rebuilding zones re-blend via the ApplyModifiers
            // postfix; between the two, a painted zone can never border an
            // unpainted one.
            TickPendingWet();   // 0.2.1081: placed water re-paints + regrows once it really answers
            Patch_Heightmap_WetMask.Sweep();
            Paint.DistantPaintCache.VanillaLodSweep();
            FiresFarWater.Tick(pp);
            FiresSkyIslandFar.Tick(pp, ZoneSwapped);   // 0.2.1070
            if (!dedicated) GhostBurst(pp);
            else if (s_centers.Count > 0) GhostBurst(NextBurstCenter());

            // Unwrap: dead heightmaps (zone unloaded); also zones beyond the
            // governor radius + hysteresis (the far ring the governor shed).
            s_scratchDrop.Clear();
            // +96 m hysteresis (not +48), so drops stop landing on the same frames as the wrap-ins ahead.
            float dropBound = GovernorBound(wrapAll ? WRAP_HARD_CAP : radius) + 96f;
            foreach (var kv in s_zones)
            {
                var w = kv.Value;
                if (w.Hm == null && !w.DataOnly) { s_scratchDrop.Add(kv.Key); continue; }
                // Drop zones outside the bound of EVERY active center — frees
                // their chunks/views/water. In wrapAll mode this is the hard cap
                // (far ZDO-only ring); in radius mode the player's WrapRadius.
                if (s_centers.Count > 0 && !NearAnyCenter(WrapCenter(w), dropBound))
                    s_scratchDrop.Add(kv.Key);
            }
            // Zone-boundary frames of ~530 ms: at most ONE out-of-range unwrap per tick, by COUNT. An unwrap's
            // real cost (vanilla collider back into PhysX, the heightmap rebuild, ~16 view destroys) is native or end-of-frame, so a
            // stopwatch cap never saw it and a whole column of zones still dropped in one frame. Zones whose heightmap is already gone
            // (vanilla unloaded them) show and rebuild nothing, so those still go at once.
            {
                bool farDone = false;
                foreach (var z in s_scratchDrop)
                {
                    bool dead = s_zones.TryGetValue(z, out var dw) && dw.Hm == null;
                    if (!dead) { if (farDone) continue; farDone = true; }
                    Unwrap(z);
                }
            }

            DrainTerraform(false);

            // Paint-only ops need no remesh. The drain runs ONCE, from VoxelPipelineDriver; a second call here
            // (same 0.25 s cadence, often the same frame) doubled the per-frame paint cost.

            // Watchdog - another mod re-enabled a swapped zone's vanilla terrain
            // (vanilla's own writer, the render group, is suspended while the
            // zone is swapped): re-hide and SAY SO, so the log names the fight.
            foreach (var kv in s_zones)
            {
                var w = kv.Value;
                if (!w.Swapped || w.Hm == null) continue;
                KeepGroundUnderSwappedZone(w);
                var rend = w.Hm.GetComponent<MeshRenderer>();
                var col = s_hmCollider(w.Hm);
                bool rendBack = rend != null && rend.enabled;
                bool colBack = col != null && col.enabled && !w.VanillaColliderBack;
                if (rendBack || colBack)
                {
                    bool sameHm = w.HiddenHmId != 0 && w.Hm.GetInstanceID() == w.HiddenHmId;
                    bool sameRenderer = rend != null && w.HiddenRendererId != 0 && rend.GetInstanceID() == w.HiddenRendererId;

                    HideVanilla(w.Hm);
                    if (w.VanillaColliderBack && col != null) { FatNativeTrace.Step("vanilla collider back", 0, "zone " + w.Zone.x + "," + w.Zone.y); col.enabled = true; }
                    w.HiddenHmId = w.Hm.GetInstanceID();
                    if (rend != null) w.HiddenRendererId = rend.GetInstanceID();

                    string verdict = sameRenderer
                        ? "SAME renderer switched back on — something is genuinely fighting the swap"
                        : sameHm
                            ? "same Heightmap but a NEW MeshRenderer — Heightmap.Awake re-added one, so the old was destroyed"
                            : "different Heightmap — the zone was rebuilt, so this is churn rather than a fight";

                    FiresLogger.LogWarning($"[FiresVoxel] zone ({kv.Key.x},{kv.Key.y}) vanilla terrain RE-ENABLED externally " +
                        $"(renderer={rendBack}, collider={colBack}) — re-hidden. {verdict}.");
                }
            }

            // Memory line (client RAM audit): every 5 min at Debug, what FAT holds.
            if (Time.time >= s_nextMemory)
            {
                s_nextMemory = Time.time + 300f;
                try
                {
                    VoxelWorld.MemoryReport(out long chunkB, out int views, out int meshes, out long meshB, out int masks, out long maskB);
                    long crackB = FatCracks.HeldBytes(out int crackCols);
                    VerdantsAscent.Terrain.Paint.DistantPaintCache.MemoryReport(out int dpZones, out long dpB, out int dpInj, out long dpTexB);
                    FiresLogger.LogDebug($"[FiresVoxel] memory: chunks={VoxelWorld.ChunkCount} chunkArrays={chunkB >> 20}MB " +
                        $"views={views} meshes={meshes} meshCpu~{meshB >> 20}MB paintMasks={masks} {maskB >> 20}MB " +
                        $"cracks={crackCols} {crackB >> 20}MB distantPaint={dpZones} zones {dpB >> 20}MB injectedTex={dpInj} {dpTexB >> 20}MB " +
                        $"zones={s_zones.Count} waterDone={s_waterDone.Count} swapAt={s_swapAt.Count} managedHeap={System.GC.GetTotalMemory(false) >> 20}MB");
                }
                catch (System.Exception e) { FiresLogger.LogDebug($"[FiresVoxel] memory line skipped: {e.Message}"); }
            }

            // Periodic streamer state summary — the ground truth for wrap
            // diagnosis. 30s (was 10s; the wrap-deadlock era is over) and
            // LogTimings-gated.
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value
                && Time.time >= s_nextSummary)
            {
                s_nextSummary = Time.time + 30f;
                int swapped = 0;
                foreach (var kv in s_zones) if (kv.Value.Swapped) swapped++;
                FiresLogger.LogInfo($"[FiresVoxel] streamer: zones={s_zones.Count} swapped={swapped} " +
                    $"pendingSwap={s_zones.Count - swapped} dirty={s_dirty.Count} chunks={VoxelWorld.ChunkCount} " +
                    $"full={VoxelWorld.BudgetedChunkCount}/{FiresVoxelConfig.MaxLoadedChunks.Value} centers={s_centers.Count}" +
                    (s_centers.Count > 0 ? $" first=({s_centers[0].x:0},{s_centers[0].z:0})" : ""));
            }

            // Wrap candidates + completion checks. ≤1 NEW zone per tick keeps
            // the worker fed without bursts.
            // A VoxelWrap dedi used to hold voxel DATA only: its physics stood on vanilla ground. Now it meshes colliders
            // like VoxelOnly unless 'Server builds voxel collision' is off.
            bool dataOnlyServer = dedicated && !FiresVoxelConfig.ServerCollides;
            if (FiresLogger.DebugEnabled && !dedicated) LogDrawnReach(GovernorBound(wrapAll ? WRAP_HARD_CAP : radius));   // 0.2.1350 (proof, every 30 s)
            // 0.2.1319 (PLAN_fat_frame_spread item 5: streamer tick 10.4 ms in one frame): key "Voxel streamer zone scan spread over
            // frames" walks a snapshot of the heightmaps at about ScanSliceMs a frame (ContinueScan from the driver) instead of all in
            // this frame; each heightmap's wrap / swap / unwrap is still done in the frame it is decided, and the tail (server data,
            // reports) runs when the pass completes. A pass still running when the next 0.25 s tick comes is continued, never restarted.
            if (FiresVoxelConfig.StreamerScanSpread != null && FiresVoxelConfig.StreamerScanSpread.Value)
            {
                if (!scanWas)
                {
                    s_scanList.Clear(); s_scanList.AddRange(Heightmap.GetAllHeightmaps());
                    s_scanIdx = 0; s_scanStarted = false;
                    s_wrapBestHm = null; s_wrapBestD = float.MaxValue; s_wrapFirstD = -1f;   // 0.2.1344
                    if (!s_scanProof) { s_scanProof = true; FiresLogger.LogInfo("[FiresVoxel] streamer zone scan spreads over frames (~1 ms a frame); wrap / swap / unwrap stay in the frame they are decided (0.2.1319)"); }
                }
                s_scanWrapAll = wrapAll; s_scanRadius = radius; s_scanDedicated = dedicated; s_scanDataOnly = dataOnlyServer;   // this tick's values
                s_scanActive = true;
                ContinueScan();
                return;
            }
            s_scanActive = false;
            bool startedOne = false;
            s_wrapBestHm = null; s_wrapBestD = float.MaxValue; s_wrapFirstD = -1f;   // 0.2.1344
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || s_isDistantLod(hm)) continue;
                Vector3 c = hm.transform.position;
                float wrapBound = GovernorBound(wrapAll ? WRAP_HARD_CAP : radius);
                if (s_centers.Count > 0 && !NearAnyCenter(c, wrapBound)) continue;
                if (dedicated && OffMap(c)) { SkipOffMap(ZoneSystem.GetZone(c)); continue; }

                var zone = ZoneSystem.GetZone(c);
                if (s_zones.TryGetValue(zone, out var wrap))
                {
                    if (wrap.DataOnly && !dataOnlyServer) { Unwrap(zone); continue; }
                    if (!wrap.Swapped) TrySwap(wrap);
                    continue;
                }
                if (startedOne || dataOnlyServer) continue;
                // MODE GATE: FullVoxel wraps every near zone; PlacedOnly/
                // ZoneReplace only wrap dug or sky-island zones so the rest of
                // the world stays vanilla.
                if (!ShouldWrapZone(zone, c)) continue;
                if (VoxelNearOrder.On) { NoteWrapCandidate(zone, hm, c); continue; }   // 0.2.1344: the nearest eligible zone, started after the pass
                startedOne = StartWrap(zone, hm);
            }
            if (StartNearestWrap()) startedOne = true;   // 0.2.1344
            if (dedicated && ServerHoldsVoxelData)
                TickServerData(GovernorBound(wrapAll ? WRAP_HARD_CAP : radius), dataOnlyServer, startedOne);
            if (dedicated && !dataOnlyServer) ReportServerColliders();
            ReportBlocked();
        }

        // 0.2.1344 (Fire: "fill from closest out"): one wrap per pass still, but the eligible zone nearest a centre rather than the first in the
        // heightmap list. ShouldWrapZone only runs for a zone closer than the best so far, so a pass costs no more than the old scan's worst case.
        private static Heightmap s_wrapBestHm; private static Vector2s s_wrapBestZone; private static float s_wrapBestD = float.MaxValue, s_wrapFirstD = -1f;
        private static int s_wrapPicks; private static double s_wrapSumD, s_wrapSumFirst;
        private static float CenterDistSq(Vector3 c)
        {
            if (s_centers.Count == 0) return 0f;
            float best = float.MaxValue;
            for (int i = 0; i < s_centers.Count; i++) { float dx = s_centers[i].x - c.x, dz = s_centers[i].z - c.z, d = dx * dx + dz * dz; if (d < best) best = d; }
            return best;
        }
        private static void NoteWrapCandidate(Vector2s zone, Heightmap hm, Vector3 c)
        {
            float d = CenterDistSq(c);
            if (d >= s_wrapBestD || !ShouldWrapZone(zone, c)) return;
            if (s_wrapFirstD < 0f) s_wrapFirstD = d;
            s_wrapBestD = d; s_wrapBestHm = hm; s_wrapBestZone = zone;
        }
        private static bool StartNearestWrap()
        {
            var hm = s_wrapBestHm;
            s_wrapBestHm = null;
            if (hm == null || s_zones.ContainsKey(s_wrapBestZone)) return false;
            s_wrapPicks++; s_wrapSumD += Mathf.Sqrt(s_wrapBestD); s_wrapSumFirst += Mathf.Sqrt(s_wrapFirstD);
            return StartWrap(s_wrapBestZone, hm);
        }
        internal static string TakeWrapPickStats()
        {
            if (s_wrapPicks == 0) return "";
            string s = $"wrap starts {s_wrapPicks}, zone distance avg {s_wrapSumD / s_wrapPicks:0} m (the list's first eligible: {s_wrapSumFirst / s_wrapPicks:0} m) ";
            s_wrapPicks = 0; s_wrapSumD = s_wrapSumFirst = 0;
            return s;
        }
        internal static void CopyCenters(List<Vector3> into) => into.AddRange(s_centers);
        // A wrapped zone still waiting for its swap (a zone not wrapped, data-only or already swapped holds nothing back).
        internal static bool ZoneMidSwap(Vector2s zone) => s_zones.TryGetValue(zone, out var w) && !w.Swapped && !w.DataOnly;

        // ---- 0.2.1319 spread zone scan (same per-heightmap body as the loop above)
        private const double ScanSliceMs = 1.0; private const int ScanMinPerFrame = 8;
        private static readonly List<Heightmap> s_scanList = new List<Heightmap>();
        private static int s_scanIdx, s_scanFrame = -1; private static bool s_scanActive, s_scanStarted, s_scanWrapAll, s_scanDedicated, s_scanDataOnly, s_scanProof; private static float s_scanRadius;
        private static readonly FatSpreadStat s_scanSpread = new FatSpreadStat("voxel streamer zone scan");

        // From the driver every frame (and from Tick): one slice of the running pass; at most one slice a frame.
        private static void ContinueScan()
        {
            if (!s_scanActive || s_scanFrame == Time.frameCount) return;
            if (!ModeActive) { s_scanActive = false; s_scanList.Clear(); return; }
            s_scanFrame = Time.frameCount;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long limit = t0 + (long)(ScanSliceMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            int done = 0;
            while (s_scanIdx < s_scanList.Count)
            {
                if (done >= ScanMinPerFrame && System.Diagnostics.Stopwatch.GetTimestamp() >= limit) break;
                var hm = s_scanList[s_scanIdx++];
                done++;
                if (hm == null || s_isDistantLod(hm)) continue;
                Vector3 c = hm.transform.position;
                float wrapBound = GovernorBound(s_scanWrapAll ? WRAP_HARD_CAP : s_scanRadius);
                if (s_centers.Count > 0 && !NearAnyCenter(c, wrapBound)) continue;
                if (s_scanDedicated && OffMap(c)) { SkipOffMap(ZoneSystem.GetZone(c)); continue; }

                var zone = ZoneSystem.GetZone(c);
                if (s_zones.TryGetValue(zone, out var wrap))
                {
                    if (wrap.DataOnly && !s_scanDataOnly) { Unwrap(zone); continue; }
                    if (!wrap.Swapped) TrySwap(wrap);
                    continue;
                }
                if (s_scanStarted || s_scanDataOnly) continue;
                if (!ShouldWrapZone(zone, c)) continue;
                if (VoxelNearOrder.On) { NoteWrapCandidate(zone, hm, c); continue; }   // 0.2.1344
                s_scanStarted = StartWrap(zone, hm);
            }
            if (s_scanIdx >= s_scanList.Count)
            {
                if (StartNearestWrap()) s_scanStarted = true;   // 0.2.1344
                s_scanActive = false; s_scanList.Clear();
                if (s_scanDedicated && ServerHoldsVoxelData)
                    TickServerData(GovernorBound(s_scanWrapAll ? WRAP_HARD_CAP : s_scanRadius), s_scanDataOnly, s_scanStarted);
                if (s_scanDedicated && !s_scanDataOnly) ReportServerColliders();
                ReportBlocked();
            }
            s_scanSpread.Note((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        // Server voxel data
        private const float DataReportSeconds = 30f;
        private const float StuckWrapSeconds = 8f;
        private const int ServerDataZonesRequestedPerTick = 2;

        private static readonly List<Vector2s> s_dataZones = new List<Vector2s>();
        private static int s_dataStarted, s_dataReady, s_dataEditsImported;
        private static int s_editRefreshCursor;
        private static int s_lastRegistryVersion = int.MinValue;
        private static float s_nextDataReport;

        private static Vector3 WrapCenter(ZoneWrap wrap)
            => wrap.Hm != null ? wrap.Hm.transform.position : ZoneSystem.GetZonePos(wrap.Zone);

        // Beyond the world's edge (vanilla's 10 km world plus its ocean rim). A joining peer's pre-spawn reference position
        // sits around (1e6, 1e6), and the dedi meshed colliders for zones out there on every join ("server colliders for
        // zone (15625,15625): 12 chunk(s)"). Neither a centre nor a wrap; one line per skipped zone.
        // The world's own edge (FatWorldSize; 10500 on a vanilla-size world). A fixed 10500 skipped every real zone
        // past 10.5 km on an Expand World Size world.
        private static float WorldEdgeMetres => FatWorldSize.EdgeRadius;
        private static readonly HashSet<Vector2s> s_offMapLogged = new HashSet<Vector2s>();

        private static bool OffMap(Vector3 p) => Mathf.Abs(p.x) > WorldEdgeMetres || Mathf.Abs(p.z) > WorldEdgeMetres;

        private static void SkipOffMap(Vector2s zone)
        {
            if (s_offMapLogged.Add(zone))
                FiresLogger.LogInfo($"[FiresVoxel] server skips zone ({zone.x},{zone.y}): beyond the world's edge ({WorldEdgeMetres:0} m), "
                                    + "a joining peer's placeholder position; nothing wrapped there.");
        }

        // Every DataReportSeconds on a dedi that builds colliders: what it holds and what it cost, for the on/off A/B.
        private static int s_serverSwaps, s_serverSwapChunks;
        private static float s_nextColliderReport;

        private static void ReportServerColliders()
        {
            if (Time.time < s_nextColliderReport) return;
            s_nextColliderReport = Time.time + DataReportSeconds;
            int zones = 0, swapped = 0, chunks = 0;
            foreach (var kv in s_zones)
            {
                if (kv.Value.DataOnly) continue;
                zones++;
                if (kv.Value.Swapped) swapped++;
                chunks += kv.Value.Keys.Count;
            }
            if (zones == 0 && s_serverSwaps == 0) return;
            FiresLogger.LogDebug($"[FiresVoxel] server colliders: {zones} zone(s) wrapped around {s_centers.Count} peer(s), {swapped} swapped, "
                                + $"{chunks} chunk(s); last {DataReportSeconds:0}s: {s_serverSwaps} zone(s) swapped, {s_serverSwapChunks} chunk(s); "
                                + $"managed heap {System.GC.GetTotalMemory(false) / 1048576.0:0} MB.");
            s_serverSwaps = 0;
            s_serverSwapChunks = 0;
        }

        private static void TickServerData(float bound, bool dataOnlyServer, bool startedOne)
        {
            NoticeZoneOpenings();
            s_dataZones.Clear();
            foreach (var kv in s_zones) if (kv.Value.DataOnly) s_dataZones.Add(kv.Key);
            foreach (var zone in s_dataZones)
            {
                var wrap = s_zones[zone];
                if (!wrap.Swapped || wrap.AwaitingRegen) TrySwapDataOnly(wrap);
            }
            RefreshNextZoneEdits();
            if (!startedOne) StartNextServerDataWrap(bound, dataOnlyServer);
            ReportServerData();
        }

        // Zone mode: an opened zone and its neighbours read new ground, and nothing pokes a heightmap on a server
        // that holds none, so the data-only copies rebuild from the open state they were built under.
        private static void NoticeZoneOpenings()
        {
            int version = Zones.ZoneModeRegistry.Version;
            if (version == s_lastRegistryVersion) return;
            s_lastRegistryVersion = version;
            foreach (var kv in s_zones)
                if (kv.Value.DataOnly && OpenMaskOf(kv.Key) != kv.Value.OpenMask) s_dirty.Add(kv.Key);
        }

        private static void StartNextServerDataWrap(float bound, bool dataOnlyServer)
        {
            int rings = Mathf.CeilToInt(bound / 64f);
            int requested = 0;
            for (int ring = 0; ring <= rings; ring++)
            {
                foreach (var center in s_centers)
                {
                    if (!dataOnlyServer && Heightmap.FindHeightmap(center) != null) continue;
                    var middle = ZoneSystem.GetZone(center);
                    for (int dy = -ring; dy <= ring; dy++)
                    {
                        for (int dx = -ring; dx <= ring; dx++)
                        {
                            if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != ring) continue;
                            var zone = new Vector2s(middle.x + dx, middle.y + dy);
                            if (s_zones.ContainsKey(zone) || !NearAnyCenter(ZoneSystem.GetZonePos(zone), bound)) continue;
                            if (!WantsServerData(zone)) continue;
                            if (!dataOnlyServer && Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zone)) != null) continue;
                            if (!HeightmapBuildCache.EnsureZoneAndNeighbours(zone))
                            {
                                if (++requested >= ServerDataZonesRequestedPerTick) return;
                                continue;
                            }
                            StartDataWrap(zone);
                            return;
                        }
                    }
                }
            }
        }

        private static void StartDataWrap(Vector2s zone)
        {
            var wrap = new ZoneWrap { Zone = zone, DataOnly = true, StartedAt = Time.time };
            s_zones[zone] = wrap;
            BuildZoneChunks(wrap);
            s_dataStarted++;
        }

        // Ready once every chunk has generated. Player edits land first, then the zone is dressed (open zones only).
        private static void TrySwapDataOnly(ZoneWrap wrap)
        {
            int pending = 0;
            foreach (var key in wrap.Keys)
            {
                if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk.Kind != ChunkKind.Full || !chunk.PendingGenerate) continue;
                pending++;
                if (!chunk.Queued) VoxelMeshPipeline.EnqueueGenerate(chunk);
            }
            if (pending > 0)
            {
                float since = wrap.AwaitingRegen ? wrap.LastRewrapAt : wrap.StartedAt;
                if (Time.time - since > StuckWrapSeconds && Time.time >= wrap.NextDiagAt)
                {
                    wrap.NextDiagAt = Time.time + StuckWrapSeconds;
                    FiresLogger.LogWarning($"[FiresVoxel] server voxel data for zone ({wrap.Zone.x},{wrap.Zone.y}) still generating after "
                        + $"{Time.time - since:F0}s: {pending} of {wrap.Keys.Count} chunk(s) pending.");
                }
                return;
            }
            s_dataEditsImported += VoxelChunkAnchor.ImportZoneEditsFromZdos(wrap.Zone, wrap.Keys, wrap.ImportedEdits);
            if (!wrap.Swapped)
            {
                wrap.Swapped = true;
                s_swapAt[wrap.Zone] = Time.time;
                s_dataReady++;
            }
            wrap.AwaitingRegen = false;
            if (!Zones.ZoneModeRegistry.IsOpen(wrap.Zone)) return;
            try { VoxelCaveContent.PlaceForZone(wrap.Zone); }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] cave content threw for ({wrap.Zone.x},{wrap.Zone.y}): {ex}"); }
        }

        private static void RefreshNextZoneEdits()
        {
            if (s_dataZones.Count == 0) return;
            s_editRefreshCursor = (s_editRefreshCursor + 1) % s_dataZones.Count;
            if (!s_zones.TryGetValue(s_dataZones[s_editRefreshCursor], out var wrap) || !wrap.Swapped || wrap.AwaitingRegen) return;
            s_dataEditsImported += VoxelChunkAnchor.ImportZoneEditsFromZdos(wrap.Zone, wrap.Keys, wrap.ImportedEdits);
        }

        private static void ReportServerData()
        {
            if (Time.time < s_nextDataReport) return;
            s_nextDataReport = Time.time + DataReportSeconds;
            if (s_dataStarted == 0 && s_dataReady == 0 && s_dataEditsImported == 0) return;
            int held = 0, ready = 0;
            foreach (var kv in s_zones)
            {
                if (!kv.Value.DataOnly) continue;
                held++;
                if (kv.Value.Swapped) ready++;
            }
            string line = $"[FiresVoxel] server voxel data: {held} zone(s) held around {s_centers.Count} player(s), {ready} ready. "
                + $"Last {DataReportSeconds:0}s: {s_dataStarted} built, {s_dataReady} finished, {s_dataEditsImported} player-edit chunk(s) imported. "
                + $"Chunks {VoxelWorld.BudgetedChunkCount}/{FiresVoxelConfig.MaxLoadedChunks.Value}, ground builds cached {HeightmapBuildCache.CachedZoneCount}.";
            // The first report proves the server holds voxel data; the rest are verbose.
            if (s_dataReported) FiresLogger.LogVerbose(line);
            else { s_dataReported = true; FiresLogger.LogInfo(line); }
            s_dataStarted = s_dataReady = s_dataEditsImported = 0;
        }

        // Ground + biome per column (34×34, apron included), from heightmap
        // DATA (never physics; +ε for the zone-border fix).
        private static void GatherColumns(int kx, int kz, out float[] ground, out Color32[] biome,
            out float minG, out float maxG, ref bool usedFallback)
        {
            var wg = WorldGenerator.instance;
            float ox = kx * 32f, oz = kz * 32f;
            ground = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
            biome = new Color32[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
            minG = float.MaxValue; maxG = float.MinValue;
            for (int z = -1; z <= VoxelChunk.CELLS; z++)
            {
                for (int x = -1; x <= VoxelChunk.CELLS; x++)
                {
                    float wx = ox + x, wz = oz + z;
                    float g;
                    // ONE SOURCE. Heightmap DATA is the ground truth — it IS
                    // our generator (the height prefixes feed vanilla's grids)
                    // PLUS every edit (hoe/pickaxe/IH/WEC comp deltas). The
                    // old code flipped per-sample between heightmap data and
                    // the raw analytic generator depending on whether a
                    // TerrainComp existed — a time-varying source switch
                    // inside the exact function that decides border data
                    // (hash churn on every first terraform, interpolated-vs-
                    // analytic shelves at borders). The pure generator now
                    // answers ONLY when neither the heightmap nor its build data
                    // is loaded, and that border stays flagged provisional until healed.
                    // Location pads (a placed location levels the ground it stands on) ride along in both paths.
                    bool colFallback = false;   // 0.2.1321 skirt probe: per column
                    if (!TryGroundHeight(wx, wz, out g))
                    {
                        g = GeneratedGround(wx, wz);
                        usedFallback = true;
                        colFallback = true;
                        if (FiresLocationPads.TrySample(wx, wz, g, out float padY)) g = padY;
                    }
                    int col = (x + 1) + VoxelChunk.SAMPLES * (z + 1);
                    ground[col] = g;
                    s_ringFallback[col] = colFallback;   // 0.2.1321
                    // ONE biome authority: paint overlay > env box > worldgen
                    // (audit §2-H: painted biome COLOR never reached the voxel
                    // skin because this read raw worldgen). Paint ops trigger
                    // Regenerate → zone dirty → rewrap, and biome is in the
                    // change-gate hash, so repainted tint lands automatically.
                    // Corner-blended (vanilla parity) — one-hot per vertex
                    // drew hard pixel edges at every biome boundary.
                    biome[col] = wg != null
                        ? BiomeCornerBlend.Sample(wx, wz)
                        : new Color32(0, 0, 0, 0);
                    if (g < minG) minG = g;
                    if (g > maxG) maxG = g;
                }
            }
        }

        // ── 0.2.1321 zone-entry skirt PROBE ONLY (dark walls ~0.5-1 m above the ice at zone entry until the heal rebuild; suspect:
        // the 32 m chunk's border ring gathered before the pond bed carve / the neighbour's ground is ready). No behaviour change.
        // Per gathered chunk column in a zone with water: the border columns (x or z = -1 or CELLS) whose chosen ground stands above
        // the local water level (generated, else placed), with the fallback flag and whether the bed carve is missing there
        // (water says wet, yet the ground is not under level - WaterMinChannelDepth). Rate-limited; the rewrap / heal line should read 0.
        private static readonly bool[] s_ringFallback = new bool[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
        private static float s_ringLogAt;
        private static bool s_ringProof;
        private static readonly HashSet<Vector2s> s_ringSwapSaid = new HashSet<Vector2s>();

        internal static bool ProbeWaterLevel(float wx, float wz, float ground, out float level, out string src)
        {
            src = "none"; level = float.NaN;
            if (FiresWorldGenBase.TryGetGenWaterSmoothReal(wx, wz, out float l, out float depth) && depth > 0.05f) { level = l; src = "generated"; return true; }
            if (VerdantsAscent.Water.Core.PlacedWaterField.TrySurface(wx, wz, ground - 4f, ground + 40f, VerdantsAscent.Water.Core.PlacedWaterField.Consumer.Sheet, out float s, out _))
            { level = s; src = "placed"; return true; }
            return false;
        }

        private static void NoteBorderRing(Vector2s zone, int kx, int kz, float[] ground, string path)
        {
            if (ground == null || Time.realtimeSinceStartup < s_ringLogAt) return;
            float ox = kx * 32f, oz = kz * 32f;
            float carve = FiresVoxelConfig.WaterMinChannelDepth != null ? FiresVoxelConfig.WaterMinChannelDepth.Value : 0f;
            int wet = 0, above = 0, fallback = 0, carveMissing = 0; float worst = 0f; string worstAt = "";
            for (int z = -1; z <= VoxelChunk.CELLS; z++)
                for (int x = -1; x <= VoxelChunk.CELLS; x++)
                {
                    if (x != -1 && x != VoxelChunk.CELLS && z != -1 && z != VoxelChunk.CELLS) continue;
                    int col = (x + 1) + VoxelChunk.SAMPLES * (z + 1);
                    float g = ground[col];
                    if (!ProbeWaterLevel(ox + x, oz + z, g, out float level, out string src)) continue;
                    wet++;
                    if (g <= level) { if (src == "generated" && g > level - carve + 0.05f) carveMissing++; continue; }
                    above++;
                    if (s_ringFallback[col]) fallback++;
                    if (src == "generated") carveMissing++;
                    if (g - level > worst) { worst = g - level; worstAt = $" worst at ({ox + x:0},{oz + z:0}) ground {g:0.00} {src} level {level:0.00}{(s_ringFallback[col] ? " FALLBACK" : "")}"; }
                }
            if (wet == 0) return;   // zones with water only
            s_ringLogAt = Time.realtimeSinceStartup + 0.5f;
            if (!s_ringProof) { s_ringProof = true; FiresLogger.LogInfo("[FiresVoxel] border ring probe on (Debug): border columns above the water level per gathered chunk (0.2.1321)"); }
            FiresLogger.LogDebug($"[FiresVoxel] border ring zone ({zone.x},{zone.y}) chunk ({kx},{kz}) [{path}]: {above} column(s) above the water level by up to {worst:0.00} m (fallback {fallback}, carve missing {carveMissing}) of {wet} wet border column(s), bed carve {carve:0.00}{worstAt} (0.2.1321)");
        }

        // At the swap: how many of each chunk renderer's vertices stand more than 0.2 m above the local water / ice level. Once per zone.
        private static void NoteSwapRing(ZoneWrap wrap)
        {
            if (!FiresLogger.DebugEnabled || !s_ringSwapSaid.Add(wrap.Zone)) return;
            if (s_ringSwapSaid.Count > 8192) s_ringSwapSaid.Clear();
            foreach (var key in wrap.Keys)
            {
                if (!VoxelWorld.TryGetView(key, out var view) || view == null) continue;
                var mesh = view.GroundMesh;
                var r = view.GroundRenderer;
                if (mesh == null || r == null || !mesh.isReadable) continue;
                var verts = mesh.vertices;
                var t = view.transform;
                int high = 0, wetV = 0; float worst = 0f;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = t.TransformPoint(verts[i]);
                    if (!ProbeWaterLevel(w.x, w.z, w.y, out float level, out _)) continue;
                    wetV++;
                    if (w.y > level + 0.2f) { high++; if (w.y - level > worst) worst = w.y - level; }
                }
                if (wetV == 0) continue;
                FiresLogger.LogDebug($"[FiresVoxel] border ring at swap zone ({wrap.Zone.x},{wrap.Zone.y}) renderer '{r.name}' chunk {key}: {high} of {wetV} vertex(es) over water are > 0.2 m above the water / ice level (worst {worst:0.00} m) (0.2.1321)");
            }
        }

        // Background re-verify cursor: walks the wrapped-zone set once per full
        // cycle (list refilled only when the cursor exhausts, NOT every tick) —
        // the safety net that replaced the old full-world AllChunks copy+Sort.
        private static readonly List<Vector2s> s_verifyZones = new List<Vector2s>();
        private static int s_verifyCursor;

        // Event-driven heal: chunk keys queued at zone swap (the zone + its
        // neighbour ring) get their borders re-mirrored ahead of the background
        // re-verify, so a rewrap converges in ticks.
        private static readonly Queue<Vector3Int> s_priorityHeal = new Queue<Vector3Int>();
        private static readonly HashSet<Vector3Int> s_priorityHealSet = new HashSet<Vector3Int>();

        private static void QueuePriorityHeal(ZoneWrap wrap)
        {
            if (wrap.DataOnly) return;
            foreach (var key in wrap.Keys)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var k = new Vector3Int(key.x + dx, key.y, key.z + dz);
                        if (s_priorityHealSet.Add(k)) s_priorityHeal.Enqueue(k);
                    }
                }
            }
        }

        private const int HealPerTick = 4;

        // Convergence is EVENT-DRIVEN: EnqueueRemesh re-mirrors a chunk's ring
        // before every mesh, and each apply pokes any neighbour whose ring
        // diverged (VoxelMeshPipeline.PokeStale/PokeOrphanNeighbours) — a data
        // change propagates as a self-terminating wave. This tick only (a) drains
        // the priority queue those events + zone swaps/unwraps feed, and (b) runs
        // a slow background re-verify as a belt-and-braces net. The old per-tick
        // full-world AllChunks copy+Sort is GONE — it re-ranked thousands of
        // chunks four times a second to catch the rare straggler PokeStale now
        // handles at its source.
        private static void AutoHealTick()
        {
            int budget = HealPerTick;
            int scan = s_priorityHeal.Count;
            while (budget > 0 && scan-- > 0 && s_priorityHeal.Count > 0)
            {
                var key = s_priorityHeal.Dequeue();
                if (!VoxelWorld.TryGetChunk(key, out var pc) || pc == null)
                { s_priorityHealSet.Remove(key); continue; }
                // Uniform chunks have no border ring to heal and no mesh to
                // rebuild — drop them instead of firing a no-op EnqueueRemesh
                // (their AppliedVersion is stale forever after a downgrade).
                if (pc.Kind != ChunkKind.Full || HoldsDataOnly(key)) { s_priorityHealSet.Remove(key); continue; }
                if (pc.PendingGenerate) { s_priorityHeal.Enqueue(key); continue; }
                s_priorityHealSet.Remove(key);
                if (pc.MirrorBordersNow(true) > 0
                    || pc.Version != pc.AppliedVersion
                    || VoxelMeshPipeline.ComputeFrontierMask(pc) != pc.FrontierMask
                    || VoxelMeshPipeline.ComputeOrphanMask(pc) != pc.OrphanMask)
                    VoxelMeshPipeline.EnqueueRemesh(pc);
                budget--;
            }

            ReverifyNextZone();
        }

        // Feed ONE wrapped zone's ring into the priority queue per cycle-step,
        // and only when the queue has drained — so it never outruns the bounded
        // drain above and never scans/sorts the whole chunk set. Over a full
        // cycle it re-checks every wrapped zone; the per-chunk diff test inside
        // the drain no-ops on converged chunks, so a steady world costs nothing
        // but dictionary walks.
        private static void ReverifyNextZone()
        {
            if (s_priorityHeal.Count > 0 || s_zones.Count == 0) return;
            if (s_verifyCursor >= s_verifyZones.Count)
            {
                s_verifyZones.Clear();
                foreach (var k in s_zones.Keys) s_verifyZones.Add(k);
                s_verifyCursor = 0;
            }
            var zone = s_verifyZones[s_verifyCursor++];
            if (s_zones.TryGetValue(zone, out var wrap)) QueuePriorityHeal(wrap);
        }

        // FNV-1a over the generate inputs (ground bits, biome, caves flag,
        // river-carve readiness — rivers carve once Ready flips, so that first
        // regen must not be gated away — and the sky-island gate). It also
        // validates the chunk disk cache, so every generate input belongs here.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float F;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint U;
        }

        // A chunk in a skyland's band wears the skyland's biome, except in columns whose ground reaches up into the
        // chunk: those keep the ground's biome, so a skyland hanging low over high land never tints the land. The
        // returned hash covers every colour, alpha included, for the chunk's generation hash.
        private static Color32[] SkylandBiome(int kx, int kz, float chunkBottom, float[] ground, Color32[] groundBiome,
            out uint colorHash)
        {
            var colors = new Color32[groundBiome.Length];
            uint hash = 2166136261u;
            for (int z = -1; z <= VoxelChunk.CELLS; z++)
            {
                for (int x = -1; x <= VoxelChunk.CELLS; x++)
                {
                    int col = (x + 1) + VoxelChunk.SAMPLES * (z + 1);
                    var color = groundBiome[col];
                    if (ground[col] < chunkBottom && FiresSkyIslands.TrySkylandColorAt(kx * 32f + x, kz * 32f + z, out var skyland))
                        color = skyland;
                    colors[col] = color;
                    unchecked
                    {
                        hash = (hash ^ color.r) * 16777619u;
                        hash = (hash ^ color.g) * 16777619u;
                        hash = (hash ^ color.b) * 16777619u;
                        hash = (hash ^ color.a) * 16777619u;
                    }
                }
            }
            colorHash = hash;
            return colors;
        }

        private static uint HashColumns(float[] ground, Color32[] biome, bool caves, bool rivers)
        {
            unchecked
            {
                uint h = 2166136261u;
                var fb = default(FloatBits);
                for (int i = 0; i < ground.Length; i++)
                {
                    fb.F = ground[i];
                    uint b = fb.U;
                    h = (h ^ (b & 0xFF)) * 16777619u;
                    h = (h ^ ((b >> 8) & 0xFF)) * 16777619u;
                    h = (h ^ ((b >> 16) & 0xFF)) * 16777619u;
                    h = (h ^ (b >> 24)) * 16777619u;
                }
                for (int i = 0; i < biome.Length; i++)
                {
                    var c = biome[i];
                    h = (h ^ c.r) * 16777619u;
                    h = (h ^ c.g) * 16777619u;
                    h = (h ^ c.b) * 16777619u;
                }
                if (caves) h = (h ^ 0x5Au) * 16777619u;
                if (rivers) h = (h ^ 0xA5u) * 16777619u;
                if (FiresVoxelConfig.BuildsSkyIslands) h = (h ^ 0x3Cu) * 16777619u;
                return h;
            }
        }

        // Stay Full within this margin of any possible surface/cave so the
        // surface band is generous — a boundary chunk meshing empty wastes one
        // 83KB alloc (negligible); a wrongly-uniform surface chunk is a hole.
        private const float UniformClassifyMargin = 4f;

        // A chunk whose highest sample is more than carveReach below the LOWEST
        // ground over its column is solid rock nothing carves; one whose lowest
        // sample is above the HIGHEST ground is air. minG/maxG come from the
        // apron-inclusive GatherColumns, so a uniform verdict provably means the
        // chunk's shared faces hold no surface (the see-through guarantee).
        // Water-only chunks (see the zone build). The highest generated water level over a chunk column (centre + 4 corners).
        internal static int s_waterOnlyChunks;
        private static float WaterLevelOverColumn(int kx, int kz)
        {
            float best = -10000f;
            float x0 = kx * VoxelChunk.CELLS, z0 = kz * VoxelChunk.CELLS, c = VoxelChunk.CELLS;
            for (int i = 0; i < 5; i++)
            {
                float x = i == 0 ? x0 + c * 0.5f : x0 + ((i & 1) != 0 ? 1f : c - 1f);
                float z = i == 0 ? z0 + c * 0.5f : z0 + ((i & 2) != 0 ? 1f : c - 1f);
                if (GenWaterField.TryWaterSurfaceSmooth(x, z, out float level, out float depth) && depth > 0.05f && level > best) best = level;
            }
            return best;
        }

        private static ChunkKind ClassifyChunkKind(int ky, float minG, float maxG, float carveReach)
        {
            float top = ky * VoxelChunk.CELLS + VoxelChunk.CELLS;
            float bot = ky * VoxelChunk.CELLS - 1f;
            if (top <= minG - carveReach - UniformClassifyMargin) return ChunkKind.UniformSolid;
            if (bot >= maxG + UniformClassifyMargin) return ChunkKind.UniformAir;
            return ChunkKind.Full;
        }

        // Generate (or REgenerate, in place) the zone's chunk stack. On a
        // swapped zone this never touches the vanilla heightmap — existing
        // chunks get a fresh generate payload and remesh seamlessly, so a
        // terraform rewrap has NO window where both/neither terrain exists.
        private static void BuildZoneChunks(ZoneWrap wrap)
        {
            var zone = wrap.Zone;
            if (wrap.DataOnly) wrap.OpenMask = OpenMaskOf(zone);
            int kx0 = FloorDiv(Mathf.RoundToInt(zone.x * 64f - 32f), 32);
            int kz0 = FloorDiv(Mathf.RoundToInt(zone.y * 64f - 32f), 32);
            // One-zone-at-a-time: a closed zone holds real terrain only in the columns on an
            // open neighbour's edge, so caves and river beds are generated only for zones that
            // contain real columns; the chunk decides column by column.
            bool zoneHasRealTerrain = Zones.ZoneModeRegistry.HasRealTerrain(zone);
            bool caves = FiresVoxelConfig.GeneratesCaves && zoneHasRealTerrain;
            int caveDepth = Mathf.CeilToInt(FiresVoxelConfig.CaveMaxDepth.Value);
            // How far below the surface anything can CARVE — the depth beyond
            // which a column is guaranteed solid rock. Caves reach CaveMaxDepth;
            // voxel-side river beds (vanilla-wrap worlds only — Fires worlds
            // carve rivers into the base heightfield) reach RiverMaxDepth.
            bool voxelRivers = FiresVoxelConfig.CarvesRiverBeds && VoxelHydrology.Ready && !FiresWorldGenBase.Active && zoneHasRealTerrain;
            // Sinkholes are part of the cave carve and can reach DEEPER than
            // CaveMaxDepth, so the guaranteed-solid depth is the max of the two.
            // Understating it would let a chunk a sinkhole punches through be
            // classified UniformSolid — i.e. a hole with no mesh.
            float caveReach = caves
                ? Mathf.Max(caveDepth, FiresVoxelConfig.SinkholeDepth != null
                    ? FiresVoxelConfig.SinkholeDepth.Value : 0f)
                : 0f;
            // The gen-water bed re-carve also lowers ground (Fires worlds), so it
            // counts toward the depth below which a column is guaranteed solid.
            float bedCarve = FiresWorldGenBase.Active && FiresVoxelConfig.WaterMinChannelDepth != null
                ? FiresVoxelConfig.WaterMinChannelDepth.Value : 0f;
            float carveReach = caveReach
                + (voxelRivers ? FiresVoxelConfig.RiverMaxDepth.Value : 0f)
                + bedCarve;

            var newKeys = new List<Vector3Int>(12);
            var armed = new List<VoxelChunk>(12);
            bool usedFallback = false;
            long buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long gatherTicks = 0;
            for (int cz = 0; cz < 2; cz++)
            {
                for (int cx = 0; cx < 2; cx++)
                {
                    int kx = kx0 + cx, kz = kz0 + cz;
                    long gatherStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    GatherColumns(kx, kz, out var ground, out var biome, out float minG, out float maxG, ref usedFallback);
                    if (FiresLogger.DebugEnabled) NoteBorderRing(zone, kx, kz, ground, wrap.Swapped ? "rewrap / heal" : "wrap");   // 0.2.1321 skirt probe
                    // Vedr's open quake fissures: ONE grid per column feeds both the hash and every chunk's carve, so
                    // the change-gate and the cut can never disagree; the crack's depth widens the column's band.
                    float[] cracks = FatCracks.ColumnCracks(kx, kz);
                    float crackDepth = FatCracks.MaxDepth(cracks);
                    uint genHash = FatCracks.Fold(HashColumns(ground, biome, caves, voxelRivers), cracks);
                    gatherTicks += System.Diagnostics.Stopwatch.GetTimestamp() - gatherStart;

                    // SKY ISLANDS — extend this column's chunk band to cover
                    // any island above it; the air gap between ground and the
                    // island underside stays UniformAir (kind override below).
                    bool isl = FiresSkyIslands.TryGetBand(kx * 32f, kz * 32f,
                        kx * 32f + 32f, kz * 32f + 32f, out float islBot, out float islTop);
                    uint islandSig = isl
                        ? FiresSkyIslands.ColumnSignature(kx * 32f, kz * 32f, kx * 32f + 32f, kz * 32f + 32f)
                        : 0u;
                    bool halo = FatHaloArc.TryGetBand(kx * 32f, kz * 32f, kx * 32f + 32f, kz * 32f + 32f, out float haloBot, out float haloTop);

                    int kyLo = FloorDiv(Mathf.FloorToInt(minG) - Mathf.Max(caves ? caveDepth : 4, Mathf.CeilToInt(crackDepth) + 1), 32);
                    int kyHi = FloorDiv(Mathf.CeilToInt(maxG) + 2, 32);
                    if (isl)
                    {
                        int islHi = FloorDiv(Mathf.CeilToInt(islTop) + 2, 32);
                        if (islHi > kyHi) kyHi = islHi;
                    }
                    if (halo)
                    {
                        int hHi = FloorDiv(Mathf.CeilToInt(haloTop) + 2, 32), hLo = FloorDiv(Mathf.FloorToInt(haloBot) - 2, 32);
                        if (hHi > kyHi) kyHi = hHi;
                        if (hLo < kyLo) kyLo = hLo;
                    }
                    // 0.2.1036 HaloFlat (ring only): a column the ring does not cross builds nothing; a ring column builds the band only
                    // (its "ground" is FatHaloArc.RingOnlyGround, far below, never a chunk).
                    if (FatHaloArc.RingOnly)
                    {
                        if (!halo) continue;
                        kyLo = FloorDiv(Mathf.FloorToInt(haloBot) - 2, 32);
                        kyHi = FloorDiv(Mathf.CeilToInt(haloTop) + 2, 32);
                    }
                    // CROSS-ZONE Y-UNION (audit §4-1): every in-plane quad at a
                    // face belongs to the +side chunk — if a lateral neighbour
                    // column has chunks at a ky we don't, the faces between us
                    // have NO owner and see-through strips appear exactly at
                    // zone borders ("always a Y difference"). Extend our range
                    // to cover every live lateral neighbour's; the neighbour
                    // zone does the same on its next build, so both sides
                    // converge to the union.
                    for (int d = 0; d < 4; d++)
                    {
                        var nc = d == 0 ? new Vector2Int(kx + 1, kz)
                               : d == 1 ? new Vector2Int(kx - 1, kz)
                               : d == 2 ? new Vector2Int(kx, kz + 1)
                                        : new Vector2Int(kx, kz - 1);
                        if (VoxelWorld.TryGetColumnKyRange(nc, out int nLo, out int nHi))
                        {
                            if (nLo < kyLo) kyLo = nLo;
                            if (nHi > kyHi) kyHi = nHi;
                        }
                    }
                    // A sheet probe found 9 of 25 columns of a crater lake had NO view in the layer holding the water
                    // level, so no sheet = the dry squares. [Voxel Terrain] "Water-only chunks draw their sheet" (default OFF). ON: the
                    // layer holding this column's generated water level is built Full even when it holds no ground (it meshes empty and
                    // its view draws only the sheet), and the column's range reaches it.
                    int kyWater = int.MinValue;
                    if (FiresVoxelConfig.WaterOnlyChunks != null && FiresVoxelConfig.WaterOnlyChunks.Value)
                    {
                        float wl = WaterLevelOverColumn(kx, kz);
                        if (wl > -1000f)
                        {
                            kyWater = FloorDiv(Mathf.FloorToInt(wl), 32);
                            if (kyWater > kyHi) kyHi = kyWater;
                        }
                    }
                    for (int ky = kyLo; ky <= kyHi; ky++)
                    {
                        var key = new Vector3Int(kx, ky, kz);
                        newKeys.Add(key);

                        // The RLE win: a chunk fully below cave/river reach is
                        // solid rock, one fully above the surface is air. Neither
                        // needs arrays, a mesh, or a collider — but it stays
                        // registered so the Y-range union + border ownership hold.
                        var kind = ClassifyChunkKind(ky, minG, maxG, Mathf.Max(carveReach, crackDepth));
                        // Chunks intersecting the island band are real, AND their
                        // density depends on the island set — so one band test
                        // drives both the kind override and the generation hash.
                        float cTop = ky * 32f + 33f, cBot = ky * 32f - 1f;
                        bool inIslandBand = isl && cTop >= islBot - 2f && cBot <= islTop + 2f;
                        if (kind != ChunkKind.Full && inIslandBand) kind = ChunkKind.Full;
                        bool inHalo = halo && FatHaloArc.ChunkOverlaps(new Vector3(kx * 32f, ky * 32f, kz * 32f))
                            && FatHaloArc.GateAllows(new Vector3(kx * 32f, ky * 32f, kz * 32f));   // 0.2.1030 step B: only the band near a player
                        if (kind != ChunkKind.Full && inHalo) kind = ChunkKind.Full;
                        if (inHalo) FiresLogger.LogDebug($"[FatHalo] ring chunk ({kx},{ky},{kz}) built Full (y {ky * 32}..{ky * 32 + 32})");
                        if (kind == ChunkKind.UniformAir && ky == kyWater)
                        {
                            kind = ChunkKind.Full;
                            int wn = System.Threading.Interlocked.Increment(ref s_waterOnlyChunks);
                            FiresLogger.LogDebug($"[FiresVoxel] water-only chunk ({kx},{ky},{kz}) built Full for its water sheet (#{wn} this session; Water-only chunks draw their sheet = ON)");
                        }
                        if (kind != ChunkKind.Full)
                        {
                            VoxelWorld.EnsureUniformChunk(key, kind);
                            continue;
                        }
                        // HashColumns hashes ground/biome/caves/rivers and never sees
                        // islands, so a chunk in the band folds in the island
                        // signature. Add, move or delete an island and exactly those
                        // chunks fail the change-gate below and regenerate in place,
                        // with brush edits re-applied on the new ground. Chunks
                        // outside the band keep the plain column hash, so deep ground
                        // and cave chunks under an island are never touched.
                        uint chunkHash = inIslandBand ? unchecked((genHash ^ islandSig) * 16777619u) : genHash;
                        // A one-zone-at-a-time skyland wears its own biome whatever lies below it (often closed sea).
                        // 0.2.1095 (Fire, live 1093: an Ashlands skyland at 671,-1292 drew Meadows grass under its Ashlands props):
                        // the gate was ZoneModeRegistry.ModeActive alone, and super flat's skylands (1075) are driven without the zone
                        // registry, so their chunks took the GROUND's biome colour (BiomeCornerBlend at x,z, y 0 = the flat Meadows).
                        // Every driven skyland wears its own biome now; ground columns under it keep theirs (SkylandBiome's ground test).
                        bool skylandBiome = inIslandBand && Zones.ZoneModeSkylands.Driven;
                        if (skylandBiome) FiresSkyIslands.NoteIslandMeshBiome(kx, kz, cBot, ground, biome);
                        uint skylandColors = 0u;
                        var chunkBiome = skylandBiome ? SkylandBiome(kx, kz, cBot, ground, biome, out skylandColors) : biome;
                        if (skylandBiome) chunkHash = unchecked((chunkHash ^ 0x5C1Au ^ skylandColors) * 16777619u);
                        if (inHalo) chunkHash = unchecked((chunkHash ^ FatHaloArc.Signature()) * 16777619u);

                        if (VoxelWorld.TryGetChunk(key, out var existing) && existing.Kind == ChunkKind.Full)
                        {
                            // Change-gate: identical inputs → identical chunk.
                            // Skipping the no-op regen keeps the collider under
                            // the player untouched (rubber-band fix).
                            if (existing.LastGenHash == chunkHash && !existing.PendingGenerate)
                                continue;
                            // In-place: fresh generate payload onto the live
                            // chunk (brush edits re-apply on the new ground).
                            existing.MeshAll = true;
                            existing.LastGenHash = chunkHash;
                            existing.PrepareGenerate(ground, chunkBiome, caves, cracks);
                            armed.Add(existing);
                            continue;
                        }
                        // New, or promoting a uniform chunk the surface now
                        // crosses (terraform raised ground into old air).
                        var chunk = VoxelWorld.CreateStreamed(key);
                        chunk.MeshAll = true;
                        // Hidden until the swap flips voxel-on/vanilla-off in
                        // one frame (already-swapped zones render immediately).
                        // Skylands reshaped on first arrival, so a skyland's chunks wait for the
                        // swap in VoxelOnly too. Its far piece stays up until then (ZoneModeSkylands.ShowFarPieces), so
                        // the island turns from far to voxel in one frame instead of building up chunk by chunk.
                        chunk.SuppressRenderUntilSwap = !wrap.Swapped && (!VoxelOnlyMode.Active || FiresSkyIslands.ChunkOverlaps(chunk.WorldOrigin));
                        chunk.LastGenHash = chunkHash;
                        chunk.PrepareGenerate(ground, chunkBiome, caves, cracks);
                        armed.Add(chunk);
                    }
                }
            }

            long columnsEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            // Whole zone is armed (mutually pending) — NOW capture canonical
            // border values from every loaded generated neighbour (brush-
            // edited chunks included) so the worker generate ends with rings
            // that mirror their owners exactly. Same-batch borders need no
            // override: they generate from the same gather, deterministically.
            // Batch keys count as COVERING for the frontier mask — pending
            // siblings otherwise read "uncovered", every first-mesh grew
            // interior lips, and the swap heal re-meshed the whole zone to
            // shed them (double mesh+bake per wrap).
            if (wrap.DataOnly)
            {
                foreach (var c in armed) VoxelMeshPipeline.EnqueueGenerate(c);
            }
            else
            {
                // 0.2.1344: nearest-first replaces this: with it on, a first wrap is no live edit and stays out of the lane.
                // 0.2.1334: a brushed zone's first wrap takes the urgent lane (an edit is waiting on this swap). The mesher reads the
                // lane at enqueue, so it is marked here, before the enqueues below.
                if (!VoxelNearOrder.On && !wrap.Swapped && s_brushedZones.Contains(zone))
                    foreach (var c in armed) VoxelMeshPipeline.MarkEditUrgent(c.Key);
                s_batchKeys.Clear();
                foreach (var c in armed) s_batchKeys.Add(c.Key);
                foreach (var c in armed)
                {
                    c.SetBorderOverrides(VoxelChunk.CaptureBorderOverrides(c.Key));
                    VoxelMeshPipeline.EnqueueRemesh(c, s_batchKeys);
                }
            }

            long enqueueEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            // Y-RANGE CONSISTENCY INVARIANT (the see-through fix): a chunk may
            // NOT be dropped while any lateral neighbour column still has a
            // chunk at its height — that neighbour's shared face quads are
            // OWNED by this chunk, and a dropped owner means nobody meshes
            // the plane: a permanent horizontal see-through band exactly at
            // the border ("always a Y difference, never XZ"). Terraform
            // range-shrinks (filling a pit raises minG) were dropping border
            // chunks their neighbours still referenced. Such chunks are KEPT
            // and re-armed with the fresh gather instead (mostly air/solid —
            // cheap); truly unneeded chunks (no lateral neighbours) drop.
            foreach (var old in wrap.Keys)
                if (!newKeys.Contains(old))
                {
                    bool neighboured =
                           VoxelWorld.TryGetChunk(new Vector3Int(old.x + 1, old.y, old.z), out var n1) && n1 != null
                        || VoxelWorld.TryGetChunk(new Vector3Int(old.x - 1, old.y, old.z), out var n2) && n2 != null
                        || VoxelWorld.TryGetChunk(new Vector3Int(old.x, old.y, old.z + 1), out var n3) && n3 != null
                        || VoxelWorld.TryGetChunk(new Vector3Int(old.x, old.y, old.z - 1), out var n4) && n4 != null;
                    if (neighboured && VoxelWorld.TryGetChunk(old, out var keep) && keep != null)
                    {
                        // Re-arm on the column's fresh gather so its data
                        // stays canonical with the new terrain — as a uniform
                        // face-owner if the new ground put it out of the band.
                        int lcx = old.x - kx0, lcz = old.z - kz0;
                        if (lcx >= 0 && lcx < 2 && lcz >= 0 && lcz < 2)
                        {
                            GatherColumns(old.x, old.z, out var kg, out var kb, out float kMin, out float kMax, ref usedFallback);
                            if (FiresLogger.DebugEnabled) NoteBorderRing(zone, old.x, old.z, kg, "rewrap keep");   // 0.2.1321 skirt probe
                            float[] keepCracks = FatCracks.ColumnCracks(old.x, old.z);
                            var keepKind = ClassifyChunkKind(old.y, kMin, kMax, Mathf.Max(carveReach, FatCracks.MaxDepth(keepCracks)));
                            if (keepKind != ChunkKind.Full)
                                VoxelWorld.EnsureUniformChunk(old, keepKind);
                            else
                            {
                                VoxelWorld.PromoteToFull(keep);
                                keep.MeshAll = true;
                                keep.LastGenHash = FatCracks.Fold(HashColumns(kg, kb, caves, voxelRivers), keepCracks);
                                keep.PrepareGenerate(kg, kb, caves, keepCracks);
                                keep.SetBorderOverrides(VoxelChunk.CaptureBorderOverrides(old));
                                VoxelMeshPipeline.EnqueueRemesh(keep);
                            }
                        }
                        newKeys.Add(old);
                        continue;
                    }
                    VoxelWorld.DropChunkKey(old);
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var k = new Vector3Int(old.x + dx, old.y, old.z + dz);
                            if (s_priorityHealSet.Add(k)) s_priorityHeal.Enqueue(k);
                        }
                }
            wrap.Keys.Clear();
            wrap.Keys.AddRange(newKeys);
            HideExistingChunksAtWrap(wrap);   // 0.2.1334
            wrap.UsedFallback = usedFallback;
            ReportSlowBuild(wrap, buildStart, gatherTicks, columnsEnd, enqueueEnd, armed.Count);
        }

        // The dedi's main thread spent 105-260 ms per new area in here. The split says which part to move to
        // workers or spread over frames: the ground and biome gather, creating and arming the chunks, the border capture
        // and queueing, or keeping and dropping the old stack.
        private const double SlowZoneBuildMs = 50d;

        private static void ReportSlowBuild(ZoneWrap wrap, long buildStart, long gatherTicks, long columnsEnd, long enqueueEnd, int armed)
        {
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            double totalMs = TicksToMs(end - buildStart);
            if (totalMs < SlowZoneBuildMs) return;
            double gatherMs = TicksToMs(gatherTicks);
            FiresLogger.LogInfo($"[FiresVoxel] zone ({wrap.Zone.x},{wrap.Zone.y}) {(wrap.DataOnly ? "server data" : "wrap")} build took "
                + $"{totalMs:0} ms on the main thread: ground + biome gather {gatherMs:0} ms, chunk setup "
                + $"{TicksToMs(columnsEnd - buildStart) - gatherMs:0} ms ({armed} armed of {wrap.Keys.Count} key(s)), borders + queue "
                + $"{TicksToMs(enqueueEnd - columnsEnd):0} ms, keep/drop {TicksToMs(end - enqueueEnd):0} ms"
                + (wrap.UsedFallback ? "; some ground came from the generator (no heightmap or build data yet)." : "."));
        }

        private const double MillisecondsPerSecond = 1000d;

        private static double TicksToMs(long ticks) => ticks * MillisecondsPerSecond / System.Diagnostics.Stopwatch.Frequency;

        private static readonly HashSet<Vector3Int> s_batchKeys = new HashSet<Vector3Int>();

        // WRAP COLOUR (Debug only). Fire 10-23: "when we start digging on the super flat and the voxel wrap applies the terrain
        // paint changes color". At the wrap, the biome colour the vanilla tile DRAWS (its render mesh's vertex colours) against the one
        // the voxel mesh will take (BiomeCornerBlend.Sample), at the tile's centre and four inset corners, with the biome each source
        // answers there. One line per wrapped zone, at most WrapColourLines a session.
        private const int WrapColourLines = 40;
        private static int s_wrapColourLines;
        private static System.Reflection.FieldInfo s_hmRenderMesh;

        private static void LogWrapColours(Vector2s zone, Heightmap hm)
        {
            if (hm == null || s_wrapColourLines >= WrapColourLines) return;
            try
            {
                if (s_hmRenderMesh == null) s_hmRenderMesh = typeof(Heightmap).GetField("m_renderMesh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var mesh = s_hmRenderMesh?.GetValue(hm) as Mesh;
                var colors = mesh != null ? mesh.colors32 : null;
                int w = hm.m_width, n = w + 1;
                if (colors == null || colors.Length < n * n) { FiresLogger.LogDebug($"[FiresVoxel] wrap colour: zone ({zone.x},{zone.y}) vanilla tile has no render mesh colours to compare"); return; }
                Vector3 origin = hm.transform.position - new Vector3(w * hm.m_scale * 0.5f, 0f, w * hm.m_scale * 0.5f);
                int inset = Mathf.Max(1, w / 8);
                var pts = new[] { (w / 2, w / 2, "centre"), (inset, inset, "SW"), (w - inset, inset, "SE"), (inset, w - inset, "NW"), (w - inset, w - inset, "NE") };
                var sb = new System.Text.StringBuilder();
                int worst = 0;
                foreach (var (vx, vy, name) in pts)
                {
                    float wx = origin.x + vx * hm.m_scale, wz = origin.z + vy * hm.m_scale;
                    Color32 van = colors[vy * n + vx];
                    Color32 vox = BiomeCornerBlend.Sample(wx, wz);
                    int d = Mathf.Max(Mathf.Abs(van.r - vox.r), Mathf.Max(Mathf.Abs(van.g - vox.g), Mathf.Max(Mathf.Abs(van.b - vox.b), Mathf.Abs(van.a - vox.a))));
                    if (d > worst) worst = d;
                    var wg = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(wx, wz) : Heightmap.Biome.None;
                    var bm = VerdantsAscent.Terrain.Biome.BiomeManager.GetBiomeAt(wx, wz);
                    sb.Append($" | {name} ({wx:0},{wz:0}) vanilla {van.r},{van.g},{van.b},{van.a} voxel {vox.r},{vox.g},{vox.b},{vox.a} (GetBiome {wg}, BiomeManager {bm})");
                }
                s_wrapColourLines++;
                FiresLogger.LogDebug($"[FiresVoxel] wrap colour: zone ({zone.x},{zone.y}) {(worst > 8 ? "DIFFERS" : "matches")} (worst channel step {worst}/255; super flat {(FatSuperFlat.On ? "on" : "off")}){sb}");
            }
            catch (System.Exception e) { FiresLogger.LogDebug($"[FiresVoxel] wrap colour: zone ({zone.x},{zone.y}) compare skipped: {e.Message}"); }
        }

        // ── Wrap: first-time build for a zone ────────────────────────────────
        private static bool StartWrap(Vector2s zone, Heightmap hm)
        {
            var wrap = new ZoneWrap { Zone = zone, Hm = hm, StartedAt = Time.time };
            if (FiresLogger.DebugEnabled) LogWrapColours(zone, hm);
            LogWrapWhy(zone);   // 0.2.1346
            NoteWrapProof(zone);   // 0.2.1350 (proof: anchor ZDOs known at wrap start)
            wrap.T0 =System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1346
            float wrapStart = Time.realtimeSinceStartup;
            BuildZoneChunks(wrap);
            wrap.TGathered = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1346
            s_zones[zone] = wrap;
            if (ZoneReveal.On) VerdantsAscent.Water.Core.PlacedWaterSheets.HoldDrawnInZone(zone);   // 0.2.1346: sheets drawn before the zone knew it would swap
            if (!VoxelNearOrder.On && s_brushedZones.Contains(zone)) VoxelMeshPipeline.WatchUrgent(wrap.Keys, zone, wrapStart, wrapStart);   // 0.2.1334: leaves the lane as meshes land

            // Round-91 — HEAL PROVISIONAL BORDERS. Any already-wrapped
            // neighbour that seeded while THIS zone's heightmap was missing
            // used WorldGenerator fallback at the shared border → persistent
            // zone-edge split (real data ≠ fallback; brushing can't fix seed
            // data). Now that our heightmap exists, queue those neighbours for
            // an in-place regen — their border re-reads our real data and the
            // two sides converge exactly.
            int healed = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var n = new Vector2s(zone.x + dx, zone.y + dz);
                    if (s_zones.TryGetValue(n, out var nw) && nw.UsedFallback)
                    {
                        s_dirty.Add(n);
                        healed++;
                    }
                }
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                FiresLogger.LogInfo($"[FiresVoxel] wrapping zone ({zone.x},{zone.y}) → {wrap.Keys.Count} chunks queued" +
                    (wrap.UsedFallback ? " [provisional borders]" : "") +
                    (healed > 0 ? $" | queued {healed} neighbour border-heal(s)" : "") + ".");
            return true;
        }

        // ── Swap: hide vanilla once every voxel collider is live ────────────
        // The handoff is ATOMIC: voxel renderers un-suppress and vanilla
        // hides in the SAME frame — the two surfaces never render together.
        // GHOST-ZONE ACCELERATOR — vanilla CreateGhostZones generates ONE
        // zone per ZoneSystem tick (~4/s): entering fresh land holds the
        // near-creation gate (IsActiveAreaLoaded) CLOSED 50-60% of frames
        // for seconds while zone stubs trickle out, so veg/rocks/monsters
        // "take forever" (ZFLOW-proven; each Ghost spawn costs ~0.3ms on
        // Fires worlds). Generate every missing active-area zone under a
        // 4ms/frame budget; SpawnZone self-registers in m_generatedZones.
        private static AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> s_generatedZonesRef;
        private static System.Reflection.MethodInfo s_spawnZoneMethod;
        private static readonly object[] s_spawnZoneArgs = new object[3];

        // Has this zone ever been generated? ZoneSystem.IsZoneGenerated is private,
        // and a publicized compile does not grant runtime access, so it goes through
        // the same field ref GhostBurst uses. SERVER-ONLY meaning: a client's
        // generated-zone set is empty, so it answers false for everything there.
        // The island registry asks this to decide whether an island counts as placed.
        internal static bool IsZoneGenerated(Vector2s zone)
        {
            var zs = ZoneSystem.instance;
            if (zs == null) return false;
            if (s_generatedZonesRef == null)
                s_generatedZonesRef = AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
            var gen = s_generatedZonesRef(zs);
            return gen != null && gen.Contains(zone);
        }

        private static int s_burstCursor;

        // A dedicated server has no player of its own: its ghost bursts rotate through its peers.
        private static Vector3 NextBurstCenter()
        {
            s_burstCursor = (s_burstCursor + 1) % s_centers.Count;
            return s_centers[s_burstCursor];
        }

        // Server only, like vanilla's ZoneSystem.Update: a client's generated-
        // zone set is empty, so a client ghost spawn re-creates every zone's
        // vegetation and zone controller as NEW networked objects (duplicates).
        private static void GhostBurst(Vector3 center)
        {
            var zs = ZoneSystem.instance;
            if (zs == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!FiresVoxelConfig.WrapsAllTerrain) return;
            if (s_spawnZoneMethod == null)
            {
                s_generatedZonesRef = AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
                s_spawnZoneMethod = AccessTools.Method(typeof(ZoneSystem), "SpawnZone");
                if (s_spawnZoneMethod == null) return;
            }
            // ~1 s spikes on a local host: the 4 ms budget never bounded the FIRST
            // spawn of a tick, and one ghost spawn costs 112-159 ms, so every tick with a missing zone paid a full spike, frame
            // after frame. Now: ONE ghost spawn per tick at most, only after an under-budget frame and at least
            // GhostSpacing s after the last one; nearest zones first (rings outward, was row order from a corner); and a solo
            // host (no peers) only ghosts ZoneLoadingNear + 1 round its player (vanilla's own near/far loading covers the rest).
            if (Time.unscaledDeltaTime > GhostFrameBudget || Time.unscaledTime - s_lastGhostAt < GhostSpacing) return;
            var gen = s_generatedZonesRef(zs);
            var zc = ZoneSystem.GetZone(center);
            bool solo = ZNet.instance.GetPeers().Count == 0;
            int area = solo ? FiresCore.Bridge.SimulationDistanceAccess.ZoneLoadingNear + 1
                : FiresCore.Bridge.SimulationDistanceAccess.ZoneLoadingNear + FiresCore.Bridge.SimulationDistanceAccess.Far;
            for (int ring = 0; ring <= area; ring++)
                for (int dz = -ring; dz <= ring; dz++)
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Mathf.Abs(dx) != ring && Mathf.Abs(dz) != ring) continue;   // this ring's edge only
                        var z = new Vector2s(zc.x + dx, zc.y + dz);
                        if (gen.Contains(z)) continue;
                        s_spawnZoneArgs[0] = z;
                        s_spawnZoneArgs[1] = ZoneSystem.SpawnMode.Ghost;
                        s_spawnZoneArgs[2] = null;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try { s_spawnZoneMethod.Invoke(zs, s_spawnZoneArgs); }
                        catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] ghost burst threw for ({z.x},{z.y}): {ex.Message}"); }
                        s_lastGhostAt = Time.unscaledTime;
                        s_ghostCount++; s_ghostMs += sw.Elapsed.TotalMilliseconds;
                        if (sw.Elapsed.TotalMilliseconds > s_ghostMaxMs) s_ghostMaxMs = sw.Elapsed.TotalMilliseconds;
                        FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "[FiresVoxel] ghost spawn ({0},{1}) ring {2}/{3}{4}: {5:0.0} ms (session: {6} ghost(s), {7:0} ms total, max {8:0.0} ms; prev frame {9:0.0} ms)",
                            z.x, z.y, ring, area, solo ? " solo" : "", sw.Elapsed.TotalMilliseconds, s_ghostCount, s_ghostMs, s_ghostMaxMs, Time.unscaledDeltaTime * 1000f));
                        return;   // one per tick
                    }
        }

        private const float GhostFrameBudget = 1f / 45f, GhostSpacing = 0.25f;
        private static float s_lastGhostAt = -10f;
        private static int s_ghostCount;
        private static double s_ghostMs, s_ghostMaxMs;

        // How far the bed paint keeps fading ABOVE the water line (metres of
        // rise). Ramping on the field's depth alone has two problems: the depth
        // reading saturates well up the bank, so the paint stays at FULL weight
        // and then falls off wherever the coarse 10m field happens to cross the
        // feather — a blocky, grid-shaped edge — and it carries no information
        // about the bank itself. Fading on how far the ground stands OVER the
        // surface gives a shoreline band whose width follows the slope: broad
        // where the bank is shallow, tight where it is steep, which is what a
        // real damp margin does.
        private static float PaintShoreRise => FiresVoxelConfig.WaterPaintShoreBlend != null
            ? FiresVoxelConfig.WaterPaintShoreBlend.Value : 2.5f;

        private static float Smooth01(float t) => t * t * (3f - 2f * t);

        // (Comp-paint machinery DELETED — the wet mask is blended at rebuild
        // by Patch_Heightmap_WetMask and stamped once onto already-built
        // heightmaps by its Sweep. No TerrainComp writes, no pokes.)


        // The paint's own criterion — the ONLY paint authority, consumed by
        // Patch_Heightmap_WetMask. NOTHING here may be binary in the shore
        // field's SOURCE payload: chamfer-DT territories meet along straight
        // Voronoi edges, and any hard decision on the nearest source's
        // level/class printed a ruler-straight paint line along them (at a
        // river mouth the ocean-source side gated to 0, the river
        // side full). The wet value is computed PER CORNER CELL and blended,
        // the sea suppression is a smooth fade in GROUND altitude, and the
        // beach gate is a smooth multiplier — every transition feathers.
        private const float PAINT_SHORE_REACH = 9f;   // legacy const (style table governs)
        // PaintWet01 ran ~700k inner steps per shore heightmap rebuild and ~1089x per
        // paint column, nothing cached, so: an EXACT memo on the input bits, so every caller gets the very same value as before. It is a
        // pure function of the world's water grids + the zone-mode state, so it is cleared when any of those change (gate epoch,
        // registry version, the generator grids' identity) and when it grows past MemoCap. Main thread only, and never inside the
        // gate's planning / source-lookup scopes (thread-local state can change the answer there).
        private const int MemoCap = 400000;   // ~16 MB worst case; the near field needs ~4.2k points per heightmap
        private static readonly Dictionary<long, float> s_wetMemo = new Dictionary<long, float>(1 << 16);
        private static int s_wetMemoEpoch = int.MinValue, s_wetMemoVersion = int.MinValue;
        private static object s_wetMemoGrid;
        internal static int s_wetMainThread = -1;   // set in FiresAdminTerrain.Awake (Unity's main thread)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct WetKeyBits { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public int I; }
        internal static long WetMemoHits, WetMemoMisses, WetMemoClears;

        internal static void ClearWetMemo() { if (s_wetMemo.Count > 0) { s_wetMemo.Clear(); WetMemoClears++; } }

        internal static float PaintWet01(float pwx, float pwz)
        {
            if (s_wetMainThread != System.Threading.Thread.CurrentThread.ManagedThreadId
                || Zones.ZoneModeGate.InPlanningScope || Zones.ZoneModeGate.InSourceLookup)
                return PaintWet01Compute(pwx, pwz);
            int epoch = Zones.ZoneModeGate.CurrentEpoch, version = Zones.ZoneModeRegistry.Version;
            object grid = FiresWorldGenBase.Active ? (object)FiresWorldGenBase.WaterShorts : null;
            if (epoch != s_wetMemoEpoch || version != s_wetMemoVersion || !ReferenceEquals(grid, s_wetMemoGrid) || s_wetMemo.Count >= MemoCap)
            {
                ClearWetMemo();
                s_wetMemoEpoch = epoch; s_wetMemoVersion = version; s_wetMemoGrid = grid;
            }
            long key = ((long)new WetKeyBits { F = pwx }.I << 32) | (uint)new WetKeyBits { F = pwz }.I;
            if (s_wetMemo.TryGetValue(key, out float v)) { WetMemoHits++; return v; }
            v = PaintWet01Compute(pwx, pwz);
            if (GenWaterField.Ready) s_wetMemo[key] = v;   // a not-ready field answers 0 now and a real value later: never memoise that
            WetMemoMisses++;
            return v;
        }

        internal static float PaintWet01Uncached(float pwx, float pwz) => PaintWet01Compute(pwx, pwz);   // fat_wetmemo_check only

        // 0.2.1023 (Fire 10-24: river / lake tools "auto paint ... the proper sandy riverbottoms and wet shorelines"): water the
        // PLAYER placed (River, Lake, Water tools: FAT water disks / combiners) wets the ground too, through this same wet mask -
        // the one paint path generated rivers already use (r = dirt, alpha -> wet). Under the water = 1, within ShoreReach of it
        // = ShoreWet. Main thread only (it probes colliders); anywhere without placed water nearby it costs one zone lookup.
        private const float PlacedShoreReach = 1.5f, PlacedShoreWet = 0.6f, PlacedShoreOuter = 3.5f, PlacedShoreOuterWet = 0.3f;
        private static readonly Vector2[] s_shoreProbe = { new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f) };

        private static float PlacedWet01(float x, float z)
        {
            if (s_wetMainThread != System.Threading.Thread.CurrentThread.ManagedThreadId) return 0f;
            var p = new Vector3(x, 0f, z);
            if (VerdantsAscent.Water.FatWaterLevelGate.CombinersAt(p).Count == 0 && VerdantsAscent.Water.FatWaterLevelGate.LegacyAt(p).Count == 0) return 0f;
            if (VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(x, z, 0.05f, false, out _, out _)) return 1f;
            foreach (var d in s_shoreProbe)
                if (VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(x + d.x * PlacedShoreReach, z + d.y * PlacedShoreReach, 0.05f, false, out _, out _)) return PlacedShoreWet;
            // 0.2.1067 (Fire: "a sandy ... shore like vanilla rivers"): a second, fainter band further out, so the sand fades into
            // the surrounding ground instead of stopping in one step.
            foreach (var d in s_shoreProbe)
                if (VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(x + d.x * PlacedShoreOuter, z + d.y * PlacedShoreOuter, 0.05f, false, out _, out _)) return PlacedShoreOuterWet;
            return 0f;
        }

        // 0.2.1081 (Fire: "fix the fucking sand not showing in the bottom of the bed and the clutter not redrawing properly"):
        // NotePlacedWater runs the moment a dab asks for its water (CreateDiskOfType), but the water a bed cell is tested against
        // (FatWaterChecks.TryWaterAt) only answers once the server has confirmed the disk (WaterOperationRPC -> combiner) AND the
        // carved chunk's new collider is live (its ground raycast still hit the old top at y 50 over a y 49 surface = "no water").
        // So the re-paint ran against dry ground, memoised wet = 0 for every bed cell, stamped the heightmaps dry, and nothing ever
        // asked again: no sand, and the grass rolled before the water stayed. Each placed water now stays pending until the water
        // really answers at its centre (or 20 s), then the area is re-painted, the clutter gate memo dropped and the grass rebuilt.
        internal static int PendingWetCount => s_pendingWet.Count;   // 0.2.1346
        private sealed class PendingWet { public Vector3 C; public float R, Until, NextTry; }
        private static readonly List<PendingWet> s_pendingWet = new List<PendingWet>();
        private const float PendingWetSeconds = 20f, PendingWetRetry = 0.5f;
        private static float s_nextBedLog;

        internal static void TickPendingWet()
        {
            if (s_pendingWet.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = s_pendingWet.Count - 1; i >= 0; i--)
            {
                var p = s_pendingWet[i];
                if (now < p.NextTry) continue;
                p.NextTry = now + PendingWetRetry;
                bool ready = VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(p.C.x, p.C.z, 0.05f, false, out float surface, out float bed);
                if (!ready && now < p.Until) continue;
                s_pendingWet.RemoveAt(i);
                RefreshWetArea(p.C, p.R);
                float reach = p.R + PlacedShoreOuter + 8f;
                int cleared = ready ? VerdantsAscent.Water.Tools.WaterShapingTools.ClearUnderWater(p.C, p.R + 1f) : 0;
                FatSuperFlat.ForgetClutterMemo();
                if (ClutterSystem.instance != null)
                {
                    try { ClutterSystem.instance.ResetGrass(p.C, reach); }
                    catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresWater] ResetGrass threw: {ex.Message}"); }
                }
                if (FiresLogger.DebugEnabled && now >= s_nextBedLog)
                {
                    s_nextBedLog = now + 5f;
                    float wet = PaintWet01Uncached(p.C.x, p.C.z);
                    if (FatDiag.WaterClutter) FiresLogger.LogDebug($"[FiresWater] clutter redraw after carve: memo cleared, {cleared} vegetation object(s) under the water removed, ResetGrass r {reach:F1} at ({p.C.x:F0},{p.C.z:F0}) "
                        + (ready ? $"after the water answered (surface {surface:F2}, bed {bed:F2})" : $"after {PendingWetSeconds:0} s with no water answering there (timeout)"));
                    if (FatDiag.WaterClutter) FiresLogger.LogDebug($"[FiresWater] bed paint at ({p.C.x:F0},{p.C.z:F0}): sand {wet:F2} wet {wet:F2} (PlacedWet01 now; the stamp + voxel column copy re-compose from it)");
                }
            }
        }

        // A placed water changed here: the memoised wet values and the columns' paint masks around it are stale.
        internal static void NotePlacedWater(Vector3 centre, float radius)
        {
            RefreshWetArea(centre, radius);
            s_pendingWet.Add(new PendingWet { C = centre, R = radius, Until = Time.unscaledTime + PendingWetSeconds, NextTry = Time.unscaledTime + PendingWetRetry });
        }

        // 0.2.1348 (Fire: "our river beds also dont properly clear the green paint and put in the wet sandy beds"): the wet memo and the bands hold what TryWaterAt said when they were read, and it says dry
        // over a bed whose collider has not landed. A ground swap or a remesh after the last disk re-arms nothing, so those columns stay dry for the session. A river's finish calls this once all of it has landed.
        internal static void RepaintPlacedWater(Vector3 centre, float radius) => RefreshWetArea(centre, radius);

        private static void RefreshWetArea(Vector3 centre, float radius)
        {
            ClearWetMemo();
            float reach = radius + PlacedShoreOuter + 2f;
            int c0x = Mathf.FloorToInt((centre.x - reach) / 32f), c1x = Mathf.FloorToInt((centre.x + reach) / 32f);
            int c0z = Mathf.FloorToInt((centre.z - reach) / 32f), c1z = Mathf.FloorToInt((centre.z + reach) / 32f);
            for (int cz = c0z; cz <= c1z; cz++)
                for (int cx = c0x; cx <= c1x; cx++) VoxelWorld.MarkPaintDirty(new Vector2Int(cx, cz));
            int restamped = Patch_Heightmap_WetMask.Restamp(centre, reach);
            int uw = VoxelUnderwaterClutter.Invalidate(centre, reach);   // 0.2.1083: FAT's reeds/weeds/silt rebuild their cells now the placed water answers
            s_placedRestamps += restamped;
            if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_nextRestampLog)
            {
                s_nextRestampLog = Time.unscaledTime + 5f;
                if (FatDiag.WaterClutter) FiresLogger.LogDebug($"[FiresWater] wet paint re-stamp: placed water at ({centre.x:F0},{centre.z:F0}) r {radius:F1} -> {restamped} heightmap mask(s) re-stamped now ({s_placedRestamps} this session), {uw} underwater clutter cell(s) rebuilt; bed + shore band + grass + reeds follow");
            }
        }
        private static float s_nextRestampLog;
        private static int s_placedRestamps;

        private static float PaintWet01Compute(float pwx, float pwz) => Mathf.Max(PaintWet01Generated(pwx, pwz), PlacedWet01(pwx, pwz));

        private static float PaintWet01Generated(float pwx, float pwz)
        {
            // One zone at a time: a closed zone is sea and paints nothing; a swapped zone reads its source's water
            // (sx, sz), raised or lowered with its land, and a lifted zone's own sea wears a shore band of its own.
            if (Zones.ZoneModeGate.IsClosedForTerrain(pwx, pwz)) return 0f;
            float sx = pwx, sz = pwz;
            Zones.ZoneModeGate.TranslateToSource(ref sx, ref sz, out float lift);
            bool nearSea = Zones.ZoneModeLiftShore.TryNearest(pwx, pwz, Zones.ZoneModeLiftShore.Reach,
                out byte seaCls, out float seaLevel, out float seaDist);
            // Cheap far bail on the 8m shore field — beyond every biome's
            // reach (+ slack for its own granularity) nothing can paint.
            if (FiresWorldGenBase.Active)
            {
                if (!nearSea && (!FiresWorldGenBase.TryGetShoreCorners(sx, sz, out float dist8, out _, out _,
                        out _, out _, out _, out _, out _, out _, out _, out _) || dist8 >= 18f))
                    return 0f;
            }
            else if (!GenWaterField.Ready) return 0f;
            byte pbio = GenWaterField.BiomeCodeAt(pwx, pwz);
            var st = FiresWorldGenBase.WaterStyleFor(pbio);
            if (st.PaintWeight <= 0f) return 0f;
            if (!GenWaterField.TryGround(pwx, pwz, out float pgy)) return 0f;
            // beach factor — smooth in both inputs: patchy low-freq noise ×
            // gentle ground; scales river shores from damp line to full sand
            float beachT;
            {
                float slope = 0f;
                if (GenWaterField.TryGround(pwx + 2f, pwz, out float gxp)
                    && GenWaterField.TryGround(pwx - 2f, pwz, out float gxm)
                    && GenWaterField.TryGround(pwx, pwz + 2f, out float gzp)
                    && GenWaterField.TryGround(pwx, pwz - 2f, out float gzm))
                    slope = Mathf.Sqrt((gxp - gxm) * (gxp - gxm) + (gzp - gzm) * (gzp - gzm)) * 0.25f;
                float bn = VoxelCaveNoise.Noise01(pwx * 0.012f, 777.7f, pwz * 0.012f);
                float th = 0.62f - st.BeachBias * 0.18f;
                beachT = Smooth01(Mathf.Clamp01((bn - th) / 0.06f))
                       * Smooth01(Mathf.Clamp01((0.28f - slope) / 0.10f));
            }
            // EXACT 2M INTEGRATION — the band is 2-7m wide, so it is judged
            // against the REAL water cells (the whole point of the 2m grid),
            // not an 8m distance field coarser than the band itself. Every
            // wet cell within reach contributes through the same smooth
            // style falloffs; the strongest contribution wins. Continuous in
            // position by construction — no seams possible.
            // Vanilla world: there is no 2m generator field to integrate, so the
            // nearest wet source comes from the channel geometry and the still
            // grid instead. Same CornerWet, same styles, same falloffs — only
            // the neighbour search differs.
            float best = nearSea ? CornerWet(seaCls, seaLevel, pgy, in st, seaDist, beachT) : 0f;
            if (!FiresWorldGenBase.Active)
            {
                float vReach = Mathf.Max(1f, st.ShoreReach) + 2f;
                if (GenWaterField.TryNearestWet(pwx, pwz, vReach, out byte vCls, out float vLvl, out float vDist))
                    best = Mathf.Max(best, CornerWet(vCls, vLvl, pgy, in st, vDist, beachT));
                return best;
            }

            var wS = FiresWorldGenBase.WaterShorts;
            var gS = FiresWorldGenBase.GridShorts;
            if (wS == null || gS == null) return best;
            int n = FiresWorldGenBase.GridN;
            float half = FiresWorldGenBase.GridHalf;
            float cell = (2f * half) / (n - 1);
            int cx = Mathf.RoundToInt((sx + half) / cell);
            int cz = Mathf.RoundToInt((sz + half) / cell);
            int R = Mathf.CeilToInt(12f / cell);
            for (int dz = -R; dz <= R; dz++)
            {
                int z2 = cz + dz;
                if (z2 < 1 || z2 >= n - 1) continue;
                float wzc = z2 * cell - half;
                int row = z2 * n;
                for (int dx = -R; dx <= R; dx++)
                {
                    int x2 = cx + dx;
                    if (x2 < 1 || x2 >= n - 1) continue;
                    int i = row + x2;
                    float gv = gS[i] * FiresWorldGenBase.UNPACK;
                    float wv = wS[i] * FiresWorldGenBase.UNPACK;
                    if (wv <= gv + 0.05f) continue;                    // dry
                    float ddx = sx - (x2 * cell - half), ddz = sz - wzc;
                    float d = Mathf.Sqrt(ddx * ddx + ddz * ddz);
                    if (d >= 12f) continue;
                    byte cls = FiresWorldGenBase.WaterClassAtReal(x2 * cell - half, wzc);
                    float c = CornerWet(cls, wv + lift, pgy, in st, d, beachT);
                    if (c > best) best = c;
                }
            }
            return best;
        }

        private static float CornerWet(byte cls, float slvl, float pgy,
            in FiresWorldGenBase.FWaterStyle st, float dist, float beachT)
        {
            // frozen paints nothing (snow stays); ocean shores stay vanilla —
            // both feather over the 8m corner blend instead of cutting
            if (cls == FiresWorldGenBase.WC_NONE || cls == FiresWorldGenBase.WC_FROZEN
                || cls == FiresWorldGenBase.WC_OCEAN) return 0f;
            float rise = Mathf.Min(PaintShoreRise, st.ShoreRise);
            float weight = st.PaintWeight;
            float reach = Mathf.Max(1f, st.ShoreReach);
            if (cls == FiresWorldGenBase.WC_RIVER || cls == FiresWorldGenBase.WC_STREAM)
            {
                reach = Mathf.Lerp(3f, reach, beachT);
                weight *= Mathf.Lerp(0.5f, 1f, beachT);
                rise = Mathf.Lerp(Mathf.Min(rise, 1.0f), rise, beachT);
            }
            else if (cls == FiresWorldGenBase.WC_SWAMP)
            {
                rise = Mathf.Min(rise, 1.0f);
            }
            float hGate = 1f - Mathf.Clamp01(dist / reach);
            if (hGate <= 0f) return 0f;
            float vGate = 1f;
            float above = pgy - slvl;
            if (above > 0f)
                vGate = 1f - Mathf.Clamp01(above / Mathf.Max(0.05f, rise));
            return Smooth01(hGate) * Smooth01(vGate) * weight;
        }

        // Zone solid AND settled — the swap flips colliders on in one frame,
        // but fallers that ground-check in that exact frame can still catch a
        // mid-bake state. A short settle window after the swap ends the
        // bounce for good.
        private static readonly Dictionary<Vector2s, float> s_swapAt = new Dictionary<Vector2s, float>();

        internal static bool IsZoneSolidFor(Vector2s zone, float minAge)
        {
            if (!s_zones.TryGetValue(zone, out var w) || w == null || !w.Swapped) return false;
            if (!s_swapAt.TryGetValue(zone, out float t)) { s_swapAt[zone] = Time.time; return minAge <= 0f; }
            return Time.time - t >= minAge;
        }

        private const float BlockedSummarySeconds = 30f;

        private static int s_blockedZones, s_blockedPendingGen, s_blockedQueued, s_blockedNoView, s_blockedNoCollider;
        private static float s_blockedLongest;
        private static float s_nextBlockedSummary;

        // WINDOW PEAKS, not one frame's tally. The counters above are rebuilt from scratch on every update, and
        // this report used to print whichever frame happened to land when the 30s window expired - one frame in
        // ~1800, the other 1799 discarded by ClearBlockedTally. Queue depths sampled that way cannot tell real
        // saturation from a momentary spike: one long traverse emitted pendingGen 0, 0, 0 and 17, which is
        // four unrelated instants and not a trend. These survive the window, so the line reports its true worst.
        private static int s_peakZones, s_peakPendingGen, s_peakQueued, s_peakNoView, s_peakNoCollider;
        private static float s_peakLongest;

        // One line for every zone the mesher is behind on, at most every 30s.
        private static void ReportBlocked()
        {
            FoldBlockedPeaks();
            if (s_peakZones == 0) { s_nextBlockedSummary = 0f; return; }
            if (Time.time < s_nextBlockedSummary) return;
            s_nextBlockedSummary = Time.time + BlockedSummarySeconds;
            FiresLogger.LogWarning($"[FiresVoxel] up to {s_peakZones} zone(s) showing vanilla terrain while their voxel "
                + $"chunks mesh, longest {s_peakLongest:F0}s: peak pendingGen={s_peakPendingGen} queued={s_peakQueued} "
                + $"noView={s_peakNoView} colliderNotReady={s_peakNoCollider}.");
            ClearBlockedPeaks();
        }

        private static void FoldBlockedPeaks()
        {
            if (s_blockedZones > s_peakZones) s_peakZones = s_blockedZones;
            if (s_blockedPendingGen > s_peakPendingGen) s_peakPendingGen = s_blockedPendingGen;
            if (s_blockedQueued > s_peakQueued) s_peakQueued = s_blockedQueued;
            if (s_blockedNoView > s_peakNoView) s_peakNoView = s_blockedNoView;
            if (s_blockedNoCollider > s_peakNoCollider) s_peakNoCollider = s_blockedNoCollider;
            if (s_blockedLongest > s_peakLongest) s_peakLongest = s_blockedLongest;
            ClearBlockedTally();
        }

        private static void ClearBlockedTally()
        {
            s_blockedZones = s_blockedPendingGen = s_blockedQueued = s_blockedNoView = s_blockedNoCollider = 0;
            s_blockedLongest = 0f;
        }

        private static void ClearBlockedPeaks()
        {
            s_peakZones = s_peakPendingGen = s_peakQueued = s_peakNoView = s_peakNoCollider = 0;
            s_peakLongest = 0f;
        }

        private static void TrySwap(ZoneWrap wrap)
        {
            if (wrap.DataOnly) { TrySwapDataOnly(wrap); return; }
            if (wrap.Hm == null) return;

            int pendingGen = 0, queued = 0, noView = 0, noCollider = 0;
            foreach (var key in wrap.Keys)
            {
                if (!VoxelWorld.TryGetChunk(key, out var chunk)) continue;
                // Uniform chunks are all-solid/all-air — no surface, no collider
                // to wait on. They're ready by definition; the surface band holds
                // the walkable ground.
                if (chunk.Kind != ChunkKind.Full) continue;
                if (chunk.PendingGenerate) pendingGen++;
                else if (chunk.Queued) queued++;
                else if (!VoxelWorld.TryGetView(key, out var view) || view == null)
                {
                    noView++;
                    // Self-heal the headless SPAWN DEADLOCK: a meshed chunk whose
                    // apply was stale-dropped (the driver's IsLive/Version race)
                    // is left viewless AND unqueued — nothing ever re-meshes it,
                    // so the zone swap blocks forever, the dedi never loads the
                    // spawn area, and every client times out at "connecting".
                    // Re-arm it; EnqueueRemesh dedupes (no-op once it lands).
                    if (!VoxelMeshPipeline.ResultWaiting(chunk)) VoxelMeshPipeline.EnqueueRemesh(chunk);   // 0.2.1344: its mesh waiting its turn nearest-first is not a lost apply
                }
                else if (!view.ColliderReady) noCollider++;
            }
            if (pendingGen + queued + noView + noCollider > 0)
            {
                if (pendingGen > 0 && FiresLogger.DebugEnabled) LogGenerateWait(wrap, pendingGen, queued, noView, noCollider);   // 0.2.1350 (proof)
                // Stuck-swap diagnostics. One line PER ZONE every 8s buried the log on a busy world (323 lines in a
                // 9.6 minute run), and every one of them says the same thing: the mesher is behind.
                // They roll up into one line instead, which keeps the signal — how many zones, the longest wait and
                // what they are waiting on — and loses the repetition. A blocked zone still SHOWS VANILLA TERRAIN: the
                // swap is the moment voxel replaces it, so a long block delays the upgrade, it does not stall anything.
                float waited = Time.time - wrap.StartedAt;
                if (waited > StuckWrapSeconds)
                {
                    s_blockedZones++;
                    s_blockedLongest = Mathf.Max(s_blockedLongest, waited);
                    s_blockedPendingGen += pendingGen;
                    s_blockedQueued += queued;
                    s_blockedNoView += noView;
                    s_blockedNoCollider += noCollider;
                }
                return;
            }
            if (!RevealPrepReady(wrap)) return;   // 0.2.1346
            wrap.TPrep = System.Diagnostics.Stopwatch.GetTimestamp();

            foreach (var key in wrap.Keys)
            {
                if (VoxelWorld.TryGetChunk(key, out var chunk)) chunk.SuppressRenderUntilSwap = false;
                if (VoxelWorld.TryGetView(key, out var view) && view != null) view.SetRenderSuppressed(false);
            }
            ShowEditChunksAtSwap(wrap);   // 0.2.1334
            try { NoteSwapRing(wrap); }   // 0.2.1321 skirt probe (Debug, once per zone)
            catch (System.Exception ex) { FiresLogger.LogDebug($"[FiresVoxel] border ring at swap probe threw: {ex.Message}"); }
            try { VoxelZoneLook.AtSwap(wrap.Zone, wrap.Hm, wrap.Keys); }
            catch (System.Exception ex) { FiresLogger.LogDebug($"[FiresVoxel] zone look ({wrap.Zone.x},{wrap.Zone.y}) skipped: {ex.Message}"); }
            HideVanilla(wrap.Hm);
            VerdantsAscent.Water.Core.PlacedWaterSheets.NoteZoneSwapped(wrap.Zone);   // 0.2.1344: the sheet rebuilds this zone held
            RecordHiddenIdentity(wrap);
            wrap.Swapped = true;
            wrap.HeldChunks = VoxelMeshPipeline.ReleaseHeldRemeshes(wrap.Zone, out wrap.HeldAsks, out wrap.HeldGaveUp);   // 0.2.1353: after Swapped, so their run is not held again
            ZoneReveal.Release(wrap.Zone, null);   // 0.2.1346: this frame; after Swapped so a held step sees the swapped zone
            LogSwapStages(wrap);   // 0.2.1346
            s_swapAt[wrap.Zone] = Time.time;
            // A skyland's far piece goes and its held dressing shows in this same frame.
            try { Zones.ZoneModeSkylands.OnZoneSwapped(wrap.Zone); }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] skyland swap hand-off threw for ({wrap.Zone.x},{wrap.Zone.y}): {ex.Message}"); }
            FatClutterRegrow.Zone(wrap.Zone);   // grass rolled on vanilla's collider re-rolls on the voxel ground
            if (IsDedicatedServer)
            {
                s_serverSwaps++;
                s_serverSwapChunks += wrap.Keys.Count;
                FiresLogger.LogDebug($"[FiresVoxel] server colliders for zone ({wrap.Zone.x},{wrap.Zone.y}): {wrap.Keys.Count} chunk(s) meshed in "
                                    + $"{(Time.time - wrap.StartedAt) * 1000f:0} ms (wrap start to swap; vanilla's heightmap collider hidden).");
            }
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                FiresLogger.LogInfo($"[FiresVoxel] zone ({wrap.Zone.x},{wrap.Zone.y}) SWAPPED to voxel terrain " +
                    $"({wrap.Keys.Count} chunks, {Time.time - wrap.StartedAt:F1}s after wrap start).");
            // W2 — schedule hydrology water for this zone (springs + ponds),
            // once per session per zone; the pour-proximity check makes it
            // idempotent across sessions.
            bool zoneContentAllowed = Zones.ZoneModeRegistry.IsOpen(wrap.Zone);
            if (zoneContentAllowed && FiresVoxelConfig.PlacesHydrologyWater && s_waterDone.Add(wrap.Zone))
                s_waterQueue.Add((wrap.Zone, Time.time + 2f));

            // Weld: borders of the freshly swapped zone (and the ring of
            // neighbours whose aprons read into it) re-mirror NOW instead of
            // waiting for the rolling sweep to come around (~30s).
            QueuePriorityHeal(wrap);

            // Phase 2 — dress the zone's caves. Runs at the swap because that is
            // the first moment the voxel density (which the void scan reads) is
            // final. Server-only and idempotent against restarts; a no-op on
            // clients and on zones with no caves.
            if (zoneContentAllowed)
            {
                try { VoxelCaveContent.PlaceForZone(wrap.Zone); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] cave content threw for ({wrap.Zone.x},{wrap.Zone.y}): {ex}"); }
            }

            // Paint that loaded from TerrainComp ZDOs before the zone
            // wrapped gets its masks on the freshly visible chunks.
            if (wrap.PrepSince < 0f || wrap.PrepCapped) MarkZonePaintDirty(wrap.Zone);   // 0.2.1346: RevealPrepReady already composed them
            NotePaintLeft(wrap);   // 0.2.1353 (proof)
        }

        // Zone (zx,zy) covers world [zx·64−32, zx·64+32) = chunk columns
        // 2zx−1..2zx; the extra west/south ring covers neighbouring columns
        // whose edge texel (i==32) probes into this zone via the +ε idiom.
        private static void MarkZonePaintDirty(Vector2s zone)
        {
            for (int cz = zone.y * 2 - 2; cz <= zone.y * 2; cz++)
                for (int cx = zone.x * 2 - 2; cx <= zone.x * 2; cx++)
                    VoxelWorld.MarkPaintDirty(new Vector2Int(cx, cz));
        }

        // A location pad registered at (center, radius): re-wrap the already-
        // wrapped zones its disc touches so GatherColumns re-reads and bakes the
        // flat pad into their voxel surface. Wrapped-only — an unwrapped zone
        // reads the pad on its first gather, so nothing to arm there.
        internal static void MarkAreaDirty(Vector3 center, float radius)
        {
            float r = radius + 24f;
            var zmin = ZoneSystem.GetZone(new Vector3(center.x - r, 0f, center.z - r));
            var zmax = ZoneSystem.GetZone(new Vector3(center.x + r, 0f, center.z + r));
            for (int zz = zmin.y; zz <= zmax.y; zz++)
                for (int zx = zmin.x; zx <= zmax.x; zx++)
                {
                    var z = new Vector2s(zx, zz);
                    if (s_zones.ContainsKey(z)) s_dirty.Add(z);
                }
        }

        // A Vedr quake scar changed inside this XZ rectangle: re-wrap exactly the wrapped zones it overlaps. A dedicated
        // server gets no heightmap poke for it; on a client the poke already queued them and the set keeps one entry.
        internal static void MarkRectDirty(float minX, float minZ, float maxX, float maxZ)
        {
            var zmin = ZoneSystem.GetZone(new Vector3(minX, 0f, minZ));
            var zmax = ZoneSystem.GetZone(new Vector3(maxX, 0f, maxZ));
            for (int zz = zmin.y; zz <= zmax.y; zz++)
                for (int zx = zmin.x; zx <= zmax.x; zx++)
                {
                    var z = new Vector2s(zx, zz);
                    if (s_zones.ContainsKey(z)) s_dirty.Add(z);
                }
        }

        // One-zone-at-a-time: true once this peer shows the zone's current terrain — either it is not
        // wrapped (the vanilla heightmap is the terrain) or its in-place rewrap has finished and every
        // surface chunk is generated, meshed and collidable.
        internal static bool IsZoneTerrainSettled(Vector2s zone)
        {
            if (!s_zones.TryGetValue(zone, out var wrap)) return true;
            if (s_dirty.Contains(zone) || !wrap.Swapped) return false;
            if (wrap.DataOnly && (wrap.AwaitingRegen || OpenMaskOf(zone) != wrap.OpenMask)) return false;
            foreach (var key in wrap.Keys)
            {
                if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk.Kind != ChunkKind.Full) continue;
                if (chunk.PendingGenerate || chunk.Queued) return false;
                if (wrap.DataOnly) continue;
                if (!VoxelWorld.TryGetView(key, out var view) || view == null || !view.ColliderReady) return false;
            }
            return true;
        }

        // A zone that opened after it was already swapped in as sea floor never swaps again, so the
        // per-zone dressing a first swap does (hydrology water, cave content) is done here instead.
        internal static void OnZonesOpened(IEnumerable<Vector2s> zones)
        {
            foreach (var zone in zones)
            {
                if (!s_zones.TryGetValue(zone, out var wrap) || !wrap.Swapped || wrap.DataOnly) continue;
                if (FiresVoxelConfig.PlacesHydrologyWater && s_waterDone.Add(zone))
                    s_waterQueue.Add((zone, Time.time + 2f));
                try { VoxelCaveContent.PlaceForZone(zone); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] cave content threw for ({zone.x},{zone.y}): {ex}"); }
            }
        }

        private const float GroundCheckSeconds = 0.5f;
        private const float GroundLogSeconds = 30f;

        // A swapped zone hides vanilla's collider, so every Full chunk must keep a view (its mesh + collider). A chunk that
        // loses its view after the swap (a remesh whose apply was stale-dropped, as the pre-swap spawn self-heal in TrySwap
        // describes) leaves a hole with no ground at all: dropped items fell 30 and 270 m after a zone reload.
        // Such a chunk is re-armed, and vanilla's collider (not its renderer) stands in until every view is back. A remesh
        // keeps its old baked collider until the new one lands, so ColliderReady going false is not a hole and is not counted.
        private static void KeepGroundUnderSwappedZone(ZoneWrap w)
        {
            if (w.DataOnly || Time.time < w.NextGroundCheckAt) return;
            w.NextGroundCheckAt = Time.time + GroundCheckSeconds;
            int viewless = 0;
            foreach (var key in w.Keys)
            {
                if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk.Kind != ChunkKind.Full) continue;
                if (VoxelWorld.TryGetView(key, out var view) && view != null) continue;
                viewless++;
                if (!chunk.PendingGenerate && !chunk.Queued) VoxelMeshPipeline.EnqueueRemesh(chunk);
            }
            var col = s_hmCollider(w.Hm);
            if (col == null) return;
            if (viewless > 0)
            {
                FatNativeTrace.Step("vanilla collider stand-in on", 0, "zone " + w.Zone.x + "," + w.Zone.y); col.enabled = true;
                if (w.VanillaColliderBack) return;
                w.VanillaColliderBack = true;
                if (Time.time >= w.NextGroundLogAt)
                {
                    w.NextGroundLogAt = Time.time + GroundLogSeconds;
                    FiresLogger.LogDebug($"[FiresVoxel] zone ({w.Zone.x},{w.Zone.y}): {viewless} voxel chunk(s) lost their mesh and collider after "
                        + "the swap; re-meshing them, and vanilla's collider stands in until they are back.");
                }
            }
            else if (viewless == 0 && w.VanillaColliderBack)
            {
                w.VanillaColliderBack = false;
                FatNativeTrace.Step("vanilla collider off", 0, "zone " + w.Zone.x + "," + w.Zone.y);
                col.enabled = false;
                FiresLogger.LogInfo($"[FiresVoxel] zone ({w.Zone.x},{w.Zone.y}): voxel colliders back; vanilla's collider hidden again.");
            }
        }

        private static void RecordHiddenIdentity(ZoneWrap wrap)
        {
            if (wrap?.Hm == null) return;
            wrap.HiddenHmId = wrap.Hm.GetInstanceID();
            var rend = wrap.Hm.GetComponent<MeshRenderer>();
            wrap.HiddenRendererId = rend != null ? rend.GetInstanceID() : 0;
        }

        // Vanilla's RenderGroupSubscriber (added by Heightmap.Awake) sets this
        // renderer's enabled flag on every Overworld render-group change: off
        // inside interiors, on again when the player leaves one. A swapped zone
        // suspends that subscription, so leaving a dungeon cannot switch the
        // vanilla ground back on over the voxel ground.
        // 0.2.1406 (FatApi.IsHeightmapHidden / VoxelSwapRevision, [seasons]' Vedr/Heightmap border rule): true while this exact
        // Heightmap is the hidden ground of a swapped voxel zone. Bumped on every hide / show, so a caller re-checks only on change.
        internal static int SwapRevision;
        internal static bool IsHeightmapHidden(Heightmap hm)
        {
            if (hm == null) return false;
            var zone = ZoneSystem.GetZone(hm.transform.position);
            return s_zones.TryGetValue(zone, out var w) && w.Swapped && w.Hm == hm;
        }

        private static void HideVanilla(Heightmap hm)
        {
            if (hm == null) return;
            SwapRevision++;
            FatNativeTrace.Step("vanilla hide", hm.GetInstanceID(), hm.transform.position.ToString());
            var groupSubscriber = hm.GetComponent<RenderGroupSubscriber>();
            if (groupSubscriber != null && groupSubscriber.enabled) groupSubscriber.enabled = false;
            var rend = hm.GetComponent<MeshRenderer>();
            if (rend != null && rend.enabled) rend.enabled = false;
            var col = s_hmCollider(hm);
            if (col != null && col.enabled) col.enabled = false;
        }

        // Hands the renderer back to its render group in the group's current
        // state, so a zone unwrapped while the player is inside an interior
        // stays hidden until they leave, like every other vanilla zone.
        private static void ShowVanilla(Heightmap hm)
        {
            SwapRevision++;   // 0.2.1406; the zone leaves s_zones later in the same Unwrap, so a check from the next frame on sees it shown
            var groupSubscriber = hm.GetComponent<RenderGroupSubscriber>();
            var rend = hm.GetComponent<MeshRenderer>();
            if (rend != null)
                rend.enabled = groupSubscriber == null || RenderGroupSystem.IsGroupActive(groupSubscriber.Group);
            if (groupSubscriber != null) groupSubscriber.enabled = true;
            var col = s_hmCollider(hm);
            FatNativeTrace.Step("vanilla show", hm.GetInstanceID(), hm.transform.position.ToString()); if (col != null) col.enabled = true;
        }

        // ── Unwrap: vanilla back FIRST, then drop chunks ─────────────────────
        // Hard cap on the wrapped radius even in wrapAll mode (metres from an
        // active center). Valheim keeps heightmaps loaded over a much wider ZDO
        // radius than it renders; holding voxel meshes+colliders for ALL of them
        // ran the memory away with a co-located client+dedi. The cap stays well
        // beyond Valheim's active area (~135m corner), so nothing the player can
        // reach ever loses its collider — it only sheds the far ZDO-only ring.
        // WRAP RADIUS — how far out zones are voxelised. This is the single
        // biggest lever on load: a zone is 2×2 chunks horizontally by 3-4 tall,
        // so ~16 chunks each, and every one has to be generated, meshed and
        // APPLIED before its zone can swap.
        //
        // At the old fixed 256m that is a 9×9 zone envelope — the dedi log
        // measured zones=44 chunks=654 with swapped=0 pendingSwap=44, i.e. not
        // one zone ever finished, all of them blocked on `queued=N` in the mesh
        // apply pipeline. Vanilla only keeps m_activeArea=1 (3×3 = 9 zones)
        // fully active plus one distant ring (5×5 = 25). Matching that is
        // ~144 chunks instead of 654 — the same queue, 4.5× less to drain.
        //
        // Config-driven so the boundary can be found by testing rather than
        // guessed: beyond this radius terrain is vanilla heightmap, and our
        // surface differs from it wherever we carve (river beds especially), so
        // too tight a radius may show a step at the edge.
        private static float WRAP_HARD_CAP =>
            FiresVoxelConfig.WrapHeadlessCap != null ? FiresVoxelConfig.WrapHeadlessCap.Value : 160f;
        private static readonly List<Vector3> s_centers = new List<Vector3>(8);
        // 0.2.1070: a zone whose live voxel chunks are back (wrapped and swapped); FiresSkyIslandFar drops its far copies then.
        internal static bool ZoneSwapped(Vector2s zone) => s_zones.TryGetValue(zone, out var w) && w.Swapped;

        private static bool NearAnyCenter(Vector3 c, float r)
        {
            for (int i = 0; i < s_centers.Count; i++)
                if (Mathf.Abs(s_centers[i].x - c.x) <= r && Mathf.Abs(s_centers[i].z - c.z) <= r) return true;
            return false;
        }

        // ── Memory governor ──────────────────────────────────────────────────
        // WRAP_HARD_CAP bounds the wrapped RADIUS per center; it does NOT bound
        // the TOTAL across spread-out peers (a busy dedi wraps every peer's ring
        // and OOMs). The governor is the RAM ceiling: it eases ONE shared wrap
        // radius down under memory pressure and back up when there's headroom,
        // until the live Full-chunk count fits MaxLoadedChunks. The wrap AND drop
        // bounds both read this radius, so shrinking simply stops wrapping (and
        // sheds) the far ring — no zone is wrapped one tick and evicted the next
        // (the churn a farthest-first eviction pass would cause). It never falls
        // below SolidKeepRadius: in VoxelOnly the voxel collider is the only
        // ground, so a peer's own footprint must always stay wrapped.
        // The floor the governor may never ease below — in VoxelOnly the voxel
        // collider is the ONLY ground, so a peer's own footprint must stay
        // wrapped or they fall through the world. Vanilla's active area is
        // 3×3 zones (~96m to a corner), so 128m is two zones of margin.
        private static float SolidKeepRadius =>
            FiresVoxelConfig.WrapKeepRadius != null ? FiresVoxelConfig.WrapKeepRadius.Value : 128f;
        private const float GovernorRadiusStep = 8f;  // metres/tick the radius eases in/out
        private static float s_governorRadius = 160f;
        private static float s_nextBudgetWarn;

        private static float GovernorBound(float baseBound)
        {
            float b = Mathf.Min(baseBound, s_governorRadius);
            return b < SolidKeepRadius ? SolidKeepRadius : b;
        }

        private static void UpdateGovernorRadius()
        {
            int budget = FiresVoxelConfig.MaxLoadedChunks.Value;
            int live = VoxelWorld.BudgetedChunkCount;
            if (live > budget)
                s_governorRadius = Mathf.Max(SolidKeepRadius, s_governorRadius - GovernorRadiusStep);
            else if (live < budget - (budget >> 3))   // < budget·0.875 — hysteresis before re-growing
                s_governorRadius = Mathf.Min(WRAP_HARD_CAP, s_governorRadius + GovernorRadiusStep);

            if (live > budget && s_governorRadius <= SolidKeepRadius + 0.5f && Time.time >= s_nextBudgetWarn)
            {
                s_nextBudgetWarn = Time.time + 30f;
                FiresLogger.LogWarning($"[FiresVoxel] chunk budget {budget} exceeded ({live} live) with the wrap radius " +
                    $"already at the {SolidKeepRadius:F0}m floor around {s_centers.Count} peer(s) — the guaranteed-" +
                    $"solid set alone is over budget. Raise 'Max loaded chunks' or reduce Cave Max Depth; the governor " +
                    $"will NOT shrink further (fall-through risk).");
            }
        }

        private static void Unwrap(Vector2s zone)
        {
            if (!s_zones.TryGetValue(zone, out var wrap)) return;
            FatNativeTrace.Step("unwrap begin", 0, "zone " + zone.x + "," + zone.y + " chunks " + wrap.Keys.Count);
            if (wrap.Hm != null)
            {
                ShowVanilla(wrap.Hm);
                // Render/collision rebuilds are skipped while hidden — the
                // returning vanilla meshes are stale until this rebuild. Poke(1) = vanilla rebuilds it in its next
                // LateUpdate, not inside this frame (Poke(0) regenerated on the spot: grass + collision cook in the unwrap frame).
                wrap.Hm.Poke(1);
            }
            FiresSkyIslandFar.Capture(zone, wrap.Keys);   // 0.2.1070: island chunks stay in view as far copies
            foreach (var key in wrap.Keys) VoxelWorld.DropChunkKey(key);
            // RAM audit: the zone's crack grids and swap time go with it; both rebuild on a re-wrap
            // (crack grids are a pure cache; s_swapAt is set again when the zone swaps).
            foreach (var key in wrap.Keys) FatCracks.ForgetColumn(key.x, key.z);
            s_swapAt.Remove(zone);
            // The receding frontier: surviving chunks in adjacent wrapped
            // zones still carry a "covered" frontier mask toward this zone —
            // no lip, no quads, and no heal ever fired (border mirroring
            // skips absent owners, so the sweep saw zero divergence). Queue
            // the zone's ring; dead keys self-discard, survivors remesh with
            // fresh masks and the lips return.
            QueuePriorityHeal(wrap);
            s_zones.Remove(zone);
            VoxelMeshPipeline.ReleaseHeldRemeshes(zone, out _, out _);   // 0.2.1353: its dropped chunks are skipped by the run, a chunk that stays (an edit above the band) meshes as any other
            s_hideGaveUp.Remove(zone);   // 0.2.1334
            VoxelGenWaterMesh.DropZone(zone);
            VoxelSnowDepth.Forget(zone);
            // Let the zone be re-dressed if it comes back; the ZDO check keeps
            // that from doubling up on content that already persisted.
            VoxelCaveContent.ForgetZone(zone);
            FatNativeTrace.Step("unwrap end", 0, "zone " + zone.x + "," + zone.y);
        }

        private static void UnwrapAll()
        {
            s_scratchDrop.Clear();
            foreach (var kv in s_zones) s_scratchDrop.Add(kv.Key);
            foreach (var z in s_scratchDrop) Unwrap(z);
        }

        // ── Terraform: the zone an edit landed in, rebuilt first ─────────────────────────────────────────────
        // Edited zones (the Regenerate hook's own zone, not its 8 neighbours) and when their heightmap changed.
        private static readonly Dictionary<Vector2s, float> s_terraformSince = new Dictionary<Vector2s, float>();
        private static bool s_terraformKick;
        // An edited zone is rebuilt again at most this often (s); its neighbours keep the 2 s debounce.
        private const float TerraformDebounce = 0.5f, NeighbourDebounce = 2f;

        // Rebuild dirty zones in place: an edited zone first (kickOnly: only those, from the per-frame kick), then at most
        // one other per tick. Vanilla stays hidden and the old voxel meshes/colliders stay live until each chunk's fresh mesh
        // swaps in, so there's no window where both or neither terrain exists.
        private static void DrainTerraform(bool kickOnly)
        {
            bool rebuilt = false;
            if (s_terraformSince.Count > 0)
            {
                s_scratchTerraform.Clear();
                foreach (var kv in s_terraformSince) s_scratchTerraform.Add(kv.Key);
                foreach (var zone in s_scratchTerraform)
                {
                    if (!s_zones.TryGetValue(zone, out var wrap)) { s_terraformSince.Remove(zone); continue; }
                    if (Time.time - wrap.LastRewrapAt < TerraformDebounce) { s_terraformKick = true; continue; }   // next frame
                    float since = s_terraformSince[zone];
                    s_terraformSince.Remove(zone);
                    if (!s_dirty.Contains(zone)) continue;
                    if (RebuildDirty(zone, wrap, since)) rebuilt = true;
                }
            }
            if (kickOnly || rebuilt) return;
            foreach (var dz in s_dirty)
            {
                if (!s_zones.TryGetValue(dz, out var dwrap)) { s_dirty.Remove(dz); break; }
                if (Time.time - dwrap.LastRewrapAt < NeighbourDebounce) continue;   // debounce — retry next tick
                if (dwrap.DataOnly && !HeightmapBuildCache.EnsureZoneAndNeighbours(dz)) continue;
                RebuildDirty(dz, dwrap, -1f);
                break;
            }
        }

        private static readonly List<Vector2s> s_scratchTerraform = new List<Vector2s>();

        // One in-place rebuild of a dirty zone. editedAt: when its heightmap changed (realtime), or -1 for a neighbour.
        private static bool RebuildDirty(Vector2s dz, ZoneWrap dwrap, float editedAt)
        {
            if (dwrap.DataOnly && !HeightmapBuildCache.EnsureZoneAndNeighbours(dz)) return false;
            s_dirty.Remove(dz);
            dwrap.LastRewrapAt = Time.time;
            if (dwrap.Hm == null && !dwrap.DataOnly) { Unwrap(dz); return false; }
            if (dwrap.DataOnly) dwrap.AwaitingRegen = true;
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                FiresLogger.LogInfo($"[FiresVoxel] zone ({dz.x},{dz.y}) terrain data changed → in-place regen ({dwrap.Keys.Count} chunks).");
            float start = Time.realtimeSinceStartup;
            bool urgent = editedAt >= 0f && !dwrap.DataOnly;
            if (urgent) VoxelMeshPipeline.MarkUrgent(dwrap.Keys);
            BuildZoneChunks(dwrap);
            if (urgent) VoxelMeshPipeline.WatchUrgent(dwrap.Keys, dz, editedAt, start);
            if (editedAt >= 0f && FatLogGate.Allow("tf-start", out _))
                if (FatDiag.Terraform) FiresLogger.LogDebug($"[FiresVoxel] terraform: zone ({dz.x},{dz.y}) rebuild started {(start - editedAt) * 1000f:0} ms after its heightmap changed; " +
                                     $"built {(Time.realtimeSinceStartup - start) * 1000f:0} ms, {dwrap.Keys.Count} chunk(s) to remesh");
            // In-place rewraps left neighbour rings stale for a full sweep pass (~30s+) — the priority heal only fired on
            // FIRST-time swaps. Queue it here too so terraform borders converge in ticks (entries skip-and-requeue while
            // chunks are mid-regen).
            QueuePriorityHeal(dwrap);
            return true;
        }

        // Terrain op (hoe/pickaxe/TerrainComp) regenerated a heightmap: mark
        // that zone AND its 8 neighbours dirty (their border/apron samples read
        // into this zone's data — kills border lips after terraform).
        // The tick drains one rewrap per 0.25s so hoe-drag op storms converge
        // instead of thrashing.
        // From FAT's one Heightmap.Regenerate hook (FatVanillaHooks), before the distant-paint inject.
        internal static void OnHeightmapRegenerate(Heightmap heightmap)
        {
            RewrapZone.Run(heightmap);
            KeepHiddenAfterOp(heightmap);
        }

        // 0.2.1057 (Fire's Voxheim pick 7): a vanilla terrain op (hoe, pickaxe, a water tool's vanilla cut) on a zone already
        // swapped to voxel regenerates its heightmap. Vanilla never writes the renderer's or collider's enabled flag there, so
        // the hidden ground should stay hidden; this checks it after every regenerate and hides it again if anything turned it
        // back on (unless the zone deliberately lent vanilla's collider back, VanillaColliderBack).
        private static float s_nextKeptHiddenLog;

        private static void KeepHiddenAfterOp(Heightmap hm)
        {
            try
            {
                if (hm == null || s_isDistantLod(hm)) return;
                var zone = ZoneSystem.GetZone(hm.transform.position);
                if (!s_zones.TryGetValue(zone, out var w) || !w.Swapped || w.Hm != hm) return;
                var rend = hm.GetComponent<MeshRenderer>();
                var col = s_hmCollider(hm);
                bool rendOn = rend != null && rend.enabled, colOn = col != null && col.enabled && !w.VanillaColliderBack;
                if (rendOn) rend.enabled = false;
                if (colOn) col.enabled = false;
                if (Time.unscaledTime < s_nextKeptHiddenLog) return;
                s_nextKeptHiddenLog = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[FiresVoxel] hidden heightmap kept hidden after a vanilla terrain op in voxel zone ({zone.x},{zone.y}) ({(rendOn || colOn ? $"switched back off: renderer {(rendOn ? "was on" : "off")}, collider {(colOn ? "was on" : "off")}" : "it never came back")})");
            }
            catch (System.Exception ex) { FiresLogger.LogDebug($"[FiresVoxel] hidden heightmap check skipped: {ex.Message}"); }
        }

        // 0.2.1171: a per-zone hash of the heightmap's heights; equal = the regenerate was paint-only.
        private static readonly AccessTools.FieldRef<Heightmap, List<float>> s_hmHeights = AccessTools.FieldRefAccess<Heightmap, List<float>>("m_heights");
        private static readonly Dictionary<Vector2s, long> s_heightHash = new Dictionary<Vector2s, long>();
        private static int s_paintOnlySkips, s_heightRebuilds;
        private static float s_paintOnlyLogAt;

        private static bool HeightsUnchanged(Heightmap hm, Vector2s zone)
        {
            List<float> h;
            try { h = s_hmHeights(hm); } catch { return false; }
            if (h == null || h.Count == 0) return false;
            long hash = h.Count;
            unchecked
            {
                for (int i = 0; i < h.Count; i++)
                    hash = hash * 1000003L ^ (long)(h[i] * 1000f);   // mm-quantised: a real edit moves heights by far more
            }
            bool same = s_heightHash.TryGetValue(zone, out long old) && old == hash;
            s_heightHash[zone] = hash;
            if (!same) s_heightRebuilds++;
            return same;
        }

        private static void NotePaintOnly(Vector2s zone)
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_paintOnlyLogAt) return;
            s_paintOnlyLogAt = Time.unscaledTime + 30f;
            FiresLogger.LogDebug($"[FiresVoxel] paint-only regenerates (heights unchanged) took the paint compose only, no zone rebuild: {s_paintOnlySkips} in 30 s; height changes rebuilt {s_heightRebuilds} (last paint-only zone ({zone.x},{zone.y})) (0.2.1171)");
            s_paintOnlySkips = 0; s_heightRebuilds = 0;
        }

        private static class RewrapZone
        {
            internal static void Run(Heightmap __instance)
            {
                try
                {
                    if (__instance == null || s_isDistantLod(__instance)) return;
                    var zone = ZoneSystem.GetZone(__instance.transform.position);
                    Patch_TerrainComp_ApplyOperation_Timing.OnRegenerated(zone);
                    // 0.2.1157 (Fire: trails as instant as Infinity Hammer's levelling): a regenerate whose only change is trail-stamp
                    // paint G re-composed its few texels and grouped the chunk(s) under them (FatTrailFast); heights are unchanged, so
                    // neither the 9-column paint compose nor the zone rewrap below has anything to do.
                    if (FatTrailFast.OnRegenerate(__instance)) return;
                    // Every paint op routes through Regenerate too
                    // (DoOperation → Poke), so this is THE paint refresh hook,
                    // on every peer. BEFORE the zone-count gate: brush chunks
                    // exist in Vanilla world mode too, and MarkPaintDirty
                    // no-ops for columns without voxel chunks.
                    MarkZonePaintDirty(zone);
                    if (s_zones.Count == 0) return;
                    // 0.2.1171 (Fire at base, FPS 47-100; FDT stalls: StreamerDriver.Update 178-191 ms = the terraform drain's
                    // in-place zone rebuild, worst 109.7 ms in one frame; "[FiresWetMask] dry skips 9660" per 10 s = ~1000 heightmap
                    // regenerates/s from companions' and creatures' walk/cultivate ops): a regenerate whose HEIGHTS did not change
                    // (a paint-only op: cultivate, walk paint, snow) only needs the paint compose above. The zone + its 8 neighbours
                    // were rebuilt for every one of them (BuildZoneChunks + a remesh of every chunk = the apply storm).
                    if (HeightsUnchanged(__instance, zone)) { s_paintOnlySkips++; NotePaintOnly(zone); return; }
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var z = new Vector2s(zone.x + dx, zone.y + dz);
                            if (s_zones.ContainsKey(z)) s_dirty.Add(z);
                        }
                    // The edited zone itself goes first, this frame: it used to wait its turn among the 9 dirty zones,
                    // one per 0.25 s tick, behind a 2 s per-zone debounce.
                    if (s_zones.ContainsKey(zone))
                    {
                        if (!s_terraformSince.ContainsKey(zone)) s_terraformSince[zone] = Time.realtimeSinceStartup;
                        s_terraformKick = true;
                    }
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresVoxel] regenerate dirty-mark threw: {ex.Message}");
                }
            }
        }

        // W2 — auto-place water for a freshly swapped zone: bank-following
        // SPRING pours at hydrology headwaters (they fill the carved beds
        // downhill — the whole point), settling water volumes on ponds.
        // Idempotent: skips anywhere a pour already exists (persisted pours
        // from previous sessions load with the zone before this runs).
        // A generated source must pin the way a hammer spring does: at the
        // ruler ON the real ground. The grid level is a ~10m-cell estimate;
        // clamp it into a sane window above the ACTUAL local surface (voxel
        // truth first, heightmap data fallback) so a source never stands
        // proud of the real bed (dome) or under it (dry).
        private static Vector3 ClampSpringToGround(Vector3 spot)
        {
            float bed;
            if (!VoxelWorld.TrySampleSurfaceY(spot.x, spot.z, spot.y + 12f, spot.y - 32f, out bed, out _))
            {
                if (!Heightmap.GetHeight(new Vector3(spot.x + 0.013f, 0f, spot.z + 0.013f), out bed))
                    return spot;
            }
            if (spot.y < bed + 0.2f) spot.y = bed + 0.2f;
            else if (spot.y > bed + 5f) spot.y = bed + 5f;
            return spot;
        }

        // ONE placement path for generated water. The Fires generator and the
        // channel network both answer through GenWaterField, so a vanilla world
        // pours exactly the way a generated one does. This retires the old
        // vanilla-side spring + pond-volume placement and with it the "lakes
        // wider than a pour grid are skipped" limit; springs stay available as
        // hammer tools.
        private static float s_nextDrownSweep;
        private static int s_drownTotal;
        private static readonly List<GameObject> s_doomed = new List<GameObject>(64);

        // Vegetation standing in a carve. On a CONVERTED world vanilla placed
        // every tree and bush against the original ground, so a bowl cut under
        // them leaves firs growing out of the lake bed. Rocks
        // stay — a boulder in the shallows is what a real shoreline looks like,
        // and that is the one exception.
        //
        // PERIODIC, NOT ONCE PER ZONE. The first version ran 2s after a zone
        // swapped, which is before the zone's objects are necessarily
        // instantiated — on a dedi they stream in later. It therefore found
        // nothing in most zones, marked them done, and left the firs standing
        // (3 zones cleared in a whole session). A throttled sweep over the live
        // instance table catches them whenever they arrive; the wet test is the
        // only gate it needs, so there is no per-zone bookkeeping to get wrong.
        //
        // Ownership: Core's SafeDestroy claims the ZDO before destroying it, so
        // this is legal from a client; hand-rolled ZDO destruction on a ZNetView
        // is not. Never on a Fires world (its generator placed vegetation
        // against the water field already).
        // Only stumps, logs and rocks stay under the water, plus perhaps a few small beech trees
        // and saplings: what is cleared is FatUnderWaterPlacement's rule (logs now STAY; small
        // beeches/birches, Destructible-only, and saplings GO), tool water drowns anything whose base is under the
        // surface by more than FatUnderWaterPlacement.SurfaceTolerance (was 0.3 m, which spared the shallow rim), and the
        // sweep runs on every world with tool water, not only hydrology worlds (it returned without the water field, so a
        // tool pond on a world without hydrology never drowned anything). Fires worlds still skip the generated half.
        private static void DrownedVegTick()
        {
            bool generated = !FiresWorldGenBase.Active && GenWaterField.Ready;
            if (!generated && global::VerdantsAscent.Water.Core.WaterMeshCombiner.AllCombiners.Count == 0) return;
            if (Time.time < s_nextDrownSweep) return;
            s_nextDrownSweep = Time.time + 5f;
            if (ZNetScene.instance == null || ZNetScene.instance.m_instances == null) return;

            float clear = FiresVoxelConfig.VegTreeWaterClearance != null
                ? FiresVoxelConfig.VegTreeWaterClearance.Value : 1f;
            int scanned = 0, veg = 0, overWater = 0;
            s_doomed.Clear();

            foreach (var kv in ZNetScene.instance.m_instances)
            {
                var view = kv.Value;
                if (view == null) continue;
                scanned++;
                var go = view.gameObject;
                if (!IsClearableVegetation(go)) continue;
                veg++;
                var p = go.transform.position;
                if (generated && GenWaterField.TryWaterSurfaceSmooth(p.x, p.z, out float lvl, out float dep) && dep >= 0.15f)
                {
                    overWater++;
                    if (p.y > lvl + clear) continue;   // stands clear of the waterline
                    s_doomed.Add(go);
                    continue;
                }
                // Saplings stood in a tool pond: tool ponds (Vpool/va_pool WaterDisks, pours)
                // are not in the generated field, so they never drowned anything. Their surface is read at the plant's own
                // base (a ray from above would land on its canopy), and only a base truly under that water is cleared.
                float tool = VerdantsAscent.Water.Core.FatWaterChecks.SurfaceAt(new Vector3(p.x, p.y + DrownProbeLift, p.z));
                if (tool <= SeaPlaneGate || tool < p.y + DrownedDepth) continue;
                overWater++;
                s_doomed.Add(go);
            }

            for (int i = 0; i < s_doomed.Count; i++)
                FiresCore.Net.NetworkObjectHelper.SafeDestroy(s_doomed[i]);

            if (s_doomed.Count > 0)
            {
                s_drownTotal += s_doomed.Count;
                FiresLogger.LogDebug($"[FiresVoxel] drowned vegetation: cleared {s_doomed.Count} " +
                    $"(session {s_drownTotal}) | scanned {scanned} instance(s), {veg} clearable veg, {overWater} over generated water.");
            }
            else if (veg > 0 && FiresLogger.VerboseEnabled)
            {
                FiresLogger.LogVerbose($"[FiresVoxel] drowned vegetation: nothing to clear | scanned {scanned}, " +
                    $"{veg} clearable veg, {overWater} over generated water (none below its waterline + {clear:0.0}m).");
            }
            s_doomed.Clear();
        }

        // Rocks, logs, stumps and water plants stay: FatUnderWaterPlacement's keep list.
        // Tool water: the probe sits just above the plant's base, which must be at least DrownedDepth under the surface;
        // water at or below the sea sheet is vanilla's own and left alone.
        private const float DrownProbeLift = 0.1f, DrownedDepth = FatUnderWaterPlacement.SurfaceTolerance, SeaPlaneGate = 30.3f;

        // One rule with generation and waterlife_test (FatUnderWaterPlacement): never anything built, alive,
        // carried or stored; rocks, logs, stumps and water plants stay; trees, Tree-type destructibles, plants, pickables,
        // bushes and saplings go.
        private static bool IsClearableVegetation(GameObject go) => FatUnderWaterPlacement.IsRefusedInstance(go);

        private static void PlaceWaterForZone(Vector2s zone)
        {
            if (!GenWaterField.Ready) return;

            // ONE OWNER, BOTH WORLD TYPES: the per-column store in VoxelChunk.
            // Generated water is the chunk sheet, the same way FullVoxel has
            // always built it — so nothing is queued here. Pouring as well put
            // a settled mesh under the sheet (the double-layer report), and
            // pouring INSTEAD left carved basins with nothing laid across them.
            // VoxelGenLiquid stays for the manual pour tools and cave pools.
            if (FiresVoxelConfig.LegacyMeshWater != null && FiresVoxelConfig.LegacyMeshWater.Value)
                VoxelGenWaterMesh.BuildForZone(zone);
        }

        // va_voxel_wrapall — burst-wrap every loaded zone right now (skips the
        // one-per-tick pacing; swaps still gate on collider readiness).
        private static bool s_wrapAllCommand;   // 0.2.1346
        public static int WrapAllNow()
        {
            if (!ModeActive) return 0;
            int started = 0;
            s_wrapAllCommand = true;   // 0.2.1346
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || s_isDistantLod(hm)) continue;
                var zone = ZoneSystem.GetZone(hm.transform.position);
                if (s_zones.ContainsKey(zone)) continue;
                if (StartWrap(zone, hm)) started++;
            }
            s_wrapAllCommand = false;
            FiresLogger.LogInfo($"[FiresVoxel] wrapall: started {started} zone wrap(s); {s_zones.Count} total tracked.");
            return started;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);
    }

    // VANILLA-PARITY biome color blending (Heightmap.GetBiomeColor(ix,iy) +
    // m_cornerBiomes): the biome is sampled at the containing ZONE's four
    // corners and every vertex gets the bilinear of the one-hot corner
    // colors — the same 64m gradients vanilla terrain wears. The per-vertex
    // one-hot we used before drew hard pixel edges at every biome boundary.
    // Corner order matches vanilla: 0=(x0,z0) 1=(x1,z0) 2=(x0,z1) 3=(x1,z1).
    // Main-thread only (BiomeManager + the single-slot zone memo).
    internal static class BiomeCornerBlend
    {
        private static int s_zx = int.MinValue, s_zz = int.MinValue;
        private static Color32 s_c0, s_c1, s_c2, s_c3;
        private static bool s_same;

        public static Color32 Sample(float wx, float wz)
        {
            // (No super flat shortcut: it answered hard Meadows / Ocean, so a content area's forced biome and every BiomeOverride
            // turned Meadows green the moment the zone wrapped - Fire 10-23. The patched WorldGenerator.GetBiome under BiomeManager
            // gives the vanilla tile's answer on super flat too.)
            int zx = Mathf.FloorToInt((wx + 32f) / 64f);
            int zz = Mathf.FloorToInt((wz + 32f) / 64f);
            if (zx != s_zx || zz != s_zz)
            {
                s_zx = zx; s_zz = zz;
                float x0 = zx * 64f - 32f, z0 = zz * 64f - 32f;
                var b0 = VerdantsAscent.Terrain.Biome.BiomeManager.GetBiomeAt(x0, z0);
                var b1 = VerdantsAscent.Terrain.Biome.BiomeManager.GetBiomeAt(x0 + 64f, z0);
                var b2 = VerdantsAscent.Terrain.Biome.BiomeManager.GetBiomeAt(x0, z0 + 64f);
                var b3 = VerdantsAscent.Terrain.Biome.BiomeManager.GetBiomeAt(x0 + 64f, z0 + 64f);
                s_same = b0 == b1 && b0 == b2 && b0 == b3;
                s_c0 = Heightmap.GetBiomeColor(b0);
                s_c1 = s_same ? s_c0 : Heightmap.GetBiomeColor(b1);
                s_c2 = s_same ? s_c0 : Heightmap.GetBiomeColor(b2);
                s_c3 = s_same ? s_c0 : Heightmap.GetBiomeColor(b3);
            }
            if (s_same) return s_c0;
            // Smoothstepped like vanilla's own corner blend (Heightmap.RebuildRenderMesh), so the colour does not shift at the wrap.
            float ix = DUtils.SmoothStep(0f, 1f, (wx - (zx * 64f - 32f)) * (1f / 64f));
            float iz = DUtils.SmoothStep(0f, 1f, (wz - (zz * 64f - 32f)) * (1f / 64f));
            return Color32.Lerp(Color32.Lerp(s_c0, s_c1, ix), Color32.Lerp(s_c2, s_c3, ix), iz);
        }
    }

    // BED PAINT, STRUCTURAL. The wet mask blends into the heightmap's paint
    // MASK at rebuild time, on every client, as a pure function of world
    // position. A painted zone can no longer meet an unpainted zone because
    // painted zones no longer EXIST — there is only the field, evaluated
    // wherever and whenever a heightmap builds. This replaces the
    // TerrainComp paint path whose per-zone timing produced hard border
        // rectangles across several test passes (a wet mask has to
        // blend). Player paint still wins: cultivated/paved verts are
    // never overridden, and dirt only ever deepens.
    // Where an edit's seconds go (hoe and Infinity Hammer edits took seconds to show, with FGN's server
    // sim on). Vanilla sends every terrain op to the TerrainComp's ZDO OWNER (RPC_ApplyOperation); only after the owner
    // applies it and its ZDO data comes back does any heightmap here change. So the time from the op placed on this peer
    // to its heightmap changing here is the owner round trip (the network and server-sim side), logged at Debug with who
    // owns the TerrainComp; the voxel rebuild after it is FAT's own ("terraform: zone … visible N ms after …").
    [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyOperation))]
    internal static class Patch_TerrainComp_ApplyOperation_Timing
    {
        private struct Placed { public float At; public string Owner; }
        private static readonly Dictionary<Vector2s, Placed> s_placed = new Dictionary<Vector2s, Placed>();

        [HarmonyPrefix]
        private static void Prefix(TerrainComp __instance, TerrainOp modifier)
        {
            try
            {
                if (__instance == null || modifier == null) return;
                var nview = __instance.GetComponent<ZNetView>();
                var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                long owner = zdo != null ? zdo.GetOwner() : 0L;
                long me = ZDOMan.GetSessionID();
                long server = ZNet.instance == null ? 0L : ZNet.instance.IsServer() ? me : ZNet.instance.GetServerPeer()?.m_uid ?? 0L;
                string who = owner == me ? "this peer" : owner != 0L && owner == server ? "the server" : $"peer {owner}";
                var zone = ZoneSystem.GetZone(modifier.transform.position);
                FatNativeTrace.Step("terrain op", 0, "zone " + zone.x + "," + zone.y + " owner " + who + " at " + modifier.transform.position);
                if (!s_placed.ContainsKey(zone)) s_placed[zone] = new Placed { At = Time.realtimeSinceStartup, Owner = who };
            }
            catch { }
        }

        // From the Regenerate hook: the op placed here for this zone, if any, and how long its heightmap took to change.
        internal static void OnRegenerated(Vector2s zone)
        {
            if (!s_placed.TryGetValue(zone, out var p)) return;
            s_placed.Remove(zone);
            float ms = (Time.realtimeSinceStartup - p.At) * 1000f;
            if (ms > 30000f) return;
            if (FatDiag.Terraform) FiresLogger.LogDebug($"[FiresVoxel] terraform: zone ({zone.x},{zone.y}) heightmap changed {ms:0} ms after the op was placed here " +
                                 $"(TerrainComp owner: {p.Owner}; the op goes to the owner and back before any peer changes)");
        }
    }

    [HarmonyPatch(typeof(Heightmap), "ApplyModifiers")]
    internal static class Patch_Heightmap_WetMask
    {
        private static readonly AccessTools.FieldRef<Heightmap, Texture2D> s_hmPaint =
            AccessTools.FieldRefAccess<Heightmap, Texture2D>("m_paintMask");
        private static readonly AccessTools.FieldRef<Heightmap, int> s_hmWidth =
            AccessTools.FieldRefAccess<Heightmap, int>("m_width");
        private static readonly AccessTools.FieldRef<Heightmap, bool> s_hmDistant =
            AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");

        // Heightmaps whose CURRENT mask carries the blend. A rebuild rewrites
        // the mask, so the postfix re-stamps (and re-adds); the Sweep catches
        // heightmaps built BEFORE the water field was ready — the zone-border
        // seam makers — exactly once, by DIRECT texture write: no Regenerate,
        // no pokes, no rebuild churn. Right the first time.
        private static readonly HashSet<Heightmap> s_painted = new HashSet<Heightmap>();
        // 0.2.1163: the cached wet band per heightmap (cell indices, wet value, Ashlands flag); dropped by Restamp / the sweep.
        // 0.2.1185: built row by row under BandBuildBudgetMs per call (super flat's ~800 m ground heightmap is 641,601 cells; its
        // one-shot build was the ~7 ms-and-up Patch_Heightmap_WetMask.Postfix calls in the stall ledger). Until NextRow == W the
        // stamp writes the rows built so far; the rest follow on the next ApplyModifiers / Sweep stamp.
        private sealed class WetBand { public Vector3 Pos; public int W; public int NextRow; public int NextCol, RowsIdx; /* 0.2.1319: column resume inside NextRow; Idx count at the last whole row (only whole rows are stamped) */ public readonly List<int> Idx = new List<int>(); public readonly List<float> Wet = new List<float>(); public readonly List<bool> Ash = new List<bool>(); }
        private const double BandBuildBudgetMs = 2.0;
        private const int WetColStep = 16;   // 0.2.1319: cells between time checks inside a row
        private static bool s_wetRowProof;
        private static long s_bandRowsCall, s_bandCalls; private static readonly FatSpreadStat s_wetSpread = new FatSpreadStat("wet-mask band build");   // 0.2.1319
        private struct DryCache { public Vector3 Pos; public bool Dry; public float Until; }
        private static readonly Dictionary<Heightmap, DryCache> s_dry = new Dictionary<Heightmap, DryCache>();
        private const float DryCacheSeconds = 10f;
        private static long s_dryCacheHits;
        private static long s_bandPartial; private static double s_bandWorstMs;
        private static readonly Dictionary<Heightmap, WetBand> s_band = new Dictionary<Heightmap, WetBand>();
        private static long s_bandBuilds, s_bandStamps; private static double s_bandMs, s_bandStampMs; private static float s_bandLogAt;
        private static void NoteBandStats()
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_bandLogAt) return;
            s_bandLogAt = Time.unscaledTime + 10f;
            FiresLogger.LogDebug($"[FiresWetMask] 10 s: band builds {s_bandBuilds} ({s_bandMs:0.0} ms), band stamps {s_bandStamps} ({s_bandStampMs:0.0} ms), band build worst {s_bandWorstMs:0.00} ms, partial builds (continued next call) {s_bandPartial}, rows per call {(s_bandCalls > 0 ? (double)s_bandRowsCall / s_bandCalls : 0.0):0.0} over {s_bandCalls} call(s) (0.2.1319), dry answers cached {s_dryCacheHits}, dry skips {s_drySkips}, cached {s_band.Count} heightmap(s) (0.2.1163: was a full 65x65 scan + GetPixels per ApplyModifiers)");
            s_bandBuilds = s_bandStamps = s_bandPartial = s_dryCacheHits = s_bandRowsCall = s_bandCalls = 0; s_bandMs = s_bandStampMs = s_bandWorstMs = 0.0;
        }
        private static readonly Dictionary<Heightmap, int> s_lastBand = new Dictionary<Heightmap, int>();
        internal static long s_sameBandSkips;
        // 0.2.1347 (1345 log: Stamp 21.8% of resets, x24 / 5 s at one heightmap): since 1163/1319 a band builds over several sweeps and each partial stamp wrote new rows, so its hash
        // differed and it re-rolled grass, up to once per sweep for one band. Grass now owes one roll from the first write and pays when the band is complete, if the band's
        // content (cells + wet values, not what the mask happened to hold) differs from the band last rolled. A dry or empty end still pays what is owed.
        private static readonly HashSet<Heightmap> s_grassOwed = new HashSet<Heightmap>();
        private static readonly Dictionary<Heightmap, int> s_grassBand = new Dictionary<Heightmap, int>();
        internal static int GrassPartialHeld, GrassSameBand, GrassRolled;
        private static bool s_grassBandProof;
        private static bool GrassBandOn => FiresVoxelConfig.ClutterWetBandComplete != null && FiresVoxelConfig.ClutterWetBandComplete.Value;
        private static void RollGrass(Vector3 p0, float half)
        {
            GrassRolled++;
            if (ClutterSystem.instance == null) return;
            try { ClutterSystem.instance.ResetGrass(p0, half + 4f); }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresWetMask] ResetGrass threw: {ex.Message}"); }
        }
        // The band left nothing to compare (dry now, or complete with no cell): a write made while it was partial still gets its roll.
        private static void PayOwedGrass(Heightmap hm, Vector3 p0, float half)
        {
            if (!s_grassOwed.Remove(hm)) return;
            s_grassBand.Remove(hm);
            RollGrass(p0, half);
        }
        private static void GrassAfterStamp(Heightmap hm, WetBand band, int w, Vector3 p0, float half, bool wrote)
        {
            if (wrote) s_grassOwed.Add(hm);
            if (band.NextRow < w) { if (wrote) GrassPartialHeld++; return; }
            if (!s_grassOwed.Remove(hm)) return;
            int key = 17;
            unchecked { for (int k = 0; k < band.RowsIdx; k++) { key = key * 31 + band.Idx[k]; key = key * 31 + (int)(band.Wet[k] * 1024f); } }
            if (s_grassBand.TryGetValue(hm, out int was) && was == key) { GrassSameBand++; return; }
            s_grassBand[hm] = key;
            if (!s_grassBandProof) { s_grassBandProof = true; FiresLogger.LogInfo("[FiresWetMask] a wet band re-rolls its grass once, when it is complete and changed (0.2.1347)"); }
            RollGrass(p0, half);
        }
        internal static long s_drySkips;

        // True when no zone overlapping [x0, x0+span] x [z0, z0+span], plus one zone all round, carries ZW_PAINT. Only on a generator
        // world with no zone-mode gate (lifted seas paint a band of their own that the baked flags do not hold); else false (do the work).
        internal static bool IsDryForPaint(float x0, float z0, float span)
        {
            if (!FiresWorldGenBase.Active || Zones.ZoneModeGate.Active) return false;
            int zMinX = Mathf.FloorToInt((x0 + 32f) / 64f) - 1, zMaxX = Mathf.FloorToInt((x0 + span + 32f) / 64f) + 1;
            int zMinZ = Mathf.FloorToInt((z0 + 32f) / 64f) - 1, zMaxZ = Mathf.FloorToInt((z0 + span + 32f) / 64f) + 1;
            for (int zz = zMinZ; zz <= zMaxZ; zz++)
                for (int zx = zMinX; zx <= zMaxX; zx++)
                {
                    if ((FiresWorldGenBase.ZoneWaterFlags(zx, zz) & FiresWorldGenBase.ZW_PAINT) != 0) return false;
                    // 0.2.1071 (Fire: "were not painting the correct paint"): the flags only know GENERATED water, so a zone with
                    // only player-placed water (every river/pond tool carve; all of them on super flat, where rivers & lakes are
                    // off) counted as dry and the slice skipped PlacedWet01's sand bed + shore band entirely.
                    var at = new Vector3(zx * 64f, 0f, zz * 64f);
                    if (VerdantsAscent.Water.FatWaterLevelGate.CombinersAt(at).Count > 0 || VerdantsAscent.Water.FatWaterLevelGate.LegacyAt(at).Count > 0)
                    {
                        NotePlacedWetSlice(zx, zz);
                        return false;
                    }
                }
            return true;
        }

        private static float s_nextPlacedWetLog;
        private static void NotePlacedWetSlice(int zx, int zz)
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_nextPlacedWetLog) return;
            s_nextPlacedWetLog = Time.unscaledTime + 30f;
            FiresLogger.LogDebug($"[FiresWater] sand/wet paint: zone ({zx},{zz}) has placed water and no generated water: its slices take the placed-water bed + shore band (was skipped as 'dry' before 0.2.1071)");
        }
        private static float s_nextMemoLog;
        private const float SweepBudgetMs = 4f;
        private static readonly System.Diagnostics.Stopwatch s_sweepSw = new System.Diagnostics.Stopwatch();
        private static readonly List<Heightmap> s_deadBand = new List<Heightmap>();
        private static float s_nextSweep;
        private static bool s_active;
        private static int s_stampCells;   // 0.2.1080: real count of mask cells written by a stamp (the river log used estimates)
        private static float s_nextStampLog;

        private static void Postfix(Heightmap __instance)
        {
            if (!GenWaterField.Ready) return;
            long t0 = Zones.ZoneUpdateTimes.Now();
            Stamp(__instance);
            Zones.ZoneUpdateTimes.AddIfInside(Zones.ZoneUpdateTimes.Cause.HmWetMask, t0);   // 0.2.1089 [ZoneUpdate]
        }

        // 0.2.1073 (Fire on 1072: "we dont have the sand or the wet band or anything here"): a heightmap is stamped ONCE
        // (s_painted), and a river carve's paint reset rebuilt (= stamped) every heightmap BEFORE its water existed, so their
        // masks never got the placed-water bed + shore band; NotePlacedWater only re-dirtied the voxel copies of that unwet mask.
        // A new placed water now forgets the stamps of the heightmaps it reaches, so the next Sweep (0.5 s) re-stamps them with
        // the water present; Stamp then re-dirties the voxel columns and re-rolls their grass (now under/at the water).
        internal static int Restamp(Vector3 centre, float reach)
        {
            int n = 0;
            var all = Heightmap.GetAllHeightmaps();
            for (int i = 0; i < all.Count; i++)
            {
                var hm = all[i];
                if (hm == null) continue;
                Vector3 p = hm.transform.position;
                if (Mathf.Abs(p.x - centre.x) > reach + 33f || Mathf.Abs(p.z - centre.z) > reach + 33f) continue;
                bool had = s_painted.Remove(hm);
                s_lastBand.Remove(hm);
                s_grassBand.Remove(hm);   // 0.2.1347
                s_band.Remove(hm);   // 0.2.1163: the placed water changed here - the cached band is re-evaluated
                s_dry.Remove(hm);   // 0.2.1185
                if (had) n++;
            }
            if (n > 0) s_nextSweep = 0f;
            return n;
        }

        // 0.2.1348: every heightmap in reach has its whole band stamped (Stamp takes a heightmap off s_painted while its band is partial).
        internal static bool StampsDone(Vector3 centre, float reach)
        {
            var all = Heightmap.GetAllHeightmaps();
            for (int i = 0; i < all.Count; i++)
            {
                var hm = all[i];
                if (hm == null) continue;
                Vector3 p = hm.transform.position;
                if (Mathf.Abs(p.x - centre.x) > reach + 33f || Mathf.Abs(p.z - centre.z) > reach + 33f) continue;
                if (!s_painted.Contains(hm)) return false;
            }
            return true;
        }

        internal static void Sweep()
        {
            if (!GenWaterField.Ready)
            {
                // World left / regen: forget every memoized heightmap so the next
                // world's builds stamp fresh (stale entries = permanent holes).
                if (s_painted.Count > 0) s_painted.Clear();
                if (s_lastBand.Count > 0) s_lastBand.Clear();
                if (s_band.Count > 0) s_band.Clear();   // 0.2.1163
                if (s_dry.Count > 0) s_dry.Clear();   // 0.2.1185
                VoxelZoneStreamer.ClearWetMemo();
                return;
            }
            if (Time.time < s_nextSweep) return;
            s_nextSweep = Time.time + 0.5f;
            if (Time.realtimeSinceStartup >= s_nextMemoLog)   // memo stats line (once a minute)
            {
                s_nextMemoLog = Time.realtimeSinceStartup + 60f;
                FiresLogger.LogDebug($"[FiresWetMask] 0.2.985 PaintWet01 memo: {VoxelZoneStreamer.WetMemoHits} hit(s), {VoxelZoneStreamer.WetMemoMisses} computed, {VoxelZoneStreamer.WetMemoClears} clear(s); {s_sameBandSkips} stamp(s) with an unchanged band skipped the voxel re-dirty + grass reset; 0.2.987: view destroys queued {VoxelWorld.ViewDestroysQueued}, paint slices {VoxelWorld.PaintSliceColumns} column(s) over {VoxelWorld.PaintSliceFrames} frame(s); 0.2.989: {s_drySkips} dry heightmap(s) skipped");
            }
            var all = Heightmap.GetAllHeightmaps();
            // At most SweepBudgetMs of first stamps per sweep (every un-stamped map in one frame meant dozens of
            // ~50 ms stamps after a load or teleport). The rest are picked up by the next sweep, 0.5 s later; at least one per sweep.
            s_sweepSw.Restart();
            int stamped = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var hm = all[i];
                if (hm == null || s_painted.Contains(hm)) continue;
                if (stamped > 0 && s_sweepSw.Elapsed.TotalMilliseconds >= SweepBudgetMs) { s_nextSweep = Time.time + 0.1f; break; }
                Stamp(hm);
                stamped++;
            }
            s_painted.RemoveWhere(h => h == null);
            s_grassOwed.RemoveWhere(h => h == null);   // 0.2.1347
            if (s_grassBand.Count > 0) { s_deadBand.Clear(); foreach (var k in s_grassBand.Keys) if (k == null) s_deadBand.Add(k); foreach (var k in s_deadBand) s_grassBand.Remove(k); }
            if (s_lastBand.Count > 0) { s_deadBand.Clear(); foreach (var k in s_lastBand.Keys) if (k == null) s_deadBand.Add(k); foreach (var k in s_deadBand) s_lastBand.Remove(k); }
            if (s_band.Count > 0) { s_deadBand.Clear(); foreach (var k in s_band.Keys) if (k == null) s_deadBand.Add(k); foreach (var k in s_deadBand) s_band.Remove(k); }   // 0.2.1163
            if (s_dry.Count > 0) { s_deadBand.Clear(); foreach (var k in s_dry.Keys) if (k == null) s_deadBand.Add(k); foreach (var k in s_deadBand) s_dry.Remove(k); }   // 0.2.1185
        }

        internal static void Stamp(Heightmap hm)
        {
            if (hm == null || s_hmDistant(hm)) return;
            // NEVER memoize before the water field is actually queryable: a heightmap
            // built during the load window used to be marked "painted" while the flag
            // table read 0 — a PERMANENT unpainted 64m rectangle mid-river (the hard
            // paint blocks in the field reports). Not-ready = try again next Sweep.
            // 0.2.1080 (Fire: "STILL not painting the carves"): a super-flat Fires world has no generated water surface (SurfF stays
            // null: hydrology off), so SurfaceReady never turned true and NOTHING was ever stamped: the session log has 0
            // "[FiresWetMask] ACTIVE" lines and "[FiresMinimapRivers] stamp waiting: water surface not built". There is no
            // generated band to wait for there; the placed-water band (PlacedWet01) is all it paints.
            if (!GenWaterField.SurfaceReady && !FatSuperFlat.On) return;
            var tex = s_hmPaint(hm);
            if (tex == null) return;
            s_painted.Add(hm);
            if (!s_active)
            {
                s_active = true;
                // One-shot build marker: this line in a log PROVES the
                // stamp-once mask paint is what the session is running.
                FiresLogger.LogInfo("[FiresWetMask] ACTIVE — wet paint stamps once per heightmap (rebuilds re-blend).");
            }
            // NO zone-flag gate: EVERY heightmap evaluates the full band,
            // always. The world-continuous PaintWet01 gives identical values
            // for the shared border vertices of adjacent maps — that is the
            // whole seamless guarantee (the vanilla model: every overlapping
            // map carries the blend). Any gate deciding who participates is
            // a seam factory: one misaligned flag and a border map sits out
            // a band its neighbour painted (razor-straight
            // paint edges at zone lines). PaintWet01 bails at dist ≥ 12m, so
            // dry maps cost 65×65 cheap distance lookups — nothing.
            Vector3 p0 = hm.transform.position;
            int w = s_hmWidth(hm) + 1;
            // A heightmap with no shore paint anywhere within one zone of it is skipped before any work. The
            // rule is DistantPaintCache's: the BAKED zone flags (ZW_PAINT) + a one-zone border, since the band reaches ~12 m past wet cells.
            // Only on a generator world with no zone-mode gate: a lifted zone's own sea (ZoneModeLiftShore) is not in the baked flags.
            // 0.2.1185: the dry answer is cached per heightmap and position for DryCacheSeconds (Restamp drops it when placed water
            // changes): it walked every zone in reach with two placed-water index lookups each, on every ApplyModifiers.
            bool dry;
            if (s_dry.TryGetValue(hm, out var dc) && dc.Pos == p0 && Time.unscaledTime < dc.Until) { dry = dc.Dry; s_dryCacheHits++; }
            else { dry = IsDryForPaint(p0.x - (w - 1) * 0.5f, p0.z - (w - 1) * 0.5f, w - 1); s_dry[hm] = new DryCache { Pos = p0, Dry = dry, Until = Time.unscaledTime + DryCacheSeconds }; }
            if (dry) { s_drySkips++; PayOwedGrass(hm, p0, (w - 1) * 0.5f); return; }   // 0.2.1347
            // 0.2.1163 (FDT F8 report: Patch_Heightmap_WetMask.Postfix 8,947 ms in stall frames): this ran on EVERY
            // ApplyModifiers - every terrain op, trail stamps included - as a 65x65 GetPixels (a 67 KB allocation) plus 4,225
            // PaintWet01 + biome lookups. The wet band of a heightmap only changes with the water (Restamp drops it), so it is
            // evaluated once per heightmap and position; a dry heightmap returns here, a wet one touches only its band cells.
            float half = (w - 1) * 0.5f;
            if (!s_band.TryGetValue(hm, out var band) || band.Pos != p0 || band.W != w)
            {
                band = new WetBand { Pos = p0, W = w };
                s_band[hm] = band;
                s_bandBuilds++;
            }
            if (band.NextRow < w)
            {
                long tb = System.Diagnostics.Stopwatch.GetTimestamp();
                long limit = tb + (long)(System.Math.Max(0.05, System.Math.Min(BandBuildBudgetMs, FatBudget.LeftMs())) * System.Diagnostics.Stopwatch.Frequency / 1000.0);   // 0.2.1188: inside FatBudget
                // 0.2.1319 (PLAN_fat_frame_spread item 4: FatBudget worst unit 10.1 ms WetMask): key "Wet mask band time limit inside a
                // row" also checks the limit every WetColStep cells inside a row (resume at NextCol), at least WetColStep cells a call;
                // the stamp below writes only the WHOLE rows built so far (RowsIdx), as before. Off = the old whole-row step.
                bool inRow = FiresVoxelConfig.WetMaskRowLimit != null && FiresVoxelConfig.WetMaskRowLimit.Value;
                if (!s_wetRowProof) { s_wetRowProof = true; FiresLogger.LogInfo($"[FiresWetMask] band build time limit inside a row: {(inRow ? "ON" : "OFF (A/B)")}, whole rows stamped (0.2.1319)"); }
                int y = band.NextRow, x0 = inRow ? band.NextCol : 0, cells = 0, rowsThis = 0;
                if (!inRow && band.NextCol > 0)
                {
                    // the key was turned off mid-row: drop that row's partial cells and redo it whole
                    band.Idx.RemoveRange(band.RowsIdx, band.Idx.Count - band.RowsIdx); band.Wet.RemoveRange(band.RowsIdx, band.Wet.Count - band.RowsIdx); band.Ash.RemoveRange(band.RowsIdx, band.Ash.Count - band.RowsIdx);
                    band.NextCol = 0;
                }
                bool stopped = false;
                for (; y < w; y++)
                {
                    if (y > band.NextRow && x0 == 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= limit) break;   // at least one row per call
                    float wz = p0.z + (y - half);
                    for (int x = x0; x < w; x++)
                    {
                        if (inRow && cells >= WetColStep && (cells % WetColStep) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= limit) { band.NextCol = x; stopped = true; break; }
                        cells++;
                        float wet = VoxelZoneStreamer.PaintWet01(p0.x + (x - half), wz);
                        if (wet <= 0.03f) continue;
                        band.Idx.Add(y * w + x); band.Wet.Add(wet);
                        // ASHLANDS: mask ALPHA is LAVA (vanilla GetVegetationMask) - wet paint must NEVER write it
                        band.Ash.Add(FiresWorldGenBase.BiomeCodeAt(p0.x + (x - half), wz) == 6);
                    }
                    if (stopped) break;
                    x0 = 0; band.NextCol = 0; band.RowsIdx = band.Idx.Count; rowsThis++;
                }
                band.NextRow = y;
                if (y < w) { s_bandPartial++; s_nextSweep = 0f; }   // unfinished: the next Sweep keeps building it
                double bms = (System.Diagnostics.Stopwatch.GetTimestamp() - tb) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                s_bandMs += bms; if (bms > s_bandWorstMs) s_bandWorstMs = bms; FatBudget.Spend(FatBudget.Who.WetMask, bms);
                s_bandRowsCall += rowsThis; s_bandCalls++; s_wetSpread.Note(bms);
                if (y < w) s_painted.Remove(hm);   // so Sweep revisits it until the band is complete
            }
            if (band.RowsIdx == 0) { s_drySkips++; NoteBandStats(); if (band.NextRow >= w) PayOwedGrass(hm, p0, half); return; }   // 0.2.1319: whole rows only (= Idx.Count when no row is half built)
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            bool changed = false; s_stampCells = 0;
            int bandHash = 17;   // hash of the stamped band (index + final r/a); same as last stamp = no voxel re-dirty, no grass reset
            for (int k = 0; k < band.RowsIdx; k++)   // 0.2.1319: whole rows only
            {
                int i = band.Idx[k], x = i % w, y = i / w;
                float wet = band.Wet[k];
                var c = tex.GetPixel(x, y);
                if (c.g > 0.25f || c.b > 0.25f) continue;   // cultivated/paved player paint wins
                float r2 = Mathf.Max(c.r, wet);
                float a2 = band.Ash[k] ? c.a : Mathf.Lerp(c.a, wet, wet);
                if (r2 - c.r > 0.005f || Mathf.Abs(a2 - c.a) > 0.005f)
                {
                    tex.SetPixel(x, y, new Color(r2, c.g, c.b, a2));
                    changed = true;
                    s_stampCells++;
                    unchecked { bandHash = bandHash * 31 + i; bandHash = bandHash * 31 + (int)(r2 * 1024f); bandHash = bandHash * 31 + (int)(a2 * 1024f); }
                }
            }
            s_bandStamps++; s_bandStampMs += (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            NoteBandStats();
            if (changed)
            {
                if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_nextStampLog)
                {
                    s_nextStampLog = Time.unscaledTime + 5f;
                    FiresLogger.LogDebug($"[FiresWetMask] placed-water band: heightmap at ({p0.x:F0},{p0.z:F0}) wrote {s_stampCells} mask cell(s) (sand r + wet a) this stamp; the voxel columns re-copy it and the grass re-rolls");
                }
                s_stampCells = 0;
                if (FatNativeTrace.On) FatNativeTrace.Step("wetmask write", tex.GetInstanceID(), hm.transform.position + " " + tex.width + "x" + tex.height + " band " + band.Idx.Count);
                tex.Apply();   // always: vanilla just rewrote the mask, so the band must go back on
                if (FatNativeTrace.On) FatNativeTrace.Step("wetmask done", tex.GetInstanceID());
                // The voxel column copies and the grass already match a band identical to this heightmap's last stamp
                // (the stamp runs inside the same ApplyModifiers call that rewrote the mask, so nothing composed in between).
                if (s_lastBand.TryGetValue(hm, out int lastHash) && lastHash == bandHash) { s_sameBandSkips++; if (GrassBandOn) GrassAfterStamp(hm, band, w, p0, half, false); return; }
                s_lastBand[hm] = bandHash;
                // The VOXEL terrain renders paint from per-column COPIES of
                // this mask, composed when the streamer refreshes them —
                // which usually happens BEFORE this stamp runs. Nothing told
                // those copies the mask changed, so near-field shore sand
                // vanished entirely while the far quilt (which evaluates
                // PaintWet01 directly) still showed it.
                // Dirty every 32m column this heightmap overlaps; the
                // streamer's paint drain re-composes and rebinds them.
                int cx0 = Mathf.FloorToInt((p0.x - 32f) / 32f), cx1 = Mathf.FloorToInt((p0.x + 32f) / 32f);
                int cz0 = Mathf.FloorToInt((p0.z - 32f) / 32f), cz1 = Mathf.FloorToInt((p0.z + 32f) / 32f);
                for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                        VoxelWorld.MarkPaintDirty(new Vector2Int(cx, cz));

                // AND RE-ROLL THE CLUTTER. Writing the mask is not enough:
                // grass patches already generated keep rendering through the
                // new paint until something marks them for rebuild, which is
                // why a lake bed came out as water over untouched meadow
                // grass. ResetGrass re-evaluates Heightmap.IsCleared (mask
                // r/g/b > 0.5 — the sand we just wrote) for every patch in
                // radius, so grass disappears under the water and the water
                // clutter rolls in its place.
                if (GrassBandOn) GrassAfterStamp(hm, band, w, p0, half, true);   // 0.2.1347
                else if (ClutterSystem.instance != null)
                {
                    try { ClutterSystem.instance.ResetGrass(p0, half + 4f); }
                    catch (System.Exception ex)
                    { FiresLogger.LogWarning($"[FiresWetMask] ResetGrass threw: {ex.Message}"); }
                }
            }
            else if (GrassBandOn) GrassAfterStamp(hm, band, w, p0, half, false);   // 0.2.1347: the band's last rows wrote nothing: what the earlier rows owe is paid now
        }
    }

    /// <summary>
    /// FAT's public voxel queries for other mods (FDT's bot, test drills). Additive; changes nothing by being called.
    /// </summary>
    public static class FiresVoxelApi
    {
        /// <summary>
        /// Whether the ground at <paramref name="point"/> shows its current terrain on this peer: true where it is vanilla
        /// terrain (not voxel-wrapped), and on voxel ground once its zone's rewrap has finished and the new chunks are
        /// collidable. A terrain op (hoe, pickaxe, TerrainComp) reaches the heightmap a frame or more after it is placed
        /// (longer when another peer owns the TerrainComp), and only then marks the zone for its rewrap, which waits at
        /// least 2 s between runs. So wait about 0.5 s after the last op before polling this, then re-measure once it is
        /// true. Main thread only.
        /// </summary>
        public static bool IsGroundSettled(Vector3 point) => VoxelZoneStreamer.IsGroundSettled(point);
    }

    // ZONE LOOK. Fire 10-23 (shots 210/211): a voxel-swapped zone drew as a whole PALER rectangle with a hard edge at the zone
    // border, while its biome vertex colours matched (the "wrap colour" line). The voxel chunks draw with the captured TEMPLATE
    // terrain material (TerrainMaterialCache); a vanilla tile draws with its own INSTANCE of it, which vanilla and other mods
    // set values on per tile. At the swap (both meshes exist) this logs, once per zone (max LookLines a session): the two
    // shaders + keywords, every float / colour / vector the instance holds differently from the voxel material, and the
    // vertex colour / normal / UV at the zone centre on both meshes. Every such float / colour / vector is then copied into
    // the zone's voxel renderers' property blocks, so the zone draws with the vanilla tile's values. Textures are not copied
    // (the paint mask is the voxel column's own).
    internal static class VoxelZoneLook
    {
        private const int LookLines = 40;
        private static int s_lines;
        private static System.Reflection.FieldInfo s_instanceField;

        private struct Prop { public int Id; public PropKind Type; public float F; public Vector4 V; }
        private static readonly Dictionary<Vector2s, List<Prop>> s_zoneProps = new Dictionary<Vector2s, List<Prop>>();

        private enum PropKind { Float, Color, Vector }

        // 0.2.1093: chunkPos is the chunk's own origin (VoxelChunkView.PushBlock, from its Key); until 1093 every view passed its
        // transform.position, (0,0,0) for every chunk, so all chunks read zone (0,0)'s values (usually none: nothing was copied).
        private static readonly HashSet<Vector2s> s_saidZone = new HashSet<Vector2s>();
        private static float s_sayAt;

        internal static void ApplyTo(MaterialPropertyBlock mpb, Vector3 chunkPos, Vector3Int key)
        {
            var zone = ZoneSystem.GetZone(chunkPos + new Vector3(16f, 0f, 16f));   // a chunk's centre picks its zone (32 m chunks, 64 m zones)
            if (s_zoneProps.Count == 0 || !s_zoneProps.TryGetValue(zone, out var props))
            {
                Say(zone, key, "none: no vanilla tile values differ (or the zone has not swapped yet)");
                return;
            }
            var sb = FiresLogger.DebugEnabled ? new System.Text.StringBuilder() : null;
            foreach (var p in props)
            {
                if (p.Type == PropKind.Float) mpb.SetFloat(p.Id, p.F);
                else if (p.Type == PropKind.Color) mpb.SetColor(p.Id, p.V);
                else mpb.SetVector(p.Id, p.V);
                sb?.Append(' ').Append(p.Id).Append('=').Append(p.Type == PropKind.Float ? p.F.ToString("0.###") : p.V.ToString("0.###"));
            }
            Say(zone, key, $"{props.Count} value(s):{sb}");
        }

        // [VoxelZoneLook] once per zone (and at most one line a second), Debug.
        private static void Say(Vector2s zone, Vector3Int key, string look)
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_sayAt || !s_saidZone.Add(zone)) return;
            s_sayAt = Time.unscaledTime + 1f;
            if (FatDiag.Looks) FiresLogger.LogDebug($"[VoxelZoneLook] chunk {key} zone ({zone.x},{zone.y}) look {look}");
        }

        internal static void AtSwap(Vector2s zone, Heightmap hm, List<Vector3Int> keys)
        {
            if (hm == null || keys == null) return;
            var hmRenderer = hm.GetComponent<MeshRenderer>();
            if (s_instanceField == null) s_instanceField = typeof(Heightmap).GetField("m_materialInstance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var van = (s_instanceField?.GetValue(hm) as Material) ?? hmRenderer?.sharedMaterial;
            VoxelChunkView view = null;
            foreach (var k in keys) if (VoxelWorld.TryGetView(k, out var v) && v != null && v.GroundRenderer != null && v.VertexCount > 0) { view = v; break; }
            var vox = view?.GroundRenderer.sharedMaterial;
            if (van == null || vox == null) return;

            var props = new List<Prop>();
            var sb = new System.Text.StringBuilder();
            var shader = van.shader;
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                int id = shader.GetPropertyNameId(i);
                if (!vox.HasProperty(id)) { sb.Append($" | {shader.GetPropertyName(i)}: voxel material lacks it"); continue; }
                switch (shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        {
                            float a = van.GetFloat(id), b = vox.GetFloat(id);
                            if (!Mathf.Approximately(a, b)) { props.Add(new Prop { Id = id, Type = PropKind.Float, F = a }); sb.Append($" | {shader.GetPropertyName(i)} {b:0.###} -> {a:0.###}"); }
                            break;
                        }
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        {
                            Color a = van.GetColor(id), b = vox.GetColor(id);
                            if (a != b) { props.Add(new Prop { Id = id, Type = PropKind.Color, V = a }); sb.Append($" | {shader.GetPropertyName(i)} {b} -> {a}"); }
                            break;
                        }
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        {
                            Vector4 a = van.GetVector(id), b = vox.GetVector(id);
                            if (a != b) { props.Add(new Prop { Id = id, Type = PropKind.Vector, V = a }); sb.Append($" | {shader.GetPropertyName(i)} {b} -> {a}"); }
                            break;
                        }
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        {
                            var a = van.GetTexture(id); var b = vox.GetTexture(id);
                            if (a != b && shader.GetPropertyName(i) != "_ClearedMaskTex") sb.Append($" | texture {shader.GetPropertyName(i)} voxel '{(b != null ? b.name : "none")}' vs vanilla '{(a != null ? a.name : "none")}' (not copied)");
                            if (van.GetTextureScale(id) != vox.GetTextureScale(id) || van.GetTextureOffset(id) != vox.GetTextureOffset(id))
                                sb.Append($" | {shader.GetPropertyName(i)} tiling voxel {vox.GetTextureScale(id)}+{vox.GetTextureOffset(id)} vs vanilla {van.GetTextureScale(id)}+{van.GetTextureOffset(id)}");
                            break;
                        }
                }
            }
            if (props.Count > 0) s_zoneProps[zone] = props; else s_zoneProps.Remove(zone);
            s_saidZone.Remove(zone);   // 0.2.1093: the zone's look line repeats with its new values
            if (props.Count > 0)
                foreach (var k in keys) if (VoxelWorld.TryGetView(k, out var v) && v != null) v.RefreshBlock();

            if (!FiresLogger.DebugEnabled || s_lines >= LookLines) return;
            s_lines++;
            string kwVan = string.Join(",", van.shaderKeywords), kwVox = string.Join(",", vox.shaderKeywords);
            string head = $"[FiresVoxel] zone look ({zone.x},{zone.y}): vanilla '{van.name}' shader '{van.shader.name}' [{kwVan}], voxel '{vox.name}' shader '{vox.shader.name}' [{kwVox}]"
                + (van.shader != vox.shader ? " SHADER DIFFERS" : "") + (kwVan != kwVox ? " KEYWORDS DIFFER" : "")
                + $"; {props.Count} value(s) copied to the voxel chunks; globals _Wet {Shader.GetGlobalFloat("_Wet"):0.00}";
            FiresLogger.LogDebug(head + (sb.Length > 0 ? sb.ToString() : " | no property differs") + " | " + MeshSample(hm, view));
        }

        // The vertex nearest the zone centre on each mesh: colour (biome), normal (lighting), UV.
        private static string MeshSample(Heightmap hm, VoxelChunkView view)
        {
            Vector3 centre = hm.transform.position;
            string Sample(Mesh mesh, Transform t)
            {
                if (mesh == null || !mesh.isReadable) return "no readable mesh";
                var verts = mesh.vertices; var cols = mesh.colors32; var nors = mesh.normals; var uvs = mesh.uv;
                int best = -1; float bd = float.MaxValue;
                for (int i = 0; i < verts.Length; i++) { var w = t.TransformPoint(verts[i]); float d = (w.x - centre.x) * (w.x - centre.x) + (w.z - centre.z) * (w.z - centre.z); if (d < bd) { bd = d; best = i; } }
                if (best < 0) return "empty mesh";
                var p = t.TransformPoint(verts[best]);
                string c = cols.Length > best ? $"{cols[best].r},{cols[best].g},{cols[best].b},{cols[best].a}" : "none";
                string nr = nors.Length > best ? t.TransformDirection(nors[best]).ToString("0.00") : "none";
                string uv = uvs.Length > best ? uvs[best].ToString("0.000") : "none";
                return $"at ({p.x:0.0},{p.y:0.0},{p.z:0.0}) colour {c} normal {nr} uv {uv}";
            }
            var hmMesh = hm.GetComponent<MeshFilter>()?.sharedMesh;
            if (hmMesh == null && s_renderMesh == null) s_renderMesh = typeof(Heightmap).GetField("m_renderMesh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (hmMesh == null) hmMesh = s_renderMesh?.GetValue(hm) as Mesh;
            return "vanilla vertex " + Sample(hmMesh, hm.transform) + "; voxel vertex " + Sample(view.AuditMesh, view.transform);
        }
        private static System.Reflection.FieldInfo s_renderMesh;

        internal static void Clear() => s_zoneProps.Clear();

        // ── fat_voxel_shader: A/B the voxel ground's shader live ───────────────────────────────────────────────────────
        // Fire 10-23: swapped zones draw PALER than vanilla on grass and dirt; the zone look line proved the only difference is
        // the shader (Custom/FiresVoxelHeightmap vs vanilla Custom/Heightmap: same material values, vertex colours, normals).
        // 'fat_voxel_shader vanilla' puts every voxel chunk on vanilla's Custom/Heightmap (through Core's shader override, so the
        // shared material keeps it), 'fat_voxel_shader fires' puts ours back. Look at a swapped zone's border with each: if
        // the edge goes with 'vanilla', the paleness is inside our shader; if it stays, it is outside the shader (mesh,
        // renderer or paint mask). Session only; nothing is saved. Each switch logs one line with the wetness globals.
        internal static Shader VoxelShader;

        [HarmonyLib.HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class ShaderCommand
        {
            private static bool s_registered;

            [HarmonyLib.HarmonyPostfix]
            private static void Register()
            {
                if (s_registered) return;
                s_registered = true;
                new Terminal.ConsoleCommand("fat_voxel_shader",
                    "[FiresAdminTerrain] the voxel ground's shader: 'fires' (Custom/FiresVoxelHeightmap) puts ours back; no argument = which one is on ('vanilla' was removed in 0.2.1083: it blanked voxel ground)",
                    args =>
                    {
                        string want = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "";
                        var mat = FiresCore.Materials.TerrainMaterialCache.GetCurrentMaterial();
                        Shader target = null;
                        FiresLogger.LogInfo($"[FiresVoxel] fat_voxel_shader '{want}' received");
                        // 0.2.1083 (Fire: 'vanilla' "makes the ground not render visually at all"): vanilla's Custom/Heightmap reads
                        // the vanilla heightmap's own vertex data (its UV/vertex-colour layout and per-heightmap textures), which a
                        // voxel chunk mesh doesn't carry, so every voxel chunk drew nothing. The option is gone; 'fires' stays.
                        if (want == "vanilla")
                        {
                            string gone = "[FAT] fat_voxel_shader: 'vanilla' is removed (vanilla's Custom/Heightmap can't draw a voxel chunk mesh: the ground went invisible). Use 'fires'.";
                            FiresLogger.LogInfo(gone); args.Context?.AddString(gone); return;
                        }
                        if (want == "fires") target = VoxelShader;
                        if (target != null)
                        {
                            FiresCore.Materials.TerrainMaterialCache.SetShaderOverride(target);
                            mat = FiresCore.Materials.TerrainMaterialCache.GetCurrentMaterial();   // applies it to the shared material now
                        }
                        else if (want != "") { string no = $"[FAT] fat_voxel_shader: no shader for '{want}' (vanilla {(VanillaShader() != null ? "found" : "MISSING: no vanilla heightmap loaded yet")}, fires {(VoxelShader != null ? "loaded" : "MISSING")}); use vanilla or fires"; FiresLogger.LogWarning(no); args.Context?.AddString(no); return; }
                        // Read back from the chunk renderers themselves: how many draw with the shared material, and what shader they
                        // draw with now. A renderer on another material is switched too (its own sharedMaterial's shader).
                        int onShared = 0, switched = 0, other = 0;
                        string sample = "no voxel chunk renderer loaded";
                        foreach (var view in Object.FindObjectsByType<VoxelChunkView>(FindObjectsSortMode.None))
                        {
                            var r = view != null ? view.GroundRenderer : null;
                            if (r == null || r.sharedMaterial == null) continue;
                            if (r.sharedMaterial == mat) onShared++;
                            else
                            {
                                other++;
                                if (target != null && r.sharedMaterial.shader != target) { r.sharedMaterial.shader = target; switched++; }
                            }
                            if (sample.StartsWith("no ")) sample = $"'{view.name}' draws with '{r.sharedMaterial.shader.name}'";
                        }
                        string line = $"[FiresVoxel] voxel shader now '{(mat != null && mat.shader != null ? mat.shader.name : "none")}' on material '{(mat != null ? mat.name : "none")}': "
                            + $"{onShared} chunk renderer(s) on it, {other} on another material ({switched} switched); read back: {sample} "
                            + $"(globals: _Wet {Shader.GetGlobalFloat("_Wet"):0.00}, _SkyAlphaPosition {Shader.GetGlobalVector("_SkyAlphaPosition")})";
                        FiresLogger.LogInfo(line);
                        args.Context?.AddString(line);
                    });
            }
        }

        // Vanilla's Custom/Heightmap is not findable by name (it lives on vanilla's terrain material, not in a Resources
        // path), so it is taken from a loaded vanilla tile's own material, and kept once seen.
        private static Shader s_vanillaShader;

        internal static Shader VanillaShader()
        {
            if (s_vanillaShader != null) return s_vanillaShader;
            foreach (var hm in Object.FindObjectsByType<Heightmap>(FindObjectsSortMode.None))
            {
                var s = hm != null && hm.m_material != null ? hm.m_material.shader : null;
                if (s != null && s != VoxelShader) { s_vanillaShader = s; break; }
            }
            if (s_vanillaShader == null) s_vanillaShader = Shader.Find("Custom/Heightmap");
            return s_vanillaShader;
        }
    }
}
