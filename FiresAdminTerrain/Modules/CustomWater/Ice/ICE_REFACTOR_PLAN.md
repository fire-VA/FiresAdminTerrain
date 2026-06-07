# Ice System Refactor Plan — Land Ice & Floating Ice Split

## Progress Tracker

| Phase | Status | Summary |
|-------|--------|---------|
| Phase 1: Extract Shared Code | ? COMPLETE | `IceMeshUtility`, `IceDiskFootprint`, `IceHole` extracted |
| Phase 2: Land Ice Controller | ? COMPLETE | `LandIceController`, `LandIceCombiner`, `LandIceCommands` implemented |
| Phase 3: Floating Ice Controller | ? COMPLETE | `FloatingIceController`, `FloatingIceCombiner`, `FloatingIceCommands` implemented |
| Phase 4: Tool Integration | ? COMPLETE | `IcePlacementTool`, YAML tools, `IceSlidingPatches` all updated |
| Phase 5: Migration & Cleanup | ? COMPLETE | Auto-migration in IceSlidingPatches, `[Obsolete]` on legacy files |

---

## Overview

Split the current `StandaloneIceController` into two distinct systems:

1. **Land Ice** (`LandIceController`) — Terrain-like ice that follows our custom terrain patterns
2. **Floating Ice** (`FloatingIceController`) — Water-based icebergs with underwater mesh

Both systems support **combining overlapping disks**, **height modification** via Infinity Hammer, and **ZDO persistence**.

---

## Legacy Architecture (Single Controller — DEPRECATED)

```
StandaloneIceController                 ? DEPRECATED, migration only
??? IsFloating = true  ? floating mode
??? IsFloating = false ? terrain overlay mode
??? IceDiskCombiner                     ? DEPRECATED
??? IceSlidingPatches  ? slippery physics (SHARED, updated)
??? IcePlacementTool   ? console commands (UPDATED, routes to new controllers)
```

### Problems (solved by refactor)
- ~~Land ice and floating ice share too much code despite very different needs~~
- ~~Land ice can't stack/combine for thickness, can't make hills~~
- ~~No height modification support (raise/lower/level via IH)~~
- ~~No persistence to anchor ZDO or TerrainComp~~
- ~~Floating ice top surface is flat — can't build ridges/hills on top~~
- ~~Climb-edge handler exists on land ice where it's not needed~~

---

## New Architecture (Implemented)

### Land Ice (`LandIceController`) — ? COMPLETE

Follows the **DirtFloorMeshCombiner** pattern exactly:
- Uses height grid for per-vertex height control (terrain-sampled defaults)
- Combines overlapping placements by merging footprint data
- Supports raise/lower/level/smooth via Infinity Hammer
- Saves to anchor ZDO (ice1 prefab clone)
- No climb-out handler, no floating physics, no underwater mesh
- Ice material on top, skirt walls at edges
- Slippery physics via `IceSlidingPatches`

```
LandIceController (MonoBehaviour on ice1 clone)
??? _heights[]          — height grid (terrain-sampled, modifiable)
??? _gridWidth/Height   — grid dimensions
??? _cellSize = 0.25    — grid resolution
??? _footprints[]       — list of IceDiskFootprint (center + radius)
??? _iceMesh            — visual mesh (top face + skirt walls)
??? _colliderMesh       — collision mesh (top face only)
??? _iceMaterial        — captured vanilla ice material
?
??? Initialize()        — sets up height grid from terrain raycasts
??? AddFootprint()      — adds a new disk, expands grid, resamples heights
??? ModifyHeights()     — raise/lower with smoothstep falloff
??? LevelHeights()      — level to target height within radius
??? SmoothHeights()     — weighted neighbor averaging
??? RebuildMesh()       — marching-squares with DisplaceCornerTowardBoundary
??? SaveToZDO()         — persist footprints, heights, grid params
??? LoadFromZDO()       — restore on zone reload
?
??? ZDO Keys:
?   ??? va_land_ice          (bool — marker)
?   ??? va_land_ice_fp       (byte[] — packed footprints)
?   ??? va_land_ice_seed     (float — noise seed)
?   ??? va_land_ice_heights  (byte[] — float[] height grid)
?   ??? va_land_ice_grid     (byte[] — gridW, gridH, bounds)
?
??? LandIceCombiner (static)
    ??? TryCombineNearby() — merges overlapping LandIceControllers
```

