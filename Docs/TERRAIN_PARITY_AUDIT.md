# Custom Terrain Feature-Parity Audit — RPG-maker vs FAT

**Audited:** 2026-05-25
**Scope:** `Modules/CustomTerrain/` in both mods. Compares
`FiresNPCs/FiresNPCs/Modules/CustomTerrain` (RPG-maker, the source of truth)
against `FiresAdminTerrain/FiresAdminTerrain/Modules/CustomTerrain` (FAT, the
in-flight port).

Legend:
- ✓ **PORTED** — works in FAT same as RPG-maker
- ⚠ **PARTIAL** — present in FAT but missing pieces or behavior diverges
- ✗ **MISSING** — not in FAT at all
- 🐛 **BROKEN** — present but actively broken (stub, no-op, etc.)
- 🔀 **REPLACED** — FAT has a different/better implementation
- ➕ **NEW** — FAT-only, no analogue in RPG-maker

---

## STRUCTURAL OBSERVATIONS (read first)

1. **Two parallel terrain stacks coexist in FAT today.** The full legacy
   `DirtFloor*` system (~46 files, ~47.5K lines) is byte-for-byte identical
   to RPG-maker (only `using` ordering and whitespace differs in shared
   files; `diff -w` finds at most 4–9 lines per file). On top of that, the
   new `Persistence/` directory adds ~10.5K lines of a tile-anchor system
   (TileAnchor, TerrainTile, TileBrush, TilePersistence façade, etc.).
