using System;
using System.Threading;
using System.Threading.Tasks;

namespace VerdantsAscent.Terrain.Voxel
{
    // To keep a dedicated server under ~5 GB (resident world fields were 1,058 MB on a large world): the whole-world fields
    // packed once world init is done. Always on (a pure storage change: every value a reader gets is the same):
    //   biome (0..8) + river mask (0..3) share one byte                      -105 MB at 10501^2
    //   water (int16) is TILED: a 64x64 tile that equals the ground everywhere (dry land: w = min(fill, terrain)) stores
    //     nothing, a tile of one value (the sea) stores that value, anything else keeps its 8 KB tile
    //   surface (int16) the same, with SURF_CUT (dry) as the usual one value
    // PackRuntimeFields runs at the END of init and VERIFIES every cell of every packed field against the dense array
    // before freeing it; any mismatch keeps the dense array for that field (and logs it). Readers go through the *F
    // properties below, which serve the packed form when it exists and the dense array before that (generation, restores).
    public static partial class FiresWorldGenBase
    {
        // A read-only int16 grid: dense (wrapping the generator's array) or tiled.
        internal sealed class I16Field
        {
            private const int Sh = 6, T = 1 << Sh, M = T - 1;
            internal readonly short[] Dense;        // non-null = a dense wrapper (no packing)
            private readonly int _n, _tn;
            private readonly byte[] _kind;          // 0 = ground's value, 1 = one value, 2 = full tile
            private readonly short[] _one;
            private readonly short[][] _tile;
            private readonly short[] _ground;

            private I16Field(short[] dense) { Dense = dense; }
            private I16Field(int n, int tn, byte[] kind, short[] one, short[][] tile, short[] ground)
            { _n = n; _tn = tn; _kind = kind; _one = one; _tile = tile; _ground = ground; }

            internal static I16Field Wrap(short[] dense) => dense == null ? null : new I16Field(dense);

            public short this[int i]
            {
                get
                {
                    var d = Dense;
                    if (d != null) return d[i];
                    int gz = i / _n, gx = i - gz * _n;
                    int t = (gz >> Sh) * _tn + (gx >> Sh);
                    switch (_kind[t])
                    {
                        case 0: return _ground[i];
                        case 1: return _one[t];
                        default: return _tile[t][((gz & M) << Sh) | (gx & M)];
                    }
                }
            }

            internal long Bytes
            {
                get
                {
                    if (Dense != null) return (long)Dense.Length * 2;
                    long b = _kind.Length + (long)_one.Length * 2 + (long)_tile.Length * IntPtr.Size;
                    foreach (var t in _tile) if (t != null) b += (long)t.Length * 2;
                    return b;
                }
            }

            // ground: the grid a dry tile reads (water), or null (surface). Returns null when packing would not pay.
            internal static I16Field Build(short[] d, int n, short[] ground, out int full, out int same, out int uniform)
            {
                full = same = uniform = 0;
                if (d == null || n <= 0 || d.Length != n * n || (ground != null && ground.Length != d.Length)) return null;
                int tn = (n + T - 1) >> Sh;
                var kind = new byte[tn * tn]; var one = new short[tn * tn]; var tile = new short[tn * tn][];
                int f = 0, s = 0, u = 0;
                Parallel.For(0, tn, tz =>
                {
                    int lf = 0, ls = 0, lu = 0;
                    for (int tx = 0; tx < tn; tx++)
                    {
                        int t = tz * tn + tx, z0 = tz << Sh, x0 = tx << Sh, z1 = Math.Min(n, z0 + T), x1 = Math.Min(n, x0 + T);
                        bool isGround = ground != null, isOne = true; short v0 = d[z0 * n + x0];
                        for (int z = z0; z < z1 && (isGround || isOne); z++)
                            for (int x = x0; x < x1; x++)
                            {
                                short v = d[z * n + x];
                                if (isGround && v != ground[z * n + x]) isGround = false;
                                if (isOne && v != v0) isOne = false;
                                if (!isGround && !isOne) break;
                            }
                        if (isGround) { kind[t] = 0; ls++; }
                        else if (isOne) { kind[t] = 1; one[t] = v0; lu++; }
                        else
                        {
                            var a = new short[T * T];
                            for (int z = z0; z < z1; z++) for (int x = x0; x < x1; x++) a[((z - z0) << Sh) | (x - x0)] = d[z * n + x];
                            kind[t] = 2; tile[t] = a; lf++;
                        }
                    }
                    Interlocked.Add(ref f, lf); Interlocked.Add(ref s, ls); Interlocked.Add(ref u, lu);
                });
                full = f; same = s; uniform = u;
                return new I16Field(n, tn, kind, one, tile, ground);
            }

