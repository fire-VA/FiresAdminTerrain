using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Loads the custom <c>Custom/FiresWater</c> shader from an asset bundle
    /// and provides a single chokepoint for wrapping vanilla water materials
    /// so they render two-sided with underwater tinting.
    ///
    /// HOW THE PIECES FIT:
    /// 1. <see cref="WaterMaterialManager.CaptureWaterMaterial"/> finds a real
    ///    vanilla water material at runtime (with all 33 shader properties
    ///    populated — depth tint, foam textures, wave velocity, refraction,
    ///    etc.).
    /// 2. <see cref="MaybeWrap"/> takes that captured material, instantiates a
    ///    new Material whose shader is <c>Custom/FiresWater</c> instead of
    ///    <c>Custom/Water</c>, and copies every matching property across. The
    ///    two shaders share property names by design, so the copy is exact.
    /// 3. <see cref="WindGlobalDriver"/> ticks once per frame and pushes
    ///    EnvMan's wind direction into the shader globals our vertex stage
    ///    reads. Without this the waves animate with default wind (+X axis)
    ///    instead of matching what the ocean is doing.
    ///
    /// WHY THIS IS OPT-IN:
    /// The shader is a fresh implementation that approximates vanilla's water
    /// look but is NOT a pixel-perfect drop-in. If <see cref="Enabled"/> is
    /// false, <see cref="MaybeWrap"/> returns the input unchanged and FAT
    /// water meshes render with the vanilla material (one-sided, no
    /// underwater tint) exactly as they do today. Flip Enabled to true once
    /// the bundle is built and shipped.
    ///
    /// BUNDLE WORKFLOW:
    /// The shader source lives at
    ///   Fires2026Project/Assets/Shaders/Fires/FiresWater.shader
    /// with its .meta assigned to bundle "fireswater". Build via the Unity
    /// menu "Valheim/Build AssetBundles" — output appears at
    ///   Fires2026Project/Assets/AssetBundles/fireswater
    /// Copy that file into this mod root as plain "fireswater" (no
    /// extension), and the csproj's
    ///   &lt;EmbeddedResource Include="fireswater" /&gt;
    /// entry compiles it into the DLL alongside the existing "firesnpcs"
    /// bundle. Loader picks it up at <see cref="EnsureLoaded"/> time.
    /// </summary>
    public static class FiresWaterShaderLoader
    {
        // Flip to true once the bundle ships. Until then this is a no-op so
        // shipping the .cs file alone doesn't change runtime behavior.
        public static bool Enabled = false;

        // Independent secondary toggle: when true, swap the Custom/Water
        // shader on every vanilla WaterVolume's m_waterSurface material to
        // Custom/FiresWater. The shader keeps every vanilla property name
        // so the swap is value-preserving — wave time, depth, foam textures,
        // colors, all transfer because they're already SET on the material.
        // We just change the shader binding that interprets them.
        //
        // Default false because this affects every ocean in the world. Flip
        // on after testing the FAT-only path looks right. Use
        // ApplyToAllVanillaWater() to apply immediately after flipping, or
        // wait for the Harmony hook on WaterVolume.OnEnable to catch new
        // volumes as they stream in.
        public static bool SwapVanillaOcean = false;

        public const string ShaderName = "Custom/FiresWater";
        public const string BundleResourceName = "fireswater";

        // Original shader cache so RestoreVanillaWater() can put things back.
        // Keyed by Material reference; each entry records the shader that
        // material had before we swapped. Volatile — if the user calls
        // RefreshMaterials() in WaterMaterialManager during the swapped
        // state, this cache becomes stale (those materials may have been
        // released). RestoreVanillaWater handles destroyed-material entries
        // by skipping them silently.
        private static readonly Dictionary<Material, Shader> s_swappedMaterials
            = new Dictionary<Material, Shader>();

        private static bool s_attemptedLoad = false;
        private static AssetBundle s_bundle;
        private static Shader s_shader;

        // Exposed so sibling shaders shipped in the same bundle (e.g.
        // Custom/FiresAurora) can pull their assets without re-loading the
        // bundle. Null until EnsureLoaded has run.
        public static AssetBundle Bundle => s_bundle;

        // Shader property IDs cached on first use. Mirrors the constants in
        // WaterVolume.cs so the names stay in sync.
        private static readonly int Prop_WaterTime  = Shader.PropertyToID("_WaterTime");
        private static readonly int Prop_Depth      = Shader.PropertyToID("_depth");
        private static readonly int Prop_GlobalWind = Shader.PropertyToID("_UseGlobalWind");

        private static readonly int Prop_FiresWind1      = Shader.PropertyToID("_FiresGlobalWind1");
        private static readonly int Prop_FiresWind2      = Shader.PropertyToID("_FiresGlobalWind2");
        private static readonly int Prop_FiresWindAlpha  = Shader.PropertyToID("_FiresGlobalWindAlpha");

        /// <summary>
        /// Resolve the custom shader. Idempotent — first call attempts the
        /// load, subsequent calls return the cached result. Returns null if
        /// the bundle is missing or the shader didn't compile on this Unity
        /// version; callers must handle that gracefully (typically by falling
        /// through to the vanilla material).
        /// </summary>
        public static Shader EnsureLoaded()
        {
            if (s_shader != null) return s_shader;
            if (s_attemptedLoad) return null;
            s_attemptedLoad = true;

            try
            {
                // Already loaded by someone else? Use it.
                s_shader = Shader.Find(ShaderName);
                if (s_shader != null)
                {
                    Debug.Log($"[FiresWaterShader] Found '{ShaderName}' already registered.");
                    return s_shader;
                }

                s_bundle = LoadBundleFromEmbeddedResource(BundleResourceName);
                if (s_bundle == null)
                {
                    Debug.LogWarning(
                        $"[FiresWaterShader] Embedded bundle '{BundleResourceName}' not found. " +
                        $"Build it in Unity ('Valheim/Build AssetBundles') and copy " +
                        $"AssetBundles/{BundleResourceName} into the mod root. " +
                        $"Falling back to vanilla water material.");
                    return null;
                }

                // Try the well-known name first, then walk all shaders in the
                // bundle and grab whichever one matches Custom/FiresWater.
                s_shader = s_bundle.LoadAsset<Shader>(ShaderName);
                if (s_shader == null)
                {
                    foreach (var s in s_bundle.LoadAllAssets<Shader>())
                    {
                        if (s != null && s.name == ShaderName) { s_shader = s; break; }
                    }
                }

                if (s_shader == null)
                {
                    Debug.LogError(
                        $"[FiresWaterShader] Bundle loaded but did not contain a shader " +
                        $"named '{ShaderName}'. Check the .shader file is assigned to " +
                        $"bundle '{BundleResourceName}' in Unity and rebuild.");
                    return null;
                }

                if (!s_shader.isSupported)
                {
                    Debug.LogError(
                        $"[FiresWaterShader] '{ShaderName}' compiled but is reported " +
                        $"unsupported on this hardware/platform. Will not be used.");
                    s_shader = null;
                    return null;
                }

                Debug.Log($"[FiresWaterShader] Loaded shader '{ShaderName}' from bundle.");
                return s_shader;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresWaterShader] EnsureLoaded threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// If the shader is enabled and loaded, return a NEW Material with our
        /// shader bound and every matching property value copied from
        /// <paramref name="source"/>. Otherwise return <paramref name="source"/>
        /// unchanged.
        ///
        /// Property copy strategy: we walk every property on the source
        /// shader and re-set it on the wrapped material using
        /// HasProperty + GetX/SetX. This preserves the captured ocean's
        /// _ColorTop, _ColorBottom, _FoamTex, normal maps, etc., so the
        /// wrapped material looks like vanilla water rather than the
        /// shader's defaults.
        /// </summary>
        public static Material MaybeWrap(Material source)
        {
            if (!Enabled || source == null) return source;
            var shader = EnsureLoaded();
            if (shader == null) return source;
            // Already wrapped? Don't double-wrap.
            if (source.shader != null && source.shader.name == ShaderName) return source;

            try
            {
                var wrapped = new Material(shader);
                wrapped.name = (source.name ?? "WrappedWater") + "_Fires";
                CopyMatchingProperties(source, wrapped);

                // Make sure the per-volume state vanilla water depends on
                // carries across. WaterVolume.SetupMaterial sets these on
                // the original; if FAT cloned the material before SetupMaterial
                // ran they may be at defaults — set sane fallbacks so waves
                // still animate.
                if (!wrapped.HasProperty(Prop_GlobalWind) || wrapped.GetFloat(Prop_GlobalWind) < 0.5f)
                    wrapped.SetFloat(Prop_GlobalWind, 1f);
                if (wrapped.HasProperty(Prop_Depth))
                {
                    var src = source.HasProperty(Prop_Depth) ? source.GetFloatArray(Prop_Depth) : null;
                    wrapped.SetFloatArray(Prop_Depth, src != null && src.Length == 4
                        ? src
                        : new float[] { 1f, 1f, 1f, 1f });
                }

                return wrapped;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresWaterShader] MaybeWrap failed, returning source: {ex}");
                return source;
            }
        }

        /// <summary>
        /// Override the underwater tint color on a wrapped material. Cheap to
        /// call; intended for per-biome / per-liquid-type tweaks (e.g. Ashlands
        /// lava is a different underwater color than Mistlands cold water).
        /// </summary>
        public static void SetUnderwaterTint(Material wrapped, Color tint, float strength = 1.5f, float depthFade = 6f)
        {
            if (wrapped == null || wrapped.shader == null || wrapped.shader.name != ShaderName) return;
            if (wrapped.HasProperty("_UnderwaterTint"))           wrapped.SetColor("_UnderwaterTint", tint);
            if (wrapped.HasProperty("_UnderwaterTintStrength"))   wrapped.SetFloat("_UnderwaterTintStrength", strength);
            if (wrapped.HasProperty("_UnderwaterDepthFade"))      wrapped.SetFloat("_UnderwaterDepthFade", depthFade);
        }

        /// <summary>
        /// Swap the shader on every currently-loaded vanilla water volume's
        /// surface material to <c>Custom/FiresWater</c>. Idempotent — once
        /// swapped, future calls skip already-swapped materials. Records
        /// originals so <see cref="RestoreVanillaWater"/> can revert.
        ///
        /// Property values are preserved because we're mutating the SHADER
        /// binding on the existing material, not creating a new material.
        /// Our shader declares every name the vanilla shader has, so they
        /// all interpret correctly the moment the swap lands.
        ///
        /// Safe to call from anywhere — gated on <see cref="SwapVanillaOcean"/>
        /// and <see cref="Enabled"/>; both must be true.
        /// </summary>
        public static int ApplyToAllVanillaWater()
        {
            if (!Enabled || !SwapVanillaOcean) return 0;
            var shader = EnsureLoaded();
            if (shader == null) return 0;

            int swapped = 0;
            if (WaterVolume.Instances != null)
            {
                foreach (var wv in WaterVolume.Instances)
                {
                    if (TrySwapOne(wv, shader)) swapped++;
                }
            }
            // ZNetScene prefabs hold the prototype materials before any
            // volume streams in. Scanning them too means cold-boot worlds
            // get swapped before the first WaterVolume.OnEnable fires.
            if (ZNetScene.instance != null && ZNetScene.instance.m_prefabs != null)
            {
                foreach (var prefab in ZNetScene.instance.m_prefabs)
                {
                    if (prefab == null) continue;
                    var wv = prefab.GetComponent<WaterVolume>();
                    if (wv == null) wv = prefab.GetComponentInChildren<WaterVolume>(true);
                    if (TrySwapOne(wv, shader)) swapped++;
                }
            }

            // water_lod (vanilla far-ocean LOD plane) is INTENTIONALLY left
            // untouched. Every attempt to bring it into our shader pipeline
            // (broad Custom/Water swap, narrow water_lod swap, property
            // copy, Y-offset push-down, _Tess gate, FollowPlayer Y-clamp)
            // produced visible artifacts (floating plane, double-layer
            // water, bright cross seams, wave-displaced LOD). Final decision
            // 2026-05-31: accept the visible boundary mismatch at the
            // load-radius edge as the lesser evil. OceanZoneRingExtender
            // mitigates this at sea by bumping ZoneSystem.m_activeArea
            // when over deep water so the boundary sits further out.

            if (swapped > 0)
            {
                Debug.Log($"[FiresWaterShader] Swapped {swapped} vanilla water material(s) to '{ShaderName}'.");
            }
            return swapped;
        }

        /// <summary>
        /// Restore every swapped vanilla water material back to its original
        /// shader. After this returns the cache is empty — the next
        /// <see cref="ApplyToAllVanillaWater"/> call (if SwapVanillaOcean
        /// is still true) will rebuild it.
        /// </summary>
        public static int RestoreVanillaWater()
        {
            int restored = 0;
            foreach (var kv in s_swappedMaterials)
            {
                if (kv.Key == null || kv.Value == null) continue;
                try { kv.Key.shader = kv.Value; restored++; } catch { }
            }
            s_swappedMaterials.Clear();
            if (restored > 0)
            {
                Debug.Log($"[FiresWaterShader] Restored {restored} vanilla water material(s).");
            }
            return restored;
        }

        // Public entry point for the Harmony hook (which lives in a sibling
        // class and can't see privates). Delegates to TrySwapOne with the
        // already-loaded shader the caller resolved via EnsureLoaded.
        public static bool TrySwapOnePublic(WaterVolume wv, Shader target)
            => TrySwapOne(wv, target);

        // Single-volume swap, called by both the bulk scan above and the
        // Harmony hook below as new WaterVolumes stream in mid-game.
        private static bool TrySwapOne(WaterVolume wv, Shader target)
        {
            if (wv == null || wv.m_waterSurface == null) return false;
            var mat = wv.m_waterSurface.sharedMaterial;
            if (mat == null || target == null) return false;
            if (mat.shader == target)
            {
                // Already swapped on a prior pass — but new volumes share
                // the prefab material and need their _VolumeSize pushed
                // through too. Tag the volume so we don't re-tag it next
                // frame.
                ApplyPerVolumeProperties(wv, mat);
                return false;
            }
            if (s_swappedMaterials.ContainsKey(mat))
            {
                ApplyPerVolumeProperties(wv, mat);
                return false;
            }

            try
            {
                s_swappedMaterials[mat] = mat.shader;
                mat.shader = target;
                ApplyPerVolumeProperties(wv, mat);
                // Bake per-vertex distance-to-land into mesh vertex colors
                // so wave amplitude attenuates near shore without the
                // floating-point drift that plagues _depth[4] bilinear at
                // zone boundaries. Idempotent — re-bakes only if the volume
                // hasn't been baked yet for its current position.
                WaterMeshShoreBake.Apply(wv);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresWaterShader] TrySwapOne failed on '{mat.name}': {ex.Message}");
                s_swappedMaterials.Remove(mat);
                return false;
            }
        }

        // Push per-volume properties that the shader needs but vanilla never
        // set on the material. The big one is _VolumeSize — without it
        // SampleDepth's bilinear interp uses a default 64m extent, which is
        // correct for vanilla ocean zones but wrong for any custom volume.
        //
        // Reads the volume's collider bounds (same source vanilla
        // WaterVolume.Depth uses internally). On rare volumes without a
        // collider we leave _VolumeSize at its default — visible seam there
        // is better than a divide-by-zero.
        private static void ApplyPerVolumeProperties(WaterVolume wv, Material mat)
        {
            if (wv == null || mat == null) return;
            try
            {
                var col = wv.GetComponent<Collider>();
                if (col != null)
                {
                    float sizeX = col.bounds.size.x;
                    if (sizeX > 0.001f && mat.HasProperty("_VolumeSize"))
                        mat.SetFloat("_VolumeSize", sizeX);
                }

                // _FadeBandMeters write removed 2026-05-30 — the shader no
                // longer reads it. Zone-boundary overlap is now prevented
                // by the dual-purpose stencil block (bit 4 claim + bit 1
                // WaterMask read in one Comp Equal + Pass Invert config),
                // not by an alpha-side UV-edge taper. The property is
                // still declared on the shader for material-capture
                // compatibility but is a no-op.
            }
            catch { /* property push failures aren't fatal */ }
        }

        // ---- Internals --------------------------------------------------------

        private static AssetBundle LoadBundleFromEmbeddedResource(string name)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return null;

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                return AssetBundle.LoadFromStream(stream);
            }
        }

        private static void CopyMatchingProperties(Material src, Material dst)
        {
            if (src == null || dst == null || src.shader == null) return;

            int count = src.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string pname = src.shader.GetPropertyName(i);
                if (!dst.HasProperty(pname)) continue;
                var ptype = src.shader.GetPropertyType(i);

                try
                {
                    switch (ptype)
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            dst.SetColor(pname, src.GetColor(pname));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:
                            dst.SetVector(pname, src.GetVector(pname));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            dst.SetFloat(pname, src.GetFloat(pname));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            var tex = src.GetTexture(pname);
                            if (tex != null) dst.SetTexture(pname, tex);
                            var ofs = src.GetTextureOffset(pname);
                            var sca = src.GetTextureScale(pname);
                            dst.SetTextureOffset(pname, ofs);
                            dst.SetTextureScale(pname, sca);
                            break;
                    }
                }
                catch
                {
                    // A property the source claims to have but actually can't
                    // be read (rare; happens on stripped builds). Skip — the
                    // wrapped material will use the shader default for it.
                }
            }

            // _depth is a float array — special case, GetPropertyCount lists
            // it as Vector type which doesn't round-trip via SetVector.
            if (src.HasProperty(Prop_Depth) && dst.HasProperty(Prop_Depth))
            {
                var arr = src.GetFloatArray(Prop_Depth);
                if (arr != null && arr.Length >= 4) dst.SetFloatArray(Prop_Depth, arr);
            }
        }
    }

    /// <summary>
    /// Per-frame driver that mirrors WaterVolume's static wind state into the
    /// shader globals our vertex stage reads. Reflects EnvMan.GetWindData
    /// each frame so the wave animation matches vanilla ocean direction.
    ///
    /// Attach exactly one instance somewhere persistent (the plugin root
    /// GameObject). It hooks itself into Update; if the game scene tears
    /// down it cleans up automatically.
    /// </summary>
    public class WindGlobalDriver : MonoBehaviour
    {
        private static readonly int Prop_FiresWind1     = Shader.PropertyToID("_FiresGlobalWind1");
        private static readonly int Prop_FiresWind2     = Shader.PropertyToID("_FiresGlobalWind2");
        private static readonly int Prop_FiresWindAlpha = Shader.PropertyToID("_FiresGlobalWindAlpha");
        private static readonly int Prop_WaterTime      = Shader.PropertyToID("_WaterTime");

        private void Update()
        {
            if (!FiresWaterShaderLoader.Enabled) return;
            var env = EnvMan.instance;
            if (env == null) return;

            // GetWindData returns (wind1, wind2, alpha) — same outputs vanilla
            // WaterVolume.StaticUpdate caches statically for its CPU CalcWave.
            // We push them as shader globals so our GPU shader sees the same
            // values and the surface stays welded with whatever vanilla is
            // doing for its own ocean.
            Vector4 w1, w2; float a;
            try
            {
                env.GetWindData(out w1, out w2, out a);
            }
            catch
            {
                // Defensive: GetWindData signature has shifted between game
                // builds. If reflection fails, hold steady at last value
                // instead of spamming logs.
                return;
            }

            Shader.SetGlobalVector(Prop_FiresWind1, w1);
            Shader.SetGlobalVector(Prop_FiresWind2, w2);
            Shader.SetGlobalFloat(Prop_FiresWindAlpha, a);

            // _WaterTime: vanilla WaterVolume.UpdateMaterials sets this per
            // material per frame, but FAT water meshes are NOT WaterVolume
            // instances so nobody ticks it for them. Push as a shader global
            // so any wrapped material reads the right value. Using the same
            // wrapped-day-time source vanilla converges toward in its lerp
            // (see WaterVolume.UpdateWaterTime) — close enough for visual
            // sync, no need to replicate the interpolation exactly.
            if (ZNet.instance != null)
            {
                Shader.SetGlobalFloat(Prop_WaterTime, ZNet.instance.GetWrappedDayTimeSeconds());
            }
        }
    }

    /// <summary>
    /// Harmony patch: whenever a WaterVolume becomes active in the scene,
    /// give the loader a chance to swap its surface material's shader to
    /// Custom/FiresWater. Self-gated on <see cref="FiresWaterShaderLoader.SwapVanillaOcean"/>
    /// inside the swap method, so when the toggle is off this postfix
    /// degenerates into a single early-return.
    ///
    /// Patches OnEnable specifically (not Start) because OnEnable fires
    /// every time a volume is re-activated after disable, which matters
    /// when the player re-streams a previously-loaded zone — the material
    /// reference may have been swapped on a different volume's shared
    /// material since, but TrySwapOne dedupes via the cache so the no-op
    /// case is cheap.
    /// </summary>
    [HarmonyPatch(typeof(WaterVolume), "OnEnable")]
    internal static class WaterVolume_OnEnable_FiresSwap
    {
        [HarmonyPostfix]
        static void Postfix(WaterVolume __instance)
        {
            if (!FiresWaterShaderLoader.Enabled) return;
            if (!FiresWaterShaderLoader.SwapVanillaOcean) return;
            try
            {
                var shader = FiresWaterShaderLoader.EnsureLoaded();
                if (shader != null) FiresWaterShaderLoader.TrySwapOnePublic(__instance, shader);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresWaterShader] WaterVolume.OnEnable postfix failed: {ex.Message}");
            }
        }
    }
}
