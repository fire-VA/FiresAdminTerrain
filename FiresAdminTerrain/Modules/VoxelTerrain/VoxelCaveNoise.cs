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

        private static float Hash(int x, int y, int z)
        {
            unchecked
            {
                int h = s_seed;
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
        private static float Noise(float x, float y, float z)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fy = y - y0, fz = z - z0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            fz = fz * fz * (3f - 2f * fz);

            float c000 = Hash(x0, y0, z0), c100 = Hash(x0 + 1, y0, z0);
            float c010 = Hash(x0, y0 + 1, z0), c110 = Hash(x0 + 1, y0 + 1, z0);
            float c001 = Hash(x0, y0, z0 + 1), c101 = Hash(x0 + 1, y0, z0 + 1);
            float c011 = Hash(x0, y0 + 1, z0 + 1), c111 = Hash(x0 + 1, y0 + 1, z0 + 1);

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

        // Carve SDF at a world position (metres, > 0 inside a tunnel).
        // depthBelowSurface pinches tunnels shut approaching the surface so
        // most stay buried and a few break through as natural entrances.
        // Gates: only above CaveMinY (W1 keeps caves above the y30 water
        // table — flooded caves need the tier-2 water mesh first) and within
        // CaveMaxDepth of the surface (no bottomless voids).
        public static float CarveSdf(float wx, float wy, float wz, float depthBelowSurface)
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
            if (depthBelowSurface <= sinkDepth)
            {
                float sThresh = FiresVoxelConfig.SinkholeRarity.Value;
                float sf = f * 0.22f;
                float sh = Noise(wx * sf + 1721.3f, 7.77f, wz * sf + 449.9f);
                if (sh > sThresh)
                {
                    float t = (sh - sThresh) / Mathf.Max(1f - sThresh, 1e-4f);
                    // FUNNEL, not a column. Narrow at the lip and opening out as
                    // it descends, so the cave ROOF survives everywhere except
                    // the entrance itself. The first cut had this inverted —
                    // widest at the surface — which stripped the roof across the
                    // whole shaft footprint and left the ceiling in floating
                    // fragments over a butchered crater.
                    float depthT = Mathf.Clamp01(depthBelowSurface / Mathf.Max(sinkDepth, 1f));
                    float profile = Mathf.Lerp(0.28f, 1f, depthT * depthT);
                    float bite = t * FiresVoxelConfig.SinkholeRadius.Value * profile;
                    // A shaft either cuts properly or not at all. Around the
                    // threshold contour `bite` ramps up from ~0, and a carve of a
                    // few centimetres leaves a paper-thin shell sitting on the
                    // original surface — those are the ribbons//checkerboard
                    // slivers left hanging around a sinkhole. Below the minimum
                    // bite the shaft simply doesn't cut, giving a clean rim.
                    sinkhole = bite >= SINK_MIN_BITE ? bite : -10f;
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
            float ent = Noise(wx * ef + 913.1f, 3.21f, wz * ef + 285.7f);
            if (ent > eThresh)
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
            float ch = Noise(wx * cf + 411.2f, wy * cf * 0.6f + 88.5f, wz * cf + 77.4f);
            if (ch > cThresh)
            {
                float c = (ch - cThresh) / Mathf.Max(1f - cThresh, 1e-4f);
                width += FiresVoxelConfig.CavernBoost.Value * Mathf.SmoothStep(0f, 1f, c) * pinch;
            }

            if (width <= 0.002f) return Mathf.Clamp(sinkhole, -10f, 12f);

            float fy = FiresVoxelConfig.CaveFrequencyY.Value;
            float n1 = Mathf.Abs(Noise(wx * f, wy * fy, wz * f) - 0.5f);
            float n2 = Mathf.Abs(Noise((wx + 137.7f) * f * 1.31f, (wy - 91.3f) * fy * 1.27f, (wz + 53.9f) * f * 1.31f) - 0.5f);
            float m = Mathf.Max(n1, n2);

            // noise-units → metres: value-noise gradient ≈ 0.5·freq per metre,
            // so distance-to-wall ≈ (width − m) / (0.5·freq). width 0.05 @
            // freq 0.025 ≈ 4m-radius tunnels. Clamped to keep the SDF band sane.
            float tunnel = (width - m) / Mathf.Max(0.5f * f, 1e-4f);

            // Union of the two carves — a sinkhole punches through regardless of
            // whether a tunnel happens to run here, and drops into one where it does.
            return Mathf.Clamp(Mathf.Max(tunnel, sinkhole), -10f, 12f);
        }
    }
}
