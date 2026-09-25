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
    internal static class ClutterSystem_GenerateVegPatch_WaterEcology
    {
        private const float SeaPlane = 30f;

        private static float s_saved;
        private static bool s_mutated;

        internal static void Apply(ClutterSystem __instance, Vector2Int patchID)
        {
            s_mutated = false;
            if (!FiresVegWaterEcology.Enabled || !GenWaterField.Ready) return;
            var c = __instance.GetVegPatchCenter(patchID);
            if (!GenWaterField.TryWaterSurfaceSmooth(c.x, c.z, out float lvl, out float dep)
                || dep < 0.15f || lvl <= 30.3f) return;

            s_saved = __instance.m_waterLevel;
            // Keep vanilla's gap between the sea plane and this constant, so a
            // band that sits at the shoreline in vanilla sits at ours.
            __instance.m_waterLevel = lvl - (SeaPlane - s_saved);
            s_mutated = true;
        }

        internal static void Restore(ClutterSystem __instance)
        {
            if (!s_mutated) return;
            s_mutated = false;
            __instance.m_waterLevel = s_saved;
        }
    }
}