2. **The toggle is a no-op for the legacy entry points.** `TilePersistence.IsEnabled`
   reads `Terrain.UseNewTilePersistence` (default OFF), but the legacy
   `DirtFloorTerrainManager.CreateDisk` / `TerrainOperationRPC` / hammer
   placement tool / smooth/level/raise/lower paths **never branch on it**.
   The toggle exists for future wiring (HANDOFF.md "Phase 8d-2 plan",
   tasks #67, #29). Flipping it ON today does nothing.
3. **The tile system is wired through completely different entry points.**
   The new path is only reached by `va_brush_*` / `va_tile_*` console
   commands and the IH HOE tools that fire them. The legacy path is
   reached by `DirtFloorPlacementTool` (vanilla hammer "TerrainDisk" piece)
   and `terrain_*` console commands. There is no compatibility shim — they
   write to entirely different ZDO key spaces (`floor_paint_v3` / Y=0
   combiner anchors vs `va_tile_v` / Y=−5000 tile anchors).
4. **The legacy DirtFloor stack still compiles, still auto-patches via
   Harmony, still self-initializes**. `DirtFloorPatches` postfixes
   `ZoneSystem.Start` to call `TerrainPaintRPC.Initialize`,
   `TerrainOperationRPC.Initialize`, `TerrainDebugCommands.Initialize`,
   `DirtFloorPlacementTool.Initialize`, `TerrainUndoManager.Initialize`,
   `TerrainCombinerAnchor.RegisterPrefab`, `TerrainCaveDetector.EnsureExists`.
   The plugin entry calls `TerrainPaintRPC.Initialize`,
   `BiomeFilePersistence.Initialize`, `BiomeOverlayPainter.RegisterCommands`
   explicitly (the rest go through `DirtFloorPatches`).

---

## CRITICAL GAPS — top 10 things that break a real player session today

> Ordered by severity to a live player who is NOT a developer. Tasks list
> already tracks many of these; severity ranking is mine.

1. **CLUTTER IS A STUB.** `Managers/VAClutterManager.EnsureRegistered()` is
   an empty method. Custom snow / biome-specific clutter from the
   `firesnpcs` bundle is never injected into `ClutterSystem.m_clutter`,
   and FAP's `VAClutterRegistry` source-link is COMMENTED OUT in the
   csproj. Snow trails won't render snow clutter; biome paints don't
   refresh clutter; `ClutterManager.PaintClutter` silently no-ops on
   custom clutter types. (`Managers/VAClutterManager.cs:14-21`, csproj
   lines 324-347, task #7 pending.)

2. **NEW TILE SYSTEM HAS NO BIOME-AWARE CLUTTER REFRESH.** When the
   player paints biome via `va_brush_biome`, the per-vertex biome flips
   and `Heightmap.FindBiome` / `WorldGenerator.GetBiome` start returning
   the new biome — but `ClutterSystem.ResetGrass` / `ClutterSystem`
   re-roll is never triggered. Old clutter persists looking out of place
   until manual `clutter` console. (`TileBrush.cs:200-300` —
   `ApplyBiomeToTile` writes only to `Tile.Biome[]`, no clutter call.
   Task #2 above.)

3. **BIOME PAINT ON CUSTOM TILES HAS NO YAML EXPOSURE.**
   `TileBrushCommands.Cmd_Biome` registers `va_brush_biome`, but
   `Docs/infinity_tools.yaml` exposes ZERO biome paint tools on the
   HOE — only flat/raise/lower/paint/clear/info. `TileHammerCompat.s_ourToolNames`
   lists "Biome Meadows", "Biome BlackForest" etc. as if YAML tools
   existed, but they don't. The user can only paint biome by typing
   `va_brush_biome 1` in console. RPG-maker exposed these via legacy
   IH tools that this YAML replaced.

4. **DELETE / WIPE COMMANDS HAVE NO YAML EXPOSURE.** `TileDeleteOps`
   registers `va_brush_delete`, `va_terrain_wipe_loaded`,
   `va_terrain_wipe_all`, `va_terrain_count`. None are in the YAML.
   `TileBrushDrag.IsOurBrushToolSelected` lists "Delete Tiles" as a
   tool name; no YAML entry creates it. The user must run console
   commands to clean up.

5. **MULTIPLAYER FOR THE NEW TILE SYSTEM IS UNVERIFIED.** Task #30 is
   still pending. `TileBrushRPC` is wired and should fan out, but no
   live two-client test has run. `TileAnchor.ApplyOpAndSave` skips
   when `ClaimOwnership` is async-pending (Phase 8d follow-up at
   `TileAnchor.cs:255-263`) — a non-host client editing an unloaded
   neighbor tile will get a "needs RPC dispatch" warning and the edit
   will silently drop.

6. **NO VANILLA HAMMER / WEC INTEGRATION FOR TILES.** The vanilla hoe's
   Level/Raise/Smooth/Paint tools and WEC's `terrain` console are
   blocked over loaded tiles by `TileTerrainOpBlocker` and the result
   is "routed" to `TileBrush.BrushFlat/Raise/Clear/Paint`. But the
   routing is best-effort — vanilla `m_smooth` collapses to
   `BrushClear` (a delete, not a smooth), and vanilla "paint reset"
   maps to a translucent white instead of clearing. Players using
   vanilla tools over tiles get surprising behavior. (`TileTerrainOpBlocker.cs:115-148`.)

7. **DISK→TILE HEIGHT APPLICATION IS NOT IMPLEMENTED.**
   `TilePersistence.RecordDiskOnOverlappingTiles` writes the disk
   record to each overlapping tile's `Disks` list but **does not**
   compute per-vertex height/paint changes from the disk's center +
   radius + baseHeight. HANDOFF.md explicitly flags this as the
   missing piece for Phase 8d-2 entry-point branching. So even if
   someone wired the legacy disk-place op to call the new path, the
   tiles wouldn't actually render any geometry from the disk record —
   only `va_brush_*` ops produce visible terrain on the new system.
   (`Persistence/TilePersistence.cs:35-49`, HANDOFF.md:128.)

8. **TWO SAVE PATHS, NO MIGRATION.** A world that placed terrain on
   the legacy path (Y=0 combiner anchors + `floor_paint_v3` ZDO keys)
   has those anchors fully persisting. A second session running with
   `UseNewTilePersistence=true` would NOT see legacy terrain via the
   tile system, and `va_brush_clear` / `va_terrain_wipe_all` only
   touch VATile ZDOs — legacy anchors and `BiomePaintPersistence`
   on-disk caches are unaffected. There is no migration tool.

9. **NEW TILES DON'T STITCH BIOME WITH VANILLA TERRAIN.** Task #73 +
   #84 still pending. Per-vertex biome paint on a custom tile creates
   a hard edge at the 64m tile boundary against vanilla — adjacent
   vanilla pixels keep their world-gen biome. `TileMeshRenderer`
   binds a SHARED Custom/Heightmap material so each tile cannot have
   its own per-vertex biome blend mask. (`TileMeshRenderer.cs:642-754`,
   "biome stitching across tile borders" comment in same file.)

10. **NO TILE-EXTENDED COLLIDER / WATER INTERACTION.** New tiles use a
    standalone MeshCollider on the `piece` layer. `WaterVolume`,
    `EWP` triggers that test against the heightmap, and `LiquidDamageHandler`
    code paths that sample `Heightmap.GetHeight` will all see the
    VANILLA height under the tile, not the tile's surface. Players
    standing on a sky-built tile over deep ocean would still be
    treated as "in deep water" by anything that doesn't go through
    `Physics.Raycast`. (No corresponding entry in vanilla heightmap
    override.)

---

## FULL PARITY INVENTORY

### 1. Brush ops

| Feature | RPG-maker | FAT (new tile path) | FAT (legacy path) | Notes |
|---|---|---|---|---|
| **Flat / Level** | ✓ | ✓ `BrushFlat` | ✓ unchanged | New: per-vertex set, circle or square, MP-broadcast via `TileBrushRPC.SendFlat`, undo wired |
| **Raise** | ✓ | ✓ `BrushRaise` | ✓ unchanged | New: linear or power-curve falloff; MP-broadcast |
| **Lower** | ✓ | ✓ `Cmd_Lower` (Raise with negated delta) | ✓ unchanged | New: same as raise with negative delta |
| **Paint vertex color (dirt/cult/paved)** | ✓ | ⚠ `BrushPaint` stores `Paint[]` but **mesh renderer ignores it** — vertex colors come from biome, not paint | ✓ unchanged | `TileMeshRenderer.cs:404-409`: "PAINT belongs in the _ClearedMaskTex texture per-tile, not in vertex colors". Paint is persisted but never rendered. The `Paint Dirt`/`Paint Cultivated`/`Paint Paved` HOE tools write data that doesn't show up visually. |
| **Clear / Reset** | ✓ | ✓ `BrushClear` (clears Height + Paint + Biome modified flags) | ✓ unchanged | |
| **Delete (destroy anchor)** | ✗ (RPG-maker: deletes per-disk) | ✓ `TileDeleteOps.Cmd_BrushDelete` + `va_terrain_wipe_*` | n/a | ➕ NEW — destroys whole TileAnchor + ZDO + mesh |
| **Smooth** | ✓ vanilla TerrainOp smoothing | ✗ — `TileTerrainOpBlocker.RouteToOurBrush` collapses smooth to `BrushClear` | ✓ unchanged | 🐛 BROKEN: vanilla smooth over a tile triggers a delete-style reset, not a smooth |
| **Biome paint (Meadows/BF/Plains/Swamp/Mountain/Ashlands/DeepNorth/Mistlands)** | ✓ | ⚠ `BrushBiome` writes per-vertex; cursor-target dispatcher routes custom vs vanilla. AshLands also depresses heights for lava well. **YAML tools missing**. | ✓ unchanged (`BiomeManager`, `BiomePaintPersistence`) | See critical gap #3 |
| **Lava overlay (vanilla-side biome)** | ✓ via `BiomeOverlayPainter.PaintOverlay(Lava)` | ✓ wired — `Cmd_Biome` calls both custom tile depression AND `BiomeOverlayPainter.PaintOverlay(Lava)` | ✓ unchanged | Task #81 pending: double-paint behavior over BOTH custom and vanilla simultaneously is still flagged for cleanup |
| **Square-footprint variant** | ✓ for some ops | ⚠ flag plumbed through `BrushFlat`/`BrushPaint` signatures but never set true from any command | ✓ unchanged | |
| **Brush respects IH ruler radius (`circle=R-R2`)** | ✓ | ✓ `TileBrushCommands.ResolveRadius` parses outer R from `circle=` | ✓ via `InfinityHammerCompat` | |
| **Brush respects IH `from=X,Z,Y`** | ✓ | ✓ `TryParseFromArg` | ✓ | |
| **Brush respects IH `height=N`** | ✓ for Raise/Lower deltas | ✓ `TryParseNamedFloat("height")` | ✓ | |
| **Click-and-drag dispatch** | ✓ | ✓ `TileBrushDrag` (10Hz, freshness gate, tool-selected gate, locks first-click Y for Flat) | ✓ unchanged | |
| **Undo / Redo** | ✓ `TerrainUndoManager` (legacy combiner / ZonePaintLayer) | ✓ `TileUndoLog` separate stack | ⚠ legacy still active for legacy ops | 🔀 REPLACED — two independent undo stacks; WEC integration in both via reflection. Tile undo persists per-vertex deltas, calls `ApplyOpAndSave`. |

### 2. Paint system

| Feature | RPG-maker | FAT (new) | Notes |
|---|---|---|---|
| Vertex color paint (RGBA→biome blend) | ✓ via `VertexPaintSystem` + `DirtFloorMeshCombiner.Paint.cs` | ⚠ stored only — `TileMeshRenderer.RebuildMesh` writes biome colors to vertex `_colors`, ignores `Paint[]` | Codec persists Paint[] for the eventual per-tile mask-texture port |
| `_ClearedMaskTex` per-tile paint mask | ✓ `DirtFloorPaintMaskStreaming` + per-zone composited mask | ✗ NOT IMPLEMENTED — `TileMeshRenderer.TryAcquireSharedHeightmapMaterial` binds a SHARED 1×1 "nothing" texture to override the cloned material's mask. Per-tile paint mask is explicitly flagged as a future port. | 🐛 The Paint Dirt/Cultivated/Paved tools have no visual effect on new tiles |
| Paint mask persistence (file backup) | ✓ via `TerrainPaintLayer` and `ClutterPersistence` (legacy) | ✗ N/A — no mask texture to persist | |
| Paint mask network sync | ✓ `TerrainPaintRPC` registered | ✓ `TerrainPaintRPC.Initialize()` called from `FiresAdminTerrain.FinishInit` for the legacy stack | The new stack uses `TileBrushRPC` only (no paint mask sync because no paint mask) |
| Per-tile vs per-zone mask resolution | per-zone (RPG-maker) | n/a | |
| `ZonePaintLayer` + `ZonePaintManager` | ✓ | ✗ Files compiled (`Paint/ZonePaintLayer.cs`, `Paint/ZonePaintManager.cs`) but only consumed by legacy `DirtFloorMeshCombiner.Paint.cs`. Not wired to new tile system. | |

### 3. Biome system

| Feature | RPG-maker | FAT (new) | Notes |
|---|---|---|---|
| Per-vertex biome on custom tiles | ⚠ (per-tile in RPG-maker; per-vertex was new in FAT codec v3) | ✓ codec v3 — `Tile.Biome[]` + `Tile.BiomeModified[]`, sparse-encoded, v2→v3 upgrade promotes tile-wide override to per-vertex | 🔀 REPLACED & improved |
| Vanilla heightmap biome overlay | ✓ `BiomePaintPersistence` | ✓ unchanged, lives alongside new system; `Cmd_Biome` routes vanilla cursor-targets to `BiomeOverlayPainter.PaintOverlay` | |
| `Heightmap.FindBiome` patch | ✓ in legacy `BiomeManager` | ✓ `TileBiomeOverride` patches `Heightmap.FindBiome` + `WorldGenerator.GetBiome` for tile lookup | dual patches active simultaneously — legacy patch covers legacy combiner positions, new patch covers tile positions |
| Biome-aware clutter selection | ✓ (via clutter refresh on biome change) | 🐛 — `BrushBiome` does NOT call `ClutterSystem.ResetGrass` or any clutter refresh. See critical gap #2 | |
| Biome-aware ambient (env boxes) | ✓ EnvironmentBoxes module | ⚠ EnvironmentBoxes module is present + compiled (env box controller / spawner / patches / scaler) but no FAT init wires it. Auto-init via `[HarmonyPatch]` may or may not fire — needs verification. | |
| Biome edge stitching across tile borders | ✗ (legacy used per-zone composite mask) | ⚠ task #73 pending — current shared material has no per-tile blend mask so adjacent tiles can't blend at the seam | |
| DeepNorth biome on vanilla terrain | ✓ task #78 done | ✓ same — `BiomeOverlayPainter` handles it | |
| Mistlands / Ashlands handling | ✓ in `BiomeManager` | ✓ `Cmd_Biome` enums maps both; AshLands has special lava-depression path | |

### 4. Clutter / vegetation

| Feature | RPG-maker | FAT | Notes |
|---|---|---|---|
| `ClutterManager` | ✓ | 🐛 file compiled but custom clutter from bundle never injected (`VAClutterManager.EnsureRegistered` is a stub). See critical gap #1. |
| Painted clutter (snow tufts etc.) | ✓ `ClutterPaintTool` + `ClutterPersistence` | ⚠ files compiled, RPG-maker static wireup works, but bundle clutter not registered → no custom clutter types available; vanilla clutter types still paintable | |
| `ClutterSystem.ResetGrass` after biome change | ✓ | ✗ neither new nor legacy biome paint calls this | |
| Clutter persistence | ✓ `ClutterPersistence` (file-backed, ZDO-keyed) | ⚠ legacy code present and auto-initialized, but with no custom clutter registered the persistence only handles vanilla types |
| Snow clutter | ✓ via `SnowClutterConfig` | ⚠ file compiled, only used by `TerritorySnowManager` legacy path; new tile system never spawns snow clutter |
| Clutter refresh on biome change | ✓ via `BiomeManager.RefreshAllBiomeColors` | ✗ same as above |
| `EnvironmentBoxClutterSpawner` | ✓ | ⚠ present but no FAT init wires env boxes generally |

### 5. Environment / atmosphere

| Feature | RPG-maker | FAT | Notes |
|---|---|---|---|
| `EnvironmentBoxes` module | ✓ wired in entry point | ⚠ compiled (4 files in csproj), no explicit init from FAT entry; relies on `[HarmonyPatch]` auto-registration (likely active but unverified) |
| Snow system (`TerritorySnowManager`) | ✓ | ⚠ compiled, no FAT init — legacy auto-patches may run; depends on FA bundle assets |
| Cave environment (`TerrainCaveEnvironment`, `TerrainCaveDetector`) | ✓ | ✓ `TerrainCaveDetector.EnsureExists()` called from legacy `DirtFloorPatches.ZoneSystem.Start` postfix → still fires |
| Ambient audio per-area | ✓ via env box | ⚠ same caveat as env boxes |

### 6. Disk system

| Feature | RPG-maker | FAT (new) | Notes |
|---|---|---|---|
| `TerrainDisk` per-disk record | ✓ | ✓ `Persistence/TerrainTileCodec.DiskRecord` struct holds metadata | But: the new system records disks per-overlapping-tile yet **doesn't apply them to heights** (see critical gap #7) |
| `DirtFloorTerrainManager.CreateDisk` | ✓ | ✓ unchanged in legacy stack; never branched to call `TilePersistence.RecordDiskOnOverlappingTiles` |
| Disk → mesh combiner attach | ✓ | ⚠ legacy path unchanged; new path stores disks as sidecar metadata only |
| Disk removal | ✓ | ⚠ legacy: `RemoveTerrainInRadius`; new: `TilePersistence.RemoveDiskFromOverlappingTiles` exists but unwired |

### 7. Persistence

| Feature | RPG-maker | FAT | Notes |
|---|---|---|---|
| Anchor ZDO keys | `TerrainCombinerAnchor` (Y=0 anchor, `floor_paint_v3`) | ➕ NEW system uses `va_tile_v` (codec version), `va_tile_data` (gzip payload), `va_tile_x`, `va_tile_z` | Two non-overlapping key spaces; legacy keys still active |
| File-cache backup | ✓ `TerrainPersistence.SaveTerrainData/LoadTerrainData` + `BiomeFilePersistence` + `ClutterPersistence` | ⚠ legacy paths still active; NEW tile system has NO file-cache backup — ZDO-only persistence |
| ZDO data revision tracking | ✓ via vanilla CheckLoad pattern (legacy combiner anchors) | ✓ `TileAnchor.CheckLoad` mirrors vanilla `TerrainComp.CheckLoad` |
| Partial load handling | ✓ | ✓ `TileAnchor.TryLoadFromZdo` retries on revision bump if payload empty |
| Schema versioning | ⚠ legacy ad-hoc | ✓ `TerrainTileCodec.SCHEMA_VERSION = 3` with v1→v2→v3 backward-compat read paths |
| Inactive-template trap fix | ✓ tasks #39, #40 done in FAT for VATile | ✓ — clone parented under inactive root, template stays activeSelf=true |
| Fake-null scrub on session change | ✓ task #36 done | ✓ `TileAnchorPrefab.Ensure` re-inserts into new ZNetScene; scrubs IH SpriteHelper cache via reflection |
| Logout/login persistence | ✓ task #49 | ✓ verified working |
| Zone-poke wipe_all | ✓ task #53 | ✓ `TileDeleteOps.WipeAllCoroutine` 3-phase, reflectively pokes generated zones |

### 8. Multiplayer

| Feature | RPG-maker | FAT | Notes |
|---|---|---|---|
| `TerrainPaintRPC` registered | ✓ | ✓ called from `FiresAdminTerrain.FinishInit:185` (legacy path) |
| `TerrainOperationRPC` registered | ✓ | ✓ called from `DirtFloorPatches.ZoneSystem.Start` postfix (legacy path) |
| `TileBrushRPC` (new) | n/a | ✓ 5 RPCs registered on `ZNet.Awake`: Flat, Raise, Paint, Clear, Biome |
| Server-authority gating | ✓ via `ConfigEnableServerAuthority` etc. | ⚠ new system has NO server-auth gating — anyone can spawn tiles via `va_brush_*`; relies on `isCheat:true` console gate |
| Ownership claim pattern | ✓ legacy: `ClaimOwnership` per anchor | ✓ new: `TileAnchor.ApplyOpAndSave` claims, but skips with warning if claim is async-pending (no RPC dispatch yet — Phase 8d follow-up) |
| Broadcast vs request | legacy: request-via-RPC server applies | new: `Everybody`-broadcast, each peer applies locally, owner's ZDO replicates |
| Live two-client test | ? | ✗ task #30 pending — never tested |

### 9. Console commands

| Command | RPG-maker | FAT | Notes |
|---|---|---|---|
| `dump_terrain_combiners` | ✓ | ✓ legacy via `TerrainDebugCommands` |
| `dump_terrain_materials` | ✓ | ✓ legacy |
| `dump_terrain_paint` | ✓ | ✓ legacy |
| `dump_terrain_compare` | ✓ | ✓ legacy |
| `terrain_cleanup` | ✓ | ✓ legacy |
| `terrain_regenerate` | ✓ | ✓ legacy |
| `terrain_clear_radius` | ✓ | ✓ legacy |
| `terrain_clear_zone` | ✓ | ✓ legacy |
| `terrain_clear_all` | ✓ | ✓ legacy |
| `terrain_test_paint` | ✓ | ✓ legacy |
| `terrain_isolated_mode` | ✓ | ✓ legacy |
| `terrain_undo` / `terrain_redo` / `terrain_undo_history` | ✓ | ✓ legacy |
| `place_cave_floor` / `place_cave_ceiling` / `place_bridge_terrain` | ✓ | ✓ legacy |
| `punch_terrain_hole` / `terrain_underside` | ✓ | ✓ legacy |
| `terrain_cave_info` / `terrain_check` / `terrain_uv_diagnose` | ✓ | ✓ legacy |
| `terrain_paint_visualize` / `terrain_fix_bounds` / `terrain_refresh_material` / `terrain_paint_debug` / `terrain_paint_diagnose` | ✓ | ✓ legacy |
| `paint_lava`/`paint_snow`/`paint_biome` (BiomeOverlayPainter) | ✓ | ✓ FAT calls `BiomeOverlayPainter.RegisterCommands` explicitly |
| `snow_*` commands | ✓ via `SnowCommands.RegisterCommands` | 🐛 NEVER CALLED — `SnowCommands.RegisterCommands` exists but no FAT call site (legacy doesn't call it either; only `DirtFloorPatches` runs and doesn't reach SnowCommands). |
| `va_tile_render` / `_slope` / `_clear_test` | n/a | ➕ NEW — `TileDebugCommands` auto-registers on `Terminal.InitTerminal` |
| `va_tile_persist` / `_slope` / `_load` / `_destroy` / `_list` / `_prefab_ensure` | n/a | ➕ NEW |
| `va_brush_flat` / `_raise` / `_lower` / `_paint` / `_clear` / `_biome` / `_where` | n/a | ➕ NEW via `TileBrushCommands` |
| `va_tile_undo` / `_redo` | n/a | ➕ NEW via `TileBrushCommands` |
| `va_brush_delete` / `va_terrain_wipe_loaded` / `_wipe_all` / `va_terrain_count` | n/a | ➕ NEW via `TileDeleteOps` |

### 10. Hammer / IH tool surface

| Tool / YAML entry | RPG-maker exposure | FAT YAML exposure | Notes |
|---|---|---|---|
| Floor (10 / 25 / 50 m) | ✓ via legacy tools | ✓ HOE `Floor (10m)`, `(25m)`, `(50m)` → `va_brush_flat` |
| Raise Terrain | ✓ | ✓ HOE `Raise Terrain` → `va_brush_raise` |
| Raise (Sharp) | ✓ | ✓ HOE `Raise (Sharp)` → `va_brush_raise R height=H 2` |
| Lower Terrain | ✓ | ✓ HOE `Lower Terrain` → `va_brush_lower` |
| Paint Dirt / Cultivated / Paved | ✓ | ⚠ HOE entries exist → `va_brush_paint`, but command's effect on visuals is broken — `TileMeshRenderer` doesn't apply Paint[] to mesh |
| Clear Brushed Area | ✓ | ✓ HOE `Clear Brushed Area` → `va_brush_clear` |
| Delete Tile | ✗ legacy didn't have this concept | ✗ command exists but no YAML entry. See critical gap #4. |
| Biome paint (Meadows / BlackForest / Plains / Swamp / Mountain / Mistlands / Ashlands / DeepNorth / Clear) | ✓ in legacy IH YAML | ✗ NO YAML — see critical gap #3 |
| Tile Info / List Tile Anchors / Force VATile Register | n/a | ➕ NEW HOE info tools |
| Vanilla WEC terrain ops (Level / Raise / Slope / Pave / Dirt / Grass / Cultivate / Reset) | ✓ | ✓ HOE under "VANILLA TERRAIN OPS (WEC)" section unchanged from canonical IH wiring |
| Tools respect IH ruler height & radius | ✓ | ✓ — `<area>` + `height=<h>` tokens parsed |
| Hammer admin / pipette / ship / FX / debug tools | ✓ (FiresRPGmaker ships canonical IH YAML) | ✓ FAT YAML carries them verbatim |

### 11. Undo / redo

| Feature | RPG-maker | FAT (new path) | FAT (legacy) |
|---|---|---|---|
| Brush flat undoable | ✓ `TerrainUndoManager` | ✓ `TileUndoLog.BeginOperation/EndOperation` | ✓ |
| Brush raise/lower undoable | ✓ | ✓ | ✓ |
| Brush paint undoable | ✓ | ✓ | ✓ |
| Brush biome undoable | ✓ | ✓ — task #77 done | ✓ |
| Brush clear undoable | ✓ | ✓ | ✓ |
| Delete tile undoable | n/a | ✗ — `TileDeleteOps.TryDestroyAnchor` does not snapshot before delete | |
| WEC bind | ✓ | ✓ — `TileUndoLog.TryRegisterWithWEC` reflectively builds an `IUndoAction` proxy | ✓ separate proxy for legacy |
| Stack depth cap | 100 | 100 (`TileUndoLog.MAX_HISTORY`) | 100 |

### 12. Material / shader

| Aspect | RPG-maker | FAT (new) | Notes |
|---|---|---|---|
| Custom/Heightmap material acquisition | ✓ via `TerrainMaterialCache.CreateMaterial` | ✓ same — `TileMeshRenderer.TryAcquireSharedHeightmapMaterial` calls into `TerrainMaterialCache.CreateMaterial` |
| Paint mask binding (`_ClearedMaskTex`) | ✓ per-tile painted mask via `DirtFloorPaintMaskStreaming` | ⚠ binds a SHARED 1×1 "nothing" texture (`Heightmap.m_paintMaskNothing`) — no per-tile mask |
| Vertex color encoding | RGBA per vertex = biome blend mask via `Heightmap.GetBiomeColor` | ✓ same encoding; per-vertex biome from `Tile.Biome[idx]` or Meadows default |
| Per-tile vs shared material | per-zone composite in legacy | shared single material for all tiles in FAT new — blocks per-tile biome stitching (critical gap #9, task #73) |
| Fallback shader chain | piece_dirtfloor_vad → Standard → Sprites/Default | ✓ same — `TileMeshRenderer.BuildFallbackMaterial` |
| Material upgrade pass when shader lands | n/a | ✓ — `TryAcquireSharedHeightmapMaterial` upgrades every existing `TileMeshRenderer` in one pass on first success |
| Underside material capture from vanilla rocks | ✓ `DirtFloorMeshCombiner.Underside.cs` | ✓ `TileMeshUnderside.TryCaptureUndersideMaterial` — same prefab fallback list, same 0.4× color darken |

### 13. LOD systems

| Feature | RPG-maker | FAT (new) | Notes |
|---|---|---|---|
| `DirtFloorLODManager` | ✓ frame-budgeted, distance-binned | ✓ unchanged in legacy stack |
| `TileMeshRenderer.LOD` | n/a | ➕ NEW — 600m render / 92m collider LOD per tile, 1 cook/frame global budget, 12m / 6m hysteresis |
| `DistantTerrainController` | ? not searched | not in new system | new tiles have explicit `m_distant=true` on ZNetView so ZoneSystem streams them at distance regardless of player Y |
| Underside LOD | ✓ legacy | ✓ — `TileMeshUnderside` renders within 120m / collider within 40m + 10m Y headroom |
| Cook-thrash safety on brush stroke | ✓ legacy | ✓ — `TileMeshRenderer` no-op-skips ApplyOpAndSave callbacks that wrote 0 verts (task #61) |
| Frame-budgeted prewarm | ✓ legacy | ✓ — `TileProactiveSpawner` 4-per-frame queue bypasses ZNetScene's CreateObjectsSorted to drop pop-in from minutes to ~0.5s on megabases |
| `IsActiveAreaLoaded` gate | ✓ documented as vanilla patch in memory, applied at server | not directly relevant to new system | |

### 14. Lifecycle

| Stage | RPG-maker | FAT (new) | Notes |
|---|---|---|---|
| Anchor spawn from saved ZDO | via legacy combiner | ✓ `TileAnchor.Awake` → `TryLoadFromZdo`; vanilla ZoneSystem.CreateObject (or proactive spawner) instantiates |
| Anchor spawn on first brush | via `DirtFloorTerrainManager.CreateDisk` | ✓ `TileAnchor.Spawn(tileXZ)` instantiates VATile prefab at Y=-5000 |
| ZDO Type prioritization | n/a | ✓ — `TileAnchorPrefab.CloneAndPrepare` sets `nview.m_type = Prioritized`; `TileAnchor.Awake` bumps existing-but-Default-type saved ZDOs to Prioritized on first load |
| Destroy | legacy: `RemoveTerrainInRadius` | new: `TileDeleteOps.TryDestroyAnchor` via `ZNetScene.Destroy` (full cascade) |
| Re-spawn on zone re-stream | ✓ vanilla `CheckLoad` path | ✓ tile reads its decoded payload, recreates mesh; verified in HANDOFF.md tests |
| Save/load timing | ✓ | ✓ Codec is pure C# (no Unity main-thread deps) — eligible for `BackgroundWorker` per the redesign plan; not yet moved off main thread |
| Save world → exit → reload | ✓ legacy | ✓ task #49 done for new |
| World save flush | piggybacks on vanilla ZDO save | piggybacks on vanilla ZDO save (no custom flush) |
| Plugin hot-unload | legacy: `harmony?.UnpatchSelf` in `OnDestroy` | same |
| Session-change re-register | ✓ | ✓ `TileAnchorPrefab.Ensure` detects same-AppDomain logout/login and re-inserts cached VATile into new ZNetScene |

### 15. Bug list / TODOs / dead code

| Item | Where | Severity |
|---|---|---|
| `VAClutterManager.EnsureRegistered` is empty stub | `Managers/VAClutterManager.cs:14-21` | high — see critical gap #1 |
| FAP source-link block commented out | `FiresAdminTerrain.csproj:324-347` | high — gates clutter port (task #7) |
| `TilePersistence.RecordDiskOnOverlappingTiles` records but doesn't apply heights | `Persistence/TilePersistence.cs:35-49` | medium — task #8 in-progress, HANDOFF.md plan exists |
| `TileAnchor.ApplyOpAndSave` skips when ClaimOwnership async-pending | `Persistence/TileAnchor.cs:255-263` | medium — comment "needs RPC dispatch (Phase 8d)" |
| `TileMeshRenderer.RebuildMesh` ignores `Tile.Paint[]` | `Persistence/TileMeshRenderer.cs:404-409` | medium — Paint Dirt/Cult/Paved HOE tools have no visual effect |
| `SnowCommands.RegisterCommands` never called | `Snow/SnowCommands.cs:17` | low — snow_* console commands missing |
| No YAML for biome paint tools | `Docs/infinity_tools.yaml` | medium — critical gap #3 |
| No YAML for delete / wipe tools | `Docs/infinity_tools.yaml` | medium — critical gap #4 |
| Phase 8e legacy delete pending | task #67 | low — dead code grows fast but stable |
| Biome stitching across tile borders | task #73 | medium — visible hard edge at tile seams |
| Lava double-paint custom + vanilla | task #81 | low — works, just over-paints |
| Anchor-per-zone (never destroy) plan | task #82 | low — design goal for cleaner streaming |
| Vanilla paint ops clear our biome overlay | task #83 | medium — bug where WEC vanilla paint over a biome-overridden vanilla pixel resets to world-gen biome |
| Ashlands edge stitching on vanilla | task #84 | low |
| Multiplayer verify | task #30 | high — see critical gap #5 |
| TerrainOpBlocker: vanilla smooth maps to Clear (delete) | `Persistence/TileTerrainOpBlocker.cs:119-124` | medium — surprising behavior |
| `Integration/` folder empty in RPG-maker too (just a dir) | both | none — no-op |
| `CAVE_AND_WATER_FIX_PLAN.md`, `TERRAIN_IMPROVEMENTS_PLAN.md` are planning docs | both | low — informational |

---

## Summary table — quick scoring by category

| Category | RPG-maker score | FAT new path score | FAT overall posture |
|---|---|---|---|
| Brush ops (height) | ✓ | ✓ | green |
| Brush paint (visual) | ✓ | 🐛 (data persisted, mesh doesn't render) | red |
| Brush biome (custom) | ✓ | ⚠ (no clutter refresh, no YAML) | yellow |
| Brush biome (vanilla overlay) | ✓ | ✓ | green |
| Undo / redo | ✓ | ✓ | green |
| MP sync | ✓ (legacy) | ⚠ (RPCs wired, untested) | yellow |
| Persistence (ZDO) | ✓ | ✓ | green |
| Persistence (file backup) | ✓ legacy | ✗ new | yellow (legacy still works) |
| Clutter registration | ✓ | ✗ stub | red |
| Clutter refresh on biome | ✓ legacy | ✗ | yellow |
| Environment boxes | ✓ wired | ⚠ compiled, init unverified | yellow |
| Cave detection | ✓ | ✓ (via legacy auto-init) | green |
| Snow system | ✓ | 🐛 commands not registered | yellow |
| IH ruler-snap | ✓ | ✓ | green |
| IH tool YAML | ✓ comprehensive | ⚠ missing biome + delete tools | yellow |
| Vanilla op interception | ✓ | ⚠ smooth/reset paths lossy | yellow |
| Disk system | ✓ legacy | ⚠ tile system records but doesn't apply disks | yellow |
| LOD (render + cook) | ✓ legacy | ✓ tile-LOD ported and tuned | green |
| Material acquisition | ✓ | ✓ | green |
| Per-tile mask texture | ✓ legacy | ✗ new | yellow |
| Cross-tile biome stitching | n/a | ✗ | yellow |

**Verdict:** The new tile system is functionally sound for height ops +
biome paint (custom + vanilla side) + IH integration + undo + LOD +
proactive spawn. It is NOT yet a feature-complete replacement for the
legacy DirtFloor stack: paint mask rendering, clutter integration,
disk-record application, and migration are all missing or broken. The
legacy stack is still compiled and still active for any code path that
hasn't been routed through the toggle (which today is "all of them").
The mod ships as "two systems running in parallel, new system for
brushes, old system for everything else" — fine for admin use, not yet
ready as a player-facing terrain replacement.
