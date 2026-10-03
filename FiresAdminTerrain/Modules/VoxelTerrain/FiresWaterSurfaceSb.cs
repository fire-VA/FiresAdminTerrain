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
    // ═══ THE RENDERED WATER SURFACE — reconciliation, labels, and the sheet mesh nodes ═══════════════
    // BuildReconciledSurface turns the solved field into the smooth surface the renderer samples;
    // BuildWaterLabels/ClipGroundAt/BuildSheetNodes turn that into the sheet the mesher consumes.
    // ⚠ ReliefFor lives here and is SUBTRACTED from the surface: relief is freeboard under the BANKS, so a
    // larger value puts the water LOWER, not higher. On a cell whose bed is already only a shelf deep, a
    // relief bigger than that shelf renders the surface UNDER its own bed.
    public static partial class FiresWorldGenBase
    {
        private static void BuildReconciledSurface(float[] g, float[] w, int n)
        {
            _surfS = null; _surfN = 0;
            if (g == null || w == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float cellM = (2f * _half) / (n - 1);
            // ⚠⚠ THE WATER SURFACE IS RECONCILED AT FULL RESOLUTION (2026-08-17). This was
            //     stride = RoundToInt(SURF_TARGET_CELL_M(8) / cellM), i.e. an 8 m lattice while the terrain the
            //     sheet has to clip into is 2 m at n=10501 — a 4x mismatch AT THE SHORELINE. That is the reported
            //     bug: "we have a ton of rivers and streams with meshes ending short of the banks". MEASURED at
            //     game res before this change: MESH EDGE (a visible lip) 316, WATER AUDIT FAIL with 78,001 inland
            //     overflows, and the WORST LIP WAS 8.01 m = EXACTLY ONE 8 m WATER CELL. The studio had been
            //     reporting 46 at n=4097, so the invariant was being validated 6.9x understated.
            //     r1/r2 below are metre-based radii divided by cell2, so they stay the same DISTANCE automatically.
            int stride = 1;
            int n2 = (n + stride - 1) / stride;
            float cell2 = cellM * stride;
            StampBodyClasses(n2, stride, g, w);   // KIND from the plan, COVERAGE from the field the world renders
            int r1 = Mathf.Max(1, Mathf.RoundToInt(SURF_RIM_M / cell2));
            int r2 = Mathf.Max(1, Mathf.RoundToInt(SURF_GOV_M / cell2));
            float freeboard = FiresVoxelConfig.WaterBankFreeboard != null
                ? FiresVoxelConfig.WaterBankFreeboard.Value : 0.45f;

            // Decimate: block-MAX for ground (a missed bank peak would seat
            // water over the bank). Water is WET-AWARE: the raw field stores
            // DRY cells as terrain height (w = min(fill, terrain)), and the
            // old point-sample leaked that terrain into the reconcile as if
            // it were a spill level — every dip under the smoothed value
            // became a PHANTOM pond that exists in no water field, which the
            // basin walk (real water only) could never roll, classify,
            // freeze, flatten or excavate. User-diagnosed: walk capHi 51m vs
            // visible "lakes" at y138 — two different waters. Now: a block is
            // wet only if it CONTAINS real water (any w > g), its level is
            // the max wet level, and dry regions get NO surface (cut below).
            var dg = new float[n2 * n2];
            var dgLo = new float[n2 * n2];   // block MIN ground — the low side a waterfall pours over
            var dgDryLo = new float[n2 * n2];// block MIN ground over DRY subcells ONLY — the low BANK a floating edge hangs over
            var dw = new float[n2 * n2];
            var wetD = new bool[n2 * n2];
            // ⚠ EVERY PASS IN THIS FUNCTION IS PARALLEL, AND BIT-EXACT (2026-08-18, user: "why is it a single
            // thread op"). Measured before: `reconciled water surface … in 68.1s` — 68 of the ~90 s a cached
            // world takes to LOAD, single-threaded over 110,271,001 cells while ErodeHydraulic next door had
            // been running on all cores since GEN 144. There was no ordering constraint, only history.
            // Safe because every pass is a pure GATHER with DISJOINT WRITES: the separable windows ping-pong
            // between two arrays (dg→a→b→a→gov, dgDryLo→t→rimGov) and the smoothing alternates a↔b, so no cell
            // ever reads a value the same pass wrote. A row pass writes only its own row, a column pass only
            // its own column.
            // ⚠ AND THE RESULT IS IDENTICAL, not merely equivalent: these are MIN/MAX selections, not sums, so
            // there is no floating-point reassociation to change a bit. That is why this needs no GEN_VERSION
            // bump and no regen — an existing world loads faster and reconciles to the same surface.
            System.Threading.Tasks.Parallel.For(0, n2, y2 =>
            {
                int y0 = y2 * stride, y1b = Mathf.Min(n - 1, y0 + stride - 1);
                for (int x2 = 0; x2 < n2; x2++)
                {
                    int x0 = x2 * stride, x1b = Mathf.Min(n - 1, x0 + stride - 1);
                    float m = float.MinValue, mLo = float.MaxValue, mLoDry = float.MaxValue;
                    bool wetB = false;
                    float wmax = float.MinValue;
                    for (int yy = y0; yy <= y1b; yy++)
                    {
                        int row = yy * n;
                        for (int xx = x0; xx <= x1b; xx++)
                        {
                            float v = g[row + xx];
                            if (v > m) m = v;
                            if (v < mLo) mLo = v;
                            float wv = w[row + xx];
                            if (wv > v + 0.01f) { wetB = true; if (wv > wmax) wmax = wv; }
                            else if (v < mLoDry) mLoDry = v;   // DRY subcell → the low ground the water must sit under
                        }
                    }
                    int di = y2 * n2 + x2;
                    dg[di] = m;
                    dgLo[di] = mLo;
                    dgDryLo[di] = mLoDry;   // float.MaxValue for an all-wet block (no dry bank constraint)
                    wetD[di] = wetB;
                    // Dry blocks: no spill term — the bank term governs the
                    // (smooth) value so shore feather/blur math stays sane;
                    // the dry-region CUT below removes the surface entirely.
                    dw[di] = wetB ? wmax : float.MaxValue;
                }
            });

            var a = new float[n2 * n2];
            var b = new float[n2 * n2];
            // Separable MAX over ±r1: local rim height (the bank tops).
            System.Threading.Tasks.Parallel.For(0, n2, y =>
            {
                int row = y * n2;
                for (int x = 0; x < n2; x++)
                {
                    float m = float.MinValue;
                    int x0 = Mathf.Max(0, x - r1), x1 = Mathf.Min(n2 - 1, x + r1);
                    for (int i2 = x0; i2 <= x1; i2++) { float v = dg[row + i2]; if (v > m) m = v; }
                    a[row + x] = m;
                }
            });
            System.Threading.Tasks.Parallel.For(0, n2, x =>
            {
                for (int y = 0; y < n2; y++)
                {
                    float m = float.MinValue;
                    int y0 = Mathf.Max(0, y - r1), y1 = Mathf.Min(n2 - 1, y + r1);
                    for (int i2 = y0; i2 <= y1; i2++) { float v = a[i2 * n2 + x]; if (v > m) m = v; }
                    b[y * n2 + x] = m;
                }
            });
            // Separable MIN over ±r2: the lowest bank that governs the section
            // — the one the water would spill over first.
            System.Threading.Tasks.Parallel.For(0, n2, y =>
            {
                int row = y * n2;
                for (int x = 0; x < n2; x++)
                {
                    float m = float.MaxValue;
                    int x0 = Mathf.Max(0, x - r2), x1 = Mathf.Min(n2 - 1, x + r2);
                    for (int i2 = x0; i2 <= x1; i2++) { float v = b[row + i2]; if (v < m) m = v; }
                    a[row + x] = m;
                }
            });
            var gov = new float[n2 * n2];
            System.Threading.Tasks.Parallel.For(0, n2, x =>
            {
                for (int y = 0; y < n2; y++)
                {
                    float m = float.MaxValue;
                    int y0 = Mathf.Max(0, y - r2), y1 = Mathf.Min(n2 - 1, y + r2);
                    for (int i2 = y0; i2 <= y1; i2++) { float v = a[i2 * n2 + x]; if (v < m) m = v; }
                    gov[y * n2 + x] = m;
                }
            });
            // NEAR LOW-BANK floor: gov is built from block-MAX ground (dg), so a
            // sub-8m low dry bank — a point-bar, a channel-edge notch — hides under
            // its block's peak and gov can seat the sheet ABOVE it → a floating edge
            // at the 2m mesh (the "why is water floating over terrain" bug, worst on
            // Gaea imports where nothing else contains the river ribbon). dgDryLo is
            // the block's lowest DRY subcell (wet subcells excluded so the channel BED
            // never drags it down); its tight ±rEdge MIN is the nearest low bank the
            // water must sit under. rEdge stays small so an unrelated far valley never
            // over-drains a body; MaxValue (an all-wet reach) leaves gov the governor.
            int rEdge = Mathf.Max(1, Mathf.RoundToInt(12f / cell2));
            var rimGov = new float[n2 * n2];
            {
                var t = new float[n2 * n2];
                System.Threading.Tasks.Parallel.For(0, n2, y =>
                {
                    int row = y * n2;
                    for (int x = 0; x < n2; x++)
                    {
                        float mn = float.MaxValue;
                        int x0 = Mathf.Max(0, x - rEdge), x1 = Mathf.Min(n2 - 1, x + rEdge);
                        for (int i2 = x0; i2 <= x1; i2++) { float v = dgDryLo[row + i2]; if (v < mn) mn = v; }
                        t[row + x] = mn;
                    }
                });
                System.Threading.Tasks.Parallel.For(0, n2, x =>
                {
                    for (int y = 0; y < n2; y++)
                    {
                        float mn = float.MaxValue;
                        int y0 = Mathf.Max(0, y - rEdge), y1 = Mathf.Min(n2 - 1, y + rEdge);
                        for (int i2 = y0; i2 <= y1; i2++) { float v = t[i2 * n2 + x]; if (v < mn) mn = v; }
                        int idx = y * n2 + x;
                        rimGov[idx] = mn < gov[idx] ? mn : gov[idx];   // whichever bank the sheet spills over FIRST governs
                    }
                });
            }
            System.Threading.Tasks.Parallel.For(0, n2, y =>
            { int row = y * n2; for (int x = 0; x < n2; x++) { int i = row + x; a[i] = Mathf.Min(dw[i], rimGov[i] - freeboard); } });

            // DE-WOBBLE. Which bank counts as "the lowest" trades places cell
            // to cell along a channel, and every governance switch is a level
            // step — the flip/bump the sheet showed in steep terrain. Two 3x3
            // box passes (~5x5 tent over 8m cells) smooth the envelope so the
            // level follows the lower bank as ONE stable line; the re-clamp
            // after guarantees smoothing never stands water above the bank
            // that governs it.
            for (int it = 0; it < 2; it++)
            {
                System.Threading.Tasks.Parallel.For(0, n2, y =>
                {
                    int row = y * n2;
                    for (int x = 0; x < n2; x++)
                    {
                        int x0 = Mathf.Max(0, x - 1), x1 = Mathf.Min(n2 - 1, x + 1);
                        float sum = 0f; int cnt = 0;
                        for (int i2 = x0; i2 <= x1; i2++) { sum += a[row + i2]; cnt++; }
                        b[row + x] = sum / cnt;
                    }
                });
                // ⚠ The smoothing DOES sum, so the order of the adds decides the bits — but each output cell
                // sums its OWN fixed 3-window in a fixed order, and threading only changes which cells are
                // computed concurrently, never the sequence within one. Still bit-exact.
                System.Threading.Tasks.Parallel.For(0, n2, x =>
                {
                    for (int y = 0; y < n2; y++)
                    {
                        int y0 = Mathf.Max(0, y - 1), y1 = Mathf.Min(n2 - 1, y + 1);
                        float sum = 0f; int cnt = 0;
                        for (int i2 = y0; i2 <= y1; i2++) { sum += b[i2 * n2 + x]; cnt++; }
                        a[y * n2 + x] = sum / cnt;
                    }
                });
            }
            System.Threading.Tasks.Parallel.For(0, n2, y =>
            { int row = y * n2; for (int x = 0; x < n2; x++) { int i = row + x; if (a[i] > rimGov[i] - freeboard) a[i] = rimGov[i] - freeboard; } });

            // WATERFALL BREAK + MID-AIR GUARD. The raw field paints basin
            // polygons at spill level, so a waterfall face carries the
            // UPSTREAM level with the ground plunging away beneath it — that
            // is the sheet "running down the cliff" and the sky-high swim
            // trigger over the valley. Two cuts, marked with a sentinel every
            // consumer treats as NO WATER:
            //  * fall step — the level drops more than this to a 4-neighbour:
            //    a waterfall face, not a river reach. The sheet breaks at the
            //    lip and resumes in the pool; the face is reserved for future
            //    waterfall FX.
            //  * depth cap — the level stands further above the block's
            //    HIGHEST ground than any channel or lake we generate: mid-air.
            // Dry-region reach: real water within 2 cells (~16m) keeps its
            // shore margin (paint rise, skirt, mouth feather all read the
            // level slightly ABOVE the waterline); beyond that, NO surface.
            var wetN = new bool[n2 * n2];
            System.Threading.Tasks.Parallel.For(0, n2, y =>
            {
                int row = y * n2;
                for (int x = 0; x < n2; x++)
                {
                    bool any2 = false;
                    for (int dy = -2; dy <= 2 && !any2; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= n2) continue;
                        int rr2 = yy * n2;
                        int xa = Mathf.Max(0, x - 2), xb = Mathf.Min(n2 - 1, x + 2);
                        for (int xx = xa; xx <= xb; xx++)
                            if (wetD[rr2 + xx]) { any2 = true; break; }
                    }
                    wetN[row + x] = any2;
                }
            });

            var s = new short[n2 * n2];
            int nDry = 0, nOwned = 0;
            // A classed water cell here is a STAMPED BASIN (lake/pond/swamp — rivers are
            // still WC_NONE, classified below), and it keeps its OWN basin-settled level
            // (dw): NormalizeBasinWater/ClampStillWaterFinal already contained it to its
            // rim with FULL basin connectivity. The windowed dry-rim floor in a[] must
            // NOT touch a basin — it would leap a thin (<1 cell) rim into a lower valley
            // and DRAIN a perched tarn to the valley floor (bake proof: −181 m). RIVERS
            // and shore margins (WC_NONE) take the contained envelope a[] (gov + the near
            // low-bank dryRim), and THAT is what stops a river/import edge floating — on
            // gen AND on imports, which carry no stamped basins so every wet cell is a[].
            var wcls = _classS;
            bool clsOk = wcls != null && _classN == n2;
            // Counters accumulate PER ROW and are summed afterwards — a shared `nDry++` across threads would
            // both race and make the reported number depend on timing. The row totals are exact and the sum
            // is over a fixed-length int array, so the log line is identical to the serial version's.
            var rowDry = new int[n2]; var rowOwned = new int[n2];
            System.Threading.Tasks.Parallel.For(0, n2, y =>
            {
                int row = y * n2; int rd = 0, ro = 0;
                for (int x = 0; x < n2; x++)
                {
                    int idx = row + x;
                    float here = a[idx];
                    bool owned = clsOk && wcls[idx] != WC_NONE && wetD[idx];
                    if (owned) { here = dw[idx]; ro++; }
                    bool cut = false;
                    if (!wetN[idx]) { cut = true; rd++; }
                    s[idx] = cut ? SURF_CUT
                        : (short)Mathf.Clamp(Mathf.RoundToInt(here * PACK), short.MinValue + 1, short.MaxValue);
                }
                rowDry[y] = rd; rowOwned[y] = ro;
            });
            for (int y = 0; y < n2; y++) { nDry += rowDry[y]; nOwned += rowOwned[y]; }
            _surfS = s; _surfN = n2; _surfCell = cell2;
            FiresLogger.LogInfo($"[FiresWorldGen] reconciled water surface: {n2}x{n2} @ {cell2:0.#}m (stride {stride} from {n}) " +
                      $"rim±{r1}c gov±{r2}c dryRim±{rEdge}c freeboard={freeboard:0.00} smooth=2x3x3 " +
                      $"dryRegion={nDry:N0} ownedLevel={nOwned:N0} in {sw.Elapsed.TotalSeconds:0.0}s.");

            // CLASS FILL — basins were stamped by the walk; sea and channels
            // classify here, swamp biome overrides both. Suppressed cells
            // carry no class.
            if (_classS == null || _classN != n2) { _classS = new byte[n2 * n2]; _classN = n2; _classCell = cell2; }
            var cls2 = _classS;
            var rm = _riverMask;
            var bm = _biomeMap;
            int cOcean = 0, cRiver = 0, cLake = 0, cPond = 0, cSwamp = 0, cFroz = 0, cHot = 0, cWfall = 0;
            var altAll = new long[5]; var altFlow = new long[5];   // water / of which flowing, by altitude band (see the census below)
            for (int y = 0; y < n2; y++)
            {
                int row = y * n2;
                for (int x = 0; x < n2; x++)
                {
                    int idx = row + x;
                    if (s[idx] == SURF_CUT) { cls2[idx] = WC_NONE; continue; }
                    if (cls2[idx] == WC_NONE)
                    {
                        float lvl = s[idx] * UNPACK;
                        if (lvl <= 30.35f && dw[idx] > SEA - 1.5f) cls2[idx] = WC_OCEAN;
                        else if (rm != null)
                        {
                            int x0 = x * stride, y0 = y * stride;
                            int x1 = Mathf.Min(n - 1, x0 + stride - 1), y1 = Mathf.Min(n - 1, y0 + stride - 1);
                            for (int yy = y0; yy <= y1 && cls2[idx] == WC_NONE; yy++)
                            {
                                int rrow = yy * n;
                                for (int xx = x0; xx <= x1; xx++)
                                {
                                    // A spring pond carries MASK_STILL: flat water at a channel head, drawn and
                                    // shaded as the pond it is rather than as the river that leaves it.
                                    if (rm[rrow + xx] == MASK_STILL) { cls2[idx] = WC_POND; break; }
                                    if (rm[rrow + xx] == 1 || rm[rrow + xx] == 3) { cls2[idx] = WC_RIVER; break; }
                                }
                            }
                        }
                        if (cls2[idx] != WC_NONE && cls2[idx] != WC_OCEAN && bm != null)
                        {
                            int bi = Mathf.Min(n - 1, y * stride) * n + Mathf.Min(n - 1, x * stride);
                            if (bm[bi] == 2) cls2[idx] = WC_SWAMP;
                        }
                        // Flood-sheeted swamp water that no channel/basin
                        // stamped: swamp-biome wet cells ARE swamp water.
                        if (cls2[idx] == WC_NONE && wetD[idx] && bm != null)
                        {
                            int bi = Mathf.Min(n - 1, y * stride) * n + Mathf.Min(n - 1, x * stride);
                            if (bm[bi] == 2) cls2[idx] = WC_SWAMP;
                        }
                    }
                    // WATERFALL: a river/stream cell whose reconciled surface drops steeply to a
                    // neighbour — the neighbour's water level if wet, else its block MIN ground (the low
                    // side it pours over) — is a cascade a flat sheet can't draw. Tag it WC_WATERFALL for
                    // the waterfall shader. Catches genuine falls AND the "flat sheet over a cliff"
                    // floaters the ±gov window missed. ADDITIVE tag only — surface geometry is unchanged
                    // (the mesh still bends down); the renderer swaps material for these cells.
                    // A raised MARSH that drops to a lower neighbour is spilling over its OUTLET into the
                    // river below — the same waterfall/spillway category (user, 2026-07-30). Tag it too, at a
                    // gentler threshold, so a contained marsh's drainage lip reads as a small spillway, not a
                    // floating sheet edge.
                    bool inMarsh = bm != null && bm[Mathf.Min(n - 1, y * stride) * n + Mathf.Min(n - 1, x * stride)] == 2;
                    if ((cls2[idx] == WC_RIVER || cls2[idx] == WC_STREAM || inMarsh) && s[idx] != SURF_CUT)
                    {
                        float lvl = s[idx] * UNPACK, lowN = lvl;
                        if (x > 0)      { int j = idx - 1;  float c = (wetD[j] && s[j] != SURF_CUT) ? s[j] * UNPACK : dgLo[j]; if (c < lowN) lowN = c; }
                        if (x < n2 - 1) { int j = idx + 1;  float c = (wetD[j] && s[j] != SURF_CUT) ? s[j] * UNPACK : dgLo[j]; if (c < lowN) lowN = c; }
                        if (y > 0)      { int j = idx - n2; float c = (wetD[j] && s[j] != SURF_CUT) ? s[j] * UNPACK : dgLo[j]; if (c < lowN) lowN = c; }
                        if (y < n2 - 1) { int j = idx + n2; float c = (wetD[j] && s[j] != SURF_CUT) ? s[j] * UNPACK : dgLo[j]; if (c < lowN) lowN = c; }
                        float thresh = inMarsh ? 0.4f : 4.5f;   // marsh spillway (any water class in marsh biome): gentle drain reads as flowing, not a flat float; river cascade: steep
                        if (lvl - lowN > thresh) cls2[idx] = WC_WATERFALL;
                    }
                    switch (cls2[idx])
                    {
                        case WC_OCEAN: cOcean++; break;
                        case WC_RIVER: cRiver++; break;
                        case WC_LAKE: cLake++; break;
                        case WC_POND: cPond++; break;
                        case WC_SWAMP: cSwamp++; break;
                        case WC_FROZEN: cFroz++; break;
                        case WC_HOTSPRING: cHot++; break;
                        case WC_WATERFALL: cWfall++; break;
                    }
                    // (2026-08-16, user) WATER BY ALTITUDE — is there ANY mountain water? A priority flood can
                    // only find hollows that already exist, and noise mountains have very few near their tops,
                    // so high drainage is thin BY CONSTRUCTION — which is why alpine tarns have to be EXCAVATED
                    // (SeedMountainTarns) rather than discovered. That makes "did it work" un-answerable by
                    // looking at totals: 2,000 river cells could all be lowland. Banding it is the only way to
                    // see whether the mountains have running water at all — and the gorges and canyons in
                    // docs/CANYONS_AND_GORGES.md live exactly where this says water does or does not reach.
                    if (cls2[idx] != WC_NONE && cls2[idx] != WC_OCEAN)
                    {
                        float gEl = dgLo[idx];
                        int ab = gEl < 100f ? 0 : gEl < 250f ? 1 : gEl < 400f ? 2 : gEl < 600f ? 3 : 4;
                        altAll[ab]++;
                        if (cls2[idx] == WC_RIVER || cls2[idx] == WC_STREAM || cls2[idx] == WC_WATERFALL) altFlow[ab]++;
                    }
                }
            }
            // ═══ THE MAP SHOWS THE WATER THAT IS ACTUALLY THERE (2026-08-11, user: "the map overlay shows this
            // lake extends to where this river is, but it doesn't in reality … the ACTUAL water level in game
            // for ponds and lakes is correct, and I KNOW it's correct because the game puts the SAND at the
            // banks in the correct spot"). The class grid is built from the reconciled surface, which is baked
            // WITHOUT the live bank relief — but every render/physics/sand query ends with
            // `level -= ReliefFor(...)`, so the water the player sees sits ~0.84 m LOWER and covers LESS ground
            // than this grid claims. That gap is the phantom shoreline on the map (and the reason the sand ring
            // reads correct while the overlay does not). Re-test every water-classed cell against the SAME
            // query the world renders from, at 2 m resolution: if not one sub-cell is wet, the map does not
            // paint it. Sub-cell (not centre) sampling on purpose — a 5 m river inside an 8 m class cell has a
            // dry centre, and erasing those would delete the rivers from the map.
            // MEASURED, NOT ASSUMED: re-testing every water-classed cell against the rendered surface cleared
            // only 2 cells world-wide, so the class grid's COVERAGE is honest — the overlay is not painting
            // phantom water. What it does do is quantize: the map is an 8 m grid, so a shoreline anywhere
            // inside a block paints the whole block, over-drawing every edge by up to 8 m and visually merging
            // a pond with a river that passes within one cell of it. That is a map RESOLUTION limit, not a
            // water bug, and the pass that checked it cost a full-res 16×-oversampled scan for 2 cells — so it
            // is not kept. The genuine level error is that _surfS carries no bank relief (see the audit).

            // (2026-08-11, user) STAMP THE RECORDED CASCADE SPANS AS WATERFALL/RAPIDS. These cells carry NO
            // water (the sheet is deliberately hidden over a drop), so the wet-cell classifier above can never
            // see them — yet they are exactly where the falls are. Stamping them WC_WATERFALL turns each
            // deleted span into placement data for the FX pass, and makes "a fall belongs here" distinguishable
            // from "the river is broken here". Class-only: no water, no mesh, geometry untouched.
            int cascadeStamped = 0;
            lock (s_cascadeSpans)
                foreach (var span in s_cascadeSpans)
                    for (int k = 0; k + 1 < span.Length; k += 2)
                    {
                        int gx = Mathf.Clamp(Mathf.RoundToInt((span[k] + _half) / cellM), 0, n - 1);
                        int gz = Mathf.Clamp(Mathf.RoundToInt((span[k + 1] + _half) / cellM), 0, n - 1);
                        int idx2 = Mathf.Min(n2 - 1, gz / stride) * n2 + Mathf.Min(n2 - 1, gx / stride);
                        if (cls2[idx2] == WC_NONE) { cls2[idx2] = WC_WATERFALL; cascadeStamped++; cWfall++; }
                    }
            FiresLogger.LogInfo($"[FiresWorldGen] water classes (8m cells): ocean={cOcean:N0} river={cRiver:N0} lake={cLake:N0} " +
                      $"pond={cPond:N0} swamp={cSwamp:N0} frozen={cFroz:N0} hotspring={cHot:N0} waterfall={cWfall:N0} " +
                      $"(of which {cascadeStamped:N0} are recorded cascade spans — dry, mesh hidden, marked for FX).");
            {
                string[] band = { "  0-100", "100-250", "250-400", "400-600", "600+   " };
                var ab = new System.Text.StringBuilder("[FiresWorldGen] WATER BY ALTITUDE (band m | water cells | of which FLOWING (river/stream/fall) | % flowing) — if the high bands are empty the mountains have no water, and gorges/canyons have nothing to cut:");
                for (int ai = 0; ai < band.Length; ai++)
                    ab.Append($"\n      {band[ai]} | {altAll[ai],10:N0} | {altFlow[ai],10:N0} | {(altAll[ai] > 0 ? 100.0 * altFlow[ai] / altAll[ai] : 0),5:0.0}%");
                FiresLogger.LogInfo(ab.ToString());
            }
            s_repClsOcean = cOcean; s_repClsRiver = cRiver; s_repClsLake = cLake;
            s_repClsPond = cPond; s_repClsSwamp = cSwamp; s_repClsFrozen = cFroz; s_repClsHot = cHot;

            // ONE CLASS PER CONTIGUOUS BODY. Two seeded bowls can merge into
            // one visual lake while the walk classified them separately — one
            // rolled FROZEN, one didn't, and the sheet rendered half ice /
            // half liquid with no blend (user screenshot). Flood contiguous
            // still-water cells and rewrite each region to its majority class.
            {
                bool Still(byte c) => c == WC_LAKE || c == WC_POND || c == WC_SWAMP
                                   || c == WC_FROZEN || c == WC_HOTSPRING;
                var vis = new bool[n2 * n2];
                var st = new Stack<int>(1024);
                var members = new System.Collections.Generic.List<int>(1024);
                var tally = new int[9];
                int unified = 0;
                for (int i0 = 0; i0 < n2 * n2; i0++)
                {
                    if (vis[i0] || !Still(cls2[i0])) continue;
                    members.Clear();
                    System.Array.Clear(tally, 0, 9);
                    vis[i0] = true;
                    st.Push(i0);
                    while (st.Count > 0)
                    {
                        int i = st.Pop();
                        members.Add(i);
                        tally[cls2[i]]++;
                        int x2 = i % n2, z2 = i / n2;
                        if (x2 + 1 < n2 && !vis[i + 1] && Still(cls2[i + 1])) { vis[i + 1] = true; st.Push(i + 1); }
                        if (x2 > 0 && !vis[i - 1] && Still(cls2[i - 1])) { vis[i - 1] = true; st.Push(i - 1); }
                        if (z2 + 1 < n2 && !vis[i + n2] && Still(cls2[i + n2])) { vis[i + n2] = true; st.Push(i + n2); }
                        if (z2 > 0 && !vis[i - n2] && Still(cls2[i - n2])) { vis[i - n2] = true; st.Push(i - n2); }
                    }
                    byte bestC = 0; int bestN = 0;
                    for (int c = 0; c < 9; c++) if (tally[c] > bestN) { bestN = tally[c]; bestC = (byte)c; }
                    if (bestN == members.Count) continue;   // already uniform
                    unified++;
                    for (int k = 0; k < members.Count; k++) cls2[members[k]] = bestC;
                }
                if (unified > 0)
                    FiresLogger.LogInfo($"[FiresWorldGen] class unification: {unified} mixed still-water region(s) rewritten to their majority class.");
            }

            // NOTE: guaranteed no-float containment runs on the RAW 2m water field (ContainWaterField,
            // called before the field is packed) — that is the field the water MESH reads for wet cells
            // (TryGetWaterSurfaceSmooth samples _waterS for anything wet; _surfS here only feathers dry
            // shore margins). Enforcing here would have missed every genuinely-wet cell, so it lives at
            // full 2m resolution upstream instead.

            // Bake per-zone water flags from the FINAL class grid — the wet
            // paint stamp and diagnostics do a table lookup instead of any
            // runtime discovery. Vanilla zones are CENTERED on multiples of
            // 64 (GetZone: floor((p+32)/64)). A cell also marks PAINT into
            // every zone its 12m shore reach crosses.
            {
                _zoneHalfN = Mathf.CeilToInt(_half / 64f) + 2;
                _zoneN = _zoneHalfN * 2 + 1;
                var zw = new byte[_zoneN * _zoneN];
                for (int y = 0; y < n2; y++)
                {
                    float wzc = (y + 0.5f) * cell2 - _half;
                    int row = y * n2;
                    for (int x = 0; x < n2; x++)
                    {
                        byte c = cls2[row + x];
                        if (c == WC_NONE) continue;
                        float wxc = (x + 0.5f) * cell2 - _half;
                        byte bit;
                        switch (c)
                        {
                            case WC_OCEAN: bit = ZW_OCEAN; break;
                            case WC_RIVER:
                            case WC_STREAM:
                            case WC_WATERFALL: bit = ZW_RIVER; break;
                            case WC_FROZEN: bit = ZW_FROZEN; break;
                            default: bit = ZW_STILL; break;
                        }
                        int zxC = Mathf.FloorToInt((wxc + 32f) / 64f);
                        int zzC = Mathf.FloorToInt((wzc + 32f) / 64f);
                        int zx0 = Mathf.FloorToInt((wxc - 12f + 32f) / 64f), zx1 = Mathf.FloorToInt((wxc + 12f + 32f) / 64f);
                        int zz0 = Mathf.FloorToInt((wzc - 12f + 32f) / 64f), zz1 = Mathf.FloorToInt((wzc + 12f + 32f) / 64f);
                        for (int zz2 = zz0; zz2 <= zz1; zz2++)
                            for (int zx2 = zx0; zx2 <= zx1; zx2++)
                            {
                                int ix = zx2 + _zoneHalfN, iz = zz2 + _zoneHalfN;
                                if (ix < 0 || iz < 0 || ix >= _zoneN || iz >= _zoneN) continue;
                                byte v = zw[iz * _zoneN + ix];
                                v |= ZW_PAINT;
                                if (zx2 == zxC && zz2 == zzC) v |= bit;
                                zw[iz * _zoneN + ix] = v;
                            }
                    }
                }
                _zoneWater = zw;
            }

            // Shore distance transform: seed at wet blocks (level = the block's
            // real max wet level, class = the final class grid), 3-4 chamfer
            // out. Two passes over 8m cells; payload rides with the distance.
            {
                var sd = new float[n2 * n2];
                var sl = new short[n2 * n2];
                var sc = new byte[n2 * n2];
                const float SD_INF = 1e9f;
                for (int i = 0; i < n2 * n2; i++)
                {
                    if (wetD[i])
                    {
                        sd[i] = 0f;
                        sl[i] = (short)Mathf.Clamp(Mathf.RoundToInt(dw[i] * PACK), short.MinValue + 1, short.MaxValue);
                        sc[i] = cls2[i];
                    }
                    else sd[i] = SD_INF;
                }
                float orth = cell2, diag = cell2 * 1.41421356f;
                for (int y = 0; y < n2; y++)
                {
                    int row = y * n2;
                    for (int x = 0; x < n2; x++)
                    {
                        int i = row + x; float d = sd[i];
                        if (x > 0 && sd[i - 1] + orth < d) { d = sd[i - 1] + orth; sl[i] = sl[i - 1]; sc[i] = sc[i - 1]; }
                        if (y > 0)
                        {
                            if (sd[i - n2] + orth < d) { d = sd[i - n2] + orth; sl[i] = sl[i - n2]; sc[i] = sc[i - n2]; }
                            if (x > 0 && sd[i - n2 - 1] + diag < d) { d = sd[i - n2 - 1] + diag; sl[i] = sl[i - n2 - 1]; sc[i] = sc[i - n2 - 1]; }
                            if (x < n2 - 1 && sd[i - n2 + 1] + diag < d) { d = sd[i - n2 + 1] + diag; sl[i] = sl[i - n2 + 1]; sc[i] = sc[i - n2 + 1]; }
                        }
                        sd[i] = d;
                    }
                }
                for (int y = n2 - 1; y >= 0; y--)
                {
                    int row = y * n2;
                    for (int x = n2 - 1; x >= 0; x--)
                    {
                        int i = row + x; float d = sd[i];
                        if (x < n2 - 1 && sd[i + 1] + orth < d) { d = sd[i + 1] + orth; sl[i] = sl[i + 1]; sc[i] = sc[i + 1]; }
                        if (y < n2 - 1)
                        {
                            if (sd[i + n2] + orth < d) { d = sd[i + n2] + orth; sl[i] = sl[i + n2]; sc[i] = sc[i + n2]; }
                            if (x < n2 - 1 && sd[i + n2 + 1] + diag < d) { d = sd[i + n2 + 1] + diag; sl[i] = sl[i + n2 + 1]; sc[i] = sc[i + n2 + 1]; }
                            if (x > 0 && sd[i + n2 - 1] + diag < d) { d = sd[i + n2 - 1] + diag; sl[i] = sl[i + n2 - 1]; sc[i] = sc[i + n2 - 1]; }
                        }
                        sd[i] = d;
                    }
                }
                var db = new byte[n2 * n2];
                for (int i = 0; i < n2 * n2; i++)
                    db[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(sd[i] * 4f), 0, 255);
                _shoreD = db; _shoreL = sl; _shoreC = sc;
            }
            FiresMinimapRivers.OnWaterFieldReady();   // map may have generated first
        }

        // LIVE bank relief (metres): inland water RENDERS this far below its contained level so the banks
        // stand PROUD of the sheet. Applied at the surface queries below (mesh + swim + depth all read them),
        // NOT baked into the world — a slider changes it and a water re-mesh shows it instantly. Sea unaffected.
        // (v106, user) SPLIT PER FAMILY for live tuning: rivers get their own knob; ponds/lakes/springs/tarns
        // (everything that sits flat) share the STILL knob. The user dials both in-game, reads the numbers
        // that look right, and those get BAKED into the gen constants — no more regen-guess iteration.
        // (2026-08-11, user: "the studio doesn't know where the ACTUAL water level lies in game, and the actual
        // level for ponds and lakes IS correct — the game puts the SAND at the banks in the right spot")
        // These default to the SHIPPED slider value, not 0. Both surface queries end with
        // `level -= ReliefFor(...)`, so this offset IS the final in-game water level: the sand/wet band
        // (VoxelChunk._genWater ← TryGetWaterSurfaceGen) and the rendered sheet both go through it, which is
        // why the sand lands correctly. But the driver that pushes the slider only exists in the GAME — in the
        // studio the statics stayed 0, so the studio reported every water level 0.84 m TOO HIGH and its
        // coverage correspondingly too wide. Defaulting to the shipped value makes studio == game out of the
        // box; FiresWaterReliefDriver still overwrites both the instant a slider moves.
        public const float DefaultRiverRelief = 0.84f;   // keep in sync with FiresConfig "River bank relief"
        public const float DefaultStillRelief = 0.84f;   // keep in sync with FiresConfig "Still bank relief"
        public static float RiverRelief = DefaultRiverRelief;   // WC_RIVER / WC_STREAM / WC_WATERFALL
        public static float StillRelief = DefaultStillRelief;   // WC_LAKE / WC_POND / WC_SWAMP / WC_FROZEN / WC_HOTSPRING + marsh sheets

        // Which live relief applies at this position — river-family by the authoritative class grid,
        // still-family otherwise. One decimated-grid read; cheap enough for the hot surface queries.
        private static float ReliefFor(float wx, float wz)
        {
            byte c = WaterClassAtReal(wx, wz);
            return (c == WC_RIVER || c == WC_STREAM || c == WC_WATERFALL) ? RiverRelief : StillRelief;
        }

        // MARSH SHEET (user, 2026-07-30): the ONE flat water level of the swamp basin this cell belongs to —
        // returned regardless of whether the cell's OWN ground is above or below it. Islands/mounds/banks
        // stand ABOVE the sheet; the voxel view stores this on EVERY marsh column (not just wet ones) so the
        // water renders as a single continuous flat plane with the terrain poking up through it.
        //
        // 2026-08-09 CONSOLIDATION (user: "renderers obey the plan"): read the PLAN's marsh body — ONE flat constant
        // `Level` over the exact body footprint — NOT `biome==2 && raw _waterS`. The old form returned `_waterS`
        // per-cell: on a marsh-body footprint cell that IS the flat L (Conform wrote it), but on swamp-BIOME land
        // OUTSIDE any placed marsh body `_waterS == ground`, so it painted a terrain-following FAKE sheet at ground
        // height on every un-bodied swamp cell (the "doesn't lay flat / wrong elevation" bug). The plan body gives
        // one flat L and covers only real marsh footprints; SeaMarsh returns false (the ocean plane owns it).
        public static bool TryGetMarshSheet(float wx, float wz, out float level)
        {
            level = 0f;
            if (_plan == null) return false;
            if (!_plan.TryGetBodyAt(wx, wz, out var b)) return false;
            if (b.Kind != WaterBodyKind.Marsh) return false;   // Lake/Pond/Tarn have their own sheet path; SeaMarsh → ocean-owned
            if (b.Level <= SEA + 0.2f) return false;
            level = b.Level - StillRelief;                     // LIVE still-family relief; the whole flat sheet drops together
            return true;
        }

        // ── AUTHORITATIVE WATER LABELS ───────────────────────────────────────
        // ONE derivation, from the FINAL fields, at the end of BOTH boot paths
        // (gen + cache restore) — never re-derived at render time. Every rule
        // here is registry-free (pure persisted data), so gen, load and the
        // studio produce byte-identical labels.
        internal static void BuildWaterLabels(float[] g, float[] w, byte[] biomes, byte[] mask, int n)
        {
            if (g == null || w == null || biomes == null) { _labelS = null; return; }
            var lbl = (_labelS != null && _labelN == n) ? _labelS : new byte[n * n];
            float half = _half, cellM = (2f * half) / (n - 1);
            System.Threading.Tasks.Parallel.For(0, n, z =>
            {
                int row = z * n;
                float wz = z * cellM - half;
                for (int x = 0; x < n; x++)
                {
                    int i = row + x;
                    float wv = w[i], gv = g[i];
                    byte v = WC_NONE;
                    if (wv <= gv + 0.01f && mask != null && (mask[i] == 1 || mask[i] == 3))
                    {
                        // DRIED channel pinch (settle/clamp artifact): the river is ONE
                        // connected ribbon by construction — a dry mask cell with wet
                        // channel within 3 cells stays RIVER, so the sheet spans the
                        // pinch (its level = the reconciled flat extension) instead of
                        // showing bare bed mid-run (user's proof-view callout).
                        for (int dz = -3; dz <= 3 && v == WC_NONE; dz++)
                        {
                            int zz = z + dz; if (zz < 0 || zz >= n) continue;
                            int zr = zz * n;
                            for (int dx = -3; dx <= 3; dx++)
                            {
                                int xx = x + dx; if (xx < 0 || xx >= n) continue;
                                int j = zr + xx;
                                // Wet water within reach revives the pinch — but ONLY at a
                                // PLAUSIBLE level: the neighbour's surface must roughly reach
                                // this cell's ground (a genuine pinch sits between water at
                                // its own elevation). Without the band, cells at the FOOT of
                                // a fall revived with the TOP's water 18m up — the worst
                                // floating defect in the audit (worstRed 18.44 @ 4438,140).
                                if (w[j] > g[j] + 0.01f && w[j] >= gv - 0.5f && w[j] <= gv + 2.5f) { v = WC_RIVER; break; }
                            }
                        }
                    }
                    else if (wv > gv + 0.01f)
                    {
                        // channel FIRST — estuary ribbons run BELOW sea and must stay
                        // RIVER (the ocean test would swallow them into the plane).
                        // mask 2 (bank margin) INCLUDED: wet margins are river water —
                        // labeled LAKE they fragmented into sub-40-cell strips between
                        // RIVER cells and the droplet cull erased them (the forensic
                        // dump's rawOk=1/lab=0 red fringe).
                        if (mask != null && mask[i] != 0)
                        {
                            v = WC_RIVER;
                            byte c = WaterClassAtReal(x * cellM - half, wz);
                            if (c == WC_STREAM || c == WC_WATERFALL) v = c;
                        }
                        else if (wv <= SEA + 0.01f) v = WC_OCEAN;                       // sea-level water — the tidal plane owns it
                        // ═══ SWAMP IS LABELLED FROM THE PLAN, NOT FROM THE BIOME (2026-08-13) ════════════
                        // This was `biomes[i] == 2 && wv > SEA + 0.2f → WC_SWAMP`, with a comment claiming it
                        // matched TryGetMarshSheet. It no longer does: that function was fixed to require real
                        // footprint membership (_plan.TryGetBodyAt + Kind == Marsh) and this rule was left
                        // behind, so ANY above-sea swamp-biome cell got labelled marsh whether or not a marsh
                        // body existed under it. Measured with ZERO marshes placed: 295 swamp-classed cells,
                        // half of them dry in the field — pure phantom, and precisely the "map paints water the
                        // game does not have" failure. StampBodyClasses already stamps WC_SWAMP into _classS
                        // from the plan's Marsh bodies (authoritative Kind + Mask), and it runs before this, so
                        // the class lookup below is the correct source. Biome no longer decides where water is.
                        else
                        {
                            v = WC_LAKE;                                               // walk-kept still water
                            byte c = WaterClassAtReal(x * cellM - half, wz);
                            if (c == WC_POND || c == WC_SWAMP || c == WC_FROZEN || c == WC_HOTSPRING || c == WC_STREAM) v = c;
                        }
                    }
                    lbl[i] = v;
                }
            });
            // STRAY-DROPLET CULL — the walk can keep 2-3 cell puddles; rendered they
            // read as random specks of water on dry land (user's proof-view callout).
            // Flood-fill still-water components; smaller than 12 cells (≈48m²) → NONE.
            // Rivers/streams/marsh/ocean are never culled.
            {
                var seen = new bool[n * n];
                var stack = new System.Collections.Generic.Stack<int>();
                var comp = new System.Collections.Generic.List<int>(64);
                for (int i0 = 0; i0 < n * n; i0++)
                {
                    byte l0 = lbl[i0];
                    if (seen[i0] || (l0 != WC_LAKE && l0 != WC_POND && l0 != WC_FROZEN && l0 != WC_HOTSPRING)) continue;
                    comp.Clear(); stack.Push(i0); seen[i0] = true;
                    bool big = false;
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop();
                        comp.Add(i);
                        if (comp.Count >= 40) big = true;   // <40 cells (~160m²) = a puddle speck, not a pond
                        int x = i % n, z = i / n;
                        for (int e = 0; e < 4; e++)
                        {
                            int xx = x + (e == 0 ? 1 : e == 1 ? -1 : 0), zz = z + (e == 2 ? 1 : e == 3 ? -1 : 0);
                            if (xx < 0 || xx >= n || zz < 0 || zz >= n) continue;
                            int j = zz * n + xx;
                            byte lj = lbl[j];
                            if (!seen[j] && (lj == WC_LAKE || lj == WC_POND || lj == WC_FROZEN || lj == WC_HOTSPRING))
                            { seen[j] = true; stack.Push(j); }
                        }
                        if (big && stack.Count == 0) break;
                    }
                    if (!big) for (int k = 0; k < comp.Count; k++) lbl[comp[k]] = WC_NONE;
                }
            }
            _labelS = lbl; _labelN = n;
            FiresLogger.LogInfo($"[FiresWorldGen] water labels derived ({n}x{n}) — renderers obey these; wet/dry is never re-inferred.");
        }

        // ── THE sheet-node builder — ONE function for the GAME VIEW and the STUDIO
        // (user: the studio must be 1:1 with the game — shared code, not mirrored
        // code, so drift is impossible). World-anchored 1m nodes over [origin ..
        // origin+cells] with a SHEET_NODE_HALO ring:
        //   • wetness from the LABEL (the carvers' authority), level from the smooth
        //     query (read even when relief-clipped; raw cell backs it up) — the sheet
        //     fills the bed fully and extends through the banks;
        //   • three 3x3 passes relax stepped reaches into even ramps;
        //   • skirt dilation: bank-tuck where ground rises to meet it, and interior
        //     HOLE-BRIDGING (≥5 wet neighbours) so mid-channel pinches close — but
        //     never an overhang over low open ground (the red floating fringe).
        // Sheet-build scratch, pooled per thread — see the long note at their use in BuildSheetNodes.
        // ⚠ ThreadStatic, not a shared pool: the sheet build also runs off the main thread.
        [System.ThreadStatic] private static byte[] s_shDist, s_shTerm, s_shBur;
        [System.ThreadStatic] private static float[] s_shH0;

        public const int SHEET_NODE_HALO = 11;   // 2026-08-18: was 6. The halo bounds how far the sheet may EXTEND past its last wet node (renderer reads -2..C+2, so usable rings = NH-2). 6 allowed 4 rings — not wide enough on shallow shores (user, repeatedly: the mesh must extend until it clips terrain). 11 allows 8 passes + the outer ring = 9 cells of extension, identical across chunk borders.
        // Drape-ring counters (studio audit only — sheetedge reads them; the game never does).
        public static long DrapeWritten, DrapeRefused;
        // ═══ THE SHEET ASKS THE MESHED TERRAIN "AM I CLIPPING?", NOT THE ANALYTIC GRID ═══════════════════
        // (2026-08-19, user: "extending it without a guard makes it lay out correct but poke through terrain,
        // not extending it and using your logic it ends before terrain … fix the broken logic for 'is this
        // mesh clipping terrain'".)
        // Every version of that test in this file compared the level against `SampleFinalMeters` — a bilinear
        // read of the packed 2 m grid, i.e. the ANALYTIC surface. The terrain the player sees is SurfaceNets
        // over the voxel density, and WorldGeneratorBridge.GetGroundY says outright why those differ: "the
        // analytic isosurface can drift a metre or two from the quantised mesh", and the 3D warp and cave
        // carving move it "by up to tens of metres". Drift one way ⇒ we call a node buried while the drawn
        // ground is still below it ⇒ the sheet ENDS BEFORE THE TERRAIN. Drift the other ⇒ we keep extending
        // over ground that is really higher ⇒ the sheet POKES THROUGH. Both reported symptoms, one cause.
        // The veg/clutter scatter hit this exact wall and its fix is the precedent: seat on the MESH surface
        // or the placement floats/sinks. So the sheet now asks the same source.
        // ⚠ A DELEGATE, because the direction of the dependency forbids a call: BuildSheetNodes lives in
        // Fires.WorldGen and VoxelWorld lives in Fires.World, which references WorldGen and not the reverse
        // (adding that edge needs a documented row in docs/ARCHITECTURE.md — FiresAsmdefRuleTests parses it).
        // Fires.World installs this at startup; when it is null, or the column is not streamed/meshed yet,
        // the probe returns NaN and we fall back to the analytic sample — the pre-existing behaviour, so an
        // unmeshed column is never worse than it was.
        public static System.Func<float, float, float> MeshedGroundProbe;
        // ═══ WAS THIS SHEET BUILT FROM COMPLETE MESHED GROUND? ═══════════════════════════════════════════
        // (2026-09-03, for the per-chunk sheet cache.) The sheet is a pure function of the water field, the
        // edit overlay and the MESHED GROUND it clips against — and the last of those is not stable while
        // the world is streaming: `MeshedGroundProbe` returns NaN wherever the neighbour is not meshed yet,
        // and ClipGroundAt silently falls back to analytic terrain. A sheet built in that state can differ
        // at its clipped edge from one built once the neighbour arrived, so CACHING IT WOULD BE THE STALE
        // WATER EDGE this project spent 2026-09-02 chasing.
        // This flag makes the difference observable instead of assumed: a build that never needed the
        // fallback saw complete meshed ground and is safe to cache; a build that fell back even once is
        // provisional and the view refuses to keep it. Cheap, and it fails CLOSED — the fallback path is
        // the one that sets it, so any new caller that skips the probe also marks the build provisional.
        public static bool SheetProbeComplete;

        // === THE STORE GUARD REFUSED 1,007 OF 1,041 SHEETS AND HELD THE CACHE AT 140 OF 2,048 =========
        // (0907.1815 flight.) Refusing to cache ANY sheet that fell back was correct but far too blunt: it
        // is why ABSENT was 64 % of node misses while the cap was 6.8 % full. The cap was never the
        // constraint; this was.
        // WHY IT REFUSED: a sheet clipped against the ANALYTIC field instead of loaded chunk data may have
        // a wrong edge, and caching it would freeze that edge for every later visit.
        // WHAT MAKES CACHING SAFE INSTEAD: the entry is valid exactly while EVERY NODE THAT FELL BACK STILL
        // FALLS BACK. ClipGroundAt returns probe(wx,wz) when it resolves and the deterministic analytic
        // value when it does not, so the only thing that can change a cached node is a probe going
        // NaN -> value, i.e. chunk data arriving for that column.
        // So: record WHICH nodes fell back, store the sheet anyway, and re-probe just those nodes on a hit.
        // ⚠ MEASURED, not assumed: apply.water.probe NEVER RANKS in any window (< 25 ms/s against water's
        // 689 at peak) while relax + corners are 92 %. Re-probing the fallback subset is a small fraction of
        // a rebuild, so this trades a cheap exact check for the whole build.
        // ⛔ Do NOT "optimise" this into a per-chunk readiness test. TrySampleSurfaceY consults s_columns
        // (chunk DATA loaded), NOT s_views/ColliderReady - they are different registries, and
        // SurfaceColliderReady would answer a different question. Re-probing IS the predicate.
        private static byte[] s_fbMask;            // one bit per node, armed only during a build
        private static int s_fbBits, s_fbOriginX, s_fbOriginZ, s_fbNW;
        public static int SheetFallbackCount => s_fbBits;

        private static void ArmFallbackMask(float originX, float originZ, int nw)
        {
            int need = (nw * nw + 7) / 8;
            if (s_fbMask == null || s_fbMask.Length < need) s_fbMask = new byte[need];
            else System.Array.Clear(s_fbMask, 0, need);
            s_fbBits = 0; s_fbNW = nw;
            s_fbOriginX = Mathf.RoundToInt(originX); s_fbOriginZ = Mathf.RoundToInt(originZ);
        }

        private static void NoteFallbackAt(float wx, float wz)
        {
            if (s_fbNW <= 0) return;
            int nx = Mathf.RoundToInt(wx) - s_fbOriginX + SHEET_NODE_HALO;
            int nz = Mathf.RoundToInt(wz) - s_fbOriginZ + SHEET_NODE_HALO;
            if ((uint)nx >= (uint)s_fbNW || (uint)nz >= (uint)s_fbNW) return;   // off-grid caller, not ours
            int ni = nx + s_fbNW * nz;
            int by = ni >> 3, bit = 1 << (ni & 7);
            if ((s_fbMask[by] & bit) != 0) return;
            s_fbMask[by] = (byte)(s_fbMask[by] | bit); s_fbBits++;
        }

        /// <summary>Copy of the fallback bitmask for the sheet just built, or null if nothing fell back.</summary>
        public static byte[] CopyFallbackMask()
        {
            if (s_fbBits == 0) return null;
            int need = (s_fbNW * s_fbNW + 7) / 8;
            var c = new byte[need]; System.Array.Copy(s_fbMask, c, need); return c;
        }

        /// <summary>
        /// TRUE while every node in <paramref name="mask"/> still falls back, i.e. the cached grid is still
        /// exactly what a rebuild would produce. FALSE the moment any of them resolves - chunk data arrived.
        /// </summary>
        public static bool FallbacksStillHold(float originX, float originZ, int nw, byte[] mask)
        {
            if (mask == null) return true;
            var probe = MeshedGroundProbe;
            if (probe == null) return true;               // no probe -> everything falls back by definition
            int ox = Mathf.RoundToInt(originX), oz = Mathf.RoundToInt(originZ);
            int n = nw * nw;
            for (int ni = 0; ni < n; ni++)
            {
                if ((mask[ni >> 3] & (1 << (ni & 7))) == 0) continue;
                int nx = ni % nw, nz = ni / nw;
                float y = probe(ox + (nx - SHEET_NODE_HALO), oz + (nz - SHEET_NODE_HALO));
                if (!float.IsNaN(y)) return false;        // ground arrived here -> the cache is stale
            }
            return true;
        }
        // ═══ WHERE DOES THE SHEET BUILD ACTUALLY SPEND ITS TIME? ═════════════════════════════════════
        // ([engine/lod] hypothesis, 2026-09-03: both hot halves of apply.water share ONE primitive —
        // per-point worldgen field queries — and if that is right the fix shape is a per-sheet WINDOW
        // FETCH, not micro-tuning either loop.) QUERY = the per-node fill, which asks the field for a
        // surface and a class at every node. RELAX = everything after it: the dilation passes, the skirt
        // and the smooth, which touch only local arrays. Two numbers decide between two different fixes.
        public static long SheetQueryTicks, SheetRelaxTicks;
        // A/B PROOF DIAL for the bounded dilation scan: FIRES_SHEET_BOX=0 restores the full-grid scan, so the
        // two paths can be compared BIT-FOR-BIT on the same binary. The claim is equivalence, not similarity.
        // Separate dial for the DRY-CHUNK GATE, because it turned out to be the whole win and the bounded
        // box nothing: a chunk with no wet nodes cannot gain one (the pass only writes a dry cell that has a
        // WET neighbour), yet the old code still ran 8 full-grid passes over it. Most chunks are dry.
        [System.ThreadStatic] private static float[] s_shG;   // per-node MESHED GROUND memo — see the note at its use
        private static readonly bool s_sheetMemo =
            System.Environment.GetEnvironmentVariable("FIRES_SHEET_MEMO") != "0";
        private static readonly bool s_sheetGate =
            System.Environment.GetEnvironmentVariable("FIRES_SHEET_GATE") != "0";
        private static readonly bool s_sheetBox =
            System.Environment.GetEnvironmentVariable("FIRES_SHEET_BOX") != "0";
        // ═══ IS THE MESHED PROBE EQUIVALENT TO THE GEN GRID? MEASURE, DO NOT ASSUME ══════════════════════
        // (user, 2026-09-07: "verify that equivalence". `.probe` is 113.6 ms/s — HALF of .nodes.relax, which
        // is 95 % of .nodes, which is 69 % of apply.water. It is the largest identifiable cost inside the
        // game's largest cost, and it is a VOXEL RAYCAST where TryGetGenGround is a 0.02 us bilinear read.)
        // The hypothesis is that the voxel mesh reproduces _gridS, so the raycast is answering a question an
        // array read already answers. SurfaceDeltaAt's header says exactly that — "subtracting this from
        // SurfaceElev reproduces _gridS EXACTLY, which is the point: the voxel mesh and the gen grid become
        // ONE surface" — but ⚠ THAT IS A COMMENT, and a comment is not a measurement. Player sculpting,
        // LOD resolution and the mid-ring all deform the mesh away from the grid, and any of them would make
        // a substitution show up as water clipped to a bed that is not there.
        // So this samples both and reports the DISTRIBUTION of their disagreement. Every SAMPLE_EVERY-th
        // call that the probe answers also does the bilinear and accumulates |delta|. Cost is 1/64 of an
        // array read on a path that is doing a raycast.
        // ⚠ THE COUNT IS PRINTED AND IS LOAD-BEARING: a zero sample count means the probe never answered and
        // the deltas describe nothing. FIRES_PROBE_EQUIV=0 disables the sampling entirely.
        private const int PROBE_EQUIV_EVERY = 64;
        private static long s_peCalls, s_peSamples, s_peOver05, s_peOver50, s_peNaN;
        private static long s_refuseInside, s_refuseReach, s_refuseBeyondOnly;
        private static double s_peSum, s_peMax;
        private static readonly bool s_peOn =
            System.Environment.GetEnvironmentVariable("FIRES_PROBE_EQUIV") != "0";
        public static string ProbeEquivReport()
            => s_peSamples == 0
               ? $"[FiresWaterProbeEquiv] NO SAMPLES ({s_peCalls:N0} ClipGroundAt calls, {s_peNaN:N0} of them NaN/fallback) — the deltas below describe NOTHING; do not read them as agreement."
               : $"[FiresWaterProbeEquiv] {s_peSamples:N0} paired samples of {s_peCalls:N0} calls ({s_peNaN:N0} fell back): "
                 + $"mean |meshed - genGrid| {s_peSum / s_peSamples:F4} m, max {s_peMax:F3} m, "
                 + $"over 0.05 m {s_peOver05:N0} ({100.0 * s_peOver05 / s_peSamples:F2} %), "
                 + $"over 0.50 m {s_peOver50:N0} ({100.0 * s_peOver50 / s_peSamples:F2} %). "
                 + $"NEAR-ZERO means the raycast is answering what a 0.02 us bilinear already knows and can be "
                 + $"replaced; a FAT TAIL means the mesh genuinely deviates (sculpt/LOD) and it cannot.";

        // Sizes the reach-bounded guard — see the note at the tally in BuildSheetNodes.
        // INSIDE + REACH still refuse (a fallback there can change an emitted quad). BEYOND-ONLY is now
        // CACHED: those rings cannot propagate inward before the dilation passes run out.
        public static string RefusalSiteReport()
        {
            long refused = s_refuseInside + s_refuseReach;
            long tot = refused + s_refuseBeyondOnly;
            if (tot == 0)
                return "[FiresWaterRefusalSite] NO FALLBACKS RECORDED - either every probe answered, or this build is not reaching the tally. Do not read absence as agreement.";
            return "[FiresWaterRefusalSite] " + tot.ToString("N0") + " sheets had a fallback: "
                 + "REFUSED " + refused.ToString("N0") + " (" + s_refuseInside.ToString("N0") + " inside the footprint, "
                 + s_refuseReach.ToString("N0") + " within " + SHEET_DILATION_PASSES + " rings - both can reach an emitted quad), "
                 + "CACHED ANYWAY " + s_refuseBeyondOnly.ToString("N0") + " (" + (100.0 * s_refuseBeyondOnly / tot).ToString("F1")
                 + " % - fallbacks only beyond dilation reach, provably irrelevant to the mesh). "
                 + "The CACHED number is the win this bound was for; if it is ~0 the bound converted nothing and should be reverted.";
        }

        private static float ClipGroundAt(float wx, float wz) => ClipGroundAt(wx, wz, out _);

        // ═══ WHERE DID THE PROBE HAVE NO GROUND? FOOTPRINT OR HALO? ══════════════════════════════════════
        // (user, 2026-09-07: "instrument which refusals had a fallback inside the footprint".)
        // The node cache is now suppressed almost entirely by my own store guard — 6,573 UNSTAMPED misses
        // against 319 KEY-CHANGED — and every suppressed entry is a full rebuild, which is where the
        // 113 ms/s of raycasts live. The guard refuses to store a sheet if ClipGroundAt fell back ANYWHERE,
        // and the sheet's node grid reaches SHEET_NODE_HALO (11) cells beyond the chunk in every direction.
        // ⭐ So the question that decides whether the guard can be narrowed is WHERE the fallback was:
        //   • inside the chunk's OWN footprint -> the sheet really was clipped against terrain that did not
        //     exist yet, and caching it would freeze a wrong edge. The guard is earning its keep.
        //   • in the HALO only -> the footprint was fully meshed; the fallback happened out in the skirt
        //     where the sheet is being dilated toward ground it does not own. Those are the refusals a
        //     footprint-scoped guard would convert into cache hits.
        // `fellBack` reports it per call so the caller, which knows the node's grid position, can classify.
        private static float ClipGroundAt(float wx, float wz, out bool fellBack)
        {
            var probe = MeshedGroundProbe;
            if (probe != null)
            {
                float y = probe(wx, wz);
                if (!float.IsNaN(y))
                {
                    fellBack = false;
                    // sampled equivalence check — 1-in-64, only where the probe answered.
                    // MEASURED 2026-09-07 and the substitution is REFUTED: 99.14 % agree within 0.05 m but
                    // 0.60 % deviate past 0.50 m, max 49.4 m — caves, where the raycast finds the topmost
                    // solid and the analytic grid knows nothing of the void. Kept because it is nearly free
                    // and it is the standing evidence for why the raycast is not replaceable.
                    if (s_peOn && (++s_peCalls % PROBE_EQUIV_EVERY) == 0
                        && TryGetGenGroundReal(wx, wz, out float gg))
                    {
                        double d = y - gg; if (d < 0) d = -d;
                        s_peSamples++; s_peSum += d;
                        if (d > s_peMax) s_peMax = d;
                        if (d > 0.05) s_peOver05++;
                        if (d > 0.50) s_peOver50++;
                    }
                    return y;
                }
                s_peNaN++;
            }
            fellBack = true;
            // READ-THEN-WRITE rather than a bare store: this runs on EVERY fallback, and the flag is
            // write-once per build so the branch is almost always taken-and-skipped.
            // ⚠ NOT MEASURED — the bench A/B for this line was worthless (a peer Unity build was running
            // and both arms doubled). Kept on reasoning alone; do not cite a figure for it.
            NoteFallbackAt(wx, wz);   // the ONE chokepoint - every ClipGroundAt fallback passes here
            if (SheetProbeComplete) SheetProbeComplete = false;
            return SampleFinalMetersReal(wx, wz);
        }
        private static bool s_sheetStamped;
        // The sheet dilation reach, as ONE named constant so the log stamp and the loop cannot disagree.
        // Bounded by SHEET_NODE_HALO−2 (VoxelChunkView consumes nodes −2..C+2): exceeding that makes a
        // node depend on which chunk computed it, cracking every chunk border.
        public const int SHEET_DILATION_PASSES = 8;
        // Sheet smoothing: passes of a 3x3 average that may only SETTLE a node toward its own lower-bank
        // level, never lift it. JOIN is the bilateral cut — neighbours further apart in height than this are
        // a different surface (a pond beside a river, a waterfall lip) and must not be averaged together.
        public const int SHEET_SMOOTH_PASSES = 10;
        public const float SHEET_SMOOTH_JOIN = 3.0f;   // 2026-08-19: was 1.25 — which SKIPPED the 1-2 m steps inside a river channel (the vertical faces the user kept photographing) because the bilateral cut treated them as "a different surface". A river surface is ONE surface: its steps are the defect, not a feature. 3.0 smooths them and still refuses a real waterfall (measured lips are 20-90 m).
        // How far SUBMERGED fill may travel from the field's own wet set (cells). Burial is unbounded and
        // terminal; this bounds only the case with no bank to bury into, which is what floats.
        public const int SHEET_SUBMERGED_REACH = 3;

        // ⚠ A/B SWITCH: the spine must DESCEND along its own path — a cell standing above its upstream
        // neighbour is water running uphill. `FIRES_RIVER_DESCEND=0` disables it. Default ON.
        // Judge on the UPHILL WATER census (rise count + total climb), not on step counts: a descending step
        // is a waterfall and is wanted.
        internal static readonly bool s_riverSpineDescend =
            Environment.GetEnvironmentVariable("FIRES_RIVER_DESCEND") != "0";

        // ⚠ A/B SWITCH: enforce the non-increasing surface by ISOTONIC REGRESSION (pool-adjacent-violators)
        // instead of a running minimum. `FIRES_RIVER_ISOTONIC=0` restores the running-min. Default ON.
        // Judge on the OVERLAP CENSUS's STEPS count (along-channel grade), not on straight-line spread.
        internal static readonly bool s_riverIsotonic =
            Environment.GetEnvironmentVariable("FIRES_RIVER_ISOTONIC") == "1";   // OFF: A/B steps 121,684 -> 120,204 (-1.2 %) and berm 19.1 -> 21.1 % (worse). The running-min is NOT the staircase source.

        // ⚠ A/B SWITCH for the WEIR SHAPING (`lvl = max(sm, stair)`, which restores a running-min's flats
        // after the low-pass would have rounded them). `FIRES_RIVER_WEIR=0` uses the smooth profile
        // everywhere. Default ON = current behaviour. Judge on the OVERLAP CENSUS's STEPS count.
        internal static readonly bool s_riverWeirShaping =
            Environment.GetEnvironmentVariable("FIRES_RIVER_WEIR") != "0";   // ON = current. Removing it entirely moves steps only -5 %, so it is not the source either.

        // ⚠ A/B SWITCH: inside a CANYON/GORGE the level is taken from the FLOOR at the spine, not from a
        // perpendicular bank sample that lands on the wall. `FIRES_CANYON_FLOOR=0` restores the old shared
        // river rule. Default ON. Judge on riverfield's C. CANYON vs OPEN census.
        // ═══ ExcavateCanyons IS OFF, AND THE RENDER IS WHY ═══════════════════════════════════════════════
        // (2026-08-22, user: "we have built so many studio tools to allow you to visualize the water
        // generation and you just guess and get it wrong over and over". He was right — five text hypotheses
        // for the residual uphill rises were all refuted, and ONE render answered it.)
        // `watermesh <seed> <n> out.png 1400 <x> <z> 250` paints the incision class ORANGE and the float audit
        // MAGENTA. Aimed at this pass's own sites (printed by the INCISION SITES line in ClassifyChannelIncision)
        // it shows the pass classifies just TWO reaches in the whole 20 km world, and turns BOTH into lumpy
        // CRATERS: a chain of overlapping radially-symmetric discs (CANYON_DIG_R 26 m, dug flat to 45 % then a
        // wall), dry floors, standing water pooled in the lobes at different levels, and float ringing the rim.
        // A disc is isotropic. A canyon is a CHANNEL — long along the flow, narrow across it — so digging one
        // disc per classified cell can only make a tube where the classified cells form a long chain, and a
        // crater where they cluster. They cluster.
        // A/B'd out of ONE binary at n=10501 seed 126369233:
        //     UPHILL WATER rises                            108 -> 3        total climb 33 m -> 2 m
        //     BERM EARNED (bank cells holding up float)     438 -> 294      (-33 %)
        //     river wet cells                           174,905 -> 175,223  (MORE water, not less)
        //     exposed mesh edges                        0 / 159 -> 0 / 159  (unchanged)
        // And rendered at the same site with the pass off, the craters are gone and the reach is a continuous
        // channel with water running its length — which is the thing the user asked for ("canyons need to
        // carve out a bit wider to allow for an actual river to run through the bottom of them").
        // ⚠ THE PASS IS KEPT, NOT DELETED, AND THE INTENT IS STILL WANTED. The canyons the player actually
        // stands in are cut by the CARVE's incision bound (see the note at `boundLo` in InciseRivers), not by
        // this; this pass is the hook for doing them DELIBERATELY. The rework it needs is a cross-section
        // ORIENTED ACROSS THE FLOW instead of a disc — then a classified chain becomes a slot rather than a
        // string of pits. `FIRES_CANYON_EXCAVATE=1` turns it back on to work on that.
        // ⚠ A/B SWITCH for the canyon FLOOR SECTION (bowl | flat shelf | cliff) added 2026-08-31 at the
        // user's spec. `FIRES_CANYON_SHELF=0` turns it off so a before/after comes out of ONE binary with
        // the shelf as the only variable. Default ON. Fires only where the thalweg's natural ground stands
        // CANYON_SLOT_M above the water, and only ever LOWERS, so no river that already works can move.
        internal static readonly bool s_canyonShelf =
            Environment.GetEnvironmentVariable("FIRES_CANYON_SHELF") != "0";

        internal static readonly bool s_canyonExcavate =
            Environment.GetEnvironmentVariable("FIRES_CANYON_EXCAVATE") == "1";

        internal static readonly bool s_canyonFloorLevel =
            Environment.GetEnvironmentVariable("FIRES_CANYON_FLOOR") != "0";

        // ⚠ A/B SWITCH for the river SURFACE SMOOTH (the nearest-spine-cell mosaic — the lumps).
        // `FIRES_RIVER_SMOOTH=0` disables it. Default ON. RIVER-ONLY; judge on riverfield N. LEVEL SPREAD.
        internal static readonly bool s_riverSurfaceSmooth =
            Environment.GetEnvironmentVariable("FIRES_RIVER_SMOOTH") != "0";

        // ⚠ A/B SWITCH: the river level comes from the DRAINAGE FILL TABLE (the reference's rule) instead of
        // the per-polyline bank-derived ribbon — docs/WATER_REFERENCE_DIVERGENCE.md §2. The bank sample is
        // kept as its CEILING. `FIRES_RIVER_FILLLEVEL=0` restores the bank-derived value. RIVER-ONLY.
        internal static readonly bool s_riverFillLevel =
            Environment.GetEnvironmentVariable("FIRES_RIVER_FILLLEVEL") == "1";   // OFF: A/B'd as a NO-OP (spread p50 0.77 -> 0.76) because it caps with min(bankL,bankR)-RELIEF, which is STRICTER than the fill term almost everywhere, so the min always picks the bank. The reference caps with hOrig[i]-0.5 AT THE CELL, which is looser. This is not yet the reference's rule.

        // ⚠ A/B SWITCH for the restored per-cell river level CEILING (`lvl <= hOrig - 0.5`), the line the
        // reference generator has and v91 deleted here — docs/WATER_REFERENCE_DIVERGENCE.md §1.
        // `FIRES_RIVER_CEILING=0` restores the v91 behaviour. Default ON. RIVER-ONLY; still water untouched.
        internal static readonly bool s_riverLevelCeiling =
            Environment.GetEnvironmentVariable("FIRES_RIVER_CEILING") == "1";   // OFF: A/B'd at n=10501 as a NO-OP on our bank-derived level (spread p50 0.76 -> 0.77). It is the guard on a FILL-TABLE level, not a fix on its own — see the note at its use site.

        // ⚠ A/B SWITCH: does the PLAN's globally-solved level beat 56,865 independent local solves?
        // `FIRES_RIVER_PLANLEVELS=0` restores the old overwrite in SetBankDerivedLevels. Judge it on
        // `riverfield`'s N. LEVEL SPREAD — the only metric that can see a multi-level sheet. Default ON.
        internal static readonly bool s_riverPlanLevels =
            Environment.GetEnvironmentVariable("FIRES_RIVER_PLANLEVELS") == "1";   // OFF by default: used RAW it dries 70% of the network — see the note at its use site

        // ⚠ A/B SWITCH for the enclosed-hole fills in both rim passes (GEN_VERSION 152). `FIRES_RIVER_FOOTPRINT=0`
        // turns them off so a before/after comes out of ONE binary with the change as the only variable. Default ON.
        // ⚠ A/B SWITCH: solve each run with the PLANNER'S LAW on the FINAL terrain (see its use in
        // SetBankDerivedLevels) instead of the per-node local bank guess. `FIRES_RIVER_RESOLVE=0` restores
        // the local guess. Default ON.
        internal static readonly bool s_riverResolveOnFinal =
            Environment.GetEnvironmentVariable("FIRES_RIVER_RESOLVE") != "0";


        internal static readonly bool s_riverFootprintFlood =
            Environment.GetEnvironmentVariable("FIRES_RIVER_FOOTPRINT") != "0";

        // ═══ THE CONNECTED SHORE FLOOD IS WRITTEN, MEASURED, AND DELIBERATELY OFF ════════════════════════════
        // (2026-08-19.) Giving rivers ExpandFootprint's rule — grow into any connected cell whose ground lies
        // below the level — is the right end state and the code below implements it. A/B'd out of one binary at
        // n=10501 on seed 126369233 with `riverfield gen`, it added 95,208 cells (river wet 299,385 → 394,696,
        // +31.8 %) and it MEASURED WORSE on the two things that matter most:
        //     STEPS between adjacent river cells   >0.5 m  14,348 → 39,608   (2.8×)
        //                                          >1 m     6,141 → 29,023   (4.7×)
        //                                          >3 m       730 → 11,585   (15.9×)
        //     river→SEA junction step p90              0.42 m → 4.64 m,  max 0.81 → 8.06 m
        //     bank cells at exactly sheet+1.0          59.1 % → 63.3 %   (MORE curb, not less)
        // The reason is an ORDERING, and it is the same shape of mistake as GEN 146 (rivers were given the
        // pond's BURIAL rule while still lacking its FOOTPRINT rule, which is what built the curbs and islands
        // this file has been chasing since). Here it is the other half: the footprint cannot be widened while
        // the LEVEL is still a latched staircase. SetBankDerivedLevels solves the surface as a running-MIN from
        // the source composed with a running-MAX from the mouth, which is a flattening operator — measured, the
        // median step between adjacent river cells is 0.000 m while 2,869 pairs step over 3 m in one 2 m cell.
        // Widening the footprint just paints that staircase over 32 % more ground, and where two reaches at
        // different latched levels now meet, their footprints touch and the step becomes a visible wall.
        // ⚠ TURN THIS ON ONLY AFTER THE LEVEL SOLVE IS FIXED, AND RE-RUN THE SAME A/B. Not before. The
        // measurement above is the whole reason GEN 143 had to be reverted a second time.
        internal static readonly bool s_riverShoreFlood =
            Environment.GetEnvironmentVariable("FIRES_RIVER_SHOREFLOOD") == "1";
        public static int BuildSheetNodes(float originX, float originZ, int cells, ref float[] nodeH, ref float[] nodeD, ref float[] scratch, ref byte[] nodeF)
        {
            SheetProbeComplete = true;   // cleared by ClipGroundAt if it ever falls back — see the flag's note
            ArmFallbackMask(originX, originZ, cells + 2 * SHEET_NODE_HALO + 1);   // NW is not in scope yet; same expression
            bool fbInside = false, fbReach = false, fbBeyond = false;   // where a fallback happened — see the footprint/halo note
            long _tq = System.Diagnostics.Stopwatch.GetTimestamp();   // phase split: field QUERIES vs RELAX passes
            int NH = SHEET_NODE_HALO, NW = cells + 2 * NH + 1;
            // ⚠ SAY WHICH SHEET CODE IS RUNNING, ONCE PER PROCESS. 2026-08-18: an entire night was spent
            // unable to answer "are your changes even in the build?" from the one artifact the user can
            // produce — Player.log. Water changes are silent code paths, so a stale DLL, a stale chunk-mesh
            // cache and a working fix are INDISTINGUISHABLE on screen. This line makes them distinguishable:
            // grep SHEET BUILD. If it is missing, this build predates the stamp; if the numbers are not what
            // you just compiled, the DLL did not land; if they are right and the water still looks wrong, the
            // fault is real and not a deployment ghost.
            if (!s_sheetStamped)
            {
                s_sheetStamped = true;
                FiresLogger.LogInfo($"[FiresVoxelWater] SHEET BUILD — halo {SHEET_NODE_HALO}, dilation {SHEET_DILATION_PASSES} passes "
                        + $"(reach {SHEET_DILATION_PASSES + 1} cells), unconditional=yes, frame {NW}x{NW}, "
                        + $"branch={(_plan != null ? "PLANNED" : "PLANLESS (restored world)")}, smooth {SHEET_SMOOTH_PASSES}x3x3 join {SHEET_SMOOTH_JOIN:0.00}m (settle-only). "
                        + "The sheet extends this far past the field's wet edge at the lowest wet neighbour's level; terrain clips it. "
                        + "⚠ BOTH branches dilate since 2026-08-18 — before that only PLANNED did, and a restored world (every relaunch) got none.");
            }
            if (nodeH == null || nodeH.Length < NW * NW)
            { nodeH = new float[NW * NW]; nodeD = new float[NW * NW]; scratch = new float[NW * NW]; nodeF = new byte[NW * NW]; }
            var H = nodeH; var D = nodeD; var Sc = scratch; var F = nodeF;
            var plan = _plan;
            for (int nz = 0; nz < NW; nz++)
                for (int nx = 0; nx < NW; nx++)
                {
                    float wx = originX + (nx - NH), wz = originZ + (nz - NH);
                    int ni = nx + NW * nz;
                    F[ni] = 0;
                    float sl = 0f, sd = 0f;
                    bool ok;
                    if (plan != null)
                    {
                        // ═══ (v106b) ONE RENDER SOURCE — THE 2m FIELD — FOR EVERYTHING ═══ (user, after the
                        // floating-pond proof): the vegetation grounds on the DATA, the sand/bed paints from
                        // the DATA, the lily pads and labels read the DATA — and only the STILL-BODY SHEET
                        // was still answering from the PLAN lookup. Two consequences, both fatal: (a) the
                        // live relief sliders apply inside the FIELD query, so the Still slider could never
                        // move a plan-rendered pond; (b) load-time passes (basin normalize / still-water
                        // clamp) adjust the FIELD after gen — the plan never re-reads it, so plan sheets
                        // floated whole metres above the world every other system agreed on. Bodies now
                        // render exactly like rivers: from TryGetWaterSurfaceSmooth over _waterS — Conform
                        // baked their levels into that field at gen, so coverage is identical; the skirt
                        // below buries the edges; sea-owned marsh cells sit ≤ SEA in the field and the
                        // `> SEA` gate excludes them. The PLAN is GEN-ONLY now (placement + conform);
                        // render == data == physics == veg, and BOTH relief sliders work on everything.
                        // THE RIVER ENDS AT THE WATERLINE (2026-08-12, user: "why the fuck do I have a river mesh
                        // extending so far into the ocean … it should end right here at the sand line from the
                        // ocean and just dive its mesh into the ground and end"). REVERTS the estuary exception
                        // I added in GEN_VERSION 119, which let CHANNEL cells keep drawing below sea so the
                        // ribbon could "blend into" the ocean. It did the opposite: the mouth profile descends
                        // to ESTUARY_FLOOR (SEA−6), so once drawn it trailed a ribbon out across the sea floor
                        // and ramped up coastal banks at y≈28 — under the 30 m waterline, through the ocean.
                        // The `> SEA` gate is right: the OCEAN owns every surface at or below sea level, the
                        // river sheet stops at the shoreline and its last quad dives into the bed. The defect
                        // that exception was chasing (a dry carved trench at the coast) was never a render
                        // problem — it was the carve going dry, and that is fixed (dry core channel cells: 0).
                        // ═══ THE OCEAN OWNS EVERY COLUMN IT CAN EVER REACH — TIDE INCLUDED (2026-08-13) ═══
                        // (user, with a screenshot of a dozen river ribbons standing as slabs out over open
                        // water: "our logic to cull the mesh when the sea owns the space is flawed because it
                        // doesn't take into account the actual tides"; "we have one river just floating out
                        // over the ocean".) `sl > SEA` compares against the CONSTANT 30, but the ocean surface
                        // is SEA + TideY and TideY runs to −5 at spring low (FiresEnv). So a mouth ribbon
                        // sitting at, say, 30.1 passed this gate, and the moment the tide receded it was left
                        // standing up to FIVE METRES above the sea — a floating plank, visible from a long way
                        // off. The surface test can never catch that, because the defect is not where the
                        // ribbon's surface is; it is where its BED is.
                        // The right question is "can the sea ever occupy this column?", and the answer is yes
                        // for every cell whose bed lies at or below the HIGHEST tide (which is exactly SEA —
                        // FiresEnv TideHighOffsetM = 0, the ocean only ever recedes). Such a column belongs to
                        // the ocean at high water and is bare seabed / tidal flat at low water; either way an
                        // inland sheet there is wrong. Gating on the bed also delivers what was asked for
                        // earlier — "it should end right here at the sand line and just dive its mesh into the
                        // ground and end" — because the bed reaches SEA exactly at the high-tide shoreline.
                        // ⚠ THE OCEAN CAN ONLY OWN A COLUMN IT CAN REACH (2026-08-18, sheetedge at n=10501: 80 m
                        // free-standing water faces at (6916..6944, 5792..5800) — water standing at 110 m over ground at
                        // EXACTLY 30.00). The bed test above says "the sea can occupy any column whose bed lies at or
                        // below high tide", and it is right at a coast — but ExcavateCanyons clamps every slot floor at
                        // exactly SEA (Math.Max(SEA, top − depth)), so a canyon dug under standing water 80 m above the
                        // sea produced bed == SEA INLAND, the gate dropped those columns, and the sheet had a HOLE punched
                        // in it with an 80 m rim. Physically the sea reaches a column only if it is hydraulically
                        // connected to it, and a surface standing more than a tidal range above the sea is, by the
                        // field's own leveling, NOT connected (were it connected the level would BE ~SEA). So a
                        // sub-tide bed only hands the column to the ocean when the water on it is also within reach of
                        // the sea. Coastal mouth ribbons (30.0–30.5 over a 29.x bed — the 08-13 floating-plank case)
                        // stay gated; inland standing water over a dug floor draws. The field is not touched.
                        ok = TryGetWaterSurfaceSmoothReal(wx, wz, out sl, out sd) && sl > SEA && ((sl - sd) > SEA || sl > SEA + 1f);
                        if (ok)
                        {
                            byte wc = WaterClassAtReal(wx, wz);
                            if (wc != WC_RIVER && wc != WC_STREAM && wc != WC_WATERFALL) F[ni] = 1;   // still family → body skirt rules (pocket-wall deep tolerance)
                        }
                    }
                    else
                    {
                        // planless worlds (old caches / faithful imports): the label path
                        byte wlab = WaterLabelAt(wx, wz);
                        ok = wlab != WC_NONE && wlab != WC_OCEAN;
                        if (!ok && TryGetRawWaterCell(wx, wz, out float erl, out _) && erl > 30.4f) ok = true;
                        if (ok)
                        {
                            TryGetWaterSurfaceSmoothReal(wx, wz, out sl, out sd);
                            if (TryGetRawWaterCell(wx, wz, out float rl, out float rd)
                                && (sl <= 28.3f || sl - rl > 1.5f)) { sl = rl; sd = rd; }
                            ok = sl > 28.05f;
                        }
                    }
                    H[ni] = ok ? sl : 0f;
                    D[ni] = ok ? sd : 0f;
                }
            // PLAN WORLDS: keep the plan's exact LEVELS (no relax — it averaged levels
            // and lifted steep ribbon reaches above their banks, sweep-proven), but DO
            // run a PER-NODE skirt so the sheet never ends short of the shore (user:
            // "the river mesh ends short of the terrain"). Per-node = shared corners
            // stay identical across quads AND across the game/studio (no per-quad
            // invented corner → no facets, no cracks). GROUND-GATED: a dry node only
            // takes water where the terrain there stands at/above that level — a bank
            // that BURIES the edge (conformance raised those banks) — or where it
            // bridges an interior pinch (≥5 wet neighbours). Never over open low
            // ground, so the skirt cannot float. Extend at the LOWEST wet neighbour so
            // a descending ribbon never rides UP into the bank.
            SheetQueryTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _tq; _tq = System.Diagnostics.Stopwatch.GetTimestamp();
            if (plan != null)
            {
                // ⚠ 8 PASSES + the outer ring = up to 9 cells of travel to reach the bank (2026-08-18, user:
                // "make the sheet stop ending so easily, make it extend its mesh PAST the dry cells" — and,
                // when 4 cells still ended short on shallow sand shelves: "YOU JUST DONT BUILD THE MESH WIDE
                // ENOUGH"). The pass count is bounded by the halo, not by taste: VoxelChunkView reads nodes
                // from −2 to C+2, each pass propagates one cell inward from the frame edge, so the outermost
                // node the renderer uses is only guaranteed identical between neighbouring chunks for
                // ≤ SHEET_NODE_HALO−2 total rings (passes + the drape ring). Exceeding that makes a node's
                // value depend on which chunk computed it: a CRACK along every chunk border. Raising the reach
                // therefore means raising the HALO (6 → 11 today, frame 45² → 55² ≈ 1.5× nodes), never just
                // the loop count.
                // ═══ THE SHEET IS SIMPLY WIDER, IN EVERY DIRECTION, AT THE LEVEL IT ALREADY HAS ═══
                // (2026-08-18, user, after I kept adding conditions: "we already have the proper LEVELS for the
                // mesh, we just EXTEND THEM UNTIL THEY CLIP TERRAIN … LET THEM EXTEND UP TO 4 CELLS in ANY
                // DIRECTION TO CLIP … THEY JUST NEED TO BE WIDER MESHES".)
                // Nothing is gated. Not on ground height, not on wet/dry, not on body identity, not on a
                // far wall, not on a terminal ring. Four unconditional dilations of the wet set, each node
                // taking the LOWEST wet neighbour's level so a descending ribbon never rides up. Where the
                // ground is above that level the terrain hides the extra rings — that IS the clip, and it is
                // why over-extending costs nothing. Where the ground is below it, the water was supposed to be
                // there anyway.
                // Every previous attempt in this file (0.1 m gate, 0.35 m river bridge, 0.9 m pocket + far
                // wall, ground−0.15 drape, divide cut, terminal ring) was an answer to "how far MAY we
                // extend". That was the wrong question and it produced visible edges every single time. The
                // answer is a fixed radius and let the terrain do the cutting.
                // Nodes the FIELD already made wet are sources, but a field node whose own ground stands above
                // its level is itself already buried — seed those as terminal so the sheet does not crawl out
                // through a bank it is already tucked under.
                // dist = rings from the FIELD's own wet set. 0 = the field said wet; n = n dilations out.
                // Submerged fill is capped on this; burial is not (a bank must always be reachable), and a
                // buried node is terminal so nothing propagates past it.
                // ═══ POOLED, NOT ALLOCATED PER CALL ══════════════════════════════════════════════════════
                // ([engine/lod], 2026-09-03, measured: `apply.water` is the apply pass's TOP ALLOCATOR at
                // 0.8–1.7 MB/s steady, and — after they split `apply.water.probe` out — its top COST too,
                // 373–403 ms/s streaming with the ground probe only 3–43 of it. The bulk is here.)
                // These four scratch buffers were `new`-ed on every call. At SHEET_NODE_HALO = 11 that is
                // NW = cells + 23, so three byte[NW²] plus a float[NW²] — order 20 KB per sheet build, all
                // of it dead the moment the call returns. That is the allocation stream, and every byte of
                // it is pure garbage-collector food for buffers whose contents never outlive the frame.
                // ⚠ WHAT MAKES THIS SAFE IS WHICH ONES ARE FULLY OVERWRITTEN. `dist` is assigned for every
                // index by the loop directly below, and `H0` is an Array.Copy of the whole grid — those two
                // carry nothing from the previous call. `term` and `bur` are written CONDITIONALLY and read
                // as "0 unless set", so they must be zeroed explicitly; `new[]` was doing that for free and
                // Array.Clear (a memset) is what replaces it. Getting that backwards would leak one chunk's
                // burial flags into the next, which would look like a water defect and read like a ghost.
                // ThreadStatic: the sheet build runs off the main thread as well, and a shared pool would
                // hand two workers the same scratch.
                int NN = NW * NW;
                if (s_shDist == null || s_shDist.Length < NN)
                { s_shDist = new byte[NN]; s_shTerm = new byte[NN]; s_shBur = new byte[NN]; }
                var dist = s_shDist;
                var term = s_shTerm;
                System.Array.Clear(term, 0, NN);   // conditionally written below — must start zeroed
                // bur[i] = 1 when node i's own ground stands at/above the level it carries. The CELL test
                // below needs both corners, so a node must remember whether it was buried, not just whether
                // it was wet.
                var bur = s_shBur;
                System.Array.Clear(bur, 0, NN);   // read as "0 unless set" by the CELL test — must start zeroed
                for (int i = 0; i < NW * NW; i++) dist[i] = H[i] > 0f ? (byte)0 : (byte)255;
                // ═══ THE MESHED-GROUND PROBE IS MEMOISED PER CALL ════════════════════════════════════════
                // ⚠ I TRIED THIS ON 09-03, MEASURED IT SLOWER (0.421 vs 0.391 ms/call), AND REVERTED IT.
                // That measurement was INVALID and this is the correction. `riverfield` runs with
                // MeshedGroundProbe == null, so ClipGroundAt always took its cheap ANALYTIC fallback — the
                // bench was A/B-ing a memo against a thing that costs nothing, and of course the bookkeeping
                // lost. In game the probe is a VOXEL RAYCAST: the user's 09-07 log puts `apply.water.probe`
                // at 24-42 ms/s synchronous and 100-137 ms/s once deferral made it succeed more often.
                // ⭐ ClipGroundAt is called per candidate node per dilation pass, and the ground under a node
                // is a pure function of its position — identical on all 8 passes. So a node visited across
                // passes paid for up to 8 raycasts to learn the same number.
                // FIRES_SHEET_MEMO=0 restores the per-pass call, and THIS TIME THE A/B MUST BE SCORED IN
                // GAME on `apply.water.probe`, not on the studio bench, which cannot see the cost at all.
                if (s_sheetMemo)
                {
                    if (s_shG == null || s_shG.Length < NN) s_shG = new float[NN];
                    var GM = s_shG;
                    for (int i = 0; i < NN; i++) GM[i] = float.NaN;
                }
                // ═══ THE DILATION ONLY SCANS WHERE WATER CAN REACH ═══════════════════════════════════════
                // ([engine/lod] measured apply.water as the apply pass's top cost; my own split then put 88 %
                // of BuildSheetNodes in these relax passes and only 12 % in field queries, which killed the
                // window-fetch idea and pointed here instead.)
                // The pass below skips a cell in its first line unless the cell is DRY AND has a wet
                // neighbour — so every node outside the wet set's bounding box, plus one ring, is guaranteed
                // to do nothing. It was still being visited: 8 passes x 2,809 interior nodes = ~22k visits,
                // most of them on a chunk whose water occupies one corner or no cells at all.
                // Bounding the scan is EXACTLY EQUIVALENT rather than an approximation: dilation grows the
                // wet set by at most one ring per pass, so expanding the box by one ring per pass covers
                // every node that could possibly change. Nothing in the loop body is touched.
                // ⚠ The box is in NODE space and clamped to [1, NW-2], the same interior the old loop used.
                int bx0 = NW, bx1 = -1, bz0 = NW, bz1 = -1;
                for (int nz2 = 0; nz2 < NW; nz2++)
                    for (int nx2 = 0; nx2 < NW; nx2++)
                        if (H[nx2 + NW * nz2] > 0f)
                        {
                            if (nx2 < bx0) bx0 = nx2; if (nx2 > bx1) bx1 = nx2;
                            if (nz2 < bz0) bz0 = nz2; if (nz2 > bz1) bz1 = nz2;
                        }
                if (bx1 >= bx0 || !s_sheetGate)
                for (int pass = 0; pass < SHEET_DILATION_PASSES; pass++)
                {
                    System.Array.Copy(H, Sc, NW * NW);        // read the snapshot, write H
                    // grow the box one ring per pass — that is exactly how far the wet set can advance
                    bx0--; bx1++; bz0--; bz1++;
                    int sx0 = bx0 < 1 ? 1 : bx0, sx1 = bx1 > NW - 2 ? NW - 2 : bx1;
                    if (!s_sheetBox) { sx0 = 1; sx1 = NW - 2; }
                    int sz0 = bz0 < 1 ? 1 : bz0, sz1 = bz1 > NW - 2 ? NW - 2 : bz1;
                    if (!s_sheetBox) { sz0 = 1; sz1 = NW - 2; }
                    for (int nz = sz0; nz <= sz1; nz++)
                        for (int nx = sx0; nx <= sx1; nx++)
                        {
                            int ni = nx + NW * nz;
                            if (Sc[ni] > 0f) continue;
                            float lo = float.MaxValue; int cnt = 0; bool bodyN = false;
                            byte nearest = 255;
                            byte srcBur = 0;   // was the neighbour we grow from itself buried? (the cell's far corner)
                            for (int dz = -1; dz <= 1; dz++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    if (dx == 0 && dz == 0) continue;
                                    int nj = ni + dx + NW * dz;
                                    float nv = Sc[nj];
                                    // ⚠ A TERMINAL NEIGHBOUR IS A DEAD END — it already intersects terrain, and the
                                    // sheet must not step over a containment bank to keep going (2026-08-19, user:
                                    // "our extend 9 cells needs to NOT extend once it actually intersects terrain,
                                    // because now it will extend out past the containment bank"). Skipping it as a
                                    // SOURCE is what makes the reach stop AT the bank instead of counting 9 cells
                                    // regardless: growth dies on the first buried ring in every direction, so the
                                    // 9 is a CEILING for how far a shore may be chased, never a distance the sheet
                                    // is entitled to travel.
                                    if (nv > 0f && term[nj] == 0)
                                    {
                                        cnt++; if (F[nj] == 1) bodyN = true;
                                        if (nv < lo) { lo = nv; srcBur = bur[nj]; }   // the neighbour we grow FROM = the cell's other corner
                                        if (dist[nj] < nearest) nearest = dist[nj];
                                    }
                                }
                            if (cnt == 0) continue;
                            float wx = originX + (nx - NH), wz = originZ + (nz - NH);
                            // (v98) BODY SHORES take a DEEPER gate: the planner's footprint flood is
                            // 4-connected and the overlap referee can notch it, so rare aliased shore
                            // pockets sit just UNDER the level outside the footprint — the sheet stopped
                            // short of the bank there (user screenshots). A body may extend over a shallow
                            // under-level shelf (≤0.9m — the pond covering its own missed shore pocket);
                            // rivers keep the strict 0.1m gate (their dry margins are bermed, and a loose
                            // gate there is the historical ride-up-the-bank float). Body identity (F=1)
                            // propagates so multi-pass extension stays scoped to bodies.
                            // (v100) POCKET-ONLY: the deep tolerance alone laid overhanging TONGUES on
                            // shores that slope down AWAY from the body (floating shards on the grass,
                            // seed 724107467). A pocket by definition has a far WALL — the deep gate now
                            // also requires a DRY 8-neighbour whose ground stands at/above the level.
                            // Genuine notches bridge; open descending shores keep the strict gate.
                            // memoised for this call — same node, same ground, every pass (see the note above)
                            // memoised: same node, same ground, every pass (see the note above)
                            float gHere;
                            if (s_sheetMemo)
                            {
                                gHere = s_shG[ni];
                                if (float.IsNaN(gHere))
                                {
                                    s_shG[ni] = gHere = ClipGroundAt(wx, wz, out bool fb);
                                    // === 2026-09-07: THIS `if (fb)` WAS MISSING. It voided BOTH the ======
                                    // === instrument AND the guard below. Do not remove it again. ========
                                    // Without it the classification ran on every memo MISS - the first touch
                                    // of every node - not on every FALLBACK. Every build touches its own
                                    // footprint, so `fbInside` was true on essentially every build: the
                                    // report read 85 % "inside the footprint" where the honest instrument it
                                    // replaced read 9 %, and the guard degenerated into "refuse always".
                                    // The 0907.0612 flight's 2.6 % CACHED ANYWAY is VOID - it measured which
                                    // rings a build TOUCHED, not where ground was missing.
                                    // A captured-and-never-read variable is the tell: `fb` was the only thing
                                    // here that separates a fallback from an ordinary probe.
                                    if (fb)
                                    {
                                    // classify WHERE: inside the chunk's own footprint, or out in the halo
                                    // ═══ CLASSIFY BY REACH, NOT BY "IS IT THE HALO" ═════════════════
                                    // The dilation advances AT MOST ONE RING PER PASS and runs
                                    // SHEET_DILATION_PASSES (8) times; the halo is SHEET_NODE_HALO (11)
                                    // rings; and the emit loop covers only the chunk's own cells. So a
                                    // node more than 8 rings outside the footprint CANNOT influence any
                                    // emitted quad — that is geometry, not optimism, and a fallback out
                                    // there is irrelevant to what the player sees.
                                    // ⚠ The reverse is NOT true and is why this is not simply "halo is
                                    // safe": a fallback in rings 1-8 CAN propagate inward across passes
                                    // and change whether a footprint node ends up wet. Those still refuse.
                                    // OFF BY ONE, fixed in the same pass: cell z uses nodes NH+z and
                                    // NH+z+1, so the LAST footprint corner is node NH+cells (43), not
                                    // NH+cells-1. `>=` called that corner ring 1, mislabelling a whole row
                                    // and column of the footprint as halo. Ring 1 refuses too, so only the
                                    // COUNTER lied - the guard behaved the same either way.
                                    int ring = 0;
                                    if (nx < NH) ring = NH - nx; else if (nx > NH + cells) ring = nx - (NH + cells);
                                    if (nz < NH) { int rz = NH - nz; if (rz > ring) ring = rz; }
                                    else if (nz > NH + cells) { int rz = nz - (NH + cells); if (rz > ring) ring = rz; }
                                    if (ring == 0) fbInside = true;
                                    else if (ring <= SHEET_DILATION_PASSES) fbReach = true;
                                    else fbBeyond = true;
                                    }
                                }
                            }
                            else gHere = ClipGroundAt(wx, wz);   // MESHED terrain, not the analytic grid
                            // ═══ THE EDGE OF THE SHEET SITS UNDER TERRAIN, ALWAYS ═══════════════════════════
                            // (2026-08-18, user, after four rounds of me over-thinking this: "its really not
                            // super complicated the edge of the mesh should sit UNDER terrain always, this
                            // prevents us from ever seeing the edges, so sheets extend until their edge is
                            // clipped into terrain".)
                            // The old gate had it exactly inverted: `gHere >= lo - 0.1f` extends ONLY onto ground
                            // that is ALREADY at/above the level — the case that is already buried and needs no
                            // help — and REFUSES the one case that matters, a neighbour whose ground lies BELOW
                            // the level. That node is under water by definition, so refusing it leaves the last
                            // wet node standing above open ground: a visible free edge. Every fix I attempted
                            // tonight (a 0.35 m river bridge, a `ground − 0.15` drape, a divide cut) was a
                            // variation on the same wrong question — how far MAY we extend — when the rule is
                            // simply: extend until the edge is buried.
                            // So: extend into ANY dry neighbour, and mark a node TERMINAL once its own ground
                            // stands at/above the level it carries. A terminal node is the buried edge — it is
                            // included (that is what hides the seam) but it does not propagate, which is what
                            // bounds the growth. Submerged nodes keep travelling outward until they reach one.
                            // `terminal` is why this cannot float: over open descending ground the passes simply
                            // run out (4 rings) instead of marching, and that residue is COUNTED below rather
                            // than hidden — those are the reaches whose level must come DOWN, the user's other
                            // instruction ("if it cannot intersect terrain on one edge, drop the cross section"),
                            // which belongs in the gen-side level solve, not here.
                            // ⚠⚠ UNCONDITIONAL WAS WRONG — IT FLOATS (2026-08-18, user screenshot: the sheet
                            // standing as a slab above the grass with a hard vertical face).
                            // "Extend 9 cells in any direction" has no stop over OPEN LOW GROUND: beside a
                            // channel where the land stays below the water level there is never a bank to bury
                            // into, so the dilation marches the full reach at the water's level and hangs in the
                            // air. That is the v100 tongue failure, re-created by removing the gate.
                            // The user's actual rule is the one to encode — "the edge of the mesh should sit
                            // UNDER terrain always" — which means the two cases are NOT the same:
                            //   • BURIAL (ground at/above the level): always allowed, at any distance. This is
                            //     the edge tucking into a bank, it is invisible, and it is the goal. It is also
                            //     TERMINAL — nothing propagates past a buried node, so it cannot run away.
                            //   • SUBMERGED FILL (ground below the level): only within SUBMERGED_REACH of the
                            //     field's own wet set. That covers the real gap — the 2 m field vs 1 m nodes
                            //     interpolation, and small shore pockets the footprint missed — without
                            //     inventing water across a floodplain the field never called wet.
                            // So a shore reaches its bank, and open low ground gets at most SUBMERGED_REACH
                            // metres and then stops, instead of a 9 m slab in mid-air.
                            // BURIED = this node's own ground stands at/above the level it would carry, i.e. the
                            // sheet INTERSECTS TERRAIN here. That is the goal state: include the node so the seam
                            // is hidden under the bank, then STOP — `term` below makes it a dead end.
                            // ═══ "ARE BOTH EDGES OF THIS CELL UNDER TERRAIN? THEN CLIP IT." ══════════════════
                            // (2026-08-19, the user's own statement of the rule, and it is the right one.)
                            // A rendered quad spans TWO nodes. It only becomes invisible when terrain covers
                            // BOTH of its corners — cover one and you still see the half that is above ground.
                            // Testing a single node was the error: the sheet stopped at the FIRST node whose
                            // ground reached the level, which leaves the quad joining it to the previous node
                            // still partly exposed — the mesh "ending before terrain". So `buried` still marks
                            // this node, but termination now needs the PAIR: this node buried AND the neighbour
                            // it grew from buried, i.e. a whole cell under terrain. The sheet therefore always
                            // lays one ring PAST first contact, which is exactly the ring that hides the seam,
                            // and stops there instead of marching on to poke out further along the bank.
                            bool buried = gHere >= lo - 0.05f;
                            // Submerged nodes (ground below the level) are genuinely under water, so they may keep
                            // travelling — but only SHEET_SUBMERGED_REACH rings from the field's own wet set, which
                            // is what stops the sheet marching across open low ground as a slab in mid-air.
                            byte myDist = nearest == 255 ? (byte)255 : (byte)(nearest + 1);
                            bool ext = buried || myDist <= SHEET_SUBMERGED_REACH;
                            // ⚠ RIVERS DELIBERATELY DO NOT GET THE DEEP-TOLERANCE BRIDGE — MEASURED, 2026-08-18.
                            // GEN_VERSION 143 briefly gave rivers a 0.35 m far-wall bridge on the theory that the
                            // user's "sheet ends short of the bank" screenshots were slightly-under-level shore
                            // notches stopping the skirt. `sheetedge` (the rendered-sheet audit that theory had
                            // never been checked against) A/B'd the gate at n=10501 over 400 channel chunks:
                            // the bridge extended the sheet by TWO nodes world-wide and visible river lips went
                            // 155 → 158. The notch population the bridge was built for barely exists, and a
                            // ≤0.35 m bridge cannot remove a >0.35 m lip by definition. The REAL exposed-edge
                            // population measures p50 ≈ 2.1 m — the bank sits metres BELOW the sheet, which no
                            // ground-gated skirt may cover without floating — with a p90 ≈ 47 m cliff tail
                            // (canyon lips, where CascadeSplitPolys — measured 0 splits at n=10501, CASCADE_MIN_M
                            // 12 m vs 0.56 m node spacing — never cuts the sheet). Those want an edge treatment
                            // at the MESH (an apron on exposed boundary edges in VoxelChunkView, the successor of
                            // the removed dry-corner fill) or the cascade — not a looser skirt gate here.
                            if (ext)
                            {
                                H[ni] = lo; D[ni] = Mathf.Max(0.1f, D[ni]); if (bodyN) F[ni] = 1;
                                dist[ni] = myDist;
                                // TERMINAL = the buried edge, and it is a DEAD END. Its own ground stands at or
                                // above the level it carries, so the terrain already hides this ring and there is
                                // nothing beyond it worth drawing. Marking it here is what makes the sheet stop AT
                                // the containment bank instead of stepping over it and carrying on for the
                                // remaining rings — the defect the user hit once the reach went to 9 cells.
                                bur[ni] = buried ? (byte)1 : (byte)0;
                                // BOTH corners under terrain ⇒ this whole cell is invisible ⇒ clip: stop here.
                                // One corner buried is NOT enough — that quad is still half-visible, and stopping
                                // on it is what made the sheet end before the terrain.
                                if (buried && srcBur == 1) term[ni] = 1;
                            }
                        }
                }
                // ⚠ THE DRAPE RING — THE SHEET NOW ENDS *INSIDE* THE GROUND, NEVER IN MID-AIR
                // (2026-08-18, user, pointing at a rendered edge: "the yellow water meshes should
                // extend and clip through the magenta terrain mesh … thats the whole issue im having
                // is the grid stops, it doesnt clip"). The ground-gated skirt above is the BURIAL
                // half — it may only extend where the bank stands at/above the level, so wherever the
                // shore ground sits BELOW the sheet (sheetedge, n=10501: 155 free river edges, drop
                // p50 2.1 m with a p90 47 m canyon-lip tail) the sheet used to stop with a free-
                // standing edge. This ring is the CLIP half: every dry node still unclaimed beside a
                // wet one takes min(lowest wet neighbour level, ITS OWN ground − 0.15) — pinned UNDER
                // its own terrain — so the last row of quads dives through the surface and the visible
                // waterline is the water/terrain intersection. It cannot float by construction (the
                // v100 tongue bug extended a LEVEL over low ground; this extends BELOW ground), and it
                // is one shared value per world node, so neighbouring chunks agree and the faithful-
                // coverage rule's anti-crack argument is preserved. ONE ring suffices: the wet→drape
                // quad crosses the terrain INSIDE its own cell (water above ground at the wet corner,
                // below it at the drape corner), and the drape edge itself is buried. 8-neighbour
                // adjacency on purpose — convex sheet corners need the diagonal node wet for the
                // all-four-corners rule to emit the corner quad.
                System.Array.Copy(H, Sc, NW * NW);
                for (int nz = 1; nz < NW - 1; nz++)
                    for (int nx = 1; nx < NW - 1; nx++)
                    {
                        int ni = nx + NW * nz;
                        if (Sc[ni] > 0f) continue;
                        float lo = float.MaxValue;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0) continue;
                                float nv = Sc[ni + dx + NW * dz];
                                if (nv > 0f && nv < lo) lo = nv;
                            }
                        if (lo == float.MaxValue) continue;
                        float g = SampleFinalMetersReal(originX + (nx - NH), originZ + (nz - NH));
                        // ⚠ A SHORE, NOT A FALL (2026-08-18, user: "the entire water logic is built on leveling from
                        // the lower of the two banks, there physically shouldn't be a bank dropped out from under it
                        // unless we have a river that is flowing in that direction and it's flowing downhill"). The
                        // corrected sheetedge dump at n=10501 bears that out: with the tide gate fixed, every remaining
                        // free edge is either a sea-level shore step (≤ 2 m: pocket shelves, estuary beds) or a
                        // WATERFALL LIP — a river at 52.7 m dropping into a tide-owned pool at 30.1, a lake at 76 m
                        // spilling over its rim into a valley 46 m below. The first is a shore and must clip; the second
                        // is flowing downhill, belongs to CascadeSplitPolys / the waterfall FX, and draping it would
                        // hang a 23–46 m water CURTAIN — worse than the lip. So the ring clips only where the ground is
                        // within DRAPE_MAX_DROP below the sheet, and leaves anything deeper as the lip it is.
                        // ⚠ THE RING CARRIES THE LEVEL, NOT `ground − 0.15`. Pinning it under the terrain was
                        // backwards: on a shore whose ground is BELOW the level that pushed the water surface
                        // beneath the ground exactly where it should have been metres deep, which is what made
                        // the sheet stop short. Everything now extends at `lo` and the TERRAIN does the cutting.
                        float v = lo;
                        if (v <= 0.05f) { DrapeRefused++; continue; }   // world floor / sea fringe — the tidal plane owns those shores
                        H[ni] = v; D[ni] = Mathf.Max(0.1f, D[ni]); DrapeWritten++;
                    }
                // ═══ SMOOTH BACK FROM THE LOWER BANK — DOWNHILL ONLY ════════════════════════════════════
                // (2026-08-18, user, once the channel finally filled: "it just needs to smooth so its not so
                // lumpy … IT SHOULD SMOOTH BACK FROM THE LOWER BANK HEIGHT RIGHT?" — yes, and that is the
                // existing law: SetBankDerivedLevels sets each node to min(bankL,bankR) − RELIEF.)
                // The planned branch has carried NO relax at all, and the note above says why: an earlier
                // average "lifted steep ribbon reaches above their banks, sweep-proven". That failure is not
                // smoothing per se — it is smoothing that may RAISE. So this one cannot:
                //   • every node is clamped to its OWN pre-smoothing level (H0), which IS its lower-bank
                //     level. The filter may settle a lump DOWN toward the bank; it can never lift water over
                //     one. The 2026 sweep regression is unreachable by construction, not by tuning.
                //   • BILATERAL: a neighbour only contributes if it is within SHEET_SMOOTH_JOIN of this node.
                //     Without that, a pond at 40 beside a river at 36 would drag its own shore down a metre,
                //     and a waterfall lip would smear into a ramp. Ripples inside one surface are well under
                //     the threshold; a genuine step between two surfaces is well over it, so steps survive
                //     exactly as they are and only the lumpiness goes.
                {
                    // pooled — Array.Copy overwrites every element, so nothing carries over (see the note above)
                    if (s_shH0 == null || s_shH0.Length < NW * NW) s_shH0 = new float[NW * NW];
                    var H0 = s_shH0;
                    System.Array.Copy(H, H0, NW * NW);
                    for (int it = 0; it < SHEET_SMOOTH_PASSES; it++)
                    {
                        System.Array.Copy(H, Sc, NW * NW);
                        for (int nz = 1; nz < NW - 1; nz++)
                            for (int nx = 1; nx < NW - 1; nx++)
                            {
                                int ni = nx + NW * nz;
                                float own = Sc[ni];
                                if (own <= 0f) continue;
                                float sum = 0f; int cnt = 0;
                                for (int dz = -1; dz <= 1; dz++)
                                    for (int dx = -1; dx <= 1; dx++)
                                    {
                                        float nv = Sc[ni + dx + NW * dz];
                                        if (nv > 0f && Mathf.Abs(nv - own) <= SHEET_SMOOTH_JOIN) { sum += nv; cnt++; }
                                    }
                                if (cnt < 2) continue;
                                float avg = sum / cnt;
                                H[ni] = avg < H0[ni] ? avg : H0[ni];   // settle toward the lower bank; never above it
                            }
                    }
                }
                SheetRelaxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _tq;
            // Tally the refusal by WHERE its fallback was. HALO-ONLY is the interesting population: the
            // chunk's own footprint was fully meshed, so the sheet over the chunk is sound and only the
            // dilation skirt reached unmeshed ground. Those are the entries a footprint-scoped guard would
            // cache, and this number is the size of that opportunity — measured before anyone changes the
            // guard, because narrowing it wrongly reinstates the stale clipped edge it exists to prevent.
            // ⭐ THE GUARD NOW REFUSES ONLY ON A FALLBACK THAT CAN REACH THE EMITTED CELLS.
            // Rings beyond SHEET_DILATION_PASSES cannot propagate inward before the passes run out, so a
            // sheet whose only fallbacks were out there is as sound as one with no fallback at all, and
            // refusing it was pure loss: measured 1,278 of 1,405 refusals (91 %) were halo-only, and every
            // refusal is a full rebuild carrying the raycasts that dominate .nodes.relax.
            if (fbInside || fbReach)
            {
                SheetProbeComplete = false;                      // genuinely provisional — do not cache
                if (fbInside) s_refuseInside++; else s_refuseReach++;
            }
            else if (fbBeyond) s_refuseBeyondOnly++;              // safe: cached despite the fallback
                return NW;
            }
            // ═══ THE PLANLESS PATH GETS THE SAME DILATION — THIS IS THE ONE THAT ACTUALLY SHIPS ═══════════
            // (2026-08-18. The user reported the sheet ending short ~50 times; five successive rewrites of the
            // skirt above changed NOTHING on screen, and this is why.)
            // EVERY dilation, skirt and drape written above lives inside `if (plan != null)` and returns at
            // the line above. But `_plan` is a GEN-TIME object: `RestoreFromArtifact` — the path EVERY
            // launch of an existing world takes — never sets it (see its own line, `if (art.Water != null &&
            // _plan == null)`, which assumes null). So a restored world falls through to the code BELOW,
            // which has no dilation at all: the sheet is exactly the raw field's wet set and stops dead at the
            // last wet cell. The studio measured "waterline gap 0" because `TryLoad` DOES rebuild the plan from
            // the .bin, so the studio took the plan branch — studio and game were running different code, which
            // is precisely the drift the shared builder exists to prevent.
            // So: run the identical unconditional dilation here. Same rule, same reach, same reason —
            // extend at the LOWEST wet neighbour's level and let the terrain do the cutting.
            for (int pass = 0; pass < SHEET_DILATION_PASSES; pass++)
            {
                System.Array.Copy(H, Sc, NW * NW);
                for (int nz = 1; nz < NW - 1; nz++)
                    for (int nx = 1; nx < NW - 1; nx++)
                    {
                        int ni = nx + NW * nz;
                        if (Sc[ni] > 0f) continue;
                        float lo = float.MaxValue;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0) continue;
                                float nv = Sc[ni + dx + NW * dz];
                                if (nv > 0f && nv < lo) lo = nv;
                            }
                        if (lo == float.MaxValue || lo <= 0.05f) continue;
                        H[ni] = lo; D[ni] = Mathf.Max(0.1f, D[ni]);
                    }
            }
            // (Cross-section spine-max flatten REVERTED — it lifted edge nodes to the
            // spine and the skirt then draped that level onto banks and mid-river rocks:
            // the riding-up-the-banks regression in the user's screenshots. The level
            // bar between banks is the GEN's job (ContainRiverWaterBelowBanks), not a
            // render-side lift.)
            for (int p = 1; p <= 3; p++)
            {
                for (int nz = p; nz < NW - p; nz++)
                    for (int nx = p; nx < NW - p; nx++)
                    {
                        int ni = nx + NW * nz;
                        float hc = H[ni];
                        if (hc <= 0f) { Sc[ni] = 0f; continue; }
                        float sum = hc; int cnt = 1;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0) continue;
                                float nv = H[ni + dx + NW * dz];
                                if (nv > 0f) { sum += nv; cnt++; }
                            }
                        Sc[ni] = sum / cnt;
                    }
                for (int nz = p; nz < NW - p; nz++)
                    for (int nx = p; nx < NW - p; nx++)
                    { int ni = nx + NW * nz; if (H[ni] > 0f) H[ni] = Sc[ni]; }
            }
            {
                const int m = 4;
                for (int nz = m; nz < NW - m; nz++)
                    for (int nx = m; nx < NW - m; nx++)
                    {
                        int ni = nx + NW * nz;
                        if (H[ni] > 0f) { Sc[ni] = H[ni]; continue; }
                        float sum = 0f; int cnt = 0; float loN = float.MaxValue, hiN = float.MinValue;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                float nv = H[ni + dx + NW * dz];
                                if (nv > 0f) { sum += nv; cnt++; if (nv < loN) loN = nv; if (nv > hiN) hiN = nv; }
                            }
                        float ext = 0f;
                        // Interior hole-bridge ONLY = surrounded by ONE surface (levels
                        // agree) — keeps the sheet solid inside its own body. The old
                        // ground-gated bank-tuck extension is GONE (user contract: the
                        // mesh is the plan's exact shape; conformance buries the edges
                        // in terrain — the mesh never reaches for the bank itself).
                        if (cnt >= 5 && hiN - loN <= 1.0f) ext = sum / cnt;
                        Sc[ni] = ext;
                    }
                for (int nz = m; nz < NW - m; nz++)
                    for (int nx = m; nx < NW - m; nx++)
                    { int ni = nx + NW * nz; H[ni] = Sc[ni]; }
            }
                SheetRelaxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _tq;
            return NW;
        }

    }
}
