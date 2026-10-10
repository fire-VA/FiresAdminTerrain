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
        internal const int GridRes = RES;
        // 0.2.721: scaled with the world's edge (FatWorldSize); exactly 10240 on a vanilla-size world (20 m cells). An Expand
        // World Size world keeps the 1024 cells over its own span.
        internal static float GridRadius => 10240f * (FatWorldSize.EdgeRadius / FatWorldSize.VanillaEdge);

        private static float[] s_ground;          // baked WG ground height
        private static float[] s_bedY;            // carved bed Y (MaxValue = no river)
        private static float[] s_stillY;          // pond/lake surface Y (MinValue = none)
        private static readonly List<Vector3> s_springs = new List<Vector3>();
        private static float s_worldRadius = GridRadius;
        private static float s_cell;               // metres per cell
        private static volatile bool s_ready;
        private static volatile bool s_building;
        private static float[] s_fillY;            // ε-sloped flood surface — monotonic downstream by construction
        private static int[] s_down;               // D8 downstream cell index, -1 = none
        private static volatile int s_gen;         // world generation; a worker from an old world never publishes

        public static bool Ready => s_ready;
        public static int SpringCount => s_springs.Count;

        // The solve runs on a worker, and a worker's Debug.Log NEVER reaches
        // BepInEx's LogOutput.log — it lands only in Unity's Player.log. Every
        // hydrology line was therefore invisible in the log the whole stack is
        // read from, which read exactly like "the solve never ran". So worker
        // lines queue here and the streamer flushes them from the main thread.
        private static readonly Queue<string> s_report = new Queue<string>();

        public static void Report(string line)
        {
            lock (s_report) s_report.Enqueue(line);
        }

        public static void FlushReport()
        {
            while (true)
            {
                string line;
                lock (s_report)
                {
                    if (s_report.Count == 0) return;
                    line = s_report.Dequeue();
                }
                FiresLogger.LogInfo(line);
            }
        }

        // World changed: drop the solve and the channels derived from it. A build
        // already running finishes into the void (generation check on publish).
        public static void Reset()
        {
            s_gen++;
            s_ready = false;
            s_ground = null; s_bedY = null; s_stillY = null; s_fillY = null; s_down = null;
            lock (s_springs) s_springs.Clear();
            VoxelRiverNetwork.Clear();
            VoxelLakeBasins.Clear();
            VoxelVanillaRivers.Clear();
            GenWaterField.Clear();
            HydrologyGuards.Reset();
        }

        private static readonly List<HydrologyGuards.Disc> s_guards = new List<HydrologyGuards.Disc>();

        // Waits for the start location (HydrologyGuards), so the solve can keep its still water off it.
        public static void EnsureBuilding()
        {
            if (s_ready || s_building) return;
            if (WorldGenerator.instance == null) return;
            if (!HydrologyGuards.TryCollect(s_guards)) return;
            s_building = true;
            var wg = WorldGenerator.instance;
            int gen = s_gen;
            var guards = new List<HydrologyGuards.Disc>(s_guards);
            s_worldRadius = GridRadius;   // 0.2.721: this world's span, read here on the main thread before the worker starts
            var t = new Thread(() => Build(wg, gen, guards)) { IsBackground = true, Name = "FiresVoxelHydrology" };
            t.Start();
        }

        private static void Build(WorldGenerator wg, int gen, List<HydrologyGuards.Disc> guards)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                s_cell = s_worldRadius * 2f / RES;
                var ground = new float[RES * RES];

                // 1) Sample the generator (WG height math is worker-safe — vanilla
                //    calls it from HeightmapBuilder threads). A world-wide drainage
                //    plan, so on a one-zone-at-a-time world it reads the REAL world:
                //    opening zones later must never move an existing river bed.
                Zones.ZoneModeGate.EnterPlanning();
                try
                {
                    for (int z = 0; z < RES; z++)
                    {
                        float wz = -s_worldRadius + (z + 0.5f) * s_cell;
                        for (int x = 0; x < RES; x++)
                        {
                            float wx = -s_worldRadius + (x + 0.5f) * s_cell;
                            ground[z * RES + x] = wg.GetHeight(wx, wz);
                        }
                    }
                }
                finally { Zones.ZoneModeGate.ExitPlanning(); }
                long sampleMs = sw.ElapsedMilliseconds;

                // 2) ε-sloped priority-flood fill.
                //
                // THE OCEAN IS THE OUTLET, NOT A SURFACE. Seeding the grid
                // border at max(ground, SEA) made the whole sea one flat 30m
                // plateau, and the ε slope then accumulated ~0.4m across it —
                // so every shoreline cell under 30.4m came out "flooded" and
                // the solve reported thousands of waterline puddles as lakes.
                // Seed every ocean cell at its OWN (below-sea) ground instead:
                // coastal land then drains to the sea and ε only accumulates
                // inland, where a fill really does mean a closed depression.
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
                // Vanilla's OWN rivers are outlets too. The generator already
                // carved them to the sea plane's level, so they drain like the
                // ocean does — and a reach that ends in one can then be graded
                // to y=30 and hand its flow over instead of stopping in a wall
                // of water above vanilla's channel.
                VoxelVanillaRivers.Build(wg);
                for (int i = 0; i < W.Length; i++)
                {
                    if (ground[i] <= SEA) { Push(i, ground[i]); continue; }
                    if (!VoxelVanillaRivers.Ready) continue;
                    float vwx = -s_worldRadius + (i % RES + 0.5f) * s_cell;
                    float vwz = -s_worldRadius + (i / RES + 0.5f) * s_cell;
                    if (VoxelVanillaRivers.Inside(vwx, vwz, out _)) Push(i, Mathf.Min(ground[i], SEA));
                }
                for (int x = 0; x < RES; x++)
                {
                    Push(x, ground[x]);
                    Push((RES - 1) * RES + x, ground[(RES - 1) * RES + x]);
                }
                for (int y = 0; y < RES; y++)
                {
                    Push(y * RES, ground[y * RES]);
                    Push(y * RES + RES - 1, ground[y * RES + RES - 1]);
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
                        Push(ni, Mathf.Max(ground[ni], lvl + 0.0004f));
                    }
                }
                long fillMs = sw.ElapsedMilliseconds;

                // 3) D8 flow accumulation (high → low over the filled surface).
                var order = new int[RES * RES];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                System.Array.Sort(order, (a, b) => W[b].CompareTo(W[a]));
                var acc = new float[RES * RES];
                var down = new int[RES * RES];
                for (int i = 0; i < acc.Length; i++) { acc[i] = 1f; down[i] = -1; }
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
                    down[idx] = best;
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
                    if (ground[i] <= SEA || FiresWaterGuards.IsGuarded(i)) continue;

                    // A closed depression worth calling water: a real fill, and
                    // clear of the sea plane so a coastal notch is never one.
                    float depth = W[i] - ground[i];
                    if (depth > 0.6f && W[i] >= SEA + 1f) stillY[i] = W[i];

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
                        if (ground[i] <= SEA + 3f || acc[i] < threshold || acc[i] > threshold * 3f || FiresWaterGuards.IsGuarded(i)) continue;
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

                // The start location and the start cave keep dry: their still cells go before the bowls and channels
                // are read from the grid, and a bowl that would still reach them is pulled back or dropped.
                int drained = HydrologyGuards.ClearStill(stillY, RES, s_cell, s_worldRadius, guards);

                if (gen != s_gen) return;   // world changed under us

                s_ground = ground;
                s_bedY = bedY;
                s_stillY = stillY;
                s_fillY = W;
                s_down = down;
                lock (s_springs) { s_springs.Clear(); s_springs.AddRange(springs); }

                // Channel GEOMETRY from the same arrays: a 20m grid cannot hold
                // a banked 8m channel, so the network becomes thalweg polylines
                // the carve works against (Tools/FAT_RIVER_CARVE_SPEC.md).
                VoxelLakeBasins.Build(ground, stillY, RES, s_cell, s_worldRadius, maxCarve, guards);
                VoxelRiverNetwork.Build(ground, W, acc, down, stillY, RES, s_cell, s_worldRadius, threshold, maxCarve);

                s_ready = true;
                Report($"[FiresVoxel] hydrology READY: {RES}x{RES} @ {s_cell:F0}m | sample={sampleMs}ms fill={fillMs - sampleMs}ms flow={flowMs - fillMs}ms total={sw.ElapsedMilliseconds}ms | springs={springs.Count} | channels: {VoxelRiverNetwork.ReachCount} reach(es), {VoxelRiverNetwork.NodeCount} node(s), {VoxelRiverNetwork.ZoneCount} zone(s)");
                Report($"[FiresVoxel] hydrology guards: {HydrologyGuards.Describe(guards)}; builds and locations: {FiresWaterGuards.Describe()}; "
                    + $"{drained} still cell(s) drained, {VoxelLakeBasins.GuardedBowls} bowl(s) pulled back, {VoxelLakeBasins.SkippedBowls} skipped, "
                    + $"{VoxelRiverNetwork.ClosedEnds} channel end(s) closed before a guard, {VoxelRiverNetwork.OpenedHeads} opened after one.");

                // WHERE did it put them? Cell census against the gates that
                // decide whether water can render at all, so "placed but
                // pointless" is answerable from one log line.
                int landCells = 0, stillCells = 0, chanCells = 0, chanRenderable = 0;
                float gLo = float.MaxValue, gHi = float.MinValue, sLo = float.MaxValue, sHi = float.MinValue;
                for (int i = 0; i < RES * RES; i++)
                {
                    if (ground[i] > SEA) { landCells++; if (ground[i] < gLo) gLo = ground[i]; if (ground[i] > gHi) gHi = ground[i]; }
                    if (stillY[i] > float.MinValue + 1f)
                    {
                        stillCells++;
                        if (stillY[i] < sLo) sLo = stillY[i];
                        if (stillY[i] > sHi) sHi = stillY[i];
                    }
                    if (bedY[i] < float.MaxValue)
                    {
                        chanCells++;
                        if (ground[i] > 30.3f) chanRenderable++;
                    }
                }
                Report($"[FiresVoxel] hydrology census: land cells={landCells}/{RES * RES} ground {gLo:F1}..{gHi:F1}m | depression cells={stillCells} fill {sLo:F1}..{sHi:F1}m | channel cells={chanCells} (above sea {chanRenderable})");
                Report($"[FiresVoxel] vanilla drainage read from the generator: {VoxelVanillaRivers.SegmentCount} river/stream segment(s) treated as outlets at y{VoxelVanillaRivers.SeaLevel:F0}.");
                Report($"[FiresVoxel] pond bowls: {VoxelLakeBasins.BasinCount} from {VoxelLakeBasins.BasinCells} cell(s) | level {VoxelLakeBasins.LevelLo:F1}..{VoxelLakeBasins.LevelHi:F1}m | radius {VoxelLakeBasins.RadiusLo:F0}..{VoxelLakeBasins.RadiusHi:F0}m");
                VoxelRiverNetwork.LogReachSummary(3);
            }
            catch (System.Exception ex)
            {
                Report($"[FiresVoxel] hydrology build FAILED: {ex}");
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

        // Baked ground (the solve's own height sample) at a world position.
        public static bool TryGroundAt(float wx, float wz, out float g)
        {
            g = 0f;
            if (!s_ready || s_ground == null) return false;
            float fx = (wx + s_worldRadius) / s_cell - 0.5f;
            float fz = (wz + s_worldRadius) / s_cell - 0.5f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            if (x0 < 0 || z0 < 0 || x0 >= RES - 1 || z0 >= RES - 1) return false;
            float tx = fx - x0, tz = fz - z0;
            float a = Mathf.Lerp(s_ground[z0 * RES + x0], s_ground[z0 * RES + x0 + 1], tx);
            float b = Mathf.Lerp(s_ground[(z0 + 1) * RES + x0], s_ground[(z0 + 1) * RES + x0 + 1], tx);
            g = Mathf.Lerp(a, b, tz);
            return true;
        }

        // Smooth pond/lake surface: bilinear over the cells that actually hold
        // still water, weights renormalised so a pond edge feathers instead of
        // stepping on the 20m grid (nearest-neighbour put 20m squares into both
        // the water and the shore paint).
        public static bool TryStillAt(float wx, float wz, out float level)
        {
            level = float.MinValue;
            if (!s_ready || s_stillY == null) return false;
            float fx = (wx + s_worldRadius) / s_cell - 0.5f;
            float fz = (wz + s_worldRadius) / s_cell - 0.5f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            if (x0 < 0 || z0 < 0 || x0 >= RES - 1 || z0 >= RES - 1) return false;
            float tx = fx - x0, tz = fz - z0;

            float sum = 0f, wsum = 0f;
            for (int dz = 0; dz <= 1; dz++)
                for (int dx = 0; dx <= 1; dx++)
                {
                    float v = s_stillY[(z0 + dz) * RES + x0 + dx];
                    if (v <= float.MinValue + 1f) continue;
                    float w = (dx == 0 ? 1f - tx : tx) * (dz == 0 ? 1f - tz : tz);
                    if (w <= 0f) continue;
                    sum += v * w;
                    wsum += w;
                }
            if (wsum <= 0f) return false;
            level = sum / wsum;
            return true;
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
        public static void GetSpringsInZone(Vector2s zone, List<Vector3> into)
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
