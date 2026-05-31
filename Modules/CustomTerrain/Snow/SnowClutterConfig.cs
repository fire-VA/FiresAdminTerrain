using UnityEngine;

namespace VerdantsAscent.Terrain.Snow
{
    /// <summary>
    /// Configures and registers a snow clutter type with the ClutterSystem.
    /// 
    /// APPROACH: We clone an existing instanced grass prefab and swap its
    /// InstanceRenderer.m_material (which is a PUBLIC field) to our snow
    /// material. The material uses the same instanced-grass shader so the
    /// player push effect still works, but texture/color/sway are overridden.
    /// 
    /// FUTURE: Once a proper Unity-authored snow clutter prefab is added to
    /// the asset bundle (with correct material baked in), TryLoadFromBundle()
    /// will pick it up automatically and skip the runtime clone path entirely.
    /// </summary>
    public static class SnowClutterConfig
    {
        /// <summary>
        /// Primary clutter name � matches the Unity prefab name in the asset bundle.
        /// This is the grass-blade layer that provides the player push-away effect.
        /// </summary>
        public const string SNOW_CLUTTER_NAME = "VA_InstancedSnow";

        /// <summary>
        /// Ground-cover layer name � flat disc instances that provide solid white coverage.
        /// These sit underneath the grass-blade layer so you can't see through gaps.
        /// </summary>
        public const string SNOW_GROUND_NAME = "VA_InstancedSnowGround";

        /// <summary>
        /// Legacy name from before the Unity prefab existed.
        /// Kept for backward compatibility with existing persistence data
        /// that may have been saved under the old name.
        /// </summary>
        public const string SNOW_CLUTTER_NAME_LEGACY = "instanced_snow_cover";

        private static bool _registered;
        private static bool _groundRegistered;
        private static bool _loadedFromBundle;
        private static GameObject _snowPrefab;
        private static GameObject _snowGroundPrefab;
        private static Material _snowMaterial;
        private static Material _snowGroundMaterial;

        public static string ClutterName => SNOW_CLUTTER_NAME;
        public static string GroundClutterName => SNOW_GROUND_NAME;
        public static Material SnowMaterial => _snowMaterial;

        /// <summary>
        /// Called by VAClutterManager after it has registered the snow clutter prefab
        /// with ClutterSystem. This prevents EnsureRegistered() from duplicating
        /// the registration or falling back to the runtime clone path.
        /// 
        /// IMPORTANT: VAClutterManager only rebuilds the material with the vanilla
        /// grass shader (so it actually renders at runtime). It does NOT apply the
        /// snow-specific overrides (solid white texture, no alpha cutout, no wind
        /// sway) or the volumetric particle mesh. We apply those here so the prefab
        /// is fully configured before any paint commands use it.
        /// </summary>
        public static void MarkRegisteredByManager(GameObject prefab)
        {
            if (prefab == null) return;

            _snowPrefab = prefab;
            _registered = true;

            var ir = prefab.GetComponent<InstanceRenderer>();
            if (ir != null)
            {
                // Apply the volumetric snow particle mesh (replaces the flat
                // grass billboard with our dense mound of tiny triangles).
                if (ir.m_mesh != null)
                {
                    ir.m_mesh = SnowGroundMesh.GetOrCreateAugmentedMesh(ir.m_mesh);
                    Debug.Log($"[SnowClutterConfig] Applied volumetric snow mesh: {ir.m_mesh.name} ({ir.m_mesh.vertexCount} verts)");
                }

                // Apply the full snow material overrides: solid white texture,
                // white color, no alpha cutout, no wind sway.
                if (ir.m_material != null)
                {
                    ir.m_material = CreateSnowMaterial(ir.m_material);
                    _snowMaterial = ir.m_material;
                    Debug.Log($"[SnowClutterConfig] Applied snow material overrides: {ir.m_material.name}, shader={ir.m_material.shader.name}");
                }

                _loadedFromBundle = true;
                Debug.Log($"[SnowClutterConfig] Marked as registered by VAClutterManager (fully configured)");
            }
            else
            {
                Debug.Log($"[SnowClutterConfig] Marked as registered by VAClutterManager (no InstanceRenderer � loadedFromBundle=false)");
            }
        }

