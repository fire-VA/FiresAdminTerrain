using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Bumps <see cref="ZoneSystem.m_activeArea"/> while the local player is
    /// over deep open water so the inner ring of animated FiresWater zone
    /// meshes extends far enough that storm-wave crests never reach the
    /// vanilla LOD-plane boundary. Reverts to the captured vanilla value
    /// the moment the player returns to a shallow / near-shore position.
    /// </summary>
    public static class OceanZoneRingExtender
    {
        public static bool  Enabled              = true;
        public static int   ExtendedActiveArea   = 2;
        public static float OceanDepthThreshold  = 10f;

        // Hide-LOD-water toggle. When true, every renderer using a
        // water_lod material has its enabled flag flipped off so the flat
        // vanilla LOD plane stops drawing. Re-scanned periodically because
        // vanilla may re-instance the plane across scene transitions.
        public static bool HideLodWater = false;
        private const float LodScanInterval = 2.0f;
        private static float s_nextLodScan;

        private const int UncapturedSentinel = -1;
        private static int  s_vanillaActiveArea = UncapturedSentinel;
        private static bool s_isExtended;

        public static void TickFromPlayer(Vector3 playerPos)
        {
            if (ZoneSystem.instance == null) return;

            if (s_vanillaActiveArea == UncapturedSentinel)
                s_vanillaActiveArea = ZoneSystem.instance.m_activeArea;

            if (!Enabled)
            {
                if (s_isExtended) ApplyVanilla();
                return;
            }

            bool overDeepWater = IsOverDeepOcean(playerPos);
            if (overDeepWater && !s_isExtended) ApplyExtended();
            else if (!overDeepWater && s_isExtended) ApplyVanilla();
        }

        public static void RestoreVanillaImmediate()
        {
            if (s_vanillaActiveArea == UncapturedSentinel || ZoneSystem.instance == null) return;
            ApplyVanilla();
        }

        private static void ApplyExtended()
        {
            ZoneSystem.instance.m_activeArea = ExtendedActiveArea;
            s_isExtended = true;
        }

        private static void ApplyVanilla()
        {
            ZoneSystem.instance.m_activeArea = s_vanillaActiveArea;
            s_isExtended = false;
        }

        private static bool IsOverDeepOcean(Vector3 worldPos)
        {
            float terrainY;
            if (!Heightmap.GetHeight(worldPos, out terrainY)) return true;
            return ZoneSystem.c_WaterLevel - terrainY > OceanDepthThreshold;
        }

        // Walk every MeshRenderer in scene-loaded objects, find any whose
        // sharedMaterial.name starts with "water_lod", flip enabled to
        // (!HideLodWater). Throttled so we don't pay the
        // FindObjectsOfTypeAll cost every frame. Cheap to call.
        public static void TickLodWaterVisibility()
        {
            if (Time.unscaledTime < s_nextLodScan) return;
            s_nextLodScan = Time.unscaledTime + LodScanInterval;

            try
            {
                var renderers = Resources.FindObjectsOfTypeAll<MeshRenderer>();
                bool shouldShow = !HideLodWater;
                int  changed = 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    if (!r.gameObject.scene.IsValid()) continue;
                    var mat = r.sharedMaterial;
                    if (mat == null || mat.name == null) continue;
                    if (!mat.name.StartsWith("water_lod")) continue;
                    if (r.enabled == shouldShow) continue;
                    r.enabled = shouldShow;
                    changed++;
                }
                if (changed > 0)
                    Debug.Log($"[OceanZoneRing] {(shouldShow ? "Shown" : "Hidden")} {changed} LOD water renderer(s).");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[OceanZoneRing] TickLodWaterVisibility failed: {ex.Message}");
            }
        }

        public static void RestoreLodWaterVisibility()
        {
            bool prev = HideLodWater;
            HideLodWater = false;
            s_nextLodScan = 0f;  // force next call to scan
            try { TickLodWaterVisibility(); } catch { }
            HideLodWater = prev;
        }
    }

    public class OceanZoneRingExtenderDriver : MonoBehaviour
    {
        private const float CheckIntervalSec = 1.0f;

        private float m_nextCheckTime;

        private void Update()
        {
            if (Time.unscaledTime < m_nextCheckTime) return;
            m_nextCheckTime = Time.unscaledTime + CheckIntervalSec;

            var player = Player.m_localPlayer;
            if (player == null) return;

            OceanZoneRingExtender.TickFromPlayer(player.transform.position);
            OceanZoneRingExtender.TickLodWaterVisibility();
        }

        private void OnDisable()
        {
            OceanZoneRingExtender.RestoreVanillaImmediate();
            OceanZoneRingExtender.RestoreLodWaterVisibility();
        }
    }
}
