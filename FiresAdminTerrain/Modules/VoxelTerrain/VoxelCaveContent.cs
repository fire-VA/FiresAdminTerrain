using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;

namespace VerdantsAscent.Terrain.Voxel
{
    // Server-side cave dressing. Once a zone's voxel data has settled (the server holds it even where it renders
    // nothing), the zone is surveyed and dressed from its biome's table: floor pickables, ceiling hangers, rubble, ore
    // nodes, a restocking chest and monster spawners sized to their caverns. Every object is a ZDO the server writes
    // directly and tags as cave content, and the zone is recorded so it is dressed once per world.
    internal static class VoxelCaveContent
    {
        internal struct Void
        {
            public float Floor;
            public float Ceiling;
            public bool OpenToSky;
            public float Headroom => Ceiling - Floor;
        }

        internal struct Spot
        {
            public Vector3 Floor;
            public float Headroom;
            public float Cover;
            public Heightmap.Biome Biome;
            public bool Island;
        }

        internal const int ContentVersion = 5;
        internal static readonly int CaveContentKey = "fires_cave".GetStableHashCode();

        private const float ScanStep = 1f;
        private const float ScanDepthMargin = 4f;
        private const float Passable = 2f;
        private const float CeilingClear = 2.6f;
        private const float CavernClear = 5.5f;
        private const float SampleSpacing = 4f;
        private const float SampleJitter = 1f;
        private const float MinCover = 3f;
        private const float ClumpSpread = 1f;
        private const int MaxClump = 3;
        private const float ClumpableRadius = 0.6f;
        private const float SurfaceProbeLift = 0.5f;
        private const float HangerKeepClear = 2.2f;
        private const float MinMountedScale = 0.3f;
        private const float WallProbeHeight = 1.4f;
        private const float WallReach = 7f;
        private const float WallReachShare = 0.45f;
        private const float FloorFacing = 0.35f;
        private const float CeilingFacing = 0.35f;
        private const float WallFacing = 0.7f;
        private const float CoverProbeLift = 0.5f;
        private const float RubbleFloorRelief = 0.9f;
        private const float SettleBand = 1.5f;
        private const float LegacyBuryDepth = 1.5f;
        private const float LegacyAirProbe = 1f;
        private const float ZoneSize = 64f;
        internal const float ChestFloorRelief = 0.6f;
        internal const float SpawnerFloorRelief = 1.5f;
        private const float OreFloorRelief = 1.2f;
        private const float OreSink = 0.3f;
        private const float PropFloorRelief = 0.9f;
        private const float FootprintProbe = 0.7f;
        private const float ClearanceStep = 0.5f;
        private const int ClearanceDirections = 8;
        private const float BigItemGap = 1f;

        private static readonly string[] LegacyPrefabs =
        {
            "Pickable_Mushroom_yellow", "Greydwarf_Root", "rock4_forest",
            "Pickable_ForestCryptRemains01", "Pickable_ForestCryptRemains02", "Pickable_ForestCryptRandom",
        };

        private static readonly HashSet<Vector2s> s_done = new HashSet<Vector2s>();
        private static readonly HashSet<Vector2s> s_waiting = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_retry = new List<Vector2s>();
        private static readonly List<Void> s_voids = new List<Void>(8);
        private static readonly List<Spot> s_spots = new List<Spot>(256);
        private static readonly List<(float bottom, float top, Heightmap.Biome biome)> s_spans = new List<(float, float, Heightmap.Biome)>(4);
        private static readonly List<ZDO> s_zoneObjects = new List<ZDO>();
        private static readonly List<Vector4> s_bigItems = new List<Vector4>();
        private static HashSet<int> s_legacyHashes;
        private const float ReportSeconds = 30f;
        private static CaveDressTally s_report;
        private static int s_reportZones, s_reportSpots, s_reportCleared, s_reportPools, s_reportColumns, s_reportPockets;
        private static int s_surveyColumns, s_surveyPockets;
        private static float s_nextReport;
        private static readonly Dictionary<int, bool> s_holdsContainer = new Dictionary<int, bool>();
        private const float ExistingContainerRadius = 1.5f;