            // every cell equal to the dense source?
            internal long CountMismatches(short[] d)
            {
                long bad = 0;
                int n = _n;
                Parallel.For(0, n, z =>
                {
                    long lb = 0; int row = z * n;
                    for (int x = 0; x < n; x++) if (this[row + x] != d[row + x]) lb++;
                    if (lb != 0) Interlocked.Add(ref bad, lb);
                });
                return bad;
            }
        }

        // A read-only byte grid view: (a[i] >> shift) & mask.
        internal sealed class ByteField
        {
            private readonly byte[] _a; private readonly int _sh, _mask;
            internal readonly byte[] Source;
            internal ByteField(byte[] a, int shift, int mask) { _a = a; _sh = shift; _mask = mask; Source = a; }
            public byte this[int i] => (byte)((_a[i] >> _sh) & _mask);
            internal int Length => _a.Length;
        }

        private static I16Field _waterSp, _surfSp, _waterWrap, _surfWrap;
        private static ByteField _bioPacked, _rivPacked, _bioWrap, _rivWrap;

        // For before/after comparisons: launch with -fat-pack-ab and the dense copies are KEPT
        // beside the packed ones for this session (about +0.5 GB, nothing saved); `fat_field_pack off|on` flips every reader
        // between them live and `fat_field_pack compare` runs every runtime sampler both ways over the whole world.
        private static bool s_packOff;
        private static I16Field _waterKeep, _surfKeep;
        private static ByteField _bioKeep, _rivKeep;
        private static bool KeepDenseForAb => Array.IndexOf(Environment.GetCommandLineArgs(), "-fat-pack-ab") >= 0;

        // Readers' entry points. Packed when packed (unless the A/B has the pack off); otherwise a cached wrapper over the dense array.
        internal static I16Field WaterF => (s_packOff && _waterKeep != null) ? _waterKeep : _waterSp ?? ((_waterWrap?.Dense == _waterS && _waterWrap != null) ? _waterWrap : (_waterWrap = I16Field.Wrap(_waterS)));
        internal static I16Field SurfF => (s_packOff && _surfKeep != null) ? _surfKeep : _surfSp ?? ((_surfWrap?.Dense == _surfS && _surfWrap != null) ? _surfWrap : (_surfWrap = I16Field.Wrap(_surfS)));
        internal static ByteField BiomeF => (s_packOff && _bioKeep != null) ? _bioKeep : _bioPacked ?? (_biomeMap == null ? null : (_bioWrap?.Source == _biomeMap ? _bioWrap : (_bioWrap = new ByteField(_biomeMap, 0, 0xFF))));
        internal static ByteField RiverF => (s_packOff && _rivKeep != null) ? _rivKeep : _rivPacked ?? (_riverMask == null ? null : (_rivWrap?.Source == _riverMask ? _rivWrap : (_rivWrap = new ByteField(_riverMask, 0, 0xFF))));

        private static void ClearPackedFields()
        {
            _waterSp = _surfSp = _waterWrap = _surfWrap = null;
            _bioPacked = _rivPacked = _bioWrap = _rivWrap = null;
            _waterKeep = _surfKeep = null; _bioKeep = _rivKeep = null; s_packOff = false;
        }