### Floating Ice (`FloatingIceController`) — ? COMPLETE

Enhanced version of original floating ice:
- Floating at water level with vanilla `Floating` component + fallback custom bobbing
- **Top surface supports height modification** (ridges, hills)
- **Bottom surface drops deep like an upside-down mountain** (cosmetic only, NO collision)
- **Walking MeshCollider follows the sculpted top surface** (players walk on ridges/hills)
- **Flat BoxCollider (trigger) on root is ONLY for the Floating buoyancy component**
- Climb-out edge handler via `IceHoleClimbPoint`
- Side walls generated from marching-squares boundary
- Hole-breaking still works (max 20 holes)

```
FloatingIceController (MonoBehaviour on ice1 clone)
??? _heights[]          — TOP surface height grid (delta from water level)
??? _gridWidth/Height   — grid dimensions
??? _cellSize = 0.25    — grid resolution
??? _footprints[]       — list of IceDiskFootprint
??? _holes[]            — list of IceHole
?
??? _iceMesh            — visual mesh (top + sides, ice material)
??? _walkCollider       — MeshCollider on child "IceShell" GO
??? _walkColliderMesh   — separate mesh for collision (follows heights)
??? _bottomMesh         — visual-only bottom mesh on "IceBottom" GO (Water layer)
??? _waterMesh          — water under-mesh on "IceWaterUnder" GO
?
??? _iceMaterial        — ice material (via WaterMaterialManager)
??? _waterMaterial      — water material (via WaterMaterialManager)
??? _floatTargetY       — water surface Y
?
??? InitializeFloating()   — sets up flat height grid, creates 3 GOs, floating physics
??? AddFootprint()         — adds footprint, expands grid, updates buoyancy box
??? ModifyTopHeights()     — raise/lower the top surface with smoothstep falloff
??? SmoothTopHeights()     — weighted neighbor averaging
??? BreakIceAt()           — punch holes (removes ice, rebuilds mesh)
??? RebuildMesh()          — rebuilds all 4 meshes + hole/edge walls
??? SaveToZDO()            — persist all state
??? LoadFromZDO()          — restore on zone reload
?
??? Mesh Builders:
?   ??? BuildIceShellMesh()      — top face with heights + bevel + side walls
?   ??? BuildWalkColliderMesh()  — top face only, follows heights exactly
?   ??? BuildBottomMesh()        — deep iceberg shape (smoothstep centerWeight)
?   ??? BuildWaterUnderMesh()    — flat water plane under ice
?
??? ZDO Keys:
?   ??? va_float_ice          (bool — marker)
?   ??? va_float_ice_fp       (byte[] — packed footprints)
?   ??? va_float_ice_seed     (float — noise seed)
?   ??? va_float_ice_heights  (byte[] — float[] height grid)
?   ??? va_float_ice_grid     (byte[] — gridW, gridH, bounds)
?   ??? va_float_ice_wy       (float — water level Y)
?
??? FloatingIceCombiner (static)
    ??? TryCombineNearby() — merges overlapping FloatingIceControllers + transfers holes
```

**Collider Architecture (THREE colliders, each with a distinct purpose):**
1. **Walking MeshCollider** (`_walkCollider` on child `IceShell` GO)
   - Non-convex MeshCollider that follows the sculpted top surface exactly
   - Players walk on this — they feel the ridges and hills
   - Rebuilt every time heights change
   - Has its own kinematic Rigidbody (required for non-convex child MeshColliders)
