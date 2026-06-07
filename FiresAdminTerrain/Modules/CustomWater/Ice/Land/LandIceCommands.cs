using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    /// <summary>
    /// Console commands for land ice height modification via Infinity Hammer.
    /// These are registered by IcePlacementTool.Initialize().
    /// </summary>
    public static class LandIceCommands
    {
        private static bool _initialized = false;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            new Terminal.ConsoleCommand("ice_land_raise",
                "[args] - Raises land ice height. raise=H for amount, circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => RaiseLandIce(args)));

            new Terminal.ConsoleCommand("ice_land_lower",
                "[args] - Lowers land ice height. lower=H for amount, circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => LowerLandIce(args)));

            new Terminal.ConsoleCommand("ice_land_level",
                "[args] - Levels land ice to target height. circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => LevelLandIce(args)));

            new Terminal.ConsoleCommand("ice_land_smooth",
                "[args] - Smooths land ice surface. circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => SmoothLandIce(args)));

            new Terminal.ConsoleCommand("delete_ice_land",
                "[args] - Removes land ice in radius. circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => DeleteLandIce(args)));

            Debug.Log("[LandIceCommands] Initialized - Land ice commands registered");
        }

        private static bool RaiseLandIce(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float amount = 0.5f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, "raise", ref amount))
                return false;

            var ctrl = LandIceController.GetLandIceAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No land ice at this position");
                return false;
            }

            ctrl.ModifyHeights(position, amount, radius);
            Console.instance.Print($"Raised land ice by {amount:F2}m in r={radius:F1}m");
            return true;
        }

        private static bool LowerLandIce(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float amount = 0.5f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, "lower", ref amount))
                return false;

            var ctrl = LandIceController.GetLandIceAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No land ice at this position");
                return false;
            }

            ctrl.ModifyHeights(position, -amount, radius);
            Console.instance.Print($"Lowered land ice by {amount:F2}m in r={radius:F1}m");
            return true;
        }

        private static bool LevelLandIce(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float level = float.MinValue;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, "level", ref level))
                return false;

            var ctrl = LandIceController.GetLandIceAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No land ice at this position");
                return false;
            }

            float targetHeight = level > -9999f ? level : position.y;
            ctrl.LevelHeights(position, radius, targetHeight);
            Console.instance.Print($"Leveled land ice to Y={targetHeight:F2}m in r={radius:F1}m");
            return true;
        }

        private static bool SmoothLandIce(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float dummy = 0f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, null, ref dummy))
                return false;

            var ctrl = LandIceController.GetLandIceAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No land ice at this position");
                return false;
            }

            ctrl.SmoothHeights(position, radius);
            Console.instance.Print($"Smoothed land ice in r={radius:F1}m");
            return true;
        }

        private static bool DeleteLandIce(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float dummy = 0f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, null, ref dummy))
                return false;

            var disks = LandIceController.GetLandIceInRadius(position, radius);
            int count = disks.Count;
            foreach (var disk in disks)
            {
                if (disk != null) disk.Remove();
            }

            Console.instance.Print($"Deleted {count} land ice disk(s) within {radius:F1}m");
            return true;
        }

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
                            radius = Mathf.Clamp(r, 1f, 30f);
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
    }
}
