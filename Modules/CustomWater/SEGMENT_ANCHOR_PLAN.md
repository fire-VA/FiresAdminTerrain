# Water Segment Anchor Architecture Plan

## Date: 2025-07-XX
## Status: IMPLEMENTED

---

## Problem Statement

The current water persistence architecture has a fundamental flaw for large water bodies
(rivers, coastlines, large lakes):

**ONE anchor ZDO per combiner, regardless of combiner size.**

A river combiner can span 2000m+ across dozens of zones, but its single anchor ZDO sits
at ONE position. This causes three cascading failures:

### Failure 1: Saves Fail When Anchor Zone Is Unloaded

```
Player mops water at position X (2000m from anchor)
  ? combiner.SaveToAnchor()
  ? anchor._nview.IsOwner() ? FALSE (anchor's zone not loaded)
  ? save silently skipped
  ? edit lost on reload
```

The player legitimately edits water near them, but the anchor is in a distant unloaded zone.
The edit cannot be persisted because Valheim only loads ZDOs for nearby zones.

### Failure 2: ZDO Byte Array Size Limits

Valheim ZDOs have practical size limits for byte arrays:
- **~10KB reliable** per byte array key
- **~64KB theoretical max** per ZDO total
- Large rivers exceed this easily:

```
River with 500 disks, grid 200x200:
  va_water_surface_heights: 200×200×4 bytes = 160KB (EXCEEDS LIMIT)
  va_water_terrain_heights: 200×200×4 bytes = 160KB (EXCEEDS LIMIT)
  va_water_disks:           500×28 bytes    = 14KB
  va_water_grid_data:       ~50 bytes
  Total: ~334KB ? FAR exceeds ZDO limits ? data truncated or lost
```

Even with GZip compression (typically 2-4x ratio for float arrays), this is 80-170KB per ZDO.

### Failure 3: Client-Side Restoration Fails

ZDO byte arrays only sync to the ZDO owner. On clients:
- `va_is_water_combiner_anchor = true` syncs (bool, tiny)
- `va_water_combiner_version = 2` syncs (int, tiny)
- `va_water_real_position` syncs (Vector3, small)
- `va_water_grid_data` ? **empty** (byte array, not synced)
- `va_water_surface_heights` ? **empty** (byte array, not synced)
- `va_water_disks` ? **empty** (byte array, not synced)

The client sees the anchor exists but has zero data to restore from.
The file-based `WaterCombinerCache` only exists on the machine that generated the water
(typically the server), so clients have no cache either.

**This is the root cause of the "water generated on server but invisible on client" bug.**

---

## Solution: Segment Anchors

Split each combiner's data across **multiple child anchors** placed along the water body's
extent. Each segment anchor:
1. Sits in a **different zone** along the river/lake
2. Stores a **portion** of the grid data (within ZDO size limits)
3. Is **independently loadable** — any segment near the player can save/restore
4. Together reconstruct the **full combiner** state

### Architecture

```
Before (current):
  ???????????????????????????????????????????????????????????
  ? WaterMeshCombiner (spans 2000m, grid 200×200)           ?
  ?                                                          ?
  ?   ????????????                                           ?
  ?   ? Anchor   ? ? Single ZDO at one position              ?
  ?   ? (ALL data?   ~334KB of byte arrays                   ?
  ?   ?  in one  ?   Only works when THIS zone is loaded     ?
  ?   ?  ZDO)    ?                                           ?
  ?   ????????????                                           ?
  ???????????????????????????????????????????????????????????

After (segment anchors):
  ???????????????????????????????????????????????????????????
  ? WaterMeshCombiner (spans 2000m, grid 200×200)           ?
  ?                                                          ?
  ?   ????????????  ????????????  ????????????  ?????????? ?
  ?   ? Primary  ?  ? Segment  ?  ? Segment  ?  ?Segment ? ?
  ?   ? Anchor   ?  ? Anchor 1 ?  ? Anchor 2 ?  ?Anchor 3? ?
  ?   ? (header  ?  ? (rows    ?  ? (rows    ?  ?(rows   ? ?
  ?   ?  + disks ?  ?  0-49)   ?  ?  50-99)  ?  ?100-199)? ?
  ?   ?  + meta) ?  ?  ~8KB    ?  ?  ~8KB    ?  ? ~16KB) ? ?
  ?   ????????????  ????????????  ????????????  ?????????? ?
  ?   Zone (5,10)   Zone (7,10)   Zone (9,10)   Zone(11,10)?
  ???????????????????????????????????????????????????????????
```

