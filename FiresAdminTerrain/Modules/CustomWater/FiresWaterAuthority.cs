using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// THE water authority — FAT owns the water clock AND the wave function.
    ///
    /// FAT runs on the client AND the dedicated server, where boat/float physics
    /// is authoritative, so the water system lives here (FiresTossinShade owns
    /// only the shaders/bundle; Galaxies rides on top). Two things vanilla used
    /// to own, now ours:
    ///
    ///   CLOCK (FiresWaterClock) — a MONOTONIC double, seeded once from net time,
    ///     advanced by dt forever forward. Vanilla lerps its visual time toward
    ///     net-synced wrapped day time, which is nudged BACKWARD by every sync
    ///     correction and wraps at midnight → the swell visibly REWINDS (user:
    ///     "waves rewind... jarring"). We don't correct that, we replace it: time
    ///     never rolls backward, so the swell never does. The clock drives the
    ///     shader global (_WaterTime) and both vanilla statics — s_waterTime
    ///     (zone-water visual) and s_wrappedDayTimeSeconds (what CalcWave floats
    ///     boats on) — so visuals and physics read the IDENTICAL time.
    ///
    ///   WAVES (FiresWaveField) — our own CalcWave, a C# port of the shader's
    ///     CalcWaveFromWind (Custom/FiresLiquid), bit-for-bit vanilla's trochoid
    ///     stack TODAY (same 10 components, directions, speeds, sharpness) so
    ///     boats float exactly as before. It exists so that when we CHANGE the
    ///     wave shape (the Crest ocean work), it is ONE C# edit here + the
    ///     matching shader edit — physics and visuals stay married by us, not by
    ///     hoping vanilla's frozen CalcWave happens to agree.
    ///
    /// Gate: OwnWaves config (default on) AND the cross-mod master (_FiresMasterFxOff
    /// global, set by FTS's master toggle). Master off / OwnWaves off → both
    /// patches fall through to vanilla, bit-exact.
    /// </summary>
    public static class FiresWaterAuthority
    {
        public static ConfigEntry<bool> OwnWaves;
        private static int s_propMasterOff;

        public static void Bind(ConfigFile cfg)
        {
            OwnWaves = cfg.Bind("03 - Water Engine", "10 OwnWavePhysics", true,
                "Fires owns the wave math and the water clock (replacing vanilla's). The waves are identical "
                + "to vanilla's today, so this changes nothing visible on its own — it keeps boat/float PHYSICS "
                + "married to our rendered swell and stops the wave 'rewind' from vanilla's net-synced clock. "
                + "Turn off to hand waves + time back to vanilla exactly.");
            s_propMasterOff = Shader.PropertyToID("_FiresMasterFxOff");
        }

        internal static bool Active =>
            (OwnWaves == null || OwnWaves.Value)
            && !(s_propMasterOff != 0 && Shader.GetGlobalFloat(s_propMasterOff) > 0.5f);
    }

    /// <summary>
    /// Monotonic water clock. Forward only, forever. See FiresWaterAuthority.
    /// </summary>
    internal static class FiresWaterClock
    {
        private const double DAY = 86400.0;   // float-precision guard = vanilla's wrap period
        private static double s_clock;        // monotonic accumulator (double: no precision loss)
        private static bool s_init;
        private static int s_frame = -1;
        private static float s_out;           // wrapped float handed out this frame

        // Current wrapped water time (advances at most once per frame, any caller).
        internal static float Tick(float dt)
        {
            if (s_frame == Time.frameCount) return s_out;
            s_frame = Time.frameCount;
            if (!s_init)
            {
                s_init = true;
                s_clock = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0.0;
            }
            else
            {
                s_clock += Mathf.Max(0f, dt);   // forward only, always
            }
            s_out = (float)(s_clock % DAY);
            return s_out;
        }

        // Read without advancing — for CalcWave mid-frame (the clock was already
        // ticked this frame by the UpdateWaterTime postfix).
        internal static float Now => s_init ? s_out : 0f;
    }

    /// <summary>
    /// Our wave function — the trochoid stack, C# side. Port of the shader's
    /// CalcWaveFromWind (which is itself a verbatim copy of vanilla's CalcWave):
    /// keep the two in lockstep whenever the wave shape changes.
    /// </summary>
    internal static class FiresWaveField
    {
        // Fixed component directions [1..9] (component [0] follows the wind).
        // Same constants as vanilla WaterVolume.s_createWaveDirections and the
        // shader's d1..d9.
        private static readonly Vector2[] s_dir = BuildDirs();
        private static readonly Vector2[] s_tan = BuildTans(s_dir);

        private static Vector2[] BuildDirs()
        {
            var d = new[]
            {
                new Vector2( 1.0312f,  0.312f),   // [0] slot, overwritten by wind each call
                new Vector2( 1.0312f,  0.312f),
                new Vector2(-0.123f,   1.12f),
                new Vector2( 0.423f,   0.124f),
                new Vector2( 0.123f,  -0.64f),
                new Vector2(-0.523f,  -0.64f),
                new Vector2( 0.223f,   0.74f),
                new Vector2( 0.923f,  -0.24f),
                new Vector2(-0.323f,   0.44f),
                new Vector2( 0.5312f, -0.812f),
            };
            for (int i = 0; i < d.Length; i++) d[i] = d[i].normalized;
            return d;
        }

        private static Vector2[] BuildTans(Vector2[] d)
        {
            var t = new Vector2[d.Length];
            for (int i = 0; i < d.Length; i++) t[i] = new Vector2(-d[i].y, d[i].x);
            return t;
        }

        private static float TrochSin(float x, float k) => Mathf.Sin(x - Mathf.Cos(x) * k) * 0.5f + 0.5f;

        private static float CreateWave(Vector3 worldPos, float time, float speed, float length,
            float height, Vector2 dir, Vector2 tan, float sharp)
        {
            Vector2 v = -(worldPos.z * dir + worldPos.x * tan);
            float n = time * speed;
            return (TrochSin(n + v.y * length, sharp)
                  * TrochSin(n * 0.123f + v.x * 0.13123f * length, sharp) - 0.2f) * height;
        }

        // One wind's contribution. `wind` = (dirX, _, dirZ, strength).
        // Mirrors WaterVolume.CalcWave verbatim. 1.0 added waveFactorBig, which scales the six LONG-swell
        // waves that used to be fixed-amplitude; waveFactor still scales the four short ones.
        internal static float FromWind(Vector3 worldPos, float depth, Vector4 wind, float time, float waveFactor, float waveFactorBig)
        {
            Vector2 dir0 = new Vector2(wind.x, wind.z).normalized;
            Vector2 tan0 = new Vector2(-dir0.y, dir0.x);
            float strength = Mathf.Lerp(0f, wind.w, depth);
            float t = time / 20f;

            float r = 0f;
            r += CreateWave(worldPos, t, 10.0f,    0.04f, 8.0f * waveFactorBig,  dir0,     tan0,     0.5f);
            r += CreateWave(worldPos, t, 14.123f,  0.08f, 6.0f * waveFactorBig,  s_dir[1], s_tan[1], 0.5f);
            r += CreateWave(worldPos, t, 22.312f,  0.10f, 4.0f * waveFactorBig,  s_dir[2], s_tan[2], 0.5f);
            r += CreateWave(worldPos, t, 31.42f,   0.20f, 2.0f * waveFactorBig,  s_dir[3], s_tan[3], 0.5f);
            r += CreateWave(worldPos, t, 35.42f,   0.40f, 1.0f * waveFactorBig,  s_dir[4], s_tan[4], 0.5f);
            r += CreateWave(worldPos, t, 38.1223f, 1.00f, 0.8f * waveFactorBig,  s_dir[5], s_tan[5], 0.7f);
            r += CreateWave(worldPos, t, 41.1223f, 1.20f, 0.6f * waveFactor,  s_dir[6], s_tan[6], 0.8f);
            r += CreateWave(worldPos, t, 51.5123f, 1.30f, 0.4f * waveFactor,  s_dir[7], s_tan[7], 0.9f);
            r += CreateWave(worldPos, t, 54.2f,    1.30f, 0.3f * waveFactor,  s_dir[8], s_tan[8], 0.9f);
            r += CreateWave(worldPos, t, 56.123f,  1.50f, 0.2f * waveFactor,  s_dir[9], s_tan[9], 0.9f);
            return r * strength;
        }
    }

    // ---- Patch: own the water clock (drives BOTH vanilla statics + the shader
    //      global). Runs every frame via WaterVolume.StaticUpdate -> here.
    [HarmonyPatch(typeof(WaterVolume), "UpdateWaterTime")]
    internal static class Patch_WaterVolume_WaterTime
    {
        private static readonly AccessTools.FieldRef<float> s_waterTime =
            AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(WaterVolume), "s_waterTime"));
        private static readonly AccessTools.FieldRef<float> s_wrapped =
            AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(WaterVolume), "s_wrappedDayTimeSeconds"));
        private static int s_propWaterTime;
        private static bool s_isClient;
        private static bool s_devInit;

        private static void Postfix(float dt)
        {
            if (!FiresWaterAuthority.Active) return;
            try
            {
                float t = FiresWaterClock.Tick(dt);
                s_waterTime() = t;      // vanilla zone-water VISUAL
                s_wrapped() = t;        // CalcWave boat/swim PHYSICS — one clock
                if (!s_devInit)
                {
                    s_devInit = true;
                    s_propWaterTime = Shader.PropertyToID("_WaterTime");
                    s_isClient = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
                }
                if (s_isClient) Shader.SetGlobalFloat(s_propWaterTime, t);   // our water shaders
            }
            catch { }
        }
    }

    // ---- Patch: own the wave function. The public CalcWave is what
    //      GetWaterSurface (boats, swim, Floating.GetLiquidLevel) calls.
    //      Reproduces vanilla's global-wind blend exactly, through OUR field.
    [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.CalcWave),
        new[] { typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(float) })]
    internal static class Patch_WaterVolume_CalcWave
    {
        private static readonly AccessTools.FieldRef<Vector4> s_wind1 =
            AccessTools.StaticFieldRefAccess<Vector4>(AccessTools.Field(typeof(WaterVolume), "s_globalWind1"));
        private static readonly AccessTools.FieldRef<Vector4> s_wind2 =
            AccessTools.StaticFieldRefAccess<Vector4>(AccessTools.Field(typeof(WaterVolume), "s_globalWind2"));
        private static readonly AccessTools.FieldRef<float> s_windAlpha =
            AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(WaterVolume), "s_globalWindAlpha"));

        // 1.0 split the wave amplitude in two: waveFactor still scales the small ripples, waveFactorBig the
        // long swell. Both have to be forwarded or our field drifts from vanilla's at high sea states.
        private static bool Prefix(Vector3 worldPos, float depth, float waterTime, float waveFactor, float waveFactorBig, ref float __result)
        {
            if (!FiresWaterAuthority.Active) return true;   // vanilla owns it
            float alpha = s_windAlpha();
            if (alpha == 0f)
                __result = FiresWaveField.FromWind(worldPos, depth, s_wind1(), waterTime, waveFactor, waveFactorBig);
            else
                __result = Mathf.LerpUnclamped(
                    FiresWaveField.FromWind(worldPos, depth, s_wind1(), waterTime, waveFactor, waveFactorBig),
                    FiresWaveField.FromWind(worldPos, depth, s_wind2(), waterTime, waveFactor, waveFactorBig),
                    alpha);
            return false;
        }
    }
}
