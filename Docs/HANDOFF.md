# FiresAdminTerrain — Build & Phase Handoff

**As of 2026-05-24** — Phase 8c complete + Phase 8d toggle scaffolded. Build green.

---

## Current state

### What works today

- **Strip + port** (Phases 1-6): FiresAdminTerrain.dll builds cleanly from a stripped FiresNPCs copy. CustomTerrain + CustomWater + AdminSyncing + slim ConfigManager + ConfigSync. ~5MB output.
- **New tile-anchor pipeline** (Phases 8a-8c): full ZDO round-trip via debug commands. Anchors at Y=-5000, 64m tiles, vanilla `CheckLoad` ZDO replication pattern, sparse gzip codec for heights/paint/disks.
- **VATile prefab**: cloned from `piece_dirtfloor_vad` at `ZNetScene.Awake` via Harmony postfix. Aggressive component strip (whitelist Transform + ZNetView).
- **Feature toggle**: `Terrain.UseNewTilePersistence` config (default OFF). When ON, the new path runs alongside the old (no entry-point branching yet — toggle exists for the routing in Phase 8d-2).

### What's NOT yet wired

- **Op entry-point branching**: existing tools (`DirtFloorPlacementTool`, height ops, paint ops) still write through the legacy `TerrainCombinerAnchor` / `DirtFloorTerrainCompStorage` / `TerrainPersistence` / `DirtFloorSaveManager`. Toggling the config flag has no effect on those paths yet.
- **Disk → tile height mapping**: `TilePersistence.RecordDiskOnOverlappingTiles` records the disk metadata but does NOT compute per-vertex height/paint changes from the disk footprint. That's Phase 8e.
- **Multiplayer verification**: not yet tested. The vanilla `CheckLoad` pattern *should* work but needs two-client smoke test.
- **Clutter replacement**: deferred. Old `Modules/CustomTerrain/Clutter/` + `GrassOverlaySystem.cs` + `DirtFloorMeshCombiner.Grass.cs` still in place. Plan: see [CLUTTER_REPLACEMENT_PLAN.md](CLUTTER_REPLACEMENT_PLAN.md).

---

## How to test the new path today

Drop `bin/Debug/FiresAdminTerrain.dll` into `BepInEx/plugins/`. Load a world (singleplayer or hosted). Open the F5 console.

```
va_tile_prefab_ensure
```
Should print `VATile prefab registered. Hash=<some int>`. If not, the bundle's `piece_dirtfloor_vad` source prefab is missing — check the log for FiresAdminTerrain bundle load errors.

```
va_tile_persist 0 0 5
```
Spawns a 64m × 64m flat tile at world origin, Y=5. A dirt-colored slab should appear. The log shows:
```
[FiresAdminTerrain] [TileAnchor] tile=(0,0) Awake | owner=True | dataRev=0
[FiresAdminTerrain] [TileAnchor.Spawn] tile=(0,0) spawned at (32.0, -5000.0, 32.0)
[FiresAdminTerrain] [TileAnchor] tile=(0,0) saved | payload=NNN B | new rev=1
```

```
va_tile_persist_slope 1 0 0 8
```
Sloped tile in the next 64m zone east. Should butt up against the first tile cleanly because both share border vertices computed identically.

```
va_tile_load 0 0
```
Dumps the loaded state from the registry — confirms the in-memory `TerrainTile` has the heights you wrote.

**Save world, exit to main menu, reload.** The tiles should re-appear via ZoneSystem streaming. That's the ZDO replication path working — the proof point for the whole redesign.

```
va_tile_destroy 0 0
va_tile_destroy 1 0
```
Cleanup.

---

## Console commands reference

| Command | Phase | Effect |
|---|---|---|
| `va_tile_render <x> <z> <h>` | 8b | Standalone tile mesh test (no ZNetView) |
| `va_tile_render_slope <x> <z> <minH> <maxH>` | 8b | Standalone slope test |
| `va_tile_clear_test` | 8b | Destroy va_tile_render test objects |
| `va_tile_prefab_ensure` | 8c | Force-register VATile prefab |
| `va_tile_persist <x> <z> <h>` | 8c | Spawn TileAnchor + ZDO round-trip |
| `va_tile_persist_slope <x> <z> <minH> <maxH>` | 8c | Sloped persist variant |
| `va_tile_load <x> <z>` | 8c | Dump existing anchor state |
| `va_tile_destroy <x> <z>` | 8c | Destroy anchor + ZDO |
| `va_tile_list` | 8c | List all registered anchors |
| `va_brush_flat [r=10] [h=auto]` | 8d-test | Flat platform at look position |
| `va_brush_raise [r=10] [delta=1] [falloff=1]` | 8d-test | Raise mound, falloff 1=linear, 2+=sharper edge |
| `va_brush_paint [r=10] [R=255] [G=0] [B=0] [A=255]` | 8d-test | Paint color in radius; default red=dirt |
| `va_brush_clear [r=10]` | 8d-test | Clear modified verts back to unmodified |
| `va_brush_where` | 8d-test | Print pointer position + which tile it's in |

All commands are `isCheat: true` — enable cheats with `devcommands`.

