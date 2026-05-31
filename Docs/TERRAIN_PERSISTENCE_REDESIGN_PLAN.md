# Terrain Persistence Redesign — Tile-Anchor Architecture

**Status:** Plan draft for review (2026-05-24)
**Replaces:** The 3 competing storage paths inherited from the FiresRPGmaker port
**Companion:** [TERRAIN_WATER_PERSISTENCE_BUGFIX_TRACKER.md](TERRAIN_WATER_PERSISTENCE_BUGFIX_TRACKER.md)

---

## 1. Goals & non-goals

### Goals
- **Single source of truth for terrain mesh data.** No more 3 storage paths racing on load.
- **Reliable restore on world load.** No more "re-combine from heights" fallback firing on every login because the cache went missing.
- **Multiplayer-correct.** Non-owner clients see the same terrain as the owner the first time they enter a zone, not after a walk-around.
- **Vanilla-pattern replication.** Use ZDO data-revision detection (`CheckLoad`) like vanilla `TerrainComp`, not prefab `Start()` timing.
- **Tile-aligned to ZoneSystem.** Anchor positions snap to the same 64m zone grid the engine streams, so anchor streaming and zone streaming move together.
- **Anchors hidden at Y=-50.** Submerged below any playable terrain, never visible, no chance of being picked, walked on, or collided with.

### Non-goals
- Re-architecting the **runtime mesh combiner** (`DirtFloorMeshCombiner.MeshGen`, `HeightOps`, `Underside`, `Utilities`, `Static`) — those work great and stay as-is.
- Re-architecting the **disk concept** (`TerrainDisk`, `DirtFloorController`) — disks remain the editing primitive.
- Per-tile rendering. The combiner still owns one mesh per group; tiles are a **persistence** unit, not a rendering unit.
- Clutter persistence (delegated to FAP — separate task).

---

## 2. Current state — what's broken (from diagnosis)

Three independent storage backends compete:

| Backend | Owner | ZDO key / file | Status |
|---|---|---|---|
| `TerrainCombinerAnchor` | piece_dirtfloor_vad ZNetView at **Y=0** | `va_terrain_data`, `va_height_data`, `va_paint_data`, `va_bounds_data`, `va_real_position`, `va_terrain_version=4`, etc. | Active but unreliable |
| `DirtFloorTerrainCompStorage` | vanilla TerrainComp ZNetView per 64m zone | `va_custom_disks` | Restore path **unimplemented** (`CreateDiskFromData` TODO at [DirtFloorTerrainCompStorage.cs:386](../Modules/CustomTerrain/Core/DirtFloorTerrainCompStorage.cs)) |
| `TerrainPersistence` | server-only world file | `{worldFolder}/{worldName}.terraindata` (gzip, magic `TERR`) | Reflection-based combiner read with `try/catch{}` — silently drops data |
| `DirtFloorCombinerCache` | local .bin file | Per-anchor MeshVertices/Triangles/UVs/Normals/Heights/PaintMask blob | The fallback the runtime depends on; regularly missing |

**Failure modes observed in the code itself:**
- `TerrainCombinerAnchor.cs:642`: `"All retries exhausted — ZDO data never synced for this terrain anchor"`
- `TerrainCombinerAnchor.cs:208,253`: routinely-firing `"destroying duplicate anchor"` / `"destroying stale anchor"`
- `TerrainCombinerAnchor.cs:519-523`: `"Link mismatch detected, repairing"`
- `DirtFloorMeshCombiner.DiskManagement.cs:643`: `"Rebuilding mesh from heights (no vertex cache)"` — the visible "re-combines every load" symptom
- Anchor at Y=0 with real position chased via `va_real_position` ZDO key — not coupled to vanilla zone streaming, so the ZNetScene may not load the anchor when the player walks into the relevant zone

**Vanilla reference (the pattern we will mirror):**
- `TerrainComp` ZNetView per 32m × 32m heightmap, ZDO byte-array `s_TCData` stores all height + paint deltas (gzipped)
- `CheckLoad()` runs in `Update()` — reads `m_nview.GetZDO().DataRevision`, calls `Load()` when it changes
- `Save()` runs after any local op, gated on `IsOwner()`
- ZDO replication does the multiplayer sync; no client-to-client RPC for terrain state
- On a new client entering an existing zone, ZoneSystem instantiates the TerrainComp prefab from the ZDO; its `Awake → Initialize → CheckLoad` reconstructs the heightmap from the same byte-array

