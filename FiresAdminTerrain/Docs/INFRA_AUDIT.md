# FiresAdminTerrain — Cross-Cutting Infrastructure Audit

**Date:** 2026-05-25
**Scope:** Code that spans both CustomTerrain and CustomWater, plus everything between the plugin entry point and the first per-tile/per-disk op
**Reference baseline:** `C:\Users\travi\OneDrive\Desktop\valheimRip2\FiresNPCs\FiresNPCs` (the RPG-maker mod this was stripped from)

---

## 0. Plugin entry, init order, dependencies

### 0.1 Plugin attribute (`FiresAdminTerrain.cs:21-26`)

```csharp
[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
public class FiresAdminTerrain : BaseUnityPlugin
{
    public const string PluginGUID = "com.Fire.FiresAdminTerrain";
    public const string PluginName = "FiresAdminTerrain";
    public const string PluginVersion = "0.1.0";
```

**Finding:** No `[BepInDependency]` attributes. Open task #12 ("Add IH + EW hard dependencies") is still pending. Today the mod auto-loads regardless of whether InfinityHammer, ExpandWorldData, ServerDevcommands, or WorldEditCommands are present. Both `TileHammerCompat` and `TileAnchorPrefab.ClearInfinityHammerSpriteCache` use reflective detection (`AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "InfinityHammer")`), so missing IH is tolerated.

Without `[BepInDependency]`, BepInEx load order is alphabetical only:
- `FiresAdminTerrain` < `Infinity Hammer` ⇒ at our Awake time IH is not yet in AppDomain.
- `FiresAdminTerrain` < `World Edit Commands` ⇒ same.
- `FiresAdminTerrain` < `ServerDevcommands`, but > `ExpandWorld...` (which start with E).

`TileHammerCompat.Initialize()` is therefore called from `TileAnchorPrefab.Ensure()` (which runs in ZNetScene.Awake postfix) — by then every plugin has been instantiated, so IH detection works. This is documented at `TileAnchorPrefab.cs:175-184`. The legacy `Modules/CustomTerrain/Tools/InfinityHammerCompat.cs:Initialize()` is **never called** from anywhere.

### 0.2 Awake → FinishInit flow (`FiresAdminTerrain.cs:38-217`)

```
Awake()
  ├─ Instance = this
  ├─ InstallLogFilter()         ← swaps Debug.unityLogger.logHandler before any asset load
  ├─ RootObject + PrefabContainer (DontDestroyOnLoad)
  ├─ ConfigManager.Instance.Initialize(Config)
  ├─ configSync = new ConfigSync(PluginGUID)
  │     ├─ AddLockingConfigEntry(configServerAuthority)
  │     ├─ AddConfigEntry(configVerboseLogging)
  │     ├─ AddConfigEntry(configHeightCacheResolution)
  │     ├─ AddConfigEntry(configHeightCacheSamplesPerFrame)
  │     └─ AddConfigEntry(configUseNewTilePersistence)
  ├─ LoadAssets()                ← embedded firesnpcs bundle
  └─ FinishInit() OR DelayedDedicatedServerInit() → FinishInit()

FinishInit():
  ├─ if !dedi:  harmony.PatchAll()
  │   else:    iterate assembly types, skip _clientOnlyPatchTypes set, patch one-by-one
  ├─ TerrainPaintRPC.Initialize()                  ← always (server+client)
  ├─ BiomeFilePersistence.Initialize()             ← always
  └─ if !dedi: BiomeOverlayPainter.RegisterCommands()
```

**Concern:** `TileBrushRPC.Register()` is hooked from `[HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]` postfix (file `TileBrushRPC.cs:188-192`). `TerrainOperationRPC.Initialize()` is **never explicitly called** — there is no `Initialize()` invocation anywhere in the codebase except inside `TerrainPaintRPC.Initialize` which doesn't call its sibling. The 10+ RPCs declared in `TerrainOperationRPC.cs:42-55` (`FiresRPGmaker_Terrain_Create`, `FiresRPGmaker_Terrain_Level`, `FiresRPGmaker_Clutter_PaintSync`, etc.) **silently never register**, which means any code that calls `TerrainOperationRPC.RequestX(...)` will succeed locally but the broadcast is a no-op. Most of those entry points are in legacy DirtFloor code paths that aren't actively triggered by today's brushes, but `BiomePaintPersistence.cs:165` and `:280` call `TerrainOperationRPC.RequestBiomePaint(...)` which won't sync to other clients.

### 0.3 _clientOnlyPatchTypes (FiresAdminTerrain.cs:102-106)

Only one type is in the skip set:
```
"VerdantsAscent.Terrain.DirtFloorPatches+DirtFloorClientPatches"
```
which contains the `Hud.UpdateCrosshair` patch at `DirtFloorPatches.cs:1048`.

Searched for other client-only `[HarmonyPatch(typeof(...))]` references — none on Hud / Minimap / InventoryGui / FejdStartup. The 19 files that use `Hud.instance.X` / `GameCamera.instance.Y` as runtime calls (not patch targets) are safe under Harmony IL rewriting — Mono only crashes when the **target** type is unresolvable, not when a method body references an unresolved type at runtime.

### 0.4 Asset bundle (`FiresAdminTerrain.cs:244-280`)

One bundle, embedded as `firesnpcs` resource. Loaded once at Awake into `FiresAdminTerrain.assetBundle`. Used by `TileAnchorPrefab.LoadSourcePrefab()` to source `piece_dirtfloor_vad` (line 199) and indirectly by water/terrain shader caches. **No catalog of "what's in the bundle vs what's loaded" exists** — would require Unity AssetStudio inspection to enumerate; from the code path:
- Direct loads: `piece_dirtfloor_vad` (one prefab)
- Indirect via TerrainMaterialCache / WaterMaterialManager: shader instances, mesh templates (not enumerated)

