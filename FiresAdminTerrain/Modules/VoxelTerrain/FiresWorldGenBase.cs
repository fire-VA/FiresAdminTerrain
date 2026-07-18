using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Random = System.Random;

namespace VerdantsAscent.Terrain.Voxel
{
    // W4 — the Fires base worldgen. Replaces vanilla's GetBaseHeight with a
    // precomputed eroded heightfield so heights, biomes, the minimap, location
    // placement and everything downstream (heightmaps, voxel wrap, W2
    // hydrology) all flow from OUR terrain. Vanilla's flat river carving is
    // disabled while active — erosion valleys and the W2 runtime hydrology own
    // rivers instead.
    //
    // Activation is per-world and deterministic on every peer with no network
    // sync: worlds whose NAME starts with "Fires" generate with this system
    // (config can force on/off). The generated grid is cached to disk next to
    // the other Fires caches; once a world has generated, its cache file IS
    // that world's base truth — later algorithm changes only affect new worlds
    // (the cache is only rebuilt if the file is deleted).
    //
    // Every stage is a pure function of the world seed: value-noise FBM
    // continents with domain warp, ridged mountain ranges, then a serial
    // droplet-erosion pass (ported from Tools/FiresWorldGen) that carves the
    // dendritic valley networks. Serial on purpose — parallel droplets would
    // be nondeterministic and desync peers.
    public static class FiresWorldGenBase
    {
        private const int FORMAT_VERSION = 33; // v34 pool-riffle beds — settled rivers hold water in pool chains
        private const uint MAGIC = 0x4E454746; // "FGEN"
        private const float HALF = 10500f;     // generated grids span ±HALF metres
        private const float SEA = 30f;

        // River water sits this far below the bank tops.
        public const float RIVER_DROP = 0.40f;

        private static float[] _grid;          // metres; row-major [z * n + x]
        private static byte[] _biomeMap;       // W4.1 — OUR biome placement, same grid
        private static byte[] _riverMask;      // 0 none, 1 river bed, 2 bank
        private static float[] _waterLevel;    // v8 — min(flood fill, original terrain): the spring fill level
        private static int _res;
        private static float _half = HALF;     // VAHC bases can span a different radius
        private static bool _active;
        private static int _seed;              // world seed (detail noise)

        public static bool Active => _active && _grid != null;

        // Biome codes (grid) → Heightmap.Biome. Order matches the offline
        // mirror: Meadows, BlackForest, Swamp, Mountain, Plains, Mistlands,
        // AshLands, DeepNorth, Ocean.
        private static readonly Heightmap.Biome[] BIOME_CODES =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp,
            Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands,
            Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean,
        };

        public static Heightmap.Biome SampleBiome(float wx, float wy)
        {
            var b = _biomeMap;
            int n = _res;
            float half = _half;
            if (b == null) return Heightmap.Biome.Ocean;
            int gx = Mathf.RoundToInt((wx + half) / (2f * half) * (n - 1));
            int gz = Mathf.RoundToInt((wy + half) / (2f * half) * (n - 1));
            if (gx < 0 || gx >= n || gz < 0 || gz >= n) return Heightmap.Biome.Ocean;
            byte code = b[gz * n + gx];
            return code < BIOME_CODES.Length ? BIOME_CODES[code] : Heightmap.Biome.Ocean;
        }

        // Final in-game surface = grid + OUR detail (±~1.75m). Vanilla's
        // per-biome height functions stack ±2-8m of their own noise on top of
        // the base — enough to bury a 2-3m river channel — so GetBiomeHeight
        // is owned outright and the offline preview becomes WYSIWYG.
        // The far quilt (and anything else sampling the analytic surface)
        // must WAIT for this — sampling before the grid exists returns 0m
        // (below sea) and the quilt tombstoned its whole far ring as
        // "underwater" on fresh worlds: ocean to the horizon.
        public static bool GridReady => _grid != null;

        // One-entry memo per thread: the vanilla heightmap build calls this
        // 4× per vertex (corner-biome blending) and biome selection hammers
        // the base sampler at repeated coords — ghost-zone spawns measured
        // 112-159ms each from exactly that (ZFLOW receipts). Same-coord
        // repeat hits return instantly; results are bit-identical.
        [ThreadStatic] private static float t_fmX, t_fmY, t_fmV;
        [ThreadStatic] private static int t_fmGen;
        private static int s_sampleGen = 1;   // bumped per world init — stale memos die

        public static float SampleFinalMeters(float wx, float wy)
        {
            if (t_fmGen == s_sampleGen && wx == t_fmX && wy == t_fmY) return t_fmV;
            float v = SampleFinalMetersUncached(wx, wy);
            t_fmX = wx; t_fmY = wy; t_fmV = v; t_fmGen = s_sampleGen;
            return v;
        }

        private static float SampleFinalMetersUncached(float wx, float wy)
        {
            float baseM = SampleBaseNormalized(wx, wy) * 200f;
            int s = _seed * 7;
            float detail = (Fbm(wx, wy, 1f / 26f, 2, s + 91) - 0.5f) * 2.6f
                         + (Fbm(wx, wy, 1f / 6.5f, 1, s + 92) - 0.5f) * 0.9f;
            // Calm shorelines/sea floor: fade detail below the waterline.
            detail *= 0.35f + 0.65f * Smooth(27f, 32f, baseM);
            // Crisp channels: inside the carved river mask the detail noise
            // stands down. SMOOTH (bilinear) damp — the old nearest-cell
            // switch stepped the ground ±1.7m in one meter along every mask
            // edge: knife-edge ridges that poked through channel water as
            // 1-cell dry slits with deep bed beside them (live-cache-proven).
            var m = _riverMask;
            int n = _res;
            float half = _half;
            if (m != null)
            {
                float cell = (2f * half) / (n - 1);
                float gxf = (wx + half) / cell, gzf = (wy + half) / cell;
                if (gxf < 0f) gxf = 0f; else if (gxf > n - 1.001f) gxf = n - 1.001f;
                if (gzf < 0f) gzf = 0f; else if (gzf > n - 1.001f) gzf = n - 1.001f;
                int xi = (int)gxf, zi = (int)gzf;
                float fx = gxf - xi, fz = gzf - zi;
                int i0 = zi * n + xi;
                float damp = (m[i0] != 0 ? 1f : 0f) * (1f - fx) * (1f - fz)
                           + (m[i0 + 1] != 0 ? 1f : 0f) * fx * (1f - fz)
                           + (m[i0 + n] != 0 ? 1f : 0f) * (1f - fx) * fz
                           + (m[i0 + n + 1] != 0 ? 1f : 0f) * fx * fz;
                detail *= 1f - 0.92f * damp;
            }
            return baseM + detail;
        }

        // Generated-water field sample: the relaxed, below-grade water level
        // plus whether this cell holds standing water (river wet channel or a
        // lake with ≥1.2m of real fill). THE settled answer — the direct
        // water meshes render exactly this field.
        public static bool TryGetGenWater(float wx, float wz, out float level, out bool wet)
        {
            level = 0f; wet = false;
            var m = _riverMask; var w = _waterLevel; var g = _grid;
            if (m == null || w == null || g == null) return false;
            int n = _res; float half = _half;
            int gx = Mathf.RoundToInt((wx + half) / (2f * half) * (n - 1));
            int gz = Mathf.RoundToInt((wz + half) / (2f * half) * (n - 1));
            if (gx < 0 || gx >= n || gz < 0 || gz >= n) return false;
            int i = gz * n + gx;
            level = w[i];
            byte mk = m[i];
            // Channel cells (v29 single-stem carve — genuinely dug trench)
            // wet at real submersion; everything else = pond rule (post
            // basin-normalize).
            wet = (mk == 1 || mk == 3) ? level - g[i] >= 0.4f : level - g[i] >= 1.2f;
            return true;
        }

        // ANY standing generated water — river wet channel OR lake fill.
        // Bed paint, the vegetation veto and the clutter veto key off THIS,
        // bank to bank across whole basins; IsRiverAt stays river-specific
        // (spine spots, detail suppression).
        public static bool IsWetAt(float wx, float wz)
            => TryGetGenWater(wx, wz, out _, out bool wet) && wet;

        // SMOOTH (bilinear) water level + depth — THE water mesh sample.
        // Nearest-cell sampling turned every 10m cell into a flat plate:
        // stepped slabs, height jumps at zone borders, channel level held
        // level all the way over the bank edge. Bilinear is a pure function
        // of world position (identical from every zone — seams impossible)
        // and tapers into the banks naturally, because dry/dried cells carry
        // w ≈ ground. Depth blends the same way and reaches 0 at dry cells,
        // so the wet edge dies out instead of clipping.
        public static bool TryGetGenWaterSmooth(float wx, float wz, out float level, out float depth)
        {
            level = 0f; depth = 0f;
            var w = _waterLevel; var g = _grid;
            if (w == null || g == null) return false;
            int n = _res; float half = _half;
            float cell = (2f * half) / (n - 1);
            float gx = (wx + half) / cell, gz = (wz + half) / cell;
            if (gx < 0f) gx = 0f; else if (gx > n - 1.001f) gx = n - 1.001f;
            if (gz < 0f) gz = 0f; else if (gz > n - 1.001f) gz = n - 1.001f;
            int xi = (int)gx, zi = (int)gz;
            float fx = gx - xi, fz = gz - zi;
            int i = zi * n + xi;
            float w00 = w[i], w10 = w[i + 1], w01 = w[i + n], w11 = w[i + n + 1];
            level = w00 * (1f - fx) * (1f - fz) + w10 * fx * (1f - fz)
                  + w01 * (1f - fx) * fz + w11 * fx * fz;
            float d00 = Mathf.Max(0f, w00 - g[i]);
            float d10 = Mathf.Max(0f, w10 - g[i + 1]);
            float d01 = Mathf.Max(0f, w01 - g[i + n]);
            float d11 = Mathf.Max(0f, w11 - g[i + n + 1]);
            depth = d00 * (1f - fx) * (1f - fz) + d10 * fx * (1f - fz)
                  + d01 * (1f - fx) * fz + d11 * fx * fz;
            return true;
        }

        // Fine water meshing: may fine-resolution water spread into this cell.
        // True inside the generated water footprint — the carved corridor
        // (bed/bank/spine mask) or anything the coarse field already calls
        // wet. The 1m flood stays inside this footprint while the real crest
        // line decides the actual water edge.
        public static bool IsWaterCorridorAt(float wx, float wz)
        {
            // WET cells only. The raw mask footprint covers entire valley
            // floors (the carve's over-wide bed marking) — treating it as
            // floodable re-drowns everything the depth rule just dried. The
            // fine flood's own one-coarse-cell dilation supplies the edge
            // margin.
            return TryGetGenWater(wx, wz, out _, out bool wet) && wet;
        }

