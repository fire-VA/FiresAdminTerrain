using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Terrain.Utilities;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// Placeable environment box that acts as a custom EnvZone.
    /// 
    /// Features:
    /// - Scalable with Shift+MouseWheel (uses Structure Tweaks style keybinds)
    /// - Toggleable environment, weather, biome override
    /// - Toggleable skybox (inherit world, black void, custom)
    /// - Toggleable visibility (full, edges only, hidden with marker sign)
    /// - Does NOT affect heightmap - only terrain paint, weather, and environment
    /// 
    /// This is a hammer-placeable piece that creates dungeon-interior-like zones anywhere.
    /// </summary>
    public class EnvironmentBoxController : MonoBehaviour, Hoverable, Interactable
    {
        #region Static Registry

        private static List<EnvironmentBoxController> _allBoxes = new List<EnvironmentBoxController>();
        private static EnvironmentBoxController _playerInsideBox = null;

        /// <summary>
        /// Gets the environment box the player is currently inside, if any.
        /// </summary>
        public static EnvironmentBoxController GetPlayerEnvBox() => _playerInsideBox;

        /// <summary>
        /// Gets an environment box at the given position.
        /// </summary>
        public static EnvironmentBoxController GetEnvBoxAtPosition(Vector3 pos)
        {
            foreach (var box in _allBoxes)
            {
                if (box != null && box.ContainsPoint(pos))
                    return box;
            }
            return null;
        }

        #endregion

        #region Configuration

        /// <summary>
        /// Available environment names from vanilla game.
        /// Complete list from EnvMan.m_environments dump.
        /// </summary>
        public static readonly string[] EnvironmentNames = {
            "",  // No override (use world environment)
            // Clear weather
            "Clear",
            "Twilight_Clear",
            "Heath clear",
            "Clear Winter",
            "Clear Summer",
            "Heath clear Winter",
            "Heath clear Summer",
            // Forest/Mist
            "Misty",
            "DeepForest Mist",
            "Misty Winter",
            "Misty Summer",
            "DeepForest Mist Winter",
            "DeepForest Mist Summer",
            // Dark/Swamp
            "Darklands_dark",
            "Darklands_dark Winter",
            "Swamp Summer",
            // Rain
            "Rain",
            "LightRain",
            "ThunderStorm",
            "SwampRain",
            "Rain Winter",
            "LightRain Winter",
            "ThunderStorm Winter",
            "ThunderStorm Fall",
            "SwampRain Winter",
            "SwampRain Fall",
            "SwampRain Summer",
            // Snow/Mountain
            "Snow",
            "Twilight_Snow",
            "Twilight_SnowStorm",
            "SnowStorm",
            // Dungeon interiors
            "Crypt",
            "SunkenCrypt",
            "Caves",
            "CavesHildir",
            "InfectedMine",
            "CryptHildir",
            "Ghosts",
            // Mistlands
            "Mistlands_clear",
            "Mistlands_rain",
            "Mistlands_thunder",
            "Mistlands_clear Winter",
            "Mistlands_clear Summer",
            "Mistlands_rain Winter",
            "Mistlands_thunder Winter",
            // Ashlands
            "Ashlands_ashrain",
            "Ashlands_ashrain_clear",
            "Ashlands_storm",
            "Ashlands_meteorshower",
            "Ashlands_misty",
            "Ashlands_CinderRain",
            "Ashlands_SeaStorm",
            // Boss environments
            "Eikthyr",
            "GDKing",
            "Bonemass",
            "Moder",
            "GoblinKing",
            "Queen",
            "Fader",
            // Special
            "nofogts",
            "DeepNorth_Freezing"
        };

        /// <summary>
        /// Available biomes for override.
        /// </summary>
        public static readonly Heightmap.Biome[] BiomeOptions = {
            Heightmap.Biome.None,  // No override
            Heightmap.Biome.Meadows,
            Heightmap.Biome.BlackForest,
            Heightmap.Biome.Swamp,
            Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains,
            Heightmap.Biome.Mistlands,
            Heightmap.Biome.AshLands,
            Heightmap.Biome.DeepNorth,
            Heightmap.Biome.Ocean
        };

        #endregion

        #region Serialized Settings

        [Header("Box Settings")]
        public Vector3 BoxSize = new Vector3(10f, 5f, 10f);
        public Vector3 MinSize = new Vector3(2f, 2f, 2f);
        public Vector3 MaxSize = new Vector3(200f, 100f, 200f);

        [Header("Lock State")]
        // When true, all configuration cycles (E + modifiers, Shift+H visibility,
        // Shift+Scroll scaling) are blocked even for admins. Toggle with Ctrl+L
        // while hovering. Default false so existing boxes stay editable on
        // upgrade; admins explicitly lock once configured.
        public bool Locked = false;

        [Header("Environment")]
        public string EnvironmentName = "";  // Empty = no override
        public bool ForceEnvironment = true;  // If true, forces env regardless of time/weather

        [Header("Biome Override")]
        public Heightmap.Biome ForcedBiome = Heightmap.Biome.None;
        public bool AffectsTerrainPaint = true;  // Affects our custom terrain paint
        public bool AffectsVanillaPaint = true;  // Affects vanilla heightmap paint queries (default true for biome visual override)

        [Header("Skybox")]
        public SkyboxMode CurrentSkyboxMode = SkyboxMode.Inherit;
        public Color AmbientColorOverride = new Color(0.1f, 0.1f, 0.15f);

        [Header("Visibility")]
        public VisibilityMode CurrentVisibility = VisibilityMode.Full;

        [Header("Wind")]
        public WindIntensity CurrentWindIntensity = WindIntensity.Inherit;

        [Header("Time of Day")]
        public ForcedTimeOfDay CurrentTimeOfDay = ForcedTimeOfDay.Inherit;

        #endregion

        #region Enums

        public enum SkyboxMode
        {
            Inherit,        // Use world skybox
            DungeonBlack,   // Pure black void (like dungeon interiors)
            DarkCave,       // Very dark with slight ambient
            Custom          // Custom ambient color
        }

        public enum VisibilityMode
        {
            Full,           // Semi-transparent box visible
            EdgesOnly,      // Only wireframe edges
            Hidden          // Invisible, spawns marker sign at top
        }

        public enum WindIntensity
        {
            Inherit,        // Use world wind (no override)
            None,           // No wind (0.0)
            Light,          // Light breeze (0.1)
            Slight,         // Slight wind (0.2)
            Moderate,       // Moderate wind (0.4)
            Strong,         // Strong wind (0.6)
            Heavy           // Heavy wind (0.8)
        }

        /// <summary>
        /// Forced time of day options.
        /// These correspond to day fractions (0-1) where:
        /// - 0.0 = Midnight
        /// - 0.25 = Morning/Dawn
        /// - 0.5 = Midday/Noon
        /// - 0.75 = Evening/Dusk
        /// </summary>
        public enum ForcedTimeOfDay
        {
            Inherit,        // Use world time (no override)
            Midnight,       // 0.0 - Deep night
            Dawn,           // 0.21 - Early morning, sun rising
            Morning,        // 0.30 - Mid morning
            Midday,         // 0.50 - High noon, brightest
            Afternoon,      // 0.65 - Late afternoon
            Dusk,           // 0.79 - Evening, sun setting
            Night           // 0.875 - Night time
        }

        #endregion

        #region Runtime State

        private ZNetView m_nview;
        private BoxCollider m_trigger;
        private MeshFilter m_meshFilter;
        private MeshRenderer m_meshRenderer;
        private GameObject m_hiddenMarker;
        private bool m_playerInside = false;

        // Original environment state for restoration
        private Material m_originalSkybox;
        private AmbientMode m_originalAmbientMode;
        private Color m_originalAmbientColor;

        // ZDO hash codes
        private static readonly int HASH_SIZE_X = "envbox_sx".GetStableHashCode();
        private static readonly int HASH_SIZE_Y = "envbox_sy".GetStableHashCode();
        private static readonly int HASH_SIZE_Z = "envbox_sz".GetStableHashCode();
        private static readonly int HASH_ENV = "envbox_env".GetStableHashCode();
        private static readonly int HASH_FORCE_ENV = "envbox_fenv".GetStableHashCode();
        private static readonly int HASH_BIOME = "envbox_biome".GetStableHashCode();
        private static readonly int HASH_SKYBOX = "envbox_sky".GetStableHashCode();
        private static readonly int HASH_VISIBILITY = "envbox_vis".GetStableHashCode();
        private static readonly int HASH_AMB_R = "envbox_ar".GetStableHashCode();
        private static readonly int HASH_AMB_G = "envbox_ag".GetStableHashCode();
        private static readonly int HASH_AMB_B = "envbox_ab".GetStableHashCode();
        private static readonly int HASH_WIND = "envbox_wind".GetStableHashCode();
        private static readonly int HASH_TIME = "envbox_time".GetStableHashCode();
        private static readonly int HASH_AFFECTS_VANILLA = "envbox_avp".GetStableHashCode();
        private static readonly int HASH_LOCKED = "envbox_locked".GetStableHashCode();

        // Original wind state for restoration
        private float m_originalWindMin;
        private float m_originalWindMax;
        private bool m_windOverrideActive = false;

        // Time override state
        private bool m_timeOverrideActive = false;

        // Enclosure mesh for blocking outside light/sky
        private GameObject m_enclosureMesh;
        private MeshFilter m_enclosureMeshFilter;
        private MeshRenderer m_enclosureMeshRenderer;

        // Original fog state for restoration
        private bool m_originalFog;
        private Color m_originalFogColor;
        private float m_originalFogDensity;

        // Snow shader state
        private bool m_snowShaderActive = false;
        private float? m_origSnowAmount;

        // Original box material for restoration
        private Material m_originalBoxMaterial;

        // Custom clutter spawner for biome override
        private EnvironmentBoxClutterSpawner m_clutterSpawner;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            m_nview = GetComponent<ZNetView>();

            // Setup trigger collider
            m_trigger = GetComponent<BoxCollider>();
            if (m_trigger == null)
            {
                m_trigger = gameObject.AddComponent<BoxCollider>();
            }
            m_trigger.isTrigger = true;
            m_trigger.size = BoxSize;

            // Setup mesh components
            m_meshFilter = GetComponent<MeshFilter>();
            m_meshRenderer = GetComponent<MeshRenderer>();

            if (m_meshFilter == null)
            {
                m_meshFilter = gameObject.AddComponent<MeshFilter>();
            }
            if (m_meshRenderer == null)
            {
                m_meshRenderer = gameObject.AddComponent<MeshRenderer>();
            }

            // Add custom clutter spawner for biome overrides
            m_clutterSpawner = GetComponent<EnvironmentBoxClutterSpawner>();
            if (m_clutterSpawner == null)
            {
                m_clutterSpawner = gameObject.AddComponent<EnvironmentBoxClutterSpawner>();
            }

            // Register
            _allBoxes.Add(this);

            // Ensure the scaler singleton exists for Shift+H and Shift+Scroll keybinds
            EnvironmentBoxScaler.EnsureExists();

            // Load from ZDO
            if (m_nview != null && m_nview.GetZDO() != null)
            {
                LoadFromZDO();
            }

            // Apply initial state
            UpdateBoxMesh();
            ApplyVisibility();
        }


        private void OnDestroy()
        {
            _allBoxes.Remove(this);

            // Clean up if player was inside
            if (_playerInsideBox == this)
            {
                OnPlayerExit();
                _playerInsideBox = null;
            }

            // Clean up marker
            if (m_hiddenMarker != null)
            {
                Destroy(m_hiddenMarker);
            }

            // Clean up enclosure mesh
            if (m_enclosureMesh != null)
            {
                Destroy(m_enclosureMesh);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            var player = other.GetComponent<Player>();
            if (player != null && player == Player.m_localPlayer)
            {
                OnPlayerEnter();
            }
        }

        private void OnTriggerExit(Collider other)
        {
            var player = other.GetComponent<Player>();
            if (player != null && player == Player.m_localPlayer)
            {
                OnPlayerExit();
            }
        }

        private void OnTriggerStay(Collider other)
        {
            // Maintain state while player is inside
            var player = other.GetComponent<Player>();
            if (player != null && player == Player.m_localPlayer)
            {
                if (!m_playerInside)
                {
                    OnPlayerEnter();
                }

                // Keep forcing environment while inside
                if (ForceEnvironment && !string.IsNullOrEmpty(EnvironmentName))
                {
                    EnvMan.instance?.SetForceEnvironment(EnvironmentName);
                }
            }
        }

        private void LateUpdate()
        {
            // Keep hidden marker at top of box (handles external scaling like Structure Tweaks)
            if (m_hiddenMarker != null && CurrentVisibility == VisibilityMode.Hidden)
            {
                UpdateHiddenMarkerPosition();
            }

            // Validate player is still inside - catches teleports, flying out, etc.
            // OnTriggerExit doesn't fire for instant position changes
            ValidatePlayerInside();
        }

        /// <summary>
        /// Validates that the player marked as "inside" is actually still inside the box.
        /// This catches cases where the player teleported or flew out without triggering OnTriggerExit.
        /// </summary>
        private void ValidatePlayerInside()
        {
            // Only check if we think the player is inside THIS box
            if (!m_playerInside) return;
            if (_playerInsideBox != this) return;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                // Player doesn't exist anymore, clean up
                OnPlayerExit();
                return;
            }

            // Check if player is ACTUALLY inside the box bounds
            if (!ContainsPoint(player.transform.position))
            {
                Debug.Log($"[EnvironmentBox] Player no longer inside box (teleported/flew out?), triggering exit");
                OnPlayerExit();
            }
        }

        #endregion

        #region Player Enter/Exit

        private void OnPlayerEnter()
        {
            if (m_playerInside) return;
            m_playerInside = true;
            _playerInsideBox = this;

            Debug.Log($"[EnvironmentBox] Player entered box: {gameObject.name}");
            Debug.Log($"[EnvironmentBox] ForcedBiome={ForcedBiome}, AffectsVanillaPaint={AffectsVanillaPaint}");

            // IMPORTANT: Notify patches that biome override is active FIRST
            // This must happen before refreshing terrain/clutter so the patches know to apply overrides
            if (ForcedBiome != Heightmap.Biome.None)
            {
                Debug.Log($"[EnvironmentBox] Calling SetHasBiomeOverrideBoxes(true)");
                EnvironmentBoxPatches.SetHasBiomeOverrideBoxes(true);
                Debug.Log($"[EnvironmentBox] HasAnyBiomeOverrideBoxes() now returns: {EnvironmentBoxPatches.HasAnyBiomeOverrideBoxes()}");
            }

            // Apply environment
            if (!string.IsNullOrEmpty(EnvironmentName) && EnvMan.instance != null)
            {
                if (ForceEnvironment)
                {
                    EnvMan.instance.SetForceEnvironment(EnvironmentName);
                }
            }

            // Apply skybox override
            ApplySkyboxOverride();

            // Apply wind override
            ApplyWindOverride();

            // Apply time of day override
            ApplyTimeOverride();

            // Apply biome shader effects (snow for Mountain/DeepNorth)
            ApplyBiomeShaders();

            // Refresh vanilla terrain paint to match biome override
            // This MUST happen AFTER SetHasBiomeOverrideBoxes(true) and AFTER _playerInsideBox is set
            if (ForcedBiome != Heightmap.Biome.None && AffectsVanillaPaint)
            {
                Debug.Log($"[EnvironmentBox] About to RefreshTerrainInBounds - _playerInsideBox={(_playerInsideBox != null ? _playerInsideBox.gameObject.name : "NULL")}");
                RefreshTerrainInBounds();
                RefreshClutterInBounds();
            }

            // Apply snow to building pieces (Mountain/DeepNorth)
            ApplySnowToBuildingPieces();
        }

        private void OnPlayerExit()
        {
            if (!m_playerInside) return;
            m_playerInside = false;

            if (_playerInsideBox == this)
            {
                _playerInsideBox = null;
            }

            Debug.Log($"[EnvironmentBox] Player exited box: {gameObject.name}");

            // Clear forced environment
            if (ForceEnvironment && EnvMan.instance != null)
            {
                EnvMan.instance.SetForceEnvironment("");
            }

            // Restore skybox
            RestoreSkybox();

            // Restore wind
            RestoreWind();

            // Restore time of day
            RestoreTime();

            // Remove snow from building pieces (before restoring shaders)
            RemoveSnowFromBuildingPieces();

            // Restore biome shader effects
            RestoreBiomeShaders();

            // Restore vanilla terrain to original biome appearance
            if (ForcedBiome != Heightmap.Biome.None && AffectsVanillaPaint)
            {
                RestoreTerrainInBounds();
            }
        }

        private void ApplySkyboxOverride()
        {
            if (CurrentSkyboxMode == SkyboxMode.Inherit)
            {
                // Destroy enclosure if switching back to inherit
                DestroyEnclosureMesh();
                return;
            }

            // Store original settings
            m_originalSkybox = RenderSettings.skybox;
            m_originalAmbientMode = RenderSettings.ambientMode;
            m_originalAmbientColor = RenderSettings.ambientLight;
            m_originalFog = RenderSettings.fog;
            m_originalFogColor = RenderSettings.fogColor;
            m_originalFogDensity = RenderSettings.fogDensity;

            // Store original box material before changing it
            m_originalBoxMaterial = m_meshRenderer.material;

            switch (CurrentSkyboxMode)
            {
                case SkyboxMode.DungeonBlack:
                    // Create FULL 6-sided enclosure - blocks ALL light/sky like vanilla dungeons
                    // Use pure black for maximum darkness
                    CreateEnclosureMesh(true, Color.black);
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(0.01f, 0.01f, 0.015f); // Very dark ambient
                    RenderSettings.fog = true;
                    RenderSettings.fogColor = Color.black;
                    RenderSettings.fogDensity = 0.2f; // Dense fog
                    break;

                case SkyboxMode.DarkCave:
                    // Create FULL 6-sided enclosure - same structure as DungeonBlack
                    // Only difference is slightly brighter ambient lighting
                    CreateEnclosureMesh(true, new Color(0.005f, 0.005f, 0.01f));
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(0.03f, 0.03f, 0.05f);
                    RenderSettings.fog = true;
                    RenderSettings.fogColor = new Color(0.01f, 0.01f, 0.015f);
                    RenderSettings.fogDensity = 0.12f;
                    break;

                case SkyboxMode.Custom:
                    // Create full enclosure with custom ambient
                    CreateEnclosureMesh(true, AmbientColorOverride * 0.1f);
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = AmbientColorOverride;
                    break;
            }
        }

        private void RestoreSkybox()
        {
            // Always destroy enclosure when exiting
            DestroyEnclosureMesh();

            if (CurrentSkyboxMode == SkyboxMode.Inherit) return;

            // Restore original render settings
            if (m_originalSkybox != null)
            {
                RenderSettings.skybox = m_originalSkybox;
            }
            RenderSettings.ambientMode = m_originalAmbientMode;
            RenderSettings.ambientLight = m_originalAmbientColor;
            RenderSettings.fog = m_originalFog;
            RenderSettings.fogColor = m_originalFogColor;
            RenderSettings.fogDensity = m_originalFogDensity;
        }

        /// <summary>
        /// Makes the main env box mesh solid and opaque to block light.
        /// This ensures no light leaks through the box boundaries.
        /// </summary>
        private void MakeBoxMeshSolid(Color color)
        {
            if (m_meshRenderer == null) return;

            // Create opaque solid material
            var solidMat = CreateSolidMaterial(color);
            m_meshRenderer.material = solidMat;
            m_meshRenderer.enabled = true;
        }

        /// <summary>
        /// Restores the original box mesh material.
        /// </summary>
        private void RestoreBoxMeshMaterial()
        {
            if (m_meshRenderer == null) return;

            // Restore original material and apply visibility settings
            ApplyVisibility();
        }

        /// <summary>
        /// Creates a solid opaque material for blocking light and skybox.
        /// Uses Standard shader in opaque mode with proper depth writing.
        /// </summary>
        private Material CreateSolidMaterial(Color color)
        {
            // Use Standard shader for proper opaque rendering that blocks skybox
            var shader = Shader.Find("Standard");
            if (shader == null)
            {
                // Fallback - try to find any opaque shader
                shader = Shader.Find("Diffuse");
            }
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            var mat = new Material(shader);

            // Configure for opaque rendering
            mat.SetFloat("_Mode", 0); // Opaque mode
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1); // Enable depth writing
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry; // Render with opaque geometry

            // Set the color - use emission for unlit appearance
            mat.SetColor("_Color", new Color(color.r, color.g, color.b, 1f));
            mat.SetColor("_EmissionColor", color * 0.1f); // Slight emission so it's not pure black hole
            mat.EnableKeyword("_EMISSION");

            // Make it not receive lighting (appear flat)
            mat.SetFloat("_Glossiness", 0f);
            mat.SetFloat("_Metallic", 0f);

            return mat;
        }

        /// <summary>
        /// Creates the enclosure mesh that visually blocks outside light and sky.
        /// </summary>
        /// <param name="includeTop">If true, creates a fully enclosed box. If false, leaves top open.</param>
        /// <param name="color">The color of the enclosure walls (usually near-black).</param>
        private void CreateEnclosureMesh(bool includeTop, Color color)
        {
            // Destroy existing enclosure first
            DestroyEnclosureMesh();

            m_enclosureMesh = new GameObject("EnvBox_Enclosure");
            m_enclosureMesh.transform.SetParent(transform);
            m_enclosureMesh.transform.localPosition = Vector3.zero;
            m_enclosureMesh.transform.localRotation = Quaternion.identity;
            m_enclosureMesh.transform.localScale = Vector3.one;

            m_enclosureMeshFilter = m_enclosureMesh.AddComponent<MeshFilter>();
            m_enclosureMeshRenderer = m_enclosureMesh.AddComponent<MeshRenderer>();

            // Create the enclosure mesh
            Mesh mesh = new Mesh();
            mesh.name = "EnvBoxEnclosure";

            Vector3 half = BoxSize / 2f;
            // Make enclosure slightly larger than box to avoid z-fighting
            half *= 1.01f;

            // Vertices for box faces (viewed from inside)
            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            // Bottom face (floor)
            AddQuadInward(vertices, triangles,
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, half.z),
                new Vector3(-half.x, -half.y, half.z));

            // Front face (-Z)
            AddQuadInward(vertices, triangles,
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3(-half.x, half.y, -half.z),
                new Vector3(half.x, half.y, -half.z),
                new Vector3(half.x, -half.y, -half.z));

            // Back face (+Z)
            AddQuadInward(vertices, triangles,
                new Vector3(half.x, -half.y, half.z),
                new Vector3(half.x, half.y, half.z),
                new Vector3(-half.x, half.y, half.z),
                new Vector3(-half.x, -half.y, half.z));

            // Left face (-X)
            AddQuadInward(vertices, triangles,
                new Vector3(-half.x, -half.y, half.z),
                new Vector3(-half.x, half.y, half.z),
                new Vector3(-half.x, half.y, -half.z),
                new Vector3(-half.x, -half.y, -half.z));

            // Right face (+X)
            AddQuadInward(vertices, triangles,
                new Vector3(half.x, -half.y, -half.z),
                new Vector3(half.x, half.y, -half.z),
                new Vector3(half.x, half.y, half.z),
                new Vector3(half.x, -half.y, half.z));

            // Top face (ceiling) - only if includeTop is true
            if (includeTop)
            {
                AddQuadInward(vertices, triangles,
                    new Vector3(-half.x, half.y, half.z),
                    new Vector3(half.x, half.y, half.z),
                    new Vector3(half.x, half.y, -half.z),
                    new Vector3(-half.x, half.y, -half.z));
            }

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            m_enclosureMeshFilter.mesh = mesh;

            // Create material - opaque shader that blocks skybox
            m_enclosureMeshRenderer.material = CreateEnclosureMaterial(color);

            // Important: Don't cast/receive shadows for performance
            m_enclosureMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_enclosureMeshRenderer.receiveShadows = false;

            // Set layer - use Default layer so it renders properly and blocks skybox
            m_enclosureMesh.layer = 0; // Default layer
        }

        /// <summary>
        /// Adds a quad to the mesh with vertices wound so the face is visible from INSIDE the box.
        /// Uses REVERSED winding (clockwise when viewed from outside = counter-clockwise from inside).
        /// This means the quad will be visible from inside, invisible from outside due to backface culling.
        /// </summary>
        private void AddQuadInward(List<Vector3> vertices, List<int> triangles,
            Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
        {
            int startIndex = vertices.Count;
            vertices.Add(v0);
            vertices.Add(v1);
            vertices.Add(v2);
            vertices.Add(v3);

            // Two triangles forming the quad
            // REVERSED winding order: clockwise when viewed from outside = front face from INSIDE
            // This makes the face visible from inside, invisible from outside
            triangles.Add(startIndex + 2);
            triangles.Add(startIndex + 1);
            triangles.Add(startIndex);

            triangles.Add(startIndex + 3);
            triangles.Add(startIndex + 2);
            triangles.Add(startIndex);
        }

        /// <summary>
        /// Creates an opaque material for the enclosure walls that blocks skybox/light.
        /// Uses Standard shader configured for opaque, unlit appearance.
        /// Only renders inner faces (backface culling enabled) so invisible from outside.
        /// </summary>
        private Material CreateEnclosureMaterial(Color color)
        {
            // Use Standard shader for proper opaque rendering
            var shader = Shader.Find("Standard");
            if (shader == null)
            {
                shader = Shader.Find("Diffuse");
            }
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            var mat = new Material(shader);

            // Configure for opaque rendering - this is critical for blocking skybox
            mat.SetFloat("_Mode", 0); // Opaque mode
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1); // Enable depth writing - blocks everything behind
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry - 1; // Render before other geometry

            // Set color - use pure color with NO emission for true black
            mat.SetColor("_Color", new Color(color.r, color.g, color.b, 1f));
            mat.SetColor("_EmissionColor", Color.black); // No emission - pure dark
            mat.DisableKeyword("_EMISSION");

            // Flat, unlit appearance
            mat.SetFloat("_Glossiness", 0f);
            mat.SetFloat("_Metallic", 0f);

            // IMPORTANT: Keep default backface culling (CullMode.Back)
            // This makes the mesh invisible from outside, only visible from inside
            // The triangle winding in AddQuadInward ensures inner faces are front-facing
            mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);

            return mat;
        }

        /// <summary>
        /// Destroys the enclosure mesh if it exists.
        /// </summary>
        private void DestroyEnclosureMesh()
        {
            if (m_enclosureMesh != null)
            {
                Destroy(m_enclosureMesh);
                m_enclosureMesh = null;
                m_enclosureMeshFilter = null;
                m_enclosureMeshRenderer = null;
            }
        }

        private void ApplyWindOverride()
        {
            if (CurrentWindIntensity == WindIntensity.Inherit) return;
            if (EnvMan.instance == null) return;

            // Store original wind values from current environment
            var currentEnv = EnvMan.instance.GetCurrentEnvironment();
            if (currentEnv != null && !m_windOverrideActive)
            {
                m_originalWindMin = currentEnv.m_windMin;
                m_originalWindMax = currentEnv.m_windMax;
                m_windOverrideActive = true;
            }

            // Get wind values for our intensity level
            float windValue = GetWindValue(CurrentWindIntensity);

            // Override the current environment's wind (this affects EnvMan's wind calculation)
            if (currentEnv != null)
            {
                currentEnv.m_windMin = windValue;
                currentEnv.m_windMax = windValue + 0.1f;
            }
        }

        private void RestoreWind()
        {
            if (!m_windOverrideActive) return;
            if (EnvMan.instance == null) return;

            var currentEnv = EnvMan.instance.GetCurrentEnvironment();
            if (currentEnv != null)
            {
                currentEnv.m_windMin = m_originalWindMin;
                currentEnv.m_windMax = m_originalWindMax;
            }
            m_windOverrideActive = false;
        }

        private static float GetWindValue(WindIntensity intensity)
        {
            switch (intensity)
            {
                case WindIntensity.None: return 0.0f;
                case WindIntensity.Light: return 0.1f;
                case WindIntensity.Slight: return 0.2f;
                case WindIntensity.Moderate: return 0.4f;
                case WindIntensity.Strong: return 0.6f;
                case WindIntensity.Heavy: return 0.8f;
                default: return 0.3f; // Default moderate
            }
        }

        /// <summary>
        /// Gets the wind intensity name for display.
        /// </summary>
        public static string GetWindIntensityName(WindIntensity intensity)
        {
            switch (intensity)
            {
                case WindIntensity.Inherit: return "World Default";
                case WindIntensity.None: return "No Wind";
                case WindIntensity.Light: return "Light Breeze";
                case WindIntensity.Slight: return "Slight Wind";
                case WindIntensity.Moderate: return "Moderate Wind";
                case WindIntensity.Strong: return "Strong Wind";
                case WindIntensity.Heavy: return "Heavy Wind";
                default: return intensity.ToString();
            }
        }

        #endregion

        #region Time of Day Override

        private void ApplyTimeOverride()
        {
            if (CurrentTimeOfDay == ForcedTimeOfDay.Inherit) return;
            if (EnvMan.instance == null) return;

            float dayFraction = GetDayFraction(CurrentTimeOfDay);

            // Use EnvMan's debug time feature to force time of day
            // This is the same approach used by devcommands
            EnvMan.instance.m_debugTimeOfDay = true;
            EnvMan.instance.m_debugTime = dayFraction;

            m_timeOverrideActive = true;
            Debug.Log($"[EnvironmentBox] Applied time override: {CurrentTimeOfDay} (fraction: {dayFraction:F2})");
        }

        private void RestoreTime()
        {
            if (!m_timeOverrideActive) return;
            if (EnvMan.instance == null) return;

            // Disable debug time override
            EnvMan.instance.m_debugTimeOfDay = false;

            m_timeOverrideActive = false;
            Debug.Log("[EnvironmentBox] Restored world time");
        }

        /// <summary>
        /// Gets the day fraction (0-1) for a time of day preset.
        /// </summary>
        private static float GetDayFraction(ForcedTimeOfDay timeOfDay)
        {
            switch (timeOfDay)
            {
                case ForcedTimeOfDay.Midnight: return 0.0f;
                case ForcedTimeOfDay.Dawn: return 0.21f;  // Just before sunrise
                case ForcedTimeOfDay.Morning: return 0.30f;  // Mid-morning
                case ForcedTimeOfDay.Midday: return 0.50f;  // High noon
                case ForcedTimeOfDay.Afternoon: return 0.65f;  // Late afternoon
                case ForcedTimeOfDay.Dusk: return 0.79f;  // Just before sunset
                case ForcedTimeOfDay.Night: return 0.875f; // Night time
                default: return 0.5f;
            }
        }

        /// <summary>
        /// Gets the display name for a time of day preset.
        /// </summary>
        public static string GetTimeOfDayName(ForcedTimeOfDay timeOfDay)
        {
            switch (timeOfDay)
            {
                case ForcedTimeOfDay.Inherit: return "World Default";
                case ForcedTimeOfDay.Midnight: return "Midnight (00:00)";
                case ForcedTimeOfDay.Dawn: return "Dawn (05:00)";
                case ForcedTimeOfDay.Morning: return "Morning (07:00)";
                case ForcedTimeOfDay.Midday: return "Midday (12:00)";
                case ForcedTimeOfDay.Afternoon: return "Afternoon (15:30)";
                case ForcedTimeOfDay.Dusk: return "Dusk (19:00)";
                case ForcedTimeOfDay.Night: return "Night (21:00)";
                default: return timeOfDay.ToString();
            }
        }

        #endregion

        #region Biome Shaders

        /// <summary>
        /// Applies shader effects for the forced biome (e.g., snow for Mountain/DeepNorth).
        /// Based on TerritoryRuntime.Environment.cs pattern.
        /// </summary>
        private void ApplyBiomeShaders()
        {
            if (ForcedBiome == Heightmap.Biome.None) return;

            // Check if this biome requires snow shader
            if (BiomeRequiresSnowShader(ForcedBiome))
            {
                // Store original snow amount if not already stored
                if (!m_origSnowAmount.HasValue)
                {
                    try { m_origSnowAmount = Shader.GetGlobalFloat("_GlobalSnowAmount"); }
                    catch { m_origSnowAmount = 0f; }
                }

                // Apply snow shader globals - same as TerritoryRuntime
                Shader.SetGlobalFloat("_GlobalSnowAmount", 1f);
                Shader.SetGlobalFloat("_SnowAmount", 1f);
                Shader.SetGlobalFloat("_AddSnow", 1f);

                m_snowShaderActive = true;
                Debug.Log($"[EnvironmentBox] Applied snow shaders for biome: {ForcedBiome}");
            }

            // Note: SetHasBiomeOverrideBoxes is now called in OnPlayerEnter before this method
        }

        /// <summary>
        /// Restores shader effects to their original state.
        /// </summary>
        private void RestoreBiomeShaders()
        {
            if (m_snowShaderActive)
            {
                Shader.SetGlobalFloat("_GlobalSnowAmount", m_origSnowAmount ?? 0f);
                Shader.SetGlobalFloat("_SnowAmount", m_origSnowAmount ?? 0f);
                Shader.SetGlobalFloat("_AddSnow", 0f);

                m_snowShaderActive = false;
                Debug.Log("[EnvironmentBox] Restored original snow shader values");
            }

            // Notify patches
            EnvironmentBoxPatches.SetHasBiomeOverrideBoxes(false);
        }

        /// <summary>
        /// Checks if a biome requires snow shader effects.
        /// </summary>
        private static bool BiomeRequiresSnowShader(Heightmap.Biome biome)
        {
            return biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain;
        }

        #endregion

        #region Terrain Visual Override

        /// <summary>
        /// Refreshes vanilla terrain paint within and around this box.
        /// Forces heightmaps to regenerate, which triggers our RebuildRenderMesh patch.
        /// Does NOT affect custom DirtFloor terrain.
        /// </summary>
        private void RefreshTerrainInBounds()
        {
            if (ForcedBiome == Heightmap.Biome.None) return;

            // Get world bounds of this box
            Bounds boxBounds = GetWorldBounds();

            // Expand bounds slightly to catch nearby heightmaps
            boxBounds.Expand(10f);

            // Use the patches class to refresh heightmaps
            EnvironmentBoxPatches.RefreshHeightmapsInBounds(boxBounds);

            Debug.Log($"[EnvironmentBox] Refreshed terrain in bounds: {boxBounds.center}, size: {boxBounds.size}");
        }

        /// <summary>
        /// Refreshes vegetation/clutter (grass) to match the biome override.
        /// Uses our custom clutter spawner to bypass vanilla altitude restrictions.
        /// </summary>
        private void RefreshClutterInBounds()
        {
            if (ForcedBiome == Heightmap.Biome.None) return;

            // First, clear vanilla clutter in our area
            Bounds boxBounds = GetWorldBounds();
            EnvironmentBoxPatches.RefreshClutterInBounds(boxBounds);

            // Then spawn our custom biome-appropriate clutter
            // This bypasses vanilla altitude restrictions
            if (m_clutterSpawner != null)
            {
                m_clutterSpawner.SpawnClutterForBiome(ForcedBiome);
            }

            Debug.Log($"[EnvironmentBox] Refreshed clutter/vegetation for {ForcedBiome}");
        }

        /// <summary>
        /// Restores terrain to original biome appearance when exiting.
        /// </summary>
        private void RestoreTerrainInBounds()
        {
            // Clear our custom clutter first
            if (m_clutterSpawner != null)
            {
                m_clutterSpawner.ClearAllClutter();
            }

            // Force heightmaps to regenerate with original biome colors
            EnvironmentBoxPatches.RestoreModifiedHeightmaps();

            // Also refresh terrain in our bounds to be sure
            Bounds boxBounds = GetWorldBounds();
            boxBounds.Expand(10f);
            EnvironmentBoxPatches.RefreshHeightmapsInBounds(boxBounds);

            // Refresh vanilla clutter to restore original vegetation
            EnvironmentBoxPatches.RefreshClutterInBounds(boxBounds);
        }

        /// <summary>
        /// Gets the world-space bounds of this env box, accounting for transform scale.
        /// </summary>
        public Bounds GetWorldBounds()
        {
            Vector3 scale = transform.localScale;
            Vector3 scaledSize = new Vector3(
                BoxSize.x * scale.x,
                BoxSize.y * scale.y,
                BoxSize.z * scale.z
            );
            return new Bounds(transform.position, scaledSize);
        }

        #endregion

        #region Building Piece Snow

        /// <summary>
        /// Applies snow tinting to building pieces within this box (for Mountain/DeepNorth biomes).
        /// Uses TerritorySnowManager which only affects exposed pieces (no roof coverage).
        /// </summary>
        private void ApplySnowToBuildingPieces()
        {
            if (!BiomeRequiresSnowShader(ForcedBiome)) return;

            try
            {
                // Use our instance ID as the "territory ID" for tracking
                int boxId = GetInstanceID();

                // Get box world bounds
                Bounds bounds = GetWorldBounds();
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;

                // Apply snow to all exposed pieces within our bounds
                int count = TerritorySnowManager.ApplySnowToRectangle(boxId, min, max, 0.5f);

                if (count > 0)
                {
                    Debug.Log($"[EnvironmentBox] Applied snow to {count} building pieces");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EnvironmentBox] Failed to apply snow to building pieces: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes snow tinting from building pieces when exiting.
        /// </summary>
        private void RemoveSnowFromBuildingPieces()
        {
            try
            {
                int boxId = GetInstanceID();
                TerritorySnowManager.RemoveSnowFromTerritory(boxId);
                Debug.Log("[EnvironmentBox] Removed snow from building pieces");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EnvironmentBox] Failed to remove snow from building pieces: {ex.Message}");
            }
        }

        #endregion

        #region Scaling

        /// <summary>
        /// Scales the box uniformly.
        /// </summary>
        public void ScaleUniform(float delta)
        {
            Vector3 newSize = BoxSize + Vector3.one * delta;
            SetSize(ClampSize(newSize));
        }

        /// <summary>
        /// Scales only the height (Y axis).
        /// </summary>
        public void ScaleHeight(float delta)
        {
            Vector3 newSize = BoxSize;
            newSize.y += delta;
            SetSize(ClampSize(newSize));
        }

        /// <summary>
        /// Scales width and depth (X and Z axes).
        /// </summary>
        public void ScaleHorizontal(float delta)
        {
            Vector3 newSize = BoxSize;
            newSize.x += delta;
            newSize.z += delta;
            SetSize(ClampSize(newSize));
        }

        private Vector3 ClampSize(Vector3 size)
        {
            return new Vector3(
                Mathf.Clamp(size.x, MinSize.x, MaxSize.x),
                Mathf.Clamp(size.y, MinSize.y, MaxSize.y),
                Mathf.Clamp(size.z, MinSize.z, MaxSize.z)
            );
        }

        public void SetSize(Vector3 size)
        {
            BoxSize = size;
            m_trigger.size = size;
            UpdateBoxMesh();
            UpdateHiddenMarkerPosition();

            // Update enclosure mesh if it exists
            if (m_enclosureMesh != null && m_playerInside)
            {
                // Recreate enclosure with new size
                bool includeTop = CurrentSkyboxMode == SkyboxMode.DungeonBlack || CurrentSkyboxMode == SkyboxMode.Custom;
                Color color = CurrentSkyboxMode == SkyboxMode.Custom
                    ? AmbientColorOverride * 0.2f
                    : new Color(0.01f, 0.01f, 0.015f);
                CreateEnclosureMesh(includeTop, color);
            }

            SaveToZDO();
        }

        #endregion

        #region Cycling Options

        public void CycleEnvironment()
        {
            int currentIndex = Array.IndexOf(EnvironmentNames, EnvironmentName);
            currentIndex = (currentIndex + 1) % EnvironmentNames.Length;
            EnvironmentName = EnvironmentNames[currentIndex];

            // Apply immediately if player is inside
            if (m_playerInside && !string.IsNullOrEmpty(EnvironmentName))
            {
                EnvMan.instance?.SetForceEnvironment(EnvironmentName);
            }
            else if (m_playerInside && string.IsNullOrEmpty(EnvironmentName))
            {
                EnvMan.instance?.SetForceEnvironment("");
            }

            SaveToZDO();
            ShowMessage($"Environment: {(string.IsNullOrEmpty(EnvironmentName) ? "None (World Default)" : EnvironmentName)}");
        }

        public void CycleBiome()
        {
            int currentIndex = Array.IndexOf(BiomeOptions, ForcedBiome);
            currentIndex = (currentIndex + 1) % BiomeOptions.Length;
            ForcedBiome = BiomeOptions[currentIndex];

            // Update biome shaders and terrain if player is inside
            if (m_playerInside)
            {
                RestoreBiomeShaders();

                // Update flag for patches
                if (ForcedBiome != Heightmap.Biome.None)
                {
                    EnvironmentBoxPatches.SetHasBiomeOverrideBoxes(true);
                    ApplyBiomeShaders();

                    // Refresh terrain and clutter to show new biome
                    if (AffectsVanillaPaint)
                    {
                        RefreshTerrainInBounds();
                        RefreshClutterInBounds();
                    }
                }
                else
                {
                    EnvironmentBoxPatches.SetHasBiomeOverrideBoxes(false);

                    // Restore original terrain and clutter
                    if (AffectsVanillaPaint)
                    {
                        RestoreTerrainInBounds();
                    }
                }
            }

            SaveToZDO();
            ShowMessage($"Biome Override: {(ForcedBiome == Heightmap.Biome.None ? "None" : ForcedBiome.ToString())}");
        }

        public void CycleSkyboxMode()
        {
            CurrentSkyboxMode = (SkyboxMode)(((int)CurrentSkyboxMode + 1) % Enum.GetValues(typeof(SkyboxMode)).Length);

            // Apply immediately if inside
            if (m_playerInside)
            {
                RestoreSkybox();
                ApplySkyboxOverride();
            }

            SaveToZDO();
            ShowMessage($"Skybox: {CurrentSkyboxMode}");
        }

        public void CycleVisibility()
        {
            CurrentVisibility = (VisibilityMode)(((int)CurrentVisibility + 1) % Enum.GetValues(typeof(VisibilityMode)).Length);
            ApplyVisibility();
            SaveToZDO();
            ShowMessage($"Visibility: {CurrentVisibility}");
        }

        public void CycleWindIntensity()
        {
            CurrentWindIntensity = (WindIntensity)(((int)CurrentWindIntensity + 1) % Enum.GetValues(typeof(WindIntensity)).Length);

            // Apply immediately if inside
            if (m_playerInside)
            {
                RestoreWind();
                ApplyWindOverride();
            }

            SaveToZDO();
            ShowMessage($"Wind: {GetWindIntensityName(CurrentWindIntensity)}");
        }

        public void CycleTimeOfDay()
        {
            CurrentTimeOfDay = (ForcedTimeOfDay)(((int)CurrentTimeOfDay + 1) % Enum.GetValues(typeof(ForcedTimeOfDay)).Length);

            // Apply immediately if inside
            if (m_playerInside)
            {
                RestoreTime();
                ApplyTimeOverride();
            }

            SaveToZDO();
            ShowMessage($"Time: {GetTimeOfDayName(CurrentTimeOfDay)}");
        }

        /// <summary>
        /// Toggle the lock state. While locked, all configuration cycles and
        /// scaling are blocked — this guards against accidental E/scroll
        /// presses on a finished box. Saves to ZDO immediately.
        /// </summary>
        public void ToggleLock()
        {
            Locked = !Locked;
            SaveToZDO();
            ShowMessage(Locked
                ? "<color=#ff8888>Environment Box: LOCKED</color>"
                : "<color=#88ff88>Environment Box: UNLOCKED</color>");
        }

        private void ShowMessage(string msg)
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, msg);
        }

        #endregion

        #region Visibility

        private void ApplyVisibility()
        {
            switch (CurrentVisibility)
            {
                case VisibilityMode.Full:
                    m_meshRenderer.enabled = true;
                    SetMaterial(CreateBoxMaterial(new Color(0.2f, 0.5f, 0.8f, 0.15f)));
                    DestroyHiddenMarker();
                    break;

                case VisibilityMode.EdgesOnly:
                    m_meshRenderer.enabled = true;
                    SetMaterial(CreateWireframeMaterial());
                    DestroyHiddenMarker();
                    break;

                case VisibilityMode.Hidden:
                    m_meshRenderer.enabled = false;
                    SpawnHiddenMarker();
                    break;
            }
        }

        private void SetMaterial(Material mat)
        {
            if (m_meshRenderer != null)
            {
                m_meshRenderer.material = mat;
            }
        }

        private Material CreateBoxMaterial(Color color)
        {
            // Based on shader dump, only Sprites/Default and UI/Default are available in Valheim
            // Try Sprites/Default first - it supports transparency and vertex colors
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("UI/Default");
            }

            // Fallback chain if somehow those aren't available
            if (shader == null)
            {
                shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
            }
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Transparent");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                Debug.LogWarning("[EnvironmentBox] Could not find any valid shader for box material!");
                return null;
            }

            var mat = new Material(shader);
            mat.color = color;
            mat.renderQueue = 3000; // Transparent queue

            return mat;
        }

        private Material CreateWireframeMaterial()
        {
            // Use same shader chain as box material - Sprites/Default works well for wireframe too
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("UI/Default");
            }
            if (shader == null)
            {
                shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
            }

            if (shader == null)
            {
                Debug.LogWarning("[EnvironmentBox] Could not find any valid shader for wireframe material!");
                return null;
            }

            var mat = new Material(shader);
            mat.color = new Color(0.2f, 0.8f, 0.2f, 0.5f);
            mat.renderQueue = 3000;
            return mat;
        }

        private void SpawnHiddenMarker()
        {
            if (m_hiddenMarker != null) return;

            // Create a simple marker object - don't parent it to avoid scale issues
            m_hiddenMarker = new GameObject("EnvBox_HiddenMarker");

            // Add a visible sphere as marker
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "EnvBox_MarkerOrb";
            marker.transform.SetParent(m_hiddenMarker.transform);
            marker.transform.localPosition = Vector3.zero;
            marker.transform.localScale = Vector3.one * 0.5f;

            var markerRenderer = marker.GetComponent<MeshRenderer>();
            markerRenderer.material = CreateBoxMaterial(new Color(1f, 0.5f, 0f, 0.8f));

            // Remove collider from marker sphere
            var col = marker.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Position at top of box (accounting for transform scale)
            UpdateHiddenMarkerPosition();
        }

        /// <summary>
        /// Updates the hidden marker position to stay at the top center of the box.
        /// Accounts for both BoxSize and transform.localScale.
        /// </summary>
        private void UpdateHiddenMarkerPosition()
        {
            if (m_hiddenMarker == null) return;

            // Calculate the actual world-space top of the box
            // The box center is at transform.position, half-extents are BoxSize/2 * localScale
            Vector3 scale = transform.localScale;
            float worldHalfHeight = (BoxSize.y / 2f) * scale.y;

            // Position marker at top of box + small offset
            Vector3 topCenter = transform.position + Vector3.up * (worldHalfHeight + 0.5f);
            m_hiddenMarker.transform.position = topCenter;

            // Keep marker oriented upright (don't inherit box rotation for visibility)
            m_hiddenMarker.transform.rotation = Quaternion.identity;
        }

        private void DestroyHiddenMarker()
        {
            if (m_hiddenMarker != null)
            {
                Destroy(m_hiddenMarker);
                m_hiddenMarker = null;
            }
        }

        #endregion

        #region Mesh Generation

        private void UpdateBoxMesh()
        {
            if (m_meshFilter == null) return;

            Mesh mesh = new Mesh();
            mesh.name = "EnvBoxMesh";

            Vector3 size = BoxSize;
            Vector3 half = size / 2f;

            // Create box vertices
            Vector3[] vertices = new Vector3[]
            {
                // Bottom face
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, half.z),
                new Vector3(-half.x, -half.y, half.z),
                // Top face
                new Vector3(-half.x, half.y, -half.z),
                new Vector3(half.x, half.y, -half.z),
                new Vector3(half.x, half.y, half.z),
                new Vector3(-half.x, half.y, half.z)
            };

            // Triangles for all 6 faces (inverted for inside view)
            int[] triangles = new int[]
            {
                // Bottom
                0, 2, 1, 0, 3, 2,
                // Top
                4, 5, 6, 4, 6, 7,
                // Front
                0, 1, 5, 0, 5, 4,
                // Back
                2, 3, 7, 2, 7, 6,
                // Left
                3, 0, 4, 3, 4, 7,
                // Right
                1, 2, 6, 1, 6, 5
            };

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            m_meshFilter.mesh = mesh;
        }

        #endregion

        #region Queries

        /// <summary>
        /// Checks if a point is inside this environment box.
        /// </summary>
        public bool ContainsPoint(Vector3 worldPoint)
        {
            Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
            Vector3 half = BoxSize / 2f;

            return localPoint.x >= -half.x && localPoint.x <= half.x &&
                   localPoint.y >= -half.y && localPoint.y <= half.y &&
                   localPoint.z >= -half.z && localPoint.z <= half.z;
        }

        #endregion

        #region Hoverable / Interactable

        public string GetHoverText()
        {
            // Non-admins see nothing — both hover label and full panel are hidden.
            if (!IsLocalPlayerAdmin()) return "";

            var sb = new StringBuilder();

            // Lock-state header. Always shown so admins know the state at a glance.
            if (Locked)
            {
                sb.AppendLine("<color=#ff6666><b>🔒 Environment Box — LOCKED</b></color>");
            }
            else
            {
                sb.AppendLine("<color=orange><b>Environment Box</b></color>");
            }

            sb.AppendLine($"Size: {BoxSize.x:F1}m x {BoxSize.y:F1}m x {BoxSize.z:F1}m");
            sb.AppendLine($"Environment: <color=yellow>{(string.IsNullOrEmpty(EnvironmentName) ? "None" : EnvironmentName)}</color>");
            sb.AppendLine($"Biome: <color=yellow>{(ForcedBiome == Heightmap.Biome.None ? "None" : ForcedBiome.ToString())}</color>");
            sb.AppendLine($"Skybox: <color=yellow>{CurrentSkyboxMode}</color>");
            sb.AppendLine($"Wind: <color=yellow>{GetWindIntensityName(CurrentWindIntensity)}</color>");
            sb.AppendLine($"Time: <color=yellow>{GetTimeOfDayName(CurrentTimeOfDay)}</color>");
            sb.AppendLine($"Visibility: <color=yellow>{CurrentVisibility}</color>");
            sb.AppendLine();

            if (Locked)
            {
                // When locked, only show the unlock hint. Configuration keys
                // are intentionally hidden so a curious admin doesn't try them
                // and wonder why nothing happens.
                sb.AppendLine("<color=#ff6666>Cycle keys disabled while locked.</color>");
                sb.AppendLine("[<color=yellow>Ctrl+L</color>] Unlock");
            }
            else
            {
                sb.AppendLine("[<color=yellow>E</color>] Cycle Environment");
                sb.AppendLine("[<color=yellow>Shift+E</color>] Cycle Biome");
                sb.AppendLine("[<color=yellow>Ctrl+E</color>] Cycle Skybox");
                sb.AppendLine("[<color=yellow>Alt+E</color>] Cycle Wind");
                sb.AppendLine("[<color=yellow>Ctrl+Shift+E</color>] Cycle Time");
                sb.AppendLine("[<color=yellow>Shift+H</color>] Cycle Visibility");
                sb.AppendLine("[<color=yellow>Shift+Scroll</color>] Scale");
                sb.AppendLine("[<color=yellow>Ctrl+L</color>] Lock");
            }

            return sb.ToString();
        }

        // Returning empty hides the cursor label entirely for non-admins, so
        // they never see "Environment Box" floating in their HUD when looking
        // at one. Admins still see the standard label.
        public string GetHoverName() => IsLocalPlayerAdmin() ? "Environment Box" : "";

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;

            // Silently ignore non-admins — no message, no prompt, no feedback.
            // GetHoverName/Text already hide the box from them; this is the
            // belt-and-suspenders block in case they trigger E somehow.
            if (!IsLocalPlayerAdmin()) return false;

            // While locked, all cycle commands are no-ops. The only allowed
            // input here is Ctrl+L to toggle the lock.
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool altKey = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

            if (Locked)
            {
                // Locked → block everything; show a brief reminder.
                ShowMessage("Environment Box is locked. Press Ctrl+L while hovering to unlock.");
                return false;
            }

            if (ctrl && shift)
            {
                // Ctrl+Shift+E = Cycle Time of Day
                CycleTimeOfDay();
            }
            else if (altKey)
            {
                CycleWindIntensity();
            }
            else if (ctrl)
            {
                CycleSkyboxMode();
            }
            else if (shift)
            {
                CycleBiome();
            }
            else
            {
                CycleEnvironment();
            }

            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        /// <summary>
        /// Checks if the local player has admin privileges.
        /// Uses the same admin check pattern as NpcController.
        /// </summary>
        private static bool IsLocalPlayerAdmin()
        {
            // Server is always admin
            if (ZNet.instance?.IsServer() == true)
                return true;

            // Check configSync admin status (used by mod's admin syncing system)
            if (FiresAdminTerrain.Instance?.configSync?.IsAdmin == true)
                return true;

            return false;
        }

        #endregion

        #region ZDO Persistence

        private void LoadFromZDO()
        {
            if (m_nview == null) return;
            var zdo = m_nview.GetZDO();
            if (zdo == null) return;

            // Load size
            float sx = zdo.GetFloat(HASH_SIZE_X, BoxSize.x);
            float sy = zdo.GetFloat(HASH_SIZE_Y, BoxSize.y);
            float sz = zdo.GetFloat(HASH_SIZE_Z, BoxSize.z);
            BoxSize = new Vector3(sx, sy, sz);

            // Load environment
            EnvironmentName = zdo.GetString(HASH_ENV, EnvironmentName);
            ForceEnvironment = zdo.GetBool(HASH_FORCE_ENV, ForceEnvironment);

            // Load biome
            ForcedBiome = (Heightmap.Biome)zdo.GetInt(HASH_BIOME, (int)ForcedBiome);

            // Load skybox
            CurrentSkyboxMode = (SkyboxMode)zdo.GetInt(HASH_SKYBOX, (int)CurrentSkyboxMode);

            // Load visibility
            CurrentVisibility = (VisibilityMode)zdo.GetInt(HASH_VISIBILITY, (int)CurrentVisibility);

            // Load wind
            CurrentWindIntensity = (WindIntensity)zdo.GetInt(HASH_WIND, (int)CurrentWindIntensity);

            // Load time
            CurrentTimeOfDay = (ForcedTimeOfDay)zdo.GetInt(HASH_TIME, (int)CurrentTimeOfDay);

            // Load AffectsVanillaPaint
            AffectsVanillaPaint = zdo.GetBool(HASH_AFFECTS_VANILLA, AffectsVanillaPaint);

            // Load Locked state
            Locked = zdo.GetBool(HASH_LOCKED, Locked);

            // Load ambient color
            float ar = zdo.GetFloat(HASH_AMB_R, AmbientColorOverride.r);
            float ag = zdo.GetFloat(HASH_AMB_G, AmbientColorOverride.g);
            float ab = zdo.GetFloat(HASH_AMB_B, AmbientColorOverride.b);
            AmbientColorOverride = new Color(ar, ag, ab);

            // Apply loaded state
            if (m_trigger != null)
            {
                m_trigger.size = BoxSize;
            }
        }

        private void SaveToZDO()
        {
            if (m_nview == null || !m_nview.IsOwner()) return;
            var zdo = m_nview.GetZDO();
            if (zdo == null) return;

            // Save size
            zdo.Set(HASH_SIZE_X, BoxSize.x);
            zdo.Set(HASH_SIZE_Y, BoxSize.y);
            zdo.Set(HASH_SIZE_Z, BoxSize.z);

            // Save environment
            zdo.Set(HASH_ENV, EnvironmentName);
            zdo.Set(HASH_FORCE_ENV, ForceEnvironment);

            // Save biome
            zdo.Set(HASH_BIOME, (int)ForcedBiome);

            // Save skybox
            zdo.Set(HASH_SKYBOX, (int)CurrentSkyboxMode);

            // Save visibility
            zdo.Set(HASH_VISIBILITY, (int)CurrentVisibility);

            // Save wind
            zdo.Set(HASH_WIND, (int)CurrentWindIntensity);

            // Save time
            zdo.Set(HASH_TIME, (int)CurrentTimeOfDay);

            // Save AffectsVanillaPaint
            zdo.Set(HASH_AFFECTS_VANILLA, AffectsVanillaPaint);

            // Save Locked state
            zdo.Set(HASH_LOCKED, Locked);

            // Save ambient color
            zdo.Set(HASH_AMB_R, AmbientColorOverride.r);
            zdo.Set(HASH_AMB_G, AmbientColorOverride.g);
            zdo.Set(HASH_AMB_B, AmbientColorOverride.b);
        }

        #endregion
    }
}
