using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    /// <summary>
    /// Custom water placement tool for terrain-style water.
    /// Works with Infinity Hammer's command system to provide dedicated water creation tools.
    /// 
    /// ARCHITECTURE (mirrors DirtFloorPlacementTool):
    /// - Creates water DIRECTLY via WaterMeshCombiner system
    /// - Uses WaterDisk data objects (like TerrainDisk)
    /// - No "piece" prefab to accidentally delete
    /// 
    /// Uses standard Infinity Hammer controls:
    /// - Scroll wheel adjusts radius via <area> parameter
    /// - Page Up/Down adjusts depth via <h> parameter
    /// </summary>
    public static class WaterPlacementTool
    {
        private static bool _initialized = false;

        // Radius limits
        private const float MIN_RADIUS = 2f;
        private const float MAX_RADIUS = 50f;

        // Depth limits
        private const float MIN_DEPTH = 0.5f;
        private const float MAX_DEPTH = 20f;

        /// <summary>
        /// Initializes the water placement tool and registers console commands.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // ===== Main Water Placement =====

            // place_water [radius] [depth=D] [tar]
            // Places water disk at cursor position with surface at cursor Y
            new Terminal.ConsoleCommand("place_water",
                "[args] - Places custom water at cursor. Use circle=R for radius, depth=D for max depth, tar for tar liquid.",
                (Terminal.ConsoleEventFailable)(args => PlaceWater(args)));

            // delete_water [radius]
            // Removes water disks in area
            new Terminal.ConsoleCommand("delete_water_area",
                "[args] - Deletes custom water in radius.",
                (Terminal.ConsoleEventFailable)(args => DeleteWater(args)));

            // mop_water [radius]
            // Precision water removal - removes water pixels without full regeneration
            new Terminal.ConsoleCommand("mop_water",
                "[args] - Mops (precision removes) custom water in radius. Removes only the pixels you hover over.",
                (Terminal.ConsoleEventFailable)(args => MopWater(args)));

            // clear_zone_water
            // Clears all water in current zone
            new Terminal.ConsoleCommand("clear_zone_water_all",
                "Clears ALL custom water in the current zone.",
                (Terminal.ConsoleEventFailable)(args => ClearZoneWater(args)));

            // ===== Water Adjustment Tools =====

            // water_set_depth [depth]
            // Adjusts depth of water at cursor
            new Terminal.ConsoleCommand("water_set_depth",
                "[depth] - Sets max depth of water at cursor position.",
                (Terminal.ConsoleEventFailable)(args => SetWaterDepth(args)));

            // water_set_surface [y]
            // Adjusts surface Y level of water at cursor
            new Terminal.ConsoleCommand("water_set_surface",
                "[y] - Sets surface Y level of water at cursor position.",
                (Terminal.ConsoleEventFailable)(args => SetWaterSurface(args)));

            // water_convert [water|tar]
            // Converts water type at cursor
            new Terminal.ConsoleCommand("water_convert",
                "[water|tar] - Converts water type at cursor position.",
                (Terminal.ConsoleEventFailable)(args => ConvertWater(args)));

            // ===== Water Smoothing Tool =====

            // smooth_water [radius]
            // Smooths transitions between water disks in a radius
            new Terminal.ConsoleCommand("smooth_water",
                "[args] - Smooths water surface transitions in radius. Averages disk heights for seamless blending. Use circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => SmoothWater(args)));

            // ===== Liquid Type Placement Tools =====

            // place_water_type [type] [radius] [depth]
            // Places custom liquid of any type at cursor
            new Terminal.ConsoleCommand("place_water_type",
                "[type] [args] - Places custom liquid (water/tar/ice/boiling/freezing/lava). Use circle=R, depth=D.",
                (Terminal.ConsoleEventFailable)(args => PlaceWaterType(args)));

            // ===== Ice / Freeze Tools =====

            // freeze_water - Freezes water at cursor into solid ice
            new Terminal.ConsoleCommand("freeze_water",
                "Freezes custom water at cursor into solid walkable ice.",
                (Terminal.ConsoleEventFailable)(args => FreezeWater(args)));

            // thaw_water - Thaws frozen ice back to water
            new Terminal.ConsoleCommand("thaw_water",
                "Thaws frozen ice at cursor back to water.",
                (Terminal.ConsoleEventFailable)(args => ThawWater(args)));

            // break_ice - Punches a hole in frozen ice
            new Terminal.ConsoleCommand("break_ice",
                "[args] - Breaks a hole in frozen ice. Use circle=R for hole radius.",
                (Terminal.ConsoleEventFailable)(args => BreakIce(args)));

            // ===== Info Commands =====

            // water_info
            // Shows info about water at cursor
            new Terminal.ConsoleCommand("water_info_at",
                "Shows info about custom water at cursor position.",
                (Terminal.ConsoleEventFailable)(args => WaterInfo(args)));

            FiresLogger.LogInfo("[WaterPlacementTool] Initialized - Terrain-style water commands registered");
        }

        #region Place Water

        /// <summary>
        /// Places water at the cursor position.
        /// The water surface is at the cursor Y, filling down to terrain.
        /// 
        /// IH Parameter Format:
        /// - from=X,Z,Y : Position from IH <area> expansion (Y = surface level)
        /// - circle=R : Radius from IH <area> expansion (creates circular water)
        /// - rect=W,D : Width,Depth from IH <area> expansion (creates rectangular water)
        /// - square : Flag indicating the ruler is square-shaped
        /// - depth=D : Max depth below surface
        /// - tar : Use tar instead of water
        /// </summary>
        private static bool PlaceWater(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            FiresLogger.LogInfo($"[WaterPlacementTool] Raw args ({args.Length}): {string.Join(" | ", args.Args)}");

            // Default values
            float radius = 5f;
            float maxDepth = 5f;
            bool isTar = false;
            CustomLiquidType customLiquidType = CustomLiquidType.Water;
            bool hasCustomType = false;
            Vector3 position = Vector3.zero;
            bool positionFromArgs = false;
            WaterDiskShape shape = WaterDiskShape.Circle;
            float shapeWidth = 0f;
            float shapeLength = 0f;
            bool hasRect = false;

            // Parse arguments - handle IH key=value format
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
                            // circle=r or circle=r-r2 - take first value
                            string[] radiusParts = value.Split('-');
                            if (radiusParts.Length > 0 && float.TryParse(radiusParts[0], out float r))
                            {
                                radius = Mathf.Clamp(r, MIN_RADIUS, MAX_RADIUS);
                                shape = WaterDiskShape.Circle;
                                FiresLogger.LogInfo($"[WaterPlacementTool] Parsed circle={radius}");
                            }
                            break;

                        case "rect":
                            // rect=W,D - width and depth for rectangle shape
                            string[] rectParts = value.Split(',');
                            if (rectParts.Length >= 2 &&
                                float.TryParse(rectParts[0], out float rw) &&
                                float.TryParse(rectParts[1], out float rd))
                            {
                                shapeWidth = Mathf.Clamp(rw, MIN_RADIUS, MAX_RADIUS * 2f);
                                shapeLength = Mathf.Clamp(rd, MIN_RADIUS, MAX_RADIUS * 2f);
                                hasRect = true;
                                shape = Mathf.Approximately(shapeWidth, shapeLength)
                                    ? WaterDiskShape.Square
                                    : WaterDiskShape.Rectangle;
                                FiresLogger.LogInfo($"[WaterPlacementTool] Parsed rect={shapeWidth},{shapeLength} -> {shape}");
                            }
                            else if (rectParts.Length >= 1 && float.TryParse(rectParts[0], out float rs))
                            {
                                // rect=S (single value = square)
                                shapeWidth = Mathf.Clamp(rs, MIN_RADIUS, MAX_RADIUS * 2f);
                                shapeLength = shapeWidth;
                                hasRect = true;
                                shape = WaterDiskShape.Square;
                                FiresLogger.LogInfo($"[WaterPlacementTool] Parsed rect={shapeWidth} (square)");
                            }
                            break;

                        case "depth":
                            if (float.TryParse(value, out float d))
                            {
                                maxDepth = Mathf.Clamp(d, MIN_DEPTH, MAX_DEPTH);
                                FiresLogger.LogInfo($"[WaterPlacementTool] Parsed depth={maxDepth}");
                            }
                            break;

                        case "type":
                            customLiquidType = ParseLiquidType(value);
                            hasCustomType = customLiquidType != CustomLiquidType.Water;
                            if (customLiquidType == CustomLiquidType.Tar) isTar = true;
                            FiresLogger.LogInfo($"[WaterPlacementTool] Parsed type={customLiquidType}");
                            break;

                        case "from":
                            // from=x,z,y format (NOTE: IH uses X,Z,Y order!)
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float fromY = 0f;
                                if (coords.Length >= 3)
                                {
                                    float.TryParse(coords[2], out fromY);
                                }
                                position = new Vector3(x, fromY, z);
                                positionFromArgs = true;
                                FiresLogger.LogInfo($"[WaterPlacementTool] Parsed from=({x}, {z}, {fromY}) -> position={position}");
                            }
                            break;
                    }
                }
                else
                {
                    string lower = arg.ToLower();
                    // Check for "tar" keyword
                    if (lower == "tar")
                    {
                        isTar = true;
                        customLiquidType = CustomLiquidType.Tar;
                        hasCustomType = true;
                        FiresLogger.LogInfo("[WaterPlacementTool] Using TAR liquid type");
                    }
                    // Check for "square" keyword
                    else if (lower == "square")
                    {
                        if (!hasRect)
                        {
                            shape = WaterDiskShape.Square;
                            shapeWidth = radius * 2f;
                            shapeLength = shapeWidth;
                            hasRect = true;
                            FiresLogger.LogInfo($"[WaterPlacementTool] Square mode: {shapeWidth}x{shapeLength}");
                        }
                    }
                    // Simple numeric arg - treat as radius (legacy support)
                    else if (float.TryParse(arg, out float numValue))
                    {
                        radius = Mathf.Clamp(numValue, MIN_RADIUS, MAX_RADIUS);
                    }
                }
            }

            // If position wasn't in args, get from raycast
            if (!positionFromArgs)
            {
                position = GetPlacementPosition(player);
                if (position == Vector3.zero)
                {
                    Console.instance.Print("Could not determine placement position");
                    return false;
                }
                FiresLogger.LogInfo($"[WaterPlacementTool] Position from raycast: {position}");
            }

            // Create water disk at this position
            // The surface Y is the position.y (where the cursor/ruler is)
            LiquidType liquidType = isTar ? LiquidType.Tar : LiquidType.Water;

            // Use RPC system for multiplayer sync
            int stableHash;
            string sizeDesc;

            if (hasCustomType && customLiquidType != CustomLiquidType.Water)
            {
                // Non-water liquid type (tar, ice, lava, boiling, freezing)
                // Must use RequestCreateWaterWithCustomType so the combiner's
                // CustomLiquidType is set correctly from the start.
                LiquidType vanillaType = customLiquidType.ToVanillaLiquidType();
                WaterDisk disk;

                if (hasRect && shape != WaterDiskShape.Circle)
                {
                    disk = new WaterDisk(position, shape, shapeWidth, shapeLength, maxDepth, vanillaType);
                    sizeDesc = $"{shapeWidth:F1}x{shapeLength:F1}m ({shape})";
                }
                else
                {
                    disk = new WaterDisk(position, radius, maxDepth, vanillaType);
                    sizeDesc = $"r={radius:F1}m (Circle)";
                }

                disk.CustomLiquidType = customLiquidType;
                stableHash = WaterOperationRPC.RequestCreateWaterWithCustomType(position, disk, customLiquidType);
            }
            else if (hasRect && shape != WaterDiskShape.Circle)
            {
                stableHash = WaterOperationRPC.RequestCreateWater(position, shape, shapeWidth, shapeLength, maxDepth, liquidType);
                sizeDesc = $"{shapeWidth:F1}x{shapeLength:F1}m ({shape})";
            }
            else
            {
                stableHash = WaterOperationRPC.RequestCreateWater(position, radius, maxDepth, liquidType);
                sizeDesc = $"r={radius:F1}m (Circle)";
            }

            if (stableHash != 0)
            {
                string liquidName = hasCustomType ? customLiquidType.GetDisplayName() : (isTar ? "tar" : "water");
                Console.instance.Print($"Placed {liquidName}: {sizeDesc}, depth={maxDepth:F1}m at Y={position.y:F2}m");

                FiresLogger.LogInfo($"[WaterPlacementTool] SUCCESS: Created water disk at {position}, {sizeDesc}, depth={maxDepth}, type={liquidType}, customType={customLiquidType}, hash={stableHash}");
                return true;
            }
            else
            {
                Console.instance.Print("Failed to create water");
                return false;
            }
        }

        #endregion

        #region Delete Water

        /// <summary>
        /// Mops (precision removes) water at the cursor position.
        /// Unlike delete_water which triggers full recombination, this directly modifies
        /// the mesh grid and saves the new state. Use for cleaning up edges.
        /// 
        /// IH Parameter Format:
        /// - from=X,Z,Y : Position from IH <area> expansion
        /// - circle=R : Radius from IH <area> expansion
        /// </summary>
        private static bool MopWater(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            // Default values
            float radius = 2f; // Small default for precision work
            Vector3 position = GetPlacementPosition(player);

            // Parse arguments
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
                            if (radiusParts.Length > 0 && float.TryParse(radiusParts[0], out float r))
                            {
                                radius = Mathf.Clamp(r, 0.5f, MAX_RADIUS);
                            }
                            break;

                        case "from":
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float y = position.y;
                                if (coords.Length >= 3)
                                {
                                    float.TryParse(coords[2], out y);
                                }
                                position = new Vector3(x, y, z);
                            }
                            break;
                    }
                }
                else if (float.TryParse(arg, out float numValue))
                {
                    radius = Mathf.Clamp(numValue, 0.5f, MAX_RADIUS);
                }
            }

            // Find combiners that contain this position and mop them
            bool anyMopped = false;

            // Get all combiners near the position
            var combiners = WaterMeshCombiner.GetCombinersInRadius(position, radius + 50f);
            foreach (var combiner in combiners)
            {
                if (combiner == null) continue;

                // Try to mop water at this position
                if (combiner.MopWaterAt(position, radius))
                {
                    anyMopped = true;
                }
            }

            // Generated carve+fill water mops too (persisted per column).
            try
            {
                if (VerdantsAscent.Terrain.Voxel.VoxelWorld.MopGeneratedWater(position, radius) > 0)
                    anyMopped = true;
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[WaterPlacementTool] mop generated pass: {ex.Message}"); }

            if (anyMopped)
            {
                Console.instance.Print($"Mopped water at radius {radius:F1}m");
                return true;
            }
            else
            {
                Console.instance.Print("No water to mop at this position");
                return false;
            }
        }

        /// <summary>
        /// Deletes custom water in a radius.
        /// 
        /// IH Parameter Format:
        /// - from=X,Z,Y : Position from IH <area> expansion
        /// - circle=R : Radius from IH <area> expansion
        /// </summary>
        private static bool DeleteWater(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            // Default values
            float radius = 5f;
            Vector3 position = GetPlacementPosition(player);

            // Parse arguments
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
                            if (radiusParts.Length > 0 && float.TryParse(radiusParts[0], out float r))
                            {
                                radius = Mathf.Clamp(r, MIN_RADIUS, MAX_RADIUS);
                            }
                            break;

                        case "from":
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float y = position.y;
                                if (coords.Length >= 3)
                                {
                                    float.TryParse(coords[2], out y);
                                }
                                position = new Vector3(x, y, z);
                            }
                            break;
                    }
                }
                else if (float.TryParse(arg, out float numValue))
                {
                    radius = Mathf.Clamp(numValue, MIN_RADIUS, MAX_RADIUS);
                }
            }

            // Execute delete using RPC system for multiplayer sync
            WaterOperationRPC.RequestRemoveWaterInRadius(position, radius);

            // Also clean up legacy water and paint controllers locally
            int legacyCount = CustomWaterManager.ClearRadius(position, radius);

            // Clean up WaterPaintController pieces
            var paintsToRemove = new List<WaterPaintController>();
            foreach (var paint in WaterPaintController.AllWaterPaints)
            {
                if (paint == null) continue;
                float dist = Vector3.Distance(paint.transform.position, position);
                if (dist <= radius)
                {
                    paintsToRemove.Add(paint);
                }
            }

            foreach (var paint in paintsToRemove)
            {
                if (paint != null && paint.gameObject != null)
                {
                    var nview = paint.GetComponent<ZNetView>();
                    if (nview != null && nview.GetZDO() != null)
                    {
                        ZNetScene.instance?.Destroy(paint.gameObject);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(paint.gameObject);
                    }
                    legacyCount++;
                }
            }

            Console.instance.Print($"Deleted water within {radius:F1}m radius");
            return true;
        }

        /// <summary>
        /// Clears all custom water in the current zone.
        /// </summary>
        private static bool ClearZoneWater(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            if (ZoneSystem.instance == null)
            {
                Console.instance.Print("ZoneSystem not available");
                return false;
            }

            // Use a large radius centered on player to clear entire zone
            // Zone size is typically 64m, so 100m radius should cover current zone
            float clearRadius = 100f;
            Vector3 playerPos = player.transform.position;

            // Use RPC system for multiplayer sync
            WaterOperationRPC.RequestClearZoneWater(playerPos, clearRadius);

            // Also clear legacy water locally. ZoneSystem.GetZone returns
            // Vector2s on public test; convert back so CustomWaterManager's
            // Vector2i-keyed APIs stay unchanged.
            Vector2i zone = ZoneSystem.GetZone(playerPos).ToVector2i();
            CustomWaterManager.ClearZone(zone);

            Console.instance.Print($"Cleared all custom water in zone {zone}");
            return true;
        }

        #endregion

        #region Water Adjustment

        /// <summary>
        /// Sets the max depth of water at cursor position.
        /// </summary>
        private static bool SetWaterDepth(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            if (args.Length < 2 || !float.TryParse(args[1], out float depth))
            {
                Console.instance.Print("Usage: water_set_depth <depth>");
                return false;
            }

            depth = Mathf.Clamp(depth, MIN_DEPTH, MAX_DEPTH);

            Vector3 pos = GetPlacementPosition(player);

            // Find water at position
            var paint = WaterPaintController.GetWaterPaintAt(pos);
            if (paint != null)
            {
                paint.SetMaxDepth(depth);
                Console.instance.Print($"Set water depth to {depth:F1}m");
                return true;
            }

            Console.instance.Print("No custom water at position");
            return false;
        }

        /// <summary>
        /// Sets the surface Y level of water at cursor position.
        /// </summary>
        private static bool SetWaterSurface(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            if (args.Length < 2 || !float.TryParse(args[1], out float surfaceY))
            {
                Console.instance.Print("Usage: water_set_surface <y>");
                return false;
            }

            Vector3 pos = GetPlacementPosition(player);

            // Find water at position
            var paint = WaterPaintController.GetWaterPaintAt(pos);
            if (paint != null)
            {
                paint.SetSurfaceY(surfaceY);
                Console.instance.Print($"Set water surface to Y={surfaceY:F1}m");
                return true;
            }

            Console.instance.Print("No custom water at position");
            return false;
        }

        /// <summary>
        /// Converts water type between water and tar.
        /// </summary>
        private static bool ConvertWater(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            string targetType = args.Length > 1 ? args[1].ToLower() : "water";
            LiquidType newType = targetType == "tar" ? LiquidType.Tar : LiquidType.Water;

            Vector3 pos = GetPlacementPosition(player);

            // Find water at position
            var paint = WaterPaintController.GetWaterPaintAt(pos);
            if (paint != null)
            {
                paint.LiquidType = newType;
                var combiner = paint.GetCombiner();
                if (combiner != null)
                {
                    combiner.LiquidType = newType;
                    combiner.ForceRegenerate();
                }
                Console.instance.Print($"Converted water to {newType}");
                return true;
            }

            // Also check legacy water
            var water = CustomWaterManager.GetWaterAt(pos);
            if (water != null)
            {
                water.LiquidType = newType;
                Console.instance.Print($"Converted water to {newType}");
                return true;
            }

            Console.instance.Print("No custom water at position");
            return false;
        }

        #endregion

        #region Info Commands

        /// <summary>
        /// Shows information about water at cursor position.
        /// </summary>
        private static bool WaterInfo(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            Vector3 pos = GetPlacementPosition(player);

            Console.instance.Print($"=== Water Info at {pos:F1} ===");

            // Check new terrain-style water
            var combiner = WaterMeshCombiner.GetCombinerAt(pos);
            if (combiner != null)
            {
                float surface = combiner.GetWaterSurfaceAt(pos);
                Console.instance.Print($"Terrain-Style Water:");
                Console.instance.Print($"  Combiner: {combiner.GetWaterDisks().Count} disks");
                Console.instance.Print($"  Surface Y: {surface:F2}m");
                Console.instance.Print($"  Type: {combiner.LiquidType}");
            }

            // Check WaterPaintController
            var paint = WaterPaintController.GetWaterPaintAt(pos);
            if (paint != null)
            {
                Console.instance.Print($"WaterPaint Controller:");
                Console.instance.Print($"  Position: {paint.transform.position}");
                Console.instance.Print($"  Radius: {paint.WaterRadius:F1}m");
                Console.instance.Print($"  Max Depth: {paint.MaxDepth:F1}m");
                Console.instance.Print($"  Surface Y: {paint.SurfaceY:F2}m");
                Console.instance.Print($"  Type: {paint.LiquidType}");
            }

            // Check legacy water
            var water = CustomWaterManager.GetWaterAt(pos);
            if (water != null)
            {
                float level = water.GetWaterSurfaceAt(pos);
                Console.instance.Print($"Legacy Water ({water.GetWaterType()}):");
                Console.instance.Print($"  Position: {water.transform.position}");
                Console.instance.Print($"  Bounds: {water.BoundsSize}");
                Console.instance.Print($"  Surface: {level:F2}m");
                Console.instance.Print($"  Type: {water.LiquidType}");
            }

            // Combined water level
            float combined = CustomWaterManager.GetCombinedWaterLevel(pos);
            Console.instance.Print($"Combined water level: {combined:F2}m");

            if (combiner == null && paint == null && water == null)
            {
                Console.instance.Print("No custom water at this position");
            }

            return true;
        }

        #endregion

        #region Freeze / Thaw / Break Ice

        /// <summary>
        /// Freezes water at cursor position into solid walkable ice.
        /// Converts the combiner's liquid type to Ice, which triggers
        /// FrozenWaterController creation.
        /// </summary>
        private static bool FreezeWater(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            Vector3 position = GetPlacementPosition(player);

            var combiner = WaterMeshCombiner.GetCombinerAt(position);
            if (combiner == null)
            {
                // Try finding by radius in case we're near the edge
                var combiners = WaterMeshCombiner.GetCombinersInRadius(position, 10f);
                foreach (var c in combiners)
                {
                    if (c != null && c.ContainsPosition(position))
                    {
                        combiner = c;
                        break;
                    }
                }
            }

            if (combiner == null)
            {
                Console.instance.Print("No custom water at this position");
                return false;
            }

            if (combiner.CustomLiquidType == CustomLiquidType.Ice)
            {
                Console.instance.Print("Water is already frozen");
                return false;
            }

            combiner.SetCustomLiquidType(CustomLiquidType.Ice);
            Console.instance.Print($"Froze water at {position:F1} - walk on it!");
            return true;
        }

        /// <summary>
        /// Thaws frozen ice back to water.
        /// </summary>
        private static bool ThawWater(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            Vector3 position = GetPlacementPosition(player);

            var combiner = WaterMeshCombiner.GetCombinerAt(position);
            if (combiner == null)
            {
                var combiners = WaterMeshCombiner.GetCombinersInRadius(position, 10f);
                foreach (var c in combiners)
                {
                    if (c != null && c.ContainsPosition(position))
                    {
                        combiner = c;
                        break;
                    }
                }
            }

            if (combiner == null)
            {
                Console.instance.Print("No custom water at this position");
                return false;
            }

            if (combiner.CustomLiquidType != CustomLiquidType.Ice)
            {
                Console.instance.Print("Water is not frozen");
                return false;
            }

            combiner.SetCustomLiquidType(CustomLiquidType.Water);
            Console.instance.Print($"Thawed ice at {position:F1}");
            return true;
        }

        /// <summary>
        /// Breaks a hole in frozen ice at cursor position.
        /// 
        /// IH Parameter Format:
        /// - from=X,Z,Y : Position from IH area expansion
        /// - circle=R : Hole radius
        /// </summary>
        private static bool BreakIce(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            float radius = 2f;
            Vector3 position = Vector3.zero;
            bool positionFromArgs = false;

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
                            if (radiusParts.Length > 0 && float.TryParse(radiusParts[0], out float r))
                                radius = Mathf.Clamp(r, 0.5f, 20f);
                            break;

                        case "from":
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float fromY = 0f;
                                if (coords.Length >= 3)
                                    float.TryParse(coords[2], out fromY);
                                position = new Vector3(x, fromY, z);
                                positionFromArgs = true;
                            }
                            break;
                    }
                }
                else if (float.TryParse(arg, out float numValue))
                {
                    radius = Mathf.Clamp(numValue, 0.5f, 20f);
                }
            }

            if (!positionFromArgs)
            {
                position = GetPlacementPosition(player);
                if (position == Vector3.zero)
                {
                    Console.instance.Print("Could not determine position");
                    return false;
                }
            }

            // Find ice at this position
            var combiner = WaterMeshCombiner.GetCombinerAt(position);
            if (combiner == null)
            {
                var combiners = WaterMeshCombiner.GetCombinersInRadius(position, radius + 10f);
                foreach (var c in combiners)
                {
                    if (c != null && c.ContainsPosition(position))
                    {
                        combiner = c;
                        break;
                    }
                }
            }

            if (combiner == null || combiner.CustomLiquidType != CustomLiquidType.Ice)
            {
                Console.instance.Print("No frozen water at this position");
                return false;
            }

            var frozenController = combiner.GetComponent<FrozenWaterController>();
            if (frozenController == null)
            {
                Console.instance.Print("Ice surface not found");
                return false;
            }

            if (frozenController.BreakIceAt(position, radius))
            {
                Console.instance.Print($"Broke ice hole: r={radius:F1}m ({frozenController.HoleCount} holes)");
                return true;
            }
            else
            {
                Console.instance.Print("Could not break ice at this position");
                return false;
            }
        }

        #endregion

        #region Smooth Water

        /// <summary>
        /// Smooths water surface transitions in a radius by averaging disk heights
        /// and triggering mesh regeneration. This fixes wonky areas where auto-generated
        /// river disks have inconsistent heights, creating visible seams.
        /// 
        /// How it works:
        /// 1. Finds all combiners with disks in the radius
        /// 2. Collects all disk surface Y values in the radius
        /// 3. Computes a weighted average based on distance from center
        /// 4. Adjusts each disk's Y toward the average (smooth blend)
        /// 5. Triggers mesh regeneration on affected combiners
        /// 
        /// IH Parameter Format:
        /// - from=X,Z,Y : Position from IH area expansion
        /// - circle=R : Radius from IH area expansion
        /// </summary>
        private static bool SmoothWater(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            float radius = 10f;
            Vector3 position = Vector3.zero;
            bool positionFromArgs = false;
            float strength = 0.5f; // 0-1 blend strength toward average

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
                            if (radiusParts.Length > 0 && float.TryParse(radiusParts[0], out float r))
                                radius = Mathf.Clamp(r, 1f, MAX_RADIUS);
                            break;

                        case "from":
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float fromY = 0f;
                                if (coords.Length >= 3)
                                    float.TryParse(coords[2], out fromY);
                                position = new Vector3(x, fromY, z);
                                positionFromArgs = true;
                            }
                            break;

                        case "strength":
                            if (float.TryParse(value, out float s))
                                strength = Mathf.Clamp01(s);
                            break;
                    }
                }
                else if (float.TryParse(arg, out float numValue))
                {
                    radius = Mathf.Clamp(numValue, 1f, MAX_RADIUS);
                }
            }

            if (!positionFromArgs)
            {
                position = GetPlacementPosition(player);
                if (position == Vector3.zero)
                {
                    Console.instance.Print("Could not determine position");
                    return false;
                }
            }

            // Find all combiners in the area
            var combiners = WaterMeshCombiner.GetCombinersInRadius(position, radius + 50f);
            if (combiners.Count == 0)
            {
                Console.instance.Print("No custom water found in range");
                return false;
            }

            // Collect all disks within the smooth radius
            float radiusSq = radius * radius;
            var disksInRange = new List<WaterDisk>();
            var affectedCombiners = new HashSet<WaterMeshCombiner>();

            foreach (var combiner in combiners)
            {
                if (combiner == null) continue;

                var disks = combiner.GetWaterDisks();
                if (disks == null) continue;

                foreach (var disk in disks)
                {
                    if (disk == null) continue;

                    float dx = disk.Position.x - position.x;
                    float dz = disk.Position.z - position.z;
                    float distSq = dx * dx + dz * dz;

                    if (distSq <= radiusSq)
                    {
                        disksInRange.Add(disk);
                        affectedCombiners.Add(combiner);
                    }
                }
            }

            if (disksInRange.Count < 2)
            {
                Console.instance.Print($"Need at least 2 water disks in range to smooth (found {disksInRange.Count})");
                return false;
            }

            // Compute distance-weighted average Y
            float totalWeight = 0f;
            float weightedY = 0f;

            foreach (var disk in disksInRange)
            {
                float dx = disk.Position.x - position.x;
                float dz = disk.Position.z - position.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                // Weight: closer to center = higher weight
                float w = 1f - Mathf.Clamp01(dist / radius);
                w = w * w; // Quadratic falloff
                w = Mathf.Max(w, 0.1f); // Minimum weight so edge disks aren't ignored

                weightedY += disk.Position.y * w;
                totalWeight += w;
            }

            float avgY = weightedY / totalWeight;

            // Find min/max for reporting
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var disk in disksInRange)
            {
                if (disk.Position.y < minY) minY = disk.Position.y;
                if (disk.Position.y > maxY) maxY = disk.Position.y;
            }

            // Apply smoothing: blend each disk toward the weighted average
            int adjustedCount = 0;
            foreach (var disk in disksInRange)
            {
                float dx = disk.Position.x - position.x;
                float dz = disk.Position.z - position.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                // Distance-based blend: full strength at center, fades at edge
                float distFactor = 1f - Mathf.Clamp01(dist / radius);
                float blend = strength * distFactor;

                float newY = Mathf.Lerp(disk.Position.y, avgY, blend);

                if (Mathf.Abs(newY - disk.Position.y) > 0.01f)
                {
                    disk.Position = new Vector3(disk.Position.x, newY, disk.Position.z);
                    adjustedCount++;
                }
            }

            // Regenerate affected combiners
            foreach (var combiner in affectedCombiners)
            {
                if (combiner != null)
                {
                    combiner.ForceRegenerate();
                }
            }

            Console.instance.Print($"Smoothed {adjustedCount}/{disksInRange.Count} water disks in {radius:F1}m radius");
            Console.instance.Print($"  Height range: {minY:F2} - {maxY:F2} -> avg {avgY:F2} (strength {strength:F1})");
            Console.instance.Print($"  Regenerated {affectedCombiners.Count} combiner(s)");

            return true;
        }

        #endregion

        #region Place Water Type

        /// <summary>
        /// Places custom liquid of any type at cursor position.
        /// Supports all CustomLiquidType values: water, tar, ice, boiling, freezing, lava.
        /// 
        /// IMPORTANT: The custom liquid type is set on the disk BEFORE it's added to a combiner,
        /// so the combiner renders the correct material from the start. No "flash of water then swap".
        /// 
        /// Respects ALL Infinity Hammer tool options:
        /// - from=X,Z,Y : Position
        /// - circle=R : Radius (circular shape)
        /// - rect=W,D : Width,Depth (rectangular shape)
        /// - square : Square flag
        /// - depth=D : Max depth
        /// - First positional arg: liquid type name
        /// </summary>
        private static bool PlaceWaterType(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            float radius = 5f;
            float maxDepth = 5f;
            Vector3 position = Vector3.zero;
            bool positionFromArgs = false;
            CustomLiquidType liquidType = CustomLiquidType.Water;
            bool typeSpecified = false;
            WaterDiskShape shape = WaterDiskShape.Circle;
            float shapeWidth = 0f;
            float shapeLength = 0f;
            bool hasRect = false;

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
                            if (radiusParts.Length > 0 && float.TryParse(radiusParts[0], out float r))
                            {
                                radius = Mathf.Clamp(r, MIN_RADIUS, MAX_RADIUS);
                                shape = WaterDiskShape.Circle;
                            }
                            break;

                        case "rect":
                            string[] rectParts = value.Split(',');
                            if (rectParts.Length >= 2 &&
                                float.TryParse(rectParts[0], out float rw) &&
                                float.TryParse(rectParts[1], out float rd))
                            {
                                shapeWidth = Mathf.Clamp(rw, MIN_RADIUS, MAX_RADIUS * 2f);
                                shapeLength = Mathf.Clamp(rd, MIN_RADIUS, MAX_RADIUS * 2f);
                                hasRect = true;
                                shape = Mathf.Approximately(shapeWidth, shapeLength)
                                    ? WaterDiskShape.Square
                                    : WaterDiskShape.Rectangle;
                            }
                            else if (rectParts.Length >= 1 && float.TryParse(rectParts[0], out float rs))
                            {
                                shapeWidth = Mathf.Clamp(rs, MIN_RADIUS, MAX_RADIUS * 2f);
                                shapeLength = shapeWidth;
                                hasRect = true;
                                shape = WaterDiskShape.Square;
                            }
                            break;

                        case "depth":
                            if (float.TryParse(value, out float d))
                                maxDepth = Mathf.Clamp(d, MIN_DEPTH, MAX_DEPTH);
                            break;

                        case "from":
                            string[] coords = value.Split(',');
                            if (coords.Length >= 2)
                            {
                                float.TryParse(coords[0], out float x);
                                float.TryParse(coords[1], out float z);
                                float fromY = 0f;
                                if (coords.Length >= 3)
                                    float.TryParse(coords[2], out fromY);
                                position = new Vector3(x, fromY, z);
                                positionFromArgs = true;
                            }
                            break;

                        case "type":
                            liquidType = ParseLiquidType(value);
                            typeSpecified = true;
                            break;
                    }
                }
                else
                {
                    // Try to parse as liquid type name
                    string lower = arg.ToLower();
                    switch (lower)
                    {
                        case "water":
                            liquidType = CustomLiquidType.Water; typeSpecified = true; break;
                        case "tar":
                            liquidType = CustomLiquidType.Tar; typeSpecified = true; break;
                        case "ice":
                        case "frozen":
                            liquidType = CustomLiquidType.Ice; typeSpecified = true; break;
                        case "boiling":
                        case "hot":
                            liquidType = CustomLiquidType.BoilingWater; typeSpecified = true; break;
                        case "freezing":
                        case "cold":
                            liquidType = CustomLiquidType.FreezingWater; typeSpecified = true; break;
                        case "lava":
                        case "magma":
                            liquidType = CustomLiquidType.Lava; typeSpecified = true; break;
                        case "square":
                            if (!hasRect)
                            {
                                shape = WaterDiskShape.Square;
                                shapeWidth = radius * 2f;
                                shapeLength = shapeWidth;
                                hasRect = true;
                            }
                            break;
                        default:
                            // Try as radius
                            if (float.TryParse(arg, out float numValue))
                                radius = Mathf.Clamp(numValue, MIN_RADIUS, MAX_RADIUS);
                            break;
                    }
                }
            }

            if (!typeSpecified)
            {
                Console.instance.Print("Usage: place_water_type <water|tar|ice|boiling|freezing|lava> [circle=R] [depth=D]");
                return false;
            }

            if (!positionFromArgs)
            {
                position = GetPlacementPosition(player);
                if (position == Vector3.zero)
                {
                    Console.instance.Print("Could not determine placement position");
                    return false;
                }
            }

            // Create the water disk with the CORRECT custom type from the start
            // This prevents the "flash of water then swap" issue
            LiquidType vanillaType = liquidType.ToVanillaLiquidType();
            WaterDisk disk;
            string sizeDesc;

            if (hasRect && shape != WaterDiskShape.Circle)
            {
                disk = new WaterDisk(position, shape, shapeWidth, shapeLength, maxDepth, vanillaType);
                sizeDesc = $"{shapeWidth:F1}x{shapeLength:F1}m ({shape})";
            }
            else
            {
                disk = new WaterDisk(position, radius, maxDepth, vanillaType);
                sizeDesc = $"r={radius:F1}m (Circle)";
            }

            // Set the custom liquid type on the disk BEFORE it enters the combiner
            disk.CustomLiquidType = liquidType;

            // Check if an existing combiner already has this liquid type at this position
            // If so, just add the disk to it (don't re-set the type which causes thaw/re-freeze)
            var existingCombiner = WaterMeshCombiner.GetCombinerAt(position);
            if (existingCombiner != null && existingCombiner.CustomLiquidType == liquidType)
            {
                // Same type - just add the new disk without touching the liquid type
                existingCombiner.AddWaterDisk(disk);

                Console.instance.Print($"Extended {liquidType.GetDisplayName()}: {sizeDesc}, depth={maxDepth:F1}m at Y={position.y:F2}m");
                return true;
            }

            // Create via RPC for multiplayer sync
            int stableHash = WaterOperationRPC.RequestCreateWaterWithCustomType(position, disk, liquidType);

            if (stableHash != 0)
            {
                Console.instance.Print($"Placed {liquidType.GetDisplayName()}: {sizeDesc}, depth={maxDepth:F1}m at Y={position.y:F2}m");
                return true;
            }
            else
            {
                Console.instance.Print("Failed to create liquid");
                return false;
            }
        }

        /// <summary>
        /// Parses a string to a CustomLiquidType.
        /// </summary>
        private static CustomLiquidType ParseLiquidType(string typeStr)
        {
            switch (typeStr.ToLower())
            {
                case "water": return CustomLiquidType.Water;
                case "tar": return CustomLiquidType.Tar;
                case "ice": case "frozen": return CustomLiquidType.Ice;
                case "boiling": case "hot": return CustomLiquidType.BoilingWater;
                case "freezing": case "cold": return CustomLiquidType.FreezingWater;
                case "lava": case "magma": return CustomLiquidType.Lava;
                default: return CustomLiquidType.Water;
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets the placement position based on player's aim/hover point.
        /// </summary>
        private static Vector3 GetPlacementPosition(Player player)
        {
            // Try camera raycast first
            var camera = GameCamera.instance;
            if (camera != null)
            {
                Ray ray = new Ray(camera.transform.position, camera.transform.forward);
                int layerMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");

                if (Physics.Raycast(ray, out RaycastHit hit, 100f, layerMask))
                {
                    return hit.point;
                }
            }

            // Fallback: player look direction
            int fallbackMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
            if (Physics.Raycast(player.GetHeadPoint(), player.GetLookDir(), out RaycastHit lookHit, 50f, fallbackMask))
            {
                return lookHit.point;
            }

            // Last resort: player position + forward
            return player.transform.position + player.transform.forward * 5f;
        }

        #endregion
    }
}
