# Drawing Feel Parity Gameplan — Match or Exceed RPG-Maker

> Source: research pass 2026-05-26 comparing `FiresNPCs/Modules/CustomTerrain/`
> (RPG-Maker, "smooth like Blender") vs `FiresAdminTerrain/Modules/CustomTerrain/`
> (FAT, currently "sub-par"). File:line refs are concrete and verified.

## 0. TL;DR

FAT feels sub-par because **(a)** the drag handler fires discrete 10 Hz brush stamps with **zero cursor interpolation** (RPG-maker has the same input rate but hides it behind a continuous SDF blend across overlapping disks), **(b)** every tick re-encodes + writes the entire 4225-vertex tile ZDO + recooks PhysX synchronously, and **(c)** the mesh is built from a stair-step "modified vertices only" mask with `RecalculateNormals` on a 1 m grid and no smoothing pass, so individual brush stamps show their hard 1 m terrace edges. RPG-maker's combiner uses a 2 verts/m grid, smoothstep falloff, 2-pass 8-neighbor Gaussian smoothing, and a worker-thread mesh job — that combination is what reads as "smooth like drawing in Blender". The headline fix is to **decouple drag sampling from tile commits**: rasterize the cursor path into an in-memory float "stroke buffer" at 60 fps, blend with smoothstep falloff + 1-pass smoothing kernel, then debounce a single ZDO/RPC commit per stroke (mouse-up or 0.5 s idle).

## 1. RPG-Maker reference architecture

### 1.1 Drag / raycast pipeline

- IH drives placement via console commands wired in `Modules/CustomTerrain/Tools/DirtFloorPlacementTool.cs:43-89` (`place_dirtfloor`, `place_cave_floor`, etc.). Each click expands `<area>` to `from=X,Z,Y circle=R offset=H` (`DirtFloorPlacementTool.cs:130-189`).
- IH itself controls the **continuous-fire rate** — RPG-maker doesn't run its own drag MonoBehaviour. It relies on the IH ruler ring + `instant: true` tool config to drive repeated `place_dirtfloor` invocations as the player moves.
- Raycast: `DirtFloorPlacementTool.cs:600-623 GetPlacementPosition` uses 50 m `Physics.Raycast` against `Default | static_solid | Default_small | piece | terrain | piece_nonsolid`, falls back to a downward 100 m raycast from (camera + forward × 10), final fallback to player feet.
- Critical gap-filling trick: **placement does not depend on cursor interpolation** because the combiner SDF blends disks together (§1.2). Two disks placed 8 m apart still produce one continuous smooth mesh.

### 1.2 Mesh combiner (DirtFloorMeshCombiner)

- **Per-disk SDF (not per-vertex grid)**: each click adds a `TerrainDisk` (position + radius + heightOffset) to the combiner's `_terrainDisks` list (`DirtFloorMeshCombiner.Core.cs:345-399 AddTerrainDisk`).
- **Grid resolution**: actual target `targetVertsPerMeter = 2f` (`DirtFloorMeshCombiner.MeshGen.cs:769`) — **2× FAT's vertex density**.
- **Overlap blend math** — `DirtFloorMeshCombiner.MeshGen.cs:971-993 CalculateBlendWeight`:
  - `BlendDistance = 1f` falloff band.
  - Inside (edgeDist ≥ blend): weight = 1.
  - Edge band: `t = edgeDist / blend; w = t² · (3 − 2t)` — Hermite smoothstep.
  - Outside band (within blend distance): `w = smoothstep · 0.5` (half-strength tail).
  - Each grid vertex sums weighted heights from every overlapping disk: `weightedHeight / totalWeight` (`MeshGen.cs:944-959`).
