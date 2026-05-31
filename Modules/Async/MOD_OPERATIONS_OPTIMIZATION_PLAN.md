# Mod Operations Optimization Plan — Buttery Smooth Heavy Operations

## Date: 2025-07-XX
## Status: PLANNING

---

## Problem Statement

Even with our existing async infrastructure (background workers, time-budgeted `MainThreadDispatcher`,
`DirtFloorDeferredInit`, coroutine-based mesh generation), players still see the disconnect icon flash
during heavy terrain loading. The root cause: **too many independent systems each doing "a little" main-thread
work that collectively exceeds the frame budget**.

### Current Architecture (Three Separate Systems)

```
????????????????????????????????????????????????????????????????????????????
?  Background Workers                                                     ?
?  (DirtFloorAsyncMeshBuilder, WaterAsyncMeshBuilder)                     ?
?  ? Pure math on dedicated threads                                       ?
?  ? Callbacks via MainThreadDispatcher (time-budgeted, 4ms)              ?
????????????????????????????????????????????????????????????????????????????
?  DirtFloorDeferredInit                                                   ?
?  ? Per-type rate limits (1 physics bake, 2 material setups, etc.)       ?
?  ? Time budget 8ms                                                       ?
?  ? Priority queue by distance                                            ?
????????????????????????????????????????????????????????????????????????????
?  Coroutines (RegenerateMeshAsyncCoroutine, etc.)                        ?
?  ? Yield per-phase, but each phase can be 5-20ms                        ?
?  ? Multiple coroutines run in the SAME frame (no global coordination)   ?
?  ? No shared budget with MainThreadDispatcher or DirtFloorDeferredInit  ?
????????????????????????????????????????????????????????????????????????????
?  Sync Paths (ForceRegenerate, RPC handlers, OnLevelGround, etc.)        ?
?  ? Block main thread entirely for 20-200ms                              ?
?  ? Called from RPC handlers, tool operations, disk add/merge            ?
????????????????????????????????????????????????????????????????????????????
```

**The problem:** These four systems don't share a frame budget. In a single frame:
- `MainThreadDispatcher` uses 4ms applying mesh callbacks
- `DirtFloorDeferredInit` uses 8ms baking collision + spawning grass
- Two coroutines each yield after 10ms of work
- An RPC handler fires `ForceRegenerate()` synchronously (50ms)

**Total: 82ms ? massive hitch ? disconnect icon**

---

## Solution: Unified `FrameBudgetScheduler`

A single, centralized MonoBehaviour that owns the **entire mod's per-frame time budget** and
coordinates ALL heavy work — background worker callbacks, deferred init, coroutine phases, and
converted sync operations — through one priority queue.

### Design Principles

1. **Single budget authority** — One `Stopwatch` per frame, one configurable budget (default 5ms).
   Everything checks the same clock.
2. **Priority lanes** — Work is categorized into priority tiers so critical operations
   (collision for terrain player is standing on) beat cosmetic work (grass spawning 100m away).
3. **Coroutine integration** — Heavy coroutines register their yielded steps as schedulable
   work items instead of running independently.
4. **Sync-to-async conversion** — `ForceRegenerate()` and RPC handlers queue work items
   instead of blocking the main thread.
5. **Simple consumer API** — Any system can schedule work with one call. The scheduler handles
   ordering, budgeting, and progress tracking.
6. **Backward compatible** — Existing `MainThreadDispatcher` and `DirtFloorDeferredInit` continue
   to work but route through the scheduler's budget.

---

## Architecture

