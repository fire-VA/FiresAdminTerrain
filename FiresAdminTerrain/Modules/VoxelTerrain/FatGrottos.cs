using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Grottos (FAT 0.2.800; Fire 10-06 via the lead: underground ponds as "little underground oases"): a big domed cavern on a
    // cave tunnel, a flat shore ring, a pond in the middle, trees, bushes and rocks of the cave's biome round it, grass from the
    // layered clutter (0.2.798), a soft glow over the water, a light shaft for some, and a brighter environment inside.
    //   * where: one roll per cave zone ('Grotto chance'), the site jittered round the zone's centre, on the first generated
    //     tunnel under it (FatUndergroundLocations.TryTunnelFloor); the same cover rule as underground locations (the ground
    //     over the whole dome at least 'Underground cover' above its top), else no grotto. Pure function of the seed + zone, so
    //     every peer knows the same grottos;
    //   * made by the server in the cave content pass (VoxelCaveContent): floor levelled, walls + cap carved, the basin carved
    //     and a calm sheet laid (WaterSheets.Lay), the shaft cut where the ground over it is dry land, the vegetation placed as
    //     cave content (tagged, so a re-dress removes exactly it);
    //   * on every client near one: a warm point light over the pond whose intensity fades in with distance, and the dome
    //     registered for the environment controller ('Grotto environment', blended like an underground location's dome).
    internal static class FatGrottos
    {
        private const string Section = "09 - World generation";
        internal static ConfigEntry<float> Chance, ShaftChance;
        internal static ConfigEntry<string> Environment, Vegetation;

        private const float MinRadius = 22f, RadiusSpan = 8f, WallHeight = 10f, CapShare = 0.3f, FloorFill = 4f, Jitter = 12f;
        private const float BasinMin = 6f, BasinSpan = 3f, BasinDepth = 2.5f, SheetDrop = 0.3f, ShoreGap = 2f, WallGap = 3f, BankRun = 2.5f;
        private const float ShaftRadius = 2.5f, ShaftOver = 3f, RingSlack = 2f, ZoneSize = 64f;
        private const int RingSamples = 16;
        private const float GlowRange = 28f, GlowIntensity = 1.6f, GlowLift = 7f, GlowFadeNear = 30f, GlowFadeFar = 70f;

        internal static void Bind(ConfigFile cfg)
        {
            Chance = cfg.Bind(Section, "Grotto chance", 0.05f,
                new ConfigDescription("Chance per cave zone of a grotto: a big domed cavern with a pond, a flat shore and vegetation of the "
                    + "cave's biome. Needs the same rock cover as underground locations.", new AcceptableValueRange<float>(0f, 1f)));
            ShaftChance = cfg.Bind(Section, "Light shaft chance (grottos)", 0.15f,
                new ConfigDescription("Share of grottos that get a narrow light shaft to the surface, where the ground over them is dry land.",
                    new AcceptableValueRange<float>(0f, 1f)));
            Environment = cfg.Bind(Section, "Grotto environment", "Clear",
                "Environment inside a grotto's dome (brighter than the passages' cave environment).");
            Vegetation = cfg.Bind(Section, "Grotto vegetation", "",
                "Override the vegetation per biome as Biome:trees/bushes/rocks entries separated by ';', each list '|'-separated prefab "
                + "names (e.g. Meadows:Beech1|Oak1/Bush01/Rock_3). Empty = FAT's defaults.");
        }

        internal struct Site
        {
            public Vector2s Zone;
            public Vector3 Centre;     // on the levelled floor
            public float Radius, Top, Basin;
            public bool Shaft;
            public Heightmap.Biome Biome;
        }

        // The grotto of a zone, if it has one.
        internal static bool TryPlan(Vector2s zone, out Site site)
        {
            site = default;
            if (Chance == null || Chance.Value <= 0f || !FiresWorldGenBase.Active || !FiresVoxelConfig.GeneratesCaves || WorldGenerator.instance == null) return false;
            if (Hash01(zone, 0x6407) >= Chance.Value) return false;
            float x = zone.x * ZoneSize + (Hash01(zone, 0x11) - 0.5f) * 2f * Jitter, z = zone.y * ZoneSize + (Hash01(zone, 0x22) - 0.5f) * 2f * Jitter;
            float surface = FiresWorldGenBase.SampleFinalMeters(x, z);
            if (!FatUndergroundLocations.TryTunnelFloor(x, z, surface, out float floor)) return false;
            float radius = MinRadius + Hash01(zone, 0x33) * RadiusSpan, top = floor + WallHeight + CapShare * radius;
            float min = surface;
            for (int i = 0; i <= RingSamples; i++)
            {
                float r = i == RingSamples ? 0f : radius + RingSlack, a = i * Mathf.PI * 2f / RingSamples;
                min = Mathf.Min(min, FiresWorldGenBase.SampleFinalMeters(x + Mathf.Cos(a) * r, z + Mathf.Sin(a) * r));
            }
            float cover = FatUndergroundLocations.Cover != null ? FatUndergroundLocations.Cover.Value : 8f;
            if (min - top < cover) return false;
            site = new Site
            {
                Zone = zone, Centre = new Vector3(x, floor, z), Radius = radius, Top = top, Basin = BasinMin + Hash01(zone, 0x44) * BasinSpan,
                Shaft = Hash01(zone, 0x55) < ShaftChance.Value && FatUndergroundLocations.DryOver(x, z, surface),
                Biome = FiresCaveBiome.RegionBiome(x, z, WorldGenerator.instance.GetBiome(x, z)),
            };
            return true;
        }

        // ── server: build it (from VoxelCaveContent's dressing pass) ──────────────────────────────────────────────────
        internal static bool Build(Vector2s zone, System.Random rng)
        {
            if (!TryPlan(zone, out var s)) return false;
            Vector3 c = s.Centre;
            // 0.2.810: walls + cap, the floor refilled flat under them (the cap used to dig a pit under the pond), then a basin
            // with sloped banks and a flat shore ring round it (FatUndergroundLocations.CarveChamber / CarveBasin).
            var lines = FatUndergroundLocations.CarveChamber(c, s.Radius, s.Top, WallHeight);
            if (lines[0] != null && lines[0].StartsWith("Refused")) { FiresLogger.LogDebug($"[Grotto] ({zone.x},{zone.y}) skipped: {lines[0]}"); return false; }
            FatUndergroundLocations.CarveBasin(c, s.Basin, BasinDepth, BankRun, lines);
            if (s.Shaft)
            {
                float top = FiresWorldGenBase.SampleFinalMeters(c.x, c.z) + ShaftOver, bottom = s.Top - 1f;
                lines.Add(VoxelWorld.ApplyBrush(new Vector3(c.x, (top + bottom) * 0.5f, c.z), ShaftRadius, top - bottom, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve));
            }
            int sheet = VerdantsAscent.Water.Tools.WaterSheets.Lay(new Vector3(c.x, c.y - BasinDepth, c.z), c.y - SheetDrop, s.Basin + BankRun + 0.5f, "grotto pond", out string refusal);
            int placed = PlaceVegetation(s, rng, out int missing);
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[Grotto] ({0},{1}) at ({2:0},{3:0},{4:0}): dome r {5:0.0} m, top {6:0.0}, biome {7}, pond r {8:0.0} m sheet {9}{10}; {11} plant(s)/rock(s) placed{12}{13}",
                zone.x, zone.y, c.x, c.y, c.z, s.Radius, s.Top, s.Biome, s.Basin, sheet, sheet == 0 ? $" (refused: {refusal})" : "",
                placed, missing > 0 ? $", {missing} prefab name(s) unknown" : "", s.Shaft ? ", light shaft" : ""));
            return true;
        }

        private static int PlaceVegetation(in Site s, System.Random rng, out int missing)
            => PlaceRing(s.Centre, s.Basin + BankRun + ShoreGap, s.Radius - WallGap, s.Biome, rng, out missing);

        // 0.2.802: a grotto's shore planting on any ring (also the waterfall sinkholes' pond shores).
        internal static int PlaceRing(Vector3 centre, float inner, float outer, Heightmap.Biome biome, System.Random rng, out int missing)
        {
            missing = 0;
            var lists = ListsFor(biome);
            int placed = 0;
            var s = new Site { Centre = centre };
            int[] counts = { 4 + rng.Next(4), 6 + rng.Next(5), 4 + rng.Next(3) };
            for (int kind = 0; kind < 3; kind++)
            {
                var names = lists[kind];
                if (names.Length == 0) continue;
                for (int i = 0; i < counts[kind]; i++)
                {
                    var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(names[rng.Next(names.Length)]) : null;
                    if (prefab == null) { missing++; continue; }
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Lerp(inner, outer, Mathf.Sqrt((float)rng.NextDouble()));
                    var at = new Vector3(s.Centre.x + Mathf.Cos(a) * r, s.Centre.y, s.Centre.z + Mathf.Sin(a) * r);
                    if (VoxelCaveContent.Spawn(prefab, at, VoxelCaveContent.RandomYaw(rng), 1f, CaveContentKind.Grotto) != null) placed++;
                }
            }
            return placed;
        }

        private static readonly Dictionary<Heightmap.Biome, string> Defaults = new Dictionary<Heightmap.Biome, string>
        {
            [Heightmap.Biome.Meadows] = "Beech1|Birch1|Oak1/Bush01|RaspberryBush/Rock_3|rock4_forest",
            [Heightmap.Biome.BlackForest] = "FirTree|Pinetree_01/BlueberryBush|Bush02_en/rock4_forest|Rock_3",
            [Heightmap.Biome.Swamp] = "SwampTree1/Bush02_en/Rock_3",
            [Heightmap.Biome.Mountain] = "FirTree/Bush02_en/rock1_mountain|Rock_3",
            [Heightmap.Biome.Plains] = "Birch1_aut|Birch2_aut/Bush01_heath|CloudberryBush/Rock_3",
            [Heightmap.Biome.Mistlands] = "YggaShoot1|YggaShoot2/Bush01/rock4_forest",
            [Heightmap.Biome.AshLands] = "//Rock_3",
            [Heightmap.Biome.DeepNorth] = "//rock1_mountain",
        };

        private static string[][] ListsFor(Heightmap.Biome biome)
        {
            string text = null;
            foreach (var entry in (Vegetation?.Value ?? "").Split(';'))
            {
                int colon = entry.IndexOf(':');
                if (colon > 0 && Enum.TryParse(entry.Substring(0, colon).Trim(), true, out Heightmap.Biome b) && b == biome) text = entry.Substring(colon + 1);
            }
            if (text == null && !Defaults.TryGetValue(biome, out text)) text = "//Rock_3";
            var parts = text.Split('/');
            var lists = new string[3][];
            for (int k = 0; k < 3; k++)
                lists[k] = k < parts.Length ? Array.FindAll(parts[k].Split('|'), n => n.Trim().Length > 0) : new string[0];
            return lists;
        }

        // ── client: glow + environment dome for the grottos round the camera ─────────────────────────────────────────
        private static readonly Dictionary<Vector2s, GameObject> s_glows = new Dictionary<Vector2s, GameObject>();
        private static readonly HashSet<Vector2s> s_planned = new HashSet<Vector2s>();
        private static Vector2s s_lastZone = new Vector2s(int.MinValue, int.MinValue);

        // From VoxelCaveEnv's poll: (re)plan the grottos of the zones round the player when it changes zone; fade the glows.
        internal static void Tick(Vector3 player)
        {
            if (Chance == null || !FiresWorldGenBase.Active) return;
            var zone = ZoneSystem.GetZone(player);
            if (zone.x != s_lastZone.x || zone.y != s_lastZone.y)
            {
                s_lastZone = zone;
                for (int dz = -2; dz <= 2; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        var z = new Vector2s(zone.x + dx, zone.y + dz);
                        if (!s_planned.Add(z) || !TryPlan(z, out var site)) continue;
                        FatUndergroundLocations.AddDome(site.Centre, site.Radius, site.Top, Environment.Value, site.Shaft);
                        var go = new GameObject($"FatGrottoGlow_{z.x}_{z.y}");
                        go.transform.position = site.Centre + Vector3.up * GlowLift;
                        var light = go.AddComponent<Light>();
                        light.type = LightType.Point;
                        light.range = GlowRange;
                        light.color = new Color(1f, 0.86f, 0.62f);
                        light.intensity = 0f;
                        light.shadows = LightShadows.None;
                        s_glows[z] = go;
                    }
            }
            foreach (var kv in s_glows)
            {
                if (kv.Value == null) continue;
                var light = kv.Value.GetComponent<Light>();
                float d = Vector3.Distance(player, kv.Value.transform.position);
                float t = Mathf.Clamp01((GlowFadeFar - d) / (GlowFadeFar - GlowFadeNear));
                light.intensity = GlowIntensity * t * t * (3f - 2f * t);   // smoothstep: fades in, never pops
                light.enabled = light.intensity > 0.01f;
            }
        }

        private static float Hash01(Vector2s zone, int salt)
        {
            unchecked
            {
                int seed = WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0;
                uint h = (uint)(seed * 73856093 ^ zone.x * 19349663 ^ zone.y * 83492791 ^ salt * 2654435761);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }
    }
}