---

## 1. Infinity Hammer ruler integration (TileBrushCommands + TileHammerCompat)

### 1.1 Resolution order for brush center

`TileBrushCommands.ResolveCenter(args, out center)` at line 86-96:

```
1. TryParseFromArg(args, out center) ← parses "from=x,z,y" if present (IH <area> expansion)
2. TileBrush.TryGetPointerWorldPosition(out center) ← camera raycast 600m → ray-plane fallback → feet
```

**Status: GOOD.** `from=` is honored when IH expands `<area>`. Standalone console commands fall through to the raycast helper.

### 1.2 Radius resolution

`ResolveRadius()` at line 301-305:
```
1. TryParseCircleArg() ← parses "circle=R-R2", returns outer radius R
2. ParseFloat(args, 1, defaultRadius) ← positional arg[1]
```

**Status: GOOD.** IH's player-scrolled ruler radius overrides the positional default.

### 1.3 Height resolution

For `Cmd_Flat`, `ResolveAbsoluteHeight()` at line 307-318:
```
1. TryParseNamedFloat(args, "height", ...) → ADDED to click Y  (correct IH <h> offset semantics)
2. Positional arg[2] as ABSOLUTE Y                              (console UX, backward compat)
3. Default to baseY (click point's Y)
```

For `Cmd_Raise` / `Cmd_Lower`, named `height=` becomes the delta directly.

**Status: GOOD.**

### 1.4 `<area>` token expansion

The YAML uses `<area>` which IH expands to `from=<x>,<z>,<y> circle=<r>-<r2> …`. All Tile_* brush tools in `infinity_tools.yaml` (lines 309-413) include `<area>`. The brush commands parse this correctly. The IH ruler ring "click to place" UX therefore works end-to-end.

### 1.5 BaseRuler.Snap postfix (`TileHammerCompat.cs:67-115`)

Postfix on `InfinityTools.BaseRuler.Snap(Vector3)`. The patch overrides `__result.y` to the ghost's Y when:
- Path A: one of `s_ourToolNames` is the active piece (HashSet at line 129-141)
- Path B (fallback): cursor over an existing loaded tile

**Status: GOOD.** Tool-name lookup matches the YAML piece names exactly.

### 1.6 `ZoneSystem.GetGroundHeight` ruler patches (`TileHammerCompat.cs:253-344`)

Two overloads patched, gated by `s_rulerSnapDepth` so non-ruler callers (vegetation placement, AI grounding, character grounding) still see vanilla heights. This is the real ruler fix and it works correctly per the comment at line 304-315.

### 1.7 Gaps in ruler integration

1. **`Cmd_Where` / `va_brush_where`** uses `ResolveCenter()` so it does respect `from=`. **OK**, just noting.
2. **`Cmd_Clear`** does NOT have any flat-height-after-clear logic. Clear ops don't need height; this is fine.
3. **Lava/AshLands biome paint** (Cmd_Biome at line 215-287) ignores `<h>` even though the lava depression depth depends on the ruler offset. Currently `LAVA_MAX_DEPTH = 0.6f` is hardcoded at `TileBrush.cs:248`. The Page-Up/Page-Down ruler offset has no effect on lava depth.
4. **`<terrainGrid>` token** used by vanilla WEC `Level` tool (line 446) — our tools don't use it. **OK**, our brushes are not grid-snapped.
5. **`<to>` token** used by `Slope` (line 462) — not consumed by our brushes. **OK**, no slope brush.

---

## 2. Hammer tool selection (TileHammerCompat + TileBrushDrag)

### 2.1 `s_ourToolNames` in TileHammerCompat (`TileHammerCompat.cs:129-141`)

```
Floor, Raise Terrain, Raise (Sharp), Lower Terrain,
Paint Dirt, Paint Cultivated, Paint Paved,
Clear Brushed Area, Delete Tiles,
Biome Meadows, Biome BlackForest, Biome Plains, Biome Swamp,
Biome Mountain, Biome Mistlands, Biome Ashlands (Lava),
Biome DeepNorth, Clear Biome
```

### 2.2 YAML actual tool names (`infinity_tools.yaml`)

The HOE entries in the current YAML use:
- `Floor (10m)`, `Floor (25m)`, `Floor (50m)` ← **mismatch**, the patch only matches bare `Floor`
- `Raise Terrain` ✓
- `Raise (Sharp)` ✓
- `Lower Terrain` ✓
- `Paint Dirt`, `Paint Cultivated`, `Paint Paved` ✓
- `Clear Brushed Area` ✓
- `Tile Info`, `List Tile Anchors`, `Force VATile Prefab Register` ← not in s_ourToolNames; not brush tools, OK
- **No `Delete Tiles`, `Biome *`, or `Clear Biome` entries in the YAML at all**

**Cross-cutting bugs:**
- **BUG-2.1 (HIGH):** `Floor (10m)/(25m)/(50m)` ≠ `Floor`. Ruler ring will NOT lift on those tools because Path A check fails. Ring falls through to Path B (existing loaded tile check), which works AFTER first placement on virgin ground but not before. Fix: rename YAML pieces to `Floor`, OR add the parenthesized variants to `s_ourToolNames`, OR switch the check to a `StartsWith("Floor")` match.
- **BUG-2.2 (MED):** YAML has no biome tools. The biome tool names in `s_ourToolNames` are dead — they were expected but never exposed.

### 2.3 TileBrushDrag.IsOurBrushToolSelected (`TileBrushDrag.cs:150-171`)

