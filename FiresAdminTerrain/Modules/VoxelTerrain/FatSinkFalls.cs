using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // River waterfall sinkholes (FAT 0.2.802; Fire 10-06 via the lead): a sinkhole opens to the sky, a river spills over its
    // lip as a waterfall into a big grotto pond with an island of sinkhole debris in its middle, and the water leaves the pond
    // as a cave river. Two kinds:
    //   * SMALL (0.2.802, every generated world): an overflow off a river's side, the surface river keeps flowing.
    //     - where: one roll per Cell x Cell m cell ('River sinkhole chance'); the cell's jittered site must be dry land with a
    //       surface river's water (above the sea) 4-10 m away. The shaft's radius keeps at least LipRock of rock between it
    //       and the river, so the bank is never undercut and the river's water store never reaches the hole.
    //   * BIG (0.2.803; generated, converted and, from 0.2.805, planned studio worlds): the WHOLE river ends in the hole.
    //     The cuts are made at world generation by FatSinkFallCuts (before the river levels and the carve; the run ends
    //     BASIN, so the channel below is never made) and kept in firesgen_<world>_sinkfalls.txt beside the world's other
    //     FiresWorldGen files, read back on every cache load; a faithful world has none.
    //     The hole is sized from the river: shaft r = width/2 + BigShaftPad, the whole width falls as side-by-side curtains.
    //   * the hole: a shaft from the surface to a pond floor ShaftDepth down (the sanctioned break to the sky), a grotto dome
    //     round the pond (cover rule everywhere but straight up the shaft), the pond ring with a calm sheet, and the island:
    //     a debris mound in the middle standing over the water, strewn with rocks (tagged cave content, removed by record).
    //   * the waterfall: FAT curtains (FiresWaterfalls, with mist and sound) from the river's surface over the lip, falling
    //     to the pond, built on each client from the site.
    //   * the cave river: a 0.2.801 river leaves the pond on the side away from the fall.
    // `fat_sinkfall_dump` lists the sites near the camera; every built one logs "[SinkFall] …" at Debug.
    internal static class FatSinkFalls
    {
        private const string Section = "09 - World generation";
        internal static ConfigEntry<float> Chance, BigChance;

        private const float Cell = 160f, Inset = 24f, EdgeMin = 4f, EdgeMax = 10f, LipRock = 3f, ShaftMin = 3f, ShaftMax = 6f;
        private const float ShaftDepth = 28f, ShaftOver = 3f, DomeRadius = 18f, WallHeight = 10f, CapShare = 0.3f, FloorFill = 4f;
        private const float PondRadius = 12f, PondDepth = 2.5f, SheetDrop = 0.3f, IslandRadius = 4f, IslandRise = 1.2f;
        private const float PondBank = 2.5f, IslandSkirt = 2f;   // 0.2.810: banked pond + sloped island; shore ring = dome - pond - bank (3.5 m small, 3.5 m big)
        private const int IslandRings = 4;
        private const float CurtainWidth = 5f, SeaGate = 31f, RiverLength = 80f;
        private const int Directions = 16, CurtainColumns = 6;
        // 0.2.803 big kind.
        private const float BigShaftPad = 5f;
        private const float BigDepth = FatSinkFallCuts.RoomDepth, BigDome = FatSinkFallCuts.RoomDome, BigPond = 18f, BigIsland = 5f, CurtainColumnStep = 1f, SmallClear = 80f;   // 0.2.859: depth + dome shared with the planner

        internal static void Bind(ConfigFile cfg)
        {
            Chance = cfg.Bind(Section, "River sinkhole chance", 0.03f,
                new ConfigDescription("Chance per 160 m cell, where a surface river runs beside dry ground, of a sinkhole whose waterfall "
                    + "spills the river into a big grotto pond with a debris island, the water leaving as a cave river.",
                    new AcceptableValueRange<float>(0f, 1f)));
            BigChance = cfg.Bind(Section, "Big river sinkhole chance", 0.1f,
                new ConfigDescription("New worlds only (generated, converted or studio-planned): chance per river (at least 4 m wide) that the whole river ends in a "
                    + "big sinkhole, falling into a grotto pond and leaving as a cave river. The river below the hole is never made.",
                    new AcceptableValueRange<float>(0f, 1f)));
            BigFloor = cfg.Bind(Section, "Big sinkhole lowest floor", 31.5f,
                new ConfigDescription("Lowest y a big river sinkhole's pond floor may reach (0.2.856, Fire: big sinkholes may cut deeper than Cave Min Y near "
                    + "rivers, so more of them qualify). Keep it over the sea water table (y 30) plus the pond's depth.", new AcceptableValueRange<float>(30.5f, 80f)));
            FatSinkFallCuts.Chance = BigChance.Value;
            BigChance.SettingChanged += (_, __) => FatSinkFallCuts.Chance = BigChance.Value;
        }

        internal struct Site
        {
            public Vector2Int Cell;
            public Vector3 Centre;          // on the pond floor, under the shaft
            public float Shaft, RiverLevel, Surface, Top;
            public Vector2 ToRiver;         // unit, from the centre towards the river
            public Heightmap.Biome Biome;
            public string Kind;
            public float Dome, Pond, Island, CurtainW;
            public float Walls;             // 0.2.856: the chamber's wall height (0 = WallHeight; shallow big sites have shorter walls)
            public int BigIndex;            // -1 for the small kind
        }

        // ── the big kind's cuts live in FatSinkFallCuts (world generation, shared with the studio) ──────────────────────
        private static int s_seenGeneration = -1;
        private static List<FatSinkFallCuts.Cut> Big => FatSinkFallCuts.Cuts;

        // 0.2.834: the least ground height a big sinkhole needs. 0.2.837 (Fire's option (b), adaptive depth): the floor is
        // BigDepth under the ground where there is room, else as deep as Cave Min Y + pond allow, down to MinBigDepth (the dome's
        // top, WallHeight + CapShare * BigDome = 17.2 m over its floor, plus ~3 m of rock under the ground).
        private const float MinBigDepth = FatSinkFallCuts.RoomLeastDepth;   // 0.2.856: was 20; shallow sites get shorter walls (MinWalls) so the dome still sits under MinRock of rock
        private const float MinWalls = FatSinkFallCuts.RoomWallMin, MinRock = FatSinkFallCuts.RoomRock;
        private const float SinkRiverFloor = 29f;   // 0.2.856: a big sinkhole river's channel bottom limit (its water stays over the y 30.3 sea gate)
        private static ConfigEntry<float> BigFloor;
        internal static float BigFloorMin => BigFloor?.Value ?? 31.5f;
        private const float MinWall = FatSinkFallCuts.RoomRingRock;   // 0.2.845: least rock between the chamber and the generated surface, and the seal shell's thickness
        private const int WallDirs = 16;
        internal static float MinBigSurface => BigFloorMin + MinBigDepth;   // 0.2.856: its own floor limit, not Cave Min Y + pond
        private static readonly HashSet<int> s_noRoomLogged = new HashSet<int>();

        private struct BigView
        {
            public Vector2 At, Flow;
            public float Width;
            public static BigView Of(FatSinkFallCuts.Cut c) => new BigView { At = new Vector2(c.X, c.Z), Flow = new Vector2(c.FlowX, c.FlowZ), Width = c.Width };
        }

        // A new list (a plan or a cache load): the next Tick plans the big sites again.
        private static void SyncGeneration()
        {
            if (s_seenGeneration == FatSinkFallCuts.Generation) return;
            s_seenGeneration = FatSinkFallCuts.Generation;
            ResetClient();
        }

        internal static bool TryPlanBig(int index, out Site site)
        {
            site = default;
            if (index < 0 || index >= Big.Count || !FiresWorldGenBase.Active || !FiresVoxelConfig.GeneratesCaves || WorldGenerator.instance == null) return false;
            var cut = Big[index];
            var b = BigView.Of(cut);
            bool large = cut.LargeOverflow;   // 0.2.806: beside the river, which keeps flowing; (X,Z) is already the centre
            float shaft = b.Width * 0.5f + BigShaftPad;
            var c2 = large ? b.At : b.At + b.Flow * (shaft - 1f);   // big: the river's end lies just inside the shaft's upstream rim
            float surface = FiresWorldGenBase.SampleFinalMeters(c2.x, c2.y);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                surface = Mathf.Max(surface, FiresWorldGenBase.SampleFinalMeters(c2.x + Mathf.Cos(a) * shaft, c2.y + Mathf.Sin(a) * shaft));
            }
            // 0.2.834 (Fire 10-07, SinkTestGen6 big #4 at (9069,3106): "a river mesh spilling out and floating with no bank
            // containment"): on a river near the sea the floor (surface - BigDepth) clamps up to Cave Min Y + pond, at or above
            // the ground: the carve "changed nothing" and the pond sheet was laid at 37.2 over the forest. No room = no site:
            // no carve, no pond, no curtain, no shaft cuts (FatSinkFallCuts.Fits).
            if (surface < MinBigSurface)
            {
                if (s_noRoomLogged.Add(index))
                    FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[SinkFall] big #{0} at ({1:0},{2:0}) skipped: ground {3:0.0} is under {4:0.0} (Cave Min Y + pond + the least depth {5:0} m), no room for the sinkhole",
                        index, c2.x, c2.y, surface, MinBigSurface, MinBigDepth));
                return false;
            }
            // 0.2.814 (Fire 10-06: "put the waterfall at the lip of the sinkhole at the bed of the river"): a big sinkhole's curtain
            // starts at the river BED at its lip (the river's sheet stays over the shaft above it); a large-overflow keeps its level.
            float level = large ? cut.Level : FiresWorldGenBase.SampleFinalMeters(b.At.x, b.At.y);
            // 0.2.845 (Fire 10-07, 120/123/124.webp: open air through the shaft wall at y ~60): the dome needs MinWall of rock to
            // the generated surface on every side. Its top is checked against the LOWEST ground on a ring at dome r + MinWall; a
            // dome too high for that is lowered (down to Cave Min Y + pond), else the site is skipped. The shaft itself is
            // sealed by a rock shell at build (BuildSite), so a low side reads as a notch, never a window. 0.2.859: the rule is
            // FatSinkFallCuts.RoomFor, which the world-generation picker now applies before it cuts a river.
            float ringLow = float.MaxValue;
            for (int i = 0; i < WallDirs; i++)
            {
                float a = i * Mathf.PI * 2f / WallDirs;
                ringLow = Mathf.Min(ringLow, FiresWorldGenBase.SampleFinalMeters(c2.x + Mathf.Cos(a) * (BigDome + MinWall), c2.y + Mathf.Sin(a) * (BigDome + MinWall)));
            }
            if (FatSinkFallCuts.RoomFor(surface, ringLow, BigFloorMin, out float floor, out float walls) != FatSinkFallCuts.Room.Fits)
            {
                if (s_noRoomLogged.Add(index))
                    FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[SinkFall] big #{0} at ({1:0},{2:0}) skipped: the lowest ground round the dome is {3:0.0}, so its top would need to be under {4:0.0} and its floor under {5:0.0} (the lowest floor {6:0.0})",
                        index, c2.x, c2.y, ringLow, ringLow - MinWall, floor, BigFloorMin));
                return false;
            }
            site = new Site
            {
                Cell = CellOf(c2.x, c2.y), Centre = new Vector3(c2.x, floor, c2.y), Shaft = shaft, RiverLevel = level, Surface = surface,
                Top = floor + walls + CapShare * BigDome, Walls = walls, ToRiver = large ? b.Flow : -b.Flow,
                Biome = FiresCaveBiome.RegionBiome(c2.x, c2.y, WorldGenerator.instance.GetBiome(c2.x, c2.y)), Kind = large ? "large-overflow" : "big",
                Dome = BigDome, Pond = BigPond, Island = BigIsland, CurtainW = large ? shaft * 1.6f : b.Width, BigIndex = index,
            };
            return true;
        }

        private static bool NearBig(float x, float z)
        {
            foreach (var cut in Big)
                if (Vector2.Distance(new Vector2(cut.X, cut.Z), new Vector2(x, z)) < SmallClear + cut.Width * 0.5f + BigShaftPad) return true;
            return false;
        }

        // ── the small kind ──────────────────────────────────────────────────────────────────────────────────────────
        internal static bool TryPlan(Vector2Int cell, out Site site)
        {
            site = default;
            if (Chance == null || Chance.Value <= 0f || !FiresWorldGenBase.Active || !FiresVoxelConfig.GeneratesCaves || WorldGenerator.instance == null) return false;
            if (Hash01(cell, 0x5F1) >= Chance.Value) return false;
            float x = cell.x * Cell + Inset + Hash01(cell, 0x5F2) * (Cell - 2f * Inset), z = cell.y * Cell + Inset + Hash01(cell, 0x5F3) * (Cell - 2f * Inset);
            if (FiresWorldGenBase.IsWetAt(x, z) || FiresSkyIslands.TryBottomAt(x, z, out _) || NearBig(x, z)) return false;
            // The nearest surface river edge (water above the sea) 4-10 m away.
            float best = float.MaxValue, level = 0f;
            var toRiver = Vector2.zero;
            for (int k = 0; k < Directions; k++)
            {
                float a = k * Mathf.PI * 2f / Directions;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                for (float d = EdgeMin; d <= EdgeMax && d < best; d += 1f)
                    if (GenWaterField.TryWaterSurfaceSmooth(x + dir.x * d, z + dir.y * d, out float lvl, out float dep) && dep > 0f && lvl > SeaGate)
                    { best = d; level = lvl; toRiver = dir; break; }
            }
            if (best == float.MaxValue) return false;
            float shaft = Mathf.Clamp(best - LipRock, ShaftMin, ShaftMax);
            if (best - shaft < LipRock) return false;
            float surface = FiresWorldGenBase.SampleFinalMeters(x, z);
            float floor = surface - ShaftDepth;
            if (floor - PondDepth < FiresVoxelConfig.CaveMinY.Value + 1f) return false;
            float top = floor + WallHeight + CapShare * DomeRadius;
            // Cover over the dome everywhere but the shaft (the ring at the dome's edge).
            float cover = FatUndergroundLocations.Cover != null ? FatUndergroundLocations.Cover.Value : 8f;
            for (int i = 0; i < 16; i++)
            {
                float a = i * Mathf.PI * 2f / 16f;
                if (FiresWorldGenBase.SampleFinalMeters(x + Mathf.Cos(a) * (DomeRadius + 2f), z + Mathf.Sin(a) * (DomeRadius + 2f)) - top < cover) return false;
            }
            site = new Site
            {
                Cell = cell, Centre = new Vector3(x, floor, z), Shaft = shaft, RiverLevel = level, Surface = surface, Top = top, ToRiver = toRiver,
                Biome = FiresCaveBiome.RegionBiome(x, z, WorldGenerator.instance.GetBiome(x, z)), Kind = "small",
                Dome = DomeRadius, Pond = PondRadius, Island = IslandRadius, CurtainW = CurtainWidth, BigIndex = -1,
            };
            return true;
        }

        private static Vector2Int CellOf(float x, float z) => new Vector2Int(Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));

        // ── server: build the sites whose centre lies in `zone` (VoxelCaveContent's dressing pass) ─────────────────────
        internal static bool Build(Vector2s zone, System.Random rng)
        {
            bool any = false;
            for (int i = 0; i < Big.Count; i++)
                if (TryPlanBig(i, out var big) && InZone(big.Centre, zone)) any |= BuildSite(big, rng);
            var c0 = ZoneSystem.GetZonePos(zone);
            if (TryPlan(CellOf(c0.x, c0.z), out var s) && InZone(s.Centre, zone)) any |= BuildSite(s, rng);
            return any;
        }

        private static bool InZone(Vector3 p, Vector2s zone)
        {
            var home = ZoneSystem.GetZone(p);
            return home.x == zone.x && home.y == zone.y;
        }

        private static bool BuildSite(in Site s, System.Random rng)
        {
            Vector3 c = s.Centre;
            // 0.2.810: walls + cap with the floor refilled flat under them (the cap used to dig a pit under the pond: Fire's
            // "water not actually laying into the sinkhole as a real pond with banks"), then a basin with sloped banks inside a
            // flat shore ring (FatUndergroundLocations.CarveChamber / CarveBasin).
            // 0.2.845 (Fire 10-07: open air through the shaft wall at y ~60, rock above it: a generated cave tunnel or a low side
            // breaking in): first a rock SHELL MinWall thick round the whole site is filled, from under the floor up to 1 m under
            // the LOWEST ground on a ring just outside it, so no tunnel within MinWall reaches the chamber or the shaft below that
            // line; above it a genuinely low side stays an open notch, as the terrain has it. Then the chamber and shaft are carved.
            float sealR = Mathf.Max(s.Dome, s.Shaft) + MinWall, sealLow = float.MaxValue;
            for (int i = 0; i < WallDirs; i++)
            {
                float a = i * Mathf.PI * 2f / WallDirs;
                sealLow = Mathf.Min(sealLow, FiresWorldGenBase.SampleFinalMeters(c.x + Mathf.Cos(a) * sealR, c.z + Mathf.Sin(a) * sealR));
            }
            float sealBottom = c.y - PondDepth - 2f, sealTop = sealLow - 1f;
            string seal = sealTop > sealBottom
                ? VoxelWorld.ApplyBrush(new Vector3(c.x, (sealTop + sealBottom) * 0.5f, c.z), sealR, sealTop - sealBottom, VoxelBrushShape.Cylinder, VoxelBrushMode.Fill)
                : "no shell (no ground over the floor on the ring)";
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture, "[SinkFall] {0} at ({1:0},{2:0}): rock shell r {3:0.0} from {4:0.0} to {5:0.0} (lowest ground round it {6:0.0}); {7}",
                s.Kind, c.x, c.z, sealR, sealBottom, sealTop, sealLow, seal));
            var lines = FatUndergroundLocations.CarveChamber(c, s.Dome, s.Top, s.Walls > 0f ? s.Walls : WallHeight);
            if (lines[0] != null && lines[0].StartsWith("Refused")) { FiresLogger.LogDebug($"[SinkFall] {s.Kind} at ({c.x:0},{c.z:0}) skipped: {lines[0]}"); return false; }
            // 0.2.834: water only over a confirmed carve. The chamber must be open air over its floor, below the ground; else no
            // pond, no island, no curtain (a sheet over an uncarved site floats over the ground).
            float probeY = c.y + 2f;
            if (!VoxelWorld.TryIsSolid(c.x, probeY, c.z, out bool solidAtFloor) || solidAtFloor || probeY >= s.Surface - 1f)
            {
                FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                    "[SinkFall] {0} at ({1:0},{2:0}): no water laid, the chamber is not open at ({1:0},{3:0.0},{2:0}) under the ground {4:0.0}",
                    s.Kind, c.x, c.z, probeY, s.Surface));
                return false;
            }
            // 0.2.815 (Fire 10-06: a band of "missing cliff" across the shaft, seen from the pond): the shaft stopped at top - 1,
            // where the dome is far narrower than the shaft (6.9 m vs 14.1 for a big site), leaving a flat rock ledge ring under the
            // shaft. It now runs down to where the dome is as wide as the shaft, so the shaft wall meets the dome wall cleanly.
            float capCentre = s.Top - s.Dome, meet = s.Shaft < s.Dome ? capCentre + Mathf.Sqrt(s.Dome * s.Dome - s.Shaft * s.Shaft) : capCentre;
            float shaftTop = s.Surface + ShaftOver, shaftBottom = Mathf.Min(s.Top - 1f, meet - 0.5f);
            lines.Add(VoxelWorld.ApplyBrush(new Vector3(c.x, (shaftTop + shaftBottom) * 0.5f, c.z), s.Shaft, shaftTop - shaftBottom, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve));
            FatUndergroundLocations.CarveBasin(c, s.Pond, PondDepth, PondBank, lines);
            // The debris island built back up in the middle, its sides sloping under the water (IslandRings, widest at the bed).
            for (int k = 0; k < IslandRings; k++)
            {
                float t = k / (float)(IslandRings - 1);
                float r = Mathf.Lerp(s.Island + IslandSkirt, s.Island, t);
                float bottom = c.y - PondDepth - 0.5f, top = Mathf.Lerp(c.y - PondDepth * 0.5f, c.y + IslandRise, t);
                lines.Add(VoxelWorld.ApplyBrush(new Vector3(c.x, (bottom + top) * 0.5f, c.z), r, top - bottom, VoxelBrushShape.Cylinder, VoxelBrushMode.Fill));
            }
            // The sheet, centred off the island (its bed must be water-floor under the centre), covering the ring.
            var sheetAt = new Vector3(c.x - s.ToRiver.x * (s.Island + 3f), c.y - PondDepth, c.z - s.ToRiver.y * (s.Island + 3f));
            int sheet = VerdantsAscent.Water.Tools.WaterSheets.Lay(sheetAt, c.y - SheetDrop, s.Pond + s.Island + 3f, "waterfall sinkhole pond", out string refusal);
            int rubble = 0;
            var rock = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Rock_3") : null;
            for (int i = 0; rock != null && i < 4 + rng.Next(3); i++)
            {
                float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = (float)rng.NextDouble() * (s.Island - 1f);
                if (VoxelCaveContent.Spawn(rock, new Vector3(c.x + Mathf.Cos(a) * r, c.y + IslandRise, c.z + Mathf.Sin(a) * r),
                    VoxelCaveContent.RandomYaw(rng), 0.6f + (float)rng.NextDouble() * 0.5f, CaveContentKind.Grotto) != null) rubble++;
            }
            int plants = FatGrottos.PlaceRing(c, s.Pond + 1.5f, s.Dome - 3f, s.Biome, rng, out _);
            // The water leaves on the side away from the fall, as a cave river.
            float heading = Mathf.Atan2(-s.ToRiver.y, -s.ToRiver.x);
            bool river = FatCaveRivers.TryPlanFrom(c.x - s.ToRiver.x * (s.Dome - 2f), c.z - s.ToRiver.y * (s.Dome - 2f), c.y - 0.5f, heading, RiverLength, out var plan,
                    s.BigIndex >= 0 ? SinkRiverFloor : float.NaN)   // 0.2.856: a big sinkhole's river may run under Cave Min Y
                && FatCaveRivers.BuildPlan(plan);
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[SinkFall] {0} at ({1:0},{2:0}): {3} {4:0.0} m, lip y {5:0.0}, fall {6:0.0} m, shaft r {7:0.0}, pond r {8:0.0}, island top {9:0.0}, "
                + "sheet {10}{11}, {12} rubble, {13} plant(s), cave river {14}",
                s.Kind, c.x, c.z, s.Kind == "big" ? "river width" : s.Kind == "large-overflow" ? "lip width" : "river edge", s.BigIndex >= 0 ? s.CurtainW : s.Shaft + LipRock, s.RiverLevel,
                s.RiverLevel - (c.y - SheetDrop), s.Shaft, s.Pond, c.y + IslandRise,
                sheet, sheet == 0 ? $" (refused: {refusal})" : "", rubble, plants, river ? "yes" : "no"));
            return true;
        }

        // ── client: the curtains, the domes and the dump ──────────────────────────────────────────────────────────────
        private static readonly Dictionary<Vector2Int, GameObject> s_falls = new Dictionary<Vector2Int, GameObject>();
        private static readonly HashSet<Vector2Int> s_planned = new HashSet<Vector2Int>();
        private static readonly Dictionary<int, GameObject> s_bigFalls = new Dictionary<int, GameObject>();
        private static readonly HashSet<int> s_bigPlanned = new HashSet<int>();
        private static Vector2Int s_lastCell = new Vector2Int(int.MinValue, int.MinValue);
        private static readonly List<FiresWaterfalls.Column> s_columns = new List<FiresWaterfalls.Column>();
        private static readonly List<FiresWaterfallMist.Site> s_mist = new List<FiresWaterfallMist.Site>();
        private static readonly List<Vector3> s_sounds = new List<Vector3>();

        // A new world's list: the next Tick plans the big sites again.
        private static void ResetClient()
        {
            s_bigPlanned.Clear();
            s_lastCell = new Vector2Int(int.MinValue, int.MinValue);
        }

        internal static void Tick(Vector3 player)
        {
            if (Chance == null || !FiresWorldGenBase.Active) return;
            SyncGeneration();
            // 0.2.809 (Fire 10-06: curtains hung over an uncut river because the server's build had failed): a site's
            // curtain and dome are drawn only once THIS client's voxels show its hole carved (air down the shaft and in the
            // chamber), re-checked every CarveCheckSeconds while the player is within a cell of it. A hole the server never
            // cut can no longer show floating falls; it logs once that it is waiting.
            var cell = CellOf(player.x, player.z);
            if (cell == s_lastCell && Time.unscaledTime < s_nextCarveCheck) return;
            s_lastCell = cell;
            s_nextCarveCheck = Time.unscaledTime + CarveCheckSeconds;
            string env = FatGrottos.Environment != null ? FatGrottos.Environment.Value : "Clear";
            // 0.2.813 (Fire 10-06: a hard dark/bright cut inside the sinkhole): the DOME (its environment, blended at its edge)
            // is registered as soon as the site is planned, the whole chamber shaft included; only the curtain waits for the
            // carve. It used to wait too, so while the curtain was held the chamber flipped between cave and open air.
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var c = new Vector2Int(cell.x + dx, cell.y + dz);
                    if (s_planned.Contains(c) || !TryPlan(c, out var site)) continue;
                    FatUndergroundLocations.AddDome(site.Centre, site.Dome, site.Top, env, true);
                    if (!Carved(site)) continue;
                    s_planned.Add(c);
                    var go = BuildCurtain(site);
                    if (go != null) s_falls[c] = go;
                    LogDrawn(site);
                }
            for (int i = 0; i < Big.Count; i++)
            {
                var at = new Vector2(Big[i].X, Big[i].Z);
                var bc = CellOf(at.x, at.y);
                if (Mathf.Abs(bc.x - cell.x) > 1 || Mathf.Abs(bc.y - cell.y) > 1 || s_bigPlanned.Contains(i) || !TryPlanBig(i, out var site)) continue;
                FatUndergroundLocations.AddDome(site.Centre, site.Dome, site.Top, env, true);
                if (!Carved(site)) continue;
                s_bigPlanned.Add(i);
                var go = BuildCurtain(site);
                if (go != null) s_bigFalls[i] = go;
                LogDrawn(site);
            }
        }

        private static void LogDrawn(in Site s)
            => FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture, "[SinkFall] {0} at ({1:0},{2:0}): carved, curtain drawn", s.Kind, s.Centre.x, s.Centre.z));

        private const float CarveCheckSeconds = 2f;
        private static float s_nextCarveCheck;

        // Air in this client's voxels at the shaft (3 m under the surface) and in the chamber (3 m over the pond floor).
        // 0.2.812 (Fire 10-06: a curtain drawn over a half-carved sinkhole, "they just don't go into a sinkhole"): the whole
        // site must be there before its curtain and dome are drawn. The shaft is air every CarveShaftStep m from under the
        // surface down to the chamber's top; the chamber is air over the floor; the shore ring's floor is solid (no pit under
        // it) with air above it; the pond basin is open below the floor; the island's top is solid. A partial carve (0.2.809's
        // build died after the first fill) fails one of these, logs which, and stays held.
        private const float CarveShaftStep = 4f;

        private static bool Carved(in Site s) => CarvedWhy(s, out _);

        // 0.2.813: the reason is returned (the dump prints it) and logged whenever it CHANGES, not only the first time.
        private static readonly Dictionary<Vector3Int, string> s_lastWhy = new Dictionary<Vector3Int, string>();

        private static bool CarvedWhy(in Site s, out string missing)
        {
            var c = s.Centre;
            var away = new Vector2(-s.ToRiver.x, -s.ToRiver.y);
            float shore = s.Pond + PondBank + 1.5f, pond = (s.Island + IslandSkirt + s.Pond) * 0.5f;
            missing = null;
            bool known = true;
            bool Air(float x, float y, float z) { if (!VoxelWorld.TryIsSolid(x, y, z, out bool solid)) { known = false; return false; } return !solid; }
            bool Rock(float x, float y, float z) { if (!VoxelWorld.TryIsSolid(x, y, z, out bool solid)) { known = false; return false; } return solid; }
            for (float y = s.Surface - 2f; y > s.Top - 1f && missing == null; y -= CarveShaftStep)
                if (!Air(c.x, y, c.z)) missing = string.Format(CultureInfo.InvariantCulture, "shaft closed at y {0:0.0}", y);
            if (missing == null && !Air(c.x + away.x * pond, c.y + 3f, c.z + away.y * pond)) missing = "chamber not open";
            // 0.2.814: the shore is checked ACROSS the flow, either side: downstream ("away") is where the cave river leaves the
            // chamber (its channel starts at Dome - 2 = the shore radius), so the old sample hit the river and read a pit (Fire's round 4).
            var across = new Vector2(-s.ToRiver.y, s.ToRiver.x);
            if (missing == null && !Rock(c.x + across.x * shore, c.y - 0.5f, c.z + across.y * shore) && !Rock(c.x - across.x * shore, c.y - 0.5f, c.z - across.y * shore)) missing = "no shore floor (pit under it)";
            if (missing == null && !Air(c.x + across.x * shore, c.y + 1.5f, c.z + across.y * shore) && !Air(c.x - across.x * shore, c.y + 1.5f, c.z - across.y * shore)) missing = "shore buried";
            if (missing == null && !Air(c.x + away.x * pond, c.y - PondDepth * 0.5f, c.z + away.y * pond)) missing = "no pond basin";
            if (missing == null && !Rock(c.x, c.y + IslandRise - 0.6f, c.z)) missing = "no island";
            if (missing == null) return true;
            var key = new Vector3Int(Mathf.RoundToInt(c.x), 0, Mathf.RoundToInt(c.z));
            if (!known) missing = "voxels not loaded here yet";
            if (!s_lastWhy.TryGetValue(key, out string before) || before != missing)
            {
                s_lastWhy[key] = missing;
                FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                    "[SinkFall] {0} at ({1:0},{2:0}): curtain held, the hole is not (fully) carved here: {3}", s.Kind, c.x, c.z, missing));
            }
            return false;
        }

        // 0.2.817 (Fire 10-07: from above, the big sinkhole's curtain and foam drew ON TOP of the river sheet): the curtain's
        // queues (3002/3003) come after the water sheet's (3000), so it always painted over the water. A big sinkhole's curtain
        // hangs UNDER the river surface, so its own copies of the materials sit just before the water (UnderRiverQueueShift
        // lower): the river sheet then draws over it from above, while from inside the shaft it shows as before. Other
        // curtains keep the shared materials (a riverside fall with water behind it must stay in front).
        private const int UnderRiverQueueShift = 4;
        private const float MinCurtainFall = 1f;   // 0.2.834
        private const float SinkPlunge = 0.05f;   // 0.2.820
        private const float WhatsAtRange = 80f;
        private const int WhatsAtListed = 12;

        private static string PathOf(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
        private static Material[] s_underRiver;

        // 0.2.840: fog mode and the light pass's brightness, from FiresWaterfalls' sync.
        internal static void SyncUnderRiver(float fogMode, float light)
        {
            if (s_underRiver == null) return;
            for (int i = 0; i < s_underRiver.Length; i++)
            {
                if (s_underRiver[i] == null) continue;
                s_underRiver[i].SetFloat("_FiresFogMode", fogMode);
                if (i == s_underRiver.Length - 1) s_underRiver[i].SetFloat("_LightScale", light);   // the last is the light pass
            }
        }

        internal static Material[] UnderRiverMaterials()
        {
            var shared = FiresWaterfalls.Materials;
            if (shared == null) return null;
            if (s_underRiver != null && s_underRiver.Length == shared.Length) return s_underRiver;
            s_underRiver = new Material[shared.Length];
            for (int i = 0; i < shared.Length; i++)
                s_underRiver[i] = shared[i] == null ? null : new Material(shared[i]) { name = shared[i].name + "_UnderRiver", renderQueue = shared[i].renderQueue - UnderRiverQueueShift };
            return s_underRiver;
        }

        private static GameObject BuildCurtain(in Site s)
        {
            if (FiresWaterfalls.Materials == null) return null;
            var rim = new Vector3(s.Centre.x + s.ToRiver.x * s.Shaft, 0f, s.Centre.z + s.ToRiver.y * s.Shaft);
            var across = new Vector3(-s.ToRiver.y, 0f, s.ToRiver.x);
            var outward = new Vector3(-s.ToRiver.x, 0f, -s.ToRiver.y);   // from the river into the shaft
            // 0.2.820 (Fire 10-07: seen sideways through the pond, the curtain glowed "super bright"): it plunged PlungeDepth (0.7 m)
            // under the pond surface, and that part (additive pass included) shone through the translucent pond. It now ends
            // SinkPlunge under the surface: the pond's mist hides the meeting line.
            float fall = s.RiverLevel - (s.Centre.y - SheetDrop - SinkPlunge);
            // 0.2.834 (Fire 10-07, 104/105.webp: flat bright white shards at the waterline): SinkTestGen6's big at (9069,3106) had
            // "fall -6.5 m", its pond above its lip, and the curtain was built inverted into flat sheets. No real drop, no curtain.
            if (fall < MinCurtainFall)
            {
                FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture, "[SinkFall] {0} at ({1:0},{2:0}): no curtain, fall {3:0.0} m (under {4:0.0})",
                    s.Kind, s.Centre.x, s.Centre.z, fall, MinCurtainFall));
                return null;
            }
            FiresWaterfalls.BeginMesh();
            s_mist.Clear();
            s_sounds.Clear();
            // 0.2.809 (Fire 10-06: the big sinkhole's curtain "splits into ribbons"): the full width is ONE curtain, a column
            // every CurtainColumnStep m, so it is one welded ribbon with one texture run (it was side-by-side 6 m curtains,
            // each with its own rounded ends, fades and seed: the seams and shards).
            int count = Mathf.Max(CurtainColumns, Mathf.CeilToInt(s.CurtainW / CurtainColumnStep) + 1);
            s_columns.Clear();
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1) - 0.5f;
                s_columns.Add(new FiresWaterfalls.Column { Base = rim + across * (t * s.CurtainW), LipY = s.RiverLevel, FallLength = fall });
            }
            if (FiresWaterfalls.AppendCurtain(s_columns, outward, FiresWaterfalls.EdgeLipOut, 0f, Hash01(s.Cell, 0x5F9) * 100f, null, approach: false))
                s_mist.Add(FiresWaterfalls.LastSite);
            s_sounds.Add(FiresWaterfalls.SoundSite(s_columns, outward));
            var go = new GameObject(s.BigIndex >= 0 ? $"FatSinkFallBig_{s.BigIndex}" : $"FatSinkFall_{s.Cell.x}_{s.Cell.y}");
            var mesh = new Mesh { name = go.name };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            // 0.2.834 (Fire 10-07, 104.webp: a sinkhole curtain bright white THROUGH the water surface): every kind, not only "big".
            renderer.sharedMaterials = UnderRiverMaterials();
            int vertices = FiresWaterfalls.EndMesh(mesh);
            go.SetActive(vertices > 0);
            FiresWaterfalls.SetSounds(go.transform, s_sounds);
            FiresWaterfallMist.SetEmitters(go.transform, s_mist);
            return go;
        }

        internal static void RegisterDump()
        {
            // 0.2.820: `fat_whatsat` (read-only): what the crosshair is on. The physics hit (collider, its object path, distance,
            // point, the voxel solid/air there), then EVERY renderer whose bounds the view ray crosses within WhatsAtRange m,
            // nearest first, with its path, material and shader: a stray quad (mist, a water tile, a grab-pass plane) has no
            // collider, so the ray alone would miss it. For Fire's phantom rectangle in the sinkhole wall.
            new Terminal.ConsoleCommand("fat_whatsat", "- what the crosshair is on: physics hit + every renderer the view ray crosses (read-only)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    var camera = global::Utils.GetMainCamera();
                    if (camera == null) return;
                    var ray = new Ray(camera.transform.position, camera.transform.forward);
                    var sb = new System.Text.StringBuilder(string.Format(inv, "[WhatsAt] from ({0:0.0},{1:0.0},{2:0.0}) dir ({3:0.00},{4:0.00},{5:0.00}):",
                        ray.origin.x, ray.origin.y, ray.origin.z, ray.direction.x, ray.direction.y, ray.direction.z));
                    if (Physics.Raycast(ray, out var hit, WhatsAtRange, ~0, QueryTriggerInteraction.Ignore))
                    {
                        bool known = VoxelWorld.TryIsSolid(hit.point.x, hit.point.y - 0.3f, hit.point.z, out bool solid);
                        sb.Append(string.Format(inv, "\n  hit {0} '{1}' at {2:0.0} m, point ({3:0.0},{4:0.0},{5:0.0}), normal ({6:0.00},{7:0.00},{8:0.00}); voxel just inside: {9}",
                            hit.collider.GetType().Name, PathOf(hit.collider.transform), hit.distance, hit.point.x, hit.point.y, hit.point.z,
                            hit.normal.x, hit.normal.y, hit.normal.z, known ? (solid ? "SOLID" : "AIR") : "not loaded"));
                    }
                    else sb.Append("\n  no physics hit");
                    var crossed = new List<KeyValuePair<float, Renderer>>();
                    foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                    {
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                        if (r.bounds.IntersectRay(ray, out float d) && d <= WhatsAtRange) crossed.Add(new KeyValuePair<float, Renderer>(d, r));
                    }
                    crossed.Sort((a, b) => a.Key.CompareTo(b.Key));
                    for (int i = 0; i < crossed.Count && i < WhatsAtListed; i++)
                    {
                        var r = crossed[i].Value;
                        var m = r.sharedMaterial;
                        sb.Append(string.Format(inv, "\n  renderer {0:0.0} m: '{1}' {2}, material '{3}', shader '{4}', queue {5}, bounds {6}",
                            crossed[i].Key, PathOf(r.transform), r.GetType().Name, m != null ? m.name : "none",
                            m != null && m.shader != null ? m.shader.name : "none", m != null ? m.renderQueue : -1, r.bounds.size));
                    }
                    sb.Append(string.Format(inv, "\n  ({0} renderer(s) crossed within {1:0} m)", crossed.Count, WhatsAtRange));
                    FiresLogger.LogInfo(sb.ToString());
                    args.Context?.AddString($"[WhatsAt] {crossed.Count} renderer(s) on the ray; the list is in the BepInEx log.");
                });
            // 0.2.817: `fat_mesh_holes [radius=64]` (read-only): every loaded Full voxel chunk within radius of the camera whose
            // border ring disagrees with its live neighbours (VoxelChunk.MirrorBordersNow audit, nothing written). A stale ring
            // is what tore the band in the sinkhole shaft at y 64 (0.2.813); its mesh stops short of the neighbour's. One Info
            // line per chunk with a mismatch, then a total.
            new Terminal.ConsoleCommand("fat_mesh_holes", "[radius=64] - loaded voxel chunks near the camera whose border rings disagree with their neighbours (read-only)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    float radius = 64f;
                    if (args.Args.Length > 1) float.TryParse(args.Args[1], NumberStyles.Float, inv, out radius);
                    var camera = global::Utils.GetMainCamera();
                    if (camera == null) return;
                    var p = camera.transform.position;
                    int scanned = 0, bad = 0;
                    foreach (var chunk in new List<VoxelChunk>(VoxelWorld.AllChunks))
                    {
                        if (chunk == null || chunk.Kind != ChunkKind.Full) continue;
                        var centre = chunk.WorldOrigin + new Vector3(VoxelChunk.CELLS * 0.5f, VoxelChunk.CELLS * 0.5f, VoxelChunk.CELLS * 0.5f);
                        if ((centre - p).magnitude > radius + VoxelChunk.CELLS) continue;
                        scanned++;
                        int mismatched = chunk.MirrorBordersNow(false);
                        if (mismatched <= 0) continue;
                        bad++;
                        FiresLogger.LogInfo(string.Format(inv, "[MeshHoles] chunk {0} (origin {1:0},{2:0},{3:0}) v{4}: {5} ring sample(s) disagree with their owners",
                            chunk.Key, chunk.WorldOrigin.x, chunk.WorldOrigin.y, chunk.WorldOrigin.z, chunk.Version, mismatched));
                    }
                    string line = string.Format(inv, "[MeshHoles] {0} loaded chunk(s) within {1:0} m scanned, {2} with stale border rings", scanned, radius, bad);
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                });
            // 0.2.813: `fat_voxel_probe <x> <y> <z>` (read-only): what each loaded chunk holding the point says there (its own
            // sample or a border ring), with the chunk's kind, version and edit flag, and what TryIsSolid answers. For telling
            // "the server's edit never arrived" from "a stale ring answered".
            new Terminal.ConsoleCommand("fat_voxel_probe", "<x> <y> <z> - the voxel density each loaded chunk holds at a point, and the solid/air answer (read-only)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    if (args.Args.Length < 4 || !float.TryParse(args.Args[1], NumberStyles.Float, inv, out float x)
                        || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out float y) || !float.TryParse(args.Args[3], NumberStyles.Float, inv, out float z))
                    { args.Context?.AddString("fat_voxel_probe <x> <y> <z>"); return; }
                    var sb = new System.Text.StringBuilder(string.Format(inv, "[VoxelProbe] ({0:0.0},{1:0.0},{2:0.0}):", x, y, z));
                    int kx = Mathf.FloorToInt(x / VoxelChunk.CELLS), kz = Mathf.FloorToInt(z / VoxelChunk.CELLS), ky0 = Mathf.FloorToInt(Mathf.Round(y) / VoxelChunk.CELLS);
                    for (int ky = ky0 - 1; ky <= ky0 + 1; ky++)
                    {
                        if (!VoxelWorld.TryGetChunk(new Vector3Int(kx, ky, kz), out var chunk) || chunk == null) { sb.Append($" chunk ({kx},{ky},{kz}) not loaded;"); continue; }
                        sbyte d = chunk.SampleDensityWorld(new Vector3(x, Mathf.Round(y), z));
                        sb.Append(string.Format(inv, " chunk ({0},{1},{2}) {3} v{4}{5} {6}{7};", kx, ky, kz, chunk.Kind, chunk.Version, chunk.HasBrushEdits ? " edited" : "",
                            d == sbyte.MinValue ? "outside" : "density " + d, ky == ky0 ? " (owner)" : ""));
                    }
                    sb.Append(VoxelWorld.TryIsSolid(x, y, z, out bool solid) ? (solid ? " -> SOLID" : " -> AIR") : " -> unknown");
                    FiresLogger.LogInfo(sb.ToString());
                    args.Context?.AddString(sb.ToString());
                });
            new Terminal.ConsoleCommand("fat_sinkfall_dump",
                "- waterfall sinkholes within 1 km of the camera: kind, site, river edge or width, lip, fall height (read-only)",
                args =>
                {
                    var camera = global::Utils.GetMainCamera();
                    if (camera == null) return;
                    var p = camera.transform.position;
                    var centre = CellOf(p.x, p.z);
                    int found = 0, reach = Mathf.CeilToInt(1000f / Cell);
                    var sb = new System.Text.StringBuilder("[SinkFall] sites within 1 km:");
                    for (int dz = -reach; dz <= reach; dz++)
                        for (int dx = -reach; dx <= reach; dx++)
                        {
                            if (!TryPlan(new Vector2Int(centre.x + dx, centre.y + dz), out var s)) continue;
                            found++;
                            sb.Append(string.Format(CultureInfo.InvariantCulture, "\n  {0} at ({1:0},{2:0}): river edge {3:0.0} m, lip y {4:0.0}, fall {5:0.0} m, curtain {6}",
                                s.Kind, s.Centre.x, s.Centre.z, s.Shaft + LipRock, s.RiverLevel, s.RiverLevel - (s.Centre.y - SheetDrop),
                                s_falls.ContainsKey(s.Cell) ? "drawn" : CarvedWhy(s, out string why) ? "not drawn yet" : "held: " + why));
                        }
                    for (int i = 0; i < Big.Count; i++)
                    {
                        if (!TryPlanBig(i, out var s) || new Vector2(s.Centre.x - p.x, s.Centre.z - p.z).magnitude > 1000f) continue;
                        found++;
                        sb.Append(string.Format(CultureInfo.InvariantCulture, "\n  {8} #{0} at ({1:0},{2:0}): curtain width {3:0.0} m, shaft r {4:0.0}, lip y {5:0.0}, fall {6:0.0} m, curtain {7}",
                            i, s.Centre.x, s.Centre.z, s.CurtainW, s.Shaft, s.RiverLevel, s.RiverLevel - (s.Centre.y - SheetDrop),
                            s_bigFalls.ContainsKey(i) ? "drawn" : CarvedWhy(s, out string why) ? "not drawn yet" : "held: " + why, s.Kind));
                    }
                    sb.Append($"\n  ({Big.Count} big or large-overflow sinkhole(s) in this world)");
                    FiresLogger.LogInfo(sb.ToString());
                    args.Context?.AddString($"[SinkFall] {found} site(s) within 1 km; the list is in the BepInEx log.");
                });
        }

        private static float SeedHash01(int seed, int id, int salt) => Hash01(seed, new Vector2Int(id, 0), salt);

        private static float Hash01(Vector2Int cell, int salt) => Hash01(WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0, cell, salt);

        private static float Hash01(int seed, Vector2Int cell, int salt)
        {
            unchecked
            {
                uint h = (uint)(seed * 73856093 ^ cell.x * 19349663 ^ cell.y * 83492791 ^ salt * 2654435761);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }
    }
}