```
???????????????????????????????????????????????????????????????????????????
?                       FrameBudgetScheduler                               ?
?                     (MonoBehaviour, singleton)                            ?
?                                                                          ?
?  Update() {                                                              ?
?    _frameSw.Restart();                                                   ?
?    while (budget remaining && work available) {                          ?
?      var item = Dequeue(highestPriority);                                ?
?      item.Execute();                // may be callback, coroutine step,  ?
?    }                                //   deferred init, or converted sync?
?  }                                                                       ?
?                                                                          ?
?  ?????????????  ??????????????  ??????????????  ????????????????????   ?
?  ? Critical  ?  ?   High     ?  ?  Normal    ?  ?    Low           ?   ?
?  ? (Lane 0)  ?  ?  (Lane 1)  ?  ?  (Lane 2)  ?  ?   (Lane 3)      ?   ?
?  ?           ?  ?            ?  ?            ?  ?                  ?   ?
?  ? Collision ?  ? Mesh apply ?  ? Material   ?  ? Grass spawn     ?   ?
?  ? for local ?  ? Background ?  ? setup      ?  ? Clutter spawn   ?   ?
?  ? player    ?  ? worker     ?  ? Paint mask ?  ? Cache file I/O  ?   ?
?  ? terrain   ?  ? callbacks  ?  ? Coroutine  ?  ? LOD transitions ?   ?
?  ?           ?  ? RPC apply  ?  ? phases     ?  ? Biome persist   ?   ?
?  ?????????????  ??????????????  ??????????????  ????????????????????   ?
?                                                                          ?
?  Budget: 5ms default (configurable)                                      ?
?  Guarantee: ?1 item/frame for forward progress                          ?
?  Distance boost: closer to player ? higher effective priority            ?
???????????????????????????????????????????????????????????????????????????
```

---

## Consumer API

### Core Scheduling

```csharp
namespace VerdantsAscent.Async
{
    /// <summary>
    /// Priority lanes for scheduled work. Lower value = higher priority.
    /// </summary>
    public enum WorkPriority
    {
        /// <summary>Collision baking for terrain the player is standing on.</summary>
        Critical = 0,
        /// <summary>Mesh apply from background workers, RPC terrain creation.</summary>
        High = 1,
        /// <summary>Material setup, paint masks, coroutine phases.</summary>
        Normal = 2,
        /// <summary>Grass, clutter, cache I/O, LOD, cosmetic work.</summary>
        Low = 3
    }

    /// <summary>
    /// Centralized per-frame time budget scheduler for all mod operations.
    /// 
    /// Replaces the fragmented budget approach where MainThreadDispatcher (4ms),
    /// DirtFloorDeferredInit (8ms), and multiple uncoordinated coroutines each
    /// consume frame time independently, causing collective budget overruns.
    /// 
    /// All heavy main-thread work routes through this scheduler so the mod never
    /// exceeds its frame budget regardless of how many systems are active.
    /// 
    /// Usage:
    ///   FrameBudgetScheduler.Schedule(WorkPriority.High, () => ApplyMeshResult(data));
    ///   FrameBudgetScheduler.Schedule(WorkPriority.Low, () => SpawnGrass(), combinerPosition);
    ///   
    ///   // For multi-step operations:
    ///   FrameBudgetScheduler.ScheduleSequence(WorkPriority.Normal, new Action[] {
    ///       () => SetupMaterial(),
    ///       () => UpdateCollisionMesh(),
    ///       () => SpawnGrassOnMesh()
    ///   });
    ///   
    ///   // For coroutine phases (replaces StartCoroutine for heavy work):
    ///   FrameBudgetScheduler.ScheduleCoroutine(WorkPriority.Normal, RegenerateMeshSteps());
    /// </summary>
    public class FrameBudgetScheduler : MonoBehaviour
    {
        /// <summary>
        /// Maximum milliseconds per frame for ALL scheduled mod work.
        /// Default 5ms — leaves 11ms of a 60fps frame for Valheim + rendering + network.
        /// </summary>
        public static float MaxBudgetMs = 5f;
        
        // --- Scheduling API (thread-safe) ---

        /// <summary>
        /// Schedules a single action for execution within the frame budget.
        /// Thread-safe — can be called from background threads.
        /// </summary>
        /// <param name="priority">Execution priority lane.</param>
        /// <param name="action">The work to execute on the main thread.</param>
        /// <param name="worldPosition">Optional position for distance-based priority boost.
        /// Pass null for position-independent work.</param>
        public static void Schedule(WorkPriority priority, Action action,
            Vector3? worldPosition = null);

        /// <summary>
        /// Schedules a sequence of actions that execute in order, one per frame budget slot.
        /// Each step is a separate work item — if the budget runs out mid-sequence,
        /// remaining steps execute in subsequent frames.
        /// </summary>
        public static void ScheduleSequence(WorkPriority priority, Action[] steps,
            Vector3? worldPosition = null);

        /// <summary>
        /// Schedules a coroutine-style IEnumerator whose steps execute within the budget.
        /// Each yield return (including null) becomes a scheduling boundary — the scheduler
        /// picks up the next step when budget is available.
        /// 
        /// This replaces StartCoroutine() for heavy operations, ensuring coroutine phases
        /// don't run uncoordinated outside the budget.
        /// </summary>
        /// <param name="priority">Execution priority lane.</param>
        /// <param name="steps">IEnumerator from a coroutine-style method.</param>
        /// <param name="worldPosition">Optional position for distance-based priority.</param>
        /// <param name="onComplete">Called when the entire sequence finishes.</param>
        public static void ScheduleCoroutine(WorkPriority priority, IEnumerator steps,
            Vector3? worldPosition = null, Action onComplete = null);

        /// <summary>
        /// Cancels all pending work associated with a specific object.
        /// Call this when a combiner/anchor is destroyed to prevent executing
        /// stale callbacks on destroyed GameObjects.
        /// </summary>
        /// <param name="owner">The object whose work should be cancelled.
        /// Matched by reference equality against the worldPosition source.</param>
        public static void CancelAll(UnityEngine.Object owner);
        
        // --- Diagnostics ---
        
        /// <summary>Number of work items waiting across all priority lanes.</summary>
        public static int PendingCount { get; }
        
        /// <summary>Milliseconds consumed by the scheduler in the last frame.</summary>
        public static float LastFrameMs { get; }
        
        /// <summary>True if the scheduler exceeded its budget last frame (for perf monitoring).</summary>
        public static bool BudgetExceededLastFrame { get; }
    }
}
```

