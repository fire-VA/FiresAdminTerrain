using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── Alt-biome paint ──────────
    // The Chaos Hammer's Paint entries lay circles over vanilla 1.0's alt-biome map. Each circle carries a brush: one
    // blend (an AltBiome) on one ground biome, the plain ground biome with no blend, or Restore (the world's own sector).
    // The newest circle holding a point decides that point's sector (BiomePaint.Sectors), so weather, music, the biome's
    // name, creature spawns and vegetation all read the painted blend. A paint replaces whatever blends were there.
    //
    // A paint also turns its circle's ground to the brush's biome: its look, what
    // grows there and what spawns there, never the land's shape (BiomePaint.Ground). The locations under it follow the
    // paint (BiomePaint.Locations).
    //
    // The server owns the list: it saves it beside the world and sends it to every client, which installs it and
    // refreshes the loaded terrain it touches. Lookups run on worker threads too, so the live list is an immutable
    // snapshot swapped whole.
    internal static partial class BiomePaint
    {
        internal enum Brush : byte { Blend = 0, Plain = 1, Restore = 2, Custom = 3 }   // 0.2.1373: Custom = an Expand World biome, named in Blend (BiomePaint.Custom)

        internal struct Circle
        {
            internal uint Id;
            internal float X, Z, R;
            internal Brush Brush;
            internal Heightmap.Biome Ground;
            internal string Blend;
            internal bool Height;   // 0.2.1373: this circle also turns the land's shape (BiomePaint.Height)
            internal PaintOutline Outline;   // 0.2.1375: a drawn area; X, Z, R are then the circle that bounds it
            internal PaintMapMask Mask;      // 0.2.1378: a black area of the BC map; also bounded by X, Z, R
            internal float BlendWidth;       // 0.2.1375: metres, below 0 = the server's default. Only with ExtraFlag on the wire
            internal bool HasExtra;

            internal bool Holds(float wx, float wz)
            {
                float dx = wx - X, dz = wz - Z;
                if (dx * dx + dz * dz > R * R) return false;
                if (Mask != null) return Mask.Holds(wx, wz);
                return Outline == null || Outline.Holds(wx, wz);
            }

            // How far inside the rim a point is (0 or less outside). `enough`: nothing cares about more than this.
            internal float Rim(float wx, float wz, float enough)
            {
                if (Mask != null) return Mask.Rim(wx, wz);
                if (Outline != null) return Outline.Rim(wx, wz, enough);
                float dx = wx - X, dz = wz - Z;
                return R - Mathf.Sqrt(dx * dx + dz * dz);
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
            internal readonly bool AnyHeight;

            // Oldest first; each cell lists the circles over it newest first.
            internal Snapshot(Circle[] circles, BiomeSector[] sectors)
            {
                Circles = circles;
                Sectors = sectors;
                foreach (var c in circles) if (c.Height) { AnyHeight = true; break; }
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
            => brush == Brush.Custom ? CustomCode + Separator + Separator + blend : (brush == Brush.Blend ? BlendCode : brush == Brush.Plain ? PlainCode : RestoreCode) + Separator
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
            if (parts[0] == CustomCode) { brush = Brush.Custom; return TryResolveCustom(blend, out ground); }
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
        internal static bool TryPaint(string key, Vector3 centre, float radius, long asker, out uint id, out string report, bool wipe = false, float blendWidth = -1f, float[] outlinePoints = null, int[] maskSeed = null)
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
                Blend = brush == Brush.Blend || brush == Brush.Custom ? blend : string.Empty,
            };
            if (outlinePoints != null)
            {
                if (!PaintOutline.TryCreate(outlinePoints, out var outline, out float cx, out float cz, out float bound, out string outlineWhy))
                {
                    report = outlineWhy;
                    s_nextId--;
                    return false;
                }
                circle.Outline = outline; circle.X = cx; circle.Z = cz; circle.R = bound; circle.HasExtra = true;
            }
            if (maskSeed != null)
            {
                var mask = new PaintMapMask(maskSeed[0], maskSeed[1]);
                if (!mask.Ensure()) { report = "That black map area could not be read."; s_nextId--; return false; }
                circle.Mask = mask; circle.X = mask.CentreX; circle.Z = mask.CentreZ; circle.R = mask.BoundR; circle.HasExtra = true;
            }
            circle.BlendWidth = float.IsNaN(blendWidth) || blendWidth < 0f ? -1f : Mathf.Min(blendWidth, MaxBlendWidth);
            if (circle.BlendWidth >= 0f) circle.HasExtra = true;
            circle.Height = HeightAllowed(circle, out string heightWhy);
            string wiped = string.Empty;
            if (wipe && !WipeGate(key, circle, asker, out wiped))
            {
                report = wiped;
                s_nextId--;
                return false;
            }
            if (circle.Height) { if (circle.Mask != null) BuildMaskHolds(circle); else BuildHolds(circle, out _, out _); }
            float took = circle.Mask != null ? 0f : Coverage(circle);   // 0.2.1378: an area kilometres across is not sampled on a 4 m grid here
            s_circles.Add(circle);
            Commit();
            var regrowKey = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.BiomePaintRegrowOnSave;
            string grown = brush == Brush.Custom && regrowKey != null && !regrowKey.Value ? "Its growth stays until a reset (key off)." : Regrow(circle, asker);
            id = circle.Id;
            report = $"Painted {Describe(circle)} over {(circle.Mask != null ? $"{circle.Mask.Area:0} m2 of a black map area" : circle.Outline != null ? $"{circle.Outline.Area:0} m2 inside a drawn outline" : $"{circle.R:0} m")} round ({circle.X:0},{circle.Z:0}). {Took(circle, took)} {grown} {heightWhy} {wiped}";
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
            DropHolds(circle.Id);
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
            var circle = Resolved(kept[0]);
            circle.Id = s_nextId++;
            if (circle.Height) { if (circle.Mask != null) BuildMaskHolds(circle); else BuildHolds(circle, out _, out _); }
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
                case Brush.Custom: return circle.Blend;
                default: return $"{circle.Blend} on {circle.Ground}";
            }
        }

        private static string Took(in Circle circle, float share)
        {
            if (circle.Brush == Brush.Restore) return string.Empty;
            string ground = circle.Brush == Brush.Custom ? circle.Blend : circle.Ground.ToString();
            if (share >= 0.99f) return $"It was all {ground} ground already.";
            if (share <= 0.01f) return $"Its ground turned to {ground}.";
            return $"{share * 100f:0} % was {ground} ground already; the rest turned to {ground}.";
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
                    if (dx * dx + dz * dz > circle.R * circle.R || !circle.Holds(circle.X + dx, circle.Z + dz)) continue;
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
            ResolveCustom(circles);
            if (!ReferenceEquals(circles, s_circles)) ResolveCustom(s_circles);
            var sectors = new BiomeSector[circles.Count];
            for (int i = 0; i < circles.Count; i++) sectors[i] = SectorFor(circles[i]);
            s_snapshot = new Snapshot(circles.ToArray(), sectors);
            uint hash = Hash(Pack(circles));
            if (hash != s_hash) BiomePaintVersion.Bump();   // 0.2.1058: the paint changed (FatApi.PaintVersion)
            s_hash = hash;
            if (s_snapshot.AnyHeight || old.AnyHeight)
            {
                if (!s_heightSaid) { s_heightSaid = true; FiresLogger.LogInfo("[BiomePaint] height paint is active on this world: custom biomes' circles turn the land (0.2.1373)"); }
                DropReadyTerrain();
            }
            RefreshTerrain(old.Circles, s_snapshot.Circles);
            NoteNewestHeight(old.Circles, s_snapshot.Circles);
            BiomeOverride.RefreshAllPieceSnow();   // pieces follow the painted ground's snow
        }

        // ── Beside the world ────────────────────────────────────────────────
        // firesgen_<world>_biomepaint.bin: magic, format, the world's uid and seed (a recreated world of the same name
        // starts clean), the next id, then the circles oldest first; from format 2, the swapped zones' locations
        // (BiomePaint.Locations). Format 1 files read as having no swaps.
        private const uint FileMagic = 0x54504246u;
        private const byte FirstFileFormat = 1;
        private const byte SwapsFileFormat = 2;
        private const byte FileFormat = HoldsFileFormat;   // 0.2.1374: format 3 adds the held ground; written only when there is any, so an older FAT still reads the rest

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
            s_holdMap = new Dictionary<uint, byte[]>();
            s_pending.Clear();
            ResetLocations();
            s_nextId = 1;
            if (world != null && ZNet.instance != null && ZNet.instance.IsServer()) { Load(world); ReadServerHeightBand(); }
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
                    s_holdMap = format >= HoldsFileFormat ? ReadHolds(reader) : new Dictionary<uint, byte[]>();
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
                    bool holds = s_holdMap.Count > 0;
                    writer.Write(FileMagic);
                    writer.Write(holds ? HoldsFileFormat : SwapsFileFormat);
                    writer.Write(world.m_uid);
                    writer.Write(world.m_seed);
                    writer.Write(s_nextId);
                    writer.Write(Pack(s_circles));
                    WriteSwaps(writer);
                    if (holds) WriteHolds(writer);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[BiomePaint] could not save the paint ({ex.Message})."); }
        }

        private static void NoteNewestHeight(Circle[] before, Circle[] after)
        {
            if (after.Length == 0 || after.Length <= before.Length) return;
            NoteHeightProof(after[after.Length - 1]);
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
                    writer.Write((byte)((byte)c.Brush | (c.Height ? HeightFlag : (byte)0) | (c.HasExtra ? ExtraFlag : (byte)0)));
                    writer.Write(c.Brush == Brush.Custom ? 0 : (int)c.Ground);   // a custom biome's bit depends on yaml order: its name is what is kept
                    writer.Write(c.Blend ?? string.Empty);
                    if (!c.HasExtra) continue;
                    writer.Write(c.BlendWidth);
                    if (c.Mask != null)
                    {
                        writer.Write(-1);   // 0.2.1378: a black map area travels as its seed pixel
                        writer.Write(c.Mask.SeedCol);
                        writer.Write(c.Mask.SeedRow);
                        continue;
                    }
                    float[] points = c.Outline != null ? c.Outline.Points : null;
                    writer.Write(points != null ? points.Length : 0);
                    if (points != null) foreach (float v in points) writer.Write(v);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private const byte HeightFlag = 0x80, ExtraFlag = 0x40;   // 0.2.1375: only circles with a drawn outline or their own blend carry the extra, so every other circle stays byte for byte as it was
        internal const float MaxBlendWidth = 96f;

        // A world's paint is hand-made circles; far more than this means a corrupt file or a forged message.
        private const int MaxCircles = 100000;

        private static List<Circle> Unpack(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<Circle>();
            using (var reader = new BinaryReader(new MemoryStream(data))) return ReadCircles(reader);
        }

        private static List<Circle> ReadCircles(BinaryReader reader)
        {
            var circles = new List<Circle>();
            int count = FatNetGuard.Count(reader.ReadInt32(), MaxCircles, "biome paint circles");
            for (int i = 0; i < count; i++)
            {
                uint id = reader.ReadUInt32();
                float x = reader.ReadSingle(), z = reader.ReadSingle(), r = reader.ReadSingle();
                byte brush = reader.ReadByte();
                var circle = new Circle
                {
                    Id = id,
                    X = x,
                    Z = z,
                    R = r,
                    Brush = (Brush)(brush & ~(HeightFlag | ExtraFlag)),
                    Height = (brush & HeightFlag) != 0,
                    Ground = (Heightmap.Biome)reader.ReadInt32(),
                    Blend = reader.ReadString(),
                    BlendWidth = -1f,
                };
                if ((brush & ExtraFlag) != 0)
                {
                    circle.HasExtra = true;
                    circle.BlendWidth = reader.ReadSingle();
                    int rawFloats = reader.ReadInt32();
                    if (rawFloats == -1)
                    {
                        circle.Mask = new PaintMapMask(reader.ReadInt32(), reader.ReadInt32());
                        circles.Add(circle);
                        continue;
                    }
                    int floats = FatNetGuard.Count(rawFloats, PaintOutline.MaxPoints * 2, "biome paint outline");
                    if (floats > 0)
                    {
                        var points = new float[floats];
                        for (int k = 0; k < floats; k++) points[k] = reader.ReadSingle();
                        if (PaintOutline.TryCreate(points, out var outline, out _, out _, out _, out string why)) circle.Outline = outline;
                        else { FiresLogger.LogWarning($"[BiomePaint] a saved outline could not be used: {why}"); circle.R = 0f; }
                    }
                }
                circles.Add(circle);
            }
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
                rpc.Register<ZPackage>(RpcHeightBand, RPC_HeightBand);
                s_registeredOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[BiomePaint] RPC register failed: {ex.Message}"); }
        }

        // Every frame from zone mode's tick, in any world (the Chaos Hammer paints anywhere).
        internal static void Tick()
        {
            if (ZNet.instance == null || ZNet.World == null) return;
            EnsureWorld();
            EnsureContentArea();   // painted circles are super flat content areas (every peer)
            FatBiomeMapTool.Ensure();   // 0.2.1393: FAT's own Biome button on the large map
            EnsureRpcs();
            HookSeasons();
            TickCustomBiomes();
            long t = ZoneUpdateTimes.Now();   // 0.2.1089: the [ZoneUpdate] line's BiomePaint stages
            TickTerrainRefresh();
            t = ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.BiomeRefresh, t);
            TickDeepRecolour();   // 0.2.1063: re-colour deep ground after a paint change
            t = ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.BiomeDeepRecolour, t);
            TickDeepCells();      // 0.2.1086: deep-ground cell cache life + budget re-pokes
            t = ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.BiomeDeepCells, t);
            LogCounts();
            if (ZNet.instance.IsServer())
            {
                SettleLocations();
                PlacePending();
                GrowPending();
                ProcessRegenQueue();
                RunJob();
                FlushStroke(false);
                ZoneUpdateTimes.Add(ZoneUpdateTimes.Cause.BiomeServer, t);
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
            SendHeightBand(target);
            var pkg = new ZPackage();
            pkg.Write(Pack(s_circles));
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcList, pkg);
        }

        private static void RPC_List(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (!FatNetGuard.FromServer(sender, "biome paint list")) return;
            EnsureWorld();
            var circles = Unpack(pkg.ReadByteArray());
            s_circles.Clear();
            s_circles.AddRange(circles);
            Install(circles);
            FiresLogger.LogDebug($"[BiomePaint] the server's paint: {circles.Count} circle(s).");
            ScheduleDeepRecolour();   // 0.2.1063: the first builds ran before this snapshot; re-colour once it is in
        }

        internal static void Reset()
        {
            s_world = null;
            s_circles.Clear();
            s_holdMap = new Dictionary<uint, byte[]>();
            s_snapshot = Snapshot.Empty;
            if (s_hash != 0u) BiomePaintVersion.Bump();
            s_hash = 0u;
            s_asked = 0f;
            s_pending.Clear();
            ResetLocations();
        }
    }
}

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1058 ([seasons]' snow diag row c): a counter that ticks on every paint change (a new circle set, or the world's paint
    // cleared), so a per-zone cache keyed on painted ground (Core's snow base, FAT's deep-snow grid) knows when to drop.
    // Read through FatApi.PaintVersion (add-only).
    internal static class BiomePaintVersion
    {
        private static int s_version;

        internal static int Current => System.Threading.Volatile.Read(ref s_version);

        internal static void Bump()
        {
            int v = System.Threading.Interlocked.Increment(ref s_version);
            FiresLogger.LogDebug($"[BiomePaint] paint version -> {v}");
        }
    }
}
