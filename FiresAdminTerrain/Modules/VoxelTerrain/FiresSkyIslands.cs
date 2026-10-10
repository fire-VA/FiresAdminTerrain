using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── SKY ISLANDS (P10 phase 1) ────────────────────────────────────────
    // Rare floating islands, deterministic per seed: flat-ish carved top,
    // pointed ridged underside (the user's scaled-cube prototype is the
    // shape target). Placed ≥200m above the LOCAL terrain — a mountain at
    // y400 gets its island around y600+. Pure voxel additions: the 2D
    // heightmap, the minimap, spawns and vegetation never see them; the
    // island SDF unions into chunk density at generate time and the zone
    // streamer extends each column's chunk band to cover the island.
    //
    // Phase 1 constraints (per plan): islands are DRY (one water level per
    // column — island ponds need multi-level water, a real format change);
    // no far-quilt/impostor representation, so a distant island appears when
    // its zone wraps (~wrap radius). Both are documented follow-ups.
    internal static partial class FiresSkyIslands
    {
        private struct Island
        {
            public float X, Z, R, BaseY, Seed;
            public float Top;      // conservative band top for streaming
            public float Bot;      // conservative band bottom
            public float MaxDepth; // belly clamp — low floaters keep ≥12m air under them
            public byte Kind;      // 0 rolling plains, 1 mini-mountain, 2 crater, 3 grotto
            // WATER FEATURES (analytic, deterministic): a pond bowl dug into
            // the top surface (grotto: the cavity floor), and an optional
            // outlet rill descending from the pond toward the rim. PondLvl 0
            // = no water on this island.
            public float PondX, PondZ, PondR, PondLvl;
            public float RillAng;     // NaN = no rill
            public float RillLen;     // metres from pond centre to the rill's end
            public float RillEndLvl;  // water level at the rill's end
            public int Variety;
            public Heightmap.Biome Biome;
            public int Site;
            public float SiteX, SiteZ, SiteY, SiteFlat, SiteReach, SiteClear, SiteYaw;
            public SkylandShape Shape;
            public GroundWaters Waters;
            // A skyland laid down from a blueprint (ZoneModeSkylandBlueprint) keeps every field above in the frame of the
            // island it was saved from, so its noise, shape, waters and caves sample exactly what the original's do; this
            // whole-metre offset carries it to where it stands. Every query that takes or answers a world position
            // translates at its door. Zero for every island the world grew itself, which then reads exactly as before.
            public float OffX, OffY, OffZ;
            // The world a blueprint's skyland grew in (Fire, 2026-09-27: "make them work anywhere"): its noise seed, the
            // seed its atoll crescents are laid out from, and its biome. A skyland carrying them derives the same shape,
            // ground, caves and biome in any world. False for every island this world grew, which reads this world's.
            public bool OwnSeeds;
            public int NoiseSeed, OwnShapeSeed;
            public Heightmap.Biome FixedBiome;
        }

        private static bool IsMoved(in Island isl) => isl.OffX != 0f || isl.OffY != 0f || isl.OffZ != 0f;

        // The island's own noise: its world's, when it came from a blueprint saved in another world.
        private static float N(in Island isl, float x, float y, float z)
            => isl.OwnSeeds ? VoxelCaveNoise.Noise01(x, y, z, isl.NoiseSeed) : VoxelCaveNoise.Noise01(x, y, z);

        private static int ShapeSeedOf(in Island isl) => isl.OwnSeeds ? isl.OwnShapeSeed : ShapeSeed;

        private static int? NoiseSeedOf(in Island isl) => isl.OwnSeeds ? isl.NoiseSeed : (int?)null;
        private static float WorldX(in Island isl) => isl.X + isl.OffX;
        private static float WorldZ(in Island isl) => isl.Z + isl.OffZ;

        private static byte KindOf(float h)
            => h < 0.40f ? (byte)0 : h < 0.65f ? (byte)1 : h < 0.85f ? (byte)2 : (byte)3;

        private static string KindName(byte k)
            => k == SkylandKind ? "skyland" : k == ShapedKind ? "shaped skyland" : k == 1 ? "mountain" : k == 2 ? "crater" : k == 3 ? "grotto" : "rolling";

        private const float PLACE_CELL = 1536f;   // one roll per ~1.5km cell
        private const float INDEX_CELL = 512f;    // spatial index granularity

        private static readonly List<Island> s_islands = new List<Island>(64);
        private static readonly Dictionary<long, List<int>> s_index = new Dictionary<long, List<int>>(256);
        private static bool s_any;

        internal static int IslandCount => s_islands.Count;
        internal static int ClusterCount;

        internal static bool Enabled => s_any && FiresVoxelConfig.BuildsSkyIslands;

        internal static void Clear()
        {
            s_islands.Clear();
            s_index.Clear();
            s_any = false;
            ClusterCount = 0;
            s_dryReported = false;
            ClearFormations();
            ResetRegistryState();
        }

        // The ONE way an island enters the live set: appended and spatially
        // indexed together, so the list and the index can never disagree. Build
        // and the registry loader both go through here.
        private static void AddIsland(in Island isl)
        {
            int idx = s_islands.Count;
            var derived = isl;
            derived.Variety = unchecked((int)SourceHash(in isl));
            bool water = IslandWaterOn;
            if (!water) BornDry(ref derived);
            if (derived.Kind == ShapedKind) derived.Shape = ShapeFor(in derived);
            if (IsSkyland(derived.Kind))
            {
                derived.Waters = water ? DeriveWaters(in derived) : null;
                derived.Biome = derived.FixedBiome != Heightmap.Biome.None ? derived.FixedBiome : RollSkylandBiome(in derived);
                RollSkylandSite(ref derived);
            }
            s_islands.Add(derived);
            float wx = WorldX(in isl), wz = WorldZ(in isl);
            int i0 = Mathf.FloorToInt((wx - isl.R - 8f) / INDEX_CELL), i1 = Mathf.FloorToInt((wx + isl.R + 8f) / INDEX_CELL);
            int j0 = Mathf.FloorToInt((wz - isl.R - 8f) / INDEX_CELL), j1 = Mathf.FloorToInt((wz + isl.R + 8f) / INDEX_CELL);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    long key = ((long)i << 32) ^ (uint)j;
                    if (!s_index.TryGetValue(key, out var list)) s_index[key] = list = new List<int>(2);
                    list.Add(idx);
                }
        }

        // 0.2.710 (Fire, 10-05, a player's ask): New World's "Auto-place water on sky islands", kept in the world's profile.
        // Off: every island is born dry - no pond bowl or rill, no river or lake carved, so no island water and no falls -
        // and its dressing stands on the dry top, for shaping water on it by hand. Variety stays the wet source's, so the
        // island's look and biome roll do not change; its id and chunk hashes do, as its density does.
        internal static bool IslandWaterOn => FiresWorldProfile.Current?.SkyIslandWater != false;

        private static bool s_dryReported;

        private static void BornDry(ref Island isl)
        {
            isl.PondX = isl.PondZ = isl.PondR = isl.PondLvl = 0f;
            isl.RillAng = float.NaN;
            isl.RillLen = isl.RillEndLvl = 0f;
            if (s_dryReported) return;
            s_dryReported = true;
            FiresLogger.LogDebug("[FiresSkyIslands] this world places no water on sky islands (its profile: water on sky islands off): "
                + "islands are born dry - no ponds, rivers or falls carved, no island water placed.");
        }

        private static long CellKey(float wx, float wz)
        {
            int cx = Mathf.FloorToInt(wx / INDEX_CELL);
            int cz = Mathf.FloorToInt(wz / INDEX_CELL);
            return ((long)cx << 32) ^ (uint)cz;
        }

        // Studio: an import ground sampler (returns the surface height, null off-map).
        // Null => use the generator's grid. Lets the offline studio place islands on an
        // IMPORTED world where the generator isn't Active.
        internal static System.Func<float, float, float?> GroundOverride;
        private static bool GetGround(float wx, float wz, out float g)
        {
            if (GroundOverride != null) { var v = GroundOverride(wx, wz); if (v.HasValue) { g = v.Value; return true; } g = 0f; return false; }
            return FiresWorldGenBase.TryGetGenGroundReal(wx, wz, out g);
        }
        // Set when the world is full voxel but the Fires generator is NOT running - a vanilla-profiled conversion,
        // where the ground comes from the vanilla heightmap instead of a generated grid. Both registry paths call
        // the one-argument Build, so the override has to ride here or it would be wiped to null by the shim and
        // every island would fail GetGround and never be placed.
        internal static System.Func<float, float, float?> GroundWhenGeneratorIsOff;

        internal static void Build(int seed) => Build(seed, GroundWhenGeneratorIsOff);
        // Called once per world init, AFTER the packed ground grid exists
        // (island base height rides the local terrain).
        internal static void Build(int seed, System.Func<float, float, float?> groundAt)
        {
            GroundOverride = groundAt;
            Clear();
            if (FiresVoxelConfig.SkyIslands != null && !FiresVoxelConfig.SkyIslands.Value) return;
            float chance = FiresVoxelConfig.SkyIslandRarity != null
                ? FiresVoxelConfig.SkyIslandRarity.Value : 0.30f;
            if (chance <= 0f) return;
            // 0.2.721 (FatWorldSize): the placement lattice stays anchored at the vanilla -10000 and only grows OUTWARD on a
            // bigger world (Expand World Size), so every island a 10 km world has is in the same place on any size: cells
            // 0..13 as before, plus cells below 0 and past 13 out to the world's radius. The rim is Radius - 700 (9300).
            const float HALF = 10000f;   // the lattice's anchor (the vanilla playable radius)
            float radius = FatWorldSize.Radius;
            float rim = radius - 700f;
            int cells = Mathf.CeilToInt(HALF * 2f / PLACE_CELL);
            int extra = radius > HALF ? Mathf.CeilToInt((radius - HALF) / PLACE_CELL) : 0;
            int clusters = 0;
            for (int cz = -extra; cz < cells + extra; cz++)
                for (int cx = -extra; cx < cells + extra; cx++)
                {
                    if (Hash(cx, cz, seed, 0x51AB) >= chance) continue;
                    float wx = -HALF + (cx + 0.2f + 0.6f * Hash(cx, cz, seed, 0x11)) * PLACE_CELL;
                    float wz = -HALF + (cz + 0.2f + 0.6f * Hash(cx, cz, seed, 0x22)) * PLACE_CELL;
                    if (wx * wx + wz * wz > rim * rim) continue;   // off the rim
                    if (!GetGround(wx, wz, out float ground0)) continue;

                    // Two placement kinds (user spec): rare CLUSTERS — an
                    // ARCHIPELAGO of many smaller satellites stepping upward
                    // and culminating in one large MOTHER island at the top
                    // of the climb — and SINGLES roaming the full 30-300m
                    // band. Sizes and shapes vary per member.
                    bool clusterKind = Hash(cx, cz, seed, 0x55) < 0.25f;
                    int count = clusterKind ? 5 + (int)(Hash(cx, cz, seed, 0x66) * 4.999f) : 1;
                    if (clusterKind) clusters++;
                    float baseUp = clusterKind ? 30f : 30f + Hash(cx, cz, seed, 0x44) * 270f;
                    float ang = Hash(cx, cz, seed, 0x77) * Mathf.PI * 2f;
                    for (int ci = 0; ci < count; ci++)
                    {
                        int salt = 0x100 + ci * 37;
                        if (wx * wx + wz * wz > rim * rim) break;   // chain wandered off the rim
                        bool mother = clusterKind && ci == count - 1;
                        float h = Hash(cx, cz, seed, salt);
                        float r = clusterKind
                            ? (mother ? 110f + h * 80f      // the mother: 110-190m
                                      : 22f + h * h * 50f)  // satellites: 22-72m
                            : 40f + h * h * 160f;           // singles 40-200m
                        if (!GetGround(wx, wz, out float ground)) ground = ground0;
                        // TERRAIN CLEARANCE — the island must clear the ground
                        // across its WHOLE footprint, not just its centre. A
                        // low-band island whose rim reached over a rising
                        // mountain flank fused into the slope as a sheer
                        // flat-topped cliff shelf (user report at y555:
                        // "chunks ripped out"). Sample the rim; the island
                        // floats above the HIGHEST ground it overhangs.
                        float gMax = ground;
                        for (int rp = 0; rp < 4; rp++)
                        {
                            float rx = wx + (rp == 0 ? r : rp == 1 ? -r : 0f);
                            float rz = wz + (rp == 2 ? r : rp == 3 ? -r : 0f);
                            if (GetGround(rx, rz, out float rg) && rg > gMax) gMax = rg;
                        }
                        float floorY = Mathf.Max(gMax, 28f);
                        float baseY = floorY + baseUp;
                        byte kind = mother
                            ? (Hash(cx, cz, seed, salt + 11) < 0.5f ? (byte)0 : (byte)1)
                            : KindOf(Hash(cx, cz, seed, salt + 11));
                        var isl = new Island
                        {
                            X = wx, Z = wz, R = r, BaseY = baseY,
                            Seed = seed * 0.61803f + cx * 17.23f + cz * 29.7f + ci * 101.3f,
                            MaxDepth = Mathf.Max(4f, baseUp - 12f),
                            Top = baseY + (kind == 1 ? r * 0.48f : r * 0.04f) + 16f,
                            Kind = kind,
                            RillAng = float.NaN,
                        };
                        isl.Bot = baseY - Mathf.Min(r * 0.90f, isl.MaxDepth) - 6f;

                        // WATER FEATURES — deterministic per member. Grottos
                        // ALWAYS pool in their cavity, craters pool in the
                        // dish, rolling/mountain tops roll for an occasional
                        // pond — sometimes with an outlet rill stepping down
                        // toward the rim. Vetoed over wet BASE ground: the
                        // column store holds ONE level, and ground water owns
                        // its columns.
                        {
                            float pr = 0f, plvl = 0f, ppx = wx, ppz = wz;
                            if (kind == 3)
                            {
                                ppx = wx + Mathf.Cos(isl.Seed) * r * 0.35f;
                                ppz = wz + Mathf.Sin(isl.Seed) * r * 0.35f;
                                float gy = baseY - Mathf.Min(isl.MaxDepth, r * 0.25f) * 0.5f;
                                float hh2 = Mathf.Max(4f, r * 0.16f);
                                pr = r * 0.28f;
                                plvl = gy - hh2 * 0.55f + 1.2f;
                            }
                            else if (kind == 2)
                            {
                                pr = r * 0.30f;
                                plvl = TopAt(in isl, wx, wz) - 0.5f;
                            }
                            else
                            {
                                float pondChance = mother ? 0.65f : kind == 0 ? 0.45f : 0.30f;
                                if (Hash(cx, cz, seed, salt + 17) < pondChance)
                                {
                                    float pAng = Hash(cx, cz, seed, salt + 19) * Mathf.PI * 2f;
                                    float pOff = r * (kind == 1 ? 0.45f : 0.25f);
                                    ppx = wx + Mathf.Cos(pAng) * pOff;
                                    ppz = wz + Mathf.Sin(pAng) * pOff;
                                    pr = r * (kind == 1 ? 0.10f + 0.08f * Hash(cx, cz, seed, salt + 23)
                                                        : 0.16f + 0.12f * Hash(cx, cz, seed, salt + 23));
                                    plvl = TopAt(in isl, ppx, ppz) - 0.4f;
                                }
                            }
                            if (pr > 0f && FiresWorldGenBase.Active
                                && (FiresWorldGenBase.IsWetAtReal(ppx, ppz)
                                 || FiresWorldGenBase.IsWetAtReal(ppx + pr, ppz) || FiresWorldGenBase.IsWetAtReal(ppx - pr, ppz)
                                 || FiresWorldGenBase.IsWetAtReal(ppx, ppz + pr) || FiresWorldGenBase.IsWetAtReal(ppx, ppz - pr)))
                                pr = 0f;
                            if (pr > 0f)
                            {
                                isl.PondX = ppx; isl.PondZ = ppz; isl.PondR = pr; isl.PondLvl = plvl;
                                if (kind <= 1 && Hash(cx, cz, seed, salt + 29) < (kind == 0 ? 0.55f : 0.30f))
                                {
                                    for (int tryDir = 0; tryDir < 2 && float.IsNaN(isl.RillAng); tryDir++)
                                    {
                                        float A = Hash(cx, cz, seed, salt + 31) * Mathf.PI * 2f + tryDir * Mathf.PI;
                                        float dxr = Mathf.Cos(A), dzr = Mathf.Sin(A);
                                        float rx = ppx - wx, rz = ppz - wz;
                                        float bq = rx * dxr + rz * dzr;
                                        float cq = rx * rx + rz * rz - (r * 0.90f) * (r * 0.90f);
                                        float disc = bq * bq - cq;
                                        if (disc <= 0f) continue;
                                        float L = -bq + Mathf.Sqrt(disc);
                                        if (L < pr + 6f) continue;
                                        float ex = ppx + dxr * L, ez = ppz + dzr * L;
                                        float endLvl = TopAt(in isl, ex, ez) - 0.8f;
                                        if (endLvl > plvl - 0.6f) continue;   // must run DOWNhill
                                        isl.RillAng = A; isl.RillLen = L; isl.RillEndLvl = endLvl;
                                    }
                                }
                            }
                        }
                        AddIsland(in isl);
                        if (clusterKind)
                        {
                            // next link: wander sideways scaled to the radii so
                            // members group tight WITHOUT merging; the last hop
                            // (into the mother) adds extra clearance for her
                            // size. Satellites climb 22-40m; the mother sits
                            // another 45-65m above the highest satellite.
                            bool nextMother = ci == count - 2;
                            ang += (Hash(cx, cz, seed, salt + 3) - 0.5f) * 1.6f;
                            float step = r * 0.85f + 60f + Hash(cx, cz, seed, salt + 5) * 90f
                                       + (nextMother ? 130f : 0f);
                            wx += Mathf.Cos(ang) * step;
                            wz += Mathf.Sin(ang) * step;
                            baseUp += nextMother
                                ? 45f + Hash(cx, cz, seed, salt + 7) * 20f
                                : 22f + Hash(cx, cz, seed, salt + 7) * 18f;
                        }
                    }
                }
            s_any = s_islands.Count > 0;
            ClusterCount = clusters;
            FiresLogger.LogInfo($"[FiresSkyIslands] {s_islands.Count} island(s) placed, {clusters} ascending cluster(s) " +
                      $"(rarity {chance:0.00}, cell {PLACE_CELL:0}m, band 30-300m).");
        }

        private static float Hash(int x, int z, int seed, int salt)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) ^ (uint)(z * 668265263) ^ (uint)((seed + salt) * 2246822519);
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        // Does any island overlap this XZ rect? Returns the union Y band for
        // the zone streamer (chunk stack must cover it).
        internal static bool TryGetBand(float x0, float z0, float x1, float z1, out float botY, out float topY)
        {
            botY = float.MaxValue; topY = float.MinValue;
            if (!Enabled) return false;
            bool any = false;
            int i0 = Mathf.FloorToInt((x0 - 8f) / INDEX_CELL), i1 = Mathf.FloorToInt((x1 + 8f) / INDEX_CELL);
            int j0 = Mathf.FloorToInt((z0 - 8f) / INDEX_CELL), j1 = Mathf.FloorToInt((z1 + 8f) / INDEX_CELL);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    if (!s_index.TryGetValue(((long)i << 32) ^ (uint)j, out var list)) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        var isl = s_islands[list[k]];
                        float cx = WorldX(in isl), cz = WorldZ(in isl);
                        float nx = Mathf.Clamp(cx, x0, x1) - cx;
                        float nz = Mathf.Clamp(cz, z0, z1) - cz;
                        if (nx * nx + nz * nz > isl.R * isl.R) continue;
                        any = true;
                        float bot = isl.Bot + isl.OffY;
                        if (bot < botY) botY = bot;
                        float top = isl.Top + ReliefCeiling(in isl) + isl.OffY;
                        if (top > topY) topY = top;
                    }
                }
            return any;
        }

        // Content signature of every island overlapping this XZ rect, 0 when none.
        // The zone streamer folds it into a chunk's generation hash so that adding,
        // moving or deleting an island makes exactly the chunks inside its band
        // fail the change-gate and regenerate — HashColumns hashes ground, biome,
        // caves and rivers, and never sees islands at all.
        //
        // SUMMED, not chained, so it does not depend on list order: removing an
        // island from the middle of s_islands renumbers everything after it, and a
        // chained hash would then mark unrelated islands as changed.
        internal static uint ColumnSignature(float x0, float z0, float x1, float z1)
        {
            if (!Enabled) return 0u;
            uint sum = 0u;
            int i0 = Mathf.FloorToInt((x0 - 8f) / INDEX_CELL), i1 = Mathf.FloorToInt((x1 + 8f) / INDEX_CELL);
            int j0 = Mathf.FloorToInt((z0 - 8f) / INDEX_CELL), j1 = Mathf.FloorToInt((z1 + 8f) / INDEX_CELL);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    if (!s_index.TryGetValue(((long)i << 32) ^ (uint)j, out var list)) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        var isl = s_islands[list[k]];
                        // Same circle-vs-rect test TryGetBand uses, so the signature
                        // covers exactly the islands that shape this column.
                        float cx = WorldX(in isl), cz = WorldZ(in isl);
                        float nx = Mathf.Clamp(cx, x0, x1) - cx;
                        float nz = Mathf.Clamp(cz, z0, z1) - cz;
                        if (nx * nx + nz * nz > isl.R * isl.R) continue;
                        sum += IslandHash(in isl) + SiteSignature(in isl);
                    }
                }
            return sum;
        }

        // The island's identity: its source fields, and where a placed copy stands. The offset is mixed in only when there is
        // one, so every island the world grew keeps the id its dressing record and chunk hashes were written under.
        private static uint IslandHash(in Island isl)
        {
            uint h = SourceHash(in isl);
            if (!IsMoved(in isl) && !isl.OwnSeeds) return h;
            h = (h ^ (uint)isl.OffX.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.OffY.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.OffZ.GetHashCode()) * 16777619u;
            if (isl.OwnSeeds)
            {
                h = (h ^ (uint)isl.NoiseSeed) * 16777619u;
                h = (h ^ (uint)isl.OwnShapeSeed) * 16777619u;
                h = (h ^ (uint)isl.FixedBiome) * 16777619u;
            }
            return h == 0u ? 1u : h;
        }

        // Every field that shapes the island's density, by bit pattern. Never 0,
        // so a present island always moves the sum it is added to. A placed copy
        // shares it with the island it was saved from, so it derives the same
        // variety, shape, waters and biome.
        private static uint SourceHash(in Island isl)
        {
            uint h = 2166136261u;
            h = (h ^ (uint)isl.X.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.Z.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.R.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.BaseY.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.Seed.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.Top.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.Bot.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.MaxDepth.GetHashCode()) * 16777619u;
            h = (h ^ isl.Kind) * 16777619u;
            h = (h ^ (uint)isl.PondX.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.PondZ.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.PondR.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.PondLvl.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.RillAng.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.RillLen.GetHashCode()) * 16777619u;
            h = (h ^ (uint)isl.RillEndLvl.GetHashCode()) * 16777619u;
            return h == 0u ? 1u : h;
        }

        internal static bool ChunkOverlaps(Vector3 worldOrigin)
        {
            if (!Enabled) return false;
            return TryGetBand(worldOrigin.x, worldOrigin.z, worldOrigin.x + 32f, worldOrigin.z + 32f,
                       out float bot, out float top)
                   && worldOrigin.y + 33f >= bot && worldOrigin.y - 1f <= top;
        }

        // Is this column inside any island's footprint? Cheap per-item gate
        // for the vegetation split (ground keeps its veg; islands roll for
        // their own share).
        internal static bool IslandAt(float wx, float wz)
        {
            if (!Enabled) return false;
            if (!s_index.TryGetValue(CellKey(wx, wz), out var list)) return false;
            for (int k = 0; k < list.Count; k++)
            {
                var isl = s_islands[list[k]];
                if (Covers(in isl, wx - isl.OffX, wz - isl.OffZ)) return true;
            }
            return false;
        }

        // Every island (pos at top height, radius, archetype) for map pins.
        // Surface sampling for the offline studio's island MESH (not used in game).
        internal static int SurfaceCount => s_islands.Count;
        internal static void SurfaceInfo(int i, out float x, out float z, out float r, out float baseY, out byte kind)
        { var isl = s_islands[i]; x = WorldX(in isl); z = WorldZ(in isl); r = isl.R; baseY = isl.BaseY + isl.OffY; kind = isl.Kind; }
        internal static float SurfaceY(int i, float wx, float wz) { var isl = s_islands[i]; return TopAt(in isl, wx - isl.OffX, wz - isl.OffZ) + isl.OffY; }

        internal static void Snapshot(List<Vector3> outPos, List<float> outR, List<string> outKind)
        {
            outPos.Clear(); outR.Clear(); outKind.Clear();
            for (int i = 0; i < s_islands.Count; i++)
            {
                var isl = s_islands[i];
                outPos.Add(new Vector3(WorldX(in isl), isl.Top + isl.OffY, WorldZ(in isl)));
                outR.Add(isl.R);
                outKind.Add(KindName(isl.Kind));
            }
        }

        // The island nearest `near`: its world centre on its top surface, and its radius (admintools_test's ISLAND place).
        internal static bool TryNearestTop(Vector3 near, out Vector3 top, out float radius)
        {
            top = default;
            radius = 0f;
            int best = -1;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < s_islands.Count; i++)
            {
                var candidate = s_islands[i];
                float dx = WorldX(in candidate) - near.x, dz = WorldZ(in candidate) - near.z;
                if (dx * dx + dz * dz >= bestSqr) continue;
                bestSqr = dx * dx + dz * dz;
                best = i;
            }
            if (best < 0) return false;
            var isl = s_islands[best];
            top = new Vector3(WorldX(in isl), TopAt(in isl, isl.X, isl.Z) + isl.OffY, WorldZ(in isl));
            radius = isl.R;
            return true;
        }

        // Console listing (va_skyislands): nearest first so the user can fly
        // straight to one and verify the emission in-world.
        internal static void PrintIslands(Terminal ctx)
        {
            if (s_islands.Count == 0) { ctx.AddString("va_skyislands: none placed."); return; }
            var p = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            var order = new List<int>(s_islands.Count);
            for (int i = 0; i < s_islands.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                var ia = s_islands[a]; var ib = s_islands[b];
                float ax = WorldX(in ia) - p.x, az = WorldZ(in ia) - p.z, bx = WorldX(in ib) - p.x, bz = WorldZ(in ib) - p.z;
                return (ax * ax + az * az).CompareTo(bx * bx + bz * bz);
            });
            ctx.AddString($"va_skyislands: {s_islands.Count} island(s); nearest first:");
            for (int k = 0; k < Mathf.Min(8, order.Count); k++)
            {
                var isl = s_islands[order[k]];
                float x = WorldX(in isl), z = WorldZ(in isl);
                float dist = Mathf.Sqrt((x - p.x) * (x - p.x) + (z - p.z) * (z - p.z));
                ctx.AddString($"  {dist / 1000f:0.0}km  x={x:0} z={z:0}  topY~{isl.Top + isl.OffY:0}  R={isl.R:0}m  [{KindName(isl.Kind)}]"
                              + (IsMoved(in isl) ? " placed" : ""));
            }
        }

        // TOP SURFACE of an island at (wx,wz) — shared by the SDF (shape),
        // Build (pond levels sample the exact surface) and the rill math.
        // A skyland holding a location is levelled around it: flat across the
        // location's footprint, eased back into the natural top by its reach.
        private static float TopAt(in Island isl, float wx, float wz)
        {
            float natural = NaturalTopAt(in isl, wx, wz);
            if (isl.SiteReach <= 0f) return natural;
            float sx = wx - isl.SiteX, sz = wz - isl.SiteZ;
            float d2 = sx * sx + sz * sz;
            if (d2 >= isl.SiteReach * isl.SiteReach) return natural;
            float d = Mathf.Sqrt(d2);
            if (d <= isl.SiteFlat) return isl.SiteY;
            return Mathf.Lerp(isl.SiteY, natural, Mathf.SmoothStep(0f, 1f, (d - isl.SiteFlat) / (isl.SiteReach - isl.SiteFlat)));
        }

        private static float NaturalTopAt(in Island isl, float wx, float wz)
        {
            if (IsSkyland(isl.Kind)) return SkylandBaseTopAt(in isl, wx, wz, out float edge) + Relief(in isl, wx, wz, edge);
            float dx = wx - isl.X, dz = wz - isl.Z;
            float rr = Mathf.Sqrt(dx * dx + dz * dz) / isl.R;
            if (rr > 1f) rr = 1f;
            float n1 = N(in isl, wx * 0.045f, isl.Seed, wz * 0.045f);
            float hills = (N(in isl, wx * 0.018f, isl.Seed + 7f, wz * 0.018f) - 0.5f)
                          * Mathf.Min(12f, isl.R * 0.10f) * 2f;
            if (isl.Kind == 1)
            {
                // A real peak — up to ~0.46R tall, ridged.
                float n3 = N(in isl, wx * 0.03f, isl.Seed + 91f, wz * 0.03f);
                return isl.BaseY + isl.R * (0.26f + 0.20f * n3) * Mathf.Pow(1f - rr, 1.35f)
                       + hills * 0.5f + (n1 - 0.5f) * 2.2f;
            }
            float top = isl.BaseY + isl.R * 0.04f * (1f - rr * rr) + hills + (n1 - 0.5f) * 2.2f;
            if (isl.Kind == 2)
                top -= isl.R * 0.10f * Mathf.Pow(Mathf.Clamp01(1f - rr / 0.55f), 2f);   // dished centre
            return top;
        }

        // A skyland's top before the ground it carries: its dome, gentle swell and rolled rim. Its underside hangs from this,
        // so hills on top never bulge the keel below.
        private static float SkylandBaseTopAt(in Island isl, float wx, float wz, out float rr)
        {
            float dx = wx - isl.X, dz = wz - isl.Z;
            rr = Mathf.Min(1f, Mathf.Sqrt(dx * dx + dz * dz) / isl.R);
            float width = isl.R, size = isl.R;
            if (isl.Shape != null)
            {
                rr = Mathf.Clamp01(isl.Shape.Sample(wx, wz, out width));
                size = isl.Shape.HeartWidth;
            }
            float n1 = N(in isl, wx * 0.045f, isl.Seed, wz * 0.045f);
            float hills = (N(in isl, wx * 0.018f, isl.Seed + 7f, wz * 0.018f) - 0.5f) * Mathf.Min(12f, size * 0.10f) * 2f;
            float dome = isl.Shape != null ? width * ShapedDomeShare : isl.R * 0.10f;
            return isl.BaseY + dome * (1f - rr * rr) + hills * 0.6f + (n1 - 0.5f) * 1.6f - RimRoll(in isl, wx, wz, rr, width);
        }

        // Rill water level at (wx,wz), or 0 when the point is off the rill.
        // outBed additionally reports the carved bed for the SDF.
        private static float RillLevelAt(in Island isl, float wx, float wz, out float bed)
        {
            bed = 0f;
            if (float.IsNaN(isl.RillAng)) return 0f;
            float dxr = Mathf.Cos(isl.RillAng), dzr = Mathf.Sin(isl.RillAng);
            float rx = wx - isl.PondX, rz = wz - isl.PondZ;
            float along = rx * dxr + rz * dzr;
            float start = isl.PondR * 0.6f;
            if (along < start || along > isl.RillLen) return 0f;
            float perp = Mathf.Abs(rx * dzr - rz * dxr);
            if (perp >= 2.0f) return 0f;
            float t = Mathf.Clamp01((along - start) / Mathf.Max(1f, isl.RillLen - start));
            float lvl = Mathf.Lerp(isl.PondLvl, isl.RillEndLvl, t);
            bed = lvl - 0.75f;
            return lvl;
        }

        // Island pond/rill water level for the chunk column store. One level
        // per column (the store's contract) — callers only apply it where the
        // BASE ground water is absent.
        internal static bool WaterLevelAt(float wx, float wz, out float level)
        {
            level = 0f;
            if (!Enabled) return false;
            if (!s_index.TryGetValue(CellKey(wx, wz), out var list)) return false;
            for (int k = 0; k < list.Count; k++)
            {
                var isl = s_islands[list[k]];
                if (IslandWaterAt(in isl, wx, wz, out float own) && own > level) level = own;
            }
            return level > 0f;
        }

        // One skyland's own water over a world point (its ponds and rivers, crater pond and rill), as a world height.
        private static bool IslandWaterAt(in Island isl, float wx, float wz, out float level)
        {
            level = 0f;
            float sx = wx - isl.OffX, sz = wz - isl.OffZ;
            if (GroundWaterAt(in isl, sx, sz, out float ground, out _)) level = ground + isl.OffY;
            if (isl.PondLvl <= 0f) return level > 0f;
            float pdx = sx - isl.PondX, pdz = sz - isl.PondZ;
            if (pdx * pdx + pdz * pdz < isl.PondR * isl.PondR)
            {
                level = Mathf.Max(level, isl.PondLvl + isl.OffY);
                return level > 0f;
            }
            float rl = RillLevelAt(in isl, sx, sz, out _);
            if (rl > 0f) level = Mathf.Max(level, rl + isl.OffY);
            return level > 0f;
        }

        // Island pond/rill surface as a continuous world function, for the water sheet's corner grid: the
        // level and how deep the bed lies under it. The rill's level ramps smoothly from the pond to the rim.
        internal static bool WaterSurfaceAt(float wx, float wz, out float level, out float depth)
        {
            level = 0f;
            depth = 0f;
            if (!Enabled || !s_index.TryGetValue(CellKey(wx, wz), out var list)) return false;
            for (int k = 0; k < list.Count; k++)
            {
                var isl = s_islands[list[k]];
                float sx = wx - isl.OffX, sz = wz - isl.OffZ;
                if (GroundWaterAt(in isl, sx, sz, out float ground, out float groundDepth) && ground + isl.OffY > level)
                {
                    level = ground + isl.OffY;
                    depth = groundDepth;
                }
                if (isl.PondLvl <= 0f) continue;
                float pdx = sx - isl.PondX, pdz = sz - isl.PondZ;
                float pr2 = pdx * pdx + pdz * pdz, pondR2 = isl.PondR * isl.PondR;
                if (pr2 < pondR2)
                {
                    if (isl.PondLvl + isl.OffY > level)
                    {
                        level = isl.PondLvl + isl.OffY;
                        depth = 0.7f + 1.3f * (1f - pr2 / pondR2);
                    }
                    continue;
                }
                float rl = RillLevelAt(in isl, sx, sz, out float bed);
                if (rl > 0f && rl + isl.OffY > level)
                {
                    level = rl + isl.OffY;
                    depth = rl - bed;
                }
            }
            return level > 0f;
        }

        // ── PROP DRESSING (rocks/stumps/logs on the rim, embedded rocks +
        // hanging roots on the belly) ────────────────────────────────────
        // Deterministic points per island, filtered to the caller's zone
        // rect so each zone spawns exactly its own share once. Pure island
        // math — works before any chunk exists.
        internal static void CollectProps(float x0, float z0, float x1, float z1,
            List<Vector3> pos, List<Quaternion> rot, List<string> name)
        {
            pos.Clear(); rot.Clear(); name.Clear();
            if (!Enabled) return;
            int i0 = Mathf.FloorToInt((x0 - 8f) / INDEX_CELL), i1 = Mathf.FloorToInt((x1 + 8f) / INDEX_CELL);
            int j0 = Mathf.FloorToInt((z0 - 8f) / INDEX_CELL), j1 = Mathf.FloorToInt((z1 + 8f) / INDEX_CELL);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    if (!s_index.TryGetValue(((long)i << 32) ^ (uint)j, out var list)) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        // An island spans several index cells; only its HOME
                        // cell emits, so no duplicates across the loop.
                        var isl = s_islands[list[k]];
                        if (Mathf.FloorToInt(WorldX(in isl) / INDEX_CELL) != i
                            || Mathf.FloorToInt(WorldZ(in isl) / INDEX_CELL) != j) continue;
                        int first = pos.Count;
                        CollectIslandProps(in isl, x0 - isl.OffX, z0 - isl.OffZ, x1 - isl.OffX, z1 - isl.OffZ, pos, rot, name);
                        ShiftOut(in isl, pos, first);
                    }
                }
        }

        // Points a query collected in an island's source frame, carried to where the island stands.
        private static void ShiftOut(in Island isl, List<Vector3> points, int first)
        {
            if (!IsMoved(in isl)) return;
            var offset = new Vector3(isl.OffX, isl.OffY, isl.OffZ);
            for (int n = first; n < points.Count; n++) points[n] += offset;
        }

        private static void CollectIslandProps(in Island isl, float x0, float z0, float x1, float z1,
            List<Vector3> pos, List<Quaternion> rot, List<string> name)
        {
            int hseed = (int)(isl.Seed * 977f);
            // RIM RING — one prop every ~9m of perimeter: rocks breaking off
            // the edge, stumps and fallen logs, the odd root arching over.
            int rimN = Mathf.Max(6, Mathf.CeilToInt(isl.R * 2f * Mathf.PI / 9f));
            for (int k = 0; k < rimN; k++)
            {
                float a = (k + Hash(k, 11, hseed, 3) * 0.8f) * (Mathf.PI * 2f / rimN);
                float rr = 0.90f + Hash(k, 13, hseed, 5) * 0.07f;
                float px = isl.X + Mathf.Cos(a) * rr * isl.R;
                float pz = isl.Z + Mathf.Sin(a) * rr * isl.R;
                if (px < x0 || px >= x1 || pz < z0 || pz >= z1) continue;
                if (InPondOrRill(in isl, px, pz)) continue;
                float py = TopAt(in isl, px, pz);
                float roll = Hash(k, 17, hseed, 7);
                string pf = roll < 0.34f ? "Rock_4" : roll < 0.55f ? "Rock_3"
                          : roll < 0.75f ? "stubbe" : roll < 0.90f ? "FirTree_oldLog" : "root08";
                float sink = pf == "Rock_4" ? 0.8f : pf == "Rock_3" ? 0.5f : 0.15f;
                var q = Quaternion.AngleAxis(Hash(k, 19, hseed, 9) * 360f, Vector3.up)
                      * Quaternion.AngleAxis(Hash(k, 23, hseed, 11) * 14f, Vector3.right);
                pos.Add(new Vector3(px, py - sink, pz)); rot.Add(q); name.Add(pf);
            }
            // BELLY — embedded rocks and roots hanging down the underside.
            int belN = Mathf.Max(4, Mathf.CeilToInt(isl.R * isl.R / 320f));
            for (int k = 0; k < belN; k++)
            {
                float a = Hash(k, 29, hseed, 13) * Mathf.PI * 2f;
                float rr = 0.25f + Hash(k, 31, hseed, 17) * 0.6f;
                float px = isl.X + Mathf.Cos(a) * rr * isl.R;
                float pz = isl.Z + Mathf.Sin(a) * rr * isl.R;
                if (px < x0 || px >= x1 || pz < z0 || pz >= z1) continue;
                float top = TopAt(in isl, px, pz);
                float n2b = N(in isl, px * 0.07f + 31f, isl.Seed + 57f, pz * 0.07f);
                float depthB = Mathf.Min(isl.MaxDepth, isl.R * (0.35f + 0.5f * n2b) * Mathf.Pow(1f - rr, 0.55f));
                float bottom = top - Mathf.Max(2.5f, depthB);
                float roll = Hash(k, 37, hseed, 19);
                if (roll < 0.55f)
                {
                    string pf = roll < 0.30f ? "Rock_3" : "Rock_4";
                    var q = Quaternion.Euler(Hash(k, 41, hseed, 23) * 360f,
                        Hash(k, 43, hseed, 29) * 360f, Hash(k, 47, hseed, 31) * 360f);
                    pos.Add(new Vector3(px, bottom + 0.5f, pz)); rot.Add(q); name.Add(pf);
                }
                else
                {
                    string pf = roll < 0.72f ? "root07" : roll < 0.88f ? "root11" : "root12";
                    // pitched past vertical so the root curls down the belly
                    var q = Quaternion.AngleAxis(Hash(k, 53, hseed, 37) * 360f, Vector3.up)
                          * Quaternion.AngleAxis(150f + Hash(k, 59, hseed, 41) * 60f, Vector3.right);
                    pos.Add(new Vector3(px, bottom + 0.8f, pz)); rot.Add(q); name.Add(pf);
                }
            }
        }

        private static bool InPondOrRill(in Island isl, float wx, float wz)
        {
            if (isl.PondLvl > 0f)
            {
                float dx = wx - isl.PondX, dz = wz - isl.PondZ;
                if (dx * dx + dz * dz < (isl.PondR + 2f) * (isl.PondR + 2f)) return true;
            }
            return RillLevelAt(in isl, wx, wz, out _) > 0f || InGroundWater(in isl, wx, wz);
        }

        // Segment distance for the grotto entrance ramp.
        private static float DistToSegment(float px, float py, float pz,
            float ax, float ay, float az, float bx, float by, float bz)
        {
            float abx = bx - ax, aby = by - ay, abz = bz - az;
            float t = ((px - ax) * abx + (py - ay) * aby + (pz - az) * abz)
                      / Mathf.Max(1e-4f, abx * abx + aby * aby + abz * abz);
            t = Mathf.Clamp01(t);
            float qx = ax + abx * t - px, qy = ay + aby * t - py, qz = az + abz * t - pz;
            return Mathf.Sqrt(qx * qx + qy * qy + qz * qz);
        }

        // Solid-positive SDF-ish metres. Worker-thread safe: pure math over
        // the immutable table (VoxelCaveNoise.Noise01 is stateless).
        internal static float Sdf(float wx, float wy, float wz)
        {
            float best = -1000f;
            int i = Mathf.FloorToInt(wx / INDEX_CELL);
            int j = Mathf.FloorToInt(wz / INDEX_CELL);
            if (!s_index.TryGetValue(((long)i << 32) ^ (uint)j, out var list)) return best;
            for (int k = 0; k < list.Count; k++)
            {
                var isl = s_islands[list[k]];
                float d = IslandDensity(list[k], in isl, wx, wy, wz, wx - isl.OffX, wy - isl.OffY, wz - isl.OffZ);
                if (d > best) best = d;
            }
            return best;
        }

        // One island's density at a world point, (sx, sy, sz) being that point in the island's source frame; the world
        // column only keys the column cache.
        private static float IslandDensity(int index, in Island isl, float wx, float wy, float wz, float sx, float sy, float sz)
        {
            const float Outside = -1000f;
            if (IsSkyland(isl.Kind))
            {
                if (!CachedColumn(index, in isl, wx, wz, sx, sz, out float skyTop, out float skyBottom, out float skySide)) return Outside;
                return SkylandDensity(in isl, sx, sy, sz, skyTop, skyBottom, skySide);
            }
            return LegacyDensity(in isl, sx, sy, sz);
        }

        // A disc island of the original four kinds (rolling, mountain, crater, grotto), in its source frame.
        private static float LegacyDensity(in Island isl, float wx, float wy, float wz)
        {
            const float Outside = -1000f;
            float dx = wx - isl.X, dz = wz - isl.Z;
            float rr = Mathf.Sqrt(dx * dx + dz * dz) / isl.R;
            if (rr >= 0.999f) return Outside;
            // ARCHETYPES (user spec): rolling plains, MINI-MOUNTAIN peaks,
            // CRATER bowls, and GROTTO hollows.
            float top = TopAt(in isl, wx, wz);

            // POND BOWL — a flat-bedded basin dug through the top noise
            // (grottos pool inside the cavity instead, handled below).
            // A raised lip rings open-top ponds so noise dips beside the
            // bowl can never read as a leak.
            if (isl.PondLvl > 0f && isl.Kind != 3)
            {
                float pdx = wx - isl.PondX, pdz = wz - isl.PondZ;
                float pd = Mathf.Sqrt(pdx * pdx + pdz * pdz);
                // THE RIM IS A RAMP, NOT A STEP - the same shape RiverBed uses at a bank.
                // The bed ends at PondLvl-0.7 and the lip sits at PondLvl+0.5, so carving them as
                // two branches put a 1.2 m vertical wall at exactly pd == PondR with no horizontal
                // run at all. Surface Nets meshing a one-cell cliff emits triangles thinner than
                // MinCollidableTriangleArea, PhysX rejects the bake, and the chunk ends up DRAWN
                // but not collidable - the layer that could be seen and fallen through.
                // Rivers never had this because RiverBed climbs out continuously.
                float rimBed = isl.PondLvl - 0.7f;                 // bed height where the bowl meets the rim
                float lipTop = isl.PondLvl + 0.5f;
                if (pd < isl.PondR)
                {
                    float rp = pd / isl.PondR;
                    float bedP = isl.PondLvl - (0.7f + 1.3f * (1f - rp * rp));
                    if (top > bedP) top = bedP;
                }
                else if (pd < isl.PondR + PondRim)
                {
                    // Continuous with the bed at pd == PondR, reaching the lip across the rim band.
                    float t = (pd - isl.PondR) / PondRim;
                    float ramp = Mathf.Lerp(rimBed, lipTop, t * t * (3f - 2f * t));
                    if (top < ramp) top = ramp;
                }
            }
            // OUTLET RILL — a narrow stepped channel from the pond toward
            // the rim, banks held above its water line.
            float rlvl = RillLevelAt(in isl, wx, wz, out float rbed);
            if (rlvl > 0f)
            {
                if (top > rbed) top = rbed;
            }
            else if (!float.IsNaN(isl.RillAng))
            {
                float dxr = Mathf.Cos(isl.RillAng), dzr = Mathf.Sin(isl.RillAng);
                float rx = wx - isl.PondX, rz = wz - isl.PondZ;
                float along = rx * dxr + rz * dzr;
                float perp = Mathf.Abs(rx * dzr - rz * dxr);
                if (along > isl.PondR * 0.6f + 1f && along < isl.RillLen && perp < 3.4f)
                {
                    float t = Mathf.Clamp01((along - isl.PondR * 0.6f) / Mathf.Max(1f, isl.RillLen - isl.PondR * 0.6f));
                    float bank = Mathf.Lerp(isl.PondLvl, isl.RillEndLvl, t) + 0.45f;
                    if (top < bank) top = bank;
                }
            }

            // Underside: depth tapers from the EDGE (pow 0.55 grows fast
            // off the rim — kills the thin brim band the 1.35 curve left)
            // and is strongly randomized so no two bellies match.
            float n2 = N(in isl, wx * 0.07f + 31f, isl.Seed + 57f, wz * 0.07f);
            // MaxDepth clamps the belly on LOW floaters (30m band) so an
            // island never buries its underside into the terrain.
            float depth = Mathf.Min(isl.MaxDepth,
                isl.R * (0.35f + 0.5f * n2) * Mathf.Pow(1f - rr, 0.55f));
            float bottom = top - Mathf.Max(2.5f, depth);
            float d =Mathf.Min(Mathf.Min(top - wy, wy - bottom), (1f - rr) * isl.R * 0.9f);
            if (isl.Kind == 3)
            {
                // GROTTO — an ellipsoid hollow bitten into the body,
                // offset so its mouth breaches the rim/underside. The
                // cavity stops at the pond bed so the floor pools water,
                // and a RAMP TUNNEL from the top surface gives a walkable
                // way in (the cavity's own breach stays as a second
                // mouth where it happens to open).
                float gx2 = isl.X + Mathf.Cos(isl.Seed) * isl.R * 0.35f;
                float gz2 = isl.Z + Mathf.Sin(isl.Seed) * isl.R * 0.35f;
                float gy = isl.BaseY - Mathf.Min(isl.MaxDepth, isl.R * 0.25f) * 0.5f;
                float pondBed = isl.PondLvl > 0f ? isl.PondLvl - 1.5f : float.MinValue;
                float ex = (wx - gx2) / (isl.R * 0.45f);
                float ey = (wy - gy) / Mathf.Max(4f, isl.R * 0.16f);
                float ez = (wz - gz2) / (isl.R * 0.45f);
                float cav = 1f - Mathf.Sqrt(ex * ex + ey * ey + ez * ez);
                if (cav > 0f && wy > pondBed) d = Mathf.Min(d, -cav * 6f);
                // entrance ramp: surface mouth on the cavity's own bearing
                float mAng = isl.Seed;
                float mx = isl.X + Mathf.Cos(mAng) * isl.R * 0.68f;
                float mz = isl.Z + Mathf.Sin(mAng) * isl.R * 0.68f;
                float my = TopAt(in isl, mx, mz) + 1.0f;
                float rampR = Mathf.Max(2.6f, isl.R * 0.055f);
                float ds = DistToSegment(wx, wy, wz, mx, my, mz, gx2, gy + 1.5f, gz2);
                if (ds < rampR && wy > pondBed)
                    d = Mathf.Min(d, -(rampR - ds) * 1.2f);
            }
            return d;
        }
    }
}
