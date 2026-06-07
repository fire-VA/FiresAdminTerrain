# Snow / Ice / Waterfall Anchor + Drag + Mesh Polish Plan

> Living tracker for the round of work covering:
> 1. Cleaner per?feature ZDO anchors (land?snow + land?ice get their own, off the water + terrain anchors).
> 2. Top?to?bottom drag handling for water/ice/snow when the cursor leaves a cliff edge � same combine model the terrain brush already uses.
> 3. `freeze_water` (ice?water) honors the IH ruler `circle=R` radius and gets the smoothed?edge mesh treatment the floating ice already has.
> 4. Sweep of original RPG?Maker ports that still aren't wired into the new systems.

Each task lists **what / why / files touched / acceptance / status**.
After each major task the mod is rebuilt and any compile errors logged inline.

---

## 0. Conventions

- **Anchor pattern of record**: `TileAnchor` (terrain) and `WaterAnchor` (water) � `ZNetView` prefab cloned via `*AnchorPrefab.Ensure()` from `piece_dirtfloor_vad`, registered in `ZNetScene.m_namedPrefabs` only, stripped invisible, ZDO?backed identity. New land?snow + land?ice anchors follow this exact template (one ZDO per feature per zone, so we never balloon a single ZDO).
- **Combiner pattern of record**: `WaterMeshCombiner` + `WaterMeshCombinerAnchor` + footprints?are?disks + per?edge smoothing. `LandIceCombiner` / `LandSnowCombiner` already follow the disk?merge half. Mesh skirts now custom?terrain?aware (see screenshot fix from previous turn).
- **Drag pattern of record**: `TileBrushDrag` mirrors the cursor at `DRAG_INTERVAL=0.1s` while LMB is held, snapshotting the last brush op and re?firing on the new cursor world position. Snow/ice/water drag will plug into the same handler instead of growing a parallel one.
- **Build cadence**: rebuild after every "major" step (marked **?? BUILD CHECK** in the task list). Failures get a fix?up sub?task appended; we do not move on with a red build.

---

## 1. New ZDO anchor for **Land Ice**

### 1.1 `LandIceAnchorPrefab` � clone the `WaterAnchorPrefab` template
- **Why**: today `LandIceController` rides on a cloned `ice1` (vanilla `Floating` + `Rigidbody` + LOD group + colliders we have to strip every load). It also collides with the water + terrain anchor namespaces because they all live in `m_namedPrefabs`. Splitting it into a dedicated `VALandIce` prefab gives one ZDO per disk?cluster, decoupled from anything else in the zone.
- **Files**:
  - **NEW** `Modules/CustomWater/Ice/Land/LandIceAnchorPrefab.cs` � copy `WaterAnchorPrefab.cs` 1:1, change `PrefabName = "VALandIce"`. Same source prefab (`piece_dirtfloor_vad`), same fake?null scrub paranoia.
  - `FiresAdminTerrain.cs` � call `LandIceAnchorPrefab.Ensure()` from the same Awake/ZNetScene hook that runs `WaterAnchorPrefab.Ensure()`.
- **Acceptance**: `VALandIce` appears in `ZNetScene.m_namedPrefabs` immediately after ZNetScene.Awake on both fresh and re?login worlds.
- **Status**: ✅ done (2026-05-26)

### 1.2 `LandIceAnchor` � minimal ZDO holder, mirrors `WaterAnchor` shape
- Owns: footprints (with Y), noise seed, grid params, height array � all the data `LandIceController.SaveToZDO/LoadFromZDO` reads/writes today, but moved to its own anchor.
- Hash keys re?namespaced (`va_landice_v`, `va_landice_data`, etc.) so old keys on the legacy ice1 ZDOs are deliberately ignored � user confirmed orphans are NOT a concern (existing snow/ice deleted before migration).
- **Files**:
  - **NEW** `Modules/CustomWater/Ice/Land/LandIceAnchor.cs`
  - `LandIceController.cs` � Save/Load now talk to the linked `LandIceAnchor`, not to the host GameObject's ZDO.
  - `IcePlacementTool.cs` � clone `LandIceAnchorPrefab.Prefab` (not `ice1`) for land?ice placements; floating?ice path unchanged.