### Data Distribution

**Primary Anchor** (existing anchor, minimal changes):
- `va_is_water_combiner_anchor = true`
- `va_water_combiner_version = 3` (bumped)
- `va_water_real_position` (Vector3)
- `va_water_grid_data` (grid config: width, height, cellSize, etc.)
- `va_water_bounds` (combined bounds)
- `va_water_disks` (all disk positions — small, ~14KB for 500 disks)
- `va_water_liquid_type`, `va_water_wave_intensity`, etc.
- **NEW:** `va_water_segment_count = N` (int — number of segment anchors)
- **NEW:** `va_water_segment_zdoids` (byte[] — ZDOID list of all segments)
- **REMOVED:** `va_water_surface_heights` (moved to segments)
- **REMOVED:** `va_water_terrain_heights` (moved to segments)

**Segment Anchors** (new child anchors):
- `va_is_water_segment = true` (bool)
- `va_water_segment_index = i` (int — which segment this is)
- `va_water_segment_parent_zdoid` (ZDOID — links back to primary)
- `va_water_segment_row_start = S` (int — first row stored in this segment)
- `va_water_segment_row_count = C` (int — number of rows)
- `va_water_segment_surface` (byte[] — compressed surface heights for rows S..S+C-1)
- `va_water_segment_terrain` (byte[] — compressed terrain heights for rows S..S+C-1)

### Segment Sizing

Target **?8KB per segment after compression** (well within ZDO limits):
- Raw float data per row: `gridWidth × 4 bytes × 2 arrays` (surface + terrain)
- For gridWidth=200: `200 × 4 × 2 = 1600 bytes/row` raw
- GZip typically achieves 2-4x compression on float arrays ? ~400-800 bytes/row compressed
- **~10-20 rows per segment** stays under 8KB
- Grid 200×200 ? 10-20 segments (manageable)

### Segment Placement

Segments are placed at **evenly spaced positions along the combiner's bounds**:

```csharp
// Place segments every ~128m along the longer axis (2 Valheim zones)
float segmentSpacing = 128f;  // 2 zones wide
float combinerLength = Mathf.Max(bounds.size.x, bounds.size.z);
int segmentCount = Mathf.CeilToInt(combinerLength / segmentSpacing);
segmentCount = Mathf.Clamp(segmentCount, 1, MAX_SEGMENTS);

// Calculate rows per segment
int rowsPerSegment = Mathf.CeilToInt((float)gridHeight / segmentCount);

for (int i = 0; i < segmentCount; i++)
{
    // Position the segment anchor along the combiner's extent
    float t = (float)i / (segmentCount - 1);
    Vector3 segmentPos = Vector3.Lerp(boundsMin, boundsMax, t);
    segmentPos.y = ANCHOR_HIDDEN_Y; // Underground
    
    // Create segment anchor ZDO at this position
    var segmentAnchor = CreateSegmentAnchor(segmentPos, i, rowStart, rowCount);
}
```

This ensures at least one segment anchor is in a loaded zone near the player at all times.

---

## API Design

### `WaterMeshCombinerAnchor` Changes

