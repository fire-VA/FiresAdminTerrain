using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    /// <summary>
    /// Dual-mode ice placement tool for Infinity Hammer integration.
    /// 
    /// DUAL BEHAVIOR:
    /// 
    /// 1. ON WATER (vanilla or custom): Creates a <see cref="FloatingIceController"/>
    ///    with height-modifiable top surface. The ice disk is a solid circular platform
    ///    that floats on the water surface, with collision sides, hole-breaking logic,
    ///    slippery physics, and climb-out interaction.
    /// 
    /// 2. ON LAND: Creates a <see cref="LandIceController"/> with height grid.
    ///    Paints ice material over terrain, conforming to the terrain shape within the
    ///    ruler radius. Supports raise/lower/level/smooth via IH. Provides slippery
    ///    physics but no sides or holes.
    /// 
    /// COMBINING: When ice disks overlap (same mode), they are automatically merged via
    /// <see cref="FloatingIceCombiner"/> or <see cref="LandIceCombiner"/> to prevent seams.
    /// 
    /// IH PARAMETER FORMAT:
    /// - from=X,Z,Y : Position from IH ruler
    /// - circle=R : Radius from IH scroll wheel
    /// - offset=H : Height offset from Page Up/Down
    /// </summary>
    public static class IcePlacementTool
    {
        private static bool _initialized = false;

        private const float MIN_RADIUS = 1f;
        private const float MAX_RADIUS = 30f;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // place_ice [args] - Auto-detects water vs land
            new Terminal.ConsoleCommand("place_ice",
                "[args] - Places ice at cursor. On water: floating solid disk. On land: terrain ice overlay. Use circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => PlaceIce(args)), isCheat: true);

            // place_ice_land [args] - Force terrain overlay mode
            new Terminal.ConsoleCommand("place_ice_land",
                "[args] - Places ice overlay on LAND (terrain). Forces terrain overlay mode, ignoring water detection.",
                (Terminal.ConsoleEventFailable)(args => PlaceIceForced(args, forceFloating: false)), isCheat: true);

            // place_ice_water [args] - Force floating mode
            new Terminal.ConsoleCommand("place_ice_water",
                "[args] - Places floating ice on WATER. Forces floating mode, ignoring water detection.",
                (Terminal.ConsoleEventFailable)(args => PlaceIceForced(args, forceFloating: true)), isCheat: true);

            // delete_ice [args] - Removes ice disks in area
            new Terminal.ConsoleCommand("delete_ice",
                "[args] - Removes standalone ice in radius. Use circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => DeleteIce(args)), isCheat: true);

            // break_standalone_ice [args] - Punches hole in standalone ice
            new Terminal.ConsoleCommand("break_standalone_ice",
                "[args] - Breaks hole in standalone floating ice. Use circle=R for hole radius.",
                (Terminal.ConsoleEventFailable)(args => BreakStandaloneIce(args)), isCheat: true);

            FiresLogger.LogInfo("[IcePlacementTool] Initialized - Ice placement commands registered");

            // Initialize land ice height modification commands
            LandIceCommands.Initialize();

            // Initialize floating ice height modification commands
            FloatingIceCommands.Initialize();
        }

        #region Place Ice

        private static bool PlaceIce(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            float radius = 5f;
            float heightOffset = 0f;
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
                                radius = Mathf.Clamp(r, MIN_RADIUS, MAX_RADIUS);
                            break;

                        case "from":
                            // from=X,Z,Y - IH ruler position; Y is the KEY height value
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

                        case "offset":
                            // offset=<h> from IH - PageUp/PageDown height adjustment
                            if (float.TryParse(value, out float h))
                                heightOffset = h;
                            break;
                    }
                }
                else if (float.TryParse(arg, out float numValue))
                {
                    radius = Mathf.Clamp(numValue, MIN_RADIUS, MAX_RADIUS);
                }
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

            // Final height = from.Y (ruler position) + offset (PageUp/PageDown)
            position.y += heightOffset;

            // Detect if we're over water
            bool overWater = IsOverWater(position);

            // Create the ice disk by cloning the vanilla ice1 prefab.
            // This gives us ZNetView (ZDO persistence), Floating, Rigidbody,
            // 2026-05-26 round-26 §1.2 — split the placement path:
            //   * Floating ice keeps cloning ice1 (it relies on
            //     vanilla Floating/Rigidbody for buoyancy).
            //   * Land ice spawns a dedicated VALandIce anchor via
            //     LandIceAnchor.SpawnNew. ZDO data now lives on the
            //     VALandIce prefab's ZDO under `va_landice_*` keys.
            if (overWater)
            {
                GameObject go = CloneIcePrefab(position);
                if (go == null)
                {
                    go = new GameObject("StandaloneIce");
                    go.transform.position = position;
                    FiresLogger.LogWarning("[IcePlacementTool] ice1 prefab not found - using bare GameObject (no persistence)");
                }
                var controller = go.GetComponent<FloatingIceController>();
                if (controller == null)
                    controller = go.AddComponent<FloatingIceController>();

                controller.InitializeFloating(position, radius);
                Console.instance.Print($"Placed floating ice: r={radius:F1}m on water at Y={position.y:F2}m");

                var surviving = FloatingIceCombiner.TryCombineNearby(controller);
            }
            else
            {
                var landCtrl = VerdantsAscent.Water.Core.LandIceAnchor.SpawnNew(position);
                if (landCtrl == null)
                {
                    Console.instance.Print("Failed to spawn land ice anchor — VALandIce prefab not registered yet.");
                    return false;
                }
                landCtrl.Initialize(position, radius);
                Console.instance.Print($"Placed land ice: r={radius:F1}m on terrain at Y={position.y:F2}m");

                var surviving = LandIceCombiner.TryCombineNearby(landCtrl);
            }

            FiresLogger.LogInfo($"[IcePlacementTool] Placed ice ({(overWater ? "floating" : "terrain")}) at {position}, r={radius}");
            return true;
        }

        /// <summary>
        /// Places ice with forced mode (no auto-detection).
        /// </summary>
        private static bool PlaceIceForced(Terminal.ConsoleEventArgs args, bool forceFloating)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            float radius = 5f;
            float heightOffset = 0f;
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
                                radius = Mathf.Clamp(r, MIN_RADIUS, MAX_RADIUS);
                            break;

                        case "from":
                            // from=X,Z,Y - IH ruler position; Y is the KEY height value
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

                        case "offset":
                            // offset=<h> from IH - PageUp/PageDown height adjustment
                            if (float.TryParse(value, out float h))
                                heightOffset = h;
                            break;
                    }
                }
                else if (float.TryParse(arg, out float numValue))
                {
                    radius = Mathf.Clamp(numValue, MIN_RADIUS, MAX_RADIUS);
                }
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

            // Final height = from.Y (ruler position) + offset (PageUp/PageDown)
            position.y += heightOffset;

            // 2026-05-26 round-26 §1.2 — same split as PlaceIce:
            // floating uses ice1 clone, land uses VALandIce anchor.
            if (forceFloating)
            {
                GameObject go = CloneIcePrefab(position);
                if (go == null)
                {
                    go = new GameObject("StandaloneIce");
                    go.transform.position = position;
                    FiresLogger.LogWarning("[IcePlacementTool] ice1 prefab not found - using bare GameObject (no persistence)");
                }
                var controller = go.GetComponent<FloatingIceController>();
                if (controller == null)
                    controller = go.AddComponent<FloatingIceController>();

                controller.InitializeFloating(position, radius);
                Console.instance.Print($"Placed floating ice (forced): r={radius:F1}m at Y={position.y:F2}m");

                var surviving = FloatingIceCombiner.TryCombineNearby(controller);
            }
            else
            {
                var landCtrl = VerdantsAscent.Water.Core.LandIceAnchor.SpawnNew(position);
                if (landCtrl == null)
                {
                    Console.instance.Print("Failed to spawn land ice anchor — VALandIce prefab not registered yet.");
                    return false;
                }
                landCtrl.Initialize(position, radius);
                Console.instance.Print($"Placed land ice (forced): r={radius:F1}m on terrain at Y={position.y:F2}m");

                var surviving = LandIceCombiner.TryCombineNearby(landCtrl);
            }

            FiresLogger.LogInfo($"[IcePlacementTool] Placed ice forced ({(forceFloating ? "floating" : "terrain")}) at {position}, r={radius}");
            return true;
        }

        #endregion

        #region Delete Ice

        private static bool DeleteIce(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
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
                            if (radiusParts.Length > 0 && float.TryParse(radiusParts[0], out float r))
                                radius = Mathf.Clamp(r, MIN_RADIUS, MAX_RADIUS);
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
                            }
                            break;
                    }
                }
                else if (float.TryParse(arg, out float numValue))
                {
                    radius = Mathf.Clamp(numValue, MIN_RADIUS, MAX_RADIUS);
                }
            }

            int count = 0;

            // Delete LandIceController instances
            var landDisks = LandIceController.GetLandIceInRadius(position, radius);
            count += landDisks.Count;

            foreach (var landDisk in landDisks)
            {
                if (landDisk != null)
                    landDisk.Remove();
            }

            // Delete FloatingIceController instances
            var floatingDisks = FloatingIceController.GetFloatingIceInRadius(position, radius);
            count += floatingDisks.Count;

            foreach (var floatingDisk in floatingDisks)
            {
                if (floatingDisk != null)
                    floatingDisk.Remove();
            }

            Console.instance.Print($"Deleted {count} ice disk(s) within {radius:F1}m");
            return true;
        }

        #endregion

        #region Break Standalone Ice

        private static bool BreakStandaloneIce(Terminal.ConsoleEventArgs args)
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

            var floatingIce = FloatingIceController.GetFloatingIceAt(position);
            if (floatingIce == null)
            {
                Console.instance.Print("No floating ice at this position");
                return false;
            }

            if (floatingIce.BreakIceAt(position, radius))
            {
                Console.instance.Print($"Broke ice hole: r={radius:F1}m ({floatingIce.HoleCount} holes)");
                return true;
            }

            Console.instance.Print("Could not break ice at this position");
            return false;
        }

        #endregion

        #region Utility

        /// <summary>
        /// Clones the vanilla ice1 prefab at the given position.
        /// Disables the original renderer so only our custom mesh is visible.
        /// The clone retains ZNetView (for ZDO/persistence), Floating, and Rigidbody.
        /// Returns null if the prefab is not found.
        /// </summary>
        // Note: The clone is named "StandaloneIce" for ZDO prefab identification
        // in IceSlidingPatches zone reload detection.
        private static GameObject CloneIcePrefab(Vector3 position)
        {
            if (ZNetScene.instance == null) return null;

            // Try ice1 first, then ice_floor as fallback
            GameObject prefab = ZNetScene.instance.GetPrefab("ice1");
            if (prefab == null)
                prefab = ZNetScene.instance.GetPrefab("ice_floor");
            if (prefab == null)
                return null;

            // Instantiate at position - ZNetScene.Instantiate will create the ZDO
            GameObject go = Object.Instantiate(prefab, position, Quaternion.identity);
            go.name = "StandaloneIce";

            // Disable all original renderers so only our custom mesh is visible
            var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var r in renderers)
            {
                if (r != null) r.enabled = false;
            }

            // Also disable LODGroup if present (prevents it from re-enabling renderers)
            var lodGroup = go.GetComponent<LODGroup>();
            if (lodGroup != null) lodGroup.enabled = false;

            // Disable any existing MeshCollider from the original model
            // (our ice controllers create their own)
            var existingColliders = go.GetComponentsInChildren<MeshCollider>(true);
            foreach (var c in existingColliders)
            {
                if (c != null) c.enabled = false;
            }

            FiresLogger.LogInfo($"[IcePlacementTool] Cloned ice1 prefab at {position} (ZNetView={go.GetComponent<ZNetView>() != null})");
            return go;
        }

        /// <summary>
        /// Destroys an ice prefab GameObject and its ZDO.
        /// Delegates to ZNetScene.instance.Destroy which handles ownership,
        /// ZDO cleanup, and GameObject destruction.
        /// </summary>
        internal static void SafeDestroyIcePrefab(GameObject go)
        {
            if (go == null) return;

            var nview = go.GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null && ZNetScene.instance != null)
            {
                ZNetScene.instance.Destroy(go);
            }
            else
            {
                Object.Destroy(go);
            }
        }

        /// <summary>
        /// Checks if a position is over water (vanilla WaterVolume or custom WaterMeshCombiner).
        /// 
        /// IMPORTANT: The vanilla ocean WaterVolume covers the entire map at sea level (~30m).
        /// We must verify that the terrain at the placement position is actually SUBMERGED
        /// (terrain Y well below water level), not just "near" water. Otherwise, placing
        /// ice on any land near sea level incorrectly creates floating ice that falls through.
        /// </summary>
        private static bool IsOverWater(Vector3 pos)
        {
            float terrainY = GetTerrainHeight(pos);

            // Check custom water FIRST (always reliable - only exists where explicitly placed)
            var combiner = WaterMeshCombiner.GetCombinerAt(pos);
            if (combiner != null)
            {
                float customLevel = combiner.GetWaterSurfaceAt(pos);
                if (customLevel > float.MinValue && terrainY < customLevel - 0.3f)
                    return true;
            }

            // Check vanilla water volumes
            if (WaterVolume.Instances != null)
            {
                foreach (var wv in WaterVolume.Instances)
                {
                    if (wv == null) continue;
                    float waterLevel = wv.GetWaterSurface(pos, 1f);
                    if (waterLevel <= -999f) continue;

                    // The terrain must be CLEARLY submerged - at least 1m below water level.
                    // This prevents false positives on beaches and low-lying land where the
                    // ocean datum is close but the ground is not actually underwater.
                    if (terrainY < waterLevel - 1f)
                        return true;
                }
            }

            return false;
        }

        private static float GetTerrainHeight(Vector3 pos)
        {
            if (ZoneSystem.instance != null)
            {
                if (ZoneSystem.instance.GetGroundHeight(pos, out float gh))
                    return gh;
            }

            int mask = LayerMask.GetMask("terrain", "Default", "static_solid");
            if (Physics.Raycast(new Vector3(pos.x, pos.y + 100f, pos.z), Vector3.down, out RaycastHit hit, 200f, mask))
                return hit.point.y;

            return pos.y;
        }

        private static Vector3 GetPlacementPosition(Player player)
        {
            var camera = GameCamera.instance;
            if (camera != null)
            {
                Ray ray = new Ray(camera.transform.position, camera.transform.forward);
                int layerMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small", "Water");

                if (Physics.Raycast(ray, out RaycastHit hit, 100f, layerMask))
                    return hit.point;
            }

            int fallbackMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
            if (Physics.Raycast(player.GetHeadPoint(), player.GetLookDir(), out RaycastHit lookHit, 50f, fallbackMask))
                return lookHit.point;

            return player.transform.position + player.transform.forward * 5f;
        }

        #endregion
    }
}
