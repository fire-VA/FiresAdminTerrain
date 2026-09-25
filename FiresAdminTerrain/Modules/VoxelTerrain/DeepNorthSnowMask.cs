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
    // Fires worlds own height and biome selection but NOT this field, so the depth is reproduced
    // here operation for operation as WorldGenerator.GetDeepNorthHeight computes it, including
    // where it widens to double and where it narrows back to float.
    //
    // THAT SEQUENCING CANNOT BE READ OFF assembly_valheimREF1.0\WorldGenerator.cs. The
    // decompilation renders the offset add as `wx = wx + 100000f + this.m_offset3` and the depth as
    // one double expression; both drop conversions the IL actually emits. Verified against the
    // shipped assembly_valheim.dll (2,569,728 B, 2026-09-18), GetDeepNorthHeight:
    //   IL_0014 conv.r8 / ldc.r8 100000 / add / ldfld m_offset3 / conv.r8 / add / IL_0027 conv.r4
    //     -> the offset add runs in DOUBLE and narrows exactly ONCE, not three float roundings.
    //   IL_01CD call float64 DUtils::Fbm / IL_01D2 conv.r4 / ldc.r4 1 add / ldc.r4 2 div /
    //   ldc.r4 0.3 / ldc.r4 0.3 mul / add
    //     -> Fbm's double result narrows IMMEDIATELY and the rest is FLOAT, so the two 0.3s are
    //        ldc.r4 and must stay float consts here. NoiseScale stays double: vanilla loads
    //        ldc.r8 0.00999999977648258, which is 0.01f widened, not the decimal 0.01.
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