- **Acceptance**: place a land?ice disk ? leave zone ? return ? mesh re?hydrates from `VALandIce` ZDO. `ice1` clones never spawn for land mode.
- **Status**: ✅ done (2026-05-26) — `LandIceAnchor.SpawnNew` + `RestoreFromAnchor` wired. `IcePlacementTool.PlaceIce` + `PlaceIceForced` land-branch now spawn VALandIce, not ice1. Build green.

---

## 2. New ZDO anchor for **Land Snow**

### 2.1 `LandSnowAnchorPrefab`
- Same as 1.1 but `PrefabName = "VALandSnow"`. Snow is visual?only (no collider, no slide) so the anchor strip is even simpler.
- **Files**: **NEW** `Modules/CustomWater/Ice/Land/LandSnowAnchorPrefab.cs`; auto-registers via `[HarmonyPatch(ZNetScene.Awake)]`.
- **Status**: ✅ done (2026-05-26)

### 2.2 `LandSnowAnchor` + `LandSnowController` migration
- Same shape as 1.2. Hash keys re?namespaced (`va_landsnow_*`). User confirmed no orphan compatibility required.
- **Files**:
  - **NEW** `Modules/CustomWater/Ice/Land/LandSnowAnchor.cs`
  - `LandSnowController.cs` ✅ Save/Load via anchor; `BindAnchor` + `RestoreFromAnchor` added; legacy `LoadFromZDO` removed.
  - `LandSnowCommands.cs` ✅ now spawns via `LandSnowAnchor.SpawnNew` (no more ice1 clone).
  - `WaterBrushCommands.PlaceLandSnowFromBrush` ✅ same route.
- **Acceptance**: ZDO byte budget per zone drops noticeably with mixed snow+ice+water; visual identical on reload.
- **Status**: ✅ done (2026-05-26) — build green, DLL deployed. Awaiting user reload-test.
- **?? BUILD CHECK** ✅ green after 2.1 + 2.2.

---

## 3. Top?to?bottom drag combining (cliff drop)

> User: *"if we're click?dragging a disk off the edge of a cliff then we should combine the mesh from top to bottom� this is actually how our waterfall logic should work as well."*

### 3.1 `DiskDragRouter` � shared drag dispatcher
- Extract the cursor?hold / `DRAG_INTERVAL` loop out of `TileBrushDrag` into a reusable `DiskDragRouter` MonoBehaviour. Routes per?tick `OnDragTick(worldPos)` calls to whichever subsystem armed itself last.
- Subsystems register via `DiskDragRouter.PrimeFor(...)` with: command name, radius, ruler offset, callback `(prevWorldPos, newWorldPos) => void`.
- `TileBrushDrag` becomes a thin adapter over `DiskDragRouter`. Snow/ice/water plug in next to it without each growing their own MonoBehaviour.
- **Files**:
  - **NEW** `Modules/CustomWater/Core/DiskDragRouter.cs`
  - `Modules/CustomTerrain/Persistence/TileBrushDrag.cs` � refactor to thin adapter.
- **Acceptance**: existing terrain brush drag behavior identical (regression check: paint/raise/lower drag a 5m brush across a tile).
- **Status**: ? pending
- **?? BUILD CHECK**.

### 3.2 Vertical drag detection � `IsVerticalDrop(prev, next)`
- Helper in `DiskDragRouter`: returns true when the new cursor position is more than `VERTICAL_DROP_THRESHOLD` (? 1.5 � disk radius) below the previous sample AND outside the previous disk's XY footprint (the "fell off the edge" case).
- When true, the per?subsystem callback is called in **vertical mode** instead of horizontal.
- **Status**: ? pending

### 3.3 Land?ice / land?snow vertical combiner
- `LandIceCombiner.TryCombineVertical(prevDisk, newDisk)`:
  - Adds a tall **side?wall** mesh segment between the upper disk's edge nearest to `newDisk.Center.xz` and the new disk's top edge, instead of merging footprints horizontally.
  - Side wall vertices follow the line `Lerp(upperEdge, lowerEdge, t)` for t?[0..1], stamped at the same XY perimeter step as the existing skirt logic, but the Y span goes top?bottom (upper disk surface Y ? lower disk surface Y) instead of top?terrain.
  - Top?edge and bottom?edge get the same edge?smoothing pass that floating ice uses (rounded corners, no hard 90�).
  - Same code path applies to `LandSnowCombiner.TryCombineVertical` � snow is just the same call with the snow material.
