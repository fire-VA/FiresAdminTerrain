using System;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // W5 — CLIMATE FIELDS. Temperature, wind-swept moisture (orographic rain
    // + rain shadow), HAND (height above nearest drainage) and valley
    // enclosure, computed ONCE per generation at 8m stride from the FINAL
    // relief + hydrology. BuildBiomes resolves biomes from these instead of
    // radius rings + noise pockets: Plains sit in real rain shadows behind
    // ranges, swamps in real floodplains between river branches and coasts,
    // Mistlands in real walled valleys, and the snowline rides latitude.
    // Moisture and enclosure are CDF-normalized over land cells, so their
    // biome gates are AREA QUOTAS — every seed lands a sane mix while the
    // spatial structure (lee sides driest, valley floors most enclosed)
    // stays physical. Pure functions of (seed, grid): deterministic,
    // dedi-safe, discarded after the biome build except for report means.
    internal static class FiresClimate
    {
        internal static int N;              // climate grid side (8m cells)
        internal static float Cell;         // metres per climate cell
        internal static float HalfW;
        internal static float[] T;          // °C
        internal static float[] M;          // 0..1 land-percentile moisture
        internal static float[] Hand;       // metres above nearest drainage
        internal static float[] Enc;        // 0..1 land-percentile enclosure

        private const float SEA = 30f;      // matches FiresWorldGenBase.SEA

        internal static void Clear()
        { T = null; M = null; Hand = null; Enc = null; N = 0; }

        internal static float SampleT(float wx, float wz) => Bil(T, wx, wz, 10f);
        internal static float SampleM(float wx, float wz) => Bil(M, wx, wz, 0.5f);
        internal static float SampleHand(float wx, float wz) => Bil(Hand, wx, wz, 99f);
        internal static float SampleEnc(float wx, float wz) => Bil(Enc, wx, wz, 0f);

        private static float Bil(float[] a, float wx, float wz, float fallback)
        {
            if (a == null || N == 0) return fallback;
            // fields are built at CELL CENTERS ((i+0.5)·Cell − half); the
            // −0.5 keeps sampling aligned (no 4m systematic shift)
            float gx = (wx + HalfW) / Cell - 0.5f, gz = (wz + HalfW) / Cell - 0.5f;
            if (gx < 0f) gx = 0f; else if (gx > N - 1.001f) gx = N - 1.001f;
            if (gz < 0f) gz = 0f; else if (gz > N - 1.001f) gz = N - 1.001f;
            int xi = (int)gx, zi = (int)gz;
            float fx = gx - xi, fz = gz - zi;
            int i = zi * N + xi;
            return a[i] * (1f - fx) * (1f - fz) + a[i + 1] * fx * (1f - fz)
                 + a[i + N] * (1f - fx) * fz + a[i + N + 1] * fx * fz;
        }

        // Noise/hash/smoothstep come from FiresWorldGenBase — ONE noise
        // family for the whole generator, one determinism story.

        // ── chamfer DTs (two-pass, metres) ───────────────────────────────
        private static void Chamfer(float[] d, int nc)
        {
            float o = Cell, g2 = Cell * 1.41421356f;
            for (int z = 0; z < nc; z++)
                for (int x = 0; x < nc; x++)
                {
                    int i = z * nc + x; float v = d[i];
                    if (x > 0) v = Mathf.Min(v, d[i - 1] + o);
                    if (z > 0)
                    {
                        v = Mathf.Min(v, d[i - nc] + o);
                        if (x > 0) v = Mathf.Min(v, d[i - nc - 1] + g2);
                        if (x + 1 < nc) v = Mathf.Min(v, d[i - nc + 1] + g2);
                    }
                    d[i] = v;
                }
            for (int z = nc - 1; z >= 0; z--)
                for (int x = nc - 1; x >= 0; x--)
                {
                    int i = z * nc + x; float v = d[i];
                    if (x + 1 < nc) v = Mathf.Min(v, d[i + 1] + o);
                    if (z + 1 < nc)
                    {
                        v = Mathf.Min(v, d[i + nc] + o);
                        if (x + 1 < nc) v = Mathf.Min(v, d[i + nc + 1] + g2);
                        if (x > 0) v = Mathf.Min(v, d[i + nc - 1] + g2);
                    }
                    d[i] = v;
                }
        }

        private static void ChamferWithPayload(float[] d, float[] pay, int nc)
        {
            float o = Cell, g2 = Cell * 1.41421356f;
            for (int z = 0; z < nc; z++)
                for (int x = 0; x < nc; x++)
                {
                    int i = z * nc + x; float v = d[i]; float p = pay[i];
                    void Take(int j, float c) { if (d[j] + c < v) { v = d[j] + c; p = pay[j]; } }
                    if (x > 0) Take(i - 1, o);
                    if (z > 0)
                    {
                        Take(i - nc, o);
                        if (x > 0) Take(i - nc - 1, g2);
                        if (x + 1 < nc) Take(i - nc + 1, g2);
                    }
                    d[i] = v; pay[i] = p;
                }
            for (int z = nc - 1; z >= 0; z--)
                for (int x = nc - 1; x >= 0; x--)
                {
                    int i = z * nc + x; float v = d[i]; float p = pay[i];
                    void Take(int j, float c) { if (d[j] + c < v) { v = d[j] + c; p = pay[j]; } }
                    if (x + 1 < nc) Take(i + 1, o);
                    if (z + 1 < nc)
                    {
                        Take(i + nc, o);
                        if (x + 1 < nc) Take(i + nc + 1, g2);
                        if (x > 0) Take(i + nc - 1, g2);
                    }
                    d[i] = v; pay[i] = p;
                }
        }

        private static void BoxBlur(float[] src, float[] tmp, int nc, int r, int iterations)
        {
            for (int it = 0; it < iterations; it++)
            {
                for (int z = 0; z < nc; z++)
                {
                    int row = z * nc;
                    float sum = 0f; int cnt = 0;
                    for (int x = 0; x <= r && x < nc; x++) { sum += src[row + x]; cnt++; }
                    for (int x = 0; x < nc; x++)
                    {
                        tmp[row + x] = sum / cnt;
                        int add = x + r + 1, sub = x - r;
                        if (add < nc) { sum += src[row + add]; cnt++; }
                        if (sub >= 0) { sum -= src[row + sub]; cnt--; }
                    }
                }
                for (int x = 0; x < nc; x++)
                {
                    float sum = 0f; int cnt = 0;
                    for (int z = 0; z <= r && z < nc; z++) { sum += tmp[z * nc + x]; cnt++; }
                    for (int z = 0; z < nc; z++)
                    {
                        src[z * nc + x] = sum / cnt;
                        int add = z + r + 1, sub = z - r;
                        if (add < nc) { sum += tmp[add * nc + x]; cnt++; }
                        if (sub >= 0) { sum -= tmp[sub * nc + x]; cnt--; }
                    }
                }
            }
        }

        // What the distribution ACTUALLY looks like over land. The biome gates
        // are percentile thresholds, so "Plains M<0.38" only means "the driest
        // 38% of land" if the values really do spread — and a world that reports
        // 0.0% Plains AND 0.0% Meadows is telling you they do not. Printing the
        // quantiles turns that from a guess into a measurement.
        private static void ReportSpread(string label, float[] a, bool[] land, int count)
        {
            int landN = 0;
            for (int i = 0; i < count; i++) if (land[i]) landN++;
            if (landN == 0) { FiresLogger.LogInfo($"[FiresWorldGen] climate {label}: NO LAND CELLS"); return; }
            var vals = new float[landN];
            int k = 0;
            for (int i = 0; i < count; i++) if (land[i]) vals[k++] = a[i];
            Array.Sort(vals);
            float Q(float q) => vals[Mathf.Clamp((int)(q * (landN - 1)), 0, landN - 1)];
            int atTop = 0;
            for (int i = 0; i < landN; i++) if (vals[i] >= 0.999f) atTop++;
            FiresLogger.LogInfo($"[FiresWorldGen] climate {label} over {landN:N0} land cells: " +
                      $"min {Q(0f):0.000} p10 {Q(0.10f):0.000} p25 {Q(0.25f):0.000} med {Q(0.50f):0.000} " +
                      $"p75 {Q(0.75f):0.000} p90 {Q(0.90f):0.000} max {Q(1f):0.000} | " +
                      $"{atTop * 100f / landN:0.0}% pinned at 1.0");
        }

        // CDF-normalize over land cells: values become land-area percentiles.
        private static void CdfNormalize(float[] a, bool[] land, int count)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < count; i++)
                if (land[i]) { if (a[i] < lo) lo = a[i]; if (a[i] > hi) hi = a[i]; }
            if (hi - lo < 1e-6f) return;
            const int BINS = 2048;
            var histo = new int[BINS];
            int landN = 0;
            float scale = (BINS - 1) / (hi - lo);
            for (int i = 0; i < count; i++)
                if (land[i]) { histo[(int)((a[i] - lo) * scale)]++; landN++; }
            var cdf = new float[BINS];
            int run = 0;
            for (int b = 0; b < BINS; b++) { run += histo[b]; cdf[b] = (float)run / landN; }
            for (int i = 0; i < count; i++)
                a[i] = cdf[Mathf.Clamp((int)((a[i] - lo) * scale), 0, BINS - 1)];
        }

        internal static void Build(float[] h, float[] w, byte[] m, int n, float half, int seed)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float cellM = (2f * half) / (n - 1);
            int stride = Mathf.Max(1, Mathf.RoundToInt(8f / cellM));
            int nc = (n + stride - 1) / stride;
            N = nc; Cell = cellM * stride; HalfW = half;
            int cn = nc * nc;

            // ── decimate: mean height + drainage/ocean flags ─────────────
            var hc = new float[cn];
            var drain = new bool[cn];
            var ocean = new bool[cn];
            var land = new bool[cn];
            var drainH = new float[cn];
            for (int zc = 0; zc < nc; zc++)
            {
                int z0 = zc * stride, z1 = Math.Min(n - 1, z0 + stride - 1);
                for (int xc = 0; xc < nc; xc++)
                {
                    int x0 = xc * stride, x1 = Math.Min(n - 1, x0 + stride - 1);
                    float sum = 0f; int cnt = 0;
                    bool dr = false, oc = false; float dh = float.MaxValue;
                    for (int zz = z0; zz <= z1; zz += 2)
                        for (int xx = x0; xx <= x1; xx += 2)
                        {
                            int i = zz * n + xx;
                            float hv = h[i];
                            sum += hv; cnt++;
                            if (hv < SEA - 0.5f) { oc = true; dr = true; if (SEA < dh) dh = SEA; }
                            // REAL water only: the raw fill field films whole
                            // valleys with sub-half-metre spill that the walk
                            // later dries — counting it as drainage flooded
                            // the moisture recharge over entire valleys and
                            // collapsed the Whittaker mid-band (log: Meadows
                            // 0.3%, Plains 0.0%). Rivers (mask) and standing
                            // water with ≥0.6m of depth are the truth.
                            else if (m[i] != 0 || w[i] > hv + 0.6f)
                            {
                                dr = true;
                                float lv = Mathf.Max(hv, Mathf.Min(w[i], hv + 6f));
                                if (lv < dh) dh = lv;
                            }
                        }
                    int ic = zc * nc + xc;
                    hc[ic] = sum / Math.Max(1, cnt);
                    drain[ic] = dr; ocean[ic] = oc;
                    land[ic] = !oc;
                    drainH[ic] = dr ? (dh == float.MaxValue ? hc[ic] : dh) : 0f;
                }
            }

            // ── HAND: nearest-drainage elevation via payload chamfer ─────
            Hand = new float[cn];
            var dist = new float[cn];
            var pay = new float[cn];
            const float INF = 1e9f;
            for (int i = 0; i < cn; i++)
            {
                if (drain[i]) { dist[i] = 0f; pay[i] = drainH[i]; }
                else { dist[i] = INF; pay[i] = 0f; }
            }
            ChamferWithPayload(dist, pay, nc);
            for (int i = 0; i < cn; i++)
                Hand[i] = drain[i] ? 0f : Mathf.Max(0f, hc[i] - pay[i]);

            // ── ocean distance (maritime moderation) ─────────────────────
            var dOce = new float[cn];
            for (int i = 0; i < cn; i++) dOce[i] = ocean[i] ? 0f : INF;
            Chamfer(dOce, nc);

            // ── temperature: latitude + lapse + maritime + weather noise ─
            // North (−z) is the cold pole (DeepNorth crescent); south (+z)
            // warms toward the Ashlands arc. Γ = 0.26 °C/m puts the mid-map
            // snowline (T < −1°C) at ≈80m — the budget the ranges were tuned
            // to — while the north snowline drops toward the lowlands and
            // the southern ranges stay green higher up.
            T = new float[cn];
            // 0.2.722: latitude runs to the world's own radius (FatWorldSize; 10000 on a vanilla-size world), so the bands keep
            // their place relative to the edge on an Expand World Size world.
            float latR = FatWorldSize.Radius;
            for (int zc = 0; zc < nc; zc++)
                for (int xc = 0; xc < nc; xc++)
                {
                    int i = zc * nc + xc;
                    float wz2 = -half + (zc + 0.5f) * Cell;
                    float wx2 = -half + (xc + 0.5f) * Cell;
                    float latN = Mathf.Clamp01(-wz2 / latR);
                    float latS = Mathf.Clamp01(wz2 / latR);
                    T[i] = 14f
                         - 26f * FiresWorldGenBase.Smooth(0.25f, 1f, latN)
                         + 9f * FiresWorldGenBase.Smooth(0.45f, 1f, latS)
                         // GENTLE LAPSE (sandbox uses 0.09). At 0.26/m temperature
                         // tracked altitude so hard that every ridge read polar,
                         // which is what let the cold gates claim the dry high
                         // ground and starve Plains/Meadows. Mountain is an
                         // elevation gate now, so temperature no longer has to
                         // carry the snowline on its own.
                         - 0.09f * Mathf.Max(0f, hc[i] - SEA)
                         + 2.5f * Mathf.Exp(-dOce[i] / 1000f)
                         + (FiresWorldGenBase.Fbm(wx2, wz2, 1f / 2500f, 2, seed ^ 0x71C1) - 0.5f) * 5f;
                }

            // ── moisture: prevailing-wind sweep with orographic rain ─────
            // One seed-fixed wind. Parcels advect cell to cell along it:
            // climbing terrain rains out (windward forests), so the parcel
            // crosses each ridge EMPTY — the lee side is the rain shadow
            // where Plains resolve. Ocean recharges to 1, lakes/rivers top
            // up a little, flats decay slowly.
            M = new float[cn];
            // ~1.4km upwind baseline (the sandbox samples the surface 1400m
            // upwind); in climate cells that is 1400/Cell.
            int UPWIND_CELLS = Mathf.Max(1, Mathf.RoundToInt(1400f / Cell));
            int wdir = (int)(FiresWorldGenBase.Hash01(seed, 0x57D, 0x11) * 8f) & 7;
            int dxw = (wdir == 7 || wdir == 0 || wdir == 1) ? 1 : (wdir >= 3 && wdir <= 5) ? -1 : 0;
            int dzw = (wdir >= 1 && wdir <= 3) ? 1 : (wdir >= 5 && wdir <= 7) ? -1 : 0;
            int zA = dzw >= 0 ? 0 : nc - 1, zB = dzw >= 0 ? nc : -1, zS = dzw >= 0 ? 1 : -1;
            int xA = dxw >= 0 ? 0 : nc - 1, xB = dxw >= 0 ? nc : -1, xS = dxw >= 0 ? 1 : -1;
            for (int zc = zA; zc != zB; zc += zS)
                for (int xc = xA; xc != xB; xc += xS)
                {
                    int i = zc * nc + xc;
                    // BOUNDED AND CENTRED, not a compounding decay (ported from
                    // the sandbox's FiresBiome3D moisture).
                    //
                    // The old sweep advected a parcel cell-to-cell and took a
                    // MULTIPLICATIVE loss on every upslope:
                    //     lift = max(0, h - hUpwind) / Cell        // Cell = 8m!
                    //     p    = mup * (0.010 + 0.5*min(1, lift/0.30))
                    // With an 8m cell a 1m rise reads as lift 0.125, i.e. a ~22%
                    // loss PER CELL, so a parcel crossing hundreds of cells
                    // inland decayed to nothing. Measured on seed -2062588818:
                    // raw moisture min/p10/p25/median over 4,082,119 land cells
                    // were all 0.000 — a rain shadow with no rain anywhere. The
                    // CDF then mapped that near-constant field to a step at
                    // 0.728, so NOTHING was ever drier than 0.73 and the Plains
                    // (<0.38) and Meadows (0.38-0.56) bands could not fire:
                    // both came out 0.0% of the world.
                    //
                    // Orography is still here — it is the `lift` term — but as a
                    // small ADDITIVE metre-scale contribution over a long upwind
                    // baseline, which is how a rain shadow actually reads: wetter
                    // climbing the windward side, drier in the lee, without the
                    // field collapsing to zero.
                    int ux = xc - dxw * UPWIND_CELLS, uz = zc - dzw * UPWIND_CELLS;
                    bool inB = ux >= 0 && ux < nc && uz >= 0 && uz < nc;
                    float hup = inB ? hc[uz * nc + ux] : SEA;
                    float lift = hc[i] - hup;                      // metres gained from upwind
                    float wx3 = (xc + 0.5f) * Cell - half, wz3 = (zc + 0.5f) * Cell - half;
                    float mnoise = FiresWorldGenBase.Fbm(wx3, wz3, 1f / 2200f, 2, seed ^ 0x140);
                    float coast = Mathf.Exp(-dOce[i] / 2500f);     // 1 at the shore, decaying inland
                    float mv = 0.44f + 0.34f * (mnoise * 2f - 1f) + 0.24f * coast + 0.0015f * lift;
                    if (ocean[i]) mv = 1f;
                    else if (drain[i]) mv += 0.12f;                // rivers/standing water damp their surroundings
                    M[i] = Mathf.Clamp01(mv);
                }
            var tmp = new float[cn];
            BoxBlur(M, tmp, nc, 12, 2);            // ~100m weather diffusion
            ReportSpread("moisture RAW", M, land, cn);
            CdfNormalize(M, land, cn);             // land-area percentiles
            ReportSpread("moisture CDF", M, land, cn);

            // ── enclosure: how far below the surrounding walls a cell sits
            var big = new float[cn];
            Array.Copy(hc, big, cn);
            BoxBlur(big, tmp, nc, 56, 1);          // ~450m neighbourhood
            Enc = new float[cn];
            for (int i = 0; i < cn; i++) Enc[i] = Mathf.Max(0f, big[i] - hc[i]);
            CdfNormalize(Enc, land, cn);

            FiresLogger.LogInfo($"[FiresWorldGen] climate: {nc}x{nc} @ {Cell:0.#}m — wind {new[] { "E", "SE", "S", "SW", "W", "NW", "N", "NE" }[wdir]}, " +
                      $"T/M/HAND/enclosure built in {sw.Elapsed.TotalSeconds:0.0}s (rain shadow + floodplains + walled valleys drive biomes).");
        }
    }
}
