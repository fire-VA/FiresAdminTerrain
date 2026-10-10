using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── Alt-biome paint (Fire, 2026-09-27; Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md, "Alt-biome paint tools") ──────────
    // The Chaos Hammer's Paint entries lay circles over vanilla 1.0's alt-biome map. Each circle carries a brush: one
    // blend (an AltBiome) on one ground biome, the plain ground biome with no blend, or Restore (the world's own sector).
    // The newest circle holding a point decides that point's sector (BiomePaint.Sectors), so weather, music, the biome's
    // name, creature spawns and vegetation all read the painted blend. A paint replaces whatever blends were there.
    //
    // A paint also turns its circle's ground to the brush's biome (Fire's popup: "Turn the ground too"): its look, what
    // grows there and what spawns there, never the land's shape (BiomePaint.Ground). The locations under it follow the
    // paint (BiomePaint.Locations).
    //
    // The server owns the list: it saves it beside the world and sends it to every client, which installs it and
    // refreshes the loaded terrain it touches. Lookups run on worker threads too, so the live list is an immutable
    // snapshot swapped whole.
    internal static partial class BiomePaint
    {
        internal enum Brush : byte { Blend = 0, Plain = 1, Restore = 2 }

        internal struct Circle
        {
            internal uint Id;
            internal float X, Z, R;
            internal Brush Brush;
            internal Heightmap.Biome Ground;
            internal string Blend;

            internal bool Holds(float wx, float wz)
            {
                float dx = wx - X, dz = wz - Z;
                return dx * dx + dz * dz <= R * R;
            }
        }

        internal const float MinRadius = 2f;
        internal const float MaxRadius = 96f;
        private const float CellSize = 64f;

        private sealed class Snapshot
        {
            internal static readonly Snapshot Empty = new Snapshot(new Circle[0], new BiomeSector[0]);

            internal readonly Circle[] Circles;
            internal readonly BiomeSector[] Sectors;
            internal readonly Dictionary<long, int[]> Cells = new Dictionary<long, int[]>();

            // Oldest first; each cell lists the circles over it newest first.
            internal Snapshot(Circle[] circles, BiomeSector[] sectors)
            {
                Circles = circles;
                Sectors = sectors;
                var lists = new Dictionary<long, List<int>>();
                for (int i = circles.Length - 1; i >= 0; i--)
                {
                    var c = circles[i];
                    int x0 = Cell(c.X - c.R), x1 = Cell(c.X + c.R), z0 = Cell(c.Z - c.R), z1 = Cell(c.Z + c.R);
                    for (int cz = z0; cz <= z1; cz++)
                        for (int cx = x0; cx <= x1; cx++)
                        {
                            long key = Key(cx, cz);
                            if (!lists.TryGetValue(key, out var list)) lists[key] = list = new List<int>();
                            list.Add(i);
                        }
                }
                foreach (var pair in lists) Cells[pair.Key] = pair.Value.ToArray();
            }
        }

        private static volatile Snapshot s_snapshot = Snapshot.Empty;
        private static readonly List<Circle> s_circles = new List<Circle>();
        private static uint s_nextId = 1;
        private static uint s_hash;
        private static World s_world;

        internal static bool Any => s_snapshot.Circles.Length > 0;

        private static int Cell(float v) => Mathf.FloorToInt(v / CellSize);
        private static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
        private static long CellKey(float wx, float wz) => Key(Cell(wx), Cell(wz));

        // ── Entry names ─────────────────────────────────────────────────────
        // A brush travels as one string: "b|Swamp|Bog Swamp", "p|Meadows|", "r||".
        private const char Separator = '|';
        private const string BlendCode = "b";
        private const string PlainCode = "p";
        private const string RestoreCode = "r";

        internal static string KeyOf(Brush brush, Heightmap.Biome ground, string blend)
            => (brush == Brush.Blend ? BlendCode : brush == Brush.Plain ? PlainCode : RestoreCode) + Separator
               + (brush == Brush.Restore ? string.Empty : ground.ToString()) + Separator + (blend ?? string.Empty);

        internal static bool TryParseKey(string key, out Brush brush, out Heightmap.Biome ground, out string blend)
        {
            brush = Brush.Restore;
            ground = Heightmap.Biome.None;
            blend = string.Empty;
            var parts = (key ?? string.Empty).Split(Separator);
            if (parts.Length != 3) return false;
            blend = parts[2];
            if (parts[0] == RestoreCode) return true;
            brush = parts[0] == BlendCode ? Brush.Blend : Brush.Plain;
            if (parts[0] != BlendCode && parts[0] != PlainCode) return false;
            if (!Enum.TryParse(parts[1], out ground) || ground == Heightmap.Biome.None) return false;
            return brush == Brush.Plain || FindBlend(blend) != null;
        }

        internal static AltBiome FindBlend(string name)
        {
            if (string.IsNullOrEmpty(name) || AltBiomeList.m_altBiomes == null) return null;
            foreach (var alt in AltBiomeList.m_altBiomes)
                if (alt != null && alt.m_name == name) return alt;
            return null;
        }

        // ── Server: paint, take back, put back ──────────────────────────────
        internal static bool TryPaint(string key, Vector3 centre, float radius, long asker, out uint id, out string report)
        {
            id = 0u;
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            if (!TryParseKey(key, out var brush, out var ground, out string blend))
            {
                report = "That paint is not one this world knows.";
                return false;
            }
            EnsureWorld();
            var circle = new Circle
            {
                Id = s_nextId++,
                X = Mathf.Round(centre.x),
                Z = Mathf.Round(centre.z),
                R = Mathf.Clamp(Mathf.Round(radius), MinRadius, MaxRadius),
                Brush = brush,
                Ground = ground,
                Blend = brush == Brush.Blend ? blend : string.Empty,
            };
            float took = Coverage(circle);
            s_circles.Add(circle);
            Commit();
            string grown = Regrow(circle, asker);
            id = circle.Id;
            report = $"Painted {Describe(circle)} over {circle.R:0} m round ({circle.X:0},{circle.Z:0}). {Took(circle, took)} {grown}";
            return true;
        }

        // Undo: the circle leaves the list and its ground grows again as the older paint or the world has it.
        internal static bool TryTakeBack(uint id, out byte[] record, out string report)
        {
            record = null;
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            int index = s_circles.FindIndex(c => c.Id == id);
            if (index < 0)
            {
                report = "that paint is no longer there.";
                return false;
            }
            var circle = s_circles[index];
            record = Pack(new List<Circle> { circle });
            s_circles.RemoveAt(index);
            Commit();
            report = $"{Describe(circle)} round ({circle.X:0},{circle.Z:0}) is gone. {Regrow(circle)}";
            return true;
        }

        // Redo: the kept circle is laid again as the newest.
        internal static bool TryPutBack(byte[] record, out uint id, out string report)
        {
            id = 0u;
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            var kept = Unpack(record);
            if (kept.Count != 1)
            {
                report = "the kept paint could not be read.";
                return false;
            }
            var circle = kept[0];
            circle.Id = s_nextId++;
            s_circles.Add(circle);
            Commit();
            id = circle.Id;
            report = $"{Describe(circle)} round ({circle.X:0},{circle.Z:0}) is back. {Regrow(circle)}";
            return true;
        }

        internal static string Describe(string key)
            => TryParseKey(key, out var brush, out var ground, out string blend)
                ? Describe(new Circle { Brush = brush, Ground = ground, Blend = blend })
                : "unknown";

        internal static string Describe(in Circle circle)
        {
            switch (circle.Brush)
            {
                case Brush.Restore: return "the world's own blends";
                case Brush.Plain: return $"plain {circle.Ground}";
                default: return $"{circle.Blend} on {circle.Ground}";
            }
        }

        private static string Took(in Circle circle, float share)
        {
            if (circle.Brush == Brush.Restore) return string.Empty;
            if (share >= 0.99f) return $"It was all {circle.Ground} ground already.";
            if (share <= 0.01f) return $"Its ground turned to {circle.Ground}.";
            return $"{share * 100f:0} % was {circle.Ground} ground already; the rest turned to {circle.Ground}.";
        }

        // The share of the circle whose own ground is the brush's biome, read from the world's sectors on a 4 m grid.
        private const float CoverageStep = 4f;

        private static float Coverage(in Circle circle)
        {
            var generator = WorldGenerator.instance;
            if (generator == null || circle.Brush == Brush.Restore) return 1f;
            int inside = 0, matching = 0;
            for (float dz = -circle.R; dz <= circle.R; dz += CoverageStep)
                for (float dx = -circle.R; dx <= circle.R; dx += CoverageStep)
                {
                    if (dx * dx + dz * dz > circle.R * circle.R) continue;
                    inside++;
                    if (OriginalBiome(generator, circle.X + dx, circle.Z + dz) == circle.Ground) matching++;
                }
            return inside == 0 ? 0f : (float)matching / inside;
        }

        // ── The live list ───────────────────────────────────────────────────
        // Saves, installs and sends the server's list after any change.
        private static void Commit()
        {
            Save();
            Install(new List<Circle>(s_circles));
            Broadcast();
        }

        private static void Install(List<Circle> circles)
        {
            var old = s_snapshot;
            var sectors = new BiomeSector[circles.Count];
            for (int i = 0; i < circles.Count; i++) sectors[i] = SectorFor(circles[i]);
            s_snapshot = new Snapshot(circles.ToArray(), sectors);
            s_hash = Hash(Pack(circles));
            RefreshTerrain(old.Circles, s_snapshot.Circles);
        }

        // ── Beside the world ────────────────────────────────────────────────
        // firesgen_<world>_biomepaint.bin: magic, format, the world's uid and seed (a recreated world of the same name
        // starts clean), the next id, then the circles oldest first; from format 2, the swapped zones' locations
        // (BiomePaint.Locations). Format 1 files read as having no swaps.
        private const uint FileMagic = 0x54504246u;
        private const byte FirstFileFormat = 1;
        private const byte SwapsFileFormat = 2;
        private const byte FileFormat = SwapsFileFormat;

        private static string FilePath()
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(ZNet.World?.m_name ?? "world", "_biomepaint.bin");
        }

        // The server reads its list once per world; a client starts empty and asks.
        private static void EnsureWorld()
        {
            var world = ZNet.World;
            if (ReferenceEquals(world, s_world)) return;
            s_world = world;
            s_circles.Clear();
            s_pending.Clear();
            ResetLocations();
            s_nextId = 1;
            if (world != null && ZNet.instance != null && ZNet.instance.IsServer()) Load(world);
            Install(new List<Circle>(s_circles));
            s_asked = 0f;
        }

        private static void Load(World world)
        {
            string path = FilePath();
            if (!File.Exists(path)) return;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.ReadUInt32() != FileMagic) throw new InvalidDataException("not a biome paint file");
                    byte format = reader.ReadByte();
                    if (format < FirstFileFormat || format > FileFormat) throw new InvalidDataException($"format {format} is newer than this FAT reads");
                    long uid = reader.ReadInt64();
                    int seed = reader.ReadInt32();
                    if (uid != world.m_uid || seed != world.m_seed)
                    {
                        FiresLogger.LogWarning($"[BiomePaint] {Path.GetFileName(path)} belongs to another world by this name; its paint is not used.");
                        return;
                    }
                    s_nextId = reader.ReadUInt32();
                    s_circles.AddRange(ReadCircles(reader));
                    if (format >= SwapsFileFormat) ReadSwaps(reader);
                }
                FiresLogger.LogInfo($"[BiomePaint] {s_circles.Count} painted circle(s) and {s_swaps.Count} swapped location(s) read for '{world.m_name}'.");
            }
            catch (Exception ex)
            {
                s_circles.Clear();
                s_swaps.Clear();
                FiresLogger.LogWarning($"[BiomePaint] {Path.GetFileName(path)} could not be read ({ex.Message}); the world shows no paint.");
            }
        }

        private static void Save()
        {
            var world = ZNet.World;
            if (world == null) return;
            string path = FilePath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                using (var writer = new BinaryWriter(File.Create(temp)))
                {
                    writer.Write(FileMagic);
                    writer.Write(FileFormat);
                    writer.Write(world.m_uid);
                    writer.Write(world.m_seed);
                    writer.Write(s_nextId);
                    writer.Write(Pack(s_circles));
                    WriteSwaps(writer);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[BiomePaint] could not save the paint ({ex.Message})."); }
        }

        private static byte[] Pack(List<Circle> circles)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(circles.Count);
                foreach (var c in circles)
                {
                    writer.Write(c.Id);
                    writer.Write(c.X);
                    writer.Write(c.Z);
                    writer.Write(c.R);
                    writer.Write((byte)c.Brush);
                    writer.Write((int)c.Ground);
                    writer.Write(c.Blend ?? string.Empty);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static List<Circle> Unpack(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<Circle>();
            using (var reader = new BinaryReader(new MemoryStream(data))) return ReadCircles(reader);
        }

        private static List<Circle> ReadCircles(BinaryReader reader)
        {
            var circles = new List<Circle>();
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
                circles.Add(new Circle
                {
                    Id = reader.ReadUInt32(),
                    X = reader.ReadSingle(),
                    Z = reader.ReadSingle(),
                    R = reader.ReadSingle(),
                    Brush = (Brush)reader.ReadByte(),
                    Ground = (Heightmap.Biome)reader.ReadInt32(),
                    Blend = reader.ReadString(),
                });
            return circles;
        }

        private static uint Hash(byte[] data)
        {
            uint h = 2166136261u;
            foreach (byte b in data) h = (h ^ b) * 16777619u;
            return h;
        }

        // ── Sync ────────────────────────────────────────────────────────────
        // A client asks with the hash of what it holds; the server answers only when it holds something else, and sends
        // the whole list to everyone on every change.
        private const string RpcAsk = "FiresAdminTerrain_BiomePaintAsk";
        private const string RpcList = "FiresAdminTerrain_BiomePaintList";
        private const float AskSeconds = 30f;

        private static ZRoutedRpc s_registeredOn;
        private static float s_asked;

        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_registeredOn, rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcAsk, RPC_Ask);
                rpc.Register<ZPackage>(RpcList, RPC_List);
                s_registeredOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[BiomePaint] RPC register failed: {ex.Message}"); }
        }

        // Every frame from zone mode's tick, in any world (the Chaos Hammer paints anywhere).
        internal static void Tick()
        {
            if (ZNet.instance == null || ZNet.World == null) return;
            EnsureWorld();
            EnsureRpcs();
            HookSeasons();
            TickTerrainRefresh();
            LogCounts();
            if (ZNet.instance.IsServer())
            {
                SettleLocations();
                PlacePending();
                GrowPending();
                return;
            }
            if (ZRoutedRpc.instance == null || Time.time < s_asked) return;
            s_asked = Time.time + AskSeconds;
            var pkg = new ZPackage();
            pkg.Write(s_hash);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcAsk, pkg);
        }

        private static void RPC_Ask(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            EnsureWorld();
            if (pkg.ReadUInt() != s_hash) Send(sender);
        }

        private static void Broadcast() => Send(ZRoutedRpc.Everybody);

        private static void Send(long target)
        {
            if (ZRoutedRpc.instance == null) return;
            var pkg = new ZPackage();
            pkg.Write(Pack(s_circles));
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcList, pkg);
        }

        private static void RPC_List(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            EnsureWorld();
            var circles = Unpack(pkg.ReadByteArray());
            s_circles.Clear();
            s_circles.AddRange(circles);
            Install(circles);
            FiresLogger.LogInfo($"[BiomePaint] the server's paint: {circles.Count} circle(s).");
        }

        internal static void Reset()
        {
            s_world = null;
            s_circles.Clear();
            s_snapshot = Snapshot.Empty;
            s_hash = 0u;
            s_asked = 0f;
            s_pending.Clear();
            ResetLocations();
        }
    }
}
