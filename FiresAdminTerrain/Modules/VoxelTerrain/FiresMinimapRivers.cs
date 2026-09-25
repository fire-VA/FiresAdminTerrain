using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Generated water on the world map ─────────────────────────────────
    // Vanilla's map shader draws water wherever the map HEIGHT texture dips
    // below sea level (30) — vanilla rivers are carved into worldgen height,
    // so they show up for free. Our rivers are carved at VOXEL level and
    // never touch the map heights, so they render as shadowed valleys.
    // Stamp the reconciled water field into the height texture (bed = 30 −
    // real depth, so the shader's depth shading matches the water) and clear
    // the forest mask there, exactly what GetMaskColor does for water.
    // Stamped on BOTH map paths (fresh generate + PNG cache load), every
    // session — idempotent, ~0.3s once, and self-updates when water configs
    // change the field. Client-only in effect: the dedi never instantiates
    // Minimap.
    internal static class FiresMinimapRivers
    {
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> s_heightTex =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> s_maskTex =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");
        private static readonly AccessTools.FieldRef<Minimap, int> s_texSize =
            AccessTools.FieldRefAccess<Minimap, int>("m_textureSize");
        private static readonly AccessTools.FieldRef<Minimap, float> s_pixSize =
            AccessTools.FieldRefAccess<Minimap, float>("m_pixelSize");

        // Textures populated this session (either map path ran).
        private static bool s_mapReady;
        private static string s_lastSkip;

        internal static void Reset() { s_mapReady = false; s_lastSkip = null; }

        internal static void MapReady(string source)
        {
            // One line per event — the 0.2.260 session had NO stamp log and
            // NO vanilla minimap log either; this says which side went silent.
            FiresLogger.LogInfo($"[FiresMinimapRivers] map event: {source}.");
            s_mapReady = true;
            Stamp();
        }

        private static void Skip(string why)
        {
            if (s_lastSkip == why) return;
            s_lastSkip = why;
            FiresLogger.LogInfo($"[FiresMinimapRivers] stamp waiting: {why}.");
        }

        // Also called when the reconciled field finishes building, in case
        // the map generated first — whichever side is ready last stamps.
        internal static void OnWaterFieldReady() { Stamp(); }

        private static void Stamp()
        {
            // No map yet (never, on a dedi) or no Fires worldgen on this world:
            // nothing to wait for here. MapReady and OnWaterFieldReady re-run
            // the stamp if either changes.
            var mm = Minimap.instance;
            if (mm == null || !s_mapReady || !FiresWorldGenBase.Active) return;
            if (!FiresWorldGenBase.WaterSurfaceReady) { Skip("water surface not built yet"); return; }
            var heightTex = s_heightTex(mm);
            var maskTex = s_maskTex(mm);
            if (heightTex == null || maskTex == null) { Skip("map textures missing"); return; }
            int size = s_texSize(mm);
            float pix = s_pixSize(mm);
            if (size <= 0 || pix <= 0f) { Skip("bad texture dimensions"); return; }
            s_lastSkip = null;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Color[] h = heightTex.GetPixels();
            Color32[] m = maskTex.GetPixels32();
            int half = size / 2;
            float hp = pix / 2f;
            long stamped = 0;
            System.Threading.Tasks.Parallel.For(0, size, z =>
            {
                int row = z * size;
                float wz = (z - half) * pix + hp;
                long local = 0;
                for (int x = 0; x < size; x++)
                {
                    float wx = (x - half) * pix + hp;
                    // SUPERSAMPLE 3x3: one probe per ~10m texel MISSED every
                    // channel narrower than a texel — rivers only showed
                    // where they were swamp-sheet wide ("other rivers aren't
                    // shown on the map"). Any wet subsample stamps the texel
                    // with its deepest hit.
                    float dep = 0f;
                    for (int sz2 = -1; sz2 <= 1; sz2++)
                        for (int sx2 = -1; sx2 <= 1; sx2++)
                        {
                            if (FiresWorldGenBase.TryGetWaterSurfaceSmooth(
                                    wx + sx2 * pix * 0.33f, wz + sz2 * pix * 0.33f,
                                    out float lvl2, out float dep2)
                                && lvl2 > 30.3f && dep2 > dep) dep = dep2;
                        }
                    if (dep < 0.15f) continue;
                    // Below map sea level by the REAL column depth so the
                    // shader's depth gradient reads true; floor 0.4 keeps
                    // hairline shallows visibly blue.
                    float mapH = 30f - Mathf.Clamp(dep, 0.4f, 12f);
                    int i = row + x;
                    if (h[i].r > mapH)
                    {
                        h[i].r = mapH;
                        m[i] = new Color32(0, 0, 0, 0);
                        local++;
                    }
                }
                if (local > 0) System.Threading.Interlocked.Add(ref stamped, local);
            });
            if (stamped > 0)
            {
                heightTex.SetPixels(h);
                heightTex.Apply();
                maskTex.SetPixels32(m);
                maskTex.Apply();
            }
            FiresLogger.LogInfo($"[FiresMinimapRivers] stamped {stamped} water pixels onto the world map " +
                      $"({size}x{size} @ {pix:0.##}m/px) in {sw.ElapsedMilliseconds}ms.");
        }
    }
}