        /// <summary>
        /// Returns true if the given clutter type name is any of our snow clutter types.
        /// Matches the blade layer, ground layer, and legacy name.
        /// </summary>
        public static bool IsSnowClutter(string clutterTypeName)
        {
            if (string.IsNullOrEmpty(clutterTypeName)) return false;
            return clutterTypeName == SNOW_CLUTTER_NAME
                || clutterTypeName == SNOW_GROUND_NAME
                || clutterTypeName == SNOW_CLUTTER_NAME_LEGACY;
        }

        /// <summary>
        /// Remaps a legacy clutter type name to the current name.
        /// Called during persistence restore so old save data that stored
        /// "instanced_snow_cover" resolves to "VA_InstancedSnow".
        /// Returns the input unchanged if it's not a legacy snow name.
        /// </summary>
        public static string RemapLegacyName(string clutterTypeName)
        {
            if (clutterTypeName == SNOW_CLUTTER_NAME_LEGACY)
                return SNOW_CLUTTER_NAME;
            return clutterTypeName;
        }

        /// <summary>
        /// Ensures GPU instancing is enabled on a live InstanceRenderer's material.
        /// No longer force-applies a snow material � the prefab's material is rebuilt
        /// with the vanilla grass shader by VAClutterManager at registration time,
        /// so Object.Instantiate produces a correctly-rendering copy automatically.
        /// </summary>
        public static void ApplySnowMaterialToLiveInstance(InstanceRenderer ir)
        {
            if (ir == null || ir.m_material == null) return;

            if (!ir.m_material.enableInstancing)
                ir.m_material.enableInstancing = true;
        }

        /// <summary>
        /// Ensures the snow ground-cover clutter type is registered with ClutterSystem.
        /// This creates a second clutter entry that uses flat disc meshes for solid
        /// coverage underneath the grass-blade snow layer.
        /// </summary>
        public static bool EnsureGroundRegistered()
        {
            if (_groundRegistered) return true;

            if (ClutterSystem.instance == null || ClutterSystem.instance.m_clutter == null)
                return false;

            // We need the blade layer registered first to get the snow material
            if (!EnsureRegistered()) return false;

            // Find a donor instanced clutter to clone the prefab from
            ClutterSystem.Clutter donor = null;
            foreach (var c in ClutterSystem.instance.m_clutter)
            {
                if (c.m_prefab == null || !c.m_instanced) continue;
                string n = c.m_prefab.name.ToLower();
                if (n.Contains("meadows_grass_short") || n.Contains("meadows_grass"))
                {
                    donor = c;
                    break;
                }
            }

            if (donor == null)
            {
                foreach (var c in ClutterSystem.instance.m_clutter)
                {
                    if (c.m_prefab != null && c.m_instanced) { donor = c; break; }
                }
            }

            if (donor == null || donor.m_prefab == null)
            {
                Debug.LogWarning("[SnowClutterConfig] No instanced donor for ground cover");
                return false;
            }

            // Clone the donor prefab and swap in our flat disc mesh
            _snowGroundPrefab = Object.Instantiate(donor.m_prefab);
            _snowGroundPrefab.name = SNOW_GROUND_NAME;
            _snowGroundPrefab.SetActive(false);
            Object.DontDestroyOnLoad(_snowGroundPrefab);

            var ir = _snowGroundPrefab.GetComponent<InstanceRenderer>();
            if (ir == null)
            {
                Debug.LogWarning("[SnowClutterConfig] Donor has no InstanceRenderer for ground cover");
                Object.Destroy(_snowGroundPrefab);
                return false;
            }

            // SWAP MESH: Replace the grass-blade mesh with our flat disc
            ir.m_mesh = SnowGroundMesh.GetOrCreateDiscMesh();

            // Create a ground-cover material: solid white, no cutout, no sway
            _snowGroundMaterial = CreateSnowMaterial(ir.m_material);
            ir.m_material = _snowGroundMaterial;

            // Register with ClutterSystem
            // Higher density + larger scale so discs provide solid ground coverage
            var groundClutter = new ClutterSystem.Clutter
            {
                m_prefab = _snowGroundPrefab,
                m_enabled = false, // Paint-only: do not auto-spawn globally (causes massive lag)
                m_instanced = true,
                m_amount = Mathf.Max(donor.m_amount, 30) * 3,
                m_biome = Heightmap.Biome.Mountain | Heightmap.Biome.DeepNorth,
                m_scaleMin = 0.4f,
                m_scaleMax = 0.7f,
                m_maxTilt = 45f,
                m_minAlt = -1000f,
                m_maxAlt = 10000f,
                m_snapToWater = false,
                m_onUncleared = true,
                m_onCleared = false,
                m_terrainTilt = false,
                m_randomOffset = 0f,
                m_name = SNOW_GROUND_NAME,
            };

            ClutterSystem.instance.m_clutter.Add(groundClutter);
            _groundRegistered = true;

            VerdantsAscent.Terrain.Clutter.ClutterManager.InvalidateCache();

            Debug.Log("[SnowClutterConfig] Registered snow ground-cover clutter type (flat disc mesh)");
            return true;
        }