        private static readonly AccessTools.FieldRef<StaticPhysics, ZNetView> s_staticPhysicsView =
            AccessTools.FieldRefAccess<StaticPhysics, ZNetView>("m_nview");

        // Every air pocket in the column at (wx,wz) from just under the surface down to the deepest carve, top-first.
        // Reads the live voxel density the terrain was built from, so a reported floor is a floor players stand on.
        internal static void ScanVoids(float wx, float wz, float surfaceY, List<Void> into)
        {
            float carveReach = Mathf.Max(FiresVoxelConfig.CaveMaxDepth.Value,
                FiresVoxelConfig.SinkholeDepth != null ? FiresVoxelConfig.SinkholeDepth.Value : 0f);
            float bottom = Mathf.Max(FiresVoxelConfig.CaveMinY.Value, surfaceY - carveReach) - ScanDepthMargin;
            ScanRange(wx, wz, surfaceY - 0.5f, bottom, into);
        }

        // Air pockets between top and bottom, top-first. A pocket still open at the bottom of the range is not one.
        private static void ScanRange(float wx, float wz, float top, float bottom, List<Void> into)
        {
            into.Clear();
            if (top <= bottom) return;

            bool inAir = false, openAtTop = false;
            float airTop = 0f;
            for (float y = top; y >= bottom; y -= ScanStep)
            {
                bool solid = IsSolid(wx, y, wz);
                if (!solid && !inAir)
                {
                    inAir = true;
                    airTop = y;
                    openAtTop = y >= top;
                }
                else if (solid && inAir)
                {
                    inAir = false;
                    into.Add(new Void { Floor = y + ScanStep * 0.5f, Ceiling = airTop, OpenToSky = openAtTop });
                }
            }
        }

        internal static bool IsSolid(float wx, float wy, float wz)
        {
            var key = new Vector3Int(
                FloorDiv(Mathf.FloorToInt(wx), VoxelChunk.CELLS),
                FloorDiv(Mathf.FloorToInt(wy), VoxelChunk.CELLS),
                FloorDiv(Mathf.FloorToInt(wz), VoxelChunk.CELLS));
            if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null) return true;
            sbyte d = chunk.SampleDensityWorld(new Vector3(wx, wy, wz));
            if (d == sbyte.MinValue) return true;
            return d > 0;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);

        // The exact solid-to-air crossing near a 1 m scan floor: anything placed on the raw scan floor floats.
        internal static float SettleToFloor(float wx, float wz, float scanFloor, float ceiling)
        {
            float top = Mathf.Min(ceiling - 0.1f, scanFloor + SettleBand);
            float bottom = scanFloor - SettleBand;
            if (top > bottom && VoxelWorld.TrySampleSurfaceY(wx, wz, top, bottom, out float surface, out _))
                return surface;
            return scanFloor;
        }

        public static void PlaceForZone(Vector2s zone)
        {
            if (!CaveContentConfig.Enabled || !FiresVoxelConfig.GeneratesCaves) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZNetScene.instance == null || ZDOMan.instance == null) return;
            if (!VoxelZoneStreamer.IsZoneKnown(zone) || !VoxelZoneStreamer.IsZoneTerrainSettled(zone)) return;
            if (s_done.Contains(zone)) return;
            bool recorded = VoxelCaveRecord.TryGetZoneVersion(zone, out int version);
            if (recorded && version == ContentVersion)
            {
                s_done.Add(zone);
                s_waiting.Remove(zone);
                return;
            }
            int seed = WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0;
            var rng = new System.Random(unchecked(zone.x * 73856093 ^ zone.y * 19349663 ^ seed));
            if (!GroundReady(zone) || Survey(zone, rng) > 0)
            {
                s_waiting.Add(zone);
                return;
            }
            s_done.Add(zone);
            s_waiting.Remove(zone);

