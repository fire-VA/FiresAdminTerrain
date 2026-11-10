using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1101 (Fire: "there is no fucking cultivated soil depth, fix this the same way you fixed the snow" + "still no paths dug in the
    // snow"). The snow raise is visible because it is a VERTEX displacement a metre tall; cultivated rows and a walked trench are
    // finer than the voxel mesh's 1 m vertices, so a vertex displacement there had nothing to move (1098's 6 cm lumps, one sunk vertex
    // per trail step). This refines the ground mesh where the paint mask's G is set (cultivated soil, walk trails, shovel and melt
    // digs) to 0.25 m, so FiresVoxelHeightmap's vertex stage can carve furrow rows and the snow trench into real geometry.
    //
    // Crack-free by construction, inside a chunk and across chunks:
    //  - an EDGE is split (3 points at 1/4, 1/2, 3/4) iff both its ends are up-facing ground with G >= GSplit. That depends only on the
    //    two end vertices, so the two triangles sharing an edge - in this chunk or the neighbour's mesh - split it identically; the
    //    split points are lerped in a canonical (lexicographic) end order, so both sides compute bitwise-identical positions;
    //  - a triangle with all three edges split becomes the regular 4x4 grid (16 triangles); one with one or two split edges becomes a
    //    fan round its centroid through the edge points; no T-junctions anywhere;
    //  - the displacement is a pure function of world x,z and the G sampled at the vertex, so shared vertices move identically.
    // The new vertices are coplanar with the old triangle, so the collider (the same mesh) keeps exactly the old surface. Attributes
    // (normal, tangent, UV0 paint, UV1 waterline, colour) are interpolated. Main thread (it reads the composed paint).
    internal static class VoxelGTessellation
    {
        internal const float GSplit = 0.1f;          // G at which a ground edge refines
        private const int N = 4;                     // 0.25 m
        private const float UpNormal = 0.6f;         // up-facing ground only (the shader's cultivated/snow slope gates start here)
        // 0.2.1105 (lead, round #3: "G refinement verts added 771860" - the low-end rule): the budget per chunk drops to 20k, and only
        // 32 m columns within RangeM of the local player refine. The column set is shared by every chunk (an edge splits only when its
        // midpoint's column is in the set), so two chunks sharing an edge still agree; when the set changes, the chunks with G in the
        // columns that changed (and their neighbours) re-mesh, so the refinement follows the player and is dropped behind him.
        private const int MaxAddedVerts = 20000;     // budget per chunk: past it, split triangles fan instead of gridding (still crack-free)
        private const float RangeM = 56f;            // a column refines while its centre is within this horizontal distance
        private const float RangeOutM = 72f;         // and stays refined until it is past this one (hysteresis)
        private const float BytesPerVert = 60f;      // pos 12 + normal 12 + tangent 16 + uv0 8 + uv1 8 + colour 4
        private static readonly HashSet<Vector2Int> s_range = new HashSet<Vector2Int>();
        private static readonly HashSet<Vector3Int> s_gChunks = new HashSet<Vector3Int>();   // chunks whose last mesh had G ground
        private static readonly Dictionary<Vector3Int, int> s_live = new Dictionary<Vector3Int, int>();
        private static readonly List<Vector2Int> s_changed = new List<Vector2Int>();
        private static readonly List<Vector3Int> s_requeue = new List<Vector3Int>();
        private static float s_tickAt, s_rangeLogAt;
        private static long s_liveVerts;

        private static Vector2Int ColumnOf(float x, float z) => new Vector2Int(Mathf.FloorToInt(x / VoxelChunk.CELLS), Mathf.FloorToInt(z / VoxelChunk.CELLS));

        private static bool InRange(Vector3 a, Vector3 b) => (t_snap != null ? t_snap.Range : s_range).Contains(ColumnOf((a.x + b.x) * 0.5f, (a.z + b.z) * 0.5f));

        // ---- 0.2.1197 (lead, option B: driver frames of 76 ms = a trail group's refinements, ~7-8 ms a chunk, on the main thread) ----
        // The refinement + trench / wall snap + normal guard run on one worker thread. Everything they read that is not the chunk's
        // own buffers is copied on the main thread first (Capture): the composed paint pixels of the 3x3 columns round the chunk,
        // the fire heat list, the refine column set and the DeepNorth flag of the 8 m cells over the chunk. The worker reads only
        // that snapshot (t_snap), so it computes exactly what the main thread would have that frame - no look change. The static
        // scratch below is used under Gate by one thread at a time; the live tallies (s_gChunks, s_live) and the log lines are
        // handed back and applied on the main thread (DrainCommits).
        internal static readonly object Gate = new object();
        private const int DnN = 7;   // 8 m DeepNorth cells over a 32 m chunk, one spare each side
        internal sealed class RefineSnap
        {
            public Vector3Int Key; public float Now;
            public Vector2Int Col0;
            public readonly Color32[][] Px = new Color32[9][];
            public Vector4[] Heat; public int HeatN;
            public readonly HashSet<Vector2Int> Range = new HashSet<Vector2Int>();
            public int DnX0, DnZ0;
            public readonly bool[] Dn = new bool[DnN * DnN];
            public bool HasResult, Any; public int Added, Misses;
            public float MainMs, WorkerMs;
            public readonly List<string> Logs = new List<string>();
        }
        [System.ThreadStatic] private static RefineSnap t_snap;
        private static readonly System.Collections.Concurrent.ConcurrentBag<RefineSnap> s_snapPool = new System.Collections.Concurrent.ConcurrentBag<RefineSnap>();

        private static float Now => t_snap != null ? t_snap.Now : Time.unscaledTime;
        private static void Log(string m) { if (t_snap != null) t_snap.Logs.Add(m); else FiresLogger.LogDebug(m); }
        private static bool HasHeatS => t_snap != null ? t_snap.HeatN > 0 : FatFireBullseye.HasHeat;
        private static bool NearWallS(Vector3 v) => t_snap != null ? FatFireBullseye.NearWall(t_snap.Heat, t_snap.HeatN, v) : FatFireBullseye.NearWall(v);
        private static float WallF(float x, float z) => t_snap != null ? FatFireBullseye.WallField(t_snap.Heat, t_snap.HeatN, x, z) : FatFireBullseye.WallField(x, z);
        private static float G(float x, float z)
        {
            var s = t_snap;
            if (s == null) return VoxelWorld.PaintGAt(x, z);
            var col = new Vector2Int(Mathf.FloorToInt(x / 32f), Mathf.FloorToInt(z / 32f));
            int dx = col.x - s.Col0.x + 1, dz = col.y - s.Col0.y + 1;
            if (dx < 0 || dx > 2 || dz < 0 || dz > 2) { s.Misses++; return 0f; }
            return VoxelWorld.SampleG(s.Px[dz * 3 + dx], col, x, z);
        }

        // Main thread: the snapshot for one chunk's refinement.
        internal static RefineSnap Capture(Vector3Int key, Vector3 origin)
        {
            if (!s_snapPool.TryTake(out var s)) s = new RefineSnap();
            s.Key = key; s.Now = Time.unscaledTime;
            s.HasResult = false; s.Any = false; s.Added = 0; s.Misses = 0; s.MainMs = 0f; s.WorkerMs = 0f; s.Logs.Clear();
            s.Col0 = new Vector2Int(Mathf.FloorToInt((origin.x + 16f) / 32f), Mathf.FloorToInt((origin.z + 16f) / 32f));
            for (int dz = 0; dz < 3; dz++)
                for (int dx = 0; dx < 3; dx++)
                    s.Px[dz * 3 + dx] = VoxelWorld.PaintPixelsOf(new Vector2Int(s.Col0.x + dx - 1, s.Col0.y + dz - 1));
            s.Heat = FatFireBullseye.HeatSnapshot(out s.HeatN);
            s.Range.Clear();
            foreach (var c in s_range) s.Range.Add(c);
            s.DnX0 = Mathf.FloorToInt(origin.x / 8f) - 1; s.DnZ0 = Mathf.FloorToInt(origin.z / 8f) - 1;
            for (int j = 0; j < DnN; j++)
                for (int i = 0; i < DnN; i++)
                    s.Dn[j * DnN + i] = DeepNorthAt((s.DnX0 + i) * 8f + 4f, (s.DnZ0 + j) * 8f + 4f);
            return s;
        }

        // Worker thread: refinement + snaps + normal guard on buf, reading only the snapshot.
        internal static void RunSnap(MeshBuffers buf, RefineSnap s)
        {
            long t0 = Stopwatch.GetTimestamp();
            lock (Gate)
            {
                t_snap = s;
                try
                {
                    try { Apply(buf, s.Key); }
                    catch (System.Exception ex) { s.Logs.Add($"[Cultivated] bail: G refinement threw for chunk {s.Key} (worker): {ex.Message} (mesh kept as meshed)"); }
                    try { GuardNormals(buf); }
                    catch (System.Exception ex) { s.Logs.Add($"[FiresVoxel] bail: normal guard threw for chunk {s.Key} (worker): {ex.Message} (normals kept as meshed)"); }
                }
                finally { t_snap = null; }
            }
            s.WorkerMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }

        private static void NoteResult(Vector3Int key, bool any, int added)
        {
            if (t_snap != null) { t_snap.HasResult = true; t_snap.Any = any; t_snap.Added = added; return; }
            CommitTally(key, any, added);
        }

        private static void CommitTally(Vector3Int key, bool any, int added)
        {
            ForgetLive(key);
            if (!any) { s_gChunks.Remove(key); return; }
            s_gChunks.Add(key);
            if (added > 0) { s_live[key] = added; s_liveVerts += added; }
        }

        // Main thread: a finished snapshot's tallies, log lines and timings; the snapshot goes back to the pool.
        private static readonly System.Collections.Concurrent.ConcurrentQueue<RefineSnap> s_done = new System.Collections.Concurrent.ConcurrentQueue<RefineSnap>();
        internal static void Finished(RefineSnap s) => s_done.Enqueue(s);
        private static int s_offN, s_offMisses, s_offFirstSaid;
        private static double s_offMainSum, s_offWorkSum;
        private static float s_offMainMax, s_offWorkMax, s_offLogAt;
        internal static int OffGroupHeld, OffGroupDetached, OffRanOnMain;
        internal static void DrainCommits()
        {
            while (s_done.TryDequeue(out var s))
            {
                if (s.HasResult) CommitTally(s.Key, s.Any, s.Added);
                foreach (var m in s.Logs) FiresLogger.LogDebug(m);
                s_offN++; s_offMisses += s.Misses;
                s_offMainSum += s.MainMs; s_offWorkSum += s.WorkerMs;
                if (s.MainMs > s_offMainMax) s_offMainMax = s.MainMs;
                if (s.WorkerMs > s_offWorkMax) s_offWorkMax = s.WorkerMs;
                if (s_offFirstSaid == 0)
                {
                    s_offFirstSaid = 1;
                    FiresLogger.LogDebug($"[Cultivated] first off-thread G refinement: chunk ({s.Key.x},{s.Key.y},{s.Key.z}), main thread {s.MainMs:0.00} ms (layer paint + snapshot), worker {s.WorkerMs:0.00} ms (refinement + snaps + normal guard), +{s.Added} verts (0.2.1197)");
                }
                System.Array.Clear(s.Px, 0, s.Px.Length); s.Heat = null; s.Logs.Clear();
                s_snapPool.Add(s);
            }
            if (s_offN > 0 && FiresLogger.DebugEnabled && Time.unscaledTime >= s_offLogAt)
            {
                s_offLogAt = Time.unscaledTime + 10f;
                FiresLogger.LogDebug($"[Cultivated] off-thread G refinement 10 s: {s_offN} chunk(s); main thread {s_offMainSum / s_offN:0.00} ms avg / {s_offMainMax:0.00} max per chunk (layer paint + snapshot), worker {s_offWorkSum / s_offN:0.00} ms avg / {s_offWorkMax:0.00} max per chunk (refinement + snaps + normal guard); trail groups held a frame for a refining member {OffGroupHeld}, members detached at the group timeout {OffGroupDetached}; refined on the main thread instead {OffRanOnMain} (seam check on / no renderer); snapshot misses {s_offMisses} (a read outside the 3x3 paint columns = G 0) (0.2.1197)");
                s_offN = 0; s_offMisses = 0; s_offMainSum = 0; s_offWorkSum = 0; s_offMainMax = 0f; s_offWorkMax = 0f;
                OffGroupHeld = 0; OffGroupDetached = 0; OffRanOnMain = 0;
            }
        }

        // Once a second (local player only): recompute the in-range columns; re-mesh the G chunks in columns that entered or left.
        internal static void Tick(Vector3 focus)
        {
            if (Time.unscaledTime < s_tickAt) return;
            s_tickAt = Time.unscaledTime + 1f;
            // unloaded chunks leave the live tally
            s_requeue.Clear();
            foreach (var key in s_gChunks) if (!VoxelWorld.TryGetChunk(key, out var ch) || ch == null) s_requeue.Add(key);
            foreach (var key in s_requeue) { s_gChunks.Remove(key); ForgetLive(key); }
            s_changed.Clear();
            var c0 = ColumnOf(focus.x, focus.z);
            int reach = Mathf.CeilToInt(RangeOutM / VoxelChunk.CELLS) + 1;
            var keep = new HashSet<Vector2Int>();
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    var col = new Vector2Int(c0.x + dx, c0.y + dz);
                    float cx = (col.x + 0.5f) * VoxelChunk.CELLS - focus.x, cz = (col.y + 0.5f) * VoxelChunk.CELLS - focus.z;
                    float d = Mathf.Sqrt(cx * cx + cz * cz);
                    bool was = s_range.Contains(col);
                    if (d <= RangeM || (was && d <= RangeOutM)) { keep.Add(col); if (!was) s_changed.Add(col); }
                }
            foreach (var col in s_range) if (!keep.Contains(col)) s_changed.Add(col);
            if (s_changed.Count == 0) return;
            s_range.Clear();
            foreach (var col in keep) s_range.Add(col);
            s_requeue.Clear();
            foreach (var key in s_gChunks)
                foreach (var col in s_changed)
                    if (Mathf.Abs(key.x - col.x) <= 1 && Mathf.Abs(key.z - col.y) <= 1) { s_requeue.Add(key); break; }
            foreach (var key in s_requeue)
                if (VoxelWorld.TryGetChunk(key, out var chunk) && chunk != null) VoxelMeshPipeline.EnqueueRemesh(chunk);
            if (Time.unscaledTime >= s_rangeLogAt)
            {
                s_rangeLogAt = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[Cultivated] G refinement range: {s_range.Count} column(s) within {RangeM:0} m of the player; {s_changed.Count} changed, {s_requeue.Count} chunk(s) re-meshed; live {s_live.Count} refined chunk(s), {s_liveVerts} verts = {s_liveVerts * BytesPerVert / 1048576f:0.0} MB (cap {MaxAddedVerts} per chunk)");
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(Player), "Update")]
        private static class Patch_Tick
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                try { Tick(__instance.transform.position); } catch (System.Exception ex) { FiresLogger.LogDebug($"[Cultivated] refinement tick threw: {ex.Message}"); }
            }
        }
        private const float GroundUMax = 33.5f / VoxelLayerPaint.MaskWidth;   // ground UV0.x (palette texels sit at x >= 34)

        private static readonly Dictionary<long, int> s_edge = new Dictionary<long, int>();   // edge (lo<<32|hi) -> first of its 3 points
        private static readonly List<int> s_tris = new List<int>(32768);
        private static float[] s_g = new float[4096];
        private static bool[] s_ok = new bool[4096];
        private static readonly int[] s_grid = new int[(N + 1) * (N + 1)];
        private static readonly Stopwatch s_sw = new Stopwatch();
        private static float s_logAt;
        private static long s_chunks, s_added;

        internal static void Apply(MeshBuffers buf, Vector3Int key)
        {
            int nv = buf.Verts.Count;
            if (nv == 0 || buf.Normals.Count != nv || buf.Uvs.Count != nv || buf.Colors.Count != nv) return;
            s_sw.Restart();
            if (s_g.Length < nv) { s_g = new float[Mathf.NextPowerOfTwo(nv)]; s_ok = new bool[s_g.Length]; }
            bool any = false;
            // 0.2.1114 (Fire: "the mesh does not break around the trench"): the split rule must be a function of the vertex POSITION,
            // or two triangles sharing a geometric edge through duplicate vertices (same position, different normal/UV - hard edges,
            // layer seams) split it differently = a T-junction crack. A position is ground if ANY vertex at it is.
            s_posOk.Clear();
            for (int i = 0; i < nv; i++)
            {
                bool ground = buf.Uvs[i].x <= GroundUMax && buf.Normals[i].y >= UpNormal;
                if (!ground) continue;
                s_posOk[buf.Verts[i]] = true;
            }
            for (int i = 0; i < nv; i++)
            {
                var v = buf.Verts[i];
                bool ground = s_posOk.ContainsKey(v);
                float g = ground ? G(v.x, v.z) : 0f;
                s_g[i] = g;
                s_ok[i] = ground && (g >= GSplit || NearWallS(v));   // 0.2.1128: the fire bullseye wall refines too
                any |= s_ok[i];
            }
            if (!any)
            {
                NoteResult(key, false, 0);
                if (SeamCheck && t_snap == null) { s_seamOpenFace.Remove(key); RecordRimOnly(buf, key); }   // 0.2.1119: a neighbour's rim to match against
                return;
            }
            s_edgeRec.Clear();
            s_baryRec.Clear();

            s_edge.Clear();
            s_tris.Clear();
            int added = 0, grids = 0, fans = 0;
            var tris = buf.Tris;
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var va = buf.Verts[a]; var vb = buf.Verts[b]; var vc = buf.Verts[c];
                bool sab = s_ok[a] && s_ok[b] && InRange(va, vb), sbc = s_ok[b] && s_ok[c] && InRange(vb, vc), sca = s_ok[c] && s_ok[a] && InRange(vc, va);
                int splits = (sab ? 1 : 0) + (sbc ? 1 : 0) + (sca ? 1 : 0);
                if (splits == 0) { s_tris.Add(a); s_tris.Add(b); s_tris.Add(c); continue; }
                int eab = sab ? Edge(buf, a, b, ref added) : -1;
                int ebc = sbc ? Edge(buf, b, c, ref added) : -1;
                int eca = sca ? Edge(buf, c, a, ref added) : -1;
                if (splits == 3 && added < MaxAddedVerts)
                {
                    Grid(buf, a, b, c, eab, ebc, eca, ref added);
                    grids++;
                }
                else
                {
                    Fan(buf, a, b, c, eab, ebc, eca, ref added);
                    fans++;
                }
            }
            int snapped = 0;
            try { snapped = SnapTrench(buf, nv); }
            catch (System.Exception ex) { Log($"[SnowTrail] bail: trench edge snap threw for chunk {key}: {ex.Message} (refined mesh kept unsnapped)"); }
            try { snapped += SnapFireWall(buf); }
            catch (System.Exception ex) { Log($"[FireMelt] bail: bullseye wall snap threw for chunk {key}: {ex.Message} (refined mesh kept unsnapped)"); }
            if (SeamCheck && t_snap == null) CheckSeams(buf, key, nv);   // 0.2.1197: reads live neighbours - never on the refine worker
            tris.Clear();
            tris.AddRange(s_tris);
            s_sw.Stop();
            s_snapped += snapped;
            s_chunks++; s_added += added;
            NoteResult(key, true, added);
            if (Now >= s_logAt)
            {
                s_logAt = Now + 10f;
                Log($"[Cultivated] G refinement chunk ({key.x},{key.y},{key.z}): +{added} verts (0.25 m) = {added * BytesPerVert / 1048576f:0.00} MB, {grids} grid + {fans} fan triangle(s), {s_sw.Elapsed.TotalMilliseconds:0.0} ms; live {s_live.Count} chunk(s) {s_liveVerts} verts = {s_liveVerts * BytesPerVert / 1048576f:0.0} MB ({s_chunks} build(s), {s_added} verts built this session incl. re-meshes); {snapped} vert(s) snapped to the trench walls ({s_snapped} this session); crack-free edge splits, collider surface unchanged");
            }
        }

        private static void ForgetLive(Vector3Int key)
        {
            if (s_live.TryGetValue(key, out int old)) { s_liveVerts -= old; s_live.Remove(key); }
        }

        // The 3 points of edge (u, v), in canonical order (lower vertex position first), created once per edge per chunk.
        private static int Edge(MeshBuffers buf, int u, int v, ref int added)
        {
            int lo = u < v ? u : v, hi = u < v ? v : u;
            long k = ((long)lo << 32) | (uint)hi;
            if (s_edge.TryGetValue(k, out int first)) return first;
            // canonical ends by position, so a neighbour chunk's copy of this edge lerps the same floats
            int p = u, q = v;
            if (Less(buf.Verts[v], buf.Verts[u])) { p = v; q = u; }
            first = buf.Verts.Count;
            for (int s = 1; s < N; s++) { s_edgeRec.Add(new EdgeRec { I = buf.Verts.Count, P = p, Q = q, T = s / (float)N }); Lerp2(buf, p, q, s / (float)N); }
            added += N - 1;
            s_edge[k] = first;
            return first;
        }

        private static bool Less(Vector3 x, Vector3 y)
            => x.x != y.x ? x.x < y.x : x.z != y.z ? x.z < y.z : x.y < y.y;

        // The edge point at fraction s (0..N) walking from u to v, given the edge's canonical first point.
        private static int EdgeAt(MeshBuffers buf, int u, int v, int first, int s)
        {
            if (s == 0) return u;
            if (s == N) return v;
            bool forward = !Less(buf.Verts[v], buf.Verts[u]);   // canonical order runs u -> v
            return first + (forward ? s - 1 : N - 1 - s);
        }

        private static void Grid(MeshBuffers buf, int a, int b, int c, int eab, int ebc, int eca, ref int added)
        {
            // P(i, j) = a + (b - a) i/N + (c - a) j/N, i + j <= N
            for (int j = 0; j <= N; j++)
                for (int i = 0; i + j <= N; i++)
                {
                    int idx;
                    if (j == 0) idx = EdgeAt(buf, a, b, eab, i);
                    else if (i == 0) idx = EdgeAt(buf, a, c, eca, j);
                    else if (i + j == N) idx = EdgeAt(buf, b, c, ebc, j);
                    else { idx = buf.Verts.Count; s_baryRec.Add(new BaryRec { I = idx, A = a, B = b, C = c, U = i / (float)N, V = j / (float)N, Reach = 0.35f / N }); Bary(buf, a, b, c, i / (float)N, j / (float)N); added++; }
                    s_grid[j * (N + 1) + i] = idx;
                }
            for (int j = 0; j < N; j++)
                for (int i = 0; i + j < N; i++)
                {
                    int p00 = s_grid[j * (N + 1) + i], p10 = s_grid[j * (N + 1) + i + 1], p01 = s_grid[(j + 1) * (N + 1) + i];
                    s_tris.Add(p00); s_tris.Add(p10); s_tris.Add(p01);
                    if (i + j < N - 1)
                    {
                        int p11 = s_grid[(j + 1) * (N + 1) + i + 1];
                        s_tris.Add(p10); s_tris.Add(p11); s_tris.Add(p01);
                    }
                }
        }

        private static readonly List<int> s_ring = new List<int>(16);

        private static void Fan(MeshBuffers buf, int a, int b, int c, int eab, int ebc, int eca, ref int added)
        {
            s_ring.Clear();
            s_ring.Add(a);
            if (eab >= 0) for (int s = 1; s < N; s++) s_ring.Add(EdgeAt(buf, a, b, eab, s));
            s_ring.Add(b);
            if (ebc >= 0) for (int s = 1; s < N; s++) s_ring.Add(EdgeAt(buf, b, c, ebc, s));
            s_ring.Add(c);
            if (eca >= 0) for (int s = 1; s < N; s++) s_ring.Add(EdgeAt(buf, c, a, eca, s));
            int centre = buf.Verts.Count;
            Bary(buf, a, b, c, 1f / 3f, 1f / 3f);
            added++;
            for (int r = 0; r < s_ring.Count; r++)
            {
                s_tris.Add(centre); s_tris.Add(s_ring[r]); s_tris.Add(s_ring[(r + 1) % s_ring.Count]);
            }
        }

        // ---- 0.2.1114 trench edge snap (Fire: "make sure that they are not too blocky and that they are smoothed out") ----
        // The shader cuts the trench where G crosses smoothstep(0.46, 0.54), a 0.05 m band, but the mesh only bends at its 0.25 m
        // vertices, so a wall running diagonally across the grid drew as 0.25 m stair-steps (the cut jumps from one vertex row to the
        // next). Here each refinement vertex near a wall slides, inside its parent triangle, onto the nearer wall contour (G 0.54 =
        // the floor rim, G 0.46 = the top rim), so the rims become smooth lines along the walked path and the wall stays one band
        // wide. Moves stay in the parent triangle's plane, so the collider surface is unchanged.
        //  - edge points slide ALONG their parent edge only, from the canonical ends and world G, so both triangles sharing the edge
        //    (in this chunk or the neighbour's) move them identically: no cracks; a slide of at most 0.35 of a step keeps their order;
        //  - interior grid points move in 2D (Newton on world G), at most 0.35 of a step; a move that flips or flattens any of its
        //    triangles is undone (interior points belong to one triangle, so undoing them can't open a seam).
        private struct EdgeRec { public int I, P, Q; public float T; }
        private struct BaryRec { public int I, A, B, C; public float U, V, Reach; }
        private static readonly List<EdgeRec> s_edgeRec = new List<EdgeRec>(8192);
        private static readonly List<BaryRec> s_baryRec = new List<BaryRec>(8192);
        private static readonly Dictionary<Vector3, bool> s_posOk = new Dictionary<Vector3, bool>(8192);
        private static readonly Dictionary<long, bool> s_deepNorth = new Dictionary<long, bool>();
        private static readonly Dictionary<int, Vector3> s_moved = new Dictionary<int, Vector3>();   // interior vertex -> its unsnapped position
        private static long s_snapped;
        private const float SnapWindow = 0.2f;   // only vertices with |G - 0.5| below this are near a wall (0.13 m at 1.5 G/m)

        private static bool DeepNorthAt(float x, float z)
        {
            var sn = t_snap;
            if (sn != null)
            {
                int i = Mathf.FloorToInt(x / 8f) - sn.DnX0, j = Mathf.FloorToInt(z / 8f) - sn.DnZ0;
                if (i < 0 || i >= DnN || j < 0 || j >= DnN) { sn.Misses++; return false; }
                return sn.Dn[j * DnN + i];
            }
            long k = ((long)Mathf.FloorToInt(x / 8f) << 32) ^ (uint)Mathf.FloorToInt(z / 8f);
            if (s_deepNorth.TryGetValue(k, out bool dn)) return dn;
            if (s_deepNorth.Count > 65536) s_deepNorth.Clear();
            dn = WorldGenerator.instance != null && WorldGenerator.instance.GetBiome(Mathf.Floor(x / 8f) * 8f + 4f, Mathf.Floor(z / 8f) * 8f + 4f) == Heightmap.Biome.DeepNorth;
            s_deepNorth[k] = dn;
            return dn;
        }

        // 0.2.1154 (round 35 RDC, Fire: "hard angles and geometry"): the snap target sat EXACTLY on the shader cut band's edge
        // (0.5 +- WallHalfG = smoothstep(0.46, 0.54) ends), so the GPU's bilinear G (8-bit filter weights) put some snapped rim
        // vertices a hair inside the band on the wrong side: lone +1.1-1.4 m teeth on the cut side (frame31996, verts at 1/6 and
        // 1/3 grid fractions). The target now sits SnapMarginG beyond the band edge (0.02 m at 1.5 G/m), so the cut decides
        // cleanly either way. Smaller and safer than re-deriving heights on the CPU: it keeps the snap (the zig-zag rim cure).
        private const float SnapMarginG = 0.03f;
        private static float Target(float g) => g >= 0.5f ? 0.5f + FatSnowTrail.WallHalfG + SnapMarginG : 0.5f - FatSnowTrail.WallHalfG - SnapMarginG;
        private static bool s_marginSaid;

        // 0.2.1154 (round 35 RDC, Fire: "dark spots"): 900-1500 triangles per draw carried vertex normals against their own face
        // (down-facing on flat ground, along the z 799.5 chunk seam / skirts and on trench walls), so light read them as dark
        // blotches and wall streaks. After the refinement + snap, a vertex whose normal points against the area-weighted mean of
        // its own faces takes that mean. Only reversed normals change (dot < 0); every agreeing normal is left as meshed.
        private static Vector3[] s_faceSum = new Vector3[0];
        private static int s_guardFixed, s_guardChunks;
        private static float s_guardLogAt;
        internal static int GuardNormals(MeshBuffers buf)
        {
            int nv = buf.Verts.Count;
            if (nv == 0 || buf.Normals.Count != nv) return 0;
            if (s_faceSum.Length < nv) s_faceSum = new Vector3[Mathf.NextPowerOfTwo(nv)];
            System.Array.Clear(s_faceSum, 0, nv);
            var t = buf.Tris;
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                Vector3 f = Vector3.Cross(buf.Verts[b] - buf.Verts[a], buf.Verts[c] - buf.Verts[a]);   // Unity winding, area-weighted
                s_faceSum[a] += f; s_faceSum[b] += f; s_faceSum[c] += f;
            }
            int fixedN = 0;
            for (int i = 0; i < nv; i++)
            {
                Vector3 s = s_faceSum[i];
                if (s.sqrMagnitude < 1e-12f) continue;
                if (Vector3.Dot(buf.Normals[i], s) >= 0f) continue;
                buf.Normals[i] = s.normalized;
                fixedN++;
            }
            s_guardChunks++; s_guardFixed += fixedN;
            if (FiresLogger.DebugEnabled && Now >= s_guardLogAt && s_guardFixed > 0)
            {
                s_guardLogAt = Now + 30f;
                Log($"[FiresVoxel] normal guard: {s_guardFixed} vertex normal(s) pointed against their own faces in {s_guardChunks} chunk build(s) and took the face mean (round 35 dark spots)");
            }
            return fixedN;
        }

        private static int SnapTrench(MeshBuffers buf, int nv0)
        {
            if (FatSnowTrail.FieldSlope <= 0f) return 0;
            if (!s_marginSaid) { s_marginSaid = true; Log($"[SnowTrail] trench snap targets G 0.5 +- {FatSnowTrail.WallHalfG + SnapMarginG:0.00} (cut band edge + {SnapMarginG:0.00}): snapped rim vertices land clear of the shader's smoothstep(0.46, 0.54) band (0.2.1154, round 35 rim teeth)"); }
            int moved = 0;
            // edge points: slide along the canonical edge P -> Q to the nearest crossing of the wall contour
            const float reachT = 0.35f / N;
            foreach (var r in s_edgeRec)
            {
                Vector3 vp = buf.Verts[r.P], vq = buf.Verts[r.Q];
                Vector3 at = Vector3.Lerp(vp, vq, r.T);
                float g = G(at.x, at.z);
                if (Mathf.Abs(g - 0.5f) >= SnapWindow) continue;
                Vector3 mid = (vp + vq) * 0.5f;
                if (!DeepNorthAt(mid.x, mid.z)) continue;
                float target = Target(g);
                float best = float.NaN;
                const int S = 8;
                float t0 = Mathf.Max(0f, r.T - reachT), t1 = Mathf.Min(1f, r.T + reachT);
                float pt = t0, pg = GAlong(vp, vq, t0) - target;
                for (int s = 1; s <= S; s++)
                {
                    float nt = Mathf.Lerp(t0, t1, s / (float)S), ng = GAlong(vp, vq, nt) - target;
                    if ((pg <= 0f) != (ng <= 0f))
                    {
                        float lo = pt, hi = nt, glo = pg;
                        for (int it = 0; it < 10; it++)
                        {
                            float m = (lo + hi) * 0.5f, gm = GAlong(vp, vq, m) - target;
                            if ((gm <= 0f) == (glo <= 0f)) { lo = m; glo = gm; } else hi = m;
                        }
                        float cross = (lo + hi) * 0.5f;
                        if (float.IsNaN(best) || Mathf.Abs(cross - r.T) < Mathf.Abs(best - r.T)) best = cross;
                    }
                    pt = nt; pg = ng;
                }
                if (float.IsNaN(best)) continue;
                SetLerp(buf, r.I, r.P, r.Q, best);
                moved++;
            }
            // interior grid points: Newton on world G inside the parent triangle
            s_moved.Clear();
            foreach (var r in s_baryRec)
            {
                Vector3 a = buf.Verts[r.A], b = buf.Verts[r.B], c = buf.Verts[r.C];
                Vector3 p0 = buf.Verts[r.I];
                float g0 = G(p0.x, p0.z);
                if (Mathf.Abs(g0 - 0.5f) >= SnapWindow) continue;
                if (!DeepNorthAt(p0.x, p0.z)) continue;
                float target = Target(g0);
                float e1x = b.x - a.x, e1z = b.z - a.z, e2x = c.x - a.x, e2z = c.z - a.z;
                float det = e1x * e2z - e2x * e1z;
                if (Mathf.Abs(det) < 1e-6f) continue;
                float u = r.U, v = r.V;
                bool ok = false;
                for (int it = 0; it < 4; it++)
                {
                    float x = a.x + e1x * u + e2x * v, z = a.z + e1z * u + e2z * v;
                    float g = G(x, z);
                    if (Mathf.Abs(g - target) < 0.004f) { ok = true; break; }
                    const float eps = 0.03f;
                    float gx = (G(x + eps, z) - G(x - eps, z)) / (2f * eps);
                    float gz = (G(x, z + eps) - G(x, z - eps)) / (2f * eps);
                    float gg = gx * gx + gz * gz;
                    if (gg < 1e-4f) break;
                    float dx = (target - g) * gx / gg, dz = (target - g) * gz / gg;
                    float du = (dx * e2z - e2x * dz) / det, dv = (e1x * dz - dx * e1z) / det;
                    u = Mathf.Clamp(u + du, r.U - r.Reach, r.U + r.Reach);
                    v = Mathf.Clamp(v + dv, r.V - r.Reach, r.V + r.Reach);
                    if (u < 0f || v < 0f || u + v > 1f) { ok = false; break; }
                }
                if (!ok) continue;
                s_moved[r.I] = p0;
                SetBary(buf, r.I, r.A, r.B, r.C, u, v);
            }
            // undo interior moves that flip or flatten a triangle (xz winding vs the unsnapped one)
            for (int pass = 0; pass < 3 && s_moved.Count > 0; pass++)
            {
                bool undid = false;
                for (int t = 0; t + 2 < s_tris.Count; t += 3)
                {
                    int i0 = s_tris[t], i1 = s_tris[t + 1], i2 = s_tris[t + 2];
                    bool m0 = s_moved.ContainsKey(i0), m1 = s_moved.ContainsKey(i1), m2 = s_moved.ContainsKey(i2);
                    if (!m0 && !m1 && !m2) continue;
                    float now = Area2(buf.Verts[i0], buf.Verts[i1], buf.Verts[i2]);
                    float was = Area2(m0 ? s_moved[i0] : buf.Verts[i0], m1 ? s_moved[i1] : buf.Verts[i1], m2 ? s_moved[i2] : buf.Verts[i2]);
                    if (Mathf.Abs(was) < 1e-7f) continue;   // vertical/degenerate in xz already (walls of the base mesh)
                    if (now * was > 0f && Mathf.Abs(now) > Mathf.Abs(was) * 0.05f) continue;
                    foreach (int i in new[] { i0, i1, i2 })
                        if (s_moved.TryGetValue(i, out var orig)) { UnSnap(buf, i, orig); s_moved.Remove(i); undid = true; }
                }
                if (!undid) break;
            }
            return moved + s_moved.Count;
        }

        // ---- 0.2.1131 bullseye wall snap (round #28 top-down: a sawtooth dark edge just outside the carved wall on one side) ----
        // The bullseye wall is a 0.25 m vertex step (smoothstep outer -+ 0.125) on the 0.25 m refined mesh, so along the circle the
        // rim alternated between a vertex at the top and one at the foot: a zig-zag rim, lit and shadowed as teeth on the sun-away
        // side. The same cure as the trench: refinement points near the wall slide (edge points along their parent edge from world
        // inputs only, interior points inside their triangle, the same reach and flip-undo) onto the nearer rim circle, outer - 0.125
        // (the foot) or outer + 0.125 (the top). The field is d - outer of the nearest heat source (FatFireBullseye.WallField).
        private const float FireWindow = 0.3f;
        private static float FireTarget(float f) => f >= 0f ? FatFireBullseye.Wall * 0.5f : -FatFireBullseye.Wall * 0.5f;

        private static int SnapFireWall(MeshBuffers buf)
        {
            if (!HasHeatS) return 0;
            int moved = 0;
            const float reachT = 0.35f / N;
            foreach (var r in s_edgeRec)
            {
                Vector3 vp = buf.Verts[r.P], vq = buf.Verts[r.Q];
                Vector3 at = Vector3.Lerp(vp, vq, r.T);
                float f = WallF(at.x, at.z);
                if (Mathf.Abs(f) >= FireWindow) continue;
                float target = FireTarget(f);
                float best = float.NaN;
                const int S = 8;
                float t0 = Mathf.Max(0f, r.T - reachT), t1 = Mathf.Min(1f, r.T + reachT);
                float pt = t0, pg = FAlong(vp, vq, t0) - target;
                for (int s = 1; s <= S; s++)
                {
                    float nt = Mathf.Lerp(t0, t1, s / (float)S), ng = FAlong(vp, vq, nt) - target;
                    if ((pg <= 0f) != (ng <= 0f))
                    {
                        float lo = pt, hi = nt, glo = pg;
                        for (int it = 0; it < 10; it++)
                        {
                            float m = (lo + hi) * 0.5f, gm = FAlong(vp, vq, m) - target;
                            if ((gm <= 0f) == (glo <= 0f)) { lo = m; glo = gm; } else hi = m;
                        }
                        float cross = (lo + hi) * 0.5f;
                        if (float.IsNaN(best) || Mathf.Abs(cross - r.T) < Mathf.Abs(best - r.T)) best = cross;
                    }
                    pt = nt; pg = ng;
                }
                if (float.IsNaN(best)) continue;
                SetLerp(buf, r.I, r.P, r.Q, best);
                moved++;
            }
            s_moved.Clear();
            foreach (var r in s_baryRec)
            {
                Vector3 a = buf.Verts[r.A], b = buf.Verts[r.B], c = buf.Verts[r.C];
                Vector3 p0 = buf.Verts[r.I];
                float f0 = WallF(p0.x, p0.z);
                if (Mathf.Abs(f0) >= FireWindow) continue;
                float target = FireTarget(f0);
                float e1x = b.x - a.x, e1z = b.z - a.z, e2x = c.x - a.x, e2z = c.z - a.z;
                float det = e1x * e2z - e2x * e1z;
                if (Mathf.Abs(det) < 1e-6f) continue;
                float u = r.U, v = r.V;
                bool ok = false;
                for (int it = 0; it < 4; it++)
                {
                    float x = a.x + e1x * u + e2x * v, z = a.z + e1z * u + e2z * v;
                    float f = WallF(x, z);
                    if (Mathf.Abs(f - target) < 0.004f) { ok = true; break; }
                    const float eps = 0.03f;
                    float gx = (WallF(x + eps, z) - WallF(x - eps, z)) / (2f * eps);
                    float gz = (WallF(x, z + eps) - WallF(x, z - eps)) / (2f * eps);
                    float gg = gx * gx + gz * gz;
                    if (gg < 1e-4f) break;
                    float dx = (target - f) * gx / gg, dz = (target - f) * gz / gg;
                    float du = (dx * e2z - e2x * dz) / det, dv = (e1x * dz - dx * e1z) / det;
                    u = Mathf.Clamp(u + du, r.U - r.Reach, r.U + r.Reach);
                    v = Mathf.Clamp(v + dv, r.V - r.Reach, r.V + r.Reach);
                    if (u < 0f || v < 0f || u + v > 1f) { ok = false; break; }
                }
                if (!ok) continue;
                s_moved[r.I] = p0;
                SetBary(buf, r.I, r.A, r.B, r.C, u, v);
            }
            for (int pass = 0; pass < 3 && s_moved.Count > 0; pass++)
            {
                bool undid = false;
                for (int t = 0; t + 2 < s_tris.Count; t += 3)
                {
                    int i0 = s_tris[t], i1 = s_tris[t + 1], i2 = s_tris[t + 2];
                    bool m0 = s_moved.ContainsKey(i0), m1 = s_moved.ContainsKey(i1), m2 = s_moved.ContainsKey(i2);
                    if (!m0 && !m1 && !m2) continue;
                    float now = Area2(buf.Verts[i0], buf.Verts[i1], buf.Verts[i2]);
                    float was = Area2(m0 ? s_moved[i0] : buf.Verts[i0], m1 ? s_moved[i1] : buf.Verts[i1], m2 ? s_moved[i2] : buf.Verts[i2]);
                    if (Mathf.Abs(was) < 1e-7f) continue;
                    if (now * was > 0f && Mathf.Abs(now) > Mathf.Abs(was) * 0.05f) continue;
                    foreach (int i in new[] { i0, i1, i2 })
                        if (s_moved.TryGetValue(i, out var orig)) { UnSnap(buf, i, orig); s_moved.Remove(i); undid = true; }
                }
                if (!undid) break;
            }
            return moved + s_moved.Count;
        }

        private static float FAlong(Vector3 p, Vector3 q, float t)
            => WallF(p.x + (q.x - p.x) * t, p.z + (q.z - p.z) * t);

        private static float GAlong(Vector3 p, Vector3 q, float t)
        {
            float x = p.x + (q.x - p.x) * t, z = p.z + (q.z - p.z) * t;
            return G(x, z);
        }

        private static float Area2(Vector3 a, Vector3 b, Vector3 c) => (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z);

        private static void UnSnap(MeshBuffers buf, int i, Vector3 orig)
        {
            foreach (var r in s_baryRec) if (r.I == i) { SetBary(buf, i, r.A, r.B, r.C, r.U, r.V); return; }
            buf.Verts[i] = orig;
        }

        private static void SetLerp(MeshBuffers buf, int i, int p, int q, float t)
        {
            buf.Verts[i] = Vector3.Lerp(buf.Verts[p], buf.Verts[q], t);
            buf.Normals[i] = Vector3.Lerp(buf.Normals[p], buf.Normals[q], t).normalized;
            if (buf.Tangents.Count > i)
            {
                var ta = buf.Tangents.Count > p ? buf.Tangents[p] : new Vector4(1f, 0f, 0f, -1f);
                var tb = buf.Tangents.Count > q ? buf.Tangents[q] : ta;
                Vector3 tt = Vector3.Lerp(ta, tb, t).normalized;
                buf.Tangents[i] = new Vector4(tt.x, tt.y, tt.z, ta.w);
            }
            buf.Uvs[i] = Vector2.Lerp(buf.Uvs[p], buf.Uvs[q], t);
            if (buf.Uv1.Count > i) buf.Uv1[i] = Vector2.Lerp(buf.Uv1[p], buf.Uv1[q], t);
            buf.Colors[i] = Color32.Lerp(buf.Colors[p], buf.Colors[q], t);
        }

        private static void SetBary(MeshBuffers buf, int i, int a, int b, int c, float u, float v)
        {
            float w = 1f - u - v;
            buf.Verts[i] = buf.Verts[a] * w + buf.Verts[b] * u + buf.Verts[c] * v;
            buf.Normals[i] = (buf.Normals[a] * w + buf.Normals[b] * u + buf.Normals[c] * v).normalized;
            if (buf.Tangents.Count > i)
            {
                var ta = buf.Tangents.Count > a ? buf.Tangents[a] : new Vector4(1f, 0f, 0f, -1f);
                var tb = buf.Tangents.Count > b ? buf.Tangents[b] : ta;
                var tc = buf.Tangents.Count > c ? buf.Tangents[c] : ta;
                Vector3 tt = ((Vector3)ta * w + (Vector3)tb * u + (Vector3)tc * v).normalized;
                buf.Tangents[i] = new Vector4(tt.x, tt.y, tt.z, ta.w);
            }
            buf.Uvs[i] = buf.Uvs[a] * w + buf.Uvs[b] * u + buf.Uvs[c] * v;
            if (buf.Uv1.Count > i) buf.Uv1[i] = buf.Uv1[a] * w + buf.Uv1[b] * u + buf.Uv1[c] * v;
            var ca = (Color)buf.Colors[a]; var cb = (Color)buf.Colors[b]; var cc = (Color)buf.Colors[c];
            buf.Colors[i] = (Color32)(ca * w + cb * u + cc * v);
        }

        // ---- 0.2.1114 seam check (proof only; armed by the test) ----
        // 0.2.1119 (round #16: "cracks 737, T-junctions 0 ... 23 shared, 23 missing in the neighbour"): the 1114 check counted every
        // open edge of a refined triangle that wasn't ON a chunk plane as a crack. But a chunk's surface-nets mesh doesn't end on its
        // planes: it owns the quads of sample edges 0..31, so its rim runs through the vertices of cells -1 and 31, inside the band
        // (-1 m, +1 m) around each plane, and that rim is an open edge of THIS mesh by construction - the neighbour owns the other
        // side of it. And a refinement point that happens to land on a plane sits on an edge only this chunk has (cells -1 <-> 0),
        // so "missing in the neighbour" compared points that exist on one side only. Now each open edge is classified:
        //  face        = both ends in the rim band; matched against the same edge (1 mm keys) in the neighbour chunks that were
        //                re-meshed in the same check, refined or not. Matched = the neighbour continues the surface there (not a gap);
        //                unmatched in a checked neighbour = a real crack (e.g. a cross-chunk T-junction: one side split, one not);
        //                no checked neighbour = unverified (reported, not a pass or a fail by itself);
        //  refine-edge = an inside edge lying on a split parent edge with no partner (a refined <-> unrefined T-junction gap);
        //  interior    = any other inside open edge (a hole).
        // T-junction = a split edge whose unsplit original is still an edge of some output triangle (same chunk).
        // stretch = the longest edge touching a refinement vertex / the median, with where it is and what it is.
        internal static bool SeamCheck;
        internal static int SeamChunks, SeamCracks, SeamTJunctions, SeamBorderMiss, SeamBorderShared;
        internal static int SeamFace, SeamFaceMatched, SeamFaceUnverified, SeamInterior, SeamRefineEdge;   // 0.2.1119
        internal static float SeamMaxStretch;
        internal static string SeamStretchAt = "-";
        internal static int SeamFlipped, SeamDegenerate;   // 0.2.1115
        private static readonly Dictionary<(Vector3Int, Vector3Int), int> s_seamEdges = new Dictionary<(Vector3Int, Vector3Int), int>();
        private static readonly List<float> s_seamLens = new List<float>();
        // per checked chunk: every edge in its rim band (refined or base mesh), and the open rim edges still to be matched
        private static readonly Dictionary<Vector3Int, HashSet<(Vector3Int, Vector3Int)>> s_seamRim = new Dictionary<Vector3Int, HashSet<(Vector3Int, Vector3Int)>>();
        private static readonly Dictionary<Vector3Int, List<(Vector3Int, Vector3Int)>> s_seamOpenFace = new Dictionary<Vector3Int, List<(Vector3Int, Vector3Int)>>();

        // The test re-meshes every loaded refined chunk AND its neighbours (refined or not), so every shared rim gets both sides.
        internal static int RemeshRefined()
        {
            var keys = new HashSet<Vector3Int>();
            foreach (var key in s_gChunks)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                            keys.Add(key + new Vector3Int(dx, dy, dz));
            int n = 0;
            foreach (var key in keys)
                if (VoxelWorld.TryGetChunk(key, out var ch) && ch != null && ch.MeshAll) { VoxelMeshPipeline.EnqueueRemesh(ch); n++; }
            return n;
        }

        internal static void ResetSeams()
        {
            SeamChunks = SeamCracks = SeamTJunctions = SeamBorderMiss = SeamBorderShared = 0;
            SeamFace = SeamFaceMatched = SeamFaceUnverified = SeamInterior = SeamRefineEdge = 0;
            SeamMaxStretch = 0f;
            SeamStretchAt = "-";
            SeamFlipped = SeamDegenerate = 0;
            s_seamRim.Clear();
            s_seamOpenFace.Clear();
        }

        // 1 mm position keys (no float-jitter misses between two chunks' copies of a vertex)
        private static Vector3Int QK(Vector3 p) => new Vector3Int(Mathf.RoundToInt(p.x * 1000f), Mathf.RoundToInt(p.y * 1000f), Mathf.RoundToInt(p.z * 1000f));
        private static (Vector3Int, Vector3Int) EKey(Vector3 x, Vector3 y)
        {
            Vector3Int a = QK(x), b = QK(y);
            bool less = a.x != b.x ? a.x < b.x : a.z != b.z ? a.z < b.z : a.y < b.y;
            return less ? (a, b) : (b, a);
        }

        private static bool InRimBand(Vector3 p, Vector3 o)
        {
            float lx = p.x - o.x, lz = p.z - o.z;
            const float C = VoxelChunk.CELLS;
            return lx < 0.001f || lx > C - 1.001f || lz < 0.001f || lz > C - 1.001f;
        }

        // A checked chunk that refined nothing still records its rim band, so a refined neighbour's open rim edges can be matched.
        private static void RecordRimOnly(MeshBuffers buf, Vector3Int key)
        {
            if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null) return;
            Vector3 o = chunk.WorldOrigin;
            var rim = new HashSet<(Vector3Int, Vector3Int)>();
            var tris = buf.Tris;
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                Vector3 a = buf.Verts[tris[t]], b = buf.Verts[tris[t + 1]], c = buf.Verts[tris[t + 2]];
                if (InRimBand(a, o) && InRimBand(b, o)) rim.Add(EKey(a, b));
                if (InRimBand(b, o) && InRimBand(c, o)) rim.Add(EKey(b, c));
                if (InRimBand(c, o) && InRimBand(a, o)) rim.Add(EKey(c, a));
            }
            s_seamRim[key] = rim;
        }

        private static void CheckSeams(MeshBuffers buf, Vector3Int key, int nv0)
        {
            if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null) return;
            Vector3 o = chunk.WorldOrigin;
            s_seamEdges.Clear();
            s_seamLens.Clear();
            var rim = new HashSet<(Vector3Int, Vector3Int)>();
            float longest = 0f; string longestAt = "-";
            for (int t = 0; t + 2 < s_tris.Count; t += 3)
            {
                int i0 = s_tris[t], i1 = s_tris[t + 1], i2 = s_tris[t + 2];
                Vector3 a = buf.Verts[i0], b = buf.Verts[i1], c = buf.Verts[i2];
                if (InRimBand(a, o) && InRimBand(b, o)) rim.Add(EKey(a, b));
                if (InRimBand(b, o) && InRimBand(c, o)) rim.Add(EKey(b, c));
                if (InRimBand(c, o) && InRimBand(a, o)) rim.Add(EKey(c, a));
                if (i0 < nv0 && i1 < nv0 && i2 < nv0) continue;
                foreach (var e in new[] { EKey(a, b), EKey(b, c), EKey(c, a) })
                {
                    s_seamEdges.TryGetValue(e, out int n);
                    s_seamEdges[e] = n + 1;
                }
                int[] ix = { i0, i1, i2 };
                for (int k = 0; k < 3; k++)
                {
                    int p = ix[k], q = ix[(k + 1) % 3];
                    float len = (buf.Verts[p] - buf.Verts[q]).magnitude;
                    s_seamLens.Add(len);
                    if (len > longest)
                    {
                        longest = len;
                        Vector3 m = (buf.Verts[p] + buf.Verts[q]) * 0.5f;
                        string kind = p < nv0 && q < nv0 ? "an unsplit base edge of a fan triangle" : "a refined edge";
                        longestAt = $"({m.x:0.0},{m.y:0.0},{m.z:0.0}) {len:0.00} m, {kind}";
                    }
                }
            }
            s_seamRim[key] = rim;
            // the unsplit neighbours of refined triangles share their edges too
            for (int t = 0; t + 2 < s_tris.Count; t += 3)
            {
                int i0 = s_tris[t], i1 = s_tris[t + 1], i2 = s_tris[t + 2];
                if (!(i0 < nv0 && i1 < nv0 && i2 < nv0)) continue;
                Vector3 a = buf.Verts[i0], b = buf.Verts[i1], c = buf.Verts[i2];
                foreach (var e in new[] { EKey(a, b), EKey(b, c), EKey(c, a) })
                    if (s_seamEdges.TryGetValue(e, out int n)) s_seamEdges[e] = n + 1;
            }
            // split parent edges (for the refine-edge class): their canonical ends
            var parents = new List<(Vector3, Vector3)>();
            var parentSeen = new HashSet<(Vector3Int, Vector3Int)>();
            foreach (var r in s_edgeRec)
                if (parentSeen.Add(EKey(buf.Verts[r.P], buf.Verts[r.Q]))) parents.Add((buf.Verts[r.P], buf.Verts[r.Q]));
            bool OnParent(Vector3 x, Vector3 y)
            {
                foreach (var pq in parents)
                    if (DistToSeg(x, pq.Item1, pq.Item2) < 0.002f && DistToSeg(y, pq.Item1, pq.Item2) < 0.002f) return true;
                return false;
            }
            int face = 0, interior = 0, refineEdge = 0;
            var openFace = new List<(Vector3Int, Vector3Int)>();
            foreach (var kv in s_seamEdges)
            {
                if (kv.Value != 1) continue;
                Vector3 x = (Vector3)kv.Key.Item1 / 1000f, y = (Vector3)kv.Key.Item2 / 1000f;
                if (InRimBand(x, o) && InRimBand(y, o)) { face++; openFace.Add(kv.Key); }
                else if (OnParent(x, y)) refineEdge++;
                else interior++;
            }
            s_seamOpenFace[key] = openFace;
            int tj = 0;
            var seenEdge = new HashSet<(Vector3Int, Vector3Int)>();
            for (int t = 0; t + 2 < s_tris.Count; t += 3)
            {
                Vector3 a = buf.Verts[s_tris[t]], b = buf.Verts[s_tris[t + 1]], c = buf.Verts[s_tris[t + 2]];
                seenEdge.Add(EKey(a, b)); seenEdge.Add(EKey(b, c)); seenEdge.Add(EKey(c, a));
            }
            var splitDone = new HashSet<(Vector3Int, Vector3Int)>();
            foreach (var r in s_edgeRec)
            {
                var e = EKey(buf.Verts[r.P], buf.Verts[r.Q]);
                if (splitDone.Add(e) && seenEdge.Contains(e)) tj++;
            }
            // 0.2.1115 (Fire's black spots): flipped = a refined snow-surface triangle whose face turns over once the shader's snow
            // displacement is applied (replica of FiresSnowDisplace's zone term: D = slopeW x depth x (1 - trench x smoothstep(0.46,
            // 0.54, G)); the drift noise is left out); degenerate = a refined triangle with ~zero area before displacement.
            int flipped = 0, degen = 0;
            float trench = FiresVoxelConfig.SnowTrailTrench != null ? Mathf.Clamp01(FiresVoxelConfig.SnowTrailTrench.Value) : 1f;
            Vector3 Disp(int i)
            {
                Vector3 p = buf.Verts[i];
                float slopeW = Mathf.Clamp01((buf.Normals[i].y - 0.35f) / 0.4f);
                float d = VoxelSnowDepth.DepthMetresAt(p);
                if (slopeW <= 0f || d <= 0f) return p;
                float g = VoxelWorld.TryTrailG(p, out float gv) ? gv : 0f;
                float t = Mathf.Clamp01((g - 0.46f) / 0.08f); t = t * t * (3f - 2f * t);
                return p + Vector3.up * (slopeW * d * (1f - trench * t));
            }
            // 0.2.1123 (round #22: "flipped 25", 21 in the chunk at the snow hut where 1.07 m of Core snow now stands over G cut many
            // times): the displacement is VERTICAL only, so it never changes a triangle's xz winding, and the up component of its normal
            // (Cross(...).y depends on x and z alone) keeps its sign - nothing can turn over. The 1115 test dotted the displaced normal
            // with the 3D base normal, so a steep trench wall (a ~1 m drop over 0.05-0.25 m, its face pointing sideways, away from the
            // base triangle's slight tilt) read as "flipped". Flipped now = the up component changing sign; the old count is kept as
            // "steep wall faces" (informational, first 10 logged with G and snow depth) so the next round shows where they are.
            int steep = 0; var steepAt = new System.Text.StringBuilder();
            for (int t = 0; t + 2 < s_tris.Count; t += 3)
            {
                int i0 = s_tris[t], i1 = s_tris[t + 1], i2 = s_tris[t + 2];
                if (i0 < nv0 && i1 < nv0 && i2 < nv0) continue;
                Vector3 n0 = Vector3.Cross(buf.Verts[i1] - buf.Verts[i0], buf.Verts[i2] - buf.Verts[i0]);
                if (n0.magnitude < 1e-6f) { degen++; continue; }
                if (Mathf.Abs(n0.normalized.y) < 0.6f) continue;   // the snow surface only
                Vector3 a = Disp(i0), b = Disp(i1), c = Disp(i2);
                Vector3 n1 = Vector3.Cross(b - a, c - a);
                if (n1.y * n0.y <= 0f) flipped++;
                if (Vector3.Dot(n1, n0) < 0f)
                {
                    if (steep < 10)
                    {
                        Vector3 m = (buf.Verts[i0] + buf.Verts[i1] + buf.Verts[i2]) / 3f;
                        bool refined = i0 >= nv0 && i1 >= nv0 && i2 >= nv0;
                        float gm = VoxelWorld.TryTrailG(m, out float gv) ? gv : -1f;
                        steepAt.Append($" ({m.x:0.0},{m.y:0.0},{m.z:0.0}) {(refined ? "refined" : "fan")} G {gm:0.00} snow {VoxelSnowDepth.DepthMetresAt(m):0.00} m;");
                    }
                    steep++;
                }
            }
            if (steep > 0) FiresLogger.LogDebug($"[SnowTrail] seam check chunk ({key.x},{key.y},{key.z}): steep wall faces {steep} (displaced face turned past 90 deg from its base tilt - a trench wall, not a flip; up component kept), first:{steepAt}");
            SeamFlipped += flipped; SeamDegenerate += degen;
            s_seamLens.Sort();
            float median = s_seamLens.Count > 0 ? s_seamLens[s_seamLens.Count / 2] : 0f;
            float stretch = median > 1e-5f ? longest / median : 0f;
            SeamChunks++; SeamTJunctions += tj; SeamFace += face; SeamInterior += interior; SeamRefineEdge += refineEdge;
            SeamCracks += interior + refineEdge;   // the face class is added by FinishSeams once matched against the neighbours
            if (stretch > SeamMaxStretch) { SeamMaxStretch = stretch; SeamStretchAt = $"{longestAt} (median {median:0.00} m)"; }
            FiresLogger.LogDebug($"[SnowTrail] seam check chunk ({key.x},{key.y},{key.z}): open edges face {face} (matched after the round), interior {interior}, refine-edge {refineEdge}; T-junctions {tj}, edge stretch {stretch:0.00} at {longestAt}, flipped {flipped}, degenerate {degen}");
        }

        private static float DistToSeg(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float l2 = ab.sqrMagnitude;
            float t = l2 > 1e-12f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / l2) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        // After the re-mesh window: each open rim edge of a refined chunk must exist in a checked neighbour (the surface continues).
        internal static void FinishSeams()
        {
            int matched = 0, unmatched = 0, unverified = 0, neighboursChecked = 0, logged = 0;
            foreach (var kv in s_seamOpenFace)
            {
                var key = kv.Key;
                foreach (var e in kv.Value)
                {
                    bool anyChecked = false, found = false;
                    for (int dy = -1; dy <= 1 && !found; dy++)
                        for (int dz = -1; dz <= 1 && !found; dz++)
                            for (int dx = -1; dx <= 1 && !found; dx++)
                            {
                                if (dx == 0 && dy == 0 && dz == 0) continue;
                                if (!s_seamRim.TryGetValue(key + new Vector3Int(dx, dy, dz), out var other)) continue;
                                anyChecked = true;
                                if (other.Contains(e)) found = true;
                            }
                    if (found) matched++;
                    else if (anyChecked)
                    {
                        unmatched++;
                        if (logged++ < 8)
                        {
                            Vector3 x = (Vector3)e.Item1 / 1000f, y = (Vector3)e.Item2 / 1000f;
                            FiresLogger.LogDebug($"[SnowTrail] seam crack: chunk ({key.x},{key.y},{key.z}) rim edge ({x.x:0.00},{x.y:0.00},{x.z:0.00})-({y.x:0.00},{y.y:0.00},{y.z:0.00}) has no partner in any re-meshed neighbour (one side split, the other not / stale)");
                        }
                    }
                    else unverified++;
                }
            }
            foreach (var k in s_seamRim.Keys) if (!s_seamOpenFace.ContainsKey(k)) neighboursChecked++;
            SeamFaceMatched = matched; SeamFaceUnverified = unverified;
            SeamCracks += unmatched;
            SeamBorderShared = matched + unmatched; SeamBorderMiss = unmatched;
            FiresLogger.LogDebug($"[SnowTrail] seam rims: {SeamFace} open rim edge(s) of refined chunks: {matched} continue in a neighbour (not gaps), {unmatched} real (no partner), {unverified} unverified (no neighbour re-meshed); {neighboursChecked} unrefined neighbour(s) recorded");
        }

        private static void Lerp2(MeshBuffers buf, int p, int q, float t)
        {
            buf.Verts.Add(Vector3.Lerp(buf.Verts[p], buf.Verts[q], t));
            buf.Normals.Add(Vector3.Lerp(buf.Normals[p], buf.Normals[q], t).normalized);
            var ta = buf.Tangents.Count > p ? buf.Tangents[p] : new Vector4(1f, 0f, 0f, -1f);
            var tb = buf.Tangents.Count > q ? buf.Tangents[q] : ta;
            Vector3 tt = Vector3.Lerp(ta, tb, t).normalized;
            if (buf.Tangents.Count == buf.Verts.Count - 1) buf.Tangents.Add(new Vector4(tt.x, tt.y, tt.z, ta.w));
            buf.Uvs.Add(Vector2.Lerp(buf.Uvs[p], buf.Uvs[q], t));
            if (buf.Uv1.Count == buf.Verts.Count - 1) buf.Uv1.Add(Vector2.Lerp(buf.Uv1[p], buf.Uv1[q], t));
            buf.Colors.Add(Color32.Lerp(buf.Colors[p], buf.Colors[q], t));
        }

        // Barycentric point a + (b - a) u + (c - a) v.
        private static void Bary(MeshBuffers buf, int a, int b, int c, float u, float v)
        {
            float w = 1f - u - v;
            buf.Verts.Add(buf.Verts[a] * w + buf.Verts[b] * u + buf.Verts[c] * v);
            buf.Normals.Add((buf.Normals[a] * w + buf.Normals[b] * u + buf.Normals[c] * v).normalized);
            var ta = buf.Tangents.Count > a ? buf.Tangents[a] : new Vector4(1f, 0f, 0f, -1f);
            var tb = buf.Tangents.Count > b ? buf.Tangents[b] : ta;
            var tc = buf.Tangents.Count > c ? buf.Tangents[c] : ta;
            Vector3 tt = ((Vector3)ta * w + (Vector3)tb * u + (Vector3)tc * v).normalized;
            if (buf.Tangents.Count == buf.Verts.Count - 1) buf.Tangents.Add(new Vector4(tt.x, tt.y, tt.z, ta.w));
            buf.Uvs.Add(buf.Uvs[a] * w + buf.Uvs[b] * u + buf.Uvs[c] * v);
            if (buf.Uv1.Count == buf.Verts.Count - 1) buf.Uv1.Add(buf.Uv1[a] * w + buf.Uv1[b] * u + buf.Uv1[c] * v);
            var ca = (Color)buf.Colors[a]; var cb = (Color)buf.Colors[b]; var cc = (Color)buf.Colors[c];
            buf.Colors.Add((Color32)(ca * w + cb * u + cc * v));
        }
    }
}