### Integration Helpers

```csharp
namespace VerdantsAscent.Async
{
    /// <summary>
    /// Extension methods for easy scheduling from any system.
    /// </summary>
    public static class SchedulerExtensions
    {
        /// <summary>
        /// Converts a ForceRegenerate()-style sync operation into scheduled async steps.
        /// The combiner's mesh is built in phases across multiple frames.
        /// </summary>
        public static void ScheduleRegeneration(this DirtFloorMeshCombiner combiner);
        
        /// <summary>
        /// Schedules a background worker callback through the FrameBudgetScheduler
        /// instead of MainThreadDispatcher. The callback runs within the shared budget.
        /// </summary>
        public static void EnqueueScheduled(WorkPriority priority, Action action,
            Vector3? position = null);
    }
}
```

---

## Implementation Plan

### Phase 1: `FrameBudgetScheduler` Core (NEW FILE)

**File:** `Modules/Async/FrameBudgetScheduler.cs`

Create the scheduler MonoBehaviour with:
- Thread-safe `Schedule()` / `ScheduleSequence()` / `ScheduleCoroutine()` API
- Priority queue with 4 lanes (Critical, High, Normal, Low)
- Per-frame `Stopwatch` budgeting with ?1 item guarantee
- Distance-based priority boost (closer to player ? higher effective priority within lane)
- `CancelAll()` for cleanup on GameObject destruction
- Diagnostic properties for perf monitoring

**Estimated effort:** Medium  
**Risk:** Low — new file, no existing code changes

### Phase 2: Wire `MainThreadDispatcher` Through Scheduler

