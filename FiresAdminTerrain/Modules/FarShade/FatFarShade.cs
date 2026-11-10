using System;
using System.Diagnostics;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    /// <summary>
    /// FarLands #8 far mountain shadows + #10 canopy shade ([generator] for [saddles], Fire 11-08; our own code, method from
    /// Tools\RESEARCH_FarLands_20261108.md §2.5 prose only). Past the game's shadow distance, mountains throw the sun's shadow
    /// across far land and trees, and far forests shade the ground under them.
    ///
    /// Grid: 256² texels of 32 m (8 km) centred on the player, snapped to 1 km and re-centred after 1 km of travel. Heights come
    /// from FarQuilt.FarHeight on a Fires world (worker-safe once FiresWorldGenBase.GridReady), else WorldGenerator on the main
    /// thread in ~1 ms slices.
    /// #8: a worker marches each texel toward the light out to [Far Shadows] Reach and keeps the height below which the texel is in
    /// shadow plus the distance to the occluder (penumbra = the sun's 0.53° width x that distance). It re-runs when the light turns
    /// by more than 0.15° or the grid moves.
    /// #10: FatFarCanopy splats the far forest at most twice a second, when the trees or the light changed.
    ///
    /// Apply: ONE command buffer ("FAT FarSunShade") on the sun Light at LightEvent.AfterScreenspaceMask multiplies the
    /// screen-space shadow mask, so only the sun's own light darkens (fires, torches and lamps stay as bright as before). It fades
    /// in from 0.8x to 1x the game's shadow distance, so it starts where the vanilla shadows end. It never clears or overwrites the
    /// mask and never assumes it runs alone: Vedr's cloud shadows multiply the same mask at the same event, and multiplying
    /// commutes. Receivers on FarQuilt's outer rings are lowered by the ring drops (-2 / -4 / -8 m) in the shader so shadow edges
    /// sit on the drawn ground. Ring / zone-mode worlds (FAT halo path) stand down with a WHY line.
    /// [Far Shadows] keys all default OFF.
    /// </summary>
    public static class FatFarShade
    {
        internal const int N = 256;
        internal const float Cell = 32f, Snap = 1024f;
        private const float SunTurnDeg = 0.15f, CanopyEvery = 0.5f;
        private const string BufferName = "FAT FarSunShade";

        internal static ConfigEntry<bool> MountainShadows, CanopyShade;
        internal static ConfigEntry<float> CanopyStrength, Reach;
        private static bool s_bound;
        private static Shader s_shader;
        private static string s_shaderWhy = "the fatfarshade bundle has not been offered yet";

        /// <summary>Binds [Far Shadows] (from FiresAdminTerrain's Awake, beside FarQuilt.BindFarTerrain) and creates the driver on a client.</summary>
        internal static void Bind(ConfigFile cfg)
        {
            if (s_bound || cfg == null) return;
            s_bound = true;
            MountainShadows = cfg.Bind("Far Shadows", "Mountain shadows", false,
                "FarLands: distant mountains cast the sun's shadow across far land beyond the game's own shadow distance. Default off.");
            CanopyShade = cfg.Bind("Far Shadows", "Canopy shade", false,
                "FarLands: distant forests shade the ground under them (needs the far forest). Default off.");
            CanopyStrength = cfg.Bind("Far Shadows", "Canopy strength", 0.6f,
                new ConfigDescription("How dark the far forest shade is (0 = none, 1 = full).", new AcceptableValueRange<float>(0f, 1f)));
            Reach = cfg.Bind("Far Shadows", "Reach", 4096f,
                new ConfigDescription("How far a mountain's shadow is followed (m).", new AcceptableValueRange<float>(1024f, 6000f)));
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var go = new GameObject("FAT_FarShade") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Driver>();
        }

        /// <summary>The fatfarshade bundle's "Hidden/FAT/FarSunShade" (FiresAdminTerrain's loader, like fatvoxelshader). Null = stand down.</summary>
        internal static void OfferShader(Shader shader, string why)
        {
            s_shader = shader != null && shader.isSupported ? shader : null;
            s_shaderWhy = s_shader != null ? null : why ?? (shader != null ? "Hidden/FAT/FarSunShade is not supported on this GPU" : "no Hidden/FAT/FarSunShade in the fatfarshade bundle");
        }

        private static bool AnyOn => (MountainShadows != null && MountainShadows.Value) || (CanopyShade != null && CanopyShade.Value);

        // ---- the driver ----------------------------------------------------------------------------------------------------------

        private sealed class Driver : MonoBehaviour
        {
            // grid + results (worker writes the arrays, the main thread uploads)
            private float _ox, _oz;                   // grid (0,0) corner
            private float[] _h = new float[N * N];
            private ushort[] _shade = new ushort[N * N * 2];
            private byte[] _canopy = new byte[FatFarCanopy.Size * FatFarCanopy.Size];
            private float[] _cover = new float[FatFarCanopy.Size * FatFarCanopy.Size];
            private Texture2D _shadeTex, _canopyTex;
            private Material _mat; private CommandBuffer _cb; private Light _cbLight;

            private int _sampleRow = -1; private bool _gridStarted, _gridOnWorker, _shadeValid, _canopyValid;
            private volatile bool _heightsReady, _sampling, _marching, _splatting, _shadeNew, _canopyNew;
            private Vector3 _marchedSun, _splatSun; private int _splatVersion = -1; private float _nextCanopy;
            private bool _saidOn, _saidWhy; private string _lastWhy;
            private long _sampleTicks, _marchTicks, _splatTicks, _uploadTicks; private int _marches, _splats, _splatTrees; private float _nextLog;

            private void OnEnable() { Camera.onPreCull += PreCull; }
            private void OnDisable() { Camera.onPreCull -= PreCull; Detach(); }

            private void Update()
            {
                if (!AnyOn) { Detach(); return; }
                string why = Why();
                if (why != null) { Detach(); if (!_saidWhy || why != _lastWhy) { _saidWhy = true; _lastWhy = why; FiresLogger.LogInfo($"[FarShade] stood down: {why}"); } return; }
                var cam = Camera.main; var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
                if (cam == null || sun == null) return;

                Vector3 p = cam.transform.position;
                float half = N * Cell * 0.5f;
                bool busy = _sampling || _marching || _splatting;
                if (!busy && (!_gridStarted || Mathf.Abs(p.x - (_ox + half)) > Snap || Mathf.Abs(p.z - (_oz + half)) > Snap))
                    StartGrid(Mathf.Round(p.x / Snap) * Snap - half, Mathf.Round(p.z / Snap) * Snap - half);
                if (!_heightsReady) { SampleSlice(); return; }

                Vector3 toSun = -sun.transform.forward;
                if (MountainShadows.Value && !_marching && Vector3.Angle(toSun, _marchedSun) > SunTurnDeg) StartMarch(toSun);
                if (CanopyShade.Value && !_splatting && Time.unscaledTime >= _nextCanopy)
                {
                    int v = FatFarCanopy.Version;
                    if (v != _splatVersion || Vector3.Angle(toSun, _splatSun) > 0.5f) { _nextCanopy = Time.unscaledTime + CanopyEvery; StartSplat(toSun, v); }
                }
                Upload();
                Attach(sun);
                if (!_saidOn)
                {
                    _saidOn = true;
                    FiresLogger.LogInfo($"[FarShade] on: mountain shadows {(MountainShadows.Value ? "on" : "off")}, canopy shade {(CanopyShade.Value ? "on" : "off")} "
                        + $"(strength {CanopyStrength.Value:0.##}); grid {N}² x {Cell:0} m, canopy {FatFarCanopy.Size}² x {FatFarCanopy.Texel:0} m, reach {Reach.Value:0} m; "
                        + $"heights from {(_gridOnWorker ? "FarQuilt.FarHeight (worker)" : "WorldGenerator (main thread slices)")}; applied on the sun's shadow mask past {QualitySettings.shadowDistance * 0.8f:0} m");
                }
                if (Time.unscaledTime >= _nextLog) { _nextLog = Time.unscaledTime + 30f; Report(); }
            }

            private static string Why()
            {
                if (s_shader == null) return s_shaderWhy;
                if (FatHaloSectors.On || FatHaloArc.Active) return "ring / zone-mode world (the FAT halo path draws its own far land; far shade follows the world up, which ring worlds bend)";
                if (QualitySettings.shadows == ShadowQuality.Disable) return "the game's shadows are off (the sun has no shadow mask to darken)";
                var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
                if (sun != null && sun.shadows == LightShadows.None) return "the sun light casts no shadows (no shadow mask)";
                return null;
            }

            // ---- heights -----------------------------------------------------------------------------------------------------

            private void StartGrid(float ox, float oz)
            {
                _ox = ox; _oz = oz; _gridStarted = true; _heightsReady = false; _marchedSun = Vector3.zero; _splatSun = Vector3.zero; _splatVersion = -1;
                _shadeValid = false; _canopyValid = false; _shadeNew = false; _canopyNew = false;   // old results belong to the old grid
                _gridOnWorker = FiresWorldGenBase.GridReady;
                if (_gridOnWorker)
                {
                    _sampleRow = N;   // the worker fills the whole grid
                    float wox = ox, woz = oz;
                    _sampling = true;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        try { for (int z = 0; z < N; z++) for (int x = 0; x < N; x++) _h[x + z * N] = FarQuilt.FarHeight(wox + (x + 0.5f) * Cell, woz + (z + 0.5f) * Cell); }
                        catch (Exception e) { FiresLogger.LogDebug($"[FarShade] height grid failed ({e.GetType().Name}: {e.Message}); retried at the next move"); }
                        Interlocked.Add(ref _sampleTicks, Stopwatch.GetTimestamp() - t0);
                        _heightsReady = true; _sampling = false;
                    });
                }
                else _sampleRow = 0;
            }

            /// <summary>Main thread (vanilla worlds): WorldGenerator rows under a ~1 ms budget a frame.</summary>
            private void SampleSlice()
            {
                if (_gridOnWorker || _sampleRow < 0 || _sampleRow >= N) return;
                var gen = WorldGenerator.instance; if (gen == null) return;
                long t0 = Stopwatch.GetTimestamp(), budget = Stopwatch.Frequency / 1000;
                while (_sampleRow < N && Stopwatch.GetTimestamp() - t0 < budget)
                {
                    int z = _sampleRow++;
                    for (int x = 0; x < N; x++) _h[x + z * N] = gen.GetHeight(_ox + (x + 0.5f) * Cell, _oz + (z + 0.5f) * Cell);
                }
                _sampleTicks += Stopwatch.GetTimestamp() - t0;
                if (_sampleRow >= N) _heightsReady = true;
            }

            private float H(float gx, float gz)
            {
                if (gx < 0 || gz < 0 || gx > N - 1 || gz > N - 1) return float.NegativeInfinity;
                int x0 = (int)gx, z0 = (int)gz, x1 = Math.Min(x0 + 1, N - 1), z1 = Math.Min(z0 + 1, N - 1);
                float fx = gx - x0, fz = gz - z0;
                float a = _h[x0 + z0 * N] + (_h[x1 + z0 * N] - _h[x0 + z0 * N]) * fx, b = _h[x0 + z1 * N] + (_h[x1 + z1 * N] - _h[x0 + z1 * N]) * fx;
                return a + (b - a) * fz;
            }

            // ---- #8 march (worker) ------------------------------------------------------------------------------------------------

            private void StartMarch(Vector3 toSun)
            {
                _marching = true; _marchedSun = toSun;
                float reach = Reach.Value;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try { March(toSun, reach); _shadeNew = true; }
                    catch (Exception e) { FiresLogger.LogDebug($"[FarShade] march failed ({e.GetType().Name}: {e.Message})"); }
                    Interlocked.Add(ref _marchTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref _marches);
                    _marching = false;
                });
            }

            /// <summary>Per texel: the highest (occluder height - distance x tan(elevation)) toward the light = the shadow line there.</summary>
            private void March(Vector3 toSun, float reach)
            {
                var flat = new Vector2(toSun.x, toSun.z); float horiz = flat.magnitude;
                bool low = toSun.y <= 0.01f || horiz < 1e-4f;
                Vector2 dir = horiz > 1e-4f ? flat / horiz : Vector2.zero;
                float tanE = low ? 0f : toSun.y / horiz;
                for (int z = 0; z < N; z++)
                    for (int x = 0; x < N; x++)
                    {
                        float best = -10000f, bestD = 0f;
                        if (!low && toSun.y < 0.999f)
                        {
                            float s = Cell;
                            while (s <= reach)
                            {
                                float gx = x + dir.x * s / Cell, gz = z + dir.y * s / Cell;
                                float hq = H(gx, gz);
                                if (float.IsNegativeInfinity(hq)) break;
                                float line = hq - s * tanE;
                                if (line > best) { best = line; bestD = s; }
                                s += Mathf.Max(Cell, s * 0.08f);
                            }
                        }
                        int i = (x + z * N) * 2;
                        _shade[i] = Mathf.FloatToHalf(best); _shade[i + 1] = Mathf.FloatToHalf(bestD);
                    }
            }

            // ---- #10 splat (worker) -----------------------------------------------------------------------------------------------

            private void StartSplat(Vector3 toSun, int version)
            {
                _splatting = true; _splatSun = toSun; _splatVersion = version;
                float half = FatFarCanopy.Size * FatFarCanopy.Texel * 0.5f, ox = _ox + N * Cell * 0.5f - half, oz = _oz + N * Cell * 0.5f - half;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try { _splatTrees = FatFarCanopy.Splat(ox, oz, toSun, _canopy, _cover); _canopyNew = true; }
                    catch (Exception e) { FiresLogger.LogDebug($"[FarShade] canopy splat failed ({e.GetType().Name}: {e.Message})"); }
                    Interlocked.Add(ref _splatTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref _splats);
                    _splatting = false;
                });
            }

            // ---- upload + apply -----------------------------------------------------------------------------------------------------

            private void Upload()
            {
                long t0 = Stopwatch.GetTimestamp();
                if (_shadeNew && !_marching)
                {
                    _shadeNew = false;
                    if (_shadeTex == null) _shadeTex = new Texture2D(N, N, TextureFormat.RGHalf, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                    _shadeTex.SetPixelData(_shade, 0); _shadeTex.Apply(false, false); _shadeValid = true;
                }
                if (_canopyNew && !_splatting)
                {
                    _canopyNew = false;
                    if (_canopyTex == null) _canopyTex = new Texture2D(FatFarCanopy.Size, FatFarCanopy.Size, TextureFormat.R8, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                    _canopyTex.SetPixelData(_canopy, 0); _canopyTex.Apply(false, false); _canopyValid = true;
                }
                _uploadTicks += Stopwatch.GetTimestamp() - t0;
            }

            private void Attach(Light sun)
            {
                if (_cbLight == sun && _cb != null) return;
                Detach();
                if (_mat == null) _mat = new Material(s_shader) { hideFlags = HideFlags.HideAndDontSave };
                _cb = new CommandBuffer { name = BufferName };
                _cb.DrawProcedural(Matrix4x4.identity, _mat, 0, MeshTopology.Triangles, 3);   // one full-screen triangle, multiply blend
                sun.AddCommandBuffer(LightEvent.AfterScreenspaceMask, _cb);
                _cbLight = sun;
            }

            private void Detach()
            {
                if (_cb != null && _cbLight != null) _cbLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, _cb);
                _cb?.Release(); _cb = null; _cbLight = null;
            }

            /// <summary>Per camera: the main camera gets the matrices and the textures; every other camera (map, previews) gets "off".</summary>
            private void PreCull(Camera cam)
            {
                if (_mat == null) return;
                bool main = cam == Camera.main && _cb != null;
                bool mountains = main && MountainShadows.Value && _shadeValid;
                bool canopy = main && CanopyShade.Value && _canopyValid;
                if (!mountains && !canopy) { _mat.SetVector("_FarShadeOn", Vector4.zero); return; }
                if (main && (cam.depthTextureMode & DepthTextureMode.Depth) == 0) cam.depthTextureMode |= DepthTextureMode.Depth;
                var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
                _mat.SetMatrix("_FarShadeInvVP", vp.inverse);
                float size = N * Cell, csize = FatFarCanopy.Size * FatFarCanopy.Texel, mid = N * Cell * 0.5f;
                if (_shadeTex != null) _mat.SetTexture("_FarShadeTex", _shadeTex);
                if (_canopyTex != null) _mat.SetTexture("_FarCanopyTex", _canopyTex);
                _mat.SetVector("_FarShadeRect", new Vector4(_ox, _oz, 1f / size, 0f));
                _mat.SetVector("_FarCanopyRect", new Vector4(_ox + mid - csize * 0.5f, _oz + mid - csize * 0.5f, 1f / csize, 0f));
                float sd = QualitySettings.shadowDistance;
                _mat.SetVector("_FarShadeOn", new Vector4(mountains ? 1f : 0f, canopy ? CanopyStrength.Value : 0f, sd * 0.8f, sd));
                // FarQuilt's drawn ring drops (receivers sit lower out there): near radius, far radius, ring-2 edge
                float near = FiresVoxelConfig.FarQuiltNearRadius != null ? FiresVoxelConfig.FarQuiltNearRadius.Value : 384f;
                float far = FiresVoxelConfig.FarQuiltFarRadius != null ? FiresVoxelConfig.FarQuiltFarRadius.Value : 1536f;
                _mat.SetVector("_FarRingDrops", _gridOnWorker ? new Vector4(near, far, 3072f, 1f) : Vector4.zero);
                _mat.SetVector("_FarShadeEye", cam.transform.position);
            }

            private void Report()
            {
                if (!FiresLogger.DebugEnabled) return;
                double ms = 1000.0 / Stopwatch.Frequency;
                FiresLogger.LogDebug($"[FarShade] last 30 s: heights {Interlocked.Exchange(ref _sampleTicks, 0) * ms:0} ms ({(_gridOnWorker ? "worker" : "main")}); "
                    + $"{_marches} march(es) {Interlocked.Exchange(ref _marchTicks, 0) * ms:0} ms (worker); {_splats} canopy splat(s) {Interlocked.Exchange(ref _splatTicks, 0) * ms:0} ms (worker, {_splatTrees} trees, {FatFarCanopy.TreeCount} held); "
                    + $"upload {_uploadTicks * ms:0.0} ms (main); apply {(_cb != null ? "on" : "off")}");
                _marches = 0; _splats = 0; _uploadTicks = 0;
            }

            private void OnDestroy()
            {
                Detach();
                if (_shadeTex != null) Destroy(_shadeTex);
                if (_canopyTex != null) Destroy(_canopyTex);
                if (_mat != null) Destroy(_mat);
            }
        }
    }
}
