using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══ RIVER CARVE — WHERE THE CHANNEL IS CUT INTO THE GROUND ══════════════════════════════════════
    // (user, 2026-09-02: "part of the problem is it shouldnt BE a 14k line file … it should be broken up
    // into manageable sections", and again the same day: "SPLIT UP THESE FUCKING MASSIVE GODDAMN CLASSES
    // YOU HAVE SO MUCH TROUBLE FOLLOWING". He is describing a real cost, not a style preference — the
    // junction bank shipped three times because the rule that built it and the carve that feathers the
    // channel to meet it sat 9,000 lines apart in one file and were never read together.)
    //
    // This file is InciseRivers, moved verbatim out of FiresWorldGenBase.cs (13,366 -> 11,177 lines).
    // It is the pass that turns the planned polylines into an actual trench: width from discharge, depth
    // from width, the bank chamfer, the mountain-incision nudge, and the BODY_TAPER that feathers the
    // channel to ~0.9 m as it reaches a still body so the two beds MEET rather than the river driving a
    // trench through the lake's shore.
    //
    // Its partners, so the next reader does not have to find them the hard way:
    //   • the ROUTE it carves comes from PlanConnectionsToPolys (FiresWorldGenBase.cs)
    //   • the SURFACE HEIGHT at every node is decided in FiresWorldGenBase.RiverLevels.cs — nothing here
    //     may change a level; this file only moves DIRT
    //   • the containment/burial that runs AFTER it is FiresWaterConform.BuryAllEdges, and that is where
    //     a junction is kept open (see the JUNCTION note there) — the taper below is the other half of
    //     that same contract
    public static partial class FiresWorldGenBase
    {
        private static byte[] InciseRivers(float[] h, int n, int seed, float[] w, List<float[]> riverPolys, float[] seeded, byte[] existingMask, out float[] waterLevel)
        {
            var rsw = System.Diagnostics.Stopwatch.StartNew();
            float cellM = (2f * _half) / (n - 1);
            float RS = 2047f / (n - 1) * SpanScale;   // 0.2.722: x (edge / 10500), exactly 1 on a vanilla-size world
            int ms = seed * 7 + 977;

            // RIVER REWORK — the global stream-power incision is REMOVED. It cut
            // the raw D-inf accumulation field into every 0.25 km² channel,
            // painting angular drainage lines across floodplains and valley
            // flats. Rivers now come ONLY from the traced polylines (carved
            // below); the mountains keep their ridge relief + droplet erosion.
            // Nothing incises flat/floodplain terrain any more.

            var mask = existingMask ?? new byte[n * n];
            // ═══ CLASSIFY, THEN DIG — BEFORE hOrig IS TAKEN, SO THE CANYON *IS* THE TERRAIN ═══════════════
            // docs/CANYONS_AND_GORGES.md §3b. Ordering is the whole trick and it is why this could not simply
            // be bolted on: excavating here means every pass downstream treats the slot as natural ground.
            // SetBankDerivedLevels reads hOrig and pins the ribbon to its local banks, so the water follows
            // the floor down on its own; CascadeSplitPolys then sees the step at the lip and does exactly the
            // right thing with it — sheet ends at the lip, resumes below, span stamped WC_WATERFALL for FX.
            // The lip is the waterfall, not a defect to engineer around.
            // ⚠ The classification MUST run ahead of the cascade for that to work. It used to sit after both
            // the splitter and the level solve, where a canyon-induced drop would arrive too late to be cut.
            // ═══ THE CARVE READS THE PLAN'S CANYONS. IT DOES NOT LOOK FOR THEM. ══════════════════════════
            // (user: "canyons are meant to be placed with purpose, carved with purpose".) The floor term
            // below used to fire on a per-cell terrain test — "is the thalweg 12 m below its own ground here"
            // — which is a detector wearing a carve's clothes: it could disagree with what the game labels a
            // canyon, it could not be listed, and "no canyons" and "canyons we cannot see" were the same
            // state. `FiresWaterPlanner.SiteCanyons` now PLACES them on the plan, so the carve just looks up
            // whether this cell belongs to one.
            // Rasterized once here: every node span becomes cells within its own FloorHalfW, so a cross-section
            // knows both THAT it is in a canyon and HOW WIDE that canyon's floor was planned to be.
            var canyonFloorW = new Dictionary<int, float>(1 << 14);
            if (_plan != null && _plan.Canyons.Count > 0)
            {
                foreach (var q in _plan.Canyons)
                {
                    if (q.ConnectionId < 0 || q.ConnectionId >= _plan.Connections.Count) continue;
                    var cc = _plan.Connections[q.ConnectionId];
                    int cn = cc.NodeCount;
                    for (int k = q.Node0; k <= q.Node1 && k < cn; k++)
                    {
                        float nx = cc.Nodes[k * 4], nz = cc.Nodes[k * 4 + 1];
                        int rad = Math.Max(1, (int)(q.FloorHalfW / cellM + 0.5f)) + 1;
                        int bx0 = Mathf.Clamp((int)((nx + _half) / cellM + 0.5f), 0, n - 1);
                        int bz0 = Mathf.Clamp((int)((nz + _half) / cellM + 0.5f), 0, n - 1);
                        for (int dz = -rad; dz <= rad; dz++)
                            for (int dx = -rad; dx <= rad; dx++)
                            {
                                int qx = bx0 + dx, qz = bz0 + dz;
                                if (qx < 0 || qx >= n || qz < 0 || qz >= n) continue;
                                int j = qz * n + qx;
                                if (!canyonFloorW.TryGetValue(j, out float had) || q.FloorHalfW > had)
                                    canyonFloorW[j] = q.FloorHalfW;
                            }
                    }
                }
            }
            var incisionCls = ClassifyChannelIncision(riverPolys, h, n, cellM, seed);
            LastIncisionClass = incisionCls;
            if (s_canyonExcavate) ExcavateCanyons(h, incisionCls, n, cellM, seed, seeded, riverPolys);   // polylines = the spline each canyon descends along

            var hOrig = (float[])h.Clone();
            // (v133) Diagnostic only, and null in every normal run — see FlatRegionAudit/LastWriteSrc. This
            // function writes the grid from seven places; without a per-cell record of WHICH one wrote last,
            // attributing a terrain artifact to a pass is guesswork, and the first guess here was wrong.
            var wsrc = Environment.GetEnvironmentVariable("FIRES_FLAT_AUDIT") == "1" ? new byte[n * n] : null;
            // ═══ WHO WROTE THIS WATER LEVEL? ════════════════════════════════════════════════════════════
            // (2026-08-22.) Three independent attempts at the level SOURCE — the local bank guess, the plan's
            // levels raw, and the plan's law re-solved on final terrain — all landed within noise of each
            // other (3,307 / 3,378 / 3,264 uphill rises). And the spine-descent experiment made every run's
            // spine monotone BEFORE the carve, corrected 9,871 cells and 5,624 m of climb, and the finished
            // field still came out at 3,818. A defect that survives every change to where the number comes
            // from is not being made where the number comes from: it is being made by one of the NINE passes
            // that overwrite `w` afterwards. The backwater lift cannot be it either — it only ever RAISES
            // nodes upstream of an anchor, which leaves a run's own profile non-increasing.
            // So stop guessing between them and record it. One byte per cell, the id of the last pass to
            // write that cell's level; the UPHILL census then attributes every rise to whoever wrote the
            // HIGHER of the two cells. Allocated only under FIRES_FLAT_AUDIT so the normal path pays nothing.
            //   1 river paint (min-write across runs)   2 dry snap to ground (mid)      3 paintLvl apply
            //   4 river surface smooth                  5 shore flood                   6 BODY ADOPTION
            //   7 sea snap                              8 dry snap to ground (final)    9 late fill / bury
            var lvlSrc = wsrc != null ? new byte[n * n] : null;
            LastWriteSrc = wsrc;

            // MARSH GUARD — SWAMP-biome cells under a seeded sheet ONLY (lakes/rivers/
            // ocean behaviour untouched, per the user's scope). Incising channels +
            // valley shaves THROUGH a marsh floor fractured it into mesa mazes with the
            // sheet pouring off every cut (in-game: waterfalls everywhere, water at many
            // levels). Inside a marsh the body IS the water: the river STOPS at the rim
            // and RESUMES past the far edge — no carve, no valley shave, no pools inside.
            bool[] underMarsh = null;
            var bm = _biomeMap;
            if (seeded != null && bm != null && bm.Length == n * n)
            {
                underMarsh = new bool[n * n];
                for (int z2 = 0; z2 < n; z2++)
                {
                    int row = z2 * n;
                    for (int x2 = 0; x2 < n; x2++)
                    {
                        int i2 = row + x2;
                        if (bm[i2] != 2) continue;
                        // Wet under the sheet, OR anywhere in a registered inland-marsh region
                        // footprint (the seeder stamps s_seedLevels across every zone block it
                        // carved) — WITHOUT the footprint the channel threaded the DRY mound
                        // corridors between pools and still cut a groove across the marsh. The
                        // river now stops at the region MOUTH and resumes past the far edge.
                        if (seeded[i2] > hOrig[i2] + 0.01f || s_marshZones.Contains(SeedKey(x2, z2)))
                            underMarsh[i2] = true;
                    }
                }
            }

            // ═══ THE MARSH FOOTPRINT COMES FROM THE PLAN, NOT THE BIOME (2026-08-13) ══════════════════════
            // `underMarsh` above is BIOME-gated (bm[i2] == 2), and that is correct for what it is used for —
            // keeping the river carve out of swampy ground. It is WRONG as a test for "is this marsh water",
            // because FiresWaterPlanner deliberately floods a marsh PAST the biome line ("water ignores the
            // biome line, the sheet floods any adjacent ground under the level"). So the outer band of every
            // marsh — which is exactly its RIM, which is exactly where it floats — is marsh water sitting on
            // non-swamp biome, and biome-gated detection cannot see it. Measured: raising the burial pass's
            // wetness sensitivity for `underMarsh` cells changed the float by literally zero, because the
            // cells that needed it were never in that set. The plan's Marsh masks are the authoritative
            // footprint (same source StampBodyClasses uses for the class grid), so use those.
            bool[] marshBody = null;
            if (_plan != null && _plan.Bodies != null)
            {
                foreach (var b in _plan.Bodies)
                {
                    if (b.Kind != WaterBodyKind.Marsh || b.Mask == null) continue;
                    if (marshBody == null) marshBody = new bool[n * n];
                    for (int lz = 0; lz < b.H; lz++)
                    {
                        int gz = b.Gz0 + lz;
                        if (gz < 0 || gz >= n) continue;
                        for (int lx = 0; lx < b.W; lx++)
                        {
                            if (b.Mask[lz * b.W + lx] == 0) continue;
                            int gx = b.Gx0 + lx;
                            if (gx < 0 || gx >= n) continue;
                            marshBody[gz * n + gx] = true;
                        }
                    }
                }
            }

            // v22 — THE RIVERHEIM GRAMMAR (Tools/FIRES_RIVER_ENGINE_RIVERHEIM.md).
            // Rivers are no longer STAMPED into independent terrain; the
            // terrain around them is SOLVED FROM the rivers:
            //   1. real dimensions — width from a discharge power law
            //      (140·q^0.4−6, clamped ≤70m), depth from width (~7:1 remap,
            //      clamped 2.5-10m) — river-scale channels;
            //   2. thalweg water level = the fill table (monotone downstream
            //      by the fill's construction) — bed and water CANNOT disagree
            //      (kills the v21 wCap machinery outright);
            //   3. a bounded multi-source Dijkstra grows a VALLEY surface
            //      outward from every river cell at a gentle incline and
            //      shaves approach bumps to it (capped, so gorge walls stay);
            //   4. the channel is a BiasedRational cross-section blended in
            //      with a cubic SmoothMin — banks merge continuously into the
            //      terrain, never terrace, and only ever LOWER ground.
            // Meander comes from the corrugated routing; mouths flare seaward.

            var widthOf = new float[n * n];
            var seedIdx = new int[n * n];
            for (int i2 = 0; i2 < seedIdx.Length; i2++) seedIdx[i2] = -1;

            // RIVER REWORK — the spine is the SMOOTH TRACED NETWORK, not a
            // per-cell accumulation threshold. Rasterize each traced polyline
            // into widthOf/w/riverCells (DDA at ½-cell steps so the spine is
            // unbroken): width = power law of the node catchment area (2m brook
            // … 14m trunk), level = the traced water table − 0.9m freeboard so
            // banks stand above, never perched above local ground, never below
            // the sea. The tuned Dijkstra valley + cross-section carve below
            // then draw the terrain around this smooth spine exactly as before.
            var riverCells = new List<int>(16384);
            // (TRACE 2026-08-19) WHICH RUN owns each spine cell. The multi-level sheet is measured
            // (riverfield N. LEVEL SPREAD: 52.7 % of river cells have another river surface >1 m off within
            // 10 m) but the MECHANISM decides the fix, and there are only two candidates: two DIFFERENT runs
            // crossing at their own levels, or ONE run whose own profile is inconsistent along its length.
            // Those need opposite fixes — merge/reconcile runs, versus fix the longitudinal solve — so this
            // is measured rather than assumed. A dictionary, not an n² array: spine cells are a few hundred
            // thousand out of 110 M, and this function is already at the memory ceiling.
            var spinePoly = new Dictionary<int, int>(1 << 16);
            var spineArc = new Dictionary<int, float>(1 << 16);   // (TRACE) cell -> metres along its run, so a
                                                                  // level difference can be judged against ALONG-CHANNEL
                                                                  // distance rather than straight-line distance
            // EXACT source cells (first rasterized cell of each polyline) —
            // the headwater-pool pass reads THIS, not a level heuristic. The
            // old "no higher neighbour" test misfired on the monotone-clamped
            // flat runs and carved a pool bowl at EVERY clamped cell — the
            // beaded pit trenches (user screenshots).
            var polySources = new HashSet<int>();
            float AREA_ONSET = WaterRules.RiverProc.AreaOnset;    // m² — below = ephemeral, no channel
            float WK = WaterRules.RiverProc.WidthK, WP = WaterRules.RiverProc.WidthP;
            float rhalf = (n - 1) * cellM * 0.5f;
            // (v94) BODY OUTLET GUARANTEE — the closed water GRAPH (user's law: rivers exist to CONNECT.
            // A body that receives drainage OVERFLOWS at its rim, and the overflow continues to the next
            // body or the sea — no lakes that mysteriously swallow their inflow, no random rivers).
            // Traced rivers are sea-anchored by construction (TraceRivers walks upstream FROM sea mouths;
            // tributaries end at junctions) and v92/v93 justified their sources — the remaining hole was
            // bodies NO trace threads. Here every seeded still body above sea gets its outlet: from the
            // rim's lowest-fill SPILL SADDLE, walk steepest descent on the REAL fill table — which by the
            // fill's own construction can only terminate at the sea or in a deeper basin (the next body) —
            // and append the path as a first-class polyline. The machinery below does the rest: the
            // base-level anchor pins its start AT the body's surface, the weir shaping holds it flat to
            // the rim lip, the ribbon carve draws it, the seeded merge blends it into whatever it enters.
            // Width scales with the body's size (area feeds the discharge law). Chains compose body→body→sea.
            if (seeded != null && riverPolys != null)
            {
                var bodyId = new int[n * n];
                for (int i2 = 0; i2 < bodyId.Length; i2++) bodyId[i2] = -1;
                var bodyCellLists = new List<List<int>>();
                var bodyLevels = new List<float>();
                {
                    var bstack = new Stack<int>();
                    for (int i0 = 0; i0 < n * n; i0++)
                    {
                        if (bodyId[i0] >= 0 || seeded[i0] <= hOrig[i0] + 0.01f) continue;
                        int id = bodyCellLists.Count;
                        var cellsB = new List<int>(1024);
                        bstack.Push(i0); bodyId[i0] = id;
                        while (bstack.Count > 0)
                        {
                            int i = bstack.Pop(); cellsB.Add(i);
                            int x = i % n, z = i / n;
                            if (x + 1 < n && bodyId[i + 1] < 0 && seeded[i + 1] > hOrig[i + 1] + 0.01f) { bodyId[i + 1] = id; bstack.Push(i + 1); }
                            if (x > 0 && bodyId[i - 1] < 0 && seeded[i - 1] > hOrig[i - 1] + 0.01f) { bodyId[i - 1] = id; bstack.Push(i - 1); }
                            if (z + 1 < n && bodyId[i + n] < 0 && seeded[i + n] > hOrig[i + n] + 0.01f) { bodyId[i + n] = id; bstack.Push(i + n); }
                            if (z > 0 && bodyId[i - n] < 0 && seeded[i - n] > hOrig[i - n] + 0.01f) { bodyId[i - n] = id; bstack.Push(i - n); }
                        }
                        bodyCellLists.Add(cellsB);
                        bodyLevels.Add(seeded[i0]);
                    }
                }
                var drained = new bool[bodyCellLists.Count];
                foreach (var poly in riverPolys)
                    for (int k = 0; k < poly.Length / 4; k++)
                    {
                        int cxq = Mathf.Clamp((int)((poly[k * 4] + rhalf) / cellM + 0.5f), 0, n - 1);
                        int czq = Mathf.Clamp((int)((poly[k * 4 + 1] + rhalf) / cellM + 0.5f), 0, n - 1);
                        int bid = bodyId[czq * n + cxq];
                        if (bid >= 0) drained[bid] = true;
                    }
                ReportDepressions(w, hOrig, n, cellM);   // (v128 diag) why this world ponds — measured, before any theory about it
                int outletsDerived = 0, alreadyDrained = 0, seaOwned = 0;
                int flatCrossings = 0, flatNodes = 0;   // (v128) how much of the routing is flat resolution — see EscapeFlat
                // (v128 diag) Straightness telemetry. Three theories about WHY these runs draw as straight lines
                // were each disproved by measurement, so stop guessing and measure the walk itself: how many
                // steps come from the carried-gradient path vs the D8 fallback, and how long the longest
                // same-heading run is. A natural channel changes heading constantly; a pathological one does not.
                long carrySteps = 0, fallbackSteps = 0, totalSteps = 0;
                int worstStraight = 0; long straightSum = 0; int straightRuns = 0;
                int flatTerminations = 0;
                int worstFrom = -1, worstTo = -1, worstDx = 0, worstDz = 0, worstBody = -1;
                var walkNodes = new List<float>(4096);
                for (int b2 = 0; b2 < bodyCellLists.Count; b2++)
                {
                    if (bodyLevels[b2] <= SEA + 0.6f) { seaOwned++; continue; }     // sea-owned sheet — the ocean drains it
                    if (drained[b2]) { alreadyDrained++; continue; }
                    if (bodyCellLists[b2].Count < 150) continue;                    // speck: no channel-worthy overflow
                    // (v104) HIGH tarns must EARN an outlet — the real massif terrain (v102) holds thousands
                    // of small cirque pockets; deriving a channel from every one carpeted the mountains with
                    // carved wet corridors (seed 49185460). Small high tarns are endorheic (seep/evaporate —
                    // real cirque tarns usually are); only substantial high lakes overflow as streams.
                    if (bodyLevels[b2] > 200f && bodyCellLists[b2].Count < 600) continue;
                    // SPILL SADDLE = the DRY rim cell with the lowest fill
                    int saddle = -1, inBody = -1; float sf = float.MaxValue;
                    foreach (int i in bodyCellLists[b2])
                    {
                        int x = i % n, z = i / n;
                        if (x + 1 < n && bodyId[i + 1] < 0 && w[i + 1] < sf) { sf = w[i + 1]; saddle = i + 1; inBody = i; }
                        if (x > 0 && bodyId[i - 1] < 0 && w[i - 1] < sf) { sf = w[i - 1]; saddle = i - 1; inBody = i; }
                        if (z + 1 < n && bodyId[i + n] < 0 && w[i + n] < sf) { sf = w[i + n]; saddle = i + n; inBody = i; }
                        if (z > 0 && bodyId[i - n] < 0 && w[i - n] < sf) { sf = w[i - n]; saddle = i - n; inBody = i; }
                    }
                    if (saddle < 0) continue;                                       // enclosed by other water — already continuous
                    walkNodes.Clear();
                    // (v104) OUTLETS ARE BORN STREAM-SCALE — keying width to the lake's SURFACE AREA made a
                    // big cirque tarn's outlet hit WidthMax/DepthMax+mountain-incision: a 70m-wide, 23m-deep
                    // channel carved ACROSS THE SUMMIT and filled bank-to-bank at ridge level (the "mountain
                    // top turned into water", seed 49185460). A real outlet starts as a stream at the rim
                    // and grows only as its downstream catchment feeds it — cap the equivalent area at 8×
                    // onset (a few metres wide, wadeable) regardless of how big the lake is.
                    float aOut = Math.Max(AREA_ONSET * 1.5f, Math.Min(bodyCellLists[b2].Count * cellM * cellM, AREA_ONSET * 8f));
                    void AddNode(int ci)
                    {
                        walkNodes.Add((ci % n) * cellM - rhalf); walkNodes.Add((ci / n) * cellM - rhalf);
                        walkNodes.Add(aOut); walkNodes.Add(0f);                     // level filled by SetBankDerivedLevels below
                    }
                    AddNode(inBody); AddNode(saddle);                               // in-body first node → the base-level anchor pins the start at the body surface
                    int cur = saddle; bool reached = false; int step = 0;
                    float accX = 0f, accZ = 0f;   // (v128) sub-cell heading carry — see the walk below
                    int prevSx = 99, prevSz = 99, runLen = 0, runStart = cur;   // (v128 diag) same-heading run
                    // One place that accounts for a step, so the descent path and the flat crossings cannot
                    // diverge again — measuring only the descent once reported "longest run 21" while the
                    // crossings were drawing kilometre-long ladders.
                    void Step(int from, int to)
                    {
                        int sx = (to % n) - (from % n), sz = (to / n) - (from / n);
                        totalSteps++;
                        if (sx == prevSx && sz == prevSz) { runLen++; return; }
                        if (runLen > worstStraight)
                        {
                            worstStraight = runLen; worstFrom = runStart; worstTo = from;
                            worstDx = prevSx; worstDz = prevSz; worstBody = b2;
                        }
                        if (runLen > 0) { straightSum += runLen; straightRuns++; }
                        runLen = 1; prevSx = sx; prevSz = sz; runStart = from;
                    }
                    while (step++ < 30000)
                    {
                        int cx2 = cur % n, cz2 = cur / n;
                        float wc = w[cur];
                        // ⚠ D8 CAN ONLY POINT EIGHT WAYS, AND THAT IS THE STRAIGHT-LINE BUG. This walk used to
                        // step to the lowest of its 8 neighbours. On a slope running, say, 20° north of east
                        // the lowest neighbour is EAST for many cells in a row, then NE once — so the channel
                        // draws as a long axis-aligned run with a 45° jog, and a field of them looks like
                        // circuit traces. It is the same defect this file already fixed for TRACED rivers with
                        // D-infinity ("rivers drew as straight axis/diagonal segments with 45°/90° jogs —
                        // user-caught at native res") — that fix went into TraceRivers and never reached here,
                        // and here is where 497 of a world's 530 rivers come from.
                        //
                        // Note it is NOT enough to divide the drop by step length (tried first, measured: the
                        // map was unchanged). Normalising the metric only changes WHICH heading wins the tie;
                        // the path is still snapped to one of eight. The direction has to stop being snapped.
                        //
                        // So: take the real downhill direction from central differences, and CARRY THE
                        // FRACTIONAL ERROR between steps. Each step adds the unit descent vector to an
                        // accumulator and moves a cell only when a component crosses half a cell — exactly how
                        // a line-drawing algorithm renders an arbitrary angle on a pixel grid. A 20° heading
                        // then comes out as a mixed E/NE stipple that averages to 20°, not a straight run.
                        int best = -1;
                        {
                            int xm = Math.Max(1, cx2 - 1), xp = Math.Min(n - 2, cx2 + 1);
                            int zm = Math.Max(1, cz2 - 1), zp = Math.Min(n - 2, cz2 + 1);
                            float dx = -(w[cz2 * n + xp] - w[cz2 * n + xm]);
                            float dz = -(w[zp * n + cx2] - w[zm * n + cx2]);
                            float mag = (float)Math.Sqrt(dx * dx + dz * dz);
                            if (mag > 1e-6f)
                            {
                                accX += dx / mag; accZ += dz / mag;
                                int sx = 0, sz = 0;
                                if (accX >= 0.5f) { sx = 1; accX -= 1f; } else if (accX <= -0.5f) { sx = -1; accX += 1f; }
                                if (accZ >= 0.5f) { sz = 1; accZ -= 1f; } else if (accZ <= -0.5f) { sz = -1; accZ += 1f; }
                                if (sx != 0 || sz != 0)
                                {
                                    int nx2 = cx2 + sx, nz2 = cz2 + sz;
                                    if (nx2 >= 1 && nx2 < n - 1 && nz2 >= 1 && nz2 < n - 1 && w[nz2 * n + nx2] < wc - FILL_EPS_FLAT)
                                    { best = nz2 * n + nx2; carrySteps++; }
                                }
                            }
                        }
                        if (best < 0)
                        {
                            // The gradient step was uphill, off-grid, or the accumulator has not yet earned a
                            // move. Fall back to the steepest GRADIENT neighbour — drop divided by step length,
                            // because a diagonal covers √2 more ground and comparing raw drop quietly favours
                            // it — and reset the carry so the next heading starts clean.
                            float bestSlope = 0f;
                            for (int e = 0; e < 8; e++)
                            {
                                int nx2 = cx2 + RIV_DX[e], nz2 = cz2 + RIV_DZ[e];
                                if (nx2 < 1 || nx2 >= n - 1 || nz2 < 1 || nz2 >= n - 1) continue;
                                int j2 = nz2 * n + nx2;
                                float drop = wc - w[j2];
                                if (drop <= FILL_EPS_FLAT) continue;               // epsilon ramp, not a fall — hand it to EscapeFlat
                                float slope = e < 4 ? drop : drop * 0.70710678f;   // 0E 1W 2N 3S | 4NE 5SE 6NW 7SW
                                if (slope > bestSlope) { bestSlope = slope; best = j2; }
                            }
                            if (best >= 0) fallbackSteps++;
                            accX = 0f; accZ = 0f;
                        }
                        if (best < 0)
                        {
                            // Stalled: every neighbour is level, so we are standing IN a filled flat. Cross it to
                            // the nearest real way down rather than abandoning the run (which used to strand a
                            // lake with no outlet) or trusting a direction the flat cannot supply (which is what
                            // drew the parallel bundles).
                            var esc = EscapeFlat(w, n, cur, 400000);
                            if (esc == null) break;                                 // closed basin — endorheic, correctly no outlet

                            // ⚠ A LARGE EPSILON-PLATEAU IS A LAKE, AND A RIVER DOES NOT CROSS A LAKE.
                            // A filled depression IS standing water — that is what filling one means. If the
                            // nearest real fall is far away across it, this walk has not found terrain to
                            // traverse, it has ARRIVED at a water body, and the run is finished: the graph edge
                            // is complete and the lake is the next node. Crossing anyway is what ploughed a
                            // channel through the middle of unregistered basins, and because the escape is a
                            // shortest path — which on a grid is a straight line — those crossings drew the
                            // straight ladders that survived every routing fix. bodyId already stops the walk at
                            // a REGISTERED body; this catches every filled hollow the planner never registered
                            // (below its size or flat-floor tests), which is most of them.
                            //
                            flatCrossings++; flatNodes += esc.Count;
                            bool ended = false;
                            for (int k = 0; k < esc.Count; k++)
                            {
                                Step(cur, esc[k]);
                                cur = esc[k];
                                // ⚠ THE SAME STANDING-WATER TEST AS THE DESCENT STEP, AND IT BELONGS HERE MOST.
                                // Measured: 277,627 of 294,631 steps are crossing steps, so a rule enforced only
                                // on descent is enforced 6% of the time. That gap WAS the surviving artifact —
                                // the worst run walked 1,984 cells dead straight (x fixed at -3780, 3,968 m of
                                // it) and ended under 1.28 m of standing water, which the descent-side check
                                // would have refused at once had the walk arrived that way.
                                if (w[cur] > hOrig[cur] + STANDING_WATER) { AddNode(cur); reached = true; flatTerminations++; ended = true; break; }
                                if (bodyId[cur] >= 0 && bodyId[cur] != b2) { AddNode(cur); reached = true; ended = true; break; }
                                if (w[cur] <= SEA + 0.2f) { AddNode(cur); reached = true; ended = true; break; }
                                if ((k & 1) == 0) AddNode(cur);
                            }
                            if (ended) break;
                            accX = 0f; accZ = 0f;
                            continue;
                        }
                        Step(cur, best);
                        cur = best;
                        // ⚠ THE FILL STANDING ABOVE THE GROUND *IS* STANDING WATER — the exact test, replacing a
                        // guess about how wide a flat has to be before it counts as a lake. That guess failed:
                        // measured, the walk crosses filled ground in ~21-cell hops, never one long traverse, so
                        // a width threshold never fired while the chained hops still drew kilometre-long lines.
                        // Here w is the depression-filled surface and hOrig the real terrain, so w - hOrig is
                        // literally the depth of water over this column. Half a metre of it means the walk has
                        // arrived at a body, not at ground to carve, and the run ends at its shore. bodyId only
                        // catches basins the planner REGISTERED; this catches every filled hollow, which is what
                        // the straight ladders were being ploughed through.
                        if (w[cur] > hOrig[cur] + STANDING_WATER) { AddNode(cur); reached = true; flatTerminations++; break; }
                        if (bodyId[cur] >= 0 && bodyId[cur] != b2) { AddNode(cur); reached = true; break; }   // next body
                        if (w[cur] <= SEA + 0.2f) { AddNode(cur); reached = true; break; }                    // the sea
                        if ((step & 1) == 0) AddNode(cur);                          // node every second cell
                    }
                    // Fold the FINAL run in. Runs only closed on a heading change, so whichever run the walk was
                    // in when it stopped was never counted — and after the fix above the longest runs are
                    // precisely the ones that end in a termination, which is exactly the case that was invisible.
                    if (runLen > worstStraight)
                    { worstStraight = runLen; worstFrom = runStart; worstTo = cur; worstDx = prevSx; worstDz = prevSz; worstBody = b2; }
                    if (runLen > 0) { straightSum += runLen; straightRuns++; }

                    if (!reached || walkNodes.Count < 5 * 4) continue;              // immediate adjacency / dead walk
                    riverPolys.Add(walkNodes.ToArray());
                    outletsDerived++;
                }
                if (bodyCellLists.Count > 0)
                    FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: BODY OUTLETS — {alreadyDrained} bodies already drained by traces, {outletsDerived} outlet(s) DERIVED (spill saddle → fill descent → next body/sea), {seaOwned} sea-owned: the water graph is closed. FLAT RESOLUTION crossed {flatCrossings} bench(es) over {flatNodes} cell(s); {flatTerminations} run(s) ENDED at standing water (fill standing >{STANDING_WATER:0.0}m above real ground = a body, not ground to carve).");
                    FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: OUTLET WALK TELEMETRY — {totalSteps:N0} step(s): {carrySteps:N0} by carried gradient, {fallbackSteps:N0} by D8 fallback ({100.0 * fallbackSteps / Math.Max(1, totalSteps):0.#}%). Same-heading runs: longest {worstStraight}, mean {(straightRuns > 0 ? (double)straightSum / straightRuns : 0):0.0} over {straightRuns:N0} run(s). A natural channel turns constantly — a long mean here IS the straight-line artifact, measured.");
                    if (worstFrom >= 0)
                    {
                        // WHERE the worst run is, and what the surface was doing under it. Three fixes were aimed
                        // at this artifact by reasoning about which code drew it; each was a measured no-op. The
                        // cheap question is not "what would cause a straight line" but "what is true along THIS
                        // line" — fill vs real ground at both ends says immediately whether it is crossing a
                        // filled basin, the SEA-floored plane (PriorityFloodFill floors every column at SEA, so
                        // all sub-sea terrain is one enormous level plate), or genuinely planar land.
                        float fx = (worstFrom % n) * cellM - rhalf, fz = (worstFrom / n) * cellM - rhalf;
                        float tx = (worstTo % n) * cellM - rhalf, tz = (worstTo / n) * cellM - rhalf;
                        FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: WORST STRAIGHT RUN — {worstStraight} cells heading ({worstDx},{worstDz}) "
                                + $"from ({fx:0},{fz:0}) to ({tx:0},{tz:0}), body {worstBody}. "
                                + $"START fill={w[worstFrom]:0.00} ground={hOrig[worstFrom]:0.00} (over ground by {w[worstFrom] - hOrig[worstFrom]:0.00}m, SEA={SEA}) · "
                                + $"END fill={w[worstTo]:0.00} ground={hOrig[worstTo]:0.00} (over ground by {w[worstTo] - hOrig[worstTo]:0.00}m).");
                    }
            }
            ReportRunLengths(riverPolys, "as planned (before truncation and cascade)");
            TruncateRunsAtSea(riverPolys, hOrig, n, cellM, rhalf);   // (v128) a river that reaches the coast is FINISHED — nothing below sea is written
            ReportRunLengths(riverPolys, "after TruncateRunsAtSea");
            CascadeSplitPolys(riverPolys, hOrig, n, cellM);   // (v99) no sheet on steep drops — lip, gap, plunge resume (covers plan runs AND derived outlets)
            ReportRunLengths(riverPolys, "after CascadeSplitPolys");
            // #2 + #3 — overwrite each polyline's fill-table level with the BANK-DERIVED, flow-monotone, smoothed
            // ribbon level BEFORE the rasterize reads it (poly[·*4+3]). This is the single source of truth for the
            // water surface now; the fill table stays only for WHERE water pools (bodies), not the river level.
            SetBankDerivedLevels(riverPolys, hOrig, n, cellM, seeded, w, incisionCls);   // `w` IS _fillTable (aliased at the call) — the drainage solution the reference uses as THE level   // (v92) seeded bodies anchor outlet levels (base-level law)
            FiresLogger.LogInfo($"[FiresWaterDiag] CANYON LEVEL RULE: {s_canyonNodes:N0} canyon/gorge node(s) reached the level solve; the FLOOR rule moved {s_canyonMoved:N0} of them by more than 0.05 m (if this is 0 the incision class is not arriving, or the bank min already equals the floor).");
            FiresLogger.LogInfo($"[FiresWaterDiag] BASE-LEVEL ANCHOR: {s_anchorRuns:N0} run(s) through SetBankDerivedLevels, {s_anchorNodes:N0} node(s) anchored to a body surface, {s_anchorRunsNone:N0} run(s) ({(s_anchorRuns > 0 ? 100.0 * s_anchorRunsNone / s_anchorRuns : 0):F1} %) anchored NOWHERE. A plan run is a hop between two bodies whose coherence comes ENTIRELY from being pinned to their levels; a run anchored nowhere invents its own surface from two ground samples.");
            if (riverPolys != null)
            {
                int _polyId = -1;
                foreach (var poly in riverPolys)
                {
                    _polyId++;
                    int nn = poly.Length / 4;
                    bool sourceMarked = false;
                    float arcM = 0f;   // (TRACE) metres travelled ALONG this run so far — see spineArc
                    // REVERTED to the original level rule (user mandate): the
                    // monotone running-min collapsed the level downstream of
                    // any deep dip and the uncapped carve then trenched whole
                    // mountainsides (locations dragged, mountain water dug to
                    // extremes). Rivers behave exactly as before; the gorge
                    // deposit-dam case gets its OWN scoped pass later.
                    for (int seg = 0; seg < nn - 1; seg++)
                    {
                        float aar = poly[seg * 4 + 2], alv = poly[seg * 4 + 3];
                        float bar = poly[(seg + 1) * 4 + 2], blv = poly[(seg + 1) * 4 + 3];
                        float acx = (poly[seg * 4] + rhalf) / cellM, acz = (poly[seg * 4 + 1] + rhalf) / cellM;
                        float bcx = (poly[(seg + 1) * 4] + rhalf) / cellM, bcz = (poly[(seg + 1) * 4 + 1] + rhalf) / cellM;
                        float ddx = bcx - acx, ddz = bcz - acz;
                        float segLenM = Mathf.Sqrt(ddx * ddx + ddz * ddz) * cellM;   // (TRACE) this segment's length in metres
                        int steps = (int)Mathf.Max(1f, Mathf.Max(Mathf.Abs(ddx), Mathf.Abs(ddz)) * 2f);
                        for (int s2 = 0; s2 <= steps; s2++)
                        {
                            float t = (float)s2 / steps;
                            int cx = (int)Mathf.Round(acx + ddx * t);
                            int cz = (int)Mathf.Round(acz + ddz * t);
                            if (cx < 2 || cx >= n - 2 || cz < 2 || cz >= n - 2) continue;
                            int i = cz * n + cx;
                            if (underMarsh != null && underMarsh[i]) continue;   // river stops at the marsh mouth — the sheet carries the crossing
                            float area = aar + (bar - aar) * t;
                            float width = Math.Min(WaterRules.RiverProc.WidthMax, Math.Max(WaterRules.RiverProc.WidthMin, WK * (float)Math.Pow(Math.Max(area, AREA_ONSET), WP)));
                            float lvl = alv + (blv - alv) * t;   // level is already the bank-derived ribbon (relief baked in by SetBankDerivedLevels); no extra freeboard
                            // (v91) NO per-cell floor clamp on the LEVEL — docs/HYDROLOGY.md Law 2: the surface
                            // glides smooth + flow-monotone; DEPTH absorbs terrain bumps, never the surface.
                            // The old `lvl ≤ hOrig[i]−0.5` re-noised the smoothed level with every floor bump
                            // (the along-flow waviness) and un-did the monotone clamp. Obstructions are the
                            // CARVE's job now (local bound in the profile), never the level's.
                            // ESTUARY — the mouth ribbon keeps DESCENDING below sea (floored just
                            // under the lowest tide) instead of clamping flat at SEA: the river then
                            // runs down its carved bed THROUGH the tidal ocean plane and the sheets
                            // clip. The old hard clamp left the ribbon at exactly 30 while the tide
                            // sat lower — the river edge floating in mid-air over the sea (user).
                            // THE SURFACE ONLY GOES UNDER THE SEA WHERE THE BED DOES (2026-08-12, user: "this
                            // end bit needs to dive into the ground, not leave a floating mesh, and the middle
                            // bits should have carved and filled with mesh and connected"). The mouth profile
                            // was floored at SEA−1.8 EVERYWHERE, so the last reach ran below sea level while
                            // still crossing dry land — and since the sheet builder (correctly) refuses to draw
                            // inland water at/below SEA, that stretch was chopped into flickering 0.2-0.4m
                            // slivers at surf≈30.0 with a hard floating edge where it cut out. The live bank
                            // relief (0.84) pushes it under the line even earlier. A real river meets the sea
                            // AT sea level and only continues down once it is in the drowned channel: so while
                            // the bed stands above SEA the surface is floored just above it (continuous sheet,
                            // right to the shoreline), and only where the bed itself is below SEA may the
                            // ribbon dive — there the ocean plane covers it and the two clip.
                            float bedHere = hOrig[i];
                            // ═══ THE PER-CELL CEILING — RESTORED FROM THE REFERENCE (2026-08-19) ═════════
                            // docs/WATER_REFERENCE_DIVERGENCE.md §1. The FiresAdminTerrain generator this
                            // file descends from carries, at exactly this point in its own rasterizer:
                            //     if (lvl > hOrig[i] - 0.5f) lvl = hOrig[i] - 0.5f;
                            // A per-cell, terrain-referenced CEILING: the river surface may never exceed the
                            // natural pre-carve ground at the cell it is written to. Floating water is not
                            // merely avoided by it, it is UNREPRESENTABLE — which is why the reference could
                            // delete its containment crest outright and write "levels sit >=0.9 m under the
                            // original floor BY CONSTRUCTION, so banks are always natural ground standing
                            // above the water; nothing to raise, ever." That generator has no berm, no burial
                            // rim, no shelf, no feather, no drape and no skirt: grep for
                            // SetBankDerivedLevels/BuryMeshEdges/bankFloor/RIVER_RIM in it returns 0.
                            // WE DELETED THIS LINE IN v91, and the note left behind says why — the clamp
                            // "re-noised the smoothed level with every floor bump (the along-flow waviness)".
                            // That reading was right about the symptom and wrong about the trade: the waviness
                            // is cosmetic, the guarantee was structural, and everything built since to replace
                            // it (wl+0.6 berm, ContainRiversBelowBanks +0.65, BuryMeshEdges RIM +1.0, the
                            // feather, SHELF 0.25, the drape ring, the skirt, the 9-cell dilation, the mesh
                            // clip test) is reactive compensation for a level that is now free to float.
                            // ⚠ RIVER-ONLY BY CONSTRUCTION, and that is required: the user keeps the current
                            // still-water logic ("our ponds and lakes work great"). This is inside the river
                            // polyline rasterization — no body cell is ever written here — and the body anchor
                            // in SetBankDerivedLevels is untouched, so a river still meets a pond at the
                            // pond's level.
                            // ⚠ It is a CEILING, applied BEFORE the sea floor below, so the estuary rule still
                            // has the last word at the mouth: a mouth cell whose ground is near sea level gets
                            // clamped down here and then floored back up to the tidal profile, which is the
                            // same order the reference uses (ceiling, then `if (lvl < SEA) lvl = SEA`).
                            if (s_riverLevelCeiling && lvl > bedHere - 0.5f) lvl = bedHere - 0.5f;
                            // ═══ A RIVER MOUTH DIVES UNDER THE SEA, IT DOES NOT PERCH ON IT ═════════════════════
                            // (user, 2026-08-22: "every river body that ends at the ocean needs to just dive
                            // its mesh into and under the terrain at ocean level so that the ocean can take
                            // care of the logic, currently the meshes float about 1/2m above the lowest tide
                            // point".) The floor was SEA-1.8 = 28.2 while the tide pulls the sea down to
                            // SEA-5 = 25 at spring low (SPRING_LOW_TIDE_M), so at low water
                            // the ribbon was left standing up to 3 m proud of the ocean it drains into, and
                            // its edge showed. Sinking it to EstuaryDiveY (24.5, half a metre under the
                            // lowest tide) hands the mouth to the ocean surface at EVERY phase of the tide,
                            // which is the whole point: one surface owns that water, and it is not this one.
                            // Above sea level this stays a FLOOR (the ribbon may not sink below its own
                            // shoreline). At or below it the statement inverts and becomes a CEILING: the
                            // mouth is PUSHED down under the lowest tide instead of merely permitted to go
                            // there, because a floor can only ever prop the ribbon up and propping it up is
                            // the defect. This does not drag the upstream profile with it the way the old
                            // ESTUARY_FLOOR (SEA-6) as a POLYLINE END did - that is what trailed a ribbon out
                            // across the seabed and it stays reverted; this is a per-cell clamp applied only
                            // where the bed is already at or under sea level, i.e. the mouth itself.
                            // ═══ THE MOUTH DESCENDS INTO THE SEA. IT DOES NOT STEP INTO IT. ══════════════
                            // (user, 2026-08-23, from a screenshot at X-2143 Z-1962 with the overlay reading
                            // `surf Y 30.1`: "at some points were culling the mesh because the ocean owning
                            // the spot, and others we draw both meshes with ours floating".)
                            // Both symptoms were ONE line — the previous form of this was a STEP:
                            //     bed > SEA  -> floor the ribbon at SEA + 0.10  (30.1)
                            //     bed <= SEA -> slam it to EstuaryDiveY         (24.5)
                            // 5.6 m apart with nothing in between, switching on a single cell of bed height.
                            // One cell inland the sheet stands 0.1 m proud of the ocean and BOTH draw; one
                            // cell seaward it is 5.6 m under and the ocean culls it. That is exactly the
                            // alternation in the screenshot, and it is a discontinuity, not a tuning error.
                            // Now the seaward side RAMPS: the cap descends smoothly from the shoreline value
                            // to EstuaryDiveY over DIVE_BAND of bed depth, so at bed == SEA the two branches
                            // AGREE (t = 0 gives exactly the land value) and there is no step anywhere.
                            // ⚠ WHY THE 0.10 m LIP STAYS, AND WHY IT CANNOT BE TUNED AWAY. `TideHighOffsetM`
                            // is 0, so HIGH TIDE IS SEA LEVEL — and the sheet builder refuses to draw inland
                            // water at or below SEA (without that rule the fill table ponds every shallow
                            // basin). So a drawable river sheet must be ABOVE 30.0, and anything above 30.0
                            // is above high tide. Those are contradictory at the mouth; 0.10 m is the minimum
                            // expression of the contradiction, not a miss. Shrinking it only trades a visible
                            // lip for z-fighting between two near-coplanar sheets. The real remedy is to stop
                            // DRAWING the last stretch — fade the inland sheet out against sea level in the
                            // shader — which is specified for [shaders] in COORDINATION.md (2026-08-23).
                            const float MOUTH_LIP = 0.10f;   // minimum height above SEA that is still drawn
                            const float DIVE_BAND = 4.0f;    // bed depth below SEA over which the mouth reaches EstuaryDiveY
                            if (bedHere > SEA)
                            {
                                if (lvl < SEA + MOUTH_LIP) lvl = SEA + MOUTH_LIP;
                            }
                            else
                            {
                                float td = Mathf.Clamp01((SEA - bedHere) / DIVE_BAND);
                                float capD = Mathf.Lerp(SEA + MOUTH_LIP, ESTUARY_DIVE_Y, Smooth01(0f, 1f, td));
                                if (lvl > capD) lvl = capD;
                            }
                            bool fresh = widthOf[i] <= 0f;
                            if (fresh) riverCells.Add(i);
                            // ═══ WHO WINS A REPAINT — THE NOTCH, FIXED AT ITS SOURCE ═════════════════════
                            // (user, 2026-09: "we have sections of rivers that dip down BELOW their ends".)
                            // The old rule was an unconditional MIN across every writer. A meander folding
                            // back inside its own tube width therefore stamped its far-DOWNSTREAM (lower)
                            // level onto an UPSTREAM cell, and the profile climbed back out of the notch —
                            // the dip below the outlet that no later pass could legitimately repair, and the
                            // entire reason the SPINE PROFILE field-repair pass existed. Retired with it:
                            //   SAME RUN revisiting → the UPSTREAM value wins (max): a run never notches
                            //     itself.
                            //   DIFFERENT runs      → the LOWER surface wins (min): the junction rule — a
                            //     tributary meets the trunk at the trunk's height, never above it.
                            spinePoly[i] = _polyId;   // (TRACE) which RUN owns this spine cell — see the OVERLAP CENSUS
                            // (TRACE) ...and HOW FAR ALONG that run it sits, in metres. Straight-line distance
                            // is not a usable proxy inside a canyon: a confined river meanders and doubles
                            // back, so two cells 16 m apart in SPACE can be hundreds of metres apart ALONG the
                            // channel, and an 11 m level difference between them is an ordinary gradient, not
                            // a step. Every canyon "multi-level" number so far has been unable to tell those
                            // apart, which is why two fixes aimed at it measured as no-ops.
                            spineArc[i] = arcM + t * segLenM;
                            if (width > widthOf[i]) widthOf[i] = width;
                            if (fresh) { w[i] = lvl; if (lvlSrc != null) lvlSrc[i] = 1; }
                            // ⚠ SAME-POLY MAX WAS TRIED HERE AND MEASURED WRONG (3,078 rises): a fold-over cell
                            // carrying the UPSTREAM level makes the downstream traversal CLIMB out of it. Water
                            // finds its level — the LOWER surface is physical for shared cells of ANY writer, and
                            // with the clean walk flooring every node at the outlet, min can no longer dip a run
                            // below its own end (the defect that motivated max). Plain min, all writers.
                            else if (lvl < w[i]) { w[i] = lvl; if (lvlSrc != null) lvlSrc[i] = 1; }
                            if (!sourceMarked) { polySources.Add(i); sourceMarked = true; }
                        }
                        arcM += segLenM;
                    }
                }
            }

            // ═══ WHAT KIND OF CHANNEL IS THIS REACH? ══════════════════════════════════════════════════════
            // docs/CANYONS_AND_GORGES.md §3. Classified HERE, on the spine cells, because every property of
            // the carve below is already read at the SEED (width, depth, level, bank slope) so that "a
            // cross-section is ONE hydraulic object" — the classification has to live in the same currency or
            // the two banks of one transect could disagree about what landform they are in.
            // ⚠ TRAP 2 FROM THE SPEC, AND THE REASON THIS IS NOT ITS OWN PASS: canyons and gorges both deepen
            // a river channel, so run independently they double-cut the same reach and produce a trench of
            // unexplainable depth. ONE pass owns channel depth. This labels the reach; the existing carve
            // applies one profile from the label.

            // Bounded Dijkstra: geodesic distance (m) from the nearest river
            // cell, carrying that seed. Expansion stops once the valley
            // surface rises past the terrain (no further influence possible).
            // (valley wall grade is now PER-REACH — see InciseRivers' two InclineFor() calls below and
            //  WaterRules.RiverProc.InclineNarrow/Wide. RiverProc.Incline survives as the imports' midpoint.)
            var rDist = new float[n * n];
            for (int i2 = 0; i2 < rDist.Length; i2++) rDist[i2] = float.MaxValue;
            var heap = new RiverHeap(riverCells.Count * 4);
            for (int k2 = 0; k2 < riverCells.Count; k2++)
            {
                int i = riverCells[k2];
                rDist[i] = 0f;
                seedIdx[i] = i;
                heap.Push(0f, i);
            }
            while (heap.Pop(out float dcur, out int cur))
            {
                if (dcur > rDist[cur] + 0.001f) continue;    // stale entry
                int cz2 = cur / n, cx2 = cur - cz2 * n;
                int sd = seedIdx[cur];
                float swl = w[sd];
                float sw = widthOf[sd];
                for (int e = 0; e < 8; e++)
                {
                    int nx2 = cx2 + RIV_DX[e], nz2 = cz2 + RIV_DZ[e];
                    if (nx2 < 1 || nx2 >= n - 1 || nz2 < 1 || nz2 >= n - 1) continue;
                    int j = nz2 * n + nx2;
                    if (underMarsh != null && underMarsh[j]) continue;   // the valley region halts at the marsh rim
                    float nd = dcur + cellM * RIV_DL[e];
                    if (nd >= rDist[j]) continue;
                    // Per-reach falloff (2026-08-13): the expansion gate now uses THIS reach's valley grade, so
                    // a wide river's region keeps growing where a brook's has already terminated — the valley
                    // extent, not just its steepness, scales with the channel.
                    if (swl + nd * InclineFor(sw) > hOrig[j] + 4f && nd > sw) continue;
                    rDist[j] = nd;
                    seedIdx[j] = sd;
                    heap.Push(nd, j);
                }
            }

            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: valley regions solved ({rsw.Elapsed.TotalSeconds:0}s); carving channels...");
            // Solve → carve, one pass over every reached cell.
            // ═══ THE CHANNEL FEATHERS INTO A STILL BODY — IT DOES NOT ARRIVE AT FULL DEPTH ═══════════════
            // (2026-08-19, user: "so that we can feather the river mesh into the still water mesh and keep it
            // contained".) The LEVEL already tapers to a body over DROP_TAPER nodes (the backwater drawdown).
            // The DEPTH never did, so the channel drove its full cross-section straight into the shore — and
            // the moment the depth budget was raised (DepthMin/Max 0.6/2.9 → 1.5/6.0 so the ribbon would have
            // headroom to sit lower on a descent) that started excavating the BODY'S OWN SHORE on its way in.
            // Measured immediately: still-family visible lips 161 → 211, worst lip 3.07 → 6.15 m, while the
            // river numbers improved. Reordering the level solve did not touch it (211 either way), which is
            // what says the defect is the CARVE arriving, not the level meeting.
            // A byte chamfer — cells to the nearest seeded-body cell, capped — costs one n² byte array
            // (110 MB at n=10501, against the ~2 GB this function already holds) and two sweeps. `seeded` is
            // the PRE-CARVE body test used everywhere else here (v104: comparing against the carved floor made
            // every shaved cell read as "under a body").
            const int BODY_TAPER_CELLS = 12;   // ~24 m at game res — the drawdown length, matched to DROP_TAPER's reach
            byte[] bodyDist = null;
            if (seeded != null)
            {
                bodyDist = new byte[n * n];
                const byte FAR = 255;
                for (int i2 = 0; i2 < n * n; i2++)
                    bodyDist[i2] = seeded[i2] > hOrig[i2] + 0.01f ? (byte)0 : FAR;
                for (int z2 = 0; z2 < n; z2++)          // forward sweep
                    for (int x2 = 0; x2 < n; x2++)
                    {
                        int i2 = z2 * n + x2; int v = bodyDist[i2];
                        if (v == 0) continue;
                        if (x2 > 0 && bodyDist[i2 - 1] + 1 < v) v = bodyDist[i2 - 1] + 1;
                        if (z2 > 0 && bodyDist[i2 - n] + 1 < v) v = bodyDist[i2 - n] + 1;
                        bodyDist[i2] = (byte)v;
                    }
                for (int z2 = n - 1; z2 >= 0; z2--)     // backward sweep
                    for (int x2 = n - 1; x2 >= 0; x2--)
                    {
                        int i2 = z2 * n + x2; int v = bodyDist[i2];
                        if (v == 0) continue;
                        if (x2 < n - 1 && bodyDist[i2 + 1] + 1 < v) v = bodyDist[i2 + 1] + 1;
                        if (z2 < n - 1 && bodyDist[i2 + n] + 1 < v) v = bodyDist[i2 + n] + 1;
                        bodyDist[i2] = (byte)v;
                    }
            }

            // ═══ WALK EACH SPLINE HEADWATER -> END AND CARRY A NON-INCREASING BED BOUND ═══════════════════
            // The path statement the carve's incision bound needs (see its use below). Flow order comes from
            // catchment AREA, which grows monotonically downstream — the same direction witness the planner and
            // ROUTE VALIDATION use — so the low-area end IS the headwater. One dictionary keyed by spine cell;
            // the classified-canyon population is far too small to matter here, this covers EVERY run.
            var monoBound = new Dictionary<int, float>(1 << 16);
            if (riverPolys != null)
            {
                float WKb = WaterRules.RiverProc.WidthK, WPb = WaterRules.RiverProc.WidthP;
                float AObv = WaterRules.RiverProc.AreaOnset;
                float WMINb = WaterRules.RiverProc.WidthMin, WMAXb = WaterRules.RiverProc.WidthMax;
                foreach (var poly in riverPolys)
                {
                    int nnb = poly.Length / 4;
                    if (nnb < 2) continue;
                    bool srcFirst = poly[2] <= poly[(nnb - 1) * 4 + 2];
                    float mono = float.MaxValue;
                    for (int idx = 0; idx < nnb; idx++)
                    {
                        int k = srcFirst ? idx : nnb - 1 - idx;          // HEADWATER -> END
                        int bx = Mathf.Clamp((int)((poly[k * 4] + rhalf) / cellM + 0.5f), 0, n - 1);
                        int bz = Mathf.Clamp((int)((poly[k * 4 + 1] + rhalf) / cellM + 0.5f), 0, n - 1);
                        int bi = bz * n + bx;
                        float wid = Math.Min(WMAXb, Math.Max(WMINb, WKb * (float)Math.Pow(Math.Max(poly[k * 4 + 2], AObv), WPb)));
                        float dep = RemapClamp(wid, WMINb, WMAXb, WaterRules.RiverProc.DepthMin, WaterRules.RiverProc.DepthMax);
                        float want = hOrig[bi] - (dep + 12f);
                        if (want < mono) mono = want;                    // the bed may only ever go DOWN
                        monoBound[bi] = mono;
                    }
                }
            }

            var paintLvl = new float[n * n];
            for (int i2 = 0; i2 < paintLvl.Length; i2++) paintLvl[i2] = float.MaxValue;
            for (int i2 = 0; i2 < rDist.Length; i2++)
            {
                int sd = seedIdx[i2];
                if (sd < 0) continue;
                if (underMarsh != null && underMarsh[i2]) continue;   // never carve/berm inside a marsh
                // NEVER CARVE INSIDE A BODY (2026-08-11, user: "why would we be carving a river at a lowland
                // body anyway — the river carves up to the inlet, and leaves the outlet"). A marsh already had
                // this guard; lakes and ponds did not, so the ribbon cut its own trench and berm THROUGH a
                // lake's shore and floor. That is what the float overlay shows: the magenta is not lakes
                // floating, it is a ring at every river↔body junction where the carve undercut the body's own
                // containment. The body owns its water and its ground: the river stops at the shore, the body
                // surface carries it across, and the outlet resumes past the far edge.
                if (seeded != null && seeded[i2] > hOrig[i2] + 0.01f) continue;
                float width = widthOf[sd];
                // Depth keyed to the 2-14m width range, dug DEEP: a 2m brook is
                // 1.5m (wadeable, real thalweg), a 7m river ~4.2m, a 14m trunk
                // 8m (swimmable). The pool-riffle term below then modulates it.
                float depth = RemapClamp(width, WaterRules.RiverProc.WidthMin, WaterRules.RiverProc.WidthMax, WaterRules.RiverProc.DepthMin, WaterRules.RiverProc.DepthMax);
                float wl = w[sd];
                // v34 — POOL-RIFFLE bed (fluvial reference; SimpleHydrology's
                // stream+pool coupling done closed-form): a MONOTONE bed has
                // no basins, so the pour settle hugged it and drained every
                // inland river dry (audit: 3,958 wet cells on a whole map).
                // Shallow sills every ~60-160m dam deep pools; settled water
                // fills the chain — pools hold, sills read as rapids.
                {
                    int sdz = sd / n, sdx = sd - sdz * n;
                    float poolT = Smooth(0.30f, 0.70f,
                        Fbm(sdx * cellM, sdz * cellM, 1f / 110f, 2, ms + 87));
                    // Riffles are PROPORTIONALLY shallower, not collapsed to the global floor. Lerping to
                    // DepthMin meant a 20 m river's riffles were cut to a brook's depth (the "river that reads
                    // super shallow"). 0.65× keeps the pool-riffle rhythm while a big river stays a big river.
                    depth = Mathf.Lerp(depth * 0.65f, depth, poolT);
                }
                // MOUNTAIN INCISION — width-depth keys the bed to CATCHMENT, so
                // a steep headwater brook (small catchment → the 1.5m floor)
                // perches its ribbon on the slope instead of laying it IN. Add
                // height+slope incision at the thalweg: the resolution-independent
                // mirror of the Gaea import channel (alt/120·2.5 + slope·2, where
                // slope/2.4 ≡ the Gaea relief/12 at its 5m cell). Deep troughs up
                // high on steep ground; ~0 in lowland flats, so valley trunks are
                // unchanged. Keyed to the SEED (thalweg) so the channel depth is
                // consistent along the river, like width/level/pool-riffle above.
                {
                    int szc = sd / n, sxc = sd - szc * n;
                    float lo = hOrig[sd], hi = hOrig[sd];
                    if (sxc > 0)     { float v = hOrig[sd - 1]; if (v < lo) lo = v; if (v > hi) hi = v; }
                    if (sxc < n - 1) { float v = hOrig[sd + 1]; if (v < lo) lo = v; if (v > hi) hi = v; }
                    if (szc > 0)     { float v = hOrig[sd - n]; if (v < lo) lo = v; if (v > hi) hi = v; }
                    if (szc < n - 1) { float v = hOrig[sd + n]; if (v < lo) lo = v; if (v > hi) hi = v; }
                    float alt = hOrig[sd] - SEA;
                    float slope = (hi - lo) / cellM;
                    float mtnDepth = Mathf.Clamp01(alt / WaterRules.RiverProc.IncisionAltRef) * WaterRules.RiverProc.IncisionAltGain
                                   + Mathf.Clamp01(slope / WaterRules.RiverProc.IncisionSlopeRef) * WaterRules.RiverProc.IncisionSlopeGain;
                    // (v104) mountain incision is a TROUGH-SEATING nudge, not a canyon license — on the real
                    // massif terrain (v102: legitimately tall EVERYWHERE) the uncapped addend dug every
                    // mountain channel 15-25m deep and the fill painted the whole massif wet (audit, seed
                    // 49185460: 56k wet cells >200m, mean 16m deep, dominant bucket 16-32m). +4m at most;
                    // real gorges become a DELIBERATE trichotomy feature (HYDROLOGY.md Part IV), not a gain.
                    depth = Math.Min(WaterRules.RiverProc.DepthCap, depth + Math.Min(mtnDepth, 4f));
                }
                // …and now feather that depth OUT as the channel approaches a still body (see the chamfer
                // above). At the shore the section is ~0.9 m — the same order as the body's own bowl edge in
                // FiresWaterConform (`0.8 + d·0.12`) — so the two beds MEET instead of the river driving a
                // full-depth trench through the lake's shore. Smoothstep, so there is no step where the taper
                // starts. The berm is deliberately still armed at these junctions (a cell touching still water
                // keeps the full rim — see `stillDemand`), which is the user's own scope for it: "rivers
                // meeting still water is specifically where the berm logic could be useful".
                if (bodyDist != null)
                {
                    float bt = Mathf.Clamp01(bodyDist[i2] / (float)BODY_TAPER_CELLS);
                    bt = bt * bt * (3f - 2f * bt);
                    depth = Mathf.Lerp(0.9f, depth, bt);
                }
                float half = Math.Max(1.5f, width * 0.5f);

                // irregular natural banks — noise widens/narrows the distance
                // outside the wet core, never at the thalweg.
                int zC = i2 / n, xC = i2 - zC * n;
                float bankNoise = (Fbm(xC, zC, RS / 9f, 2, ms + 21) - 0.5f) * 6f;
                float d = rDist[i2];
                float dN = Math.Max(0f, d + bankNoise * Smooth(0.3f * half, 1.4f * half, d));

                // channel: BiasedRational — −depth at the thalweg, asymptotic
                // linear banks at slope `steep`; UNCAPPED so the river always
                // gets through (bumps on the line become incised gorges).
                // (Phase 2, 2026-08-15) BANK SLOPE IS A PROPERTY OF THE REACH, KEYED TO DISCHARGE.
                // Was `RemapClamp(hOrig[i2], 3f, 20f, 2.9f, 4.4f)`, which had two defects: it saturates at
                // 20 m elevation while SEA is 30, so every land cell took the 4.4 endpoint and the altitude
                // ramp was dead code; and it read hOrig[i2] PER-CELL, so the two banks of one cross-section
                // could take different wall angles — the very coupling the incision bound was fixed for
                // ("a cross-section is ONE hydraulic object", :7478). Now read at the seed off `width`, which
                // is already the discharge proxy (WidthK·area^WidthP): a brook cuts a tight slot, a trunk a
                // broader canyon. This governs how much material the wall cut removes; it does NOT touch the
                // thalweg, so it cannot dry a reach.
                // ⚠ REVERTED 2026-08-15 with the two changes below. The finding stands — this ramp saturates
                // at 20 m elevation while SEA is 30, so it is a de-facto constant 4.4 on land, and it reads
                // per-CELL across one cross-section — but changing the bank angle also changes containment,
                // and it went out in the same batch as the floating-river regression. Restore the original
                // and re-land it on its own, gated on AuditWater's float count.
                float steep = RemapClamp(hOrig[i2], 3f, 20f, 2.9f, 4.4f);
                // (v91) CARVE FROM THE LEVEL — docs/HYDROLOGY.md Part II (self-formation): the channel is MADE
                // BY its water. bed = L − depth·shape(dN), rising to meet L EXACTLY at the wetted edge, so the
                // T-section fills bank-to-bank BY CONSTRUCTION (v89's hOrig[sd] base decoupled bed from surface —
                // only the deep line wetted, and the unpainted uphill half kept fill-table water: the tilted bar).
                // L is bank-derived (min channel-edge bank − 0.84, smooth + flow-monotone), so L−depth stays
                // pinned to the local banks — no bookkeeping gorge. The LOCAL BOUND caps the cut at (depth+4)
                // under the cell's own natural ground: where a mis-routed spine crosses high terrain the bed
                // stays high and DRY (a visible break, not a gorge — the pool|gorge|tunnel trichotomy owns
                // those reaches later, HYDROLOGY.md Part IV).
                // (2026-08-13) Now expressed through the SHARED cross-section (ChannelShape) rather than
                // inlining the BiasedRational here — algebraically identical (verified to 1e-9 across the
                // half/depth/steep range: wl + depth·(term − 1) ≡ wl − depth·(1 − term)), so this is a pure
                // refactor. It is the same curve StampChannelCell now uses, which is the whole point: there is
                // one channel section in this generator, and its rim behaviour is defined in exactly one place.
                float profile = wl - depth * ChannelShape(dN / half, half / depth, steep);
                // (user 2026-08-11: "then let it fucking dig deeper than 4m — give it 12m") The local-incision
                // bound was +4m: where a reach had to cross ground higher than that above its ribbon level the
                // bed stayed UP, the cell fell out of the fill gate and the reach went DRY — the visible river
                // break / "stops short of the lake". 12m lets the channel dig through those approaches.
                // ═══ THE BOUND IS A PROPERTY OF THE REACH, NOT OF THE CELL (2026-08-13) ═══════════════════
                // This read hOrig[i2] — each cell's OWN natural ground — so the incision limit varied ACROSS
                // the cross-section. On any real slope hOrig swings metres between one bank and the other, so
                // the bound bit on some cells of a transect and not on others: the bed landed below wl on the
                // downhill half and above it on the uphill half, and the fill gate then called one cell wet and
                // its neighbour dry. That is the shard mechanism CascadeSplitPolys describes in its own note —
                // "the carve's local bound + the fill gate fight cell-by-cell there and emit patchy floating
                // shards" — and the v99 cascade rule was invented to DELETE the reaches it produced.
                // A cross-section is ONE hydraulic object: its floor belongs to the reach. Read the limit at
                // the seed (thalweg) and the whole T-section shares it. Where a spine genuinely crosses ground
                // too high to cut, the entire reach is limited together — one clean break, never speckle.
                // (Phase 2, 2026-08-15) THE INCISION BUDGET IS PER-REACH, FROM DISCHARGE — was a flat +12 for
                // everything, so a 2 m brook was licensed to cut as deep as a 20 m trunk. Keyed to `width`
                // (already WidthK·area^WidthP) and read at the seed, like the bound itself. The trunk endpoint
                // stays 12 so the big rivers the 12 was chosen for are unchanged; only small channels tighten.
                // ⚠ REVERTED 2026-08-15 with the valley shave above — same reason, same missing float gate.
                // Tightening the bound for small channels holds their bed UP, which is one of the ways a
                // sheet ends up above its ground. Restore the flat +12 until the float audit is the gate.
                // ═══ THE BED DESCENDS FROM HEADWATER TO END — THE BOUND IS A PATH STATEMENT ══════════════
                // (2026-08-22, user: "we should know its origin point and its end point and we should know
                // the y levels of the bed at each point to make sure they are descending".)
                // This bound is what actually shapes the visible canyons — NOT ExcavateCanyons, which its own
                // log shows touching 490 canyon + 862 gorge spine cells in the whole world while the terrain
                // test finds 13,572 canyon-like water cells. The slots you stand in are cut by THIS.
                // And it was per-cell and terrain-referenced: `hOrig[sd] − (depth + 12)`. Wherever a reach
                // crosses higher ground the bound rises with it and holds the bed UP, the water pools behind
                // that rise, and the profile reads as river → step UP to a wide pool → drop → step UP again.
                // A running minimum of the same quantity taken along the spline from the headwater makes the
                // bound non-increasing downstream, so the bed can only ever fall and the pools settle at the
                // base of the slot instead of perching partway up it. Falls back to the per-cell value where
                // the walk never reached this spine cell.
                float boundLo = monoBound.TryGetValue(sd, out float mb) ? mb : hOrig[sd] - (depth + 12f);
                if (profile < boundLo) { profile = boundLo; if (dN <= half) System.Threading.Interlocked.Increment(ref s_boundHits); }   // (TRACE) the local-incision bound held the bed UP inside the wetted width — this is what dries a reach
                // (TRACE, Phase 2) ARE OUR CANYONS EARNED? The deep carve is dominated by (hOrig[i2] − hOrig[sd])
                // — a cell whose own ground stands far above the thalweg — which neither the bank slope nor the
                // incision budget bounds. Whether that is a FEATURE or a defect depends entirely on WHICH
                // channels are doing it: a 20 m trunk sawing a gorge is a real canyon, a 2 m brook doing the
                // same is not. Bucket every >20 m cut by the reach's width so the answer is measured, not argued.
                {
                    float cut = hOrig[i2] - profile;
                    if (cut > 20f)
                    {
                        if (width < 5f) System.Threading.Interlocked.Increment(ref s_deepBrook);
                        else if (width < 10f) System.Threading.Interlocked.Increment(ref s_deepStream);
                        else if (width < 15f) System.Threading.Interlocked.Increment(ref s_deepRiver);
                        else System.Threading.Interlocked.Increment(ref s_deepTrunk);
                    }
                }
                // ═══ ONE EVALUATION FEEDS BOTH THE BED AND THE FILL (2026-08-13) ══════════════════════════
                // bedDemand is what the CHANNEL demands of the ground at this cell — the layer, in UE's terms
                // (their water body writes a dedicated landscape edit layer instead of mutating the sculpt, so
                // `bed = surface − Depth·Curve(t)` with Curve ≥ 0 holds BY CONSTRUCTION and floating water is
                // simply unrepresentable). We cannot afford a real delta layer here — three float[n*n] at
                // n=10501 is ~1.3GB on top of rDist/seedIdx/paintLvl — but we get the same guarantee for free
                // by making the terrain write and the fill decision read the SAME value instead of the fill
                // interrogating whatever h ended up as. Everything that used to modify the bed AFTER the write
                // (the wetted-core cap below) is folded in HERE, before it, so there is exactly one number.
                float bedDemand = profile;
                // WETTED-CORE GUARANTEE, folded in: a real river erodes EVERYTHING inside its wetted width
                // below its surface (docs/HYDROLOGY.md Part II — the bed meets L only AT the edges; a bar above
                // the surface mid-channel is a deposition feature, not leftover terrain). Kills the estuary
                // MEDIAL SPIKES (parallel corridors carving to ~wl at their shared midline) and the mid-chute
                // mounds the sheet had to ride over. UNCONDITIONAL (user 2026-08-11): it used to switch off on
                // "obstructed" reaches, deferring them to a trichotomy that was never built, so those reaches
                // came out DRY and the river visibly stopped and restarted. Applied to bedDemand rather than
                // clamping h afterwards, so SmoothMinCubic eases the core edge instead of leaving a hard step.
                // ⭐ AND THE CORE IS CUT BELOW THE BANK RELIEF, NOT 0.35 m ABOVE IT (user, 2026-09-02:
                // "the river bed should sit low enough that it's never dry here", and "make sure the carve
                // runs into and overlaps the still water slightly").
                // 0.35 m was the depth of a puddle, and the renderer takes DefaultRiverRelief (0.84 m) off
                // every inland surface before drawing it — so a core cut 0.35 m under its own water level is
                // drawn 0.49 m UNDER ITS OWN BED. Wet in the field, dry on screen, and dry EVERY TIME, which
                // is why this reads as a defect at the mouths rather than as a rare one. It is also thin
                // enough that any later raise buries it outright.
                // WETTED_CORE clears the relief with margin, so a carved channel holds water at every phase
                // of the render, and at a junction the channel therefore OVERLAPS the still body it runs
                // into instead of feathering out to a lip just short of it. The overlap is the point: the
                // two beds are continuous under one surface.
                // ⚠ This is the ONE number that decides whether a river is wet. It is not a look tweak — if
                // the render relief ever moves, this must stay above it.
                const float WETTED_CORE = 1.20f;   // > FiresWorldGenBase.DefaultRiverRelief (0.84) + margin
                if (dN <= half * 0.95f && bedDemand > wl - WETTED_CORE) bedDemand = wl - WETTED_CORE;
                float chan = SmoothMinCubic(h[i2], bedDemand, 3f);

                // valley: shave approach bumps toward the solved surface, capped so gorge/canyon walls survive.
                // (Phase 2, 2026-08-15) THE SHAVE CAP IS PER-REACH, FROM DISCHARGE — was a flat 24 m for every
                // river, which is what let a 2 m brook take 24 m off a ridge exactly like a 20 m trunk. This is
                // the pass that actually digs the canyons: measured per-pass, FiresWaterConform and the tarns
                // cut nothing past 3.5 m, while InciseRivers produces every >20 m cell and the 53.6 m max, most
                // of it on DRY valley wall rather than in the channel. The grade was already discharge-keyed
                // (InclineFor: 0.75 brook → 0.22 trunk); only the depth cap was not. Trunk endpoint stays 24 so
                // the big rivers are unchanged.
                // ⚠⚠ REVERTED 2026-08-15 — DO NOT RE-APPLY WITHOUT A FLOAT GATE. Budgeting this by discharge
                // (ValleyShaveMin 8 → Max 24) produced FLOATING RIVERS everywhere: the shave is not just
                // cosmetic valley shaping, it is CONTAINMENT. Capping it leaves the ground high while `wl` —
                // the traced water level — is unchanged, so the sheet perches above the terrain beside it.
                // My gate was s_dryCore, which only detects the OPPOSITE failure (bed held UP inside the
                // wetted width ⇒ dry) and is structurally blind to water-above-ground. The right gate already
                // exists and I did not use it: AuditWater() — "the height-aware float detector the game runs
                // (inland-overflow audit)", which prints `lvl Xm stands Ym over bank` / `N overflows`.
                // ⚠⚠ THE THIRD ABSOLUTE-HEIGHT WRITE, AND THE ONE v132 MISSED (v133). `hOrig[sd] + d·Incline`
                // is a pure geometric CONE rising from the thalweg — no terrain in it at all — so wherever it
                // bites, the ±1.75 m baked detail is replaced by a smooth ramp. v132 found and fixed exactly
                // this shape in the berm (`wl + 0.6`) and the feather (`hi − FEATHER`) and stopped there;
                // this one survived because it hides inside a `SmoothMinCubic`, which eases the JOIN but
                // still lands the interior on the bare cone. It is by far the widest of the three — the shave
                // reaches out to the full valley falloff, not a 2·half ring — and a stage bisect holds it
                // solely responsible: auditing four grid snapshots against ONE water mask gives flat land
                // 2.14% before the planner, 2.18% after conform, 2.18% after the tarns, and 11.86% after this
                // pass, with all 136 large flat regions appearing here and nowhere earlier.
                // Same repair as the other two: compute the constraint, then put the ground's own grain back
                // on top of it.
                // ⚠ THE DETAIL COMES FROM hOrig, NOT h. The berm and feather read live `h`, which is being
                // rewritten by this very loop — tolerable there only because the loop is serial. hOrig is the
                // pre-carve clone: it cannot race, it cannot depend on iteration order, and it is the actual
                // surface BakeSurfaceDetail textured. Determinism here is not optional (GEN_VERSION 131: a
                // neighbour-reading race in a grid pass let host and client build different terrain).
                // ⚠ SIGNED, unlike the berm and feather, and the asymmetry is deliberate. Those two RAISE
                // ground to contain water, so a negative detail punches a hole through a bank (measured:
                // STRICT float 0 → 118, mesh lips 69 → 9,125). This one only ever LOWERS — `h` takes `outH`
                // only where `outH < h` — and it cannot affect containment inside the sheet at all, because
                // there `outH = min(chan, vall) ≤ chan ≤ wl − 0.35` holds whatever `vall` is. So the grain may
                // run both ways, which is what keeps the shaved valley wall reading like the ground beside it
                // instead of like a bevel with a rough edge. Verified against AuditWater, which the reverted
                // 2026-08-15 shave-cap note names as the correct gate for any change to this line.
                float vdet = 0f;
                {
                    int vx = i2 % n, vz = i2 / n;
                    if (vx > 0 && vx < n - 1 && vz > 0 && vz < n - 1)
                    {
                        float avgV = 0.25f * (hOrig[i2 + 1] + hOrig[i2 - 1] + hOrig[i2 + n] + hOrig[i2 - n]);
                        vdet = Mathf.Clamp(hOrig[i2] - avgV, -0.35f, 0.35f);   // same bound as the berm/feather
                    }
                }
                // ═══ THE CANYON / GORGE PROFILE — ONE PASS OWNS CHANNEL DEPTH (docs/CANYONS_AND_GORGES.md §3) ═
                // Trap 2 of that spec is explicit: canyons and gorges both deepen a river channel, so run as
                // separate passes they double-cut the same reach and produce a trench of unexplainable depth.
                // So the landform is expressed HERE, in the one place that already decides the valley, by
                // changing TWO things about the wall and nothing about the bed.
                //
                // ⚠ THE WALL GRADE, NOT THE DEPTH. It is tempting to make a canyon by digging deeper, and
                // GEN_VERSION 104 records what that costs: an uncapped mountain-incision addend dug every
                // channel 15-25 m and the fill painted the whole massif wet (56k wet cells above 200 m). The
                // depth term also sets the bed against the water level, so moving it is how reaches go dry or
                // float. The wall is the safe lever AND the correct one — §1a says lithology writes the
                // profile, and what a canyon reads as is its WALLS, not a deeper stream.
                // A steeper grade means the cone rises faster from the thalweg, so LESS material is removed
                // and the channel sits in a narrow slot instead of a broad shallow valley. Containment is
                // untouched: inside the wetted width `chan` still binds (chan <= wl-0.35 by construction and
                // outH = min(chan, vall)), so this cannot lift the sheet off its bed.
                //
                // ⚠ BENCHING IS WHY FiresRock HAD TO EXIST. §1a: "Lithology writes the profile. Hard layers
                // capping soft ones produce the stair-stepped walls, benches and overhangs that read as
                // 'canyon' — a uniform slope does not, however deep it is." So the wall is displaced by the
                // resistance of the rock AT THE HEIGHT THE WALL HAS REACHED: a resistant bed stands proud
                // (less cut, a cliff band), a soft one is cut back (more cut, a tread). Because FiresRock's
                // beds are horizontal-ish with a regional dip, the steps come out horizontal and traceable
                // across the landscape, which is what makes mesa country read as mesa country.
                byte incCls = (incisionCls != null && sd >= 0 && sd < incisionCls.Length) ? incisionCls[sd] : INC_NONE;
                float wallGrade = InclineFor(width);
                if (incCls == INC_CANYON) wallGrade *= CANYON_WALL_MUL;
                else if (incCls == INC_GORGE) wallGrade *= GORGE_WALL_MUL;
                float wallY = hOrig[sd] + d * wallGrade + vdet;
                if (incCls != INC_NONE)
                {
                    float bx = xC * cellM - _half, bz = zC * cellM - _half;
                    float res = FiresRock.Hardness(bx, wallY, bz, seed);          // 0 soft .. 1 resistant
                    // Centred on 0.5 so a reach in average rock keeps the grade it was given; only the
                    // CONTRAST between beds displaces the wall, which is exactly what benching is.
                    wallY += (res - 0.5f) * 2f * (incCls == INC_CANYON ? CANYON_BENCH_M : GORGE_BENCH_M);
                }
                float valley = Math.Max(wallY, h[i2] - 24f);
                float vall = SmoothMinCubic(h[i2], valley, 8f);

                float outH = Math.Min(chan, vall);
                // ═══ A CANYON HAS A FLOOR YOU CAN WALK ON ═══════════════════════════════════════════════
                // (user, 2026-08-31: "canyons need to carve out wide floors that allow us land on each side
                // of the central river to walk and explore, we should have a cliff wall, a meter or two of
                // terrain, and THEN the carved depression of the river in the center, another meter or two
                // of terrain, then the cliff wall".)
                // Today a canyon has no floor at all, and the reason is visible two lines up: the ground
                // outside the channel is shaped by `vall`, a cone rising at `wallGrade` from hOrig[sd] — the
                // thalweg's NATURAL height. In a slot that is metres above the water, so the cone starts high
                // and shaves almost nothing; the only thing cut near the river is the channel bowl itself.
                // The result is a narrow trench in natural ground with the water wall-to-wall, which is
                // exactly what the user is describing and why there is nowhere to stand.
                // So add the missing term: hold the ground FLAT just above the waterline for a shelf either
                // side, then let the cliff take over. Cross-section becomes
                //     bowl (0..half) | flat shelf (half..half+SHELF) | cliff at CLIFF_GRADE
                // which is the section asked for, in that order.
                // ⚠ THREE THINGS KEEP THIS OFF EVERY RIVER THAT ALREADY WORKS — the user's constraint is
                // "we need to be SUPER careful here not to screw up any existing river or water logic":
                //   1. GATED ON A REAL SLOT. It fires only where the thalweg's own natural ground stands
                //      CANYON_SLOT_M above the water. That is the SAME 12 m test IsCanyonWaterAt uses, so
                //      what the game CALLS a canyon and what the carve SHAPES as one cannot drift apart —
                //      the classifier/terrain divergence has already cost this workstream once.
                //   2. LOWERING ONLY, like every other term here (`outH < h[i2]`). It can never raise ground,
                //      so it cannot float a sheet, strand a cell dry, or disturb containment.
                //   3. NO BODY MAY BE UNDERCUT. A lake's own rim is not ours to cut; the same guard the
                //      excavation needed after it cut one and put +88 cells of float on the map.
                // `vdet` is carried into the floor so the shelf keeps the local detail and does not read as
                // a glassy plane.
                if (s_canyonShelf && canyonFloorW.Count > 0 && canyonFloorW.TryGetValue(i2, out float plannedHalfW)
                    && (seeded == null || seeded[i2] <= hOrig[i2] + 0.01f))
                {
                    // The floor is as wide as the PLAN said, not as wide as this cell's terrain happens to
                    // suggest: bowl in the middle, walkable shelf either side out to FloorHalfW, then the
                    // cliff. `plannedHalfW` already includes the channel half-width plus ShelfM, decided at
                    // siting from the run's own width.
                    float floorY = wl + CANYON_FLOOR_UP + vdet;
                    if (d > plannedHalfW) floorY += (d - plannedHalfW) * CANYON_CLIFF_GRADE;   // …then the wall
                    if (floorY < outH)
                    {
                        outH = floorY;
                        System.Threading.Interlocked.Increment(ref s_canyonShelfCells);
                        if (wl > 0f && hOrig[sd] - wl > s_shelfDeepSlot) { s_shelfDeepSlot = hOrig[sd] - wl; s_shelfDeepCell = i2; s_shelfDeepWl = wl; }
                    }
                }
                if (outH < h[i2]) { h[i2] = outH; if (wsrc != null) wsrc[i2] = (byte)(vall < chan ? 1 : 2); }

                // (v92 wetted-core cap MOVED UP into bedDemand — it is the same guarantee applied BEFORE the
                // write instead of clamping h after it. Proof it still holds: chan = SmoothMinCubic(h, bedDemand)
                // ≤ bedDemand ≤ wl−0.35 inside the core, outH = min(chan, vall) ≤ chan, and h only ever takes
                // outH when outH < h — so for dN ≤ 0.95·half the final h is ≤ wl−0.35 in every branch.)
                // (v24 containment crest deleted — levels sit ≥0.9m under the
                // original floor by construction, so banks are always natural
                // ground standing above the water; nothing to raise, ever.)

                // v30 — EVERYTHING the carve dug below the waterline inside
                // the trench corridor is WET BED, at the LOWEST level of any
                // stem claiming it (transect-proven: a tributary's Dijkstra
                // territory painted a trunk's trench 8m above the trunk's own
                // water — min-write makes the trunk rule shared trenches; the
                // trib steps down into it at its mouth, a natural fall).
                // RIBBON FILL (2026-08-07) — water covers EVERY cell the carve left below the level: the whole
                // T-section bank-to-bank, not just the narrow core. The channel profile above already rises to wl at
                // the bank, so `h < wl` fills exactly to the banks and STOPS there (terrain standing above wl IS the
                // dry bank). This puts "level across the entire T-section" into the DATA, so the sheet — which now
                // renders from this field — tucks into the banks like a pond instead of drawing a 1-cell thread.
                // (v91) cap tightened 2.0→1.3·half: the carve-from-L profile crosses wl at dN≈half, so 1.3·half
                // is a pure leak guard (the blend tail reaching an unrelated low spot) — and it cuts the wet-cell
                // count the sheet builder walks every apply (the apply.water main-thread cost).
                // THE WATER FILLS THE CARVE — NOT THE FLATS (2026-08-11, user: "you cannot just make the rivers
                // wider at the mesh, you need to carve them wider, and deeper, and lower the mesh into them so
                // it remains contained"). I had pushed this to 1.8 half-widths to chase a buried edge; that is
                // the exact failure docs/HYDROLOGY.md Law 5 warns about — treating the channel as "a ditch water
                // was poured into" and then asking which cells happen to lie under L. Past the carve's own
                // wetted edge the test admits any flat ground that is merely a few cm below the surface, so the
                // sheet crawled out over sand as a jagged 0.9 m apron. The carve profile is built to rise and
                // meet L exactly at dN = half (shape(edge)=0), so filling to 1.05·half fills the channel
                // bank-to-bank BY CONSTRUCTION and stops where the bed does. Width comes from the WIDTH LAW
                // (which now carves wider), never from the fill radius.
                // ═══ THE FILL READS THE CHANNEL, NOT THE GROUND (2026-08-13) ═════════════════════════════
                // Was `h[i2] < wl − 0.05f`: the fill INTERROGATED the terrain the carve had just written, so
                // "is this cell wet" was answered by whatever survived the bound, the valley shave, the smooth
                // blend and any earlier pass — a different question from "does the channel put water here".
                // Reading bedDemand asks the channel directly, which is the UE invariant: the water body and
                // the landscape deformation come from ONE spline evaluation, so they cannot disagree.
                //   • STRICTLY SAFE — chan = SmoothMinCubic(h, bedDemand, 3) ≤ bedDemand and outH ≤ chan, so
                //     h ≤ bedDemand always: bedDemand < wl−0.05 IMPLIES h < wl−0.05. Every cell this admits
                //     really does have its bed below the surface. It is a stronger test, not a looser one.
                //   • AND IT DROPS THE CELLS IT SHOULD — ground that merely happens to dip a few centimetres
                //     below wl outside the channel no longer reads as wet. That is the "sheet crawled out over
                //     the sand as a jagged 0.9m apron" failure in the 1.05·half note: width belongs to the
                //     WIDTH LAW, never to whatever the terrain is doing nearby.
                if (dN <= half * 1.05f && bedDemand < wl - 0.05f)
                {
                    mask[i2] = 1;
                    if (paintLvl[i2] > wl) paintLvl[i2] = wl;
                }
                // ═══ THE SUBMERGED SHORE IS PART OF THE RIVER — RIVERS FOOTPRINT LIKE PONDS DO ═════════════
                // (2026-08-18, user: "our lake and pond logic already handles this cleanly, why do our rivers
                // act different?" — the right question, and the answer was in the two footprint rules.)
                // A BODY's footprint is `FiresWaterPlanner.ExpandFootprint`, which floods across any neighbour
                // with `g[j] < level`, and its own header states the invariant this buys: "The true shoreline
                // is where ground rises past the level — so a body's edge can NEVER stand over lower dry
                // ground." A RIVER's footprint was the tube above: `dN <= 1.05·half && bedDemand < wl−0.05` —
                // distance from the spine and the CHANNEL PROFILE, with no term that reads the terrain at all.
                // So wherever the real ground beside the channel lies below the water level but outside that
                // tube, the river's wet region ENDED OVER GROUND LOWER THAN ITS OWN SURFACE — precisely the
                // case the body rule exists to make impossible. Every render-side patch after that (skirt
                // gate, drape, apron) was trying to hide a footprint that was already wrong here.
                // Now the river takes the pond's test on the same terms — ground below the level is wet —
                // BOUNDED TO THE BERM RING (2·half) so the `wl + 0.6` berm still owns the bank just outside it
                // and the ribbon cannot creep across an open floodplain. Order matters: this sets mask 1, and
                // the berm below deliberately skips mask 1/3, so a cell we call submerged does not also get
                // bermed — it is riverbed, not bank.
                // ⚠ HONEST LIMIT: this is a per-cell depth test inside the ring, not a connected flood like
                // ExpandFootprint. Within 2·half of the channel a below-level cell is essentially always the
                // river's own shore, but a hollow cut off by a ridge inside the ring would also fill. Its edge
                // would sit against that ridge (ground above the level ⇒ buried), so it cannot produce a
                // floating edge — it would read as a backwater. If those show up, the upgrade is a real BFS
                // from the wet cells rather than a wider or narrower radius.
                else if (dN <= half * 2.0f && h[i2] < wl - 0.05f && wl > SEA + 0.3f && hOrig[i2] > SEA)
                {
                    mask[i2] = 1;
                    if (paintLvl[i2] > wl) paintLvl[i2] = wl;
                    s_shoreFilled++;
                }
                else if (dN <= half * 0.9f)
                {
                    // ═══ NOW UNREACHABLE BY CONSTRUCTION — s_dryCore IS THE PROOF (2026-08-13) ════════════
                    // dN ≤ 0.9·half ⇒ dN ≤ 0.95·half ⇒ bedDemand ≤ wl−0.35 < wl−0.05, so the branch above
                    // always takes this cell. A dry core cell is no longer a thing that CAN happen rather
                    // than a thing we hope does not: if this counter is ever non-zero the invariant has been
                    // broken somewhere and the log says so immediately. Kept as a live assertion, not dead code.
                    System.Threading.Interlocked.Increment(ref s_dryCore);   // (TRACE) core channel cell left DRY — the visible river break
                    // narrow core stays channel even where the carve left it shallow — but it gets NO water
                    // claim: (v91) drop any fill-table level still parked here (`w` IS _fillTable, aliased at
                    // the call), else the final pass preserves it (mask 1) and the cell stands wet at the OLD
                    // terrain height — the uphill half of every contour reach rode up the bank exactly so
                    // (continuity, HYDROLOGY.md CONTRACT #6: no water without a source). With the carve based
                    // at wl this branch only fires where the local bound held the bed ABOVE the level —
                    // genuinely dry (obstructed) reaches.
                    mask[i2] = 1;
                    if (w[i2] > h[i2]) { w[i2] = h[i2]; if (lvlSrc != null) lvlSrc[i2] = 2; }
                }
                else if (dN <= half * 1.6f && mask[i2] == 0) mask[i2] = 2;        // just-above-water bank margin (feather)

                // WATER-FIRST BANK FLOOR (docs/RIVER_WATERFIRST.md P1) — re-instates the v24
                // containment crest deleted above on the false "banks are always above water"
                // assumption (the user hit overflow). The immediate bank margin must stand ABOVE the
                // level ribbon, so the ribbon can NEVER overflow — a construction guarantee, not the
                // reactive containment clamp. Raise-only + margin-only ⇒ no-op in gorges / valley
                // walls already above, and only berms the genuine low banks the water would spill.
                // The berm rings the WATER: the fill now stops at 1.05·half, so a 2.0 ring is a genuine bank
                // outside the sheet again (at 1.8 fill there was nothing left between water and berm edge).
                // ⚠ THE SEA OWNS ITS OWN COLUMNS — NEVER BERM ONE (2026-08-15, user screenshots: a river
                // running out into open water as a long raised causeway, and the map showing it as a stripe
                // reaching far offshore). This is the ONLY raise in InciseRivers, and with `wl` still above
                // SEA+0.3 along a mouth reach it was lifting SEABED to wl+0.6 — measured max raise 98.7 m
                // and 134,301 ocean cells turned into land by this pass alone. It was always in the grid;
                // it only became visible when the mesh stopped clamping raises away, so this is gen data
                // being corrected, not a rendering fix.
                // The test is the same one GEN_VERSION 126 established for the sheet: the ocean owns any
                // column whose NATURAL bed lies at or below the highest tide (TideHighOffsetM = 0, the sea
                // only recedes), so hOrig — pre-carve ground — decides it. A river reaching the sea now
                // simply stops being bermed and blends into it, which is what a mouth does.
                if (mask[i2] != 1 && mask[i2] != 3 && dN <= half * 2.0f && wl > SEA + 0.3f
                    && hOrig[i2] > SEA
                    && (seeded == null || seeded[i2] <= hOrig[i2] + 0.05f))   // no levee where a seeded lake/swamp already covers this cell — vs PRE-CARVE ground (v104: the carved floor made every shaved cell read as "covered")
                {
                    // ⚠ RAISE TO THE FLOOR, THEN PUT THE GROUND'S TEXTURE BACK ON TOP — and only ever UPWARD.
                    // `h = bankFloor` writes an absolute height across everything within 2x the channel
                    // half-width, so wherever `wl` is steady the berm becomes a flat band; at first-order
                    // stream density those bands merge into visible plateaus (largest measured 1.07 km² at
                    // 273 m, and the straight edges are this dN footprint). Carrying the cell's own bump over
                    // keeps the grain. The detail is clamped to be NON-NEGATIVE because this berm is a
                    // containment GUARANTEE, not a cosmetic ramp: letting it subtract would punch the same
                    // holes the feather did when I forgot that (STRICT float 0 → 118, mesh lips 69 → 9,125).
                    float bankFloor = wl + 0.6f;
                    if (h[i2] < bankFloor)
                    {
                        float avgB = 0.25f * (h[i2 + 1] + h[i2 - 1] + h[i2 + n] + h[i2 - n]);
                        float detB = Mathf.Clamp(h[i2] - avgB, 0f, 0.35f);   // upward texture only
                        h[i2] = bankFloor + detB; if (wsrc != null) wsrc[i2] = 3;
                    }
                }
            }

            // Apply the min-paint: the trench takes the lowest claiming
            // stem's level (raise-only vs the cell's own ground).
            for (int i2 = 0; i2 < paintLvl.Length; i2++)
                if (paintLvl[i2] != float.MaxValue && w[i2] != paintLvl[i2]) { w[i2] = paintLvl[i2]; if (lvlSrc != null) lvlSrc[i2] = 3; }


            // ═══ THE RIVER'S SHORE IS FOUND BY FLOODING, NOT BY MEASURING OUT FROM THE SPINE ══════════════
            // (2026-08-19. The user asked the question that names the defect — "ponds and lakes reach and
            // intersect their shores perfectly every time, why can you not accomplish this with rivers?" — and
            // the answer is one line of each footprint rule.)
            //   • A BODY: FiresWaterPlanner.ExpandFootprint, a 4-connected flood over `g[j] < level`. Its
            //     shoreline is wherever the GROUND rises through the level, so it is exact by construction,
            //     and because the flood is CLOSED it can never leave a below-level cell unclaimed inside
            //     itself. That is the whole reason lakes have always worked.
            //   • A RIVER, until now: `dN ≤ 1.05·half` — a stamped tube around a polyline — plus a per-cell
            //     depth test out to 2·half. Neither is connected, and the second is the one whose own header
            //     already confessed the problem ("HONEST LIMIT: this is a per-cell depth test inside the ring,
            //     not a connected flood like ExpandFootprint … If those show up, the upgrade is a real BFS
            //     from the wet cells rather than a wider or narrower radius"). They did show up. This is that
            //     BFS.
            // MEASURED, why it matters more than it looks: with the tube, 62.1 % of all 177,745 dry cells
            // touching river water stood at EXACTLY sheet+1.0 m — i.e. for most of its length a river's "bank"
            // was a curb the burial pass BUILT at the tube's edge, not ground the water had reached. That is
            // the mounded-puddle look, and no render-side change can touch it: the field's exposed-river-edge
            // count was already 0 of 146,920, because the curb was hiding every edge.
            // THE FLOOD, and what bounds it:
            //   • it grows only from cells that are ALREADY WET (connectivity — it cannot jump a divide, which
            //     is what the per-cell test could and what its note warned about);
            //   • it may only enter ground BELOW the level it carries (the pond rule, verbatim);
            //   • it stays inside the reach's own solved VALLEY REGION (`seedIdx ≥ 0`) and within REACH·half
            //     of the spine, so a high reach cannot carpet a mountainside just because the land keeps
            //     falling away from it — that is the v104 failure and this is the guard against it;
            //   • it never enters a marsh, a seeded body, or a column the sea owns.
            // Double-buffered ring by ring, so the result does not depend on scan order.
            // ⚠⚠ OFF BY DEFAULT AND THAT IS DELIBERATE — the A/B measured it WORSE. See s_riverShoreFlood's
            // declaration for the numbers and for what has to land first (the level solve). `FIRES_RIVER_SHOREFLOOD=1`.
            if (s_riverShoreFlood)
            {
                const float REACH_HALVES = 3.0f;   // how far from the spine a shore may be chased, in channel half-widths (the old per-cell test stopped at 2.0)
                const float REACH_MIN_M = 12f;     // …but a brook's half is 1.5 m, and its shore is not 4.5 m away
                int floodRings = Math.Max(1, (int)(40f / cellM));
                long floodAdded = 0;
                // A FRONTIER, not a rescan. Rings × n² is 2.2 billion cell visits at n=10501 for work that only
                // ever touches the few cells beside a channel; BFS from the wet set costs what it actually does.
                var frontier = new List<int>(1 << 16);
                var next = new List<int>(1 << 16);
                var add = new List<(int idx, float lvl)>(1 << 16);
                for (int i2 = 0; i2 < n * n; i2++)
                    if ((mask[i2] == 1 || mask[i2] == 3) && w[i2] > h[i2] + 0.01f && w[i2] > SEA + 0.3f) frontier.Add(i2);
                for (int ring = 0; ring < floodRings && frontier.Count > 0; ring++)
                {
                    add.Clear(); next.Clear();
                    foreach (int src in frontier)
                    {
                        float lvl = w[src];
                        int zs = src / n, xs = src - zs * n;
                        if (xs < 1 || zs < 1 || xs >= n - 1 || zs >= n - 1) continue;
                        for (int e = 0; e < 8; e++)
                        {
                            int i2 = src + RIV_DZ[e] * n + RIV_DX[e];
                            if (mask[i2] == 1 || mask[i2] == 3 || mask[i2] == MASK_STILL) continue;
                            int sd = seedIdx[i2];
                            if (sd < 0) continue;                                           // outside every reach's solved valley region
                            if (underMarsh != null && underMarsh[i2]) continue;
                            if (seeded != null && seeded[i2] > hOrig[i2] + 0.01f) continue;  // a body owns this cell
                            if (hOrig[i2] <= SEA) continue;                                 // the sea owns its columns (same test the berm uses)
                            float reach = Math.Max(REACH_MIN_M, REACH_HALVES * Math.Max(1.5f, widthOf[sd] * 0.5f));
                            if (rDist[i2] > reach) continue;
                            if (h[i2] >= lvl - 0.05f) continue;                             // ground rises through the level — THIS IS THE SHORE
                            add.Add((i2, lvl));
                        }
                    }
                    if (add.Count == 0) break;
                    // Apply by MAX: two reaches at different levels can reach the same cell in one ring, and the
                    // higher one is the one that spills into it. Taking the max makes the result independent of
                    // the order the frontier happens to be in.
                    foreach (var (i2, lvl) in add)
                    {
                        if (mask[i2] == 1) { if (w[i2] < lvl) { w[i2] = lvl; if (lvlSrc != null) lvlSrc[i2] = 5; } continue; }       // claimed earlier this ring
                        mask[i2] = 1; w[i2] = lvl; if (lvlSrc != null) lvlSrc[i2] = 5; floodAdded++; next.Add(i2);
                    }
                    var swapF = frontier; frontier = next; next = swapF;
                }
                FiresLogger.LogInfo($"[FiresWaterDiag] RIVER SHORE FLOOD: {floodAdded:N0} cell(s) added by the connected `ground below the level` flood — the pond rule, applied to rivers. These are the cells the 1.05·half tube left dry and the burial pass would otherwise have raised into a curb at the waterline.");
            }

            // v26 — mark the SPINE (extraction cells) distinctly: mask 3 =
            // spine (wet by construction, dN=0). Spring spots emit ONLY spine
            // cells so every pour pins dead-center of the channel instead of
            // at a bank edge (off-center pours left the far bank dry and
            // pinned tilted levels).
            for (int k2 = 0; k2 < riverCells.Count; k2++) mask[riverCells[k2]] = 3;

            // v27 — HEADWATER POOLS: every river ends upstream in a rounded
            // irregular pool instead of a dead stop. Sources are EXACT (the
            // first rasterized cell of each polyline) — the old no-higher-
            // neighbour heuristic pooled every monotone-clamped flat cell.
            int poolsKept = 0, poolsSkipped = 0;
            foreach (int i in polySources)
            {
                int cz2 = i / n, cx2 = i - cz2 * n;
                if (cx2 < 2 || cz2 < 2 || cx2 >= n - 2 || cz2 >= n - 2) continue;
                float pr = Math.Max(9f, widthOf[i] * 1.4f);
                // (2026-08-09, user: "the ponds and lakes get the proper surface level — make the springs into
                // mini ponds") SPRING LEVEL FROM THE LOCAL GROUND, not the river field. The old `wl2 = w[i]`
                // took the inherited water-field value at the source, which can sit tens of metres above the
                // head's actual ground (a high fill-table/junction level) → the pool rendered as a floating
                // disc/column above the terrain (user screenshots). A spring is a MINI POND: derive its surface
                // from the CONTAINING RIM exactly like FiresWaterPlanner does for ponds/lakes (level = spill −
                // relief), so it beds in its own basin and its surface meets the river that descends from it.
                float rimMin = float.MaxValue, rimMax = float.MinValue;
                {
                    float rimR = pr / cellM;
                    for (int rs = 0; rs < 8; rs++)
                    {
                        float ang = rs * 0.7853982f;
                        float rg = SampleHOrig(hOrig, n, cx2 + Mathf.Cos(ang) * rimR, cz2 + Mathf.Sin(ang) * rimR);
                        if (rg < rimMin) rimMin = rg; if (rg > rimMax) rimMax = rg;
                    }
                }
                // A pond needs a bowl that HOLDS it: a steep head (rim relief > 2.2m) can't — it becomes a bare
                // channel head, no pool (same rule the plan cull uses). Gentle heads keep their mini-pond.
                if (rimMax - rimMin > 2.2f) { poolsSkipped++; continue; }
                // (user) THE POND'S FLAT LEVEL = THE RIVER'S OWN MESH LEVEL AT THE SOURCE it connects to. The
                // river already runs correctly (bank-pinned), so matching its level here makes the pond meet the
                // river flush and it CANNOT float. Not the rim, not a derived offset — the river's level, period.
                float wl2 = w[i];
                if (wl2 <= h[i] + 0.01f || wl2 <= SEA + 0.3f) { poolsSkipped++; continue; }   // no river water at the source / sea-owned
                poolsKept++;
                // Find the source, carve a hole, lay the flat mesh at that level.
                int span = (int)(pr / cellM) + 2;
                for (int dz2 = -span; dz2 <= span; dz2++)
                {
                    for (int dx2 = -span; dx2 <= span; dx2++)
                    {
                        int nx3 = cx2 + dx2, nz3 = cz2 + dz2;
                        if (nx3 < 1 || nx3 >= n - 1 || nz3 < 1 || nz3 >= n - 1) continue;
                        int j = nz3 * n + nx3;
                        if (underMarsh != null && underMarsh[j]) continue;   // pools never dig into a marsh
                        float rr = Mathf.Sqrt(dx2 * dx2 + dz2 * dz2) * cellM;
                        if (rr > pr) continue;
                        float t = rr / pr;
                        float bed = wl2 - (0.5f + 2.5f * (1f - t * t));      // carve the hole below the level (deep centre, shallow rim)
                        if (bed < h[j]) { h[j] = SmoothMinCubic(h[j], bed, 3f); if (wsrc != null) wsrc[j] = 5; }
                        if (h[j] < wl2)                                       // lay the flat mesh over the carved hole
                        {
                            // ⚠ A SPRING POND IS STILL WATER, SO IT GETS THE STILL MASK — NOT THE CHANNEL ONE.
                            // (2026-08-16, user, looking at a render: "what exactly is happening in spots like
                            // here where we have a stillwater surrounded by rivers? that's not correct at all".)
                            // This wrote `mask[j] = 1` — the CHANNEL mask — across the whole pool disc, so
                            // every one of these (4,163 on seed 530442569) was a flat circular pond that the
                            // class grid then read as WC_RIVER and drew in river colour, with the channel head
                            // threading through it and bank noise fraying its rim. That is the tangle in the
                            // screenshots. It was not only a colour error: mask 1 also opts a cell into RIVER
                            // treatment downstream — the bank/berm rules and the Part VII rapids disturbance
                            // bake — so a still spring pool was getting flow shading applied to it.
                            // MASK_STILL keeps the water (the final dry pass below honours it exactly as it
                            // honours a channel) while every `mk == 1 || mk == 3` test in the codebase — the
                            // channel tests — correctly says NO.
                            if (mask[j] == 0 || mask[j] == 2) mask[j] = MASK_STILL;
                            w[j] = wl2;   // FLAT — ONE level across the whole source pool, raise AND LOWER (a pond/lake is laid flat; the old raise-only left higher inherited river/fill levels inside the pool, so the source sat too high and sloped instead of sitting flat at the river's level)
                        }
                    }
                }
            }
            if (poolsKept + poolsSkipped > 0)
                FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: headwater pools — {poolsKept} justified by a containing basin, {poolsSkipped} SKIPPED (rim can't hold water → channel head, no pool).");

            // v18 — thermal relaxation: two talus passes shed over-steep
            // droplet spikes and incision walls the way real slopes shed
            // scree (~41° limit at these cells). Carved channel walls sit
            // under the limit and survive.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int z2 = 1; z2 < n - 1; z2++)
                {
                    for (int x2 = 1; x2 < n - 1; x2++)
                    {
                        int i2 = z2 * n + x2;
                        float hc = h[i2];
                        int lo = i2;
                        float hl = hc;
                        if (h[i2 - 1] < hl) { hl = h[i2 - 1]; lo = i2 - 1; }
                        if (h[i2 + 1] < hl) { hl = h[i2 + 1]; lo = i2 + 1; }
                        if (h[i2 - n] < hl) { hl = h[i2 - n]; lo = i2 - n; }
                        if (h[i2 + n] < hl) { hl = h[i2 + n]; lo = i2 + n; }
                        float d2 = hc - hl;
                        const float TALUS = 9f;
                        if (d2 > TALUS)
                        {
                            // (2026-08-11) NEVER BACKFILL A CHANNEL. Scree sheds onto natural ground, but a
                            // river bed is kept open by its own flow — and dumping the slope's excess INTO the
                            // channel raised the bed back above the ribbon, re-drying reaches the carve had
                            // just cut (the river that "stops and starts again 40m later", especially where a
                            // slot cuts through high ground and the walls exceed the 9m talus limit).
                            if (mask[lo] == 1 || mask[lo] == 3) continue;
                            float move = (d2 - TALUS) * 0.5f;
                            h[i2] -= move;
                            h[lo] += move;
                        }
                    }
                }
            }

            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: channels carved ({rsw.Elapsed.TotalSeconds:0}s); finalizing water...");
            // RIVER REWORK — WATER IS THE CARVED RIVER NETWORK ONLY.
            // Sea snap first (the fill's +0.0002/cell tie-breaker accumulates to
            // +0.2..0.4m across the ocean flat). River cells (mask 1 channel,
            // mask 3 spine) KEEP their monotone level (bed = level − depth by the
            // cross-section, so depth is correct by construction). EVERY other
            // land cell is dried to its bed: the global fill otherwise ponds
            // every shallow basin AND every valley shoulder the Dijkstra carve
            // lowered below the pre-carve ground — a map-wide flood. Deliberate
            // lakes/ponds are a separate placement pass, not this fill.
            for (int i2 = 0; i2 < n * n; i2++)
            {
                // In-marsh cells carry NO channel mask: the guards kept every real carve
                // out, so whatever mask is here is the thin drainage stand-in handed in
                // via existingMask — left set, ContainRiversBelowBanks berms its dry
                // neighbours into raised lines across the sheet. The marsh owns it all.
                if (underMarsh != null && underMarsh[i2] && mask[i2] != 0) mask[i2] = 0;
                // REORDER — ANY cell under a seeded body adopts the body's surface,
                // INCLUDING a river channel crossing it (mask 1/3). Without this the
                // incised river keeps its own (higher) level and renders as a SEPARATE
                // mesh FLOATING above the swamp/lake sheet (user's in-game report); here
                // the river blends into the body as ONE surface and resumes past the
                // far edge. Runs before sea-snap/dry so highland bodies survive.
                // ═══ (v104) COMPARE AGAINST THE PRE-CARVE GROUND, NOT THE CARVED FLOOR. `seeded` is
                // BuildGrid's DryWaterField = a COPY OF THE PRE-INCISION h ("water = ground everywhere,
                // dry"), with real bodies raised above it by Conform/tarns. Testing `seeded > h` against
                // the CARVED h made EVERY cell the carve or valley-shave lowered read as "under a body"
                // (old ground > new floor) — the final pass then painted the ENTIRE carved territory with
                // water at pre-carve terrain height: the terrain-conforming water CARPET over the massifs
                // (seed 49185460 audit: 58k high wet cells whose depth exactly matched the carve depth
                // histograms). A real body sheet stands above the PRE-CARVE ground; a carved-only cell
                // does not — hOrig is the discriminator (the marsh guard above already uses it).
                if (seeded != null && seeded[i2] > hOrig[i2] + 0.01f)
                {
                    w[i2] = seeded[i2]; if (lvlSrc != null) lvlSrc[i2] = 6;
                    continue;
                }
                if (w[i2] < SEA + 0.5f)
                {
                    // ESTUARY — a wet river-mask cell keeps its DESCENDING mouth ribbon
                    // (floored at SEA−1.8 by the rasterizer) instead of snapping flat to
                    // SEA: the river runs down through the tidal ocean plane and the two
                    // sheets clip (user: no river edge may float above the tide — swamps
                    // at y30 AND open-ocean mouths alike). Everything else snaps as before.
                    bool riverRibbon = (mask[i2] == 1 || mask[i2] == 3) && w[i2] > h[i2] + 0.01f && w[i2] > ESTUARY_DIVE_Y - 0.1f;
                    if (!riverRibbon) { w[i2] = SEA; if (lvlSrc != null) lvlSrc[i2] = 7; }
                }
                else if (mask[i2] != 1 && mask[i2] != 3 && mask[i2] != MASK_STILL)
                {
                    if (w[i2] > h[i2]) { w[i2] = h[i2]; if (lvlSrc != null) lvlSrc[i2] = 8; }
                }
                // ═══ THE BED IS ALWAYS BELOW THE MESH (user 2026-08-11: "the channel needs to fucking carve
                // to be deeper than the mesh wherever we put the mesh, period"). The carve's own wetted-core
                // rule only guaranteed this within 0.95 half-widths; everything out to the fill edge merely
                // needed `h < wl − 0.05`, so the sheet's outer band sat on a bed a few centimetres under it —
                // a paper-thin lip that reads as a visible mesh EDGE and floats the moment a neighbouring stem
                // carries a different level. Applied HERE because the level is final at this point (the
                // paint min-write is resolved), so it uses the surface we actually render: every wet river
                // cell gets at least CHANNEL_MIN_DEPTH of bed under it. Wet cells only — a deliberately dry
                // channel cell stays dry; the sea/estuary keeps its own descending floor.
                if ((mask[i2] == 1 || mask[i2] == 3) && w[i2] > h[i2] + 0.01f && w[i2] > ESTUARY_DIVE_Y - 0.1f)
                {
                    const float CHANNEL_MIN_DEPTH = 0.5f;
                    if (h[i2] > w[i2] - CHANNEL_MIN_DEPTH) { h[i2] = w[i2] - CHANNEL_MIN_DEPTH; if (wsrc != null) wsrc[i2] = 4; }
                }
            }
            // A top-down render cannot show a 2 m shelf (the class overlay covers the ground and the
            // hillshade will not resolve it), and "15,991 cells cut" says the code RAN, not that the shape
            // is right. So walk a row of ground straight through the deepest slot this world produced and
            // print it: the bowl, the flat, and the wall should be readable in the numbers.
            if (s_canyonShelf && s_shelfDeepCell >= 0)
            {
                int tz = s_shelfDeepCell / n, tx = s_shelfDeepCell - tz * n;
                var sbT = new System.Text.StringBuilder();
                sbT.Append($"[FiresWaterDiag] CANYON SECTION at ({tx * cellM - _half:0},{tz * cellM - _half:0}) — ground every {cellM:0} m across the channel, water at {s_shelfDeepWl:0.0}. Expect: bowl, then ~{CANYON_SHELF_M:0} m flat near {s_shelfDeepWl + CANYON_FLOOR_UP:0.0}, then a wall.  ");
                for (int q = -10; q <= 10; q++)
                {
                    int qx = tx + q;
                    if (qx < 0 || qx >= n) continue;
                    float gy = h[tz * n + qx];
                    sbT.Append(q == 0 ? $"[{gy:0.0}] " : $"{gy:0.0} ");
                }
                FiresLogger.LogInfo(sbT.ToString());
            }
            // ═══ RIVER BANK BURIAL — NO FLOATING WATER, NO VISIBLE MESH EDGE, PERIOD (user 2026-08-11:
            // "i don't accept a tradeoff — we either carve the rivers deeper in these spots to conceal the mesh
            // edges or we raise the banks and smooth them into the surrounding terrain"; "there is no floating
            // water that is acceptable period"; "i don't ever want to see the edge of a river's mesh").
            // This is the project's own contract — TERRAIN CONFORMS TO WATER — and it is exactly the
            // construction FiresWaterConform.BuryMeshEdges already performs for still bodies (shore shelf +
            // burial rim + feather). It was simply never applied to the river ribbon, which is why a reach on
            // any real slope could stand above its banks: the ribbon is a smooth monotone surface, the ground
            // under it is not, so on a descent the sheet outruns its own banks. Deleting those reaches (the old
            // 25%-grade cascade rule) traded a float for a dry trench — the user rejects both. Instead the BANK
            // is made to hold the water, everywhere: every DRY cell touching river water rises above that
            // water, then feathers back to natural ground over a few rings so it reads as a bank, not a wall.
            // Containment becomes a GUARANTEE by construction, so steep reaches keep continuous water AND the
            // mesh edge is always buried. Raise-only; never touches wet cells, marsh, seeded bodies, or the
            // sea (below SEA the ocean plane owns the surface and the estuary must stay open to it).
            {
                const float RIVER_RIM = 0.65f;    // dry bank stands this far above the ribbon it touches
                const float MARSH_BANK_RIM = 0.28f; // ...but a marsh shore is a beach, not a levee (see below)
                // ⚠ THE RAMP IS A GRADE AND A DISTANCE, NOT "0.7 m PER RING FOR 4 RINGS" (v135). A ring is one
                // CELL, so the old pair described a completely different bank at each resolution the player can
                // choose: 4 rings of 0.7 m is an 8 m ramp falling at 35% on the game's 2 m grid, and a 41 m ramp
                // falling at 6.8% at n=2049. Same seed, same river, different bank. (The v134 leapfrog was
                // hiding this — while the ramp swept the map in scan order, its reach was not 4 cells anyway.)
                // Anchored to what n=10501 builds: 4 rings x 2 m = 8 m of reach, 0.7 m per 2 m = 0.35 grade.
                // Full res is bit-unchanged; every other resolution now feathers the same bank in METRES.
                // Precedent: v125 did exactly this to FindSeededNear (3 cells → a metre-based 24 m).
                const float FEATHER_REACH_M = 8f;     // how far the bank eases back into natural ground
                const float FEATHER_GRADE = 0.35f;    // ...falling this steeply while it does
                int featherRings = Math.Max(1, (int)(FEATHER_REACH_M / cellM + 0.5f));
                float FEATHER = FEATHER_GRADE * cellM;   // fall per ring, so the GRADE is what stays constant
                var bankStamp = new byte[n * n];
                long rimRaised = 0, feathered = 0, holesFilled = 0;
                var mergeFills = new List<(int idx, float lvl)>(); object mergeFillLock = new object();   // seam cells between two same-level waters, FLOODED after the loop (buffered so the parallel loop stays deterministic)
                // Use the SAME wet test the float audit uses. A cell holding <0.4m over its bed is a wet FRINGE
                // to the field but reads as DRY GROUND to the audit (and to the eye), so treating it as water
                // here left exactly those cells unbermed — which is where the residual float lived.
                bool WetLikeAudit(int i)
                {
                    byte mk = mask[i];
                    if (mk == 1 || mk == 3) return w[i] - h[i] >= 0.4f;
                    // ═══ A MARSH IS SHALLOW BY DESIGN — 1.2m MADE IT INVISIBLE HERE (2026-08-13) ═════════
                    // The 1.2m depth test is a sensible "is this really a lake" bar, but a marsh is
                    // specified as a LARGE SHALLOW lake, and its bed additionally lerps up toward natural
                    // ground through the shore band — so most of a marsh, and essentially all of its rim,
                    // holds less than 1.2m and was classified as DRY GROUND by this pass. Dry ground
                    // contributes no `demand`, so NO BANK WAS EVER BUILT against a marsh edge: marshes were
                    // the only inland water in the world with no final containment. Measured: 0 marshes →
                    // 8 float cells world-wide; 3 marshes → 1,435 (swamp 552 + lowland 864), i.e. ~475 float
                    // cells per marsh placed. Marsh water now uses a shallow-water threshold so the burial
                    // pass can see it and berm its rim like every other body's.
                    if (marshBody != null && marshBody[i]) return w[i] - h[i] >= 0.3f;
                    return w[i] - h[i] >= 1.2f;
                }
                // ═══ AN ENCLOSED DRY CELL IS A HOLE IN THE SHEET — FILL IT, DO NOT BUILD A CURB IN IT ════════
                // (2026-08-19, measured by the new `riverfield` probe on seed 126369233 at n=10501: 7,098 dry
                // cells sat with ≥6 of their 8 neighbours WET, in 4,646 islands — and 6,422 of them, 90.5%,
                // stood at EXACTLY water+1.00 ±0.15 m, which is the RIM constant of the burial passes. They are
                // not terrain. They are cells the RIVER FOOTPRINT MISSED, which the burial pass then met as "a
                // dry cell touching water", did exactly what it is told, and RAISED A METRE ABOVE THE SHEET —
                // turning a gap into an island. That is the user's "mysterious dry cells in the middle of pooled
                // up rivers", and it is why rivers carry 18.2 such holes per 1,000 wet cells against a body's
                // 0.15: a body's footprint is a FLOOD on `g < level` (FiresWaterPlanner.ExpandFootprint) and
                // cannot leave an interior cell out; a river's was a stamped tube (`dN ≤ 1.05·half`) and does.
                // A cell surrounded by water on 6 of 8 sides has no shore to be — it is interior — and if its
                // ground also lies below the water pressing in on it, the water belongs there. THIS CANNOT RUN
                // AWAY: it requires enclosure, so it can only close a hole, never open a shore, and the
                // `h < demand` test still leaves a genuine rock outcrop standing proud of the surface.
                // Iterated + double-buffered so it is order-independent (the raster scan below is not, and the
                // v134 leapfrog is what that costs): each round closes one ring, so a compact island up to
                // ~2·ROUNDS cells across closes. Measured island sizes are p90 2 / p99 8 / max 45 cells.
                // ⚠ THE SAME GUARD EXISTS IN FiresWaterConform.BuryMeshEdges, which is the other pass that
                // raises dry cells touching water (and the one whose RIM = 1.0 the measurement fingerprinted).
                // Both need it and the two must agree; change one, change the other.
                if (s_riverFootprintFlood)
                {
                    const int FILL_ROUNDS = 5;
                    var fills = new List<(int idx, float lvl)>();
                    for (int it = 0; it < FILL_ROUNDS; it++)
                    {
                        fills.Clear();
                        for (int z2 = 1; z2 < n - 1; z2++)
                            for (int x2 = 1; x2 < n - 1; x2++)
                            {
                                int i2 = z2 * n + x2;
                                if (w[i2] > h[i2] + 0.01f) continue;                          // already water (any depth — see the note below)
                                if (underMarsh != null && underMarsh[i2]) continue;           // the marsh owns its own islands
                                if (hOrig[i2] <= SEA) continue;                               // the sea owns its columns
                                // ⚠ ANY water counts as a wet neighbour here, NOT WetLikeAudit's 0.4/1.2 m depth
                                // bars. Those are consumer gates ("is this deep enough to be a lake"); the
                                // question this pass asks is "am I walled in by water", and a 0.2 m shallow is a
                                // wall for that purpose. Using the depth bars also stalled the ring march: a cell
                                // filled shallow in round N did not read as wet in round N+1, so the island's next
                                // ring never closed. Same test BuryMeshEdges uses and the same one the
                                // measurement counted with.
                                int wetN = 0; float demand = 0f, loLvl = float.MaxValue;
                                for (int e = 0; e < 8; e++)
                                {
                                    int j = (z2 + RIV_DZ[e]) * n + (x2 + RIV_DX[e]);
                                    if (w[j] <= h[j] + 0.01f) continue;
                                    if (w[j] <= SEA + 0.5f) continue;                         // estuary/ocean edge — not an inland hole
                                    wetN++;
                                    if (w[j] > demand) demand = w[j];
                                    if (w[j] < loLvl) loLvl = w[j];
                                }
                                if (wetN < 6 || demand <= 0f) continue;
                                // ⚠ ONE LEVEL, OR IT IS NOT A HOLE. A cell walled in by water at TWO different
                                // elevations is the DIVIDE between two reaches — a lip — and filling it makes the
                                // sheet step. Measured on the first cut of this pass: 1,130 holes closed but
                                // >3 m adjacent-cell steps went 730 → 862, and those extra 132 are exactly this
                                // case. A genuine footprint miss is surrounded by ONE surface.
                                if (demand - loLvl > 4.0f) continue;
                                // …and fill at the LOWER of them, never the higher: a cell filled to the max can
                                // stand above its lower neighbour, which is the free edge this whole file exists
                                // to prevent. The low level cannot stand above anything touching it.
                                if (h[i2] >= demand - 0.05f) continue;                         // genuinely stands proud — a real islet, leave it
                                fills.Add((i2, demand));
                            }
                        if (fills.Count == 0) break;
                        foreach (var (i2, lvl) in fills)                                       // serial apply — order cannot matter, each writes its own index
                        {
                            w[i2] = lvl; if (lvlSrc != null) lvlSrc[i2] = 9;
                            if (mask[i2] == 0 || mask[i2] == 2) mask[i2] = 1;
                            holesFilled++;
                        }
                    }
                }
                for (int z2 = 1; z2 < n - 1; z2++)
                    for (int x2 = 1; x2 < n - 1; x2++)
                    {
                        int i2 = z2 * n + x2;
                        if (WetLikeAudit(i2)) continue;                                      // real water — not a bank
                        // Guard on WATER, not on the channel mask. Skipping every mask-1/3 cell also spared
                        // DRY channel cells (a carved-but-waterless bed) that sit right beside live water —
                        // they are ground below the surface, i.e. exactly where the sheet leaks out. Guarding
                        // on "carries any water at all" protects every drop from being raised (raising water
                        // is what put 306 gaps back) while letting a dry bed be bermed like any other bank.
                        if (w[i2] > h[i2] + 0.01f) continue;
                        // ═══ MARSH GROUND IS NO LONGER EXEMPT FROM CONTAINMENT (2026-08-13) ══════════════
                        // This blanket skip is why marshes floated and why two earlier attempts at the wetness
                        // test changed the float by EXACTLY ZERO three runs running: the cells that needed a
                        // bank exited here, before `demand` was ever computed. A dry island shore inside a
                        // marsh was skipped and never raised, so the sheet stood above it — float around the
                        // perimeter of every island, which is most of a marsh's shoreline.
                        // The skip exists for a real reason (see the mask note below: a river's drainage
                        // stand-in mask inside a marsh must not berm raised lines across the sheet), but that
                        // concern is about CHANNEL cells, so it is now scoped to exactly those. Marsh ground
                        // otherwise gets contained like every other body's.
                        if (underMarsh != null && underMarsh[i2] && (mask[i2] == 1 || mask[i2] == 3)) continue;
                        // NOTE: no "inside a body footprint" exemption. A footprint cell that ended up DRY is
                        // still ground below the neighbouring surface — i.e. exactly where a lake leaks out —
                        // and skipping it left the body (lowland) float untouched. Water itself is already
                        // protected by the guard above, so this is safe.
                        // Demand from ANY inland water, not just the river. Still bodies were buried back in
                        // FiresWaterConform.BuryMeshEdges — but that ran BEFORE InciseRivers, and the river
                        // carve + valley shave then lower ground that was a lake's containing bank, so the
                        // body's guarantee is stale by the time the world is final. Lowland (body) float was
                        // the single biggest bucket in the audit for exactly this reason. Burying against the
                        // FINAL field covers rivers and bodies in one construction.
                        float demand = 0f;
                        bool marshDemand = false;   // the water asking for this bank is a marsh sheet
                        bool riverDemand = false;   // …or a CHANNEL, which is the family whose banks were being terraced
                        bool stillDemand = false;   // …and does ANY still water also want this bank? (then it is a body shore)
                        float stillLvl = 0f, riverLvl = 0f;   // the two families' own levels, so a MERGE POINT can be told from a bank
                        const float MERGE_TOL = 0.5f;         // levels this close are one surface, not two
                        for (int e = 0; e < 8; e++)
                        {
                            int j = (z2 + RIV_DZ[e]) * n + (x2 + RIV_DX[e]);
                            if (!WetLikeAudit(j)) continue;                                  // dry / fringe cell
                            if (w[j] <= SEA + 0.5f) continue;                                // estuary/ocean — the sea owns that edge, never wall it off
                            if (w[j] > demand) { demand = w[j]; marshDemand = marshBody != null && marshBody[j]; riverDemand = mask[j] == 1 || mask[j] == 3; }
                            // ⚠ A CELL THAT ALSO TOUCHES STILL WATER IS A BODY SHORE AND KEEPS THE STRICT RULE.
                            // The loose trigger below is scoped to CHANNEL banks; taking it from `riverDemand`
                            // alone gave it to every river↔body junction too, because at a junction the river is
                            // usually the higher of the two and therefore the one that set `demand`. Measured:
                            // still-family visible lips 161 → 211 and worst lip 3.07 → 6.15 m — a regression on
                            // the exact family that has always worked. If any still water is asking for this
                            // bank, it gets the body rule.
                            if (mask[j] != 1 && mask[j] != 3) { stillDemand = true; if (w[j] > stillLvl) stillLvl = w[j]; }
                            else if (w[j] > riverLvl) riverLvl = w[j];
                        }
                        if (demand <= 0f) continue;
                        // ═══ WHERE TWO WATERS MEET, THEY MERGE. A BANK BETWEEN THEM IS A DEFECT. ═════════
                        // (user, 2026-08-31, standing on a river mouth at X4779 Z8310: "seems like every
                        // water connection where a river meets the stillwater has a slight berm containing it
                        // rather than the two water bodies merging cleanly".)
                        // `stillDemand` above changes only the TRIGGER, never the OUTCOME: a junction cell
                        // takes `demand + rim` either way, so the strip between a river and the lake it runs
                        // into is raised to water+0.65 on both sides and reads as a sand bar holding them
                        // apart. The rim exists to bury a CHANNEL's own mesh edge; at a junction there is no
                        // edge to bury, because the neighbouring sheet continues the surface.
                        // So: if a cell is wanted by a river AND by still water AND the two are asking for
                        // essentially the SAME level, it is a MERGE POINT and gets no bank at all. Left at
                        // natural ground it either drowns (the two sheets join, which is the point) or stands
                        // as a natural spit — both honest, neither a built wall.
                        // ⚠ SCOPED SO THE HALF THAT WORKS CANNOT MOVE. A lake shore with no river touching it
                        // has riverLvl = 0 and is untouched; a channel bank with no still water has stillLvl
                        // = 0 and is untouched. Only the junction itself changes, and only when the two
                        // levels already agree — if they disagree by more than MERGE_TOL there IS a real step
                        // to contain and the bank stays.
                        if (riverLvl > 0f && stillLvl > 0f && Math.Abs(riverLvl - stillLvl) <= MERGE_TOL)
                        { float mlo = Math.Min(riverLvl, stillLvl); if (h[i2] < mlo - 0.05f) lock (mergeFillLock) mergeFills.Add((i2, mlo)); System.Threading.Interlocked.Increment(ref s_bermMergePoint); continue; }
                        // A MARSH BANK IS A SHORE, NOT A LEVEE. A marsh is specified as a large shallow lake
                        // with ROLLING islands, and most of its shoreline is island perimeter, not outer rim —
                        // so stamping the full river rim around every island would turn soft pillows into
                        // plateaus with a lip, which is the one thing the model must not look like. Just enough
                        // to bury the sheet edge and no more; the feather below still eases it outward.
                        float rim = marshDemand ? MARSH_BANK_RIM : RIVER_RIM;
                        // ⚠ ONLY A BANK THAT WAS ACTUALLY BUILT SEEDS THE FEATHER. This used to stamp EVERY
                        // cell touching water, raised or not — so the feather below radiated from the entire
                        // shoreline of every channel rather than from real banks. Harmless while a world had a
                        // few hundred rivers; ruinous once first-order streams exist. Measured at 19,155
                        // rivers: 8,226 cells raised, **1,648,174 feathered** — 13.9% of all land overwritten
                        // with `hi - FEATHER`, a value computed from neighbours, which erases that cell's own
                        // detail. It showed on the map as large featureless patches at the foot of mountains
                        // (largest 3.6 km² at 288 m) and the roughness audit put it at 4.63% → 16.45% smooth
                        // ground across this one pass. The feather exists to ease a RAISED bank back into the
                        // terrain; where nothing was raised there is nothing to ease, and the ground should be
                        // left exactly as the erosion and detail passes made it.
                        // ═══ (TRACE 2026-08-19) IS THIS BERM EARNED, OR IS IT A CURB? ════════════════════
                        // The user's cross-section is three lines: the NATURAL ground (green), the bed we cut
                        // into it (red), and the water filling that cut (blue). The green line is never
                        // raised — the water is contained because it sits BELOW natural ground, not because a
                        // wall was built beside it. This pass raises the green line, on 58.9 % of every river
                        // bank in the world, to EXACTLY water+1.0.
                        // That is only legitimate where the level solve put water ABOVE the natural ground, so
                        // split the raises by that test and let the log say which we are doing. `hOrig` is the
                        // pre-carve ground = the green line. If `hOrig >= demand` dominates, this berm is
                        // almost entirely gratuitous and the fix is to stop building it; if `hOrig < demand`
                        // dominates, the berm is holding up a level solve that is genuinely floating water and
                        // the fix belongs upstream in SetBankDerivedLevels. Measure before deleting.
                        if (hOrig[i2] < demand) System.Threading.Interlocked.Increment(ref s_bermEarned);
                        else System.Threading.Interlocked.Increment(ref s_bermGratuitous);
                        // ═══ THE TRIGGER AND THE ACTION ARE NOT THE SAME NUMBER ══════════════════════════
                        // (2026-08-19, user: "the berm stays as a safety for when you inevitably cannot
                        // contain the mesh in spots".) It stays. What changes is that it stops being the
                        // primary containment and goes back to being the safety it is described as.
                        // `h < demand + rim` then `h = demand + rim` is one threshold used twice, and that is
                        // what makes every riverbank in the world an identical terrace: any ground below
                        // water+rim is dragged UP TO water+rim, including ground that was already above the
                        // waterline and containing the river perfectly well. MEASURED with the counter above:
                        // 3,896 raises were EARNED (natural ground genuinely below the water — real float,
                        // exactly what a safety is for) against 224,145 GRATUITOUS. 98.3 % of this pass was
                        // flattening natural banks onto a constant offset for no containment gain.
                        // Split them: TRIGGER on whether the ground actually buries the sheet (a small margin
                        // above the waterline), ACT with the full rim so that where it does fire you get a
                        // real bank and not a lip. Ground already standing above the water is left exactly as
                        // the terrain made it — the green line of the user's cross-section, untouched.
                        const float BURY_MARGIN = 0.50f;   // "does this ground bury the sheet?" — 0.15 was too tight to survive the dry node's 2x2 bilinear over deep channel neighbours: river lips 20 -> 47. 0.50 keeps natural banks and catches the marginal ground.
                        float trigger = (riverDemand && !stillDemand) ? demand + BURY_MARGIN : demand + rim;
                        if (h[i2] < trigger) { h[i2] = demand + rim; rimRaised++; bankStamp[i2] = 1; if (wsrc != null) wsrc[i2] = 6; }
                    }
                // ═══ THE SEAM BETWEEN TWO SAME-LEVEL WATERS IS WATER ═════════════════════════════════════
                // (user: "merging cleanly"; and, from the screenshot: "none of them even properly connect,
                // they constantly have a fucking gap".) The merge branch above correctly builds NO BANK
                // between a river and the still water it joins — but leaving the seam cell DRY makes a
                // notch: bare ground with water either side, which is exactly the photographed gap, and the
                // sheet edge over it is a visible free face (EXPOSED went 0 → 19 the moment the draw
                // contract lifted approaches to lake level). So the seam is FLOODED at the LOWER of the two
                // levels — the same never-the-higher law the enclosure hole-fill above applies, through the
                // same lvlSrc = 9 family. Buffered and applied serially so the parallel rim loop stays
                // deterministic. Ground standing proud of both waters was never buffered — a natural spit is
                // honest and keeps its bank-free shape.
                foreach (var (mi, mlvl) in mergeFills)
                {
                    w[mi] = mlvl; if (lvlSrc != null) lvlSrc[mi] = 9;
                    if (mask[mi] == 0 || mask[mi] == 2) mask[mi] = 1;
                }
                // feather: walk the raised rim back down to natural ground outward, raise-only, so the bank
                // blends into the surrounding terrain instead of standing as a wall around the channel.
                for (int it = 0; it < featherRings; it++)
                {
                    for (int z2 = 1; z2 < n - 1; z2++)
                        for (int x2 = 1; x2 < n - 1; x2++)
                        {
                            int i2 = z2 * n + x2;
                            if (bankStamp[i2] != 0) continue;
                            if (w[i2] > h[i2] + 0.01f) continue;                             // never touch water, at any depth
                            // ⚠ THE SEA OWNS ITS COLUMNS (2026-08-15, user: a river running out into open
                            // water as a raised causeway, visible as a stripe far offshore on the map).
                            // The RIM pass above already refuses to wall off the sea — "estuary/ocean — the
                            // sea owns that edge, never wall it off" — but this FEATHER had no such guard, so
                            // it propagated the bank outward over SEABED for 4 iterations and lifted it to
                            // hi−FEATHER. The `w[i2] > h[i2]` test above does not catch it because the ocean
                            // is not a seeded body: it is implicit ("water is rivers + seeded bodies + sea"),
                            // so _waterLevel is not set on open-sea cells and they read as dry land to feather.
                            // Measured: this pass built ~134k ocean cells of land, median 16.8 m above sea.
                            // hOrig (pre-carve natural ground) is the same ownership test GEN_VERSION 126 set
                            // for the sheet: at or below the highest tide ⇒ the sea's, never built on.
                            if (hOrig[i2] <= SEA) continue;
                            if (underMarsh != null && underMarsh[i2]) continue;
                            if (seeded != null && seeded[i2] > hOrig[i2] + 0.01f) continue;
                            float hi = 0f;
                            for (int e = 0; e < 8; e++)
                            {
                                int j = (z2 + RIV_DZ[e]) * n + (x2 + RIV_DX[e]);
                                // ⚠⚠ `== 1`, NOT `!= 0` — THE LEAPFROG, AND IT SURVIVED THE FIX THAT NAMED IT.
                                // GEN_VERSION 131 diagnosed this exact expression: "the race let a cell see a
                                // neighbour feathered EARLIER IN THE SAME ITERATION (stamp==2 still passes
                                // `!= 0`), so the ramp leapfrogged outward instead of advancing one ring per
                                // iteration as its own comment specifies." It then fixed the PARALLELISM —
                                // and the leapfrog is not a parallelism bug. This raster scan is serial, so
                                // a cell stamped 2 a moment ago at (x-1,z) is read as an established bank by
                                // (x,z), and the ramp sweeps across the map in scan order every iteration
                                // instead of growing one ring. Making it serial removed the nondeterminism
                                // and left the leapfrog running deterministically.
                                // MEASURED, seed 530442569 n=2049: 3,507 raised banks produced 561,814
                                // feathered cells — 160 per seed, where four rings around an isolated seed
                                // can reach at most ~40 — and a write-site census attributed 85.6% of ALL
                                // flat land (300,217 of 350,888 cells) to this one write. It also explains
                                // why v132's `+ det` repair did nothing here: `det` is measured against live
                                // `h`, and in a sweep the neighbours it averages were themselves written to
                                // the ramp moments earlier, so the detail it recovers is zero by
                                // construction. Stamp 1 = a bank, or a ring completed in a PREVIOUS
                                // iteration; stamp 2 = written this iteration and not yet a seed.
                                if (bankStamp[j] == 1 && h[j] > hi) hi = h[j];
                            }
                            // ⚠ RAISE THE CELL, DO NOT PLANE IT. Setting `h = hi - FEATHER` writes an ABSOLUTE
                            // height, so every cell in a ramp lands on the same surface and the ground's own
                            // texture is erased. With a few hundred rivers that touched ~0.9% of the world and
                            // nobody noticed; with first-order streams it overwrote 14% and showed up as large
                            // featureless patches at the foot of mountains. Carry the cell's own micro-relief
                            // (its offset from its neighbours) onto the new height: the bank still ramps down
                            // to natural ground, but it follows the terrain's grain instead of ironing it flat.
                            if (hi > 0f && h[i2] < hi - FEATHER)
                            {
                                // ⚠ RAISE-ONLY IS THE INVARIANT THAT CONTAINS THE WATER — detail rides ON TOP of
                                // it, it never digs. The first cut wrote `hi - FEATHER + det` unclamped, and a
                                // cell sitting in a dip beside high neighbours has a large NEGATIVE det, so the
                                // result landed BELOW where the cell started (h=10 beside avg=20 with target 11
                                // gives 1). That punched holes straight through the banks: STRICT float 0 → 118
                                // and visible mesh lips 69 → 9,125, worst 14.93 m. Bound the detail and floor
                                // the result at the cell's own height: the ramp keeps its grain, and it can
                                // still only ever raise.
                                float avg = 0.25f * (h[i2 + 1] + h[i2 - 1] + h[i2 + n] + h[i2 - n]);
                                float det = Mathf.Clamp(h[i2] - avg, -0.35f, 0.35f);
                                h[i2] = Math.Max(h[i2], hi - FEATHER + det); if (wsrc != null) wsrc[i2] = 7;
                                bankStamp[i2] = 2; feathered++;
                            }
                        }
                    for (int i2 = 0; i2 < n * n; i2++) if (bankStamp[i2] == 2) bankStamp[i2] = 1;
                }
                FiresLogger.LogInfo($"[FiresWaterDiag] MERGE POINTS — {s_bermMergePoint:N0} junction cell(s) between a river and still water at the same level: no bank built (a bank there is a sand bar between two waters that should merge), and seams whose ground lies under the shared level were flooded at it.");
                FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: BANKS BURY THE MESH — {rimRaised:N0} bank cell(s) raised above the ribbon they touch, {feathered:N0} feathered back to natural ground: no floating water, no visible sheet edge. HOLES CLOSED: {holesFilled:N0} enclosed dry cell(s) filled with water instead of being raised into an island (see the enclosure note above). BERM EARNED vs CURB: {s_bermEarned:N0} bank cell(s) whose NATURAL (pre-carve) ground lay BELOW the water — those raises hold up a level the solve floated; {s_bermGratuitous:N0} were already above it and are raised only to make a terrace at exactly water+rim. The user's cross-section never raises natural ground: the water is contained by the CUT, not by a wall.");
            // STUDIO-ONLY: two full-grid scans that the GAME was paying for at every world gen (user: 'the
            // world generation itself is taking close to 20min now because of all the re passes'). Gated on
            // the same flag as the rest of the instrumentation; riverfield sets FIRES_FLAT_AUDIT=1.
            if (wsrc != null)
            {
            // ═══ THE JOIN AUDIT — DO TWO WATER SURFACES ACTUALLY MEET AT THE SAME HEIGHT? ════════════════
            // (user, 2026-09: "water bodies, rivers, rivers running into lakes, canyons running into still
            // water, rivers meeting the ocean NONE of it is fucking accurate, NONE of it meets at the correct
            // fucking height".)
            // ⚠ EVERY METRIC THIS FILE PRINTS WALKS ALONG A RUN. UPHILL WATER, total climb, LEVEL SPREAD —
            // all of them measure a single surface against itself, in flow order. NOT ONE of them looks at
            // the place two DIFFERENT surfaces touch. So "3 uphill rises, climb 2 m" could be true while
            // every river mouth in the world stood a metre proud of the lake it enters, and it was: the
            // instrument could not see the defect being reported.
            // This measures the JOIN: every wet cell whose 4-neighbour is wet and belongs to a DIFFERENT
            // water family, and the height difference across that boundary. A correct join is 0.
            {
                var gaps = new List<float>(1 << 16);
                double gsum = 0; float gworst = 0f; int gworstAt = -1;
                long nRiverStill = 0, nRiverOcean = 0, nStillStill = 0;
                double sRiverStill = 0, sRiverOcean = 0, sStillStill = 0;
                for (int z2 = 1; z2 < n - 1; z2++)
                    for (int x2 = 1; x2 < n - 1; x2++)
                    {
                        int i2 = z2 * n + x2;
                        if (w[i2] <= h[i2] + 0.01f) continue;                  // dry
                        bool aRiver = mask[i2] == 1 || mask[i2] == 3;
                        bool aOcean = w[i2] <= SEA + 0.3f;
                        for (int e = 0; e < 4; e++)
                        {
                            int j = e == 0 ? i2 + 1 : e == 1 ? i2 - 1 : e == 2 ? i2 + n : i2 - n;
                            if (w[j] <= h[j] + 0.01f) continue;                // dry neighbour
                            bool bRiver = mask[j] == 1 || mask[j] == 3;
                            bool bOcean = w[j] <= SEA + 0.3f;
                            if (aRiver == bRiver && aOcean == bOcean) continue; // same family — not a join
                            float gap = Math.Abs(w[i2] - w[j]);
                            gaps.Add(gap); gsum += gap;
                            if (gap > gworst) { gworst = gap; gworstAt = i2; }
                            if (aOcean || bOcean) { nRiverOcean++; sRiverOcean += gap; }
                            else if (aRiver != bRiver) { nRiverStill++; sRiverStill += gap; }
                            else { nStillStill++; sStillStill += gap; }
                        }
                    }
                if (gaps.Count > 0)
                {
                    gaps.Sort();
                    float P(float f) => gaps[Math.Min(gaps.Count - 1, (int)(gaps.Count * f))];
                    long over10 = 0, over50 = 0, over200 = 0;
                    for (int q = 0; q < gaps.Count; q++)
                    { if (gaps[q] > 0.10f) over10++; if (gaps[q] > 0.50f) over50++; if (gaps[q] > 2.00f) over200++; }
                    float wx0 = gworstAt >= 0 ? -_half + (gworstAt % n) * cellM : 0f;
                    float wz0 = gworstAt >= 0 ? -_half + (gworstAt / n) * cellM : 0f;
                    FiresLogger.LogInfo($"[FiresWaterDiag] JOIN AUDIT — {gaps.Count:N0} boundary(ies) where two DIFFERENT water surfaces touch. " +
                              $"height difference p50 {P(0.5f):F2} m · p90 {P(0.9f):F2} m · p99 {P(0.99f):F2} m · max {gworst:F2} m at ({wx0:0},{wz0:0}), mean {(gsum / gaps.Count):F2} m. " +
                              $"OVER 0.10 m: {over10:N0} ({100.0 * over10 / gaps.Count:F1} %) · over 0.50 m: {over50:N0} ({100.0 * over50 / gaps.Count:F1} %) · over 2 m: {over200:N0} ({100.0 * over200 / gaps.Count:F1} %). " +
                              $"BY FAMILY — river↔still {nRiverStill:N0} mean {(nRiverStill > 0 ? sRiverStill / nRiverStill : 0):F2} m · " +
                              $"river/still↔OCEAN {nRiverOcean:N0} mean {(nRiverOcean > 0 ? sRiverOcean / nRiverOcean : 0):F2} m · " +
                              $"other {nStillStill:N0} mean {(nStillStill > 0 ? sStillStill / nStillStill : 0):F2} m. " +
                              $"A CORRECT JOIN IS 0.00 m. This is the metric the along-run ones (UPHILL WATER, LEVEL SPREAD) structurally cannot see: they walk ONE surface against itself and say nothing about where it meets another.");
                }
                // ═══ AND THE GAP — CELLS WHERE NEITHER SURFACE IS DRAWN ══════════════════════════════════
                // (user, 2026-09, on a frozen coast at X2896 Z-6865: "none of them even properly connect,
                // they constantly have a fucking gap".)
                // ⚠ THE JOIN AUDIT ABOVE IS BLIND TO THIS BY CONSTRUCTION. It walks boundaries where two wet
                // cells already TOUCH, so it can only score joins that exist. A gap is the absence of a join:
                // a DRY cell sitting between two different water families, which is what you see as a strip
                // of bare ground between a river and the lake it is supposed to enter, or between the shore
                // and the sea. Counting only the joins that exist and calling the result good is the same
                // closed-list mistake this file has made before.
                long gapCells = 0, gapToOcean = 0; float gapWorstDrop = 0f; int gapAt = -1;
                for (int z2 = 1; z2 < n - 1; z2++)
                    for (int x2 = 1; x2 < n - 1; x2++)
                    {
                        int i2 = z2 * n + x2;
                        if (w[i2] > h[i2] + 0.01f) continue;                   // this cell IS wet — not a gap
                        bool sawRiver = false, sawStill = false, sawOcean = false;
                        float hi = float.MinValue, lo = float.MaxValue;
                        for (int e = 0; e < 4; e++)
                        {
                            int j = e == 0 ? i2 + 1 : e == 1 ? i2 - 1 : e == 2 ? i2 + n : i2 - n;
                            if (w[j] <= h[j] + 0.01f) continue;                // dry neighbour
                            if (w[j] <= SEA + 0.3f) sawOcean = true;
                            else if (mask[j] == 1 || mask[j] == 3) sawRiver = true;
                            else sawStill = true;
                            if (w[j] > hi) hi = w[j];
                            if (w[j] < lo) lo = w[j];
                        }
                        int fams = (sawRiver ? 1 : 0) + (sawStill ? 1 : 0) + (sawOcean ? 1 : 0);
                        if (fams < 2) continue;                                // not between two waters
                        gapCells++;
                        if (sawOcean) gapToOcean++;
                        float drop = hi - h[i2];                               // how far the ground sits BELOW the higher surface
                        if (drop > gapWorstDrop) { gapWorstDrop = drop; gapAt = i2; }
                    }
                {
                    float gx0 = gapAt >= 0 ? -_half + (gapAt % n) * cellM : 0f;
                    float gz0 = gapAt >= 0 ? -_half + (gapAt / n) * cellM : 0f;
                    FiresLogger.LogInfo($"[FiresWaterDiag] GAP AUDIT — {gapCells:N0} DRY cell(s) sit BETWEEN two different water families ({gapToOcean:N0} of them against the OCEAN). These are the cells where nothing is drawn and you see bare ground where two waters should meet. Worst: ground {gapWorstDrop:F2} m BELOW the higher of its two neighbouring surfaces at ({gx0:0},{gz0:0}) — a positive number there means the ground is UNDER the water and the sheet simply is not covering it, which is a footprint failure, not a level one. 0 is correct.");
                }
            }
            }   // end studio-only water audits
            }

            // (TRACE 2026-08-11, user: "the river stops for no goddamn reason and then starts again 40m away")
            // GAP SCAN — walk every FINAL river polyline and sample wetness along it. A dry run between two wet
            // runs IS the visible break. Reports how many, how long, and where the worst ones are, so the cause
            // can be attributed instead of guessed (steep-cascade drop vs carve-bound dry vs off-valley meander).
            if (riverPolys != null)
            {
                int gaps = 0; float gapM = 0f; int g10 = 0, g30 = 0, g100 = 0, gBig = 0;
                var worstG = new List<(float len, float wx, float wz)>();
                foreach (var poly in riverPolys)
                {
                    int nn2 = poly.Length / 4;
                    bool prevWet = false; float runLen = 0f; float gx0 = 0, gz0 = 0;
                    for (int k = 0; k < nn2; k++)
                    {
                        int cx = Mathf.Clamp((int)((poly[k * 4] + rhalf) / cellM + 0.5f), 0, n - 1);
                        int cz = Mathf.Clamp((int)((poly[k * 4 + 1] + rhalf) / cellM + 0.5f), 0, n - 1);
                        int i = cz * n + cx;
                        bool wet = w[i] > h[i] + 0.05f;
                        float segM = k > 0 ? Mathf.Sqrt((poly[k * 4] - poly[(k - 1) * 4]) * (poly[k * 4] - poly[(k - 1) * 4]) + (poly[k * 4 + 1] - poly[(k - 1) * 4 + 1]) * (poly[k * 4 + 1] - poly[(k - 1) * 4 + 1])) : 0f;
                        if (!wet)
                        {
                            if (prevWet) { runLen = 0f; gx0 = poly[k * 4]; gz0 = poly[k * 4 + 1]; }
                            runLen += segM;
                        }
                        else if (!prevWet && runLen > 0f)
                        {
                            // a dry run that ENDED in wet — a true interior gap
                            gaps++; gapM += runLen;
                            if (runLen <= 10f) g10++; else if (runLen <= 30f) g30++; else if (runLen <= 100f) g100++; else gBig++;
                            if (worstG.Count < 10) worstG.Add((runLen, gx0, gz0));
                            runLen = 0f;
                        }
                        prevWet = wet;
                    }
                }
                worstG.Sort((a, b) => b.len.CompareTo(a.len));
                var gb = new System.Text.StringBuilder();
                foreach (var e in worstG) gb.Append($"\n    {e.len:0}m gap starting ({e.wx:0},{e.wz:0})");
                FiresLogger.LogInfo($"[FiresWaterDiag] RIVER GAP SCAN: {gaps:N0} interior dry gaps along the final polylines, {gapM:N0}m total — ≤10m {g10}, ≤30m {g30}, ≤100m {g100}, >100m {gBig}.{gb}");
            }
            // ═══ OVERLAP CENSUS — IS THE MULTI-LEVEL SHEET *BETWEEN* RUNS, OR *WITHIN* ONE? ══════════════
            // (2026-08-19.) `riverfield`'s N. LEVEL SPREAD proves the defect exists (52.7 % of river cells sit
            // within 10 m of another river surface more than a metre off in height) but not what causes it,
            // and the two candidates want opposite fixes:
            //   • BETWEEN runs — two traced channels crossing at levels each solved from its own local banks.
            //     Fix = the runs must share a surface (reconcile/merge), which is a NETWORK problem.
            //   • WITHIN one run — a single polyline whose own longitudinal profile is inconsistent.
            //     Fix = the longitudinal solve, which is a per-run problem.
            // Guessing between them is how a day gets spent tuning cross-sections on a network that was never
            // one surface. `seedIdx` already maps every carved cell to the spine cell that owns it, and
            // `spinePoly` maps that spine cell to its RUN, so the attribution is exact rather than inferred.
            {
                const int R = 5;                 // 10 m at game res — the radius riverfield reports
                long pairsSame = 0, pairsDiff = 0;   // neighbour pairs >1 m apart in level, by same/different run
                long stepPairs = 0, gradePairs = 0;  // ...within-run pairs judged by ALONG-CHANNEL grade: a STEP vs a real descent
                float stepWorst = 0f, stepWorstAlong = 0f, stepWorstX = 0f, stepWorstZ = 0f, stepWorstA = 0f, stepWorstB = 0f;
                var stepSites = new List<(float x, float z, float drop, float along)>();   // where to go and LOOK
                float worstDiff = 0f; int worstAt = -1;
                for (int z2 = R; z2 < n - R; z2++)
                    for (int x2 = R; x2 < n - R; x2++)
                    {
                        int i2 = z2 * n + x2;
                        if ((mask[i2] != 1 && mask[i2] != 3) || w[i2] <= h[i2] + 0.01f || w[i2] <= SEA + 0.3f) continue;
                        int sd = seedIdx[i2];
                        if (sd < 0 || !spinePoly.TryGetValue(sd, out int myPoly)) continue;
                        for (int dz = -R; dz <= R; dz++)
                            for (int dx = -R; dx <= R; dx++)
                            {
                                int j = i2 + dz * n + dx;
                                if ((mask[j] != 1 && mask[j] != 3) || w[j] <= h[j] + 0.01f || w[j] <= SEA + 0.3f) continue;
                                float d = Math.Abs(w[i2] - w[j]);
                                if (d <= 1f) continue;
                                int sj = seedIdx[j];
                                if (sj < 0 || !spinePoly.TryGetValue(sj, out int itsPoly)) continue;
                                if (itsPoly == myPoly) pairsSame++; else pairsDiff++;
                                // ═══ A STEP, OR JUST A GRADIENT? ══════════════════════════════════════════
                                // The discriminator every canyon number so far has lacked. Two cells 16 m
                                // apart in SPACE can be hundreds of metres apart ALONG a meandering channel,
                                // and an 11 m difference between those is an ordinary river gradient. Only a
                                // large level difference over a SHORT along-channel distance is a step — the
                                // thing the user is actually asking to remove ("the rest of the river all
                                // lays out at the canyons bottom", not perched at several levels).
                                // Judge it as a GRADE: metres of fall per metre travelled along the run.
                                if (itsPoly == myPoly
                                    && spineArc.TryGetValue(sd, out float arcA)
                                    && spineArc.TryGetValue(sj, out float arcB))
                                {
                                    float along = Math.Abs(arcA - arcB);
                                    if (along < 1f) along = 1f;                 // same station; any drop here is a step
                                    if (d / along > 0.25f)
                                    {
                                        stepPairs++;                            // >25 % grade over the channel = a STEP
                                        // ⚠ LOCATE THEM. Five mechanisms were each tested against this count
                                        // and each moved it by ~1 % or less (canyon weir gate, weir removal,
                                        // isotonic regression, canyon floor from the fill table, monotone bed
                                        // bound). At that point the model is wrong, not the tuning, and the
                                        // next input has to be an observation of a real place rather than a
                                        // sixth reading of the pipeline. Keep the worst few with world
                                        // coordinates so they can be stood in with `waterat`.
                                        if (d > stepWorst)
                                        {
                                            stepWorst = d; stepWorstAlong = along;
                                            stepWorstX = -_half + x2 * cellM; stepWorstZ = -_half + z2 * cellM;
                                            stepWorstA = w[i2]; stepWorstB = w[j];
                                        }
                                        if (stepSites.Count < 12 && d > 4f
                                            && (stepSites.Count == 0 || Math.Abs(stepSites[stepSites.Count - 1].x - (-_half + x2 * cellM)) > 300f
                                                                      || Math.Abs(stepSites[stepSites.Count - 1].z - (-_half + z2 * cellM)) > 300f))
                                            stepSites.Add((-_half + x2 * cellM, -_half + z2 * cellM, d, along));
                                    }
                                    else gradePairs++;                          // a real descent, however steep it looks in plan
                                }
                                if (d > worstDiff && itsPoly != myPoly) { worstDiff = d; worstAt = i2; }
                            }
                    }
                // ═══ THE ONLY DEFECT THAT MATTERS: THE SURFACE GOING BACK UP ════════════════════════════
                // (2026-08-22, user: "as long as steps are descending along the river's path they're actually
                // a good thing, rivers do naturally step — the issue is we are dropping and then raising back
                // up.") That is the correct definition and it retires the >25 %-grade "STEP" count above: a
                // step is a waterfall and belongs there. The defect is NON-MONOTONICITY along the path.
                // `SetBankDerivedLevels` does enforce monotone on the POLYLINE — but the FIELD level is not
                // the polyline. It goes through the Voronoi assignment (`wl = w[sd]`), the min-write where
                // stems overlap, the surface smooth, and the final snap. Any of those can put a rise back in,
                // and nothing has ever checked. Walk each run's own spine cells in flow order (by arc) and
                // count every place the water climbs going downstream.
                long riseCount = 0; double riseSum = 0; float riseWorst = 0f; float riseX = 0f, riseZ = 0f;
                var riseBy = new long[12]; long riseCrossRun = 0;
                long riseBodyBody = 0, riseRiverIntoBody = 0, riseBodyIntoRiver = 0, riseRiverRiver = 0;
                double bbFromEndSum = 0; long bbNearEnd = 0, bbIsLastNode = 0;   // WHERE on the run do these sit? the appended landing node lives at the very end
                long riseBedToo = 0, riseWaterOnly = 0; double riseBedSum = 0;   // is the BED climbing with it (carve too shallow) or not (level wrong)?
                var riseSites = new List<(float x, float z, float up)>();
                {
                    var byRun = new Dictionary<int, List<(float arc, int cell)>>();
                    foreach (var kv in spinePoly)
                    {
                        int cell = kv.Key;
                        if ((mask[cell] != 1 && mask[cell] != 3) || w[cell] <= h[cell] + 0.01f || w[cell] <= SEA + 0.3f) continue;
                        if (!spineArc.TryGetValue(cell, out float a)) continue;
                        if (!byRun.TryGetValue(kv.Value, out var lst)) { lst = new List<(float, int)>(); byRun[kv.Value] = lst; }
                        lst.Add((a, cell));
                    }
                    foreach (var kv in byRun)
                    {
                        var lst = kv.Value;
                        if (lst.Count < 3) continue;
                        lst.Sort((p, q) => p.arc.CompareTo(q.arc));      // arc grows from the headwater
                        for (int t2 = 1; t2 < lst.Count; t2++)
                        {
                            float up = w[lst[t2].cell] - w[lst[t2 - 1].cell];
                            if (up <= 0.10f) continue;                   // descending (or flat) — correct, and a step is fine
                            riseCount++; riseSum += up;
                            // ⚠ WHOSE RISE IS IT — THE WATER'S, OR THE BED'S? (user, 2026-08-22: "it could
                            // also be places where the carve itself does not run deep enough causing the
                            // water to have to come back up a bit".) That is a distinct mechanism from a bad
                            // level solve and it wants the opposite fix, so measure it rather than argue: if
                            // the BED climbs alongside the surface, the carve failed to dig and the water is
                            // merely sitting on it; if the bed descends while the surface climbs, the level
                            // is wrong on a perfectly good channel.
                            float bedUp = h[lst[t2].cell] - h[lst[t2 - 1].cell];
                            // …and WHICH PASS put the higher cell where it is. The rise is created by whoever
                            // wrote the upstream-of-flow cell UP, so that is the one to attribute.
                            if (lvlSrc != null) riseBy[lvlSrc[lst[t2].cell]]++;
                            // ...and WHAT KIND OF PAIR is it? The attribution says BODY ADOPTION wrote the
                            // higher cell, but that alone does not say whether the river approached a lake
                            // from below (which the upstream backwater is supposed to have fixed) or whether
                            // the spine crosses TWO bodies standing at different levels (which no river-side
                            // pass may touch, because a lake owns its own surface). Those want opposite work,
                            // so classify rather than assume.
                            {
                                bool hiBody = seeded != null && seeded[lst[t2].cell] > hOrig[lst[t2].cell] + 0.01f;
                                bool loBody = seeded != null && seeded[lst[t2 - 1].cell] > hOrig[lst[t2 - 1].cell] + 0.01f;
                                if (hiBody && loBody) { riseBodyBody++; float arcEnd = spineArc[lst[lst.Count - 1].cell]; float fromEnd = arcEnd - spineArc[lst[t2].cell]; bbFromEndSum += fromEnd; if (fromEnd <= 24f) bbNearEnd++; if (t2 == lst.Count - 1) bbIsLastNode++; }
                                else if (loBody) riseBodyIntoRiver++;
                                else riseRiverRiver++;
                            }
                            if (spinePoly.TryGetValue(lst[t2].cell, out int pA) && spinePoly.TryGetValue(lst[t2 - 1].cell, out int pB) && pA != pB) riseCrossRun++;
                            if (bedUp > 0.10f) { riseBedToo++; riseBedSum += bedUp; }
                            else riseWaterOnly++;
                            if (up > riseWorst)
                            {
                                riseWorst = up;
                                riseX = -_half + (lst[t2].cell % n) * cellM;
                                riseZ = -_half + (lst[t2].cell / n) * cellM;
                            }
                            if (riseSites.Count < 10 && up > 1.5f
                                && (riseSites.Count == 0 || Math.Abs(riseSites[riseSites.Count - 1].x - (-_half + (lst[t2].cell % n) * cellM)) > 400f))
                                riseSites.Add((-_half + (lst[t2].cell % n) * cellM, -_half + (lst[t2].cell / n) * cellM, up));
                        }
                    }
                }
                FiresLogger.LogInfo($"[FiresWaterDiag] UPHILL WATER — the surface RISING as it goes downstream, walked along each run's own spine in flow order. "
                        + $"{riseCount:N0} rise(s), total climb {riseSum:F0} m, worst single rise {riseWorst:F2} m at ({riseX:0},{riseZ:0}). "
                        + "A descending STEP is a waterfall and is wanted; only a RISE is the defect (user, 2026-08-22). "
                        + $"OF THOSE RISES: {riseBedToo:N0} have the BED climbing with the water (the carve did not run deep enough there — the water is simply sitting on ground that goes up, user hypothesis) and {riseWaterOnly:N0} have the bed DESCENDING while the surface climbs (the level is wrong on a good channel). Bed climb total {riseBedSum:F0} m. "
                        + "SetBankDerivedLevels makes the POLYLINE monotone, but the FIELD passes through the Voronoi level assignment, the overlap min-write, the surface smooth and the final snap. "
                        + (lvlSrc != null ? $"WHO WROTE THE HIGHER CELL: river-paint {riseBy[1]:N0}, dry-snap(mid) {riseBy[2]:N0}, paintLvl-apply {riseBy[3]:N0}, surface-smooth {riseBy[4]:N0}, shore-flood {riseBy[5]:N0}, BODY-ADOPTION {riseBy[6]:N0}, sea-snap {riseBy[7]:N0}, dry-snap(final) {riseBy[8]:N0}, late-fill/bury {riseBy[9]:N0}, spine-descent {riseBy[10]:N0}, untouched {riseBy[0]:N0}. " : "(set FIRES_FLAT_AUDIT=1 for per-pass attribution.) ")
                        + $"RISE PAIRS: {riseRiverIntoBody:N0} are a RIVER approaching a still body FROM BELOW (the backwater should have met it), {riseBodyBody:N0} are TWO BODIES at different levels on one spine (mean {(riseBodyBody > 0 ? bbFromEndSum / riseBodyBody : 0):F0} m from the run END along the channel; {bbNearEnd:N0} within 24 m of the end, {bbIsLastNode:N0} ARE the final spine cell - the appended landing node lives exactly there), {riseBodyIntoRiver:N0} body-then-river, {riseRiverRiver:N0} river-to-river. "
                        + $"CROSS-RUN: {riseCrossRun:N0} of the rises have a DIFFERENT run owning the higher cell - those are two reaches disagreeing at an overlap, not one reach climbing. "
                        + (riseSites.Count > 0 ? "GO AND LOOK: " + string.Join("  |  ", riseSites.ConvertAll(s => $"({s.x:0},{s.z:0}) +{s.up:F1} m")) : ""));

                long tot = pairsSame + pairsDiff;
                float wx0 = worstAt >= 0 ? -_half + (worstAt % n) * cellM : 0f;
                float wz0 = worstAt >= 0 ? -_half + (worstAt / n) * cellM : 0f;
                FiresLogger.LogInfo($"[FiresWaterDiag] OVERLAP CENSUS — river cell pairs within {R * cellM:0} m whose LEVELS differ by >1 m: {tot:N0} total. "
                        + $"BETWEEN DIFFERENT RUNS {pairsDiff:N0} ({(tot > 0 ? 100.0 * pairsDiff / tot : 0):F1} %) · WITHIN ONE RUN {pairsSame:N0} ({(tot > 0 ? 100.0 * pairsSame / tot : 0):F1} %). "
                        + $"Worst cross-run gap {worstDiff:F1} m at ({wx0:0},{wz0:0}). "
                        + "BETWEEN dominant ⇒ the runs must share a surface (a NETWORK fix); WITHIN dominant ⇒ the longitudinal solve is the fault."
                        + $" ── OF THE WITHIN-RUN PAIRS, judged against ALONG-CHANNEL distance: {stepPairs:N0} are STEPS (>25 % grade along the channel — the defect) and {gradePairs:N0} are ordinary DESCENT."
                        + " Straight-line distance cannot tell those apart: a confined river meanders, so two cells 16 m apart in plan can be hundreds of metres apart along the channel and an 11 m difference between them is a normal gradient. Every canyon multi-level number before this one lacked that discriminator, which is why two fixes aimed at it measured as no-ops."
                        + $"\n    WORST STEP {stepWorst:F1} m over {stepWorstAlong:F0} m of channel at ({stepWorstX:0},{stepWorstZ:0}) — levels {stepWorstA:F1} vs {stepWorstB:F1}."
                        + $"\n    GO AND LOOK (teleport, then `waterat`): {string.Join("  |  ", stepSites.ConvertAll(s => $"({s.x:0},{s.z:0}) {s.drop:F1} m over {s.along:F0} m"))}");
            }
            waterLevel = w;
            return mask;
        }
    }
}
