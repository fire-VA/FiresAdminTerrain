using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // FAR VEGETATION BILLBOARDS — past the real vegetation ring the terrain
    // ran bald to the horizon. Each species is BAKED ONCE at load (prefab →
    // ortho render → cutout texture) and drawn as GPU-instanced CROSSED QUADS
    // (16 tris/tree vs. a few-hundred-tri LOD mesh).
    //
    // Cost model (the 22-FPS regression was all here):
    //  • Tiles build ONCE and PERSIST — a 128m tile's placement is a
    //    deterministic per-8m-cell hash over the analytic gen surface, so it
    //    never changes until the terrain is edited. Built tiles stay cached
    //    (LRU-capped); re-entering a zone never rebuilds.
    //  • The build (256 cells × biome/height/forest/wetness sampling) runs on
    //    a BACKGROUND THREAD — the gen samplers are pure reads over immutable
    //    load-time arrays with per-thread memos, so no main-thread hitch.
    //  • Draw batches (per-species Matrix4x4[] chunks) are CACHED and rebuilt
    //    only when the player crosses a tile boundary or a tile arrives — a
    //    standing/turning player just issues a few DrawMeshInstanced calls per
    //    frame with zero allocation. Rendering is GPU-instanced end to end.
    internal static class FarVegImpostors
    {
        public static bool Enabled = true;
        public static float RadiusMeters = 2400f;
        public static float DensityMul = 1f;

        private const float TILE = 128f;
        private const float CELL = 8f;
        private const int BAKE_RES = 256;
        private const int BATCH = 1023;      // DrawMeshInstanced hard cap
        private const int TILE_CAP = 6000;   // LRU ceiling (~roaming safety)

        private sealed class Species
        {
            public string Prefab;
            public Heightmap.Biome Biomes;
            public float Density;      // chance per 8m cell (pre-DensityMul)
            public float AltMin, AltMax;
            public float SlopeMax;     // max Δy over the 8.5m slope probe
            public float OpenMul;      // density multiplier outside forest patches
            public Mesh Quad;          // crossed quads sized to the prefab (main thread)
            public Material Mat;       // baked billboard, instancing on (main thread)
            public volatile bool Broken;
        }

        // OpenMul is a SOFT forest gate — the old hard ForestOnly gate left
        // every open meadow/plain bald to the horizon, which read as "no
        // impostors at all". Rocks get a high SlopeMax so mountainsides
        // (which fail the tree gate) still show cover; fir climbs to the
        // 260m treeline, mountain rock all the way to the 620m summits.
        private static readonly Species[] s_species =
        {
            new Species { Prefab = "Beech1",         Biomes = Heightmap.Biome.Meadows,     Density = 0.34f, AltMin = 31f, AltMax = 95f,  SlopeMax = 5.5f, OpenMul = 0.15f },
            new Species { Prefab = "Oak1",           Biomes = Heightmap.Biome.Meadows,     Density = 0.05f, AltMin = 31f, AltMax = 80f,  SlopeMax = 5.5f, OpenMul = 1f },
            new Species { Prefab = "Pinetree_01",    Biomes = Heightmap.Biome.BlackForest, Density = 0.38f, AltMin = 31f, AltMax = 170f, SlopeMax = 6.5f, OpenMul = 1f },
            new Species { Prefab = "FirTree",        Biomes = Heightmap.Biome.BlackForest | Heightmap.Biome.Mountain, Density = 0.34f, AltMin = 31f, AltMax = 300f, SlopeMax = 15f, OpenMul = 1f },
            new Species { Prefab = "SwampTree1",     Biomes = Heightmap.Biome.Swamp,       Density = 0.22f, AltMin = 29f, AltMax = 60f,  SlopeMax = 5.5f, OpenMul = 1f },
            new Species { Prefab = "Birch1",         Biomes = Heightmap.Biome.Plains,      Density = 0.08f, AltMin = 31f, AltMax = 90f,  SlopeMax = 5.5f, OpenMul = 1f },
            new Species { Prefab = "rock4_forest",   Biomes = Heightmap.Biome.BlackForest | Heightmap.Biome.Meadows, Density = 0.03f, AltMin = 31f, AltMax = 200f, SlopeMax = 12f, OpenMul = 1f },
            // Mountains are CRAGGY — rocks belong on the steep faces + snow caps
            // above the tree line, so a high SlopeMax and heavier density fill
            // the peak the trees (tree-line 285m) can't reach.
            new Species { Prefab = "rock1_mountain", Biomes = Heightmap.Biome.Mountain | Heightmap.Biome.Plains, Density = 0.34f, AltMin = 40f, AltMax = 620f, SlopeMax = 30f, OpenMul = 1f },
        };

        private sealed class Tile
        {
            public Matrix4x4[][] PerSpecies;   // built once off-thread — never mutated after
            public Vector3 Center;
            public bool Any;
        }

        private static readonly Dictionary<Vector2Int, Tile> s_tiles = new Dictionary<Vector2Int, Tile>();

        // ── off-thread build plumbing ──
        private static readonly ConcurrentQueue<Vector2Int> s_buildQueue = new ConcurrentQueue<Vector2Int>();
        private static readonly ConcurrentQueue<KeyValuePair<Vector2Int, Tile>> s_doneQueue = new ConcurrentQueue<KeyValuePair<Vector2Int, Tile>>();
        private static readonly HashSet<Vector2Int> s_inFlight = new HashSet<Vector2Int>();
        private static readonly AutoResetEvent s_wake = new AutoResetEvent(false);
        private static Thread s_worker;
        private static volatile bool s_running;

        // ── cached draw batches (rebuilt only on tile-cross / arrival) ──
        // Chunk arrays are POOLED (persistent, always BATCH-sized) and reused;
        // s_chunkCounts holds the live instance count per chunk. Rebuilding no
        // longer allocates (the old `new Matrix4x4[n]` per rebuild churned GC
        // on every camera turn — a real stutter source).
        private static readonly List<Matrix4x4[]>[] s_chunks = new List<Matrix4x4[]>[s_species.Length];
        private static readonly List<int>[] s_chunkCounts = new List<int>[s_species.Length];
        private static readonly List<Matrix4x4>[] s_flat = new List<Matrix4x4>[s_species.Length];
        private static bool s_batchesDirty;
        private static Vector2Int s_lastBatchTile = new Vector2Int(int.MinValue, int.MinValue);
        private static Vector2 s_lastBatchFwd = Vector2.up;
        private static int s_drawnInst;
        private static int s_bandNear, s_bandMid, s_bandFar;
        private static float s_nextStatLog;
        private static string s_shaderName = "?";
        private static bool s_baked;

        public static void Tick()
        {
            if (!Enabled || !FiresWorldGenBase.Active || ZNetScene.instance == null)
            {
                if (s_tiles.Count > 0) { s_tiles.Clear(); s_batchesDirty = true; }
                return;
            }
            var cam = Utils.GetMainCamera();
            var player = Player.m_localPlayer;
            if (cam == null || player == null) return;

            if (!s_baked && !BakeBillboards()) { DrainDone(); return; }
            EnsureWorker();

            Vector3 pp = player.transform.position;
            int ptx = Mathf.FloorToInt(pp.x / TILE), ptz = Mathf.FloorToInt(pp.z / TILE);
            float radius = FiresVoxelConfig.ImpostorRadius?.Value ?? RadiusMeters;
            float fwdRadius = Mathf.Max(radius, FiresVoxelConfig.ImpostorForwardRadius?.Value ?? radius);
            int range = Mathf.CeilToInt(fwdRadius / TILE);
            // Density read on the main thread; the worker's BuildTile uses it.
            DensityMul = FiresVoxelConfig.ImpostorDensity?.Value ?? 0.65f;
            // Camera forward on the XZ plane — drives the "see further where I'm
            // looking" cone below.
            Vector3 f3 = cam.transform.forward;
            Vector2 fwd = new Vector2(f3.x, f3.z);
            fwd = fwd.sqrMagnitude < 1e-4f ? Vector2.up : fwd.normalized;

            // Enqueue any missing tile once (ring order → nearest first), but
            // only the CIRCULAR radius disk plus the forward cone beyond it —
            // NOT the whole forward-radius square in every direction (that built
            // 5000+ tiles, blew the LRU cap, and churned). The prebuild cone is
            // a bit wider than the draw cone so tiles are ready before a turn
            // sweeps them into view.
            if (FiresWorldGenBase.GridReady)
            {
                float rad2 = radius * radius, fwd2 = fwdRadius * fwdRadius;
                const float PREBUILD_COS = CONE_COS - 0.15f;
                for (int ring = 0; ring <= range; ring++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        for (int dx = -ring; dx <= ring; dx++)
                        {
                            if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring) continue;
                            float ex = (ptx + dx) * TILE + TILE * 0.5f - pp.x;
                            float ez = (ptz + dz) * TILE + TILE * 0.5f - pp.z;
                            float ed2 = ex * ex + ez * ez;
                            if (ed2 > rad2)
                            {
                                if (ed2 > fwd2) continue;
                                float inv = 1f / Mathf.Sqrt(ed2 > 1e-4f ? ed2 : 1f);
                                if (ex * fwd.x * inv + ez * fwd.y * inv < PREBUILD_COS) continue;
                            }
                            var key = new Vector2Int(ptx + dx, ptz + dz);
                            if (s_tiles.ContainsKey(key) || s_inFlight.Contains(key)) continue;
                            s_inFlight.Add(key);
                            s_buildQueue.Enqueue(key);
                        }
                    }
                }
                if (!s_buildQueue.IsEmpty) s_wake.Set();
            }

            DrainDone();

            // LRU: over the cap, drop the tiles farthest from the player.
            if (s_tiles.Count > TILE_CAP) EvictFarTiles(ptx, ptz);

            // Rebuild the draw batches on a tile crossing OR when the camera has
            // turned enough that the forward cone points somewhere new (~12°).
            var curTile = new Vector2Int(ptx, ptz);
            if (curTile != s_lastBatchTile) { s_lastBatchTile = curTile; s_batchesDirty = true; }
            if (Vector2.Dot(fwd, s_lastBatchFwd) < 0.978f) { s_lastBatchFwd = fwd; s_batchesDirty = true; }

            if (s_batchesDirty) RebuildBatches(pp, fwd, radius, fwdRadius);

            // Per-frame: just replay the cached instanced batches. No culling,
            // no copies — 16-tri billboards are trivial on the GPU and the
            // stable batch means turning the camera costs nothing.
            for (int si = 0; si < s_species.Length; si++)
            {
                var sp = s_species[si];
                var chunks = s_chunks[si];
                var counts = s_chunkCounts[si];
                if (sp.Broken || sp.Quad == null || sp.Mat == null || chunks == null || counts == null) continue;
                for (int c = 0; c < counts.Count; c++)
                {
                    int n = counts[c];
                    if (n <= 0) continue;
                    try
                    {
                        Graphics.DrawMeshInstanced(sp.Quad, 0, sp.Mat, chunks[c], n, null,
                            UnityEngine.Rendering.ShadowCastingMode.Off, false);
                    }
                    catch (System.Exception ex)
                    {
                        sp.Broken = true;
                        Debug.LogWarning($"[FiresVoxel] far billboard species '{sp.Prefab}' disabled: {ex.Message}");
                        break;
                    }
                }
            }

            if (Time.time >= s_nextStatLog)
            {
                s_nextStatLog = Time.time + 30f;
                Debug.Log($"[FiresVoxel] impostors: tiles={s_tiles.Count} inFlight={s_inFlight.Count} " +
                          $"drawn={s_drawnInst} inst (0-1km {s_bandNear} / 1-2km {s_bandMid} / 2km+ {s_bandFar}), " +
                          $"shader={s_shaderName}.");
                // Forward-terrain probe: sample the analytic gen along the view
                // line so we can see WHY distant terrain is bare — biome / alt /
                // slope / wet at each range decide whether a species places.
                var sb = new System.Text.StringBuilder("[FiresVoxel] fwd probe:");
                foreach (float d in s_probeDist)
                {
                    float px = pp.x + fwd.x * d, pz = pp.z + fwd.y * d;
                    var bi = FiresWorldGenBase.SampleBiome(px, pz);
                    float al = FiresWorldGenBase.SampleFinalMeters(px, pz);
                    float al2 = FiresWorldGenBase.SampleFinalMeters(px + 6f, pz + 6f);
                    bool wet = FiresWorldGenBase.IsWetAt(px, pz);
                    sb.Append($" {d/1000f:0.0}km[{bi} y{al:0} d{Mathf.Abs(al2 - al):0}{(wet ? " WET" : "")}]");
                }
                Debug.Log(sb.ToString());
            }
        }

        private static readonly float[] s_probeDist = { 500f, 1000f, 1500f, 2000f, 2500f, 3000f, 4000f };

        // Terrain edit → the overlapping tile's placement is stale. Drop it;
        // it rebuilds off-thread on the next Tick. (Impostors live far past
        // reach so this rarely fires, but the contract is "persist until edited".)
        public static void Invalidate(float wx, float wz)
        {
            var key = new Vector2Int(Mathf.FloorToInt(wx / TILE), Mathf.FloorToInt(wz / TILE));
            if (s_tiles.Remove(key)) s_batchesDirty = true;
        }

        private static void DrainDone()
        {
            while (s_doneQueue.TryDequeue(out var kv))
            {
                s_inFlight.Remove(kv.Key);
                s_tiles[kv.Key] = kv.Value;
                s_batchesDirty = true;
            }
        }

        private static readonly List<Vector2Int> s_evict = new List<Vector2Int>();
        private static void EvictFarTiles(int ptx, int ptz)
        {
            s_evict.Clear();
            int overflow = s_tiles.Count - TILE_CAP;
            float fwdRadius = Mathf.Max(FiresVoxelConfig.ImpostorRadius?.Value ?? RadiusMeters,
                                        FiresVoxelConfig.ImpostorForwardRadius?.Value ?? RadiusMeters);
            int keepRing = Mathf.CeilToInt(fwdRadius / TILE) + 2;
            // Cheap approximate LRU: collect everything past the keep ring and
            // drop the overflow-worst. Runs only when the cap is exceeded.
            foreach (var kv in s_tiles)
            {
                int d = Mathf.Max(Mathf.Abs(kv.Key.x - ptx), Mathf.Abs(kv.Key.y - ptz));
                if (d > keepRing) s_evict.Add(kv.Key);
            }
            s_evict.Sort((a, b) =>
                Mathf.Max(Mathf.Abs(b.x - ptx), Mathf.Abs(b.y - ptz))
                    .CompareTo(Mathf.Max(Mathf.Abs(a.x - ptx), Mathf.Abs(a.y - ptz))));
            for (int i = 0; i < s_evict.Count && i < overflow; i++) s_tiles.Remove(s_evict[i]);
            s_batchesDirty = true;
        }

        // CONE_COS = cos(~62°): a ~124° forward wedge gets the extended reach.
        private const float CONE_COS = 0.47f;

        private static void RebuildBatches(Vector3 pp, Vector2 fwd, float radius, float fwdRadius)
        {
            s_batchesDirty = false;
            var zs = ZoneSystem.instance;
            // Start past where the real trees render, else the near billboards
            // just hide behind the real forest and waste frames.
            float nearSkip = Mathf.Max(zs != null ? (zs.m_activeArea + 1) * 64f : 192f,
                                       FiresVoxelConfig.ImpostorNearSkip?.Value ?? 300f);
            float nearEdge = Mathf.Max(0f, nearSkip - TILE);   // one-tile skirt
            float near2 = nearEdge * nearEdge;
            float near2All = radius * radius, far2 = fwdRadius * fwdRadius;

            for (int si = 0; si < s_species.Length; si++)
                (s_flat[si] ?? (s_flat[si] = new List<Matrix4x4>(8192))).Clear();

            int bandNear = 0, bandMid = 0, bandFar = 0;   // 0-1km / 1-2km / 2km+ verify
            foreach (var kv in s_tiles)
            {
                var tile = kv.Value;
                if (tile == null || !tile.Any) continue;
                float ddx = tile.Center.x - pp.x, ddz = tile.Center.z - pp.z;
                float d2 = ddx * ddx + ddz * ddz;
                if (d2 < near2) continue;                       // inside real veg ring
                // Draw the full circle out to `radius`; between `radius` and
                // `fwdRadius` draw ONLY the forward cone — trees/rocks reach up
                // the mountain you're facing, nothing rendered behind you. Fog
                // (FiresImpostor shader) fades the far edge into haze.
                if (d2 > near2All)
                {
                    if (d2 > far2) continue;
                    float inv = 1f / Mathf.Sqrt(d2 > 1e-4f ? d2 : 1f);
                    if (ddx * fwd.x * inv + ddz * fwd.y * inv < CONE_COS) continue;
                }
                int tileCount = 0;
                for (int si = 0; si < s_species.Length; si++)
                {
                    var arr = tile.PerSpecies[si];
                    if (arr != null && arr.Length > 0) { s_flat[si].AddRange(arr); tileCount += arr.Length; }
                }
                if (d2 < 1_000_000f) bandNear += tileCount;
                else if (d2 < 4_000_000f) bandMid += tileCount;
                else bandFar += tileCount;
            }
            s_bandNear = bandNear; s_bandMid = bandMid; s_bandFar = bandFar;

            int total = 0;
            for (int si = 0; si < s_species.Length; si++)
            {
                var list = s_chunks[si] ?? (s_chunks[si] = new List<Matrix4x4[]>());
                var counts = s_chunkCounts[si] ?? (s_chunkCounts[si] = new List<int>());
                counts.Clear();
                var flat = s_flat[si];
                int ci = 0;
                for (int off = 0; off < flat.Count; off += BATCH, ci++)
                {
                    int n = Mathf.Min(BATCH, flat.Count - off);
                    Matrix4x4[] arr;
                    if (ci < list.Count) arr = list[ci];             // reuse pooled array
                    else { arr = new Matrix4x4[BATCH]; list.Add(arr); }
                    flat.CopyTo(off, arr, 0, n);
                    counts.Add(n);
                }
                total += flat.Count;
            }
            s_drawnInst = total;
        }

        // ── background worker: pure sampling → matrices ──
        private static void EnsureWorker()
        {
            if (s_worker != null) return;
            s_running = true;
            s_worker = new Thread(WorkerLoop) { IsBackground = true, Name = "FiresImpostorBuild" };
            s_worker.Start();
        }

        private static void WorkerLoop()
        {
            while (s_running)
            {
                if (s_buildQueue.IsEmpty) s_wake.WaitOne(1000);
                while (s_buildQueue.TryDequeue(out var key))
                {
                    Tile tile;
                    try { tile = BuildTile(key); }
                    catch { tile = new Tile { PerSpecies = new Matrix4x4[s_species.Length][], Center = TileCenter(key) }; }
                    s_doneQueue.Enqueue(new KeyValuePair<Vector2Int, Tile>(key, tile));
                }
            }
        }

        private static Vector3 TileCenter(Vector2Int key)
            => new Vector3(key.x * TILE + TILE * 0.5f, 0f, key.y * TILE + TILE * 0.5f);

        [ThreadStatic] private static List<Matrix4x4>[] t_build;

        private static Tile BuildTile(Vector2Int key)
        {
            var tile = new Tile
            {
                PerSpecies = new Matrix4x4[s_species.Length][],
                Center = TileCenter(key),
            };
            var build = t_build ?? (t_build = new List<Matrix4x4>[s_species.Length]);
            for (int si = 0; si < s_species.Length; si++)
                (build[si] ?? (build[si] = new List<Matrix4x4>(64))).Clear();

            int cells = (int)(TILE / CELL);
            float hSum = 0f; int hCnt = 0;
            for (int cz = 0; cz < cells; cz++)
            {
                for (int cx = 0; cx < cells; cx++)
                {
                    float wx = key.x * TILE + (cx + 0.5f) * CELL;
                    float wz = key.y * TILE + (cz + 0.5f) * CELL;
                    uint h = Hash((int)wx, (int)wz);
                    float r01 = (h & 0xFFFF) / 65536f;

                    var biome = FiresWorldGenBase.SampleBiome(wx, wz);
                    float y = FiresWorldGenBase.SampleFinalMeters(wx, wz);
                    if (y < 28f) continue;
                    if (FiresWorldGenBase.IsWetAt(wx, wz)) continue;
                    float y2 = FiresWorldGenBase.SampleFinalMeters(wx + 6f, wz + 6f);
                    float dy = Mathf.Abs(y2 - y);
                    if (dy > 32f) continue;   // per-species SlopeMax gates below; rocks tolerate steep mountain faces

                    bool forest = WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz)) < 1.15f;
                    float biomeCov = BiomeCoverage(biome);
                    float pickR = ((h >> 16) & 0xFFFF) / 65536f;
                    for (int si = 0; si < s_species.Length; si++)
                    {
                        var sp = s_species[si];
                        if (sp.Broken || (sp.Biomes & biome) == 0) continue;
                        if (y < sp.AltMin || y > sp.AltMax || dy > sp.SlopeMax) continue;
                        float density = sp.Density * DensityMul * biomeCov * (forest ? 1f : sp.OpenMul);
                        if (r01 >= density) continue;
                        float jx = (((h >> 8) & 0xFF) / 255f - 0.5f) * CELL;
                        float jz = (((h >> 20) & 0xFF) / 255f - 0.5f) * CELL;
                        float yaw = pickR * 360f;
                        float scale = 0.85f + pickR * 0.5f;
                        build[si].Add(Matrix4x4.TRS(new Vector3(wx + jx, y - 0.2f, wz + jz),
                                      Quaternion.Euler(0f, yaw, 0f), Vector3.one * scale));
                        tile.Any = true;
                        hSum += y; hCnt++;
                        break;
                    }
                }
            }
            for (int si = 0; si < s_species.Length; si++)
                if (build[si].Count > 0) tile.PerSpecies[si] = build[si].ToArray();
            if (hCnt > 0) tile.Center = new Vector3(tile.Center.x, hSum / hCnt, tile.Center.z);
            return tile;
        }

        // ── one-time billboard bake: prefab → ortho render → cutout quad ──
        private static bool BakeBillboards()
        {
            var zns = ZNetScene.instance;
            if (zns == null) return false;

            // Our own bundle shader first — it is the only one GUARANTEED to
            // carry instancing variants (stock legacy cutouts may not, and a
            // non-instancing material makes DrawMeshInstanced a silent no-op).
            Shader shader = null;
            try { shader = VerdantsAscent.FiresAdminTerrain.Instance?.GetAssetBundle()?.LoadAsset<Shader>("FiresImpostor"); }
            catch { }
            shader = shader
                        ?? Shader.Find("Unlit/Transparent Cutout")
                        ?? Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse")
                        ?? Shader.Find("Sprites/Default");
            if (shader == null) { Debug.LogWarning("[FiresVoxel] no billboard shader available"); return false; }
            s_shaderName = shader.name;

            const int BAKE_LAYER = 30;
            var camGO = new GameObject("FiresImpostorBakeCam");
            var cam = camGO.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << BAKE_LAYER;
            cam.nearClipPlane = 0.1f;

            var lightGO = new GameObject("FiresImpostorBakeLight");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.color = Color.white;
            light.cullingMask = 1 << BAKE_LAYER;
            light.transform.rotation = Quaternion.Euler(40f, 205f, 0f);

            var rt = new RenderTexture(BAKE_RES, BAKE_RES, 24, RenderTextureFormat.ARGB32);
            int ok = 0;
            foreach (var sp in s_species)
            {
                if (sp.Broken || sp.Quad != null) continue;
                var prefab = zns.GetPrefab(sp.Prefab);
                if (prefab == null) { sp.Broken = true; continue; }
                GameObject inst = null, bakeRoot = null;
                try
                {
                    // Instantiate under an INACTIVE root so no Awake runs — a
                    // live ZNetView registers in ZNetScene.m_instances and our
                    // Destroy would orphan the entry (stale-instance diagnostic
                    // spam for every species). Strip all MonoBehaviours before
                    // activating; renderers aren't MonoBehaviours, so the mesh
                    // still bakes.
                    bakeRoot = new GameObject("FiresImpostorBakeRoot");
                    bakeRoot.transform.position = new Vector3(0f, -6000f, 0f);
                    bakeRoot.SetActive(false);
                    inst = UnityEngine.Object.Instantiate(prefab, bakeRoot.transform);
                    inst.transform.localPosition = Vector3.zero;
                    foreach (var mb in inst.GetComponentsInChildren<MonoBehaviour>(true))
                        try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
                    foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = BAKE_LAYER;
                    bakeRoot.SetActive(true);

                    var bounds = new Bounds(inst.transform.position, Vector3.zero);
                    bool got = false;
                    foreach (var r in inst.GetComponentsInChildren<Renderer>())
                    {
                        if (r is ParticleSystemRenderer) continue;
                        if (!got) { bounds = r.bounds; got = true; } else bounds.Encapsulate(r.bounds);
                    }
                    if (!got) { sp.Broken = true; continue; }

                    float w = Mathf.Max(bounds.size.x, bounds.size.z);
                    float hgt = bounds.size.y;
                    float half = Mathf.Max(w, hgt) * 0.5f * 1.05f;
                    cam.orthographicSize = half;
                    cam.farClipPlane = w * 4f + 50f;
                    cam.transform.position = bounds.center - new Vector3(0f, 0f, w * 2f + 10f);
                    cam.transform.rotation = Quaternion.identity;
                    cam.targetTexture = rt;
                    cam.Render();

                    RenderTexture.active = rt;
                    var tex = new Texture2D(BAKE_RES, BAKE_RES, TextureFormat.RGBA32, true);
                    tex.ReadPixels(new Rect(0, 0, BAKE_RES, BAKE_RES), 0, 0);
                    tex.Apply(true, false);
                    RenderTexture.active = null;
                    tex.wrapMode = TextureWrapMode.Clamp;

                    sp.Mat = new Material(shader) { mainTexture = tex };
                    if (sp.Mat.HasProperty("_Cutoff")) sp.Mat.SetFloat("_Cutoff", 0.35f);
                    sp.Mat.enableInstancing = true;

                    // Crossed quads sized to the baked frame (square, side =
                    // 2*half), pivot at the prefab's base so y sits on ground.
                    float baseY = bounds.min.y - inst.transform.position.y;
                    float side = half * 2f;
                    float y0 = baseY + (bounds.center.y - inst.transform.position.y) - half;
                    sp.Quad = BuildCrossedQuads(side, y0);
                    ok++;
                }
                catch (System.Exception ex)
                {
                    sp.Broken = true;
                    Debug.LogWarning($"[FiresVoxel] billboard bake failed for '{sp.Prefab}': {ex.Message}");
                }
                finally
                {
                    if (bakeRoot != null) UnityEngine.Object.Destroy(bakeRoot);
                }
            }
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(rt);
            UnityEngine.Object.Destroy(camGO);
            UnityEngine.Object.Destroy(lightGO);
            s_baked = ok > 0;
            if (s_baked) Debug.Log($"[FiresVoxel] far billboards baked: {ok} species (16 tris each), shader={s_shaderName}.");
            return s_baked;
        }

        // Two perpendicular quads, both windings (visible from every side):
        // 16 triangles total.
        private static Mesh BuildCrossedQuads(float side, float y0)
        {
            float hw = side * 0.5f;
            float y1 = y0 + side;
            var verts = new Vector3[16];
            var uvs = new Vector2[16];
            var tris = new int[24];
            int v = 0, t = 0;
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                verts[v] = a; verts[v + 1] = b; verts[v + 2] = c; verts[v + 3] = d;
                uvs[v] = new Vector2(0, 0); uvs[v + 1] = new Vector2(1, 0);
                uvs[v + 2] = new Vector2(1, 1); uvs[v + 3] = new Vector2(0, 1);
                tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
                tris[t + 3] = v; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
                v += 4; t += 6;
            }
            Quad(new Vector3(-hw, y0, 0), new Vector3(hw, y0, 0), new Vector3(hw, y1, 0), new Vector3(-hw, y1, 0));
            Quad(new Vector3(hw, y0, 0), new Vector3(-hw, y0, 0), new Vector3(-hw, y1, 0), new Vector3(hw, y1, 0));
            Quad(new Vector3(0, y0, -hw), new Vector3(0, y0, hw), new Vector3(0, y1, hw), new Vector3(0, y1, -hw));
            Quad(new Vector3(0, y0, hw), new Vector3(0, y0, -hw), new Vector3(0, y1, -hw), new Vector3(0, y1, hw));
            var mesh = new Mesh { name = "FiresImpostorQuads" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Per-biome continuous-coverage multiplier (DonegalHorizonLift model —
        // each biome gets its own treeline density instead of one flat rate).
        // Mountain is lifted because sparse rock on white snow read as bare;
        // open Plains is thinned. Multiplies the per-species chance.
        private static float BiomeCoverage(Heightmap.Biome b)
        {
            switch (b)
            {
                case Heightmap.Biome.Mountain:    return 1.35f;
                case Heightmap.Biome.BlackForest: return 1.15f;
                case Heightmap.Biome.Mistlands:   return 1.1f;
                case Heightmap.Biome.Swamp:       return 1.05f;
                case Heightmap.Biome.Plains:      return 0.8f;
                case Heightmap.Biome.AshLands:    return 0.7f;
                default:                          return 1f;   // Meadows / DeepNorth / rest
            }
        }

        private static uint Hash(int x, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) ^ (uint)(z * 668265263) ^ 0x9E3779B9u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                return h;
            }
        }
    }
}
