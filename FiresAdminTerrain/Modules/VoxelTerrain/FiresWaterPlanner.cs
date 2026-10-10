using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══════════════════════════════════════════════════════════════════════════
    // THE PLANNER — decides the water. Runs once at generation, after terrain and
    // biomes exist, before ANY water is shaped. Consumes the drainage ANALYSIS
    // (depression-fill table + traced flow polylines) strictly as an ADVISOR —
    // it suggests where nature would put water; the planner DECIDES, and once a
    // level is written into the plan it is constant forever.
    //
    // Order of decisions (user contract): highest to lowest. Mountain tarns and
    // high lakes first, then downslope, marshes from their own suitability, then
    // the connections that run every body's outflow down to the next body or the
    // sea. Terrain conformance and rendering happen ELSEWHERE, from the plan.
    // ═══════════════════════════════════════════════════════════════════════════
    internal static class FiresWaterPlanner
    {
        private const float SEA = 30f;

        // Tuning (deliberately few, deliberately named).
        // BASIN_MIN_DEPTH is ABOVE the baked surface detail's amplitude (±1.75m): the grid
        // now carries that detail (hard data), so a 1.2m floor turned every noise dip into a
        // pond — 3,790 sub-hectare puddles on one seed, water in nearly every chunk, and the
        // per-chunk water build hitched the game to a crawl. A real basin holds ≥2.5m; noise
        // dips don't. BODY_MIN_CELLS backstops it on size.
        private const float BASIN_MIN_DEPTH = 2.5f;   // a depression must hold ≥ this to be real water, not surface noise
        private const float LEVEL_UNDER_SPILL = 0.85f; // a body's level sits this far under its spill lip.
                                                       // (v105) 0.35 → 0.85: BODY BANK RELIEF — the user measured a persistent
                                                       // ~0.5m step at every body↔river junction, iteration after iteration:
                                                       // rivers ride relief+drop under their banks while bodies sat at
                                                       // spill−0.35 — every seam lump/break inherited that offset. All still
                                                       // water drops the same 0.5 so bodies meet the river convention.
        // ═══ THESE ARE AREAS, NOT CELL COUNTS (v135) ══════════════════════════════════════════════════════
        // Every size gate here was authored against the game's FULL-RESOLUTION grid — n=10501 across a 21 km
        // world, i.e. 2 m cells and 4 m² each — and the old comments said so outright: "≈1600 m²", "≈3.2 km²",
        // "≈0.24 km²", "256 m² at 2 m". They were still stored as raw CELL COUNTS, and resolution is
        // PLAYER-SELECTABLE (FiresGameUI RES = 1025 / 2049 / 4097 / 10501), so each one meant something
        // different in every world: a 2,500-cell marsh floor is 0.01 km² at n=10501 and 0.066 km² at n=4097 —
        // a six-fold difference in the bar a basin must clear. The same seed therefore grew DIFFERENT MARSHES
        // at different settings, and every measurement taken in the studio (which runs at 2049/4097) described
        // a world the game does not build. The hydrology beside this already solved the same problem the right
        // way — "the hydrology is authored at the 2048 reference grid… cellA converts flow accumulation into
        // physical drainage AREA, so rivers, valleys and erosion are the SAME physical features whether a cell
        // is 10 m or 2 m" — these constants simply never joined it.
        // Held as areas and converted per run against the live cell size. n=10501 reproduces exactly what it
        // built before (400/3000/800000/2500/60000 cells); every other resolution now agrees with it.
        private const float BODY_MIN_AREA_M2   = 1_600f;      // was 400 cells — smaller → not worth a body (noise speck)
        private const float LAKE_MIN_AREA_M2   = 12_000f;     // was 3,000 cells — bigger than this → Lake, else Pond
        private const float BODY_MAX_AREA_M2   = 3_200_000f;  // was 800,000 cells ≈ 3.2 km²: a body bigger than this is
                                                              // not a discrete lake/marsh but a coastal drainage
                                                              // lowland — the flood overflows, the level drops,
                                                              // and near sea it falls out (rivers drain it to the
                                                              // ocean instead of one map-swallowing sheet).
        private const float MARSH_MIN_AREA_M2  = 10_000f;     // was 2,500 cells — swamp-biome region floor for a marsh body

        /// Cells covering `areaM2` at the current cell size. At least 1, so no gate silently becomes "anything".
        private static int CellsFor(float areaM2, float cellA) => System.Math.Max(1, (int)(areaM2 / System.Math.Max(1e-6f, cellA) + 0.5f));
        // ── THE MARSH MODEL (user, verbatim): "a LARGE SHALLOW LAKE with MANY ROLLING ISLANDS" ───────────
        // 0.35 → 0.70. The level is the height that submerges this share of the region, so 0.35 flooded only
        // the lowest THIRD — i.e. scattered puddles sitting in the dips, which is the exact model the user
        // rejected. A large shallow lake with islands is the INVERSE: most of the basin is under one flat
        // sheet and only the mounds stand out of it. 0.70 floods most and leaves the top ~30% emergent as
        // the islands. The shallow part is already handled downstream (the marsh bed shaper cuts 0.5-1.8m of
        // murk under the sheet and lerps wide low domes UP THROUGH it — that pass is the "rolling islands"
        // generator and it was always right; it was just being handed a level far too low to island against).
        private const float MARSH_SUBMERGE_PCT = 0.70f; // marsh level submerges ~this share of its region
        private const float MARSH_BUDGET_AREA_M2 = 240_000f; // was 60,000 cells ≈ 0.24 km² — absolute footprint allowance (see the budget note)
        private const int   MARSH_LEVEL_TRIES = 8;       // an overflowing marsh settles to a lower level instead of dying
        private const float MARSH_LEVEL_STEP = 0.75f;    // ...8 x 0.75 = 6m of range, vs the old 3 x 0.5 = 1.5m
        // ── A MARSH TOLERATES INCLUSIONS; A LAKE DOES NOT (2026-08-13) ────────────────────────────────────
        // The overlap referee drops a candidate whole unless 60% of its cells are unowned. That is the right
        // rule for a DISCRETE body — a lake half-eaten by another lake is a bad lake. A marsh is not discrete:
        // it is a broad wet district that legitimately contains ponds and abuts lakes, and it floods flat
        // lowland where it will always meet its neighbours. Measured: 8 of 11 surviving marsh candidates died
        // here. The referee already handles partial ownership correctly when it builds the mask (it skips
        // owned cells), so the only thing throwing these away was the fraction test itself.
        // 0.25 was measured and REVERTED: at that fraction a marsh places even when most of its cells are
        // already owned, so its mask comes out as swiss cheese with an enormous perimeter — every hole edge is
        // another mesh edge to contain, and it took lowland float from 46 to 907. 0.5 keeps marshes tolerant of
        // the ponds inside them without admitting shredded footprints. Discrete bodies keep the original 0.6.
        private const float MARSH_FREE_FRAC = 0.5f;      // marshes need only this share unowned (discrete bodies: 0.6)
        private const float MARSH_MIN_SHEET = 0.4f;      // a basin whose lip can't hold this much water is damp ground, not a marsh
        // High tide is EXACTLY Y30 (FiresEnv TideHighOffsetM = 0 — the ocean only ever recedes). An inland
        // marsh sheet within this of SEA would be coplanar with the ocean plane and the two would fight, so
        // the band is forbidden: below it the ocean owns the water outright, above it the marsh is inland.
        private const float MARSH_TIDE_BAND = 1.2f;      // marsh sheets may never sit within this of sea level
        // A tributary has ARRIVED once it is this close to a still body; nodes beyond that are the trace
        // following a contour around the shore, not a river approaching it. Roughly one trunk channel width.
        private const float SHORE_ARRIVE_M = 12f;
        // Two runs landing within this of each other on one body are the SAME mouth (user: "one inlet and one
        // outlet when a river meets a stillbody within 10m"). The later one is trimmed to join at that mouth.
        private const float INLET_MERGE_M = 10f;
        private static int s_sinkSea, s_sinkJoin, s_sinkNone;   // (TRACE) how each run's destination actually resolved: reached the sea / joined another river / had none and was dropped
        private static int s_emergentPonds;   // (TRACE) ponds the rivers MADE for themselves - a run entered a bodyless basin, so the basin became a pond and the run continues over its spill
        private static int s_endsLanded;   // (TRACE) runs whose final node was landed ON the receiving body's footprint instead of stopping short of it
        private static int s_shoreTrimmed, s_inletMerged, s_tracesReversed, s_endsReconnected;
        // ── WHY THERE IS NO "MERGE THE FRAGMENTS" PASS HERE (measured 2026-08-13, seed 220092304 @ n=10501) ──
        // The obvious theory — the noise-gated swamp biome shatters one basin into crumbs, so merge them — is
        // WRONG, and the census (MARSH REGION CENSUS, logged below) is what proves it. Sizes run
        // p50 48 · p75 304 · p90 1,024 · p99 13,136 · max 37,632 cells, and 31 regions ALREADY clear the 2500
        // floor holding 71% of all swamp. The 558 sub-floor regions are noise fringe (median 48 cells = 192 m²),
        // not fragments of anything. A gap-bridging fill was built and measured here: it merged three regions
        // out of 562 and slightly hurt (broken-ground 0 → 2). Removed. The losses are downstream — footprint
        // OVERFLOW and the overlap REFEREE — see MARSH_LEVEL_TRIES and MARSH_FREE_FRAC.
        // Also measured and NOT the problem: the biome slope gate is evaluated over a 120m baseline
        // (FiresBiome3D e = 60f), not per-cell, so fine terrain detail does not speckle it.
        // The sea's surface, as DATA — the Ocean body's Level (see WaterBodyKind.Ocean). Falls back to SEA if
        // a plan somehow has no ocean entry (old persisted plans), so this can never return a wrong level.
        private static float OceanLevel(FiresWaterPlan plan)
        {
            if (plan != null && plan.Bodies != null)
                for (int i = 0; i < plan.Bodies.Count; i++)
                    if (plan.Bodies[i].Kind == WaterBodyKind.Ocean) return plan.Bodies[i].Level;
            return SEA;
        }

        private const float ESTUARY_FLOOR = SEA - 6f;   // ocean outfalls end here — a metre UNDER spring-low tide
                                                         // (FiresEnv SpringLowOffsetM = -5 → 25), so a river meets
                                                         // the sea BELOW the ocean plane at every tide and the two
                                                         // sheets clip; it can never float over the ocean.

        // The footprint of a decided level = EVERYTHING that level covers: flood from
        // the seed cells across any neighbour under the level. The true shoreline is
        // where ground rises past the level — so a body's edge can NEVER stand over
        // lower dry ground (the dangle-ring the audit caught). If the flood exceeds
        // its budget the level does not hold on this terrain — the caller lowers it
        // (smaller body beats a leaking one) or drops the candidate.
        private static List<int> ExpandFootprint(List<int> seedCells, float level, float[] g, int n,
                                                 int[] stamp, int stampId, int budget, out bool overflow)
        {
            overflow = false;
            var cells = new List<int>(seedCells.Count * 2);
            var stack = new Stack<int>();
            foreach (int i in seedCells)
                if (g[i] < level && stamp[i] != stampId) { stamp[i] = stampId; stack.Push(i); }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                cells.Add(i);
                if (cells.Count > budget) { overflow = true; return cells; }
                int x = i % n, z = i / n;
                for (int e = 0; e < 4; e++)
                {
                    int xx = x + (e == 0 ? 1 : e == 1 ? -1 : 0), zz = z + (e == 2 ? 1 : e == 3 ? -1 : 0);
                    if (xx < 1 || xx >= n - 1 || zz < 1 || zz >= n - 1) continue;
                    int j = zz * n + xx;
                    if (stamp[j] != stampId && g[j] < level) { stamp[j] = stampId; stack.Push(j); }
                }
            }
            return cells;
        }

        // `wetland` (nullable): FiresWorldGenBase._wetlandMask — the closed basins hydrology classified as
        // WETLAND rather than lake (flat-floored, shallow, big enough to be a marsh). It is a statement about
        // the ground, so it binds BOTH funnels below: the lake funnel may not claim that ground, and the marsh
        // funnel gets it via the swamp biome ApplyWetlandBiome painted from the same mask. Null ⇒ the old
        // behaviour exactly (imports, and any caller with no hydrology pass in front of it).
        internal static FiresWaterPlan BuildPlan(float[] g, byte[] biomes, float[] fill, List<float[]> polys,
                                                 int n, float half, int seed, bool[] wetland = null)
        {
            var plan = new FiresWaterPlan();
            // ═══ THE OCEAN, AS A BODY ════════════════════════════════════════════════════════════════════
            // Created FIRST so it always holds id 0 and every ocean-bound run has a real destination to
            // terminate on, exactly like a lake-bound one. It carries the sea's LEVEL as data instead of the
            // profile solver hard-coding a constant halfway through itself (see WaterBodyKind.Ocean).
            // Deliberately degenerate geometry: a ZERO-LENGTH mask (serialises as nothing — `bw.Write(Mask)`
            // would throw on null, and `Contains` already returns false once lx >= W) and point bounds, so
            // BuildIndex does not try to bucket a 21km-wide body across ~1.7M spatial buckets. Its footprint
            // is analytic — every column whose ground lies under SEA — and the existing ocean query path
            // already owns those lookups.
            plan.Bodies.Add(new WaterBody
            {
                Id = 0,
                Kind = WaterBodyKind.Ocean,
                Level = SEA,
                Gx0 = 0, Gz0 = 0, W = 0, H = 0,
                Mask = System.Array.Empty<byte>(),
                MinX = 0f, MinZ = 0f, MaxX = 0f, MaxZ = 0f,
            });
            float cell = (2f * half) / (n - 1);
            // The size gates in PHYSICAL units for this grid — see the AREA constants above. A body is the same
            // body whether the player generated the world at 1025 or 10501.
            float cellA = cell * cell;
            int bodyMinCells   = CellsFor(BODY_MIN_AREA_M2,   cellA);
            int lakeMinCells   = CellsFor(LAKE_MIN_AREA_M2,   cellA);
            int bodyMaxCells   = CellsFor(BODY_MAX_AREA_M2,   cellA);
            int marshMinCells  = CellsFor(MARSH_MIN_AREA_M2,  cellA);
            int marshBudgetFloor = CellsFor(MARSH_BUDGET_AREA_M2, cellA);
            var owner = new int[n * n];                     // cell → body id + 1 (0 = none); the overlap referee
            var stamp = new int[n * n]; int stampId = 0;    // ExpandFootprint scratch
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ── WATER DIAGNOSTICS (nail down the swamp/lake/pond issue — user, 2026-08-09) ──
            // Every place a candidate can die gets a named counter, so the [FiresWaterPlan] summary shows the FUNNEL:
            // how many basins/marsh-regions were found, and exactly which gate culled the ones that didn't survive.
            // A world with lake=0 pond=0 then reads its cause straight off the log (no lowland flat basins? all lost
            // to the overlap referee? swamp biome eating everything?) instead of guessing.
            s_shoreTrimmed = 0; s_endsLanded = 0; s_emergentPonds = 0; s_sinkSea = 0; s_sinkJoin = 0; s_sinkNone = 0; s_inletMerged = 0; s_tracesReversed = 0; s_endsReconnected = 0;
            var candidates = new List<(float level, WaterBodyKind kind, List<int> cells)>();
            int rejSteep = 0, rejHigh = 0;
            int lakeBasinsFound = 0, candLake = 0, candPond = 0, candTarn = 0;   // lowland depression funnel
            int marshRegions = 0, marshTooSmall = 0, marshBroken = 0, marshOverflow = 0, candMarsh = 0, candSeaMarsh = 0;
            // broken-ground is FOUR different tests; counting them together hid which one actually rejects.
            int brkHoldable = 0, brkNoFlood = 0, brkThinSheet = 0, brkNotLevel = 0;
            // REGION SIZE CENSUS — "562 of 589 too small" says the bar is not being cleared but not WHY.
            // These say whether consolidation can ever work: if the swamp cells are spread evenly across
            // hundreds of small regions, merging neighbours is the answer; if a handful of regions already
            // hold most of the cells, then the bar itself (or the model) is wrong and bridging is a waste.
            var marshSizes = new List<int>(1024);
            int placedLake = 0, placedPond = 0, placedTarn = 0, placedMarsh = 0, placedSeaMarsh = 0, overlapDrop = 0, oceanOwned = 0;
            // Swamp-biome EXTENT — the root the user suspects. Fraction of land that FiresBiome3D calls swamp (code 2);
            // an over-broad swamp biome both spreads marshes AND steals the lake/pond classification downstream.
            int swampBiomeCells = 0, landCells = 0;
            if (biomes != null)
                for (int i = 0; i < n * n; i++) { if (g[i] > SEA + 0.2f) { landCells++; if (biomes[i] == 2) swampBiomeCells++; } }
            // ── 1) MARSH CANDIDATES from swamp biome (their own suitability) ─────
            // ⚠ THIS SCAN NOW RUNS BEFORE THE LAKE SCAN, AND ONLY TO ANSWER ONE QUESTION EARLY (v133): which
            // wetland ground can the marsh funnel actually place a body on? The lake funnel below yields that
            // ground and NOTHING ELSE, so a wetland basin the marsh funnel cannot take stays a lake instead of
            // becoming a hole in the world. Nothing else moved: the two scans share no state but `stamp`
            // (scratch, keyed by an incrementing id) and both only APPEND to `candidates`, which is sorted
            // afterwards — so the decision order in the referee, and every lake/pond/tarn footprint, is
            // bit-identical to running them the other way round.
            var marshClaim = new bool[n * n];   // ground a marsh CANDIDATE covers — see the lake funnel's gate
            if (biomes != null)
            {
                var seenM = new bool[n * n];
                var stack = new Stack<int>();
                var comp = new List<int>(8192);
                var heights = new List<float>(8192);
                var holdable = new List<float>(8192);   // fill-table spill levels over the region's floodable cells
                for (int i0 = 0; i0 < n * n; i0++)
                {
                    if (seenM[i0] || biomes[i0] != 2) continue;
                    comp.Clear(); heights.Clear(); stack.Push(i0); seenM[i0] = true;
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop();
                        comp.Add(i); heights.Add(g[i]);
                        int x = i % n, z = i / n;
                        for (int e = 0; e < 4; e++)
                        {
                            int xx = x + (e == 0 ? 1 : e == 1 ? -1 : 0), zz = z + (e == 2 ? 1 : e == 3 ? -1 : 0);
                            if (xx < 1 || xx >= n - 1 || zz < 1 || zz >= n - 1) continue;
                            int j = zz * n + xx;
                            if (!seenM[j] && biomes[j] == 2) { seenM[j] = true; stack.Push(j); }
                        }
                    }
                    marshRegions++;   // a connected swamp-biome region (any size) — the marsh candidate pool
                    marshSizes.Add(comp.Count);
                    if (comp.Count < marshMinCells) { marshTooSmall++; continue; }
                    // ═══ THE LEVEL IS SPILL-BOUNDED, LIKE EVERY OTHER BODY (2026-08-13) ══════════════════
                    // This used to be a pure height PERCENTILE of the biome patch — a number with no relation
                    // to any lip, so the flood that follows had nothing to stop it and simply ran until it hit
                    // a cell budget (55% of qualifying regions overflowed and were discarded). Every other body
                    // in this planner takes `level = spill − LEVEL_UNDER_SPILL` from a real basin; marshes were
                    // the sole exception, and that is the actual reason they barely exist.
                    // Marshes cannot reuse the lake depression scan — BASIN_MIN_DEPTH is 2.5m specifically to
                    // reject noise dips, and a marsh basin is SHALLOWER than that by definition — so the lip is
                    // measured marsh-locally from the region's own boundary (rimMin above). Same law, own scan,
                    // and the lake/pond/tarn funnel is untouched.
                    // TWO bounds, take the lower:
                    //   • rim  — water cannot stand above its lowest lip. Containment by construction.
                    //   • pct  — and it must not drown the mounds: keep the top (1−PCT) of the region emergent,
                    //            which is what makes it "a large shallow lake with MANY ROLLING ISLANDS"
                    //            rather than one flat sheet with nothing standing out of it.
                    heights.Sort();
                    float pctLevel = heights[(int)(heights.Count * MARSH_SUBMERGE_PCT)] - 0.5f;
                    // THE LIP COMES FROM THE FILL TABLE, NOT FROM THE REGION'S EDGE. Measured 2026-08-13: using
                    // the region's own lowest boundary cell as a spill lip rejected ALL 31 qualifying regions.
                    // A swamp-biome region is NOT a basin — its boundary is wherever the biome gate stopped,
                    // which is frequently DOWNHILL of the region's floor, so "lowest boundary cell" is arbitrary
                    // terrain rather than a lip. (A lake's depression boundary is a rising lip by construction;
                    // a biome patch's is not. That was the bad assumption.)
                    // PriorityFloodFill already computed the real answer: fill[i] IS the level cell i's
                    // depression fills to before spilling. Cells with fill > g sit in something that can HOLD
                    // water; their median fill is this marsh's spill level. Shallow basins the lake scan throws
                    // away (BASIN_MIN_DEPTH = 2.5m rejects them as noise dips) are exactly what a marsh wants,
                    // which is why marshes need their own read of the same table rather than the lake funnel.
                    float fillLevel = float.MaxValue;
                    if (fill != null)
                    {
                        holdable.Clear();
                        for (int k = 0; k < comp.Count; k++)
                            if (fill[comp[k]] > g[comp[k]] + 0.05f) holdable.Add(fill[comp[k]]);
                        // too little of the region can hold anything ⇒ damp ground, not a marsh basin
                        if (holdable.Count < comp.Count * 0.15f) { marshBroken++; brkHoldable++; continue; }
                        holdable.Sort();
                        fillLevel = holdable[holdable.Count / 2] - LEVEL_UNDER_SPILL;
                    }
                    float level = System.Math.Min(pctLevel, fillLevel);
                    // ...and it must actually FLOOD. A level that leaves the region essentially dry would place
                    // a marsh the map paints and the game does not have — the exact failure the user named.
                    int under = 0;
                    for (int k = 0; k < heights.Count; k++) if (heights[k] < level) under++;
                    if (under < heights.Count * 0.25f) { marshBroken++; brkNoFlood++; continue; }
                    if (level < heights[0] + MARSH_MIN_SHEET) { marshBroken++; brkThinSheet++; continue; }
                    // suitability: the region must actually offer near-level country
                    int inBand = 0;
                    for (int k = 0; k < heights.Count; k++)
                        if (heights[k] >= level - 2.5f && heights[k] <= level + 1.5f) inBand++;
                    if (inBand < heights.Count * 0.30f) { marshBroken++; brkNotLevel++; continue; }   // broken ground → stays dry swampy land
                    // ═══ THE TIDE BAND IS FORBIDDEN — NO TWO SHEETS MAY MEET NEAR SEA LEVEL ══════════════
                    // (user 2026-08-13: "we need very specific and clean logic for swamps/marshes close to
                    // sea level to keep meshes from conflicting".) High tide is EXACTLY Y30 — FiresEnv's
                    // TideHighOffsetM is 0, so the ocean never rises above SEA; it only recedes (−2 neap,
                    // −5 spring). The failure mode is an inland marsh sheet sitting a few centimetres either
                    // side of the ocean plane: two coplanar surfaces, both drawn, fighting. So the band around
                    // SEA is made UNINHABITABLE for an inland sheet — a marsh is either
                    //   • OCEAN-OWNED (SeaMarsh): snapped to SEA, and NO inland sheet is emitted at all
                    //     (FiresWaterConform writes w only for Kind == Marsh), so there is exactly one surface;
                    //   • or fully INLAND, guaranteed ≥ MARSH_TIDE_BAND above the highest tide, so the two can
                    //     never be coplanar and the ocean can never reach it.
                    // Nothing is allowed to land between. Widened 0.6 → 1.2 for real clearance.
                    bool sea = level <= SEA + MARSH_TIDE_BAND;
                    if (sea) level = SEA;                            // the ocean plane owns sea-level marsh water
                    // marsh footprint = the level's TRUE coverage too — water ignores the
                    // biome line, so the sheet floods any adjacent ground under the level;
                    // a leak past budget lowers the level (smaller marsh beats a leak).
                    List<int> cells = null; bool overflow = true;
                    float mlvl = level;
                    // ── THE FOOTPRINT BUDGET IS ABSOLUTE, NOT A MULTIPLE OF THE SEED (2026-08-13) ─────────
                    // Was min(comp.Count*3 + 2000, BODY_MAX_CELLS). Two things wrong with keying the budget
                    // to the swamp patch: (a) the footprint is explicitly NOT the biome region — the comment
                    // above says water ignores the biome line and floods whatever sits under the level — so
                    // sizing the allowance by the seed contradicts the very thing being computed; (b) it caps
                    // a marsh at 3x its patch, which is the opposite of "a LARGE shallow lake". Measured: 17
                    // of the 31 regions that cleared the floor (55%) blew this budget and were DISCARDED.
                    // Now: a generous absolute allowance, still hard-capped by BODY_MAX_CELLS so a flood
                    // across coastal lowland can never become one map-swallowing sheet.
                    int budget = System.Math.Min(System.Math.Max(comp.Count * 8, marshBudgetFloor), bodyMaxCells);
                    // ...and an overflowing marsh SETTLES rather than dying. 3 tries x 0.5m gave 1.5m of range;
                    // on the near-flat ground a marsh sits on, 1.5m of level is nowhere near enough to pull a
                    // sprawling flood back inside any budget. A lower level is a smaller marsh, which is a
                    // perfectly good marsh — discarding it outright is what left the world with three.
                    for (int tries = 0; tries < MARSH_LEVEL_TRIES && overflow && !sea; tries++)
                    {
                        stampId++;
                        cells = ExpandFootprint(comp, mlvl, g, n, stamp, stampId, budget, out overflow);
                        if (!overflow) break;
                        mlvl -= MARSH_LEVEL_STEP;
                        // Never settle an INLAND marsh down onto the sea. `sea` was decided from the original
                        // level, so without this the widened retry range (now 6m, was 1.5m) could walk a marsh
                        // that started well above the tide down to or under it — and an inland sheet at sea
                        // level is exactly the thing the SeaMarsh split exists to prevent (the ocean owns that
                        // water). Stop here and let the overflow check below reject it as unplaceable.
                        if (mlvl <= SEA + 0.6f) break;
                    }
                    if (sea) { stampId++; cells = ExpandFootprint(comp, SEA + 1.5f, g, n, stamp, stampId, budget, out overflow); }
                    // 0.2.861 (Fire 10-08, SinkTestGen9: terraced sea floor + 50 m walls across the bay): a SEA marsh's overflowed
                    // flood was kept. ExpandFootprint is a depth-first flood that stops at its budget, so a cut-off flood is a set
                    // of grid COLUMNS; that striped mask (0101 by column) combed the sea floor in Conform, and the lakes that later
                    // filled the free stripes between its columns came out 1-3 cells wide and walled up by BuryMeshEdges. An
                    // overflowed flood is refused for both kinds now.
                    if (overflow) { marshOverflow++; continue; }
                    if (cells == null || cells.Count < marshMinCells / 2) { marshTooSmall++; continue; }
                    if (sea) candSeaMarsh++; else candMarsh++;
                    // conformance reshapes the interior; include the near-level band above
                    // the sheet so mound country joins the footprint
                    for (int k = 0; k < comp.Count; k++)
                        if (stamp[comp[k]] != stampId && g[comp[k]] <= mlvl + 1.5f) { stamp[comp[k]] = stampId; cells.Add(comp[k]); }
                    // ⚠ THE CLAIM IS THE WHOLE REGION, NOT THE FOOTPRINT. `cells` is only what sits UNDER the
                    // sheet; the ground standing above it is this marsh's hummocks and tree-islands, and it is
                    // part of the marsh. Claiming the footprint alone would leave those island tops open to
                    // the lake funnel — which, reading a brim-full fill table, would happily pool a lake on
                    // top of the mounds in the middle of a swamp.
                    for (int k = 0; k < comp.Count; k++) marshClaim[comp[k]] = true;
                    for (int k = 0; k < cells.Count; k++) marshClaim[cells[k]] = true;
                    candidates.Add((sea ? SEA : mlvl, sea ? WaterBodyKind.SeaMarsh : WaterBodyKind.Marsh, cells));
                }
            }

            if (fill != null)
            {
                var seen = new bool[n * n];
                var stack = new Stack<int>();
                var comp = new List<int>(4096);
                for (int i0 = 0; i0 < n * n; i0++)
                {
                    // ═══ WETLAND GROUND A MARSH WILL ACTUALLY TAKE IS NOT AVAILABLE TO A LAKE (v133) ════════
                    // A wetland basin is brim-full IN THE FILL TABLE like any other, because a priority flood
                    // has no evaporation term; only the classifier knows the basin cannot hold that much
                    // water. Without this gate the lake funnel gets there first, and since marshes decide LAST
                    // and need MARSH_FREE_FRAC of their cells unowned, every marsh over a real basin dies in
                    // the overlap referee — which is how v132's world came out with 22 marshes' worth of
                    // qualifying ground and one marsh on it.
                    // ⚠ BOTH TESTS, AND THE SECOND ONE IS THE IMPORTANT ONE. `wetland[]` alone was measured
                    // first and is WRONG: 229,965 of 400,945 wetland cells (57%) ended up owned by nothing at
                    // all — cold basins the biome pass rightly refuses to call swamp, plus every region that
                    // failed a marsh suitability gate. Taking a basin off the lake funnel and giving it to no
                    // one does not make its water shallow, it deletes it, and that is exactly the failure this
                    // whole change exists to undo. So the ground moves only when BOTH owners agree: hydrology
                    // says wetland AND the marsh funnel has actually produced a candidate covering it.
                    // Skipping cells rather than candidates keeps the rule where the fact is — on the ground —
                    // so a depression that merely TOUCHES a marsh still forms its lake from the rest.
                    if (wetland != null && wetland[i0] && marshClaim[i0]) continue;
                    if (seen[i0] || fill[i0] - g[i0] < BASIN_MIN_DEPTH) continue;
                    // flood one depression (cells the analysis says would hold ≥ the min depth)
                    comp.Clear(); stack.Push(i0); seen[i0] = true;
                    float spill = float.MaxValue;
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop();
                        comp.Add(i);
                        if (fill[i] < spill) spill = fill[i];      // the depression's lowest lip
                        int x = i % n, z = i / n;
                        for (int e = 0; e < 4; e++)
                        {
                            int xx = x + (e == 0 ? 1 : e == 1 ? -1 : 0), zz = z + (e == 2 ? 1 : e == 3 ? -1 : 0);
                            if (xx < 1 || xx >= n - 1 || zz < 1 || zz >= n - 1) continue;
                            int j = zz * n + xx;
                            if (wetland != null && wetland[j] && marshClaim[j]) continue;
                            if (!seen[j] && fill[j] - g[j] >= BASIN_MIN_DEPTH) { seen[j] = true; stack.Push(j); }
                        }
                    }
                    float level = spill - LEVEL_UNDER_SPILL;
                    if (level <= SEA + 0.6f) continue;             // near-sea pits belong to the ocean/estuary story
                    // the DECIDED footprint = the level's TRUE coverage (flood past the
                    // deep component over every shallow shelf under the level); if it
                    // leaks past its budget, the level comes DOWN — smaller beats leaking.
                    List<int> cells = null; bool overflow = true;
                    for (int tries = 0; tries < 3 && overflow; tries++)
                    {
                        stampId++;
                        int budget = System.Math.Min(comp.Count * 4 + 2000, bodyMaxCells);
                        cells = ExpandFootprint(comp, level, g, n, stamp, stampId, budget, out overflow);
                        if (overflow) level -= 0.5f;   // too big to be a lake here → lower it; near sea it drops out below
                    }
                    if (overflow || level <= SEA + 0.6f) continue;
                    if (cells.Count < bodyMinCells) continue;
                    lakeBasinsFound++;   // a real depression past the size floor — now run the lake-basin laws
                    // ═══ (v104) A BASIN MUST BE ABLE TO HOLD A LAKE (user: "there's no world where that
                    // mountain peak would have a depression with water in it"). The real massif terrain
                    // (v102) is full of ridge POCKETS — noise crevices between crests that pass the depth
                    // test but are steep rock funnels, not lake basins. Two laws close them out:
                    //   (a) FLOOR TEST — a real lake floor is NEAR-FLAT (deposition flattens it): the mean
                    //       grade over the deep floor must be gentle, else it's mountainside;
                    //   (b) ALTITUDE LADDER — the higher the basin, the more it must justify itself: small
                    //       high pockets have no catchment and shed/seep dry. Only genuine cirques and
                    //       caldera-scale bowls hold high water.
                    {
                        double gsum = 0; int gcnt = 0;
                        foreach (int ci in comp)
                        {
                            int cxq = ci % n, czq = ci / n;
                            if (cxq < 1 || cxq >= n - 1 || czq < 1 || czq >= n - 1) continue;
                            float gx = (g[ci + 1] - g[ci - 1]) * 0.5f / cell;
                            float gzv = (g[ci + n] - g[ci - n]) * 0.5f / cell;
                            gsum += Math.Sqrt(gx * gx + gzv * gzv); gcnt++;
                        }
                        float meanGrade = gcnt > 0 ? (float)(gsum / gcnt) : 0f;
                        // 0.18 refused 140 of 318 depressions on our terrain (44%) — that gate was
                        // cut against sandbox relief, and ours is gentler now that no river carve
                        // trenches it. A real lake floor can roll; only a hillside cannot hold water.
                        if (meanGrade > 0.30f) { rejSteep++; continue; }
                        // (c) HIGH STILL WATER IS NOT SCAVENGED FROM THE FILL AT ALL (user, emphatic — and
                        //     the admin-terrain mod PROVED the right way): mountain water comes from
                        //     SeedMountainTarns — deliberate rare cirque sites that verify a NATURAL hollow
                        //     (floor below the lowest rim) and EXCAVATE their own parabolic basin, filling
                        //     only the bowl. The ridged massif's noise pockets pass any depth filter you
                        //     write; the answer is a different SYSTEM, not a stricter filter. The planner's
                        //     fill-basin candidates are LOWLAND-ONLY; SeedMountainTarns (wired into the gen
                        //     path) owns everything above.
                        if (level > 150f) { rejHigh++; continue; }
                    }
                    // classify: mountain/frostreach ground → tarn; else by size
                    int mid = cells[cells.Count / 2];
                    byte bio = biomes != null ? biomes[mid] : (byte)0;
                    WaterBodyKind kind = (bio == 3 || bio == 7) ? WaterBodyKind.Tarn
                                       : cells.Count >= lakeMinCells ? WaterBodyKind.Lake : WaterBodyKind.Pond;
                    if (kind == WaterBodyKind.Tarn) candTarn++; else if (kind == WaterBodyKind.Lake) candLake++; else candPond++;
                    candidates.Add((level, kind, cells));
                }
            }

            // ── 3) DECIDE, highest to lowest — first decision wins the ground ────
            // ═══ MARSHES DECIDE LAST — THEY MAY NEVER TAKE GROUND FROM A DISCRETE BODY (2026-08-13) ══════
            // (user: "i cannot afford for you to break the other working water logic while trying to fix
            // marshes and swamps".) `owner` is SHARED by every body kind, so anything that makes marshes claim
            // more ground silently takes it from lakes/ponds/tarns — measured: widening the marsh footprint
            // budget trimmed lakes into swiss cheese and took lowland float from 46 to 907. Ordering purely by
            // level let a high marsh outrank a lake beneath it. Marshes are broad, shallow, low-lying districts
            // that legitimately wrap around other water, so they are now decided AFTER every discrete body
            // regardless of level: they fill in whatever ground is left. Lake/pond/tarn selection, ordering and
            // footprints are then bit-identical to before this change — the marsh work cannot regress them.
            candidates.Sort((a, b) =>
            {
                bool am = a.kind == WaterBodyKind.Marsh || a.kind == WaterBodyKind.SeaMarsh;
                bool bm = b.kind == WaterBodyKind.Marsh || b.kind == WaterBodyKind.SeaMarsh;
                if (am != bm) return am ? 1 : -1;              // marshes to the back of the queue
                return b.level.CompareTo(a.level);             // within a group: highest first, as before
            });
            foreach (var cand in candidates)
            {
                // overlap referee: a cell already owned by a higher body is not available
                int free = 0;
                for (int k = 0; k < cand.cells.Count; k++)
                    if (owner[cand.cells[k]] == 0) free++;
                // A marsh keeps its ground around inclusions (see MARSH_FREE_FRAC); discrete bodies still
                // demand 60% exclusivity. The mask build below already skips owned cells, so a surviving
                // ═══ THE OCEAN OWNS SEA LEVEL. A POND THERE DOES NOT EXIST. ══════════════════════════════
                // (user, 2026-09, standing on a coast with the overlay reading "Pond · surf Y30.4" beside the
                // tidal sea: "for some reason were drawing ponds and other still water bodies at sea level,
                // where the ocean should own all logic and ponds should never exist".)
                // A still body within the tide band is not a body — it is a patch of sea that the basin scan
                // mistook for an inland depression, and emitting it puts a SECOND sheet at the coast that
                // fights the tidal plane (the hard sheet corner in the screenshot). The tide runs SEA-5 to
                // SEA, so anything whose surface would sit at or barely above SEA is tidal ground: refused
                // here, before a footprint or a carve ever exists, and the ocean plane simply covers it.
                // Sea marshes are exempt — coastal-at-sea-level is their entire definition.
                if (cand.kind != WaterBodyKind.Marsh && cand.kind != WaterBodyKind.SeaMarsh
                    && cand.level <= SEA + 0.75f) { oceanOwned++; continue; }
                // marsh is TRIMMED around whatever sits inside it rather than overlapping it.
                bool isMarsh = cand.kind == WaterBodyKind.Marsh || cand.kind == WaterBodyKind.SeaMarsh;
                float freeFrac = isMarsh ? MARSH_FREE_FRAC : 0.6f;
                if (free < cand.cells.Count * freeFrac || free < bodyMinCells) { overlapDrop++; continue; }   // a higher body already owns this ground

                int gx0 = int.MaxValue, gz0 = int.MaxValue, gx1 = int.MinValue, gz1 = int.MinValue;
                for (int k = 0; k < cand.cells.Count; k++)
                {
                    int i = cand.cells[k];
                    if (owner[i] != 0) continue;
                    int x = i % n, z = i / n;
                    if (x < gx0) gx0 = x; if (x > gx1) gx1 = x;
                    if (z < gz0) gz0 = z; if (z > gz1) gz1 = z;
                }
                var body = new WaterBody
                {
                    Id = plan.Bodies.Count,
                    Kind = cand.kind,
                    Level = cand.level,
                    Gx0 = gx0, Gz0 = gz0, W = gx1 - gx0 + 1, H = gz1 - gz0 + 1,
                };
                body.Mask = new byte[body.W * body.H];
                for (int k = 0; k < cand.cells.Count; k++)
                {
                    int i = cand.cells[k];
                    if (owner[i] != 0) continue;
                    owner[i] = body.Id + 1;
                    int x = i % n, z = i / n;
                    body.Mask[(z - gz0) * body.W + (x - gx0)] = 1;
                }
                body.MinX = gx0 * cell - half; body.MaxX = gx1 * cell - half;
                body.MinZ = gz0 * cell - half; body.MaxZ = gz1 * cell - half;
                plan.Bodies.Add(body);
                switch (cand.kind)
                {
                    case WaterBodyKind.Lake: placedLake++; break;
                    case WaterBodyKind.Pond: placedPond++; break;
                    case WaterBodyKind.Tarn: placedTarn++; break;
                    case WaterBodyKind.Marsh: placedMarsh++; break;
                    case WaterBodyKind.SeaMarsh: placedSeaMarsh++; break;
                }
            }

            // (Investigation note, 2026-08-11 — the diagnostic itself is removed, it allocated a full n² array
            // on every gen.) A body is only detected by the connection loop below when a traced poly NODE lands
            // inside its FOOTPRINT. Measured at n=10501: only ~26 of 562 bodies had a node inside; ~36 had a
            // trace pass within 10-120m and register as nothing; the rest had no trace near them at all and
            // rely on the derived outlet. That asymmetry is why "rivers stop short" was a footprint-vs-drainage
            // question, not a routing one.

            // ── 4) CONNECTIONS — every traced flow path becomes river runs between
            //      bodies. Geometry + catchment come from the analysis; the LEVELS
            //      come from the PLAN: exactly the upstream body's constant down to
            //      exactly the downstream body's constant (or the estuary floor),
            //      monotone, weighted by the terrain's own descent along the path.
            if (polys != null)
            {
                float WK = 0.042f, WP = 0.36f;
                float WMIN = 2f, WMAX = 20f;
                // statics so the summary below can report them from outside this block
                // ONE INLET PER LANDING (user: "a body of water should be limited to one inlet and one outlet
                // when a river meets a stillbody within 10m"). Records, per body, the shore points already
                // taken by an arriving run; a later run landing within INLET_MERGE_M of one of them is a
                // duplicate mouth on the same spot, and duplicates are what build the knot.
                var inlets = new Dictionary<int, List<(float x, float z)>>();
                var depTried = new bool[n * n];   // basins that failed emergent-pond creation — never re-flooded
                // ═══ EVERY RUN MUST START SOMEWHERE AND END SOMEWHERE ════════════════════════════════════
                // (user, 2026-08-22: "ideally we make sure all runs start somewhere and end somewhere, if
                // they have no spring, pond, lake, or another river to start from or run into they should not
                // exist".)
                // The destination is not actually unknown — it was known and thrown away. TraceRivers only
                // ACCEPTS a mouth where `intoSea || intoBody`, so every trace in `polys` provably drains into
                // real water; but it marks the mouth as the cell whose DOWNSTREAM NEIGHBOUR is that water, so
                // the mouth node itself is always one cell OUTSIDE it. The segmentation below therefore never
                // sees `inBody` at the mouth, falls through to `atEnd`, and stamps OCEAN on it.
                // MEASURED, seed 126369233 n=10501: 4,337 runs labelled tail=OCEAN and only 800 of 5,270 tied
                // to real water at both ends. EmitConnection then hands every one of those a destination
                // level of SEA — for runs that end inland at altitude — and the monotone profile is free to
                // plunge toward water it never reaches. That is the fabricated endpoint underneath the
                // drop-then-rise, and no amount of shaping downstream can recover from it.
                // So resolve the sink from what is actually there, in this order: the sea it reached, the
                // body it landed beside, or the RIVER it joins — a confluence is a real destination and is
                // how a tributary ends. `channelOwner` maps every traced channel cell to its trace index so
                // the third test is a lookup rather than a search. A run that satisfies none of the three has
                // no destination at all and is not emitted.
                var channelOwner = new Dictionary<int, int>(1 << 16);
                {
                    int pi = 0;
                    foreach (var cp in polys)
                    {
                        int cn = cp.Length / 4;
                        for (int k = 0; k < cn; k++)
                        {
                            int qx = Mathf.Clamp(Mathf.RoundToInt((cp[k * 4] + half) / cell), 0, n - 1);
                            int qz = Mathf.Clamp(Mathf.RoundToInt((cp[k * 4 + 1] + half) / cell), 0, n - 1);
                            channelOwner[qz * n + qx] = pi;
                        }
                        pi++;
                    }
                }
                float seaY = OceanLevel(plan);
                int polyIndex = -1;
                foreach (var rawPoly in polys)
                {
                    polyIndex++;
                    int count = rawPoly.Length / 4;
                    if (count < 2) continue;
                    // ═══ WALK THE TRACE IN FLOW ORDER — IT ARRIVES MOUTH-FIRST ═══════════════════════════
                    // (2026-08-19.) THE defect this whole file's river levels rest on, and it is an ordering
                    // one. `TraceRivers` collects MOUTHS and then walks UPSTREAM appending as it goes, so a
                    // trace array is ordered mouth → source. FiresWorldGenBase's own ROUTE VALIDATION knows
                    // this — it computes `srcAtStart = area[0] <= area[last]` and its loop is commented "walk
                    // mouth → source". This loop did not: it took `fromBody` from the bodies it met at LOW
                    // indices and `toBody` at HIGH indices, i.e. it read the MOUTH as the source and the
                    // SOURCE as the destination. Every connection came out reversed.
                    // What that cost, all measured on seed 126369233 at n=10501:
                    //   • EmitConnection descends lStart → lEnd across indices 0..count-1, so the planned
                    //     surface descended FROM THE MOUTH UP TO THE SOURCE, and `lEnd = OceanLevel` (30 m)
                    //     was assigned to the river's HEAD in the hills. Restoring those levels raw dried
                    //     70 % of the network (293,462 → 88,687 wet cells) because the surface sat below the
                    //     bed everywhere upstream — that experiment was measuring this bug, not the idea.
                    //   • RUN PROVENANCE: 5,317 of 6,401 runs (83 %) came out SPRING→OCEAN, tied to real
                    //     water at NEITHER end, because the body each run actually leaves and enters was
                    //     being looked for at the wrong end. 5,800 runs claimed an ocean mouth on a coastline
                    //     that receives nothing like that many.
                    //   • BASE-LEVEL ANCHOR: 81.1 % of runs had zero anchored nodes, so the anchor, the
                    //     junction taper and the backwater lift were all dead code, and SetBankDerivedLevels
                    //     invented each run's surface from two local bank samples — which is the multi-level
                    //     sheet (riverfield N. LEVEL SPREAD: 52.7 % of river cells within 10 m of another
                    //     river surface more than a metre off).
                    // Normalise here rather than at the twenty places downstream that consume a connection:
                    // area grows monotonically downstream by construction, so it is the direction witness.
                    // Nothing outside the plan is affected — InciseRivers receives the PLAN's polys, and
                    // SetBankDerivedLevels re-derives `srcAtStart` for itself either way.
                    float[] poly = rawPoly;
                    if (rawPoly[2] > rawPoly[(count - 1) * 4 + 2])          // area[0] > area[last] ⇒ mouth-first
                    {
                        poly = new float[rawPoly.Length];
                        for (int k = 0; k < count; k++)
                        {
                            int s = (count - 1 - k) * 4, d = k * 4;
                            poly[d] = rawPoly[s]; poly[d + 1] = rawPoly[s + 1];
                            poly[d + 2] = rawPoly[s + 2]; poly[d + 3] = rawPoly[s + 3];
                        }
                        s_tracesReversed++;
                    }
                    int runStart = -1; int fromBody = WaterConnection.SPRING;
                    for (int i = 0; i <= count; i++)
                    {
                        int bodyHere = -1; bool inDep = false; float depFill = 0f;
                        if (i < count)
                        {
                            int gx = Mathf.Clamp(Mathf.RoundToInt((poly[i * 4] + half) / cell), 0, n - 1);
                            int gz = Mathf.Clamp(Mathf.RoundToInt((poly[i * 4 + 1] + half) / cell), 0, n - 1);
                            int gi = gz * n + gx;
                            bodyHere = owner[gi] - 1;
                            // ═══ THE SOURCE OF EVERY AQUEDUCT: FLOW IS ROUTED ON THE FILLED SURFACE ══════
                            // (user, 2026-09-01: "fixing the source means runs should never even be proposed
                            // in areas they shouldnt be, were trying to mimic real world water here".)
                            // Drainage analysis fills every depression to its spill so flow always has a path
                            // to the sea — standard hydrology, and correct: real water DOES cross a basin,
                            // AS A LAKE. Where the planner placed a body in that basin, this walk already
                            // splits the run at its shore and everything is right. Where NO body was placed
                            // (the overlap referee dropped it, or the tide rule gave it to the ocean), the
                            // trace kept gliding across the DRY basin at spill height — and the berm then
                            // walled that water across the valley: the photographed aqueduct. A route is
                            // only real where the water can actually BE, so the walk now knows about
                            // bodyless depressions and treats them as what they are: the place this water
                            // ENDS.
                            if (bodyHere < 0 && fill != null && fill[gi] > g[gi] + 1.0f)
                            {
                                if (fill[gi] <= SEA + 0.75f) { inDep = true; depFill = fill[gi]; }   // tidal — the sea's
                                else
                                {
                                    // ═══ THE RIVER MAKES ITS POND (2026-09-02) ═══════════════════════════
                                    // GEN 188 terminated runs here and spring-culled their continuations —
                                    // half of real hydrology, and the user's waterat proved the cost: dry
                                    // corridors mid-run and past lake outlets, with the buried tail of the
                                    // dead run at the stretch's edge. A basin that RECEIVES a river fills to
                                    // its spill and the river CONTINUES: create the pond (footprint = the
                                    // pond rule's own flood, level = fill height minus the standard
                                    // freeboard) and let the walk treat it like any body — run ends AT it,
                                    // continuation resumes FROM it. Oversize/undersize floods abort and the
                                    // old terminal-BASIN behaviour stands for genuine sinks.
                                    int nb = TryCreateEmergentPond(plan, owner, g, fill, gi, n, cell, half, depTried);
                                    if (nb >= 0) bodyHere = nb;
                                    else { inDep = true; depFill = fill[gi]; }
                                }
                            }
                        }
                        bool inBody = bodyHere >= 0;
                        bool atEnd = i == count;
                        if (runStart < 0)
                        {
                            // A run may not BEGIN inside a bodyless depression either — water there is a
                            // pond that was refused, not a river head. It may begin again past the far rim,
                            // spring-headed, and the spring-cull rules judge it like any other head.
                            if (!inBody && !atEnd && !inDep) { runStart = i; }
                            else if (inBody) fromBody = bodyHere;             // passing through a body updates the upstream
                            continue;
                        }
                        if (inBody || atEnd || inDep)
                        {
                            int runEnd = i;                                    // exclusive
                            int toBody = inBody ? bodyHere
                                       : inDep ? (depFill <= SEA + 0.75f ? WaterConnection.OCEAN : WaterConnection.BASIN)
                                       : WaterConnection.OCEAN;
                            // A TIDAL basin IS the sea: the run ends into the ocean, properly, at the coast
                            // (this is the coastal channel mess in the user's screenshots — routes were
                            // crossing tide flats the ocean owns). An INLAND bodyless basin is a terminal
                            // sink: the run pours in and ENDS — a fall into the valley is a descending step
                            // and is wanted; carrying the water across on walls is not. The continuation of
                            // the trace past the basin gets NO inherited source (fromBody resets to SPRING):
                            // it must justify itself as a spring head or be culled like any other.
                            // ═══ A TRACE STOPS ONE CELL SHORT OF THE WATER IT DRAINS INTO ═══════════════
                            // (2026-08-19, user: "they should not be stopping at nodes outside the body".)
                            // TraceRivers marks a MOUTH as the cell whose DOWNSTREAM NEIGHBOUR is sea or a
                            // body — so the mouth node itself is ADJACENT to the receiving water, never
                            // inside it. Walking in flow order the trace therefore runs out one cell short,
                            // this loop reaches `atEnd` instead of `inBody`, and the run is labelled OCEAN
                            // even where it is visibly draining into a lake.
                            // The cost is total: OCEAN is −2 and both FindSeededNear anchor calls in
                            // PlanConnectionsToPolys are gated on `>= 0`, so the run gets no junction node,
                            // `anchored[k]` never fires in SetBankDerivedLevels, and the base-level anchor,
                            // the junction taper and the backwater lift are all dead code for it. Measured
                            // before this: 5,317 of 6,401 runs (83 %) tied to real water at NEITHER end, and
                            // 5,800 claiming an ocean mouth on a coastline that receives nothing like that
                            // many. Those runs then invent their own surface from two local bank samples,
                            // which is the multi-level sheet.
                            // So ask the ground what this run actually arrives at: look for a body owning any
                            // cell within SHORE_ARRIVE_M of the final node. Finding one makes the run a real
                            // body→body connection, and EmitConnection lands it EXACTLY on that body's
                            // constant. Finding none leaves it OCEAN, which is then a genuine sea mouth (the
                            // ocean has no footprint in `owner` by construction — it is a zero-mask body).
                            if (atEnd && runEnd - runStart >= 2)
                            {
                                int lastN = runEnd - 1;
                                int ex = Mathf.Clamp(Mathf.RoundToInt((poly[lastN * 4] + half) / cell), 0, n - 1);
                                int ez = Mathf.Clamp(Mathf.RoundToInt((poly[lastN * 4 + 1] + half) / cell), 0, n - 1);
                                int erad = Mathf.Max(1, Mathf.RoundToInt(SHORE_ARRIVE_M / cell));
                                int found = -1;
                                for (int r = 1; r <= erad && found < 0; r++)
                                    for (int dz = -r; dz <= r && found < 0; dz++)
                                        for (int dx = -r; dx <= r; dx++)
                                        {
                                            if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;   // ring only
                                            int qx = ex + dx, qz = ez + dz;
                                            if (qx < 0 || qx >= n || qz < 0 || qz >= n) continue;
                                            int o = owner[qz * n + qx] - 1;
                                            if (o >= 0) { found = o; break; }
                                        }
                                if (found >= 0) { toBody = found; s_endsReconnected++; }
                            // ═══ AND IF IT FOUND NO BODY, IT IS STILL NOT AUTOMATICALLY AN OCEAN MOUTH ═══
                            // `toBody = OCEAN` above is a FALLBACK, not a finding, and EmitConnection turns it
                            // into a hard promise: `lEnd = OceanLevel(plan)`. A run that ends inland at 300 m
                            // is then given SEA as its destination and descends toward it monotonically —
                            // fabricating a plunge to water it never meets. Ask the three real questions
                            // instead (user: "if they have no spring, pond, lake, or another river to start
                            // from or run into they should not exist"):
                            //   1. did it REACH the sea?  — its mouth ground is at or under the ocean surface.
                            //   2. did it land beside a BODY? — the owner search above already answered that.
                            //   3. does it join another RIVER? — its mouth sits on a cell owned by a different
                            //      trace. That is a confluence, and it is how most tributaries end; the level
                            //      it must meet is that trunk's, which the junction machinery downstream
                            //      already solves once the run is tied to it.
                            // None of the three ⇒ no destination ⇒ the run is not emitted.
                            if (atEnd && toBody == WaterConnection.OCEAN)
                            {
                                int lastN3 = runEnd - 1;
                                int mx3 = Mathf.Clamp(Mathf.RoundToInt((poly[lastN3 * 4] + half) / cell), 0, n - 1);
                                int mz3 = Mathf.Clamp(Mathf.RoundToInt((poly[lastN3 * 4 + 1] + half) / cell), 0, n - 1);
                                if (g[mz3 * n + mx3] <= seaY + 0.5f) s_sinkSea++;             // 1. a genuine sea mouth
                                else
                                {
                                    bool joins = false;                                        // 3. a confluence
                                    int jr = Mathf.Max(1, Mathf.RoundToInt(SHORE_ARRIVE_M / cell));
                                    for (int dz = -jr; dz <= jr && !joins; dz++)
                                        for (int dx = -jr; dx <= jr; dx++)
                                        {
                                            int qx = mx3 + dx, qz = mz3 + dz;
                                            if (qx < 0 || qx >= n || qz < 0 || qz >= n) continue;
                                            if (channelOwner.TryGetValue(qz * n + qx, out int op) && op != polyIndex) { joins = true; break; }
                                        }
                                    if (joins) { toBody = WaterConnection.CONFLUENCE; s_sinkJoin++; }
                                    else { s_sinkNone++; runStart = -1; continue; }            // no destination — do not emit
                                }
                            }
                            }
                            // ═══ A RIVER MEETS A LAKE HEAD-ON, IT DOES NOT RUN ROUND THE RIM ═════════════
                            // (2026-08-16, user: "rivers should approach a stillbody at a sharp angle to
                            // prevent running a river along side the edge of a lake".) A run is cut where its
                            // nodes first land INSIDE the footprint, so a trace that grazes the shore and
                            // continues along it keeps every one of those shore-parallel nodes — and they get
                            // rasterized, carved and bermed as channel. On a render that is the cyan thread
                            // seen wrapping a pond's rim, and where several runs do it at once it is the knot
                            // the user pointed at.
                            // Physically, a tributary joins still water at its SHORE: once the channel is
                            // within a channel-width or so of the body it has arrived, and anything beyond
                            // that is the trace following a contour, not a river. So the run is truncated at
                            // the FIRST node that comes within SHORE_ARRIVE_M of this body — the approach
                            // becomes a short direct entry and the shore-parallel tail is dropped.
                            // ⚠ TRUNCATE ONLY — no node is moved and no run is deleted. v113 reverted a pass
                            // that SNAPPED loose river ends to the nearest body and it broke this exact area
                            // twice over: it chose the geometrically nearest body rather than the one the
                            // river drains to (carving out of a lake and back into the same lake), and it
                            // anchored the end at the body's LEVEL, lifting the ribbon above its own banks =
                            // floating rivers. Cutting a run shorter can do neither: the destination is
                            // unchanged, the level is unchanged, and the junction machinery downstream
                            // (FindSeededNear anchor, taper, backwater curve) still sees a run ending at the
                            // same body, just sooner.
                            if (inBody && runEnd - runStart > 2)
                            {
                                int rad = Mathf.Max(1, Mathf.RoundToInt(SHORE_ARRIVE_M / cell));
                                int cut = -1;
                                for (int k = runStart; k < runEnd && cut < 0; k++)
                                {
                                    int kx = Mathf.Clamp(Mathf.RoundToInt((poly[k * 4] + half) / cell), 0, n - 1);
                                    int kz = Mathf.Clamp(Mathf.RoundToInt((poly[k * 4 + 1] + half) / cell), 0, n - 1);
                                    for (int dz = -rad; dz <= rad && cut < 0; dz++)
                                        for (int dx = -rad; dx <= rad; dx++)
                                        {
                                            int qx = kx + dx, qz = kz + dz;
                                            if (qx < 0 || qx >= n || qz < 0 || qz >= n) continue;
                                            if (owner[qz * n + qx] - 1 == bodyHere) { cut = k; break; }
                                        }
                                }
                                // Keep at least two nodes so the run still has a direction to arrive on.
                                if (cut >= runStart + 2 && cut + 1 < runEnd) { s_shoreTrimmed += runEnd - (cut + 1); runEnd = cut + 1; }
                            }
                            // ⚠ MERGE DUPLICATE MOUTHS, DO NOT DELETE RIVERS. The tempting reading of "one
                            // inlet" is to keep the biggest arriving run and cull the others — which would
                            // STRAND their catchments, deleting real drainage to tidy a picture. What is
                            // actually wrong is several runs landing on the SAME few metres of shore, each
                            // rasterized and carved separately: a braid of parallel threads at one mouth.
                            // So a run whose landing point is within INLET_MERGE_M of a mouth this body has
                            // already been given is trimmed back to end at the shared mouth instead of adding
                            // its own, and its water still arrives — through the same junction, which is what
                            // a confluence at a lake head actually looks like.
                            if (inBody && runEnd - runStart > 2)
                            {
                                float lx = poly[(runEnd - 1) * 4], lz = poly[(runEnd - 1) * 4 + 1];
                                if (!inlets.TryGetValue(bodyHere, out var mouths)) { mouths = new List<(float, float)>(); inlets[bodyHere] = mouths; }
                                int hit = -1;
                                for (int m = 0; m < mouths.Count && hit < 0; m++)
                                {
                                    float dx = mouths[m].x - lx, dz = mouths[m].z - lz;
                                    if (dx * dx + dz * dz <= INLET_MERGE_M * INLET_MERGE_M) hit = m;
                                }
                                if (hit >= 0)
                                {
                                    // End this run where it first reaches the mouth that is already there, so
                                    // the two become one channel arriving at one point instead of a braid.
                                    float mx = mouths[hit].x, mz = mouths[hit].z;
                                    int cut2 = -1;
                                    for (int k = runStart; k < runEnd && cut2 < 0; k++)
                                    {
                                        float dx = poly[k * 4] - mx, dz = poly[k * 4 + 1] - mz;
                                        if (dx * dx + dz * dz <= INLET_MERGE_M * INLET_MERGE_M) cut2 = k;
                                    }
                                    if (cut2 >= runStart + 2 && cut2 + 1 < runEnd) { s_inletMerged++; runEnd = cut2 + 1; }
                                }
                                else mouths.Add((lx, lz));
                            }
                            // ═══ A RUN ENDS AT THE WATER, NOT TWELVE METRES SHORT OF IT ══════════════════
                            // (user, 2026-08-22: "the last node shouldnt fucking sit 12m out tho".)
                            // The shore-parallel trim above cuts the run at the FIRST node that comes WITHIN
                            // SHORE_ARRIVE_M of the body. That is the right cut for killing the rim-hugging
                            // tail and the wrong ENDING: the node it cuts at can be the full 12 m out, so a
                            // trimmed run stops on dry shore. Two things break because of it, and they are
                            // the two this level solve has spent its whole life fighting:
                            //   • EmitConnection lands lEnd — the receiving body's own surface — on a node
                            //     that is not AT the body. The level is right; the place is not. The last
                            //     12 m of channel gets rasterized and carved with nothing tying it to the
                            //     water it enters.
                            //   • SetBankDerivedLevels' base-level anchor asks `seeded > hOrig` AT THE NODE,
                            //     so it cannot fire. MEASURED at n=10501 seed 126369233: 4,252 of 5,270 runs
                            //     (80.7 %) anchored NOWHERE and fell back to inventing their surface from two
                            //     ground samples — the local guess this file then spends ten passes shaping.
                            // So finish the job the trim starts. After every trim, append ONE node at the
                            // nearest FOOTPRINT cell to the run's new last node: the tail is still dropped,
                            // the approach is still head-on and direct, and the run now terminates ON the
                            // water, at the water's level, where the anchor can see it. The carve does not
                            // trench the lake — InciseRivers already skips any cell a body owns.
                            float endX = float.NaN, endZ = float.NaN;
                            if (toBody >= 0 && runEnd > runStart)
                            {
                                int lx2 = Mathf.Clamp(Mathf.RoundToInt((poly[(runEnd - 1) * 4] + half) / cell), 0, n - 1);
                                int lz2 = Mathf.Clamp(Mathf.RoundToInt((poly[(runEnd - 1) * 4 + 1] + half) / cell), 0, n - 1);
                                int erad2 = Mathf.Max(1, Mathf.RoundToInt((SHORE_ARRIVE_M * 2f) / cell));
                                int bestD = int.MaxValue;
                                for (int dz = -erad2; dz <= erad2; dz++)
                                    for (int dx = -erad2; dx <= erad2; dx++)
                                    {
                                        int d2 = dx * dx + dz * dz;
                                        if (d2 >= bestD) continue;
                                        int qx = lx2 + dx, qz = lz2 + dz;
                                        if (qx < 0 || qx >= n || qz < 0 || qz >= n) continue;
                                        if (owner[qz * n + qx] - 1 != toBody) continue;
                                        bestD = d2; endX = qx * cell - half; endZ = qz * cell - half;
                                    }
                                if (bestD < int.MaxValue) s_endsLanded++;
                            }
                            EmitConnection(plan, poly, runStart, runEnd, fromBody, toBody,
                                           g, n, half, cell, WK, WP, WMIN, WMAX, endX, endZ);
                            runStart = -1;
                            if (inDep) fromBody = WaterConnection.SPRING;   // past a bodyless basin the trace has NO inherited source — the continuation must justify itself as a spring or be culled
                            if (inBody) fromBody = bodyHere;
                        }
                    }
                }
            }

            // ── CONNECT INFLOWS AT THEIR SPILL BASIN (user: "find the spot it would spill over, carve a bank,
            // connect the river"). The outlet guarantee already spills every BODY out at its saddle; this is the
            // mirror for the INFLOW side. Traces reach a body's basin but stop in the shore band the lowered
            // footprint (Level = spill − 0.85) leaves dry — zero connection ends land in a footprint, ~690 land
            // in a basin. The FILL TABLE is the spill map: it is exactly flat (= the spill) across a body's whole
            // depression and DROPS past the lip, so flooding `fill == spill` gives the real basin and cannot
            // escape it (no downhill leak). Label each basin + a BFS parent toward the footprint, then walk any
            // stranded river end down that in-basin path INTO the footprint and connect. Downhill, in-basin ⇒ the
            // receiving body is genuinely downstream, so the existing carve+bank+anchor connect it with no lift
            // and no float (AuditWater-verified). Marshes excluded (a river stops at the marsh mouth).
            if (fill != null && owner != null && plan.Bodies.Count > 0)
            {
                var basin = new int[n * n];          // 0 = none, else bodyId+1
                var bparent = new int[n * n];        // BFS parent toward the footprint (−1 = footprint root)
                for (int i = 0; i < bparent.Length; i++) bparent[i] = -1;
                var bq = new Queue<int>();
                for (int bi = 0; bi < plan.Bodies.Count; bi++)
                {
                    var b = plan.Bodies[bi];
                    if (b.Kind == WaterBodyKind.Marsh || b.Kind == WaterBodyKind.SeaMarsh) continue;
                    float spill = float.MinValue;
                    for (int lz = 0; lz < b.H; lz++)
                        for (int lx = 0; lx < b.W; lx++)
                        {
                            if (b.Mask[lz * b.W + lx] == 0) continue;
                            int i = (b.Gz0 + lz) * n + (b.Gx0 + lx);
                            if (fill[i] > spill) spill = fill[i];
                            if (basin[i] == 0) { basin[i] = bi + 1; bq.Enqueue(i); }   // footprint root, parent −1
                        }
                    if (spill == float.MinValue) { bq.Clear(); continue; }
                    float lip = spill - 0.05f;       // flat basin only (fill==spill); past the lip fill drops → excluded, can't leave the depression
                    while (bq.Count > 0)
                    {
                        int i = bq.Dequeue(); int cx = i % n, cz = i / n;
                        for (int e = 0; e < 4; e++)
                        {
                            int nx = cx + (e == 0 ? 1 : e == 1 ? -1 : 0), nz = cz + (e == 2 ? 1 : e == 3 ? -1 : 0);
                            if (nx < 1 || nx >= n - 1 || nz < 1 || nz >= n - 1) continue;
                            int j = nz * n + nx;
                            if (basin[j] != 0 || fill[j] < lip) continue;
                            basin[j] = bi + 1; bparent[j] = i; bq.Enqueue(j);
                        }
                    }
                    bq.Clear();
                }
                int inflowsConnected = 0;
                foreach (var c in plan.Connections)
                {
                    int nn = c.Nodes.Length / 4;
                    for (int side = 0; side < 2; side++)
                    {
                        bool head = side == 0;
                        if ((head ? c.FromBody : c.ToBody) >= 0) continue;        // already lands in a body
                        int k = head ? 0 : nn - 1;
                        int ex = Mathf.Clamp(Mathf.RoundToInt((c.Nodes[k * 4] + half) / cell), 0, n - 1);
                        int ez = Mathf.Clamp(Mathf.RoundToInt((c.Nodes[k * 4 + 1] + half) / cell), 0, n - 1);
                        int start = ez * n + ex;
                        if (owner[start] != 0) continue;
                        int B = basin[start] - 1;
                        if (B < 0) continue;                                      // not in any basin — true headwater/divide (springs, deferred)
                        // walk the parents DOWN to (but not INTO) the footprint: the last node is the shore-band
                        // cell touching the body. We deliberately stop at the edge and DON'T set From/ToBody —
                        // entering a seeded footprint cell (or labelling the body) triggers SetBankDerivedLevels'
                        // anchor + backwater-lift, which pulls the shore approach UP to the body level over the
                        // dry shore = float. Ending at the edge, the natural bank-derived level (≈Level here,
                        // banks≈spill) meets the body with no lift; the carve digs the channel across to it.
                        var path = new List<int>(32);
                        int cur = start; bool reachedEdge = false;
                        while (path.Count <= 90)
                        {
                            int p = bparent[cur];
                            if (p < 0) break;
                            if (owner[p] == B + 1) { reachedEdge = true; break; }   // next cell is the footprint — stop at the shore edge
                            cur = p; path.Add(cur);
                        }
                        int add = path.Count;
                        if (!reachedEdge || add == 0) continue;                     // never reached the body edge
                        float ewid = c.Nodes[k * 4 + 2];
                        var nw = new float[c.Nodes.Length + add * 4];
                        void PNode(float[] dst, int at, int ci)
                        { dst[at] = (ci % n) * cell - half; dst[at + 1] = (ci / n) * cell - half; dst[at + 2] = ewid; dst[at + 3] = 0f; }
                        if (head)
                        {
                            for (int p = 0; p < add; p++) PNode(nw, p * 4, path[add - 1 - p]);   // shore edge first
                            Array.Copy(c.Nodes, 0, nw, add * 4, c.Nodes.Length);
                        }
                        else
                        {
                            Array.Copy(c.Nodes, 0, nw, 0, c.Nodes.Length);
                            for (int p = 0; p < add; p++) PNode(nw, c.Nodes.Length + p * 4, path[p]);   // shore edge last
                        }
                        // NOTE: From/ToBody deliberately left as-is (SPRING/OCEAN) — labelling would re-anchor via
                        // PlanConnectionsToPolys.FindSeededNear and reintroduce the lift. The extended channel
                        // reaches the body's edge on its own bank-derived level; the carve makes them contiguous.
                        c.Nodes = nw;
                        for (int p = 0; p < add; p++)
                        {
                            float wx2 = (path[p] % n) * cell - half, wz2 = (path[p] / n) * cell - half;
                            if (wx2 < c.MinX) c.MinX = wx2; if (wx2 > c.MaxX) c.MaxX = wx2;
                            if (wz2 < c.MinZ) c.MinZ = wz2; if (wz2 > c.MaxZ) c.MaxZ = wz2;
                        }
                        inflowsConnected++;
                    }
                }
                FiresLogger.LogInfo($"[FiresWaterPlan]  │  rivers: INFLOWS CONNECTED — {inflowsConnected} stranded river end(s) carved down their spill-basin into the body they drain into (fill-bounded, in-basin).");
            }

            FiresLogger.LogInfo($"[FiresWaterDiag] SHORE APPROACH — {s_endsLanded:N0} run(s) LANDED their final node on the receiving body itself (was: every trimmed run stopped up to {SHORE_ARRIVE_M:0} m short of the water, so EmitConnection put the body level on a node that was not at the body and SetBankDerivedLevels' base-level anchor could not see it at all). {s_shoreTrimmed:N0} node(s) trimmed where a run reached within {SHORE_ARRIVE_M:0} m of its body and then kept going ALONG the shore, and {s_inletMerged:N0} run(s) re-ended on a mouth another run had already made within {INLET_MERGE_M:0} m. "
                    + "Both are TRUNCATIONS: no run is deleted and no node is moved, so every catchment still drains to the same body (v113 reverted a pass that snapped ends to the nearest body and floated the ribbons — this cannot do either).");
            FiresLogger.LogInfo($"[FiresWaterDiag] SINK RESOLUTION - of the runs whose trace merely ENDED (previously ALL stamped OCEAN and handed sea level as their destination): {s_sinkSea:N0} genuinely REACHED the sea, {s_sinkJoin:N0} join another river at a CONFLUENCE, and {s_sinkNone:N0} had no destination of any kind and were NOT emitted (user: 'if they have no spring, pond, lake, or another river to start from or run into they should not exist'). Every emitted run now ends on water that exists.");
            // ═══ PROBE: WHAT IS THE CONNECTION GRAPH ACTUALLY DOING AT ONE SPOT? ══════════════════════════
            // Set FIRES_PROBE="x,z,radius" (world metres). Lists every connection with a node in that box —
            // its endpoints' body ids and KINDS, node count and length. Exists because the tangled "knots" of
            // channel around some ponds survived both the shore-approach and duplicate-mouth rules, which
            // means the cause is a third mechanism and two guesses have already been wrong. The graph can say
            // what it built; nothing else in the pipeline can.
            {
                string probe = Environment.GetEnvironmentVariable("FIRES_PROBE");
                if (!string.IsNullOrEmpty(probe))
                {
                    var parts = probe.Split(',');
                    if (parts.Length >= 3 && float.TryParse(parts[0], out float px) && float.TryParse(parts[1], out float pz) && float.TryParse(parts[2], out float pr))
                    {
                        string KindOf(int id) => id == WaterConnection.SPRING ? "SPRING"
                                               : id == WaterConnection.OCEAN ? "OCEAN"
                                               : (id >= 0 && id < plan.Bodies.Count) ? $"{plan.Bodies[id].Kind}#{id}" : $"?{id}";
                        var sbp = new System.Text.StringBuilder($"[FiresWaterDiag] PROBE ({px:0},{pz:0}) r={pr:0} m — connections with a node in the box:");
                        int hits = 0;
                        foreach (var c in plan.Connections)
                        {
                            int m = c.Nodes.Length / 4; bool inBox = false; float len = 0f;
                            for (int k = 0; k < m; k++)
                            {
                                float dx = c.Nodes[k * 4] - px, dz = c.Nodes[k * 4 + 1] - pz;
                                if (Math.Abs(dx) <= pr && Math.Abs(dz) <= pr) inBox = true;
                                if (k > 0) { float ax = c.Nodes[k * 4] - c.Nodes[(k - 1) * 4], az = c.Nodes[k * 4 + 1] - c.Nodes[(k - 1) * 4 + 1]; len += (float)Math.Sqrt(ax * ax + az * az); }
                            }
                            if (!inBox) continue;
                            hits++;
                            if (hits <= 40)
                                sbp.Append($"\n      {KindOf(c.FromBody),-12} → {KindOf(c.ToBody),-12} | {m,4} node(s) | {len,7:0} m | first ({c.Nodes[0]:0},{c.Nodes[1]:0}) last ({c.Nodes[(m - 1) * 4]:0},{c.Nodes[(m - 1) * 4 + 1]:0})");
                        }
                        sbp.Append($"\n      TOTAL {hits} connection(s) in a {2 * pr:0} m box. Many short runs sharing one destination is a BRAID; many runs between DIFFERENT bodies is a genuine junction of separate drainages.");
                        FiresLogger.LogInfo(sbp.ToString());
                    }
                }
            }
            plan.BuildIndex(n, half);
            FiresLogger.LogInfo($"[FiresWaterPlan] FLOW ORDER: {s_tracesReversed:N0} trace(s) arrived MOUTH-FIRST and were reversed into flow order before segmentation - see the note there; reading them as-is is what made every connection FromBody/ToBody reversed. ENDS RECONNECTED: {s_endsReconnected:N0} run(s) whose trace stopped one cell short of the water it drains into were tied to that real body instead of being labelled OCEAN.");
            FiresLogger.LogInfo($"[FiresWaterPlan] planned {plan.Bodies.Count} bodies + {plan.Connections.Count} connections in {sw.Elapsed.TotalSeconds:0.0}s " +
                      $"({rejSteep} candidates rejected: steep floor ≠ lake basin; {rejHigh} rejected: high pocket without cirque-scale size — levels are constants, nothing downstream may change them).");
            // ── THE WATER FUNNEL — read a lake=0/pond=0 world's cause straight off this line ──
            float swampPct = landCells > 0 ? 100f * swampBiomeCells / landCells : 0f;
            FiresLogger.LogInfo($"[FiresWaterDiag] swamp-biome {swampPct:0.0}% of land ({swampBiomeCells:N0}/{landCells:N0} cells). " +
                      $"LAKE FUNNEL: {lakeBasinsFound} lowland depressions → rej steep-floor {rejSteep}, rej high-pocket {rejHigh} → " +
                      $"candidates lake {candLake} pond {candPond} tarn {candTarn}. " +
                      $"MARSH FUNNEL: {marshRegions} swamp-biome regions → too-small {marshTooSmall}, broken-ground {marshBroken} (holdable<15% {brkHoldable} | floods<25% {brkNoFlood} | sheet-too-thin {brkThinSheet} | not-level<30% {brkNotLevel}), overflow {marshOverflow} → " +
                      $"candidates marsh {candMarsh} sea-marsh {candSeaMarsh}. " +
                      $"PLACED (overlap referee dropped {overlapDrop}; {oceanOwned} refused as TIDAL - level <= SEA+0.75, the ocean owns them; {s_emergentPonds} EMERGENT - made by their own rivers at bodyless basins): lake {placedLake} pond {placedPond} tarn {placedTarn} marsh {placedMarsh} sea-marsh {placedSeaMarsh}. " +
                      $"[If placedLake+placedPond==0 the cause is now explicit: no depressions found, all culled by a gate above, or all lost to overlap/marsh.]");
            // ── WHERE DID THE WETLAND GROUND ACTUALLY GO? (v133) ──────────────────────────────────────────
            // ⚠ THIS IS THE INSTRUMENT THAT CATCHES THE FAILURE THE v132 WETLAND PASS SHIPPED. Taking a basin
            // away from the lake funnel is only correct if something else then places water on it. If it
            // becomes neither marsh nor anything else, the pass has not made the water shallow — it has
            // DELETED it, and every other number (fewer bodies, less flooded area) reads like progress while
            // the world loses lakes. ORPHANED is the number that must stay near zero; it is the whole risk of
            // the wetland model in one integer.
            if (wetland != null)
            {
                long wTot = 0, wMarsh = 0, wOther = 0, wIsland = 0, wNone = 0;
                for (int i = 0; i < n * n; i++)
                {
                    if (!wetland[i]) continue;
                    wTot++;
                    int o = owner[i];
                    if (o == 0) { if (marshClaim[i]) wIsland++; else wNone++; continue; }
                    var k = plan.Bodies[o - 1].Kind;
                    if (k == WaterBodyKind.Marsh || k == WaterBodyKind.SeaMarsh) wMarsh++; else wOther++;
                }
                FiresLogger.LogInfo($"[FiresWaterDiag] WETLAND GROUND — {wTot:N0} cell(s) classified wetland by hydrology: " +
                          $"{wMarsh:N0} ({(wTot > 0 ? 100.0 * wMarsh / wTot : 0):0.0}%) under a MARSH body, " +
                          $"{wIsland:N0} standing above a marsh sheet as its islands (dry BY DESIGN — MARSH_SUBMERGE_PCT leaves the top {100f * (1f - MARSH_SUBMERGE_PCT):0}% emergent), " +
                          $"{wOther:N0} under a lake/pond/tarn (no marsh candidate covered them, so the lake funnel kept them — the fallback working), " +
                          $"{wNone:N0} DRY AND UNCLAIMED. " +
                          $"Only the last number is a loss, and it is bounded by what the lake funnel always rejected anyway: " +
                          $"this pass may only REDIRECT a basin from lake to marsh, never make one disappear.");
            }
            if (marshSizes.Count > 0)
            {
                marshSizes.Sort();
                int Pct(float p) => marshSizes[System.Math.Min(marshSizes.Count - 1, (int)(marshSizes.Count * p))];
                long totalSwamp = 0; foreach (int s in marshSizes) totalSwamp += s;
                long inBig = 0; int nBig = 0;
                foreach (int s in marshSizes) if (s >= marshMinCells) { inBig += s; nBig++; }
                // MARSH SIZE FLOOR — now an AREA, so it means the same thing at every resolution the player
                // can pick. Still printed every run: this line is how the old cell-count bug was caught, and
                // it is the fastest check that a studio measurement describes the world the game builds.
                FiresLogger.LogInfo($"[FiresWaterDiag] MARSH SIZE FLOOR — {MARSH_MIN_AREA_M2 / 1e6:0.000} km² = {marshMinCells:N0} cells at this resolution (n={n}, {cell:0.00} m cells). Area-based since v135, so all four selectable resolutions apply the same bar.");
                FiresLogger.LogInfo($"[FiresWaterDiag] MARSH REGION CENSUS ({marshSizes.Count} regions, {totalSwamp:N0} swamp cells, floor {marshMinCells}): " +
                          $"p50 {Pct(0.50f):N0} · p75 {Pct(0.75f):N0} · p90 {Pct(0.90f):N0} · p99 {Pct(0.99f):N0} · max {marshSizes[marshSizes.Count - 1]:N0} cells. " +
                          $"{nBig} region(s) clear the floor and hold {inBig:N0} cells ({(totalSwamp > 0 ? 100f * inBig / totalSwamp : 0f):0.0}% of all swamp); " +
                          $"the rest is {totalSwamp - inBig:N0} cells spread over {marshSizes.Count - nBig} sub-floor region(s). " +
                          $"[If the sub-floor share is large the swamp is FRAGMENTED and merging is the fix; if it is small the floor/model is the fix.]");
            }
            TruncateAqueductRoutes(plan, g, n, half, cell);   // routes that leave the ground are not rivers and are NOT drawn — before anything reads them
            ResolveConfluenceMouths(plan);        // river->river mouths land on the trunk surface, BEFORE canyons read node levels
            SiteCanyons(plan, g, n, half, cell);   // canyons are PLACED here, recorded on the plan, and serialized with it
            return plan;
        }

        // Build one connection's nodes: geometry from the traced path, widths from
        // catchment, LEVELS from the plan's constants with terrain-descent-weighted
        // monotone interpolation (steep stretches drop fast, flats stay near-level).
        /// ═══ SITE THE CANYONS — THIS IS THE "PLACED WITH PURPOSE" STEP ══════════════════════════════════
        /// (user: "canyons are meant to be placed with purpose, carved with purpose, laid out with purpose,
        /// we should KNOW EXACTLY where every canyon is"; and "canyons MUST always descend and be placed in a
        /// way that their outlet can meet a still water or the ocean cleanly".)
        ///
        /// A canyon is a SPAN of an existing run — never new geometry, never a re-route. That is what makes
        /// this safe to add to a water system the user says is otherwise working: siting cannot move a river,
        /// cannot touch a pond, and cannot change where anything drains. It only marks which stretch of an
        /// already-planned run is a canyon, so the carve can shape that stretch differently.
        ///
        /// THE THREE RULES, all decided HERE rather than measured after the fact:
        ///   1. WALLED ON BOTH SIDES. The ground must stand CANYON_WALL_M above the water on BOTH flanks,
        ///      sampled ACROSS the flow. One steep bank is a hillside, not a canyon — the mistake the old
        ///      any-of-32-directions detector made, which pinned lowland lake country.
        ///   2. IT DESCENDS, END TO END. `InletY > OutletY` is asserted, not hoped for. EmitConnection builds
        ///      node levels monotone non-increasing, so a span that fails this is a bug upstream and is
        ///      refused rather than recorded.
        ///   3. ITS OUTLET MEETS REAL WATER. The run must end at a body or a verified ocean mouth
        ///      (`ToBody >= 0 || ToBody == OCEAN`). A run that ends nowhere gets no canyon, because a canyon
        ///      that drains to nothing is the "steps up to a pond, drops, steps up again" defect by another
        ///      name.
        /// Short spans are dropped: a canyon is a landform, not three cells of steep bank.
        /// ═══ A TRIBUTARY MEETS THE TRUNK AT THE TRUNK'S HEIGHT — RESOLVED AT PLAN TIME ══════════════════
        /// (user, 2026-09: "WE PLACE STILLWATER FIRST WE SHOULD BE ABLE TO DRAW THE FUCKING CONNECTIONS AT
        /// THE CORRECT HEIGHTS NOT FUCKING RE PASS TO FIX THEM LATER".)
        /// River→body mouths land exactly on the body constant. River→river mouths did NOT: CONFLUENCE has
        /// no body, so EmitConnection fell through to a GROUND-derived guess for lEnd, and the tributary
        /// arrived above or below the trunk it joins. The field's min-write then stamped the wrong level
        /// onto the trunk's own cells and the trunk's surface dipped and climbed at every junction —
        /// measured as ~2,400 uphill rises world-wide after every other junction family was fixed.
        /// The trunk's surface at the junction is KNOWN — it is in the trunk's own Nodes — so it is looked
        /// up here, once, before anything consumes the plan. The tributary is then re-floored at it and its
        /// tail lands ON it: the same law every body mouth already obeys.
        /// ═══ A ROUTE THAT LEAVES THE GROUND IS NOT A RIVER. IT IS NOT DRAWN. ════════════════════════════
        /// (user, 2026-09-01, standing beside a walled channel carrying water across a valley: "if the start
        /// point and the end point cannot be at the proper levels because the river wants to run across a
        /// valley than that river had no business even being drawn there in the first place … obviously
        /// rivers should never be running perpendicular to the fucking valleys".)
        /// He is right, and the aqueduct was made by two CORRECT rules colliding over an INVALID route:
        /// the junction law floors the surface at the destination body's level, and where the trace crossed
        /// ground far BELOW that level, the berm dutifully built walls to carry the water over the valley —
        /// a Roman aqueduct, photographed. The old code resolved the same collision the other way (drop the
        /// water into the valley, then CLIMB back out = the uphill defect). Level rules cannot fix a route:
        /// water that reaches a valley ENDS in that valley.
        /// So routes are validated against the ground BEFORE anything consumes them: walk each run in flow
        /// order, and where its planned surface would stand more than STAND_TOL above the terrain for longer
        /// than STAND_RUN_M, CUT the run at the last grounded node. The remainder is not re-routed, not
        /// lowered, not walled — it is NOT DRAWN. A cut run that keeps enough nodes ends where the water
        /// physically ends (the valley edge, as a fall — descending steps are wanted); one that loses its
        /// body is removed outright.
        static void TruncateAqueductRoutes(FiresWaterPlan plan, float[] g, int n, float half, float cell)
        {
            const float STAND_TOL = 4f;     // the sheet may stand this far over pre-carve ground (the carve + berm can honestly absorb it)
            const float STAND_RUN_M = 24f;  // …but only for this long: a gully crossing is fine, a valley crossing is not
            int truncated = 0, removed = 0; float worstStand = 0f;
            float GroundAt(float wx, float wz)
            {
                int gx = Mathf.Clamp(Mathf.RoundToInt((wx + half) / cell), 0, n - 1);
                int gz = Mathf.Clamp(Mathf.RoundToInt((wz + half) / cell), 0, n - 1);
                return g[gz * n + gx];
            }
            foreach (var c in plan.Connections)
            {
                int nn = c.NodeCount;
                if (nn < 2) continue;
                int cutAt = -1; float over = 0f; int overStart = -1;
                for (int k = 0; k < nn; k++)
                {
                    float stand = c.Nodes[k * 4 + 3] - GroundAt(c.Nodes[k * 4], c.Nodes[k * 4 + 1]);
                    if (stand > worstStand) worstStand = stand;
                    if (stand > STAND_TOL)
                    {
                        if (overStart < 0) { overStart = k; over = 0f; }
                        if (k > 0)
                        {
                            float ex = c.Nodes[k * 4] - c.Nodes[(k - 1) * 4], ez = c.Nodes[k * 4 + 1] - c.Nodes[(k - 1) * 4 + 1];
                            over += Mathf.Sqrt(ex * ex + ez * ez);
                        }
                        if (over > STAND_RUN_M) { cutAt = overStart; break; }
                    }
                    else { overStart = -1; over = 0f; }
                }
                if (cutAt < 0) continue;
                if (cutAt < 3) { c.Nodes = System.Array.Empty<float>(); removed++; continue; }   // nothing grounded to keep
                var keep = new float[cutAt * 4];
                Array.Copy(c.Nodes, keep, keep.Length);
                c.Nodes = keep;
                truncated++;
            }
            FiresLogger.LogInfo($"[FiresWaterDiag] AQUEDUCT ROUTES — {truncated:N0} run(s) CUT where their planned surface stood more than {STAND_TOL:0} m over the terrain for over {STAND_RUN_M:0} m (water that reaches a valley ENDS there; it is not carried across on walls), {removed:N0} run(s) removed outright (nothing grounded left). Worst stand seen {worstStand:F1} m. The berm no longer receives impossible water to wall in.");
        }

        static void ResolveConfluenceMouths(FiresWaterPlan plan)
        {
            // Coarse spatial index of every node of every run, so each mouth queries neighbours instead of
            // scanning the world. 16 m cells; a junction is by definition within a tube width.
            const float CELL = 16f;
            var index = new Dictionary<long, List<(int conn, int node)>>(1 << 16);
            long Key(float x, float z) => ((long)Mathf.Floor(x / CELL) << 32) ^ (uint)(int)Mathf.Floor(z / CELL);
            for (int ci = 0; ci < plan.Connections.Count; ci++)
            {
                var c = plan.Connections[ci];
                for (int k = 0; k < c.NodeCount; k++)
                {
                    long key = Key(c.Nodes[k * 4], c.Nodes[k * 4 + 1]);
                    if (!index.TryGetValue(key, out var l)) index[key] = l = new List<(int, int)>();
                    l.Add((ci, k));
                }
            }
            int resolved = 0, orphan = 0;
            foreach (var c in plan.Connections)
            {
                if (c.ToBody != WaterConnection.CONFLUENCE) continue;
                int nn = c.NodeCount;
                if (nn < 2) continue;
                float mx = c.Nodes[(nn - 1) * 4], mz = c.Nodes[(nn - 1) * 4 + 1];
                // The trunk's level at the junction: the nearest node of a DIFFERENT run within 24 m.
                float best = float.MaxValue, trunkLvl = 0f;
                for (int dz = -2; dz <= 2; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        long key = Key(mx + dx * CELL, mz + dz * CELL);
                        if (!index.TryGetValue(key, out var l)) continue;
                        foreach (var (oc, ok) in l)
                        {
                            if (oc == c.Id) continue;
                            var t = plan.Connections[oc];
                            float ddx = t.Nodes[ok * 4] - mx, ddz = t.Nodes[ok * 4 + 1] - mz;
                            float d2 = ddx * ddx + ddz * ddz;
                            if (d2 < best && d2 <= 24f * 24f) { best = d2; trunkLvl = t.Nodes[ok * 4 + 3]; }
                        }
                    }
                if (best == float.MaxValue) { orphan++; continue; }   // no trunk in reach — leave the guess
                // Same law as every body mouth — but the floor is the APPROACH's, not the whole run's.
                // Flooring every node at trunk level lifted kilometres of tributary over low banks and the
                // berm count went 10,022 -> 16,208 building levees to hold it: measured, reverted. Only the
                // last APPROACH_M pool up as backwater; upstream keeps its own descent.
                const float APPROACH_M = 60f;
                float back = 0f;
                for (int k = nn - 1; k > 0 && back <= APPROACH_M; k--)
                {
                    if (c.Nodes[k * 4 + 3] < trunkLvl) c.Nodes[k * 4 + 3] = trunkLvl;
                    float ex = c.Nodes[k * 4] - c.Nodes[(k - 1) * 4], ez = c.Nodes[k * 4 + 1] - c.Nodes[(k - 1) * 4 + 1];
                    back += Mathf.Sqrt(ex * ex + ez * ez);
                }
                c.Nodes[(nn - 1) * 4 + 3] = trunkLvl;
                resolved++;
            }
            FiresLogger.LogInfo($"[FiresWaterDiag] CONFLUENCE MOUTHS — {resolved:N0} tributary run(s) re-floored to their TRUNK's own surface at the junction (was: a ground-derived guess, because CONFLUENCE has no body constant), {orphan:N0} found no trunk within 24 m and keep the guess. River→river junctions now obey the same law as river→body ones: the mouth height is KNOWN, so the run lands on it.");
        }

        /// ═══ A RIVER THAT ENTERS A BASIN FILLS IT AND CONTINUES — THE POND MAKES ITSELF ═════════════════
        /// (user, 2026-09-02, standing in a dry river corridor with `waterat` reading `class NONE(dry),
        /// surfaceSmooth none` mid-run and `surfaceSmooth 51.2 (depth 0.1)` under ground 53.2 at its edge —
        /// the buried tail of a run that POURED INTO A DIP AND STOPPED.)
        /// GEN 188's basin rule was half of real hydrology: water entering a bodyless depression does end
        /// there — but only until the basin FILLS TO ITS SPILL, and then it continues. Terminating the run
        /// and spring-culling its continuation modelled an endorheic sink; most of these dips are not sinks,
        /// they are pond sites the overlap referee or the size floor happened to refuse. The result was
        /// exactly what the user photographed: a carved valley whose river dies at a dip, a dry corridor
        /// beyond it, and dry stretches just past lake outlets (the run left the lake and died at the first
        /// hollow).
        /// So the river now MAKES its pond: entry into a bodyless non-tidal depression creates a real
        /// WaterBody (Kind = Pond) whose footprint is the pond rule's own flood (`ground < level`,
        /// 4-connected, closed by construction) and whose level is the basin's FILL height minus the same
        /// freeboard every placed body carries. The segmentation walk then handles it like any body it has
        /// ever met: the run ends AT the pond (landing node, junction-exact mouth), and the continuation
        /// resumes FROM it as the pond's outlet at the pond's level. Rivers chain through ponds — which is
        /// what they do.
        /// Bounds, so this cannot recreate what other rules deliberately refused: TIDAL basins still belong
        /// to the ocean (existing rule); a flood bigger than EMERGENT_MAX_CELLS aborts (a huge refused lake
        /// was refused for ownership reasons — the run terminates as a true BASIN sink instead); smaller
        /// than EMERGENT_MIN_CELLS is a puddle and not worth a body. The flood halts at any owned cell, so
        /// an existing body's territory is never re-claimed.
        static int TryCreateEmergentPond(FiresWaterPlan plan, int[] owner, float[] g, float[] fill,
                                         int seedI, int n, float cell, float half, bool[] depTried)
        {
            const int EMERGENT_MIN_CELLS = 8;
            const int EMERGENT_MAX_CELLS = 60000;
            float lvl = fill[seedI] - 0.85f;                 // the same freeboard placed bodies carry (spill − 0.85)
            if (lvl <= g[seedI] + 0.05f) lvl = fill[seedI] - 0.05f;   // very shallow basin — sit just under the spill
            if (depTried[seedI]) return -1;                  // this basin already failed once — do not re-flood it per NODE (a 60k-cell abort repeated for every node of every trace in a big basin is what made generation hang)
            var cells = new List<int>(256);
            var q = new Queue<int>();
            var seen = new HashSet<int> { seedI };
            q.Enqueue(seedI);
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                if (owner[i] != 0) continue;                 // someone else's territory bounds the flood
                if (g[i] >= lvl) continue;                   // the shoreline: ground rises through the level
                cells.Add(i);
                if (cells.Count > EMERGENT_MAX_CELLS) { foreach (int t in seen) depTried[t] = true; return -1; }
                int x = i % n, z = i / n;
                if (x + 1 < n - 1 && seen.Add(i + 1)) q.Enqueue(i + 1);
                if (x - 1 > 0 && seen.Add(i - 1)) q.Enqueue(i - 1);
                if (z + 1 < n - 1 && seen.Add(i + n)) q.Enqueue(i + n);
                if (z - 1 > 0 && seen.Add(i - n)) q.Enqueue(i - n);
            }
            if (cells.Count < EMERGENT_MIN_CELLS) { foreach (int t in seen) depTried[t] = true; return -1; }

            int gx0 = int.MaxValue, gz0 = int.MaxValue, gx1 = int.MinValue, gz1 = int.MinValue;
            foreach (int i in cells)
            {
                int x = i % n, z = i / n;
                if (x < gx0) gx0 = x; if (x > gx1) gx1 = x;
                if (z < gz0) gz0 = z; if (z > gz1) gz1 = z;
            }
            var body = new WaterBody
            {
                Id = plan.Bodies.Count,
                Kind = WaterBodyKind.Pond,
                Level = lvl,
                Gx0 = gx0, Gz0 = gz0, W = gx1 - gx0 + 1, H = gz1 - gz0 + 1,
            };
            body.Mask = new byte[body.W * body.H];
            foreach (int i in cells)
            {
                owner[i] = body.Id + 1;
                int x = i % n, z = i / n;
                body.Mask[(z - gz0) * body.W + (x - gx0)] = 1;
            }
            body.MinX = gx0 * cell - half; body.MaxX = gx1 * cell - half;
            body.MinZ = gz0 * cell - half; body.MaxZ = gz1 * cell - half;
            plan.Bodies.Add(body);
            s_emergentPonds++;
            return body.Id;
        }

        static void SiteCanyons(FiresWaterPlan plan, float[] g, int n, float half, float cell)
        {
            const float WALL_M     = 12f;   // ground this far above the water is a wall — same bar the runtime label uses
            const float PROBE_M    = 20f;   // how far across the flow to look for that wall
            const float MIN_LEN_M  = 60f;   // shorter than this is a gorge-let, not a canyon worth naming
            const float GAP_M      = 90f;   // a knickpoint POOL this long or shorter is interior, not an end
            const float SHELF_M    = 2.0f;  // walkable land each side of the water (user: "a meter or two")
            int sited = 0, refusedShort = 0, refusedFlat = 0, refusedEnd = 0, merged = 0;

            float GroundAt(float wx, float wz)
            {
                int gx = Mathf.Clamp(Mathf.RoundToInt((wx + half) / cell), 0, n - 1);
                int gz = Mathf.Clamp(Mathf.RoundToInt((wz + half) / cell), 0, n - 1);
                return g[gz * n + gx];
            }

            foreach (var c in plan.Connections)
            {
                int nn = c.NodeCount;
                if (nn < 3) continue;
                // ⚠ A CONFLUENCE AND A CANYON LAKE ARE BOTH DESTINATIONS. (user: "this is exactly why i said
                // canyons need their own special logic, we cannot just blanket cover all rivers with one
                // logic"; and "a canyon can always end in a canyon lake or pond, thats fine".) This rule was
                // `ToBody >= 0 || == OCEAN`, borrowed wholesale from how a RUN is validated, and it refused
                // 41,455 runs — exactly the confluence count. A tributary that joins a trunk still reaches
                // still water, through the trunk; and a canyon may simply terminate in its OWN pool. Only a
                // run that goes nowhere at all is refused.
                if (c.ToBody == WaterConnection.SPRING) { refusedEnd++; continue; }

                // ── 1. FIND THE ENCLOSED STRETCHES ───────────────────────────────────────────────────────
                var spans = new List<(int a, int b)>();
                int spanStart = -1;
                for (int k = 0; k <= nn; k++)
                {
                    bool walled = false;
                    if (k < nn)
                    {
                        float x = c.Nodes[k * 4], z = c.Nodes[k * 4 + 1], lvl = c.Nodes[k * 4 + 3];
                        // Across-flow axis from the local tangent — a canyon is walled ACROSS the channel,
                        // and sampling on world axes would read a bend as a wall.
                        int kp = k > 0 ? k - 1 : k, kn2 = k + 1 < nn ? k + 1 : k;
                        float dx = c.Nodes[kn2 * 4] - c.Nodes[kp * 4], dz = c.Nodes[kn2 * 4 + 1] - c.Nodes[kp * 4 + 1];
                        float dl = Mathf.Sqrt(dx * dx + dz * dz);
                        if (dl > 1e-4f)
                        {
                            float px = -dz / dl, pz = dx / dl;
                            bool left = false, right = false;
                            for (float r = 6f; r <= PROBE_M && !(left && right); r += 4f)
                            {
                                if (!left  && GroundAt(x + px * r, z + pz * r) - lvl >= WALL_M) left = true;
                                if (!right && GroundAt(x - px * r, z - pz * r) - lvl >= WALL_M) right = true;
                            }
                            walled = left && right;      // BOTH flanks — one is a hillside
                        }
                    }
                    if (walled) { if (spanStart < 0) spanStart = k; continue; }
                    if (spanStart >= 0) { if (k - 1 > spanStart) spans.Add((spanStart, k - 1)); spanStart = -1; }
                }
                if (spans.Count == 0) continue;

                // ── 2. A POOL DOES NOT END A CANYON — MERGE ACROSS IT ────────────────────────────────────
                // (researched 2026-08-31: a canyon is graded reaches SEPARATED BY KNICKPOINTS, and below each
                // knickpoint a hydraulic jump drills a pothole that becomes a PLUNGE POOL — the user's
                // "grottos, nice pools of water".) At such a pool the water widens, the walls step back, and
                // the two-flank test lapses for a few nodes. Treating that as the END of a canyon splits ONE
                // canyon into fragments and then rejects each as too short — measured, 1,728 refusals, which
                // is why siting produced zero. The gap IS the pool, and the pool is interior.
                for (int q = spans.Count - 1; q > 0; q--)
                {
                    float gap = 0f;
                    for (int t = spans[q - 1].b; t < spans[q].a; t++)
                    {
                        float ex = c.Nodes[(t + 1) * 4] - c.Nodes[t * 4], ez = c.Nodes[(t + 1) * 4 + 1] - c.Nodes[t * 4 + 1];
                        gap += Mathf.Sqrt(ex * ex + ez * ez);
                    }
                    if (gap <= GAP_M) { spans[q - 1] = (spans[q - 1].a, spans[q].b); spans.RemoveAt(q); merged++; }
                }

                // ── 3. EMIT THE ONES THAT ARE ACTUALLY CANYONS ───────────────────────────────────────────
                foreach (var sp in spans)
                {
                    int a = sp.a, b = sp.b;
                    float len = 0f;
                    for (int q = a; q < b; q++)
                    {
                        float ex = c.Nodes[(q + 1) * 4] - c.Nodes[q * 4], ez = c.Nodes[(q + 1) * 4 + 1] - c.Nodes[q * 4 + 1];
                        len += Mathf.Sqrt(ex * ex + ez * ez);
                    }
                    if (len < MIN_LEN_M) { refusedShort++; continue; }

                    float inY = c.Nodes[a * 4 + 3], outY = c.Nodes[b * 4 + 3];
                    // ⚠ IT MUST NOT CLIMB. IT DOES NOT HAVE TO DESCEND. (user: "a canyon doesnt NEED to
                    // descend, it can just never have water CLIMBING from inlet to outlet".) Requiring a
                    // strict drop refused 658 spans that were simply FLAT — a low-gradient reach, or the
                    // pool between two knickpoints, both of which are canyon floor and not defects. Interior
                    // steps DOWN are knickpoints and are wanted; the only thing refused is a canyon whose
                    // outlet stands above its inlet, which is water running uphill.
                    if (outY > inY + 0.05f) { refusedFlat++; continue; }

                    float wid = c.Nodes[a * 4 + 2];
                    var q2 = new CanyonReach
                    {
                        Id = plan.Canyons.Count,
                        ConnectionId = c.Id,
                        Node0 = a, Node1 = b,
                        InletY = inY, OutletY = outY,
                        FloorHalfW = Mathf.Max(1.5f, wid * 0.5f) + SHELF_M,
                        ShelfM = SHELF_M,
                        MinX = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxZ = float.MinValue,
                    };
                    for (int q = a; q <= b; q++)
                    {
                        float x = c.Nodes[q * 4], z = c.Nodes[q * 4 + 1];
                        if (x < q2.MinX) q2.MinX = x; if (x > q2.MaxX) q2.MaxX = x;
                        if (z < q2.MinZ) q2.MinZ = z; if (z > q2.MaxZ) q2.MaxZ = z;
                    }
                    plan.Canyons.Add(q2);
                    sited++;
                }
            }
            FiresLogger.LogInfo($"[FiresWaterDiag] CANYON SITING — {sited:N0} canyon(s) PLACED and recorded on the plan, each a span of an existing run so siting cannot move a river, re-route one, or touch a pond. Walled on BOTH flanks (>= {WALL_M:0} m within {PROBE_M:0} m, sampled ACROSS the flow — one flank is a hillside), at least {MIN_LEN_M:0} m long, and it never CLIMBS. {merged:N0} fragment(s) JOINED across an interior pool: a canyon is graded reaches separated by KNICKPOINTS, and the plunge pool below a knickpoint is canyon floor, not a boundary — splitting there is what made an earlier cut site ZERO. Refused: {refusedShort:N0} too short, {refusedFlat:N0} CLIMBED (outlet above inlet; FLAT is fine, only a rise is refused), {refusedEnd:N0} run(s) going nowhere at all (a confluence and a canyon's own pool both count as destinations). Serialized with the plan, so the carve and the game read the SAME canyons.");
        }

        private static void EmitConnection(FiresWaterPlan plan, float[] poly, int i0, int i1,
                                           int fromBody, int toBody,
                                           float[] g, int n, float half, float cell,
                                           float WK, float WP, float WMIN, float WMAX,
                                           float endX = float.NaN, float endZ = float.NaN)
        {
            int count = i1 - i0;
            if (count < 2) return;

            float GroundAt(int i)
            {
                int gx = Mathf.Clamp(Mathf.RoundToInt((poly[i * 4] + half) / cell), 0, n - 1);
                int gz = Mathf.Clamp(Mathf.RoundToInt((poly[i * 4 + 1] + half) / cell), 0, n - 1);
                return g[gz * n + gx];
            }

            // DEPTH IS SET BY WIDTH (user): the surface sits SurfDepth(width) below the
            // terrain it runs through — a narrow stream hugs the ground and runs shallow;
            // a wide river sits deep and carves. It descends ONLY (never uphill) but as
            // STEEPLY as the terrain drops (user: water flows steeply downhill), and never
            // below its downstream destination. A spring starts a channel-depth down; an
            // ocean outfall ends below spring-low tide. (The conformance BED carve is
            // width-scaled too, so the whole channel gets deeper with width, not just the
            // surface.)
            float WidthAt(int i) => Mathf.Clamp(WK * Mathf.Pow(Mathf.Max(poly[(i0 + i) * 4 + 2], 1f), WP), WMIN, WMAX);
            float SurfDepth(float w) => Mathf.Lerp(0.6f, 3f, Mathf.InverseLerp(WMIN, WMAX, w));

            // The surface HUGS the terrain (sits SurfDepth below it), so on a steep drop it
            // stays a thin sliver in the channel and NEVER floats — user: "thin slivers of
            // water are better than floating mesh… VFX shows the rapids/waterfall." So the
            // profile is NOT smoothed toward a gentle ramp (that floats over the drop); it
            // tracks the real ground, descend-only, and steep reaches are left for VFX.
            float wStart = WidthAt(0), wEnd = WidthAt(count - 1);
            float lStart = fromBody >= 0 ? plan.Bodies[fromBody].Level : GroundAt(i0) - SurfDepth(wStart);
            // ═══ THE SEA'S LEVEL IS LOOKED UP, NOT HARD-CODED (2026-08-13) ═══════════════════════════════
            // Was ESTUARY_FLOOR (SEA−6). That constant conflated two different things: the water SURFACE and
            // the estuary BED. Ending the SURFACE six metres under the sea dragged the whole approach profile
            // down with it (the profile is monotone to lEnd), which is what pushed the ribbon out across the
            // seabed and left rivers drawn over open water. The surface of a river at its mouth is the surface
            // of the sea — that is what "meeting the ocean" means — so it now ends at the ocean BODY's Level.
            // The bed still descends below it (the carve cuts `level − depth·shape`), so the estuary channel
            // is still dug; only the sheet stops at the waterline, which is the whole point.
            float lEnd = toBody >= 0 ? plan.Bodies[toBody].Level
                        : toBody == WaterConnection.OCEAN ? OceanLevel(plan)
                        : GroundAt(i1 - 1) - SurfDepth(wEnd);
            if (lEnd > lStart) lEnd = lStart;                       // water never runs uphill

            // The run may be given an explicit LANDING NODE on the receiving body (see the trim block at
            // the call site): one extra node, at the water, carrying lEnd. Without it a trimmed run ends up
            // to SHORE_ARRIVE_M short of the body it drains into and nothing downstream can tell it apart
            // from a run that ends nowhere.
            bool hasEnd = !float.IsNaN(endX) && !float.IsNaN(endZ);
            int total = count + (hasEnd ? 1 : 0);
            var nodes = new float[total * 4];
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            float lvl = lStart;
            for (int i = 0; i < count; i++)
            {
                int si = (i0 + i) * 4;
                float x = poly[si], z = poly[si + 1];
                float width = WidthAt(i);
                if (i > 0)
                {
                    float target = GroundAt(i0 + i) - SurfDepth(width);  // sit width-scaled depth under the terrain…
                    lvl = Mathf.Clamp(Mathf.Min(lvl, target), lEnd, lvl); // …descend only, hugging it (thin slivers, no float)
                }
                nodes[i * 4] = x; nodes[i * 4 + 1] = z; nodes[i * 4 + 2] = width; nodes[i * 4 + 3] = lvl;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            if (hasEnd)
            {
                int li = count;
                nodes[li * 4] = endX; nodes[li * 4 + 1] = endZ;
                nodes[li * 4 + 2] = WidthAt(count - 1);              // the mouth keeps the arriving channel's width
                if (endX < minX) minX = endX; if (endX > maxX) maxX = endX;
                if (endZ < minZ) minZ = endZ; if (endZ > maxZ) maxZ = endZ;
            }
            nodes[(total - 1) * 4 + 3] = lEnd;                      // lands EXACTLY on the downstream constant, AT the water

            plan.Connections.Add(new WaterConnection
            {
                Id = plan.Connections.Count,
                FromBody = fromBody >= 0 ? fromBody : WaterConnection.SPRING,
                ToBody = toBody,
                Nodes = nodes,
                MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ,
            });
        }
    }
}
