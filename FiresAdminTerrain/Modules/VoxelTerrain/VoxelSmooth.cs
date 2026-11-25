using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Vlevel + Left Alt = a real smooth (0.2.1455, [ghost]; lead 11-17 Voxheim diff row P2, agreed with [worldgen]) ─────────────
    // Before, Alt-smooth levelled the WHOLE disc to one height (halfway from the centre's ground to the disc's mean), so a slope
    // under it turned into a flat shelf. Now, our own design: the ground under the disc is read as a grid of columns once, before the
    // dab; each column moves toward the average of its neighbours' heights (so bumps and pits even out while a slope stays a slope), by
    // `Strength` (VoxelBrushSoft's value, 100 / 50 / 25 %) x a soft falloff to the rim; repeated dabs converge. Each column's move is a
    // small cylinder fill (up) or carve (down) between its surface and its target, like the island / layer reshapes do, so caves
    // under the ground and anything above the reach are left alone. The grid coarsens on a big disc so a dab stays bounded (at most
    // ~400 stamps). Called inside the Vlevel stroke (VoxelWorld.VToolScope, FatUndo's player-edit scope), so it undoes as one step.
    internal static class VoxelSmooth
    {
        private const int MaxPerSide = 20;          // grid columns across the disc's diameter, at most
        private const float SurfaceReach = 6f;      // how far up / down from the hit a column's surface is looked for
        private const float NeighbourStep = 1.5f;   // neighbour ring distance, in grid steps
        private const float MinMove = 0.05f;        // a column closer than this to its target is left
        private const float BedOverlap = 0.5f;

        private static float[] s_heights = new float[0];
        private static bool[] s_have = new bool[0];

        /// <summary>One smooth dab; returns its Debug line.</summary>
        internal static string Dab(Vector3 at, float radius, float strength)
        {
            radius = Mathf.Max(radius, 1f);
            int n = Mathf.Clamp(Mathf.CeilToInt(radius * 2f), 3, MaxPerSide);   // columns across
            float step = radius * 2f / n;
            int count = n * n;
            if (s_heights.Length < count) { s_heights = new float[count]; s_have = new bool[count]; }
            float x0 = at.x - radius + step * 0.5f, z0 = at.z - radius + step * 0.5f;

            // 1) the surface of every column, read before anything moves
            int read = 0;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int k = i + j * n;
                    float x = x0 + i * step, z = z0 + j * step;
                    s_have[k] = InDisc(x, z, at, radius) && VoxelWorld.TryFindSurfaceY(x, z, at.y, SurfaceReach, out s_heights[k]);
                    if (s_have[k]) read++;
                }
            if (read < 3) return $"[FiresVoxel] smooth at ({at.x:0.0},{at.z:0.0}) r {radius:0.0}: too little ground under the disc ({read} column(s)), nothing done";

            // 2) each column toward its neighbours' mean, by strength x falloff
            int filled = 0, cut = 0, still = 0;
            float stamp = step * 0.75f, worst = 0f;
            int reach = Mathf.Max(1, Mathf.RoundToInt(NeighbourStep));
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int k = i + j * n;
                    if (!s_have[k]) continue;
                    float sum = 0f; int cnt = 0;
                    for (int dj = -reach; dj <= reach; dj++)
                        for (int di = -reach; di <= reach; di++)
                        {
                            int ii = i + di, jj = j + dj;
                            if (ii < 0 || jj < 0 || ii >= n || jj >= n) continue;
                            int kk = ii + jj * n;
                            if (!s_have[kk]) continue;
                            sum += s_heights[kk]; cnt++;
                        }
                    if (cnt < 3) { still++; continue; }
                    float x = x0 + i * step, z = z0 + j * step;
                    float d = Mathf.Sqrt((x - at.x) * (x - at.x) + (z - at.z) * (z - at.z)) / radius;
                    float w = 0.5f * Mathf.Clamp01(strength) * Mathf.SmoothStep(0f, 1f, 1f - Mathf.Clamp01(d));   // 100 % = half way per dab
                    float surface = s_heights[k];
                    float target = Mathf.Lerp(surface, sum / cnt, w);
                    if (Mathf.Abs(target - surface) < MinMove) { still++; continue; }
                    bool fill = target > surface;
                    float lo = fill ? surface - BedOverlap : target, hi = fill ? target : surface + BedOverlap;
                    VoxelWorld.ApplyBrush(new Vector3(x, (lo + hi) * 0.5f, z), stamp, hi - lo, VoxelBrushShape.Cylinder, fill ? VoxelBrushMode.Fill : VoxelBrushMode.Carve);
                    if (fill) filled++; else cut++;
                    worst = Mathf.Max(worst, Mathf.Abs(target - surface));
                }
            return $"[FiresVoxel] smooth at ({at.x:0.0},{at.y:0.0},{at.z:0.0}) r {radius:0.0}, strength {strength * 100f:0} %: {read} column(s) on a {step:0.0} m grid, "
                 + $"{filled} raised, {cut} lowered, {still} already smooth (largest move {worst:0.00} m)";
        }

        private static bool InDisc(float x, float z, Vector3 at, float r) => (x - at.x) * (x - at.x) + (z - at.z) * (z - at.z) <= r * r;
    }
}
