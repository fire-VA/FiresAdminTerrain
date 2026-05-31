using System;
using UnityEngine;
using VerdantsAscent.Terrain.Biome;
using VerdantsAscent.Terrain.Persistence;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // One-shot orchestrator for "paint Ashlands lava at this spot".
    //
    // 2026-05-26 round-25 DIG REMOVED: this now does ONE op — paint the
    // lava overlay. The DIG step that lowered terrain by 1m is gone.
    //
    // Rationale: round-21 wired the lava overlay alpha to vanilla
    // Heightmap.GetHeightOffset's `lerp(0.1m, 0.8m, alpha)` formula in
    // PaintVanillaHeightmapMask. The lava SURFACE now sits at a smoothly
    // varying Y above terrain (0.1m at edges, 0.8m at center) regardless
    // of whether terrain was dug. The dig was always a workaround for
    // "make the lava mesh flush with surrounding terrain"; with the alpha
    // gradient the lava IS the visual top surface and terrain is below.
    //
    // User directive 2026-05-26: "we also no longer need to terrain op
    // lower the ground by any amount when placing lava since it never
    // worked as intended, just overlay it on top of terrain and use the
    // alpha channel to blend it right?".
    //
    // For the swimmable lava water disk (separate tool), see
    // WaterBrushCommands.PlaceSwimmableLava.
    public static class LavaPlacement
    {
        // Returns 0 — no longer digs. Kept as int return for ABI/log
        // compatibility with TileBrushCommands.Cmd_Biome callers.
        public static int PlaceLava(Vector3 center, float radius)
        {
            if (radius <= 0f) return 0;

            // ── PAINT vanilla biome overlay ──────────────────────────────
            //
            // BiomeOverlayPainter handles the in-memory cache + TerrainComp
            // ZDO persistence + Heightmap regen postfix. The downstream
            // PaintVanillaHeightmapMask call (round-21) writes the lava
            // paint mask with alpha falloff, so the lava surface ramps
            // from +0.8m at center to +0.1m at the edge — naturally
            // blending into surrounding terrain without any DIG required.
            try
            {
                BiomeOverlayPainter.PaintOverlay(
                    center, radius,
                    BiomeOverlayPainter.OverlayType.Lava);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[LavaPlacement] PAINT step threw: {ex.Message}");
            }

            return 0;
        }
    }
}