**File:** `Modules/Async/MainThreadDispatcher.cs` (modify)

Change `MainThreadDispatcher.Enqueue()` to route through `FrameBudgetScheduler.Schedule(WorkPriority.High, ...)` 
instead of maintaining its own queue and budget. The dispatcher becomes a thin forwarding layer
for backward compatibility.

```csharp
// MainThreadDispatcher.Enqueue() becomes:
public static void Enqueue(Action action)
{
    if (action == null) return;
    FrameBudgetScheduler.Schedule(WorkPriority.High, action);
}
```

Remove the internal queue, processing list, and Stopwatch — the scheduler owns the budget now.

**Estimated effort:** Low  
**Risk:** Low — API unchanged, behavior improves

### Phase 3: Wire `DirtFloorDeferredInit` Through Scheduler

**File:** `Modules/CustomTerrain/Utilities/DirtFloorDeferredInit.cs` (modify)

Route `QueueWork()` through `FrameBudgetScheduler` with appropriate priority mapping:
- `PhysicsBake` ? `WorkPriority.Critical` (if near player) or `WorkPriority.High`
- `MeshRestore` ? `WorkPriority.High`
- `MaterialSetup` ? `WorkPriority.Normal`
- `GrassSpawn` ? `WorkPriority.Low`
- `PaintMaskApply` ? `WorkPriority.Normal`
- `ClutterSpawn` ? `WorkPriority.Low`

Remove the internal per-type counters, frame time tracking, and sorted work list —
the scheduler handles all of this.

**Estimated effort:** Medium  
**Risk:** Low-Medium — must preserve distance-based priority and per-type rate limits
during transition, can keep type-based throttling as an inner layer

### Phase 4: Convert `ForceRegenerate()` to Scheduled Steps

**File:** `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.MeshGen.cs` (modify)

Replace the blocking `ForceRegenerate()` with `ScheduleRegeneration()` that breaks the work into steps:

```csharp
public void ForceRegenerate()
{
    _restoredFromCache = false;
    _needsRegeneration = false;
    _collisionDirty = true;
    _isFinalized = false;

    FrameBudgetScheduler.ScheduleCoroutine(
        WorkPriority.High,
        ForceRegenerateSteps(),
        transform.position);
}

private IEnumerator ForceRegenerateSteps()
{
    // Step 1: Calculate bounds + setup grid (fast, ~1ms)
    CalculateCombinedBounds();
    SetupGrid();
    yield return null;
    
    // Step 2: Pre-compute biome colors (main thread, ~2-5ms)
    Color32[] biomeColorGrid = PrecomputeBiomeColorGrid();
    yield return null;
    
    // Step 3: Queue background mesh job (near-instant on main thread)
    var meshInput = DirtFloorAsyncMeshBuilder.CreateInputFromTerrainDisks(
        _terrainDisks, _connectedDisks,
        _gridWidth, _gridHeight, _cellSize,
        _gridOrigin, transform.position, _combinedBounds,
        BlendDistance, MaxHeightDifference,
        biomeColorGrid);
    
    bool jobComplete = false;
    DirtFloorAsyncMeshBuilder.MeshJobOutput meshResult = null;
    
    DirtFloorAsyncMeshBuilder.QueueMeshJob(meshInput, output =>
    {
        meshResult = output;
        jobComplete = true;
    });
    
    // Step 4: Wait for background thread (yields each frame, 0ms main thread)
    while (!jobComplete) yield return null;
    
    // Step 5: Apply mesh result (~2-5ms)
    if (meshResult != null && !meshResult.HasError && meshResult.IsComplete)
        ApplyMeshJobResult(meshResult);
    else
    {
        SampleHeights();
        BuildMesh();
    }
    yield return null;
    
    // Step 6: Material + collision (~2-10ms)
    SetupMaterial();
    yield return null;
    
    UpdateCollisionMesh();
    yield return null;
    
    // Step 7: Grass refresh (~1-5ms)
    RefreshGrassHeights();
    
    _isActivelyEditing = false;
    _isFinalized = true;
}
```

