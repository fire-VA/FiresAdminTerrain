# Phase F — Legacy DirtFloor Decommission (complete)

**Landed 2026-05-25** in one pass. Build clean: 0 errors, 0 warnings.

## What got deleted

45 files removed from the source tree + csproj (about 47K lines):

**DirtFloor core (combiner + controller + persistence):**
- `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.{Core,MeshGen,Lifecycle,HeightOps,DiskManagement,Utilities,Static,Grass,Underside}.cs` (9 partials)
- `Modules/CustomTerrain/Paint/DirtFloorMeshCombiner.Paint.cs` (10th partial, ~2000 lines)
- `Modules/CustomTerrain/Core/DirtFloorController.cs`
- `Modules/CustomTerrain/Core/DirtFloorTerrainManager.cs`
- `Modules/CustomTerrain/Core/DirtFloorZoneRegistry.cs`
- `Modules/CustomTerrain/Core/DirtFloorCombinerCache.cs`
- `Modules/CustomTerrain/Core/DirtFloorAsyncMeshBuilder.cs`
- `Modules/CustomTerrain/Core/DirtFloorHeightmap.cs`
- `Modules/CustomTerrain/Core/DirtFloorSaveManager.cs`
- `Modules/CustomTerrain/Core/DirtFloorTerrainCompStorage.cs`
- `Modules/CustomTerrain/Core/CustomHeightmap.cs`
- `Modules/CustomTerrain/Core/TerrainCombinerAnchor.cs`
- `Modules/CustomTerrain/Core/TerrainPersistence.cs`
- `Modules/CustomTerrain/Core/TerrainOperationRPC.cs`
- `Modules/CustomTerrain/Core/TerrainDisk.cs`
- `Modules/CustomTerrain/DirtFloorPatches.cs` (1700-line Harmony patch surface)
- `Modules/CustomTerrain/DirtFloorTerrainManager.cs`
- `Modules/CustomTerrain/GrassOverlaySystem.cs`

**Paint / clutter (legacy systems superseded by tile codec + Phase D5):**
- `Modules/CustomTerrain/Paint/DirtFloorPaintMaskStreaming.cs`
- `Modules/CustomTerrain/Paint/VertexPaintSystem.cs`
- `Modules/CustomTerrain/Paint/TerrainPaintRPC.cs`
- `Modules/CustomTerrain/Paint/TerrainPaintManager.cs`
- `Modules/CustomTerrain/Paint/ZonePaintManager.cs`
- `Modules/CustomTerrain/Paint/ZonePaintLayer.cs`
- `Modules/CustomTerrain/Paint/TerrainPaintLayer.cs`
- `Modules/CustomTerrain/Clutter/ClutterPaintTool.cs`
- `Modules/CustomTerrain/Clutter/ClutterPersistence.cs`
- `Modules/CustomTerrain/Clutter/ClutterPaintInputHandler.cs`

**Tools & legacy debug surface:**
- `Modules/CustomTerrain/Tools/InfinityHammerCompat.cs` (legacy — confirmed dead per audit)
- `Modules/CustomTerrain/Tools/DirtFloorPlacementTool.cs`
- `Modules/CustomTerrain/Tools/TerrainOperationParams.cs`
- `Modules/CustomTerrain/Tools/TerrainOperationExecutor.cs`
- `Modules/CustomTerrain/Tools/TerrainDebugCommands.cs` (2900 lines of combiner-debug commands)
- `Modules/CustomTerrain/Tools/TerrainCommandParser.cs`
- `Modules/CustomTerrain/Tools/TerrainUndoManager.cs` (legacy combiner-undo; replaced by `Persistence/TileUndoLog.cs`)
- `Modules/CustomTerrain/Utilities/DirtFloorLODManager.cs`
- `Modules/CustomTerrain/Utilities/DirtFloorDeferredInit.cs`
- `Modules/CustomTerrain/Cave/TerrainCaveEnvironment.cs` (RPG-maker cave system, out of FAT scope)
- `Modules/CustomTerrain/Cave/TerrainCaveDetector.cs`
- `Modules/CustomTerrain/TerrainAPI.cs` (legacy public API; superseded by `TileBrush` + `RegionScrubber`)

## What got surgically modified (kept files)