- **Files**:
  - `LandIceCombiner.cs` + `LandSnowCombiner.cs` � `TryCombineVertical` method.
  - `LandIceController.cs` + `LandSnowController.cs` � `AddVerticalSkirtTo(otherDisk)` mesh builder, reusing `EmitSkirtQuad` plumbing but with `bottomY = otherDisk.SurfaceY` instead of `terrainY ? penetration`.
- **Acceptance**: drag a snow disk off a cliff ? continuous mesh from the upper shelf down to the lower shelf, no white sheet artifacts.
- **Status**: ? pending
- **?? BUILD CHECK**.

### 3.4 Waterfall: same model
- `CustomWaterfallController.GenerateMesh` is a `// TODO: Implement proper waterfall mesh generation` stub today (`Modules/CustomWater/Waterfall/CustomWaterfallController.cs:59`).
- Implement it as the **same** vertical?skirt builder used by 3.3, just sourcing top/bottom edges from the river?detector's cliff?edge polyline rather than from a single disk pair. Same edge smoothing pass.
- Drag?off?cliff in `place_water` now routes through 3.1's `DiskDragRouter`, calling `WaterMeshCombiner.AddVerticalSegment` when `IsVerticalDrop` fires � same UX as ice/snow.
- **Files**:
  - `CustomWaterfallController.cs` � `GenerateMesh` body.
  - `WaterMeshCombiner.MeshGen.cs` � shared `EmitVerticalSegment(...)` helper consumed by both the waterfall and disk?drag paths.
  - `WaterPlacementTool.cs` � drag prime on `place_water` / `place_water_type`.
- **Status**: ? pending
- **?? BUILD CHECK**.

---

## 4. `freeze_water` (ice?water) � radius + edge polish

> User: *"the tool that draws water with a solid ice top that has collision and we can slide on � properly draws in the circle radius from our infinity hammer ruler like all the rest of our meshes, and lets smooth the edges of this mesh to be more rounded like we do for our floating ice from the ice1 prefab."*

