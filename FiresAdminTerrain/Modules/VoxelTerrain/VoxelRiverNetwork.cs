using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Channel GEOMETRY for the hydrology network (spec: Tools/FAT_RIVER_CARVE_SPEC.md).
    //
    // VoxelHydrology solves WHERE water flows, on a 20m grid. A 20m grid cannot
    // express an 8m channel with banks: sampling its bed per column gave broad
    // soft swales that read as terrain, which is why the 09-17 test produced
    // "no actual rivers". So the network is re-expressed as thalweg polylines
    // carrying width, bed and surface per node, and chunk generation carves a
    // cross-section against the nearest segment. Channel shape is then
    // independent of the solve's resolution.
    //
    // Deterministic: built from the solve's arrays plus seeded noise, so every
    // peer derives identical channels and nothing is persisted.
    internal static class VoxelRiverNetwork
    {
        private const float NodeSpacing = 4f;      // metres between thalweg nodes
        private const float BucketSize = 64f;      // spatial index cell = one zone
        private const float SEA = 30f;
        private const float MinHoldDepth = 0.4f;   // below this a reach is truncated
        private const int MinReachCells = 3;
        // ONE sheet per column. The vanilla ocean mesh sits at SEA across every
        // zone, hidden inland only because terrain occludes it — so a channel
        // floor cut below SEA makes the ocean plane show THROUGH our river
        // sheet (two stacked waters). Wherever the channel carries its own
        // surface (above SeaSheetGate, the same gate the water store uses), the
        // floor stays above SeaFloorMin and the ocean stays occluded. At a
        // mouth the surface grades to SEA, the store drops out, the estuary
        // pass lowers the bed again, and the ocean IS the water — so the higher
        // surface always owns the column.
        private const float SeaSheetGate = 30.3f;
        private const float SeaFloorMin = 30.1f;
        // Steepest fall the water SURFACE may take, per metre of run. The
        // surface tracks the flood fill less a freeboard, so on a coastal drop
        // it would otherwise plunge with the ground while the bed is pinned at
        // the sea floor — the sheet detaching into a wall against the ocean.
        private const float MaxSurfaceGrade = 0.3f;

        private sealed class Reach
        {
            public Vector2[] P;          // thalweg nodes (world XZ)
            public float[] SurfaceY;     // water surface per node (monotonic downstream)
            public float[] BedY;         // channel floor per node
            public float[] HalfWidth;    // half channel width per node
        }

        // Where a channel meets guarded ground its carve closes over this run: the bed climbs back above the untouched
        // ground, so the trench fades out and the water, a freeboard below the banks, runs out onto the rising floor.
        private const float GuardTaperLength = 24f;
        private const float GuardCloseLift = 1f;

        private static int s_closedEnds;
        private static int s_openedHeads;

        public static int ClosedEnds => s_closedEnds;
        public static int OpenedHeads => s_openedHeads;

        private static Reach[] s_reaches = new Reach[0];
        private static Dictionary<int, int[]> s_index = new Dictionary<int, int[]>();
        private static HashSet<Vector2s> s_zones = new HashSet<Vector2s>();
        private static volatile bool s_ready;

        public static bool Ready => s_ready;
        public static int ReachCount => s_reaches.Length;
        public static int NodeCount { get { int n = 0; foreach (var r in s_reaches) n += r.P.Length; return n; } }
        public static int ZoneCount => s_zones.Count;

        // A zone the network runs through (Stage 5 conversion scope).
        public static bool TouchesZone(Vector2s zone) => s_ready && s_zones.Contains(zone);

        // What the reaches actually are, in world coordinates: head, mouth,
        // surface range, width range. Answers "is this on land at all" without
        // another in-game round trip.
        public static void LogReachSummary(int count)
        {
            if (!s_ready) { VoxelHydrology.Report("[FiresVoxel] channels: none built."); return; }
            int shown = 0;
            for (int r = 0; r < s_reaches.Length && shown < count; r++, shown++)
            {
                var re = s_reaches[r];
                float sLo = float.MaxValue, sHi = float.MinValue, wLo = float.MaxValue, wHi = float.MinValue;
                for (int k = 0; k < re.P.Length; k++)
                {
                    if (re.SurfaceY[k] < sLo) sLo = re.SurfaceY[k];
                    if (re.SurfaceY[k] > sHi) sHi = re.SurfaceY[k];
                    if (re.HalfWidth[k] * 2f < wLo) wLo = re.HalfWidth[k] * 2f;
                    if (re.HalfWidth[k] * 2f > wHi) wHi = re.HalfWidth[k] * 2f;
                }
                var h = re.P[0];
                var m = re.P[re.P.Length - 1];
                VoxelHydrology.Report($"[FiresVoxel] reach {r}: {re.P.Length} node(s) head=({h.x:F0},{h.y:F0}) mouth=({m.x:F0},{m.y:F0}) surface {sLo:F1}..{sHi:F1}m width {wLo:F1}..{wHi:F1}m");
            }
        }

        // Minimap pins along the channels near a point, one every `spacing`
        // metres of run, so generated water can actually be found.
        public static int PinNear(Vector3 center, float radius, float spacing)
        {
            if (!s_ready || Minimap.instance == null) return 0;
            float r2 = radius * radius;
            int pins = 0;
            foreach (var reach in s_reaches)
            {
                float run = float.MaxValue;
                for (int k = 0; k < reach.P.Length; k++)
                {
                    if (k > 0) run += Vector2.Distance(reach.P[k - 1], reach.P[k]);
                    float dx = reach.P[k].x - center.x, dz = reach.P[k].y - center.z;
                    if (dx * dx + dz * dz > r2) continue;
                    if (run < spacing) continue;
                    run = 0f;
                    Minimap.instance.AddPin(new Vector3(reach.P[k].x, reach.SurfaceY[k], reach.P[k].y),
                        Minimap.PinType.Icon3, reach.HalfWidth[k] >= 4f ? "River" : "Stream", false, false);
                    pins++;
                }
            }
            return pins;
        }

        public static void Clear()
        {
            s_ready = false;
            s_reaches = new Reach[0];
            s_index = new Dictionary<int, int[]>();
            s_zones = new HashSet<Vector2s>();
        }

        // Worker thread, straight after the solve. ground/fill/acc/still are the
        // solve's arrays; down[i] = D8 downstream cell index, or -1.
        public static void Build(float[] ground, float[] fill, float[] acc, int[] down, float[] still,
            int res, float cell, float worldRadius, float threshold, float maxDepth)
        {
            var reaches = new List<Reach>();
            var claimed = new bool[res * res];
            int[] dx8 = { -1, 1, 0, 0, -1, -1, 1, 1 };
            int[] dz8 = { 0, 0, -1, 1, -1, 1, -1, 1 };

            Vector2 CellToWorld(int i)
                => new Vector2(-worldRadius + (i % res + 0.5f) * cell, -worldRadius + (i / res + 0.5f) * cell);

            s_closedEnds = s_openedHeads = 0;
            for (int i = 0; i < acc.Length; i++)
            {
                if (acc[i] < threshold || ground[i] <= SEA || claimed[i] || FiresWaterGuards.IsGuarded(i)) continue;

                // Headwater: no upstream neighbour that is itself a channel. A channel cell on guarded ground (a build, a
                // placed location) is none, so the river starts again past it.
                int cx = i % res, cz = i / res;
                bool head = true;
                bool afterGuard = false;
                for (int k = 0; k < 8 && head; k++)
                {
                    int nx = cx + dx8[k], nz = cz + dz8[k];
                    if (nx < 0 || nz < 0 || nx >= res || nz >= res) continue;
                    int ni = nz * res + nx;
                    if (acc[ni] < threshold || down[ni] != i) continue;
                    if (FiresWaterGuards.IsGuarded(ni)) afterGuard = true;
                    else head = false;
                }
                if (!head) continue;

                // Walk downstream to the ocean, a lake, a confluence, guarded ground or the edge.
                var cells = new List<int>(64);
                int cur = i;
                bool reachesOcean = false;
                bool beforeGuard = false;
                while (cur >= 0 && !claimed[cur])
                {
                    if (FiresWaterGuards.IsGuarded(cur)) { beforeGuard = true; break; }
                    claimed[cur] = true;
                    cells.Add(cur);
                    if (ground[cur] <= SEA) { reachesOcean = true; break; }
                    // A VANILLA river is the same kind of outlet: its bed was
                    // carved to the sea plane, so our channel ends here and
                    // grades to y=30 across the junction.
                    {
                        var cw = CellToWorld(cur);
                        if (VoxelVanillaRivers.Inside(cw.x, cw.y, out _)) { reachesOcean = true; break; }
                    }
                    if (still[cur] > float.MinValue + 1f) break;   // runs into a lake
                    cur = down[cur];
                }
                if (cells.Count < MinReachCells) continue;

                BuildReach(cells, ground, fill, acc, still, res, cell, worldRadius,
                    threshold, maxDepth, reachesOcean, afterGuard, beforeGuard, CellToWorld, reaches);
            }

            var index = new Dictionary<int, List<int>>();
            var zones = new HashSet<Vector2s>();
            for (int r = 0; r < reaches.Count; r++) IndexReach(reaches[r], r, index, zones);

            var flat = new Dictionary<int, int[]>(index.Count);
            foreach (var kv in index) flat[kv.Key] = kv.Value.ToArray();

            s_reaches = reaches.ToArray();
            s_index = flat;
            s_zones = zones;
            s_ready = s_reaches.Length > 0;
        }

        private static void BuildReach(List<int> cells, float[] ground, float[] fill, float[] acc, float[] still,
            int res, float cell, float worldRadius, float threshold, float maxDepth, bool reachesOcean,
            bool opensAfterGuard, bool closesBeforeGuard, System.Func<int, Vector2> cellToWorld, List<Reach> into)
        {
            // Coarse path → nodes every NodeSpacing along a Catmull-Rom through
            // the cell centres, so the channel curves instead of stepping.
            var coarse = new List<Vector2>(cells.Count);
            for (int c = 0; c < cells.Count; c++) coarse.Add(cellToWorld(cells[c]));
            var pts = Resample(coarse, NodeSpacing);
            if (pts.Count < 2) return;

            float meander = FiresVoxelConfig.RiverMeander != null ? FiresVoxelConfig.RiverMeander.Value : 6f;
            // The surface comes off a 20m solve but has to sit inside 1m ground,
            // so the freeboard carries the grid's inaccuracy as extra bank.
            float freeboard = (FiresVoxelConfig.RiverFreeboard != null ? FiresVoxelConfig.RiverFreeboard.Value : 0.5f)
                              + FiresVoxelConfig.CoarseBankRelief;
            float wMin = FiresVoxelConfig.RiverWidthMin != null ? FiresVoxelConfig.RiverWidthMin.Value : 5f;
            float wMax = FiresVoxelConfig.RiverWidthMax != null ? FiresVoxelConfig.RiverWidthMax.Value : 18f;
            float maxCut = FiresVoxelConfig.RiverMaxNewCut != null ? FiresVoxelConfig.RiverMaxNewCut.Value : maxDepth;
            float estLen = FiresVoxelConfig.EstuaryLength != null ? FiresVoxelConfig.EstuaryLength.Value : 80f;
            float estFlare = FiresVoxelConfig.EstuaryFlare != null ? FiresVoxelConfig.EstuaryFlare.Value : 2.5f;

            int n = pts.Count;
            var P = new Vector2[n];
            var surf = new float[n];
            var bed = new float[n];
            var half = new float[n];

            // Meander: perpendicular offset, tapered to zero at both ends so
            // confluences and mouths stay where the solve put them.
            for (int k = 0; k < n; k++)
            {
                Vector2 p = pts[k];
                Vector2 dir = pts[Mathf.Min(k + 1, n - 1)] - pts[Mathf.Max(k - 1, 0)];
                if (dir.sqrMagnitude < 1e-6f) dir = new Vector2(1f, 0f);
                dir.Normalize();
                Vector2 perp = new Vector2(-dir.y, dir.x);
                float taper = Mathf.Min(1f, Mathf.Min(k, n - 1 - k) / 6f);
                float off = (VoxelCaveNoise.Noise01(p.x * 0.012f, 17.3f, p.y * 0.012f) - 0.5f) * 2f * meander * taper;
                P[k] = p + perp * off;
            }

            // Per-node hydraulics from the solve, sampled at the meandered point.
            var gnd = new float[n];
            for (int k = 0; k < n; k++)
            {
                float a = SampleGrid(acc, P[k], res, cell, worldRadius, threshold);
                float g = SampleGrid(ground, P[k], res, cell, worldRadius, SEA);
                gnd[k] = g;
                float w = SampleGrid(fill, P[k], res, cell, worldRadius, g);
                float ratio = Mathf.Max(1f, a / Mathf.Max(1f, threshold));
                float t = Mathf.Log10(ratio);

                half[k] = Mathf.Clamp(wMin * 0.5f + t * 3.2f, wMin * 0.5f, wMax * 0.5f);
                float depth = Mathf.Clamp(0.8f + t * 2.2f, 0.8f, maxDepth);
                surf[k] = w - freeboard;
                bed[k] = surf[k] - depth;

                // New routes are allowed, canyons are not: never cut deeper
                // than MaxNewCut below the original ground.
                float floorLimit = g - maxCut;
                if (bed[k] < floorLimit) bed[k] = floorLimit;
                if (surf[k] > SeaSheetGate && bed[k] < SeaFloorMin) bed[k] = SeaFloorMin;
            }

            // Monotonic surface downstream — the ε-flood fill guarantees it on
            // the grid; re-enforce after meandering and sampling.
            for (int k = 1; k < n; k++) if (surf[k] > surf[k - 1]) surf[k] = surf[k - 1];

            // BOUNDED DESCENT. The surface is the flood fill less a freeboard,
            // so it follows the ORIGINAL ground — and where a coast drops 15m
            // between two 4m nodes it plunges with it, while the bed is pinned
            // at the sea floor. The sheet then detaches from the channel: a
            // near-vertical wall of water against the ocean plane instead of a
            // river grading into it. Cap the fall per metre, but never lift the
            // surface above its own node's ground (that floats water over dry
            // land); a genuine cliff therefore fails the hold test below and
            // the reach simply ends above it.
            for (int k = 1; k < n; k++)
            {
                float span = Mathf.Max(0.5f, Vector2.Distance(P[k - 1], P[k]));
                float gentle = surf[k - 1] - MaxSurfaceGrade * span;
                if (surf[k] < gentle) surf[k] = Mathf.Min(gentle, gnd[k] - freeboard);
                if (bed[k] > surf[k] - MinHoldDepth) bed[k] = surf[k] - MinHoldDepth;
                float floorLimit2 = gnd[k] - maxCut;
                if (bed[k] < floorLimit2) bed[k] = floorLimit2;
            }

            // Estuary: flare width and grade the last stretch to the sea plane.
            if (reachesOcean)
            {
                float run = 0f;
                var dist = new float[n];
                for (int k = n - 2; k >= 0; k--) { run += Vector2.Distance(P[k], P[k + 1]); dist[k] = run; }
                for (int k = 0; k < n; k++)
                {
                    if (dist[k] > estLen) continue;
                    float u = 1f - dist[k] / Mathf.Max(1f, estLen);   // 0 inland → 1 at the mouth
                    half[k] *= Mathf.Lerp(1f, estFlare, u);
                    surf[k] = Mathf.Lerp(surf[k], SEA, u);
                    bed[k] = Mathf.Min(bed[k], surf[k] - 1f);
                }
                surf[n - 1] = SEA;
            }

            // Truncate where the channel can no longer hold water (a capped cut
            // on a saddle): the reach ends there as a sink instead of a trench.
            int keep = n;
            for (int k = 0; k < n; k++)
            {
                if (surf[k] - bed[k] >= MinHoldDepth) continue;
                keep = k;
                break;
            }
            if (keep < 2) return;
            if (keep < n)
            {
                System.Array.Resize(ref P, keep);
                System.Array.Resize(ref surf, keep);
                System.Array.Resize(ref bed, keep);
                System.Array.Resize(ref half, keep);
            }

            Smooth(half);
            Smooth(bed);
            for (int k = 1; k < surf.Length; k++) if (surf[k] > surf[k - 1]) surf[k] = surf[k - 1];
            for (int k = 0; k < bed.Length; k++)
                if (surf[k] > SeaSheetGate && bed[k] < SeaFloorMin) bed[k] = SeaFloorMin;

            AddClearRuns(P, surf, bed, half, gnd, opensAfterGuard, closesBeforeGuard && keep == n, into);
        }

        // A reach whose carve would reach guarded ground (FiresWaterGuards) is split there, and every piece closes into
        // the ground at the ends that meet it.
        private static void AddClearRuns(Vector2[] P, float[] surf, float[] bed, float[] half, float[] gnd,
            bool opensAfterGuard, bool closesBeforeGuard, List<Reach> into)
        {
            int n = P.Length;
            int start = -1;
            for (int k = 0; k <= n; k++)
            {
                bool clear = k < n && FiresWaterGuards.RoomAround(P[k].x, P[k].y, half[k]) >= half[k];
                if (clear)
                {
                    if (start < 0) start = k;
                    continue;
                }
                if (start < 0) continue;
                AddRun(P, surf, bed, half, gnd, start, k, start > 0 || opensAfterGuard, k < n || closesBeforeGuard, into);
                start = -1;
            }
        }

        private static void AddRun(Vector2[] P, float[] surf, float[] bed, float[] half, float[] gnd,
            int from, int to, bool opens, bool closes, List<Reach> into)
        {
            int count = to - from;
            if (count < 2) return;
            float length = 0f;
            for (int k = from + 1; k < to; k++) length += Vector2.Distance(P[k - 1], P[k]);
            int tapers = (opens ? 1 : 0) + (closes ? 1 : 0);
            if (length < GuardTaperLength * tapers) return;

            bool whole = from == 0 && to == P.Length;
            var reach = new Reach
            {
                P = whole ? P : Slice(P, from, count),
                SurfaceY = whole ? surf : Slice(surf, from, count),
                BedY = whole ? bed : Slice(bed, from, count),
                HalfWidth = whole ? half : Slice(half, from, count),
            };
            var runGround = Slice(gnd, from, count);
            if (opens) CloseIntoGround(reach.P, reach.BedY, runGround, fromEnd: false);
            if (closes) CloseIntoGround(reach.P, reach.BedY, runGround, fromEnd: true);
            into.Add(reach);
            if (opens) s_openedHeads++;
            if (closes) s_closedEnds++;
        }

        private static T[] Slice<T>(T[] source, int from, int count)
        {
            var slice = new T[count];
            System.Array.Copy(source, from, slice, 0, count);
            return slice;
        }

        // The surface stays as it is, monotonic downstream; only the bed climbs, so the water ends (or starts) where the
        // floor rises through it, and the carve is gone before the guarded ground.
        private static void CloseIntoGround(Vector2[] P, float[] bed, float[] gnd, bool fromEnd)
        {
            int n = P.Length;
            float run = 0f;
            for (int step = 0; step < n; step++)
            {
                int k = fromEnd ? n - 1 - step : step;
                if (step > 0) run += Vector2.Distance(P[k], P[fromEnd ? k + 1 : k - 1]);
                if (run >= GuardTaperLength) break;
                float u = 1f - run / GuardTaperLength;
                float closed = gnd[k] + GuardCloseLift;
                bed[k] = Mathf.Lerp(bed[k], Mathf.Max(bed[k], closed), u * u * (3f - 2f * u));
            }
        }

        private static void Smooth(float[] v)
        {
            if (v.Length < 3) return;
            var copy = (float[])v.Clone();
            for (int k = 1; k < v.Length - 1; k++) v[k] = (copy[k - 1] + copy[k] * 2f + copy[k + 1]) * 0.25f;
        }

        private static List<Vector2> Resample(List<Vector2> path, float spacing)
        {
            var outPts = new List<Vector2>(path.Count * 4);
            if (path.Count == 0) return outPts;
            if (path.Count < 3) { outPts.AddRange(path); return outPts; }

            outPts.Add(path[0]);
            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector2 p0 = path[Mathf.Max(i - 1, 0)];
                Vector2 p1 = path[i];
                Vector2 p2 = path[i + 1];
                Vector2 p3 = path[Mathf.Min(i + 2, path.Count - 1)];
                float seg = Vector2.Distance(p1, p2);
                int steps = Mathf.Max(1, Mathf.CeilToInt(seg / spacing));
                for (int s = 1; s <= steps; s++)
                {
                    Vector2 q = CatmullRom(p0, p1, p2, p3, s / (float)steps);
                    if (Vector2.Distance(q, outPts[outPts.Count - 1]) >= spacing * 0.75f) outPts.Add(q);
                }
            }
            if (outPts[outPts.Count - 1] != path[path.Count - 1]) outPts.Add(path[path.Count - 1]);
            return outPts;
        }

        private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private static float SampleGrid(float[] grid, Vector2 p, int res, float cell, float worldRadius, float fallback)
        {
            float fx = (p.x + worldRadius) / cell - 0.5f;
            float fz = (p.y + worldRadius) / cell - 0.5f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            if (x0 < 0 || z0 < 0 || x0 >= res - 1 || z0 >= res - 1) return fallback;
            float tx = fx - x0, tz = fz - z0;
            float a = Mathf.Lerp(grid[z0 * res + x0], grid[z0 * res + x0 + 1], tx);
            float b = Mathf.Lerp(grid[(z0 + 1) * res + x0], grid[(z0 + 1) * res + x0 + 1], tx);
            return Mathf.Lerp(a, b, tz);
        }

        private static int BucketKey(int bx, int bz) => (bx + 4096) * 16384 + (bz + 4096);

        private static void IndexReach(Reach r, int id, Dictionary<int, List<int>> index, HashSet<Vector2s> zones)
        {
            for (int k = 0; k < r.P.Length - 1; k++)
            {
                float pad = Mathf.Max(r.HalfWidth[k], r.HalfWidth[k + 1]) + 2f;
                float minX = Mathf.Min(r.P[k].x, r.P[k + 1].x) - pad;
                float maxX = Mathf.Max(r.P[k].x, r.P[k + 1].x) + pad;
                float minZ = Mathf.Min(r.P[k].y, r.P[k + 1].y) - pad;
                float maxZ = Mathf.Max(r.P[k].y, r.P[k + 1].y) + pad;
                int seg = (id << 16) | k;   // 65k nodes per reach is ample

                for (int bz = Mathf.FloorToInt(minZ / BucketSize); bz <= Mathf.FloorToInt(maxZ / BucketSize); bz++)
                    for (int bx = Mathf.FloorToInt(minX / BucketSize); bx <= Mathf.FloorToInt(maxX / BucketSize); bx++)
                    {
                        int key = BucketKey(bx, bz);
                        if (!index.TryGetValue(key, out var list)) index[key] = list = new List<int>(4);
                        list.Add(seg);
                    }

                for (int zz = Mathf.FloorToInt((minZ + 32f) / 64f); zz <= Mathf.FloorToInt((maxZ + 32f) / 64f); zz++)
                    for (int zx = Mathf.FloorToInt((minX + 32f) / 64f); zx <= Mathf.FloorToInt((maxX + 32f) / 64f); zx++)
                        zones.Add(new Vector2s(zx, zz));
            }
        }

        // Carve query (worker-safe: immutable arrays after Build). Returns the
        // channel floor at this column and the water surface over it.
        //
        // Cross-section: the floor rises as (d/halfWidth)^2 from the thalweg to
        // meet the caller's own ground exactly at the bank edge, so there is no
        // lip at the channel rim and no step where a streaming frontier lands
        // mid-channel. Overlapping segments (confluences) take the LOWEST floor
        // and the HIGHEST surface, so a junction stays connected and wet.
        // VANILLA OWNS ITS OWN RIVERS. Inside one of the generator's river
        // corridors the bed is already cut to the sea plane and the ocean mesh
        // is the water; carving again there fights it (our surface pinned above
        // the sheet gate over a bed clamped at the sea floor) and that is the
        // step between a river and the ocean plane. Our channel surface grades
        // to y=30 across the junction and then stands down.
        public static bool TryCarveAt(float wx, float wz, float groundY, out float carvedY, out float surfaceY)
        {
            carvedY = groundY;
            surfaceY = float.MinValue;
            // A lifted chunk (ZoneModeLift) carries its channels up with its ground: the bed and surface are its source's.
            Zones.ZoneModeGate.TranslateToSource(ref wx, ref wz, out float lift);
            if (VoxelVanillaRivers.HandoffAt(wx, wz) >= 1f) return false;
            bool carved = Query(wx, wz, groundY - lift, out carvedY, out surfaceY);
            carvedY += lift;
            if (surfaceY > float.MinValue) surfaceY += lift;
            return carved && carvedY < groundY;
        }

        // Water surface only — for water placement and swim queries.
        public static bool TrySurfaceAt(float wx, float wz, out float level)
        {
            Query(wx, wz, float.MaxValue, out _, out level);
            return level > float.MinValue + 1f;
        }

        // Nearest channel within `reach` of its WET EDGE: its surface, the
        // distance from the point to that edge (0 = inside the water), and the
        // local half width so the caller can tell a river from a stream.
        public static bool TryWetInfo(float wx, float wz, float reach,
            out float level, out float distToEdge, out float halfWidth, out float bedY)
        {
            level = float.MinValue;
            distToEdge = float.MaxValue;
            halfWidth = 0f;
            bedY = float.MaxValue;
            if (!s_ready) return false;
            // Deep inside a vanilla river the ocean mesh is the water; we do not
            // report a second surface over it.
            if (VoxelVanillaRivers.HandoffAt(wx, wz) >= 1f) return false;

            var p = new Vector2(wx, wz);
            int b0x = Mathf.FloorToInt((wx - reach) / BucketSize), b1x = Mathf.FloorToInt((wx + reach) / BucketSize);
            int b0z = Mathf.FloorToInt((wz - reach) / BucketSize), b1z = Mathf.FloorToInt((wz + reach) / BucketSize);

            for (int bz = b0z; bz <= b1z; bz++)
                for (int bx = b0x; bx <= b1x; bx++)
                {
                    if (!s_index.TryGetValue(BucketKey(bx, bz), out int[] segs)) continue;
                    for (int s = 0; s < segs.Length; s++)
                    {
                        var r = s_reaches[segs[s] >> 16];
                        int k = segs[s] & 0xFFFF;
                        Vector2 a = r.P[k], b = r.P[k + 1];
                        Vector2 ab = b - a;
                        float len2 = ab.sqrMagnitude;
                        float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                        float hw = Mathf.Lerp(r.HalfWidth[k], r.HalfWidth[k + 1], t);
                        float d = Mathf.Max(0f, Vector2.Distance(p, a + ab * t) - hw);
                        if (d > reach || d > distToEdge) continue;
                        float stationLevel = Mathf.Lerp(r.SurfaceY[k], r.SurfaceY[k + 1], t);
                        float stationBed = Mathf.Lerp(r.BedY[k], r.BedY[k + 1], t);
                        if (stationBed >= stationLevel) continue;
                        distToEdge = d;
                        level = stationLevel;
                        bedY = stationBed;
                        halfWidth = hw;
                    }
                }

            return level > float.MinValue + 1f;
        }

        private static bool Query(float wx, float wz, float groundY, out float carvedY, out float surfaceY)
        {
            carvedY = groundY;
            surfaceY = float.MinValue;
            if (!s_ready) return false;

            int key = BucketKey(Mathf.FloorToInt(wx / BucketSize), Mathf.FloorToInt(wz / BucketSize));
            if (!s_index.TryGetValue(key, out int[] segs)) return false;

            var p = new Vector2(wx, wz);
            bool hit = false;
            for (int s = 0; s < segs.Length; s++)
            {
                var r = s_reaches[segs[s] >> 16];
                int k = segs[s] & 0xFFFF;
                Vector2 a = r.P[k], b = r.P[k + 1];
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                float d = Vector2.Distance(p, a + ab * t);

                float hw = Mathf.Lerp(r.HalfWidth[k], r.HalfWidth[k + 1], t);
                if (d > hw) continue;

                float bed = Mathf.Lerp(r.BedY[k], r.BedY[k + 1], t);
                float surf = Mathf.Lerp(r.SurfaceY[k], r.SurfaceY[k + 1], t);
                float u = hw < 0.01f ? 1f : d / hw;
                float floor = groundY == float.MaxValue ? bed : Mathf.Lerp(bed, groundY, u * u);

                if (floor < carvedY) carvedY = floor;
                if (surf > surfaceY) surfaceY = surf;
                hit = true;
            }

            return hit;
        }
    }
}
