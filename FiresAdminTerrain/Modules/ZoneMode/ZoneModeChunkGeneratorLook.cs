using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // The Chunk Generator's body and dressing (Tools\CHUNK_GENERATOR.md, "The look"). The bake (FiresZonesBuilder with
    // FiresChunkGeneratorLook) strips every IronGate mesh, material, sound and texture off the prefab, so the bundle
    // carries none. At registration the body takes each back from the live vanilla smelter, matched by child path and
    // type, and its two stone materials are tinted grey again; each dressing anchor under "dress" takes its mesh and
    // materials from the live prefab named in Bindings. Every machine's glow, lights and fire follow its recipe through
    // shared per-recipe material copies (the plain recipe's cycle through every biome's colour), and the floating core
    // turns under one shared driver that skips machines out of sight range.
    internal static class ZoneModeChunkGeneratorLook
    {
        private const string BodySource = "smelter";
        private static readonly string[] TintedBodyMaterials = { "smeltermat", "smeltermat_worn" };
        private static readonly Color BodyTint = new Color(0.62f, 0.62f, 0.60f);
        private const BindingFlags FieldScope = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal const string DressRoot = "dress";
        internal const string EmissionColor = "_EmissionColor";
        private const float GlowStrength = 1.6f;
        private const float SpinDegreesPerSecond = 24f;
        private const float SpinRange = 40f;
        // The plain recipe makes seeds of no one biome, so its glow walks every biome's colour in turn (Fire, 2026-09-28:
        // "multicolor, pulsing gently in and out of different colors"): each colour swells and fades over one period, and
        // blends into the next through the dim half.
        private const float CycleSecondsPerColour = 3.5f;
        private const float CycleDimBrightness = 0.45f;
        private const float CycleBlendStart = 0.55f;

        private sealed class Binding
        {
            internal readonly string Anchor;
            internal readonly string Source;
            internal readonly string Path;
            internal readonly bool TakesMesh;
            internal readonly bool Spins;
            internal readonly bool RuneMask;

            internal Binding(string anchor, string source, string path, bool takesMesh, bool spins = false, bool runeMask = false)
            {
                Anchor = anchor;
                Source = source;
                Path = path;
                TakesMesh = takesMesh;
                Spins = spins;
                RuneMask = runeMask;
            }
        }

        // Anchor name prefix -> live source prefab and the child path of its renderer (the paths the editor's survey
        // logs as "source ...: '<path>'"). The rune halo is our own ribbon and cutout material; it borrows only the rune
        // tile's glow map, turned into a runes-only mask (Fire, 2026-09-27: just the runes floating, no ring).
        private static readonly Binding[] Bindings =
        {
            new Binding("RuneTile", "LastBossGate_RuneTile", "attach/model", true),
            new Binding("RuneHalo", "LastBossGate_RuneTile", "attach/model", false, spins: true, runeMask: true),
            new Binding("FrostCore", "FrostCore", "default", true, spins: true),
            new Binding("BlackIce", "BlackIce_Core", "model", true),
        };

        // The rune columns of the Frozen King gate's sheet the halo wraps (the same rect the editor bakes its UVs for),
        // read into a mask whose alpha is the runes' glow: the halo's cutout draws only the runes.
        private static readonly Rect RunePatch = new Rect(0.55f, 0.06f, 0.36f, 0.31f);
        private const int MaskWidth = 1024;
        private const int MaskHeight = 896;
        internal const string MainTex = "_MainTex";
        private const string AlbedoColor = "_Color";
        // The halo's runes are a pure white mask, so they take the recipe colour as their albedo and a gentle glow; at the
        // rune tiles' strength they wash out to white.
        private const float HaloGlowStrength = 1f;
        private const string EmissionMap = "_EmissionMap";
        private const string MaskName = "fires_chunkgenerator_runes";
        private static Texture2D s_runeMask;
        private static bool s_maskTried;

        internal static Texture2D RuneMask => s_runeMask;

        // The fire tints to the recipe too, the plain recipe's through its colour cycle. Smoke is never tinted.
        private static readonly string[] FireParts = { "flame", "flare" };
        private const string CyclingRecipe = "Plain";

        // The colours the plain recipe's glow walks, in progression order.
        private static readonly string[] CycleOrder =
            { "Meadows", "BlackForest", "Swamp", "Mountain", "Plains", "Mistlands", "Ashlands", "DeepNorth" };

        private static readonly Dictionary<string, Color> RecipeGlow = new Dictionary<string, Color>
        {
            { "Plain", new Color(0.92f, 0.94f, 1.00f) },
            { "Meadows", new Color(0.55f, 1.00f, 0.30f) },
            { "BlackForest", new Color(0.15f, 0.90f, 0.60f) },
            { "Swamp", new Color(0.46f, 0.62f, 0.22f) },
            { "Mountain", new Color(0.55f, 0.85f, 1.00f) },
            { "Plains", new Color(1.00f, 0.72f, 0.16f) },
            { "Mistlands", new Color(0.72f, 0.38f, 1.00f) },
            { "Ashlands", new Color(1.00f, 0.34f, 0.10f) },
            { "DeepNorth", new Color(0.38f, 0.70f, 1.00f) },
        };

        private static readonly HashSet<string> s_reported = new HashSet<string>();
        private static readonly Dictionary<Material, Material> s_tinted = new Dictionary<Material, Material>();
        private static readonly Dictionary<Material, Dictionary<string, Material>> s_recipeMaterials = new Dictionary<Material, Dictionary<string, Material>>();
        private static readonly Dictionary<Material, Material> s_recipeSource = new Dictionary<Material, Material>();
        private static readonly List<KeyValuePair<Material, bool>> s_cyclingMaterials = new List<KeyValuePair<Material, bool>>();

        // Binds the prefab's body and anchors once per prefab load; a later call finds them bound and does nothing.
        internal static void Dress(GameObject machine)
        {
            if (ZNetScene.instance == null) return;
            // R51: "raven + look" was 46 ms on the bot (12 on the dedi); these laps say which part.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            BindBody(machine);
            long bodyMs = clock.ElapsedMilliseconds;
            var root = machine.transform.Find(DressRoot);
            if (root == null) return;
            int bound = 0, missing = 0;
            long maskMs = 0;
            var dressed = new List<Renderer>();
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var binding = BindingFor(AnchorOf(renderer));
                if (binding == null) continue;
                dressed.Add(renderer);
                if (binding.RuneMask)
                {
                    long before = clock.ElapsedMilliseconds;
                    if (EnsureRuneMask(binding)) bound++; else missing++;
                    maskMs += clock.ElapsedMilliseconds - before;
                    continue;
                }
                var filter = renderer.GetComponent<MeshFilter>();
                if (renderer.sharedMaterials.Length > 0 && renderer.sharedMaterial != null && (!binding.TakesMesh || filter.sharedMesh != null)) continue;
                if (!TryGetSource(binding, out var source))
                {
                    missing++;
                    continue;
                }
                renderer.sharedMaterials = source.sharedMaterials;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (binding.TakesMesh && filter != null) filter.sharedMesh = source.GetComponent<MeshFilter>().sharedMesh;
                bound++;
            }
            long dressMs = clock.ElapsedMilliseconds - bodyMs;
            AddToFirstLod(machine, dressed);
            long lodMs = clock.ElapsedMilliseconds - bodyMs - dressMs;
            if (bound > 0 || missing > 0)
                FiresLogger.LogInfo($"[FiresZones] Chunk Generator dressing: {bound} piece(s) bound from the live game"
                    + (missing > 0 ? $", {missing} left bare (source missing; see the warning above)" : "")
                    + $"; body {bodyMs} ms, dress {dressMs} ms (rune mask {maskMs} ms), lod {lodMs} ms.");
        }

        // Every empty slot on the body (everything outside "dress") takes the live smelter's value at the same child path:
        // meshes, collider meshes, renderer and particle materials, particle meshes, sounds, light cookies, and asset
        // fields on vanilla components (ZSFX clips). A slot the bake left filled is ours and is kept.
        private static void BindBody(GameObject machine)
        {
            var source = ZNetScene.instance.GetPrefab(BodySource);
            if (source == null)
            {
                if (s_reported.Add(BodySource))
                    FiresLogger.LogWarning($"[FiresZones] Chunk Generator body: no live '{BodySource}' prefab to take its looks from; it stays bare.");
                return;
            }
            var dress = machine.transform.Find(DressRoot);
            int bound = 0, unmatched = 0;
            foreach (var component in machine.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || (dress != null && component.transform.IsChildOf(dress))) continue;
                if (!NeedsBinding(component)) continue;
                var twin = TwinOf(source.transform, machine.transform, component);
                if (twin == null) { unmatched++; continue; }
                bound += BindFrom(twin, component);
            }
            if (bound > 0 || unmatched > 0)
                FiresLogger.LogInfo($"[FiresZones] Chunk Generator body: {bound} slot(s) bound from the live {BodySource}"
                    + (unmatched > 0 ? $", {unmatched} component(s) with no twin there left bare." : "."));
        }

        private static bool NeedsBinding(Component component)
        {
            switch (component)
            {
                case MeshFilter filter: return filter.sharedMesh == null;
                case MeshCollider collider: return collider.sharedMesh == null;
                case ParticleSystemRenderer particles: return Empty(particles.sharedMaterials) || particles.trailMaterial == null;
                case Renderer renderer: return Empty(renderer.sharedMaterials);
                case AudioSource audio: return audio.clip == null;
                case Light light: return light.cookie == null;
                case ParticleSystem system: return system.shape.mesh == null;
                case MonoBehaviour behaviour: return AssetFields(behaviour.GetType()).Exists(f => IsEmpty(f.GetValue(behaviour)));
                default: return false;
            }
        }

        private static int BindFrom(Component twin, Component ours)
        {
            int bound = 0;
            switch (ours)
            {
                case MeshFilter filter:
                    filter.sharedMesh = ((MeshFilter)twin).sharedMesh;
                    return filter.sharedMesh != null ? 1 : 0;
                case MeshCollider collider:
                    collider.sharedMesh = ((MeshCollider)twin).sharedMesh;
                    return collider.sharedMesh != null ? 1 : 0;
                case Renderer renderer:
                    var live = (Renderer)twin;
                    if (Empty(renderer.sharedMaterials) && !Empty(live.sharedMaterials))
                    {
                        renderer.sharedMaterials = Tinted(live.sharedMaterials);
                        bound++;
                    }
                    if (renderer is ParticleSystemRenderer particles && live is ParticleSystemRenderer liveParticles)
                    {
                        if (particles.trailMaterial == null && liveParticles.trailMaterial != null) { particles.trailMaterial = liveParticles.trailMaterial; bound++; }
                        if (particles.mesh == null && liveParticles.mesh != null) { particles.mesh = liveParticles.mesh; bound++; }
                    }
                    return bound;
                case AudioSource audio:
                    audio.clip = ((AudioSource)twin).clip;
                    return audio.clip != null ? 1 : 0;
                case Light light:
                    light.cookie = ((Light)twin).cookie;
                    return light.cookie != null ? 1 : 0;
                case ParticleSystem system:
                    var shape = system.shape;
                    shape.mesh = ((ParticleSystem)twin).shape.mesh;
                    return shape.mesh != null ? 1 : 0;
                case MonoBehaviour behaviour:
                    foreach (var field in AssetFields(behaviour.GetType()))
                    {
                        if (!IsEmpty(field.GetValue(behaviour))) continue;
                        var value = field.GetValue(twin);
                        if (IsEmpty(value)) continue;
                        field.SetValue(behaviour, value);
                        bound++;
                    }
                    return bound;
                default:
                    return 0;
            }
        }

        // The smelter's stone keeps the grey the plain generator has worn since 2026-09-27; one tinted copy per material.
        private static Material[] Tinted(Material[] live)
        {
            var materials = new Material[live.Length];
            for (int i = 0; i < live.Length; i++)
            {
                var material = live[i];
                if (material == null || Array.IndexOf(TintedBodyMaterials, material.name) < 0) { materials[i] = material; continue; }
                if (!s_tinted.TryGetValue(material, out var copy) || copy == null)
                {
                    copy = new Material(material) { name = material.name + "_chunkgenerator" };
                    copy.color = material.color * BodyTint;
                    s_tinted[material] = copy;
                }
                materials[i] = copy;
            }
            return materials;
        }

        private static readonly Dictionary<Type, List<FieldInfo>> s_assetFields = new Dictionary<Type, List<FieldInfo>>();

        // Serialized fields holding meshes, materials, textures, sprites or sounds (or arrays of them).
        private static List<FieldInfo> AssetFields(Type type)
        {
            if (s_assetFields.TryGetValue(type, out var fields)) return fields;
            fields = new List<FieldInfo>();
            foreach (var field in type.GetFields(FieldScope))
            {
                if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null) continue;
                var element = field.FieldType.IsArray ? field.FieldType.GetElementType() : field.FieldType;
                if (typeof(Mesh).IsAssignableFrom(element) || typeof(Material).IsAssignableFrom(element) || typeof(Texture).IsAssignableFrom(element)
                    || typeof(Sprite).IsAssignableFrom(element) || typeof(AudioClip).IsAssignableFrom(element))
                    fields.Add(field);
            }
            return s_assetFields[type] = fields;
        }

        private static bool IsEmpty(object value)
        {
            if (value is UnityEngine.Object asset) return asset == null;
            if (value is IList list)
            {
                if (list.Count == 0) return true;
                foreach (var item in list) if (item == null || item is UnityEngine.Object element && element == null) return true;
                return false;
            }
            return value == null;
        }

        private static bool Empty(Material[] materials) => IsEmpty(materials);

        private static Component TwinOf(Transform sourceRoot, Transform ourRoot, Component component)
        {
            var node = component.transform == ourRoot ? sourceRoot : sourceRoot.Find(PathFrom(ourRoot, component.transform));
            return node != null ? node.GetComponent(component.GetType()) : null;
        }

        private static string PathFrom(Transform root, Transform node)
        {
            string path = node.name;
            for (var parent = node.parent; parent != null && parent != root; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }

        internal static void OnAwake(Smelter smelter, ChunkTier recipe)
        {
            var glow = smelter.GetComponent<GeneratorGlow>();
            if (glow == null) glow = smelter.gameObject.AddComponent<GeneratorGlow>();
            glow.Bind(smelter.transform.Find(DressRoot));
            Apply(glow, recipe);
        }

        internal static void Refresh(Smelter smelter, ChunkTier recipe)
        {
            var glow = smelter.GetComponent<GeneratorGlow>();
            if (glow != null && glow.Recipe != recipe.Id) Apply(glow, recipe);
        }

        // Shared per-recipe material copies, not property blocks: vanilla's MaterialMan rewrites the block of every renderer
        // under a piece on a hammer highlight, snow or Ashlands wear, which wiped the runes and left a white band.
        private static void Apply(GeneratorGlow glow, ChunkTier recipe)
        {
            glow.Recipe = recipe.Id;
            glow.Cycles = recipe.Id == CyclingRecipe;
            if (glow.Cycles) LookDriver.Add(glow);
            if (!RecipeGlow.TryGetValue(recipe.Id, out var color)) color = RecipeGlow[CyclingRecipe];
            for (int i = 0; i < glow.Renderers.Count; i++)
            {
                var renderer = glow.Renderers[i];
                if (renderer == null) continue;
                bool halo = glow.Halos.Contains(renderer);
                if (halo && s_runeMask == null)
                {
                    renderer.enabled = false;
                    if (s_maskPending) s_awaitingMask.Add(glow);
                    continue;
                }
                if (halo) renderer.enabled = true;
                var originals = glow.Originals[i];
                var materials = new Material[originals.Length];
                for (int m = 0; m < originals.Length; m++) materials[m] = RecipeMaterial(originals[m], recipe.Id, color, halo);
                renderer.sharedMaterials = materials;
            }
            if (!glow.Cycles) TintFireAndLights(glow, color);
        }

        private static void TintFireAndLights(GeneratorGlow glow, Color color)
        {
            foreach (var light in glow.Lights)
                if (light != null) light.color = color;
            foreach (var fire in glow.Fires)
            {
                if (fire == null) continue;
                var main = fire.main;
                main.startColor = new ParticleSystem.MinMaxGradient(color);
            }
        }

        // Where the plain recipe's glow is at `time`: the colour of the moment, and how bright it is in its swell.
        private static void CycleAt(float time, out Color color, out float brightness)
        {
            float step = time / CycleSecondsPerColour;
            int index = Mathf.FloorToInt(step);
            float phase = step - index;
            var current = RecipeGlow[CycleOrder[Mod(index, CycleOrder.Length)]];
            var next = RecipeGlow[CycleOrder[Mod(index + 1, CycleOrder.Length)]];
            color = Color.Lerp(current, next, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CycleBlendStart, 1f, phase)));
            brightness = Mathf.Lerp(CycleDimBrightness, 1f, Mathf.Sin(phase * Mathf.PI));
        }

        private static int Mod(int value, int count) => ((value % count) + count) % count;

        private static int Glow(Color32 pixel) => Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));

        // Once per game: the live rune sheet's glow map, cropped to the halo's rune columns, becomes a white texture whose
        // alpha is the runes, levelled so the brightest stroke is solid. Read back through the GPU, so the game's texture
        // need not be readable. A headless server draws nothing and skips it.
        // Built OFF the join frame (R53: the synchronous ReadPixels + two passes over 917k pixels + a mipmapped Apply were 49 ms
        // of the bot's ZNetScene.Awake). The glow patch is read back asynchronously, the maths runs on a worker, and only the
        // texture upload returns to the main thread. Until then a halo stays hidden (Apply already does that for a missing
        // mask) and is lit when the mask lands. A 1x2 probe read back beside it tells whether this API returns rows flipped
        // relative to ReadPixels, so the runes can never come out mirrored.
        private static bool s_maskPending;
        private static readonly HashSet<GeneratorGlow> s_awaitingMask = new HashSet<GeneratorGlow>();

        // A readback's callback can find its texture already gone (a scene change, or a device that lost it); Unity's null check
        // covers a destroyed one, and a release that still throws only costs the halo.
        private static void ReleaseTexture(RenderTexture texture)
        {
            if (texture == null) return;
            try { texture.Release(); }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresZones] Chunk Generator dressing: a readback texture would not release ({ex.GetType().Name})."); }
            UnityEngine.Object.Destroy(texture);
        }

        private static bool EnsureRuneMask(Binding binding)
        {
            // No halo where nobody sees it: no graphics device, or a batch-mode client (the headless autoplay bot, R55: its readback
            // callbacks threw NullReferenceException in RenderTexture.Release).
            if (s_runeMask != null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || Application.isBatchMode)
                return true;
            if (s_maskTried) return s_maskPending;
            s_maskTried = true;
            if (!TryGetSource(binding, out var tile)) return false;
            var material = tile.sharedMaterial;
            var sheet = material != null && material.HasProperty(EmissionMap) ? material.GetTexture(EmissionMap) : null;
            if (sheet == null)
            {
                FiresLogger.LogWarning($"[FiresZones] Chunk Generator dressing: the live rune tile has no glow map; the halo is hidden.");
                return false;
            }
            if (!SystemInfo.supportsAsyncGPUReadback) return BuildMaskNow(sheet);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var target = new RenderTexture(MaskWidth, MaskHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(sheet, target, RunePatch.size, RunePatch.position);
            var probeSource = new Texture2D(1, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            probeSource.SetPixels32(new[] { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255) });
            probeSource.Apply(false, false);
            var probe = new RenderTexture(1, 2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
            Graphics.Blit(probeSource, probe);
            UnityEngine.Object.Destroy(probeSource);
            s_maskPending = true;
            bool? flipped = null;
            Color32[] read = null;
            void TryFinish()
            {
                if (flipped == null || read == null) return;
                bool flip = flipped.Value;
                var pixels = read;
                System.Threading.Tasks.Task.Run(() =>
                {
                    var glow = GlowMask(pixels, flip);
                    VerdantsAscent.Async.MainThreadDispatcher.Enqueue(() => FinishMask(glow, flip, clock));
                });
            }
            UnityEngine.Rendering.AsyncGPUReadback.Request(probe, 0, TextureFormat.RGBA32, request =>
            {
                // ReadPixels puts the texture's bottom row first; the probe's bottom texel is red.
                flipped = !request.hasError && request.GetData<Color32>()[0].r < 128;
                ReleaseTexture(probe);
                TryFinish();
            });
            UnityEngine.Rendering.AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
            {
                if (request.hasError)
                {
                    s_maskPending = false;
                    FiresLogger.LogWarning("[FiresZones] Chunk Generator dressing: the rune glow readback failed; the halo is hidden.");
                }
                else
                {
                    read = request.GetData<Color32>().ToArray();
                    TryFinish();
                }
                ReleaseTexture(target);
            });
            return true;
        }

        // Pure maths, safe on a worker: brightness to alpha over white, normalised to the brightest texel.
        private static Color32[] GlowMask(Color32[] pixels, bool flip)
        {
            int brightest = 1;
            for (int i = 0; i < pixels.Length; i++) brightest = Math.Max(brightest, Glow(pixels[i]));
            var mask = new Color32[pixels.Length];
            for (int y = 0; y < MaskHeight; y++)
            {
                int from = (flip ? MaskHeight - 1 - y : y) * MaskWidth;
                int to = y * MaskWidth;
                for (int x = 0; x < MaskWidth; x++)
                    mask[to + x] = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue,
                        (byte)Math.Min(byte.MaxValue, Glow(pixels[from + x]) * byte.MaxValue / brightest));
            }
            return mask;
        }

        private static void FinishMask(Color32[] glow, bool flipped, System.Diagnostics.Stopwatch clock)
        {
            var upload = System.Diagnostics.Stopwatch.StartNew();
            s_runeMask = NewMaskTexture(glow);
            s_maskPending = false;
            long uploadMs = upload.ElapsedMilliseconds;
            int lit = 0;
            foreach (var glowComponent in s_awaitingMask)
            {
                if (glowComponent == null) continue;
                var recipe = ZoneModeChunkTiers.ByRecipeId(glowComponent.Recipe);
                if (recipe == null) continue;
                Apply(glowComponent, recipe);
                lit++;
            }
            s_awaitingMask.Clear();
            FiresLogger.LogInfo($"[FiresZones] Chunk Generator rune mask built off the join frame: ready {clock.ElapsedMilliseconds} ms after the "
                + $"request, {uploadMs} ms of it on the main thread (upload){(flipped ? ", rows flipped to match ReadPixels" : "")}; {lit} waiting halo(s) lit.");
        }

        private static Texture2D NewMaskTexture(Color32[] glow)
        {
            var mask = new Texture2D(MaskWidth, MaskHeight, TextureFormat.RGBA32, true) { name = MaskName, wrapMode = TextureWrapMode.Repeat };
            mask.SetPixels32(glow);
            mask.Apply(true, true);
            return mask;
        }

        // The old synchronous path, for a graphics API without async readback.
        private static bool BuildMaskNow(Texture sheet)
        {
            var target = RenderTexture.GetTemporary(MaskWidth, MaskHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(sheet, target, RunePatch.size, RunePatch.position);
                RenderTexture.active = target;
                var read = new Texture2D(MaskWidth, MaskHeight, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0f, 0f, MaskWidth, MaskHeight), 0, 0, false);
                var pixels = read.GetPixels32();
                UnityEngine.Object.Destroy(read);
                s_runeMask = NewMaskTexture(GlowMask(pixels, false));
                return true;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        // The bake puts each piece's renderer on a "model" child of its anchor, so the anchor is the pivot that turns.
        private static string AnchorOf(Renderer renderer) => renderer.transform.parent != null ? renderer.transform.parent.name : "";

        private static Binding BindingFor(string anchorName)
        {
            foreach (var binding in Bindings)
                if (anchorName.StartsWith(binding.Anchor, System.StringComparison.Ordinal)) return binding;
            return null;
        }

        private static bool TryGetSource(Binding binding, out Renderer source)
        {
            source = null;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(binding.Source) : null;
            if (prefab == null && ObjectDB.instance != null) prefab = ObjectDB.instance.GetItemPrefab(binding.Source);
            var node = prefab != null ? prefab.transform.Find(binding.Path) : null;
            source = node != null ? node.GetComponent<MeshRenderer>() : null;
            if (source != null && (!binding.TakesMesh || node.GetComponent<MeshFilter>()?.sharedMesh != null)) return true;
            if (s_reported.Add(binding.Source + "/" + binding.Path))
                FiresLogger.LogWarning($"[FiresZones] Chunk Generator dressing: no live '{binding.Source}' renderer at '{binding.Path}'"
                    + (prefab == null ? " (the prefab is missing)" : "") + $"; its {binding.Anchor} anchors stay bare.");
            source = null;
            return false;
        }

        private static Material RecipeMaterial(Material source, string recipe, Color color, bool halo)
        {
            if (source == null) return null;
            if (!s_recipeMaterials.TryGetValue(source, out var byRecipe)) s_recipeMaterials[source] = byRecipe = new Dictionary<string, Material>();
            if (byRecipe.TryGetValue(recipe, out var copy) && copy != null) return copy;
            copy = new Material(source) { name = source.name + "_" + recipe };
            copy.SetColor(EmissionColor, color * (halo ? HaloGlowStrength : GlowStrength));
            if (halo)
            {
                copy.SetColor(AlbedoColor, color);
                copy.SetTexture(MainTex, s_runeMask);
                copy.SetTexture(EmissionMap, s_runeMask);
            }
            s_recipeSource[copy] = source;
            if (recipe == CyclingRecipe) s_cyclingMaterials.Add(new KeyValuePair<Material, bool>(copy, halo));
            return byRecipe[recipe] = copy;
        }

        private static Material SourceOf(Material material) => material != null && s_recipeSource.TryGetValue(material, out var source) ? source : material;

        // The dressing culls with the smelter body's first LOD, so it costs nothing at distance.
        private static void AddToFirstLod(GameObject machine, List<Renderer> dressed)
        {
            var group = machine.GetComponentInChildren<LODGroup>(true);
            if (group == null || dressed.Count == 0) return;
            var lods = group.GetLODs();
            if (lods.Length == 0) return;
            var renderers = new List<Renderer>(lods[0].renderers);
            foreach (var renderer in dressed) if (!renderers.Contains(renderer)) renderers.Add(renderer);
            lods[0].renderers = renderers.ToArray();
            group.SetLODs(lods);
        }

        // Per machine: its dressing renderers (the halo's among them), its fire lights and flames, the recipe its glow
        // shows, whether that glow cycles, and the anchors that turn.
        internal sealed class GeneratorGlow : MonoBehaviour
        {
            internal readonly List<Renderer> Renderers = new List<Renderer>();
            internal readonly List<Material[]> Originals = new List<Material[]>();
            internal readonly HashSet<Renderer> Halos = new HashSet<Renderer>();
            internal readonly List<Light> Lights = new List<Light>();
            internal readonly List<ParticleSystem> Fires = new List<ParticleSystem>();
            internal readonly List<Transform> Spinners = new List<Transform>();
            internal string Recipe;
            internal bool Cycles;

            internal void Bind(Transform root)
            {
                Renderers.Clear();
                Originals.Clear();
                Halos.Clear();
                Lights.Clear();
                Fires.Clear();
                Spinners.Clear();
                foreach (var light in GetComponentsInChildren<Light>(true))
                    if (root == null || !light.transform.IsChildOf(root)) Lights.Add(light);
                foreach (var system in GetComponentsInChildren<ParticleSystem>(true))
                    if ((root == null || !system.transform.IsChildOf(root)) && IsFire(system.name)) Fires.Add(system);
                if (root == null) return;
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var binding = BindingFor(AnchorOf(renderer));
                    if (binding == null) continue;
                    Renderers.Add(renderer);
                    Originals.Add(Array.ConvertAll(renderer.sharedMaterials, SourceOf));
                    if (binding.RuneMask) Halos.Add(renderer);
                    if (binding.Spins && renderer.transform.parent != null) Spinners.Add(renderer.transform.parent);
                }
                if (isActiveAndEnabled && (Spinners.Count > 0 || Cycles)) LookDriver.Add(this);
            }

            private static bool IsFire(string name)
            {
                foreach (string part in FireParts)
                    if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }

            private void OnEnable() { if (Spinners.Count > 0 || Cycles) LookDriver.Add(this); }

            private void OnDisable() => LookDriver.Remove(this);
        }

        // Every frame, for machines near the camera: the halo turns, and a plain-recipe machine's fire and lights follow the
        // colour cycle, whose shared rune and glow materials are set once for all of them.
        private sealed class LookDriver : MonoBehaviour
        {
            private static readonly List<GeneratorGlow> s_live = new List<GeneratorGlow>();
            private static LookDriver s_host;

            internal static void Add(GeneratorGlow glow)
            {
                if (!s_live.Contains(glow)) s_live.Add(glow);
                if (s_host != null) return;
                var host = new GameObject("FiresChunkGeneratorLook");
                UnityEngine.Object.DontDestroyOnLoad(host);
                s_host = host.AddComponent<LookDriver>();
            }

            internal static void Remove(GeneratorGlow glow) => s_live.Remove(glow);

            private void Update()
            {
                var camera = Utils.GetMainCamera();
                if (camera == null || s_live.Count == 0) return;
                var eye = camera.transform.position;
                float step = SpinDegreesPerSecond * Time.deltaTime;
                CycleAt(Time.time, out var colour, out float brightness);
                var pulsed = new Color(colour.r * brightness, colour.g * brightness, colour.b * brightness, colour.a);
                bool cycling = false;
                for (int i = s_live.Count - 1; i >= 0; i--)
                {
                    var glow = s_live[i];
                    if (glow == null) { s_live.RemoveAt(i); continue; }
                    if ((glow.transform.position - eye).sqrMagnitude > SpinRange * SpinRange) continue;
                    foreach (var spinner in glow.Spinners) if (spinner != null) spinner.Rotate(Vector3.up, step, Space.World);
                    if (!glow.Cycles) continue;
                    cycling = true;
                    TintFireAndLights(glow, pulsed);
                }
                if (cycling) SetCyclingMaterials(colour, brightness);
            }

            private static void SetCyclingMaterials(Color colour, float brightness)
            {
                foreach (var entry in s_cyclingMaterials)
                {
                    if (entry.Key == null) continue;
                    entry.Key.SetColor(EmissionColor, colour * brightness * (entry.Value ? HaloGlowStrength : GlowStrength));
                    if (entry.Value) entry.Key.SetColor(AlbedoColor, colour);
                }
            }
        }
    }
}
