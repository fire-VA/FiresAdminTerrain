using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Terrain.Voxel
{
    // W2 — world hydrology. Once per login (worker thread, ~2-4s), sample the
    // world generator into a coarse grid and run the pipeline proven in the
    // offline prototype (Tools/FiresWorldGen):
    //   * ε-sloped priority-flood fill  → still-water surfaces (ponds/lakes)
    //   * D8 flow accumulation          → river network + carve depths
    //   * headwater detection           → spring points (spaced)
    // Products are pure arrays — chunk generation samples them (worker-safe)
    // to CARVE river beds into the voxel density (wrap-and-subtract, exactly
    // like caves), and the zone streamer places our existing bank-following
    // SPRING pours at headwaters + settling water volumes on ponds, so the
    // beds fill with flowing water using entirely existing water tech.
    //
    // Deterministic: same world seed → same hydrology, every login.
    public static class VoxelHydrology
    {
        private const int RES = 1024;            // grid cells per axis
        private const float SEA = 30f;

        private static float[] s_ground;          // baked WG ground height
        private static float[] s_bedY;            // carved bed Y (MaxValue = no river)
        private static float[] s_stillY;          // pond/lake surface Y (MinValue = none)
        private static readonly List<Vector3> s_springs = new List<Vector3>();
        private static float s_worldRadius = 10240f;
        private static float s_cell;               // metres per cell
        private static volatile bool s_ready;
        private static volatile bool s_building;

        public static bool Ready => s_ready;
        public static int SpringCount => s_springs.Count;

        public static void EnsureBuilding()
        {
            if (s_ready || s_building) return;
            if (WorldGenerator.instance == null) return;
            s_building = true;
            var wg = WorldGenerator.instance;
            var t = new Thread(() => Build(wg)) { IsBackground = true, Name = "FiresVoxelHydrology" };
            t.Start();
        }

        private static void Build(WorldGenerator wg)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                s_cell = s_worldRadius * 2f / RES;
                var ground = new float[RES * RES];

                // 1) Sample the generator (WG height math is worker-safe — vanilla
                //    calls it from HeightmapBuilder threads).
                for (int z = 0; z < RES; z++)
                {
                    float wz = -s_worldRadius + (z + 0.5f) * s_cell;
                    for (int x = 0; x < RES; x++)
                    {
                        float wx = -s_worldRadius + (x + 0.5f) * s_cell;
                        ground[z * RES + x] = wg.GetHeight(wx, wz);
                    }
                }
                long sampleMs = sw.ElapsedMilliseconds;

                // 2) ε-sloped priority-flood fill.
                var W = new float[RES * RES];
                var visited = new bool[RES * RES];
                var pq = new PriorityQueue();
                for (int i = 0; i < W.Length; i++) W[i] = float.MaxValue;
                void Push(int idx, float lvl)
                {
                    if (visited[idx]) return;
                    visited[idx] = true;
                    W[idx] = lvl;
                    pq.Enqueue(idx, lvl);
                }
                for (int x = 0; x < RES; x++)
                {
                    Push(x, Mathf.Max(ground[x], SEA));
                    Push((RES - 1) * RES + x, Mathf.Max(ground[(RES - 1) * RES + x], SEA));
                }
                for (int y = 0; y < RES; y++)
                {
                    Push(y * RES, Mathf.Max(ground[y * RES], SEA));
                    Push(y * RES + RES - 1, Mathf.Max(ground[y * RES + RES - 1], SEA));
                }
                int[] dx8 = { -1, 1, 0, 0, -1, -1, 1, 1 };
                int[] dy8 = { 0, 0, -1, 1, -1, 1, -1, 1 };
                while (pq.TryDequeue(out int cur, out float lvl))
                {
                    int cx = cur % RES, cy = cur / RES;
                    for (int k = 0; k < 8; k++)
                    {
                        int nx = cx + dx8[k], ny = cy + dy8[k];
                        if (nx < 0 || nx >= RES || ny < 0 || ny >= RES) continue;
                        int ni = ny * RES + nx;
                        if (visited[ni]) continue;
                        Push(ni, Mathf.Max(Mathf.Max(ground[ni], SEA), lvl + 0.0004f));
                    }
                }
                long fillMs = sw.ElapsedMilliseconds;

                // 3) D8 flow accumulation (high → low over the filled surface).
                var order = new int[RES * RES];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                System.Array.Sort(order, (a, b) => W[b].CompareTo(W[a]));
                var acc = new float[RES * RES];
                for (int i = 0; i < acc.Length; i++) acc[i] = 1f;
                foreach (int idx in order)
                {
                    if (ground[idx] <= SEA) continue;
                    int cx = idx % RES, cy = idx / RES;
                    int best = -1; float bestDrop = 0f;
                    for (int k = 0; k < 8; k++)
                    {
                        int nx = cx + dx8[k], ny = cy + dy8[k];
                        if (nx < 0 || nx >= RES || ny < 0 || ny >= RES) continue;
                        int ni = ny * RES + nx;
                        float drop = (W[idx] - W[ni]) / (k < 4 ? 1f : 1.41421356f);
                        if (drop > bestDrop) { bestDrop = drop; best = ni; }
                    }
                    if (best >= 0) acc[best] += acc[idx];
                }
                long flowMs = sw.ElapsedMilliseconds;

                // 4) Products.
                float threshold = FiresVoxelConfig.RiverThreshold.Value;
                float maxCarve = FiresVoxelConfig.RiverMaxDepth.Value;
                var bedY = new float[RES * RES];
                var stillY = new float[RES * RES];
                var springs = new List<Vector3>();
                for (int i = 0; i < RES * RES; i++)
                {
                    bedY[i] = float.MaxValue;
                    stillY[i] = float.MinValue;
                    if (ground[i] <= SEA) continue;

                    float depth = W[i] - ground[i];
                    if (depth > 0.3f) stillY[i] = W[i];   // pond/lake water surface

                    if (acc[i] >= threshold)
                    {
                        // Carve depth grows with catchment (log-ish) up to max.
                        float t = Mathf.Log10(Mathf.Max(1f, acc[i] / threshold));
                        float carve = Mathf.Min(maxCarve, 1.4f + t * 2.2f);
                        bedY[i] = ground[i] - carve;
                    }
                }

                // Springs — headwater cells with spacing.
                float springSpacing = FiresVoxelConfig.SpringSpacing.Value;
                float spacing2 = springSpacing * springSpacing;
                for (int y = 1; y < RES - 1; y++)
                {
                    for (int x = 1; x < RES - 1; x++)
                    {
                        int i = y * RES + x;
                        if (ground[i] <= SEA + 3f || acc[i] < threshold || acc[i] > threshold * 3f) continue;
                        bool head = true;
                        for (int k = 0; k < 8 && head; k++)
                        {
                            int ni = (y + dy8[k]) * RES + (x + dx8[k]);
                            if (acc[ni] >= threshold && W[ni] > W[i]) head = false;
                        }
                        if (!head) continue;
                        var p = CellToWorld(x, y);
                        p.y = ground[i] + 0.4f;
                        bool tooClose = false;
                        for (int s = 0; s < springs.Count && !tooClose; s++)
                        {
                            float ddx = springs[s].x - p.x, ddz = springs[s].z - p.z;
                            if (ddx * ddx + ddz * ddz < spacing2) tooClose = true;
                        }
                        if (!tooClose) springs.Add(p);
                    }
                }

                s_ground = ground;
                s_bedY = bedY;
                s_stillY = stillY;
                lock (s_springs) { s_springs.Clear(); s_springs.AddRange(springs); }
                s_ready = true;
                Debug.Log($"[FiresVoxel] hydrology READY: {RES}x{RES} @ {s_cell:F0}m | sample={sampleMs}ms fill={fillMs - sampleMs}ms flow={flowMs - fillMs}ms total={sw.ElapsedMilliseconds}ms | springs={springs.Count}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[FiresVoxel] hydrology build FAILED: {ex}");
            }
            finally { s_building = false; }
        }

        private static Vector3 CellToWorld(int x, int y)
            => new Vector3(-s_worldRadius + (x + 0.5f) * s_cell, 0f, -s_worldRadius + (y + 0.5f) * s_cell);

        // ── Samplers (worker-safe once Ready; pure array reads) ─────────────
        // Bilinear carved-bed Y at a world position; MaxValue = no river here.
        //
        // W2.1 EROSION DETAIL — the hydrology grid is 20m; raw bilinear gives
        // soft, straight-ish banks. Three deterministic noise terms restore
        // voxel-scale character:
        //   * DOMAIN WARP (±4m) — the sampled position wobbles, so the whole
        //     bank line meanders locally instead of following grid lines;
        //   * pools & riffles (~22m wavelength) — bed depth swells and
        //     shallows along the run;
        //   * bank wiggle (~6m) — fine edge raggedness.
        public static float SampleBedY(float wx, float wz)
        {
            if (!s_ready) return float.MaxValue;

            float wob1 = VoxelCaveNoise.Noise01(wx * 0.11f, 7.3f, wz * 0.11f) - 0.5f;
            float wob2 = VoxelCaveNoise.Noise01(wx * 0.11f, 41.9f, wz * 0.11f) - 0.5f;
            wx += wob1 * 8f;
            wz += wob2 * 8f;

            float fx = (wx + s_worldRadius) / s_cell - 0.5f;
            float fz = (wz + s_worldRadius) / s_cell - 0.5f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            if (x0 < 0 || z0 < 0 || x0 >= RES - 1 || z0 >= RES - 1) return float.MaxValue;
            float tx = fx - x0, tz = fz - z0;

            // Bilinear over bed DELTAS (bed relative to baked ground) so a
            // river cell next to a non-river cell feathers to zero carve
            // instead of averaging against MaxValue.
            float D(int xx, int zz)
            {
                int i = zz * RES + xx;
                return s_bedY[i] == float.MaxValue ? 0f : s_ground[i] - s_bedY[i];
            }
            float carve = Mathf.Lerp(
                Mathf.Lerp(D(x0, z0), D(x0 + 1, z0), tx),
                Mathf.Lerp(D(x0, z0 + 1), D(x0 + 1, z0 + 1), tx), tz);
            if (carve < 0.05f) return float.MaxValue;

            // Pools & riffles + bank-edge raggedness.
            carve *= 0.7f + 0.65f * VoxelCaveNoise.Noise01(wx * 0.045f, 133.7f, wz * 0.045f);
            carve *= 0.78f + 0.5f * VoxelCaveNoise.Noise01(wx * 0.16f, 211.1f, wz * 0.16f);
            if (carve < 0.08f) return float.MaxValue;

            float g = Mathf.Lerp(
                Mathf.Lerp(s_ground[z0 * RES + x0], s_ground[z0 * RES + x0 + 1], tx),
                Mathf.Lerp(s_ground[(z0 + 1) * RES + x0], s_ground[(z0 + 1) * RES + x0 + 1], tx), tz);
            return g - carve;
        }

        // Pond/lake still-water surface at a world position (MinValue = none).
        public static float SampleStillWaterY(float wx, float wz)
        {
            if (!s_ready) return float.MinValue;
            int x = Mathf.RoundToInt((wx + s_worldRadius) / s_cell - 0.5f);
            int z = Mathf.RoundToInt((wz + s_worldRadius) / s_cell - 0.5f);
            if (x < 0 || z < 0 || x >= RES || z >= RES) return float.MinValue;
            return s_stillY[z * RES + x];
        }

        // Springs inside a zone (world-space AABB).
        public static void GetSpringsInZone(Vector2i zone, List<Vector3> into)
        {
            float minX = zone.x * 64f - 32f, minZ = zone.y * 64f - 32f;
            lock (s_springs)
            {
                foreach (var p in s_springs)
                    if (p.x >= minX && p.x < minX + 64f && p.z >= minZ && p.z < minZ + 64f)
                        into.Add(p);
            }
        }

        // Minimal binary heap (net48 has no PriorityQueue).
        private sealed class PriorityQueue
        {
            private int[] _items = new int[1024];
            private float[] _prio = new float[1024];
            private int _count;

            public void Enqueue(int item, float priority)
            {
                if (_count == _items.Length)
                {
                    System.Array.Resize(ref _items, _count * 2);
                    System.Array.Resize(ref _prio, _count * 2);
                }
                int i = _count++;
                _items[i] = item; _prio[i] = priority;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (_prio[parent] <= _prio[i]) break;
                    (_items[parent], _items[i]) = (_items[i], _items[parent]);
                    (_prio[parent], _prio[i]) = (_prio[i], _prio[parent]);
                    i = parent;
                }
            }

            public bool TryDequeue(out int item, out float priority)
            {
                if (_count == 0) { item = 0; priority = 0; return false; }
                item = _items[0]; priority = _prio[0];
                _count--;
                _items[0] = _items[_count]; _prio[0] = _prio[_count];
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < _count && _prio[l] < _prio[m]) m = l;
                    if (r < _count && _prio[r] < _prio[m]) m = r;
                    if (m == i) break;
                    (_items[m], _items[i]) = (_items[i], _items[m]);
                    (_prio[m], _prio[i]) = (_prio[i], _prio[m]);
                    i = m;
                }
                return true;
            }
        }
    }
}
