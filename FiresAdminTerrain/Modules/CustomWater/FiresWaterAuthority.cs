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
    /// Monotonic water clock, one sea for every peer. See FiresWaterAuthority.
    /// It advances by real time once per frame: the first caller's dt used to be FixedUpdate's 0.02 in any frame with a
    /// physics step, so the sea ran at fps x 0.02 (R33 boat_test: Fire x1.63 at 147 fps, the bot x1.67, the dedi x0.60 at
    /// 30 fps, hundreds of seconds apart). It snaps to net time when a session starts: the main menu's sea used to seed it
    /// at 0 for good, so no two peers shared a wave phase and every ownership change re-floated a hull on another sea
    /// ([fgn], boat_test, 2026-09-28). After that it leans toward net time by at most a tenth of each step, never
    /// backward, so the swell still never rewinds.
    /// </summary>
    internal static class FiresWaterClock
    {
        private const double DAY = 86400.0;   // float-precision guard = vanilla's wrap period
        private const double MaxSlew = 0.1;   // share of a step the clock may run fast or slow to meet net time
        private const double SnapAheadSeconds = 2.0;   // this far behind net time (a join, a big correction): jump forward
        private static double s_clock;        // monotonic accumulator (double: no precision loss)
        private static bool s_init;
        private static double s_lastReal = -1.0;
        private static ZNet s_session;        // the session the clock was last snapped to
        private static int s_frame = -1;
        private static float s_out;           // wrapped float handed out this frame

        // Current wrapped water time (advances at most once per frame, any caller). The caller's dt is not used: it is
        // FixedUpdate's step when the first caller of a frame is a physics step.
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
                if (znet != s_session)
                {
                    s_session = znet;
                    s_clock = net;
                }
                // The server OWNS net time: vanilla advances it only while a player is connected, with dt capped at
                // maximumDeltaTime (ZNet.UpdateNetTime), so adding real time here ran the server's sea ahead on an empty server
                // or a long hitch (R53b dedi: -0.36 s). Net time never rewinds within a session, so the sea just follows it.
                else if (znet.IsServer()) s_clock = net;
                else
                {
                    double behind = net - s_clock;
                    if (behind > SnapAheadSeconds)
                    {
                        FiresLogger.LogInfo($"[WaterClock] snapped forward {behind:0.00} s to net time (a join or a big correction).");
                        s_clock = net;
                    }
                    else s_clock += System.Math.Max(-step * MaxSlew, System.Math.Min(step * MaxSlew, behind));
                    Watch(behind, step);
                }
            }
            s_out = (float)(s_clock % DAY);
            return s_out;
        }

        // OPEN_BUGS #18 diagnostic ([visual], R52 boat_test sea spikes): the largest offset from net time over each window,
        // logged only when it passed WatchOffset. Allocation-free unless it logs.
        private const double WatchOffset = 0.1;
        private const float WatchWindowSeconds = 5f;
        private static double s_watchMax;
        private static double s_watchMaxStep;
        private static float s_watchUntil = -1f;

        private static void Watch(double behind, double step)
        {
            double abs = behind < 0.0 ? -behind : behind;
            if (abs > (s_watchMax < 0.0 ? -s_watchMax : s_watchMax)) { s_watchMax = behind; s_watchMaxStep = step; }
            float now = Time.unscaledTime;
            if (s_watchUntil < 0f) { s_watchUntil = now + WatchWindowSeconds; return; }
            if (now < s_watchUntil) return;
            s_watchUntil = now + WatchWindowSeconds;
            double worst = s_watchMax < 0.0 ? -s_watchMax : s_watchMax;
            if (worst > WatchOffset)
                FiresLogger.LogInfo($"[WaterClock] up to {s_watchMax:+0.00;-0.00} s from net time in the last {WatchWindowSeconds:0} s "
                                    + $"(frame {s_watchMaxStep * 1000.0:0} ms then; slewing at most {MaxSlew:P0} of a step).");
            s_watchMax = 0.0;
            s_watchMaxStep = 0.0;
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

    // ---- 0.2.698 (Fire 10-01: "our swim trigger/float trigger doesn't actually match the waves", against a sea cliff;
    //      [visual]'s static compare): the clock and the ten trochoids already match the FiresWater shader; the DEPTH term
    //      did not. Physics took vanilla WaterVolume.Depth (a bilinear of 4 corner depths per 64 m volume), the shader
    //      FTS FiresShoreField's per-texel R (~21 m texels, Lerp(floor, 1, Clamp01(depth / 10)) from the generated ground).
    //      Near cliffs, islands and shores they disagree, so the body floated on a different wave than the one drawn.
    //      Where the field covers a SEA volume (global wind, surface at the sea plane), Depth answers with the field's R,
    //      through FTS's public FiresShoreField.TrySampleWave, bound by name (no hard dependency); elsewhere, vanilla's.
    internal static class FiresWaveDepthMatch
    {
        private delegate bool Sampler(float x, float z, out float wave);
        private static Sampler s_sampler;
        private static float s_nextBind;
        private static bool s_bindLogged;
        private const float BindRetrySeconds = 10f, SeaSurfaceBand = 1f;
        private static readonly AccessTools.FieldRef<WaterVolume, bool> UsesGlobalWind = AccessTools.FieldRefAccess<WaterVolume, bool>("m_useGlobalWind");

        // 0.2.699: FTS's sampler is bound (what the FiresWater shader draws is known here).
        internal static bool HasFtsSampler => Bind() != null;

        // FTS's value where it covers the point, else none.
        internal static bool TrySampleFts(float x, float z, out float wave) { wave = 0f; var s = Bind(); return s != null && s(x, z, out wave); }

        internal static bool TrySample(float x, float z, out float wave)
        {
            wave = 0f;
            var s = Bind();
            if (s != null && s(x, z, out wave)) return true;
            // 0.2.699 ([lead]: server-owned floaters near coasts must match too): without FTS's sampler (a dedicated server,
            // or a client without FiresTossinShade), the SAME field, evaluated here: FTS 0.3.153 snaps its bake centre to the
            // texel grid, so its texel centres sit at (k + 0.5) * texel on one world grid and these values are its values.
            if (WorldGenerator.instance == null) return false;
            wave = AnalyticWave(x, z);
            return true;
        }

        // FTS FiresShoreField's defaults (a dedicated server cannot read FTS's statics): Extent 1024, Resolution 96,
        // WaveShoreFloor 0, WaveFullDepth 10; per texel: water (depth > 0.05) ? round(Lerp(floor, 1, Clamp01(depth / full))
        // * 255) / 255 : 0, depth = sea + _FiresTideOffset - WorldGenerator.GetHeight at the texel centre; then bilinear.
        internal const float ShoreExtent = 1024f, ShoreFloor = 0f, ShoreFullDepth = 10f;
        internal const int ShoreResolution = 96;
        private const float ShoreTexel = 2f * ShoreExtent / ShoreResolution;
        private static readonly int s_tideId = Shader.PropertyToID("_FiresTideOffset");
        private static readonly System.Collections.Generic.Dictionary<long, float> s_texels = new System.Collections.Generic.Dictionary<long, float>();
        private static float s_texelTide = float.NaN;
        private const int TexelCacheMax = 65536;

        // 0.2.700 ([visual]): the tide FTS bakes with is Galaxies' TideSystem.SurfaceOffset (it writes _FiresTideOffset from
        // it, Vedr's flood included); a shader global is not trusted on a null-graphics server, so read the source by name,
        // else the global, else 0.
        private static System.Func<float> s_tide;
        private static bool s_tideBound;

        internal static float Tide()   // 0.2.867: internal, wavematch_test adds it to the drawn height
        {
            if (!s_tideBound)
            {
                s_tideBound = true;
                var t = AccessTools.TypeByName("VerdantsAscent.Galaxies.TideSystem");
                // 0.2.869 ([lead] 10-09): Galaxies' SurfaceOffset is a property; look the property up first so HarmonyX's
                // AccessTools.Field does not warn "Could not find field" every load. The field path stays for older builds.
                var pr = t != null ? AccessTools.Property(t, "SurfaceOffset") : null;
                var f = pr == null && t != null ? AccessTools.Field(t, "SurfaceOffset") : null;
                if (f != null && f.IsStatic && f.FieldType == typeof(float)) s_tide = () => (float)f.GetValue(null);
                else if (pr != null && pr.GetGetMethod() != null && pr.GetGetMethod().IsStatic && pr.PropertyType == typeof(float))
                    s_tide = (System.Func<float>)System.Delegate.CreateDelegate(typeof(System.Func<float>), pr.GetGetMethod());
            }
            try { if (s_tide != null) return s_tide(); } catch { s_tide = null; }
            return Shader.GetGlobalFloat(s_tideId);
        }

        internal static float AnalyticWave(float x, float z)
        {
            float tide = Tide();
            if (!(Mathf.Abs(tide - s_texelTide) < 0.005f) || s_texels.Count > TexelCacheMax) { s_texels.Clear(); s_texelTide = tide; }
            float fx = x / ShoreTexel - 0.5f, fz = z / ShoreTexel - 0.5f;
            int ix = Mathf.FloorToInt(fx), iz = Mathf.FloorToInt(fz);
            float tx = fx - ix, tz = fz - iz;
            float a = Texel(ix, iz, tide), b = Texel(ix + 1, iz, tide), c = Texel(ix, iz + 1, tide), d = Texel(ix + 1, iz + 1, tide);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        private static float Texel(int i, int k, float tide)
        {
            long key = ((long)i << 32) ^ (uint)k;
            if (s_texels.TryGetValue(key, out float v)) return v;
            float wx = (i + 0.5f) * ShoreTexel, wz = (k + 0.5f) * ShoreTexel;
            float depth = Mathf.Max(0f, ZoneSystem.c_WaterLevel + tide - WorldGenerator.instance.GetHeight(wx, wz));
            v = depth > 0.05f ? Mathf.Round(Mathf.Clamp01(Mathf.Lerp(ShoreFloor, 1f, Mathf.Clamp01(depth / ShoreFullDepth))) * 255f) / 255f : 0f;
            s_texels[key] = v;
            return v;
        }

        // 0.2.699: the dedicated server's depth factor at points, for wavematch_test's server step. RPC
        // "FiresAdminTerrain_WaveDepthProbe" (client → server): ZPackage { int id, int n, n × (float x, float z) };
        // reply "FiresAdminTerrain_WaveDepthProbeReply": { int id, int n, n × float depth (-1 = no sea volume there) }.
        internal const string ProbeRpc = "FiresAdminTerrain_WaveDepthProbe", ProbeReplyRpc = "FiresAdminTerrain_WaveDepthProbeReply";
        private static ZRoutedRpc s_rpcOn;
        private static int s_nextProbe = 1;
        private static readonly System.Collections.Generic.Dictionary<int, System.Action<float[]>> s_probes = new System.Collections.Generic.Dictionary<int, System.Action<float[]>>();

        internal static void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_rpcOn)) return;
            rpc.Register<ZPackage>(ProbeRpc, OnProbe);
            rpc.Register<ZPackage>(ProbeReplyRpc, OnProbeReply);
            s_rpcOn = rpc;
            s_probes.Clear();
        }

        internal static void ServerDepths(Vector2[] points, System.Action<float[]> reply)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null) { reply(null); return; }
            if (ZNet.instance.IsServer()) { reply(LocalDepths(points)); return; }
            EnsureRegistered();
            int id = s_nextProbe++;
            s_probes[id] = reply;
            var pkg = new ZPackage();
            pkg.Write(id); pkg.Write(points.Length);
            foreach (var p in points) { pkg.Write(p.x); pkg.Write(p.y); }
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), ProbeRpc, pkg);
        }

        private static float[] LocalDepths(Vector2[] points)
        {
            var r = new float[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var p = new Vector3(points[i].x, ZoneSystem.c_WaterLevel, points[i].y);
                WaterVolume v = null;
                Floating.GetWaterLevel(p, ref v);
                if (v != null && Mathf.Abs(v.transform.position.y - ZoneSystem.c_WaterLevel) <= SeaSurfaceBand) r[i] = v.Depth(p);
                // 0.2.870 ([visual] 09:18 round: 49 of 49 points had no sea volume on the dedi, whose zones near a client
                // are not instantiated there): answer with the field a server sea volume's Depth would return (TrySample),
                // so the leg still compares the two evaluations; -1 only without a world generator.
                else r[i] = TrySample(p.x, p.z, out float w) ? w : -1f;
            }
            return r;
        }

        private static void OnProbe(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            int id = pkg.ReadInt(), n = Mathf.Clamp(pkg.ReadInt(), 0, 1024);
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++) pts[i] = new Vector2(pkg.ReadSingle(), pkg.ReadSingle());
            var d = LocalDepths(pts);
            var reply = new ZPackage();
            reply.Write(id); reply.Write(n);
            foreach (float f in d) reply.Write(f);
            reply.Write(Tide());   // 0.2.872: the server's tide (its shore-field depth uses it), read by newer clients only
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ProbeReplyRpc, reply);
        }

        // 0.2.872: the tide the server answered with (NaN from an older server).
        internal static float LastServerTide = float.NaN;

        private static void OnProbeReply(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            int id = pkg.ReadInt(), n = pkg.ReadInt();
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = pkg.ReadSingle();
            LastServerTide = pkg.GetPos() + 4 <= pkg.Size() ? pkg.ReadSingle() : float.NaN;
            if (s_probes.TryGetValue(id, out var cb)) { s_probes.Remove(id); cb(d); }
        }

        private static Sampler Bind()
        {
            if (s_sampler != null || Time.realtimeSinceStartup < s_nextBind) return s_sampler;
            s_nextBind = Time.realtimeSinceStartup + BindRetrySeconds;
            var t = AccessTools.TypeByName("VerdantsAscent.Water.Core.FiresShoreField");
            var m = t != null ? AccessTools.Method(t, "TrySampleWave", new[] { typeof(float), typeof(float), typeof(float).MakeByRefType() }) : null;
            if (m == null || !m.IsStatic) return null;
            try { s_sampler = (Sampler)System.Delegate.CreateDelegate(typeof(Sampler), m); }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresWater] FiresShoreField.TrySampleWave has an unexpected signature: {ex.Message}"); s_nextBind = float.MaxValue; return null; }
            if (!s_bindLogged)
            {
                s_bindLogged = true;
                FiresLogger.LogInfo("[FiresWater] swim/float waves use FiresTossinShade's shore field depth (FiresShoreField.TrySampleWave), the same the FiresWater shader draws.");
            }
            return s_sampler;
        }

        // The postfix of WaterVolume.Depth.
        internal static void Depth(WaterVolume v, Vector3 point, ref float result)
        {
            if (v == null || !UsesGlobalWind(v)) return;
            if (Mathf.Abs(v.transform.position.y - ZoneSystem.c_WaterLevel) > SeaSurfaceBand) return;   // a sea volume only
            if (TrySample(point.x, point.z, out float wave)) result = wave;
        }
    }

    // ---- 0.2.698 ([visual]: FiresLiquid draws `CalcWave(...) * _WaveAmplitude`, 0.15 on FAT's pond/pour liquids, 1.0 on
    //      the sea; physics had no factor): the wave part of a volume's surface is scaled by its own surface material's
    //      _WaveAmplitude, so a pool's body floats on its drawn 0.15 waves. Cached per volume, re-read every few seconds
    //      (the liquid config hot-reloads).
    internal static class FiresWaveAmplitudeMatch
    {
        private static readonly int s_amp = Shader.PropertyToID("_WaveAmplitude");
        private static readonly System.Collections.Generic.Dictionary<int, (float amp, float at)> s_cache =
            new System.Collections.Generic.Dictionary<int, (float, float)>();
        private const float RefreshSeconds = 5f;

        internal static float AmplitudeOf(WaterVolume v)
        {
            if (v == null || v.m_waterSurface == null) return 1f;
            int id = v.GetInstanceID();
            if (s_cache.TryGetValue(id, out var c) && Time.time - c.at < RefreshSeconds) return c.amp;
            var m = v.m_waterSurface.sharedMaterial;
            float amp = m != null && m.HasProperty(s_amp) ? m.GetFloat(s_amp) : 1f;
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
            if (!FiresWaterAuthority.Active || __instance == null) return;
            float amp = FiresWaveAmplitudeMatch.AmplitudeOf(__instance);
            if (Mathf.Approximately(amp, 1f)) return;
            float plane = __instance.transform.position.y + __instance.m_surfaceOffset;
            __result = plane + (__result - plane) * amp;
        }
    }

    [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.Depth))]
    internal static class Patch_WaterVolume_Depth
    {
        private static void Postfix(WaterVolume __instance, Vector3 point, ref float __result)
        {
            if (FiresWaterAuthority.Active) FiresWaveDepthMatch.Depth(__instance, point, ref __result);
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
            Vector4 wind1 = s_wind1();
            Vector4 wind2 = s_wind2();
            float alpha = s_windAlpha();
            FiresSeaWind.Apply(worldPos, ref wind1, ref wind2, ref alpha);
            if (alpha == 0f)
                __result = FiresWaveField.FromWind(worldPos, depth, wind1, waterTime, waveFactor, waveFactorBig);
            else
                __result = Mathf.LerpUnclamped(
                    FiresWaveField.FromWind(worldPos, depth, wind1, waterTime, waveFactor, waveFactorBig),
                    FiresWaveField.FromWind(worldPos, depth, wind2, waterTime, waveFactor, waveFactorBig),
                    alpha);
            return false;
        }
    }
}
