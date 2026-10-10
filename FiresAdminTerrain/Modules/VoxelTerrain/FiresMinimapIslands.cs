using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Skylands on the world map (FAT 0.2.758) ──────────────────────────
    // Vanilla draws the map's biome colours from WorldGenerator.GetBiome, so
    // a skyland showed the biome of the ground under it. Each skyland's disc
    // is stamped in its own biome's map colour (Minimap.GetPixelColor) onto
    // the map texture, the same on the minimap and the large map. Stamped
    // over a copy of the generated pixels, so a moved or removed skyland
    // never leaves its old disc, and never saved into the map cache (vanilla
    // saves before the stamp). Restamped when the island set changes.
    // Client-only in effect: the dedi never instantiates Minimap.
    internal static class FiresMinimapIslands
    {
        private static readonly AccessTools.FieldRef<Minimap, int> s_texSize =
            AccessTools.FieldRefAccess<Minimap, int>("m_textureSize");
        private static readonly AccessTools.FieldRef<Minimap, float> s_pixSize =
            AccessTools.FieldRefAccess<Minimap, float>("m_pixelSize");
        private static readonly MethodInfo s_pixelColor = AccessTools.Method(typeof(Minimap), "GetPixelColor", new[] { typeof(Heightmap.Biome) });

        private static Color32[] s_base;          // the map's own pixels, before any skyland
        private static string s_stampedFor;       // the island set the map shows
        private static bool s_stampedActive;      // whether the islands were on when it was stamped
        private static bool IslandsShown => Zones.ZoneModeRegistry.ModeActive && FiresSkyIslands.Enabled;

        internal static void Reset() { s_base = null; s_stampedFor = null; }

        // 0.2.774 ([lead] / Fire 10-06): the large map's hover label names the biome under the cursor by XZ (GetBiomeSector of a
        // map point with no height), so over a skyland it named the ground or sea below. Inside a skyland's shape it names the
        // island's biome, matching the stamp drawn there. Vanilla left the label empty for an unexplored point; so does this.
        private static readonly MethodInfo s_screenToWorld = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) });

        [HarmonyPatch(typeof(Minimap), "UpdateBiome")]
        private static class Minimap_UpdateBiome
        {
            private static void Postfix(Minimap __instance)
            {
                if (__instance.m_mode != Minimap.MapMode.Large || s_screenToWorld == null || __instance.m_biomeNameLarge == null) return;
                if (string.IsNullOrEmpty(__instance.m_biomeNameLarge.text)) return;
                var screen = ZInput.IsMouseActive() || ZInput.IsTouchPressedDown()
                    ? ZInput.pointerPosition : new Vector3(Screen.width / 2f, Screen.height / 2f);
                var point = (Vector3)s_screenToWorld.Invoke(__instance, new object[] { screen });
                if (!FiresSkyIslands.TrySkylandBiomeOver(point.x, point.z, out var biome)) return;
                __instance.m_biomeNameLarge.text = Localization.instance.Localize("$biome_" + biome.ToString().ToLower());
            }
        }

        // Both map paths (fresh generate, cache load): the texture holds fresh pixels.
        internal static void MapReady()
        {
            s_base = null;
            Stamp();
        }

        // Per frame on a client: the island set changed since the stamp (a skyland placed, moved or removed; the registry synced).
        internal static void Tick()
        {
            // 0.2.772: also when the islands turn on: a client holds them off until the server confirms its set, so the stamp at
            // map load drew none, and the hash alone could stay the same.
            if (s_base != null && (s_stampedFor != FiresSkyIslands.ActiveSetHash || s_stampedActive != IslandsShown)) Stamp();
        }

        private static void Stamp()
        {
            var mm = Minimap.instance;
            if (mm == null || mm.m_mapTexture == null || s_pixelColor == null) return;
            int size = s_texSize(mm);
            float pix = s_pixSize(mm);
            if (size <= 0 || pix <= 0f) return;
            if (s_base == null || s_base.Length != size * size) s_base = mm.m_mapTexture.GetPixels32();
            var pixels = (Color32[])s_base.Clone();
            int stamped = FiresSkyIslands.StampSkylands(pixels, size, pix,
                biome => (Color32)(Color)s_pixelColor.Invoke(mm, new object[] { biome }));
            mm.m_mapTexture.SetPixels32(pixels);
            mm.m_mapTexture.Apply();
            s_stampedFor = FiresSkyIslands.ActiveSetHash;
            s_stampedActive = IslandsShown;
            FiresLogger.LogDebug($"[FiresMinimapIslands] {stamped} map pixel(s) wear their skyland's biome (island set {s_stampedFor}).");
        }
    }
}