        // `fat_field_pack off|on|compare` (admin): the 948 pack A/B. Needs a -fat-pack-ab launch.
        internal static void RegisterPackAbCommand()
        {
            new Terminal.ConsoleCommand("fat_field_pack",
                "off | on | compare - A/B the whole-world field pack (needs a -fat-pack-ab launch; session only). After off/on run fat_rewrap_area <x> <z> <r> so the terrain there rebuilds.",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "compare";
                    string line;
                    bool kept = _waterKeep != null || _surfKeep != null || _bioKeep != null || _rivKeep != null;
                    if (!kept) line = "[FiresWorldGen] fat_field_pack: no dense copies kept this session (launch with -fat-pack-ab), or the world is not packed";
                    else if (a == "off" || a == "on") { s_packOff = a == "off"; line = $"[FiresWorldGen] fat_field_pack: readers now use the {(s_packOff ? "DENSE copies (pack off)" : "PACKED fields (pack on)")}; rebuild the area with fat_rewrap_area before a shot"; }
                    else line = CompareAb();
                    FiresLogger.LogInfo(line); args.Context?.AddString(line);
                }, isCheat: true);
        }

        // Every runtime sampler, both ways, on a 32 m grid over the whole world. PASS = 0 differences over > 0 points.
        private static string CompareAb()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool was = s_packOff;
            float half = _half, step = 32f;
            int side = Math.Max(1, (int)(2f * half / step));
            long points = 0, diffs = 0; string first = null;
            const int Q = 8;
            var a = new float[Q]; var b = new float[Q];
            void Sample(float x, float z, float[] o)
            {
                o[0] = (float)SampleBiomeReal(x, z);
                o[1] = BiomeCodeAtReal(x, z);
                o[2] = IsChannelAtReal(x, z) ? 1f : 0f;
                o[3] = TryGetGenWater(x, z, out float l1, out bool w1) ? l1 + (w1 ? 100000f : 0f) : -1f;
                o[4] = TryGetRawWaterCell(x, z, out float l2, out float d2) ? l2 + d2 * 7f : -1f;
                o[5] = TryGetWaterSurfaceGen(x, z, out float l3, out float d3) ? l3 + d3 * 7f : -1f;
                o[6] = TryGetGenWaterSmoothReal(x, z, out float l4, out float d4) ? l4 + d4 * 7f : -1f;
                o[7] = SampleFinalMetersUncached(x, z);
            }
            try
            {
                for (int iz = 0; iz < side; iz++)
                    for (int ix = 0; ix < side; ix++)
                    {
                        float x = -half + (ix + 0.5f) * step, z = -half + (iz + 0.5f) * step;
                        s_packOff = true; Sample(x, z, a);
                        s_packOff = false; Sample(x, z, b);
                        points++;
                        for (int q = 0; q < Q; q++)
                            if (!(a[q] == b[q] || (float.IsNaN(a[q]) && float.IsNaN(b[q]))))
                            { diffs++; if (first == null) first = $"query {q} at ({x:0},{z:0}): dense {a[q]} vs packed {b[q]}"; }
                    }
            }
            finally { s_packOff = was; }
            bool pass = points > 0 && diffs == 0;
            return $"[FiresWorldGen] fat_field_pack compare {(pass ? "PASS" : "FAIL")}: {points:N0} points x {Q} queries (biome, biome code, channel, gen water, raw water, " +
                $"surface, smooth water, height) on a {step:0} m grid over the whole world, {diffs:N0} differ{(first != null ? "; first: " + first : "")} ({sw.ElapsedMilliseconds} ms)";
        }

        // End of world init: pack, verify every cell, then free the dense arrays.
        private static void PackRuntimeFields()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const double MB = 1024d * 1024d;
            int n = _res;
            var parts = new System.Collections.Generic.List<string>();
            long before = 0, after = 0;
            // Collect first, so the packed tiles reuse the freed float fields' space instead of growing Mono's heap
            // (the heap grew 2,863 -> 3,000 MB in the pack otherwise).
            GC.Collect();

            // biome + river -> one byte, as two NIBBLES (river code 4 exists on 15,152 cells of
            // a large world, which a 6+2-bit split refused; biome codes are 0..8).
            var bm = _biomeMap; var rm = _riverMask;
            if (bm != null && rm != null && bm.Length == rm.Length)
            {
                int badRange = 0;
                var p = new byte[bm.Length];
                Parallel.For(0, n, z =>
                {
                    int row = z * n, lb = 0;
                    for (int x = 0; x < n; x++)
                    {
                        int i = row + x; byte b = bm[i], r = rm[i];
                        if (b > 0x0F || r > 0x0F) { lb++; continue; }
                        p[i] = (byte)(b | (r << 4));
                    }
                    if (lb != 0) Interlocked.Add(ref badRange, lb);
                });
                var bio = new ByteField(p, 0, 0x0F); var riv = new ByteField(p, 4, 0x0F);
                long bad = badRange;
                if (bad == 0)
                    Parallel.For(0, n, z => { int row = z * n; long lb = 0; for (int x = 0; x < n; x++) { int i = row + x; if (bio[i] != bm[i] || riv[i] != rm[i]) lb++; } if (lb != 0) Interlocked.Add(ref bad, lb); });
                before += bm.Length * 2L;
                if (bad == 0) { _bioPacked = bio; _rivPacked = riv; _biomeMap = null; _riverMask = null; after += p.Length; parts.Add($"biome+river {bm.Length * 2L / MB:N0} -> {p.Length / MB:N0} MB"); }
                else { after += bm.Length * 2L; parts.Add($"biome+river NOT packed ({bad:N0} cell(s) out of range or mismatched; kept dense)"); FiresLogger.LogWarning($"[FiresWorldGen] field pack: biome+river kept dense, {bad:N0} cell(s) would not round-trip"); }
            }

            // water (dry tiles read the ground) and surface (SURF_CUT tiles are one value)
            var waterDense = _waterS; var surfDense = _surfS;
            PackI16Field(ref _waterS, ref _waterSp, n, _gridS, "water", parts, ref before, ref after);
            if (_surfS != null && _surfN > 0) PackI16Field(ref _surfS, ref _surfSp, _surfN, null, "surface", parts, ref before, ref after);

            // -fat-pack-ab keeps the dense copies for this session's A/B (fat_field_pack)
            if (KeepDenseForAb)
            {
                if (_bioPacked != null) { _bioKeep = new ByteField(bm, 0, 0xFF); _rivKeep = new ByteField(rm, 0, 0xFF); }
                if (_waterSp != null) _waterKeep = I16Field.Wrap(waterDense);
                if (_surfSp != null) _surfKeep = I16Field.Wrap(surfDense);
                parts.Add("-fat-pack-ab: dense copies KEPT this session for fat_field_pack (not freed)");
            }

            FiresLogger.LogInfo($"[FiresWorldGen] field pack (every cell verified identical before the dense copy was freed): {before / MB:N0} -> {after / MB:N0} MB " +
                $"in {sw.ElapsedMilliseconds} ms ({string.Join("; ", parts.ToArray())}).");
        }

        private static void PackI16Field(ref short[] dense, ref I16Field packed, int n, short[] ground, string name,
            System.Collections.Generic.List<string> parts, ref long before, ref long after)
        {
            const double MB = 1024d * 1024d;
            var d = dense;
            if (d == null) return;
            var f = I16Field.Build(d, n, ground, out int full, out int same, out int uniform);
            long denseBytes = d.Length * 2L;
            before += denseBytes;
            if (f == null) { after += denseBytes; parts.Add($"{name} not packed (shape)"); return; }
            long bad = f.CountMismatches(d);
            if (bad != 0 || f.Bytes >= denseBytes)
            {
                after += denseBytes;
                parts.Add($"{name} kept dense ({(bad != 0 ? bad.ToString("N0") + " mismatched cell(s)" : "packing would not save memory")})");
                if (bad != 0) FiresLogger.LogWarning($"[FiresWorldGen] field pack: {name} kept dense, {bad:N0} cell(s) did not round-trip");
                return;
            }
            packed = f; dense = null; after += f.Bytes;
            parts.Add($"{name} {denseBytes / MB:N0} -> {f.Bytes / MB:N0} MB ({full:N0} full / {uniform:N0} one-value / {same:N0} same-as-ground tiles)");
        }
    }
}
