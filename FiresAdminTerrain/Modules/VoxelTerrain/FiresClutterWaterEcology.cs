using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Clutter ecology around generated water ───────────────────────────
    // The vegetation port (FiresVegWaterEcology) fixed TREES and the placed
    // shore plants. Clutter — vass, reeds, waterlilies, the swamp grasses —
    // is a different system with the same defect, and its own constant:
    //
    //   float alt = point.y - this.m_waterLevel;        // m_waterLevel = 27
    //   if (alt >= clutter.m_minAlt && alt <= clutter.m_maxAlt) ...
    //   if (clutter.m_snapToWater) p.y = this.m_waterLevel;
    //
    // Every water-clutter band is written against that 27. At a generated
    // lake surface 60m up, `alt` is ~33 so only dry-land grass qualifies, and
    // a snapToWater lily would be placed 30m underground. Result: water over
    // ordinary meadow grass, no vass, no lilies.
    //
    // The fix is the mutate-and-restore pattern the vegetation density and
    // altitude patches already use: for each 8m patch that sits over our
    // water, point m_waterLevel at THAT water for the duration of the patch
    // build, preserving vanilla's own offset between the sea plane and this
    // constant, then put it back. Every band vanilla tuned then applies to
    // our lakes and rivers unchanged, and patches away from generated water
    // are bit-identical vanilla.
    // Apply is a prefix and Restore a finalizer of FAT's one ClutterSystem.GenerateVegPatch hook (FatVanillaHooks).
    //
    // 0.2.662 (Fire 09-30: "inland water ... grow water clutter at its shores and over its water (lillies and vass)"):
    //  * The spread builder (ClutterSpread, on by default) places a patch frames AFTER this hook has restored
    //    m_waterLevel, so it read 27 and the move never reached a single instance. It now takes WaterLevel and
    //    Surface from here when the patch is queued.
    //  * A patch whose centre is dry but which lies on a shore looks for the water around it (ShoreRings), so the
    //    bank grows vass as well as the water its lilies.
    //  * Surface is the water's own level (diagnostics). The ocean-depth bands keep vanilla's zone-level depth under the
    //    sea, which reads 0 inland (0.2.677: substituting the local depth refused every lily).
    //  * What grows where, from the runtime rules (R47, 10-01; m_waterLevel ~27 against the 30 m sea, so the move keeps
    //    the 3 m gap): instanced_waterlilies alt -1.5..-0.2 → a bed 3.2-4.5 m under the surface; instanced_vass alt
    //    -1..-0.1 → 3.1-4.0 m. Fish1 (SpawnSystem, against 30) wants a bed 1.5-3 m deep. A Vpool pond is shaped for both.
    internal static class ClutterSystem_GenerateVegPatch_WaterEcology
    {
        // ClutterSystem's own m_waterLevel (27, its class default) sits this far under the 30 m sea plane.
        internal const float VanillaClutterGap = 3f;
        private const float SeaSheetGate = 30.3f;
        private const float MinDepth = 0.15f;
        // Probe rings around a dry patch centre (metres) and the points on each.
        private static readonly float[] ShoreRings = { 6f, 12f };
        private const int RingPoints = 8;

        private static float s_saved;
        private static bool s_mutated;

        // The water this patch was moved to, or NaN; read by ClutterSpread while the patch is queued.
        internal static float Surface { get; private set; } = float.NaN;

        internal static void Apply(ClutterSystem __instance, Vector2Int patchID)
        {
            s_mutated = false;
            Surface = float.NaN;
            // No hydrology needed: tool ponds count too (0.2.673), and Wet asks the generated field first.
            if (!FiresVegWaterEcology.Enabled) return;
            var c = __instance.GetVegPatchCenter(patchID);
            if (!TryPatchWater(c, out float lvl)) return;

            s_saved = __instance.m_waterLevel;
            // Keep vanilla's DESIGN gap between the sea plane and this constant (30 - 27 = 3 m), so a band that sits at
            // the shoreline in vanilla sits at ours. 0.2.679 (H7, 10-01): not the live value's gap. Galaxies' tide moves
            // m_waterLevel with the sea (it read 25.15, a 4.85 m gap), and an inland pond has no tide, so the lily band
            // drifted 1.85 m deeper than any pond is cut.
            __instance.m_waterLevel = lvl - VanillaClutterGap;
            Surface = lvl;
            s_mutated = true;
            if (s_logged) return;
            s_logged = true;
            FiresLogger.LogInfo($"[FiresWater] clutter: the grass patch at ({c.x:0}, {c.z:0}) grows against water at y {lvl:0.0} "
                + $"(clutter level {__instance.m_waterLevel:0.0}); first of this session.");
        }

        private static bool s_logged;

        internal static void Restore(ClutterSystem __instance)
        {
            if (!s_mutated) return;
            s_mutated = false;
            Surface = float.NaN;
            __instance.m_waterLevel = s_saved;
        }

        // 0.2.668 (R29a, [lead] 10-01): a client learns its generated water late (the hydrology solve runs on a worker
        // thread), and the grass already built around it keeps the sea-level bands until each patch happens to rebuild.
        // Called every frame from ClutterSpread's LateUpdate postfix: the first frame the water is queryable, the grass
        // around the camera is rebuilt once, so the shore and lake patches grow against it.
        private static bool s_waterWasKnown;

        internal static void RebuildWhenWaterKnown(ClutterSystem cs)
        {
            if (s_waterWasKnown || cs == null || !FiresVegWaterEcology.Enabled || !GenWaterField.SurfaceReady) return;
            s_waterWasKnown = true;
            Camera cam = Utils.GetMainCamera();
            if (cam == null) return;
            Vector3 at = cam.transform.position;
            cs.ResetGrass(at, cs.m_distance + cs.m_grassPatchSize);
            FiresLogger.LogInfo($"[FiresWater] clutter: generated water became known; grass within {cs.m_distance + cs.m_grassPatchSize:0} m "
                + $"of ({at.x:0}, {at.z:0}) rebuilt so it grows against it.");
        }

        // A new world in the same session learns its water again.
        internal static void ForgetWater() => s_waterWasKnown = false;

        // The generated water under the patch centre, else the highest water on the shore rings around it.
        internal static bool TryPatchWater(Vector3 c, out float level)
        {
            if (Wet(c.x, c.z, out level)) return true;
            level = float.MinValue;
            foreach (float r in ShoreRings)
                for (int k = 0; k < RingPoints; k++)
                {
                    float a = k * (2f * Mathf.PI / RingPoints);
                    if (Wet(c.x + Mathf.Cos(a) * r, c.z + Mathf.Sin(a) * r, out float l) && l > level) level = l;
                }
            return level > float.MinValue;
        }

        // FatWaterChecks.TryWaterAt (0.2.673): the generated field, else tool ponds and other FAT water over solid ground.
        private static bool Wet(float x, float z, out float level)
            => VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(x, z, MinDepth, false, out level, out _);
    }
}
