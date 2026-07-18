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

        public static ConfigEntry<Color> ColorTop, ColorBottom, ColorShallow, SurfaceColor, FoamColor;
        public static ConfigEntry<float> Glossiness, Metallic, Fresnel, Reflection, Specular,
            WaveAmplitude, NormalPower, NormalScale, Refraction, EdgeSoftness, DepthFade,
            FoamAmount, FoamDensity, FoamOpacity, FoamSlope, Opacity;

        private static bool _bound;

        public static void Bind(ConfigFile cfg)
        {
            if (_bound || cfg == null) return;
            _bound = true;

            Diagnostics = cfg.Bind("FiresLiquid Diagnostics", "Verbose pour tracing", false,
                "Dev-only: trace custom-water pour spawn/destroy/ZDO-reset lifecycle (noisy Warning+stack). Off = quiet.");

            ColorTop     = cfg.Bind(S, "Color Top",            new Color(0.10f, 0.50f, 0.70f, 1f), "Surface top tint");
            ColorBottom  = cfg.Bind(S, "Color Bottom (deep)",  new Color(0.05f, 0.27f, 0.45f, 1f), "Deep-water colour");
            ColorShallow = cfg.Bind(S, "Color Bottom (shallow)", new Color(0.32f, 0.70f, 0.60f, 1f), "Shallow-water colour");
            SurfaceColor = cfg.Bind(S, "Surface Color",        new Color(0.70f, 0.72f, 0.63f, 1f), "Surface reflection tint");
            FoamColor    = cfg.Bind(S, "Foam Color",           new Color(1f, 1f, 1f, 1f),          "Foam colour");

            Glossiness   = cfg.Bind(S, "Smoothness",     0.88f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
            Metallic     = cfg.Bind(S, "Metallic",       0.0f,  new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
            Fresnel      = cfg.Bind(S, "Fresnel Strength", -0.33f, new ConfigDescription("Reflection falloff at grazing angles", new AcceptableValueRange<float>(-2f, 8f)));
            Reflection   = cfg.Bind(S, "Reflection Strength", 0.7f, new ConfigDescription("Sky/environment reflection — lower = less mirror-like", new AcceptableValueRange<float>(0f, 2f)));
            Specular     = cfg.Bind(S, "Specular Intensity", 0.6f, new ConfigDescription("Sun glint strength", new AcceptableValueRange<float>(0f, 2f)));
            WaveAmplitude = cfg.Bind(S, "Wave Amplitude", 0.15f, new ConfigDescription("Surface wave height", new AcceptableValueRange<float>(0f, 2f)));
            NormalPower  = cfg.Bind(S, "Ripple Power",   1.0f,  new ConfigDescription("Normal-map ripple strength", new AcceptableValueRange<float>(0f, 5f)));
            NormalScale  = cfg.Bind(S, "Ripple Scale",   0.16f, new ConfigDescription("Normal-map tiling", new AcceptableValueRange<float>(0.01f, 2f)));
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

            // Round-86 — master surface transparency (drives the shader's
            // _FiresLiquidOpacity). Lower = the water is less opaque/glassy and
            // you see through it more. Foam/crests stay defined.
            Opacity      = cfg.Bind(S, "Water Opacity",  0.7f,  new ConfigDescription("Surface opacity — lower = more see-through, less solid sheet", new AcceptableValueRange<float>(0.1f, 1.2f)));

            // Hot-reload — any change to our section re-skins the live pours.
            cfg.SettingChanged += (_, args) =>
            {
                if (args?.ChangedSetting?.Definition?.Section != S) return;
                try { FiresLiquidWaterPrefab.ReapplyFiresLiquidToAll(); }
                catch (System.Exception ex) { Debug.LogWarning($"[FiresLiquidConfig] re-skin failed: {ex.Message}"); }
            };

            Debug.Log("[FiresLiquidConfig] look sliders bound (section 'FiresLiquid Look', hot-reload).");
        }

        private static IEnumerable<ConfigEntryBase> AllEntries()
        {
            yield return ColorTop; yield return ColorBottom; yield return ColorShallow;
            yield return SurfaceColor; yield return FoamColor;
            yield return Glossiness; yield return Metallic; yield return Fresnel;
            yield return Reflection; yield return Specular; yield return WaveAmplitude;
            yield return NormalPower; yield return NormalScale; yield return Refraction;
            yield return DepthFade; yield return FoamAmount; yield return EdgeSoftness;
            yield return FoamDensity; yield return FoamOpacity; yield return FoamSlope;
            yield return Opacity;
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
            SetF(m, "_RefractionScale", Refraction);
            SetF(m, "_DepthFade", DepthFade);
            SetF(m, "_FiresEdgeSoftness", EdgeSoftness);
            SetF(m, "_FiresLiquidOpacity", Opacity);

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
