using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Tints the whole rendered scene when the player's eye is below a water
    /// surface. Complements <see cref="FiresWaterShaderLoader"/>: the shader
    /// handles the surface mesh itself (visible from below with refraction +
    /// tint), this driver handles everything else the player sees — rocks,
    /// fish, the seafloor, the underside of the world — so it all reads as
    /// "I am under water" rather than "I am in the same fog as a moment ago."
    ///
    /// Vanilla never does this because <c>GameCamera</c> clips the camera
    /// above the surface (assembly_valheim:GameCamera.cs around line 244 —
    /// see m_waterClipping / m_minWaterDistance). That's fine for surface
    /// swimming, but FAT custom water meshes are used for pools, dunk-tanks
    /// and full swimmable lakes where the player legitimately goes under.
    /// For those cases this driver kicks in.
    ///
    /// FLAG MATRIX:
    /// * <see cref="Enabled"/> — master switch. Default false. Independent
    ///   of <see cref="FiresWaterShaderLoader.Enabled"/> on purpose: the fog
    ///   override is useful even without the custom shader, and vice versa.
    /// * Restores the pre-override fog values on first frame ABOVE the
    ///   surface, so disabling at runtime cleanly reverts the scene.
    /// </summary>
    public class UnderwaterFogDriver : MonoBehaviour
    {
        public static bool Enabled = false;

        // Fog tint applied when submerged. Pick a color slightly darker /
        // greener than _UnderwaterTint on the shader so the fog tinted scene
        // doesn't fight the shader's surface tint.
        public static Color SubmergedFogColor = new Color(0.05f, 0.18f, 0.24f, 1f);
        public static float SubmergedFogDensity = 0.07f;
        public static FogMode SubmergedFogMode = FogMode.ExponentialSquared;

        // Saved fog state from before submersion. Captured exactly once per
        // transition (above→below) and restored on the reverse transition.
        // Storing per-driver instead of statically so unloading FAT cleanly
        // releases the captured state.
        private bool m_active;
        private Color m_savedFogColor;
        private float m_savedFogDensity;
        private FogMode m_savedFogMode;
        private bool m_savedFogEnabled;

        private void OnDisable()
        {
            // Component going away while we're still applying the override —
            // restore so the world doesn't get stuck looking tinted.
            if (m_active) RestoreFog();
        }

        private void LateUpdate()
        {
            if (!Enabled)
            {
                if (m_active) RestoreFog();
                return;
            }

            // Need a local player with a valid eye to test submersion against.
            // During menu / world-load we just hold whatever state we were in
            // rather than thrashing the fog every frame.
            var player = Player.m_localPlayer;
            if (player == null) return;

            Vector3 eye;
            try { eye = player.m_eye != null ? player.m_eye.position : player.transform.position + Vector3.up * 1.6f; }
            catch { return; }

            // Floating.GetLiquidLevel(point) returns the wave-deformed water
            // surface Y for any WaterVolume overlapping `point`. Returns a
            // very negative sentinel when no water is present. We treat
            // "no water" as "above water" so the override stays off.
            float liquidY;
            try { liquidY = Floating.GetLiquidLevel(eye); }
            catch { return; }

            bool submerged = liquidY > eye.y + 0.05f && liquidY > -1000f;

            if (submerged && !m_active) ApplyFog();
            else if (!submerged && m_active) RestoreFog();
        }

        private void ApplyFog()
        {
            try
            {
                m_savedFogEnabled = RenderSettings.fog;
                m_savedFogColor   = RenderSettings.fogColor;
                m_savedFogDensity = RenderSettings.fogDensity;
                m_savedFogMode    = RenderSettings.fogMode;

                RenderSettings.fog        = true;
                RenderSettings.fogMode    = SubmergedFogMode;
                RenderSettings.fogColor   = SubmergedFogColor;
                RenderSettings.fogDensity = SubmergedFogDensity;
                m_active = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnderwaterFogDriver] ApplyFog failed: {ex.Message}");
            }
        }

        private void RestoreFog()
        {
            try
            {
                RenderSettings.fog        = m_savedFogEnabled;
                RenderSettings.fogMode    = m_savedFogMode;
                RenderSettings.fogColor   = m_savedFogColor;
                RenderSettings.fogDensity = m_savedFogDensity;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnderwaterFogDriver] RestoreFog failed: {ex.Message}");
            }
            finally
            {
                m_active = false;
            }
        }
    }
}