- **Reference-Y clamping** prevents long legs: `MaxHeightDifference = 8f` (`Core.cs:42`). Anything beyond is hard-clamped to `referenceY ± 8m`.
- **Smoothing pass**: `SmoothHeightEdges` (`MeshGen.cs:998-1050`) runs **2 passes** of 8-neighbor Gaussian (cardinals 1.0, diagonals 0.5, center 4.0). Only LERPs toward neighbor average when delta > 0.05 m, with `blendFactor = clamp01(diff · 0.5)` — preserves sharp intentional edges while dissolving brush-stamp terraces.
- **Gap-fill**: `FillHeightGaps` (`MeshGen.cs:1055-1137`) runs 3 passes of 8-neighbor averaging on cells marked invalid but within `BlendDistance × 1.5` of any disk's edge. Visually closes ragged areas between stamps.
- **Async builder**: `DirtFloorAsyncMeshBuilder.cs:96-105 QueueMeshJob` → `BackgroundWorker<MeshJobInput, MeshJobOutput>` runs height-sample + vertex/triangle/UV/biome-color generation on a worker thread. Main thread only does `Mesh.SetVertices/SetTriangles/RecalculateNormals`. Co-driven by `RegenerateMeshAsync` (`MeshGen.cs:181-354`) coroutine that yields between phases.
- **Debounced commit**: `Core.cs:251-253 SAVE_DEBOUNCE_TIME = 0.5f`. Each disk addition calls `QueueRegeneration` (`Core.cs:393`) with `REGENERATION_DELAY = 0.1f`. Mesh visibly updates ~100 ms after the LAST click in a stroke; ZDO + cache write fires 500 ms after the last click.
- **Cache**: `DirtFloorCombinerCache.UpdateCombinerCache` (called at end of every regen) persists height grid + mesh + paint mask to disk; cold-load of a zone never re-runs the disk sampler.

### 1.3 Why it FEELS smooth

