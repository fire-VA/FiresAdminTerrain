# Water Ambient VFX — Implementation Changelog

## Date: 2025

## Overview

Added ephemeral ambient visual effects and audio to custom water bodies (rivers, ponds, lakes).
All effects are non-persistent (no ZDO, no ZNetView, no ZNetScene registration).
They are created as child GameObjects of the combiner and cascade-destroyed when it is destroyed.
Re-created automatically on combiner restore from anchor or mesh regeneration.

---

## Files Created

### `Modules/CustomWater/VFX/WaterAmbientVFX.cs` — NEW

A `MonoBehaviour` component attached to each `WaterMeshCombiner`. Manages 4 effect types:

| Effect | Description | Budget |
|--------|-------------|--------|
| **Shoreline Splash** | Procedural `ParticleSystem` mist emitters at water/terrain boundary cells. Scans the combiner grid for cells with water depth > 0.15m that have a dry neighbor. | Max 8 emitters, 8m min spacing |
| **Waterfall Spray** | Cone-shaped particle spray at cliff cells with >2m drops. Uses existing `CliffCellData` from `DetectCliffEdges()`. Larger drops produce more particles. | Max 4 emitters, sorted by drop height |
| **Surface Debris** | Single `ParticleSystem` covering water bounds. Emits tiny foam/speck particles that drift gently. Count scales with water area (capped at 40 particles). | 1 emitter per combiner |
| **Ambient Audio** | 3D spatialized looping `AudioSource`. Searches Valheim prefabs (`fx_river`, `sfx_water`, `WaterVolume`, etc.) and `ZSFX` components for water audio clips. Rivers get higher volume (0.3) + range (40m) vs ponds (0.15 / 25m). Falls back to `Resources.FindObjectsOfTypeAll<AudioClip>()` name search. | 1 source per combiner |

**Key design decisions:**
- All effects are plain `GameObject` children of a `_vfxContainer` parented to the combiner
- No `ZNetView`, no `ZDO`, no `ZNetScene` registration — avoids `ZNetScene.RemoveObjects` NRE
- Cascade-destroyed when combiner is destroyed
- Skipped during mass auto-generation (`AutoWaterGenerator.IsGenerating` check)
- Static caches (audio clips, particle material) reset on `ClearAllCombiners()`

---

## Files Modified

### `Modules/CustomWater/Core/WaterMeshCombiner.cs`

**4 changes:**

1. **Added `_ambientVFX` field** (line near `_damageHandler`):
   ```csharp
   private WaterAmbientVFX _ambientVFX;
   ```

2. **Added `SpawnAmbientVFX()` method** (new region before Wave Calculation):
   ```csharp
   private void SpawnAmbientVFX()
   {
       if (AutoWaterGenerator.IsGenerating) return;
       if (_ambientVFX == null)
           _ambientVFX = gameObject.AddComponent<WaterAmbientVFX>();
       _ambientVFX.Initialize(this);
   }
   ```

3. **Added cleanup in `OnDestroy()`**:
   ```csharp
   if (_ambientVFX != null)
   {
       _ambientVFX.CleanupVFX();
       _ambientVFX = null;
   }
   ```

4. **Added static reset in `ClearAllCombiners()`**:
   ```csharp
   WaterAmbientVFX.ResetStaticCache();
   ```

5. **Added `SpawnAmbientVFX()` call in `RestoreFromAnchorData()`** after `DetectCliffEdges()`:
   ```csharp
   DetectCliffEdges();
   SpawnAmbientVFX();
   ```

### `Modules/CustomWater/Core/WaterMeshCombiner.MeshGen.cs`

**2 changes:**

1. **Sync `RegenerateMesh()` path** — added `SpawnAmbientVFX()` call after `DetectCliffEdges()`:
   ```csharp
   DetectCliffEdges();
   SpawnAmbientVFX();
   ```

2. **Async `RegenerateMeshAsyncCoroutine()` path** — same addition after `DetectCliffEdges()`:
   ```csharp
   DetectCliffEdges();
   yield return null;
   SpawnAmbientVFX();
   ```

---

## How to Revert

### Quick revert (disable VFX without removing code):

In `WaterMeshCombiner.cs`, comment out the body of `SpawnAmbientVFX()`:
```csharp
private void SpawnAmbientVFX()
{
    return; // DISABLED — uncomment to re-enable ambient VFX
    // ... rest of method
}
```

### Full revert (remove all changes):

1. **Delete** `Modules/CustomWater/VFX/WaterAmbientVFX.cs`

2. **In `WaterMeshCombiner.cs`:**
   - Remove the `_ambientVFX` field
   - Remove the entire `#region Ambient VFX` block (the `SpawnAmbientVFX()` method)
   - Remove the cleanup block in `OnDestroy()`:
     ```csharp
     if (_ambientVFX != null) { _ambientVFX.CleanupVFX(); _ambientVFX = null; }
     ```
   - Remove `WaterAmbientVFX.ResetStaticCache();` from `ClearAllCombiners()`
   - Remove `SpawnAmbientVFX();` from `RestoreFromAnchorData()`

