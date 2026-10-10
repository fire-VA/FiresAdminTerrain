using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Underground locations (FAT 0.2.799; Fire 10-06 via the lead: "All location types should be able to spawn underground,
    // provided the cave biome matches the location's normal biome"). When vanilla places a location (ZoneSystem.PlaceLocations,
    // a NEW placement only), a share of eligible placements goes into the cave under its spot instead:
    //   * eligible: not snap-to-water, not unique, not on the 'Surface-only locations' list, a generated cave tunnel under the
    //     spot (VoxelCaveNoise, the same on every peer), the cave region's biome (FiresCaveBiome) in the location's biome mask;
    //   * the cavern: a dome of radius (exterior + DomeExtra) whose floor is the tunnel's floor, levelled; carved by the server
    //     with the voxel brush when the location spawns (saved in the chunk anchors, synced like any voxel edit);
    //   * cover: the ground over the WHOLE dome (17 samples out to its radius + 2 m) stands at least 'Underground cover' above
    //     the dome's top, else the location is placed on the surface as before (counted, logged at Debug);
    //   * a light shaft for 'Light shaft chance' of them, only where the ground over it is dry land (not wet, above +2 m,
    //     no skyland over it): a narrow vertical cylinder from the dome's top through the surface.
    // Inside a dome the player gets the location environment of its biome ('Underground location environments'); the cave
    // environment controller (VoxelCaveEnv) blends to it through vanilla's own forced-environment cross-fade, with hysteresis.
    // Everything here is decided from the world seed and the spot, so every peer agrees; a saved surface placement stays
    // where it is (only new placements are moved).
    internal static class FatUndergroundLocations
    {
        private const string Section = "09 - World generation";
        internal static ConfigEntry<float> Share, Cover, ShaftChance, SinkholeChance;
        internal static ConfigEntry<string> SurfaceOnly, LocationEnvironments;

        private const float DomeExtra = 6f, WallHeight = 8f, CapShare = 0.3f, FloorFill = 4f, ShaftRadius = 2.5f, ShaftOver = 3f;
        private const float UndergroundMargin = 6f, TunnelTop = 12f, ScanStep = 1f, DryAltitude = 2f, SeaLevel = 30f;
        private const float RingSlack = 2f;
        private const int RingSamples = 16;

        internal static void Bind(ConfigFile cfg)
        {
            Share = cfg.Bind(Section, "Underground location share", 0.20f,
                new ConfigDescription("Share of NEW location placements that go into the cave under their spot, where the cave's biome "
                    + "matches the location and the rock cover allows. 0 = all on the surface.", new AcceptableValueRange<float>(0f, 1f)));
            Cover = cfg.Bind(Section, "Underground cover", 8f,
                new ConfigDescription("Least rock (m) between an underground location's dome and the ground above it, checked over the "
                    + "whole dome; less and the location stays on the surface.", new AcceptableValueRange<float>(4f, 40f)));
            ShaftChance = cfg.Bind(Section, "Light shaft chance (locations)", 0.10f,
                new ConfigDescription("Share of underground locations that get a narrow light shaft to the surface, where the ground over "
                    + "them is dry land.", new AcceptableValueRange<float>(0f, 1f)));
            SinkholeChance = cfg.Bind(Section, "Sinkhole entrance chance (locations)", 0.12f,
                new ConfigDescription("Share of dug underground locations (no cave under them, none within reach to connect to) that get a "
                    + "dry sinkhole entrance with a spiral ramp instead of a walkable tunnel, where the ground over them is dry land.",
                    new AcceptableValueRange<float>(0f, 1f)));
            SurfaceOnly = cfg.Bind(Section, "Surface-only locations", "StartTemple,Eikthyrnir,GDKing,Bonemass,Dragonqueen,GoblinKing,"
                + "Mistlands_DvergrBossEntrance1,FaderLocation,Vendor_BlackForest,Hildir_camp,Hildir_cave,Hildir_crypt,Hildir_plainsfortress,BogWitch_Camp",
                "Location prefab names that always stay on the surface (bosses, traders, quest places). Snap-to-water and unique "
                + "locations always stay on the surface too.");
            LocationEnvironments = cfg.Bind(Section, "Underground location environments",
                "Meadows:Clear,BlackForest:DeepForest Mist,Swamp:SunkenCrypt,Mountain:Twilight_Snow,Plains:Heath clear,"
                + "Mistlands:Mistlands_clear,AshLands:Ashlands_ashrain,DeepNorth:Twilight_Snow",
                "Environment inside an underground location's dome, by the cave's biome (Biome:Environment pairs). An unknown name "
                + "is reported at world load and falls back to 'Clear'.");
            LocationEnvironments.SettingChanged += (_, __) => s_envMap = null;
            SurfaceOnly.SettingChanged += (_, __) => s_surfaceOnly = null;
        }

        // ── the plan (pure, deterministic) ──────────────────────────────────────────────────────────────────────────
        internal struct Site
        {
            public Vector3 Centre;     // the location's position: on the dome's floor
            public float Radius, Top, SurfaceMin;
            public bool Shaft;
            public Heightmap.Biome Biome;
            public bool Dug;           // no generated cave under it: the cavern and its access tunnel are dug
            public Vector3 Entrance;   // Dug only: the tunnel's mouth / the sinkhole's surface centre / the cave floor connected to
            public Access Access;
        }

        internal enum Access { None, Connect, Sinkhole, Tunnel }

        private const float ConnectMin = 4f, ConnectMax = 80f, ConnectStep = 6f, ConnectBand = 28f, ConnectSlope = 0.7f;
        private const int ConnectDirs = 12;

        // The nearest generated cave floor around a dug cavern (ConnectMin..ConnectMax m past its wall, ConnectDirs directions,
        // within ConnectBand m up or down and no steeper than ConnectSlope): `to` is that floor. Deterministic.
        private static bool TryConnect(float x, float z, float floor, float radius, out Vector3 to)
        {
            to = default;
            float a0 = Hash01(x, z, 0x3C01) * Mathf.PI * 2f, minY = FiresVoxelConfig.CaveMinY.Value + 1f;
            for (float d = ConnectMin; d <= ConnectMax; d += ConnectStep)
                for (int t = 0; t < ConnectDirs; t++)
                {
                    float a = a0 + t * Mathf.PI * 2f / ConnectDirs, px = x + Mathf.Cos(a) * (radius + d), pz = z + Mathf.Sin(a) * (radius + d);
                    float ground = FiresWorldGenBase.SampleFinalMeters(px, pz), reach = Mathf.Min(ConnectBand, (radius + d) * ConnectSlope);
                    bool air = false;
                    for (float y = Mathf.Min(floor + reach, ground - TunnelTop); y > Mathf.Max(floor - reach, minY); y -= ScanStep)
                    {
                        bool open = VoxelCaveNoise.CarveSdf(px, y, pz, ground - y) > 0f;
                        if (open) { air = true; continue; }
                        if (!air) continue;
                        to = new Vector3(px, y + ScanStep, pz);
                        return true;
                    }
                }
            return false;
        }

        private const float SinkholeRadius = 4f, SinkholeOffset = 0.5f, RampRadius = 5.5f, RampRise = 0.45f, RampStep = 1.2f, SinkholeOver = 3f;

        // A dry sinkhole entrance: a shaft over the cavern, offset toward its wall, where the ground over it is dry land.
        private static bool TrySinkhole(float x, float z, float radius, out Vector3 centre)
        {
            float a = Hash01(x, z, 0x51C5) * Mathf.PI * 2f, off = radius * SinkholeOffset;
            float px = x + Mathf.Cos(a) * off, pz = z + Mathf.Sin(a) * off, ground = FiresWorldGenBase.SampleFinalMeters(px, pz);
            centre = new Vector3(px, ground, pz);
            return DryOver(px, pz, ground);
        }

        // Spheres along a straight line (a connector or a tunnel).
        private static int CarveLine(Vector3 from, Vector3 to, out int failed)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / TunnelStep));
            failed = 0;
            for (int i = 0; i <= n; i++)
            {
                string line = VoxelWorld.ApplyBrush(Vector3.Lerp(from, to, i / (float)n), TunnelRadius, TunnelRadius * 2f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve);
                if (line == null || line.IndexOf("fail", System.StringComparison.OrdinalIgnoreCase) >= 0) failed++;
            }
            return n + 1;
        }

        private static string CarveConnector(Vector3 floor, float radius, Vector3 cave)
        {
            var dir = new Vector2(cave.x - floor.x, cave.z - floor.z).normalized;
            var from = new Vector3(floor.x + dir.x * (radius - 1f), floor.y + TunnelRadius, floor.z + dir.y * (radius - 1f));
            int n = CarveLine(from, cave + Vector3.up * TunnelRadius, out int failed);
            return string.Format(CultureInfo.InvariantCulture, "connector {0} sphere(s) r {1:0.0} to the cave at ({2:0},{3:0},{4:0}){5}",
                n, TunnelRadius, cave.x, cave.y, cave.z, failed > 0 ? $", {failed} brush(es) reported a failure" : "");
        }

        // The shaft from the cavern's top to the surface, and a spiral ramp cut into its wall (spheres on a helix of RampRadius
        // around the axis, rising RampRise per metre): from the cavern floor up to the rim.
        private static string CarveSinkhole(Vector3 floor, float top, Vector3 rim)
        {
            float shaftTop = rim.y + SinkholeOver, shaftBottom = top - 1f;
            VoxelWorld.ApplyBrush(new Vector3(rim.x, (shaftTop + shaftBottom) * 0.5f, rim.z), SinkholeRadius, shaftTop - shaftBottom, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
            float a = Hash01(rim.x, rim.z, 0x51C6) * Mathf.PI * 2f, dA = RampStep / RampRadius;
            int n = 0, failed = 0;
            for (float y = floor.y + TunnelRadius; y < rim.y + TunnelRadius; y += RampStep * RampRise, a += dA, n++)
            {
                var p = new Vector3(rim.x + Mathf.Cos(a) * RampRadius, y, rim.z + Mathf.Sin(a) * RampRadius);
                string line = VoxelWorld.ApplyBrush(p, TunnelRadius, TunnelRadius * 2f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve);
                if (line == null || line.IndexOf("fail", System.StringComparison.OrdinalIgnoreCase) >= 0) failed++;
            }
            return string.Format(CultureInfo.InvariantCulture, "sinkhole shaft r {0:0.0} from {1:0.0} to {2:0.0} with a spiral ramp of {3} sphere(s){4}",
                SinkholeRadius, shaftBottom, shaftTop, n, failed > 0 ? $", {failed} brush(es) reported a failure" : "");
        }

        private const float DigExtra = 2f, TunnelRadius = 2.2f, TunnelRise = 0.45f, TunnelStep = 1.5f, TunnelMax = 160f;
        private const int TunnelTries = 8;

        // The access tunnel of a dug cavern: from the chamber wall at floor height, outward and up at TunnelRise (~24 deg) until
        // it meets the ground; the first of TunnelTries directions (hash-rotated) whose mouth is dry land. Deterministic.
        private static bool TryAccess(float x, float z, float floor, float radius, out Vector3 entrance)
        {
            entrance = default;
            float a0 = Hash01(x, z, 0x7A11) * Mathf.PI * 2f;
            for (int t = 0; t < TunnelTries; t++)
            {
                float a = a0 + t * Mathf.PI * 2f / TunnelTries, dx = Mathf.Cos(a), dz = Mathf.Sin(a);
                for (float d = 0f; d <= TunnelMax; d += TunnelStep)
                {
                    float px = x + dx * (radius - 1f + d), pz = z + dz * (radius - 1f + d), y = floor + TunnelRadius + d * TunnelRise;
                    float ground = FiresWorldGenBase.SampleFinalMeters(px, pz);
                    if (y < ground - TunnelRadius) continue;
                    if (DryOver(px, pz, ground)) { entrance = new Vector3(px, ground, pz); return true; }
                    break;
                }
            }
            return false;
        }

        // The tunnel itself: spheres along the line from the chamber wall to the mouth, plus one more past it to open the mouth.
        private static string CarveAccess(Vector3 floor, float radius, Vector3 entrance)
        {
            var dir = new Vector2(entrance.x - floor.x, entrance.z - floor.z).normalized;
            var from = new Vector3(floor.x + dir.x * (radius - 1f), floor.y + TunnelRadius, floor.z + dir.y * (radius - 1f));
            var to = new Vector3(entrance.x + dir.x * TunnelStep, entrance.y + TunnelRadius * 0.5f, entrance.z + dir.y * TunnelStep);
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / TunnelStep)), failed = 0;
            for (int i = 0; i <= n; i++)
            {
                string line = VoxelWorld.ApplyBrush(Vector3.Lerp(from, to, i / (float)n), TunnelRadius, TunnelRadius * 2f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve);
                if (line == null || line.IndexOf("fail", System.StringComparison.OrdinalIgnoreCase) >= 0) failed++;
            }
            return string.Format(CultureInfo.InvariantCulture, "access tunnel {0} sphere(s) r {1:0.0} to the mouth at ({2:0},{3:0},{4:0}){5}",
                n + 1, TunnelRadius, entrance.x, entrance.y, entrance.z, failed > 0 ? $", {failed} brush(es) reported a failure" : "");
        }

        internal enum Verdict { Surface, Underground, NoCave, BiomeMismatch, ShortCover, Excluded, NotRolled, TooDeep, NoAccess }   // 0.2.833: a dug cavern too deep for Cave Min Y / with no connector, sinkhole or tunnel

        // [ThreadStatic] scope set by the PlaceLocations prefix: only a NEW placement may go underground.
        [ThreadStatic] internal static bool t_placing;

        internal static Verdict Plan(ZoneSystem.ZoneLocation l, float x, float z, out Site site)
        {
            site = default;
            if (l == null || Share == null || Share.Value <= 0f || !FiresWorldGenBase.Active || !FiresVoxelConfig.GeneratesCaves) return Verdict.Surface;
            if (l.m_snapToWater || l.m_unique || IsSurfaceOnly(l.m_prefabName)) return Verdict.Excluded;
            if (Hash01(x, z, 0x6E0D) >= Share.Value) return Verdict.NotRolled;
            var wg = WorldGenerator.instance;
            if (wg == null) return Verdict.Surface;
            float surface = FiresWorldGenBase.SampleFinalMeters(x, z);
            float radius = Mathf.Max(l.m_exteriorRadius, 4f) + DomeExtra;
            float min = surface;
            for (int i = 0; i <= RingSamples; i++)
            {
                float r = i == RingSamples ? 0f : radius + RingSlack, a = i * Mathf.PI * 2f / RingSamples;
                min = Mathf.Min(min, FiresWorldGenBase.SampleFinalMeters(x + Mathf.Cos(a) * r, z + Mathf.Sin(a) * r));
            }
            bool dug = false;
            Vector3 entrance = default;
            var access = Access.None;
            if (!TryTunnelFloor(x, z, surface, out float floor))
            {
                // 0.2.823 (SinkTestGen4 round 8: 3 of 3 rolled placements had no cave under them, 0 underground): with no
                // generated tunnel under the spot, DIG the cavern (Fire: "carve out a large cavern … deep enough") with its top
                // Cover + DigExtra under the lowest ground over it.
                // 0.2.827 (Fire 10-07: "an access tunnel if it cannot connect to an existing cave network, or a small chance of a
                // sinkhole entrance instead"): connect to the nearest generated cave first; else, at 'Sinkhole entrance chance', a
                // dry shaft with a spiral ramp; else the walkable tunnel to dry land.
                floor = min - Cover.Value - DigExtra - WallHeight - CapShare * radius;
                if (floor < FiresVoxelConfig.CaveMinY.Value + 1f) return Verdict.TooDeep;
                if (TryConnect(x, z, floor, radius, out entrance)) access = Access.Connect;
                else if (Hash01(x, z, 0x51C4) < SinkholeChance.Value && TrySinkhole(x, z, radius, out entrance)) access = Access.Sinkhole;
                else if (TryAccess(x, z, floor, radius, out entrance)) access = Access.Tunnel;
                else return Verdict.NoAccess;
                dug = true;
            }
            var biome = FiresCaveBiome.RegionBiome(x, z, wg.GetBiome(x, z));
            if ((l.m_biome & biome) == Heightmap.Biome.None) return Verdict.BiomeMismatch;

            float top = floor + WallHeight + CapShare * radius;
            if (min - top < Cover.Value) return Verdict.ShortCover;
            site = new Site
            {
                Centre = new Vector3(x, floor, z), Radius = radius, Top = top, SurfaceMin = min, Biome = biome,
                Shaft = Hash01(x, z, 0x5AF7) < ShaftChance.Value && DryOver(x, z, surface),
                Dug = dug, Entrance = entrance, Access = access,
            };
            return Verdict.Underground;
        }

        // The floor of the first generated tunnel under (x, z): stepping down from TunnelTop under the surface, the first air
        // (CarveSdf > 0) with rock under it, above Cave Min Y.
        internal static bool TryTunnelFloor(float x, float z, float surface, out float floor)
        {
            floor = float.NaN;
            float minY = FiresVoxelConfig.CaveMinY.Value + 1f;
            bool air = false;
            for (float y = surface - TunnelTop; y > minY; y -= ScanStep)
            {
                bool open = VoxelCaveNoise.CarveSdf(x, y, z, surface - y) > 0f;
                if (open) { air = true; continue; }
                if (!air) continue;
                floor = y + ScanStep;
                return true;
            }
            return false;
        }

        internal static bool DryOver(float x, float z, float surface)
            => surface - SeaLevel > DryAltitude && !FiresWorldGenBase.IsWetAt(x, z) && !FiresSkyIslands.TryBottomAt(x, z, out _);

        // A position that already sits underground (a saved placement or a proxy respawn): well under its column's surface.
        internal static bool IsUnderground(Vector3 p)
            => FiresWorldGenBase.Active && p.y < FiresWorldGenBase.SampleFinalMeters(p.x, p.z) - UndergroundMargin;

        // ── wiring: FiresLocationHeight.Correct and the spawn postfix ─────────────────────────────────────────────────
        // True when pos was moved (or kept) underground; the caller must not snap it to the surface.
        internal static bool TryPlace(ZoneSystem.ZoneLocation l, ref Vector3 pos)
        {
            if (l == null) return false;
            if (!t_placing)
            {
                if (!IsUnderground(pos)) return false;
                // A spawn of a placement already underground (proxy respawn, a load): keep it, and know its dome.
                if (Plan(l, pos.x, pos.z, out var known) == Verdict.Underground) NoteDome(l, known);
                return true;
            }
            var verdict = Plan(l, pos.x, pos.z, out var site);
            Count(l.m_prefabName, verdict);
            if (verdict == Verdict.TooDeep || verdict == Verdict.NoAccess)
                FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                    "[Underground] {0} at ({1:0},{2:0}) stays on the surface: {3}", l.m_prefabName, pos.x, pos.z,
                    verdict == Verdict.TooDeep
                        ? $"a dug cavern here would sit under 'Cave Min Y' ({FiresVoxelConfig.CaveMinY.Value:0}) with the cover it needs"
                        : $"no access fits (no generated cave within {ConnectMax:0} m to connect to, no dry ground for a sinkhole or a tunnel mouth within {TunnelMax:0} m)"));
            if (verdict != Verdict.Underground) return false;
            var surfacePos = pos;
            pos = site.Centre;
            NoteDome(l, site);
            // 0.2.831 (Fire 10-07, TarPit1/TarPit3: "none of these are actually location spots"): the saved LocationInstance kept
            // the SURFACE position (pins, finds, any later respawn read it), and the cavern was only carved in a Full spawn, never
            // in the Ghost spawn the dedi generates zones with. Now the instance holds the cavern floor, and the cavern is carved
            // here, BEFORE SpawnLocation instantiates anything (server).
            MoveInstance(surfacePos, pos);
            s_carveNext = true;
            s_carveSite = site;
            s_carvePrefab = l.m_prefabName;
            // 0.2.809: the placement itself is logged (ghost zones never reach OnSpawned's "[Underground] … dome" line, so a
            // round that only streamed past had no evidence and no coordinates to go and look).
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture, "[Underground] placed {0} at ({1:0},{2:0},{3:0}), biome {4}{5}",
                l.m_prefabName, pos.x, pos.y, pos.z, site.Biome,
                !site.Dug ? "" : string.Format(CultureInfo.InvariantCulture, ", dug (no cave here), {0} ({1:0},{2:0},{3:0})",
                    site.Access == Access.Connect ? "connected to cave at" : site.Access == Access.Sinkhole ? "sinkhole entrance at" : "tunnel entrance",
                    site.Entrance.x, site.Entrance.y, site.Entrance.z)));
            return true;
        }

        // 0.2.810 (Fire 10-06, in a grotto: "the water not actually laying into the sinkhole as a real pond with banks"): every
        // chamber (underground location, grotto, waterfall sinkhole) used to fill its floor FIRST, then carve the walls and the cap
        // sphere centred at top - radius. That sphere reaches ~1.7 radius under the floor (30 m for r 24), so it dug a pit under
        // the floor just filled, and the pond sheet, the island and the location stood over air. Now: walls, cap, THEN the
        // floor is filled flat from under the sphere's bottom up to the floor height, across the whole chamber.
        internal static List<string> CarveChamber(Vector3 floor, float radius, float top, float wallHeight)
        {
            float sphereBottom = top - 2f * radius;
            var lines = new List<string>
            {
                VoxelWorld.ApplyBrush(new Vector3(floor.x, floor.y + wallHeight * 0.5f, floor.z), radius, wallHeight, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve),
                VoxelWorld.ApplyBrush(new Vector3(floor.x, top - radius, floor.z), radius, radius * 2f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve),
            };
            float fillBottom = Mathf.Min(sphereBottom - 1f, floor.y - 4f);
            lines.Add(VoxelWorld.ApplyBrush(new Vector3(floor.x, (fillBottom + floor.y) * 0.5f, floor.z), radius + 0.5f, floor.y - fillBottom,
                VoxelBrushShape.Cylinder, VoxelBrushMode.Fill));
            return lines;
        }

        // 0.2.810: a pond basin with real banks: BankRings stacked cylinders from a wide, shallow ring at the shore (radius + bank)
        // to a narrow, deep middle (half the radius at full depth). The voxel surface smooths the steps into a sloped bank; the
        // floor outside radius + bank stays flat, the walkable shore.
        private const int BankRings = 6;
        internal static void CarveBasin(Vector3 floor, float radius, float depth, float bank, List<string> lines)
        {
            for (int k = 0; k < BankRings; k++)
            {
                float t = (k + 1) / (float)BankRings;
                float r = Mathf.Lerp(radius + bank, radius * 0.5f, t * t);
                float d = depth * t;
                lines.Add(VoxelWorld.ApplyBrush(new Vector3(floor.x, floor.y - d * 0.5f + 0.25f, floor.z), r, d + 0.5f, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve));
            }
        }

        // Server, a location's full spawn: carve its dome, level its floor, cut its shaft.
        // 0.2.831: set by TryPlace for a NEW underground placement; SpawnLocation's prefix (FiresLocationHeight.Correct) then
        // carves before the spawn, and SnapToGround is held off for the spawn (its pieces would snap to the SURFACE height).
        private static bool s_carveNext;
        private static Site s_carveSite;
        private static string s_carvePrefab;
        [ThreadStatic] internal static bool t_spawningUnderground;

        // From SpawnLocation's prefix, after TryPlace answered true (pos = the cavern floor, or a saved underground position).
        internal static void BeforeSpawn(ZoneSystem.ZoneLocation l, Vector3 pos)
        {
            t_spawningUnderground = true;
            if (!s_carveNext) return;
            s_carveNext = false;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            StartSurfaceCheck(s_carvePrefab, pos, s_carveSite);   // 0.2.832: before anything can touch the ground
            Carve(s_carvePrefab, pos, s_carveSite);
        }

        private static readonly FieldInfo f_instances = AccessTools.Field(typeof(ZoneSystem), "m_locationInstances");

        // The saved LocationInstance of this zone (at the surface position) moves to the cavern floor.
        private static void MoveInstance(Vector3 from, Vector3 to)
        {
            try
            {
                if (ZoneSystem.instance == null || !(f_instances?.GetValue(ZoneSystem.instance) is Dictionary<Vector2s, ZoneSystem.LocationInstance> map)) return;
                var zone = ZoneSystem.GetZone(from);
                if (!map.TryGetValue(zone, out var inst)) return;
                if (Mathf.Abs(inst.m_position.x - from.x) > 0.5f || Mathf.Abs(inst.m_position.z - from.z) > 0.5f) return;
                inst.m_position = to;
                map[zone] = inst;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[Underground] could not move the location instance underground: {ex.Message}"); }
        }

        // SpawnLocation's postfix: the evidence line (the root's real position), and the snap hold released.
        internal static void OnSpawned(ZoneSystem.ZoneLocation l, Vector3 pos, ZoneSystem.SpawnMode mode)
        {
            if (!t_spawningUnderground) return;
            t_spawningUnderground = false;
            // 0.2.832: the surface check right after the spawn, and again CheckDelay s later (ServerTick).
            if (s_checks.Count > 0 && s_checks[s_checks.Count - 1].Due == 0f)
            {
                var check = s_checks[s_checks.Count - 1];
                FinishSurfaceCheck(check, "right after the spawn");
                check.Due = Time.time + CheckDelay;
            }
            if (l == null) return;
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[Underground] spawned {0} ({1}) with its root at ({2:0.0},{3:0.0},{4:0.0}); the ground over it is {5:0.0}",
                l.m_prefabName, mode, pos.x, pos.y, pos.z, FiresWorldGenBase.SampleFinalMeters(pos.x, pos.z)));
        }

        private static void Carve(string prefab, Vector3 pos, Site site)
        {
            var lines = CarveChamber(pos, site.Radius, site.Top, WallHeight);
            if (site.Access == Access.Connect) lines.Add(CarveConnector(pos, site.Radius, site.Entrance));
            else if (site.Access == Access.Sinkhole) lines.Add(CarveSinkhole(pos, site.Top, site.Entrance));
            else if (site.Access == Access.Tunnel) lines.Add(CarveAccess(pos, site.Radius, site.Entrance));
            if (site.Shaft)
            {
                float top = FiresWorldGenBase.SampleFinalMeters(pos.x, pos.z) + ShaftOver, bottom = site.Top - 1f;
                lines.Add(VoxelWorld.ApplyBrush(new Vector3(pos.x, (top + bottom) * 0.5f, pos.z), ShaftRadius, top - bottom, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve));
            }
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[Underground] {0} at ({1:0},{2:0},{3:0}): dome r {4:0.0} m, top {5:0.0}, cover {6:0.0} m over the dome, biome {7}{8}; {9}",
                prefab, pos.x, pos.y, pos.z, site.Radius, site.Top, site.SurfaceMin - site.Top, site.Biome,
                site.Shaft ? ", light shaft" : "", string.Join(" | ", lines)));
        }

        // ── domes the client knows (for the environment) ────────────────────────────────────────────────────────────
        internal struct Dome { public Vector3 Centre; public float Radius, Top; public string Environment; public bool Shaft; }
        private static readonly Dictionary<Vector2Int, Dome> s_domes = new Dictionary<Vector2Int, Dome>();

        private static void NoteDome(ZoneSystem.ZoneLocation l, in Site site)
        {
            var key = new Vector2Int(Mathf.RoundToInt(site.Centre.x), Mathf.RoundToInt(site.Centre.z));
            s_domes[key] = new Dome { Centre = site.Centre, Radius = site.Radius, Top = site.Top, Environment = EnvironmentFor(site.Biome), Shaft = site.Shaft };
        }

        // The dome around a point, and how deep into it (0 at its boundary band's outer edge, 1 well inside).
        internal static bool TryDomeAt(Vector3 p, float band, out Dome dome, out float depth)
        {
            dome = default; depth = 0f;
            foreach (var d in s_domes.Values)
            {
                float flat = new Vector2(p.x - d.Centre.x, p.z - d.Centre.z).magnitude;
                if (flat > d.Radius + band || p.y < d.Centre.y - band || p.y > d.Top + band) continue;
                float inside = Mathf.Min(d.Radius - flat, p.y - d.Centre.y + band, d.Top - p.y);
                depth = Mathf.Clamp01((inside + band) / (2f * band));
                dome = d;
                return true;
            }
            return false;
        }

        internal static void ClearDomes() => s_domes.Clear();
        internal static IEnumerable<Dome> Domes => s_domes.Values;   // 0.2.801: shaft lights

        // 0.2.800: grottos register their domes for the environment too.
        internal static void AddDome(Vector3 centre, float radius, float top, string environment, bool shaft)
            => s_domes[new Vector2Int(Mathf.RoundToInt(centre.x), Mathf.RoundToInt(centre.z))]
                = new Dome { Centre = centre, Radius = radius, Top = top, Environment = environment, Shaft = shaft };

        private static Dictionary<Heightmap.Biome, string> s_envMap;

        internal static string EnvironmentFor(Heightmap.Biome biome)
        {
            var map = s_envMap ??= Parse(LocationEnvironments?.Value);
            return map.TryGetValue(biome, out var env) ? env : "Clear";
        }

        internal static Dictionary<Heightmap.Biome, string> Parse(string text)
        {
            var map = new Dictionary<Heightmap.Biome, string>();
            foreach (var pair in (text ?? "").Split(','))
            {
                int colon = pair.IndexOf(':');
                if (colon <= 0 || pair.Length <= colon + 1) continue;
                if (Enum.TryParse(pair.Substring(0, colon).Trim(), true, out Heightmap.Biome b)) map[b] = pair.Substring(colon + 1).Trim();
            }
            return map;
        }

        // At world load: every configured environment name EnvMan does not know is reported (it would fall back).
        internal static void CheckEnvironments()
        {
            var env = EnvMan.instance;
            if (env == null) return;
            // EnvMan.GetEnv is private (a publicized compile is no runtime access): its public list instead.
            var unknown = Parse(LocationEnvironments?.Value).Values.Where(n => !env.m_environments.Any(e => e != null && e.m_name == n)).Distinct().ToList();
            if (unknown.Count > 0)
                FiresLogger.LogWarning($"[Underground] unknown environment name(s) in 'Underground location environments': {string.Join(", ", unknown)}; "
                    + "those domes use 'Clear'. Known: " + string.Join(", ", env.m_environments.Select(e => e.m_name)));
        }

        // ── counts + fat_location_dump ──────────────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, int[]> s_counts = new Dictionary<string, int[]>();

        private static void Count(string prefab, Verdict v)
        {
            if (!s_counts.TryGetValue(prefab ?? "?", out var c)) s_counts[prefab ?? "?"] = c = new int[Enum.GetValues(typeof(Verdict)).Length];
            c[(int)v]++;
            // 0.2.822 ([lead] 10-07: "how many went underground, and why not?" from ONE dedi log): every SummaryEvery placements
            // (or SummarySeconds after the last summary if anything changed), and once more when the game quits, the per-type
            // verdict counts go to the log at Debug.
            s_counted++;
            if (!s_quitHooked) { s_quitHooked = true; Application.quitting += () => LogSummary("final"); }
            if (s_counted % SummaryEvery == 0 || Time.realtimeSinceStartup - s_lastSummary > SummarySeconds) LogSummary("so far");
        }

        private const int SummaryEvery = 50;
        private const float SummarySeconds = 60f;
        private static int s_counted, s_summarised;
        private static float s_lastSummary;
        private static bool s_quitHooked;

        private static void LogSummary(string when)
        {
            // 0.2.826 (round 8: no "final" line): the final summary always logs; it was skipped as "nothing new" whenever the
            // last "so far" line already held every placement.
            if (s_counted == s_summarised && when != "final") return;
            s_summarised = s_counted;
            s_lastSummary = Time.realtimeSinceStartup;
            var total = new int[Enum.GetValues(typeof(Verdict)).Length];
            var sb = new StringBuilder();
            foreach (var kv in s_counts.OrderBy(k => k.Key))
            {
                var c = kv.Value;
                for (int i = 0; i < c.Length; i++) total[i] += c[i];
                sb.Append(string.Format(CultureInfo.InvariantCulture, "\n  {0}: underground {1}, surface {2}, no cave {3}, biome {4}, cover {5}, excluded {6}, not rolled {7}, too deep {8}, no access {9}",
                    kv.Key, c[(int)Verdict.Underground], c[(int)Verdict.Surface], c[(int)Verdict.NoCave], c[(int)Verdict.BiomeMismatch],
                    c[(int)Verdict.ShortCover], c[(int)Verdict.Excluded], c[(int)Verdict.NotRolled], c[(int)Verdict.TooDeep], c[(int)Verdict.NoAccess]));
            }
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[Underground] verdicts {0} ({1} placement(s)): underground {2}, surface {3}, no cave {4}, biome {5}, cover {6}, excluded {7}, not rolled {8}, too deep {9}, no access {10}{11}",
                when, s_counted, total[(int)Verdict.Underground], total[(int)Verdict.Surface], total[(int)Verdict.NoCave], total[(int)Verdict.BiomeMismatch],
                total[(int)Verdict.ShortCover], total[(int)Verdict.Excluded], total[(int)Verdict.NotRolled], total[(int)Verdict.TooDeep], total[(int)Verdict.NoAccess], sb));
        }

        internal static void RegisterDump()
        {
            new Terminal.ConsoleCommand("fat_location_dump",
                "- per location type this session: placements underground / on the surface, and why the surface (no cave, biome, cover, excluded, not rolled) (read-only)",
                args =>
                {
                    var sb = new StringBuilder("[Underground] placements this session by type (underground / surface: no cave, biome mismatch, short cover, excluded, not rolled):");
                    int under = 0, total = 0;
                    foreach (var kv in s_counts.OrderBy(k => k.Key))
                    {
                        var c = kv.Value;
                        int n = c.Sum();
                        total += n; under += c[(int)Verdict.Underground];
                        sb.Append($"\n  {kv.Key}: {c[(int)Verdict.Underground]}/{n} underground; surface: no cave {c[(int)Verdict.NoCave]}, biome {c[(int)Verdict.BiomeMismatch]}, "
                            + $"cover {c[(int)Verdict.ShortCover]}, excluded {c[(int)Verdict.Excluded]}, not rolled {c[(int)Verdict.NotRolled]}, too deep {c[(int)Verdict.TooDeep]}, no access {c[(int)Verdict.NoAccess]}");
                    }
                    sb.Append($"\n  total: {under}/{total} underground; {s_domes.Count} dome(s) known here.");
                    FiresLogger.LogInfo(sb.ToString());
                    args.Context?.AddString($"[Underground] {under}/{total} placements underground this session; the per-type table is in the BepInEx log.");
                },
                isCheat: true);
        }

        private static HashSet<string> s_surfaceOnly;
        private static bool IsSurfaceOnly(string prefab)
        {
            s_surfaceOnly ??= new HashSet<string>((SurfaceOnly?.Value ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
            return prefab != null && s_surfaceOnly.Contains(prefab);
        }

        private static float Hash01(float x, float z, int salt)
        {
            unchecked
            {
                int seed = WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0;
                uint h = (uint)(seed * 73856093 ^ Mathf.RoundToInt(x) * 19349663 ^ Mathf.RoundToInt(z) * 83492791 ^ salt * 2654435761);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        // Only a placement made by PlaceLocations is NEW (a proxy's respawn and a load keep where they are).
        // 0.2.830 (Fire 10-07, TarPit1 dug at (-259,49,-3647): a 30 m stone crater in the SURFACE down to the cavern): a location
        // prefab's vanilla TerrainModifier children level the heightmap to their own y, and the voxel ground is rebuilt from it
        // ("terraform: zone rebuild ... after its heightmap changed"), in ghost spawns too. Underground, that y is tens of metres
        // down. A TerrainModifier of an underground location never registers: the dome is levelled by our own carve.
        [HarmonyPatch(typeof(TerrainModifier), "Awake")]
        private static class TerrainModifier_Underground
        {
            [HarmonyPrefix]
            private static bool Prefix(TerrainModifier __instance)
            {
                if (__instance == null || !FiresWorldGenBase.Active) return true;
                // 0.2.841: a surface location levelled by our pad (a relaxed terrain-delta site) loses its vanilla modifiers too,
                // so there is no double edit.
                if (VerdantsAscent.Terrain.Zones.FatLocationAccelerator.InLevelSite(__instance.transform.position)
                    && __instance.GetComponentInParent<Location>() != null)
                {
                    __instance.enabled = false;
                    UnityEngine.Object.Destroy(__instance);
                    FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                        "[LocationLevel] vanilla terrain modifier '{0}' at ({1:0},{2:0},{3:0}) suppressed: the site is levelled by our pad",
                        __instance.name, __instance.transform.position.x, __instance.transform.position.y, __instance.transform.position.z));
                    return false;
                }
                if (!t_spawningUnderground && __instance.GetComponentInParent<Location>() == null) return true;
                if (!IsUnderground(__instance.transform.position)) return true;
                __instance.enabled = false;
                UnityEngine.Object.Destroy(__instance);
                Blocked("TerrainModifier", __instance.name, __instance.transform.position);
                return false;
            }
        }

        // 0.2.832 (Fire 10-07: underground locations must not keep ANY vanilla terrain manipulation): every evidence line is
        // written (not only the first), one per blocked item, naming the channel.
        internal static void Blocked(string channel, string what, Vector3 at)
            => FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[Underground] blocked {0} '{1}' at ({2:0.0},{3:0.0},{4:0.0}) ({5:0.0} m under the ground there): the surface over it is left alone",
                channel, what, at.x, at.y, at.z, FiresWorldGenBase.SampleFinalMeters(at.x, at.z) - at.y));

        // 0.2.832: the LAST channel. Every heightmap write (vanilla's TerrainOp path, Expand World Data's levelArea, which writes
        // the TerrainComp directly, a blueprint, anything) goes through these four TerrainComp primitives. One aimed at a point
        // under a known underground dome (its column, well below the ground) is dropped.
        [HarmonyPatch]
        private static class TerrainComp_Underground
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var name in new[] { "LevelTerrain", "RaiseTerrain", "SmoothTerrain", "PaintCleared" })
                {
                    var m = AccessTools.Method(typeof(TerrainComp), name);
                    if (m != null) yield return m;
                }
            }

            [HarmonyPrefix]
            private static bool Prefix(Vector3 worldPos, MethodBase __originalMethod)
            {
                if (!FiresWorldGenBase.Active || !UnderDome(worldPos)) return true;
                Blocked("TerrainComp." + __originalMethod.Name, "heightmap write", worldPos);
                return false;
            }
        }

        // A point well under the ground inside (or beside) a known underground location's dome.
        internal static bool UnderDome(Vector3 p)
        {
            if (s_domes.Count == 0 || !IsUnderground(p)) return false;
            foreach (var d in s_domes.Values)
                if (new Vector2(p.x - d.Centre.x, p.z - d.Centre.z).magnitude <= d.Radius + DomeGuardSlack && p.y <= d.Top + DomeGuardSlack) return true;
            return false;
        }
        private const float DomeGuardSlack = 8f;

        // ── 0.2.832: the proof line, "[Underground] surface over X unchanged: max |dh| = N m over r R" ────────────────────
        private const float CheckStep = 4f, CheckRing = 6f, EntranceClear = 6f, CheckDelay = 15f, PlausibleOffset = 60f;
        private sealed class SurfaceCheck
        {
            public string Prefab;
            public Vector3 Centre, Entrance;
            public bool HasEntrance;
            public float Radius, Due;
            public readonly List<Vector3> Points = new List<Vector3>();   // x, ground height before, z
        }
        private static readonly List<SurfaceCheck> s_checks = new List<SurfaceCheck>();

        private static void StartSurfaceCheck(string prefab, Vector3 centre, in Site site)
        {
            if (ZoneSystem.instance == null) return;
            var check = new SurfaceCheck
            {
                Prefab = prefab, Centre = centre, Radius = site.Radius + CheckRing, Entrance = site.Entrance,
                HasEntrance = site.Access != Access.None || site.Shaft,
            };
            for (float dx = -check.Radius; dx <= check.Radius; dx += CheckStep)
                for (float dz = -check.Radius; dz <= check.Radius; dz += CheckStep)
                {
                    if (dx * dx + dz * dz > check.Radius * check.Radius) continue;
                    float x = centre.x + dx, z = centre.z + dz;
                    if (check.HasEntrance && site.Access != Access.Connect
                        && new Vector2(x - site.Entrance.x, z - site.Entrance.z).magnitude < EntranceClear + SinkholeRadius) continue;
                    if (site.Shaft && new Vector2(dx, dz).magnitude < ShaftRadius + EntranceClear) continue;
                    check.Points.Add(new Vector3(x, TopSurface(x, z), z));
                }
            s_checks.Add(check);
        }

        // 0.2.852 (SinkTestGen9 overnight 8: "CHANGED 23.74 m" over Dolmen01 while a column probe showed 10+ m of intact rock):
        // ZoneSystem.GetGroundHeight answered the cavern FLOOR once the zone was loaded. The check now reads the TOPMOST voxel
        // surface from above (from TopReach over the generated surface down to TopDepth under it), else the generated surface.
        private const float TopReach = 20f, TopDepth = 40f, ChangedOver = 0.5f;   // voxel vs generated surface differ by up to a few dm
        private static float TopSurface(float x, float z)
        {
            float gen = FiresWorldGenBase.SampleFinalMeters(x, z);
            return VoxelWorld.TrySampleSurfaceY(x, z, gen + TopReach, gen - TopDepth, out float top, out bool wall) && !wall ? top : gen;
        }

        private static void FinishSurfaceCheck(SurfaceCheck check, string when)
        {
            // 0.2.836 (SinkTestGen7: "CHANGED: max |dh| = 959.95 m" 15 s after Dolmen01's spawn): once a zone unloads, the ground
            // query answers a sentinel, not a height. A point counts only while both readings are within PlausibleOffset of the
            // generated surface there; the line says how many were readable.
            float max = 0f;
            int sampled = 0;
            var at = check.Centre;
            foreach (var p in check.Points)
            {
                float now = TopSurface(p.x, p.z), gen = FiresWorldGenBase.SampleFinalMeters(p.x, p.z);
                if (Mathf.Abs(p.y - gen) > PlausibleOffset || Mathf.Abs(now - gen) > PlausibleOffset) continue;
                sampled++;
                float d = Mathf.Abs(now - p.y);
                if (d > max) { max = d; at = p; }
            }
            if (sampled == 0)
            {
                FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                    "[Underground] surface over {0}: not readable {1} (0 of {2} point(s) had a ground height; the zone is not loaded here)",
                    check.Prefab, when, check.Points.Count));
                return;
            }
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[Underground] surface over {0} {1}: max |dh| = {2:0.00} m over r {3:0} ({4} of {5} point(s) sampled, entrance/shaft excluded, {6}){7}",
                check.Prefab, max <= ChangedOver ? "unchanged" : "CHANGED", max, check.Radius, sampled, check.Points.Count, when,
                max <= ChangedOver ? "" : string.Format(CultureInfo.InvariantCulture, "; largest at ({0:0},{1:0})", at.x, at.z)));
        }

        // From the server's cave tick: the delayed check (heightmap writes that land after the spawn frame).
        // 0.2.843 (the lead, SinkTestGen8: 550 zones generated, 26 with a location - missing locations, or a sparse area?):
        // every CensusSeconds on the server, the generated zones against the world's location instances. An instance in a
        // generated zone that is NOT placed is a location that should exist and does not.
        private const float CensusSeconds = 60f;
        private static float s_nextCensus;
        private static int s_lastCensusZones = -1;
        private static readonly FieldInfo f_generated = AccessTools.Field(typeof(ZoneSystem), "m_generatedZones");

        private static void Census()
        {
            if (Time.time < s_nextCensus || ZoneSystem.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            s_nextCensus = Time.time + CensusSeconds;
            if (!(f_generated?.GetValue(ZoneSystem.instance) is HashSet<Vector2s> generated) || generated.Count == s_lastCensusZones) return;
            if (!(f_instances?.GetValue(ZoneSystem.instance) is Dictionary<Vector2s, ZoneSystem.LocationInstance> map)) return;
            s_lastCensusZones = generated.Count;
            int placed = 0, unplaced = 0;
            string first = null;
            foreach (var kv in map)
            {
                if (!generated.Contains(kv.Key)) continue;
                if (kv.Value.m_placed) placed++;
                else { unplaced++; first ??= $"{kv.Value.m_location?.m_prefabName ?? "?"} in zone ({kv.Key.x},{kv.Key.y})"; }
            }
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[LocationCensus] {0} generated zone(s); {1} location instance(s) in the world; in generated zones: {2} placed, {3} NOT placed{4} ({5:0.0}% of generated zones hold one)",
                generated.Count, map.Count, placed, unplaced, first != null ? $" (first: {first})" : "",
                generated.Count > 0 ? 100f * (placed + unplaced) / generated.Count : 0f));
        }

        internal static void ServerTick()
        {
            Census();
            if (s_checks.Count == 0 || ZoneSystem.instance == null) return;
            for (int i = s_checks.Count - 1; i >= 0; i--)
            {
                if (s_checks[i].Due == 0f || Time.time < s_checks[i].Due) continue;   // 0 = its spawn has not finished
                FinishSurfaceCheck(s_checks[i], $"{CheckDelay:0} s after the spawn");
                s_checks.RemoveAt(i);
            }
        }

        // 0.2.831: vanilla's SpawnLocation ends with SnapToGround.SnappAll, which puts every snapping piece at
        // ZoneSystem.GetGroundHeight - the SURFACE - so an underground location's bones, stones and tar went up to the ground
        // over it. During an underground spawn the pieces stay where the prefab puts them, on the cavern floor.
        [HarmonyPatch(typeof(SnapToGround), nameof(SnapToGround.Snap))]
        private static class SnapToGround_Underground
        {
            [HarmonyPrefix]
            private static bool Prefix(SnapToGround __instance)
                => !(t_spawningUnderground && __instance != null && IsUnderground(__instance.transform.position));
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        private static class PlaceLocations_Scope
        {
            private static void Prefix() => t_placing = true;
            private static void Finalizer() => t_placing = false;
        }
    }
}
