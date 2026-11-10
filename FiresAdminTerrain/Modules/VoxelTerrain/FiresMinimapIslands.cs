using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Skylands on the world map ──────────────────────────
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

        // 0.2.1104 (overnight #6, RAM): the map's own pixels are no longer kept whole (Color32[size²], ~16.8 MB at 2048²).
        // Only the pixels a stamp overwrote are kept (index + the map's own colour), and a restamp first puts them back,
        // so the texture before stamping is exactly the old s_base and the stamped result is identical.
        private static int[] s_savedIdx = new int[0];
        private static Color32[] s_savedCol = new Color32[0];
        private static bool s_ready;              // the texture holds the map's pixels (plus our saved stamps)
        internal static long ResidentBytes => s_savedIdx.Length * 4L + s_savedCol.Length * 4L;
        internal static long DenseBytes => Minimap.instance != null && s_ready ? (long)s_texSize(Minimap.instance) * s_texSize(Minimap.instance) * 4L : 0;
        private static string s_stampedFor;       // the island set the map shows
        private static bool s_stampedActive;      // whether the islands were on when it was stamped
        private const float PaintStampSeconds = 2f;
        private static int s_stampedPaint = -1;
        private static float s_paintStampAt;
        private static bool IslandsShown => Zones.ZoneModeRegistry.ModeActive && FiresSkyIslands.Enabled;

        internal static void Reset() { s_ready = false; s_savedIdx = new int[0]; s_savedCol = new Color32[0]; s_stampedFor = null; }

        // The large map's hover label names the biome under the cursor by XZ (GetBiomeSector of a
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
            // fresh pixels: nothing of ours is in the texture
            s_ready = false; s_savedIdx = new int[0]; s_savedCol = new Color32[0];
            Stamp();
        }

        // Per frame on a client: the island set changed since the stamp (a skyland placed, moved or removed; the registry synced).
        internal static void Tick()
        {
            // Also when the islands turn on: a client holds them off until the server confirms its set, so the stamp at
            // map load drew none, and the hash alone could stay the same.
            if (s_ready && (s_stampedFor != FiresSkyIslands.ActiveSetHash || s_stampedActive != IslandsShown)) Stamp();
            // 0.2.1375 (Fire: the world uses the override "without just our paint method"): the map's colours were made once from GetBiome and kept, so paint never reached them.
            else if (s_ready && s_stampedPaint != Zones.BiomePaintVersion.Current && Time.unscaledTime >= s_paintStampAt)
            {
                s_paintStampAt = Time.unscaledTime + PaintStampSeconds;   // a stroke is many changes: one rewrite of the map at most this often
                Stamp();
            }
        }

        private static void Stamp()
        {
            var mm = Minimap.instance;
            if (mm == null || mm.m_mapTexture == null || s_pixelColor == null) return;
            int size = s_texSize(mm);
            float pix = s_pixSize(mm);
            if (size <= 0 || pix <= 0f) return;
            var pixels = mm.m_mapTexture.GetPixels32();
            if (pixels.Length != size * size) { s_savedIdx = new int[0]; s_savedCol = new Color32[0]; }
            // put back what the last stamp overwrote: `pixels` is now exactly the map's own pixels (the old s_base)
            for (int k = 0; k < s_savedIdx.Length; k++) pixels[s_savedIdx[k]] = s_savedCol[k];
            var before = (Color32[])pixels.Clone();   // transient, freed after this call
            int stamped = FiresSkyIslands.StampSkylands(pixels, size, pix,
                biome => (Color32)(Color)s_pixelColor.Invoke(mm, new object[] { biome }));
            s_stampedPaint = Zones.BiomePaintVersion.Current;
            int painted = Zones.BiomePaint.StampMap(pixels, size, pix, biome => (Color32)(Color)s_pixelColor.Invoke(mm, new object[] { biome }));
            var idx = new System.Collections.Generic.List<int>();
            var col = new System.Collections.Generic.List<Color32>();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 a = pixels[i], b = before[i];
                if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) { idx.Add(i); col.Add(b); }
            }
            s_savedIdx = idx.ToArray(); s_savedCol = col.ToArray();
            s_ready = true;
            mm.m_mapTexture.SetPixels32(pixels);
            mm.m_mapTexture.Apply();
            s_stampedFor = FiresSkyIslands.ActiveSetHash;
            s_stampedActive = IslandsShown;
            FiresLogger.LogDebug($"[FiresMinimapIslands] {painted} map pixel(s) wear custom biome paint (0.2.1375).");
            FiresLogger.LogDebug($"[FiresMinimapIslands] {stamped} map pixel(s) wear their skyland's biome (island set {s_stampedFor}).");
        }
    }
}
