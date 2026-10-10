using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Round-78 — shared reflective access to vanilla LiquidVolume's private
    // grid, used by the spring emitter, the ruler-local delete, and the mop
    // tool. Centralizes the AccessTools field refs + the cell<->world math
    // (the volume's grid: cells at m_scale spacing, centred on its transform) so
    // the three callers can't drift apart.
    //
    // Grid model: (m_width+1)² cells at m_scale spacing, centered on the
    // volume transform. m_depths = water column per cell; m_heights = raycast
    // terrain height (relative to transform.y) per cell, refreshed ~1/s by
    // the volume's own UpdateHeights. Surface world Y of a cell =
    // transform.y + m_heights[i] + m_depths[i].
    internal static class FiresLiquidGridOps
    {
        internal static readonly AccessTools.FieldRef<LiquidVolume, List<float>> Depths =
            AccessTools.FieldRefAccess<LiquidVolume, List<float>>("m_depths");
        internal static readonly AccessTools.FieldRef<LiquidVolume, List<float>> Heights =
            AccessTools.FieldRefAccess<LiquidVolume, List<float>>("m_heights");
        internal static readonly AccessTools.FieldRef<LiquidVolume, object> MeshDataLock =
            AccessTools.FieldRefAccess<LiquidVolume, object>("m_meshDataLock");
        internal static readonly AccessTools.FieldRef<LiquidVolume, bool> Dirty =
            AccessTools.FieldRefAccess<LiquidVolume, bool>("m_dirty");
        internal static readonly AccessTools.FieldRef<LiquidVolume, bool> NeedsSaving =
            AccessTools.FieldRefAccess<LiquidVolume, bool>("m_needsSaving");
        internal static readonly AccessTools.FieldRef<LiquidVolume, bool> HaveHeights =
            AccessTools.FieldRefAccess<LiquidVolume, bool>("m_haveHeights");

        // Round-79 — spring ZDO keys (source emit rate + fill level).
        internal const string ZDO_SPRING_RATE = "va_spring_rate";
        internal const string ZDO_SPRING_FILL = "va_spring_fill";
        internal const string ZDO_SPRING_BANKOFFSET = "va_spring_bankoff";
        internal const string ZDO_SPRING_RADIUS = "va_spring_radius";
        internal const string ZDO_SPRING_BLOCKED = "va_spring_blocked";  // dam/barrier cell bitmask
        internal const string ZDO_SPRING_FROZEN = "va_spring_frozen";    // 1 = sim frozen (mesh held)
        internal const string ZDO_SPRING_STILL = "va_spring_still";      // 1 = still water: dead-level cap at the source level (lakes/ponds), not bank-following
        internal const string ZDO_SPRING_SOURCES = "va_spring_sources";  // round-85 — extra merged source disks (x,z,r,fillY blob)

        // Round-79 — flat-ground spill cull, direction-independent. A wet
        // cell is culled when it is BOTH thin (depth < shallowCull) AND the
        // terrain around it is essentially flat (local relief max-min <
        // bankRelief), i.e. it is NOT sitting in a cut/channel. This is the
        // "needs a real cut depth before it holds water" rule: water in a
        // banked channel (high relief) is kept at any depth; a thin sheet on
        // the open flat path (low relief) is zeroed, killing the spill AND
        // the z-fighting. Deep water is always kept, so filled basins don't
        // get holes punched in their middle. Caller holds the mesh lock.
        // Returns m³ culled.
        internal static float CullFlatSpill(List<float> depths, List<float> heights,
            int num, int centerIdxX, int centerIdxZ, int srcHalf,
            float shallowCull, float bankRelief)
        {
            const int SCAN = 3;
            float culled = 0f;
            for (int z = 1; z < num - 1; z++)
            {
                for (int x = 1; x < num - 1; x++)
                {
                    int i = z * num + x;
                    float d = depths[i];
                    if (d <= 0f || d >= shallowCull) continue;
                    // Never cull the emitter source square.
                    if (Mathf.Abs(x - centerIdxX) <= srcHalf && Mathf.Abs(z - centerIdxZ) <= srcHalf) continue;

                    float hMin = float.MaxValue, hMax = float.MinValue;
                    for (int dz = -SCAN; dz <= SCAN; dz++)
                    {
                        int nz = z + dz;
                        if (nz < 0 || nz >= num) continue;
                        for (int dx = -SCAN; dx <= SCAN; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= num) continue;
                            float h = heights[nz * num + nx];
                            if (h < hMin) hMin = h;
                            if (h > hMax) hMax = h;
                        }
                    }
                    if (hMax - hMin < bankRelief) { culled += d; depths[i] = 0f; }
                }
            }
            return culled;
        }

        // World XZ of grid cell (x,z). Mirrors LiquidVolume.CalcVertex's
        // base offset: -width*scale*0.5 + index*scale, plus transform.
        internal static Vector2 CellWorldXZ(LiquidVolume lv, int x, int z)
        {
            float half = lv.m_width * lv.m_scale * 0.5f;
            Vector3 p = lv.transform.position;
            return new Vector2(p.x - half + x * lv.m_scale, p.z - half + z * lv.m_scale);
        }

        // Reduce (fraction 1 = fully remove) water depth in every cell within
        // `radius` of `centerXZ`. Returns total m³ removed. Owner-side only —
        // caller must ClaimOwnership first. Marks the volume dirty + saving.
        // Sets `anyLeft` false if the whole volume is now dry (caller destroys).
        internal static float RemoveInRadius(LiquidVolume lv, Vector2 centerXZ,
            float radius, float fraction, out bool anyLeft)
        {
            anyLeft = false;
            float removed = 0f;
            var depths = Depths(lv);
            var mutex = MeshDataLock(lv);
            if (depths == null || mutex == null) { anyLeft = true; return 0f; }

            int num = lv.m_width + 1;
            float r2 = radius * radius;
            fraction = Mathf.Clamp01(fraction);

            System.Threading.Monitor.Enter(mutex);
            try
            {
                for (int z = 0; z < num; z++)
                {
                    for (int x = 0; x < num; x++)
                    {
                        int i = z * num + x;
                        float d = depths[i];
                        if (d > 0f)
                        {
                            Vector2 w = CellWorldXZ(lv, x, z);
                            float dx = w.x - centerXZ.x, dz = w.y - centerXZ.y;
                            if (dx * dx + dz * dz <= r2)
                            {
                                float take = d * fraction;
                                depths[i] = d - take;
                                removed += take;
                                d = depths[i];
                            }
                        }
                        if (d > 0.001f) anyLeft = true;
                    }
                }
                Dirty(lv) = true;
                NeedsSaving(lv) = true;
            }
            finally
            {
                System.Threading.Monitor.Exit(mutex);
            }
            return removed;
        }
    }
}
