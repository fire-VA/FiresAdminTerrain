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
    // A whole chunk in a file: the land it stands on as a VOLUME - every voxel of the zone from below its caves to
    // above its surface, so caves and overhangs come with it - its paint and biome, and every object in it, builds
    // included. Skylands hanging above the zone are left where they are. A blueprint is self-contained, so it outlives
    // the world it came from and can be handed to someone else. Saved as FiresWorldGen\chunks\<name>.chunkbp.
    //
    // Placing one drops it as it was: its own heights, nothing blended into the zones around it, and a build that ran
    // into the next zone cut at the border (Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md). Every placement first saves the zone
    // it replaces, so an admin can undo and redo.
    internal static class ZoneModeChunkBlueprint
    {
        internal const string Extension = ".chunkbp";
        private const string FolderName = "chunks";
        private const string UndoFolderName = "_undo";
        private const uint FileMagic = 0x50424346u;
        private const byte FileFormat = 2;
        private const int Pitch = (int)ZoneOpenSet.ZoneSize + 1;
        private const int MinNameLength = 2;
        private const float BytesPerKilobyte = 1024f;
        private const float SurfaceMargin = 12f;
        private const float CaveReachFallback = 48f;
        private const float SkylandGap = 8f;
        private const float RangeStep = 4f;
        private const int UndoDepth = 16;
        internal const string WorldSavingMessage = "The world is being saved right now. Try again in a moment.";
        internal static string RefusedLoadMessage => $"This world's load was refused ({FiresWorldLoadGuard.Reason}), so nothing in it is changed.";

        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> CompilerPaint =
            AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> CompilerPainted =
            AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly MethodInfo CompilerSave = AccessTools.Method(typeof(TerrainComp), "Save");
        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZonesRef =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");

        // One placement that can be stepped back: a whole zone, or an area (ZoneModeAreaBlueprint), whose kept file knows
        // its own place. A skyland (ZoneModeSkylandBlueprint) steps two ways: a placed one (SkylandId) is taken back into a
        // kept file, and a kept one (IsSkyland) is raised again from it. A biome paint (BiomePaint) steps the same way: a
        // laid circle (PaintId) is taken back into its kept record (Paint), which is laid again.
        private struct Step
        {
            internal Vector2s Zone;
            internal string Path;
            internal string What;
            internal bool WasOpen;
            internal bool IsArea;
            internal bool IsSkyland;
            internal uint SkylandId;
            internal uint PaintId;
            internal byte[] Paint;
        }

        private static readonly List<Step> s_undo = new List<Step>();
        private static readonly List<Step> s_redo = new List<Step>();
        private static int s_stepCount;

        internal struct Blueprint
        {
            internal string World;
            internal int Seed;
            internal Vector2s Zone;
            internal global::Version.World WorldVersion;
            internal long CreatedUtcTicks;
            internal float LowGround;
            internal float HighGround;
            internal int ChunkMinY;
            internal int ChunkCountY;
            internal int Pieces;
            internal int Objects;
            internal int Portals;
            internal sbyte[] Volume;
            internal Color32[] Paint;
            internal byte[] Biome;
        }

        internal static string Folder => Path.Combine(FiresWorldRecords.Folder, FolderName);

        private static string UndoFolder => Path.Combine(Folder, UndoFolderName);

        internal static string PathFor(string name) => Path.Combine(Folder, FiresWorldRecords.SafeName(name) + Extension);

        internal static bool IsUsableName(string name, out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(name) || name.Trim().Length < MinNameLength)
                problem = $"A blueprint name needs at least {MinNameLength} characters.";
            else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Trim() != name)
                problem = $"'{name}' cannot be a blueprint name.";
            return problem == null;
        }

        // A world's undo history is that world's: leaving takes the kept zones with it.
        internal static void Reset()
        {
            foreach (var step in s_undo) TryDelete(step.Path);
            foreach (var step in s_redo) TryDelete(step.Path);
            s_undo.Clear();
            s_redo.Clear();
            s_stepCount = 0;
        }

        // ── Saving ──────────────────────────────────────────────────────────
        // `snapshot` is the saving client's own render of the chunk (ZoneModeBlueprintIcons.TakeSnapshot), or null.
        internal static bool TrySave(Vector2s zone, string name, bool overwrite, byte[] snapshot, out string report)
        {
            if (!ServerReady(out report)) return false;
            if (!IsUsableName(name, out report)) return false;
            string path = PathFor(name);
            if (!overwrite && File.Exists(path))
            {
                report = $"'{name}' already exists. Pass 'overwrite' to replace it.";
                return false;
            }
            var builtAt = new List<Vector2>();
            if (!TryCapture(zone, path, out var blueprint, out report, builtAt)) return false;
            ZoneModeBlueprintIcons.Delete(path);
            WriteMap(path, blueprint, builtAt);
            bool snapped = ZoneModeBlueprintIcons.IsAcceptableSnapshot(snapshot);
            if (snapped) ZoneModeBlueprintIcons.Write(path, ZoneModeBlueprintIcons.Kind.Snapshot, snapshot);
            report = $"Saved '{name}': zone ({zone.x},{zone.y}) of '{blueprint.World}', ground {blueprint.LowGround:0.#} to "
                + $"{blueprint.HighGround:0.#} m, {blueprint.ChunkCountY * VoxelWorld.ZoneChunksPerAxis * VoxelWorld.ZoneChunksPerAxis} "
                + $"chunk(s) of land with everything carved into them, {blueprint.Objects + blueprint.Portals} object(s) "
                + $"({blueprint.Pieces} built piece(s)), {new FileInfo(path).Length / BytesPerKilobyte:0} KB, pictured by "
                + (snapped ? "a snapshot" : "its map") + ". Skylands over the zone stay where they are.";
            return true;
        }

        // A blueprint saved before pictures existed gets its map the first time its name is listed.
        internal static void EnsureMap(string path)
        {
            if (!File.Exists(path) || ZoneModeBlueprintIcons.IsCurrent(path, ZoneModeBlueprintIcons.Kind.Map)) return;
            var objects = new List<ZDO>();
            try
            {
                var blueprint = Read(path, objects);
                var builtAt = new List<Vector2>();
                AddBuiltPositions(blueprint.Zone, objects, builtAt);
                WriteMap(path, blueprint, builtAt);
                FiresLogger.LogInfo($"[FiresZones] drew the map of {Path.GetFileName(path)} ({builtAt.Count} built piece(s) marked).");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] no map for {Path.GetFileName(path)} ({ex.GetType().Name}: {ex.Message}); it wears the hammer's icon.");
            }
            finally
            {
                foreach (var zdo in objects) ZDOPool.Release(zdo);
            }
        }

        private static void WriteMap(string path, Blueprint blueprint, List<Vector2> builtAt)
            => ZoneModeBlueprintIcons.Write(path, ZoneModeBlueprintIcons.Kind.Map, ZoneModeBlueprintIcons.DrawMap(blueprint.Volume,
                blueprint.ChunkCountY, blueprint.ChunkMinY, blueprint.Paint, blueprint.Biome, Pitch, builtAt));

        // Built pieces as metre positions from the zone's minimum corner, for the map's marks.
        private static void AddBuiltPositions(Vector2s zone, List<ZDO> zdos, List<Vector2> into)
        {
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            float minX = centre.x - ZoneOpenSet.ZoneSize * 0.5f, minZ = centre.z - ZoneOpenSet.ZoneSize * 0.5f;
            foreach (var zdo in zdos)
            {
                if (!ZoneModeBuilds.IsBuilt(zdo)) continue;
                Vector3 at = zdo.GetPosition();
                into.Add(new Vector2(at.x - minX, at.z - minZ));
            }
        }

        private static bool TryCapture(Vector2s zone, string path, out Blueprint blueprint, out string problem,
            List<Vector2> builtAt = null)
        {
            blueprint = new Blueprint
            {
                World = ZNet.World?.m_name ?? "world",
                Seed = WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0,
                Zone = zone,
                WorldVersion = global::Version.c_WorldVersion,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
            };
            problem = null;
            if (!TryGroundRange(zone, out blueprint.LowGround, out blueprint.HighGround))
            {
                problem = $"zone ({zone.x},{zone.y}) has no terrain here yet: stand in it, or wait for it to build, and save again.";
                return false;
            }

            float caveReach = FiresVoxelConfig.CaveMaxDepth != null ? FiresVoxelConfig.CaveMaxDepth.Value : CaveReachFallback;
            float floorY = blueprint.LowGround - caveReach - SurfaceMargin;
            float ceilY = SkylandFloor(zone, blueprint.HighGround + SurfaceMargin);
            blueprint.Volume = VoxelWorld.CaptureZoneVolume(zone, floorY, ceilY, out blueprint.ChunkMinY, out blueprint.ChunkCountY);

            blueprint.Paint = new Color32[Pitch * Pitch];
            blueprint.Biome = new byte[Pitch * Pitch];
            ReadSurface(zone, blueprint.Paint, blueprint.Biome);

            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            ZoneModePark.CollectZoneObjects(zone, objects, portals);
            blueprint.Objects = objects.Count;
            blueprint.Portals = portals.Count;
            blueprint.Pieces = CountBuilt(objects) + CountBuilt(portals);
            if (builtAt != null)
            {
                AddBuiltPositions(zone, objects, builtAt);
                AddBuiltPositions(zone, portals, builtAt);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Write(path, blueprint, objects, portals);
            }
            catch (Exception ex)
            {
                problem = $"could not be written ({ex.GetType().Name}: {ex.Message}).";
                return false;
            }
            return true;
        }

        // A zone's ceiling stops short of any skyland hanging over it: those are their own thing, not this chunk's land.
        private static float SkylandFloor(Vector2s zone, float wanted)
        {
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            float half = ZoneOpenSet.ZoneSize * 0.5f;
            return FiresSkyIslands.TryGetBand(centre.x - half, centre.z - half, centre.x + half, centre.z + half, out float botY, out _)
                ? Mathf.Min(wanted, botY - SkylandGap)
                : wanted;
        }

        private static bool TryGroundRange(Vector2s zone, out float low, out float high)
        {
            low = float.MaxValue;
            high = float.MinValue;
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            float half = ZoneOpenSet.ZoneSize * 0.5f;
            for (float oz = -half; oz <= half; oz += RangeStep)
            {
                for (float ox = -half; ox <= half; ox += RangeStep)
                {
                    if (!VoxelZoneStreamer.TryGroundHeight(centre.x + ox, centre.z + oz, out float y)) continue;
                    low = Mathf.Min(low, y);
                    high = Mathf.Max(high, y);
                }
            }
            return high > float.MinValue;
        }

        private static void ReadSurface(Vector2s zone, Color32[] paint, byte[] biome)
        {
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            float minX = centre.x - ZoneOpenSet.ZoneSize * 0.5f, minZ = centre.z - ZoneOpenSet.ZoneSize * 0.5f;
            for (int j = 0; j < Pitch; j++)
            {
                for (int i = 0; i < Pitch; i++)
                {
                    float wx = minX + i, wz = minZ + j;
                    int index = j * Pitch + i;
                    paint[index] = PaintAt(new Vector3(wx, 0f, wz));
                    biome[index] = GenWaterField.BiomeCodeAt(wx, wz);
                }
            }
        }

        private static Color32 PaintAt(Vector3 point)
        {
            var heightmap = Heightmap.FindHeightmap(point);
            if (heightmap == null || heightmap.GetPaintMask() == null) return new Color32(0, 0, 0, 0);
            heightmap.WorldToVertexMask(point, out int x, out int y);
            return heightmap.GetPaintMask(x, y);
        }

        private static int CountBuilt(List<ZDO> zdos)
        {
            int built = 0;
            foreach (var zdo in zdos)
                if (ZoneModeBuilds.IsBuilt(zdo)) built++;
            return built;
        }

        // ── Placing, undo and redo ──────────────────────────────────────────
        internal static bool TryPlace(Vector2s zone, string name, out string report)
        {
            if (!ServerReady(out report)) return false;
            if (!FiresVoxelConfig.StreamerRuns)
            {
                report = "This world has no voxel terrain, so a saved chunk's land cannot be laid down here.";
                return false;
            }
            string path = PathFor(name);
            if (!File.Exists(path))
            {
                report = $"There is no chunk blueprint called '{name}'.";
                return false;
            }
            if (!Apply(path, zone, $"'{name}'", s_undo, out report)) return false;
            foreach (var step in s_redo) TryDelete(step.Path);
            s_redo.Clear();
            return true;
        }

        internal static bool TryUndo(out string report) => TryStep(s_undo, s_redo, "Undid", out report);

        internal static bool TryRedo(out string report) => TryStep(s_redo, s_undo, "Redid", out report);

        // An area placement joins the same history as chunk placements, so the hammer's Undo and Redo cover both.
        internal static void RecordAreaPlacement(string keptPath, string what)
        {
            if (keptPath != null) Keep(s_undo, new Step { Path = keptPath, What = what, IsArea = true });
            foreach (var step in s_redo) TryDelete(step.Path);
            s_redo.Clear();
        }

        // A skyland placement joins the same history, so Undo takes the skyland back and Redo raises it again.
        internal static void RecordSkylandPlacement(uint id, string what)
        {
            Keep(s_undo, new Step { SkylandId = id, What = what });
            foreach (var step in s_redo) TryDelete(step.Path);
            s_redo.Clear();
        }

        // A biome paint joins the same history (Fire's popup: Undo/Redo cover paint).
        internal static void RecordPaint(uint id, string what)
        {
            Keep(s_undo, new Step { PaintId = id, What = what });
            foreach (var step in s_redo) TryDelete(step.Path);
            s_redo.Clear();
        }

        internal static string NewUndoPath(string extension)
        {
            Directory.CreateDirectory(UndoFolder);
            return Path.Combine(UndoFolder, $"{++s_stepCount:00}_area{extension}");
        }

        private static void Keep(List<Step> into, Step step)
        {
            into.Add(step);
            while (into.Count > UndoDepth)
            {
                TryDelete(into[0].Path);
                into.RemoveAt(0);
            }
        }

        // Stepping back and forward is symmetric: what the zone holds now goes onto the other stack before the saved
        // state is laid down, so undo and redo are the same move in opposite directions.
        private static bool TryStep(List<Step> from, List<Step> onto, string verb, out string report)
        {
            if (!ServerReady(out report)) return false;
            if (from.Count == 0)
            {
                report = from == s_undo ? "Nothing to undo." : "Nothing to redo.";
                return false;
            }
            var step = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            if (step.PaintId != 0u)
            {
                if (!BiomePaint.TryTakeBack(step.PaintId, out byte[] kept, out string takenReport))
                {
                    report = takenReport;
                    return false;
                }
                Keep(onto, new Step { Paint = kept, What = step.What });
                report = $"{verb} {step.What}. {takenReport}";
                return true;
            }
            if (step.Paint != null)
            {
                if (!BiomePaint.TryPutBack(step.Paint, out uint laid, out string laidReport))
                {
                    report = laidReport;
                    return false;
                }
                Keep(onto, new Step { PaintId = laid, What = step.What });
                report = $"{verb} {step.What}. {laidReport}";
                return true;
            }
            if (step.SkylandId != 0u)
            {
                if (!ZoneModeSkylandBlueprint.TryTakeBack(step.SkylandId, step.What, out string kept, out string takenReport))
                {
                    report = takenReport;
                    return false;
                }
                Keep(onto, new Step { Path = kept, What = step.What, IsSkyland = true });
                report = $"{verb} {step.What}. {takenReport}";
                return true;
            }
            if (step.IsSkyland)
            {
                if (!ZoneModeSkylandBlueprint.TryPutBack(step.Path, step.What, out uint raised, out string raisedReport))
                {
                    report = raisedReport;
                    return false;
                }
                Keep(onto, new Step { SkylandId = raised, What = step.What });
                TryDelete(step.Path);
                report = $"{verb} {step.What}. {raisedReport}";
                return true;
            }
            if (step.IsArea)
            {
                if (!ZoneModeAreaBlueprint.TryApplyKept(step.Path, step.What, out string keptNow, out string areaReport))
                {
                    report = areaReport;
                    return false;
                }
                if (keptNow != null) Keep(onto, new Step { Path = keptNow, What = step.What, IsArea = true });
                TryDelete(step.Path);
                report = $"{verb} {step.What}. {areaReport}";
                return true;
            }
            if (!Apply(step.Path, step.Zone, step.What, onto, out string applied))
            {
                report = applied;
                return false;
            }
            TryDelete(step.Path);
            report = $"{verb} {step.What} on zone ({step.Zone.x},{step.Zone.y}). {applied}";
            // A zone that was closed sea before the placement is closed again, so the chunk is hidden and a different
            // one can be laid there.
            if (!step.WasOpen && ZoneModeRegistry.IsOpen(step.Zone))
            {
                ZoneModeOpening.Close(new[] { step.Zone }, $"{step.What} was taken back");
                report += " The chunk is hidden again.";
            }
            return true;
        }

        // The whole apply path: keep what is there on `keepInto`, clear the zone, lay the saved volume, paint it, and
        // put the objects back.
        private static bool Apply(string path, Vector2s zone, string what, List<Step> keepInto, out string report)
        {
            Blueprint blueprint;
            var objects = new List<ZDO>();
            try { blueprint = Read(path, objects); }
            catch (Exception ex)
            {
                foreach (var zdo in objects) ZDOPool.Release(zdo);
                report = $"{what} could not be read ({ex.GetType().Name}: {ex.Message}). Nothing was placed.";
                return false;
            }

            string kept = Snapshot(zone, what);
            if (keepInto != null && kept != null)
            {
                keepInto.Add(new Step { Zone = zone, Path = kept, What = what, WasOpen = ZoneModeRegistry.IsOpen(zone) });
                while (keepInto.Count > UndoDepth)
                {
                    TryDelete(keepInto[0].Path);
                    keepInto.RemoveAt(0);
                }
            }

            int cleared = ZoneModePark.ClearZoneObjects(zone);
            float clearToY = blueprint.HighGround, fillFromY = blueprint.LowGround;
            if (TryGroundRange(zone, out float low, out float high))
            {
                clearToY = Mathf.Max(clearToY, high) + SurfaceMargin;
                fillFromY = Mathf.Min(fillFromY, low) - SurfaceMargin;
            }
            string stamped = VoxelWorld.StampZoneVolume(zone, blueprint.Volume, blueprint.ChunkMinY, blueprint.ChunkCountY,
                clearToY, fillFromY, out int touched);
            PaintZone(zone, blueprint.Paint);

            Vector3 from = ZoneSystem.GetZonePos(blueprint.Zone), to = ZoneSystem.GetZonePos(zone);
            var shift = new Vector3(to.x - from.x, 0f, to.z - from.z);
            if (shift.sqrMagnitude > 0.001f)
                foreach (var zdo in objects) zdo.SetPosition(zdo.GetPosition() + shift);
            ZoneModePark.AdoptObjects(objects, blueprint.Objects);
            GeneratedZonesRef(ZoneSystem.instance)?.Add(zone);

            report = $"{stamped}, {objects.Count} object(s) ({blueprint.Pieces} built piece(s)) from zone "
                + $"({blueprint.Zone.x},{blueprint.Zone.y}) of '{blueprint.World}'"
                + (cleared > 0 ? $", {cleared} object(s) that stood here removed" : "")
                + ". Its land keeps the height it was saved at, so the zones around it may step to meet it.";
            return touched > 0;
        }

        // What the zone holds right now, kept so a placement can be taken back.
        private static string Snapshot(Vector2s zone, string what)
        {
            try
            {
                Directory.CreateDirectory(UndoFolder);
                string path = Path.Combine(UndoFolder, $"{++s_stepCount:00}_{zone.x}_{zone.y}{Extension}");
                if (TryCapture(zone, path, out _, out string problem)) return path;
                FiresLogger.LogWarning($"[FiresZones] zone ({zone.x},{zone.y}) could not be kept before placing {what}: {problem} "
                    + "This placement cannot be undone.");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] zone ({zone.x},{zone.y}) could not be kept before placing {what} "
                    + $"({ex.GetType().Name}: {ex.Message}). This placement cannot be undone.");
            }
            return null;
        }

        private static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { FiresLogger.LogVerbose($"[FiresZones] could not remove {Path.GetFileName(path)}: {ex.Message}"); }
        }

        // Paint has no clamp, so vanilla's own terrain edits carry it exactly; every peer's TerrainComp reloads from
        // the ZDO and pokes its heightmap.
        private static void PaintZone(Vector2s zone, Color32[] paint)
        {
            var heightmap = Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zone));
            var compiler = heightmap != null ? heightmap.GetAndCreateTerrainCompiler() : null;
            if (compiler == null) return;
            var painted = CompilerPainted(compiler);
            var colours = CompilerPaint(compiler);
            if (painted == null || colours == null) return;
            int pitch = heightmap.m_width + 1;
            for (int y = 0; y < pitch && y < Pitch; y++)
            {
                for (int x = 0; x < pitch && x < Pitch; x++)
                {
                    int index = y * pitch + x;
                    if (index >= painted.Length) continue;
                    painted[index] = true;
                    colours[index] = paint[y * Pitch + x];
                }
            }
            var nview = compiler.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid() && !nview.IsOwner()) nview.ClaimOwnership();
            CompilerSave.Invoke(compiler, null);
            heightmap.Poke(0, true);
        }

        internal static bool ServerReady(out string problem)
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || ZDOMan.instance == null || ZoneSystem.instance == null)
            {
                problem = "Chunks are saved and placed by the server: run this on the host or the server console.";
                return false;
            }
            // Clearing a zone resets vanilla's chunk save mapping, and a save in flight would swap the old mapping
            // back in afterwards (Tools\WORLD_LOSS_20260927_RPGWORLD_CHUNKS.md).
            if (net.IsSaving())
            {
                problem = WorldSavingMessage;
                return false;
            }
            if (FiresWorldLoadGuard.Refused)
            {
                problem = RefusedLoadMessage;
                return false;
            }
            problem = null;
            return true;
        }

        // ── File ────────────────────────────────────────────────────────────
        private static void Write(string path, Blueprint blueprint, List<ZDO> objects, List<ZDO> portals)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                var pkg = new ZPackage();
                pkg.SetWriter(writer);
                pkg.Write(FileMagic);
                pkg.Write(FileFormat);
                pkg.Write(blueprint.World);
                pkg.Write(blueprint.Seed);
                pkg.Write(blueprint.Zone);
                pkg.Write((short)blueprint.WorldVersion);
                pkg.Write(blueprint.CreatedUtcTicks);
                pkg.Write(Pitch);
                pkg.Write(blueprint.LowGround);
                pkg.Write(blueprint.HighGround);
                pkg.Write(blueprint.ChunkMinY);
                pkg.Write(blueprint.ChunkCountY);
                pkg.Write(Pack(blueprint.Volume));
                for (int i = 0; i < blueprint.Paint.Length; i++)
                {
                    pkg.Write(blueprint.Paint[i].r);
                    pkg.Write(blueprint.Paint[i].g);
                    pkg.Write(blueprint.Paint[i].b);
                    pkg.Write(blueprint.Paint[i].a);
                }
                pkg.Write(blueprint.Biome);
                pkg.Write(blueprint.Pieces);
                pkg.Write(objects.Count);
                pkg.Write(portals.Count);
                ZDOExtraData.PrepareSave();
                try
                {
                    foreach (var zdo in objects) zdo.Save(pkg);
                    foreach (var zdo in portals) zdo.Save(pkg);
                }
                finally { ZDOExtraData.ClearSave(); }
            }
        }

        private static Blueprint Read(string path, List<ZDO> objects)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                var pkg = new ZPackage();
                pkg.SetReader(reader);
                var blueprint = ReadHead(pkg, out int volumeBytes);
                blueprint.Volume = Unpack(pkg.ReadByteArray(), volumeBytes);
                blueprint.Paint = new Color32[Pitch * Pitch];
                for (int i = 0; i < blueprint.Paint.Length; i++)
                    blueprint.Paint[i] = new Color32(pkg.ReadByte(), pkg.ReadByte(), pkg.ReadByte(), pkg.ReadByte());
                blueprint.Biome = pkg.ReadByteArray();
                blueprint.Pieces = pkg.ReadInt();
                blueprint.Objects = pkg.ReadInt();
                blueprint.Portals = pkg.ReadInt();
                for (int i = 0; i < blueprint.Objects + blueprint.Portals; i++)
                {
                    var zdo = ZDOPool.Create();
                    objects.Add(zdo);
                    zdo.Load(pkg, blueprint.WorldVersion);
                }
                return blueprint;
            }
        }

        private static Blueprint ReadHead(ZPackage pkg, out int volumeBytes)
        {
            if (pkg.ReadUInt() != FileMagic) throw new InvalidDataException("not a chunk blueprint");
            byte format = pkg.ReadByte();
            if (format != FileFormat) throw new InvalidDataException($"blueprint format {format}, this build reads {FileFormat}");
            var blueprint = new Blueprint
            {
                World = pkg.ReadString(),
                Seed = pkg.ReadInt(),
                Zone = pkg.ReadVector2s(),
                WorldVersion = (global::Version.World)pkg.ReadShort(),
                CreatedUtcTicks = pkg.ReadLong(),
            };
            int pitch = pkg.ReadInt();
            if (pitch != Pitch) throw new InvalidDataException($"blueprint is {pitch} wide, this build reads {Pitch}");
            blueprint.LowGround = pkg.ReadSingle();
            blueprint.HighGround = pkg.ReadSingle();
            blueprint.ChunkMinY = pkg.ReadInt();
            blueprint.ChunkCountY = pkg.ReadInt();
            volumeBytes = blueprint.ChunkCountY * VoxelWorld.ZoneChunksPerAxis * VoxelWorld.ZoneChunksPerAxis * VoxelChunk.SampleCount;
            return blueprint;
        }

        // Rock and air run flat for most of a zone, so the volume deflates to a fraction of its size.
        private static byte[] Pack(sbyte[] volume)
        {
            var bytes = new byte[volume.Length];
            Buffer.BlockCopy(volume, 0, bytes, 0, volume.Length);
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
                    if (got <= 0) throw new InvalidDataException($"the land ends after {read} of {count} bytes");
                    read += got;
                }
            }
            var volume = new sbyte[count];
            Buffer.BlockCopy(bytes, 0, volume, 0, count);
            return volume;
        }

        internal static IEnumerable<string> List()
        {
            if (!Directory.Exists(Folder)) yield break;
            foreach (string path in Directory.GetFiles(Folder, "*" + Extension))
            {
                Blueprint blueprint = default;
                string failure = null;
                try
                {
                    using (var reader = new BinaryReader(File.OpenRead(path)))
                    {
                        var pkg = new ZPackage();
                        pkg.SetReader(reader);
                        blueprint = ReadHead(pkg, out int volumeBytes);
                        pkg.ReadByteArray();
                        pkg.SetPos(pkg.GetPos() + Pitch * Pitch * 4);
                        pkg.ReadByteArray();
                        blueprint.Pieces = pkg.ReadInt();
                        blueprint.Objects = pkg.ReadInt();
                        blueprint.Portals = pkg.ReadInt();
                    }
                }
                catch (Exception ex) { failure = ex.Message; }
                if (failure != null)
                {
                    yield return $"{Path.GetFileNameWithoutExtension(path)}: unreadable ({failure})";
                    continue;
                }
                yield return $"{Path.GetFileNameWithoutExtension(path)}: zone ({blueprint.Zone.x},{blueprint.Zone.y}) of "
                    + $"'{blueprint.World}', {blueprint.Objects + blueprint.Portals} object(s), {blueprint.Pieces} built "
                    + $"piece(s), ground {blueprint.LowGround:0.#} to {blueprint.HighGround:0.#} m, "
                    + $"{new FileInfo(path).Length / BytesPerKilobyte:0} KB, saved "
                    + $"{new DateTime(blueprint.CreatedUtcTicks, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd HH:mm}";
            }
        }
    }
}
