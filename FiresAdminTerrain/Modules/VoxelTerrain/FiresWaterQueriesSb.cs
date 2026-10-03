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
    // ═══ RUNTIME WATER QUERIES — what the game asks the field at a point ═════════════════════════════
    // Surface height/normal/descent, class and label lookups, the raw cell reads, and the baked obstacle
    // disturbance field the rapids shading samples.
    // ⚠ These are READS. Nothing here may modify the field: several of them are called from the mesher and
    // from gameplay on arbitrary threads.
    public static partial class FiresWorldGenBase
    {
        // The label at a world position (nearest 2m cell). WC_NONE = dry land,
        // WC_OCEAN = the tidal plane's water (no inland sheet here).
        public static byte WaterLabelAt(float wx, float wz)
        {
            var l = _labelS;
            if (l == null) return WC_NONE;
            int n = _labelN; float half = _half; float cell = (2f * half) / (n - 1);
            int gx = Mathf.Clamp(Mathf.RoundToInt((wx + half) / cell), 0, n - 1);
            int gz = Mathf.Clamp(Mathf.RoundToInt((wz + half) / cell), 0, n - 1);
            return l[gz * n + gx];
        }

        // SMOOTH reconciled surface + depth over the FULL-res ground — THE
        // number the bed carve is cut for and the water sheet is emitted at.
        // NEAREST-CELL raw water — the SOLID sheet the mesh renders (no bilinear extension onto dry shore
        // cells, which the smooth query does and the mesh's lip-guard then culls). A cell is wet ONLY if
        // its OWN 2m column holds water. This is the field ContainWaterField governs, so a float audit on
        // it measures water that actually renders, not the shore feather.
        public static bool TryGetRawWaterCell(float wx, float wz, out float level, out float depth)
        {
            level = 0f; depth = 0f;
            var ws = _waterS; var gs = _gridS;
            if (ws == null || gs == null) return false;
            int n = _res; float half = _half; float cell = (2f * half) / (n - 1);
            int gx = Mathf.Clamp(Mathf.RoundToInt((wx + half) / cell), 0, n - 1);
            int gz = Mathf.Clamp(Mathf.RoundToInt((wz + half) / cell), 0, n - 1);
            int i = gz * n + gx;
            float w = ws[i] * UNPACK, g = gs[i] * UNPACK;
            if (w <= g + 0.01f) return false;
            level = w; depth = w - g; return true;
        }

        // RIVER BED-CARVE at (wx,wz): how far the deliberate water plan incised the ground BELOW the analytic
        // FiresTerrain3D surface — 0 everywhere except along river/water beds. The voxel gen subtracts this so a
        // real channel exists in the terrain the plan's water sits in (containment: banks stand above the level).
        // ═══ SIGNED, AND THAT IS THE WHOLE POINT (2026-08-15) — renamed from RiverCarveAt ═══════════════
        // (analytic surface) − _gridS. POSITIVE where the gen cut the ground down (river beds, lake bowls),
        // NEGATIVE where it built the ground up (the ±1.75 m surface detail's up-half, river BANKS, berms,
        // depression fill). Subtracting it from SurfaceElev reproduces _gridS EXACTLY, which is the point:
        // the voxel mesh and the gen grid become ONE surface.
        //
        // ⚠ IT USED TO BE CLAMPED — `return carve > 0f ? carve : 0f` — on the reasoning that "water only
        // lowers ground". Water does, but GEN does not: it also raises. With the clamp the voxel mesh was
        // min(SurfaceElev, _gridS) while every water level was solved against _gridS, so on every raised
        // cell the water sat above the terrain. Measured (seed 12345, n=4097, studio matching the game):
        // 7,566,548 raised cells, 1,752,452 of them on channel/wet ground, median 0.17 m, p90 0.44 m,
        // p99 1.44 m, max 37.11 m — THE floating-river mechanism. It is also exactly why bodies always
        // worked and rivers never did: a lake is contained by DIGGING a bowl (positive, survived the clamp),
        // a river by RAISED banks (negative, discarded). Renamed because a member called "…CarveAt" invites
        // the clamp back; this is a surface DELTA, not a carve depth.
        //
        // Consumers are the two that want the real surface: the density fill (VoxelChunk → DensityFillJob →
        // FiresTerrain3D.Density's `carve` input, where a negative value correctly RAISES the surface and the
        // cave guarantees follow it) and FiresFarTerrain's backdrop. Nothing treats it as non-negative.
        //
        // ⚠ 2026-08-09 REGRESSION FIX: the analytic macro surface is computed HERE, internally, NOT taken from the
        // caller. The old signature took `surfaceHere` "which VoxelChunk already has as _genGround[col], no recompute"
        // — but the perf §1 "bake-first" change re-sourced _genGround from the INCISED _gridS bake (StreamGroundBiomeJob
        // useBake=1), so the caller was passing the incised grid → carve = _gridS − _gridS ≈ 0 → rivers stopped cutting
        // into the voxel terrain while the gen labels still showed them (map says river, ground is flat meadow). The
        // carve fundamentally needs the UN-incised surface, so it recomputes SurfaceElev (gen-time, one call per column
        // on the worker — the density fill already samples it 34× more often; no steady-state cost). No caller can
        // re-introduce the trap. `_seed` is the world seed (set on gen AND cache-restore).
        public static float SurfaceDeltaAt(float wx, float wz)
        {
            var g = _gridS;
            if (g == null) return 0f;
            int n = _res; float half = _half;
            float cell = (2f * half) / (n - 1);
            float gx = (wx + half) / cell, gz = (wz + half) / cell;
            if (gx < 0f) gx = 0f; else if (gx > n - 1.001f) gx = n - 1.001f;
            if (gz < 0f) gz = 0f; else if (gz > n - 1.001f) gz = n - 1.001f;
            int xi = (int)gx, zi = (int)gz;
            float fx = gx - xi, fz = gz - zi;
            int i = zi * n + xi;
            float g00 = g[i] * UNPACK, g10 = g[i + 1] * UNPACK, g01 = g[i + n] * UNPACK, g11 = g[i + n + 1] * UNPACK;
            float ground = g00 * (1f - fx) * (1f - fz) + g10 * fx * (1f - fz) + g01 * (1f - fx) * fz + g11 * fx * fz;
            // SIGNED: + where gen cut down, − where gen built up. NO CLAMP — see the header. Subtracting this
            // from SurfaceElev reproduces `ground` (_gridS) exactly, so the voxel mesh IS the gen grid.
            return SampleFinalMetersReal(wx, wz) - ground;
        }

        // Zone mode's door, as in FiresWorldGenBase.SampleBiome: closed zones hold no generated water.
        public static bool TryGetWaterSurfaceSmooth(float wx, float wz, out float level, out float depth)
        {
            float tx = wx, tz = wz;
            if (!Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz))
                return Zones.ZoneModeGate.LiftWater(tx, tz, TryGetWaterSurfaceSmoothReal(wx, wz, out level, out depth), ref level, ref depth);
            level = 0f;
            depth = 0f;
            return false;
        }

        // ── PLAYER WATER EDITS OVERLAY ───────────────────────────────────────
        // THE public water-surface query every RENDER / PHYSICS / VEG system reads. It applies the player's
        // water edits (FiresWaterEdits — the "draw water" tools) ON TOP of the pure generated surface, so
        // drawn / smoothed / erased water is the ONE surface the shared sheet builder (BuildSheetNodes),
        // swim/buoyancy and scatter all see — painted water and gen rivers/lakes MERGE into a single sheet,
        // never a double layer. Byte-identical to the gen field when the player has made no water edits
        // (Active == false → the immediate passthrough). NOTE the gen-water BAKE inside VoxelChunk reads the
        // PURE variant (TryGetWaterSurfaceGen) so a transient edit never contaminates the cached _genWater.
        internal static bool TryGetWaterSurfaceSmoothReal(float wx, float wz, out float level, out float depth)
        {
            s_wsCalls++;                                                  // see WaterQueryCallReport
            bool wet = TryGetWaterSurfaceGen(wx, wz, out level, out depth);
            if (!FiresWaterEdits.Active || !FiresWaterEdits.TryGet(wx, wz, out float pl, out bool removed)) return wet;
            if (removed) { level = 0f; depth = 0f; return false; }              // erased → dry (drops gen water here too)
            float ground = wet ? level - depth : (TryGetGenGroundReal(wx, wz, out float gg) ? gg : pl - 0.1f);
            level = pl;                                                          // fills were already max-merged at write time
            depth = Mathf.Max(0.05f, pl - ground);
            return pl > ground + 0.01f;                                         // painted below the local ground → terrain clips it (dry)
        }

        // === HOW MANY TIMES A SECOND IS THIS CALLED? ====================================================
        // ([clutter] asked, 2026-09-07: they have us at 0.0556 us/call and 0.000 bytes/call from qbench, so
        // calls/s was the only missing factor in "is TryGetWaterSurfaceSmooth worth chasing".)
        // WARNING - SCOPE: this counts EVERY caller: the 8 render-side sites across 5 systems, the ~24
        // internal uses in this file, and the apply path. The ms/s it yields is an UPPER BOUND on any one
        // caller's share, never that caller's own cost. It answers "can this family matter at all", which is
        // the question that decides whether anyone should instrument their own site.
        // WARNING - unsynchronised on purpose: this is read for magnitude, and an Interlocked on a function
        // this hot would make the instrument part of the cost it reports. Concurrent callers LOSE counts, so
        // the number is a FLOOR. A stopwatch, not a frame count, so it is valid off the main thread.
        private static long s_wsCalls;
        private static readonly System.Diagnostics.Stopwatch s_wsClock = System.Diagnostics.Stopwatch.StartNew();
        private const double WS_US_PER_CALL = 0.0556;   // qbench, 171,931 real hits on a restored field

        public static string WaterQueryCallReport()
        {
            long n = s_wsCalls;
            double sec = s_wsClock.Elapsed.TotalSeconds;
            if (n == 0)
                return "[FiresWaterQueryRate] ZERO CALLS RECORDED - either nothing queried the water surface "
                     + "this session, or this build does not carry the counter. Those are different and this "
                     + "line cannot tell them apart: probe the DLL for s_wsCalls before reading a zero as an "
                     + "answer.";
            if (sec < 1.0)
                return "[FiresWaterQueryRate] " + n.ToString("N0") + " calls in " + sec.ToString("F2")
                     + " s - TOO SHORT TO RATE. Ignore.";
            double per = n / sec;
            return "[FiresWaterQueryRate] " + n.ToString("N0") + " calls to TryGetWaterSurfaceSmooth over "
                 + sec.ToString("F0") + " s = " + per.ToString("N0") + " /s (ALL callers - render, veg, apply, "
                 + "and this file's own uses). At " + WS_US_PER_CALL.ToString("F4") + " us/call that is "
                 + (per * WS_US_PER_CALL / 1000.0).ToString("F1") + " ms/s TOTAL, which is the CEILING for a "
                 + "single caller's share, not its cost. Unsynchronised, so the count is a floor.";
        }

        // The PURE generated water surface (NO player edits) — the original reconciled-field query. The
        // gen-water bake (VoxelChunk._genWater) calls THIS so the per-chunk cache stays edit-free; everything
        // that should reflect drawn water calls TryGetWaterSurfaceSmooth above (same name, all existing callers).
        public static bool TryGetWaterSurfaceGen(float wx, float wz, out float level, out float depth)
        {
            level = 0f; depth = 0f;
            var s = _surfS; var g = _gridS;
            if (s == null || g == null) return false;
            float half = _half;

            // FULL-res ground corners first — the wet tests and depth share them.
            int n = _res;
            float cell = (2f * half) / (n - 1);
            float gx = (wx + half) / cell, gz = (wz + half) / cell;
            if (gx < 0f) gx = 0f; else if (gx > n - 1.001f) gx = n - 1.001f;
            if (gz < 0f) gz = 0f; else if (gz > n - 1.001f) gz = n - 1.001f;
            int xi = (int)gx, zi = (int)gz;
            float fx = gx - xi, fz = gz - zi;
            int i = zi * n + xi;
            float g00 = g[i] * UNPACK, g10 = g[i + 1] * UNPACK;
            float g01 = g[i + n] * UNPACK, g11 = g[i + n + 1] * UNPACK;
            float ground = g00 * (1f - fx) * (1f - fz) + g10 * fx * (1f - fz)
                         + g01 * (1f - fx) * fz + g11 * fx * fz;

            // WET LEVEL AT FULL 2m RES. The world moved to a 2m grid exactly
            // so water could be RIGHT — and the 8m reconciled surface then
            // block-MAXED the wet level anyway (user-diagnosed as the wrong
            // play). On a steep stream one 8m block is a flat plate 2m+ above
            // the next: the giant staircase. On a bowl rim the block holding
            // both the flat cap and an inflow's higher cells took the inflow's
            // level: the sheet riding up the bank. Wet cells now sample the
            // REAL field; dry corners extend flat from the wettest corner so
            // the sheet still tucks into banks. The 8m envelope below serves
            // ONLY dry shore margins (paint rise, skirt feather, CUT holes).
            var wf = _waterS;
            if (wf != null)
            {
                float w00 = wf[i] * UNPACK, w10 = wf[i + 1] * UNPACK;
                float w01 = wf[i + n] * UNPACK, w11 = wf[i + n + 1] * UNPACK;
                bool b00 = w00 > g00 + 0.01f, b10 = w10 > g10 + 0.01f;
                bool b01 = w01 > g01 + 0.01f, b11 = w11 > g11 + 0.01f;
                if (b00 || b10 || b01 || b11)
                {
                    float wm = float.MinValue;
                    if (b00 && w00 > wm) wm = w00;
                    if (b10 && w10 > wm) wm = w10;
                    if (b01 && w01 > wm) wm = w01;
                    if (b11 && w11 > wm) wm = w11;
                    float v00 = b00 ? w00 : wm, v10 = b10 ? w10 : wm;
                    float v01 = b01 ? w01 : wm, v11 = b11 ? w11 : wm;
                    level = v00 * (1f - fx) * (1f - fz) + v10 * fx * (1f - fz)
                          + v01 * (1f - fx) * fz + v11 * fx * fz;
                    // LIVE per-family relief (v106: river vs still sliders; sea untouched).
                    // (2026-08-12) RELIEF TAPERS OUT AT THE TIDAL MARGIN. Bank relief is freeboard under the
                    // BANKS — but a river at its mouth has no banks left, it is level with the sea. Subtracting
                    // the full 0.84 near the shoreline pushed the surface BELOW sea, where the sheet builder
                    // (rightly) refuses to draw inland water — chopping the last reach into 0.2-0.4m slivers at
                    // surf≈30.0 with a floating cut edge (user screenshots at X≈-1800..-1830). Clamping the
                    // dropped level so water that was above the sea STAYS above it keeps the ribbon continuous
                    // all the way to the shoreline, where the terrain falls below SEA and the ocean takes over.
                    // ⭐ RELIEF MAY NEVER EXCEED THE WATER'S OWN DEPTH. (user, 2026-09-02, `waterat` on a lake
                    // shore: "class LAKE genGround 54.0 surfaceSmooth 53.2 (depth 0.1)" — the lake drawn
                    // 0.8 m UNDER ITS OWN BED.) Relief means "the surface sits this far below its banks", and
                    // as an unconditional subtraction it contradicts the beds the generator actually cut:
                    // a still body's edge cells are held at level − SHELF (0.25 m) by the containment pass
                    // while StillRelief takes 0.84 m off them, so THE SHORELINE RING OF EVERY LAKE AND POND
                    // RENDERS DRY — which is the sand-coloured "bank" around still water in his screenshots.
                    // The rivers had the identical contradiction against their 0.35 m wetted core, fixed on
                    // the terrain side by cutting the core to 1.20 m; this is the same law where the two
                    // numbers actually meet, and it covers every family at once.
                    // Full relief still applies wherever there IS depth for it — this only refuses to draw
                    // water below the ground it lies on, which was never a look, only an error.
                    // ⭐ THE SOLVED LEVEL *IS* THE SURFACE — RELIEF IS NOT SUBTRACTED AGAIN HERE.
                    // (user, 2026-09-02: "at the default .84 rivers sit WAY TOO LOW in their carves", and a
                    // berm still drawn between river and still water with the slider raised to −0.5.)
                    // Bank relief is applied ONCE, at GENERATION: RiverLevels solves every ribbon against
                    // `min(bankL,bankR) − RELIEF` (0.84 m), so the stored level ALREADY sits that far under
                    // its banks. Subtracting `ReliefFor` here charged it a SECOND time and put the water
                    // 1.68 m below its own banks — the sunken-in-a-trench look, and the lip left standing
                    // between a river and the still water it runs into.
                    // ⚠ IT ALSO EXPLAINS WHY MOVING THE SLIDER NEVER REMOVED THE LIP. The slider only ever
                    // moved the SECOND subtraction; the generation-time one is a const the renderer cannot
                    // see, so no setting could reach it. Two places owning one quantity, RULES.md §1.
                    // The slider stays live as a DEVIATION FROM WHAT GENERATION ALREADY DID: at its default
                    // it is exactly zero change, and moving it raises or lowers the sheet from there.
                    if (level > SEA + 0.3f)
                    {
                        float extra = ReliefFor(wx, wz) - DefaultRiverRelief;   // 0 at the shipped default
                        if (extra > 0.0001f || extra < -0.0001f)
                        {
                            float dropped = level - extra;
                            level = dropped < SEA + 0.05f ? SEA + 0.05f : dropped;
                        }
                    }
                    // (v106c, user: "when the level drops the mesh size should NOT") COVERAGE is decided by
                    // the UNDROPPED data (the wet-corner test above); relief moves HEIGHT ONLY. The old
                    // dry-out here shrank the footprint as the slider dropped — the sheet edge RETREATED
                    // from the banks (jagged floating edges short of shore, user screenshot) instead of
                    // tucking under them. Where the dropped surface dips below the shore ground the TERRAIN
                    // CLIPS the sheet — buried edge, exactly right. Depth floors at 0.05 so the node still
                    // emits; swim/physics see the level under their feet there and correctly ignore it.
                    depth = Mathf.Max(0.05f, level - ground);
                    return true;
                }
            }

            // DRY margin: bilinear over the decimated envelope — the feather
            // band paint/skirt/mouth logic reads just above the waterline.
            int n2 = _surfN; float c2 = _surfCell;
            float sx = (wx + half) / c2, sz = (wz + half) / c2;
            if (sx < 0f) sx = 0f; else if (sx > n2 - 1.001f) sx = n2 - 1.001f;
            if (sz < 0f) sz = 0f; else if (sz > n2 - 1.001f) sz = n2 - 1.001f;
            int sxi = (int)sx, szi = (int)sz;
            float sfx = sx - sxi, sfz = sz - szi;
            int si = szi * n2 + sxi;
            short c00 = s[si], c10 = s[si + 1], c01 = s[si + n2], c11 = s[si + n2 + 1];
            if (c00 == SURF_CUT || c10 == SURF_CUT || c01 == SURF_CUT || c11 == SURF_CUT)
            {
                // EDGE HEAL: a bilinear touching a suppressed cell used to
                // return "no water", which pulled the store/sheet up to a
                // full 8m short of banks and pond walls (user screenshots:
                // sheet ending mid-bowl). Fall back to the NEAREST live cell
                // so coverage runs flush to the last wet cell.
                int nxi = sfx < 0.5f ? sxi : sxi + 1;
                int nzi = sfz < 0.5f ? szi : szi + 1;
                short nc = s[nzi * n2 + nxi];
                if (nc == SURF_CUT) return false;
                level = nc * UNPACK;
            }
            else
                level = (c00 * (1f - sfx) * (1f - sfz) + c10 * sfx * (1f - sfz)
                       + c01 * (1f - sfx) * sfz + c11 * sfx * sfz) * UNPACK;
            if (level > SEA + 0.3f)
            {
                // Relief is baked at GENERATION (RiverLevels: min(bank) - RELIEF), so only the slider's
                // DEVIATION from the default is applied here. Both query sites must agree, or the two
                // surfaces disagree about where the same water is. See the long note at the smooth query.
                float extra2 = ReliefFor(wx, wz) - DefaultRiverRelief;   // 0 at the shipped default
                if (extra2 > 0.0001f || extra2 < -0.0001f) level -= extra2;
            }
            depth = level - ground;
            if (depth <= 0f) { depth = 0f; return false; }
            return true;
        }

        // ═══ IS THIS WATER IN A CANYON? — DERIVED AT RUNTIME, ON PURPOSE ══════════════════════════════════
        // (User, 2026-08-20: "a river flowing through a canyon should read as canyon river, not river
        // (flowing)" — so they can tell from inside the world whether the reaches that look wrong are the
        // canyon ones.)
        // ⚠ IT IS NOT READ FROM `LastIncisionClass`, AND THAT IS DELIBERATE. That array is produced by
        // ClassifyChannelIncision DURING GENERATION and is NOT baked into the artifact, so on a RESTORED world
        // — which is every normal launch — it is null. A label that only worked on the one launch that
        // generated the world would be worse than no label: it would read "River" in exactly the sessions the
        // user is inspecting. This asks the TERRAIN instead, which the artifact does carry.
        // The test is the same one the `riverfield` C. CANYON vs OPEN census uses, so the in-world label and
        // the offline measurement always agree about what a canyon is: a wall standing WALL_M above the water
        // within RING_M of it. Sampled on a ring rather than a filled block — this runs per HUD frame.
        public static bool IsCanyonWaterAt(float wx, float wz)
        {
            const float RING_M = 16f;   // how far out to look for a wall
            const float WALL_M = 12f;   // ground this far above the surface is a wall, not a bank
            if (!TryGetWaterSurfaceSmoothReal(wx, wz, out float lvl, out _)) return false;

            // ═══ A CANYON HAS TWO WALLS. ONE IS A HILLSIDE. ══════════════════════════════════════════════
            // (user, 2026-08-31, on a map full of pins in flat lake country: "clearly something is wrong
            // because these areas marked as canyons are anything but".)
            // ⛔ THIS USED TO RETURN TRUE ON *ANY* OF 32 SAMPLES — 8 spokes x radii 4..16. So a single steep
            // bank on ONE side, which is what most rivers in rolling terrain have, satisfied it. That is a
            // riverbank against a hill, not a canyon, and it is why the survey pinned lowland lake country
            // and skipped the one real mountain range. It also means every "canyon reaches" count taken with
            // this test (the riverfield C. CANYON census, and the 9,501 figure quoted from it) counted steep
            // banks and was inflated — those numbers should not be reused.
            // The defining feature of a slot is that it is walled on BOTH sides, so the test now requires a
            // wall in some direction AND in the OPPOSITE direction. Same spokes, same radii, same threshold;
            // only the quantifier changes, from "any" to "opposing pair".
            for (int a = 0; a < 4; a++)          // 4 axes, each checked against its own opposite
            {
                float th = a * 0.7853982f;       // pi/4 apart: the 8 spokes are 4 opposing pairs
                float cx = Mathf.Cos(th), cz = Mathf.Sin(th);
                bool wallA = false, wallB = false;
                for (int r = 4; r <= (int)RING_M && !(wallA && wallB); r += 4)
                {
                    if (!wallA && TryGetGenGroundReal(wx + cx * r, wz + cz * r, out float ga) && ga - lvl >= WALL_M) wallA = true;
                    if (!wallB && TryGetGenGroundReal(wx - cx * r, wz - cz * r, out float gb) && gb - lvl >= WALL_M) wallB = true;
                }
                if (wallA && wallB) return true;
            }
            return false;
        }

        private static readonly System.Func<float, float, float> s_genGroundReal = GenGroundOrFinal;

        private static float GenGroundOrFinal(float wx, float wz)
            => TryGetGenGroundReal(wx, wz, out float ground) ? ground : SampleFinalMetersReal(wx, wz);

        public static bool TryGetGenGround(float wx, float wz, out float ground)
        {
            float tx = wx, tz = wz;
            if (Zones.ZoneModeGate.TryClosedHeight(ref wx, ref wz, s_genGroundReal, out ground)) return true;
            if (!TryGetGenGroundReal(wx, wz, out ground)) return false;
            ground = Zones.ZoneModeGate.Lifted(tx, tz, ground);
            return true;
        }

        // Full-res GENERATED ground (bilinear) — the pure field, no brush
        // edits. Cheap enough for per-cell gates at view apply.
        internal static bool TryGetGenGroundReal(float wx, float wz, out float ground)
        {
            ground = 0f;
            var g = _gridS;
            if (g == null) return false;
            float half = _half;
            int n = _res;
            float cell = (2f * half) / (n - 1);
            float gx = (wx + half) / cell, gz = (wz + half) / cell;
            if (gx < 0f) gx = 0f; else if (gx > n - 1.001f) gx = n - 1.001f;
            if (gz < 0f) gz = 0f; else if (gz > n - 1.001f) gz = n - 1.001f;
            int xi = (int)gx, zi = (int)gz;
            float fx = gx - xi, fz = gz - zi;
            int i = zi * n + xi;
            ground = (g[i] * (1f - fx) * (1f - fz) + g[i + 1] * fx * (1f - fz)
                    + g[i + n] * (1f - fx) * fz + g[i + n + 1] * fx * fz) * UNPACK;
            return true;
        }

        // SMOOTH terrain-surface normal — a PURE FUNCTION of world XZ (the un-quantized ground-height
        // gradient). Because it's world-space, coincident chunk-border vertices get the IDENTICAL normal, so
        // the voxel chunks BLEND instead of each 32m tile shading distinctly (the per-chunk sbyte-density
        // gradient the mesher uses otherwise). Returns false unless the point sits ON the terrain surface
        // (|wy − ground| small) so cave/island/overhang bodies keep their own density-gradient normals.
        public static bool TrySurfaceNormal(float wx, float wy, float wz, out Vector3 normal)
        {
            normal = Vector3.up;
            if (!TryGetGenGroundReal(wx, wz, out float g) || System.Math.Abs(wy - g) > 1.5f) return false;
            const float e = 2f;   // central-difference radius (m); wider = smoother
            if (!TryGetGenGroundReal(wx - e, wz, out float hxm) || !TryGetGenGroundReal(wx + e, wz, out float hxp)
             || !TryGetGenGroundReal(wx, wz - e, out float hzm) || !TryGetGenGroundReal(wx, wz + e, out float hzp)) return false;
            normal = new Vector3(-(hxp - hxm) / (2f * e), 1f, -(hzp - hzm) / (2f * e)).normalized;
            return true;
        }

        // Per-point DOWNHILL FLOW (world m/m, depth-damped, capped) — the SAME
        // vector VoxelChunkView bakes into the sheet's UV1 (minus the per-corner
        // coherence pass, which only tunes the visual chop). Physics reads this
        // so buoyancy bobs with the visual rolling swell. Zero = still / dry.
        // ── WHICH WAY IS DOWNHILL, MEASURED OVER A DISTANCE WHERE THE DROP IS REAL ──────────────────────
        // The user's framing, and it is the right one: a reach that runs y 114 → y 100 is unambiguously
        // flowing downhill and we already know which way that is — the water surface itself says so.
        //
        // Nothing here needs the river plan, its polyline ordering, or any bed gradient. It reads the SAME
        // reconciled surface every other consumer reads. The only reason that was not already enough is
        // BASELINE: a ±1 m central difference across a 14 m drop spread over a few hundred metres sees
        // millimetres, which is under the noise in the stored heights, so its DIRECTION came out close to
        // random. Sampling the same field at a long radius turns the same 14 m into an overwhelming signal.
        //
        // Samples are kept only where the neighbour is still WATER, so a meander cannot borrow the bank or
        // the next reach over, and the result is a drop-WEIGHTED mean over 8 directions rather than a
        // single winner — that keeps it smooth and continuous instead of stepping between compass points.
        // Returns false where nothing downhill is reachable (a flat pond, a lake), and the caller keeps
        // whatever it already had.
        //
        // ⚠ DIRECTION ONLY at the call sites. An earlier attempt (788a04a8, reverted in 2f307e05) also
        // replaced the MAGNITUDE, which on a cascade swapped the large and genuinely correct local gradient
        // for a much smaller number and made steep water look worse. On a steep face −∇(surface) is the one
        // signal that already works; only the direction ever needed fixing.
        // How far to look to decide "which way is downhill". Long enough that a real reach's drop swamps
        // the height noise, short enough to still follow a bend rather than cut the corner off a meander.
        public const float FLOW_BASELINE = 24f;

        public static bool TryGetSurfaceDescent(float wx, float wz, float radius, out Vector2 dir)
        {
            dir = Vector2.zero;
            if (!TryGetWaterSurfaceSmoothReal(wx, wz, out float here, out _) || here <= 0f) return false;
            // ESCALATING BASELINE — the whole point, and the reason a single radius was not enough.
            // A pool immediately below a rapid genuinely IS flat: over 24 m its surface does not measurably
            // drop, so the near ring finds nothing and the old code fell back to the broken bed gradient.
            // But a flat reach still flows, because it still has to DRAIN somewhere — "even a flat river
            // flows in the direction it travels overall". So when the near ring is level, look further, and
            // further again. Pools are flat locally and never flat at 150 m; a genuine lake is flat at every
            // radius and correctly gets nothing. Each ring only costs anything when the previous one was
            // level, so a normal reach still answers on the first try.
            if (DescentRing(wx, wz, here, radius, ref dir)) return true;
            if (DescentRing(wx, wz, here, radius * 2.5f, ref dir)) return true;
            if (DescentRing(wx, wz, here, radius * 6f, ref dir)) return true;
            // ── LOCAL MINIMUM: the plunge pool at the foot of a drop ──────────────────────────────────────
            // Every ring above asks "which way is down", and in a pool immediately below a slope the answer
            // is NOWHERE — it is a local low point, so every direction around it reads uphill and all three
            // rings fail. That is precisely the dead spot that appears after every slope: no descent found
            // means no floor applied, so the garbage value underneath survives.
            // But the water is obviously still moving through it. It ARRIVES from the high side and LEAVES
            // the other way, so the honest answer is the direction of steepest ASCENT, reversed: flow away
            // from wherever this water is deepest-fed. Weighted by rise for the same reason descent is
            // weighted by drop — the dominant inflow should set the direction, not whichever probe wins.
            return AwayFromHighest(wx, wz, here, radius * 2.5f, ref dir);
        }

        // The local-minimum fallback: 8 probes, keep only WATER samples that lie ABOVE here, and return the
        // rise-weighted mean direction REVERSED — i.e. away from where the water is coming from. In a plunge
        // pool the highest surrounding water is the drop you just came over, so this points downstream; in a
        // flat pool fed from one end it points at the outlet. Both are the direction the water leaves by.
        private static bool AwayFromHighest(float wx, float wz, float here, float radius, ref Vector2 dir)
        {
            const int N = 8;
            Vector2 acc = Vector2.zero;
            float wsum = 0f;
            for (int k = 0; k < N; k++)
            {
                float a = k * (6.2831853f / N);
                float dx = Mathf.Cos(a), dz = Mathf.Sin(a);
                if (!TryGetWaterSurfaceSmoothReal(wx + dx * radius, wz + dz * radius, out float lv, out _) || lv <= 0f)
                    continue;
                float rise = lv - here;
                if (rise <= 1e-4f) continue;
                acc += new Vector2(dx, dz) * rise;
                wsum += rise;
            }
            if (wsum <= 1e-5f || acc.sqrMagnitude <= 1e-12f) return false;
            dir = (-acc).normalized;   // AWAY from the inflow
            return true;
        }

        // One ring of the search: 8 probes at `radius`, keep only WATER samples that lie BELOW here, and
        // return their drop-weighted mean direction. Weighting by drop rather than counting means the true
        // fall line dominates instead of the result snapping to whichever compass point happened to win.
        private static bool DescentRing(float wx, float wz, float here, float radius, ref Vector2 dir)
        {
            const int N = 8;
            Vector2 acc = Vector2.zero;
            float wsum = 0f;
            for (int k = 0; k < N; k++)
            {
                float a = k * (6.2831853f / N);
                float dx = Mathf.Cos(a), dz = Mathf.Sin(a);
                if (!TryGetWaterSurfaceSmoothReal(wx + dx * radius, wz + dz * radius, out float lv, out _) || lv <= 0f)
                    continue;                       // dry, or off the sheet — not part of this water
                float drop = here - lv;
                if (drop <= 1e-4f) continue;        // level or uphill in that direction: contributes nothing
                acc += new Vector2(dx, dz) * drop;
                wsum += drop;
            }
            if (wsum <= 1e-5f || acc.sqrMagnitude <= 1e-12f) return false;
            dir = acc.normalized;
            return true;
        }

        public static Vector2 GenFlowAt(float wx, float wz)
            => Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz) ? Vector2.zero : GenFlowAtReal(wx, wz);

        internal static Vector2 GenFlowAtReal(float wx, float wz)
        {
            if (!TryGetWaterSurfaceSmoothReal(wx, wz, out float hC, out float depthC) || hC <= 0f)
                return Vector2.zero;
            Vector2 fl = Vector2.zero;
            if (TryGetWaterSurfaceSmoothReal(wx - 1f, wz, out float hXm, out _) && hXm > 0f
                && TryGetWaterSurfaceSmoothReal(wx + 1f, wz, out float hXp, out _) && hXp > 0f
                && TryGetWaterSurfaceSmoothReal(wx, wz - 1f, out float hZm, out _) && hZm > 0f
                && TryGetWaterSurfaceSmoothReal(wx, wz + 1f, out float hZp, out _) && hZp > 0f)
                fl = new Vector2((hXm - hXp) * 0.5f, (hZm - hZp) * 0.5f);

            byte cls = WaterClassAtReal(wx, wz);
            if (cls == WC_RIVER || cls == WC_STREAM)
            {
                float smag = fl.magnitude;
                const float DRIFT = 0.025f;
                if (smag < DRIFT
                    && TryGetGenGroundReal(wx - 3f, wz, out float bxm) && TryGetGenGroundReal(wx + 3f, wz, out float bxp)
                    && TryGetGenGroundReal(wx, wz - 3f, out float bzm) && TryGetGenGroundReal(wx, wz + 3f, out float bzp))
                {
                    var bed = new Vector2((bxm - bxp) * (1f / 6f), (bzm - bzp) * (1f / 6f));
                    float bmag = bed.magnitude;
                    if (bmag > 1e-4f) fl = bed * (DRIFT / bmag);
                    else if (smag > 1e-5f) fl *= DRIFT / smag;
                }
                // DIRECTION FROM THE LONG-BASELINE SURFACE DESCENT, MAGNITUDE LEFT EXACTLY AS DECIDED ABOVE.
                if (TryGetSurfaceDescent(wx, wz, FLOW_BASELINE, out Vector2 ddir))
                {
                    float m = fl.magnitude;
                    fl = ddir * (m > 1e-6f ? m : DRIFT);
                }
            }
            float mag = fl.magnitude;
            if (mag > 0.35f) fl *= 0.35f / mag;
            fl *= Mathf.Clamp01(depthC / 0.5f);
            return fl;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  OBSTACLE-DISTURBANCE FIELD — rapids realism, Tier A (baked at gen)
        //  docs/HYDROLOGY_SHADING.md Part VII. Water reacting to solids sitting
        //  in the current: a raised PILLOW upstream of a rock, foam+swirl in the
        //  WAKE and along the two EDDYLINES, a world-pinned STANDING-WAVE train
        //  on fast reaches, a violently aerated BACKWASH at waterfall-lip bases,
        //  and a thin FOAM COLLAR where flowing water meets any solid. A flat
        //  −∇(level) flow field knows nothing of a rock in it (it flows straight
        //  through); this pass stamps the analytic reaction ONCE at gen into a
        //  resident field the water-sheet builder samples per vertex → four
        //  shader channels + a real mesh-Y deform.
        //
        //  CHANNEL CONTRACT (COORDINATION.md 2026-08-09, LOCKED to UV3 —
        //  supersedes the doc's stale UV2 table):
        //    UV3.x = surface HEIGHT perturbation (m, signed)  → also deforms vert Y
        //    UV3.y = AERATION 0..1 (entrained-air / whitewater)
        //    UV3.z = signed VORTICITY −1..1 (eddy spin / the two eddylines)
        //    UV3.w = STANDING-WAVE LOCK 0..1 (1 = world-pinned, doesn't scroll)
        //  UV1 (flow/coherence/fluid) and UV2.x (depth) are SHIPPED and UNTOUCHED
        //  — the eddy "reversed flow" + shoulder "split" are rendered by the
        //  SHADER from UV3.z (its local scroll override) + UV3.y, never by
        //  rewriting the physics flow (which also drives buoyancy). Froude is an
        //  INPUT here (kernel magnitudes key off it); it is NOT emitted (UV2.y is
        //  Part V's reserved home, coordinate before emitting).
        //
        //  RESIDENT + SPARSE: disturbance lives only near solids-in-current, a
        //  tiny fraction of the 2m grid, so a dense full-res field (~0.9 GB at the
        //  game's 10501² res — the dedi already fights the packed grids' ~0.5 GB)
        //  is out. It's a Dictionary<cell, packed-u32>, memory ∝ stamped cells.
        //  Rebuilt deterministically from the resident _gridS/_waterS/_riverMask
        //  on BOTH gen AND artifact-restore (same inputs → identical field), so a
        //  cached-seed relaunch shows it too (chunks re-mesh on the GEN_VERSION
        //  bump and re-sample). Closed-form, one pass — mirrors InciseRivers.
        // ═══════════════════════════════════════════════════════════════════
        private static Dictionary<int, uint> _obst;   // cell(gz*n+gx) → packed {pert,aer,vort,lock}; absent = calm
        // ═══ COARSE REJECT MASK FOR THE DISTURBANCE FIELD ════════════════════════════════════════════════
        // (2026-09-04, measured: `TryGetObstacleDisturbance` is 0.1344 us/call against 0.0066 for
        // WaterClassAt and 0.0197 for TryGetGenGround — 61 % of the modelled per-corner cost of the water
        // sheet's corner pass, which is `apply.water`'s biggest remaining component at 111–345 ms/s.)
        // The reason is not that the lookup is slow, it is that it ALMOST ALWAYS MISSES: the field is a
        // sparse Dictionary of rapids cells, and on a real world only **392 of 400,000** sampled positions
        // had any data at all. So 99.9 % of calls paid four hash probes and a bilinear setup to return false.
        // This is one bit per OBST_TILE cells of the 2 m grid, set when the dictionary is built. A miss costs
        // one array read and a shift; only a position whose tile actually holds baked cells reaches the
        // dictionary. It cannot change a RESULT — the mask is a strict superset of the key set, so anything
        // the dictionary would have answered still reaches it.
        // ⛔ IF YOU ADD A WRITER OF `_obst`, BUILD THE MASK WITH IT. A stale mask does not read as slow, it
        // reads as MISSING RAPIDS — the field silently answering false where data exists.
        private const int OBST_TILE = 16;          // 32 m at 2 m/cell
        private static byte[] _obstTile;           // 1 = this tile holds at least one baked cell
        private static int _obstTileN;
        private static readonly bool s_obstTileOn =
            System.Environment.GetEnvironmentVariable("FIRES_OBST_TILE") != "0";
        private static void BuildObstTileMask(Dictionary<int, uint> src, int n)
        {
            if (src == null || src.Count == 0) { _obstTile = null; _obstTileN = 0; return; }
            int tn = (n + OBST_TILE - 1) / OBST_TILE;
            var m = new byte[tn * tn];
            foreach (var kv in src)
            {
                int i = kv.Key, gx = i % n, gz = i / n;
                int tx = gx / OBST_TILE, tz = gz / OBST_TILE;
                if (tx >= 0 && tz >= 0 && tx < tn && tz < tn) m[tz * tn + tx] = 1;
            }
            _obstTile = m; _obstTileN = tn;
        }


        // 8-neighbour offsets (dx,dz) — the bake's rings.
        private static readonly int[] NB8X = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] NB8Z = { -1, -1, -1, 0, 0, 1, 1, 1 };

        // Float accumulator (many kernels overlap a cell); packed once at the end.
        private struct DistAccum { public float pert, aer, vort, lok; }

        // 32-bit pack: pert 12b (signed cm, ±20.47 m) · aer 8b (/255) ·
        // vort 8b (signed, /127) · lock 4b (/15). Lossy but bilinear-smoothed.
        private static uint PackDist(float pert, float aer, float vort, float lok)
        {
            int p = Mathf.Clamp(Mathf.RoundToInt(pert * 100f), -2048, 2047) + 2048;
            int a = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(aer) * 255f), 0, 255);
            int v = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp(vort, -1f, 1f) * 127f), -127, 127) + 128;
            int l = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(lok) * 15f), 0, 15);
            return (uint)((p & 0xFFF) | (a << 12) | (v << 20) | (l << 28));
        }
        private static void UnpackDist(uint u, out float pert, out float aer, out float vort, out float lok)
        {
            pert = ((int)(u & 0xFFFu) - 2048) * 0.01f;
            aer = ((u >> 12) & 0xFFu) / 255f;
            vort = ((int)((u >> 20) & 0xFFu) - 128) / 127f;
            lok = ((u >> 28) & 0xFu) / 15f;
        }

        // THE sheet sampler — bilinear over the 2m field, absent cells = calm.
        // Returns false when the whole 2×2 is calm so the caller skips the add.
        // Alloc-free (value-type dict, no boxing).
        public static bool TryGetObstacleDisturbance(float wx, float wz,
            out float pert, out float aer, out float vort, out float lok)
        {
            pert = aer = vort = lok = 0f;
            var o = _obst;
            if (o == null || o.Count == 0) return false;
            // COARSE REJECT — one array read instead of four hash probes for the 99.9 % that miss.
            // Superset by construction (every dictionary key sets its tile), so no real hit is lost.
            // FIRES_OBST_TILE=0 disables it, for A/B against the dictionary-only path.
            var tm = _obstTile;
            if (tm != null && s_obstTileOn)
            {
                int tn = _obstTileN;
                int qn = _res; float qhalf = _half; float qcell = (2f * qhalf) / (qn - 1);
                int qx = (int)((wx + qhalf) / qcell), qz = (int)((wz + qhalf) / qcell);
                if (qx < 0) qx = 0; else if (qx > qn - 1) qx = qn - 1;
                if (qz < 0) qz = 0; else if (qz > qn - 1) qz = qn - 1;
                // the bilinear below reads cells (xi,zi)..(xi+1,zi+1); test BOTH tiles they can span
                int t0x = qx / OBST_TILE, t0z = qz / OBST_TILE;
                int t1x = (qx + 1) / OBST_TILE, t1z = (qz + 1) / OBST_TILE;
                if (t1x > tn - 1) t1x = tn - 1; if (t1z > tn - 1) t1z = tn - 1;
                if (tm[t0z * tn + t0x] == 0 && tm[t0z * tn + t1x] == 0
                    && tm[t1z * tn + t0x] == 0 && tm[t1z * tn + t1x] == 0) return false;
            }
            int n = _res; float half = _half; float cell = (2f * half) / (n - 1);
            float gx = (wx + half) / cell, gz = (wz + half) / cell;
            if (gx < 0f) gx = 0f; else if (gx > n - 1.001f) gx = n - 1.001f;
            if (gz < 0f) gz = 0f; else if (gz > n - 1.001f) gz = n - 1.001f;
            int xi = (int)gx, zi = (int)gz;
            float fx = gx - xi, fz = gz - zi;
            int i = zi * n + xi;
            bool any = false;
            AccumCorner(o, i,         (1f - fx) * (1f - fz), ref pert, ref aer, ref vort, ref lok, ref any);
            AccumCorner(o, i + 1,     fx * (1f - fz),        ref pert, ref aer, ref vort, ref lok, ref any);
            AccumCorner(o, i + n,     (1f - fx) * fz,        ref pert, ref aer, ref vort, ref lok, ref any);
            AccumCorner(o, i + n + 1, fx * fz,               ref pert, ref aer, ref vort, ref lok, ref any);
            return any;
        }
        private static void AccumCorner(Dictionary<int, uint> o, int idx, float wgt,
            ref float pert, ref float aer, ref float vort, ref float lok, ref bool any)
        {
            if (wgt <= 0f || !o.TryGetValue(idx, out uint u)) return;
            UnpackDist(u, out float p, out float a, out float v, out float l);
            pert += p * wgt; aer += a * wgt; vort += v * wgt; lok += l * wgt; any = true;
        }

        private static void StampDist(Dictionary<int, DistAccum> acc, int idx,
            float pert, float aer, float vort, float lok)
        {
            // === THE DISCARDED BOOL IS SAFE HERE, AND ITS SAFETY IS FRAGILE. READ THIS BEFORE ADDING ===
            // === A BRANCH ON PRESENCE. (2026-09-07, [clutter]'s taxonomy after the `fb` bug.) =========
            // Nothing below asks whether the key EXISTED: accumulate-then-store gives the identical
            // result for a miss (zeroed struct) and for a real all-zero entry, so the distinction is not
            // needed and the bool is genuinely irrelevant - not merely "recovered elsewhere".
            // WARNING - this is NOT "safe because DistAccum is a struct". Every field is a float and ZERO
            // IS A LEGITIMATE VALUE, so a miss and a real zeroed entry are INDISTINGUISHABLE. That is
            // harmless only while no consumer needs to tell them apart.
            // The moment anyone adds a branch here that cares whether this cell had been stamped before,
            // this line stops being safe and NOTHING in the file will say so - the guard lives in the
            // ABSENCE of a branch, which is invisible to the person adding one. Capture the bool then.
            // (Contrast :907-910, which read the same dictionary and DO test the flag. Both forms are
            // correct for what they do; do not "tidy" one into the other.)
            acc.TryGetValue(idx, out var d);
            // ⚠ HEIGHTS SUPERPOSE BUT ARE CAPPED (2026-08-18, user: the filled channel "still pretty lumpy").
            // `pert` is added to VERTEX Y in VoxelChunkView — it is real geometry, not a shader tint — and
            // this sum had no bound. A single feature is PILLOW_H 0.30 m / WAVE_H 0.35 m, but the bake stamps
            // 9,522 mid-stream obstacles + 1,306 waterfall bases + 247,654 collar cells into 323,260 cells, so
            // wherever their footprints overlap the sum ran to ±0.5–0.7 m and the sheet read as LUMPY — and no
            // amount of node smoothing could touch it, because this is applied after the sheet is built.
            // Two overlapping rocks do not make a wave twice as tall; the water finds one surface. Capping at
            // the tallest single feature keeps every individual pillow/standing wave exactly as authored and
            // only removes the stacking artefact. `aer` already used this reasoning ("strongest overlap, not a
            // runaway sum") — the height term simply never got the same treatment.
            const float PERT_CAP = 0.35f;   // = WAVE_H, the tallest single authored feature
            d.pert = Mathf.Clamp(d.pert + pert, -PERT_CAP, PERT_CAP);
            if (aer > d.aer) d.aer = aer;   // foam = strongest overlap, not a runaway sum
            d.vort += vort;                 // signed shear superposes (opposite eddylines cancel)
            if (lok > d.lok) d.lok = lok;
            acc[idx] = d;
        }

        // Cheap surface-slope flow (downhill, world m/m) + depth from the packed
        // field — the bake's dense inner flow (avoids GenFlowAt's 5 smooth
        // queries per cell). Guards on neighbour wetness like GenFlowAt; falls
        // back to the bed gradient where the surface is flat. gx,gz in [1,n-2].
        private static void CellFlow(int gx, int gz, out float fx, out float fz, out float depth)
        {
            int n = _res; var w = _waterS; var g = _gridS;
            int i = gz * n + gx;
            depth = (w[i] - g[i]) * UNPACK; fx = 0f; fz = 0f;
            float lxm = w[i - 1] * UNPACK, lxp = w[i + 1] * UNPACK, lzm = w[i - n] * UNPACK, lzp = w[i + n] * UNPACK;
            float gxm = g[i - 1] * UNPACK, gxp = g[i + 1] * UNPACK, gzm = g[i - n] * UNPACK, gzp = g[i + n] * UNPACK;
            if (lxm > gxm + 0.01f && lxp > gxp + 0.01f && lzm > gzm + 0.01f && lzp > gzp + 0.01f)
            { fx = (lxm - lxp) * 0.5f; fz = (lzm - lzp) * 0.5f; }
            if (fx * fx + fz * fz < 0.02f * 0.02f)   // flat surface → bed gradient gives the downstream sense
            { fx = (gxm - gxp) * 0.5f; fz = (gzm - gzp) * 0.5f; }
        }

        // Build the resident disturbance field from the FINAL water world. Runs
        // on the gen thread after the grids are packed + labels built (gen,
        // studio SolveWater3D, and artifact-restore all call it with identical
        // resident inputs → identical field). Magnitudes are in METRES / 0..1;
        // the shader team tunes the final LOOK, this sets physically-oriented data.
        public static void BakeObstacleDisturbance()
        {
            _obst = null; _obstTile = null; _obstTileN = 0;
            var g = _gridS; var w = _waterS; var rm = _riverMask;
            int n = _res;
            if (g == null || w == null || rm == null || n < 8) return;
            float half = _half; float cell = (2f * half) / (n - 1);

            const float WET = 0.06f, SOLIDMARGIN = 0.10f;
            const float PILLOW_LEN = 5f, PILLOW_W = 2.5f, PILLOW_H = 0.30f;
            const float WAKE_LEN = 10f, EDDYOFF = 2.5f, EDDYSIG = 1.6f;
            const float EDDY_VORT = 0.9f, EDDYLINE_AER = 0.55f, EDDY_DIP = 0.12f;
            const float WAVE_H = 0.35f, WAVE_HALFW = 3f, WAVE_TRAIN = 2.5f, WAVE_CREST_AER = 0.7f;
            const float COLLAR_AER = 0.28f, COLLAR_LIFT = 0.04f;   // thin BANK ACCENT only — kept under the shader's smoothstep(0.5,0.9) foam gate so a narrow river's all-bank-adjacent cells don't wash milky (shaders 2026-08-09); real rapids/eddyline/backwash still foam from their own higher UV3.y
            const float BACK_AER = 1f, BACK_DIP = 0.25f;
            const int RWIN = 9;   // kernel window radius (cells) — covers the wake/wave train

            var acc = new Dictionary<int, DistAccum>();
            var solids = new HashSet<int>();   // deduped obstacle rocks (each stamps ONE kernel set)
            int obstacles = 0, waterfalls = 0, collars = 0;

            for (int gz = 2; gz < n - 2; gz++)
                for (int gx = 2; gx < n - 2; gx++)
                {
                    int i = gz * n + gx;
                    if (rm[i] == 0) continue;                       // only river/stream water cells drive the pass
                    float L = w[i] * UNPACK, gr = g[i] * UNPACK;
                    if (L - gr <= WET) continue;                    // must be genuinely wet
                    float wcx = -half + gx * cell, wcz = -half + gz * cell;

                    CellFlow(gx, gz, out float lfx, out float lfz, out float _);
                    float lspd = Mathf.Sqrt(lfx * lfx + lfz * lfz);

                    // ── CONTACT COLLAR (feature 8): flowing water touching any
                    //    solid (bank OR rock) — foam + thin meniscus, scaled by
                    //    local flow so calm shores stay glassy (no lake-edge foam). ──
                    bool touchesSolid = false;
                    for (int k = 0; k < 8 && !touchesSolid; k++)
                    {
                        int sx = gx + NB8X[k], sz = gz + NB8Z[k];
                        if (g[sz * n + sx] * UNPACK >= L - SOLIDMARGIN) touchesSolid = true;
                    }
                    if (touchesSolid)
                    {
                        // FLOW FLOOR: only genuinely MOVING water foams at a solid contact — a still pool's
                        // shore is glassy. Below ~0.03 m/m the collar is OFF entirely (kills a faint foam ring
                        // on near-still river-classified pools that reads as noise).
                        float ff = Mathf.Clamp01((lspd - 0.03f) / 0.12f);
                        if (ff > 0.02f) { StampDist(acc, i, COLLAR_LIFT * ff, COLLAR_AER * ff, 0f, 0f); collars++; }
                    }

                    // ── HOLE / BACKWASH (feature 5): the aerated recirculation at
                    //    the base of a waterfall drop (we already tag WC_WATERFALL
                    //    lips). Stamp ~2 cells downstream: aeration~1, a dip, locked.
                    //    Reversed surface flow is the shader's job (from aeration~1). ──
                    if (lspd > 1e-4f && WaterClassAtReal(wcx, wcz) == WC_WATERFALL)
                    {
                        float ux0 = lfx / lspd, uz0 = lfz / lspd;
                        int bx = gx + (int)Mathf.Round(ux0 * 2f), bz = gz + (int)Mathf.Round(uz0 * 2f);
                        if (bx >= 0 && bx < n && bz >= 0 && bz < n)
                        {
                            int bi = bz * n + bx;
                            if (rm[bi] != 0 && (w[bi] - g[bi]) * UNPACK > WET
                                && WaterClassAtReal(-half + bx * cell, -half + bz * cell) != WC_WATERFALL)
                            { StampDist(acc, bi, -BACK_DIP, BACK_AER, 0f, 1f); waterfalls++; }
                        }
                    }

                    // ── MID-STREAM OBSTACLES (features 1–4): a solid poking above
                    //    the water that the current HITS — found from this water
                    //    cell's solid neighbours, deduped so each rock stamps once. ──
                    for (int k = 0; k < 8; k++)
                    {
                        int sx = gx + NB8X[k], sz = gz + NB8Z[k];
                        if (sx < 2 || sx >= n - 2 || sz < 2 || sz >= n - 2) continue;
                        int si = sz * n + sx;
                        if (rm[si] != 0) continue;                          // neighbour is water, not a solid
                        if (g[si] * UNPACK < L - SOLIDMARGIN) continue;     // submerged bed, not poking above
                        if (!solids.Add(si)) continue;                      // this rock already stamped

                        // count its wet river neighbours + find the wettest (flow probe)
                        int wetN = 0; float maxL = -1e9f; int bestX = sx, bestZ = sz;
                        for (int m = 0; m < 8; m++)
                        {
                            int nx = sx + NB8X[m], nz = sz + NB8Z[m]; int ni = nz * n + nx;
                            if (rm[ni] == 0) continue;
                            float Lm = w[ni] * UNPACK;
                            if (Lm <= g[ni] * UNPACK + WET) continue;
                            wetN++; if (Lm > maxL) { maxL = Lm; bestX = nx; bestZ = nz; }
                        }
                        if (wetN < 4) continue;                             // too little surrounding flow → a plain bank

                        var fl = GenFlowAtReal(-half + bestX * cell, -half + bestZ * cell);
                        float spd = fl.magnitude;
                        if (spd < 1e-4f) continue;                          // no current → collar only (already done)
                        float ux = fl.x / spd, uz = fl.y / spd;            // unit downstream
                        // the current must actually HIT it: a wet neighbour on the
                        // UPSTREAM face (−flow). Rules out straight banks the flow
                        // runs parallel to; keeps mid-stream rocks + jutting promontories.
                        bool upstreamWet = false;
                        for (int m = 0; m < 8 && !upstreamWet; m++)
                        {
                            int nx = sx + NB8X[m], nz = sz + NB8Z[m]; int ni = nz * n + nx;
                            if (rm[ni] == 0 || (w[ni] - g[ni]) * UNPACK <= WET) continue;
                            float dl = Mathf.Sqrt((float)(NB8X[m] * NB8X[m] + NB8Z[m] * NB8Z[m]));
                            if ((NB8X[m] * -ux + NB8Z[m] * -uz) / dl > 0.3f) upstreamWet = true;
                        }
                        if (!upstreamWet) continue;

                        float px = -uz, pz = ux;                            // unit perpendicular (left of flow)
                        float sn = Mathf.Clamp01(spd / 0.35f);              // normalised flow strength
                        float dNbr = Mathf.Max(0.1f, maxL - g[bestZ * n + bestX] * UNPACK);
                        float bxm = g[sz * n + (sx - 1)] * UNPACK, bxp = g[sz * n + (sx + 1)] * UNPACK;
                        float bzm = g[(sz - 1) * n + sx] * UNPACK, bzp = g[(sz + 1) * n + sx] * UNPACK;
                        float bslope = Mathf.Sqrt(((bxm - bxp) * 0.5f) * ((bxm - bxp) * 0.5f)
                                                       + ((bzm - bzp) * 0.5f) * ((bzm - bzp) * 0.5f)) / cell;
                        float fr = spd / Mathf.Sqrt(Mathf.Max(dNbr, 0.05f));   // Froude proxy (input only)
                        float rapids = Mathf.Clamp01(Mathf.Max((bslope - 0.05f) / 0.15f, (fr - 0.5f) / 0.6f));
                        float lam = Mathf.Clamp(dNbr * 3f * (0.7f + sn), 3f, 10f);           // standing-wave λ (m)

                        for (int dz = -RWIN; dz <= RWIN; dz++)
                            for (int dx = -RWIN; dx <= RWIN; dx++)
                            {
                                int cxg = sx + dx, czg = sz + dz;
                                if (cxg < 0 || cxg >= n || czg < 0 || czg >= n) continue;
                                int ci = czg * n + cxg;
                                if (rm[ci] == 0) continue;                  // only stamp onto water cells
                                float rx = dx * cell, rz = dz * cell;
                                float along = rx * ux + rz * uz;            // + downstream of the rock
                                float lat = rx * px + rz * pz;              // signed across the flow
                                float latA = Mathf.Abs(lat);

                                if (along < 0f)
                                {
                                    // PILLOW — glassy raised hump on the upstream face (stagnation ∝ v²).
                                    float u = -along, Lp = PILLOW_LEN * (0.5f + sn);
                                    if (u < Lp && latA < PILLOW_W + u * 0.4f)
                                    {
                                        float shape = (1f - u / Lp) * Mathf.Exp(-(lat * lat) / (2f * PILLOW_W * PILLOW_W));
                                        StampDist(acc, ci, PILLOW_H * sn * sn * shape, 0f, 0f, 0f);
                                    }
                                }
                                else
                                {
                                    float Lw = WAKE_LEN * (0.5f + sn);
                                    if (along < Lw)
                                    {
                                        float decay = 1f - along / Lw;
                                        // EDDYLINES — the two shear boundaries, opposite spin, foam-collecting.
                                        // Scaled by flow strength sn: a slow reach barely swirls, a fast one swirls
                                        // hard. Rapids features MUST key off real current — an unscaled full-strength
                                        // vortex on a near-still obstacle is what the shader renders as the ugly
                                        // concentric smear (user: inland flow "looks really bad, needs tuning").
                                        float shear = Mathf.Exp(-((latA - EDDYOFF) * (latA - EDDYOFF)) / (2f * EDDYSIG * EDDYSIG));
                                        StampDist(acc, ci, 0f, EDDYLINE_AER * shear * decay * sn,
                                                  Mathf.Sign(lat) * EDDY_VORT * shear * decay * sn, 0f);
                                        // EDDY POOL — calm recirculation dip near the centreline.
                                        if (latA < EDDYOFF) StampDist(acc, ci, -EDDY_DIP * decay * (1f - latA / EDDYOFF), 0f, 0f, 0f);
                                        // STANDING WAVES — world-pinned crest train on supercritical reaches.
                                        if (rapids > 0.4f && along > 1f && latA < WAVE_HALFW)
                                        {
                                            float crest = Mathf.Cos(6.2831853f * along / lam);
                                            if (crest > 0f)
                                            {
                                                float amp = Mathf.Exp(-along / (WAVE_TRAIN * lam)) * (1f - latA / WAVE_HALFW);
                                                StampDist(acc, ci, WAVE_H * rapids * crest * amp,
                                                          WAVE_CREST_AER * rapids * Mathf.Max(0f, crest - 0.6f) * amp, 0f, rapids);
                                            }
                                        }
                                    }
                                }
                            }
                        obstacles++;
                    }
                }

            if (acc.Count > 0)
            {
                // ⚠ BLUR THE HEIGHT TERM BEFORE PACKING (2026-08-18, same user report: "still pretty lumpy").
                // Every feature above is stamped per 2 m CELL with a hard footprint test (`u < Lp`,
                // `latA < PILLOW_W + …`), so a pillow's edge is a cliff between a stamped cell and its
                // unstamped neighbour — at the 1 m sheet nodes that reads as a step, not a swell. The cap
                // above bounds how TALL the bumps get; this bounds how SHARP they are. Two 5-point passes
                // over the sparse set, treating absent neighbours as 0 (calm water), so a bump tapers out
                // into the surrounding surface instead of ending on an edge.
                // ONLY `pert` is blurred: it is the term that becomes geometry. aer/vort/lock are shader
                // inputs whose authored footprints are deliberate (foam lines, eddy shear) and must stay crisp.
                var blur = new Dictionary<int, float>(acc.Count);
                for (int it = 0; it < 2; it++)
                {
                    blur.Clear();
                    foreach (var kv in acc)
                    {
                        int i = kv.Key;
                        float c = kv.Value.pert, s = c * 2f; int wsum = 2;   // centre weighted 2
                        if (acc.TryGetValue(i - 1, out var a1)) { s += a1.pert; } wsum++;
                        if (acc.TryGetValue(i + 1, out var a2)) { s += a2.pert; } wsum++;
                        if (acc.TryGetValue(i - n, out var a3)) { s += a3.pert; } wsum++;
                        if (acc.TryGetValue(i + n, out var a4)) { s += a4.pert; } wsum++;
                        blur[i] = s / wsum;
                    }
                    foreach (var kv in blur) { var d = acc[kv.Key]; d.pert = kv.Value; acc[kv.Key] = d; }
                }
                var packed = new Dictionary<int, uint>(acc.Count);
                foreach (var kv in acc) { var d = kv.Value; packed[kv.Key] = PackDist(d.pert, d.aer, d.vort, d.lok); }
                _obst = packed;
                BuildObstTileMask(packed, n);   // coarse reject — see the mask note; a writer without this reads as MISSING RAPIDS
            }
            FiresLogger.LogInfo($"[FiresWaterObst] Tier-A disturbance baked: {obstacles} mid-stream obstacle(s) · {waterfalls} waterfall base(s) · " +
                      $"{collars} collar cell(s) → {acc.Count} cell(s) resident (~{(acc.Count * 12L) / 1024} KB).");
        }

        // SEASONAL FREEZE. Still water (lake, pond, swamp) is frozen when its water is no deeper than the ice REACH, so
        // shallow shores freeze first as winter sets in and the deep centre thaws first in spring; the ice/water boundary
        // is a depth contour. The reach comes from Vedr's calendar through FatSeasons (always 0 without Vedr), so every
        // peer agrees. The one authority the chunk ice sheet, the far water and the swim test read.
        public const float MaxSeasonalIceDepthM = 4f;
        private const float StillWaterProbeM = 4f;

        public static float SeasonalIceReachM(float wx, float wz)
        {
            float reach = FatSeasons.IceReachAt(wx, wz);
            return reach > 0f && StillWaterNear(wx, wz) ? reach : 0f;
        }

        public static bool SeasonalFrozenAt(float wx, float wz)
        {
            float tx = wx, tz = wz;
            return !Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz) && (SeasonalFrozenAtReal(wx, wz) || LiftedSeaFrozenAt(tx, tz));
        }

        // A lifted chunk's sea freezes like a lake (Fire, 2026-09-27, [seasons]' popup): the same depth contour as
        // SeasonalFrozenAtReal, read at the chunk itself, so its own biome decides the winter. A dry cell beside that sea
        // is the sheet's buried skirt and freezes with it.
        private static bool LiftedSeaFrozenAt(float wx, float wz)
        {
            float reach = FatSeasons.IceReachAt(wx, wz);
            if (reach <= 0f) return false;
            if (Zones.ZoneModeGate.TryLiftedSea(wx, wz, out float level, out float floor)) return level - floor <= reach;
            return Zones.ZoneModeGate.IsLiftedSeaAt(wx + StillWaterProbeM, wz) || Zones.ZoneModeGate.IsLiftedSeaAt(wx - StillWaterProbeM, wz)
                || Zones.ZoneModeGate.IsLiftedSeaAt(wx, wz + StillWaterProbeM) || Zones.ZoneModeGate.IsLiftedSeaAt(wx, wz - StillWaterProbeM);
        }

        // A dry cell next to still water is the sheet's buried skirt: it freezes with the water it borders.
        internal static bool SeasonalFrozenAtReal(float wx, float wz)
        {
            float reach = SeasonalIceReachM(wx, wz);
            if (reach <= 0f) return false;
            return !TryGetWaterSurfaceSmoothReal(wx, wz, out _, out float depth) || depth <= reach;
        }

        // Classes live on an 8m grid, so a shore quad often lands in an unclassified block: any still-water block within
        // 4m claims it. The ice sheet's own flatness test still keeps sloped channels liquid.
        private static bool StillWaterNear(float wx, float wz)
            => IsStillWater(WaterClassAtReal(wx, wz))
            || IsStillWater(WaterClassAtReal(wx + StillWaterProbeM, wz)) || IsStillWater(WaterClassAtReal(wx - StillWaterProbeM, wz))
            || IsStillWater(WaterClassAtReal(wx, wz + StillWaterProbeM)) || IsStillWater(WaterClassAtReal(wx, wz - StillWaterProbeM));

        private static bool IsStillWater(byte cls) => cls == WC_LAKE || cls == WC_POND || cls == WC_SWAMP;

        public static bool IsWaterCorridorAt(float wx, float wz)
        {
            float tx = wx, tz = wz;
            return !Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz) && (IsWaterCorridorAtReal(wx, wz) || Zones.ZoneModeGate.IsLiftedSeaAt(tx, tz));
        }

        // Fine water meshing: may fine-resolution water spread into this cell.
        // True inside the generated water footprint — the carved corridor
        // (bed/bank/spine mask) or anything the coarse field already calls
        // wet. The 1m flood stays inside this footprint while the real crest
        // line decides the actual water edge.
        internal static bool IsWaterCorridorAtReal(float wx, float wz)
        {
            // WET cells only. The raw mask footprint covers entire valley
            // floors (the carve's over-wide bed marking) — treating it as
            // floodable re-drowns everything the depth rule just dried. The
            // fine flood's own one-coarse-cell dilation supplies the edge
            // margin.
            return TryGetGenWater(wx, wz, out _, out bool wet) && wet;
        }

    }
}
