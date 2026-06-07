# Custom Terrain System Improvements Plan

## Overview

This document outlines the plan to fix several terrain system issues and add new features for cave/underground building support.

---

## Current Issues to Fix

### Issue 1: Terrain Edit Delay ("Wait to Draw Again")

**Problem:** After making a terrain edit, there's a noticeable delay before you can make another edit. Users must wait ~1-2 seconds between terrain paint strokes.

**Symptoms:**
- Click to draw terrain ? works
- Click again immediately ? nothing happens
- Wait 1 second ? works again

**Root Cause Analysis:**
1. **RPC Roundtrip Delay**: `TerrainOperationRPC` sends operations to server, waits for broadcast back
2. **Debounced Regeneration**: `REGENERATION_DELAY = 0.1f` batches mesh rebuilds
3. **Save Debouncing**: `QueueDebouncedSave()` delays persistence
4. **Operation Deduplication**: `_appliedOperations` HashSet checks might be blocking rapid repeat operations

**Solution:**

```csharp
// Problem: In TerrainOperationRPC, we add operation ID to _appliedOperations
// and check against it, but rapid clicks can generate different IDs
// that still get blocked by the dedup logic

// Fix 1: Separate "visual applied" from "server confirmed"
// Allow visual changes immediately, only block duplicate SERVER confirmations

// Fix 2: Add a "busy" state that's checked in the tool, not the RPC layer
// This gives immediate UI feedback that operation is in progress
```

**Files to Modify:**
- `TerrainOperationRPC.cs` - Add immediate local application bypass
- `DirtFloorTerrainManager.cs` - Add "ready for next operation" state check
- `InfinityHammerCompat.cs` - Check ready state before applying operations

---

### Issue 2: Isolated Mode Not Working ? FIXED

**Problem:** The isolated placement mode toggle (`IsolatedPlacementMode` flag / Alt key) doesn't actually prevent terrain from auto-combining with existing terrain.

**Solution Implemented:**
1. Added `IsolationGroupId` property to combiners - all terrain placed in the same isolation session shares this ID
2. Updated `CanMergeWith()` method to check isolation group compatibility
3. Updated `CheckAndMergeWithExistingCombiners()` to use `CanMergeWith()` instead of just checking `IsIsolated`
4. Updated `RemoveOverlappingCombiners()` to respect isolation groups
5. Updated `FindOrCreateCombinerForPosition()` to only find combiners in the same isolation group
6. Added isolation group ID persistence to anchor ZDO

**How It Works:**
- Non-isolated terrain (IsolationGroupId=0) can only merge with other non-isolated terrain
- When Alt is pressed, a new unique isolation group ID is generated (timestamp-based)
- All terrain placed while Alt is held shares this isolation group and CAN combine with each other
- When Alt is released, the isolation group resets to 0
- This allows building connected isolated structures (like a cave ceiling) that stay separate from other terrain

**Files Modified:**
- `DirtFloorMeshCombiner.Core.cs` - Added `IsolationGroupId` and `CanMergeWith()`
- `DirtFloorMeshCombiner.Lifecycle.cs` - Updated merge logic to use `CanMergeWith()`
- `DirtFloorTerrainManager.cs` - Added isolation group management and updated combiner search
- `TerrainCombinerAnchor.cs` - Added persistence for `IsolationGroupId`

---

### Issue 3: See-Through Terrain (Need Underside Material)

**Problem:** When looking at terrain from below (inside a cave), you can see through it because the mesh is single-sided.

**Current State:**
- Terrain mesh is generated with vertices/triangles facing UP only
- Standard Unity behavior: backfaces are culled (invisible)
- Result: Walking under custom terrain, you can see the sky through it

**Solutions (in order of preference):**

#### Option A: Double-Sided Shader (Easiest)
Modify the terrain material to render both front and back faces:

```csharp
// In DirtFloorMeshCombiner.SetupMaterial():
_terrainMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
```

**Pros:** Simple, no mesh changes
**Cons:** Underside looks same as top (grass texture from below is weird)

