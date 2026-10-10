using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Utilities; // EnvironmentBoxController (moved to Core)

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// Spawns biome-appropriate clutter (grass/vegetation) inside an Environment Box.
    /// 
    /// This bypasses Valheim's ClutterSystem altitude restrictions by spawning clutter directly.
    /// Uses the same instanced rendering approach as GrassOverlayController and vanilla ClutterSystem.
    /// 
    /// Why this exists:
    /// - Valheim's ClutterSystem has altitude checks (Mountain grass only spawns above ~100m)
    /// - Our patches on GetPatchBiomes/GetGroundInfo return the correct biome
    /// - But the clutter definitions have m_minAltitude/m_maxAltitude that block spawning
    /// - This spawner ignores those restrictions and spawns the correct biome's clutter anyway
    /// </summary>
    public class EnvironmentBoxClutterSpawner : MonoBehaviour
    {
        #region Static Clutter Cache

        private static Dictionary<string, ClutterSystem.Clutter> _clutterCache = new Dictionary<string, ClutterSystem.Clutter>();
        private static bool _clutterCached = false;

        /// <summary>
        /// Biome to clutter type mappings. Each biome can have multiple clutter types.
        /// These are the instanced_ prefabs that work with InstanceRenderer.
        /// </summary>
        private static readonly Dictionary<Heightmap.Biome, string[]> BiomeClutterTypes = new Dictionary<Heightmap.Biome, string[]>
        {
            { Heightmap.Biome.Meadows, new[] { "instanced_meadows_grass", "instanced_meadows_grass_short", "instanced_meadows_flowers" } },
            { Heightmap.Biome.BlackForest, new[] { "instanced_forest_groundcover", "instanced_forest_groundcover_brown", "instanced_ormbunke" } },
            { Heightmap.Biome.Swamp, new[] { "instanced_swamp_grass", "instanced_swamp_grass_bloom" } },
            { Heightmap.Biome.Mountain, new[] { "instanced_meadows_grass_short" } }, // Mountain has minimal grass
            { Heightmap.Biome.Plains, new[] { "instanced_heathgrass", "instanced_heathflowers" } },
            { Heightmap.Biome.Mistlands, new[] { "instanced_mistlands_grass_short" } },
            { Heightmap.Biome.AshLands, new[] { "instanced_ashlands_grass_short", "instanced_ashlands_grass_long" } },
            { Heightmap.Biome.DeepNorth, new[] { "instanced_meadows_grass_short" } }, // DeepNorth has minimal grass
            { Heightmap.Biome.Ocean, new[] { "instanced_vass", "instanced_waterlilies" } },
        };

        #endregion

        #region Instance State

        private EnvironmentBoxController _envBox;
        private List<GameObject> _spawnedClutter = new List<GameObject>();
        private bool _clutterSpawned = false;
        private Heightmap.Biome _spawnedForBiome = Heightmap.Biome.None;

        // Spawn settings
        private float _clutterDensity = 0.8f; // Slightly lower than vanilla for performance
        private float _clutterSpacing = 0.5f; // Distance between clutter instances

        // Paint mask for grass control (like DirtFloorController)
        // Black = grass, Red = dirt, Green = cultivated, Blue = paved
        private Texture2D _paintMask;
        private const int PAINT_MASK_RESOLUTION = 64;

        // ZDO key for paint mask persistence
        private static readonly int ZDO_PAINT_MASK = "envbox_paintmask".GetStableHashCode();

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _envBox = GetComponent<EnvironmentBoxController>();
        }

        private void OnDestroy()
        {
            ClearAllClutter();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Spawns clutter for the given biome inside the env box bounds.
        /// Called when player enters the box with a biome override.
        /// </summary>
        public void SpawnClutterForBiome(Heightmap.Biome biome)
        {
            if (biome == Heightmap.Biome.None)
            {
                ClearAllClutter();
                return;
            }

            // Don't respawn if already spawned for this biome
            if (_clutterSpawned && _spawnedForBiome == biome)
                return;

            // Clear existing clutter
            ClearAllClutter();

            // Cache clutter prefabs if needed
            CacheClutterPrefabs();

            if (_clutterCache.Count == 0)
            {
                FiresLogger.LogWarning("[EnvBoxClutter] No clutter prefabs cached");
                return;
            }

            // Get clutter types for this biome
            if (!BiomeClutterTypes.TryGetValue(biome, out string[] clutterTypes))
            {
                FiresLogger.LogInfo($"[EnvBoxClutter] No clutter defined for biome: {biome}");
                return;
            }

            // Get spawn bounds
            Bounds bounds = _envBox.GetWorldBounds();

            int totalSpawned = 0;

            // Spawn each clutter type
            foreach (string clutterName in clutterTypes)
            {
                if (!_clutterCache.TryGetValue(clutterName, out ClutterSystem.Clutter clutterData))
                {
                    // Try case-insensitive search
                    foreach (var kvp in _clutterCache)
                    {
                        if (kvp.Key.Equals(clutterName, StringComparison.OrdinalIgnoreCase))
                        {
                            clutterData = kvp.Value;
                            break;
                        }
                    }
                }

                if (clutterData == null || clutterData.m_prefab == null)
                {
                    FiresLogger.LogWarning($"[EnvBoxClutter] Could not find clutter: {clutterName}");
                    continue;
                }

                int spawned = SpawnClutterType(clutterData, bounds);
                totalSpawned += spawned;
            }

            _clutterSpawned = true;
            _spawnedForBiome = biome;

            FiresLogger.LogInfo($"[EnvBoxClutter] Spawned {totalSpawned} clutter instances for biome {biome}");
        }

        /// <summary>
        /// Clears all spawned clutter.
        /// Called when player exits the box or biome changes.
        /// </summary>
        public void ClearAllClutter()
        {
            foreach (var obj in _spawnedClutter)
            {
                if (obj != null)
                    Destroy(obj);
            }
            _spawnedClutter.Clear();
            _clutterSpawned = false;
            _spawnedForBiome = Heightmap.Biome.None;
        }

        #endregion

        #region Clutter Spawning

        /// <summary>
        /// Spawns instances of a specific clutter type within bounds.
        /// Uses InstanceRenderer for instanced clutter (same as vanilla).
        /// </summary>
        private int SpawnClutterType(ClutterSystem.Clutter clutterData, Bounds bounds)
        {
            if (clutterData == null || clutterData.m_prefab == null)
                return 0;

            // Calculate spawn grid - use world-space iteration to avoid strips
            float spacing = _clutterSpacing / Mathf.Max(0.1f, _clutterDensity);

            // Get terrain info
            int terrainMask = LayerMask.GetMask("terrain", "Default");
            float waterLevel = 30f;

            // Create container for this clutter type
            GameObject container = null;
            InstanceRenderer instanceRenderer = null;

            if (clutterData.m_instanced)
            {
                container = Instantiate(clutterData.m_prefab, bounds.center, Quaternion.identity);
                container.name = $"EnvBoxClutter_{clutterData.m_prefab.name}";
                container.transform.SetParent(transform, true);

                instanceRenderer = container.GetComponent<InstanceRenderer>();
                if (instanceRenderer != null)
                {
                    // Configure LOD: near=0, far=80m for full visibility
                    var irType = instanceRenderer.GetType();

                    var useLodField = irType.GetField("m_useLod", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (useLodField != null)
                        useLodField.SetValue(instanceRenderer, false);

                    var lodMinField = irType.GetField("m_lodMinDistance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (lodMinField != null)
                        lodMinField.SetValue(instanceRenderer, 0f);

                    var lodMaxField = irType.GetField("m_lodMaxDistance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (lodMaxField != null)
                        lodMaxField.SetValue(instanceRenderer, 80f);

                    instanceRenderer.Clear();
                }
                else
                {
                    FiresLogger.LogWarning($"[EnvBoxClutter] NO InstanceRenderer on prefab: {clutterData.m_prefab.name}");
                }

                _spawnedClutter.Add(container);
            }

            int count = 0;
            Vector3 minPos = bounds.min;

            List<Vector3> positions = new List<Vector3>();
            List<Quaternion> rotations = new List<Quaternion>();
            List<float> scales = new List<float>();

            // Use world-space iteration instead of grid iteration to avoid strips
            for (float wx = 0; wx < bounds.size.x; wx += spacing)
            {
                for (float wz = 0; wz < bounds.size.z; wz += spacing)
                {
                    // Add slight random offset to break up the grid pattern
                    float offsetX = wx + UnityEngine.Random.Range(0, spacing * 0.7f);
                    float offsetZ = wz + UnityEngine.Random.Range(0, spacing * 0.7f);

                    Vector3 worldPos = new Vector3(
                        minPos.x + offsetX,
                        bounds.center.y + 500f,
                        minPos.z + offsetZ
                    );

                    if (Physics.Raycast(worldPos, Vector3.down, out RaycastHit hit, 1000f, terrainMask))
                    {
                        Vector3 spawnPos = hit.point;
                        Vector3 normal = hit.normal;

                        float tilt = Mathf.Acos(Mathf.Clamp01(normal.y)) * Mathf.Rad2Deg;
                        if (tilt > clutterData.m_maxTilt || tilt < clutterData.m_minTilt)
                        {
                            continue;
                        }

                        if (clutterData.m_minOceanDepth != clutterData.m_maxOceanDepth)
                        {
                            float oceanDepth = waterLevel - spawnPos.y;
                            if (oceanDepth < clutterData.m_minOceanDepth || oceanDepth > clutterData.m_maxOceanDepth)
                            {
                                continue;
                            }
                        }

                        if (clutterData.m_snapToWater && spawnPos.y < waterLevel)
                        {
                            spawnPos.y = waterLevel;
                        }

                        if (clutterData.m_randomOffset != 0f)
                        {
                            spawnPos.y += UnityEngine.Random.Range(-clutterData.m_randomOffset, clutterData.m_randomOffset);
                        }

                        // Check paint mask - skip if this position is painted (cultivated/paved/dirt)
                        if (!ShouldSpawnGrassAtPosition(spawnPos))
                        {
                            continue;
                        }

                        float yRotation = UnityEngine.Random.Range(0f, 360f);
                        Quaternion rotation = clutterData.m_terrainTilt
                            ? Quaternion.AngleAxis(yRotation, normal)
                            : Quaternion.Euler(0f, yRotation, 0f);

                        float scale = UnityEngine.Random.Range(clutterData.m_scaleMin, clutterData.m_scaleMax);

                        positions.Add(spawnPos);
                        rotations.Add(rotation);
                        scales.Add(scale);
                        count++;
                    }
                }
            }

            // Now add all instances
            if (clutterData.m_instanced && instanceRenderer != null && count > 0)
            {
                // InstanceRenderer.AddInstance uses WORLD positions
                for (int i = 0; i < count; i++)
                {
                    instanceRenderer.AddInstance(positions[i], rotations[i], scales[i]);
                }

                instanceRenderer.enabled = true;
                container.SetActive(true);

                // Warn if we exceeded InstanceRenderer limit
                int maxInstances = 1023;
                if (count > maxInstances)
                {
                    FiresLogger.LogWarning($"[EnvBoxClutter] Exceeded instance limit ({count}/{maxInstances}) for {clutterData.m_prefab.name}");
                }
            }
            else if (!clutterData.m_instanced && count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var obj = Instantiate(clutterData.m_prefab, positions[i], rotations[i]);
                    obj.name = $"EnvBoxClutter_{i}";
                    obj.transform.SetParent(transform, true);
                    obj.transform.localScale = Vector3.one * scales[i];
                    _spawnedClutter.Add(obj);
                }
            }

            return count;
        }

        #endregion

        #region Clutter Cache

        /// <summary>
        /// Caches clutter prefabs from ClutterSystem.
        /// </summary>
        private static void CacheClutterPrefabs()
        {
            if (_clutterCached && _clutterCache.Count > 0) return;

            if (ClutterSystem.instance == null)
            {
                FiresLogger.LogWarning("[EnvBoxClutter] ClutterSystem.instance is null");
                return;
            }

            if (ClutterSystem.instance.m_clutter == null || ClutterSystem.instance.m_clutter.Count == 0)
            {
                FiresLogger.LogWarning("[EnvBoxClutter] ClutterSystem has no clutter entries");
                return;
            }

            foreach (var clutter in ClutterSystem.instance.m_clutter)
            {
                if (clutter.m_prefab == null) continue;

                string name = clutter.m_prefab.name;
                if (!_clutterCache.ContainsKey(name))
                {
                    _clutterCache[name] = clutter;
                }
            }

            _clutterCached = true;
            FiresLogger.LogInfo($"[EnvBoxClutter] Cached {_clutterCache.Count} clutter prefabs");
        }

        /// <summary>
        /// Gets all available clutter names for a biome (for debugging).
        /// </summary>
        public static string[] GetClutterNamesForBiome(Heightmap.Biome biome)
        {
            if (BiomeClutterTypes.TryGetValue(biome, out string[] types))
                return types;
            return new string[0];
        }

        #endregion

        #region Paint Mask System

        /// <summary>
        /// Ensures the paint mask texture exists and is initialized.
        /// </summary>
        private void EnsurePaintMaskExists()
        {
            if (_paintMask != null) return;

            _paintMask = new Texture2D(PAINT_MASK_RESOLUTION, PAINT_MASK_RESOLUTION, TextureFormat.RGBA32, false);
            _paintMask.name = "EnvBoxPaintMask";
            _paintMask.wrapMode = TextureWrapMode.Clamp;
            _paintMask.filterMode = FilterMode.Bilinear;

            // Initialize to black (grass/default)
            Color[] pixels = new Color[PAINT_MASK_RESOLUTION * PAINT_MASK_RESOLUTION];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.black;
            }
            _paintMask.SetPixels(pixels);
            _paintMask.Apply();
        }

        /// <summary>
        /// Paints a circle at the specified world position with the given paint type.
        /// Returns true if paint was applied.
        /// </summary>
        public bool PaintAt(Vector3 worldPosition, TerrainModifier.PaintType paintType, float radius)
        {
            if (_envBox == null) return false;

            Bounds bounds = _envBox.GetWorldBounds();

            // Check if position is within our bounds
            if (!bounds.Contains(worldPosition))
                return false;

            EnsurePaintMaskExists();

            // Convert world position to UV coordinates (0-1 range)
            float uvX = (worldPosition.x - bounds.min.x) / bounds.size.x;
            float uvZ = (worldPosition.z - bounds.min.z) / bounds.size.z;

            // Convert radius to UV space
            float uvRadiusX = radius / bounds.size.x;
            float uvRadiusZ = radius / bounds.size.z;
            float uvRadius = Mathf.Max(uvRadiusX, uvRadiusZ);

            // Get paint color based on type
            Color paintColor = GetPaintColor(paintType);

            // Paint pixels in radius
            int centerPixelX = Mathf.RoundToInt(uvX * PAINT_MASK_RESOLUTION);
            int centerPixelZ = Mathf.RoundToInt(uvZ * PAINT_MASK_RESOLUTION);
            int pixelRadius = Mathf.CeilToInt(uvRadius * PAINT_MASK_RESOLUTION);

            bool painted = false;
            for (int x = -pixelRadius; x <= pixelRadius; x++)
            {
                for (int z = -pixelRadius; z <= pixelRadius; z++)
                {
                    int px = centerPixelX + x;
                    int pz = centerPixelZ + z;

                    // Bounds check
                    if (px < 0 || px >= PAINT_MASK_RESOLUTION) continue;
                    if (pz < 0 || pz >= PAINT_MASK_RESOLUTION) continue;

                    // Circle check
                    float dist = Mathf.Sqrt(x * x + z * z);
                    if (dist > pixelRadius) continue;

                    _paintMask.SetPixel(px, pz, paintColor);
                    painted = true;
                }
            }

            if (painted)
            {
                _paintMask.Apply();

                // Respawn clutter to reflect paint changes
                if (_clutterSpawned && _spawnedForBiome != Heightmap.Biome.None)
                {
                    // Force respawn with same biome
                    var biome = _spawnedForBiome;
                    ClearAllClutter();
                    SpawnClutterForBiome(biome);
                }

                FiresLogger.LogInfo($"[EnvBoxClutter] Painted at {worldPosition} with {paintType}");
            }

            return painted;
        }

        /// <summary>
        /// Gets the paint color for a given paint type.
        /// Uses Valheim's actual Heightmap paint mask colors for shader compatibility.
        /// </summary>
        private Color GetPaintColor(TerrainModifier.PaintType paintType)
        {
            switch (paintType)
            {
                case TerrainModifier.PaintType.Dirt:
                    return Heightmap.m_paintMaskDirt;
                case TerrainModifier.PaintType.Cultivate:
                    return Heightmap.m_paintMaskCultivated;
                case TerrainModifier.PaintType.Paved:
                    return Heightmap.m_paintMaskPaved;
                case TerrainModifier.PaintType.DeepSnow:
                    return Heightmap.m_paintMaskDeepSnow;
                case TerrainModifier.PaintType.Reset:
                case TerrainModifier.PaintType.ClearVegetation:
                default:
                    return Heightmap.m_paintMaskNothing;
            }
        }

        /// <summary>
        /// Checks if grass should spawn at a position based on paint mask.
        /// Returns true if grass should spawn (paint is black/grass).
        /// </summary>
        private bool ShouldSpawnGrassAtPosition(Vector3 worldPos)
        {
            if (_paintMask == null) return true;
            if (_envBox == null) return true;

            Bounds bounds = _envBox.GetWorldBounds();

            // Convert world position to UV coordinates
            float uvX = (worldPos.x - bounds.min.x) / bounds.size.x;
            float uvZ = (worldPos.z - bounds.min.z) / bounds.size.z;

            if (uvX < 0 || uvX > 1 || uvZ < 0 || uvZ > 1) return false;

            int px = Mathf.Clamp(Mathf.RoundToInt(uvX * _paintMask.width), 0, _paintMask.width - 1);
            int pz = Mathf.Clamp(Mathf.RoundToInt(uvZ * _paintMask.height), 0, _paintMask.height - 1);

            Color pixel = _paintMask.GetPixel(px, pz);

            // Black = grass, anything else = no grass
            return pixel.r < 0.5f && pixel.g < 0.5f && pixel.b < 0.5f;
        }

        /// <summary>
        /// Gets the paint type at a specific world position.
        /// </summary>
        public TerrainModifier.PaintType GetPaintTypeAt(Vector3 worldPosition)
        {
            if (_paintMask == null) return TerrainModifier.PaintType.Reset;
            if (_envBox == null) return TerrainModifier.PaintType.Reset;

            Bounds bounds = _envBox.GetWorldBounds();

            float uvX = (worldPosition.x - bounds.min.x) / bounds.size.x;
            float uvZ = (worldPosition.z - bounds.min.z) / bounds.size.z;

            if (uvX < 0 || uvX > 1 || uvZ < 0 || uvZ > 1) return TerrainModifier.PaintType.Reset;

            int px = Mathf.Clamp(Mathf.RoundToInt(uvX * _paintMask.width), 0, _paintMask.width - 1);
            int pz = Mathf.Clamp(Mathf.RoundToInt(uvZ * _paintMask.height), 0, _paintMask.height - 1);

            Color pixel = _paintMask.GetPixel(px, pz);

            // m_paintMaskDeepSnow is the only mask with every colour channel high, so it
            // has to be tested before the single-channel cases or it reads as Dirt.
            if (pixel.r > 0.5f && pixel.g > 0.5f && pixel.b > 0.5f) return TerrainModifier.PaintType.DeepSnow;
            if (pixel.r > 0.5f) return TerrainModifier.PaintType.Dirt;
            if (pixel.g > 0.5f) return TerrainModifier.PaintType.Cultivate;
            if (pixel.b > 0.5f) return TerrainModifier.PaintType.Paved;
            return TerrainModifier.PaintType.Reset; // Grass/default
        }

        /// <summary>
        /// Gets the paint mask texture (for terrain material).
        /// </summary>
        public Texture2D GetPaintMask()
        {
            EnsurePaintMaskExists();
            return _paintMask;
        }

        #endregion
    }
}
