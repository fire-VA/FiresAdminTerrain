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
            // A player was still swimming in mid-air 7.6 m under the river bed in a big sinkhole's shaft: the swim checks
            // (CustomWaterPatches.GetValidWaterLevelForCharacter: InWater / IsSwimming / GetLiquidLevel) read THIS, not
            // CustomWaterManager.GetWaterLevelAt where the floor is applied. Inside a big shaft below the river bed, the
            // river is not this point's water (the pond's own sheet is).
            if (FatSinkFallCuts.BelowShaftBed(pos, ShaftBedMargin)) return -10000f;
            // 0.2.1097: a generated column that is solid ice, cooled crust or hot lava (WaterStateField) has no liquid level; lava burns
            // through vanilla's heat path (FatLava).
            if (VerdantsAscent.Water.Core.FatLava.GenNotLiquidAt(pos.x, pos.z)) return -10000f;
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

        // `fat_swim_probe <x> <z> [y]` (admin, read-only; for swimming below the drawn surface): at a point, the swim/float level
        // the OLD way (stored columns), the sheet-following way, the drawn sheet itself, their delta, and the switch, so the fix is
        // chosen on numbers. y defaults to the player's height (the layer scan reaches one layer above it).
        internal static void RegisterSwimProbe()
        {
            new Terminal.ConsoleCommand("fat_swim_probe",
                "<x> <z> [y] - generated water at a point: the old swim level, the drawn sheet's level, the delta (read-only)",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (args.Length < 3 || !float.TryParse(args[1], System.Globalization.NumberStyles.Float, inv, out float x)
                        || !float.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out float z))
                    { args.Context?.AddString("fat_swim_probe <x> <z> [y]"); return; }
                    float y = args.Length > 3 && float.TryParse(args[3], System.Globalization.NumberStyles.Float, inv, out float yy) ? yy
                        : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position.y : 40f);
                    var p = new Vector3(x, y, z);
                    float oldLevel = SurfaceAtBase(p, false), sheetLevel = SurfaceAtBase(p, true);
                    int cx = Mathf.FloorToInt(x / VoxelChunk.CELLS), cz = Mathf.FloorToInt(z / VoxelChunk.CELLS);
                    float lxf = x - cx * VoxelChunk.CELLS, lzf = z - cz * VoxelChunk.CELLS;
                    int lx = Mathf.FloorToInt(lxf), lz = Mathf.FloorToInt(lzf);
                    string drawn = "no sheet cell here (edge, dry or no sheet)";
                    for (int ky = -2; ky <= 12; ky++)
                        if (SheetSurfaceAt(new Vector3Int(cx, ky, cz), lx, lz, lxf - lx, lzf - lz, out float d))
                        { drawn = string.Format(inv, "{0:0.000} (chunk ({1},{2},{3}))", d, cx, ky, cz); break; }
                    bool on = FiresVoxelConfig.SwimFollowsDrawnWater != null && FiresVoxelConfig.SwimFollowsDrawnWater.Value;
                    string line = string.Format(inv,
                        "[FatSwimProbe] ({0:0.0},{1:0.0},{2:0.0}): swim level OLD (stored columns) {3}, SHEET-following {4}, drawn sheet {5}; delta sheet-old {6}; " +
                        "in use: {7} ([Voxel Terrain] Swim level follows the drawn water = {8})",
                        x, y, z, Fmt(oldLevel), Fmt(sheetLevel), drawn,
                        oldLevel > -9999f && sheetLevel > -9999f ? (sheetLevel - oldLevel).ToString("+0.000;-0.000;0.000", inv) + " m" : "n/a",
                        on ? "SHEET-following" : "OLD", on);
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                },
                isCheat: true);
        }

        private static string Fmt(float v) => v <= -9999f ? "none" : v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

        // The drawn sheet's surface in a chunk cell, from the view's captured smoothed corners; false at the sheet's edge
        // (any corner dry or frozen) or when the chunk has no sheet. Used by the trigger (switch) and the point probe (always).
        internal static bool SheetSurfaceAt(Vector3Int key, int lx, int lz, float fx, float fz, out float level)
        {
            level = 0f;
            if (!VoxelWorld.TryGetView(key, out var view) || view == null) return false;
            var h = view.SheetCornerH;
            if (h == null) return false;
            int n = VoxelChunk.CELLS + 1;
            if (lx < 0 || lz < 0 || lx + 1 >= n || lz + 1 >= n) return false;
            float h00 = h[lx + n * lz], h10 = h[lx + 1 + n * lz], h01 = h[lx + n * (lz + 1)], h11 = h[lx + 1 + n * (lz + 1)];
            if (h00 <= 0f || h10 <= 0f || h01 <= 0f || h11 <= 0f) return false;
            float bx = key.x * VoxelChunk.CELLS, bz = key.z * VoxelChunk.CELLS;
            if (FrozenAt(bx + lx, bz + lz) || FrozenAt(bx + lx + 1, bz + lz) || FrozenAt(bx + lx, bz + lz + 1) || FrozenAt(bx + lx + 1, bz + lz + 1)) return false;
            level = h00 * (1f - fx) * (1f - fz) + h10 * fx * (1f - fz) + h01 * (1f - fx) * fz + h11 * fx * fz;
            return true;
        }

        private static float SurfaceAtBase(Vector3 pos) => SurfaceAtBase(pos, FiresVoxelConfig.SwimFollowsDrawnWater != null && FiresVoxelConfig.SwimFollowsDrawnWater.Value);

        // useSheet explicit, so fat_swim_probe can read the old trigger and the sheet-following one at the same point.
        internal static float SurfaceAtBase(Vector3 pos, bool useSheet)
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
            // Water on the sky islands: the scan stopped at ky 7 (y 256), so a sky island's stored
            // pond at y 600 was never water to swimmers or FatWaterChecks. It now reaches the layer just above the asker and
            // no higher: water stored in an island's layers counts for someone up there, never for someone on the ground
            // below. Below y 224 the scan is exactly the old one.
            int kyTop = Mathf.Max(7, Mathf.FloorToInt(pos.y / VoxelChunk.CELLS) + 1);
            for (int ky = -2; ky <= kyTop; ky++)
            {
                if (!VoxelWorld.TryGetChunk(new Vector3Int(cx, ky, cz), out var ch)
                    || ch == null || !ch.TryGetWater3x3(lx, lz, s_nine)) continue;

                // Swimming with the drawn surface well above the player ([Voxel Terrain] "Swim level follows
                // the drawn water", default OFF): inside the sheet (all four corners wet), read the SAME smoothed corner heights
                // the sheet was drawn from (VoxelChunkView.SheetCornerH, captured after BuildSheet's smoothing), not the stored
                // columns: those differ by the smoothing, and by a whole reach step where the old rule took the wet MIN. At the
                // edge (any dry corner) the old stored-column rule below still runs.
                if (useSheet
                    && SheetSurfaceAt(new Vector3Int(cx, ky, cz), lx, lz, fx, fz, out float drawn))
                {
                    if (drawn > surface) surface = drawn;
                    continue;
                }

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

        // 0.2.1314 (Fire 10-31 on 1313: "it showed as ice and we fell through and started walking under the ice"): a column was "frozen"
        // here (no liquid level: no swimming) by the world-gen FROZEN class or the old instant seasonal reach (SeasonalFrozenAt), while
        // 1313's ice collider only exists where the ONE column grid is solid - so forming ice (slush, below _FiresIceSolid) had no
        // collider AND no water: you dropped to the bed and walked under the sheet. Frozen = the one grid's value at the column >=
        // _FiresIceSolid (the collider's own test); anything below is water you swim in. No grid yet = the per-column sample.
        private static bool FrozenAt(float wx, float wz)
        {
            float cx = wx + 0.5f, cz = wz + 0.5f;
            bool frozen;
            string src;
            if (WaterStateField.TryGridIce(WaterStateField.GridSrc.Gen, cx, cz, out float ice)) { frozen = ice >= WaterStateField.IceSolid; src = "grid"; }
            else if (GenWaterField.Ready && GenWaterField.TryWaterSurfaceSmooth(cx, cz, out float lvl, out _)) { ice = WaterStateField.Sample(cx, cz, lvl).Ice; frozen = ice >= WaterStateField.IceSolid; src = "column sample"; }
            else { ice = 0f; frozen = false; src = "no water"; }
            if (!s_frozenOneSaid) { s_frozenOneSaid = true; FiresLogger.LogInfo($"[WaterIce] swim / liquid level: a column is frozen only where the one ice grid is solid (>= {WaterStateField.IceSolid:0.00}, the collider's test); slush below that is swimmable water (was: world-gen frozen class / seasonal reach) (0.2.1314)"); }
            if (FiresLogger.DebugEnabled && System.Threading.Thread.CurrentThread.ManagedThreadId == WaterStateField.MainThread && Player.m_localPlayer != null && Time.unscaledTime >= s_frozenWhyAt)
            {
                Vector3 p = Player.m_localPlayer.transform.position;
                if (Mathf.Abs(p.x - cx) < 1.5f && Mathf.Abs(p.z - cz) < 1.5f)
                {
                    s_frozenWhyAt = Time.unscaledTime + 5f;
                    bool oldRule = FiresWorldGenBase.WaterClassAt(wx, wz) == FiresWorldGenBase.WC_FROZEN || FiresWorldGenBase.SeasonalFrozenAt(wx, wz);
                    FiresLogger.LogDebug($"[WaterIce] swim column ({wx:0},{wz:0}) at the player: ice {ice:0.00} via {src}, solid {WaterStateField.IceSolid:0.00} -> {(frozen ? "FROZEN (no liquid, walk the collider)" : "WATER (swim)")}; old rule (class / seasonal reach) said {(oldRule ? "frozen" : "water")} (0.2.1314)");
                }
            }
            return frozen;
        }
        private static bool s_frozenOneSaid;
        private static float s_frozenWhyAt;

        // Main-thread scratch for the one-lock 3x3 batch.
        private static readonly float[] s_nine = new float[9];
    }
}