| File | Change |
|------|--------|
| `Modules/CustomTerrain/Clutter/ClutterManager.cs` | Rewritten as a no-op compatibility shim — every legacy method now logs deferred-to-D5 and either returns empty or routes to `ClutterSystem.ResetGrass`. Keeps every KEEP-file caller compiling. |
| `Modules/CustomTerrain/Biome/BiomePaintPersistence.cs` | Removed two `DirtFloorMeshCombiner.GetCombinerAt` branches; clutter restore re-routes through `ClutterManager`; `TerrainOperationRPC.IsMultiplayer` calls removed (now always-local apply, ZDO replication propagates). |
| `Modules/CustomTerrain/Biome/BiomeManager.cs` | `RefreshTerrainBiomeColors` and `RefreshAllBiomeColors` collapsed to no-ops (tile system self-refreshes on `NotifyMutated`). |
| `Modules/CustomTerrain/Biome/BiomeOverlayPainter.cs` | Undo bracket now uses `TileUndoLog` instead of the deleted `TerrainUndoManager`. |
| `Modules/CustomTerrain/Snow/SnowTrailController.cs`, `SnowCommands.cs`, `SnowClutterConfig.cs` | `ClutterPaintTool.*` calls routed through `ClutterManager.*` (which has the same surface as no-op stubs). |
| `Modules/CustomWater/Core/WaterMeshCombiner.MeshGen.cs` | Custom-terrain sampling now uses `TileAnchorRegistry.TrySampleHeightAtWorld` instead of `DirtFloorMeshCombiner.GetCombinerAt`. |
| `Modules/CustomWater/Core/WaterMeshCombinerAnchor.cs` | `CheckAndConvertToWaterAnchor(DirtFloorController)` legacy fallback removed (no callers). |
| `Modules/CustomWater/Core/CustomWaterBase.cs` | `SampleTerrainHeight` now uses tile-anchor lookup. |
| `Modules/CustomWater/CustomWaterPatches.cs` | `TerrainCaveDetector` cave-fast-path removed; `TerrainCombinerAnchor` typed lookup dropped in favor of ZDO key check. |
| `Modules/EnvironmentBoxes/EnvironmentBoxPatches.cs` | Custom-terrain detection now keys off `TerrainTile` component; legacy DirtFloor probes + `ConvertVanillaPaintType` helper removed. |
| `Modules/CustomTerrain/Lifecycle/RegionScrubber.cs` | `VanillaPaint` scope layer no-op'd (ZonePaintManager is gone); enum flag stays for future re-wire to direct `TerrainComp.PaintCleared` clear. |
| `FiresAdminTerrain.cs` | Stale `DirtFloorClientPatches` dedi-skip entry removed; legacy `TerrainPaintRPC.Initialize` call removed (RegionScrubberRPC + TileBrushRPC + TerrainPaintRPC's own ZNet.Awake hook handle init). |

## What the user-visible system looks like now

The "main path" for terrain ops is now a single, coherent set:

```
User input (console / IH brush / vanilla hoe)
      │
      ▼
TileBrush.cs / TileBrushCommands.cs ─────────► TileBrushRPC (MP sync, admin-gated)
      │
      ▼
TileAnchor.ApplyOpAndSave  ◄────  TileUndoLog (Begin/End)
      │
      ▼
TileMeshRenderer.RebuildMesh

For clears / wipes:
RegionScrubber.ScrubRegion → ApplyLocally → TileAnchor.ClearData + BiomePaintPersistence + ClutterManager + ClutterSystem
                          └─► RegionScrubberRPC (MP request pattern, admin-gated)

For vanilla terrain paint overlap:
TerrainOp.Awake / TerrainComp.DoOperation postfix → RegionScrubber.ScrubRegion(VanillaBiome | Clutter | VanillaGrass)
```

## Known follow-ups (intentional gaps to fill in subsequent phases)

| Gap | Phase | Notes |
|-----|-------|-------|
| Painted-clutter spawn (snow trail, biome auto-clutter) | **D5** | `ClutterManager.PaintClutter` is a no-op stub. FAP source-link clutter wires it up. |
| Tile paint mask render (Paint Dirt/Cult/Paved doesn't visually show) | **D1** | Tile codec persists `Paint[]` but `TileMeshRenderer` doesn't sample it. Per-tile material instance + per-tile `Texture2D` lands the render. |
| Biome stitching across tile borders | **D2** | Per-tile material instance refactor (same one as D1). |
| Lava water auto-spawn on AshLands paint | **D3** | Needs water `IRegionAnchor` (Phase E2). |
| Vanilla heightmap paint mask clear (`VanillaPaint` scope) | G follow-up | Currently no-op; future direct `TerrainComp.PaintCleared` writer if needed. |
| Old worlds with orphan `floor_paint_v3` ZDOs / `.terraindata` files | F-follow-up | Explicit `va_migrate_zone` command (user picked this option). No auto-migration. Old data sits dormant in saves until manually migrated. |

## Migration story for existing worlds

The legacy stack wrote to:
- `floor_paint_v3` ZDOs (DirtFloorController persistence)
- `.terraindata` / `.clutterdata` / `.biomedata` files alongside the world `.fwl`
- TerrainComp ZDO bytes (biome paints — STILL READ by `BiomePaintPersistence`, this is the seam we kept)

After Phase F:
- Old `floor_paint_v3` ZDOs are loaded but ignored (no `DirtFloorController` to consume them).
- Old `.terraindata` / `.clutterdata` files are no longer written; existing ones are dormant on disk.
- Biome paint bytes on TerrainComp ZDOs still flow through `BiomePaintPersistence` — the user's painted biomes survive.

The `va_migrate_zone` command (not yet written) would read `floor_paint_v3` ZDO data per zone and convert it into per-vertex `TerrainTile` data on the matching `TileAnchor`. Until then, builds from the legacy era will appear flat/empty in newly visited zones — but tiles painted via the new system will work normally.

## Build status

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Ready for live test.
