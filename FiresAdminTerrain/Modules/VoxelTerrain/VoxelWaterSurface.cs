using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Generated-water swim/query router ────────────────────────────────
    // ONE place the swim patches ask "is there generated water here, and how
    // high". Routes to whichever backend is live:
    //   * Legacy Mesh Water ON  → the old per-zone meshed field.
    //   * Voxel Water (default) → the RECONCILED surface — the same
    //     precomputed number the bed carve was cut for and the water sheet is
    //     emitted at, so seen == swum by construction.
    // −10000 = no water here (vanilla's "no liquid" sentinel).
    public static class VoxelWaterSurface
    {
        // Public entry: the settled surface. (The rolling-swell bob is REVERTED
        // in lockstep with the shader — the visual roll wasn't seam-safe and
        // broke the river mesh, so physics must not bob either. FlowWaveY kept
        // for the seam-safe re-approach.)
        private const float ShaftBedMargin = 0.3f;

        public static float SurfaceAt(Vector3 pos)
        {
            // 0.2.817 (Fire 10-07: still swimming in mid-air 7.6 m under the river bed in a big sinkhole's shaft): the swim checks
            // (CustomWaterPatches.GetValidWaterLevelForCharacter: InWater / IsSwimming / GetLiquidLevel) read THIS, not
            // CustomWaterManager.GetWaterLevelAt where 0.2.814 put the floor. Inside a big shaft below the river bed, the
            // river is not this point's water (the pond's own sheet is).
            if (FatSinkFallCuts.BelowShaftBed(pos, ShaftBedMargin)) return -10000f;
            return SurfaceAtBase(pos);
        }

        // (kept for the seam-safe roll re-approach)
        // Mirror of the shader FiresFlowWaveY (coherence fixed at 1 = the
        // coherent-channel chop; the confluence chop is a sub-decimetre visual
        // detail buoyancy doesn't need). _WaterTime == FiresWaterClock.Now, so
        // physics reproduces the shader's exact phase.
        private static float FlowWaveY(float wx, float wz)
        {
            // Legacy mesh water bakes no flow UV1, so its visual doesn't roll —
            // keep physics matched (no bob) there.
            if (FiresVoxelConfig.LegacyMeshWater != null && FiresVoxelConfig.LegacyMeshWater.Value) return 0f;
            var ampE = FiresLiquidConfig.FlowWaveAmp;
            if (ampE == null) return 0f;
            float amp = ampE.Value;
            if (amp <= 0.0001f) return 0f;
            Vector2 fdir = FiresWorldGenBase.GenFlowAt(wx, wz);
            float fspd = fdir.magnitude;
            if (fspd < 0.0005f) return 0f;
            Vector2 fn = fdir / fspd;
            float spd01 = Mathf.Clamp01(fspd / 0.06f);
            float lenM = FiresLiquidConfig.FlowWaveLength != null ? FiresLiquidConfig.FlowWaveLength.Value : 6f;
            float freq = 6.2831853f / Mathf.Max(1f, lenM);
            float speed = FiresLiquidConfig.FlowWaveSpeed != null ? FiresLiquidConfig.FlowWaveSpeed.Value : 1f;
            float tt = FiresWaterClock.Now * speed;
            float swell = Mathf.Sin((wx * fn.x + wz * fn.y) * freq - tt * (0.6f + spd01 * 1.6f));
            Vector2 cn = new Vector2(fn.x * 0.80f - fn.y * 0.60f, fn.x * 0.60f + fn.y * 0.80f);
            float chop = 0.15f * Mathf.Sin((wx * cn.x + wz * cn.y) * freq * 2.4f - tt * 1.9f);
            return (swell + chop) * amp * spd01;
        }

        private static float SurfaceAtBase(Vector3 pos)
        {
            if (FiresVoxelConfig.LegacyMeshWater != null && FiresVoxelConfig.LegacyMeshWater.Value)
                return VoxelGenWaterMesh.SurfaceAt(pos);
            if (FiresVoxelConfig.VoxelWater != null && !FiresVoxelConfig.VoxelWater.Value) return -10000f;

            // THE STORED COLUMNS, not the global field. The sheet is built from
            // chunk._genWater in the same apply as the terrain — so a swim
            // answer sourced anywhere else can disagree with the screen, and
            // did: the global field said wet across areas whose chunks never
            // stored (nearest sheet 2668m from a swimming player, measured).
            // Reading the data the mesh was built from makes swim-without-water
            // structurally impossible: no stored column, no swim; stored
            // column, sheet exists by construction.
            int cx = Mathf.FloorToInt(pos.x / VoxelChunk.CELLS);
            int cz = Mathf.FloorToInt(pos.z / VoxelChunk.CELLS);
            float lxf = pos.x - cx * VoxelChunk.CELLS;
            float lzf = pos.z - cz * VoxelChunk.CELLS;
            int lx = Mathf.FloorToInt(lxf), lz = Mathf.FloorToInt(lzf);
            float fx = lxf - lx, fz = lzf - lz;
            // Layers in a stack are NOT interchangeable any more: a brush edit
            // spreads water on the layer it touched, the others keep baseline.
            // Scan the whole stack and keep the best answer.
            float surface = -10000f;
            // 0.2.715 (Fire 10-05: water on the sky islands): the scan stopped at ky 7 (y 256), so a sky island's stored
            // pond at y 600 was never water to swimmers or FatWaterChecks. It now reaches the layer just above the asker and
            // no higher: water stored in an island's layers counts for someone up there, never for someone on the ground
            // below. Below y 224 the scan is exactly the old one.
            int kyTop = Mathf.Max(7, Mathf.FloorToInt(pos.y / VoxelChunk.CELLS) + 1);
            for (int ky = -2; ky <= kyTop; ky++)
            {
                if (!VoxelWorld.TryGetChunk(new Vector3Int(cx, ky, cz), out var ch)
                    || ch == null || !ch.TryGetWater3x3(lx, lz, s_nine)) continue;

                // CLAMPED TO THE MESH. The sheet's quads interpolate linearly
                // between the stored corner levels with dry corners taking the
                // wet max — so a bilinear read over the SAME corners with the
                // SAME rule is exactly the rendered surface. Where the river
                // ramps down to meet the sea, the trigger now ramps with it;
                // the old 3x3 MAX held swimmers at the upstream level all the
                // way down the slope.
                // FROZEN class = ice, not water: those columns render a
                // walkable IceShell and must read DRY here, or you swim
                // through solid ice.
                float bx = cx * VoxelChunk.CELLS, bz = cz * VoxelChunk.CELLS;
                float l00 = FrozenAt(bx + lx, bz + lz) ? 0f : s_nine[4];
                float l10 = FrozenAt(bx + lx + 1, bz + lz) ? 0f : s_nine[5];
                float l01 = FrozenAt(bx + lx, bz + lz + 1) ? 0f : s_nine[7];
                float l11 = FrozenAt(bx + lx + 1, bz + lz + 1) ? 0f : s_nine[8];
                float mx = Mathf.Max(Mathf.Max(l00, l10), Mathf.Max(l01, l11));
                if (mx > 0f)
                {
                    // Dry corners take the wet MIN. The mesh fills them with
                    // MAX to bury its edge inside the bank — right for pixels,
                    // wrong for the trigger: at a river MOUTH the max held
                    // swimmers at the upstream level while the visible sheet
                    // ramped away beneath them (reported twice). Min keeps the
                    // trigger AT or a hair under the rendered surface — never
                    // above it — and at banks min≈max so nothing changes.
                    float mn = float.MaxValue;
                    if (l00 > 0f && l00 < mn) mn = l00;
                    if (l10 > 0f && l10 < mn) mn = l10;
                    if (l01 > 0f && l01 < mn) mn = l01;
                    if (l11 > 0f && l11 < mn) mn = l11;
                    if (l00 <= 0f) l00 = mn;
                    if (l10 <= 0f) l10 = mn;
                    if (l01 <= 0f) l01 = mn;
                    if (l11 <= 0f) l11 = mn;
                    float s = l00 * (1f - fx) * (1f - fz) + l10 * fx * (1f - fz)
                            + l01 * (1f - fx) * fz + l11 * fx * fz;
                    if (s > surface) surface = s;
                    continue;
                }

                // Containing cell fully dry: straddle fallback so a swimmer
                // hugging the bank edge is not dropped mid-stroke (the legacy
                // mesh learned this the hard way). MIN of the wet neighbours,
                // same mouth reasoning as above.
                float near = float.MaxValue;
                for (int k = 0; k < 9; k++)
                {
                    if (s_nine[k] <= 0f) continue;
                    if (FrozenAt(bx + lx + (k % 3) - 1, bz + lz + (k / 3) - 1)) continue;
                    if (s_nine[k] < near) near = s_nine[k];
                }
                if (near != float.MaxValue && near > surface) surface = near;
            }
            return surface;
        }

        private static bool FrozenAt(float wx, float wz)
            => FiresWorldGenBase.WaterClassAt(wx, wz) == FiresWorldGenBase.WC_FROZEN
            || FiresWorldGenBase.SeasonalFrozenAt(wx, wz);

        // Main-thread scratch for the one-lock 3x3 batch.
        private static readonly float[] s_nine = new float[9];
    }
}