1. **Per-disk SDF model**: a single click drops a smooth dome; multiple clicks at slightly different positions BLEND with smoothstep falloff into one shape — even if drag is choppy, the visual result is continuous because the union of overlapping disks fills any gap up to `BlendDistance`.
2. **2 verts/m mesh density** (vs FAT's 1 vert/m): doubles curvature resolution — slopes look like slopes, not staircases.
3. **2-pass 8-neighbor smoothing**: dissolves 1 m terraces at brush edges.
4. **Background-thread mesh job**: keeps main thread at 60 fps; cursor tracks mouse without hitches.
5. **0.1 s regen + 0.5 s save debounce**: spam-click produces ONE commit, ONE PhysX cook, ONE cache write — no mid-stroke stutter.

## 2. FAT current architecture

### 2.1 TileBrushDrag input pipeline

- `Persistence/TileBrushDrag.cs:32 DRAG_INTERVAL = 0.1f` → **10 Hz dispatch rate**.
- `Persistence/TileBrushDrag.cs:90-153 Update` polls `Input.GetMouseButton(0)` and re-fires the cached brush command at the current cursor position via `TileBrushRPC.SendFlat/SendRaise/SendPaint`.
- **No cursor interpolation**. If the mouse moves 5 m between ticks and brush radius is 2 m, that creates a 1 m gap.
- Raycast: `TileBrush.cs:687-744 TryGetPointerTarget` — `Physics.Raycast` 600 m, then ray-to-player-Y-plane fallback, then feet. Classifies `hitCustomTile` vs vanilla.
- Each tick → RPC fan-out + per-peer `TileBrush.Brush*` call → `TileAnchorRegistry.TilesOverlappingCircle` returns every overlapping tile → for each, `TileAnchor.ApplyOpAndSave` claims ZDO ownership, mutates vertices, writes ZDO + bumps DataRevision, calls `TileMeshRenderer.RebuildMesh` synchronously.

### 2.2 64 m tile grid (TerrainTile + TileAnchor)

- **Fixed 65×65 vertex grid per 64 m tile, 1 m spacing** (`TerrainTile.cs:24-27`, `TerrainTileCodec.cs:33 VERTEX_GRID_SIZE = 65`).
- 4225 verts, 4096 quads, 24576 indices per tile.
- **Direct per-vertex writes**: `TileBrush.cs:513-539 ApplyFlatToTile` sets `Heights[idx] = height; HeightModified[idx] = true`. No SDF blend; the new value overwrites whatever was there.
- `TileBrush.cs:541-570 ApplyRaiseToTile` applies linear-or-power falloff: `t = 1 - dist/radius; if (falloff != 1f) t = pow(t, falloff)`. NOT smoothstep; discontinuous derivative at brush edge.
- **Floating-terrain quad gating**: `TileMeshRenderer.cs:528-555` skips any quad where ANY corner is `!HeightModified`. Clean edge but at brush-stamp boundaries creates hard 1 m terrace steps.
- **No smoothing pass.** `RebuildMesh` writes verts directly from `Heights[]` and calls `RecalculateNormals()`.
- **Synchronous PhysX cook**: `TileMeshRenderer.cs:402-409 TryConsumeCookSlot` rate-limits to 1 cook/frame globally — but each touched tile during a drag tries to cook. 4 tiles × 10 ticks/sec = 40 cook attempts/sec → backlog → 100-300 ms collider lag.
- **ZDO write per tick**: `TileAnchor.cs:257-287 ApplyOpAndSave` encodes full tile data + writes ZDO + bumps DataRevision **every brush tick** that touches ≥ 1 vertex. MP fans out immediately to every peer.

### 2.3 Why it FEELS sub-par

1. **1 m vertex spacing** = visible 1 m terraces on any non-axis-aligned slope.
2. **No smoothstep falloff on flat brush** — hard cutoff; edge of brush is a vertical 1 m cliff at radius boundary.
3. **No multi-pass smoothing**; brush stamps don't dissolve into each other.
4. **No cursor interpolation** between 10 Hz ticks; fast drags leave gaps.
5. **Synchronous mesh rebuild + PhysX cook per tick per tile** + per-tick ZDO/RPC commit → main-thread stalls visible as cursor lag.
6. **Single 64 m tile grid origin**: brush spanning 2 tiles writes vertex columns at neighbor tile boundaries — deterministic but missing the SDF blur RPG-maker uses to dissolve the seam.

## 3. Concrete gap analysis

| Aspect              | RPG-Maker                                                                | FAT today                                                  | Gap                                                          |
| ------------------- | ------------------------------------------------------------------------ | ---------------------------------------------------------- | ------------------------------------------------------------ |
| Drag sample rate    | IH-driven (effectively per-frame)                                        | 10 Hz fixed (`DRAG_INTERVAL = 0.1f`)                       | FAT can't sample faster than 100 ms                          |
| Cursor interpolation | Not needed — SDF blend covers gaps up to 1 m                            | None — discrete stamps                                     | Gaps when mouse > radius/tick                                |
| Mesh combining      | Per-disk SDF + smoothstep + half-strength tail + `MaxHeightDifference`  | Per-vertex direct write, linear/power falloff, none on Flat | FAT brushes have hard edges, no inter-stamp blend            |
| Async build         | Worker thread + coroutine yield                                          | Synchronous (`TileMeshRenderer.RebuildMesh`)               | FAT stalls main thread per touched tile per tick             |
| Cache reuse         | `DirtFloorCombinerCache` persists computed mesh to disk                  | ZDO-only, no on-disk cache                                 | Cold-load re-decodes ZDO + rebuilds                          |
| Boundary smoothing  | 2-pass 8-neighbor Gaussian + 3-pass gap-fill                             | None                                                       | FAT shows 1 m terrace stairs at brush edges                  |
| Grid density        | 2 verts/m (0.5 m spacing)                                                | 1 vert/m                                                   | FAT has half the curvature resolution                        |
| Commit cadence      | 0.1 s regen debounce + 0.5 s save debounce                               | Save + RPC every tick                                      | FAT does N saves per stroke instead of 1                     |
| PhysX cook          | Async coroutine + budgeted                                               | Synchronous, 1/frame global budget                         | FAT backlogs cook queue                                      |
| LOD                 | `DirtFloorLODManager` with low-detail mesh swap                          | Render LOD on/off, no low-poly variant                     | Equivalent at typical view, gap at megabase                  |

## 4. Gameplan — Order of execution

### Phase 1: Quick wins (cheap, big visual impact)

- [x] **2-pass 8-neighbor smoothing kernel after every brush op** ✅ done 2026-05-26
  - **File**: `Persistence/TileBrush.cs` `SmoothHeightsInRadius` added; called from `BrushFlat` (non-square) + `BrushRaise` after the per-tile apply.
  - **What**: Cardinals weight 1.0, diagonals 0.5, center 4.0. Only smooths verts where `HeightModified[idx] && |neighborAvg - current| > 0.05m`. `blendFactor = clamp01(diff · 0.5)`. 2 passes.
  - **Status**: build green, DLL deployed. Awaiting visual confirmation.

- [x] **Replace hard-cutoff `BrushFlat` falloff with smoothstep** ✅ done 2026-05-26
  - **File**: `Persistence/TileBrush.cs` — new `Smoothstep01` + `BrushWeight` helpers. `ApplyFlatToTile` now lerps existing height → target with smoothstep weight across the outer `BRUSH_EDGE_BLEND_DISTANCE = 1.0f` band. `ApplyRaiseToTile` also switched to smoothstep (the legacy `falloff` arg is now ignored — kept on signature for backward compat with TileBrushRPC/TileBrushDrag).
  - **Status**: build green, DLL deployed.

- [ ] **Increase vertex density to 2 verts/m (cellSize 0.5 m) — codec v4**
  - **File**: `TerrainTileCodec.cs:33` `VERTEX_GRID_SIZE = 65 → 129`; `TerrainTile.cs:27 VERTEX_SPACING = 1f → 0.5f`. Migration: hard upsample on load (bilinear v3 → v4 once per tile).
  - **Status**: pending — schedule next, after the user confirms Phase 1 smoothing feels right.

### Phase 2: Drag pipeline parity

- [x] **Cursor-path interpolation between drag ticks** ✅ done 2026-05-26
  - **File**: `Persistence/TileBrushDrag.cs` — new `_lastInterpCursor` cache + `DispatchBrushAt` helper. On each 10 Hz tick, if cursor moved >`radius*0.5`, walks the segment in `radius*0.5` steps and dispatches the brush op at each step. Capped at 8 intermediate steps per tick.
  - **Result**: Fast drags no longer leave gaps; continuous trench / hill ridge.

- [ ] **Switch drag to local-only writes during stroke; defer RPC + ZDO commit to mouse-up** — PENDING
  - Stayed deferred this turn to keep scope focused. Cursor interp + Phase 1 smoothing already covers most of the visual win. Deferred commit is mostly an MP perf optimization that's not blocking the user. Will be next turn.

### Phase 3: Mesh combining parity

- [ ] **Per-tile SDF blend instead of pure per-vertex write (optional, big architectural shift)**
  - **File**: New `Persistence/TileStampBuffer.cs` — list of recent disks per tile. `TileMeshRenderer.RebuildMesh` swaps to sample-from-stamps the way `DirtFloorMeshCombiner.MeshGen.cs SampleHeightAtGridPoint` does.
  - **What**: Each brush click appends a `Stamp { centerXZ, radius, height, falloffKind }` to the tile's stamp list. RebuildMesh derives Heights[] by accumulating weighted heights from every stamp with smoothstep falloff.
  - **Why**: User explicitly called out RPG-maker's "smooth, like drawing in Blender" feel — that's the SDF model. Without this, FAT is fundamentally a 1-bit "vert is modified or not" system.
  - **Acceptance**: Place two overlapping 5 m Floor brushes 6 m apart at same Y — produces one continuous smooth peanut shape, not two discrete disks with a hard 1 m terrace at meeting point.
  - **Caveat**: Phase 8e-class architectural change. Storing N stamps/tile costs ZDO bytes; cap at ~32 stamps/tile and bake into Heights[] when capped.

- [ ] **Port `FillHeightGaps` 3-pass neighbor-averaging fill**
  - **File**: `Persistence/TileBrush.cs` add post-op gap-fill on tiles touched in the last brush op.
  - **What**: For each unmodified vertex within `1.5 · BLEND_DISTANCE` of a modified neighbor, set `Heights[idx] = avgOfModifiedNeighbors` and `HeightModified[idx] = true`. Stop when no more fills, max 3 passes.
  - **Why**: Closes ragged inter-stamp gaps without the full SDF rewrite.
  - **Acceptance**: Jagged hand-drawn line of Floor brushes shows continuous filled outline.

### Phase 4: Async build + caching

- [ ] **Move `TileMeshRenderer.RebuildMesh` vertex/triangle gen to worker thread**
  - **File**: `Persistence/TileMeshRenderer.cs:419-671 RebuildMesh`; new `Persistence/TileMeshJob.cs`.
  - **What**: Port `DirtFloorAsyncMeshBuilder` pattern. Main thread snapshots `Heights[] + HeightModified[] + Paint[] + Biome[] + BiomeModified[]` into a thread-safe input; worker computes `_vertices/_uvs/_colors/_triangles`; main thread does `mesh.SetVertices/...` + `RecalculateNormals`. Use existing `BackgroundWorker<TIn, TOut>` infrastructure.
  - **Why**: Removes 5-25 ms main-thread stall per touched tile per tick. Drag feels weightless.
  - **Acceptance**: Profile shows `RebuildMesh` main-thread cost drops to ~1 ms (Mesh API only); worker shoulders the per-vertex loop.

- [ ] **On-disk mesh cache (port `DirtFloorCombinerCache`)**
  - **File**: New `Persistence/TileMeshCache.cs`.
  - **What**: After stroke commits, save Heights[] + Vertices/Triangles to `<world>/VATileCache/tile_X_Z.bin`. On `TileAnchor.Awake`, if cache hit AND `LastSeenDataRevision` matches, skip ZDO decode + RebuildMesh and bind cached Mesh directly.
  - **Why**: Cold-load of megabase re-decodes every tile ZDO + runs RebuildMesh. Cache-first matches RPG-maker.
  - **Acceptance**: Reload a saved 100-tile area; second load ~5× faster.

- [ ] **Async PhysX cook**
  - **File**: `Persistence/TileMeshRenderer.cs:618-628`
  - **What**: Replace synchronous `_meshCollider.sharedMesh = _colliderMesh` (cooks on assign) with `Physics.BakeMesh(mesh.GetInstanceID(), convex: false)` queued via `Task.Run`, then assign on main thread when complete.
  - **Why**: Removes 10-50 ms cook stalls during drag. Combined with worker-thread RebuildMesh eliminates ALL main-thread stalls during a stroke.
  - **Acceptance**: Profile shows zero spikes > 2 ms during sustained Raise drag across 4 tiles.

### NEW (round-30 user requests, 2026-05-26)

- [x] **Floating-island mode** (new tool variant + dome underside)
  - **Files**: `TerrainTile.cs` (`FloatingIslandMode` field) · `TerrainTileCodec.cs` (codec v4 tail-append, v3 readers ignore) · `TileMeshUnderside.cs` (`ShouldUseDomedUnderside` + `BuildDomedUnderside`) · `TileBrush.cs` (`BrushFlat` / `BrushRaise` accept `islandMode` flag) · `TileBrushRPC.cs` (RPC carries flag, backwards-compat via `TryReadBoolTail`) · `TileBrushCommands.cs` (`va_brush_flat_island` / `va_brush_raise_island` / `va_brush_lower_island`) · `TileBrushDrag.cs` (drag re-dispatch propagates flag).
  - **Rule**: tile flagged for island mode renders a domed teardrop underside (max-depth 30% of horizontal extent, smoothstep falloff, 0.5m edge inset) when its top sits ≥50m above local terrain. Below 50m, falls back to default "stretch to terrain" behavior. Standard `va_brush_flat` / `_raise` / `_lower` unchanged — old YAML tools work the same.
  - **YAML tools added**: `Floor (Floating Island)`, `Raise Terrain (Floating Island)`, `Lower Terrain (Floating Island)`.

- [x] **Clutter clamp on sculpt**
  - **File**: `TerrainTile.cs:NotifyMutated`
  - **What**: After every mesh rebuild, fires `ClutterManager.RespawnInRadius(tileWorldCenter, TILE_SIZE/2 + 4m)`. That helper calls vanilla `ClutterSystem.ResetGrass` which re-rolls grass + flower placement at the CURRENT terrain height. Prevents the floating-clutter screenshot the user posted.
  - **Note**: fires on every brush tick during drag (10 Hz). Vanilla `ResetGrass` is cheap (just re-rolls patch positions); haven't observed thrashing but if it becomes a problem can throttle to 1× per tile per second.

### Phase 5: Beyond parity — FAT should be BETTER

- **Multi-zone tile awareness already exists** (`TileAnchorRegistry.TilesOverlappingCircle`) — RPG-maker hand-rolls zone connectivity via `DirtFloorZoneRegistry`. FAT's deterministic-grid model means cross-zone seams are EXACT by construction (no float-precision drift) once SDF smoothing lands.
- **Per-vertex biome paint** (`BiomeModified[] + Biome[]`) is already finer than RPG-maker's per-disk paint. Document as feature.
- **Undo log** (`TileUndoLog`) is richer — RPG-maker's `TerrainUndoManager` is per-combiner; FAT's is per-stroke with multi-tile delta capture.
- **MP broadcast already in place** via `TileBrushRPC` — RPG-maker uses request/server-apply which costs round-trip. FAT's deterministic args let peers apply locally.
- **Future extension**: layered tiles (cave floor vs ceiling vs bridge). RPG-maker uses `TerrainDiskType` + `IsolationGroupId` (`DirtFloorTerrainManager.cs:51-117`). FAT's grid is single-layer today; a 2-layer extension (top + bottom Heights[]) would do cave overhangs without the isolation-group bookkeeping.

## 5. Risk + sequencing notes

- **Phase 1 is safe + order-independent.** Localized improvements, no schema/network touch.
- **Phase 2 cursor interpolation independent of mesh changes.** Do FIRST after Phase 1.
- **Phase 2 deferred-commit MUST land before Phase 4 async-cook** — otherwise async cook queues stale data before local buffer flushes.
- **Phase 3 SDF blend is most disruptive** — changes data model (Heights[] becomes derived from Stamp list). Schedule LAST behind a config flag (`ConfigEnableTileSDF`).
- **Codec v4 grid-density bump requires migration testing.** v3→v4 upsampler on load; keep v3 reader 30 days.
- **PhysX async cook requires Unity 2022.3+** — verify before scheduling.
- **DO NOT remove no-op-skip in `TileAnchor.ApplyOpAndSave:282`** — load-bearing for partial-tile-overlap perf.

## 6. Open questions — ANSWERED 2026-05-26

1. **Full Blender feel?** ✅ YES — all 4 phases. User wants RPG-maker behavior + a little smoother. Brush lower-drag should carve a clean path; raise-drag should sculpt smooth hills.
2. **Codec v4 migration**: ✅ HARD UPSAMPLE ON LOAD. Bilinear `65×65 → 129×129` on first read of a v3 tile. Clean break, no dual-path. Old clients can't read new saves — acceptable for admin-only mod.
3. **Mesh cache** (Phase 4): ✅ YES — build it. Cold-load megabase is the dominant cost; SDF stamps will make ZDO replication more frequent.
4. **Drag tick rate**: ✅ 60 Hz (per-frame) **after Phase 4 async build lands**. Until then stay at 10 Hz. Phase 2 cursor interp covers gaps in the meantime.
5. **Raise/Lower falloff**: ✅ SMOOTHSTEP UNIVERSALLY. `t²·(3-2t)` for Raise + Lower + Flat. Drops the `falloff=` YAML arg.

## 7. Estimated impact summary

| Phase | Effort | Risk | Visual impact | MP impact | Perf impact |
|---|---|---|---|---|---|
| Phase 1 (smoothing + smoothstep + density) | 1-2 days | low (codec rev) | +++ | none | -1 ms/rebuild |
| Phase 2 (interp + deferred commit) | 1 day | low | ++ | +++ (90% less RPC) | +++ (no mid-stroke stalls) |
| Phase 3 (SDF blend) | 1-2 weeks | high | ++ (the last 30%) | none | ZDO bytes ↑ |
| Phase 4 (async build + cache + async cook) | 3-5 days | medium | none | none | +++ (smooth 60 fps drag) |
| Phase 5 (beyond) | ongoing | varies | varies | none | varies |
