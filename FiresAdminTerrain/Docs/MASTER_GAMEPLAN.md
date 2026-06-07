# FiresAdminTerrain — Master Implementation Gameplan

**Drafted 2026-05-25** after a full cross-mod audit of FiresNPCs (RPG-maker) and the new FiresAdminTerrain (FAT). Replaces the partial scope of `CENTRALIZED_LIFECYCLE_PLAN.md` — that work is now Phase B here.

Source audits (all in `Docs/`):
- `TERRAIN_PARITY_AUDIT.md` — CustomTerrain feature parity, RPG-maker → FAT
- `WATER_PARITY_AUDIT.md` — CustomWater feature parity
- `INFRA_AUDIT.md` — cross-cutting (IH ruler, MP, server-auth, lifecycle, init order)
- `RPGMAKER_DESTROY_AUDIT.md` + `FAT_DESTROY_AUDIT.md` + `WATER_LIFECYCLE_AUDIT.md` — destroy/clear surface

---

## 0. Executive summary

FAT today is two terrain stacks coexisting in one DLL:
- **Legacy `DirtFloor*` stack** — ~47.5K lines, byte-identical to RPG-maker, Harmony auto-init still alive, writes `.terraindata` / `.clutterdata` / `.biomedata` files every save cycle.
- **New tile-anchor stack** — ~10.5K lines under `Modules/CustomTerrain/Persistence/`, ZDO-anchored at Y=−5000, per-vertex grids, this is the future.

They write to **disjoint ZDO key spaces** (`floor_paint_v3` vs `va_tile_v`), reached through different entry points, and the `TilePersistence.IsEnabled` toggle wired to bridge them is connected to zero legacy call sites — flipping it does nothing.

