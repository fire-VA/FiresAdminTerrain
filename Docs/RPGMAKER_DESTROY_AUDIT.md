# RPG-maker Terrain Destruction Audit

Source: `C:\Users\travi\OneDrive\Desktop\valheimRip2\FiresNPCs\FiresNPCs`
Date: 2026-05-25
Purpose: Inventory every path that destroys/clears/wipes custom terrain so the new
FiresAdminTerrain mod can collapse them into one central remove API.

---

## A. Console commands

### A1. `terrain_clear_radius [radius=50]`
- Entry: `Modules/CustomTerrain/Tools/TerrainDebugCommands.cs::ClearTerrainInRadius`
- Clears: combiner mesh height pixels + TerrainDisk data + legacy `DirtFloorController` GameObjects + clutter
- Call chain:
  1. `DirtFloorTerrainManager.RemoveTerrainInRadius(center, radius, 0, true)` (aggressive, `ignoreHeight=true`)
     → MP branch: `TerrainOperationRPC.RequestRemoveTerrain` → RPC to everyone → `RemoveTerrainInRadiusInternal` on each peer
     → SP/handler branch: `DirtFloorTerrainManager.RemoveTerrainInRadiusInternal`
        → iterates `DirtFloorZoneRegistry.GetAllCombiners()`
        → for each affected combiner: `combiner.PunchHole(center, radius, MinValue, MaxValue)`
           → punches `_heights[idx] = -100f` cells
           → removes `TerrainDisk` entries that overlap (`RemoveTerrainDisk`)
           → calls `ClearPaintMaskInRadius`
           → calls `ClearGrassInRadius(center, radius)`
           → calls `ClutterManager.ClearInRadius(center, radius)` (yes, again)
           → if combiner becomes empty: sets `_isPermanentlyDeleted=true`,
             calls `DirtFloorCombinerCache.OnCombinerPermanentlyDeleted` (which
             marks the position deleted and removes from disk cache),
             calls `Anchor.DestroyPermanently()` (clears ZDO bytes for terrain/height/paint/bounds,
             zeroes the version, flips `va_is_terrain_anchor=false`, then `ZNetScene.Destroy`),
             and `Destroy(gameObject)`
        → `ClutterManager.ClearInRadiusPermanent(center, radius)` (yet again, with persistence)
        → `DirtFloorCombinerCache.SaveCache()`
  2. After the manager call, iterates `DirtFloorController.AllDirtFloors` and calls
     `DirtFloorTerrainManager.RemoveDisk(disk)` on every legacy disk in range
     → finds combiner, calls `combiner.RemoveDisk(disk)` (routes to
     `DirtFloorZoneRegistry.RemoveDisk` which destroys empty combiners) → `ZNetScene.Destroy` the disk
  3. `DirtFloorCombinerCache.SaveCache()` (third save in this command)
- Persistence: yes — combiner cache file rewritten; anchor ZDOs wiped; clutter persistence cleared.
- Network: yes — `RPC_REMOVE_TERRAIN` broadcast to `Everybody`.

### A2. `terrain_clear_zone`
- Entry: `TerrainDebugCommands.ClearTerrainInZone`
- Snaps to current 64m zone, then runs `DirtFloorTerrainManager.RemoveTerrainInRadius`
  9 times (3×3 zone grid). Each call goes through the full path above.
- Then iterates `DirtFloorController.AllDirtFloors` within `clearRadius * 1.5f` and removes
  each via `RemoveDisk`. Saves cache once at end. Each inner call also broadcasts an RPC.

### A3. `terrain_clear_all confirm`
- Entry: `TerrainDebugCommands.ClearAllTerrain`
- The "nuclear" option. Iterates `DirtFloorZoneRegistry.GetAllCombiners()`:
  - reflectively sets `_isPermanentlyDeleted=true`
  - `DirtFloorCombinerCache.OnCombinerPermanentlyDeleted(combiner)`
  - `combiner.Anchor.DestroyPermanently()` (clears every ZDO byte key + version)
  - `UnityEngine.Object.Destroy(combiner.gameObject)`
- Then iterates `DirtFloorController.AllDirtFloors` → `RemoveDisk`
- Then `DirtFloorCombinerCache.ClearCache()` + `SaveCache()`
- Does NOT broadcast RPCs. Local-only nuclear wipe; relies on each peer running it.

