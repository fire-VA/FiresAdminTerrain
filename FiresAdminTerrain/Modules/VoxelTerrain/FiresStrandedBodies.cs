using System.Globalization;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    public static partial class FiresWorldGenBase
    {
        // 0.2.849 (Fire 10-07, 125-127.webp: "a bump of water and rapids forming in our river for no reason"; the lead's
        // fat_water_line on SinkTestGen8: Pond #119 at 74.64 over river runs #591 73.18 / #594 73.36 / #52 71.87, ground under it
        // 68.45-69.09): the bake levels every still body (FiresWaterConform) BEFORE FatSinkFallCuts truncates rivers and
        // InciseRivers carves their channels, so a body a carved river now runs through can stand metres over that channel - a
        // mound of water with no banks. At every load (old and new bakes alike), a body crossed by a river run lower than it by
        // more than StrandSlack is lowered to the LOWEST such run level inside it (0.2.849b: flush with its outflow, so the old footprint never rises downstream),
        // and its water cells follow (a cell whose ground is above the new level goes dry). One line lists every body clamped.
        private const float StrandSlack = 0.3f, BodyCellMatch = 0.05f, StrandShare = 0.25f;
        private const int DetectStride = 2;
        private const float SeaGate = 30.3f;

        private static void ClampStrandedBodies(int n, float half)
        {
            var plan = _plan;
            var water = _waterLevel;
            var ground = _grid;
            if (plan == null || water == null || ground == null || n < 2 || water.Length != n * n) return;
            float cell = (2f * half) / (n - 1);
            var inv = CultureInfo.InvariantCulture;
            var report = new StringBuilder();
            int clamped = 0, cellsLowered = 0, cellsDried = 0;
            foreach (var b in plan.Bodies)
            {
                if (b == null || b.Mask == null || b.W <= 0 || b.H <= 0 || b.Mask.Length < b.W * b.H) continue;
                float runTop = float.PositiveInfinity;   // 0.2.849b: the LOWEST run crossing it (its outflow), not the highest
                int inside = 0, under = 0;
                for (int dz = 0; dz < b.H; dz += DetectStride)
                    for (int dx = 0; dx < b.W; dx += DetectStride)
                    {
                        if (b.Mask[dz * b.W + dx] == 0) continue;
                        inside++;
                        float wx = (b.Gx0 + dx) * cell - half, wz = (b.Gz0 + dz) * cell - half;
                        if (!plan.TryGetRiverAt(wx, wz, 0f, out float runLevel, out _, out _) || runLevel >= b.Level - StrandSlack) continue;
                        if (runLevel <= SeaGate) continue;   // 0.2.849b: a sea-bound run reports 0 (Pond #120 went 70.88 -> 0.00)
                        under++;
                        if (runLevel < runTop) runTop = runLevel;
                    }
                // A lake with an ordinary outlet has its run dropping under its level only at its edge: a stranded body has the
                // lower river across a real share of it.
                if (float.IsPositiveInfinity(runTop) || under < inside * StrandShare) continue;
                float old = b.Level;
                for (int dz = 0; dz < b.H; dz++)
                    for (int dx = 0; dx < b.W; dx++)
                    {
                        if (b.Mask[dz * b.W + dx] == 0) continue;
                        int gx = b.Gx0 + dx, gz = b.Gz0 + dz;
                        if (gx < 0 || gz < 0 || gx >= n || gz >= n) continue;
                        int i = gz * n + gx;
                        if (Mathf.Abs(water[i] - old) > BodyCellMatch) continue;   // a cell this body does not own (a run or another body)
                        if (ground[i] >= runTop) { water[i] = ground[i]; cellsDried++; }
                        else { water[i] = runTop; cellsLowered++; }
                    }
                b.Level = runTop;
                clamped++;
                if (clamped <= 20) report.Append(string.Format(inv, "\n  {0} #{1} at ({2:0},{3:0}): {4:0.00} -> {5:0.00}",
                    b.Kind, b.Id, (b.Gx0 + b.W * 0.5f) * cell - half, (b.Gz0 + b.H * 0.5f) * cell - half, old, runTop));
            }
            FiresLogger.LogInfo(string.Format(inv, "[FiresWorldGen] stranded bodies: {0} body(ies) lowered to the river run they lie in ({1} cell(s) lowered, {2} dried){3}{4}",
                clamped, cellsLowered, cellsDried, report, clamped > 20 ? $"\n  … and {clamped - 20} more" : ""));
        }
    }
}
