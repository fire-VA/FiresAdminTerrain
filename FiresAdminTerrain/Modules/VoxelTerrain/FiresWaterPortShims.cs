using UnityEngine;
using System.Collections.Generic;

namespace VerdantsAscent.Terrain.Voxel
{
    // Declarations the ported sandbox water subsystem expects from its own base
    // file. They are pulled out here rather than merged into FiresWorldGenBase
    // so the port stays legible as a port.
    public static partial class FiresWorldGenBase
    {
        private static byte[] _labelS;
        private static int _labelN;
        private static readonly HashSet<long> s_marshZones = new HashSet<long>();
        public const byte WC_WATERFALL = 9;   // river/stream cell on a steep drop → cascade

        // Runoff model constants (sandbox FiresClimate lineage).
        private const float OROGRAPHIC_PER_M = 0.004f;
        private const float EVAP_REF = 0.45f;
        private const float EVAP_REF_C = 15f;
        private const float RUNOFF_MIN = 0.02f;
        private const float RUNOFF_SCALE = 1.7f;
        // RIVER banks — hold a sloped river's ribbon below terrain WITHOUT dropping it (dropping a
        // sloped reach notches the flow). For each wet river cell, raise any dry lower neighbour to
        // (ribbon + freeboard), capped SMALL (natural incision, not a lake wall); a bigger gap is a
        // genuine cliff/waterfall edge and is left. A few passes so a raised bank's neighbours settle.
        private static void ContainRiversBelowBanks(float[] w, float[] g, byte[] m, int n)
        {
            const float FREEBOARD = 0.4f, BANK_CAP = 3.0f;
            int raised = 0;
            for (int it = 0; it < 6; it++)
            {
                int fixedN = 0;
                for (int i = 0; i < n * n; i++)
                {
                    if ((m[i] != 1 && m[i] != 3) || w[i] <= g[i] + 0.01f) continue;
                    float lvl = w[i];
                    int x = i % n, z = i / n;
                    B(i - 1, x > 0); B(i + 1, x + 1 < n); B(i - n, z > 0); B(i + n, z + 1 < n);
                    void B(int j, bool ok)
                    {
                        if (!ok || w[j] > g[j] + 0.01f) return;         // wet neighbour → continuous
                        if (g[j] >= lvl + FREEBOARD - 0.01f) return;    // already a proud bank
                        // ⚠ DO NOT BANK THE CELL WHERE THIS RIVER MEETS ANOTHER SHEET. (user, 2026-09-02:
                        // "bodies of water drag a bank where they should be connecting".) This raise asks only
                        // whether the neighbour is dry, so at a river↔pond mouth — dry ridge, river on one
                        // side, pond on the other — it dutifully builds a levee across the junction and the two
                        // waters never meet. The matching rule and the measurement are in FiresWaterConform's
                        // neck note. If the cell OPPOSITE this river carries a sheet within NECK_TOL of it,
                        // this is a junction and the bank is a dam; leave it for the neck rule to open.
                        const float NECK_TOL = 0.75f;
                        int back = j + (j - i);                          // the cell on the far side of j from i
                        if (back >= 0 && back < n * n && w[back] > g[back] + 0.01f
                            && Mathf.Abs(w[back] - lvl) <= NECK_TOL) return;
                        if (lvl + FREEBOARD - g[j] <= BANK_CAP)
                        { g[j] = lvl + FREEBOARD; raised++; fixedN++; } // small natural bank
                        // else: genuine cliff / waterfall edge — leave it
                    }
                }
                if (fixedN == 0) break;
            }
            FiresLogger.LogInfo($"[FiresWorldGen] river banks: {raised:N0} bank cells raised to keep the ribbon below terrain (cap {BANK_CAP:0}m; steeper edges left as falls).");
        }
    }

    // The sandbox's player-facing water-draw layer. FAT has no water-edit tools,
    // so the passthrough is permanent: TryGetWaterSurfaceSmooth is byte-identical
    // to the pure generated surface, which is exactly what Active == false means
    // in the sandbox too.
    internal static class FiresWaterEdits
    {
        public static bool Active => false;
        public static bool TryGet(float wx, float wz, out float level, out bool removed)
        { level = 0f; removed = false; return false; }
    }
}

namespace VerdantsAscent.Terrain.Voxel
{
    public static partial class FiresWorldGenBase
    {
        // GLSL smoothstep semantics (sandbox FiresTerrain3D.Smooth01): Unity's
        // Mathf.SmoothStep diverges from it, and the marsh mound gate was authored
        // against the GLSL one — the studio render is what that gate always showed.
        private static float Smooth01Glsl(float a, float b, float x)
        {
            float t = UnityEngine.Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
