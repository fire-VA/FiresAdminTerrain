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
            if (depthBelowSurface > FiresVoxelConfig.CaveMaxDepth.Value) return -10f;

            float width = FiresVoxelConfig.CaveWidth.Value
                        * Mathf.Clamp01(depthBelowSurface / FiresVoxelConfig.CaveSurfacePinch.Value);
            if (width <= 0.002f) return -10f;

            float f = FiresVoxelConfig.CaveFrequency.Value;
            float fy = FiresVoxelConfig.CaveFrequencyY.Value;
            float n1 = Mathf.Abs(Noise(wx * f, wy * fy, wz * f) - 0.5f);
            float n2 = Mathf.Abs(Noise((wx + 137.7f) * f * 1.31f, (wy - 91.3f) * fy * 1.27f, (wz + 53.9f) * f * 1.31f) - 0.5f);
            float m = Mathf.Max(n1, n2);

            // noise-units → metres: value-noise gradient ≈ 0.5·freq per metre,
            // so distance-to-wall ≈ (width − m) / (0.5·freq). width 0.05 @
            // freq 0.025 ≈ 4m-radius tunnels. Clamped to keep the SDF band sane.
            return Mathf.Clamp((width - m) / Mathf.Max(0.5f * f, 1e-4f), -10f, 12f);
        }
    }
}
