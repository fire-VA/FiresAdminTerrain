using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// 0.2.1448 (Fire 11-16: "fat needs the code for its own custom water, but vanilla oceans and water will be handled by vedr or vanilla or any
    /// other mod hooking them"; [lead]-approved list). FAT no longer patches vanilla water. Removed from this file: the WaterVolume.UpdateWaterTime
    /// postfix that rewrote vanilla's s_waterTime / s_wrappedDayTimeSeconds, the WaterVolume.CalcWave prefix (FAT's wave function, the sea-wind
    /// swap, the wave multiplier and the calm scaling), and the WaterVolume.Depth postfix (FiresWaveDepthMatch, its probe RPC and the Galaxies tide
    /// read). Vedr owns the sea's waves, tides, clock, wind and calm-under-ice physics.
    ///
    /// What stays, FAT's own water only:
    ///  - FiresWaterClock: FAT's private monotonic clock, read by FAT's voxel water (VoxelWaterSurface) and fed to FAT's liquid materials;
    ///  - the _WaterTime shader GLOBAL as a fallback only (vanilla never sets it globally; it sets each volume's own material): written while no
    ///    Vedr water clock owns it (Vedr.VedrApi.WaterClockActive, bound by name), so the shaders that read it (FTS / EBM / Vedr water, caustics,
    ///    fog, FAT's inland water) keep moving until Vedr writes it;
    ///  - the GetWaterSurface amplitude postfix, now gated to FAT liquid volumes only (surface material _FiresLiquidGrid = 1).
    /// </summary>
    public static class FiresWaterAuthority
    {
        public static void Bind(ConfigFile cfg)
        {
            // 0.2.1448: "[03 - Water Engine] 10 OwnWavePhysics / 20 WaveIntensity / 21 WaveWeatherMap" are no longer bound (FAT does not touch the sea).
        }

        private static readonly int s_propWaterTime = Shader.PropertyToID("_WaterTime");
        private static System.Func<bool> s_vedrClock;
        private static bool s_vedrLooked, s_saidOwner;
        private static bool? s_lastWrote;

        /// <summary>Main thread, from FAT's Game.Update postfix: ticks FAT's private clock and, while no Vedr clock owns it, the _WaterTime global.</summary>
        internal static void Tick()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { FiresWaterClock.Tick(Time.deltaTime); return; }
            float t = FiresWaterClock.Tick(Time.deltaTime);
            bool vedrOwns = VedrOwnsClock();
            if (!vedrOwns) Shader.SetGlobalFloat(s_propWaterTime, t);
            if (s_lastWrote != !vedrOwns || !s_saidOwner)
            {
                s_saidOwner = true; s_lastWrote = !vedrOwns;
                FiresLogger.LogInfo(vedrOwns
                    ? "[FiresWater] _WaterTime global: Vedr's water clock owns it; FAT leaves it alone (0.2.1448)"
                    : "[FiresWater] _WaterTime global: no Vedr water clock (VedrApi.WaterClockActive), FAT writes it as a fallback; vanilla's own water statics are untouched (0.2.1448)");
            }
        }

        private static bool VedrOwnsClock()
        {
            if (s_vedrClock == null)
            {
                if (s_vedrLooked && !ReferenceEquals(ZNetScene.instance, null)) return false;   // looked after a world was up: absent for the session
                s_vedrLooked = !ReferenceEquals(ZNetScene.instance, null);
                try
                {
                    var t = FatSoftTypes.Find("Vedr.VedrApi");
                    var p = t?.GetProperty("WaterClockActive", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (p != null && p.PropertyType == typeof(bool))
                        s_vedrClock = (System.Func<bool>)System.Delegate.CreateDelegate(typeof(System.Func<bool>), p.GetGetMethod());
                }
                catch { s_vedrClock = null; }
                if (s_vedrClock == null) return false;
            }
            try { return s_vedrClock(); } catch { s_vedrClock = null; return false; }
        }
    }

    /// <summary>
    /// FAT's private monotonic water clock (no longer written into vanilla's statics). Advances by real time once per frame; snaps to net time
    /// when a session starts, then leans toward it by at most a tenth of a step, never backward. The dedicated server follows net time.
    /// </summary>
    internal static class FiresWaterClock
    {
        private const double DAY = 86400.0;
        private const double MaxSlew = 0.1;
        private const double SnapAheadSeconds = 2.0;
        private static double s_clock;
        private static bool s_init;
        private static double s_lastReal = -1.0;
        private static ZNet s_session;
        private static int s_frame = -1;
        private static float s_out;

        internal static float Tick(float callerDt)
        {
            if (s_frame == Time.frameCount) return s_out;
            s_frame = Time.frameCount;
            s_init = true;
            double real = Time.unscaledTimeAsDouble;
            double step = s_lastReal < 0.0 ? 0.0 : System.Math.Max(0.0, real - s_lastReal);
            s_lastReal = real;
            s_clock += step;
            var znet = ZNet.instance;
            if (znet != null)
            {
                double net = znet.GetTimeSeconds();
                if (znet != s_session) { s_session = znet; s_clock = net; }
                else if (znet.IsServer()) s_clock = net;
                else
                {
                    double behind = net - s_clock;
                    if (behind > SnapAheadSeconds) s_clock = net;
                    else s_clock += System.Math.Max(-step * MaxSlew, System.Math.Min(step * MaxSlew, behind));
                }
            }
            s_out = (float)(s_clock % DAY);
            return s_out;
        }

        // Read without advancing (FAT's voxel water, mid-frame).
        internal static float Now => s_init ? s_out : 0f;
    }

    // ---- Wave amplitude match for FAT liquids only (FiresLiquid draws CalcWave(...) * _WaveAmplitude, 0.15 on FAT's pond / pour liquids): the
    //      wave part of a FAT liquid volume's surface is scaled by its own material's _WaveAmplitude, so a body floats on its drawn waves.
    //      0.2.1448: gated to FAT liquid materials (_FiresLiquidGrid = 1, set only by FiresLiquidWaterPrefab.cs:613 / :636), never the sea.
    internal static class FiresWaveAmplitudeMatch
    {
        private static readonly int s_amp = Shader.PropertyToID("_WaveAmplitude");
        private static readonly int s_liquidGrid = Shader.PropertyToID("_FiresLiquidGrid");
        private static readonly System.Collections.Generic.Dictionary<int, (float amp, float at)> s_cache =
            new System.Collections.Generic.Dictionary<int, (float, float)>();
        private const float RefreshSeconds = 5f;

        /// <summary>FAT liquid volume: its surface material's amplitude; 1 (no change) for any other water, the sea included.</summary>
        internal static float AmplitudeOf(WaterVolume v)
        {
            if (v == null || v.m_waterSurface == null) return 1f;
            int id = v.GetInstanceID();
            if (s_cache.TryGetValue(id, out var c) && Time.time - c.at < RefreshSeconds) return c.amp;
            var m = v.m_waterSurface.sharedMaterial;
            bool fatLiquid = m != null && m.HasProperty(s_liquidGrid) && m.GetFloat(s_liquidGrid) > 0.5f;
            float amp = fatLiquid && m.HasProperty(s_amp) ? m.GetFloat(s_amp) : 1f;
            if (s_cache.Count > 512) s_cache.Clear();
            s_cache[id] = (amp, Time.time);
            return amp;
        }
    }

    [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.GetWaterSurface))]
    internal static class Patch_WaterVolume_GetWaterSurface_Amplitude
    {
        private static void Postfix(WaterVolume __instance, ref float __result)
        {
            if (__instance == null) return;
            float amp = FiresWaveAmplitudeMatch.AmplitudeOf(__instance);
            if (Mathf.Approximately(amp, 1f)) return;   // the sea and every non-FAT volume end here
            float plane = __instance.transform.position.y + __instance.m_surfaceOffset;
            __result = plane + (__result - plane) * amp;
        }
    }
}
