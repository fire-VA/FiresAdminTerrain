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
    // ═══ WATER AUDITS — THE INSTRUMENTS, KEPT AWAY FROM THE RULES THEY MEASURE ═══════════════════════
    // Class-vs-field agreement, river/body connectivity, and the world water report.
    // ⚠ An audit that re-implements the rule it measures drifts the moment that rule moves: call the
    // authority instead of copying it. Several of these deliberately report a POPULATION and a worst-case
    // coordinate rather than a pass/fail, because a yes/no instrument silently eats "cannot tell".
    public static partial class FiresWorldGenBase
    {
        // (2026-08-11, user: "your studio reads are full of shit … the map overlay shows this lake extends to
        // where this river is, but it doesn't in reality" / "the studio needs to represent what's actually in
        // game, the actual levels the water sits at") CLASS-vs-FIELD TRUTH — run AFTER the fields are final
        // (_gridS/_waterS packed, _classS built), because those are what the game renders and queries from.
        // The world map + every WaterClassAt measurement read _classS, which StampBodyClasses fills from the
        // PLAN's body masks; the world itself draws from the 2m field via TryGetWaterSurfaceSmooth. Where the
        // two disagree the map paints water that does not exist — and any audit built on the class map
        // (including mine) is measuring a fiction. This reports the disagreement, both ways, with levels.
        public static void AuditClassVsField()
        {
            var cls = _classS;
            if (cls == null || _classN <= 0) { FiresLogger.LogInfo("[FiresWaterDiag] CLASS vs FIELD: no class grid."); return; }
            int n4 = _classN; float c4 = _classCell;
            int classWater = 0, fieldDry = 0, stillDry = 0, riverDry = 0;
            int swampClass = 0, swampDry = 0;   // marsh truth: planned-and-classed vs actually WET in the rendered field
            double lvlErrSum = 0; int lvlErrN = 0; float lvlErrMax = 0;
            var samp = new List<(float wx, float wz, byte c)>();
            for (int z = 0; z < n4; z++)
                for (int x = 0; x < n4; x++)
                {
                    byte c = cls[z * n4 + x];
                    if (c == WC_NONE || c == WC_OCEAN) continue;
                    classWater++;
                    if (c == WC_SWAMP) swampClass++;   // marshes are judged on what RENDERS, never on the plan's placed count
                    float wx = -_half + (x + 0.5f) * c4, wz = -_half + (z + 0.5f) * c4;
                    if (!TryGetWaterSurfaceSmoothReal(wx, wz, out float fieldLvl, out _))
                    {
                        fieldDry++;
                        if (c == WC_RIVER || c == WC_STREAM || c == WC_WATERFALL) riverDry++; else stillDry++;
                        if (c == WC_SWAMP) swampDry++;
                        if (samp.Count < 8) samp.Add((wx, wz, c));
                        continue;
                    }
                    // the class grid's own surface (what the map/labels believe) vs the field the world draws
                    float surfLvl = SampleSurfaceLevel(wx, wz);
                    if (surfLvl > -9000f)
                    {
                        float e = Math.Abs(surfLvl - fieldLvl);
                        lvlErrSum += e; lvlErrN++; if (e > lvlErrMax) lvlErrMax = e;
                    }
                }
            var sb = new System.Text.StringBuilder();
            foreach (var e in samp) sb.Append($"\n    class {e.c} at ({e.wx:0},{e.wz:0}) — field says DRY");
            string lvlPart = lvlErrN > 0 ? $" Level agreement over {lvlErrN:N0} wet cells: mean |Δ| {lvlErrSum / lvlErrN:0.00}m, worst {lvlErrMax:0.00}m." : "";
            // MARSH TRUTH (user 2026-08-13: "we cannot use just the studio's data for detecting where actual
            // water bodies lie … in-game water bodies tend to be SMALLER than what's shown on the map"). The
            // planner's "placed marsh N" is a PLAN count and says nothing about what the world draws — a marsh
            // can be planned and render as almost nothing. This is the number marsh work is judged on.
            string marshPart = swampClass > 0
                ? $" MARSH: {swampClass:N0} swamp-classed cells, {swampDry:N0} DRY in the field ({100f * swampDry / swampClass:0.0}% of the marsh the map paints does not exist in game), {swampClass - swampDry:N0} actually wet."
                : " MARSH: no swamp-classed cells at all.";
            FiresLogger.LogInfo($"[FiresWaterDiag] CLASS vs FIELD: {classWater:N0} water-classed 8m cells, {fieldDry:N0} DRY in the field the world renders ({stillDry:N0} lake/pond/still, {riverDry:N0} river) — that gap is what the map overlay paints but the world does not.{marshPart}{lvlPart}{sb}");
        }

        // The reconciled surface level the class/label grid carries at a world position (−9999 when none).
        private static float SampleSurfaceLevel(float wx, float wz)
        {
            var s = _surfS;
            if (s == null || _classN <= 0) return -9999f;
            int x = Mathf.Clamp(Mathf.FloorToInt((wx + _half) / _classCell), 0, _classN - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt((wz + _half) / _classCell), 0, _classN - 1);
            short v = s[z * _classN + x];
            return v == SURF_CUT ? -9999f : v * UNPACK;
        }

        private static void AuditRiverBodyConnect()
        {
            FiresLogger.LogInfo($"[FiresWaterDiag] CANYON FLOOR — {s_canyonShelfCells:N0} cell(s) cut to a walkable shelf: bowl | {CANYON_SHELF_M:0.0} m flat at {CANYON_FLOOR_UP:0.0} m above the water | cliff at {CANYON_CLIFF_GRADE:0}:1. Fires only where the thalweg's own ground stands {CANYON_SLOT_M:0} m above the water - the same test IsCanyonWaterAt uses, so the shaping and the in-game label cannot drift - and it only ever LOWERS, so no working river can move. 0 here means no reach in this world is that deeply incised.");
            FiresLogger.LogInfo($"[FiresWaterDiag] CANYON EARNED?: cells cut >20m by reach width — brook(<5m) {s_deepBrook:N0} · stream(5-10m) {s_deepStream:N0} · river(10-15m) {s_deepRiver:N0} · trunk(>15m) {s_deepTrunk:N0}. A canyon cut by a brook is not earned; one cut by a trunk is.");
            // (TRACE 2026-08-11, user: "the river just stops abruptly even tho the river bank is carved out and
            // you can clearly see where it's meant to meet the ocean") ESTUARY AUDIT — coastal channel cells
            // (carved river mask, ground at/below SEA+2) that carry NO water. A dry carved trench running into
            // the sea is the mouth reach failing, which is a different defect from the inland breaks.
            {
                var mm2 = _riverMask; var ww2 = _waterLevel; var gg2 = _grid;
                if (mm2 != null && ww2 != null && gg2 != null)
                {
                    int n3 = _res; float c3 = (2f * _half) / (n3 - 1);
                    int coastal = 0, coastalDry = 0;
                    var samp = new List<(float wx, float wz, float g, float w)>();
                    for (int i = 0; i < n3 * n3; i++)
                    {
                        byte k = mm2[i];
                        if (k != 1 && k != 3) continue;
                        if (gg2[i] > SEA + 2f) continue;              // not a coastal/estuary cell
                        coastal++;
                        if (ww2[i] > gg2[i] + 0.05f) continue;        // wet — fine
                        coastalDry++;
                        if (samp.Count < 8) samp.Add(((i % n3) * c3 - _half, (i / n3) * c3 - _half, gg2[i], ww2[i]));
                    }
                    var sb2 = new System.Text.StringBuilder();
                    foreach (var e in samp) sb2.Append($"\n    dry mouth cell ({e.wx:0},{e.wz:0}) ground {e.g:0.0} water {e.w:0.0}");
                    FiresLogger.LogInfo($"[FiresWaterDiag] ESTUARY AUDIT: {coastal:N0} coastal channel cells (ground ≤ SEA+2), {coastalDry:N0} of them DRY — a carved trench with no water running into the sea.{sb2}");
                }
            }
            // (TRACE) SURFACE STEPS — user: "multiple water levels rather than evening out and leveling off".
            // Scan every WET river cell against its WET river neighbours: a level jump between two touching wet
            // cells is a hard step in the sheet (the Dijkstra seedIdx gives each cell ONE stem's level, so two
            // stems' territories meet in a Voronoi boundary and the surface folds there).
            {
                var mm = _riverMask; var ww = _waterLevel; var gg = _grid;
                if (mm != null && ww != null && gg != null)
                {
                    int nn2 = _res; float cc = (2f * _half) / (nn2 - 1);
                    int step05 = 0, step1 = 0, step2 = 0; float worstD = 0f; int worstI = -1;
                    for (int z = 1; z < nn2 - 1; z++)
                        for (int x = 1; x < nn2 - 1; x++)
                        {
                            int i = z * nn2 + x;
                            byte k = mm[i];
                            if ((k != 1 && k != 3) || ww[i] <= gg[i] + 0.05f) continue;
                            for (int e = 0; e < 2; e++)
                            {
                                int j = e == 0 ? i + 1 : i + nn2;
                                byte k2 = mm[j];
                                if ((k2 != 1 && k2 != 3) || ww[j] <= gg[j] + 0.05f) continue;
                                float d = Math.Abs(ww[i] - ww[j]);
                                if (d > 0.5f) step05++;
                                if (d > 1f) step1++;
                                if (d > 2f) step2++;
                                if (d > worstD) { worstD = d; worstI = i; }
                            }
                        }
                    string loc = worstI >= 0 ? $" worst {worstD:0.0}m @({(worstI % nn2) * cc - _half:0},{(worstI / nn2) * cc - _half:0})" : "";
                    FiresLogger.LogInfo($"[FiresWaterDiag] RIVER SURFACE STEPS (adjacent WET river cells): >0.5m {step05:N0}, >1m {step1:N0}, >2m {step2:N0}.{loc}");
                }
            }
            s_boundHits = 0; s_dryCore = 0; s_resolvedRuns = 0; s_canyonShelfCells = 0; s_bermMergePoint = 0; s_shelfDeepCell = -1; s_shelfDeepSlot = 0f; s_shoreFilled = 0; s_bermEarned = 0; s_bermGratuitous = 0; s_anchorNodes = 0; s_anchorRuns = 0; s_anchorRunsNone = 0; s_runBothEnds = 0; s_runOneEnd = 0; s_runNoEnds = 0; s_runToOcean = 0; s_runFromSpring = 0; s_canyonNodes = 0; s_canyonMoved = 0;
            s_deepBrook = 0; s_deepStream = 0; s_deepRiver = 0; s_deepTrunk = 0;
            var plan = _plan; var m = _riverMask; var wv = _waterLevel; var gv = _grid;
            if (plan == null || m == null || wv == null || gv == null || plan.Bodies.Count == 0) return;
            int n = _res;
            int touching = 0, d10 = 0, d30 = 0, d60 = 0, d120 = 0, far = 0;
            int dryTouch = 0;   // body whose nearest CHANNEL cell touches but is DRY (carve bound held the bed above the level) — the "stops short" break
            var worst = new List<(float gap, float wx, float wz)>();
            float cell = (2f * _half) / (n - 1);
            // TRUE distance from each body's FOOTPRINT (not its bbox — a big lake's bbox spans open country and
            // made unrelated rivers read as "touching"). One multi-source BFS seeded from every footprint cell,
            // expanding outward; the first WET river cell a body's front reaches gives that body's real gap.
            const int MAXR = 60;                      // cells (~120m)
            var dist = new int[n * n];
            var src = new int[n * n];
            for (int i = 0; i < dist.Length; i++) { dist[i] = -1; src[i] = -1; }
            var q = new Queue<int>();
            for (int bi = 0; bi < plan.Bodies.Count; bi++)
            {
                var b = plan.Bodies[bi];
                for (int lz = 0; lz < b.H; lz++)
                    for (int lx = 0; lx < b.W; lx++)
                    {
                        if (b.Mask[lz * b.W + lx] == 0) continue;
                        int i = (b.Gz0 + lz) * n + (b.Gx0 + lx);
                        if (dist[i] < 0) { dist[i] = 0; src[i] = bi; q.Enqueue(i); }
                    }
            }
            var gap = new int[plan.Bodies.Count];
            var gapAt = new int[plan.Bodies.Count];
            for (int i = 0; i < gap.Length; i++) { gap[i] = int.MaxValue; gapAt[i] = -1; }
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int d = dist[i], bi2 = src[i];
                if (d > 0 && gap[bi2] == int.MaxValue)
                {
                    byte mk = m[i];
                    if ((mk == 1 || mk == 3) && wv[i] > gv[i] + 0.05f) { gap[bi2] = d; gapAt[bi2] = i; }
                }
                if (d >= MAXR) continue;
                int cx = i % n, cz = i / n;
                for (int e = 0; e < 4; e++)
                {
                    int nx = cx + (e == 0 ? 1 : e == 1 ? -1 : 0), nz = cz + (e == 2 ? 1 : e == 3 ? -1 : 0);
                    if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                    int j = nz * n + nx;
                    if (dist[j] >= 0) continue;
                    dist[j] = d + 1; src[j] = bi2; q.Enqueue(j);
                }
            }
            for (int bi = 0; bi < plan.Bodies.Count; bi++)
            {
                int gcell = gap[bi];
                if (gcell == int.MaxValue) { far++; continue; }
                float gm = gcell * cell;
                if (gcell <= 1) { touching++; continue; }
                if (gm <= 10f) d10++; else if (gm <= 30f) d30++; else if (gm <= 60f) d60++; else d120++;
                if (worst.Count < 12 && gapAt[bi] >= 0)
                    worst.Add((gm, (gapAt[bi] % n) * cell - _half, (gapAt[bi] / n) * cell - _half));
            }
            var wb = new System.Text.StringBuilder();
            foreach (var e in worst) wb.Append($"\n    river ~{e.gap:0}m from a body near ({e.wx:0},{e.wz:0})");
            FiresLogger.LogInfo($"[FiresWaterDiag] RIVER→BODY CONNECT (final field, WET river cells only, {plan.Bodies.Count} bodies): TOUCHING {touching} | wet river within 10m {d10}, 30m {d30}, 60m {d60}, 120m {d120} (= 'runs at the lake, stops short') | none within 120m {far} (fine — no inflow expected).{wb}");
        }

        // The exact cells the float audit flagged (1 = water standing above its lowest dry bank), so the studio
        // can RENDER them instead of quoting a number. OFF by default: at the game's 10501 grid this array is
        // ~110 MB, and the game gains nothing from it — the studio opts in.
        public static bool CaptureFloatMask;
        public static byte[] LastFloatMask;





        // FLAT-REGION AUDIT — find unnaturally flat ground and say WHERE it is.
        //
        // Diagnostic only. Added because a map render showed large flat grey polygons with straight edges at
        // the foot of mountains that no summary number revealed — the class tallies, altitude bands and basin
        // census all looked healthy while the terrain had visible plateaus in it. The first hypothesis (bank
        // feather merging at high channel density) was wrong: BuryMeshEdges reported byte-identical counts
        // across runs with 706, 2,696 and 19,155 polylines, so it does not scale with rivers at all.
        //
        // Rather than guess again, measure. A natural landscape has almost no LARGE regions of near-zero
        // gradient above sea level — a floodplain is gently sloped, a plateau is small relative to a continent.
        // Connected components of flat ground, reported with size and altitude, turn "those patches look wrong"
        // into a number that can be bisected against pipeline stages.
        // ⚠ OFF BY DEFAULT — set FIRES_FLAT_AUDIT=1 to enable. This allocates an n²-bit visited set and runs a
        // flood fill over every smooth cell, on a pass that already holds several n² float arrays (priority
        // flood alone is ~1.4 GB at 10501). Left always-on it pushed generation into repeated hard exits with
        // no output — the same "exit -1 with an empty log" that looked transient earlier. A diagnostic must not
        // cost the thing it is diagnosing; it found what it was built for, so it stays available and idle.
        // ⚠ WHICH WRITE FLATTENED THIS CELL. InciseRivers mutates the grid from seven different places, so
        // "the carve did it" is not an answer — the first fix aimed at the valley shave on exactly that
        // reasoning and moved the number by 0.09 points. Stamped only when FIRES_FLAT_AUDIT=1 (one n² byte
        // grid), and reported as a breakdown under the flat audit so the guilty pass NAMES ITSELF.
        internal static byte[] LastWriteSrc;
        internal static readonly string[] WRITE_SRC_NAME =
        {
            "untouched by InciseRivers", "carve: valley shave (cone)", "carve: channel bed (chan)",
            "berm (wl + 0.6)", "channel min depth (w - 0.5)", "source pool", "bank rim (demand + rim)", "bank feather",
        };

        internal static void FlatRegionAudit(float[] h, int n, float cellM, string when, float[] wLvl = null)
        {
            if (Environment.GetEnvironmentVariable("FIRES_FLAT_AUDIT") != "1") return;
            const float FLAT_GRADE = 0.004f;      // ~0.4% — below any natural slope worth the name
            const int MIN_REPORT = 400;           // ignore genuinely small benches
            var seen = new System.Collections.BitArray(n * n);
            var q = new Queue<int>();
            var sizes = new List<int>(); var alts = new List<float>(); var cxs = new List<float>(); var czs = new List<float>();
            long flatCells = 0, landCells = 0;

            // ⚠ ROUGHNESS, NOT GRADIENT — and the distinction is the whole point. The first version of this
            // tested for near-zero SLOPE and reported almost nothing, while the map plainly showed large
            // featureless patches at the foot of the mountains. Hillshade responds to the gradient's VARIATION,
            // so a uniformly sloped surface shades uniformly and looks featureless WITHOUT being flat. What is
            // actually missing there is the fine detail, so the thing to measure is local roughness: how far a
            // cell sits from the average of its neighbours. Baked ±1.75 m detail gives every natural cell a
            // non-trivial value; a bilinearly upsampled field gives almost none.
            const float SMOOTH_REL = 0.02f;   // metres of local relief below which terrain has no detail left
            bool Flat(int i)
            {
                int x = i % n, z = i / n;
                if (x < 1 || x >= n - 1 || z < 1 || z >= n - 1) return false;
                if (h[i] <= SEA) return false;
                // ⚠ A SUBMERGED LAKE BED IS NOT AN ARTIFACT. Without this the audit counted any flat ground
                // above SEA — including the floor of a 273 m mountain lake, which is flat because the fill and
                // conform passes correctly made it so, and which no player will ever see. That put a 1.071 km²
                // "worst offender" at the top of the list and sent two fixes chasing it; both changed nothing,
                // because there was nothing wrong. Measure only ground that is actually DRY and visible.
                if (wLvl != null && wLvl[i] > h[i] + 0.05f) return false;
                float avg = 0.25f * (h[i + 1] + h[i - 1] + h[i + n] + h[i - n]);
                return Math.Abs(h[i] - avg) < SMOOTH_REL;
            }

            for (int i = 0; i < n * n; i++) if (h[i] > SEA) landCells++;
            for (int i0 = 0; i0 < n * n; i0++)
            {
                if (seen[i0] || !Flat(i0)) continue;
                int size = 0; double aSum = 0, cxSum = 0, czSum = 0;
                q.Clear(); q.Enqueue(i0); seen[i0] = true;
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); size++; aSum += h[c]; flatCells++;
                    cxSum += c % n; czSum += c / n;
                    int cx = c % n, cz = c / n;
                    for (int e = 0; e < 4; e++)
                    {
                        int nx = cx + RIV_DX[e], nz = cz + RIV_DZ[e];
                        if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                        int j = nz * n + nx;
                        if (seen[j] || !Flat(j)) continue;
                        seen[j] = true; q.Enqueue(j);
                    }
                }
                if (size >= MIN_REPORT) { sizes.Add(size); alts.Add((float)(aSum / size)); cxs.Add((float)(cxSum / size)); czs.Add((float)(czSum / size)); }
            }
            double cellA = cellM * cellM;
            if (sizes.Count == 0)
            {
                FiresLogger.LogInfo($"[FiresWorldGen]  │  FLAT AUDIT ({when}) — no flat region over {MIN_REPORT} cells. {100.0 * flatCells / Math.Max(1, landCells):0.00}% of land is flat.");
                return;
            }
            // biggest offenders first — those are the ones visible on a map
            var order = new List<int>();
            for (int i = 0; i < sizes.Count; i++) order.Add(i);
            order.Sort((a, b) => sizes[b].CompareTo(sizes[a]));
            var sb = new System.Text.StringBuilder($"[FiresWorldGen]  │  FLAT AUDIT ({when}) — {sizes.Count:N0} flat region(s) over {MIN_REPORT} cells; "
                   + $"{flatCells:N0} flat cell(s) = {100.0 * flatCells / Math.Max(1, landCells):0.00}% of all land. Largest:");
            for (int k = 0; k < Math.Min(6, order.Count); k++)
                sb.Append($"\n      {sizes[order[k]] * cellA / 1e6:0.000} km² centred ({cxs[order[k]] * cellM - (n - 1) * cellM * 0.5f:0},{czs[order[k]] * cellM - (n - 1) * cellM * 0.5f:0}) altitude {alts[order[k]]:0} m");
            // WHO FLATTENED IT — the breakdown that makes this audit actionable instead of merely alarming.
            if (LastWriteSrc != null && LastWriteSrc.Length == n * n)
            {
                var bySrc = new long[WRITE_SRC_NAME.Length];
                for (int i = 0; i < n * n; i++)
                    if (seen[i] && Flat(i)) { byte s = LastWriteSrc[i]; if (s < bySrc.Length) bySrc[s]++; }
                sb.Append("\n      flat cells BY THE PASS THAT LAST WROTE THEM:");
                for (int s = 0; s < bySrc.Length; s++)
                    if (bySrc[s] > 0)
                        sb.Append($"\n        {WRITE_SRC_NAME[s],-28} {bySrc[s],10:N0} ({100.0 * bySrc[s] / Math.Max(1, flatCells),5:0.0}% of flat land)");
            }
            FiresLogger.LogInfo(sb.ToString());
        }

        // ═══ CHANNEL INCISION CLASS — ordinary / canyon / gorge ═══════════════════════════════════════════
        // docs/CANYONS_AND_GORGES.md §1: these are NOT one feature at three sizes. Each is defined by its
        // formative process, and the process decides which query answers "does one belong here".
        //
        //   • CANYON (§1a) — a river cutting DOWN into rising ground, holding its course while the plateau
        //     lifts around it. So: sustained high ground standing above the thalweg, a river big enough to
        //     have held its line, and — close to a precondition — ARID country, because canyon walls stay
        //     sheer only where there is little rain, vegetation or chemical weathering to soften them.
        //     "A canyon in a rainforest biome is a modelling error" (§1a). It also wants BEDDED rock, because
        //     the stair-stepped walls that make a canyon read as a canyon come from hard layers capping soft.
        //   • GORGE (§1b) — narrower, steeper, shorter, and the distinguishing input is ROCK STRENGTH, not
        //     size: where a river crosses resistant bedrock it cannot widen, so it cuts a slot. This is the
        //     first consumer of FiresRock, and it is why that field had to exist before this pass.
        //
        // NOT ATTEMPTED HERE — water gaps and wind gaps (§1c). Trap 1 rules them out of a detection pass:
        // our drainage is solved by priority flood on the FINISHED heightfield, so every river we have
        // already avoids every ridge, and a water gap is by definition a river OLDER than the ridge it
        // crosses. Detecting one would find nothing. They have to be PLACED — decide a ridge crossing first
        // and let the drainage inherit it — which is a change to how terrain and drainage are ordered, not a
        // classification. Left for its own piece of work rather than faked here.
        // ── THE RIVER MASK'S VALUE SPACE ──────────────────────────────────────────────────────────────────
        // 0 = nothing · 1 = channel · 2 = bank margin · 3 = channel (written only by the IMPORT path's
        // CarveRivers; every gen-path test spells the channel question as `mk == 1 || mk == 3`) ·
        // 4 = STILL water that is not a planned body — currently the headwater SPRING PONDS.
        // ⚠ 4 IS DELIBERATELY OUTSIDE THE CHANNEL TESTS. That is the whole point: every existing
        // `mk == 1 || mk == 3` in this file is asking "is this a channel", and for a spring pond the honest
        // answer is no — so those tests need no edit and cannot accidentally treat a pond as a river. Only
        // the two places that ask "is there WATER here" had to learn about it: the final dry pass in
        // InciseRivers, and the class derivation in BuildWaterLabels.
        internal const byte MASK_STILL = 4;

        internal const byte INC_NONE = 0, INC_CANYON = 1, INC_GORGE = 2;

        // ── THE PROFILE EACH LANDFORM CUTS (docs/CANYONS_AND_GORGES.md §3 step 3) ────────────────────────
        // Multipliers on the reach's own valley grade (InclineFor(width), already discharge-keyed: 0.75 at a
        // 2 m brook down to 0.22 at a 20 m trunk). A steeper grade removes LESS material, so the channel sits
        // in a slot rather than a broad valley — which is the shape, and is also why this cannot float a
        // sheet: inside the wetted width the bed is set by `chan`, never by the wall.
        const float CANYON_WALL_MUL = 2.2f;   // sheer, but still a valley you could walk out of in places
        const float GORGE_WALL_MUL  = 3.4f;   // a slot: resistant rock the river could not widen (§1b)
        // Metres the wall is displaced between the softest and most resistant bed — the bench relief. Gorges
        // bench less: §1b's resistant rock is massive (granite/basalt), and FiresRock gives those a low
        // BeddingStrength precisely because they fail in columns rather than stepping.
        // ── EXCAVATION (docs/CANYONS_AND_GORGES.md §3b) ──────────────────────────────────────────────────
        // How deep the slot is dug and how wide it reaches. A canyon is broad enough to hold a floor and a
        // pool; a gorge is a slot the river could not widen (§1b), so it is deeper relative to its width.
        // ⚠ These are the first numbers in the generator that dig deeper than the river carve, so they are
        // the ones to re-measure with `gorgeprobe` (trap 3) — and with the CAVE rule the user set: a void
        // directly BELOW the floor must be sealed so the floor cannot collapse into a sinkhole, while
        // breaking into the SIDE of one is wanted (lagoons, and terrain worth exploring).
        // ═══ THE CANYON FLOOR SECTION (user, 2026-08-31) ══════════════════════════════════════════════
        // "a cliff wall, a meter or two of terrain, and THEN the carved depression of the river in the
        // center, another meter or two of terrain, then the cliff wall". CANYON_SLOT_M is deliberately the
        // same 12 m as IsCanyonWaterAt's WALL_M so the shaping and the in-game label share one definition.
        const float CANYON_SLOT_M     = 12f;   // thalweg ground this far above the water = a canyon
        const float CANYON_SHELF_M    = 2.0f;  // walkable ground each side of the channel
        const float CANYON_FLOOR_UP   = 0.6f;  // the shelf sits this far above the water, so it is dry land
        const float CANYON_CLIFF_GRADE = 4.0f; // rise per metre once past the shelf — a wall, not a ramp
        const float CANYON_DIG_M = 22f;   // floor this far below the reach it follows
        const float CANYON_DIG_R = 26f;   // ...over this reach, flat to 45% then a wall
        const float GORGE_DIG_M  = 16f;
        // (2026-08-20, user: "canyons need to carve out a bit wider to allow for an actual river to run
        // through the bottom of them, right now they tighten up quite a bit at the bottoms".) 11 -> 14 m.
        // The flat floor is FLOOR_FRAC of the reach on each side, so 11 m gave a 15 m floor — and a GORGE is
        // the common case here (9,044 classified nodes against a canyon's 3,515), so most of what reads as
        // canyon in game is this number. A river is up to WidthMax = 20 m, so a 15 m floor cannot hold one:
        // the channel had to climb its own walls. 14 m with the wider FLOOR_FRAC below gives ~22 m.
        const float GORGE_DIG_R  = 14f;
        const float CANYON_BENCH_M = 3.0f;
        const float GORGE_BENCH_M  = 1.2f;

        /// Studio overlay (see FiresWaterPlanner's float-mask precedent): the incision class per spine cell,
        /// so a render can SHOW where these landforms are instead of reporting a count nobody can check.
        internal static byte[] LastIncisionClass;

        // Thresholds. Every one is a physical statement, and each is logged in the census below so the
        // distribution can be read rather than guessed at.
        const float INC_RING_M     = 130f;   // radius at which "does high ground stand over this reach" is asked
        const float CANYON_RELIEF  = 26f;    // m the surrounding ground must stand above the thalweg
        const float GORGE_RELIEF   = 15f;    // a gorge is shorter and tighter — it needs less relief to read
        const float GORGE_HARDNESS = 0.66f;  // resistant bedrock: the defining input for a slot
        const float CANYON_BEDDING = 0.50f;  // below this the rock cannot bench, so it cannot be a canyon wall
        const float GORGE_SLOPE    = 0.10f;   // along-valley steepness that says the river is still cutting down
        const float CANYON_MOIST_MAX = 0.40f; // moisture ceiling for a canyon — read off the census below
        // Canyons are SITED by rank, not by an absolute bar — see CANYON SITING in ClassifyChannelIncision.
        // The top slice of the relief distribution along this world's own rivers, with a floor so a flat
        // world correctly gets none.
        const float CANYON_RELIEF_PCT = 0.985f;
        const float CANYON_RELIEF_FLOOR = 14f;
        // THE TWO CONSTANTS THAT MAKE THESE LANDFORMS RATHER THAN CONFETTI. The window is what the measures
        // are averaged over, and MIN_RUN_M is the length a reach must SUSTAIN the class for or be demoted —
        // measured necessity, not taste: without them the classifier produced 884 reaches averaging 24 m.
        const float INC_WINDOW_M = 400f;      // reach window the relief/rock/grade terms are averaged over
        // ⚠ THE TWO ARE NOT THE SAME LENGTH, AND §1b SAYS SO: a gorge is "narrower and steeper than a
        // canyon, and usually SHORTER". One shared sustain length was my own conflation, and it measured as
        // CANYON 0 GORGE 0 with every one of 283 stamped reaches demoted — including gorges of 100-320 m,
        // which are gorges at exactly the size a gorge comes in. A canyon has to be long because it is a
        // river holding its course across a rising plateau; a gorge is a short crossing of resistant rock.
        // ⚠ 400 m WAS UNREACHABLE ON THIS TERRAIN AND PRODUCED ZERO. With the relief bar cleared, 6,362
        // nodes classified canyon and then EVERY span was demoted for length — so the binding constraint
        // was never relief, it was that canyon-grade ground here comes in 200-300 m stretches. Earth's
        // canyons are longer because Earth has uplift; ours are sited on the best ground available, so the
        // sustain length has to be one this world can actually offer or the feature does not exist at all.
        const float CANYON_MIN_RUN_M = 250f;  // a canyon shorter than this is not a canyon
        const float GORGE_MIN_RUN_M  = 120f;  // ...but a gorge that long is already a big one

        // ⚠ ARIDITY COMES FROM THE MOISTURE FIELD, NOT FROM THE BIOME LABEL — and the first cut got this
        // wrong in a way worth recording. A biome lookup table looks like the cheap answer, but our biome
        // codes are not a moisture axis: `Mountain(3)` is assigned by ELEVATION (`elevA > MOUNT_START`) before
        // moisture is even consulted, so a LUT that scores Mountain "cold, therefore not arid" conflates
        // altitude with dryness — and canyon country is high ground BY DEFINITION (§1a: the plateau lifts
        // around the river). Measured: the LUT refused 3,721 of the 3,736 reaches that cleared the relief bar,
        // i.e. it rejected 99.6% of every candidate on a proxy error, and reported 15 canyons world-wide.
        // `FiresBiome3D.Classify` already returns the real moisture term as an out-parameter, so this needs no
        // new field, no duplicated formula (the moisture expression already exists twice in that file — a
        // third copy is how v102 happened) and no edit to the Burst-compiled `ClassifyCode` hot path.
        static float MoistureAt(float wx, float wz, int seed)
        {
            return FiresClimate.SampleM(wx, wz);
        }

    }
}
