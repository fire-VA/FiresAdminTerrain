using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Terrain.Voxel
{
    // Voxel P0 — the meshing pipeline. ALL heavy work runs off the main thread:
    //
    //   brush (main) → remesh queue → WORKER: snapshot + surface nets
    //     → apply queue → MAIN: fill Mesh object (cheap SetVertices, ≤2/frame)
    //     → bake queue  → WORKER: Physics.BakeMesh (PhysX cook, thread-safe)
    //     → assign queue→ MAIN: collider.sharedMesh swap (reference only)
    //
    // Stale-version guards at every main-thread step: a chunk edited mid-flight
    // just drops the old result (a newer job is already queued). BakeInFlight
    // serializes per-view so the mesh is never mutated while PhysX reads it.
    public static class VoxelMeshPipeline
    {
        private sealed class ApplyResult
        {
            public VoxelChunk Chunk; public MeshBuffers Buffers; public int Version; public float MeshMs;
        }
        private sealed class BakeJob
        {
            public VoxelChunkView View; public VoxelChunk Chunk; public int MeshId; public int Version; public float MeshMs, ApplyMs;
        }
        private sealed class AssignJob
        {
            public VoxelChunkView View; public VoxelChunk Chunk; public int Version; public float MeshMs, ApplyMs, BakeMs;
        }

        private static readonly ConcurrentQueue<VoxelChunk> s_remeshQueue = new ConcurrentQueue<VoxelChunk>();
        private static readonly ConcurrentQueue<ApplyResult> s_applyQueue = new ConcurrentQueue<ApplyResult>();
        private static readonly ConcurrentQueue<BakeJob> s_bakeQueue = new ConcurrentQueue<BakeJob>();
        private static readonly ConcurrentQueue<AssignJob> s_assignQueue = new ConcurrentQueue<AssignJob>();
        private static readonly ConcurrentBag<MeshBuffers> s_bufferPool = new ConcurrentBag<MeshBuffers>();
        private static readonly AutoResetEvent s_signal = new AutoResetEvent(false);
        // POOL of mesher workers (was a single thread — the world-wide
        // meshing/streaming bottleneck). Each pulls from the SAME concurrent
        // queues + buffer pool; a chunk is dequeued by exactly one worker and
        // its mesh is computed identically regardless of which core runs it
        // (border data is a gather-time snapshot, not a live cross-chunk read),
        // so parallelism cannot change the mesh or introduce a seam.
        private static Thread[] s_workers;
        private static GameObject s_driverGO;

        public static Transform Root => EnsureDriver().transform;

        public static void EnqueueRemesh(VoxelChunk chunk) => EnqueueRemesh(chunk, null);

        // batchAlsoCovers: keys armed in the same zone build — mutually
        // pending siblings count as covering (they all land before the swap
        // reveals anything), so first-meshes don't grow interior lips that a
        // full-zone re-mesh would then have to shed.
        public static void EnqueueRemesh(VoxelChunk chunk, HashSet<Vector3Int> batchAlsoCovers)
        {
            EnsureDriver();
            // Convergence by construction: a mesh is never built from an
            // un-mirrored border ring. The audit caught chunks sitting at
            // dataDiff=122 with queued=False — remeshed via orphan/frontier
            // pokes that never re-pulled ring data, so the seam kept the
            // stale surface (the 0.4–0.5m tears).
            if (!chunk.PendingGenerate) chunk.MirrorBordersNow(true);
            chunk.FrontierMask = ComputeFrontierMask(chunk, batchAlsoCovers);
            chunk.OrphanMask = ComputeOrphanMask(chunk, batchAlsoCovers);
            if (chunk.Queued) return;
            chunk.Queued = true;
            s_remeshQueue.Enqueue(chunk);
            s_signal.Set();
        }

        // A +face is ORPHANED when the chunk that owns its in-plane quads
        // does not exist at all — this chunk then meshes the plane itself
        // (real surface quads from its apron samples, not cover geometry).
        public static byte ComputeOrphanMask(VoxelChunk chunk) => ComputeOrphanMask(chunk, null);

        // When a chunk's mesh APPLIES, any lateral neighbour that was orphan-
        // meshing a shared plane toward it hands the face back immediately.
        private static void PokeOrphanNeighbours(VoxelChunk chunk)
        {
            // −X neighbour orphan-meshes its +X face toward us; −Z likewise.
            if (VoxelWorld.TryGetChunk(new Vector3Int(chunk.Key.x - 1, chunk.Key.y, chunk.Key.z), out var nx)
                && nx != null && (nx.OrphanMask & 1) != 0 && ComputeOrphanMask(nx) != nx.OrphanMask)
                EnqueueRemesh(nx);
            if (VoxelWorld.TryGetChunk(new Vector3Int(chunk.Key.x, chunk.Key.y, chunk.Key.z - 1), out var nz)
                && nz != null && (nz.OrphanMask & 4) != 0 && ComputeOrphanMask(nz) != nz.OrphanMask)
                EnqueueRemesh(nz);
        }

        // After a chunk's mesh applies, re-mirror any lateral neighbour whose
        // border ring disagrees with the fresh data (or whose mesh never
        // applied its own current version). MirrorBordersNow(true) bumps the
        // neighbour's Version only when samples actually change, and
        // EnqueueRemesh re-mirrors again harmlessly (0 diffs) — the chain
        // terminates as soon as both rings match both interiors.
        private static void PokeStaleNeighbours(VoxelChunk chunk)
        {
            // All SIX face neighbours — the mirror contract is 26-direction
            // (CaptureBorderOverrides walks y edges too), and caves tear on
            // the horizontal y=32k planes exactly like walls tear on x/z.
            for (int f = 0; f < 6; f++)
            {
                int dx = f == 0 ? 1 : f == 1 ? -1 : 0;
                int dz = f == 2 ? 1 : f == 3 ? -1 : 0;
                int dy = f == 4 ? 1 : f == 5 ? -1 : 0;
                if (!VoxelWorld.TryGetChunk(new Vector3Int(chunk.Key.x + dx, chunk.Key.y + dy, chunk.Key.z + dz), out var n)
                    || n == null || n.PendingGenerate) continue;
                // Queued chunks re-mirror too: their job snapshot happens at
                // worker dequeue, and a ring mirrored before THIS data landed
                // would still mesh stale. A version bump from the mirror
                // stale-drops any in-flight job; EnqueueRemesh dedupes. NOT
                // gated on Version≠Applied — during wrap-in every sibling is
                // legitimately mid-flight and that gate would re-queue the
                // whole batch (the sweep already rescues stale-drops).
                if (n.MirrorBordersNow(true) > 0)
                    EnqueueRemesh(n);
            }
        }

        private static byte ComputeOrphanMask(VoxelChunk chunk, HashSet<Vector3Int> batch)
        {
            if (!chunk.MeshAll) return 0;
            byte mask = 0;
            var px = new Vector3Int(chunk.Key.x + 1, chunk.Key.y, chunk.Key.z);
            var pz = new Vector3Int(chunk.Key.x, chunk.Key.y, chunk.Key.z + 1);
            if ((batch == null || !batch.Contains(px)) && !VoxelWorld.TryGetChunk(px, out _)) mask |= 1;
            if ((batch == null || !batch.Contains(pz)) && !VoxelWorld.TryGetChunk(pz, out _)) mask |= 4;
            return mask;
        }

        // Main-thread only (reads VoxelWorld). A face is a FRONTIER when the
        // horizontal neighbour that owns its in-plane border quads won't emit
        // them — absent, still generating, or a gated brush chunk.
        public static byte ComputeFrontierMask(VoxelChunk chunk) => ComputeFrontierMask(chunk, null);

        private static byte ComputeFrontierMask(VoxelChunk chunk, HashSet<Vector3Int> batch)
        {
            if (!chunk.MeshAll) return 0;
            byte mask = 0;
            if (!Covers(chunk.Key.x + 1, chunk.Key.y, chunk.Key.z)) mask |= 1;
            if (!Covers(chunk.Key.x - 1, chunk.Key.y, chunk.Key.z)) mask |= 2;
            if (!Covers(chunk.Key.x, chunk.Key.y, chunk.Key.z + 1)) mask |= 4;
            if (!Covers(chunk.Key.x, chunk.Key.y, chunk.Key.z - 1)) mask |= 8;
            return mask;

            bool Covers(int x, int y, int z)
            {
                var k = new Vector3Int(x, y, z);
                if (batch != null && batch.Contains(k)) return true;
                if (VoxelWorld.TryGetChunk(k, out var n)
                    && n != null && n.MeshAll && !n.PendingGenerate) return true;
                // No chunk at THIS Y is not a frontier when the neighbour
                // column's zone is voxel-solid — per-column Y-ranges diverge
                // on every hillside and the old chunk-at-Y test grew lip
                // slivers along contour lines all over mid-land (boot-2
                // screenshots). Frontier = the ZONE beyond the face isn't
                // solid yet.
                return VoxelZoneStreamer.IsZoneSolid(new Vector2i(FloorDiv(x, 2), FloorDiv(z, 2)));
            }

            int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);
        }

        private static GameObject EnsureDriver()
        {
            // Worker check runs BEFORE the driver early-return: with the old
            // order a dead worker was unrecoverable while the driver GO lived —
            // Queued flags stuck true, queues drained by nobody, every border
            // heal healed data but no mesh ever rebuilt until relog.
            // Ensure the whole pool is alive (any dead worker is respawned so a
            // single crash can't silently halve throughput). Size = cores − 2,
            // clamped [2..6] (leave the main + render threads a core each).
            int desired = System.Environment.ProcessorCount - 2;
            if (desired < 2) desired = 2; else if (desired > 6) desired = 6;
            if (s_workers == null) s_workers = new Thread[desired];
            for (int i = 0; i < s_workers.Length; i++)
            {
                if (s_workers[i] != null && s_workers[i].IsAlive) continue;
                if (s_workers[i] != null)
                    Debug.LogError($"[FiresVoxel] mesher worker {i} was DEAD — restarting it.");
                s_workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"FiresVoxelMesher{i}" };
                s_workers[i].Start();
            }
            if (s_driverGO != null) return s_driverGO;
            s_driverGO = new GameObject("FiresVoxelTerrain");
            s_driverGO.AddComponent<VoxelPipelineDriver>();
            return s_driverGO;
        }

        // ── Worker thread — meshing + PhysX cooking ─────────────────────────
        private static void WorkerLoop()
        {
            var sw = new Stopwatch();
            while (true)
            {
                bool didWork = false;

                if (s_bakeQueue.TryDequeue(out var bake))
                {
                    didWork = true;
                    sw.Restart();
                    try { Physics.BakeMesh(bake.MeshId, false); }
                    catch (System.Exception ex) { Debug.LogWarning($"[FiresVoxel] BakeMesh threw: {ex.Message}"); }
                    sw.Stop();
                    s_assignQueue.Enqueue(new AssignJob
                    {
                        View = bake.View, Chunk = bake.Chunk, Version = bake.Version,
                        MeshMs = bake.MeshMs, ApplyMs = bake.ApplyMs, BakeMs = (float)sw.Elapsed.TotalMilliseconds,
                    });
                }
                else if (s_remeshQueue.TryDequeue(out var chunk))
                {
                    didWork = true;
                    chunk.Queued = false;
                    if (!s_bufferPool.TryTake(out var buf)) buf = new MeshBuffers();
                    // One unhandled exception here used to kill the thread —
                    // and with it ALL meshing until relog. Contain it to the
                    // chunk, name it loudly, keep the loop alive.
                    try
                    {
                        sw.Restart();
                        chunk.RunPendingGenerate();   // W1 streamed chunks: fill + cave-carve first (no-op otherwise)
                        int version = chunk.Snapshot(buf.Density, buf.Seed, buf.Biome);
                        buf.MeshAll = chunk.MeshAll;
                        // Crossing Y-band over the SNAPSHOTTED density (NOT the
                        // gen-time density) — brush edits like vcarve dig tunnels
                        // AFTER gen, often below the surface band; a stale gen
                        // band would skip them and the tunnel would never draw.
                        {
                            const int SS = VoxelChunk.SAMPLES;
                            int bLo = VoxelChunk.CELLS + 2, bHi = -2;
                            var dd = buf.Density;
                            for (int z = -1; z <= VoxelChunk.CELLS; z++)
                                for (int x = -1; x <= VoxelChunk.CELLS; x++)
                                {
                                    bool prev = dd[(x + 1) + SS * (0 + SS * (z + 1))] > 0;
                                    for (int y = 0; y <= VoxelChunk.CELLS; y++)
                                    {
                                        bool cur = dd[(x + 1) + SS * ((y + 1) + SS * (z + 1))] > 0;
                                        if (cur != prev) { if (y - 1 < bLo) bLo = y - 1; if (y > bHi) bHi = y; }
                                        prev = cur;
                                    }
                                }
                            buf.MeshYLo = bLo; buf.MeshYHi = bHi;
                        }
                        buf.EmitLips = FiresVoxelConfig.SeamLips != null && FiresVoxelConfig.SeamLips.Value;
                        buf.FrontierLips = FiresVoxelConfig.FrontierLips != null && FiresVoxelConfig.FrontierLips.Value;
                        buf.FrontierMask = chunk.FrontierMask;
                        buf.OrphanMask = chunk.OrphanMask;
                        buf.KeyX = chunk.Key.x;
                        buf.KeyZ = chunk.Key.z;
                        VoxelMesherSurfaceNets.Build(buf, chunk.WorldOrigin);
                        sw.Stop();
                        s_applyQueue.Enqueue(new ApplyResult
                        {
                            Chunk = chunk, Buffers = buf, Version = version,
                            MeshMs = (float)sw.Elapsed.TotalMilliseconds,
                        });
                    }
                    catch (System.Exception ex)
                    {
                        s_bufferPool.Add(buf);
                        Debug.LogError($"[FiresVoxel] mesher worker threw on chunk {chunk.Key}: {ex}");
                        // A pending chunk that threw would otherwise strand
                        // PendingGenerate=true forever (zone swap blocked,
                        // priority heals cycling). Bounded retry — the gather
                        // payload survives a throw (cleared only on success).
                        if (chunk.PendingGenerate && !chunk.Queued && ++chunk.GenFailCount <= 3)
                        {
                            chunk.Queued = true;
                            s_remeshQueue.Enqueue(chunk);
                            s_signal.Set();
                        }
                        else if (chunk.PendingGenerate)
                        {
                            Debug.LogError($"[FiresVoxel] chunk {chunk.Key} generate FAILED {chunk.GenFailCount}x — zone swap stays blocked until a terraform/rewrap re-arms it.");
                        }
                    }
                }

                // Short idle poll: AutoResetEvent wakes only ONE worker per
                // Set(), so under a burst the other idle workers grab queued
                // chunks on this poll instead of stalling. Empty TryDequeue is
                // ~free, so N threads polling at 10ms costs nothing when idle.
                if (!didWork) s_signal.WaitOne(10);
            }
        }

        // ── Main-thread pump ─────────────────────────────────────────────────
        private sealed class VoxelPipelineDriver : MonoBehaviour
        {
            private readonly Stopwatch _sw = new Stopwatch();

            private float _nextPaintDrain;

            private void Update()
            {
                VoxelWorld.FlushSaves();   // P1 — debounced anchor persistence

                // W3.3 — the driver exists whenever chunks do (brush chunks in
                // Vanilla world mode included); the streamer tick only runs in
                // voxel modes, so the paint drain lives here.
                if (Time.time >= _nextPaintDrain)
                {
                    _nextPaintDrain = Time.time + 0.25f;
                    VoxelWorld.DrainPaintDirty(4);
                }

                // Mesh-truth seam sentinel — silent unless a face diverges.
                VoxelSeamAudit.AutoTick();

                // Apply built meshes (≤2/frame).
                for (int n = 0; n < 2 && s_applyQueue.TryDequeue(out var r);)
                {
                    if (!VoxelWorld.IsLive(r.Chunk) || r.Version != r.Chunk.Version)
                    {
                        s_bufferPool.Add(r.Buffers);   // stale/dead — newer job queued or world cleared
                        continue;
                    }
                    var view = VoxelWorld.GetOrCreateView(r.Chunk);
                    if (view.BakeInFlight)
                    {
                        s_applyQueue.Enqueue(r);       // PhysX is reading this mesh — retry next frame
                        break;
                    }

                    _sw.Restart();
                    int meshId = view.ApplyBuffers(r.Buffers);
                    _sw.Stop();
                    s_bufferPool.Add(r.Buffers);
                    r.Chunk.AppliedVersion = r.Version;   // sweep's stale-mesh test
                    // ORPHAN HANDOFF: a lateral neighbour that was meshing a
                    // shared plane as an orphan face (this chunk didn't exist
                    // or hadn't meshed) must drop its stand-in NOW — the
                    // audit caught stand-ins lingering as opposite-normal
                    // slivers (dot −0.9999) until the slow sweep came around.
                    PokeOrphanNeighbours(r.Chunk);
                    // BORDER CONVERGENCE: this chunk's data just became mesh
                    // truth — any lateral neighbour whose ring disagrees with
                    // it re-mirrors and remeshes NOW. This is the event that
                    // was missing: a neighbour whose last mesh predated this
                    // chunk's final data kept a stale ring forever (audit:
                    // dataDiff=122, queued=False, both sides idle).
                    PokeStaleNeighbours(r.Chunk);
                    n++;

                    if (meshId != 0)
                    {
                        view.BakeInFlight = true;
                        s_bakeQueue.Enqueue(new BakeJob
                        {
                            View = view, Chunk = r.Chunk, MeshId = meshId, Version = r.Version,
                            MeshMs = r.MeshMs, ApplyMs = (float)_sw.Elapsed.TotalMilliseconds,
                        });
                        s_signal.Set();
                    }
                    else if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                    {
                        Debug.Log($"[FiresVoxel] chunk {r.Chunk.Key} v{r.Version} EMPTY | mesh={r.MeshMs:F1}ms apply={_sw.Elapsed.TotalMilliseconds:F1}ms");
                    }
                }

                // Assign baked colliders (≤2/frame).
                for (int n = 0; n < 2 && s_assignQueue.TryDequeue(out var a); n++)
                {
                    if (a.View == null) continue;
                    a.View.BakeInFlight = false;
                    if (!VoxelWorld.IsLive(a.Chunk) || a.Version != a.Chunk.Version) continue;   // newer bake coming
                    a.View.AssignCollider();

                    if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                    {
                        Debug.Log($"[FiresVoxel] chunk {a.Chunk.Key} v{a.Version} | mesh={a.MeshMs:F1}ms apply={a.ApplyMs:F1}ms bake={a.BakeMs:F1}ms verts={a.View.VertexCount} tris={a.View.TriangleCount}");
                    }
                }
            }

            private void OnDestroy()
            {
                // World unload — drop everything; the worker thread survives
                // idle (background) and the queues drain to stale-drops.
                VoxelWorld.Clear();
                s_driverGO = null;
            }
        }
    }
}