We will use this exact pattern, but for our custom DirtFloor meshes, at a 64m tile granularity.

---

## 3. Proposed architecture

### 3.1 The tile

**Definition.** A 64m × 64m world-aligned rectangle, identical to `ZoneSystem` zones.

- **Tile index:** `Vector2i tileXZ = new(Mathf.FloorToInt(worldX / 64f), Mathf.FloorToInt(worldZ / 64f))`
- **Tile origin:** `new Vector3(tileXZ.x * 64f, anchorY, tileXZ.y * 64f)` (south-west corner)
- **Tile center:** origin + `(32, 0, 32)` — this is where the anchor sits in XZ
- **Vertex grid:** 65×65 per tile (`(64+1)^2 = 4225` vertices, 1m spacing), matching the vanilla heightmap convention of vertices-not-cells. Border vertices coincide with neighbor tiles' border vertices by construction.

### 3.2 The anchor

**`TileAnchor`** (replaces `TerrainCombinerAnchor`). A stripped ZNetView at:

```
new Vector3(tileXZ.x * 64f + 32f, -50f, tileXZ.y * 64f + 32f)
```

- **Prefab:** the same `piece_dirtfloor_vad` reused (saves a prefab registration; ZNetView is what matters, not the visual). On Awake, `StripToInvisible` zeros out any renderer/collider so it cannot be picked or walked on. Y=-50 means even if the strip somehow misses, no player sees it.
- **Identity:** `tileXZ` is encoded in the ZDO (`va_tile_xz`). Lookup is by tile index, not by world position. **Deterministic** — same tile = same identity, no proximity matching, no "destroy duplicate".
- **Ownership:** standard ZNetView pattern. First peer to write claims via `ClaimOwnership()`. Subsequent peers detect `DataRevision` changes via `CheckLoad`.
- **Persistence:** ZNetView `m_persistent = true`, `m_distant = true` (so it survives world unload + streams from arbitrary distance). The `m_distant` flag matters: anchors at Y=-50 must reach players from neighboring zones.

### 3.3 ZDO payload (the only source of truth)

| Key | Type | Purpose |
|---|---|---|
| `va_tile_v` | int | Schema version (start at 1) |
| `va_tile_xz` | Vector2i (via two ZDO ints) | Tile index — sanity check on load |
| `va_heights` | byte[] | Gzipped `float[4225]` final height per vertex. Sparse encoding: write only modified vertices + run-length skips. Estimated 3-8 KB per typical edited tile. |
| `va_paint_mask` | byte[] | Gzipped `Color32[4225]` RGBA paint per vertex. Same sparse encoding. ~3-6 KB. |
| `va_disk_records` | byte[] | Gzipped serialized list of disks **whose footprint overlaps this tile**: `{stableId, centerX, centerY, centerZ, radius, baseHeight, paintColor, materialId, flags}`. For re-edit support and disk-level operations; the height grid is authoritative for rendering. ~50-500 bytes per disk. |