            int cleared = recorded ? ClearTaggedContent(zone) : ClearLegacyContent(zone);
            var tally = new CaveDressTally();
            s_bigItems.Clear();
            ClaimExistingContainers(zone);
            DressSmallItems(rng, ref tally);
            VoxelCaveChests.PlaceForZone(zone, s_spots, rng, ref tally);
            VoxelCaveSpawners.PlaceForZone(zone, s_spots, rng, ref tally);
            PlaceOres(rng, ref tally);
            PlaceProps(rng, ref tally);
            bool pooled = TrySeedCavePool(rng);

            VoxelCaveRecord.MarkZoneDressed(zone, ContentVersion);
            s_report.Add(tally);
            s_reportZones++;
            s_reportSpots += s_spots.Count;
            s_reportColumns += s_surveyColumns;
            s_reportPockets += s_surveyPockets;
            s_reportCleared += cleared;
            if (pooled) s_reportPools++;
            ReportIfDue();
        }

        public static void ForgetZone(Vector2s zone)
        {
            s_done.Remove(zone);
            s_waiting.Remove(zone);
        }

        // A survey needs the zone's ground heights under the current zone-mode view, and a dedicated server rebuilds
        // its cached ones after every zone opening. Without them it would see no caves and record the zone as done,
        // so the zone waits and tries again every streamer tick.
        internal static void RetryWaiting()
        {
            if (s_waiting.Count == 0) return;
            s_retry.Clear();
            s_retry.AddRange(s_waiting);
            foreach (var zone in s_retry)
            {
                if (!VoxelZoneStreamer.IsZoneKnown(zone) || !Zones.ZoneModeRegistry.IsOpen(zone)) s_waiting.Remove(zone);
                else PlaceForZone(zone);
            }
        }

        private static bool GroundReady(Vector2s zone)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                return HeightmapBuildCache.EnsureZoneAndNeighbours(zone);
            var center = ZoneSystem.GetZonePos(zone);
            return VoxelZoneStreamer.TryGroundHeight(center.x, center.z, out _);
        }

        internal static void ReportIfDue()
        {
            if ((s_reportZones == 0 && s_waiting.Count == 0) || Time.time < s_nextReport) return;
            s_nextReport = Time.time + ReportSeconds;
            FiresLogger.LogInfo($"[FiresVoxel] caves dressed in {s_reportZones} zone(s) from {s_reportSpots} cave floor spot(s) ({s_reportPockets} air pocket(s) in {s_reportColumns} surveyed column(s)): "
                + s_report.Describe() + $", {s_reportPools} pool(s)"
                + (s_reportCleared > 0 ? $"; first removed {s_reportCleared} object(s) of an older dressing" : "")
                + (s_waiting.Count > 0 ? $"; {s_waiting.Count} zone(s) waiting for their ground heights" : "") + ".");
            s_report = new CaveDressTally();
            s_reportZones = s_reportSpots = s_reportCleared = s_reportPools = s_reportColumns = s_reportPockets = 0;
        }

        internal static bool IsCaveContent(StaticPhysics physics)
        {
            var view = s_staticPhysicsView(physics);
            var zdo = view != null ? view.GetZDO() : null;
            return zdo != null && zdo.GetInt(CaveContentKey) != 0;
        }

        // Returns how many columns had no ground height; the survey only stands when that is none.
        private static int Survey(Vector2s zone, System.Random rng)
        {
            s_spots.Clear();
            int groundMisses = 0;
            s_surveyColumns = s_surveyPockets = 0;
            var generator = WorldGenerator.instance;
            float minX = zone.x * ZoneSize - ZoneSize * 0.5f, minZ = zone.y * ZoneSize - ZoneSize * 0.5f;
            for (float oz = SampleSpacing * 0.5f; oz < ZoneSize; oz += SampleSpacing)
            {
                for (float ox = SampleSpacing * 0.5f; ox < ZoneSize; ox += SampleSpacing)
                {
                    float wx = minX + ox + Jitter(rng), wz = minZ + oz + Jitter(rng);
                    if (VoxelZoneStreamer.TryGroundHeight(wx, wz, out float ground))
                    {
                        ScanVoids(wx, wz, ground, s_voids);
                        s_surveyColumns++;
                        s_surveyPockets += s_voids.Count;
                        AddSpots(wx, wz, ground, generator != null ? generator.GetBiome(wx, wz) : Heightmap.Biome.Meadows, false);
                    }
                    else groundMisses++;
                    int spans = FiresSkyIslands.GetIslandSpans(wx, wz, s_spans);
                    for (int s = 0; s < spans; s++)
                    {
                        var span = s_spans[s];
                        ScanRange(wx, wz, span.top - 0.5f, span.bottom + 0.5f, s_voids);
                        s_surveyPockets += s_voids.Count;
                        AddSpots(wx, wz, span.top, span.biome, true);
                    }
                }
            }
            return groundMisses;
        }

        // The covered pockets of one scan: a skyland's caves count their cover from the island's top, like the ground's
        // from the ground, and dress from the island's own biome.
        private static void AddSpots(float wx, float wz, float roof, Heightmap.Biome biome, bool island)
        {
            for (int i = 0; i < s_voids.Count; i++)
            {
                var pocket = s_voids[i];
                if (pocket.OpenToSky || pocket.Headroom < Passable || roof - pocket.Ceiling < MinCover) continue;
                float floor = SettleToFloor(wx, wz, pocket.Floor, pocket.Ceiling);
                s_spots.Add(new Spot
                {
                    Floor = new Vector3(wx, floor, wz),
                    Headroom = pocket.Ceiling - floor,
                    Cover = roof - pocket.Ceiling,
                    Biome = biome,
                    Island = island,
                });
            }
        }

        // The surface the rock over a point ends at: the top of the skyland the point is inside, else the ground.
        private static bool TryRoofLine(Vector3 p, out float roof)
        {
            int spans = FiresSkyIslands.GetIslandSpans(p.x, p.z, s_spans);
            for (int s = 0; s < spans; s++)
            {
                if (p.y <= s_spans[s].bottom || p.y >= s_spans[s].top) continue;
                roof = s_spans[s].top;
                return true;
            }
            return VoxelZoneStreamer.TryGroundHeight(p.x, p.z, out roof);
        }

        // At least `depth` inside the rock mass: under the ground, or inside a skyland under its top.
        internal static bool IsInsideRock(Vector3 p, float depth) => TryRoofLine(p, out float roof) && p.y < roof - depth;

        private static float Jitter(System.Random rng) => (float)(rng.NextDouble() * 2.0 - 1.0) * SampleJitter;

        private static void DressSmallItems(System.Random rng, ref CaveDressTally tally)
        {
            float density = CaveContentConfig.Density;
            if (density <= 0f) return;
            for (int i = 0; i < s_spots.Count; i++)
            {
                var spot = s_spots[i];
                var table = CaveContentConfig.For(spot.Biome);
                if (table == null) continue;

                if (rng.NextDouble() < CaveContentConfig.FloorChance * density
                    && table.Floor.TryPick(rng, out var floorPrefab))
                {
                    int clump = VoxelSurfaceSnap.Anchor(floorPrefab).Radius <= ClumpableRadius ? 1 + rng.Next(MaxClump) : 1;
                    for (int c = 0; c < clump; c++)
                    {
                        float x = spot.Floor.x + (c == 0 ? 0f : (float)(rng.NextDouble() * 2.0 - 1.0) * ClumpSpread);
                        float z = spot.Floor.z + (c == 0 ? 0f : (float)(rng.NextDouble() * 2.0 - 1.0) * ClumpSpread);
                        float y = c == 0 ? spot.Floor.y : SettleToFloor(x, z, spot.Floor.y, spot.Floor.y + spot.Headroom);
                        if (TryPlaceOnFloor(floorPrefab, new Vector3(x, y, z), 1f, rng, CaveContentKind.Floor)) tally.Floor++;
                    }
                }

                if (spot.Headroom >= CeilingClear && rng.NextDouble() < CaveContentConfig.CeilingChance * density
                    && table.Ceiling.TryPick(rng, out var ceilingPrefab) && TryHang(spot, ceilingPrefab, rng))
                    tally.Ceiling++;

                if (rng.NextDouble() < CaveContentConfig.WallChance * density
                    && table.Wall.TryPick(rng, out var wallPrefab) && TryMountOnWall(spot, wallPrefab, rng))
                    tally.Wall++;

                if (spot.Headroom >= CavernClear && rng.NextDouble() < CaveContentConfig.RubbleChance * density
                    && table.Rubble.TryPick(rng, out var rubblePrefab))
                {
                    var rubble = VoxelSurfaceSnap.Anchor(rubblePrefab);
                    float scale = rubble.Scalable ? CaveContentConfig.RubbleScale : 1f;
                    if (FitsBigItem(spot, rubble.Radius * scale, rubble.Reach * scale, RubbleFloorRelief)
                        && TryPlaceOnFloor(rubblePrefab, spot.Floor, scale, rng, CaveContentKind.Rubble))
                    {
                        ClaimBigItem(spot.Floor, rubble.Radius * scale);
                        tally.Rubble++;
                    }
                }
            }
        }

        private static void PlaceOres(System.Random rng, ref CaveDressTally tally)
        {
            int budget = CaveContentConfig.OresPerZone;
            float chance = CaveContentConfig.OreChance * CaveContentConfig.Density;
            for (int i = 0; i < s_spots.Count && budget > 0; i++)
            {
                var spot = s_spots[i];
                if (rng.NextDouble() >= chance) continue;
                var table = CaveContentConfig.For(spot.Biome);
                if (table == null || !table.Ores.TryPick(rng, out var ore)) continue;
                var footprint = Footprint(ore);
                if (!FitsBigItem(spot, footprint.x, footprint.y, OreFloorRelief)) continue;
                if (Spawn(ore, spot.Floor + Vector3.down * OreSink, RandomYaw(rng), 1f, CaveContentKind.Ore) == null) continue;
                ClaimBigItem(spot.Floor, footprint.x);
                tally.Ores++;
                budget--;
            }
        }

        private static void PlaceProps(System.Random rng, ref CaveDressTally tally)
        {
            float chance = CaveContentConfig.PropChance * CaveContentConfig.Density;
            for (int i = 0; i < s_spots.Count; i++)
            {
                var spot = s_spots[i];
                if (spot.Headroom < CavernClear || rng.NextDouble() >= chance) continue;
                var table = CaveContentConfig.For(spot.Biome);
                if (table == null || !table.Props.TryPick(rng, out var prop)) continue;
                var footprint = Footprint(prop);
                if (!FitsBigItem(spot, footprint.x, footprint.y, PropFloorRelief)) continue;
                if (Spawn(prop, spot.Floor, RandomYaw(rng), 1f, CaveContentKind.Prop) == null) continue;
                ClaimBigItem(spot.Floor, footprint.x);
                tally.Props++;
            }
        }

        // Cavern floor with room for an object of this footprint: headroom, flat enough, clear sideways, and not on
        // top of another big item.
        internal static bool FitsBigItem(Spot spot, float radius, float height, float maxRelief)
        {
            if (spot.Headroom < height + BigItemGap) return false;
            if (OverlapsBigItem(spot.Floor, radius)) return false;
            if (!FlatFloorWithin(spot.Floor, spot.Floor.y + spot.Headroom, radius, maxRelief)) return false;
            return ClearanceAt(spot.Floor, height, radius + BigItemGap) >= radius;
        }

        internal static void ClaimBigItem(Vector3 at, float radius) => s_bigItems.Add(new Vector4(at.x, at.y, at.z, radius));

        // Chests already in the zone (another system's, or a restocked one) keep their space.
        private static void ClaimExistingContainers(Vector2s zone)
        {
            foreach (var zdo in ZoneObjects(zone))
            {
                int prefabHash = zdo.GetPrefab();
                if (!s_holdsContainer.TryGetValue(prefabHash, out bool holds))
                {
                    var prefab = ZNetScene.instance.GetPrefab(prefabHash);
                    holds = prefab != null && prefab.GetComponentInChildren<Container>(true) != null;
                    s_holdsContainer[prefabHash] = holds;
                }
                if (holds) ClaimBigItem(zdo.GetPosition(), ExistingContainerRadius);
            }
        }

        private static bool OverlapsBigItem(Vector3 at, float radius)
        {
            for (int i = 0; i < s_bigItems.Count; i++)
            {
                var item = s_bigItems[i];
                float dx = item.x - at.x, dz = item.z - at.z, reach = item.w + radius + BigItemGap;
                if (dx * dx + dz * dz < reach * reach && Mathf.Abs(item.y - at.y) < reach) return true;
            }
            return false;
        }

        private static bool FlatFloorWithin(Vector3 floor, float ceiling, float radius, float maxRelief)
        {
            float offset = radius * FootprintProbe;
            float min = floor.y, max = floor.y;
            for (int i = 0; i < 4; i++)
            {
                float x = floor.x + (i == 0 ? offset : i == 1 ? -offset : 0f);
                float z = floor.z + (i == 2 ? offset : i == 3 ? -offset : 0f);
                float top = Mathf.Min(ceiling - 0.1f, floor.y + SettleBand);
                if (!VoxelWorld.TrySampleSurfaceY(x, z, top, floor.y - SettleBand, out float y, out _)) return false;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
            }
            return max - min <= maxRelief;
        }

        // Free radius around a floor point at standing heights, in half-metre steps up to maxRadius.
        internal static float ClearanceAt(Vector3 floor, float height, float maxRadius)
        {
            float mid = Mathf.Max(0.6f, height * 0.5f), top = Mathf.Max(0.8f, height - 0.2f);
            for (float r = ClearanceStep; r <= maxRadius; r += ClearanceStep)
            {
                for (int d = 0; d < ClearanceDirections; d++)
                {
                    float angle = d * (Mathf.PI * 2f / ClearanceDirections);
                    float x = floor.x + Mathf.Cos(angle) * r, z = floor.z + Mathf.Sin(angle) * r;
                    if (IsSolid(x, floor.y + mid, z) || IsSolid(x, floor.y + top, z)) return r - ClearanceStep;
                }
            }
            return maxRadius;
        }

        // Horizontal radius (x) and height (y) of an upright floor-standing prefab around its pivot.
        internal static Vector2 Footprint(GameObject prefab)
        {
            var mesh = VoxelSurfaceSnap.Anchor(prefab).Mesh;
            float x = Mathf.Max(-mesh.min.x, mesh.max.x), z = Mathf.Max(-mesh.min.z, mesh.max.z);
            return new Vector2(Mathf.Sqrt(x * x + z * z), Mathf.Max(0f, mesh.max.y));
        }

        private static bool TryPlaceOnFloor(GameObject prefab, Vector3 floor, float scale, System.Random rng, CaveContentKind kind)
        {
            var normal = VoxelSurfaceSnap.SurfaceNormal(floor, Vector3.up);
            if (normal.y < FloorFacing) return false;
            if (!VoxelSurfaceSnap.TryMount(prefab, floor, normal, scale, rng, out var at, out var rotation)) return false;
            return SpawnTracked(prefab, at, rotation, scale, kind);
        }

        // A hanger comes down from the rock overhead with its attach face on the ceiling, shortened so a player still
        // fits underneath; one that cannot shrink enough is skipped.
        private static bool TryHang(Spot spot, GameObject prefab, System.Random rng)
        {
            var anchor = VoxelSurfaceSnap.Anchor(prefab);
            if (!anchor.Valid) return false;
            if (!VoxelSurfaceSnap.TryRaySurface(spot.Floor + Vector3.up * SurfaceProbeLift, Vector3.up, spot.Headroom + SettleBand,
                    out var ceiling, out _))
                return false;
            var normal = VoxelSurfaceSnap.SurfaceNormal(ceiling, Vector3.down);
            if (normal.y > -CeilingFacing) return false;
            float scale = Mathf.Min(1f, (ceiling.y - spot.Floor.y - HangerKeepClear) / (anchor.Reach * -normal.y));
            if (scale < 1f && (!anchor.Scalable || scale < MinMountedScale)) return false;
            if (!VoxelSurfaceSnap.TryMount(prefab, ceiling, normal, scale, rng, out var at, out var rotation)) return false;
            return SpawnTracked(prefab, at, rotation, scale, CaveContentKind.Ceiling);
        }

        // A wall piece goes flat on the rock beside a floor spot and never reaches more than part of the way across the
        // passage. Only cave walls count: rock with a roof over it, not the outside of the terrain.
        private static bool TryMountOnWall(Spot spot, GameObject prefab, System.Random rng)
        {
            var anchor = VoxelSurfaceSnap.Anchor(prefab);
            if (!anchor.Valid) return false;
            float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
            var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            var from = spot.Floor + Vector3.up * Mathf.Min(WallProbeHeight, spot.Headroom * 0.5f);
            if (!VoxelSurfaceSnap.TryRaySurface(from, direction, WallReach, out var wall, out float distance)) return false;
            var normal = VoxelSurfaceSnap.SurfaceNormal(wall, -direction);
            if (Mathf.Abs(normal.y) > WallFacing || !IsRoofed(wall, normal)) return false;
            float scale = anchor.Scalable
                ? Mathf.Lerp(CaveContentConfig.WallScaleMin, CaveContentConfig.WallScaleMax, (float)rng.NextDouble()) : 1f;
            float reach = distance * WallReachShare;
            if (anchor.Reach * scale > reach)
            {
                if (!anchor.Scalable) return false;
                scale = reach / anchor.Reach;
                if (scale < MinMountedScale) return false;
            }
            if (!VoxelSurfaceSnap.TryMount(prefab, wall, normal, scale, rng, out var at, out var rotation)) return false;
            return SpawnTracked(prefab, at, rotation, scale, CaveContentKind.Wall);
        }

        // Air in front of the rock with rock above it before the ground surface: inside a cave, not open to the sky.
        private static bool IsRoofed(Vector3 surface, Vector3 normal)
        {
            var air = surface + normal * CoverProbeLift;
            if (!TryRoofLine(air, out float roof) || roof - air.y < MinCover) return false;
            for (float y = air.y + ScanStep; y < roof; y += ScanStep)
                if (IsSolid(air.x, y, air.z)) return true;
            return false;
        }

        internal static Quaternion RandomYaw(System.Random rng) => Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

        // A cave object is a ZDO written straight from the server and tagged as cave content: no instance on the server,
        // the owning client builds it like any streamed-in object.
        internal static ZDO Spawn(GameObject prefab, Vector3 at, Quaternion rotation, float scale, CaveContentKind kind)
        {
            var zdo = NetworkObjectHelper.CreateZdo(prefab, at, rotation);
            if (zdo == null) return null;
            if (scale != 1f && prefab.GetComponent<ZNetView>().m_syncInitialScale) zdo.Set(ZDOVars.s_scaleHash, prefab.transform.localScale * scale);
            zdo.Set(CaveContentKey, (int)kind);
            return zdo;
        }

        // Dressing placements; a pickable that never regrows in vanilla also goes on the restock clock.
        private static bool SpawnTracked(GameObject prefab, Vector3 at, Quaternion rotation, float scale, CaveContentKind kind)
        {
            var zdo = Spawn(prefab, at, rotation, scale, kind);
            VoxelCavePickables.Track(zdo, prefab, at, rotation, scale, kind);
            return zdo != null;
        }

        private static int ClearTaggedContent(Vector2s zone)
        {
            VoxelCaveRecord.RemoveRestockingInZone(zone);
            int cleared = 0;
            foreach (var zdo in ZoneObjects(zone))
            {
                if (zdo.GetInt(CaveContentKey) == 0) continue;
                DestroyServerZdo(zdo);
                cleared++;
            }
            return cleared;
        }

        // Dressing from before the record existed carried no tag: the old default prefabs, buried under the ground.
        private static int ClearLegacyContent(Vector2s zone)
        {
            if (s_legacyHashes == null)
            {
                s_legacyHashes = new HashSet<int>();
                foreach (var name in LegacyPrefabs) s_legacyHashes.Add(name.GetStableHashCode());
            }
            int cleared = 0;
            foreach (var zdo in ZoneObjects(zone))
            {
                if (!s_legacyHashes.Contains(zdo.GetPrefab()) || zdo.GetInt(CaveContentKey) != 0) continue;
                var at = zdo.GetPosition();
                if (!VoxelZoneStreamer.TryGroundHeight(at.x, at.z, out float ground) || at.y > ground - LegacyBuryDepth) continue;
                if (IsSolid(at.x, at.y + LegacyAirProbe, at.z) && IsSolid(at.x, at.y - LegacyAirProbe, at.z)) continue;
                DestroyServerZdo(zdo);
                cleared++;
            }
            return cleared;
        }

        internal static List<ZDO> ZoneObjects(Vector2s zone)
        {
            s_zoneObjects.Clear();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), s_zoneObjects);
            s_zoneObjects.RemoveAll(zdo => zdo == null);
            return s_zoneObjects;
        }

        internal static void DestroyServerZdo(ZDO zdo)
        {
            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
        }

        // The deepest roomy cavern floor may hold a FiresLiquid pour: it drapes the carved floor and floods any passage
        // below its settled level, which is where the submerged routes come from.
        private static bool TrySeedCavePool(System.Random rng)
        {
            if (!CaveContentConfig.PoolsEnabled) return false;
            var at = Vector3.zero;
            bool found = false;
            float minHeadroom = CaveContentConfig.PoolMinHeadroom;
            for (int i = 0; i < s_spots.Count; i++)
            {
                var spot = s_spots[i];
                if (spot.Island || spot.Headroom < minHeadroom || (found && spot.Floor.y >= at.y)
                    || Zones.ZoneModeStartCave.Contains(spot.Floor)) continue;
                at = spot.Floor;
                found = true;
            }
            if (!found || rng.NextDouble() >= CaveContentConfig.PoolChance) return false;
            try
            {
                if (VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.HasPourNear(at, CaveContentConfig.PoolSpacing)) return false;
                var source = new Vector3(at.x, at.y + CaveContentConfig.PoolSourceLift, at.z);
                return VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.Spawn(source, CaveContentConfig.PoolRadius) != null;
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxel] cave pool seed failed at ({at.x:0},{at.z:0}): {ex.Message}");
                return false;
            }
        }
    }

    internal enum CaveContentKind { Floor = 1, Ceiling = 2, Rubble = 3, Ore = 4, Chest = 5, Spawner = 6, Prop = 7, Wall = 8 }

    internal struct CaveDressTally
    {
        public int Floor, Ceiling, Wall, Rubble, Props, Ores, Chests, Spawners;

        public void Add(CaveDressTally other)
        {
            Floor += other.Floor; Ceiling += other.Ceiling; Wall += other.Wall; Rubble += other.Rubble; Props += other.Props;
            Ores += other.Ores; Chests += other.Chests; Spawners += other.Spawners;
        }

        public int Total => Floor + Ceiling + Wall + Rubble + Props + Ores + Chests + Spawners;

        public string Describe()
            => $"{Floor} floor pickable(s), {Ceiling} ceiling hanger(s), {Wall} wall piece(s), {Rubble} rubble rock(s), {Props} prop(s), {Ores} ore node(s), "
               + $"{Chests} chest(s), {Spawners} monster spawner(s)";
    }
}
