using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // An AREA in a file (Fire, 2026-09-22 and 2026-09-27, Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md "Save area"): everything
    // inside an upright cylinder - a radius round a centre, between a bottom and a top height - so a sky island cluster
    // or a patch of land can be picked up whole. It holds the land as whole-metre voxel samples (the rest of the box
    // marked "keep"), the ground's paint in every column inside, and every object inside, builds included. Saved as
    // FiresWorldGen\areas\<name>.areabp.
    //
    // Placing lays it down with its centre at the spot the admin looks at, at the heights it was saved at, and touches
    // nothing outside its cylinder. What stood inside is kept first, so the chunk tools' Undo and Redo step it back and
    // forth. Objects are removed only through vanilla's own DestroyZDO (Tools\WORLD_LOSS_20260927_RPGWORLD_CHUNKS.md).
    internal static class ZoneModeAreaBlueprint
    {
        internal const string Extension = ".areabp";
        private const string FolderName = "areas";
        private const uint FileMagic = 0x42414246u;
        private const byte FileFormat = 1;
        internal const int MinRadius = 2;
        internal const int MaxRadius = 96;
        internal const int MaxSpan = 256;
        private const float BytesPerKilobyte = 1024f;
        private const string CompilerPrefabName = "_TerrainCompiler";

        private static readonly int CompilerPrefabHash = CompilerPrefabName.GetStableHashCode();
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> CompilerPaint =
            AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> CompilerPainted =
            AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly MethodInfo CompilerSave = AccessTools.Method(typeof(TerrainComp), "Save");

        internal struct Area
        {
            internal string World;
            internal long CreatedUtcTicks;
            internal global::Version.World WorldVersion;
            internal int CentreX;
            internal int CentreZ;
            internal int Radius;
            internal int Bottom;
            internal int Top;
            internal int Pieces;
            internal int Objects;
            internal int Portals;

            internal Vector3Int Origin => new Vector3Int(CentreX - Radius, Bottom, CentreZ - Radius);
            internal Vector3Int Size => new Vector3Int(Radius * 2 + 1, Top - Bottom + 1, Radius * 2 + 1);

            internal bool Holds(float x, float y, float z)
                => y >= Bottom && y <= Top && (x - CentreX) * (x - CentreX) + (z - CentreZ) * (z - CentreZ) <= Radius * Radius;

            internal bool HoldsColumn(int x, int z) => (x - CentreX) * (x - CentreX) + (z - CentreZ) * (z - CentreZ) <= Radius * Radius;
        }

        internal static string Folder => Path.Combine(FiresWorldRecords.Folder, FolderName);

        internal static string PathFor(string name) => Path.Combine(Folder, FiresWorldRecords.SafeName(name) + Extension);

        internal static bool IsUsableShape(int radius, int bottom, int top, out string problem)
        {
            problem = null;
            if (radius < MinRadius || radius > MaxRadius) problem = $"The radius must be {MinRadius} to {MaxRadius} m.";
            else if (top <= bottom) problem = "The top must be above the bottom.";
            else if (top - bottom > MaxSpan) problem = $"An area can be at most {MaxSpan} m tall.";
            return problem == null;
        }

        internal static IEnumerable<string> Names()
        {
            if (!Directory.Exists(Folder)) yield break;
            foreach (string path in Directory.GetFiles(Folder, "*" + Extension)) yield return Path.GetFileNameWithoutExtension(path);
        }

        // ── Saving ──────────────────────────────────────────────────────────
        internal static bool TrySave(Vector3 centre, int radius, int bottom, int top, string name, bool overwrite, byte[] snapshot,
            out string report)
        {
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            if (!ZoneModeChunkBlueprint.IsUsableName(name, out report)) return false;
            if (!IsUsableShape(radius, bottom, top, out report)) return false;
            string path = PathFor(name);
            if (!overwrite && File.Exists(path))
            {
                report = $"'{name}' already exists. Tick 'Replace' to save over it.";
                return false;
            }
            var area = Describe(Mathf.RoundToInt(centre.x), Mathf.RoundToInt(centre.z), radius, bottom, top);
            if (!TryCapture(area, path, out area, out report)) return false;
            ZoneModeBlueprintIcons.Delete(path);
            bool snapped = ZoneModeBlueprintIcons.IsAcceptableSnapshot(snapshot);
            if (snapped) ZoneModeBlueprintIcons.Write(path, ZoneModeBlueprintIcons.Kind.Snapshot, snapshot);
            report = $"Saved area '{name}': {radius} m round ({area.CentreX},{area.CentreZ}), y {bottom} to {top}, "
                + $"{area.Objects} object(s) ({area.Pieces} built piece(s)), {new FileInfo(path).Length / BytesPerKilobyte:0} KB"
                + (snapped ? ", pictured by a snapshot." : ".");
            return true;
        }

        private static Area Describe(int x, int z, int radius, int bottom, int top) => new Area
        {
            World = ZNet.World?.m_name ?? "world",
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            WorldVersion = global::Version.c_WorldVersion,
            CentreX = x,
            CentreZ = z,
            Radius = radius,
            Bottom = bottom,
            Top = top,
        };

        private static bool TryCapture(Area shape, string path, out Area area, out string problem)
        {
            area = shape;
            problem = null;
            var inside = area;
            var grid = VoxelWorld.CaptureBox(area.Origin, area.Size, (x, y, z) => inside.Holds(x, y, z));
            var paint = ReadPaint(area);
            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            CollectObjects(area, objects, portals);
            area.Objects = objects.Count;
            area.Portals = portals.Count;
            area.Pieces = 0;
            foreach (var zdo in objects) if (ZoneModeBuilds.IsBuilt(zdo)) area.Pieces++;
            foreach (var zdo in portals) if (ZoneModeBuilds.IsBuilt(zdo)) area.Pieces++;
            objects.AddRange(portals);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Write(path, area, grid, paint, objects);
            }
            catch (Exception ex)
            {
                problem = $"could not be written ({ex.GetType().Name}: {ex.Message}).";
                return false;
            }
            return true;
        }

        // Every persistent object inside, except the land's own bookkeeping: voxel anchors (the land travels in the
        // samples) and vanilla's terrain compilers (the paint travels per column). Portals stay a list of their own:
        // they go back into vanilla's portal list, after everything else.
        private static void CollectObjects(Area area, List<ZDO> objects, List<ZDO> portals)
        {
            var lo = ZoneSystem.GetZone(new Vector3(area.CentreX - area.Radius, 0f, area.CentreZ - area.Radius));
            var hi = ZoneSystem.GetZone(new Vector3(area.CentreX + area.Radius, 0f, area.CentreZ + area.Radius));
            var inObjects = new List<ZDO>();
            var inPortals = new List<ZDO>();
            for (int zy = lo.y; zy <= hi.y; zy++)
                for (int zx = lo.x; zx <= hi.x; zx++)
                    ZoneModePark.CollectZoneObjects(new Vector2s(zx, zy), inObjects, inPortals);
            var seen = new HashSet<ZDOID>();
            Keep(area, inPortals, portals, seen);
            Keep(area, inObjects, objects, seen);
        }

        private static void Keep(Area area, List<ZDO> from, List<ZDO> into, HashSet<ZDOID> seen)
        {
            foreach (var zdo in from)
            {
                int prefab = zdo.GetPrefab();
                if (prefab == VoxelAnchorPrefab.PrefabHash || prefab == CompilerPrefabHash) continue;
                Vector3 at = zdo.GetPosition();
                if (area.Holds(at.x, at.y, at.z) && seen.Add(zdo.m_uid)) into.Add(zdo);
            }
        }

        private static Color32[] ReadPaint(Area area)
        {
            var size = area.Size;
            var paint = new Color32[size.x * size.z];
            for (int gz = 0; gz < size.z; gz++)
                for (int gx = 0; gx < size.x; gx++)
                {
                    int x = area.CentreX - area.Radius + gx, z = area.CentreZ - area.Radius + gz;
                    if (!area.HoldsColumn(x, z)) continue;
                    var heightmap = Heightmap.FindHeightmap(new Vector3(x, 0f, z));
                    if (heightmap == null || heightmap.GetPaintMask() == null) continue;
                    heightmap.WorldToVertexMask(new Vector3(x, 0f, z), out int vx, out int vy);
                    paint[gz * size.x + gx] = heightmap.GetPaintMask(vx, vy);
                }
            return paint;
        }

        // ── Placing, and the kept copy undo needs ────────────────────────────
        internal static bool TryPlace(string name, Vector3 target, out string report)
        {
            if (!ZoneModeChunkBlueprint.ServerReady(out report)) return false;
            if (!FiresVoxelConfig.StreamerRuns)
            {
                report = "This world has no voxel terrain, so an area's land cannot be laid down here.";
                return false;
            }
            string path = PathFor(name);
            if (!File.Exists(path))
            {
                report = $"There is no area blueprint called '{name}'.";
                return false;
            }
            if (!TryApply(path, Mathf.RoundToInt(target.x), Mathf.RoundToInt(target.z), $"area '{name}'", out string kept, out report))
                return false;
            ZoneModeChunkBlueprint.RecordAreaPlacement(kept, $"area '{name}'");
            return true;
        }

        // Undo and redo: a kept area goes back exactly where it was taken, and what stands there now is kept in turn.
        internal static bool TryApplyKept(string path, string what, out string keptNow, out string report)
        {
            keptNow = null;
            Area area;
            try { area = ReadHead(path); }
            catch (Exception ex)
            {
                report = $"{what} could not be read back ({ex.GetType().Name}: {ex.Message}).";
                return false;
            }
            return TryApply(path, area.CentreX, area.CentreZ, what, out keptNow, out report);
        }

        private static bool TryApply(string path, int centreX, int centreZ, string what, out string kept, out string report)
        {
            kept = null;
            Area saved;
            sbyte[] grid;
            Color32[] paint;
            var objects = new List<ZDO>();
            try { saved = Read(path, out grid, out paint, objects); }
            catch (Exception ex)
            {
                foreach (var zdo in objects) ZDOPool.Release(zdo);
                report = $"{what} could not be read ({ex.GetType().Name}: {ex.Message}). Nothing was placed.";
                return false;
            }

            var target = saved;
            target.CentreX = centreX;
            target.CentreZ = centreZ;
            kept = ZoneModeChunkBlueprint.NewUndoPath(Extension);
            if (!TryCapture(Describe(target.CentreX, target.CentreZ, target.Radius, target.Bottom, target.Top), kept, out _, out string problem))
            {
                FiresLogger.LogWarning($"[FiresZones] what stood under {what} could not be kept ({problem}); this placement cannot be undone.");
                kept = null;
            }

            int worldBefore = ZDOMan.instance.NrOfObjects();
            int removed = ClearObjects(target);
            int touched = VoxelWorld.StampBox(grid, target.Origin, target.Size);
            WritePaint(target, paint);
            var shift = new Vector3(target.CentreX - saved.CentreX, 0f, target.CentreZ - saved.CentreZ);
            if (shift.sqrMagnitude > 0f)
                foreach (var zdo in objects) zdo.SetPosition(zdo.GetPosition() + shift);
            ZoneModePark.AdoptObjects(objects, saved.Objects);
            FiresLogger.LogInfo($"[FiresZones] {what} counts at ({target.CentreX},{target.CentreZ}): world {worldBefore:N0} -> "
                + $"{ZDOMan.instance.NrOfObjects():N0} object(s), {removed:N0} removed, {objects.Count:N0} placed, {touched} chunk(s) stamped.");
            report = $"Placed {what} at ({target.CentreX},{target.CentreZ}), y {target.Bottom} to {target.Top}: {touched} chunk(s) of land, "
                + $"{objects.Count} object(s)" + (removed > 0 ? $", {removed} that stood here removed" : "") + ".";
            return touched > 0 || objects.Count > 0;
        }

        // What stands inside goes, the way vanilla removes an object, so every peer drops it. The chunk world's
        // protected pieces stay.
        private static int ClearObjects(Area area)
        {
            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            CollectObjects(area, objects, portals);
            objects.AddRange(portals);
            long self = ZDOMan.GetSessionID();
            int removed = 0;
            foreach (var zdo in objects)
            {
                if (ZoneModeChunkMachines.IsProtected(zdo)) continue;
                if (!zdo.IsOwner()) zdo.SetOwner(self);
                ZDOMan.instance.DestroyZDO(zdo);
                removed++;
            }
            return removed;
        }

        // Paint has no clamp, so vanilla's own terrain edits carry it; every peer's TerrainComp reloads from its ZDO.
        private static void WritePaint(Area area, Color32[] paint)
        {
            var size = area.Size;
            var touched = new HashSet<Heightmap>();
            for (int gz = 0; gz < size.z; gz++)
                for (int gx = 0; gx < size.x; gx++)
                {
                    int x = area.CentreX - area.Radius + gx, z = area.CentreZ - area.Radius + gz;
                    if (!area.HoldsColumn(x, z)) continue;
                    var point = new Vector3(x, 0f, z);
                    var heightmap = Heightmap.FindHeightmap(point);
                    var compiler = heightmap != null ? heightmap.GetAndCreateTerrainCompiler() : null;
                    if (compiler == null) continue;
                    var painted = CompilerPainted(compiler);
                    var colours = CompilerPaint(compiler);
                    if (painted == null || colours == null) continue;
                    heightmap.WorldToVertexMask(point, out int vx, out int vy);
                    int index = vy * (heightmap.m_width + 1) + vx;
                    if (index < 0 || index >= painted.Length) continue;
                    painted[index] = true;
                    colours[index] = paint[gz * size.x + gx];
                    touched.Add(heightmap);
                }
            foreach (var heightmap in touched)
            {
                var compiler = heightmap.GetAndCreateTerrainCompiler();
                var nview = compiler != null ? compiler.GetComponent<ZNetView>() : null;
                if (nview != null && nview.IsValid() && !nview.IsOwner()) nview.ClaimOwnership();
                if (compiler != null) CompilerSave.Invoke(compiler, null);
                heightmap.Poke(0, true);
            }
        }

        // ── File ────────────────────────────────────────────────────────────
        private static void Write(string path, Area area, sbyte[] grid, Color32[] paint, List<ZDO> objects)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                var pkg = new ZPackage();
                pkg.SetWriter(writer);
                pkg.Write(FileMagic);
                pkg.Write(FileFormat);
                pkg.Write(area.World);
                pkg.Write((short)area.WorldVersion);
                pkg.Write(area.CreatedUtcTicks);
                pkg.Write(area.CentreX);
                pkg.Write(area.CentreZ);
                pkg.Write(area.Radius);
                pkg.Write(area.Bottom);
                pkg.Write(area.Top);
                pkg.Write(area.Pieces);
                pkg.Write(area.Objects);
                pkg.Write(area.Portals);
                pkg.Write(Pack(grid));
                foreach (var colour in paint)
                {
                    pkg.Write(colour.r);
                    pkg.Write(colour.g);
                    pkg.Write(colour.b);
                    pkg.Write(colour.a);
                }
                ZDOExtraData.PrepareSave();
                try { foreach (var zdo in objects) zdo.Save(pkg); }
                finally { ZDOExtraData.ClearSave(); }
            }
        }

        private static Area ReadHead(ZPackage pkg)
        {
            if (pkg.ReadUInt() != FileMagic) throw new InvalidDataException("not an area blueprint");
            byte format = pkg.ReadByte();
            if (format != FileFormat) throw new InvalidDataException($"area format {format}, this build reads {FileFormat}");
            return new Area
            {
                World = pkg.ReadString(),
                WorldVersion = (global::Version.World)pkg.ReadShort(),
                CreatedUtcTicks = pkg.ReadLong(),
                CentreX = pkg.ReadInt(),
                CentreZ = pkg.ReadInt(),
                Radius = pkg.ReadInt(),
                Bottom = pkg.ReadInt(),
                Top = pkg.ReadInt(),
                Pieces = pkg.ReadInt(),
                Objects = pkg.ReadInt(),
                Portals = pkg.ReadInt(),
            };
        }

        private static Area ReadHead(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                var pkg = new ZPackage();
                pkg.SetReader(reader);
                return ReadHead(pkg);
            }
        }

        private static Area Read(string path, out sbyte[] grid, out Color32[] paint, List<ZDO> objects)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                var pkg = new ZPackage();
                pkg.SetReader(reader);
                var area = ReadHead(pkg);
                var size = area.Size;
                grid = Unpack(pkg.ReadByteArray(), size.x * size.y * size.z);
                paint = new Color32[size.x * size.z];
                for (int i = 0; i < paint.Length; i++)
                    paint[i] = new Color32(pkg.ReadByte(), pkg.ReadByte(), pkg.ReadByte(), pkg.ReadByte());
                for (int i = 0; i < area.Objects + area.Portals; i++)
                {
                    var zdo = ZDOPool.Create();
                    objects.Add(zdo);
                    zdo.Load(pkg, area.WorldVersion);
                }
                return area;
            }
        }

        // One line per saved area, for the console list.
        internal static IEnumerable<string> List()
        {
            foreach (string name in Names())
            {
                string path = PathFor(name);
                string line;
                try
                {
                    var area = ReadHead(path);
                    line = $"{name}: {area.Radius} m round ({area.CentreX},{area.CentreZ}) of '{area.World}', y {area.Bottom} to {area.Top}, "
                        + $"{area.Objects} object(s), {area.Pieces} built piece(s), {new FileInfo(path).Length / BytesPerKilobyte:0} KB";
                }
                catch (Exception ex) { line = $"{name}: unreadable ({ex.Message})"; }
                yield return line;
            }
        }

        private static byte[] Pack(sbyte[] samples)
        {
            var bytes = new byte[samples.Length];
            Buffer.BlockCopy(samples, 0, bytes, 0, samples.Length);
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress)) deflate.Write(bytes, 0, bytes.Length);
                return output.ToArray();
            }
        }

        private static sbyte[] Unpack(byte[] packed, int count)
        {
            var bytes = new byte[count];
            using (var input = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                int read = 0;
                while (read < count)
                {
                    int got = input.Read(bytes, read, count - read);
                    if (got <= 0) throw new InvalidDataException($"the land ends after {read} of {count} samples");
                    read += got;
                }
            }
            var samples = new sbyte[count];
            Buffer.BlockCopy(bytes, 0, samples, 0, count);
            return samples;
        }
    }
}