#### Option B: Separate Underside Material (Recommended)
Create a second mesh renderer for the underside with a dark/rock material:

```csharp
// In DirtFloorMeshCombiner:
private MeshRenderer _undersideRenderer;
private Material _undersideMaterial;

// In RegenerateMesh():
GenerateUndersideMesh(); // Creates inverted normals mesh

// Material is dark rock or pure black
_undersideMaterial = new Material(Shader.Find("Standard"));
_undersideMaterial.color = new Color(0.1f, 0.1f, 0.1f, 1f);
```

**Pros:** Looks natural, distinct underside appearance
**Cons:** More complex, doubles mesh vertex count

#### Option C: Environment Box Integration (Best for Caves)
When player is UNDER terrain mesh, activate an environment zone:

```csharp
// New class: TerrainUndersideEnvironment
// - Detects when player is under terrain
// - Creates EnvironmentBox-like zone
// - Overrides environment, lighting, fog

public class TerrainUndersideEnvironment : MonoBehaviour
{
    private DirtFloorMeshCombiner _combiner;
    private bool _playerInside = false;
    
    void Update()
    {
        // Check if player is below terrain surface
        if (IsPlayerUnderTerrain())
        {
            if (!_playerInside)
            {
                EnterCaveMode();
            }
        }
        else if (_playerInside)
        {
            ExitCaveMode();
        }
    }
}
```

---

## New Feature: Underground Environment System

### Concept

When player is detected as being UNDER a layer of custom terrain mesh:
1. Apply dark/cave environment
2. Override lighting to dim/moody
3. Show admin hover controls for environment customization
4. Persist environment settings per terrain "room"

### Detection Logic

```csharp
/// <summary>
/// Checks if a position is underneath custom terrain.
/// Returns the terrain combiner that forms the "ceiling" above the position.
/// </summary>
public static DirtFloorMeshCombiner GetTerrainCeilingAbove(Vector3 position, float maxCheckHeight = 20f)
{
    // Raycast UP from position
    // Check if we hit a custom terrain mesh from below
    
    Vector3 rayStart = position + Vector3.up * 0.5f;
    int terrainLayer = LayerMask.GetMask("piece"); // Our terrain uses piece layer
    
    if (Physics.Raycast(rayStart, Vector3.up, out RaycastHit hit, maxCheckHeight, terrainLayer))
    {
        // Check if hit object is a DirtFloorMeshCombiner
        var combiner = hit.collider.GetComponentInParent<DirtFloorMeshCombiner>();
        if (combiner != null)
        {
            // Verify we're actually BELOW the terrain (not on top)
            float terrainHeight = combiner.SampleHeightAt(position);
            if (terrainHeight > position.y + 0.5f)
            {
                return combiner; // We're under this terrain
            }
        }
    }
    
    return null;
}
```

### Environment Override System

```csharp
public class TerrainCaveEnvironment : MonoBehaviour
{
    // Environment settings (like EnvironmentBoxController)
    public string EnvironmentName = "Crypt";
    public bool OverrideAmbientLight = true;
    public Color AmbientColor = new Color(0.1f, 0.1f, 0.15f);
    public float FogDensity = 0.05f;
    
    // Admin hover info (like Hoverable interface)
    public string GetHoverText()
    {
        return $"[Cave Environment]\n" +
               $"Environment: {EnvironmentName}\n" +
               $"[Shift+E] Cycle Environment\n" +
               $"[E] Toggle Lighting Override";
    }
    
    // Available cave environments (subset of EnvironmentBoxController list)
    private static readonly string[] CaveEnvironments = {
        "Crypt",
        "SunkenCrypt", 
        "Caves",
        "CavesHildir",
        "InfectedMine",
        "Darklands_dark",
        "nofogts"
    };
}
```

### Admin Interaction

When player looks at underside of terrain mesh (from inside cave):
- Show hover text with current environment settings
- Allow cycling through cave-appropriate environments
- Settings persist to combiner's anchor ZDO

---

## Implementation Phases

### Phase 1: Fix Terrain Edit Delay (Priority: HIGH)