2. **Floating BoxCollider** (on root GO, `isTrigger = true`)
   - Flat trigger BoxCollider at water level, sized to cover full footprint bounds
   - Used ONLY by the vanilla `Floating` component for buoyancy force calculations
   - Does NOT affect walking — it's a trigger, invisible to character physics
   - Resized when footprints are added via `UpdateFloatingColliderBounds()`
3. **Bottom mesh has NO collider at all**
   - Pure visual — `IceBottom` GO on Water layer
   - Deepest at center: `bottomY = -ICE_THICKNESS - (centerWeight * ICEBERG_DEPTH)`
   - `ICEBERG_DEPTH = 2.5m`, smoothstep falloff from center to edges

---

## Shared Code (Phase 1 — ? COMPLETE)

### `IceMeshUtility` (static helper) — `Shared/IceMeshUtility.cs`
Extracted from original `StandaloneIceController`:
- `EmitMarchingSquaresQuad()` — emits boundary cell triangles (top face)
- `EmitMarchingSquaresQuadFlipped()` — bottom face variant (reversed winding)
- `EmitMarchingSquaresSideWalls()` — side wall quads along boundary
- `LerpEdgeCrossing()` — isoline interpolation
- `ComputeBevel()` — edge bevel computation
- `DisplaceCornerTowardBoundary()` — near-edge smoothing
- `GetEdgeDistance()` — signed distance with Perlin noise wobble
- `IsInsideAnyFootprintNoisy()` — noisy containment test
- `RecalcBounds()` — footprint union AABB with noise padding
- `PackFootprints()` / `UnpackFootprints()` — ZDO byte array serialization
- `IsInsideAnyFootprint()` / `OverlapsWithAnyFootprint()` / `IsInsideAnyFootprintExcept()`

### `IceDiskFootprint` (struct) — `Shared/IceDiskFootprint.cs`
Shared between both controllers. Fields: `Center` (Vector3), `Radius` (float).

### `IceHole` (struct) — `Shared/IceHole.cs`
Only used by `FloatingIceController`. Fields: `Center`, `Radius`, `TimeCreated`.

### `IceSlidingPatches` — `IceSlidingPatches.cs` (updated)
Shared — applies slippery physics to both land and floating ice.
Detects collider names: `"IceShell"`, `"IceOverlay"`.

### `WaterMaterialManager` — `Rendering/WaterMaterialManager.cs`
Shared — provides ice material via `GetMaterial(CustomLiquidType.Ice)`.

---

## Console Commands — All Registered

### IcePlacementTool (core placement & deletion)
| Command | Action | Controller |
|---------|--------|-----------|
| `place_ice [args]` | Auto-detect water vs land | FloatingIceController or LandIceController |
| `place_ice_land [args]` | Force land ice | LandIceController |
| `place_ice_water [args]` | Force floating ice | FloatingIceController |
| `delete_ice [args]` | Remove all ice types in radius | All (Standalone + Land + Floating) |
| `break_standalone_ice [args]` | Punch hole in floating ice | FloatingIceController ? StandaloneIceController fallback |

### LandIceCommands (height modification)
| Command | Action |
|---------|--------|
| `ice_land_raise raise=H circle=R` | Raise land ice surface |
| `ice_land_lower lower=H circle=R` | Lower land ice surface |
| `ice_land_level circle=R` | Level to clicked height |
| `ice_land_smooth circle=R` | Smooth average |
| `delete_ice_land circle=R` | Remove land ice only |

### FloatingIceCommands (height modification)
| Command | Action |
|---------|--------|
| `ice_water_raise raise=H circle=R` | Build ridges on iceberg top |
| `ice_water_lower lower=H circle=R` | Lower iceberg top surface |
| `ice_water_smooth circle=R` | Smooth iceberg top |
| `delete_ice_water circle=R` | Remove floating ice only |

---

## YAML Tool Definitions — ? UPDATED

