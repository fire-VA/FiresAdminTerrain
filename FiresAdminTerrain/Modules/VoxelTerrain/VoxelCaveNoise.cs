using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // W1 — deterministic cave field. Our OWN hash-based 3D value noise (seeded
    // from the world seed) so every client generates identical caves on every
    // login — determinism is a hard requirement (caves shifting under builds
    // would be catastrophic). NEVER change this math for existing worlds
    // without a worldgen-version gate (Stage-W risk list).
    //
    // Cave shape: "spaghetti" tunnels = the intersection of two independent
    // noise bands (carve where BOTH |n−0.5| are small — the modern
    // noise-intersection approach). Returns an SDF-ish carve value in metres
    // so tunnel walls interpolate smoothly instead of stair-stepping.
    public static class VoxelCaveNoise
    {
        // Smallest carve a sinkhole is allowed to make (metres). Anything less
        // is a sliver, not a hole — see the ribbon note in CarveSdf.
        private const float SINK_MIN_BITE = 0.75f;

        private static int s_seed;
        public static void SetSeed(int worldSeed) => s_seed = worldSeed;

        // The world's seed, for a skyland blueprint to carry: a skyland raised in another world samples the noise of the
        // world it grew in (FiresSkyIslands.Placed.cs), through the seeded overloads below.
        public static int Seed => s_seed;

        // Per-cell sinkhole veto cache. CarveSdf runs per VOXEL, but the site
        // and its wet test are per CELL, so without a cache the water field
        // would be sampled nine times for every voxel in the column. Chunk
        // generation walks columns in order, so a one-entry cache hits almost
        // always. Thread-static: chunk generation is multi-threaded.
        [System.ThreadStatic] private static int t_sinkCX, t_sinkCZ;
        [System.ThreadStatic] private static bool t_sinkValid, t_sinkWet;

        private static bool SinkholeSiteWet(int cx, int cz, float sx, float sz, float radius)
        {
            if (t_sinkValid && t_sinkCX == cx && t_sinkCZ == cz) return t_sinkWet;
            // No caching while the water field is down: a chunk generated in
            // that window would latch "dry" for the cell and carve a shaft
            // under a river the field would have vetoed.
            if (!FiresWorldGenBase.Active) return false;
            t_sinkCX = cx; t_sinkCZ = cz; t_sinkValid = true;
            t_sinkWet = false;
            {
                // Centre plus a ring at the rim + margin: a shaft must clear the
                // channel entirely, not merely miss its centreline.
                float m = radius * 1.6f + 4f;
                if (FiresWorldGenBase.IsWetAtReal(sx, sz)) t_sinkWet = true;
                else
                    for (int k = 0; k < 8 && !t_sinkWet; k++)
                    {
                        float a = k * (Mathf.PI * 2f / 8f);
                        if (FiresWorldGenBase.IsWetAtReal(sx + Mathf.Cos(a) * m, sz + Mathf.Sin(a) * m))
                            t_sinkWet = true;
                    }
            }
            return t_sinkWet;
        }

        // Per-column wet cache for the ENTRANCE veto (4m quantized). An
        // entrance releasing the surface pinch under a riverbed drives the
        // tunnel through the bed to open air — the "sinkhole in the river"
        // holes the shaft veto alone never covered.
        [System.ThreadStatic] private static int t_entCX, t_entCZ;
        [System.ThreadStatic] private static bool t_entValid, t_entWet;

        private static bool EntranceColumnWet(float wx, float wz)
        {
            int cx = Mathf.FloorToInt(wx * 0.25f), cz = Mathf.FloorToInt(wz * 0.25f);
            if (t_entValid && t_entCX == cx && t_entCZ == cz) return t_entWet;
            if (!FiresWorldGenBase.Active) return false;
            t_entCX = cx; t_entCZ = cz; t_entValid = true;
            float sx = (cx + 0.5f) * 4f, sz = (cz + 0.5f) * 4f;
            t_entWet = FiresWorldGenBase.IsWetAtReal(sx, sz)
                || FiresWorldGenBase.IsWetAtReal(sx + 10f, sz) || FiresWorldGenBase.IsWetAtReal(sx - 10f, sz)
                || FiresWorldGenBase.IsWetAtReal(sx, sz + 10f) || FiresWorldGenBase.IsWetAtReal(sx, sz - 10f);
            return t_entWet;
        }

        private static float Hash(int x, int y, int z) => Hash(x, y, z, s_seed);

        private static float Hash(int x, int y, int z, int seed)
        {
            unchecked
            {
                int h = seed;
                h = h * 374761393 + x * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                h += y * 974711;
                h = (h ^ (h >> 16)) * -1640531535;
                h += z * 373587883;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777215f;   // 0..1
            }
        }

        // Trilinear value noise with smoothstep fades.
        private static float Noise(float x, float y, float z) => Noise(x, y, z, s_seed);

        private static float Noise(float x, float y, float z, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fy = y - y0, fz = z - z0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            fz = fz * fz * (3f - 2f * fz);

            float c000 = Hash(x0, y0, z0, seed), c100 = Hash(x0 + 1, y0, z0, seed);
            float c010 = Hash(x0, y0 + 1, z0, seed), c110 = Hash(x0 + 1, y0 + 1, z0, seed);
            float c001 = Hash(x0, y0, z0 + 1, seed), c101 = Hash(x0 + 1, y0, z0 + 1, seed);
            float c011 = Hash(x0, y0 + 1, z0 + 1, seed), c111 = Hash(x0 + 1, y0 + 1, z0 + 1, seed);

            float x00 = c000 + (c100 - c000) * fx;
            float x10 = c010 + (c110 - c010) * fx;
            float x01 = c001 + (c101 - c001) * fx;
            float x11 = c011 + (c111 - c011) * fx;
            float y0v = x00 + (x10 - x00) * fy;
            float y1v = x01 + (x11 - x01) * fy;
            return y0v + (y1v - y0v) * fz;
        }

        // Public deterministic noise for other generation passes (river bank
        // erosion detail, undercuts) — same seed, same guarantees.
        public static float Noise01(float x, float y, float z) => Noise(x, y, z);

        public static float Noise01(float x, float y, float z, int seed) => Noise(x, y, z, seed);

        // ── SINKHOLES (0.2.751; Fire 10-05: "our sinkholes are often just straight cylinders with broken latticed terrain
        // over the top, the holes should be fully open ... irregular and appear as real sinkholes with the debris that
        // 'fell in' piled up along the bottom") ───────────────────────────────────────────────────────────────────────
        // The shaft is a true signed distance (metres, > 0 inside): its radius at a depth minus the distance from its
        // axis. Up to 0.2.750 it was a cone value shifted down by a minimum bite, only ~1 m positive at the centre of
        // the mouth: the top cells were left half cleared and the mesher skinned them as a lattice. Now the mouth is as
        // open as the shaft and the wall's zero crossing is clean. Per site (deterministic from the cell): a lean (the
        // axis drifts sideways with depth), a funnel (wider mouth) or a bell (an overhanging lip), wall noise in 3D so
        // no two depths share a cross-section, a floor short of the full depth, and a mound of fallen rock heaped in the
        // middle of the floor. The cave-content pass dresses that mound with fallen rubble, logs and stones.
        internal struct SinkholeSite
        {
            public float X, Z, Radius, Floor, LeanX, LeanZ, Mound;
            public bool Bell;
        }

        private static SinkholeSite SiteOf(int cx, int cz, float x, float z, float radius, float sinkDepth, int seed)
        {
            return new SinkholeSite
            {
                X = x, Z = z, Radius = radius,
                Floor = sinkDepth * (0.8f + 0.2f * Hash(cx, 90004, cz, seed)),
                LeanX = (Hash(cx, 90005, cz, seed) - 0.5f) * 0.3f,
                LeanZ = (Hash(cx, 90006, cz, seed) - 0.5f) * 0.3f,
                Mound = 1.5f + 2.5f * Hash(cx, 90007, cz, seed),
                Bell = Hash(cx, 90008, cz, seed) < 0.35f,
            };
        }

        private static float SinkholeSdf(in SinkholeSite site, float wx, float wz, float depth, int seed)
        {
            float d = Mathf.Max(0f, depth);
            float ax = site.X + site.LeanX * d, az = site.Z + site.LeanZ * d;
            float dx = wx - ax, dz = wz - az;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            float t = Mathf.Clamp01(d / Mathf.Max(site.Floor, 1f));
            // Funnel: 1.25x at the mouth narrowing to 0.9x; bell: 0.8x at the lip widening to 1.15x (an overhang).
            float profile = site.Bell ? Mathf.Lerp(0.8f, 1.15f, Mathf.Sqrt(t)) : Mathf.Lerp(1.25f, 0.9f, t);
            // Wall noise in 3D: big lobes plus a finer crumble, so the wall is never one circle swept down.
            float lobes = Noise(wx * 0.11f + 31.7f, d * 0.09f + 613.1f, wz * 0.11f - 17.3f, seed);
            float crumble = Noise(wx * 0.35f - 5.1f, d * 0.3f + 77.2f, wz * 0.35f + 9.4f, seed);
            float r = site.Radius * profile * (0.72f + 0.56f * lobes) + (crumble - 0.5f) * 0.9f;
            float wall = r - dist;
            // The floor, and the fallen-rock mound heaped in its middle (higher at the centre, lumpy).
            float heap = site.Mound * Mathf.Pow(1f - Mathf.Clamp01(dist / Mathf.Max(1f, r * 0.85f)), 1.4f)
                         + (Noise(wx * 0.45f + 3.3f, 911.7f, wz * 0.45f - 8.8f, seed) - 0.5f) * 0.8f;
            float floor = (site.Floor - heap) - depth;
            return Mathf.Min(wall, floor);
        }

        // The sinkhole of the spacing cell a world point lies in, if that cell has one (same rarity and river veto as the
        // carve). For the cave-content pass: where the shaft's floor and fallen rock are.
        internal static bool TrySinkholeAt(float wx, float wz, out SinkholeSite site)
        {
            site = default;
            if (FiresVoxelConfig.SinkholeSpacing == null || FiresVoxelConfig.SinkholeRadius == null || FiresVoxelConfig.SinkholeDepth == null) return false;
            float spacing = Mathf.Max(16f, FiresVoxelConfig.SinkholeSpacing.Value);
            int cx = Mathf.FloorToInt(wx / spacing), cz = Mathf.FloorToInt(wz / spacing);
            float radius = Mathf.Max(1f, FiresVoxelConfig.SinkholeRadius.Value);
            float inset = Mathf.Clamp01((radius * 1.5f) / spacing);
            float span = Mathf.Max(0f, 1f - inset * 2f);
            float sxw = (cx + inset + span * Hash(cx, 90002, cz, s_seed)) * spacing;
            float szw = (cz + inset + span * Hash(cx, 90003, cz, s_seed)) * spacing;
            if (Hash(cx, 90001, cz, s_seed) < FiresVoxelConfig.SinkholeRarity.Value || SinkholeSiteWet(cx, cz, sxw, szw, radius)) return false;
            site = SiteOf(cx, cz, sxw, szw, radius, FiresVoxelConfig.SinkholeDepth.Value, s_seed);
            return true;
        }

        // Carve SDF at a world position (metres, > 0 inside a tunnel): the noise
        // caves, unioned with a chunk world's guaranteed start cave.
        public static float CarveSdf(float wx, float wy, float wz, float depthBelowSurface, bool sinkholes = true)
        {
            float carve = CarveNoise(wx, wy, wz, depthBelowSurface, sinkholes, s_seed, true);
            float start = Zones.ZoneModeStartCave.Carve(wx, wy, wz);
            return start > carve ? Mathf.Min(start, 12f) : carve;
        }

        // The caves of another world's seed, for a skyland raised from a blueprint saved there: its tunnels and caverns
        // only. This world's start cave and its river vetoes belong to this world's ground, not to the island.
        public static float CarveSdf(float wx, float wy, float wz, float depthBelowSurface, int seed)
            => CarveNoise(wx, wy, wz, depthBelowSurface, false, seed, false);

        // depthBelowSurface pinches tunnels shut approaching the surface so
        // most stay buried and a few break through as natural entrances.
        // Gates: only above CaveMinY (W1 keeps caves above the y30 water
        // table — flooded caves need the tier-2 water mesh first) and within
        // CaveMaxDepth of the surface (no bottomless voids).
        private static float CarveNoise(float wx, float wy, float wz, float depthBelowSurface, bool sinkholes, int seed, bool worldVetoes)
        {
            if (wy < FiresVoxelConfig.CaveMinY.Value) return -10f;

            float f = FiresVoxelConfig.CaveFrequency.Value;

            // SINKHOLES — rare open vertical drops from the surface into the
            // tunnel layer. A sparse HORIZONTAL mask (no Y term) makes each one a
            // column rather than a blob; the noise level-set doubles as the radial
            // falloff, so the shaft is widest at its centre and tapers to nothing
            // at the rim. Evaluated before the depth gate because a sinkhole is
            // exactly the case that must reach the open air.
            float sinkhole = -10f;
            float sinkDepth = FiresVoxelConfig.SinkholeDepth.Value;
            if (sinkholes && depthBelowSurface <= sinkDepth)
            {
                // ONE SHAFT PER SPACING CELL. The old sparse-noise mask cut
                // wherever the field crossed its threshold, and a smooth field
                // crosses a threshold along a whole CONTOUR — so a single high
                // region produced a scatter of part-holes and slivers across an
                // entire hillside instead of one hole. Quantise to a spacing
                // grid and give each cell exactly one jittered site: a column
                // carves only inside that site's radius, so the extras are never
                // cut at all and the roof between sites is never opened. That is
                // strictly better than carving them and patching the mesh back
                // over — there is nothing to patch.
                float spacing = Mathf.Max(16f, FiresVoxelConfig.SinkholeSpacing.Value);
                int cx = Mathf.FloorToInt(wx / spacing);
                int cz = Mathf.FloorToInt(wz / spacing);
                // Rarity now reads as "share of cells with NO shaft", so the
                // existing config still means higher = rarer.
                float radius = Mathf.Max(1f, FiresVoxelConfig.SinkholeRadius.Value);
                // Inset the jitter so a site can never straddle a cell edge
                // and end up touching its neighbour's shaft.
                float inset = Mathf.Clamp01((radius * 1.5f) / spacing);
                float span = Mathf.Max(0f, 1f - inset * 2f);
                float sxw = (cx + inset + span * Hash(cx, 90002, cz, seed)) * spacing;
                float szw = (cz + inset + span * Hash(cx, 90003, cz, seed)) * spacing;
                // NEVER UNDER A RIVER. A shaft that opens in a bed drains the
                // channel into the cave layer and leaves the holes punched
                // straight through the riverbed. Skips the SHAFT only — tunnels
                // below still carve here, they just stay roofed. The site is
                // deterministic per cell so the test is cached per cell per
                // thread; the field reads are immutable load-time arrays and
                // therefore worker-safe.
                if (Hash(cx, 90001, cz, seed) >= FiresVoxelConfig.SinkholeRarity.Value
                    && (!worldVetoes || !SinkholeSiteWet(cx, cz, sxw, szw, radius)))
                {
                    sinkhole = SinkholeSdf(SiteOf(cx, cz, sxw, szw, radius, sinkDepth, seed), wx, wz, depthBelowSurface, seed);
                }
            }

            if (depthBelowSurface > FiresVoxelConfig.CaveMaxDepth.Value)
                return Mathf.Clamp(sinkhole, -10f, 12f);

            // ENTRANCES — tunnels normally pinch shut approaching the surface so
            // they stay buried. Where this sparse horizontal mask runs high the
            // pinch is released and the tunnel drives clean through to open air,
            // which is what turns "occasional accidental breakthrough" into
            // findable entrances.
            float pinch = Mathf.Clamp01(depthBelowSurface / FiresVoxelConfig.CaveSurfacePinch.Value);
            float eThresh = FiresVoxelConfig.EntranceRarity.Value;
            float ef = f * 0.35f;
            float ent = Noise(wx * ef + 913.1f, 3.21f, wz * ef + 285.7f, seed);
            // NEVER release the pinch under water: an entrance in a riverbed
            // is a drain hole, exactly like the vetoed shafts. Tunnels still
            // run below — they just keep their roof.
            if (ent > eThresh && (!worldVetoes || !EntranceColumnWet(wx, wz)))
            {
                float e = (ent - eThresh) / Mathf.Max(1f - eThresh, 1e-4f);
                pinch = Mathf.Lerp(pinch, 1f, Mathf.SmoothStep(0f, 1f, e));
            }

            float width = FiresVoxelConfig.CaveWidth.Value * pinch;

            // CAVERNS — a much lower-frequency octave that locally fattens the
            // tunnel into a chamber. Because it is low frequency the rooms are
            // large and occasional, and because it only ADDS width the tunnel
            // network stays continuous: passages open out and close back down
            // instead of running at one gauge forever.
            float cThresh = FiresVoxelConfig.CavernRarity.Value;
            float cf = f * FiresVoxelConfig.CavernScale.Value;
            float ch = Noise(wx * cf + 411.2f, wy * cf * 0.6f + 88.5f, wz * cf + 77.4f, seed);
            if (ch > cThresh)
            {
                float c = (ch - cThresh) / Mathf.Max(1f - cThresh, 1e-4f);
                width += FiresVoxelConfig.CavernBoost.Value * Mathf.SmoothStep(0f, 1f, c) * pinch;
            }

            if (width <= 0.002f) return Mathf.Clamp(sinkhole, -10f, 12f);

            float fy = FiresVoxelConfig.CaveFrequencyY.Value;
            float n1 = Mathf.Abs(Noise(wx * f, wy * fy, wz * f, seed) - 0.5f);
            float n2 = Mathf.Abs(Noise((wx + 137.7f) * f * 1.31f, (wy - 91.3f) * fy * 1.27f, (wz + 53.9f) * f * 1.31f, seed) - 0.5f);
            float m = Mathf.Max(n1, n2);

            // noise-units → metres: value-noise gradient ≈ 0.5·freq per metre,
            // so distance-to-wall ≈ (width − m) / (0.5·freq). width 0.05 @
            // freq 0.025 ≈ 4m-radius tunnels. Clamped to keep the SDF band sane.
            float tunnel = (width - m) / Mathf.Max(0.5f * f, 1e-4f);

            // Near the surface the carve is SHIFTED down by the min-bite
            // (fading back to no shift at 3m depth) — small carves close
            // smoothly instead of flipping per-voxel. The old binary gate
            // aliased into checkerboard shells around breach rims.
            if (depthBelowSurface < 3f)
                tunnel -= SINK_MIN_BITE * (1f - depthBelowSurface / 3f);

            // Union of the two carves — a sinkhole punches through regardless of
            // whether a tunnel happens to run here, and drops into one where it does.
            return Mathf.Clamp(Mathf.Max(tunnel, sinkhole), -10f, 12f);
        }
    }
}
