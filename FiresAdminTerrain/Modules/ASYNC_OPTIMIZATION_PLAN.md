# Async & Off-Main-Thread Optimization Plan

## Overview

This document outlines a comprehensive plan to move CPU-intensive work off the Unity main thread
across all custom terrain, water, paint, and clutter systems. The goal is to eliminate frame hitches
during terrain placement, zone loading, paint operations, and mesh generation.

**Target Framework:** .NET Framework 4.8 (Unity Mono — no `ValueTask`, no `Span<T>`, limited `Task` support)

**Async Strategy:** We use three complementary patterns:
1. **Background `Thread`** via `DirtFloorAsyncMeshBuilder` / `MainThreadDispatcher` (already exists) — for pure math
2. **Unity `Coroutine` (`IEnumerator` + `yield return null`)** — for operations that must interleave with Unity API calls
3. **Deferred work queue** via `DirtFloorDeferredInit` (already exists) — for rate-limiting expensive per-frame work

**Key Constraint:** Unity API calls (Mesh, Texture2D, Material, Physics, Transform) MUST happen on the main thread.
All off-thread work must be limited to pure math/data — arrays, lists, structs only.

---

## Current State Summary

### What Already Has Async Support
| System | Sync Path | Async/Background Path | Notes |
|--------|-----------|----------------------|-------|
| Height Sampling | `SampleHeights()` | `SampleHeightsAsync()` (coroutine, batched) | ? Good |
| Mesh Building (terrain) | `BuildMesh()` | `BuildMeshAsync()` (coroutine, batched) | ? Good |
| Mesh Regeneration | `RegenerateMesh()` | `RegenerateMeshAsync()` (coroutine) | ? Good |
| Background Mesh Gen | — | `DirtFloorAsyncMeshBuilder` (Thread) | ? UV fix applied, **WIRED** into `RegenerateMeshAsyncCoroutine()` |
| LOD Transitions | `UpdateLOD()` | `DirtFloorLODManager` (batched) | ? Good |
| Deferred Init | — | `DirtFloorDeferredInit` (work queue) | ? Good |
| Water Terrain Sampling | `SampleTerrainHeights()` | `SampleTerrainHeightsAsync()` (coroutine) | ? Good |
| Water Surface Calc | `CalculateWaterSurfaceWithDepth()` | `CalculateWaterSurfaceWithDepthAsync()` (coroutine) | ? Good |
| Water Smoothing | `SmoothWaterSurface()` | `SmoothWaterSurfaceAsync()` (coroutine) | ? Good |
| Water Mesh Building | `BuildWaterMesh()` | `BuildWaterMeshAsync()` (coroutine) | ? Good |
| Paint Saves | `SavePaintMaskToTerrainComp()` | Batched via `_paintSaveTimer` | ? Good |
| Zone Paint Saves | `ZonePaintManager.SaveDirtyLayers()` | Background thread compress + main thread ZDO write | ? **IMPLEMENTED** (Phase 2) |
| Cache File I/O | `SaveCacheToFile()` / `LoadCacheFromFile()` | `SaveCacheAsync()` snapshots then background thread I/O | ? **IMPLEMENTED** (Phase 1) |
| Collision Baking | `UpdateCollisionMesh()` | Dirty flag + LOD skip + optimized cooking | ? **IMPLEMENTED** (Phase 4) |
| Biome Color Calc | `GetBiomeColorForPosition()` per vertex | `PrecomputeBiomeColorGrid()` ? background thread | ? **IMPLEMENTED** (Phase 7) |
| Water Mesh Gen | `CalculateWaterSurfaceWithDepth()` + `SmoothWaterSurface()` + `BuildWaterMesh()` | `WaterAsyncMeshBuilder` (Thread) | ? **IMPLEMENTED** (Phase 5) |
| Clutter Spawning | `PaintClutter()` | `PaintClutterBatch()` via `DirtFloorDeferredInit` | ? **IMPLEMENTED** (Phase 6) |
| Underside Mesh Gen | `UpdateUndersideMesh()` | `UpdateUndersideMeshAsync()` (background thread) | ? **IMPLEMENTED** (Phase 8) |
| Paint Mask Streaming | per-disk streaming (stub) | `ZonePaintManager.StreamLayers()` + `ZonePaintLayer.CacheAndUnload()`/`ReloadFromCache()` | ? **IMPLEMENTED** (Phase 9) |

