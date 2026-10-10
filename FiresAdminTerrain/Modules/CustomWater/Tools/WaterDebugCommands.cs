using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Console commands for testing and debugging custom water.
    /// Supports both debug commands and Infinity Hammer-style placement commands.
    /// </summary>
    public static class WaterDebugCommands
    {
        private static bool s_registered = false;

        /// <summary>
        /// Registers all custom water console commands.
        /// </summary>
        // Every uniform the FAT config writes to the inland liquid material,
        // and the ocean curve FTS pushes. A knob whose name the shader never
        // declares fails SILENTLY (these are set raw, with no HasProperty
        // guard), so the only way to tell a mis-tuned slider from a dead one
        // is to ask the material.
        private static readonly string[] s_probeProps =
        {
            "_ColorTop", "_ColorBottom", "_ColorBottomShallow", "_SurfaceColor",
            "_DepthFade", "_RefractionScale", "_FiresEdgeSoftness", "_FiresLiquidOpacity",
            "_FiresAbsorbExtinction",
            "_FiresTurbidity", "_FiresTurbidityDepth", "_FiresTurbChurn", "_FiresMurkColor",
            "_FiresFlowSpeed", "_FiresFlowTurb", "_FiresFlowFoam", "_FiresFlowFoamAdvect",
            "_FiresFlowWaveAmp", "_FiresFlowWaveFreq", "_FiresFlowWaveSpeed",
            "_FiresFoamIntensityA", "_FiresFoamIntensityB", "_FiresFoamShoreAmount",
            "_FiresFoamSlopeDriven", "_FiresFoamColor",
        };

        private static void DumpMaterialProps(Terminal.ConsoleEventArgs args, string label, Material mat)
        {
            if (mat == null) { args.Context.AddString($"{label}: NO MATERIAL"); return; }
            var sh = mat.shader;
            args.Context.AddString($"— {label}: '{mat.name}' shader='{(sh != null ? sh.name : "null")}'");
            if (sh == null) return;

            int declared = 0;
            var have = new List<string>();
            var missing = new List<string>();
            for (int i = 0; i < s_probeProps.Length; i++)
            {
                if (mat.HasProperty(s_probeProps[i])) { have.Add(s_probeProps[i]); declared++; }
                else missing.Add(s_probeProps[i]);
            }
            args.Context.AddString($"   Properties block: {sh.GetPropertyCount()} entr(ies); of the {s_probeProps.Length} knobs we write, {declared} are declared.");
            if (have.Count > 0) args.Context.AddString("   DECLARED: " + string.Join(", ", have.ToArray()));
            if (missing.Count > 0)
                args.Context.AddString("   NOT IN PROPERTIES: " + string.Join(", ", missing.ToArray()));
            args.Context.AddString("   (a name can still be a CGINCLUDE uniform the shader reads — but if a knob does " +
                                   "NOTHING in game and shows here, that is your answer.)");
        }

        public static void Register()
        {
            if (s_registered) return;

            // ===== Debug Commands =====

            // Which water knobs actually reach a shader — ocean and inland are
            // different shaders with different property sets, and a raw SetFloat
            // to a name the shader never declared is a silent no-op.
            new Terminal.ConsoleCommand("va_water_props",
                "[va_water_props] Dump the inland-liquid and ocean materials: shader name, property count, and which of the config knobs each one actually declares.",
                args =>
                {
                    DumpMaterialProps(args, "INLAND (rivers/ponds/lakes)",
                        FiresLiquidWaterPrefab.GetSharedPourMaterial());
                    DumpMaterialProps(args, "OCEAN",
                        WaterMaterialManager.GetWaterMaterial(LiquidType.Water));
                });

            // spawn_pond <width> <height> <length> [waterLevel] [persist]
            new Terminal.ConsoleCommand("spawn_pond", "Spawns a custom pond at player position",
                (args) => SpawnPondCommand(args), isCheat: true);

            // spawn_river <width> <height> <length> [flowDirection] [flowSpeed] [persist]
            new Terminal.ConsoleCommand("spawn_river", "Spawns a custom river at player position",
                (args) => SpawnRiverCommand(args), isCheat: true);

            // spawn_waterfall <dropHeight> [flowRate] [width] [persist]
            new Terminal.ConsoleCommand("spawn_waterfall", "Spawns a custom waterfall at player position",
                (args) => SpawnWaterfallCommand(args), isCheat: true);

            // clear_water <radius>
            new Terminal.ConsoleCommand("clear_water", "Removes all custom water in radius",
                (args) => ClearWaterCommand(args), isCheat: true);

            // clear_zone_water
            new Terminal.ConsoleCommand("clear_zone_water", "Removes all custom water in current zone",
                (args) => ClearZoneWaterCommand(args), isCheat: true);

            // water_info
            new Terminal.ConsoleCommand("water_info", "Shows info about custom water at player position",
                (args) => WaterInfoCommand(args), isCheat: false);

            // Round-W-4 — toggle the balloon-shell mesh post-process.
            new Terminal.ConsoleCommand("va_water_balloon_shell",
                "[0|1] Toggle the balloon-shell water mesh (top + bottom + side walls). On = water is a 3D body; off = flat top sheet (legacy).",
                (args) => ToggleBalloonShell(args), isCheat: true);

            // list_water
            new Terminal.ConsoleCommand("list_water", "Lists all custom water instances",
                (args) => ListWaterCommand(args), isCheat: false);

            // ===== Infinity Hammer Style Commands =====

            // place_pond [size=X] [depth=Y] [level=0.5] [tar]
            new Terminal.ConsoleCommand("place_pond", "Places a pond (Infinity Hammer compatible)",
                (args) => PlacePondCommand(args), isCheat: true);

            // place_river [size=X] [depth=Y] [flow=DIR] [speed=S] [tar]
            new Terminal.ConsoleCommand("place_river", "Places a river (Infinity Hammer compatible)",
                (args) => PlaceRiverCommand(args), isCheat: true);

            // place_waterfall [height=H] [flow=F] [width=W] [tar]
            new Terminal.ConsoleCommand("place_waterfall", "Places a waterfall (Infinity Hammer compatible)",
                (args) => PlaceWaterfallCommand(args), isCheat: true);

            // delete_water [radius=R]
            new Terminal.ConsoleCommand("delete_water", "Deletes custom water in area (Infinity Hammer compatible)",
                (args) => DeleteWaterCommand(args), isCheat: true);

            // convert_water_type [water|tar]
            new Terminal.ConsoleCommand("convert_water_type", "Converts water to tar or vice versa",
                (args) => ConvertWaterTypeCommand(args), isCheat: true);

            // set_liquid_type [water|tar|ice|boiling|freezing|lava]
            new Terminal.ConsoleCommand("set_liquid_type", "Sets liquid type of nearest custom water (water/tar/ice/boiling/freezing/lava)",
                (args) => SetLiquidTypeCommand(args), isCheat: true);

            // cycle_liquid_type [forward|backward]
            new Terminal.ConsoleCommand("cycle_liquid_type", "Cycles liquid type of nearest custom water",
                (args) => CycleLiquidTypeCommand(args), isCheat: true);

            // water_material_info
            new Terminal.ConsoleCommand("water_material_info", "Shows captured water/tar material info",
                (args) => WaterMaterialInfoCommand(args), isCheat: false);

            // water_refresh_materials
            new Terminal.ConsoleCommand("water_refresh_materials", "Force re-capture of water/tar materials",
                (args) => WaterRefreshMaterialsCommand(args), isCheat: true);

            // cleanup_water_anchors - IMPORTANT: Run this to remove duplicate anchors
            new Terminal.ConsoleCommand("cleanup_water_anchors", "Removes duplicate water anchor ZDOs (fixes persistence issues)",
                (Terminal.ConsoleEventFailable)(args => CleanupWaterAnchorsCommand(args)), isCheat: true);

            // ===== Auto Water Generation Commands =====

            // va_auto_water_status
            new Terminal.ConsoleCommand("va_auto_water_status", "Shows auto water generation status and config",
                (args) => AutoWaterStatusCommand(args), isCheat: false);

            // va_auto_water_generate
            new Terminal.ConsoleCommand("va_auto_water_generate", "Force re-generates auto water from WorldGenerator data",
                (args) => AutoWaterGenerateCommand(args), isCheat: true);

            // va_clear_auto_water
            new Terminal.ConsoleCommand("va_clear_auto_water", "Permanently removes ALL custom water from the entire world (loaded + unloaded zones + save data). Clients send request to server via RPC.",
                (args) => ClearAutoWaterCommand(args), isCheat: true);

            // va_rivers - pings all vanilla river centers on minimap
            new Terminal.ConsoleCommand("va_rivers", "Pings all vanilla river positions on minimap",
                (args) => PingRiversCommand(args), isCheat: false);

            // va_streams - pings all vanilla stream centers on minimap
            new Terminal.ConsoleCommand("va_streams", "Pings all vanilla stream positions on minimap",
                (args) => PingStreamsCommand(args), isCheat: false);

            // va_lakes - pings all vanilla lake positions on minimap
            new Terminal.ConsoleCommand("va_lakes", "Pings all vanilla lake positions on minimap",
                (args) => PingLakesCommand(args), isCheat: false);

            // va_find_water - lists nearest water combiners and offers teleport
            new Terminal.ConsoleCommand("va_find_water", "Lists nearest auto-generated water bodies with distance and type. Optional: va_find_water tp <index> to teleport.",
                (args) => FindWaterCommand(args), isCheat: true);

            // va_dump_heightmap - exports BC heightmap and depression map to PNG
            new Terminal.ConsoleCommand("va_dump_heightmap", "Exports BC heightmap and depression map to PNG in FiresAdminTerrain debug folder",
                (args) => DumpHeightmapCommand(args), isCheat: true);

            // va_build_height_cache - samples real terrain into a grid for depression analysis
            new Terminal.ConsoleCommand("va_build_height_cache", "Samples WorldGenerator.GetHeight() into a grid for accurate river detection (includes ALL BC modifications)",
                (args) => BuildHeightCacheCommand(args), isCheat: true);

            // va_dump_world_heightcache - exports the world height cache to PNG for visualization
            new Terminal.ConsoleCommand("va_dump_world_heightcache", "Exports WorldHeightCache (real terrain) to heightmap + depression map PNGs. Builds cache first if needed.",
                (args) => DumpWorldHeightCacheCommand(args), isCheat: true);

            // ===== Waterfall Terrain Paint Commands =====

            // paint_waterfall [radius]
            new Terminal.ConsoleCommand("paint_waterfall", "Paints waterfall texture on terrain (like lava paint)",
                (Terminal.ConsoleEventFailable)(args => PaintWaterfallCommand(args)), isCheat: true);

            // clear_waterfall_paint [radius]
            new Terminal.ConsoleCommand("clear_waterfall_paint", "Clears waterfall paint from terrain",
                (Terminal.ConsoleEventFailable)(args => ClearWaterfallPaintCommand(args)), isCheat: true);

            // ===== Manual VFX Placement Commands =====

            // place_water_vfx [mist|splash] [scale=S]
            new Terminal.ConsoleCommand("place_water_vfx", "Places water VFX (mist or splash) at cursor",
                (Terminal.ConsoleEventFailable)(args => PlaceWaterVFXCommand(args)), isCheat: true);

            // clear_water_vfx [radius]
            new Terminal.ConsoleCommand("clear_water_vfx", "Clears manually placed water VFX in area",
                (Terminal.ConsoleEventFailable)(args => ClearWaterVFXCommand(args)), isCheat: true);

            s_registered = true;
            FiresLogger.LogInfo("[WaterDebugCommands] Registered custom water console commands");
        }

        #region Debug Spawn Commands

        private static void SpawnPondCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            // Parse arguments
            float width = args.Length > 1 ? float.Parse(args[1]) : 10f;
            float height = args.Length > 2 ? float.Parse(args[2]) : 3f;
            float length = args.Length > 3 ? float.Parse(args[3]) : 10f;
            float waterLevel = args.Length > 4 ? float.Parse(args[4]) : 0.5f;
            bool persist = args.Length > 5 && (args[5].ToLower() == "true" || args[5] == "1");

            Vector3 pos = player.transform.position + player.transform.forward * 5f;
            Vector3 size = new Vector3(width, height, length);

            var pond = CustomWaterManager.SpawnPond(pos, size, waterLevel, persist);

            args.Context.AddString($"Spawned pond at {pos} with size {size}, water level {waterLevel}" + (persist ? " (persistent)" : ""));
        }

        private static void SpawnRiverCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            // Parse arguments
            float width = args.Length > 1 ? float.Parse(args[1]) : 10f;
            float height = args.Length > 2 ? float.Parse(args[2]) : 3f;
            float length = args.Length > 3 ? float.Parse(args[3]) : 20f;
            float flowDir = args.Length > 4 ? float.Parse(args[4]) : player.transform.eulerAngles.y;
            float flowSpeed = args.Length > 5 ? float.Parse(args[5]) : 2f;
            bool persist = args.Length > 6 && (args[6].ToLower() == "true" || args[6] == "1");

            Vector3 pos = player.transform.position + player.transform.forward * 5f;
            Vector3 size = new Vector3(width, height, length);

            var river = CustomWaterManager.SpawnRiver(pos, size, flowDir, flowSpeed, persist);

            args.Context.AddString($"Spawned river at {pos}, flow direction {flowDir}\u00B0, speed {flowSpeed} m/s" + (persist ? " (persistent)" : ""));
        }

        private static void SpawnWaterfallCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            // Parse arguments
            float dropHeight = args.Length > 1 ? float.Parse(args[1]) : 10f;
            float flowRate = args.Length > 2 ? float.Parse(args[2]) : 5f;
            float width = args.Length > 3 ? float.Parse(args[3]) : 2f;
            bool persist = args.Length > 4 && (args[4].ToLower() == "true" || args[4] == "1");

            Vector3 pos = player.transform.position + player.transform.forward * 3f + Vector3.up * 2f;

            var waterfall = CustomWaterManager.SpawnWaterfall(pos, dropHeight, flowRate, null, persist);
            waterfall.Width = width;

            args.Context.AddString($"Spawned waterfall at {pos}, drop {dropHeight}m, flow rate {flowRate}" + (persist ? " (persistent)" : ""));
        }

        #endregion

        #region Infinity Hammer Style Commands

        private static void PlacePondCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            // Parse key=value arguments
            float size = 10f;
            float depth = 3f;
            float level = 0.5f;
            bool isTar = false;

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (arg.StartsWith("size="))
                    float.TryParse(arg.Substring(5), out size);
                else if (arg.StartsWith("depth="))
                    float.TryParse(arg.Substring(6), out depth);
                else if (arg.StartsWith("level="))
                    float.TryParse(arg.Substring(6), out level);
                else if (arg == "tar")
                    isTar = true;
            }

            // Use camera raycast position if available, otherwise use player position
            Vector3 pos = GetPlacementPosition(player);
            Vector3 bounds = new Vector3(size, depth, size);

            var pond = CustomWaterManager.SpawnPond(pos, bounds, level, persist: true);
            pond.LiquidType = isTar ? LiquidType.Tar : LiquidType.Water;

            string liquidName = isTar ? "tar" : "water";
            args.Context.AddString($"Placed {liquidName} pond at {pos:F1}, size {size}m, depth {depth}m");
        }

        private static void PlaceRiverCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            // Parse key=value arguments
            float size = 10f;
            float depth = 2f;
            float flow = player.transform.eulerAngles.y;
            float speed = 2f;
            bool isTar = false;

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (arg.StartsWith("size="))
                    float.TryParse(arg.Substring(5), out size);
                else if (arg.StartsWith("depth="))
                    float.TryParse(arg.Substring(6), out depth);
                else if (arg.StartsWith("flow="))
                    float.TryParse(arg.Substring(5), out flow);
                else if (arg.StartsWith("speed="))
                    float.TryParse(arg.Substring(6), out speed);
                else if (arg == "tar")
                    isTar = true;
            }

            Vector3 pos = GetPlacementPosition(player);
            Vector3 bounds = new Vector3(size, depth, size * 2f); // Rivers are typically longer than wide

            var river = CustomWaterManager.SpawnRiver(pos, bounds, flow, speed, persist: true);
            river.LiquidType = isTar ? LiquidType.Tar : LiquidType.Water;

            string liquidName = isTar ? "tar" : "water";
            args.Context.AddString($"Placed {liquidName} river at {pos:F1}, flow {flow}\u00B0, speed {speed}m/s");
        }

        private static void PlaceWaterfallCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            // Parse key=value arguments
            float height = 10f;
            float flow = 5f;
            float width = 2f;
            bool isTar = false;

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (arg.StartsWith("height="))
                    float.TryParse(arg.Substring(7), out height);
                else if (arg.StartsWith("flow="))
                    float.TryParse(arg.Substring(5), out flow);
                else if (arg.StartsWith("width="))
                    float.TryParse(arg.Substring(6), out width);
                else if (arg == "tar")
                    isTar = true;
            }

            Vector3 pos = GetPlacementPosition(player);

            var waterfall = CustomWaterManager.SpawnWaterfall(pos, height, flow, null, persist: true);
            waterfall.Width = width;
            waterfall.LiquidType = isTar ? LiquidType.Tar : LiquidType.Water;

            string liquidName = isTar ? "tar" : "water";
            args.Context.AddString($"Placed {liquidName} waterfall at {pos:F1}, height {height}m, flow {flow}");
        }

        private static void DeleteWaterCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            float radius = 5f;

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (arg.StartsWith("radius="))
                    float.TryParse(arg.Substring(7), out radius);
            }

            Vector3 pos = GetPlacementPosition(player);

            // ClearRadius now also handles WaterMeshCombiners with proper anchor cleanup
            int count = CustomWaterManager.ClearRadius(pos, radius);

            args.Context.AddString($"Deleted {count} water instance(s) within {radius}m of {pos:F1}");
        }

        private static void ConvertWaterTypeCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            string targetType = args.Length > 1 ? args[1].ToLower() : "water";
            LiquidType newType = targetType == "tar" ? LiquidType.Tar : LiquidType.Water;

            Vector3 pos = GetPlacementPosition(player);
            var water = CustomWaterManager.GetWaterAt(pos);

            if (water == null)
            {
                args.Context.AddString("No custom water at position");
                return;
            }

            water.LiquidType = newType;
            args.Context.AddString($"Converted water to {newType}");
        }

        private static void SetLiquidTypeCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            if (args.Length < 2)
            {
                args.Context.AddString("Usage: set_liquid_type [water|tar|ice|boiling|freezing|lava]");
                return;
            }

            string typeStr = args[1].ToLower();
            CustomLiquidType newType;
            switch (typeStr)
            {
                case "water": newType = CustomLiquidType.Water; break;
                case "tar": newType = CustomLiquidType.Tar; break;
                case "ice": newType = CustomLiquidType.Ice; break;
                case "boiling": newType = CustomLiquidType.BoilingWater; break;
                case "freezing": newType = CustomLiquidType.FreezingWater; break;
                case "lava": newType = CustomLiquidType.Lava; break;
                default:
                    args.Context.AddString($"Unknown liquid type: {typeStr}. Valid: water, tar, ice, boiling, freezing, lava");
                    return;
            }

            Vector3 pos = GetPlacementPosition(player);

            // Try WaterMeshCombiner first
            var combiner = WaterMeshCombiner.GetCombinerAt(pos);
            if (combiner != null)
            {
                combiner.SetCustomLiquidType(newType);
                args.Context.AddString($"Set combiner liquid type to {newType.GetDisplayName()}");
                return;
            }

            // Try CustomWaterBase
            var water = CustomWaterManager.GetWaterAt(pos);
            if (water != null)
            {
                water.LiquidType = newType.ToVanillaLiquidType();
                args.Context.AddString($"Set water to {newType.GetDisplayName()} (vanilla type: {water.LiquidType})");
                return;
            }

            args.Context.AddString("No custom water at position");
        }

        private static void CycleLiquidTypeCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            bool forward = true;
            if (args.Length > 1 && args[1].ToLower() == "backward")
                forward = false;

            Vector3 pos = GetPlacementPosition(player);
            var combiner = WaterMeshCombiner.GetCombinerAt(pos);
            if (combiner == null)
            {
                args.Context.AddString("No custom water combiner at position");
                return;
            }

            int current = (int)combiner.CustomLiquidType;
            int maxType = 5; // Lava = 5
            int next = forward ? (current + 1) : (current - 1);
            if (next > maxType) next = 0;
            if (next < 0) next = maxType;

            CustomLiquidType newType = (CustomLiquidType)next;
            combiner.SetCustomLiquidType(newType);
            args.Context.AddString($"Cycled liquid type to {newType.GetDisplayName()}");
        }

        /// <summary>
        /// Gets the placement position from camera raycast or player position.
        /// </summary>
        private static Vector3 GetPlacementPosition(Player player)
        {
            // Try to raycast from camera
            var camera = GameCamera.instance;
            if (camera != null)
            {
                Ray ray = new Ray(camera.transform.position, camera.transform.forward);
                if (Physics.Raycast(ray, out RaycastHit hit, 100f, LayerMask.GetMask("Default", "static_solid", "terrain", "piece")))
                {
                    return hit.point;
                }
            }

            // Fallback to player position + forward
            return player.transform.position + player.transform.forward * 5f;
        }

        #endregion

        #region Utility Commands

        private static void ClearWaterCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            float radius = args.Length > 1 ? float.Parse(args[1]) : 50f;

            // ClearRadius now also handles WaterMeshCombiners with proper anchor cleanup
            int count = CustomWaterManager.ClearRadius(player.transform.position, radius);

            args.Context.AddString($"Removed {count} custom water instance(s) within {radius}m (anchors destroyed)");
        }

        private static void ClearZoneWaterCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            if (ZoneSystem.instance == null)
            {
                args.Context.AddString("ZoneSystem not available");
                return;
            }

            // ClearZone now also handles WaterMeshCombiners with proper anchor cleanup.
            // ZoneSystem.GetZone returns Vector2s on public test; convert back so
            // CustomWaterManager's Vector2i-keyed APIs stay unchanged.
            Vector2i zone = ZoneSystem.GetZone(player.transform.position).ToVector2i();
            CustomWaterManager.ClearZone(zone);

            args.Context.AddString($"Cleared all custom water in zone {zone} (anchors destroyed)");
        }

        // Round-W-4 — toggle balloon shell on/off. Forces all combiners
        // to regenerate so the change applies immediately.
        private static void ToggleBalloonShell(Terminal.ConsoleEventArgs args)
        {
            bool enable;
            if (args != null && args.Length > 1)
            {
                if (args[1] == "0" || args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                    enable = false;
                else if (args[1] == "1" || args[1].Equals("on", StringComparison.OrdinalIgnoreCase))
                    enable = true;
                else
                {
                    args.Context.AddString($"va_water_balloon_shell: expected 0 or 1, got '{args[1]}'");
                    return;
                }
            }
            else
            {
                enable = !WaterBalloonShell.Enabled; // toggle
            }

            WaterBalloonShell.Enabled = enable;

            // Force regen on every combiner so the new mesh shape shows up.
            int regen = 0;
            foreach (var c in WaterMeshCombiner.AllCombiners)
            {
                if (c == null) continue;
                try { c.ForceRegenerate(); regen++; } catch { }
            }
            args.Context.AddString($"va_water_balloon_shell → {(enable ? "ON" : "OFF")} ({regen} combiner(s) regenerated)");
        }

        private static void WaterInfoCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            Vector3 pos = player.transform.position;

            // Check for custom water
            var water = CustomWaterManager.GetWaterAt(pos);
            if (water != null)
            {
                float level = water.GetWaterSurfaceAt(pos);
                args.Context.AddString($"Custom Water: {water.GetWaterType()}");
                args.Context.AddString($"  Position: {water.transform.position}");
                args.Context.AddString($"  Bounds: {water.BoundsSize}");
                args.Context.AddString($"  Water level: {level:F2}");
                args.Context.AddString($"  Player depth: {level - pos.y:F2}m");
                args.Context.AddString($"  Liquid type: {water.LiquidType}");

                if (water is CustomRiverController river)
                {
                    args.Context.AddString($"  Flow direction: {river.FlowDirection}\u00B0");
                    args.Context.AddString($"  Flow speed: {river.FlowSpeed} m/s");
                    args.Context.AddString($"  Flow force: {river.GetFlowForceAt(pos)}");
                }
            }
            else
            {
                args.Context.AddString("No custom water at player position");
            }

            // Also show combined water level
            float combinedLevel = WaterBoatSupport.GetCombinedWaterLevel(pos);
            args.Context.AddString($"Combined water level (vanilla+custom): {combinedLevel:F2}");
        }

        private static void ListWaterCommand(Terminal.ConsoleEventArgs args)
        {
            args.Context.AddString($"=== Custom Water Instances ===");
            args.Context.AddString($"Total: {CustomWaterManager.AllWater.Count}");
            args.Context.AddString($"  Ponds: {CustomWaterManager.Ponds.Count}");
            args.Context.AddString($"  Rivers: {CustomWaterManager.Rivers.Count}");
            args.Context.AddString($"  Waterfalls: {CustomWaterManager.Waterfalls.Count}");

            foreach (var water in CustomWaterManager.AllWater)
            {
                if (water == null) continue;

                string typeChar = water.GetWaterType() switch
                {
                    CustomWaterType.Pond => "[P]",
                    CustomWaterType.River => "[R]",
                    CustomWaterType.Waterfall => "[W]",
                    _ => "[?]"
                };

                Vector3 waterPos = water.transform.position;
                args.Context.AddString($"  {typeChar} at ({waterPos.x:F0}, {waterPos.y:F0}, {waterPos.z:F0}) - {water.BoundsSize}");
            }
        }

        #endregion

        #region Material Debug Commands

        private static void WaterMaterialInfoCommand(Terminal.ConsoleEventArgs args)
        {
            args.Context.AddString(WaterMaterialManager.GetDebugInfo());
            args.Context.AddString($"Has valid water material: {WaterMaterialManager.HasValidMaterials()}");
            args.Context.AddString($"Has valid tar material: {WaterMaterialManager.HasValidTarMaterial()}");
        }

        private static void WaterRefreshMaterialsCommand(Terminal.ConsoleEventArgs args)
        {
            WaterMaterialManager.RefreshMaterials();
            args.Context.AddString("Materials refreshed");
            args.Context.AddString(WaterMaterialManager.GetDebugInfo());

            // Also refresh any existing water combiners
            var player = Player.m_localPlayer;
            if (player != null)
            {
                var combiners = WaterMeshCombiner.GetCombinersInRadius(player.transform.position, 200f);
                foreach (var combiner in combiners)
                {
                    combiner.ForceRegenerate();
                }
                args.Context.AddString($"Regenerated {combiners.Count} nearby water combiner(s)");
            }
        }

        /// <summary>
        /// Cleanup command to remove duplicate water anchor ZDOs.
        /// This fixes persistence issues caused by multiple anchors being created at similar positions.
        /// 
        /// How it works:
        /// 1. Finds all GameObjects with WaterMeshCombinerAnchor component
        /// 2. Groups them by position (within 2m tolerance)
        /// 3. Keeps only ONE anchor per group (the one with the most data)
        /// 4. Destroys the duplicate anchors
        /// </summary>
        private static bool CleanupWaterAnchorsCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No player found");
                return false;
            }

            float searchRadius = 200f; // Search within 200m
            if (args.Length > 1)
            {
                float.TryParse(args[1], out searchRadius);
            }

            Vector3 playerPos = player.transform.position;

            // Find all anchor components in loaded scene
            var allAnchors = UnityEngine.Object.FindObjectsByType<WaterMeshCombinerAnchor>(UnityEngine.FindObjectsSortMode.None);

            Console.instance.Print($"Found {allAnchors.Length} water anchor component(s) in scene");

            // Filter to those within range
            var nearbyAnchors = new System.Collections.Generic.List<WaterMeshCombinerAnchor>();
            foreach (var anchor in allAnchors)
            {
                if (anchor == null) continue;

                float dist = Vector3.Distance(anchor.transform.position, playerPos);
                if (dist <= searchRadius)
                {
                    nearbyAnchors.Add(anchor);
                }
            }

            Console.instance.Print($"Found {nearbyAnchors.Count} anchor(s) within {searchRadius}m");

            if (nearbyAnchors.Count == 0)
            {
                Console.instance.Print("No water anchors found to cleanup");
                return true;
            }

            // Group anchors by position (within 2m tolerance)
            var groups = new System.Collections.Generic.List<System.Collections.Generic.List<WaterMeshCombinerAnchor>>();
            var processed = new System.Collections.Generic.HashSet<WaterMeshCombinerAnchor>();

            foreach (var anchor in nearbyAnchors)
            {
                if (processed.Contains(anchor)) continue;

                var group = new System.Collections.Generic.List<WaterMeshCombinerAnchor> { anchor };
                processed.Add(anchor);

                // Find other anchors within 2m of this one
                foreach (var other in nearbyAnchors)
                {
                    if (processed.Contains(other)) continue;

                    float dist = Vector3.Distance(anchor.transform.position, other.transform.position);
                    if (dist < 2f)
                    {
                        group.Add(other);
                        processed.Add(other);
                    }
                }

                groups.Add(group);
            }

            Console.instance.Print($"Grouped into {groups.Count} position group(s)");

            // Process each group - keep the best anchor, destroy duplicates
            int destroyed = 0;
            int kept = 0;

            foreach (var group in groups)
            {
                if (group.Count <= 1)
                {
                    // Only one anchor in this group - nothing to cleanup
                    kept++;
                    continue;
                }

                Console.instance.Print($"Group at {group[0].transform.position:F1} has {group.Count} anchors (duplicates!)");

                // Find the "best" anchor - the one with a linked combiner or most data
                WaterMeshCombinerAnchor bestAnchor = null;
                int bestScore = -1;

                foreach (var anchor in group)
                {
                    int score = 0;

                    // Anchor with linked combiner gets highest priority
                    if (anchor.Combiner != null)
                    {
                        score += 1000;
                    }

                    // Check ZDO data size
                    var nview = anchor.GetComponent<ZNetView>();
                    if (nview != null && nview.GetZDO() != null)
                    {
                        var zdo = nview.GetZDO();

                        // Check for grid data
                        var gridData = zdo.GetByteArray("va_water_grid_data".GetStableHashCode());
                        if (gridData != null) score += gridData.Length;

                        // Check for surface heights
                        var surfaceData = zdo.GetByteArray("va_water_surface_heights".GetStableHashCode());
                        if (surfaceData != null) score += surfaceData.Length;

                        // Check for disks
                        var diskData = zdo.GetByteArray("va_water_disks".GetStableHashCode());
                        if (diskData != null) score += diskData.Length;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAnchor = anchor;
                    }
                }

                // Destroy all but the best anchor
                foreach (var anchor in group)
                {
                    if (anchor == bestAnchor)
                    {
                        kept++;
                        Console.instance.Print($"  Keeping anchor at {anchor.transform.position:F1} (score: {bestScore})");
                        continue;
                    }

                    Console.instance.Print($"  Destroying duplicate anchor at {anchor.transform.position:F1}");

                    // Use DestroyPermanently which clears the ZDO data
                    anchor.DestroyPermanently();
                    destroyed++;
                }
            }

            Console.instance.Print($"Cleanup complete: kept {kept} anchor(s), destroyed {destroyed} duplicate(s)");

            // Save the world to commit changes
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                Console.instance.Print("Saving world to commit cleanup...");
                ZNet.instance.Save(true);
            }
            else
            {
                Console.instance.Print("Note: Run this command on the server/host to persist changes");
            }

            return true;
        }

        #endregion

        #region Auto Water Generation Commands

        private static void AutoWaterStatusCommand(Terminal.ConsoleEventArgs args)
        {
            float waterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            bool hasRiverWeight = WorldGeneratorAccess.HasRiverWeightAccess;
            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            args.Context.AddString("=== Auto Water Generation ===");
            args.Context.AddString($"  This peer: {(isServer ? "SERVER/HOST (generates directly)" : "CLIENT (sends request to server via RPC)")}");
            args.Context.AddString($"  Status: {(AutoWaterGenerator.IsGenerating ? "GENERATING..." : "Idle")}");
            args.Context.AddString($"  Global water level: {waterLevel}");
            args.Context.AddString($"  GetRiverWeight available: {hasRiverWeight}");
            args.Context.AddString($"  Rivers enabled: {AutoWaterGenerator.EnableRivers}");
            args.Context.AddString($"  Streams enabled: {AutoWaterGenerator.EnableStreams}");
            args.Context.AddString($"  Lakes enabled: {AutoWaterGenerator.EnableLakes}");
            args.Context.AddString($"  River sample step: {AutoWaterGenerator.RiverSampleStep}m");
            args.Context.AddString($"  River max depth: {AutoWaterGenerator.RiverMaxDepth}m");
            args.Context.AddString($"  Stream max depth: {AutoWaterGenerator.StreamMaxDepth}m");
            args.Context.AddString($"  Lake max depth: {AutoWaterGenerator.LakeMaxDepth}m");
            args.Context.AddString($"  River surface offset: {AutoWaterGenerator.RiverSurfaceOffset}m");
            args.Context.AddString($"  Prefer heightmap detection: {AutoWaterGenerator.PreferHeightmapDetection}");
            args.Context.AddString($"  {BCHeightmapWaterDetector.GetStatusString()}");
            args.Context.AddString($"  WorldHeightCache: {(WorldHeightCache.HasCache ? $"built ({WorldHeightCache.CachedSnapshot?.Size}x{WorldHeightCache.CachedSnapshot?.Size}, biome={WorldHeightCache.CachedBiomes != null})" : WorldHeightCache.IsBuilding ? "building..." : "not built")}");
            args.Context.AddString($"  Last run: {AutoWaterGenerator.GetGenerationSummary()}");
            args.Context.AddString($"  WorldGen data: {AutoWaterGenerator.CachedRiverCount} rivers, {AutoWaterGenerator.CachedStreamCount} streams, {AutoWaterGenerator.CachedLakeCount} lakes");
            args.Context.AddString($"  Active combiners: {WaterMeshCombiner.AllCombiners.Count}");
            args.Context.AddString("  Run 'va_auto_water_generate' to start generation (clients send request to server)");
        }

        private static void AutoWaterGenerateCommand(Terminal.ConsoleEventArgs args)
        {
            if (WorldGenerator.instance == null)
            {
                args.Context.AddString("WorldGenerator not available");
                return;
            }

            // If we're a client, send an RPC to the server requesting generation.
            // The server will run GenerateFromWorldData() and create anchor ZDOs
            // that sync to all clients automatically.
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                AutoWaterPatches.RequestAutoWaterGeneration();
                args.Context.AddString("Sent auto water generation request to server. " +
                    "Water will appear via ZDO sync once the server finishes generating.");
                return;
            }

            // We are the server/host.
            // STEP 1: Wipe ALL existing custom water first (clean slate)
            int combinersBefore = WaterMeshCombiner.AllCombiners != null ? WaterMeshCombiner.AllCombiners.Count : 0;

            if (combinersBefore > 0 || AutoWaterGenerator.IsGenerating)
            {
                args.Context.AddString("Clearing existing custom water first...");

                int loadedDestroyed, zdoDestroyed;
                AutoWaterPatches.ClearAllWaterInternal(out loadedDestroyed, out zdoDestroyed);

                int combinersAfter = WaterMeshCombiner.AllCombiners != null ? WaterMeshCombiner.AllCombiners.Count : 0;
                args.Context.AddString($"  Cleared: {loadedDestroyed} loaded, {zdoDestroyed} ZDOs, combiners {combinersBefore} -> {combinersAfter}");
            }

            // STEP 2: Generate fresh water
            var lakes = WorldGeneratorAccess.GetLakes(WorldGenerator.instance);
            var rivers = WorldGeneratorAccess.GetRivers(WorldGenerator.instance);
            var streams = WorldGeneratorAccess.GetStreams(WorldGenerator.instance);

            AutoWaterGenerator.GenerateFromWorldData(lakes, rivers, streams);

            args.Context.AddString($"Started auto water generation (clean slate): " +
                $"{rivers?.Count ?? 0} rivers, " +
                $"{streams?.Count ?? 0} streams, " +
                $"{lakes?.Count ?? 0} lakes");
        }

        private static void ClearAutoWaterCommand(Terminal.ConsoleEventArgs args)
        {
            if (ZNet.instance == null)
            {
                args.Context.AddString("Not connected to a world");
                return;
            }

            // If we're a client, send an RPC to the server requesting clear.
            // The server will destroy all water anchors/ZDOs and save.
            if (!ZNet.instance.IsServer())
            {
                AutoWaterPatches.RequestClearAutoWater();
                args.Context.AddString("Sent clear auto water request to server. " +
                    "Water will be removed once the server finishes processing.");
                return;
            }

            // We are the server/host - run clear directly.
            int combinersBefore = WaterMeshCombiner.AllCombiners != null ? WaterMeshCombiner.AllCombiners.Count : 0;

            int loadedDestroyed, zdoDestroyed;
            AutoWaterPatches.ClearAllWaterInternal(out loadedDestroyed, out zdoDestroyed);

            int combinersAfter = WaterMeshCombiner.AllCombiners != null ? WaterMeshCombiner.AllCombiners.Count : 0;

            args.Context.AddString($"=== Clear Complete ===");
            args.Context.AddString($"  Loaded objects destroyed: {loadedDestroyed}");
            args.Context.AddString($"  Unloaded ZDOs destroyed: {zdoDestroyed}");
            args.Context.AddString($"  Combiners: {combinersBefore} -> {combinersAfter}");
            args.Context.AddString($"  World saved.");

            if (loadedDestroyed == 0 && zdoDestroyed == 0)
            {
                args.Context.AddString("  (No custom water found to clear)");
            }
        }

        private static void PingRiversCommand(Terminal.ConsoleEventArgs args)
        {
            if (WorldGenerator.instance == null)
            {
                args.Context.AddString("No river data available");
                return;
            }
            var riverList = WorldGeneratorAccess.GetRivers(WorldGenerator.instance);
            if (riverList == null)
            {
                args.Context.AddString("No river data available");
                return;
            }

            int count = riverList.Count;
            args.Context.AddString($"Vanilla rivers: {count}");

            foreach (var river in riverList)
            {
                Vector3 center = new Vector3(river.center.x, 30f, river.center.y);
                args.Context.AddString($"  River at ({river.center.x:F0}, {river.center.y:F0}) " +
                    $"width {river.widthMin:F0}-{river.widthMax:F0}m, " +
                    $"length {(river.p1 - river.p0).magnitude:F0}m");

                if (Minimap.instance != null)
                {
                    Minimap.instance.AddPin(center, Minimap.PinType.Icon3, "River", false, false);
                }
            }
        }

        private static void PingStreamsCommand(Terminal.ConsoleEventArgs args)
        {
            if (WorldGenerator.instance == null)
            {
                args.Context.AddString("No stream data available");
                return;
            }
            var streamList = WorldGeneratorAccess.GetStreams(WorldGenerator.instance);
            if (streamList == null)
            {
                args.Context.AddString("No stream data available");
                return;
            }

            int count = streamList.Count;
            args.Context.AddString($"Vanilla streams: {count}");

            foreach (var stream in streamList)
            {
                Vector3 center = new Vector3(stream.center.x, 30f, stream.center.y);
                args.Context.AddString($"  Stream at ({stream.center.x:F0}, {stream.center.y:F0}) " +
                    $"width {stream.widthMin:F0}-{stream.widthMax:F0}m");

                if (Minimap.instance != null)
                {
                    Minimap.instance.AddPin(center, Minimap.PinType.Icon2, "Stream", false, false);
                }
            }
        }

        private static void PingLakesCommand(Terminal.ConsoleEventArgs args)
        {
            if (WorldGenerator.instance == null)
            {
                args.Context.AddString("No lake data available");
                return;
            }
            var lakeList = WorldGeneratorAccess.GetLakes(WorldGenerator.instance);
            if (lakeList == null)
            {
                args.Context.AddString("No lake data available");
                return;
            }

            int count = lakeList.Count;
            args.Context.AddString($"Vanilla lakes: {count}");

            foreach (var lake in lakeList)
            {
                Vector3 center = new Vector3(lake.x, 30f, lake.y);
                args.Context.AddString($"  Lake at ({lake.x:F0}, {lake.y:F0})");

                if (Minimap.instance != null)
                {
                    Minimap.instance.AddPin(center, Minimap.PinType.Icon1, "Lake", false, false);
                }
            }
        }

        // Cached list for va_find_water teleportation
        private static List<WaterMeshCombiner> _lastFoundWater = new List<WaterMeshCombiner>();

        private static void FindWaterCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                args.Context.AddString("No player found");
                return;
            }

            // Check for teleport sub-command: va_find_water tp <index>
            if (args.Length >= 3 && args[1].ToLower() == "tp")
            {
                int tpIdx;
                if (int.TryParse(args[2], out tpIdx) && tpIdx >= 0 && tpIdx < _lastFoundWater.Count)
                {
                    var target = _lastFoundWater[tpIdx];
                    if (target != null)
                    {
                        Vector3 tpPos = target.transform.position;
                        tpPos.y += 5f; // Slightly above to avoid clipping into water
                        player.TeleportTo(tpPos, player.transform.rotation, true);
                        args.Context.AddString($"Teleported to water #{tpIdx} at ({tpPos.x:F0}, {tpPos.y:F0}, {tpPos.z:F0})");
                    }
                    else
                    {
                        args.Context.AddString($"Water #{tpIdx} no longer exists. Run va_find_water again.");
                    }
                }
                else
                {
                    args.Context.AddString($"Invalid index. Use va_find_water first, then va_find_water tp <index>");
                }
                return;
            }

            float searchRadius = 500f;
            if (args.Length >= 2)
                float.TryParse(args[1], out searchRadius);

            Vector3 playerPos = player.transform.position;

            var combiners = WaterMeshCombiner.GetCombinersInRadius(playerPos, searchRadius);

            if (combiners.Count == 0)
            {
                int totalCombiners = WaterMeshCombiner.AllCombiners != null ? WaterMeshCombiner.AllCombiners.Count : 0;
                args.Context.AddString($"No custom water found within {searchRadius}m (XZ). " +
                    $"Total combiners in world: {totalCombiners}.");

                if (totalCombiners > 0)
                {
                    // Show nearest combiner to help diagnose
                    float nearestDist = float.MaxValue;
                    WaterMeshCombiner nearest = null;
                    foreach (var c in WaterMeshCombiner.AllCombiners)
                    {
                        if (c == null) continue;
                        Vector3 cp = c.transform.position;
                        float dx = cp.x - playerPos.x;
                        float dz = cp.z - playerPos.z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d < nearestDist) { nearestDist = d; nearest = c; }
                    }
                    if (nearest != null)
                    {
                        Vector3 np = nearest.transform.position;
                        int nDisks = nearest.WaterDisks != null ? nearest.WaterDisks.Count : 0;
                        args.Context.AddString($"  Nearest combiner: {nearestDist:F0}m away at ({np.x:F0}, {np.y:F0}, {np.z:F0}), {nDisks} disks");
                    }
                    args.Context.AddString($"  Try: va_find_water {(int)(nearestDist + 100)}");
                }
                else
                {
                    args.Context.AddString("  Run va_auto_water_generate first");
                }
                _lastFoundWater.Clear();
                return;
            }

            // Sort by XZ distance
            combiners.Sort((a, b) =>
            {
                float dxA = a.transform.position.x - playerPos.x;
                float dzA = a.transform.position.z - playerPos.z;
                float distA = dxA * dxA + dzA * dzA;
                float dxB = b.transform.position.x - playerPos.x;
                float dzB = b.transform.position.z - playerPos.z;
                float distB = dxB * dxB + dzB * dzB;
                return distA.CompareTo(distB);
            });

            // Cap display to 20 nearest
            int displayCount = Math.Min(combiners.Count, 20);

            args.Context.AddString($"=== Nearest Custom Water ({combiners.Count} total within {searchRadius}m) ===");

            _lastFoundWater.Clear();
            _lastFoundWater.AddRange(combiners);

            for (int i = 0; i < displayCount; i++)
            {
                var c = combiners[i];
                if (c == null) continue;

                Vector3 cPos = c.transform.position;
                float dxc = cPos.x - playerPos.x;
                float dzc = cPos.z - playerPos.z;
                float dist = Mathf.Sqrt(dxc * dxc + dzc * dzc);
                int diskCount = c.WaterDisks != null ? c.WaterDisks.Count : 0;
                string liquidName = c.CustomLiquidType.GetDisplayName();

                args.Context.AddString($"  [{i}] {dist:F0}m away - {liquidName}, {diskCount} disks, " +
                    $"pos ({cPos.x:F0}, {cPos.y:F0}, {cPos.z:F0})");
            }

            if (combiners.Count > displayCount)
            {
                args.Context.AddString($"  ... and {combiners.Count - displayCount} more");
            }

            args.Context.AddString($"Use 'va_find_water tp <index>' to teleport (e.g. 'va_find_water tp 0')");
        }

        private static void BuildHeightCacheCommand(Terminal.ConsoleEventArgs args)
        {
            if (WorldHeightCache.IsBuilding)
            {
                args.Context.AddString("WorldHeightCache build already in progress...");
                return;
            }

            if (WorldGenerator.instance == null)
            {
                args.Context.AddString("WorldGenerator not available - must be in-game");
                return;
            }

            // If we're a client, send an RPC to the server so the server builds its own cache.
            // The cache file is saved to the local filesystem, so the server must build it
            // on the server's machine - a client-built cache is inaccessible to the server.
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                AutoWaterPatches.RequestBuildHeightCache();
                args.Context.AddString("Sent height cache build request to server. " +
                    "The server will build and save the cache on its own filesystem. " +
                    "Check server console for progress.");

                // Also build locally so the client has it for status/debug commands
                args.Context.AddString("Also building local copy...");
            }

            // Re-apply config in case the user edited the cfg file mid-session
            // (the file watcher only works on servers/dedicated)
            try
            {
                var cfgMgr = VerdantsAscent.Managers.ConfigManager.Instance;
                if (cfgMgr?.configHeightCacheResolution != null)
                {
                    int cfgVal = cfgMgr.configHeightCacheResolution.Value;
                    WorldHeightCache.GridSize = Mathf.Clamp(cfgVal, 1024, 20480);
                    FiresLogger.LogInfo($"[va_build_height_cache] Re-read config: HeightCacheResolution={cfgVal}, GridSize now={WorldHeightCache.GridSize}");
                }
                if (cfgMgr?.configHeightCacheSamplesPerFrame != null)
                {
                    WorldHeightCache.SamplesPerFrame = Mathf.Clamp(cfgMgr.configHeightCacheSamplesPerFrame.Value, 500, 20000);
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[va_build_height_cache] Failed to re-read config: {ex.Message}");
            }

            // Parse optional grid size argument (overrides config)
            if (args.Length > 1 && int.TryParse(args[1], out int gridSize))
            {
                gridSize = Mathf.Clamp(gridSize, 256, 20480);
                WorldHeightCache.GridSize = gridSize;
            }

            args.Context.AddString($"Building WorldHeightCache ({WorldHeightCache.GridSize}x{WorldHeightCache.GridSize} grid, " +
                $"{WorldHeightCache.SamplesPerFrame} samples/frame, biome={WorldHeightCache.IncludeBiomeData})...");
            args.Context.AddString("This samples WorldGenerator.GetHeight() across the entire world.");
            args.Context.AddString("The cache includes ALL BC modifications (roughmap, flatmap, noise, river carving).");
            args.Context.AddString("Check console (F5) for progress updates.");

            var host = Game.instance != null ? (MonoBehaviour)Game.instance : (MonoBehaviour)ZoneSystem.instance;
            if (host == null)
            {
                args.Context.AddString("No coroutine host available");
                return;
            }

            host.StartCoroutine(WorldHeightCache.BuildCacheCoroutine(snap =>
            {
                if (snap != null)
                    FiresLogger.LogInfo($"[va_build_height_cache] Cache built successfully: {snap.Size}x{snap.Size}, " +
                        $"IsWorldHeightCache={snap.IsWorldHeightCache}");
                else
                    FiresLogger.LogWarning("[va_build_height_cache] Cache build failed");
            }));
        }

        private static void DumpWorldHeightCacheCommand(Terminal.ConsoleEventArgs args)
        {
            if (WorldHeightCache.IsBuilding)
            {
                args.Context.AddString("WorldHeightCache is currently building. Please wait and try again.");
                return;
            }

            if (!WorldHeightCache.HasCache)
            {
                // Auto-build the cache instead of just telling the user to run another command
                if (WorldGenerator.instance == null)
                {
                    args.Context.AddString("WorldGenerator not available - must be in-game to build height cache.");
                    return;
                }

                // Re-apply config in case the user edited the cfg file mid-session
                try
                {
                    var cfgMgr = VerdantsAscent.Managers.ConfigManager.Instance;
                    if (cfgMgr?.configHeightCacheResolution != null)
                    {
                        int cfgVal = cfgMgr.configHeightCacheResolution.Value;
                        WorldHeightCache.GridSize = Mathf.Clamp(cfgVal, 1024, 20480);
                    }
                    if (cfgMgr?.configHeightCacheSamplesPerFrame != null)
                    {
                        WorldHeightCache.SamplesPerFrame = Mathf.Clamp(cfgMgr.configHeightCacheSamplesPerFrame.Value, 500, 20000);
                    }
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[va_dump_world_heightcache] Failed to re-read config: {ex.Message}");
                }

                args.Context.AddString($"WorldHeightCache not built yet - building now ({WorldHeightCache.GridSize}x{WorldHeightCache.GridSize} grid)...");
                args.Context.AddString("This samples WorldGenerator.GetHeight() across the entire world.");
                args.Context.AddString("Check console (F5) for progress. The dump will run automatically when done.");

                var host = Game.instance != null ? (MonoBehaviour)Game.instance : (MonoBehaviour)ZoneSystem.instance;
                if (host == null)
                {
                    args.Context.AddString("No coroutine host available");
                    return;
                }

                host.StartCoroutine(WorldHeightCache.BuildCacheCoroutine(snap =>
                {
                    if (snap != null)
                    {
                        FiresLogger.LogInfo($"[va_dump_world_heightcache] Cache built, now exporting...");
                        ExportWorldHeightCache(snap);
                    }
                    else
                    {
                        FiresLogger.LogWarning("[va_dump_world_heightcache] Cache build failed - cannot export");
                    }
                }));
                return;
            }

            var snapshot = WorldHeightCache.CachedSnapshot;
            if (snapshot == null || snapshot.Map == null)
            {
                args.Context.AddString("WorldHeightCache is empty");
                return;
            }

            ExportWorldHeightCache(snapshot);

            // Print summary to console since ExportWorldHeightCache only logs to Debug
            args.Context.AddString($"Exported WorldHeightCache to Debug folder. Check console (F5) for file paths.");
        }

        /// <summary>
        /// Exports the given HeightmapSnapshot to PNG files:
        /// 1. Grayscale heightmap
        /// 2. Colorized heightmap (terrain + water level highlight)
        /// 3. Depression map (water candidate detection)
        /// 4. Biome color map (if biome data is available - uses correct Valheim biome colors)
        /// Extracted so it can be called both directly and from the build-cache callback.
        /// </summary>
        private static void ExportWorldHeightCache(BCHeightmapWaterDetector.HeightmapSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Map == null)
            {
                FiresLogger.LogWarning("[ExportWorldHeightCache] Snapshot is null or empty");
                return;
            }

            int size = snapshot.Size;

            // Unity caps Texture2D dimensions at 16384.  When the cache is
            // larger (e.g. 20480) we export at the max supported size and
            // map output pixels back into the full-resolution source data.
            const int MaxTextureSize = 16384;
            int outputSize = Mathf.Min(size, MaxTextureSize);
            if (outputSize < size)
            {
                PrintConsole($"Source {size}x{size} exceeds GPU max {MaxTextureSize}. Exporting at {outputSize}x{outputSize}.");
            }

            string debugDir = Path.Combine(BepInEx.Paths.ConfigPath, "FiresAdminTerrain", "Debug");
            Directory.CreateDirectory(debugDir);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // --- Find min/max for normalization ---
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int i = 0; i < snapshot.Map.Length; i++)
            {
                float h = snapshot.Map[i];
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
            float range = maxH - minH;
            if (range < 0.01f) range = 0.01f;

            float waterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;

            PrintConsole($"Exporting WorldHeightCache ({outputSize}x{outputSize}, source {size}x{size})...");
            PrintConsole($"Height range: {minH:F1} to {maxH:F1} (water level: {waterLevel:F1})");

            // === 1. GRAYSCALE HEIGHTMAP ===
            float srcStep = (float)(size - 1) / (outputSize - 1);
            var heightmapTex = new Texture2D(outputSize, outputSize, TextureFormat.RGB24, false);
            for (int y = 0; y < outputSize; y++)
            {
                for (int x = 0; x < outputSize; x++)
                {
                    int srcX = Mathf.Min((int)(x * srcStep), size - 1);
                    int srcY = Mathf.Min((int)(y * srcStep), size - 1);
                    float h = snapshot.Map[srcY * size + srcX];
                    float norm = (h - minH) / range;
                    heightmapTex.SetPixel(x, y, new Color(norm, norm, norm));
                }
            }
            heightmapTex.Apply();
            byte[] heightmapPng = heightmapTex.EncodeToPNG();
            UnityEngine.Object.Destroy(heightmapTex);
            string heightmapPath = Path.Combine(debugDir, $"world_heightcache_{timestamp}.png");
            File.WriteAllBytes(heightmapPath, heightmapPng);
            PrintConsole($"Saved grayscale heightmap: {heightmapPath}");

            // === 2. COLORIZED HEIGHTMAP (terrain + water level highlight) ===
            var colorTex = new Texture2D(outputSize, outputSize, TextureFormat.RGB24, false);
            for (int y = 0; y < outputSize; y++)
            {
                for (int x = 0; x < outputSize; x++)
                {
                    int srcX = Mathf.Min((int)(x * srcStep), size - 1);
                    int srcY = Mathf.Min((int)(y * srcStep), size - 1);
                    float h = snapshot.Map[srcY * size + srcX];

                    Color pixel;
                    if (h < waterLevel - 5f)
                    {
                        // Deep underwater - dark blue
                        float depth01 = Mathf.Clamp01((waterLevel - h) / 60f);
                        pixel = new Color(0f, 0.05f, 0.2f + 0.4f * (1f - depth01));
                    }
                    else if (h < waterLevel)
                    {
                        // Near water surface - lighter blue
                        float t = Mathf.Clamp01((waterLevel - h) / 5f);
                        pixel = Color.Lerp(new Color(0.2f, 0.5f, 0.3f), new Color(0.1f, 0.3f, 0.7f), t);
                    }
                    else if (h < waterLevel + 30f)
                    {
                        // Low terrain - green
                        float t = (h - waterLevel) / 30f;
                        pixel = Color.Lerp(new Color(0.2f, 0.5f, 0.15f), new Color(0.4f, 0.55f, 0.2f), t);
                    }
                    else if (h < waterLevel + 80f)
                    {
                        // Mid terrain - brown/gray
                        float t = (h - waterLevel - 30f) / 50f;
                        pixel = Color.Lerp(new Color(0.4f, 0.35f, 0.2f), new Color(0.5f, 0.5f, 0.45f), t);
                    }
                    else
                    {
                        // High terrain / mountains - white/gray
                        float t = Mathf.Clamp01((h - waterLevel - 80f) / 80f);
                        pixel = Color.Lerp(new Color(0.5f, 0.5f, 0.45f), new Color(0.9f, 0.9f, 0.9f), t);
                    }

                    colorTex.SetPixel(x, y, pixel);
                }
            }
            colorTex.Apply();
            byte[] colorPng = colorTex.EncodeToPNG();
            UnityEngine.Object.Destroy(colorTex);
            string colorPath = Path.Combine(debugDir, $"world_heightcache_color_{timestamp}.png");
            File.WriteAllBytes(colorPath, colorPng);
            PrintConsole($"Saved colorized heightmap: {colorPath}");

            // === 3. DEPRESSION MAP ===
            PrintConsole("Computing depression map...");

            int blurRadius = BCHeightmapWaterDetector.NeighborhoodRadius;
            if (blurRadius < 2) blurRadius = 2;

            // Horizontal blur pass
            float[] temp = new float[size * size];
            for (int y2 = 0; y2 < size; y2++)
            {
                float bsum = 0f; int bcnt = 0;
                int rowOff = y2 * size;
                for (int x2 = 0; x2 <= blurRadius && x2 < size; x2++) { bsum += snapshot.Map[rowOff + x2]; bcnt++; }
                temp[rowOff] = bsum / bcnt;
                for (int x2 = 1; x2 < size; x2++)
                {
                    int addX = x2 + blurRadius; if (addX < size) { bsum += snapshot.Map[rowOff + addX]; bcnt++; }
                    int remX = x2 - blurRadius - 1; if (remX >= 0) { bsum -= snapshot.Map[rowOff + remX]; bcnt--; }
                    temp[rowOff + x2] = bsum / bcnt;
                }
            }
            // Vertical blur pass
            float[] blurred = new float[size * size];
            for (int x2 = 0; x2 < size; x2++)
            {
                float bsum = 0f; int bcnt = 0;
                for (int y2 = 0; y2 <= blurRadius && y2 < size; y2++) { bsum += temp[y2 * size + x2]; bcnt++; }
                blurred[x2] = bsum / bcnt;
                for (int y2 = 1; y2 < size; y2++)
                {
                    int addY = y2 + blurRadius; if (addY < size) { bsum += temp[addY * size + x2]; bcnt++; }
                    int remY = y2 - blurRadius - 1; if (remY >= 0) { bsum -= temp[remY * size + x2]; bcnt--; }
                    blurred[y2 * size + x2] = bsum / bcnt;
                }
            }

            // Depression threshold - for world height cache, values are in meters
            // so we scale the normalized threshold to meters
            float minDepth = snapshot.IsWorldHeightCache
                ? BCHeightmapWaterDetector.MinDepressionDepth * 200f  // ~1.6m
                : BCHeightmapWaterDetector.MinDepressionDepth;

            // Find max depression for normalization
            float maxDep = 0f;
            for (int i = 0; i < size * size; i++)
            {
                float dep = blurred[i] - snapshot.Map[i];
                if (dep > maxDep) maxDep = dep;
            }
            if (maxDep < 0.001f) maxDep = 0.001f;

            var depressionTex = new Texture2D(outputSize, outputSize, TextureFormat.RGB24, false);
            for (int y = 0; y < outputSize; y++)
            {
                for (int x = 0; x < outputSize; x++)
                {
                    int srcX = Mathf.Min((int)(x * srcStep), size - 1);
                    int srcY = Mathf.Min((int)(y * srcStep), size - 1);
                    int srcIdx = srcY * size + srcX;
                    float localH = snapshot.Map[srcIdx];
                    float avgH = blurred[srcIdx];
                    float dep = avgH - localH;

                    Color pixel;
                    if (dep > minDepth)
                    {
                        // Detected depression - blue (water candidate)
                        float intensity = Mathf.Clamp01(dep / maxDep);
                        pixel = new Color(0f, 0.2f * intensity, intensity);
                    }
                    else if (dep > 0f)
                    {
                        // Slight depression below threshold - gray
                        float g = 0.3f + 0.2f * Mathf.Clamp01(dep / minDepth);
                        pixel = new Color(g, g, g);
                    }
                    else
                    {
                        // Not depressed - dark terrain
                        float norm = Mathf.Clamp01((localH - minH) / range);
                        float g = 0.1f + 0.2f * norm;
                        pixel = new Color(g, g, g);
                    }

                    depressionTex.SetPixel(x, y, pixel);
                }
            }
            depressionTex.Apply();
            byte[] depressionPng = depressionTex.EncodeToPNG();
            UnityEngine.Object.Destroy(depressionTex);
            string depressionPath = Path.Combine(debugDir, $"world_depression_map_{timestamp}.png");
            File.WriteAllBytes(depressionPath, depressionPng);
            PrintConsole($"Saved depression map: {depressionPath}");

            PrintConsole($"Depression threshold: {minDepth:F2}m, max depression: {maxDep:F2}m");
            PrintConsole($"Blue = detected depression (water candidate), darker blue = deeper");

            // === 4. BIOME COLOR MAP (if biome data is available) ===
            int[] biomeData = WorldHeightCache.CachedBiomes;
            if (biomeData != null && biomeData.Length == size * size)
            {
                PrintConsole("Generating biome color map...");

                var biomeTex = new Texture2D(outputSize, outputSize, TextureFormat.RGB24, false);
                for (int y = 0; y < outputSize; y++)
                {
                    for (int x = 0; x < outputSize; x++)
                    {
                        int srcX = Mathf.Min((int)(x * srcStep), size - 1);
                        int srcY = Mathf.Min((int)(y * srcStep), size - 1);
                        int srcIdx = srcY * size + srcX;

                        Heightmap.Biome biome = (Heightmap.Biome)biomeData[srcIdx];
                        float h = snapshot.Map[srcIdx];

                        // Get the canonical Valheim biome color
                        Color biomeColor = GetBiomeColor(biome);

                        // Darken underwater areas to show ocean/lake depth
                        if (h < waterLevel)
                        {
                            float depthFactor = Mathf.Clamp01((waterLevel - h) / 40f);
                            biomeColor = Color.Lerp(biomeColor, new Color(0.02f, 0.05f, 0.15f), depthFactor * 0.8f);
                        }

                        // Subtle elevation shading to add terrain relief
                        float elevNorm = Mathf.Clamp01((h - waterLevel) / 200f);
                        float shadow = 0.7f + 0.3f * elevNorm;
                        biomeColor *= shadow;
                        biomeColor.a = 1f;

                        biomeTex.SetPixel(x, y, biomeColor);
                    }
                }
                biomeTex.Apply();
                byte[] biomePng = biomeTex.EncodeToPNG();
                UnityEngine.Object.Destroy(biomeTex);
                string biomePath = Path.Combine(debugDir, $"world_biome_map_{timestamp}.png");
                File.WriteAllBytes(biomePath, biomePng);
                PrintConsole($"Saved biome color map: {biomePath}");

                // Count biome pixel distribution
                var biomeCounts = new Dictionary<Heightmap.Biome, int>();
                for (int i = 0; i < biomeData.Length; i++)
                {
                    Heightmap.Biome b = (Heightmap.Biome)biomeData[i];
                    if (!biomeCounts.ContainsKey(b)) biomeCounts[b] = 0;
                    biomeCounts[b]++;
                }
                PrintConsole("Biome distribution:");
                foreach (var kv in biomeCounts)
                {
                    float pct = (float)kv.Value / biomeData.Length * 100f;
                    PrintConsole($"  {kv.Key}: {kv.Value} pixels ({pct:F1}%)");
                }
            }
            else
            {
                PrintConsole("Biome data not available (rebuild cache with IncludeBiomeData=true to generate biome map)");
            }

            PrintConsole($"Output directory: {debugDir}");
        }

        /// <summary>
        /// Returns the canonical Valheim biome color for map visualization.
        /// Colors match Valheim's minimap biome coloring as closely as possible.
        /// 
        /// Valheim biome enum values (from Heightmap.Biome):
        ///   None=0, Meadows=1, Swamp=2, Mountain=4, BlackForest=8,
        ///   Plains=16, AshLands=32, DeepNorth=64, Ocean=256, Mistlands=512
        /// </summary>
        private static Color GetBiomeColor(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows:
                    // Bright green - lush starter biome
                    return new Color(0.45f, 0.65f, 0.20f);

                case Heightmap.Biome.BlackForest:
                    // Dark forest green - dense conifer forests
                    return new Color(0.15f, 0.35f, 0.12f);

                case Heightmap.Biome.Swamp:
                    // Murky dark brown-green - fetid wetlands
                    return new Color(0.25f, 0.28f, 0.10f);

                case Heightmap.Biome.Mountain:
                    // Cool gray-white - snowy peaks
                    return new Color(0.75f, 0.78f, 0.82f);

                case Heightmap.Biome.Plains:
                    // Warm golden yellow - open grasslands
                    return new Color(0.72f, 0.68f, 0.30f);

                case Heightmap.Biome.Mistlands:
                    // Deep purple-gray - misty ancient forests
                    return new Color(0.35f, 0.25f, 0.40f);

                case Heightmap.Biome.AshLands:
                    // Volcanic red-orange - scorched earth
                    return new Color(0.55f, 0.20f, 0.10f);

                case Heightmap.Biome.DeepNorth:
                    // Icy pale blue-white - frozen tundra
                    return new Color(0.80f, 0.88f, 0.95f);

                case Heightmap.Biome.Ocean:
                    // Deep ocean blue
                    return new Color(0.08f, 0.15f, 0.45f);

                default:
                    // Heightmap.Biome.None or unknown - dark gray (edge of world)
                    return new Color(0.10f, 0.10f, 0.10f);
            }
        }

        /// <summary>
        /// Helper to print to both the in-game console and Debug.Log.
        /// Works even when called from a coroutine callback where Terminal.ConsoleEventArgs is unavailable.
        /// </summary>
        private static void PrintConsole(string message)
        {
            if (Console.instance != null)
                Console.instance.Print(message);
            FiresLogger.LogInfo($"[va_dump_world_heightcache] {message}");
        }

        private static void DumpHeightmapCommand(Terminal.ConsoleEventArgs args)
        {
            if (!BCHeightmapWaterDetector.CanReadHeightmap)
            {
                args.Context.AddString("BC heightmap reader not available (BC not loaded or no heightmap)");
                return;
            }

            args.Context.AddString("Reading BC heightmap...");

            var snapshot = BCHeightmapWaterDetector.ReadHeightmap();
            if (snapshot == null || snapshot.Map == null)
            {
                args.Context.AddString("Failed to read heightmap data");
                return;
            }

            // Unity caps Texture2D dimensions at 16384
            const int MaxTextureSize = 16384;
            int outputSize = Mathf.Min(snapshot.Size, MaxTextureSize);
            float hmSrcStep = (float)(snapshot.Size - 1) / (outputSize - 1);
            if (outputSize < snapshot.Size)
            {
                args.Context.AddString($"Source {snapshot.Size}x{snapshot.Size} exceeds GPU max {MaxTextureSize}. Exporting at {outputSize}x{outputSize}.");
            }

            string debugDir = Path.Combine(BepInEx.Paths.ConfigPath, "FiresAdminTerrain", "Debug");
            Directory.CreateDirectory(debugDir);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // --- Export raw heightmap ---
            args.Context.AddString($"Exporting heightmap ({outputSize}x{outputSize})...");

            var heightmapTex = new Texture2D(outputSize, outputSize, TextureFormat.RGB24, false);

            for (int y = 0; y < outputSize; y++)
            {
                for (int x = 0; x < outputSize; x++)
                {
                    int srcX = Mathf.Min((int)(x * hmSrcStep), snapshot.Size - 1);
                    int srcY = Mathf.Min((int)(y * hmSrcStep), snapshot.Size - 1);

                    float h = snapshot.Map[srcY * snapshot.Size + srcX];
                    heightmapTex.SetPixel(x, y, new Color(h, h, h));
                }
            }

            heightmapTex.Apply();
            byte[] heightmapPng = heightmapTex.EncodeToPNG();
            UnityEngine.Object.Destroy(heightmapTex);

            string heightmapPath = Path.Combine(debugDir, $"bc_heightmap_{timestamp}.png");
            File.WriteAllBytes(heightmapPath, heightmapPng);
            args.Context.AddString($"Saved: {heightmapPath}");

            // --- Export depression map ---
            args.Context.AddString("Computing depression map at full resolution...");

            // Run the same full-resolution box blur the detector uses.
            // This ensures the debug image is a pixel-perfect preview of what DetectWaterBodies sees.
            int fullSize = snapshot.Size;
            int blurRadius = BCHeightmapWaterDetector.NeighborhoodRadius;
            if (blurRadius < 2) blurRadius = 2;

            // Horizontal pass
            float[] temp = new float[fullSize * fullSize];
            for (int y2 = 0; y2 < fullSize; y2++)
            {
                float bsum = 0f; int bcnt = 0;
                int rowOff = y2 * fullSize;
                for (int x2 = 0; x2 <= blurRadius && x2 < fullSize; x2++) { bsum += snapshot.Map[rowOff + x2]; bcnt++; }
                temp[rowOff] = bsum / bcnt;
                for (int x2 = 1; x2 < fullSize; x2++)
                {
                    int addX = x2 + blurRadius; if (addX < fullSize) { bsum += snapshot.Map[rowOff + addX]; bcnt++; }
                    int remX = x2 - blurRadius - 1; if (remX >= 0) { bsum -= snapshot.Map[rowOff + remX]; bcnt--; }
                    temp[rowOff + x2] = bsum / bcnt;
                }
            }
            // Vertical pass
            float[] blurred = new float[fullSize * fullSize];
            for (int x2 = 0; x2 < fullSize; x2++)
            {
                float bsum = 0f; int bcnt = 0;
                for (int y2 = 0; y2 <= blurRadius && y2 < fullSize; y2++) { bsum += temp[y2 * fullSize + x2]; bcnt++; }
                blurred[x2] = bsum / bcnt;
                for (int y2 = 1; y2 < fullSize; y2++)
                {
                    int addY = y2 + blurRadius; if (addY < fullSize) { bsum += temp[addY * fullSize + x2]; bcnt++; }
                    int remY = y2 - blurRadius - 1; if (remY >= 0) { bsum -= temp[remY * fullSize + x2]; bcnt--; }
                    blurred[y2 * fullSize + x2] = bsum / bcnt;
                }
            }

            // Compute depression and render to texture
            float minDepth = BCHeightmapWaterDetector.MinDepressionDepth;
            var depressionTex = new Texture2D(outputSize, outputSize, TextureFormat.RGB24, false);

            // Find max depression for normalization
            float maxDep = 0f;
            for (int i = 0; i < fullSize * fullSize; i++)
            {
                float dep = blurred[i] - snapshot.Map[i];
                if (dep > maxDep) maxDep = dep;
            }

            if (maxDep < 0.001f) maxDep = 0.001f;

            for (int y = 0; y < outputSize; y++)
            {
                for (int x = 0; x < outputSize; x++)
                {
                    int srcX = Mathf.Min((int)(x * hmSrcStep), fullSize - 1);
                    int srcY = Mathf.Min((int)(y * hmSrcStep), fullSize - 1);

                    int srcIdx = srcY * fullSize + srcX;
                    float localH = snapshot.Map[srcIdx];
                    float avgH = blurred[srcIdx];
                    float dep = avgH - localH;

                    Color pixel;
                    if (dep > minDepth)
                    {
                        // Depressed pixel - show as blue (intensity = depression depth)
                        float intensity = Mathf.Clamp01(dep / maxDep);
                        pixel = new Color(0f, 0.2f * intensity, intensity);
                    }
                    else if (dep > 0f)
                    {
                        // Slightly depressed but below threshold - gray
                        float g = 0.3f + 0.2f * Mathf.Clamp01(dep / minDepth);
                        pixel = new Color(g, g, g);
                    }
                    else
                    {
                        // Not depressed - dark terrain
                        float g = 0.15f + 0.15f * Mathf.Clamp01(localH);
                        pixel = new Color(g, g, g);
                    }

                    depressionTex.SetPixel(x, y, pixel);
                }
            }

            depressionTex.Apply();
            byte[] depressionPng = depressionTex.EncodeToPNG();
            UnityEngine.Object.Destroy(depressionTex);

            string depressionPath = Path.Combine(debugDir, $"bc_depression_map_{timestamp}.png");
            File.WriteAllBytes(depressionPath, depressionPng);
            args.Context.AddString($"Saved: {depressionPath}");

            args.Context.AddString($"Heightmap: {snapshot.Size}x{snapshot.Size}, output: {outputSize}x{outputSize}");
            args.Context.AddString($"Depression threshold: {minDepth}, max depression found: {maxDep:F4}");
            args.Context.AddString($"Blue = detected depression (water candidate), darker = deeper");
        }

        #endregion

        #region Waterfall Terrain Paint Commands

        /// <summary>
        /// Paints waterfall texture on terrain using the lava paint approach but with water shader.
        /// This creates a visual waterfall effect on steep terrain faces.
        /// </summary>
        private static bool PaintWaterfallCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No player found");
                return false;
            }

            // Parse arguments - support IH format
            float radius = 5f;
            Vector3 position = GetPlacementPosition(player);

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg.Contains("="))
                {
                    string[] parts = arg.Split(new char[] { '=' }, 2);
                    string key = parts[0].ToLower();
                    string value = parts.Length > 1 ? parts[1] : "";

                    switch (key)
                    {
                        case "circle":
                            string[] radiusParts = value.Split('-');
                            if (radiusParts.Length > 0)
                                float.TryParse(radiusParts[0], out radius);
                            break;
                        case "from":
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float y = position.y;
                                if (coords.Length >= 3)
                                    float.TryParse(coords[2], out y);
                                position = new Vector3(x, y, z);
                            }
                            break;
                    }
                }
                else if (float.TryParse(arg, out float r))
                {
                    radius = r;
                }
            }

            // Paint waterfall using biome overlay system with a special "waterfall" biome
            // This will use the lava paint system but swap shader to water
            int painted = WaterfallTerrainPaint.PaintWaterfall(position, radius);

            Console.instance.Print($"Painted waterfall texture at {position:F1} with radius {radius}m ({painted} terrain cells)");
            return true;
        }

        /// <summary>
        /// Clears waterfall paint from terrain.
        /// </summary>
        private static bool ClearWaterfallPaintCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No player found");
                return false;
            }

            float radius = 5f;
            Vector3 position = GetPlacementPosition(player);

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg.Contains("="))
                {
                    string[] parts = arg.Split(new char[] { '=' }, 2);
                    string key = parts[0].ToLower();
                    string value = parts.Length > 1 ? parts[1] : "";

                    switch (key)
                    {
                        case "circle":
                            string[] radiusParts = value.Split('-');
                            if (radiusParts.Length > 0)
                                float.TryParse(radiusParts[0], out radius);
                            break;
                        case "from":
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float y = position.y;
                                if (coords.Length >= 3)
                                    float.TryParse(coords[2], out y);
                                position = new Vector3(x, y, z);
                            }
                            break;
                    }
                }
                else if (float.TryParse(arg, out float r))
                {
                    radius = r;
                }
            }

            int cleared = WaterfallTerrainPaint.ClearWaterfall(position, radius);

            Console.instance.Print($"Cleared waterfall paint at {position:F1} with radius {radius}m ({cleared} terrain cells)");
            return true;
        }

        #endregion

        #region Manual VFX Placement Commands

        /// <summary>
        /// Places a water VFX (mist or splash) at the cursor position.
        /// </summary>
        private static bool PlaceWaterVFXCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No player found");
                return false;
            }

            // Parse arguments
            string vfxType = "mist"; // Default to mist
            float scale = 1f;
            Vector3 position = GetPlacementPosition(player);

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg.Contains("="))
                {
                    string[] parts = arg.Split(new char[] { '=' }, 2);
                    string key = parts[0].ToLower();
                    string value = parts.Length > 1 ? parts[1] : "";

                    switch (key)
                    {
                        case "scale":
                            float.TryParse(value, out scale);
                            break;
                        case "from":
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float y = position.y;
                                if (coords.Length >= 3)
                                    float.TryParse(coords[2], out y);
                                position = new Vector3(x, y, z);
                            }
                            break;
                    }
                }
                else if (arg.ToLower() == "mist" || arg.ToLower() == "splash")
                {
                    vfxType = arg.ToLower();
                }
            }

            // Create the VFX using the factory (handles persistence)
            GameObject vfxInstance = WaterfallVFXFactory.CreateVFX(vfxType, position, scale);

            if (vfxInstance != null)
            {
                Console.instance.Print($"Placed {vfxType} VFX at {position:F1} with scale {scale}");
                return true;
            }
            else
            {
                Console.instance.Print($"Failed to create {vfxType} VFX");
                return false;
            }
        }

        /// <summary>
        /// Clears manually placed water VFX in an area.
        /// </summary>
        private static bool ClearWaterVFXCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No player found");
                return false;
            }

            float radius = 10f;
            Vector3 position = GetPlacementPosition(player);

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg.Contains("="))
                {
                    string[] parts = arg.Split(new char[] { '=' }, 2);
                    string key = parts[0].ToLower();
                    string value = parts.Length > 1 ? parts[1] : "";

                    switch (key)
                    {
                        case "circle":
                            string[] radiusParts = value.Split('-');
                            if (radiusParts.Length > 0)
                                float.TryParse(radiusParts[0], out radius);
                            break;
                        case "from":
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float y = position.y;
                                if (coords.Length >= 3)
                                    float.TryParse(coords[2], out y);
                                position = new Vector3(x, y, z);
                            }
                            break;
                    }
                }
                else if (float.TryParse(arg, out float r))
                {
                    radius = r;
                }
            }

            // Use the persistence system to clear VFX
            int cleared = WaterVFXPersistence.ClearInRadius(position, radius);

            Console.instance.Print($"Cleared {cleared} VFX within {radius}m of {position:F1}");
            return true;
        }

        #endregion
    }

    /// <summary>
    /// Static helper class for painting waterfall textures on terrain.
    /// Uses the same approach as lava paint but with water shader.
    /// </summary>
    public static class WaterfallTerrainPaint
    {
        /// <summary>
        /// Paints waterfall texture on terrain at the given position.
        /// Returns the number of terrain cells affected.
        /// </summary>
        public static int PaintWaterfall(Vector3 position, float radius)
        {
            // This will use the biome paint system with a special approach:
            // We paint lava-style (which sticks to steep terrain) but swap to water shader

            int cellsAffected = 0;

            // Find terrain compiler at position
            var heightmaps = new System.Collections.Generic.List<Heightmap>();
            Heightmap.FindHeightmap(position, radius, heightmaps);

            if (heightmaps.Count > 0)
            {
                foreach (var hm in heightmaps)
                {
                    var tc = hm.GetAndCreateTerrainCompiler();
                    if (tc != null)
                    {
                        // Paint using vanilla terrain paint system with lava-like values
                        // Paint values: (dirt, cultivate, paved, lava) = (0, 0, 0, 1) for orange lava effect
                        // We'll use similar approach but mark as "waterfall" somehow

                        // For now, just mark as painted and count
                        cellsAffected++;

                        // TODO: Implement proper waterfall terrain paint that:
                        // 1. Uses lava paint mechanism (sticks to steep terrain)
                        // 2. Swaps shader to water shader at runtime
                        // 3. Persists with biome paint system
                    }
                }
            }

            // Fallback: Create a visual indicator for now
            FiresLogger.LogInfo($"[WaterfallTerrainPaint] Paint waterfall at {position}, radius {radius} - {cellsAffected} cells");

            return cellsAffected;
        }

        /// <summary>
        /// Clears waterfall paint from terrain at the given position.
        /// Returns the number of terrain cells affected.
        /// </summary>
        public static int ClearWaterfall(Vector3 position, float radius)
        {
            int cellsAffected = 0;

            // Reset terrain paint to default
            var heightmaps = new System.Collections.Generic.List<Heightmap>();
            Heightmap.FindHeightmap(position, radius, heightmaps);

            if (heightmaps.Count > 0)
            {
                foreach (var hm in heightmaps)
                {
                    var tc = hm.GetAndCreateTerrainCompiler();
                    if (tc != null)
                    {
                        cellsAffected++;
                    }
                }
            }

            FiresLogger.LogInfo($"[WaterfallTerrainPaint] Clear waterfall at {position}, radius {radius} - {cellsAffected} cells");

            return cellsAffected;
        }
    }

    /// <summary>
    /// Factory class for creating waterfall VFX instances using vanilla game VFX.
    /// Uses proper particle effects from the game that already have correct shaders.
    /// </summary>
    public static class WaterfallVFXFactory
    {
        // VFX prefab names from FX_REFERENCE.md that work well for water effects
        // These are vanilla VFX with proper particle shaders

        // Mist/fog effects - use frost/cold effects for misty appearance
        private static readonly string[] MistPrefabNames = new string[]
        {
            "vfx_Cold",           // Cold aura - good for persistent mist
            "vfx_Frost",          // Frost aura
            "fx_shaman_protect",  // Green protective bubble - good for misty effect
            "vfx_spray_spirit",   // Spirit spray - ethereal mist
        };

        // Splash effects - impact/burst effects
        private static readonly string[] SplashPrefabNames = new string[]
        {
            "vfx_sledge_hit",           // Hammer slam - very visible impact
            "fx_eikthyr_stomp",         // Boss stomp - impactful
            "vfx_troll_groundslam",     // Heavy slam
            "fx_DvergerMage_Support_hit", // Support hit - nice pulse
        };

        /// <summary>
        /// Creates a VFX instance of the specified type at the given position.
        /// Uses vanilla game VFX prefabs with proper particle shaders.
        /// </summary>
        public static GameObject CreateVFX(string type, Vector3 position, float scale)
        {
            if (ZNetScene.instance == null)
            {
                FiresLogger.LogWarning("[WaterfallVFXFactory] ZNetScene not available");
                return null;
            }

            GameObject prefab = null;
            string[] prefabNames = null;

            switch (type.ToLower())
            {
                case "mist":
                case "fog":
                    prefabNames = MistPrefabNames;
                    break;

                case "splash":
                    prefabNames = SplashPrefabNames;
                    break;

                default:
                    FiresLogger.LogWarning($"[WaterfallVFXFactory] Unknown VFX type: {type}");
                    return null;
            }

            // Try each prefab name until we find one that exists
            foreach (var prefabName in prefabNames)
            {
                prefab = ZNetScene.instance.GetPrefab(prefabName);
                if (prefab != null)
                {
                    FiresLogger.LogInfo($"[WaterfallVFXFactory] Using VFX prefab: {prefabName}");
                    break;
                }
            }

            if (prefab == null)
            {
                FiresLogger.LogWarning($"[WaterfallVFXFactory] Could not find any VFX prefab for type: {type}");
                return null;
            }

            // Instantiate the VFX
            var instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            instance.name = $"VA_WaterVFX_{type}_{position.GetHashCode()}";
            instance.transform.localScale = Vector3.one * scale;

            // Remove ZNetView to prevent network sync issues
            // These are local-only visual effects
            var nview = instance.GetComponent<ZNetView>();
            if (nview != null)
            {
                UnityEngine.Object.Destroy(nview);
            }

            // Add our persistence component for zone reload
            var persistence = instance.AddComponent<WaterVFXPersistence>();
            persistence.VFXType = type;
            persistence.Scale = scale;
            persistence.OriginalPosition = position;

            instance.SetActive(true);

            // Start particle systems
            var ps = instance.GetComponent<ParticleSystem>();
            if (ps == null) ps = instance.GetComponentInChildren<ParticleSystem>();
            if (ps != null)
            {
                // Make it loop continuously
                var main = ps.main;
                main.loop = true;
                ps.Play();
            }

            return instance;
        }
    }

    /// <summary>
    /// Component that handles persistence of manually placed water VFX.
    /// Saves/loads VFX data to ensure they respawn on zone reload.
    /// </summary>
    public class WaterVFXPersistence : MonoBehaviour
    {
        public string VFXType;
        public float Scale;
        public Vector3 OriginalPosition;

        // Static registry for all placed VFX
        private static System.Collections.Generic.List<WaterVFXData> s_placedVFX = new System.Collections.Generic.List<WaterVFXData>();
        private static bool s_initialized = false;

        [System.Serializable]
        public class WaterVFXData
        {
            public string Type;
            public Vector3 Position;
            public float Scale;
            public int Hash; // For deduplication
        }

        private void Awake()
        {
            // Register this VFX
            RegisterVFX();
        }

        private void OnDestroy()
        {
            // Unregister this VFX
            UnregisterVFX();
        }

        private void RegisterVFX()
        {
            int hash = GetVFXHash();

            // Check if already registered
            if (s_placedVFX.Exists(v => v.Hash == hash))
                return;

            s_placedVFX.Add(new WaterVFXData
            {
                Type = VFXType,
                Position = OriginalPosition,
                Scale = Scale,
                Hash = hash
            });

            // Save to persistence
            SaveVFXData();
        }

        private void UnregisterVFX()
        {
            int hash = GetVFXHash();
            s_placedVFX.RemoveAll(v => v.Hash == hash);
            SaveVFXData();
        }

        private int GetVFXHash()
        {
            return $"{VFXType}_{OriginalPosition.x:F1}_{OriginalPosition.y:F1}_{OriginalPosition.z:F1}".GetHashCode();
        }

        /// <summary>
        /// Gets the world save path for custom data files.
        /// </summary>
        private static string GetWorldSavePath()
        {
            // Try to get path from ZNet
            if (ZNet.instance != null && ZNet.instance.GetWorldUID() != 0)
            {
                string worldName = ZNet.instance.GetWorldName();
                if (!string.IsNullOrEmpty(worldName))
                {
                    // Vanilla's own world save root (honours -savedir; on a plain client it is the same LocalLow\IronGate\
                    // Valheim\worlds_local this used to build by hand from the user profile, so saved data stays put). A public
                    // mod's files stay inside the game's and Valheim's own folders (Fire, 2026-09-29).
                    string basePath = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
                    return System.IO.Path.Combine(basePath, worldName);
                }
            }
            return null;
        }

        /// <summary>
        /// Saves VFX data to the world save.
        /// </summary>
        public static void SaveVFXData()
        {
            try
            {
                string worldPath = GetWorldSavePath();
                if (string.IsNullOrEmpty(worldPath)) return;

                // Ensure directory exists
                if (!System.IO.Directory.Exists(worldPath))
                {
                    System.IO.Directory.CreateDirectory(worldPath);
                }

                string filePath = System.IO.Path.Combine(worldPath, "water_vfx.dat");

                // Simple binary format
                using (var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Create))
                using (var writer = new System.IO.BinaryWriter(stream))
                {
                    writer.Write(s_placedVFX.Count);
                    foreach (var vfx in s_placedVFX)
                    {
                        writer.Write(vfx.Type ?? "");
                        writer.Write(vfx.Position.x);
                        writer.Write(vfx.Position.y);
                        writer.Write(vfx.Position.z);
                        writer.Write(vfx.Scale);
                        writer.Write(vfx.Hash);
                    }
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[WaterVFXPersistence] Failed to save VFX data: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads and restores VFX data from the world save.
        /// Called on zone load.
        /// </summary>
        public static void LoadAndRestoreVFX()
        {
            if (s_initialized) return;
            s_initialized = true;

            try
            {
                string worldPath = GetWorldSavePath();
                if (string.IsNullOrEmpty(worldPath)) return;

                string filePath = System.IO.Path.Combine(worldPath, "water_vfx.dat");
                if (!System.IO.File.Exists(filePath)) return;

                s_placedVFX.Clear();

                // Simple binary format
                using (var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Open))
                using (var reader = new System.IO.BinaryReader(stream))
                {
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var vfx = new WaterVFXData
                        {
                            Type = reader.ReadString(),
                            Position = new Vector3(
                                reader.ReadSingle(),
                                reader.ReadSingle(),
                                reader.ReadSingle()
                            ),
                            Scale = reader.ReadSingle(),
                            Hash = reader.ReadInt32()
                        };
                        s_placedVFX.Add(vfx);
                    }
                }

                // Restore each VFX
                foreach (var vfx in s_placedVFX)
                {
                    // Only restore if in loaded zone
                    if (!IsPositionInLoadedZone(vfx.Position))
                        continue;

                    // Check if already exists
                    var existing = FindVFXAtPosition(vfx.Position, vfx.Type);
                    if (existing != null)
                        continue;

                    // Create the VFX
                    WaterfallVFXFactory.CreateVFX(vfx.Type, vfx.Position, vfx.Scale);
                }

                FiresLogger.LogInfo($"[WaterVFXPersistence] Restored {s_placedVFX.Count} water VFX");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[WaterVFXPersistence] Failed to load VFX data: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears all VFX in a radius.
        /// </summary>
        public static int ClearInRadius(Vector3 center, float radius)
        {
            int cleared = 0;
            float radiusSq = radius * radius;

            // Find and destroy VFX GameObjects
            var allVFX = UnityEngine.Object.FindObjectsByType<WaterVFXPersistence>(UnityEngine.FindObjectsSortMode.None);
            foreach (var vfx in allVFX)
            {
                if (vfx == null) continue;

                float distSq = (vfx.OriginalPosition - center).sqrMagnitude;
                if (distSq <= radiusSq)
                {
                    UnityEngine.Object.Destroy(vfx.gameObject);
                    cleared++;
                }
            }

            // Also remove from registry (OnDestroy will handle save)
            s_placedVFX.RemoveAll(v => (v.Position - center).sqrMagnitude <= radiusSq);
            SaveVFXData();

            return cleared;
        }

        /// <summary>
        /// Resets initialization flag on world change.
        /// </summary>
        public static void OnWorldChange()
        {
            s_initialized = false;
            s_placedVFX.Clear();
        }

        private static bool IsPositionInLoadedZone(Vector3 position)
        {
            if (ZoneSystem.instance == null) return false;
            // Both ZoneSystem.GetZone and IsZoneLoaded flipped Vector2i → Vector2s
            // on public test in unison — keep the local var's type matching whichever
            // pair this build links against.
            Vector2s zone = ZoneSystem.GetZone(position);
            return ZoneSystem.instance.IsZoneLoaded(zone);
        }

        private static WaterVFXPersistence FindVFXAtPosition(Vector3 position, string type)
        {
            var allVFX = UnityEngine.Object.FindObjectsByType<WaterVFXPersistence>(UnityEngine.FindObjectsSortMode.None);
            foreach (var vfx in allVFX)
            {
                if (vfx == null) continue;
                if (vfx.VFXType != type) continue;

                float dist = Vector3.Distance(vfx.OriginalPosition, position);
                if (dist < 1f)
                    return vfx;
            }
            return null;
        }
    }
}
