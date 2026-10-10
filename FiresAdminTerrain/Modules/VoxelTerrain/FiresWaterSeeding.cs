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
    // ═══ WATER BODY SEEDING — WHERE STILL WATER IS PUT ON THE MAP ════════════════════════════════════
    // Swamp ponds, mountain tarns, lowland lakes, highland lakes and their spillways, mountain streams.
    // This is the "stillwater first" half of the order the user set: bodies are placed and their levels
    // fixed BEFORE any river is routed, so a run can be drawn to a junction height that is already known
    // rather than reconciled afterwards. The planner proper lives in FiresWaterPlanner.cs.
    public static partial class FiresWorldGenBase
    {
        // SWAMP FLOOD — swamps are MOSTLY WATER (user doctrine): broad
        // lowland sheets with dry hummock islands dotted about, not dry land
        // with pond acne. Per swamp REGION (connected swamp zones in the
        // survey): pick the level that submerges ~70% of the region's swamp
        // ground, clamp it under the surrounding ring so it cannot spill,
        // dig shallow murk beds under the sheet, and raise coherent-noise
        // islands clear of it. Deterministic per (seed); runs every load.
        private static void SeedSwampPonds()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask; var b = _biomeMap;
            if (w == null || g == null || m == null || b == null || _survey == null) return;
            int n = _res;
            float cellM = (2f * _half) / (n - 1);
            int zr = _surveyHalfN, zn = _surveyN;
            var visited = new bool[zn * zn];
            var member = new System.Collections.Generic.List<int>(512);
            var stack2 = new Stack<int>(256);
            var heights = new System.Collections.Generic.List<float>(4096);
            var rim = new System.Collections.Generic.List<float>(2048);
            int regions = 0; long flooded = 0;
            int skTiny = 0, skFewInt = 0, skLow = 0;   // skip-gate tallies (diagnostic)
            const ushort SWAMP_BIT = 1 << 2;
            // Contained-basin shape (user, 2026-07-30): a raised outer bank that slopes GENTLY down into the
            // basin on the inside AND eases smoothly out into the existing terrain on the outside — never a
            // sharp dam. Slopes are GRADE-limited (fixed rise:run), so a bigger required raise auto-stretches
            // into a longer, more natural ramp instead of a wall.
            float FREEBOARD = WaterRules.Swamp.Freeboard;   // bank crest stands this far above the water sheet
            float IN_GRADE = WaterRules.Swamp.InGrade;      // inner slope crest→basin — gentle down into the marsh
            float OUT_GRADE = WaterRules.Swamp.OutGrade;    // outer slope crest→natural — eases into the surrounding land
            int bankCells = Mathf.CeilToInt(WaterRules.Swamp.BankReachM / cellM) + 2;   // band wide enough for the outer ramp to reach natural ground
            for (int zi0 = 0; zi0 < zn * zn; zi0++)
            {
                if (visited[zi0]) continue;
                visited[zi0] = true;
                if ((_survey[zi0].BiomeBits & SWAMP_BIT) == 0 || _survey[zi0].AltMin == float.MaxValue) continue;
                member.Clear(); heights.Clear(); rim.Clear();
                stack2.Clear(); stack2.Push(zi0);
                member.Add(zi0);
                while (stack2.Count > 0)
                {
                    int zi = stack2.Pop();
                    int zx = zi % zn, zz = zi / zn;
                    for (int e = 0; e < 4; e++)
                    {
                        int jx = zx + (e == 0 ? 1 : e == 1 ? -1 : 0);
                        int jz = zz + (e == 2 ? 1 : e == 3 ? -1 : 0);
                        if (jx < 0 || jz < 0 || jx >= zn || jz >= zn) continue;
                        int j = jz * zn + jx;
                        var sj = _survey[j];
                        bool isSwamp = (sj.BiomeBits & SWAMP_BIT) != 0 && sj.AltMin != float.MaxValue;
                        if (isSwamp && !visited[j]) { visited[j] = true; stack2.Push(j); member.Add(j); }
                    }
                }
                if (member.Count < 2) { skTiny++; continue; }   // too small to read as a marsh
                // WATER SHAPES TERRAIN — the swamp DIPS DOWN (user doctrine:
                // "large lowland areas with a large sheet of our water drawn
                // across with islands dotted about"). The old flood fitted
                // the level to the existing terrain (70th percentile minus
                // containment clamps), so any sloped region kept 70%+ of its
                // area as dry plain and the sheet huddled in the low corner
                // (screenshot: Swamp at y:65, bone dry to the horizon). Now
                // the level comes from the BORDER (low quartile of outside
                // ground + 0.8) and the interior is DUG to murk beds under
                // it, feathered from natural grade at the border over an
                // adaptive span — high ground gets a long approach, so the
                // dip-in reads as a lowland, not a quarry.
                int gxA = int.MaxValue, gzA = int.MaxValue, gxB = int.MinValue, gzB = int.MinValue;
                for (int k = 0; k < member.Count; k++)
                {
                    int zx = member[k] % zn - zr, zz = member[k] / zn - zr;
                    int gx0 = Mathf.RoundToInt((zx * 64f - 32f + _half) / cellM);
                    int gz0 = Mathf.RoundToInt((zz * 64f - 32f + _half) / cellM);
                    int gx1 = gx0 + Mathf.RoundToInt(64f / cellM);
                    int gz1 = gz0 + Mathf.RoundToInt(64f / cellM);
                    if (gx0 < gxA) gxA = gx0; if (gz0 < gzA) gzA = gz0;
                    if (gx1 > gxB) gxB = gx1; if (gz1 > gzB) gzB = gz1;
                }
                gxA = Mathf.Max(2, gxA - bankCells); gzA = Mathf.Max(2, gzA - bankCells);
                gxB = Mathf.Min(n - 3, gxB + bankCells); gzB = Mathf.Min(n - 3, gzB + bankCells);
                int W = gxB - gxA + 1, H = gzB - gzA + 1;
                if (W <= 0 || H <= 0) continue;
                // Signed distance to the swamp boundary: din = distance INTO the marsh (source = non-swamp
                // cells), dout = distance OUTSIDE it (source = swamp cells). The bank is built off both.
                var din = new float[W * H];
                var dout = new float[W * H];
                const float INF = 1e9f;
                for (int gz2 = gzA; gz2 <= gzB; gz2++)
                    for (int gx2 = gxA; gx2 <= gxB; gx2++)
                    {
                        int ci = gz2 * n + gx2;
                        int li = (gz2 - gzA) * W + (gx2 - gxA);
                        bool sw = b[ci] == 2;
                        din[li] = sw ? INF : 0f;    // non-swamp = source for the INTO-distance
                        dout[li] = sw ? 0f : INF;   // swamp = source for the OUTSIDE-distance
                        if (sw)
                        {
                            if (m[ci] == 0 && g[ci] > SEA + 0.3f) heights.Add(g[ci]);   // interior ground stat
                        }
                        // RIM = solid dry ground on the perimeter (a non-swamp neighbour of a swamp cell,
                        // river outlets excluded); the level can only stand as high as this lip.
                        else if (m[ci] == 0 && g[ci] > SEA + 0.3f
                                 && (b[ci - 1] == 2 || b[ci + 1] == 2 || b[ci - n] == 2 || b[ci + n] == 2))
                            rim.Add(g[ci]);
                    }
                if (heights.Count < 16) { skFewInt++; continue; }
                // FLOOD LEVEL = the CONTAINING RIM, not the interior. Water only
                // stands as high as the lowest lip that holds it. GEN 67 keyed
                // off the interior 60th-pct, which on a perched swamp sits ABOVE
                // the rim — the sheet was written standing over its own banks,
                // and the bank-enforcement fixpoint then DRAINED every such
                // swamp back to mud (audit: 221k above-bank violations; dry
                // y:130 screenshots). The 20th-pct rim sits just under the bulk
                // of the perimeter but above the single drainage outlet (which
                // becomes the outflow stream), so the sheet is CONTAINED and
                // survives. The interior is then carved BELOW it (aggressively,
                // see the dig) so the biome floods biome-wide. Rim-starved
                // swamps that fill their whole bbox fall back to a low interior
                // percentile.
                float lvl;
                if (rim.Count >= 8) { rim.Sort(); lvl = rim[(int)(rim.Count * FiresGenTuning.SwampLevelPct)]; }
                else { heights.Sort(); lvl = heights[(int)(heights.Count * 0.25f)]; }
                // COASTAL vs INLAND (user, 2026-07-30): a marsh whose containing level sits within a few
                // metres of the sea DIGS its basin BELOW sea level and lets the OCEAN fill it — no berm, no
                // separate sheet (the ocean IS the water). Higher inland marshes get their own contained
                // basin: a raised bank + flat inland sheet. Never berm the ocean side of a coastal marsh.
                bool coastal = lvl <= SEA + WaterRules.Swamp.CoastalCutoff;
                Chamfer2(din, W, H); Chamfer2(dout, W, H);
                {
                    // REGION SUITABILITY — a marsh only forms where the terrain actually
                    // offers a near-level floor. A region whose swamp ground is mostly
                    // OUTSIDE the level band (steep/broken country) is SKIPPED outright:
                    // shaping it anyway scattered pockets against cliffs, and the water
                    // reconcile's dry-margin feather then hung 16m sheets over the drops
                    // (audit: WL 44 over H 28 — the in-game floating shards). Smaller or
                    // NO marsh beats a wrong one (user).
                    float sl0 = lvl <= SEA + 0.6f ? SEA : lvl;
                    int bandN = 0, bioN = 0;
                    for (int gz2 = gzA; gz2 <= gzB; gz2++)
                        for (int gx2 = gxA; gx2 <= gxB; gx2++)
                        {
                            int ci = gz2 * n + gx2;
                            if (b[ci] != 2) continue;
                            bioN++;
                            float gv2 = g[ci];
                            if (gv2 <= sl0 + 2.0f && gv2 >= sl0 - 2.5f) bandN++;
                        }
                    if (bioN == 0 || bandN < bioN * 0.35f) { skFewInt++; continue; }
                }
                regions++; if (coastal) skLow++;
                float crest = lvl + FREEBOARD;
                // ═══ THE MARSH LAW (clean rewrite — user: "scrap it and get it right") ═══
                // One rule set, no branches-of-branches:
                //   • the marsh occupies only ground NEAR its level — unsuitable terrain
                //     makes the marsh SMALLER, never forces a bowl/mesa onto it;
                //   • the bed is ONE smooth field a little under the sheet, with WIDE low
                //     tussock domes that clearly breach it (walkable, consistent mounds);
                //   • HYSTERESIS: no cell may sit AT the waterline — decisively wet or
                //     decisively dry. The ambiguous band is what rendered the sheet as
                //     angular per-cell confetti shards (user screenshots, seed 318699305);
                //   • coastal regions lay the same shape at SEA level and the ocean owns
                //     the water; inland regions flood exactly lvl. Rivers/spillways still
                //     stop at the region mouth (the incision/stamper guards).
                // SEA-LAID only when the level genuinely sits AT the sea. The old
                // coastal cutoff (lvl ≤ SEA+4) also caught ENCLOSED near-sea basins —
                // laid at SEA they became sub-sea bowls the basin walk re-flooded up
                // to their rims (~+3m) in sloped caps (audit: scattered 33.x levels).
                // Those are ordinary inland marshes with their own flat sheet.
                bool seaLaid = lvl <= SEA + 0.6f;
                float shapeLvl = seaLaid ? SEA : lvl;
                for (int gz2 = gzA; gz2 <= gzB; gz2++)
                    for (int gx2 = gxA; gx2 <= gxB; gx2++)
                    {
                        int ci = gz2 * n + gx2;
                        int li = (gz2 - gzA) * W + (gx2 - gxA);
                        float gv = g[ci];
                        float wxs = gx2 * cellM - _half, wzs = gz2 * cellM - _half;
                        if (b[ci] == 2)
                        {
                            if (gv > shapeLvl + 2.0f || gv < shapeLvl - 2.5f) continue;   // unsuitable ground — the marsh shrinks around it
                            // FLATNESS — the 4m surroundings must ALSO sit near the level. A band
                            // cell on a cliff LIP passed the gv gate, and the pocket it anchored fed
                            // the reconcile's dry-margin feather a sheet over the drop beside it.
                            // No marsh within 2 cells of ground far off the level.
                            if (gx2 < 2 || gx2 >= n - 2 || gz2 < 2 || gz2 >= n - 2) continue;
                            if (g[ci - 2] < shapeLvl - 3.5f || g[ci + 2] < shapeLvl - 3.5f
                                || g[ci - 2 * n] < shapeLvl - 3.5f || g[ci + 2 * n] < shapeLvl - 3.5f
                                || g[ci - 2] > shapeLvl + 3f || g[ci + 2] > shapeLvl + 3f
                                || g[ci - 2 * n] > shapeLvl + 3f || g[ci + 2 * n] > shapeLvl + 3f) continue;
                            float edgeT = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(din[li] / 10f));   // 0 at the marsh edge → 1 inside
                            float bed = shapeLvl - (0.5f + 1.3f * Fbm(wxs, wzs, 1f / 42f, 2, _seed + 77));   // smooth murk, 0.5..1.8 under
                            float mt = Smooth01Glsl(0.58f, 0.72f, Fbm(wxs, wzs, 1f / 26f, 2, _seed + 177));   // (v102) GLSL semantics — Unity SmoothStep diverged (see FiresTerrain3D.Smooth01); this gate now computes what the studio always showed
                            bed = Mathf.Lerp(bed, shapeLvl + 0.8f, mt);                   // wide low domes rise THROUGH the sheet
                            bed = Mathf.Lerp(gv, bed, edgeT);                             // blend from natural ground at the rim
                            // hysteresis — never AT the waterline, and wet runs DEEP enough
                            // (≥0.65m) that the basin walk's 0.45m depth-film can never dry a
                            // marsh cell back into per-cell confetti.
                            if (bed > shapeLvl - 0.6f && bed < shapeLvl + 0.25f)
                                bed = mt > 0.5f ? shapeLvl + 0.3f : shapeLvl - 0.65f;
                            g[ci] = bed; flooded++;
                            if (seaLaid)
                            {
                                // sea owns the basin — and kill the stale dry-fill w (= the PRE-dig
                                // terrain height), which otherwise minted a phantom inland sheet
                                // OVER the ocean plane (the y30 double layer).
                                if (w[ci] > g[ci] + 0.01f) w[ci] = g[ci] < SEA ? SEA : g[ci];
                            }
                            else
                            {
                                // ONE sheet at exactly lvl on wet cells; dry cells cleared of stale w.
                                w[ci] = bed < lvl - 0.5f ? lvl : Mathf.Min(w[ci], g[ci]);
                            }
                        }
                        else if (!seaLaid && gv > SEA + 0.3f)   // outer bank ONLY for inland marshes (sea-laid uses the sea)
                        {
                            float berm = crest - OUT_GRADE * dout[li];           // gentle out-ramp from the crest to natural terrain
                            if (berm > g[ci]) g[ci] = berm;                      // RAISE ONLY — where natural already stands higher, untouched → blends in
                        }
                    }
                // Register the sheet level across the region so the basin walk keeps the marsh at it —
                // INLAND marshes ONLY. A coastal marsh has no inland sheet (the OCEAN fills its dug basin);
                // registering a level would make NormalizeBasinWater flood it to ~lvl and perch that water
                // over the tussocks — the coastal class-4/biome-2 float. Leave coastal marshes to the sea.
                // RIM SEAL — the containment guarantee, cell-exact. The berm ramp covers the
                // rect's reach band, but sheet cells at ragged biome edges could still sit
                // beside a DRY outside cell LOWER than the level (numeric audit: 960 cells,
                // worst +0.61m — reads in-game as the sheet hanging past a bank / not meeting
                // it). Any dry 4-neighbour of a wet marsh cell below lvl rises to lvl+0.3:
                // raise-only, one cell wide, inland only (a coastal basin is the sea's).
                if (!seaLaid)
                {
                    // CONTAIN BY RETREAT, then SEAL. A sheet cell bordering a dry drop the
                    // level cannot sit against (>2m below lvl — footprint edges on cliffs,
                    // near-sea slopes the berm never touches: the 16m bank-riding cells in
                    // the audit) is NOT walled in (user: never raise surrounding high
                    // ground) — the marsh RETREATS from it, iterated until the shoreline
                    // settles on defensible ground. Then the surviving shoreline's shallow
                    // dry dips are sealed just above the level. Smaller marsh > wrong marsh.
                    for (int pass = 0; pass < 3; pass++)
                    {
                        int retreated = 0;
                        for (int gz2 = Mathf.Max(1, gzA - 1); gz2 <= Mathf.Min(n - 2, gzB + 1); gz2++)
                            for (int gx2 = Mathf.Max(1, gxA - 1); gx2 <= Mathf.Min(n - 2, gxB + 1); gx2++)
                            {
                                int ci = gz2 * n + gx2;
                                if (b[ci] != 2 || w[ci] <= g[ci] + 0.01f) continue;
                                for (int e = 0; e < 4; e++)
                                {
                                    int j = ci + (e == 0 ? 1 : e == 1 ? -1 : e == 2 ? n : -n);
                                    if (w[j] > g[j] + 0.01f) continue;
                                    if (g[j] < lvl - 2.0f) { w[ci] = Mathf.Min(w[ci], g[ci]); retreated++; break; }
                                }
                            }
                        if (retreated == 0) break;
                    }
                    for (int gz2 = Mathf.Max(1, gzA - 1); gz2 <= Mathf.Min(n - 2, gzB + 1); gz2++)
                        for (int gx2 = Mathf.Max(1, gxA - 1); gx2 <= Mathf.Min(n - 2, gxB + 1); gx2++)
                        {
                            int ci = gz2 * n + gx2;
                            if (b[ci] != 2 || w[ci] <= g[ci] + 0.01f) continue;
                            for (int e = 0; e < 4; e++)
                            {
                                int j = ci + (e == 0 ? 1 : e == 1 ? -1 : e == 2 ? n : -n);
                                if (w[j] > g[j] + 0.01f) continue;
                                if (g[j] < lvl + 0.05f) g[j] = lvl + 0.3f;   // shallow dip on the surviving shoreline → sealed
                            }
                        }
                }
                for (int k = 0; k < member.Count; k++)
                {
                    int zx = member[k] % zn - zr, zz = member[k] / zn - zr;
                    int gx0 = Mathf.RoundToInt((zx * 64f - 32f + _half) / cellM);
                    int gz0 = Mathf.RoundToInt((zz * 64f - 32f + _half) / cellM);
                    for (int qz = 0; qz < 4; qz++)
                        for (int qx = 0; qx < 4; qx++)
                        {
                            long key = SeedKey(gx0 + qx * 8 + 4, gz0 + qz * 8 + 4);
                            s_marshZones.Add(key);                  // footprint: EVERY region (sea-laid too) — the carve guards read this
                            if (!seaLaid) s_seedLevels[key] = lvl;  // LEVEL: inland marshes — the walk holds their flat sheet at it
                        }
                }
            }
            s_repSwampSeeds = regions;
            if (regions > 0 || skTiny + skFewInt + skLow > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] swamp basins: {regions} region(s) ({skLow} coastal→dug below sea/ocean-filled, {regions - skLow} inland→raised-bank basin), crest +{FREEBOARD:0.0}m, inner {IN_GRADE:0.00}/outer {OUT_GRADE:0.00} grade, 50/50 island/murk, {flooded:N0} cells wet. Skipped: tiny={skTiny} fewInterior={skFewInt}.");
        }

        // Two-pass chamfer distance transform (metres) over a W×H field: cells at 0 are sources, INF are
        // targets to be filled with their distance to the nearest source (3-4 chamfer, ~2m cells).
        private static void Chamfer2(float[] d, int W, int H)
        {
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int li = z * W + x; float dc = d[li];
                    if (dc <= 0f) continue;
                    if (x > 0) { float c = d[li - 1] + 2f; if (c < dc) dc = c; }
                    if (z > 0)
                    {
                        float c = d[li - W] + 2f; if (c < dc) dc = c;
                        if (x > 0) { c = d[li - W - 1] + 2.83f; if (c < dc) dc = c; }
                        if (x + 1 < W) { c = d[li - W + 1] + 2.83f; if (c < dc) dc = c; }
                    }
                    d[li] = dc;
                }
            for (int z = H - 1; z >= 0; z--)
                for (int x = W - 1; x >= 0; x--)
                {
                    int li = z * W + x; float dc = d[li];
                    if (dc <= 0f) continue;
                    if (x + 1 < W) { float c = d[li + 1] + 2f; if (c < dc) dc = c; }
                    if (z + 1 < H)
                    {
                        float c = d[li + W] + 2f; if (c < dc) dc = c;
                        if (x + 1 < W) { c = d[li + W + 1] + 2.83f; if (c < dc) dc = c; }
                        if (x > 0) { c = d[li + W - 1] + 2.83f; if (c < dc) dc = c; }
                    }
                    d[li] = dc;
                }
        }






        // LOWLAND LAKES & PONDS — the river rework dries every natural basin
        // (deliberate placement owns still water), and the only placers were
        // swamp ponds and mountain tarns: temperate biomes had NOTHING to
        // classify (fresh-world report: lake 0, pond 0 map-wide). Same
        // dig-in + berm construction as the tarns, seeded across meadows /
        // black forest / plains / mistlands — mostly ponds, ~1 in 8 a true
        // lake. The walk floods, flattens, excavates and classifies them
        // LAKE/POND by area at the centroid biome.
        // Deterministic zone-key hash order for candidate draws.
        private struct LakeCandidate { public int Zx, Zz; public float Order; }

        private static void SeedLowlandLakes()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask; var b = _biomeMap;
            if (w == null || g == null || m == null || b == null || _survey == null) return;
            int n = _res;
            float cellM = (2f * _half) / (n - 1);
            int seeded = 0, lakesN = 0;
            // LPA-STYLE: filter the survey into per-biome CANDIDATE pools,
            // then draw a QUOTA in deterministic hash order with spacing —
            // exact counts and shortfall accounting, not lattice chance.
            byte[] poolBiomes = { 0, 1, 4, 5 };                    // Meadows/BF/Plains/Rainforest
            int[] zonesPerLake = { 55, 62, 72, 84 };               // ≈ prior densities
            var occupied = new System.Collections.Generic.HashSet<long>();
            var cands = new System.Collections.Generic.List<LakeCandidate>(4096);
            var quota = new int[poolBiomes.Length];
            var placedB = new int[poolBiomes.Length];
            int zr = _surveyHalfN;
            for (int pb = 0; pb < poolBiomes.Length; pb++)
            {
                cands.Clear();
                ushort bit = (ushort)(1 << poolBiomes[pb]);
                for (int zz = -zr; zz <= zr; zz++)
                    for (int zx = -zr; zx <= zr; zx++)
                    {
                        if (!TryGetSurvey(zx, zz, out var s)) continue;
                        if ((s.BiomeBits & bit) == 0) continue;
                        if (s.AltMin <= SEA + 1f) continue;        // touching sea level
                        if (s.HasRiver) continue;                  // channel owns it
                        if (s.MeanSlope > WaterRules.Lake.SiteSlopeMax) continue;         // cliffs make bad lake sites
                        cands.Add(new LakeCandidate
                        {
                            Zx = zx, Zz = zz,
                            Order = WanderHash01(zx, zz, _seed ^ (0x4A40 + pb))
                        });
                    }
                quota[pb] = cands.Count == 0 ? 0 : Mathf.Max(1, cands.Count / zonesPerLake[pb]);
                cands.Sort((a2, b2) => a2.Order.CompareTo(b2.Order));
                for (int k = 0; k < cands.Count && placedB[pb] < quota[pb]; k++)
                {
                    int zx = cands[k].Zx, zz = cands[k].Zz;
                    // spacing: no lake in a zone adjacent to an already-drawn one
                    bool near = false;
                    for (int dz2 = -1; dz2 <= 1 && !near; dz2++)
                        for (int dx2 = -1; dx2 <= 1; dx2++)
                            if (occupied.Contains(((long)(zx + dx2) << 32) ^ (uint)(zz + dz2))) { near = true; break; }
                    if (near) continue;
                    // site: zone centre + deterministic jitter
                    float wxs = zx * 64f + (WanderHash01(zx, zz, _seed ^ 0x4A31) - 0.5f) * 48f;
                    float wzs = zz * 64f + (WanderHash01(zx, zz, _seed ^ 0x4A32) - 0.5f) * 48f;
                    int px = Mathf.RoundToInt((wxs + _half) / cellM);
                    int pz = Mathf.RoundToInt((wzs + _half) / cellM);
                    if (px < 4 || pz < 4 || px >= n - 4 || pz >= n - 4) continue;
                    if (TrySeedLakeSite(px, pz, ref seeded, ref lakesN))
                    {
                        occupied.Add(((long)zx << 32) ^ (uint)zz);
                        placedB[pb]++;
                    }
                }
            }
            if (seeded > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] lowland lakes (quota placement): {seeded} placed ({lakesN} lake-size) — " +
                          $"Meadows {placedB[0]}/{quota[0]}, Forest {placedB[1]}/{quota[1]}, " +
                          $"Plains {placedB[2]}/{quota[2]}, Rainforest {placedB[3]}/{quota[3]}.");
        }

        // One lowland lake/pond site — the dig-in + berm construction.
        // Returns false when per-site ground checks reject (caller draws the
        // next candidate, so quotas still fill).
        private static bool TrySeedLakeSite(int px, int pz, ref int seeded, ref int lakesN)
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask; var b = _biomeMap;
            int n = _res;
            float cellM = (2f * _half) / (n - 1);
            {
                    int ci = pz * n + px;
                    switch (b[ci])
                    {
                        case 0: case 1: case 4: case 5: break;
                        default: return false;           // jitter drifted out of pool biomes
                    }
                    if (m[ci] != 0) return false;
                    float g0 = g[ci];
                    if (g0 <= SEA + 1f) return false;
                    if (w[ci] > g0 + 0.01f) return false;
                    bool big = WanderHash01(px, pz, _seed ^ 0x4A33) < WaterRules.Lake.BigChance;
                    float R = big ? WaterRules.Lake.BigMinRadius + WanderHash01(px, pz, _seed ^ 0x4A34) * WaterRules.Lake.BigRadiusRange
                                  : WaterRules.Lake.PondMinRadius + WanderHash01(px, pz, _seed ^ 0x4A34) * WaterRules.Lake.PondRadiusRange;
                    int rc = Mathf.CeilToInt(R / cellM);
                    int rcFill = rc + Mathf.CeilToInt(40f / cellM);   // 0.32 wedge needs ~12m drop coverage
                    if (px < rcFill || pz < rcFill || px >= n - rcFill || pz >= n - rcFill) return false;
                    float ringMin = float.MaxValue, ringMax = float.MinValue, innerMin = float.MaxValue;
                    bool blocked = false;
                    for (int dz = -rcFill; dz <= rcFill && !blocked; dz++)
                        for (int dx = -rcFill; dx <= rcFill; dx++)
                        {
                            float rm2 = Mathf.Sqrt(dx * dx + dz * dz) * cellM;
                            if (rm2 >= R + 2.5f) continue;
                            int i2 = (pz + dz) * n + (px + dx);
                            if (m[i2] != 0 || w[i2] > g[i2] + 0.01f || b[i2] == 2) { blocked = true; break; }   // don't overlap a river or a MARSH (marsh owns its basin)
                            if (rm2 >= R)
                            {
                                if (g[i2] < ringMin) ringMin = g[i2];
                                if (g[i2] > ringMax) ringMax = g[i2];
                            }
                            else if (g[i2] < innerMin) innerMin = g[i2];   // natural floor of the hollow
                        }
                    if (blocked || ringMin == float.MaxValue || innerMin == float.MaxValue) return false;
                    if (ringMax - ringMin > WaterRules.Lake.RingRejectRelief) return false;   // cliff-straddling site would quarry a crater
                    // NATURAL BASIN ONLY (user: "we cannot place a lake where a lake doesn't belong").
                    // The site must ALREADY be a closed hollow — its natural floor below the LOWEST
                    // surrounding bank — so water would pool here on its own. A flat / convex / sloping
                    // spot (floor at or above its lowest rim → water drains out) is rejected; we no longer
                    // quarry a crater into dry ground just to hit a biome quota.
                    if (innerMin > ringMin - WaterRules.Lake.BasinGate) return false;
                    // STRICTLY below the lowest bank (user: "drops a bit below
                    // the lowest bank and levels perfectly"). The old relief
                    // bonus could push the level ABOVE ringMin on a high-relief
                    // ring — that lowest bank then floated. −0.35 guarantees
                    // every bank stands proud; the flatten levels it perfectly.
                    float lvl = ringMin - WaterRules.Lake.LevelDrop;
                    if (lvl <= SEA + 0.6f) return false;
                    var st = WaterRules.WaterStyleFor(b[ci]);
                    for (int dz = -rcFill; dz <= rcFill; dz++)
                        for (int dx = -rcFill; dx <= rcFill; dx++)
                        {
                            float rm2 = Mathf.Sqrt(dx * dx + dz * dz) * cellM;
                            int i2 = (pz + dz) * n + (px + dx);
                            if (rm2 < R)
                            {
                                float rr = rm2 / R;
                                float bed = lvl - (WaterRules.Lake.BaseDepth + (big ? st.LakeDepth : st.PondDepth) * (1f - rr * rr));
                                if (g[i2] > bed) g[i2] = bed;
                                if (w[i2] < lvl) w[i2] = lvl;
                            }
                            else if (m[i2] == 0 && w[i2] <= g[i2] + 0.01f)
                            {
                                float wedge = lvl + st.BermH - WaterRules.Lake.WedgeGrade * Mathf.Max(0f, rm2 - R - 3f);
                                if (g[i2] < wedge) g[i2] = wedge;
                            }
                        }
                    s_seedLevels[SeedKey(px, pz)] = lvl;
                    seeded++;
                    if (big) lakesN++;
                    return true;
            }
        }

        // HEADWATER STREAMS — mountains were riverless because traced
        // channels only began at big-catchment thresholds. WE own the
        // terrain: sources seed high on the ranges and each stream is CARVED
        // downhill — steepest 4-way descent with a meander bias so it
        // wanders and switchbacks — banks cut into grade, the low side
        // bermed, width growing downstream, until it meets existing water,
        // the sea, or sinks in a pit. Stream cells join the river MASK:
        // sloped flow, no settle-flatten, RIVER class, shore paint. A stream
        // that reaches a STILL lake stops two cells short (spring-gap) so
        // the walk keeps seeing a closed basin.
        // ONE channel stamp for every traced brook/stream: parabolic bed
        // across the width, banks CUT into rising ground, bounded low-side
        // berm. The outlet brooks used to dig only the path's CENTER cell —
        // on the 8-dir descent consecutive cells touch at CORNERS, so the
        // "brook" was a beaded chain of single-cell pits with berm lips
        // (user screenshots: dotted trench lines that are obviously not
        // rivers). A disk stamp per step overlaps across diagonal moves and
        // the channel is continuous by construction. skipWetAbove: don't
        // wet cells 4-adjacent to standing water above the brook (keeps a
        // spillway brook from 4-connecting its source lake).
        // Marsh-footprint test for the channel stampers — a seeded INLAND marsh region
        // (its zone blocks are registered in s_seedLevels by SeedSwampPonds) owns its
        // ground: spillways / mountain streams / lake outlet brooks stop at the region
        // mouth exactly like the incised rivers do (user: "rivers stop at the mouth of
        // the swamp and continue from the other side"). The stampers' wet-skip already
        // protects the SHEET; this stops the groove they cut across the DRY mound
        // corridors between pools. Swamp-biome-scoped — lakes/rivers/ocean untouched.
        private static bool IsSeededMarshCell(int i)
        {
            var b = _biomeMap;
            if (b == null || b[i] != 2) return false;
            int n = _res;
            return s_marshZones.Contains(SeedKey(i % n, i / n));
        }

        // ═══ THE CHANNEL CROSS-SECTION — ONE CURVE, EVERY CHANNEL (2026-08-13) ═══════════════════════════
        // UE's water plugin keeps the river's cross-section in a single Elevation Curve asset: normalized 0-1
        // on both axes, Y multiplied by each spline point's Depth, one default S-curve shared by every river.
        // We had TWO different sections in two code paths that had drifted apart at the one place it matters —
        // the rim:
        //   • InciseRivers      BiasedRational, meets the level EXACTLY at the wetted edge (shape(1) = 0), so
        //                       the sheet tapers to zero thickness and there is no edge to see;
        //   • StampChannelCell  1 − 0.65t², which at t=1 leaves the bed 0.35·depth BELOW the level while still
        //                       laying water at lvl — a 0.23-0.63m vertical lip of exposed mesh right at the
        //                       disc boundary, on lake outlets and spillways.
        // This is that one curve. bed = level − depth · ChannelShape(t), with:
        //   shape(0) = 1   full depth at the thalweg
        //   shape(1) = 0   bed rises to MEET the surface at the wetted edge — docs/HYDROLOGY.md Law 5, and the
        //                  reason the T-section fills bank-to-bank by construction rather than by luck
        //   shape(t>1) < 0 keeps climbing into the natural banks (NOT clamped — InciseRivers evaluates out to
        //                  2·half for the berm ring and needs the rise; clamping to 0 would flatten the banks)
        // `aspect` is half/depth (wide-shallow vs narrow-deep sections differ, which is why this is a family
        // and not a single fixed table) and `steep` is the asymptotic bank slope.
        public static float ChannelShape(float t, float aspect, float steep)
        {
            if (t <= 0f) return 1f;
            float nrm = t * aspect;
            float wBias = Math.Max(0.001f, aspect * (steep * aspect - 1f));
            return 1f - (steep * nrm * nrm) / (nrm + wBias);
        }

        // VALLEY FALLOFF GRADE for a channel of this width (m/m) — see WaterRules.RiverProc.InclineNarrow/Wide.
        // The blend distance is its own quantity, not a restatement of the water width: a brook gets tight steep
        // banks, a trunk gets a broad floodplain. Inverse in width because valley half-width scales WITH width,
        // so the wall grade (relief / half-width) scales against it.
        public static float InclineFor(float width)
            => RemapClamp(width, WaterRules.RiverProc.WidthMin, WaterRules.RiverProc.WidthMax,
                                 WaterRules.RiverProc.InclineNarrow, WaterRules.RiverProc.InclineWide);

        private static int StampChannelCell(int i, float lvl, float depth, int hw, float skipWetAbove)
        {
            if (IsSeededMarshCell(i)) return 0;   // marsh owns its ground — the channel resumes past the region
            var w = _waterLevel; var g = _grid; var m = _riverMask;
            int n = _res;
            float cellM = (2f * _half) / (n - 1);
            float bedR = hw + 0.4f;
            // Cross-section shared with InciseRivers (see ChannelShape). bedR is in CELLS, depth in metres, so
            // the aspect ratio needs the half-width in metres. `steep` is fixed here rather than ramped off the
            // ground the way the incision does it: this stamp runs DURING seeding, when the grid it would sample
            // is still being mutated by other passes, so a live-ground ramp would make the section depend on
            // pass order — exactly the coupling this work is removing. A mid-range constant is the honest choice.
            const float STAMP_STEEP = 3.2f;
            float chanAspect = Math.Max(0.05f, (bedR * cellM) / Math.Max(0.05f, depth));
            int stamped = 0;
            for (int dz = -hw - 2; dz <= hw + 2; dz++)
                for (int dx = -hw - 2; dx <= hw + 2; dx++)
                {
                    int j = i + dz * n + dx;
                    float rc = Mathf.Sqrt(dx * dx + dz * dz);
                    // mask-2 bench margins are CARVABLE (the stream cuts its
                    // bed through the river's dry margin at a confluence);
                    // only real channel cells and standing water are owned.
                    if (m[j] == 1 || m[j] == 3 || w[j] > g[j] + 0.01f)
                    {
                        // OWN-CHANNEL RE-LEVEL. Overlapping upstream disks
                        // wet the flank cells FIRST at their higher level and
                        // the wet-skip locked them there — the sheet rendered
                        // as a U-trough hugging the cutout with lifted edges
                        // instead of FLAT ACROSS THE SECTION flowing cleanly
                        // downhill (user). Water only ever steps DOWN: own
                        // channel cells take the minimum level and the bed
                        // re-digs to match. A river at the junction sits
                        // BELOW the arriving stream, so w > lvl is false
                        // there and river cells stay untouched.
                        if (rc <= bedR && m[j] == 1 && w[j] > lvl)
                        {
                            float bed2 = lvl - depth * ChannelShape(rc / bedR, chanAspect, STAMP_STEEP);
                            if (g[j] > bed2) g[j] = Mathf.Max(bed2, g[j] - 4f);
                            w[j] = lvl;
                        }
                        continue;
                    }
                    if (rc <= bedR)
                    {
                        // ONE CURVE (2026-08-13): was `1 − 0.65t²`, which at the rim left the bed 0.35·depth
                        // under the level while line 3992 below still laid water at lvl — so every outlet and
                        // spillway disc ended in a vertical lip of exposed mesh 0.23-0.63m tall. ChannelShape
                        // reaches 0 at t=1, so the bed rises to MEET the surface and the sheet tapers to nothing.
                        float bed = lvl - depth * ChannelShape(rc / bedR, chanAspect, STAMP_STEEP);
                        // BOUNDED: never remove more than 4m from any cell —
                        // a channel through a knob notches a gorge through
                        // it instead of atomizing it, and a path hugging a
                        // cliff can't quarry the wall.
                        if (g[j] > bed) g[j] = Mathf.Max(bed, g[j] - 4f);
                        if (skipWetAbove > 0f
                            && (w[j + 1] > g[j + 1] + 0.01f && w[j + 1] > skipWetAbove
                             || w[j - 1] > g[j - 1] + 0.01f && w[j - 1] > skipWetAbove
                             || w[j + n] > g[j + n] + 0.01f && w[j + n] > skipWetAbove
                             || w[j - n] > g[j - n] + 0.01f && w[j - n] > skipWetAbove))
                            continue;   // dug but dry: the junction margin
                        if (w[j] < lvl && g[j] < lvl) w[j] = lvl;
                        m[j] = 1; stamped++;
                    }
                    else if (g[j] > lvl + 0.35f)
                    {
                        // BANK CUT: rising ground shears down to a 0.85 m/m
                        // bank from the bed edge — the channel is carved
                        // INTO the hillside with a visible bank. BOUNDED to
                        // 2.5m of removal: a stream skirting a rock face
                        // notches a shelf, never a pit wall.
                        float bankCap = lvl + 0.35f + 0.85f * (rc - bedR) * cellM;
                        if (g[j] > bankCap) g[j] = Mathf.Max(bankCap, g[j] - 2.5f);
                    }
                    else if (lvl + 0.35f - g[j] <= 1.6f)
                    {
                        // low-side berm, BOUNDED: never a curtain wall; cells
                        // hanging over a drop stay open (cascade lip).
                        g[j] = lvl + 0.35f;
                    }
                }
            return stamped;
        }

        // RUNOFF STREAM stamp — real mountain runoff (user): a LARGE CARVED EROSION PATH (a snowmelt/flood
        // gully) with only a TRICKLE of water in the bottom, like you see in summer. So we cut a wide V-ravine
        // (bounded) and wet ONLY the ~centre as a thin continuous ribbon — the rest of the gully is carved but
        // dry. The overlapping disks keep it continuous on diagonal steps (no beading into dots), and the water
        // sits at the ravine bottom well below its banks (it can't float off the terrain). wlvl = the trickle
        // surface (pass it monotonically DECREASING down the path); gullyDepth = how deep the ravine cuts.
        private static int StampRunoffCell(int i, float wlvl, float gullyDepth, int hw)
        {
            if (IsSeededMarshCell(i)) return 0;   // marsh owns its ground (see IsSeededMarshCell)
            var w = _waterLevel; var g = _grid; var m = _riverMask;
            int n = _res; float cellM = (2f * _half) / (n - 1);
            float bedR = hw + 0.4f;
            float bedC = wlvl - 0.4f;                       // gully floor — the trickle is ~0.4m deep at the centre
            int stamped = 0;
            for (int dz = -hw - 2; dz <= hw + 2; dz++)
                for (int dx = -hw - 2; dx <= hw + 2; dx++)
                {
                    int j = i + dz * n + dx;
                    if (m[j] == 3 || (m[j] == 1 && w[j] <= wlvl + 0.01f)) continue;   // a bigger river / own bed below — leave it
                    if (w[j] > g[j] + 0.01f && w[j] > wlvl + 0.5f) continue;          // standing water above (the lake) — don't drown it
                    float rc = Mathf.Sqrt(dx * dx + dz * dz);
                    if (rc <= bedR)
                    {
                        float bed = bedC + gullyDepth * (rc / bedR);                  // V-ravine: floor at centre → rim at the edge
                        if (g[j] > bed) g[j] = Mathf.Max(bed, g[j] - 6f);             // carve (bounded, no bottomless gouge)
                        if (rc < 1.35f && g[j] < wlvl)                                // TRICKLE — only the centre is wet
                        {
                            if (w[j] < wlvl) { w[j] = wlvl; stamped++; }
                            m[j] = 1;
                        }
                    }
                    else if (g[j] > bedC + gullyDepth + 0.35f)
                    {
                        // BANK: rising ground shears down to the gully rim at ~0.85 m/m (bounded).
                        float bankCap = bedC + gullyDepth + 0.35f + 0.85f * (rc - bedR) * cellM;
                        if (g[j] > bankCap) g[j] = Mathf.Max(bankCap, g[j] - 2.5f);
                    }
                }
            return stamped;
        }

        private static void SeedMountainStreams()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask; var b = _biomeMap;
            if (w == null || g == null || m == null || b == null) return;
            int n = _res;
            float cellM = (2f * _half) / (n - 1);
            int step = Mathf.Max(4, Mathf.RoundToInt(WaterRules.Stream.LatticeStep / cellM));
            int sources = 0, cellsN = 0;
            var path = new System.Collections.Generic.List<int>(2048);
            var pathG = new System.Collections.Generic.List<float>(2048);
            for (int gz = step / 2; gz < n; gz += step)
                for (int gx = step / 2; gx < n; gx += step)
                {
                    if (WanderHash01(gx, gz, _seed ^ 0x57E4) >= WaterRules.Stream.KeepChance) continue;
                    int px = gx + Mathf.RoundToInt((WanderHash01(gx, gz, _seed ^ 0x57E5) - 0.5f) * step * 0.7f);
                    int pz = gz + Mathf.RoundToInt((WanderHash01(gx, gz, _seed ^ 0x57E6) - 0.5f) * step * 0.7f);
                    if (px < 6 || pz < 6 || px >= n - 6 || pz >= n - 6) continue;
                    int ci = pz * n + px;
                    byte bio = b[ci];
                    if (bio != 3 && bio != 7) continue;
                    if (m[ci] != 0 || w[ci] > g[ci] + 0.01f) continue;
                    if (g[ci] < WaterRules.Stream.HeadwaterMinY) continue;                    // headwaters live HIGH
                    path.Clear();
                    int cur = ci;
                    bool metMask = false, metStill = false;
                    for (int guard = 0; guard < 1500; guard++)
                    {
                        int cx = cur % n, cz = cur / n;
                        if (cx < 5 || cz < 5 || cx >= n - 5 || cz >= n - 5) break;   // stamp box reaches ±4 cells
                        int best = -1, second = -1; float bg = g[cur], sg = g[cur];
                        for (int e = 0; e < 4; e++)
                        {
                            int j = cur + (e == 0 ? 1 : e == 1 ? -1 : e == 2 ? n : -n);
                            // mask-2 BENCH cells are WALKABLE: the river's dry
                            // margin blocked the trace 2-6m short of the real
                            // channel and every confluence had a dry strip
                            // (user screenshot: stream fanning out on the sand
                            // beside the river). Only the channel itself ends
                            // the stream.
                            if (m[j] == 1 || m[j] == 3) { if (guard > 3) metMask = true; continue; }
                            if (w[j] > g[j] + 0.01f) { if (guard > 3) metStill = true; continue; }
                            if (g[j] < bg) { second = best; sg = bg; best = j; bg = g[j]; }
                            else if (g[j] < sg) { second = j; sg = g[j]; }
                        }
                        if (metMask || metStill) break;
                        if (best < 0) break;                      // pit — the stream sinks
                        int take = best;
                        // MEANDER: sometimes take the second-lowest exit so
                        // the stream wanders instead of running the fall line.
                        if (second >= 0 && sg < g[cur] - 0.02f
                            && WanderHash01(cur % n, cur / n, _seed ^ 0x57E7) < WaterRules.Stream.MeanderChance)
                            take = second;
                        path.Add(take);
                        cur = take;
                        if (g[cur] <= SEA + 0.5f) break;
                    }
                    if (path.Count < WaterRules.Stream.MinPathCells) continue;                // < 60m — not a stream
                    // NO truncation at still water: the old RemoveAt(×2) left
                    // a 4m UNCARVED dirt lip between the channel and every
                    // pool it fed ("streams still not properly carving out
                    // between pools" — user). The stamp skips wet cells, so
                    // carving flush to the pool can't damage the pool itself.
                    sources++;
                    // ORIGINAL surface captured BEFORE any digging (the disk
                    // stamp digs ahead — live reads fed the bed back into the
                    // ratchet = runaway trenches). RIVER RULE upgrade: the
                    // reference height is the pristine MIN over the FULL
                    // stamp box, not the centreline — on spiky ground the
                    // centreline rides the ridge and the sheet FLOATED over
                    // every crevice beside it, curl shards and all (user
                    // screenshot). With the box min, the level sits below
                    // every bank cell the sheet spans and the carve digs the
                    // channel down to it — the mesh clips INTO the banks.
                    pathG.Clear();
                    for (int k = 0; k < path.Count; k++)
                    {
                        int i = path[k];
                        float mn = g[i];
                        for (int dz = -4; dz <= 4; dz++)
                            for (int dx = -4; dx <= 4; dx++)
                            {
                                float gv2 = g[i + dz * n + dx];
                                if (gv2 < mn) mn = gv2;
                            }
                        pathG.Add(mn);
                    }
                    float lvl = pathG[0] - WaterRules.Stream.SourceLevelDrop;
                    for (int k = 0; k < path.Count; k++)
                    {
                        int i = path[k];
                        // Real summer RUNOFF: a carved erosion GULLY with a TRICKLE in the bottom (user). The
                        // ravine deepens + widens downstream; the trickle surface sits well below the ground so
                        // it can't float off the terrain, and the overlapping stamp keeps it continuous (no dots).
                        float gd = WaterRules.Stream.RavineDepthMin + Mathf.Min(WaterRules.Stream.RavineDepthMax, k * WaterRules.Stream.RavineDepthRate);            // ravine depth 1.8 → 4.4m
                        lvl = Mathf.Min(lvl - 0.05f, pathG[k] - gd + 0.4f);       // trickle at the ravine bottom
                        cellsN += StampRunoffCell(i, lvl, gd, k < 100 ? 1 : 2);
                    }
                    // TERMINAL RULE: a channel ends IN WATER or INTO A BANK —
                    // never hanging on open ground (user screenshot: the
                    // run's end standing as a floating curtain). A trace that
                    // died in a pit or on the flats gets a terminal POOL: one
                    // wide deep stamp — bowl, cut banks, berms — so the
                    // stream visibly drains into a pond.
                    if (!metMask && !metStill)
                    {
                        int last = path[path.Count - 1];
                        if (g[last] > SEA + 0.6f)
                            cellsN += StampChannelCell(last,
                                Mathf.Min(lvl, pathG[path.Count - 1] - 0.3f), 2.0f, 3, 0f);
                    }
                }
            if (sources > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] headwater streams: {sources} mountain stream(s) carved ({cellsN:N0} cells) — sources above 85m, meandering to lower water.");
        }

        // REALISTIC HIGHLAND LAKES — carve believable lake basins INTO the headwater terrain. The
        // ridge ranges are too dissected for natural flat bowls to be found (that starved the tarns),
        // so we SHAPE them the way water would over time: a lake sits in a valley on the real drainage,
        // fed from upstream and DAMMED at the downstream lip with a cut OUTLET notch (the outflow
        // stream), a steep CUT-BANK cliff on one shore and a gentle DELTA shelf on the other — the
        // erosion asymmetry the user described. Runs before NormalizeBasinWater so the drop-to-bank
        // pass then levels each below its banks.
        private struct HLake { public int Px, Pz, DownE; }
        private static readonly System.Collections.Generic.List<HLake> s_highlandLakes = new System.Collections.Generic.List<HLake>();

        private static void CarveHighlandLakes(int seed)
        {
            var g = _grid; var w = _waterLevel; var m = _riverMask; var b = _biomeMap;
            if (g == null || w == null || m == null || b == null) return;
            int n = _res; float cellM = (2f * _half) / (n - 1);
            int step = Mathf.Max(10, Mathf.RoundToInt(620f / cellM));
            int[] ex = { 1, -1, 0, 0, 1, 1, -1, -1 }, ez = { 0, 0, 1, -1, 1, -1, 1, -1 };
            int made = 0; long wet = 0; s_highlandLakes.Clear();
            for (int gz = step; gz < n - step; gz += step)
                for (int gx = step; gx < n - step; gx += step)
                {
                    if (WanderHash01(gx, gz, seed ^ 0x1A40) > 0.5f) continue;
                    int px = gx + Mathf.RoundToInt((WanderHash01(gx, gz, seed ^ 0x1A41) - 0.5f) * step * 0.6f);
                    int pz = gz + Mathf.RoundToInt((WanderHash01(gx, gz, seed ^ 0x1A42) - 0.5f) * step * 0.6f);
                    int c = pz * n + px;
                    byte bio = b[c];
                    if (bio != 3 && bio != 7) continue;                 // Mountain / Frostreach ground
                    if (m[c] != 0 || w[c] > g[c] + 0.01f) continue;     // not on a river / existing water
                    float g0 = g[c];
                    if (g0 < 95f) continue;

                    // downstream = steepest descent; must actually drop (a valley floor, not a peak/pit)
                    int down = -1; float lo = g0;
                    for (int e = 0; e < 8; e++) { int j = (pz + ez[e]) * n + (px + ex[e]); if (g[j] < lo) { lo = g[j]; down = e; } }
                    if (down < 0 || g0 - lo < 0.15f) continue;
                    float fx = ex[down], fz = ez[down]; float fl = Mathf.Sqrt(fx * fx + fz * fz); fx /= fl; fz /= fl;
                    float cx = -fz, cz = fx;                             // cross-flow axis

                    float A = 30f + WanderHash01(px, pz, seed ^ 0x1A43) * 46f;         // lake radius along the valley (m) — any size
                    float B = A * (0.5f + WanderHash01(px, pz, seed ^ 0x1A44) * 0.45f); // cross-valley radius
                    float depth = 4f + WanderHash01(px, pz, seed ^ 0x1A45) * 8f;
                    float level = g0;
                    int reach = Mathf.CeilToInt((A * 1.8f + 24f) / cellM);
                    if (px < reach || pz < reach || px >= n - reach || pz >= n - reach) continue;

                    // VALLEY check: cross-walls must rise (a real valley that can hold a lake), and no carved
                    // river / standing water in the near cross-section (don't drown a river).
                    float wallL = 0f, wallR = 0f; bool bad = false;
                    int chk = Mathf.CeilToInt(A / cellM);
                    for (int s2 = -chk; s2 <= chk && !bad; s2++)
                    {
                        int ax = px + Mathf.RoundToInt(cx * s2), az = pz + Mathf.RoundToInt(cz * s2);
                        int aj = az * n + ax;
                        if (Mathf.Abs(s2) <= 2 && (m[aj] == 1 || m[aj] == 3 || w[aj] > g[aj] + 0.01f)) { bad = true; break; }
                        if (s2 > chk / 3) wallR = Mathf.Max(wallR, g[aj] - g0);
                        else if (s2 < -chk / 3) wallL = Mathf.Max(wallL, g[aj] - g0);
                    }
                    if (bad || (wallL < 3f && wallR < 3f)) continue;

                    // CARVE an ORGANIC depression and LAY the water in (user: "carve the depression and lay the
                    // water in, at whatever size is needed" — we're not simulating flow, the mesh just sits
                    // where we put it). The outline is a DOMAIN-WARPED blob — each point is shoved by smooth
                    // fbm before the radial test — so the shore is curved and irregular: NO circle, NO straight
                    // dam edge, NO radius clip. Bed is a bowl (deep centre → shore); the warped rim just past
                    // the shore is raised proud so the flat water reads as contained. Runs after normalize.
                    float wpx = -_half + px * cellM, wpz = -_half + pz * cellM;
                    for (int dz = -reach; dz <= reach; dz++)
                        for (int dx = -reach; dx <= reach; dx++)
                        {
                            int j = (pz + dz) * n + (px + dx);
                            if (m[j] == 1 || m[j] == 3) continue;                        // never drown a river
                            float um = (dx * fx + dz * fz) * cellM, vm = (dx * cx + dz * cz) * cellM;
                            float wox = wpx + um, woz = wpz + vm;                        // world coords for the warp noise
                            float uw = um + (Fbm(wox, woz, 1f / 52f, 3, seed + 71) - 0.5f) * A * 0.85f;
                            float vw = vm + (Fbm(wox, woz, 1f / 52f, 3, seed + 72) - 0.5f) * A * 0.85f;
                            float dm = uw * uw / (A * A) + vw * vw / (B * B);            // warped-blob distance²
                            if (dm < 1f)
                            {
                                float bed = level - depth * (1f - dm);                   // bowl: deep centre → shore
                                if (g[j] > bed) g[j] = bed;
                                if (w[j] < level) { w[j] = level; wet++; }
                            }
                            else if (dm < 1.7f && w[j] <= g[j] + 0.01f && g[j] < level + 0.6f)
                                g[j] = level + 0.6f;                                     // organic rim, proud of the water
                        }

                    // Record the lake; its spillway notch + outflow river are cut AFTER drop-to-bank has
                    // leveled the basin (an outlet carved now would let drop-to-bank drain the whole lake).
                    s_highlandLakes.Add(new HLake { Px = px, Pz = pz, DownE = down });
                    made++;
                    if (made <= 6)
                        FiresLogger.LogInfo($"[FiresWorldGen]   highland lake #{made} @ world({-_half + px * cellM:0},{-_half + pz * cellM:0}) level {level:0}m A={A:0}m");
                }
            if (made > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] highland lakes: {made} closed valley basins carved ({wet:N0} cells wet) — cut-bank cliff + delta shore; spillways cut after leveling.");
        }

        // Cut each highland lake's SPILLWAY + OUTFLOW after drop-to-bank has leveled the basin (user:
        // "run the spillway notch after drop to banks, then run the river out from that"). Carving the
        // outlet earlier let drop-to-bank drain the whole lake down it. Here the level is FINAL: breach the
        // downstream bank down to the surface (the notch) and run a channel — a real outflow river —
        // stepping downhill until it meets lower water.
        private static void CutHighlandSpillways(int seed)
        {
            var g = _grid; var w = _waterLevel; var m = _riverMask;
            if (g == null || w == null || m == null || s_highlandLakes.Count == 0) return;
            int n = _res;
            int[] ex = { 1, -1, 0, 0, 1, 1, -1, -1 }, ez = { 0, 0, 1, -1, 1, -1, 1, -1 };
            int cut = 0, dried = 0;
            foreach (var lk in s_highlandLakes)
            {
                int px = lk.Px, pz = lk.Pz, c = pz * n + px;
                float surf = w[c];
                if (surf <= g[c] + 0.05f) { dried++; continue; }          // basin dried — nothing to spill
                float fx = ex[lk.DownE], fz = ez[lk.DownE];
                float fl = Mathf.Sqrt(fx * fx + fz * fz); fx /= fl; fz /= fl;
                // walk downstream to the lake shore (last wet cell before dry ground)
                int sx = px, sz = pz;
                for (int s2 = 1; s2 < 200; s2++)
                {
                    int nx = px + Mathf.RoundToInt(fx * s2), nz = pz + Mathf.RoundToInt(fz * s2);
                    if (nx < 3 || nz < 3 || nx >= n - 3 || nz >= n - 3) break;
                    if (w[nz * n + nx] > g[nz * n + nx] + 0.05f) { sx = nx; sz = nz; continue; }   // still in the lake
                    break;                                                // reached the shore
                }
                // Run the outflow as a carved runoff GULLY with a trickle — continuous and sitting IN the
                // terrain (replaces the old dotted, floating single-cell carve). Starts ~1m below the lake
                // surface (a spillway lip) so the stamp's own-water guard doesn't back-flood the standing lake.
                int ox = sx, oz = sz; float lvl = surf - 1.0f; bool ran = false; int steps = 0;
                for (int s2 = 0; s2 < 220; s2++)
                {
                    if (ox < 6 || oz < 6 || ox >= n - 6 || oz >= n - 6) break;
                    int oc = oz * n + ox;
                    if (s2 > 2 && ((m[oc] == 1 || m[oc] == 3) || w[oc] > g[oc] + 0.05f)) { ran = true; break; }   // met a river / lower water
                    float gd = 1.6f + Mathf.Min(2.4f, s2 * 0.02f);         // ravine deepens downstream
                    lvl = Mathf.Min(lvl - 0.1f, g[oc] - gd + 0.4f);        // trickle at the ravine bottom, below ground
                    StampRunoffCell(oc, lvl, gd, s2 < 60 ? 1 : 2);
                    steps++;
                    int nd = -1; float nl = 1e9f;
                    for (int e = 0; e < 8; e++) { int j = (oz + ez[e]) * n + (ox + ex[e]); if (g[j] < nl) { nl = g[j]; nd = e; } }
                    if (nd < 0) break;
                    ox += ex[nd]; oz += ez[nd];
                }
                if (ran || steps > 4) cut++;
            }
            FiresLogger.LogInfo($"[FiresWorldGen] highland spillways: {cut} lake outlets cut AFTER drop-to-bank ({dried}/{s_highlandLakes.Count} basins had dried before spill) — notch at leveled surface, outflow river downhill.");
        }

    }
}