**Goal:** Allow rapid terrain editing without waiting between strokes.

**Tasks:**
1. [ ] Add `IsReadyForOperation()` check to `DirtFloorTerrainManager`
2. [ ] Separate visual application from server confirmation in RPC
3. [ ] Add rate limiting instead of blocking (max 10 ops/second)
4. [ ] Test rapid terrain painting in single player
5. [ ] Test rapid terrain painting in multiplayer

**Files:**
- `TerrainOperationRPC.cs`
- `DirtFloorTerrainManager.cs`

**Estimated Time:** 2-3 hours

---

### Phase 2: Fix Isolated Mode (Priority: HIGH)

**Goal:** Alt+click creates truly isolated terrain that never auto-merges.

**Tasks:**
1. [ ] Add `IsIsolated` property to `DirtFloorMeshCombiner`
2. [ ] Skip merge check for isolated combiners in `CheckAndMergeWithExistingCombiners()`
3. [ ] Set `IsIsolated = true` when `ShouldUseIsolatedPlacement()` returns true
4. [ ] Persist `IsIsolated` flag to anchor ZDO
5. [ ] Restore `IsIsolated` flag from anchor ZDO
6. [ ] Add visual indicator when isolated mode is active (console message)
7. [ ] Test creating cave: floor ? isolated ceiling ? verify no merge

**Files:**
- `DirtFloorMeshCombiner.cs`
- `DirtFloorMeshCombiner.Lifecycle.cs`
- `DirtFloorTerrainManager.cs`
- `TerrainCombinerAnchor.cs`

**Estimated Time:** 3-4 hours

---

### Phase 3: Add Underside Material (Priority: MEDIUM)

**Goal:** Make terrain opaque from below (visible rock/dark surface).

**Tasks:**
1. [ ] Add `_undersideMaterial` field to `DirtFloorMeshCombiner`
2. [ ] Create/capture dark rock material (like cave walls)
3. [ ] Generate underside mesh (inverted normals) in `RegenerateMesh()`
4. [ ] Add second `MeshRenderer` for underside
5. [ ] Set underside to render only backfaces
6. [ ] Test visibility from above and below

**Files:**
- `DirtFloorMeshCombiner.cs`
- `DirtFloorMeshCombiner.MeshGen.cs` (if split)

**Estimated Time:** 4-5 hours

---

### Phase 4: Underground Environment Detection (Priority: MEDIUM)

**Goal:** Detect when player is under custom terrain.

**Tasks:**
1. [ ] Create `TerrainCaveDetector` component
2. [ ] Implement `GetTerrainCeilingAbove()` utility method
3. [ ] Add `IsUnderCustomTerrain()` to `DirtFloorTerrainManager`
4. [ ] Fire events when player enters/exits cave space
5. [ ] Test detection with various terrain configurations

**Files:**
- New: `TerrainCaveDetector.cs`
- `DirtFloorTerrainManager.cs`

**Estimated Time:** 3-4 hours

---

### Phase 5: Cave Environment Override (Priority: MEDIUM)

**Goal:** Apply custom environment when inside cave.

**Tasks:**
1. [ ] Create `TerrainCaveEnvironment` component
2. [ ] Integrate with `EnvMan` for environment override
3. [ ] Add configurable lighting override
4. [ ] Add fog density control
5. [ ] Test environment transitions

**Files:**
- New: `TerrainCaveEnvironment.cs`
- Reference: `EnvironmentBoxController.cs`

**Estimated Time:** 4-5 hours

---

### Phase 6: Admin Hover Controls (Priority: LOW) ? PARTIALLY COMPLETE

**Goal:** Let admins configure cave environment via hover interaction.

**Tasks:**
1. [x] Implement `Hoverable` interface on `TerrainCaveEnvironment`
2. [x] Add environment cycling with E
3. [x] Add lighting toggle with Shift+E
4. [x] Add skybox block toggle with Ctrl+E
5. [x] Add fog toggle with Alt+E
6. [x] Persist settings to combiner anchor ZDO
7. [x] Add visual feedback for setting changes
8. [x] Add polygon boundary visualization (LineRenderer wireframe)
9. [x] Show boundary when admin is looking at ceiling
10. [ ] Add boundary resize handles (future)