        /// <summary>
        /// Ensures the snow clutter type is registered with ClutterSystem.
        /// Safe to call multiple times � only registers once.
        /// 
        /// Priority:
        /// 1. Try loading a pre-built prefab from the asset bundle (future)
        /// 2. Clone a donor grass prefab and swap its material at runtime
        /// </summary>
        public static bool EnsureRegistered()
        {
            if (_registered) return true;

            if (ClutterSystem.instance == null || ClutterSystem.instance.m_clutter == null)
                return false;

            // PRIORITY 1: Try loading from asset bundle (Unity-authored prefab)
            if (TryLoadFromBundle())
            {
                _registered = true;
                VerdantsAscent.Terrain.Clutter.ClutterManager.InvalidateCache();
                Debug.Log("[SnowClutterConfig] Loaded snow clutter from asset bundle");
                return true;
            }

            // PRIORITY 2: Runtime clone from donor grass prefab
            ClutterSystem.Clutter donor = null;
            foreach (var c in ClutterSystem.instance.m_clutter)
            {
                if (c.m_prefab == null || !c.m_instanced) continue;
                string n = c.m_prefab.name.ToLower();
                if (n.Contains("meadows_grass_short") || n.Contains("meadows_grass"))
                {
                    donor = c;
                    break;
                }
            }

            if (donor == null)
            {
                foreach (var c in ClutterSystem.instance.m_clutter)
                {
                    if (c.m_prefab != null && c.m_instanced)
                    {
                        donor = c;
                        break;
                    }
                }
            }

            if (donor == null || donor.m_prefab == null)
            {
                Debug.LogWarning("[SnowClutterConfig] No instanced clutter donor found");
                return false;
            }

            // Clone the donor prefab
            _snowPrefab = Object.Instantiate(donor.m_prefab);
            _snowPrefab.name = SNOW_CLUTTER_NAME;
            _snowPrefab.SetActive(false);
            Object.DontDestroyOnLoad(_snowPrefab);

            // Create the snow material from the donor's grass material
            var donorIR = _snowPrefab.GetComponent<InstanceRenderer>();
            Material baseMat = donorIR != null ? donorIR.m_material : null;
            _snowMaterial = CreateSnowMaterial(baseMat);

            // Apply snow material directly to the template prefab's InstanceRenderer
            if (donorIR != null)
            {
                donorIR.m_material = _snowMaterial;

                // Augment the mesh with fill caps so solid-white snow
                // doesn't look like a sponge through billboard gaps
                if (donorIR.m_mesh != null)
                {
                    donorIR.m_mesh = SnowGroundMesh.GetOrCreateAugmentedMesh(donorIR.m_mesh);
                }
            }

            // Scale: match vanilla grass scale range for visible height;
            // Amount: 2x for thick coverage
            var snowClutter = new ClutterSystem.Clutter
            {
                m_prefab = _snowPrefab,
                m_enabled = false, // Paint-only: do not auto-spawn globally (causes massive lag)
                m_instanced = true,
                m_amount = Mathf.Max(donor.m_amount, 30) * 2,
                m_biome = Heightmap.Biome.Mountain | Heightmap.Biome.DeepNorth,
                m_scaleMin = 0.8f,
                m_scaleMax = 1.5f,
                m_maxTilt = donor.m_maxTilt,
                m_minAlt = -1000f,
                m_maxAlt = 10000f,
                m_snapToWater = false,
                m_onUncleared = true,
                m_onCleared = false,
                m_terrainTilt = false,
                m_randomOffset = donor.m_randomOffset,
                m_name = SNOW_CLUTTER_NAME,
            };

            ClutterSystem.instance.m_clutter.Add(snowClutter);
            _registered = true;

            VerdantsAscent.Terrain.Clutter.ClutterManager.InvalidateCache();

            Debug.Log("[SnowClutterConfig] Registered snow clutter type (runtime clone, paint-only)");
            return true;
        }