**Estimated effort:** Medium  
**Risk:** Medium — must ensure terrain edits complete before next edit starts.
Add a `_pendingRegeneration` guard to prevent overlapping regenerations.

### Phase 5: Convert RPC Handlers to Scheduled Execution

**File:** `Modules/CustomTerrain/Core/TerrainOperationRPC.cs` (modify)

RPC handlers like `RPC_HandleCreateTerrain`, `RPC_HandleLevelTerrain`, etc. currently call
`CreateDiskInternal()`, `LevelAreaInternal()`, etc. which end up calling `ForceRegenerate()` synchronously.

After Phase 4, `ForceRegenerate()` is already scheduled, so these naturally benefit.
However, the RPC handlers themselves should also be scheduled to prevent bursts:

```csharp
private static void RPC_HandleCreateTerrain(long sender, ZPackage pkg)
{
    // ... parse pkg ...
    
    FrameBudgetScheduler.Schedule(WorkPriority.High, () =>
    {
        DirtFloorTerrainManager.CreateDiskInternal(position, radius, height);
        QueueLazySave(position);
    }, position);
}
```

**Estimated effort:** Low  
**Risk:** Low — operations are already debounced via operation IDs

### Phase 6: Convert `RegenerateMeshAsyncCoroutine()` to Scheduled Coroutine

**File:** `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.MeshGen.cs` (modify)

Replace `StartCoroutine(RegenerateMeshAsyncCoroutine())` with
`FrameBudgetScheduler.ScheduleCoroutine(...)`. The coroutine body stays the same but
its yield points are now budget boundaries managed by the scheduler instead of running
as an independent Unity coroutine.

```csharp
public void RegenerateMeshAsync()
{
    if (_isRegeneratingAsync) return;
    _isRegeneratingAsync = true;
    
    FrameBudgetScheduler.ScheduleCoroutine(
        WorkPriority.Normal,
        RegenerateMeshAsyncCoroutine(),
        transform.position,
        onComplete: () => { _isRegeneratingAsync = false; });
}
```

Similarly for `WaterMeshCombiner.RegenerateMeshAsync()`.

**Estimated effort:** Low  
**Risk:** Low — coroutine body unchanged, just different execution host

### Phase 7: Convert Water Terrain Sampling to Scheduled Batches

**File:** `Modules/CustomWater/Core/WaterMeshCombiner.MeshGen.cs` (modify)

`SampleTerrainHeightsAsync()` does 500 `Physics.Raycast` + `ZoneSystem.GetGroundHeight` per batch.
Each batch can cost 2-5ms. Route through the scheduler:

```csharp
private IEnumerator SampleTerrainHeightsAsync()
{
    const int BATCH_SIZE = 200; // Smaller batches for tighter budget control
    int processed = 0;
    
    for (int z = 0; z < _gridHeight; z++)
    {
        for (int x = 0; x < _gridWidth; x++)
        {
            _terrainHeights[z * _gridWidth + x] = SampleTerrainHeightAt(GridToWorld(x, z));
            
            processed++;
            if (processed % BATCH_SIZE == 0)
                yield return null; // Scheduler picks up next batch when budget allows
        }
    }
}
```

**Estimated effort:** Low  
**Risk:** Low — same logic, just smaller batches

---

## Sync Path Audit — What Still Blocks the Main Thread

These methods call `ForceRegenerate()` or `RegenerateMesh()` synchronously and will be
automatically fixed once Phase 4 is complete:

