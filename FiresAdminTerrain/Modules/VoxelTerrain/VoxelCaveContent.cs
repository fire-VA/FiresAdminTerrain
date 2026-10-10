using System.Collections.Generic;
using System.Linq;
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

        // 6 (FAT 0.2.713): crypt roots hang by their top. A zone dressed at 5 is NOT re-dressed (that would clear its chests and
        // spawners too): only its own ceiling roots are re-hung, from the tag each one carries (FixHangingRoots).
        // 7 (FAT 0.2.720, Fire 10-05: "a couple different kinds of roots ... mini yggdrasil roots, the roots we have, some
        // greydwarf roots"): a wider ceiling pool. A zone dressed at 5 or 6 is NOT re-dressed either: only the ceiling hangers it
        // tagged itself (CaveContentKey = Ceiling) are re-rolled from the biome's current Ceiling list, each at its own attach
        // point (RerollHangers). Chests, spawners, ores, pickables and everything else stay as they are.
        // 8 (FAT 0.2.751, Fire 10-05: sinkholes "with the debris that 'fell in' piled up along the bottom"): each sinkhole's
        // floor gets fallen rubble, logs and stones on its mound (PlaceFellIn). A zone dressed at 5, 6 or 7 is NOT re-dressed:
        // it only gets the fallen debris of the sinkholes it holds (after its root fix / re-roll where those apply).
        // 9 (FAT 0.2.762, Fire 10-06: cave water as real carves and fills): the zone's deepest roomy floor may get a pool, a
        // carved basin with a calm sheet (TrySeedCavePool). A zone dressed at 5 to 8 is NOT re-dressed: after its own upgrade it
        // only gets that pool roll (SeedPoolInDressedZone).
        // 10 (FAT 0.2.765, Fire 10-06: YggdrasilRoot "turned 180 when on the cealing ... with the ends buried a bit in the mesh"):
        // the root hangs by its bottom (VoxelSurfaceSnap). A zone dressed at 7 to 9 re-mounts only its own tagged ceiling
        // YggdrasilRoots at their ceiling points (RehangYggdrasil); 5 and 6 re-roll every hanger anyway, with the new mount.
        internal const int ContentVersion = 10;
        private const int RehangFrom = 9;
        private const int PoolFrom = 8;
        private static readonly int s_yggdrasilHash = "YggdrasilRoot".GetStableHashCode();
        private const int PoolSalt = 0x5C61;
        private const int RootFixFrom = 5;
        private const int RerollFrom = 6;
        private const int FellInFrom = 7;
        private static readonly HashSet<int> s_rootHashes = new HashSet<int>
            { "root07".GetStableHashCode(), "root08".GetStableHashCode(), "root11".GetStableHashCode(), "root12".GetStableHashCode() };
        // The hangers whose every mount gets a Debug evidence line (pose, attach side, model up, length, scale).
        private static readonly HashSet<int> s_loggedHangers = new HashSet<int>
        {
            "root07".GetStableHashCode(), "root08".GetStableHashCode(), "root11".GetStableHashCode(), "root12".GetStableHashCode(),
            "Greydwarf_Root".GetStableHashCode(), "YggdrasilRoot".GetStableHashCode(),
        };
        // A hanger taller than the cave is shrunk to between this share of the room it has and all of it, so a pool of giant
        // prefabs (YggdrasilRoot) gives mini roots of different lengths; one that would end shorter than MinHangLength is skipped.
        private const float ShrunkShareMin = 0.55f;
        private const float MinHangLength = 0.8f;
        private static readonly HashSet<int> s_reportedHangers = new HashSet<int>();
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
            if (recorded && version == RehangFrom)
            {
                if (!GroundReady(zone)) { s_waiting.Add(zone); return; }
                s_done.Add(zone);
                s_waiting.Remove(zone);
                int rehungYgg = RehangYggdrasil(zone, rng, out int droppedYgg);
                VoxelCaveRecord.MarkZoneDressed(zone, ContentVersion);
                if (rehungYgg + droppedYgg > 0)
                    FiresLogger.LogDebug($"[Caves] zone ({zone.x},{zone.y}) v{version} -> v{ContentVersion}: {rehungYgg} YggdrasilRoot(s) re-hung by their ends, "
                        + $"{droppedYgg} removed (no fit); nothing else touched");
                return;
            }
            if (recorded && version == PoolFrom)
            {
                if (!GroundReady(zone) || Survey(zone, new System.Random(rng.Next() ^ PoolSalt)) > 0) { s_waiting.Add(zone); return; }
                s_done.Add(zone);
                s_waiting.Remove(zone);
                bool laid = SeedPoolInDressedZone(zone, seed);
                RehangYggdrasil(zone, rng, out _);   // 0.2.765: the v10 root mount
                VoxelCaveRecord.MarkZoneDressed(zone, ContentVersion);
                if (laid) FiresLogger.LogDebug($"[Caves] zone ({zone.x},{zone.y}) v{version} -> v{ContentVersion}: a pool carved and filled; nothing else touched");
                return;
            }
            if (recorded && version == FellInFrom)
            {
                if (!GroundReady(zone)) { s_waiting.Add(zone); return; }
                s_done.Add(zone);
                s_waiting.Remove(zone);
                int fell = PlaceFellIn(zone, rng);
                if (Survey(zone, new System.Random(rng.Next() ^ PoolSalt)) == 0) SeedPoolInDressedZone(zone, seed);   // 0.2.762: the v9 pool roll
                RehangYggdrasil(zone, rng, out _);   // 0.2.765: the v10 root mount
                VoxelCaveRecord.MarkZoneDressed(zone, ContentVersion);
                if (fell > 0) FiresLogger.LogDebug($"[Caves] zone ({zone.x},{zone.y}) v{version} -> v{ContentVersion}: {fell} fallen piece(s) on its sinkhole floor(s); nothing else touched");
                return;
            }
            if (recorded && (version == RootFixFrom || version == RerollFrom))
            {
                if (!GroundReady(zone)) { s_waiting.Add(zone); return; }
                s_done.Add(zone);
                s_waiting.Remove(zone);
                int rehung = 0, dropped = 0;
                if (version == RootFixFrom) rehung = FixHangingRoots(zone, rng, out dropped);
                int rerolled = RerollHangers(zone, rng, out int rerollDropped, out string mix);
                PlaceFellIn(zone, rng);
                if (Survey(zone, new System.Random(rng.Next() ^ PoolSalt)) == 0) SeedPoolInDressedZone(zone, seed);   // 0.2.762: the v9 pool roll
                VoxelCaveRecord.MarkZoneDressed(zone, ContentVersion);
                if (rehung + dropped + rerolled + rerollDropped > 0)
                {
                    string line = $"[Caves] zone ({zone.x},{zone.y}) v{version} -> v{ContentVersion}: "
                        + (version == RootFixFrom ? $"{rehung} ceiling root(s) re-hung by their top, {dropped} removed (no fit); " : "")
                        + $"{rerolled} ceiling hanger(s) re-rolled ({mix}), {rerollDropped} removed (no fit); nothing else touched";
                    FiresLogger.LogDebug(line);
                    Remember(RerollEvidence, line);
                }
                return;
            }
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
            tally.Rubble += PlaceFellIn(zone, rng);
            bool pooled = TrySeedCavePool(rng);
            // 0.2.809: each feature is isolated, with its stack, so one that throws no longer skips the others. 0.2.810: a zone
            // where one threw is NOT recorded as dressed, so the next start dresses it again (it cleared and redoes its content).
            bool featureThrew = false;
            try { if (FatGrottos.Build(zone, rng)) s_reportGrottos++; }   // 0.2.800: a grotto in this zone (seeded plan; cover rule)
            catch (System.Exception ex) { FiresLogger.LogWarning($"[Grotto] ({zone.x},{zone.y}) build threw: {ex}"); featureThrew = true; }
            try { FatCaveRivers.Build(zone); }   // 0.2.801: the underground river that starts in this zone, if its block has one
            catch (System.Exception ex) { FiresLogger.LogWarning($"[CaveRiver] ({zone.x},{zone.y}) build threw: {ex}"); featureThrew = true; }
            try { FatSinkFalls.Build(zone, rng); }   // 0.2.802: a river waterfall sinkhole whose pond lies in this zone
            catch (System.Exception ex) { FiresLogger.LogWarning($"[SinkFall] ({zone.x},{zone.y}) build threw: {ex}"); featureThrew = true; }

            if (!featureThrew) VoxelCaveRecord.MarkZoneDressed(zone, ContentVersion);
            else FiresLogger.LogWarning($"[Caves] zone ({zone.x},{zone.y}) left undressed in the record: a feature build threw, it is dressed again on the next start");
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

        // ── Fallen debris on a sinkhole's floor (0.2.751) ────────────────────────────────────────────────────────────────
        // The zone that holds a sinkhole's site dresses it: on the floor's mound (VoxelCaveNoise.SinkholeSdf), FellInMin to
        // FellInMax pieces within the shaft, each a rubble rock from the biome's cave table, a fallen old log, or a rock.
        // Server ZDOs, tagged Rubble like the rest of the cave dressing, so clients see what the server placed.
        private const int FellInMin = 5, FellInMax = 10;
        private static readonly string[] FellInLogs = { "FirTree_oldLog" };
        private static readonly string[] FellInRocks = { "Rock_3", "Rock_4" };

        private static int PlaceFellIn(Vector2s zone, System.Random rng)
        {
            var centre = ZoneSystem.GetZonePos(zone);
            var seen = new HashSet<long>();
            int placed = 0;
            for (float dz = -32f; dz <= 32f; dz += 16f)
                for (float dx = -32f; dx <= 32f; dx += 16f)
                {
                    if (!VoxelCaveNoise.TrySinkholeAt(centre.x + dx, centre.z + dz, out var site)) continue;
                    if (ZoneSystem.GetZone(new Vector3(site.X, 0f, site.Z)) != zone) continue;   // its own zone dresses it
                    long key = ((long)Mathf.RoundToInt(site.X) << 32) ^ (uint)Mathf.RoundToInt(site.Z);
                    if (!seen.Add(key)) continue;
                    placed += DressSinkholeFloor(site, rng);
                }
            return placed;
        }

        private static int DressSinkholeFloor(in VoxelCaveNoise.SinkholeSite site, System.Random rng)
        {
            if (!VoxelZoneStreamer.TryGroundHeight(site.X, site.Z, out float ground)) return 0;
            float ax = site.X + site.LeanX * site.Floor, az = site.Z + site.LeanZ * site.Floor;
            var biome = WorldGenerator.instance != null ? FiresCaveBiome.RegionBiome(ax, az, WorldGenerator.instance.GetBiome(ax, az)) : Heightmap.Biome.Meadows;
            var table = CaveContentConfig.For(biome);
            int count = FellInMin + rng.Next(FellInMax - FellInMin + 1), placed = 0;
            for (int i = 0; i < count; i++)
            {
                float a = (float)(rng.NextDouble() * Mathf.PI * 2.0), d = site.Radius * 0.7f * Mathf.Sqrt((float)rng.NextDouble());
                float x = ax + Mathf.Cos(a) * d, z = az + Mathf.Sin(a) * d;
                float top = ground - site.Floor + site.Mound + 3f, bottom = ground - site.Floor - 6f;
                if (!VoxelWorld.TrySampleSurfaceY(x, z, top, bottom, out float y, out _)) continue;
                double roll = rng.NextDouble();
                GameObject prefab = null;
                if (roll < 0.45 && table != null) table.Rubble.TryPick(rng, out prefab);
                else if (roll < 0.7) prefab = ZNetScene.instance.GetPrefab(FellInLogs[rng.Next(FellInLogs.Length)]);
                if (prefab == null) prefab = ZNetScene.instance.GetPrefab(FellInRocks[rng.Next(FellInRocks.Length)]);
                if (prefab == null) continue;
                float scale = 0.6f + (float)rng.NextDouble() * 0.5f;
                if (Spawn(prefab, new Vector3(x, y - 0.15f, z), RandomYaw(rng), scale, CaveContentKind.Rubble) != null) placed++;
            }
            return placed;
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
                        AddSpots(wx, wz, ground, generator != null ? FiresCaveBiome.RegionBiome(wx, wz, generator.GetBiome(wx, wz)) : Heightmap.Biome.Meadows, false);
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

        private const double RareStandZoneChance = 0.35;
        // The core stands (Surtling, Black, Molten): rare singles.
        private static bool IsRareStand(GameObject prefab) => prefab != null && prefab.name.IndexOf("CoreStand", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsMushroom(GameObject prefab) => prefab != null && prefab.name.IndexOf("Mushroom", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static float Jitter(System.Random rng) => (float)(rng.NextDouble() * 2.0 - 1.0) * SampleJitter;

        private static void DressSmallItems(System.Random rng, ref CaveDressTally tally)
        {
            float density = CaveContentConfig.Density;
            if (density <= 0f) return;
            int mushrooms = 0, stands = 0;
            bool standZone = rng.NextDouble() < RareStandZoneChance;
            for (int i = 0; i < s_spots.Count; i++)
            {
                var spot = s_spots[i];
                var table = CaveContentConfig.For(spot.Biome);
                if (table == null) continue;

                // 0.2.829: mushrooms come only from the dark-floor roll below, never from the Floor list.
                if (rng.NextDouble() < CaveContentConfig.FloorChance * density
                    && table.Floor.TryPick(rng, p => !IsMushroom(p) && (!IsRareStand(p) || (stands == 0 && standZone)), out var floorPrefab))
                {
                    // 0.2.838 (Fire 10-07, 116.webp: Surtling core stands "keep spawning in rows of 3 ... these should be a bit
                    // rare"): a core stand is a clump of 1 (small props clump 1-3 round one spot) and at most one per zone, in
                    // only RareStandZoneChance of the zones.
                    bool rare = IsRareStand(floorPrefab);
                    if (rare) stands++;
                    int clump = !rare && VoxelSurfaceSnap.Anchor(floorPrefab).Radius <= ClumpableRadius ? 1 + rng.Next(MaxClump) : 1;
                    for (int c = 0; c < clump; c++)
                    {
                        float x = spot.Floor.x + (c == 0 ? 0f : (float)(rng.NextDouble() * 2.0 - 1.0) * ClumpSpread);
                        float z = spot.Floor.z + (c == 0 ? 0f : (float)(rng.NextDouble() * 2.0 - 1.0) * ClumpSpread);
                        float y = c == 0 ? spot.Floor.y : SettleToFloor(x, z, spot.Floor.y, spot.Floor.y + spot.Headroom);
                        if (TryPlaceOnFloor(floorPrefab, new Vector3(x, y, z), 1f, rng, CaveContentKind.Floor)) tally.Floor++;
                    }
                }

                // 0.2.829 (Fire 10-07): pickable mushrooms where it is dark, (1 - light)^2, at most 'Dark mushrooms per zone' clumps.
                if (mushrooms < CaveContentConfig.MushroomsPerZone && table.Dark != null)
                {
                    float dark = 1f - CaveLight.Static(spot.Floor);
                    if (rng.NextDouble() < CaveContentConfig.MushroomChance * density * dark * dark && table.Dark.TryPick(rng, out var mushroom))
                    {
                        mushrooms++;
                        int clump = 1 + rng.Next(MaxClump);
                        for (int c = 0; c < clump; c++)
                        {
                            float x = spot.Floor.x + (c == 0 ? 0f : (float)(rng.NextDouble() * 2.0 - 1.0) * ClumpSpread);
                            float z = spot.Floor.z + (c == 0 ? 0f : (float)(rng.NextDouble() * 2.0 - 1.0) * ClumpSpread);
                            float y = c == 0 ? spot.Floor.y : SettleToFloor(x, z, spot.Floor.y, spot.Floor.y + spot.Headroom);
                            if (TryPlaceOnFloor(mushroom, new Vector3(x, y, z), 1f, rng, CaveContentKind.Floor)) tally.Floor++;
                        }
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
            return HangAt(prefab, anchor, ceiling, normal, spot.Floor.y, rng);
        }

        // One hanger on the ceiling point over a floor at floorY: its own size when it fits, else (when the prefab syncs its
        // scale) shrunk from its bounds to 55-100 % of the room it has; skipped when that leaves it shorter than MinHangLength.
        private static bool HangAt(GameObject prefab, SurfaceAnchor anchor, Vector3 ceiling, Vector3 normal, float floorY, System.Random rng)
        {
            float room = (ceiling.y - floorY - HangerKeepClear) / -normal.y;
            ReportHanger(prefab, anchor, room);
            if (room <= 0f) return false;
            float scale = 1f;
            if (anchor.Reach > room)
            {
                if (!anchor.Scalable) return false;
                scale = room / anchor.Reach * Mathf.Lerp(ShrunkShareMin, 1f, (float)rng.NextDouble());
                if (anchor.Reach * scale < MinHangLength) return false;
            }
            if (!VoxelSurfaceSnap.TryMount(prefab, ceiling, normal, scale, rng, out var at, out var rotation)) return false;
            if (s_loggedHangers.Contains(prefab.name.GetStableHashCode())) LogRoot(prefab.name, at, ceiling, normal, rotation, anchor, scale);
            return SpawnTracked(prefab, at, rotation, scale, CaveContentKind.Ceiling);
        }

        // Once per prefab and session: how the pool reads it (the ObjectDB export has no bounds, so this is the evidence that a
        // pool prefab can hang at all: an unscalable one longer than a cave is tall never will).
        private static void ReportHanger(GameObject prefab, SurfaceAnchor anchor, float room)
        {
            if (!s_reportedHangers.Add(prefab.name.GetStableHashCode())) return;
            string line = $"[Caves] ceiling pool: {prefab.name} attaches by its {anchor.Side}, {anchor.Reach:0.00} m long, "
                + $"{anchor.Radius:0.00} m wide, {(anchor.Scalable ? "scalable" : "NOT scalable (its own size or nothing)")}; first cave room {room:0.00} m";
            FiresLogger.LogDebug(line);
            Remember(PoolEvidence, line);
        }

        // This session's [Caves] evidence lines, for caves_test (CavesTest asks the server for them over an RPC).
        internal static readonly List<string> PoolEvidence = new List<string>();
        internal static readonly List<string> RerollEvidence = new List<string>();
        private const int EvidenceCap = 40;

        private static void Remember(List<string> into, string line)
        {
            lock (into) { if (into.Count < EvidenceCap) into.Add(line); }
        }

        // A zone dressed at 5 or 6: every ceiling hanger it tagged itself (CaveContentKey = Ceiling) is re-rolled from the biome's
        // current Ceiling list at its own attach point, recovered from its pose by the face it was hung with; the new pick
        // follows the hang rule above, or the spot is left empty when it does not fit. Found by tag, never by a radius; nothing
        // but tagged ceiling hangers is touched.
        // A zone dressed at 7 to 9: each ceiling YggdrasilRoot it tagged itself is taken off and mounted again at its own ceiling
        // point by the current attach side (0.2.765: its bottom, so the ends bury in the rock and the arch hangs down). Its old
        // ceiling point is recovered from its pose by the face it was hung with (its top). One that no longer fits is removed;
        // nothing but tagged ceiling YggdrasilRoots is touched.
        private static int RehangYggdrasil(Vector2s zone, System.Random rng, out int dropped)
        {
            dropped = 0;
            int rehung = 0;
            var roots = new List<ZDO>();
            foreach (var zdo in ZoneObjects(zone))
                if (zdo.GetInt(CaveContentKey) == (int)CaveContentKind.Ceiling && zdo.GetPrefab() == s_yggdrasilHash) roots.Add(zdo);
            if (roots.Count == 0) return 0;
            var prefab = ZNetScene.instance.GetPrefab(s_yggdrasilHash);
            if (prefab == null) return 0;
            var oldAnchor = VoxelSurfaceSnap.AnchorAs(prefab, AttachSide.Top);
            var anchor = VoxelSurfaceSnap.Anchor(prefab);
            foreach (var zdo in roots)
            {
                Vector3 pos = zdo.GetPosition();
                Quaternion rot = zdo.GetRotation();
                float oldScale = prefab.GetComponent<ZNetView>().m_syncInitialScale
                    ? zdo.GetVec3(ZDOVars.s_scaleHash, prefab.transform.localScale).y / Mathf.Max(1e-4f, prefab.transform.localScale.y) : 1f;
                Vector3 hint = pos + rot * ((oldAnchor.Valid ? oldAnchor.FaceCenter : Vector3.zero) * oldScale);
                bool placed = false;
                if (anchor.Valid && VoxelSurfaceSnap.TryRaySurface(hint + Vector3.down * 0.6f, Vector3.up, 1.6f, out var ceiling, out _))
                {
                    var normal = VoxelSurfaceSnap.SurfaceNormal(ceiling, Vector3.down);
                    if (normal.y <= -CeilingFacing
                        && VoxelSurfaceSnap.TryRaySurface(ceiling + normal * SurfaceProbeLift, Vector3.down, 40f, out var floor, out _))
                        placed = HangAt(prefab, anchor, ceiling, normal, floor.y, rng);
                }
                DestroyServerZdo(zdo);
                if (placed) rehung++; else dropped++;
            }
            return rehung;
        }

        private static int RerollHangers(Vector2s zone, System.Random rng, out int dropped, out string mix)
        {
            dropped = 0;
            int rerolled = 0;
            var picks = new Dictionary<string, int>();
            var hangers = new List<ZDO>();
            foreach (var zdo in ZoneObjects(zone))
                if (zdo.GetInt(CaveContentKey) == (int)CaveContentKind.Ceiling) hangers.Add(zdo);
            var generator = WorldGenerator.instance;
            foreach (var zdo in hangers)
            {
                var old = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (old == null) continue;
                Vector3 pos = zdo.GetPosition();
                Quaternion rot = zdo.GetRotation();
                float oldScale = old.GetComponent<ZNetView>().m_syncInitialScale
                    ? zdo.GetVec3(ZDOVars.s_scaleHash, old.transform.localScale).y / Mathf.Max(1e-4f, old.transform.localScale.y) : 1f;
                var oldAnchor = VoxelSurfaceSnap.Anchor(old);
                Vector3 hint = pos + rot * ((oldAnchor.Valid ? oldAnchor.FaceCenter : Vector3.zero) * oldScale);
                bool placed = false;
                if (VoxelSurfaceSnap.TryRaySurface(hint + Vector3.down * 0.6f, Vector3.up, 1.6f, out var ceiling, out _))
                {
                    var normal = VoxelSurfaceSnap.SurfaceNormal(ceiling, Vector3.down);
                    var biome = generator != null ? FiresCaveBiome.RegionBiome(ceiling.x, ceiling.z, generator.GetBiome(ceiling.x, ceiling.z)) : Heightmap.Biome.Meadows;
                    var table = CaveContentConfig.For(biome);
                    if (normal.y <= -CeilingFacing && table != null && table.Ceiling.TryPick(rng, out var prefab)
                        && VoxelSurfaceSnap.TryRaySurface(ceiling + normal * SurfaceProbeLift, Vector3.down, 40f, out var floor, out _))
                    {
                        var anchor = VoxelSurfaceSnap.Anchor(prefab);
                        if (anchor.Valid && HangAt(prefab, anchor, ceiling, normal, floor.y, rng))
                        {
                            placed = true;
                            picks.TryGetValue(prefab.name, out int n);
                            picks[prefab.name] = n + 1;
                        }
                    }
                }
                DestroyServerZdo(zdo);
                if (placed) rerolled++; else dropped++;
            }
            mix = picks.Count == 0 ? "none" : string.Join(", ", picks.OrderByDescending(p => p.Value).Select(p => $"{p.Key} x{p.Value}"));
            return rerolled;
        }

        // Evidence for the root orientation: where its attach face meets the ceiling, which way the ceiling faces, the pose, and
        // which way the model's own up now points (up, y ~ +1 = hanging as modelled from its top; y ~ -1 = flipped, the old
        // bottom mount) and how much of it is in the rock.
        private static void LogRoot(string name, Vector3 at, Vector3 ceiling, Vector3 normal, Quaternion rotation, SurfaceAnchor anchor, float scale)
        {
            var up = rotation * Vector3.up;
            FiresLogger.LogDebug($"[Caves] root {name} at ({at.x:0.00}, {at.y:0.00}, {at.z:0.00}) ceiling ({ceiling.x:0.00}, {ceiling.y:0.00}, {ceiling.z:0.00}) "
                + $"normal ({normal.x:0.00}, {normal.y:0.00}, {normal.z:0.00}) -> rotation {rotation.eulerAngles.x:0}/{rotation.eulerAngles.y:0}/{rotation.eulerAngles.z:0}, "
                + $"attach {anchor.Side}, model up ({up.x:0.00}, {up.y:0.00}, {up.z:0.00}), buried {VoxelSurfaceSnap.SeatDepth(anchor, scale) + anchor.Buried * scale:0.00} m, "
                + $"hangs {anchor.Reach * scale:0.00} m, scale {scale:0.00}");
        }

        // A zone dressed at version 5: its ceiling roots hang by their bottom (base in the rock, tip end in the cave). Each one is
        // found by its own tag (CaveContentKey = Ceiling) and prefab, never by a radius; its old attach point on the ceiling is
        // recovered from its pose (the bottom face centre), and it is re-hung there by its top under the same headroom rule as a
        // new hanger, or removed when it no longer fits. Nothing else in the zone is touched.
        private static int FixHangingRoots(Vector2s zone, System.Random rng, out int dropped)
        {
            dropped = 0;
            int rehung = 0;
            var roots = new List<ZDO>();
            foreach (var zdo in ZoneObjects(zone))
                if (zdo.GetInt(CaveContentKey) == (int)CaveContentKind.Ceiling && s_rootHashes.Contains(zdo.GetPrefab())) roots.Add(zdo);
            foreach (var zdo in roots)
            {
                var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab == null) continue;
                Vector3 pos = zdo.GetPosition();
                Quaternion rot = zdo.GetRotation();
                float oldScale = prefab.GetComponent<ZNetView>().m_syncInitialScale
                    ? zdo.GetVec3(ZDOVars.s_scaleHash, prefab.transform.localScale).y / Mathf.Max(1e-4f, prefab.transform.localScale.y) : 1f;
                var oldAnchor = OldBottomAnchor(prefab);
                Vector3 hint = pos + rot * (oldAnchor * oldScale);
                bool placed = false;
                if (VoxelSurfaceSnap.TryRaySurface(hint + Vector3.down * 0.6f, Vector3.up, 1.6f, out var ceiling, out _))
                {
                    var normal = VoxelSurfaceSnap.SurfaceNormal(ceiling, Vector3.down);
                    var anchor = VoxelSurfaceSnap.Anchor(prefab);
                    if (normal.y <= -CeilingFacing && anchor.Valid
                        && VoxelSurfaceSnap.TryRaySurface(ceiling + normal * SurfaceProbeLift, Vector3.down, 40f, out var floor, out _))
                    {
                        float scale = Mathf.Min(1f, (ceiling.y - floor.y - HangerKeepClear) / (anchor.Reach * -normal.y));
                        if ((scale >= 1f || (anchor.Scalable && scale >= MinMountedScale))
                            && VoxelSurfaceSnap.TryMount(prefab, ceiling, normal, scale, rng, out var at, out var rotation))
                        {
                            LogRoot(prefab.name, at, ceiling, normal, rotation, anchor, scale);
                            placed = SpawnTracked(prefab, at, rotation, scale, CaveContentKind.Ceiling);
                        }
                    }
                }
                DestroyServerZdo(zdo);
                if (placed) rehung++; else dropped++;
            }
            return rehung;
        }

        // The bottom face centre the version-5 mount put on the ceiling, in the prefab's own frame (its bounds' centre at the
        // pivot plane), as VoxelSurfaceSnap measured it for a bottom attach.
        private static Vector3 OldBottomAnchor(GameObject prefab)
        {
            var anchor = VoxelSurfaceSnap.AnchorAs(prefab, AttachSide.Bottom);
            return anchor.Valid ? anchor.FaceCenter : Vector3.zero;
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
            // 0.2.809: a prefab whose ZNetView is not on its root (or has none) threw here on any scale != 1, which killed
            // the big sinkhole's build (its rubble is scaled) and the rest of that zone's cave content.
            var nview = prefab.GetComponent<ZNetView>();
            if (scale != 1f && nview != null && nview.m_syncInitialScale) zdo.Set(ZDOVars.s_scaleHash, prefab.transform.localScale * scale);
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

        // The deepest roomy cavern floor may hold a pool. 0.2.756 (Fire 10-06: "springs ... a perf nightmare ... all other
        // rivers should be carved and filled with our custom mesh and water shader"): nothing generated pours a FiresLiquidWater
        // volume any more. 0.2.762 (Fire 10-06: "we want more than just pools in caves, we should be able to do real carves and
        // fills"): the pool is a CARVE + a CALM SHEET - a flat-bottomed basin cut PoolDepth into that floor (a voxel brush stroke,
        // saved to its chunks' anchors like any edit), then one still sheet laid at its rim by [seasons]' WaterSheets.Lay, which
        // clips the sheet to the carved bed: it never pours or spreads. Server only (the content pass is). The sheet's id is
        // deterministic, so a repeat lays nothing. Pools poured by older versions stay until va_water_cleanup_spread.
        private const float PoolDepth = 1.2f, PoolRimDrop = 0.2f, PoolRadiusShare = 0.5f, PoolMinRadius = 2f;
        private const float PoolSheetPastRim = 1f;
        private static int s_poolsLaid, s_poolsRefused;
        private static int s_reportGrottos;

        // A zone dressed before v9 (its survey just run): the pool roll alone, from its own seed so it is the same on every run.
        private static bool SeedPoolInDressedZone(Vector2s zone, int seed)
            => TrySeedCavePool(new System.Random(unchecked(zone.x * 83492791 ^ zone.y * 2971215 ^ seed ^ PoolSalt)));

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
                if (!float.IsNaN(VerdantsAscent.Water.Tools.WaterSheets.SurfaceAt(at.x, at.z, at.y - 4f, at.y + 4f))
                    || VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.HasPourNear(at, CaveContentConfig.PoolSpacing)) return false;
                float radius = Mathf.Max(PoolMinRadius, CaveContentConfig.PoolRadius * PoolRadiusShare);
                // A cylinder centred on the floor, 2 x PoolDepth tall: everything from PoolDepth under the floor up is cut, and
                // the air over the floor is untouched; the walls round the basin lose at most PoolDepth at their foot.
                string carved = VoxelWorld.ApplyBrush(at, radius, PoolDepth * 2f, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
                if (carved != null && carved.StartsWith("Refused"))
                {
                    s_poolsRefused++;
                    FiresLogger.LogDebug($"[Caves] pool at ({at.x:0.0},{at.y:0.0},{at.z:0.0}) skipped: {carved}");
                    return false;
                }
                float surface = at.y - PoolRimDrop;
                int id = VerdantsAscent.Water.Tools.WaterSheets.Lay(new Vector3(at.x, at.y - PoolDepth, at.z), surface,
                    radius + PoolSheetPastRim, "cave pool", out string refusal);
                if (id == 0)
                {
                    s_poolsRefused++;
                    FiresLogger.LogDebug($"[Caves] pool at ({at.x:0.0},{at.y:0.0},{at.z:0.0}) r {radius:0.0}: basin carved ({carved}), sheet refused: {refusal}");
                    return false;
                }
                s_poolsLaid++;
                FiresLogger.LogDebug($"[Caves] pool at ({at.x:0.0},{at.y:0.0},{at.z:0.0}): basin r {radius:0.0} m, {PoolDepth:0.0} m deep ({carved}); "
                    + $"sheet {id} at {surface:0.00}. This session: {s_poolsLaid} laid, {s_poolsRefused} refused");
                return true;
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxel] cave pool at ({at.x:0},{at.z:0}) failed: {ex.Message}");
                return false;
            }
        }
    }

    internal enum CaveContentKind { Floor = 1, Ceiling = 2, Rubble = 3, Ore = 4, Chest = 5, Spawner = 6, Prop = 7, Wall = 8, Grotto = 9 }

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