### A4. `delete_dirtfloor` (hammer tool via IH)
- Entry: `Modules/CustomTerrain/Tools/DirtFloorPlacementTool.cs::DeleteDirtFloor` → `ExecuteDelete`
- Parses IH `from=`/`circle=`/`height=`. `height=0` collapses to "no Y constraint"
  (uses `float.MinValue`/`MaxValue`).
- Calls `DirtFloorMeshCombiner.PunchHoleAt(position, radius, yMin, yMax)` — the static
  version that iterates `AllCombiners`, filters by bounds, and calls
  `combiner.PunchHole(...)` on each match (same call chain as A1).
- ALSO removes legacy `DirtFloorController` disks fully inside radius and within Y bounds
  via `DirtFloorTerrainManager.RemoveDisk`.
- Calls `ClutterManager.ClearInRadius(position, radius)` at end.
- Wraps in `TerrainUndoManager.BeginOperation`/`EndOperation`.
- Persistence: PunchHole's internal path saves to anchor & cache when terrain remains;
  full anchor destruction when combiner empties.
- Network: PunchHole path is LOCAL only on this entry (no RPC) — IH command runs the
  command on each peer if multiple peers are using it. The MP-sync version is
  `TerrainOperationRPC.RequestPunchHole` (only called by ad-hoc code, not by `delete_dirtfloor`).

### A5. `punch_terrain_hole` (hammer tool / console)
- Entry: `DirtFloorPlacementTool.PunchTerrainHole`
- Raycasts to identify ONE target combiner, then `targetCombiner.PunchHolePixelsOnly(...)`
  - This variant ONLY edits `_heights[]` pixels (no `TerrainDisk` removal, no clutter clear).
  - Calls `ClearGrassInRadius(center, radius)` if any pixels removed.
  - Triggers a partial mesh refresh, NOT full regeneration.
- Persistence: relies on the combiner's debounced save (`_pendingSave` → `SaveToAnchor`).
- Network: local only.

### A6. `terrain_underside on|off`, `terrain_isolated_mode`, `terrain_undo*`, `terrain_check`, `terrain_uv_*`,
`dump_terrain_*`, `terrain_test_paint`, `terrain_paint_*`, `terrain_refresh_material`,
`terrain_fix_bounds`, `terrain_regenerate`, `terrain_cleanup`, `place_*`, `patch_terrain_hole`
- None of these directly clear terrain except `terrain_cleanup` →
  `DirtFloorMeshCombiner.PerformGlobalCleanup()` (removes overlapping combiners — it can
  destroy a combiner if it overlaps another at the same elevation).

---

## B. Hammer / TerrainOp interception

### B1. `TerrainOp.Awake` (vanilla hoe + IH terrain tools)
- Patch: `Modules/CustomTerrain/Tools/InfinityHammerCompat.cs::TerrainOp_Awake_Prefix`
- For any TerrainOp placed over our terrain:
  - `m_level` → `DirtFloorTerrainManager.LevelArea` (uses RPC in MP)
  - `m_raise` → `DirtFloorTerrainManager.RaiseArea` / `LowerArea` (RPC)
  - `m_smooth` → `DirtFloorTerrainManager.SmoothArea` (RPC)
  - `m_paintCleared` → `DirtFloorTerrainManager.PaintArea`
    - Paint operations also call `ClutterManager.ClearInRadius` for paved/dirt/cultivated.
- The TerrainOp itself is NOT skipped — vanilla terrain is also modified
  (which is why FiresAdminTerrain wants to block this).

### B2. `Modules/Territories/TerritoryRuntime.cs::TerrainComp_ApplyOperation_Prefix`
- Separate concern (territory PvP). Cancels `TerrainOp.Apply` over protected territory.
- Not a clear path per se, but it's another TerrainOp hook to be aware of.

---

## C. Multiplayer RPC handlers (all in `Core/TerrainOperationRPC.cs` + `Paint/TerrainPaintRPC.cs`)

### C1. `FiresRPGmaker_Terrain_Remove` — `RPC_HandleRemoveTerrain`
- Same path as A1: `DirtFloorTerrainManager.RemoveTerrainInRadiusInternal` + lazy-save queue.

### C2. `FiresRPGmaker_Terrain_ClearZone` — `RPC_HandleClearZone`
- Identical to Remove but always `ignoreHeight=true`.

### C3. `FiresRPGmaker_Clutter_ClearSync` — `RPC_HandleClutterClear`
- Calls `ClutterPaintTool.ClearClutterInternal(center, radius)` (despawns painted clutter).
- Server-only: `ClutterPersistence.RecordClutterClear(center, radius)` (rewrites file cache).

