using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    /// <summary>
    /// Console commands for terrain-anchor snow:
    ///   place_land_snow / delete_land_snow / snow_land_raise / lower /
    ///   level / smooth.
    ///
    /// EXACT clone of the LandIce command surface; only the underlying
    /// controller is swapped for LandSnowController (visual-only, no
    /// collider, no water-volume coupling). All other behavior — IH arg
    /// parsing, ruler radius/offset, position resolution — is identical.
    /// </summary>
    public static class LandSnowCommands
    {
        private static bool _initialized = false;

        private const float MIN_RADIUS = 1f;
        private const float MAX_RADIUS = 30f;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            new Terminal.ConsoleCommand("place_land_snow",
                "[args] - Places a snow overlay disk on terrain. circle=R for radius, offset=H for height.",
                (Terminal.ConsoleEventFailable)(args => PlaceLandSnow(args)), isCheat: true);

            new Terminal.ConsoleCommand("delete_land_snow",
                "[args] - Removes land snow in radius. circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => DeleteLandSnow(args)), isCheat: true);

            new Terminal.ConsoleCommand("snow_land_raise",
                "[args] - Raises land snow height. raise=H for amount, circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => RaiseLandSnow(args)), isCheat: true);

            new Terminal.ConsoleCommand("snow_land_lower",
                "[args] - Lowers land snow height. lower=H for amount, circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => LowerLandSnow(args)), isCheat: true);

            new Terminal.ConsoleCommand("snow_land_level",
                "[args] - Levels land snow to target height. circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => LevelLandSnow(args)), isCheat: true);

            new Terminal.ConsoleCommand("snow_land_smooth",
                "[args] - Smooths land snow surface. circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => SmoothLandSnow(args)), isCheat: true);

            FiresLogger.LogInfo("[LandSnowCommands] Initialized - Land snow commands registered");
        }

        #region Place / Delete

        private static bool PlaceLandSnow(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            float radius = 5f;
            float heightOffset = 0f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, "offset", ref heightOffset))
                return false;

            position.y += heightOffset;

            // 2026-05-26 round-26 §2.2: spawn via dedicated VALandSnow
            // anchor (NOT ice1) so persistence lives on a clean ZDO with
            // the `va_landsnow_*` keyspace, not shared with anything else.
            var ctrl = VerdantsAscent.Water.Core.LandSnowAnchor.SpawnNew(position);
            if (ctrl == null)
            {
                Console.instance.Print("Failed to spawn land snow anchor — VALandSnow prefab not registered yet.");
                return false;
            }

            ctrl.Initialize(position, radius);
            Console.instance.Print($"Placed land snow: r={radius:F1}m on terrain at Y={position.y:F2}m");

            // Merge with any existing overlapping snow disks so overlapping
            // footprints render as a single smoothed mesh rather than two
            // disks fighting at the boundary (mirrors LandIceCombiner).
            var surviving = LandSnowCombiner.TryCombineNearby(ctrl);

            FiresLogger.LogInfo($"[LandSnowCommands] Placed land snow at {position}, r={radius}, surviving={(surviving != null ? surviving.Footprints.Count : 0)} footprint(s)");
            return true;
        }

        private static bool DeleteLandSnow(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float dummy = 0f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, null, ref dummy))
                return false;

            var disks = LandSnowController.GetLandSnowInRadius(position, radius);
            int count = disks.Count;
            foreach (var disk in disks)
            {
                if (disk != null) disk.Remove();
            }

            Console.instance.Print($"Deleted {count} land snow disk(s) within {radius:F1}m");
            return true;
        }

        #endregion

        #region Height Modification

        private static bool RaiseLandSnow(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float amount = 0.5f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, "raise", ref amount))
                return false;

            var ctrl = LandSnowController.GetLandSnowAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No land snow at this position");
                return false;
            }

            ctrl.ModifyHeights(position, amount, radius);
            Console.instance.Print($"Raised land snow by {amount:F2}m in r={radius:F1}m");
            return true;
        }

        private static bool LowerLandSnow(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float amount = 0.5f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, "lower", ref amount))
                return false;

            var ctrl = LandSnowController.GetLandSnowAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No land snow at this position");
                return false;
            }

            ctrl.ModifyHeights(position, -amount, radius);
            Console.instance.Print($"Lowered land snow by {amount:F2}m in r={radius:F1}m");
            return true;
        }

        private static bool LevelLandSnow(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float level = float.MinValue;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, "level", ref level))
                return false;

            var ctrl = LandSnowController.GetLandSnowAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No land snow at this position");
                return false;
            }

            float targetHeight = level > -9999f ? level : position.y;
            ctrl.LevelHeights(position, radius, targetHeight);
            Console.instance.Print($"Leveled land snow to Y={targetHeight:F2}m in r={radius:F1}m");
            return true;
        }

        private static bool SmoothLandSnow(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float dummy = 0f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, null, ref dummy))
                return false;

            var ctrl = LandSnowController.GetLandSnowAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No land snow at this position");
                return false;
            }

            ctrl.SmoothHeights(position, radius);
            Console.instance.Print($"Smoothed land snow in r={radius:F1}m");
            return true;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Parses common IH args: from=X,Z,Y, circle=R, and an optional named float param.
        /// </summary>
        private static bool ParseArgs(Terminal.ConsoleEventArgs args, out Vector3 position,
            ref float radius, string floatKey, ref float floatValue)
        {
            position = Vector3.zero;
            bool positionFromArgs = false;

            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance.Print("No local player found");
                return false;
            }

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.Contains("=")) continue;

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
                            if (coords.Length >= 3) float.TryParse(coords[2], out fromY);
                            position = new Vector3(x, fromY, z);
                            positionFromArgs = true;
                        }
                        break;
                    default:
                        if (floatKey != null && key == floatKey)
                        {
                            if (float.TryParse(value, out float fv))
                                floatValue = fv;
                        }
                        break;
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

            return true;
        }

        private static Vector3 GetPlacementPosition(Player player)
        {
            var camera = GameCamera.instance;
            if (camera != null)
            {
                Ray ray = new Ray(camera.transform.position, camera.transform.forward);
                int layerMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");
                if (Physics.Raycast(ray, out RaycastHit hit, 100f, layerMask))
                    return hit.point;
            }

            int fallbackMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
            if (Physics.Raycast(player.GetHeadPoint(), player.GetLookDir(), out RaycastHit lookHit, 50f, fallbackMask))
                return lookHit.point;

            return player.transform.position + player.transform.forward * 5f;
        }

        /// <summary>
        /// Clones the vanilla ice1 prefab for ZNetView/ZDO persistence.
        /// The visuals get stripped inside LandSnowController.Initialize.
        /// </summary>
        private static GameObject CloneAnchorPrefab(Vector3 position)
        {
            if (ZNetScene.instance == null) return null;

            GameObject prefab = ZNetScene.instance.GetPrefab("ice1");
            if (prefab == null)
                prefab = ZNetScene.instance.GetPrefab("ice_floor");
            if (prefab == null)
                return null;

            GameObject go = Object.Instantiate(prefab, position, Quaternion.identity);

            var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var r in renderers)
                if (r != null) r.enabled = false;

            var lodGroup = go.GetComponent<LODGroup>();
            if (lodGroup != null) lodGroup.enabled = false;

            var existingColliders = go.GetComponentsInChildren<MeshCollider>(true);
            foreach (var c in existingColliders)
                if (c != null) c.enabled = false;

            FiresLogger.LogInfo($"[LandSnowCommands] Cloned ice1 prefab at {position} (ZNetView={go.GetComponent<ZNetView>() != null})");
            return go;
        }

        #endregion
    }
}