        /// <summary>
        /// Tries to load the VA_InstancedSnow prefab from the asset bundle.
        /// The prefab has an InstanceRenderer with the correct snow material
        /// already baked in from Unity � no runtime material swapping needed.
        /// 
        /// Expected asset path: Assets/Custom/VAitems/clutter/VA_InstancedSnow.prefab
        /// </summary>
        private static bool TryLoadFromBundle()
        {
            var bundle = FiresAdminTerrain.assetBundle;
            if (bundle == null) return false;

            // Search paths � lowercase variants for Unity's case-insensitive bundle lookup
            string[] searchPaths = {
                "assets/custom/vaitems/clutter/va_instancedsnow.prefab",
                $"assets/custom/vaitems/clutter/{SNOW_CLUTTER_NAME}.prefab",
                $"Assets/Custom/VAitems/clutter/{SNOW_CLUTTER_NAME}.prefab",
                "assets/custom/vaitems/clutter/VA_InstancedSnow.prefab",
                $"{SNOW_CLUTTER_NAME}.prefab",
                SNOW_CLUTTER_NAME,
            };

            foreach (var path in searchPaths)
            {
                var prefab = bundle.LoadAsset<GameObject>(path);
                if (prefab == null) continue;

                var ir = prefab.GetComponent<InstanceRenderer>();
                if (ir == null)
                {
                    Debug.LogWarning($"[SnowClutterConfig] Bundle prefab at '{path}' has no InstanceRenderer");
                    continue;
                }

                if (ir.m_material == null)
                {
                    Debug.LogWarning($"[SnowClutterConfig] Bundle prefab at '{path}' InstanceRenderer has null m_material");
                    continue;
                }

                // CRITICAL: Validate m_mesh � InstanceRenderer.DrawMeshInstanced
                // silently draws nothing if m_mesh is null. This is the #1 reason
                // bundle-loaded clutter can be "painted" (logs show instances added)
                // but nothing is visible on screen.
                if (ir.m_mesh == null)
                {
                    Debug.LogWarning($"[SnowClutterConfig] Bundle prefab '{path}' has NULL m_mesh � borrowing from vanilla grass");
                    Mesh donorMesh = FindDonorGrassMesh();
                    if (donorMesh != null)
                    {
                        ir.m_mesh = donorMesh;
                        Debug.Log($"[SnowClutterConfig] Borrowed mesh '{donorMesh.name}' ({donorMesh.vertexCount} verts, {donorMesh.triangles.Length / 3} tris)");
                    }
                    else
                    {
                        Debug.LogError($"[SnowClutterConfig] Cannot find any donor mesh � snow will be INVISIBLE");
                    }
                }

                // Augment the mesh with fill caps so solid-white snow
                // doesn't look like a sponge through billboard gaps
                if (ir.m_mesh != null)
                {
                    ir.m_mesh = SnowGroundMesh.GetOrCreateAugmentedMesh(ir.m_mesh);
                }

                // Apply snow material overrides to the bundle material:
                // solid white texture, white color, no alpha cutout, no wind sway.
                // The bundle material may not have these set correctly.
                ir.m_material = CreateSnowMaterial(ir.m_material);

                _snowPrefab = prefab;
                _snowMaterial = ir.m_material;
                _loadedFromBundle = true;
                Object.DontDestroyOnLoad(_snowPrefab);

                // Register with ClutterSystem
                var snowClutter = new ClutterSystem.Clutter
                {
                    m_prefab = _snowPrefab,
                    m_enabled = false, // Paint-only: do not auto-spawn globally (causes massive lag)
                    m_instanced = true,
                    m_amount = 60,
                    m_biome = Heightmap.Biome.Mountain | Heightmap.Biome.DeepNorth,
                    m_scaleMin = 0.8f,
                    m_scaleMax = 1.5f,
                    m_maxTilt = 30f,
                    m_minAlt = -1000f,
                    m_maxAlt = 10000f,
                    m_snapToWater = false,
                    m_onUncleared = true,
                    m_onCleared = false,
                    m_terrainTilt = false,
                    m_randomOffset = 0f,
                    m_name = SNOW_CLUTTER_NAME,
                };

                ClutterSystem.instance.m_clutter.Add(snowClutter);

                Debug.Log($"[SnowClutterConfig] ? Loaded VA_InstancedSnow from bundle: {path}");
                Debug.Log($"[SnowClutterConfig]   Material: {ir.m_material.name}, Shader: {ir.m_material.shader.name}");
                Debug.Log($"[SnowClutterConfig]   Mesh: {(ir.m_mesh != null ? ir.m_mesh.name + " (" + ir.m_mesh.vertexCount + " verts)" : "NULL � WILL BE INVISIBLE")}");
                return true;
            }

            Debug.Log("[SnowClutterConfig] VA_InstancedSnow not found in asset bundle � will use runtime clone fallback");
            return false;
        }

