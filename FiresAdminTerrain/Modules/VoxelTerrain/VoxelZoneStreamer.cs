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
        private static GameObject s_driverGO;

        private static readonly AccessTools.FieldRef<Heightmap, bool> s_isDistantLod =
            AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");
        private static readonly AccessTools.FieldRef<Heightmap, MeshCollider> s_hmCollider =
            AccessTools.FieldRefAccess<Heightmap, MeshCollider>("m_collider");

        public static int WrappedZoneCount => s_zones.Count;

        // W3-A1 spawn gate: a zone is SOLID once its voxel colliders are live
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
            if (!Heightmap.GetHeight(new Vector3(wx + GroundProbeEpsilon, 0f, wz + GroundProbeEpsilon), out ground)
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

        // The spawn/teleport gate for every voxel world, not only VoxelOnly (Fire 10-03: on a converted voxel world the
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

        // 0.2.778 ([generator] / FDT 1.1.303 on ATomTestCloseChunks: after each TeleportTo the tester stood 4-8 m UNDER the surface
        // it was sent to): a swapped zone can still have chunks under the arrival point whose edit anchor's latest revision is not
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
            // The arrival's own layer ([lead] 10-06: a surface arrival must not wait on cave chunks far below it): from ArrivalUnder
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
                    foreach (var key in w.Keys)
                    {
                        if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk.Kind != ChunkKind.Full) continue;
                        full++;
                        if (chunk.PendingGenerate) pendingGen++;
                        else if (chunk.Queued) queued++;
                        else if (!VoxelWorld.TryGetView(key, out var view) || view == null) noView++;
                        else if (!view.ColliderReady) noCollider++;
                    }
                    sb.Append($"({z.x},{z.y}):gen={pendingGen} q={queued} noView={noView} noCol={noCollider}/{full} ");
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

        internal static void MarkZoneBrushed(Vector3Int chunkKey)
        {
            var zone = ZoneOfChunk(chunkKey);
            if (s_brushedZones.Add(zone) && FiresVoxelConfig.WrapsBrushedZones)
                FiresLogger.LogInfo($"[FiresVoxel] zone ({zone.x},{zone.y}) holds voxel edits → converting to voxel (Terrain Mode = {FiresVoxelConfig.Mode}).");
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
            if (FiresVoxelConfig.WrapsAllTerrain || !s_zones.ContainsKey(zone)) return;
            FiresLogger.LogInfo($"[FiresVoxel] zone ({zone.x},{zone.y}) has no voxel brush edits left → back to vanilla terrain.");
            Unwrap(zone);
        }

        // Should a NOT-yet-wrapped zone convert to voxel under the current mode?
        private static bool ShouldWrapZone(Vector2s zone, Vector3 zoneCenter)
        {
            if (FiresVoxelConfig.WrapsAllTerrain) return true;                 // FullVoxel
            if (s_brushedZones.Contains(zone)) return true;                    // voxel-brushed (ZoneReplace + PlacedOnly)
            // A channel or lake runs through it: convert this zone alone, not
            // the world (Tools/FAT_RIVER_CARVE_SPEC.md stage 5).
            if (FiresVoxelConfig.CarvesRiverBeds && GenWaterField.TouchesZone(zone)) return true;
            if (FiresVoxelConfig.WrapsIslandZones)                             // PlacedOnly: sky-island zones
            {
                float bx = zone.x * 64f - 32f, bz = zone.y * 64f - 32f;
                for (int cz = 0; cz < 64; cz += 32)
                    for (int cx = 0; cx < 64; cx += 32)
                        if (FiresSkyIslands.ChunkOverlaps(new Vector3(bx + cx, 0f, bz + cz))) return true;
            }
            return false;
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
            FiresLogger.LogInfo($"[FiresVoxel] zone streamer up (Terrain Mode={FiresVoxelConfig.Mode}, World Terrain Mode={FiresVoxelConfig.WorldKind}).");
        }

        private sealed class StreamerDriver : MonoBehaviour
        {
            private float _nextTick;

            private void FixedUpdate()
            {
                try { VoxelCavePopGuard.FixedTick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] pop guard threw: {ex.Message}"); }
            }

            private void Update()
            {
                // Retries until the scene's RenderGroupSystem exists; a no-op
                // once subscribed. Armed here rather than in the wrap path
                // because brush chunks render in Terrain Mode Off too.
                VoxelRenderGroup.Arm();

                // Impostors draw per frame (DrawMeshInstanced is a one-frame
                // submission) — outside the 0.25s streamer throttle.
                try { FarVegImpostors.Tick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] far impostors threw: {ex.Message}"); }
                try { VoxelCaveClutter.Tick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] cave clutter threw: {ex.Message}"); }

                // A terrain edit doesn't wait for the 0.25 s tick: its zone is rebuilt this frame (Fire 10-05: hoe, pickaxe and
                // Infinity Hammer edits took seconds to show).
                if (s_terraformKick)
                {
                    s_terraformKick = false;
                    try { DrainTerraform(true); }
                    catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] terraform drain threw: {ex}"); }
                }

                if (Time.time < _nextTick) return;
                _nextTick = Time.time + 0.25f;
                try { Tick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] streamer tick threw: {ex}"); }
            }

            private void OnDestroy()
            {
                VoxelRenderGroup.Disarm();
                s_zones.Clear();
                s_brushedZones.Clear();
                FatCracks.Forget();
                FiresLocationPads.Clear();
                HeightmapBuildCache.Clear();
                VoxelCaveClutter.Clear();
                s_driverGO = null;
            }
        }

        // ── Main tick ────────────────────────────────────────────────────────
        private static void Tick()
        {
            var player = Player.m_localPlayer;

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
                // W3-A3 — the dedi streams COLLIDERS around its peers. Zone
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
                // W3-A1: in VoxelOnly there is no vanilla ground, so the spawn
                // area must wrap BEFORE the player exists — center on the
                // network reference position (vanilla points it at the pending
                // spawn during the respawn wait). Never unwind while waiting.
                // 0.2.854 (the lead's spawn-wait breakdown: ~30 of the ~40 s spawn gate passed before the player object existed,
                // with this tick returning here on every non-VoxelOnly world, so no hydrology, no wraps, no cache until then):
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
            for (int i = s_waterQueue.Count - 1; i >= 0; i--)
            {
                if (Time.time < s_waterQueue[i].due) continue;
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
            Patch_Heightmap_WetMask.Sweep();
            Paint.DistantPaintCache.VanillaLodSweep();
            FiresFarWater.Tick(pp);
            if (!dedicated) GhostBurst(pp);
            else if (s_centers.Count > 0) GhostBurst(NextBurstCenter());

            // Unwrap: dead heightmaps (zone unloaded); also zones beyond the
            // governor radius + hysteresis (the far ring the governor shed).
            s_scratchDrop.Clear();
            float dropBound = GovernorBound(wrapAll ? WRAP_HARD_CAP : radius) + 48f;
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
            foreach (var z in s_scratchDrop) Unwrap(z);

            DrainTerraform(false);

            // W3.3 — paint-only ops need no remesh: drain a few mask-texture
            // refreshes per tick (1089-texel copy + rebind each).
            VoxelWorld.DrainPaintDirty(4);

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
                    if (w.VanillaColliderBack && col != null) col.enabled = true;
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
            // A VoxelWrap dedi held voxel DATA only until 0.2.652: its physics stood on vanilla ground (R77). Now it meshes colliders
            // like VoxelOnly unless 'Server builds voxel collision' is off.
            bool dataOnlyServer = dedicated && !FiresVoxelConfig.ServerCollides;
            bool startedOne = false;
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
                startedOne = StartWrap(zone, hm);
            }
            if (dedicated && ServerHoldsVoxelData)
                TickServerData(GovernorBound(wrapAll ? WRAP_HARD_CAP : radius), dataOnlyServer, startedOne);
            if (dedicated && !dataOnlyServer) ReportServerColliders();
            ReportBlocked();
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
        // sits around (1e6, 1e6), and the dedi meshed colliders for zones out there on every join (R78: "server colliders for
        // zone (15625,15625): 12 chunk(s)"). Neither a centre nor a wrap; one line per skipped zone.
        // 0.2.721: the world's own edge (FatWorldSize; 10500 on a vanilla-size world). A fixed 10500 skipped every real zone
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
            FiresLogger.LogInfo($"[FiresVoxel] server colliders: {zones} zone(s) wrapped around {s_centers.Count} peer(s), {swapped} swapped, "
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
        // DATA (never physics — round-87 lesson; +ε — round-89 border fix).
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
                    // is loaded, and that border stays flagged provisional
                    // (round-91 heal).
                    // Location pads (a placed location levels the ground it stands on) ride along in both paths.
                    if (!TryGroundHeight(wx, wz, out g))
                    {
                        g = GeneratedGround(wx, wz);
                        usedFallback = true;
                        if (FiresLocationPads.TrySample(wx, wz, g, out float padY)) g = padY;
                    }
                    int col = (x + 1) + VoxelChunk.SAMPLES * (z + 1);
                    ground[col] = g;
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

                    int kyLo = FloorDiv(Mathf.FloorToInt(minG) - Mathf.Max(caves ? caveDepth : 4, Mathf.CeilToInt(crackDepth) + 1), 32);
                    int kyHi = FloorDiv(Mathf.CeilToInt(maxG) + 2, 32);
                    if (isl)
                    {
                        int islHi = FloorDiv(Mathf.CeilToInt(islTop) + 2, 32);
                        if (islHi > kyHi) kyHi = islHi;
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
                        bool skylandBiome = inIslandBand && Zones.ZoneModeRegistry.ModeActive;
                        uint skylandColors = 0u;
                        var chunkBiome = skylandBiome ? SkylandBiome(kx, kz, cBot, ground, biome, out skylandColors) : biome;
                        if (skylandBiome) chunkHash = unchecked((chunkHash ^ 0x5C1Au ^ skylandColors) * 16777619u);

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
                        // 0.2.763 (Fire 10-06: skylands "reshaping on first arrival"): a skyland's chunks wait for the
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
            wrap.UsedFallback = usedFallback;
            ReportSlowBuild(wrap, buildStart, gatherTicks, columnsEnd, enqueueEnd, armed.Count);
        }

        // [perf] R32: the dedi's main thread spent 105-260 ms per new area in here. The split says which part to move to
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

        // ── Wrap: first-time build for a zone ────────────────────────────────
        private static bool StartWrap(Vector2s zone, Heightmap hm)
        {
            var wrap = new ZoneWrap { Zone = zone, Hm = hm, StartedAt = Time.time };
            BuildZoneChunks(wrap);
            s_zones[zone] = wrap;

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
            var gen = s_generatedZonesRef(zs);
            var zc = ZoneSystem.GetZone(center);
            int area = FiresCore.Bridge.SimulationDistanceAccess.ZoneLoadingNear + FiresCore.Bridge.SimulationDistanceAccess.Far;
            System.Diagnostics.Stopwatch sw = null;
            for (int dz = -area; dz <= area; dz++)
            {
                for (int dx = -area; dx <= area; dx++)
                {
                    var z = new Vector2s(zc.x + dx, zc.y + dz);
                    if (gen.Contains(z)) continue;
                    // Budget BEFORE spawning: zone spawns measured 112-159ms
                    // on mountain worlds — checking after meant every burst
                    // frame ate one anyway ON TOP of the budget. One
                    // expensive spawn per tick max; cheap ones still batch.
                    if (sw != null && sw.Elapsed.TotalMilliseconds >= 4.0) return;
                    if (sw == null) sw = System.Diagnostics.Stopwatch.StartNew();
                    s_spawnZoneArgs[0] = z;
                    s_spawnZoneArgs[1] = ZoneSystem.SpawnMode.Ghost;
                    s_spawnZoneArgs[2] = null;
                    try { s_spawnZoneMethod.Invoke(zs, s_spawnZoneArgs); }
                    catch (System.Exception ex)
                    {
                        FiresLogger.LogWarning($"[FiresVoxel] ghost burst threw for ({z.x},{z.y}): {ex.Message}");
                        return;
                    }
                }
            }
        }

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
        // level/class printed a ruler-straight paint line along them (user
        // screenshot at a river mouth: ocean-source side gated to 0, river
        // side full). The wet value is computed PER CORNER CELL and blended,
        // the sea suppression is a smooth fade in GROUND altitude, and the
        // beach gate is a smooth multiplier — every transition feathers.
        private const float PAINT_SHORE_REACH = 9f;   // legacy const (style table governs)
        internal static float PaintWet01(float pwx, float pwz)
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
        // saturation from a momentary spike: the 2026-09-26 traverse emitted pendingGen 0, 0, 0 and 17, which is
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
                    VoxelMeshPipeline.EnqueueRemesh(chunk);
                }
                else if (!view.ColliderReady) noCollider++;
            }
            if (pendingGen + queued + noView + noCollider > 0)
            {
                // Stuck-swap diagnostics. One line PER ZONE every 8s buried the log on a busy world (323 lines in a
                // 9.6 minute run, [perf] 2026-09-22), and every one of them says the same thing: the mesher is behind.
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

            foreach (var key in wrap.Keys)
            {
                if (VoxelWorld.TryGetChunk(key, out var chunk)) chunk.SuppressRenderUntilSwap = false;
                if (VoxelWorld.TryGetView(key, out var view) && view != null) view.SetRenderSuppressed(false);
            }
            HideVanilla(wrap.Hm);
            RecordHiddenIdentity(wrap);
            wrap.Swapped = true;
            s_swapAt[wrap.Zone] = Time.time;
            // 0.2.763: a skyland's far piece goes and its held dressing shows in this same frame.
            try { Zones.ZoneModeSkylands.OnZoneSwapped(wrap.Zone); }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] skyland swap hand-off threw for ({wrap.Zone.x},{wrap.Zone.y}): {ex.Message}"); }
            FatClutterRegrow.Zone(wrap.Zone);   // 0.2.690: grass rolled on vanilla's collider re-rolls on the voxel ground
            if (IsDedicatedServer)
            {
                s_serverSwaps++;
                s_serverSwapChunks += wrap.Keys.Count;
                FiresLogger.LogInfo($"[FiresVoxel] server colliders for zone ({wrap.Zone.x},{wrap.Zone.y}): {wrap.Keys.Count} chunk(s) meshed in "
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

            // W3.3 — paint that loaded from TerrainComp ZDOs before the zone
            // wrapped gets its masks on the freshly visible chunks.
            MarkZonePaintDirty(wrap.Zone);
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
        // describes) leaves a hole with no ground at all: [fgn] R89 saw Fire's own drops fall 30 and 270 m after a zone reload.
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
                col.enabled = true;
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
        private static void HideVanilla(Heightmap hm)
        {
            if (hm == null) return;
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
            var groupSubscriber = hm.GetComponent<RenderGroupSubscriber>();
            var rend = hm.GetComponent<MeshRenderer>();
            if (rend != null)
                rend.enabled = groupSubscriber == null || RenderGroupSystem.IsGroupActive(groupSubscriber.Group);
            if (groupSubscriber != null) groupSubscriber.enabled = true;
            var col = s_hmCollider(hm);
            if (col != null) col.enabled = true;
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
            if (wrap.Hm != null)
            {
                ShowVanilla(wrap.Hm);
                // Render/collision rebuilds are skipped while hidden — the
                // returning vanilla meshes are stale until this rebuild.
                wrap.Hm.Poke(0);
            }
            foreach (var key in wrap.Keys) VoxelWorld.DropChunkKey(key);
            // The receding frontier: surviving chunks in adjacent wrapped
            // zones still carry a "covered" frontier mask toward this zone —
            // no lip, no quads, and no heal ever fired (border mirroring
            // skips absent owners, so the sweep saw zero divergence). Queue
            // the zone's ring; dead keys self-discard, survivors remesh with
            // fresh masks and the lips return.
            QueuePriorityHeal(wrap);
            s_zones.Remove(zone);
            VoxelGenWaterMesh.DropZone(zone);
            // Let the zone be re-dressed if it comes back; the ZDO check keeps
            // that from doubling up on content that already persisted.
            VoxelCaveContent.ForgetZone(zone);
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
            if (editedAt >= 0f)
                FiresLogger.LogDebug($"[FiresVoxel] terraform: zone ({dz.x},{dz.y}) rebuild started {(start - editedAt) * 1000f:0} ms after its heightmap changed; " +
                                     $"built {(Time.realtimeSinceStartup - start) * 1000f:0} ms, {dwrap.Keys.Count} chunk(s) to remesh");
            // In-place rewraps left neighbour rings stale for a full sweep pass (~30s+) — the priority heal only fired on
            // FIRST-time swaps. Queue it here too so terraform borders converge in ticks (entries skip-and-requeue while
            // chunks are mid-regen).
            QueuePriorityHeal(dwrap);
            return true;
        }

        // Terrain op (hoe/pickaxe/TerrainComp) regenerated a heightmap: mark
        // that zone AND its 8 neighbours dirty (their border/apron samples read
        // into this zone's data — round-89, kills border lips after terraform).
        // The tick drains one rewrap per 0.25s so hoe-drag op storms converge
        // instead of thrashing.
        // From FAT's one Heightmap.Regenerate hook (FatVanillaHooks), before the distant-paint inject.
        internal static void OnHeightmapRegenerate(Heightmap heightmap) => RewrapZone.Run(heightmap);

        private static class RewrapZone
        {
            internal static void Run(Heightmap __instance)
            {
                try
                {
                    if (__instance == null || s_isDistantLod(__instance)) return;
                    var zone = ZoneSystem.GetZone(__instance.transform.position);
                    Patch_TerrainComp_ApplyOperation_Timing.OnRegenerated(zone);
                    // W3.3 — every paint op routes through Regenerate too
                    // (DoOperation → Poke), so this is THE paint refresh hook,
                    // on every peer. BEFORE the zone-count gate: brush chunks
                    // exist in Vanilla world mode too, and MarkPaintDirty
                    // no-ops for columns without voxel chunks.
                    MarkZonePaintDirty(zone);
                    if (s_zones.Count == 0) return;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var z = new Vector2s(zone.x + dx, zone.y + dz);
                            if (s_zones.ContainsKey(z)) s_dirty.Add(z);
                        }
                    // The edited zone itself goes first, this frame (0.2.711): it used to wait its turn among the 9 dirty zones,
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
        // them leaves firs growing out of the lake bed (user screenshots). Rocks
        // stay — a boulder in the shallows is what a real shoreline looks like,
        // and the owner asked for exactly that exception.
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
        // 0.2.686 ([lead] relaying Fire 10-01: "only stumps logs and rocks … under the water"; "a few small beech trees
        // and maybe some saplings in the water"): what is cleared is FatUnderWaterPlacement's rule (logs now STAY; small
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
                // 0.2.675 (R46: saplings standing in the CoopStream base pond): tool ponds (Vpool/va_pool WaterDisks, pours)
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

        // Rocks, logs, stumps and water plants stay: FatUnderWaterPlacement's keep list (0.2.686).
        // Tool water: the probe sits just above the plant's base, which must be at least DrownedDepth under the surface;
        // water at or below the sea sheet is vanilla's own and left alone.
        private const float DrownProbeLift = 0.1f, DrownedDepth = FatUnderWaterPlacement.SurfaceTolerance, SeaPlaneGate = 30.3f;

        // 0.2.686: one rule with generation and waterlife_test (FatUnderWaterPlacement): never anything built, alive,
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
        public static int WrapAllNow()
        {
            if (!ModeActive) return 0;
            int started = 0;
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || s_isDistantLod(hm)) continue;
                var zone = ZoneSystem.GetZone(hm.transform.position);
                if (s_zones.ContainsKey(zone)) continue;
                if (StartWrap(zone, hm)) started++;
            }
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
            float ix = (wx - (zx * 64f - 32f)) * (1f / 64f);
            float iz = (wz - (zz * 64f - 32f)) * (1f / 64f);
            return Color32.Lerp(Color32.Lerp(s_c0, s_c1, ix), Color32.Lerp(s_c2, s_c3, ix), iz);
        }
    }

    // BED PAINT, STRUCTURAL. The wet mask blends into the heightmap's paint
    // MASK at rebuild time, on every client, as a pure function of world
    // position. A painted zone can no longer meet an unpainted zone because
    // painted zones no longer EXIST — there is only the field, evaluated
    // wherever and whenever a heightmap builds. This replaces the
    // TerrainComp paint path whose per-zone timing produced hard border
    // rectangles across three test rounds ("if there's a wet mask it HAS to
    // blend — period"). Player paint still wins: cultivated/paved verts are
    // never overridden, and dirt only ever deepens.
    // Where an edit's seconds go (0.2.711, Fire 10-05: hoe and Infinity Hammer edits took seconds to show, with FGN's server
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
            FiresLogger.LogDebug($"[FiresVoxel] terraform: zone ({zone.x},{zone.y}) heightmap changed {ms:0} ms after the op was placed here " +
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
        private static float s_nextSweep;
        private static bool s_active;

        private static void Postfix(Heightmap __instance)
        {
            if (!GenWaterField.Ready) return;
            Stamp(__instance);
        }

        internal static void Sweep()
        {
            if (!GenWaterField.Ready)
            {
                // World left / regen: forget every memoized heightmap so the next
                // world's builds stamp fresh (stale entries = permanent holes).
                if (s_painted.Count > 0) s_painted.Clear();
                return;
            }
            if (Time.time < s_nextSweep) return;
            s_nextSweep = Time.time + 0.5f;
            var all = Heightmap.GetAllHeightmaps();
            for (int i = 0; i < all.Count; i++)
            {
                var hm = all[i];
                if (hm == null || s_painted.Contains(hm)) continue;
                Stamp(hm);
            }
            s_painted.RemoveWhere(h => h == null);
        }

        internal static void Stamp(Heightmap hm)
        {
            if (hm == null || s_hmDistant(hm)) return;
            // NEVER memoize before the water field is actually queryable: a heightmap
            // built during the load window used to be marked "painted" while the flag
            // table read 0 — a PERMANENT unpainted 64m rectangle mid-river (the hard
            // paint blocks in the field reports). Not-ready = try again next Sweep.
            if (!GenWaterField.SurfaceReady) return;
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
            // a band its neighbour painted (user screenshot: razor-straight
            // paint edges at zone lines). PaintWet01 bails at dist ≥ 12m, so
            // dry maps cost 65×65 cheap distance lookups — nothing.
            Vector3 p0 = hm.transform.position;
            int w = s_hmWidth(hm) + 1;
            var px = tex.GetPixels();
            if (px == null || px.Length < w * w) return;
            float half = (w - 1) * 0.5f;
            bool changed = false;
            for (int y = 0; y < w; y++)
            {
                float wz = p0.z + (y - half);
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float wet = VoxelZoneStreamer.PaintWet01(p0.x + (x - half), wz);
                    if (wet <= 0.03f) continue;
                    int i = row + x;
                    var c = px[i];
                    if (c.g > 0.25f || c.b > 0.25f) continue;   // cultivated/paved player paint wins
                    float r2 = Mathf.Max(c.r, wet);
                    // ASHLANDS: mask ALPHA is LAVA (vanilla GetVegetationMask)
                    // — wet paint must NEVER write it, or every Ashlands
                    // shoreline wears a lava band (user report). The r
                    // channel (dirt) is safe; alpha stays the worldgen lava.
                    float a2 = FiresWorldGenBase.BiomeCodeAt(p0.x + (x - half), wz) == 6
                        ? c.a : Mathf.Lerp(c.a, wet, wet);
                    if (r2 - c.r > 0.005f || Mathf.Abs(a2 - c.a) > 0.005f)
                    {
                        px[i] = new Color(r2, c.g, c.b, a2);
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                tex.SetPixels(px);
                tex.Apply();
                // The VOXEL terrain renders paint from per-column COPIES of
                // this mask, composed when the streamer refreshes them —
                // which usually happens BEFORE this stamp runs. Nothing told
                // those copies the mask changed, so near-field shore sand
                // vanished entirely while the far quilt (which evaluates
                // PaintWet01 directly) still showed it (user screenshot).
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
                if (ClutterSystem.instance != null)
                {
                    try { ClutterSystem.instance.ResetGrass(p0, half + 4f); }
                    catch (System.Exception ex)
                    { FiresLogger.LogWarning($"[FiresWetMask] ResetGrass threw: {ex.Message}"); }
                }
            }
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
}
