using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Round-84 — live-tunable look for Custom/FiresLiquid. Every slider is a
    // BepInEx config entry (hot-reloads from the .cfg while in-game); on any
    // change we re-skin all live pours so you see it immediately. Dial it in,
    // then hand me the values and I bake them as the shader/material defaults.
    // Defaults here mirror the shader's own defaults (calm-water look).
    public static class FiresLiquidConfig
    {
        private const string S = "FiresLiquid Look";

        // Round-86 — dev-only pour lifecycle tracing (FiresLiquidWaterDebug).
        // Off by default; the origin-destroy bug it hunted is long fixed, so on
        // a healthy world it's just noisy Warning+stack spam on every unload.
        public static ConfigEntry<bool> Diagnostics;

        public static ConfigEntry<Color> ColorTop, ColorBottom, ColorShallow, SurfaceColor, FoamColor,
            AbsorbExtinction, MurkColor;
        public static ConfigEntry<float> Turbidity, TurbidityDepth, TurbChurn;
        public static ConfigEntry<float> CausticStrength, CausticScale, CausticSharp, CausticWarp;
        public static ConfigEntry<float> Glossiness, Metallic, Fresnel, Reflection, Specular,
            WaveAmplitude, NormalPower, NormalScale, Refraction, EdgeSoftness, DepthFade,
            FoamAmount, FoamDensity, FoamOpacity, FoamSlope, Opacity,
            FlowSpeed, FlowTurbulence, FlowFoam, FlowFoamAdvect, FlowWaveAmp, FlowWaveLength, FlowWaveSpeed,
            NormalSpeedA, NormalSpeedB, NormalAngleA, NormalAngleB;
        public static ConfigEntry<bool> UseFiresNormals;

        private static bool _bound;

        public static void Bind(ConfigFile cfg)
        {
            if (_bound || cfg == null) return;
            _bound = true;

            Diagnostics = cfg.Bind("FiresLiquid Diagnostics", "Verbose pour tracing", false,
                "Dev-only: trace custom-water pour spawn/destroy/ZDO-reset lifecycle (noisy Warning+stack). Off = quiet.");

            // TUNED FOR POND DEPTH, NOT OCEAN DEPTH. Everything the shader does
            // with colour scales with the water column: absorption runs
            // `depthMetres = vertMetres * 2`, so a 2.5m pond only gets ~5m of
            // extinction path and the TOP tint dominates almost everything you
            // see. A saturated blue there reads as "far too blue no matter what
            // I set" — the deep colours never get depth enough to show. These
            // defaults sit in fresh-water teal instead.
            ColorTop     = cfg.Bind(S, "Color Top",            new Color(0.16f, 0.45f, 0.48f, 1f), "Surface top tint");
            ColorBottom  = cfg.Bind(S, "Color Bottom (deep)",  new Color(0.06f, 0.24f, 0.26f, 1f), "Deep-water colour");
            ColorShallow = cfg.Bind(S, "Color Bottom (shallow)", new Color(0.32f, 0.70f, 0.60f, 1f), "Shallow-water colour");
            SurfaceColor = cfg.Bind(S, "Surface Color",        new Color(0.70f, 0.72f, 0.63f, 1f), "Surface reflection tint");
            FoamColor    = cfg.Bind(S, "Foam Color",           new Color(1f, 1f, 1f, 1f),          "Foam colour");
            AbsorbExtinction = cfg.Bind(S, "Absorb Extinction", new Color(0.45f, 0.16f, 0.30f, 1f),
                "FRESH-water depth extinction (rivers/ponds/far river sheets only — the ocean keeps its own "
                + "curve, FiresTossinShade key 35). Per-channel: how fast each colour DIES per metre of depth, "
                + "so this is an inverse — high red = red vanishes first. Fresh water plus tannins also eat "
                + "BLUE, so with the default green survives longest and depths go olive/emerald instead of the "
                + "ocean's blue-teal. Set equal to the ocean curve (993D21FF) to re-unify the two. Only active "
                + "while AbsorbBlend (FiresTossinShade key 34) is above 0.");

            Glossiness   = cfg.Bind(S, "Smoothness",     0.88f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
            Metallic     = cfg.Bind(S, "Metallic",       0.0f,  new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
            Fresnel      = cfg.Bind(S, "Fresnel Strength", -0.33f, new ConfigDescription("Reflection falloff at grazing angles", new AcceptableValueRange<float>(-2f, 8f)));
            Reflection   = cfg.Bind(S, "Reflection Strength", 0.7f, new ConfigDescription("Sky/environment reflection — lower = less mirror-like", new AcceptableValueRange<float>(0f, 2f)));
            Specular     = cfg.Bind(S, "Specular Intensity", 0.6f, new ConfigDescription("Sun glint strength", new AcceptableValueRange<float>(0f, 2f)));
            WaveAmplitude = cfg.Bind(S, "Wave Amplitude", 0.15f, new ConfigDescription("Surface wave height", new AcceptableValueRange<float>(0f, 2f)));
            NormalPower  = cfg.Bind(S, "Ripple Power",   1.0f,  new ConfigDescription("Normal-map ripple strength", new AcceptableValueRange<float>(0f, 5f)));
            NormalScale  = cfg.Bind(S, "Ripple Scale",   0.16f, new ConfigDescription("Normal-map tiling", new AcceptableValueRange<float>(0.01f, 2f)));
            // ── RIPPLE NORMALS ───────────────────────────────────────────────
            // The two normal layers are the whole surface read, and until now
            // only their strength and tiling were reachable — the SCROLL and the
            // two layer ANGLES were baked into the shader, so a creased map had
            // no way out. Vanilla's ocean normals are what produced the hard
            // lines on a river; ours replace them, and these expose the rest.
            UseFiresNormals = cfg.Bind(S, "Use Fires Water Normals", true,
                new ConfigDescription("Use our water_nrm / water_nrm_detail maps instead of Valheim's ocean normals. Off = vanilla's (creased on shallow water)."));
            NormalSpeedA = cfg.Bind(S, "Ripple Speed (coarse)", 1.0f,
                new ConfigDescription("Scroll rate of the coarse ripple layer", new AcceptableValueRange<float>(0f, 5f)));
            NormalSpeedB = cfg.Bind(S, "Ripple Speed (fine)", 1.0f,
                new ConfigDescription("Scroll rate of the fine ripple layer", new AcceptableValueRange<float>(0f, 5f)));
            NormalAngleA = cfg.Bind(S, "Ripple Angle (coarse)", 20f,
                new ConfigDescription("Drift heading of the coarse layer, degrees. Only used on still water — flowing water takes its direction from the river.", new AcceptableValueRange<float>(0f, 360f)));
            NormalAngleB = cfg.Bind(S, "Ripple Angle (fine)", 115f,
                new ConfigDescription("Drift heading of the fine layer, degrees. Keep it well away from the coarse angle or the two layers beat into visible bands.", new AcceptableValueRange<float>(0f, 360f)));
            Refraction   = cfg.Bind(S, "Refraction",     0.04f, new ConfigDescription("See-through distortion", new AcceptableValueRange<float>(0f, 0.3f)));
            DepthFade    = cfg.Bind(S, "Depth Fade (m)", 100f,  new ConfigDescription("Distance the deep colour ramps over", new AcceptableValueRange<float>(1f, 300f)));
            EdgeSoftness = cfg.Bind(S, "Bank Edge Softness", 0.55f, new ConfigDescription("Soft wash-out at the bank (higher = softer)", new AcceptableValueRange<float>(0.1f, 0.9f)));

            // Round-86 — FOAM controls. The HD foam texture is denser than the
            // vanilla one, so at the shared global intensity it blankets the
            // liquid white and the additive foam whites-out whatever is seen
            // through the surface. These override the shared _FiresFoam* globals
            // PER-MATERIAL (liquid only — the ocean keeps its own foam).
            FoamDensity  = cfg.Bind(S, "Foam Density",   0.18f, new ConfigDescription("Foam texture strength — lower = less white foam on the surface", new AcceptableValueRange<float>(0f, 1.5f)));
            FoamOpacity  = cfg.Bind(S, "Foam Opacity",   0.65f, new ConfigDescription("Foam whiteness — lower = foam is dimmer and you see through it more", new AcceptableValueRange<float>(0f, 1f)));
            FoamSlope    = cfg.Bind(S, "Foam on Flow",   0.05f, new ConfigDescription("Extra foam on steep/flowing surface — lower = flowing water stays clear", new AcceptableValueRange<float>(0f, 1f)));
            FoamAmount   = cfg.Bind(S, "Foam at Edges",  0.3f,  new ConfigDescription("Shore/edge foam amount", new AcceptableValueRange<float>(0f, 2f)));

            // TURBIDITY (suspended sediment): a depth-scaled blur + murk that
            // dissolves hard seabed-mesh corners seen through the water AND
            // gives moving water / ponds / lakes their churny, alive look.
            // THE MURK IS DEPTH-SCALED, so these defaults are set for the depth
            // inland water actually has. The shader computes
            //     murk = 1 - exp(-Turbidity * (depth * 2) / FullDepth)
            // and then multiplies by Murk Color's ALPHA. At the old ocean-tuned
            // defaults (0.4 / 5m / alpha 0.35) a 2.5m pond came out at
            // 0.33 * 0.35 = a 12% tint — working, but invisible, which reads as
            // "the murk slider does nothing".
            Turbidity      = cfg.Bind(S, "Turbidity", 0.9f, new ConfigDescription(
                "How cloudy the water is with suspended sediment. Blurs the bottom (dissolving the hard "
                + "corners / 'two layers' you see through the water where the seabed mesh steps) and hazes "
                + "deep water toward the murk colour. 0 = crystal clear (sharp bottom, old look), 1 = thick "
                + "silty water. Works WITH Turbidity Full Depth and the Murk Color alpha — all three scale "
                + "with the water column, so raising this alone does little in a shallow pond.",
                new AcceptableValueRange<float>(0f, 1f)));
            TurbidityDepth = cfg.Bind(S, "Turbidity Full Depth", 1.5f, new ConfigDescription(
                "Metres of water depth at which turbidity reaches full strength. Lower = even shallow water "
                + "goes cloudy; higher = only deep water clouds over. Inland ponds and rivers are 2-6m deep, "
                + "so a value near the ocean's scale leaves them crystal clear.",
                new AcceptableValueRange<float>(0.5f, 30f)));
            // CAUSTICS, inland-only override of the FTS globals. Ocean scale on
            // a pond = one giant Worley cell = the concentric rings and long
            // straight white lines across the water.
            CausticStrength = cfg.Bind(S, "Caustic Strength", 0.35f, new ConfigDescription(
                "Brightness of the light web cast on the bottom, for INLAND water only (the ocean keeps the "
                + "FiresTossinShade value). SET THIS TO 0 TO TURN INLAND CAUSTICS OFF — that is the A/B for "
                + "'is this weird pattern the caustics'.", new AcceptableValueRange<float>(0f, 4f)));
            CausticScale   = cfg.Bind(S, "Caustic Scale", 0.05f, new ConfigDescription(
                "Cell size of the caustic web: this is 1/metres, so HIGHER = smaller cells. The ocean runs "
                + "0.007 (one cell per ~140m); at that scale a pond fits inside a single cell and you see its "
                + "distance field as concentric rings with the cell boundary as a straight white line. 0.05 = "
                + "one cell per ~20m, so a pond gets a web instead of one blob.",
                new AcceptableValueRange<float>(0.001f, 0.5f)));
            CausticSharp   = cfg.Bind(S, "Caustic Sharpness", 1.4f, new ConfigDescription(
                "Line thickness/contrast — the shader raises the web to this power, so BELOW 1 brightens and "
                + "fattens the bands (the washed-out look), above 1 thins them into crisper threads.",
                new AcceptableValueRange<float>(0.1f, 6f)));
            CausticWarp    = cfg.Bind(S, "Caustic Warp", 1.2f, new ConfigDescription(
                "Domain warp — how irregular the cells are. 0 = regular lattice (reads as a grid), higher = "
                + "organic blobs.", new AcceptableValueRange<float>(0f, 4f)));

            TurbChurn      = cfg.Bind(S, "Sediment Churn", 0.4f, new ConfigDescription(
                "How fast the suspended sediment drifts/shimmers. 0 = static haze, higher = livelier churn.",
                new AcceptableValueRange<float>(0f, 3f)));
            MurkColor      = cfg.Bind(S, "Murk Color", new Color(0.30f, 0.40f, 0.36f, 0.85f), new ConfigDescription(
                "Sediment haze colour the deep bottom fades into (RGB), and its max strength (ALPHA — this is "
                + "the master on how murky water can ever get; the old 0.35 capped every pond at a third of "
                + "the haze no matter what Turbidity said). A green-grey reads like silty fresh water; raise "
                + "Alpha for murkier, lower for clearer.", null));

            // Downhill FLOW (rivers): the gen-water sheet bakes the surface's
            // downhill gradient per vertex; the shader flow-maps its ripple
            // normals along it — speed scales with slope (fast rapids, slow
            // flats, still ponds), steep runs turn turbulent and foam white.
            FlowSpeed      = cfg.Bind(S, "Flow Speed",      1.0f, new ConfigDescription(
                "Master scale on how fast river water visibly travels downhill. The rate already scales with "
                + "terrain slope (steep = fast, flat = crawl, ponds = still); this multiplies it. 0.5 = lazy "
                + "rivers, 2 = racing meltwater.", new AcceptableValueRange<float>(0.1f, 4f)));
            FlowTurbulence = cfg.Bind(S, "Flow Turbulence", 1.0f, new ConfigDescription(
                "How churned the surface ripple gets on steep, fast sections (boosts the normal-map relief "
                + "with slope). 0 = steep water stays as calm as flat water.", new AcceptableValueRange<float>(0f, 3f)));
            FlowFoam       = cfg.Bind(S, "Flow Foam",       0.5f, new ConfigDescription(
                "White-water foam on steep, fast sections — rapids churn white even without waves. 0 = off.",
                new AcceptableValueRange<float>(0f, 2f)));
            FlowFoamAdvect = cfg.Bind(S, "Flow Foam Drift", 1f, new ConfigDescription(
                "Makes the foam/whitecaps visibly TRAVEL downstream with the current (vanilla flowing-water "
                + "look) instead of sitting still or drifting on the wind. 0 = static/wind foam (old look); "
                + "1 = full downstream drift at the flow speed. The single biggest 'reads as flowing' cue.",
                new AcceptableValueRange<float>(0f, 1f)));
            FlowWaveAmp    = cfg.Bind(S, "Roll Height", 0.12f, new ConfigDescription(
                "How high the river surface actually ROLLS — travelling swells that move downstream along the "
                + "flow and heave the mesh (not static bumps). Scales with flow speed, so ponds stay glassy and "
                + "fast water heaves. 0 = flat sheet. Visual only (boats float on the flat level), so keep it "
                + "modest.", new AcceptableValueRange<float>(0f, 0.6f)));
            FlowWaveLength = cfg.Bind(S, "Roll Length", 6f, new ConfigDescription(
                "Wavelength (metres) of the rolling swells. Shorter = tighter, choppier rolls; longer = broad "
                + "lazy swells.", new AcceptableValueRange<float>(2f, 20f)));
            FlowWaveSpeed  = cfg.Bind(S, "Roll Speed", 1f, new ConfigDescription(
                "How fast the rolls travel downstream. 0 = frozen ridges, higher = faster rolling.",
                new AcceptableValueRange<float>(0f, 3f)));

            // Round-86 — master surface transparency (drives the shader's
            // _FiresLiquidOpacity). Lower = the water is less opaque/glassy and
            // you see through it more. Foam/crests stay defined.
            Opacity      = cfg.Bind(S, "Water Opacity",  0.85f, new ConfigDescription("Surface opacity — lower = more see-through, less solid sheet", new AcceptableValueRange<float>(0.1f, 1.2f)));

            // Hot-reload — any change to our section re-skins the live pours.
            cfg.SettingChanged += (_, args) =>
            {
                if (args?.ChangedSetting?.Definition?.Section != S) return;
                try { FiresLiquidWaterPrefab.ReapplyFiresLiquidToAll(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresLiquidConfig] re-skin failed: {ex.Message}"); }
            };

            FiresLogger.LogInfo("[FiresLiquidConfig] look sliders bound (section 'FiresLiquid Look', hot-reload).");
        }

        private static IEnumerable<ConfigEntryBase> AllEntries()
        {
            yield return ColorTop; yield return ColorBottom; yield return ColorShallow;
            yield return SurfaceColor; yield return FoamColor; yield return AbsorbExtinction;
            yield return Glossiness; yield return Metallic; yield return Fresnel;
            yield return Reflection; yield return Specular; yield return WaveAmplitude;
            yield return NormalPower; yield return NormalScale; yield return Refraction;
            yield return NormalSpeedA; yield return NormalSpeedB;
            yield return NormalAngleA; yield return NormalAngleB;
            yield return DepthFade; yield return FoamAmount; yield return EdgeSoftness;
            yield return FoamDensity; yield return FoamOpacity; yield return FoamSlope;
            yield return Opacity;
            yield return FlowSpeed; yield return FlowTurbulence; yield return FlowFoam; yield return FlowFoamAdvect;
            yield return FlowWaveAmp; yield return FlowWaveLength; yield return FlowWaveSpeed;
            yield return Turbidity; yield return TurbidityDepth; yield return TurbChurn; yield return MurkColor;
            yield return CausticStrength; yield return CausticScale; yield return CausticSharp; yield return CausticWarp;
        }

        // Apply the current config values to a FiresLiquid material.
        public static void Apply(Material m)
        {
            if (m == null || !_bound) return;
            SetC(m, "_ColorTop", ColorTop);
            SetC(m, "_ColorBottom", ColorBottom);
            SetC(m, "_ColorBottomShallow", ColorShallow);
            SetC(m, "_SurfaceColor", SurfaceColor);
            SetF(m, "_Glossiness", Glossiness);
            SetF(m, "_Metallic", Metallic);
            SetF(m, "_FresnelStrength", Fresnel);
            SetF(m, "_ReflectionStrength", Reflection);
            SetF(m, "_SpecularIntensity", Specular);
            SetF(m, "_WaveAmplitude", WaveAmplitude);
            SetF(m, "_NormalPower", NormalPower);
            SetF(m, "_NormalScale", NormalScale);
            SetF(m, "_NormalSpeedA", NormalSpeedA);
            SetF(m, "_NormalSpeedB", NormalSpeedB);
            SetF(m, "_NormalAngleA", NormalAngleA);
            SetF(m, "_NormalAngleB", NormalAngleB);
            FiresLiquidWaterPrefab.ApplyFiresWaterNormals(m);
            SetF(m, "_RefractionScale", Refraction);
            SetF(m, "_DepthFade", DepthFade);
            SetF(m, "_FiresEdgeSoftness", EdgeSoftness);
            SetF(m, "_FiresLiquidOpacity", Opacity);

            // Depth extinction — same global-override trick as the foam block
            // below: _FiresAbsorbExtinction is the FTS-pushed OCEAN curve; a raw
            // per-material set gives fresh water its own hue-with-depth (tannin
            // waters eat blue too — olive/emerald depths, not ocean blue-teal).
            if (AbsorbExtinction != null) m.SetColor("_FiresAbsorbExtinction", AbsorbExtinction.Value);

            // Turbidity — CGINCLUDE uniforms, set RAW per-material like the foam/
            // extinction block, so rivers/ponds get their own murk without
            // touching the ocean.
            if (Turbidity != null) m.SetFloat("_FiresTurbidity", Turbidity.Value);
            if (TurbidityDepth != null) m.SetFloat("_FiresTurbidityDepth", TurbidityDepth.Value);
            if (TurbChurn != null) m.SetFloat("_FiresTurbChurn", TurbChurn.Value);
            if (MurkColor != null) m.SetColor("_FiresMurkColor", MurkColor.Value);

            // Caustics — the seabed light web. FTS pushes _FiresCaustic* as
            // GLOBALS for the ocean, and at ocean scale (one Worley cell per
            // ~140m) a 30m pond sits INSIDE a single cell: you see that one
            // cell's distance field as concentric contour rings with its
            // boundary crossing the water as a long straight white line.
            // Override per-material so inland gets pond-scale cells and the
            // ocean keeps its own, and so Strength=0 is a real A/B kill switch
            // for inland caustics alone.
            if (CausticStrength != null) m.SetFloat("_FiresCausticStrength", CausticStrength.Value);
            if (CausticScale != null) m.SetFloat("_FiresCausticScale", CausticScale.Value);
            if (CausticSharp != null) m.SetFloat("_FiresCausticSharp", CausticSharp.Value);
            if (CausticWarp != null) m.SetFloat("_FiresCausticWarp", CausticWarp.Value);

            // Downhill flow — CGINCLUDE uniforms, set RAW like the foam block.
            if (FlowSpeed != null) m.SetFloat("_FiresFlowSpeed", FlowSpeed.Value);
            if (FlowTurbulence != null) m.SetFloat("_FiresFlowTurb", FlowTurbulence.Value);
            if (FlowFoam != null) m.SetFloat("_FiresFlowFoam", FlowFoam.Value);
            if (FlowFoamAdvect != null) m.SetFloat("_FiresFlowFoamAdvect", FlowFoamAdvect.Value);
            // Rolling mesh swell. Shader wants spatial frequency = 2π/wavelength.
            if (FlowWaveAmp != null) m.SetFloat("_FiresFlowWaveAmp", FlowWaveAmp.Value);
            if (FlowWaveLength != null)
                m.SetFloat("_FiresFlowWaveFreq", 6.2831853f / Mathf.Max(1f, FlowWaveLength.Value));
            if (FlowWaveSpeed != null) m.SetFloat("_FiresFlowWaveSpeed", FlowWaveSpeed.Value);

            // Foam — the shader reads _FiresFoam* GLOBALS (set by FTS for the
            // ocean). Override them per-material so the liquid foam is tunable
            // without touching the ocean. These are CGINCLUDE globals, not
            // Properties, so HasProperty is false — set them RAW (a per-material
            // value still overrides the global at render).
            if (FoamDensity != null) { m.SetFloat("_FiresFoamIntensityA", FoamDensity.Value); m.SetFloat("_FiresFoamIntensityB", FoamDensity.Value * 0.6f); }
            if (FoamAmount != null) m.SetFloat("_FiresFoamShoreAmount", FoamAmount.Value);
            if (FoamSlope != null) m.SetFloat("_FiresFoamSlopeDriven", FoamSlope.Value);
            // Foam colour = tint (picker) dimmed by opacity — lower opacity makes
            // the additive foam dimmer so it stops whiting-out what's behind it.
            {
                Color fc = FoamColor != null ? FoamColor.Value : Color.white;
                float o = FoamOpacity != null ? FoamOpacity.Value : 1f;
                m.SetColor("_FiresFoamColor", new Color(fc.r * o, fc.g * o, fc.b * o, fc.a));
            }
        }

        private static void SetC(Material m, string p, ConfigEntry<Color> e)
        {
            if (e != null && m.HasProperty(p)) m.SetColor(p, e.Value);
        }
        private static void SetF(Material m, string p, ConfigEntry<float> e)
        {
            if (e != null && m.HasProperty(p)) m.SetFloat(p, e.Value);
        }
    }
}
