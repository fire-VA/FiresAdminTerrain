using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // Rainbows from the optics (Fire 10-05: "rainbows can be explained with math so we should be able to map this out
    // correctly to be realistic"). Light enters a drop, reflects once (primary) or twice (secondary) and leaves at the
    // angle of minimum deviation, which depends on water's refractive index and so on the wavelength. That angle, measured
    // from the point opposite the sun, is the bow's radius: about 40.6 deg violet to 42.4 deg red for the primary, and about
    // 50.4 to 53.6 deg with the colours reversed for the secondary. FiresRainbow works those out once per wavelength, finds
    // the TRUE sun (Valheim turns its directional light round by 180 degrees for moonlight whenever night light is mixed in,
    // so the light itself is no use), and hands both to Custom/FiresWaterfallMist (the bow in a fall's spray) and
    // Custom/FiresRainbowSky (the bow in the sky after rain) through shader globals; FiresRainbow.cginc does the drawing.
    internal static class FiresRainbow
    {
        private const string Section = "Rainbow";
        private const int Samples = 13;
        private const float FirstNm = 400f, LastNm = 700f;
        // Water's refractive index, Cauchy's fit: n(400 nm) = 1.343, n(700 nm) = 1.330.
        private const float CauchyA = 1.3236f, CauchyB = 3094f;
        // Only a canvas: the shader puts the bow on the far plane (Fire 10-05, skylands), so this distance does not show.
        private const float SkyDistance = 200f;
        // Fire 10-05: "a little intense" - the sky bow at 0.6 of the configured intensity.
        private const float SkyBrightness = 0.6f;
        private const float SkyHalfAngle = 65f;
        private const int SkyQueue = 2501;
        private const float FadeInSeconds = 30f, FadeOutSeconds = 60f;
        private const float ReportSeconds = 30f;
        private const float FogSyncSeconds = 1f;

        private static readonly Vector4[] s_spectrum = new Vector4[Samples];
        private static readonly Vector4[] s_angles = new Vector4[Samples];
        private static Vector4 s_means;
        private static float s_primaryViolet, s_primaryRed, s_secondaryRed, s_secondaryViolet;

        private static readonly int SunId = Shader.PropertyToID("_FiresBowSun");
        private static readonly int SunColorId = Shader.PropertyToID("_FiresBowSunColor");
        private static readonly int SpectrumId = Shader.PropertyToID("_FiresBowSpectrum");
        private static readonly int AnglesId = Shader.PropertyToID("_FiresBowAngles");
        private static readonly int MeansId = Shader.PropertyToID("_FiresBowMeans");
        private static readonly int ShapeId = Shader.PropertyToID("_FiresBowShape");
        private static readonly int FadeId = Shader.PropertyToID("_Fade");
        private static readonly int FogModeId = Shader.PropertyToID("_FiresFogMode");

        private static ConfigEntry<bool> s_enabled, s_secondary, s_skyEnabled;
        private static ConfigEntry<float> s_bowSize, s_bandThickness, s_skyBowSize, s_skyBandThickness, s_mistBowSize, s_mistBandThickness;
        private static ConfigEntry<float> s_skyChance, s_skyMinutes, s_skyIntensity, s_mistIntensity;

        private static GameObject s_driver;
        private static GameObject s_sky;
        private static Material s_skyMaterial;
        private static bool s_skyTried;
        private static bool s_wasWet, s_wetKnown;
        private static float s_skyStart = -1f, s_skyUntil = -1f;
        private static string s_skyCause = "";
        private static float s_nextReport, s_nextFogSync;

        private sealed class Driver : MonoBehaviour
        {
            private void LateUpdate() => Tick();
        }

        internal static void Init(ConfigFile config)
        {
            if (s_driver != null || config == null) return;
            s_enabled = config.Bind(Section, "Enabled", true, "Rainbows from the optics: in waterfall mist, and in the sky after rain.");
            s_bowSize = config.Bind(Section, "Bow size", 1f, new ConfigDescription(
                "Multiplier on the bow's angular radius (1 = the physical 42 degrees). The secondary and the dark band scale with it.",
                new AcceptableValueRange<float>(0.5f, 2f)));
            s_bandThickness = config.Bind(Section, "Band thickness", 1f, new ConfigDescription(
                "Multiplier on the width of the colour band (1 = the physical red-to-violet spread, about 1.8 degrees).",
                new AcceptableValueRange<float>(0.5f, 4f)));
            // Defaults are Fire's own tuning (10-06: "this intensity should be the default ... as well as a single rainbow").
            s_secondary = config.Bind(Section, "Secondary bow", false, "Draw the secondary bow (about 51 degrees, colours reversed) and the dark band between the two.");
            s_skyEnabled = config.Bind(Section, "Sky rainbow", true, "A rainbow in the sky when rain ends while the sun is out.");
            s_skyChance = config.Bind(Section, "Sky rainbow chance", 0.6f, new ConfigDescription(
                "Chance that a rain ending gives a sky rainbow.", new AcceptableValueRange<float>(0f, 1f)));
            s_skyMinutes = config.Bind(Section, "Sky rainbow minutes", 6f, new ConfigDescription(
                "How long a sky rainbow lasts after the rain ends (real minutes), fades included.", new AcceptableValueRange<float>(1f, 30f)));
            s_skyIntensity = config.Bind(Section, "Sky rainbow intensity", 0.28f, new ConfigDescription(
                "Brightness of the sky rainbow.", new AcceptableValueRange<float>(0f, 3f)));
            s_skyBowSize = config.Bind(Section, "Sky bow size", 0f, new ConfigDescription(
                "Sky rainbow only: overrides Bow size (0 = use Bow size).", new AcceptableValueRange<float>(0f, 2f)));
            s_skyBandThickness = config.Bind(Section, "Sky band thickness", 0f, new ConfigDescription(
                "Sky rainbow only: overrides Band thickness (0 = use Band thickness).", new AcceptableValueRange<float>(0f, 4f)));
            s_mistIntensity = config.Bind(Section, "Mist rainbow intensity", 1f, new ConfigDescription(
                "Brightness of the rainbow in waterfall mist.", new AcceptableValueRange<float>(0f, 3f)));
            s_mistBowSize = config.Bind(Section, "Mist bow size", 0f, new ConfigDescription(
                "Waterfall mist only: overrides Bow size (0 = use Bow size).", new AcceptableValueRange<float>(0f, 2f)));
            s_mistBandThickness = config.Bind(Section, "Mist band thickness", 0f, new ConfigDescription(
                "Waterfall mist only: overrides Band thickness (0 = use Band thickness).", new AcceptableValueRange<float>(0f, 4f)));

            BuildTable();
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            s_driver = new GameObject("FiresRainbow");
            Object.DontDestroyOnLoad(s_driver);
            s_driver.hideFlags = HideFlags.HideAndDontSave;
            s_driver.AddComponent<Driver>();
            FiresLogger.LogDebug($"[Rainbow] optics: primary {s_primaryViolet:0.00} (violet) - {s_primaryRed:0.00} (red) deg, secondary {s_secondaryRed:0.00} (red) - "
                + $"{s_secondaryViolet:0.00} (violet) deg from the antisolar point; {Samples} wavelengths {FirstNm:0}-{LastNm:0} nm");
        }

        // Per wavelength: water's index n, the primary bow's radius 180 - D1 and the secondary's D2 - 180, where D is the
        // minimum deviation after k internal reflections: cos^2 i = (n^2 - 1) / (k^2 + 2k), D = 180k + 2i - 2(k + 1)r.
        private static void BuildTable()
        {
            Vector3 sum = Vector3.zero;
            float primarySum = 0f, secondarySum = 0f;
            for (int s = 0; s < Samples; s++)
            {
                float nm = Mathf.Lerp(FirstNm, LastNm, s / (float)(Samples - 1));
                float n = CauchyA + CauchyB / (nm * nm);
                float primary = 180f - Deviation(n, 1);
                float secondary = Deviation(n, 2) - 180f;
                Vector3 rgb = WavelengthToLinear(nm);
                s_angles[Samples - 1 - s] = new Vector4(primary, secondary, nm, 0f);
                s_spectrum[Samples - 1 - s] = new Vector4(rgb.x, rgb.y, rgb.z, 0f);
                sum += rgb;
                primarySum += primary;
                secondarySum += secondary;
            }
            // Index 0 is the red end, the last the violet end (the cginc reads the edges that way). Weights sum to white.
            for (int s = 0; s < Samples; s++)
            {
                var v = s_spectrum[s];
                s_spectrum[s] = new Vector4(v.x / Mathf.Max(sum.x, 1e-4f), v.y / Mathf.Max(sum.y, 1e-4f), v.z / Mathf.Max(sum.z, 1e-4f), 0f) * 3f;
            }
            s_means = new Vector4(primarySum / Samples, secondarySum / Samples, 0f, 0f);
            s_primaryRed = s_angles[0].x;
            s_primaryViolet = s_angles[Samples - 1].x;
            s_secondaryRed = s_angles[0].y;
            s_secondaryViolet = s_angles[Samples - 1].y;
        }

        private static float Deviation(float n, int reflections)
        {
            float k = reflections;
            float cosI = Mathf.Sqrt((n * n - 1f) / (k * k + 2f * k));
            float i = Mathf.Acos(cosI);
            float r = Mathf.Asin(Mathf.Sin(i) / n);
            return (180f * k + 2f * i * Mathf.Rad2Deg - 2f * (k + 1f) * r * Mathf.Rad2Deg) % 360f;
        }

        // A visible wavelength as linear RGB (Bruton's piecewise fit, faded at the ends of the eye's range).
        private static Vector3 WavelengthToLinear(float nm)
        {
            float r, g, b;
            if (nm < 440f) { r = -(nm - 440f) / 60f; g = 0f; b = 1f; }
            else if (nm < 490f) { r = 0f; g = (nm - 440f) / 50f; b = 1f; }
            else if (nm < 510f) { r = 0f; g = 1f; b = -(nm - 510f) / 20f; }
            else if (nm < 580f) { r = (nm - 510f) / 70f; g = 1f; b = 0f; }
            else if (nm < 645f) { r = 1f; g = -(nm - 645f) / 65f; b = 0f; }
            else { r = 1f; g = 0f; b = 0f; }
            float edge = nm < 420f ? 0.3f + 0.7f * (nm - 380f) / 40f : nm > 680f ? 0.3f + 0.7f * (720f - nm) / 40f : 1f;
            return new Vector3(Mathf.GammaToLinearSpace(r * edge), Mathf.GammaToLinearSpace(g * edge), Mathf.GammaToLinearSpace(b * edge));
        }

        private static float Shared(ConfigEntry<float> own, ConfigEntry<float> shared) => own.Value > 0f ? own.Value : shared.Value;

        // The direction TO the sun by EnvMan's own formula, without the 180 degree turn it gives m_dirLight at night.
        private static bool TrueSun(out Vector3 toSun, out Color light, out EnvSetup env)
        {
            toSun = Vector3.up;
            light = Color.black;
            var man = EnvMan.instance;
            env = man != null ? man.GetCurrentEnvironment() : null;
            if (man == null || env == null) return false;
            Quaternion rotation = Quaternion.Euler(-90f + env.m_sunAngle, 0f, 0f) * Quaternion.Euler(0f, -90f, 0f)
                * Quaternion.Euler(-90f + 360f * man.m_smoothDayFraction, 0f, 0f);
            toSun = -(rotation * Vector3.forward);
            float altitude = Mathf.Asin(Mathf.Clamp(toSun.y, -1f, 1f)) * Mathf.Rad2Deg;
            float up = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-1f, 5f, altitude));
            light = env.m_alwaysDark ? Color.black : env.m_sunColorDay * (env.m_lightIntensityDay * up);
            return true;
        }

        private static void Tick()
        {
            if (s_enabled == null) return;
            var camera = Utils.GetMainCamera();
            bool sun = TrueSun(out Vector3 toSun, out Color light, out EnvSetup env);
            float sunLight = s_enabled.Value && sun ? Mathf.Max(light.r, Mathf.Max(light.g, light.b)) : 0f;
            Shader.SetGlobalVector(SunId, new Vector4(toSun.x, toSun.y, toSun.z, sunLight));
            Shader.SetGlobalVector(SunColorId, sunLight > 0f ? (Vector4)(light / sunLight) : Vector4.zero);
            Shader.SetGlobalVectorArray(SpectrumId, s_spectrum);
            Shader.SetGlobalVectorArray(AnglesId, s_angles);
            Shader.SetGlobalVector(MeansId, s_means);

            var mistShape = new Vector4(Shared(s_mistBowSize, s_bowSize), Shared(s_mistBandThickness, s_bandThickness), s_mistIntensity.Value, s_secondary.Value ? 1f : 0f);
            var veil = FiresWaterfalls.VeilMaterial;
            if (veil != null) veil.SetVector(ShapeId, mistShape);
            var mist = FiresWaterfalls.MistMaterial;
            if (mist != null) mist.SetVector(ShapeId, mistShape);

            float fade = SkyFade(env, toSun, sunLight, out string why);
            DrawSky(camera, toSun, fade);

            if (Time.realtimeSinceStartup >= s_nextReport && camera != null && sun)
            {
                s_nextReport = Time.realtimeSinceStartup + ReportSeconds;
                Report(camera, toSun, sunLight, fade, why, mistShape);
            }
        }

        // The sky bow's fade: started when the environment stops being wet (a chance roll), up for the configured minutes
        // with a fade in and out. why says why it shows or not, for the Debug line.
        private static float SkyFade(EnvSetup env, Vector3 toSun, float sunLight, out string why)
        {
            float now = Time.time;
            bool wet = env != null && (env.m_isWet || Has(env.m_name, "rain") || Has(env.m_name, "storm"));
            if (s_wetKnown && s_wasWet && !wet)
            {
                bool roll = s_skyEnabled.Value && Random.value < s_skyChance.Value;
                if (roll)
                {
                    s_skyStart = now;
                    s_skyUntil = now + s_skyMinutes.Value * 60f;
                }
                s_skyCause = $"rain ended ({(env != null ? env.m_name : "?")}), chance {s_skyChance.Value:0.00}: {(roll ? "rolled a bow" : "no bow this time")}";
                FiresLogger.LogDebug($"[Rainbow] {s_skyCause}");
            }
            s_wasWet = wet;
            s_wetKnown = env != null;

            if (!s_enabled.Value || !s_skyEnabled.Value) { why = "sky rainbow off in the config"; return 0f; }
            if (now >= s_skyUntil || s_skyStart < 0f) { why = s_skyCause.Length > 0 ? $"not shown: no bow running ({s_skyCause})" : "not shown: no rain has ended yet"; return 0f; }
            if (sunLight <= 0f) { why = "not shown: the sun is down"; return 0f; }
            float altitude = Mathf.Asin(Mathf.Clamp(toSun.y, -1f, 1f)) * Mathf.Rad2Deg;
            float radius = s_primaryRed * Shared(s_skyBowSize, s_bowSize);
            why = altitude < radius
                ? $"visible because a bow is running ({s_skyCause}) and the sun is {altitude:0.0} deg up, under the bow's {radius:0.0} deg, so its top stands {radius - altitude:0.0} deg above the horizon"
                : $"running ({s_skyCause}) but the sun is {altitude:0.0} deg up, over the bow's {radius:0.0} deg: the whole bow is below the horizon";
            float fadeIn = Mathf.Clamp01((now - s_skyStart) / FadeInSeconds);
            float fadeOut = Mathf.Clamp01((s_skyUntil - now) / FadeOutSeconds);
            return Mathf.Min(fadeIn, fadeOut) * s_skyIntensity.Value * SkyBrightness;
        }

        private static void DrawSky(Camera camera, Vector3 toSun, float fade)
        {
            if (fade <= 0f || camera == null)
            {
                if (s_sky != null && s_sky.activeSelf) s_sky.SetActive(false);
                return;
            }
            if (s_sky == null && !Create()) return;
            float distance = Mathf.Min(SkyDistance, camera.farClipPlane * 0.5f);
            Vector3 antisolar = -toSun;
            s_sky.transform.SetPositionAndRotation(camera.transform.position + antisolar * distance, Quaternion.LookRotation(antisolar, Vector3.up));
            float side = 2f * distance * Mathf.Tan(SkyHalfAngle * Mathf.Deg2Rad);
            s_sky.transform.localScale = new Vector3(side, side, 1f);
            s_skyMaterial.SetFloat(FadeId, fade);
            s_skyMaterial.SetVector(ShapeId, new Vector4(Shared(s_skyBowSize, s_bowSize), Shared(s_skyBandThickness, s_bandThickness), 1f, s_secondary.Value ? 1f : 0f));
            if (Time.time >= s_nextFogSync)
            {
                s_nextFogSync = Time.time + FogSyncSeconds;
                s_skyMaterial.SetFloat(FogModeId, !RenderSettings.fog ? 0f : RenderSettings.fogMode == FogMode.Linear ? 1f
                    : RenderSettings.fogMode == FogMode.Exponential ? 2f : 3f);
            }
            if (!s_sky.activeSelf) s_sky.SetActive(true);
        }

        private static bool Create()
        {
            if (s_skyTried) return false;
            s_skyTried = true;
            var shader = FiresWaterfalls.LoadShader("FiresRainbowSky");
            if (shader == null || !shader.isSupported)
            {
                FiresLogger.LogWarning($"[Rainbow] Custom/FiresRainbowSky {(shader == null ? "not in the fireswaterfall bundle" : "not supported on this GPU")} - no sky rainbow.");
                return false;
            }
            s_skyMaterial = new Material(shader) { name = "FiresRainbowSky", renderQueue = SkyQueue };
            var mesh = new Mesh { name = "FiresRainbowSky" };
            mesh.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) });
            mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 1f));
            s_sky = new GameObject("FiresRainbowSky");
            Object.DontDestroyOnLoad(s_sky);
            s_sky.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = s_sky.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = s_skyMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            FiresLogger.LogDebug($"[Rainbow] sky bow material ready ({shader.name}, queue {SkyQueue}).");
            return true;
        }

        private static void Report(Camera camera, Vector3 toSun, float sunLight, float fade, string why, Vector4 mistShape)
        {
            float altitude = Mathf.Asin(Mathf.Clamp(toSun.y, -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 antisolar = -toSun;
            float skySize = Shared(s_skyBowSize, s_bowSize), skyThick = Shared(s_skyBandThickness, s_bandThickness);
            FiresLogger.LogDebug($"[Rainbow] sky bow: sun alt {altitude:0.0} deg, antisolar ({antisolar.x:0.00},{antisolar.y:0.00},{antisolar.z:0.00}), "
                + $"primary radius {s_primaryRed * skySize:0.0} deg (size {skySize:0.00}, thickness {skyThick:0.00}), fade {fade:0.00} (x{SkyBrightness:0.0} brightness), "
                + $"camera at y {camera.transform.position.y:0}, drawn on the sky (far plane) as an arc fading out over the last 12 deg above the horizon; {why}");

            // The mist bow's view rule, then and now: what the old shader took as the sun (Valheim's directional light) against
            // the true sun, and where the camera looks from the antisolar point.
            var man = EnvMan.instance;
            Vector3 toLight = man != null && man.m_dirLight != null ? -man.m_dirLight.transform.forward : toSun;
            float turned = Vector3.Angle(toLight, toSun);
            float lookAngle = Vector3.Angle(camera.transform.forward, antisolar);
            float radius = s_primaryRed * mistShape.x;
            FiresLogger.LogDebug($"[Rainbow] mist bow: sun alt {altitude:0.0} deg, sunlight {sunLight:0.00}, antisolar ({antisolar.x:0.00},{antisolar.y:0.00},{antisolar.z:0.00}), "
                + $"primary radius {radius:0.0} deg (size {mistShape.x:0.00}, thickness {mistShape.y:0.00}); the camera looks {lookAngle:0.0} deg from the antisolar point "
                + $"({(Mathf.Abs(lookAngle - radius) < 20f ? "the bow is in view where there is mist" : lookAngle < radius ? "inside the bow" : "outside the bow")}); "
                + $"Valheim's light is turned {turned:0} deg from the true sun{(turned > 90f ? " (flipped for moonlight: the old mist bow sat around the SUN)" : "")}");
        }

        private static bool Has(string text, string part) => text != null && text.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
