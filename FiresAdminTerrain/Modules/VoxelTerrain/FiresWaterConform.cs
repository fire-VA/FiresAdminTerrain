using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══════════════════════════════════════════════════════════════════════════
    // CONFORMANCE — the terrain fills in to work with the water (user contract).
    // Runs once at generation, immediately after the plan is decided. Consumes the
    // plan, mutates the height grid to serve it, and emits the runtime water
    // fields (level + mask) FROM the plan — the fields are a projection of the
    // plan for legacy consumers (swim, paint, veg), never a source of truth.
    //
    //   • still bodies: bowl carved below the level, one-cell rim sealed above it,
    //     w = the body's constant on every footprint cell (ONE sheet);
    //   • marshes: the VANILLA shaping (verified from assembly_valheim
    //     WorldGenerator.GetMarshHeight): floor 2.6 m under the sheet, sparse
    //     coherent mounds from a PRODUCT of two noise octaves poking through,
    //     fine ripple on top — the flat sheet floods the dips;
    //   • sea marshes: same shaping at sea level; the OCEAN plane owns the water
    //     (no inland sheet is emitted there at all);
    //   • connections: the channel is carved under the plan's profile, banks are
    //     held above it, and the ribbon's water is written at exactly the profile.
    //     A connection leaving a body carves its own outlet notch through the rim
    //     because it runs after the seal and digs by min().
    // ═══════════════════════════════════════════════════════════════════════════
    internal static class FiresWaterConform
    {
        private const float SEA = 30f;
        // SHALLOW-RIBBON river profile (user 2026-08-07): a river is a flat ribbon sitting a little below its
        // banks in a shallow groove — NOT a gorge. The ribbon rides the LOCAL surface (never deeper than one bank
        // relief below the ground the corridor crosses), so where the drainage routes a river over a small ridge
        // the fill smoothed away, it tracks the terrain up-and-over as a shallow run instead of digging a 50 m slot.
        private const float RIBBON_BANK_RELIEF = 0.84f;   // water sits this far below the local bank
        private const float RIBBON_MAX_DEPTH   = 2.0f;    // channel water is at most this deep (bed below the ribbon)
        private const float MAX_LOCAL_INCISE   = 4.0f;    // a channel bed digs at most this far below the LOCAL natural ground → no gorge, ever

        internal static void Apply(FiresWaterPlan plan, float[] g, float[] w, byte[] mask, int n, float half, int seed, bool carveConnections = true)
        {
            float cell = (2f * half) / (n - 1);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // start DRY everywhere: water exists only where the plan puts it
            System.Array.Copy(g, w, g.Length);
            System.Array.Clear(mask, 0, mask.Length);

            // ── still bodies (already ordered highest → lowest by the planner) ──
            foreach (var b in plan.Bodies)
            {
                // The OCEAN is a body so rivers can terminate on it, but it has no footprint to conform:
                // the sea shapes nothing, the terrain simply lies under it. (Harmless today — W=H=0 makes
                // every loop below a no-op — but stated explicitly so it stays that way.)
                if (b.Kind == WaterBodyKind.Ocean) continue;
                float L = b.Level;
                // distance-to-edge inside the footprint (cells) — depth + blend driver
                // A LAKE'S DEPTH IS A FUNCTION OF ITS SIZE, NOT A CONSTANT.
                // The dig is a floor, not a ceiling: `g[i] > bed` only ever
                // LOWERS ground, so a body sitting in a genuinely deep natural
                // basin keeps that basin. The cap is what the body gets when the
                // ground is flat — and a flat 3.5m for everything is what read as
                // "not dug out": a 400m lake and a 20m puddle came out the same
                // depth. Scale it by the body's own extent (√area is its radius
                // in metres, near enough) so ponds stay ankle-deep and real lakes
                // are swimmable, bounded at 18m so nothing becomes a pit.
                int bodyCells = 0;
                for (int mi = 0; mi < b.Mask.Length; mi++) if (b.Mask[mi] != 0) bodyCells++;
                float bodyR = Mathf.Sqrt(bodyCells * cell * cell);
                float maxDepth = Mathf.Clamp(1.6f + 0.055f * bodyR, 2.0f, 18f);
                var din = BuildInteriorDistance(b);
                for (int lz = 0; lz < b.H; lz++)
                {
                    int gz = b.Gz0 + lz;
                    for (int lx = 0; lx < b.W; lx++)
                    {
                        if (b.Mask[lz * b.W + lx] == 0) continue;
                        int gx = b.Gx0 + lx;
                        int i = gz * n + gx;
                        float d = din[lz * b.W + lx] * cell;               // metres from the shore
                        float wx = gx * cell - half, wz = gz * cell - half;

                        if (b.Kind == WaterBodyKind.Marsh || b.Kind == WaterBodyKind.SeaMarsh)
                        {
                            // SMOOTH BULBOUS MOUNDS (user reference: soft rounded pillows, not
                            // noise). A PRODUCT of two VERY broad octaves (83m·36m wavelengths)
                            // gives sparse blobs; a SMOOTHSTEP domes their tops so they read as
                            // rounded pillows, not peaks; a faint roll breaks up the flats. The
                            // interior is pure smooth target (the baked ±1.75m detail is fully
                            // overwritten) blending out over a 6m rim into the surrounding land.
                            float blob = FiresWorldGenBase.Fbm(wx, wz, 0.012f, 1, seed + 901)
                                       * FiresWorldGenBase.Fbm(wx, wz, 0.028f, 1, seed + 902);
                            float mound = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(blob * 2.4f)) * 7.5f;
                            float roll = (FiresWorldGenBase.Fbm(wx, wz, 0.05f, 1, seed + 903) - 0.5f) * 0.8f;
                            float target = (L - 2.4f) + mound + roll;
                            float edgeT = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / 6f)); // blend in from natural ground
                            g[i] = Mathf.Lerp(g[i], target, edgeT);
                            if (b.Kind == WaterBodyKind.Marsh) w[i] = L;   // ONE sheet; sea marsh: the ocean owns it
                        }
                        else
                        {
                            // lake / pond / tarn: a bowl under the level — dig-only
                            float depth = Mathf.Min(maxDepth, 0.8f + d * 0.12f);
                            float bed = L - depth;
                            if (g[i] > bed) g[i] = bed;
                            w[i] = L;                                       // THE constant, everywhere in the body
                        }
                    }
                }
            }

            // ── connections: carve the channel under the profile, hold the banks ──
            var g0Bank = (float[])g.Clone();   // natural ground (post-lakes, pre-rivers) — the local-incision clamp keys off THIS, never the mutating g, so overlapping segments can't cascade a groove into a gorge
            float DMIN = 1.5f, DMAX = 6.0f;
            float WMIN = 2f, WMAX = 20f;
            // POINT-TO-SEGMENT rasterization — the EXACT math TryGetRiverAt uses
            // (same t-clamp, same width/level interpolation, same 1m half-width
            // floor), so the field's corridor and the rendered mesh are ONE shape.
            // The old stepped-center version wrote a slightly WIDER corridor at
            // bends and on narrow streams: dug, field-wet, mesh-dry bed strips the
            // burial pass then skipped as "water" — floating mesh edges.
            if (carveConnections)   // OFF in native-3D: FiresWorldGenBase.CarveRiversFlat owns rivers now (flat spine ribbon, no gorge/burial)
            foreach (var c in plan.Connections)
            {
                var nd = c.Nodes;
                for (int s = 0; s + 4 < nd.Length; s += 4)
                {
                    float ax = nd[s], az = nd[s + 1], aw = nd[s + 2], al = nd[s + 3];
                    float bx = nd[s + 4], bz = nd[s + 5], bw = nd[s + 6], bl = nd[s + 7];
                    float exs = bx - ax, ezs = bz - az;
                    float len2 = exs * exs + ezs * ezs;
                    // The MESH renders the smooth profile at true node positions; a cell's
                    // banks/bed are built here from its CENTER sample. Over the ±1m node
                    // reach the profile moves by up to slope×1m — banks clear the local
                    // MAX, the bed digs under the local MIN, so the continuous sheet always
                    // sits inside its terrain regardless of where it crosses the cell.
                    float slope1 = len2 > 1e-6f ? Mathf.Abs(bl - al) / Mathf.Sqrt(len2) : 0f;
                    float reach = Mathf.Max(1f, Mathf.Max(aw, bw) * 0.5f) + 3f;
                    int gx0 = Mathf.Max(1, Mathf.FloorToInt((Mathf.Min(ax, bx) - reach + half) / cell));
                    int gx1 = Mathf.Min(n - 2, Mathf.CeilToInt((Mathf.Max(ax, bx) + reach + half) / cell));
                    int gz0 = Mathf.Max(1, Mathf.FloorToInt((Mathf.Min(az, bz) - reach + half) / cell));
                    int gz1 = Mathf.Min(n - 2, Mathf.CeilToInt((Mathf.Max(az, bz) + reach + half) / cell));
                    for (int gz = gz0; gz <= gz1; gz++)
                    {
                        float pz = gz * cell - half;
                        for (int gx = gx0; gx <= gx1; gx++)
                        {
                            float px = gx * cell - half;
                            float t = len2 > 1e-6f ? ((px - ax) * exs + (pz - az) * ezs) / len2 : 0f;
                            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                            float ddx = px - (ax + exs * t), ddz = pz - (az + ezs * t);
                            float dist = Mathf.Sqrt(ddx * ddx + ddz * ddz);
                            float width = aw + (bw - aw) * t, lvl = al + (bl - al) * t;
                            float halfW = Mathf.Max(1f, width * 0.5f);
                            if (dist > halfW + 3f) continue;
                            int i = gz * n + gx;
                            if (dist <= halfW)
                            {
                                // channel: flat-across water at the profile; parabolic bed
                                // dug under the profile's local MINIMUM over this cell
                                float tt = dist / halfW;
                                float depth = Mathf.Min(RIBBON_MAX_DEPTH, DMIN + (DMAX - DMIN) * Mathf.InverseLerp(WMIN, WMAX, width));
                                float bed = lvl - depth * (1f - 0.7f * tt * tt);
                                // NEVER a gorge: the bed digs at most MAX_LOCAL_INCISE below the LOCAL natural ground
                                // (g0Bank), so a wide corridor crossing high ground stays a shallow groove — its far
                                // cells simply fall dry — instead of a slot cut down to the low spine level. The water
                                // stays the FLAT planner level `lvl` (a flat ribbon → no false waterfalls from noise).
                                bed = Mathf.Max(bed, g0Bank[i] - MAX_LOCAL_INCISE);
                                if (g[i] > bed) g[i] = bed;
                                if (w[i] <= g[i] + 0.01f || w[i] > lvl) w[i] = lvl;   // ribbon level; a body's sheet (≤ lvl) is never overwritten
                                byte m = tt <= 0.35f ? (byte)3 : (byte)1;
                                if (mask[i] < m) mask[i] = m;
                            }
                            else
                            {
                                // bank: a FLAT full-height crest for the first 1.5m, built
                                // against the profile's local MAXIMUM — every node the
                                // smooth ribbon edge can reach stands over buried ground —
                                // then feathered back to natural. UNCONDITIONAL raise: the
                                // terrain fills in to work with the water, PERIOD; the only
                                // skip is a cell already carrying planned water.
                                float over = dist - halfW;
                                float crest = lvl + slope1 + RIM;
                                float bank = over <= 1.5f ? crest
                                                          : lvl + (slope1 + RIM) * (1f - (over - 1.5f) / 1.5f);
                                if (g[i] < bank && w[i] <= g[i] + 0.01f) g[i] = bank;
                                if (mask[i] == 0) mask[i] = 2;
                            }
                        }
                    }
                }
            }

            BuryMeshEdges(g, w, n, false, mask);

            FiresLogger.LogInfo($"[FiresWaterPlan] conformance: terrain shaped to {plan.Bodies.Count} bodies + {plan.Connections.Count} connections in {sw.Elapsed.TotalSeconds:0.0}s.");
        }

        // ═══ BURY ALL MESH EDGES (user contract 2026-08-04) ═══════════════════
        // The water mesh is computed ONCE from the plan and NEVER adjusted — the
        // TERRAIN is what moves. At every coverage boundary (wet cell with a dry
        // 8-neighbour) the terrain must stand ABOVE the sheet so the mesh edge is
        // buried inside a bank, however high that needs to be:
        //   • shore shelf: the boundary wet cell's bed rises to level-0.25 (still
        //     underwater) so the ground under the edge quad can't sag away;
        //   • burial rim: every dry cell touching water rises to level+0.55;
        //   • feather: raised rims ramp back to natural outward (raise-only,
        //     ~0.7m/cell) — a bank, not a wall, unless it must be a wall.
        // ⚠⚠ THESE PASSES ARE **NOT** PURE GATHERS, AND THIS COMMENT USED TO CLAIM THEY WERE.
        // That claim is what made the bug invisible. Each cell READS its 8 neighbours' `g[j]`/`stamp[j]` and
        // WRITES its own `g[i]`/`stamp[i]`. Under `Parallel.For(1, n-1, z => …)` the split is by ROW, so the
        // neighbours at z-1 and z+1 belong to other threads and are being written while they are read.
        // Whether a neighbour has already been updated this iteration depends on thread scheduling, so the
        // OUTPUT IS NON-DETERMINISTIC: two identical watermesh runs on one seed produced different worlds
        // (55,575 vs 51,823 feathered cells). This mutates the ground grid, so host and client could build
        // DIFFERENT TERRAIN from the same seed — precisely what GEN_VERSION exists to prevent, and not
        // something a version bump can catch because both sides are running the same code.
        //
        // FIX: parallel COMPUTE, serial APPLY. The parallel phase writes nothing, so every read sees the same
        // pre-iteration state on every run; the changed cells are buffered per row and applied afterwards.
        // Each cell only ever writes its own index, so apply order cannot matter. Cheap, because only the
        // cells that actually change are buffered (tens of thousands, not n²) — double-buffering the whole
        // grid would have cost ~550 MB per iteration on a carve that has hit OOM before.
        //
        // Raise-only, and TERRAIN only — EXCEPT pass 0 below, which fills cells ENCLOSED by water (see its own
        // note: the old blanket promise is precisely what kept the island defect unfixable at its cause).
        private const float SHELF = 0.25f;    // edge bed hugs the sheet this close
        // Rim height is BOUND to the shelf: at a concave outline corner a dry node's
        // 2x2 bilinear reads 3 shelf cells + 1 rim cell — (3·(L−SHELF)+(L+RIM))/4
        // must clear L, so RIM ≥ 3·SHELF + margin. 1.0m: worst corner = L+0.06,
        // straight edge = L+0.38. The berm reads as a real bank; feather softens it.
        private const float RIM = 1.0f;       // dry rim stands this far above the sheet
        // ═══ RIVERS GET THE SAME BANK RULE AS PONDS AND LAKES (2026-08-18, GEN_VERSION 146) ═══════════════
        // (user, with a screenshot of river water standing as raised slabs above sand benches at
        // (5935,−1473) seed 1095533379: "WHAT IS SO HARD ABOUT GIVING RIVERS THE SAME BANK LOGIC AS PONDS
        // AND LAKES SO THEY EXTEND THEIR MESH OUT TO THE TERRAIN INSTEAD OF SITTING IN MOUNDED PUDDLES".)
        // He was right, and the reason is an ORDERING, not a rule: BuryMeshEdges runs inside Apply(), which
        // both gen paths call BEFORE InciseRivers — so when the pond/lake bank rule ran, no river water was
        // in `w` yet, and the rivers incised afterwards never received it. They had only InciseRivers' own
        // thinner berm (wl+0.6, skipped over any seeded footprint — i.e. exactly on flat valley floors), and
        // the universal no-float settle (ContainWaterField) is skipped for planned worlds on the strength of
        // "the plan's conformance GUARANTEES containment" — a guarantee that never covered rivers. Three nets,
        // rivers fell through all of them.
        // This entry runs the identical shelf / rim(+1.0) / feather pass over the FULL field AFTER the rivers
        // exist. Raise-only and idempotent: bodies buried by the first call are already at level+RIM and are
        // untouched; only cells the first call could not see move. `seaGuard` is the one difference, and it
        // is InciseRivers' own berm guard: a mouth reach (w ≤ SEA+0.3) is not bermed and seabed (g ≤ SEA) is
        // never raised — without it this pass would rebuild the 2026-08-15 causeway-into-the-sea (134,301
        // ocean cells turned to land by a bank raise). Sky-island water is not in this grid.
        public static void BuryAllEdges(float[] g, float[] w, int n, byte[] mask = null) => BuryMeshEdges(g, w, n, seaGuard: true, mask: mask);

        private static void BuryMeshEdges(float[] g, float[] w, int n, bool seaGuard = false, byte[] mask = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long shelf = 0, rim = 0, feather = 0, holes = 0;
            var stamp = new byte[n * n];      // 1 = burial rim cell (feather source)
            // ═══ PASS 0 — AN ENCLOSED DRY CELL IS A HOLE IN THE SHEET, NOT A SHORE ════════════════════════
            // (2026-08-19, measured with the `riverfield` probe on seed 126369233 at n=10501.) Pass 1 below
            // raises EVERY dry cell touching water to `demand + RIM`, and RIM is 1.0 m. It has no idea whether
            // that cell is on the OUTSIDE of the sheet — a real bank, which is the case it was written for —
            // or INSIDE it, a cell some footprint failed to claim. For the inside case the rule is actively
            // destructive: it lifts the gap A METRE ABOVE THE SURROUNDING WATER and the feather then ramps
            // outward from it, so a missed cell becomes a visible island in the middle of a pool.
            // THE FINGERPRINT IS THIS CONSTANT. 7,098 dry cells world-wide had ≥6 of 8 neighbours wet, forming
            // 4,646 islands (p90 2, p99 8, max 45 cells) — and 6,422 of them, 90.5%, stood at EXACTLY
            // water+1.00 ±0.15 m. That is RIM, not terrain.
            // A cell walled in by water on 6 of 8 sides has no shore to be. If its ground also lies below the
            // water pressing in on it, the water belongs there: fill it, and pass 1 then sees water and leaves
            // it alone. Bounded by construction — it requires enclosure, so it can only ever close a hole, and
            // `g < demand` still leaves a genuine islet standing proud.
            // ⚠ THIS PASS WRITES `w`, AND THE HEADER ABOVE USED TO PROMISE IT NEVER WOULD. That promise was
            // the reason the defect could not be fixed where it is caused: the pass is allowed to move terrain
            // by a metre to hide a mesh edge, but not to admit that the water was simply missing. It writes w
            // ONLY here, ONLY into cells enclosed by water, and only upward from dry to the enclosing level.
            // ⚠ THE SAME GUARD EXISTS IN FiresWorldGenBase.InciseRivers' ContainRiversBelowBanks, the other
            // pass that raises dry cells touching water. Both need it; change one, change the other.
            if (FiresWorldGenBase.s_riverFootprintFlood)   // A/B switch, FIRES_RIVER_FOOTPRINT=0 — see its declaration
            {
                const int FILL_ROUNDS = 5;   // each round closes one ring, so islands up to ~2·ROUNDS across
                var rowFill = new List<(int idx, float val)>[n];
                for (int it = 0; it < FILL_ROUNDS; it++)
                {
                    System.Array.Clear(rowFill, 0, n);
                    System.Threading.Tasks.Parallel.For(1, n - 1, z =>
                    {
                        List<(int, float)> f = null;
                        for (int x = 1; x < n - 1; x++)
                        {
                            int i = z * n + x;
                            if (w[i] > g[i] + 0.01f) continue;                    // already water
                            if (seaGuard && g[i] <= SEA) continue;                // the sea owns its columns
                            int wetN = 0; float demand = 0f, loLvl = float.MaxValue;
                            for (int dz = -1; dz <= 1; dz++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    if (dx == 0 && dz == 0) continue;
                                    int j = i + dz * n + dx;
                                    if (w[j] <= g[j] + 0.01f) continue;
                                    if (seaGuard && w[j] <= SEA + 0.3f) continue; // an estuary/ocean edge is not an inland hole
                                    wetN++;
                                    if (w[j] > demand) demand = w[j];
                                    if (w[j] < loLvl) loLvl = w[j];
                                }
                            if (wetN < 6 || demand <= 0f) continue;
                            if (demand - loLvl > 4.0f) continue;                  // walled in by TWO levels = a divide between reaches, not a hole
                            if (g[i] >= demand - 0.05f) continue;                  // stands proud of the surface — a real islet
                            (f ??= new List<(int, float)>()).Add((i, demand));
                        }
                        rowFill[z] = f;
                    });
                    int changed = 0;
                    for (int z = 1; z < n - 1; z++)   // serial apply, like every other pass here
                    {
                        var f = rowFill[z]; if (f == null) continue;
                        foreach (var (i, v) in f) { w[i] = v; holes++; changed++; }
                    }
                    if (changed == 0) break;
                }
            }
            // pass 1 — gather per cell: what do my 8 neighbours demand of me?
            var rowShelf = new List<(int idx, float val)>[n];
            var rowRim = new List<(int idx, float val)>[n];
            var rowStamp = new List<int>[n];
            System.Threading.Tasks.Parallel.For(1, n - 1, z =>
            {
                List<(int, float)> sh = null, rm = null; List<int> st = null;
                for (int x = 1; x < n - 1; x++)
                {
                    int i = z * n + x;
                    bool wet = w[i] > g[i] + 0.01f;
                    if (seaGuard && wet && w[i] <= SEA + 0.3f) continue;          // mouth reach — the sea owns it, no berm (InciseRivers' rule)
                    if (seaGuard && !wet && g[i] <= SEA) continue;                // seabed — never raised into a causeway
                    float demand = 0f;                  // highest adjacent sheet
                    bool dryNbr = false, riverDemand = false, stillDemand = false;   // channel bank, body shore, or both?
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int j = i + dz * n + dx;
                            if (w[j] > g[j] + 0.01f)
                            {
                                if (!(seaGuard && w[j] <= SEA + 0.3f) && w[j] > demand)
                                { demand = w[j]; riverDemand = mask != null && (mask[j] == 1 || mask[j] == 3); }
                                // ⚠ ANY still neighbour makes this a BODY SHORE — see the matching note in
                                // ContainRiversBelowBanks. Scoping the loose trigger on `riverDemand` alone
                                // handed it to every river↔body junction (the river is usually the higher of
                                // the two, so it is the one that sets `demand`) and cost still-family lips
                                // 161 → 211 with the worst going 3.07 → 6.15 m.
                                if (mask == null || (mask[j] != 1 && mask[j] != 3)) stillDemand = true;
                            }
                            else dryNbr = true;
                        }
                    if (wet)
                    {
                        // ═══ THE SHELF IS A BEACH RULE. A RIVER HAS NO BEACH. ════════════════════════════
                        // (2026-08-19, user: "if the river's ribbon logic read the terrain height correctly,
                        // carved the river bed deep enough, and actually set its mesh down into that carve like
                        // I wanted from the start then most of these issues would have been solved already —
                        // but we had to do a bunch of workarounds because we do not carve our river correctly
                        // and then lay the mesh down into the carve correctly." He is right, and this line is
                        // the workaround. It is not that the carve fails; the carve is UNDONE afterwards.)
                        // This raises the BED of every wet cell that has a dry neighbour to `level − 0.25`.
                        // On a lake that is a shallow shore and it looks like one — the boundary is a thin
                        // fringe of a large body. On a CHANNEL A FEW CELLS WIDE almost the whole river is
                        // boundary, so it re-fills the trench that InciseRivers just dug.
                        // MEASURED with `riverfield` on the shipped seed 126369233 at n=10501, and the number
                        // is not a distribution, it is a constant:
                        //     BOUNDARY river cells  146,920   depth p10/p50/p90 = 0.25 / 0.25 / 0.25 m
                        //                                     146,913 of them (100.0 %) at 0.25 ±0.05
                        //     INTERIOR river cells  176,425   depth p10/p50/p90 = 1.10 / 2.50 / 3.41 m
                        //     boundary share of all river water: 45.4 %
                        // So 45 % of every river in the world was 25 cm deep BY CONSTRUCTION, while the carve
                        // underneath had dug 2.5 m. Add the RIM below (+1.0 m on the dry neighbour) and the
                        // cross-section reads: curb, hard drop, a 25 cm tray, then the real channel — which is
                        // exactly the "mounded puddle" silhouette this file has been chasing for weeks.
                        // ⚠ NOTE THE GUARD IS INVERTED RELATIVE TO ITS OWN COMMENT. It claimed the bed "rises
                        // so the ground under the edge quad can't sag away", but `g[i] < w[i] − SHELF` fires
                        // only when the bed is ALREADY comfortably deep. It never protected a thin lip; it
                        // capped depth. Deep ground under an edge quad was never the problem.
                        // Still bodies keep it — it is right for them and it is what their shores are built
                        // from. Channel cells (mask 1/3) are excluded and keep the cross-section the carve
                        // gave them, floored by InciseRivers' own CHANNEL_MIN_DEPTH.
                        bool channel = mask != null && (mask[i] == 1 || mask[i] == 3);
                        if (!channel && dryNbr && g[i] < w[i] - SHELF) (sh ??= new List<(int, float)>()).Add((i, w[i] - SHELF));
                    }
                    else if (demand > 0f)
                    {
                        // dry cell touching water → BURIED rim. PERIOD… for a BODY.
                        // ⚠ THE TRIGGER AND THE ACTION WERE THE SAME NUMBER, and that is what terraced every
                        // riverbank in the world onto a constant offset. See the long note at the matching
                        // raise in FiresWorldGenBase's ContainRiversBelowBanks for the measurement (3,896
                        // earned vs 224,145 gratuitous). For a CHANNEL the trigger is now simply "does this
                        // ground stand above the sheet at all" while the action keeps the full RIM, so a
                        // natural bank is left as terrain made it and a genuine low spot still gets a real
                        // bank. A STILL BODY keeps the old test unchanged: its boundary beds are still held at
                        // level−SHELF, and RIM = 1.0 is bound to that (RIM >= 3·SHELF, the concave-corner
                        // bilinear argument in the constant's own comment) — loosening the trigger there would
                        // un-bury pond corners, which is the one thing that has always worked.
                        const float BURY_MARGIN = 0.50f;   // matched to ContainRiversBelowBanks — see its note
                        bool channel = riverDemand && !stillDemand;
                        float trigger = channel ? demand + BURY_MARGIN : demand + RIM;
                        if (g[i] < trigger) (rm ??= new List<(int, float)>()).Add((i, demand + RIM));
                        (st ??= new List<int>()).Add(i);
                    }
                }
                rowShelf[z] = sh; rowRim[z] = rm; rowStamp[z] = st;
            });
            for (int z = 1; z < n - 1; z++)      // serial apply — deterministic regardless of thread timing
            {
                var sh = rowShelf[z]; if (sh != null) foreach (var (i, v) in sh) { g[i] = v; shelf++; }
                var rm = rowRim[z]; if (rm != null) foreach (var (i, v) in rm) { g[i] = v; rim++; }
                var st = rowStamp[z]; if (st != null) foreach (var i in st) stamp[i] = 1;
            }
            // pass 2 — feather: walk the rim height back to natural ground outward.
            // Each iteration lets cells one ring further out rise to (highest
            // stamped/feathered neighbour - FEATHER_STEP); cells already that high
            // are left alone, so the ramp ends the moment it meets natural terrain.
            // 0.7m over 4 rings was a 0.35 grade on 2m cells — the bank read as a
            // hard lip ringing every body, which is exactly what containment should
            // NOT look like. Half the step over twice the distance keeps the same
            // total rise and lands it as a shore you can walk down.
            const float FEATHER_STEP = 0.35f;
            const int FEATHER_RINGS = 8;
            for (int it = 0; it < FEATHER_RINGS; it++)
            {
                var rowFeather = new List<(int idx, float val)>[n];
                System.Threading.Tasks.Parallel.For(1, n - 1, z =>
                {
                    List<(int, float)> f = null;
                    for (int x = 1; x < n - 1; x++)
                    {
                        int i = z * n + x;
                        if (stamp[i] != 0) continue;
                        if (w[i] > g[i] + 0.01f) continue;              // never touch water
                        if (seaGuard && g[i] <= SEA) continue;         // the feather stops at the waterline too — no ramp out across the seabed
                        float hi = 0f;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0) continue;
                                int j = i + dz * n + dx;
                                if (stamp[j] != 0 && g[j] > hi) hi = g[j];
                            }
                        if (hi > 0f && g[i] < hi - FEATHER_STEP) (f ??= new List<(int, float)>()).Add((i, hi - FEATHER_STEP));
                    }
                    rowFeather[z] = f;
                });
                for (int z = 1; z < n - 1; z++)   // serial apply, as pass 1
                {
                    var f = rowFeather[z]; if (f == null) continue;
                    foreach (var (i, v) in f) { g[i] = v; stamp[i] = 2; feather++; }
                }
                // promote this ring to feather sources for the next iteration
                System.Threading.Tasks.Parallel.For(0, n, z =>
                { for (int x = 0; x < n; x++) { int i = z * n + x; if (stamp[i] == 2) stamp[i] = 1; } });
            }
            FiresLogger.LogInfo($"[FiresWaterPlan] mesh edges buried{(seaGuard ? " (ALL water, post-InciseRivers, sea-guarded)" : " (bodies)")}: {holes:N0} enclosed HOLES filled with water (not raised into islands — pass 0), {shelf:N0} shore shelves, {rim:N0} rim cells raised, {feather:N0} feathered in {sw.Elapsed.TotalSeconds:0.0}s.");
        }

        // Chamfer distance (in cells) from the footprint edge, inside the mask.
        private static float[] BuildInteriorDistance(WaterBody b)
        {
            int W = b.W, H = b.H;
            var d = new float[W * H];
            const float INF = 1e9f;
            for (int i = 0; i < W * H; i++) d[i] = b.Mask[i] != 0 ? INF : 0f;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (d[i] <= 0f) continue;
                    float v = d[i];
                    if (x > 0) v = Mathf.Min(v, d[i - 1] + 1f);
                    if (z > 0)
                    {
                        v = Mathf.Min(v, d[i - W] + 1f);
                        if (x > 0) v = Mathf.Min(v, d[i - W - 1] + 1.414f);
                        if (x + 1 < W) v = Mathf.Min(v, d[i - W + 1] + 1.414f);
                    }
                    if (x == 0 || z == 0 || x == W - 1 || z == H - 1) v = Mathf.Min(v, 1f);
                    d[i] = v;
                }
            for (int z = H - 1; z >= 0; z--)
                for (int x = W - 1; x >= 0; x--)
                {
                    int i = z * W + x;
                    if (d[i] <= 0f) continue;
                    float v = d[i];
                    if (x + 1 < W) v = Mathf.Min(v, d[i + 1] + 1f);
                    if (z + 1 < H)
                    {
                        v = Mathf.Min(v, d[i + W] + 1f);
                        if (x + 1 < W) v = Mathf.Min(v, d[i + W + 1] + 1.414f);
                        if (x > 0) v = Mathf.Min(v, d[i + W - 1] + 1.414f);
                    }
                    d[i] = v;
                }
            return d;
        }

    }
}