Three categories of problem:
1. **Broken right now** — drag stacks +10m/sec, Floor doesn't lift over virgin ground, MP biome sync is dead because RPC inits before ZRoutedRpc, custom-tile delete leaves biome/clutter ghosts, RPC handlers lack admin guards.
2. **Architecturally wrong** — destruction is scattered across 14 paths (RPG-maker's pattern we shouldn't inherit), anchors get destroyed on empty (user wants them persistent like vanilla TerrainComp), water has the identical anchor-leak bug.
3. **Not yet built** — per-tile paint mask texture (Paint Dirt/Cult/Paved doesn't render), biome stitching across tile borders, lava water auto-spawn, custom clutter (FAP source-link commented out), snow commands not registered, IH YAML exposure for biome/delete.

This plan delivers seven phases (A–G), each one PR-sized, each verifiable in isolation, each leaving the mod shippable.

---

## 1. Phase plan at a glance

| Phase | Theme | Risk | Touches | Verify |
|-------|-------|------|---------|--------|
| **A** | Stop the bleeding | Low | 6 files | Manual: drag a brush, look at log, walk on virgin ground |
| **B** | Centralized lifecycle (scrubber + anchor-per-zone) | Med | 12 files (8 new) | Round-trip: paint → clear → repaint, no ghosts |
| **C** | MP + security hardening | Med | 8 files | Two-client session, non-admin can't grief |
| **D** | Visual completeness | High | 15 files (5 new) | Eye test — paint, biome, clutter, snow all render |
| **E** | Water consolidation | Low | 4 files | RPC rename, anchor through scrubber |
| **F** | Legacy decommission | Med | -24 files (delete) | Old worlds still load, new code is sole owner |
| **G** | Polish & ongoing | Low | Various | Configs, perf tuning, two-client smoke |

Phases A–C are the must-ship slice. D adds the missing visual layers. E–F clean up. G is ongoing.

---

## 2. Phase A — Stop the bleeding (1–2 days)

Six things actively breaking play sessions today, each a small focused fix.

### A1. Fix tool-name mismatch (Floor doesn't lift)
- `infinity_tools.yaml` declares `Floor (10m)/(25m)/(50m)`.
- `TileHammerCompat.s_ourToolNames` + `TileBrushDrag.IsOurBrushToolSelected` switch both expect bare `"Floor"`.
- Effect: ruler doesn't lift over virgin ground, click-and-drag silently fails for any sized Floor tool.
- Fix: add the YAML names to both sets. Or rename YAML tools to `Floor`. Pick one and align.

### A2. Drag-Raise stacking bug
- `TileBrushDrag` re-fires the cached `_height` (delta) at 10Hz. Holding mouse over a Raise tool stacks 10× the delta per second → unintended +10m brushes.
- Fix: scale the delta by `DRAG_INTERVAL / 0.5f` so per-tick delta equals what one click would've done over 500ms, or convert Raise to a single-shot per click (no drag).

### A3. Log-spam throttle
- `TileBrush.BrushFlat / Raise / Paint / Clear / Biome` all `FiresLogger.LogInfo` per call. Drag fires 10/sec → 50+ lines/sec.
- Fix: demote to `LogVerbose` (already exists in FiresLogger). Keep Info for first-shot only via a static throttle.

### A4. TerrainPaintRPC.Initialize timing
- Initialize runs in `FinishInit()` BEFORE `ZRoutedRpc.instance` exists on first session. It early-returns silently. Biome/clutter MP sync is dead until something else later triggers registration (nothing does).
- Fix: move Initialize onto a Harmony postfix of `ZRoutedRpc.Awake` (mirror what `TileBrushRPC` already does). Add `ResetInitialized` so logout/reconnect re-registers.

### A5. Underside / TileMeshRenderer on dedi
- Both `Awake` allocate Mesh objects + run shader lookups on dedi where there's no rendering.
- Fix: in `Awake`, early-return when `SystemInfo.graphicsDeviceType == Null`. Server only needs the collider mesh for raycasts — handle that minimally.

### A6. Lava double-paint regression (already in flight)
- My recent `Cmd_Biome` AshLands branch fires both custom-tile RPC AND `BiomeOverlayPainter.PaintOverlay`. Remove the vanilla call. Single path: custom tile only (which spawns tiles to host depression). This becomes irrelevant once Phase B's scope enum lands, but the bleed needs to stop now.

**Verify:** drag Flat over flat ground (no stacking, no spam), Floor lifts ruler over virgin ground, biome paint syncs to a second client, dedi server starts clean.

---

## 3. Phase B — Centralized lifecycle (4–6 days)

The work originally drafted in `CENTRALIZED_LIFECYCLE_PLAN.md`, refined here. **Ruler integration is now a first-class concern of the scrubber API.**

### B1. `RegionScrubber` API (new `Modules/CustomTerrain/Lifecycle/RegionScrubber.cs`)

```csharp
public static class RegionScrubber
{
    [Flags] public enum Scope
    {
        TileHeights = 1, TilePaint = 2, TileBiome = 4,
        VanillaBiome = 8, VanillaPaint = 16,
        Clutter = 32, VanillaGrass = 64,
        Vegetation = 128, Pieces = 256,

        TileOnly                 = TileHeights | TilePaint | TileBiome,
        VisualOnly               = TileOnly | VanillaBiome | Clutter | VanillaGrass,
        EverythingNoBuild        = VisualOnly | VanillaPaint | Vegetation,
        EverythingIncludingBuild = EverythingNoBuild | Pieces,
    }

    public enum Source { Console, BrushClear, BrushDelete, WipeLoaded, WipeAll, VanillaTerrainOp, Undo, Rpc }

    // Plain world-space scrub.
    public static int ScrubRegion(Vector3 center, float radius, Scope scope, Source source, bool broadcastRpc = true);

    // Ruler-aware scrub — pulls center, radius, and Y offset from the
    // currently-rendered IH ruler instead of taking explicit args. Used
    // when the user clicks a brush/delete tool with a sized ruler active.
    public static int ScrubFromRuler(Scope scope, Source source, bool broadcastRpc = true);
}
```

Anatomy of one call (and what makes it the SINGLE source of truth):
1. Walk `TileAnchorRegistry.TilesOverlappingCircle(center, radius)`. For each anchor → `anchor.ClearData(center, radius, scope)`. **Anchor stays alive, only per-vertex data clears.**
2. If `VanillaBiome` in scope → ONE call to a new `BiomePaintPersistence.RemoveOverlapping(center, radius)` (this method does not exist today — it's the missing piece behind the ghost-overlay bug).
3. If `VanillaPaint` in scope → ONE call to `ZonePaintManager.ClearInRadius` (existing).
4. If `Clutter` in scope → ONE call to `ClutterManager.ClearInRadius`.
5. If `VanillaGrass` in scope → ONE call to `ClutterSystem.instance.ResetGrass`.
6. If `Vegetation` / `Pieces` in scope → walk `ZDOMan` once with the matching filter.
7. If `broadcastRpc` and `ZNet.instance != null` → ONE RPC fanout (`Everybody`) with `(center, radius, scope)`. Receivers call back into `ScrubRegion(..., source: Rpc, broadcastRpc: false)`.

Every scrub call pushes a `TileUndoLog` entry covering everything it touched, so undo works for free.

### B2. Ruler integration spec

`RegionScrubber.ScrubFromRuler` reads the IH ruler state via the same path `TileBrushCommands` already uses (`from=`, `circle=`, `<h>`). Brush op commands that wrap scrub (`va_brush_clear`, `va_brush_delete`, etc.) pass IH args straight through. Console commands without IH context fall through to camera-raycast + default radius.

Hard rule: **the cursor/raycast Y is only the fallback.** When the IH ruler is active and the user has Page Up/Down'd to a custom height, the scrubber operates at that height — same as Flat/Raise already do for height ops. This means deleting a sky-build tile from inside it works, and scrubbing a depressed lava pool from outside it works.

### B3. `TileAnchor.ClearData(center, radius, scope)`

Per-anchor inner loop. Iterates verts overlapping the circle, zeroes the flags indicated by `scope`:
- `TileHeights` → `HeightModified[idx] = false; Heights[idx] = 0`
- `TilePaint`   → `PaintModified[idx] = false; Paint[idx] = default`
- `TileBiome`   → `BiomeModified[idx] = false; Biome[idx] = 0`

Then calls `WriteToZdo` + `NotifyMutated` ONCE per anchor regardless of how many verts changed. No-op-skip when zero verts change (preserves the existing optimization).

### B4. Anchor-per-zone, never destroy

- `ZoneSystem.SpawnZone` postfix → `TileAnchorRegistry.EnsureAnchor(zoneKey)` spawns an empty VATile ZDO if none exists for that zone.
- `TileAnchor.DestroyPermanently` becomes `internal` and is only called from `OnDestroy` when the zone itself unloads (cleanup, NOT data clear).
- `va_terrain_wipe_loaded` → `for each loaded anchor: anchor.ClearData(scope: EverythingNoBuild)`. Anchor stays.
- `va_terrain_wipe_all` → existing zone-poke logic + per-anchor `ClearData`. Anchor stays.
- `va_brush_delete` → `ScrubRegion(scope: EverythingNoBuild)`. Anchor stays.

**This single change eliminates the ghost-overlay bug AND the orphan-ZDO key class:** there's nothing to resurrect because nothing was destroyed; the byte arrays we cleared stay cleared because the same ZDO holds them.

### B5. Route every entry point through the scrubber

- `TileBrush.BrushClear` → thin shim: `ScrubRegion(scope: TileOnly | VanillaBiome | Clutter | VanillaGrass)`.
- `TileBrushCommands.Cmd_BrushClear` / `Cmd_BrushDelete` → call `ScrubFromRuler`.
- `TileDeleteOps.TryDestroyAnchor` → becomes `internal`, used only by `OnDestroy`.
- Vanilla `TerrainOp.Apply` postfix (new) → on paint-with-vegetation-clear (dirt/cult/paved), call `ScrubRegion(scope: VanillaBiome | Clutter | VanillaGrass)`. NO tile clear from vanilla paint — user picked the vanilla tool, they're modifying vanilla.
- Lava biome paint → no longer needs the special-case dual-paint logic; `ScrubRegion(scope: TileOnly)` happens before the depression write because biome change pushes through normally.

### B6. `BiomePaintPersistence.RemoveOverlapping(center, radius)` (NEW METHOD)

The function that doesn't exist today and is behind every ghost-overlay bug. Walks `_activePaints`, removes entries whose `(X, Z)` falls within `radius` of `(center.x, center.z)`. Calls `ForceHeightmapRegeneration(center, radius)` to repaint vertex colors on affected Heightmaps. Persists the trimmed list via the same `SaveBiomePaintsToTerrainComp` path the painter uses.

### B7. `RegionScrubberRPC` (new)

Mirror of `TileBrushRPC.cs`. Single `RPC_SCRUB` channel with `(center, radius, scope)` payload. Idempotent — receivers running the same scrub locally is fine because `ClearData` is idempotent.

**Verify:** paint biome on custom + vanilla → clear → repaint vanilla biome under the cleared spot, ghost is GONE; wipe a tile, anchor still exists in `va_terrain_count`; vanilla hoe paint dirt over a lava-painted area, lava overlay clears.

---

## 4. Phase C — MP & security hardening (2–3 days)

### C1. Admin guards on all RPC handlers
- `TileBrushRPC.Recv_Flat / Raise / Paint / Clear / Biome` lack any sender check. Any non-admin can broadcast world-altering brush ops.
- `TerrainOperationRPC` / `TerrainPaintRPC` similarly unguarded.
- Fix: every handler checks `AdminSyncing.IsAdmin(sender)` (or `configServerAuthority` policy) before applying. Reject with one log line if denied.

### C2. ResetInitialized on logout
- `TileBrushRPC._registered` is a static bool; surviving logout means re-login skips re-registration, MP sync silently breaks.
- Fix: subscribe to `ZNet.OnDestroy` or `Game.OnLogout` to reset flags. Same pattern for `TerrainPaintRPC` once A4 lands.

### C3. Wire `ConfigServerAuthority` properly
- Today the config exists but isn't checked at the RPC handler layer.
- Fix: handlers gate write-side behavior. Clients send a *request*; server applies and broadcasts the actual scrub/op. Mirror `AutoWaterPatches` pattern (only module doing this correctly).

### C4. `[BepInDependency]` declarations
- Plugin attribute carries NO dependencies. IH/EW/WEC are reflectively detected.
- Fix: add soft dependencies on the GUIDs we care about so r2modman shows proper conflict info.

### C5. ConfigLockChanged event
- `ConfigManager.OnConfigLockChanged` triggers an event with NO subscribers.
- `AdminSyncing` reflectively calls a `FiresAdminTerrain.OnConfigLockChanged` that doesn't exist.
- Fix: actually subscribe and act on it (re-evaluate cached admin state, etc.), or rip the dead wiring.

**Verify:** two-client session, non-admin client can't fire brush ops; admin can; logout/reconnect doesn't break MP biome sync.

---

## 5. Phase D — Visual completeness (3–5 days)

The "looks right" layer. This is what the user actually sees.

### D1. Per-tile paint mask texture (THE big one)
- Today `TileMeshRenderer.RebuildMesh` writes biome colors to vertex `_colors` and explicitly ignores `Tile.Paint[]`. Paint Dirt/Cultivated/Paved data IS persisted, just doesn't render.
- Implementation: per-tile `Texture2D` (65×65 RGBA32) materialized lazily on first paint, bound via `MaterialPropertyBlock` to per-tile material instance. Already documented in Task #73 ("per-tile material instances for biome stitching") — this and D2 share the same instance-material refactor.

### D2. Biome stitching across tile borders
- Shared `Custom/Heightmap` material across all tiles can't carry per-tile blend masks → hard edges at 64m seams.
- Implementation: per-tile material instance (cheap — clone of the cached template), per-tile `_BlendMask` populated from adjacent-tile biome data for the border verts. Render order matters: tile A renders its half of the seam, tile B blends to A's edge values.

### D3. Lava water auto-spawn on AshLands paint
- Currently AshLands paint depresses terrain but doesn't spawn lava water in the depression. Per the design gap inherited from RPG-maker.
- Implementation: after `ScrubRegion(scope: TileOnly) + BrushBiome(AshLands)`, fire a `WaterAPI.CreateLavaDisk(center, radius)` call (need to expose this). Disk Y = depression Y + surface offset (already -0.5m for lava in `GetLiquidTerrainOffset`).

### D4. Edge stitching for biome overlays on vanilla
- AshLands overlay paints with hard radius edge; transitions to surrounding biome look harsh.
- Implementation: in `BiomePaintPersistence`-style apply path, ramp vertex color over the outer 20% of radius (linear blend with the natural biome color at each vertex).

### D5. Custom clutter (FAP source-link)
- `VAClutterManager.EnsureRegistered()` is empty. FAP source-link is commented out in csproj.
- Implementation: uncomment the FAP `<Compile Include><Link>` entries (Task #7 in the existing list), wire `EnsureRegistered` to add bundle clutter prefabs into `ClutterSystem.m_clutter`.

### D6. Snow commands registered
- `SnowCommands.RegisterCommands` defined but never called.
- Fix: call from `FinishInit` (client-side only).

### D7. BrushBiome resets grass
- Per-vertex biome write doesn't call `ClutterSystem.ResetGrass`, so painted Plains-on-Meadows still shows meadows grass.
- Fix: after `ApplyBiomeToTile` returns >0 verts, call `ClutterSystem.instance.ResetGrass(center, radius)`. Then re-spawn target-biome clutter via `ClutterManager.SpawnInRadius(center, radius, targetBiome)` once D5 lands.

### D8. IH YAML exposure
- Biome tools listed in `s_ourToolNames` but missing from `infinity_tools.yaml` (Meadows, BlackForest, Plains, Swamp, Mountain, AshLands, DeepNorth, Mistlands, Clear Biome).
- Delete + wipe also have no YAML entries.
- Fix: add them. One YAML edit.

**Verify:** paint dirt on a tile, it renders dirt; paint biome across a tile seam, no hard edge; paint AshLands, lava water pools in the depression; `/snow_*` commands work.

---

## 6. Phase E — Custom water consolidation (2–3 days)

### E1. RPC name rename
- All 13 water RPC names still `"FiresRPGmaker_Water_*"` / `"FiresRPGmaker_AutoWater_*"`.
- Fix: rename to `"FiresAdminTerrain_Water_*"`. Document the breaking-change implication — saved worlds with in-flight water RPCs (rare) will see them as unknown channels and drop them harmlessly.

### E2. Water through `RegionScrubber`
- `WaterMeshCombinerAnchor` implements a new `IRegionAnchor` interface alongside `TileAnchor`. Scrubber walks both registries when `WaterCells`/`WaterDisks` flags are set in scope.
- Kill the auto-destroy in `RemoveWaterDisk` + `MopWaterAt`. Water clear routes through scrubber. Anchor stays alive empty.
- Long-term: move water from per-disk-cluster to per-zone anchor with segment children (current v3 segment infra already proves this works).

### E3. Symmetric water Initialize
- Today water init is implicit via Harmony patches on `ZNet.Start` / `ZoneSystem.Start`. Inconsistent with how terrain explicitly calls `TerrainPaintRPC.Initialize` etc. in `FinishInit`.
- Fix: explicit `WaterOperationRPC.Initialize()` + `WaterPersistence.Initialize()` calls from `FinishInit`. Idempotent in case the patches also fire.

### E4. Auto-spawn lava on biome paint
- See D3 — water provides the API; terrain calls it.

**Verify:** water tools still work; paint-mop-paint doesn't leak ZDOs; lava auto-spawns on AshLands paint; RPC name doesn't collide with the old RPG-maker mod if both are loaded.

---

## 7. Phase F — Legacy DirtFloor decommission (1–2 days)

The original Phase 8e. With B in place, this becomes safe.

### F1. Migration path
- `ZoneSystem.SpawnZone` postfix (now spawning `TileAnchor` proactively) ALSO checks for legacy `floor_paint_v3` anchors in the same zone. If found, log + leave alone. Migration is opt-in via a `va_migrate_zone` console command that reads legacy data + writes equivalent tile data + destroys the legacy anchor.
- Document the migration command in HANDOFF.md. Run it manually per zone for a test world before bulk migration.

### F2. Delete the files
- Per HANDOFF.md task #67 / #29. Targets:
  - `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.*.cs` (10 partials)
  - `Modules/CustomTerrain/Core/DirtFloorController.cs`
  - `Modules/CustomTerrain/Core/DirtFloorPlacementTool.cs`
  - `Modules/CustomTerrain/Core/DirtFloorTerrainManager.cs`
  - `Modules/CustomTerrain/Core/DirtFloorZoneRegistry.cs`
  - `Modules/CustomTerrain/Core/DirtFloorCombinerCache.cs`
  - `Modules/CustomTerrain/DirtFloorPatches.cs` (1700 lines of legacy Harmony patches)
  - `Modules/CustomTerrain/Paint/DirtFloorMeshCombiner.Paint.cs` (~2000 lines, partial of the combiner)
  - `Modules/CustomTerrain/Tools/InfinityHammerCompat.cs` (dead — never initialized)
  - Plus anything else `DirtFloor*`-prefixed.
- Update csproj `<Compile Include>` entries.
- Keep `TerrainUndoManager` — it's actively used by `TileBrushCommands.Cmd_Undo/Redo`.

### F3. Remove legacy file-cache writes
- DirtFloor stack writes `.terraindata` / `.clutterdata` / `.biomedata` files every save cycle. After F2, these writes vanish. Confirm by tailing the world folder during a save.

**Verify:** clean save folder, all brush ops still work, no lingering `DirtFloor` references in build output.

---

## 8. Phase G — Polish & ongoing

Lower-priority items that can ship anytime after A–F.

- **G1. Config-ify hardcoded constants** — `LAVA_MAX_DEPTH`, `COLLIDER_ACTIVATE_DIST`, `RENDER_ACTIVATE_DIST`, drag intervals, etc.
- **G2. Tile collider visible to `Heightmap.GetHeight`** — currently WaterVolume / EWP triggers / Heightmap.GetHeight see vanilla heightmap below sky-built tiles. Hook `Heightmap.GetHeight` to also sample our tile mesh when standing on one. Same fix pattern as the existing `ZoneSystem.GetGroundHeight` patches.
- **G3. Disk → tile height application** — HANDOFF.md's documented next-step blocker for Phase 8d-2. Port `DirtFloorMeshCombiner.HeightOps.cs` disk-blend logic into `TilePersistence.ApplyDiskToTile`.
- **G4. Two-client smoke test** — Task #30 has been pending forever. Block Phase F2 (legacy delete) on passing this.
- **G5. Vanilla TerrainOp postfix → tile collider** — when player uses vanilla hoe over a custom tile, the vanilla op currently no-ops (TileTerrainOpBlocker). Smooth in particular should route to BrushClear; raise/lower should route to BrushRaise. Currently smooth → BrushClear (DESTRUCTIVE — bug). Fix: smooth = a smooth-vertex op on our grid, not a clear.
- **G6. Dedi-skip more components** — TileMeshUnderside fully on dedi (no rendering, no collider needed except for raycasts which dedi doesn't do).
- **G7. RPC payload versioning** — RPC packages currently have no version byte. Schema changes break MP. Add a 1-byte version header to every channel.

---

## 9. Deferred (won't do unless asked)

- **Per-tile mesh combiner** (RPG-maker pattern) — the tile-anchor system explicitly chose to render per-tile. Combining into multi-tile meshes is a perf optimization for megabuilds and not on the path.
- **Cave system port** — RPG-maker has `TerrainCaveEnvironment`. Cave-as-feature is out of scope for FAT unless the user calls it back in.
- **Companion / NPC / Quest / Guild / Discord / Wayshrine / Territory** — explicitly excluded from FAT per the plugin's own doc comment.
- **TerrainOp-based vanilla heightmap modification for lava** — anchor-per-zone makes this unnecessary; custom tile depression covers the painted area cleanly.

---

## 10. Quality gates between phases

Before merging each phase:
1. **Build clean** — 0 errors, warnings allowed only for pre-existing unused-field cases.
2. **Smoke test** — load into a SP world, run the phase's verify steps from §2–§8.
3. **Two-client check** (Phase C onward) — paint on host, verify on client and vice versa.
4. **`va_terrain_count`** before/after the phase — anchor count should be stable or grow (never spuriously shrink).
5. **Save folder diff** — no new orphan files.

---

## 11. Open questions for the user (please answer before B starts)

1. **Anchor-per-zone spawn timing.** Spawn proactively in EVERY `ZoneSystem.SpawnZone` (millions of anchors world-wide), or lazily on first brush touch in the zone (anchor only where the player builds)? Proactive matches vanilla TerrainComp. Lazy is much lighter.
2. **Scrubber broadcast policy.** Should every scrub from a non-admin client be REJECTED (security) or REQUEST-PATTERN'd (client asks, server decides)? Vanilla pattern is request. Recommend request.
3. **Migration ergonomics.** Run-once auto-migrate at world load, or explicit per-zone console command? Auto is fewer player steps; explicit is safer for the rollout.
4. **Lava water auto-spawn (D3).** Does the user want this ALWAYS or only when painted on existing AshLands-biome world cells? Always = simpler; gated = matches "natural lava" intuition.
5. **Phase ordering preference.** A→B→C as listed, or interleave (e.g. A+C bleed-fix together first, then B)?

---

## 12. What I'll do after you approve

1. Mark Phase A items as in-progress tasks.
2. Land A1–A6 in one focused batch (small files, low risk, fast).
3. Stop. Ask you to live-test A.
4. Then proceed to B per the same pattern — each numbered sub-item is one Edit/Build cycle.

No more single-bug patching while this plan is awaiting feedback. The remaining `[pending]` tasks (#81–84, #75) all fold into the phases above; they'll get marked done or merged into the relevant phase task as the work lands.
