using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Water survey — flood-fills the generated water field around the player,
    // groups it into distinct BODIES, classifies each by size and shape, and
    // reports the numbers that actually decide whether the leveling is right:
    // the level range across the body (a settled body should be near-flat
    // across, only falling along its length) and the worst STAND — how far the
    // surface sits above the ground beside it, which is what reads as water
    // hovering over its bank.
    internal static class VoxelWaterSurvey
    {
        private struct Cell { public int X, Z; }

        private sealed class Body
        {
            public int Cells;
            public float LvlMin = float.MaxValue, LvlMax = float.MinValue;
            public float DepthMax;
            // Lowest REAL bank around the body: the minimum ground among dry
            // perimeter cells that actually rise above the water. Freeboard is
            // (lowestBank - level) — how far the surface sits UNDER the bank it
            // would spill over first. Small positive is the goal.
            public float LowestBank = float.MaxValue;
            public float LowestBankLvl;
            public float LowestBankX, LowestBankZ;
            // Dry cells sitting BELOW the water level next to wet ones. These are
            // holes in the wet field, not banks — counting them as "stand" is
            // what made the old report claim water over banks that aren't there.
            public int Gaps;
            public float WorstGap;
            public float WorstGapX, WorstGapZ;
            public int MinX = int.MaxValue, MaxX = int.MinValue, MinZ = int.MaxValue, MaxZ = int.MinValue;
            public float CenX, CenZ;
        }

        // Same survey the command runs, straight to the log. Called on the
        // water diagnostic interval so a test session leaves a readable trail
        // of every body's freeboard and gap count without anyone typing
        // va_water_scan at the spot.
        public static void LogAuto()
        {
            if (Player.m_localPlayer == null || !GenWaterField.Ready) return;
            foreach (var line in Run(160f, 4f))
                FiresLogger.LogInfo("[FiresVoxelWater] " + line);
        }

        // ── WATER SEAM SCAN ──────────────────────────────────────────────
        // Every zone builds its own water field over a G×G lattice with a wide
        // apron, so adjacent zones OVERLAP heavily and both compute a value for
        // the same world positions. If those two answers differ, that is a seam
        // — by definition, before any meshing is involved.
        //
        // Two failure kinds, and they are not the same bug:
        //   * WET/DRY  — one zone calls a shared cell water, the other calls it
        //     dry. This is what tears a visible slit through the sheet: one
        //     side's mesh ends and the other's never begins.
        //   * LEVEL    — both agree it is wet but disagree on the surface Y, so
        //     the sheet steps at the border.
        //
        // A pass here means the two fields are identical over their whole
        // overlap, which is the only real proof the apron covers the passes.
        public static List<string> SeamScan()
        {
            var outLines = new List<string>();
            if (!GenWaterField.Ready)            {
                outLines.Add("va_water_seamscan: no generated water field to audit.");
                return outLines;
            }

            var zones = VoxelGenWaterMesh.FieldZones();
            if (zones.Count == 0)
            {
                outLines.Add("va_water_seamscan: no water fields built yet.");
                return outLines;
            }

            int G = VoxelGenWaterMesh.FieldG;
            float step = VoxelGenWaterMesh.FieldStep;
            float span = (G - 1) * step;

            int pairs = 0, compared = 0, wetDry = 0, levelOff = 0;
            float worst = 0f, worstX = 0f, worstZ = 0f;
            float wdX = 0f, wdZ = 0f;
            bool haveWd = false;

            var dirs = new[] { new Vector2s(1, 0), new Vector2s(0, 1) };
            foreach (var a in zones)
            {
                var fa = VoxelGenWaterMesh.GetField(a);
                if (fa == null) continue;
                foreach (var d in dirs)
                {
                    var b = new Vector2s(a.x + d.x, a.y + d.y);
                    var fb = VoxelGenWaterMesh.GetField(b);
                    if (fb == null) continue;
                    pairs++;

                    VoxelGenWaterMesh.FieldOrigin(a, out float ax, out float az);
                    VoxelGenWaterMesh.FieldOrigin(b, out float bx, out float bz);
                    float minX = Mathf.Max(ax, bx), maxX = Mathf.Min(ax + span, bx + span);
                    float minZ = Mathf.Max(az, bz), maxZ = Mathf.Min(az + span, bz + span);

                    for (float pz = minZ; pz <= maxZ + 0.001f; pz += step)
                    {
                        int azi = Mathf.RoundToInt((pz - az) / step);
                        int bzi = Mathf.RoundToInt((pz - bz) / step);
                        if (azi < 0 || azi >= G || bzi < 0 || bzi >= G) continue;
                        for (float px = minX; px <= maxX + 0.001f; px += step)
                        {
                            int axi = Mathf.RoundToInt((px - ax) / step);
                            int bxi = Mathf.RoundToInt((px - bx) / step);
                            if (axi < 0 || axi >= G || bxi < 0 || bxi >= G) continue;

                            float va = fa[azi * G + axi];
                            float vb = fb[bzi * G + bxi];
                            compared++;
                            bool wa = va > 0f, wb = vb > 0f;
                            if (wa != wb)
                            {
                                wetDry++;
                                if (!haveWd) { wdX = px; wdZ = pz; haveWd = true; }
                                continue;
                            }
                            if (!wa) continue;
                            float delta = Mathf.Abs(va - vb);
                            if (delta > 0.01f)
                            {
                                levelOff++;
                                if (delta > worst) { worst = delta; worstX = px; worstZ = pz; }
                            }
                        }
                    }
                }
            }

            outLines.Add($"va_water_seamscan: {zones.Count} field(s), {pairs} adjacent pair(s), {compared} shared cell(s) compared.");
            if (wetDry == 0 && levelOff == 0)
            {
                outLines.Add("  CLEAN — every shared cell agrees. The apron covers the passes.");
                return outLines;
            }
            if (wetDry > 0)
                outLines.Add($"  ⚠ WET/DRY disagreement on {wetDry} cell(s) — a slit through the sheet. First at ({wdX:0},{wdZ:0}).");
            if (levelOff > 0)
                outLines.Add($"  ⚠ LEVEL disagreement on {levelOff} cell(s) — the sheet steps. Worst {worst:0.00}m at ({worstX:0},{worstZ:0}).");
            outLines.Add("  Both mean a pass reads further than APRON covers; the reach table in VoxelGenWaterMesh is the thing to re-add.");
            return outLines;
        }

        public static List<string> Run(float radius, float step)
        {
            var outLines = new List<string>();
            var player = Player.m_localPlayer;
            if (player == null) { outLines.Add("va_water_scan: no local player."); return outLines; }
            if (!GenWaterField.Ready)            {
                outLines.Add("va_water_scan: no generated water field (hydrology not ready, or Terrain Mode/Hydrology off).");
                return outLines;
            }

            Vector3 p = player.transform.position;
            int n = Mathf.Max(4, Mathf.CeilToInt((radius * 2f) / step));
            float ox = p.x - radius, oz = p.z - radius;

            var lvl = new float[n * n];
            var gnd = new float[n * n];
            var wet = new bool[n * n];
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    int i = z * n + x;
                    float wx = ox + x * step, wz = oz + z * step;
                    wet[i] = VoxelGenWaterMesh.WetAt(wx, wz, out float s);
                    lvl[i] = s;
                    gnd[i] = Heightmap.GetHeight(new Vector3(wx + 0.013f, 0f, wz + 0.013f), out float g)
                        ? g : float.MaxValue;
                }
            }

            // Flood-fill connected wet cells into bodies.
            var seen = new bool[n * n];
            var bodies = new List<Body>();
            var stack = new Stack<Cell>();
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    int i0 = z * n + x;
                    if (!wet[i0] || seen[i0]) continue;
                    var b = new Body();
                    stack.Clear();
                    stack.Push(new Cell { X = x, Z = z });
                    seen[i0] = true;
                    while (stack.Count > 0)
                    {
                        var c = stack.Pop();
                        int i = c.Z * n + c.X;
                        b.Cells++;
                        b.CenX += ox + c.X * step; b.CenZ += oz + c.Z * step;
                        if (lvl[i] < b.LvlMin) b.LvlMin = lvl[i];
                        if (lvl[i] > b.LvlMax) b.LvlMax = lvl[i];
                        if (c.X < b.MinX) b.MinX = c.X; if (c.X > b.MaxX) b.MaxX = c.X;
                        if (c.Z < b.MinZ) b.MinZ = c.Z; if (c.Z > b.MaxZ) b.MaxZ = c.Z;
                        if (gnd[i] != float.MaxValue)
                        {
                            float d = lvl[i] - gnd[i];
                            if (d > b.DepthMax) b.DepthMax = d;
                        }
                        // Stand = how far the surface sits above the DRY ground
                        // touching this cell. That is the hovering-over-the-bank
                        // number; depth over its own bed is not.
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = c.X + dx, nz = c.Z + dz;
                                if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                                int j = nz * n + nx;
                                if (wet[j])
                                {
                                    if (!seen[j]) { seen[j] = true; stack.Push(new Cell { X = nx, Z = nz }); }
                                    continue;
                                }
                                if (gnd[j] == float.MaxValue) continue;
                                if (gnd[j] >= lvl[i])
                                {
                                    // A real bank: dry ground standing above the
                                    // surface. The LOWEST one is what the water
                                    // would spill over first.
                                    if (gnd[j] < b.LowestBank)
                                    {
                                        b.LowestBank = gnd[j];
                                        b.LowestBankLvl = lvl[i];
                                        b.LowestBankX = ox + nx * step; b.LowestBankZ = oz + nz * step;
                                    }
                                }
                                else
                                {
                                    // Dry, but BELOW the surface — a hole in the
                                    // wet field, not a bank.
                                    b.Gaps++;
                                    float g = lvl[i] - gnd[j];
                                    if (g > b.WorstGap)
                                    {
                                        b.WorstGap = g;
                                        b.WorstGapX = ox + nx * step; b.WorstGapZ = oz + nz * step;
                                    }
                                }
                            }
                    }
                    b.CenX /= b.Cells; b.CenZ /= b.Cells;
                    bodies.Add(b);
                }
            }

            if (bodies.Count == 0)
            {
                outLines.Add($"va_water_scan: no generated water within {radius:0}m (step {step:0.#}m).");
                return outLines;
            }

            bodies.Sort((a, b) => b.Cells.CompareTo(a.Cells));
            float cellArea = step * step;
            outLines.Add($"va_water_scan: {bodies.Count} water bod(ies) within {radius:0}m — " +
                         "freeboard = how far the surface sits UNDER its lowest bank (small positive is right).");
            int shown = 0;
            foreach (var b in bodies)
            {
                if (++shown > 12) { outLines.Add($"  … and {bodies.Count - 12} smaller."); break; }
                float w = (b.MaxX - b.MinX + 1) * step, h = (b.MaxZ - b.MinZ + 1) * step;
                float area = b.Cells * cellArea;
                float longSide = Mathf.Max(w, h), shortSide = Mathf.Max(1f, Mathf.Min(w, h));
                float elong = longSide / shortSide;
                // Shape+size classification: long and thin = flowing, blobby =
                // standing. Area splits pond from lake, width splits stream from river.
                string kind = elong >= 3f
                    ? (shortSide <= 8f ? "STREAM" : "RIVER")
                    : (area >= 6000f ? "LAKE" : "POND");
                string flat = (b.LvlMax - b.LvlMin) <= 0.35f ? "flat" : $"fall {(b.LvlMax - b.LvlMin):0.0}m";

                string free;
                if (b.LowestBank == float.MaxValue) free = "  freeboard n/a (no bank found — body wider than the scan)";
                else
                {
                    float fb = b.LowestBank - b.LowestBankLvl;
                    free = fb < 0f
                        ? $"  ⚠ OVER lowest bank by {-fb:0.0}m at ({b.LowestBankX:0},{b.LowestBankZ:0})"
                        : $"  freeboard {fb:0.0}m at ({b.LowestBankX:0},{b.LowestBankZ:0})";
                }
                string gaps = b.Gaps > 0
                    ? $"  gaps {b.Gaps} (worst {b.WorstGap:0.0}m at ({b.WorstGapX:0},{b.WorstGapZ:0}))"
                    : "";

                outLines.Add($"  [{kind}] ~{area:0}m² {w:0}x{h:0} at ({b.CenX:0},{b.CenZ:0}) " +
                             $"lvl {b.LvlMin:0.0}-{b.LvlMax:0.0} ({flat}) depth {b.DepthMax:0.0}m{free}{gaps}");
            }
            return outLines;
        }
    }
}
