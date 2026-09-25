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
    // ═══ RIVER TRACING — FROM FLOW ACCUMULATION TO POLYLINES ═════════════════════════════════════════
    // TraceRivers walks the accumulation field into raw channel paths; the rest is polyline handling
    // (Chaikin smoothing, length, closed-loop rejection) plus ExcavateCanyons and the spring-site query.
    // These produce ROUTES ONLY. The surface height on those routes is decided in
    // FiresWorldGenBase.RiverLevels.cs, and the trench is cut in FiresWorldGenBase.RiverCarve.cs.
    public static partial class FiresWorldGenBase
    {



        // ═══ EXCAVATE THE CANYON ══════════════════════════════════════════════════════════════════════════
        // (user, 2026-08-16: "we dig a canyon and if the bottom of the canyon hits y30 or below we dont even
        // need to render the river, the ocean level will handle it, if it sits above y30 we draw our water
        // mesh at the bottom".)
        // ⚠ THIS EXISTS BECAUSE THE v140 PROFILE COULD NOT WORK, AND THE REASON IS WORTH KEEPING: that pass
        // steepened the valley wall, and a steeper wall REMOVES LESS MATERIAL. It preserves relief where
        // relief exists and does nothing where it does not — measured at 0.5 m/px, a correctly sited canyon
        // sat on visibly smooth ground with no notch. A canyon is a HOLE. You cannot make one by shaving less.
        // The precedent is SeedMountainTarns, which digs its own parabolic cirque basin rather than hoping the
        // fill finds one (v104): a deliberate landform EXCAVATES itself.
        //
        // THE FLOOR IS CLAMPED AT SEA, which is the user's rule and also the ownership rule GEN_VERSION
        // 126/127 established: the ocean owns every column at or below the waterline, so a canyon that cuts
        // that deep simply stops there and the ocean plane draws it. Above SEA the floor is dry ground and the
        // ribbon/pool machinery downstream puts water on it.
        //
        // ⚠ MIN-WRITE, NO SCRATCH GRID. Overlapping discs take the LOWER target, so a reach digs one coherent
        // slot rather than a string of craters — and it costs no n² allocation, which matters because this
        // pass runs inside InciseRivers, already the memory ceiling of the whole generator (441 MB per n²
        // float at game res; the carve has hit OOM before).
        /// ═══ A CANYON IS A SLOT, NOT A STRING OF PITS ═══════════════════════════════════════════════════
        /// (2026-08-22, user: "canyons need to carve out a bit wider to allow for an actual river to run
        /// through the bottom of them"; and, on method, "we have built so many studio tools to allow you to
        /// visualize the water generation and you just guess and get it wrong over and over".)
        ///
        /// WHAT THE OLD PASS DID, SEEN RATHER THAN REASONED. `ClassifyChannelIncision` stamps `cls` as a
        /// CHAIN — cells sampled every half-cell along each polyline, one cell wide — which is correct. The
        /// excavation then walked those cells and dug, at EACH ONE, a radially symmetric DISC: reach 26 m,
        /// profile `Smooth01(0.80, 1, dist/reach)`, i.e. FLAT out to 20.8 m and a 5 m rim. That is a 41 m
        /// pan with a lip, and the union of a chain of them is a crater. Rendered with `watermesh` at the
        /// pass's own INCISION SITES it is unmistakable: two lobed pits, dry floors, water pooled in the
        /// lobes at different levels, float ringing the rim, and no channel through it. Both of the world's
        /// two classified reaches came out that way, and the pass was responsible for 105 of the 108
        /// remaining uphill rises and a third of all floating bank cells.
        ///
        /// WHAT IT DOES NOW. A canyon is anisotropic: narrow ACROSS the flow, continuous ALONG it. So the
        /// cross-section is stamped PERPENDICULAR to the local flow direction and marched along each
        /// segment, instead of a disc being dug per cell:
        ///   • the FLOOR is flat across `halfFlat`, sized to the CHANNEL that has to run down it (the river's
        ///     own half-width plus FLOOR_MARGIN) rather than to a fixed 80 % of the reach — this is the
        ///     user's "carve out a bit wider to allow for an actual river to run through the bottom";
        ///   • the WALLS rise from there to the rim over the remaining reach, so the slot has real sides;
        ///   • the floor is the SAME monotone running minimum from the headwater as before, so the slot only
        ///     ever descends and cannot pond mid-canyon.
        /// Marching the section along the segment (half-cell steps) is what makes it continuous: consecutive
        /// sections overlap along the flow, so there are no scallops between nodes and no pits.
        ///
        /// ⚠ OFF BY DEFAULT (`s_canyonExcavate`, `FIRES_CANYON_EXCAVATE=1` to enable) until this shape is
        /// measured to beat leaving it off. The canyons the player stands in are cut by the CARVE's incision
        /// bound, not by this pass; this is the hook for cutting them DELIBERATELY.
        static void ExcavateCanyons(float[] h, byte[] cls, int n, float cellM, int seed, float[] seeded, List<float[]> polys)
        {
            if (cls == null || polys == null) return;
            float rhalfE = (n - 1) * cellM * 0.5f;
            float WKc = WaterRules.RiverProc.WidthK, WPc = WaterRules.RiverProc.WidthP;
            float AOc = WaterRules.RiverProc.AreaOnset;
            float WMINc = WaterRules.RiverProc.WidthMin, WMAXc = WaterRules.RiverProc.WidthMax;
            const float FLOOR_MARGIN = 4f;      // dry bed either side of the water, so a river fits with banks
            const float FLOOR_MIN = 6f;         // ...and a slot is never narrower than this even for a brook

            // ── THE FLOOR DESCENDS FROM THE HEADWATER (unchanged) ────────────────────────────────────────
            // A running minimum along each spline in flow order, so the slot's floor can only ever go DOWN.
            // Without it the floor tracks local ground and a canyon ponds partway along — the "steps up to a
            // wide pool, then drops again" the user photographed.
            var floorAt = new Dictionary<int, float>(1 << 14);
            foreach (var poly in polys)
            {
                int nn = poly.Length / 4;
                if (nn < 2) continue;
                bool srcAtStart = poly[2] <= poly[(nn - 1) * 4 + 2];
                float mono = float.MaxValue;
                for (int idx = 0; idx < nn; idx++)
                {
                    int k = srcAtStart ? idx : nn - 1 - idx;      // walk HEADWATER -> END
                    int cx = Mathf.Clamp((int)((poly[k * 4] + rhalfE) / cellM + 0.5f), 0, n - 1);
                    int cz = Mathf.Clamp((int)((poly[k * 4 + 1] + rhalfE) / cellM + 0.5f), 0, n - 1);
                    int ci = cz * n + cx;
                    byte kc = cls[ci];
                    if (kc == INC_NONE) continue;                 // not a slot here — the chain resumes later
                    float dep = kc == INC_CANYON ? CANYON_DIG_M : GORGE_DIG_M;
                    float want = Math.Max(SEA, h[ci] - dep);
                    if (want < mono) mono = want;                 // …and it may only ever go DOWN
                    floorAt[ci] = mono;
                }
            }

            // ── STAMP A CROSS-SECTION ACROSS THE FLOW, MARCHED ALONG IT ──────────────────────────────────
            int sections = 0, cut = 0; double moved = 0; float deepest = 0f;
            foreach (var poly in polys)
            {
                int nn = poly.Length / 4;
                if (nn < 2) continue;
                for (int k = 0; k < nn; k++)
                {
                    int cx0 = Mathf.Clamp((int)((poly[k * 4] + rhalfE) / cellM + 0.5f), 0, n - 1);
                    int cz0 = Mathf.Clamp((int)((poly[k * 4 + 1] + rhalfE) / cellM + 0.5f), 0, n - 1);
                    int ci0 = cz0 * n + cx0;
                    byte kind = cls[ci0];
                    if (kind == INC_NONE) continue;
                    if (h[ci0] <= SEA) continue;                  // the sea owns its own columns

                    // Local flow direction by central difference — the section is perpendicular to THIS,
                    // which is the whole point: a disc has no direction and that is why it made pits.
                    int kp = k > 0 ? k - 1 : k, kn2 = k + 1 < nn ? k + 1 : k;
                    float dx0 = poly[kn2 * 4] - poly[kp * 4], dz0 = poly[kn2 * 4 + 1] - poly[kp * 4 + 1];
                    float dl = (float)Math.Sqrt(dx0 * dx0 + dz0 * dz0);
                    if (dl < 1e-4f) continue;
                    float ux = dx0 / dl, uz = dz0 / dl;           // along flow
                    float px = -uz, pz = ux;                      // across flow

                    float reach = kind == INC_CANYON ? CANYON_DIG_R : GORGE_DIG_R;
                    float wid = Math.Min(WMAXc, Math.Max(WMINc, WKc * (float)Math.Pow(Math.Max(poly[k * 4 + 2], AOc), WPc)));
                    float halfFlat = Math.Max(FLOOR_MIN, wid * 0.5f + FLOOR_MARGIN);
                    if (halfFlat > reach - 2f) halfFlat = Math.Max(1f, reach - 2f);   // always leave some wall

                    float floor = floorAt.TryGetValue(ci0, out float mf)
                                ? mf : Math.Max(SEA, h[ci0] - (kind == INC_CANYON ? CANYON_DIG_M : GORGE_DIG_M));

                    // A body's own floor wins — never dig a slot out from under a lake (unchanged rule).
                    if (seeded != null)
                    {
                        int brad = (int)((reach + 8f) / cellM + 0.5f);
                        float bodyFloor = float.MinValue;
                        for (int dz = -brad; dz <= brad && bodyFloor == float.MinValue; dz += 2)
                        {
                            int qz = cz0 + dz; if (qz < 1 || qz >= n - 1) continue;
                            for (int dx = -brad; dx <= brad; dx += 2)
                            {
                                int qx = cx0 + dx; if (qx < 1 || qx >= n - 1) continue;
                                int bj = qz * n + qx;
                                if (seeded[bj] > h[bj] + 0.01f) { bodyFloor = seeded[bj] - 1.0f; break; }
                            }
                        }
                        if (bodyFloor > floor) floor = bodyFloor;
                    }
                    if (h[ci0] - floor < 1f) continue;            // nothing to dig here

                    // March the section along this segment so consecutive sections OVERLAP — that is what
                    // makes a continuous slot instead of scallops between nodes.
                    float ax = poly[k * 4], az = poly[k * 4 + 1];
                    float bx = k + 1 < nn ? poly[(k + 1) * 4] : ax;
                    float bz = k + 1 < nn ? poly[(k + 1) * 4 + 1] : az;
                    float segL = (float)Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                    int steps = Math.Max(1, (int)(segL / (cellM * 0.5f) + 0.5f));
                    int uSteps = Math.Max(1, (int)(reach / (cellM * 0.5f) + 0.5f));
                    sections++;
                    for (int t = 0; t < steps; t++)
                    {
                        float f = t / (float)steps;
                        float sx = ax + (bx - ax) * f, sz = az + (bz - az) * f;
                        for (int s = -uSteps; s <= uSteps; s++)
                        {
                            float u = s * (cellM * 0.5f);
                            float au = Math.Abs(u);
                            if (au > reach) continue;
                            int qx = Mathf.Clamp((int)((sx + px * u + rhalfE) / cellM + 0.5f), 0, n - 1);
                            int qz = Mathf.Clamp((int)((sz + pz * u + rhalfE) / cellM + 0.5f), 0, n - 1);
                            if (qx < 1 || qx >= n - 1 || qz < 1 || qz >= n - 1) continue;
                            int j = qz * n + qx;
                            // A BODY OWNS THIS CELL — AND SO DOES ITS BANK. Skipping only the footprint left
                            // the pass free to cut the RIM that holds a lake in, and the water then stands
                            // above its own lowest dry bank: measured +88 float cells (BERM EARNED 294 -> 382)
                            // and a visible magenta cluster beside the slot in the render. A shore is one cell
                            // of footprint and several of bank, so the ring is protected too.
                            if (seeded != null)
                            {
                                bool nearBody = false;
                                for (int bz2 = -2; bz2 <= 2 && !nearBody; bz2++)
                                    for (int bx2 = -2; bx2 <= 2; bx2++)
                                    {
                                        int rz2 = qz + bz2, rx2 = qx + bx2;
                                        if (rz2 < 0 || rz2 >= n || rx2 < 0 || rx2 >= n) continue;
                                        int rj = rz2 * n + rx2;
                                        if (seeded[rj] > h[rj] + 0.01f) { nearBody = true; break; }
                                    }
                                if (nearBody) continue;
                            }
                            float rim = Math.Max(h[j], floor);
                            float tt = (au - halfFlat) / Math.Max(0.001f, reach - halfFlat);
                            if (tt < 0f) tt = 0f; else if (tt > 1f) tt = 1f;
                            float target = floor + (rim - floor) * Smooth01(0f, 1f, tt);
                            if (target < h[j])
                            {
                                moved += h[j] - target;
                                if (h[j] - target > deepest) deepest = h[j] - target;
                                h[j] = target; cut++;
                            }
                        }
                    }
                }
            }
            FiresLogger.LogInfo($"[FiresWorldGen]  │  CANYON EXCAVATION — {sections:N0} cross-section(s) stamped ACROSS the flow and marched along it (was: one radially-symmetric DISC per classified cell, which is what made craters). {cut:N0} cell(s) lowered, mean cut {(cut > 0 ? moved / cut : 0):0.0} m, deepest {deepest:0.0} m. Floor flat to the channel's own width + {FLOOR_MARGIN:0} m so a river fits down the bottom, walls over the rest of the reach. Dug BEFORE hOrig is taken, so every pass downstream treats the slot as natural ground.");
        }

        /// GLSL smoothstep — a smoothed inverse-lerp. Spelled out rather than using Mathf.SmoothStep, which
        /// INTERPOLATES and is a different function sharing a name (GEN_VERSION 102 lost a session to that).
        static float Smooth01(float e0, float e1, float x)
        {
            if (e1 <= e0) return x < e0 ? 0f : 1f;
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        /// Length of a [x,z,area,level] polyline in metres.
        static float PolyLengthM(float[] p)
        {
            float len = 0f;
            for (int k = 1; k < p.Length / 4; k++)
            {
                float ax = p[k * 4] - p[(k - 1) * 4], az = p[k * 4 + 1] - p[(k - 1) * 4 + 1];
                len += (float)Math.Sqrt(ax * ax + az * az);
            }
            return len;
        }

        /// Does this polyline travel a real distance and end up back where it started?
        /// ⚠ THE TEST IS PATH LENGTH THAT GOES NOWHERE, NOT MERELY CLOSE ENDS. Asking only "are the two ends
        /// within 2 cells" is what a first version did, and every SHORT poly passes that trivially — it
        /// reported 13,534 loops (390 km) whose largest was 50 nodes, i.e. ~4 raw cells before Chaikin's 4
        /// subdivisions. It was counting stubs. With the length conditions the same world reports 252 (32.8 km).
        /// Same class of error as the flat audit counting submerged lake beds as land: an instrument that
        /// flatters the hypothesis is the one to distrust.
        static bool IsClosedLoop(float[] p, float cellM)
        {
            int m = p.Length / 4;
            if (m < 8) return false;
            float dx = p[0] - p[(m - 1) * 4], dz = p[1] - p[(m - 1) * 4 + 1];
            float endSep = (float)Math.Sqrt(dx * dx + dz * dz);
            if (endSep > 2f * cellM) return false;
            float len = PolyLengthM(p);
            return len >= 100f && len >= 6f * Math.Max(endSep, 1f);
        }




        // ── river spring placement (v10.1) ──────────────────────────────────




        // shards (summit flanks, seed 220092304). Split every polyline — plan runs AND derived outlets — at
        // cascade nodes, keep sane sub-reaches (≥ MIN_KEEP), drop the steep segments entirely: no rasterize,
        // no carve, natural rock face between lip and resume. Plunge pools come FREE: each sub-reach head is
        // a polySource, and the v93 rim gate digs a pool exactly where the bench below can hold one.
        private static void CascadeSplitPolys(List<float[]> polys, float[] hOrig, int n, float cellM)
        {
            lock (s_cascadeSpans) { s_cascadeSpans.Clear(); s_cascadeDroppedM = 0f; }   // per-world: this run's falls only
            if (polys == null) return;
            // (2026-08-11, user: "we are still just drying between river sections for no goddamn reason")
            // 0.25 → 1.00. The old threshold called a 25% grade (14°) a waterfall and DELETED the reach —
            // but a real river runs continuously down 14°; that is a riffle, not a fall. At full res it was
            // dropping 12,764 spans per world, i.e. 12,764 dry carved trenches between wet sections, and the
            // gap scan could never see them (cascade removes the span from the polyline, so the hole is not
            // "interior" to any run). 1.00 = 45°: only a genuine cliff loses its sheet, everything gentler
            // keeps continuous water. Dropped spans are still recorded + stamped WC_WATERFALL for FX.
            const float CASCADE_SLOPE = 1.00f;
            const int MIN_KEEP = 16;             // (v104) a kept bench must be a real reach — 8-node slivers between cascade flickers carved terrace bands down mountainsides (seed 49185460)
            const int CASC_DILATE = 6;           // (v104) swallow the flicker fringe: a steep face is ONE dry cascade, not alternating benches
            // A waterfall is a SUSTAINED face. Below this the steep reading is terrain noise on a single node
            // pair, and the dilation above would turn it into a 13-node hole that severs the river. See the
            // open pass below for the measurement that set it.
            const float CASCADE_MIN_M = 12f;
            float rhalf = (n - 1) * cellM * 0.5f;
            int splitN = 0, droppedSeg = 0;
            // DIAGNOSTIC (2026-08-17): this pass logged NOTHING when it did nothing, so "0 cascade spans" in a
            // shipped world was invisible until someone grepped for a MISSING line. Silence is not evidence.
            long pairsTested = 0, over025 = 0, over050 = 0, over100 = 0; double distSum = 0;
            float maxGrade = 0f; int runsShort = 0, runsAnySteep = 0;
            for (int p = polys.Count - 1; p >= 0; p--)
            {
                var poly = polys[p];
                int nn = poly.Length / 4;
                if (nn < 2) continue;
                var casc = new bool[nn];
                bool any = false;
                const int END_GUARD = 10;   // (v100) JUNCTION/APPROACH GUARD — the descent into a body's basin is
                                            // often the steepest reach of the whole run; cutting it severed the very
                                            // connection the run exists to make (rivers stopping short of the next
                                            // body, seed 724107467). Run ends never cascade-cut: a short steep chute
                                            // INTO a lake is a rapids entry — real. Mid-run cascades split as before.
                for (int k = 0; k < nn - 1; k++)
                {
                    if (k < END_GUARD || k >= nn - 1 - END_GUARD) continue;
                    float ax = (poly[k * 4] + rhalf) / cellM, az = (poly[k * 4 + 1] + rhalf) / cellM;
                    float bx = (poly[(k + 1) * 4] + rhalf) / cellM, bz = (poly[(k + 1) * 4 + 1] + rhalf) / cellM;
                    float ga = SampleHOrig(hOrig, n, ax, az), gb = SampleHOrig(hOrig, n, bx, bz);
                    float dist = Mathf.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)) * cellM;
                    if (dist < 0.5f) dist = 0.5f;
                    float gr = Mathf.Abs(gb - ga) / dist;
                    pairsTested++; distSum += dist;
                    if (gr > maxGrade) maxGrade = gr;
                    if (gr > 0.25f) over025++;
                    if (gr > 0.50f) over050++;
                    if (gr > 1.00f) over100++;
                    if (gr > CASCADE_SLOPE) { casc[k] = true; casc[k + 1] = true; any = true; }
                }
                if (nn <= 2 * END_GUARD + 2) runsShort++;
                if (!any) continue;
                runsAnySteep++;
                // ═══ OPEN THE MASK BEFORE DILATING IT — A LONE STEEP SAMPLE IS NOISE, NOT A CLIFF ═══════════
                // ⚠ THIS IS THE THIRD TIME FIRST-ORDER STREAMS HAVE BROKEN A RULE TUNED FOR TRUNK RIVERS.
                // GEN_VERSION 132 recorded two (the bank feather's seeding, and absolute-height writes) with
                // the note that the carve "was tuned when a world had a few hundred rivers and now has
                // ~19,000". This is the same break in the cascade: the grade test fires on a SINGLE node pair,
                // and the DILATE below then expands that one sample into a 13-node dry span — so one noisy
                // sample deletes ~13 nodes of river AND SPLITS THE RUN IN TWO. When channels only existed in
                // valleys that was rare; now that HEAD_A initiates them high on mountainsides it is constant.
                // MEASURED, seed 530442569 n=4097, before this filter: 19,944 drop spans totalling 112,690 m —
                // a MEAN SPAN OF 5.6 m, i.e. one node pair each — which took the network from 3,912 runs /
                // 414.9 km to 9,492 runs / 290.7 km. Thirty per cent of every river in the world deleted, and
                // the median run halved from 41 m to 16 m, by an accumulation of single-sample flickers.
                // A real waterfall is a SUSTAINED near-vertical face. Requiring the steep condition to hold
                // over CASCADE_MIN_M of run keeps every genuine cliff (where many consecutive pairs are steep,
                // which is exactly the "flicker fringe" case v104's dilation was built for) and discards the
                // isolated sample. Erode-then-dilate is the standard morphological open; doing it in the other
                // order — which is what the code did by having no open at all — is what let noise through.
                {
                    int s2 = 0;
                    while (s2 < nn)
                    {
                        if (!casc[s2]) { s2++; continue; }
                        int e3 = s2;
                        while (e3 + 1 < nn && casc[e3 + 1]) e3++;
                        float spanM = 0f;
                        for (int k = s2; k < e3; k++)
                        {
                            float dx = poly[(k + 1) * 4] - poly[k * 4], dz = poly[(k + 1) * 4 + 1] - poly[k * 4 + 1];
                            spanM += Mathf.Sqrt(dx * dx + dz * dz);
                        }
                        if (spanM < CASCADE_MIN_M) for (int k = s2; k <= e3; k++) casc[k] = false;
                        s2 = e3 + 1;
                    }
                    any = false;
                    for (int k = 0; k < nn; k++) if (casc[k]) { any = true; break; }
                    if (nn <= 2 * END_GUARD + 2) runsShort++;
                if (!any) continue;
                runsAnySteep++;
                }
                // (v104) DILATE the cascade mask so a steep face is ONE continuous dry cascade — the raw
                // mask flickers around the threshold and MIN_KEEP kept every flicker bench: carve+fill+berm
                // bands terraced whole mountainsides (seed 49185460).
                {
                    var cd = new bool[nn];
                    for (int k = 0; k < nn; k++)
                    {
                        if (!casc[k]) continue;
                        int lo2 = Math.Max(0, k - CASC_DILATE), hi2 = Math.Min(nn - 1, k + CASC_DILATE);
                        for (int j = lo2; j <= hi2; j++) cd[j] = true;
                    }
                    casc = cd;
                }
                var subs = new List<float[]>(4);
                int s0 = -1;
                for (int k = 0; k <= nn; k++)
                {
                    bool cut = k == nn || casc[k];
                    if (!cut) { if (s0 < 0) s0 = k; continue; }
                    if (s0 >= 0)
                    {
                        int len = k - s0;
                        // NEVER drop the sub-reach that touches a run END — the FIRST (s0==0, the source/upstream
                        // body) or the LAST (k==nn, the downstream body/sea). END_GUARD stops a cascade being
                        // DETECTED in the last/first 10 nodes, but CASC_DILATE can bleed a cascade detected just
                        // outside the guard INTO the approach, and MIN_KEEP then dropped that short tail → the
                        // channel ended short of its body ("rivers stop short of merging", user 2026-08-09). Keeping
                        // the end sub-reach reconnects the river to the body; it's a flat backwater approach, not a
                        // mid-mountain sliver, so it can't terrace (the case MIN_KEEP exists for).
                        bool touchesEnd = s0 == 0 || k == nn;
                        if (len >= MIN_KEEP || touchesEnd)
                        {
                            var np = new float[len * 4];
                            Array.Copy(poly, s0 * 4, np, 0, len * 4);
                            subs.Add(np);
                        }
                        else { droppedSeg++; RecordCascadeSpan(poly, s0, len); }   // a dropped sliver is still a real drop — keep it as FX data
                        s0 = -1;
                    }
                }
                // The CASCADE-MASKED spans themselves are the actual falls (never emitted as sub-reaches).
                // Record each contiguous run so the drop is DATA, not a hole in the river.
                {
                    int c0 = -1;
                    for (int k = 0; k <= nn; k++)
                    {
                        bool isC = k < nn && casc[k];
                        if (isC) { if (c0 < 0) c0 = k; }
                        else if (c0 >= 0) { RecordCascadeSpan(poly, c0, k - c0); c0 = -1; }
                    }
                }
                polys.RemoveAt(p);
                for (int s = 0; s < subs.Count; s++) polys.Add(subs[s]);
                splitN++;
            }
            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: CASCADE INPUTS — {polys.Count:N0} run(s); {runsShort:N0} too short for the END_GUARD({10}) window to leave ANY testable node. Node pairs tested {pairsTested:N0}, mean spacing {(pairsTested > 0 ? distSum / pairsTested : 0):0.00} m. Grade > 0.25: {over025:N0} · > 0.50: {over050:N0} · > 1.00 (CASCADE_SLOPE): {over100:N0}; MAX grade seen {maxGrade:0.00}. Runs with any steep pair {runsAnySteep:N0}. ⚠ A span must ALSO be >= CASCADE_MIN_M(12) m long ALONG THE CHANNEL to survive — a real waterfall lip is only a few metres long horizontally, so that minimum is the likely binding constraint if splits are 0 while max grade is high.");
            if (splitN > 0)
                FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: CASCADE SEGMENTATION — {splitN} run(s) split at steep reaches (grade > {CASCADE_SLOPE:0.00}): sheet ends at the lip, resumes below; {droppedSeg} short remnant(s) dropped. {s_cascadeSpans.Count} drop span(s) = {s_cascadeDroppedM:N0}m of river DELETED (recorded + stamped WC_WATERFALL for FX — this is the length that renders as a dry carved trench, so it must stay small).");
        }

        // ── CASCADE (waterfall/rapids) SPANS ────────────────────────────────────────────────────────
        // (user 2026-08-11) "we want to hide the mesh in these spots but we still have to mark the area as
        // waterfall or rapids so we know where to place the fx." A steep reach gets NO flat sheet (a ribbon
        // can't drape a drop) — but deleting it outright threw the information away: the world was left with
        // an unexplained hole in the river and nothing downstream could tell a designed fall from a bug.
        // Every dropped span is recorded here in world space; BuildReconciledSurface stamps those cells
        // WC_WATERFALL (dry — no sheet, mesh still hidden) so the FX pass has its placement list and the
        // gap diagnostics can tell "waterfall here" apart from "river broken here".
        private static readonly List<float[]> s_cascadeSpans = new List<float[]>();   // each: x,z pairs along the drop
        public static IReadOnlyList<float[]> CascadeSpans => s_cascadeSpans;

        private static float s_cascadeDroppedM;   // total river length deleted as "cascade" — the real cost, in metres

        private static void RecordCascadeSpan(float[] poly, int start, int len)
        {
            if (len < 1) return;
            var span = new float[len * 2];
            float runM = 0f;
            for (int k = 0; k < len; k++)
            {
                float x = poly[(start + k) * 4], z = poly[(start + k) * 4 + 1];
                span[k * 2] = x; span[k * 2 + 1] = z;
                if (k > 0)
                {
                    float dx = x - span[(k - 1) * 2], dz = z - span[(k - 1) * 2 + 1];
                    runM += Mathf.Sqrt(dx * dx + dz * dz);
                }
            }
            lock (s_cascadeSpans) { s_cascadeSpans.Add(span); s_cascadeDroppedM += runM; }
        }


        // ── RIVER INCISION — carve the traced network into the terrain and
        // finalize the water field. Consumes the drainage analysis (fill table
        // `w` + `riverPolys`); recomputes the cheap cell metrics locally. Called
        // back-to-back after AnalyzeDrainage this reproduces the old CarveRivers.
        // `seeded` (nullable): pre-incision water surface holding lakes/swamps placed
        // BEFORE incision (reorder) — banks are NOT raised where a cell is under it,
        // and it is preserved through the final dry pass (the river blends into the
        // body instead of levee'ing across it). `existingMask` (nullable): a mask to
        // merge river cells INTO (keeps mountain-stream cells) instead of a fresh one.
        // Both null → the old CarveRivers behaviour exactly (imports/cache).
        // (v94) THE PLAN'S CONNECTIONS ARE THE RIVERS (user: placement from the PLAN, carve from
        // InciseRivers). The plan segments every validated trace at body boundaries into runs with
        // PROVENANCE — FromBody {body | SPRING} → ToBody {body | OCEAN} — so every river connects water
        // to water: body→body, body→sea, or spring→…, where a SPRING head is realized as a REAL spring
        // (user spec): the headwater-pool pass digs a small pond — carved indentation, flat sheet — at
        // the head, gated by the v93 rim justification (gentle ground → spring pond; steep → a bare
        // seep/channel head). Convert the runs to InciseRivers' poly format: [x, z, AREA, level] — the
        // plan stores WIDTH per node, so back-solve the equivalent catchment area (inverse of the
        // discharge power law) and the rasterizer reproduces the plan's width exactly. Levels in slot 3
        // are overwritten by SetBankDerivedLevels (bank-derived ribbon + body anchor) — the plan decides
        // WHERE, the ribbon machinery decides the surface.
        private static List<float[]> PlanConnectionsToPolys(FiresWaterPlan plan, float[] seeded, float[] g,
                                                            int n, float cellM, out int springRuns, out int culledSprings)
        {
            springRuns = 0; culledSprings = 0;
            if (plan == null || plan.Connections == null || plan.Connections.Count == 0) return null;
            float WK = WaterRules.RiverProc.WidthK, WP = WaterRules.RiverProc.WidthP;
            float AO = WaterRules.RiverProc.AreaOnset;
            float rhalf = (n - 1) * cellM * 0.5f;
            // (v97) JUNCTION NODES — EmitConnection cuts runs at the body boundary (first node OUTSIDE),
            // so converted polys carried NO in-body nodes: the anchor never fired, distA stayed ∞, and the
            // v96 taper NEVER ENGAGED for plan runs — every junction took the full channel drop at the
            // shore (the height step + the widened dry collar, seed 220092304). The plan KNOWS the
            // junction bodies (FromBody/ToBody), so when an end abuts a real body we find the adjacent
            // seeded cell and add it as an explicit node — the anchor then pins it to the body surface and
            // the taper eases the drop in/out exactly as designed. Confined to run ENDS: no mid-run
            // anchoring on near-miss shores.
            // ═══ THE JUNCTION SNAPS TO WATER THAT EXISTS, ACROSS THE PLAN'S DRY RIM (2026-08-13) ═════════
            // (user: "it's the same reason rivers used to stop short of connecting bodies of water — as far as
            // the map was concerned they DID connect, so the river would end where it thought the still water
            // started, but it would be many metres away".) This function is already right in kind: it looks for
            // a cell that is ACTUALLY WET (seeded > g), not merely inside a footprint. The defect was REACH.
            // A plan footprint is the flood's full extent, but conformance/incision leave part of it dry —
            // measured at 43,168 footprint cells "SKIPPED as dry in the field" on this seed. Where that dry rim
            // is wider than the search, this returns −1, NO junction node is emitted, and the run simply stops
            // at the phantom edge. That is the stop-short defect, and it is invisible to any audit built on the
            // class map because the map agrees the water is there.
            // TWO fixes: reach far enough to cross a real dry rim, and measure that reach in METRES. The radius
            // was in CELLS, so it meant 6m at game res (n=10501, 2m cells) and 31m in a 2049 studio run — the
            // search behaved differently depending on resolution, which is its own studio-vs-game divergence.
            // 24m is bounded on purpose: wide enough to cross the rim, far too short to jump to another body.
            int seedSnapR = Math.Max(3, (int)(24f / Math.Max(0.01f, cellM) + 0.5f));
            int FindSeededNear(float wx, float wz)
            {
                if (seeded == null || g == null) return -1;
                int cx = Mathf.Clamp((int)((wx + rhalf) / cellM + 0.5f), 1, n - 2);
                int cz = Mathf.Clamp((int)((wz + rhalf) / cellM + 0.5f), 1, n - 2);
                for (int r = 0; r <= seedSnapR; r++)
                    for (int dz = -r; dz <= r; dz++)
                        for (int dx = -r; dx <= r; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;   // ring only
                            int x = cx + dx, z = cz + dz;
                            if (x < 1 || x >= n - 1 || z < 1 || z >= n - 1) continue;
                            int i = z * n + x;
                            if (seeded[i] > g[i] + 0.01f) return i;
                        }
                return -1;
            }
            var outPolys = new List<float[]>(plan.Connections.Count);
            var buf = new List<float>(1024);
            foreach (var c in plan.Connections)
            {
                int count = c.Nodes != null ? c.Nodes.Length / 4 : 0;
                if (count < 2) continue;
                if (c.FromBody == WaterConnection.SPRING)
                {
                    // (v99) SPRING-SITE CULL (user: "river culling for spots that shouldn't have a river is
                    // broken") — a SPRING is the run's SOURCE, and a spring is a small pond (user spec). If
                    // the head site cannot HOLD a pond (same rim rule as v93, as ground relief across the
                    // pool ring), there is no spring — and with no source there is NO RIVER: cull the WHOLE
                    // run. The half-generated hillside garbage (no pool bowl, patchy bound-clamped carve,
                    // floating sheet shards — seed 220092304) was exactly a river downstream of a spring
                    // that was refused. Gentle heads keep their pond + river.
                    if (g != null)
                    {
                        float prM = Math.Max(9f, c.Nodes[2] * 1.4f);      // pool radius from the head's plan width
                        float hx = c.Nodes[0], hz = c.Nodes[1];
                        float gMin = float.MaxValue, gMax = float.MinValue;
                        for (int rs = 0; rs < 8; rs++)
                        {
                            float ang = rs * 0.7853982f;
                            int sx = Mathf.Clamp((int)((hx + Mathf.Cos(ang) * prM + rhalf) / cellM + 0.5f), 0, n - 1);
                            int sz = Mathf.Clamp((int)((hz + Mathf.Sin(ang) * prM + rhalf) / cellM + 0.5f), 0, n - 1);
                            float gv = g[sz * n + sx];
                            if (gv < gMin) gMin = gv; if (gv > gMax) gMax = gv;
                        }
                        if (gMax - gMin > 2.2f) { culledSprings++; continue; }
                    }
                    springRuns++;
                }
                buf.Clear();
                float AreaOf(int i) => Math.Max(AO * 1.01f, (float)Math.Pow(c.Nodes[i * 4 + 2] / WK, 1.0 / WP));   // width → equivalent area
                if (c.FromBody >= 0)
                {
                    int j = FindSeededNear(c.Nodes[0], c.Nodes[1]);
                    if (j >= 0) { buf.Add((j % n) * cellM - rhalf); buf.Add((j / n) * cellM - rhalf); buf.Add(AreaOf(0)); buf.Add(seeded[j]); }
                }
                // ═══ A RIVER ENDS AT THE OCEAN — IT DOES NOT RUN ACROSS IT (2026-08-13) ══════════════════
                // (user: "we're not ending rivers at the oceans, we're just running through/over them"; "our
                // detection of the ocean is not as sane as our detection of other bodies of water"; "we're
                // using bad data to find the ocean level".) Exactly right, and it is a DATA-SHAPE problem:
                // every other destination is a planned WaterBody with a real footprint and a real Level, so a
                // run terminates on it properly — FindSeededNear plants a junction node on genuinely wet
                // ground, the anchor pins it, the taper eases into it, the backwater curve holds it level.
                // The OCEAN is not a body at all. `EmitConnection` just sets lEnd = ESTUARY_FLOOR (the bare
                // constant SEA−6) and keeps emitting nodes, so the polyline marches on across the seabed with
                // nothing to stop it — and the carve, the fill and the sheet all follow it out to sea. That is
                // the fleet of ribbons standing over open water in the user's screenshot.
                // The ocean's shoreline IS well-defined data we already have: the high-tide waterline, where
                // ground meets SEA (TideHighOffsetM = 0, so the sea never rises past it). Cut the run there
                // and pin that last node AT the waterline — the same contract a lake-bound run gets, and the
                // "dive the mesh into the sand line and end" the user asked for. Everything seaward of it is
                // the ocean's, at every tide.
                bool toOcean = c.ToBody == WaterConnection.OCEAN;
                int added = 0;
                for (int i = 0; i < count; i++)
                {
                    float nx = c.Nodes[i * 4], nz = c.Nodes[i * 4 + 1];
                    if (toOcean && added >= 2 && g != null)
                    {
                        int gx = Mathf.Clamp((int)((nx + rhalf) / cellM + 0.5f), 0, n - 1);
                        int gz = Mathf.Clamp((int)((nz + rhalf) / cellM + 0.5f), 0, n - 1);
                        // ═══ SHORTEST ROUTE TO THE SEA — NOT A RUN ALONG THE BEACH (2026-08-13) ══════════
                        // (user: "we should be taking the shortest route to the ocean body, not running
                        // parallel to the shore like this… and our containment logic gets broken there where
                        // it's running along the edge sideways".) Testing only THIS node's ground misses that
                        // completely: a trace hugging the coast at, say, 30.5 never dips below SEA, so it was
                        // never cut and simply ran on beside the water for hundreds of metres — carving a
                        // channel along the beach, which is also why containment fell apart there (a ribbon
                        // running along a shore has the sea on one side and no bank to hold it).
                        // A river reaching the coast is DONE. Finding the ocean is not hard — it is every
                        // column whose ground lies under SEA — so the mouth is the first node from which the
                        // sea is within MOUTH_REACH, and the run ends there pinned to the waterline. What was
                        // a long shore-parallel tail becomes a short dive into the sea.
                        // Find the NEAREST sea cell, not merely whether one is close. Truncating the run at
                        // "the sea is within reach" would end the river up to MOUTH_REACH short of the water
                        // — which is the stop-short defect all over again, just against the ocean instead of
                        // a lake. The run must REACH the sea: we take the closest sub-SEA column and finish
                        // there, so the mouth turns and dives in by the shortest path available.
                        int R = Math.Max(1, (int)(30f / Math.Max(0.01f, cellM) + 0.5f));
                        int seaX = -1, seaZ = -1; long bestD2 = long.MaxValue;
                        for (int dz = -R; dz <= R; dz++)
                        {
                            int sz = gz + dz;
                            if (sz < 0 || sz >= n) continue;
                            for (int dx = -R; dx <= R; dx++)
                            {
                                int sx = gx + dx;
                                if (sx < 0 || sx >= n) continue;
                                if (g[sz * n + sx] > SEA) continue;
                                long d2 = (long)dx * dx + (long)dz * dz;
                                if (d2 < bestD2) { bestD2 = d2; seaX = sx; seaZ = sz; }
                            }
                        }
                        if (seaX >= 0)
                        {
                            // the mouth is the WATERLINE cell itself, pinned to sea level: the ribbon runs to
                            // the sea and stops dead there, and everything seaward belongs to the ocean.
                            buf.Add(seaX * cellM - rhalf); buf.Add(seaZ * cellM - rhalf);
                            buf.Add(AreaOf(i)); buf.Add(SEA);
                            added++;
                            break;
                        }
                    }
                    buf.Add(nx); buf.Add(nz);
                    buf.Add(AreaOf(i)); buf.Add(c.Nodes[i * 4 + 3]);
                    added++;
                }
                if (c.ToBody >= 0)
                {
                    int j = FindSeededNear(c.Nodes[(count - 1) * 4], c.Nodes[(count - 1) * 4 + 1]);
                    if (j >= 0) { buf.Add((j % n) * cellM - rhalf); buf.Add((j / n) * cellM - rhalf); buf.Add(AreaOf(count - 1)); buf.Add(seeded[j]); }
                }
                // (TRACE 2026-08-19) WHY IS 81 % OF THE NETWORK ANCHORED TO NOTHING? A run is pinned to real
                // water only where FindSeededNear plants a junction node, and both calls are gated on
                // `>= 0` — while SPRING is −1 and OCEAN is −2. The segmentation labels `toBody = OCEAN` for
                // ANY run whose trace merely ran out in open country, not only for one that reached the sea,
                // so those runs get no downstream anchor at all; paired with a SPRING head that is a run tied
                // to nothing at either end, free to invent its own surface. Count the shapes.
                if (c.FromBody >= 0 && c.ToBody >= 0) s_runBothEnds++;
                else if (c.FromBody >= 0 || c.ToBody >= 0) s_runOneEnd++;
                else s_runNoEnds++;
                if (c.ToBody == WaterConnection.OCEAN) s_runToOcean++;
                if (c.FromBody == WaterConnection.SPRING) s_runFromSpring++;
                outPolys.Add(buf.ToArray());
            }
            FiresLogger.LogInfo($"[FiresWaterDiag] RUN PROVENANCE: {s_runBothEnds:N0} run(s) tied to a real body at BOTH ends, {s_runOneEnd:N0} at ONE end, "
                    + $"{s_runNoEnds:N0} at NEITHER. (head=SPRING {s_runFromSpring:N0}, tail=OCEAN {s_runToOcean:N0} — and `toBody = OCEAN` is assigned "
                    + "to every run whose trace simply ENDED, not only to one that reached the sea.) SPRING is −1 and OCEAN is −2, and both "
                    + "FindSeededNear anchor calls are gated on `>= 0`, so every run in the last two columns is pinned to no water at that end "
                    + "and SetBankDerivedLevels invents its level from two ground samples.");
            return outPolys;
        }

        // (v99) CASCADE SEGMENTATION — the sheet-on-steep invariant (docs/HYDROLOGY.md Part V + the locked
        // waterfall rule: NO sheet on a steep drop — the sheet ends at the LIP and resumes at the PLUNGE
        // POOL; the drop itself is VFX). A reach whose ground falls faster than CASCADE_SLOPE cannot hold a
        // ribbon: the carve's local bound + the fill gate fight cell-by-cell there and emit patchy floating
        // The smallest fall the outlet walk will accept as REAL descent. PriorityFloodFill is epsilon-filled
        // (`lvl + 0.0002f` per expanded cell), so anything at or below that scale is the flood's bookkeeping,
        // not the landscape — and descending bookkeeping is what drew kilometres of dead-straight channel.
        const float FILL_EPS_FLAT = 0.005f;

        // Depth of fill-over-ground at which a column stops being land the walk may carve and becomes a water
        // body it must stop at. Half a metre is unambiguously standing water and well clear of the 0.0002
        // epsilon and of ordinary carve/detail noise.
        const float STANDING_WATER = 0.5f;

        // Largest closed basin treated as terrain NOISE rather than water. 64 cells = 256 m² at 2 m. Read off the
        // basin histogram (seed 530442569): <=64 cells is 90.7% of all basins and 1.8% of the filled area, while
        // 423 basins over 4096 cells hold 87.9% — the distribution separates cleanly, so this is a measurement
        // rather than a taste. Raise it only with the histogram in front of you.
        // ⚠ AN AREA SINCE v135, for the reason spelled out over FiresWaterPlanner's size gates: resolution is
        // player-selectable, so a raw cell count made the same seed a different world at each setting. 64 cells
        // at the game's 2 m grid IS 256 m², and this comment already said so.
        const float POTHOLE_MAX_AREA_M2 = 256f;

        // Wetland CLASSIFICATION (v133 — it no longer sets a level; see ClassifyWetlands). Grade threshold read
        // off the census, not chosen: basins above 0.20 average ~22 m deep on both seeds measured (real lakes),
        // below it 6.5-6.9 m (wetland candidates). MEAN depth above which a basin is a lake regardless of how
        // flat its floor is (Tier B in the spec) — the census put grade<0.20 basins at 6.5-6.9 m mean, so 10 m
        // keeps the deeper tail as real water.
        const float WETLAND_MAX_MEAN_DEPTH = 10f;
        const float WETLAND_MAX_GRADE = 0.20f;
        // ⚠ SIZE FLOOR = THE MARSH FLOOR, DELIBERATELY. Claiming a basin here takes it away from the lake
        // funnel, so claiming one the marsh funnel is too small to place would delete the water outright. It
        // was 64 cells, which is a puddle: a basin that small is a POND and the lake funnel should keep it.
        // ⚠ MUST TRACK FiresWaterPlanner.MARSH_MIN_AREA_M2 — the two gates are the same statement about what
        // counts as a marsh, made in two files, and if they disagree this one claims basins the planner will
        // not place. Both became AREAS in v135 so they cannot drift with the player's resolution setting.
        const float WETLAND_MIN_AREA_M2 = 10_000f;

    }
}