### What Is Still Fully Synchronous (Targets for Optimization)
| System | Method(s) | Cost | Impact |
|--------|-----------|------|--------|
| **Paint mask decompression** | `ZonePaintLayer.DecompressPaintData()` | Medium | Blocks main thread during load |
| **Mesh cache restoration** | `RestoreFromCache()` ? mesh creation | Medium | Unity Mesh API required but data prep is threadable |
| **Paint texture initialization** | `ZonePaintLayer.InitializePaintMask()` (4225 pixels) | Low | Adds up with many zones |
| **Height edge smoothing** | `SmoothHeightEdges()` + `FillHeightGaps()` | Medium | Multiple passes over full grid |
| **Clutter clearing** | `ClutterManager.ClearInRadius()` | Low-Med | Iterates all grass objects |
| **Water cliff detection** | `DetectCliffEdges()` | Low | Full grid scan |
| **Water swim trigger setup** | `SetupSwimTrigger()` full grid scan | Low-Med | Scans entire grid for bounds |
| **Terrain persistence save** | `TerrainPersistence` ZDO writes | Low-Med | Multiple ZDO writes |
| **Biome persistence** | `BiomePaintPersistence.SaveCache()` | Low | File I/O |

---

## Phase 1: Cache I/O — Background Thread (HIGHEST IMPACT)

### Problem
`DirtFloorCombinerCache.SaveCacheToFile()` and `LoadCacheFromFile()` perform synchronous binary file I/O
that can block the main thread for 50-200ms on large worlds (100+ terrain groups, each with mesh vertex data).

### Solution
Move file I/O to a background thread. The data structures are already plain serializable arrays/lists
that don't touch Unity API.

### Implementation

```csharp
// In DirtFloorCombinerCache.cs

private static volatile bool _isSaving = false;
private static readonly object _saveLock = new object();

/// <summary>
/// Saves cache to file on a background thread.
/// Thread-safe: takes a snapshot of all data before starting the thread.
/// </summary>
public static void SaveCacheAsync()
{
    if (_currentCache == null || string.IsNullOrEmpty(_currentWorldName)) return;
    if (_isSaving) return; // Already saving

    // SNAPSHOT on main thread (fast — just copies references/arrays)
    var snapshot = SnapshotCacheData(_currentCache);
    string cachePath = GetCachePath(_currentWorldName);

    _isSaving = true;
    _cacheDirty = false;

    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            lock (_saveLock)
            {
                string directory = System.IO.Path.GetDirectoryName(cachePath);
                if (!System.IO.Directory.Exists(directory))
                    System.IO.Directory.CreateDirectory(directory);

                SaveCacheToFile(cachePath, snapshot);
            }
        }
        catch (System.Exception ex)
        {
            // Log on main thread
            MainThreadDispatcher.Enqueue(() =>
                Debug.LogWarning($"[DirtFloorCombinerCache] Async save failed: {ex.Message}"));
        }
        finally
        {
            _isSaving = false;
        }
    });
}

/// <summary>
/// Deep-copies cache data for thread-safe background saving.
/// Only copies arrays (no Unity objects).
/// </summary>
private static WorldCacheData SnapshotCacheData(WorldCacheData source) { ... }
```