3. **In `WaterMeshCombiner.MeshGen.cs`:**
   - Remove `SpawnAmbientVFX();` call after `DetectCliffEdges()` in `RegenerateMesh()`
   - Remove `SpawnAmbientVFX();` call after `DetectCliffEdges()` in `RegenerateMeshAsyncCoroutine()`
   - Restore the old comment line: `// VFX manager removed - auto VFX spawning disabled`

4. **Delete** this file (`WATER_AMBIENT_VFX_CHANGES.md`)

---

## Testing Checklist

- [ ] Place water disks manually — VFX appear after mesh regeneration
- [ ] Auto-generate rivers (`va_auto_water_generate`) — VFX NOT spawned during generation (performance)
- [ ] Logout + re-login — VFX appear on restored combiners from anchors
- [ ] Mop tool removes water — VFX rebuild correctly (no orphaned particles)
- [ ] Delete combiner (`DestroyPermanently`) — VFX destroyed cleanly (no errors)
- [ ] Multiple water bodies — each has independent VFX, no cross-contamination
- [ ] Audio plays for rivers (flowing) and ponds (gentle) — verify correct clip
- [ ] Waterfall spray appears at cliff edges with >2m drop height
- [ ] Shoreline splash appears at water/terrain boundary
- [ ] Surface debris particles visible on water surface, drifting gently
- [ ] No `NullReferenceException` in `ZNetScene.RemoveObjects` — VFX have no ZNetView
- [ ] Performance: FPS stable with 10+ water bodies visible simultaneously
- [ ] `va_clear_auto_water` — all VFX cleaned up, no orphaned particles/audio

---

## Performance Notes

- All `ParticleSystem` instances are culled by Unity's renderer when off-screen — zero CPU cost
- `AudioSource` uses linear rolloff — silent beyond max distance
- Shoreline detection runs once per mesh regeneration (not per-frame)
- Audio clip search runs once per session (static cache)
- Particle material created once (static cache)
- All static caches reset on `ClearAllCombiners()` (logout/world change)

---

## Known Limitations

- Audio clips depend on Valheim having suitable clips loaded; may be null if prefabs not yet loaded
- Particle appearance is procedural (no texture) — looks like white misty specks, not detailed foam
- No flow direction on surface debris (would require shader access for UV scrolling)
- Shoreline emitter positions are grid-aligned (snapped to cell centers, not smooth contours)

---

## Terrain Clip Pass Fix (added post-VFX)

### Problem
River water extended as a flat sheet through hillsides. The rectangular combiner grid
filled the entire bounding box — cells on steep valley walls got surface Y from nearby
river disks even though terrain was meters above the water. Existing passes
(`GravitySettleEdges`, `NormalizeChannelDepth`) only handled edge cases and bank slopes,
not interior cells where terrain rises above the water plane.

### Root Cause
No pass existed to simply say "if terrain > water surface here, remove this cell."
The bank extension logic only converts such cells to below-terrain water for clipping,
not removing them. `FillSurfaceGaps` could even re-introduce killed cells. Smoothing
would then blur valid water into hillside cells.

### Fix: `TerrainClipPass()`
Added as Step 4.7 (sync) / Step 2.7 (async), **after** `NormalizeChannelDepth` and
**before** `SmoothWaterSurface`. For every grid cell:
- If terrain > surfaceY + 0.05m ? kill cell (set to -100)
- Skip bank extensions (surfaceY < terrainY, intentional below-terrain mesh)

### Files Modified

| File | Change |
|------|--------|
| `Core/WaterMeshCombiner.MeshGen.cs` | Added `TerrainClipPass()` method + call in sync `RegenerateMesh()` (Step 4.7) and async fallback path |
| `Core/WaterAsyncMeshBuilder.cs` | Added static `TerrainClipPass()` method + call in `ProcessWaterMeshJob()` (Step 2.7) |

### How to Revert
Remove the `TerrainClipPass()` method from both files and remove the call sites:
- `WaterMeshCombiner.MeshGen.cs`: Remove `TerrainClipPass();` between `NormalizeChannelDepth()` and `SmoothWaterSurface()` (2 locations: sync + async fallback)
- `WaterAsyncMeshBuilder.cs`: Remove `TerrainClipPass(surfaceHeights, input);` between `NormalizeChannelDepth` and `SmoothSurface` (1 location)
- Remove the `TerrainClipPass` method definitions from both files

---

## River Surface Height Pipeline Overhaul

### Date: 2025 (post-TerrainClipPass)

### Problem
Rivers appeared as flat water sheets cutting through hillsides, with inconsistent surface
heights along the river path. Multiple interacting issues:

1. **Dual surface computation conflict**: `TraceCenterline` pre-computed `SurfaceY` using a
   `channelFillFraction = 0.65` formula. `DropRiverToTerrain` then computed ANOTHER surface
   using the "spoke" model (bed + 1.5m). Taking `min(both)` produced unpredictable results.

