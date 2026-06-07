using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    /// <summary>
    /// Console commands for floating ice height modification via Infinity Hammer.
    /// These are registered by IcePlacementTool.Initialize().
    /// </summary>
    public static class FloatingIceCommands
    {
        private static bool _initialized = false;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            new Terminal.ConsoleCommand("ice_water_raise",
                "[args] - Raises floating ice top surface. raise=H for amount, circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => RaiseFloatingIce(args)));

            new Terminal.ConsoleCommand("ice_water_lower",
                "[args] - Lowers floating ice top surface. lower=H for amount, circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => LowerFloatingIce(args)));

            new Terminal.ConsoleCommand("ice_water_smooth",
                "[args] - Smooths floating ice top surface. circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => SmoothFloatingIce(args)));

            new Terminal.ConsoleCommand("delete_ice_water",
                "[args] - Removes floating ice in radius. circle=R for radius.",
                (Terminal.ConsoleEventFailable)(args => DeleteFloatingIce(args)));

            Debug.Log("[FloatingIceCommands] Initialized - Floating ice commands registered");
        }

        private static bool RaiseFloatingIce(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float amount = 0.5f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, "raise", ref amount))
                return false;

            var ctrl = FloatingIceController.GetFloatingIceAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No floating ice at this position");
                return false;
            }

            ctrl.ModifyTopHeights(position, amount, radius);
            Console.instance.Print($"Raised floating ice by {amount:F2}m in r={radius:F1}m");
            return true;
        }

        private static bool LowerFloatingIce(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float amount = 0.5f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, "lower", ref amount))
                return false;

            var ctrl = FloatingIceController.GetFloatingIceAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No floating ice at this position");
                return false;
            }

            ctrl.ModifyTopHeights(position, -amount, radius);
            Console.instance.Print($"Lowered floating ice by {amount:F2}m in r={radius:F1}m");
            return true;
        }

        private static bool SmoothFloatingIce(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float dummy = 0f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, null, ref dummy))
                return false;

            var ctrl = FloatingIceController.GetFloatingIceAt(position);
            if (ctrl == null)
            {
                Console.instance.Print("No floating ice at this position");
                return false;
            }

            ctrl.SmoothTopHeights(position, radius);
            Console.instance.Print($"Smoothed floating ice in r={radius:F1}m");
            return true;
        }

        private static bool DeleteFloatingIce(Terminal.ConsoleEventArgs args)
        {
            float radius = 5f;
            float dummy = 0f;
            Vector3 position;
            if (!ParseArgs(args, out position, ref radius, null, ref dummy))
                return false;

            var disks = FloatingIceController.GetFloatingIceInRadius(position, radius);
            int count = disks.Count;
            foreach (var disk in disks)
            {
                if (disk != null) disk.Remove();
            }

            Console.instance.Print($"Deleted {count} floating ice disk(s) within {radius:F1}m");
            return true;
        }

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
