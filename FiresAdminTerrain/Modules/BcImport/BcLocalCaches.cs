using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;

namespace VerdantsAscent.Terrain.BcImport
{
    /// <summary>
    /// A world this PC already has must not download again: this game's OWN world caches - the terrain
    /// grids it generated or loaded (FiresWorldGen\&lt;world&gt;\firesgen_&lt;world&gt;_&lt;seed&gt;_r&lt;res&gt;.bin) and their water
    /// surfaces (firesgen_&lt;world&gt;_surface_&lt;key&gt;.bin) and studio bakes (firesgen_&lt;world&gt;_baked.bin) - are offered to the server in the join sync's held list, under the
    /// same content id the server gives its copy (SHA-256, first 32 hex chars, as BcJoinSync). Generation is deterministic, so a
    /// server whose cache is byte-identical answers "cached" and the client reads its own file where it lies (BcJoinSync.OnCached):
    /// no download, no bcsync write. Reused from BcJoinSync's hashing (one worker hash per file, kept per path, size and write
    /// time); the ids are kept in FiresWorldGen\local_cache_ids.txt so a file is hashed once, not at every start. The hashing runs on
    /// one background worker from the main menu, so a join never waits for it; a file not hashed yet simply downloads as before.
    /// </summary>
    internal static class BcLocalCaches
    {
        private const string IndexName = "local_cache_ids.txt";
        private const int IdHexChars = 32;
        private const long MinBytes = 1024 * 1024;
        // a generated grid (firesgen_<world>_<seed>_r<res>.bin) or a studio bake (firesgen_<world>_baked.bin): the two caches a server shares
        private static readonly Regex TerrainGrid = new Regex(@"^firesgen_.+(_-?\d+_r\d+(_vahc2)?|_baked)\.bin$", RegexOptions.IgnoreCase);
        private static readonly Regex Surface = new Regex(@"^firesgen_.+_surface_[0-9a-f]+\.bin$", RegexOptions.IgnoreCase);

        private sealed class Entry
        {
            public string Path;
            public long Length;
            public long Ticks;
        }

        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, Entry> s_byId = new Dictionary<string, Entry>();
        private static Task s_worker;

        private static string Root => Voxel.FiresWorldRecords.Folder;
        private static string IndexPath => Path.Combine(Root, IndexName);

        /// <summary>The ids of this game's own world caches that are hashed and still unchanged on disk.</summary>
        internal static IEnumerable<string> KnownIds()
        {
            lock (s_lock) return s_byId.Where(p => Unchanged(p.Value)).Select(p => p.Key).ToList();
        }

        /// <summary>This game's own world cache with that content id, if it is still the file that was hashed.</summary>
        internal static bool TryGet(string id, out string path)
        {
            path = null;
            lock (s_lock)
            {
                if (id == null || !s_byId.TryGetValue(id, out var e) || !Unchanged(e)) return false;
                path = e.Path;
                return true;
            }
        }

        private static bool Unchanged(Entry e)
        {
            try
            {
                var info = new FileInfo(e.Path);
                return info.Exists && info.Length == e.Length && info.LastWriteTimeUtc.Ticks == e.Ticks;
            }
            catch { return false; }
        }

        /// <summary>Client, from the main menu: read the kept ids, then hash whatever is new on one worker.</summary>
        internal static void Start()
        {
            if (s_worker != null) return;
            string root = Root;
            s_worker = Task.Run(() => Index(root));
        }

        private static void Index(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                var kept = Load(root);
                var files = Directory.GetFiles(root, "firesgen_*.bin", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bcsync" + Path.DirectorySeparatorChar))
                    .Where(f => { string n = Path.GetFileName(f); return TerrainGrid.IsMatch(n) || Surface.IsMatch(n); })
                    .ToList();
                int hashed = 0, reused = 0;
                foreach (string file in files)
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || info.Length < MinBytes) continue;
                    var entry = new Entry { Path = info.FullName, Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks };
                    string id = kept.FirstOrDefault(p => p.Value.Path == entry.Path && p.Value.Length == entry.Length && p.Value.Ticks == entry.Ticks).Key;
                    if (id != null) reused++;
                    else
                    {
                        using (var sha = SHA256.Create())
                        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                            id = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Substring(0, IdHexChars).ToLowerInvariant();
                        hashed++;
                    }
                    lock (s_lock) s_byId[id] = entry;
                    if (hashed > 0) Save(root);
                }
                Save(root);
                FiresLogger.LogInfo($"[BcImport] local world caches: {files.Count} found, {reused} known, {hashed} hashed now; "
                    + "a join whose server cache matches one uses it with no download.");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[BcImport] local world caches: indexing stopped ({ex.GetType().Name}: {ex.Message}); those worlds download as before."); }
        }

        private static Dictionary<string, Entry> Load(string root)
        {
            var kept = new Dictionary<string, Entry>();
            try
            {
                string index = Path.Combine(root, IndexName);
                if (!File.Exists(index)) return kept;
                foreach (string line in File.ReadAllLines(index))
                {
                    var parts = line.Split('|');
                    if (parts.Length != 4 || !long.TryParse(parts[2], out long len) || !long.TryParse(parts[3], out long ticks)) continue;
                    kept[parts[0]] = new Entry { Path = parts[1], Length = len, Ticks = ticks };
                }
            }
            catch { }
            return kept;
        }

        private static void Save(string root)
        {
            try
            {
                List<string> lines;
                lock (s_lock) lines = s_byId.Select(p => $"{p.Key}|{p.Value.Path}|{p.Value.Length}|{p.Value.Ticks}").ToList();
                string index = Path.Combine(root, IndexName), part = index + ".part";
                File.WriteAllLines(part, lines);
                if (File.Exists(index)) File.Delete(index);
                File.Move(part, index);
            }
            catch { }
        }

        // The main menu: a client only (a dedicated server has no FejdStartup), well before any join.
        [HarmonyPatch(typeof(FejdStartup), "Start")]
        private static class FejdStartup_Start_LocalCaches
        {
            private static void Postfix() => Start();
        }
    }
}
