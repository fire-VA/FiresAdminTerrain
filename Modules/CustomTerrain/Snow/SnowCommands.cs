using UnityEngine;
// using VerdantsAscent.Terrain.Biome;  // Disabled: biome overlay paint temporarily removed
using VerdantsAscent.Terrain.Clutter;

namespace VerdantsAscent.Terrain.Snow
{
    /// <summary>
    /// Console commands for the snow clutter + trail system.
    ///
    /// Commands:
    ///   paint_snow_clutter [radius] [density]  � paint snow clutter at cursor
    ///   clear_snow_clutter [radius|all]         � clear snow clutter
    ///   snow_trail [on|off|reset]               � toggle trail stamping
    /// </summary>
    public static class SnowCommands
    {
        public static void RegisterCommands()
        {
            new Terminal.ConsoleCommand(
                "paint_snow_clutter",
                "[radius] [density] - Paint snow clutter at cursor. Persistent across zone reload.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (!SnowClutterConfig.EnsureRegistered())
                    {
                        args.Context.AddString("Snow clutter registration failed (ClutterSystem not ready?)");
                        return;
                    }

                    float radius = 5f;
                    float density = 1.5f;

                    for (int i = 1; i < args.Length; i++)
                    {
                        string arg = args[i];
                        if (string.IsNullOrEmpty(arg)) continue;

                        // Handle Infinity Hammer key=value args
                        if (arg.Contains("="))
                        {
                            string[] parts = arg.Split(new char[] { '=' }, 2);
                            if (parts[0].ToLower() == "circle" && parts.Length > 1)
                            {
                                string[] rParts = parts[1].Split('-');
                                if (rParts.Length > 0 && float.TryParse(rParts[0], out float r))
                                    radius = r;
                            }
                            continue;
                        }

                        if (float.TryParse(arg, out float val))
                        {
                            if (i == 1) radius = val;
                            else if (i == 2) density = val;
                        }
                    }

                    Vector3 pos = GetCursorPosition();
                    if (pos == Vector3.zero)
                    {
                        args.Context.AddString("No terrain at cursor position");
                        return;
                    }

                    // DISABLED: Biome overlay paint was making clutter snow invisible.
                    // Testing with clutter-only to see if snow particles render correctly
                    // without the biome terrain paint underneath.
                    // int biomeVerts = BiomeOverlayPainter.PaintOverlay(pos, radius, BiomeOverlayPainter.OverlayType.Snow, skipClutterClear: true);

                    // Paint snow clutter � provides push-away interaction
                    int spawned = VerdantsAscent.Terrain.Clutter.ClutterManager.PaintClutter(pos, radius, SnowClutterConfig.ClutterName, density);

                    args.Context.AddString($"Painted {spawned} snow instances (radius={radius}, density={density})");
                },
                isCheat: true, isNetwork: false, onlyServer: false);

            new Terminal.ConsoleCommand(
                "clear_snow_clutter",
                "[radius|all] - Clear snow clutter at cursor or everywhere",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (args.Length > 1 && args[1].ToLower() == "all")
                    {
                        int cleared = VerdantsAscent.Terrain.Clutter.ClutterManager.ClearAllClutter();
                        args.Context.AddString($"Cleared all {cleared} clutter objects");
                        return;
                    }

                    float radius = 5f;
                    if (args.Length > 1 && float.TryParse(args[1], out float r))
                        radius = r;

                    Vector3 pos = GetCursorPosition();
                    if (pos == Vector3.zero)
                    {
                        args.Context.AddString("No terrain at cursor position");
                        return;
                    }

                    int cleared2 = VerdantsAscent.Terrain.Clutter.ClutterManager.ClearClutter(pos, radius);
                    args.Context.AddString($"Cleared {cleared2} snow clutter objects (radius={radius})");
                },
                isCheat: true, isNetwork: false, onlyServer: false);

            new Terminal.ConsoleCommand(
                "snow_trail",
                "[on|off|reset] - Toggle snow trail stamping or reset trails",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (args.Length < 2)
                    {
                        bool active = SnowTrailController.IsActive;
                        int stamps = SnowTrailPaint.StampCount;
                        args.Context.AddString($"Snow trail: {(active ? "ON" : "OFF")}, {stamps} stamps");
                        args.Context.AddString("Usage: snow_trail [on|off|reset]");
                        return;
                    }

                    string action = args[1].ToLower();
                    switch (action)
                    {
                        case "on":
                        case "true":
                        case "1":
                            SnowTrailController.Enable();
                            args.Context.AddString("Snow trail stamping enabled � walk through snow to leave trails");
                            break;

                        case "off":
                        case "false":
                        case "0":
                            SnowTrailController.Disable();
                            args.Context.AddString("Snow trail stamping disabled � trails cleared");
                            break;

                        case "reset":
                        case "clear":
                            SnowTrailController.ResetTrails();
                            args.Context.AddString("Snow trails reset � clutter will respawn on zone reload");
                            break;

                        default:
                            args.Context.AddString($"Unknown action: {action}. Use on/off/reset.");
                            break;
                    }
                },
                isCheat: true, isNetwork: false, onlyServer: false);
        }

        private static Vector3 GetCursorPosition()
        {
            if (Player.m_localPlayer == null) return Vector3.zero;

            var cam = GameCamera.instance?.transform;
            if (cam == null) return Vector3.zero;

            Ray ray = new Ray(cam.position, cam.forward);
            int mask = LayerMask.GetMask("terrain", "Default");

            if (Physics.Raycast(ray, out RaycastHit hit, 100f, mask))
                return hit.point;

            return Vector3.zero;
        }
    }
}
