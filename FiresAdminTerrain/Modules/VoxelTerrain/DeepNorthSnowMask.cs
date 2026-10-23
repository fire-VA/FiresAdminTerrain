using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Vanilla 1.0 carries Deep North SNOW DEPTH in the paint mask's GREEN channel - the same
    // channel Heightmap.GetCultivationMask reads. WorldGenerator.GetDeepNorthHeight authors it as
    // mask = (0, g, 0, 0) and HeightmapBuilder blends it into the zone's base mask; nothing saves
    // it, so a worldgen patch that drops the out-param drops the field for good.
    //
    // Everything Deep North downstream reads that channel: the 0.1m..2.1m surface offset
    // (Heightmap.GetHeightOffset, which GameCamera, FloatingTerrain and TreeLog follow), the
    // SnowDeep / SnowVeryDeep ground materials, the movement slow and footprint trails in
    // Character.UpdateWalking, SnowRoller growth, and Piece.m_allowedInDeepSnow placement gating.
    //
    // Fires worlds own height and biome selection but NOT this field, so the depth is computed here to
    // give exactly the value the game gives at every point (terrain parity), as measured on the game:
    //   - the position is offset by 100000 plus the world's third offset, added in DOUBLE precision and
    //     rounded to float once;
    //   - the noise is a 3-octave fractal (lacunarity 2, gain 0.5) at scale 0.01f held as a double (the
    //     float 0.01 widened, not the decimal 0.01), and its double result is rounded to float at once;
    //   - the depth is then (noise + 1) / 2 x 0.3 + 0.3 in FLOAT arithmetic, so the two 0.3s stay floats.
    // Any other precision path drifts by a few ulps, which shows as seams between our snow and the game's.
    internal static class DeepNorthSnowMask
    {
        private const double NoiseOffset = 100000.0;
        private const double NoiseScale = 0.01f;
        private const int NoiseOctaves = 3;
        private const double NoiseLacunarity = 2.0;
        private const double NoiseGain = 0.5;
        private const float DepthMin = 0.3f;
        private const float DepthRange = 0.3f;

        public static Color Sample(WorldGenerator generator, float wx, float wy)
        {
            return new Color(0f, Depth(generator, wx, wy), 0f, 0f);
        }

        public static float Depth(WorldGenerator generator, float wx, float wy)
        {
            wx = (float)((double)wx + NoiseOffset + generator.m_offset3);
            wy = (float)((double)wy + NoiseOffset + generator.m_offset3);
            double nx = wx;
            double ny = wy;
            float noise = (float)DUtils.Fbm(
                new Vector2((float)(nx * NoiseScale), (float)(ny * NoiseScale)),
                NoiseOctaves, NoiseLacunarity, NoiseGain);
            noise = (noise + 1f) / 2f;
            return DepthMin + noise * DepthRange;
        }
    }
}