### C4. `FiresRPGmaker_Biome_ClearSync` — `RPC_HandleBiomeClear`
- Calls `BiomePaintPersistence.ResetBiomeOverlayInternal(center, radius)`:
  - On custom terrain: `combiner.Anchor.RecordBiomeClear` + `combiner.ResetBiomeOverlayVertexColors`
  - On vanilla: prunes `_activePaints` cache + rewrites the owning `TerrainComp` ZDO bytes
  - Always: `ClutterSystem.instance.ResetGrass(center, radius)`
  - Always: `ForceHeightmapRegeneration` (calls `hmap.Regenerate()` on every overlapping hmap)
- Server-only: `BiomeFilePersistence.RecordBiomeReset` (prunes server-wide cache)

### C5. `FiresRPGmaker_Terrain_ClearPaint` — `RPC_HandleClearPaintRequest`
- Calls `combiner.ClearPaintInRegion(center, radius)` + `combiner.SavePaintMaskToTerrainComp()`.

### C6. `FiresRPGmaker_Clutter_Clear` / `FiresRPGmaker_Biome_Clear` (the request RPCs in TerrainPaintRPC.cs)
- Forward to `BiomePaintPersistence.RecordClutterClearDirect` / `RecordBiomeClearDirect`.
  These rewrite the `TerrainComp` ZDO's `va_biome_paints` / `va_clutter_paints` byte arrays.

---

## D. Module-level clear methods (called by the above)

### D1. `DirtFloorTerrainManager.RemoveTerrainInRadiusInternal`
- The canonical "remove terrain in radius" path. Documented above (A1).

### D2. `DirtFloorTerrainManager.RemoveDisk(DirtFloorController)`
- Single-disk removal. Calls `combiner.RemoveDisk(disk)` → `DirtFloorZoneRegistry.RemoveDisk`
  → if combiner becomes empty (no terrain disks + no legacy disks), `SafeDestroy(combiner.gameObject)`.
- Then `ZNetScene.instance.Destroy(disk.gameObject)` (sends ZDO destroy RPC).

### D3. `DirtFloorMeshCombiner.PunchHole` (instance) and `.PunchHoleAt` (static)
- Already detailed in A1. Key effects:
  - Removes overlapping TerrainDisk entries
  - Marks `_heights[idx] = -100f` for cells in radius
  - `ClearPaintMaskInRadius`, `ClearGrassInRadius`, `ClutterManager.ClearInRadius`
  - If combiner empties: `_isPermanentlyDeleted=true`, `DirtFloorCombinerCache.OnCombinerPermanentlyDeleted`,
    `Anchor.DestroyPermanently()`, `Destroy(gameObject)`.

### D4. `DirtFloorMeshCombiner.PunchHolePixelsOnly`
- Lightweight variant — only edits the height grid, calls `ClearGrassInRadius`,
  partial mesh refresh. No TerrainDisk removal. No anchor destruction.

### D5. `BiomePaintPersistence.ResetBiomeOverlay` / `ResetBiomeOverlayInternal`
- Detailed in C4. Two persistence branches (custom anchor vs vanilla TerrainComp ZDO),
  both bookended by `ClutterSystem.ResetGrass` and `ForceHeightmapRegeneration`.

### D6. `BiomePaintPersistence.ClearAllClutterPersistence`
- Iterates `FindObjectsByType<TerrainComp>()` and zeroes the `va_clutter_paints` ZDO byte array
  on every owned TerrainComp.

### D7. `ClutterManager.ClearInRadius(center, radius)`
- Three steps:
  1. `combiner.ClearGrassInRadius(center, radius)` on every combiner in `radius+5f`
     (despawns `_spawnedGrass[]` instances; does NOT touch persistence)
  2. `ClutterPaintTool.ClearClutter(center, radius)` (despawns painted clutter instances)
     + `BiomePaintPersistence.RecordClutterClear(center, radius)` (rewrites TerrainComp ZDO)
  3. `ClutterSystem.instance.ResetGrass(center, radius)` (vanilla grass)

### D8. `ClutterManager.ClearInRadiusPermanent(center, radius)`
- `ClearInRadius` PLUS an extra `BiomePaintPersistence.RecordClutterClear` at the end
  (defense-in-depth duplicate of step 2 above).

### D9. `ClutterManager.ClearAllPainted()` → `ClutterPaintTool.ClearAllClutter()`
- Destroys every painted-clutter GameObject in the world.
- Also calls `BiomePaintPersistence.ClearAllClutterPersistence()` (D6).