All tools defined in `INFINITY_HAMMER_ICE_TOOLS.yaml`:
- Placement: Place Ice (auto), Place Ice on Land, Place Ice on Water, size presets
- Breaking: Break Standalone Ice
- Land height: Raise/Lower/Level/Smooth Land Ice (all `continuous: -<mod1>`)
- Floating height: Raise/Lower/Smooth Floating Ice (all `continuous: -<mod1>`)
- Deletion: Delete Ice (all types), Delete Floating Ice
- Frozen water: Freeze Water, Thaw Ice, Break Ice Hole

---

## IceSlidingPatches — Zone Reload Restore & Migration Order

When a ZNetView spawns on zone load, the restore patch checks in this order:
1. `FloatingIceController.ZdoHasFloatingIceData()` ? add `FloatingIceController`
2. `LandIceController.ZdoHasLandIceData()` ? add `LandIceController`
3. `StandaloneIceController.ZdoHasCustomIceData()` ? **auto-migrate**:
   - Read `va_ice_mode` (1 = floating, 0 = land)
   - Copy `va_ice_footprints` and `va_ice_noise_seed` to new-format keys
   - Add `FloatingIceController` or `LandIceController`
   - New controller restores from ZDO in `Start()` (flat/terrain-sampled heights)
   - Fallback: if migration fails, add legacy `StandaloneIceController`

---

## Implementation Order & Status

### Phase 1: Extract Shared Code — ? COMPLETE
1. ? Created `Shared/IceMeshUtility.cs` — marching-squares helpers + SDF + ZDO packing
2. ? Created `Shared/IceDiskFootprint.cs` — standalone struct
3. ? Created `Shared/IceHole.cs` — standalone struct

### Phase 2: Land Ice Controller — ? COMPLETE
1. ? Created `Land/LandIceController.cs` — height grid, terrain sampling, mesh gen, combining
2. ? Created `Land/LandIceCombiner.cs` — disk merging logic
3. ? Created `Land/LandIceCommands.cs` — console commands (raise/lower/level/smooth/delete)
4. ? ZDO persistence integrated directly in controller (SaveToZDO/LoadFromZDO)

### Phase 3: Floating Ice Controller — ? COMPLETE
1. ? Created `Floating/FloatingIceController.cs` — top heights, iceberg bottom, holes, floating, 3-collider architecture
2. ? Created `Floating/FloatingIceCombiner.cs` — disk merging + hole transfer
3. ? Created `Floating/FloatingIceCommands.cs` — console commands (raise/lower/smooth/delete)
4. ? ZDO persistence integrated directly in controller (SaveToZDO/LoadFromZDO)

### Phase 4: Tool Integration — ? COMPLETE
1. ? Updated `IcePlacementTool.cs`:
   - `place_ice` (water auto-detect) ? `FloatingIceController` + `FloatingIceCombiner`
   - `place_ice_water` (forced) ? `FloatingIceController` + `FloatingIceCombiner`
   - `place_ice_land` (forced) ? `LandIceController` + `LandIceCombiner` (already done in Phase 2)
   - `delete_ice` ? deletes `StandaloneIceController` + `LandIceController` + `FloatingIceController`
   - `break_standalone_ice` ? checks `FloatingIceController` first, falls back to `StandaloneIceController`
   - Registers `LandIceCommands.Initialize()` + `FloatingIceCommands.Initialize()`
2. ? Updated `INFINITY_HAMMER_ICE_TOOLS.yaml`:
   - Added Raise/Lower/Smooth Floating Ice tools
   - Added Delete Floating Ice tool
   - Updated Delete Ice description (all types)
3. ? Updated `IceSlidingPatches.cs`:
   - Zone reload restore: FloatingIceController ? LandIceController ? StandaloneIceController (priority order)
   - Ice detection unchanged (collider names `IceShell`, `IceOverlay` match both old and new controllers)