        /// <summary>
        /// Finds a mesh from an existing vanilla instanced grass prefab.
        /// Used as a fallback when the bundle prefab has no mesh assigned.
        /// InstanceRenderer.m_mesh is a PUBLIC field on vanilla grass prefabs.
        /// </summary>
        private static Mesh FindDonorGrassMesh()
        {
            if (ClutterSystem.instance == null || ClutterSystem.instance.m_clutter == null)
                return null;

            // First pass: prefer meadows_grass_short (small, simple billboard quad)
            foreach (var c in ClutterSystem.instance.m_clutter)
            {
                if (c.m_prefab == null || !c.m_instanced) continue;
                string n = c.m_prefab.name.ToLower();
                if (!n.Contains("meadows_grass")) continue;

                var ir = c.m_prefab.GetComponent<InstanceRenderer>();
                if (ir != null && ir.m_mesh != null)
                {
                    Debug.Log($"[SnowClutterConfig] Found donor mesh from '{c.m_prefab.name}': {ir.m_mesh.name}");
                    return ir.m_mesh;
                }
            }

            // Second pass: any instanced clutter with a valid mesh
            foreach (var c in ClutterSystem.instance.m_clutter)
            {
                if (c.m_prefab == null || !c.m_instanced) continue;

                var ir = c.m_prefab.GetComponent<InstanceRenderer>();
                if (ir != null && ir.m_mesh != null)
                {
                    Debug.Log($"[SnowClutterConfig] Found donor mesh from '{c.m_prefab.name}': {ir.m_mesh.name}");
                    return ir.m_mesh;
                }
            }

            return null;
        }

