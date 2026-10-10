using System;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Reflective bridge to Infinity Hammer's ruler. IH exposes a static
    // class `InfinityTools.Ruler` (in InfinityHammer.dll) with static
    // fields/properties that describe the currently-rendered ruler
    // GameObject. We don't want a hard reference to IH (it's an
    // optional dep + the IH internals are subject to change without
    // notice), so we sniff it at runtime and cache reflection handles.
    //
    // What we read each frame:
    //   * `InfinityTools.Ruler.IsActive` — static property, true when
    //     the ruler is currently being rendered (player is hovering
    //     with the IH tool active).
    //   * `InfinityTools.Ruler.Projector` — static field, the
    //     projector GameObject/Component that positions the ruler
    //     circle. Its transform.position is the ruler's WORLD CENTER.
    //
    // Used by TileBrushDrag to clamp the brush position to where the
    // IH ruler is actually rendered, so the brush can't drift off
    // into the distance when the cursor raycast gets ahead of IH's
    // own ruler raycast.
    //
    // If IH isn't loaded, the probe stays inactive and callers fall
    // back to the standard cursor raycast.
    public static class InfinityHammerRulerProbe
    {
        private static bool s_resolved;
        private static bool s_available;
        private static PropertyInfo s_isActiveProp;
        private static FieldInfo s_projectorField;

        // Try once. After this the cached handles either work (s_available=true)
        // or are permanently null (s_available=false). Called lazily on first use.
        private static void TryResolve()
        {
            if (s_resolved) return;
            s_resolved = true;

            try
            {
                // Walk every loaded assembly looking for the IH ruler type.
                // Slower than `Assembly.Load("InfinityHammer")` but works
                // regardless of how IH was loaded (BepInEx plugin, manual,
                // r2modman, etc.) without needing the exact assembly name.
                Type rulerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType("InfinityTools.Ruler", throwOnError: false);
                        if (t != null) { rulerType = t; break; }
                    }
                    catch { /* malformed assemblies — skip */ }
                }
                if (rulerType == null) return;

                s_isActiveProp = rulerType.GetProperty("IsActive",
                    BindingFlags.Public | BindingFlags.Static);
                s_projectorField = rulerType.GetField("Projector",
                    BindingFlags.Public | BindingFlags.Static);

                s_available = s_isActiveProp != null && s_projectorField != null;
            }
            catch
            {
                s_available = false;
            }
        }

        // True if IH is loaded AND the ruler is currently active. When
        // false, callers should use their own raycast.
        public static bool IsRulerActive()
        {
            TryResolve();
            if (!s_available) return false;

            try
            {
                var v = s_isActiveProp.GetValue(null);
                return v is bool b && b;
            }
            catch { return false; }
        }

        // Return the ruler's current world position (Projector's
        // transform.position). Returns true if the ruler is active
        // AND the projector exists. Used to clamp the brush position
        // to where the IH ruler is rendered.
        public static bool TryGetRulerPosition(out Vector3 worldPos)
        {
            worldPos = default;
            if (!TryGetProjector(out var go)) return false;
            if (!IsRulerActive()) return false;
            worldPos = go.transform.position;
            return true;
        }

        // Round-57 — raw projector accessor for the ruler ring colorizer.
        // Unlike TryGetRulerPosition this does NOT require the ruler to be
        // active — the colorizer wants to tint the segments even while the
        // projector is momentarily hidden so the color is right the frame
        // it reappears.
        public static bool TryGetProjector(out GameObject projector)
        {
            projector = null;
            TryResolve();
            if (!s_available) return false;

            try
            {
                var proj = s_projectorField.GetValue(null);
                if (proj == null) return false;

                // The Projector field's runtime type might be DecalProjector,
                // GameObject, Component, etc. — normalize to the GameObject.
                if (proj is GameObject go) projector = go;
                else if (proj is Component comp) projector = comp.gameObject;

                return projector != null;
            }
            catch { return false; }
        }
    }
}
