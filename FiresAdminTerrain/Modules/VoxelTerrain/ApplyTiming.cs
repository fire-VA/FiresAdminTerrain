using System.Diagnostics;
using System.Globalization;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1167 (the lead: "[FiresVoxelPipe] frame budget 4 ms: worst frame 30.6 ms (apply 28.5 ...)"): which part of ONE chunk
    // apply (VoxelChunkView.ApplyBuffers) costs the ms. Parts: layer paint (VoxelLayerPaint.Render), G refinement
    // (VoxelGTessellation.Apply), normal guard, vertex attribute upload (SetVertices..SetColors), triangles (SetTriangles), rest
    // (material, collidable check, gen water, visibility). Debug only: a 10 s line with the sums and the worst apply's breakdown,
    // and one line for any single apply over 8 ms (at most one a second).
    internal static class ApplyTiming
    {
        private static readonly double Ms = 1000.0 / Stopwatch.Frequency;
        private static double s_paint, s_refine, s_guard, s_upload, s_tris, s_rest, s_worst;
        private static int s_n, s_worstVerts;
        private static string s_worstLine = "-";
        private static float s_reportAt, s_slowAt;

        internal static void Note(Vector3Int key, int verts, long a0, long a1, long a2, long a3, long a4, long a5, long a6)
        {
            if (!FiresLogger.DebugEnabled) return;
            double paint = (a1 - a0) * Ms, refine = (a2 - a1) * Ms, guard = (a3 - a2) * Ms, upload = (a4 - a3) * Ms, tris = (a5 - a4) * Ms, rest = (a6 - a5) * Ms;
            double total = (a6 - a0) * Ms;
            s_paint += paint; s_refine += refine; s_guard += guard; s_upload += upload; s_tris += tris; s_rest += rest; s_n++;
            if (total > s_worst || s_worstLine == "-")
            {
                s_worst = total; s_worstVerts = verts;
                s_worstLine = string.Format(CultureInfo.InvariantCulture, "chunk {0} {1} verts: paint {2:0.00}, refine {3:0.00}, guard {4:0.00}, upload {5:0.00}, tris {6:0.00}, rest {7:0.00} ms",
                    key, verts, paint, refine, guard, upload, tris, rest);
            }
            float now = Time.realtimeSinceStartup;
            if (total > 8.0 && now >= s_slowAt)
            {
                s_slowAt = now + 1f;
                FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture, "[FiresVoxelPipe] slow apply {0:0.0} ms: chunk {1} {2} verts (paint {3:0.00}, refine {4:0.00}, guard {5:0.00}, upload {6:0.00}, tris {7:0.00}, rest {8:0.00} ms) (0.2.1167)",
                    total, key, verts, paint, refine, guard, upload, tris, rest));
            }
            if (now < s_reportAt) return;
            s_reportAt = now + 10f;
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[FiresVoxelPipe] apply parts 10 s ({0} apply(s), ms total): paint {1:0.0}, refine {2:0.0}, guard {3:0.0}, upload {4:0.0}, tris {5:0.0}, rest {6:0.0}; worst apply {7:0.0} ms = {8} (0.2.1167)",
                s_n, s_paint, s_refine, s_guard, s_upload, s_tris, s_rest, s_worst, s_worstLine));
            s_paint = s_refine = s_guard = s_upload = s_tris = s_rest = s_worst = 0; s_n = 0; s_worstLine = "-";
        }
    }
}