        /// <summary>
        /// Applies snow-specific overrides to the material.
        /// 
        /// IMPORTANT: We preserve the bundle's textures (_MainTex, _TerrainColorTex, etc.)
        /// because Valheim's instanced grass shader samples _TerrainColorTex as a GLOBAL
        /// render target � per-material overrides to it are ignored. The bundle's textures
        /// were authored as snow in Unity and work correctly with the shader.
        /// 
        /// We only override: color ? white, alpha cutout ? disabled, wind sway ? off.
        /// </summary>
        private static Material CreateSnowMaterial(Material baseMat)
        {
            Material snowMat;
            if (baseMat != null)
            {
                snowMat = new Material(baseMat);
                snowMat.name = "SnowCover_Material";
                LogShaderProperties(snowMat);
            }
            else
            {
                snowMat = new Material(Shader.Find("Standard"));
                snowMat.name = "SnowCover_Fallback";
                Debug.LogWarning("[SnowClutterConfig] No donor material � using Standard shader fallback");
            }

            // ?? 1. PRESERVE BUNDLE TEXTURES ????????????????????????????????
            // Do NOT replace _MainTex or _TerrainColorTex � the bundle's textures
            // were authored as snow in Unity. The instanced grass shader samples
            // _TerrainColorTex as a global render target, so per-material overrides
            // are ignored anyway. Replacing _MainTex with a 4x4 white texture
            // loses the bundle's snow texture detail.

            // ?? 2. PURE WHITE COLOR ????????????????????????????????????????
            // Set color tint to white so the bundle texture renders at full brightness.
            Color snowColor = Color.white;
            if (snowMat.HasProperty("_Color"))
                snowMat.SetColor("_Color", snowColor);
            if (snowMat.HasProperty("_MainColor"))
                snowMat.SetColor("_MainColor", snowColor);
            if (snowMat.HasProperty("_BaseColor"))
                snowMat.SetColor("_BaseColor", snowColor);

            if (snowMat.HasProperty("_EmissionColor"))
                snowMat.SetColor("_EmissionColor", Color.black);
            if (snowMat.HasProperty("_SpecColor"))
                snowMat.SetColor("_SpecColor", Color.white);

            // ?? 3. DISABLE ALPHA CUTOUT ????????????????????????????????????
            // Snow is solid � it should NOT use alpha-test/cutout rendering.
            if (snowMat.HasProperty("_Cutoff"))
                snowMat.SetFloat("_Cutoff", 0f);
            if (snowMat.HasProperty("_AlphaCutoff"))
                snowMat.SetFloat("_AlphaCutoff", 0f);

            snowMat.DisableKeyword("_ALPHATEST_ON");
            snowMat.DisableKeyword("_ALPHABLEND_ON");
            snowMat.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            // ?? 3b. DISABLE BACKFACE CULLING & DEPTH WRITE ?????????????????
            // Snow particles are single-sided triangles packed into a volume.
            // Disabling culling lets them be visible from all angles without
            // needing a duplicate back-face (which was the main z-fight source).
            // Disabling ZWrite prevents remaining inter-particle z-fighting:
            // since all particles are solid white, depth ordering between them
            // doesn't matter visually � the result is the same white regardless
            // of which particle "wins". With ZWrite off, coplanar particles
            // simply blend/overwrite without flickering.
            if (snowMat.HasProperty("_Cull"))
                snowMat.SetFloat("_Cull", 0f); // 0 = Off (render both sides)
            if (snowMat.HasProperty("_ZWrite"))
                snowMat.SetFloat("_ZWrite", 0f);

            // Render after opaque geometry so terrain is already in the depth
            // buffer, but snow particles don't fight each other.
            snowMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry + 10;

            // ?? 4. DISABLE WIND SWAY ???????????????????????????????????????
            DisableWindSway(snowMat);

            Debug.Log($"[SnowClutterConfig] Created snow material: shader={snowMat.shader.name}, renderQueue={snowMat.renderQueue}");
            return snowMat;
        }

