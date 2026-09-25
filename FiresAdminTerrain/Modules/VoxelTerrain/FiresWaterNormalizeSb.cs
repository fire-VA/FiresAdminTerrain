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
    // ═══ CONTAINMENT AND BASIN NORMALISATION — the last word on a still surface ══════════════════════
    // ContainWaterField / ClampStillWaterFinal / NormalizeBasinWater: one flat level per basin, water held
    // under its banks, nothing left perched.
    // ⚠ These RAISE GROUND. Every raise here must ask the second question — what is on the FAR side of the
    // cell — or it builds a dam between two halves of one water surface. See the JUNCTION and NECK notes in
    // FiresWaterConform, which is where that lesson was finally paid for.
    public static partial class FiresWorldGenBase
    {
        // FINAL still-water containment — run LAST, over ALL still water (swamps, lakes, ponds, highland
        // lakes). Floods each connected still body, finds its lowest DRY/channel bank, levels the body at
        // (bank − FB); a cell whose bed then sits above the level dries (body shrinks, per user "smaller is
        // fine"). Rivers (channels) FLOW and are skipped. Drives the audit's inland still-water overflow to
        // ZERO by construction (user: "nail down the generation to never have a single overflow"). Swamps
        // were seeded at a 15th-pct rim (deliberately spilling); this re-contains them to the true rim.
        // GUARANTEED NO-FLOAT — the hard invariant (user, 2026-07-29): a NON-ocean water cell may NEVER
        // stand above the terrain of a DRY neighbour. Runs on the RAW 2m field the water MESH actually
        // samples (TryGetWaterSurfaceSmooth reads _waterS for wet cells), so containment is EXACT — full
        // resolution, no decimation, the real per-cell rim. Drain-to-contain: a floating wet cell is
        // settled just under the lowest DRY bank it borders (its own 2m neighbours), or DRIED if that
        // falls to its bed; lowering re-queues neighbours so a whole leaking pool settles together. The
        // ONLY exceptions: the sea datum (never lowered, never counted as a bank) and a genuine POUR — a
        // drop steeper than CLIFF in one 2m step (a waterfall/cliff face) which is allowed over its lip.
        // Levels only ever decrease → the work queue terminates.
        private static void ContainWaterField(float[] w, float[] g, byte[] mask, int n, string tag)
        {
            if (w == null || g == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int total = n * n;
            const float EPS = 0.01f, MARGIN = 0.05f, CLIFF = 8f;
            const int REACH = 4;                                        // scan banks out to REACH cells (~20m) — a flat sheet must not perch over a bank sloping away within this span
            bool WetAny(int k) => w[k] > g[k] + EPS;                    // any standing water (incl. the sea)
            bool Proc(int k) => w[k] > g[k] + EPS && w[k] > SEA + 0.2f; // inland water we may lower (never the sea plane)
            bool River(int k) => mask != null && (mask[k] == 1 || mask[k] == 3);   // a flowing channel — the ONLY water allowed to pour over a lip
            var bm = _biomeMap;
            bool Swamp(int k) => bm != null && k < bm.Length && bm[k] == 2;         // marsh — a raised shallow sheet; FILLED to bury its edge, not drained
            // Lowest DRY ground within REACH along the 4 axes — the bank the sheet at L would spill over.
            // Scanning outward STOPS at another water cell (that side is contained by connected water, not a
            // bank). A RIVER may pour past a CLIFF-steep drop (→ a waterfall, tagged later); STILL water
            // (pond/lake) never pours, so it is contained no matter how far the bank falls.
            float BankUnder(int i, float L)
            {
                int x = i % n, y = i / n; float cap = float.MaxValue;
                float cliff = River(i) ? CLIFF : float.MaxValue;
                void Ray(int dx, int dy)
                {
                    for (int s = 1; s <= REACH; s++)
                    {
                        int nx = x + dx * s, ny = y + dy * s;
                        if (nx < 0 || nx >= n || ny < 0 || ny >= n) break;
                        int j = ny * n + nx;
                        if (WetAny(j)) break;                            // connected water on this side — not a dry bank
                        float b = g[j];
                        if (L - b > cliff) break;                        // river lip — allowed to pour; still water: cliff=∞, always contained
                        // (v91) PROUD BANK — dry ground standing at/above the level CONTAINS this side: STOP
                        // (hydrostatics, docs/HYDROLOGY.md Law 1 — a connected section is level; only ground
                        // the water actually touches can spill it). The min-over-ray used to walk PAST the
                        // 0.84m bank and the wl+0.6 berm to open falling ground beyond, then shave individual
                        // channel cells — a tilted cross-section, physically impossible water (ran on every
                        // artifact-restore boot via RestoreFromArtifact). RayBankHeight always had this rule.
                        if (b >= L + 0.05f) break;
                        if (b < cap) cap = b;
                    }
                }
                Ray(1, 0); Ray(-1, 0); Ray(0, 1); Ray(0, -1);
                return cap;
            }
            // PASS 1 — STILL-WATER BODY FLOOD: settle each connected still-water body (pond/lake) to its
            // true lowest DRY rim, as ONE body — not a window. A "pond" stepping down a slope is connected
            // water the reach-scan can't drain (it stops at the wet cell below), but the flood finds the dry
            // ground at the body's edge and clamps the whole body under it (cells whose bed stands above the
            // rim fall dry). A bowl tarn keeps its level (its rim is above the water); only water that leaks
            // over a lower edge recedes. Rivers (flowing) are left for the reach pass.
            {
                var seen = new bool[total];
                var stack = new System.Collections.Generic.Stack<int>(1 << 16);
                var body = new System.Collections.Generic.List<int>(1 << 12);
                long bodies = 0, drainedCells = 0;
                for (int i0 = 0; i0 < total; i0++)
                {
                    if (seen[i0]) continue;
                    seen[i0] = true;
                    if (!Proc(i0) || River(i0) || Swamp(i0)) continue;   // swamps are filled (PASS 3), not drained
                    body.Clear(); stack.Clear(); stack.Push(i0);
                    float rim = float.MaxValue;
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop(); body.Add(i);
                        int x = i % n, y = i / n;
                        void Nb(int j)
                        {
                            if (Proc(j) && !River(j) && !Swamp(j)) { if (!seen[j]) { seen[j] = true; stack.Push(j); } }
                            else if (!WetAny(j) && g[j] < rim) rim = g[j];   // dry land on the border → a spill-rim candidate
                        }
                        if (x < n - 1) Nb(i + 1);
                        if (x > 0) Nb(i - 1);
                        if (y < n - 1) Nb(i + n);
                        if (y > 0) Nb(i - n);
                    }
                    if (rim == float.MaxValue) continue;                  // no dry rim (all water/edge) — leave it
                    float lvl = rim - MARGIN;                             // contained just under the rim (visible relief is applied LIVE at the surface, not baked)
                    for (int k = 0; k < body.Count; k++)
                    {
                        int i = body[k];
                        if (w[i] > lvl + 0.001f) { w[i] = g[i] >= lvl ? g[i] : lvl; drainedCells++; }   // clamp under the rim; dry where the bed stands above it
                    }
                    bodies++;
                }
                FiresLogger.LogInfo($"[FiresWorldGen] still-water body flood ({tag}): {bodies:N0} bodies settled to their lowest rim, {drainedCells:N0} cells lowered/dried.");
            }
            // PASS 2 — REACH SCAN: rivers + any residual, contained under the nearest dry bank in reach.
            var inq = new bool[total];
            var q = new System.Collections.Generic.Queue<int>(1 << 18);
            for (int i = 0; i < total; i++)
                if (Proc(i) && !Swamp(i) && BankUnder(i, w[i]) < w[i] - 0.5f) { q.Enqueue(i); inq[i] = true; }   // only genuine floaters (bank below the sheet)
            long lowered = 0, dried = 0, guard = 0, maxGuard = 64L * total;
            while (q.Count > 0 && guard++ < maxGuard)
            {
                int i = q.Dequeue(); inq[i] = false;
                if (!Proc(i)) continue;
                float L = w[i];
                float cap = BankUnder(i, L);
                if (cap == float.MaxValue || L <= cap + MARGIN) continue;   // contained — sits under every bank in reach
                float newL = cap - MARGIN;
                if (newL <= g[i] + EPS) { w[i] = g[i]; dried++; }            // fell to its own bed → dry land
                else { w[i] = newL; lowered++; }
                int x = i % n, y = i / n;
                void Push(int j) { if (!inq[j] && Proc(j) && !Swamp(j)) { inq[j] = true; q.Enqueue(j); } }   // a lowered cell can expose neighbours within reach
                for (int s = 1; s <= REACH; s++)
                {
                    if (x + s < n) Push(i + s); if (x - s >= 0) Push(i - s);
                    if (y + s < n) Push(i + s * n); if (y - s >= 0) Push(i - s * n);
                }
            }
            FiresLogger.LogInfo($"[FiresWorldGen] water field containment ({tag}): {lowered:N0} cells settled under their bank, {dried:N0} dried — 2m field, guaranteed no-float (guard {guard:N0}/{maxGuard:N0}); {sw.Elapsed.TotalSeconds:0.0}s.");
            // (Swamps are now generated as contained basins — SeedSwampPonds — so no post-hoc fill is needed;
            // the reach scan above catches any residual marsh edge locally.)
        }

        private static void ClampStillWaterFinal()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask; var b = _biomeMap;
            if (w == null || g == null || m == null) return;
            int n = _res; const float FB = 0.4f, CARVE_CAP = 4f;
            var seen = new bool[n * n];
            var stack = new System.Collections.Generic.Stack<int>(8192);
            var comp = new System.Collections.Generic.List<int>(8192);
            long totalChanged = 0; int passes = 0;
            for (int pass = 0; pass < 24; pass++)   // iterate: adjusting one body exposes a neighbour's new low bank
            {
            System.Array.Clear(seen, 0, seen.Length);
            long changed = 0;
            for (int start = 0; start < n * n; start++)
            {
                if (seen[start]) continue;
                if (w[start] <= g[start] + 0.01f || m[start] == 1 || m[start] == 3 || w[start] <= SEA + 0.2f) { seen[start] = true; continue; }
                comp.Clear(); stack.Clear(); stack.Push(start); seen[start] = true;
                float bank = float.MaxValue; bool bordersOcean = false; int swampN = 0;
                while (stack.Count > 0)
                {
                    int i = stack.Pop(); comp.Add(i);
                    if (b != null && b[i] == 2) swampN++;
                    int x = i % n, z = i / n;
                    Nb(i + 1, x + 1 < n); Nb(i - 1, x > 0); Nb(i + n, z + 1 < n); Nb(i - n, z > 0);
                    void Nb(int j, bool ok)
                    {
                        if (!ok) return;
                        bool jStill = w[j] > g[j] + 0.01f && m[j] != 1 && m[j] != 3;
                        if (jStill && g[j] <= SEA + 0.2f) { bordersOcean = true; return; }    // sea floor → a DRAIN, not a containing bank
                        if (jStill) { if (!seen[j]) { seen[j] = true; stack.Push(j); } return; }   // same inland body
                        float rim = (m[j] == 1 || m[j] == 3) ? w[j] : g[j];                   // dry ground / channel = a bank
                        if (rim < bank) bank = rim;
                    }
                }
                bool swampBody = b != null && swampN * 2 > comp.Count;
                if (swampBody) continue;                              // swamps handled at the terraform (SwampLevelPct); clamping them here fought the sheet + oscillated
                if (bank == float.MaxValue) continue;                 // no dry bank — leave it
                float level = bank - FB;                              // lake: sit BELOW every bank
                foreach (int i in comp)
                {
                    if (g[i] < level - 0.01f) { if (w[i] != level) { w[i] = level; changed++; } }              // below → flat sheet
                    else if (g[i] - level <= CARVE_CAP) { g[i] = level - 1f; w[i] = level; changed++; }        // carve to keep footprint
                    else { if (w[i] > g[i]) { w[i] = g[i]; changed++; } }                                      // proud bed → dry
                }
            }
            totalChanged += changed; passes = pass + 1;
            if (changed == 0) break;
            }
            if (totalChanged > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] still-water clamp (biome-aware): {passes} pass(es), {totalChanged:N0} cells adjusted — swamps clip/drain, lakes below banks, no inland overflow (rivers flow, skipped).");
        }

        // (SettleWater DELETED — CarveRivers' final water snap dries every
        // non-river cell to its bed, so the settle's pour fill lowered ZERO
        // cells on every world our pipeline can produce.)

        // BASIN WALK — cap to floor+6, leaky-rim clamp, and the COMPOSITION
        // pass (not every low spot holds water). Cheap (~5s), runs on EVERY
        // load: the settled cache stores basins pre-selection, and the
        // deterministic hash re-rolls identically per (seed, settings), so
        // the keep-chance knobs stay tunable without touching the cache.
        private static void NormalizeBasinWater()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask;
            if (w == null || g == null || m == null) return;
            int n = _res;
            // Class grid rides the same decimation the reconcile uses; the
            // walk stamps kept basins, the reconcile fills channels + sea.
            float ccm = (2f * _half) / (n - 1);
            int cstride = Mathf.Max(1, Mathf.RoundToInt(SURF_TARGET_CELL_M / ccm));
            int cn2 = (n + cstride - 1) / cstride;
            _classS = new byte[cn2 * cn2];
            _classN = cn2;
            _classCell = ccm * cstride;
            var seen = new bool[n * n];
            var stack = new Stack<int>(1024);
            var comp = new System.Collections.Generic.List<int>(4096);
            int capped = 0, comps = 0, dropped = 0, droppedComps = 0, smearFix = 0, cutRim = 0, bermFill = 0;
            int walked = 0, keptN = 0, ruleDrop = 0, rollDrop = 0, riverFedN = 0, leakDrop = 0;
            var outletSpill = new System.Collections.Generic.List<int>(64);
            var outletCap = new System.Collections.Generic.List<float>(64);
            int bLake = 0, bPond = 0, bSwampN = 0, bFroz = 0, bHot = 0;
            int rimCold = 0, rimSwamp = 0, rimOther = 0;
            float capHi = float.MinValue;
            for (int i0 = 0; i0 < n * n; i0++)
            {
                if (seen[i0] || w[i0] <= g[i0] + 0.01f) continue;
                comp.Clear();
                float floor = float.MaxValue;
                seen[i0] = true;
                stack.Push(i0);
                float rimMin = float.MaxValue;
                float rimMax = float.MinValue;
                int rimMinIdx = -1;
                bool riverFed = false;
                long csx = 0, csz = 0;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    comp.Add(i);
                    csx += i % n;
                    csz += i / n;
                    byte mw = m[i];
                    if (mw == 1 || mw == 3) riverFed = true;
                    else if (mw == 0 && g[i] < floor) floor = g[i];
                    int x = i % n, z = i / n;
                    if (x + 1 < n) { int j = i + 1; if (!seen[j] && w[j] > g[j] + 0.01f) { seen[j] = true; stack.Push(j); } else if (w[j] <= g[j] + 0.01f) { if (g[j] < rimMin) { rimMin = g[j]; rimMinIdx = j; } if (g[j] > rimMax) rimMax = g[j]; } }
                    if (x > 0) { int j = i - 1; if (!seen[j] && w[j] > g[j] + 0.01f) { seen[j] = true; stack.Push(j); } else if (w[j] <= g[j] + 0.01f) { if (g[j] < rimMin) { rimMin = g[j]; rimMinIdx = j; } if (g[j] > rimMax) rimMax = g[j]; } }
                    if (z + 1 < n) { int j = i + n; if (!seen[j] && w[j] > g[j] + 0.01f) { seen[j] = true; stack.Push(j); } else if (w[j] <= g[j] + 0.01f) { if (g[j] < rimMin) { rimMin = g[j]; rimMinIdx = j; } if (g[j] > rimMax) rimMax = g[j]; } }
                    if (z > 0) { int j = i - n; if (!seen[j] && w[j] > g[j] + 0.01f) { seen[j] = true; stack.Push(j); } else if (w[j] <= g[j] + 0.01f) { if (g[j] < rimMin) { rimMin = g[j]; rimMinIdx = j; } if (g[j] > rimMax) rimMax = g[j]; } }
                }
                if (floor == float.MaxValue) continue;   // river-only component
                // Centroid + seed lookup FIRST: seeded basins (tarns, lowland
                // lakes, swamp floods) keep their STAMPED level. The leaky-rim
                // clamp below reads the lowest dry cell touching the sheet —
                // on a flooded swamp region that's always some low outside
                // border cell, and rimMin−0.3 re-crushed every deliberately
                // placed sheet to a moat ring (log-proven: 24 regions sheeted,
                // 2,888 wet cells map-wide). Containment for seeded water is
                // the SEEDER's job (boundary clamp + berms), not the walk's.
                int cci = comp.Count > 0
                    ? (int)(csz / comp.Count) * n + (int)(csx / comp.Count) : i0;
                bool seededBasin = TryGetSeedLevel(cci % n, cci / n, out float seedLvl);
                float cap = Mathf.Max(floor + FiresGenTuning.LakeMaxDepth, SEA + 0.5f);
                // River-fed too: a river CROSSING a flooded swamp made the
                // component river-fed, and the rim clamp then drained the
                // whole sheet down to the channel's dried bench cells. The
                // river-fed flatten below only lowers cells above cap and
                // respects local ground, so holding cap at the stamped level
                // merges the sheet into the crossing cleanly.
                if (seededBasin)
                    cap = seedLvl;
                // LEAKY ponds: the fill table called this basin contained,
                // but its rim dips BELOW the water level — in-game that's a
                // pond hanging on a slope with a water wall at the downhill
                // edge. The level cannot exceed the lowest rim.
                else if (rimMin != float.MaxValue && cap > rimMin - 0.3f)
                    cap = rimMin - 0.3f;
                bool any = false;
                int wetCells = 0;
                // AREA — any wet non-channel cell. The old counter only
                // counted cells ≥1.2m deep, which the shallow-spill generator
                // NEVER produces: kept 0 of 634, then 0 of 427, both logged.
                // Size decisions read THIS; wetCells stays as the depth stat.
                int areaCells = 0;
                float depthSum = 0f, depthMax = 0f;
                for (int k = 0; k < comp.Count; k++)
                {
                    int i = comp[k];
                    byte mk = m[i];
                    if (mk == 1 || mk == 3) continue;
                    // Never below the cell's own ground — a leaky-rim cap
                    // under local terrain wrote NEGATIVE levels on trench
                    // rings, gutting the bilinear field (transect-proven
                    // W=−13.3 on mask-2 cells = the persistent dry slits).
                    if (!riverFed)
                    {
                        // ONE FLAT LEVEL per still-water basin, from the
                        // LOWEST bank (cap = min(floor+6, rimMin−0.3)). The
                        // old code only capped cells standing ABOVE it —
                        // cells below kept their sloped settled levels, so
                        // ponds on hillsides rendered tilted basins. Cells
                        // whose ground stands above the flat level simply
                        // fall dry: the pond retreats to the level's own
                        // contour instead of smearing up the slope, and the
                        // bowl excavation below digs from this flat plane.
                        if (w[i] != cap) { w[i] = cap; capped++; any = true; }
                    }
                    // River-fed: no global rim clamp (it drained sloped gully
                    // chains to one bottom-of-chain cap) — but the clamp WAS
                    // load-bearing for flats: it dried the fill table's
                    // PHANTOM valley sheets. Without any discipline those
                    // sheets stood at full spill level (log: 2.9M lake-class
                    // cells, 45.8M "pool" cells — entire valleys underwater).
                    // The replacement: thin fill FILM dies by DEPTH. Cells
                    // under 0.45m of standing fill are spill artifacts, not
                    // water bodies; genuinely dug pools are deeper and keep
                    // their levels for the pool pass to shape.
                    else if (!seededBasin && w[i] > g[i] && w[i] - g[i] < 0.45f)
                    { w[i] = g[i]; capped++; }
                    float d = w[i] - g[i];
                    if (d > 0.01f && w[i] > SEA + 0.5f) areaCells++;
                    if (d >= 1.2f && mk == 0 && w[i] > SEA + 0.5f)
                    { wetCells++; depthSum += d; if (d > depthMax) depthMax = d; }
                }
                // Lake-shaped or gone: a pond survives as a small pool with
                // REAL depth (≤ ~80×80m and ≥2m somewhere — kills the
                // 1.2-2m dimple puddles that acne'd every mountainside) or
                // as a genuinely deep bowl (avg ≥ 3m, e.g. crater lakes).
                // Broad shallow sheets — whole valley floors the fill table
                // couldn't drain through the 1-cell channel — are dropped
                // (offline proof: 93% of a river tile flooded without this).
                walked++;
                if (riverFed) riverFedN++;
                // KEEP DECISION — size + biome + district, NOT incoming
                // depth. The fill table hands us SHALLOW spills (fresh-gen
                // proof: kept 0 of 634 — not one basin on the whole map had
                // 2m of natural depth), while the old drop only cleared
                // mask-0 cells and left every bank-masked margin wet:
                // mountains full of unkept, unclassified, unexcavated
                // puddles. Depth is OURS to make — kept basins are EXCAVATED
                // into real bowls below.
                // BIOME AT THE SPOT THE WATER SITS (the basin centroid) —
                // pipeline-read verified: BuildBiomes paints swamp as NOISE
                // POCKETS ON GROUND BELOW 52m (not water margins — the
                // earlier "circular margin paint" theory was wrong), and
                // mountain is the >80m line. The 454-swamp readings were
                // TRUTHFUL: every real basin on that seed sat below 51m in
                // legit swamp country, and the mountains held only PHANTOM
                // water the walk could never see (fixed in the reconcile) —
                // with real tarns now seeded at altitude, the centroid biome
                // is the honest answer. Elevation (>80m) stays as the
                // glacial backstop.
                byte basinBio = _biomeMap != null ? _biomeMap[cci] : (byte)0;
                bool coldBasin = basinBio == 3 || basinBio == 7 || (cap > 80f && basinBio != 2);
                bool swampy = basinBio == 2 && !coldBasin;
                if (basinBio == 3 || basinBio == 7) rimCold++;
                else if (basinBio == 2) rimSwamp++;
                else rimOther++;
                if (cap > capHi) capHi = cap;

                // DELIBERATE DEPRESSIONS ONLY (user doctrine, final form):
                // water is NEVER poured into terrain-made dips. Every still
                // body is a bowl WE dug (seeders — swamp basins, quota
                // lakes, tarns) or a pool riding a carved river. With the
                // fill table dried before seeding, an unseeded non-river
                // basin reaching this point can only be a LEAK from some
                // pass — it is dropped and counted loudly, never blessed.
                // The natural-basin keep rolls, biome profiles, districts,
                // broad-flood guard and pit veto all died with the
                // phantoms: bank logic is ours by construction and there is
                // nothing left to judge.
                bool keep = seededBasin ? areaCells >= 8
                          : riverFed ? areaCells >= 24
                          : false;
                bool keptByRule = keep;
                if (!seededBasin && !riverFed && areaCells >= 24) leakDrop++;
                if (!keep && !keptByRule) ruleDrop++;
                if (!keep)
                {
                    for (int k = 0; k < comp.Count; k++)
                    {
                        int i = comp[k];
                        // Clear mask-2 (bank margin) water too. Only the
                        // flowing channel (1/3) survives a drop — the old
                        // mask-0-only clear was a COLANDER: 44.7M capped
                        // cells vs 46K dropped, every margin stayed wet.
                        if (m[i] == 1 || m[i] == 3) continue;
                        if (w[i] > g[i] && w[i] > SEA + 0.5f) { w[i] = Mathf.Min(w[i], g[i]); dropped++; }
                    }
                    droppedComps++;
                }
                else
                {
                    // CLASSIFY the kept basin — biome at the water itself.
                    // Cold (biome OR >80m) still water freezes; ~1 in 7 stays
                    // liquid as a hot spring; river-fed never freezes.
                    byte cls;
                    if (swampy) cls = WC_SWAMP;
                    else if (coldBasin && !riverFed)
                        cls = WanderHash01(i0 % n, i0 / n, _seed ^ 0x2F5D) < 0.15f ? WC_HOTSPRING : WC_FROZEN;
                    else cls = areaCells >= 800 ? WC_LAKE : WC_POND;
                    keptN++;
                    switch (cls)
                    {
                        case WC_LAKE: bLake++; break;
                        case WC_POND: bPond++; break;
                        case WC_SWAMP: bSwampN++; break;
                        case WC_FROZEN: bFroz++; break;
                        case WC_HOTSPRING: bHot++; break;
                    }
                    for (int k = 0; k < comp.Count; k++)
                    {
                        int i = comp[k];
                        if (m[i] == 1 || m[i] == 3 || w[i] <= g[i] + 0.01f) continue;
                        _classS[(i / n / cstride) * cn2 + (i % n / cstride)] = cls;
                    }

                    // BOWL EXCAVATION — every kept still-water basin digs a
                    // REAL bowl: ring-BFS in from the dry boundary, bed
                    // dropping 0.6 / 1.1 / 1.7 / 2.3m by ring and 2.6m from
                    // ring 4 inward. The fill table only ever hands us
                    // shallow spills ("these are like puddles") — depth is
                    // manufactured HERE, so banks exist for the sheet to
                    // tuck into and the classes sit in real water. River-fed
                    // networks skip it: the channel carve owns their depth.
                    // Seeded SWAMP floods skip it too — the terraform dug
                    // wadeable murk beds (0.45–1.2m) on purpose; the ring
                    // schedule would re-dig the marsh into a 2m lake.
                    if (!riverFed && !(seededBasin && swampy))
                    {
                        // Swamps stay SHALLOW MURK — the deep-lake schedule
                        // scaled way down for them.
                        float exScale = swampy ? 0.45f : 1f;
                        s_ringSeen.Clear();
                        s_ringA.Clear();
                        for (int k = 0; k < comp.Count; k++)
                        {
                            int i = comp[k];
                            if (m[i] == 1 || m[i] == 3 || w[i] <= g[i] + 0.01f) continue;
                            int x = i % n, z = i / n;
                            bool edge =
                                   (x + 1 < n && w[i + 1] <= g[i + 1] + 0.01f)
                                || (x > 0 && w[i - 1] <= g[i - 1] + 0.01f)
                                || (z + 1 < n && w[i + n] <= g[i + n] + 0.01f)
                                || (z > 0 && w[i - n] <= g[i - n] + 0.01f);
                            if (!edge) continue;
                            s_ringSeen.Add(i);
                            s_ringA.Add(i);
                            float dig0 = w[i] - 0.8f * exScale;
                            if (g[i] > dig0) { g[i] = dig0; smearFix++; }
                        }
                        var front = s_ringA;
                        var nxt = s_ringB;
                        // REAL lakes, not puddles: 0.8m at the shoreline,
                        // stepping 1.6/2.6/3.6/4.2 by ring, 4.8m interior.
                        // Small ponds only ever reach the first rings, so
                        // depth scales with body size by construction.
                        for (int ring = 1; ring <= 4 && front.Count > 0; ring++)
                        {
                            float depth = (ring == 1 ? 1.6f : ring == 2 ? 2.6f : ring == 3 ? 3.6f : 4.2f) * exScale;
                            nxt.Clear();
                            for (int k = 0; k < front.Count; k++)
                            {
                                int i = front[k];
                                int x = i % n, z = i / n;
                                Grow(i + 1, x + 1 < n);
                                Grow(i - 1, x > 0);
                                Grow(i + n, z + 1 < n);
                                Grow(i - n, z > 0);
                            }
                            var t = front; front = nxt; nxt = t;

                            void Grow(int j, bool ok)
                            {
                                if (!ok || s_ringSeen.Contains(j)) return;
                                if (w[j] <= g[j] + 0.01f) return;
                                if (m[j] == 1 || m[j] == 3) return;
                                s_ringSeen.Add(j);
                                nxt.Add(j);
                                float dig = w[j] - depth;
                                if (g[j] > dig) { g[j] = dig; smearFix++; }
                            }
                        }
                        for (int k = 0; k < comp.Count; k++)
                        {
                            int i = comp[k];
                            if (m[i] == 1 || m[i] == 3 || w[i] <= g[i] + 0.01f) continue;
                            if (s_ringSeen.Contains(i)) continue;
                            float dig = w[i] - 4.8f * exScale;
                            if (g[i] > dig) { g[i] = dig; smearFix++; }
                        }
                    }
                    // Berms build for every still basin AND for seeded
                    // river-crossed swamps — a crossing river must not cost
                    // the sheet its containment (ordinary river-fed chains
                    // keep their carved channel banks and skip this).
                    if (!riverFed || seededBasin)
                    {
                        // BUILD THE LOW BANK (both-sides rule): dry rim
                        // ground below the crest raises on a gentle 0.32 m/m
                        // wedge descending outward until it merges with
                        // natural grade — the sheet edge dies into solid
                        // ground on EVERY side. The high side was dug above;
                        // the low side is now buried. 0.55 m/m read as an
                        // obvious artificial dike from outside the water
                        // (user screenshots), and its thin body let the
                        // sheet's 3-cell skirt punch out the outer face.
                        s_ringSeen.Clear();
                        s_ringA.Clear(); s_ringB.Clear();
                        float bermTop = cap + WaterRules.WaterStyleFor(basinBio).BermH;
                        for (int k = 0; k < comp.Count; k++)
                        {
                            int i = comp[k];
                            if (m[i] != 0 || w[i] <= g[i] + 0.01f) continue;
                            int x = i % n, z = i / n;
                            SeedBerm(i + 1, x + 1 < n);
                            SeedBerm(i - 1, x > 0);
                            SeedBerm(i + n, z + 1 < n);
                            SeedBerm(i - n, z > 0);

                            void SeedBerm(int j, bool ok)
                            {
                                if (!ok || s_ringSeen.Contains(j)) return;
                                if (m[j] != 0 || w[j] > g[j] + 0.01f) return;
                                s_ringSeen.Add(j);
                                if (g[j] < bermTop) { g[j] = bermTop; bermFill++; }
                                s_ringA.Add(j);
                            }
                        }
                        var bf = s_ringA; var bn = s_ringB;
                        for (int ring = 2; ring <= 22 && bf.Count > 0; ring++)
                        {
                            // 3-cell CREST PLATEAU before the wedge descends —
                            // the sheet's skirt dilation reaches three cells
                            // past the waterline and must land INSIDE the berm
                            // body, never out its far face.
                            float wedge = bermTop - 0.32f * ccm * Mathf.Max(0, ring - 3);
                            bn.Clear();
                            for (int k = 0; k < bf.Count; k++)
                            {
                                int i = bf[k];
                                int x = i % n, z = i / n;
                                GrowBerm(i + 1, x + 1 < n);
                                GrowBerm(i - 1, x > 0);
                                GrowBerm(i + n, z + 1 < n);
                                GrowBerm(i - n, z > 0);

                                void GrowBerm(int j, bool ok)
                                {
                                    if (!ok || s_ringSeen.Contains(j)) return;
                                    if (m[j] != 0 || w[j] > g[j] + 0.01f) return;
                                    s_ringSeen.Add(j);
                                    // Irregular TOE past the plateau: hash
                                    // dither breaks the perfect cone so the
                                    // outer slope reads as ground, not a dike.
                                    float wj = ring <= 3 ? wedge
                                        : wedge - WanderHash01(j % n, j / n, _seed ^ 0x6B11) * 0.3f;
                                    if (g[j] < wj) { g[j] = wj; bermFill++; bn.Add(j); }
                                    // wedge ≤ ground = merged with grade; stop
                                }
                            }
                            var t2 = bf; bf = bn; bn = t2;
                        }
                    }
                    // Rim low spots must DRAIN somewhere or they read as
                    // broken banks: roll for an outlet brook at the natural
                    // spill point (carved after the walk, below).
                    if (!riverFed && rimMinIdx >= 0
                        && (cls == WC_LAKE || cls == WC_POND || cls == WC_SWAMP)
                        && WanderHash01(rimMinIdx % n, rimMinIdx / n, _seed ^ 0x0575) < 0.55f)
                    {
                        outletSpill.Add(rimMinIdx);
                        outletCap.Add(cap);
                    }
                    if (rimMax > cap + 2f)
                    {
                        s_cutA.Clear();
                        for (int k = 0; k < comp.Count; k++) s_cutA.Add(comp[k]);
                        var frontier = s_cutA;
                        var next = s_cutB;
                        for (int ring = 0; ring < 2 && frontier.Count > 0; ring++)
                        {
                            next.Clear();
                            for (int k = 0; k < frontier.Count; k++)
                            {
                                int i = frontier[k];
                                int x = i % n, z = i / n;
                                if (x + 1 < n) CutRim(i + 1);
                                if (x > 0) CutRim(i - 1);
                                if (z + 1 < n) CutRim(i + n);
                                if (z > 0) CutRim(i - n);
                            }
                            var t = frontier; frontier = next; next = t;
                        }

                        void CutRim(int j)
                        {
                            if (seen[j] || m[j] != 0) return;
                            if (s_ringSeen.Contains(j)) return;           // berm crest — never breach it
                            if (w[j] > g[j] + 0.01f) return;              // already water
                            if (g[j] <= cap || g[j] > cap + 1.5f) return; // not the near-rim band
                            g[j] = cap - 0.6f;
                            w[j] = cap;
                            seen[j] = true;   // the outer scan must never re-flood these as a "new" basin
                            _classS[(j / n / cstride) * cn2 + (j % n / cstride)] = cls;
                            next.Add(j);
                            cutRim++;
                        }
                    }
                }
                if (any) comps++;
            }
            // RIVER POOLS — every slope break along a carved run pours a
            // fill-table pool (a mask-0 wet cluster BETWEEN channel cells).
            // The walk left them as RAW CONTOUR SHEETS: unflattened
            // shorelines spilling jagged tongues across the snow, no bowl,
            // no banks — the giant aprons beside sloped channels (user:
            // "flatter rivers we handle perfectly, but you are not handling
            // the slopes"). Every pool touching a channel now gets the pond
            // treatment at ITS OWN level: flatten to the pool's fill level,
            // dig a modest bowl, berm the rim locally (bounded lift — the
            // downhill cascade lip stays open). Runs AFTER the outlet brooks
            // carve (call site below) so pools poured by the outlets get the
            // same treatment — declared here, invoked there.
            void ShapeRiverPools()
            {
                int poolN = 0; long poolCellsN = 0;
                var pvis = new HashSet<int>();
                var pcells = new System.Collections.Generic.List<int>(1024);
                var plevels = new System.Collections.Generic.List<float>(1024);
                var pstack = new Stack<int>(256);
                for (int p0 = 0; p0 < n * n; p0++)
                {
                    if (m[p0] != 0 || w[p0] <= g[p0] + 0.01f || pvis.Contains(p0)) continue;
                    pcells.Clear(); pstack.Clear(); plevels.Clear();
                    pstack.Push(p0); pvis.Add(p0);
                    bool touchesChannel = false;
                    long pcx = 0, pcz = 0;
                    while (pstack.Count > 0)
                    {
                        int i = pstack.Pop();
                        pcells.Add(i);
                        plevels.Add(w[i]);
                        pcx += i % n; pcz += i / n;
                        int x = i % n, z = i / n;
                        VisitP(i + 1, x + 1 < n); VisitP(i - 1, x > 0);
                        VisitP(i + n, z + 1 < n); VisitP(i - n, z > 0);

                        void VisitP(int j, bool ok)
                        {
                            if (!ok) return;
                            if (m[j] != 0) { touchesChannel = true; return; }
                            if (w[j] <= g[j] + 0.01f || pvis.Contains(j)) return;
                            pvis.Add(j); pstack.Push(j);
                        }
                    }
                    if (!touchesChannel || pcells.Count < 12) continue;
                    // RUNAWAY GUARD: a "pool" bigger than ~0.6km² is a flood
                    // regression, not a slope-break pool — never shape it
                    // (log-proven: 45.8M cells shaped when the phantom fill
                    // sheets leaked through).
                    if (pcells.Count > 40000)
                    {
                        FiresLogger.LogWarning($"[FiresWorldGen] river pool cluster of {pcells.Count:N0} cells SKIPPED — that is a flood, not a pool.");
                        continue;
                    }
                    // Seeded sheets (swamp floods, lakes, tarns) are OWNED by
                    // their seeders — this pass flattening one of their
                    // clusters to the cluster max built glassy water MOUNDS
                    // standing above the swamp's banks (user screenshot).
                    if (TryGetSeedLevel((int)(pcx / pcells.Count), (int)(pcz / pcells.Count), out _))
                        continue;
                    // MEDIAN, not max: one contaminated high cell (a junction
                    // margin, a neighbouring terrace) must never lift the
                    // whole pool above its banks.
                    plevels.Sort();
                    float plvl = plevels[plevels.Count / 2];
                    poolN++; poolCellsN += pcells.Count;
                    // ONE flat level (the pool's own fill level) + a floor
                    // under every cell so no zero-depth film survives; cells
                    // whose ground stands above the level fall DRY instead
                    // of being cratered down to it.
                    for (int k = 0; k < pcells.Count; k++)
                    {
                        int i = pcells[k];
                        if (g[i] > plvl + 0.5f) { if (w[i] > g[i]) w[i] = g[i]; continue; }
                        if (w[i] != plvl) w[i] = plvl;
                        if (g[i] > plvl - 0.35f) g[i] = plvl - 0.35f;
                    }
                    // modest bowl: edge 0.6 / ring 1.2 / interior 1.8
                    s_ringSeen.Clear(); s_ringA.Clear(); s_ringB.Clear();
                    for (int k = 0; k < pcells.Count; k++)
                    {
                        int i = pcells[k];
                        int x = i % n, z = i / n;
                        bool edge =
                               (x + 1 < n && w[i + 1] <= g[i + 1] + 0.01f)
                            || (x > 0 && w[i - 1] <= g[i - 1] + 0.01f)
                            || (z + 1 < n && w[i + n] <= g[i + n] + 0.01f)
                            || (z > 0 && w[i - n] <= g[i - n] + 0.01f);
                        if (!edge) continue;
                        s_ringSeen.Add(i); s_ringA.Add(i);
                        if (g[i] > plvl - 0.6f) g[i] = plvl - 0.6f;
                    }
                    var pf = s_ringA; var pn2 = s_ringB;
                    pn2.Clear();
                    for (int k = 0; k < pf.Count; k++)
                    {
                        int i = pf[k];
                        int x = i % n, z = i / n;
                        GrowP(i + 1, x + 1 < n); GrowP(i - 1, x > 0);
                        GrowP(i + n, z + 1 < n); GrowP(i - n, z > 0);

                        void GrowP(int j, bool ok)
                        {
                            if (!ok || s_ringSeen.Contains(j)) return;
                            if (m[j] != 0 || w[j] <= g[j] + 0.01f) return;
                            s_ringSeen.Add(j);
                            pn2.Add(j);
                            if (g[j] > plvl - 1.2f) g[j] = plvl - 1.2f;
                        }
                    }
                    for (int k = 0; k < pcells.Count; k++)
                    {
                        int i = pcells[k];
                        if (s_ringSeen.Contains(i)) continue;
                        if (g[i] > plvl - 1.8f) g[i] = plvl - 1.8f;
                    }
                    // local berm: rim to plvl+0.5, gentle 0.32 wedge out,
                    // lift bounded 2.2m (never a curtain wall)
                    s_ringSeen.Clear(); s_ringA.Clear(); s_ringB.Clear();
                    for (int k = 0; k < pcells.Count; k++)
                    {
                        int i = pcells[k];
                        int x = i % n, z = i / n;
                        SeedPB(i + 1, x + 1 < n); SeedPB(i - 1, x > 0);
                        SeedPB(i + n, z + 1 < n); SeedPB(i - n, z > 0);

                        void SeedPB(int j, bool ok)
                        {
                            if (!ok || s_ringSeen.Contains(j)) return;
                            if (m[j] != 0 || w[j] > g[j] + 0.01f) return;
                            s_ringSeen.Add(j); s_ringA.Add(j);
                            if (g[j] < plvl + 0.5f && plvl + 0.5f - g[j] <= 2.2f) g[j] = plvl + 0.5f;
                        }
                    }
                    var bf2 = s_ringA; var bn2 = s_ringB;
                    for (int ring = 2; ring <= 8 && bf2.Count > 0; ring++)
                    {
                        float wedge = plvl + 0.5f - 0.32f * ccm * Mathf.Max(0, ring - 2);
                        bn2.Clear();
                        for (int k = 0; k < bf2.Count; k++)
                        {
                            int i = bf2[k];
                            int x = i % n, z = i / n;
                            GrowPB(i + 1, x + 1 < n); GrowPB(i - 1, x > 0);
                            GrowPB(i + n, z + 1 < n); GrowPB(i - n, z > 0);

                            void GrowPB(int j, bool ok)
                            {
                                if (!ok || s_ringSeen.Contains(j)) return;
                                if (m[j] != 0 || w[j] > g[j] + 0.01f) return;
                                s_ringSeen.Add(j);
                                if (g[j] < wedge && wedge - g[j] <= 2.2f) { g[j] = wedge; bn2.Add(j); }
                            }
                        }
                        var tt = bf2; bf2 = bn2; bn2 = tt;
                    }
                }
                if (poolN > 0)
                    FiresLogger.LogInfo($"[FiresWorldGen] river pools: {poolN} slope-break pool(s) shaped ({poolCellsN:N0} cells) — flattened, dug in, rims bermed at local level.");
            }
            // (ShapeRiverPools is invoked AFTER the outlet brooks below.)

            // OUTLET STREAMS — the spillway made real. The berm gets a NOTCH
            // at the basin's natural low-rim point, and a brook emerges just
            // below it (spring-style — it starts OUTSIDE the berm and never
            // wet-connects to the lake, so next load's walk still sees a
            // closed still body) and follows steepest descent until it meets
            // a river, other water, the sea, a pit, or ~800m. Stream cells
            // join the river MASK: they flow sloped, settle is skipped,
            // class = RIVER, and the shore paint follows them.
            int outlets = 0, outletCellsN = 0;
            var opath = new System.Collections.Generic.List<int>(512);
            var opathG = new System.Collections.Generic.List<float>(512);
            for (int q = 0; q < outletSpill.Count; q++)
            {
                int spill = outletSpill[q];
                float cap2 = outletCap[q];
                // notch the berm so the bank visibly dips at the spillway
                int nx0 = spill % n, nz0 = spill / n;
                if (nx0 < 2 || nz0 < 2 || nx0 >= n - 2 || nz0 >= n - 2) continue;
                for (int dz2 = -1; dz2 <= 1; dz2++)
                    for (int dx2 = -1; dx2 <= 1; dx2++)
                    {
                        int j = (nz0 + dz2) * n + (nx0 + dx2);
                        if (m[j] != 0 || w[j] > g[j] + 0.01f) continue;
                        float notch = cap2 + 0.15f;
                        if (g[j] > notch && g[j] < cap2 + 1.2f) g[j] = notch;
                    }
                // trace: steepest descent over dry ground
                opath.Clear();
                int cur = spill;
                bool reachedWater = false;
                for (int guard = 0; guard < 400; guard++)
                {
                    int cx2 = cur % n, cz2 = cur / n;
                    if (cx2 < 6 || cz2 < 6 || cx2 >= n - 6 || cz2 >= n - 6) break;   // terminal pool box reaches ±5
                    int best = -1; float bg = g[cur];
                    for (int e = 0; e < 8; e++)
                    {
                        int j = cur + (e == 0 ? 1 : e == 1 ? -1 : e == 2 ? n : e == 3 ? -n
                                     : e == 4 ? n + 1 : e == 5 ? n - 1 : e == 6 ? -n + 1 : -n - 1);
                        // mask-2 bench cells are walkable/carvable — only the
                        // real channel or standing water ends the brook (the
                        // bench margin used to stop it 2-6m short: dry strip
                        // at every confluence).
                        if (m[j] == 1 || m[j] == 3 || w[j] > g[j] + 0.01f)
                        {
                            if (guard > 3) reachedWater = true;   // met a river/lake — done
                            continue;
                        }
                        if (g[j] < bg) { bg = g[j]; best = j; }
                    }
                    if (reachedWater) break;
                    if (best < 0) break;                          // pit/flat — the brook sinks
                    opath.Add(best);
                    cur = best;
                    if (g[cur] <= SEA + 0.5f) break;
                }
                if (opath.Count < 6) continue;
                outlets++;
                // ORIGINAL surface captured before digging — same runaway-
                // trench feedback as the headwater streams. Same RIVER RULE:
                // pristine MIN over the stamp box, so the brook's level sits
                // below every bank cell and the mesh clips into the banks.
                opathG.Clear();
                for (int k2 = 0; k2 < opath.Count; k2++)
                {
                    int i = opath[k2];
                    float mn = g[i];
                    for (int dz = -3; dz <= 3; dz++)
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            float gv2 = g[i + dz * n + dx];
                            if (gv2 < mn) mn = gv2;
                        }
                    opathG.Add(mn);
                }
                float lvl2 = cap2 - 0.05f;
                // Continuous CHANNEL stamp (shared with headwater streams) —
                // the old center-cell dig beaded into single-cell pits on
                // every diagonal descent step. The junction margin
                // (skipWetAbove = cap2 − 0.2) digs but never WETS cells
                // touching the lake's standing water, so the brook emerges
                // spring-style a few metres below the notch and can never
                // 4-connect its source basin.
                for (int k2 = 1; k2 < opath.Count; k2++)
                {
                    int i = opath[k2];
                    lvl2 = Mathf.Min(lvl2 - 0.015f, opathG[k2] - 0.25f);
                    outletCellsN += StampChannelCell(i, lvl2, 0.65f, 1, cap2 - 0.2f);
                }
                // TERMINAL RULE — same as the headwater streams: a brook that
                // never reached water ends in a POOL, not a hanging face.
                if (!reachedWater)
                {
                    int last = opath[opath.Count - 1];
                    if (g[last] > SEA + 0.6f)
                        outletCellsN += StampChannelCell(last, lvl2, 1.8f, 3, 0f);
                }
            }
            // Pools shaped AFTER every channel exists (walk rivers + outlet
            // brooks), so outlet-poured pools get the pond treatment too.
            ShapeRiverPools();

            // WATER-FIRST DROP-TO-BANK (user spec, 2026-07-28): a still-water body is placed TOO HIGH.
            // Take its LOWEST BANK and level the whole FLAT body at (lowest bank − freeboard). We DROP
            // the water and OWN the terrain — a wall is NEVER built. It's fine if the lake gets smaller:
            // cells whose bed rose above the new level are CARVED down (modest gaps, to keep the
            // footprint) or dried (big gaps). Rivers (channels) keep their sloped ribbon and are
            // skipped; the ocean is its own flat plane. The WATER AUDIT after is the proof it holds.
            {
                const float FREEBOARD = 0.5f, CARVE_CAP = 4f, LAKE_MIN_DEPTH = 1.0f;
                var wfSeen = new bool[n * n];
                var wfStack = new System.Collections.Generic.Stack<int>(8192);
                var wfComp = new System.Collections.Generic.List<int>(8192);
                int wfBodies = 0; long wfLevelled = 0, wfCarved = 0, wfDried = 0;
                for (int start = 0; start < n * n; start++)
                {
                    if (wfSeen[start]) continue;
                    if (w[start] <= g[start] + 0.01f || m[start] == 1 || m[start] == 3 || w[start] <= SEA + 1f)
                    { wfSeen[start] = true; continue; }   // dry / channel / ocean → not a still-water body cell
                    // Flood the connected STILL-water body; its lowest DRY (or channel/ocean) neighbour is the spill bank.
                    wfComp.Clear(); wfStack.Clear(); wfStack.Push(start); wfSeen[start] = true;
                    float bank = float.MaxValue;
                    while (wfStack.Count > 0)
                    {
                        int i = wfStack.Pop(); wfComp.Add(i);
                        int x = i % n, z = i / n;
                        WfN(i + 1, x + 1 < n); WfN(i - 1, x > 0); WfN(i + n, z + 1 < n); WfN(i - n, z > 0);
                        void WfN(int j, bool ok)
                        {
                            if (!ok) return;
                            bool stillWet = w[j] > g[j] + 0.01f && m[j] != 1 && m[j] != 3 && w[j] > SEA + 1f;
                            if (stillWet) { if (!wfSeen[j]) { wfSeen[j] = true; wfStack.Push(j); } }
                            else { float rim = (m[j] == 1 || m[j] == 3) ? w[j] : g[j]; if (rim < bank) bank = rim; }
                        }
                    }
                    if (bank == float.MaxValue) continue;    // no bank found (edge-locked) — leave it
                    wfBodies++;
                    float level = bank - FREEBOARD;           // sit BELOW the lowest bank
                    foreach (int i in wfComp)
                    {
                        if (g[i] < level - 0.01f) { w[i] = level; wfLevelled++; }                                  // already below → flat level
                        else if (g[i] - level <= CARVE_CAP) { g[i] = level - LAKE_MIN_DEPTH; w[i] = level; wfCarved++; }  // carve to keep footprint
                        else { w[i] = g[i]; wfDried++; }                                                            // too high → dry (smaller lake, per user)
                    }
                }
                FiresLogger.LogInfo($"[FiresWorldGen] water DROP-to-bank: {wfBodies:N0} still-water bodies levelled at lowest-bank−{FREEBOARD:0.0}m ({wfLevelled:N0} flat, {wfCarved:N0} bed-carved, {wfDried:N0} dried) — water now sits BELOW terrain; no walls built.");
            }
            // WATERSHED: now the lakes sit at their real (dropped) level, slope every river DOWN to
            // meet the flat body it flows into, so the network is continuous source→lake→river→ocean.
            ReconcileRiversToWater(w, g, m, n);
            // RIVER RESIDUAL: the bed carves above can lower a river's own BANK below its ribbon (the
            // "floating mesh"). Rivers slope, so we can't drop them like lakes — re-assert a SMALL
            // natural bank (capped, not a wall) so the ribbon sits below terrain. Steeper edges = falls.
            ContainRiversBelowBanks(w, g, m, n);
            if (capped + dropped + smearFix + cutRim + bermFill > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] basin water normalized: {capped:N0} cells capped across {comps} basins (floor+{LAKE_MAX_DEPTH:0}m); " +
                          $"{dropped:N0} cells across {droppedComps} basins dropped; " +
                          $"slope surgery: {smearFix:N0} smear cells deepened, {cutRim:N0} rim cells cut in, {bermFill:N0} berm cells raised.");
            if (outlets > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] outlet streams: {outlets} spillway brook(s) carved ({outletCellsN:N0} cells) from lake/pond/swamp rims toward lower water.");
            // The composition-health line: this answers "did the rules do
            // what we meant" at a glance, per load, without a survey pass.
            if (leakDrop > 0)
                FiresLogger.LogWarning($"[FiresWorldGen] WATER LEAK: {leakDrop} unseeded, non-river basin(s) dropped — " +
                                 "some pass is wetting terrain depressions; deliberate-depressions doctrine says that is a bug.");
            FiresLogger.LogInfo($"[FiresWorldGen] basin walk: {walked} basin(s) — kept {keptN} " +
                      $"(lake {bLake}, pond {bPond}, swamp {bSwampN}, frozen {bFroz}, hotspring {bHot}); " +
                      $"river-fed {riverFedN}; dropped {ruleDrop} by shape rule + {rollDrop} by composition roll " +
                      $"| basin-bio: cold {rimCold}, swamp {rimSwamp}, other {rimOther}; highest level {capHi:0}m.");
            s_repWalked = walked; s_repKept = keptN; s_repLake = bLake; s_repPond = bPond;
            s_repSwampB = bSwampN; s_repFrozen = bFroz; s_repHot = bHot; s_repRiverFed = riverFedN;
            s_repCapHi = capHi;
        }

        // Load-time ACCEPTANCE AUDIT — the never-above-banks invariant,
        // verified against the exact field the session will play on. A
        // violation = a wet cell whose 3×3 holds a DRY cell with ground
        // below the water level − 0.35 (a water wall in-game). MUST be 0;
        // the first few violations log world coordinates so a screenshot
        // can be matched to the data cell-for-cell.
        // Overflow audit with FULL diagnostics (user: "nail down WHAT water is incorrect, what banks are
        // over and where, so we can nail down the generation to never have a single overflow"). An overflow
        // is a wet cell standing >0.35m above its lowest DRY neighbour (that neighbour is the bank the water
        // spills over). Reports the count broken down by water CLASS, by overflow MAGNITUDE, the WORST
        // offenders with full context, and spatial HOTSPOT regions — everything needed to hunt each one down.
        // (TRACE 2026-08-11, user: "if a river is obviously running towards a lake and stops short it's going
        // to piss me off") FINAL-FIELD CONNECTION AUDIT. For every planned body, how close does the nearest
        // CARVED river cell (mask 1 channel / 3 spine) actually get to its footprint? Touching = connected.
        // A river cell 1–30 cells away with NO touch is exactly the "runs at the lake and stops short" defect.
        // Bodies with no river anywhere near are fine (not every body needs an inflow — user).
        private static int s_bermMergePoint;   // (TRACE) junction cells left unbanked so a river and a body merge
        private static int s_canyonShelfCells;
        private static int s_shelfDeepCell = -1; private static float s_shelfDeepSlot, s_shelfDeepWl;   // the deepest slot the floor pass cut, so its SECTION can be printed   // (TRACE) cells where the canyon floor section cut a walkable shelf
        private static int s_resolvedRuns;   // (TRACE) runs whose level came from the planner's law re-solved on final terrain
        private static int s_boundHits, s_dryCore;   // (TRACE) carve diagnostics: local-bound hits inside the wetted width, and core cells left DRY
        private static int s_canyonNodes, s_canyonMoved;   // (TRACE) canyon/gorge nodes seen by the level solve, and how many the floor rule actually MOVED
        private static int s_runBothEnds, s_runOneEnd, s_runNoEnds, s_runToOcean, s_runFromSpring;   // (TRACE) run provenance — how many rivers are tied to real water at each end
        private static int s_anchorNodes, s_anchorRuns, s_anchorRunsNone;   // (TRACE) does the base-level anchor ever fire on a plan run?
        private static int s_bermEarned, s_bermGratuitous;   // (TRACE) river bank raises where the LEVEL was above natural ground (earned) vs already below it (a curb) — see the note at the rim raise
        private static int s_shoreFilled;            // (TRACE) submerged-shore cells the pond-style footprint test added outside the 1.05·half tube
        // (TRACE, Phase 2) cells cut deeper than 20 m, bucketed by the reach's width — "are our canyons earned?"
        private static int s_deepBrook, s_deepStream, s_deepRiver, s_deepTrunk;

    }
}