```csharp
public partial class WaterMeshCombinerAnchor
{
    // NEW: Segment management
    private List<WaterSegmentAnchor> _segments = new List<WaterSegmentAnchor>();
    private int _segmentCount;
    
    /// <summary>
    /// Saves combiner data distributed across segment anchors.
    /// Grid heights are split by row ranges; each segment stores its portion.
    /// </summary>
    public void SaveToZDO()
    {
        // Save header data to primary anchor (same as before, minus height arrays)
        SaveHeaderData(zdo);
        SaveWaterDisks(zdo);
        SaveGridData(zdo);
        SaveBoundsData(zdo);
        
        // Calculate segment layout
        int segmentCount = CalculateSegmentCount();
        int rowsPerSegment = Mathf.CeilToInt((float)_combiner.GridHeight / segmentCount);
        
        // Ensure segment anchors exist
        EnsureSegmentAnchors(segmentCount);
        
        // Distribute height data across segments
        for (int i = 0; i < segmentCount; i++)
        {
            int rowStart = i * rowsPerSegment;
            int rowCount = Mathf.Min(rowsPerSegment, _combiner.GridHeight - rowStart);
            _segments[i].SaveSegmentData(rowStart, rowCount, _combiner);
        }
        
        // Save segment ZDOIDs to primary anchor for reassembly
        SaveSegmentIndex(zdo, _segments);
    }
    
    /// <summary>
    /// Restores combiner by collecting data from all available segment anchors.
    /// Missing segments (unloaded zones) are filled with regenerated data on demand.
    /// </summary>
    private IEnumerator RestoreFromZDOAsync()
    {
        // Load header from primary anchor
        LoadHeaderData(zdo);
        LoadWaterDisks(zdo);
        LoadGridData(zdo);
        LoadBoundsData(zdo);
        
        // Load segment index
        var segmentZdoids = LoadSegmentIndex(zdo);
        
        // Collect available segment data
        float[] surfaceHeights = new float[gridWidth * gridHeight];
        float[] terrainHeights = new float[gridWidth * gridHeight];
        bool[] rowsLoaded = new bool[gridHeight];
        
        foreach (var segmentZdoid in segmentZdoids)
        {
            var segmentZdo = ZDOMan.instance.GetZDO(segmentZdoid);
            if (segmentZdo == null) continue; // Segment's zone not loaded yet
            
            int rowStart = segmentZdo.GetInt(s_segmentRowStartHash);
            int rowCount = segmentZdo.GetInt(s_segmentRowCountHash);
            
            // Load this segment's height data
            LoadSegmentHeights(segmentZdo, surfaceHeights, terrainHeights, 
                              rowStart, rowCount, gridWidth);
            
            for (int r = rowStart; r < rowStart + rowCount; r++)
                rowsLoaded[r] = true;
        }
        
        // For any missing rows, regenerate from disk data
        // (This handles the case where some segment zones aren't loaded yet)
        if (HasMissingRows(rowsLoaded) && _combiner.WaterDisks.Count > 0)
        {
            yield return RegenerateMissingRows(surfaceHeights, terrainHeights, 
                                                rowsLoaded, gridWidth, gridHeight);
        }
        
        // Apply data to combiner
        _combiner.SetSurfaceHeights(surfaceHeights);
        _combiner.SetTerrainHeights(terrainHeights);
        _combiner.RestoreFromAnchorData();
    }
}
```

### New `WaterSegmentAnchor` Class

```csharp
/// <summary>
/// A child anchor ZDO that stores a portion of a water combiner's grid data.
/// 
/// Segment anchors are placed along the water body's extent so that at least
/// one is always in a loaded zone near the player. Each segment stores a
/// contiguous range of grid rows (surface + terrain heights).
/// 
/// Segment anchors use the same CustomRiver prefab as the primary anchor.
/// They are invisible (Y=-10) and have no combiner of their own.
/// </summary>
public class WaterSegmentAnchor : MonoBehaviour
{
    private ZNetView _nview;
    
    // ZDO keys
    private static readonly int s_isSegmentHash = "va_is_water_segment".GetStableHashCode();
    private static readonly int s_segmentIndexHash = "va_water_segment_index".GetStableHashCode();
    private static readonly int s_segmentParentHash = "va_water_segment_parent".GetStableHashCode();
    private static readonly int s_segmentRowStartHash = "va_water_segment_row_start".GetStableHashCode();
    private static readonly int s_segmentRowCountHash = "va_water_segment_row_count".GetStableHashCode();
    private static readonly int s_segmentSurfaceHash = "va_water_segment_surface".GetStableHashCode();
    private static readonly int s_segmentTerrainHash = "va_water_segment_terrain".GetStableHashCode();
    
    public void SaveSegmentData(int rowStart, int rowCount, WaterMeshCombiner combiner);
    public bool LoadSegmentData(out int rowStart, out int rowCount, 
                                out float[] surfaceRows, out float[] terrainRows);
}
```

---

## Save Flow

