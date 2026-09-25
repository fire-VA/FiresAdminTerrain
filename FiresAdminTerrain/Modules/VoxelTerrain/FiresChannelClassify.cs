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
    // ═══ CHANNEL CLASSIFICATION — WHICH RUNS ARE REALLY RIVERS ═══════════════════════════════════════
    // Incision classification, run-length reporting, and the demotion of runs too short or too weak to be
    // channels. This is the "a river should not be drawn where a river would not be" half of the routing
    // contract: it is cheaper and far more honest to refuse a run here than to carve it and patch it later.
    public static partial class FiresWorldGenBase
    {
        // ⚠⚠ CLASSIFIED ALONG THE POLYLINE, IN WINDOWS — NOT PER CELL. The first version tested each spine
        // CELL against its own local relief, slope and hardness, and the run-length census demolished it:
        // 884 reaches with a MEAN LENGTH OF 4.8 CELLS (24 m), longest 323 m. A 24 m canyon is not a landform,
        // it is confetti — the per-cell measures fluctuate cell to cell, so a reach flickered in and out of
        // the class along its own length. §1a states the shape of the answer plainly: a canyon "follows an
        // existing major river across high ground, and is long. It is not a hole cut between two points; it
        // is the river's own path, deepened." So the unit of classification is a REACH — a window of river —
        // and a reach that cannot sustain the class for MIN_RUN_M is not one.
        static byte[] ClassifyChannelIncision(List<float[]> polys, float[] hOrig, int n, float cellM, int seed)
        {
            var cls = new byte[n * n];
            if (polys == null || polys.Count == 0) return cls;
            float half = (n - 1) * cellM * 0.5f;
            int ring = Math.Max(1, (int)(INC_RING_M / cellM + 0.5f));
            var rise = new float[8];
            var mBuckets = new long[10];
            int canyonNodes = 0, gorgeNodes = 0, totalNodes = 0, demoted = 0;
            int noRelief = 0, tooWet = 0, notBedded = 0, tooNarrow = 0;
            double relSum = 0, hardSum = 0; int nSamp = 0;

            // ═══ CANYONS ARE PLACED ON THE BEST GROUND THIS WORLD HAS, NOT ON AN ABSOLUTE BAR ══════════════
            // (2026-08-16, user: "canyons can be deliberately placed based on logical spots they should sit if
            // you cannot work out the logic to work them into the generation naturally".)
            // A fixed CANYON_RELIEF of 26 m returned ZERO canyons on every seed, and that was not a shy
            // threshold — mean relief along our rivers is 12 m, because §1a's formative process (the plateau
            // RISING while the river holds its course) is not modelled at all. Detection can only ever find
            // what the terrain already contains, so it will keep returning zero however the number moves.
            // Ranking answers the question the user actually asked: WHERE SHOULD ONE SIT. Take the relief
            // distribution along this world's own rivers and cut it at a percentile — the reaches that are
            // most enclosed by high ground, whatever that means on this planet — then keep the ARID, BEDDED,
            // real-river gates unchanged so the ones selected are still justified by §1a's other conditions.
            // A canyon lands on the most canyon-like ground that exists here, the count scales with how much
            // such ground there is, and it cannot silently produce none.
            var reliefAll = new List<float>(8192);
            foreach (var poly0 in polys)
            {
                int m0 = poly0.Length / 4;
                for (int k = 0; k < m0; k += 16)          // sampled: this is a distribution, not a census
                {
                    int x0 = Mathf.Clamp((int)((poly0[k * 4] + half) / cellM + 0.5f), 0, n - 1);
                    int z0 = Mathf.Clamp((int)((poly0[k * 4 + 1] + half) / cellM + 0.5f), 0, n - 1);
                    if (x0 < ring || x0 >= n - ring || z0 < ring || z0 >= n - ring) continue;
                    float b0 = hOrig[z0 * n + x0];
                    if (b0 <= SEA) continue;
                    // ⚠ THE SAME MEASURE THE CLASSIFIER USES — upper median of 8 rays, not the max. A bar set
                    // from a max-of-8 distribution and applied to a median-of-8 one is meaningless: the first
                    // cut did exactly that and reported p50 37.7 m for ground the classifier scored at 12 m.
                    for (int e = 0; e < 8; e++)
                    {
                        int rx = Mathf.Clamp(x0 + RIV_DX[e] * ring, 0, n - 1);
                        int rz = Mathf.Clamp(z0 + RIV_DZ[e] * ring, 0, n - 1);
                        rise[e] = hOrig[rz * n + rx] - b0;
                    }
                    for (int a = 1; a < 8; a++) { float v = rise[a]; int b = a - 1; while (b >= 0 && rise[b] > v) { rise[b + 1] = rise[b]; b--; } rise[b + 1] = v; }
                    reliefAll.Add(rise[4]);
                }
            }
            float canyonReliefBar = CANYON_RELIEF;
            if (reliefAll.Count > 200)
            {
                reliefAll.Sort();
                float pick = reliefAll[(int)(reliefAll.Count * CANYON_RELIEF_PCT)];
                // Never let the percentile place a canyon on ground with no relief at all — a floor keeps the
                // selection honest on a genuinely flat world (where the right answer IS no canyons).
                canyonReliefBar = Math.Max(CANYON_RELIEF_FLOOR, Math.Min(CANYON_RELIEF, pick));
                FiresLogger.LogInfo($"[FiresWorldGen]  │  CANYON SITING — relief along rivers p50 {reliefAll[reliefAll.Count / 2]:0.0} m · p90 {reliefAll[(int)(reliefAll.Count * 0.90f)]:0.0} m · p99 {reliefAll[(int)(reliefAll.Count * 0.99f)]:0.0} m · max {reliefAll[reliefAll.Count - 1]:0.0} m. "
                        + $"Bar set at the {100f * CANYON_RELIEF_PCT:0.0}th percentile = {canyonReliefBar:0.0} m (absolute ideal {CANYON_RELIEF:0} m, floor {CANYON_RELIEF_FLOOR:0} m). "
                        + "Canyons are SITED on the most enclosed ground this world offers, because the uplift that makes a real one is not modelled — see the note above ClassifyChannelIncision.");
            }

            var relief = new List<float>(4096);
            var hardV  = new List<float>(4096);
            var bedV   = new List<float>(4096);
            var widthV = new List<float>(4096);
            var gradeV = new List<float>(4096);
            var dist   = new List<float>(4096);
            var kind   = new List<byte>(4096);

            foreach (var poly in polys)
            {
                int nn = poly.Length / 4;
                if (nn < 2) continue;
                relief.Clear(); hardV.Clear(); bedV.Clear(); widthV.Clear(); gradeV.Clear(); dist.Clear(); kind.Clear();

                float run = 0f;
                for (int k = 0; k < nn; k++)
                {
                    float wx = poly[k * 4], wz = poly[k * 4 + 1];
                    if (k > 0)
                    {
                        float dx = wx - poly[(k - 1) * 4], dz = wz - poly[(k - 1) * 4 + 1];
                        run += (float)Math.Sqrt(dx * dx + dz * dz);
                    }
                    dist.Add(run);

                    int x = Mathf.Clamp((int)((wx + half) / cellM + 0.5f), 0, n - 1);
                    int z = Mathf.Clamp((int)((wz + half) / cellM + 0.5f), 0, n - 1);
                    float bed = hOrig[z * n + x];

                    // RELIEF — how far the surrounding country stands ABOVE this reach. Eight rays rather than
                    // a box so the measure is not biased by the channel's own heading, and the UPPER MEDIAN of
                    // the eight rejects the case that would otherwise dominate: a reach hard against one
                    // hillside reads as deeply enclosed on one side and open on the other, which is a valley
                    // wall, not a canyon. A canyon is enclosed on BOTH sides by definition.
                    for (int e = 0; e < 8; e++)
                    {
                        int rx = Mathf.Clamp(x + RIV_DX[e] * ring, 0, n - 1);
                        int rz = Mathf.Clamp(z + RIV_DZ[e] * ring, 0, n - 1);
                        rise[e] = hOrig[rz * n + rx] - bed;
                    }
                    for (int a = 1; a < 8; a++)
                    {
                        float v = rise[a]; int b = a - 1;
                        while (b >= 0 && rise[b] > v) { rise[b + 1] = rise[b]; b--; }
                        rise[b + 1] = v;
                    }
                    relief.Add(bed <= SEA ? -999f : rise[4]);

                    hardV.Add(FiresRock.Hardness(wx, bed, wz, seed));
                    bedV.Add(FiresRock.BeddingStrength(FiresRock.ClassAt(wx, wz, seed)));
                    // WIDTH from the node's catchment area, the same power law the carve uses — one discharge
                    // model, so "a trickle has not cut a canyon" means the same thing here as there.
                    widthV.Add(Math.Min(WaterRules.RiverProc.WidthMax, Math.Max(WaterRules.RiverProc.WidthMin,
                                 WaterRules.RiverProc.WidthK * (float)Math.Pow(Math.Max(poly[k * 4 + 2], WaterRules.RiverProc.AreaOnset), WaterRules.RiverProc.WidthP))));
                    gradeV.Add(0f);
                    totalNodes++;
                }
                // ALONG-REACH GRADIENT from the node LEVELS, which is the river's own fall. The first version
                // used the local terrain slope at the cell, which is the hillside's steepness and not the
                // river's — a flat reach at the foot of a cliff scored as a plunging one.
                for (int k = 1; k < nn; k++)
                {
                    float dl = dist[k] - dist[k - 1];
                    gradeV[k] = dl > 0.01f ? Math.Abs(poly[(k - 1) * 4 + 3] - poly[k * 4 + 3]) / dl : 0f;
                }
                if (nn > 1) gradeV[0] = gradeV[1];

                // WINDOWED classification: every measure is averaged over INC_WINDOW_M of river centred on the
                // node, so a single noisy cell can neither create nor destroy a landform.
                for (int k = 0; k < nn; k++)
                {
                    int lo = k, hi = k;
                    while (lo > 0 && dist[k] - dist[lo - 1] < INC_WINDOW_M * 0.5f) lo--;
                    while (hi < nn - 1 && dist[hi + 1] - dist[k] < INC_WINDOW_M * 0.5f) hi++;
                    int cnt = hi - lo + 1;
                    float mRel = 0f, mHard = 0f, mBed = 0f, mW = 0f, mG = 0f; bool sea = false;
                    for (int q = lo; q <= hi; q++)
                    {
                        if (relief[q] < -900f) { sea = true; break; }
                        mRel += relief[q]; mHard += hardV[q]; mBed += bedV[q]; mW += widthV[q]; mG += gradeV[q];
                    }
                    if (sea) { kind.Add(INC_NONE); continue; }
                    mRel /= cnt; mHard /= cnt; mBed /= cnt; mW /= cnt; mG /= cnt;
                    relSum += mRel; hardSum += mHard; nSamp++;

                    // GORGE FIRST — rock strength is the DISTINGUISHING input (§1b), so a resistant reach is a
                    // gorge whatever the climate says. A gorge is a rock feature, not a climate one.
                    if (mHard >= GORGE_HARDNESS && mRel >= GORGE_RELIEF && mG >= GORGE_SLOPE)
                    { kind.Add(INC_GORGE); continue; }

                    if (mRel < canyonReliefBar) { noRelief++; kind.Add(INC_NONE); continue; }
                    // Moisture is sampled ONLY past the relief gate: FiresBiome3D.Classify walks a 450 m
                    // enclosure ring, so it is by far the most expensive term here, and most reaches are
                    // already out on geometry.
                    float wetM = MoistureAt(poly[k * 4], poly[k * 4 + 1], seed);
                    mBuckets[Mathf.Clamp((int)(wetM * 10f), 0, 9)]++;
                    if (wetM > CANYON_MOIST_MAX) { tooWet++; kind.Add(INC_NONE); continue; }
                    if (mBed < CANYON_BEDDING) { notBedded++; kind.Add(INC_NONE); continue; }
                    if (mW < 3f) { tooNarrow++; kind.Add(INC_NONE); continue; }
                    kind.Add(INC_CANYON);
                }

                // ⚠⚠ THE SUSTAIN RULE IS *NOT* APPLIED HERE, AND THE REASON IS THE SHAPE OF OUR WATER GRAPH.
                // The obvious place to demote a short landform is along the polyline, and that was the first
                // attempt — it produced CANYON 0, GORGE 0 with every candidate span demoted. Measuring the
                // network explains why: these polys are the PLAN'S CONNECTION RUNS, of which a world has some
                // 18,000, and their lengths run p50 16 m · p90 60 m · longest 893 m, with only 0.9% of runs as
                // long as MIN_RUN_M. A river the player follows for a kilometre is a CHAIN of dozens of these
                // fragments, so a canyon spanning 500 m of river crosses ~30 separate polys and a per-poly
                // sustain test is structurally incapable of seeing it. Contiguity lives in CELL space, where
                // the fragments touch — so the reach is assembled there, after stamping. See DemoteShortRuns.

                // STAMP the surviving reaches onto the grid, walking the segment between consecutive nodes so
                // the cells the rasterizer filled between them are covered too (nodes can be several cells
                // apart, and the carve reads this array at the SEED cell of each cross-section).
                for (int k = 0; k < nn; k++)
                {
                    if (kind[k] == INC_NONE) continue;
                    if (kind[k] == INC_CANYON) canyonNodes++; else gorgeNodes++;
                    float ax = poly[k * 4], az = poly[k * 4 + 1];
                    float bx = k + 1 < nn ? poly[(k + 1) * 4] : ax, bz = k + 1 < nn ? poly[(k + 1) * 4 + 1] : az;
                    float seg = (float)Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                    int steps = Math.Max(1, (int)(seg / (cellM * 0.5f) + 0.5f));
                    for (int t = 0; t <= steps; t++)
                    {
                        float f = t / (float)steps;
                        int x = Mathf.Clamp((int)((ax + (bx - ax) * f + half) / cellM + 0.5f), 0, n - 1);
                        int z = Mathf.Clamp((int)((az + (bz - az) * f + half) / cellM + 0.5f), 0, n - 1);
                        int i = z * n + x;
                        if (cls[i] < kind[k]) cls[i] = kind[k];
                    }
                }
            }

            {
                var mb = new System.Text.StringBuilder("[FiresWorldGen]  │  CANYON MOISTURE CENSUS (of the reaches that CLEAR the relief bar — the threshold is read off this, not guessed):");
                long mTot = 0; foreach (var v in mBuckets) mTot += v;
                for (int b = 0; b < 10; b++)
                    if (mBuckets[b] > 0)
                        mb.Append($"\n      M {b / 10f:0.0}-{(b + 1) / 10f:0.0} | {mBuckets[b],8:N0} | {(mTot > 0 ? 100.0 * mBuckets[b] / mTot : 0),5:0.0}%");
                mb.Append($"\n      (current bar: canyon needs M <= {CANYON_MOIST_MAX:0.00}; FiresBiome3D calls M < 0.30 'Plains — dry flat rain-shadow' and M > 0.56 Forest)");
                FiresLogger.LogInfo(mb.ToString());
            }

            // ⚠ HOW LONG IS A RIVER HERE? The sustain rule can only be met by a run that is itself longer than
            // MIN_RUN_M, so before touching a threshold, measure what the network actually offers. First-order
            // stream initiation (GEN_VERSION 132) turned a few hundred trunks into thousands of short runs,
            // and a rule written for trunk-scale rivers may simply be longer than the median river.
            {
                var lens = new List<float>(polys.Count);
                foreach (var p in polys)
                {
                    int m = p.Length / 4; float L = 0f;
                    for (int k = 1; k < m; k++)
                    {
                        float dx = p[k * 4] - p[(k - 1) * 4], dz = p[k * 4 + 1] - p[(k - 1) * 4 + 1];
                        L += (float)Math.Sqrt(dx * dx + dz * dz);
                    }
                    lens.Add(L);
                }
                lens.Sort();
                float P(float f) => lens[Math.Min(lens.Count - 1, (int)(lens.Count * f))];
                int overMin = 0; foreach (var L in lens) if (L >= CANYON_MIN_RUN_M) overMin++;
                FiresLogger.LogInfo($"[FiresWorldGen]  │  RIVER RUN LENGTHS — {lens.Count:N0} run(s): p50 {P(0.50f):0} m · p75 {P(0.75f):0} m · p90 {P(0.90f):0} m · p99 {P(0.99f):0} m · longest {lens[lens.Count - 1]:0} m. "
                        + $"{overMin:N0} run(s) ({100.0 * overMin / Math.Max(1, lens.Count):0.0}%) are even LONG ENOUGH to sustain a {CANYON_MIN_RUN_M:0} m landform — a canyon can only exist on those.");
            }

            demoted = DemoteShortRuns(cls, n, cellM);
            ReportIncisionClusters(cls, n, cellM);

            FiresLogger.LogInfo($"[FiresWorldGen]  │  INCISION CLASS — {totalNodes:N0} river node(s) over {polys.Count:N0} run(s): CANYON {canyonNodes:N0}, GORGE {gorgeNodes:N0}, ordinary {totalNodes - canyonNodes - gorgeNodes:N0}; {demoted:N0} span(s) DEMOTED for not sustaining their kind's minimum (canyon {CANYON_MIN_RUN_M:0} m, gorge {GORGE_MIN_RUN_M:0} m). "
                    + $"Refused for canyon: relief<{canyonReliefBar:0.0} m {noRelief:N0} | too wet (M>{CANYON_MOIST_MAX:0.00}) {tooWet:N0} | rock won't bench {notBedded:N0} | channel too narrow {tooNarrow:N0}. "
                    + $"Windowed over {INC_WINDOW_M:0} m; mean relief {(nSamp > 0 ? relSum / nSamp : 0):0.0} m, mean rock hardness {(nSamp > 0 ? hardSum / nSamp : 0):0.00}. "
                    + "Water/wind gaps are NOT detected here — see the note above: our drainage is solved on finished terrain, so it already avoids every ridge and a detector would find nothing.");
            // ═══ WHERE ARE THEY? COORDINATES, SO A RENDER CAN BE AIMED AT ONE ═══════════════════════════
            // (2026-08-22, user: "we have built so many studio tools to allow you to visualize the water
            // generation and you just guess and get it wrong over and over".) He is right, and the reason a
            // render was not aimed at a canyon before is that nothing printed WHERE one is — the census gives
            // counts, and a count cannot be looked at. `watermesh` paints the incision class ORANGE and takes
            // a zoom, so a coordinate is the only missing piece.
            // Spatially DE-DUPLICATED at 400 m, exactly as GorgeProbe's own site list is and for the same
            // reason: classified cells cluster along one thalweg, so ten samples of one canyon would read as
            // ten canyons. Sampled from `cls` itself, i.e. the array the carve actually consumes.
            {
                var csite = new List<(float x, float z, byte k)>();
                for (int i2 = 0; i2 < cls.Length && csite.Count < 12; i2++)
                {
                    if (cls[i2] == INC_NONE) continue;
                    float cxw = -half + (i2 % n) * cellM, czw = -half + (i2 / n) * cellM;
                    bool near = false;
                    foreach (var p in csite)
                        if ((p.x - cxw) * (p.x - cxw) + (p.z - czw) * (p.z - czw) < 400f * 400f) { near = true; break; }
                    if (!near) csite.Add((cxw, czw, cls[i2]));
                }
                var sb2 = new System.Text.StringBuilder();
                sb2.Append($"[FiresWorldGen]  │  INCISION SITES — {csite.Count:N0} distinct classified reach(es), >=400 m apart, to aim a render at. ");
                foreach (var p in csite)
                    sb2.Append($"{(p.k == INC_GORGE ? "GORGE" : "CANYON")} ({p.x:F0},{p.z:F0})  ");
                sb2.Append(". Render one with:  watermesh <seed> <n> out.png 1400 <x> <z> 250   (ORANGE = canyon class, RED = gorge, MAGENTA = float).");
                FiresLogger.LogInfo(sb2.ToString());
            }
            return cls;
        }

        /// HOW LONG IS A RIVER RUN, at a named point in the pipeline. Called on both sides of the passes that
        /// cut runs up, because "the plan's runs are 16 m fragments" is a claim about WHICH pass fragmented
        /// them, and that is not answerable from the end state alone.
        internal static void ReportRunLengths(List<float[]> polys, string when)
        {
            if (polys == null || polys.Count == 0) { FiresLogger.LogInfo($"[FiresWorldGen]  │  RIVER RUN LENGTHS ({when}) — no runs."); return; }
            var lens = new List<float>(polys.Count);
            double total = 0;
            foreach (var p in polys)
            {
                int m = p.Length / 4; float L = 0f;
                for (int k = 1; k < m; k++)
                {
                    float dx = p[k * 4] - p[(k - 1) * 4], dz = p[k * 4 + 1] - p[(k - 1) * 4 + 1];
                    L += (float)Math.Sqrt(dx * dx + dz * dz);
                }
                lens.Add(L); total += L;
            }
            lens.Sort();
            float P(float f) => lens[Math.Min(lens.Count - 1, (int)(lens.Count * f))];
            FiresLogger.LogInfo($"[FiresWorldGen]  │  RIVER RUN LENGTHS ({when}) — {lens.Count:N0} run(s), {total / 1000.0:0.0} km of channel total: "
                    + $"p50 {P(0.50f):0} m · p75 {P(0.75f):0} m · p90 {P(0.90f):0} m · p99 {P(0.99f):0} m · longest {lens[lens.Count - 1]:0} m.");
        }

        /// SUSTAIN OR DEMOTE, in cell space — the rule that turns a classification into a landform.
        ///
        /// A canyon is long by definition (§1a: "it is the river's own path, deepened"), so a stretch that
        /// cannot hold its class for MIN_RUN_M of continuous channel is the classifier reacting to local
        /// noise. Applied HERE rather than along the polyline because the plan's connection runs are
        /// fragments — p50 16 m — and a real river is a chain of dozens of them; only after stamping do the
        /// fragments touch, so only here can a reach be assembled. 8-connected because a channel runs
        /// diagonally as often as not, and length is taken as cell count × cell size, which is right for a
        /// one-cell-wide channel line.
        static int DemoteShortRuns(byte[] cls, int n, float cellM)
        {
            var seen = new System.Collections.BitArray(n * n);
            var q = new Queue<int>();
            var comp = new List<int>(512);
            int demoted = 0;
            for (int i0 = 0; i0 < cls.Length; i0++)
            {
                if (cls[i0] == 0 || seen[i0]) continue;
                byte kind = cls[i0];
                comp.Clear(); q.Clear(); q.Enqueue(i0); seen[i0] = true;
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); comp.Add(c);
                    int cz = c / n, cx = c - cz * n;
                    for (int e = 0; e < 8; e++)
                    {
                        int nx = cx + RIV_DX[e], nz = cz + RIV_DZ[e];
                        if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                        int j = nz * n + nx;
                        if (seen[j] || cls[j] != kind) continue;
                        seen[j] = true; q.Enqueue(j);
                    }
                }
                float need = kind == INC_CANYON ? CANYON_MIN_RUN_M : GORGE_MIN_RUN_M;
                if (comp.Count * cellM < need)
                {
                    for (int k = 0; k < comp.Count; k++) cls[comp[k]] = INC_NONE;
                    demoted++;
                }
            }
            return demoted;
        }

        /// The classified reaches as CONTIGUOUS RUNS with world coordinates and length, biggest first — so a
        /// claim about canyons can be inspected on a zoomed render instead of taken on trust. Also the honest
        /// check on whether these are LANDFORMS or CONFETTI: a canyon is long (§1a — "it is the river's own
        /// path, deepened"), so a census of 400 cells scattered as 400 one-cell specks would mean the
        /// classifier is firing on noise, and the mean run length below is what says which it is.
        static void ReportIncisionClusters(byte[] cls, int n, float cellM)
        {
            var seen = new System.Collections.BitArray(n * n);
            var q = new Queue<int>();
            var runs = new List<(int size, byte kind, float cx, float cz)>();
            float half = (n - 1) * cellM * 0.5f;
            for (int i0 = 0; i0 < cls.Length; i0++)
            {
                if (cls[i0] == 0 || seen[i0]) continue;
                byte kind = cls[i0];
                int size = 0; double sx = 0, sz = 0;
                q.Clear(); q.Enqueue(i0); seen[i0] = true;
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); size++;
                    int cz = c / n, cx = c - cz * n;
                    sx += cx; sz += cz;
                    for (int e = 0; e < 8; e++)   // 8-connected: a channel runs diagonally as often as not
                    {
                        int nx = cx + RIV_DX[e], nz = cz + RIV_DZ[e];
                        if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                        int j = nz * n + nx;
                        if (seen[j] || cls[j] != kind) continue;
                        seen[j] = true; q.Enqueue(j);
                    }
                }
                runs.Add((size, kind, (float)(sx / size) * cellM - half, (float)(sz / size) * cellM - half));
            }
            if (runs.Count == 0) { FiresLogger.LogInfo("[FiresWorldGen]  │  INCISION RUNS — none."); return; }
            runs.Sort((a, b) => b.size.CompareTo(a.size));
            long tot = 0; int canyonRuns = 0, gorgeRuns = 0;
            foreach (var r in runs) { tot += r.size; if (r.kind == INC_CANYON) canyonRuns++; else gorgeRuns++; }
            var sb = new System.Text.StringBuilder($"[FiresWorldGen]  │  INCISION RUNS — {runs.Count:N0} contiguous reach(es) ({canyonRuns:N0} canyon, {gorgeRuns:N0} gorge), mean {(double)tot / runs.Count:0.0} cells = {(double)tot / runs.Count * cellM:0} m. A canyon is LONG by definition, so a mean near 1 means this is firing on noise, not landforms. Longest:");
            for (int k = 0; k < Math.Min(8, runs.Count); k++)
                sb.Append($"\n      {(runs[k].kind == INC_CANYON ? "CANYON" : "GORGE ")} {runs[k].size,5:N0} cells = {runs[k].size * cellM,6:0} m centred ({runs[k].cx:0},{runs[k].cz:0})");
            FiresLogger.LogInfo(sb.ToString());
        }

        // (v133) THE WATER TABLE MAKES THE BIOME, not the other way round.
        //
        // Set by ClassifyWetlands: every cell of every closed basin it judged a WETLAND rather than a lake.
        // Read TWICE, and the two readers are the point of the design:
        //   • ApplyWetlandBiome, right after biome classification — waterlogged ground becomes swamp biome;
        //   • FiresWaterPlanner.BuildPlan — the lake funnel yields this ground wherever the marsh funnel has
        //     produced a candidate for it, so the marsh is not outbid by a lake on its own basin.
        //
        // WHY THIS EXISTS: `WC_SWAMP` comes from `WaterBodyKind.Marsh` on a plan body, and the planner only
        // nominates marsh candidates from SWAMP BIOME. The v132 wetland pass set water DEPTH, not biome, so the
        // planner kept seeing lakes and ponds — measured `swamp = 32` on a world with 679 converted basins.
        // The causality was backwards: biome was decided from climate and the water fitted to it, when in
        // reality a swamp exists BECAUSE the ground is waterlogged. Emitting wetness from hydrology and letting
        // the biome read it fixes the classification and the causality in one change, and needs no reordering —
        // biomes are already classified after AnalyzeDrainage.
        internal static bool[] _wetlandMask;

        // ⚠ TEMPERATURE-GATED. Waterlogged ground in the cold is a FEN or a frozen bog, not a cypress swamp,
        // and Mountain/Frostreach cells must keep their identity — `StampBodyClasses` freezes bodies on those
        // biomes, and overwriting them here would silently thaw every alpine tarn.
        const float SWAMP_MIN_C = 2f;

    }
}