```csharp
switch (piece.m_name)
{
    case "Floor":              ← same mismatch as 2.1
    case "Raise Terrain":
    case "Raise (Sharp)":
    case "Lower Terrain":
    case "Paint Dirt":
    case "Paint Cultivated":
    case "Paint Paved":
    case "Clear Brushed Area":
        return true;
}
```

**Cross-cutting bugs:**
- **BUG-2.3 (HIGH):** Same `Floor` vs `Floor (10m)` mismatch. **Click-and-drag will not fire for any Floor tool.** Player has to click each time to place a flat platform — the drag-paint UX silently fails.
- **BUG-2.4 (LOW):** No biome / no delete in the drag list — by design (delete is comment-noted as intentionally not drag-repeatable at line 138-143; biome same logic).

### 2.4 Drag behavior

`TileBrushDrag.Update` at line 82-145:
- Polls `Input.GetMouseButton(0)` while one of our tools is active and the cached brush is < 2s old.
- Re-fires at 10Hz (`DRAG_INTERVAL = 0.1f`).
- For Flat: locks Y to the first-click cursor height (correct RPG-maker behavior).
- For Raise / Lower: re-fires the cached delta per tick. **Concern:** since each tick repeats the delta, holding down on a Raise tool with delta=1 for 1 second would attempt 10× +1m raises = +10m. The no-op-skip in `ApplyOpAndSave` does NOT prevent this (the verts ARE changing each tick). Drag-raise effectively becomes "rapid stacking" — may surprise players who expect "one click = one delta".

---

## 3. Server authority / ConfigSync flow

### 3.1 Wiring (`FiresAdminTerrain.cs:54-65`)

```csharp
configSync = new ConfigSync(PluginGUID) { ... };
configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
configSync.AddLockingConfigEntry(ConfigManager.Instance.configServerAuthority);
configSync.AddConfigEntry(ConfigManager.Instance.configVerboseLogging);
configSync.AddConfigEntry(ConfigManager.Instance.configHeightCacheResolution);
configSync.AddConfigEntry(ConfigManager.Instance.configHeightCacheSamplesPerFrame);
configSync.AddConfigEntry(ConfigManager.Instance.configUseNewTilePersistence);
```

Only 5 entries are synced. Anything not in this list is per-client (e.g. `LAVA_MAX_DEPTH`, `RENDER_ACTIVATE_DIST`, all the snow / ice / water module configs).

### 3.2 Admin gates that ARE checked

`configSync?.IsAdmin == true` is checked in:
- `EnvironmentBoxScaler.cs:204`
- `EnvironmentBoxController.cs:1745`
- `TerrainCaveEnvironment.cs:680`
- `CustomWaterBase.cs:668`
- `WaterPaintController.cs:433`
- `DirtFloorMeshCombiner.Underside.cs:649` (in legacy DirtFloor code)

`AdminSyncing.IsAdmin(sender)` server-side checks in:
- `AutoWaterPatches.cs:169, 249, 353` (request gates for auto-water RPCs)

### 3.3 Admin gates that are MISSING

**BUG-3.1 (HIGH):** **`TileBrushRPC.Recv_*` handlers (lines 141-184) have ZERO admin checks.** Any client (admin or not, on a server with `ConfigServerAuthority=true`) can broadcast `va_brush_flat / raise / paint / clear / biome` via `ZRoutedRpc.Everybody`, and every peer will obediently execute the op. On a server where players are non-admin, they can:
- Permanently alter terrain anywhere in the world (paint, raise, lower, biome flip)
- Flood worldspace with custom tile ZDOs (resource exhaustion vector)
- Bypass the WEC / IH admin checks the YAML implies

