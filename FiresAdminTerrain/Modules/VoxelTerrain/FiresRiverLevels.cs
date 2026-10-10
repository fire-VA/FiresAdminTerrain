using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══ RIVER LEVELS — THE ONE PLACE A RIVER'S SURFACE HEIGHT IS DECIDED ═══════════════════════════════
    // (user, 2026-09: "WE PLACE STILLWATER FIRST WE SHOULD BE ABLE TO DRAW THE FUCKING CONNECTIONS AT THE
    // CORRECT HEIGHTS NOT FUCKING RE PASS TO FIX THEM LATER"; and "part of the problem is it shouldnt BE a
    // 14k line file … it should be broken up into manageable sections".)
    //
    // This file replaced ~250 lines of shaping that ran AFTER the levels were already correct and un-knew
    // them: the weir max(sm,stair), the CHANNEL_DROP taper, the junction blend, the slope relief, the
    // isotonic experiment, the containment re-clamp, two monotone re-clamps, and the draw-contract repair
    // block that existed only to undo the damage of the others. Measured before the rewrite: 104,934
    // river↔still joins averaging 0.21 m apart at the exact places the plan had already solved to 0.
    //
    // THE LAW, once, final:
    //   • the SOURCE body's level is the ceiling — water does not climb leaving a body;
    //   • descend only, hugging the local containment cap (lowest bank − relief; in a canyon, the floor
    //     rule) — the CARVE makes the ground fit the level, never the reverse;
    //   • never below the DESTINATION body's level — water does not climb arriving either;
    //   • nodes inside a body's own footprint carry that body's surface exactly;
    //   • the tail node IS the destination level — the two sheets TOUCH. A run arriving high keeps its
    //     lip: a waterfall into a lake is physical and wanted. It may not arrive low.
    // Everything downstream of this file consumes these numbers; nothing edits them.
    public static partial class FiresWorldGenBase
    {
        private static void SetBankDerivedLevels(List<float[]> polys, float[] hOrig, int n, float cellM, float[] seeded, float[] fill, byte[] incCls)
        {
            if (polys == null) return;
            const float RELIEF = 0.84f;     // ribbon sits this far under the lowest bank
            float rhalf = (n - 1) * cellM * 0.5f;
            float WK = WaterRules.RiverProc.WidthK, WP = WaterRules.RiverProc.WidthP, AO = WaterRules.RiverProc.AreaOnset;
            float WMIN = WaterRules.RiverProc.WidthMin, WMAX = WaterRules.RiverProc.WidthMax;
            foreach (var poly in polys)
            {
                int nn = poly.Length / 4;
                if (nn < 2) continue;
                var lvl = new float[nn];
                var anchored = new bool[nn];   // node under a seeded body — carries the body surface, exempt from the channel drop
                var inCanyon = new bool[nn];   // node in a slot — takes the SMOOTH profile, not the weir staircase (see the restore below)
                var anchorLvl = new float[nn]; // the body surface at each anchored node — restored HARD after every shaping pass
                // ═══ THE CONTAINMENT CEILING — WHAT THIS NODE'S OWN BANKS CAN ACTUALLY HOLD ══════════════
                // (2026-08-19, user: "the only reason we currently raise the green line is because you cannot
                // seem to get the red and blue line to place right".) The FIRST value computed below is
                // already the physically correct ceiling — lowest bank minus relief — and if the level simply
                // stayed at or under it, water could never stand above its own banks and the berm would have
                // nothing to catch. It does not stay under it: of the ten longitudinal passes that follow,
                // `max(sm, stair)` and the junction blend can both push a node
                // UP, and none of them re-checks the local ground. A node lifted past this ceiling is water
                // standing above its bank — floating — and the berm exists to bury exactly that.
                // Kept per node so the last thing this function does is put the ceiling back on.
                var cap = new float[nn];
                for (int k = 0; k < nn; k++)
                {
                    float cx = (poly[k * 4] + rhalf) / cellM, cz = (poly[k * 4 + 1] + rhalf) / cellM;
                    float area = poly[k * 4 + 2];
                    float width = Mathf.Min(WMAX, Mathf.Max(WMIN, WK * Mathf.Pow(Mathf.Max(area, AO), WP)));
                    float edge = Mathf.Max(1.5f, width * 0.5f) / cellM + 1f;   // channel edge in cells (+1 to clear the carve blend) — the width we cut
                    int kp = Mathf.Max(0, k - 1), kn = Mathf.Min(nn - 1, k + 1);
                    float fdx = (poly[kn * 4] - poly[kp * 4]) / cellM, fdz = (poly[kn * 4 + 1] - poly[kp * 4 + 1]) / cellM;
                    float fl = Mathf.Sqrt(fdx * fdx + fdz * fdz); if (fl < 1e-4f) fl = 1f;
                    float px = -fdz / fl, pz = fdx / fl;   // perpendicular to flow (the T-section direction)
                    // ═══ THE BANK IS THE LOWEST POINT THE WATER MUST STAY UNDER, NOT ONE SAMPLE OF IT ═══════
                    // (2026-08-19, user: "if the rivers carve deep enough then we can afford to lay the mesh at
                    // a steeper angle where we need to by dropping it down into the carve further to avoid
                    // exposing the edge of the meshes".) Exactly — and that is affordable here BY CONSTRUCTION,
                    // because the bed is cut FROM the level (`profile = wl − depth·ChannelShape(dN/half)`), so
                    // dropping the level takes the whole channel down with it. There is no cost to going lower
                    // except a steeper bank, which is the thing being asked for.
                    // What was actually wrong: this took ONE nearest-cell sample per side, at exactly `edge`
                    // cells out. The sheet does not span one point — it runs the whole wetted perimeter — so
                    // the level has to clear the LOWEST ground along it. Any dip between the sample offset and
                    // the next cell out is a place the surface stands above the bank, and that is precisely
                    // where the berm then had to be built to bury it (3,896 measured "earned" raises) and where
                    // the render audit still finds a free edge.
                    // Take the MINIMUM over three offsets stepping outward on each side. Min, not mean and not
                    // median: this is a containment bound, so the statistic that matters is the worst case, and
                    // the whole point is to catch the notch a point sample walks straight past.
                    // (⚠ An earlier attempt used the MEDIAN here, to denoise the running-min further down. It
                    // was A/B'd at n=10501 and did nothing — flat runs 270 → 270, depth percentiles unchanged —
                    // and was reverted. Median asks "what is the typical bank"; that was never the question.)
                    float bankL = SampleHOrig(hOrig, n, cx + px * edge, cz + pz * edge);
                    float bankR = SampleHOrig(hOrig, n, cx - px * edge, cz - pz * edge);
                    for (int o = 1; o <= 2; o++)
                    {
                        float e2 = edge + o;
                        float l2 = SampleHOrig(hOrig, n, cx + px * e2, cz + pz * e2);
                        float r2 = SampleHOrig(hOrig, n, cx - px * e2, cz - pz * e2);
                        if (l2 < bankL) bankL = l2;
                        if (r2 < bankR) bankR = r2;
                    }
                    // ═══ A CANYON IS NOT A RIVER WITH TALL BANKS — ITS LEVEL COMES OFF THE FLOOR ══════════
                    // (User, 2026-08-20: "a canyon has to know its a canyon and cannot use generic river
                    // logic, because by default its a river at the bottom of a cliff on each side, so the
                    // leveling logic is different and the bank offsets… the water within the canyon should
                    // follow the same rules other water does, where it descends to the lowest point, not
                    // drawing at multiple levels.")
                    // THE MECHANISM, and it is an OFFSET problem. `edge` above is ~3.5 m for a brook and ~12 m
                    // for a trunk, and ExcavateCanyons runs BEFORE `hOrig` is cloned (:9511 vs :9513) — so
                    // inside a canyon `hOrig` already holds the dug slot, and the perpendicular sample lands
                    // on the FLOOR wherever the slot is wider than `edge` but ON THE WALL wherever it is
                    // narrower. A wall sample is ground tens of metres up, so `min(bank) − RELIEF` puts the
                    // water near the CANYON RIM there. Alternating between the two along one reach is exactly
                    // the reported "high spots, low spots, then high spots in the middle, then low again".
                    // MEASURED (riverfield C. CANYON vs OPEN): canyon reaches are BIMODAL against open ones —
                    // spread p50 0.00 m (a flat pool in a slot) but p90 11.82 m and p99 30.21 m, against open
                    // water's 3.84 / 9.42. Far fewer bad cells, an order of magnitude bigger jumps.
                    // THE RULE: in a canyon the containing surface is the FLOOR the river runs on, not a bank
                    // a cliff away. Take the level from the pre-carve ground AT THE SPINE and let the monotone
                    // + backwater passes below carry it down to the lowest point exactly as they do
                    // everywhere else — which is the user's requirement that canyon water obey the same rules
                    // as all other water. The rim then has no say in the water height, which is the point.
                    // ⚠ The class is available here only because ClassifyChannelIncision runs at :9482, ahead
                    // of this solve. It is GEN-TIME state and is NOT in the artifact — which is why the
                    // in-world "Canyon river" label re-derives it from terrain instead (IsCanyonWaterAt).
                    int ncx = Mathf.Clamp((int)(cx + 0.5f), 0, n - 1), ncz = Mathf.Clamp((int)(cz + 0.5f), 0, n - 1);
                    // ⚠ A SINGLE-CELL LOOKUP MISSES 98 % OF THE CLASS, MEASURED. ClassifyChannelIncision
                    // writes `cls` at RASTERIZED spine cells (DDA at half-cell steps — dense along the trace),
                    // while this solve walks the polyline's NODES, which are sparse. Rounding a node to one
                    // cell therefore lands between marked cells nearly every time: the first cut of this rule
                    // saw 295 of 12,559 classified nodes (2.3 %) and the A/B came out BYTE-IDENTICAL, which is
                    // what a rule that never fires looks like. Scan the immediate neighbourhood instead — a
                    // node within a cell or two of a classified spine cell is in that canyon.
                    // ⚠⚠ THE GENERATOR'S INCISION CLASS CANNOT BE THE GATE HERE — MEASURED. The stamp counts
                    // CANYON 3,515 + GORGE 9,044 = 12,559 nodes, but that tally is taken BEFORE the
                    // component-demotion pass ("213 span(s) DEMOTED", `cls[comp] = INC_NONE`), and after it
                    // only ~417 nodes still carry a class. Meanwhile the terrain test below finds 13,705
                    // canyon-LOOKING water cells. The surviving classification and the water that actually
                    // reads as a canyon disagree by roughly 30x, so gating on `incCls` describes almost none
                    // of what the user is pointing at — the first two cuts of this rule A/B'd BYTE-IDENTICAL
                    // for exactly that reason.
                    // So ask the ground, with the same test the in-world "Canyon river" label uses
                    // (IsCanyonWaterAt) so the label and the rule always agree about what a canyon is: a wall
                    // standing WALL_M above the water within RING_M of it. `incCls` is still accepted and
                    // still counts as a canyon where it survives — it is strictly additional evidence.
                    const float CANYON_WALL_M = 12f, CANYON_RING_M = 16f;
                    byte kcls = incCls != null ? incCls[ncz * n + ncx] : INC_NONE;
                    if (kcls == INC_NONE)
                    {
                        // ⚠ REFERENCE THE WALL TEST TO THE FLOOR, NOT TO THE BANK SAMPLE. This used
                        // `min(bankL,bankR) − RELIEF`, and in a canyon that sample lands ON THE WALL — so the
                        // wall did not measure 12 m above it and the reach failed its own detector, worst
                        // exactly in the deepest slots. That bug produced a "canyon nodes are 1.0 % of the
                        // network" figure, and a conclusion drawn from it ("the staircase was never a canyon
                        // phenomenon") that the user correctly rejected with screenshots of confirmed canyons
                        // showing the stepping. The floor at the node is the thing a wall stands over.
                        float probeLvl = hOrig[ncz * n + ncx];               // the bed here — what a wall is measured from
                        for (int rr = 4; rr <= (int)CANYON_RING_M && kcls == INC_NONE; rr += 4)
                            for (int a = 0; a < 8; a++)
                            {
                                float th = a * 0.7853982f;
                                int qx = Mathf.Clamp(ncx + (int)(Mathf.Cos(th) * rr / cellM), 0, n - 1);
                                int qz = Mathf.Clamp(ncz + (int)(Mathf.Sin(th) * rr / cellM), 0, n - 1);
                                if (hOrig[qz * n + qx] - probeLvl >= CANYON_WALL_M) { kcls = INC_CANYON; break; }
                            }
                    }
                    // ═══ THE LEVEL IS REFERENCED TO THE BED; THE BANK ONLY SAYS "DO NOT OVERFLOW" ═════════
                    // (User, 2026-08-20: "maybe filling to the lowest bank minus relief really only works when
                    // all rivers are shallow and dont have tall banks — maybe instead we should be drawing
                    // river depth from the bed and just making sure it doesnt overflow the bank?" That is the
                    // right model and it subsumes the canyon special case below.)
                    // `min(bank) − RELIEF` is a TOP-DOWN rule. It assumes the banks sit close to the water,
                    // which is true of a meadow stream and nonsense for a river at the bottom of a cliff,
                    // where it puts the surface near the RIM. That is why canyon reaches measured bimodal
                    // (spread p90 11.82 m / p99 30.21 m against open water's 3.84 / 9.42) and why the worst
                    // cases were absurd: a wall sample tens of metres up became the water height.
                    // BED-REFERENCED is the physical statement — a river's surface is its bed plus the depth
                    // its discharge sustains, and the surrounding relief has no vote. The bank keeps exactly
                    // one job, the one it can actually answer: a surface may not stand above the lowest ground
                    // that contains it, or it spills.
                    // Normally `min(bank) >= floor`, so the FLOOR term governs and the bank never binds. Where
                    // a bank is LOWER than the bed — a real spill point — the bank term binds and stops the
                    // overflow. Canyons stop being a special case; they are just where the two terms differ by
                    // tens of metres instead of centimetres.
                    // MEASURED, canyon-gated first (the step that proved the mechanism): canyon spread max
                    // 295.51 -> 47.51 m, p99 30.21 -> 24.56, worst adjacent step 294.59 -> 44.46 m, cells >8 m
                    // apart 2,291 -> 1,954, exposed edges 0 -> 0.
                    inCanyon[k] = kcls != INC_NONE;
                    float bankCap = Mathf.Min(bankL, bankR) - RELIEF;
                    float floorCap = hOrig[ncz * n + ncx] - RELIEF;      // the bed this reach actually runs on
                    if (s_canyonFloorLevel)
                    {
                        // ⚠ NOT `Mathf.Min(floorCap, bankCap)` — that was tried and LOST the entire canyon
                        // win (spread max back to 295.51 from 47.51). Which refutes the mechanism I first
                        // wrote here: the catastrophic outliers are NOT a wall sample perching water at the
                        // rim. They are the opposite — `min(bankL,bankR)` landing in an adjacent slot and
                        // dragging the surface FAR BELOW ITS OWN BED. A `min` with the bank therefore keeps
                        // exactly the value that causes the defect.
                        // The bed is the primary, and the bank may only lower it by a bounded amount: a bank
                        // genuinely a little below the bed is a real spill point and should pull the level
                        // down; a bank 200 m below is a sampling artifact and must not. Beyond that bound the
                        // existing berm/rim safety is what holds the water in — which is the role the user
                        // scopes it to ("a safety for when you inevitably cannot contain the mesh in spots").
                        const float MAX_BANK_DRAW = 3f;
                        cap[k] = Mathf.Max(bankCap, floorCap - MAX_BANK_DRAW);
                        if (kcls != INC_NONE) System.Threading.Interlocked.Increment(ref s_canyonNodes);
                        if (Math.Abs(cap[k] - bankCap) > 0.05f) System.Threading.Interlocked.Increment(ref s_canyonMoved);
                    }
                    else cap[k] = bankCap;
                    // ═══ THE PLAN ALREADY SOLVED THIS SURFACE. STOP THROWING IT AWAY. ════════════════════
                    // (2026-08-19, user: "I explicitly said I wanted all water accounted for and placed with
                    // purpose — how can our water be planned and purpose built and still have overlaps?"
                    // He is right, and the answer is that it is planned for PLACEMENT and improvised for
                    // HEIGHT.)
                    // FiresWaterPlanner.EmitConnection solves every run's levels GLOBALLY: anchored at the
                    // FromBody surface, descending monotonically to the ToBody surface, never uphill, never
                    // below its destination. Each run is tied to real water at BOTH ENDS, so two runs draining
                    // the same valley agree with each other by construction. That is what a plan is for.
                    // PlanConnectionsToPolys then hands those polylines here with the levels in slot 3, and
                    // this function overwrote them with a fresh LOCAL guess — min(bankL,bankR) − RELIEF — that
                    // knows nothing about any other run. With a few hundred separated rivers that was
                    // invisible. With the 56,865 first-order channels GEN 132 introduced, the network is
                    // 56,865 unrelated local solves piled on top of each other.
                    // MEASURED (the metric that was missing all along — `riverfield` N. LEVEL SPREAD, on the
                    // SHIPPED artifact, so this long predates any of today's work): within a 10 m radius the
                    // river surface spread is p50 1.12 m, p90 5.88 m, p99 39.72 m. 52.7 % of all river cells
                    // have another river surface more than a METRE off in height within 10 m of them; 22.4 %
                    // more than 3 m. That is the terracing and the spikes, and no cross-section, bank rule or
                    // sheet-builder change can touch it — they all ask "is this cell right against the
                    // ground", and every one of those slabs IS correctly against the ground. They just are not
                    // the same surface as each other.
                    // So: where the plan gave us a level, that level is the BASE. The bank machinery may still
                    // lower it for containment (`cap` at the end) — the ground always wins over a plan — but it
                    // may not INVENT one. A node with no plan level (derived body outlets, spring runs — they
                    // arrive as 0, see `walkNodes.Add(0f)`) keeps the bank-derived value it always had.
                    // ═══ THE LEVEL IS THE DRAINAGE SOLUTION, THE BANK IS ONLY ITS CEILING ════════════════
                    // docs/WATER_REFERENCE_DIVERGENCE.md §1–2. This is the reference generator's rule,
                    // expressed in our structure. Its rasterizer reads the TRACED node level — which
                    // TraceRivers copies straight out of the PriorityFloodFill drainage table — and then:
                    //     lvl = tracedLevel - 0.9;                              // freeboard
                    //     if (lvl > hOrig[i] - 0.5f) lvl = hOrig[i] - 0.5f;     // ceiling: never above ground
                    //     if (lvl < SEA) lvl = SEA;
                    //     if (fresh || lvl < w[i]) w[i] = lvl;                  // min-write across stems
                    // The fill table is the elevation at which water at a cell can first escape to the map
                    // edge: `>= h` everywhere, `>= SEA` everywhere, and NON-INCREASING along every flow path,
                    // all by construction. That is ONE globally consistent surface which every stem shares —
                    // so two channels crossing cannot carry different heights, and a reach cannot disagree
                    // with the reach it flows into. It is the guarantee this file has been trying to
                    // reconstruct reactively with a berm, a rim, a shelf, a feather, a drape and a skirt.
                    // We replaced it with `min(bankL, bankR) - RELIEF` from two nearest-cell ground samples
                    // per node (the "bank-derived ribbon"), which is a purely LOCAL statement — hence the
                    // measured 52.7 % of river cells sitting within 10 m of another river surface more than a
                    // metre off in height.
                    // ⚠ THE BANK SAMPLE IS KEPT — as `cap[k]`, a CEILING, never as the value. That is exactly
                    // its role in the reference (their ceiling is `hOrig - 0.5`; ours is the slightly stricter
                    // `min(bank) - RELIEF`, which is the same statement made with a wider stencil).
                    // ⚠ THE BODY ANCHOR BELOW IS UNTOUCHED and must stay so: a node under a seeded body takes
                    // that body's surface, which is what makes a river meet a pond at the pond's level — the
                    // half the user says already works. Still water is not in scope here at all; this function
                    // only ever sees river polylines.
                    // ⚠ The fill table is NOT the wet set — it fills every depression to its spill point. The
                    // dry pass that follows (mask 0 + above sea => w = g) already handles that, and is what the
                    // reference's own "THE FILL TABLE IS NOT WATER" note describes.
                    float planLvl = poly[k * 4 + 3];
                    if (s_riverFillLevel && fill != null)
                    {
                        int fx = Mathf.Clamp((int)(cx + 0.5f), 0, n - 1), fz = Mathf.Clamp((int)(cz + 0.5f), 0, n - 1);
                        float fl2 = fill[fz * n + fx] - RELIEF;      // drainage solution, minus freeboard
                        lvl[k] = fl2 < cap[k] ? fl2 : cap[k];        // …and the local bank is its ceiling
                    }
                    else lvl[k] = (s_riverPlanLevels && planLvl > 0f) ? planLvl : cap[k];
                    // (v92) BASE-LEVEL ANCHOR (Playfair / HYDROLOGY.md Part V): a node UNDER a seeded body
                    // (lake/swamp) carries the body's surface EXACTLY — the outlet run then STARTS at the
                    // lake's spill elevation (the WHY of an outlet river), holds it flat across the body, and
                    // the monotone pass keeps everything downstream ≤ it. No step at the shore, no early dip.
                    int ax = Mathf.Clamp((int)(cx + 0.5f), 0, n - 1), az = Mathf.Clamp((int)(cz + 0.5f), 0, n - 1);
                    int ai = az * n + ax;
                    if (seeded != null && seeded[ai] > hOrig[ai] + 0.01f) { lvl[k] = seeded[ai]; anchored[k] = true; anchorLvl[k] = seeded[ai]; }
                }
                // ═══ (TRACE 2026-08-19) DOES THE BASE-LEVEL ANCHOR EVER ACTUALLY FIRE? ═══════════════════
                // The whole coherence of this network rests on it. A plan run is a HOP BETWEEN TWO BODIES —
                // p50 is 28 m, because FiresWaterPlanner starts a run in open country and ends it the moment
                // it enters a body — and `EmitConnection` makes it coherent by pinning both ends to those
                // bodies' constants. `anchored[k]` is what carries that through here.
                // But the planner also TRUNCATES each run at SHORE_ARRIVE_M (12 m) BEFORE the footprint, so a
                // run's last node can sit entirely outside the body it drains into — and this test asks
                // `seeded > hOrig` AT THE NODE. If that never fires, no run is tied to the water at either
                // end, the "base-level anchor" and the backwater lift are both dead code for plan runs, and
                // every one of these 28 m hops is free to invent its own level from two ground samples.
                // Counting it rather than reasoning about it.
                { int na = 0; for (int k = 0; k < nn; k++) if (anchored[k]) na++;
                  System.Threading.Interlocked.Add(ref s_anchorNodes, na);
                  System.Threading.Interlocked.Increment(ref s_anchorRuns);
                  if (na == 0) System.Threading.Interlocked.Increment(ref s_anchorRunsNone); }
                bool srcAtStart = poly[2] <= poly[(nn - 1) * 4 + 2];   // area[0] <= area[last] -> flow runs 0->last

                // ═══ THE WALK — the whole solve, and the LAST thing that touches lvl[] ═══════════════════
                int kHead = srcAtStart ? 0 : nn - 1, kTail = srcAtStart ? nn - 1 : 0;
                float lStart = anchored[kHead] ? anchorLvl[kHead] : poly[kHead * 4 + 3];
                float lEnd = anchored[kTail] ? anchorLvl[kTail] : poly[kTail * 4 + 3];
                if (lStart <= 0f) lStart = cap[kHead];                  // no plan level (studio drainage path) — the cap stands in
                if (lEnd <= 0f) lEnd = cap[kTail];
                if (lEnd > lStart) lEnd = lStart;                       // water never runs uphill
                float run = lStart;
                for (int t = 0; t < nn; t++)
                {
                    int k = srcAtStart ? t : nn - 1 - t;                // walk SOURCE -> DESTINATION
                    if (t > 0)
                    {
                        float tgt = cap[k];                             // the ground here, minus freeboard
                        if (tgt < run) run = tgt;                       // descend only…
                        if (run < lEnd) run = lEnd;                     // …and never below the destination
                    }
                    lvl[k] = run;
                    if (anchored[k]) { lvl[k] = anchorLvl[k]; run = anchorLvl[k]; }
                }
                if (lEnd > 0f && !anchored[kTail]) lvl[kTail] = lEnd;   // the sheets TOUCH
                System.Threading.Interlocked.Increment(ref s_resolvedRuns);

                for (int k = 0; k < nn; k++) poly[k * 4 + 3] = lvl[k];
            }
        }
    }
}
