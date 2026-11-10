using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // HALO RING step C (0.2.1031; Fire 10-24, HaloFlat): the ring you can see but cannot reach. The voxel shell is only built in
    // the Y band near a player (FatHaloArc's gate), and the far side of a 4,775 m ring is 9.5 km away, past the camera's far
    // clip. So the whole hoop is ONE low-poly mesh (the inside face, Segments around x Rows along the axis) drawn every frame
    // with its vertices pulled toward the camera ALONG their own view ray: a point d metres away is drawn at Compress(d), which
    // is d itself out to NearKeep and then squeezes everything beyond into the camera's far clip. A move along the view ray
    // keeps each point on the same pixel, so the hoop's outline is exactly the real ring's; only its depth changes.
    // Clients only.
    //
    // 0.2.1045 FAR RING LOW LOD, phase 1 (Fire 10-24: "a low lod terrain working for the ring so we can look 'up' and see the
    // other side"; Tools\DESIGN_halo_far_ring_lod_20261024.md). The ring floor is a perfect cylinder, so the hoop's outline was
    // already right; what was missing was the LOOK:
    // - a ring albedo map (MapW x MapH) baked ONCE per world on a worker from FatHaloBiome (the same 900 m cells and blend the near
    //   side uses, in its own unrolled frame, so they line up), per-biome terrain colours, the 40 m rim lips in a cliff tone. It
    //   is cached as BepInEx\cache\FiresAdminTerrain\halo\farring_<seed>_<ring signature>_v1.rgb, raw RGB (any ring or seed change = a new
    //   file). Every client bakes its own: deterministic, nothing over the network;
    // - the hoop has UVs from that same frame (Rows 24 across, the seam column wraps) and draws the map, tinted by the sun and
    //   the ambient light (day/night), faded into the fog with distance;
    // - it sits OUTWARD of the real floor by max(2 m, 0.003 d) on top of R + T/2: behind the 4 m opaque shell, so wherever the real
    //   voxel ground is loaded it wins the depth test by geometry (no z-fighting, no cut-out at the streamed band's edge);
    // - the vertex rewrite runs only when the camera moved more than 0.25 m or the light changed (every 0.5 s).
    // 0.2.1046 phase 2: with Custom/FiresHaloFar (embedded bundle 'fathalofar', built LINEAR) the squeeze, the seam gap, the sun
    // lambert against the ring's local up and a thinner fog curve all run on the GPU; the mesh is static (no CPU per frame). The
    // CPU path above stays as the fallback when the shader is missing or unsupported.
    internal static class FatHaloSky
    {
        private const int Segments = 720, Rows = 24;
        private const int MapW = 4096, MapH = 512, BakeVersion = 1;
        private const float NearKeep = 1500f, FarShare = 0.92f, MinGap = 2f, GapPerMetre = 0.003f, RimBand = 40f;
        private const float MoveSq = 0.0625f, LightEvery = 0.5f;
        private static Mesh s_mesh;
        private static Vector3[] s_world, s_out, s_draw;
        private static Color[] s_colors;
        private static Material s_mat;
        private static string s_builtFor;
        private static bool s_failed, s_logged;
        private static Vector3 s_lastCam = new Vector3(float.NaN, 0f, 0f);
        private static float s_nextLight;
        private static Color s_tint = Color.white;
        private static readonly Color Ground = new Color(0.36f, 0.42f, 0.25f, 1f);

        // the bake
        private static Texture2D s_map;
        private static string s_mapFor;
        private static Task<Color32[]> s_bake;
        private static string s_bakePath;
        private static float s_bakeStarted;

        internal static void Tick(bool active, string ringKey, Vector3 origin, Vector3 dir, float radius, float halfLength, float thickness)
        {
            if (s_failed || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            if (!active || Mathf.Abs(dir.y) < 0.999f) return;   // the full vertical ring only (HaloFlat)
            var cam = Camera.main;
            if (cam == null) return;
            try
            {
                if (s_builtFor != ringKey) Build(ringKey, origin, radius + thickness * 0.5f, halfLength);
                if (s_mesh == null || s_mat == null) return;
                TickMap(ringKey);
                Vector3 c = cam.transform.position;
                bool lightDue = Time.unscaledTime >= s_nextLight;
                if (lightDue)
                {
                    s_nextLight = Time.unscaledTime + LightEvery;
                    s_tint = DayTint();
                }
                if (Gpu)
                {
                    // phase 2: the GPU squeezes; only the far-clip target and the light/fog globals change.
                    s_mat.SetFloat("_FarTarget", cam.farClipPlane * FarShare);
                    if (lightDue) PushLight();
                    // 0.2.1047 phase 3: the far tree silhouettes (same shader, own meshes)
                    FatHaloFarTrees.Tick(ringKey, cam, origin, radius + thickness * 0.5f, halfLength, cam.farClipPlane * FarShare);
                }
                else if (lightDue || (c - s_lastCam).sqrMagnitude > MoveSq || float.IsNaN(s_lastCam.x))
                {
                    s_lastCam = c;
                    float far = cam.farClipPlane * FarShare;
                    Color fog = RenderSettings.fog ? RenderSettings.fogColor : Ground;
                    Color near = s_map != null ? s_tint : Ground;
                    for (int i = 0; i < s_world.Length; i++)
                    {
                        Vector3 v0 = s_world[i] - c;
                        float d0 = v0.magnitude;
                        Vector3 v = v0 + s_out[i] * Mathf.Max(MinGap, GapPerMetre * d0);   // behind the real shell
                        float d = v.magnitude;
                        float k = d <= NearKeep || d < 1e-3f ? 1f : Compress(d, far) / d;
                        s_draw[i] = c + v * k;
                        s_colors[i] = Color.Lerp(near, fog, Mathf.Clamp01(d / 9000f) * 0.85f);
                    }
                    s_mesh.vertices = s_draw;
                    s_mesh.colors = s_colors;
                    s_mesh.RecalculateBounds();
                }
                Graphics.DrawMesh(s_mesh, Matrix4x4.identity, s_mat, 0, cam, 0, null, false, false, false);
                if (!s_logged && s_map != null)
                {
                    s_logged = true;
                    FiresLogger.LogInfo($"[FatHalo] far ring drawing: {s_world.Length} verts, 1 call, {(Gpu ? "GPU (Custom/FiresHaloFar)" : "CPU (Sprites/Default)")}, map {s_map.width}x{s_map.height}, radius {radius + thickness * 0.5f:F0} m, far clip {cam.farClipPlane:F0} m (beyond {NearKeep:F0} m squeezed into {cam.farClipPlane * FarShare:F0} m)");
                }
            }
            catch (Exception ex)
            {
                s_failed = true;
                FiresLogger.LogWarning($"[FatHalo] sky hoop off: {ex.Message}");
            }
        }

        // d -> drawn distance: unchanged to NearKeep, then everything up to 2 x 9.6 km squeezed into (NearKeep, far].
        private static float Compress(float d, float far)
        {
            if (far <= NearKeep + 10f) return Mathf.Min(d, far);
            float t = Mathf.Clamp01((d - NearKeep) / 19000f);
            return NearKeep + (far - NearKeep) * Mathf.Sqrt(t);
        }

        // Day/night: the ambient light plus the sun's colour x intensity (the far side is lit by the same sky).
        // ── phase 2 (0.2.1046): Custom/FiresHaloFar from the fathalofar bundle (set by FiresAdminTerrain at load) ──────────
        internal static Shader FarShader;
        private static bool Gpu => FarShader != null;
        private const float FarFogDensity = 0.00008f, FarFogMax = 0.6f;   // thinner than Valheim's ExpSq; agreed with [seasons]
        private static readonly int SunDirId = Shader.PropertyToID("_HaloSunDir"), SunColorId = Shader.PropertyToID("_HaloSunColor"),
            AmbientId = Shader.PropertyToID("_HaloAmbient"), FogColorId = Shader.PropertyToID("_HaloFogColor");

        // The shader's light: the sun's travel direction and colour x intensity, the ambient colour, the fog colour.
        // SetGlobalColor converts the gamma-space colours to linear for the linear pipeline.
        // The squeeze, seam gap and fog every far-ring material shares (the floor here, the trees in FatHaloFarTrees).
        internal static void ApplyFarDefaults(Material m)
        {
            m.SetFloat("_NearKeep", NearKeep);
            m.SetFloat("_SqueezeSpan", 19000f);
            m.SetFloat("_GapMin", MinGap);
            m.SetFloat("_GapPerM", GapPerMetre);
            m.SetFloat("_FogDensity", FarFogDensity);
            m.SetFloat("_FogMax", FarFogMax);
            m.enableInstancing = true;
        }

        private static void PushLight()
        {
            var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            bool on = sun != null && sun.isActiveAndEnabled;
            Shader.SetGlobalVector(SunDirId, on ? (Vector4)sun.transform.forward : new Vector4(0f, -1f, 0f, 0f));
            Shader.SetGlobalColor(SunColorId, on ? sun.color * (sun.intensity * 0.7f) : Color.black);
            Shader.SetGlobalColor(AmbientId, RenderSettings.ambientLight);
            Shader.SetGlobalColor(FogColorId, RenderSettings.fog ? RenderSettings.fogColor : Ground);
        }

        private static Color DayTint()
        {
            Color amb = RenderSettings.ambientLight;
            var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            Color s = sun != null && sun.isActiveAndEnabled ? sun.color * sun.intensity * 0.7f : Color.black;
            Color t = amb + s;
            float m = Mathf.Max(t.r, Mathf.Max(t.g, t.b));
            if (m > 1.2f) t *= 1.2f / m;
            t.a = 1f;
            return t;
        }

        // ── the ring albedo map ─────────────────────────────────────────────────────────────────────────────────────────
        private static void TickMap(string ringKey)
        {
            if (!FatHaloBiome.Ready) return;
            string key = $"{FatHaloBiome.Seed}_{FatHaloArc.Signature():x8}_v{BakeVersion}";
            if (s_mapFor == key)
            {
                if (s_bake == null || !s_bake.IsCompleted) return;
                var bake = s_bake;
                s_bake = null;
                if (bake.IsFaulted || bake.Result == null)
                {
                    FiresLogger.LogWarning($"[FatHalo] far ring bake failed ({bake.Exception?.GetBaseException().Message}); the hoop stays a silhouette.");
                    return;
                }
                var tex = NewMap();
                tex.SetPixels32(bake.Result);
                tex.Apply(true, false);
                long ms = (long)((Time.realtimeSinceStartup - s_bakeStarted) * 1000f);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(s_bakePath));
                    File.WriteAllBytes(s_bakePath, ToRgb(bake.Result));
                }
                catch (Exception ex) { FiresLogger.LogWarning($"[FatHalo] far ring map not cached ({ex.Message})."); }
                Use(tex);
                FiresLogger.LogInfo($"[FatHalo] far ring baked: {MapW}x{MapH} in {ms} ms, cache {s_bakePath}");
                return;
            }
            s_mapFor = key;
            s_bakePath = Path.Combine(FiresCachePaths.For("FiresAdminTerrain", "halo"), $"farring_{key}.rgb");
            if (File.Exists(s_bakePath))
            {
                var t0 = Time.realtimeSinceStartup;
                byte[] raw = File.ReadAllBytes(s_bakePath);
                if (raw.Length == MapW * MapH * 3)
                {
                    var tex = NewMap();
                    tex.SetPixels32(FromRgb(raw));
                    tex.Apply(true, false);
                    Use(tex);
                    FiresLogger.LogInfo($"[FatHalo] far ring loaded: {MapW}x{MapH} in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms, cache {s_bakePath}");
                    return;
                }
            }
            s_bakeStarted = Time.realtimeSinceStartup;
            float halfW = FatHaloBiome.HalfWidth;
            s_bake = Task.Run(() => Bake(halfW));
        }

        // The cache file is the map's raw RGB bytes (MapW x MapH x 3): no image codec needed (FAT keeps clear of
        // UnityEngine.ImageConversionModule at compile time, see ZoneModeBlueprintIcons).
        private static byte[] ToRgb(Color32[] px)
        {
            var b = new byte[px.Length * 3];
            for (int i = 0, j = 0; i < px.Length; i++) { b[j++] = px[i].r; b[j++] = px[i].g; b[j++] = px[i].b; }
            return b;
        }

        private static Color32[] FromRgb(byte[] b)
        {
            var px = new Color32[b.Length / 3];
            for (int i = 0, j = 0; i < px.Length; i++, j += 3) px[i] = new Color32(b[j], b[j + 1], b[j + 2], 255);
            return px;
        }

        private static Texture2D NewMap() => new Texture2D(MapW, MapH, TextureFormat.RGBA32, true)
        {
            name = "FatHaloFarRing", wrapMode = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear, anisoLevel = 4,
        };

        private static void Use(Texture2D tex)
        {
            if (s_map != null && s_map != tex) UnityEngine.Object.Destroy(s_map);
            s_map = tex;
            if (s_mat != null) s_mat.mainTexture = tex;
            s_nextLight = 0f;   // repaint now
        }

        // Worker: one colour per texel from the ring's own biome field. Row 0 = one band edge, MapH - 1 = the other.
        private static Color32[] Bake(float halfW)
        {
            var px = new Color32[MapW * MapH];
            float rim = halfW > 0f ? RimBand / (2f * halfW) : 0f;
            for (int y = 0; y < MapH; y++)
            {
                float v01 = (y + 0.5f) / MapH;
                bool lip = v01 < rim || v01 > 1f - rim;
                for (int x = 0; x < MapW; x++)
                {
                    float u01 = (x + 0.5f) / MapW;
                    Color c;
                    if (lip) c = Cliff;
                    else
                    {
                        FatHaloBiome.SampleAt01(u01, v01, out var a, out var b, out float t);
                        c = Color.Lerp(Albedo(a), Albedo(b), t);
                    }
                    // a little per-texel grain so wide patches don't read as flat paint
                    uint h = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663));
                    h ^= h >> 13; h *= 0x5BD1E995u; h ^= h >> 15;
                    float g = 0.94f + 0.12f * ((h & 0xFFFF) / 65535f);
                    px[y * MapW + x] = new Color(c.r * g, c.g * g, c.b * g, 1f);
                }
            }
            return px;
        }

        private static readonly Color Cliff = new Color(0.45f, 0.43f, 0.40f, 1f);

        // What each biome's ground reads as from far away: its terrain colour with its forest cover folded in (Black Forest and
        // Mistlands read as dark woods, Plains as dry grass, the cold biomes as snow).
        private static Color Albedo(Heightmap.Biome b)
        {
            switch (b)
            {
                case Heightmap.Biome.Meadows: return new Color(0.42f, 0.55f, 0.22f, 1f);
                case Heightmap.Biome.BlackForest: return new Color(0.19f, 0.26f, 0.13f, 1f);
                case Heightmap.Biome.Swamp: return new Color(0.33f, 0.30f, 0.20f, 1f);
                case Heightmap.Biome.Plains: return new Color(0.66f, 0.60f, 0.33f, 1f);
                case Heightmap.Biome.Mistlands: return new Color(0.27f, 0.29f, 0.30f, 1f);
                case Heightmap.Biome.Mountain: return new Color(0.82f, 0.84f, 0.88f, 1f);
                case Heightmap.Biome.DeepNorth: return new Color(0.90f, 0.93f, 0.97f, 1f);
                case Heightmap.Biome.AshLands: return new Color(0.30f, 0.12f, 0.08f, 1f);
                default: return Ground;
            }
        }

        // ── the hoop mesh ───────────────────────────────────────────────────────────────────────────────────────────────
        private static void Build(string key, Vector3 origin, float r, float halfLength)
        {
            s_builtFor = key;
            s_lastCam = new Vector3(float.NaN, 0f, 0f);
            int cols = Segments + 1, rowsV = Rows + 1;
            s_world = new Vector3[cols * rowsV];
            s_out = new Vector3[s_world.Length];
            s_draw = new Vector3[s_world.Length];
            s_colors = new Color[s_world.Length];
            var normals = new Vector3[s_world.Length];
            var uvs = new Vector2[s_world.Length];
            // UVs in FatHaloBiome's unrolled frame: column 0's u, and which way u grows with the column, read from the field
            // itself, so the map lands on the same ground the near side shows.
            float u0 = 0f, du = 1f / Segments;
            if (FatHaloBiome.Ready
                && FatHaloBiome.TryUnroll01(origin + new Vector3(r, 0f, 0f), out float ua, out _)
                && FatHaloBiome.TryUnroll01(origin + new Vector3(Mathf.Cos(Mathf.PI * 2f / Segments) * r, 0f, Mathf.Sin(Mathf.PI * 2f / Segments) * r), out float ub, out _))
            {
                u0 = ua;
                float step = Mathf.Repeat(ub - ua + 0.5f, 1f) - 0.5f;
                du = step < 0f ? -1f / Segments : 1f / Segments;
            }
            else s_builtFor = null;   // the biome frame isn't up yet: build again next tick with the right UVs
            for (int y = 0; y < rowsV; y++)
            {
                float t = -halfLength + 2f * halfLength * y / Rows;
                for (int s = 0; s < cols; s++)
                {
                    float a = s * Mathf.PI * 2f / Segments;
                    var outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    int i = y * cols + s;
                    s_world[i] = origin + outward * r + Vector3.up * t;
                    s_out[i] = outward;
                    normals[i] = -outward;
                    uvs[i] = new Vector2(u0 + du * s, (float)y / Rows);
                    s_colors[i] = Color.white;   // 0.2.1047: FiresHaloFar multiplies the map by the vertex colour (the CPU path rewrites it)
                }
            }
            var tris = new int[Segments * Rows * 6];
            int n = 0;
            for (int y = 0; y < Rows; y++)
                for (int s = 0; s < Segments; s++)
                {
                    int a = y * cols + s, b = a + 1, c = a + cols, d = c + 1;
                    // Facing the axis (seen from inside the ring).
                    tris[n++] = a; tris[n++] = c; tris[n++] = b;
                    tris[n++] = b; tris[n++] = c; tris[n++] = d;
                }
            if (s_mesh == null) s_mesh = new Mesh { name = "FatHaloSkyHoop" };
            s_mesh.Clear();
            s_mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            s_mesh.vertices = s_world;
            s_mesh.normals = normals;
            s_mesh.uv = uvs;
            s_mesh.colors = s_colors;
            s_mesh.triangles = tris;
            if (Gpu)
            {
                // phase 2: the mesh never changes again; the shader squeezes it. A huge bound keeps it from being culled.
                s_mesh.bounds = new Bounds(origin, Vector3.one * 40000f);
                s_mesh.UploadMeshData(false);
            }
            else s_mesh.MarkDynamic();
            if (s_mat == null && Gpu)
            {
                s_mat = new Material(FarShader) { name = "FatHaloFar", renderQueue = 2450 };
                ApplyFarDefaults(s_mat);
                s_mat.SetFloat("_Cutoff", 0f);     // the floor never clips
                s_mat.SetFloat("_HideNear", 0f);
                if (s_map != null) s_mat.mainTexture = s_map;
            }
            if (s_mat == null)
            {
                var sh = Shader.Find("Sprites/Default");
                if (sh == null) { s_failed = true; FiresLogger.LogWarning("[FatHalo] sky hoop off: no Sprites/Default shader"); return; }
                s_mat = new Material(sh) { name = "FatHaloSkyHoop", renderQueue = 2450 };   // after opaque geometry: real ground wins depth
                s_mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                if (s_map != null) s_mat.mainTexture = s_map;
            }
            FiresLogger.LogDebug($"[FatHalo] sky hoop built: {Segments} x {Rows} quads at radius {r:F0} m, uv u0 {u0:F4} step {du:F6}");
        }
    }
}