        /// <summary>
        /// Creates a solid bright-white texture with full alpha. This is used as
        /// the billboard texture so each instanced clutter quad renders as a solid
        /// white patch instead of a grass-blade-shaped cutout.
        /// </summary>
        private static Texture2D CreateSolidWhiteTexture()
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            tex.name = "SnowCover_SolidWhite";
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Repeat;

            Color snowPixel = Color.white;
            var pixels = new Color[16];
            for (int i = 0; i < 16; i++)
                pixels[i] = snowPixel;

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>
        /// Disables wind sway shader properties on the snow material.
        /// The vanilla instanced grass shader uses various properties for wind
        /// animation. We zero them all out so snow sits still. The player push
        /// (_PushForce / _PlayerPosition) is intentionally left untouched so
        /// snow still bends when walked through.
        /// </summary>
        private static void DisableWindSway(Material mat)
        {
            // Known wind/sway property names used by Valheim's instanced vegetation shaders
            string[] windProperties = {
                "_SwaySpeed",      // Speed of wind sway animation
                "_SwayDistance",   // How far instances sway
                "_SwayFactor",     // Overall sway multiplier
                "_WindSpeed",      // Wind speed input
                "_WindForce",      // Wind force multiplier
                "_WindDir",        // Wind direction
                "_SwayAmount",     // Alternative sway amount
                "_Sway",           // Generic sway toggle
                "_SwayFreq",       // Sway frequency
                "_Flex",           // Flex/bend amount
                "_RippleSpeed",    // Ripple animation speed
                "_RippleDistance", // Ripple distance
                "_PushForce",      // Player push effect � also disable for snow
            };

            int zeroed = 0;
            foreach (string prop in windProperties)
            {
                if (mat.HasProperty(prop))
                {
                    mat.SetFloat(prop, 0f);
                    zeroed++;
                }
            }

            // CRITICAL: Valheim's instanced grass shader reads wind from GLOBAL
            // shader properties and uses vertex Y position to compute sway amount.
            // Per-material properties alone cannot fully disable the vertex animation.
            // The real fix is in SnowGroundMesh.AugmentGrassMeshWithCaps() which
            // flattens all vertex Y positions to 0 � this makes the shader's
            // height-based sway calculation yield zero displacement.

            Debug.Log($"[SnowClutterConfig] Zeroed {zeroed} wind/sway properties");
        }

        /// <summary>
        /// Logs all properties on the material's shader for debugging.
        /// Helps identify which wind/sway/cutoff properties are available.
        /// </summary>
        private static void LogShaderProperties(Material mat)
        {
            if (mat == null || mat.shader == null) return;

            int propCount = mat.shader.GetPropertyCount();
            Debug.Log($"[SnowClutterConfig] Shader '{mat.shader.name}' has {propCount} properties:");

            for (int i = 0; i < propCount; i++)
            {
                string propName = mat.shader.GetPropertyName(i);
                var propType = mat.shader.GetPropertyType(i);

                string valueStr = "";
                switch (propType)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        valueStr = mat.GetFloat(propName).ToString("F3");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        valueStr = mat.GetColor(propName).ToString();
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var tex = mat.GetTexture(propName);
                        valueStr = tex != null ? tex.name : "(null)";
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        valueStr = mat.GetVector(propName).ToString();
                        break;
                }

                Debug.Log($"  [{propType}] {propName} = {valueStr}");
            }
        }

    }
}
