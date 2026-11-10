using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Skyland biomes. Three in four skylands take the biome the world plans at their centre; the rest, and every
    // skyland over planned sea or Deep North, roll a random land biome. The roll hashes the island record, so every
    // peer derives the same biome from the synced registry. Each biome dresses from its own 1.0 world-gen vegetation,
    // its ore, relic and loot nodes a small share of the top. Scales come
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
            // 0.2.1077: on super flat the 2D GetBiome is forced to Meadows (the island isn't registered yet when it rolls), so
            // 12 of 14 skylands came out Meadows; the world's own biome there is what a normal world plans.
            // 0.2.1079: 1077's WorldBiome still answered Meadows for all 7 skylands on SuperFlatSkyCaves2 (the super-flat field). Fire:
            // "varied biomes like the VA world": on super flat every skyland takes the seeded pick from the 7 land biomes.
            if (FatSuperFlat.On) return SkylandLandBiomes[(int)(Hash(0, 0, isl.Variety, BiomePickSalt) * SkylandLandBiomes.Length)];
            var planned = PlannedBiomeAt(isl.X, isl.Z);
            bool plannedLand = planned != Heightmap.Biome.None && planned != Heightmap.Biome.Ocean
                               && planned != Heightmap.Biome.DeepNorth;
            if (plannedLand && Hash(0, 0, isl.Variety, BiomeShareSalt) < PlannedBiomeShare) return planned;
            return SkylandLandBiomes[(int)(Hash(0, 0, isl.Variety, BiomePickSalt) * SkylandLandBiomes.Length)];
        }

        internal static Heightmap.Biome SkylandBiome(int index) => s_islands[index].Biome;

        // 0.2.1096 (Fire: "our flat worlds no locations logic screwed us on skyland and cave location placement"; SFSC2 dedi:
        // "location site no" x10 vs "yes" x2, every Ashlands island "no"): a pond island never got a site, 30 % failed the chance,
        // and an island narrower than every site's reach got none. Now: pond islands take a site on dry ground (TryDrySite already
        // finds it), the chance is the cfg ("Skyland location chance" / "... on super flat", 1.0 there), and an island too narrow for
        // the weighted pick takes the palette's smallest site when it fits the island's whole heart. Still rolled per world seed +
        // island (Variety), so every peer agrees. Tally for the [Locations] line.
        internal static int SitesYes, SitesNoChance, SitesNoRoom, SitesNoDry, SitesNoPondDry, SitesPondIslands;
        internal static int SkylandCount() { int n = 0; foreach (var i in s_islands) if (IsSkyland(i.Kind)) n++; return n; }
        private static float SiteChance()
        {
            var c = FatSuperFlat.On ? FiresVoxelConfig.SkylandSiteChanceSuperFlat : FiresVoxelConfig.SkylandSiteChance;
            return c != null ? c.Value : SkylandSiteChance;
        }

        // 0.2.1137 [SkySite] (Fire: "fit more islands"): the 1137 rule for one island: a pond island takes a site on the dry ground
        // above its pond (DeriveWaters never maps a pond island, so the 1096 roller refused all 5 on SFSC3), and an island too
        // narrow for every palette site takes a mini site ('Skyland mini sites'). A new world (SkySites3) uses it on every island; an
        // existing world only on the islands the server switched to it (FatLocationReplan: no generated zone under them yet).
        internal static int SitesMini, SitesPondDry, SitesRule3Islands;
        private static bool UsesSiteRule3(in Island isl)
        {
            var p = FiresWorldProfile.Current;
            if (p == null) return false;
            if (p.SkySites3) return true;
            // Existing worlds: only islands the server already recorded (FatLocationReplan decides them at a session's start, once the
            // generated-zone set is loaded, and they take the rule from the NEXT load on, so no island changes shape mid-session).
            return p.SiteIslands3.Contains(isl.Variety);
        }

        // 0.2.1137: every skyland with no site, for FatLocationReplan's decision (Variety, world centre, radius).
        internal static List<(int variety, float x, float z, float r)> SkylandsWithoutSite()
        {
            var list = new List<(int, float, float, float)>();
            foreach (var isl in s_islands)
                if (IsSkyland(isl.Kind) && isl.Site <= 0) list.Add((isl.Variety, WorldX(in isl), WorldZ(in isl), isl.R));
            return list;
        }

        private static void Say(in Island isl, string what)
        {
            if (!Zones.FatLocationReplan.SiteLogOn) return;
            FiresLogger.LogDebug($"[SkySite] island {isl.Variety} ({WorldX(in isl):0},{WorldZ(in isl):0}) {isl.Biome}: {what}");
        }

        private static void RollSkylandSite(ref Island isl)
        {
            if (isl.PondLvl > 0f) SitesPondIslands++;
            bool rule3 = UsesSiteRule3(in isl);
            if (rule3) SitesRule3Islands++;
            if (isl.PondLvl > 0f && isl.Waters == null && !rule3) { SitesNoPondDry++; Say(in isl, "skipped: pond with no water map (pre-1137 rule)"); return; }
            if (Hash(0, 0, isl.Variety, SiteChanceSalt) >= SiteChance()) { SitesNoChance++; Say(in isl, "skipped: chance"); return; }
            var sites = PaletteOf(isl.Biome).Sites;
            float heartWidth = isl.Shape != null ? isl.Shape.HeartWidth : isl.R;
            float room = heartWidth * SiteRoomShare;
            int total = 0;
            foreach (var site in sites) if (site.Reach <= room) total += site.Weight;
            float reachCap = float.MaxValue;
            int pick = -1;
            SkylandSite chosen = default;
            string kind = "normal";
            if (total == 0)
            {
                int smallest = -1;
                for (int s = 0; s < sites.Length; s++) if (smallest < 0 || sites[s].Reach < sites[smallest].Reach) smallest = s;
                // 0.2.1102 (SFSC3: "skyland sites 7 of 25 (room 13)"): HeartWidth is the widest bone's RADIUS, and the smallest
                // palette site's Reach (its clear radius + the 4 m ease-back) is ~10 m, so every island with a heart under ~10 m
                // got none. On a world made on 0.2.1102+ (FiresWorldProfile.SkySites2) the smallest site is taken whenever its
                // flat footprint fits the heart, and its ease-back is clamped to the heart. Older worlds keep the 1096 rule, so
                // islands already built never change shape. 0.2.1137: an island on the 1137 rule takes it too.
                bool newRule = rule3 || (FiresWorldProfile.Current != null && FiresWorldProfile.Current.SkySites2);
                bool smallestFits = smallest >= 0 && (sites[smallest].Reach <= heartWidth || (newRule && sites[smallest].Flat + 1f <= heartWidth));
                if (smallestFits)
                {
                    if (sites[smallest].Reach > heartWidth) reachCap = heartWidth;
                    room = Mathf.Max(heartWidth, sites[smallest].Reach);
                    total = sites[smallest].Weight;
                }
                else if (rule3 && TryMiniSite(in isl, heartWidth, out int mini, out chosen))
                {
                    // 0.2.1137: a mini site (a runestone, a waymarker) when no palette site fits the island's heart.
                    pick = sites.Length + mini;
                    reachCap = heartWidth;
                    room = Mathf.Max(heartWidth, chosen.Reach);
                    kind = "mini";
                }
                else { SitesNoRoom++; Say(in isl, $"skipped: no room (heart {heartWidth:0.0} m{(rule3 ? ", no mini site fits" : "")})"); return; }
            }
            if (pick < 0)
            {
                int roll = (int)(Hash(0, 0, isl.Variety, SitePickSalt) * total);
                pick = 0;
                for (; pick < sites.Length; pick++)
                {
                    if (sites[pick].Reach > room) continue;
                    roll -= sites[pick].Weight;
                    if (roll < 0) break;
                }
                if (pick >= sites.Length) { SitesNoRoom++; Say(in isl, "skipped: no room"); return; }
                chosen = sites[pick];
            }
            float drift = (room - chosen.Reach) * SiteDrift * Hash(0, 0, isl.Variety, SiteDriftSalt);
            float bearing = Hash(0, 0, isl.Variety, SiteBearingSalt) * Mathf.PI * 2f;
            var anchor = isl.Shape != null ? isl.Shape.Heart : new Vector2(isl.X, isl.Z);
            var spot = anchor + new Vector2(Mathf.Cos(bearing), Mathf.Sin(bearing)) * drift;
            if (isl.Waters != null && !TryDrySite(in isl, room, chosen.Reach, out spot)) { SitesNoDry++; Say(in isl, "skipped: no dry spot"); return; }
            if (rule3 && isl.PondLvl > 0f && isl.Waters == null)
            {
                if (!TryPondDrySite(in isl, Mathf.Min(chosen.Reach, reachCap), out spot)) { SitesNoPondDry++; Say(in isl, "skipped: no dry ground above the pond"); return; }
                SitesPondDry++;
                kind = kind == "mini" ? "pond+mini" : "pond";
            }
            if (kind.EndsWith("mini")) SitesMini++;
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
            isl.SiteReach = Mathf.Min(chosen.Reach, reachCap);   // 0.2.1102: a narrow island eases back within its heart
            isl.SiteClear = chosen.Clear;
            isl.SiteYaw = Mathf.Floor(Hash(0, 0, isl.Variety, SiteYawSalt) * 16f) * 22.5f;
            isl.Site = pick + 1;
            SitesYes++;
            Say(in isl, $"site {chosen.Location} ({kind}{(rule3 ? ", 1137 rule" : "")}) at ({isl.SiteX:0},{isl.SiteZ:0}), flat {isl.SiteFlat:0.0} m, reach {isl.SiteReach:0.0} m");
        }

        // ── 0.2.1137: mini sites + the pond-aware dry site ─────────────────────────────────────────────────────────
        private static string s_miniParsedFrom;
        private static readonly Dictionary<Heightmap.Biome, List<SkylandSite>> s_mini = new Dictionary<Heightmap.Biome, List<SkylandSite>>();

        private static List<SkylandSite> MiniSitesOf(Heightmap.Biome biome)
        {
            string cfg = FiresVoxelConfig.SkylandMiniSites != null ? FiresVoxelConfig.SkylandMiniSites.Value : "";
            if (!ReferenceEquals(cfg, s_miniParsedFrom) && cfg != s_miniParsedFrom)
            {
                s_miniParsedFrom = cfg;
                s_mini.Clear();
                foreach (var entry in (cfg ?? "").Split(','))
                {
                    var parts = entry.Split(':');
                    if (parts.Length != 4) continue;
                    if (!System.Enum.TryParse(parts[0].Trim(), true, out Heightmap.Biome b)) continue;
                    if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float clear)) continue;
                    if (!float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float flat)) continue;
                    if (!s_mini.TryGetValue(b, out var list)) s_mini[b] = list = new List<SkylandSite>();
                    list.Add(Site(parts[1].Trim(), 1, Mathf.Max(1f, clear), flat));
                }
            }
            return s_mini.TryGetValue(biome, out var found) ? found : null;
        }

        // A mini site whose flat footprint fits the island's heart; seeded pick among those that fit.
        private static bool TryMiniSite(in Island isl, float heartWidth, out int index, out SkylandSite site)
        {
            index = -1;
            site = default;
            var list = MiniSitesOf(isl.Biome);
            if (list == null || list.Count == 0) return false;
            var fits = new List<int>();
            for (int i = 0; i < list.Count; i++) if (list[i].Flat + 1f <= heartWidth) fits.Add(i);
            if (fits.Count == 0) return false;
            index = fits[Mathf.Min(fits.Count - 1, (int)(Hash(0, 0, isl.Variety, SitePickSalt) * fits.Count))];
            site = list[index];
            return true;
        }

        // The location of a site index past the palette: a mini site.
        private static bool TrySiteAt(Heightmap.Biome biome, int site, out SkylandSite found)
        {
            found = default;
            var sites = PaletteOf(biome).Sites;
            if (site <= 0) return false;
            if (site - 1 < sites.Length) { found = sites[site - 1]; return true; }
            var mini = MiniSitesOf(biome);
            int m = site - 1 - sites.Length;
            if (mini == null || m < 0 || m >= mini.Count) return false;
            found = mini[m];
            return true;
        }

        // A pond island (DeriveWaters maps none): dry ground standing PondDryLift above the pond, clear of the pond's circle and its
        // rill by the site's reach + a gap, inside the island's shape. Seeded spiral from the heart, the same on every peer.
        private const float PondDryLift = 1.5f;
        private static bool TryPondDrySite(in Island isl, float reach, out Vector2 site)
        {
            var heart = isl.Shape != null ? isl.Shape.Heart : new Vector2(isl.X, isl.Z);
            float turn = Hash(0, 0, isl.Variety, SiteBearingSalt + 11) * Mathf.PI * 2f;
            float spread = (isl.Shape != null ? isl.Shape.HeartWidth : isl.R) * 1.2f + isl.PondR;
            const int tries = 48;
            for (int k = 0; k < tries; k++)
            {
                float a = turn + k * 2.3999632f;
                float d = spread * Mathf.Sqrt((k + 0.5f) / tries);
                site = heart + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d;
                float dx = site.x - isl.PondX, dz = site.y - isl.PondZ;
                if (Mathf.Sqrt(dx * dx + dz * dz) - isl.PondR < reach + 2f) continue;
                if (!float.IsNaN(isl.RillAng) && isl.RillLen > 0f)
                {
                    float rx = Mathf.Cos(isl.RillAng), rz = Mathf.Sin(isl.RillAng);
                    float along = Mathf.Clamp(dx * rx + dz * rz, 0f, isl.RillLen);
                    float ex = dx - rx * along, ez = dz - rz * along;
                    if (Mathf.Sqrt(ex * ex + ez * ez) < reach + 2f) continue;
                }
                if (isl.Shape != null && isl.Shape.Sample(site.x, site.y, out float width) > 1f - (reach + 2f) / Mathf.Max(1f, width)) continue;
                bool dry = NaturalTopAt(in isl, site.x, site.y) > isl.PondLvl + PondDryLift;
                for (int s = 0; dry && s < 4; s++)
                {
                    float sa = s * Mathf.PI * 0.5f;
                    dry = NaturalTopAt(in isl, site.x + Mathf.Cos(sa) * reach * 0.7f, site.y + Mathf.Sin(sa) * reach * 0.7f) > isl.PondLvl + PondDryLift;
                }
                if (dry) return true;
            }
            site = default;
            return false;
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
            if (!TrySiteAt(isl.Biome, isl.Site, out var siteDef)) return false;   // 0.2.1137: past the palette = a mini site
            location = siteDef.Location;
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

        // 0.2.1075 (add-only contract for FTS): the biome of ANY floating island covering a point near its height band, on every world
        // type: a skyland answers its own biome (TrySkylandBiomeAt); a lattice island answers the world's own biome at x,z (ocean ->
        // Meadows), the same its content gets on super flat (FatSuperFlat.WorldBiome). False when no island covers the point or the
        // point is not within BandSlack of the island's top..bottom.
        private const float BandSlack = 6f;
        internal static bool TryIslandBiomeAt(Vector3 point, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (!Enabled || !IslandAt(point.x, point.z)) return false;
            if (!TryTopAt(point.x, point.z, out float top) || !TryBottomAt(point.x, point.z, out float bottom)) return false;
            if (point.y > top + BandSlack || point.y < bottom - BandSlack) return false;
            if (TrySkylandBiomeAt(point, out biome) && biome != Heightmap.Biome.None) return true;
            biome = FatSuperFlat.WorldBiome(point.x, point.z);
            if (biome == Heightmap.Biome.Ocean || biome == Heightmap.Biome.None) biome = Heightmap.Biome.Meadows;
            return true;
        }

        // The biome of the skyland a point stands on (or is inside). Runs on hot vanilla paths (Heightmap.GetBiome),
        // so it is one cell lookup and never a scan.
        internal static bool TrySkylandBiomeAt(Vector3 point, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (!Zones.ZoneModeSkylands.Driven || !Enabled || !s_index.TryGetValue(CellKey(point.x, point.z), out var list))
                return false;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                float sx = point.x - isl.OffX, sz = point.z - isl.OffZ;
                if (!IsSkyland(isl.Kind) || !Covers(in isl, sx, sz)) continue;
                if (point.y - isl.OffY <= SkylandBottomAt(in isl, sx, sz) - IslandContactMargin) continue;
                // A biome painted on the skyland (VoxelLayerPaint) is its biome there - the HUD, weather, spawns.
                biome = VoxelLayerPaint.TryPaintedBiomeAt(point, out var painted) ? painted : isl.Biome;
                return true;
            }
            return false;
        }

        // For FiresMinimapIslands: every skyland's shape stamped in its biome's map colour onto map pixels (size x size,
        // pixSize metres each, centred on the world origin as vanilla's map). Returns the pixels stamped.
        internal static int StampSkylands(Color32[] pixels, int size, float pixSize, System.Func<Heightmap.Biome, Color32> colorOf)
        {
            if (!Zones.ZoneModeSkylands.Driven || !Enabled) return 0;
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

        // The biome of the skyland whose shape covers a map point, by XZ alone (the large map's hover has no height):
        // the island's own biome, the one StampSkylands draws there.
        internal static bool TrySkylandBiomeOver(float wx, float wz, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (!Zones.ZoneModeSkylands.Driven || !Enabled || !s_index.TryGetValue(CellKey(wx, wz), out var list)) return false;
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
            if (!Enabled || !Zones.ZoneModeSkylands.Driven) return biomes;
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
        // ── 0.2.1095: Ashlands skyland lava + the island mesh biome proof ([IslandPaint]) ─────────────────────────────────
        // Fire (live 1093): "ashlands sky islands are still drawing the biome paint on the ground and not on the islands ... sky
        // islands need lava". The island's terrain now wears its own biome (VoxelZoneStreamer's skylandBiome gate), and an Ashlands
        // skyland's top grows lava pools and veins: two octaves of Perlin noise on world x,z (the same answer on every peer), only on
        // the top skin (within LavaTopSkin of the island's top), never on its sides or underside. VoxelLayerPaint draws it through the
        // palette (alpha 1 = lava on Ashlands-coloured vertices) and Heightmap.IsLava / GetLava answer it for damage. Visual + damage
        // only: nothing saved, no heights.
        private const float LavaTopSkin = 1.5f;
        private const float LavaLo = 0.58f, LavaHi = 0.66f;

        // 0..1 lava at a point ON an Ashlands skyland's top; 0 anywhere else (ground, other biomes, the sides).
        internal static float IslandLavaAt(Vector3 p)
        {
            if (!Enabled || !TrySkylandBiomeAt(p, out var biome) || biome != Heightmap.Biome.AshLands) return 0f;
            if (!TryTopAt(p.x, p.z, out float top) || p.y < top - LavaTopSkin) return 0f;
            float n = Mathf.PerlinNoise(p.x * 0.031f + 517.3f, p.z * 0.031f + 211.9f) * 0.7f
                    + Mathf.PerlinNoise(p.x * 0.113f + 91.7f, p.z * 0.113f + 377.1f) * 0.3f;
            return Mathf.Clamp01((n - LavaLo) / (LavaHi - LavaLo));
        }

        // From VoxelZoneStreamer when a skyland-band chunk is generated: once per island, which biome its mesh wears and what the
        // ground under it keeps (proof that nothing transfers either way), with the island's lava cell count over a 2 m grid.
        private static readonly HashSet<uint> s_paintSaid = new HashSet<uint>();
        internal static void NoteIslandMeshBiome(int kx, int kz, float chunkBottom, float[] ground, Color32[] groundBiome)
        {
            float cx = kx * 32f + 16f, cz = kz * 32f + 16f;
            if (!s_index.TryGetValue(CellKey(cx, cz), out var list)) return;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                if (!IsSkyland(isl.Kind) || !Covers(in isl, cx - isl.OffX, cz - isl.OffZ)) continue;
                uint id = IslandHash(isl);
                lock (s_paintSaid) { if (!s_paintSaid.Add(id)) return; }
                int mid = (VoxelChunk.CELLS / 2 + 1) + VoxelChunk.SAMPLES * (VoxelChunk.CELLS / 2 + 1);
                var g = mid < groundBiome.Length ? groundBiome[mid] : default;
                string groundName = BiomeOfColour(g);
                int lava = 0;
                if (isl.Biome == Heightmap.Biome.AshLands)
                    for (float x = cx - 16f; x < cx + 16f; x += 2f)
                        for (float z = cz - 16f; z < cz + 16f; z += 2f)
                            if (TryTopAt(x, z, out float t) && IslandLavaAt(new Vector3(x, t, z)) > 0.5f) lava++;
                FiresLogger.LogInfo($"[IslandPaint] island {id:x8} ({isl.Biome}): island mesh biome {isl.Biome} (was {groundName} through 0.2.1094: the ground's colour), "
                    + (isl.Biome == Heightmap.Biome.AshLands ? $"lava cells {lava} on island (2 m grid, chunk ({kx},{kz})), " : "")
                    + $"ground under island biome {groundName} (no transfer)");
                return;
            }
        }

        private static string BiomeOfColour(Color32 c)
        {
            foreach (Heightmap.Biome b in System.Enum.GetValues(typeof(Heightmap.Biome)))
            {
                if (b == Heightmap.Biome.None || b == Heightmap.Biome.All) continue;
                var bc = Heightmap.GetBiomeColor(b);
                if (bc.r == c.r && bc.g == c.g && bc.b == c.b && bc.a == c.a) return b.ToString();
            }
            return $"blend({c.r},{c.g},{c.b},{c.a})";
        }
        // Lava damage on an Ashlands skyland. Vanilla's Character.UpdateLava (1.0.15 Character.cs:573-600) only heats a character
        // when WorldGenerator.IsAshlands(x, z) (the ground's biome below, not the island's), its GetGroundData heightmap is not null
        // (the ray hits the island's voxel collider: no Heightmap) and nothing non-heightmap is underfoot (the island again). So
        // an island's lava never burned. After vanilla's step, a character on an Ashlands skyland's top over one of its pools gets
        // vanilla's own heat build-up (proximity x dt x m_heatBuildupBase x height factor x (1 - heat resistance)); vanilla's
        // UpdateHeatDamage turns it into damage as on Ashlands ground.
        [HarmonyLib.HarmonyPatch(typeof(Character), "UpdateLava")]
        private static class IslandLavaHeat
        {
            private static readonly HarmonyLib.AccessTools.FieldRef<Character, float> Heat = HarmonyLib.AccessTools.FieldRefAccess<Character, float>("m_lavaHeatLevel");
            private static readonly HarmonyLib.AccessTools.FieldRef<Character, float> Timer = HarmonyLib.AccessTools.FieldRefAccess<Character, float>("m_lavaTimer");
            private static readonly HarmonyLib.AccessTools.FieldRef<Character, float> MinMask = HarmonyLib.AccessTools.FieldRefAccess<Character, float>("m_minLavaMaskThreshold");
            private static float s_logAt;

            private static void Postfix(Character __instance, float dt)
            {
                if (__instance == null || !Enabled) return;
                var p = __instance.transform.position;
                if (!TrySkylandBiomeAt(p, out var biome) || biome != Heightmap.Biome.AshLands || !TryTopAt(p.x, p.z, out float top)) return;
                float above = p.y - top;
                if (above < -LavaTopSkin || above > __instance.m_lavaAirDamageHeight) return;
                float lava = IslandLavaAt(new Vector3(p.x, top, p.z));
                if (lava <= MinMask(__instance)) return;
                float height = (__instance.m_lavaAirDamageHeight - Mathf.Max(0f, above)) / __instance.m_lavaAirDamageHeight;
                Heat(__instance) += lava * dt * __instance.m_heatBuildupBase * height * (1f - __instance.GetEquipmentHeatResistanceModifier());
                Timer(__instance) = 0f;
                if (__instance.IsPlayer() && FiresLogger.DebugEnabled && Time.unscaledTime >= s_logAt)
                {
                    s_logAt = Time.unscaledTime + 10f;
                    FiresLogger.LogDebug($"[IslandPaint] lava heat on an Ashlands skyland at ({p.x:0.0},{p.z:0.0}): pool {lava:0.00}, {above:0.0} m over the top, heat {Heat(__instance):0.00}");
                }
            }
        }
    }
}