### D10. `TerrainCombinerAnchor.DestroyPermanently`
- The "really really gone" anchor cleanup:
  - Unlinks combiner
  - Sets `va_terrain_version=0`, `va_is_terrain_anchor=false`
  - Zeroes `va_terrain_data`, `va_height_data`, `va_paint_data`, `va_bounds_data`
  - Calls `ZNetScene.instance.Destroy(gameObject)` (sends ZDO destroy network-wide)
  - Note: does NOT zero `va_clutter_data`, `va_biome_data`, `va_zpaint_bundle` — those are
    technically orphaned bytes after this call.

### D11. `DirtFloorCombinerCache.OnCombinerPermanentlyDeleted(combiner)`
- Calls `MarkPositionAsDeleted(combiner.transform.position)` (adds to a "do not restore" set)
- Removes the group from `_groupToCombiners`, `_restoredGroups`, `_groupsBeingInstantiated`
- Removes the `GroupData` (vertices/disk ZDO ids/heights/bounds) from `_currentCache.Groups`
- Calls `SaveCache()` immediately (forces a file rewrite — the only place that bypasses the
  batched/debounced save timer).

### D12. `DirtFloorCombinerCache.ClearCache()`
- Nukes the in-memory cache structures (`_currentCache.Groups.Clear()`, `_assignedDiskIds.Clear()`,
  etc.). Called by A3 (`terrain_clear_all`) and on world unload.

### D13. `TerrainCombinerAnchor.RecordBiomeClear` / `RecordClutterClear`
- Per-anchor in-memory list pruning (`_biomePaints.RemoveAll`, `_clutterPaints.RemoveAll`)
  then `SaveBiomeData()` / `SaveClutterData()` which rewrites the anchor ZDO bytes.

### D14. `DirtFloorMeshCombiner.ClearGrassInRadius` / `ClearGrass`
- `ClearGrass()`: destroys ALL `_spawnedGrass[]` entries on this combiner.
- `ClearGrassInRadius(c, r)`: only entries within radius.
- These are pure runtime — no persistence side. Persistence is in clutter records, not grass instances.

### D15. `DirtFloorMeshCombiner.ClearPaintMaskInRadius` / `ClearPaintInRegion`
- Sets paint mask texture pixels to black in radius. Triggers `_pendingPaintSave`.

---

## E. Lifecycle / unload paths

### E1. `DirtFloorMeshCombiner.OnDestroy`
- If `_pendingSave && !_isPermanentlyDeleted` → `ForceImmediateSave()` (save to anchor first).
- If `!_isPermanentlyDeleted` → `DirtFloorCombinerCache.OnCombinerDestroyed(this)` (caches mesh
  vertices/heights so a reload can restore — does NOT mark deleted).
- Destroys runtime mesh, LOD mesh, material, paint mask texture.
- DOES NOT touch the anchor ZDO (anchor is its own GameObject; survives combiner destruction
  via its own ZNetView).

### E2. `DirtFloorPatches.ZNet_Shutdown_Prefix`
- `DirtFloorCombinerCache.SaveCache()` + `OnWorldUnload()`
- `TerrainPersistence.ClearCaches()`, `ClutterPersistence.ClearCaches()`,
  `BiomeFilePersistence.ClearCaches()`, `TerrainOperationRPC.ClearOperationCache()`,
  `TerrainUndoManager.ClearHistory()`
- This clears in-memory state only. File-cache contents are saved first.

### E3. `DirtFloorPatches.Game_Logout_Postfix`
- Same set of calls as E2, plus `ResetInitialized()` on each RPC class so they re-register.

### E4. `BiomePaintPatches.ZNetScene_OnDestroy_Prefix`
- `BiomePaintPersistence.OnWorldUnload` — destroys all spawned clutter, clears `_activePaints`,
  resets the "restored" set.

### E5. `BiomePaintPatches.TerrainComp_OnDestroy_Postfix`
- Per-TerrainComp cleanup (zone unload). Destroys spawned clutter for that TerrainComp.
  Note: does NOT prune `_activePaints` since the data may be needed when zone reloads.

### E6. `DirtFloorZoneRegistry.Clear()`
- Routes every combiner through `NetworkObjectHelper.SafeDestroy` (which delegates to
  `ZNetScene.Destroy` if applicable, else `Object.Destroy`).
- Clears `s_allCombiners`, `s_diskToCombiner`, counters. Called from non-shutdown paths
  (world reloads / debug commands).

---

## F. File persistence (server-side)