### 4.1 Parse `circle=R` in `freeze_water`
- `WaterPlacementTool.FreezeWater(args)` today ignores all args and just toggles the combiner at the cursor. It should accept `circle=R` and `offset=H` like every other water command (use `ParseArgs` already in the file).
- When `R > 0`, freeze only the disks within `R` of the cursor (or merge a new ice?topped disk of radius R into the combiner if there isn't one yet).
- **Files**: `WaterPlacementTool.cs::FreezeWater`.
- **Acceptance**: pulling out the IH ruler to a 12m circle and running `freeze_water` produces a 12m ice cap, not a single?disk freeze.
- **Status**: ? pending

### 4.2 Smooth the `FrozenWaterController` ice?top mesh edges
- Today `FrozenWaterController.RebuildIceMesh` emits the combiner's outline as a hard polygonal edge. The floating?ice prefab (`IceMeshUtility`) does a 2?pass corner round + boundary?vertex normal blend that we already use for the `place_ice` path.
- Re?use `IceMeshUtility.RoundBoundary(...)` and `IceMeshUtility.SmoothBoundaryNormals(...)` on the frozen?water cap. Same call sites the floating ice uses.
- **Files**:
  - `Modules/CustomWater/Ice/FrozenWaterController.cs` � `RebuildIceMesh` post?pass.
  - Possibly `Modules/CustomWater/Ice/Shared/IceMeshUtility.cs` � verify the public surface is enough; widen if needed.
- **Acceptance**: top edge of frozen water has the same rounded look as a floating ice puck instead of a faceted boundary.
- **Status**: ? pending
- **?? BUILD CHECK** after 4.1 + 4.2.

---

## 5. Original RPG?Maker ports � still not wired

Sweep of legacy ports that are dead?code or partial. Each gets a yes/no decision: **wire** (finish the port) or **delete** (confirm the modern replacement covers it). Bundled into one section so we triage them together rather than chasing them piecemeal later.

### 5.1 `VAClutterManager` (`Managers/VAClutterManager.cs`) — **DELETED 2026-05-26**
- User confirmed FAP is source-linked. The empty `EnsureRegistered` stub was dead weight; no live callers in the codebase. File removed from disk and from `FiresAdminTerrain.csproj`. Build green.
- **Status**: ✅ done

### 5.2 `SnowGroundMesh` / `SnowClutterConfig` / `SnowCommands` / `SnowTrailController` / `SnowTrailPaint` � RPG?Maker land?snow drift system
- This is the *old* snow path the user explicitly wants replaced by `LandSnowController` (per turn 1). Today:
  - `SnowCommands.RegisterCommands()` IS being called in `FiresAdminTerrain.cs:245`, registering `paint_snow_clutter` / `clear_snow_clutter` / `snow_trail`.
  - `SnowClutterConfig.EnsureRegistered()` lazily registers a clutter type that uses `SnowGroundMesh.GetOrCreateDiscMesh()`.
  - `SnowTrailController` stamps footprint trails into the same clutter system as the player walks.
- These are independent of the new `LandSnowController` (which is the mesh disk system). The user has not asked to remove them yet.
- **Decision needed**: keep both side?by?side (snow clutter = ground sparkle, LandSnowController = thick drift)? Or fold the clutter into LandSnowController as a child renderer? Flag for explicit user direction.
- **Status**: ? awaiting decision

### 5.3 `TileAnchorRegistry.GetOrCreate` � **8c TODO** (`TileAnchorRegistry.cs:45`)
- Stub. Cross?tile disk ops that overlap an unloaded neighbor tile silently no?op for that neighbor. Phase 8c work.
- Impact today: a 30m terrain?paint disk straddling a zone boundary only writes the loaded half. Re?entering the unloaded zone shows the paint cut off at the tile line.
- **Fix**: spawn the `VATile` prefab at `WorldCenterForTile(tileXZ)` here, claim ownership, register, return. Pattern lifted from `WaterAnchor.CreateAnchorForWater`.
- **Status**: ? pending (recommend scheduling **after** �1�4 since drag work will hit this).

### 5.4 `RegionScrubber.cs:269` � booleans?flipping TODO
- "Log a TODO so any caller that flips them sees a clear" � comment is truncated in the source; need to read the surrounding context and decide if this is still meaningful or stale.
- **Status**: ? inspect + decide

### 5.5 `CustomWaterfallController.GenerateMesh` � covered by �3.4
- The `// TODO: Implement proper waterfall mesh generation` lives here. Don't duplicate work; �3.4 closes it.
- **Status**: ? �3.4

### 5.6 `WaterDebugCommands.cs:2114` � waterfall **terrain?paint** TODO
- 3?step plan in the comment: (1) reuse lava paint mechanism for steep?terrain stickiness, (2) runtime shader swap to water shader, (3) persist via biome paint system.
- This is a *visual* paint for vanilla cliff faces beneath a waterfall � orthogonal to �3.4 (which is the falling?water mesh itself). Both should exist; track separately.
- **Status**: ? schedule after �3 lands, once we know the waterfall mesh API.

### 5.7 `EnvironmentBoxPatches.cs:228 / :950` � legacy `DirtFloor` probes already removed
- Comments only; no live dead?code. **No action.**

### 5.8 `CustomWaterPatches.cs:59 / :126 / :160 / :305 / :453` � legacy water probes already removed
- Same as 5.7. **No action.**

### 5.9 `ManifestDiffSync.cs:57?58` � RPC names still carry `FiresRPGmaker_` prefix
- Cosmetic only; renaming requires bumping a compat key and re?syncing every existing world. **Defer.**

### 5.10 `PlayerSpawnGate.cs:203` � `FiresRPGmaker_PlayerSpawnGateDriver` GameObject name
- Cosmetic; harmless. **Defer.**

---

## 6. Order of execution

| # | Task | Build check | Owner |
|---|---|---|---|
| 1 | �1 LandIceAnchor prefab + migration | ? | |
| 2 | �2 LandSnowAnchor prefab + migration | ? | |
| 3 | �4 freeze_water radius + edge smoothing | ? | |
| 4 | �3.1 + �3.2 shared `DiskDragRouter` | ? | |
| 5 | �3.3 land ice/snow vertical drag | ? | |
| 6 | �3.4 waterfall mesh + drag | ? | |
| 7 | �5 RPG?Maker port triage (user decisions) | � | |

Each row is one "major change" ? one build ? log green/red here.

---

## 7. Open questions for the user — ANSWERED 2026-05-26

1. **§5.1** — FAP source-linked? **YES → delete VAClutterManager stub.**
2. **§5.2** — old snow clutter stack? **FOLD into LandSnowController as a child renderer.** SnowGroundMesh / SnowClutterConfig / SnowCommands / SnowTrailController / SnowTrailPaint either delegate to LandSnowController or get deleted, with the ground-sparkle + footprint-trail behavior driven from LandSnowController instances.
3. **§3.3** vertical drag threshold? **1.5 × diskRadius** (scales with brush size — small drag adjustments don't accidentally trigger).
4. **§4.1** — clarified by user: this isn't about `freeze_water` (the console command) but about the **`place_ice_water`** / `va_brush_water_ice` tool (the IH hammer tool that places frozen walkable water via `FrozenWaterController`). For `freeze_water` itself the answer is: **respect the IH ruler radius, create a NEW ice-mesh floating on top of existing water** (don't transform the existing combiner). §4 work now covers both:
   - `freeze_water circle=R` → create new ice cap of radius R on top of overlapping water.
   - `place_ice_water` → honor `circle=R` from ruler (currently ignored), apply rounded-edge smoothing pass.

---

## 8. Change log (filled as work lands)

- _2026-05-26_ — Plan file created.
- _2026-05-26_ — **§1 LandIce anchor migration done.** Files:
  - NEW `Modules/CustomWater/Ice/Land/LandIceAnchorPrefab.cs` — clones `piece_dirtfloor_vad`, registers as `VALandIce` via `[HarmonyPatch(ZNetScene.Awake)]`. Idempotent across logout/reconnect.
  - NEW `Modules/CustomWater/Ice/Land/LandIceAnchor.cs` — MonoBehaviour on the VALandIce prefab. Hosts ZDO data under `va_landice_*` keyspace. `SpawnNew(position)` static factory creates the prefab + binds a controller. `WriteData` / `ReadData` for save/restore. `DestroyPermanently` for clean teardown.
  - EDIT `LandIceController.cs` — `Start()` no longer auto-loads; `BindAnchor(LandIceAnchor)` + `RestoreFromAnchor(LandIceAnchor)` replace direct ZDO talk; `SaveToZDO()` routes through `_anchor.WriteData`; `Remove()` uses `_anchor.DestroyPermanently`. Legacy `LoadFromZDO` deleted (replaced by `RestoreFromAnchor`).
  - EDIT `IcePlacementTool.cs` — land branches (PlaceIce + PlaceIceForced) now spawn via `LandIceAnchor.SpawnNew`. Floating-ice branch still clones ice1 (vanilla buoyancy dependency).
- _2026-05-26_ — **§2 LandSnow anchor migration done.** Same pattern as §1, swap "Ice" → "Snow":
  - NEW `LandSnowAnchorPrefab.cs` — `VALandSnow` prefab.
  - NEW `LandSnowAnchor.cs` — `va_landsnow_*` keyspace, mirror API.
  - EDIT `LandSnowController.cs` — anchor-routed Save/Restore.
  - EDIT `LandSnowCommands.PlaceLandSnow` + `WaterBrushCommands.PlaceLandSnowFromBrush` — both now route through `LandSnowAnchor.SpawnNew`.
- Both files added to `FiresAdminTerrain.csproj`. Build green. DLL deployed to BlueHills profile.
- _2026-05-26_ — **§5.1 done.** `Managers/VAClutterManager.cs` deleted (FAP source-linked, no callers). Build green.
- _2026-05-26_ — **Pre-§3.3 placeholder.** `LandSnowController.EmitSkirtQuad` now skips the skirt when the top-face boundary crossing is already at-or-below local terrain (the SINGLE-PILE case — the dome shape from round-28 has tapered the top down to terrain, so no skirt is needed; cliff-edge cases still get the skirt because their crossing stays at disk Y). Will be replaced by explicit DiskDragRouter gating in §3.3.
