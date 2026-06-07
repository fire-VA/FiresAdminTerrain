using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Terrain.Clutter;

namespace VerdantsAscent.Terrain.Biome
{
    /// <summary>
    /// Paints biome overlay effects (snow, lava, etc.) on vanilla terrain.
    /// 
    /// This works by modifying the vertex colors of Heightmap render meshes,
    /// which controls the biome texture blending in the terrain shader.
    /// 
    /// Unlike Environment Boxes (which override biome while player is inside),
    /// this system PERMANENTLY paints the terrain until manually reset.
    /// 
    /// Features:
    /// - Paint snow overlay (Mountain biome color) anywhere
    /// - Paint lava/ash overlay (AshLands biome color) anywhere
    /// - Paint any biome overlay regardless of actual world biome
    /// - Persistent until reset or repainted
    /// - Works with Infinity Hammer tool system
    /// 
    /// IMPORTANT: This does NOT change terrain height or actual biome data.
    /// It only changes the visual texture blending.
    /// </summary>
    public static class BiomeOverlayPainter
    {
        #region Reflection Cache

        private static bool _reflectionCached = false;
        private static FieldInfo _renderMeshField;
        private static FieldInfo _cornerBiomesField;
        private static MethodInfo _rebuildRenderMeshMethod;

        private static void EnsureReflectionCached()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;