| Caller | File | Triggered By |
|--------|------|-------------|
| `ForceRegenerate()` | MeshGen.cs | Level/Raise/Lower/Smooth tools, RPC handlers |
| `OnLevelGround()` | HeightOps.cs | Hoe level tool |
| `OnRaiseGround()` | HeightOps.cs | Hoe raise tool |
| `OnSmoothGround()` | HeightOps.cs | Hoe smooth tool |
| `OnPickaxeHit()` | HeightOps.cs | Pickaxe damage |
| `ModifyHeightAt()` | HeightOps.cs | Generic height mod |
| `PunchHole()` | HeightOps.cs | Terrain deletion |
| `PatchHole()` | HeightOps.cs | Terrain fill |
| `ApplyMinHeight()` / `ApplyMaxHeight()` | HeightOps.cs | Constraint ops |
| `AddDiskInternal()` | DiskManagement.cs | Disk merge |
| `RestoreFromAnchorData()` | Core.cs | Zone reload |
| `RebuildMeshFromHeights()` | MeshGen.cs | Undo/redo |

### Methods that MUST stay synchronous (player safety)

| Method | Reason |
|--------|--------|
| `PunchHole()` ? `DirtFloorCombinerCache.SaveCache()` | Must persist deletion before server restart |
| `Shutdown()` on workers | Must complete before process exit |

---

## BackgroundWorker Callback Routing

After Phase 2, `BackgroundWorker.WorkerLoop()` already routes through `MainThreadDispatcher.Enqueue()`
which forwards to the scheduler. No changes needed to `BackgroundWorker.cs`.

However, for **priority awareness**, background workers can optionally use the scheduler directly:

```csharp
// In BackgroundWorker.WorkerLoop(), after _processFunc completes:
MainThreadDispatcher.Enqueue(() =>
{
    try { callback(output); }
    catch (Exception ex) { Debug.LogWarning($"[{_name}] Callback failed: {ex.Message}"); }
});

// FUTURE: Optional priority-aware routing (no changes needed for initial rollout):
// FrameBudgetScheduler.Schedule(WorkPriority.High, () => callback(output));
```

---

## Migration Strategy

### Rollout Order (minimize risk)

1. **Phase 1** — Create `FrameBudgetScheduler.cs` (new file, zero risk)
2. **Phase 2** — Wire `MainThreadDispatcher` through scheduler (immediate benefit)
3. **Phase 6** — Convert async coroutines to scheduled (easy, high impact)
4. **Phase 4** — Convert `ForceRegenerate()` to scheduled (highest impact, moderate risk)
5. **Phase 5** — Convert RPC handlers (prevents burst from multiplayer)
6. **Phase 3** — Wire `DirtFloorDeferredInit` through scheduler (consolidation)
7. **Phase 7** — Tighten water terrain sampling batches (polish)

### Fallback Plan

Keep all sync paths available behind a flag:

```csharp
// In DirtFloorMeshCombiner
public static bool UseScheduledRegeneration = true;

public void ForceRegenerate()
{
    if (UseScheduledRegeneration)
    {
        ScheduleRegeneration();
    }
    else
    {
        // Legacy sync path
        ForceRegenerateSync();
    }
}
```

---

## Expected Impact

| Scenario | Before (current) | After (scheduled) |
|----------|-------------------|-------------------|
| Server logs % loading (30+ combiners) | 50-200ms spikes, disconnect icon | ?5ms/frame, no disconnect |
| RPC burst (3 terrain edits in 1 frame) | 150ms+ (3× ForceRegenerate) | ?5ms/frame, edits spread over ~30 frames |
| Zone reload with 10 combiners | 100-500ms spike | ?5ms/frame, loads over ~50 frames |
| Terrain placement while server saving | Stutter + disconnect | Smooth — save and placement share budget |
| Large water body generation | 40-80ms coroutine phases | ?5ms/frame, spreads over ~15 frames |
| Clutter painting (100+ objects) | 10-50ms spike | ?5ms/frame, spawns over ~20 frames |

---

## File Summary

### New Files

