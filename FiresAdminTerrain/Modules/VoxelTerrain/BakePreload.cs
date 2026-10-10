using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace VerdantsAscent.Terrain.Voxel
{
    // A studio bake read on a worker while the client still waits at the loading screen ([perf] 2026-09-28: R25's join froze
    // the client 7.8 s, 6.1 s of it the Fires generator reading and unpacking the 377 MB bake inside RPC_PeerInfo). The join
    // sync starts it the moment it knows the bake (cached, or downloaded and verified), and the server holds PeerInfo until
    // the client reports it done, so the frames keep drawing; the generator then adopts the arrays instead of reading the
    // file. The read is FiresWorldGenBase.ReadCacheFile, the same one the main thread would run, and touches no generator
    // state. Main thread only, apart from the read itself.
    internal static class BakePreload
    {
        private const long NotAdopted = -1;

        private static string s_path;
        private static Task<FiresWorldGenBase.CacheFile> s_read;
        private static Stopwatch s_clock;
        private static long s_readMs;

        internal static bool Running => s_read != null && !s_read.IsCompleted;

        // How long the worker's read took, for the join's line, once this join's world adopted it; NotAdopted otherwise.
        internal static long AdoptedReadMs { get; private set; } = NotAdopted;

        internal static void Start(string path)
        {
            if (string.IsNullOrEmpty(path) || (path == s_path && s_read != null)) return;
            s_path = path;
            s_clock = Stopwatch.StartNew();
            s_readMs = 0;
            AdoptedReadMs = NotAdopted;
            var clock = s_clock;
            s_read = Task.Run(() =>
            {
                var file = FiresWorldGenBase.ReadCacheFile(path);
                s_readMs = clock.ElapsedMilliseconds;
                return file;
            });
            FiresLogger.LogInfo($"[FiresWorldGen] reading this world's terrain cache on a worker while the loading screen waits: {path}");
        }

        // The preloaded file for `path`, once: null when none was started for it or the read failed (the caller reads it
        // itself). A read still running is waited for, and says so.
        internal static FiresWorldGenBase.CacheFile TakeFor(string path)
        {
            var read = s_read;
            if (read == null || path != s_path) return null;
            long waitStart = s_clock.ElapsedMilliseconds;
            FiresWorldGenBase.CacheFile file = null;
            try { file = read.Result; }
            catch (AggregateException ex) { FiresLogger.LogWarning($"[FiresWorldGen] the worker's read failed ({ex.GetBaseException().Message}); reading it here."); }
            long waited = s_clock.ElapsedMilliseconds - waitStart;
            long readMs = s_readMs;
            Forget();
            if (file == null) return null;
            AdoptedReadMs = readMs;
            FiresLogger.LogInfo($"[FiresWorldGen] adopted the terrain cache a worker read in {readMs} ms"
                + (waited > 0 ? $" (the world waited {waited} ms more for it)." : ", ready before the world asked for it."));
            return file;
        }

        internal static void Reset()
        {
            Forget();
            AdoptedReadMs = NotAdopted;
        }

        private static void Forget()
        {
            s_path = null;
            s_read = null;
            s_clock = null;
        }
    }
}