```
combiner.SaveToAnchor()
  ?
  ?? Primary Anchor ZDO (at combiner center)
  ?   ?? va_water_grid_data (gridW, gridH, cellSize, settled, isPond)
  ?   ?? va_water_disks (all disk positions, compressed)
  ?   ?? va_water_bounds (min, max)
  ?   ?? va_water_liquid_type, va_water_wave_intensity, etc.
  ?   ?? va_water_segment_count = 4
  ?   ?? va_water_segment_zdoids = [zdoid0, zdoid1, zdoid2, zdoid3]
  ?
  ?? Segment 0 ZDO (at western end of river)
  ?   ?? va_is_water_segment = true
  ?   ?? va_water_segment_index = 0
  ?   ?? va_water_segment_row_start = 0
  ?   ?? va_water_segment_row_count = 50
  ?   ?? va_water_segment_surface = compressed[50 × gridWidth floats]
  ?   ?? va_water_segment_terrain = compressed[50 × gridWidth floats]
  ?
  ?? Segment 1 ZDO (at center-west)
  ?   ?? ... rows 50-99 ...
  ?
  ?? Segment 2 ZDO (at center-east)
  ?   ?? ... rows 100-149 ...
  ?
  ?? Segment 3 ZDO (at eastern end of river)
      ?? ... rows 150-199 ...
```

---

## Restore Flow

```
WaterMeshCombinerAnchor.Start()
  ?
  ?? Check file cache (fast path) ? if hit, skip ZDO
  ?
  ?? Load primary anchor ZDO
  ?   ?? Read grid config, bounds, disks, segment count
  ?   ?? Read segment ZDOID list
  ?
  ?? For each segment ZDOID:
  ?   ?? Try ZDOMan.instance.GetZDO(zdoid)
  ?   ?? If loaded ? read row range + height data
  ?   ?? If NOT loaded ? mark rows as missing
  ?
  ?? If missing rows > 0 AND disks available:
  ?   ?? Regenerate missing rows from disk data (terrain sampling)
  ?       This handles the case where the player is near one end of a
  ?       river and segment anchors at the other end aren't loaded yet.
  ?       The regenerated data won't be pixel-perfect (no mop edits) but
  ?       the water will be visible and functional.
  ?
  ?? Apply to combiner ? RestoreFromAnchorData() ? mesh visible
```

### Progressive Restoration

As the player moves along a river and more segments load:

```
Frame N:   Segments 0,1 loaded ? rows 0-99 from ZDO, rows 100-199 regenerated
Frame N+k: Segment 2 loads ? rows 100-149 replaced with ZDO data (preserves mop edits)
Frame N+m: Segment 3 loads ? rows 150-199 replaced with ZDO data
           ? Full fidelity restoration complete
```

This progressive approach means:
1. Water is **always visible** near the player (regenerated from disks if needed)
2. **Mop edits are preserved** for segments that are loaded
3. **Distant segments** load progressively as the player moves along the river

---

## Handling Saves at Any Position

With segment anchors, saves work regardless of where the player edits:

```
Player mops water at position X (near Segment 2)
  ? combiner.SaveToAnchor()
  ? Primary anchor may or may not be loaded — doesn't matter for height data
  ? Find which segment covers the modified rows
  ? Segment 2 IS loaded (player is near it!) ? save succeeds
  ? Only the affected segment's ZDO is written (small, fast)
```

**Optimization: Partial saves.** Instead of saving ALL segments on every edit,
only save segments whose rows were modified. Track dirty row ranges.

```csharp
private HashSet<int> _dirtySegments = new HashSet<int>();

public void MopWaterAt(Vector3 worldPos, float radius)
{
    // ... modify surface heights ...
    
    // Mark affected segments dirty
    int rowStart = ...; // first modified row
    int rowEnd = ...;   // last modified row
    for (int i = 0; i < _segmentCount; i++)
    {
        if (SegmentOverlapsRows(i, rowStart, rowEnd))
            _dirtySegments.Add(i);
    }
}

public void SaveToAnchor()
{
    // Only save dirty segments (not all of them)
    foreach (int segIdx in _dirtySegments)
    {
        if (_segments[segIdx] != null && _segments[segIdx].IsLoaded)
            _segments[segIdx].SaveSegmentData(...);
    }
    _dirtySegments.Clear();
    
    // Primary anchor header is always saved (disk list may have changed)
    SaveHeaderToZDO();
}
```

---

## Client-Side Fix

Segment anchors also fix the client-side water invisibility bug:

**Before:** Client gets primary anchor ZDO with version=2, but ALL byte arrays are empty
(ZDO byte arrays don't sync to non-owners). No data ? no water.

**After:** Segments are placed in zones near the player. When the client's zone loads,
Valheim instantiates the segment prefab and the client **becomes the owner** of that
zone's ZDOs (Valheim assigns ownership to the closest peer). As owner, the client
CAN read the byte arrays.

Even if the client doesn't own all segments, it has the disk positions from the primary
anchor (or can regenerate from WorldGenerator river data) and fills missing segments
with terrain-sampled data. **Water is always visible.**

Additionally, the `WaterCombinerCache` now saves on BOTH server and client after
successful restoration, so subsequent loads use the fast cache path.

---

## Migration: v2 ? v3

When an anchor with `va_water_combiner_version = 2` loads:
1. Detect v2 format (no segment count field)
2. Load ALL data from primary ZDO (old format — monolithic)
3. After successful load, convert to v3:
   - Create segment anchors along the combiner's bounds
   - Distribute height data across segments
   - Save segment ZDOIDs to primary anchor
   - Bump version to 3
   - Clear old monolithic height arrays from primary ZDO

This is transparent — old worlds auto-upgrade on first load.

---

## Small Water Bodies (No Segmentation Needed)

For small water bodies (ponds, short streams) where the entire grid fits in one ZDO:
- `va_water_segment_count = 0` ? use legacy monolithic storage
- Heights stay in the primary anchor ZDO
- No segment anchors created
- Threshold: `gridWidth × gridHeight × 8 < 8192` (8KB uncompressed)
  ? grid ? ~32×32 (1024 cells) uses monolithic
  ? grid > 32×32 uses segments

---

## Implementation Phases

### Phase 1: `WaterSegmentAnchor` Class (NEW FILE) ? DONE
- Create `Modules/CustomWater/Core/WaterSegmentAnchor.cs`
- ZDO read/write for segment data
- Prefab reuse (same CustomRiver prefab)
- Awake/Start deduplication (skip restoration — segments don't create combiners)

### Phase 2: Modify `WaterMeshCombinerAnchor.SaveToZDO()` ? DONE
- Calculate segment count from grid size
- Create/reuse segment anchor ZDOs
- Distribute rows across segments
- Save segment ZDOID index to primary anchor
- Support partial saves (dirty segments only)

### Phase 3: Modify `WaterMeshCombinerAnchor.RestoreFromZDOAsync()` ? DONE
- Read segment ZDOID index from primary anchor
- Collect available segment data
- Handle missing segments (regenerate from disks)
- Progressive restoration as segments load

### Phase 4: Version Migration (v2 ? v3) ? DONE
- Detect old format in `RestoreFromZDOAsync`
- Auto-convert to segmented format on first save

### Phase 5: Client-Side Cache Improvements ? DONE
- After successful restoration, save to `WaterCombinerCache` on client
- On subsequent loads, client uses cache (fast, always available)
- Cache invalidation when segments update

### Phase 6: Dirty Segment Tracking for Partial Saves ? DONE
- Track which grid rows were modified
- Only save affected segments
- Reduces ZDO write overhead for mop operations

---

## Files Summary

### New Files
| File | Purpose |
|------|---------|
| `Modules/CustomWater/Core/WaterSegmentAnchor.cs` | Segment anchor component |
| `Modules/CustomWater/SEGMENT_ANCHOR_PLAN.md` | This document |

### Modified Files
| File | Changes |
|------|---------|
| `WaterMeshCombinerAnchor.cs` | Segment creation/management, v3 save/restore |
| `WaterMeshCombiner.cs` | Dirty row tracking for partial saves |
| `WaterMeshCombiner.MeshGen.cs` | Progressive segment loading during async regen |
| `WaterCombinerCache.cs` | Client-side cache after segment restoration |

---

## Constraints

- **.NET Framework 4.8** — no async/await, use coroutines + ThreadPool
- **Unity Mono** — Thread, Monitor, lock are safe
- **ZDO size limit** — target ?8KB per segment byte array after compression
- **Segment count** — max ~20 segments per combiner (avoid ZDO spam)
- **CustomRiver prefab** — reuse for segments (already registered, piece layer)
- **Backward compatible** — v2 anchors auto-upgrade to v3 on first load