### F1. `BiomeFilePersistence`
- File: `<world>.biomedata` in world save folder (or BepInEx fallback).
- `RecordBiomeReset(position, radius)` → prunes `_serverBiomeCache` entries whose
  `Position` is within `radius + entry.Radius`.
- `SaveBiomeData()` — if cache is empty, calls `File.Delete(path)`. Otherwise rewrites.
- `ClearCaches()` — just empties `_serverBiomeCache`. No file deletion.

### F2. `TerrainPersistence` & `ClutterPersistence`
- Similar shape: server-side caches with `ClearCaches()` for unload and
  `RemoveFromServerCache(stableHash)` for selective removal.

---

## G. Other clear sites (less direct)

- `TerrainUndoManager.cs:891` — undo path can call `DirtFloorCombinerCache.OnCombinerPermanentlyDeleted`
  when undoing a placement that resulted in an empty combiner.
- `DirtFloorController.cs:378` — `DirtFloorTerrainCompStorage.RemoveDisk(transform.position)`
  in the disk's own destruction path (legacy storage cleanup).
- `DirtFloorMeshCombiner.HeightOps.cs:191` — the in-place "combiner is empty" branch
  inside `PunchHole` is the single place where the anchor and combiner are torn down
  together as a direct consequence of an erase operation.
- `BiomePaintPatches.ZNetScene_OnDestroy_Prefix` in E4 already noted.

---

# Synthesis

**There is no single "remove" function.** Destruction is spread across **at least 14 different
methods**, with overlapping responsibilities and multiple entry points hitting the same code
through different routes:

- Three console commands (`terrain_clear_radius`, `terrain_clear_zone`, `terrain_clear_all`)
- One hammer tool (`delete_dirtfloor`)
- One precision hammer tool (`punch_terrain_hole`)
- One TerrainOp interception path (vanilla hoe + IH)
- Six RPC handlers (`RPC_HandleRemoveTerrain`, `RPC_HandleClearZone`, `RPC_HandleClutterClear`,
  `RPC_HandleBiomeClear`, `RPC_HandleClearPaintRequest`, plus the `TerrainPaintRPC` request RPCs)
- An undo-path call site that also destroys empty combiners

A typical "remove all terrain at point" actually touches **seven distinct subsystems**:
the combiner height grid (`_heights[]`), the combiner's `_terrainDisks` list,
the combiner's paint mask texture, the per-combiner `_spawnedGrass` instances,
the painted-clutter manager (`ClutterPaintTool` + `ClutterPersistence` ZDO bytes),
the vanilla `ClutterSystem.ResetGrass`, and finally — if the combiner becomes empty —
the file cache (`DirtFloorCombinerCache`), the anchor ZDO (8 byte-array keys + 4 flags),
and the GameObject itself. `ClutterManager.ClearInRadius` is invoked **three times** inside a
single `RemoveTerrainInRadiusInternal` call (once in `PunchHole`, once in `RemoveTerrainInRadiusInternal`
proper as `ClearInRadiusPermanent`, and a third time if the entry came through TerrainOp
paint-with-vegetation-clear).

The "delete then save then save again" pattern shows up everywhere: `terrain_clear_radius`
calls `DirtFloorCombinerCache.SaveCache()` after the inner clear has already saved,
and `OnCombinerPermanentlyDeleted` force-saves immediately (bypassing the batched timer)
to defend against server restart races. Persistence consistency is achieved through
redundancy, not centralization.

Networking is similarly fragmented: `RequestRemoveTerrain` broadcasts to `Everybody` and
deduplicates by op-id; `RPC_HandleClearZone` is a near-identical second RPC that exists
because `terrain_clear_radius` and the hammer-tool path use different code routes;
`punch_terrain_hole` and `delete_dirtfloor` are local-only (they don't broadcast — they
rely on every peer running the command independently or on lazy-save propagation).

**For FiresAdminTerrain**, the single centralized API should own all of:
(1) hole-punching the tile heights, (2) clearing per-tile paint state,
(3) wiping the anchor ZDO bytes when the tile is empty, (4) updating the
`DirtFloorCombinerCache`/tile-anchor registry equivalent, (5) the **one** ClutterManager
clear call, (6) the **one** RPC broadcast, and (7) a single optional file-save. Every
entry point — console command, hammer tool, RPC handler, TerrainOp interceptor, undo —
should funnel through that one function and pass nothing more than `(center, radius, Y bounds,
flags)`. The bug class here is obvious: when you have three save calls per operation
and three subsystems each clearing clutter, any one of them silently failing leaves
stale state that the next load surfaces as ghost terrain.
