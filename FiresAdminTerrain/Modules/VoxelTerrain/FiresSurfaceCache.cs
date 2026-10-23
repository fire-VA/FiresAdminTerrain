using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace VerdantsAscent.Terrain.Voxel
{
    // RAM: cutting the LIVE world fields by 316 MB did not shrink Mono's RESERVED heap, which
    // stayed at ~7.6 GB, because BuildReconciledSurface still has ~8 full-resolution float/bool temporaries alive at once and Mono keeps
    // the heap at that peak for the session. The reconcile's results are cached next to the world's other records, keyed by a hash of
    // EXACTLY what it reads (ground, water, river mask, biomes, the water classes stamped before it, the cascade spans) plus
    // SurfaceCacheVersion, so any upstream change (a new bake, world edits, a gen tweak) misses the cache and rebuilds. A hit reads the
    // results straight into their final arrays: no full-res temporaries, so every load after the first never reaches that peak.
    // Shared by the client and the dedi on one machine (tmp file + move; a writer that loses the race just drops its tmp).
    public static partial class FiresWorldGenBase
    {
        // Raise whenever BuildReconciledSurface (or StampBodyClasses / the shore transform) changes what it produces.
        private const int SurfaceCacheVersion = 1;
        private const uint SurfaceCacheMagic = 0x46535246;   // "FSRF"
        private const string SurfaceCachePrefix = "_surface_";

        // A client loading the downloaded world cache still rebuilt this reconcile (~6 GB of private
        // memory): the server's finished surface cache goes to joining clients through the join sync too (BcJoinSync, item
        // Surface). Its key is a hash of the inputs, which the client now has identical to the server's, so the server's file is
        // a valid hit. Server: the path of this world's surface cache once it exists. Client: a synced file used when its key matches.
        internal static volatile string LoadedSurfacePath;

        private static void NoteSurfaceForClients(string path)
        {
            if (ZNet.instance != null && !ZNet.instance.IsServer()) return;
            if (File.Exists(path)) LoadedSurfacePath = path;
        }

        // Client: this world's cache path, or else the server's surface cache from the join sync when its header carries this key.
        private static string SurfacePathFor(string localPath, ulong key, int n)
        {
            if (File.Exists(localPath)) return localPath;
            if (ZNet.instance == null || ZNet.instance.IsServer()) return localPath;
            if (BcImport.BcJoinSync.SurfaceAnswer(out string synced) != BcImport.BcJoinSync.ServerAnswer.Data || !File.Exists(synced)) return localPath;
            if (!SurfaceHeaderMatches(synced, key, n))
            {
                FiresLogger.LogWarning($"[FiresWorldGen] the server's water surface cache from the join sync is for other inputs; this game builds its own.");
                return localPath;
            }
            FiresLogger.LogInfo($"[FiresWorldGen] water surface: using the SERVER'S cache from the join sync (inputs {key:x16}; no local reconcile).");
            return synced;
        }

        private static bool SurfaceHeaderMatches(string path, ulong key, int n)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(gz))
                    return r.ReadUInt32() == SurfaceCacheMagic && r.ReadInt32() == SurfaceCacheVersion && r.ReadUInt64() == key && r.ReadInt32() == n;
            }
            catch (Exception) { return false; }
        }

        private static void BuildOrLoadReconciledSurface(float[] g, float[] w, int n, string worldName)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ulong key = SurfaceInputHash(g, w, n);
            string path = SurfacePathFor(FiresWorldRecords.PathFor(worldName, SurfaceCachePrefix + key.ToString("x16") + ".bin"), key, n);
            if (TryLoadSurface(path, key, n))
            {
                NoteSurfaceForClients(path);
                FiresMinimapRivers.OnWaterFieldReady();
                FiresLogger.LogInfo($"[FiresWorldGen] water surface loaded from its cache in {sw.Elapsed.TotalSeconds:0.0}s (inputs {key:x16}; the full-res reconcile and its temporaries skipped)");
                return;
            }
            BuildReconciledSurface(g, w, n);
            SaveSurfaceAsync(path, key, n, worldName);
        }

        // The cache-hit path split in two so the float fields can go between the key and the load (OnWorldInitialize).
        // The key is the same SurfaceInputHash the old path took (its inputs do not change across the int16 pack).
        private static bool s_surfaceRetry;

        private static bool SurfaceCacheHeaderMatches(float[] g, float[] w, int n, string worldName, out ulong key, out string path)
        {
            key = SurfaceInputHash(g, w, n);
            path = SurfacePathFor(FiresWorldRecords.PathFor(worldName, SurfaceCachePrefix + key.ToString("x16") + ".bin"), key, n);
            if (!File.Exists(path)) return false;
            try
            {
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(gz))
                    return r.ReadUInt32() == SurfaceCacheMagic && r.ReadInt32() == SurfaceCacheVersion && r.ReadUInt64() == key && r.ReadInt32() == n;
            }
            catch (Exception) { return false; }
        }

        private static bool LoadSurfaceAfterFree(string path, ulong key, int n)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!TryLoadSurface(path, key, n)) return false;
            NoteSurfaceForClients(path);
            FiresMinimapRivers.OnWaterFieldReady();
            FiresLogger.LogInfo($"[FiresWorldGen] water surface loaded from its cache in {sw.Elapsed.TotalSeconds:0.0}s (inputs {key:x16}; the full-res reconcile and its temporaries skipped; 0.2.953: loaded after the float fields were freed)");
            return true;
        }

        // FNV-1a over every input value, rows hashed in parallel then folded in order (deterministic).
        private static ulong SurfaceInputHash(float[] g, float[] w, int n)
        {
            var rows = new ulong[n];
            var rm = _riverMask; var bm = _biomeMap; var cls = _classS;
            Parallel.For(0, n, y =>
            {
                ulong h = 14695981039346656037UL;
                int row = y * n;
                for (int x = 0; x < n; x++)
                {
                    int i = row + x;
                    h = Mix(h, Bits(g[i]));
                    h = Mix(h, Bits(w[i]));
                    if (rm != null && i < rm.Length) h = Mix(h, rm[i]);
                    if (bm != null && i < bm.Length) h = Mix(h, bm[i]);
                    if (cls != null && i < cls.Length) h = Mix(h, cls[i]);
                }
                rows[y] = h;
            });
            ulong all = 14695981039346656037UL;
            all = Mix(all, (uint)SurfaceCacheVersion);
            all = Mix(all, (uint)n);
            all = Mix(all, Bits(_half));
            all = Mix(all, (uint)(cls != null ? cls.Length : 0));
            for (int y = 0; y < n; y++) { all = Mix(all, (uint)rows[y]); all = Mix(all, (uint)(rows[y] >> 32)); }
            foreach (var span in FiresWorldGenBase.CascadeSpans)
                foreach (float f in span) all = Mix(all, Bits(f));
            return all;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public uint U; }

        private static uint Bits(float f) => new FloatBits { F = f }.U;

        private static ulong Mix(ulong h, uint v)
        {
            unchecked
            {
                h = (h ^ (v & 0xFF)) * 1099511628211UL;
                h = (h ^ ((v >> 8) & 0xFF)) * 1099511628211UL;
                h = (h ^ ((v >> 16) & 0xFF)) * 1099511628211UL;
                return (h ^ (v >> 24)) * 1099511628211UL;
            }
        }

        private static bool TryLoadSurface(string path, ulong key, int n)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(gz))
                {
                    if (r.ReadUInt32() != SurfaceCacheMagic || r.ReadInt32() != SurfaceCacheVersion || r.ReadUInt64() != key || r.ReadInt32() != n) return false;
                    int surfN = r.ReadInt32(); float surfCell = r.ReadSingle();
                    int classN = r.ReadInt32(); float classCell = r.ReadSingle();
                    int zoneN = r.ReadInt32(), zoneHalfN = r.ReadInt32();
                    var reps = new int[7];
                    for (int i = 0; i < reps.Length; i++) reps[i] = r.ReadInt32();
                    var surf = ReadShorts(r);
                    var cls = ReadBytes(r);
                    var shoreD = ReadBytes(r);
                    var zone = ReadBytes(r);
                    if (surf == null || cls == null) return false;
                    _surfS = surf; _surfN = surfN; _surfCell = surfCell;
                    _classS = cls; _classN = classN; _classCell = classCell;
                    _shoreD = shoreD; _shoreL = null; _shoreC = null;
                    _zoneWater = zone; _zoneN = zoneN; _zoneHalfN = zoneHalfN;
                    s_repClsOcean = reps[0]; s_repClsRiver = reps[1]; s_repClsLake = reps[2]; s_repClsPond = reps[3];
                    s_repClsSwamp = reps[4]; s_repClsFrozen = reps[5]; s_repClsHot = reps[6];
                    return true;
                }
            }
            catch (Exception e)
            {
                FiresLogger.LogDebug($"[FiresWorldGen] water surface cache unreadable ({e.GetType().Name}: {e.Message}); rebuilding");
                return false;
            }
        }

        private static void SaveSurfaceAsync(string path, ulong key, int n, string worldName)
        {
            // the results are not changed after the build; capture them so a world unload mid-write can't swap them out
            var surf = _surfS; var cls = _classS; var shoreD = _shoreD; var zone = _zoneWater;
            int surfN = _surfN, classN = _classN, zoneN = _zoneN, zoneHalfN = _zoneHalfN;
            float surfCell = _surfCell, classCell = _classCell;
            var reps = new[] { s_repClsOcean, s_repClsRiver, s_repClsLake, s_repClsPond, s_repClsSwamp, s_repClsFrozen, s_repClsHot };
            if (surf == null || cls == null) return;
            Task.Run(() =>
            {
                string tmp = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var fs = File.Create(tmp))
                    using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                    using (var wtr = new BinaryWriter(gz))
                    {
                        wtr.Write(SurfaceCacheMagic); wtr.Write(SurfaceCacheVersion); wtr.Write(key); wtr.Write(n);
                        wtr.Write(surfN); wtr.Write(surfCell); wtr.Write(classN); wtr.Write(classCell); wtr.Write(zoneN); wtr.Write(zoneHalfN);
                        foreach (int v in reps) wtr.Write(v);
                        WriteShorts(wtr, surf); WriteBytes(wtr, cls); WriteBytes(wtr, shoreD); WriteBytes(wtr, zone);
                    }
                    if (File.Exists(path)) { File.Delete(tmp); LoadedSurfacePath = path; return; }   // the other process on this machine got there first
                    File.Move(tmp, path);
                    LoadedSurfacePath = path;   // Joining clients get it through the join sync
                    // older caches of this world (other inputs) are dead weight
                    string dir = Path.GetDirectoryName(path), mine = Path.GetFileName(path);
                    string stem = Path.GetFileName(FiresWorldRecords.PathFor(worldName, SurfaceCachePrefix));
                    foreach (var old in Directory.GetFiles(dir, stem + "*.bin"))
                        if (!string.Equals(Path.GetFileName(old), mine, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
                    FiresLogger.LogDebug($"[FiresWorldGen] water surface cached: {mine} ({new FileInfo(path).Length >> 20} MB)");
                }
                catch (Exception e)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    FiresLogger.LogDebug($"[FiresWorldGen] water surface cache not written ({e.GetType().Name}: {e.Message})");
                }
            });
        }

        private const int CacheChunk = 1 << 20;

        private static void WriteBytes(BinaryWriter w, byte[] a)
        {
            w.Write(a != null ? a.Length : -1);
            if (a != null) w.Write(a);
        }

        private static byte[] ReadBytes(BinaryReader r)
        {
            int len = r.ReadInt32();
            if (len < 0) return null;
            var a = new byte[len];
            int got = 0;
            while (got < len) { int k = r.Read(a, got, Math.Min(CacheChunk, len - got)); if (k <= 0) throw new EndOfStreamException(); got += k; }
            return a;
        }

        private static void WriteShorts(BinaryWriter w, short[] a)
        {
            w.Write(a.Length);
            var buf = new byte[CacheChunk * 2];
            for (int i = 0; i < a.Length; i += CacheChunk)
            {
                int k = Math.Min(CacheChunk, a.Length - i);
                Buffer.BlockCopy(a, i * 2, buf, 0, k * 2);
                w.Write(buf, 0, k * 2);
            }
        }

        private static short[] ReadShorts(BinaryReader r)
        {
            int len = r.ReadInt32();
            if (len < 0) return null;
            var a = new short[len];
            var buf = new byte[CacheChunk * 2];
            for (int i = 0; i < len; i += CacheChunk)
            {
                int k = Math.Min(CacheChunk, len - i), need = k * 2, got = 0;
                while (got < need) { int m = r.Read(buf, got, need - got); if (m <= 0) throw new EndOfStreamException(); got += m; }
                Buffer.BlockCopy(buf, 0, a, i * 2, need);
            }
            return a;
        }
    }
}
