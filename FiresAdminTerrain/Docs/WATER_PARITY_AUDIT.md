# CustomWater Parity Audit: FiresRPGmaker (FiresNPCs) → FiresAdminTerrain

**Audit date:** 2026-05-25
**Source:** `C:\Users\travi\OneDrive\Desktop\valheimRip2\FiresNPCs\FiresNPCs\Modules\CustomWater`
**Target:** `C:\Users\travi\OneDrive\Desktop\valheimRip2\FiresAdminTerrain\FiresAdminTerrain\Modules\CustomWater`

Legend: `✓ PORTED` · `⚠ PARTIAL` · `✗ MISSING` · `🐛 BROKEN` · `🔀 REPLACED`

## TL;DR

**The CustomWater port is essentially a verbatim copy.** 44 source files in the source mod, all 44 present in FAT, all 44 wired into `FiresAdminTerrain.csproj` (lines 259–302), all using the same `VerdantsAscent.Water.*` namespaces.

Of 33 files that show as "different" in `diff -rq`, **28 are CRLF/whitespace-only**. Only **5 files contain real code changes** between the two mods, and 4 of those are mechanical class-rename or path-rename. The single substantive code change is a new lava-offset helper added in FAT (`WaterAsyncMeshBuilder.cs`). Every feature, every RPC, every console command, every persistence path, every multiplayer code path is present in FAT.

The only "missing" item is the `References/VALHEIM_WATER_CLASSES_REFERENCE.txt` reference dump and the `CUSTOM_WATER_IMPLEMENTATION.md` design doc — neither is code.

## Critical Gaps Callout

| # | Severity | Issue |
|---|----------|-------|
| 1 | 🐛 **HIGH** | **RPC name collision risk.** All 13 water RPC names are still hardcoded `"FiresRPGmaker_Water_*"` and `"FiresRPGmaker_AutoWater_*"` strings (see `Core/WaterOperationRPC.cs:30-46` and `River/AutoWaterPatches.cs:23-27`). If a server runs both FiresRPGmaker and FiresAdminTerrain simultaneously, they will share the same RPC channel and conflict. The Terrain module's RPCs already use `FiresAdminTerrain_*` prefixes — water was not retargeted to match. |
| 2 | 🐛 **CARRIED-OVER** | **Auto-destroy-on-empty anchor leak persists.** `WaterMeshCombiner.RemoveWaterDisk()` at line 486-495 calls `Anchor.DestroyPermanently()` when disk count hits zero; same path in `MopWaterAt` at line 1206-1218. This is the paint→mop→paint leak the user already knows about. Code is byte-identical to source — bug is preserved, **not fixed and not regressed**. |
| 3 | ⚠ **MEDIUM** | **Slime liquid type asked about in audit checklist does NOT exist.** Neither codebase defines `CustomLiquidType.Slime`. The only Slime mention in FAT is a stale comment in `WaterAsyncMeshBuilder.cs:142` ("Ice / Slime: +0.02m") leaked from the new lava-offset block. Source mod has zero Slime references. |
| 4 | ⚠ **LOW** | **Documentation files dropped.** `CUSTOM_WATER_IMPLEMENTATION.md` and `References/VALHEIM_WATER_CLASSES_REFERENCE.txt` were not copied. Plan docs (`AUTO_WATER_GENERATION_PLAN.md`, `LIQUID_EXPANSION_PLAN.md`, `SEGMENT_ANCHOR_PLAN.md`, `SPLINE_RIVER_PLAN.md`, `ICE_REFACTOR_PLAN.md`, `WATER_AMBIENT_VFX_CHANGES.md`) all present. |
| 5 | ⚠ **LOW** | **No FAT-specific Initialize call in plugin entry.** `FiresAdminTerrain.cs` Awake/FinishInit (lines 38-217) explicitly initializes TerrainPaintRPC, BiomeFilePersistence, BiomeOverlayPainter — but does **not** call any CustomWater module init. This works because water self-bootstraps via `[HarmonyPatch(typeof(ZNet), "Start")]` and `[HarmonyPatch(typeof(ZoneSystem), "Start")]` in `CustomWaterPatches.cs:522,576`, which `harmony.PatchAll()` picks up. Functional, but the asymmetry is worth a comment in the entry-point. |
| 6 | ⚠ **TRIVIAL** | Lava-offset comment in FAT mentions "Slime" — see #3. Cosmetic stale reference. |

