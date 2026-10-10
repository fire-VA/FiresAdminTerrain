using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Generated water v4 — ONE pure function decides everything.
    // WetAt/SurfaceAt sample the SMOOTH (bilinear) gen water field against
    // the real heightmap: wet where smoothed depth and clearance over real
    // ground both hold. The mesh renders that function per 1m vertex; the
    // swim surface reports THE SAME function at the swimmer's position — the
    // trigger IS the mesh, nothing else. Pure function of world position →
    // adjacent zones compute identical border values (level steps at zone
    // crossings are impossible), bilinear taper → the surface dives under
    // the banks instead of holding channel level over the edge (both were
    // shipped 0.2.142 defects, screenshot-proven).
    // Zones whose heightmaps aren't in yet queue and retry (the wrap can
    // beat the vanilla heightmap spawn; caching an empty build forever was
    // the sheets-ending-mid-air / water-with-no-swim bug).
    internal static class VoxelGenWaterMesh
    {
        private const float STEP = 1f;
        private const int INTERIOR = 64;           // 64m zone, 65 verts
        // Must cover the SUM of every pass's reach, because they run in series:
        // two zones agree on a shared cell only if both derived it from an
        // identical window. Anything less and border cells resolve differently
        // on each side — which is the seam splitting the sheet.
        //
        //   rim scan   2 × RIM_SCAN  = 16   (a max pass then a min pass)
        //   relaxation RELAX_PASSES  = 12
        //   fill       FILL_PASSES   =  3
        //   wall       WALL_PASSES   =  4
        //   overshoot  EXT_RINGS     =  4
        //                            = 39   ≤ APRON
        //
        // Widening the apron costs area: G² grows as (64 + 1 + 2·APRON)², so 20
        // → 40 is ~1.9× the per-zone work and takes the published field to
        // ~84KB per loaded zone. Zones build once, so that is the right trade
        // against a permanent visible seam.
        private const int APRON = 40;
        private const int G = INTERIOR + 1 + APRON * 2;
        private const float EDGE_DROP = 0.6f;      // shoreline overshoot may bury ≤ this into a bank
        private const float SEA_GATE = 30.25f;     // ≤ this: the ocean owns it — river mouths settle to ~30 and BLEND into the sea surface. Mouth verts ride the tide in-shader (Custom/FiresLiquid weights _FiresTideOffset by vert height: full ≤30.55, 0 ≥31.5).
        private const float MIN_DEPTH = 0.25f;     // smoothed-field depth to count as water
        private const float MIN_CLEAR = 0.05f;     // level must stand above real ground
        // Flat shoreline overshoot — the sheet holds channel level INTO the bank
        // and clips through it. Widened from 2: at 2m a low camera angle could
        // still catch the rim where the bank rises gently, which reads as the
        // water stopping short of its bank. The bank-only gate below means this
        // only ever extends into RISING ground, so more rings cost nothing but
        // a little buried geometry.
        private const int EXT_RINGS = 4;

        // STAND GATE — a water surface may not stand higher above the REAL
        // ground than the generator field claims the water is DEEP (plus slack
        // for bed detail the 2m grid can't see).
        //
        // The gen grid stores a FILLED field: on dry upland its "water" value is
        // the basin spill elevation, not "no water". Wherever the live heightmap
        // sits below that grid's own ground — carved channels, erosion, terrain
        // edits — the old `lvl >= gnd + MIN_CLEAR` test passed with a level
        // metres over the real bed, which is exactly what shipped: floats=1190,
        // worst Δ12.1m, and a swim surface reading 44.0 three metres from dry
        // ground (every zone logged the same 44.0 plateau). Measuring the stand
        // against the REAL ground is what ties the sheet to its bank.
        private const float STAND_SLACK = 0.75f;
        // Absolute backstop — no generated body stands this far over its bed.
        private const float MAX_STAND = 20f;

        // THE stand test. `depth` is measured on the COARSE grid, against that
        // grid's OWN ground; `gnd` is the real fine heightmap. Wherever the fine
        // terrain cuts the channel deeper than a 10m grid can see, the raw
        // `stand > depth` form fired on genuinely submerged ground and dried it
        // — punching holes straight THROUGH rivers. va_water_scan measured the
        // damage: 829–1313 dry cells per body sitting up to 8.5m BELOW the water
        // beside them, i.e. dry gorges with a river surface at both ends.
        //
        // Inside the generator's own wet corridor a bigger-than-claimed stand
        // means deeper water, not wrong water, so the cell stays. OUTSIDE the
        // corridor the strict form still applies — that is the filled-basin
        // field spilling its spill-elevation onto dry upland, which is the
        // failure the gate was built for (floats=1190, worst Δ12.1m).
        private static bool StandOk(float wx, float wz, float lvl, float depth, float gnd)
        {
            float stand = lvl - gnd;
            if (stand > MAX_STAND) { s_dgStandKill++; return false; }
            if (stand <= depth + STAND_SLACK) return true;
            bool ok = FiresWorldGenBase.IsWaterCorridorAt(wx, wz);
            if (ok) s_dgStandRescue++; else s_dgStandKill++;
            return ok;
        }

        // ── Diagnostics ────────────────────────────────────────────────────
        // Which gate is eating the water. Every pass that can turn a wet cell
        // dry reports here, so a log line names the culprit instead of needing
        // va_water_scan run by hand at the spot. Counters are cumulative; the
        // line prints the DELTA since the last one.
        private static long s_dgZones, s_dgSeed, s_dgWet, s_dgStandKill, s_dgStandRescue;
        private static long s_dgCapClamp, s_dgBedKill, s_dgWallKill;
        private static long s_pZones, s_pSeed, s_pWet, s_pStandKill, s_pStandRescue;
        private static long s_pCapClamp, s_pBedKill, s_pWallKill;
        private static float s_nextDiag;

        private static void DiagTick()
        {
            float every = FiresVoxelConfig.WaterDiagInterval != null
                ? FiresVoxelConfig.WaterDiagInterval.Value : 0f;
            if (every <= 0f) { s_nextDiag = 0f; return; }
            if (s_nextDiag == 0f) { s_nextDiag = Time.time + every; return; }
            if (Time.time < s_nextDiag) return;
            s_nextDiag = Time.time + every;

            long zones = s_dgZones - s_pZones;
            if (zones <= 0) return;
            // Seam state every interval, not only when someone runs the command
            // by hand — a border disagreement is invisible until you happen to
            // stand on it, and it is the failure that has cost the most time.
            foreach (var sl in VoxelWaterSurvey.SeamScan())
                FiresLogger.LogInfo("[FiresVoxelWater] " + sl);

            FiresLogger.LogInfo($"[FiresVoxelWater] {zones} zone(s) built | seeded {s_dgSeed - s_pSeed} → wet {s_dgWet - s_pWet} " +
                      $"| seed-gate stand {s_dgStandKill - s_pStandKill} (corridor-rescued {s_dgStandRescue - s_pStandRescue}) " +
                      $"| rim-capped {s_dgCapClamp - s_pCapClamp} | relaxed-dry {s_dgBedKill - s_pBedKill} " +
                      $"| wall-retreat {s_dgWallKill - s_pWallKill} | pending {s_pending.Count}");
            s_pZones = s_dgZones; s_pSeed = s_dgSeed; s_pWet = s_dgWet;
            s_pStandKill = s_dgStandKill; s_pStandRescue = s_dgStandRescue;
            s_pCapClamp = s_dgCapClamp; s_pBedKill = s_dgBedKill; s_pWallKill = s_dgWallKill;

            VoxelWaterSurvey.LogAuto();
        }

        // Column relaxation — see the block in TryBuild. RIM_SCAN is the reach
        // of the containing-rim search (metres at STEP 1); it must be at least
        // half the widest channel we want to hold water, and stay ≤ APRON so
        // two zones compute the same rim for a shared cell. RELAX_PASSES bounds
        // the influence radius the same way.
        private const int RIM_SCAN = 8;
        private const int RELAX_PASSES = 12;
        private const int FILL_PASSES = 3;         // waterline notch fill reach (m)
        private const float FLOW_RATE = 0.35f;
        // A 6cm column renders as a sheet lying within centimetres of the bed —
        // coplanar enough to Z-FIGHT with the terrain, and it is the thin film
        // that makes a shoreline shimmer. Water that shallow is not worth
        // drawing; below this the cell is dry and the shoreline overshoot buries
        // the edge instead.
        private const float MIN_COLUMN = 0.25f;
        private const int WALL_PASSES = 4;         // no-standing-walls retreat reach (m)
        private const float WALL_DROP = 1.0f;      // a wet edge may overhang dry ground by at most this
        // How far UNDER the lower bank edge the surface is seated. This must
        // exceed the shader's WAVE AMPLITUDE: the mesh is displaced at render
        // time, so a level seated flush with the bank has its crests break over
        // it and the water visibly climbs the bank. Freeboard is the crest
        // budget, not a cosmetic offset. Config-driven so it can track the wave
        // settings without a rebuild.
        private static float BankFreeboard =>
            FiresVoxelConfig.WaterBankFreeboard != null
                ? FiresVoxelConfig.WaterBankFreeboard.Value : 0.45f;

        private static readonly Dictionary<Vector2s, GameObject> s_zones = new Dictionary<Vector2s, GameObject>();
        // The BUILT level field per zone (0 = dry), in the same G×G lattice the
        // mesh is generated from. The swim query reads THIS — not a re-derived
        // sample — so the surface a swimmer gets is literally the surface that
        // was meshed, leveling included.
        private static readonly Dictionary<Vector2s, float[]> s_fields = new Dictionary<Vector2s, float[]>();
        private static readonly HashSet<Vector2s> s_pending = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_retryScratch = new List<Vector2s>();
        private static Transform s_root;
        private static float s_nextRetry;

        private static readonly float[] s_out = new float[G * G];
        private static readonly float[] s_gnd = new float[G * G];
        private static readonly bool[] s_wet = new bool[G * G];
        private static readonly bool[] s_kill = new bool[G * G];
        private static readonly byte[] s_ring = new byte[G * G];
        private static readonly float[] s_lvl2 = new float[G * G];
        private static readonly bool[] s_preWet = new bool[G * G];
        private static readonly float[] s_col = new float[G * G];
        private static readonly float[] s_next = new float[G * G];
        private static readonly float[] s_cap = new float[G * G];
        private static readonly float[] s_seedCol = new float[G * G];

        // THE function. Wet + surface level at a world position, or dry.
        // Everything — mesh verts, swim level, future FX — must go through
        // this so they can never disagree.
        public static bool WetAt(float wx, float wz, out float surface)
        {
            surface = 0f;
            if (!FiresWorldGenBase.TryGetGenWaterSmooth(wx, wz, out float lvl, out float depth)) return false;
            if (depth < MIN_DEPTH || lvl <= SEA_GATE) return false;
            if (!Heightmap.GetHeight(new Vector3(wx + 0.013f, 0f, wz + 0.013f), out float gnd)) return false;
            if (lvl < gnd + MIN_CLEAR) return false;
            if (!StandOk(wx, wz, lvl, depth, gnd)) return false;   // stand gate — see StandOk
            surface = lvl;
            return true;
        }

        // Swim query — the mesh function at the swimmer's position. −10000 =
        // not in this water (vanilla's "no liquid" sentinel). Positions well
        // below the real terrain surface are caves UNDER the river: dry.
        public static float SurfaceAt(Vector3 pos)
        {
            // Read the BUILT field, not a fresh sample: the mesh is levelled
            // across the channel, so re-deriving here would hand swimmers a
            // different surface than the one they can see (the class invariant).
            var zone = ZoneSystem.GetZone(pos);
            if (!s_fields.TryGetValue(zone, out var f) || f == null) return -10000f;
            float ox = zone.x * 64f - 32f - APRON * STEP;
            float oz = zone.y * 64f - 32f - APRON * STEP;
            int gx = Mathf.RoundToInt((pos.x - ox) / STEP);
            int gz = Mathf.RoundToInt((pos.z - oz) / STEP);
            if (gx < 0 || gx >= G || gz < 0 || gz >= G) return -10000f;
            // Tolerant 3×3 lookup: at a 1m lattice a swimmer straddles wet/dry
            // cells constantly, and a strict single-cell read dropped them out of
            // the water mid-stroke. Take the highest wet neighbour instead.
            float surface = 0f;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int sx = gx + dx, sz = gz + dz;
                    if (sx < 0 || sx >= G || sz < 0 || sz >= G) continue;
                    float s = f[sz * G + sx];
                    if (s > surface) surface = s;
                }
            if (surface <= 0f) return -10000f;

            // Pure surface query — NO under-terrain special case. The ocean does
            // not ask whether you are in a cave beneath it, and every attempt to
            // make river water smarter than that is what kept ejecting swimmers
            // mid-stroke. The surface is the surface; vanilla's own swim logic
            // (via the CustomWaterManager level patches) decides the rest,
            // identically to ocean water.
            return surface;
        }

        // ── Border audit access ──────────────────────────────────────────
        // The water seam scan compares the PUBLISHED field of adjacent zones at
        // shared world positions. va_voxel_seamscan cannot see any of this — it
        // audits voxel chunk data, which is a different system with a different
        // border contract, so a water seam reports terrain-clean.
        internal const int FieldG = G;
        internal const float FieldStep = STEP;

        internal static void FieldOrigin(Vector2s zone, out float ox, out float oz)
        {
            ox = zone.x * 64f - 32f - APRON * STEP;
            oz = zone.y * 64f - 32f - APRON * STEP;
        }

        internal static float[] GetField(Vector2s zone)
            => s_fields.TryGetValue(zone, out var f) ? f : null;

        internal static List<Vector2s> FieldZones()
        {
            var l = new List<Vector2s>(s_fields.Count);
            foreach (var kv in s_fields) if (kv.Value != null) l.Add(kv.Key);
            return l;
        }

        public static void BuildForZone(Vector2s zone)
        {
            if (s_zones.ContainsKey(zone)) return;
            if (!FiresWorldGenBase.Active) return;
            if (!TryBuild(zone)) s_pending.Add(zone);
        }

        public static void DropZone(Vector2s zone)
        {
            s_pending.Remove(zone);
            s_fields.Remove(zone);
            if (!s_zones.TryGetValue(zone, out var go)) return;
            s_zones.Remove(zone);
            if (go != null) Object.Destroy(go);
        }

        // Vanilla's overworld render group flipped (interior entered or left).
        // These sheets stand in for the zone WaterSurface, which carries the
        // same group in the vanilla zone prefab.
        internal static void RefreshVisibility()
        {
            foreach (var kv in s_zones)
                if (kv.Value != null) VoxelRenderGroup.Apply(kv.Value.GetComponent<MeshRenderer>());
        }

        public static void Tick()
        {
            DiagTick();
            if (s_pending.Count == 0 || Time.time < s_nextRetry) return;
            s_nextRetry = Time.time + 1.5f;
            s_retryScratch.Clear();
            s_retryScratch.AddRange(s_pending);
            foreach (var zone in s_retryScratch)
            {
                if (s_zones.ContainsKey(zone)) { s_pending.Remove(zone); continue; }
                if (TryBuild(zone)) s_pending.Remove(zone);
            }
        }

        // One conserved-volume relaxation pass — the same arithmetic the manual
        // pour runs (WaterVolumeSettle.FlowPass). Each wet cell pushes water to
        // its lower-SURFACE 4-neighbours in proportion to the drop, capped at
        // half the column per pass for stability. Off-grid neighbours drain;
        // the apron keeps that boundary well away from the zone itself.
        private static void FlowPass(float[] col)
        {
            var next = s_next;
            System.Array.Copy(col, next, G * G);
            for (int z = 0; z < G; z++)
            {
                for (int x = 0; x < G; x++)
                {
                    int i = z * G + x;
                    float c = col[i];
                    if (c <= 0f || s_gnd[i] == float.MaxValue) continue;
                    float surf = s_gnd[i] + c;
                    float d0 = Drop(col, surf, x - 1, z, i);
                    float d1 = Drop(col, surf, x + 1, z, i);
                    float d2 = Drop(col, surf, x, z - 1, i);
                    float d3 = Drop(col, surf, x, z + 1, i);
                    float total = d0 + d1 + d2 + d3;
                    if (total <= 0f) continue;
                    float outflow = Mathf.Min(c * 0.5f, FLOW_RATE * total * 0.25f);
                    next[i] -= outflow;
                    // Every positive drop now has a real in-grid destination
                    // (Drop returns 0 off-grid), so nothing is deposited into
                    // nowhere and total volume is conserved.
                    if (d0 > 0f) Deposit(next, x - 1, z, outflow * d0 / total);
                    if (d1 > 0f) Deposit(next, x + 1, z, outflow * d1 / total);
                    if (d2 > 0f) Deposit(next, x, z - 1, outflow * d2 / total);
                    if (d3 > 0f) Deposit(next, x, z + 1, outflow * d3 / total);
                }
            }
            System.Array.Copy(next, col, G * G);
        }

        // Positive drop from `surf` to a neighbour's water surface.
        //
        // BOUNDARY = NO-FLOW (mirror), and this is the whole seam story.
        //
        // It used to treat off-grid as "our own ground, no water", i.e. a full-
        // height drop — so every edge cell dumped water out of the grid at the
        // maximum rate, every pass. That is not a reach problem, it is a
        // BOUNDARY CONDITION problem, and no apron width can fix it: zone A's
        // grid edge sits at a different world position than zone B's, so for the
        // same world cell one zone is draining and the other is not. The two can
        // never agree. Measured: 1051 wet/dry + 3448 level disagreements, worst
        // 2.14m — after widening the apron to 40, which changed nothing because
        // width was never the problem.
        //
        // Mirroring the surface makes the drop zero, so nothing leaves the grid.
        // The interior then depends only on the interior, the apron does its job,
        // and adjacent zones converge on the same answer. It also stops the
        // relaxation destroying the water it was given — the same log showed
        // 575,716 cells seeded surviving as 30,693 wet with 40,256 relaxed dry.
        private static float Drop(float[] col, float surf, int nx, int nz, int ownIdx)
        {
            if (nx < 0 || nx >= G || nz < 0 || nz >= G) return 0f;   // no-flow wall
            int ni = nz * G + nx;
            if (s_gnd[ni] == float.MaxValue) return 0f;              // no data = no flow
            float d = surf - (s_gnd[ni] + col[ni]);
            return d > 0f ? d : 0f;
        }

        private static void Deposit(float[] next, int nx, int nz, float amount)
        {
            if (nx < 0 || nx >= G || nz < 0 || nz >= G) return;
            next[nz * G + nx] += amount;
        }

        private static bool TryBuild(Vector2s zone)
        {
            float ox = zone.x * 64f - 32f - APRON * STEP;
            float oz = zone.y * 64f - 32f - APRON * STEP;

            var outY = s_out; var wet = s_wet;
            bool any = false, groundMissing = false;
            float lvMin = float.MaxValue, lvMax = float.MinValue, gndMin = float.MaxValue;
            for (int z = 0; z < G; z++)
            {
                for (int x = 0; x < G; x++)
                {
                    int i = z * G + x;
                    float wx = ox + x * STEP, wz = oz + z * STEP;
                    wet[i] = false; outY[i] = 0f; s_kill[i] = false;
                    s_gnd[i] = Heightmap.GetHeight(new Vector3(wx + 0.013f, 0f, wz + 0.013f), out float gndHere)
                        ? gndHere : float.MaxValue;
                    if (!FiresWorldGenBase.TryGetGenWaterSmooth(wx, wz, out float lvl, out float depth)) continue;
                    if (depth < MIN_DEPTH || lvl <= SEA_GATE) continue;
                    if (s_gnd[i] == float.MaxValue) { groundMissing = true; continue; }
                    if (lvl < s_gnd[i] + MIN_CLEAR) continue;
                    if (!StandOk(wx, wz, lvl, depth, s_gnd[i])) continue;   // stand gate — shared with WetAt
                    wet[i] = true;
                    outY[i] = lvl;
                    any = true;
                    s_dgSeed++;
                    if (lvl < lvMin) lvMin = lvl;
                    if (lvl > lvMax) lvMax = lvl;
                    if (s_gnd[i] < gndMin) gndMin = s_gnd[i];
                }
            }
            if (groundMissing) return false;             // heightmap not in — retry from Tick
            if (!any) { s_fields.Remove(zone); s_zones[zone] = null; return true; }

            // ── COLUMN RELAXATION ────────────────────────────────────────
            // This replaces the entire classify-then-police chain that used to
            // live here: slope gate, bank erosion, bank-referenced leveling,
            // drain pass, along-flow smoothing. Every one of them was a rule
            // approximating what water does, and each new failure needed
            // another rule to police the last one's damage.
            //
            // The manual Water Volume / Spring tools already model it properly
            // (Modules/CustomWater/Volume/WaterVolumeSettle.cs): carry a COLUMN
            // depth per cell over the REAL ground, let every cell push water to
            // its lower-SURFACE neighbours, and cap each cell at its local
            // containing rim. Water then levels across, bends downhill, fills
            // the groove and stops under the bank because that is what the
            // arithmetic does — not because a gate said so. That is exactly why
            // pouring by hand looks right where the generated field did not.
            //
            // The gen field SEEDS it — it already knows the basin level, which
            // is the long-range part — so relaxation only reconciles that seed
            // with real terrain. That work is local, so it settles well inside
            // the apron and neighbouring zones still agree on shared borders.
            var col = s_col; var cap = s_cap; var seedCol = s_seedCol;
            for (int i = 0; i < G * G; i++)
            {
                seedCol[i] = wet[i] ? Mathf.Max(0f, outY[i] - s_gnd[i]) : 0f;
                col[i] = seedCol[i];
            }

            // Local containing RIM — the highest ground within RIM_SCAN. No
            // cell may hold water above (rim − freeboard). This one term does
            // the work of the stand gate AND the bank leveling: on flat upland
            // the rim is the cell's own ground, so the cap collapses to ~0 and
            // the filled-basin field's spill elevation simply cannot stand
            // there; in a channel the rim is the bank top, so the cap is a real
            // depth. Separable max — two 1D passes instead of a full R² window.
            for (int z = 0; z < G; z++)
                for (int x = 0; x < G; x++)
                {
                    float m = float.MinValue;
                    for (int d = -RIM_SCAN; d <= RIM_SCAN; d++)
                    {
                        int nx = x + d; if (nx < 0 || nx >= G) continue;
                        float g = s_gnd[z * G + nx];
                        if (g != float.MaxValue && g > m) m = g;
                    }
                    s_lvl2[z * G + x] = m;
                }
            for (int z = 0; z < G; z++)
                for (int x = 0; x < G; x++)
                {
                    float m = float.MinValue;
                    for (int d = -RIM_SCAN; d <= RIM_SCAN; d++)
                    {
                        int nz = z + d; if (nz < 0 || nz >= G) continue;
                        float v = s_lvl2[nz * G + x];
                        if (v > m) m = v;
                    }
                    int i = z * G + x;
                    // Local rim as a SURFACE ceiling, not yet a depth.
                    cap[i] = m == float.MinValue ? float.MaxValue : m - BankFreeboard;
                }

            // ── RIM → SPILL CEILING ──────────────────────────────────────
            // The rim above is a MAX over a window, so it varies laterally:
            // near a bank the window catches the bank top and the ceiling is
            // high; mid-channel it reaches neither bank and the ceiling drops.
            // Clamping each cell to its own ceiling therefore stamps that shape
            // onto the water — bank, rise, dip over the deep middle, rise,
            // bank. The relaxation cannot undo it because the clamp is re-
            // applied every pass.
            //
            // Water does not care about the highest ground near it; it cares
            // about the LOWEST bank that would spill it, and that one value
            // governs the whole cross-section. So take a MIN of the ceiling
            // over the same reach. Max-then-min is a morphological opening: it
            // comes out laterally CONSTANT across a channel — both banks are
            // within reach from anywhere in the section, and the min picks the
            // lower one. That is "level straight across, just under the bank
            // it touches", which is the shape being asked for.
            for (int z = 0; z < G; z++)
                for (int x = 0; x < G; x++)
                {
                    float v = float.MaxValue;
                    for (int d = -RIM_SCAN; d <= RIM_SCAN; d++)
                    {
                        int nx = x + d; if (nx < 0 || nx >= G) continue;
                        float c = cap[z * G + nx];
                        if (c < v) v = c;
                    }
                    s_next[z * G + x] = v;
                }
            for (int z = 0; z < G; z++)
                for (int x = 0; x < G; x++)
                {
                    float v = float.MaxValue;
                    for (int d = -RIM_SCAN; d <= RIM_SCAN; d++)
                    {
                        int nz = z + d; if (nz < 0 || nz >= G) continue;
                        float c = s_next[nz * G + x];
                        if (c < v) v = c;
                    }
                    int i = z * G + x;
                    cap[i] = (s_gnd[i] == float.MaxValue || v == float.MaxValue)
                        ? 0f : Mathf.Max(0f, v - s_gnd[i]);
                    if (col[i] > cap[i]) { col[i] = cap[i]; s_dgCapClamp++; }
                }

            for (int pass = 0; pass < RELAX_PASSES; pass++)
            {
                FlowPass(col);
                // SUSTAINED FEED. A generated river is fed from upstream, so
                // its seed cells are a SOURCE, not a fixed volume — without
                // re-pinning them the run drains off the grid edge and the
                // channel empties (exactly what the 0.2.201 drain pass did by
                // accident). Re-pinned under the cap, so the feed can never
                // push a surface over its own rim.
                for (int i = 0; i < G * G; i++)
                {
                    float want = seedCol[i] < cap[i] ? seedCol[i] : cap[i];
                    if (col[i] < want) col[i] = want;
                    else if (col[i] > cap[i]) col[i] = cap[i];
                }
            }

            for (int i = 0; i < G * G; i++)
            {
                if (col[i] >= MIN_COLUMN && s_gnd[i] != float.MaxValue)
                { wet[i] = true; outY[i] = s_gnd[i] + col[i]; }
                else
                { if (wet[i]) s_dgBedKill++; wet[i] = false; outY[i] = 0f; }
            }

            // WATERLINE FILL. A dry cell whose ground lies UNDER the surface of
            // the water touching it is submerged — not a judgement call. Cells
            // land there constantly: a column just under MIN_COLUMN, or one the
            // rim cap trimmed, while the bed still sits below the waterline.
            // Each one leaves a notch, and the shoreline overshoot now refuses
            // to cover them (it only extends into ground AT OR ABOVE the line),
            // so the bank reads as a row of jagged empty cells. Fill them at the
            // level of the water they touch.
            //
            // Runs BEFORE the wall retreat on purpose: anything this fills that
            // is actually hanging over a drop still gets pulled back below.
            for (int pass = 0; pass < FILL_PASSES; pass++)
            {
                bool filled = false;
                for (int i = 0; i < G * G; i++) s_lvl2[i] = 0f;
                for (int z = 1; z < G - 1; z++)
                    for (int x = 1; x < G - 1; x++)
                    {
                        int i = z * G + x;
                        if (wet[i] || s_gnd[i] == float.MaxValue) continue;
                        float top = 0f;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int j = (z + dz) * G + (x + dx);
                                if (wet[j] && outY[j] > top) top = outY[j];
                            }
                        if (top > 0f && s_gnd[i] <= top - MIN_CLEAR) { s_lvl2[i] = top; filled = true; }
                    }
                if (!filled) break;
                for (int i = 0; i < G * G; i++)
                    if (s_lvl2[i] > 0f) { wet[i] = true; outY[i] = s_lvl2[i]; }
            }

            // NO STANDING WALLS. The relaxation solves a level surface, but a
            // level surface can still END IN MID-AIR: where the field stops
            // claiming water and the ground beyond falls away, the last wet
            // cell has nothing beneath its outer edge and the sheet hangs there
            // as a cliff — the river that stops dead in a floating slab. Water
            // cannot stand against nothing; it pours over.
            //
            // Retreat any wet cell whose NEVER-WET dry neighbour sits more than
            // WALL_DROP under its surface. Only never-wet neighbours count: a
            // cell that has already retreated exposes the river's OWN bed, and
            // counting that cascades the retreat straight up the channel and
            // empties it — which is exactly why the original erosion pass was
            // scoped this way before I removed it in 0.2.204. Bounded passes so
            // the reach stays inside the apron and zone borders still agree.
            for (int i = 0; i < G * G; i++) s_preWet[i] = wet[i];
            for (int pass = 0; pass < WALL_PASSES; pass++)
            {
                bool changed = false;
                for (int z = 0; z < G; z++)
                    for (int x = 0; x < G; x++)
                    {
                        int i = z * G + x;
                        if (!wet[i]) continue;
                        bool wall = false;
                        for (int dz = -1; dz <= 1 && !wall; dz++)
                            for (int dx = -1; dx <= 1 && !wall; dx++)
                            {
                                int nx = x + dx, nz = z + dz;
                                if (nx < 0 || nx >= G || nz < 0 || nz >= G) continue;
                                int j = nz * G + nx;
                                if (wet[j] || s_preWet[j] || s_gnd[j] == float.MaxValue) continue;
                                if (s_gnd[j] < outY[i] - WALL_DROP) wall = true;
                            }
                        if (wall) { s_kill[i] = true; changed = true; }
                    }
                if (!changed) break;
                for (int i = 0; i < G * G; i++)
                    if (s_kill[i]) { wet[i] = false; outY[i] = 0f; s_kill[i] = false; s_dgWallKill++; }
            }

            any = false;
            for (int i = 0; i < G * G && !any; i++) if (wet[i]) any = true;
            if (!any) { s_fields.Remove(zone); s_zones[zone] = null; return true; }


            // Shoreline: FLAT overshoot. Dry verts near wet hold the neighbor
            // water LEVEL and the sheet clips straight through the rising
            // bank — a flooded bed that visually fills its channel edge to
            // edge. (The old tuck bent the rim under its own bank, which read
            // as water stopping short of the banks.) Two rings so the clip
            // plane buries deep enough that no camera angle sees the rim.
            for (int i = 0; i < G * G; i++) s_ring[i] = 0;
            for (int pass = 1; pass <= EXT_RINGS; pass++)
            {
                for (int z = 0; z < G; z++)
                {
                    for (int x = 0; x < G; x++)
                    {
                        int i = z * G + x;
                        if (wet[i] || s_ring[i] != 0) continue;
                        float lvl = 0f;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, nz = z + dz;
                                if (nx < 0 || nx >= G || nz < 0 || nz >= G) continue;
                                int j = nz * G + nx;
                                bool src = pass == 1 ? wet[j] : s_ring[j] == pass - 1;
                                if (src && outY[j] > lvl) lvl = outY[j];
                            }
                        // Overshoot INTO a bank only — ground standing at or above
                        // the water line, which the sheet clips through. Extending
                        // over ground that FALLS AWAY leaves the sheet hanging in
                        // mid-air past the channel edge (the visible float).
                        // ONLY into ground that is AT OR ABOVE the water line.
                        // The old `>= lvl - EDGE_DROP` also extended over ground
                        // up to 0.6m BELOW the surface, which does not bury —
                        // it lays a sliver of water ON the bank, and because the
                        // test is per-cell against a bumpy shore it does so in a
                        // ragged, spiky outline. That is the jagged shard edge:
                        // not a meshing artifact, water genuinely sitting a
                        // little above the land. Requiring gnd >= lvl means every
                        // extended vertex is under the terrain by construction,
                        // so the sheet is hidden the moment it crosses the
                        // waterline and simply stops where it meets the ground.
                        if (lvl > 0f && s_gnd[i] != float.MaxValue && s_gnd[i] >= lvl)
                        { s_kill[i] = true; outY[i] = lvl; }
                    }
                }
                for (int i = 0; i < G * G; i++)
                    if (s_kill[i]) { s_ring[i] = (byte)pass; s_kill[i] = false; }
            }

            // Publish the FINAL levelled field — this is what the swim query
            // reads, so a swimmer's surface is the meshed surface by definition.
            // Wet cells only: the shoreline overshoot below is visual clip-into-
            // bank, not swimmable water.
            var field = new float[G * G];
            for (int i = 0; i < G * G; i++) { field[i] = wet[i] ? outY[i] : 0f; if (wet[i]) s_dgWet++; }
            s_fields[zone] = field;
            s_dgZones++;

            var go = new GameObject($"FiresGenWater_{zone.x}_{zone.y}");
            if (s_root == null)
            {
                var rgo = new GameObject("FiresGenWaterRoot");
                Object.DontDestroyOnLoad(rgo);
                s_root = rgo.transform;
            }
            go.transform.SetParent(s_root, false);
            go.transform.position = new Vector3(zone.x * 64f - 32f, 0f, zone.y * 64f - 32f);

            var verts = new List<Vector3>(1024);
            var tris = new List<int>(2048);
            var map = new int[G * G];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            int V(int x, int z)
            {
                int i = z * G + x;
                if (map[i] >= 0) return map[i];
                map[i] = verts.Count;
                verts.Add(new Vector3((x - APRON) * STEP, outY[i], (z - APRON) * STEP));
                return map[i];
            }
            // Float detector — a wet vert whose neighbor ground sits well
            // below the surface renders as a sheet hanging over a hole (the
            // clip-through overshoot is INTO banks; over drops it's visible
            // underside). Counted per zone; worst offender logs world coords.
            int floats = 0;
            float worstF = 0f, worstWx = 0f, worstWz = 0f;
            for (int z = APRON; z <= APRON + INTERIOR; z++)
            {
                for (int x = APRON; x <= APRON + INTERIOR; x++)
                {
                    int i = z * G + x;
                    if (!wet[i]) continue;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (nx < 0 || nx >= G || nz < 0 || nz >= G) continue;
                            int j = nz * G + nx;
                            if (wet[j] || s_ring[j] == 0 || s_gnd[j] == float.MaxValue) continue;
                            float gap = outY[i] - s_gnd[j];
                            if (gap > 1.5f)
                            {
                                floats++;
                                if (gap > worstF)
                                { worstF = gap; worstWx = ox + x * STEP; worstWz = oz + z * STEP; }
                            }
                        }
                }
            }

            int wetCells = 0;
            for (int z = APRON; z < APRON + INTERIOR; z++)
            {
                for (int x = APRON; x < APRON + INTERIOR; x++)
                {
                    int i00 = z * G + x, i10 = i00 + 1, i01 = i00 + G, i11 = i01 + 1;
                    // Emit through the first extension ring so ring-1↔ring-2
                    // quads exist — the flat overshoot spans clear into banks.
                    bool core = wet[i00] || wet[i10] || wet[i01] || wet[i11]
                             || s_ring[i00] == 1 || s_ring[i10] == 1 || s_ring[i01] == 1 || s_ring[i11] == 1;
                    if (!core) continue;
                    if (outY[i00] <= 0f || outY[i10] <= 0f || outY[i01] <= 0f || outY[i11] <= 0f) continue;
                    int a = V(x, z), b = V(x + 1, z), c = V(x, z + 1), d = V(x + 1, z + 1);
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                    wetCells++;
                }
            }
            if (tris.Count == 0) { Object.Destroy(go); s_fields.Remove(zone); s_zones[zone] = null; return true; }

            var mesh = new Mesh { name = go.name };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            var norms = new Vector3[verts.Count];
            for (int i = 0; i < norms.Length; i++) norms[i] = Vector3.up;
            mesh.normals = norms;
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var mat = Water.Core.FiresLiquidWaterPrefab.GetSharedPourMaterial();
            if (mat != null) mr.sharedMaterial = mat;
            VoxelRenderGroup.Apply(mr);

            // NO swim trigger. Swimming comes from CustomWaterManager.
            // GetWaterLevelAt reporting this water's surface into vanilla's own
            // liquid-level path — the same route the ocean and every other
            // custom water source take. The old per-zone trigger drove swim by
            // calling SetLiquidLevel each FixedUpdate, which competed with
            // vanilla's own writes and flip-flopped the player between swim and
            // walk. One writer, one source of truth.

            s_zones[zone] = go;
            FiresLogger.LogInfo($"[FiresVoxel] zone ({zone.x},{zone.y}) gen water: {wetCells} cells @1m, " +
                      $"lvl {lvMin:F1}-{lvMax:F1}, floats={floats}" +
                      (floats > 0 ? $" worst Δ{worstF:0.0}m at ({worstWx:0},{worstWz:0})" : "") +
                      ", swim=mesh function.");
            return true;
        }
    }

    // LiquidSurface-style swim driver: characters entering the trigger get
    // their liquid level from VoxelGenWaterMesh.SurfaceAt every FixedUpdate —
    // the exact function the visible mesh renders. Standing on a dry bank
    // inside the box reads −10000 (dry); wading where the mesh is reads the
    // mesh height. Swim and visuals cannot disagree.
    // Anti-flap: at the 1m shoreline lattice a wading character straddles
    // wet/dry cells every step, and raw −10000 flips bounced them between
    // walk and swim (the hop). Dry reads HOLD the last wet surface for
    // DRY_HOLD ticks before reporting dry. Players also get hip-depth swim
    // (m_swimDepth 2→1.5: vanilla enters swim at depth > m_swimDepth−0.4,
    // so ~1.1m — the hips — instead of chest/head).
    internal sealed class FiresGenWaterSurface : MonoBehaviour
    {
        private const int DRY_HOLD = 12;

        private sealed class Entry
        {
            public IWaterInteractable Wi;
            public float LastSurface = -10000f;
            public int DryTicks;
        }

        private readonly List<Entry> m_inWater = new List<Entry>();

        private void OnTriggerEnter(Collider c)
        {
            var rb = c.attachedRigidbody;
            if (rb == null) return;
            var wi = rb.GetComponent<IWaterInteractable>();
            if (wi == null) return;
            wi.Increment(LiquidType.Water);
            for (int i = 0; i < m_inWater.Count; i++)
                if (ReferenceEquals(m_inWater[i].Wi, wi)) return;
            m_inWater.Add(new Entry { Wi = wi });
        }

        private void OnTriggerExit(Collider c)
        {
            var rb = c.attachedRigidbody;
            if (rb == null) return;
            var wi = rb.GetComponent<IWaterInteractable>();
            if (wi == null) return;
            if (wi.Decrement(LiquidType.Water) == 0)
                wi.SetLiquidLevel(-10000f, LiquidType.Water, this);
            for (int i = m_inWater.Count - 1; i >= 0; i--)
                if (ReferenceEquals(m_inWater[i].Wi, wi)) m_inWater.RemoveAt(i);
        }

        private bool m_playerWasInMeshWater;

        private void FixedUpdate()
        {
            for (int i = m_inWater.Count - 1; i >= 0; i--)
            {
                var e = m_inWater[i];
                var wi = e.Wi;
                if (wi == null) { m_inWater.RemoveAt(i); continue; }
                var t = wi.GetTransform();
                if (t == null) { m_inWater.RemoveAt(i); continue; }
                float surface = VoxelGenWaterMesh.SurfaceAt(t.position);
                if (surface > -9999f)
                {
                    e.LastSurface = surface;
                    e.DryTicks = 0;
                }
                else if (e.LastSurface > -9999f && ++e.DryTicks <= DRY_HOLD)
                {
                    surface = e.LastSurface;
                }
                else
                {
                    e.LastSurface = -10000f;
                }
                wi.SetLiquidLevel(surface, LiquidType.Water, this);
                if (wi is Player pl && pl.m_swimDepth > 1.5f) pl.m_swimDepth = 1.5f;

                // Swim receipt: state CHANGES for the local player log the
                // surface the mesh function reported at their feet — the log
                // proves swim==mesh (or names the exact spot where it lied).
                if (Player.m_localPlayer != null && t == Player.m_localPlayer.transform)
                {
                    // Feet-relative and honest: the old −2f tolerance logged
                    // ENTER while the surface was still two metres BELOW the
                    // player, which made the receipt contradict itself.
                    bool inWater = surface > -9999f && surface > t.position.y;
                    if (inWater != m_playerWasInMeshWater)
                    {
                        m_playerWasInMeshWater = inWater;
                        Vector3 p = t.position;
                        FiresLogger.LogInfo($"[FiresVoxel] swim {(inWater ? "ENTER" : "EXIT")} gen water at ({p.x:0},{p.z:0}): " +
                                  $"surface={(surface > -9999f ? surface.ToString("F1") : "dry")} feet={p.y:F1}");
                    }
                }
            }
        }

        private void OnDestroy()
        {
            foreach (var e in m_inWater)
                if (e.Wi != null && e.Wi.Decrement(LiquidType.Water) == 0)
                    e.Wi.SetLiquidLevel(-10000f, LiquidType.Water, this);
            m_inWater.Clear();
        }
    }
}
