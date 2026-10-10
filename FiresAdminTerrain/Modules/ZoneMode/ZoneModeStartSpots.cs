using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Zones
{
    // Where FAT's start objects can stand (Tools\FAT_START_ITEMS.md): natural ground (ZoneModeStartGround: unpainted,
    // clear of builds, dry and flat), and on a converted map, where the start is the player's own town, a walkable build
    // floor at the height a player stands. Shared by the starter Chunk Generator, the hammer chest and the Cultivator
    // chest.
    internal static class ZoneModeStartSpots
    {
        private const float FootprintOffset = 0.8f;
        private const float MaxFloorRelief = 0.6f;
        private const float SurfaceSettleRange = 3f;
        private const float PoolClearance = 10f;
        private const float DryMargin = 1f;
        private const float StepUp = 0.6f;
        private const float FloorReach = 1.6f;
        private const float FloorBelow = 1.2f;
        private const float FloorAbove = 0.25f;
        private const float BodyClearance = 1.1f;
        private const float BodyHeight = 2.2f;
        private const int RingStepsMin = 8;

        private static readonly List<ZDO> s_nearby = new List<ZDO>();
        private static readonly List<Vector3> s_cover = new List<Vector3>();
        private static readonly Dictionary<int, bool> s_coverByPrefab = new Dictionary<int, bool>();

        // ── Natural ground ──────────────────────────────────────────────────
        // The ground at the spot, settled on the live voxel surface where one is held, when its footprint is flat enough.
        internal static bool TryFlatGround(float wx, float wz, out float ground)
        {
            ground = 0f;
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 5; i++)
            {
                float x = wx + (i == 1 ? FootprintOffset : i == 2 ? -FootprintOffset : 0f);
                float z = wz + (i == 3 ? FootprintOffset : i == 4 ? -FootprintOffset : 0f);
                if (!VoxelZoneStreamer.TryGroundHeight(x, z, out float y)) return false;
                if (VoxelWorld.TrySampleSurfaceY(x, z, y + SurfaceSettleRange, y - SurfaceSettleRange, out float surface, out _)) y = surface;
                if (i == 0) ground = y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
            }
            return max - min <= MaxFloorRelief;
        }

        // Flat dry natural ground (ZoneModeStartGround, surveyed for the search) in an open zone, away from any pool.
        internal static bool TryNaturalSpot(float wx, float wz, float buildClearance, float objectClearance, out Vector3 spot)
        {
            spot = default;
            if (!ZoneModeRegistry.IsOpen(ZoneSystem.GetZone(new Vector3(wx, 0f, wz)))) return false;
            if (!TryFlatGround(wx, wz, out float ground) || ground < ZoneSystem.instance.m_waterLevel + DryMargin) return false;
            spot = new Vector3(wx, ground, wz);
            if (FiresLiquidWaterPrefab.HasPourNear(spot, PoolClearance) || ZoneModeStartGround.UnderGeneratedWater(spot)) return false;
            return ZoneModeStartGround.Check(spot, FootprintOffset, buildClearance, objectClearance) == ZoneModeStartGround.Ground.Natural;
        }

        internal static bool HasObjectNear(Vector3 at, float clearance)
        {
            s_nearby.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(at), new SimulationDistance(1, 0), s_nearby);
            float clearance2 = clearance * clearance;
            foreach (var zdo in s_nearby)
                if (zdo != null && (zdo.GetPosition() - at).sqrMagnitude < clearance2) return true;
            return false;
        }

        // ── Cover (trees, rocks, brush and ruins; builds, creatures, dropped items and FAT's own objects hide nothing) ─
        internal static void CollectCover(Vector3 centre, int zones)
        {
            s_cover.Clear();
            s_nearby.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(centre), new SimulationDistance(zones, 0), s_nearby);
            foreach (var zdo in s_nearby)
            {
                if (zdo == null || ZoneModeChunkMachines.IsProtected(zdo) || ZoneModeBuilds.IsBuilt(zdo)) continue;
                if (IsCover(zdo.GetPrefab())) s_cover.Add(zdo.GetPosition());
            }
        }

        internal static int CoverAround(Vector3 at, float radius)
        {
            float radius2 = radius * radius;
            int around = 0;
            foreach (var item in s_cover)
                if (FlatDistanceSquared(item, at) < radius2) around++;
            return around;
        }

        private static bool IsCover(int prefabHash)
        {
            if (s_coverByPrefab.TryGetValue(prefabHash, out bool cover)) return cover;
            var prefab = ZNetScene.instance.GetPrefab(prefabHash);
            if (prefab == null) return false;
            cover = prefab.GetComponent<Character>() == null && prefab.GetComponent<ItemDrop>() == null;
            s_coverByPrefab[prefabHash] = cover;
            return cover;
        }

        // ── Standing spots, build floors included ───────────────────────────
        // A spot a player could stand on at about `at`'s height: real ground or a floor piece up to MaxDrop below it (a
        // step above), with nothing in the space the object needs. Ground is the live voxel surface, else the loaded
        // colliders, read straight down through trees and rocks: only terrain and floor pieces carry (Sanctuary 0.2.579:
        // the old test took any collider, and the starter stood in a tree). The topmost of the two wins, so a floor over
        // the ground is stood on. A zone nobody has near them holds no colliders, so floors are also read from ZDOs.
        // The space is checked twice. Where colliders are loaded, a box of the object's size over the spot is the judge:
        // walls, furniture, trunks, branches and rocks block, while flat decoration under knee height and pickables do not
        // (Sanctuary 0.2.582: a dense town refused every spot near the spawn). ZDO pivots then only catch trees and rocks,
        // whose colliders may sit away from their pivots. Where no colliders are loaded, every piece, tree and rock
        // pivot in the space blocks. LastSupport says what it stands on and how far below `at`.
        internal static bool TryStandingSpot(Vector3 at, out Vector3 spot) => TryStandingSpot(at, BodyClearance, out spot);

        internal static bool TryStandingSpot(Vector3 at, float clearance, out Vector3 spot)
        {
            spot = at;
            if (OnStartTemple(at)) return Refuse(Refusal.Temple);
            if (!s_batched) Gather(at, OwnArea);
            bool supported = false;
            string support = null;
            Collider standOn = null;
            if (VoxelWorld.TrySampleSurfaceY(at.x, at.z, at.y + StepUp, at.y - MaxDrop, out float ground, out _))
            {
                spot.y = ground;
                supported = true;
                support = GroundSupport;
            }
            if (TryColliderGround(at, out float hitY, out standOn, out string hitSupport) && (!supported || hitY > spot.y))
            {
                spot.y = hitY;
                supported = true;
                support = hitSupport;
            }
            for (int i = 0; !supported && i < s_floors.Count; i++)
            {
                var piece = s_floors[i].At;
                supported = FlatDistanceSquared(piece, at) <= FloorReach * FloorReach && piece.y >= at.y - FloorBelow && piece.y <= at.y + FloorAbove;
                if (supported) support = FloorSupport + PrefabName(s_floors[i].Prefab);
            }
            if (!supported) return Refuse(Refusal.NoGround);
            if (s_keepAwayRange > 0f && FlatDistanceSquared(spot, s_keepAway) < s_keepAwayRange * s_keepAwayRange) return Refuse(Refusal.KeptApart);
            bool collidersLoaded = ZoneSystem.instance.IsZoneLoaded(spot);
            foreach (var (other, kind) in s_solids)
            {
                if (collidersLoaded && kind != Refusal.Growth) continue;
                if (other.y < spot.y - FloorAbove || other.y >= spot.y + BodyHeight) continue;
                if (FlatDistanceSquared(other, spot) < clearance * clearance) return Refuse(kind);
            }
            var blocking = Blocked(spot, clearance, standOn);
            if (blocking != Refusal.None) return Refuse(blocking);
            LastSupport = $"{support}, {at.y - spot.y:0.0} m below the spawn's height";
            return true;
        }

        internal static string LastSupport { get; private set; } = string.Empty;

        // The spawn stands on a converted map's raised StartTemple, so the town round it lies metres lower (Sanctuary
        // 0.2.586: 1,182 ring spots found no ground within 3 m below the spawn).
        private const float MaxDrop = 8f;
        private const float BoxLift = 0.15f;
        private const float KneeHeight = 0.45f;
        private const string GroundSupport = "on ground";
        private const string FloorSupport = "on floor piece ";
        private const string TerrainLayer = "terrain";
        private static readonly string[] StandLayers = { TerrainLayer, "piece" };
        private static readonly string[] BlockLayers = { "Default", "static_solid", "Default_small", "piece", "vehicle" };
        private static readonly Collider[] s_overlap = new Collider[32];
        private static int s_standMask, s_blockMask, s_terrainLayer = -1;

        // ── Why spots were refused (for the log) ────────────────────────────
        private enum Refusal { None, Temple, NoGround, KeptApart, Piece, Growth, Other }

        private static readonly string[] RefusalNames =
        {
            "", "on the StartTemple", "no ground or floor", "too near the other start object", "a build in the way",
            "a tree or rock in the way", "something else in the way",
        };

        private static readonly int[] s_refusals = new int[RefusalNames.Length];

        private static bool Refuse(Refusal why)
        {
            s_refusals[(int)why]++;
            return false;
        }

        // The refusals of the last ring search, e.g. "212 spot(s) refused: 150 a build in the way, 62 no ground or floor".
        internal static string LastRefusals { get; private set; } = string.Empty;

        private static void SummariseRefusals()
        {
            int total = 0;
            var parts = new List<string>();
            for (int i = 1; i < s_refusals.Length; i++)
            {
                if (s_refusals[i] == 0) continue;
                total += s_refusals[i];
                parts.Add($"{s_refusals[i]} {RefusalNames[i]}");
            }
            LastRefusals = total == 0 ? "no spot refused" : $"{total} spot(s) refused: {string.Join(", ", parts)}";
        }

        // Straight down from a step above, through everything but terrain and pieces. A piece carries only when it is a
        // floor; a wall top or a roof is no place to stand a machine.
        private static bool TryColliderGround(Vector3 at, out float ground, out Collider standOn, out string support)
        {
            ground = 0f;
            standOn = null;
            support = null;
            EnsureMasks();
            if (!Physics.Raycast(at + Vector3.up * StepUp, Vector3.down, out var hit, StepUp + MaxDrop, s_standMask, QueryTriggerInteraction.Ignore))
                return false;
            if (hit.collider.gameObject.layer == s_terrainLayer)
            {
                ground = hit.point.y;
                support = GroundSupport;
                return true;
            }
            var view = hit.collider.GetComponentInParent<ZNetView>();
            string name = view != null ? Utils.GetPrefabName(view.gameObject) : null;
            if (name == null || name.IndexOf(FloorWord, System.StringComparison.OrdinalIgnoreCase) < 0) return false;
            ground = hit.point.y;
            standOn = hit.collider;
            support = FloorSupport + name;
            return true;
        }

        // Anything solid in a box of the object's size just above the spot: trees, rocks, walls, other machines. Flat
        // decoration whose top stays under knee height, and pickables, leave the space free.
        private static Refusal Blocked(Vector3 spot, float clearance, Collider standOn)
        {
            EnsureMasks();
            var centre = spot + Vector3.up * (BoxLift + BodyHeight * 0.5f);
            var half = new Vector3(clearance, BodyHeight * 0.5f, clearance);
            int count = Physics.OverlapBoxNonAlloc(centre, half, s_overlap, Quaternion.identity, s_blockMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var collider = s_overlap[i];
                if (collider == null || collider == standOn || collider.bounds.max.y < spot.y + KneeHeight) continue;
                if (collider.GetComponentInParent<Pickable>() != null) continue;
                if (collider.GetComponentInParent<WearNTear>() != null) return Refusal.Piece;
                return IsGrowth(collider.GetComponentInParent<ZNetView>()) ? Refusal.Growth : Refusal.Other;
            }
            return Refusal.None;
        }

        private static bool IsGrowth(Component part)
            => part != null && (part.GetComponent<TreeBase>() != null || part.GetComponent<TreeLog>() != null
                                || part.GetComponent<MineRock>() != null || part.GetComponent<MineRock5>() != null
                                || part.GetComponent<Destructible>() != null);

        // What an object's pivot in the space counts as: a non-floor piece, a tree or rock, or nothing that blocks.
        private static readonly Dictionary<int, Refusal> s_solidByPrefab = new Dictionary<int, Refusal>();

        private static Refusal SolidKind(int prefabHash)
        {
            if (s_solidByPrefab.TryGetValue(prefabHash, out var kind)) return kind;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            kind = prefab == null || IsFloor(prefabHash) ? Refusal.None
                : prefab.GetComponent<WearNTear>() != null ? Refusal.Piece
                : prefab.GetComponent<TreeBase>() != null || prefab.GetComponent<TreeLog>() != null
                  || prefab.GetComponent<MineRock>() != null || prefab.GetComponent<MineRock5>() != null ? Refusal.Growth
                : Refusal.None;
            s_solidByPrefab[prefabHash] = kind;
            return kind;
        }

        private static void EnsureMasks()
        {
            if (s_standMask != 0) return;
            s_standMask = LayerMask.GetMask(StandLayers);
            s_blockMask = LayerMask.GetMask(BlockLayers);
            s_terrainLayer = LayerMask.NameToLayer(TerrainLayer);
        }

        private static string PrefabName(int prefabHash)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            return prefab != null ? prefab.name : prefabHash.ToString();
        }

        private const string FloorWord = "floor";
        private static readonly Dictionary<int, bool> s_floorByPrefab = new Dictionary<int, bool>();

        private static bool IsFloor(int prefabHash)
        {
            if (s_floorByPrefab.TryGetValue(prefabHash, out bool floor)) return floor;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            floor = prefab != null && prefab.name.IndexOf(FloorWord, System.StringComparison.OrdinalIgnoreCase) >= 0;
            s_floorByPrefab[prefabHash] = floor;
            return floor;
        }

        // The standing spot nearest `centre`, ring by ring from `minRadius` out to `maxRadius`, at `centre`'s height, at
        // least `apart` from `keepAway` when one is given (Fire, 2026-09-27: the hammer chest and the starter each have a
        // spot of their own). The objects round the rings are read once. LastRefusals says why the others were refused.
        internal static bool TryRingSpot(Vector3 centre, float minRadius, float maxRadius, float step, out Vector3 spot,
            float clearance = BodyClearance, Vector3 keepAway = default, float apart = 0f)
        {
            spot = default;
            System.Array.Clear(s_refusals, 0, s_refusals.Length);
            s_keepAway = keepAway;
            s_keepAwayRange = apart;
            Gather(centre, OwnArea + Mathf.CeilToInt(maxRadius / ZoneSystem.c_ZoneSize));
            s_batched = true;
            try
            {
                for (float radius = minRadius; radius <= maxRadius; radius += step)
                {
                    int steps = Mathf.Max(RingStepsMin, Mathf.CeilToInt(2f * Mathf.PI * radius / step));
                    for (int k = 0; k < steps; k++)
                    {
                        float angle = k * 2f * Mathf.PI / steps;
                        var at = new Vector3(centre.x + Mathf.Cos(angle) * radius, centre.y, centre.z + Mathf.Sin(angle) * radius);
                        if (TryStandingSpot(at, clearance, out spot)) return true;
                    }
                }
                return false;
            }
            finally
            {
                s_batched = false;
                s_keepAwayRange = 0f;
                SummariseRefusals();
            }
        }

        // The ring round the spawn that the converted-map starter and hammer chest search, nearest first. It reaches well
        // past the town's first ring (Sanctuary 0.2.582: 12 m held nothing, and the starter fell back 42 m out), and the
        // two keep ObjectsApart from each other (Fire, 2026-09-27: "the hammer in one spot the chest in another").
        internal const float SpawnMinRing = 3f;
        internal const float SpawnMaxRing = 30f;
        internal const float SpawnRingStep = 1.5f;
        internal const float ObjectsApart = 10f;

        private const int OwnArea = 1;
        private static bool s_batched;
        private static Vector3 s_keepAway;
        private static float s_keepAwayRange;
        private static readonly List<(Vector3 At, int Prefab)> s_floors = new List<(Vector3, int)>();
        private static readonly List<(Vector3 At, Refusal Kind)> s_solids = new List<(Vector3, Refusal)>();

        // The placed floors and the solid objects (pieces, trees, rocks) round a point, `area` zones out.
        private static void Gather(Vector3 centre, int area)
        {
            s_floors.Clear();
            s_solids.Clear();
            s_nearby.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(centre), new SimulationDistance(area, 0), s_nearby);
            foreach (var zdo in s_nearby)
            {
                if (zdo == null) continue;
                int prefab = zdo.GetPrefab();
                if (IsFloor(prefab))
                {
                    if (ZoneModeBuilds.IsPlaced(zdo)) s_floors.Add((zdo.GetPosition(), prefab));
                    continue;
                }
                if (!zdo.Persistent) continue;
                var kind = SolidKind(prefab);
                if (kind != Refusal.None) s_solids.Add((zdo.GetPosition(), kind));
            }
        }

        // ── The StartTemple ─────────────────────────────────────────────────
        // On a converted map nothing of the start stands on the StartTemple location itself (Fire, 2026-09-27: the Va
        // re-convert put the starter and chest "RIGHT on the spawn temple", on the town built over it). Its footprint is
        // the location's own radius round its centre. A fresh world keeps Fire's rule of the chest close by the spawn.
        private const string TempleName = "StartTemple";
        private const float TempleMargin = 2f;
        private static ZoneSystem s_templeFor;
        private static Vector3 s_templeCentre;
        private static float s_templeRadius;

        internal static bool OnStartTemple(Vector3 point)
        {
            if (!ZoneModePark.Converted || !TryGetTemple(out var centre, out float radius)) return false;
            float reach = radius + TempleMargin;
            return FlatDistanceSquared(point, centre) < reach * reach;
        }

        // How far past the temple a ring round the spawn must reach to find ground off it.
        internal static float PastTemple(float reach)
            => ZoneModePark.Converted && TryGetTemple(out _, out float radius) ? reach + radius + TempleMargin : reach;

        private static bool TryGetTemple(out Vector3 centre, out float radius)
        {
            var zones = ZoneSystem.instance;
            if (zones != null && !ReferenceEquals(zones, s_templeFor) && zones.FindClosestLocation(TempleName, Vector3.zero, out var temple))
            {
                s_templeFor = zones;
                s_templeCentre = temple.m_position;
                s_templeRadius = temple.m_location != null ? Mathf.Max(temple.m_location.m_exteriorRadius, temple.m_location.m_interiorRadius) : 0f;
            }
            centre = s_templeCentre;
            radius = s_templeRadius;
            return zones != null && ReferenceEquals(zones, s_templeFor);
        }

        internal static float FlatDistanceSquared(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        internal static string Bearing(Vector3 from, Vector3 to)
        {
            float degrees = Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;
            string[] points = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return points[Mathf.RoundToInt(Mathf.Repeat(degrees, 360f) / 45f) % points.Length];
        }
    }
}
