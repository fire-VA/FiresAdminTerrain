using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Terrain.Voxel
{
    // Voxel P0 - the meshing pipeline. ALL heavy work runs off the main thread:
    //
    //   brush (main) -> remesh queue -> WORKER: snapshot + surface nets
    //     -> apply queue -> MAIN: fill Mesh object (cheap SetVertices, <=2/frame)
    //     -> bake queue  -> WORKER: Physics.BakeMesh (PhysX cook, thread-safe)
    //     -> assign queue-> MAIN: collider.sharedMesh swap (reference only)
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
        private static readonly ConcurrentQueue<VoxelChunk> s_generateQueue = new ConcurrentQueue<VoxelChunk>();
        private const int MaxGenerateRetries = 3;
        private static readonly ConcurrentQueue<ApplyResult> s_applyQueue = new ConcurrentQueue<ApplyResult>();
        private static readonly ConcurrentQueue<BakeJob> s_bakeQueue = new ConcurrentQueue<BakeJob>();
        private static readonly ConcurrentQueue<AssignJob> s_assignQueue = new ConcurrentQueue<AssignJob>();
        private static readonly ConcurrentBag<MeshBuffers> s_bufferPool = new ConcurrentBag<MeshBuffers>();
        private static readonly AutoResetEvent s_signal = new AutoResetEvent(false);

        // Hard cap on concurrently-live mesh buffers (~0.6MB each) - the mesh
        // pipeline's memory ceiling. At login the streamer queues hundreds of
        // chunks and N workers mesh them far faster than the main thread applies
        // them; an uncapped `new MeshBuffers()` ballooned the pool into a
        // process-wide OOM (the voxel workers AND unrelated mods both threw on
        // their next allocation). Capped, a worker that can't get a buffer
        // re-queues its chunk and waits for apply to free one, so pipeline RAM
        // can never run away.
        //
        // Config-driven (Mesh buffer budget) and refreshed on the main thread:
        // too tight starves throughput - the first fixed value of 12 left zones
        // at pendingGen=8-12 for 8-24s - and too loose just costs RAM.
        private static int s_maxBuffers = 16;
        private static int s_buffersAllocated;

        // Main-thread work per apply is a Mesh upload (SetVertices/SetTriangles)
        // and per assign a collider swap. A client shares that frame with
        // rendering; a headless dedi does not render at all, so it gets 4x.
        // Pre-spawn loading screen: nothing renders for the player yet, so the
        // per-frame throttle exists only to protect gameplay that is not
        // happening. The spawn gate flips this while it holds and the spawn
        // area drains wide open instead of 2-per-frame.
        internal static volatile bool SpawnGateBoost;

        private static int ApplyPerFrame
        {
            get
            {
                int n = FiresVoxelConfig.MeshAppliesPerFrame != null
                    ? FiresVoxelConfig.MeshAppliesPerFrame.Value : 2;
                if (Headless) n *= 4;
                if (SpawnGateBoost) n *= 16;
                return n;
            }
        }

        // Refreshed from config on the MAIN thread each driver tick so the
        // setting hot-reloads without a worker ever reading a ConfigEntry.
        private static void RefreshBufferBudget()
        {
            int b = FiresVoxelConfig.MeshBufferBudget != null
                ? FiresVoxelConfig.MeshBufferBudget.Value : 16;
            s_maxBuffers = Headless ? b * 2 : b;
        }

        // -- PIPELINE STAGE TELEMETRY -------------------------------------
        // The dedi measured zones stuck at swapped=0 pendingSwap=44 with their
        // chunks sitting in `queued=N` - i.e. blocked on the MAIN-THREAD apply,
        // not on generation. Two caches were built for the worker side before
        // anyone checked which side was saturated. These counters settle it:
        //
        //   applies/s at or near (ApplyPerFrame x fps)  -> THROTTLE-bound;
        //       raising MeshAppliesPerFrame is the fix, no cache helps.
        //   queues deep but applies/s well under the cap -> COST-bound;
        //       the apply itself is slow, look at mesh upload / collider swap.
        //   starved climbing                            -> the buffer pool cap
        //       is the constraint and workers are idling on backpressure.
        private static long s_pipeApplied, s_pipeAssigned, s_pipeStarved;
        private static double s_pipeMsApply, s_pipeMsAssign;
        private static float s_pipeNextReport, s_pipeWindowStart;
        private static long s_pipeAppliedAtWindow;

        private static void PipeReport()
        {
            if (Time.time < s_pipeNextReport) return;
            float span = Mathf.Max(0.001f, Time.time - s_pipeWindowStart);
            long applied = Interlocked.Read(ref s_pipeApplied);
            float rate = (applied - s_pipeAppliedAtWindow) / span;
            bool idle = applied == s_pipeAppliedAtWindow && s_applyQueue.IsEmpty && s_assignQueue.IsEmpty
                && s_remeshQueue.IsEmpty && s_generateQueue.IsEmpty;
            s_pipeNextReport = Time.time + 15f;
            s_pipeWindowStart = Time.time;
            s_pipeAppliedAtWindow = applied;
            if (idle) return;

            int cap = ApplyPerFrame;
            float fps = 1f / Mathf.Max(0.0001f, Time.smoothDeltaTime);
            long assigned = Interlocked.Read(ref s_pipeAssigned);
            double msA, msB;
            lock (s_bufferPool) { msA = s_pipeMsApply; msB = s_pipeMsAssign; }

            // INFO, not Verbose: this is the line that decides throttle-bound vs cost-bound vs starved, and the
            // 2026-09-26 traverse produced none of it because FAT verbose was off. Self-throttled to one line per
            // 15s and skipped entirely when every queue is idle, so it cannot flood the way global verbose does.
            FiresLogger.LogInfo($"[FiresVoxelPipe] applies {applied}({rate:0.0}/s; cap {cap}/frame x {fps:0} fps = {cap * fps:0.0}/s ceiling) " +
                      $"assigns {assigned} | queues apply={s_applyQueue.Count} assign={s_assignQueue.Count} remesh={s_remeshQueue.Count} generate={s_generateQueue.Count} " +
                      $"| starved {Interlocked.Read(ref s_pipeStarved)} " +
                      $"| buffers alloc={s_buffersAllocated}/{s_maxBuffers} pool={s_bufferPool.Count} " +
                      $"outstanding={Interlocked.Read(ref s_bufRented) - Interlocked.Read(ref s_bufReturned)} " +
                      $"| avg apply {(applied > 0 ? msA / applied : 0):0.00}ms assign {(assigned > 0 ? msB / assigned : 0):0.00}ms");
        }

        // Checkout/return balance. `outstanding` is how many buffers are legally
        // in flight (rented but not yet applied). If that stays at the cap while
        // every queue is empty, buffers are being LEAKED - some path takes one
        // and never gives it back - and the pool can never refill. If it drops
        // to ~0 when the queues drain, there is no leak and starvation is just
        // honest backpressure.
        private static long s_bufRented, s_bufReturned;

        private static MeshBuffers RentBuffer()
        {
            if (s_bufferPool.TryTake(out var b)) { Interlocked.Increment(ref s_bufRented); return b; }
            if (Interlocked.Increment(ref s_buffersAllocated) <= s_maxBuffers)
            {
                try { Interlocked.Increment(ref s_bufRented); return new MeshBuffers(); }
                catch { Interlocked.Decrement(ref s_bufRented); Interlocked.Decrement(ref s_buffersAllocated); return null; }
            }
            Interlocked.Decrement(ref s_buffersAllocated);
            return null;
        }

        private static void ReturnBufferCounted(MeshBuffers buf)
        {
            Interlocked.Increment(ref s_bufReturned);
            ReturnBuffer(buf);
        }

        private static void ReturnBuffer(MeshBuffers buf)
        {
            if (buf == null) return;
            s_bufferPool.Add(buf);
            s_signal.Set();   // wake a worker blocked on backpressure
        }
        // POOL of mesher workers (was a single thread - the world-wide
        // meshing/streaming bottleneck). Each pulls from the SAME concurrent
        // queues + buffer pool; a chunk is dequeued by exactly one worker and
        // its mesh is computed identically regardless of which core runs it
        // (border data is a gather-time snapshot, not a live cross-chunk read),
        // so parallelism cannot change the mesh or introduce a seam.
        private static Thread[] s_workers;
        private static GameObject s_driverGO;

        // Cached on the main thread (EnsureDriver) - the worker must not touch
        // SystemInfo. On a headless dedi the mesh feeds only a MeshCollider, so
        // the worker skips normals/tangents/uv/colors entirely.
        internal static bool Headless;

        public static Transform Root => EnsureDriver().transform;

        public static void EnqueueRemesh(VoxelChunk chunk) => EnqueueRemesh(chunk, null);

        // batchAlsoCovers: keys armed in the same zone build - mutually
        // pending siblings count as covering (they all land before the swap
        // reveals anything), so first-meshes don't grow interior lips that a
        // full-zone re-mesh would then have to shed.
        public static void EnqueueRemesh(VoxelChunk chunk, HashSet<Vector3Int> batchAlsoCovers)
        {
            if (VoxelZoneStreamer.HoldsDataOnly(chunk.Key)) { EnqueueGenerate(chunk); return; }
            // Uniform (all-solid/all-air) chunks have no surface - nothing to
            // mesh, and their arrays are null. This early-out is what makes every
            // scattered EnqueueRemesh call site (heal drain, neighbour pokes,
            // keep-block) safe to fire without a per-caller Kind check.
            if (chunk.Kind != ChunkKind.Full) { chunk.Queued = false; return; }
            EnsureDriver();
            // Convergence by construction: a mesh is never built from an
            // un-mirrored border ring. The audit caught chunks sitting at
            // dataDiff=122 with queued=False - remeshed via orphan/frontier
            // pokes that never re-pulled ring data, so the seam kept the
            // stale surface (the 0.4-0.5m tears).
            if (!chunk.PendingGenerate) chunk.MirrorBordersNow(true);
            chunk.FrontierMask = ComputeFrontierMask(chunk, batchAlsoCovers);
            chunk.OrphanMask = ComputeOrphanMask(chunk, batchAlsoCovers);
            if (chunk.Queued) return;
            if (s_batchDepth > 0 && System.Threading.Thread.CurrentThread.ManagedThreadId == s_batchThread) { s_batched.Add(chunk); return; }
            if (!s_urgentKeys.ContainsKey(chunk.Key) && Defer(chunk)) return;
            chunk.Queued = true;
            NoteRemesh(chunk.Key);
            (s_urgentKeys.ContainsKey(chunk.Key) ? s_urgentRemesh : s_remeshQueue).Enqueue(chunk);
            s_signal.Set();
        }

        // 0.2.862 (Fire 10-08: "sometimes super smooth, other times it redraws the mesh like 8 times"): `Queued` only folds the
        // requests that arrive while a chunk WAITS; a stroke whose stamps land over several frames re-queues the same chunk after
        // each mesh. Evidence line (Debug): a chunk queued for a fresh mesh ChurnLog+ times inside ChurnWindow seconds.
        private const float ChurnWindow = 5f;
        private const int ChurnLog = 3;
        private static readonly Dictionary<Vector3Int, (float since, int count)> s_churn = new Dictionary<Vector3Int, (float, int)>();

        // 0.2.863 (remesh coalescing, Fire 10-08): a multi-stamp edit (a level pad, a ramp) runs inside BeginBatch/EndBatch, and
        // every chunk its stamps touch is queued ONCE when the batch ends instead of once per stamp. Main thread only; nests.
        private static int s_batchDepth, s_batchThread;
        private static readonly HashSet<VoxelChunk> s_batched = new HashSet<VoxelChunk>();

        public static void BeginBatch()
        {
            if (s_batchDepth++ == 0) { s_batchThread = System.Threading.Thread.CurrentThread.ManagedThreadId; s_batched.Clear(); }
        }

        public static void EndBatch()
        {
            if (s_batchDepth <= 0 || --s_batchDepth > 0) return;
            var list = new List<VoxelChunk>(s_batched);
            s_batched.Clear();
            foreach (var c in list) EnqueueRemesh(c);
            if (list.Count > 0) FiresLogger.LogDebug($"[FiresVoxel] edit batch: {list.Count} chunk(s) queued once each");
        }

        // 0.2.865 (Fire 10-09: 60 -> 16 fps dips at his SinkTestGen11 camp; 21 churn lines, chunks re-queued 3-7 times inside
        // 0.1-0.8 s, mostly as neighbours of chunks applying shared records): a chunk queued again within CoalesceWindow of its
        // last queue is DEFERRED and queued once when the window ends (from the driver's Update), so a burst of pokes builds it
        // once. An edited zone's urgent chunks and a batch's chunks are never deferred: player edits stay instant.
        private const float CoalesceWindow = 0.4f;
        private static readonly Dictionary<Vector3Int, float> s_lastQueued = new Dictionary<Vector3Int, float>();
        private static readonly Dictionary<VoxelChunk, float> s_deferred = new Dictionary<VoxelChunk, float>();
        private static int s_deferredTotal;

        private static float Now() => Stopwatch.GetTimestamp() / (float)Stopwatch.Frequency;

        private static bool Defer(VoxelChunk chunk)
        {
            float now = Now();
            lock (s_lastQueued)
            {
                if (s_lastQueued.TryGetValue(chunk.Key, out float last) && now - last < CoalesceWindow)
                {
                    if (!s_deferred.ContainsKey(chunk)) { s_deferred[chunk] = last + CoalesceWindow; s_deferredTotal++; }
                    return true;
                }
                s_lastQueued[chunk.Key] = now;
                if (s_lastQueued.Count > 8192) s_lastQueued.Clear();
                return false;
            }
        }

        // Main thread (the driver's Update): queue the deferred chunks whose window has passed.
        private static readonly List<VoxelChunk> s_due = new List<VoxelChunk>();
        private static void FlushDeferred()
        {
            float now = Now();
            lock (s_lastQueued)
            {
                if (s_deferred.Count == 0) return;
                s_due.Clear();
                foreach (var kv in s_deferred) if (now >= kv.Value) s_due.Add(kv.Key);
                foreach (var c in s_due) { s_deferred.Remove(c); s_lastQueued.Remove(c.Key); }
            }
            foreach (var c in s_due) EnqueueRemesh(c);
        }

        private static void NoteRemesh(Vector3Int key)
        {
            if (!FiresLogger.DebugEnabled) return;   // thread-safe: any caller thread (Stopwatch time + a lock)
            float now = Stopwatch.GetTimestamp() / (float)Stopwatch.Frequency;
            (float since, int count) c;
            lock (s_churn)
            {
                if (!s_churn.TryGetValue(key, out c) || now - c.since > ChurnWindow) c = (now, 0);
                c.count++;
                s_churn[key] = c;
                if (s_churn.Count > 4096) s_churn.Clear();
            }
            if (c.count >= ChurnLog)
            {
                // 0.2.865: who asked (the callers above EnqueueRemesh), only on a churn line so the stack walk stays rare.
                string from = "?";
                try
                {
                    var st = new StackTrace(3, false);
                    var names = new List<string>();
                    for (int f = 0; f < st.FrameCount && names.Count < 3; f++)
                    {
                        var m = st.GetFrame(f)?.GetMethod();
                        if (m != null) names.Add((m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name);
                    }
                    from = string.Join(" < ", names);
                }
                catch { }
                FiresLogger.LogDebug($"[FiresVoxel] remesh churn: chunk {key} queued {c.count} time(s) in {now - c.since:0.0} s (from {from}; {s_deferredTotal} deferred so far)");
            }
        }

        // ── Terraform lane (0.2.711) ─────────────────────────────────────────────────────────────────────────────────
        // A terrain edit's zone rebuild used to queue behind whatever the streamer had in flight (first-time zone meshes,
        // heals), applied at 2 meshes a frame: seconds before a hoe or hammer edit showed (Fire, 10-05). Its chunks now take
        // an urgent lane through the workers and the apply, and the last one landing logs the whole wait at Debug.
        private static readonly ConcurrentQueue<VoxelChunk> s_urgentRemesh = new ConcurrentQueue<VoxelChunk>();
        private static readonly ConcurrentQueue<ApplyResult> s_urgentApply = new ConcurrentQueue<ApplyResult>();
        private static readonly ConcurrentDictionary<Vector3Int, byte> s_urgentKeys = new ConcurrentDictionary<Vector3Int, byte>();
        private const int UrgentAppliesPerFrame = 8;
        private const float UrgentWatchSeconds = 20f;

        private sealed class TerraformWatch
        {
            public Vector2s Zone;
            public float EditedAt, StartedAt;
            public readonly HashSet<Vector3Int> Pending = new HashSet<Vector3Int>();
            public int Total;
        }
        private static readonly List<TerraformWatch> s_watches = new List<TerraformWatch>();

        /// <summary>The chunks of an edited zone about to be rebuilt: mesh and apply them ahead of other work (main thread).</summary>
        internal static void MarkUrgent(List<Vector3Int> keys)
        {
            foreach (var k in keys) s_urgentKeys[k] = 1;
        }

        /// <summary>
        /// After the edited zone's rebuild: watch the chunks it actually sent to the mesher (an unchanged column isn't
        /// remeshed) and log when the last lands; the rest leave the urgent lane (main thread).
        /// </summary>
        internal static void WatchUrgent(List<Vector3Int> keys, Vector2s zone, float editedAt, float startedAt)
        {
            var watch = new TerraformWatch { Zone = zone, EditedAt = editedAt, StartedAt = startedAt };
            foreach (var k in keys)
            {
                if (VoxelWorld.TryGetChunk(k, out var c) && c != null && (c.Queued || c.PendingGenerate)) watch.Pending.Add(k);
                else s_urgentKeys.TryRemove(k, out _);
            }
            watch.Total = watch.Pending.Count;
            s_watches.RemoveAll(w => w.Zone.x == zone.x && w.Zone.y == zone.y);
            if (watch.Total > 0) s_watches.Add(watch);
            else FiresLogger.LogDebug($"[FiresVoxel] terraform: zone ({zone.x},{zone.y}) rebuilt with no chunk to remesh " +
                                      $"({(startedAt - editedAt) * 1000f:0} ms after its heightmap changed)");
        }

        // A mesh landed (main thread): leave the urgent lane, and close the zone's watch when it was the last one.
        private static void NoteApplied(Vector3Int key)
        {
            if (!s_urgentKeys.TryRemove(key, out _) && s_watches.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = s_watches.Count - 1; i >= 0; i--)
            {
                var w = s_watches[i];
                bool expired = now - w.StartedAt > UrgentWatchSeconds;
                if (!w.Pending.Remove(key) && !expired) continue;
                if (w.Pending.Count > 0 && !expired) continue;
                FiresLogger.LogDebug($"[FiresVoxel] terraform: zone ({w.Zone.x},{w.Zone.y}) visible {(now - w.EditedAt) * 1000f:0} ms after its heightmap changed " +
                                     $"(rebuild started at +{(w.StartedAt - w.EditedAt) * 1000f:0} ms; {w.Total - w.Pending.Count} of {w.Total} chunk mesh(es) landed" +
                                     $"{(expired ? ", gave up waiting for the rest" : "")}; queues: urgent {s_urgentRemesh.Count}, normal {s_remeshQueue.Count}, apply {s_applyQueue.Count})");
                foreach (var k in w.Pending) s_urgentKeys.TryRemove(k, out _);
                s_watches.RemoveAt(i);
            }
        }

        // A +face is ORPHANED when the chunk that owns its in-plane quads
        // does not exist at all - this chunk then meshes the plane itself
        // (real surface quads from its apron samples, not cover geometry).
        public static byte ComputeOrphanMask(VoxelChunk chunk) => ComputeOrphanMask(chunk, null);

        // Server data: a chunk's density generated on the workers, never meshed.
        public static void EnqueueGenerate(VoxelChunk chunk)
        {
            if (chunk == null || chunk.Kind != ChunkKind.Full || !chunk.PendingGenerate || chunk.Queued) return;
            EnsureDriver();
            chunk.Queued = true;
            s_generateQueue.Enqueue(chunk);
            s_signal.Set();
        }

        private static bool MeshesAt(Vector3Int key)
            => VoxelWorld.TryGetChunk(key, out _) && !VoxelZoneStreamer.HoldsDataOnly(key);

        // When a chunk's mesh APPLIES, any lateral neighbour that was orphan-
        // meshing a shared plane toward it hands the face back immediately.
        private static void PokeOrphanNeighbours(VoxelChunk chunk)
        {
            // -X neighbour orphan-meshes its +X face toward us; -Z likewise.
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
        // EnqueueRemesh re-mirrors again harmlessly (0 diffs) - the chain
        // terminates as soon as both rings match both interiors.
        private static void PokeStaleNeighbours(VoxelChunk chunk)
        {
            // All SIX face neighbours - the mirror contract is 26-direction
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
                // gated on Version!=Applied - during wrap-in every sibling is
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
            if ((batch == null || !batch.Contains(px)) && !MeshesAt(px)) mask |= 1;
            if ((batch == null || !batch.Contains(pz)) && !MeshesAt(pz)) mask |= 4;
            return mask;
        }

        // Main-thread only (reads VoxelWorld). A face is a FRONTIER when the
        // horizontal neighbour that owns its in-plane border quads won't emit
        // them - absent, still generating, or a gated brush chunk.
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
                if (VoxelWorld.TryGetChunk(k, out var n) && n != null && !VoxelZoneStreamer.HoldsDataOnly(k))
                {
                    // A uniform neighbour covers by definition: its faces hold no
                    // surface (classified from apron-inclusive ground), so there
                    // is no gap for a frontier lip to seal.
                    if (n.Kind != ChunkKind.Full) return true;
                    if (n.MeshAll && !n.PendingGenerate) return true;
                }
                // No chunk at THIS Y is not a frontier when the neighbour
                // column's zone is voxel-solid - per-column Y-ranges diverge
                // on every hillside and the old chunk-at-Y test grew lip
                // slivers along contour lines all over mid-land (boot-2
                // screenshots). Frontier = the ZONE beyond the face isn't
                // solid yet.
                var zone = new Vector2s(FloorDiv(x, 2), FloorDiv(z, 2));
                return VoxelZoneStreamer.IsZoneSolid(zone) && !VoxelZoneStreamer.IsZoneDataOnly(zone);
            }

            int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);
        }

        private static GameObject EnsureDriver()
        {
            // Worker check runs BEFORE the driver early-return: with the old
            // order a dead worker was unrecoverable while the driver GO lived -
            // Queued flags stuck true, queues drained by nobody, every border
            // heal healed data but no mesh ever rebuilt until relog.
            // Ensure the whole pool is alive (any dead worker is respawned so a
            // single crash can't silently halve throughput). Size = cores - 2,
            // clamped [2..6] (leave the main + render threads a core each).
            Headless = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
            RefreshBufferBudget();
            int desired = System.Environment.ProcessorCount - 2;
            if (desired < 2) desired = 2; else if (desired > 6) desired = 6;
            if (s_workers == null) s_workers = new Thread[desired];
            for (int i = 0; i < s_workers.Length; i++)
            {
                if (s_workers[i] != null && s_workers[i].IsAlive) continue;
                if (s_workers[i] != null)
                    FiresLogger.LogError($"[FiresVoxel] mesher worker {i} was DEAD - restarting it.");
                try
                {
                    s_workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"FiresVoxelMesher{i}" };
                    s_workers[i].Start();
                }
                catch (System.Exception ex)
                {
                    // Thread creation ITSELF OOMs on a memory-starved dedi
                    // ("can't create thread"). Don't let it propagate up through
                    // EnqueueRemesh -> AutoHeal -> the streamer tick and cascade;
                    // leave the slot null and retry on a later EnsureDriver once
                    // memory frees. With the workers that DID start still alive,
                    // meshing continues at reduced throughput instead of halting.
                    s_workers[i] = null;
                    FiresLogger.LogWarning($"[FiresVoxel] mesher worker {i} failed to start ({ex.GetType().Name}) - retrying when memory frees.");
                }
            }
            if (s_driverGO != null) return s_driverGO;
            s_driverGO = new GameObject("FiresVoxelTerrain");
            s_driverGO.AddComponent<VoxelPipelineDriver>();
            return s_driverGO;
        }

        // -- Worker thread - meshing + PhysX cooking -------------------------
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
                    catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] BakeMesh threw: {ex.Message}"); }
                    sw.Stop();
                    s_assignQueue.Enqueue(new AssignJob
                    {
                        View = bake.View, Chunk = bake.Chunk, Version = bake.Version,
                        MeshMs = bake.MeshMs, ApplyMs = bake.ApplyMs, BakeMs = (float)sw.Elapsed.TotalMilliseconds,
                    });
                }
                else if (s_urgentRemesh.TryDequeue(out var chunk) || s_remeshQueue.TryDequeue(out chunk))
                {
                    didWork = true;
                    chunk.Queued = false;
                    // DROPPED while queued (zone unwrapped, player flew past):
                    // skip instantly instead of paying the generate+mesh for
                    // terrain nobody is near, so the CURRENT zones get worker
                    // headway (user: "we skip along if we fly too fast").
                    if (chunk.Dropped) continue;
                    // Reclassified to uniform (no surface) while it sat queued -
                    // nothing to mesh; its view was already dropped by the downgrade.
                    if (chunk.Kind != ChunkKind.Full) continue;
                    // RentBuffer applies the memory backpressure: null = every
                    // buffer is in flight (apply hasn't returned one yet), so
                    // re-queue this chunk and wait rather than allocating past the
                    // cap - an uncapped burst was the process-wide OOM. It also
                    // absorbs an allocation OOM internally, so the rent can't kill
                    // the worker (which used to cascade the whole pipeline dead).
                    MeshBuffers buf = RentBuffer();
                    if (buf == null)
                    {
                        // Backpressure: the pool is at its cap and this worker
                        // has nothing to build into. Counted because a high
                        // number means the BUFFER CAP is the constraint, not
                        // the apply rate - a completely different fix.
                        Interlocked.Increment(ref s_pipeStarved);
                        if (!chunk.Queued) { chunk.Queued = true; s_remeshQueue.Enqueue(chunk); }
                        s_signal.WaitOne(10);
                        continue;
                    }
                    // Contain any per-chunk throw so one bad chunk can't kill the
                    // worker (and all meshing until relog).
                    try
                    {
                        sw.Restart();
                        chunk.RunPendingGenerate();   // W1 streamed chunks: fill + cave-carve first (no-op otherwise)
                        int version = chunk.Snapshot(buf.Density, buf.Seed, buf.Biome, buf.WaterLvl, out buf.HasWater, buf.Ground);
                        if (version == VoxelChunk.SnapshotUniform) { ReturnBufferCounted(buf); continue; }
                        // FIRES: per-vertex inland waterline for the wet-shore band. Seed from the reconciled
                        // inland surface (buf.WaterLvl > sea+0.25), then BOUNDED-dilate a few cells so the band
                        // rides onto the dry bank. Baked into UV1.x by the mesher → shader fWL, per-vertex (no
                        // per-column 64m rectangles). Bounded on purpose: flooding would re-wet low ground far
                        // from water (only the shader's ±0.3m height gate limits the band above the line).
                        {
                            var wl = buf.WaterLvl; var wet = buf.WetLine;
                            // 0.2.820 (Fire 10-07: the shore sand/wet band, and the paint edge next to it, ran in 90/45-degree steps):
                            // on a generated world the waterline is seeded per column from the generated water within WetReach m,
                            // sampled on circles in WORLD space (WetLineSeed), so the band's coverage edge is round and the same on
                            // both sides of a chunk border. It used to be the chunk's own store water max-dilated 6 times with a
                            // 4-neighbour kernel inside this chunk's 34 x 34 grid: diamonds (45-degree edges), and a straight cut at
                            // every chunk border where the water lay just outside the ring. The shader's height gate still decides
                            // what shows, following the terrain. Worlds without the generated field keep the old dilation.
                            if (WetLineSeed.Fill(chunk.WorldOrigin, buf.HasWater ? wl : null, wet)) { }
                            else if (!buf.HasWater) System.Array.Clear(wet, 0, wet.Length);
                            else
                            {
                                for (int i = 0; i < wet.Length; i++) { float w = wl[i]; wet[i] = (w > 30.25f) ? w : 0f; }
                                const int SS = VoxelChunk.SAMPLES;
                                var src = buf.WetScratch;
                                for (int pass = 0; pass < 6; pass++)   // ~6 cells of bank — covers the widened wet-shore band (shader height gate limits what actually shows)
                                {
                                    System.Array.Copy(wet, src, wet.Length);
                                    for (int z = 0; z < SS; z++)
                                        for (int x = 0; x < SS; x++)
                                        {
                                            int i = x + SS * z; float m = src[i];
                                            if (x > 0 && src[i - 1] > m) m = src[i - 1];
                                            if (x < SS - 1 && src[i + 1] > m) m = src[i + 1];
                                            if (z > 0 && src[i - SS] > m) m = src[i - SS];
                                            if (z < SS - 1 && src[i + SS] > m) m = src[i + SS];
                                            wet[i] = m;
                                        }
                                }
                            }
                        }
                        buf.MeshAll = chunk.MeshAll;
                        // Crossing Y-band over the SNAPSHOTTED density (NOT the
                        // gen-time density) - brush edits like vcarve dig tunnels
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
                        buf.OriginX = chunk.WorldOrigin.x;
                        buf.OriginY = chunk.WorldOrigin.y;
                        buf.OriginZ = chunk.WorldOrigin.z;
                        buf.CollidersOnly = Headless;
                        VoxelMesherSurfaceNets.Build(buf, chunk.WorldOrigin);
                        sw.Stop();
                        (s_urgentKeys.ContainsKey(chunk.Key) ? s_urgentApply : s_applyQueue).Enqueue(new ApplyResult
                        {
                            Chunk = chunk, Buffers = buf, Version = version,
                            MeshMs = (float)sw.Elapsed.TotalMilliseconds,
                        });
                    }
                    catch (System.Exception ex)
                    {
                        ReturnBufferCounted(buf);
                        FiresLogger.LogError($"[FiresVoxel] mesher worker threw on chunk {chunk.Key}: {ex}");
                        // Back off on OOM so the worker doesn't spin-retry the
                        // allocation instantly and deepen the memory pressure -
                        // give the GC / the sharing process a moment to free.
                        if (ex is System.OutOfMemoryException) s_signal.WaitOne(50);
                        // A pending chunk that threw would otherwise strand
                        // PendingGenerate=true forever (zone swap blocked,
                        // priority heals cycling). Bounded retry - the gather
                        // payload survives a throw (cleared only on success).
                        if (chunk.PendingGenerate && !chunk.Queued && ++chunk.GenFailCount <= 3)
                        {
                            chunk.Queued = true;
                            s_remeshQueue.Enqueue(chunk);
                            s_signal.Set();
                        }
                        else if (chunk.PendingGenerate)
                        {
                            FiresLogger.LogError($"[FiresVoxel] chunk {chunk.Key} generate FAILED {chunk.GenFailCount}x - zone swap stays blocked until a terraform/rewrap re-arms it.");
                        }
                    }
                }
                else if (s_generateQueue.TryDequeue(out var dataChunk))
                {
                    didWork = true;
                    dataChunk.Queued = false;
                    if (dataChunk.Dropped || dataChunk.Kind != ChunkKind.Full) continue;
                    try { dataChunk.RunPendingGenerate(); }
                    catch (System.Exception ex)
                    {
                        FiresLogger.LogError($"[FiresVoxel] server data generate threw on chunk {dataChunk.Key}: {ex.Message}");
                        if (ex is System.OutOfMemoryException) s_signal.WaitOne(50);
                        if (dataChunk.PendingGenerate && !dataChunk.Queued && ++dataChunk.GenFailCount <= MaxGenerateRetries)
                        {
                            dataChunk.Queued = true;
                            s_generateQueue.Enqueue(dataChunk);
                            s_signal.Set();
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

        // -- Main-thread pump -------------------------------------------------
        private sealed class VoxelPipelineDriver : MonoBehaviour
        {
            private readonly Stopwatch _sw = new Stopwatch();

            private float _nextPaintDrain;

            private void Update()
            {
                RefreshBufferBudget();     // config hot-reload (main thread only)
                FlushDeferred();           // 0.2.865: coalesced re-queues whose window has passed
                VoxelWorld.FlushSaves();   // P1 - debounced anchor persistence

                // W3.3 - the driver exists whenever chunks do (brush chunks in
                // Vanilla world mode included); the streamer tick only runs in
                // voxel modes, so the paint drain lives here.
                if (Time.time >= _nextPaintDrain)
                {
                    _nextPaintDrain = Time.time + 0.25f;
                    VoxelWorld.DrainPaintDirty(4);
                }

                // Mesh-truth seam sentinel - silent unless a face diverges.
                VoxelSeamAudit.AutoTick();
                PipeReport();

                // Apply built meshes (<=2/frame).
                var swApply = System.Diagnostics.Stopwatch.StartNew();
                int appliedThisFrame = 0;
                // An edited zone's meshes go first and on top of the per-frame cap (0.2.711).
                int applyCap = ApplyPerFrame + Mathf.Min(s_urgentApply.Count, UrgentAppliesPerFrame);
                for (int n = 0; n < applyCap && (s_urgentApply.TryDequeue(out var r) || s_applyQueue.TryDequeue(out r));)
                {
                    appliedThisFrame++;
                    if (!VoxelWorld.IsLive(r.Chunk))
                    {
                        ReturnBufferCounted(r.Buffers);   // world cleared / chunk dropped
                        continue;
                    }
                    // Downgraded to uniform after this mesh was built (terraform
                    // reclassified it mid-flight). Uniform chunks stay registered,
                    // so IsLive passes - but applying would RESURRECT a view+mesh+
                    // collider onto a chunk the governor accounts as free (the leak
                    // that defeats the RAM ceiling). EnsureUniformChunk already
                    // dropped the view; just recycle and skip.
                    if (r.Chunk.Kind != ChunkKind.Full)
                    {
                        ReturnBufferCounted(r.Buffers);
                        continue;
                    }
                    // DO NOT drop a version-stale mesh. Dropping it left the chunk
                    // VIEWLESS while a neighbour's border-mirror kept bumping the
                    // version, so the view NEVER landed and the zone swap
                    // deadlocked at noView for minutes (log: noView=12 queued=0,
                    // both idle) - a HOLE in the world (vanilla render+collider are
                    // suppressed in voxel mode). Apply the slightly-stale mesh so
                    // the chunk gets a collider NOW and the zone swaps; if the data
                    // genuinely advanced, re-queue ONE refresh so the border seam
                    // corrects on the next pass. A brief seam beats a permanent
                    // hole, and it self-converges once the borders settle.
                    bool stale = r.Version != r.Chunk.Version;
                    var view = VoxelWorld.GetOrCreateView(r.Chunk);
                    if (view.BakeInFlight)
                    {
                        s_applyQueue.Enqueue(r);       // PhysX is reading this mesh - retry next frame
                        break;
                    }

                    _sw.Restart();
                    int meshId = view.ApplyBuffers(r.Buffers);
                    _sw.Stop();
                    // ReturnBuffer, NOT a raw pool add: the raw add put the buffer
                    // back but never called s_signal.Set(), so a worker blocked in
                    // RentBuffer's WaitOne(10) was not woken by the very event that
                    // freed its buffer. It sat out the full timeout and retried -
                    // once per freed buffer, ~4/frame. That is what drove
                    // starved=40696 with the queues sitting empty.
                    ReturnBufferCounted(r.Buffers);
                    NoteApplied(r.Chunk.Key);
                    VoxelWaterSheetMesh.NoteChunkMeshed(r.Chunk.Key);   // 0.2.765: placed water rebuilds over a carved / filled chunk
                    r.Chunk.AppliedVersion = r.Version;   // sweep's stale-mesh test
                    if (stale && !r.Chunk.Queued && !r.Chunk.PendingGenerate) EnqueueRemesh(r.Chunk);
                    // ORPHAN HANDOFF: a lateral neighbour that was meshing a
                    // shared plane as an orphan face (this chunk didn't exist
                    // or hadn't meshed) must drop its stand-in NOW - the
                    // audit caught stand-ins lingering as opposite-normal
                    // slivers (dot -0.9999) until the slow sweep came around.
                    PokeOrphanNeighbours(r.Chunk);
                    // BORDER CONVERGENCE: this chunk's data just became mesh
                    // truth - any lateral neighbour whose ring disagrees with
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
                    // (per-chunk EMPTY timing line retired — mesh/apply health
                    // lives in the [FiresVoxelPipe] ledger; re-add if a chunk
                    // apply regression needs per-chunk attribution again)
                }

                if (appliedThisFrame > 0)
                {
                    Interlocked.Add(ref s_pipeApplied, appliedThisFrame);
                    lock (s_bufferPool) s_pipeMsApply += swApply.Elapsed.TotalMilliseconds;
                }

                // Assign baked colliders (<=2/frame).
                var swAssign = System.Diagnostics.Stopwatch.StartNew();
                int assignedThisFrame = 0;
                for (int n = 0; n < ApplyPerFrame && s_assignQueue.TryDequeue(out var a); n++)
                {
                    assignedThisFrame++;
                    if (a.View == null) continue;
                    a.View.BakeInFlight = false;
                    // Match the VIEW's applied mesh (AppliedVersion), not the
                    // chunk's data Version - a border-mirror bumps Version without
                    // re-applying the mesh, and gating on it left the view with a
                    // mesh but no collider (colliderNotReady) forever. If this bake
                    // is for the mesh the view is actually showing, assign it; a
                    // later re-mesh carries its own bake.
                    if (!VoxelWorld.IsLive(a.Chunk) || a.Version != a.Chunk.AppliedVersion) continue;
                    a.View.AssignCollider();

                    // (per-chunk mesh/bake timing line retired — see the
                    // [FiresVoxelPipe] ledger; this printed once per chunk and
                    // dominated the log during streaming)
                }
                if (assignedThisFrame > 0)
                {
                    Interlocked.Add(ref s_pipeAssigned, assignedThisFrame);
                    lock (s_bufferPool) s_pipeMsAssign += swAssign.Elapsed.TotalMilliseconds;
                }
            }

            private void OnDestroy()
            {
                // World unload - drop everything; the worker thread survives
                // idle (background) and the queues drain to stale-drops.
                VoxelWorld.Clear();
                s_driverGO = null;
            }
        }
    }
}
