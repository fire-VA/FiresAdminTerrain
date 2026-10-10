using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Random = System.Random;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══ DRAINAGE — WHERE WATER WOULD GO ═════════════════════════════════════════════════════════════
    // Priority-flood depression filling and flow accumulation: the analysis the whole water plan is built
    // on. PriorityFloodFill produces the FILL TABLE (every depression raised to its spill), and that table
    // is why routing must ask whether a basin actually holds a body — a route traced on the filled surface
    // glides across a dry basin at spill height, which is the aqueduct defect.
    public static partial class FiresWorldGenBase
    {




        // (v128 diag) WHY DOES THIS WORLD POND INSTEAD OF DRAIN? Counting the depressions, not theorising about
        // them. A river exists to connect one body to the next, so a landscape whose terrain is pocked with
        // thousands of closed basins cannot help but be mostly lake: each basin swallows its own catchment, no
        // catchment grows large enough to trace a channel, and every body then needs an INVENTED outlet (497 of
        // 530 on seed 530442569). That is the shape of the remaining problem, and this measures it directly:
        // how much land sits under fill, in how many separate basins, and how deep.
        //
        // `w` is the depression-filled surface and hOrig the real ground, so w - hOrig is the water a basin
        // holds. Counted on LAND only (hOrig > SEA) — the ocean is not a drainage failure.
        static void ReportDepressions(float[] w, float[] hOrig, int n, float cellM)
        {
            var seen = new System.Collections.BitArray(n * n);
            var q = new Queue<int>();
            var sizes = new List<int>();
            var basinDepth = new List<float>();     // max depth of each basin, paired with sizes by index
            long filledCells = 0; double volume = 0; float deepest = 0f;
            // PER-CELL DEPTH — the measurement that decides whether this is fixable by draining or only by
            // changing the terrain. Standing water that is mostly ANKLE DEEP is marginal flooding: lower a
            // basin's outlet a couple of metres and most of it becomes land. Standing water that is mostly
            // DEEP is real basin volume, and no outlet treatment will meaningfully shrink it — the continent
            // itself is bowl-shaped and that is a SurfaceElev question.
            float[] dEdge = { 1f, 2f, 5f, 10f, 25f, 50f, float.MaxValue };
            var dCells = new long[dEdge.Length];
            for (int i0 = 0; i0 < n * n; i0++)
            {
                if (seen[i0] || !(hOrig[i0] > SEA && w[i0] > hOrig[i0] + 0.5f)) continue;
                int size = 0; float basinMax = 0f;
                q.Clear(); q.Enqueue(i0); seen[i0] = true;
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); size++; filledCells++;
                    float d = w[c] - hOrig[c];
                    volume += d; if (d > deepest) deepest = d;
                    if (d > basinMax) basinMax = d;
                    { int b = 0; while (b < dEdge.Length - 1 && d > dEdge[b]) b++; dCells[b]++; }
                    int cx = c % n, cz = c / n;
                    for (int e = 0; e < 4; e++)
                    {
                        int nx = cx + RIV_DX[e], nz = cz + RIV_DZ[e];
                        if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                        int j = nz * n + nx;
                        if (seen[j] || !(hOrig[j] > SEA && w[j] > hOrig[j] + 0.5f)) continue;
                        seen[j] = true; q.Enqueue(j);
                    }
                }
                sizes.Add(size); basinDepth.Add(basinMax);
            }
            // WHERE THE WATER ACTUALLY LIVES vs where the COUNT lives. A threshold has to be read off this and
            // not guessed: if the tiny basins are almost all of the count but almost none of the area, deleting
            // them is free. Guessing a threshold is what wasted the `maxFlatCross` round.
            {
                int[] edge = { 1, 4, 16, 64, 256, 1024, 4096, int.MaxValue };
                var bCount = new int[edge.Length]; var bCells = new long[edge.Length]; var bDeep = new float[edge.Length];
                for (int i = 0; i < sizes.Count; i++)
                {
                    int b = 0; while (b < edge.Length - 1 && sizes[i] > edge[b]) b++;
                    bCount[b]++; bCells[b] += sizes[i];
                    if (basinDepth[i] > bDeep[b]) bDeep[b] = basinDepth[i];
                }
                var sb = new System.Text.StringBuilder("[FiresWorldGen]  │  BASIN SIZE HISTOGRAM (cells | basins | % of all basins | % of all filled area | deepest):");
                long allCells = Math.Max(1, filledCells);
                for (int b = 0; b < edge.Length; b++)
                {
                    if (bCount[b] == 0) continue;
                    string lo = b == 0 ? "1" : (edge[b - 1] + 1).ToString();
                    string hi = edge[b] == int.MaxValue ? "+" : edge[b].ToString();
                    sb.Append($"\n      {lo}-{hi,-6} | {bCount[b],7:N0} | {100.0 * bCount[b] / sizes.Count,5:0.0}% | {100.0 * bCells[b] / allCells,5:0.0}% | {bDeep[b],6:0.0} m");
                }
                FiresLogger.LogInfo(sb.ToString());
            }
            {
                // HYPSOMETRY — how much land exists at each altitude, and how much of it is drowned. The target
                // is not "no standing water" but a believable amount, and WHERE it sits decides what to do about
                // it. SEA is 30, so ground below that is ocean by definition and never appears here. Flooding
                // packed just above 30 is coastal lowland — short paths to the sea, cheap to drain or simply
                // let the ocean have. Flooding spread through the mid altitudes is interior basin, and only a
                // change to the terrain's low-frequency shape reaches it.
                float[] hEdge = { 50f, 100f, 200f, 400f, float.MaxValue };
                var landB = new long[hEdge.Length]; var wetB = new long[hEdge.Length];
                for (int i = 0; i < n * n; i++)
                {
                    float g = hOrig[i];
                    if (g <= SEA) continue;
                    int b = 0; while (b < hEdge.Length - 1 && g > hEdge[b]) b++;
                    landB[b]++;
                    if (w[i] > g + 0.5f) wetB[b]++;
                }
                var hb = new System.Text.StringBuilder("[FiresWorldGen]  │  HYPSOMETRY (altitude band | land cells | % of all land | flooded | % of THAT band drowned):");
                long landTot = 0; for (int b = 0; b < hEdge.Length; b++) landTot += landB[b];
                float lo2 = SEA;
                for (int b = 0; b < hEdge.Length; b++)
                {
                    string hi = hEdge[b] == float.MaxValue ? "+" : hEdge[b].ToString("0");
                    hb.Append($"\n      {lo2,4:0}-{hi,-4} m | {landB[b],11:N0} | {100.0 * landB[b] / Math.Max(1, landTot),5:0.0}% | {wetB[b],11:N0} | {100.0 * wetB[b] / Math.Max(1, landB[b]),5:0.0}%");
                    lo2 = hEdge[b];
                }
                FiresLogger.LogInfo(hb.ToString());
            }
            {
                var sb = new System.Text.StringBuilder("[FiresWorldGen]  │  WATER DEPTH HISTOGRAM (how deep the standing water actually is | cells | % of flooded area | % of ALL land):");
                double landAll = 0; for (int i = 0; i < n * n; i++) if (hOrig[i] > SEA) landAll++;
                long allC = Math.Max(1, filledCells);
                double cellA2 = cellM * cellM;
                float lo = 0.5f;
                for (int b = 0; b < dEdge.Length; b++)
                {
                    string hi = dEdge[b] == float.MaxValue ? "+" : dEdge[b].ToString("0");
                    sb.Append($"\n      {lo,5:0.0}-{hi,-4} m | {dCells[b],10:N0} | {100.0 * dCells[b] / allC,5:0.0}% | {100.0 * dCells[b] / Math.Max(1, landAll),5:0.0}% of land = {dCells[b] * cellA2 / 1e6,6:0.0} km²");
                    lo = dEdge[b];
                }
                FiresLogger.LogInfo(sb.ToString());
            }
            if (sizes.Count == 0) { FiresLogger.LogInfo("[FiresWorldGen]  │  DEPRESSION CENSUS — no inland fill at all."); return; }
            sizes.Sort(); sizes.Reverse();
            double cellA = cellM * cellM;
            double landCells = 0;
            for (int i = 0; i < n * n; i++) if (hOrig[i] > SEA) landCells++;
            int med = sizes[sizes.Count / 2];
            long top10 = 0; for (int i = 0; i < Math.Min(10, sizes.Count); i++) top10 += sizes[i];
            FiresLogger.LogInfo($"[FiresWorldGen]  │  DEPRESSION CENSUS — {sizes.Count:N0} closed basin(s) holding {filledCells:N0} cells "
                    + $"= {filledCells * cellA / 1e6:0.0} km² ({100.0 * filledCells / Math.Max(1, landCells):0.0}% OF ALL LAND under standing water). "
                    + $"Largest {sizes[0] * cellA / 1e6:0.00} km², top-10 hold {100.0 * top10 / Math.Max(1, filledCells):0.0}% of it, median basin {med} cell(s) "
                    + $"({med * cellA:0} m²). Mean depth {volume / Math.Max(1, filledCells):0.00} m, deepest {deepest:0.0} m. "
                    + $"A landscape drains when basins are FEW and LARGE; thousands of small ones means every catchment is swallowed before it can become a river.");
        }


        private static List<float[]> AnalyzeDrainage(float[] h, int n, int seed, out float[] fill)
        {
            var rsw = System.Diagnostics.Stopwatch.StartNew();
            FlatRegionAudit(h, n, (2f * _half) / (n - 1), "A: terrain + erosion, before any water");
            var w = PriorityFloodFill(h, n, "drainage fill");
            // (v129) Remove the noise potholes from the terrain itself, BEFORE anything downstream treats them as
            // water. Threshold read off the basin histogram, not guessed: <=64 cells is 90.7% of all basins and
            // 1.8% of the filled area on the seed this was measured on. `w` is unchanged by this — raising a pit
            // floor to its own spill level cannot alter where anything else spills.
            {
                float cellA0 = ((2f * _half) / (n - 1)) * ((2f * _half) / (n - 1));
                int potholeMaxCells = Math.Max(1, (int)(POTHOLE_MAX_AREA_M2 / cellA0 + 0.5f));
                int potFilled = FillSmallDepressions(h, w, n, potholeMaxCells, out long potCells);
                if (potFilled > 0)
                    FiresLogger.LogInfo($"[FiresWorldGen]  │  POTHOLE FILL — {potFilled:N0} noise depression(s) (<={POTHOLE_MAX_AREA_M2:0} m² = {potholeMaxCells} cells here) raised to their own spill level, {potCells:N0} cell(s) of terrain. These were standing water to the router, so every inland run died in the first one it met.");

                // (v133) Then say WHICH closed basins are wetlands rather than lakes. This only CLASSIFIES —
                // it writes no water and no terrain — so there is exactly one owner of "what level does this
                // wetland sit at", and it is the planner's marsh funnel.
                int wetl = ClassifyWetlands(h, w, n, (2f * _half) / (n - 1), out long wetC);
                if (wetl > 0)
                    FiresLogger.LogInfo($"[FiresWorldGen]  │  WETLANDS — {wetl:N0} closed basin(s) classified WETLAND rather than lake (floor gentler than {WETLAND_MAX_GRADE:0.00}, mean depth under {WETLAND_MAX_MEAN_DEPTH:0} m, at least {WETLAND_MIN_AREA_M2 / 1e4:0.0} ha): {wetC:N0} cell(s) of ground handed to the planner's marsh funnel, which owns their level. Steeper or deeper basins keep their depth — they are lakes.");

                FlatRegionAudit(h, n, (2f * _half) / (n - 1), "B: after fill + pothole + wetland", w);

                // ⚠ DO NOT ADD AN OUTLET-BREACHING PASS HERE. It was written, measured across three seeds, and
                // removed: cutting the spillway of every low basin (surface ≤ SEA+70 m) by 6 m carved 258k-350k
                // cells of terrain per world and moved land-under-water by NOTHING — 21.1% → 21.1%, 17.6% →
                // 17.7%, 26.4% → 26.6%. The arithmetic says why, and it is in the WATER DEPTH HISTOGRAM above:
                // only ~10% of flooded area is under 2 m while half is under more than 10 m, so lowering a lake
                // surface barely moves its FOOTPRINT when the basin has steep sides. Reclaiming real area needs
                // 10-25 m of cut, which is the same demolition that made hydraulic erosion unusable at strength.
                // Deep basins cannot be fixed from their outlets — only by not generating them that deep.
            }
            int ms = seed * 7 + 977;
            // Resolution independence: the hydrology is authored at the 2048
            // reference grid. RS rescales every cell-space frequency/distance
            // and cellA converts flow accumulation (a CELL COUNT) into physical
            // drainage AREA (m²) — so rivers, valleys and erosion are the SAME
            // physical features whether a cell is 10m or 2m. RS==1 at n=2048.
            float cellM = (2f * _half) / (n - 1);
            float cellA = cellM * cellM;
            float RS = 2047f / (n - 1) * SpanScale;   // 0.2.722: x (edge / 10500), exactly 1 on a vanilla-size world

            // v10 — CONVERGENT drainage. On broad uniform slopes pure
            // steepest-descent forms SHEETS of parallel streams ("30 rivers
            // in a line"). Route the flow over a corrugated copy of the water
            // table: coherent valley-attractor noise (~700m wavelength, ~7m
            // deep) gathers neighbouring columns into common winding trunks.
            // The corrugation only biases WHERE water runs — beds, depths,
            // water levels and estuaries still read the REAL table.
            // v12 — the corrugation is DOMAIN-WARPED so trough lines curve:
            // straight troughs made tributaries join trunks at dead right
            // angles (the comb pattern in flats/swamps); curved troughs give
            // oblique, dendritic joins.
            var wr = new float[n * n];
            for (int z2 = 0; z2 < n; z2++)
            {
                for (int x2 = 0; x2 < n; x2++)
                {
                    int i2 = z2 * n + x2;
                    float wx2 = x2 + (Fbm(x2, z2, RS / 210f, 2, ms + 8) - 0.5f) * (22f / RS);
                    float wz2 = z2 + (Fbm(x2, z2, RS / 210f, 2, ms + 9) - 0.5f) * (22f / RS);
                    float vr = 1f - Math.Abs(2f * Fbm(wx2, wz2, RS / 68f, 3, ms + 7) - 1f);
                    // (v92) ROUTE ON TRUTH — docs/HYDROLOGY.md Part III: convergence must EMERGE from the real
                    // terrain; a routing surface that lies routes rivers over ridges. The 13m valley-attractor
                    // was exactly such a lie — deep fake troughs cut ACROSS real hills, and flow followed them
                    // up-and-over (braids around knobs, channels crossing on saddles, the "no logical sense"
                    // rivers). Amplitude cut 13m → 1.2m MICRO-TOPOGRAPHY: floodplain micro-relief is real, so
                    // a ~1m bias may still break steepest-descent ties on broad flats and gather parallel
                    // threads — but it is physically INCAPABLE of carrying flow across any hill taller than a
                    // metre. Slope gate tightened (fade starts 1.5%, gone by 6%): on any perceptible slope the
                    // routing surface IS the real fill. (v90's 13m + 13% gate was not enough — the lie stayed.)
                    float sgx = (x2 > 0 && x2 < n - 1) ? (w[i2 + 1] - w[i2 - 1]) : 0f;
                    float sgz = (z2 > 0 && z2 < n - 1) ? (w[i2 + n] - w[i2 - n]) : 0f;
                    float grade = Mathf.Sqrt(sgx * sgx + sgz * sgz) / (2f * cellM);   // real surface grade (m/m)
                    float flat = 1f - Mathf.Clamp01((grade - 0.015f) / 0.045f);        // 1 on <1.5% flats → 0 by 6%
                    wr[i2] = w[i2] - vr * 1.2f * flat;
                }
            }
            wr = PriorityFloodFill(wr, n, "corrugated re-fill");   // re-fill so corrugation pits don't strand flow
            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: corrugated ({rsw.Elapsed.TotalSeconds:0}s); computing flow directions...");

            // v30 — flow structure FIRST (D8 single-flow tree on the
            // corrugated fill surface), and EVERYTHING downstream reads it:
            // the v18 incision previously used the braided multi-flow
            // accumulation and cut every corrugation groove 3-14m deep —
            // parallel dry slit channels beside the painted rivers
            // (live-cache-proven; unaffected by any mask/paint fix because
            // the grooves are in the TERRAIN, not the water field).
            // v32 — D-INFINITY flow (Tarboton 1997): CONTINUOUS flow direction,
            // replacing the D8 single-flow that locked every channel to one of 8
            // compass headings — rivers drew as straight axis/diagonal segments
            // with 45°/90° jogs (user-caught at native res). Each cell takes the
            // steepest of its 8 triangular facets → a REAL-VALUED flow angle, and
            // SPLITS its flow between the two neighbours bracketing that angle, so
            // accumulation (and every incised channel) follows smooth curves.
            // (The dominant-receiver array `dir[]` was dropped with the tree
            // level-solve that consumed it — nothing reads it now; removing it
            // saves a full n² int buffer at 2m, part of the CarveRivers OOM fix.)
            var fr1 = new int[n * n];       // facet cardinal-side receiver
            var fr2 = new int[n * n];       // facet diagonal-side receiver (-1 = pure cardinal)
            var fp1 = new float[n * n];     // fraction of flow to fr1
            var order = new int[n * n];
            for (int i2 = 0; i2 < n * n; i2++) { fr1[i2] = -1; fr2[i2] = -1; fp1[i2] = 1f; order[i2] = i2; }
            // Facet f (0..7): cardinal neighbour CARD[f], adjacent diagonal
            // DIAG[f], indexing RIV_DX/RIV_DZ (0E 1W 2N 3S 4NE 5SE 6NW 7SW).
            int[] CARD = { 0, 2, 2, 1, 1, 3, 3, 0 };
            int[] DIAG = { 4, 4, 6, 6, 7, 7, 5, 5 };
            const float QPI = 0.7853982f;                   // π/4 = facet angular width
            float invCellM = 1f / cellM;
            for (int z2 = 1; z2 < n - 1; z2++)
            {
                for (int x2 = 1; x2 < n - 1; x2++)
                {
                    int i2 = z2 * n + x2;
                    float zc = wr[i2];
                    float bestS = 0f; int bC = -1, bD = -1; float bR = 0f;
                    for (int f = 0; f < 8; f++)
                    {
                        int cj = (z2 + RIV_DZ[CARD[f]]) * n + (x2 + RIV_DX[CARD[f]]);
                        int dj = (z2 + RIV_DZ[DIAG[f]]) * n + (x2 + RIV_DX[DIAG[f]]);
                        float s1 = (zc - wr[cj]) * invCellM;
                        float s2 = (wr[cj] - wr[dj]) * invCellM;
                        if (s1 <= 0f && s2 <= 0f) continue;
                        float r = (float)Math.Atan2(s2, s1);
                        float s;
                        if (r < 0f) { r = 0f; s = s1; }
                        else if (r > QPI) { r = QPI; s = (zc - wr[dj]) * invCellM * 0.70710678f; }
                        else s = (float)Math.Sqrt(s1 * s1 + s2 * s2);
                        if (s > bestS) { bestS = s; bC = cj; bD = dj; bR = r; }
                    }
                    if (bC < 0) continue;
                    float pc = 1f - bR / QPI;               // fraction to the cardinal receiver
                    fr1[i2] = bC;
                    if (pc >= 0.99995f) { fp1[i2] = 1f; fr2[i2] = -1; }
                    else { fp1[i2] = pc; fr2[i2] = bD; }
                }
            }
            Array.Sort(wr, order);                          // ascending fill surface — sort IN PLACE (wr is dead after this; skips a full n² clone that OOM'd the 2m carve)
            wr = null; GC.Collect();                         // reclaim the corrugated fill before acc1 grows the heap — keeps the accumulation phase off the OOM ceiling
            // (v133) Seed accumulation with each cell's RUNOFF, not a flat 1. See RunoffAt — this is the
            // single line the hydrology audit turns on: with a constant, catchment is pure upstream AREA and a
            // headwater can never clear a channel threshold, which is why there was no water above 250 m.
            var acc1 = new float[n * n];
            {
                float half2 = (n - 1) * cellM * 0.5f;
                double roSum = 0; float roLo = float.MaxValue, roHi = 0f;
                for (int z2 = 0; z2 < n; z2++)
                {
                    float wz2 = z2 * cellM - half2;
                    for (int x2 = 0; x2 < n; x2++)
                    {
                        float r = RunoffAt(h[z2 * n + x2], x2 * cellM - half2, wz2, seed);
                        acc1[z2 * n + x2] = r;
                        roSum += r; if (r < roLo) roLo = r; if (r > roHi) roHi = r;
                    }
                }
                FiresLogger.LogInfo($"[FiresWorldGen]  │  RUNOFF — mean {roSum / ((double)n * n):0.00} per cell (was a flat 1.00), range {roLo:0.00}–{roHi:0.00}. "
                        + "Orographic: high ground makes far more water than lowland, and evaporation falls with altitude — so mountains can finally clear a channel threshold.");
            }
            for (int k2 = n * n - 1; k2 >= 0; k2--)         // upstream → downstream
            {
                int i2 = order[k2];
                int c = fr1[i2];
                if (c < 0) continue;
                float a = acc1[i2], f1 = fp1[i2];
                acc1[c] += a * f1;
                int d = fr2[i2];
                if (d >= 0) acc1[d] += a * (1f - f1);
            }

            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: flow field done ({rsw.Elapsed.TotalSeconds:0}s); tracing channels...");
            ReportBasinCensus(h, w, acc1, n, cellM);   // read-only: is this basin a lake or a wetland? see docs/WETLANDS_AND_LAKES.md
            // RIVER REWORK — the traced smooth polylines ARE the rivers now, so
            // the drainage no longer draws itself into the terrain by hand.
            var riverPolys = TraceRivers(h, acc1, fr1, fr2, w, n, cellM, (n - 1) * cellM * 0.5f);
            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: {(riverPolys != null ? riverPolys.Count : 0)} polyline(s) traced ({rsw.Elapsed.TotalSeconds:0}s); solving valleys...");

            // (v92) ROUTE VALIDATION — detect and kill the traces that make no physical sense (user mandate).
            // The REAL fill table `w` IS the pooled water surface on the true terrain: along any physically
            // possible flow path it is NON-INCREASING downstream by the fill's construction. A trace seduced
            // across a divide (corrugation-era routes, braids over knobs, rivers over hills) shows the
            // signature unmistakably: walking UPSTREAM from the mouth, w rises to the divide crest then FALLS
            // on the far side — water on the far side belongs to a DIFFERENT catchment. TRUNCATE at the first
            // such fall (TraceRivers walks upstream from real mouths, so the mouth-anchored reach is the
            // well-founded part) and drop stubs too short to be rivers. Lakes/pools pass untouched: fill is
            // FLAT across them (rises upstream beyond — never falls).
            if (riverPolys != null)
            {
                const float CLIMB_TOL = 2.5f;     // m of fill-fall before we call it a divide crossing (flat-epsilon/pool slack)
                const int MIN_NODES = 8;          // a truncated stub shorter than this isn't a river
                float rhalfV = (n - 1) * cellM * 0.5f;
                int truncatedN = 0, droppedN = 0;
                for (int p = riverPolys.Count - 1; p >= 0; p--)
                {
                    var poly = riverPolys[p];
                    int nn = poly.Length / 4;
                    if (nn < 2) continue;
                    bool srcAtStart = poly[2] <= poly[(nn - 1) * 4 + 2];   // area grows downstream → mouth = high-area end
                    float runMax = float.MinValue;
                    int good = -1;                                         // nodes (from the mouth) before the crest fall
                    for (int s = 0; s < nn; s++)
                    {
                        int k = srcAtStart ? nn - 1 - s : s;               // walk mouth → source
                        int cxi = Mathf.Clamp((int)((poly[k * 4] + rhalfV) / cellM + 0.5f), 0, n - 1);
                        int czi = Mathf.Clamp((int)((poly[k * 4 + 1] + rhalfV) / cellM + 0.5f), 0, n - 1);
                        float fw = w[czi * n + cxi];
                        if (fw > runMax) runMax = fw;
                        else if (runMax - fw > CLIMB_TOL) { good = s; break; }
                    }
                    int keepM = good < 0 ? nn : good;                      // nodes retained from the mouth after divide truncation
                    // SOURCE JUSTIFICATION (user: "no source water at the top — know where and WHY every body
                    // of water is placed"): a surviving head must be a REAL channel head — node catchment
                    // area ≥ AreaOnset (channels begin where drainage crosses the threshold; water never
                    // starts mid-slope from nothing). Walk in from the source end of the retained reach and
                    // trim every node that can't justify itself; a trace that never can is no river at all.
                    float ONSET = WaterRules.RiverProc.AreaOnset;
                    int keep = 0;
                    for (int s2 = keepM - 1; s2 >= 0; s2--)                // source-most retained node first
                    {
                        int k2 = srcAtStart ? nn - 1 - s2 : s2;
                        if (poly[k2 * 4 + 2] >= ONSET) { keep = s2 + 1; break; }
                    }
                    if (keep == nn && good < 0) continue;                  // clean trace, justified head — untouched
                    if (keep < MIN_NODES) { riverPolys.RemoveAt(p); droppedN++; continue; }
                    var np = new float[keep * 4];
                    if (srcAtStart) Array.Copy(poly, (nn - keep) * 4, np, 0, keep * 4);   // keep the tail (mouth end)
                    else Array.Copy(poly, 0, np, 0, keep * 4);                            // keep the head (mouth end)
                    riverPolys[p] = np;
                    truncatedN++;
                }
                if (truncatedN > 0 || droppedN > 0)
                    FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: ROUTE VALIDATION — {truncatedN} trace(s) truncated (divide crossings cut at the crest + sourceless heads trimmed to their real channel head), {droppedN} dropped outright (fill-monotone tol {CLIMB_TOL:0.0}m, head catchment ≥ AreaOnset): every river now has a WHY at both ends.");
            }

            // MEMORY: free the flow-routing scratch NOW — the carve below
            // allocates its own large n² buffers (hOrig clone, widthOf, seedIdx,
            // rDist, paintLvl) and the 2m transient set otherwise peaks ~5.5GB
            // and threw OutOfMemory at the hOrig clone on the client (32GB box +
            // heavy modpack + LOH pressure). wr/fp1/order are dead after the acc
            // walk, fr1/fr2/acc1 after the trace; none is read again. The forced
            // collect reclaims the LOH before the carve grows the heap.
            wr = null; fp1 = null; fr1 = null; fr2 = null; order = null; acc1 = null;
            GC.Collect();
            fill = w;
            return riverPolys;
        }

    }
}