        public static void OnWorldInitialize(World world)
        {
            _active = false;
            s_sampleGen++;   // invalidate every thread's sampler memo
            _grid = null;
            _biomeMap = null;
            _riverMask = null;
            _waterLevel = null;
            if (world == null || world.m_menu) return;

            var mode = FiresVoxelConfig.WorldGenMode?.Value ?? FiresWorldGenMode.Auto;
            if (mode == FiresWorldGenMode.Off) return;
            bool nameHit = (world.m_name ?? "").StartsWith("Fires", StringComparison.OrdinalIgnoreCase);
            if (mode == FiresWorldGenMode.Auto && !nameHit)
            {
                Debug.Log($"[FiresWorldGen] world '{world.m_name}' has no Fires prefix — vanilla generation (mode=Auto).");
                return;
            }

            int res = Mathf.Clamp(FiresVoxelConfig.WorldGenResolution?.Value ?? 10501, 512, 12000);
            int droplets = Mathf.Max(0, FiresVoxelConfig.WorldGenDroplets?.Value ?? 400000);
            string vahc = FiresVoxelConfig.WorldGenVahcBase?.Value ?? "";
            bool useVahc = !string.IsNullOrWhiteSpace(vahc);

            // A configured base whose file is MISSING is a hard config error —
            // silently generating a different planet desyncs every peer that
            // HAS the file. Scream, then generate under the UNTAGGED cache
            // name so the fallback is never mistaken for the real base.
            if (useVahc)
            {
                string vp = Path.IsPathRooted(vahc) ? vahc
                    : Path.Combine(FiresCore.Storage.FiresConfigPaths.Cache, vahc);
                if (!File.Exists(vp))
                {
                    Debug.LogError($"[FiresWorldGen] WorldGen VAHC Base is configured but the file is MISSING: {vp} — " +
                                   "generating procedurally instead. Peers that HAVE the file will build a DIFFERENT world. " +
                                   "Fix the file placement, delete this world's firesgen_* cache, and wipe the world.");
                    useVahc = false;
                }
            }
            string path = CachePath(world.m_name, world.m_seed, res, useVahc);

            var sw = Stopwatch.StartNew();
            _grid = TryLoad(path, out _biomeMap, out _riverMask, out _waterLevel, out int n, out float half);
            string source = "cache";
            if (_grid == null)
            {
                string img = FiresVoxelConfig.WorldGenImageBase?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(img))
                {
                    _grid = LoadImageBase(img, res);
                    if (_grid != null)
                    {
                        n = res;
                        half = HALF;
                        Erode(_grid, n, droplets, world.m_seed ^ 0x51ED);
                        _riverMask = CarveRivers(_grid, n, world.m_seed, out _waterLevel);
                        source = "image base";
                    }
                }
                if (_grid == null && useVahc)
                {
                    _grid = LoadVahcBase(vahc, res, out n, out half);
                    if (_grid != null)
                    {
                        Erode(_grid, n, droplets, world.m_seed ^ 0x51ED);
                        _riverMask = CarveRivers(_grid, n, world.m_seed, out _waterLevel);
                        source = "vahc base";
                    }
                    else
                    {
                        Debug.LogError("[FiresWorldGen] VAHC base failed to load (see above) — generating procedurally under the untagged cache name.");
                        path = CachePath(world.m_name, world.m_seed, res, false);
                    }
                }
                if (_grid == null)
                {
                    _grid = BuildGrid(world.m_seed, res, droplets, out _riverMask, out _waterLevel);
                    n = res;
                    half = HALF;
                    source = "generated";
                }
                _biomeMap = BuildBiomes(_grid, n, half, world.m_seed, source == "generated");
                string biomeImg = FiresVoxelConfig.WorldGenBiomeImage?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(biomeImg)) ApplyBiomeImage(biomeImg, _biomeMap, n);
                try { Save(path, _grid, _biomeMap, _riverMask, _waterLevel, n, half); }
                catch (Exception ex) { Debug.LogWarning($"[FiresWorldGen] cache save failed ({ex.Message}) — world still active, will regenerate next load."); }
            }
            _res = n;
            _half = half;
            _seed = world.m_seed;
            NormalizeBasinWater();
            AuditWater();
            _active = true;
            Debug.Log($"[FiresWorldGen] ACTIVE for '{world.m_name}' seed={world.m_seed} grid={n} half={half:F0} droplets={droplets} " +
                      $"({source}) in {sw.ElapsedMilliseconds}ms | {path}");
        }

        // Basin fills come out of the fill table at SPILL level — a closed
        // mountain bowl "fills" 50-200m deep, and the pond rule then marks
        // the whole valley wet at that level (offline proof against the live
        // v28 cache: 93% of a 512m tile wet, depths to 226m). Real lakes sit
        // at the BOTTOM of their basin: cap every non-river cell of each
        // connected fill component to the component's floor (min ground over
        // its plain-pond cells) + LAKE_MAX_DEPTH. Channel/spine cells keep
        // the relaxed river field; sea-level components are untouched via the
        // SEA floor on the cap. Pure function of (w,h,mask) → idempotent,
        // runs on fresh gen AND cache load, no format bump.
        // Deterministic per-cell hash → [0,1) — MIRRORED byte-for-byte in the
        // offline tool (mirror rule: identical rivers or the preview lies).
        private static float WanderHash01(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) ^ (uint)(z * 668265263) ^ (uint)(seed * 2246822519);
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        private const float LAKE_MAX_DEPTH = 6f;
        private static void NormalizeBasinWater()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask;
            if (w == null || g == null || m == null) return;
            int n = _res;

            // SETTLE, THEN FREEZE (the user's Water-Volume doctrine, done as
            // one closed-form pass instead of a sim): water at rest cannot
            // stand above the terrain's own POUR surface — anything higher
            // would drain over a rim. level := min(level, pour(carved
            // ground)) collapses every draped blanket, tilted sheet and
            // cliff curtain into flat pools with steps at the falls, and
            // frozen-by-construction needs no updates. Runs on every load
            // (pure function of cached data — no format bump, existing
            // worlds settle on next boot).
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pour = PriorityFloodFill(g, n);
                int settledCells = 0;
                for (int i = 0; i < n * n; i++)
                    if (w[i] > pour[i]) { w[i] = pour[i]; settledCells++; }
                Debug.Log($"[FiresWorldGen] water settled: {settledCells:N0} cells lowered to the pour surface in {sw.ElapsedMilliseconds}ms.");
            }
            var seen = new bool[n * n];
            var stack = new Stack<int>(1024);
            var comp = new System.Collections.Generic.List<int>(4096);
            int capped = 0, comps = 0, dropped = 0, droppedComps = 0;
            for (int i0 = 0; i0 < n * n; i0++)
            {
                if (seen[i0] || w[i0] <= g[i0] + 0.01f) continue;
                comp.Clear();
                float floor = float.MaxValue;
                seen[i0] = true;
                stack.Push(i0);
                float rimMin = float.MaxValue;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    comp.Add(i);
                    if (m[i] == 0 && g[i] < floor) floor = g[i];
                    int x = i % n, z = i / n;
                    if (x + 1 < n) { int j = i + 1; if (!seen[j] && w[j] > g[j] + 0.01f) { seen[j] = true; stack.Push(j); } else if (w[j] <= g[j] + 0.01f && g[j] < rimMin) rimMin = g[j]; }
                    if (x > 0) { int j = i - 1; if (!seen[j] && w[j] > g[j] + 0.01f) { seen[j] = true; stack.Push(j); } else if (w[j] <= g[j] + 0.01f && g[j] < rimMin) rimMin = g[j]; }
                    if (z + 1 < n) { int j = i + n; if (!seen[j] && w[j] > g[j] + 0.01f) { seen[j] = true; stack.Push(j); } else if (w[j] <= g[j] + 0.01f && g[j] < rimMin) rimMin = g[j]; }
                    if (z > 0) { int j = i - n; if (!seen[j] && w[j] > g[j] + 0.01f) { seen[j] = true; stack.Push(j); } else if (w[j] <= g[j] + 0.01f && g[j] < rimMin) rimMin = g[j]; }
                }
                if (floor == float.MaxValue) continue;   // river-only component
                float cap = Mathf.Max(floor + LAKE_MAX_DEPTH, SEA + 0.5f);
                // LEAKY ponds: the fill table called this basin contained,
                // but its rim dips BELOW the water level — in-game that's a
                // pond hanging on a slope with a water wall at the downhill
                // edge. The level cannot exceed the lowest rim.
                if (rimMin != float.MaxValue && cap > rimMin - 0.3f)
                    cap = rimMin - 0.3f;
                bool any = false;
                int wetCells = 0;
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
                    if (w[i] > cap) { w[i] = Mathf.Max(cap, g[i]); capped++; any = true; }
                    float d = w[i] - g[i];
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
                bool keep = (wetCells <= 60 && depthMax >= 2f)
                         || (wetCells <= 4000 && depthSum / Mathf.Max(1, wetCells) >= 3f);
                if (!keep)
                {
                    for (int k = 0; k < comp.Count; k++)
                    {
                        int i = comp[k];
                        if (m[i] != 0) continue;
                        if (w[i] > g[i] && w[i] > SEA + 0.5f) { w[i] = Mathf.Min(w[i], g[i]); dropped++; }
                    }
                    droppedComps++;
                }
                if (any) comps++;
            }
            if (capped + dropped > 0)
                Debug.Log($"[FiresWorldGen] basin water normalized: {capped:N0} cells capped across {comps} basins (floor+{LAKE_MAX_DEPTH:0}m); " +
                          $"{dropped:N0} cells across {droppedComps} broad-shallow basins dropped (valley floods).");
        }

        // Load-time ACCEPTANCE AUDIT — the never-above-banks invariant,
        // verified against the exact field the session will play on. A
        // violation = a wet cell whose 3×3 holds a DRY cell with ground
        // below the water level − 0.35 (a water wall in-game). MUST be 0;
        // the first few violations log world coordinates so a screenshot
        // can be matched to the data cell-for-cell.
        private static void AuditWater()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask;
            if (w == null || g == null || m == null) return;
            int n = _res;
            float half = _half;
            float cell = (2f * half) / (n - 1);
            int wet = 0, violIn = 0, violRim = 0;
            float lvMin = float.MaxValue, lvMax = float.MinValue;
            var examples = new System.Text.StringBuilder(128);
            for (int z = 1; z < n - 1; z++)
            {
                for (int x = 1; x < n - 1; x++)
                {
                    int i = z * n + x;
                    byte mk = m[i];
                    bool isWet = (mk == 1 || mk == 3) ? w[i] - g[i] >= 0.4f : w[i] - g[i] >= 1.2f;
                    if (!isWet || w[i] <= SEA + 0.5f) continue;
                    wet++;
                    if (w[i] < lvMin) lvMin = w[i];
                    if (w[i] > lvMax) lvMax = w[i];
                    bool bad = false;
                    for (int e = 0; e < 8 && !bad; e++)
                    {
                        int j = (z + RIV_DZ[e]) * n + (x + RIV_DX[e]);
                        byte mj = m[j];
                        bool jWet = (mj == 1 || mj == 3) ? w[j] - g[j] >= 0.4f : w[j] - g[j] >= 1.2f;
                        if (!jWet && g[j] < w[i] - 0.35f) bad = true;
                    }
                    if (!bad) continue;
                    // The world-rim fade band (r ≥ ~9.3km) crushes terrain
                    // under existing water — perpetual violations in the
                    // unplayable margin. Acceptance is the INLAND count.
                    float wx = -half + x * cell, wz = -half + z * cell;
                    if (wx * wx + wz * wz >= 9300f * 9300f) { violRim++; continue; }
                    violIn++;
                    if (violIn <= 5)
                        examples.Append($" ({wx:0},{wz:0})Δ{w[i] - g[i]:0.0}m");
                }
            }
            Debug.Log($"[FiresWorldGen] WATER AUDIT: {wet:N0} wet cells, lvl {lvMin:F1}-{lvMax:F1}, " +
                      $"inland above-bank violations {violIn} (rim band: {violRim})" +
                      (violIn > 0 ? $" — FAIL, first:{examples}" : " — PASS."));
        }

        // Vanilla GetBaseHeight units: metres / 200. Water sits at 0.15 (30m),
        // Ocean biome ≤ 0.02, Mountain biome ≥ 0.4. The >10000m edge fades are
        // vanilla's own numbers, reproduced so the world-rim void is intact.
        [ThreadStatic] private static float t_bnX, t_bnY, t_bnV;
        [ThreadStatic] private static int t_bnGen;

        public static float SampleBaseNormalized(float wx, float wy)
        {
            if (t_bnGen == s_sampleGen && wx == t_bnX && wy == t_bnY) return t_bnV;
            float v = SampleBaseNormalizedUncached(wx, wy);
            t_bnX = wx; t_bnY = wy; t_bnV = v; t_bnGen = s_sampleGen;
            return v;
        }

        private static float SampleBaseNormalizedUncached(float wx, float wy)
        {
            var g = _grid;
            int n = _res;
            float half = _half;
            if (g == null) return 0f;

            float cell = (2f * half) / (n - 1);
            float gx = (wx + half) / cell;
            float gz = (wy + half) / cell;
            if (gx < 0f) gx = 0f; else if (gx > n - 1.001f) gx = n - 1.001f;
            if (gz < 0f) gz = 0f; else if (gz > n - 1.001f) gz = n - 1.001f;
            int xi = (int)gx, zi = (int)gz;
            float fx = gx - xi, fz = gz - zi;
            int i = zi * n + xi;
            float h = g[i] * (1f - fx) * (1f - fz) + g[i + 1] * fx * (1f - fz)
                    + g[i + n] * (1f - fx) * fz + g[i + n + 1] * fx * fz;

            float baseN = h / 200f;
            float r = Mathf.Sqrt(wx * wx + wy * wy);
            if (r > 10000f)
            {
                baseN = Mathf.Lerp(baseN, -0.2f, Mathf.Clamp01((r - 10000f) / 500f));
                if (r > 10490f)
                    baseN = Mathf.Lerp(baseN, -2f, Mathf.Clamp01((r - 10490f) / 10f));
            }
            return baseN;
        }

        // ── generation ──────────────────────────────────────────────────────

        private static float[] BuildGrid(int seed, int res, int droplets, out byte[] riverMask, out float[] waterLevel)
        {
            int n = res;
            var h = new float[n * n];
            float cell = (2f * HALF) / (n - 1);
            int s = seed * 7;

            // Crater ring-basins — the reference map's signature. Large floors
            // dip below the waterline and become circular lakes; the erosion
            // pass afterwards breaches rims into connecting channels.
            // v14 — the crater carpet thins to a FEW true craters; every basin
            // gets an angular radius WOBBLE so most read as natural lakes /
            // small oceans instead of compass circles. Rim lips soften to
            // shoreline bluffs; the remaining big ones keep their rim-arc
            // ranges (which now follow the wobbled outline).
            var cr = new Random(seed ^ 0x00C4A7E5);
            const int BIG = 4, MED = 8;
            const int CST = 8;   // crater record stride
            var crat = new float[(BIG + MED) * CST];
            for (int i = 0; i < BIG + MED; i++)
            {
                bool big = i < BIG;
                float R = big ? 520f + (float)cr.NextDouble() * 780f
                              : 200f + (float)cr.NextDouble() * 340f;
                float minC = big ? 1500f : 900f, maxC = big ? 8200f : 8800f;
                float ang = (float)(cr.NextDouble() * Math.PI * 2.0);
                float rad = minC + (float)cr.NextDouble() * (maxC - minC);
                crat[i * CST + 0] = Mathf.Cos(ang) * rad;
                crat[i * CST + 1] = Mathf.Sin(ang) * rad;
                crat[i * CST + 2] = R;
                crat[i * CST + 3] = (big ? 10f + R * 0.022f : 6f + R * 0.02f) * 0.35f;   // soft shoreline bluff
                crat[i * CST + 4] = big ? 8f + R * 0.028f : 5f + R * 0.022f;             // bowl depth
                crat[i * CST + 5] = (float)(cr.NextDouble() * 360.0);                     // arc center (deg)
                crat[i * CST + 6] = big ? 70f + (float)cr.NextDouble() * 120f : 0f;       // arc half-len (deg); med = none
                crat[i * CST + 7] = big ? 55f + (float)cr.NextDouble() * 85f : 0f;        // range amp (m)
            }

            // v16 — VOLCANO ISLANDS in the southern ocean (the reference
            // look): big freestanding cones rising from the sea — green
            // skirt at the shore, Mountain ring on the >80m slopes, AshLands
            // only at the caldera core (the biome pass paints the red top).
            var volc = DrawVolcanos(seed);

            // v14 — CORDILLERAS: curved range systems with ONE crest line
            // along their length. Gentle heading drift (±0.55 vs the old
            // ±1.1 zigzag) keeps each system a smooth arc; the crest profile
            // is computed from the along-length coordinate, so summits and
            // saddles line up WITH the range, not across it.
            // v33 — WANDERING CRESTS: short segments with per-system
            // curvature drift + jitter — a range is an ARC with kinks, never
            // a ruler bar; the world-rim 90° turn is replaced by a smooth
            // steer back toward the map (user mandate: no straight ranges,
            // no right angles).
            var sr = new Random(seed ^ 0x0051DE5);
            const int SYS = 5, SEGS = 24;
            var segs = new float[SYS * SEGS * 5];   // ax az bx bz cumLen
            int sc = 0;
            for (int sp = 0; sp < SYS; sp++)
            {
                float a0 = (float)(sr.NextDouble() * Math.PI * 2.0);
                float r0 = 1800f + (float)sr.NextDouble() * 4600f;
                float px = Mathf.Cos(a0) * r0, pz = Mathf.Sin(a0) * r0;
                float heading = (float)(sr.NextDouble() * Math.PI * 2.0);
                float curve = (float)(sr.NextDouble() - 0.5) * 0.30f;   // slow arc bias
                float cum = sp * 100000f;   // separate systems on the along axis
                for (int g = 0; g < SEGS; g++)
                {
                    float len = 320f + (float)sr.NextDouble() * 280f;
                    float nx2 = px + Mathf.Cos(heading) * len;
                    float nz2 = pz + Mathf.Sin(heading) * len;
                    float rr = Mathf.Sqrt(nx2 * nx2 + nz2 * nz2);
                    if (rr > 8300f)
                    {
                        float inward = Mathf.Atan2(-pz, -px);
                        heading += Mathf.DeltaAngle(heading * Mathf.Rad2Deg, inward * Mathf.Rad2Deg) * Mathf.Deg2Rad * 0.45f;
                        nx2 = px + Mathf.Cos(heading) * len;
                        nz2 = pz + Mathf.Sin(heading) * len;
                    }
                    segs[sc * 5 + 0] = px; segs[sc * 5 + 1] = pz;
                    segs[sc * 5 + 2] = nx2; segs[sc * 5 + 3] = nz2;
                    segs[sc * 5 + 4] = cum;
                    cum += len;
                    sc++;
                    px = nx2; pz = nz2;
                    heading += curve + (float)(sr.NextDouble() - 0.5) * 0.75f;
                }
            }
            int segCount = sc;

            // v19 — ALPINE MASSES (the Swiss Alps reference): broad elevated
            // blocks seeded ON the cordillera lines, so long ranges run
            // THROUGH real large mountain regions instead of standing alone;
            // the stream-power incision then trenches deep valleys into the
            // mass — the broad-bright-massif-with-dark-trenches DEM look.
            var mr2 = new Random(seed ^ 0x3A55E5);
            int massN = 2 + mr2.Next(2);
            var mass = new float[massN * 4];
            for (int i = 0; i < massN; i++)
            {
                int pick = mr2.Next(segCount);
                float t2 = (float)mr2.NextDouble();
                mass[i * 4 + 0] = segs[pick * 5] + (segs[pick * 5 + 2] - segs[pick * 5]) * t2;
                mass[i * 4 + 1] = segs[pick * 5 + 1] + (segs[pick * 5 + 3] - segs[pick * 5 + 1]) * t2;
                mass[i * 4 + 2] = 1100f + (float)mr2.NextDouble() * 900f;   // radius
                mass[i * 4 + 3] = 40f + (float)mr2.NextDouble() * 50f;      // v33 — real alpine lift
            }

            // The FBM fill is a pure per-cell function — parallel is safe and
            // still deterministic. Erosion below is serial (order matters).
            Parallel.For(0, n, z =>
            {
                for (int x = 0; x < n; x++)
                {
                    float wx = -HALF + x * cell;
                    float wz = -HALF + z * cell;
                    float r = Mathf.Sqrt(wx * wx + wz * wz);

                    float qx = wx + (Fbm(wx, wz, 1f / 1800f, 3, s + 401) - 0.5f) * 1300f;
                    float qz = wz + (Fbm(wx, wz, 1f / 1800f, 3, s + 402) - 0.5f) * 1300f;

                    float c = Fbm(qx, qz, 1f / 5200f, 4, s + 11);
                    c += (Fbm(qx, qz, 1f / 1400f, 3, s + 12) - 0.5f) * 0.52f;
                    c += 0.36f * (1f - Smooth(9400f, 10200f, r));        // land to the world edge
                    c -= Smooth(9500f, 10400f, r) * 0.7f;                // thin rim margin
                    c -= Smooth(0.55f, 0.75f, Fbm(qx, qz, 1f / 3400f, 3, s + 15)) * 0.62f;   // inland seas
                    c += Mathf.Exp(-(r * r) / (650f * 650f)) * 0.22f;    // gentle spawn dome
                    // Polar arcs (Ashlands south / DeepNorth north) get
                    // guaranteed land — their locations need ground.
                    float ashD = Mathf.Sqrt(wx * wx + (wz - 3810f) * (wz - 3810f)) - 11430f;
                    float dnD = Mathf.Sqrt(wx * wx + (wz + 3810f) * (wz + 3810f)) - 11430f;
                    c += Smooth(-150f, 250f, ashD) * 0.20f;
                    c += Smooth(-150f, 250f, dnD) * 0.18f;

                    // Bands tuned to disc hypsometry (~29% sea / 34% lowland /
                    // 22% upland / 15% mountain, ~200m peaks).
                    const float C0 = 0.63f;

                    // v9 — coastline articulation: crinkle the waterline only
                    // (bays, peninsulas, fjords). The bell around c≈C0 keeps
                    // interiors and sea floors untouched.
                    float coast = Mathf.Exp(-((c - C0) * (c - C0)) / (0.06f * 0.06f));
                    c += ((Fbm(qx, qz, 1f / 650f, 2, s + 21) - 0.5f) * 0.10f
                        + (Fbm(qx, qz, 1f / 160f, 2, s + 22) - 0.5f) * 0.05f) * coast;

                    float t = c - C0;
                    // v11 — HEIGHT BUDGET: anything over 80m becomes Mountain
                    // biome (snow, freeze, mountain spawns — vanilla's 0.4
                    // line, mirrored by our biome rule). Generic uplands stay
                    // UNDER that line; the RANGES spend the height budget.
                    // v17 — LOW, DARK land (the reference DEM): lowlands hug
                    // the sea, uplands compressed; ranges carry all drama.
                    float y = t < 0f ? SEA + t * 110f                    // shelf → sea floor
                        : t < 0.30f ? SEA + t * 60f                      // lowlands 30..48m
                        : SEA + 18f + (t - 0.30f) * 70f;                 // uplands top out ~66m

                    // Foothill texture only — noise can never cross the 80m
                    // Mountain line on its own; ranges own the peaks.
                    float m = Ridged(qx, qz, 1f / 2600f, 4, s + 13);
                    float mMask = Smooth(C0 + 0.26f, C0 + 0.42f, c) * Smooth(700f, 1700f, r);
                    y += m * m * mMask * 18f;

                    // v17 — mid-frequency ridged BONE for the erosion to bite:
                    // the dendritic filigree needs relief at gully scale.
                    y += (Ridged(qx, qz, 1f / 750f, 3, s + 16) - 0.45f) * 12f
                       * Smooth(C0 - 0.05f, C0 + 0.15f, c);

                    // v33 — JAGGED crest field: the distance query runs on
                    // WARPED coordinates (±130m torn edges — capsule outlines
                    // impossible) and the ridge width bulges/pinches along
                    // the crest, so massifs read irregular, never extruded.
                    float mwx = wx + (Fbm(wx, wz, 1f / 450f, 3, s + 301) - 0.5f) * 260f;
                    float mwz = wz + (Fbm(wx, wz, 1f / 450f, 3, s + 302) - 0.5f) * 260f;
                    float dmin = 1e9f;
                    float along = 0f;
                    for (int g2 = 0; g2 < segCount; g2++)
                    {
                        float ax = segs[g2 * 5], az = segs[g2 * 5 + 1];
                        float bx = segs[g2 * 5 + 2], bz = segs[g2 * 5 + 3];
                        float ex = bx - ax, ez = bz - az;
                        float segLen2 = ex * ex + ez * ez;
                        float tseg = ((mwx - ax) * ex + (mwz - az) * ez) / segLen2;
                        if (tseg < 0f) tseg = 0f; else if (tseg > 1f) tseg = 1f;
                        float ddx = mwx - (ax + ex * tseg), ddz = mwz - (az + ez * tseg);
                        float dd = ddx * ddx + ddz * ddz;
                        if (dd < dmin) { dmin = dd; along = segs[g2 * 5 + 4] + tseg * Mathf.Sqrt(segLen2); }
                    }
                    // v14 profile gaps stay (pass valleys); v33 lifts the
                    // budget into the 400-600m band: crests ~200m over the
                    // uplands, TRUE summits spike another ~240m, alpine
                    // masses stack the rest.
                    float prof = Fbm(along, 811f, 1f / 1050f, 2, s + 77);
                    float shaped = Smooth(0.32f, 0.58f, prof);
                    float summits = Smooth(0.72f, 0.93f, Fbm(along, 407f, 1f / 640f, 1, s + 79));
                    float widthMod = 0.55f + 0.9f * Fbm(along, 133f, 1f / 800f, 2, s + 85);
                    float ridgeW = 390f * widthMod;
                    float ridgeT = Mathf.Exp(-dmin / (ridgeW * ridgeW));
                    y += ridgeT * (shaped * 200f + summits * 240f) * Smooth(1100f, 2200f, r);

                    // v19 — alpine mass lift, warped edge.
                    for (int mi = 0; mi < mass.Length; mi += 4)
                    {
                        float dxm = wx - mass[mi], dzm = wz - mass[mi + 1];
                        float Rm = mass[mi + 2];
                        float limM = Rm * 1.4f;
                        if (dxm > limM || dxm < -limM || dzm > limM || dzm < -limM) continue;
                        float dm = Mathf.Sqrt(dxm * dxm + dzm * dzm)
                                 / (Rm * (0.85f + 0.3f * Fbm(wx, wz, 1f / 900f, 2, s + 19)));
                        if (dm > 1.15f) continue;
                        y += mass[mi + 3] * (1f - Smooth(0.55f, 1.1f, dm));
                    }

                    y += (Fbm(qx, qz, 1f / 240f, 2, s + 14) - 0.5f) * 9f;

                    for (int ci = 0; ci < crat.Length; ci += 8)
                    {
                        float dx = wx - crat[ci], dz = wz - crat[ci + 1], R = crat[ci + 2];
                        float lim = R * 2.3f;
                        if (dx > lim || dx < -lim || dz > lim || dz < -lim) continue;
                        // v14 — angular radius WOBBLE (wrap-continuous around
                        // the circle): basins become lobed natural lakes, not
                        // compass circles. Rim arcs follow the wobbled shore.
                        float theta0 = Mathf.Atan2(dz, dx);
                        float wob = 1f + 0.38f * (Fbm(Mathf.Cos(theta0) * 2.7f + ci, Mathf.Sin(theta0) * 2.7f, 1f, 2, s + 66) - 0.5f) * 2f;
                        float d = Mathf.Sqrt(dx * dx + dz * dz) / (R * wob);
                        if (d > 1.6f) continue;
                        float floorT = 1f - Smooth(0.72f, 1.02f, d);     // flat bowl → 0 at rim
                        float rimT = Mathf.Exp(-((d - 1f) * (d - 1f)) / (0.18f * 0.18f));
                        // Rugged floors — real crater floors aren't flat, and
                        // vanilla's location flatness check must FAIL here so
                        // spawns stop landing inside pits.
                        y += -crat[ci + 4] * floorT * (1f - 0.3f * d * d) + crat[ci + 3] * rimT
                           + floorT * (Fbm(wx, wz, 1f / 70f, 2, s + 55) - 0.5f) * 5f;

                        // v9 — rim range arc: ridge chain along part of the
                        // rim, amp varied along the arc (peaks and passes).
                        float arcHalf = crat[ci + 6];
                        if (arcHalf > 0f && rimT > 0.02f)
                        {
                            float theta = Mathf.Atan2(dz, dx) * Mathf.Rad2Deg;
                            float delta = Mathf.Abs(Mathf.DeltaAngle(theta, crat[ci + 5])) / arcHalf;
                            if (delta < 1.1f)
                            {
                                float inArc = 1f - Smooth(0.75f, 1.05f, delta);
                                float along2 = theta * Mathf.Deg2Rad * R;     // arc-length coordinate
                                // v11 — squared chain profile: passes drop low,
                                // summits spike, instead of one rounded wall.
                                float cn = Fbm(along2, ci * 173f, 1f / 240f, 2, s + 81);
                                float chain = 0.35f + 1.05f * cn * cn;
                                // Occasional true PEAKS (~600m apart, top ~15%
                                // of the arc) — spires rising well above the
                                // chain so ranges get summits, not just crest.
                                float peaks = Smooth(0.74f, 0.94f, Fbm(along2, ci * 91f, 1f / 620f, 1, s + 83));
                                y += crat[ci + 7] * rimT * inArc * (chain + 0.85f * peaks);
                            }
                        }
                    }

                    // v14 — volcanos: steep cone, caldera bite at the summit.
                    for (int vi = 0; vi < volc.Length; vi += 4)
                    {
                        float dx = wx - volc[vi], dz = wz - volc[vi + 1];
                        float Rc = volc[vi + 2];
                        if (dx > Rc || dx < -Rc || dz > Rc || dz < -Rc) continue;
                        float d = Mathf.Sqrt(dx * dx + dz * dz) / Rc;
                        if (d > 1f) continue;
                        float cone = Mathf.Pow(1f - d, 1.35f) * volc[vi + 3];
                        float caldera = (1f - Smooth(0.10f, 0.22f, d)) * volc[vi + 3] * 0.45f;
                        y += cone - caldera;
                    }

                    h[z * n + x] = y;
                }
            });

            Erode(h, n, droplets, seed ^ 0x51ED);
            riverMask = CarveRivers(h, n, seed, out waterLevel);
            return h;
        }

        // W4.1/v15 — OUR biome placement, progression-aware: a real randomized
        // Meadows blob at spawn; BF/swamp/mountain adjacent; Plains rare near
        // spawn and common outward (gradient, not a gate); Mistlands moderate
        // outer pockets; AshLands/DeepNorth are RARE chunky pockets with
        // south/north bias — no more polar band swaths. Swamps hug water
        // margins, mountains are the >80m line. Codes match BIOME_CODES.
        // MIRRORED offline (Tools/FiresWorldGen BuildBiomes) — keep in sync.
        // v20 — ONE volcano draw for every consumer (BuildGrid relief,
        // BuildBiomes caldera halo, offline mirror). Size ladder guarantees
        // contrast: the first cone is the big stratovolcano (R 780..1200m),
        // the rest stay small (R 300..630m) — no seed can roll near-twins.
        // Placement redraws until the cone clears every earlier one, so
        // calderas never overlap into mush.
        private static float[] DrawVolcanos(int seed)
        {
            var vc = new Random(seed ^ 0x70CA20);
            int volcN = 2 + vc.Next(2);
            var volc = new float[volcN * 4];
            for (int i = 0; i < volcN; i++)
            {
                float rc = i == 0 ? 780f + (float)vc.NextDouble() * 420f
                                  : 300f + (float)vc.NextDouble() * 330f;
                float x = 0f, z = 0f;
                for (int t = 0; t < 12; t++)
                {
                    x = (float)(vc.NextDouble() * 8400.0 - 4200.0);
                    z = -8300f - (float)vc.NextDouble() * 900f;
                    bool clear = true;
                    for (int j = 0; j < i; j++)
                    {
                        float dx = x - volc[j * 4], dz = z - volc[j * 4 + 1];
                        float need = (rc + volc[j * 4 + 2]) * 1.15f;
                        if (dx * dx + dz * dz < need * need) { clear = false; break; }
                    }
                    if (clear) break;
                }
                volc[i * 4 + 0] = x;
                volc[i * 4 + 1] = z;
                volc[i * 4 + 2] = rc;
                volc[i * 4 + 3] = 90f + rc * 0.12f + (float)vc.NextDouble() * 30f;
            }
            return volc;
        }

        private static byte[] BuildBiomes(float[] h, int n, float half, int seed, bool generated)
        {
            const float R = 10000f;
            int s = seed * 7;
            float cellM = (2f * half) / (n - 1);
            var b = new byte[n * n];

            // Spawn blob nominal radius — per-world randomized (650..1300m).
            float spawnR = 650f + Hash01(seed, 331, 777) * 650f;

            // Volcano caldera cores (generated worlds only): AshLands paints
            // ONLY the summit crater zone — the >80m slopes become the
            // Mountain ring naturally and the shore skirt stays green (the
            // reference's volcano-island layering). Positions re-derived from
            // the same seed/draws as BuildGrid's volcano pass.
            float[] volc = null;
            if (generated)
            {
                volc = DrawVolcanos(seed);
            }

            Parallel.For(0, n, z =>
            {
                for (int x = 0; x < n; x++)
                {
                    int i = z * n + x;
                    float wx = -half + x * cellM;
                    float wz = -half + z * cellM;
                    float r = Mathf.Sqrt(wx * wx + wz * wz);
                    float hh = h[i];

                    // v23 — VANILLA'S LADDER (WorldGenerator.GetBiome:620-643),
                    // bands scaled to our R, first match wins exactly like
                    // vanilla: polar Ash/DN caps (offset circles + WorldAngle
                    // wobble), Mountain by height (0.4·200 = 80m — same
                    // number), Swamp low-ground pockets, Mistlands rare outer
                    // pockets, Plains uncommon mid-ring, BlackForest the
                    // DEFAULT beyond mid-world, Meadows the core default.
                    // Hierarchy: meadows/BF/swamp/mountain majority; plains
                    // uncommon (0.52); mistlands rarer (0.62); ash/DN polar.
                    float k = R / 10000f;
                    float ang = Mathf.Sin(Mathf.Atan2(wx, wz) * 20f) * 100f * k;
                    float dzS = wz - 4000f * k, dzN = wz + 4000f * k;

                    byte v;
                    if (hh < 22f) v = 8;                                                   // Ocean (deep)
                    // Caps use vanilla's TRUE radius (12000 in a 10000-world):
                    // the offset circle only exceeds it in the far polar
                    // crescent (boundary at |z| ≈ 8000k). Scaling the radius
                    // down pulled the caps inside the landmass and they ate
                    // half the map.
                    else if (Mathf.Sqrt(wx * wx + dzS * dzS) > 12000f * k + ang) v = 6;    // AshLands (south crescent)
                    else if (Mathf.Sqrt(wx * wx + dzN * dzN) > 12000f * k + ang)
                        v = hh > 80f ? (byte)3 : (byte)7;                                  // DeepNorth (north crescent)
                    else if (hh > 80f) v = 3;                                              // Mountain
                    else if (Fbm(wx, wz, 1f / 1000f, 2, s + 51) > 0.53f
                             && r > 1800f * k && r < 8000f * k && hh < 52f) v = 2;         // Swamp (low wet ground)
                    else if (Fbm(wx, wz, 1f / 1000f, 2, s + 52) > 0.62f
                             && r > 6000f * k + ang && r < 9900f * k) v = 5;               // Mistlands (rare outer)
                    else if (Fbm(wx, wz, 1f / 1000f, 2, s + 53) > 0.58f
                             && r > 3000f * k + ang && r < 8500f * k && hh < 70f) v = 4;   // Plains (uncommon mid)
                    else if ((Fbm(wx, wz, 1f / 1000f, 2, s + 54) > 0.40f && r > 600f * k + ang && r < 6000f * k)
                             || r > 5000f * k + ang) v = 1;                                // BlackForest (outer default)
                    else v = 0;                                                            // Meadows (core default)

                    // Volcano caldera cores — red only at the summit crater;
                    // the >80m slopes are already Mountain, the skirt green.
                    if (generated && volc != null && v != 8)
                    {
                        for (int vi = 0; vi < volc.Length; vi += 4)
                        {
                            float dxv = wx - volc[vi], dzv = wz - volc[vi + 1];
                            float hr = volc[vi + 2] * 0.45f;
                            if (dxv * dxv + dzv * dzv < hr * hr) { v = 6; break; }
                        }
                    }

                    // Spawn blob: a REAL meadows region around the start —
                    // per-world size, warped edge. Never overrides ocean or
                    // mountain, so coastline and range drama survive.
                    if (v != 8 && v != 3
                        && r < spawnR + (Fbm(wx, wz, 1f / 700f, 2, s + 37) - 0.5f) * spawnR * 0.9f)
                        v = 0;
                    b[i] = v;
                }
            });
            return b;
        }

        // v4 — REAL RIVERS. The drainage fill surface W descends monotonically
        // along every flow path, so carving relative to it gives each river a
        // TRUE DOWNHILL BED: headwaters high, mouth below the 30m waterline.
        // Channels get width + depth by catchment and crisp stamped banks; OUR
        // water (chained river sources on the FiresLiquid engine) runs the
        // course. The mask drives detail-noise suppression AND relay water
        // placement. MIRRORED offline (Tools/FiresWorldGen) — keep in sync.
        private static byte[] CarveRivers(float[] h, int n, int seed, out float[] waterLevel)
        {
            var w = PriorityFloodFill(h, n);
            int ms = seed * 7 + 977;
            // Resolution independence: the hydrology is authored at the 2048
            // reference grid. RS rescales every cell-space frequency/distance
            // and cellA converts flow accumulation (a CELL COUNT) into physical
            // drainage AREA (m²) — so rivers, valleys and erosion are the SAME
            // physical features whether a cell is 10m or 2m. RS==1 at n=2048.
            float cellM = (2f * _half) / (n - 1);
            float cellA = cellM * cellM;
            float RS = 2047f / (n - 1);

            // v10 — CONVERGENT drainage. On broad uniform slopes pure
            // steepest-descent forms SHEETS of parallel streams ("30 rivers
            // in a line"). Route the flow over a corrugated copy of the water
            // table: coherent valley-attractor noise (~700m wavelength, ~7m
            // deep) gathers neighbouring columns into common winding trunks.
            // The corrugation only biases WHERE water runs — beds, depths,
            // water levels and estuaries still read the REAL table.
            // v12 — the corrugation is DOMAIN-WARPED so trough lines curve:
            // straight troughs made tributaries join trunks at dead right
            // angles (the comb pattern in flats/swamps); curved troughs give
            // oblique, dendritic joins.
            var wr = new float[n * n];
            for (int z2 = 0; z2 < n; z2++)
            {
                for (int x2 = 0; x2 < n; x2++)
                {
                    int i2 = z2 * n + x2;
                    float wx2 = x2 + (Fbm(x2, z2, RS / 210f, 2, ms + 8) - 0.5f) * (22f / RS);
                    float wz2 = z2 + (Fbm(x2, z2, RS / 210f, 2, ms + 9) - 0.5f) * (22f / RS);
                    float vr = 1f - Math.Abs(2f * Fbm(wx2, wz2, RS / 68f, 3, ms + 7) - 1f);
                    wr[i2] = w[i2] - vr * 13f;   // v28: deeper convergence trough — parallel streams on broad slopes merge into common trunks before extraction
                }
            }
            wr = PriorityFloodFill(wr, n);   // re-fill so corrugation pits don't strand flow

            // v30 — flow structure FIRST (D8 single-flow tree on the
            // corrugated fill surface), and EVERYTHING downstream reads it:
            // the v18 incision previously used the braided multi-flow
            // accumulation and cut every corrugation groove 3-14m deep —
            // parallel dry slit channels beside the painted rivers
            // (live-cache-proven; unaffected by any mask/paint fix because
            // the grooves are in the TERRAIN, not the water field).
            var dir = new int[n * n];
            var order = new int[n * n];
            for (int i2 = 0; i2 < n * n; i2++) { dir[i2] = -1; order[i2] = i2; }
            // v31 — WANDERING DESCENT: pure steepest-descent locks onto one
            // of 8 compass headings across smooth ground — kilometer-ruler
            // rivers (map-proven). Choose among ALL lower neighbors weighted
            // by slope² through a deterministic cell hash: deep troughs still
            // dominate, open slopes wander and the lines meander naturally.
            var wjs = new int[8];
            var wws = new float[8];
            for (int z2 = 1; z2 < n - 1; z2++)
            {
                for (int x2 = 1; x2 < n - 1; x2++)
                {
                    int i2 = z2 * n + x2;
                    int cand = 0; float tot = 0f;
                    for (int e = 0; e < 8; e++)
                    {
                        int j = (z2 + RIV_DZ[e]) * n + (x2 + RIV_DX[e]);
                        float drop = wr[i2] - wr[j];
                        if (drop <= 0f) continue;
                        float wgt = drop / RIV_DL[e];
                        wgt *= wgt;
                        wjs[cand] = j; wws[cand] = wgt; tot += wgt; cand++;
                    }
                    if (cand == 0) continue;
                    float pick = WanderHash01(x2, z2, seed) * tot;
                    int sel = cand - 1;
                    for (int k3 = 0; k3 < cand; k3++)
                    { pick -= wws[k3]; if (pick <= 0f) { sel = k3; break; } }
                    dir[i2] = wjs[sel];
                }
            }
            var wrKeys = (float[])wr.Clone();
            Array.Sort(wrKeys, order);                      // ascending fill surface
            var acc1 = new float[n * n];
            for (int i2 = 0; i2 < n * n; i2++) acc1[i2] = 1f;
            for (int k2 = n * n - 1; k2 >= 0; k2--)         // upstream → downstream
            {
                int i2 = order[k2];
                if (dir[i2] >= 0) acc1[dir[i2]] += acc1[i2];
            }

            // v18 — STREAM-POWER INCISION on the TREE: the drainage draws
            // itself into the surface along the single-flow stems; dissection
            // provinces keep plains smooth while highlands dissect hard.
            for (int z2 = 0; z2 < n; z2++)
            {
                for (int x2 = 0; x2 < n; x2++)
                {
                    int i2 = z2 * n + x2;
                    if (h[i2] <= SEA + 0.5f) continue;
                    float dissect = 0.35f + 1.0f * Smooth(0.35f, 0.65f, Fbm(x2, z2, RS / 440f, 2, ms + 11));
                    float cut = Math.Min(14f, 1.35f * Mathf.Log(1f + acc1[i2] * cellA / 3157f)) * dissect;
                    float floorY = Math.Max(SEA - 0.5f, w[i2] - 12f);
                    float target = Math.Max(floorY, h[i2] - cut);
                    if (target < h[i2]) h[i2] = target;
                }
            }

            const float THR = 5000f;        // v29: single-flow catchment (cells) — tree acc concentrates the whole basin on the stem, so the old multi-flow 780 lit every corrugation trough
            var mask = new byte[n * n];
            var hOrig = (float[])h.Clone();

            // v22 — THE RIVERHEIM GRAMMAR (Tools/FIRES_RIVER_ENGINE_RIVERHEIM.md).
            // Rivers are no longer STAMPED into independent terrain; the
            // terrain around them is SOLVED FROM the rivers:
            //   1. real dimensions — width from a discharge power law
            //      (140·q^0.4−6, clamped ≤70m), depth from width (~7:1 remap,
            //      clamped 2.5-10m) — river-scale channels;
            //   2. thalweg water level = the fill table (monotone downstream
            //      by the fill's construction) — bed and water CANNOT disagree
            //      (kills the v21 wCap machinery outright);
            //   3. a bounded multi-source Dijkstra grows a VALLEY surface
            //      outward from every river cell at a gentle incline and
            //      shaves approach bumps to it (capped, so gorge walls stay);
            //   4. the channel is a BiasedRational cross-section blended in
            //      with a cubic SmoothMin — banks merge continuously into the
            //      terrain, never terrace, and only ever LOWER ground.
            // Meander comes from the corrugated routing; mouths flare seaward.

            var widthOf = new float[n * n];
            var seedIdx = new int[n * n];
            for (int i2 = 0; i2 < seedIdx.Length; i2++) seedIdx[i2] = -1;

            // v29 — SINGLE-STEM EXTRACTION from the tree built above (one
            // outflow per cell → braids structurally cannot exist).
            var riverCells = new List<int>(16384);
            // RIVER REWORK — resolution-independent hydraulic geometry. Width is
            // a power law of PHYSICAL drainage area A = acc1·cell² (m²), never a
            // cell count, so a river is the same width on a 10m or a 2m grid.
            // Spec: brook 2m at the 0.06km² onset, common river 6-8m, biggest
            // trunks clamp at 14m — 10m+ is already a big Valheim river. On the
            // 2m grid this whole 2-14m range is finally REPRESENTABLE; on the
            // old 10m grid everything under a cell just aliased away.
            const float AREA_ONSET = 60000f;    // m² — below = ephemeral, no channel
            const float WK = 0.039f, WP = 0.36f;
            for (int k2 = n * n - 1; k2 >= 0; k2--)         // upstream-first order
            {
                int i = order[k2];
                int z = i / n, x = i - z * n;
                if (x < 2 || x >= n - 2 || z < 2 || z >= n - 2) continue;
                float area = acc1[i] * cellA;
                if (area <= AREA_ONSET || hOrig[i] <= 8f) continue;
                float width = WK * (float)Math.Pow(area, WP);
                if (width < 2f) continue;                              // below a brook — no channel
                width = Math.Min(14f, width);
                width *= 1f + 0.5f * Smooth(6f, 1f, w[i] - SEA);       // mouth flare
                widthOf[i] = width;
                riverCells.Add(i);
            }

            // v30 — PARALLEL-STEM SUPPRESSION. Corrugation grooves run stems
            // side by side at offset levels through one dug lowland; the
            // higher one's water then edges the shared floor (transect-proven
            // 38.27 beside 30.00, 90m apart). Bigger catchment rules: a stem
            // cell dies when a ≥2.5× stem passes within ~80m. One river's own
            // stations never suppress each other (adjacent acc ratio ≈ 1).
            {
                const int SUP_R = 8;
                var byAcc = riverCells.ToArray();
                Array.Sort(byAcc, (a, b) => acc1[b].CompareTo(acc1[a]));
                var keptMark = new bool[n * n];
                var keptList = new List<int>(riverCells.Count);
                for (int k2 = 0; k2 < byAcc.Length; k2++)
                {
                    int i = byAcc[k2];
                    int cz2 = i / n, cx2 = i - cz2 * n;
                    bool dead = false;
                    for (int dz2 = -SUP_R; dz2 <= SUP_R && !dead; dz2++)
                    {
                        int zz = cz2 + dz2;
                        if (zz < 0 || zz >= n) continue;
                        for (int dx2 = -SUP_R; dx2 <= SUP_R && !dead; dx2++)
                        {
                            int xx = cx2 + dx2;
                            if (xx < 0 || xx >= n) continue;
                            int j = zz * n + xx;
                            if (keptMark[j] && acc1[j] >= acc1[i] * 2.5f) dead = true;
                        }
                    }
                    if (dead) { widthOf[i] = 0f; continue; }
                    keptMark[i] = true;
                    keptList.Add(i);
                }
                riverCells = keptList;
            }

            // v29 — LEVELS ON THE TREE: the never-above-banks INVARIANT.
            // Walk every cell upstream→downstream; a stem station's level =
            // min(level arriving from upstream, local ORIGINAL floor (3×3
            // min) − 0.9), floored at SEA; the level then rides the flow
            // chain down (through pruned gaps too). Monotone downstream by
            // construction; ≥0.9m below the ground beside it by construction
            // — water standing above a bank is impossible in the data. Where
            // the floor steps down, the surface steps with it (falls).
            {
                var lvlIn = new float[n * n];
                for (int i2 = 0; i2 < n * n; i2++) lvlIn[i2] = float.MaxValue;
                float cellM0 = (2f * _half) / (n - 1);
                for (int k2 = n * n - 1; k2 >= 0; k2--)
                {
                    int i = order[k2];
                    float outLvl = lvlIn[i];
                    if (widthOf[i] > 0f)
                    {
                        // Floor scan radius = the PAINT radius (1.6×half):
                        // the 3×3 scan let a stem on a rise hold 38m water
                        // while its own paint disk reached a natural 26m
                        // groove 50m away (transect-proven). Whatever ground
                        // the stem can wet must stand above its level.
                        int cz2 = i / n, cx2 = i - cz2 * n;
                        int rr2 = Math.Max(1, (int)Math.Ceiling(0.8f * widthOf[i] / cellM0));
                        float gmin = hOrig[i];
                        int z0 = Math.Max(0, cz2 - rr2), z1 = Math.Min(n - 1, cz2 + rr2);
                        int x0 = Math.Max(0, cx2 - rr2), x1 = Math.Min(n - 1, cx2 + rr2);
                        for (int zz = z0; zz <= z1; zz++)
                            for (int xx = x0; xx <= x1; xx++)
                            { float gv = hOrig[zz * n + xx]; if (gv < gmin) gmin = gv; }
                        float lvl = Math.Min(outLvl, gmin - 0.9f);
                        if (lvl < SEA) lvl = SEA;
                        w[i] = lvl;
                        outLvl = lvl;
                    }
                    if (outLvl == float.MaxValue) continue;
                    int dn = dir[i];
                    if (dn >= 0 && lvlIn[dn] > outLvl) lvlIn[dn] = outLvl;
                }
            }

            // Bounded Dijkstra: geodesic distance (m) from the nearest river
            // cell, carrying that seed. Expansion stops once the valley
            // surface rises past the terrain (no further influence possible).
            const float INCLINE = 0.45f;     // valley wall grade away from the river
            var rDist = new float[n * n];
            for (int i2 = 0; i2 < rDist.Length; i2++) rDist[i2] = float.MaxValue;
            var heap = new RiverHeap(riverCells.Count * 4);
            for (int k2 = 0; k2 < riverCells.Count; k2++)
            {
                int i = riverCells[k2];
                rDist[i] = 0f;
                seedIdx[i] = i;
                heap.Push(0f, i);
            }
            while (heap.Pop(out float dcur, out int cur))
            {
                if (dcur > rDist[cur] + 0.001f) continue;    // stale entry
                int cz2 = cur / n, cx2 = cur - cz2 * n;
                int sd = seedIdx[cur];
                float swl = w[sd];
                float sw = widthOf[sd];
                for (int e = 0; e < 8; e++)
                {
                    int nx2 = cx2 + RIV_DX[e], nz2 = cz2 + RIV_DZ[e];
                    if (nx2 < 1 || nx2 >= n - 1 || nz2 < 1 || nz2 >= n - 1) continue;
                    int j = nz2 * n + nx2;
                    float nd = dcur + cellM * RIV_DL[e];
                    if (nd >= rDist[j]) continue;
                    if (swl + nd * INCLINE > hOrig[j] + 4f && nd > sw) continue;
                    rDist[j] = nd;
                    seedIdx[j] = sd;
                    heap.Push(nd, j);
                }
            }

            // Solve → carve, one pass over every reached cell.
            var paintLvl = new float[n * n];
            for (int i2 = 0; i2 < paintLvl.Length; i2++) paintLvl[i2] = float.MaxValue;
            for (int i2 = 0; i2 < rDist.Length; i2++)
            {
                int sd = seedIdx[i2];
                if (sd < 0) continue;
                float width = widthOf[sd];
                // Depth keyed to the 2-14m width range, dug DEEP: a 2m brook is
                // 1.5m (wadeable, real thalweg), a 7m river ~4.2m, a 14m trunk
                // 8m (swimmable). The pool-riffle term below then modulates it.
                float depth = RemapClamp(width, 2f, 14f, 1.5f, 8f);
                float wl = w[sd];
                // v34 — POOL-RIFFLE bed (fluvial reference; SimpleHydrology's
                // stream+pool coupling done closed-form): a MONOTONE bed has
                // no basins, so the pour settle hugged it and drained every
                // inland river dry (audit: 3,958 wet cells on a whole map).
                // Shallow sills every ~60-160m dam deep pools; settled water
                // fills the chain — pools hold, sills read as rapids.
                {
                    int sdz = sd / n, sdx = sd - sdz * n;
                    float poolT = Smooth(0.30f, 0.70f,
                        Fbm(sdx * cellM, sdz * cellM, 1f / 110f, 2, ms + 87));
                    depth = Mathf.Lerp(1.5f, depth, poolT);
                }
                float half = Math.Max(1.5f, width * 0.5f);

                // irregular natural banks — noise widens/narrows the distance
                // outside the wet core, never at the thalweg.
                int zC = i2 / n, xC = i2 - zC * n;
                float bankNoise = (Fbm(xC, zC, RS / 9f, 2, ms + 21) - 0.5f) * 6f;
                float d = rDist[i2];
                float dN = Math.Max(0f, d + bankNoise * Smooth(0.3f * half, 1.4f * half, d));

                // channel: BiasedRational — −depth at the thalweg, asymptotic
                // linear banks at slope `steep`; UNCAPPED so the river always
                // gets through (bumps on the line become incised gorges).
                float steep = RemapClamp(hOrig[i2], 3f, 20f, 2.9f, 4.4f);
                float nrm = dN / depth;
                float wBias = Math.Max(0.001f, (half / depth) * (steep * half / depth - 1f));
                float profile = wl + depth * ((steep * nrm * nrm) / (nrm + wBias) - 1f);
                float chan = SmoothMinCubic(h[i2], profile, 3f);

                // valley: shave approach bumps toward the solved surface,
                // capped at 24m of cut so gorge/canyon walls survive.
                // v23 — deeper cap + wider blend: v22 valleys were too shy
                // to read as river corridors at map scale.
                float valley = Math.Max(wl + d * INCLINE, h[i2] - 24f);
                float vall = SmoothMinCubic(h[i2], valley, 8f);

                float outH = Math.Min(chan, vall);
                if (outH < h[i2]) h[i2] = outH;
                // (v24 containment crest deleted — levels sit ≥0.9m under the
                // original floor by construction, so banks are always natural
                // ground standing above the water; nothing to raise, ever.)

                // v30 — EVERYTHING the carve dug below the waterline inside
                // the trench corridor is WET BED, at the LOWEST level of any
                // stem claiming it (transect-proven: a tributary's Dijkstra
                // territory painted a trunk's trench 8m above the trunk's own
                // water — min-write makes the trunk rule shared trenches; the
                // trib steps down into it at its mouth, a natural fall).
                if (dN <= half * 1.6f && h[i2] < wl - 0.25f)
                {
                    mask[i2] = 1;
                    if (paintLvl[i2] > wl) paintLvl[i2] = wl;
                }
                else if (dN <= half * 0.9f) mask[i2] = 1;
                else if (dN <= half * 1.6f && mask[i2] == 0) mask[i2] = 2;
            }

            // Apply the min-paint: the trench takes the lowest claiming
            // stem's level (raise-only vs the cell's own ground).
            for (int i2 = 0; i2 < paintLvl.Length; i2++)
                if (paintLvl[i2] != float.MaxValue && w[i2] != paintLvl[i2]) w[i2] = paintLvl[i2];

            // v26 — mark the SPINE (extraction cells) distinctly: mask 3 =
            // spine (wet by construction, dN=0). Spring spots emit ONLY spine
            // cells so every pour pins dead-center of the channel instead of
            // at a bank edge (off-center pours left the far bank dry and
            // pinned tilted levels).
            for (int k2 = 0; k2 < riverCells.Count; k2++) mask[riverCells[k2]] = 3;

            // v27 — HEADWATER POOLS: every river ends upstream in a rounded
            // irregular pool instead of a dead stop. A source = spine cell
            // with no higher-level spine neighbour; carve a wobbled bowl at
            // its level and flood it.
            for (int k2 = 0; k2 < riverCells.Count; k2++)
            {
                int i = riverCells[k2];
                int cz2 = i / n, cx2 = i - cz2 * n;
                bool source = true;
                for (int e = 0; e < 8 && source; e++)
                {
                    int j = (cz2 + RIV_DZ[e]) * n + (cx2 + RIV_DX[e]);
                    if (widthOf[j] > 0f && w[j] > w[i] + 0.02f) source = false;
                }
                if (!source) continue;
                float wl2 = w[i];
                float pr = Math.Max(9f, widthOf[i] * 1.4f);
                int span = (int)(pr / cellM) + 2;
                for (int dz2 = -span; dz2 <= span; dz2++)
                {
                    for (int dx2 = -span; dx2 <= span; dx2++)
                    {
                        int nx3 = cx2 + dx2, nz3 = cz2 + dz2;
                        if (nx3 < 1 || nx3 >= n - 1 || nz3 < 1 || nz3 >= n - 1) continue;
                        int j = nz3 * n + nx3;
                        float rr = Mathf.Sqrt(dx2 * dx2 + dz2 * dz2) * cellM;
                        float rEff = pr * (1f + (Fbm(nx3, nz3, RS / 11f, 2, ms + 33) - 0.5f) * 0.7f);
                        if (rr > rEff) continue;
                        float t = rr / rEff;
                        float bed = wl2 - 2.6f * (1f - t * t);
                        float carved = SmoothMinCubic(h[j], bed, 3f);
                        if (carved < h[j]) h[j] = carved;
                        if (widthOf[j] <= 0f)
                        {
                            if (rr <= rEff * 0.8f)
                            {
                                if (mask[j] == 0 || mask[j] == 2) mask[j] = 1;
                                if (w[j] < wl2) w[j] = wl2;
                            }
                            else if (mask[j] == 0) mask[j] = 2;
                        }
                    }
                }
            }

            // v18 — thermal relaxation: two talus passes shed over-steep
            // droplet spikes and incision walls the way real slopes shed
            // scree (~41° limit at these cells). Carved channel walls sit
            // under the limit and survive.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int z2 = 1; z2 < n - 1; z2++)
                {
                    for (int x2 = 1; x2 < n - 1; x2++)
                    {
                        int i2 = z2 * n + x2;
                        float hc = h[i2];
                        int lo = i2;
                        float hl = hc;
                        if (h[i2 - 1] < hl) { hl = h[i2 - 1]; lo = i2 - 1; }
                        if (h[i2 + 1] < hl) { hl = h[i2 + 1]; lo = i2 + 1; }
                        if (h[i2 - n] < hl) { hl = h[i2 - n]; lo = i2 - n; }
                        if (h[i2 + n] < hl) { hl = h[i2 + n]; lo = i2 + n; }
                        float d2 = hc - hl;
                        const float TALUS = 9f;
                        if (d2 > TALUS)
                        {
                            float move = (d2 - TALUS) * 0.5f;
                            h[i2] -= move;
                            h[lo] += move;
                        }
                    }
                }
            }

            // v8 — the stored water level is BANK-REFERENCED: min(fill
            // surface, ORIGINAL terrain). The raw fill surface stands at each
            // basin's rim pour point — on relief with big closed basins (the
            // Craters map) that "water table" sat metres above kilometres of
            // lowland and flooded the world. Clamped to the pre-carve ground
            // it can never stand above the banks; it is the SPRING FILL LEVEL
            // for the river sources, and the liquid sim owns actual pooling.
            // Sea snap first: the fill's +0.0002/cell tie-breaker accumulates
            // to +0.2..0.4m across the ocean flat.
            // v22 — river cells KEEP the monotone fill level (thalweg = fill
            // table; the profile carved the bed exactly `depth` below it, so
            // fill depth is correct by construction). Non-river cells keep
            // the v8 bank clamp for legacy consumers.
            for (int i2 = 0; i2 < n * n; i2++)
            {
                if (w[i2] < SEA + 0.5f) w[i2] = SEA;
                else if (mask[i2] == 0)
                {
                    if (w[i2] > hOrig[i2]) w[i2] = hOrig[i2];
                    // v24 — ponds need REAL depth. Every damp depression in
                    // the fill table was emitting a water blob lying on the
                    // grass (the meadow screenshots); standing water outside
                    // rivers now requires ≥1.2m of fill.
                    if (w[i2] - h[i2] < 1.2f && w[i2] > h[i2]) w[i2] = h[i2];
                }
            }
            waterLevel = w;
            return mask;
        }

        // ── v22 river-solve helpers (mirrored offline — keep identical) ─────
        private static readonly int[] RIV_DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] RIV_DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private static readonly float[] RIV_DL = { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f };

        private static float RemapClamp(float v, float fromA, float fromB, float toA, float toB)
        {
            float t = (v - fromA) / (fromB - fromA);
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return toA + (toB - toA) * t;
        }

        // Cubic smooth minimum — the anti-terracing keystone: the carve only
        // ever lowers terrain and its banks blend continuously (k = blend m).
        private static float SmoothMinCubic(float a, float b, float k)
        {
            float t = Math.Max(k - Math.Abs(a - b), 0f) / k;
            return Math.Min(a, b) - t * t * t * k * (1f / 6f);
        }

        // Minimal binary min-heap (net48 has no PriorityQueue).
        private sealed class RiverHeap
        {
            private float[] _p; private int[] _v; private int _count;
            public RiverHeap(int cap) { if (cap < 64) cap = 64; _p = new float[cap]; _v = new int[cap]; }
            public void Push(float pri, int val)
            {
                if (_count == _p.Length)
                {
                    System.Array.Resize(ref _p, _count * 2);
                    System.Array.Resize(ref _v, _count * 2);
                }
                int i = _count++;
                while (i > 0)
                {
                    int par = (i - 1) >> 1;
                    if (_p[par] <= pri) break;
                    _p[i] = _p[par]; _v[i] = _v[par]; i = par;
                }
                _p[i] = pri; _v[i] = val;
            }
            public bool Pop(out float pri, out int val)
            {
                if (_count == 0) { pri = 0f; val = 0; return false; }
                pri = _p[0]; val = _v[0];
                _count--;
                float lp = _p[_count]; int lv = _v[_count];
                int i = 0;
                while (true)
                {
                    int c = i * 2 + 1;
                    if (c >= _count) break;
                    if (c + 1 < _count && _p[c + 1] < _p[c]) c++;
                    if (_p[c] >= lp) break;
                    _p[i] = _p[c]; _v[i] = _v[c]; i = c;
                }
                _p[i] = lp; _v[i] = lv;
                return true;
            }
        }

        // ── river spring placement (v10.1) ──────────────────────────────────

        // Bed-cell spring spots inside a 64m zone, positioned at the cell
        // center (the carve's bed cells ARE the meandered thalweg) with y =
        // the spring FILL level (RIVER_DROP below the bank-referenced water
        // level). Sorted upstream→downstream (descending level) so relay
        // placement chains sources the way the liquid engine is designed to
        // take them: each next source lands inside the previous pour's grid
        // and MERGES as a lower pinned level — one continuous sloped mesh
        // whose bank-following spread fills to the bank edges. Cells at or
        // below SEA+0.6 are the zone sea's (river mouths get no source).
        public static void GetRiverSpringSpots(Vector2i zone, List<Vector3> outSpots)
        {
            outSpots.Clear();
            var m = _riverMask;
            var w = _waterLevel;
            int n = _res;
            float half = _half;
            if (m == null || w == null) return;
            float cell = (2f * half) / (n - 1);
            float minX = zone.x * 64f - 32f, minZ = zone.y * 64f - 32f;
            int gx0 = Mathf.Max(0, Mathf.FloorToInt((minX + half) / cell));
            int gx1 = Mathf.Min(n - 1, Mathf.CeilToInt((minX + 64f + half) / cell));
            int gz0 = Mathf.Max(0, Mathf.FloorToInt((minZ + half) / cell));
            int gz1 = Mathf.Min(n - 1, Mathf.CeilToInt((minZ + 64f + half) / cell));
            for (int gz = gz0; gz <= gz1; gz++)
            {
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    if (m[gz * n + gx] != 3) continue;   // v26 — SPINE cells only: pours pin dead-center
                    float y = w[gz * n + gx] - RIVER_DROP;
                    if (y <= SEA + 0.6f) continue;
                    float wx = -half + gx * cell;
                    float wz = -half + gz * cell;
                    if (wx < minX || wx >= minX + 64f || wz < minZ || wz >= minZ + 64f) continue;
                    outSpots.Add(new Vector3(wx, y, wz));
                }
            }
            outSpots.Sort((a, b) => b.y.CompareTo(a.y));
        }

        // Biome-color PNG → biome field, BC palette (existing BetterContinents
        // biome images port unmodified): Meadows 00FF00, BlackForest 007F00,
        // Swamp 7F7F00, Mountain FFFFFF, Plains FFFF00, Mistlands 7F7F7F,
        // AshLands FF0000, DeepNorth 00FFFF, Ocean 0000FF. BLACK (None) keeps
        // the terrain-derived biome — paint only where you want control.
        private static readonly byte[,] BIOME_PALETTE =
        {
            { 0x00, 0xFF, 0x00, 0 }, { 0x00, 0x7F, 0x00, 1 }, { 0x7F, 0x7F, 0x00, 2 },
            { 0xFF, 0xFF, 0xFF, 3 }, { 0xFF, 0xFF, 0x00, 4 }, { 0x7F, 0x7F, 0x7F, 5 },
            { 0xFF, 0x00, 0x00, 6 }, { 0x00, 0xFF, 0xFF, 7 }, { 0x00, 0x00, 0xFF, 8 },
        };

        private static void ApplyBiomeImage(string cfgPath, byte[] biomes, int res)
        {
            try
            {
                string p = cfgPath;
                if (!Path.IsPathRooted(p))
                    p = Path.Combine(FiresCore.Storage.FiresConfigPaths.Cache, p);
                if (!File.Exists(p))
                {
                    Debug.LogWarning($"[FiresWorldGen] biome image not found: {p} — using derived biomes.");
                    return;
                }
                var rgb = FiresPng.LoadRgb(p, out int iw, out int ih);
                int painted = 0;
                for (int z = 0; z < res; z++)
                {
                    int iy = Mathf.Clamp(Mathf.RoundToInt((1f - z / (float)(res - 1)) * (ih - 1)), 0, ih - 1);
                    for (int x = 0; x < res; x++)
                    {
                        int ix = Mathf.Clamp(Mathf.RoundToInt(x / (float)(res - 1) * (iw - 1)), 0, iw - 1);
                        int o = (iy * iw + ix) * 3;
                        int r = rgb[o], gg = rgb[o + 1], b = rgb[o + 2];
                        if (r + gg + b < 30) continue;   // black/None → keep derived
                        int best = -1, bestD = int.MaxValue;
                        for (int k = 0; k < 9; k++)
                        {
                            int dr = r - BIOME_PALETTE[k, 0], dg = gg - BIOME_PALETTE[k, 1], db = b - BIOME_PALETTE[k, 2];
                            int d = dr * dr + dg * dg + db * db;
                            if (d < bestD) { bestD = d; best = k; }
                        }
                        biomes[z * res + x] = BIOME_PALETTE[best, 3];
                        painted++;
                    }
                }
                Debug.Log($"[FiresWorldGen] biome image applied: {iw}x{ih}, {painted} cells painted from {p}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresWorldGen] biome image failed ({ex.Message}) — using derived biomes.");
            }
        }

        // Greyscale heightmap image → base grid (BC convention: 16-bit PNG,
        // 0..1 normalized; 8-bit accepted with terracing warning). Image top
        // = north (+z); resampled bilinearly onto the grid. Heights map
        // linearly ImageMinY..ImageMaxY. Erosion + river carve still run on
        // top — paint the MACRO design, the drainage solve derives rivers.
        private static float[] LoadImageBase(string cfgPath, int res)
        {
            try
            {
                string p = cfgPath;
                if (!Path.IsPathRooted(p))
                    p = Path.Combine(FiresCore.Storage.FiresConfigPaths.Cache, p);
                if (!File.Exists(p))
                {
                    Debug.LogWarning($"[FiresWorldGen] image base not found: {p} — falling back.");
                    return null;
                }
                var g01 = FiresPng.LoadGray01(p, out int iw, out int ih);
                float minY = FiresVoxelConfig.WorldGenImageMinY?.Value ?? -40f;
                float maxY = FiresVoxelConfig.WorldGenImageMaxY?.Value ?? 200f;
                var g = new float[res * res];
                for (int z = 0; z < res; z++)
                {
                    // grid row 0 = south (-z world) ← image bottom row.
                    float v = 1f - z / (float)(res - 1);
                    float iy = v * (ih - 1);
                    int y0 = (int)iy; int y1 = Math.Min(y0 + 1, ih - 1); float fy = iy - y0;
                    for (int x = 0; x < res; x++)
                    {
                        float ix = x / (float)(res - 1) * (iw - 1);
                        int x0 = (int)ix; int x1 = Math.Min(x0 + 1, iw - 1); float fx = ix - x0;
                        float s = g01[y0 * iw + x0] * (1 - fx) * (1 - fy) + g01[y0 * iw + x1] * fx * (1 - fy)
                                + g01[y1 * iw + x0] * (1 - fx) * fy + g01[y1 * iw + x1] * fx * fy;
                        g[z * res + x] = minY + s * (maxY - minY);
                    }
                }
                Debug.Log($"[FiresWorldGen] image base loaded: {iw}x{ih} → {res} grid, heights {minY}..{maxY} from {p}");
                return g;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresWorldGen] image base load failed ({ex.Message}) — falling back.");
                return null;
            }
        }

        // Relay water placement asks: is this position inside a carved river
        // channel?
        public static bool IsRiverAt(float wx, float wy)
        {
            var m = _riverMask;
            int n = _res;
            float half = _half;
            if (m == null) return false;
            int gx = Mathf.RoundToInt((wx + half) / (2f * half) * (n - 1));
            int gz = Mathf.RoundToInt((wy + half) / (2f * half) * (n - 1));
            return gx >= 0 && gx < n && gz >= 0 && gz < n
                && (m[gz * n + gx] == 1 || m[gz * n + gx] == 3);   // wet channel incl. spine
        }

        private static float[] PriorityFloodFill(float[] h, int n)
        {
            int total = n * n;
            var w = new float[total];
            var visited = new bool[total];
            var heapIdx = new int[total + 1];
            var heapKey = new float[total + 1];
            int heapN = 0;

            void HPush(int idx, float key)
            {
                int i = ++heapN;
                heapIdx[i] = idx; heapKey[i] = key;
                while (i > 1)
                {
                    int p = i >> 1;
                    if (heapKey[p] <= heapKey[i]) break;
                    int ti = heapIdx[p]; heapIdx[p] = heapIdx[i]; heapIdx[i] = ti;
                    float tk = heapKey[p]; heapKey[p] = heapKey[i]; heapKey[i] = tk;
                    i = p;
                }
            }

            bool HPop(out int idx)
            {
                if (heapN == 0) { idx = 0; return false; }
                idx = heapIdx[1];
                heapIdx[1] = heapIdx[heapN]; heapKey[1] = heapKey[heapN];
                heapN--;
                int i = 1;
                while (true)
                {
                    int l = i << 1, sm = i;
                    if (l <= heapN && heapKey[l] < heapKey[sm]) sm = l;
                    if (l + 1 <= heapN && heapKey[l + 1] < heapKey[sm]) sm = l + 1;
                    if (sm == i) break;
                    int ti = heapIdx[sm]; heapIdx[sm] = heapIdx[i]; heapIdx[i] = ti;
                    float tk = heapKey[sm]; heapKey[sm] = heapKey[i]; heapKey[i] = tk;
                    i = sm;
                }
                return true;
            }

            void Push(int idx, float lvl)
            {
                if (visited[idx]) return;
                visited[idx] = true;
                w[idx] = lvl;
                HPush(idx, lvl);
            }

            for (int x = 0; x < n; x++)
            {
                Push(x, Math.Max(h[x], SEA));
                Push((n - 1) * n + x, Math.Max(h[(n - 1) * n + x], SEA));
            }
            for (int y = 0; y < n; y++)
            {
                Push(y * n, Math.Max(h[y * n], SEA));
                Push(y * n + n - 1, Math.Max(h[y * n + n - 1], SEA));
            }

            int[] ddx = { -1, 1, 0, 0, -1, -1, 1, 1 };
            int[] ddy = { 0, 0, -1, 1, -1, 1, -1, 1 };
            while (HPop(out int cur))
            {
                float lvl = w[cur];
                int cx = cur % n, cy = cur / n;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + ddx[k], ny = cy + ddy[k];
                    if (nx < 0 || nx >= n || ny < 0 || ny >= n) continue;
                    int ni = ny * n + nx;
                    if (visited[ni]) continue;
                    Push(ni, Math.Max(Math.Max(h[ni], SEA), lvl + 0.0002f));
                }
            }
            return w;
        }

        private static float[] FlowAccumulation(float[] h, float[] w, int n)
        {
            int total = n * n;
            var order = new int[total];
            for (int i = 0; i < total; i++) order[i] = i;
            Array.Sort(order, (a, b) => w[b].CompareTo(w[a]));   // high → low

            var acc = new float[total];
            for (int i = 0; i < total; i++) acc[i] = 1f;

            int[] ddx = { -1, 1, 0, 0, -1, -1, 1, 1 };
            int[] ddy = { 0, 0, -1, 1, -1, 1, -1, 1 };
            foreach (int idx in order)
            {
                if (h[idx] <= SEA) continue;   // ocean absorbs
                int cx = idx % n, cy = idx / n;
                int best = -1; float bestDrop = 0f;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + ddx[k], ny = cy + ddy[k];
                    if (nx < 0 || nx >= n || ny < 0 || ny >= n) continue;
                    int ni = ny * n + nx;
                    // Gradient (diagonal-normalized) with a deterministic
                    // per-cell weight: pure steepest-descent picks the same
                    // axis for every tie on flats/uniform slopes, bundling
                    // rivers into ruler-straight parallel channels. The
                    // jitter diversifies the choice; flow is still downhill.
                    float drop = (w[idx] - w[ni]) / (k < 4 ? 1f : 1.41421356f);
                    drop *= 0.55f + 0.9f * Hash01(nx, ny, 1237);
                    if (drop > bestDrop) { bestDrop = drop; best = ni; }
                }
                if (best >= 0) acc[best] += acc[idx];
            }
            return acc;
        }

        // Optional exact-map base: a VAHC v2 heightcache (the BetterContinents
        // dump format) resampled onto the grid. The file must exist on EVERY
        // peer (server and clients) before the world's first load — after
        // that, each machine's firesgen cache is self-sufficient.
        private static float[] LoadVahcBase(string cfgPath, int res, out int nOut, out float halfOut)
        {
            nOut = 0;
            halfOut = HALF;
            try
            {
                string p = cfgPath;
                if (!Path.IsPathRooted(p))
                    p = Path.Combine(FiresCore.Storage.FiresConfigPaths.Cache, p);
                if (!File.Exists(p))
                {
                    Debug.LogError($"[FiresWorldGen] VAHC base not found: {p} — falling back to the generator. Peers with the file will build a DIFFERENT world.");
                    return null;
                }
                using (var fs = File.OpenRead(p))
                using (var br = new BinaryReader(fs))
                {
                    var magic = br.ReadBytes(4);
                    if (magic.Length != 4 || magic[0] != 'V' || magic[1] != 'A' || magic[2] != 'H' || magic[3] != 'C')
                        throw new Exception("not a VAHC cache");
                    br.ReadInt32();                       // vahc version
                    int size = br.ReadInt32();
                    br.ReadSingle();                      // worldRadius
                    float totalRadius = br.ReadSingle();
                    bool hasBiome = br.ReadByte() != 0;
                    br.ReadInt32();                       // compressed length
                    if (hasBiome) { /* biome block follows the height block; we only read heights */ }

                    int step = Math.Max(1, size / res);
                    int n = size / step;
                    var g = new float[n * n];
                    using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    {
                        var rowBytes = new byte[size * 4];
                        for (int row = 0; row < size; row++)
                        {
                            int off = 0;
                            while (off < rowBytes.Length)
                            {
                                int got = gz.Read(rowBytes, off, rowBytes.Length - off);
                                if (got <= 0) throw new EndOfStreamException();
                                off += got;
                            }
                            if (row % step != 0) continue;
                            int oy = row / step;
                            if (oy >= n) break;
                            for (int ox = 0; ox < n; ox++)
                                g[oy * n + ox] = BitConverter.ToSingle(rowBytes, ox * step * 4);
                        }
                    }
                    nOut = n;
                    halfOut = totalRadius;
                    Debug.Log($"[FiresWorldGen] VAHC base loaded: {size}→{n} grid, half={totalRadius:F0}m from {p}");
                    return g;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresWorldGen] VAHC base load failed ({ex.Message}) — falling back to the generator.");
                return null;
            }
        }

        // Droplet erosion — straight port of the proven offline pass
        // (Tools/FiresWorldGen/Program.cs). Grid units are metres; droplets die
        // when they reach the sea so coasts keep their shelves.
        private static void Erode(float[] h, int n, int count, int seed)
        {
            // Erosion DENSITY (droplets per unit area) must be constant across
            // resolutions or a fine grid is barely touched and terraces. The
            // droplet budget is authored at the 2048 reference grid; scale it by
            // the cell-count ratio (2m grid → ~26× the droplets of a 10m one).
            // Single source of truth so every base path — procedural, image,
            // VAHC — erodes at the same physical density.
            count = (int)Math.Min(int.MaxValue, (long)count * n * n / (2048L * 2048L));
            if (count <= 0) return;
            var rng = new Random(seed);
            // v17 — EROSION-DOMINATED look (the Gaea-class reference DEM):
            // longer droplet lives, stronger carve, tighter brush → dendritic
            // filigree on every slope instead of light seasoning.
            const int LIFE = 64, RADIUS = 2;
            const float INERTIA = 0.08f, CAPACITY = 7.5f, MIN_SLOPE = 0.01f;
            const float ERODE = 0.42f, DEPOSIT = 0.3f, EVAP = 0.012f, GRAVITY = 4f;

            int brushN = 0;
            var bdx = new int[64]; var bdy = new int[64]; var bw = new float[64];
            float wSum = 0f;
            for (int dy = -RADIUS; dy <= RADIUS; dy++)
                for (int dx = -RADIUS; dx <= RADIUS; dx++)
                {
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > RADIUS) continue;
                    bdx[brushN] = dx; bdy[brushN] = dy; bw[brushN] = 1f - d / RADIUS;
                    wSum += bw[brushN];
                    brushN++;
                }
            for (int i = 0; i < brushN; i++) bw[i] /= wSum;

            for (int drop = 0; drop < count; drop++)
            {
                float px = (float)(rng.NextDouble() * (n - 2)) + 1f;
                float py = (float)(rng.NextDouble() * (n - 2)) + 1f;
                float dirX = 0f, dirY = 0f, speed = 1f, water = 1f, sediment = 0f;

                for (int life = 0; life < LIFE; life++)
                {
                    int xi = (int)px, yi = (int)py;
                    if (xi < 1 || xi >= n - 2 || yi < 1 || yi >= n - 2) break;
                    float fx = px - xi, fy = py - yi;

                    int i00 = yi * n + xi;
                    float h00 = h[i00], h10 = h[i00 + 1], h01 = h[i00 + n], h11 = h[i00 + n + 1];
                    float gradX = (h10 - h00) * (1f - fy) + (h11 - h01) * fy;
                    float gradY = (h01 - h00) * (1f - fx) + (h11 - h10) * fx;
                    float hOld = h00 * (1f - fx) * (1f - fy) + h10 * fx * (1f - fy)
                               + h01 * (1f - fx) * fy + h11 * fx * fy;
                    if (hOld <= SEA - 2f) break;

                    dirX = dirX * INERTIA - gradX * (1f - INERTIA);
                    dirY = dirY * INERTIA - gradY * (1f - INERTIA);
                    float len = Mathf.Sqrt(dirX * dirX + dirY * dirY);
                    if (len < 1e-5f) break;
                    dirX /= len; dirY /= len;
                    px += dirX; py += dirY;

                    int xj = (int)px, yj = (int)py;
                    if (xj < 1 || xj >= n - 2 || yj < 1 || yj >= n - 2) break;
                    float gx = px - xj, gy = py - yj;
                    int j00 = yj * n + xj;
                    float hNew = h[j00] * (1f - gx) * (1f - gy) + h[j00 + 1] * gx * (1f - gy)
                               + h[j00 + n] * (1f - gx) * gy + h[j00 + n + 1] * gx * gy;
                    float dh = hNew - hOld;

                    float capacity = Mathf.Max(-dh, MIN_SLOPE) * speed * water * CAPACITY;
                    if (sediment > capacity || dh > 0f)
                    {
                        float amount = dh > 0f ? Mathf.Min(dh, sediment) : (sediment - capacity) * DEPOSIT;
                        sediment -= amount;
                        h[i00] += amount * (1f - fx) * (1f - fy);
                        h[i00 + 1] += amount * fx * (1f - fy);
                        h[i00 + n] += amount * (1f - fx) * fy;
                        h[i00 + n + 1] += amount * fx * fy;
                    }
                    else
                    {
                        float amount = Mathf.Min((capacity - sediment) * ERODE, -dh);
                        for (int b = 0; b < brushN; b++)
                        {
                            int bx = xi + bdx[b], by = yi + bdy[b];
                            if (bx < 0 || bx >= n || by < 0 || by >= n) continue;
                            h[by * n + bx] -= amount * bw[b];
                        }
                        sediment += amount;
                    }

                    speed = Mathf.Sqrt(Mathf.Max(0.01f, speed * speed + (-dh) * GRAVITY));
                    water *= 1f - EVAP;
                    if (water < 0.01f) break;
                }
            }
        }

        // ── deterministic noise (integer-hash value noise; no engine calls) ──

        private static float Hash01(int x, int y, int s)
        {
            unchecked
            {
                uint v = (uint)(x * 374761393 + y * 668265263 + s * 1274126177);
                v = (v ^ (v >> 13)) * 1103515245u;
                v ^= v >> 16;
                return (v & 0xFFFFFF) / 16777216f;
            }
        }

        private static float VNoise(float x, float y, int s)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash01(xi, yi, s), b = Hash01(xi + 1, yi, s);
            float c = Hash01(xi, yi + 1, s), d = Hash01(xi + 1, yi + 1, s);
            float ab = a + (b - a) * fx;
            float cd = c + (d - c) * fx;
            return ab + (cd - ab) * fy;
        }

        private static float Fbm(float x, float y, float freq, int octaves, int s)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += VNoise(x * freq, y * freq, s + o * 131) * amp;
                norm += amp;
                amp *= 0.5f;
                freq *= 2f;
            }
            return sum / norm;
        }

        private static float Ridged(float x, float y, float freq, int octaves, int s)
        {
            float sum = 0f, amp = 0.6f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                float v = 1f - Mathf.Abs(VNoise(x * freq, y * freq, s + o * 131) * 2f - 1f);
                sum += v * v * amp;
                norm += amp;
                amp *= 0.55f;
                freq *= 2.1f;
            }
            return sum / norm;
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // ── disk cache ──────────────────────────────────────────────────────

        // CANONICAL cache location = FUC's one-root layout (Debug/Cache).
        // The old literal "FiresRPGmaker/Cache" was a LEGACY path that FUC's
        // Migrate() empties on every boot — the 1.3GB VAHC base kept
        // "vanishing" because migration relocated it before this code read it
        // (and dropped later re-copies as duplicates of the migrated one).
        private static string CachePath(string worldName, int seed, int res, bool vahc)
        {
            string dir = FiresCore.Storage.FiresConfigPaths.Cache;
            Directory.CreateDirectory(dir);
            string safe = string.Concat((worldName ?? "world").Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(dir, $"firesgen_{safe}_{seed}_r{res}{(vahc ? "_vahc" : "")}.bin");
        }

        private static float[] TryLoad(string path, out byte[] biomes, out byte[] riverMask, out float[] waterLevel, out int nOut, out float halfOut)
        {
            biomes = null;
            riverMask = null;
            waterLevel = null;
            nOut = 0;
            halfOut = HALF;
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var br = new BinaryReader(gz))
                {
                    if (br.ReadUInt32() != MAGIC) return null;
                    if (br.ReadInt32() != FORMAT_VERSION) return null;
                    int n = br.ReadInt32();
                    float half = br.ReadSingle();
                    if (n < 2 || n > 32768 || half < 1000f) return null;
                    var bytes = new byte[(long)n * n * 4];
                    int off = 0;
                    while (off < bytes.Length)
                    {
                        int got = gz.Read(bytes, off, bytes.Length - off);
                        if (got <= 0) return null;
                        off += got;
                    }
                    var g = new float[n * n];
                    Buffer.BlockCopy(bytes, 0, g, 0, bytes.Length);
                    var bm = new byte[n * n];
                    off = 0;
                    while (off < bm.Length)
                    {
                        int got = gz.Read(bm, off, bm.Length - off);
                        if (got <= 0) return null;
                        off += got;
                    }
                    var rm = new byte[n * n];
                    off = 0;
                    while (off < rm.Length)
                    {
                        int got = gz.Read(rm, off, rm.Length - off);
                        if (got <= 0) return null;
                        off += got;
                    }
                    var wbytes = new byte[(long)n * n * 4];
                    off = 0;
                    while (off < wbytes.Length)
                    {
                        int got = gz.Read(wbytes, off, wbytes.Length - off);
                        if (got <= 0) return null;
                        off += got;
                    }
                    var wl = new float[n * n];
                    Buffer.BlockCopy(wbytes, 0, wl, 0, wbytes.Length);
                    biomes = bm;
                    riverMask = rm;
                    waterLevel = wl;
                    nOut = n;
                    halfOut = half;
                    return g;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresWorldGen] cache load failed ({ex.Message}) — regenerating.");
                biomes = null;
                return null;
            }
        }

        private static void Save(string path, float[] g, byte[] biomes, byte[] riverMask, float[] waterLevel, int n, float half)
        {
            var bytes = new byte[g.Length * 4];
            Buffer.BlockCopy(g, 0, bytes, 0, bytes.Length);
            var wbytes = new byte[g.Length * 4];
            if (waterLevel != null && waterLevel.Length == g.Length)
                Buffer.BlockCopy(waterLevel, 0, wbytes, 0, wbytes.Length);
            using (var fs = File.Create(path))
            using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
            using (var bw = new BinaryWriter(gz))
            {
                bw.Write(MAGIC);
                bw.Write(FORMAT_VERSION);
                bw.Write(n);
                bw.Write(half);
                bw.Write(bytes);
                bw.Write(biomes);
                bw.Write(riverMask ?? new byte[g.Length]);
                bw.Write(wbytes);
            }
        }
    }

    // Prefix (not postfix) so the grid exists before the WorldGenerator ctor
    // pre-generates rivers/lakes against it.
    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.Initialize))]
    internal static class Patch_WorldGenerator_Initialize_FiresGen
    {
        private static void Prefix(World world)
        {
            try { FiresWorldGenBase.OnWorldInitialize(world); }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresWorldGen] init failed — falling back to vanilla generation: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(WorldGenerator), "GetBaseHeight")]
    internal static class Patch_WorldGenerator_GetBaseHeight_FiresGen
    {
        private static bool Prefix(float wx, float wy, bool menuTerrain, ref float __result)
        {
            if (menuTerrain || !FiresWorldGenBase.Active) return true;
            __result = FiresWorldGenBase.SampleBaseNormalized(wx, wy);
            return false;
        }
    }

    // Vanilla rivers/streams carve flat channels via this one choke point.
    // Our erosion valleys + the W2 runtime hydrology replace them entirely.
    [HarmonyPatch(typeof(WorldGenerator), "AddRivers")]
    internal static class Patch_WorldGenerator_AddRivers_FiresGen
    {
        private static bool Prefix(float h, ref float __result)
        {
            if (!FiresWorldGenBase.Active) return true;
            __result = h;
            return false;
        }
    }

    // FULL biome ownership (W4.1). Vanilla's ring rules are never consulted on
    // Fires worlds — biome selection samples OUR precomputed field, derived
    // from the terrain (swamps on water margins, mountains by altitude, polar
    // arcs with guaranteed land, Meadows at spawn). Faster than vanilla too:
    // an array lookup instead of a Perlin stack, which also speeds location
    // placement.
    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome),
        typeof(float), typeof(float), typeof(float), typeof(bool))]
    internal static class Patch_WorldGenerator_GetBiome_FiresGen
    {
        private static bool Prefix(float wx, float wy, float oceanLevel, bool waterAlwaysOcean,
            ref Heightmap.Biome __result)
        {
            if (!FiresWorldGenBase.Active) return true;
            if (waterAlwaysOcean && FiresWorldGenBase.SampleBaseNormalized(wx, wy) <= oceanLevel)
            {
                __result = Heightmap.Biome.Ocean;
                return false;
            }
            __result = FiresWorldGenBase.SampleBiome(wx, wy);
            return false;
        }
    }

    // FULL height ownership. Vanilla's per-biome height functions add ±2-8m
    // of their own noise on top of the base — enough to bury river channels
    // and to make in-game terrain diverge from the reviewed preview. On Fires
    // worlds the final surface is ours: grid + our detail, period. (Biome
    // SELECTION still runs vanilla's rules over our base via GetBaseHeight.)
    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight))]
    internal static class Patch_WorldGenerator_GetBiomeHeight_FiresGen
    {
        private static bool Prefix(float wx, float wy, ref Color mask, ref float __result)
        {
            if (!FiresWorldGenBase.Active) return true;
            mask = Color.black;
            __result = FiresWorldGenBase.SampleFinalMeters(wx, wy);
            return false;
        }
    }
}