            try
            {
                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

                _renderMeshField = typeof(Heightmap).GetField("m_renderMesh", instanceFlags);
                _cornerBiomesField = typeof(Heightmap).GetField("m_cornerBiomes", instanceFlags);
                _rebuildRenderMeshMethod = typeof(Heightmap).GetMethod("RebuildRenderMesh", instanceFlags);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomeOverlayPainter] Failed to cache reflection: {ex.Message}");
            }
        }

        #endregion

        #region Paint Types

        /// <summary>
        /// Overlay paint types that can be applied to terrain.
        /// </summary>
        public enum OverlayType
        {
            /// <summary>Reset to original biome.</summary>
            Reset,

            /// <summary>Snow overlay (Mountain biome vertex color).</summary>
            Snow,

            /// <summary>Lava/Ash overlay (AshLands biome vertex color).</summary>
            Lava,

            /// <summary>Swamp overlay.</summary>
            Swamp,

            /// <summary>Black Forest overlay.</summary>
            BlackForest,

            /// <summary>Plains overlay.</summary>
            Plains,

            /// <summary>Mistlands overlay.</summary>
            Mistlands,

            /// <summary>Meadows overlay.</summary>
            Meadows,

            /// <summary>Deep North overlay (icy / wintry snow biome).</summary>
            DeepNorth
        }

        /// <summary>
        /// Gets the biome associated with an overlay type.
        /// </summary>
        public static Heightmap.Biome GetBiomeForOverlay(OverlayType overlay)
        {
            switch (overlay)
            {
                case OverlayType.Snow: return Heightmap.Biome.Mountain;
                case OverlayType.Lava: return Heightmap.Biome.AshLands;
                case OverlayType.Swamp: return Heightmap.Biome.Swamp;
                case OverlayType.BlackForest: return Heightmap.Biome.BlackForest;
                case OverlayType.Plains: return Heightmap.Biome.Plains;
                case OverlayType.Mistlands: return Heightmap.Biome.Mistlands;
                case OverlayType.Meadows: return Heightmap.Biome.Meadows;
                case OverlayType.DeepNorth: return Heightmap.Biome.DeepNorth;
                default: return Heightmap.Biome.None;
            }
        }

        #endregion

        #region Tracking Painted Areas

        // Track painted heightmaps so we can restore them
        private static readonly Dictionary<int, PaintedHeightmapData> _paintedHeightmaps = new Dictionary<int, PaintedHeightmapData>();

        private class PaintedHeightmapData
        {
            public Heightmap Heightmap;
            public Color32[] OriginalColors;
            public Heightmap.Biome[] OriginalCornerBiomes;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Paints a biome overlay in a circular radius.
        /// Also clears any painted clutter in the area to prevent visual mismatches,
        /// unless <paramref name="skipClutterClear"/> is true (e.g., when the caller
        /// will immediately paint its own clutter on top, like snow).
        /// 
        /// This now uses the CORRECT approach: modifying TerrainComp's paint mask directly,
        /// which persists through the vanilla save system.
        /// </summary>
        /// <param name="center">World position center of the paint operation.</param>
        /// <param name="radius">Radius in world units.</param>
        /// <param name="overlay">Type of overlay to paint.</param>
        /// <param name="blend">Blend factor 0-1. 1 = full overlay, 0.5 = half blended with original.</param>
        /// <param name="skipClutterClear">If true, skip clearing painted clutter in the area.
        /// Use when the caller will paint its own clutter immediately after (e.g., snow clutter).</param>
        /// <returns>Number of vertices affected.</returns>
        public static int PaintOverlay(Vector3 center, float radius, OverlayType overlay, float blend = 1f, bool skipClutterClear = false)
        {
            // Clear any painted clutter in the area to prevent visual mismatches
            // (e.g., meadows grass on lava overlay looks wrong).
            // Skipped when the caller manages its own clutter (e.g., snow paint command
            // paints biome overlay first, then immediately spawns snow clutter on top).
            if (!skipClutterClear)
            {
                ClutterManager.ClearInRadius(center, radius);
            }

            // Phase F: undo capture now goes through TileUndoLog (which
            // covers our tile-anchor vertex deltas; vanilla biome paint
            // is its own ZDO write and survives via ZDO replication).
            string undoName = overlay == OverlayType.Reset ? "BiomeReset" : $"BiomePaint:{overlay}";
            VerdantsAscent.Terrain.Persistence.TileUndoLog.BeginOperation(undoName, center, radius);

            int totalAffected = 0;

            if (overlay == OverlayType.Reset)
            {
                // Use the persistence system's reset method
                totalAffected = BiomePaintPersistence.ResetBiomeOverlay(center, radius);
            }
            else
            {
                // Get the target biome and use the persistence system
                Heightmap.Biome targetBiome = GetBiomeForOverlay(overlay);
                totalAffected = BiomePaintPersistence.PaintBiomeOverlay(center, radius, targetBiome);
            }

            VerdantsAscent.Terrain.Persistence.TileUndoLog.EndOperation();

            Debug.Log($"[BiomeOverlayPainter] Painted {overlay} overlay at {center}, radius {radius}, affected {totalAffected} vertices");

            return totalAffected;
        }

        /// <summary>
        /// Resets all painted overlays to original biome colors.
        /// </summary>
        public static void ResetAllOverlays()
        {
            foreach (var kvp in _paintedHeightmaps)
            {
                var data = kvp.Value;
                if (data.Heightmap == null) continue;

                try
                {
                    // Restore corner biomes
                    if (data.OriginalCornerBiomes != null && _cornerBiomesField != null)
                    {
                        _cornerBiomesField.SetValue(data.Heightmap, data.OriginalCornerBiomes);
                    }

                    // Force regenerate
                    data.Heightmap.Regenerate();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BiomeOverlayPainter] Failed to reset heightmap: {ex.Message}");
                }
            }

            _paintedHeightmaps.Clear();
            Debug.Log("[BiomeOverlayPainter] Reset all overlays");
        }

        /// <summary>
        /// Resets painted overlay in a circular area.
        /// </summary>
        public static int ResetOverlay(Vector3 center, float radius)
        {
            return PaintOverlay(center, radius, OverlayType.Reset);
        }

        #endregion

        #region Internal Implementation

        /// <summary>
        /// Paints a single heightmap with the overlay.
        /// </summary>
        private static int PaintHeightmap(Heightmap hm, Vector3 center, float radius,
            OverlayType overlay, Color32 targetColor, float blend)
        {
            try
            {
                // Get render mesh
                var renderMesh = _renderMeshField.GetValue(hm) as Mesh;
                if (renderMesh == null) return 0;

                // Store original data if not already stored
                int hmId = hm.GetInstanceID();
                if (!_paintedHeightmaps.ContainsKey(hmId))
                {
                    var data = new PaintedHeightmapData
                    {
                        Heightmap = hm,
                        OriginalColors = renderMesh.colors32,
                        OriginalCornerBiomes = _cornerBiomesField?.GetValue(hm) as Heightmap.Biome[]
                    };

                    // Clone the corner biomes array
                    if (data.OriginalCornerBiomes != null)
                    {
                        data.OriginalCornerBiomes = (Heightmap.Biome[])data.OriginalCornerBiomes.Clone();
                    }

                    _paintedHeightmaps[hmId] = data;
                }

                var originalData = _paintedHeightmaps[hmId];

                // Get heightmap properties
                var hmPos = hm.transform.position;
                float scale = hm.m_scale;
                int width = hm.m_width;
                float halfSize = width * scale * 0.5f;
                int num = width + 1;

                // Calculate origin (bottom-left corner)
                Vector3 origin = hmPos - new Vector3(halfSize, 0f, halfSize);

                // Get current colors or create new array
                Color32[] colors = renderMesh.colors32;
                if (colors == null || colors.Length != num * num)
                {
                    colors = new Color32[num * num];
                    // Initialize with original biome colors
                    for (int i = 0; i < colors.Length; i++)
                    {
                        colors[i] = originalData.OriginalColors != null && i < originalData.OriginalColors.Length
                            ? originalData.OriginalColors[i]
                            : new Color32(0, 0, 0, 0);
                    }
                }

                int affectedCount = 0;
                float radiusSq = radius * radius;

                // Paint vertices within radius
                for (int z = 0; z < num; z++)
                {
                    for (int x = 0; x < num; x++)
                    {
                        float worldX = origin.x + x * scale;
                        float worldZ = origin.z + z * scale;

                        float dx = worldX - center.x;
                        float dz = worldZ - center.z;
                        float distSq = dx * dx + dz * dz;

                        if (distSq <= radiusSq)
                        {
                            int index = z * num + x;

                            if (overlay == OverlayType.Reset)
                            {
                                // Reset to original biome color
                                Heightmap.Biome originalBiome = WorldGenerator.instance.GetBiome(worldX, worldZ);
                                colors[index] = Heightmap.GetBiomeColor(originalBiome);
                            }
                            else
                            {
                                // Apply blend if needed
                                if (blend >= 1f)
                                {
                                    colors[index] = targetColor;
                                }
                                else
                                {
                                    // Blend with original
                                    Color32 original = colors[index];
                                    colors[index] = Color32.Lerp(original, targetColor, blend);
                                }
                            }

                            affectedCount++;
                        }
                    }
                }

                if (affectedCount > 0)
                {
                    // Apply colors to mesh
                    renderMesh.colors32 = colors;

                    // Update corner biomes if needed for proper texture selection
                    UpdateCornerBiomes(hm, center, radius, overlay);
                }

                return affectedCount;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomeOverlayPainter] Error painting heightmap: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Updates corner biomes to the overlay biome for proper texture selection.
        /// NOTE: We REPLACE the biome instead of OR-ing, because combined biome flags
        /// (like Mountain | BlackForest) cause KeyNotFoundException errors in ExpandWorldData
        /// and other mods that expect single biome values.
        /// </summary>
        private static void UpdateCornerBiomes(Heightmap hm, Vector3 center, float radius, OverlayType overlay)
        {
            if (_cornerBiomesField == null || overlay == OverlayType.Reset) return;

            try
            {
                var cornerBiomes = _cornerBiomesField.GetValue(hm) as Heightmap.Biome[];
                if (cornerBiomes == null || cornerBiomes.Length < 4) return;

                var hmPos = hm.transform.position;
                float scale = hm.m_scale;
                int width = hm.m_width;
                float halfSize = width * scale * 0.5f;

                Heightmap.Biome targetBiome = GetBiomeForOverlay(overlay);

                // Check each corner
                float[] cornerX = { hmPos.x - halfSize, hmPos.x + halfSize, hmPos.x - halfSize, hmPos.x + halfSize };
                float[] cornerZ = { hmPos.z - halfSize, hmPos.z - halfSize, hmPos.z + halfSize, hmPos.z + halfSize };

                bool anyChanged = false;
                for (int i = 0; i < 4; i++)
                {
                    float dx = cornerX[i] - center.x;
                    float dz = cornerZ[i] - center.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);

                    if (dist <= radius)
                    {
                        // REPLACE the biome instead of OR-ing to avoid invalid combined biome flags
                        // Combined flags like (Mountain | BlackForest) cause dictionary lookup errors
                        cornerBiomes[i] = targetBiome;
                        anyChanged = true;
                    }
                }

                if (anyChanged)
                {
                    _cornerBiomesField.SetValue(hm, cornerBiomes);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomeOverlayPainter] Error updating corner biomes: {ex.Message}");
            }
        }

        #endregion

        #region Console Commands

        /// <summary>
        /// Registers console commands for biome overlay painting.
        /// Call this during mod initialization.
        /// </summary>
        public static void RegisterCommands()
        {
            Terminal.ConsoleCommand paintSnowCmd = new Terminal.ConsoleCommand(
                "paint_snow",
                "[radius] - Paint snow overlay on terrain at cursor position",
                (Terminal.ConsoleEventArgs args) =>
                {
                    float radius = 4f; // Default radius matching Infinity Hammer
                    if (args.Length > 1 && float.TryParse(args[1], out float r))
                        radius = r;

                    Vector3 pos = GetCursorTerrainPosition();
                    if (pos == Vector3.zero)
                    {
                        args.Context.AddString("No terrain at cursor position");
                        return;
                    }

                    int affected = PaintOverlay(pos, radius, OverlayType.Snow);
                    args.Context.AddString($"Painted snow overlay (radius={radius}): {affected} vertices");
                },
                isCheat: true,
                isNetwork: false,
                onlyServer: false
            );

            Terminal.ConsoleCommand paintLavaCmd = new Terminal.ConsoleCommand(
                "paint_lava",
                "[radius] - Paint lava/ash overlay on terrain at cursor position",
                (Terminal.ConsoleEventArgs args) =>
                {
                    float radius = 4f;
                    if (args.Length > 1 && float.TryParse(args[1], out float r))
                        radius = r;

                    Vector3 pos = GetCursorTerrainPosition();
                    if (pos == Vector3.zero)
                    {
                        args.Context.AddString("No terrain at cursor position");
                        return;
                    }

                    int affected = PaintOverlay(pos, radius, OverlayType.Lava);
                    args.Context.AddString($"Painted lava overlay (radius={radius}): {affected} vertices");
                },
                isCheat: true,
                isNetwork: false,
                onlyServer: false
            );

            // Main paint_biome command - handles all biome types
            // From Infinity Hammer with <area>: paint_biome type from=x,z,y circle=r-r2 angle=a rect=w-w2,d ignore= id=
            Terminal.ConsoleCommand paintBiomeCmd = new Terminal.ConsoleCommand(
                "paint_biome",
                "[biome] [radius] - Paint biome overlay (snow/lava/swamp/forest/plains/mistlands/meadows/reset)",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (args.Length < 2)
                    {
                        args.Context.AddString("Usage: paint_biome [biome] [radius]");
                        args.Context.AddString("Biomes: snow, lava, swamp, forest, plains, mistlands, meadows, reset");
                        return;
                    }

                    OverlayType overlay = OverlayType.Reset;
                    float radius = 4f; // Default radius for Infinity Hammer compatibility

                    // Debug: log all args
                    Debug.Log($"[BiomeOverlayPainter] paint_biome called with {args.Length} args: {string.Join(", ", args.Args)}");

                    // Parse arguments - handle Infinity Hammer's expanded <area> format
                    for (int i = 1; i < args.Length; i++)
                    {
                        string arg = args[i];
                        if (string.IsNullOrEmpty(arg)) continue;

                        // Handle key=value style arguments from Infinity Hammer
                        if (arg.Contains("="))
                        {
                            string[] parts = arg.Split(new char[] { '=' }, 2);
                            string key = parts[0].ToLower();
                            string value = parts.Length > 1 ? parts[1] : "";

                            // Extract radius from circle=r-r2 format
                            if (key == "circle" && !string.IsNullOrEmpty(value))
                            {
                                string[] radiusParts = value.Split('-');
                                if (radiusParts.Length > 0 && float.TryParse(radiusParts[0], out float r))
                                {
                                    radius = r;
                                    Debug.Log($"[BiomeOverlayPainter] Parsed radius from circle={value}: {radius}");
                                }
                            }
                            continue;
                        }

                        // Try to parse as number (legacy radius format)
                        if (float.TryParse(arg, out float numValue))
                        {
                            radius = numValue;
                            Debug.Log($"[BiomeOverlayPainter] Parsed radius from number: {radius}");
                        }
                        else
                        {
                            // Not a number - must be biome type
                            overlay = ParseOverlayType(arg);
                            Debug.Log($"[BiomeOverlayPainter] Parsed overlay type: {overlay} from '{arg}'");
                        }
                    }

                    Vector3 pos = GetCursorTerrainPosition();
                    if (pos == Vector3.zero)
                    {
                        args.Context.AddString("No terrain at cursor position");
                        return;
                    }

                    int affected = PaintOverlay(pos, radius, overlay);
                    args.Context.AddString($"Painted {overlay} overlay (radius={radius}): {affected} vertices");
                },
                isCheat: true,
                isNetwork: false,
                onlyServer: false
            );

            Terminal.ConsoleCommand resetOverlayCmd = new Terminal.ConsoleCommand(
                "reset_biome_overlay",
                "[radius] - Reset biome overlay to original at cursor position. Use 'all' to reset everything.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (args.Length > 1 && args[1].ToLower() == "all")
                    {
                        ResetAllOverlays();
                        args.Context.AddString("Reset all biome overlays");
                        return;
                    }

                    float radius = 4f;
                    if (args.Length > 1 && float.TryParse(args[1], out float r))
                        radius = r;

                    Vector3 pos = GetCursorTerrainPosition();
                    if (pos == Vector3.zero)
                    {
                        args.Context.AddString("No terrain at cursor position");
                        return;
                    }

                    int affected = ResetOverlay(pos, radius);
                    args.Context.AddString($"Reset overlay (radius={radius}): {affected} vertices");
                },
                isCheat: true,
                isNetwork: false,
                onlyServer: false
            );
        }

        private static OverlayType ParseOverlayType(string name)
        {
            switch (name.ToLower())
            {
                case "snow":
                case "mountain":
                    return OverlayType.Snow;
                case "lava":
                case "ash":
                case "ashlands":
                    return OverlayType.Lava;
                case "swamp":
                    return OverlayType.Swamp;
                case "forest":
                case "blackforest":
                    return OverlayType.BlackForest;
                case "plains":
                    return OverlayType.Plains;
                case "mistlands":
                    return OverlayType.Mistlands;
                case "meadows":
                    return OverlayType.Meadows;
                case "reset":
                default:
                    return OverlayType.Reset;
            }
        }

        private static Vector3 GetCursorTerrainPosition()
        {
            if (Player.m_localPlayer == null) return Vector3.zero;

            var cam = GameCamera.instance?.transform;
            if (cam == null) return Vector3.zero;

            Ray ray = new Ray(cam.position, cam.forward);
            int terrainMask = LayerMask.GetMask("terrain", "Default");

            if (Physics.Raycast(ray, out RaycastHit hit, 100f, terrainMask))
            {
                return hit.point;
            }

            return Vector3.zero;
        }

        #endregion
    }
}
