using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.BcImport
{
    // D13 (Tools\BC_CHUNK_CONVERSION.md): the server's lakes, rivers and streams for a joining client. Valheim builds them in
    // WorldGenerator.Pregenerate on every peer, inside the WorldGenerator constructor, so inside a client's RPC_PeerInfo:
    // FindLakes, PlaceRivers and PlaceStreams (twice) search the world through GetBaseHeight (R6: rivers 31 ms, streams 289,
    // Deep North streams 163), and each Place ends in RenderRivers, which turns its list into the river grid every height
    // read on that peer uses. Pregeneration reads the real world (FAT's planning scope), so every peer finds the same lists.
    // The lists are small: the server sends them with the Random state each RenderRivers started from (it draws the widths),
    // and the client replays only RenderRivers. The grid it builds must checksum to the server's, or it is cleared and the
    // client searches the vanilla way. Anything missing or off falls back to vanilla, with its own line.
    internal static class RiverSync
    {
        private const int Magic = 0x52565246;
        private const int FormatVersion = 1;
        private const int PassCount = 3;
        private const int RiversPass = 0;
        private const int StreamsPass = 1;
        private const int StateWords = 4;
        private const int FloatsPerRiver = 10;
        private const int MaxLakes = 100000;
        private const int MaxRiversPerPass = 1000000;
        private const double KilobytesPerByte = 1d / 1024d;

        private static readonly AccessTools.FieldRef<WorldGenerator, List<Vector2>> LakesOf =
            AccessTools.FieldRefAccess<WorldGenerator, List<Vector2>>("m_lakes");
        private static readonly AccessTools.FieldRef<WorldGenerator, List<WorldGenerator.River>> RiversOf =
            AccessTools.FieldRefAccess<WorldGenerator, List<WorldGenerator.River>>("m_rivers");
        private static readonly AccessTools.FieldRef<WorldGenerator, List<WorldGenerator.River>> StreamsOf =
            AccessTools.FieldRefAccess<WorldGenerator, List<WorldGenerator.River>>("m_streams");
        private static readonly AccessTools.FieldRef<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>> GridOf =
            AccessTools.FieldRefAccess<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>>("m_riverPoints");
        private static readonly AccessTools.FieldRef<WorldGenerator, int> RiverSeedOf = AccessTools.FieldRefAccess<WorldGenerator, int>("m_riverSeed");
        private static readonly AccessTools.FieldRef<WorldGenerator, int> StreamSeedOf = AccessTools.FieldRefAccess<WorldGenerator, int>("m_streamSeed");
        private static readonly MethodInfo RenderRivers = AccessTools.Method(typeof(WorldGenerator), "RenderRivers");
        private static readonly Type RiverAddType = RenderRivers?.GetParameters()[1].ParameterType;
        private static readonly FieldInfo[] StateFields = FindStateFields();

        private static List<Pass> s_capture;
        private static Blob s_blob;

        private sealed class Pass
        {
            internal int Rule;
            internal int[] State;
            internal List<WorldGenerator.River> Rivers;
        }

        internal sealed class Blob
        {
            internal byte[] Data;
            internal string Id;
            internal int Lakes, Rivers, Streams, DeepNorthStreams, Cells;
        }

        /// <summary>The server's lists for this world, or null when this peer has none to give.</summary>
        internal static Blob ServerBlob => s_blob;

        internal static bool Replayable => RenderRivers != null && RiverAddType != null && StateFields != null;

        // ── Server ──────────────────────────────────────────────────────────
        // From FAT's one WorldGenerator.Pregenerate hook (FatVanillaHooks): the server records its own run.
        internal static void BeforePregenerate(WorldGenerator generator)
        {
            s_capture = IsServerWorld(generator) && Replayable ? new List<Pass>(PassCount) : null;
            if (IsServerWorld(generator)) s_blob = null;
        }

        [HarmonyPatch(typeof(WorldGenerator), "RenderRivers")]
        private static class CaptureRenderRivers
        {
            private static void Prefix(object[] __args)
            {
                if (s_capture == null) return;
                s_capture.Add(new Pass
                {
                    Rule = Convert.ToInt32(__args[1]),
                    State = ReadState(UnityEngine.Random.state),
                    Rivers = (List<WorldGenerator.River>)__args[0],
                });
            }
        }

        internal static void AfterPregenerate(WorldGenerator generator)
        {
            var passes = s_capture;
            s_capture = null;
            if (passes == null) return;
            var clock = Stopwatch.StartNew();
            if (passes.Count != PassCount)
            {
                FiresLogger.LogWarning($"[BcImport] rivers: this world's pregeneration drew {passes.Count} river pass(es), not {PassCount}, "
                    + "so joining clients search for their own rivers (vanilla).");
                return;
            }
            var lakes = LakesOf(generator) ?? new List<Vector2>();
            uint checksum = GridChecksum(GridOf(generator), out int cells);
            byte[] data = Serialise(generator, lakes, passes, cells, checksum);
            s_blob = new Blob
            {
                Data = data,
                Id = BcJoinSync.IdOf(data),
                Lakes = lakes.Count,
                Rivers = passes[RiversPass].Rivers.Count,
                Streams = passes[StreamsPass].Rivers.Count,
                DeepNorthStreams = passes[PassCount - 1].Rivers.Count,
                Cells = cells,
            };
            FiresLogger.LogInfo($"[BcImport] rivers: {Describe(s_blob)} ready for joining clients ({clock.ElapsedMilliseconds} ms).");
        }

        // ── Client ──────────────────────────────────────────────────────────
        // First thing in Pregenerate on a client: true when the server's lists now stand in for vanilla's search.
        internal static bool TryReplay(WorldGenerator generator)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || WorldGeneratorAccess.IsMenuWorld(generator)) return false;
            switch (BcJoinSync.RiversAnswer(out string path))
            {
                case BcJoinSync.ServerAnswer.Data:
                    break;
                case BcJoinSync.ServerAnswer.Failed:
                    FiresLogger.LogWarning("[BcImport] rivers: the server's lists did not arrive intact, so this client searches for its own (vanilla).");
                    return false;
                default:
                    FiresLogger.LogInfo("[BcImport] rivers: the server sent none this join (its FAT predates them), so this client searches "
                        + "for its own (vanilla).");
                    return false;
            }
            if (!Replayable)
            {
                FiresLogger.LogWarning("[BcImport] rivers: this game's RenderRivers or Random.State is not the one FAT knows, so this client "
                    + "searches for its own (vanilla).");
                return false;
            }
            var clock = Stopwatch.StartNew();
            Parsed parsed;
            try { parsed = Parse(File.ReadAllBytes(path)); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BcImport] rivers: {path} could not be read ({ex.GetType().Name}: {ex.Message}), so this client "
                    + "searches for its own (vanilla).");
                return false;
            }
            if (parsed.RiverSeed != RiverSeedOf(generator) || parsed.StreamSeed != StreamSeedOf(generator))
            {
                FiresLogger.LogWarning("[BcImport] rivers: the server's lists are for other river seeds than this world's, so this client "
                    + "searches for its own (vanilla).");
                return false;
            }
            long loadMs = clock.ElapsedMilliseconds;
            LakesOf(generator) = parsed.Lakes;
            var outside = UnityEngine.Random.state;
            try
            {
                foreach (var pass in parsed.Passes)
                {
                    UnityEngine.Random.state = WriteState(pass.State);
                    RenderRivers.Invoke(generator, new[] { pass.Rivers, Enum.ToObject(RiverAddType, pass.Rule) });
                }
            }
            finally { UnityEngine.Random.state = outside; }
            RiversOf(generator) = parsed.Passes[RiversPass].Rivers;
            StreamsOf(generator) = parsed.Passes[StreamsPass].Rivers;
            long replayMs = clock.ElapsedMilliseconds - loadMs;
            uint checksum = GridChecksum(GridOf(generator), out int cells);
            if (checksum != parsed.Checksum || cells != parsed.Cells)
            {
                generator.CleanCachedRiverData();
                LakesOf(generator) = null;
                FiresLogger.LogWarning($"[BcImport] rivers: the replayed river grid ({cells:N0} cells, {checksum:X8}) is not the server's "
                    + $"({parsed.Cells:N0} cells, {parsed.Checksum:X8}), so it was cleared and this client searches for its own (vanilla).");
                return false;
            }
            FiresLogger.LogInfo($"[BcImport] rivers: took the server's {parsed.Lakes.Count} lakes, {parsed.Passes[RiversPass].Rivers.Count} rivers "
                + $"and {parsed.Passes[StreamsPass].Rivers.Count + parsed.Passes[PassCount - 1].Rivers.Count} streams instead of searching: "
                + $"load {loadMs} ms, replay {replayMs} ms, check {clock.ElapsedMilliseconds - loadMs - replayMs} ms; the river grid "
                + $"({cells:N0} cells) matches the server's.");
            return true;
        }

        internal static string Describe(Blob blob)
            => $"{blob.Lakes} lakes, {blob.Rivers} rivers, {blob.Streams + blob.DeepNorthStreams} streams, {blob.Cells:N0} river grid cells "
                + $"({blob.Data.Length * KilobytesPerByte:0} KB)";

        // ── The blob ────────────────────────────────────────────────────────
        // Magic, format, the two river seeds; the lakes; three passes (RiverAdd rule, Random state, rivers as ten floats each);
        // the river grid's cell count and checksum.
        private sealed class Parsed
        {
            internal int RiverSeed, StreamSeed, Cells;
            internal uint Checksum;
            internal List<Vector2> Lakes;
            internal List<Pass> Passes;
        }

        private static byte[] Serialise(WorldGenerator generator, List<Vector2> lakes, List<Pass> passes, int cells, uint checksum)
        {
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(RiverSeedOf(generator));
                writer.Write(StreamSeedOf(generator));
                writer.Write(lakes.Count);
                foreach (var lake in lakes)
                {
                    writer.Write(lake.x);
                    writer.Write(lake.y);
                }
                writer.Write(passes.Count);
                foreach (var pass in passes)
                {
                    writer.Write(pass.Rule);
                    foreach (int word in pass.State) writer.Write(word);
                    writer.Write(pass.Rivers.Count);
                    foreach (var river in pass.Rivers)
                    {
                        writer.Write(river.p0.x);
                        writer.Write(river.p0.y);
                        writer.Write(river.p1.x);
                        writer.Write(river.p1.y);
                        writer.Write(river.center.x);
                        writer.Write(river.center.y);
                        writer.Write(river.widthMin);
                        writer.Write(river.widthMax);
                        writer.Write(river.curveWidth);
                        writer.Write(river.curveWavelength);
                    }
                }
                writer.Write(cells);
                writer.Write(checksum);
                writer.Flush();
                return output.ToArray();
            }
        }

        private static Parsed Parse(byte[] data)
        {
            using (var reader = new BinaryReader(new MemoryStream(data, false)))
            {
                if (reader.ReadInt32() != Magic) throw new InvalidDataException("not a river list");
                int version = reader.ReadInt32();
                if (version != FormatVersion) throw new InvalidDataException($"river list format {version}, this build reads {FormatVersion}");
                var parsed = new Parsed { RiverSeed = reader.ReadInt32(), StreamSeed = reader.ReadInt32() };
                int lakes = CountIn(reader, MaxLakes, "lakes");
                parsed.Lakes = new List<Vector2>(lakes);
                for (int i = 0; i < lakes; i++) parsed.Lakes.Add(new Vector2(reader.ReadSingle(), reader.ReadSingle()));
                int passes = reader.ReadInt32();
                if (passes != PassCount) throw new InvalidDataException($"{passes} river passes, expected {PassCount}");
                parsed.Passes = new List<Pass>(passes);
                for (int p = 0; p < passes; p++)
                {
                    var pass = new Pass { Rule = reader.ReadInt32(), State = new int[StateWords] };
                    for (int w = 0; w < StateWords; w++) pass.State[w] = reader.ReadInt32();
                    int rivers = CountIn(reader, MaxRiversPerPass, "rivers");
                    pass.Rivers = new List<WorldGenerator.River>(rivers);
                    for (int r = 0; r < rivers; r++)
                    {
                        pass.Rivers.Add(new WorldGenerator.River
                        {
                            p0 = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                            p1 = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                            center = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                            widthMin = reader.ReadSingle(),
                            widthMax = reader.ReadSingle(),
                            curveWidth = reader.ReadSingle(),
                            curveWavelength = reader.ReadSingle(),
                        });
                    }
                    parsed.Passes.Add(pass);
                }
                parsed.Cells = reader.ReadInt32();
                parsed.Checksum = reader.ReadUInt32();
                return parsed;
            }
        }

        private static int CountIn(BinaryReader reader, int limit, string what)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > limit) throw new InvalidDataException($"{count} {what}");
            return count;
        }

        // ── The river grid's checksum ───────────────────────────────────────
        // Every cell and point in the grid's own order: the same RenderRivers calls on the same lists build the same
        // dictionary in the same order, so a difference anywhere changes the sum.
        private static uint GridChecksum(Dictionary<Vector2i, WorldGenerator.RiverPoint[]> grid, out int cells)
        {
            cells = grid?.Count ?? 0;
            uint running = Crc32.Start;
            if (grid == null) return Crc32.Finish(running);
            var bits = new FloatBits();
            foreach (var pair in grid)
            {
                running = Crc32.Add(running, pair.Key.x);
                running = Crc32.Add(running, pair.Key.y);
                running = Crc32.Add(running, pair.Value.Length);
                foreach (var point in pair.Value)
                {
                    running = Crc32.Add(running, bits.Of(point.p.x));
                    running = Crc32.Add(running, bits.Of(point.p.y));
                    running = Crc32.Add(running, bits.Of(point.w));
                    running = Crc32.Add(running, bits.Of(point.w2));
                }
            }
            return Crc32.Finish(running);
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] private float _float;
            [FieldOffset(0)] private int _int;

            internal int Of(float value)
            {
                _float = value;
                return _int;
            }
        }

        // ── Random.State ────────────────────────────────────────────────────
        private static FieldInfo[] FindStateFields()
        {
            var fields = new FieldInfo[StateWords];
            for (int i = 0; i < StateWords; i++)
            {
                fields[i] = typeof(UnityEngine.Random.State).GetField("s" + i, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fields[i] == null || fields[i].FieldType != typeof(int)) return null;
            }
            return fields;
        }

        private static int[] ReadState(UnityEngine.Random.State state)
        {
            object boxed = state;
            var words = new int[StateWords];
            for (int i = 0; i < StateWords; i++) words[i] = (int)StateFields[i].GetValue(boxed);
            return words;
        }

        private static UnityEngine.Random.State WriteState(int[] words)
        {
            object boxed = default(UnityEngine.Random.State);
            for (int i = 0; i < StateWords; i++) StateFields[i].SetValue(boxed, words[i]);
            return (UnityEngine.Random.State)boxed;
        }

        private static bool IsServerWorld(WorldGenerator generator)
            => ZNet.instance != null && ZNet.instance.IsServer() && !WorldGeneratorAccess.IsMenuWorld(generator);
    }
}