The fix is one line per handler: `if (sender != 0L && !AdminSyncing.IsAdmin(sender)) return;` — same pattern AutoWaterPatches uses. Compare to `TerrainOperationRPC.RPC_Handle*` which the FiresNPCs original was structured around server-side admin checking (though even those handlers in this codebase don't gate by IsAdmin — see also BUG-3.2 below).

**BUG-3.2 (MED):** `TerrainOperationRPC.RPC_Handle*` (terrain create, raise, paint biome, etc.) and `TerrainPaintRPC.RPC_Handle*` are similarly unguarded. Less impactful because those code paths are no longer driven by today's brush tools, but anyone with the RPC names can fire them.

**BUG-3.3 (LOW):** `BiomeOverlayPainter.RegisterCommands` (vanilla heightmap biome paint, line 408 onward) uses `isCheat: true`. Cheat gate is the only check. Server-side `BiomePaintPersistence.RPC_HandleBiomePaint` (in BiomePaintPersistence.cs) — needs verification it has an IsAdmin guard.

### 3.4 ConfigSync lockedConfigChanged event

`ConfigManager.OnConfigLockChanged()` is wired but its body just invokes `ConfigLockChanged?.Invoke()` — and **nothing subscribes to that event**. The hook is a no-op. Compare to `AdminSyncing.RPC_AdminStatusSync` which uses reflection to call back into `FiresAdminTerrain.OnConfigLockChanged` (line 93-95) — that method doesn't exist on the plugin in this codebase. The reflection call silently no-ops.

---

## 4. ZDO ownership / claim patterns

### 4.1 TileAnchor (the canonical pattern, `TileAnchor.cs:243-273`)

```csharp
if (!_nview.IsOwner())
{
    _nview.ClaimOwnership();
    if (!_nview.IsOwner())
    {
        FiresLogger.LogWarning("ClaimOwnership did not promote us; needs RPC dispatch (Phase 8d).");
        return 0;
    }
}
EnsureTile();
int wrote = mutate(Tile);
if (wrote <= 0) return 0;
WriteToZdo();
Tile.NotifyMutated();
```

**Status: GOOD for single-player and admin-host MP.** ClaimOwnership succeeds synchronously when nobody else owns the ZDO or the requesting client beats the others. Real MP behavior depends on the network round-trip timing.

The warning fires when claim doesn't promote us in the same tick. Recovery path is described as "needs RPC dispatch (Phase 8d)" but Phase 8d landed only the TileBrushRPC fan-out — there is no fallback "I'll defer this op to the actual owner" logic. **The op is silently dropped** when claim fails.

### 4.2 No-op skip (`TileAnchor.cs:243-273`)

The `Func<TerrainTile, int>` overload short-circuits when the inner op wrote 0 verts — no ZDO write, no NotifyMutated. This is a critical perf fix mentioned in the comment at line 230-241 (avoids 11 FPS on drag).

### 4.3 ZDO replication for remote-driven decode (`TileAnchor.CheckLoad` at line 137-144)

```csharp
if (zdo.DataRevision == _lastDataRevision) return;
TryLoadFromZdo();
```

Vanilla pattern, exactly. Receivers will re-decode whenever the owner's ZDO byte array gets replicated.

### 4.4 MP race risks

- Two clients simultaneously brushing the same tile: both call `ClaimOwnership`, last one wins, the other silently drops its op. Acceptable for admin use.
- Client sends a brush RPC for a tile a different client owns: every peer runs `TileBrush.BrushFlat` locally, only the owner's `ApplyOpAndSave` succeeds (others log "ClaimOwnership did not promote us"). The owner's write replicates back via ZDO. **This works for the common case but has no retry** — the brush appears for the owner but the original sender (if it's not the owner) sees the same broadcast result. Functional; minor log spam.

---

## 5. Save/load timing

### 5.1 ZNet.SaveWorld hooks

- `DirtFloorPatches.cs:394` — legacy TerrainPersistence/ClutterPersistence/BiomeFilePersistence saves
- `CustomWaterPatches.cs:759` — water persistence

The TileAnchor system does NOT hook `ZNet.SaveWorld`. Instead, every successful `ApplyOpAndSave` immediately writes the encoded byte array into the ZDO (line 277-306 in TileAnchor.cs). ZNet's normal ZDO flush handles disk persistence — no separate file backup, no batching. **Status: GOOD for the new path** — relies entirely on vanilla ZDO save semantics.

### 5.2 ZNet.Shutdown hooks

- `DirtFloorPatches.cs:418` — legacy cleanup, calls `AsyncShutdownManager.ShutdownAll`, terrain caches, undo manager
- `TileProactiveSpawner.cs:187` — clears in-flight spawn queue
- `ZoneStreamTiming.cs:76` — resets timing telemetry
- `CustomWaterPatches.cs:735` — water teardown

### 5.3 Game.Logout

- `DirtFloorPatches.cs:447` — Resets `_initialized` on TerrainOperationRPC / TerrainPaintRPC / TerrainPersistence / BiomeFilePersistence / ClutterPersistence

The **new** TileBrushRPC (`TileBrushRPC.cs:37`) has `private static bool _registered` but **no Reset** — it relies on the Awake patch to call Register() on the next ZNet.Awake, and `Register()` is idempotent because of the bool guard. On a re-login the ZRoutedRpc.instance is NEW so the OLD `_registered=true` would short-circuit registration of the new instance. **BUG-5.1 (HIGH)** — TileBrushRPC is missing a `ResetInitialized()` / a re-register-on-shutdown pattern. After logout→reconnect, the brush RPC handlers are dead and brush ops become single-client-local only.

(Same pattern bug for TerrainPaintRPC was fixed at `DirtFloorPatches.cs:459` — `TerrainPaintRPC.ResetInitialized()`. TileBrushRPC didn't get the same treatment.)

### 5.4 No file-backup writers in the new path

The new TileAnchor system has zero file IO of its own — all state lives in ZDOs and rides ZNet's normal save cadence. Compare to legacy TerrainPersistence which writes `.terraindata` files under World/. This is a deliberate simplification, **status GOOD**.

---

## 6. Mod dependencies

### 6.1 Declared in csproj

References (all hardcoded HintPaths, not resolved through NuGet):
- `0Harmony.dll` (from BepInEx core, dedi-server path)
- `assembly_guiutils_publicized.dll` (from local libs/)
- `assembly_utils.dll` (pinned to `..\..\assembly_valheim\lib\assembly_utils.dll` — see memory `reference_assembly_utils_pin`)
- `assembly_valheim_publicized.dll` (from the live Steam install's `publicized_assemblies\`; the old `PublicTest` build toggle was removed 2026-09-23, we build against 1.0 only)
- `BepInEx.dll` (from `..\..\..\customspawnsmod\lib\` — fragile path)
- `gui_framework.dll`, `Splatform.dll`, Unity modules (all from `Program Files (x86)\Steam\steamapps\common\Valheim\...`)
- `Newtonsoft.Json` (from packages/)

### 6.2 Declared in plugin attribute

**None.** Open task #12 still pending.

### 6.3 Reflectively-detected mods

- InfinityHammer (`TileHammerCompat.cs:43`)
- WorldEditCommands (legacy `InfinityHammerCompat.cs:62` — but `Initialize()` is never called)

### 6.4 Source-link block

Commented-out in csproj (lines 312-348). Would source-link 8 files from `FiresAdminPrefabs` for clutter replacement. Currently the legacy `Modules/CustomTerrain/Clutter/` is in use and FAP is not coupled.

---

## 7. Config system

### 7.1 What's configurable today (`ConfigManager.cs`)

| Key | Default | Section | Synced |
|---|---|---|---|
| `VerboseLogging` | `false` | General | yes |
| `ServerAuthority` | `true` | General | yes (locking) |
| `HeightCacheResolution` | `4096` | Water | yes |
| `HeightCacheSamplesPerFrame` | `5000` | Water | yes |
| `UseNewTilePersistence` | `false` | Terrain | yes |

Five total. **Drastic shrink** from the original FiresRPGmaker ConfigManager (hundreds of entries).

### 7.2 Hard-coded constants that should be configs

| Constant | File | Line | Suggested config |
|---|---|---|---|
| `LAVA_MAX_DEPTH = 0.6f` | TileBrush.cs | 248 | Terrain.LavaDepressDepth |
| `TILE_SIZE = 64f` | TerrainTile.cs | 24 | Compile-time only (changing breaks save format); leave as const |
| `VERTEX_SPACING = 1f` | TerrainTile.cs | 27 | Same |
| `ANCHOR_DEPTH = -5000f` | TileAnchor.cs | 34 | Could be config but no real reason to expose |
| `RENDER_ACTIVATE_DIST = 600f` | TileMeshRenderer.cs | 74 | Performance.TileRenderDistance |
| `COLLIDER_ACTIVATE_DIST = 92f` | TileMeshRenderer.cs | 76 | Performance.TileColliderDistance |
| `LOD_CHECK_INTERVAL = 0.25f` | TileMeshRenderer.cs | 72 | Performance.TileLODCheckInterval |
| `MAX_COOKS_PER_FRAME = 1` | TileMeshRenderer.cs | 82 | Performance.TileColliderCooksPerFrame |
| `DRAG_INTERVAL = 0.1f` | TileBrushDrag.cs | 32 | Brush.DragRateHz |
| `DRAG_FRESHNESS_SECONDS = 2f` | TileBrushDrag.cs | 37 | Brush.DragFreshnessSec |
| `MAX_SPAWN_PER_FRAME = 4` | TileProactiveSpawner.cs | 43 | Performance.ProactiveTileSpawnPerFrame |
| `MAX_RAYCAST = 600f` | TileBrush.cs | 535 | Brush.MaxRaycastDistance |
| `UNDERSIDE_RENDER_DISTANCE = 120f` | TileMeshUnderside.cs | 56 | Performance.UndersideRenderDistance |
| `UNDERSIDE_COLLIDER_DISTANCE = 40f` | TileMeshUnderside.cs | 57 | Performance.UndersideColliderDistance |

None of these are urgent — they're all sensible defaults — but moving them to ConfigManager would make tuning easier without recompiles.

---

## 8. Logging

### 8.1 FiresLogger (`Utilities/FiresLogger.cs`)

```csharp
public static void LogInfo(string)     → Debug.Log         (always)
public static void LogVerbose(string)  → Debug.Log         (gated on configVerboseLogging)
public static void LogWarning(string)  → Debug.LogWarning  (always)
public static void LogError(string)    → Debug.LogError    (always)
```

Verbose gating works. Several files use `Debug.Log(...)` directly (mostly legacy DirtFloor code) which bypasses the verbose gate.

### 8.2 RateLimitedLogHandler (`Modules/CustomTerrain/Utilities/RateLimitedLogHandler.cs`)

Installed in `FiresAdminTerrain.InstallLogFilter` before any asset load. Throttles / suppresses:
- `Failed to send data k_EResultLimitExceeded` (30s throttle)
- `Failed to find expected binary shader data` (suppressed entirely, with periodic summary)
- `Setting linear/angular velocity of a kinematic body` (suppressed)
- NRE in `ShieldGenerator.OnDestroy` and `ArcheryTarget.Start` (suppressed with periodic summary)

**Status: GOOD.** This is identical to FiresNPCs.

### 8.3 Logs that spam unconditionally

These run at LogInfo (not gated) every time the op fires:
- `TileBrush.BrushFlat/Raise/Paint/Clear/Biome` final-line per op (5 sites in `TileBrush.cs`) — at 10Hz drag × 5 tools, this dumps ~10 lines/sec into BepInEx.log while the user holds mouse.
- `TileAnchor.WriteToZdo` "saved | payload=NNN B | new rev=N" per write — also at 10Hz drag × N tiles per brush.
- `TileAnchor.Awake` + `EnsureTile` info logs every time a tile spawns.

**BUG-8.1 (MED):** Bulk-brush operations produce log floods. Either rate-limit per-second, or downgrade these to Verbose. Compare to `TileBrush.cs:34, 78, 115, 165` which use LogWarning for radius<=0 (good, that's an actual bug) versus the LogInfo at line 66, 104, 142, 197, 334 which fire on every successful op.

---

## 9. Error handling

### 9.1 Try/catch coverage

Solid coverage at: TileAnchorPrefab (every shader/reflection path), TileHammerCompat (IH detection), AsyncShutdownManager, ConfigSync, AdminSyncing, BiomeOverlayPainter reflection caching.

### 9.2 Silent-fail risk sites

- **`AdminSyncing.RPC_AdminStatusSync` (line 84-97)** — `catch { }` swallows everything. If admin status arrives malformed, the player stays non-admin with no log.
- **`FiresAdminTerrain.OnDestroy` Harmony unpatch / Config dispose** (line 234-236) — `catch { }` silently swallow. Acceptable on shutdown.
- **`BiomeOverlayPainter.EnsureReflectionCached`** catches and warns. If reflection fails, the paint commands silently no-op forever (the reflection cache stays half-populated, subsequent calls don't retry).
- **`TileAnchor.TryLoadFromZdo` Decode catch** (line 173) sets `_lastDataRevision = zdo.DataRevision` so corrupt ZDOs don't retry — this is correct. But the corrupt ZDO is then permanently unreadable; no flag to surface "tile (X,Y) has corrupt data" to admin.
- **`TileMeshRenderer.Awake` material catch** (line 183) leaves `_meshRenderer.sharedMaterial = null` → tile renders default magenta. Visible failure mode, OK.

### 9.3 Reflection sites

- TileAnchorPrefab IH SpriteHelper reflection: cached, retried with logging on failure ✓
- TileHammerCompat BaseRuler.Snap reflection: cached, logged on miss ✓
- TileProactiveSpawner ZNetScene.CreateObject + ZDOMan.FindObjects: cached, logged on miss ✓
- BiomeOverlayPainter m_renderMesh / m_cornerBiomes / RebuildRenderMesh: cached, logged on miss ✓
- AdminSyncing m_adminList field: NO try/catch (line 35). If Valheim renames the field this throws unhandled.

---

## 10. Initialization order

```
1. BepInEx loads FiresAdminTerrain (alphabetical: IH and WEC NOT yet loaded)
2. FiresAdminTerrain.Awake
   ├─ InstallLogFilter
   ├─ RootObject + PrefabContainer
   ├─ ConfigManager.Initialize  (binds 5 config entries)
   ├─ ConfigSync construction (+lockedConfigChanged hook, +5 entries)
   ├─ LoadAssets (embedded firesnpcs bundle)
   ├─ if dedi: DelayedDedicatedServerInit (waits for ZNetScene+ObjectDB)
   └─ FinishInit
       ├─ harmony.PatchAll (excluding _clientOnlyPatchTypes on dedi)
       ├─ TerrainPaintRPC.Initialize  (early no-op: ZRoutedRpc.instance might be null)
       ├─ BiomeFilePersistence.Initialize
       └─ BiomeOverlayPainter.RegisterCommands (client only)
3. ... time passes ...
4. ZNet.Awake → TileBrushRPC.Register (via Harmony postfix)
5. ZNet.Awake → AdminSyncing.AdminStatusSyncPatch + WatchAdminListChanges
6. ZNet.Awake → ConfigSync handlers register
7. ZNetScene.Awake → TileAnchorPrefab.Ensure
   ├─ ScrubFakeNulls
   ├─ Clone source prefab → register VATile
   ├─ TileHammerCompat.Initialize (IH IS now loaded, detection works)
   └─ TileBrushDrag.EnsureAttached
8. ZoneSystem.SpawnZone → TileProactiveSpawner scans for VATile ZDOs, drains pending queue
9. Terminal.InitTerminal → TileBrushCommands.Register (Harmony postfix)
```

### 10.1 Races / problems

- **R-10.1 (LOW):** `TerrainPaintRPC.Initialize()` runs at FinishInit which is in plugin Awake. `ZRoutedRpc.instance` is `null` at this point. The code defensively early-returns on `if (ZRoutedRpc.instance == null) return;` — but **then never retries**. There's no equivalent of TileBrushRPC's ZNet.Awake hook to re-trigger registration. Net result: TerrainPaintRPC RPCs are registered in single-player but ONLY because ZRoutedRpc gets instantiated mid-game and then `Initialize()` re-runs from... nowhere. Actually it never re-runs. **BUG-10.1 (HIGH):** TerrainPaintRPC.Initialize is called too early and never re-runs. The Initialize() body has the `if (_initialized) return;` guard so a `ZNet.Awake` postfix that calls it would work. Compare to the working TileBrushRPC pattern.
- **R-10.2 (LOW):** TileAnchorPrefab depends on the bundle being loaded. It runs at ZNetScene.Awake which is well after FiresAdminTerrain.Awake.LoadAssets, so the bundle is reliably available.

---

## 11. Asset bundle inventory

Without runtime AssetStudio inspection of the binary `firesnpcs` bundle, I can only enumerate what the code **loads from it**:

| Asset name | Loader | Use |
|---|---|---|
| `piece_dirtfloor_vad` | TileAnchorPrefab.LoadSourcePrefab | Source prefab cloned to make VATile |
| (water shader / mesh templates) | WaterMaterialManager — needs separate inspection | Water rendering |
| (terrain shader / heightmap material) | TerrainMaterialCache | Tile material fallback |
| (FAP clutter prefabs) | Likely none — clutter system here uses ClutterPersistence which writes ZDO data, not bundle assets | — |

**Suggestion:** add a `va_bundle_list` debug command that dumps `assetBundle.GetAllAssetNames()` — would give a definitive inventory.

---

## 12. Dedi server handling

### 12.1 _clientOnlyPatchTypes set (`FiresAdminTerrain.cs:102-106`)

Only one entry:
- `VerdantsAscent.Terrain.DirtFloorPatches+DirtFloorClientPatches` (the inner class with `Hud.UpdateCrosshair`)

### 12.2 Should be in the skip set but aren't

Reviewed all `[HarmonyPatch(typeof(X))]` annotations for client-only X. Found: nothing else. Other Hud/Minimap/GameCamera references are runtime calls in method bodies, not patch targets, and Harmony tolerates those.

### 12.3 Dedi-only paths that should skip client-only Initialize calls

`FinishInit` runs:
- `TerrainPaintRPC.Initialize()` — server-side OK (RPCs are server-side too)
- `BiomeFilePersistence.Initialize()` — server-side OK
- `BiomeOverlayPainter.RegisterCommands()` — gated by `!isDedicatedServer` ✓

`TileAnchorPrefab.Ensure` runs `TileHammerCompat.Initialize` and `TileBrushDrag.EnsureAttached`. Neither is gated on dedi. On dedi:
- `TileHammerCompat` will not find IH (dedi doesn't load IH); silent no-op.
- `TileBrushDrag` attaches a MonoBehaviour whose Update polls `Player.m_localPlayer` (always null on dedi) → early-returns. Fine.

### 12.4 TileMeshRenderer on dedi (`TileMeshRenderer.cs`)

**BUG-12.1 (MED):** TileMeshRenderer's Awake (line 132-188) allocates two Mesh objects, attempts shader lookups, and tries to acquire a heightmap material — all of which are pointless on a dedi (no rendering). Every TileAnchor spawn triggers a TileMeshRenderer.Awake on dedi. The work doesn't crash but it's wasted CPU and memory on the server.

Suggested fix at TileAnchor.EnsureTile (`TileAnchor.cs:308-331`): skip adding the TileMeshRenderer on dedi. The collider IS needed for player physics queries on dedi though (server-authoritative collision), so the simpler fix is to skip just the visual mesh / material logic in TileMeshRenderer.Awake when `SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null`.

### 12.5 TileMeshUnderside

Same issue — collider needs to be there for raycasts/grounding queries, but the renderer / material isn't useful on dedi.

---

## 13. Files in csproj vs disk

PowerShell Compare-Object run:
- **0 files on disk but missing from csproj**
- **0 files in csproj but missing from disk**

(The source-link block in csproj at lines 312-348 is inside an XML comment so it does not contribute active `<Compile Include>` entries.)

**Status: GOOD.** csproj is in sync.

---

## 14. Legacy DirtFloor decommission status

### 14.1 Files

Legacy DirtFloor files still on disk and compiled into the DLL (counted via Glob):
- `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.{Core,DiskManagement,Grass,HeightOps,Lifecycle,MeshGen,Static,Underside,Utilities}.cs` (9 partial files)
- `Modules/CustomTerrain/Core/DirtFloorController.cs`
- `Modules/CustomTerrain/Core/DirtFloorHeightmap.cs`
- `Modules/CustomTerrain/Core/DirtFloorAsyncMeshBuilder.cs`
- `Modules/CustomTerrain/Core/DirtFloorCombinerCache.cs`
- `Modules/CustomTerrain/Core/DirtFloorSaveManager.cs`
- `Modules/CustomTerrain/Core/DirtFloorTerrainCompStorage.cs`
- `Modules/CustomTerrain/Core/DirtFloorZoneRegistry.cs`
- `Modules/CustomTerrain/Core/TerrainCombinerAnchor.cs`
- `Modules/CustomTerrain/Core/TerrainPersistence.cs`
- `Modules/CustomTerrain/DirtFloorPatches.cs` (1700+ lines, ~30+ Harmony patches)
- `Modules/CustomTerrain/DirtFloorTerrainManager.cs`
- `Modules/CustomTerrain/Paint/DirtFloorMeshCombiner.Paint.cs`
- `Modules/CustomTerrain/Paint/DirtFloorPaintMaskStreaming.cs`
- `Modules/CustomTerrain/Tools/DirtFloorPlacementTool.cs`
- `Modules/CustomTerrain/Tools/InfinityHammerCompat.cs` (LEGACY — `Initialize()` never called)
- `Modules/CustomTerrain/Tools/TerrainCommandParser.cs`
- `Modules/CustomTerrain/Tools/TerrainOperationExecutor.cs`
- `Modules/CustomTerrain/Tools/TerrainOperationParams.cs`
- `Modules/CustomTerrain/Tools/TerrainUndoManager.cs` (called from `TileBrushCommands.Cmd_Undo/Redo` — actually USED)
- `Modules/CustomTerrain/Utilities/DirtFloorDeferredInit.cs`
- `Modules/CustomTerrain/Utilities/DirtFloorLODManager.cs`

### 14.2 Active vs dormant

- **ACTIVE (Harmony patches still apply, code still runs):**
  - `DirtFloorPatches.cs` — every `[HarmonyPatch]` inside is auto-applied via `harmony.PatchAll`. ZNet.SaveWorld, ZNet.Shutdown, Game.Logout postfixes all fire. Character.UnderWorldCheck, Heightmap.GetHeight, etc. — these are patches that affect vanilla terrain behavior, currently active.
  - `DirtFloorCombinerCache.SaveCache()` runs on every ZNet.Shutdown (DirtFloorPatches.cs:426)
  - `TerrainPersistence.SaveTerrainData()` / `ClutterPersistence.SaveClutterData()` / `BiomeFilePersistence.SaveBiomeData()` run on every ZNet.SaveWorld (DirtFloorPatches.cs:401-407)
  - `TerrainUndoManager.ClearHistory` runs on logout
- **DORMANT (compiled, referenced by active code, but no entry-point fires the actual op today):**
  - `DirtFloorTerrainManager` — the `HandleTerrainOperation` and `HandleTerrainCommand` entry points are not invoked by anything in the codebase (verified via grep — no callers exist). The class itself is referenced for its `MIN_DISK_CREATION_RADIUS` constant and as a holder for now-orphaned helpers.
  - `DirtFloorPlacementTool` — entry point not wired to YAML / hammer; the only "place a custom terrain disk" path today is via the new TileBrush.
  - Legacy `InfinityHammerCompat.cs` — `Initialize()` is dead code; the live ruler patch is in the new `TileHammerCompat.cs`.
- **DEAD STATIC HELPERS but called indirectly via DirtFloorPatches:**
  - `DirtFloorMeshCombiner.GetCombinerAt(pos)` — still called from `EnvironmentBoxPatches.cs:952`, `BiomePaintPersistence.cs:165,280`, `DirtFloorTerrainManager` internal methods, `TerrainAPI.cs:204`. If a player placed an EnvironmentBox or painted vanilla biome paint that ran the legacy clutter rebuild, those would resolve to an old combiner if one existed.

### 14.3 Conflicts between legacy and new

- **CONFLICT-14.1 (HIGH):** Both systems persist data on `ZNet.SaveWorld`. The new TileAnchor system writes via vanilla ZDO replication automatically; the legacy DirtFloorPatches.cs:394-413 hook ALSO runs and writes the `.terraindata` files. Until Phase 8e deletes the legacy files, **both data formats are being saved every world save**. If a user toggles `UseNewTilePersistence=true` and brushes a tile, the new TileAnchor ZDO is written. If they then toggle back to `false`, the legacy system has no record of the tile and the new ZDO is also no longer being looked at by anything → terrain effectively disappears on next reload (only the visible mesh remains because the tile's anchor still spawns from its ZDO).
- **CONFLICT-14.2 (MED):** `Modules/CustomTerrain/Tools/InfinityHammerCompat.cs` (legacy) and `Modules/CustomTerrain/Persistence/TileHammerCompat.cs` (new) both have logic to patch `ZoneSystem.GetGroundHeight` and `InfinityTools.BaseRuler.Snap`. Only the new one is actually applied (because legacy `Initialize()` is never called). If anyone accidentally re-wires the legacy Initialize, double-patches will fight each other.
- **CONFLICT-14.3 (LOW):** `TerrainOperationRPC` has RPC handlers for biome paint that fire `BiomeOverlayPainter.PaintOverlay`. The new `TileBrushCommands.Cmd_Biome` routes biome ops via cursor-target classification — could end up calling `BiomeOverlayPainter.PaintOverlay` directly for vanilla terrain, AND the legacy `BiomePaintPersistence.RPC_HandleBiomePaint` could also fire `BiomeOverlayPainter.PaintOverlay`. Possible double-paint.

### 14.4 Decommission status

Per HANDOFF.md and Task #67 ("Delete legacy DirtFloor system (Phase 8e)") which is still **pending**:
- Phase 8d (entry-point branching) is documented as "in progress" but only the TilePersistence facade + UseNewTilePersistence toggle exist. The legacy disk-placement entry points (DirtFloorController.cs:470, DirtFloorMeshCombiner.HeightOps.cs MarkDirty sites) are NOT yet wrapped in `if (TilePersistence.IsEnabled) return;` early-returns.
- Phase 8e (file deletion) has not started.

**Net:** the legacy DirtFloor system is in a half-deprecated state — its patches still apply on every save/load, its data files still write, but no active brush tool drives data **into** it. New brushes route exclusively through TileAnchor. The legacy system is effectively a dormant zombie writing empty `.terraindata` files on every save.

---

## TOP 10 CROSS-CUTTING BUGS / GAPS (severity-ordered)

1. **BUG-3.1 (HIGH)** — `TileBrushRPC.Recv_*` handlers have no admin guard. Any non-admin client can permanently brush terrain across the whole world via RPC, including biome flips and lava depressions.
2. **BUG-5.1 (HIGH)** — `TileBrushRPC` lacks `ResetInitialized()`; after a logout→reconnect cycle the brush RPC handlers never re-register and brushes go single-client-local silently.
3. **BUG-10.1 (HIGH)** — `TerrainPaintRPC.Initialize` runs once at plugin Awake before `ZRoutedRpc.instance` exists, early-returns, and never retries. Its biome/clutter/paint RPCs are never registered for MP sync. Compare to TileBrushRPC's working ZNet.Awake hook.
4. **BUG-2.1 / BUG-2.3 (HIGH)** — Tool name mismatch: YAML has `Floor (10m)/(25m)/(50m)`, but both `TileHammerCompat.s_ourToolNames` and `TileBrushDrag.IsOurBrushToolSelected` match only bare `Floor`. Ruler-ring lift fails on virgin ground AND click-and-drag silently doesn't fire for any floor tool.
5. **CONFLICT-14.1 (HIGH)** — Legacy DirtFloor save/load hooks remain active. Phase 8e deletion not started; the legacy `.terraindata` files write every save and the legacy biome/clutter persistence runs every load. Toggling `UseNewTilePersistence` off-then-on can lose new-path data because the toggle has no migration path.
6. **No BepInDependency declared (HIGH)** — Open task #12. Hard-dependency on Infinity Hammer / Expand World Data / WEC is documented in plans but not declared. Players who install FAT without IH get tools that "kind of work" but don't appear in any menu.
7. **BUG-12.1 (MED)** — TileMeshRenderer + TileMeshUnderside Awake on dedi servers, allocating Mesh objects and attempting shader lookups that can't succeed. Memory and CPU waste at scale (megabase = thousands of TileAnchors = thousands of pointless Mesh allocations).
8. **BUG-8.1 (MED)** — `TileBrush.Brush*` and `TileAnchor.WriteToZdo` log at LogInfo on every successful op. At 10Hz drag with 4 overlapping tiles this is ~80 log lines/sec while painting. Should be Verbose or rate-limited.
9. **R-Drag-Raise (MED)** — TileBrushDrag re-fires the Raise delta at 10Hz, so holding the mouse on a +1m brush silently stacks to +10m in 1s. Either lock initial delta and ignore subsequent ticks, or document this clearly.
10. **CONFIG-Lock event no-op (LOW)** — `ConfigManager.OnConfigLockChanged` triggers a `ConfigLockChanged` event nobody subscribes to. `AdminSyncing.RPC_AdminStatusSync` tries to reflectively call `FiresAdminTerrain.OnConfigLockChanged` which doesn't exist on the plugin. Lock-change callbacks silently no-op.

---

## Document trail

- See `Docs/HANDOFF.md` for phase planning and remaining 8d/8e/8f work.
- See `Docs/TERRAIN_PERSISTENCE_REDESIGN_PLAN.md` for the new tile-anchor design rationale.
- See `Docs/CLUTTER_REPLACEMENT_PLAN.md` for the deferred FAP source-link plan.
- See `Docs/TERRAIN_WATER_PERSISTENCE_BUGFIX_TRACKER.md` for the original bug-class analysis from FiresNPCs era.
