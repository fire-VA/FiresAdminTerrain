using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // HaloFlat's map (0.2.1037 footprint circle; 0.2.1038 Fire 10-24: "the map should basically just show a flat strip by
    // default, and then we build a 3d ring view map later"). On a ring-only world the map is the ring UNROLLED: the angle
    // around the axis runs along map x (the whole 30 km circumference across the map's width) and the position along the axis
    // runs along map y at the SAME metres-per-pixel, so the 3 km band is a horizontal strip. Vanilla's three world<->map
    // conversions are remapped through that unrolling, so the player marker, pins, clicks (pins, map teleport), centring and
    // fog-of-war exploring all follow the strip:
    //   WorldToMapPoint / WorldToPixel: world -> (angle, axis) -> strip coords -> vanilla's pixel maths;
    //   MapPointToWorld: strip coords -> the point on the ring's inside face at that angle and axis position.
    // Textures: after vanilla builds or loads them (MapReady), empty dark space, no sea, and the strip in the Meadows colour
    // (the rim lips at both edges a shade darker). The 3D ring view is a later step.
    internal static class FatHaloMap
    {
        private const float LandHeight = 40f, RimShare = 0.03f;
        private static readonly Color32 Space = new Color32(8, 9, 14, 255);
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> MapTex = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_mapTexture");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> MaskTex = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> HeightTex = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");

        // Half the map's world extent: vanilla draws +-HalfExtent metres across the texture.
        private static float HalfExtent(Minimap map) => map.m_textureSize * 0.5f * map.m_pixelSize;

        // World point -> strip "world" coordinates (x = arc position scaled onto the map width, z = axis position, same scale).
        private static Vector3 Unroll(Minimap map, Vector3 p)
        {
            FatHaloArc.MapStrip(out Vector3 o, out float r, out _);
            float e = HalfExtent(map);
            float k = e / (Mathf.PI * r);   // metres of map per metre of ring
            float ang = Mathf.Atan2(p.z - o.z, p.x - o.x);
            return new Vector3(ang / Mathf.PI * e, 0f, (p.y - o.y) * k);
        }

        private static Vector3 Roll(Minimap map, float sx, float sz)
        {
            FatHaloArc.MapStrip(out Vector3 o, out float r, out _);
            float e = HalfExtent(map);
            float k = e / (Mathf.PI * r);
            float ang = sx / e * Mathf.PI;
            return o + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (r - 1.5f) + Vector3.up * (sz / k);
        }

        [HarmonyPatch(typeof(Minimap), "WorldToMapPoint")]
        private static class StripWorldToMapPoint
        {
            private static void Prefix(Minimap __instance, ref Vector3 p) { if (FatHaloArc.RingOnly) p = Unroll(__instance, p); }
        }

        [HarmonyPatch(typeof(Minimap), "WorldToPixel")]
        private static class StripWorldToPixel
        {
            private static void Prefix(Minimap __instance, ref Vector3 p) { if (FatHaloArc.RingOnly) p = Unroll(__instance, p); }
        }

        [HarmonyPatch(typeof(Minimap), "MapPointToWorld")]
        private static class StripMapPointToWorld
        {
            private static void Postfix(Minimap __instance, ref Vector3 __result)
            {
                if (FatHaloArc.RingOnly) __result = Roll(__instance, __result.x, __result.z);
            }
        }

        // Halo ring map arrow ([classes-ui] for [worldgen], lead 10-24): vanilla turns the player marker by the camera's WORLD yaw
        // (Euler(0,0,-eulerAngles.y)), which on the ring wall spins / freezes. On the strip, map x = the angle round the axis (the
        // tangent) and map up = the axis, at the same metres-per-pixel, so the heading is the camera forward split into those two.
        private static bool s_arrowLogged;

        [HarmonyPatch(typeof(Minimap), "UpdatePlayerMarker")]
        private static class StripPlayerArrow
        {
            private static void Postfix(Minimap __instance, Player player, Quaternion playerRot)
            {
                if (!FatHaloArc.RingOnly || player == null || __instance.m_smallMarker == null) return;
                FatHaloArc.MapStrip(out Vector3 o, out _, out _);
                Vector3 p = player.transform.position;
                float ang = Mathf.Atan2(p.z - o.z, p.x - o.x);
                Vector3 tangent = new Vector3(-Mathf.Sin(ang), 0f, Mathf.Cos(ang));   // +map x (the way Unroll's angle grows)
                Vector3 f = playerRot * Vector3.forward;
                float mx = Vector3.Dot(f, tangent), my = Vector3.Dot(f, Vector3.up);   // +map up = the axis (world +Y, as Unroll)
                if (mx * mx + my * my < 1e-6f) return;   // looking straight at the floor or the axis: keep the last heading
                float heading = Mathf.Atan2(mx, my) * Mathf.Rad2Deg;   // 0 = map up, 90 = map right (vanilla's yaw convention)
                Quaternion rot = Quaternion.Euler(0f, 0f, -heading);
                __instance.m_smallMarker.rotation = rot;
                if (__instance.m_largeMarker != null) __instance.m_largeMarker.rotation = rot;
                if (!s_arrowLogged)
                {
                    s_arrowLogged = true;
                    FiresLogger.LogDebug($"[FatHalo] HaloFlat map arrow: heading on the strip {heading:F0} deg (along the ring {mx:F2}, along the axis {my:F2}) at angle {ang * Mathf.Rad2Deg:F0}");
                }
            }
        }

        internal static void MapReady()
        {
            var map = Minimap.instance;
            if (map == null || !FatHaloArc.RingOnly) return;
            try
            {
                var mapTex = MapTex(map); var maskTex = MaskTex(map); var heightTex = HeightTex(map);
                if (mapTex == null || maskTex == null || heightTex == null) return;
                FatHaloArc.MapStrip(out _, out float r, out float w);
                int size = map.m_textureSize;
                float px = map.m_pixelSize, half = size / 2, halfPx = px / 2f;
                float k = HalfExtent(map) / (Mathf.PI * r);
                float stripHalf = Mathf.Max(w * k, px * 1.5f), rimFrom = stripHalf * (1f - RimShare * 2f);
                Color strip = map.m_meadowsColor;
                Color32 band = strip, rim = (Color)(strip * 0.7f);
                var colors = new Color32[size * size];
                var mask = new Color32[size * size];
                var heights = new Color[size * size];
                int rows = 0;
                for (int i = 0; i < size; i++)
                {
                    float sz = Mathf.Abs((i - half) * px + halfPx);
                    bool onStrip = sz <= stripHalf;
                    if (onStrip) rows++;
                    Color32 c = !onStrip ? Space : sz >= rimFrom ? rim : band;
                    for (int j = 0; j < size; j++)
                    {
                        int idx = i * size + j;
                        colors[idx] = c;
                        mask[idx] = new Color32(0, 0, 0, 0);
                        heights[idx] = new Color(LandHeight, 0f, 0f, 1f);
                    }
                }
                mapTex.SetPixels32(colors); mapTex.Apply();
                maskTex.SetPixels32(mask); maskTex.Apply();
                heightTex.SetPixels(heights); heightTex.Apply();
                FiresLogger.LogInfo($"[FatHalo] HaloFlat map: the ring unrolled into a flat strip - {2f * Mathf.PI * r / 1000f:F1} km around across the map, "
                    + $"{2f * w / 1000f:F1} km band = {rows} of {size} rows ({px * size / (2f * Mathf.PI * r):F3} map m per ring m); marker, pins and clicks follow the strip");
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FatHalo] HaloFlat map repaint threw: {ex.Message}"); }
        }
    }
}
