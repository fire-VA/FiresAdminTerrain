using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Utilities
{
    // ONE-TIME static capture of a fully-finalized vanilla Heightmap material.
    //
    // OLD BEHAVIOR (deleted): per-tile retry, scene search, FindObjectsByType
    // every 2 seconds, fallback chains, throttled warnings. Tiles cloned
    // sharedMaterial — which on Custom/Heightmap shader is missing the
    // per-instance state (cornerBiomes, splat texture array bindings) that
    // Heightmap.RebuildRenderMesh writes onto m_material. Result: clone
    // rendered as invisible / flat-color top mesh.
    //
    // NEW BEHAVIOR (Phase F+):
    //   * Harmony postfix on Heightmap.RebuildRenderMesh fires the FIRST
    //     time any vanilla heightmap finishes building its render mesh.
    //     At that moment m_material has all per-instance properties set.
    //   * We reflect m_material out, clone it, set sensible Meadows-x-4
    //     cornerBiomes defaults, bind a blank "no paint" mask texture,
    //     stash the result in a static field.
    //   * Every TileMeshRenderer reads from that static field. ONE material
    //     instance shared across all tiles for the rest of the session.
    //   * Per-tile biome painting is a future per-tile material variant
    //     (Task #73). For now: tiles render Meadows green by default;
    //     biome paint changes vertex colors but blends within Meadows.
    //
    // FALLBACK: if no vanilla heightmap ever renders (admin build yard far
    // from any vanilla terrain on first session?), GetCurrentMaterial returns
    // a Standard-shader fallback so tiles aren't pink/invisible. Once the
    // player approaches vanilla terrain, the Heightmap postfix fires and
    // the cache upgrades for the rest of the session.
    public static class TerrainMaterialCache
    {
        private static Material s_cachedMaterial;
        private static Material s_fallbackMaterial;
        private static Texture2D s_paintMaskNothing;

        private static readonly FieldInfo s_heightmapMaterialField =
            typeof(Heightmap).GetField("m_material",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly int s_ClearedMaskTexProp = Shader.PropertyToID("_ClearedMaskTex");
        private static readonly int s_cornerBiomes3Prop = Shader.PropertyToID("_CornerBiomes3");
        private static readonly int s_cornerBiomes4Prop = Shader.PropertyToID("_CornerBiomes4");

        public static bool HasMaterial => s_cachedMaterial != null;

        // Public read API used by TileMeshRenderer. Returns the cached
        // Custom/Heightmap material when available, otherwise a Standard
        // fallback that's at least visible.
        public static Material GetCurrentMaterial()
        {
            if (s_cachedMaterial != null) return s_cachedMaterial;
            if (s_fallbackMaterial == null) BuildFallback();
            return s_fallbackMaterial;
        }

        // ── Capture path ─────────────────────────────────────────────────

        // Called by Heightmap.RebuildRenderMesh postfix. Once we have a
        // cached material, this is a no-op.
        private static void TryCaptureFrom(Heightmap hm)
        {
            if (s_cachedMaterial != null) return;
            if (hm == null) return;

            Material source = null;

            // Prefer the per-instance m_material — it has the per-Heightmap
            // SetTexture state baked in (splat texture array, cornerBiomes
            // shader uniforms, etc). If reflection fails for some reason,
            // fall back to sharedMaterial — better than nothing.
            try
            {
                if (s_heightmapMaterialField != null)
                {
                    source = s_heightmapMaterialField.GetValue(hm) as Material;
                }
            }
            catch { /* fall through */ }

            if (source == null)
            {
                var rend = hm.GetComponent<MeshRenderer>();
                source = rend != null ? rend.sharedMaterial : null;
            }

            if (source == null) return;
            if (source.shader == null) return;

            // 2026-05-25: relaxed shader-name gate to a PROPERTY check so we
            // play nicely with replacement shaders shipped by other mods
            // (e.g. ValheimHDTerrain swaps Custom/Heightmap for its own
            // "HD Terrain" shader at startup). Any primary heightmap shader
            // has `_ClearedMaskTex` (used for the per-tile paint mask);
            // LOD-distance variants do not. So:
            //   - Vanilla Custom/Heightmap                → accepted
            //   - HD Terrain replacement shader           → accepted
            //   - Future modded heightmap shaders         → accepted
            //   - LOD distance / overlay-only shaders     → rejected
            //
            // Without this, HD Terrain users saw invisible custom-tile tops
            // (capture rejected → tiles fell through to Standard fallback,
            // which doesn't have the right vertex layout / lighting setup).
            // The cave-mesh underside kept rendering because it uses its
            // own material, not the cached heightmap one.
            bool isHeightmapShader = source.shader.name == "Custom/Heightmap"
                                  || source.HasProperty(s_ClearedMaskTexProp);
            if (!isHeightmapShader)
            {
                return;
            }

            // Clone — we're going to mutate cornerBiomes + paint mask, do
            // NOT touch the source material that the vanilla terrain uses.
            var mat = new Material(source) { name = "FiresAdminTerrain_TileMaterial" };

            // ── Override per-tile properties ─────────────────────────────
            // Bind a 1x1 "nothing" paint mask. SetTexture(null) gives
            // platform-defined garbage; explicit (0,0,0,1) sample is
            // unambiguous "no paint".
            if (s_paintMaskNothing == null) s_paintMaskNothing = BuildPaintMaskNothing();
            if (mat.HasProperty(s_ClearedMaskTexProp))
            {
                mat.SetTexture(s_ClearedMaskTexProp, s_paintMaskNothing);
            }
            if (mat.HasProperty("_ClearedMaskTex_ST"))
            {
                mat.SetTextureScale("_ClearedMaskTex", Vector2.one);
                mat.SetTextureOffset("_ClearedMaskTex", Vector2.zero);
            }
            if (mat.HasProperty("_MainTex"))
            {
                mat.SetTextureScale("_MainTex", Vector2.one);
                mat.SetTextureOffset("_MainTex", Vector2.zero);
            }

            // Force cornerBiomes default to Meadows-x-4. We INHERIT whatever
            // the source heightmap had set, which would tie our tile's look
            // to wherever the player happened to be when capture fired.
            // Meadows-x-4 produces uniform Meadows green tiles regardless of
            // capture origin — predictable and matches user intent.
            // Per-tile biome paint future-work via Task #73 (per-tile
            // material instances) will let tiles override these.
            ApplyMeadowsDefault(mat);

            Object.DontDestroyOnLoad(mat);
            s_cachedMaterial = mat;
            Debug.Log($"[TerrainMaterialCache] Captured heightmap material from {hm.name} | shader='{source.shader.name}' (per-instance m_material clone). Meadows defaults applied. All tile renderers will now share this material.");

            // Upgrade every existing TileMeshRenderer in one pass.
            VerdantsAscent.Terrain.Persistence.TileMeshRenderer.UpgradeAllRenderersToCachedMaterial();
        }

        /// <summary>
        /// Drops the cached material and re-captures from the scene. Use when
        /// another mod (e.g. ValheimHDTerrain) finishes swapping shaders or
        /// texture arrays AFTER our initial capture and we need to pick up
        /// the new visual. Triggered by the `va_terrain_recapture` console
        /// command.
        /// </summary>
        public static int ForceRecaptureFromScene()
        {
            s_cachedMaterial = null;
            return ForceCaptureFromScene();
        }

        // Set the shader's per-instance "corner biome" uniforms to Meadows
        // on all 4 corners. Vanilla writes these as floats (biome enum
        // value cast). We try both common property names since the shader
        // has evolved across Valheim patches.
        private static void ApplyMeadowsDefault(Material mat)
        {
            float meadows = (float)(int)Heightmap.Biome.Meadows;

            // The Custom/Heightmap shader uses 4 corner-biome uniforms.
            // Older patches called them _ColorMap[0..3]; current patches
            // use a 4-value vector or per-corner floats. Set all variants
            // we know about — SetXXX on a property the shader doesn't
            // declare is a silent no-op.
            for (int i = 0; i < 4; i++)
            {
                string name = $"_Biome{i}";
                if (mat.HasProperty(name)) mat.SetFloat(name, meadows);
            }
            if (mat.HasProperty("_BiomeMeadows")) mat.SetFloat("_BiomeMeadows", 1f);

            // Older shader naming.
            for (int i = 1; i <= 4; i++)
            {
                string name = $"_CornerBiomes{i}";
                if (mat.HasProperty(name)) mat.SetFloat(name, meadows);
            }
        }

        // Tiny 1x1 RGBA32 texture filled with Heightmap.m_paintMaskNothing
        // (0,0,0,1). Bound to _ClearedMaskTex so the shader samples
        // "no paint" unambiguously regardless of platform null-sampler
        // defaults.
        private static Texture2D BuildPaintMaskNothing()
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
            {
                name = "FiresAdminTerrain_PaintMaskNothing",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixel(0, 0, Heightmap.m_paintMaskNothing);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return tex;
        }

        private static void BuildFallback()
        {
            var stdShader = Shader.Find("Standard");
            if (stdShader != null)
            {
                s_fallbackMaterial = new Material(stdShader)
                {
                    name = "FiresAdminTerrain_TileFallback_Standard",
                    color = new Color(0.35f, 0.55f, 0.25f, 1f),
                    hideFlags = HideFlags.HideAndDontSave,
                };
                return;
            }
            // Last resort — Sprites/Default ships with every Unity build.
            var spriteShader = Shader.Find("Sprites/Default");
            if (spriteShader != null)
            {
                s_fallbackMaterial = new Material(spriteShader)
                {
                    name = "FiresAdminTerrain_TileFallback_Sprite",
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
        }

        // ── Eager-capture entry points ───────────────────────────────────
        //
        // Multiple capture sites so the cache populates as early as
        // possible. ALL of them short-circuit once s_cachedMaterial is set.
        //
        // Priority order (first one that produces a Custom/Heightmap
        // material wins; capture is sticky for the session):
        //
        // 1. Heightmap.Awake postfix — fires for EVERY Heightmap that
        //    spawns (vanilla zones). On dedi server, m_material may be
        //    populated late so capture often returns null here, but the
        //    no-op fast-path is cheap.
        //
        // 2. Heightmap.RebuildRenderMesh postfix — fires after vanilla
        //    finalizes m_material. This is the MOST RELIABLE moment.
        //    Always produces a working material.
        //
        // 3. ZNetScene.Awake postfix — fires once at world spawn. Scans
        //    every Heightmap already loaded in the scene and tries to
        //    capture. Mostly redundant with #1/#2 but catches the edge
        //    case where ZNetScene initializes before Heightmaps render.
        //
        // 4. va_terrain_capture console command — manual trigger. Useful
        //    if the player is in an admin yard far from any vanilla
        //    terrain and capture hasn't fired yet. Forces a scan of all
        //    Heightmaps in scene.

        [HarmonyPatch(typeof(Heightmap), "Awake")]
        private static class Heightmap_Awake_Capture
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Heightmap __instance)
            {
                if (s_cachedMaterial != null) return;
                TryCaptureFrom(__instance);
            }
        }

        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        private static class Heightmap_RebuildRenderMesh_Capture
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Heightmap __instance)
            {
                if (s_cachedMaterial != null) return;
                TryCaptureFrom(__instance);
            }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static class ZNetScene_Awake_ScanHeightmaps
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix()
            {
                if (s_cachedMaterial != null) return;
                // One-shot scene scan. If a heightmap exists already, capture.
                var heightmaps = Object.FindObjectsByType<Heightmap>(FindObjectsSortMode.None);
                foreach (var hm in heightmaps)
                {
                    if (hm == null) continue;
                    TryCaptureFrom(hm);
                    if (s_cachedMaterial != null) return;
                }
            }
        }

        // Manual force-capture entry. Called by the va_terrain_capture
        // console command for emergency re-scans (e.g. player teleported
        // to admin yard before any vanilla terrain loaded).
        public static int ForceCaptureFromScene()
        {
            if (s_cachedMaterial != null) return 0;
            int scanned = 0;
            var heightmaps = Object.FindObjectsByType<Heightmap>(FindObjectsSortMode.None);
            foreach (var hm in heightmaps)
            {
                if (hm == null) continue;
                scanned++;
                TryCaptureFrom(hm);
                if (s_cachedMaterial != null) break;
            }
            return scanned;
        }
    }
}