### Load Path
Loading happens at world init (player isn't moving yet), so the frame hitch is less critical.
However, for very large caches we can still background it:

```csharp
public static void InitializeForWorldAsync(string worldName, long worldSeed, Action onComplete)
{
    // ... setup ...
    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            var cache = LoadCacheFromFile(cachePath);
            MainThreadDispatcher.Enqueue(() =>
            {
                _currentCache = cache;
                // rebuild DiskToGroupMap etc. on main thread
                onComplete?.Invoke();
            });
        }
        catch { ... }
    });
}
```

### Files Modified
- `DirtFloorCombinerCache.cs` — Add `SaveCacheAsync()`, `SnapshotCacheData()`, `InitializeForWorldAsync()`
- `DirtFloorMeshCombiner.Lifecycle.cs` — Call `SaveCacheAsync()` instead of `SaveCache()` in non-critical paths
- Keep synchronous `SaveCache()` for `OnCombinerPermanentlyDeleted()` (must be reliable)

### Estimated Impact
- **Before:** 50-200ms frame hitch on auto-save (every 2 min) with large worlds
- **After:** <1ms (enqueue snapshot), I/O happens entirely off-thread

---

## Phase 2: Paint Data Compression/Decompression — Background Thread

### Problem
`ZonePaintLayer.CompressPaintData()` and `DecompressPaintData()` call `Utils.Compress()`/`Utils.Decompress()`
which are zlib operations on 65×65×4 = 16,900 bytes. Each call takes 1-5ms. When saving multiple dirty layers
simultaneously, this adds up.

### Solution
Move compression to background thread. The pixel data is a plain `byte[]` — no Unity API needed.

### Implementation

```csharp
// In ZonePaintManager.cs - modify SaveDirtyLayers()

public static void SaveDirtyLayersAsync()
{
    if (_dirtyLayers.Count == 0) return;

    var layersToSave = new List<(ZonePaintLayer layer, Color32[] pixels)>();

    // SNAPSHOT pixel data on main thread (Texture2D.GetPixels32 requires main thread)
    foreach (var key in _dirtyLayers)
    {
        if (_layers.TryGetValue(key, out var layer))
        {
            Color32[] pixels = layer.PaintMask.GetPixels32();
            layersToSave.Add((layer, pixels));
        }
    }

    var dirtyKeys = new List<(Vector2Int, TerrainPaintLayer)>(_dirtyLayers);
    _dirtyLayers.Clear();

    // COMPRESS on background thread
    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
    {
        var results = new List<(ZonePaintLayer layer, byte[] compressed, int zdoKey)>();

        foreach (var (layer, pixels) in layersToSave)
        {
            byte[] raw = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                raw[i * 4]     = pixels[i].r;
                raw[i * 4 + 1] = pixels[i].g;
                raw[i * 4 + 2] = pixels[i].b;
                raw[i * 4 + 3] = pixels[i].a;
            }

            byte[] compressed = Utils.Compress(raw);
            int zdoKey = layer.GetZDOKey();
            results.Add((layer, compressed, zdoKey));
        }

        // WRITE ZDO on main thread (ZDO API not thread-safe)
        MainThreadDispatcher.Enqueue(() =>
        {
            foreach (var (layer, compressed, zdoKey) in results)
            {
                Vector3 worldPos = ZonePaintManager.GetZoneOrigin(layer.ZoneKey) + new Vector3(32, 0, 32);
                TerrainComp tc = TerrainComp.FindTerrainCompiler(worldPos);
                if (tc == null) continue;

                var nview = tc.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) continue;

                nview.GetZDO()?.Set(zdoKey, compressed);
            }
        });
    });
}
```

### Files Modified
- `ZonePaintManager.cs` — Add `SaveDirtyLayersAsync()`
- `ZonePaintLayer.cs` — Add `CompressPaintDataFromPixels(Color32[])` static helper

### Estimated Impact
- **Before:** 2-15ms per save batch (1-5ms per layer × number of dirty layers)
- **After:** <0.5ms main thread (snapshot pixels), compression runs off-thread

---

## Phase 3: Background Mesh Builder — Fix & Wire Into Main Flow

### Problem
`DirtFloorAsyncMeshBuilder` already exists with a proper worker thread pattern, but:
1. UV calculation uses bounds-relative coords (stale — should be zone-relative)
2. It's not wired into the main `RegenerateMesh()` / `RegenerateMeshAsync()` flow
3. Missing biome color computation (runs per-vertex, hits multiple systems)

### Solution
Update `DirtFloorAsyncMeshBuilder` to:
1. Use zone-relative UV calculation matching `ZonePaintManager.WorldToZoneUV()`
2. Accept `TerrainDisk[]` data (not just legacy `DirtFloorController`)
3. Compute biome color data on background thread (pre-sample into a grid)
4. Wire it as the primary path for initial mesh generation

### Implementation Changes

```csharp
// DirtFloorAsyncMeshBuilder.cs — UV fix in ProcessMeshJob()

// OLD:
float paintU = (worldX - input.BoundsMin.x) / boundsWidth;
float paintV = (worldZ - input.BoundsMin.z) / boundsDepth;

// NEW:
int zoneKeyX = (int)Math.Floor(worldX / 64f);
int zoneKeyZ = (int)Math.Floor(worldZ / 64f);
float zoneOriginX = zoneKeyX * 64f;
float zoneOriginZ = zoneKeyZ * 64f;
float paintU = Clamp01((worldX - zoneOriginX) / 64f);
float paintV = Clamp01((worldZ - zoneOriginZ) / 64f);
```

Add `TerrainDiskData` struct support:
```csharp
public struct TerrainDiskInput
{
    public float PosX, PosY, PosZ;
    public float Radius;
    public float HeightOffset;
}

public static MeshJobInput CreateInputFromTerrainDisks(
    List<TerrainDisk> terrainDisks, ...) { ... }
```

Wire into `RegenerateMeshAsync()`:
```csharp
// In DirtFloorMeshCombiner.MeshGen.cs — RegenerateMeshAsyncCoroutine()

// Instead of yielding per-batch in SampleHeightsAsync/BuildMeshAsync,
// submit the ENTIRE job to DirtFloorAsyncMeshBuilder:

var input = DirtFloorAsyncMeshBuilder.CreateInputFromTerrainDisks(
    _terrainDisks, _gridWidth, _gridHeight, _cellSize,
    _gridOrigin, transform.position, _combinedBounds,
    BlendDistance, MaxHeightDifference);

bool jobComplete = false;
DirtFloorAsyncMeshBuilder.MeshJobOutput result = null;

DirtFloorAsyncMeshBuilder.QueueMeshJob(input, output =>
{
    result = output;
    jobComplete = true;
});

// Wait for background thread
while (!jobComplete) yield return null;

// Apply result on main thread (Unity API)
if (result != null && !result.HasError)
{
    ApplyMeshJobResult(result);
}
```

### Files Modified
- `DirtFloorAsyncMeshBuilder.cs` — Fix UVs, add `TerrainDiskInput`, add UV2/color support
- `DirtFloorMeshCombiner.MeshGen.cs` — Wire `QueueMeshJob` into `RegenerateMeshAsyncCoroutine()`
- `MainThreadDispatcher.cs` — Already exists, no changes needed

### Estimated Impact
- **Before:** Coroutine-based async still blocks main thread for each batch
- **After:** Full height sampling + vertex/triangle generation runs on background thread; main thread only does `Mesh.Set*()` calls

---

## Phase 4: Collision Mesh Baking — Deferred Queue

### Problem
`Physics.BakeMesh()` (called from `UpdateCollisionMesh()`) is one of the most expensive single calls —
20-100ms for large meshes. It cannot be moved off the main thread (Unity PhysX requirement).

### Solution
Already partially solved by `DirtFloorDeferredInit` which limits to 1 bake/frame.
Improvements:
1. **Ensure ALL collision bakes go through the deferred queue** (some paths call `UpdateCollisionMesh()` directly)
2. **Use `MeshCollider.cookingOptions`** to speed up baking (disable unnecessary features)
3. **Skip collision bake entirely for LOD/culled combiners** (they don't need collision)
4. **Re-bake only when mesh actually changes** (track dirty flag)

### Implementation

```csharp
// In DirtFloorMeshCombiner — UpdateCollisionMesh()

private bool _collisionDirty = true;

public void UpdateCollisionMesh()
{
    if (!_collisionDirty) return;

    // Skip if LOD or culled
    if (_isLOD || _isCulled) return;

    // Always defer to DirtFloorDeferredInit
    var deferredInit = DirtFloorDeferredInit.Instance;
    if (deferredInit != null)
    {
        deferredInit.QueuePhysicsBake(this, () => BakeCollisionMeshImmediate());
    }
    else
    {
        BakeCollisionMeshImmediate();
    }
}

private void BakeCollisionMeshImmediate()
{
    if (_meshCollider == null || _combinedMesh == null) return;

    // Optimize cooking options
    _meshCollider.cookingOptions =
        MeshColliderCookingOptions.CookForFasterSimulation |
        MeshColliderCookingOptions.UseFastMidphase;

    _meshCollider.sharedMesh = null;
    _meshCollider.sharedMesh = _combinedMesh;
    _collisionDirty = false;
}
```

### Files Modified
- `DirtFloorMeshCombiner.Core.cs` or a new partial `DirtFloorMeshCombiner.Collision.cs`
- `DirtFloorMeshCombiner.MeshGen.cs` — Ensure `RegenerateMesh()` uses deferred path
- `DirtFloorMeshCombiner.Lifecycle.cs` — Skip collision during LOD/cull transitions

### Estimated Impact
- **Before:** Random 20-100ms spikes when collision bakes happen synchronously
- **After:** Max 1 bake per frame (via deferred init), optimized cooking options

---

## Phase 5: Water Mesh Generation — True Background Thread

### Problem
Water mesh generation is already coroutine-based (good), but still runs batched loops on the main thread.
For large water bodies (rivers), this can still cause micro-hitches (5-15ms per batch yield).

### Solution
Create `WaterAsyncMeshBuilder` following the same pattern as `DirtFloorAsyncMeshBuilder`:
1. Serialize all water disk data to thread-safe structs
2. Compute terrain sampling, surface calculation, smoothing, and vertex generation on background thread
3. Apply to Unity Mesh on main thread

### Implementation
```csharp
// New file: WaterAsyncMeshBuilder.cs

public static class WaterAsyncMeshBuilder
{
    public class WaterMeshJobInput
    {
        public int GridWidth, GridHeight;
        public float CellSize;
        public Vector3 GridOrigin, CombinerPosition;
        public WaterDiskData[] Disks;
        public float BlendDistance;
        public float PondHeightTolerance;
        // ... all constants needed for surface calc
    }

    public struct WaterDiskData
    {
        public float PosX, PosY, PosZ;
        public float Radius, RadiusPlusBlend;
        public float MaxDepth, EdgeFadeStart;
    }

    public class WaterMeshJobOutput
    {
        public float[] SurfaceHeights;
        public float[] TerrainHeights;
        public float[] WaterDepths;
        public Vector3[] Vertices;
        public int[] Triangles;
        public Vector2[] UVs;
        public Color[] Colors;
        public bool IsComplete, HasError;
    }

    // Uses same MainThreadDispatcher + worker thread pattern
}
```

**Note:** `SampleTerrainHeightAt()` calls `ZoneSystem.instance.GetGroundHeight()` and `Physics.Raycast()`,
which are Unity API calls. These CANNOT run on a background thread. Options:
- **Option A:** Pre-sample terrain heights into a grid on main thread (fast raycast batch), then pass grid to background thread
- **Option B:** Keep terrain sampling as coroutine, move only surface calc + smoothing + mesh building to background thread

**Recommended: Option A** — batch all raycasts in a single coroutine pass first, then submit the full grid to background thread.

### Files Modified
- New: `WaterAsyncMeshBuilder.cs` (in `Modules\CustomWater\Core\`)
- `WaterMeshCombiner.MeshGen.cs` — Wire background builder into `RegenerateMeshAsyncCoroutine()`

### Estimated Impact
- **Before:** 15-40ms total across coroutine batches for large water bodies
- **After:** <2ms main thread (terrain pre-sample + mesh apply), math runs off-thread

---

## Phase 6: Clutter Spawning — Batched Instantiation

### Problem
`ClutterPaintTool.PaintClutter()` and grass spawning (`SpawnGrassOnMesh()` — currently disabled)
instantiate many GameObjects synchronously. `Object.Instantiate()` is a main-thread-only Unity API call,
so it can't be moved off-thread, but it can be batched/spread.

### Solution
Extend `DirtFloorDeferredInit` to handle clutter spawning in batches:

```csharp
// Rate-limited clutter spawning
public void QueueClutterSpawn(Vector3 position, string clutterType, float density,
    int maxPerFrame = 10)
{
    QueueWork(WorkType.ClutterSpawn, null, () =>
    {
        // Spawn up to maxPerFrame objects, re-queue remainder
        int spawned = ClutterPaintTool.PaintClutterBatch(position, clutterType, density, maxPerFrame);
        if (spawned >= maxPerFrame)
        {
            // More to spawn — re-queue
            QueueClutterSpawn(position, clutterType, density, maxPerFrame);
        }
    }, 6);
}
```

### Files Modified
- `DirtFloorDeferredInit.cs` — Add `WorkType.ClutterSpawn`, `QueueClutterSpawn()`
- `ClutterPaintTool.cs` — Add `PaintClutterBatch()` that limits instantiation count
- `ClutterManager.cs` — Route through deferred init

### Estimated Impact
- **Before:** 10-50ms spike when painting clutter (depends on density/area)
- **After:** Spread across multiple frames, max 2-5ms per frame

---

## Phase 7: Biome Color Pre-computation

### Problem
`GetBiomeColorForPosition()` is called per-vertex during mesh building. Each call potentially:
1. Checks anchor biome paints (dictionary lookup)
2. Checks `BiomePaintPersistence` (spatial lookup)
3. Checks `EnvironmentBoxController` (list scan)
4. Calls `WorldGenerator.instance.GetBiome()` (noise sampling)

For a mesh with 5000 vertices, this is 5000 × 4 lookups.

### Solution
Pre-compute a biome color grid on the background thread:

```csharp
// Before mesh building, compute a biome color lookup grid
// on the main thread (one pass — much faster than per-vertex)

Color32[] biomeColorGrid = new Color32[_gridWidth * _gridHeight];
for (int z = 0; z < _gridHeight; z++)
{
    for (int x = 0; x < _gridWidth; x++)
    {
        Vector3 worldPos = GridToWorld(x, z);
        biomeColorGrid[z * _gridWidth + x] = GetBiomeColorForPosition(worldPos.x, worldPos.z);
    }
}

// Pass grid to background thread for mesh building
// Background thread indexes into grid instead of calling Unity APIs
```

This makes biome colors O(grid) instead of O(vertices × lookups) and enables it to run on background thread.

### Files Modified
- `DirtFloorMeshCombiner.MeshGen.cs` — Add `PrecomputeBiomeColorGrid()`, pass to `DirtFloorAsyncMeshBuilder`
- `DirtFloorAsyncMeshBuilder.cs` — Accept `Color32[] biomeColors` in `MeshJobInput`

### Estimated Impact
- **Before:** 5-20ms per mesh build for biome color lookups
- **After:** <1ms (single grid pass, results cached for background thread)

---

## Phase 8: Underside Mesh Generation — Background Thread ?

### Problem
`UpdateUndersideMesh()` generates a full mesh synchronously when the underside is enabled
(isolated/floating terrain). This mirrors the main mesh generation cost.

### Solution
Added `UpdateUndersideMeshAsync()` coroutine in `DirtFloorMeshCombiner.Underside.cs` that:
1. **Main thread:** Snapshots `_combinedMesh.vertices` and `_combinedMesh.triangles` + `transform.position`
2. **Background thread (ThreadPool):** Computes vertex offsets (-0.02 Y for z-fighting prevention),
   inverts triangle winding order, calculates world-space UVs for rock tiling — all pure math
3. **Main thread:** Applies computed arrays to `_undersideMesh` via Unity Mesh API, updates `_undersideCollider`
4. **Sync fallback:** If the background job fails, falls back to `GenerateUndersideMesh()` sync path

Wired into `RegenerateMeshAsyncCoroutine()` — the async path now calls `yield return UpdateUndersideMeshAsync()`
after `CheckAutoEnableUnderside()` instead of the synchronous `UpdateUndersideMesh()`.

### Files Modified
- `DirtFloorMeshCombiner.Underside.cs` — Added `UpdateUndersideMeshAsync()` coroutine with background thread
- `DirtFloorMeshCombiner.MeshGen.cs` — Replaced sync `UpdateUndersideMesh()` call with `yield return UpdateUndersideMeshAsync()`

### Estimated Impact
- **Before:** 5-30ms synchronous underside mesh generation
- **After:** <1ms main thread (snapshot + apply), math runs off-thread

---

## Phase 9: Paint Mask Streaming — Zone-Based ?

### Problem
`DirtFloorPaintMaskStreaming` existed but was incomplete:
- `LoadPaintMask()` and `UnloadPaintMask()` were stubbed out
- No actual texture creation/destruction happening
- Not integrated with the zone paint system

### Solution
Implemented zone-based paint mask streaming through `ZonePaintManager` and `ZonePaintLayer`:

**ZonePaintLayer changes:**
- Added `IsUnloaded` property and `_cachedCompressedData` field
- `CacheAndUnload()` — Compresses pixel data via `Utils.Compress()`, destroys GPU `Texture2D`,
  stores ~2-4KB compressed data in memory
- `ReloadFromCache()` — Decompresses cached data, recreates `Texture2D`, applies pixels,
  frees compressed cache. Falls back to blank mask if cache is missing.

**ZonePaintManager changes:**
- `StreamLayers()` — Called every 2 seconds from `Update()`. Iterates all cached layers,
  compares zone center distance to player position. Unloads layers beyond 100m,
  reloads layers within 80m. Skips dirty layers (must save first). Limited to 3 ops/update.
- `GetPaintTexture()` — Auto-reloads unloaded layers when accessed (on-demand reload)
- `GetCacheStats()` — Now reports streamed-out layer count

**DirtFloorPaintMaskStreaming changes:**
- Deprecated in favor of `ZonePaintManager.StreamLayers()` — all methods are now stubs
  that delegate to the zone-based system or are no-ops
- `ForceLoadPaintMask()` delegates to `ZonePaintManager.GetPaintTexture()` which auto-reloads

### Files Modified
- `ZonePaintLayer.cs` — Added `CacheAndUnload()`, `ReloadFromCache()`, `IsUnloaded`, `_cachedCompressedData`
- `ZonePaintManager.cs` — Added `StreamLayers()`, streaming constants, auto-reload in `GetPaintTexture()`
- `DirtFloorPaintMaskStreaming.cs` — Redirected to zone paint streaming (deprecated legacy stubs)

### Estimated Impact
- **Before:** All zone paint textures loaded permanently (65×65×4 = 17KB each × zones)
- **After:** Only nearby zones have GPU textures; distant zones use compressed `byte[]` (~2-4KB)

---

## Implementation Priority

| Phase | Impact | Effort | Risk | Priority |
|-------|--------|--------|------|----------|
| **1. Cache I/O** | ?? High | ?? Low | ?? Low | ? **DONE** |
| **2. Paint Compression** | ?? Medium | ?? Low | ?? Low | ? **DONE** |
| **4. Collision Baking** | ?? High | ?? Low | ?? Low | ? **DONE** |
| **3. Background Mesh Builder** | ?? High | ?? Medium | ?? Medium | ? **DONE** (UV fix + wired into main flow) |
| **7. Biome Color Pre-compute** | ?? Medium | ?? Low | ?? Low | ? **DONE** |
| **5. Water Background Builder** | ?? Medium | ?? Medium | ?? Medium | ? **DONE** |
| **6. Clutter Batching** | ?? Medium | ?? Low | ?? Low | ? **DONE** |
| **8. Underside Mesh Async** | ?? Low | ?? Low | ?? Low | ? **DONE** |
| **9. Paint Mask Streaming** | ?? Low | ?? Medium | ?? Medium | ? **DONE** |

---

## Thread Safety Rules

### Rule 1: Never Touch Unity API Off-Thread
Unity APIs that MUST be called on main thread:
- `Mesh.Set*()`, `Mesh.RecalculateNormals()`, etc.
- `Texture2D.SetPixels()`, `Texture2D.Apply()`, `Texture2D.GetPixels32()`
- `Material.*`, `MaterialPropertyBlock.*`
- `Physics.*`, `MeshCollider.*`
- `Transform.*`
- `GameObject.*`, `Object.Instantiate()`, `Object.Destroy()`
- `ZNetView.*`, `ZDO.*`
- `Debug.Log()` (technically thread-safe in Unity but can cause issues)

### Rule 2: Snapshot Before Dispatching
Always take a snapshot of data on the main thread before passing to background:
```csharp
// GOOD: Snapshot first
Color32[] pixels = texture.GetPixels32(); // Main thread
ThreadPool.QueueUserWorkItem(_ => {
    byte[] compressed = CompressPixels(pixels); // Background thread
    MainThreadDispatcher.Enqueue(() => SaveToZDO(compressed)); // Main thread
});

// BAD: Accessing texture on background thread
ThreadPool.QueueUserWorkItem(_ => {
    Color32[] pixels = texture.GetPixels32(); // CRASH!
});
```

### Rule 3: Use `MainThreadDispatcher` for Callbacks
All background thread results must be marshaled back via `MainThreadDispatcher.Enqueue()`.

### Rule 4: Protect Shared State
Use `volatile`, `Interlocked`, or `lock` for any state shared between main and background threads:
- `_isSaving` flag ? `volatile bool`
- `_pendingJobs` queue ? `lock` + `Monitor.Pulse`
- Job result ? callback via `MainThreadDispatcher`

---

## Testing Checklist

For each phase, verify:
- [ ] No Unity API calls on background threads (test with `UnityEngine.Debug.Assert(Thread.CurrentThread == mainThread)`)
- [ ] No race conditions (test rapid placement + zone transitions)
- [ ] Correct visual results (paint appears in right location, meshes look identical)
- [ ] Memory cleanup (no texture/mesh leaks when zones unload)
- [ ] Graceful shutdown (background threads terminate on world unload)
- [ ] Fallback to sync path if async fails (e.g., `MainThreadDispatcher` destroyed)
- [ ] Dedicated server compatibility (no GPU/material operations on headless)

---

## Performance Measurement

Use `Stopwatch` or `Time.realtimeSinceStartup` to measure before/after:

```csharp
// Instrument key methods
var sw = System.Diagnostics.Stopwatch.StartNew();
// ... operation ...
sw.Stop();
if (sw.ElapsedMilliseconds > 5)
    Debug.Log($"[PERF] {methodName}: {sw.ElapsedMilliseconds}ms");
```

Key metrics to track:
- Frame time during zone load (target: <16ms / 60fps)
- Frame time during terrain placement (target: <20ms)
- Frame time during paint operations (target: <10ms)
- Frame time during auto-save (target: <2ms)
- Memory usage per zone (paint textures + cached data)

---

## Migration Notes

### Backward Compatibility
- All async paths must have synchronous fallbacks
- `DirtFloorAsyncMeshBuilder` UV fix must also be applied to sync `BuildMesh()` (already done — uses `ZonePaintManager.WorldToZoneUV()`)
- Cache file format unchanged (version 4) — only the I/O mechanism changes

### Dedicated Server Considerations
- Skip all GPU operations (`Texture2D`, `Material`, `Shader`)
- Skip LOD/culling (no renderer)
- Still need: collision baking, height sampling, ZDO persistence
- Background mesh builder: skip UV/color generation on server, only compute heights + collision geometry
