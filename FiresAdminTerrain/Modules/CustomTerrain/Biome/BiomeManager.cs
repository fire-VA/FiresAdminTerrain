using UnityEngine;
using VerdantsAscent.Utilities;
using FiresCore.Utilities; // EnvironmentBoxController (moved to Core)

namespace VerdantsAscent.Terrain.Biome
{
    /// <summary>
    /// Central manager for ALL biome-related queries and modifications.
    /// 
    /// This is the SINGLE ENTRY POINT for:
    /// - Querying effective biome at a position
    /// - Painting biome overlays
    /// - Getting biome colors for terrain
    /// 
    /// Biome priority:
    /// 1. Painted biome overlays (BiomePaintPersistence)
    /// 2. Environment box overrides (EnvironmentBoxController)
    /// 3. World generator (WorldGenerator.GetBiome)
    /// 
    /// Consolidates logic from:
    /// - BiomePaintPersistence.cs (painted biome storage)
    /// - BiomeOverlayPainter.cs (vanilla heightmap painting)
    /// - DirtFloorMeshCombiner.Paint.cs (GetBiomeColorForPosition)
    /// - EnvironmentBoxPatches.cs (env box biome override)
    /// </summary>
    public static class BiomeManager
    {
        #region Biome Queries

        /// <summary>
        /// Gets the effective biome at a world position.
        /// 
        /// Priority:
        /// 1. Painted biome overlays
        /// 2. Environment box override
        /// 3. World generator
        /// </summary>
        /// <param name="worldPos">World position to query</param>
        /// <returns>The effective biome at that position</returns>
        public static Heightmap.Biome GetBiomeAt(Vector3 worldPos)
        {
            // 1. Check painted biome overlays first (highest priority)
            var paintedBiome = BiomePaintPersistence.GetPaintAtPosition(worldPos.x, worldPos.z);
            if (paintedBiome != null)
            {
                return paintedBiome.Biome;
            }

            // 2. Check environment box override
            var envBox = EnvironmentBoxController.GetEnvBoxAtPosition(worldPos);
            if (envBox != null && envBox.ForcedBiome != Heightmap.Biome.None)
            {
                return envBox.ForcedBiome;
            }

            // 3. Fall back to world generator
            if (WorldGenerator.instance != null)
            {
                return WorldGenerator.instance.GetBiome(worldPos);
            }

            return Heightmap.Biome.Meadows;
        }

        /// <summary>
        /// Gets the effective biome at XZ coordinates.
        /// </summary>
        /// <param name="worldX">World X coordinate</param>
        /// <param name="worldZ">World Z coordinate</param>
        /// <returns>The effective biome at that position</returns>
        public static Heightmap.Biome GetBiomeAt(float worldX, float worldZ)
        {
            return GetBiomeAt(new Vector3(worldX, 0f, worldZ));
        }

        /// <summary>
        /// Checks if there's a painted biome overlay at a position.
        /// </summary>
        /// <param name="worldPos">World position to check</param>
        /// <returns>True if a biome has been painted at this position</returns>
        public static bool HasPaintedBiome(Vector3 worldPos)
        {
            return BiomePaintPersistence.GetPaintAtPosition(worldPos.x, worldPos.z) != null;
        }

        /// <summary>
        /// Checks if there are any painted biome areas in the world.
        /// </summary>
        public static bool HasAnyPaintedAreas => BiomePaintPersistence.HasAnyPaintedAreas();

        #endregion

        #region Biome Painting

        /// <summary>
        /// Paints a biome overlay at a position.
        /// Updates vertex colors on custom terrain and refreshes clutter.
        /// </summary>
        /// <param name="center">World position center</param>
        /// <param name="radius">Paint radius</param>
        /// <param name="biome">Biome to paint</param>
        /// <returns>Number of terrain tiles affected</returns>
        public static int PaintBiome(Vector3 center, float radius, Heightmap.Biome biome)
        {
            // 1. Record the biome paint for persistence
            int affected = BiomePaintPersistence.PaintBiomeOverlay(center, radius, biome);

            // 2. Refresh custom terrain vertex colors
            RefreshTerrainBiomeColors(center, radius);

            // 3. Refresh clutter for new biome
            Clutter.ClutterManager.RefreshInRadius(center, radius);

            return affected;
        }

        /// <summary>
        /// Resets biome overlay at position to original world biome.
        /// </summary>
        /// <param name="center">World position center</param>
        /// <param name="radius">Reset radius</param>
        /// <returns>Number of terrain tiles affected</returns>
        public static int ResetBiome(Vector3 center, float radius)
        {
            int affected = BiomePaintPersistence.ResetBiomeOverlay(center, radius);
            RefreshTerrainBiomeColors(center, radius);
            Clutter.ClutterManager.RefreshInRadius(center, radius);
            return affected;
        }

        /// <summary>
        /// Paints biome overlay on vanilla heightmaps.
        /// Updates vertex colors and corner biomes.
        /// </summary>
        /// <param name="center">World position center</param>
        /// <param name="radius">Paint radius</param>
        /// <param name="overlay">Overlay type (Snow, Ash, Swamp, etc.)</param>
        /// <param name="blend">Blend strength 0-1</param>
        /// <returns>Number of vertices affected</returns>
        public static int PaintVanillaOverlay(Vector3 center, float radius, BiomeOverlayPainter.OverlayType overlay, float blend = 1f)
        {
            return BiomeOverlayPainter.PaintOverlay(center, radius, overlay, blend);
        }

