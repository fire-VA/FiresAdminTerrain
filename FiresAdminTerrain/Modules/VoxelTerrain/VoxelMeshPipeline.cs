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
        private static float s_nextSwapLine;   // 0.2.1054 collider-swap line, every 30 s
        private sealed class ApplyResult
        {
            public VoxelChunk Chunk; public MeshBuffers Buffers; public int Version; public float MeshMs;
            // 0.2.1197 off-thread refinement: Stage 0 = as meshed, 1 = on the refine worker (buffers belong to it), 2 = refined.
            // InGroup = held by a trail group (the worker leaves it there); Discard = replaced while refining (the worker frees it).
            // Stage / InGroup / Discard change only under lock (this).
            public int Stage; public bool InGroup, Discard; public int PreVerts, PreTris;
            public uint GroundSig; public bool HasGroundSig;   // 0.2.1344: the density this mesh was built from (worker); VoxelWaterSheetMesh.NoteChunkMeshed
        }

        // ---- 0.2.1197: the refine worker (one thread; VoxelGTessellation's scratch is single-user under its Gate) ----
        private static readonly ConcurrentQueue<(ApplyResult r, VoxelGTessellation.RefineSnap s)> s_refineJobs = new ConcurrentQueue<(ApplyResult, VoxelGTessellation.RefineSnap)>();
        private static readonly AutoResetEvent s_refineWake = new AutoResetEvent(false);
        private static Thread s_refineThread;
        private static bool s_refineBroken;

        private static bool RefineOffThreadOn
            => !Headless && !s_refineBroken && FiresVoxelConfig.RefineOffThread != null && FiresVoxelConfig.RefineOffThread.Value;

        // Main thread: layer paint now, then the refinement on the worker. True = r is on the worker (do not apply it now).
        private static readonly StallPhaseTimer s_refineStartTimer = new StallPhaseTimer("[Cultivated] refine start", 20.0, "view get/create", "worker check", "layer paint", "capture");   // 0.2.1322
        private static bool TryStartRefine(ApplyResult r)
        {
            if (r.Stage != 0 || !RefineOffThreadOn) return false;
            if (!VoxelWorld.IsLive(r.Chunk) || r.Chunk.Kind != ChunkKind.Full || r.Buffers.Verts.Count == 0 || r.Buffers.CollidersOnly) return false;
            if (VoxelGTessellation.SeamCheck) { VoxelGTessellation.OffRanOnMain++; return false; }   // the seam check reads live neighbours: main thread
            // 0.2.1322 (PLAN_fat_driver_stalls step 1b, proof only): refine start split - view get/create, layer paint, capture (+ gen-0 GC).
            var rt = s_refineStartTimer.T; int rgc0 = StallPhaseTimer.Gc();
            rt[0] = StallPhaseTimer.Now();
            var view = VoxelWorld.GetOrCreateView(r.Chunk);
            rt[1] = StallPhaseTimer.Now();
            if (view == null || !view.HasRenderer) { VoxelGTessellation.OffRanOnMain++; return false; }
            if (s_refineThread == null)
            {
                try { s_refineThread = new Thread(RefineLoop) { IsBackground = true, Name = "FiresVoxelRefine" }; s_refineThread.Start(); }
                catch (System.Exception ex) { s_refineThread = null; FiresLogger.LogDebug($"[Cultivated] bail: refine worker thread could not start: {ex.Message} (refining on the main thread)"); s_refineBroken = true; return false; }
            }
            long t0 = Stopwatch.GetTimestamp();
            var buf = r.Buffers;
            r.PreVerts = buf.Verts.Count; r.PreTris = buf.Tris.Count;
            rt[2] = StallPhaseTimer.Now();
            VoxelLayerPaint.Render(r.Chunk.Key, buf.Verts, buf.Uvs, buf.Uv1, buf.Colors);
            rt[3] = StallPhaseTimer.Now();
            var s = VoxelGTessellation.Capture(r.Chunk.Key, r.Chunk.WorldOrigin);
            rt[4] = StallPhaseTimer.Now();
            s.MainMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            s_refineStartTimer.Note(StallPhaseTimer.Gc() - rgc0, $"chunk {r.Chunk.Key} {buf.Verts.Count} verts");
            lock (r) r.Stage = 1;
            s_refineJobs.Enqueue((r, s));
            s_refineWake.Set();
            return true;
        }

        private static void RefineLoop()
        {
            while (true)
            {
                if (!s_refineJobs.TryDequeue(out var job)) { s_refineWake.WaitOne(100); continue; }
                var r = job.r;
                try { VoxelGTessellation.RunSnap(r.Buffers, job.s); }
                catch (System.Exception ex) { job.s.Logs.Add($"[Cultivated] bail: refine worker threw for chunk {r.Chunk?.Key}: {ex.Message}"); }
                VoxelGTessellation.Finished(job.s);
                lock (r)
                {
                    r.Stage = 2;
                    if (r.Discard) { ReturnBufferCounted(r.Buffers); continue; }
                    if (r.InGroup) continue;   // its trail group lands it
                }
                if (r.Chunk != null && s_urgentKeys.ContainsKey(r.Chunk.Key)) s_urgentApply.Enqueue(r); else s_applyQueue.Enqueue(r, r.Chunk);   // 0.2.1344
            }
        }
        private sealed class BakeJob
        {
            public VoxelChunkView View; public VoxelChunk Chunk; public int MeshId; public int Version; public float MeshMs, ApplyMs;
        }
        private sealed class AssignJob
        {
            public VoxelChunkView View; public VoxelChunk Chunk; public int Version; public int MeshId; public float MeshMs, ApplyMs, BakeMs;
        }

        // Meshes whose view was destroyed while the bake worker was still cooking them, by mesh id (guards a native crash).
        // Main thread only (VoxelChunkView.OnDestroy adds, the assign loop frees once that bake returns): PhysX never reads freed memory.
        private static readonly Dictionary<int, Mesh> s_destroyAfterBake = new Dictionary<int, Mesh>();

        internal static void DestroyAfterBake(int meshId, Mesh mesh)
        {
            if (mesh == null) return;
            if (s_destroyAfterBake.TryGetValue(meshId, out var old) && old != null && old != mesh) Object.Destroy(old);
            s_destroyAfterBake[meshId] = mesh;
        }

        private static readonly VoxelNearQueue<VoxelChunk> s_remeshQueue = new VoxelNearQueue<VoxelChunk>(stampPicks: true);   // 0.2.1344: nearest first (key off: the FIFO); 0.2.1353: stamps for a starved re-queue
        private static readonly VoxelNearQueue<VoxelChunk> s_generateQueue = new VoxelNearQueue<VoxelChunk>();   // 0.2.1344
        private const int MaxGenerateRetries = 3;
        private static readonly VoxelNearQueue<ApplyResult> s_applyQueue = new VoxelNearQueue<ApplyResult>(trackChunks: true);   // 0.2.1344
        private static readonly ConcurrentQueue<BakeJob> s_bakeQueue = new ConcurrentQueue<BakeJob>();
        private static readonly ConcurrentQueue<AssignJob> s_assignQueue = new ConcurrentQueue<AssignJob>();
        // 0.2.1105: everything still queued in the pipeline (the circle test waits for 0 before it measures a settled window).
        internal static int Backlog => s_remeshQueue.Count + s_generateQueue.Count + s_applyQueue.Count + s_bakeQueue.Count + s_assignQueue.Count + s_urgentRemesh.Count + s_urgentApply.Count;
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
        // first long traverse produced none of it because FAT verbose was off. Self-throttled to one line per
            // 15s and skipped entirely when every queue is idle, so it cannot flood the way global verbose does.
            FiresLogger.LogDebug($"[FiresVoxelPipe] applies {applied}({rate:0.0}/s; cap {cap}/frame x {fps:0} fps = {cap * fps:0.0}/s ceiling) " +
                      $"assigns {assigned} | queues apply={s_applyQueue.Count} assign={s_assignQueue.Count} remesh={s_remeshQueue.Count} generate={s_generateQueue.Count} " +
                      $"| starved {Interlocked.Read(ref s_pipeStarved)} " +
                      $"| buffers alloc={s_buffersAllocated}/{s_maxBuffers} pool={s_bufferPool.Count} " +
                      $"outstanding={Interlocked.Read(ref s_bufRented) - Interlocked.Read(ref s_bufReturned)} " +
                      $"| avg apply {(applied > 0 ? msA / applied : 0):0.00}ms assign {(assigned > 0 ? msB / assigned : 0):0.00}ms" +
                      $" | record applies held by the 2.5 ms budget {VoxelChunkAnchor.BudgetDeferred} (total) | remeshes folded {s_deferredTotal} (total)");
        }

        // Checkout/return balance. `outstanding` is how many buffers are legally
        // in flight (rented but not yet applied). If that stays at the cap while
        // every queue is empty, buffers are being LEAKED - some path takes one
        // and never gives it back - and the pool can never refill. If it drops
        // to ~0 when the queues drain, there is no leak and starvation is just
        // honest backpressure.
        private static long s_bufRented, s_bufReturned;

        // 0.2.1350 (proof only): worker time and picks, counted only at Debug, read by the generate wait line. Busy % is the share of all workers'
        // wall time spent in generate / mesh / bake over the window since the previous line. A starve costs a worker up to 10 ms (WaitOne(10)).
        private static long s_proofGenTicks, s_proofMeshTicks, s_proofBakeTicks, s_proofPicksGen, s_proofPicksPlain;
        private static long s_proofLastAt, s_proofLastGen, s_proofLastMesh, s_proofLastBake, s_proofLastStarved, s_proofLastPicksGen, s_proofLastPicksPlain, s_proofLastApplied;
        internal static string GenerateWaitProof()
        {
            long now = Stopwatch.GetTimestamp();
            long gen = Interlocked.Read(ref s_proofGenTicks), mesh = Interlocked.Read(ref s_proofMeshTicks), bake = Interlocked.Read(ref s_proofBakeTicks);
            long starved = Interlocked.Read(ref s_pipeStarved), pg = Interlocked.Read(ref s_proofPicksGen), pp = Interlocked.Read(ref s_proofPicksPlain), applied = Interlocked.Read(ref s_pipeApplied);
            int workers = s_workers != null ? s_workers.Length : 0;
            double window = s_proofLastAt == 0 ? 0.0 : (now - s_proofLastAt) / (double)Stopwatch.Frequency;
            double all = window * Mathf.Max(1, workers) * Stopwatch.Frequency;
            string Pct(long ticks) => all > 0.0 ? (ticks / all * 100.0).ToString("0") + "%" : "n/a";
            string line = $"queues remesh {s_remeshQueue.Count} urgent {s_urgentRemesh.Count} apply {s_applyQueue.Count} urgent apply {s_urgentApply.Count} retry {s_applyRetry.Count} refine {s_refineJobs.Count} bake {s_bakeQueue.Count} assign {s_assignQueue.Count} | " +
                $"buffers alloc {s_buffersAllocated}/{s_maxBuffers} pool {s_bufferPool.Count} outstanding {Interlocked.Read(ref s_bufRented) - Interlocked.Read(ref s_bufReturned)} | " +
                $"over the last {window:0.0} s, {workers} workers: generate {Pct(gen - s_proofLastGen)} mesh {Pct(mesh - s_proofLastMesh)} bake {Pct(bake - s_proofLastBake)}, starved +{starved - s_proofLastStarved}, picks needing generate +{pg - s_proofLastPicksGen} plain remesh +{pp - s_proofLastPicksPlain}, applies +{applied - s_proofLastApplied}";
            s_proofLastAt = now; s_proofLastGen = gen; s_proofLastMesh = mesh; s_proofLastBake = bake; s_proofLastStarved = starved; s_proofLastPicksGen = pg; s_proofLastPicksPlain = pp; s_proofLastApplied = applied;
            return line;
        }

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
            if (!chunk.Queued && HoldRemeshUntilSwap(chunk)) return;   // 0.2.1353: ahead of the mirror below: the run at the swap comes back through here and mirrors then
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
            if (!s_urgentKeys.ContainsKey(chunk.Key) && Defer(chunk)) { if (CarveTiming.On) CarveTiming.Deferred(chunk.Key); return; }
            chunk.Queued = true;
            NoteRemesh(chunk.Key);
            if (s_urgentKeys.ContainsKey(chunk.Key)) s_urgentRemesh.Enqueue(chunk); else s_remeshQueue.Enqueue(chunk, chunk);   // 0.2.1344
            s_signal.Set();
        }

        // 0.2.1353 (Fire: "get this terrain loading thing fixed"; login zones: workers 0-2% busy, all 32 buffers held, apply queue 30-32, 5-9 applies per chunk, 59 plain remeshes
        // picked against 17 first generates): while a zone is mid-swap, a remesh that could not change anything shown stops using a buffer. It only notes the chunk (one per chunk);
        // the swap runs each once. Same shape as PlacedWaterSheets' HoldFor / Flush / DrainHeld, including its 20 s give-up. Off: the 1352 path. Main thread.
        //
        // Held only when ALL of these hold, and each one is what keeps a rule:
        //  - the renderer is suppressed until the swap: nothing the player can see goes stale (a zone in VoxelOnly, or past the 1334 give-up, shows its chunks: never held);
        //  - no generate pending, and this chunk's own data has been read by a mesh job (OwnDataMeshed): a first mesh, a brush dab, a saved record, a stamp or a mop always meshes,
        //    so the swap shows the real ground and its collider, and only ring / lip / paint refinement work waits;
        //  - not in the urgent lane or a trail group (live edits);
        //  - TrySwap's gate is already met or on its way without a further mesh: a view whose collider is ready or baking, or no view yet but its built mesh waiting in the apply queue.
        //    A chunk with neither is the swap's own re-arm (noView) and must mesh, so it passes straight through.
        // Queued stays false for a held chunk, so TrySwap's "queued" count never waits on one.
        private sealed class HeldRemeshes { public readonly HashSet<VoxelChunk> Chunks = new HashSet<VoxelChunk>(); public float Since; public int Asks; }
        private static readonly Dictionary<Vector2s, HeldRemeshes> s_heldRemesh = new Dictionary<Vector2s, HeldRemeshes>();
        private static readonly HashSet<Vector2s> s_heldRemeshGaveUp = new HashSet<Vector2s>();   // not held again until the zone swaps or unwraps (a zone that will not swap must not cycle)
        private static readonly List<Vector2s> s_heldRemeshDue = new List<Vector2s>();
        private static readonly Queue<VoxelChunk> s_heldRemeshRun = new Queue<VoxelChunk>();
        private const float HeldRemeshGiveUpSeconds = 20f;
        private const double HeldRemeshRunMs = 1.0;   // a run re-mirrors each chunk, so it spreads over frames (at least one a frame) instead of landing in the swap's frame
        private static float s_heldRemeshCheckAt;
        private static long s_hrAsks, s_hrHeldChunks, s_hrFlushed, s_hrDropped, s_hrGaveUpZones, s_starveAgeKept, s_starveAgeLast;
        private static bool s_hrProof, s_starveProof;

        private static bool HoldRemeshUntilSwap(VoxelChunk chunk)
        {
            if (FiresVoxelConfig.RemeshHoldUntilSwap == null || !FiresVoxelConfig.RemeshHoldUntilSwap.Value) return false;
            if (Thread.CurrentThread.ManagedThreadId != VoxelZoneStreamer.s_wetMainThread) return false;   // the zone table is main-thread
            if (chunk.PendingGenerate || !chunk.SuppressRenderUntilSwap) return false;
            if (s_urgentKeys.ContainsKey(chunk.Key) || s_trailGroupOf.ContainsKey(chunk.Key)) return false;
            if (!VoxelZoneStreamer.ZoneHoldsRemeshes(chunk.Key, out var zone) || s_heldRemeshGaveUp.Contains(zone)) return false;
            if (!chunk.OwnDataMeshed) return false;
            bool gateSafe = VoxelWorld.TryGetView(chunk.Key, out var view) && view != null ? view.ColliderReady || view.BakeInFlight : s_applyQueue.Holds(chunk);
            if (!gateSafe) return false;
            if (!s_heldRemesh.TryGetValue(zone, out var held)) { held = new HeldRemeshes { Since = Time.realtimeSinceStartup }; s_heldRemesh[zone] = held; }
            held.Asks++;
            s_hrAsks++;
            if (held.Chunks.Add(chunk)) s_hrHeldChunks++;
            if (!s_hrProof) { s_hrProof = true; FiresLogger.LogInfo("[FiresVoxel] a zone mid-swap holds the remeshes that cannot change its ground; each runs once at the swap (0.2.1353)"); }
            return true;
        }

        // The zone swapped or left (or gave up, from the drain below): its held chunks line up to run. A dropped chunk is skipped by the run. Returns how many; asks = the requests they folded.
        internal static int ReleaseHeldRemeshes(Vector2s zone, out int asks, out bool gaveUp)
        {
            gaveUp = s_heldRemeshGaveUp.Remove(zone);
            asks = 0;
            if (!s_heldRemesh.TryGetValue(zone, out var held)) return 0;
            s_heldRemesh.Remove(zone);
            asks = held.Asks;
            foreach (var c in held.Chunks) s_heldRemeshRun.Enqueue(c);
            return held.Chunks.Count;
        }

        // Main thread (the driver's Update).
        private static void DrainHeldRemeshes()
        {
            if (s_heldRemesh.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                if (now >= s_heldRemeshCheckAt)
                {
                    s_heldRemeshCheckAt = now + 1f;
                    bool on = FiresVoxelConfig.RemeshHoldUntilSwap != null && FiresVoxelConfig.RemeshHoldUntilSwap.Value;
                    s_heldRemeshDue.Clear();
                    foreach (var kv in s_heldRemesh)
                        if (!on || now - kv.Value.Since >= HeldRemeshGiveUpSeconds || !VoxelZoneStreamer.ZoneHoldsRemeshes(kv.Key)) s_heldRemeshDue.Add(kv.Key);
                    foreach (var z in s_heldRemeshDue)
                    {
                        bool stillMid = on && VoxelZoneStreamer.ZoneHoldsRemeshes(z);
                        float held = now - s_heldRemesh[z].Since;
                        int n = ReleaseHeldRemeshes(z, out int asks, out _);
                        if (!stillMid) continue;
                        s_heldRemeshGaveUp.Add(z);
                        s_hrGaveUpZones++;
                        FiresLogger.LogDebug($"[FiresVoxel] zone ({z.x},{z.y}): gave up holding remeshes after {held:0} s with no swap: {n} chunk(s) ({asks} ask(s)) run now, none held again until it swaps or unwraps (0.2.1353)");
                    }
                }
            }
            if (s_heldRemeshRun.Count == 0) return;
            long t0 = Stopwatch.GetTimestamp(), limit = t0 + (long)(HeldRemeshRunMs * Stopwatch.Frequency / 1000.0);
            do
            {
                var c = s_heldRemeshRun.Dequeue();
                if (c.Dropped || !VoxelWorld.IsLive(c)) { s_hrDropped++; continue; }
                // this run IS the coalesced mesh: past the window, and any deferral of the same chunk folds into it
                lock (s_lastQueued) { s_deferred.Remove(c); s_deferredFirst.Remove(c); s_lastQueued.Remove(c.Key); }
                s_hrFlushed++;
                EnqueueRemesh(c);
            }
            while (s_heldRemeshRun.Count > 0 && Stopwatch.GetTimestamp() < limit);
        }

        // One clause set for the 10 s line; empty when nothing happened. Resets. Also says the starve proof line on the main thread.
        private static string TakeHeldRemeshStats()
        {
            long kept = Interlocked.Read(ref s_starveAgeKept);
            if (kept > 0 && !s_starveProof) { s_starveProof = true; FiresLogger.LogInfo("[FiresVoxel] a chunk starved of a mesh buffer keeps its place by age (0.2.1353)"); }
            long keptNow = kept - s_starveAgeLast;
            s_starveAgeLast = kept;
            string s = "";
            if (s_hrAsks + s_hrFlushed + s_hrDropped + s_hrGaveUpZones > 0 || s_heldRemesh.Count > 0)
                s = $"remeshes held: {s_hrAsks} ask(s) on {s_hrHeldChunks} chunk(s), {s_hrFlushed} run after the swap, {s_hrDropped} dropped with their zone, {s_hrGaveUpZones} zone(s) gave up, {s_heldRemesh.Count} zone(s) holding, {s_heldRemeshRun.Count} waiting to run; ";
            if (keptNow > 0) s += $"starved chunks that kept their age {keptNow}; ";
            s_hrAsks = s_hrHeldChunks = s_hrFlushed = s_hrDropped = s_hrGaveUpZones = 0;
            return s;
        }

        // Edits were sometimes smooth and other times redrew the mesh ~8 times: `Queued` only folds the
        // requests that arrive while a chunk WAITS; a stroke whose stamps land over several frames re-queues the same chunk after
        // each mesh. Evidence line (Debug): a chunk queued for a fresh mesh ChurnLog+ times inside ChurnWindow seconds.
        private const float ChurnWindow = 5f;
        private const int ChurnLog = 3;
        private static readonly Dictionary<Vector3Int, (float since, int count)> s_churn = new Dictionary<Vector3Int, (float, int)>();

        // Remesh coalescing: a multi-stamp edit (a level pad, a ramp) runs inside BeginBatch/EndBatch, and
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

        // 60 -> 16 fps dips at a camp (21 churn lines, chunks re-queued 3-7 times inside
        // 0.1-0.8 s, mostly as neighbours of chunks applying shared records): a chunk queued again within CoalesceWindow of its
        // last queue is DEFERRED and queued once when the window ends (from the driver's Update), so a burst of pokes builds it
        // once. An edited zone's urgent chunks and a batch's chunks are never deferred: player edits stay instant.
        // Chunks were meshed 7-9 times in ~3 s while an area streamed in, each request from a different
        // source - a record apply, a terraform rebuild, the orphan / stale-ring pokes - arriving just over 0.4 s after the last, so
        // none folded, so the window is 1.2 s. Only a re-queue of a chunk queued moments ago waits; a chunk's first mesh and a player's edit (the
        // urgent lane) never do.
        private const float CoalesceWindow = 1.2f;

        // ── THE COALESCING RULE, in one place ────────────────────────────────────────────────────────────────────────
        //   1. A chunk's FIRST mesh is never deferred (nothing to coalesce with).
        //   2. A PLAYER EDIT is never deferred: its chunks ride the urgent lane (MarkUrgent) and what you dig shows at once.
        //   3. Any other re-queue within CoalesceWindow of the chunk's last queue waits, and is queued once when the window ends.
        //   4. A SEAM REFRESH - a mesh that landed stale because a neighbour's border moved on while it was built - is never a
        //      player edit, so it follows rule 3 even on an urgent chunk. Without that, "queued 3 time(s) in
        //      0.0 s" followed each long teleport: a zone rebuilt on arrival is urgent, and its arriving neighbours bumped the border
        //      version several times in a row; each stale mesh re-queued at once through the urgent lane.
        // EnqueueRemesh applies rules 1-3; EnqueueSeamRefresh is rule 4's only entry point.
        internal static void EnqueueSeamRefresh(VoxelChunk chunk)
        {
            if (chunk == null || chunk.Queued || chunk.PendingGenerate) return;
            if (Defer(chunk)) return;   // queued once by FlushDeferred when the window ends
            EnqueueRemesh(chunk);
        }

        private static readonly Dictionary<Vector3Int, float> s_lastQueued = new Dictionary<Vector3Int, float>();
        private static readonly Dictionary<VoxelChunk, float> s_deferred = new Dictionary<VoxelChunk, float>();
        private static readonly Dictionary<VoxelChunk, float> s_deferredFirst = new Dictionary<VoxelChunk, float>();
        private const float MaxSettle = 4f;
        private static int s_deferredTotal;

        private static float Now() => Stopwatch.GetTimestamp() / (float)Stopwatch.Frequency;

        private static bool Defer(VoxelChunk chunk)
        {
            float now = Now();
            lock (s_lastQueued)
            {
                if (s_lastQueued.TryGetValue(chunk.Key, out float last) && now - last < CoalesceWindow + MaxSettle)
                {
                    // Remesh churn from FlushDeferred (chunks meshed every 1.2 s for as long as neighbours
                    // kept streaming in): the wait is TRAILING now. Each new poke pushes the mesh CoalesceWindow later, so a burst
                    // of neighbour applies builds it once, capped at MaxSettle after the first deferral so nothing waits forever.
                    if (!s_deferred.TryGetValue(chunk, out float due)) { s_deferred[chunk] = now + CoalesceWindow; s_deferredFirst[chunk] = now; s_deferredTotal++; }
                    else { float first = s_deferredFirst.TryGetValue(chunk, out float f0) ? f0 : now; s_deferred[chunk] = Mathf.Min(now + CoalesceWindow, first + MaxSettle); }
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
                foreach (var c in s_due) { s_deferred.Remove(c); s_deferredFirst.Remove(c); s_lastQueued.Remove(c.Key); }
            }
            foreach (var c in s_due) { if (CarveTiming.On) CarveTiming.Released(c.Key); EnqueueRemesh(c); }
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
                // Who asked (the callers above EnqueueRemesh), only on a churn line so the stack walk stays rare.
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
                if (FatLogGate.Allow("churn", out int heldC)) FiresLogger.LogDebug($"[FiresVoxel] remesh churn: chunk {key} queued {c.count} time(s) in {now - c.since:0.0} s (from {from}; {s_deferredTotal} deferred so far)" + FatLogGate.Held(heldC));
            }
        }

        // ── Terraform lane ─────────────────────────────────────────────────────────────────────────────────────────────
        // A terrain edit's zone rebuild used to queue behind whatever the streamer had in flight (first-time zone meshes,
        // heals), applied at 2 meshes a frame: seconds before a hoe or hammer edit showed. Its chunks now take
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

        // ── Trail groups (0.2.1157) ────────────────────────────────────────────────────────────────────────────────────────
        // Fire, round 36: "sometimes when we walk the seams seperate and it takes a few frames for them to re seam" - a fresh trail
        // stamp on a chunk border re-meshed one side first and the other a few frames later (the 1119 neighbour re-mesh), a dark
        // see-through crack in between. A trail stamp's chunks now form ONE group: all ride the urgent lane (no coalesce window),
        // each built mesh is held, and the group's meshes swap in together in one frame; until then the old meshes stay visible.
        private sealed class TrailGroup
        {
            public readonly HashSet<Vector3Int> Pending = new HashSet<Vector3Int>();
            public readonly HashSet<Vector3Int> Members = new HashSet<Vector3Int>();
            public readonly List<ApplyResult> Ready = new List<ApplyResult>();
            public int Frame0, Size; public float T0;
            public System.Action<int> OnApplied;
        }
        private static readonly List<TrailGroup> s_trailGroups = new List<TrailGroup>();
        private static readonly Dictionary<Vector3Int, TrailGroup> s_trailGroupOf = new Dictionary<Vector3Int, TrailGroup>();
        // 0.2.1160 (PERF): 1 group a frame across BOTH ApplyTrailGroups calls (was 2 per call = up to 4 a frame at ~5.7 ms a chunk)
        private const int TrailGroupsPerFrame = 1;
        private static int s_tgFrame = -1, s_tgThisFrame;
        internal static bool InTrailGroup(Vector3Int key) => s_trailGroupOf.ContainsKey(key);
        private const float TrailGroupTimeout = 0.5f;
        private static bool s_tgSwapSaid, s_tgPartialSaid;
        private static int s_tgDone, s_tgMinSize = int.MaxValue, s_tgMaxSize, s_tgWaited, s_tgTimedOut, s_tgCapped;
        private static float s_tgLogAt;

        /// <summary>
        /// Queue these chunks as one atomic trail group (main thread). A chunk already in an open group joins it (the groups merge).
        /// Returns how many chunks were queued into the group. onApplied gets the frame the group landed.
        /// </summary>
        internal static int EnqueueTrailGroup(List<VoxelChunk> chunks, System.Action<int> onApplied)
        {
            TrailGroup g = null;
            foreach (var c in chunks) if (c != null && s_trailGroupOf.TryGetValue(c.Key, out var og)) { g = og; break; }
            if (g == null) { g = new TrailGroup { Frame0 = Time.frameCount, T0 = Time.realtimeSinceStartup }; s_trailGroups.Add(g); }
            int added = 0;
            foreach (var c in chunks)
            {
                if (c == null || c.Kind != ChunkKind.Full || !c.MeshAll || c.PendingGenerate || VoxelZoneStreamer.HoldsDataOnly(c.Key)) continue;
                if (s_trailGroupOf.TryGetValue(c.Key, out var other) && other != g)
                {
                    // merge the other open group into this one
                    foreach (var k in other.Pending) g.Pending.Add(k);
                    foreach (var k in other.Members) { g.Members.Add(k); s_trailGroupOf[k] = g; }
                    g.Ready.AddRange(other.Ready);
                    g.OnApplied += other.OnApplied;
                    if (other.Frame0 < g.Frame0) { g.Frame0 = other.Frame0; g.T0 = other.T0; }
                    s_trailGroups.Remove(other);
                }
                s_urgentKeys[c.Key] = 1;
                EnqueueRemesh(c);
                if (!c.Queued) { if (!g.Members.Contains(c.Key)) s_urgentKeys.TryRemove(c.Key, out _); continue; }
                // a member whose result is already held waits for the newer one too (both read the same G at apply)
                for (int i = g.Ready.Count - 1; i >= 0; i--)
                    if (g.Ready[i].Chunk.Key == c.Key) { var old = g.Ready[i]; lock (old) { if (old.Stage == 1) old.Discard = true; else ReturnBufferCounted(old.Buffers); } g.Ready.RemoveAt(i); }   // 0.2.1197: a refining one is freed by the worker
                g.Pending.Add(c.Key);
                if (g.Members.Add(c.Key)) added++;
                s_trailGroupOf[c.Key] = g;
            }
            g.Size = g.Members.Count;
            if (onApplied != null) g.OnApplied += onApplied;
            if (g.Members.Count == 0) { s_trailGroups.Remove(g); return 0; }
            return added;
        }

        // A built mesh of a chunk in an open trail group is held (true) until the whole group is built (main thread).
        private static bool CaptureTrailMember(ApplyResult r)
        {
            if (s_trailGroupOf.Count == 0 || r == null || r.Chunk == null) return false;
            if (!s_trailGroupOf.TryGetValue(r.Chunk.Key, out var g)) return false;
            for (int i = g.Ready.Count - 1; i >= 0; i--)
                if (g.Ready[i].Chunk.Key == r.Chunk.Key)   // keep the newest
                {
                    var old = g.Ready[i];
                    lock (old) { if (old.Stage == 1) old.Discard = true; else ReturnBufferCounted(old.Buffers); }   // 0.2.1197: a refining one is freed by the worker
                    g.Ready.RemoveAt(i);
                }
            lock (r) r.InGroup = true;
            g.Ready.Add(r);
            g.Pending.Remove(r.Chunk.Key);
            return true;
        }

        internal static void TrailGroupStats(out int groups, out int minSize, out int maxSize, out int waited, out int timedOut, out int capped)
        {
            groups = s_tgDone; minSize = s_tgDone > 0 ? s_tgMinSize : 0; maxSize = s_tgMaxSize; waited = s_tgWaited; timedOut = s_tgTimedOut; capped = s_tgCapped;
            s_tgDone = 0; s_tgMinSize = int.MaxValue; s_tgMaxSize = 0; s_tgWaited = 0; s_tgTimedOut = 0; s_tgCapped = 0;
        }

        /// <summary>The chunks of an edited zone about to be rebuilt: mesh and apply them ahead of other work (main thread).</summary>
        internal static void MarkUrgent(List<Vector3Int> keys)
        {
            foreach (var k in keys) s_urgentKeys[k] = 1;
        }

        /// <summary>
        /// A V-tool dab changed this chunk (VoxelWorld.ApplyBrush, main thread): the coalescing rule's "a player edit is never
        /// deferred". Only the hoe/terraform rebuild used to mark its chunks, so a carve stroke's re-dabs waited 1.2-4 s each
        /// (Fire 10-23: "the edits with the voxel carve tools take forever to apply"). The key leaves the lane when its mesh lands.
        /// </summary>
        internal static void MarkEditUrgent(Vector3Int key) => s_urgentKeys[key] = 1;

        // 0.2.1344 near-first: results that met a busy collider bake this frame (main thread), the streamer's question, and the 10 s line.
        private static readonly List<ApplyResult> s_applyRetry = new List<ApplyResult>();
        internal static bool ResultWaiting(VoxelChunk chunk) => s_applyQueue.Holds(chunk);
        private static float s_nearLogAt;
        private static bool s_nearProof;
        private static void NearOrderReport()
        {
            if (!s_nearProof && VoxelNearOrder.On)
            {
                s_nearProof = true;
                FiresLogger.LogInfo("[FiresVoxel] build order: wrap start, generate, remesh and apply take the pending item nearest the player; first wraps left the urgent lane (live edits keep it) (0.2.1344)");
            }
            float now = Time.realtimeSinceStartup;
            if (now < s_nearLogAt) return;
            s_nearLogAt = now + 10f;
            string s = s_remeshQueue.TakeStats("remesh") + s_applyQueue.TakeStats("apply") + s_generateQueue.TakeStats("generate");   // always taken, so a window never carries over
            if (s.Length > 0 && FiresLogger.DebugEnabled) FiresLogger.LogDebug($"[FiresVoxel] near-first last 10 s: {s}{VoxelZoneStreamer.TakeWrapPickStats()}(0.2.1344)");
            string held = TakeHeldRemeshStats();   // 0.2.1353: always taken too
            if (held.Length > 0 && FiresLogger.DebugEnabled) FiresLogger.LogDebug($"[FiresVoxel] loading last 10 s: {held}(0.2.1353)");
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
            else if (FatDiag.Terraform) FiresLogger.LogDebug($"[FiresVoxel] terraform: zone ({zone.x},{zone.y}) rebuilt with no chunk to remesh " +
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
                if (FatLogGate.Allow("tf-visible", out _))
                if (FatDiag.Terraform) FiresLogger.LogDebug($"[FiresVoxel] terraform: zone ({w.Zone.x},{w.Zone.y}) visible {(now - w.EditedAt) * 1000f:0} ms after its heightmap changed " +
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
            s_generateQueue.Enqueue(chunk, chunk);
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
                // slivers along contour lines all over mid-land. Frontier = the ZONE beyond the face isn't
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
                    if (FatNativeTrace.On) FatNativeTrace.Step("bake begin", bake.MeshId, bake.Chunk != null ? bake.Chunk.Key.ToString() : null);
                    try { Physics.BakeMesh(bake.MeshId, false); }
                    catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] BakeMesh threw: {ex.Message}"); }
                    FatNativeTrace.Step("bake end", bake.MeshId);
                    sw.Stop();
                    if (FiresLogger.DebugEnabled) Interlocked.Add(ref s_proofBakeTicks, sw.ElapsedTicks);   // 0.2.1350 (proof)
                    s_assignQueue.Enqueue(new AssignJob
                    {
                        View = bake.View, Chunk = bake.Chunk, Version = bake.Version, MeshId = bake.MeshId,
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
                    // headway (flying fast must not outrun the mesher).
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
                        if (!chunk.Queued)
                        {
                            chunk.Queued = true;
                            // 0.2.1353 (Fire: "get this terrain loading thing fixed"): starving must not reset the chunk's age
                            if (FiresVoxelConfig.StarveKeepsAge != null && FiresVoxelConfig.StarveKeepsAge.Value)
                            {
                                if (s_remeshQueue.EnqueueKeepAge(chunk, chunk)) Interlocked.Increment(ref s_starveAgeKept);
                            }
                            else s_remeshQueue.Enqueue(chunk, chunk);
                        }
                        s_signal.WaitOne(10);
                        continue;
                    }
                    chunk.PickTicks = 0L;   // 0.2.1353: it has its buffer; these stamps are spent
                    // Contain any per-chunk throw so one bad chunk can't kill the
                    // worker (and all meshing until relog).
                    try
                    {
                        sw.Restart();
                        bool proofOn = FiresLogger.DebugEnabled;   // 0.2.1350 (proof: picks and the generate / mesh split of this job)
                        if (proofOn) Interlocked.Increment(ref chunk.PendingGenerate ? ref s_proofPicksGen : ref s_proofPicksPlain);
                        chunk.RunPendingGenerate();   // W1 streamed chunks: fill + cave-carve first (no-op otherwise)
                        long proofGenTicks = proofOn ? sw.ElapsedTicks : 0L;
                        if (proofOn) Interlocked.Add(ref s_proofGenTicks, proofGenTicks);
                        int version =chunk.Snapshot(buf.Density, buf.Seed, buf.Biome, buf.WaterLvl, out buf.HasWater, buf.Ground);
                        if (version == VoxelChunk.SnapshotUniform) { ReturnBufferCounted(buf); continue; }
                        // FIRES: per-vertex inland waterline for the wet-shore band. Seed from the reconciled
                        // inland surface (buf.WaterLvl > sea+0.25), then BOUNDED-dilate a few cells so the band
                        // rides onto the dry bank. Baked into UV1.x by the mesher → shader fWL, per-vertex (no
                        // per-column 64m rectangles). Bounded on purpose: flooding would re-wet low ground far
                        // from water (only the shader's ±0.3m height gate limits the band above the line).
                        {
                            var wl = buf.WaterLvl; var wet = buf.WetLine;
                            // The shore sand/wet band, and the paint edge next to it, ran in 90/45-degree steps:
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
                            // 0.2.1035 (HaloFlat: Fire fell through a ring wall whose density probed SOLID): the band above only
                            // sees crossings ALONG Y. A vertical wall - the ring's inside face, a sheer cliff a brush cut - has
                            // none in its columns (solid all the way up beside air all the way up), so the band came out empty
                            // or ended at the ground and the wall was never meshed: no render, no collider. Crossings between
                            // horizontal neighbours widen the band too.
                            for (int y = -1; y <= VoxelChunk.CELLS; y++)
                            {
                                if (y >= bLo && y <= bHi) continue;
                                bool crosses = false;
                                for (int z = -1; z <= VoxelChunk.CELLS && !crosses; z++)
                                    for (int x = -1; x <= VoxelChunk.CELLS; x++)
                                    {
                                        int i = (x + 1) + SS * ((y + 1) + SS * (z + 1));
                                        bool cur = dd[i] > 0;
                                        if ((x < VoxelChunk.CELLS && (dd[i + 1] > 0) != cur) || (z < VoxelChunk.CELLS && (dd[i + SS * SS] > 0) != cur)) { crosses = true; break; }
                                    }
                                if (!crosses) continue;
                                if (y - 1 < bLo) bLo = y - 1;
                                if (y + 1 > bHi) bHi = y + 1;
                            }
                            if (bLo < -1) bLo = -1;
                            if (bHi > VoxelChunk.CELLS) bHi = VoxelChunk.CELLS;
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
                        if (proofOn) Interlocked.Add(ref s_proofMeshTicks, sw.ElapsedTicks - proofGenTicks);   // 0.2.1350 (proof)
                        chunk.MeshDoneTicks = Stopwatch.GetTimestamp();   // 0.2.1346
                        var built = new ApplyResult
                        {
                            Chunk = chunk, Buffers = buf, Version = version,
                            MeshMs = (float)sw.Elapsed.TotalMilliseconds,
                        };
                        // 0.2.1344: a hash of the density this mesh came from, the data every placed-water ground read answers from (SampleColumnSurface);
                        // a remesh with the same bytes (paint, snow G, refine, a border mirror that bumped Version only) moved no ground.
                        if (VoxelWaterSheetMesh.GroundSigOn || FatClutterRegrow.GroundChangeOn) { built.GroundSig = VoxelWaterSheetMesh.DensityHash(buf.Density); built.HasGroundSig = true; }
                        if (s_urgentKeys.ContainsKey(chunk.Key)) s_urgentApply.Enqueue(built); else s_applyQueue.Enqueue(built, chunk);   // 0.2.1344
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
                            s_remeshQueue.Enqueue(chunk, chunk);
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
                            s_generateQueue.Enqueue(dataChunk, dataChunk);
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
        // ── 0.2.1163 DRIVER FRAME BUDGET ───────────────────────────────────────────────────────────────────────────────
        // One time budget for every main-thread stage of VoxelPipelineDriver.Update (4 ms; 8 ms while far behind; 16 ms
        // before the player exists - a loading screen). A stage that alone exceeds it still runs once (one apply / one
        // assign a frame minimum); everything after waits for the next frame. Debug every 10 s: the worst frame's breakdown.
        internal static class Budget
        {
            internal enum S { Flush, Saves, Paint, Trail, Destroy, Apply, Collider, Other, Count }
            private const float BaseMs = 4f, BehindMs = 8f, LoadingMs = 16f;
            private const int BehindUnits = 64;
            private static long s_frame0, s_mark;
            private static float s_budgetMs = BaseMs;
            private static readonly double[] s_cur = new double[(int)S.Count], s_worst = new double[(int)S.Count];
            private static double s_worstMs;
            private static int s_over, s_frames, s_lastBacklog;
            private static long s_deferredSum;
            private static float s_logAt;

            internal static void Begin()
            {
                s_frame0 = s_mark = Stopwatch.GetTimestamp();
                for (int i = 0; i < s_cur.Length; i++) s_cur[i] = 0.0;
                s_budgetMs = Player.m_localPlayer == null ? LoadingMs : s_lastBacklog > BehindUnits ? BehindMs : BaseMs;
            }

            internal static void Mark(S stage)
            {
                long now = Stopwatch.GetTimestamp();
                s_cur[(int)stage] += (now - s_mark) * 1000.0 / Stopwatch.Frequency;
                s_mark = now;
            }

            internal static float LeftMs => s_budgetMs - (float)((Stopwatch.GetTimestamp() - s_frame0) * 1000.0 / Stopwatch.Frequency);
            internal static bool SpentAny => LeftMs < s_budgetMs - 0.5f;

            internal static void End(int backlog)
            {
                double ms = (Stopwatch.GetTimestamp() - s_frame0) * 1000.0 / Stopwatch.Frequency;
                s_frames++;
                if (ms > s_budgetMs) { s_over++; s_deferredSum += backlog; }
                s_lastBacklog = backlog;
                if (ms > s_worstMs) { s_worstMs = ms; System.Array.Copy(s_cur, s_worst, s_cur.Length); }
                if (ms > 50.0 && FiresLogger.DebugEnabled)
                    FiresLogger.LogDebug($"[FiresVoxelPipe] driver frame {ms:0} ms over the {s_budgetMs:0} ms budget: {Breakdown(s_cur)}; backlog {backlog} unit(s)");
                float t = Time.realtimeSinceStartup;
                if (t < s_logAt) return;
                s_logAt = t + 10f;
                if (FiresLogger.DebugEnabled && s_frames > 0)
                    FiresLogger.LogDebug($"[FiresVoxelPipe] frame budget {BaseMs:0} ms ({BehindMs:0} behind, {LoadingMs:0} loading): worst frame {s_worstMs:0.0} ms "
                        + $"(stage breakdown: {Breakdown(s_worst)}), frames over budget {s_over}/{s_frames}, deferred units {(s_over > 0 ? s_deferredSum / s_over : 0)} avg backlog when over (0.2.1163)");
                s_worstMs = 0.0; s_over = s_frames = 0; s_deferredSum = 0;
            }

            private static string Breakdown(double[] a) =>
                $"apply {a[(int)S.Apply]:0.0}, collider {a[(int)S.Collider]:0.0}, paint compose+upload {a[(int)S.Paint]:0.0}, remesh flush {a[(int)S.Flush]:0.0}, "
                + $"saves {a[(int)S.Saves]:0.0}, trail {a[(int)S.Trail]:0.0}, view destroys {a[(int)S.Destroy]:0.0}, other {a[(int)S.Other]:0.0}";
        }

        private sealed class VoxelPipelineDriver : MonoBehaviour
        {
            private readonly Stopwatch _sw = new Stopwatch();

            private const float PaintDrainBudgetMs = 2f;
            private const float ApplyBudgetMs = 3f;
            private const int TimedSlotHardCap = 64;   // 0.2.1346: time ends the loop; this only bounds a run of zero-cost skips
            private static bool s_timedSlotsProof;
            private static readonly FatSpreadStat s_timedSlotStat = new FatSpreadStat("mesh apply by time");
            private const double SlowApplyMs = 50.0;   // 0.2.1418 hitch proof threshold

            // 0.2.1157: the trail groups whose every member is built land now, all in this frame (at most TrailGroupsPerFrame
            // groups a frame - each counts as one unit, never split); a group still waiting after TrailGroupTimeout lands what it has.
            private int ApplyTrailGroups()
            {
                if (s_trailGroups.Count == 0) return 0;
                if (s_tgFrame != Time.frameCount) { s_tgFrame = Time.frameCount; s_tgThisFrame = 0; }
                long t0 = Stopwatch.GetTimestamp();
                int applied = 0, groups = 0;
                float now = Time.realtimeSinceStartup;
                for (int gi = 0; gi < s_trailGroups.Count; gi++)
                {
                    var g = s_trailGroups[gi];
                    // a member that left the world (dropped / not Full) is not waited for
                    if (g.Pending.Count > 0) g.Pending.RemoveWhere(k => !VoxelWorld.TryGetChunk(k, out var c) || c == null || c.Kind != ChunkKind.Full);
                    bool timedOut = g.Pending.Count > 0 && now - g.T0 > TrailGroupTimeout;
                    if (g.Pending.Count > 0 && !timedOut) continue;
                    if (s_tgThisFrame >= TrailGroupsPerFrame) { s_tgCapped++; continue; }   // next frame, still whole
                    // 0.2.1197: a member still on the refine worker holds the group (whole, one frame); at the timeout it is detached
                    // and lands alone when the worker hands it back. Every member's refinement starts in THIS frame, from one frame's
                    // paint and column set, so two members sharing an edge split it identically (as when all refined at landing).
                    bool refining = false;
                    if (!timedOut)
                        foreach (var m in g.Ready)
                            if (m.Stage == 0 && TryStartRefine(m)) refining = true;
                    for (int ri = g.Ready.Count - 1; ri >= 0; ri--)
                    {
                        var m = g.Ready[ri];
                        lock (m)
                        {
                            if (m.Stage != 1) continue;
                            if (!timedOut) { refining = true; break; }
                            m.InGroup = false; g.Ready.RemoveAt(ri); VoxelGTessellation.OffGroupDetached++;
                        }
                    }
                    if (refining) { VoxelGTessellation.OffGroupHeld++; continue; }
                    bool busy = false;
                    foreach (var r in g.Ready)
                        if (VoxelWorld.IsLive(r.Chunk) && VoxelWorld.TryGetView(r.Chunk.Key, out var v) && v != null && v.BakeInFlight) { busy = true; break; }
                    if (busy && !timedOut) continue;   // a collider bake is reading one member's mesh: the whole group waits a frame
                    int frames = Time.frameCount - g.Frame0;
                    foreach (var r in g.Ready)
                    {
                        if (ApplyOne(r) == 1) applied++;
                    }
                    foreach (var k in g.Pending) s_urgentKeys.TryRemove(k, out _);   // timed-out stragglers apply alone when they arrive
                    foreach (var k in g.Members) if (s_trailGroupOf.TryGetValue(k, out var og) && og == g) s_trailGroupOf.Remove(k);
                    s_trailGroups.RemoveAt(gi--);
                    groups++; s_tgThisFrame++;
                    s_tgDone++;
                    if (g.Size < s_tgMinSize) s_tgMinSize = g.Size;
                    if (g.Size > s_tgMaxSize) s_tgMaxSize = g.Size;
                    if (timedOut) s_tgTimedOut++;
                    // 0.2.1159 (Fire: "you fix, I check" - proof lines in his log)
                    if (!s_tgSwapSaid && !timedOut && g.Size > 1)
                    {
                        s_tgSwapSaid = true;
                        FiresLogger.LogDebug($"[TrailSeam] first atomic border group swapped: {g.Size} chunk(s), {applied} applied, waited {frames} frame(s) - all members swapped in one frame: no seam crack (0.2.1157)");
                    }
                    if (!s_tgPartialSaid && (timedOut || busy))
                    {
                        s_tgPartialSaid = true;
                        FiresLogger.LogDebug($"[TrailSeam] trail group landed PARTIAL (once per session): {g.Size} chunk(s), {g.Pending.Count} unbuilt after {(now - g.T0) * 1000f:0} ms (timeout {TrailGroupTimeout * 1000f:0} ms){(busy ? ", a collider bake was reading a member" : "")} - the unbuilt member(s) land alone (a short seam crack is possible there)");
                    }
                    if (frames > 2)
                    {
                        s_tgWaited++;
                        if (FiresLogger.DebugEnabled && now >= s_tgLogAt)
                        {
                            s_tgLogAt = now + 2f;
                            FiresLogger.LogDebug($"[TrailPredict] trail group of {g.Size} chunk(s) waited {frames} frame(s) ({(now - g.T0) * 1000f:0} ms) before it landed{(timedOut ? $"; TIMED OUT with {g.Pending.Count} member(s) unbuilt (they land alone)" : "")}");
                        }
                    }
                    try { g.OnApplied?.Invoke(Time.frameCount); } catch { }
                }
                if (applied > 0) FatTrailFast.AddApplyCost(t0);
                return applied;
            }

            // One built mesh onto its chunk (the apply loop's body, 0.2.1157: shared with the trail groups).
            // 0 = skipped (dropped / not Full), 1 = applied, 2 = its view's collider bake is busy (re-queued; stop for this frame).
            private int ApplyOne(ApplyResult r)
            {
                if (!VoxelWorld.IsLive(r.Chunk))
                {
                    ReturnBufferCounted(r.Buffers);   // world cleared / chunk dropped
                    return 0;
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
                    return 0;
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
                long a0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1204: apply sub-timers (ApplySplit)
                int nVerts = r.Buffers != null && r.Buffers.Verts != null ? r.Buffers.Verts.Count : -1;
                var view = VoxelWorld.GetOrCreateView(r.Chunk);
                long a1 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (view.BakeInFlight)
                {
                    // 0.2.1344: nearest-first would hand this same result back next frame and stop every other apply until the bake ends;
                    // it sits out this frame instead and the rest keep landing. Key off: the 1343 re-queue + stop.
                    if (VoxelNearOrder.On) { s_applyRetry.Add(r); return 0; }
                    s_applyQueue.Enqueue(r, r.Chunk);       // PhysX is reading this mesh - retry next frame
                    return 2;
                }

                _sw.Restart();
                var spk = FatSpike.Start();
                if (FatNativeTrace.On) FatNativeTrace.Step("apply begin", 0, r.Chunk.Key + " v" + r.Version + " verts " + r.Buffers.Verts.Count);
                int meshId = view.ApplyBuffers(r.Buffers, r.Stage == 2, r.PreVerts, r.PreTris);   // 0.2.1197: Stage 2 = refined on the worker
                if (FatNativeTrace.On) FatNativeTrace.Step("apply end", meshId, r.Chunk.Key.ToString());
                FatSpike.Add(FatSpike.Stage.MeshApply, spk);
                _sw.Stop();
                long a2 = System.Diagnostics.Stopwatch.GetTimestamp();
                // ReturnBuffer, NOT a raw pool add: the raw add put the buffer
                // back but never called s_signal.Set(), so a worker blocked in
                // RentBuffer's WaitOne(10) was not woken by the very event that
                // freed its buffer. It sat out the full timeout and retried -
                // once per freed buffer, ~4/frame. That is what drove
                // starved=40696 with the queues sitting empty.
                ReturnBufferCounted(r.Buffers);
                NoteApplied(r.Chunk.Key);
                VoxelZoneStreamer.NoteStageApply(r.Chunk.Key, r.MeshMs);   // 0.2.1346
                if (CarveTiming.On) CarveTiming.MeshShown(r.Chunk.Key);
                if (view != null) view.NoteMeshGround(r.HasGroundSig, r.GroundSig);   // 0.2.1347: the grass reads this at the collider assign
                VoxelWaterSheetMesh.NoteChunkMeshed(r.Chunk, r.Version, r.HasGroundSig, r.GroundSig);   // 0.2.1344: + the ground signature; 0.2.1165: voxel data changes only; placed water rebuilds over a carved / filled chunk
                r.Chunk.AppliedVersion = r.Version;   // sweep's stale-mesh test
                if (stale) EnqueueSeamRefresh(r.Chunk);   // the coalescing rule: see EnqueueSeamRefresh
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
                ApplySplit.Note(r.Chunk.Key, nVerts, a0, a1, a2, System.Diagnostics.Stopwatch.GetTimestamp());

                if (meshId != 0)
                {
                    view.NoteBakeStarted(meshId);
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
                return 1;
            }

            private void Update()
            {
                FatSpike.FrameStart();     // report the last frame if it was a spike
                // 0.2.1163 (the lead, FDT stall ledger: "193 ms FiresAdminTerrain: VoxelPipelineDriver.Update x1" at a zone border):
                // ONE per-frame time budget across every main-thread stage here; each stage is timed and the 10 s line names the
                // worst frame's breakdown. The collider ASSIGN loop had no time check at all and runs ApplyPerFrame per frame, which
                // the spawn gate multiplies x16 (the "cap 96/frame" in the applies line).
                Budget.Begin();
                RefreshBufferBudget();     // config hot-reload (main thread only)
                VoxelNearOrder.Publish();  // 0.2.1344: the position the workers' nearest picks measure against
                NearOrderReport();         // 0.2.1344
                VerdantsAscent.Water.Core.WaterMeshCombiner.PumpMopRefresh();   // 0.2.1344: queued mop-mask refreshes
                { var t0 = FatSpike.Start(); FlushDeferred(); DrainHeldRemeshes(); FatSpike.Add(FatSpike.Stage.RemeshFlush, t0); }   // coalesced re-queues whose window has passed; 0.2.1353: the remeshes a swap released
                Budget.Mark(Budget.S.Flush);
                VoxelWorld.FlushSaves();   // P1 - debounced anchor persistence
                CarveTiming.Tick();
                Budget.Mark(Budget.S.Saves);

                // The driver exists whenever chunks do (brush chunks in
                // Vanilla world mode included); the streamer tick only runs in
                // voxel modes, so the paint drain lives here.
                // Every frame, at most PaintDrainBudgetMs, resumable mid-column (4 whole columns every
                // 0.25 s in one frame cost ~91 ms median in fly-through hitches).
                VoxelWorld.DrainPaintDirtyTimed(Mathf.Min(PaintDrainBudgetMs, Mathf.Max(0.25f, Budget.LeftMs)));
                Budget.Mark(Budget.S.Paint);
                FatTrailFast.Tick();   // 0.2.1157: predicted-trail reconcile + the [TrailPredict] / [TrailLatency] lines
                VoxelGTessellation.DrainCommits();   // 0.2.1197: the refine worker's tallies + log lines, on the main thread
                Budget.Mark(Budget.S.Trail);
                VoxelWorld.DrainViewDestroys(VoxelWorld.ViewDestroysPerFrame);   // unwrapped zones' views, a few a frame
                Budget.Mark(Budget.S.Destroy);

                // Mesh-truth seam sentinel - silent unless a face diverges.
                VoxelSeamAudit.AutoTick();
                PipeReport();
                Budget.Mark(Budget.S.Other);

                // Apply built meshes (<=2/frame).
                var swApply = System.Diagnostics.Stopwatch.StartNew();
                int appliedThisFrame = 0;
                // 0.2.1157 (Fire, round 36: "sometimes when we walk the seams seperate and it takes a few frames for them to re seam"):
                // a trail stamp's chunks land together, in one frame, outside the per-frame cap (a group counts as one unit).
                long g0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1204
                int groupApplied = Budget.LeftMs > 0f ? ApplyTrailGroups() : 0;
                double groupMs = (System.Diagnostics.Stopwatch.GetTimestamp() - g0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;   // 0.2.1418 hitch proof
                ApplySplit.NoteStage(0, g0);   // 0.2.1163: a group is one unit inside the frame budget
                // An edited zone's meshes go first and on top of the per-frame cap.
                int applyCap = ApplyPerFrame + Mathf.Min(s_urgentApply.Count, UrgentAppliesPerFrame);
                // 0.2.1346 (plan B: 1 ms applies, 3 ms a frame: at 5 fps about 17/s with 226 waiting; slow frames throttled the drain and slowed the frames behind it):
                // slots by time. A slow frame gets the same SHARE of its time (3 ms of 16.7), and the driver's budget still ends the loop. Off: the count and the fixed 3 ms.
                bool timedSlots = FiresVoxelConfig.ApplySlotsTimed != null && FiresVoxelConfig.ApplySlotsTimed.Value;
                double applyBudgetMs = ApplyBudgetMs;
                if (timedSlots)
                {
                    applyCap = Mathf.Max(applyCap, TimedSlotHardCap);
                    applyBudgetMs = System.Math.Max(ApplyBudgetMs, ApplyBudgetMs * Time.unscaledDeltaTime / (1f / 60f));
                    if (!s_timedSlotsProof) { s_timedSlotsProof = true; FiresLogger.LogInfo("[FiresVoxel] mesh apply slots run by time inside the driver budget, not a count per frame (0.2.1346)"); }
                }
                // Also stop once ApplyBudgetMs is spent (at least one apply a frame, so it never stalls); the
                // count cap alone let a SpawnGate x16 burst upload dozens of meshes in one frame.
                // 0.2.1163: and the shared frame budget (a single unit that alone exceeds it still lands: one apply a frame minimum)
                for (int n = 0; n < applyCap && (appliedThisFrame == 0 ? Budget.LeftMs > 0f || !Budget.SpentAny : swApply.Elapsed.TotalMilliseconds < applyBudgetMs && Budget.LeftMs > 0f)
                    && (s_urgentApply.TryDequeue(out var r) || s_applyQueue.TryDequeue(out r));)
                {
                    if (CaptureTrailMember(r)) continue;   // held until its whole group is built
                    appliedThisFrame++;
                    long rf0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1204
                    bool refining = TryStartRefine(r);
                    ApplySplit.NoteStage(1, rf0);
                    if (refining) { n++; continue; }   // 0.2.1197: layer paint now, refinement on the worker; it comes back as Stage 2
                    long ap0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    int apVerts = r.Buffers != null && r.Buffers.Verts != null ? r.Buffers.Verts.Count : -1;
                    int res = ApplyOne(r);
                    double apMs = (System.Diagnostics.Stopwatch.GetTimestamp() - ap0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (apMs > SlowApplyMs && FiresLogger.DebugEnabled)   // 0.2.1418 ([lead]: "worst 337 ms in one frame"): which mesh makes the slow frame
                        FiresLogger.LogDebug($"[FatSpread] slow mesh apply: chunk {r.Chunk.Key} (zone {ZoneSystem.GetZone(r.Chunk.WorldOrigin + new Vector3(16f, 0f, 16f))}), {apVerts} verts, stage {r.Stage}, {apMs:0.0} ms (result {res}; frame {Time.frameCount})");
                    if (res == 2) break;
                    if (res == 0) continue;
                    n++;
                }
                if (Budget.LeftMs > 0f) groupApplied += ApplyTrailGroups();   // a group the loop above completed lands this same frame
                for (int ri = 0; ri < s_applyRetry.Count; ri++) s_applyQueue.Enqueue(s_applyRetry[ri], s_applyRetry[ri].Chunk);   // 0.2.1344
                s_applyRetry.Clear();
                Budget.Mark(Budget.S.Apply);
                if (timedSlots && appliedThisFrame > 0) s_timedSlotStat.Note(swApply.Elapsed.TotalMilliseconds);   // 0.2.1346
                if (swApply.Elapsed.TotalMilliseconds > SlowApplyMs && FiresLogger.DebugEnabled)   // 0.2.1418: the slow frame's split
                    FiresLogger.LogDebug($"[FatSpread] slow apply frame {Time.frameCount}: {swApply.Elapsed.TotalMilliseconds:0.0} ms total = trail groups {groupApplied} in {groupMs:0.0} ms + {appliedThisFrame} single apply(s); queue {s_applyQueue.Count}, urgent {s_urgentApply.Count}, frame dt {Time.unscaledDeltaTime * 1000f:0} ms");
                appliedThisFrame += groupApplied;

                if (appliedThisFrame > 0)
                {
                    Interlocked.Add(ref s_pipeApplied, appliedThisFrame);
                    lock (s_bufferPool) s_pipeMsApply += swApply.Elapsed.TotalMilliseconds;
                }

                BodyHold.Tick();
                Budget.Mark(Budget.S.Other);   // 0.2.1054: bodies over a chunk with a surface but no collider yet stay put until it is live
                if (FiresLogger.DebugEnabled && Time.realtimeSinceStartup >= s_nextSwapLine && VoxelChunkView.CollidersSwapped > 0)
                {
                    s_nextSwapLine = Time.realtimeSinceStartup + 30f;
                    FiresLogger.LogDebug($"[FiresVoxel] collider swap: {VoxelChunkView.CollidersSwapped} chunks swapped (staging), max wait {VoxelChunkView.MaxSwapWaitMs:0} ms");
                    VoxelChunkView.CollidersSwapped = 0; VoxelChunkView.MaxSwapWaitMs = 0f;
                }

                // Assign baked colliders (<=2/frame).
                var swAssign = System.Diagnostics.Stopwatch.StartNew();
                int assignedThisFrame = 0;
                // 0.2.1163: inside the frame budget (it had none: up to ApplyPerFrame = 96 under the spawn gate in one frame); at least
                // one assign a frame so colliders never stall.
                for (int n = 0; n < (timedSlots ? TimedSlotHardCap : ApplyPerFrame) && (n == 0 || Budget.LeftMs > 0f) && s_assignQueue.TryDequeue(out var a); n++)
                {
                    assignedThisFrame++;
                    // A view destroyed while its mesh was cooking left the mesh here; the bake is back, so it can go now
                    if (s_destroyAfterBake.TryGetValue(a.MeshId, out var orphan)) { s_destroyAfterBake.Remove(a.MeshId); if (orphan != null) { FatNativeTrace.Step("orphan mesh destroy", a.MeshId); Object.Destroy(orphan); } }
                    if (a.View == null) continue;
                    a.View.NoteBakeDone();
                    // Match the VIEW's applied mesh (AppliedVersion), not the
                    // chunk's data Version - a border-mirror bumps Version without
                    // re-applying the mesh, and gating on it left the view with a
                    // mesh but no collider (colliderNotReady) forever. If this bake
                    // is for the mesh the view is actually showing, assign it; a
                    // later re-mesh carries its own bake.
                    if (!VoxelWorld.IsLive(a.Chunk) || a.Version != a.Chunk.AppliedVersion) continue;
                    if (FatNativeTrace.On) FatNativeTrace.Step("assign begin", a.MeshId, a.Chunk.Key.ToString());
                    a.View.AssignCollider();
                    VoxelZoneStreamer.NoteStageCollider(a.Chunk.Key);   // 0.2.1346
                    if (CarveTiming.On) CarveTiming.ColliderOn(a.Chunk.Key);
                    FatNativeTrace.Step("assign end", a.MeshId);

                    // (per-chunk mesh/bake timing line retired — see the
                    // [FiresVoxelPipe] ledger; this printed once per chunk and
                    // dominated the log during streaming)
                }
                if (assignedThisFrame > 0)
                {
                    Interlocked.Add(ref s_pipeAssigned, assignedThisFrame);
                    lock (s_bufferPool) s_pipeMsAssign += swAssign.Elapsed.TotalMilliseconds;
                }
                Budget.Mark(Budget.S.Collider);
                Budget.End(s_applyQueue.Count + s_urgentApply.Count + s_assignQueue.Count);
            }

            private void OnDestroy()
            {
                // World unload - drop everything; the worker thread survives
                // idle (background) and the queues drain to stale-drops.
                VoxelWorld.Clear();
                s_heldRemesh.Clear(); s_heldRemeshGaveUp.Clear(); s_heldRemeshRun.Clear();   // 0.2.1353
                s_driverGO = null;
            }
        }

        // ── CARVE TIMING (Debug only) ───────────────────────────────────────────────────────────────────────────────
        // Fire 10-23: "the edits with the voxel carve tools take forever to apply". One line per V-tool stroke (after the last of
        // its chunks has its collider, or 20 s): dab -> mesh visible and dab -> collider (avg / max ms), how many of its re-queues
        // the coalescing rule DEFERRED and for how long, the main-thread cost of the dabs, and the queues when it ended.
        // Times run from the FIRST dab still waiting on that chunk, so a deferred chunk shows the whole wait the player sees.
        internal static class CarveTiming
        {
            private const float StrokeQuietSeconds = 1f, GiveUpSeconds = 20f;
            private sealed class Pending { public float DabAt, MeshAt = -1f, DeferredAt = -1f; public int Defers; }
            private static readonly Dictionary<Vector3Int, Pending> s_pending = new Dictionary<Vector3Int, Pending>();
            private static float s_strokeStart = -1f, s_lastDab;
            private static int s_dabs, s_chunkDabs, s_meshes, s_colliders, s_defers, s_seeded;
            private static double s_meshSum, s_meshMax, s_colSum, s_colMax, s_deferSum, s_deferMax, s_brushMs, s_brushMax;

            internal static bool On => FiresLogger.DebugEnabled;

            // VoxelWorld.ApplyBrush, main thread: a chunk this dab changed is about to be queued.
            internal static void Dab(Vector3Int key)
            {
                float now = Now();
                lock (s_pending)
                {
                    if (s_strokeStart < 0f) s_strokeStart = now;
                    s_lastDab = now;
                    s_chunkDabs++;
                    if (!s_pending.ContainsKey(key)) s_pending[key] = new Pending { DabAt = now };
                }
            }

            // End of VoxelWorld.ApplyBrush: its main-thread cost.
            internal static void BrushDone(double ms, int seeded)
            {
                lock (s_pending) { s_dabs++; s_seeded += seeded; s_brushMs += ms; if (ms > s_brushMax) s_brushMax = ms; }
            }

            // Defer() returned true for this chunk.
            internal static void Deferred(Vector3Int key)
            {
                lock (s_pending)
                    if (s_pending.TryGetValue(key, out var p)) { p.Defers++; s_defers++; if (p.DeferredAt < 0f) p.DeferredAt = Now(); }
            }

            // FlushDeferred released it.
            internal static void Released(Vector3Int key)
            {
                lock (s_pending)
                    if (s_pending.TryGetValue(key, out var p) && p.DeferredAt >= 0f)
                    {
                        double w = (Now() - p.DeferredAt) * 1000.0;
                        s_deferSum += w; if (w > s_deferMax) s_deferMax = w;
                        p.DeferredAt = -1f;
                    }
            }

            internal static void MeshShown(Vector3Int key)
            {
                lock (s_pending)
                    if (s_pending.TryGetValue(key, out var p) && p.MeshAt < 0f)
                    {
                        p.MeshAt = Now();
                        double ms = (p.MeshAt - p.DabAt) * 1000.0;
                        s_meshes++; s_meshSum += ms; if (ms > s_meshMax) s_meshMax = ms;
                    }
            }

            internal static void ColliderOn(Vector3Int key)
            {
                lock (s_pending)
                    if (s_pending.TryGetValue(key, out var p) && p.MeshAt >= 0f)
                    {
                        double ms = (Now() - p.DabAt) * 1000.0;
                        s_colliders++; s_colSum += ms; if (ms > s_colMax) s_colMax = ms;
                        s_pending.Remove(key);   // a later dab on this chunk starts a new wait
                    }
            }

            // Driver Update, main thread.
            internal static void Tick()
            {
                if (s_strokeStart < 0f) return;
                float now = Now();
                string line;
                lock (s_pending)
                {
                    bool quiet = now - s_lastDab > StrokeQuietSeconds;
                    if (!quiet || (s_pending.Count > 0 && now - s_lastDab < GiveUpSeconds)) return;
                    line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[FiresVoxel] carve timing: stroke {0:0.0} s, {1} dab(s) ({2} chunk edit(s), {3} chunk(s) seeded), main thread {4:0.0} ms/dab (max {5:0.0}) | "
                        + "dab -> mesh visible avg {6:0} ms max {7:0} ({8} mesh(es)) | dab -> collider avg {9:0} ms max {10:0} ({11}) | "
                        + "coalescing deferred {12} re-queue(s), waited avg {13:0} ms max {14:0} | {15} chunk(s) still waiting | queues: urgent {16}, normal {17}, apply {18}, assign {19}",
                        s_lastDab - s_strokeStart, s_dabs, s_chunkDabs, s_seeded, s_dabs > 0 ? s_brushMs / s_dabs : 0.0, s_brushMax,
                        s_meshes > 0 ? s_meshSum / s_meshes : 0.0, s_meshMax, s_meshes, s_colliders > 0 ? s_colSum / s_colliders : 0.0, s_colMax, s_colliders,
                        s_defers, s_defers > 0 ? s_deferSum / s_defers : 0.0, s_deferMax, s_pending.Count,
                        s_urgentRemesh.Count, s_remeshQueue.Count, s_applyQueue.Count, s_assignQueue.Count);
                    s_pending.Clear();
                    s_strokeStart = -1f;
                    s_dabs = s_chunkDabs = s_meshes = s_colliders = s_defers = s_seeded = 0;
                    s_meshSum = s_meshMax = s_colSum = s_colMax = s_deferSum = s_deferMax = s_brushMs = s_brushMax = 0.0;
                }
                FiresLogger.LogDebug(line);
            }
        }
    }
}

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1204 (lead, fly56 outer circle 900 m: the driver's worst zone-load frames were "apply 82.7 / 109.9 / 135.8" ms with one unit
    // landing, every other stage ~0): where one apply goes. When a single apply (or the trail-group / refine-start step) takes
    // >= 20 ms, one Debug line (at most every 5 s) splits it: view create, mesh upload (ApplyBuffers), the after-steps (water note,
    // orphan / stale neighbour pokes), with the chunk and its vertex count. The worst of the window is kept.
    internal static class ApplySplit
    {
        private const double SlowMs = 20.0;
        private static double s_logAt, s_worst;
        private static string s_worstLine;
        private static readonly double Ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        internal static void Note(object key, int verts, long a0, long a1, long a2, long a3)
        {
            double total = (a3 - a0) * Ms;
            if (total < SlowMs || !FiresLogger.DebugEnabled) return;
            if (total > s_worst) { s_worst = total; s_worstLine = $"chunk {key}, {verts} vert(s): {total:0.0} ms = view create {(a1 - a0) * Ms:0.0} + mesh upload (ApplyBuffers) {(a2 - a1) * Ms:0.0} + after-steps (water note, neighbour pokes) {(a3 - a2) * Ms:0.0}"; }
            Flush();
        }

        // 0 = trail groups, 1 = refine start (layer paint on the main thread)
        internal static void NoteStage(int which, long t0)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * Ms;
            if (ms < SlowMs || !FiresLogger.DebugEnabled) return;
            if (ms > s_worst) { s_worst = ms; s_worstLine = $"{(which == 0 ? "trail group apply (ApplyTrailGroups)" : "refine start (TryStartRefine: layer paint on the main thread)")} {ms:0.0} ms"; }
            Flush();
        }

        private static void Flush()
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (now < s_logAt) return;
            s_logAt = now + 5.0;
            FiresLogger.LogDebug($"[FiresVoxelPipe] slow apply: {s_worstLine} (worst of the last 5 s; 0.2.1204)");
            s_worst = 0; s_worstLine = null;
        }
    }
}
