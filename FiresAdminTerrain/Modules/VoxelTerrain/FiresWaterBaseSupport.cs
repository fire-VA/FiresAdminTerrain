using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    public static partial class FiresWorldGenBase
    {
        // USER'S BICYCLE-BAR MODEL — the "level bar swept down the river spine".
        // A river cell's water may slope DOWNSTREAM and must sit LEVEL bank-to-
        // bank, but it must NEVER stand above the LOWER of its two CROSS-CHANNEL
        // banks: the bar rolls down the spine and turns with the bends, but never
        // rolls sideways up over a bank.
        //
        // This restores property #3 the raw-field sampler dropped — once wet
        // cells sampled _waterS directly (TryGetWaterSurfaceSmooth) instead of the
        // reconcile's bank-governed envelope, NOTHING kept a cell below its local
        // banks, so meanders/confluences/steep reaches floated the sheet up over
        // the bank (user screenshots: water draped across the surface, leveled
        // over the banks). Enforced per-cell at full 2m res, PERPENDICULAR to the
        // local flow so a reach never caps itself against its own up/downstream
        // bed. Only LOWERS water; a no-op on well-formed channels.
        // WATERSHED — after the lakes drop to their real level, make the river network CONTINUOUS into
        // them: source → stream → river → lake → river → … → ocean. A river reach touching a LOWER flat
        // body either SMOOTHS down to meet it (gentle drop) or WATERFALLS in (steep drop), user's call:
        // a Dijkstra up-river from every body pulls each reach down to (body + gentle slope × distance)
        // — but only where the total drop is smoothable; bigger drops are left as a fall. Where a reach
        // reaches the body level it merges with it. Rivers already below a body are left (it drains in).
        private static void ReconcileRiversToWater(float[] w, float[] g, byte[] m, int n)
        {
            const float SLOPE = 0.03f;        // gentle river grade for a smoothed approach (m per m up-stream)
            const float SMOOTH_MAX = 4.0f;    // drop ≤ this to a body → SMOOTH down to it; more → let it WATERFALL
            const float CHANNEL_DEPTH = 1.5f; // keep the re-carved channel this deep under the new level
            float cellM = 2f * _half / (n - 1);
            var target = new System.Collections.Generic.Dictionary<int, float>(1 << 17);
            var dist = new System.Collections.Generic.Dictionary<int, float>(1 << 17);
            var heap = new RiverHeap(1 << 16);
            int waterfalls = 0;

            // Seed: a wet river cell touching a LOWER still-water body (lake/pond/ocean).
            for (int i = 0; i < n * n; i++)
            {
                if ((m[i] != 1 && m[i] != 3) || w[i] <= g[i] + 0.01f) continue;
                int x = i % n, z = i / n;
                float body = float.MaxValue;
                Body(i - 1, x > 0); Body(i + 1, x + 1 < n); Body(i - n, z > 0); Body(i + n, z + 1 < n);
                void Body(int j, bool ok)
                {
                    if (ok && w[j] > g[j] + 0.01f && m[j] != 1 && m[j] != 3 && w[j] < body) body = w[j];
                }
                if (body >= w[i] - 0.01f) continue;          // no lower body adjacent
                if (w[i] - body <= SMOOTH_MAX) { target[i] = body; dist[i] = 0f; heap.Push(0f, i); }  // smooth down
                else waterfalls++;                            // steep → leave the drop as a waterfall
            }

            // Propagate the smoothed descent UP the river: the ramp rises by SLOPE·distance from the body.
            while (heap.Pop(out float d, out int i))
            {
                if (!dist.TryGetValue(i, out float di) || d > di + 0.001f) continue;
                float ti = target[i];
                int x = i % n, z = i / n;
                Up(i - 1, x > 0); Up(i + 1, x + 1 < n); Up(i - n, z > 0); Up(i + n, z + 1 < n);
                void Up(int j, bool ok)
                {
                    if (!ok || (m[j] != 1 && m[j] != 3) || w[j] <= g[j] + 0.01f) return;
                    float nlevel = ti + SLOPE * cellM;
                    if (nlevel >= w[j] - 0.01f) return;        // river already at/below the ramp → it met its own grade
                    if (!target.TryGetValue(j, out float tj) || nlevel < tj)
                    { float nd = di + cellM; target[j] = nlevel; dist[j] = nd; heap.Push(nd, j); }
                }
            }

            // Apply: lower the smoothed cells + re-carve the bed under them.
            int lowered = 0;
            foreach (var kv in target)
            {
                int i = kv.Key; float lvl = kv.Value;
                if (lvl >= w[i] - 0.01f) continue;
                w[i] = lvl;
                float bed = lvl - CHANNEL_DEPTH;
                if (g[i] > bed) g[i] = bed;
                lowered++;
            }
            FiresLogger.LogInfo($"[FiresWorldGen] watershed: {lowered:N0} river cells sloped down to meet lakes/ocean; {waterfalls:N0} steep confluences left as waterfalls — network flows source→lake→river→ocean.");
        }

        // ═══ WATER-CLASS CONSOLIDATION (2026-08-09, user: "consolidate all generation logic using the correct data") ═══
        // The ONE reconcile that classifies still bodies — replacing the DEAD NormalizeBasinWater "basin walk"
        // (its only caller ApplyWaterToImport has no callers, so it never ran → the class grid could NEVER label
        // lake/pond/frozen → lake=0, and every planner lake got tagged RIVER, given RiverRelief, and drawn as
        // flowing water). We do NOT resurrect the discovery walk. The PLAN already knows each body's Kind AND its
        // exact footprint (WaterBody.Kind + Mask) — the authoritative data — so we STAMP the class straight from it
        // and CLEAR the river mask over body cells (a river/outlet crossing a body IS the body, per the merge rule;
        // leaving the mask made _labelS's mask-first test + IsChannelAt re-tag the lake as river). Runs at the TOP of
        // BuildReconciledSurface so its `owned` test (keeps the body's own level in _surfS) and the class-fill's
        // WC_NONE preserve-guard both see the stamps. _plan persists in the .fgen, so this fires on gen AND load.
        // `g`/`w` = the FINAL 2m ground + water fields the world renders from. Coverage is taken from THEM,
        // not from the plan's footprint mask (user 2026-08-11: "the map overlay shows this lake extends to
        // where this river is, but it doesn't in reality … the studio needs to represent what's actually in
        // game"). The plan decides WHICH KIND of body a cell belongs to; whether there is water there at all
        // is the field's call — it is what the sheet, physics and veg all read. Stamping the raw footprint
        // painted 36,521 dry 8m cells as water on a single seed (studio n=10501, seed 220092304), so the map
        // drew lakes over dry ground and every WaterClassAt-based audit inherited the same fiction.
        private static void StampBodyClasses(int n2, int stride, float[] g, float[] w)
        {
            if (_plan == null || _plan.Bodies.Count == 0) return;
            float cellM = (2f * _half) / (_res - 1);
            if (_classS == null || _classN != n2) { _classS = new byte[n2 * n2]; _classN = n2; _classCell = cellM * stride; }
            var cls = _classS; var rm = _riverMask; var bm = _biomeMap; int n = _res;
            int stamped = 0, lakes = 0, ponds = 0, marsh = 0, frozen = 0, skippedDry = 0;
            foreach (var b in _plan.Bodies)
            {
                if (b.Kind == WaterBodyKind.SeaMarsh) continue;   // sea marsh: the ocean plane owns the water — no inland class
                if (b.Kind == WaterBodyKind.Ocean) continue;      // the sea is a body so rivers can terminate on it, but it carries NO inland class (and no mask) — without this it falls through to the else below and is tallied as a LAKE
                byte wc;
                if (b.Kind == WaterBodyKind.Marsh) { wc = WC_SWAMP; marsh++; }
                else
                {
                    // STRICTLY-FROZEN all year (user): mountain/frostreach cold bodies. A Tarn IS a cold cirque lake;
                    // a Lake/Pond whose ground biome is Mountain(3)/Frostreach(7) freezes too. Distinct from lowland
                    // lakes (seasonal freeze by temperature). Sample the biome at the body centroid.
                    byte bio = bm != null ? bm[Mathf.Clamp(b.Gz0 + b.H / 2, 0, n - 1) * n + Mathf.Clamp(b.Gx0 + b.W / 2, 0, n - 1)] : (byte)0;
                    if (b.Kind == WaterBodyKind.Tarn || bio == 3 || bio == 7) { wc = WC_FROZEN; frozen++; }
                    else if (b.Kind == WaterBodyKind.Pond) { wc = WC_POND; ponds++; }
                    else { wc = WC_LAKE; lakes++; }
                }
                for (int lz = 0; lz < b.H; lz++)
                {
                    int gz = b.Gz0 + lz;
                    if (gz < 0 || gz >= n) continue;
                    for (int lx = 0; lx < b.W; lx++)
                    {
                        if (b.Mask[lz * b.W + lx] == 0) continue;
                        int gx = b.Gx0 + lx;
                        if (gx < 0 || gx >= n) continue;
                        int gi = gz * n + gx;
                        if (rm != null) rm[gi] = 0;                          // the body owns this water — never a river cell
                        // COVERAGE FROM THE FIELD: only a cell the world actually renders wet may be classed
                        // as water. A footprint cell the carve/conform left dry is dry — on the map too.
                        if (g != null && w != null && w[gi] <= g[gi] + 0.01f) { skippedDry++; continue; }
                        int cx = gx / stride, cz = gz / stride;
                        if (cx < n2 && cz < n2) { cls[cz * n2 + cx] = wc; stamped++; }
                    }
                }
            }
            FiresLogger.LogInfo($"[FiresWorldGen] body classes stamped from plan: {_plan.Bodies.Count} bodies (lake {lakes}, pond {ponds}, frozen {frozen}, marsh {marsh}) → {stamped} 8m class cells; {skippedDry:N0} footprint cell(s) SKIPPED as dry in the field (the map now shows only water the world renders); river mask cleared over bodies.");
            // ═══ HIGH / FROZEN WATER CENSUS — "am I placing any at all, and WHERE?" (2026-08-14) ════════════
            // (user: "I need a way to find frozen water bodies because I'm not sure we're placing any frozen
            // lakes or high elevation water".) A count alone cannot answer that — it says how many exist, not
            // whether you can ever reach one. This lists the biggest by SIZE with world coordinates and surface
            // level, so they can be flown to directly (the map takes a right-click teleport), and reports the
            // altitude spread so "no high water" is answerable as a fact rather than an impression.
            {
                var hi = new List<(float area, float wx, float wz, float lvl, WaterBodyKind kind)>();
                float loLvl = float.MaxValue, hiLvl = float.MinValue;
                foreach (var b in _plan.Bodies)
                {
                    if (b.Kind == WaterBodyKind.Ocean || b.Mask == null) continue;
                    bool cold = b.Kind == WaterBodyKind.Tarn;
                    if (!cold && bm != null)
                    {
                        byte bio = bm[Mathf.Clamp(b.Gz0 + b.H / 2, 0, n - 1) * n + Mathf.Clamp(b.Gx0 + b.W / 2, 0, n - 1)];
                        cold = bio == 3 || bio == 7;   // Mountain / Frostreach — the same test the class stamp uses
                    }
                    if (!cold) continue;
                    int cells = 0;
                    for (int k = 0; k < b.Mask.Length; k++) if (b.Mask[k] != 0) cells++;
                    if (cells == 0) continue;
                    if (b.Level < loLvl) loLvl = b.Level;
                    if (b.Level > hiLvl) hiLvl = b.Level;
                    hi.Add((cells * cellM * cellM, (b.Gx0 + b.W * 0.5f) * cellM - _half, (b.Gz0 + b.H * 0.5f) * cellM - _half, b.Level, b.Kind));
                }
                if (hi.Count == 0)
                {
                    FiresLogger.LogInfo("[FiresWaterDiag] FROZEN/HIGH WATER: none placed — no tarn, and no lake/pond whose ground biome is Mountain or Frostreach.");
                }
                else
                {
                    hi.Sort((a, b2) => b2.area.CompareTo(a.area));
                    var sb2 = new System.Text.StringBuilder();
                    for (int k = 0; k < Math.Min(10, hi.Count); k++)
                        sb2.Append($"\n    {hi[k].kind} {hi[k].area / 10000f:0.00} ha at ({hi[k].wx:0},{hi[k].wz:0}) surface y={hi[k].lvl:0.0}");
                    FiresLogger.LogInfo($"[FiresWaterDiag] FROZEN/HIGH WATER: {hi.Count} bodies, surface levels y={loLvl:0.0}..{hiLvl:0.0} " +
                              $"(sea is {SEA:0}, so {hiLvl - SEA:0}m above it at the highest). Biggest first — right-click these on the map to teleport:{sb2}");
                }
            }
        }

        // (v132) WETLAND RECLASSIFICATION — the water is not too much, it is at the wrong depth.
        //
        // `PriorityFloodFill` fills every hollow to its SPILL level and leaves it brim-full forever, because it
        // has no evaporation term. That is only right for a basin with enough inflow to overflow. A genuinely
        // closed basin is evaporation-limited: inflow balances evaporation and seepage, and the equilibrium is
        // SHALLOW. Real endorheic basins are playas, salt flats and marshes — the Sudd, the Pantanal, the
        // Western Siberian Lowland (~27% wetland, more drowned than our worst seed, and nobody calls it wrong).
        //
        // ⚠ WHICH BASINS — decided by MEASUREMENT, and not by the axis the spec first proposed. The census
        // (ReportBasinCensus, below) showed catchment:area is a smooth continuum here that separates nothing,
        // while FLOOR GRADE tracks depth almost identically across unrelated seeds:
        //     grade 0.05-0.10 → 2.4 m / 2.7 m      grade 0.10-0.20 → 6.5 m / 6.9 m      grade 0.20+ → 21.6 m / 22.1 m
        // So grade is the classifier. Above 0.20 a basin averages ~22 m deep: a real lake, left alone. There is
        // also a geometric reason to stop there — draining a steep 22 m bowl to a 1 m sheet would not make a
        // swamp, it would very nearly DELETE it, because 1 m of water covers almost none of a steep basin.
        //
        // ⚠⚠ THIS PASS CLASSIFIES. IT DOES NOT SET A LEVEL — and that is the whole point (v133).
        //
        // Until v133 it also lowered `w` to a ~1 m sheet and wrote `w[c] = h[c]` on the cells left standing.
        // That looked like the fix and was measurably the opposite, because `w` IS `_fillTable` and TWO passes
        // were then deciding one thing:
        //   • the LAKE funnel admits a depression on `fill − g >= BASIN_MIN_DEPTH` (2.5 m), so pre-lowering a
        //     basin to ~1 m deleted it from that funnel outright — measured seed 530442569 n=4097, lowland
        //     depressions 1,447 → 710 and placed bodies 266 → 96;
        //   • the MARSH funnel reads the same table expecting the raw priority-flood result ("fill[i] IS the
        //     level cell i's depression fills to before spilling"), so an island at `w == h` stopped being
        //     holdable and `median(holdable) − LEVEL_UNDER_SPILL` landed BELOW the sheet that had already been
        //     laid — 21 regions failed holdable<15% and 25 failed floods<25%, leaving marsh = 1.
        // So a basin was removed from the lake path and rejected by the marsh path, and rendered as nothing:
        // still-water class cells 550,372 → 426,923 with swamp only 373 → 2,245. The water was not made
        // shallow, it was made absent.
        //
        // Now hydrology answers only the question hydrology can answer — IS THIS GROUND A WETLAND — and the
        // planner's marsh funnel owns the level, which it already solves properly (MARSH_SUBMERGE_PCT against
        // the region's own height distribution, spill-bounded off the fill table, three suitability tests).
        // The shallow sheet and its hummocks then come out of the marsh body and its bed shaper, which is the
        // pass built for exactly that, instead of being pre-empted here.
        //
        // ⚠ Two other consumers were silently reading the doctored table and are fixed by the same removal:
        // InciseRivers takes `_fillTable` as its working water, and AnalyzeDrainage's ROUTE VALIDATION tests
        // that fill is monotone along a trace — `w[c] = h[c]` on islands made the fill rise and fall inside a
        // single basin, which is precisely the divide-crossing signature that test truncates rivers on.
        static int ClassifyWetlands(float[] h, float[] w, int n, float cellM, out long claimedCells)
        {
            var seen = new System.Collections.BitArray(n * n);
            var q = new Queue<int>();
            var comp = new List<int>();
            int converted = 0; claimedCells = 0;
            _wetlandMask = new bool[n * n];   // hydrology's output for the biome pass — see ApplyWetlandBiome

            for (int i0 = 0; i0 < n * n; i0++)
            {
                if (seen[i0] || !(h[i0] > SEA && w[i0] > h[i0] + 0.5f)) continue;
                comp.Clear(); q.Clear(); q.Enqueue(i0); seen[i0] = true;
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); comp.Add(c);
                    int cx = c % n, cz = c / n;
                    for (int e = 0; e < 4; e++)
                    {
                        int nx = cx + RIV_DX[e], nz = cz + RIV_DZ[e];
                        if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                        int j = nz * n + nx;
                        if (seen[j] || !(h[j] > SEA && w[j] > h[j] + 0.5f)) continue;
                        seen[j] = true; q.Enqueue(j);
                    }
                }
                if (comp.Count * cellM * cellM < WETLAND_MIN_AREA_M2) continue;   // area, not cells — see WETLAND_MIN_AREA_M2

                // mean |grade| of the floor — the classifier
                double gsum = 0; int gn = 0;
                for (int k = 0; k < comp.Count; k++)
                {
                    int c = comp[k]; int cx = c % n, cz = c / n;
                    if (cx < 1 || cx >= n - 1 || cz < 1 || cz >= n - 1) continue;
                    float gx = (h[c + 1] - h[c - 1]) / (2f * cellM);
                    float gz = (h[c + n] - h[c - n]) / (2f * cellM);
                    gsum += Math.Sqrt(gx * gx + gz * gz); gn++;
                }
                float grade = gn > 0 ? (float)(gsum / gn) : 0f;
                if (grade >= WETLAND_MAX_GRADE) continue;      // a steep bowl — a real lake, leave it full

                // ⚠ TIER B — DEEP IS A LAKE WHATEVER ITS FLOOR LOOKS LIKE. Grade alone is not enough: a big
                // lake can have a perfectly flat floor (rift and glacial basins do), so the steepness test
                // passes it through and we drain a real lake into a marsh. Measured with grade alone, seed
                // 530442569's largest body went from 10.20 km² to 2.62 km² and the world fell to 10.7% water,
                // under the target — we were not fixing depth, we were deleting lakes.
                double fsum = 0;
                for (int k = 0; k < comp.Count; k++) fsum += h[comp[k]];
                float meanDepth = w[i0] - (float)(fsum / comp.Count);
                if (meanDepth >= WETLAND_MAX_MEAN_DEPTH) continue;   // deep water is a lake, flat floor or not

                // ⚠ THE WHOLE BASIN IS WETLAND, ISLANDS INCLUDED. Marking only the cells that end up wet
                // shreds each marsh into fragments separated by its own hummocks, and the planner needs a
                // CONTIGUOUS swamp-biome region of MARSH_MIN_CELLS — so marking wet-only gave `marsh 0` from
                // 679 converted basins. A swamp is wet ground WITH hummocks in it; the islands are part of
                // the marsh, not holes in it. Which cells finally stand above the sheet is decided later, by
                // the marsh level, and it is not this pass's business.
                for (int k = 0; k < comp.Count; k++) _wetlandMask[comp[k]] = true;
                claimedCells += comp.Count;
                converted++;
            }
            return converted;
        }

        static float RunoffAt(float ground, float wx, float wz, int seed)
        {
            float above = Math.Max(0f, ground - SEA);
            // OROGRAPHIC LIFT — air forced up over high ground cools and rains out.
            float precip = 1f + above * OROGRAPHIC_PER_M;
            // Regional wetness: one slow field, rainforest ↔ desert.
            float wet = 0.35f + 1.30f * Fbm(wx, wz, 1f / 9000f, 3, seed + 613);
            // EVAPORATION roughly doubles per +10 °C (saturation vapour pressure). Warm lowlands bake dry;
            // cold highlands and poles keep nearly everything that falls on them.
            float tC = FiresClimate.SampleT(wx, wz);
            float evap = EVAP_REF * (float)Math.Pow(2.0, (tC - EVAP_REF_C) / 10.0);
            return Math.Max(RUNOFF_MIN, (precip * wet - evap) * RUNOFF_SCALE);
        }

        // (v129) POTHOLE FILL — take the noise depressions out of the TERRAIN before any water logic sees them.
        //
        // Measured on seed 530442569 (BASIN SIZE HISTOGRAM): the land carries 47,313 closed basins, and basins of
        // 64 cells or fewer are **90.7% of all of them while holding 1.8% of the filled area**. The 423 basins
        // over 4096 cells hold 87.9%. The small ones are not ponds — the histogram's depth column shows 1-cell
        // basins 32 m deep and 2-4 cell basins 86 m deep, which is a shaft, not a hollow. Nothing in a real
        // landscape is 4 m² across and 85 m deep.
        //
        // Leaving them in has a specific cost, and it is not cosmetic: every one is standing water as far as the
        // router is concerned, so a derived outlet run terminates at the first one it meets (correctly — a river
        // does not cross a lake) and inland rivers die within a few cells of their source. That is why river
        // cells collapsed to 5,864 against 248,256 of lake once runs stopped crossing water.
        //
        // Raising the ground to the spill level is the standard DEM treatment and is exactly what the fill
        // already computed: `w` IS the level at which that hollow would overflow, so h = w removes the
        // depression without inventing a height. Real basins are untouched.
        static int FillSmallDepressions(float[] h, float[] w, int n, int maxCells, out long cellsRaised)
        {
            var seen = new System.Collections.BitArray(n * n);
            var q = new Queue<int>();
            var comp = new List<int>();
            int filled = 0; cellsRaised = 0;
            for (int i0 = 0; i0 < n * n; i0++)
            {
                if (seen[i0] || !(h[i0] > SEA && w[i0] > h[i0] + 0.5f)) continue;
                comp.Clear();
                q.Clear(); q.Enqueue(i0); seen[i0] = true;
                bool tooBig = false;
                while (q.Count > 0)
                {
                    int c = q.Dequeue();
                    if (!tooBig) { comp.Add(c); if (comp.Count > maxCells) tooBig = true; }
                    int cx = c % n, cz = c / n;
                    for (int e = 0; e < 4; e++)
                    {
                        int nx = cx + RIV_DX[e], nz = cz + RIV_DZ[e];
                        if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                        int j = nz * n + nx;
                        if (seen[j] || !(h[j] > SEA && w[j] > h[j] + 0.5f)) continue;
                        seen[j] = true; q.Enqueue(j);
                    }
                }
                if (tooBig) continue;                       // a real basin — leave it as water
                for (int k = 0; k < comp.Count; k++) { h[comp[k]] = w[comp[k]]; cellsRaised++; }
                filled++;
            }
            return filled;
        }

        // (v128) FLAT RESOLUTION — the fix the straight-line bundles actually needed.
        //
        // A priority-flood fill gives every cell in a depression the SAME spill elevation. Inside such a flat
        // there is no local information at all: every neighbour is equal, so steepest descent — by raw drop, by
        // drop/distance, or by central-difference gradient — is reading a field that says nothing. Two earlier
        // attempts failed here for exactly that reason, and the tell was in the picture: the artifact is not one
        // wandering staircase (which is what D8 quantisation gives a SINGLE river) but BUNDLES OF PARALLEL
        // straight lines — many separate outlet walks in one basin all inheriting the same degenerate answer.
        //
        // Local comparison cannot fix this; the flat has to be crossed as a whole. The standard treatment builds
        // a gradient over the flat from its outlet and its inflow edges. Doing that globally would mean another
        // n² field — 440 MB at 10501, on a carve that has hit OOM before — so it is done ON DEMAND instead:
        // when a walk stalls, breadth-first search the connected flat for the nearest genuinely lower cell and
        // return the path to it. BFS yields the SHORTEST way out by construction, which is also why this answers
        // "rivers run parallel to the shore instead of dumping into it" — on a flat coastal plain the nearest
        // exit is the sea, not a traverse along it.
        //
        // Returns the path (exclusive of `start`, inclusive of the exit cell), or null if the flat is closed —
        // a genuinely endorheic basin, where refusing to invent an outlet is the correct answer.
        static List<int> EscapeFlat(float[] w, int n, int start, int maxVisit)
        {
            float lvl = w[start];
            // ⚠ MUST EXCEED THE FLOOD'S EPSILON. PriorityFloodFill forces every expanded cell to at least
            // `parent + 0.0002`, so a filled basin is a planar ramp rather than a level surface. A tolerance
            // below that epsilon reads the ramp as real descent and this routine exits after one cell, which is
            // exactly why it reported "0 flat(s) crossed" while the world was full of straight channels. 5 mm is
            // 25x the epsilon and still under any terrain worth calling sloped (0.25% over a 2 m cell).
            const float EPS = FILL_EPS_FLAT;
            var parent = new Dictionary<int, int>(2048) { [start] = -1 };
            var q = new Queue<int>();
            q.Enqueue(start);
            int visited = 0;
            while (q.Count > 0 && visited < maxVisit)
            {
                int c = q.Dequeue(); visited++;
                int cx = c % n, cz = c / n;
                for (int e = 0; e < 8; e++)
                {
                    int nx = cx + RIV_DX[e], nz = cz + RIV_DZ[e];
                    if (nx < 1 || nx >= n - 1 || nz < 1 || nz >= n - 1) continue;
                    int j = nz * n + nx;
                    if (parent.ContainsKey(j)) continue;
                    float wj = w[j];
                    if (wj < lvl - EPS)                     // a real way down — done
                    {
                        parent[j] = c;
                        var path = new List<int>();
                        for (int t = j; t != start && t != -1; t = parent[t]) path.Add(t);
                        path.Reverse();
                        return path;
                    }
                    if (wj <= lvl + EPS) { parent[j] = c; q.Enqueue(j); }   // still on the flat
                }
            }
            return null;
        }

        // (v128) THE RIVER ENDS AT THE WATERLINE — measured, not assumed.
        //
        // On seed 530442569, decoded straight out of the shipped artifact, 220,643 of 549,837 river cells —
        // **40.13%** — sat on ground at or below SEA. Those are river markings inside the ocean: rasterized,
        // carved, banked and saved, then hidden at render time by the v126 cull. Hiding it was never enough,
        // because the CARVE already happened: what the player finds at a river mouth is a dry trench and a
        // raised rim running out under the water (the user's "messed up terrain along their paths"), and the
        // world file carries a third of a million cells of river that nothing will ever draw.
        //
        // v126/v127 established the right ownership test — `hOrig <= SEA` means the sea owns this column — but
        // applied it only to the bank FEATHER and the sheet CULL, both downstream of the damage. Applying it to
        // the PATH is what actually stops the data existing.
        //
        // Orientation-agnostic on purpose: plan connections run source→mouth, derived outlets run body→sea, and
        // cascade splitting can reverse or fragment either. So rather than trusting node order, keep the longest
        // contiguous ABOVE-SEA span and extend it by exactly one node at whichever end touches the sea — that
        // node is the mouth, pinned at the waterline, and the run stops there.
        static void TruncateRunsAtSea(List<float[]> polys, float[] hOrig, int n, float cellM, float rhalf)
        {
            if (polys == null) return;
            int cutRuns = 0, droppedRuns = 0, cutNodes = 0;
            for (int p = polys.Count - 1; p >= 0; p--)
            {
                var poly = polys[p];
                int cnt = poly.Length / 4;
                if (cnt < 2) continue;

                bool Land(int k)
                {
                    int cx = Mathf.Clamp((int)((poly[k * 4] + rhalf) / cellM + 0.5f), 0, n - 1);
                    int cz = Mathf.Clamp((int)((poly[k * 4 + 1] + rhalf) / cellM + 0.5f), 0, n - 1);
                    return hOrig[cz * n + cx] > SEA;
                }

                // longest contiguous run of land nodes
                int bestS = -1, bestL = 0, curS = -1, curL = 0;
                for (int k = 0; k < cnt; k++)
                {
                    if (Land(k)) { if (curL == 0) curS = k; curL++; if (curL > bestL) { bestL = curL; bestS = curS; } }
                    else curL = 0;
                }
                if (bestL == 0) { polys.RemoveAt(p); droppedRuns++; continue; }   // entirely at/below sea — it is not a river
                int s = bestS, e = bestS + bestL - 1;
                if (s > 0) s--;            // one sub-sea node = the mouth, so the run actually REACHES the water
                if (e < cnt - 1) e++;
                if (s == 0 && e == cnt - 1) continue;                            // nothing below sea — untouched

                int keep = e - s + 1;
                if (keep < 4) { polys.RemoveAt(p); droppedRuns++; continue; }     // stub left over — not worth a channel
                var cut = new float[keep * 4];
                Array.Copy(poly, s * 4, cut, 0, keep * 4);
                polys[p] = cut;
                cutRuns++; cutNodes += cnt - keep;
            }
            if (cutRuns > 0 || droppedRuns > 0)
                FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: COAST TRUNCATION — {cutRuns} run(s) cut back to the waterline ({cutNodes} node(s) below SEA dropped), {droppedRuns} run(s) removed as entirely sub-sea. Nothing is carved or saved seaward of a mouth.");
        }

        // Pre-carve ground at a world-cell position (nearest). The bank is just the terrain at the CHANNEL EDGE we
        // already define by the carve width — no marching/searching for it (we know exactly how wide we cut).
        private static float SampleHOrig(float[] hOrig, int n, float cx, float cz)
        {
            int ix = Mathf.Clamp((int)(cx + 0.5f), 0, n - 1), iz = Mathf.Clamp((int)(cz + 0.5f), 0, n - 1);
            return hOrig[iz * n + ix];
        }

        // BASIN CENSUS — is this basin a LAKE or a WETLAND? Read-only; changes nothing. Spec:
        // docs/WETLANDS_AND_LAKES.md. Exists because two thresholds picked by intuition failed outright this
        // session (a 60 m flat-crossing limit that fired zero times; a 6 m outlet breach that carved 300k cells
        // for no measurable gain), so this one gets read off the distribution instead of guessed.
        //
        // THE PHYSICS: a lake holds at its spill sill when catchment runoff exceeds evaporation off its own
        // surface. Big catchment into a small basin overflows and stays full — a real lake. Small catchment
        // spread over a broad basin evaporates down to a shallow sheet — a marsh. So the discriminator is a
        // RATIO, and both halves are already in hand: `acc1` counts the cells draining through a point, and the
        // basin's own cell count is its area. Because both are in cells the ratio is dimensionless:
        //
        //      catchment:area  =  max(acc1 over the basin)  /  basin cell count
        //
        // FLOOR FLATNESS is the second axis, and it decides whether a basin CAN be a wetland at all. Fill a
        // flat floor 1 m deep and it still covers nearly all of itself — a broad shallow sheet, which is the
        // thing we want. Fill a steep bowl 1 m deep and it covers only its centre; that basin is a lake by
        // geometry no matter what its catchment says.
        static void ReportBasinCensus(float[] h, float[] w, float[] acc1, int n, float cellM)
        {
            var seen = new System.Collections.BitArray(n * n);
            var q = new Queue<int>();
            var comp = new List<int>();
            // per-basin: cells, ratio, mean floor grade, max depth, surface level
            var bCells = new List<int>(); var bRatio = new List<float>();
            var bGrade = new List<float>(); var bDepth = new List<float>(); var bLevel = new List<float>();
            long totalWet = 0;

            for (int i0 = 0; i0 < n * n; i0++)
            {
                if (seen[i0] || !(h[i0] > SEA && w[i0] > h[i0] + 0.5f)) continue;
                comp.Clear(); q.Clear(); q.Enqueue(i0); seen[i0] = true;
                float maxAcc = 0f, maxDep = 0f, lvl = w[i0];
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); comp.Add(c);
                    if (acc1[c] > maxAcc) maxAcc = acc1[c];
                    float d = w[c] - h[c]; if (d > maxDep) maxDep = d;
                    int cx = c % n, cz = c / n;
                    for (int e = 0; e < 4; e++)
                    {
                        int nx = cx + RIV_DX[e], nz = cz + RIV_DZ[e];
                        if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                        int j = nz * n + nx;
                        if (seen[j] || !(h[j] > SEA && w[j] > h[j] + 0.5f)) continue;
                        seen[j] = true; q.Enqueue(j);
                    }
                }
                if (comp.Count < 64) continue;                    // the pothole pass owns these
                // mean |grade| of the basin FLOOR — central differences on the real ground
                double gsum = 0; int gn = 0;
                for (int k = 0; k < comp.Count; k++)
                {
                    int c = comp[k]; int cx = c % n, cz = c / n;
                    if (cx < 1 || cx >= n - 1 || cz < 1 || cz >= n - 1) continue;
                    float gx = (h[c + 1] - h[c - 1]) / (2f * cellM);
                    float gz = (h[c + n] - h[c - n]) / (2f * cellM);
                    gsum += Math.Sqrt(gx * gx + gz * gz); gn++;
                }
                bCells.Add(comp.Count);
                bRatio.Add(maxAcc / Math.Max(1, comp.Count));
                bGrade.Add(gn > 0 ? (float)(gsum / gn) : 0f);
                bDepth.Add(maxDep); bLevel.Add(lvl);
                totalWet += comp.Count;
            }
            if (bCells.Count == 0) { FiresLogger.LogInfo("[FiresWorldGen]  │  BASIN CENSUS — no basins above the pothole threshold."); return; }

            // ── the discriminator's distribution ──
            float[] rEdge = { 0.5f, 1f, 2f, 5f, 10f, 25f, 100f, float.MaxValue };
            var rN = new int[rEdge.Length]; var rArea = new long[rEdge.Length];
            for (int i = 0; i < bCells.Count; i++)
            { int b = 0; while (b < rEdge.Length - 1 && bRatio[i] > rEdge[b]) b++; rN[b]++; rArea[b] += bCells[i]; }
            var sb = new System.Text.StringBuilder($"[FiresWorldGen]  │  BASIN CENSUS — {bCells.Count:N0} basin(s) >64 cells holding {totalWet:N0} cells."
                   + "\n      CATCHMENT:AREA (the lake-vs-wetland discriminator) | basins | % basins | % of flooded area");
            float lo = 0f;
            for (int b = 0; b < rEdge.Length; b++)
            {
                if (rN[b] == 0) { lo = rEdge[b]; continue; }
                string hi = rEdge[b] == float.MaxValue ? "+" : rEdge[b].ToString("0");
                sb.Append($"\n      {lo,6:0.0}-{hi,-5} | {rN[b],7:N0} | {100.0 * rN[b] / bCells.Count,5:0.0}% | {100.0 * rArea[b] / Math.Max(1, totalWet),5:0.0}%");
                lo = rEdge[b];
            }
            FiresLogger.LogInfo(sb.ToString());

            // ── the second axis: can this basin physically BE a wetland? ──
            var fb = new System.Text.StringBuilder("[FiresWorldGen]  │  FLOOR FLATNESS (mean grade of basin floor — flat floors can hold a broad shallow sheet, bowls cannot):"
                   + "\n      grade      | basins | % of flooded area | mean depth");
            float[] gEdge = { 0.02f, 0.05f, 0.10f, 0.20f, float.MaxValue };
            var gN = new int[gEdge.Length]; var gA = new long[gEdge.Length]; var gD = new double[gEdge.Length];
            for (int i = 0; i < bCells.Count; i++)
            { int b = 0; while (b < gEdge.Length - 1 && bGrade[i] > gEdge[b]) b++; gN[b]++; gA[b] += bCells[i]; gD[b] += bDepth[i]; }
            float glo = 0f;
            for (int b = 0; b < gEdge.Length; b++)
            {
                if (gN[b] == 0) { glo = gEdge[b]; continue; }
                string ghi = gEdge[b] == float.MaxValue ? "+" : gEdge[b].ToString("0.00");
                fb.Append($"\n      {glo,5:0.00}-{ghi,-5} | {gN[b],6:N0} | {100.0 * gA[b] / Math.Max(1, totalWet),5:0.0}% | {gD[b] / gN[b],6:0.0} m");
                glo = gEdge[b];
            }
            FiresLogger.LogInfo(fb.ToString());

            // ── what each candidate threshold would actually COST, which is the number that decides it ──
            var kb = new System.Text.StringBuilder("[FiresWorldGen]  │  IF WE SPLIT AT catchment:area = X, this many basins stay TRUE LAKES (the rest become wetland):"
                   + "\n      X      | lakes kept | % of basins | % of flooded area kept as lake");
            foreach (float t in new[] { 1f, 2f, 5f, 10f, 25f })
            {
                int keep = 0; long keepA = 0;
                for (int i = 0; i < bCells.Count; i++) if (bRatio[i] >= t) { keep++; keepA += bCells[i]; }
                kb.Append($"\n      {t,5:0} | {keep,10:N0} | {100.0 * keep / bCells.Count,10:0.0}% | {100.0 * keepA / Math.Max(1, totalWet),5:0.0}%");
            }
            FiresLogger.LogInfo(kb.ToString());
        }
    }
}