        /// <summary>
        /// Resets vanilla heightmap overlays in a radius.
        /// </summary>
        public static int ResetVanillaOverlay(Vector3 center, float radius)
        {
            return BiomeOverlayPainter.ResetOverlay(center, radius);
        }

        /// <summary>
        /// Resets all vanilla heightmap overlays in the world.
        /// </summary>
        public static void ResetAllVanillaOverlays()
        {
            BiomeOverlayPainter.ResetAllOverlays();
        }

        #endregion

        #region Biome Colors

        /// <summary>
        /// Gets the appropriate vertex color for a biome.
        /// Used for terrain mesh coloring.
        /// </summary>
        /// <param name="biome">The biome</param>
        /// <returns>Color32 for vertex coloring</returns>
        public static Color32 GetBiomeColor(Heightmap.Biome biome)
        {
            // Use Valheim's built-in biome color calculation
            return Heightmap.GetBiomeColor(biome);
        }

        /// <summary>
        /// Gets the biome color at a world position.
        /// Takes into account painted biomes and environment boxes.
        /// </summary>
        /// <param name="worldPos">World position</param>
        /// <returns>Color32 for vertex coloring</returns>
        public static Color32 GetBiomeColorAt(Vector3 worldPos)
        {
            Heightmap.Biome biome = GetBiomeAt(worldPos);
            return GetBiomeColor(biome);
        }

        /// <summary>
        /// Gets the biome color at XZ coordinates.
        /// </summary>
        public static Color32 GetBiomeColorAt(float worldX, float worldZ)
        {
            return GetBiomeColorAt(new Vector3(worldX, 0f, worldZ));
        }

        /// <summary>
        /// Gets a display-friendly grass tint color for a biome.
        /// Used for grass coloring.
        /// </summary>
        /// <param name="biome">The biome</param>
        /// <returns>Grass tint color</returns>
        public static Color GetGrassColor(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows:
                    return new Color(0.35f, 0.55f, 0.25f, 1f); // Bright green

                case Heightmap.Biome.BlackForest:
                    return new Color(0.25f, 0.4f, 0.2f, 1f); // Darker green

                case Heightmap.Biome.Swamp:
                    return new Color(0.3f, 0.35f, 0.2f, 1f); // Murky green-brown

                case Heightmap.Biome.Plains:
                    return new Color(0.6f, 0.55f, 0.3f, 1f); // Golden/tan

                case Heightmap.Biome.Mistlands:
                    return new Color(0.35f, 0.3f, 0.35f, 1f); // Purple-ish gray

                case Heightmap.Biome.Mountain:
                case Heightmap.Biome.DeepNorth:
                    return new Color(0.7f, 0.75f, 0.8f, 1f); // Snowy white

                case Heightmap.Biome.AshLands:
                    return new Color(0.3f, 0.25f, 0.25f, 1f); // Ashy dark

                default:
                    return new Color(0.35f, 0.5f, 0.25f, 1f); // Default grass green
            }
        }

        #endregion

        #region Terrain Integration

        /// <summary>
        /// Phase F: legacy combiner refresh removed. New tile system rebuilds
        /// vertex colors automatically via TileMeshRenderer when biome data
        /// changes on a TerrainTile (no explicit refresh needed).
        /// </summary>
        private static void RefreshTerrainBiomeColors(Vector3 center, float radius)
        {
            // No-op — tile meshes self-refresh on Tile.NotifyMutated.
        }

        /// <summary>
        /// Phase F: legacy combiner-wide refresh removed.
        /// </summary>
        public static void RefreshAllBiomeColors()
        {
            // No-op — tile meshes self-refresh.
        }

        #endregion

        #region Biome Properties

        /// <summary>
        /// Checks if a biome should have snow overlay.
        /// </summary>
        public static bool BiomeHasSnow(Heightmap.Biome biome)
        {
            return biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth;
        }

        /// <summary>
        /// Checks if a biome should have grass/vegetation.
        /// </summary>
        public static bool BiomeHasGrass(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Mountain:
                case Heightmap.Biome.DeepNorth:
                case Heightmap.Biome.Ocean:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Checks if a biome is a cold/freezing biome.
        /// </summary>
        public static bool BiomeIsCold(Heightmap.Biome biome)
        {
            return biome == Heightmap.Biome.Mountain ||
                   biome == Heightmap.Biome.DeepNorth;
        }

        /// <summary>
        /// Checks if a biome is a hot/burning biome.
        /// </summary>
        public static bool BiomeIsHot(Heightmap.Biome biome)
        {
            return biome == Heightmap.Biome.AshLands;
        }

        /// <summary>
        /// Gets a human-readable name for a biome.
        /// </summary>
        public static string GetBiomeName(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return "Meadows";
                case Heightmap.Biome.BlackForest: return "Black Forest";
                case Heightmap.Biome.Swamp: return "Swamp";
                case Heightmap.Biome.Mountain: return "Mountain";
                case Heightmap.Biome.Plains: return "Plains";
                case Heightmap.Biome.AshLands: return "Ashlands";
                case Heightmap.Biome.DeepNorth: return "Deep North";
                case Heightmap.Biome.Mistlands: return "Mistlands";
                case Heightmap.Biome.Ocean: return "Ocean";
                default: return biome.ToString();
            }
        }

        #endregion
    }
}