### Phase 5: Migration & Cleanup — ? COMPLETE
1. ? Added auto-migration in `IceSlidingPatches.MigrateLegacyIceZDO()`:
   - Detects legacy `StandaloneIceController` ZDO keys (`va_is_custom_ice`, `va_ice_mode`, `va_ice_footprints`, `va_ice_noise_seed`)
   - Reads `IsFloating` flag to determine target controller type
   - Writes new-format ZDO keys (`va_float_ice` + `va_float_ice_fp` + `va_float_ice_seed` for floating, `va_land_ice` + `va_land_ice_fp` + `va_land_ice_seed` for land)
   - Adds `FloatingIceController` or `LandIceController` which restores from ZDO in Start()
   - Falls back to legacy `StandaloneIceController` if migration fails
   - Height grids are initialized fresh (flat for floating, terrain-sampled for land) since legacy had no height data
2. ? Added `[Obsolete]` attribute to `StandaloneIceController`
3. ? Added `[Obsolete]` attribute to `IceDiskCombiner`
4. ?? `FrozenWaterController` left unchanged (separate grid-based system, not part of this refactor)

---

## Key Design Decisions

1. **Land ice follows terrain patterns** — height grid, terrain raycasts for defaults, anchor ZDO
2. **Walking collider follows visual top mesh** — MeshCollider on sculpted surface, players feel ridges/hills
3. **Floating BoxCollider is for buoyancy ONLY** — trigger collider sized to bounds, used by `Floating` component
4. **Bottom mesh is cosmetic only, NO collision** — deep iceberg shape (upside-down mountain), won't get stuck in shallow water
5. **Both use marching-squares** — same smooth edge code, extracted to `IceMeshUtility`
6. **Edge smoothing preserved** — `DisplaceCornerTowardBoundary()` in shared utility
7. **No climb-out on land ice** — only floating ice has the edge interaction via `IceHoleClimbPoint`
8. **ZDO persistence baked into controllers** — no separate anchor class needed (ice1 clone provides ZNetView)

---

## File Structure (Current)

```
FiresNPCs/Modules/CustomWater/Ice/
??? Shared/
?   ??? IceMeshUtility.cs          ? marching-squares helpers + SDF + ZDO packing
?   ??? IceDiskFootprint.cs        ? footprint struct
?   ??? IceHole.cs                 ? hole struct
??? Land/
?   ??? LandIceController.cs       ? main controller (height grid, mesh, ZDO)
?   ??? LandIceCombiner.cs         ? combining logic
?   ??? LandIceCommands.cs         ? IH console commands
??? Floating/
?   ??? FloatingIceController.cs   ? main controller (heights, iceberg, 3 colliders, ZDO)
?   ??? FloatingIceCombiner.cs     ? combining logic + hole transfer
?   ??? FloatingIceCommands.cs     ? IH console commands
??? IceSlidingPatches.cs           ? updated — detects all 3 controller types on zone reload
??? FrozenWaterController.cs       — unchanged (separate grid-based system)
??? StandaloneIceController.cs     ?? DEPRECATED [Obsolete] — auto-migrated on zone reload
??? IceDiskCombiner.cs             ?? DEPRECATED [Obsolete] — only used by StandaloneIceController
??? ICE_REFACTOR_PLAN.md           — this document

FiresNPCs/Modules/CustomWater/Tools/
??? IcePlacementTool.cs            ? routes to FloatingIceController/LandIceController

FiresNPCs/Debug/DebugRefs/
??? INFINITY_HAMMER_ICE_TOOLS.yaml ? all land + floating tools defined
```

---

## Refactor Complete ?

All 5 phases are implemented:
- **Shared utilities** extracted (`IceMeshUtility`, `IceDiskFootprint`, `IceHole`)
- **LandIceController** with height grid, terrain sampling, raise/lower/level/smooth
- **FloatingIceController** with sculpted top, iceberg bottom, 3-collider architecture
- **Tool integration** — all commands route to new controllers, YAML tools updated
- **Auto-migration** — legacy ZDOs seamlessly upgrade on zone reload
- **Deprecation** — `StandaloneIceController` and `IceDiskCombiner` marked `[Obsolete]`

### Future Cleanup (not blocking)
- Remove `StandaloneIceController.cs` and `IceDiskCombiner.cs` after one release cycle
- Optional: Update `FrozenWaterController` to delegate to `FloatingIceController`
