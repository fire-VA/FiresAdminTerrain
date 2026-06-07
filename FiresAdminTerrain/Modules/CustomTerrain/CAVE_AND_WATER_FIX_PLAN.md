# Cave Detection & Water Boat Support Fix Plan

## Status: ? IMPLEMENTED

---

## Overview

This document tracks the fixes for:
1. Cave detection not working (player under custom terrain not detected)
2. Environment effects not being applied when under terrain
3. Admin interaction on underside mesh for environment settings
4. Water surface shape for boat navigation (river channels)
5. Disabling automatic VFX (too inaccurate)

---

## Issue 1: Cave Detection Not Working ? FIXED

### Problem
The `TerrainCaveDetector` raycasts UP from the player to find custom terrain ceiling, but:
- The underside mesh (`Underside` child object) had NO collider
- The main mesh's collider faces UP, so raycasts from below don't hit it
- Result: Player was never detected as being "under" terrain

### Solution Implemented
Added `MeshCollider` to the underside mesh in `DirtFloorMeshCombiner.Underside.cs`:

```csharp
// In SetupUndersideComponents():
_undersideCollider = _undersideGO.AddComponent<MeshCollider>();
_undersideCollider.convex = false; // Non-convex for accurate raycasts

// In GenerateUndersideMesh():
if (_undersideCollider != null)
{
    _undersideCollider.sharedMesh = null; // Clear first to force rebuild
    _undersideCollider.sharedMesh = _undersideMesh;
}
```

### Implementation Status
- [x] Added MeshCollider to underside mesh
- [x] Updated GenerateUndersideMesh() to also update collider
- [x] Underside has correct layer (same as parent)

---

## Issue 2: Environment Effects - Already Working ?

### Solution
The `TerrainCaveDetector` already uses `GetComponentInParent<DirtFloorMeshCombiner>()` 
which correctly finds the parent combiner from the underside child hit.

---

## Issue 3: Admin Interaction on Underside ? FIXED

### Problem
Admins needed to interact with cave ceilings from below to change environment settings.
The hit object is the Underside child, not the parent with TerrainCaveEnvironment.

### Solution Implemented
Created `UndersideInteractionProxy` class that forwards Hoverable/Interactable to parent:

```csharp
public class UndersideInteractionProxy : MonoBehaviour, Hoverable, Interactable
{
    private DirtFloorMeshCombiner _parentCombiner;
    
    public void Initialize(DirtFloorMeshCombiner combiner);
    public string GetHoverText(); // Forwards to TerrainCaveEnvironment
    public bool Interact(Humanoid user, bool hold, bool alt); // Forwards to TerrainCaveEnvironment
}
```

Added to underside in `SetupUndersideComponents()`:
```csharp
var interactionProxy = _undersideGO.AddComponent<UndersideInteractionProxy>();
interactionProxy.Initialize(this);
```

### Implementation Status
- [x] Created UndersideInteractionProxy class
- [x] Added to underside in SetupUndersideComponents()

---

## Issue 4: Water Surface for Boat Navigation ? FIXED

### Problem
Water surface for rivers needed:
1. Edges that curl DOWN toward terrain (already working)
2. A slight concave CENTER channel to guide boats
3. Smooth transitions at edges

### Solution Implemented
Added center channel depression in `WaterMeshCombiner.MeshGen.cs`:

```csharp
// New constants:
private const float CENTER_CHANNEL_DEPTH = 0.15f; // 15cm depression at center
private const float CENTER_CHANNEL_RADIUS_FRACTION = 0.5f; // Channel covers center 50%

// In CalculateSurfaceWithEdgeCurl():
if (CENTER_CHANNEL_DEPTH > 0f && dist < cached.CenterChannelRadius)
{
    float channelFactor = 1f - (dist / cached.CenterChannelRadius);
    channelFactor = channelFactor * channelFactor; // Smooth easing
    float channelDepression = channelFactor * CENTER_CHANNEL_DEPTH;
    curledY -= channelDepression;
}
```

### Implementation Status
- [x] Added center channel depression calculation
- [x] Channel depth configurable (default 0.15m)
- [x] Added to CachedWaterDiskData struct

---

## Issue 5: Disable Automatic VFX ? FIXED

### Problem
`WaterRapidsVFXManager` spawned fog/splash VFX at cliff edges with inaccurate placement.

### Solution Implemented
Added global toggle in `WaterRapidsVFXManager.cs`:

```csharp
/// <summary>
/// Master toggle for automatic VFX spawning.
/// DISABLED by default - VFX placement is too inaccurate.
/// </summary>
public static bool AutoVFXEnabled = false;

public void UpdateVFXForCombiner(WaterMeshCombiner combiner)
{
    if (!AutoVFXEnabled)
    {
        _vfxContainers.Remove(combiner);
        _vfxCounts.Remove(combiner);
        return;
    }
    // ... rest of method
}
```

### Implementation Status
- [x] Added AutoVFXEnabled toggle (default false)
- [x] Skip VFX spawning when disabled

---

## Files Modified

| File | Changes |
|------|---------|
| `DirtFloorMeshCombiner.Underside.cs` | Added MeshCollider, created UndersideInteractionProxy |
| `WaterMeshCombiner.MeshGen.cs` | Added center channel depression for boats |
| `WaterRapidsVFXManager.cs` | Added AutoVFXEnabled toggle (default false) |

---

## Testing Checklist

### Cave Detection
- [ ] Walk under custom terrain ceiling
- [ ] Console shows "Player is now under terrain"
- [ ] Environment changes (if ForceEnvironment enabled)
- [ ] Walk out from under terrain
- [ ] Console shows "Player exited from under terrain"
- [ ] Environment restores

### Admin Interaction
- [ ] As admin, look at underside of ceiling
- [ ] Hover text appears with environment settings
- [ ] Press E to cycle environment
- [ ] Press Shift+E to toggle lighting
- [ ] Press Ctrl+E to toggle fog
- [ ] Press Alt+E to toggle force environment
- [ ] Settings persist after logout/login

### Water Boat Navigation
- [ ] Place river water (connected water disks)
- [ ] Get in boat
- [ ] Sail down the river
- [ ] Boat doesn't get stuck at edges
- [ ] Boat naturally follows center channel

### VFX Disabled
- [ ] Place water with elevation change (waterfall)
- [ ] NO automatic VFX spawns
- [ ] Console does not spam VFX creation messages

---

## Notes

- Cave detection runs every 0.2 seconds to avoid performance issues
- Environment effects are CLIENT-SIDE only (each player sees their own)
- Boat channel depth (0.15m) is subtle - should not be visually obvious
- VFX can be re-enabled by setting `WaterRapidsVFXManager.AutoVFXEnabled = true`
- TerrainCaveDetector is initialized in `ZoneSystem_Start_Postfix` via `TerrainCaveDetector.EnsureExists()`

---

## Progress Log

| Date | Change | Status |
|------|--------|--------|
| 2025-XX-XX | Created plan document | ? |
| 2025-XX-XX | Added underside MeshCollider | ? |
| 2025-XX-XX | Added UndersideInteractionProxy | ? |
| 2025-XX-XX | Disabled auto VFX (default false) | ? |
| 2025-XX-XX | Added boat channel center depression | ? |
