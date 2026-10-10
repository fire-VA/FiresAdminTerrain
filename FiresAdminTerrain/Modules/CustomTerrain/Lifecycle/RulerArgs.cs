using System.Globalization;
using UnityEngine;
using VerdantsAscent.Terrain.Persistence;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Shared parser for Infinity Hammer's `<area>` / `<h>` / `<circle>` token
    // expansions on console commands. IH expands its tokens into named
    // console args before invoking our `va_*` commands:
    //
    //   from=x,z,y       — ruler world position (WEC convention: X, Z, Y)
    //   circle=R-R2      — outer/inner ruler radius (R2 usually 0)
    //   height=H         — ruler Page Up/Down Y offset
    //
    // This used to live as private statics inside TileBrushCommands. Promoted
    // here so RegionScrubber (and any future ruler-aware caller) can reuse
    // the exact same parsing without duplicating it — single source of truth
    // for "how do we read the IH ruler state from a console event".
    //
    // Hard rule documented for callers:
    //   * When `from=` is present, ruler is active. Use ruler Y as the
    //     authoritative elevation, NOT the camera raycast.
    //   * When `from=` is absent (player ran the command bare from
    //     console), fall back to TileBrush.TryGetPointerWorldPosition.
    //   * `circle=` overrides positional radius args.
    //   * `height=` is an OFFSET from the ruler/click Y for Flat ops, a
    //     DELTA for Raise/Lower, irrelevant for biome/paint/clear.
    public static class RulerArgs
    {
        // Returns true + populated center if either `from=` was parsed OR
        // a camera raycast succeeded. `usedRuler` tells the caller whether
        // the center came from the ruler (so they know whether to respect
        // ruler Y vs cursor Y).
        public static bool TryResolveCenter(Terminal.ConsoleEventArgs args, out Vector3 center, out bool usedRuler)
        {
            usedRuler = false;
            if (TryParseFromArg(args, out center))
            {
                usedRuler = true;
                return true;
            }

            if (!TileBrush.TryGetPointerWorldPosition(out center))
            {
                args?.Context?.AddString("No local player — must be in-world.");
                return false;
            }
            return true;
        }

        // Args-less variant — purely cursor raycast. Used by callers that
        // weren't invoked via the console (e.g. direct API usage).
        public static bool TryResolveCenter(out Vector3 center, out bool usedRuler)
        {
            usedRuler = false;
            return TileBrush.TryGetPointerWorldPosition(out center);
        }

        // Round-69 — there is deliberately NO "correction" of the ruler
        // position. `from=` IS the coordinate readout the user sees in
        // the tool tooltip (proven 07-10: tooltip x:333.4 z:258.2 y:35.5,
        // command from=333.4134,258.2081,35.52475). The round-68 attempt
        // to re-anchor Y to a crosshair raycast placed water on top of
        // PREVIOUSLY PLACED water (the ray hit gelled swim boxes at
        // y=40.0 exactly) and once moved a pour 9m off-ring in XZ.
        // Water goes EXACTLY where the tool says. Period.

        // Ruler radius wins (`circle=R-R2` outer R). Falls through to the
        // positional arg at index 1 (legacy console convention), then to
        // the default.
        public static float ResolveRadius(Terminal.ConsoleEventArgs args, float defaultRadius)
        {
            if (TryParseCircleArg(args, out float ihRadius) && ihRadius > 0f) return ihRadius;
            return ParsePositionalFloat(args, 1, defaultRadius);
        }

        // Height/offset readers — separated for the two semantics:
        //   ResolveAbsoluteHeight is for Flat (ruler `<h>` ADDS to click Y)
        //   ResolveHeightDelta   is for Raise/Lower (ruler `<h>` IS the delta)
        public static float ResolveAbsoluteHeight(Terminal.ConsoleEventArgs args, float baseY)
        {
            if (TryParseNamedFloat(args, "height", out float hOffset))
                return baseY + hOffset;
            if (args != null && args.Length > 2 && args[2] != null && !args[2].Contains("=") &&
                float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float absH))
                return absH;
            return baseY;
        }

        public static float ResolveHeightDelta(Terminal.ConsoleEventArgs args, float defaultDelta)
        {
            if (TryParseNamedFloat(args, "height", out float ihDelta)) return ihDelta;
            return ParsePositionalFloat(args, 2, defaultDelta);
        }

        // ── Raw parsers ──────────────────────────────────────────────────

        public static bool TryParseFromArg(Terminal.ConsoleEventArgs args, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (args == null) return false;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null) continue;
                if (!a.StartsWith("from=", System.StringComparison.OrdinalIgnoreCase)) continue;

                string payload = a.Substring(5).Trim();
                string[] coords = payload.Split(',');
                if (coords.Length < 3) continue;

                // WEC convention: from=X,Z,Y (3D world, Y last). We map to
                // Vector3(x, y, z).
                if (float.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                    float.TryParse(coords[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    pos = new Vector3(x, y, z);
                    return true;
                }
            }
            return false;
        }

        public static bool TryParseCircleArg(Terminal.ConsoleEventArgs args, out float outerRadius)
        {
            outerRadius = 0f;
            if (args == null) return false;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null || !a.StartsWith("circle=", System.StringComparison.OrdinalIgnoreCase)) continue;
                string payload = a.Substring("circle=".Length).Trim();
                string[] parts = payload.Split('-');
                if (parts.Length < 1) continue;
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out outerRadius))
                    return true;
            }
            return false;
        }

        public static bool TryParseNamedFloat(Terminal.ConsoleEventArgs args, string name, out float value)
        {
            value = 0f;
            if (args == null) return false;
            string prefix = name + "=";
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null || !a.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                string payload = a.Substring(prefix.Length).Trim();
                if (float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return true;
            }
            return false;
        }

        public static float ParsePositionalFloat(Terminal.ConsoleEventArgs args, int idx, float def)
        {
            if (args == null || args.Length <= idx) return def;
            return float.TryParse(args[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        }
    }
}
