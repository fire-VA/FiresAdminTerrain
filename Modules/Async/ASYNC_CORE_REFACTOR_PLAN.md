# Async Core Refactor Plan

## Date: 2025-01-XX
## Status: IN PROGRESS

---

## Problem Statement

The mod has two independent async mesh builder implementations that duplicate the same worker-thread
pattern, each buried inside their respective module folders:

1. **`DirtFloorAsyncMeshBuilder`** — `Modules/CustomTerrain/Core/DirtFloorAsyncMeshBuilder.cs`
   - Contains `MainThreadDispatcher` (MonoBehaviour for dispatching callbacks to Unity main thread)
   - Contains terrain-specific `MeshJobInput`, `MeshJobOutput`, `PendingJob`, `MeshJobCallback`
   - Worker thread with job queue, lock, shutdown flag

2. **`WaterAsyncMeshBuilder`** — `Modules/CustomWater/Core/WaterAsyncMeshBuilder.cs`
   - Duplicates the entire worker thread pattern
   - Contains water-specific `WaterMeshJobInput`, `WaterMeshJobOutput`, `PendingJob`, `WaterMeshJobCallback`
   - Its own worker thread with job queue, lock, shutdown flag

### Issues
- **Code duplication:** Both files implement identical thread management (queue, lock, worker loop, shutdown)
- **Naming collisions:** Both define a `PendingJob` class (works only because they're in different files/namespaces but confusing)
- **No unified shutdown:** Worker threads must be individually shut down; if one is missed, threads leak
- **Not reusable:** Other systems that need background work (e.g., paint compression, cache I/O, biome computation) can't easily use these without copying the pattern again
- **`MainThreadDispatcher` is misplaced:** It lives inside the terrain async builder file but is used by water and cache systems too — it's a general utility

---

## Solution: Extract Shared Async Infrastructure

### New Folder: `Modules/Async/`

Create a shared async core that any module can use to offload CPU work to background threads.

### Architecture

```
Modules/Async/
??? ASYNC_CORE_REFACTOR_PLAN.md    ? This file
??? MainThreadDispatcher.cs         ? Moved from DirtFloorAsyncMeshBuilder.cs
??? BackgroundWorker.cs             ? Generic worker thread pool with job queue
??? AsyncJobTypes.cs                ? Common interfaces and base types
??? AsyncShutdownManager.cs         ? Centralized shutdown for all background workers
```

---

## Phase 1: Extract `MainThreadDispatcher` (FIRST)

`MainThreadDispatcher` is a general-purpose MonoBehaviour that dispatches `Action` callbacks
from background threads to the Unity main thread. It belongs in the async core, not inside
a terrain-specific file.

### Current Location
```csharp
// Inside DirtFloorAsyncMeshBuilder.cs
class MainThreadDispatcher : MonoBehaviour
{
    private static MainThreadDispatcher _instance;
    private static readonly Queue<Action> _actionQueue;
    private static readonly object _queueLock;
    
    public static void Enqueue(Action action);
    private static void EnsureInstance();
    private void Update();
    private void OnDestroy();
}
```

### New Location
`Modules/Async/MainThreadDispatcher.cs` — same class, proper namespace (`VerdantsAscent.Async`),
public visibility. All existing references continue to work since it's a static API.

---

## Phase 2: Create `BackgroundWorker` — Generic Worker Thread

Replace the duplicated worker thread pattern with a single, configurable `BackgroundWorker<TInput, TOutput>`.

### API Design
```csharp
namespace VerdantsAscent.Async
{
    /// <summary>
    /// Callback invoked on the main thread when a background job completes.
    /// </summary>
    public delegate void AsyncJobCallback<TOutput>(TOutput output);
    
    /// <summary>
    /// A generic single-thread background worker that processes jobs of type TInput ? TOutput.
    /// Consumers provide a processing function; the worker handles threading, queuing, and shutdown.
    /// 
    /// Usage:
    ///   var worker = new BackgroundWorker<MyInput, MyOutput>("TerrainMesh", ProcessJob);
    ///   worker.QueueJob(input, output => { /* main thread callback */ });
    ///   // ...
    ///   worker.Shutdown();
    /// </summary>
    public class BackgroundWorker<TInput, TOutput>
        where TInput : class
        where TOutput : class, new()
    {
        public BackgroundWorker(string name, Action<TInput, TOutput> processFunc);
        public bool HasPendingJobs { get; }
        public void QueueJob(TInput input, AsyncJobCallback<TOutput> callback);
        public void Shutdown();
    }
}
```

### Key Design Decisions
- **Single thread per worker** (not a thread pool) — matches existing behavior, predictable resource usage
- **Generic types** — each system defines its own Input/Output classes (terrain mesh, water mesh, paint compression, etc.)
- **Lazy thread creation** — thread only starts when first job is queued
- **Graceful shutdown** — sets flag, signals semaphore, joins thread with timeout
- **Main-thread callback dispatch** — uses `MainThreadDispatcher.Enqueue()` to deliver results

---

## Phase 3: Create `AsyncShutdownManager` — Centralized Lifecycle

A static registry that tracks all active `BackgroundWorker` instances and shuts them all down
on game logout / application quit.

### API
```csharp
namespace VerdantsAscent.Async
{
    public static class AsyncShutdownManager
    {
        public static void Register(IAsyncWorker worker);
        public static void Unregister(IAsyncWorker worker);
        public static void ShutdownAll();
    }
    
    public interface IAsyncWorker
    {
        void Shutdown();
        bool HasPendingJobs { get; }
    }
}
```

### Integration Points
- `DirtFloorPatches.ZNet_Shutdown_Prefix()` or `CustomWaterPatches.Game_Logout_Postfix()` calls `AsyncShutdownManager.ShutdownAll()`
- Each `BackgroundWorker` auto-registers on construction, auto-unregisters on shutdown

---

## Phase 4: Migrate `DirtFloorAsyncMeshBuilder`

Refactor to use the new `BackgroundWorker<MeshJobInput, MeshJobOutput>`:

```csharp
// DirtFloorAsyncMeshBuilder.cs — simplified
public static class DirtFloorAsyncMeshBuilder
{
    private static BackgroundWorker<MeshJobInput, MeshJobOutput> _worker;
    
    public static bool HasPendingJobs => _worker?.HasPendingJobs ?? false;
    
    public static void QueueMeshJob(MeshJobInput input, AsyncJobCallback<MeshJobOutput> callback)
    {
        if (_worker == null)
            _worker = new BackgroundWorker<MeshJobInput, MeshJobOutput>("TerrainMesh", ProcessMeshJob);
        _worker.QueueJob(input, callback);
    }
    
    public static void Shutdown() => _worker?.Shutdown();
    
    // CreateInputFromCombiner(), CreateInputFromTerrainDisks() — unchanged
    // ProcessMeshJob() — unchanged (the actual math)
}
```

- Remove `MainThreadDispatcher` from this file (moved to `Modules/Async/`)
- Remove duplicated thread management code
- Keep all terrain-specific math (`ProcessMeshJob`, `SmoothHeightEdgesOnThread`, etc.)
- Keep `MeshJobInput`, `MeshJobOutput` in this file (terrain-specific data)

---

## Phase 5: Migrate `WaterAsyncMeshBuilder`

Same pattern:

```csharp
public static class WaterAsyncMeshBuilder
{
    private static BackgroundWorker<WaterMeshJobInput, WaterMeshJobOutput> _worker;
    
    public static void QueueMeshJob(WaterMeshJobInput input, AsyncJobCallback<WaterMeshJobOutput> callback)
    {
        if (_worker == null)
            _worker = new BackgroundWorker<WaterMeshJobInput, WaterMeshJobOutput>("WaterMesh", ProcessWaterMeshJob);
        _worker.QueueJob(input, callback);
    }
    
    // All water math stays here
}
```

---

## Phase 6: Future Integration Points

Once the async core exists, other systems can easily adopt it:

| System | Current State | Background Work | Main Thread Work |
|--------|---------------|-----------------|------------------|
| Paint compression | `ZonePaintManager.SaveDirtyLayersAsync()` — already uses ThreadPool | Could use `BackgroundWorker` for consistency | ZDO writes |
| Cache file I/O | `DirtFloorCombinerCache.SaveCacheAsync()` — already uses ThreadPool | Could use `BackgroundWorker` | Snapshot only |
| Water cache I/O | `WaterCombinerCache.SaveCacheAsync()` — already uses ThreadPool | Could use `BackgroundWorker` | Snapshot only |
| Biome color grid | `PrecomputeBiomeColorGrid()` — already background via MeshBuilder | Already integrated | — |
| Underside mesh | `UpdateUndersideMeshAsync()` — already uses ThreadPool | Could use `BackgroundWorker` | Mesh.Set*() |
| Clutter batch spawn | `DirtFloorDeferredInit` — coroutine batching | Not threadable (Unity API) | Keep coroutine |
| Collision baking | `DirtFloorDeferredInit` — deferred queue | Not threadable (PhysX) | Keep deferred |

### Priority for Migration
1. ? `DirtFloorAsyncMeshBuilder` — primary terrain mesh builder
2. ? `WaterAsyncMeshBuilder` — primary water mesh builder
3. ? Paint compression — medium win, already works with ThreadPool
4. ? Cache I/O — medium win, already works with ThreadPool
5. ? Underside mesh — small win, infrequent operation

---

## File Changes Summary

### New Files
| File | Purpose |
|------|---------|
| `Modules/Async/MainThreadDispatcher.cs` | Extracted from `DirtFloorAsyncMeshBuilder.cs` |
| `Modules/Async/BackgroundWorker.cs` | Generic worker thread with job queue |
| `Modules/Async/AsyncShutdownManager.cs` | Centralized shutdown registry |

### Modified Files
| File | Changes |
|------|---------|
| `Modules/CustomTerrain/Core/DirtFloorAsyncMeshBuilder.cs` | Remove `MainThreadDispatcher`, remove thread management, use `BackgroundWorker` |
| `Modules/CustomWater/Core/WaterAsyncMeshBuilder.cs` | Remove thread management, use `BackgroundWorker` |
| `Modules/CustomTerrain/DirtFloorPatches.cs` | Add `AsyncShutdownManager.ShutdownAll()` to `ZNet_Shutdown_Prefix` |

### No Changes Needed
- All callers of `DirtFloorAsyncMeshBuilder.QueueMeshJob()` — API unchanged
- All callers of `WaterAsyncMeshBuilder.QueueMeshJob()` — API unchanged  
- All callers of `MainThreadDispatcher.Enqueue()` — API unchanged (class just moves files)

---

## Constraints

- **.NET Framework 4.8** — no `ValueTask`, no `Span<T>`, limited `Task` support
- **Unity Mono runtime** — `Thread`, `ThreadPool`, `Monitor`, `lock` are safe; `SemaphoreSlim` available
- **Unity API is main-thread-only** — `Mesh`, `Texture2D`, `Material`, `Physics`, `Transform`, `GameObject`
- **Keep it simple** — one thread per worker, not a full task scheduler. The mod doesn't need that complexity.
