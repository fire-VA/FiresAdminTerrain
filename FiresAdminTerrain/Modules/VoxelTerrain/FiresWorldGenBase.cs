using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
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
    public static partial class FiresWorldGenBase
    {
        // ONE format, no back-compat (dev doctrine): anything older is
        // rejected outright and the world regenerates. 36 = the water PLAN
        // rides in the cache (seeders + basin walk deleted).
        private const int FORMAT_VERSION = 37;

        // A cache marked FAITHFUL is a world somebody has already played on: its
        // heights are the source world's own and everything standing in it is
        // seated on them. The load path must therefore ADD NOTHING. Tarn seeding
        // excavates bowls and the surface reconcile rewrites water from heights;
        // both are correct for a world we generated and both are vandalism on a
        // world we imported.
        internal static bool LoadedFaithful { get; private set; }

        // Faithful Bake Water (FiresVoxelConfig): a faithful bake's own water gets the smooth surface too.
        internal static bool FaithfulBakeWaterOn => FiresVoxelConfig.FaithfulBakeWater != null && FiresVoxelConfig.FaithfulBakeWater.Value;
        private static bool s_saveFaithful;
        private const uint MAGIC = 0x4E454746; // "FGEN"
        // Generated grids span ±HALF metres: the world's edge (0.2.722, FatWorldSize; 10500 on a vanilla-size world, so a 10 km
        // world's grid, cache and terrain are what they were). A VAHC base keeps its own span in _half.
        private static float HALF => FatWorldSize.EdgeRadius;

        // The hydrology's RS was "cells of this grid per cell of the 2048 reference grid over 21 km"; on a bigger world the
        // reference cell must keep its metres, so RS gains (edge / 10500). Exactly 1f on a vanilla-size world (and on VAHC /
        // image bases, which are vanilla-size worlds), so their rivers, valleys and erosion are what they were.
        private static float SpanScale => FatWorldSize.EdgeRadius / FatWorldSize.VanillaEdge;
        private const float SEA = 30f;
        // Tide constants the ported river carve keys its mouths to (sandbox
        // WorldConstants): a river mouth sinks half a metre under the lowest
        // spring tide, so the two surfaces meet under water at every tide
        // instead of the river ending on a visible lip.
        private const float SPRING_LOW_TIDE_M = -5f;
        private const float ESTUARY_DIVE_Y = SEA + SPRING_LOW_TIDE_M - 0.5f;

        // River water sits this far below the bank tops.
        public const float RIVER_DROP = 0.40f;

        private static float[] _grid;          // metres; row-major [z * n + x]
        private static byte[] _biomeMap;       // W4.1 — OUR biome placement, same grid
        private static byte[] _riverMask;      // 0 none, 1 river bed, 2 bank
        private static float[] _waterLevel;    // v8 — min(flood fill, original terrain): the spring fill level
        // THE WATER PLAN — authored once at GEN, carried in the cache, obeyed
        // by everything downstream. Bodies hold ONE constant level each;
        // connections hold monotone polylines between them. Nothing infers
        // water from terrain any more.
        private static FiresWaterPlan _plan;
        // Traced drainage polylines (stride 4: x, z, area, level), retained
        // from CarveRivers so the planner can author connections from them.
        private static List<float[]> _riverPolys;
        // int16-PACKED resident copies. The float _grid/_waterLevel above are
        // gen/init scratch; after init they are packed into these and FREED, so
        // the resident 2m grid halves (~1.1GB → ~550MB — the dedi's runtime OOM).
        // Every RUNTIME sampler reads these. PACK=46 → ±712m range, ~2cm step;
        // bilinear + the ±2.6m detail noise in SampleFinalMeters dither it away.
        // Deterministic (client+dedi pack/unpack identically), gen output & the
        // on-disk cache stay float so the offline mirror is unaffected.
        private static short[] _gridS;
        private static short[] _waterS;
        internal const float PACK = 46f, UNPACK = 1f / 46f;

        // 2m-grid access for consumers that must integrate over the REAL
        // water cells (shore paint) — the whole point of the 2m grid.
        internal static short[] WaterShorts => _waterS;
        internal static short[] GridShorts => _gridS;
        internal static int GridN => _res;
        internal static float GridHalf => _half;
        // The resident packed height grid, read-only, for identifying WHICH
        // terrain the generator is serving (e.g. the minimap cache stamp).
        internal static short[] PackedGridForIdentity => _gridS;
        // Every value packs on its own, so fixed ranges pack in parallel to the bytes the one-thread loop wrote (checked
        // against it on the ATomTest bake, 2026-09-28). On one thread the two 110M-value fields took 0.43 s of every load
        // and join.
        private const int PackRangeValues = 1 << 20;

        private static short[] PackI16(float[] f)
        {
            if (f == null) return null;
            var s = new short[f.Length];
            int ranges = (f.Length + PackRangeValues - 1) / PackRangeValues;
            Parallel.For(0, ranges, range =>
            {
                int end = Math.Min(f.Length, (range + 1) * PackRangeValues);
                for (int i = range * PackRangeValues; i < end; i++) s[i] = PackValue(f[i]);
            });
            return s;
        }

        private static short PackValue(float value)
        {
            float v = value * PACK;
            return v >= short.MaxValue ? short.MaxValue : v <= short.MinValue ? short.MinValue : (short)v;
        }
        private static int _res;
        private static float _half = HALF;     // VAHC bases can span a different radius
        private static bool _active;
        private static int _seed;              // world seed (detail noise)
        // STUDIO ONLY: when set, a CACHE load re-derives biomes from the current
        // dials (cheap, no erosion) so biome/feature sliders update live. The game
        // never sets this — cached biomes are used as-is, zero per-load cost.
        public static bool RestyleOnLoad = false;

        public static bool Active => _active && _gridS != null;

        // Biome codes (grid) → Heightmap.Biome. Order matches the offline
        // mirror: Meadows, BlackForest, Swamp, Mountain, Plains, Mistlands,
        // AshLands, DeepNorth, Ocean.
        private static readonly Heightmap.Biome[] BIOME_CODES =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp,
            Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands,
            Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean,
        };

        private static readonly byte OceanCode = (byte)System.Array.IndexOf(BIOME_CODES, Heightmap.Biome.Ocean);

        // Zone mode's door: every public sampler below asks ZoneModeGate first, so a closed zone reads as sea to
        // everything that draws or plays on the land, and a swapped zone reads its source. The generator's own code and
        // generation that must not depend on which zones are open call the Real twins.
        public static Heightmap.Biome SampleBiome(float wx, float wy)
            => Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wy) ? Heightmap.Biome.Ocean : SampleBiomeReal(wx, wy);

        internal static Heightmap.Biome SampleBiomeReal(float wx, float wy)
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
        public static bool GridReady => _gridS != null;

        // One-entry memo per thread: the vanilla heightmap build calls this
        // 4× per vertex (corner-biome blending) and biome selection hammers
        // the base sampler at repeated coords — ghost-zone spawns measured
        // 112-159ms each from exactly that (ZFLOW receipts). Same-coord
        // repeat hits return instantly; results are bit-identical.
        [ThreadStatic] private static float t_fmX, t_fmY, t_fmV;
        [ThreadStatic] private static int t_fmGen;
        private static int s_sampleGen = 1;   // bumped per world init — stale memos die

        private static readonly System.Func<float, float, float> s_finalMetersReal = SampleFinalMetersReal;

        public static float SampleFinalMeters(float wx, float wy)
        {
            float tx = wx, ty = wy;
            return Zones.ZoneModeGate.TryClosedHeight(ref wx, ref wy, s_finalMetersReal, out float sea)
                ? sea
                : Zones.ZoneModeGate.Lifted(tx, ty, SampleFinalMetersReal(wx, wy));
        }

        internal static float SampleFinalMetersReal(float wx, float wy)
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
            var em = FiresWorldEdits.EditMask;
            int n = _res;
            float half = _half;
            if (m != null || em != null)
            {
                float cell = (2f * half) / (n - 1);
                float gxf = (wx + half) / cell, gzf = (wy + half) / cell;
                if (gxf < 0f) gxf = 0f; else if (gxf > n - 1.001f) gxf = n - 1.001f;
                if (gzf < 0f) gzf = 0f; else if (gzf > n - 1.001f) gzf = n - 1.001f;
                int xi = (int)gxf, zi = (int)gzf;
                float fx = gxf - xi, fz = gzf - zi;
                int i0 = zi * n + xi;
                if (m != null)
                {
                    float damp = (m[i0] != 0 ? 1f : 0f) * (1f - fx) * (1f - fz)
                               + (m[i0 + 1] != 0 ? 1f : 0f) * fx * (1f - fz)
                               + (m[i0 + n] != 0 ? 1f : 0f) * (1f - fx) * fz
                               + (m[i0 + n + 1] != 0 ? 1f : 0f) * fx * fz;
                    detail *= 1f - 0.92f * damp;
                }
                if (em != null)
                {
                    // Authored cells hold their stamped height — stand the
                    // procedural ripple down (feathered bilinear so the patch
                    // edge doesn't step).
                    float ed = (em[i0] != 0 ? 1f : 0f) * (1f - fx) * (1f - fz)
                             + (em[i0 + 1] != 0 ? 1f : 0f) * fx * (1f - fz)
                             + (em[i0 + n] != 0 ? 1f : 0f) * (1f - fx) * fz
                             + (em[i0 + n + 1] != 0 ? 1f : 0f) * fx * fz;
                    detail *= 1f - 0.97f * ed;
                }
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
            var m = _riverMask; var w = _waterS; var g = _gridS;
            if (m == null || w == null || g == null) return false;
            int n = _res; float half = _half;
            int gx = Mathf.RoundToInt((wx + half) / (2f * half) * (n - 1));
            int gz = Mathf.RoundToInt((wz + half) / (2f * half) * (n - 1));
            if (gx < 0 || gx >= n || gz < 0 || gz >= n) return false;
            int i = gz * n + gx;
            level = w[i] * UNPACK;
            byte mk = m[i];
            float gi = g[i] * UNPACK;
            // Channel cells (v29 single-stem carve — genuinely dug trench)
            // wet at real submersion; everything else = pond rule (post
            // basin-normalize).
            wet = (mk == 1 || mk == 3) ? level - gi >= 0.4f : level - gi >= 1.2f;
            return true;
        }

        public static bool IsWetAt(float wx, float wz)
        {
            float tx = wx, tz = wz;
            return !Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz) && (IsWetAtReal(wx, wz) || Zones.ZoneModeGate.IsLiftedSeaAt(tx, tz));
        }

        // Rivers and lakes only, never the sea, lifted or not: the ground probe reads this as Ocean, and a lifted chunk's
        // sea keeps its land's biome the way the sea it was did (Swamp leeches spawn in a lifted swamp).
        public static bool IsGenWetAt(float wx, float wz)
            => !Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz) && IsWetAtReal(wx, wz);

        // ANY standing generated water — river wet channel OR lake fill.
        // Bed paint, the vegetation veto and the clutter veto key off THIS,
        // bank to bank across whole basins; IsRiverAt stays river-specific
        // (spine spots, detail suppression).
        internal static bool IsWetAtReal(float wx, float wz)
            => TryGetGenWater(wx, wz, out _, out bool wet) && wet;

        public static bool IsChannelAt(float wx, float wz)
            => !Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz) && IsChannelAtReal(wx, wz);

        // Channel test for the water STORE: the river carve's bank margins
        // (mask 2) include the wetted BENCH — carved ground BELOW the water
        // line that the generator's final snap marks dry. The sheet must run
        // flat over it into the bank wall (and clip through), same as the
        // flowing core — gating the store on raw wetness alone made the
        // sheet edge curl down just before the bank (user screenshots).
        internal static bool IsChannelAtReal(float wx, float wz)
        {
            var m = _riverMask;
            if (m == null) return false;
            int n = _res; float half = _half;
            int gx = Mathf.RoundToInt((wx + half) / (2f * half) * (n - 1));
            int gz = Mathf.RoundToInt((wz + half) / (2f * half) * (n - 1));
            if (gx < 0 || gx >= n || gz < 0 || gz >= n) return false;
            return m[gz * n + gx] != 0;
        }

        public static bool IsWetRawAt(float wx, float wz)
        {
            float tx = wx, tz = wz;
            return !Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz) && (IsWetRawAtReal(wx, wz) || Zones.ZoneModeGate.IsLiftedSeaAt(tx, tz));
        }

        // RAW wet truth for the water STORE: any real standing water at the
        // nearest field cell. IsWetAt's 0.4/1.2m depth thresholds are
        // consumer gates — using them for the store would dry the shallow
        // shoreline ring and stop the sheet short of the banks.
        internal static bool IsWetRawAtReal(float wx, float wz)
        {
            var w = _waterS; var g = _gridS;
            if (w == null || g == null) return false;
            int n = _res; float half = _half;
            int gx = Mathf.RoundToInt((wx + half) / (2f * half) * (n - 1));
            int gz = Mathf.RoundToInt((wz + half) / (2f * half) * (n - 1));
            if (gx < 0 || gx >= n || gz < 0 || gz >= n) return false;
            int i = gz * n + gx;
            return (w[i] - g[i]) * UNPACK > 0.05f;
        }

        public static bool TryGetGenWaterSmooth(float wx, float wz, out float level, out float depth)
        {
            float tx = wx, tz = wz;
            if (!Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz))
                return Zones.ZoneModeGate.LiftWater(tx, tz, TryGetGenWaterSmoothReal(wx, wz, out level, out depth), ref level, ref depth);
            level = 0f;
            depth = 0f;
            return false;
        }

        // SMOOTH (bilinear) water level + depth — THE water mesh sample.
        // Nearest-cell sampling turned every 10m cell into a flat plate:
        // stepped slabs, height jumps at zone borders, channel level held
        // level all the way over the bank edge. Bilinear is a pure function
        // of world position (identical from every zone — seams impossible)
        // and tapers into the banks naturally, because dry/dried cells carry
        // w ≈ ground. Depth blends the same way and reaches 0 at dry cells,
        // so the wet edge dies out instead of clipping.
        internal static bool TryGetGenWaterSmoothReal(float wx, float wz, out float level, out float depth)
        {
            level = 0f; depth = 0f;
            var w = _waterS; var g = _gridS;
            if (w == null || g == null) return false;
            int n = _res; float half = _half;
            float cell = (2f * half) / (n - 1);
            float gx = (wx + half) / cell, gz = (wz + half) / cell;
            if (gx < 0f) gx = 0f; else if (gx > n - 1.001f) gx = n - 1.001f;
            if (gz < 0f) gz = 0f; else if (gz > n - 1.001f) gz = n - 1.001f;
            int xi = (int)gx, zi = (int)gz;
            float fx = gx - xi, fz = gz - zi;
            int i = zi * n + xi;
            float w00 = w[i] * UNPACK, w10 = w[i + 1] * UNPACK, w01 = w[i + n] * UNPACK, w11 = w[i + n + 1] * UNPACK;
            level = w00 * (1f - fx) * (1f - fz) + w10 * fx * (1f - fz)
                  + w01 * (1f - fx) * fz + w11 * fx * fz;
            float d00 = Mathf.Max(0f, w00 - g[i] * UNPACK);
            float d10 = Mathf.Max(0f, w10 - g[i + 1] * UNPACK);
            float d01 = Mathf.Max(0f, w01 - g[i + n] * UNPACK);
            float d11 = Mathf.Max(0f, w11 - g[i + n + 1] * UNPACK);
            depth = d00 * (1f - fx) * (1f - fz) + d10 * fx * (1f - fz)
                  + d01 * (1f - fx) * fz + d11 * fx * fz;
            return true;
        }

        // ── RECONCILED WATER SURFACE — the carve+fill level ──────────────
        // The raw filled field (_waterS) stores basin SPILL levels: right
        // along a channel's length, but standing above the local banks across
        // its width — the over-bank failure every downstream water system
        // spent its life policing. Reconcile ONCE at load on the coarse grid:
        // a morphological opening (max over the rim reach, then min over the
        // governing reach) finds the LOWEST BANK governing each section, and
        // the surface is seated just under it. Precomputed pure data → the
        // bed carve, the water mesh, swim, veg prediction and bed paint all
        // read the SAME number at the same position; the systems cannot
        // disagree, and zone/chunk seams are impossible by construction.
        private static short[] _surfS;
        private static int _surfN;
        private static float _surfCell;
        // Sentinel for suppressed cells (waterfall faces, mid-air shelves).
        // Real packed levels are clamped to MinValue+1 so they can never
        // collide with it.
        private const short SURF_CUT = short.MinValue;

        public static bool WaterSurfaceReady => _surfS != null;

        // ── Water-body CLASSIFICATION (P6) ───────────────────────────────
        // One class per decimated cell, rebuilt every load: basins classify
        // in the walk (lake/pond/swamp/frozen/hot-spring), channels and sea
        // fill in during the reconcile. Pure function of world position —
        // nothing persists it, every consumer queries.
        public const byte WC_NONE = 0, WC_OCEAN = 1, WC_RIVER = 2, WC_STREAM = 3,
                          WC_LAKE = 4, WC_POND = 5, WC_SWAMP = 6, WC_FROZEN = 7, WC_HOTSPRING = 8;
        private static byte[] _classS;
        private static int _classN;
        private static float _classCell;
        private static readonly System.Collections.Generic.List<int> s_cutA = new System.Collections.Generic.List<int>(4096);
        private static readonly System.Collections.Generic.List<int> s_cutB = new System.Collections.Generic.List<int>(4096);
        private static readonly System.Collections.Generic.List<int> s_ringA = new System.Collections.Generic.List<int>(8192);
        private static readonly System.Collections.Generic.List<int> s_ringB = new System.Collections.Generic.List<int>(8192);
        private static readonly System.Collections.Generic.HashSet<int> s_ringSeen = new System.Collections.Generic.HashSet<int>();
        // Centers of SEEDED basins (swamp floods, lowland lakes, mountain
        // tarns), 16m-quantized → the STAMPED water level. Seeded water is
        // already rarity-controlled at placement — the walk's composition
        // roll must NOT thin it again (log-proven: 89 cold tarn basins
        // walked, 88 exterminated by the 0.05 roll → frozen 0) — and the
        // walk's leaky-rim clamp must NOT re-level it either: a swamp
        // sheet's boundary always has a low outside cell somewhere, and
        // rimMin−0.3 crushed every flooded region to a moat ring
        // (log-proven: 24 regions sheeted, 2,888 wet cells map-wide).
        private static readonly System.Collections.Generic.Dictionary<long, float> s_seedLevels =
            new System.Collections.Generic.Dictionary<long, float>();

        // WORLD REPORT capture — the walk/census/seeders record what they
        // made; LogWorldReport prints the LPA-style breakdown after ACTIVE so
        // a world can be judged from the log before joining.
        // Per-basin census for the water report (see the keep path in the basin
        // walk). Cleared at the start of every walk.
        private struct BasinCensus
        {
            public int Cells; public byte Cls; public bool Seeded; public bool RiverFed;
            public float X, Z, Level;
        }
        private static readonly System.Collections.Generic.List<BasinCensus> s_censusBasins =
            new System.Collections.Generic.List<BasinCensus>(4096);

        private static string ClsName(byte c)
            => c == WC_LAKE ? "lake" : c == WC_POND ? "pond" : c == WC_SWAMP ? "swamp"
             : c == WC_FROZEN ? "frozen" : c == WC_HOTSPRING ? "hotspring" : c == WC_RIVER ? "river"
             : c == WC_OCEAN ? "ocean" : "none";

        // WHERE THE WATER COMES FROM. Totals by class and by origin, then the
        // ten biggest bodies with their origin and position, so a runaway flood
        // is named and locatable instead of being a number in a class tally.
        private static void LogWaterCensus(float cellM)
        {
            if (s_censusBasins.Count == 0) { FiresLogger.LogInfo("[FiresWorldGen] water census: no basins kept."); return; }
            var byCls = new System.Collections.Generic.Dictionary<byte, (int n, long cells)>();
            long seededCells = 0, riverCells = 0, otherCells = 0;
            int seededN = 0, riverN = 0, otherN = 0;
            foreach (var b in s_censusBasins)
            {
                byCls.TryGetValue(b.Cls, out var acc);
                byCls[b.Cls] = (acc.n + 1, acc.cells + b.Cells);
                if (b.Seeded) { seededCells += b.Cells; seededN++; }
                else if (b.RiverFed) { riverCells += b.Cells; riverN++; }
                else { otherCells += b.Cells; otherN++; }
            }
            float cellArea = cellM * cellM;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in byCls)
                sb.Append($"{ClsName(kv.Key)} {kv.Value.n} basin(s)/{kv.Value.cells:N0} cells ({kv.Value.cells * cellArea / 1e6f:0.0} km²) | ");
            FiresLogger.LogInfo($"[FiresWorldGen] water census by class: {sb.ToString().TrimEnd(' ', '|')}");
            FiresLogger.LogInfo($"[FiresWorldGen] water census by ORIGIN: seeded {seededN} basin(s)/{seededCells:N0} cells " +
                      $"({seededCells * cellArea / 1e6f:0.0} km²) | river-fed {riverN}/{riverCells:N0} cells " +
                      $"({riverCells * cellArea / 1e6f:0.0} km²) | neither {otherN}/{otherCells:N0} cells " +
                      $"({otherCells * cellArea / 1e6f:0.0} km²){(otherN > 0 ? "  ← DOCTRINE VIOLATION: kept water that was neither seeded nor river-fed" : "")}");
            s_censusBasins.Sort((a, b2) => b2.Cells.CompareTo(a.Cells));
            int show = Mathf.Min(10, s_censusBasins.Count);
            for (int i = 0; i < show; i++)
            {
                var b = s_censusBasins[i];
                FiresLogger.LogInfo($"[FiresWorldGen]   #{i + 1} {ClsName(b.Cls)} {b.Cells:N0} cells " +
                          $"({b.Cells * cellArea / 1e6f:0.00} km²) at ({b.X:0},{b.Z:0}) level {b.Level:0.0}m — " +
                          $"{(b.Seeded ? "SEEDED" : b.RiverFed ? "river-fed" : "NEITHER")}");
            }
        }

        private static int s_repWalked, s_repKept, s_repLake, s_repPond, s_repSwampB, s_repFrozen, s_repHot, s_repRiverFed;
        private static float s_repCapHi;
        private static int s_repTarns, s_repSwampSeeds;
        private static int s_repClsOcean, s_repClsRiver, s_repClsLake, s_repClsPond, s_repClsSwamp, s_repClsFrozen, s_repClsHot;

        private static void LogWorldReport(string worldName, int seed)
        {
            var bm = _biomeMap; var rm = _riverMask;
            if (bm == null) return;
            int n = _res;
            long total = (long)n * n;
            var cnt = new long[9];
            for (int i = 0; i < bm.Length; i++) { byte b = bm[i]; cnt[b < 9 ? b : 0]++; }
            string[] names = { "Meadows", "BlackForest", "Swamp", "Mountain", "Plains", "Mistlands", "AshLands", "DeepNorth", "Ocean" };
            var sb = new System.Text.StringBuilder(256);
            for (int b = 0; b < 9; b++)
            {
                if (b > 0) sb.Append(" | ");
                sb.Append(names[b]).Append(' ').Append((cnt[b] * 100.0 / total).ToString("0.0")).Append('%');
            }
            // River NETWORKS: connected components of channel/spine cells;
            // fragments under 50 cells are carve noise, not rivers.
            int nets = 0; long riverCells = 0;
            if (rm != null)
            {
                var visited = new System.Collections.BitArray(rm.Length);
                var stack = new Stack<int>(4096);
                for (int i0 = 0; i0 < rm.Length; i0++)
                {
                    if (visited[i0] || (rm[i0] != 1 && rm[i0] != 3)) continue;
                    int size = 0;
                    visited[i0] = true;
                    stack.Push(i0);
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop(); size++;
                        int x = i % n, z = i / n;
                        if (x + 1 < n) { int j = i + 1; if (!visited[j] && (rm[j] == 1 || rm[j] == 3)) { visited[j] = true; stack.Push(j); } }
                        if (x > 0) { int j = i - 1; if (!visited[j] && (rm[j] == 1 || rm[j] == 3)) { visited[j] = true; stack.Push(j); } }
                        if (z + 1 < n) { int j = i + n; if (!visited[j] && (rm[j] == 1 || rm[j] == 3)) { visited[j] = true; stack.Push(j); } }
                        if (z > 0) { int j = i - n; if (!visited[j] && (rm[j] == 1 || rm[j] == 3)) { visited[j] = true; stack.Push(j); } }
                    }
                    riverCells += size;
                    if (size >= 50) nets++;
                }
            }
            float cellM = (2f * _half) / (n - 1);
            double riverKm2 = riverCells * (double)cellM * cellM / 1e6;
            FiresLogger.LogInfo($"[FiresWorldGen] ══ WORLD REPORT — '{worldName}' seed {seed} ══");
            FiresLogger.LogInfo($"[FiresWorldGen]  biomes: {sb}");
            if (!float.IsNaN(s_repBioT[0]) && FiresClimate.M != null)
                FiresLogger.LogInfo($"[FiresWorldGen]  climate (mean °C / moisture-pct): Meadows {s_repBioT[0]:0.#}/{s_repBioM[0]:0.00} | " +
                          $"BF {s_repBioT[1]:0.#}/{s_repBioM[1]:0.00} | Swamp {s_repBioT[2]:0.#}/{s_repBioM[2]:0.00} | " +
                          $"Mtn {s_repBioT[3]:0.#}/{s_repBioM[3]:0.00} | Plains {s_repBioT[4]:0.#}/{s_repBioM[4]:0.00} | " +
                          $"Mist {s_repBioT[5]:0.#}/{s_repBioM[5]:0.00} — Plains M<0.30 = rain shadow working");
            FiresLogger.LogInfo($"[FiresWorldGen]  rivers: {nets} network(s), {riverKm2:0.00} km² of channel");
            if (_zoneWater != null)
            {
                int zr = 0, zs = 0, zf = 0, zp = 0;
                for (int i = 0; i < _zoneWater.Length; i++)
                {
                    byte v = _zoneWater[i];
                    if ((v & ZW_RIVER) != 0) zr++;
                    if ((v & ZW_STILL) != 0) zs++;
                    if ((v & ZW_FROZEN) != 0) zf++;
                    if ((v & ZW_PAINT) != 0) zp++;
                }
                FiresLogger.LogInfo($"[FiresWorldGen]  zones: rivers through {zr}, still water in {zs}, frozen in {zf}, shore paint needed in {zp}");
            }
            FiresLogger.LogInfo($"[FiresWorldGen]  still water: walked {s_repWalked} basins → kept {s_repKept} " +
                      $"(lake {s_repLake}, pond {s_repPond}, swamp {s_repSwampB}, frozen {s_repFrozen}, hotspring {s_repHot}); " +
                      $"river-fed {s_repRiverFed}; highest level {s_repCapHi:0}m");
            FiresLogger.LogInfo($"[FiresWorldGen]  seeded: {s_repTarns} mountain tarn(s), {s_repSwampSeeds} flooded swamp region(s)");
            FiresLogger.LogInfo($"[FiresWorldGen]  sky: {FiresSkyIslands.IslandCount} island(s), {FiresSkyIslands.ClusterCount} ascending cluster(s)");
            FiresLogger.LogInfo($"[FiresWorldGen]  water classes (8m cells): ocean {s_repClsOcean:N0} | river {s_repClsRiver:N0} | lake {s_repClsLake:N0} | " +
                      $"pond {s_repClsPond:N0} | swamp {s_repClsSwamp:N0} | frozen {s_repClsFrozen:N0} | hotspring {s_repClsHot:N0}");
        }

        // Per-biome climate means for the world report — filled after the
        // biome build when climate fields exist (generated worlds).
        private static readonly float[] s_repBioT = new float[9];
        private static readonly float[] s_repBioM = new float[9];

        private static void CaptureClimateReport(byte[] b, int n, float half)
        {
            for (int i = 0; i < 9; i++) s_repBioT[i] = float.NaN;
            if (FiresClimate.M == null || b == null) return;
            var cntA = new int[9];
            var sumT = new float[9];
            var sumM = new float[9];
            float cellM = (2f * half) / (n - 1);
            for (int z = 2; z < n; z += 4)
                for (int x = 2; x < n; x += 4)
                {
                    byte v = b[z * n + x];
                    if (v > 8) continue;
                    float wx = -half + x * cellM, wz = -half + z * cellM;
                    cntA[v]++;
                    sumT[v] += FiresClimate.SampleT(wx, wz);
                    sumM[v] += FiresClimate.SampleM(wx, wz);
                }
            for (int i = 0; i < 9; i++)
            {
                s_repBioT[i] = cntA[i] > 0 ? sumT[i] / cntA[i] : float.NaN;
                s_repBioM[i] = cntA[i] > 0 ? sumM[i] / cntA[i] : 0f;
            }
        }

        // Volcano draws cached for the lava mask (BuildBiomes stashes them on
        // gen; cache loads re-derive lazily from the seed — deterministic).
        private static float[] _volcanoDraws;

        // LAVA rides the vegetation-mask ALPHA (vanilla GetAshlandsHeight:
        // mask = (0,0,0,lava)). Our GetBiomeHeight override returned
        // Color.black — which has ALPHA 1 — so the entire Ashlands crescent
        // and every volcano cone rendered as solid lava (user report). Lava
        // is now deliberate: the molten CORE of each volcano caldera (ramped
        // to a crusted rim) and sparse cellular fields on the ash flats.
        // LAVA FIELD — baked once per load at 8m from the FINAL terrain:
        // crater pools/brimming lakes, REAL downhill flow runs traced by
        // steepest descent from brimming rims (and lowland vents) until
        // they meet the ocean, and sparse molten fields on the flats.
        // SampleLavaMask (heightmap masks, far quilt, gameplay GetLava)
        // reads this one field.
        private static byte[] _lavaS;
        private static int _lavaN;
        private static float _lavaCell;

        public static Color SampleLavaMask(float wx, float wz)
            => Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz) ? Color.clear : SampleLavaMaskReal(wx, wz);

        internal static Color SampleLavaMaskReal(float wx, float wz)
        {
            var lf = _lavaS;
            if (lf == null || _biomeMap == null || BiomeCodeAtReal(wx, wz) != 6) return Color.clear;
            int nc = _lavaN;
            float gx = (wx + _half) / _lavaCell, gz = (wz + _half) / _lavaCell;
            if (gx < 0f) gx = 0f; else if (gx > nc - 1.001f) gx = nc - 1.001f;
            if (gz < 0f) gz = 0f; else if (gz > nc - 1.001f) gz = nc - 1.001f;
            int xi = (int)gx, zi = (int)gz;
            float fx = gx - xi, fz = gz - zi;
            int i = zi * nc + xi;
            float a = (lf[i] * (1f - fx) * (1f - fz) + lf[i + 1] * fx * (1f - fz)
                     + lf[i + nc] * (1f - fx) * fz + lf[i + nc + 1] * fx * fz) / 255f;
            return new Color(0f, 0f, 0f, a);
        }

        private static void BakeLavaField()
        {
            _lavaS = null;
            var g = _grid; var b = _biomeMap;
            if (g == null || b == null) return;
            int n = _res;
            float cellM = (2f * _half) / (n - 1);
            int stride = Mathf.Max(1, Mathf.RoundToInt(8f / cellM));
            int nc = (n + stride - 1) / stride;
            _lavaN = nc; _lavaCell = cellM * stride;
            var lf = new byte[nc * nc];
            var volc = _volcanoDraws ?? (_volcanoDraws = DrawVolcanos(_seed));

            float HAt(float wx, float wz)
            {
                int gx = Mathf.Clamp(Mathf.RoundToInt((wx + _half) / cellM), 1, n - 2);
                int gz = Mathf.Clamp(Mathf.RoundToInt((wz + _half) / cellM), 1, n - 2);
                return g[gz * n + gx];
            }
            byte BioAt(float wx, float wz)
            {
                int gx = Mathf.Clamp(Mathf.RoundToInt((wx + _half) / cellM), 1, n - 2);
                int gz = Mathf.Clamp(Mathf.RoundToInt((wz + _half) / cellM), 1, n - 2);
                return b[gz * n + gx];
            }
            void PaintDisk(float wx, float wz, float a, float rad)
            {
                int c0x = Mathf.Max(0, (int)((wx - rad + _half) / _lavaCell));
                int c1x = Mathf.Min(nc - 1, (int)((wx + rad + _half) / _lavaCell) + 1);
                int c0z = Mathf.Max(0, (int)((wz - rad + _half) / _lavaCell));
                int c1z = Mathf.Min(nc - 1, (int)((wz + rad + _half) / _lavaCell) + 1);
                for (int cz = c0z; cz <= c1z; cz++)
                    for (int cx = c0x; cx <= c1x; cx++)
                    {
                        float dwx = cx * _lavaCell - _half - wx, dwz = cz * _lavaCell - _half - wz;
                        float d = Mathf.Sqrt(dwx * dwx + dwz * dwz);
                        if (d >= rad) continue;
                        byte v = (byte)((1f - Smooth(rad * 0.5f, rad, d)) * a * 255f);
                        int ci = cz * nc + cx;
                        if (v > lf[ci]) lf[ci] = v;
                    }
            }
            int TraceRun(float px, float pz, float a, int maxSteps, float rad)
            {
                int steps = 0;
                for (; steps < maxSteps; steps++)
                {
                    PaintDisk(px, pz, a, rad);
                    float bh = HAt(px, pz); float bx = px, bz = pz; bool moved = false;
                    for (int e = 0; e < 8; e++)
                    {
                        float nx2 = px + (e == 0 ? 8f : e == 1 ? -8f : e == 4 ? 8f : e == 5 ? -8f : e == 6 ? 8f : e == 7 ? -8f : 0f);
                        float nz2 = pz + (e == 2 ? 8f : e == 3 ? -8f : e == 4 ? 8f : e == 5 ? 8f : e == 6 ? -8f : e == 7 ? -8f : 0f);
                        float hh2 = HAt(nx2, nz2);
                        if (hh2 < bh) { bh = hh2; bx = nx2; bz = nz2; moved = true; }
                    }
                    if (!moved) { PaintDisk(px, pz, a, rad + 5f); break; }  // pooled in a dip
                    px = bx; pz = bz;
                    if (bh < SEA + 0.4f) break;                             // met the ocean
                    if (steps > 8 && BioAt(px, pz) != 6) break;             // left the crescent
                }
                return steps;
            }

            // 1) volcano craters: pooled lakes low in the bowl, or brimming
            //    lakes near the rim that SHED FLOWS downhill
            int flowsN = 0;
            if (volc != null)
                for (int vi = 0; vi < volc.Length; vi += 4)
                {
                    float vx = volc[vi], vz = volc[vi + 1], R = volc[vi + 2];
                    bool brimming = Hash01(vi, 0x11, _seed ^ 0x7F10) < 0.5f;
                    PaintDisk(vx, vz, 1f, brimming ? R * 0.42f : R * 0.26f);
                    if (!brimming) continue;
                    int flows = 2 + (int)(Hash01(vi, 0x22, _seed ^ 0x7F11) * 3f);
                    for (int fl = 0; fl < flows; fl++)
                    {
                        float th = Hash01(vi, 0x33 + fl, _seed ^ 0x7F12) * (Mathf.PI * 2f);
                        TraceRun(vx + Mathf.Cos(th) * R * 0.44f, vz + Mathf.Sin(th) * R * 0.44f,
                                 1f, 260, 5.5f);
                        flowsN++;
                    }
                }

            // 2) lowland vents across the crescent: molten runs snaking
            //    downhill until they reach the sea
            int latt = Mathf.Max(8, Mathf.RoundToInt(210f / cellM));
            for (int gz = latt / 2; gz < n; gz += latt)
                for (int gx = latt / 2; gx < n; gx += latt)
                {
                    if (WanderHash01(gx, gz, _seed ^ 0x7F20) >= 0.30f) continue;
                    int ci = gz * n + gx;
                    if (b[ci] != 6 || g[ci] <= SEA + 4f) continue;
                    TraceRun(gx * cellM - _half, gz * cellM - _half, 0.85f, 170, 4.5f);
                    flowsN++;
                }

            // 3) sparse molten fields on the flats
            for (int cz = 0; cz < nc; cz++)
                for (int cx = 0; cx < nc; cx++)
                {
                    float wx = cx * _lavaCell - _half, wz = cz * _lavaCell - _half;
                    if (BioAt(wx, wz) != 6) continue;
                    float f = Fbm(wx, wz, 1f / 140f, 3, _seed ^ 0x1A7A);
                    byte v = (byte)(Smooth(0.72f, 0.86f, f) * 0.9f * 255f);
                    int ci = cz * nc + cx;
                    if (v > lf[ci]) lf[ci] = v;
                }

            _lavaS = lf;
            if (flowsN > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] lava field: {flowsN} molten run(s) traced downhill (crater lakes + vents → ocean), fields baked at {_lavaCell:0.#}m.");
        }

        private static long SeedKey(int gx, int gz) => ((long)(gx >> 3) << 32) ^ (uint)(gz >> 3);

        private static bool TryGetSeedLevel(int cx2, int cz2, out float lvl)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (s_seedLevels.TryGetValue(((long)((cx2 >> 3) + dx) << 32) ^ (uint)((cz2 >> 3) + dz), out lvl))
                        return true;
            lvl = 0f;
            return false;
        }

        public static byte BiomeCodeAt(float wx, float wz)
            => Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz) ? OceanCode : BiomeCodeAtReal(wx, wz);

        // Generated biome code at a world position (BIOME_CODES order).
        // Consumers: wet paint (mountain shores get a tight faint band).
        internal static byte BiomeCodeAtReal(float wx, float wz)
        {
            var b = _biomeMap;
            if (b == null) return 0;
            int n = _res;
            float cell = (2f * _half) / (n - 1);
            int gx = Mathf.Clamp(Mathf.RoundToInt((wx + _half) / cell), 0, n - 1);
            int gz = Mathf.Clamp(Mathf.RoundToInt((wz + _half) / cell), 0, n - 1);
            return b[gz * n + gx];
        }

        // ── BAKED ZONE WATER FLAGS ───────────────────────────────────────
        // Known AT GENERATION, never discovered at runtime (user doctrine:
        // we own ALL the worldgen logic — which zones the rivers carved
        // through and which need shore blending is never a mystery). One
        // byte per 64m zone, built from the FINAL class grid.
        public const byte ZW_RIVER = 1, ZW_STILL = 2, ZW_FROZEN = 4, ZW_PAINT = 8, ZW_OCEAN = 16;
        private static byte[] _zoneWater;
        private static int _zoneN, _zoneHalfN;

        public static byte ZoneWaterFlags(int zx, int zz)
        {
            var t = _zoneWater;
            if (t == null) return 0;
            int ix = zx + _zoneHalfN, iz = zz + _zoneHalfN;
            if (ix < 0 || iz < 0 || ix >= _zoneN || iz >= _zoneN) return 0;
            return t[iz * _zoneN + ix];
        }

        // A lifted chunk's sea (ZoneModeLift) is still water: swamp water on swamp land, a lake elsewhere.
        public static byte WaterClassAt(float wx, float wz)
        {
            float tx = wx, tz = wz;
            if (Zones.ZoneModeGate.SamplerSeesClosed(ref wx, ref wz)) return WC_NONE;
            byte cls = WaterClassAtReal(wx, wz);
            if ((cls != WC_NONE && cls != WC_OCEAN) || !Zones.ZoneModeGate.IsLiftedSeaAt(tx, tz)) return cls;
            return SampleBiomeReal(wx, wz) == Heightmap.Biome.Swamp ? WC_SWAMP : WC_LAKE;
        }

        internal static byte WaterClassAtReal(float wx, float wz)
        {
            var c = _classS;
            if (c == null) return WC_NONE;
            int n2 = _classN;
            float cell = _classCell;
            // FLOOR, not round: the fill maps full-res cell gx to class cell
            // gx/stride (floor), so class cell k covers [k*cell, (k+1)*cell).
            // Rounding read every class shifted half a cell (4m) off truth —
            // basin edges misrouted between ice and water sheets.
            int gx = Mathf.FloorToInt((wx + _half) / cell);
            int gz = Mathf.FloorToInt((wz + _half) / cell);
            if (gx < 0) gx = 0; else if (gx >= n2) gx = n2 - 1;
            if (gz < 0) gz = 0; else if (gz >= n2) gz = n2 - 1;
            return c[gz * n2 + gx];
        }

        // ── SHORE DISTANCE FIELD ─────────────────────────────────────────
        // Nearest REAL-water block (any cell with actual w > g), with that
        // block's level and class carried along the propagation. This is the
        // wet paint's driver: a radial falloff from true water cannot show
        // the reconciled surface's cut/smooth block boundaries — the
        // rectangular wet patches were exactly those boundaries leaking
        // through TryGetWaterSurfaceSmooth's coverage.
        private static byte[] _shoreD;    // distance, 0.25m units, capped 63.75m
        private static short[] _shoreL;   // packed level of the nearest wet block
        private static byte[] _shoreC;    // class of the nearest wet block

        // Shore field CORNER sampler. The nearest-cell payload variant was a
        // hard-line factory: chamfer-DT source territories meet along
        // STRAIGHT Voronoi edges, and any binary decision keyed on the
        // nearest source's level/class (sea gate, class weights) printed a
        // ruler-straight paint edge along them (user screenshot at a river
        // mouth). Callers now evaluate per corner and blend the RESULTS, so
        // source switches feather over 8m instead of cutting.
        public static bool TryGetShoreCorners(float wx, float wz, out float distM,
            out float fx, out float fz,
            out float l00, out float l10, out float l01, out float l11,
            out byte c00, out byte c10, out byte c01, out byte c11)
        {
            distM = 0f; fx = 0f; fz = 0f;
            l00 = l10 = l01 = l11 = 0f;
            c00 = c10 = c01 = c11 = WC_NONE;
            var d = _shoreD;
            if (d == null) return false;
            int n2 = _classN; float cell = _classCell;
            float u = (wx + _half) / cell - 0.5f;
            float v = (wz + _half) / cell - 0.5f;
            if (u < 0f) u = 0f; else if (u > n2 - 1.001f) u = n2 - 1.001f;
            if (v < 0f) v = 0f; else if (v > n2 - 1.001f) v = n2 - 1.001f;
            int xi = (int)u, zi = (int)v;
            fx = u - xi; fz = v - zi;
            int i = zi * n2 + xi;
            distM = (d[i] * (1f - fx) * (1f - fz) + d[i + 1] * fx * (1f - fz)
                   + d[i + n2] * (1f - fx) * fz + d[i + n2 + 1] * fx * fz) * 0.25f;
            l00 = _shoreL[i] * UNPACK; l10 = _shoreL[i + 1] * UNPACK;
            l01 = _shoreL[i + n2] * UNPACK; l11 = _shoreL[i + n2 + 1] * UNPACK;
            c00 = _shoreC[i]; c10 = _shoreC[i + 1];
            c01 = _shoreC[i + n2]; c11 = _shoreC[i + n2 + 1];
            return true;
        }
        // Enumerate distinct bodies of a given class from the final class
        // grid: connected components over the 8m cells, centroid + cell count
        // each. Drives va_water_find map pins — no more coordinate guessing.
        public static int FindWaterBodies(byte cls, System.Collections.Generic.List<Vector3> outPos,
            System.Collections.Generic.List<int> outCells, int max)
        {
            outPos.Clear(); outCells.Clear();
            var c = _classS;
            if (c == null) return 0;
            int n2 = _classN;
            float cell = _classCell;
            var seen = new bool[n2 * n2];
            var stack = new Stack<int>(256);
            for (int i0 = 0; i0 < n2 * n2 && outPos.Count < max; i0++)
            {
                if (seen[i0] || c[i0] != cls) continue;
                long sx = 0, sz = 0; int cnt = 0;
                seen[i0] = true;
                stack.Clear();
                stack.Push(i0);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    int x = i % n2, z = i / n2;
                    sx += x; sz += z; cnt++;
                    if (x + 1 < n2 && !seen[i + 1] && c[i + 1] == cls) { seen[i + 1] = true; stack.Push(i + 1); }
                    if (x > 0 && !seen[i - 1] && c[i - 1] == cls) { seen[i - 1] = true; stack.Push(i - 1); }
                    if (z + 1 < n2 && !seen[i + n2] && c[i + n2] == cls) { seen[i + n2] = true; stack.Push(i + n2); }
                    if (z > 0 && !seen[i - n2] && c[i - n2] == cls) { seen[i - n2] = true; stack.Push(i - n2); }
                }
                float wx = ((sx / (float)cnt) + 0.5f) * cell - _half;
                float wz = ((sz / (float)cnt) + 0.5f) * cell - _half;
                float y = 30f;
                if (TryGetWaterSurfaceSmoothReal(wx, wz, out float lvl, out _)) y = lvl;
                else if (TryGetGenGroundReal(wx, wz, out float gg)) y = gg;
                outPos.Add(new Vector3(wx, y, wz));
                outCells.Add(cnt);
            }
            return outPos.Count;
        }

        // BIOME REGIONS — connected components of one biome code, sampled at
        // 8m, centroid + 64m² cell count each. The feature registry's swamp/
        // mistlands/plains "district" tour (distinct from the water-CLASS
        // bodies FindWaterBodies returns).
        public static int FindBiomeRegions(byte biome, System.Collections.Generic.List<Vector3> outPos,
            System.Collections.Generic.List<int> outCells, int minCells, int max)
        {
            outPos.Clear(); outCells.Clear();
            var bm = _biomeMap; if (bm == null) return 0;
            int n = _res; float half = _half;
            float cellM = (2f * half) / (n - 1);
            int stride = Mathf.Max(1, Mathf.RoundToInt(8f / cellM));
            int nc = (n + stride - 1) / stride;
            var hit = new bool[nc * nc];
            for (int zc = 0; zc < nc; zc++)
                for (int xc = 0; xc < nc; xc++)
                    hit[zc * nc + xc] = bm[Mathf.Min(n - 1, zc * stride) * n + Mathf.Min(n - 1, xc * stride)] == biome;
            var seen = new bool[nc * nc];
            var stack = new Stack<int>(256);
            float cell = cellM * stride;
            for (int i0 = 0; i0 < nc * nc; i0++)
            {
                if (seen[i0] || !hit[i0]) continue;
                long sx = 0, sz = 0; int cnt = 0;
                seen[i0] = true; stack.Clear(); stack.Push(i0);
                while (stack.Count > 0)
                {
                    int i = stack.Pop(); int x = i % nc, z = i / nc;
                    sx += x; sz += z; cnt++;
                    if (x + 1 < nc && !seen[i + 1] && hit[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                    if (x > 0 && !seen[i - 1] && hit[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                    if (z + 1 < nc && !seen[i + nc] && hit[i + nc]) { seen[i + nc] = true; stack.Push(i + nc); }
                    if (z > 0 && !seen[i - nc] && hit[i - nc]) { seen[i - nc] = true; stack.Push(i - nc); }
                }
                if (cnt < minCells) continue;
                float wx = ((sx / (float)cnt) + 0.5f) * cell - half;
                float wz = ((sz / (float)cnt) + 0.5f) * cell - half;
                float y = 30f;
                if (TryGetWaterSurfaceSmoothReal(wx, wz, out float lvl, out _)) y = lvl;
                else if (TryGetGenGroundReal(wx, wz, out float gg)) y = gg;
                outPos.Add(new Vector3(wx, y, wz));
                outCells.Add(cnt * stride * stride);   // in 2m-cell units (×4 = m²)
                if (outPos.Count >= max) break;
            }
            return outPos.Count;
        }

        // Volcano cone positions + fill state (pooled crater vs brimming with
        // lava flows) for the feature tour.
        public static int SnapshotVolcanos(System.Collections.Generic.List<Vector3> outPos,
            System.Collections.Generic.List<bool> outBrimming)
        {
            outPos.Clear(); outBrimming.Clear();
            var v = _volcanoDraws ?? (Active ? _volcanoDraws = DrawVolcanos(_seed) : null);
            if (v == null) return 0;
            for (int vi = 0; vi < v.Length; vi += 4)
            {
                float y = 30f;
                if (TryGetGenGroundReal(v[vi], v[vi + 1], out float gg)) y = gg;
                outPos.Add(new Vector3(v[vi], y, v[vi + 1]));
                outBrimming.Add(Hash01(vi, 0x11, _seed ^ 0x7F10) < 0.5f);
            }
            return outPos.Count;
        }

        private const float SURF_RIM_M = 24f;          // bank-top search reach (m)
        private const float SURF_GOV_M = 36f;          // governing lowest-bank reach (m)
        // The surface is smoothed by 24-36m windows, so computing it at the
        // base grid's 2m was pure waste — the first build ran the opening over
        // 10501² = 110M cells: 40.8 SECONDS of login stall and ~880MB of
        // transient arrays, with a 220MB resident result. Decimate to ~8m
        // first (block-MAX ground, so bank tops survive decimation), run the
        // opening there, and the whole build is ~1s with a ~14MB result.
        private const float SURF_TARGET_CELL_M = 8f;






        public static void OnWorldInitialize(World world)
        {
            _active = false;
            LoadedBakePath = null;
            s_sampleGen++;   // invalidate every thread's sampler memo
            _grid = null; _gridS = null;
            _biomeMap = null;
            _riverMask = null;
            _waterLevel = null; _waterS = null; _surfS = null; _classS = null;
            _plan = null; _riverPolys = null; FiresImportedFlow.Clear();
            FiresWorldEdits.Clear();
            s_seedLevels.Clear();
            _volcanoDraws = null;
            _lavaS = null;
            FiresClimate.Clear();
            FiresSkyIslands.Clear();
            // The chunk cache latches its key on first use; a new world must mint
            // its own, and only after its island set is known (below).
            VoxelChunkCache.Reset();
            FiresMinimapRivers.Reset();
            FiresMinimapIslands.Reset();
            VoxelHydrology.Reset();   // per-world solve + channel geometry
            if (world == null || world.m_menu) return;

            // Master switch: OFF ⇒ never activate Fires worldgen, exactly like WorldGenMode.Off. Leaves
            // _active=false so all five WorldGenerator patches (height/biome/rivers) passthrough to vanilla.
            if (FiresVoxelConfig.CustomGeneration != null && !FiresVoxelConfig.CustomGeneration.Value)
            {
                FiresLogger.LogInfo("[FiresWorldGen] 'Fires Custom Generation' (master switch) = false — FAT adds NO terrain generation to this world; vanilla generation. 'Terrain Mode' and 'World Terrain Mode' are ignored while this is off.");
                return;
            }
            // TERRAIN MODE gate: the Fires GENERATOR (custom height/biomes/
            // rivers) runs ONLY in FullVoxel. Off / PlacedOnly / ZoneReplace
            // keep vanilla's terrain and just add or convert voxel where the
            // owner asks — so the mod is safe to drop onto an existing world.
            if (!FiresVoxelConfig.GeneratesTerrain)
            {
                FiresLogger.LogInfo($"[FiresWorldGen] 'Terrain Mode' = {FiresVoxelConfig.Mode} (this is the GENERATOR gate, a different " +
                    $"setting from 'World Terrain Mode' = {FiresVoxelConfig.WorldKind}) — the Fires generator runs only in " +
                    "FullVoxel, so this world keeps VANILLA height, biomes and rivers. Voxel brush tools still work.");
                return;
            }
            var mode = FiresVoxelConfig.WorldGenMode?.Value ?? FiresWorldGenMode.Auto;
            if (mode == FiresWorldGenMode.Off) return;
            // THE WORLD'S OWN PROFILE WINS (FiresWorldProfile); only a world without one is judged by the older
            // signals: the answer recorded at creation, a baked cache, then the name.
            var profile = FiresWorldProfile.Current;
            if (profile != null)
            {
                if (profile.Generator == ProfileGenerator.Vanilla)
                {
                    FiresLogger.LogInfo($"[FiresWorldGen] world '{world.m_name}' is profiled as VANILLA — vanilla generation.");
                    _vanillaFullVoxelWorld = profile.Terrain == ProfileTerrain.VoxelOnly ? world : null;
                    return;
                }
                FiresLogger.LogInfo($"[FiresWorldGen] world '{world.m_name}' is profiled as {profile.Generator} — the Fires generator runs.");
            }
            else
            {
                bool fires = UnprofiledChoosesFires(world.m_name, mode, out bool? chose, out bool nameHit, out bool bakedHit);
                if (chose == false)
                {
                    FiresLogger.LogInfo($"[FiresWorldGen] world '{world.m_name}' was created as VANILLA — vanilla generation.");
                    return;
                }
                if (bakedHit && !nameHit)
                    FiresLogger.LogInfo($"[FiresWorldGen] world '{world.m_name}' has a BAKED cache — activating on that; the Fires name prefix is not required.");
                if (!fires)
                {
                    FiresLogger.LogInfo($"[FiresWorldGen] world '{world.m_name}' has no Fires prefix — vanilla generation (mode=Auto).");
                    return;
                }
            }

            // 21001 over the 21km world is exactly 1m cells — vanilla's own heightmap
            // spacing, so a bake can match the ground a vanilla world was built on
            // instead of halving it. The ceiling is memory, not correctness: the grid
            // is one resident array PER FIELD for the whole world, so the cost is
            // logged below rather than guessed at.
            int res = Mathf.Clamp(FiresVoxelConfig.WorldGenResolution?.Value ?? 10501, 512, 21001);
            {
                double cells = (double)res * res;
                double packedMb = cells * 2 / (1024 * 1024), floatMb = cells * 4 / (1024 * 1024);
                string cost = $"[FiresWorldGen] grid {res} = {(2f * HALF) / (res - 1):0.###}m cells, "
                            + $"{cells / 1e6:0.#}M cells/field ({packedMb:0} MB resident int16, {floatMb:0} MB as the "
                            + "float array it generates from, per field).";
                if (res > 12000) FiresLogger.LogWarning(cost + " ABOVE the old 12000 ceiling — watch for OOM on first generate.");
                else FiresLogger.LogInfo(cost);
            }
            int droplets = Mathf.Max(0, FiresVoxelConfig.WorldGenDroplets?.Value ?? 400000);
            string vahc = FiresWorldProfile.Current?.VahcBase is string profiledVahc && profiledVahc.Length > 0
                ? profiledVahc : (FiresVoxelConfig.WorldGenVahcBase?.Value ?? "");
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
                    FiresLogger.LogError($"[FiresWorldGen] WorldGen VAHC Base is configured but the file is MISSING: {vp} — " +
                                   "generating procedurally instead. Peers that HAVE the file will build a DIFFERENT world. " +
                                   "Fix the file placement, delete this world's firesgen_* cache, and wipe the world.");
                    useVahc = false;
                }
            }
            // A studio-BAKED world (offline WorldGen Studio: imported DEM / hand-built)
            // is a name-keyed cache loaded by world NAME regardless of seed or res — a
            // shipped world just needs its name. Its own n/half (world size) ride in the
            // file, so the cache carries the studio's world size. Falls through to the
            // normal seed-keyed cache when no bake exists.
            // A client takes the server's own bake and flow field from the join sync (D14) when the server sent them.
            string bakedPath = BakedCachePath(world.m_name);
            string syncedFlow = null, syncedCuts = null;
            bool synced = false;
            if (ZNet.instance != null && !ZNet.instance.IsServer()
                && BcImport.BcJoinSync.BakeAnswer(out string syncedBake) == BcImport.BcJoinSync.ServerAnswer.Data)
            {
                bakedPath = syncedBake;
                BcImport.BcJoinSync.BakeFlowAnswer(out syncedFlow);
                BcImport.BcJoinSync.BakeCutsAnswer(out syncedCuts);   // 0.2.806: the server's big waterfall sinkholes
                synced = true;
            }
            string path = File.Exists(bakedPath) ? bakedPath : CachePath(world.m_name, world.m_seed, res, useVahc);
            if (path == bakedPath)
            {
                FiresLogger.LogInfo($"[FiresWorldGen] loading STUDIO-BAKED world '{world.m_name}' (seed-agnostic"
                    + (synced ? ", the server's copy from the join sync" : "") + $"): {bakedPath}");
                if (ZNet.instance == null || ZNet.instance.IsServer())
                {
                    LoadedBakePath = bakedPath;
                    BcImport.BcJoinSync.Prehash(bakedPath);
                }
            }
            var steps = new StepTimes();
            if (synced) FiresImportedFlow.TryLoad(syncedFlow);   // the server's flow field, when it has one
            else FiresImportedFlow.TryLoadFor(path);   // authored flow rides beside a baked world; absent for generated ones
            steps.Lap("flow field");

            var sw = Stopwatch.StartNew();
            // A client's bake may already be read: the join sync started it on a worker (BakePreload).
            var loaded = BakePreload.TakeFor(path) ?? ReadCacheFile(path);
            // 0.2.722: a cache FAT generated for another world size spans the wrong disc; it was reused anyway (the header's
            // half was never compared). Only the GENERATED cache is judged: a bake (FiresWorldBake writes half = the playable
            // radius, faithful or not, at BakedCachePath), a synced bake and a VAHC base keep their own spans.
            if (loaded != null && !useVahc && !loaded.Faithful && path == CachePath(world.m_name, world.m_seed, res, false)
                && Mathf.Abs(loaded.Half - HALF) > 0.5f)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] the cache for '{world.m_name}' spans +-{loaded.Half:0} m but this world's edge is "
                    + $"{HALF:0} m ({FatWorldSize.Describe()}): generating it again.");
                loaded = null;
            }
            steps.Lap(BakePreload.AdoptedReadMs >= 0 ? "cache adopted from the worker" : "cache read");
            _grid = loaded?.Grid;
            _biomeMap = loaded?.Biomes;
            _riverMask = loaded?.RiverMask;
            _waterLevel = loaded?.WaterLevel;
            _plan = loaded?.Plan;
            int n = loaded?.N ?? 0;
            float half = loaded?.Half ?? HALF;
            if (loaded != null) LoadedFaithful = loaded.Faithful;
            string source = "cache";
            if (_grid == null)
            {
                string img = FiresWorldProfile.Current?.ImageBase is string profiledImage && profiledImage.Length > 0
                    ? profiledImage : (FiresVoxelConfig.WorldGenImageBase?.Value ?? "");
                if (!string.IsNullOrWhiteSpace(img))
                {
                    _grid = LoadImageBase(img, res);
                    if (_grid != null)
                    {
                        n = res;
                        half = HALF;
                        _half = half;   // 0.2.723: CarveRivers reads _half; it held the previous world's span here
                        Erode(_grid, n, droplets, world.m_seed ^ 0x51ED, half);
                        _riverMask = CarveRivers(_grid, n, world.m_seed, out _waterLevel);
                        source = "image base";
                    }
                }
                if (_grid == null && useVahc)
                {
                    _grid = LoadVahcBase(vahc, res, out n, out half);
                    if (_grid != null)
                    {
                        _half = half;   // 0.2.723: the VAHC base's own span, for the erosion and CarveRivers below
                        Erode(_grid, n, droplets, world.m_seed ^ 0x51ED, half);
                        _riverMask = CarveRivers(_grid, n, world.m_seed, out _waterLevel);
                        source = "vahc base";
                    }
                    else
                    {
                        FiresLogger.LogError("[FiresWorldGen] VAHC base failed to load (see above) — generating procedurally under the untagged cache name.");
                        path = CachePath(world.m_name, world.m_seed, res, false);
                    }
                }
                if (_grid == null)
                {
                    _half = HALF;   // 0.2.723: BuildGrid's CarveRivers reads _half (a VAHC world earlier this session left its span)
                    _grid = BuildGrid(world.m_seed, res, droplets, out _riverMask, out _waterLevel);
                    n = res;
                    half = HALF;
                    source = "generated";
                }
                // ONE TERRAIN TRUTH: despike at GEN, before biomes/climate/
                // cache. It used to run after the cache save on EVERY load —
                // so climate and biomes judged PRE-despike heights while the
                // seeders and walk saw POST-despike ground (a real
                // misalignment), and every boot repeated a full-grid pass.
                // Now every consumer reads the same final base relief and
                // cache loads skip the pass entirely (the cache stores it).
                _res = n; _half = half; _seed = world.m_seed;
                DespikeGrid();
                _biomeMap = BuildBiomes(_grid, n, half, world.m_seed, source == "generated");
                CaptureClimateReport(_biomeMap, n, half);
                string biomeImg = FiresWorldProfile.Current?.BiomeImage is string profiledBiomes && profiledBiomes.Length > 0
                    ? profiledBiomes : (FiresVoxelConfig.WorldGenBiomeImage?.Value ?? "");
                if (!string.IsNullOrWhiteSpace(biomeImg)) ApplyBiomeImage(biomeImg, _biomeMap, n);
                // ═══ WATER IS AUTHORED, NOT DISCOVERED ═══════════════════════
                // The planner reads the terrain, the biomes, the flood-fill
                // table and the traced drainage, and decides what water EXISTS:
                // every still body gets ONE constant level chosen once at
                // spill-0.85, every run is a monotone polyline from its
                // upstream body's constant to its downstream body's constant.
                // Conform then makes the TERRAIN agree with that decision.
                // Runs at GEN only and rides in the cache — a load never
                // re-plans, and nothing downstream re-derives water from
                // heights. This replaces the four seeders and the basin walk.
                FatSinkFallCuts.Arm(FiresWorldRecords.PathFor(world.m_name, FatSinkFallCuts.Suffix), FiresVoxelConfig.GeneratesCaves);   // 0.2.803: big sinkholes cut rivers
                try { BuildWaterPlan(world.m_seed, n, half); } finally { FatSinkFallCuts.Disarm(); }
                steps.Lap($"generate ({source})");
            }
            _res = n;
            _half = half;
            _seed = world.m_seed;
            // STUDIO live restyle: on a CACHE load, re-derive biomes with the
            // current dials (cheap — no erosion) so biome/feature sliders update
            // without a full regen. The seeders below already re-run every load,
            // so feature dials take effect too. Off in game.
            if (source == "cache" && RestyleOnLoad)
            {
                _biomeMap = BuildBiomes(_grid, n, half, world.m_seed, false);
                CaptureClimateReport(_biomeMap, n, half);
            }
            // PERSIST fresh generations.
            if (source != "cache")
            {
                var swSave = Stopwatch.StartNew();
                try
                {
                    Save(path, _grid, _biomeMap, _riverMask, _waterLevel, n, half, _plan);
                    FiresLogger.LogInfo($"[FiresWorldGen] worldgen grid cached ({source}) in {swSave.Elapsed.TotalSeconds:0.0}s.");
                }
                catch (Exception ex) { FiresLogger.LogWarning($"[FiresWorldGen] cache save failed ({ex.Message}) — world still active, regenerates next load."); }
                steps.Lap("cache save");
            }
            if (source == "cache")
            {
                if (synced && syncedCuts != null) FiresLogger.LogDebug($"[SinkFall] big: the server's sinkhole list came with the join sync ({Path.GetFileName(syncedCuts)})");
                FatSinkFallCuts.Load(synced ? syncedCuts : FiresWorldRecords.PathFor(world.m_name, FatSinkFallCuts.Suffix), world.m_seed, anySeed: path == bakedPath);
            }
            // Composition on EVERY load — cheap, and it keeps the pond
            // keep-chances tunable: the selection re-rolls deterministically
            // per (seed, settings). Despike moved to GEN (cache stores it).
            BakeLavaField();
            steps.Lap("lava field");
            BuildZoneSurvey();
            steps.Lap("zone survey");
            // MOUNTAIN WATER IS A DIFFERENT SYSTEM. The planner is LOWLAND-ONLY
            // on purpose (it rejects every fill candidate above 150m): a ridged
            // massif's noise pockets pass any depth filter you can write, so
            // high still water is not scavenged from the fill table at all.
            // Tarns are deliberate rare cirque sites that verify a natural
            // hollow and excavate their own bowl, and they keep running here.
            if (!LoadedFaithful)
            {
                SeedMountainTarns();
                steps.Lap("mountain tarns");
            }
            else FiresLogger.LogInfo("[FiresWorldGen] FAITHFUL cache — tarn seeding SKIPPED; this world's ground is the source world's and nothing may excavate it.");
            _plan?.BuildIndex(n, half);
            steps.Lap("water plan index");
            ClampStrandedBodies(n, half);   // 0.2.849: a body levelled before its river was cut/carved sits on that river's level
            steps.Lap("stranded bodies");
            // The plan already decided every level; this only translates it
            // into the 8m class grid the paint/clutter/ice samplers read.
            StampWaterClasses();
            steps.Lap("water classes");
            if (RunsWholeWorldDiagnostics)
            {
                AuditWater();
                steps.Lap("water audit");
            }
            // VERBATIM OVERLAYS: stamp studio-authored world edits onto the final
            // base fields LAST — after all procedural seeding/reconcile — so what
            // you sculpted appears exactly. Untouched cells already equal the gen,
            // so patch edges are seamless; the edit mask (below) holds the stamped
            // height against detail noise, and the water reconcile treats a stamped
            // surface as any other wet body against the stamped banks.
            FiresWorldEdits.Apply(world.m_seed, _grid, _waterLevel, _biomeMap, n, half);
            steps.Lap("world edits");
            // Pack the resident grid to int16 and free the float copies — halves
            // the 2m grid's runtime footprint (the dedi OOM). Gen/Save/Normalize/
            // Audit above all ran on the float arrays; every runtime sampler
            // below reads _gridS/_waterS.
            _gridS = PackI16(_grid); _waterS = PackI16(_waterLevel);
            steps.Lap("int16 pack");
            // Carve+fill water: reconcile the filled field against its banks
            // ONCE, here, while the float arrays still exist. Everything
            // downstream (bed carve, water mesh, swim, veg prediction, paint)
            // reads the one reconciled surface.
            // A faithful bake's ground is never excavated (tarns stay skipped above), but with Faithful Bake Water on its
            // own baked water gets the smooth surface every water reader needs (Fire 10-01: gated test). The reconcile
            // only reads the ground and water fields.
            if (!LoadedFaithful || FaithfulBakeWaterOn)
            {
                BuildReconciledSurface(_grid, _waterLevel, n);
                steps.Lap("surface reconcile");
                if (LoadedFaithful)
                    FiresLogger.LogInfo("[FiresWorldGen] FAITHFUL cache — water surface reconcile RUN (Faithful Bake Water on): the bake's "
                        + "own rivers and lakes are queryable this session.");
            }
            else FiresLogger.LogInfo("[FiresWorldGen] FAITHFUL cache — water surface reconcile SKIPPED; the water is the flat sea the source world had and is not re-derived from heights.");
            _grid = null; _waterLevel = null;
            // Needs the packed ground grid. The server loads or seeds the world's
            // frozen island set; a client holds islands off until the server's set
            // arrives (FiresSkyIslands.Registry.cs).
            FiresSkyIslands.Initialize(world);
            steps.Lap("sky islands");
            _active = true;
            FiresLogger.LogInfo($"[FiresWorldGen] ACTIVE for '{world.m_name}' seed={world.m_seed} grid={n} half={half:F0} droplets={droplets} " +
                      $"({source}) — world ready in {sw.Elapsed.TotalSeconds:0.0}s | {path}");
            LogResidentFields();
            if (RunsWholeWorldDiagnostics) LogWorldReport(world.m_name, world.m_seed);
            steps.Lap("reports");
            FiresLogger.LogInfo($"[FiresWorldGen] world init steps: {steps}.");
        }

        // The water audit and the world report scan the whole grid (0.36 s and 0.82 s on the ATomTest dedi, 0.2.618) and
        // say the same on every peer, so the server logs them and a client only when verbose: on a client they ran inside
        // every join's RPC_PeerInfo.
        private static bool RunsWholeWorldDiagnostics => ZNet.instance == null || ZNet.instance.IsServer() || FiresLogger.VerboseEnabled;

        // The whole-world fields this peer keeps for the session, by size ([perf] 2026-09-28, weighing ATomTest's managed
        // heap against its GC stops and page faults). Every one is a pointer-free array: Boehm never scans its contents.
        private static void LogResidentFields()
        {
            var parts = new List<string>();
            long total = 0;
            AddResident(parts, ref total, "ground", _gridS);
            AddResident(parts, ref total, "water", _waterS);
            AddResident(parts, ref total, "surface", _surfS);
            AddResident(parts, ref total, "class", _classS);
            AddResident(parts, ref total, "lava", _lavaS);
            AddResident(parts, ref total, "biome", _biomeMap);
            AddResident(parts, ref total, "river mask", _riverMask);
            AddResident(parts, ref total, "zone water", _zoneWater);
            AddResident(parts, ref total, "shore distance", _shoreD);
            AddResident(parts, ref total, "shore level", _shoreL);
            AddResident(parts, ref total, "shore class", _shoreC);
            AddResident(parts, ref total, "volcano draws", _volcanoDraws);
            AddResident(parts, ref total, "full ground", _grid);
            AddResident(parts, ref total, "full water", _waterLevel);
            FiresLogger.LogInfo($"[FiresWorldGen] resident world fields: {total / ResidentBytesPerMegabyte:N0} MB in all, pointer-free "
                + $"({string.Join(", ", parts.ToArray())}).");
        }

        private const double ResidentBytesPerMegabyte = 1024d * 1024d;

        private static void AddResident(List<string> parts, ref long total, string name, Array field)
        {
            if (field == null) return;
            long bytes = Buffer.ByteLength(field);
            total += bytes;
            parts.Add($"{name} {bytes / ResidentBytesPerMegabyte:N0} MB");
        }

        // 0.2.722: the world's playable radius (FatWorldSize; 10000 on a vanilla-size world).
        private static float PlayableRadius => FatWorldSize.Radius;
        private static float PlayableRadiusSqr => PlayableRadius * PlayableRadius;

        // Set by the vanilla-profile branch above; consumed from ZNet.Start, which is where the world is actually
        // loaded. The profile branch runs during world INIT, and WorldGenerator.instance does not exist yet there.
        private static World _vanillaFullVoxelWorld;

        // A full-voxel world whose HEIGHT comes from vanilla still gets its sky islands, and therefore its caves:
        // FiresVoxelConfig.GeneratesCaves is only ever read through FiresSkyIslands (HoldsCaves), so no islands
        // means no caves either. The island set is normally seeded at the END of the Fires generator's init, which
        // a vanilla-profiled world never reaches - so it is seeded here instead, against vanilla's own heightmap.
        // GroundOverride exists for exactly this ("lets the offline studio place islands on an IMPORTED world where
        // the generator isn't Active"), and vanilla's GetHeight answers anywhere, so nothing must be explored first.
        internal static void SeedSkyIslandsOnVanillaGround()
        {
            var world = _vanillaFullVoxelWorld;
            _vanillaFullVoxelWorld = null;
            if (world == null) return;

            var generator = WorldGenerator.instance;
            if (generator == null)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] world '{world.m_name}' is full voxel on vanilla height, but "
                    + "WorldGenerator was not up at ZNet.Start — sky islands (and therefore caves) were NOT seeded.");
                return;
            }

            FiresSkyIslands.GroundWhenGeneratorIsOff = (wx, wz) =>
                wx * wx + wz * wz > PlayableRadiusSqr ? (float?)null : generator.GetHeight(wx, wz);
            FiresSkyIslands.Initialize(world);
            FiresLogger.LogInfo($"[FiresWorldGen] world '{world.m_name}' is full voxel on VANILLA height — "
                + $"sky islands seeded from vanilla's heightmap: {FiresSkyIslands.IslandCount} island(s).");
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

        // Give rivers the still-body footprint rule (grow into any connected cell
        // whose ground lies under the level). Written and measured in the sandbox
        // and deliberately OFF: A/B'd at n=10501 it added 31.8% more river cells
        // and made the two things that matter WORSE — steps over 0.5m between
        // adjacent river cells went 14,348 → 39,608 (2.8x). Kept as a named
        // constant so the conformance port reads exactly as it was written.

        // ── BIOME WATER STYLES ───────────────────────────────────────────
        // Biome rules govern HOW a water body is BUILT, not what appears
        // (user doctrine): excavation depths, berm crest height, and the
        // shore paint's reach/rise/weight + beach readiness. One table, one
        // tuning surface — every seeder, the walk's berms, and PaintWet01
        // read it. Indexed by biome code (BIOME_CODES order).
        public struct FWaterStyle
        {
            public float PondDepth;   // bowl centre dig for pond-size bodies (m, on top of 1.2 base)
            public float LakeDepth;   // bowl centre dig for lake-size bodies
            public float BermH;       // berm crest height above the water level
            public float ShoreReach;  // paint horizontal falloff (m)
            public float ShoreRise;   // paint vertical rise cap (m)
            public float PaintWeight; // paint strength 0..1
            public float BeachBias;   // 0..1 — how readily river beaches form
        }
        private static readonly FWaterStyle[] WATER_STYLES =
        {
            new FWaterStyle { PondDepth = 3.2f, LakeDepth = 4.6f, BermH = 0.9f, ShoreReach = 6f,  ShoreRise = 1.8f, PaintWeight = 1.0f,  BeachBias = 0.7f },  // Meadows
            new FWaterStyle { PondDepth = 3.2f, LakeDepth = 4.6f, BermH = 0.9f, ShoreReach = 5.5f, ShoreRise = 1.6f, PaintWeight = 0.9f, BeachBias = 0.5f },  // BlackForest
            new FWaterStyle { PondDepth = 1.6f, LakeDepth = 2.6f, BermH = 0.6f, ShoreReach = 5f,  ShoreRise = 1.0f, PaintWeight = 0.75f, BeachBias = 0.15f }, // Swamp — murky, shallow
            new FWaterStyle { PondDepth = 3.8f, LakeDepth = 5.2f, BermH = 0.9f, ShoreReach = 4.5f, ShoreRise = 1.2f, PaintWeight = 0f,   BeachBias = 0f },    // Mountain — snow shores STAY SNOW, zero paint
            new FWaterStyle { PondDepth = 3.0f, LakeDepth = 4.4f, BermH = 0.8f, ShoreReach = 7f,  ShoreRise = 1.8f, PaintWeight = 1.0f,  BeachBias = 0.8f },  // Plains — broad beaches
            new FWaterStyle { PondDepth = 3.4f, LakeDepth = 4.8f, BermH = 0.9f, ShoreReach = 5f,  ShoreRise = 1.5f, PaintWeight = 0.7f,  BeachBias = 0.3f },  // Mistlands
            new FWaterStyle { PondDepth = 2.6f, LakeDepth = 4.0f, BermH = 0.8f, ShoreReach = 6f,  ShoreRise = 1.5f, PaintWeight = 0.8f,  BeachBias = 0.4f },  // AshLands
            new FWaterStyle { PondDepth = 3.8f, LakeDepth = 5.2f, BermH = 0.9f, ShoreReach = 4.5f, ShoreRise = 1.2f, PaintWeight = 0f,   BeachBias = 0f },    // DeepNorth — snow shores stay snow
            new FWaterStyle { PondDepth = 0f,   LakeDepth = 0f,   BermH = 0f,   ShoreReach = 0f,  ShoreRise = 0f,   PaintWeight = 0f,    BeachBias = 0f },    // Ocean — sea rules own it
        };
        public static FWaterStyle WaterStyleFor(byte bio)
            => WATER_STYLES[bio < WATER_STYLES.Length ? bio : 0];

        // (WaterProfile keep-roll table DELETED — deliberate-depressions
        // doctrine: unseeded natural basins are never blessed, so there is
        // no density to roll. Seeders own placement; quota lives there.)


        // ═══ WATER IS AUTHORED, NOT DISCOVERED ═══════════════════════════════
        // Run once, at GEN, on the raw carve. The planner reads terrain, biomes,
        // the flood-fill table and the traced drainage and decides what water
        // EXISTS — every still body gets ONE constant, every run is a monotone
        // polyline between two constants. Conformance then moves the TERRAIN to
        // agree. The sea is laid on afterwards because the ocean conforms
        // nothing, and the coastline gets the same buried-edge treatment as
        // every inland boundary. From here the plan is READ, never re-derived.
        // 0.2.861 (Fire 10-08, SinkTestGen9 x -3264 z 791 / x -4100: straight 50 m walls and terraced strips across the
        // sea): the planner emits some still bodies only 1-3 cells wide and tens to hundreds long (seed 777: 20 of 153,
        // e.g. a Pond 2 x 343 cells at level 53.2 over 14 m ground). Conform's BuryMeshEdges then raises the ground to
        // meet that sheet, column by column. No real lake is a 4 m-wide line, so such a body loses its footprint here,
        // before Conform: no water, no shaping. New generations only.
        private const int LineBodyMaxWidth = 3, LineBodyMinLength = 30;

        private static void DropLineBodies(FiresWaterPlan plan, int n, float half)
        {
            if (plan == null) return;
            int dropped = 0;
            foreach (var b in plan.Bodies)
            {
                if (b.Kind == WaterBodyKind.Ocean || b.Mask == null) continue;
                bool line = (b.W <= LineBodyMaxWidth && b.H >= LineBodyMinLength) || (b.H <= LineBodyMaxWidth && b.W >= LineBodyMinLength);
                if (!line) continue;
                System.Array.Clear(b.Mask, 0, b.Mask.Length);
                dropped++;
                float cell = (2f * half) / (n - 1);
                FiresLogger.LogDebug($"[FiresWorldGen] line-shaped {b.Kind} dropped: {b.W}x{b.H} cells at ({b.Gx0 * cell - half:0},{b.Gz0 * cell - half:0}), level {b.Level:0.0}");
            }
            if (dropped > 0) FiresLogger.LogInfo($"[FiresWorldGen] water plan: {dropped} line-shaped still bod(ies) dropped (a 1-3 cell wide strip is no lake; it walled the sea in)");
        }

        private static void BuildWaterPlan(int seed, int n, float half)
        {
            if (_grid == null || _waterLevel == null || _riverMask == null) return;
            var swP = Stopwatch.StartNew();
            bool[] wetland = null;
            if (_biomeMap != null)
            {
                wetland = new bool[n * n];
                for (int i = 0; i < wetland.Length; i++) wetland[i] = _biomeMap[i] == 2;
            }
            // The fill table must describe the terrain being planned on, so it is
            // computed here on the final despiked grid rather than carried from
            // the trace (CarveRivers no longer moves the ground at all).
            var fill = PriorityFloodFill(_grid, n, "plan fill");
            try
            {
                _plan = FiresWaterPlanner.BuildPlan(_grid, _biomeMap, fill, _riverPolys, n, half, seed, wetland);
            }
            catch (Exception ex)
            {
                _plan = null;
                FiresLogger.LogError($"[FiresWorldGen] water planner FAILED ({ex.Message}) — this world gets rivers and sea only.\n{ex.StackTrace}");
            }
            DropLineBodies(_plan, n, half);
            _riverPolys = null;
            if (_plan == null)
            {
                Array.Copy(_grid, _waterLevel, _grid.Length);
                Array.Clear(_riverMask, 0, _riverMask.Length);
                return;
            }
            // BODIES ONLY. carveConnections:false is the sandbox's own call: the
            // channels between bodies belong to InciseRivers below, which is the
            // proven carve (bank-derived ribbon, weir shaping, Dijkstra valley,
            // SmoothMin banks). Letting Conform carve them too was two carves on
            // one piece of ground and is what stood water up in vertical blades.
            FiresWaterConform.Apply(_plan, _grid, _waterLevel, _riverMask, n, half, seed, carveConnections: false);
            // Mountain water is a different system and runs after Conform, so the
            // rivers and the outlet guarantee below see tarns as seeded bodies.
            SeedMountainTarns();
            // RIVERS ARE THE PLAN'S CONNECTION RUNS — placement with provenance
            // (body→body, body→sea, spring→…), carved once, from the plan.
            // 0.2.803: big waterfall sinkholes end chosen runs in a shaft BEFORE the levels and the carve see them.
            // 0.2.859: with the planning ground (metres, bilinear, before the rivers are carved), so a run is only cut where
            // its sinkhole has room (FatSinkFallCuts.NodeRoom); the runtime check reads the final ground the same way.
            var planGrid = _grid; float planCell = (2f * half) / (n - 1);
            float PlanGround(float wx, float wz)
            {
                float gx = Mathf.Clamp((wx + half) / planCell, 0f, n - 1.001f), gz = Mathf.Clamp((wz + half) / planCell, 0f, n - 1.001f);
                int xi = (int)gx, zi = (int)gz, i = zi * n + xi; float fx = gx - xi, fz = gz - zi;
                return planGrid[i] * (1f - fx) * (1f - fz) + planGrid[i + 1] * fx * (1f - fz) + planGrid[i + n] * (1f - fx) * fz + planGrid[i + n + 1] * fx * fz;
            }
            FatSinkFallCuts.Run(_plan, seed, PlanGround);
            var connPolys = PlanConnectionsToPolys(_plan, _waterLevel, _grid, n, (2f * half) / (n - 1),
                                                   out int springRuns, out int culledSprings);
            FiresLogger.LogInfo($"[FiresWorldGen] rivers = plan connections: {(connPolys != null ? connPolys.Count : 0)} run(s) " +
                      $"({springRuns} spring-headed, {culledSprings} CULLED — no source pond ⇒ no river).");
            _riverMask = InciseRivers(_grid, n, seed, fill, connPolys, _waterLevel, _riverMask, out _waterLevel);
            FiresWaterConform.BuryAllEdges(_grid, _waterLevel, n, _riverMask);
            _plan.BuildIndex(n, half);
            FiresLogger.LogInfo($"[FiresWorldGen] water plan: {_plan.Bodies.Count} body(ies), " +
                      $"{_plan.Connections.Count} connection(s) in {swP.Elapsed.TotalSeconds:0.0}s.");
        }

        // The 8m class grid, STAMPED FROM THE PLAN instead of walked out of the
        // terrain. The reconcile CLASS FILL still classifies sea, channels and
        // swamp-biome sheets afterwards exactly as it did under the walk — this
        // replaces only what the walk itself used to stamp.
        private static void StampWaterClasses()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask;
            if (w == null || g == null || m == null) return;
            int n = _res;
            float ccm = (2f * _half) / (n - 1);
            int cstride = Mathf.Max(1, Mathf.RoundToInt(SURF_TARGET_CELL_M / ccm));
            int cn2 = (n + cstride - 1) / cstride;
            _classS = new byte[cn2 * cn2];
            _classN = cn2;
            _classCell = ccm * cstride;
            int bLake = 0, bPond = 0, bSwampN = 0, bFroz = 0, bHot = 0, empty = 0;
            float capHi = 0f;
            if (_plan != null)
                foreach (var body in _plan.Bodies)
                {
                    if (body.Kind == WaterBodyKind.Ocean || body.W <= 0 || body.H <= 0) continue;
                    byte cls;
                    if (body.Kind == WaterBodyKind.Marsh || body.Kind == WaterBodyKind.SeaMarsh) cls = WC_SWAMP;
                    else if (body.Kind == WaterBodyKind.Lake) cls = WC_LAKE;
                    else if (body.Kind == WaterBodyKind.Tarn) cls = WC_FROZEN;
                    else cls = WC_POND;
                    // Cold still water freezes wherever it sits — the walk own
                    // rule, kept verbatim: cold biome OR above 80m, ~1 in 7
                    // staying liquid as a hot spring.
                    if (cls == WC_LAKE || cls == WC_POND || cls == WC_FROZEN)
                    {
                        int mgx = Mathf.Clamp(body.Gx0 + body.W / 2, 0, n - 1);
                        int mgz = Mathf.Clamp(body.Gz0 + body.H / 2, 0, n - 1);
                        byte bio = _biomeMap != null ? _biomeMap[mgz * n + mgx] : (byte)0;
                        if (bio == 3 || bio == 7 || body.Level > 80f)
                            cls = WanderHash01(mgx, mgz, _seed ^ 0x2F5D) < 0.15f ? WC_HOTSPRING : WC_FROZEN;
                        else if (cls == WC_FROZEN) cls = WC_POND;
                    }
                    int stamped = 0;
                    for (int lz = 0; lz < body.H; lz++)
                    {
                        int gz = body.Gz0 + lz;
                        if (gz < 0 || gz >= n) continue;
                        int row = lz * body.W;
                        for (int lx = 0; lx < body.W; lx++)
                        {
                            if (body.Mask[row + lx] == 0) continue;
                            int gx = body.Gx0 + lx;
                            if (gx < 0 || gx >= n) continue;
                            int i = gz * n + gx;
                            if (m[i] == 1 || m[i] == 3) continue;
                            if (w[i] <= g[i] + 0.01f) continue;
                            _classS[(gz / cstride) * cn2 + (gx / cstride)] = cls;
                            stamped++;
                        }
                    }
                    if (stamped == 0) { empty++; continue; }
                    switch (cls)
                    {
                        case WC_LAKE: bLake++; break;
                        case WC_POND: bPond++; break;
                        case WC_SWAMP: bSwampN++; break;
                        case WC_FROZEN: bFroz++; break;
                        case WC_HOTSPRING: bHot++; break;
                    }
                    if (body.Level > capHi) capHi = body.Level;
                }
            // Mountain tarns are seeded AFTER the plan and own everything above
            // it, so they carry no body — classify their wet ground directly.
            long tarnCells = 0;
            if (_biomeMap != null)
                for (int gz = 0; gz < n; gz++)
                {
                    int crow = (gz / cstride) * cn2;
                    for (int gx = 0; gx < n; gx++)
                    {
                        int i = gz * n + gx;
                        if (m[i] == 1 || m[i] == 3) continue;
                        if (w[i] <= g[i] + 0.01f || w[i] <= SEA + 0.5f) continue;
                        byte bio = _biomeMap[i];
                        if (bio != 3 && bio != 7) continue;
                        int ci = crow + gx / cstride;
                        if (_classS[ci] != WC_NONE) continue;
                        _classS[ci] = WanderHash01(gx, gz, _seed ^ 0x2F5D) < 0.15f ? WC_HOTSPRING : WC_FROZEN;
                        tarnCells++;
                    }
                }
            s_repWalked = _plan != null ? _plan.Bodies.Count : 0;
            s_repKept = bLake + bPond + bSwampN + bFroz + bHot;
            s_repLake = bLake; s_repPond = bPond; s_repSwampB = bSwampN;
            s_repFrozen = bFroz; s_repHot = bHot;
            s_repRiverFed = _plan != null ? _plan.Connections.Count : 0;
            s_repCapHi = capHi;
            FiresLogger.LogInfo($"[FiresWorldGen] water classes stamped from the plan: {s_repKept} body(ies) " +
                      $"(lake {bLake}, pond {bPond}, swamp {bSwampN}, frozen {bFroz}, hotspring {bHot})" +
                      (empty > 0 ? $", {empty} planned body(ies) held no wet cell" : "") +
                      (tarnCells > 0 ? $"; {tarnCells:N0} seeded tarn cell(s)" : "") +
                      $"; highest level {capHi:0}m.");
        }

        // MOUNTAIN TARNS — the raw fill table never fills high basins (log:
        // highest natural spill 51m on a mountain-covered seed), so cold
        // biomes get their rare still water SEEDED: hash-lattice bowls the
        // walk then floods, flattens, excavates and classifies via rim-bio →
        // FROZEN / hot spring. This is the substrate the freeze rules were
        // starving for.
        private static void SeedMountainTarns()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask; var b = _biomeMap;
            if (w == null || g == null || m == null || b == null) return;
            int n = _res;
            float cellM = (2f * _half) / (n - 1);
            int step = Mathf.Max(4, Mathf.RoundToInt(420f / cellM));
            int o8 = Mathf.Max(1, Mathf.RoundToInt(8f / cellM));
            int seeded = 0;
            for (int gz = step / 2; gz < n; gz += step)
                for (int gx = step / 2; gx < n; gx += step)
                {
                    if (WanderHash01(gx, gz, _seed ^ 0x7A11) >= 0.5f) continue;
                    int px = gx + Mathf.RoundToInt((WanderHash01(gx, gz, _seed ^ 0x7A12) - 0.5f) * step * 0.7f);
                    int pz = gz + Mathf.RoundToInt((WanderHash01(gx, gz, _seed ^ 0x7A13) - 0.5f) * step * 0.7f);
                    if (px < o8 || pz < o8 || px >= n - o8 || pz >= n - o8) continue;
                    int ci = pz * n + px;
                    byte bio = b[ci];
                    if (bio != 3 && bio != 7) continue;          // Mountain / DeepNorth ground only
                    if (m[ci] != 0) continue;
                    float g0 = g[ci];
                    if (w[ci] > g0 + 0.01f) continue;
                    // BOTH SIDES: DIG the high side out (headwall, however
                    // tall) AND BUILD the low bank up — a berm wedge from the
                    // waterline descending outward at 0.55 m/m until it
                    // merges with natural grade. Continuous solid fill, so it
                    // reads as an earthen shore, never the old knife-edge
                    // ring. Only a river in the footprint skips a site.
                    float R = 6f + WanderHash01(px, pz, _seed ^ 0x7A14) * 12f;
                    int rc = Mathf.CeilToInt(R / cellM);
                    int rcFill = rc + Mathf.CeilToInt(40f / cellM);   // 0.32 wedge needs ~12m drop coverage
                    if (px < rcFill || pz < rcFill || px >= n - rcFill || pz >= n - rcFill) continue;
                    float ringMin = float.MaxValue, ringMax = float.MinValue;
                    bool blocked = false;
                    for (int dz = -rcFill; dz <= rcFill && !blocked; dz++)
                        for (int dx = -rcFill; dx <= rcFill; dx++)
                        {
                            float rm2 = Mathf.Sqrt(dx * dx + dz * dz) * cellM;
                            if (rm2 >= R + 2.5f) continue;
                            int i2 = (pz + dz) * n + (px + dx);
                            if (m[i2] != 0) { blocked = true; break; }   // river in the footprint
                            if (rm2 >= R)
                            {
                                if (g[i2] < ringMin) ringMin = g[i2];
                                if (g[i2] > ringMax) ringMax = g[i2];
                            }
                        }
                    if (blocked || ringMin == float.MaxValue) continue;
                    // CLIFF-STRADDLE VETO: a ring spanning a big drop anchors
                    // the level at the cliff BASE and the dig then quarries
                    // the high side 20-50m down — the "massive holes that
                    // aren't even sinkholes" (user screenshots). A tarn needs
                    // a feasible bowl; the lattice just rolls elsewhere.
                    if (ringMax - ringMin > 7f) continue;
                    // Level sits a touch above the lowest rim: the low side
                    // then needs a MODEST berm (which buries the shoreline),
                    // while the high side still reads as dug-in.
                    // STRICTLY below the lowest bank (user: "drops a bit below
                    // the lowest bank and levels perfectly"). The old relief
                    // bonus could push the level ABOVE ringMin on a high-relief
                    // ring — that lowest bank then floated. −0.35 guarantees
                    // every bank stands proud; the flatten levels it perfectly.
                    float lvl = ringMin - 0.35f;
                    var stT = WaterStyleFor(bio);
                    for (int dz = -rcFill; dz <= rcFill; dz++)
                        for (int dx = -rcFill; dx <= rcFill; dx++)
                        {
                            float rm2 = Mathf.Sqrt(dx * dx + dz * dz) * cellM;
                            int i2 = (pz + dz) * n + (px + dx);
                            if (rm2 < R)
                            {
                                float rr = rm2 / R;
                                float bed = lvl - (1.2f + stT.PondDepth * (1f - rr * rr));
                                if (g[i2] > bed) g[i2] = bed;   // headwall cut included
                                if (w[i2] < lvl) w[i2] = lvl;
                            }
                            else if (m[i2] == 0 && w[i2] <= g[i2] + 0.01f)
                            {
                                // low-bank berm: 3m crest plateau (the sheet's
                                // 3-cell skirt must die INSIDE the berm body),
                                // then a gentle 0.32 m/m wedge merging with
                                // grade — 0.55 read as an obvious dike.
                                float wedge = lvl + stT.BermH - 0.32f * Mathf.Max(0f, rm2 - R - 3f);
                                if (g[i2] < wedge) g[i2] = wedge;
                            }
                        }
                    s_seedLevels[SeedKey(px, pz)] = lvl;
                    seeded++;
                }
            s_repTarns = seeded;
            if (seeded > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] mountain tarns seeded: {seeded} cold-biome bowls (the walk flattens, excavates and freezes them).");
        }

        // ── ZONE SURVEY (LPA architecture: survey once, place from candidate
        // pools) ─────────────────────────────────────────────────────────
        // Per 64m zone, computed from OUR OWN grids BEFORE the seeders run:
        // biome bits, altitude min/max, mean slope, river presence. Seeders
        // filter this into candidate pools and place QUOTAS with spacing —
        // exact counts and shortfall accounting instead of lattice chance.
        private struct ZoneSurvey
        {
            public ushort BiomeBits;   // 1<<code
            public float AltMin, AltMax;
            public float MeanSlope;    // m/m
            public bool HasRiver;
        }
        private static ZoneSurvey[] _survey;
        private static int _surveyN, _surveyHalfN;

        private static void BuildZoneSurvey()
        {
            var g = _grid; var b = _biomeMap; var m = _riverMask;
            if (g == null || b == null || m == null) return;
            int n = _res;
            float cellM = (2f * _half) / (n - 1);
            _surveyHalfN = Mathf.CeilToInt(_half / 64f) + 2;
            _surveyN = _surveyHalfN * 2 + 1;
            var sv = new ZoneSurvey[_surveyN * _surveyN];
            for (int i = 0; i < sv.Length; i++) { sv[i].AltMin = float.MaxValue; sv[i].AltMax = float.MinValue; }
            var slopeSum = new float[_surveyN * _surveyN];
            var slopeCnt = new int[_surveyN * _surveyN];
            int stride = Mathf.Max(1, Mathf.RoundToInt(8f / cellM));
            for (int gz = stride; gz < n - stride; gz += stride)
            {
                float wz = gz * cellM - _half;
                int zz = Mathf.FloorToInt((wz + 32f) / 64f) + _surveyHalfN;
                if (zz < 0 || zz >= _surveyN) continue;
                for (int gx = stride; gx < n - stride; gx += stride)
                {
                    float wx = gx * cellM - _half;
                    int zx = Mathf.FloorToInt((wx + 32f) / 64f) + _surveyHalfN;
                    if (zx < 0 || zx >= _surveyN) continue;
                    int zi = zz * _surveyN + zx;
                    int i = gz * n + gx;
                    float h = g[i];
                    if (h < sv[zi].AltMin) sv[zi].AltMin = h;
                    if (h > sv[zi].AltMax) sv[zi].AltMax = h;
                    byte bio = b[i];
                    if (bio < 16) sv[zi].BiomeBits |= (ushort)(1 << bio);
                    if (m[i] != 0) sv[zi].HasRiver = true;
                    float sx = Mathf.Abs(g[i + stride] - g[i - stride]);
                    float sz = Mathf.Abs(g[i + stride * n] - g[i - stride * n]);
                    slopeSum[zi] += Mathf.Sqrt(sx * sx + sz * sz) / (2f * stride * cellM);
                    slopeCnt[zi]++;
                }
            }
            for (int i = 0; i < sv.Length; i++)
                sv[i].MeanSlope = slopeCnt[i] > 0 ? slopeSum[i] / slopeCnt[i] : 0f;
            _survey = sv;
        }

        private static bool TryGetSurvey(int zx, int zz, out ZoneSurvey s)
        {
            s = default;
            var sv = _survey;
            if (sv == null) return false;
            int ix = zx + _surveyHalfN, iz = zz + _surveyHalfN;
            if (ix < 0 || iz < 0 || ix >= _surveyN || iz >= _surveyN) return false;
            s = sv[iz * _surveyN + ix];
            return s.AltMin != float.MaxValue;
        }

        // DESPIKE — erosion deposition wrote sediment onto single bilinear
        // corners (the carve used a radius-2 brush, the deposit did not), and
        // repeated point dumps on steep hillsides left one-cell pillars the
        // mesher renders as sharp pyramid tents ("points on our steep
        // hills"). Cap any cell standing above ALL four neighbours; ridges
        // and cliff edges keep a high neighbour and are untouched. Runs every
        // load so existing settled caches heal without a format bump; two
        // passes catch the 2-cell stacks. Dry cells keep w glued to g so a
        // lowered spike can never read as phantom water.
        private static void DespikeGrid()
        {
            var g = _grid; var w = _waterLevel;
            if (g == null || w == null) return;
            int n = _res;
            int fixedCells = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int z = 1; z < n - 1; z++)
                {
                    int row = z * n;
                    for (int x = 1; x < n - 1; x++)
                    {
                        int i = row + x;
                        float nb = g[i - 1];
                        if (g[i + 1] > nb) nb = g[i + 1];
                        if (g[i - n] > nb) nb = g[i - n];
                        if (g[i + n] > nb) nb = g[i + n];
                        float cap = nb + 1.2f;
                        if (g[i] > cap)
                        {
                            bool dry = w[i] <= g[i] + 0.01f;
                            g[i] = cap;
                            if (dry && w[i] > cap) w[i] = cap;
                            fixedCells++;
                        }
                    }
                }
                // Radius-2 ring: multi-cell CONES (stacked deposition tents).
                // A ridge LINE keeps a high ring cell along its crest and is
                // untouched; an isolated cone steeper than ~31° to the ring
                // gets its tip capped — spikes round into mounds.
                int n2r = 2 * n;
                for (int z = 2; z < n - 2; z++)
                {
                    int row = z * n;
                    for (int x = 2; x < n - 2; x++)
                    {
                        int i = row + x;
                        float nb = g[i - 2];
                        if (g[i + 2] > nb) nb = g[i + 2];
                        if (g[i - n2r] > nb) nb = g[i - n2r];
                        if (g[i + n2r] > nb) nb = g[i + n2r];
                        if (g[i - n2r - 2] > nb) nb = g[i - n2r - 2];
                        if (g[i - n2r + 2] > nb) nb = g[i - n2r + 2];
                        if (g[i + n2r - 2] > nb) nb = g[i + n2r - 2];
                        if (g[i + n2r + 2] > nb) nb = g[i + n2r + 2];
                        float cap = nb + 2.4f;
                        if (g[i] > cap)
                        {
                            bool dry = w[i] <= g[i] + 0.01f;
                            g[i] = cap;
                            if (dry && w[i] > cap) w[i] = cap;
                            fixedCells++;
                        }
                    }
                }
            }
            if (fixedCells > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] despike: {fixedCells:N0} spike/cone cells rounded (r1 +1.2m, r2 ring +2.4m).");
        }







        // Load-time ACCEPTANCE AUDIT — the never-above-banks invariant,
        // verified against the exact field the session will play on. A
        // violation = a wet cell whose 3×3 holds a DRY cell with ground
        // below the water level − 0.35 (a water wall in-game). MUST be 0;
        // the first few violations log world coordinates so a screenshot
        // can be matched to the data cell-for-cell.
        // RUN THE GAME'S OWN AUDIT IN THE STUDIO, BEFORE ANYTHING IS BAKED.
        // The audit only ever ran on load, which meant the first time anyone saw
        // "inland above-bank violations 86,992" was after a cache had been baked,
        // deployed and played. It asks a question the studio can answer just as
        // well — is any wet cell standing over a DRY NEIGHBOUR's ground — so it
        // gets asked there too, while the world can still be rejected.
        internal static void AuditImportedWater() => AuditWater();

        private static void AuditWater()
        {
            var w = _waterLevel; var g = _grid; var m = _riverMask;
            if (w == null || g == null || m == null) return;
            int n = _res;
            float half = _half;
            float cell = (2f * half) / (n - 1);
            int wet = 0, violIn = 0, violRim = 0;
            float lvMin = float.MaxValue, lvMax = float.MinValue;
            int violChanChan = 0, violChanOnly = 0, violNbrChan = 0, violPlain = 0, violOverCap = 0;
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
                    // WHICH REMEDY DECLINED. Bank enforcement can only build a
                    // bank when the DRY neighbour is not a channel (m[j]==0),
                    // and can only retreat the edge when the WET cell is not a
                    // channel. A violation is therefore fixable, or it fell
                    // through that hole — and the fix differs per case, so the
                    // audit has to say which rather than just counting.
                    bool selfChan = mk == 1 || mk == 3;
                    bool nbrChan = false; float worstDrop = 0f;
                    for (int e = 0; e < 8; e++)
                    {
                        int j = (z + RIV_DZ[e]) * n + (x + RIV_DX[e]);
                        byte mj = m[j];
                        bool jWet = (mj == 1 || mj == 3) ? w[j] - g[j] >= 0.4f : w[j] - g[j] >= 1.2f;
                        if (jWet || g[j] >= w[i] - 0.35f) continue;
                        bad = true;
                        float drop = w[i] - g[j];
                        if (drop > worstDrop) { worstDrop = drop; nbrChan = mj != 0; }
                    }
                    if (!bad) continue;
                    if (selfChan && nbrChan) violChanChan++;
                    else if (selfChan) violChanOnly++;
                    else if (nbrChan) violNbrChan++;
                    else violPlain++;
                    if (worstDrop > FiresGenTuning.BankBuildCap) violOverCap++;
                    // The world-rim fade band (r ≥ ~9.3km) crushes terrain
                    // under existing water — perpetual violations in the
                    // unplayable margin. Acceptance is the INLAND count.
                    float wx = -half + x * cell, wz = -half + z * cell;
                    if (wx * wx + wz * wz >= (PlayableRadius - 700f) * (PlayableRadius - 700f)) { violRim++; continue; }   // 9300 at vanilla size
                    violIn++;
                    if (violIn <= 5)
                        examples.Append($" ({wx:0},{wz:0})Δ{w[i] - g[i]:0.0}m");
                }
            }
            FiresLogger.LogInfo($"[FiresWorldGen] WATER AUDIT: {wet:N0} wet cells, lvl {lvMin:F1}-{lvMax:F1}, " +
                      $"inland above-bank violations {violIn} (rim band: {violRim})" +
                      (violIn > 0 ? $" — FAIL, first:{examples}" : " — PASS."));
            if (violIn > 0)
                FiresLogger.LogInfo($"[FiresWorldGen] WATER AUDIT breakdown: channel-beside-channel {violChanChan:N0} " +
                          $"(neither remedy applies) | channel-beside-plain {violChanOnly:N0} | plain-beside-channel {violNbrChan:N0} | " +
                          $"plain-beside-plain {violPlain:N0} (should be zero — enforcement covers these) | " +
                          $"drop over the {FiresGenTuning.BankBuildCap:0.#}m bank cap {violOverCap:N0}.");
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
            var g = _gridS;
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
            float h = (g[i] * (1f - fx) * (1f - fz) + g[i + 1] * fx * (1f - fz)
                    + g[i + n] * (1f - fx) * fz + g[i + n + 1] * fx * fz) * UNPACK;

            float baseN = h / 200f;
            float r = Mathf.Sqrt(wx * wx + wy * wy);
            // 0.2.722: the fade to sea starts at the world's radius and the cliff stands 10 m inside its edge (FatWorldSize;
            // 10000 / 500 m / 10490 on a vanilla-size world, the same floats as before).
            float radius = FatWorldSize.Radius, edge = FatWorldSize.EdgeRadius;
            if (r > radius)
            {
                baseN = Mathf.Lerp(baseN, -0.2f, Mathf.Clamp01((r - radius) / (edge - radius)));
                if (r > edge - 10f)
                    baseN = Mathf.Lerp(baseN, -2f, Mathf.Clamp01((r - (edge - 10f)) / 10f));
            }
            return baseN;
        }

        // ── generation ──────────────────────────────────────────────────────

        private static float[] BuildGrid(int seed, int res, int droplets, out byte[] riverMask, out float[] waterLevel)
        {
            int n = res;
            var h = new float[n * n];
            float cell = (2f * HALF) / (n - 1);
            // 0.2.722 (FatWorldSize, read once for the build): the grid's span, the playable radius, the edge and the ring scale.
            // Vanilla size: 10500 / 10000 / 10500 / 1, so every expression below yields the floats it always did.
            float halfSpan = HALF, worldR = FatWorldSize.Radius, worldEdge = FatWorldSize.EdgeRadius, worldK = FatWorldSize.Scale;
            int s = seed * 7;
            var gsw = System.Diagnostics.Stopwatch.StartNew();
            FiresLogger.LogInfo($"[FiresWorldGen] GENERATING {n}x{n} grid (~{cell:F1}m cells) — first-load build. On a 2m grid this takes SEVERAL MINUTES (mono is ~3x the offline tool); watch these stage logs so you know it is alive, not hung.");

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
            int reliefRows = 0;
            int reliefStep = Math.Max(1, n / 5);
            Parallel.For(0, n, z =>
            {
                int rowsDone = System.Threading.Interlocked.Increment(ref reliefRows);
                if (rowsDone % reliefStep == 0 && rowsDone < n)
                    FiresLogger.LogInfo($"[FiresWorldGen]  │   base relief {rowsDone * 100L / n}% ({gsw.Elapsed.TotalSeconds:0}s)");
                for (int x = 0; x < n; x++)
                {
                    float wx = -halfSpan + x * cell;
                    float wz = -halfSpan + z * cell;
                    float r = Mathf.Sqrt(wx * wx + wz * wz);

                    float qx = wx + (Fbm(wx, wz, 1f / 1800f, 3, s + 401) - 0.5f) * 1300f;
                    float qz = wz + (Fbm(wx, wz, 1f / 1800f, 3, s + 402) - 0.5f) * 1300f;

                    float c = Fbm(qx, qz, 1f / 5200f, 4, s + 11);
                    c += (Fbm(qx, qz, 1f / 1400f, 3, s + 12) - 0.5f) * 0.52f;
                    c += 0.36f * (1f - Smooth(worldR - 600f, worldR + 200f, r));        // land to the world edge (9400..10200)
                    c -= Smooth(worldR - 500f, worldEdge - 100f, r) * 0.7f;            // thin rim margin (9500..10400)
                    c -= Smooth(0.55f, 0.75f, Fbm(qx, qz, 1f / 3400f, 3, s + 15)) * 0.62f;   // inland seas
                    c += Mathf.Exp(-(r * r) / (650f * 650f)) * 0.22f;    // gentle spawn dome
                    // Polar arcs (Ashlands south / DeepNorth north) get
                    // guaranteed land — their locations need ground.
                    float arcC = 3810f * worldK, arcR = 11430f * worldK;   // vanilla's polar arcs, scaled with the world
                    float ashD = Mathf.Sqrt(wx * wx + (wz - arcC) * (wz - arcC)) - arcR;
                    float dnD = Mathf.Sqrt(wx * wx + (wz + arcC) * (wz + arcC)) - arcR;
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
                    // TWO nearest DISTINCT crest identities, not one. The old
                    // nearest-segment-only lookup stepped the along-coordinate
                    // (and with it the whole 200-440m profile) wherever the
                    // winning segment switched — two systems passing near each
                    // other, or one range hairpinning onto itself — and the
                    // equidistance line rendered as a dead-straight sheer wall
                    // spanning whole zones with a mesa top (user screenshots:
                    // "looks like the height got broken"). Segments within
                    // 420m of along-distance are the SAME crest (continuous
                    // profile — no blend needed); a second identity further
                    // away gets tracked and blended below.
                    float dmin = 1e9f, dmin2 = 1e9f;
                    float along = 0f, along2 = 0f;
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
                        float al = segs[g2 * 5 + 4] + tseg * Mathf.Sqrt(segLen2);
                        if (dd < dmin)
                        {
                            if (Mathf.Abs(al - along) > 420f) { dmin2 = dmin; along2 = along; }
                            dmin = dd; along = al;
                        }
                        else if (dd < dmin2 && Mathf.Abs(al - along) > 420f)
                        {
                            dmin2 = dd; along2 = al;
                        }
                    }
                    // v14 profile gaps stay (pass valleys); v33 lifts the
                    // budget into the 400-600m band: crests ~200m over the
                    // uplands, TRUE summits spike another ~240m, alpine
                    // masses stack the rest.
                    float radialT = Smooth(1100f, 2200f, r);
                    float shaped = Smooth(0.32f, 0.58f, Fbm(along, 811f, 1f / 1050f, 2, s + 77));
                    float summits = Smooth(FiresGenTuning.MountainSummitGate, 0.95f, Fbm(along, 407f, 1f / 640f, 1, s + 79));
                    float ridgeW = FiresGenTuning.MountainRidgeWidth * (0.55f + 0.9f * Fbm(along, 133f, 1f / 800f, 2, s + 85));
                    float ridgeT = Mathf.Exp(-dmin / (ridgeW * ridgeW));
                    float lift1 = shaped * FiresGenTuning.MountainCrestAmp + summits * FiresGenTuning.MountainSummitAmp;
                    // Weight-blend the second identity in: far from it this is
                    // exactly the old single-crest lift; on the equidistance
                    // band the two profiles average into a saddle/pass instead
                    // of a wall. Continuous everywhere by construction.
                    float ridgeT2 = 0f, lift2 = 0f;
                    if (dmin2 < 1e8f)
                    {
                        float ridgeW2 = FiresGenTuning.MountainRidgeWidth * (0.55f + 0.9f * Fbm(along2, 133f, 1f / 800f, 2, s + 85));
                        ridgeT2 = Mathf.Exp(-dmin2 / (ridgeW2 * ridgeW2));
                        if (ridgeT2 > 0.002f)
                        {
                            float shaped2 = Smooth(0.32f, 0.58f, Fbm(along2, 811f, 1f / 1050f, 2, s + 77));
                            float summits2 = Smooth(FiresGenTuning.MountainSummitGate, 0.95f, Fbm(along2, 407f, 1f / 640f, 1, s + 79));
                            lift2 = shaped2 * FiresGenTuning.MountainCrestAmp + summits2 * FiresGenTuning.MountainSummitAmp;
                        }
                        else ridgeT2 = 0f;
                    }
                    float wSum = ridgeT + ridgeT2;
                    if (wSum > 1e-6f)
                        y += (ridgeT * lift1 + ridgeT2 * lift2) / wSum
                           * Mathf.Max(ridgeT, ridgeT2) * radialT;

                    // Broad foot apron under the nearest crest: a wide, low rise
                    // (Gaussian several times wider than the crest itself) so the
                    // massif climbs out of the plains gradually and its flanks
                    // taper back to level ground, instead of the sharp crest
                    // erupting straight out of the uplands (the "rolling hill then
                    // spike" look). Set MountainApronAmp=0 for the old sharp foot.
                    if (FiresGenTuning.MountainApronAmp > 0.01f)
                    {
                        float apronW = ridgeW * FiresGenTuning.MountainApronWidthMul;
                        y += Mathf.Exp(-dmin / (apronW * apronW)) * FiresGenTuning.MountainApronAmp * radialT;
                    }

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

                    // v21 — MOUNTAIN RIDGE STRUCTURE: the range + alpine lifts are
                    // smooth domes; real mountains have crests, spurs and gullies.
                    // Layer ridged relief scaled by how high we already are (nil in
                    // lowland, full above the ~85m mountain line) so ranges read as
                    // ridgelines, not blobs — two scales (broad crest spines + finer
                    // spurs). The erosion pass then bites these into a dissected
                    // massif. World-meter noise = resolution-independent.
                    float mtnT = Smooth(85f, 140f, y);
                    if (mtnT > 0.001f)
                    {
                        // Amplitudes tamed again (34/12 → 24/8): even with
                        // the talus pass, the raw field kept throwing spike
                        // fins with crater Vs beside them (user screenshots);
                        // the ridged STRUCTURE survives at lower amplitude
                        // and the 45° mountain talus mounds what remains.
                        float crest = Ridged(wx, wz, 1f / 340f, 4, s + 205) - 0.5f;
                        float spur = Ridged(wx, wz, 1f / 140f, 3, s + 206) - 0.5f;
                        y += mtnT * (crest * FiresGenTuning.MountainStructAmp + spur * FiresGenTuning.MountainStructAmp * 0.33f);
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
                                float arcAlong = theta * Mathf.Deg2Rad * R;   // arc-length coordinate
                                // v11 — squared chain profile: passes drop low,
                                // summits spike, instead of one rounded wall.
                                float cn = Fbm(arcAlong, ci * 173f, 1f / 240f, 2, s + 81);
                                float chain = 0.35f + 1.05f * cn * cn;
                                // Occasional true PEAKS (~600m apart, top ~15%
                                // of the arc) — spires rising well above the
                                // chain so ranges get summits, not just crest.
                                float peaks = Smooth(0.74f, 0.94f, Fbm(arcAlong, ci * 91f, 1f / 620f, 1, s + 83));
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

            FiresLogger.LogInfo($"[FiresWorldGen]  ├ base relief done ({gsw.Elapsed.TotalSeconds:0}s); eroding {(long)droplets * n * n / (2048L * 2048L):N0} droplets...");
            Erode(h, n, droplets, seed ^ 0x51ED, halfSpan);
            FiresLogger.LogInfo($"[FiresWorldGen]  ├ erosion done ({gsw.Elapsed.TotalSeconds:0}s); relaxing slopes...");
            // THERMAL RELAXATION before the river carve. The old single 58°
            // limit (3.2m over 2m) let the ridged field's knife fins STAND —
            // giant spikes with crater-deep Vs beside them ("that's not what
            // a landslide looks like" — user screenshots). Real scree rests
            // near 45°: above the mountain line the limit tightens to 2.0m
            // per cell and a fourth pass runs, so fins shed into mounded
            // aprons and the Vs between them FILL (the moved material lands
            // in the lows — that IS the landslide look). Runs BEFORE
            // CarveRivers so channel walls are never touched.
            for (int pass = 0; pass < 4; pass++)
            {
                for (int z2 = 1; z2 < n - 1; z2++)
                {
                    int row2 = z2 * n;
                    for (int x2 = 1; x2 < n - 1; x2++)
                    {
                        int i2 = row2 + x2;
                        float hc = h[i2];
                        int lo = i2;
                        float hl = hc;
                        if (h[i2 - 1] < hl) { hl = h[i2 - 1]; lo = i2 - 1; }
                        if (h[i2 + 1] < hl) { hl = h[i2 + 1]; lo = i2 + 1; }
                        if (h[i2 - n] < hl) { hl = h[i2 - n]; lo = i2 - n; }
                        if (h[i2 + n] < hl) { hl = h[i2 + n]; lo = i2 + n; }
                        float d2 = hc - hl;
                        float tal = hc > 80f ? 2.0f : 3.2f;
                        if (d2 > tal)
                        {
                            float move = (d2 - tal) * 0.5f;
                            h[i2] -= move;
                            h[lo] += move;
                        }
                    }
                }
            }
            FiresLogger.LogInfo($"[FiresWorldGen]  ├ slopes relaxed ({gsw.Elapsed.TotalSeconds:0}s); carving rivers (the longest stage)...");
            riverMask = CarveRivers(h, n, seed, out waterLevel);
            FiresLogger.LogInfo($"[FiresWorldGen]  └ rivers carved ({gsw.Elapsed.TotalSeconds:0}s) — base grid complete; building biomes + saving cache next.");
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
            float R = FatWorldSize.Radius;   // 0.2.722: the world's radius (10000 on a vanilla-size world; k below = R / 10000)
            int s = seed * 7;
            float cellM = (2f * half) / (n - 1);
            var b = new byte[n * n];

            // W5 — CLIMATE-DRIVEN BIOMES. Temperature, wind-swept moisture
            // (rain shadow), HAND floodplains and valley enclosure replace
            // the radius rings + noise pockets: features land where the
            // geography says they belong. Fields build at 8m from the FINAL
            // relief + rivers (both exist by this point).
            if (generated)
                FiresClimate.Build(h, _waterLevel, _riverMask, n, half, seed);

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
                _volcanoDraws = volc;   // lava mask reads these
            }
            bool climate = generated && FiresClimate.M != null;

            Parallel.For(0, n, z =>
            {
                for (int x = 0; x < n; x++)
                {
                    int i = z * n + x;
                    float wx = -half + x * cellM;
                    float wz = -half + z * cellM;
                    float r = Mathf.Sqrt(wx * wx + wz * wz);
                    float hh = h[i];

                    // Polar crescents stay geometric (guaranteed-land arcs in
                    // the relief carry them); everything between resolves
                    // from climate. Priority: geology > cryosphere >
                    // hydrology > Whittaker core.
                    float k = R / 10000f;
                    float ang = Mathf.Sin(Mathf.Atan2(wx, wz) * 20f) * 100f * k;
                    float dzS = wz - 4000f * k, dzN = wz + 4000f * k;
                    float bj = (Fbm(wx, wz, 1f / 420f, 2, s + 61) - 0.5f) * 26f;
                    // NO BULLSEYE. Every radial gate below is perturbed by this
                    // before it is compared, so no biome boundary is ever a
                    // circle centred on spawn. `ang` alone (±100m) is far too
                    // small to hide a 2400m cut — the Meadows disc around spawn
                    // was exactly that cut, seen from above. A ~2.6km fbm swing
                    // makes the same threshold wander between roughly a third
                    // and twice its nominal radius, which reads as coastline,
                    // not as geometry.
                    float rJit = ang * 4f + (Fbm(wx, wz, 1f / 2600f, 2, s + 71) - 0.5f) * 2600f * k;

                    byte v;
                    if (hh < 22f) v = 8;                                                   // Ocean (deep)
                    else if (Mathf.Sqrt(wx * wx + dzS * dzS) > 12000f * k + ang) v = 6;    // AshLands (south crescent)
                    else if (Mathf.Sqrt(wx * wx + dzN * dzN) > 12000f * k + ang)
                        v = hh > 80f + bj ? (byte)3 : (byte)7;                             // DeepNorth (north crescent)
                    else if (!climate)
                    {
                        // fallback (image/VAHC bases without climate): old ladder core
                        if (hh > 80f + bj) v = 3;
                        else if (Fbm(wx, wz, 1f / 1000f, 2, s + 51) > 0.53f
                                 && r > 1800f * k && r < 8000f * k && hh < 52f + bj * 0.5f) v = 2;
                        else if (Fbm(wx, wz, 1f / 1000f, 2, s + 52) > 0.62f
                                 && r > 6000f * k + ang && r < 9900f * k) v = 5;
                        else if (Fbm(wx, wz, 1f / 1000f, 2, s + 53) > 0.58f
                                 && r > 3000f * k + ang && r < 8500f * k && hh < 70f + bj * 0.5f) v = 4;
                        else if ((Fbm(wx, wz, 1f / 1000f, 2, s + 54) > 0.40f && r > 600f * k + ang && r < 6000f * k)
                                 || r > 5000f * k + ang) v = 1;
                        else v = 0;
                    }
                    else
                    {
                        float tC = FiresClimate.SampleT(wx, wz);
                        float mC = FiresClimate.SampleM(wx, wz);
                        // 2m slope (central difference, edge cells read flat)
                        float sl = 0f;
                        if (x > 0 && z > 0 && x + 1 < n && z + 1 < n)
                        {
                            float dhx = h[i + 1] - h[i - 1];
                            float dhz = h[i + n] - h[i - n];
                            sl = Mathf.Sqrt(dhx * dhx + dhz * dhz) / (4f * cellM);
                        }
                        // CRYOSPHERE — the snowline is T, not one altitude:
                        // it rides low near the north pole, high in the warm
                        // south. Inland DeepNorth pockets only in the north
                        // third (gameplay: DN fauna stays northern).
                        // Gates read live-tunable dials (FiresGenTuning) whose
                        // defaults equal the shipped constants — the studio
                        // overrides them to dial biomes in without a game wipe.
                        // MOUNTAIN FIRST, ON ELEVATION. Temperature-gating it
                        // handed every high (and therefore dry) cell to Mountain
                        // or DeepNorth, which is why the dry end of the moisture
                        // percentile never reached Plains and the mid band never
                        // reached Meadows — both 0.0%. Elevation is also simply
                        // what a Valheim mountain is. The cold snowline stays as
                        // a POLAR secondary so northern highlands still ice over.
                        if (hh - SEA > FiresGenTuning.MountainStartM) v = 3;
                        else if (tC < FiresGenTuning.DeepNorthTemp && wz < -2500f * k) v = 7;
                        else if (tC < FiresGenTuning.MountainSnowTemp) v = 3;
                        // SWAMP DISTRICTS — broad, chunky LOWLAND regions, not
                        // HAND floodplain ribbons (user: swamps came out "just
                        // a river"). A ~1.5km district noise picks swamp
                        // COUNTRY inside genuinely low + flat + moist land; the
                        // sheet-lay then floods each region below its own
                        // border level so terrain dips in and out of one flat
                        // sheet (the vanilla-sea model at any altitude).
                        else if (Fbm(wx, wz, 1f / 1500f, 2, s + 58) > FiresGenTuning.SwampNoiseGate
                                 && sl < FiresGenTuning.SwampSlopeMax && mC > FiresGenTuning.SwampMoistMin
                                 && hh < SEA + FiresGenTuning.SwampAltMax && r > 1400f * k + rJit) v = 2;
                        // Mistlands — moist WALLED VALLEYS (enclosure), kept
                        // to the outer half for progression.
                        else if (FiresClimate.SampleEnc(wx, wz) > FiresGenTuning.MistEncMin
                                 && mC > FiresGenTuning.MistMoistMin && r > FiresGenTuning.MistRadius * k + rJit) v = 5;
                        // PLAINS — dry flat rain-shadow grassland, and ONLY that.
                        // The old rule had a second "hot-flat savanna" branch
                        // (tC > 11 && mC < 0.52) bolted on because Plains was
                        // coming out 0% — but that was the broken moisture
                        // field, which read 0.000 across every land cell and
                        // CDF'd to a step at 0.728 so no dry percentile existed
                        // at all. With moisture a real land-area percentile the
                        // branch is actively harmful: mC < 0.52 is "the driest
                        // HALF of the world", so Plains swallowed everything
                        // warm below the median and Meadows was left with only
                        // the 0.52-0.56 sliver plus its forced spawn blob
                        // (studio render, seed -2062588818: Plains 20.3% and
                        // Meadows visible only as the circle at the centre).
                        // Dry percentile alone now does the job it was always
                        // meant to.
                        // Progression still wants Plains away from spawn, but a
                        // RADIUS cannot express that without drawing a circle —
                        // and scaling the dryness threshold by a ramp was worse
                        // (near spawn it demanded the driest ~18% of all land,
                        // so Plains never fired and Meadows, the catch-all,
                        // painted a solid 100% disc out to 1km). Keep the dry
                        // rule intact and make ACCEPTANCE patchy instead: a
                        // chunky noise must fall under a radial bias that is ~0
                        // at spawn and 1 by ~2.7km, with rJit wandering the
                        // transition. Near spawn Plains is rare and scattered
                        // rather than forbidden, and the boundary has no centre.
                        else if (sl < FiresGenTuning.PlainsSlopeMax && mC < FiresGenTuning.PlainsDryMax
                                 && Fbm(wx, wz, 1f / 1800f, 2, s + 73)
                                    < Mathf.Clamp01((r - 500f * k + rJit) / (2200f * k))) v = 4;
                        // the moist tail = BlackForest; mild middle = Meadows.
                        else if (mC > FiresGenTuning.BlackForestMoistMin) v = 1;
                        else v = 0;
                    }

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
            var rsw = System.Diagnostics.Stopwatch.StartNew();
            var w = PriorityFloodFill(h, n, "drainage fill");
            int ms = seed * 7 + 977;
            // Resolution independence: the hydrology is authored at the 2048
            // reference grid. RS rescales every cell-space frequency/distance
            // and cellA converts flow accumulation (a CELL COUNT) into physical
            // drainage AREA (m²) — so rivers, valleys and erosion are the SAME
            // physical features whether a cell is 10m or 2m. RS==1 at n=2048.
            float cellM = (2f * _half) / (n - 1);
            float cellA = cellM * cellM;
            float RS = 2047f / (n - 1) * SpanScale;   // 0.2.722: x (edge / 10500), exactly 1 on a vanilla-size world

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
            wr = PriorityFloodFill(wr, n, "corrugated re-fill");   // re-fill so corrugation pits don't strand flow
            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: corrugated ({rsw.Elapsed.TotalSeconds:0}s); computing flow directions...");

            // v30 — flow structure FIRST (D8 single-flow tree on the
            // corrugated fill surface), and EVERYTHING downstream reads it:
            // the v18 incision previously used the braided multi-flow
            // accumulation and cut every corrugation groove 3-14m deep —
            // parallel dry slit channels beside the painted rivers
            // (live-cache-proven; unaffected by any mask/paint fix because
            // the grooves are in the TERRAIN, not the water field).
            // v32 — D-INFINITY flow (Tarboton 1997): CONTINUOUS flow direction,
            // replacing the D8 single-flow that locked every channel to one of 8
            // compass headings — rivers drew as straight axis/diagonal segments
            // with 45°/90° jogs (user-caught at native res). Each cell takes the
            // steepest of its 8 triangular facets → a REAL-VALUED flow angle, and
            // SPLITS its flow between the two neighbours bracketing that angle, so
            // accumulation (and every incised channel) follows smooth curves.
            // (The dominant-receiver array `dir[]` was dropped with the tree
            // level-solve that consumed it — nothing reads it now; removing it
            // saves a full n² int buffer at 2m, part of the CarveRivers OOM fix.)
            var fr1 = new int[n * n];       // facet cardinal-side receiver
            var fr2 = new int[n * n];       // facet diagonal-side receiver (-1 = pure cardinal)
            var fp1 = new float[n * n];     // fraction of flow to fr1
            var order = new int[n * n];
            for (int i2 = 0; i2 < n * n; i2++) { fr1[i2] = -1; fr2[i2] = -1; fp1[i2] = 1f; order[i2] = i2; }
            // Facet f (0..7): cardinal neighbour CARD[f], adjacent diagonal
            // DIAG[f], indexing RIV_DX/RIV_DZ (0E 1W 2N 3S 4NE 5SE 6NW 7SW).
            int[] CARD = { 0, 2, 2, 1, 1, 3, 3, 0 };
            int[] DIAG = { 4, 4, 6, 6, 7, 7, 5, 5 };
            const float QPI = 0.7853982f;                   // π/4 = facet angular width
            float invCellM = 1f / cellM;
            for (int z2 = 1; z2 < n - 1; z2++)
            {
                for (int x2 = 1; x2 < n - 1; x2++)
                {
                    int i2 = z2 * n + x2;
                    float zc = wr[i2];
                    float bestS = 0f; int bC = -1, bD = -1; float bR = 0f;
                    for (int f = 0; f < 8; f++)
                    {
                        int cj = (z2 + RIV_DZ[CARD[f]]) * n + (x2 + RIV_DX[CARD[f]]);
                        int dj = (z2 + RIV_DZ[DIAG[f]]) * n + (x2 + RIV_DX[DIAG[f]]);
                        float s1 = (zc - wr[cj]) * invCellM;
                        float s2 = (wr[cj] - wr[dj]) * invCellM;
                        if (s1 <= 0f && s2 <= 0f) continue;
                        float r = (float)Math.Atan2(s2, s1);
                        float s;
                        if (r < 0f) { r = 0f; s = s1; }
                        else if (r > QPI) { r = QPI; s = (zc - wr[dj]) * invCellM * 0.70710678f; }
                        else s = (float)Math.Sqrt(s1 * s1 + s2 * s2);
                        if (s > bestS) { bestS = s; bC = cj; bD = dj; bR = r; }
                    }
                    if (bC < 0) continue;
                    float pc = 1f - bR / QPI;               // fraction to the cardinal receiver
                    fr1[i2] = bC;
                    if (pc >= 0.99995f) { fp1[i2] = 1f; fr2[i2] = -1; }
                    else { fp1[i2] = pc; fr2[i2] = bD; }
                }
            }
            Array.Sort(wr, order);                          // ascending fill surface — sort IN PLACE (wr is dead after this; skips a full n² clone that OOM'd the 2m carve)
            wr = null; GC.Collect();                         // reclaim the corrugated fill before acc1 grows the heap — keeps the accumulation phase off the OOM ceiling
            var acc1 = new float[n * n];
            for (int i2 = 0; i2 < n * n; i2++) acc1[i2] = 1f;
            for (int k2 = n * n - 1; k2 >= 0; k2--)         // upstream → downstream
            {
                int i2 = order[k2];
                int c = fr1[i2];
                if (c < 0) continue;
                float a = acc1[i2], f1 = fp1[i2];
                acc1[c] += a * f1;
                int d = fr2[i2];
                if (d >= 0) acc1[d] += a * (1f - f1);
            }

            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: flow field done ({rsw.Elapsed.TotalSeconds:0}s); tracing channels...");
            // RIVER REWORK — the traced smooth polylines ARE the rivers now, so
            // the drainage no longer draws itself into the terrain by hand.
            var riverPolys = TraceRivers(h, acc1, fr1, fr2, w, n, cellM, (n - 1) * cellM * 0.5f);
            _riverPolys = riverPolys;   // the planner authors its connections from these
            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: {(riverPolys != null ? riverPolys.Count : 0)} polyline(s) traced ({rsw.Elapsed.TotalSeconds:0}s); solving valleys...");

            // MEMORY: free the flow-routing scratch NOW — the carve below
            // allocates its own large n² buffers (hOrig clone, widthOf, seedIdx,
            // rDist, paintLvl) and the 2m transient set otherwise peaks ~5.5GB
            // and threw OutOfMemory at the hOrig clone on the client (32GB box +
            // heavy modpack + LOH pressure). wr/fp1/order are dead after the acc
            // walk, fr1/fr2/acc1 after the trace; none is read again. The forced
            // collect reclaims the LOH before the carve grows the heap.
            wr = null; fp1 = null; fr1 = null; fr2 = null; order = null; acc1 = null;
            GC.Collect();

            // RIVER REWORK — the global stream-power incision is REMOVED. It cut
            // the raw D-inf accumulation field into every 0.25 km² channel,
            // painting angular drainage lines across floodplains and valley
            // flats. Rivers now come ONLY from the traced polylines (carved
            // below); the mountains keep their ridge relief + droplet erosion.
            // Nothing incises flat/floodplain terrain any more.

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

            // RIVER REWORK — the spine is the SMOOTH TRACED NETWORK, not a
            // per-cell accumulation threshold. Rasterize each traced polyline
            // into widthOf/w/riverCells (DDA at ½-cell steps so the spine is
            // unbroken): width = power law of the node catchment area (2m brook
            // … 14m trunk), level = the traced water table − 0.9m freeboard so
            // banks stand above, never perched above local ground, never below
            // the sea. The tuned Dijkstra valley + cross-section carve below
            // then draw the terrain around this smooth spine exactly as before.
            var riverCells = new List<int>(16384);
            // EXACT source cells (first rasterized cell of each polyline) —
            // the headwater-pool pass reads THIS, not a level heuristic. The
            // old "no higher neighbour" test misfired on the monotone-clamped
            // flat runs and carved a pool bowl at EVERY clamped cell — the
            // beaded pit trenches (user screenshots).
            var polySources = new HashSet<int>();
            const float AREA_ONSET = 60000f;    // m² — below = ephemeral, no channel
            const float WK = 0.039f, WP = 0.36f;
            float rhalf = (n - 1) * cellM * 0.5f;
            if (riverPolys != null)
            {
                foreach (var poly in riverPolys)
                {
                    int nn = poly.Length / 4;
                    bool sourceMarked = false;
                    // REVERTED to the original level rule (user mandate): the
                    // monotone running-min collapsed the level downstream of
                    // any deep dip and the uncapped carve then trenched whole
                    // mountainsides (locations dragged, mountain water dug to
                    // extremes). Rivers behave exactly as before; the gorge
                    // deposit-dam case gets its OWN scoped pass later.
                    for (int seg = 0; seg < nn - 1; seg++)
                    {
                        float aar = poly[seg * 4 + 2], alv = poly[seg * 4 + 3];
                        float bar = poly[(seg + 1) * 4 + 2], blv = poly[(seg + 1) * 4 + 3];
                        float acx = (poly[seg * 4] + rhalf) / cellM, acz = (poly[seg * 4 + 1] + rhalf) / cellM;
                        float bcx = (poly[(seg + 1) * 4] + rhalf) / cellM, bcz = (poly[(seg + 1) * 4 + 1] + rhalf) / cellM;
                        float ddx = bcx - acx, ddz = bcz - acz;
                        int steps = (int)Mathf.Max(1f, Mathf.Max(Mathf.Abs(ddx), Mathf.Abs(ddz)) * 2f);
                        for (int s2 = 0; s2 <= steps; s2++)
                        {
                            float t = (float)s2 / steps;
                            int cx = (int)Mathf.Round(acx + ddx * t);
                            int cz = (int)Mathf.Round(acz + ddz * t);
                            if (cx < 2 || cx >= n - 2 || cz < 2 || cz >= n - 2) continue;
                            int i = cz * n + cx;
                            float area = aar + (bar - aar) * t;
                            float width = Math.Min(14f, Math.Max(2f, WK * (float)Math.Pow(Math.Max(area, AREA_ONSET), WP)));
                            float lvl = alv + (blv - alv) * t - 0.9f;
                            if (lvl > hOrig[i] - 0.5f) lvl = hOrig[i] - 0.5f;
                            if (lvl < SEA) lvl = SEA;
                            bool fresh = widthOf[i] <= 0f;
                            if (fresh) riverCells.Add(i);
                            if (width > widthOf[i]) widthOf[i] = width;
                            if (fresh || lvl < w[i]) w[i] = lvl;
                            if (!sourceMarked) { polySources.Add(i); sourceMarked = true; }
                        }
                    }
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

            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: valley regions solved ({rsw.Elapsed.TotalSeconds:0}s); carving channels...");
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
            // irregular pool instead of a dead stop. Sources are EXACT (the
            // first rasterized cell of each polyline) — the old no-higher-
            // neighbour heuristic pooled every monotone-clamped flat cell.
            foreach (int i in polySources)
            {
                int cz2 = i / n, cx2 = i - cz2 * n;
                if (cx2 < 2 || cz2 < 2 || cx2 >= n - 2 || cz2 >= n - 2) continue;
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

            FiresLogger.LogInfo($"[FiresWorldGen]  │  rivers: channels carved ({rsw.Elapsed.TotalSeconds:0}s); finalizing water...");
            // RIVER REWORK — WATER IS THE CARVED RIVER NETWORK ONLY.
            // Sea snap first (the fill's +0.0002/cell tie-breaker accumulates to
            // +0.2..0.4m across the ocean flat). River cells (mask 1 channel,
            // mask 3 spine) KEEP their monotone level (bed = level − depth by the
            // cross-section, so depth is correct by construction). EVERY other
            // land cell is dried to its bed: the global fill otherwise ponds
            // every shallow basin AND every valley shoulder the Dijkstra carve
            // lowered below the pre-carve ground — a map-wide flood. Deliberate
            // lakes/ponds are a separate placement pass, not this fill.
            for (int i2 = 0; i2 < n * n; i2++)
            {
                if (w[i2] < SEA + 0.5f) w[i2] = SEA;
                else if (mask[i2] != 1 && mask[i2] != 3)
                {
                    if (w[i2] > h[i2]) w[i2] = h[i2];
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

        // ── VECTOR-SPINE river tracing (mirrored offline — keep identical) ──
        // Compute WHERE the rivers go as smooth polylines; the carve then draws
        // the terrain around them. From each major MOUTH (big catchment draining
        // to the sea) walk UPSTREAM along the real drainage (max inflow),
        // recording the cell path; branch a tributary wherever a second sizeable
        // inflow joins; Chaikin-smooth each path into a meandering curve. Each
        // node carries [x,z,area,level] — level = the water table at the traced
        // cell (before meander) so the carve seats the surface correctly.
        private static List<float[]> TraceRivers(float[] h, float[] acc1, int[] fr1, int[] fr2, float[] wlvl, int n, float cellM, float half)
        {
            var polys = new List<float[]>();
            float cellA = cellM * cellM;
            const float MOUTH_A = 1.2e7f;   // >=12 km² catchment = a MAIN river mouth
            const float HEAD_A = 4.0e5f;    // stop tracing below 0.4 km² (no tiny threads)
            const float TRIB_A = 1.8e6f;    // branch a tributary only at >=1.8 km² side inflow

            var mouths = new List<int>();
            for (int i = 0; i < n * n; i++)
            {
                if (h[i] <= SEA || acc1[i] * cellA < MOUTH_A) continue;
                int d1 = fr1[i], d2 = fr2[i];
                if ((d1 >= 0 && h[d1] <= SEA + 0.2f) || (d2 >= 0 && h[d2] <= SEA + 0.2f)) mouths.Add(i);
            }
            mouths.Sort((a, b) => acc1[b].CompareTo(acc1[a]));
            var mstart = new List<int>();
            int rad = (int)(250f / cellM);
            foreach (var mm in mouths)
            {
                int mx = mm % n, mz = mm / n; bool near = false;
                foreach (var t in mstart) if (Math.Abs(t % n - mx) < rad && Math.Abs(t / n - mz) < rad) { near = true; break; }
                if (!near) mstart.Add(mm);
            }

            var visited = new bool[n * n];
            var q = new Queue<int>();
            // tributary-start cell → the JUNCTION cell it branched from, so the
            // tributary's mouth can be curved into the main channel's flow (a
            // natural Y confluence) instead of meeting it at the grid-flow
            // angle — often a hard 90° (user: "rivers should not be meeting at
            // hard 90°s").
            var tribJunction = new System.Collections.Generic.Dictionary<int, int>();
            foreach (var mm in mstart) q.Enqueue(mm);
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                if (visited[cur]) continue;
                var raw = new List<float>();
                // CONFLUENCE MOUTH: seed a tributary's path with a point just
                // DOWNSTREAM on the main (toward the junction's receiver) then
                // the junction itself, so Chaikin curves the mouth to follow
                // the main's flow. Without this the tributary approaches the
                // trunk dead-straight at its raw grid angle.
                if (tribJunction.TryGetValue(cur, out int jcell))
                {
                    int jx = jcell % n, jz = jcell / n;
                    int recv = fr1[jcell];
                    if (recv >= 0)
                    {
                        int rx = recv % n, rz = recv / n;
                        raw.Add(jx + (rx - jx) * 0.5f); raw.Add(jz + (rz - jz) * 0.5f);
                        raw.Add(acc1[jcell] * cellA); raw.Add(wlvl[jcell]);
                    }
                    raw.Add(jx); raw.Add(jz); raw.Add(acc1[jcell] * cellA); raw.Add(wlvl[jcell]);
                }
                int guard = 0;
                while (cur >= 0 && !visited[cur] && guard++ < 40000)
                {
                    visited[cur] = true;
                    int cx = cur % n, cz = cur / n;
                    float area = acc1[cur] * cellA;
                    raw.Add(cx); raw.Add(cz); raw.Add(area); raw.Add(wlvl[cur]);
                    if (area < HEAD_A) break;
                    int best = -1; float bestA = 0f; int second = -1; float secA = 0f;
                    for (int e = 0; e < 8; e++)
                    {
                        int nx = cx + RIV_DX[e], nz = cz + RIV_DZ[e];
                        if (nx < 0 || nx >= n || nz < 0 || nz >= n) continue;
                        int j = nz * n + nx;
                        if (fr1[j] != cur && fr2[j] != cur) continue;
                        float aj = acc1[j];
                        if (aj > bestA) { second = best; secA = bestA; best = j; bestA = aj; }
                        else if (aj > secA) { second = j; secA = aj; }
                    }
                    if (best < 0) break;
                    if (second >= 0 && !visited[second] && secA * cellA >= TRIB_A)
                    {
                        q.Enqueue(second);
                        tribJunction[second] = cur;   // remember the trunk cell for the mouth curve
                    }
                    cur = best;
                }
                // 4 Chaikin passes (was 3): the extra corner-cut rounds the
                // grid-flow staircase kinks that still read as hard turns on
                // the 2m grid (user: "rivers should not be making hard 90°
                // turns"). Confluence mouths are curved via the tribJunction
                // seed above.
                if (raw.Count >= 12) polys.Add(ChaikinSmooth(raw, cellM, half, 4));
            }
            return polys;
        }

        // Chaikin corner-cutting: angular cell path → smooth curve, then a
        // two-scale perpendicular meander tapered to 0 at the mouth + headwater
        // so joins hold. raw = [cx,cz,area,lvl,...] cell coords; output =
        // [x,z,area,lvl,...] world metres (level carried through, never meandered).
        private static float[] ChaikinSmooth(List<float> raw, float cellM, float half, int iters)
        {
            int m = raw.Count / 4;
            var xs = new float[m]; var zs = new float[m]; var ar = new float[m]; var lv = new float[m];
            for (int i = 0; i < m; i++) { xs[i] = raw[i * 4]; zs[i] = raw[i * 4 + 1]; ar[i] = raw[i * 4 + 2]; lv[i] = raw[i * 4 + 3]; }
            for (int it = 0; it < iters && xs.Length > 2; it++)
            {
                int k = xs.Length;
                var nx = new float[(k - 1) * 2]; var nz = new float[(k - 1) * 2]; var na = new float[(k - 1) * 2]; var nl = new float[(k - 1) * 2];
                for (int i = 0; i < k - 1; i++)
                {
                    nx[i * 2] = 0.75f * xs[i] + 0.25f * xs[i + 1]; nz[i * 2] = 0.75f * zs[i] + 0.25f * zs[i + 1]; na[i * 2] = 0.75f * ar[i] + 0.25f * ar[i + 1]; nl[i * 2] = 0.75f * lv[i] + 0.25f * lv[i + 1];
                    nx[i * 2 + 1] = 0.25f * xs[i] + 0.75f * xs[i + 1]; nz[i * 2 + 1] = 0.25f * zs[i] + 0.75f * zs[i + 1]; na[i * 2 + 1] = 0.25f * ar[i] + 0.75f * ar[i + 1]; nl[i * 2 + 1] = 0.25f * lv[i] + 0.75f * lv[i + 1];
                }
                xs = nx; zs = nz; ar = na; lv = nl;
            }
            int kk = xs.Length;
            var outp = new float[kk * 4];
            float s = 0f;
            float phase = xs[0] * 0.011f + zs[0] * 0.017f;    // per-river phase
            for (int i = 0; i < kk; i++)
            {
                if (i > 0) s += Mathf.Sqrt((xs[i] - xs[i - 1]) * (xs[i] - xs[i - 1]) + (zs[i] - zs[i - 1]) * (zs[i] - zs[i - 1])) * cellM;
                int a = Math.Max(0, i - 1), b = Math.Min(kk - 1, i + 1);
                float tx = xs[b] - xs[a], tz = zs[b] - zs[a];
                float tl = Mathf.Sqrt(tx * tx + tz * tz); if (tl < 1e-4f) tl = 1f;
                float perpx = -tz / tl, perpz = tx / tl;                    // unit perpendicular
                float amp = Mathf.Min(130f, 18f + Mathf.Sqrt(ar[i]) / 45f);
                float off = amp * (0.68f * Mathf.Sin(s / 110f + phase) + 0.32f * Mathf.Sin(s / 260f + phase * 1.7f));
                off *= Mathf.Min(1f, Mathf.Min(i, kk - 1 - i) / 10f);
                outp[i * 4] = -half + xs[i] * cellM + perpx * off;
                outp[i * 4 + 1] = -half + zs[i] * cellM + perpz * off;
                outp[i * 4 + 2] = ar[i];
                outp[i * 4 + 3] = lv[i];
            }
            return outp;
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
            var w = _waterS;
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
                    float y = w[gz * n + gx] * UNPACK - RIVER_DROP;
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
                    FiresLogger.LogWarning($"[FiresWorldGen] biome image not found: {p} — using derived biomes.");
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
                FiresLogger.LogInfo($"[FiresWorldGen] biome image applied: {iw}x{ih}, {painted} cells painted from {p}");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] biome image failed ({ex.Message}) — using derived biomes.");
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
                    FiresLogger.LogWarning($"[FiresWorldGen] image base not found: {p} — falling back.");
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
                FiresLogger.LogInfo($"[FiresWorldGen] image base loaded: {iw}x{ih} → {res} grid, heights {minY}..{maxY} from {p}");
                return g;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] image base load failed ({ex.Message}) — falling back.");
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

        internal static float[] PriorityFloodFill(float[] h, int n, string label = null)
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
            int pops = 0, popStep = Math.Max(1, total / 4);
            var psw = label != null ? System.Diagnostics.Stopwatch.StartNew() : null;
            while (HPop(out int cur))
            {
                if (label != null && ++pops % popStep == 0 && pops < total)
                    FiresLogger.LogInfo($"[FiresWorldGen]  │   {label} {pops * 100L / total}% ({psw.Elapsed.TotalSeconds:0}s)");
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
                    FiresLogger.LogError($"[FiresWorldGen] VAHC base not found: {p} — falling back to the generator. Peers with the file will build a DIFFERENT world.");
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
                    FiresLogger.LogInfo($"[FiresWorldGen] VAHC base loaded: {size}→{n} grid, half={totalRadius:F0}m from {p}");
                    return g;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] VAHC base load failed ({ex.Message}) — falling back to the generator.");
                return null;
            }
        }

        // Droplet erosion — straight port of the proven offline pass
        // (Tools/FiresWorldGen/Program.cs). Grid units are metres; droplets die
        // when they reach the sea so coasts keep their shelves.
        // 0.2.723 (Fire 10-05: "the mod isn't released, so we fix bugs, existing worlds are test worlds"): `half` is the span
        // of THIS grid. It read HALF, the generated grid's span, so a VAHC base of another radius eroded at the wrong cell size.
        // Image bases and generated grids pass HALF and erode exactly as before; VAHC bases re-erode once (their cache name
        // changed to _vahc2, so an old VAHC cache is rebuilt rather than reused).
        private static void Erode(float[] h, int n, int count, int seed, float half)
        {
            // Resolution-INDEPENDENT erosion (the 2m "30 parallel rills" fix): a
            // droplet models a fixed PHYSICAL rain path, so its LIFE must scale
            // with cells-per-metre — else at 2m it dies after ~128m, far too
            // short to find and merge into channels, so it just scratches a local
            // track straight down the slope and thousands of them read as a comb.
            // Longer paths mean each droplet covers more ground, so COUNT scales
            // only LINEARLY with side length: total work stays ~constant across
            // resolutions and the droplets consolidate into a dendritic tree.
            float cellM = (2f * half) / (n - 1);
            float resK = 10.2591f / cellM;                 // 1 at 2048, ~5.1 at 2m
            int LIFE = (int)(64f * resK);                  // physical path length constant
            const int RADIUS = 2;
            count = (int)Math.Min(int.MaxValue, (long)count * (n - 1) / 2047L);
            if (count <= 0) return;
            var rng = new Random(seed);
            const float INERTIA = 0.08f, CAPACITY = 7.5f, MIN_SLOPE = 0.01f;
            const float ERODE = 0.42f, DEPOSIT = 0.3f, EVAP = 0.012f, GRAVITY = 4f;
            float MAX_CARVE = FiresGenTuning.ErosionDepthCap;   // hard cap: no cell incises >this below its pre-erosion surface

            // Pre-erosion surface snapshot at 8m for the depth cap. A full-res
            // clone is 440MB at 2m (OOM); 8m is ~27MB and a cap needs no more
            // precision. Nearest-sampled in the carve.
            int erC = Math.Max(1, (int)Math.Round(8f / cellM));
            int eN = (n + erC - 1) / erC;
            var h0 = new float[eN * eN];
            for (int zc = 0; zc < eN; zc++)
                for (int xc = 0; xc < eN; xc++)
                    h0[zc * eN + xc] = h[Math.Min(n - 1, zc * erC) * n + Math.Min(n - 1, xc * erC)];

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

            // DEPOSIT brush — wider and DOME-shaped (the carve tent kernel,
            // stacked repeatedly where a droplet stalls, literally built 4-8m
            // cones all over mountain flanks). A radius-4 dome lays mounded
            // fans instead of tents.
            const int DEP_RADIUS = 4;
            int depN = 0;
            var depDx = new int[92]; var depDy = new int[92]; var depW = new float[92];
            float depSum = 0f;
            for (int dy = -DEP_RADIUS; dy <= DEP_RADIUS; dy++)
                for (int dx = -DEP_RADIUS; dx <= DEP_RADIUS; dx++)
                {
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > DEP_RADIUS) continue;
                    float t = 1f - (d / DEP_RADIUS) * (d / DEP_RADIUS);
                    depDx[depN] = dx; depDy[depN] = dy; depW[depN] = t * t;
                    depSum += t * t;
                    depN++;
                }
            for (int i = 0; i < depN; i++) depW[i] /= depSum;

            int dropStep = Math.Max(1, count / 5);
            var esw = System.Diagnostics.Stopwatch.StartNew();
            for (int drop = 0; drop < count; drop++)
            {
                if (drop > 0 && drop % dropStep == 0)
                    FiresLogger.LogInfo($"[FiresWorldGen]  │   erosion {drop * 100L / count}% ({esw.Elapsed.TotalSeconds:0}s)");
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
                    // Symmetry-break: without this, thousands of droplets track
                    // the SAME gentle gradient straight down a uniform slope and
                    // carve dead-parallel rills (the "30 rivers" comb). A small
                    // seeded per-step jitter lets erosion feedback pull them into
                    // dendritic channels instead. Deterministic (mirrored offline).
                    dirX += (float)(rng.NextDouble() - 0.5) * 0.35f;
                    dirY += (float)(rng.NextDouble() - 0.5) * 0.35f;
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
                        // LANDSLIDE PHYSICS: sediment does NOT settle on a
                        // steep face — deposition fades out above ~16° and is
                        // zero past ~29°, so overloaded droplets carry their
                        // material to the slope break and mound it against
                        // the base of the hill (the real slide-apron look).
                        // Uphill pit-filling (dh>0) keeps its full amount.
                        if (dh <= 0f)
                        {
                            // Fade deposits only on genuinely STEEP faces
                            // (27°→43°). The first gate (16°→29°) starved
                            // moderate slopes of the sediment that blunts
                            // the ridged-noise teeth.
                            float grade = Mathf.Sqrt(gradX * gradX + gradY * gradY) / cellM;
                            amount *= 1f - Smooth(0.50f, 0.95f, grade);
                        }
                        sediment -= amount;
                        for (int b = 0; b < depN; b++)
                        {
                            int bx = xi + depDx[b], by = yi + depDy[b];
                            if (bx < 0 || bx >= n || by < 0 || by >= n) continue;
                            h[by * n + bx] += amount * depW[b];
                        }
                    }
                    else
                    {
                        // SLOPE-GATE the carve (user directive): droplets do NOT
                        // erode gentle floodplains — only real hillsides/mountains
                        // carve. Rivers in the flats are drawn by CarveRivers'
                        // stream-power incision, not by parallel droplet rills.
                        // grade = vertical drop per metre this step (dh<=0 here).
                        float slopeGate = Smooth(0.12f, 0.30f, -dh / cellM);
                        // STEEP BEDROCK RESISTS INCISION (user: landslide/
                        // erosion "only ADD not REMOVE" — stop the giant holes
                        // gouged into mountain faces). Deposition already
                        // mounds slide-aprons at slope bases; the CARVE had no
                        // upper limit, so on a steep face slopeGate saturated
                        // and droplets trenched deep crevices into the peak.
                        // Fade the carve out past ~31° (grade 0.60) to zero by
                        // ~48° (1.10): moderate hillsides still cut dendritic
                        // valleys, steep peaks shed only via talus deposition.
                        float gradeC = Mathf.Sqrt(gradX * gradX + gradY * gradY) / cellM;
                        float steepDamp = 1f - Smooth(0.60f, 1.10f, gradeC);
                        float amount = Mathf.Min((capacity - sediment) * ERODE, -dh) * slopeGate * steepDamp;
                        for (int b = 0; b < brushN; b++)
                        {
                            int bx = xi + bdx[b], by = yi + bdy[b];
                            if (bx < 0 || bx >= n || by < 0 || by >= n) continue;
                            int ci = by * n + bx;
                            h[ci] -= amount * bw[b];
                            // HARD DEPTH CAP (user: "cap the max depth it can
                            // carve into a surface"). No droplet run may incise
                            // a cell more than MAX_CARVE below its PRE-erosion
                            // surface — thousands of droplets converging can
                            // no longer gouge a bottomless crater; a real
                            // canyon just bottoms out at the cap. h0 is an 8m
                            // snapshot (a full-res clone is 440MB at 2m).
                            float floor0 = h0[(by / erC) * eN + (bx / erC)] - MAX_CARVE;
                            if (h[ci] < floor0) h[ci] = floor0;
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
        // INTERNAL: FiresClimate (and any future gen module) uses the SAME
        // noise family — one implementation, one determinism story.

        internal static float Hash01(int x, int y, int s)
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

        internal static float Fbm(float x, float y, float freq, int octaves, int s)
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

        internal static float Smooth(float a, float b, float x)
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
        // A studio-baked world's cache: keyed by world NAME only (seed- and res-agnostic),
        // so an imported / hand-built world ships as one file loaded by name.

        // ── PER-WORLD GENERATOR CHOICE ──────────────────────────────────────
        // Written once by the creation popup, read on every load. It sits beside
        // the world caches rather than in config because it is a property of the
        // WORLD, not of the installation: a dedicated server must reach the same
        // answer as the client that made it, and a config value cannot promise
        // that. A world created before this existed has no file and falls back to
        // the older signals (a baked cache, then the name prefix).
        private static string WorldChoicePath(string worldName)
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(worldName, ".mode");
        }

        public static void SetWorldGenChoice(string worldName, bool fires)
        {
            try { File.WriteAllText(WorldChoicePath(worldName), fires ? "fires" : "vanilla"); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresWorldGen] could not record the generator choice for '{worldName}': {ex.Message} — it will fall back to the name/bake rules."); }
        }

        // true = Fires, false = vanilla, null = never asked.
        public static bool? GetWorldGenChoice(string worldName)
        {
            try
            {
                string p = WorldChoicePath(worldName);
                if (!File.Exists(p)) return null;
                return File.ReadAllText(p).Trim().Equals("fires", StringComparison.OrdinalIgnoreCase);
            }
            catch { return null; }
        }

        // A world without a profile: the answer recorded at its creation wins; otherwise a baked cache under its exact name
        // (a deliberate opt-in by name, so the studio's bake → save → play path works) or the Fires name prefix, unless
        // WorldGenMode forces the generator on. WorldGenMode Off is the caller's to check first.
        internal static bool UnprofiledChoosesFires(string worldName, FiresWorldGenMode mode, out bool? chose, out bool nameHit, out bool bakedHit)
        {
            bool records = RecordsChooseFires(worldName, out chose, out nameHit, out bakedHit);
            if (chose.HasValue) return chose.Value;
            return mode != FiresWorldGenMode.Auto || records;
        }

        // The same question with the config's vote taken away: only what the WORLD says about itself. The recorded answer,
        // then a baked cache under its exact name, then the Fires name prefix - all three are properties of the world, not
        // of the machine it is opened on. WorldGenMode is deliberately absent: `mode != Auto` is a config forcing the
        // generator on, and an existing world's ground is not the config's to change.
        internal static bool RecordsChooseFires(string worldName, out bool? chose, out bool nameHit, out bool bakedHit)
        {
            chose = GetWorldGenChoice(worldName);
            nameHit = (worldName ?? "").StartsWith("Fires", StringComparison.OrdinalIgnoreCase);
            bakedHit = false;
            try { bakedHit = File.Exists(BakedCachePath(worldName)); } catch { }
            if (chose.HasValue) return chose.Value;
            return nameHit || bakedHit;
        }

        // A baked cache IS the terrain: a client without it generates different ground from the same seed, so the server
        // would be placing vegetation, spawns and locations on a world no client can see. The server's bake (and its .flow)
        // goes to every joining client through FAT's join sync BEFORE PeerInfo (BcJoinSync, D14), so the client's world is
        // built on it from its first join. Core's WorldCacheSync used to send it after the client's terrain was built.
        internal static string LoadedBakePath { get; private set; }

        internal static string BakedCachePath(string worldName)
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(worldName, "_baked.bin");
        }

        // True when `path` is a cache this build can load: FGEN magic and this FORMAT_VERSION, the checks TryLoad makes first.
        // A bake from an older World Gen Studio fails it (FiresGaeaStudio refuses to install one).
        internal static bool IsLoadableBake(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var br = new BinaryReader(gz))
                    return br.ReadUInt32() == MAGIC && br.ReadInt32() == FORMAT_VERSION;
            }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        // firesgen_<world>_<seed>_r<res>[_vahc].bin: the only derived, regenerable file in FiresWorldGen.
        private static readonly Regex SeedGridCacheName =
            new Regex(@"^firesgen_.+_-?\d+_r\d+(_vahc2?)?\.bin$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string CachePath(string worldName, int seed, int res, bool vahc)
        {
            // SHARED across installs, not per-install. This file is ~600MB and
            // costs ~6.7 MINUTES of droplet erosion to produce (dedi measured
            // 402184ms) — and the per-install path had the client and the dedi
            // on the SAME machine each generating their own byte-identical
            // copy, plus hoarding one per wiped seed forever (6 × ~600MB per
            // install found on disk). worlds_local is already shared between
            // both processes; the gen cache now lives beside it.
            string safe = string.Concat((worldName ?? "world").Split(Path.GetInvalidFileNameChars()));
            // 0.2.723: _vahc2 - a VAHC base now erodes at its own cell size, so an older _vahc cache is never reused.
            string file = $"firesgen_{safe}_{seed}_r{res}{(vahc ? "_vahc2" : "")}.bin";
            // One folder per world (FiresWorldRecords): the seed grid lives in FiresWorldGen\<world>\.
            string dir = VerdantsAscent.Terrain.Voxel.FiresWorldRecords.WorldFolder(worldName);
            string recordsRoot = VerdantsAscent.Terrain.Voxel.FiresWorldRecords.Folder;
            string shared = Path.Combine(dir, file);
            try
            {
                // A truncated corpse (process killed mid-save) must never feed
                // TryLoad garbage or block the migration below.
                if (File.Exists(shared) && new FileInfo(shared).Length < 1024)
                    File.Delete(shared);
                if (!File.Exists(shared))
                {
                    // Migrate this install's old per-install copy instead of
                    // regenerating for 6.7 minutes.
                    string legacy = Path.Combine(FiresCore.Storage.FiresConfigPaths.Cache, file);
                    if (File.Exists(legacy) && new FileInfo(legacy).Length >= 1024)
                        File.Move(legacy, shared);
                }
                // Prune wiped-world corpses: any OTHER seed-grid cache untouched
                // for 3+ days. A different world played recently survives. Only
                // the seed-grid shape is eligible: baked terrain and island
                // registries share the prefix and extension, but they are a
                // world's only copy of its terrain and islands.
                var seedGridDirs = new List<string> { recordsRoot };
                if (Directory.Exists(recordsRoot)) seedGridDirs.AddRange(Directory.GetDirectories(recordsRoot));
                foreach (var gridDir in seedGridDirs)
                foreach (var f in Directory.GetFiles(gridDir, "firesgen_*.bin"))
                {
                    string name = Path.GetFileName(f);
                    if (string.Equals(name, file, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!SeedGridCacheName.IsMatch(name)) continue;
                    try
                    {
                        var fi = new FileInfo(f);
                        if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalDays > 3)
                        {
                            fi.Delete();
                            FiresLogger.LogInfo($"[FiresWorldGen] pruned stale seed-grid cache {name}");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresWorldGen] shared-cache migrate/prune: {ex.Message}"); }
            return shared;
        }

        private static float[] TryLoad(string path, out byte[] biomes, out byte[] riverMask, out float[] waterLevel, out int nOut, out float halfOut, out FiresWaterPlan plan)
        {
            var file = ReadCacheFile(path);
            plan = file?.Plan;
            biomes = file?.Biomes;
            riverMask = file?.RiverMask;
            waterLevel = file?.WaterLevel;
            nOut = file?.N ?? 0;
            halfOut = file?.Half ?? HALF;
            if (file != null) LoadedFaithful = file.Faithful;
            return file?.Grid;
        }

        // One cache or bake file, read whole. It touches no generator state, so a worker can read it ahead of the world
        // (BakePreload) while the main thread keeps drawing; null when the file is missing, stale or unreadable.
        internal sealed class CacheFile
        {
            internal float[] Grid;
            internal byte[] Biomes;
            internal byte[] RiverMask;
            internal float[] WaterLevel;
            internal int N;
            internal float Half;
            internal FiresWaterPlan Plan;
            internal bool Faithful;
        }

        internal static CacheFile ReadCacheFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var br = new BinaryReader(gz))
                {
                    if (br.ReadUInt32() != MAGIC) return null;
                    int ver = br.ReadInt32();
                    if (ver != FORMAT_VERSION) return null;
                    int n = br.ReadInt32();
                    float half = br.ReadSingle();
                    if (n < 2 || n > 32768 || half < 1000f) return null;
                    var g = new float[n * n];
                    if (!ReadFloatsInChunks(gz, g)) return null;
                    var bm = new byte[n * n];
                    if (!ReadBytesFully(gz, bm)) return null;
                    var rm = new byte[n * n];
                    if (!ReadBytesFully(gz, rm)) return null;
                    var wl = new float[n * n];
                    if (!ReadFloatsInChunks(gz, wl)) return null;
                    FiresWaterPlan plan = br.ReadBoolean() ? FiresWaterPlan.Load(br) : null;
                    bool faithful = br.ReadBoolean();
                    return new CacheFile { Grid = g, Biomes = bm, RiverMask = rm, WaterLevel = wl, N = n, Half = half, Plan = plan, Faithful = faithful };
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] cache load failed ({ex.Message}) — regenerating.");
                return null;
            }
        }

        private static bool ReadBytesFully(Stream source, byte[] destination)
        {
            int off = 0;
            while (off < destination.Length)
            {
                int got = source.Read(destination, off, destination.Length - off);
                if (got <= 0) return false;
                off += got;
            }
            return true;
        }

        // A full-size byte[] mirror of a float[] is the single biggest allocation
        // this file used to make, and it exists only to change the type. At the
        // baked resolution the grid alone is ~440 MB, so the mirror doubled the
        // peak and asked for two blocks that large back to back. The copy now
        // goes through a fixed chunk, which keeps every allocation small and
        // leaves the destination array as the only large one.
        private const int CacheStreamChunkBytes = 1 << 20;

        private static bool ReadFloatsInChunks(Stream source, float[] destination)
        {
            var chunk = new byte[CacheStreamChunkBytes];
            long totalBytes = (long)destination.Length * 4;
            long done = 0;
            while (done < totalBytes)
            {
                int want = (int)Math.Min(chunk.Length, totalBytes - done);
                int filled = 0;
                while (filled < want)
                {
                    int got = source.Read(chunk, filled, want - filled);
                    if (got <= 0) return false;
                    filled += got;
                }
                Buffer.BlockCopy(chunk, 0, destination, (int)done, want);
                done += want;
            }
            return true;
        }

        // `source` null writes the same zero-fill the old byte[] mirror produced
        // when waterLevel was absent or the wrong length.
        private static void WriteFloatsInChunks(BinaryWriter writer, float[] source, int floatCount)
        {
            var chunk = new byte[CacheStreamChunkBytes];
            long totalBytes = (long)floatCount * 4;
            long done = 0;
            while (done < totalBytes)
            {
                int want = (int)Math.Min(chunk.Length, totalBytes - done);
                if (source != null) Buffer.BlockCopy(source, (int)done, chunk, 0, want);
                else Array.Clear(chunk, 0, want);
                writer.Write(chunk, 0, want);
                done += want;
            }
        }

        private static void Save(string path, float[] g, byte[] biomes, byte[] riverMask, float[] waterLevel, int n, float half, FiresWaterPlan plan)
        {
            var waterToWrite = (waterLevel != null && waterLevel.Length == g.Length) ? waterLevel : null;
            // Atomic: temp + swap. Both installs share this file (client and
            // dedi on one machine) and a crash mid-write must not corrupt it.
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
            using (var bw = new BinaryWriter(gz))
            {
                bw.Write(MAGIC);
                bw.Write(FORMAT_VERSION);
                bw.Write(n);
                bw.Write(half);
                WriteFloatsInChunks(bw, g, g.Length);
                bw.Write(biomes);
                bw.Write(riverMask ?? new byte[g.Length]);
                WriteFloatsInChunks(bw, waterToWrite, g.Length);
                bw.Write(plan != null);
                if (plan != null) plan.Save(bw);
                bw.Write(s_saveFaithful);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // Offline studio bake: write an IMPORTED / hand-built world (grid in metres,
        // biomes as 0-8 codes, water in metres) into the game cache format at an
        // arbitrary world size, so the game loads it instantly. Identical MAGIC /
        // version / layout to a generated cache.
        // ═══ AN IMPORTED WORLD GETS THE SAME WATER AS A GENERATED ONE ═══════════
        // A DEM import used to write wet cells straight into the cache and stop
        // there: no bodies, no banks, no weir shaping, no lakes — none of the
        // carve. The only thing an import genuinely owns that the generator does
        // not is the river ROUTE (Gaea solved its own hydrology, and its flow
        // field is better than anything we would re-derive). So the route comes
        // in as polylines and everything downstream is the ordinary pipeline:
        // plan the bodies, conform the terrain, incise the runs, bury the edges.
        // `polys` is stride 4 — (x, z, drainage area m², surface level) — exactly
        // what the generated path hands BuildPlan.
        public static void PlanImportedWorld(float[] gridMetres, byte[] biomes, List<float[]> polys,
                                             int n, float half, int seed,
                                             out float[] waterLevel, out byte[] riverMask)
        {
            _grid = gridMetres; _biomeMap = biomes; _res = n; _half = half; _seed = seed;
            // NO POLYLINES MEANS SOLVE THEM, NOT PLAN WITHOUT THEM. The planner
            // authors its connections FROM traced polylines; handed none it finds
            // bodies and joins nothing, and the world comes out as hundreds of
            // ponds with zero rivers. An import that carries its own river layers
            // (Gaea) passes them in. A height cache cannot: GetHeight already cut
            // the beds but nothing recorded where they ran. So the drainage is
            // solved here, through AnalyzeDrainage — the generator's OWN single
            // drainage path, so an import and a generated world still agree on
            // what a river is.
            if (polys == null || polys.Count == 0)
            {
                FiresLogger.LogInfo("[FiresWorldGen] import carries no river network — solving drainage from the terrain itself.");
                polys = AnalyzeDrainage(gridMetres, n, seed, out _);
                FiresLogger.LogInfo($"[FiresWorldGen] import drainage: {polys.Count:N0} polyline(s) traced; the planner authors its connections from these.");
            }
            _riverPolys = polys;
            _waterLevel = new float[gridMetres.Length];
            Array.Copy(gridMetres, _waterLevel, gridMetres.Length);
            _riverMask = new byte[gridMetres.Length];
            s_seedLevels.Clear();
            BuildZoneSurvey();
            BuildWaterPlan(seed, n, half);
            waterLevel = _waterLevel;
            riverMask = _riverMask;
        }

        // The plan an import just produced, so the caller can persist it with the
        // cache instead of throwing away everything the carve decided.
        internal static FiresWaterPlan LastPlan => _plan;
        // THE PLAN RIDES IN THE CACHE OR THE WORLD IS NOT THE ONE THAT WAS BAKED.
        // This passed `null` for the plan, and the cost was not subtle: on load the
        // game found no bodies at all ("water classes stamped from the plan: 0
        // body(ies)" on a bake that planned 1,421 of them), so every lake, pond and
        // marsh lost both its identity and its ONE constant level. What followed is
        // the whole seeder chain this design exists to replace — SeedMountainTarns
        // excavating fresh bowls into imported terrain, the lava field re-traced,
        // and the water surface reconciled from heights — which is exactly what
        // "a load never re-plans, and nothing downstream re-derives water from
        // heights" forbids. The audit then failed with 86,992 above-bank
        // violations and the sheets stood in slabs over a terraced hillside.
        internal static void SaveImportedCache(string path, float[] gridMetres, byte[] biomes, byte[] riverMask, float[] waterLevel, int n, float half, FiresWaterPlan plan = null, bool faithful = false)
        {
            s_saveFaithful = faithful;
            try { Save(path, gridMetres, biomes, riverMask ?? new byte[n * n], waterLevel, n, half, plan ?? _plan); }
            finally { s_saveFaithful = false; }
        }

        // Read a cache back — the other half of SaveImportedCache, so the studio
        // can LOAD a world it baked earlier instead of only ever writing one.
        // Returns null if the file is missing or a stale FORMAT_VERSION.
        public static float[] LoadImportedCache(string path, out byte[] biomes, out byte[] riverMask,
                                                out float[] waterLevel, out int n, out float half)
            => TryLoad(path, out biomes, out riverMask, out waterLevel, out n, out half, out _);
    }

    // A Normal-priority prefix of FAT's one WorldGenerator.Initialize hook (FatVanillaHooks), not a postfix, so the
    // grid exists before the WorldGenerator ctor pre-generates rivers/lakes against it.
    internal static class FiresWorldGenInit
    {
        internal static void Run(World world)
        {
            try { FiresWorldGenBase.OnWorldInitialize(world); }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[FiresWorldGen] init failed — falling back to vanilla generation: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(WorldGenerator), "GetBaseHeight")]
    internal static class Patch_WorldGenerator_GetBaseHeight_FiresGen
    {
        private static bool Prefix(float wx, float wy, bool menuTerrain, ref float __result,
            float ___m_offset0, float ___m_offset1, float ___m_minMountainDistance)
        {
            if (menuTerrain) return true;

            // A migrated BetterContinents world owns its heights: that is the terrain its bases were built on,
            // so it answers ahead of FAT's own procedural generator.
            if (BcImport.BcMigratedWorld.TryGetBaseHeight(wx, wy, ___m_offset0, ___m_offset1, ___m_minMountainDistance,
                    out float migrated))
            {
                __result = migrated;
                return false;
            }

            if (!FiresWorldGenBase.Active) return true;
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
            // A BetterContinents world with rivers turned off has none to carve.
            if (BcImport.BcMigratedWorld.RiversDisabled)
            {
                __result = h;
                return false;
            }
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
    // After Expand World Data's own GetBiome prefix, as BetterContinents orders its own (Patcher: after: [EWD.GUID]): every
    // prefix runs, so whichever runs last decides, and a painted BC biome map must win over EWD's world rules where it
    // paints (None leaves EWD's or vanilla's answer standing).
    [HarmonyAfter("expand_world_data")]
    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome),
        typeof(float), typeof(float), typeof(float), typeof(bool))]
    internal static class Patch_WorldGenerator_GetBiome_FiresGen
    {
        private static bool Prefix(WorldGenerator __instance, float wx, float wy, float oceanLevel, bool waterAlwaysOcean,
            ref Heightmap.Biome __result)
        {
            if (Zones.ZoneModeGate.TryGateBiome(__instance, wx, wy, oceanLevel, waterAlwaysOcean, ref __result)) return false;

            // A migrated BetterContinents biome map is authoritative where it paints; None means it does not.
            if (BcImport.BcMigratedWorld.TryGetBiome(wx, wy, out Heightmap.Biome painted))
            {
                __result = painted;
                return false;
            }

            if (!FiresWorldGenBase.Active) return true;
            if (waterAlwaysOcean && FiresWorldGenBase.SampleBaseNormalized(wx, wy) <= oceanLevel)
            {
                __result = Heightmap.Biome.Ocean;
                return false;
            }
            __result = FiresWorldGenBase.SampleBiomeReal(wx, wy);
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
        // __state = this sample's height is already final (zone mode's gate answered it, or the Fires generator owns the
        // ground), so the Postfix leaves it alone. The gate's own real-height samples are inner calls, corrected there.
        private static bool Prefix(WorldGenerator __instance, Heightmap.Biome biome, float wx, float wy,
            bool preGeneration, bool riverPreDN, ref Color mask, ref float __result, out bool __state)
        {
            __state = true;
            if (Zones.ZoneModeGate.TryGateBiomeHeight(__instance, biome, wx, wy, preGeneration, riverPreDN, ref mask, ref __result))
                return false;
            __state = FiresWorldGenBase.Active;
            if (!FiresWorldGenBase.Active) return true;
            // PER-BIOME, because vanilla is per-biome. GetBiomeHeight starts at mask = Color.black and only
            // three biomes overwrite it; every other biome ships with ALPHA 1. Measured on the game: the
            // mask starts black for every biome, then AshLands and Mistlands write (0,0,0,a) and DeepNorth
            // writes (0,g,0,0).
            //
            // A blanket Color.clear here was the old fix for a real bug - Color.black everywhere made the
            // Ashlands shader read alpha as full LAVA and rendered the whole crescent molten - but it
            // over-corrected onto every other biome. Heightmap.GetVegetationMask IS that alpha, and
            // ClutterSystem filters on it (m_minVegetation / m_maxVegetation), so alpha 0 world-wide
            // suppressed clutter that vanilla spawns. Ashlands keeps its lava alpha through
            // SampleLavaMask, so the original bug stays fixed by the branch rather than by the blanket.
            //
            // Mistlands deliberately keeps Color.clear: vanilla's alpha there is a terrain blend factor we
            // do not reproduce, and 0 (no blend) is the neutral value. Same as it shipped, stated so it
            // reads as a decision rather than an omission.
            mask = biome == Heightmap.Biome.DeepNorth ? DeepNorthSnowMask.Sample(__instance, wx, wy)
                 : biome == Heightmap.Biome.AshLands  ? FiresWorldGenBase.SampleLavaMaskReal(wx, wy)
                 : biome == Heightmap.Biome.Mistlands ? Color.clear
                 : Color.black;
            __result = FiresWorldGenBase.SampleFinalMetersReal(wx, wy);
            return false;
        }

        // A converted BetterContinents world gets BC's own biome height (roughmap / Override All) and paint, as BC's
        // postfixes give it (BcWorldGen.ApplyBiomeHeight); then a chunk world's start-cave hill rises from that ground.
        private static void Postfix(WorldGenerator __instance, Heightmap.Biome biome, float wx, float wy, ref Color mask, ref float __result, bool __state)
        {
            if (!__state) global::VerdantsAscent.Terrain.BcImport.BcMigratedWorld.Generator?.ApplyBiomeHeight(__instance, biome, wx, wy, ref mask, ref __result);
            Zones.ZoneModeStartCave.AddHill(wx, wy, ref __result);
        }
    }
}