2. **Bank detection found single lowest across both sides**: A gulch on one side of the
   river would pull the bank height down to the gulch floor, constraining the entire water
   level at that cross-section. Should find lowest per-side, then use the lower of the two.

3. **Cross-section bed clamp defeated itself**: `SampleCrossSectionBed` computed a weighted
   bed height across the channel center, then clamped `if (weightedBed < centerHeight)`
   which always returned centerHeight — making the entire scan useless.

4. **Monotonic flow direction used SurfaceY**: After spoke + bank cap computation, a narrow
   gorge could make an upstream point have LOWER `SurfaceY` than downstream, flipping flow.
   Should use raw `TerrainY` (bed elevation) which always decreases downstream.

5. **Phase 4 re-clamped independently**: Wider overlay disks were re-clamped to real terrain
   with a different formula than Phase 3, creating height discontinuities.

6. **No center-out ordering**: Flood-fill started at pixel index 0 (top-left corner), so
   edge-of-world rivers were processed first. When MaxPaths clipped the list, central
   rivers were trimmed.

### Fix Summary

| File | Change |
|------|--------|
| `BCHeightmapWaterDetector.cs` | `TraceCenterline` now sets `SurfaceY = 0` (raw data only — bed, bank, depression depth). `DropRiverToTerrain` is the SINGLE AUTHORITY for surface computation. |
| `BCHeightmapWaterDetector.cs` | Bank detection finds lowest per-side independently (`leftBankVal`, `rightBankVal`), then uses `Math.Min(left, right)`. |
| `BCHeightmapWaterDetector.cs` | `DetectWaterBodies` sorts results center-out (by pixel distance from heightmap center) before returning. |
| `AutoWaterGenerator.cs` | `DropRiverToTerrain` Step 1: removed conflicting `pt.SurfaceY` comparison — spoke model is now the sole authority. |
| `AutoWaterGenerator.cs` | `SampleCrossSectionBed`: removed floor clamp (`if weighted < center ? center`). Scan extent widened from ±15% to ±20%. Bed can now be lower than center sample (that's the riverbed). |
| `AutoWaterGenerator.cs` | Step 4 monotonic flow uses `TerrainY` (raw bed elevation) not `SurfaceY` to determine upstream vs downstream. |
| `AutoWaterGenerator.cs` | Phase 4 overlay disks use the SAME `pt.SurfaceY` as Phase 3 — no independent re-clamping. |

### How to Revert

**`BCHeightmapWaterDetector.cs` — TraceCenterline:**
- Re-add `channelFillFraction = 0.65f` and `minBedOffset = 0.15f` constants
- Restore proportional fill computation for `SurfaceY` (was: `bedY + channelDepth * channelFillFraction`)
- Restore single `bankPixelVal` with `float.MinValue` init and `< -0.5f` check

**`AutoWaterGenerator.cs` — DropRiverToTerrain:**
- Re-add the `if (pt.SurfaceY > 0f && pt.SurfaceY != pt.TerrainY)` block at end of Step 1
- Restore `SampleCrossSectionBed` floor clamp: `if (weightedBed < centerHeight) weightedBed = centerHeight`
- Restore Phase 4 independent re-clamp block with `WorldGenerator.instance.GetHeight()` calls
- Change Step 4 back to using `SurfaceY` for upstream detection

### How the Pipeline Works Now (End-to-End)

```
BCHeightmapWaterDetector.TraceCenterline()
  ? Raw data: TerrainY (bed from heightmap), BankY (per-side bank from heightmap),
    DepressionDepth (meters below banks), Width (pixel extent), SurfaceY = 0
  
AutoWaterGenerator.DropRiverToTerrain()   [SINGLE AUTHORITY for surface]
  Step 1: Cross-reference heightmap vs real terrain
    bedY = min(heightmap TerrainY, SampleCrossSectionBed(real terrain))
    bankY = min(heightmap BankY, SampleBankHeights(real terrain))
    surfaceY = min(bedY + SPOKE_HEIGHT, bankY - BANK_MARGIN)
    surfaceY = max(surfaceY, centerTerrain + MIN_BED_OFFSET)
  
  Step 2: Cumulative path distance
  
  Step 3: 5-point rolling average smoothing (3 passes, 0.4 strength)
    ? removes terrain noise jitter, re-enforces bank cap + floor after each pass
  
  Step 4: Monotonic flow enforcement (using TerrainY for direction)
    ? surface never rises going downstream
  
  Step 5: Final safety clamps (depth cap, bank cap, center floor)
  
  Step 6: Ocean blend at downstream end (50m transition to waterLevel)

Phase 3: Place FULL-SIZE disks (1.0x width) at computed SurfaceY
  ? Combiner's TerrainClipPass handles bank/terrain clipping
  ? Full-size disks ensure dense overlap so surface stays smooth at low spots
Phase 4: Coarse infill pass — full-size disks at wider spacing for gap coverage

WaterMeshCombiner.TerrainClipPass()
  ? Kills any grid cell where terrain > surfaceY + 0.05m
  ? Prevents floating water sheets on hillsides
```
