using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VerdantsAscent.Terrain.Zones
{
    // A 1.0 world folder holds one generation of main files per save: _main.<n>.chunks, .db2 and .fwl2 are written in that
    // order, each flushed to disk before the next starts, then .ok, and only then is the generation before deleted. A crash
    // mid-save leaves two generations. Vanilla picks the ordinally first .fwl2, not the newest ("_main.10" sorts before
    // "_main.9", and "_main.9." before "_main.90"), so it can pick a torn meta that fails to load (a dedicated server then
    // creates a NEW world under the name) or bring back a stale generation many saves later. (Vanilla 1.0.15 instead keeps the
    // newest generation with all four main files at its first save lookup and DELETES the rest, so FiresWorldLoadGuard sorts
    // before that lookup: crash series 2026-09-29, OPEN_BUGS #44.) This keeps one generation: the
    // newest complete one. The other generations' main files, and copies of chunk files written after the kept meta, go into a
    // snapshot folder, so nothing is lost. Tools\SAVE_SAFETY_AUDIT.md
    internal static class FiresSaveGenerations
    {
        private static readonly Regex MainFile = new Regex(@"^_main\.(\d+)\.(fwl2|db2|chunks|ok)$", RegexOptions.CultureInvariant);
        private const string MetaExtension = "fwl2";
        private const string DataExtension = "db2";
        private const string ChunkMapExtension = "chunks";
        private const string OkExtension = "ok";
        private const string ChunkFilePattern = "*.chunk";
        private const string MainFilePattern = "_main.*";
        private const int LengthPrefixBytes = 4;

        internal sealed class Generation
        {
            internal uint Number;
            internal readonly List<string> Files = new List<string>();
            internal bool MetaWhole;
            internal bool MetaPresent;
            internal bool HasData;
            internal bool HasChunkMap;
            internal bool HasOk;
            // Vanilla 1.0.15's first save lookup (SaveCollection.KeepOnlyNewest) keeps only a generation with all four main
            // files and deletes every other one, so a generation without its .ok is never kept here either.
            internal bool Complete => MetaWhole && HasData && HasChunkMap && HasOk;

            internal string Describe()
            {
                string meta = !MetaPresent ? "no meta" : MetaWhole ? "whole meta" : "TORN meta";
                return $"_main.{Number} ({meta}, {(HasData ? "db2" : "no db2")}, {(HasChunkMap ? "chunk map" : "no chunk map")}, "
                    + $"{(HasOk ? "ok" : "no ok")})";
            }
        }

        internal sealed class Plan
        {
            internal Generation Keep;
            internal readonly List<Generation> Others = new List<Generation>();
            internal string Why;
        }

        internal static List<Generation> Scan(string saveDirectory)
        {
            var byNumber = new SortedDictionary<uint, Generation>();
            if (!Directory.Exists(saveDirectory)) return new List<Generation>();
            foreach (string path in Directory.GetFiles(saveDirectory, MainFilePattern))
            {
                var match = MainFile.Match(Path.GetFileName(path));
                if (!match.Success || !uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint number)) continue;
                if (!byNumber.TryGetValue(number, out var generation))
                    byNumber[number] = generation = new Generation { Number = number };
                generation.Files.Add(path);
                string extension = match.Groups[2].Value;
                if (extension == MetaExtension)
                {
                    generation.MetaPresent = true;
                    generation.MetaWhole = IsWholeMeta(path);
                }
                else if (extension == DataExtension) generation.HasData = true;
                else if (extension == ChunkMapExtension) generation.HasChunkMap = true;
                else if (extension == OkExtension) generation.HasOk = true;
            }
            return byNumber.Values.ToList();
        }

        // FileWriter writes the meta as one length-prefixed block through a 256 KB buffer and flushes it at the end, so a
        // crash while writing it leaves an empty or short file.
        internal static bool IsWholeMeta(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < LengthPrefixBytes) return false;
                    int length = reader.ReadInt32();
                    return length > 0 && stream.Length == LengthPrefixBytes + (long)length;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // Null when the folder already holds a single generation with a whole meta: vanilla loads that as it is.
        internal static Plan PlanFor(List<Generation> generations)
        {
            if (generations.Count == 0) return null;
            if (generations.Count == 1 && (generations[0].MetaWhole || !generations[0].MetaPresent)) return null;
            var plan = new Plan
            {
                Keep = generations.LastOrDefault(g => g.Complete) ?? generations.LastOrDefault(g => g.MetaWhole),
            };
            plan.Others.AddRange(generations.Where(g => g != plan.Keep));
            plan.Why = generations.Count == 1
                ? $"its only save, {generations[0].Describe()}, is torn"
                : $"it holds {generations.Count} save generations: " + string.Join(", ", generations.Select(g => g.Describe()));
            return plan;
        }

        // Copies first, then moves the other generations' main files. A failed copy stops before anything is moved. Copied: every
        // chunk file the kept generation's chunk map does not list - vanilla's load deletes those as orphans, and they are the
        // other generations' chunk versions, older (a set-aside older save needs them to be restored on its own; crash series
        // rerun 2026-09-29, OPEN_BUGS #44) or newer - plus, as before, any written after the kept meta.
        internal static bool Apply(Plan plan, string saveDirectory, string snapshotFolder, out int copied, out int moved, out string error)
        {
            error = null;
            copied = 0;
            moved = 0;
            try
            {
                Directory.CreateDirectory(snapshotFolder);
                DateTime keptMetaTime = File.GetLastWriteTimeUtc(plan.Keep.Files.First(f => f.EndsWith("." + MetaExtension, StringComparison.Ordinal)));
                HashSet<string> keptChunks = ReadChunkMap(plan.Keep.Files.First(f => f.EndsWith("." + ChunkMapExtension, StringComparison.Ordinal)));
                foreach (string chunk in Directory.GetFiles(saveDirectory, ChunkFilePattern))
                {
                    bool orphan = keptChunks != null && !keptChunks.Contains(Path.GetFileName(chunk));
                    if (!orphan && File.GetLastWriteTimeUtc(chunk) <= keptMetaTime) continue;
                    File.Copy(chunk, Path.Combine(snapshotFolder, Path.GetFileName(chunk)), overwrite: false);
                    copied++;
                }
                foreach (var generation in plan.Others)
                    foreach (string file in generation.Files)
                    {
                        File.Move(file, Path.Combine(snapshotFolder, Path.GetFileName(file)));
                        moved++;
                    }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = $"{ex.GetType().Name}: {ex.Message} ({copied} chunk file(s) copied and {moved} main file(s) moved before it)";
                return false;
            }
        }

        // The chunk file names a generation's .chunks map lists, as vanilla's ChunkSaveMapping writes and names them: ushort format,
        // int ZDO count, int entries, then per entry ushort chunk, byte size, uint version, int ZDO count. Null when it cannot be
        // read; Apply then falls back to copying only the chunk files written after the kept meta.
        internal static HashSet<string> ReadChunkMap(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream))
                {
                    reader.ReadUInt16();
                    reader.ReadInt32();
                    int entries = reader.ReadInt32();
                    if (entries < 0 || stream.Length < 10L + 11L * entries) return null;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < entries; i++)
                    {
                        ushort chunk = reader.ReadUInt16();
                        byte size = reader.ReadByte();
                        uint version = reader.ReadUInt32();
                        reader.ReadInt32();
                        names.Add(string.Format(CultureInfo.InvariantCulture, "{0:x2}_{1:x2}__{2}_{3}.chunk", chunk >> 8, chunk & 0xFF, size, version));
                    }
                    return names;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) { return null; }
        }

        // Copies every generation's main files into the snapshot and moves nothing: for a folder with no complete generation,
        // which vanilla's first save lookup is about to empty.
        internal static bool CopyAside(List<Generation> generations, string snapshotFolder, out int copied, out string error)
        {
            error = null;
            copied = 0;
            try
            {
                Directory.CreateDirectory(snapshotFolder);
                foreach (var generation in generations)
                    foreach (string file in generation.Files)
                    {
                        File.Copy(file, Path.Combine(snapshotFolder, Path.GetFileName(file)), overwrite: false);
                        copied++;
                    }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = $"{ex.GetType().Name}: {ex.Message} ({copied} main file(s) copied before it)";
                return false;
            }
        }
    }
}