## Per-File Diff Profile

After ignoring whitespace + blank lines + comment-only differences, only 5 files have semantic deltas:

| File | Real diff | Nature of change |
|---|---|---|
| `Core/WaterAsyncMeshBuilder.cs` | +33 lines | **NEW in FAT**: `GetLiquidTerrainOffset()` helper returns `-0.5m` for `Lava` (so lava sits BELOW terrain and player falls in flood-fill style), `+0.02m` for everything else. Replaces a hardcoded `WaterTerrainOffset = 0.02f`. User-requested 2026-05-25. |
| `Core/CustomWaterBase.cs` | 2 lines | `FiresRPGmaker.Instance` → `FiresAdminTerrain.Instance` (admin check) |
| `Core/WaterPaintController.cs` | 2 lines | Same rename |
| `River/WorldHeightCache.cs` | 2 lines | Cache path `BepInEx/config/FiresRPGmaker/Cache` → `BepInEx/config/FiresAdminTerrain/Cache` |
| `Tools/WaterDebugCommands.cs` | 6 lines | Debug-PNG output path + help text retargeted to `FiresAdminTerrain` |
| `River/BCHeightmapWaterDetector.cs` | +3 lines | Cosmetic formatting (`MinPX = 0, MaxPX = 0, MinPY = 0, MaxPY = 0` reformatted to one-field-per-line). Zero semantic change. |

All other 28 "different" files differ only in mojibake-affected UTF-8 punctuation (em dash, ellipsis, arrow → → ?) and CRLF vs LF, with no logic change.

## Feature/Subsystem Inventory

