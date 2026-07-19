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
        // Must cover the widest neighbourhood any pass reads (EDGE_PASSES and
        // the bank scan) so two zones computing a shared cell see the same
        // window and agree on its level — otherwise the surface steps at zone
        // borders. Widened from 8 when bank-referenced leveling landed: the
        // scan has to reach clear across a channel to find both banks.
        private const int APRON = 20;
        private const int G = INTERIOR + 1 + APRON * 2;
        private const int EDGE_PASSES = 8;         // bank-erosion reach (m)
        private const float EDGE_DROP = 0.6f;      // water edge may stand ≤ this over dry ground
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

        // CROSS-CHANNEL LEVELING — real water is level ACROSS its channel and
        // falls only ALONG it. Sampling the gen field per vertex tilts the sheet
        // across the width (each bank pulls its own side down), which is what
        // read as the water hugging one bank and standing proud of the other.
        // Each wet vertex is averaged with its wet neighbours PERPENDICULAR to
        // the local surface gradient — i.e. across the channel — leaving the
        // along-flow fall untouched, so it still runs downhill but a line drawn
        // bank-to-bank is level. Reach (CROSS_R × CROSS_PASSES) is held ≤ APRON
        // so neighbouring zones still agree on shared border values.
        private const int CROSS_R = 4;
        private const int CROSS_PASSES = 2;
        private const int BANK_R = 18;             // how far across a channel the bank scan reaches (≤ APRON)
        // Deepest a cell may stand over its OWN bed when no bank is reachable.
        // This is the backstop for over-wide flood regions the bank scan can't
        // resolve; a real channel or pool is always banked within BANK_R.
        private const float UNBANKED_MAX_DEPTH = 2.5f;
        // How far UNDER the lower bank edge the surface is seated. This must
        // exceed the shader's WAVE AMPLITUDE: the mesh is displaced at render
        // time, so a level seated flush with the bank has its crests break over
        // it and the water visibly climbs the bank. Freeboard is the crest
        // budget, not a cosmetic offset. Config-driven so it can track the wave
        // settings without a rebuild.
        private static float BankFreeboard =>
            FiresVoxelConfig.WaterBankFreeboard != null
                ? FiresVoxelConfig.WaterBankFreeboard.Value : 0.45f;
        private const int ALONG_PASSES = 3;         // along-flow smoothing so the level bends, never steps

        private static readonly Dictionary<Vector2i, GameObject> s_zones = new Dictionary<Vector2i, GameObject>();
        // The BUILT level field per zone (0 = dry), in the same G×G lattice the
        // mesh is generated from. The swim query reads THIS — not a re-derived
        // sample — so the surface a swimmer gets is literally the surface that
        // was meshed, leveling included.
        private static readonly Dictionary<Vector2i, float[]> s_fields = new Dictionary<Vector2i, float[]>();
        private static readonly HashSet<Vector2i> s_pending = new HashSet<Vector2i>();
        private static readonly List<Vector2i> s_retryScratch = new List<Vector2i>();
        private static Transform s_root;
        private static float s_nextRetry;

        private static readonly float[] s_out = new float[G * G];
        private static readonly float[] s_gnd = new float[G * G];
        private static readonly bool[] s_wet = new bool[G * G];
        private static readonly bool[] s_preWet = new bool[G * G];
        private static readonly bool[] s_kill = new bool[G * G];
        private static readonly byte[] s_ring = new byte[G * G];
        private static readonly float[] s_lvl2 = new float[G * G];

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
            if (lvl - gnd > depth + STAND_SLACK) return false;   // stand gate — see STAND_SLACK
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

        public static void BuildForZone(Vector2i zone)
        {
            if (s_zones.ContainsKey(zone)) return;
            if (!FiresWorldGenBase.Active) return;
            if (!TryBuild(zone)) s_pending.Add(zone);
        }

        public static void DropZone(Vector2i zone)
        {
            s_pending.Remove(zone);
            s_fields.Remove(zone);
            if (!s_zones.TryGetValue(zone, out var go)) return;
            s_zones.Remove(zone);
            if (go != null) Object.Destroy(go);
        }

        public static void Tick()
        {
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

        private static bool TryBuild(Vector2i zone)
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
                    if (lvl - s_gnd[i] > depth + STAND_SLACK) continue;   // stand gate — must match WetAt exactly
                    wet[i] = true;
                    outY[i] = lvl;
                    any = true;
                    if (lvl < lvMin) lvMin = lvl;
                    if (lvl > lvMax) lvMax = lvl;
                    if (s_gnd[i] < gndMin) gndMin = s_gnd[i];
                }
            }
            if (groundMissing) return false;             // heightmap not in — retry from Tick
            if (!any) { s_fields.Remove(zone); s_zones[zone] = null; return true; }

            // SLOPE GATE — a water SURFACE steeper than ~20° is the bilinear
            // ramp between two pools draped on a hillside (screenshot-proven
            // water climbing a 45° slope), not water. Settled pools are
            // near-flat; small cascade steps (≤0.35m per meter) survive.
            for (int z = 0; z < G; z++)
            {
                for (int x = 0; x < G; x++)
                {
                    int i = z * G + x;
                    if (!wet[i]) continue;
                    bool steep = (x + 1 < G && wet[i + 1] && Mathf.Abs(outY[i + 1] - outY[i]) > 0.35f)
                              || (z + 1 < G && wet[i + G] && Mathf.Abs(outY[i + G] - outY[i]) > 0.35f);
                    if (steep) s_kill[i] = true;
                }
            }
            for (int i = 0; i < G * G; i++)
                if (s_kill[i]) { wet[i] = false; outY[i] = 0f; s_kill[i] = false; }

            // BANK EROSION — fine-scale enforcement of never-above-banks.
            // A wet vert whose NEVER-WET dry neighbor's ground sits more than
            // EDGE_DROP below the surface is a standing water wall (perched
            // rims, step faces); retreat until the edge lands on holding
            // ground. Only never-wet neighbors count — an eroded cell's
            // ground is the water body's own bed, and counting it cascaded
            // the erosion straight through entire rivers. Two-phase per pass,
            // bounded by EDGE_PASSES = APRON for zone-identical borders.
            for (int i = 0; i < G * G; i++) s_preWet[i] = wet[i];
            for (int pass = 0; pass < EDGE_PASSES; pass++)
            {
                bool changed = false;
                for (int z = 0; z < G; z++)
                {
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
                                if (s_gnd[j] < outY[i] - EDGE_DROP) wall = true;
                            }
                        if (wall) { s_kill[i] = true; changed = true; }
                    }
                }
                if (!changed) break;
                // s_preWet stays TRUE on eroded cells — they were water, and
                // treating them as fresh cliff would restart the cascade.
                for (int i = 0; i < G * G; i++)
                    if (s_kill[i]) { wet[i] = false; outY[i] = 0f; s_kill[i] = false; }
            }
            any = false;
            for (int i = 0; i < G * G && !any; i++) if (wet[i]) any = true;
            if (!any) { s_fields.Remove(zone); s_zones[zone] = null; return true; }

            // BANK-REFERENCED LEVELING. For each wet cell, scan ACROSS the
            // channel (perpendicular to the surface gradient, i.e. the flow) to
            // the first dry ground each way — those are the two bank edges — and
            // seat the surface just under the LOWER of them. Water can't stand
            // higher than the bank that would spill it, and because both sides of
            // a cross-section resolve to the same bank pair, a line drawn
            // bank-to-bank comes out level. Each section solves independently, so
            // the level still climbs and falls with the bed and banks.
            for (int i = 0; i < G * G; i++) s_lvl2[i] = outY[i];
            for (int z = 1; z < G - 1; z++)
            {
                for (int x = 1; x < G - 1; x++)
                {
                    int i = z * G + x;
                    if (!wet[i]) continue;
                    float gx = 0f, gz = 0f;
                    if (wet[i - 1] && wet[i + 1]) gx = (outY[i + 1] - outY[i - 1]) * 0.5f;
                    if (wet[i - G] && wet[i + G]) gz = (outY[i + G] - outY[i - G]) * 0.5f;
                    float m = Mathf.Sqrt(gx * gx + gz * gz);
                    // No usable gradient (still pool): fall back to the across-X
                    // axis so a flat pond still gets banked rather than skipped.
                    float px, pz;
                    if (m < 1e-5f) { px = 1f; pz = 0f; }
                    else { px = -gz / m; pz = gx / m; }

                    float bank = float.MaxValue;
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        for (int s = 1; s <= BANK_R; s++)
                        {
                            int sx = x + Mathf.RoundToInt(px * s * sgn);
                            int sz = z + Mathf.RoundToInt(pz * s * sgn);
                            if (sx < 0 || sx >= G || sz < 0 || sz >= G) break;
                            int j = sz * G + sx;
                            if (wet[j]) continue;                       // still mid-channel
                            if (s_gnd[j] == float.MaxValue) break;      // no ground data that way
                            if (s_gnd[j] < bank) bank = s_gnd[j];       // first dry ground = this bank edge
                            break;
                        }
                    }
                    if (bank < float.MaxValue)
                        s_lvl2[i] = Mathf.Min(outY[i], bank - BankFreeboard);
                    else
                        // No bank within BANK_R. Proven by va_water_scan: the wet
                        // field can flood a 320m-wide region, and every cell in
                        // its interior is further than the scan reaches, so the
                        // bank rule never applied and the raw field level stood
                        // 6m over the surrounding ground. Unbanked water is
                        // seated relative to its OWN bed instead, which bounds
                        // the stand everywhere the bank rule can't see.
                        s_lvl2[i] = Mathf.Min(outY[i], s_gnd[i] + UNBANKED_MAX_DEPTH);
                }
            }
            for (int i = 0; i < G * G; i++) if (wet[i]) outY[i] = s_lvl2[i];

            // Seating the level to the bank can drop it to/below the bed — that
            // cell simply isn't underwater any more.
            for (int i = 0; i < G * G; i++)
                if (wet[i] && outY[i] < s_gnd[i] + MIN_CLEAR) { wet[i] = false; outY[i] = 0f; }

            // ALONG-FLOW SMOOTHING. Neighbouring cross-sections can pick banks a
            // step apart; average along the flow direction so the surface bends
            // smoothly downhill instead of terracing. Across-channel levelness is
            // preserved because neighbours along the flow already share a level.
            for (int pass = 0; pass < ALONG_PASSES; pass++)
            {
                for (int i = 0; i < G * G; i++) s_lvl2[i] = outY[i];
                for (int z = 1; z < G - 1; z++)
                {
                    for (int x = 1; x < G - 1; x++)
                    {
                        int i = z * G + x;
                        if (!wet[i]) continue;
                        float sum = outY[i];
                        int n = 1;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0) continue;
                                int j = (z + dz) * G + (x + dx);
                                if (!wet[j]) continue;
                                sum += outY[j];
                                n++;
                            }
                        s_lvl2[i] = sum / n;
                    }
                }
                for (int i = 0; i < G * G; i++) if (wet[i]) outY[i] = s_lvl2[i];
            }

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
                        if (lvl > 0f && s_gnd[i] != float.MaxValue && s_gnd[i] >= lvl - EDGE_DROP)
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
            for (int i = 0; i < G * G; i++) field[i] = wet[i] ? outY[i] : 0f;
            s_fields[zone] = field;

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

            // NO swim trigger. Swimming comes from CustomWaterManager.
            // GetWaterLevelAt reporting this water's surface into vanilla's own
            // liquid-level path — the same route the ocean and every other
            // custom water source take. The old per-zone trigger drove swim by
            // calling SetLiquidLevel each FixedUpdate, which competed with
            // vanilla's own writes and flip-flopped the player between swim and
            // walk. One writer, one source of truth.

            s_zones[zone] = go;
            Debug.Log($"[FiresVoxel] zone ({zone.x},{zone.y}) gen water: {wetCells} cells @1m, " +
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
                        Debug.Log($"[FiresVoxel] swim {(inWater ? "ENTER" : "EXIT")} gen water at ({p.x:0},{p.z:0}): " +
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