The `va_brush_*` family aims at the player's camera raycast hit (max 50m). Use it like a hammer — look at the ground and call the command. No IH required; if IH is installed the ruler-ring patch still works.

---

## File map — new code

```
Modules/CustomTerrain/Persistence/
  TerrainTileCodec.cs       (230 lines)  Sparse gzip encode/decode
  TerrainTile.cs            (180 lines)  Per-tile data holder MonoBehaviour
  TileAnchorRegistry.cs     (115 lines)  Vector2i tile index → TileAnchor lookup
  TileAnchor.cs             (290 lines)  ZNetView component, vanilla CheckLoad
  TileAnchorPrefab.cs       (160 lines)  Clone+register VATile on ZNetScene.Awake
  TileMeshRenderer.cs       (170 lines)  Per-tile mesh generation
  TileDebugCommands.cs      (270 lines)  9 debug commands
  TilePersistence.cs        ( 70 lines)  Façade for Phase 8d entry-point branching
  TileBrush.cs              (300 lines)  Hammer-style brush API (Flat/Raise/Paint/Clear)
  TileBrushCommands.cs      (130 lines)  va_brush_* console commands
```

Total: ~1915 lines of new code.

---

## Next session — Phase 8d-2 plan

The remaining entry-point branching work, in order:

1. **Wrap legacy save methods** with `if (TilePersistence.IsEnabled) return;` early-returns:
   - `DirtFloorTerrainCompStorage.SaveDisk(...)` (1 method, many call sites)
   - `DirtFloorTerrainCompStorage.RemoveDisk(...)` (1 method)
   - `DirtFloorSaveManager.MarkDirty(...)` (1 method, ~20 call sites)
   - `TerrainPersistence.SaveTerrainData()` / `LoadTerrainData()` / `ClearCaches()` (3 methods)
   - `TerrainCombinerAnchor.SaveToZdo` and related Y=0 anchor logic
   
   Method-level early-return is fewer touch points than call-site-level.

2. **Add new-path call** mirror at each disk-mutation site:
   - `DirtFloorController.cs:470` — after disk is initialized/placed
   - `DirtFloorController.cs:378` — when disk is destroyed (call `TilePersistence.RemoveDiskFromOverlappingTiles`)
   - `DirtFloorMeshCombiner.HeightOps.cs` — at each `DirtFloorSaveManager.MarkDirty(disk)` site, also call the new path
   - Need a `BuildDiskRecord(DirtFloorController) → DiskRecord` helper that maps disk fields

3. **Disk → tile height application** (the missing piece that makes the new path actually produce a visible mesh from disk-place ops). Today's `TilePersistence.RecordDiskOnOverlappingTiles` only records metadata. To match the existing behavior, port the relevant code from `DirtFloorMeshCombiner.HeightOps` that computes vertex heights from a disk's center/radius/baseHeight. ~150 lines.

4. **Build verify after each substep.** Don't batch step 1+2+3 — green-build per substep.

5. **Live test**: toggle ON, place a disk, verify it persists across reload via the new path.

---

## Phase 8e (after 8d-2 lands)

Deletion of the old persistence files. Files to delete:
- `Modules/CustomTerrain/Core/DirtFloorTerrainCompStorage.cs` (~830 lines)
- `Modules/CustomTerrain/Core/TerrainPersistence.cs` (~470 lines)
- `Modules/CustomTerrain/Core/DirtFloorSaveManager.cs` (~340 lines)
- `Modules/CustomTerrain/Core/DirtFloorCombinerCache.cs` (~1900 lines)
- `Modules/CustomTerrain/Core/TerrainCombinerAnchor.cs` Y=0 anchor logic (file may be replaced by new Tile* family, or trimmed to just legacy stub if anything else still references it)

Cascading using-statement cleanup in: DirtFloorPatches, DirtFloorTerrainManager, DirtFloorController, DirtFloorMeshCombiner.{Core,DiskManagement,Lifecycle,MeshGen,Static,HeightOps}.cs, TerrainOperationRPC, ZonePaintManager, TerrainUndoManager, TerrainDebugCommands, ClutterPaintTool, CustomHeightmap.

---

## Phase 8f — Multiplayer verify

Two-client smoke test:
1. Host edits a tile via `va_tile_persist 5 5 10`.
2. Client connects, walks to tile (5, 5). Verify the tile appears on the client.
3. Client edits the tile via `va_tile_persist 5 5 15`. Verify the host sees the new height.
4. Both clients logout, host re-loads. Verify the tile persists at h=15.
5. Re-test with two clients in different zones simultaneously, both editing — verify no ZDO collisions.

---

## Plans (read these first when resuming)

- [TERRAIN_PERSISTENCE_REDESIGN_PLAN.md](TERRAIN_PERSISTENCE_REDESIGN_PLAN.md) — full design with locked decisions
- [CLUTTER_REPLACEMENT_PLAN.md](CLUTTER_REPLACEMENT_PLAN.md) — FAP source-link strategy
- [TERRAIN_WATER_PERSISTENCE_BUGFIX_TRACKER.md](TERRAIN_WATER_PERSISTENCE_BUGFIX_TRACKER.md) — original bug analysis (FiresRPGmaker era)