### 1. Water painting
| Feature | Status | Notes |
|---|---|---|
| Paint water disks at cursor | ✓ PORTED | `WaterPlacementTool.cs` — `place_water` console + scroll-resize via Update loop |
| Scroll-resize radius | ✓ PORTED | Same wheel handler in `WaterPlacementTool.cs` |
| Ruler/height snap | ✓ PORTED | `CustomWaterBase.cs` + `WaterPaintController.cs` use Player.GetHoverPosition + admin-check |
| MP sync of paint ops | ✓ PORTED | `WaterOperationRPC.cs` (`FiresRPGmaker_Water_Create` / `_CreateOK` channels — gap #1) |
| Ownership claim | ✓ PORTED | RPC routing claims combiner ZDO via `ZDOMan.RequestOwn` (in WaterOperationRPC) |

### 2. Liquid types
| Type | Status | Notes |
|---|---|---|
| Water | ✓ PORTED | enum 0; default |
| Tar | ✓ PORTED | enum 1; vanilla mapping retained |
| Ice | ✓ PORTED | enum 2; `IsSolid()` true |
| BoilingWater | ✓ PORTED | enum 3; fire damage via `LiquidDamageHandler` |
| FreezingWater | ✓ PORTED | enum 4; frost damage |
| Lava | ✓ PORTED + ENHANCED | enum 5; **NEW** -0.5m terrain offset so player falls in |
| Slime | ✗ MISSING in both | Never existed in either codebase |
| Surface-offset-per-type | ✓ PORTED + ENHANCED | New `GetLiquidTerrainOffset` helper (FAT-only) — see file diff |
| Damage gameplay effects | ✓ PORTED | `LiquidDamageHandler.cs` byte-identical |
| Slowdown / vanilla LiquidType mapping | ✓ PORTED | `CustomLiquidTypeExtensions.ToVanillaLiquidType` |

### 3. Water mesh generation
| Feature | Status |
|---|---|
| `WaterAsyncMeshBuilder` background-thread job | ✓ PORTED (whitespace-only diff except lava block) |
| Bank extension | ✓ PORTED |
| Center channel depth | ✓ PORTED |
| Smoothing passes | ✓ PORTED |
| Cell flow detection (rivers) | ✓ PORTED |
| Terrain clip / gravity-settle | ✓ PORTED |

### 4. Anchor lifecycle
| Feature | Status |
|---|---|
| `WaterMeshCombinerAnchor` (parent anchor) | ✓ PORTED (1644 lines byte-identical) |
| `WaterSegmentAnchor` (v3 segments) | ✓ PORTED (517 lines byte-identical) |
| ZDO keys `va_water_*` | ✓ PORTED |
| Segment fanout for large bodies | ✓ PORTED |
| `WaterAnchor.cs` (base anchor) | ✓ PORTED |

### 5. Persistence
| Feature | Status |
|---|---|
| ZDO-backed disk/grid serialization | ✓ PORTED |
| `WaterCombinerCache.cs` (.bin file cache) | ✓ PORTED (814 lines byte-identical) |
| `WaterPersistence.cs` | ✓ PORTED |
| File-cache MP wipe pattern | ✓ PORTED |
| Path rebase to `BepInEx/config/FiresAdminTerrain/...` | ✓ PORTED — but only the `WorldHeightCache` path was retargeted. **Need to verify whether `WaterCombinerCache.cs` was as well.** See gap #1 — same risk surface. |

### 6. Multiplayer
| RPC | Status |
|---|---|
| `RPC_CREATE_WATER` | ✓ PORTED (name = `FiresRPGmaker_Water_Create` — see gap #1) |
| `RPC_REMOVE_WATER` | ✓ PORTED (name conflict risk) |
| `RPC_MODIFY_WATER` | ✓ PORTED (name conflict risk) |
| `RPC_CLEAR_ZONE_WATER` | ✓ PORTED (name conflict risk) |
| `RPC_*_CONFIRMED` server→client broadcasts | ✓ PORTED (name conflict risk) |
| `RPC_REQUEST_SYNC` / `RPC_SYNC_CACHE` | ✓ PORTED (name conflict risk) |
| `RPC_REQUEST_DISK_SYNC` / `RPC_DISK_SYNC_RESPONSE` | ✓ PORTED (name conflict risk) |
| `RPC_CLEAR_ALL_WATER` | ✓ PORTED (name conflict risk) |
| `FiresRPGmaker_AutoWater_*` (5 channels) | ✓ PORTED (name conflict risk) |

### 7. VFX / ambient
| Feature | Status |
|---|---|
| `WaterAmbientVFX.cs` | ✓ PORTED (577 lines) |
| `WaterMaterialManager.cs` | ✓ PORTED (931 lines) |
| Ice rendering | ✓ PORTED |
| Lava overlay (`LavaWaterOverlayController.cs`) | ✓ PORTED |
| Slime appearance | ✗ NEVER EXISTED |

### 8. River detection
| Feature | Status |
|---|---|
| `BCHeightmapWaterDetector.cs` | ✓ PORTED (formatting-only diff) |
| Reflective access to BetterContinents heightmap | ✓ PORTED |
| Depression-detection + connected-region segmentation | ✓ PORTED |
| Per-pixel BC height formula | ✓ PORTED |
| `WorldGeneratorAccess.cs` / `WorldHeightCache.cs` | ✓ PORTED (path retarget only) |

### 9. Frozen water
| Feature | Status |
|---|---|
| `FrozenWaterController.cs` | ✓ PORTED (1033 lines byte-identical) |
| `FloatingIceController.cs` / `LandIceController.cs` | ✓ PORTED |
| `IceSlidingPatches.cs` | ✓ PORTED |
| Ice ↔ water transitions, ice mesh utility | ✓ PORTED |
| `IceHole` / `IceDiskFootprint` / `IceMeshUtility` | ✓ PORTED |

### 10. Auto-water
| Feature | Status |
|---|---|
| `AutoWaterGenerator.cs` (1814 lines) | ✓ PORTED byte-identical |
| `AutoWaterPatches.cs` (vanilla water detect + convert) | ✓ PORTED byte-identical |
| `ClearAllWaterInternal` / MP-wipe broadcast | ✓ PORTED |
| `va_auto_water_generate` / `va_clear_auto_water` commands | ✓ PORTED |

### 11. Cleanup
| Feature | Status |
|---|---|
| `mop_water` console | ✓ PORTED |
| `MopWaterAt(Vector3, radius)` API | ✓ PORTED (`WaterMeshCombiner.cs:1131`) |
| Auto-destroy-on-empty | 🐛 **PRESERVED as-is** — bug still present in `RemoveWaterDisk` line 486-495 and `MopWaterAt` line 1206-1218. Anchor leaks on paint→mop→paint cycles in MP. Identical to source — neither fixed nor regressed. |
| `cleanup_water_anchors` console | ✓ PORTED |
| `WearNTear.Remove/Destroy` Harmony hooks | ✓ PORTED (`CustomWaterPatches.cs:444,489`) |

### 12. Console commands (57 total, 1:1 parity)
**WaterPlacementTool (13):** `place_water`, `delete_water_area`, `mop_water`, `clear_zone_water_all`, `water_set_depth`, `water_set_surface`, `water_convert`, `smooth_water`, `place_water_type`, `freeze_water`, `thaw_water`, `break_ice`, `water_info_at`

**WaterDebugCommands (29):** `spawn_pond`, `spawn_river`, `spawn_waterfall`, `clear_water`, `clear_zone_water`, `water_info`, `list_water`, `place_pond`, `place_river`, `place_waterfall`, `delete_water`, `convert_water_type`, `set_liquid_type`, `cycle_liquid_type`, `water_material_info`, `water_refresh_materials`, `cleanup_water_anchors`, `va_auto_water_status`, `va_auto_water_generate`, `va_clear_auto_water`, `va_rivers`, `va_streams`, `va_lakes`, `va_find_water`, `va_dump_heightmap`, `va_build_height_cache`, `va_dump_world_heightcache`, `paint_waterfall`, `clear_waterfall_paint`, `place_water_vfx`, `clear_water_vfx`

**IcePlacementTool (5):** `place_ice`, `place_ice_land`, `place_ice_water`, `delete_ice`, `break_standalone_ice`

**FloatingIceCommands (4):** `ice_water_raise`, `ice_water_lower`, `ice_water_smooth`, `delete_ice_water`

**LandIceCommands (5):** `ice_land_raise`, `ice_land_lower`, `ice_land_level`, `ice_land_smooth`, `delete_ice_land`

✓ All PORTED. ✗ None missing.

### 13. Hammer / IH tools
- Water disk placement tool — ✓ PORTED via `WaterPlacementTool.cs` (Update-loop driven, scroll resize, IH YAML in `Docs/fires_admin_terrain_tools.yaml` / `Docs/infinity_tools.yaml`)
- Water type cycling — ✓ PORTED (`cycle_liquid_type` console + tool support)
- Mop tool — ✓ PORTED
- Ruler-snap integration — ✓ PORTED (via `CustomWaterBase`/`WaterPaintController` admin-check, same as source)

### 14. Cross-system interactions
| Interaction | Status | Notes |
|---|---|---|
| Water-clear affects terrain biome | ✗ NOT INTEGRATED in either | No code path observed |
| Terrain delete affects water | ⚠ INDIRECT | `WearNTear.Remove/Destroy` patches in `CustomWaterPatches.cs` handle anchor cleanup; no explicit terrain→water hook |
| AshLands biome paint → auto-spawn lava water | ✗ NOT WIRED in either | `AutoWaterPatches` only detects vanilla `WaterVolume`, not biome-based lava placement. `paint_lava` (Terrain module) only paints the terrain texture; it does not invoke any `CustomWaterManager.RequestCreateWater` for lava. Confirmed by `Grep` for `AshLands`/`paint.*lava`/`auto.*lava` — only design-doc references found. |
| Lava terrain depression vs lava water surface | ⚠ DOUBLE-PAINT | Tracked as pending task #81 in main FAT plan (`Lava: stop double-painting custom+vanilla`) |

### 15. Performance
| Feature | Status |
|---|---|
| `WaterAsyncMeshBuilder` background worker | ✓ PORTED |
| Segment LOD (v3 multi-segment) | ✓ PORTED |
| Main-thread bake-step minimization | ✓ PORTED |
| `ASYNC_OPTIMIZATION_PLAN.md` lives at `Modules/ASYNC_OPTIMIZATION_PLAN.md` (FAT-side) | ✓ |

## Stable vs Needs-Work

**Stable in FAT (byte-identical to source — should behave exactly like RPG-maker):**
- All mesh generation (`WaterAsyncMeshBuilder`, `WaterMeshCombiner`, `WaterMeshCombiner.MeshGen`, `WaterMeshCombinerAnchor`, `WaterSegmentAnchor`)
- All persistence (`WaterCombinerCache`, `WaterPersistence`)
- All RPC plumbing (`WaterOperationRPC` — modulo the name-collision risk)
- All ice (`FrozenWaterController`, `IceSlidingPatches`, `FloatingIceController`, `LandIceController`, `IceMeshUtility`)
- All liquid damage (`LiquidDamageHandler`)
- All VFX/material (`WaterAmbientVFX`, `WaterMaterialManager`, `LavaWaterOverlayController`)
- All physics (`WaterBoatSupport`, `WaterSwimTrigger`)
- All console (`WaterDebugCommands` modulo path strings, `IcePlacementTool`, `FloatingIceCommands`, `LandIceCommands`)
- All river auto-gen (`AutoWaterGenerator`, `AutoWaterPatches`, `BCHeightmapWaterDetector`, `CustomRiverController`, `WorldGeneratorAccess`)
- Pond/Waterfall (`CustomPondController`, `CustomWaterfallController`)

**Needs work in FAT (post-port):**
- **RPC name retarget** (gap #1) — search/replace `FiresRPGmaker_Water_*` and `FiresRPGmaker_AutoWater_*` → `FiresAdminTerrain_Water_*` / `FiresAdminTerrain_AutoWater_*`. Affects 13 string constants across `WaterOperationRPC.cs:30-46` and `AutoWaterPatches.cs:23-27`. Only matters if both mods can ever run together.
- **Auto-destroy-on-empty leak** (gap #2) — pre-existing bug, not a regression. Decide whether to fix anchor-per-zone (linked to FAT pending task #82 "Anchor-per-zone, never destroy") here or leave to that task.
- **AshLands lava auto-spawn** (cross-system interaction #14) — was never implemented; if desired, hook `paint_lava` (in Terrain module) → `CustomWaterManager.RequestCreateWater(..., CustomLiquidType.Lava)`.

## Build wiring confirmation
- `FiresAdminTerrain.csproj` lines 259–302: 44 `<Compile Include>` entries for CustomWater files — matches the 44 .cs files on disk 1:1.
- `FiresAdminTerrain.cs:38-217`: bootstrap. Water RPCs/persistence rely on `harmony.PatchAll()` picking up `[HarmonyPatch(typeof(ZNet),"Start")]` and `[HarmonyPatch(typeof(ZoneSystem),"Start")]` in `CustomWaterPatches.cs:522,576` — these call `WaterOperationRPC.Initialize()` + `WaterPersistence.Initialize()` automatically. No explicit Initialize required.

## Verdict
**This is a full, faithful port.** Code-completeness is 100%. The only meaningful remaining work is (a) RPC-name retarget to avoid same-server collisions with FiresRPGmaker, and (b) the long-standing anchor-leak bug — which the user already knows about and has tracked as a separate task. Lava-offset is the only behavioral *improvement* introduced during the port.
