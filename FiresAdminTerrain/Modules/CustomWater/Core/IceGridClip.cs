using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1313 (Fire: "visually our ice mesh shows right here but physically there is a hole in the collision mesh - the visual and
    // collision should match"): the ice COLLIDER of one 1 m sheet cell, cut from the SAME column grid the state texture is filled
    // from (WaterStateField.BuildGrid). The shader samples that texture bilinearly with texel centres on the column centres, so a
    // 1 m cell spans four texel quads; the cell is split into its four 0.5 m sub-cells, whose corner values are exact bilinear values
    // (cell corner = mean of 4 texels, edge middle = mean of 2, cell centre = the texel), and each sub-cell is clipped at
    // _FiresIceSolid by PlacedWaterSolids.ClipCell (the one marching-squares implementation). Used by generated water
    // (VoxelChunkView.BuildSheet) and placed water (PlacedWaterSolids.Build). Main thread (shared scratch lists).
    internal static class IceGridClip
    {
        private const int Px = WaterStateField.GridPx;

        // Exact bilinear value at sub-node (si, sj) of local cell (cx, cz) (cell 0..31), si/sj in 0..2 (0 = low edge, 1 = middle, 2 = high).
        private static float Node(WaterStateField.Grid g, int cx, int cz, int si, int sj)
        {
            // texel index of column c is c + 1; the cell's own column is texel (cx + 1, cz + 1)
            int ti = cx + 1, tj = cz + 1;
            float a, b;
            if (si == 1) { a = Row(g, ti, tj, sj); return a; }
            int ti0 = si == 0 ? ti - 1 : ti;
            a = Row(g, ti0, tj, sj); b = Row(g, ti0 + 1, tj, sj);
            return 0.5f * (a + b);
        }

        // value along z at texel column ti for sub-row sj
        private static float Row(WaterStateField.Grid g, int ti, int tj, int sj)
        {
            if (sj == 1) return g.Ice[ti + Px * tj];
            int tj0 = sj == 0 ? tj - 1 : tj;
            return 0.5f * (g.Ice[ti + Px * tj0] + g.Ice[ti + Px * (tj0 + 1)]);
        }

        private static readonly float[] s_node = new float[9];
        private static readonly float[] s_h = new float[9];

        // Clips one cell into v / t. h00..h11 = the cell's corner heights (the drawn sheet's). Counts the four sub-cell centres for the
        // proof: visual = bilinear >= iso there, collider = inside a polygon this call emitted.
        internal static void ClipCell(WaterStateField.Grid g, int cx, int cz, float wx, float wz, float h00, float h10, float h01, float h11,
                                      float iso, List<Vector3> v, List<int> t, List<Vector3> n, List<Vector2> uv,
                                      ref int visual, ref int collider, ref int visualOnly, ref int colliderOnly, ref string first)
        {
            for (int sj = 0; sj < 3; sj++)
                for (int si = 0; si < 3; si++)
                {
                    s_node[si + 3 * sj] = Node(g, cx, cz, si, sj);
                    float fx = si * 0.5f, fz = sj * 0.5f;
                    s_h[si + 3 * sj] = Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
                }
            for (int qj = 0; qj < 2; qj++)
                for (int qi = 0; qi < 2; qi++)
                {
                    int a = qi + 3 * qj;
                    float f00 = s_node[a], f10 = s_node[a + 1], f01 = s_node[a + 3], f11 = s_node[a + 4];
                    int vb = v.Count, tb = t.Count;
                    float sx = wx + qi * 0.5f, sz = wz + qj * 0.5f;
                    PlacedWaterSolids.ClipCell(sx, sz, s_h[a], s_h[a + 1], s_h[a + 3], s_h[a + 4], f00, f10, f01, f11, iso, v, t, 0.5f);
                    if (n != null) for (int k = vb; k < v.Count; k++) n.Add(Vector3.up);
                    if (uv != null) for (int k = vb; k < v.Count; k++) uv.Add(new Vector2(v[k].x / 64f, v[k].z / 64f));
                    // proof at the sub-cell centre
                    float px = sx + 0.25f, pz = sz + 0.25f;
                    float bil = 0.25f * (f00 + f10 + f01 + f11);   // exact: bilinear at the centre of a bilinear patch
                    bool vis = bil >= iso;
                    bool col = Inside(v, t, tb, px, pz);
                    if (vis) visual++;
                    if (col) collider++;
                    if (vis && !col) { visualOnly++; if (first == null) first = $", first mismatch ({px:0.00},{pz:0.00}) R {bil:0.00} visual-only"; }
                    if (col && !vis) { colliderOnly++; if (first == null) first = $", first mismatch ({px:0.00},{pz:0.00}) R {bil:0.00} collider-only"; }
                }
        }

        private static bool Inside(List<Vector3> v, List<int> t, int tb, float x, float z)
        {
            for (int k = tb; k + 2 < t.Count; k += 3)
            {
                Vector3 a = v[t[k]], b = v[t[k + 1]], c = v[t[k + 2]];
                float d1 = (x - b.x) * (a.z - b.z) - (a.x - b.x) * (z - b.z);
                float d2 = (x - c.x) * (b.z - c.z) - (b.x - c.x) * (z - c.z);
                float d3 = (x - a.x) * (c.z - a.z) - (c.x - a.x) * (z - a.z);
                bool neg = d1 < -1e-6f || d2 < -1e-6f || d3 < -1e-6f, pos = d1 > 1e-6f || d2 > 1e-6f || d3 > 1e-6f;
                if (!(neg && pos)) return true;
            }
            return false;
        }
    }
}
