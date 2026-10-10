using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // ═══ LIVE LOOK FOR Custom/FiresInlandWater ═══════════════════════════════
    // The inland shader carries ~80 properties and the config used to reach 19
    // of them, so most of how the water reads could only be changed by editing
    // the shader and rebuilding a bundle. Everything it declares is bound here:
    // colour, clarity, ripples per layer, the normal bend, flow, swell, foam and
    // the underwater view.
    //
    // It matters that this is the INLAND shader and not FiresLiquid. FiresLiquid
    // has ONE tiling value for both ripple layers, the second layer's scale
    // written in as a literal (baseUV * 2.7), and no per-layer direction,
    // strength or tiling break. No config can reach those without editing that
    // shader, which is off limits.
    //
    // Sections are numbered so the .cfg and the config UI list them in the order
    // you would actually work through them. Hot-reload is filtered on the shared
    // prefix, so a change in ANY of them re-skins the live sheet immediately.
    public static class FiresInlandWaterConfig
    {
        private const string P  = "FiresInlandWater";
        private const string S1 = P + " 1 Colour";
        private const string S2 = P + " 2 Clarity and murk";
        private const string S3 = P + " 3 Ripple scale and direction";
        private const string S4 = P + " 4 Normal bend";
        private const string S5 = P + " 5 Flow";
        private const string S6 = P + " 6 Swell";
        private const string S7 = P + " 7 Foam and churn";
        private const string S8 = P + " 8 Optical";
        private const string S9 = P + " 9 Underwater";

        public static ConfigEntry<Color> ShallowColor, DeepColor, FoamColor, ReflTint, SpecColor, UwTint;

        public static ConfigEntry<float> ClearDepth, MinOpacity, MaxOpacity, EdgeSoftness, SeaFade;
        public static ConfigEntry<float> Turbidity, ShoreChurn, ShoreChurnDepth, ShoreBlend;   // 0.2.819: Core 0.2.314+ shader dials
        public static ConfigEntry<float> UwAirAlphaMax, UwAirTint;   // 0.2.842: Core 0.2.319 underside-from-air dials

        public static ConfigEntry<float> BumpTiling, DetailTiling, GlobalScale, DetailRotate, TileBreak,
            NormalStrength, NormalOpacityMin, NormalOpacityMax, DetailNormalOpacityMin, DetailNormalOpacityMax,
            TexRipple, ProcRipple, ProcScale, RippleSpeed, RippleSize, RippleStrength, RippleDrift,
            NormalPhaseSpeed, NormalCrossSpeed, DetailNormalCrossSpeed;
        public static ConfigEntry<Vector2> BumpScroll, DetailScroll;

        public static ConfigEntry<float> SwellVisual, SwellNormalBend, BendClamp, BendShoreDepth,
            BendShoreFade, DistanceFlatten, NormalFlatFloor;

        public static ConfigEntry<float> FlowStrength, FlowResponse, FlowCycle, FlowSpeedFloor,
            FlowSpeedFull, RiverSwell, RiverWhitewater, FlowDebug;

        public static ConfigEntry<float> SwellAmp, SwellLength, SwellSpeed, SwellShoreCalm, SwellShoreFloor;

        public static ConfigEntry<float> EdgeFoamDepth, EdgeFoamOpacity, EdgeFoamTiling, EdgeFoamScroll,
            EdgeFoamBase, EdgeFoamContrast, EdgeFoamGain, RapidsFoam, RapidsStart, RapidsRamp,
            RapidsTiling, EddyFoam, WhitewaterOpacity;

        public static ConfigEntry<float> RefractStrength, FresnelPower, ReflStrength, ReflBlurStart,
            ReflBlurRange, ReflBlurMax, StillReflBoost, ReflWaterTint, Smoothness;

        public static ConfigEntry<float> UwDepthFade, UwTintStrength, UwSunMaskLow, UwSunMaskHigh,
            UwCaustic, UwRefract, UwSnell, UwAlphaMin, UwAlphaMax;

        private static bool _bound;

        private static ConfigEntry<float> F(ConfigFile c, string s, string k, float v, float lo, float hi, string d)
            => c.Bind(s, k, v, new ConfigDescription(d, new AcceptableValueRange<float>(lo, hi)));

        public static void Bind(ConfigFile cfg)
        {
            if (_bound) return;
            _bound = true;

            ShallowColor = cfg.Bind(S1, "Shallow Tint", new Color(0.24f, 0.42f, 0.42f, 1f),
                "Colour of a shallow column. This is what most of a river reads as, so tune it first.");
            DeepColor = cfg.Bind(S1, "Deep Tint", new Color(0.05f, 0.18f, 0.22f, 1f),
                "Colour the water reaches at full clarity depth. The gap between this and the shallow tint IS the sense of depth.");
            FoamColor = cfg.Bind(S1, "Foam Colour", new Color(0.95f, 0.97f, 0.99f, 1f),
                "Bank collar, rapids and whitewater. Pure white reads as blown highlight in bright sun - pull it grey.");
            ReflTint = cfg.Bind(S1, "Reflection Tint", new Color(0.7f, 0.8f, 0.9f, 1f),
                "Tints what the surface reflects. Cooler than the water tint keeps the sky reading as sky.");
            SpecColor = cfg.Bind(S1, "Sun Specular", Color.white,
                "Colour of the sun glint. Drop its brightness if the surface sparkles hard at grazing angles.");
            UwTint = cfg.Bind(S1, "Underwater Tint", new Color(0.10f, 0.30f, 0.38f, 1f),
                "What the world turns when the camera is under the surface.");

            ClearDepth = F(cfg, S2, "See-through depth (m)", 3.0f, 0.1f, 40f,
                "Metres of water it takes to reach the deep tint and full opacity. LOW = murky, HIGH = clear.");
            MinOpacity = F(cfg, S2, "Shallow opacity", 0.35f, 0f, 1f,
                "Opacity where the column is nearly nothing. 0.2.815: 0.35 (was 0.10: the shallows went clear and then jumped to the deep blue over 1.8 m, Fire 10-06).");
            MaxOpacity = F(cfg, S2, "Deep opacity", 0.90f, 0f, 1f,
                "Opacity at full clarity depth.");
            EdgeSoftness = F(cfg, S2, "Waterline fade (m)", 0.18f, 0.001f, 4f,
                "Dissolves the last centimetres at the bank so the sheet's own mesh edge is never a hard line.");
            // 0.2.819 (Fire 10-06: "the depth blend was meant to blend and churn a bit, not just be completely clear at the
            // shoreline"): Core 0.2.314's per-pixel shore dials ([generator]); on an older Core the shader lacks them and they
            // are skipped (HasProperty).
            Turbidity = F(cfg, S2, "Turbidity", 0.3f, 0f, 1f,
                "Sediment murk: at 1 the clear depth is a quarter, the shallow opacity floor rises to 0.6 and the tint leans 35% toward the deep tint. 0 = the old clear look.");
            ShoreChurn = F(cfg, S2, "Shore churn", 0.35f, 0f, 1f,
                "How much the shallow band's depth wobbles with the flow (moving noise along the shore). 0 = a still band.");
            ShoreChurnDepth = F(cfg, S2, "Shore churn depth (m)", 0.35f, 0f, 2f,
                "How big that wobble is, in metres of apparent depth.");
            ShoreBlend = F(cfg, S2, "Shore blend", 1f, 0f, 1f,
                "1 = soft exponential shallow fade (1 - exp(-depth / clear depth)); 0 = the old linear opacity ramp.");
            // 0.2.842 ([generator], Core 0.2.319; Fire 10-07, 119.webp: "lighten this up even more"): the underside of a sheet seen
            // from AIR (a grotto, a camera in a sinkhole shaft below the river bed). A diver in the water is unchanged.
            UwAirAlphaMax = F(cfg, S2, "Underside from air: max opacity", 0.28f, 0f, 1f,
                "Most opacity a water surface has when seen from below out of the water (from a grotto or a sinkhole shaft). Lower = clearer sky through it.");
            UwAirTint = F(cfg, S2, "Underside from air: tint", 0.4f, 0f, 1f,
                "How much of the underwater tint that underside keeps (1 = full, 0 = none).");
            SeaFade = F(cfg, S2, "Sea handover fade (m)", 1f, 0f, 12f,
                "Width of the ribbon at a river mouth where this sheet hands over to the ocean.");

            BumpTiling = F(cfg, S3, "Normal tiling (coarse)", 0.02f, 0.001f, 0.5f,
                "World tiling of the coarse ripple. SMALLER = the texture covers more ground = less visible repeat.");
            DetailTiling = F(cfg, S3, "Normal tiling (detail)", 0.02f, 0.001f, 0.5f,
                "World tiling of the detail ripple. Keep it off a simple multiple of the coarse tiling or the two lattices line up.");
            GlobalScale = F(cfg, S3, "Global ripple scale", 1f, 0.05f, 10f,
                "Scales both layers at once - the quick way to find the right size before splitting them.");
            BumpScroll = cfg.Bind(S3, "Normal scroll (coarse) xy", new Vector2(0.02f, 0.015f),
                "Drift of the coarse layer in world x/y. STILL water only - flowing water takes its direction from the river.");
            DetailScroll = cfg.Bind(S3, "Normal scroll (detail) xy", new Vector2(-0.03f, 0.02f),
                "Drift of the detail layer. Point it AGAINST the coarse layer so the surface never reads as one sheet sliding.");
            DetailRotate = F(cfg, S3, "Detail rotate (deg)", 52f, 0f, 180f,
                "Rotates the detail layer's UVs. The cheapest fix for a visible lattice: rotate it off the coarse layer's axis.");
            TileBreak = F(cfg, S3, "Tiling break", 1f, 0f, 3f,
                "Breaks up the texture repeat. Raise this FIRST when you can see the tile grid; 0 disables it and the lattice returns.");
            NormalStrength = F(cfg, S3, "Normal strength", 0.6f, 0f, 2f,
                "Overall ripple depth. Lower it if creases in the map read as hard edges.");
            NormalOpacityMin = F(cfg, S3, "Coarse opacity min", 0.5f, 0f, 1f, "Coarse layer's weakest contribution over its cycle.");
            NormalOpacityMax = F(cfg, S3, "Coarse opacity max", 1f, 0f, 1f, "Coarse layer's strongest contribution over its cycle.");
            DetailNormalOpacityMin = F(cfg, S3, "Detail opacity min", 0.5f, 0f, 1f, "Detail layer's weakest contribution.");
            DetailNormalOpacityMax = F(cfg, S3, "Detail opacity max", 1f, 0f, 1f, "Detail layer's strongest contribution.");
            TexRipple = F(cfg, S3, "Texture ripple weight", 0.4f, 0f, 1f,
                "Balance between the TEXTURE ripple and the PROCEDURAL one. Toward 0 leans on the procedural ripple, which cannot tile or crease.");
            ProcRipple = F(cfg, S3, "Procedural ripple", 0.35f, 0f, 2f,
                "Strength of the generated ripple - no texture, so no repeat and no hard lines.");
            ProcScale = F(cfg, S3, "Procedural ripple scale", 1.2f, 0.05f, 10f,
                "Size of the generated ripple. Going small turns its 3-wave interference into visible hexagons.");
            RippleSpeed = F(cfg, S3, "Ripple speed", 1f, 0f, 5f, "Master rate for the ripple animation.");
            RippleSize = F(cfg, S3, "Ripple size", 1f, 0.05f, 20f, "Divides both layers' tiling. A second scale control, independent of tiling.");
            RippleStrength = F(cfg, S3, "Ripple strength", 1f, 0f, 4f, "Multiplies normal strength. Set 0 for a glass-flat surface.");
            RippleDrift = F(cfg, S3, "Still drift (m per s)", 0.05f, 0f, 2f, "How fast the two layers counter-scroll on STILL water.");
            NormalPhaseSpeed = F(cfg, S3, "Normal phase speed", 1f, 0f, 10f,
                "Rate the two layers cross-fade. This is what makes a river read as moving rather than sliding.");
            NormalCrossSpeed = F(cfg, S3, "Cross speed (coarse)", 1f, 0f, 2f, "Cross-drift of the coarse layer.");
            DetailNormalCrossSpeed = F(cfg, S3, "Cross speed (detail)", 1f, 0f, 2f, "Cross-drift of the detail layer.");

            SwellVisual = F(cfg, S4, "Swell bend vs mesh", 1f, 0f, 20f,
                "1 = the shading slope equals the mesh slope. Above 1 is deliberate exaggeration and is where faceting starts.");
            SwellNormalBend = F(cfg, S4, "Swell normal bend", 1f, 0f, 4f,
                "Gain on the swell's bend of the NORMAL, separate from the mesh displacement. SET 0 to remove the bend entirely and keep the waving - the A/B for whether the bend is what looks wrong.");
            BendClamp = F(cfg, S4, "Swell bend clamp", 0.35f, 0f, 2f,
                "Ceiling on how far the bend may push the normal. A steep per-vertex gradient can catch a highlight on one cell and not its neighbour - the hard-edged bright patch. 0 removes the ceiling.");
            BendShoreDepth = F(cfg, S4, "Bend shore fade depth (m)", 1.5f, 0f, 8f,
                "Column depth below which the bend fades out. The shallows are where the per-vertex gradient changes fastest and facets worst.");
            BendShoreFade = F(cfg, S4, "Bend shore fade amount", 1f, 0f, 1f,
                "How much of that shore fade to apply. 0 keeps the bend at full strength right up to the bank.");
            DistanceFlatten = F(cfg, S4, "Distance flatten", 0.85f, 0f, 1f,
                "How flat the normal goes at reflection-blur distance. This is the anti-sparkle control for far water.");
            NormalFlatFloor = F(cfg, S4, "Normal flat floor", 0.5f, 0f, 0.95f,
                "HARD floor on the normal's up component. Wherever the ripple slope exceeds it the normal stops turning and shades flat - a crease with a visible edge. Raise to flatten, lower to let the map turn freely.");

            FlowStrength = F(cfg, S5, "Flow speed at full flow (m per s)", 2.4f, 0f, 50f,
                "Travel speed of the current where the grade is already at full. The master river-speed dial.");
            FlowResponse = F(cfg, S5, "Flow response", 10f, 0f, 300f,
                "How readily a reach counts as flowing at all. 0 is the master OFF switch - every surface goes still.");
            FlowCycle = F(cfg, S5, "Flow cycle (s)", 2f, 0.05f, 60f,
                "Period of the two-tap advection cross-fade. Short = tighter but more visible pulsing; long = smoother but more smear per tap.");
            FlowSpeedFloor = F(cfg, S5, "Slowest travel (m per s)", 0.002f, 0f, 10f,
                "Floor so a gently graded reach still creeps instead of standing dead.");
            FlowSpeedFull = F(cfg, S5, "Grade that is already full speed", 0.15f, 0.001f, 10f,
                "Steepness at which travel saturates. LOWER makes ordinary reaches run fast.");
            RiverSwell = F(cfg, S5, "Swell kept on flowing water", 0.25f, 0f, 4f,
                "Rivers are ripples, not waves - wind swell needs open fetch a channel has not got. Scales swell down wherever water flows.");
            RiverWhitewater = F(cfg, S5, "River whitewater overlay", 0.15f, 0f, 4f, "Aerated white over fast water.");
            FlowDebug = F(cfg, S5, "Flow debug view", 0f, 0f, 3f,
                "0 off, 1 flow DIRECTION as colour, 2 river weight, 3 coherence. Use 1 to check the current actually runs downstream.");

            SwellAmp = F(cfg, S6, "Swell amplitude (m)", 0.06f, 0f, 2f,
                "How far the MESH itself lifts. This is real vertex displacement, so it is also what can tear a sheet if pushed hard.");
            SwellLength = F(cfg, S6, "Swell wavelength (m)", 9f, 0.5f, 60f, "Distance between crests.");
            SwellSpeed = F(cfg, S6, "Swell speed", 1f, 0f, 5f, "Rate the swell travels.");
            SwellShoreCalm = F(cfg, S6, "Shore calm depth (m)", 1.2f, 0.05f, 10f,
                "Depth over which the swell calms down toward the bank.");
            SwellShoreFloor = F(cfg, S6, "Shore swell floor", 0.3f, 0f, 1f,
                "How much swell survives at the very waterline. 0 stops it dead at the lip; higher keeps the edge waxing and waning.");

            EdgeFoamDepth = F(cfg, S7, "Bank foam width (m)", 0.45f, 0f, 6f, "Column depth the bank collar reaches out to.");
            EdgeFoamOpacity = F(cfg, S7, "Bank foam opacity", 0.55f, 0f, 4f, "Master strength of the bank collar. 0 removes it.");
            EdgeFoamTiling = F(cfg, S7, "Bank foam tiling", 0.5f, 0.01f, 8f, "World tiling of the foam texture at the bank.");
            EdgeFoamScroll = F(cfg, S7, "Bank foam drift (m per s)", 0.045f, 0f, 10f, "How fast the bank foam drifts with the wind.");
            EdgeFoamBase = F(cfg, S7, "Bank foam base", 0.5f, 0f, 4f, "Texture-independent floor. Raise for a solid collar, drop to let the texture break it up.");
            EdgeFoamContrast = F(cfg, S7, "Bank foam texture contrast", 0.7f, 0f, 8f, "How much the foam texture modulates the collar.");
            EdgeFoamGain = F(cfg, S7, "Bank foam gain", 1.4f, 0f, 16f, "Overall gain before clamping. High values make a hard white lip.");
            RapidsFoam = F(cfg, S7, "Rapids foam", 0.6f, 0f, 4f, "Churn on fast water. 0 removes it.");
            RapidsStart = F(cfg, S7, "Rapids flow threshold", 0.16f, 0f, 5f, "Flow magnitude at which churn starts. LOWER = more of the river churns.");
            RapidsRamp = F(cfg, S7, "Rapids ramp sharpness", 6f, 0.05f, 120f, "How abruptly churn arrives past the threshold. High = a hard line between calm and white.");
            RapidsTiling = F(cfg, S7, "Rapids foam tiling", 0.85f, 0.01f, 8f, "World tiling of the churn texture.");
            EddyFoam = F(cfg, S7, "Eddyline foam", 1.3f, 0f, 20f, "Foam where two currents shear past each other.");
            WhitewaterOpacity = F(cfg, S7, "Whitewater opacity", 0.85f, 0f, 1f, "How opaque aerated water goes. You cannot see through a hole in a rapid.");

            RefractStrength = F(cfg, S8, "Refraction (bed wobble)", 0.03f, 0f, 0.2f, "How far the ripple normal pushes the view of the bed.");
            FresnelPower = F(cfg, S8, "Fresnel power", 5f, 1f, 8f, "How sharply reflection takes over at grazing angles. Lower = mirror-like from more angles.");
            ReflStrength = F(cfg, S8, "Reflection strength", 0.35f, 0f, 1f, "Master reflection amount.");
            ReflBlurStart = F(cfg, S8, "Reflection blur start (m)", 100f, 0f, 2000f, "Distance at which far water starts flattening and blurring.");
            ReflBlurRange = F(cfg, S8, "Reflection blur range (m)", 500f, 1f, 4000f, "Distance over which that blur completes.");
            ReflBlurMax = F(cfg, S8, "Reflection blur max mip", 5f, 0f, 7f, "How blurred the far reflection gets.");
            StillReflBoost = F(cfg, S8, "Still water reflection boost", 1.4f, 0f, 3f, "Extra reflection on still water, which is where a mirror actually reads.");
            ReflWaterTint = F(cfg, S8, "Reflection tinted by water", 0.6f, 0f, 1f, "How much the water colour stains what it reflects.");
            Smoothness = F(cfg, S8, "Sun glint sharpness", 0.85f, 0f, 1f, "Tight bright glint at 1, broad sheen at 0.");

            UwDepthFade = F(cfg, S9, "Underwater depth fade (m)", 6f, 0.1f, 60f, "Distance underwater over which the tint reaches full.");
            UwTintStrength = F(cfg, S9, "Underwater tint strength", 1.3f, 0f, 3f, "How strongly the tint stains the view from below.");
            UwSunMaskLow = F(cfg, S9, "Underwater sun preserve start", 0.75f, 0f, 1f, "Where the sun starts to survive the tint.");
            UwSunMaskHigh = F(cfg, S9, "Underwater sun preserve full", 0.97f, 0f, 1f, "Where the sun is fully preserved.");
            UwCaustic = F(cfg, S9, "Underwater caustic dapple", 1f, 0f, 4f, "Light dapple on everything seen from below.");
            UwRefract = F(cfg, S9, "Underwater ripple distortion", 7f, 0f, 20f, "How much the surface ripple bends the view from below.");
            UwSnell = F(cfg, S9, "Snell window sharpness", 0.5f, 0f, 1f, "Sharpness of the bright circle overhead when submerged.");
            UwAlphaMin = F(cfg, S9, "Underwater alpha (shallow)", 0.45f, 0f, 1f, "Opacity of the surface seen from below in shallow water.");
            UwAlphaMax = F(cfg, S9, "Underwater alpha (deep)", 0.85f, 0f, 1f, "Opacity of the surface seen from below in deep water.");

            cfg.SettingChanged += (_, args) =>
            {
                var sec = args?.ChangedSetting?.Definition?.Section;
                if (sec == null || !sec.StartsWith(P)) return;
                try { ReskinLiveSheets(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresInlandWaterConfig] re-skin failed: {ex.Message}"); }
            };

            FiresLogger.LogInfo("[FiresInlandWaterConfig] inland water look bound — 9 sections, hot-reload: " +
                      "colour, clarity, per-layer ripples, normal bend, flow, swell, foam and underwater.");
        }

        public static void Apply(Material m)
        {
            if (m == null || !_bound) return;

            SetC(m, "_ShallowColor", ShallowColor);
            SetC(m, "_DeepColor", DeepColor);
            SetC(m, "_FoamColor", FoamColor);
            SetC(m, "_ReflTint", ReflTint);
            SetC(m, "_SpecColorW", SpecColor);
            SetC(m, "_UwTint", UwTint);

            SetF(m, "_ClearDepth", ClearDepth);
            SetF(m, "_MinOpacity", MinOpacity);
            SetF(m, "_MaxOpacity", MaxOpacity);
            SetF(m, "_EdgeSoftness", EdgeSoftness);
            SetF(m, "_Turbidity", Turbidity);
            SetF(m, "_ShoreChurn", ShoreChurn);
            SetF(m, "_ShoreChurnDepth", ShoreChurnDepth);
            SetF(m, "_ShoreBlend", ShoreBlend);
            SetF(m, "_SeaFade", SeaFade);
            SetF(m, "_UwAirAlphaMax", UwAirAlphaMax);
            SetF(m, "_UwAirTint", UwAirTint);

            SetF(m, "_BumpTiling", BumpTiling);
            SetF(m, "_DetailTiling", DetailTiling);
            SetF(m, "_GlobalScale", GlobalScale);
            SetV(m, "_BumpScroll", BumpScroll);
            SetV(m, "_DetailScroll", DetailScroll);
            SetF(m, "_DetailRotate", DetailRotate);
            SetF(m, "_TileBreak", TileBreak);
            SetF(m, "_NormalStrength", NormalStrength);
            SetF(m, "_NormalOpacityMin", NormalOpacityMin);
            SetF(m, "_NormalOpacityMax", NormalOpacityMax);
            SetF(m, "_DetailNormalOpacityMin", DetailNormalOpacityMin);
            SetF(m, "_DetailNormalOpacityMax", DetailNormalOpacityMax);
            SetF(m, "_TexRipple", TexRipple);
            SetF(m, "_ProcRipple", ProcRipple);
            SetF(m, "_ProcScale", ProcScale);
            SetF(m, "_RippleSpeed", RippleSpeed);
            SetF(m, "_RippleSize", RippleSize);
            SetF(m, "_RippleStrength", RippleStrength);
            SetF(m, "_RippleDrift", RippleDrift);
            SetF(m, "_NormalPhaseSpeed", NormalPhaseSpeed);
            SetF(m, "_NormalCrossSpeed", NormalCrossSpeed);
            SetF(m, "_DetailNormalCrossSpeed", DetailNormalCrossSpeed);

            SetF(m, "_SwellVisual", SwellVisual);
            SetF(m, "_SwellNormalBend", SwellNormalBend);
            SetF(m, "_BendClamp", BendClamp);
            SetF(m, "_BendShoreDepth", BendShoreDepth);
            SetF(m, "_BendShoreFade", BendShoreFade);
            SetF(m, "_DistanceFlatten", DistanceFlatten);
            SetF(m, "_NormalFlatFloor", NormalFlatFloor);

            SetF(m, "_FlowStrength", FlowStrength);
            SetF(m, "_FlowResponse", FlowResponse);
            SetF(m, "_FlowCycle", FlowCycle);
            SetF(m, "_FlowSpeedFloor", FlowSpeedFloor);
            SetF(m, "_FlowSpeedFull", FlowSpeedFull);
            SetF(m, "_RiverSwell", RiverSwell);
            SetF(m, "_RiverWhitewater", RiverWhitewater);
            SetF(m, "_FlowDebug", FlowDebug);

            SetF(m, "_SwellAmp", SwellAmp);
            SetF(m, "_SwellLength", SwellLength);
            SetF(m, "_SwellSpeed", SwellSpeed);
            SetF(m, "_SwellShoreCalm", SwellShoreCalm);
            SetF(m, "_SwellShoreFloor", SwellShoreFloor);

            SetF(m, "_EdgeFoamDepth", EdgeFoamDepth);
            SetF(m, "_EdgeFoamOpacity", EdgeFoamOpacity);
            SetF(m, "_EdgeFoamTiling", EdgeFoamTiling);
            SetF(m, "_EdgeFoamScroll", EdgeFoamScroll);
            SetF(m, "_EdgeFoamBase", EdgeFoamBase);
            SetF(m, "_EdgeFoamContrast", EdgeFoamContrast);
            SetF(m, "_EdgeFoamGain", EdgeFoamGain);
            SetF(m, "_RapidsFoam", RapidsFoam);
            SetF(m, "_RapidsStart", RapidsStart);
            SetF(m, "_RapidsRamp", RapidsRamp);
            SetF(m, "_RapidsTiling", RapidsTiling);
            SetF(m, "_EddyFoam", EddyFoam);
            SetF(m, "_WhitewaterOpacity", WhitewaterOpacity);

            SetF(m, "_RefractStrength", RefractStrength);
            SetF(m, "_FresnelPower", FresnelPower);
            SetF(m, "_ReflStrength", ReflStrength);
            SetF(m, "_ReflBlurStart", ReflBlurStart);
            SetF(m, "_ReflBlurRange", ReflBlurRange);
            SetF(m, "_ReflBlurMax", ReflBlurMax);
            SetF(m, "_StillReflBoost", StillReflBoost);
            SetF(m, "_ReflWaterTint", ReflWaterTint);
            SetF(m, "_Smoothness", Smoothness);

            SetF(m, "_UwDepthFade", UwDepthFade);
            SetF(m, "_UwTintStrength", UwTintStrength);
            SetF(m, "_UwSunMaskLow", UwSunMaskLow);
            SetF(m, "_UwSunMaskHigh", UwSunMaskHigh);
            SetF(m, "_UwCaustic", UwCaustic);
            SetF(m, "_UwRefract", UwRefract);
            SetF(m, "_UwSnell", UwSnell);
            SetF(m, "_UwAlphaMin", UwAlphaMin);
            SetF(m, "_UwAlphaMax", UwAlphaMax);
        }

        private static void ReskinLiveSheets()
        {
            Apply(VerdantsAscent.Terrain.Voxel.VoxelChunkView.CurrentSheetMaterial);
        }

        private static void SetF(Material m, string p, ConfigEntry<float> e)
        {
            if (e != null && m.HasProperty(p)) m.SetFloat(p, e.Value);
        }
        private static void SetC(Material m, string p, ConfigEntry<Color> e)
        {
            if (e != null && m.HasProperty(p)) m.SetColor(p, e.Value);
        }
        private static void SetV(Material m, string p, ConfigEntry<Vector2> e)
        {
            if (e != null && m.HasProperty(p)) m.SetVector(p, new Vector4(e.Value.x, e.Value.y, 0f, 0f));
        }
    }
}
