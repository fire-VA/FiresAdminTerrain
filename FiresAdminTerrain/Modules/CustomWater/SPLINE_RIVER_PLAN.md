# Spline-Based River Generation Plan

## Overview

Upgrade the `AutoWaterGenerator` disk placement to use spline-based data points
with adaptive disk sizing for natural-looking rivers.

## Current System

`DropRiverToTerrain` computes surface heights using:
1. **Spoke model**: bed + 1.5m, capped by banks
2. **Cross-section sampling**: perpendicular terrain scan for bed/bank
3. **Monotonic flow**: two-pass envelope for smooth downhill
4. **Phase 3**: Places disks at `radius = pt.Width * 0.55f` (fixed proportion)
5. **Phase 4**: Coarse infill at 6-10m intervals

## Proposed Improvements

### 1. Spline Data Points

Instead of raw PathPoint arrays, generate cubic spline control points:
- Each control point stores: position, width, surfaceY, tangent
- Spline interpolation gives smooth curves between points
- Curvature at each point determines disk density

### 2. Adaptive Disk Sizing

**Key idea**: More, smaller disks on steep terrain for smooth surface tracking.

```
For each spline segment:
  curvature = abs(d²Y/ds²)  // second derivative of surface height
  slope = abs(dY/ds)          // first derivative

  if slope > 0.05:  // steep — more than 5% grade
    diskRadius = min(channelWidth * 0.4, 3m)   // small disks
    diskSpacing = diskRadius * 0.5              // tight overlap
  elif slope > 0.02:  // moderate
    diskRadius = channelWidth * 0.5
    diskSpacing = diskRadius * 0.6
  else:  // flat
    diskRadius = channelWidth * 0.6             // larger disks
    diskSpacing = diskRadius * 0.7              // wider spacing
```

### 3. Bank Clipping

Disks near banks should be trimmed or offset inward:
- Sample terrain at disk edge points along the bank direction
- If terrain rises above water surface at disk edge, shrink radius
- This creates natural-looking tapered edges at the shoreline

### 4. Mesh Blending

The existing `WaterMeshCombiner` already handles:
- Height interpolation between overlapping disks
- Terrain clip pass (drops vertices below terrain)
- Surface smoothing passes

No changes needed to the combiner — just better input data.

## Implementation Steps

| Step | Task | Status | Risk |
|------|------|--------|------|
| 1 | Slope-adaptive disk sizing in Phase 3 | ? DONE | Low |
| 2 | Distance-based spacing (skip redundant flat points) | ? DONE | Low |
| 3 | Clarify water anchor logging (prefab name in logs) | ? DONE | Low |
| 4 | Add bank-edge shrinking to Phase 3 disk creation | Pending | Medium |
| 5 | Test with BC HeightmapOverrideAll world | Pending | — |

## Files Modified

- `Modules/CustomWater/River/AutoWaterGenerator.cs` — Phase 3 disk placement
  - Slope computation from SurfaceY using 5-point window
  - Adaptive radius: 40% width (steep) ? 55% width (flat)
  - Adaptive spacing: 45% radius (steep) ? 65% radius (flat)
  - Distance-based placement skips redundant points on flat terrain
- `Modules/CustomTerrain/Core/DirtFloorController.cs` — Clarified legacy comment
- `Modules/CustomWater/Core/WaterPersistence.cs` — Prefab name in save log
- `Modules/CustomWater/Core/WaterMeshCombinerAnchor.cs` — Prefab name in conversion log

## ZDO Persistence Note

Water anchors correctly use `CustomRiver` prefab (not dirtfloor).
See `WaterMeshCombinerAnchor.BASE_PREFAB_NAME = "CustomRiver"`.

**Conversion paths:**
- `WaterPaintController.Awake` ? `CheckAndConvertFromWaterPaint` — **PRIMARY** path for
  `CustomRiver`-based anchors (current system)
- `DirtFloorController.Awake` ? `CheckAndConvertToWaterAnchor` — **LEGACY FALLBACK** for
  old saves that used `piece_dirtfloor_vad` as the anchor prefab

The logout save correctly iterates all `WaterMeshCombiner` instances and calls
`SaveToAnchor()` which writes to the anchor's ZDO. The anchor's prefab is always
`CustomRiver` (see `FindPrefab()`) — it never saves to dirtfloor ZDOs.
