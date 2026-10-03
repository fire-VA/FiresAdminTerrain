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
            FiresLogger.LogInfo($"{Tag} Routing WEC / Infinity Hammer terrain commands to FAT tiles and voxel terrain.");
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
            return missing.Count == 0;
        }

        // The command's own Y comes from ZoneSystem.GetGroundHeight, which answers the
        // FAT tile's surface only while TileHammerCompat's lift is open.
        private static void Parameters_Prefix() => TileHammerCompat.BeginTerrainCommandQuery();

        // A finalizer, not a postfix: the constructor throws when the zone is unloaded.
        private static void Parameters_Finalizer() => TileHammerCompat.EndTerrainCommandQuery();

        private static bool GetCompilers_Prefix(object pars, ref TerrainComp[] __result)
        {
            var op = WecTerrainOp.Read(pars, s_fields);

            if (IsTargetingTile(op.Position))
            {
                RouteToTile(op);
                __result = Array.Empty<TerrainComp>();
                return false;
            }

            if (op.Void && VoxelZoneStreamer.IsVoxelWorldActive)
                RefuseVoxelVoid(pars);
            return true;
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

            public static WecTerrainOp Read(object pars, Dictionary<string, FieldInfo> fields) => new WecTerrainOp(pars, fields);
        }
    }
}