| File | Purpose |
|------|---------|
| `Modules/Async/FrameBudgetScheduler.cs` | Centralized per-frame budget scheduler |
| `Modules/Async/MOD_OPERATIONS_OPTIMIZATION_PLAN.md` | This document |

### Modified Files (in rollout order)

| File | Changes | Phase |
|------|---------|-------|
| `Modules/Async/MainThreadDispatcher.cs` | Forward `Enqueue()` to scheduler | 2 |
| `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.MeshGen.cs` | `RegenerateMeshAsync()` ? scheduled coroutine, `ForceRegenerate()` ? scheduled steps | 4, 6 |
| `Modules/CustomTerrain/Core/TerrainOperationRPC.cs` | RPC handlers schedule instead of sync execute | 5 |
| `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.HeightOps.cs` | Height ops use scheduled `ForceRegenerate()` (automatic once Phase 4 is done) | 4 |
| `Modules/CustomTerrain/Utilities/DirtFloorDeferredInit.cs` | Route through scheduler | 3 |
| `Modules/CustomWater/Core/WaterMeshCombiner.MeshGen.cs` | Smaller terrain sampling batches, scheduled coroutine | 6, 7 |

### Unchanged Files

| File | Why |
|------|-----|
| `Modules/Async/BackgroundWorker.cs` | Already routes through `MainThreadDispatcher` ? scheduler |
| `Modules/Async/AsyncShutdownManager.cs` | Shutdown is intentionally synchronous |
| `Modules/CustomTerrain/Core/DirtFloorAsyncMeshBuilder.cs` | Background math unchanged |
| `Modules/CustomWater/Core/WaterAsyncMeshBuilder.cs` | Background math unchanged |

---

## Constraints

- **.NET Framework 4.8** — No `ValueTask`, no `Span<T>`, limited `Task` support
- **Unity Mono runtime** — `Thread`, `ThreadPool`, `Monitor`, `lock` are safe
- **Unity API is main-thread-only** — `Mesh`, `Texture2D`, `Material`, `Physics`, `Transform`, `GameObject`
- **Valheim ZNet heartbeat** — Must respond within ~500ms or disconnect icon appears.
  5ms/frame budget = ZNet gets 11ms+ every frame = no disconnects
- **Tool responsiveness** — Player terrain edits (place, level, raise) should visually
  complete within 0.5s (30 frames × ~1-2 steps/frame). Acceptable tradeoff vs 200ms freeze.

---

## Testing Checklist

### Budget Enforcement
- [ ] Place 50+ terrain disks rapidly ? no frame exceeds 20ms from mod work
- [ ] Server loads 100+ combiners ? disconnect icon never appears
- [ ] Three players edit terrain simultaneously ? all edits apply within 1s
- [ ] Paint clutter on large area ? spawning spreads over multiple frames

### Correctness
- [ ] Terrain edit (level/raise/smooth) produces identical result to sync path
- [ ] Mesh regeneration completes fully (no half-built meshes visible)
- [ ] Collision mesh matches visual mesh after scheduled regeneration
- [ ] Undo/redo works correctly with scheduled operations
- [ ] Zone unload during scheduled operation ? no errors (CancelAll cleans up)
- [ ] `PunchHole` ? `SaveCache` still synchronous for data integrity

### Diagnostics
- [ ] `FrameBudgetScheduler.PendingCount` shows correct queue depth
- [ ] `FrameBudgetScheduler.LastFrameMs` stays under 5ms in steady state
- [ ] `FrameBudgetScheduler.BudgetExceededLastFrame` only true for single-item overruns

---

## Performance Monitoring

Add to existing debug overlay or console command:

```csharp
// Console: va_scheduler_stats
Debug.Log($"Scheduler: {FrameBudgetScheduler.PendingCount} pending, " +
          $"last frame {FrameBudgetScheduler.LastFrameMs:F1}ms, " +
          $"exceeded={FrameBudgetScheduler.BudgetExceededLastFrame}");
```
