using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Skyland biomes. Three in four skylands take the biome the world plans at their centre; the rest, and every
    // skyland over planned sea or Deep North, roll a random land biome. The roll hashes the island record, so every
    // peer derives the same biome from the synced registry. Each biome dresses from its own 1.0 world-gen vegetation
    // (Tools\ZONE_MODE_BIOME_DATA.md, appendix B), its ore, relic and loot nodes a small share of the top. Scales come
    // from the prefabs' measured sizes in the 1.0 rip.
    //
    // A dry skyland with room for it can also hold one of its biome's vanilla locations, spawned whole with its
    // spawners. The skyland is its ground: the top is levelled across the location's footprint and eased back into
    // the natural top by its reach, and the location's own terrain modifiers never reach the land below. Footprints
    // and exterior radii are measured from the 1.0 rip (FiresSkylandPreview.MeasureLocations), so every peer derives
    // the same plateau without loading a prefab.
    internal static partial class FiresSkyIslands
    {
        private const float PlannedBiomeShare = 0.75f;
        private const int BiomeShareSalt = 0x5C41;
        private const int BiomePickSalt = 0x5C42;
        private const float SkylandSkinReach = 8f;
        private const float SkylandSiteChance = 0.7f;
        private const int SiteChanceSalt = 0x5C46;
        private const int SitePickSalt = 0x5C47;
        private const int SiteDriftSalt = 0x5C48;
        private const int SiteBearingSalt = 0x5C49;
        private const int SiteYawSalt = 0x5C4A;
        private const float SiteBlend = 4f;
        private const float SiteMinFlat = 3f;
        private const float SiteRoomShare = 0.85f;
        private const float SiteDrift = 0.5f;
        private const float SiteLevelRing = 0.7f;
        private const int SiteLevelSamples = 8;
        private const float SiteDressingGap = 1f;
        private const float SiteTreeGap = 6f;

        private static readonly Heightmap.Biome[] SkylandLandBiomes =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands,
        };

        // The biome the world plans at a point, read in a planning scope: the zone gate shows closed zones as sea.
        internal static Heightmap.Biome PlannedBiomeAt(float x, float z)
        {
            var generator = WorldGenerator.instance;
            if (generator == null) return Heightmap.Biome.None;
            Zones.ZoneModeGate.EnterPlanning();
            try { return generator.GetBiome(x, z); }
            finally { Zones.ZoneModeGate.ExitPlanning(); }
        }

        private static Heightmap.Biome RollSkylandBiome(in Island isl)
        {
            var planned = PlannedBiomeAt(isl.X, isl.Z);
            bool plannedLand = planned != Heightmap.Biome.None && planned != Heightmap.Biome.Ocean
                               && planned != Heightmap.Biome.DeepNorth;
            if (plannedLand && Hash(0, 0, isl.Variety, BiomeShareSalt) < PlannedBiomeShare) return planned;
            return SkylandLandBiomes[(int)(Hash(0, 0, isl.Variety, BiomePickSalt) * SkylandLandBiomes.Length)];
        }

        internal static Heightmap.Biome SkylandBiome(int index) => s_islands[index].Biome;

        private static void RollSkylandSite(ref Island isl)
        {
            if (isl.PondLvl > 0f || Hash(0, 0, isl.Variety, SiteChanceSalt) >= SkylandSiteChance) return;
            var sites = PaletteOf(isl.Biome).Sites;
            float room = (isl.Shape != null ? isl.Shape.HeartWidth : isl.R) * SiteRoomShare;
            int total = 0;
            foreach (var site in sites) if (site.Reach <= room) total += site.Weight;
            if (total == 0) return;
            int roll = (int)(Hash(0, 0, isl.Variety, SitePickSalt) * total);
            int pick = 0;
            for (; pick < sites.Length; pick++)
            {
                if (sites[pick].Reach > room) continue;
                roll -= sites[pick].Weight;
                if (roll < 0) break;
            }
            if (pick >= sites.Length) return;
            var chosen = sites[pick];
            float drift = (room - chosen.Reach) * SiteDrift * Hash(0, 0, isl.Variety, SiteDriftSalt);
            float bearing = Hash(0, 0, isl.Variety, SiteBearingSalt) * Mathf.PI * 2f;
            var anchor = isl.Shape != null ? isl.Shape.Heart : new Vector2(isl.X, isl.Z);
            var spot = anchor + new Vector2(Mathf.Cos(bearing), Mathf.Sin(bearing)) * drift;
            if (isl.Waters != null && !TryDrySite(in isl, room, chosen.Reach, out spot)) return;
            isl.SiteX = spot.x;
            isl.SiteZ = spot.y;
            float level = NaturalTopAt(in isl, isl.SiteX, isl.SiteZ);
            for (int k = 0; k < SiteLevelSamples; k++)
            {
                float a = k * Mathf.PI * 2f / SiteLevelSamples;
                level += NaturalTopAt(in isl, isl.SiteX + Mathf.Cos(a) * chosen.Flat * SiteLevelRing, isl.SiteZ + Mathf.Sin(a) * chosen.Flat * SiteLevelRing);
            }
            isl.SiteY = level / (SiteLevelSamples + 1);
            isl.SiteFlat = chosen.Flat;
            isl.SiteReach = chosen.Reach;
            isl.SiteClear = chosen.Clear;
            isl.SiteYaw = Mathf.Floor(Hash(0, 0, isl.Variety, SiteYawSalt) * 16f) * 22.5f;
            isl.Site = pick + 1;
        }

        // The location a skyland holds, where it stands and how it turns, and the seed its random parts roll from.
        internal static bool TryGetSkylandSite(int index, out string location, out Vector3 position, out Quaternion rotation, out int seed)
        {
            var isl = s_islands[index];
            location = null;
            position = default;
            rotation = Quaternion.identity;
            seed = isl.Variety;
            if (!IsSkyland(isl.Kind) || isl.Site <= 0) return false;
            location = PaletteOf(isl.Biome).Sites[isl.Site - 1].Location;
            position = new Vector3(isl.SiteX + isl.OffX, isl.SiteY + isl.OffY, isl.SiteZ + isl.OffZ);
            rotation = Quaternion.Euler(0f, isl.SiteYaw, 0f);
            return true;
        }

        private static bool InSiteClearing(in Island isl, float wx, float wz, float gap)
        {
            if (isl.Site <= 0) return false;
            float dx = wx - isl.SiteX, dz = wz - isl.SiteZ;
            float clear = Mathf.Max(isl.SiteFlat, isl.SiteClear) + gap;
            return dx * dx + dz * dz < clear * clear;
        }

        private static uint SiteSignature(in Island isl)
        {
            if (isl.Site <= 0) return 0u;
            uint h = 2166136261u;
            h = (h ^ (uint)isl.Site) * 16777619u;
            h = (h ^ (uint)isl.SiteX.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.SiteZ.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.SiteY.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.SiteFlat.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.SiteReach.GetHashCode()) * 16777619u;
            return h;
        }

        // The biome of the skyland a point stands on (or is inside). Runs on hot vanilla paths (Heightmap.GetBiome),
        // so it is one cell lookup and never a scan.
        internal static bool TrySkylandBiomeAt(Vector3 point, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (!Zones.ZoneModeRegistry.ModeActive || !Enabled || !s_index.TryGetValue(CellKey(point.x, point.z), out var list))
                return false;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                float sx = point.x - isl.OffX, sz = point.z - isl.OffZ;
                if (!IsSkyland(isl.Kind) || !Covers(in isl, sx, sz)) continue;
                if (point.y - isl.OffY <= SkylandBottomAt(in isl, sx, sz) - IslandContactMargin) continue;
                // 0.2.758: a biome painted on the skyland (VoxelLayerPaint) is its biome there - the HUD, weather, spawns.
                biome = VoxelLayerPaint.TryPaintedBiomeAt(point, out var painted) ? painted : isl.Biome;
                return true;
            }
            return false;
        }

        // 0.2.758 (FiresMinimapIslands): every skyland's shape stamped in its biome's map colour onto map pixels (size x size,
        // pixSize metres each, centred on the world origin as vanilla's map). Returns the pixels stamped.
        internal static int StampSkylands(Color32[] pixels, int size, float pixSize, System.Func<Heightmap.Biome, Color32> colorOf)
        {
            if (!Zones.ZoneModeRegistry.ModeActive || !Enabled) return 0;
            int half = size / 2, stamped = 0;
            float hp = pixSize * 0.5f;
            foreach (var isl in s_islands)
            {
                if (!IsSkyland(isl.Kind)) continue;
                var color = colorOf(isl.Biome);
                float cx = WorldX(in isl), cz = WorldZ(in isl);
                int x0 = Mathf.Max(0, Mathf.FloorToInt((cx - isl.R) / pixSize) + half), x1 = Mathf.Min(size - 1, Mathf.CeilToInt((cx + isl.R) / pixSize) + half);
                int z0 = Mathf.Max(0, Mathf.FloorToInt((cz - isl.R) / pixSize) + half), z1 = Mathf.Min(size - 1, Mathf.CeilToInt((cz + isl.R) / pixSize) + half);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float wx = (x - half) * pixSize + hp, wz = (z - half) * pixSize + hp;
                        if (!Covers(in isl, wx - isl.OffX, wz - isl.OffZ)) continue;
                        pixels[z * size + x] = color;
                        stamped++;
                    }
            }
            return stamped;
        }

        // 0.2.774: the biome of the skyland whose shape covers a map point, by XZ alone (the large map's hover has no height):
        // the island's own biome, the one StampSkylands draws there.
        internal static bool TrySkylandBiomeOver(float wx, float wz, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (!Zones.ZoneModeRegistry.ModeActive || !Enabled || !s_index.TryGetValue(CellKey(wx, wz), out var list)) return false;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                if (!IsSkyland(isl.Kind) || !Covers(in isl, wx - isl.OffX, wz - isl.OffZ)) continue;
                biome = isl.Biome;
                return true;
            }
            return false;
        }

        // Every skyland biome whose disc reaches into a square: the biomes a clutter patch there may grow.
        internal static Heightmap.Biome SkylandBiomesOver(Vector3 center, float halfSize)
        {
            var biomes = Heightmap.Biome.None;
            if (!Enabled || !Zones.ZoneModeRegistry.ModeActive) return biomes;
            float x0 = center.x - halfSize, x1 = center.x + halfSize, z0 = center.z - halfSize, z1 = center.z + halfSize;
            int i0 = Mathf.FloorToInt(x0 / INDEX_CELL), i1 = Mathf.FloorToInt(x1 / INDEX_CELL);
            int j0 = Mathf.FloorToInt(z0 / INDEX_CELL), j1 = Mathf.FloorToInt(z1 / INDEX_CELL);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    if (!s_index.TryGetValue(((long)i << 32) ^ (uint)j, out var list)) continue;
                    foreach (int k in list)
                    {
                        var isl = s_islands[k];
                        if (!IsSkyland(isl.Kind)) continue;
                        float cx = WorldX(in isl), cz = WorldZ(in isl);
                        float nx = Mathf.Clamp(cx, x0, x1) - cx, nz = Mathf.Clamp(cz, z0, z1) - cz;
                        if (nx * nx + nz * nz <= isl.R * isl.R) biomes |= isl.Biome;
                    }
                }
            return biomes;
        }

        // The terrain colour a voxel column in a skyland's band wears: the biome of the skyland whose rim is nearest.
        internal static bool TrySkylandColorAt(float wx, float wz, out Color32 color)
        {
            bool found = TrySkylandBiomeNear(wx, wz, out var biome);
            color = found ? Heightmap.GetBiomeColor(biome) : default;
            return found;
        }

        // The biome of the skyland whose rim is nearest, within the skin a skyland's band colours (FatSeasons snows by it).
        internal static bool TrySkylandBiomeNear(float wx, float wz, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (!s_index.TryGetValue(CellKey(wx, wz), out var list)) return false;
            float nearest = SkylandSkinReach;
            bool found = false;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                if (!IsSkyland(isl.Kind)) continue;
                float sx = wx - isl.OffX, sz = wz - isl.OffZ;
                float dx = sx - isl.X, dz = sz - isl.Z;
                float outside = Mathf.Sqrt(dx * dx + dz * dz) - isl.R;
                if (outside >= nearest) continue;
                if (isl.Shape != null)
                {
                    outside = (isl.Shape.Sample(sx, sz, out float width) - 1f) * width;
                    if (outside >= nearest) continue;
                }
                nearest = outside;
                biome = isl.Biome;
                found = true;
            }
            return found;
        }

        private readonly struct DressingPick
        {
            internal readonly string Prefab;
            internal readonly float Upto;
            internal readonly float ScaleMin;
            internal readonly float ScaleSpan;
            internal readonly float Lift;
            internal readonly bool Tall;
            internal readonly bool Hangs;

            internal DressingPick(string prefab, float upto, float scaleMin, float scaleMax, float lift, bool tall, bool hangs)
            {
                Prefab = prefab;
                Upto = upto;
                ScaleMin = scaleMin;
                ScaleSpan = scaleMax - scaleMin;
                Lift = lift;
                Tall = tall;
                Hangs = hangs;
            }

            internal float ScaleAt(float roll) => ScaleMin + ScaleSpan * roll;
        }

        private readonly struct SkylandSite
        {
            internal readonly string Location;
            internal readonly int Weight;
            internal readonly float Clear;
            internal readonly float Flat;

            internal SkylandSite(string location, int weight, float clear, float flat)
            {
                Location = location;
                Weight = weight;
                Clear = clear;
                Flat = flat;
            }

            internal float Reach => Mathf.Max(Clear, Flat) + SiteBlend;
        }

        private static SkylandSite Site(string location, int weight, float clear, float footprint)
            => new SkylandSite(location, weight, clear, Mathf.Clamp(footprint, SiteMinFlat, clear));

        private sealed class SkylandPalette
        {
            internal DressingPick[] Top;
            internal DressingPick NearRim;
            internal DressingPick[] Rim;
            internal DressingPick[] Belly;
            internal DressingPick[] Cliff;
            internal string Vines;
            internal float BareVineShare;
            internal string Centerpiece;
            internal string[] Remains = new string[0];
            internal SkylandSite[] Sites = new SkylandSite[0];
        }

        private const float TopLift = -0.1f;
        private const float RimLift = -0.6f;
        private const float BellyRockLift = -0.3f;
        private const float BellyRootLift = 0.9f;
        private const float CliffRockLift = -0.3f;

        private static DressingPick Tree(string prefab, float upto, float scaleMin = 1f, float scaleMax = 1f)
            => new DressingPick(prefab, upto, scaleMin, scaleMax, TopLift, true, false);

        private static DressingPick Plant(string prefab, float upto, float scaleMin = 1f, float scaleMax = 1f)
            => new DressingPick(prefab, upto, scaleMin, scaleMax, TopLift, false, false);

        private static DressingPick Stone(string prefab, float upto, float lift, float scaleMin = 1f, float scaleMax = 1f)
            => new DressingPick(prefab, upto, scaleMin, scaleMax, lift, false, false);

        private static DressingPick BellyRock(string prefab, float upto, float scaleMin, float scaleMax, float lift = BellyRockLift)
            => new DressingPick(prefab, upto, scaleMin, scaleMax, lift, false, false);

        private static DressingPick BellyRoot(string prefab, float upto, float growMin, float growMax)
            => new DressingPick(prefab, upto, growMin, growMax, BellyRootLift, false, true);

        private static DressingPick CliffRock(string prefab, float upto, float scaleMin, float scaleMax, float lift = CliffRockLift)
            => new DressingPick(prefab, upto, scaleMin, scaleMax, lift, false, false);

        private static DressingPick Ore(string prefab, float upto, float lift, float scaleMin = 1f, float scaleMax = 1f)
            => new DressingPick(prefab, upto, scaleMin, scaleMax, lift, true, false);

        private static DressingPick Bare(float upto) => new DressingPick(null, upto, 1f, 1f, 0f, false, false);

        private static DressingPick Choose(DressingPick[] picks, float roll)
        {
            foreach (var pick in picks)
                if (roll < pick.Upto) return pick;
            return picks[picks.Length - 1];
        }

        private static readonly string SkyRoot07 = ScaledRootPrefix + "root07";
        private static readonly string SkyRoot08 = ScaledRootPrefix + "root08";
        private static readonly string SkyRoot11 = ScaledRootPrefix + "root11";
        private static readonly string SkyRoot12 = ScaledRootPrefix + "root12";

        private static readonly SkylandPalette MeadowsPalette = new SkylandPalette
        {
            Top = new[]
            {
                Tree("Beech1", 0.12f), Tree("Oak1", 0.20f), Tree("Birch1", 0.28f), Tree("Beech_small1", 0.38f),
                Plant("Bush01", 0.50f), Plant("Bush02_en", 0.58f), Plant("RaspberryBush", 0.64f),
                Plant("Pickable_Dandelion", 0.72f), Plant("Pickable_Mushroom", 0.78f), Plant("Pickable_Flint", 0.84f),
                Plant("Pickable_Stone", 0.90f), Plant("Pickable_Branch", 1f),
            },
            NearRim = Plant("Bush01", 1f),
            Rim = new[] { Stone("Rock_3", 0.5f, RimLift), Stone("Rock_4", 1f, RimLift) },
            Belly = new[]
            {
                BellyRock("Rock_3", 0.2f, 1.2f, 2.5f, -0.8f), BellyRock("Rock_4", 0.4f, 1.2f, 2.5f),
                BellyRoot(SkyRoot07, 0.6f, 2f, 3.5f), BellyRoot(SkyRoot08, 0.75f, 2f, 3.5f),
                BellyRoot(SkyRoot11, 0.9f, 2f, 3.5f), BellyRoot(SkyRoot12, 1f, 2f, 3.5f),
            },
            Vines = ScaledVinesPrefab,
            BareVineShare = 0.45f,
            Cliff = new[] { CliffRock("rock4_coast", 0.55f, 0.3f, 0.45f, 0f), CliffRock("Rock_4", 0.85f, 2.5f, 4f, -0.5f), CliffRock("Rock_3", 1f, 2f, 3.2f, -1.5f) },
            Remains = new[] { "Beech_Stub", "OakStub", "BirchStub" },
            Sites = new[]
            {
                Site("WoodHouse1", 1, 8f, 6.9f), Site("WoodHouse2", 1, 8f, 7.1f), Site("WoodHouse3", 1, 10f, 9.7f),
                Site("WoodHouse4", 1, 8f, 4.2f), Site("WoodHouse5", 1, 8f, 4.7f), Site("WoodHouse6", 1, 8f, 4.9f),
                Site("WoodHouse7", 1, 8f, 6.5f), Site("WoodHouse8", 1, 10f, 7.8f), Site("WoodHouse9", 1, 8f, 8.1f),
                Site("WoodHouse10", 1, 8f, 8.1f), Site("WoodHouse11", 1, 8f, 6f), Site("WoodHouse12", 1, 6f, 2.9f),
                Site("WoodHouse13", 1, 9f, 6.1f), Site("Dolmen01", 2, 8f, 9f), Site("Dolmen02", 2, 8f, 6.8f),
                Site("Dolmen03", 2, 10f, 8.3f), Site("Runestone_Boars", 2, 8f, 2.1f), Site("Runestone_Meadows", 1, 8f, 2.1f),
                Site("StoneCircle", 2, 16f, 20.7f), Site("ShipSetting01", 1, 24f, 21.6f),
            },
        };

        private static readonly SkylandPalette BlackForestPalette = new SkylandPalette
        {
            Top = new[]
            {
                Tree("Pinetree_01", 0.18f, 0.9f, 1.4f), Tree("FirTree", 0.32f, 1.3f, 1.9f), Plant("FirTree_small", 0.46f, 0.4f, 0.8f),
                Plant("shrub_2", 0.58f, 0.6f, 1f), Plant("BlueberryBush", 0.63f), Stone("stubbe", 0.67f, TopLift, 0.8f, 1.2f),
                Tree("FirTree_oldLog", 0.70f, 0.9f, 1.1f), Plant("Pickable_Thistle", 0.75f), Plant("Pickable_Mushroom", 0.80f),
                Plant("Pickable_Branch", 0.88f), Plant("Pickable_Stone", 0.93f), Ore("MineRock_Tin", 0.95f, -0.5f), Ore("rock4_copper", 0.965f, -0.5f, 0.4f, 0.5f), Stone("Rock_4", 1f, -0.3f, 0.8f, 1.4f),
            },
            NearRim = Plant("shrub_2", 1f, 0.6f, 1f),
            Rim = new[] { Stone("Rock_3", 0.4f, RimLift), Stone("Rock_4", 0.85f, RimLift), Stone("rock4_forest", 1f, -0.3f, 0.15f, 0.22f) },
            Belly = new[]
            {
                BellyRock("Rock_3", 0.15f, 1.2f, 2.5f, -0.8f), BellyRock("Rock_4", 0.3f, 1.2f, 2.5f),
                BellyRoot(SkyRoot07, 0.55f, 2f, 3.5f), BellyRoot(SkyRoot08, 0.7f, 2f, 3.5f),
                BellyRoot(SkyRoot11, 0.85f, 2f, 3.5f), BellyRoot(SkyRoot12, 1f, 2f, 3.5f),
            },
            Vines = ScaledVinesPrefab,
            BareVineShare = 0.45f,
            Cliff = new[] { CliffRock("rock4_forest", 0.55f, 0.3f, 0.45f, 0f), CliffRock("Rock_4", 0.8f, 2.5f, 4f, -0.5f), CliffRock("Rock_3", 1f, 2f, 3.2f, -1.5f) },
            Remains = new[] { "Pinetree_01_Stub", "FirTree_Stub" },
            Sites = new[]
            {
                Site("Greydwarf_camp1", 4, 10f, 8.7f), Site("Runestone_Greydwarfs", 2, 8f, 10.6f), Site("Ruin1", 2, 10.7f, 6.6f),
                Site("Ruin2", 2, 9.9f, 5.7f), Site("StoneHouse3", 1, 6f, 4.5f), Site("StoneHouse4", 1, 7f, 5.7f),
                Site("StoneTowerRuins03", 1, 9f, 12.8f), Site("StoneTowerRuins07", 1, 9f, 6.8f), Site("StoneTowerRuins08", 1, 9f, 6.8f),
                Site("StoneTowerRuins09", 1, 7f, 6.4f), Site("StoneTowerRuins10", 1, 7f, 6.4f), Site("Dolmen01", 1, 8f, 9f),
                Site("Dolmen02", 1, 8f, 6.8f), Site("Dolmen03", 1, 10f, 8.3f), Site("Runestone_BlackForest", 1, 8f, 2.1f),
            },
        };

        private static readonly SkylandPalette SwampPalette = new SkylandPalette
        {
            Top = new[]
            {
                Tree("SwampTree1", 0.16f, 0.8f, 1.2f), Tree("SwampTree2", 0.21f, 0.35f, 0.5f), Plant("FirTree_small_dead", 0.37f, 0.4f, 0.8f),
                Plant("shrub_2_heath", 0.49f, 0.6f, 1f), Stone("stubbe", 0.53f, TopLift, 0.8f, 1.1f), Tree("SwampTree2_log", 0.56f, 0.2f, 0.28f),
                Tree("FirTree_oldLog", 0.59f), Plant("Pickable_Thistle", 0.65f), Plant("Pickable_Mushroom", 0.71f),
                Plant("Pickable_Branch", 0.83f), Plant("Pickable_Stone", 0.89f), Ore("mudpile_beacon", 0.92f, -0.5f, 1f, 1.5f), Stone("Rock_4", 1f, -0.3f, 0.8f, 1.4f),
            },
            NearRim = Plant("shrub_2_heath", 1f, 0.6f, 1f),
            Rim = new[] { Stone("Rock_4", 0.7f, RimLift), Stone("Rock_3", 1f, RimLift) },
            Belly = new[]
            {
                BellyRock("Rock_4", 0.3f, 1.2f, 2.5f),
                BellyRoot(SkyRoot07, 0.5f, 2f, 3.5f), BellyRoot(SkyRoot08, 0.65f, 2f, 3.5f),
                BellyRoot(SkyRoot11, 0.8f, 2f, 3.5f), BellyRoot(SkyRoot12, 0.9f, 2f, 3.5f), BellyRoot("HugeRoot1", 1f, 0.3f, 0.5f),
            },
            Vines = ScaledVinesPrefab,
            BareVineShare = 0.3f,
            Cliff = new[] { CliffRock("rock4_coast", 0.5f, 0.3f, 0.45f, 0f), CliffRock("Rock_4", 0.85f, 2.5f, 4f, -0.5f), CliffRock("Rock_3", 1f, 2f, 3.2f, -1.5f) },
            Remains = new[] { "SwampTree1_Stub" },
            Sites = new[]
            {
                Site("SwampHut1", 1, 8f, 6.1f), Site("SwampHut2", 1, 8f, 5.8f), Site("SwampHut3", 1, 8f, 6.9f),
                Site("SwampHut4", 1, 8f, 7.8f), Site("SwampHut5", 1, 10f, 9.5f), Site("Grave1", 3, 12f, 14.9f),
                Site("SwampRuin1", 2, 12f, 10.4f), Site("SwampRuin2", 2, 10f, 9.5f), Site("Runestone_Draugr", 2, 8f, 9.3f),
                Site("Runestone_Swamps", 1, 8f, 2.1f), Site("InfestedTree01", 1, 5f, 5.4f),
            },
        };

        private static readonly SkylandPalette MountainPalette = new SkylandPalette
        {
            Top = new[]
            {
                Tree("FirTree", 0.14f, 1.2f, 1.8f), Plant("FirTree_small", 0.34f, 0.4f, 0.8f), Stone("Rock_4", 0.48f, -0.3f, 0.8f, 1.6f),
                Stone("Rock_3", 0.54f, -0.5f, 1.2f, 2f), Stone("rock3_mountain", 0.58f, -1f, 0.25f, 0.4f),
                Plant("Pickable_StoneRock", 0.62f), Ore("MineRock_Obsidian", 0.645f, 0f, 0.9f, 1f), Ore("silvervein", 0.657f, -1.5f), Bare(1f),
            },
            NearRim = Stone("Rock_4", 1f, -0.3f, 0.8f, 1.6f),
            Rim = new[]
            {
                Stone("rock1_mountain", 0.3f, -0.5f, 0.15f, 0.25f), Stone("rock2_mountain", 0.6f, -0.5f, 0.18f, 0.3f),
                Stone("Rock_4", 1f, RimLift),
            },
            Belly = new[]
            {
                BellyRock("rock1_mountain", 0.3f, 0.15f, 0.3f, 0f), BellyRock("rock2_mountain", 0.6f, 0.18f, 0.35f, 0f),
                BellyRock("Rock_4", 0.8f, 1.2f, 2.5f), BellyRoot(SkyRoot08, 0.9f, 2f, 3f), BellyRoot(SkyRoot12, 1f, 2f, 3f),
            },
            BareVineShare = 1f,
            Cliff = new[] { CliffRock("rock1_mountain", 0.4f, 0.22f, 0.35f, 0f), CliffRock("rock2_mountain", 0.8f, 0.25f, 0.4f, 0f), CliffRock("Rock_4", 1f, 2.5f, 4f, -0.5f) },
            Remains = new[] { "FirTree_Stub" },
            Sites = new[]
            {
                Site("AbandonedLogCabin02", 2, 10.5f, 8f), Site("AbandonedLogCabin03", 2, 10f, 6f), Site("AbandonedLogCabin04", 2, 10f, 4.8f),
                Site("DrakeNest01", 4, 5f, 5.2f), Site("StoneTowerRuins04", 1, 12.3f, 11.3f), Site("StoneTowerRuins05", 1, 22f, 13.6f),
                Site("MountainGrave01", 1, 3.9f, 3.8f), Site("Waymarker01", 1, 3f, 1.6f), Site("Waymarker02", 1, 3f, 1.9f),
                Site("DrakeLorestone", 1, 4f, 3.5f), Site("Runestone_Mountains", 1, 8f, 2.1f),
            },
        };

        private static readonly SkylandPalette PlainsPalette = new SkylandPalette
        {
            Top = new[]
            {
                Tree("Birch1_aut", 0.12f, 0.6f, 1f), Tree("Birch2_aut", 0.18f, 0.8f, 1.1f), Plant("Bush01_heath", 0.38f),
                Plant("Bush02_en", 0.44f, 0.6f, 1.2f), Plant("shrub_2_heath", 0.54f, 0.6f, 1f), Plant("CloudberryBush", 0.62f),
                Plant("Pickable_Stone", 0.70f), Stone("Rock_4_plains", 0.78f, -0.3f, 0.8f, 1.5f), Bare(1f),
            },
            NearRim = Plant("Bush01_heath", 1f),
            Rim = new[] { Stone("Rock_4_plains", 0.6f, RimLift), Stone("rock4_heath", 1f, -0.3f, 0.15f, 0.22f) },
            Belly = new[]
            {
                BellyRock("Rock_4_plains", 0.5f, 1.2f, 2.5f),
                BellyRoot(SkyRoot07, 0.7f, 2f, 3.5f), BellyRoot(SkyRoot08, 0.85f, 2f, 3.5f), BellyRoot(SkyRoot11, 1f, 2f, 3.5f),
            },
            Vines = ScaledVinesPrefab,
            BareVineShare = 0.8f,
            Cliff = new[] { CliffRock("rock4_heath", 0.6f, 0.3f, 0.45f, 0f), CliffRock("Rock_4_plains", 1f, 2.5f, 4f, -0.5f) },
            Remains = new[] { "BirchStub" },
            Sites = new[]
            {
                Site("StoneTower1", 2, 14f, 11.7f), Site("StoneTower3", 2, 14f, 11.2f), Site("Ruin3", 2, 10f, 9.6f),
                Site("StoneHenge5", 1, 16f, 12.2f), Site("StoneHenge6", 1, 16f, 11.5f), Site("StoneHenge1", 1, 30f, 24.2f),
                Site("StoneHenge2", 1, 30f, 22f), Site("StoneHenge3", 1, 30f, 22.5f), Site("StoneHenge4", 1, 30f, 22.8f),
                Site("StoneHouse1_heath", 1, 6f, 3.8f), Site("StoneHouse2_heath", 1, 6f, 4.5f), Site("StoneHouse5_heath", 1, 7f, 5.2f),
                Site("Runestone_Plains", 1, 8f, 2.1f),
            },
        };

        private static readonly SkylandPalette MistlandsPalette = new SkylandPalette
        {
            Top = new[]
            {
                Tree("YggaShoot1", 0.04f, 0.45f, 0.65f), Tree("YggaShoot2", 0.08f, 0.45f, 0.65f), Tree("YggaShoot3", 0.12f, 0.45f, 0.65f),
                Plant("YggaShoot_small1", 0.36f, 1f, 1.6f), Plant("Pickable_Mushroom_Magecap", 0.42f),
                Plant("Pickable_Mushroom_JotunPuffs", 0.46f), Stone("rock_mistlands1", 0.56f, -0.3f, 0.6f, 1.2f),
                Stone("cliff_mistlands2", 0.60f, -0.5f, 0.15f, 0.25f), Ore("giant_sword1", 0.607f, 0f), Ore("giant_sword2", 0.614f, 0f),
                Ore("giant_helmet1", 0.621f, 0f), Ore("giant_helmet2", 0.628f, -1f), Ore("giant_ribs", 0.635f, 0f), Bare(1f),
            },
            NearRim = Plant("YggaShoot_small1", 1f, 1f, 1.6f),
            Rim = new[] { Stone("rock_mistlands1", 0.6f, RimLift, 0.6f, 1f), Stone("cliff_mistlands2", 1f, -0.5f, 0.12f, 0.18f) },
            Belly = new[]
            {
                BellyRock("rock_mistlands1", 0.35f, 0.8f, 1.4f, 0f), BellyRoot("YggdrasilRoot", 0.5f, 0.3f, 0.5f),
                BellyRoot(SkyRoot07, 0.7f, 2f, 3.5f), BellyRoot(SkyRoot11, 0.85f, 2f, 3.5f), BellyRoot(SkyRoot12, 1f, 2f, 3.5f),
            },
            Vines = ScaledVinesPrefab,
            BareVineShare = 0.55f,
            Centerpiece = "MistArea",
            Cliff = new[] { CliffRock("rock_mistlands1", 0.6f, 1.4f, 2f, 0f), CliffRock("cliff_mistlands2", 1f, 0.15f, 0.22f, 0f) },
            Remains = new[] { "ShootStump" },
            Sites = new[]
            {
                Site("Mistlands_GuardTower1_new", 1, 24f, 19f), Site("Mistlands_GuardTower2_new", 1, 24f, 19f),
                Site("Mistlands_GuardTower1_ruined_new", 1, 20f, 19f), Site("Mistlands_GuardTower1_ruined_new2", 1, 20f, 33.5f),
                Site("Mistlands_Giant1", 2, 11f, 16.2f), Site("Mistlands_Giant2", 2, 10f, 12.9f), Site("Mistlands_RoadPost1", 1, 6f, 1.4f),
                Site("Mistlands_StatueGroup1", 1, 16f, 10.5f), Site("Mistlands_Statue1", 1, 6f, 2f), Site("Mistlands_Statue2", 1, 6f, 2f),
                Site("Mistlands_Swords1", 1, 12f, 6.6f), Site("Mistlands_Swords2", 1, 12f, 7.3f), Site("Mistlands_Swords3", 1, 8f, 8.3f),
                Site("Runestone_Mistlands", 1, 12f, 2.1f),
            },
        };

        private static readonly SkylandPalette AshlandsPalette = new SkylandPalette
        {
            Top = new[]
            {
                Tree("AshlandsTree1", 0.10f, 0.7f, 1.1f), Tree("AshlandsTree3", 0.17f, 0.6f, 0.9f), Tree("AshlandsTree6", 0.21f, 0.5f, 0.7f),
                Plant("AshlandsBush1", 0.33f, 1f, 1.8f), Plant("AshlandsBranch1", 0.41f, 1f, 1.8f), Plant("AshlandsBranch2", 0.46f),
                Plant("AshlandsBranch3", 0.51f), Plant("Pickable_Charredskull", 0.59f), Plant("Pickable_Ashstone", 0.65f),
                Plant("Pickable_SmokePuff", 0.69f), Plant("Pickable_Pot_Shard", 0.72f),
                Stone("cliff_ashlands6", 0.80f, -0.5f, 0.6f, 1f), Stone("GraveStone_Broken_World", 0.82f, TopLift), Ore("UnstableLavaRock", 0.835f, 0f),
                Ore("ashland_pot2_red", 0.85f, -0.5f), Bare(1f),
            },
            NearRim = Plant("AshlandsBush1", 1f, 1f, 1.8f),
            Rim = new[] { Stone("cliff_ashlands5", 0.5f, -0.5f, 0.06f, 0.1f), Stone("cliff_ashlands6", 1f, -0.4f, 0.6f, 1f) },
            Belly = new[]
            {
                BellyRock("cliff_ashlands5", 0.4f, 0.06f, 0.1f), BellyRock("cliff_ashlands6", 0.7f, 0.8f, 1.2f),
                BellyRock("Ashlands_rock2", 0.85f, 0.15f, 0.22f, 0f), BellyRoot(SkyRoot08, 1f, 1.5f, 2.5f),
            },
            BareVineShare = 1f,
            Cliff = new[] { CliffRock("cliff_ashlands5", 0.4f, 0.06f, 0.1f), CliffRock("cliff_ashlands6", 0.8f, 1.2f, 1.8f, -0.5f), CliffRock("Ashlands_rock2", 1f, 0.15f, 0.22f, 0f) },
            Remains = new[] { "AshlandsTreeStump1", "AshlandsTreeStump2", "AshlandsTreeStump3" },
            Sites = new[]
            {
                Site("CharredRuins2", 2, 15f, 13.7f), Site("CharredRuins3", 2, 12f, 7.4f), Site("CharredRuins4", 2, 10f, 7.4f),
                Site("CharredRuins1", 1, 32f, 28.8f), Site("CharredStone_Spawner", 2, 20f, 7.6f), Site("VoltureNest", 3, 16f, 13.8f),
                Site("CharredTowerRuins1", 1, 22f, 24.2f), Site("CharredTowerRuins1_dvergr", 1, 22f, 24.2f),
                Site("CharredTowerRuins2", 1, 14f, 9.5f), Site("CharredTowerRuins3", 1, 14f, 11.4f), Site("SulfurArch", 1, 30f, 19.6f),
                Site("Runestone_Ashlands", 1, 12f, 2.1f),
            },
        };

        private static SkylandPalette PaletteOf(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.BlackForest: return BlackForestPalette;
                case Heightmap.Biome.Swamp: return SwampPalette;
                case Heightmap.Biome.Mountain: return MountainPalette;
                case Heightmap.Biome.Plains: return PlainsPalette;
                case Heightmap.Biome.Mistlands: return MistlandsPalette;
                case Heightmap.Biome.AshLands: return AshlandsPalette;
                default: return MeadowsPalette;
            }
        }

        // Every prefab a skyland's dressing can hold, in any biome, including the stumps its trees leave behind.
        internal static IEnumerable<string> AllDressingPrefabs()
        {
            foreach (var biome in SkylandLandBiomes)
            {
                var palette = PaletteOf(biome);
                foreach (var pick in palette.Top) if (pick.Prefab != null) yield return pick.Prefab;
                yield return palette.NearRim.Prefab;
                foreach (var pick in palette.Rim) yield return pick.Prefab;
                foreach (var pick in palette.Belly) yield return pick.Prefab;
                foreach (var pick in palette.Cliff) yield return pick.Prefab;
                if (palette.Vines != null) yield return palette.Vines;
                if (palette.Centerpiece != null) yield return palette.Centerpiece;
                foreach (var remains in palette.Remains) yield return remains;
            }
        }
    }
}