**Files:**
- `TerrainCaveEnvironment.cs`
- `TerrainCombinerAnchor.cs`

**Estimated Time:** 3-4 hours (DONE)

---

## File Changes Summary

| File | Phase | Changes |
|------|-------|---------|
| `TerrainOperationRPC.cs` | 1 | Add rate limiting, separate visual/server confirmation |
| `DirtFloorTerrainManager.cs` | 1, 2, 4 | Add ready check, set isolation flag, cave detection |
| `DirtFloorMeshCombiner.cs` | 2, 3 | Add `IsIsolated`, underside material |
| `DirtFloorMeshCombiner.Lifecycle.cs` | 2 | Check isolation in merge logic |
| `TerrainCombinerAnchor.cs` | 2, 6 | Persist isolation, cave environment settings |
| `TerrainCaveDetector.cs` | 4 | **NEW** - Detect underground state |
| `TerrainCaveEnvironment.cs` | 5, 6 | **NEW** - Cave environment override |

---

## Testing Checklist

### Terrain Edit Delay
- [ ] Can paint terrain rapidly (10+ clicks per second)
- [ ] Visual updates immediately (no stutter)
- [ ] Multiplayer: rapid edits sync correctly
- [ ] No duplicate terrain created from rapid clicks

### Isolated Mode
- [ ] Alt+click creates new combiner (check console log)
- [ ] Isolated terrain doesn't merge with nearby terrain
- [ ] Isolated flag persists through logout/login
- [ ] Can create cave: floor at Y=50, ceiling at Y=55, they don't connect

### Underside Material
- [ ] Terrain is visible from above (normal grass texture)
- [ ] Terrain is visible from below (dark rock texture)
- [ ] No see-through gaps at terrain edges
- [ ] Performance acceptable with underside mesh

### Cave Detection
- [ ] Console shows "entered cave" when going under terrain
- [ ] Console shows "exited cave" when leaving terrain
- [ ] Detection works with multiple terrain layers
- [ ] Detection doesn't trigger on vertical walls

### Cave Environment
- [ ] Environment changes when entering cave
- [ ] Environment restores when exiting cave
- [ ] Shift+E cycles through cave environments
- [ ] Settings persist through logout/login

---

## Notes

### Performance Considerations

- Underside mesh doubles vertex count - may need LOD consideration
- Cave detection runs in Update() - use timer to limit checks
- Environment transitions should be smooth (fade)

### Multiplayer Considerations

- Isolation flag must sync via anchor ZDO
- Cave environment is client-side only (each player sees their own)
- Terrain edit delay fix must not break multiplayer sync

### Compatibility

- Works with existing EnvironmentBox system (non-conflicting)
- Works with existing water system (water in caves)
- Works with existing clutter system (can have clutter on cave floor)

---

## Related Documentation

- `ZONE_TERRAIN_CAVE_SUPPORT.md` - Previous cave system planning
- `MULTIPLAYER_TERRAIN_FIX_2025.md` - Multiplayer sync architecture
- `EnvironmentBoxController` reference - Environment override system

---

## Estimated Total Effort

| Phase | Hours |
|-------|-------|
| Phase 1: Fix Edit Delay | 2-3 |
| Phase 2: Fix Isolated Mode | 3-4 |
| Phase 3: Underside Material | 4-5 |
| Phase 4: Cave Detection | 3-4 |
| Phase 5: Cave Environment | 4-5 |
| Phase 6: Admin Controls | 3-4 |

**Total: 19-25 hours**

---

## Quick Wins (Can Do Immediately)

1. **Add isolation flag** - Simple property + check (1 hour)
2. **Add debug logging** for isolation mode - Verify Alt key detection works (15 min)
3. **Disable merge for isolated combiners** - One if statement (30 min)

These can be done in a single session to enable cave building immediately, then follow up with underside material and environment system later.
