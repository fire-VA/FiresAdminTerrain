using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Persistence
{
    // Routes World Edit Commands' `terrain` command - and Infinity Hammer's terrain
    // tools, which run it - to the terrain actually being targeted. Over a FAT tile the
    // operations go to TileBrushRPC and the vanilla heightmap below is left untouched.
    // In a voxel world `void` is refused. Everything else runs unchanged.
    // Plan: Docs/PLAN_WecInfinityHammerTerrainBridge.md
    public static class WecTerrainBridge
    {
        private const string HarmonyId = "VerdantsAscent.WecTerrainBridge";
        private const string TerrainCommandType = "WorldEditCommands.TerrainCommand";
        private const string TerrainParametersType = "WorldEditCommands.TerrainParameters";
        private const string GetCompilersMethod = "GetCompilers";
        private const string PaintsField = "Paints";
        private const string Tag = "[WecTerrainBridge]";
        private const string BlockCheckOff = "Off";
        private const float FullChance = 1f;
        private const int CustomPaintChannelsMin = 3;
        private const int PaintChannels = 4;

        private static readonly string[] RequiredFields =
        {
            "Position", "Size", "Width", "Set", "Reset", "Void", "Delta", "Level",
            "Min", "Max", "Smooth", "Slope", "SlopeAngle", "Paint", "BlockCheck", "Within", "Chance",
        };

        private static readonly Dictionary<string, FieldInfo> s_fields = new Dictionary<string, FieldInfo>();
        private static Dictionary<string, Color> s_paints;
        private static bool s_initialized;

        public static void Initialize()
        {
            if (s_initialized) return;
            s_initialized = true;

            // Assembly.GetType per assembly is a lookup; AccessTools.TypeByName walks every type of every assembly when the
            // type is missing, which was most of ZNetScene.Awake's 123 ms "drag + ruler" step on a dedi without WEC (R44).
            Type commandType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { commandType = assembly.GetType(TerrainCommandType, false); }
                catch (Exception) { continue; }
                if (commandType != null) break;
            }
            if (commandType == null)
            {
                FiresLogger.LogVerbose($"{Tag} World Edit Commands not loaded - not needed.");
                return;
            }

            var getCompilers = AccessTools.Method(commandType, GetCompilersMethod);
            var parametersType = commandType.Assembly.GetType(TerrainParametersType, false);
            var parametersCtor = parametersType != null
                ? AccessTools.Constructor(parametersType, new[] { typeof(Terminal.ConsoleEventArgs) })
                : null;
            if (getCompilers == null || parametersCtor == null || !TryBindFields(parametersType))
            {
                FiresLogger.LogError($"{Tag} World Edit Commands is loaded but its terrain command no longer matches "
                    + "(TerrainCommand.GetCompilers / TerrainParameters changed). WEC and Infinity Hammer terrain tools "
                    + "will edit the vanilla heightmap under FAT tiles, and void is not blocked in voxel worlds.");
                return;
            }

            s_paints = AccessTools.Field(commandType, PaintsField)?.GetValue(null) as Dictionary<string, Color>;

            var harmony = new Harmony(HarmonyId);
            harmony.Patch(parametersCtor,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(WecTerrainBridge), nameof(Parameters_Prefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(WecTerrainBridge), nameof(Parameters_Finalizer))));
            harmony.Patch(getCompilers,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(WecTerrainBridge), nameof(GetCompilers_Prefix))));
            var addTerrainAction = AccessTools.Method(commandType.Assembly.GetType(UndoHelperType, false), "AddTerrainAction");
            if (addTerrainAction != null)
                harmony.Patch(addTerrainAction, prefix: new HarmonyMethod(AccessTools.Method(typeof(WecTerrainBridge), nameof(AddTerrainAction_Prefix))));
            else FiresLogger.LogDebug($"{Tag} World Edit Commands' UndoHelper.AddTerrainAction not found; a voxel-routed op also leaves an empty WEC undo step");
            PatchHammerHeights(harmony);
            FiresLogger.LogInfo($"{Tag} Routing WEC / Infinity Hammer terrain commands to FAT tiles and voxel terrain.");
        }

        // ── The voxel surface where Infinity Hammer and WEC ask for "the ground" (0.2.744) ─────────────────────────────────
        // Fire 10-05: "infinity hammers level tool only shows when we hold alt, its slope tool doesnt work, its reset tool doesnt
        // work, and its raise tool doesnt work on skylands or voxel ground". IH reads ZoneSystem.GetGroundHeight where a terrain
        // tool depends on it: its grid snap (FreezePlacementMarker.Postfix: y = ground + 2, then a 50 m ray down) and WEC's own
        // command Y (TerrainParameters, slope's to=x,z). On a voxel world that answer is the ground under the voxel surface
        // (VoxelWrap: the heightmap) or, over a sky island, the ground far below it (VoxelOnly's VoxelGroundTruth reads at most
        // 24 m above the heightmap) - the ghost dropped under the island unless Alt (player height) lifted it. Inside those calls
        // only, GetGroundHeight answers the voxel surface nearest the height IH asked at (the ghost's, so an island answers the
        // island's top); FAT's one GetGroundHeight hook (FatVanillaHooks) asks TryHammerHeight first (0.2.750: a separate prefix
        // was overwritten, HarmonyX runs every prefix). IH's ruler ring (BaseRuler.Snap) never asks the ground height: it casts
        // from 500 m up and takes the topmost terrain, so it gets its own prefix (the surface nearest the segment, else IH's ray).
        // Everything else (spawns, vegetation, AI) keeps the normal answer. IH's own code is untouched; its methods are found by
        // name, and a missing one is skipped.
        [ThreadStatic] private static int s_hammerHeightDepth;
        private const float HammerSurfaceReach = 16f;
        private static FieldInfo s_ghostField;

        private static void PatchHammerHeights(Harmony harmony)
        {
            var open = new HarmonyMethod(AccessTools.Method(typeof(WecTerrainBridge), nameof(HammerHeight_Open)));
            var close = new HarmonyMethod(AccessTools.Method(typeof(WecTerrainBridge), nameof(HammerHeight_Close)));
            var scoped = new List<string>();
            Assembly ih = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "InfinityHammer") { ih = assembly; break; }
            if (ih != null)
            {
                var freeze = AccessTools.Method(ih.GetType("InfinityHammer.FreezePlacementMarker", false), "Postfix");
                if (freeze == null) FiresLogger.LogDebug($"{Tag} Infinity Hammer's FreezePlacementMarker.Postfix not found; its ground height stays vanilla's");
                else { harmony.Patch(freeze, prefix: open, finalizer: close); scoped.Add("InfinityHammer.FreezePlacementMarker.Postfix"); }
                var snap = AccessTools.Method(ih.GetType("InfinityTools.BaseRuler", false), "Snap", new[] { typeof(Vector3) });
                if (snap == null || snap.ReturnType != typeof(Vector3)) FiresLogger.LogDebug($"{Tag} Infinity Hammer's BaseRuler.Snap(Vector3) not found; its ruler ring stays on IH's own ray");
                else { harmony.Patch(snap, prefix: new HarmonyMethod(AccessTools.Method(typeof(WecTerrainBridge), nameof(RulerSnap_Prefix)))); scoped.Add("InfinityTools.BaseRuler.Snap (ring on the nearest surface)"); }
            }
            FiresLogger.LogDebug($"{Tag} ground height answers the voxel surface inside: WEC's TerrainParameters{(scoped.Count > 0 ? ", " + string.Join(", ", scoped.ToArray()) : " (Infinity Hammer not loaded)")}");
        }

        private static void HammerHeight_Open() => s_hammerHeightDepth++;
        private static void HammerHeight_Close() { if (s_hammerHeightDepth > 0) s_hammerHeightDepth--; }

        private static bool HammerSurface(Vector3 p, out float y)
        {
            y = 0f;
            if (s_hammerHeightDepth <= 0 || !VoxelZoneStreamer.IsVoxelWorldActive) return false;
            float hint = p.y;
            if (Mathf.Abs(hint) < 0.01f)
            {
                // A bare XZ query: the height the player is working at (the ghost's, else the player's own).
                var player = Player.m_localPlayer;
                if (player == null) return false;
                if (s_ghostField == null) s_ghostField = AccessTools.Field(typeof(Player), "m_placementGhost");
                var ghost = s_ghostField?.GetValue(player) as GameObject;
                hint = ghost != null && ghost.activeSelf ? ghost.transform.position.y : player.transform.position.y;
            }
            return VoxelWorld.TryFindSurfaceY(p.x, p.z, hint, HammerSurfaceReach, out y);
        }

        // From FAT's one ZoneSystem.GetGroundHeight hook (FatVanillaHooks), before VoxelGroundHeight.
        internal static bool TryHammerHeight(Vector3 p, out float y) => HammerSurface(p, out y);

        // IH's ring segment: the voxel surface nearest the segment's height (+0.05 m, as IH lifts it), else IH's own ray.
        private static bool RulerSnap_Prefix(Vector3 pos, ref Vector3 __result)
        {
            if (!VoxelZoneStreamer.IsVoxelWorldActive) return true;
            if (!VoxelWorld.TryFindSurfaceY(pos.x, pos.z, pos.y, HammerSurfaceReach, out float y)) return true;
            __result = new Vector3(pos.x, y + 0.05f, pos.z);
            return false;
        }

        private static bool TryBindFields(Type parametersType)
        {
            var missing = new List<string>();
            foreach (var name in RequiredFields)
            {
                var field = AccessTools.Field(parametersType, name);
                if (field == null) missing.Add(name);
                else s_fields[name] = field;
            }
            if (missing.Count > 0)
                FiresLogger.LogError($"{Tag} TerrainParameters is missing: {string.Join(", ", missing)}.");
            // 0.2.795: WEC 1.80's path / arc / curve ops (optional: absent before 1.80).
            s_pathsField = AccessTools.Field(parametersType, "Paths");
            s_pathRadiusField = AccessTools.Field(parametersType, "PathRadius");
            s_levelToPathField = AccessTools.Field(parametersType, "LevelToPath");
            return missing.Count == 0;
        }

        // ── WEC 1.80 path / arc / curve (0.2.795) ─────────────────────────────────────────────────────────────────────
        // WEC's TerrainCommand.GetCompilers collects compilers along TerrainParameters.Paths (polylines) with PathRadius when a
        // path op is given, and Position / Size no longer describe the area. The island and layer routes below work on a circle
        // at a point, so a path op that touches a sky island or a cave layer anywhere is replayed by FAT point by point along
        // its polylines (spacing half the radius), each point on its own route, all as ONE FatUndo step; `level` follows the
        // path's own height where WEC's LevelToPath asks for it. A path wholly on plain ground stays WEC's own.
        private static FieldInfo s_pathsField, s_pathRadiusField, s_levelToPathField;
        private static bool s_quiet;
        private const float PathStepShare = 0.5f, PathMinStep = 0.5f;

        private static List<List<Vector3>> PathsOf(object pars) => s_pathsField?.GetValue(pars) as List<List<Vector3>>;

        private static float PathRadiusOf(object pars, float fallback)
        {
            object range = s_pathRadiusField?.GetValue(pars);
            var max = range?.GetType().GetField("Max");
            return max != null && max.GetValue(range) is float f && f > 0f ? f : fallback;
        }

        private static IEnumerable<Vector3> PathPoints(List<List<Vector3>> paths, float step)
        {
            foreach (var line in paths)
            {
                if (line == null || line.Count == 0) continue;
                yield return line[0];
                for (int i = 1; i < line.Count; i++)
                {
                    Vector3 a = line[i - 1], b = line[i];
                    float len = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
                    int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
                    for (int k = 1; k <= n; k++) yield return Vector3.Lerp(a, b, k / (float)n);
                }
            }
        }

        // True when the path op was taken off WEC and applied by FAT along its polylines.
        private static bool TryRoutePath(WecTerrainOp op, object pars)
        {
            var paths = PathsOf(pars);
            if (paths == null || !op.HasHeightOp || !VoxelZoneStreamer.IsVoxelWorldActive) return false;
            float r = PathRadiusOf(pars, Mathf.Max(op.Size, 1f));
            float step = Mathf.Max(PathMinStep, r * PathStepShare);
            var points = new List<Vector3>(PathPoints(paths, step));
            bool fatLayer = false;
            foreach (var p in points)
                if (FiresSkyIslandTerrainEdit.Targets(p) || FiresLayerTerrainEdit.Takes(FiresTerrainLayers.Resolve(p))) { fatLayer = true; break; }
            if (!fatLayer) return false;   // wholly on plain ground: WEC's own path edit on the TerrainComp

            bool levelToPath = s_levelToPathField?.GetValue(pars) is bool ltp && ltp;
            int island = 0, layered = 0;
            FatUndo.BeginPlayerEdit("a WEC path");
            s_quiet = true;
            try
            {
                foreach (var p in points)
                {
                    var at = op.At(p, r, levelToPath && op.Level.HasValue ? p.y : op.Level);
                    if (FiresSkyIslandTerrainEdit.Targets(p)) { RouteToIsland(at, pars); island++; }
                    else { var layer = FiresTerrainLayers.Resolve(p); RouteToLayer(at, pars, in layer); layered++; }
                }
            }
            finally { s_quiet = false; FatUndo.EndPlayerEdit(); }
            string line = $"{Tag} WEC path op along {paths.Count} line(s): {points.Count} stamp(s) of radius {r:0.0} m ({island} on a sky island, {layered} on voxel layers), one undo step.";
            global::Console.instance?.Print(line);
            FiresLogger.LogInfo(line);
            return true;
        }

        // The command's own Y comes from ZoneSystem.GetGroundHeight, which answers the
        // FAT tile's surface only while TileHammerCompat's lift is open.
        private static void Parameters_Prefix() { TileHammerCompat.BeginTerrainCommandQuery(); HammerHeight_Open(); }

        // A finalizer, not a postfix: the constructor throws when the zone is unloaded.
        private static void Parameters_Finalizer() { TileHammerCompat.EndTerrainCommandQuery(); HammerHeight_Close(); }

        // 0.2.757 (Fire 10-06: voxel edits must undo exactly like TerrainComp edits): WEC's terrain command records an undo step
        // (UndoHelper.AddTerrainAction) even when GetCompilers handed it no TerrainComp. An op taken off the command (island /
        // cave voxels) is FatUndo's one step, so WEC's empty one is skipped - else every such op cost a dead undo press.
        private const string UndoHelperType = "WorldEditCommands.UndoHelper";
        private static bool s_tookOp;

        private static bool AddTerrainAction_Prefix()
        {
            if (!s_tookOp) return true;
            s_tookOp = false;
            return false;
        }

        private static bool GetCompilers_Prefix(object pars, ref TerrainComp[] __result)
        {
            var op = WecTerrainOp.Read(pars, s_fields);
            s_tookOp = false;

            // 0.2.795: WEC 1.80 path / arc / curve. Its area is the polylines, not Position / Size: never route it as a circle.
            if (PathsOf(pars) != null)
            {
                if (op.HasHeightOp) WatchOp(op);
                if (TryRoutePath(op, pars))
                {
                    if (op.Paint.Length > 0) return true;   // paint goes on with WEC's own path edit (height ops were taken off)
                    __result = Array.Empty<TerrainComp>();
                    s_tookOp = true;
                    return false;
                }
                return true;
            }

            // 0.2.710 (Fire, 10-05): aimed at a sky island (Infinity Hammer passes its ghost's Y), the height ops reshape that
            // island's voxels and are taken off the command; only paint goes on to the ground's TerrainComp (per XZ column,
            // so it is the island's paint too). Without this a level wrote (island Y - ground) into the ground below.
            if (op.HasHeightOp) WatchOp(op);
            if (op.HasHeightOp && FiresSkyIslandTerrainEdit.Targets(op.Position))
            {
                RouteToIsland(op, pars);
                if (op.Paint.Length > 0 && !PaintOnLayer(op)) return true;   // 0.2.759: the island's own paint; the ground's only as a fallback
                __result = Array.Empty<TerrainComp>();
                s_tookOp = true;
                return false;
            }

            // 0.2.757 (Fire, 10-06: "all the tools should only affect the layer they are raycast on"): the op's layer comes from
            // FiresTerrainLayers. A cave, or ground with a cave under it, is reshaped as that layer's voxels inside its band
            // (FiresLayerTerrainEdit) and the height ops are taken off the command - the TerrainComp holds one height per column,
            // the column's top. Paint: on ground over a cave the TerrainComp colour IS that ground's, so it goes on; in a cave it
            // would colour the ground above the roof, so it is skipped. Open ground keeps the TerrainComp.
            if ((op.HasHeightOp || op.Paint.Length > 0) && VoxelZoneStreamer.IsVoxelWorldActive)
            {
                var layer = FiresTerrainLayers.Resolve(op.Position);
                if (FiresLayerTerrainEdit.Takes(in layer))
                {
                    RouteToLayer(op, pars, in layer);
                    if (op.Paint.Length > 0 && layer.Kind == TerrainLayerKind.Ground) return true;   // paint only, on its own WEC step
                    __result = Array.Empty<TerrainComp>();
                    s_tookOp = true;
                    return false;
                }
                // 0.2.759: a paint-only op on a skyland paints that island (its height ops took the island route above).
                if (layer.Kind == TerrainLayerKind.Island && !op.HasHeightOp && PaintOnLayer(op))
                {
                    __result = Array.Empty<TerrainComp>();
                    s_tookOp = true;
                    return false;
                }
            }

            if (IsTargetingTile(op.Position))
            {
                RouteToTile(op);
                __result = Array.Empty<TerrainComp>();
                return false;
            }

            if (op.Void && VoxelZoneStreamer.IsVoxelWorldActive)
                RefuseVoxelVoid(pars);
            if (op.Reset && VoxelZoneStreamer.IsVoxelWorldActive) RestoreVoxelGround(op);
            return true;
        }

        // 0.2.744: `reset` on voxel ground (not an island) also takes back the voxel brush edits in its circle - V-tool carves and
        // fills are voxel data a TerrainComp reset cannot see. A cylinder ResetHalfHeight up and down from the op's height, its top
        // kept under any sky island's underside so an island above is never touched.
        private const float ResetHalfHeight = 12f, IslandClearance = 1f;

        private static void RestoreVoxelGround(WecTerrainOp op)
        {
            Vector3 p = op.Position;
            float r = Mathf.Max(op.Size, 1f), bottom = p.y - ResetHalfHeight, top = p.y + ResetHalfHeight;
            if (FiresSkyIslands.TryBottomAt(p.x, p.z, out float underside)) top = Mathf.Min(top, underside - IslandClearance);
            if (top <= bottom) return;
            // 0.2.757: inside a FatUndo scope, so the voxel half of the reset is an undo step too (WEC's step holds the TerrainComp half).
            string done;
            FatUndo.BeginPlayerEdit("a voxel ground reset");
            try { done = VoxelWorld.ApplyBrush(new Vector3(p.x, (bottom + top) * 0.5f, p.z), r, top - bottom, VoxelBrushShape.Cylinder, VoxelBrushMode.Restore); }
            finally { FatUndo.EndPlayerEdit(); }
            FiresLogger.LogDebug($"{Tag} reset on voxel ground at ({p.x:0.0},{p.z:0.0}) r={r:0.0}: voxel brush edits restored y {bottom:0.0}..{top:0.0} -> {done}");
        }

        // 0.2.744, the easy check (Fire tests by hand): every WEC / Infinity Hammer height op logs, 1.5 s later, the voxel surface
        // at its point before and after, and whether it was an island op.
        private const float WatchSeconds = 1.5f;

        private static void WatchOp(WecTerrainOp op)
        {
            if (!VoxelZoneStreamer.IsVoxelWorldActive || ZNet.instance == null) return;
            Vector3 p = op.Position;
            string what = op.Reset ? "reset" : op.Level.HasValue ? "level" : op.Slope.HasValue ? "slope" : op.Delta.HasValue ? (op.Delta.Value >= 0f ? "raise" : "lower")
                : op.Set.HasValue ? "set" : op.Min.HasValue ? "min" : op.Max.HasValue ? "max" : op.Void ? "void" : "op";
            string kind = FiresSkyIslandTerrainEdit.Targets(p) ? "island" : FiresTerrainLayers.Resolve(p).Describe();
            bool had = VoxelWorld.TryFindSurfaceY(p.x, p.z, p.y, HammerSurfaceReach, out float before);
            float top = !FiresSkyIslandTerrainEdit.Targets(p) && FiresTerrainLayers.Resolve(p).Kind == TerrainLayerKind.Cave && VoxelZoneStreamer.TryGroundHeight(p.x, p.z, out float t0) ? t0 : float.NaN;
            ZNet.instance.StartCoroutine(WatchAfter(what, p, kind, had ? before : float.NaN, top));
        }

        private static System.Collections.IEnumerator WatchAfter(string what, Vector3 p, string kind, float before, float top)
        {
            yield return new WaitForSeconds(WatchSeconds);
            bool has = VoxelWorld.TryFindSurfaceY(p.x, p.z, float.IsNaN(before) ? p.y : before, HammerSurfaceReach, out float after);
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "{0} {1} at ({2:0.0},{3:0.0}) aimed y {4:0.0}: voxel surface {5} -> {6}{7} ({8}){9}", Tag, what, p.x, p.z, p.y,
                float.IsNaN(before) ? "none" : before.ToString("0.00", CultureInfo.InvariantCulture),
                has ? after.ToString("0.00", CultureInfo.InvariantCulture) : "none",
                !float.IsNaN(before) && has ? string.Format(CultureInfo.InvariantCulture, ", changed {0:+0.00;-0.00;0.00} m", after - before) : "",
                kind, float.IsNaN(top) ? "" : string.Format(CultureInfo.InvariantCulture, "; ground top above the roof {0:0.00} -> {1}", top,
                    VoxelZoneStreamer.TryGroundHeight(p.x, p.z, out float t1) ? t1.ToString("0.00", CultureInfo.InvariantCulture) : "none")));
        }

        // The FAT tile when one is at the command's position and the command's Y is at
        // least as close to it as to the ground; otherwise the ground, so a command
        // aimed under a tile still edits the ground.
        private static bool IsTargetingTile(Vector3 position)
        {
            if (TileAnchor.Retired) return false;   // the 2D tile layer is retired: every command edits the ground
            if (!TileAnchorRegistry.TrySampleHeightAtWorld(position, out float tileY)) return false;
            if (!Heightmap.GetHeight(position, out float groundY)) return true;
            return Mathf.Abs(position.y - tileY) <= Mathf.Abs(position.y - groundY);
        }

        // WEC's own operation order and maths (Terrain.cs: smooth = (1-d)/smooth near the edge, slope = sin(a)*dw + cos(a)*dd
        // over the radius), with the island's live surface in place of the heightmap height. No WEC undo entry: the island
        // edit is a voxel brush edit, undone with `reset` over it. `set` and `void` have no island meaning and are skipped.
        private const string IslandRoute = "WEC / Infinity Hammer";

        private static void RouteToIsland(WecTerrainOp op, object pars)
        {
            var lines = new List<string>();
            var notes = new List<string>();
            Vector3 p = op.Position;
            float r = Mathf.Max(op.Size, 1f), smooth = op.Smooth;
            float Falloff(float dx, float dz)
            {
                float d = r > 0f ? Mathf.Sqrt(dx * dx + dz * dz) / r : 0f;
                return 1f - d < smooth ? (1f - d) / smooth : 1f;
            }
            if (op.Reset) lines.Add(FiresSkyIslandTerrainEdit.Restore(IslandRoute, p, r));
            if (op.Set.HasValue) notes.Add("set has no island meaning - use level");
            if (op.Slope.HasValue && !op.Level.HasValue)
            {
                float angle = op.SlopeAngle, amount = op.Slope.Value, altitude = p.y;
                lines.Add(FiresSkyIslandTerrainEdit.Reshape("slope", IslandRoute, p, r, (dx, dz, s) =>
                    altitude + (Mathf.Sin(angle) * dx / r + Mathf.Cos(angle) * dz / r) * Falloff(dx, dz) * amount / 2f));
            }
            if (op.Level.HasValue)
            {
                float level = op.Level.Value;
                lines.Add(FiresSkyIslandTerrainEdit.Reshape("level", IslandRoute, p, r, (dx, dz, s) => s + Falloff(dx, dz) * (level - s)));
            }
            if (op.Delta.HasValue)
            {
                float delta = op.Delta.Value;
                lines.Add(FiresSkyIslandTerrainEdit.Reshape(delta >= 0f ? "raise" : "lower", IslandRoute, p, r, (dx, dz, s) => s + Falloff(dx, dz) * delta));
            }
            if (op.Min.HasValue)
            {
                float min = op.Min.Value;
                lines.Add(FiresSkyIslandTerrainEdit.Reshape("min", IslandRoute, p, r, (dx, dz, s) => Mathf.Max(min, s)));
            }
            if (op.Max.HasValue)
            {
                float max = op.Max.Value;
                lines.Add(FiresSkyIslandTerrainEdit.Reshape("max", IslandRoute, p, r, (dx, dz, s) => Mathf.Min(max, s)));
            }
            if (op.Void) notes.Add("void has no island meaning");
            if (op.IsRect) notes.Add("a rectangle became a circle of its size on the island");
            if (op.HasFilters) notes.Add("blockcheck / within / chance are ignored on islands");

            foreach (var name in new[] { "Set", "Delta", "Level", "Min", "Max", "Slope" }) s_fields[name].SetValue(pars, null);
            s_fields["Reset"].SetValue(pars, false);
            s_fields["Void"].SetValue(pars, false);

            if (!s_quiet) foreach (var line in lines) global::Console.instance?.Print(line);
            if (notes.Count > 0 && !s_quiet) global::Console.instance?.Print($"{Tag} sky island: not applied: {string.Join("; ", notes)}.");
        }

        // The island route's maths on the op's layer (FiresLayerTerrainEdit): each column of the same layer, its cut or fill held
        // inside the layer's band. One FatUndo step per op (the queue's scope); WEC's empty step is skipped (AddTerrainAction_Prefix).
        private const string LayerRoute = "WEC / Infinity Hammer";

        private static void RouteToLayer(WecTerrainOp op, object pars, in TerrainLayer layer)
        {
            var lines = new List<string>();
            var notes = new List<string>();
            Vector3 p = op.Position;
            float r = Mathf.Max(op.Size, 1f), smooth = op.Smooth;
            float Falloff(float dx, float dz)
            {
                float d = r > 0f ? Mathf.Sqrt(dx * dx + dz * dz) / r : 0f;
                return 1f - d < smooth ? (1f - d) / smooth : 1f;
            }
            bool cave = layer.Kind == TerrainLayerKind.Cave;
            if (op.Reset) lines.Add(FiresLayerTerrainEdit.Restore(LayerRoute, in layer, p, r));
            if (op.Set.HasValue) notes.Add("set has no voxel meaning - use level");
            if (op.Slope.HasValue && !op.Level.HasValue)
            {
                float angle = op.SlopeAngle, amount = op.Slope.Value, altitude = p.y;
                lines.Add(FiresLayerTerrainEdit.Reshape("slope", LayerRoute, in layer, p, r, (dx, dz, s) =>
                    altitude + (Mathf.Sin(angle) * dx / r + Mathf.Cos(angle) * dz / r) * Falloff(dx, dz) * amount / 2f));
            }
            if (op.Level.HasValue)
            {
                float level = op.Level.Value;
                lines.Add(FiresLayerTerrainEdit.Reshape("level", LayerRoute, in layer, p, r, (dx, dz, s) => s + Falloff(dx, dz) * (level - s)));
            }
            if (op.Delta.HasValue)
            {
                float delta = op.Delta.Value;
                lines.Add(FiresLayerTerrainEdit.Reshape(delta >= 0f ? "raise" : "lower", LayerRoute, in layer, p, r, (dx, dz, s) => s + Falloff(dx, dz) * delta));
            }
            if (op.Min.HasValue)
            {
                float min = op.Min.Value;
                lines.Add(FiresLayerTerrainEdit.Reshape("min", LayerRoute, in layer, p, r, (dx, dz, s) => Mathf.Max(min, s)));
            }
            if (op.Max.HasValue)
            {
                float max = op.Max.Value;
                lines.Add(FiresLayerTerrainEdit.Reshape("max", LayerRoute, in layer, p, r, (dx, dz, s) => Mathf.Min(max, s)));
            }
            if (op.Void) notes.Add("void has no voxel meaning");
            if (cave && op.Paint.Length > 0 && !PaintOnLayer(op))
                notes.Add("paint could not go on the cave layer (no local player, or not a paint), and the TerrainComp would colour the ground above the roof, so it is skipped");
            if (op.IsRect) notes.Add("a rectangle became a circle of its size");
            if (op.HasFilters) notes.Add("blockcheck / within / chance are ignored on voxel layers");

            // Ground over a cave keeps its paint on the TerrainComp: only the height ops are taken off the command.
            foreach (var name in new[] { "Set", "Delta", "Level", "Min", "Max", "Slope" }) s_fields[name].SetValue(pars, null);
            s_fields["Reset"].SetValue(pars, false);
            s_fields["Void"].SetValue(pars, false);

            if (!s_quiet) foreach (var line in lines) global::Console.instance?.Print(line);
            if (notes.Count > 0 && !s_quiet)
            {
                string note = $"{Tag} {layer.Describe()}: not applied: {string.Join("; ", notes)}.";
                global::Console.instance?.Print(note);
                FiresLogger.LogDebug(note);
            }
        }

        // WEC's own operation order. Each tile op records its own undo entry, and
        // TileUndoLog does not nest, so nothing wraps them.
        private static void RouteToTile(WecTerrainOp op)
        {
            var notes = new List<string>();
            NoteTileLimits(op, notes);

            if (!MayApplyEveryOp(op, out string deniedOp))
            {
                Report($"FAT tiles: only an admin may {deniedOp} one. Nothing changed.", notes);
                return;
            }

            var sent = new List<string>();
            if (op.Slope.HasValue && !op.Level.HasValue)
            {
                TileBrushRPC.SendSlope(op.Position, op.Size, op.SlopeAngle, op.Position.y, op.Slope.Value, op.Smooth, op.IsRect);
                sent.Add("slope");
            }
            if (op.Level.HasValue)
            {
                TileBrushRPC.SendFlat(op.Position, op.Size, op.Level.Value, op.IsRect);
                sent.Add("level");
            }
            if (op.Delta.HasValue)
            {
                TileBrushRPC.SendRaise(op.Position, op.Size, op.Delta.Value, op.Smooth);
                sent.Add(op.Delta.Value >= 0f ? "raise" : "lower");
            }
            if (op.Void)
            {
                TileBrushRPC.SendHolePunch(op.Position, op.Size);
                sent.Add("void");
            }
            if (op.Paint.Length > 0 && TrySendTilePaint(op, notes)) sent.Add("paint");

            Report(sent.Count == 0 ? "FAT tile: nothing to apply." : $"FAT tile: {string.Join(", ", sent)}.", notes);
        }

        // One command can carry several ops, so it is applied whole or refused whole -
        // the same rule the vanilla-hoe blocker uses, in WEC's own order.
        private static bool MayApplyEveryOp(WecTerrainOp op, out string deniedOp)
        {
            deniedOp = null;
            if (op.Slope.HasValue && !op.Level.HasValue && !TileBrushRPC.LocalPeerMayEdit(TileBrushRPC.TileOp.Slope)) deniedOp = "slope";
            else if (op.Level.HasValue && !TileBrushRPC.LocalPeerMayEdit(TileBrushRPC.TileOp.Level)) deniedOp = "level";
            else if (op.Delta.HasValue && !TileBrushRPC.LocalPeerMayEdit(TileBrushRPC.TileOp.Raise)) deniedOp = "raise or lower";
            else if (op.Void && !TileBrushRPC.LocalPeerMayEdit(TileBrushRPC.TileOp.HolePunch)) deniedOp = "void";
            else if (op.Paint.Length > 0 && !TileBrushRPC.LocalPeerMayEdit(TileBrushRPC.TileOp.Paint)) deniedOp = "paint";
            return deniedOp == null;
        }

        private static void NoteTileLimits(WecTerrainOp op, List<string> notes)
        {
            if (op.Reset) notes.Add("reset has no tile equivalent (tiles have no generated height to return to, and a clear would delete the tile)");
            if (op.Set.HasValue) notes.Add("set has no tile equivalent - use level");
            if (op.Min.HasValue) notes.Add("min has no tile equivalent");
            if (op.Max.HasValue) notes.Add("max has no tile equivalent");
            if (op.Smooth > 0f && (op.Level.HasValue || op.Delta.HasValue))
                notes.Add("smooth is ignored by level and raise on FAT tiles (the tile brush always uses its own smoothstep edge)");
            if (op.IsRect) notes.Add("a rectangle becomes a square of its longer side on FAT tiles, and its rotation is ignored");
            if (op.HasFilters) notes.Add("blockcheck / within / chance are ignored on FAT tiles");
        }

        private static bool TrySendTilePaint(WecTerrainOp op, List<string> notes)
        {
            if (!TryResolvePaint(op.Paint, out Color32 color, out string problem))
            {
                notes.Add($"paint skipped: {problem}");
                return false;
            }
            TileBrushRPC.SendPaint(op.Position, op.Size, color, op.IsRect);
            return true;
        }

        // WEC's own named paints, or "r,g,b[,a]" - the same two forms its terrain
        // command accepts, except an unparsable channel is reported instead of
        // silently becoming its default.
        // 0.2.759 (Fire 10-06: voxel edits get paint like TerrainComp edits): a WEC paint aimed at a cave or a skyland goes on
        // that layer only ([worldgen]'s VoxelLayerPaint: one FatUndo step, synced through the chunk anchors). False on the
        // ground, with no local player or an unknown paint - the caller then keeps WEC's own TerrainComp paint (or skips it).
        private static bool PaintOnLayer(WecTerrainOp op)
        {
            if (op.Paint.Length == 0 || !TryResolvePaint(op.Paint, out Color32 color, out _)) return false;
            return VoxelLayerPaint.TryPaintColor(op.Position, Mathf.Max(op.Size, 1f), color, "a WEC paint");
        }

        private static bool TryResolvePaint(string paint, out Color32 color, out string problem)
        {
            color = default;
            problem = null;
            if (s_paints != null && s_paints.TryGetValue(paint, out Color named))
            {
                color = named;
                return true;
            }

            var parts = paint.Split(',');
            if (parts.Length < CustomPaintChannelsMin)
            {
                problem = $"unknown paint '{paint}'";
                return false;
            }

            var channels = new float[PaintChannels] { 0f, 0f, 0f, 1f };
            for (int i = 0; i < parts.Length && i < PaintChannels; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out channels[i]))
                {
                    problem = $"'{parts[i]}' is not a paint channel";
                    return false;
                }
            }
            color = new Color(channels[0], channels[1], channels[2], channels[3]);
            return true;
        }

        // A void writes NaN heights. In VoxelWrap the vanilla heightmap stays
        // authoritative, so a void would hole the server's terrain while voxel clients
        // stand on ground. The rest of the command still runs.
        private static void RefuseVoxelVoid(object pars)
        {
            s_fields["Void"].SetValue(pars, false);
            Report("void skipped: voxel terrain does not mirror terrain holes. Carve voxel holes with "
                + "va_voxel_carve (not yet persisted across relog).", null);
        }

        private static void Report(string summary, List<string> notes)
        {
            string line = notes == null || notes.Count == 0
                ? $"{Tag} {summary}"
                : $"{Tag} {summary} Not applied: {string.Join("; ", notes)}.";
            global::Console.instance?.Print(line);
            FiresLogger.LogInfo(line);
        }

        private readonly struct WecTerrainOp
        {
            public readonly Vector3 Position;
            public readonly float Size;
            public readonly bool IsRect;
            public readonly float? Set;
            public readonly bool Reset;
            public readonly bool Void;
            public readonly float? Delta;
            public readonly float? Level;
            public readonly float? Min;
            public readonly float? Max;
            public readonly float Smooth;
            public readonly float? Slope;
            public readonly float SlopeAngle;
            public readonly string Paint;
            public readonly bool HasFilters;

            // 0.2.795: the same op as a circle of `size` at one point of a WEC path (its level replaced when given).
            private WecTerrainOp(in WecTerrainOp o, Vector3 position, float size, float? level)
            {
                Position = position; Size = size; IsRect = false;
                Set = o.Set; Reset = o.Reset; Void = o.Void; Delta = o.Delta; Level = level; Min = o.Min; Max = o.Max;
                Smooth = o.Smooth; Slope = o.Slope; SlopeAngle = o.SlopeAngle; Paint = o.Paint; HasFilters = o.HasFilters;
            }

            public WecTerrainOp At(Vector3 position, float size, float? level) => new WecTerrainOp(in this, position, size, level);

            private WecTerrainOp(object pars, Dictionary<string, FieldInfo> f)
            {
                Position = (Vector3)f["Position"].GetValue(pars);
                Size = (float)f["Size"].GetValue(pars);
                IsRect = f["Width"].GetValue(pars) != null;
                Set = (float?)f["Set"].GetValue(pars);
                Reset = (bool)f["Reset"].GetValue(pars);
                Void = (bool)f["Void"].GetValue(pars);
                Delta = (float?)f["Delta"].GetValue(pars);
                Level = (float?)f["Level"].GetValue(pars);
                Min = (float?)f["Min"].GetValue(pars);
                Max = (float?)f["Max"].GetValue(pars);
                Smooth = (float)f["Smooth"].GetValue(pars);
                Slope = (float?)f["Slope"].GetValue(pars);
                SlopeAngle = (float)f["SlopeAngle"].GetValue(pars);
                Paint = (string)f["Paint"].GetValue(pars) ?? string.Empty;
                HasFilters = f["BlockCheck"].GetValue(pars)?.ToString() != BlockCheckOff
                    || f["Within"].GetValue(pars) != null
                    || (float)f["Chance"].GetValue(pars) < FullChance;
            }

            public bool HasHeightOp => Set.HasValue || Reset || Void || Delta.HasValue || Level.HasValue || Min.HasValue || Max.HasValue || Slope.HasValue;

            public static WecTerrainOp Read(object pars, Dictionary<string, FieldInfo> fields) => new WecTerrainOp(pars, fields);
        }
    }
}
