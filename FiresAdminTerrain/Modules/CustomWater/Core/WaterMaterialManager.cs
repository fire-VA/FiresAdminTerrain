using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Manages water materials by capturing Valheim's water shaders at runtime.
    /// 
    /// SUPPORTED MATERIALS:
    /// - Water: Standard blue water from WaterVolume
    /// - Tar: Dark sticky tar from LiquidVolume/tar pits
    /// - Ice: Frozen surface from mountain biome prefabs
    /// - Lava: Glowing magma from Ashlands prefabs
    /// - BoilingWater: Tinted water with orange hue
    /// - FreezingWater: Tinted water with deep blue hue
    /// </summary>
    public static class WaterMaterialManager
    {
        private static Material s_waterMaterial;
        private static Material s_tarMaterial;
        private static Material s_iceMaterial;
        private static Material s_lavaMaterial;
        private static Material s_boilingMaterial;
        private static Material s_freezingMaterial;
        private static Material s_snowMaterial;
        // Cached custom-shaded wrap of s_waterMaterial. Built lazily on first
        // GetMaterial(Water) call when the shader mod has registered a wrapper
        // with FUC, so without the shader mod we never build it and pay zero
        // cost. See FiresCore.Services.WaterShaderBridge — the shader mod's
        // wrapper re-sets every property on a fresh material.
        private static Material s_waterMaterialWrapped;
        private static bool s_initialized = false;
        private static bool s_captureAttempted = false;

        // Per-material capture-success flags. Differentiate "we got the
        // real vanilla material" vs "we created a fallback because
        // capture missed." If a slot is still using a fallback, the next
        // Initialize() call retries capture for that slot — important
        // because the first call may happen during prefab-prewarm
        // before WaterVolume.Instances has populated, and a later call
        // (after a water zone streams in) can succeed.
        private static bool s_waterCaptured = false;
        private static bool s_tarCaptured = false;
        private static bool s_iceCaptured = false;
        private static bool s_lavaCaptured = false;

        // Prefab names to search for tar material
        private static readonly string[] TAR_PREFAB_NAMES = {
            "TarLiquid",
            "Tar_blob_aoe",
            "TarGeyser",
            "Pickable_Tar",
            "piece_dvergr_spiralstair_tar",
            "tar_",
            "lava_" // Ashlands tar/lava might share similar shader
        };

        // Prefab names to search for water material. Vanilla Valheim does
        // NOT have a prefab literally named "water_ocean" — the candidates
        // below are the actual scene prefab names that carry a WaterVolume
        // with m_waterSurface populated. Tried in order.
        private static readonly string[] WATER_PREFAB_NAMES = {
            "Water",
            "WaterSurface",
            "WaterEdge",
            "water_d_v2",
            "water",
        };

        /// <summary>
        /// Gets the appropriate material for a vanilla liquid type.
        /// </summary>
        public static Material GetWaterMaterial(LiquidType liquidType)
        {
            Initialize();

            return liquidType == LiquidType.Tar ? s_tarMaterial : GetWrappedWaterOrRaw();
        }

        /// <summary>
        /// Round-65 — the RAW captured vanilla Custom/Water material,
        /// explicitly bypassing the FiresTossinShade WaterShaderBridge wrap.
        /// Used by the `va_water_mat_debug 1` A/B switch: if raw vanilla
        /// renders on a combiner mesh while the wrapped material doesn't,
        /// the wrap is what breaks combiner rendering.
        /// </summary>
        public static Material GetRawWaterMaterial()
        {
            Initialize();
            return s_waterMaterial;
        }

        /// <summary>
        /// Gets the appropriate material for a custom liquid type.
        /// This is the preferred method for the expanded liquid system.
        /// </summary>
        public static Material GetMaterial(CustomLiquidType liquidType)
        {
            Initialize();

            switch (liquidType)
            {
                case CustomLiquidType.Tar:
                    return s_tarMaterial ?? GetWrappedWaterOrRaw();
                case CustomLiquidType.Ice:
                    return s_iceMaterial ?? CreateIceFallback();
                case CustomLiquidType.Lava:
                    return s_lavaMaterial ?? CreateLavaFallback();
                case CustomLiquidType.BoilingWater:
                    return s_boilingMaterial ?? CreateBoilingFallback();
                case CustomLiquidType.FreezingWater:
                    return s_freezingMaterial ?? CreateFreezingFallback();
                case CustomLiquidType.Snow:
                    return s_snowMaterial ?? CreateSnowFallback();
                case CustomLiquidType.Water:
                default:
                    return GetWrappedWaterOrRaw();
            }
        }

        /// <summary>
        /// Returns the custom-shader-wrapped water material if the shader mod
        /// is present and the wrap succeeds, otherwise the raw captured
        /// vanilla material. Result is cached — the wrap is built once per
        /// successful capture. Invalidated when <see cref="RefreshMaterials"/>
        /// clears state.
        ///
        /// Kept separate from <see cref="s_waterMaterial"/> so the
        /// boiling/freezing/lava fallback paths (which clone from the
        /// captured vanilla material) keep working unchanged — they don't
        /// want our two-sided shader bleeding into a re-tinted boiling
        /// pool, only the actual water surface does.
        /// </summary>
        private static Material GetWrappedWaterOrRaw()
        {
            if (s_waterMaterial == null) return null;
            // Ask the shader mod (FiresTossinShade) via FUC to skin the captured
            // vanilla water with its custom water shader. No shader mod installed
            // → Wrap returns the source unchanged and we serve plain vanilla
            // water. See FiresCore.Services.WaterShaderBridge.
            if (!FiresCore.Services.WaterShaderBridge.HasWrapper) return s_waterMaterial;
            if (s_waterMaterialWrapped != null) return s_waterMaterialWrapped;

            var wrapped = FiresCore.Services.WaterShaderBridge.Wrap(s_waterMaterial);
            // If Wrap returned the source unchanged (bundle missing / shader not
            // ready), don't cache it as "wrapped" — next call will retry.
            // Otherwise lock it in.
            if (wrapped != null && wrapped != s_waterMaterial)
            {
                s_waterMaterialWrapped = wrapped;
                return s_waterMaterialWrapped;
            }
            return s_waterMaterial;
        }

        /// <summary>
        /// Creates a snow surface material — soft white with very low gloss.
        /// Tries to base it off the ice material (so the captured Valheim
        /// snow/ice shader handles lighting correctly), then desaturates and
        /// brightens. Falls back to Standard if neither ice nor water capture
        /// is available.
        /// </summary>
        private static Material CreateSnowFallback()
        {
            if (s_snowMaterial != null) return s_snowMaterial;

            Material baseMat = s_iceMaterial != null ? s_iceMaterial
                              : (s_waterMaterial != null ? s_waterMaterial : null);

            // 2026-05-25 round-20: Real-snow tint instead of pure white
            // (looked like cardboard in-game). Slight cool blue cast in
            // the shadows + medium-high smoothness for a soft sparkle.
            // The geometric bumpiness from LandSnowController.GetSnowBump
            // does the heavy lifting for the "fresh drift" look — the
            // material only needs to read as cold-white, not flat-white.
            //
            // Color is a real-world snow albedo: very slightly blue-tinted
            // off-white in midtones, brighter at peaks. Smoothness 0.32 +
            // metallic 0.05 gives the subtle sparkle without making it
            // look like an ice rink.
            Color snowAlbedo = new Color(0.93f, 0.95f, 0.99f, 1f);

            if (baseMat != null)
            {
                s_snowMaterial = new Material(baseMat);
                s_snowMaterial.name = "CustomWater_Snow";

                if (s_snowMaterial.HasProperty("_Color"))
                    s_snowMaterial.SetColor("_Color", snowAlbedo);
                if (s_snowMaterial.HasProperty("_BaseColor"))
                    s_snowMaterial.SetColor("_BaseColor", snowAlbedo);
                // Subtle sparkle — not mirror-shiny but not matte either.
                if (s_snowMaterial.HasProperty("_Glossiness"))
                    s_snowMaterial.SetFloat("_Glossiness", 0.32f);
                if (s_snowMaterial.HasProperty("_Smoothness"))
                    s_snowMaterial.SetFloat("_Smoothness", 0.32f);
                if (s_snowMaterial.HasProperty("_Metallic"))
                    s_snowMaterial.SetFloat("_Metallic", 0.05f);

                // If the base material had a normal/bump map slot (Standard
                // or Custom/Heightmap both expose _BumpMap), boost the
                // bump scale slightly so the existing texture reads as
                // crisp snow grain rather than smooth ice.
                if (s_snowMaterial.HasProperty("_BumpScale"))
                    s_snowMaterial.SetFloat("_BumpScale", 1.5f);

                // Cooler shadow tint via _SpecColor if available — gives
                // shaded areas the slight blue cast real snow has.
                if (s_snowMaterial.HasProperty("_SpecColor"))
                    s_snowMaterial.SetColor("_SpecColor", new Color(0.78f, 0.85f, 0.95f, 1f));

                FiresLogger.LogVerbose("[WaterMaterialManager] Created snow material from ice/water base (cool-tinted real-snow look)");
                return s_snowMaterial;
            }

            // Last resort: Standard shader with snow tint.
            Shader shader = Shader.Find("Standard");
            if (shader == null) return null;

            s_snowMaterial = new Material(shader);
            s_snowMaterial.name = "CustomWater_SnowFallback";
            s_snowMaterial.SetColor("_Color", snowAlbedo);
            s_snowMaterial.SetFloat("_Glossiness", 0.32f);
            s_snowMaterial.SetFloat("_Smoothness", 0.32f);
            s_snowMaterial.SetFloat("_Metallic", 0.05f);
            if (s_snowMaterial.HasProperty("_SpecColor"))
                s_snowMaterial.SetColor("_SpecColor", new Color(0.78f, 0.85f, 0.95f, 1f));
            FiresLogger.LogVerbose("[WaterMaterialManager] Created fallback snow material (Standard shader, cool tint)");
            return s_snowMaterial;
        }

        private static void Initialize()
        {
            // If every slot we care about already holds a real captured
            // material, nothing to do. Otherwise re-attempt capture for
            // any slot still on a fallback — a later call (after zones
            // stream in) may now find real WaterVolume / liquid prefabs
            // that weren't present on the first attempt.
            bool allCaptured = s_waterCaptured && s_tarCaptured
                            && s_iceCaptured && s_lavaCaptured;
            if (s_initialized && allCaptured) return;

            CaptureVanillaMaterials();

            // Backfill fallbacks for whatever capture still didn't get.
            // Fallback materials are placeholders — the next Initialize()
            // call retries capture, so a zone that streams in later can
            // upgrade us to the real shader.
            if (s_waterMaterial == null) CreateWaterFallback();
            if (s_tarMaterial == null) CreateTarFallback();

            s_initialized = true;
        }

        private static void CaptureVanillaMaterials()
        {
            // Retry-aware: each slot only attempts capture if it's not
            // already holding a real captured material. Once a slot is
            // captured the flag stays true and that capture function
            // is skipped on subsequent Initialize() calls.
            if (!s_waterCaptured) CaptureWaterMaterial();
            if (!s_tarCaptured) CaptureTarMaterial();
            if (!s_iceCaptured) CaptureIceMaterial();
            if (!s_lavaCaptured) CaptureLavaMaterial();

            // Infer captured-vs-fallback from material name. Every
            // capture-success path sets the material name to a token
            // without "Fallback"; every fallback creator suffixes
            // "Fallback" / "_Fallback_". This lets us treat all the
            // tar/ice/lava success branches uniformly without having
            // to set per-path flags.
            s_waterCaptured = HasRealCapture(s_waterMaterial);
            s_tarCaptured = HasRealCapture(s_tarMaterial);
            s_iceCaptured = HasRealCapture(s_iceMaterial);
            s_lavaCaptured = HasRealCapture(s_lavaMaterial);
        }

        private static bool HasRealCapture(Material m)
        {
            if (m == null) return false;
            string n = m.name ?? string.Empty;
            return n.IndexOf("Fallback", System.StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static void CaptureWaterMaterial()
        {
            // Strategy 1: any active WaterVolume instance in the scene.
            // Empty during initial prefab-prewarm (no zones loaded yet),
            // populated as soon as the first water zone streams in.
            if (WaterVolume.Instances != null && WaterVolume.Instances.Count > 0)
            {
                foreach (var wv in WaterVolume.Instances)
                {
                    if (wv == null) continue;

                    if (wv.m_waterSurface != null && wv.m_waterSurface.sharedMaterial != null)
                    {
                        s_waterMaterial = new Material(wv.m_waterSurface.sharedMaterial);
                        s_waterMaterial.name = "CustomWater_Surface";
                        s_waterCaptured = true;
                        FiresLogger.LogVerbose($"[WaterMaterialManager] Captured water material from WaterVolume");
                        return;
                    }
                }
            }

            if (ZNetScene.instance == null) return;

            // Strategy 2: try the curated candidate names. Vanilla Valheim
            // has multiple variants ("Water", "WaterSurface", "WaterEdge",
            // "water_d_v2") — older code only checked "water_ocean" which
            // does not exist in vanilla, so capture always missed here.
            foreach (var prefabName in WATER_PREFAB_NAMES)
            {
                var prefab = ZNetScene.instance.GetPrefab(prefabName);
                if (prefab == null) continue;

                var wv = prefab.GetComponent<WaterVolume>();
                if (wv == null) wv = prefab.GetComponentInChildren<WaterVolume>(true);
                if (wv == null) continue;

                if (wv.m_waterSurface != null && wv.m_waterSurface.sharedMaterial != null)
                {
                    s_waterMaterial = new Material(wv.m_waterSurface.sharedMaterial);
                    s_waterMaterial.name = "CustomWater_Surface";
                    s_waterCaptured = true;
                    FiresLogger.LogVerbose($"[WaterMaterialManager] Captured water material from ZNetScene prefab '{prefabName}'");
                    return;
                }
            }

            // Strategy 3: brute-force walk every registered prefab and
            // grab the first one carrying a WaterVolume with a populated
            // m_waterSurface material. Mirrors the tar-capture's
            // dictionary-walk fallback. Uses reflection because
            // m_namedPrefabs / m_prefabs are private on ZNetScene.
            var prefabsField = typeof(ZNetScene).GetField("m_namedPrefabs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (prefabsField == null)
            {
                prefabsField = typeof(ZNetScene).GetField("m_prefabs",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }

            if (prefabsField != null)
            {
                var prefabs = prefabsField.GetValue(ZNetScene.instance);
                if (prefabs is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        var prefab = entry.Value as GameObject;
                        if (prefab == null) continue;

                        var wv = prefab.GetComponent<WaterVolume>();
                        if (wv == null) wv = prefab.GetComponentInChildren<WaterVolume>(true);
                        if (wv == null) continue;

                        if (wv.m_waterSurface == null || wv.m_waterSurface.sharedMaterial == null) continue;

                        s_waterMaterial = new Material(wv.m_waterSurface.sharedMaterial);
                        s_waterMaterial.name = "CustomWater_Surface";
                        s_waterCaptured = true;
                        FiresLogger.LogVerbose($"[WaterMaterialManager] Captured water material from scanned prefab '{prefab.name}'");
                        return;
                    }
                }
                else if (prefabs is System.Collections.IList list)
                {
                    // m_prefabs (the list) — same scan, different shape.
                    for (int i = 0; i < list.Count; i++)
                    {
                        var prefab = list[i] as GameObject;
                        if (prefab == null) continue;

                        var wv = prefab.GetComponent<WaterVolume>();
                        if (wv == null) wv = prefab.GetComponentInChildren<WaterVolume>(true);
                        if (wv == null) continue;

                        if (wv.m_waterSurface == null || wv.m_waterSurface.sharedMaterial == null) continue;

                        s_waterMaterial = new Material(wv.m_waterSurface.sharedMaterial);
                        s_waterMaterial.name = "CustomWater_Surface";
                        s_waterCaptured = true;
                        FiresLogger.LogVerbose($"[WaterMaterialManager] Captured water material from scanned prefab '{prefab.name}'");
                        return;
                    }
                }
            }
        }

        private static void CaptureTarMaterial()
        {
            // Strategy 1: Search active LiquidVolume instances
            var liquidVolumes = Object.FindObjectsByType<LiquidVolume>(FindObjectsSortMode.None);
            foreach (var lv in liquidVolumes)
            {
                if (lv == null || lv.m_liquidType != LiquidType.Tar) continue;

                var renderer = lv.GetComponent<MeshRenderer>();
                if (renderer == null) renderer = lv.GetComponentInChildren<MeshRenderer>();

                if (renderer != null && renderer.sharedMaterial != null)
                {
                    s_tarMaterial = new Material(renderer.sharedMaterial);
                    s_tarMaterial.name = "CustomWater_Tar";
                    s_tarCaptured = true;
                    FiresLogger.LogVerbose($"[WaterMaterialManager] Captured tar material from LiquidVolume: {lv.name}");
                    return;
                }
            }

            // Strategy 2: Search ZNetScene prefabs by name
            if (ZNetScene.instance != null)
            {
                foreach (var prefabName in TAR_PREFAB_NAMES)
                {
                    var prefab = ZNetScene.instance.GetPrefab(prefabName);
                    if (prefab != null)
                    {
                        var tarMat = ExtractMaterialFromPrefab(prefab, "tar", "Tar");
                        if (tarMat != null)
                        {
                            s_tarMaterial = new Material(tarMat);
                            s_tarMaterial.name = "CustomWater_Tar";
                            s_tarCaptured = true;
                            FiresLogger.LogVerbose($"[WaterMaterialManager] Captured tar material from prefab: {prefabName}");
                            return;
                        }
                    }
                }

                // Strategy 3: Search all prefabs for tar-related materials
                // Use reflection to get prefab dictionary since field name may vary
                var prefabsField = typeof(ZNetScene).GetField("m_namedPrefabs",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (prefabsField == null)
                {
                    prefabsField = typeof(ZNetScene).GetField("m_prefabs",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }

                if (prefabsField != null)
                {
                    var prefabs = prefabsField.GetValue(ZNetScene.instance);
                    if (prefabs is System.Collections.IDictionary dict)
                    {
                        foreach (System.Collections.DictionaryEntry entry in dict)
                        {
                            var prefab = entry.Value as GameObject;
                            if (prefab == null) continue;

                            string name = prefab.name.ToLower();

                            if (name.Contains("tar") || name.Contains("goo"))
                            {
                                var tarMat = ExtractMaterialFromPrefab(prefab, "tar", "Tar", "goo", "Goo");
                                if (tarMat != null)
                                {
                                    s_tarMaterial = new Material(tarMat);
                                    s_tarMaterial.name = "CustomWater_Tar";
                                    s_tarCaptured = true;
                                    FiresLogger.LogVerbose($"[WaterMaterialManager] Captured tar material from prefab: {prefab.name}");
                                    return;
                                }
                            }
                        }
                    }
                }
            }

            FiresLogger.LogWarning("[WaterMaterialManager] Could not capture tar material from vanilla - using fallback");
        }

        /// <summary>
        /// Extracts a material from a prefab by searching for keywords in material names.
        /// </summary>
        private static Material ExtractMaterialFromPrefab(GameObject prefab, params string[] keywords)
        {
            if (prefab == null) return null;

            // Check all renderers in prefab
            var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                var mats = renderer.sharedMaterials;
                foreach (var mat in mats)
                {
                    if (mat == null) continue;

                    string matName = mat.name.ToLower();
                    foreach (var keyword in keywords)
                    {
                        if (matName.Contains(keyword.ToLower()))
                        {
                            return mat;
                        }
                    }
                }
            }

            // Also check particle system renderers
            var particleRenderers = prefab.GetComponentsInChildren<ParticleSystemRenderer>(true);
            foreach (var renderer in particleRenderers)
            {
                if (renderer == null) continue;

                var mat = renderer.sharedMaterial;
                if (mat == null) continue;

                string matName = mat.name.ToLower();
                foreach (var keyword in keywords)
                {
                    if (matName.Contains(keyword.ToLower()))
                    {
                        return mat;
                    }
                }
            }

            // Just return any material if we found renderers
            if (renderers.Length > 0 && renderers[0] != null && renderers[0].sharedMaterial != null)
            {
                return renderers[0].sharedMaterial;
            }

            return null;
        }

        private static void CreateWaterFallback()
        {
            // Try to find a transparent shader
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            if (shader != null)
            {
                s_waterMaterial = new Material(shader);
                s_waterMaterial.name = "CustomWater_Fallback";
                s_waterMaterial.SetColor("_Color", new Color(0.2f, 0.5f, 0.9f, 0.6f));

                // Setup for transparency
                SetupTransparentMaterial(s_waterMaterial);

                FiresLogger.LogWarning("[WaterMaterialManager] Using fallback water material - shader capture failed");
            }
            else
            {
                FiresLogger.LogError("[WaterMaterialManager] Could not find any shader for water material!");
            }
        }

        private static void CreateTarFallback()
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            if (shader != null)
            {
                s_tarMaterial = new Material(shader);
                s_tarMaterial.name = "CustomWater_TarFallback";

                // Dark brownish-black color with high opacity
                s_tarMaterial.SetColor("_Color", new Color(0.08f, 0.05f, 0.03f, 0.95f));

                // Make it look wet/shiny
                s_tarMaterial.SetFloat("_Metallic", 0.7f);
                s_tarMaterial.SetFloat("_Glossiness", 0.85f);
                s_tarMaterial.SetFloat("_Smoothness", 0.85f);

                // Setup for transparency (slight)
                SetupTransparentMaterial(s_tarMaterial);

                FiresLogger.LogWarning("[WaterMaterialManager] Using fallback tar material");
            }
        }

        /// <summary>
        /// Captures ice material from mountain biome prefabs.
        /// </summary>
        private static void CaptureIceMaterial()
        {
            if (ZNetScene.instance == null) return;

            string[] icePrefabNames = {
                "ice_floor",
                "ice_rock1",
                "MountainKit_ice_floor",
                "Iceblock",
                "IceFloor",
                "mountainkit_ice",
                "ice_ground"
            };

            foreach (var name in icePrefabNames)
            {
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab != null)
                {
                    var mat = ExtractMaterialFromPrefab(prefab, "ice", "Ice", "frozen", "Frozen");
                    if (mat != null)
                    {
                        s_iceMaterial = new Material(mat);
                        s_iceMaterial.name = "CustomWater_Ice";
                        TuneIceMaterial(s_iceMaterial);
                        FiresLogger.LogVerbose($"[WaterMaterialManager] Captured ice material from prefab: {name}");
                        return;
                    }
                }
            }

            // Search all prefabs for ice-related materials
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null) continue;
                string pName = prefab.name.ToLower();
                if (!pName.Contains("ice") && !pName.Contains("frozen")) continue;

                var mat = ExtractMaterialFromPrefab(prefab, "ice", "Ice", "frozen", "Frozen");
                if (mat != null)
                {
                    s_iceMaterial = new Material(mat);
                    s_iceMaterial.name = "CustomWater_Ice";
                    TuneIceMaterial(s_iceMaterial);
                    FiresLogger.LogVerbose($"[WaterMaterialManager] Captured ice material from prefab: {prefab.name}");
                    return;
                }
            }

            FiresLogger.LogVerbose("[WaterMaterialManager] Could not capture ice material from vanilla - will use fallback");
        }

        private static Material CreateIceFallback()
        {
            if (s_iceMaterial != null) return s_iceMaterial;

            Shader shader = Shader.Find("Standard");
            if (shader == null) return s_waterMaterial;

            s_iceMaterial = new Material(shader);
            s_iceMaterial.name = "CustomWater_IceFallback";
            s_iceMaterial.SetColor("_Color", new Color(0.75f, 0.88f, 0.95f, 0.80f));
            s_iceMaterial.SetFloat("_Metallic", 0.02f);
            s_iceMaterial.SetFloat("_Glossiness", 0.35f);
            s_iceMaterial.SetFloat("_Smoothness", 0.35f);
            SetupTransparentMaterial(s_iceMaterial);

            FiresLogger.LogVerbose("[WaterMaterialManager] Created fallback ice material");
            return s_iceMaterial;
        }

        /// <summary>
        /// Tunes an ice material after capture to reduce excessive reflectivity/brightness.
        /// Vanilla ice materials can be blindingly white and shiny - this brings them to
        /// a level where the surface texture is visible even in daylight.
        /// </summary>
        private static void TuneIceMaterial(Material mat)
        {
            if (mat == null) return;

            // Reduce smoothness/glossiness so the surface isn't a mirror
            if (mat.HasProperty("_Glossiness"))
                mat.SetFloat("_Glossiness", Mathf.Min(mat.GetFloat("_Glossiness"), 0.4f));
            if (mat.HasProperty("_Smoothness"))
                mat.SetFloat("_Smoothness", Mathf.Min(mat.GetFloat("_Smoothness"), 0.4f));

            // Pull metallic down so it doesn't act like a metal mirror
            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", Mathf.Min(mat.GetFloat("_Metallic"), 0.05f));

            // If specular highlights exist, dial them back
            if (mat.HasProperty("_SpecularHighlights"))
                mat.SetFloat("_SpecularHighlights", 0.3f);
            if (mat.HasProperty("_GlossyReflections"))
                mat.SetFloat("_GlossyReflections", 0.2f);

            // Slight color tint toward blue-white to ensure it reads as ice, not a white void
            if (mat.HasProperty("_Color"))
            {
                Color c = mat.GetColor("_Color");
                // Only darken if it's too bright (luminance > 0.85)
                float luminance = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                if (luminance > 0.85f)
                {
                    mat.SetColor("_Color", new Color(
                        c.r * 0.82f,
                        c.g * 0.88f,
                        c.b * 0.95f,
                        c.a
                    ));
                }
            }

            FiresLogger.LogVerbose($"[WaterMaterialManager] Tuned ice material: smoothness/glossiness capped at 0.4, metallic at 0.05");
        }

        /// <summary>
        /// Captures lava material from Ashlands LiquidVolume instances or prefabs.
        /// 
        /// PREFERRED: Capture from LiquidVolume instances in Ashlands biome areas,
        /// or from lava-specific prefabs that use the correct liquid surface shader.
        /// 
        /// FALLBACK: High-quality emissive material with proper lava appearance.
        /// </summary>
        private static void CaptureLavaMaterial()
        {
            if (ZNetScene.instance == null) return;

            // STRATEGY 1: Search active LiquidVolume instances near Ashlands
            var liquidVolumes = Object.FindObjectsByType<LiquidVolume>(FindObjectsSortMode.None);
            foreach (var lv in liquidVolumes)
            {
                if (lv == null) continue;

                // Check if this liquid volume is in or near Ashlands
                var renderer = lv.GetComponent<MeshRenderer>();
                if (renderer == null) renderer = lv.GetComponentInChildren<MeshRenderer>();
                if (renderer == null || renderer.sharedMaterial == null) continue;

                string matName = renderer.sharedMaterial.name.ToLower();
                string shaderName = renderer.sharedMaterial.shader != null ? renderer.sharedMaterial.shader.name.ToLower() : "";

                // Look for lava-related materials on liquid volumes
                if (matName.Contains("lava") || matName.Contains("magma") ||
                    shaderName.Contains("lava") || shaderName.Contains("magma"))
                {
                    s_lavaMaterial = new Material(renderer.sharedMaterial);
                    s_lavaMaterial.name = "CustomWater_Lava";
                    FiresLogger.LogVerbose($"[WaterMaterialManager] Captured lava material from LiquidVolume: {lv.name} (shader: {renderer.sharedMaterial.shader?.name})");
                    return;
                }
            }

            // STRATEGY 2: Search ZNetScene prefabs for lava-specific objects
            // These are the actual Valheim prefab names for lava surfaces
            string[] lavaPrefabNames = {
                "demister_lava",
                "Ashlands_dvergr_lava",
                "LavaFloor",
                "lava_floor",
                "Lava_floor_burnable",
                "lava_river",
                "LavaPool",
                "fx_lava_bubble",
                "Ashlands_lava",
                "CharredFortress_lava",
                "lavarock1",
                "magma"
            };

            foreach (var name in lavaPrefabNames)
            {
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab != null)
                {
                    // First try to get LiquidVolume material (proper liquid shader)
                    var lv = prefab.GetComponent<LiquidVolume>();
                    if (lv == null) lv = prefab.GetComponentInChildren<LiquidVolume>(true);
                    if (lv != null)
                    {
                        var lvRenderer = lv.GetComponent<MeshRenderer>();
                        if (lvRenderer == null) lvRenderer = lv.GetComponentInChildren<MeshRenderer>(true);
                        if (lvRenderer != null && lvRenderer.sharedMaterial != null)
                        {
                            s_lavaMaterial = new Material(lvRenderer.sharedMaterial);
                            s_lavaMaterial.name = "CustomWater_Lava";
                            FiresLogger.LogVerbose($"[WaterMaterialManager] Captured lava material from LiquidVolume in prefab: {name} (shader: {lvRenderer.sharedMaterial.shader?.name})");
                            return;
                        }
                    }

                    // Then try general material extraction
                    var mat = ExtractMaterialFromPrefab(prefab, "lava", "Lava", "magma", "Magma");
                    if (mat != null)
                    {
                        s_lavaMaterial = new Material(mat);
                        s_lavaMaterial.name = "CustomWater_Lava";
                        FiresLogger.LogVerbose($"[WaterMaterialManager] Captured lava material from prefab: {name} (shader: {mat.shader?.name})");
                        return;
                    }
                }
            }

            // STRATEGY 3: Search all prefabs for lava-related materials
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null) continue;
                string pName = prefab.name.ToLower();
                if (!pName.Contains("lava") && !pName.Contains("magma")) continue;

                // Prefer LiquidVolume materials
                var lv = prefab.GetComponent<LiquidVolume>();
                if (lv == null) lv = prefab.GetComponentInChildren<LiquidVolume>(true);
                if (lv != null)
                {
                    var lvRenderer = lv.GetComponent<MeshRenderer>();
                    if (lvRenderer == null) lvRenderer = lv.GetComponentInChildren<MeshRenderer>(true);
                    if (lvRenderer != null && lvRenderer.sharedMaterial != null)
                    {
                        s_lavaMaterial = new Material(lvRenderer.sharedMaterial);
                        s_lavaMaterial.name = "CustomWater_Lava";
                        FiresLogger.LogVerbose($"[WaterMaterialManager] Captured lava material from LiquidVolume in: {prefab.name}");
                        return;
                    }
                }

                var mat = ExtractMaterialFromPrefab(prefab, "lava", "Lava", "magma", "Magma");
                if (mat != null)
                {
                    s_lavaMaterial = new Material(mat);
                    s_lavaMaterial.name = "CustomWater_Lava";
                    FiresLogger.LogVerbose($"[WaterMaterialManager] Captured lava material from prefab: {prefab.name}");
                    return;
                }
            }

            // STRATEGY 4: Try to clone tar material and re-tint it for lava
            // Tar uses the correct liquid surface shader, so tinting it orange/red
            // gives a much better result than Standard shader
            if (s_tarMaterial != null && !s_tarMaterial.name.Contains("Fallback"))
            {
                s_lavaMaterial = new Material(s_tarMaterial);
                s_lavaMaterial.name = "CustomWater_Lava_FromTar";
                if (s_lavaMaterial.HasProperty("_Color"))
                {
                    s_lavaMaterial.SetColor("_Color", new Color(1f, 0.25f, 0.02f, 0.95f));
                }
                if (s_lavaMaterial.HasProperty("_EmissionColor"))
                {
                    s_lavaMaterial.EnableKeyword("_EMISSION");
                    s_lavaMaterial.SetColor("_EmissionColor", new Color(1f, 0.35f, 0.05f) * 2.5f);
                }
                FiresLogger.LogVerbose("[WaterMaterialManager] Created lava material by tinting captured tar material");
                return;
            }

            FiresLogger.LogVerbose("[WaterMaterialManager] Could not capture lava material from vanilla - will use fallback");
        }

        private static Material CreateLavaFallback()
        {
            if (s_lavaMaterial != null) return s_lavaMaterial;

            // Try to clone captured water material and tint it for lava
            // Water material uses the correct WaterVolume shader with wave animation
            if (s_waterMaterial != null)
            {
                s_lavaMaterial = new Material(s_waterMaterial);
                s_lavaMaterial.name = "CustomWater_LavaFallback_FromWater";

                // Tint the water shader to look like lava
                if (s_lavaMaterial.HasProperty("_Color"))
                {
                    s_lavaMaterial.SetColor("_Color", new Color(1f, 0.25f, 0.02f, 0.95f));
                }
                // Some water shaders use _BaseColor
                if (s_lavaMaterial.HasProperty("_BaseColor"))
                {
                    s_lavaMaterial.SetColor("_BaseColor", new Color(1f, 0.25f, 0.02f, 0.95f));
                }
                if (s_lavaMaterial.HasProperty("_EmissionColor"))
                {
                    s_lavaMaterial.EnableKeyword("_EMISSION");
                    s_lavaMaterial.SetColor("_EmissionColor", new Color(1f, 0.35f, 0.05f) * 2.5f);
                }

                FiresLogger.LogVerbose("[WaterMaterialManager] Created lava fallback by tinting captured water material");
                return s_lavaMaterial;
            }

            // Last resort: Standard shader with strong emission
            Shader shader = Shader.Find("Standard");
            if (shader == null) return s_waterMaterial;

            s_lavaMaterial = new Material(shader);
            s_lavaMaterial.name = "CustomWater_LavaFallback";
            s_lavaMaterial.SetColor("_Color", new Color(1f, 0.25f, 0.02f, 0.95f));
            s_lavaMaterial.SetColor("_EmissionColor", new Color(1f, 0.35f, 0.05f) * 3f);
            s_lavaMaterial.EnableKeyword("_EMISSION");
            s_lavaMaterial.SetFloat("_Metallic", 0.6f);
            s_lavaMaterial.SetFloat("_Glossiness", 0.85f);
            s_lavaMaterial.SetFloat("_Smoothness", 0.85f);

            // Use opaque rendering for lava (not transparent) - lava is opaque
            s_lavaMaterial.SetFloat("_Mode", 0); // Opaque
            s_lavaMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            s_lavaMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            s_lavaMaterial.SetInt("_ZWrite", 1);
            s_lavaMaterial.DisableKeyword("_ALPHATEST_ON");
            s_lavaMaterial.DisableKeyword("_ALPHABLEND_ON");
            s_lavaMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            s_lavaMaterial.renderQueue = 2000;

            FiresLogger.LogVerbose("[WaterMaterialManager] Created fallback lava material (Standard shader)");
            return s_lavaMaterial;
        }

        /// <summary>
        /// Creates boiling water material - tinted orange version of captured water material.
        /// </summary>
        private static Material CreateBoilingFallback()
        {
            if (s_boilingMaterial != null) return s_boilingMaterial;

            // Clone from water material if available
            if (s_waterMaterial != null)
            {
                s_boilingMaterial = new Material(s_waterMaterial);
                s_boilingMaterial.name = "CustomWater_Boiling";

                // Tint toward orange/red
                if (s_boilingMaterial.HasProperty("_Color"))
                {
                    Color base_color = s_boilingMaterial.GetColor("_Color");
                    s_boilingMaterial.SetColor("_Color", Color.Lerp(base_color, new Color(1f, 0.4f, 0.1f, base_color.a), 0.5f));
                }
            }
            else
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) return null;

                s_boilingMaterial = new Material(shader);
                s_boilingMaterial.name = "CustomWater_BoilingFallback";
                s_boilingMaterial.SetColor("_Color", new Color(0.8f, 0.35f, 0.1f, 0.7f));
                s_boilingMaterial.SetColor("_EmissionColor", new Color(0.3f, 0.1f, 0f) * 0.5f);
                s_boilingMaterial.EnableKeyword("_EMISSION");
                SetupTransparentMaterial(s_boilingMaterial);
            }

            FiresLogger.LogVerbose("[WaterMaterialManager] Created boiling water material");
            return s_boilingMaterial;
        }

        /// <summary>
        /// Creates freezing water material - tinted deep blue version of captured water material.
        /// </summary>
        private static Material CreateFreezingFallback()
        {
            if (s_freezingMaterial != null) return s_freezingMaterial;

            // Clone from water material if available
            if (s_waterMaterial != null)
            {
                s_freezingMaterial = new Material(s_waterMaterial);
                s_freezingMaterial.name = "CustomWater_Freezing";

                // Tint toward deep blue/cyan
                if (s_freezingMaterial.HasProperty("_Color"))
                {
                    Color base_color = s_freezingMaterial.GetColor("_Color");
                    s_freezingMaterial.SetColor("_Color", Color.Lerp(base_color, new Color(0.1f, 0.3f, 0.9f, base_color.a), 0.5f));
                }
            }
            else
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) return null;

                s_freezingMaterial = new Material(shader);
                s_freezingMaterial.name = "CustomWater_FreezingFallback";
                s_freezingMaterial.SetColor("_Color", new Color(0.1f, 0.25f, 0.8f, 0.7f));
                SetupTransparentMaterial(s_freezingMaterial);
            }

            FiresLogger.LogVerbose("[WaterMaterialManager] Created freezing water material");
            return s_freezingMaterial;
        }

        /// <summary>
        /// Sets up a material for transparent rendering.
        /// </summary>
        private static void SetupTransparentMaterial(Material mat)
        {
            if (mat == null) return;

            // Standard shader transparency setup
            mat.SetFloat("_Mode", 3); // Transparent mode
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }

        /// <summary>
        /// Forces re-capture of materials. Call after world load if initial capture failed.
        /// </summary>
        public static void RefreshMaterials()
        {
            s_initialized = false;
            s_captureAttempted = false;
            s_waterMaterial = null;
            s_waterMaterialWrapped = null;
            s_tarMaterial = null;
            s_iceMaterial = null;
            s_lavaMaterial = null;
            s_boilingMaterial = null;
            s_freezingMaterial = null;
            Initialize();
        }

        /// <summary>
        /// Returns true if materials were successfully captured from Valheim.
        /// </summary>
        public static bool HasValidMaterials()
        {
            Initialize();
            return s_waterMaterial != null;
        }

        /// <summary>
        /// Returns true if tar material was captured (not using fallback).
        /// </summary>
        public static bool HasValidTarMaterial()
        {
            Initialize();
            return s_tarMaterial != null && !s_tarMaterial.name.Contains("Fallback");
        }

        /// <summary>
        /// Returns true if ice material was captured (not using fallback).
        /// </summary>
        public static bool HasValidIceMaterial()
        {
            Initialize();
            return s_iceMaterial != null && !s_iceMaterial.name.Contains("Fallback");
        }

        /// <summary>
        /// Returns true if lava material was captured (not using fallback).
        /// </summary>
        public static bool HasValidLavaMaterial()
        {
            Initialize();
            return s_lavaMaterial != null && !s_lavaMaterial.name.Contains("Fallback");
        }

        /// <summary>
        /// Gets debug info about captured materials.
        /// </summary>
        public static string GetDebugInfo()
        {
            Initialize();

            string FormatMat(string label, Material mat)
            {
                if (mat == null) return $"{label}: NULL";
                return $"{label}: {mat.name} (Shader: {mat.shader?.name ?? "null"})";
            }

            return $"[WaterMaterialManager]\n" +
                   $"{FormatMat("Water", s_waterMaterial)}\n" +
                   $"{FormatMat("Tar", s_tarMaterial)}\n" +
                   $"{FormatMat("Ice", s_iceMaterial)}\n" +
                   $"{FormatMat("Lava", s_lavaMaterial)}\n" +
                   $"{FormatMat("Boiling", s_boilingMaterial)}\n" +
                   $"{FormatMat("Freezing", s_freezingMaterial)}";
        }
    }
}