**Hard ceiling:** each ZDO byte array ≤ 32 KB (Valheim's practical limit before sync slows). Our typical tile fits well under. Worst case (heavily edited 64×64 m with 100+ disks): ~10-15 KB total — fine.

**Things deliberately NOT in the ZDO:**
- Pre-combined mesh vertices/triangles/UVs/normals. The combiner reconstructs these from the height grid + paint grid at runtime (it already does this — see `DirtFloorMeshCombiner.MeshGen`). Storing them is what the old anchor did and it's both redundant and brittle.
- Clutter paint records. FAP owns those in its own `fap_clutter_paints` byte-array on vanilla TerrainComp ZDOs — independent of our anchors.
- Bounds. Computed from the height grid.

### 3.4 Combiner ↔ tile relationship

- **One `DirtFloorMeshCombiner` GameObject per contiguous group of tiles** that should weld into a single mesh.
  - Combiner group identity: a deterministic id derived from the cluster of tile indices it spans (e.g. `min(tileXZ).GetHashCode() ^ count`).
  - When a new tile-anchor loads adjacent to an existing combiner's tiles, the combiner annexes the tile (rebuilds its mesh union from the loaded slices).
  - When a tile-anchor unloads (zone exit), the combiner releases that tile's slice. If all tiles unload, the combiner GameObject is destroyed.
- **One `TerrainTile` MonoBehaviour per loaded tile** (parented under the combiner). Holds:
  - Reference to the `TileAnchor`'s ZNetView
  - The decoded height grid float[4225]
  - The decoded paint grid Color32[4225]
  - The disk record list
  - A LastSeenDataRevision cached for `CheckLoad`
- **Combiner mesh** = welded union of all loaded `TerrainTile.HeightGrid` slices. Border vertices dedup deterministically by world position (see §4).

### 3.5 The restore flow on world load — one path only

1. Player enters a zone → ZoneSystem streams in all ZDOs in the zone, including any `TileAnchor` ZDOs.
2. ZNetScene instantiates the `piece_dirtfloor_vad` prefab for each anchor ZDO. `TileAnchor.Awake` runs:
   - Reads `va_tile_xz` from the ZDO (the tile this anchor owns)
   - Registers self with `TileAnchorRegistry` (replaces `DirtFloorZoneRegistry`)
   - Strips renderer/collider — stays invisible
   - Calls `LoadFromZdo()` immediately (synchronous; data is already in the ZDO bytes)
3. `LoadFromZdo`:
   - Decode `va_heights`, `va_paint_mask`, `va_disk_records`
   - Find or create the parent `DirtFloorMeshCombiner` based on adjacent loaded tiles
   - Attach a `TerrainTile` MonoBehaviour, populate from decoded data
   - Combiner's `RebuildMeshUnion()` runs: welds this tile's slice into its mesh
4. `TileAnchor.Update` runs `CheckLoad()` every frame — when `ZDO.DataRevision` changes (because owner peer wrote new data), re-decode and re-weld. **This is the vanilla replication pattern.** No client-to-client RPC.
5. On zone exit, the anchor is destroyed by ZoneSystem. `TileAnchor.OnDestroy` removes its slice from the combiner; combiner destroys itself if empty.

### 3.6 The save flow on edit — one path only

1. Player paints/raises/levels terrain → the existing tool (e.g. `DirtFloorPlacementTool`, `ClutterPaintTool`) calls the operation API.
2. Operation API resolves which tiles the operation touches (overlapping tile rect).
3. For each touched tile:
   - If a `TileAnchor` exists locally: call `tileAnchor.ApplyOpAndSave(op)` directly.
   - If not: dispatch an RPC to the tile's owner peer (or create the anchor locally if no peer claims the tile yet). Cross-tile RPC pattern mirrors vanilla `TerrainComp.RPC_ApplyOperation`.
4. `ApplyOpAndSave`:
   - `ClaimOwnership()` if not already owner
   - Apply the op to the local `TerrainTile.HeightGrid` / `PaintGrid` / `DiskRecords`
   - Re-encode and write the 3 byte arrays to the ZDO (bumps `DataRevision`)
   - Trigger combiner mesh rebuild for the slice
5. ZDO replication propagates the change. Other clients with this tile loaded see `DataRevision` change in their next `CheckLoad`, re-decode, re-weld.

**This single path replaces:** `DirtFloorTerrainCompStorage` (deleted), `TerrainPersistence.SaveTerrainData` world file (deleted), `DirtFloorSaveManager` (deleted), `DirtFloorCombinerCache` (deleted or demoted), `TerrainOperationRPC` (rewritten as thin owner-dispatch around the ZDO write).

---

## 4. Mesh slicing & border dedup

### 4.1 The slicing problem

The combiner generates one mesh covering all its disks via [DirtFloorMeshCombiner.MeshGen.cs](../Modules/CustomTerrain/Core/DirtFloorMeshCombiner.MeshGen.cs). Today that mesh is monolithic per combiner group. For per-tile persistence, we need to:

- Store only the **height vertices** belonging to each tile in that tile's ZDO
- On restore, rebuild the combined mesh from the union of loaded tiles' height grids

### 4.2 The clean approach: heights as the canonical form

Store **heights, not vertices**. Then the rendered mesh is always derived from heights at runtime — same as vanilla Heightmap.

Per tile:
- `float[65*65]` height grid (1m spacing, vertices at integer world coordinates within the tile, inclusive of both edges)
- `Color32[65*65]` paint grid (same spacing)
- Both grids stored sparse (only modified verts; default = "sample WorldGenerator.GetHeight + vanilla biome paint")

On combiner mesh rebuild:
1. Walk all loaded `TerrainTile`s
2. Compute the union AABB
3. Generate vertices at every integer world (x, z) in the union, sampling the appropriate tile's grid for height/paint
4. Border vertices: a vertex at world position (x=64k, z=anything) lies on the seam between tile (k-1) and tile (k). **Both tiles store it** in their grids (it's the rightmost column of one and leftmost column of the other). They will have **identical values** if both tiles have been edited consistently. On weld, take from one canonical tile (e.g. the lower-index tile). Drift detection: if values differ by more than ε (e.g. 0.01m), log a warning and average — but in normal operation this can't happen because the same operation writes to both tiles.

### 4.3 Why store both border copies instead of letting one tile own the border

- Simpler ownership: each tile is a self-contained snapshot. No "tile A is incomplete unless tile B is also loaded".
- ZoneSystem may load tiles in arbitrary order. A tile must render correctly even if its neighbor isn't loaded yet (combiner mesh just stops at the non-loaded border, with no skirt needed if Y=-50 keeps it out of sight; if visible terrain is at Y=0+, render the border as a flat seam to whatever vanilla heightmap is there).
- Costs an extra 65 floats per shared border = 260 bytes — negligible vs. the 16 KB ZDO budget.

### 4.4 Cross-tile operations

A disk centered at (50, 50) with radius 20 overlaps tiles (0,0), (1,0), (0,1), (1,1). The operation API:

```csharp
public static void ApplyDisk(Disk disk) {
  var touched = TileAnchorRegistry.TilesOverlapping(disk.Bounds);
  foreach (var tileXZ in touched) {
    var anchor = TileAnchorRegistry.GetOrCreate(tileXZ);
    anchor.ApplyDiskAndSave(disk);  // RPC-dispatches to owner if not local
  }
}
```

Each tile applies the disk to its own local slice of the height/paint grids (the disk-to-vertex math is clipped to the tile bounds) and saves its ZDO independently. Combiner rebuild happens per-tile as each save completes. This naturally serializes via ZDO data-revision events.

---

## 5. Code map — what changes

### 5.1 Deleted entirely

| File | Reason |
|---|---|
| `Modules/CustomTerrain/Core/DirtFloorTerrainCompStorage.cs` | Broken TerrainComp piggyback. Unimplemented `CreateDiskFromData`. |
| `Modules/CustomTerrain/Core/TerrainPersistence.cs` | `.terraindata` world file is no longer source of truth. |
| `Modules/CustomTerrain/Core/DirtFloorSaveManager.cs` | Per-combiner save replaced by per-tile owner-write on op. |
| `Modules/CustomTerrain/Core/DirtFloorCombinerCache.cs` | Local file mesh cache demoted to optional perf cache (only worth keeping if profile shows tile-decode is slow). Default: delete. |
| `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.DiskManagement.cs` | Connectivity-based combiner grouping replaced by tile-based annexation. |

### 5.2 Rewritten

| File | New responsibility |
|---|---|
| `Modules/CustomTerrain/Core/TerrainCombinerAnchor.cs` → **renamed** `TileAnchor.cs` | Per-tile ZNetView at Y=-50; LoadFromZdo / ApplyOpAndSave / CheckLoad; no Y=0 chase, no `va_real_position` |
| `Modules/CustomTerrain/Core/DirtFloorZoneRegistry.cs` → **renamed** `TileAnchorRegistry.cs` | Lookup tile → anchor by `Vector2i`; `TilesOverlapping(bounds)` helper; `GetOrCreate(tileXZ)` factory |
| `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.Lifecycle.cs` | Tile-driven instead of connectivity-driven; `AnnexTile(TerrainTile)` / `ReleaseTile(TerrainTile)` |
| `Modules/CustomTerrain/Core/TerrainOperationRPC.cs` | Thin owner-routing for cross-tile ops; mirrors vanilla `TerrainComp.RPC_ApplyOperation` exactly |

### 5.3 New files

| File | Responsibility |
|---|---|
| `Modules/CustomTerrain/Core/TerrainTile.cs` | MonoBehaviour per loaded tile; holds HeightGrid, PaintGrid, DiskRecords; references parent combiner and TileAnchor |
| `Modules/CustomTerrain/Core/TerrainTileCodec.cs` | Sparse encode/decode of height grid + paint grid + disk records to/from byte arrays. Versioned by `va_tile_v`. |

### 5.4 Touched but not rewritten (clutter-using-statement cleanup only — task #7)

These need their `using VerdantsAscent.Terrain.Clutter` cleaned up but their persistence logic stays:
- `DirtFloorMeshCombiner.{Core,MeshGen,HeightOps,Static,Underside,Utilities,Paint,Grass}.cs`
- `DirtFloorController.cs`, `DirtFloorPatches.cs`, `BiomePaintPersistence.cs`, `TerrainCombinerAnchor.cs`-derived files

### 5.5 Untouched

- `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.MeshGen.cs` — runtime mesh generation (works great)
- `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.HeightOps.cs` — height ops (work great)
- `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.Underside.cs` — bottom face generation
- `Modules/CustomTerrain/Core/CustomHeightmap.cs`, `DirtFloorHeightmap.cs` — runtime heightmap abstraction
- `Modules/CustomTerrain/Tools/*` — disk placement, paint, undo, debug commands (call into the new API but tool logic itself is unchanged)
- `Modules/CustomTerrain/Biome/*`, `Snow/*`, `Cave/*` — these write to the anchor today; reroute their writes through the new tile API (1-3 line changes each)

---

## 6. Migration & rollout

### 6.1 No data migration needed

Worlds with old anchor-at-Y=0 ZDOs **lose their custom terrain** on first load with the new mod. Rationale:
- The old format was broken (re-combining on every load). Migration would carry forward broken data.
- The user has flagged the project as "we've learned a lot since we made the code" — the old data is treated as throwaway.
- We can add a one-shot import command (`va_import_legacy_terrain`) later if there's a real megabase worth preserving. Optional, post-V1.

### 6.2 Phased delivery within the redesign

1. **Phase 8a — Plumbing.** Write `TerrainTileCodec.cs`, `TerrainTile.cs`, `TileAnchorRegistry.cs`, `TileAnchor.cs`. Compile-only, no integration yet. Existing system still runs.
2. **Phase 8b — Load path.** Wire `TileAnchor` into the combiner so loaded tiles produce a working mesh on world join. Old system still does saves. Verify a hand-edited tile loads cleanly.
3. **Phase 8c — Save path.** Reroute the operation API + `TerrainOperationRPC` through the new tile-save path. Old `DirtFloorSaveManager` / `TerrainCompStorage` / `TerrainPersistence` calls become no-ops behind a feature flag.
4. **Phase 8d — Demolition.** Delete the 5 obsolete files (§5.1). Remove the feature flag. Clean csproj.
5. **Phase 8e — Multiplayer verify.** Two clients in the same zone, edit + relog scenarios. Confirm the "non-owner client never sees terrain" bug is dead.

Each phase ends at a green build.

---

## 7. Locked decisions (2026-05-24)

1. **Tile size = 64m.** Matches `ZoneSystem` zone exactly. One anchor per zone, vertex grid 65×65, ~5-30 KB per tile gzipped. Stitching at 64m borders is the cleanest seam math because adjacent tiles' border vertices coincide by construction.

2. **Anchor Y = -5000.** Well below any expected gameplay range (Valheim seabed ≈ -30, sky-builds can go to Y=10000+). `m_distant=true` makes Y irrelevant for streaming. Y=-5000 leaves comfortable headroom both ways without flirting with Unity float-precision degradation.

3. **Disk records per-tile (self-contained).** Each tile's ZDO carries only the disks whose footprint overlaps it. Matches vanilla `TerrainComp`'s self-contained-per-tile pattern, which is the cleanest path for ZDO replication and tolerates arbitrary zone-load order. Costs ~4× ZDO bytes per disk vs. central, accepted as negligible (20 KB total across a megabase of 100 disks).

4. **Async decode + main-thread mesh ops.** Pattern from `LocationPlacementAccelerator` (LPA):
   - **Off-thread:** byte-array decode (gzip + sparse-grid expansion) via `BackgroundWorker` (already in `Modules/Async/`)
   - **Main-thread:** mesh weld / collider rebake via `FrameBudgetScheduler` (already in `Modules/Async/`) — yields per-frame to keep framerate smooth when many tiles load at once
   - Vanilla `CheckLoad` runs sync in `Update`, fine for tiny TerrainComp grids; our 65×65 grids + N disks would stutter at >5 tiles/frame sync. The async split mirrors what LPA does for location placement and what FAP does for icon rendering.
   - **Thread-safety note:** Unity collection types (Mesh, Texture2D) are main-thread-only. `WorldGenerator.GetHeight` is thread-safe (per LPA's `ThreadSafeTerrainDelta`). Decoded grids are plain `float[]` / `Color32[]` — safe to build off-thread, hand to main thread for application.

5. **Hard dependencies declared:** Infinity Hammer + Expand World (Data + Spawns + Prefabs).
   - Declared via `[BepInDependency]` on the FiresAdminTerrain plugin attribute.
   - Lets us reference IH's selection/placement types directly (cleaner editor tooling) and EW's heightmap/prefab APIs for terrain bake/clear/override flows.
   - Mod won't load without them — explicit by design per user direction.

## 8. Still-open questions (answer as we land each sub-phase)

- **Q5 (combiner welding on bridge-paint):** when two independent combiners are bridged by a newly-painted tile, do they merge in-place (smaller into larger), spawn a new union combiner, or stay separate but visually contiguous? Punt to Phase 8b — first iteration will keep them separate; merging is a follow-up if it matters visually.
- **Q7 (legacy ZDO sweep):** old anchors at Y=0 from prior mod sessions. Plan to add a one-shot `va_terrain_clear_legacy` console command + auto-sweep on first load with a `va_legacy_swept` config flag. Tracked in Phase 8d.

## 9. Phase 8a deliverables

Compile-only plumbing, no integration with existing system yet. Existing CustomTerrain keeps running.

| File | Lines (est.) | Responsibility |
|---|---|---|
| `Modules/CustomTerrain/Persistence/TerrainTileCodec.cs` | ~250 | Sparse gzip encode/decode for `float[4225]` heights + `Color32[4225]` paint + disk record list. Versioned (`va_tile_v=1`). Pure C#, no Unity main-thread deps — safe to call from `BackgroundWorker`. |
| `Modules/CustomTerrain/Persistence/TerrainTile.cs` | ~150 | MonoBehaviour data holder per loaded tile: `Vector2i TileXZ`, `float[] Heights`, `Color32[] Paint`, `List<DiskRecord> Disks`, `uint LastSeenDataRevision`. Parented under a `DirtFloorMeshCombiner`. |
| `Modules/CustomTerrain/Persistence/TileAnchorRegistry.cs` | ~180 | Static lookup: `tileXZ → TileAnchor`, `TilesOverlapping(Bounds) → IEnumerable<Vector2i>`, `GetOrCreate(tileXZ) → TileAnchor`. Replaces `DirtFloorZoneRegistry` (which stays around through 8b for the old path). |
| `Modules/CustomTerrain/Persistence/TileAnchor.cs` | ~300 | ZNetView component at Y=-5000. `Awake` → registers + `LoadFromZdo`. `Update` → `CheckLoad` (compare DataRevision). `ApplyOpAndSave` → ClaimOwnership + mutate + encode + write ZDO. `OnDestroy` → unregisters, releases tile from combiner. |

All four files compile against existing code without modifying it. Csproj gets 4 new `<Compile Include>` entries plus the 2 `[BepInDependency]` lines on the plugin attribute.

Phase 8b wires the Load path. Phase 8c reroutes Save. Phase 8d demolishes the old. Phase 8e verifies multiplayer.
