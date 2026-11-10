using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    /// <summary>
    /// The "Super flat" admin build / test / blueprint world, linked through Dimensions so build yards and
    /// big ZDO counts stay off the main server. Chosen on the New World screen (FiresNewWorldOptions) and kept in the world's profile
    /// (FiresWorldProfile.SuperFlat, sent to every client in the handshake), so every gate here reads the WORLD, never a config:
    ///   - ground: flat Meadows at y = 50 (water is at 30) inside the world's radius (2500 m = 25% of vanilla); vanilla-depth ocean past it
    ///   - no locations except the start temple, no vegetation, pickables or rocks, no grass / clutter
    ///   - no spawning of any kind: spawn systems, creature spawners, spawn areas, random events (raids)
    /// Everything is a postfix or a "skip" prefix that runs only on a super flat world; every other world is untouched.
    /// </summary>
    internal static class FatSuperFlat
    {
        private const float ShoreWidth = 24f;     // the ground slopes from y 50 down to the sea floor over this many metres past the radius
        private const float SeaFloorY = -10f;     // about vanilla deep-ocean depth under the 30 m water line

        internal static bool On => FiresWorldProfile.CurrentIsSuperFlat;

        // Draw biomes with territories for testing:
        // CONTENT AREAS. A mod registers a test (FatApi.AddSuperFlatContentArea, first user RPGM's ForceBiome territories): a point it
        // answers true for is a normal world again on super flat for everything except the flat ground: the biome / biome sector answer
        // earlier patches gave (the territory's forced biome) stands, the height mask is not blacked out, vegetation is placed, auto
        // clutter grows and spawners / spawn systems run. Everywhere else super flat stays empty. Copy-on-write array: generator threads
        // call it; a test must be cheap, thread-safe and never call back into GetBiome. Each blocked/bypassed switch logs once.
        private static System.Func<float, float, bool>[] s_content = new System.Func<float, float, bool>[0];
        private static readonly object s_contentLock = new object();
        [System.ThreadStatic] private static int t_inContent;
        private static int s_contentFaults;
        internal static int ContentAreaCount => s_content.Length;

        internal static void AddContentArea(System.Func<float, float, bool> test)
        {
            lock (s_contentLock)
            {
                var a = new System.Func<float, float, bool>[s_content.Length + 1];
                System.Array.Copy(s_content, a, s_content.Length);
                a[a.Length - 1] = test;
                s_content = a;
            }
        }

        internal static bool RemoveContentArea(System.Func<float, float, bool> test)
        {
            lock (s_contentLock)
            {
                int i = System.Array.IndexOf(s_content, test);
                if (i < 0) return false;
                var a = new System.Func<float, float, bool>[s_content.Length - 1];
                System.Array.Copy(s_content, 0, a, 0, i);
                System.Array.Copy(s_content, i + 1, a, i, a.Length - i);
                s_content = a;
                return true;
            }
        }

        internal static bool ContentAt(float wx, float wz)
        {
            var tests = s_content;
            if (tests.Length == 0 || t_inContent > 0) return false;
            t_inContent++;
            try
            {
                foreach (var t in tests)
                {
                    try { if (t(wx, wz)) return true; }
                    catch (System.Exception ex)
                    {
                        if (System.Threading.Interlocked.Increment(ref s_contentFaults) == 1)
                            FiresLogger.LogWarning($"[FAT] super flat: a content-area test threw, skipped (logged once): {ex.Message}");
                    }
                }
                return false;
            }
            finally { t_inContent--; }
        }

        private static readonly System.Collections.Generic.HashSet<string> s_bypassLogged = new System.Collections.Generic.HashSet<string>();
        private static void LogBypass(string what, float x, float z)
        {
            lock (s_bypassLogged) { if (!s_bypassLogged.Add(what)) return; }
            FiresLogger.LogInfo($"[FAT] super flat: {what} ALLOWED in a content area (first at {x:0},{z:0}); outside content areas it stays blocked.");
        }

        private static bool s_logged;

        private static float Radius => FiresWorldProfile.Current?.WorldRadius ?? FiresWorldProfile.SuperFlatRadius;

        internal static float HeightAt(float wx, float wy)
        {
            if (FatHaloArc.RingOnly) return FatHaloArc.RingOnlyGround;   // 0.2.1036 HaloFlat: the ring only, no floor (far below, never built)
            float d = Mathf.Sqrt(wx * wx + wy * wy) - Radius;
            if (d <= 0f) return FiresWorldProfile.SuperFlatHeight;
            return Mathf.Lerp(FiresWorldProfile.SuperFlatHeight, SeaFloorY, Mathf.Clamp01(d / ShoreWidth));
        }

        private static bool Land(float wx, float wy) => wx * wx + wy * wy <= Radius * Radius;
        internal static bool IsLand(float wx, float wz) => Land(wx, wz);   // ONE land rule for the flat field, the biome postfix and the corner blend

        private static void LogOnce(string where)
        {
            if (s_logged) return;
            s_logged = true;
            FiresLogger.LogInfo($"[FAT] super flat: this world is the admin build world - flat Meadows at y {FiresWorldProfile.SuperFlatHeight:0} "
                + $"inside radius {Radius:0}, ocean past it; no locations, vegetation, clutter or spawning (first seen in {where}).");
        }

        // ── Ground ──────────────────────────────────────────────────────────
        // Postfixes, Last: whatever any other generator answered (vanilla, Fires, a paint, a BetterContinents map), a super flat world's
        // answer is this one. The mask is black (no forest / clutter weighting).
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight))]
        private static class Height
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(float wx, float wy, ref Color mask, ref float __result)
            {
                if (!On) return;
                __result = HeightAt(wx, wy);
                if (ContentAt(wx, wy)) return;   // A content area keeps its biome's mask (vegetation density / forest)
                mask = Color.black;
            }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(float), typeof(float), typeof(float), typeof(bool))]
        private static class BiomeAt
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(float wx, float wy, ref Heightmap.Biome __result)
            {
                if (!On) return;
                if (KeepOwnBiome(wx, wy, ref __result)) return;   // 0.2.1075: islands and the world's biome for cave content
                if (Land(wx, wy) && Zones.BiomePaint.TryPaintedSectorAnyScope(wx, wy, out var painted, out _)) { __result = painted; return; }   // 0.2.1086: paint = the biome, corner scope included (height stays flat)
                if (Land(wx, wy) && ContentAt(wx, wy) && __result != Heightmap.Biome.Ocean) return;   // The earlier answer (a forced biome) stands; never the seed's sea under the flat land (0.2.1024)
                __result = Land(wx, wy) ? Heightmap.Biome.Meadows : Heightmap.Biome.Ocean;
            }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(Vector3))]
        private static class BiomeAtPoint
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Vector3 __0, ref Heightmap.Biome __result)   // positional: vanilla's parameter name is not ours to rely on
            {
                try
                {
                    if (!On) return;
                    if (OffSurface(__0)) return;   // 0.2.1075: islands and caves keep the world's own biome (Fire: only the overworld is flat)
                    if (Land(__0.x, __0.z) && Zones.BiomePaint.TryPaintedSectorAnyScope(__0.x, __0.z, out var painted, out _)) { __result = painted; return; }   // 0.2.1086
                    if (Land(__0.x, __0.z) && ContentAt(__0.x, __0.z) && __result != Heightmap.Biome.Ocean) return;
                    __result = Land(__0.x, __0.z) ? Heightmap.Biome.Meadows : Heightmap.Biome.Ocean;
                
                }
                catch (System.Exception e) { VerdantsAscent.Terrain.Zones.FatBiomeResolver.NoteThrow("FatSuperFlat BiomeAtPoint", __0, e); throw; }   // 0.2.1201
            }
        }

        // ── Biome sectors ─────────────────────────────────────────
        // The ground once drew as SAND with grass only on the temple patch. Heightmap corners, the environment and the
        // player's biome read the biome-sector grid (GetBiomeSector), not GetBiome, so the client mesh kept vanilla's biomes. On a super
        // flat world every sector inside the land is Meadows (the same kind of BiomeSector BiomePaint answers with); Ocean past the edge.
        private static BiomeSector s_meadows, s_ocean;
        private static BiomeSector Sector(float wx, float wz)
            => Land(wx, wz) ? (s_meadows ?? (s_meadows = new BiomeSector(null, Heightmap.Biome.Meadows))) : (s_ocean ?? (s_ocean = new BiomeSector(null, Heightmap.Biome.Ocean)));

        // 0.2.1086 (Fire: "painted biomes should get normal goddamn biome logic"): on the flat land a painted circle's sector answers
        // every lookup, a heightmap's corner sectors included (BiomePaint skips paint in the corner scope on other worlds to keep
        // their ground shape; super flat's height is flat regardless). Through 1085 the corner scope left the seed's own sector
        // standing (paint skipped, ContentAt stood aside), so a painted spot's heightmap carried the seed's corner biomes.
        private static bool PaintedSector(float wx, float wz, ref BiomeSector sector)
        {
            if (!Land(wx, wz) || !Zones.BiomePaint.TryPaintedSectorAnyScope(wx, wz, out _, out var painted)) return false;
            sector = painted;
            return true;
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(float), typeof(float), typeof(bool))]
        private static class SectorAt
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(float __0, float __1, ref BiomeSector __result) { if (On && !KeepOwnSector(__0, __1, ref __result) && !PaintedSector(__0, __1, ref __result) &&!(Land(__0, __1) && ContentAt(__0, __1) && __result != null && __result.Biome != Heightmap.Biome.Ocean)) __result = Sector(__0, __1); }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(Vector3), typeof(bool))]
        private static class SectorAtPoint
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Vector3 __0, ref BiomeSector __result) { if (On && !OffSurface(__0) && !KeepOwnSector(__0.x, __0.z, ref __result) && !PaintedSector(__0.x, __0.z, ref __result) &&!(Land(__0.x, __0.z) && ContentAt(__0.x, __0.z) && __result != null && __result.Biome != Heightmap.Biome.Ocean)) __result = Sector(__0.x, __0.z); }
        }

        // ── Locations: none, the start temple included ──────────────────────
        // Every location's quota already counts as met, so none is ever planned; PlaceLocations is a second wall. Without a start temple
        // a new character spawns at the world's centre on the flat ground.
        [HarmonyPatch(typeof(ZoneSystem), "CountNrOfLocation")]
        private static class NoLocationPlans
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(ZoneSystem.ZoneLocation location, ref int __result)
            {
                if (!On || location == null) return true;
                // The start temple and nothing else: without it a new character never spawned (the client waited for
                // a StartTemple that never came). Vanilla plans it centre-first, so it lands at the middle on the flat ground.
                if (location.m_prefabName == StartTempleName || IsAllowedExtra(location.m_prefabName)) return true;   // The Mausoleum auto-places too
                // 0.2.1075: caves keep their locations on super flat: a location that may go underground is planned as on a normal
                // world; SpawnLocation then puts it in the cave under its spot or refuses it (never on the flat ground).
                if (CavesOn && FatUndergroundLocations.MayGoUnderground(location)) return true;
                LogOnce("location planning");
                Zones.FatLocationsReport.NoteOverworldSkipped(location.m_prefabName);   // 0.2.1096 [Locations]
                __result = location.m_quantity;
                return false;
            }
        }

        // Only the start temple is ever planned on a super flat world, so PlaceLocations runs (it places the temple and nothing
        // else exists to place). Its postfix logs where the temple went, once.
        internal static bool CavesOn => On && FiresWorldProfile.Current?.Caves == true && FiresVoxelConfig.GeneratesCaves;
        private static int s_cavePlaced, s_caveRefused;
        internal static int CavePlacedThisSession => s_cavePlaced;
        internal static int SurfaceRefusedThisSession => s_caveRefused;   // 0.2.1096 [Locations]: refused at spawn (no cave room under a cave-planned site)

        // 0.2.1075: SpawnLocation asks this first (FatVanillaHooks): on super flat a location stands only in a cave or on a sky island,
        // or is the start temple / an allowed extra. A flat-ground spot is refused (no object at all).
        // 0.2.1076: one Info line per cave region as its locations land (super flat, caves planned by the region's own biome).
        private static readonly Dictionary<Vector2s, List<string>> s_caveRegions = new Dictionary<Vector2s, List<string>>();
        private static void NoteCaveRegion(string prefab, Vector3 pos)
        {
            var zone = ZoneSystem.GetZone(pos);
            if (!s_caveRegions.TryGetValue(zone, out var names)) s_caveRegions[zone] = names = new List<string>();
            names.Add(prefab);
            var biome = FiresCaveBiome.RegionBiome(pos.x, pos.z, WorldBiome(pos.x, pos.z));
            FiresLogger.LogInfo($"[FiresCaves] cave region ({pos.x:0},{pos.z:0}): biome {biome}, {names.Count} location(s) placed ({string.Join(", ", names)})");
        }

        internal static bool RefusesSurfaceLocation(ZoneSystem.ZoneLocation location, Vector3 pos)
        {
            if (!On || location == null || location.m_prefabName == StartTempleName || IsAllowedExtra(location.m_prefabName)) return false;
            if (FatUndergroundLocations.IsUnderground(pos))
            {
                NoteCaveRegion(location.m_prefabName, pos);
                if (++s_cavePlaced <= 8) FiresLogger.LogDebug($"[FiresCaves] cave location {location.m_prefabName} at ({pos.x:0},{pos.y:0},{pos.z:0}) (super-flat bypass); {s_cavePlaced} this session");
                return false;
            }
            if (pos.y > FiresWorldProfile.SuperFlatHeight + 12f && FiresSkyIslands.Enabled && FiresSkyIslands.IslandAt(pos.x, pos.z)) return false;   // a skyland's own location
            if (++s_caveRefused <= 8) FiresLogger.LogDebug($"[FAT] super flat: location {location.m_prefabName} at ({pos.x:0},{pos.z:0}) refused: no cave room under it (the flat ground stays empty); {s_caveRefused} this session");
            return true;
        }

        internal const string StartTempleName = "StartTemple";
        // Nothing on it except the start temple and the Mausoleum (which auto-places): FiresMausoleum's one
        // near-spawn location (FiresMausoleumCrypt, quantity 1, centre-first) is planned like vanilla plans it.
        internal const string MausoleumLocationName = "FiresMausoleumCrypt";
        private static bool IsAllowedExtra(string prefab) => prefab == MausoleumLocationName;
        private static bool s_templeLogged;

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        private static class TempleOnly
        {
            private static void Postfix(ZoneSystem __instance)
            {
                if (!On || s_templeLogged || __instance == null) return;
                try
                {
                    var instances = Traverse.Create(__instance).Field("m_locationInstances").GetValue() as System.Collections.IDictionary;
                    if (instances == null) return;
                    foreach (var li in instances.Values)
                    {
                        var t = Traverse.Create(li);
                        var loc = t.Field("m_location").GetValue();
                        string name = loc != null ? Traverse.Create(loc).Field("m_prefabName").GetValue<string>() : null;
                        if (name != StartTempleName || !t.Field("m_placed").GetValue<bool>()) continue;
                        Vector3 at = t.Field("m_position").GetValue<Vector3>();
                        s_templeLogged = true;
                        FiresLogger.LogInfo($"[FAT] super flat: start temple placed at {at.x:0},{at.z:0} (y {at.y:0.0}); no other location on this world.");
                        return;
                    }
                }
                catch (System.Exception e) { FiresLogger.LogWarning("[FAT] super flat: could not read the start temple's spot: " + e.Message); s_templeLogged = true; }
            }
        }

        // ── Vegetation, pickables, rocks, grass: none ───────────────────────
        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation")]
        private static class NoVegetation
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Vector2s __0)   // positional: the zone
            {
                if (!On) return true;
                var c = ZoneSystem.GetZonePos(__0);
                if (ContentAt(c.x, c.z)) { LogBypass("vegetation", c.x, c.z); return true; }
                // 0.2.1070 (Fire: "we want them to have all the proper clutter and vegetation even on super flat"): a zone under
                // a sky island places its vegetation, but only items that land ON the island (IslandItemsOnly in InsideClearArea
                // below); the flat ground stays bare.
                if (FiresSkyIslands.Enabled && FiresSkyIslands.TryGetBand(c.x - 32f, c.z - 32f, c.x + 32f, c.z + 32f, out _, out _))
                {
                    s_islandOnlyZone = __0;
                    s_islandOnly = true;
                    if (!s_dressSaid) { s_dressSaid = true; FiresLogger.LogInfo("[IslandDress] island-only vegetation gate executing (ZoneSystem.PlaceVegetation + InsideClearArea)"); }
                    s_islandItems = 0; s_islandOffRim = 0;
                    return true;
                }
                LogOnce("vegetation");
                return false;
            }

            private static void Finalizer()
            {
                if (!s_islandOnly) return;
                s_islandOnly = false;
                if (true)
                {
                    FiresSkyIslands.TryNearest(ZoneSystem.GetZonePos(s_islandOnlyZone), out float ix, out float iz, out float top, out _);
                    FiresLogger.LogInfo($"[IslandDress] super flat: island content in zone ({s_islandOnlyZone.x},{s_islandOnlyZone.y}) "
                        + $"(island at ({ix:0},{iz:0}) top y {top:0}): {s_islandItems} vegetation item(s) (trees, bushes, rocks, pickables), {s_islandOffRim} refused past the rim (0.2.1086), {s_islandGrass} island grass patch(es) so far; ground items refused");
                }
            }
        }

        private static bool s_islandOnly;
        private static bool s_dressSaid;
        private static Vector2s s_islandOnlyZone;
        private static int s_islandItems, s_islandOffRim;   // 0.2.1086: items refused past the built rim
        private static int s_islandGrass;   // island clutter patches let through this session

        // Is p on a sky island's surface (well above the flat ground)? The island-only gate for vegetation, clutter, spawns.
        internal static bool OnIsland(Vector3 p)
            => p.y > FiresWorldProfile.SuperFlatHeight + 12f && IslandCell(p.x, p.z, out _);   // 0.2.1078: height first, then the cell cache

        // 0.2.1075 (Fire: "just the overworld is meant to be super flat"): a point on a sky island or down in cave space is not the
        // overworld surface, so the super-flat biome override stands aside there and the world's own answer (a cave or island biome) holds.
        private const float CaveBelow = 4f;
        private static int s_offSurfaceLogged;
        internal static bool OffSurface(Vector3 p)
        {
            bool island = OnIsland(p);
            bool cave = !island && p.y < FiresWorldProfile.SuperFlatHeight - CaveBelow && p.y > FiresWorldProfile.SuperFlatHeight - 400f;
            if ((island || cave) && s_offSurfaceLogged < 6)
            {
                s_offSurfaceLogged++;
                FiresLogger.LogDebug(island
                    ? $"[FiresSkyIslands] super flat: island point ({p.x:0},{p.y:0},{p.z:0}) keeps its own biome (super-flat bypass)"
                    : $"[FiresCaves] cave region ({p.x:0},{p.z:0}) y {p.y:0}: keeps the world's biome (super-flat bypass)");
            }
            return island || cave;
        }

        // 0.2.1075: the 2D biome / sector answers have no height. Under a sky island (x,z) answers the island's own biome (a skyland's
        // Biome, else the world's own answer for a lattice island) so its vegetation, clutter and spawns read their island; and inside
        // WorldBiome (cave content: FiresCaveBiome, the cave clutter and props) the world's own answer stands.
        [System.ThreadStatic] private static int t_worldBiome;
        internal static bool InWorldBiome => t_worldBiome > 0;   // 0.2.1145: FatBiomeResolver stands aside inside WorldBiome
        internal static bool IslandColumn(float x, float z) => IslandCell(x, z, out _);   // 0.2.1145: the 2D answer under an island stays the island's
        private static readonly Dictionary<Heightmap.Biome, BiomeSector> s_ownSectors = new Dictionary<Heightmap.Biome, BiomeSector>();

        internal static Heightmap.Biome WorldBiome(float x, float z)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return Heightmap.Biome.Meadows;
            t_worldBiome++;
            try
            {
                // 0.2.1079 (Fire: all 7 skylands on SuperFlatSkyCaves2 came out Meadows): on super flat the patched GetBiome is
                // the super-flat field's own answer (the Fires generator's flat branch), Meadows everywhere, even with this
                // method's stand-aside. The world's REAL biome there is vanilla's own math for this seed, unpatched.
                Heightmap.Biome b;
                if (On && VanillaBiome.Ok) b = VanillaBiome.At(wg, x, z);
                else b = wg.GetBiome(x, z);
                if (On && b == Heightmap.Biome.Ocean) b = Heightmap.Biome.Meadows;   // a cave under the flat land is never "ocean"
                if (On && FiresLogger.DebugEnabled && s_worldBiomeLogs < 8)
                {
                    s_worldBiomeLogs++;
                    FiresLogger.LogDebug($"[FatSuperFlat] world biome at ({x:0},{z:0}) = {b} ({(VanillaBiome.Ok ? "vanilla math, unpatched" : "patched GetBiome")})");
                }
                return b;
            }
            finally { t_worldBiome--; }
        }

        private static int s_worldBiomeLogs;

        // Vanilla WorldGenerator.GetBiome(x, z, oceanLevel, waterAlwaysOcean) as shipped, without any mod's prefix/postfix.
        [HarmonyPatch]
        internal static class VanillaBiome
        {
            internal static bool Ok = true;

            [HarmonyReversePatch(HarmonyReversePatchType.Original)]
            [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(float), typeof(float), typeof(float), typeof(bool) })]
            private static Heightmap.Biome Original(WorldGenerator instance, float wx, float wy, float oceanLevel, bool waterAlwaysOcean)
                => throw new System.NotImplementedException("reverse patch");

            internal static Heightmap.Biome At(WorldGenerator wg, float x, float z)
            {
                try { return Original(wg, x, z, 0.02f, false); }
                catch (System.Exception e)
                {
                    Ok = false;
                    FiresLogger.LogWarning($"[FatSuperFlat] vanilla biome math unavailable ({e.GetType().Name}: {e.Message}); using the patched GetBiome");
                    return wg.GetBiome(x, z);
                }
            }
        }

        // 0.2.1078 (Fire's skyland stalls): every 2D GetBiome / GetBiomeSector on super flat asked the island index and, under an
        // island, the skyland biome. Clutter and the snow lookups call these hundreds of thousands of times as a player arrives, so
        // the answer is cached per 4 m cell (bounded; dropped when the island set changes, FiresSkyIslands.Generation).
        // Value: 0 = no island, else 1 + (int)skyland biome (1 = an island with no skyland biome of its own).
        private const float IslandCellSize = 4f;
        private const int IslandCellCap = 200000;
        private static readonly Dictionary<long, int> s_islandCells = new Dictionary<long, int>(4096);
        private static int s_islandCellsFor = -1;
        private static int s_islandCellHits, s_islandCellMisses;
        private static long s_islandCellNextLog;   // UTC ticks (thread-safe; Unity's Time is main-thread only)

        private static bool IslandCell(float x, float z, out Heightmap.Biome own)
        {
            own = Heightmap.Biome.None;
            if (!FiresSkyIslands.Enabled) return false;
            long key = ((long)Mathf.FloorToInt(x / IslandCellSize) << 32) ^ (uint)Mathf.FloorToInt(z / IslandCellSize);
            int gen = FiresSkyIslands.Generation, v;
            lock (s_islandCells)
            {
                if (s_islandCellsFor != gen || s_islandCells.Count >= IslandCellCap) { s_islandCells.Clear(); s_islandCellsFor = gen; }
                if (s_islandCells.TryGetValue(key, out v)) { s_islandCellHits++; }
                else v = -1;
            }
            if (v < 0)
            {
                float cx = (Mathf.Floor(x / IslandCellSize) + 0.5f) * IslandCellSize, cz = (Mathf.Floor(z / IslandCellSize) + 0.5f) * IslandCellSize;
                v = 0;
                if (FiresSkyIslands.IslandAt(cx, cz))
                    v = FiresSkyIslands.TrySkylandBiomeOver(cx, cz, out var b) && b != Heightmap.Biome.None ? 1 + (int)b : 1;
                lock (s_islandCells) { s_islandCells[key] = v; s_islandCellMisses++; }
            }
            long now = System.DateTime.UtcNow.Ticks;
            if (FiresLogger.DebugEnabled && FatDiag.BiomePaint && now >= System.Threading.Interlocked.Read(ref s_islandCellNextLog))
            {
                System.Threading.Interlocked.Exchange(ref s_islandCellNextLog, now + System.TimeSpan.TicksPerSecond * 30);
                FiresLogger.LogDebug($"[FatSuperFlat] island biome cell cache: {s_islandCells.Count} cell(s), {s_islandCellHits} hit(s) / {s_islandCellMisses} miss(es) last 30 s");
                s_islandCellHits = s_islandCellMisses = 0;
            }
            if (v == 0) return false;
            if (v > 1) own = (Heightmap.Biome)(v - 1);
            return true;
        }

        // 0.2.1086 (Fire shot 518: an Ashlands skyland's lava drawn on the flat ground below it): HeightmapBuilder.Build asks
        // GetBiomeSector/GetBiome(x, z) for the GROUND heightmap's corners, per-vertex biome and vegetation mask (the lava mask),
        // and the island answer above is by x,z alone (4 m IslandCell), so ground under a skyland built as Ashlands: lava colour,
        // lava mask, Heightmap.IsLava/GetLava (they read the corners). Inside a heightmap build (BiomePaint corner scope) the
        // island steps aside: ground keeps its own biome. The skyland answers for itself by height (TrySkylandBiomeAt in
        // Heightmap.GetBiome) and its zone still runs its biome's vegetation lists (IslandHaveBiome below).
        private static bool KeepOwnBiome(float x, float z, ref Heightmap.Biome biome)
        {
            if (t_worldBiome > 0) return true;
            if (Zones.BiomePaint.InCornerScope) return false;
            if (!IslandCell(x, z, out var own)) return false;
            if (own != Heightmap.Biome.None) { biome = own; return true; }
            if (biome == Heightmap.Biome.Ocean || biome == Heightmap.Biome.None) biome = Heightmap.Biome.Meadows;
            return true;
        }

        private static bool KeepOwnSector(float x, float z, ref BiomeSector sector)
        {
            if (t_worldBiome > 0) return true;
            if (Zones.BiomePaint.InCornerScope) return false;   // 0.2.1086: ground heightmap builds keep the ground's biome
            if (!IslandCell(x, z, out var own)) return false;
            Heightmap.Biome b = sector != null ? sector.Biome : Heightmap.Biome.Meadows;
            if (own != Heightmap.Biome.None) b = own;
            if (b == Heightmap.Biome.Ocean || b == Heightmap.Biome.None) b = Heightmap.Biome.Meadows;
            if (sector != null && sector.Biome == b) return true;
            lock (s_ownSectors)
            {
                if (!s_ownSectors.TryGetValue(b, out var s)) s_ownSectors[b] = s = new BiomeSector(null, b);
                sector = s;
            }
            return true;
        }

        // 0.2.1086: the ground heightmap's corners no longer carry a skyland's biome (KeepOwnBiome steps aside in a build), so a
        // zone under a skyland says it has that biome here, and vanilla still runs the skyland biome's vegetation lists there.
        // Each item's own biome check (Heightmap.GetBiome at the item's height) keeps ground items on the ground's biome.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.HaveBiome))]
        private static class IslandHaveBiome
        {
            private static void Postfix(Heightmap __instance, Heightmap.Biome biome, ref bool __result)
            {
                if (__result || !On || __instance == null || !FiresSkyIslands.Enabled) return;
                float half = __instance.m_width * __instance.m_scale * 0.5f;
                if ((FiresSkyIslands.SkylandBiomesOver(__instance.transform.position, half) & biome) != 0) __result = true;
            }
        }

        // Vanilla PlaceVegetation asks InsideClearArea once per item, right before it places it: in an island-only zone an item
        // not on the island counts as "inside a clear area" and is skipped.
        [HarmonyPatch(typeof(ZoneSystem), "InsideClearArea")]
        private static class IslandItemsOnly
        {
            private static void Postfix(Vector3 point, ref bool __result)
            {
                if (!s_islandOnly || __result) return;
                // 0.2.1086 (Fire: trees/stumps/logs floating and falling round a skyland; his log: Birch1/Beech1/Oak1 "still held
                // after 30 s" over island air): OnIsland is the 4 m cell over the island's shaped disc, which reaches past the rim
                // the mesher builds. An item must also stand on the island's own solid column at its height (TryIslandAt: the
                // skyland column + solid span, 1.5 m slack), else it hangs off the rim and falls.
                if (OnIsland(point) && FiresSkyIslands.TryIslandAt(point, 1.5f, out _, out _, out _)) s_islandItems++;
                else { __result = true; if (OnIsland(point)) s_islandOffRim++; }
            }
        }

        // The auto clutter gate itself sits FIRST in FatVanillaHooks' one GenerateVegPatch prefix (a separate prefix here was
        // overridden by ClutterSpread's own patch). Patches built before the profile said super flat are cleared once, after the frame.
        private static bool s_clearPending = true;
        internal static void NoteAutoClutterBlocked() => LogOnce("auto clutter (vanilla grass; FAP-painted clutter is unaffected)");

        [HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
        private static class ClearAutoClutterOnce
        {
            private static void Postfix(ClutterSystem __instance)
            {
                if (!On) { s_clearPending = true; return; }
                if (!s_clearPending) return;
                s_clearPending = false;
                __instance.ClearAll();
                Debug.Log("[FiresAdminTerrain] [FAT] super flat: auto clutter patches cleared once (built before the profile was known)");
            }
        }

        // ── Spawning: none of any kind ──────────────────────────────────────
        [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        private static class NoSpawnSystem
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(SpawnSystem __instance)
            {
                s_fishOnly = false;
                if (!On || Allowed("the spawn system", __instance)) return true;
                // 0.2.1083 (Fire: "still no ... fish"): the flat ground spawns nothing, but a zone holding OUR placed water
                // (Vriver / Vpond / Vpool) runs the spawn system for FISH only, through the same inland-fish path generated
                // water uses (InlandFishSpawns + FatWaterChecks.TryWaterAt). Every non-fish spawn is refused below.
                if (!ZoneHasPlacedWater(__instance.transform.position)) return false;
                s_fishOnly = true;
                return true;
            }

            private static void Postfix() => s_fishOnly = false;
        }

        private static bool s_fishOnly;
        private static readonly Dictionary<long, (bool wet, float until)> s_zoneWater = new Dictionary<long, (bool, float)>();
        private static int s_fishZoneHits;
        private static float s_fishLogAt;

        // 9 probes over the 64 m zone around the spawn system, cached 20 s per zone.
        private static bool ZoneHasPlacedWater(Vector3 c)
        {
            var z = ZoneSystem.GetZone(c);
            long key = ((long)z.x << 32) ^ (uint)z.y;
            if (s_zoneWater.TryGetValue(key, out var v) && Time.time < v.until) return v.wet;
            var zc = ZoneSystem.GetZonePos(z);
            bool wet = false;
            for (int i = -1; i <= 1 && !wet; i++)
                for (int j = -1; j <= 1 && !wet; j++)
                    wet = VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(zc.x + i * 24f, zc.z + j * 24f, 0.5f, false, out _, out _);
            s_zoneWater[key] = (wet, Time.time + 20f);
            if (wet) { s_fishZoneHits++; if (FiresLogger.DebugEnabled && Time.time >= s_fishLogAt) { s_fishLogAt = Time.time + 30f; FiresLogger.LogDebug($"[WaterLife] placed water dressed like generated: zone ({z.x},{z.y}) runs the spawn system for fish only (InlandFishSpawns / FatWaterChecks.TryWaterAt); {s_fishZoneHits} wet zone check(s) this session"); } }
            return wet;
        }

        [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
        private static class FishOnlyOnFlat
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(SpawnSystem.SpawnData spawn, ref bool __result)
            {
                if (!s_fishOnly || spawn == null || VerdantsAscent.Water.Core.InlandFishSpawns.IsFish(spawn)) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(CreatureSpawner), "UpdateSpawner")]
        private static class NoCreatureSpawners
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(CreatureSpawner __instance) => !On || Allowed("creature spawners", __instance);
        }

        [HarmonyPatch(typeof(SpawnArea), "UpdateSpawn")]
        private static class NoSpawnAreas
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(SpawnArea __instance) => !On || Allowed("spawn areas", __instance);
        }

        // Spawning runs inside a content area (random events stay off everywhere).
        private static bool Allowed(string what, Component c)
        {
            if (c == null) return false;
            var p = c.transform.position;
            if (OnIsland(p)) { LogBypass(what + " (sky island)", p.x, p.z); return true; }   // 0.2.1070
            if (!ContentAt(p.x, p.z)) return false;
            LogBypass(what, p.x, p.z);
            return true;
        }

        // Auto clutter grows in a content area (FatVanillaHooks' GenerateVegPatch gate asks this).
        // 0.2.1078 (Fire's skyland stalls): the answer per patch is memoised (an arrival regrows every patch at once, and the water
        // test below probes 5 points + the ecology's shore rings); the memo drops after MemoSeconds or when a water tool touches.
        private const float ClutterMemoSeconds = 10f;
        private static readonly Dictionary<long, bool> s_clutterMemo = new Dictionary<long, bool>(1024);
        private static float s_clutterMemoUntil;
        private static int s_clutterMemoHits, s_clutterMemoMisses;

        internal static bool ClutterAllowed(Vector2Int patchID, float size)
        {
            float now = Time.time;
            if (now >= s_clutterMemoUntil)
            {
                if (FatDiag.ClutterGate && s_clutterMemoHits + s_clutterMemoMisses > 0)
                    FiresLogger.LogDebug($"[FatSuperFlat] clutter gate memo: {s_clutterMemoHits} hit(s) / {s_clutterMemoMisses} computed patch(es) last {ClutterMemoSeconds:0} s");
                s_clutterMemo.Clear(); s_clutterMemoUntil = now + ClutterMemoSeconds; s_clutterMemoHits = s_clutterMemoMisses = 0;
            }
            // 0.2.1080: the key carries the patch size too (vanilla asks with m_grassPatchSize only today, so this is a guard,
            // not the proven cause of Fire's Deep North flicker; NoteClutterDecision + ClutterResetWatch log the evidence).
            long key = ((((long)patchID.x << 32) ^ (uint)patchID.y) * 31) ^ (long)Mathf.RoundToInt(size * 16f);
            if (s_clutterMemo.TryGetValue(key, out bool memo)) { s_clutterMemoHits++; return memo; }
            s_clutterMemoMisses++;
            bool allowed = ClutterAllowedNow(patchID, size);
            s_clutterMemo[key] = allowed;
            NoteClutterDecision(key, patchID, size, allowed);
            return allowed;
        }

        // 0.2.1080: a patch whose gate answer changes more than 3 times in 5 s logs its position, distance and inputs each time.
        private static readonly Dictionary<long, (bool last, int flips, float since)> s_clutterFlips = new Dictionary<long, (bool, int, float)>();
        private static void NoteClutterDecision(long key, Vector2Int patchID, float size, bool allowed)
        {
            float now = Time.time;
            if (!s_clutterFlips.TryGetValue(key, out var st) || now - st.since > 5f) { s_clutterFlips[key] = (allowed, 0, now); return; }
            if (st.last == allowed) return;
            st = (allowed, st.flips + 1, st.since);
            s_clutterFlips[key] = st;
            if (st.flips <= 3 || !FiresLogger.DebugEnabled) return;
            float x = patchID.x * size, z = patchID.y * size;
            var cam = Camera.main;
            float dist = cam != null ? Vector2.Distance(new Vector2(cam.transform.position.x, cam.transform.position.z), new Vector2(x, z)) : -1f;
            bool painted = Zones.BiomePaint.TryPaintedGround(x, z, out Heightmap.Biome pb);
            Heightmap.Biome world = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(x, z) : Heightmap.Biome.None;
            FiresLogger.LogDebug($"[FatSuperFlat] clutter gate flip #{st.flips} in 5 s: patch ({x:F0},{z:F0}) size {size:F0} dist {dist:F0} m -> {(allowed ? "allow" : "refuse")}; water {WaterClutterAt(x, z, size)}, island {IslandCell(x, z, out _)}, content {ContentAt(x, z)}, painted {(painted ? pb.ToString() : "none")}, GetBiome {world}");
            if (s_clutterFlips.Count > 4096) s_clutterFlips.Clear();
        }

        private static bool ClutterAllowedNow(Vector2Int patchID, float size)
        {
            float x = patchID.x * size, z = patchID.y * size;   // vanilla's patch centre
            if (WaterClutterAt(x, z, size)) { LogBypass("water-tool clutter", x, z); return true; }
            // 0.2.1070: a patch over a sky island grows its grass (the clutter ground probe stands it on the island's surface).
            if (IslandCell(x, z, out _))
            {
                s_islandGrass++;
                LogBypass("sky island clutter", x, z);
                return true;
            }
            if (!ContentAt(x, z)) return false;
            LogBypass("auto clutter", x, z);
            return true;
        }

        // 0.2.1052 (Fire 10-26: "our water tools are not properly laying in the paint and clutter"): the auto clutter block kept
        // every reed and shore grass off the banks the water tools carve. A patch at placed water (FAT's water sheets, which
        // persist and sync) or at a spot a water tool touched this session grows vanilla clutter; the rest of the field stays bare.
        private static readonly List<Vector3> s_waterClutter = new List<Vector3>();   // x, z, reach
        private const int WaterClutterMax = 512;

        // 0.2.1081: a placed water that just became real (VoxelZoneStreamer.TickPendingWet) drops every memoised answer.
        internal static void ForgetClutterMemo() => s_clutterMemo.Clear();

        internal static void AllowWaterClutter(Vector3 centre, float radius)
        {
            if (!On) return;
            if (s_waterClutter.Count >= WaterClutterMax) s_waterClutter.RemoveAt(0);
            s_waterClutter.Add(new Vector3(centre.x, centre.z, radius));
            s_clutterMemo.Clear();   // 0.2.1078: a water tool changed what grows here
        }

        private static bool WaterClutterAt(float x, float z, float size)
        {
            float half = size * 0.5f;
            for (int i = 0; i < s_waterClutter.Count; i++)
            {
                Vector3 w = s_waterClutter[i];
                float dx = w.x - x, dz = w.y - z, reach = w.z + half * 1.42f;
                if (dx * dx + dz * dz <= reach * reach) return true;
            }
            for (int i = 0; i < 5; i++)
            {
                float px = x + (i == 1 ? half : i == 2 ? -half : 0f), pz = z + (i == 3 ? half : i == 4 ? -half : 0f);
                if (global::VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(px, pz, 0.05f, false, out _, out _)) return true;
            }
            // 0.2.1067 (Fire: "make sure it gets all our custom water clutter"): a dry SHORE patch (the bank, where the reeds and
            // shore grass stand) was blocked after a reload, because only water under the patch counted. The water ecology's own
            // shore rings (6 and 12 m round the centre) decide it now, the same test that moves the patch's clutter to that water.
            return ClutterSystem_GenerateVegPatch_WaterEcology.TryPatchWater(new Vector3(x, 0f, z), out _);
        }

        [HarmonyPatch(typeof(RandEventSystem), "FixedUpdate")]
        private static class NoRandomEvents
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix() => !On;
        }

        // ── Zone water surfaces on the land: hidden ─────────────────────────
        // Ocean and water showed right near spawn: 8 zone 'WaterSurface' renderers (Custom/FiresWater) were
        // drawn with their top at y 50 (the ground height) where the probe found NO liquid.
        // Every zone carries vanilla's sea WaterVolume at y 30; on a super flat world a zone wholly
        // inside the land radius has no sea, so its surface is not drawn. Zones that reach the shore or the ocean keep theirs.
        // Built-piece water (a hot tub: a WaterVolume under a ZNetView) is never touched.
        private const float ZoneHalfDiagonal = 46f;   // 64 m zone: half its diagonal, rounded up
        private static int s_hiddenSurfaces;

        [HarmonyPatch(typeof(WaterVolume), "OnEnable")]
        private static class LandHasNoSea
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(WaterVolume __instance)
            {
                if (!On || __instance == null || __instance.m_waterSurface == null) return;
                if (__instance.GetComponentInParent<ZNetView>() != null) return;
                if (FatHaloArc.RingOnly)
                {
                    // 0.2.1036 HaloFlat (ring only): no ocean anywhere - no surface and no swim volume at y 30 under the ring.
                    __instance.m_waterSurface.forceRenderingOff = true;
                    foreach (var col in __instance.GetComponentsInChildren<Collider>(true)) col.enabled = false;
                    if (++s_hiddenSurfaces == 1) FiresLogger.LogInfo("[FAT] HaloFlat: zone sea removed (surface off, water volume collider off) - the ring world has no ocean");
                    return;
                }
                Vector3 c = __instance.transform.position;
                if (Mathf.Sqrt(c.x * c.x + c.z * c.z) + ZoneHalfDiagonal >= Radius) return;
                // Hidden here ("zone sea surface hidden" logged) yet 8 were drawn again
                // later: something re-sets renderer.enabled). forceRenderingOff is a separate switch that code toggling .enabled never
                // touches, so the surface stays undrawn for the zone's life.
                if (__instance.m_waterSurface.forceRenderingOff) return;
                __instance.m_waterSurface.forceRenderingOff = true;
                if (++s_hiddenSurfaces == 1 || s_hiddenSurfaces % 50 == 0)
                    FiresLogger.LogDebug($"[FAT] super flat: zone sea surface hidden on the land ({s_hiddenSurfaces} so far; first at {c.x:0},{c.z:0}) - no sea inside the {Radius:0} m land radius.");
            }
        }

        // ── Every water renderer of ours over the land: off ─────────────────
        // Super flat draws ONLY flat terrain, no generated water of ours: one client sweep every 2 s, on a
        // super flat world only. Any renderer drawing a Fires water shader (Custom/FiresWater*, FAT inland water / combiners,
        // TossinShade's surfaces), a vanilla zone 'WaterSurface', or TossinShade's player-centred ocean (FiresClipmapOcean*,
        // FiresWaterRing*) is switched off with forceRenderingOff while it sits over the land - the player-centred oceans while the
        // player is inside the land. Past the edge they draw again: the vanilla-style ocean beyond 2500 m stays.
        private const float SweepSeconds = 2f, OceanInsideMargin = 300f;
        private const bool ForceHideWaterOverLand = false;
        private static float s_nextSweep;
        private static readonly System.Collections.Generic.HashSet<Renderer> s_off = new System.Collections.Generic.HashSet<Renderer>();
        private static int s_sweepLogged = -1;

        // 0.2.1020 (Fire 10-24: "our v pool tool places a swim trigger but never a water mesh", "v pour / v pond does nothing"):
        // the SEA only. The old test hid every renderer whose shader name holds "Water" over the land, which also hid the water a
        // player PLACES (Vpool / Vpour / Vpond / Vriver: inland-water combiners and the chunk water sheets) - the log said "37
        // water renderer(s) of ours over the land kept undrawn" while the pools were laid. A super flat world generates no water of
        // its own, so everything else that draws water there was placed and stays drawn.
        private static bool IsOurWater(Renderer r)
        {
            string n = r.name;
            if (n == "WaterSurface" || n.StartsWith("FiresClipmapOcean", System.StringComparison.Ordinal) || n.StartsWith("FiresWaterRing", System.StringComparison.Ordinal)) return true;
            if (r.bounds.extents.x > 500f || r.bounds.extents.z > 500f) return true;   // a distant / LOD sea plane around the player
            var m = r.sharedMaterial;
            return m != null && m.shader != null && m.shader.name == "Custom/Water";   // vanilla's own sea shader
        }

        // A sea plane still showed further out: any mesh far bigger than a zone (a distant / LOD sea plane
        // drawn around the player) counts as player-centred too.
        private static bool PlayerCentred(Renderer r) => r.name.StartsWith("FiresClipmapOcean", System.StringComparison.Ordinal) || r.name.StartsWith("FiresWaterRing", System.StringComparison.Ordinal)
            || r.bounds.extents.x > 500f || r.bounds.extents.z > 500f;

        [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
        private static class NoWaterOverLand
        {
            private static void Postfix()
            {
                // 0.2.1021 (Fire 10-24: "we can stop force hiding the water, ocean would never be over flat land at y50"): the sweep is
                // off - it hid the water players place (Vpool / Vpour / Vpond / Vriver). Anything it hid before is drawn again here.
                if (!On || !(ForceHideWaterOverLand || FatHaloArc.RingOnly)) { if (s_off.Count > 0) { foreach (var r in s_off) if (r != null) r.forceRenderingOff = false; s_off.Clear(); } return; }
                if (Time.unscaledTime < s_nextSweep) return;
                s_nextSweep = Time.unscaledTime + SweepSeconds;
                var cam = GameCamera.instance != null ? GameCamera.instance.transform.position : Vector3.zero;
                bool playerInside = Mathf.Sqrt(cam.x * cam.x + cam.z * cam.z) < Radius - OceanInsideMargin;
                int hidden = 0;
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (r == null || !IsOurWater(r) || r.GetComponentInParent<ZNetView>() != null) continue;   // built-piece water (hot tubs) stays
                    Vector3 c = r.bounds.center;
                    bool overLand = PlayerCentred(r) ? playerInside : Mathf.Sqrt(c.x * c.x + c.z * c.z) < Radius - ShoreWidth;
                    if (overLand) { if (!r.forceRenderingOff) { r.forceRenderingOff = true; s_off.Add(r); } hidden++; }
                    else if (s_off.Remove(r)) r.forceRenderingOff = false;
                }
                s_off.RemoveWhere(r => r == null);
                if (hidden != s_sweepLogged)
                {
                    s_sweepLogged = hidden;
                    FiresLogger.LogInfo($"[FAT] super flat: {hidden} water renderer(s) of ours over the land kept undrawn (player {(playerInside ? "inside" : "at the edge")}).");
                }
            }
        }

        // A new world resets the one-time log line (a Dimensions hop can go from a super flat world to a normal one and back).
        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ResetLog
        {
            private static void Postfix() { s_logged = false; s_templeLogged = false; s_hiddenSurfaces = 0; s_sweepLogged = -1; s_off.Clear(); s_waterClutter.Clear(); }
        }
    }
}
