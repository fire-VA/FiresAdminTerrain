# Centralized Terrain Lifecycle — Gameplan

**Why this doc exists.** Three audits commissioned 2026-05-25 show our destroy/clear surface is fragmented in the same way RPG-maker's is, and we're starting to bolt on point-fix patches that contradict each other. This plan defines ONE lifecycle API every entry point routes through — terrain today, water tomorrow.

Source audits:
- `Docs/RPGMAKER_DESTROY_AUDIT.md` — what the old mod actually does (and why it's brittle)
- `Docs/FAT_DESTROY_AUDIT.md` — what we have today + gaps vs RPG-maker
- `Docs/WATER_LIFECYCLE_AUDIT.md` — water's anchor pattern + auto-destroy hazard

---

## 1. State of play

**RPG-maker destruction is a 14-path tangle.** `ClutterManager.ClearInRadius` gets called *three times* inside a single `RemoveTerrainInRadiusInternal`. `DestroyPermanently` zeroes the height/paint/bounds ZDO keys but leaves `va_clutter_data`, `va_biome_data`, and `va_zpaint_bundle` orphaned in the save. Persistence consistency is achieved by redundant saves rather than centralization. We do NOT want to inherit this.

**FAT is partially centralized.** `TileBrush.BrushClear → ApplyClearToTile` is one funnel for per-vertex clears, and `TileDeleteOps.TryDestroyAnchor` is one funnel for hard deletes. BUT — neither calls `BiomeOverlayPainter.ResetBiomeOverlay`, neither calls `ClutterManager.ClearInRadius`, and `va_terrain_wipe_all` flushes VATile ZDOs while leaving `va_biome_paints` / `va_clutter_paints` on TerrainComp ZDOs untouched. **This is exactly the ghost-overlay bug the user is reporting:** the tile dies, the vanilla overlay ZDO survives, re-paint resurrects the ghost.

**Anchor lifecycle is wrong by user spec.** Today `va_brush_delete` / `wipe_*` call `ZNetScene.Destroy(anchor.gameObject)` — the anchor + its ZDO vanish. The user wants `TerrainComp`-style: ONE anchor per zone, spawned proactively, persistent, never destroyed; data is what gets cleared.

**Water is in the same boat.** `WaterMeshCombiner.RemoveWaterDisk` and `MopWaterAt` auto-destroy the anchor the moment the last cell is gone. Paint-mop-paint leaks a fresh ZDO each cycle. The fix is structurally identical to terrain's.

---

## 2. Target architecture

### 2.1 The single scrubber

ONE static class. Every entry point (console command, brush op, RPC handler, vanilla TerrainOp interceptor, undo, future water tools) calls into it and passes nothing more than `(center, radius, scope, source)`:

```csharp
namespace VerdantsAscent.Terrain.Lifecycle;

public static class RegionScrubber
{
    [Flags] public enum Scope
    {
        TileHeights      = 1 << 0,   // wipe per-vertex height + HeightModified
        TilePaint        = 1 << 1,   // wipe per-vertex paint mask
        TileBiome        = 1 << 2,   // wipe per-vertex biome override
        VanillaBiome     = 1 << 3,   // BiomePaintPersistence.ResetBiomeOverlay
        VanillaPaint     = 1 << 4,   // ZonePaintLayer clear / TerrainComp paint mask
        Clutter          = 1 << 5,   // ClutterManager.ClearInRadius (painted)
        VanillaGrass     = 1 << 6,   // ClutterSystem.ResetGrass (vanilla)
        Vegetation       = 1 << 7,   // ZDOs in radius matching VegetationFilter
        Pieces           = 1 << 8,   // player-placed pieces in radius (opt-in)

        // Convenience masks
        TileOnly         = TileHeights | TilePaint | TileBiome,
        VisualOnly       = TileOnly | VanillaBiome | Clutter | VanillaGrass,
        EverythingNoBuild = VisualOnly | VanillaPaint | Vegetation,
        EverythingIncludingBuild = EverythingNoBuild | Pieces,
    }

    public enum Source { Console, BrushClear, BrushDelete, WipeLoaded, WipeAll, VanillaTerrainOp, Undo, Rpc }

    public static int ScrubRegion(Vector3 center, float radius, Scope scope, Source source, bool broadcastRpc = true);
}
```

**Anatomy of one call:**
1. Snapshot affected anchors via `TileAnchorRegistry.TilesOverlappingCircle`.
2. For each anchor in scope: call `TileAnchor.ClearData(center, radius, scope)` — anchor stays, only data clears.
3. If `VanillaBiome` in scope: ONE call to `BiomePaintPersistence.RemoveOverlapping(center, radius)` (NEW — adds the missing remove-from-cache logic and force-regenerates affected Heightmaps).
4. If `Clutter` in scope: ONE call to `ClutterManager.ClearInRadius`.
5. If `VanillaGrass` in scope: ONE call to `ClutterSystem.instance.ResetGrass`.
6. If `Vegetation` / `Pieces` in scope: walk ZDOMan once with the appropriate filter.
7. If `broadcastRpc` and `ZNet.instance != null`: ONE RPC fanout with `(center, radius, scope)`. Receivers re-enter `ScrubRegion` with `broadcastRpc=false` + `source=Rpc`.

No nested scrub calls. No three-times-clutter. No orphan ZDO keys.

### 2.2 Anchor-per-zone, never destroy

- `ZoneSystem.SpawnZone` postfix → ensure a `TileAnchor` ZDO exists for the zone (spawn empty if not). One per `Vector2i` zone key. ZoneID-anchored, not tile-coord-anchored, so vanilla zone replication carries it.
- `TileAnchor` keeps current per-vertex grids but adds `bool IsEmpty()` (zero modified flags + zero disks + zero biome modified). Persists empty fine; ZDO is just a few bytes of header.
- `TileAnchor.DestroyPermanently` becomes private/internal and is only called from one place: TileAnchor.OnDestroy when the zone itself is being unloaded by vanilla (cleanup). User-facing destroy commands route to `RegionScrubber.ScrubRegion(scope: EverythingNoBuild)` instead.
- `va_terrain_wipe_loaded` → `for each loaded anchor: anchor.ClearAllData()`. `va_terrain_wipe_all` zone-pokes (existing logic) then runs the same per-anchor clear instead of `DestroyZDO`.

This eliminates the resurrection bug categorically: there's nothing to resurrect because the anchor never went away.

### 2.3 Layered hooks

- `BiomeOverlayPainter.PaintOverlay` gains a `Source` parameter so it knows whether the call came from a user paint or from `ScrubRegion` (avoids recursion).
- Vanilla `TerrainOp.Apply` Harmony postfix detects paint-with-vegetation-clear (dirt / cultivated / paved) and calls `ScrubRegion(scope: VanillaBiome | Clutter | VanillaGrass)` — this is the user's "vanilla ops clear our biome overlay" requirement. NO custom-tile clear from vanilla paint; the user picked the vanilla tool, they're modifying vanilla.
- `TileBrush.BrushClear` becomes a thin shim: `ScrubRegion(scope: TileOnly | VanillaBiome | Clutter | VanillaGrass, source: BrushClear)`. The current `ApplyClearToTile` logic moves into `TileAnchor.ClearData(scope: TileOnly)`.

---

## 3. Refactor plan (ordered, each is one PR-sized step)

| # | Step | Files | Why first |
|---|------|-------|-----------|
| 1 | **Define `RegionScrubber` skeleton** (signature + enums, no callers) | new `Modules/CustomTerrain/Lifecycle/RegionScrubber.cs` | Nothing else compiles until the API exists. |
| 2 | **Add `TileAnchor.ClearData(center, radius, scope)`** | `TileAnchor.cs`, `TerrainTile.cs` | Per-anchor clear is the inner loop scrub calls. |
| 3 | **Implement scrub for `TileOnly` + `VanillaBiome` scopes** | `RegionScrubber.cs`, new `BiomePaintPersistence.RemoveOverlapping` method | Smallest meaningful slice — fixes the ghost-overlay bug today. |
| 4 | **Route `BrushClear` and `BrushDelete` through scrub** | `TileBrush.cs`, `TileDeleteOps.cs` | Old code stays as fallback; deprecate when scrub proves out. |
| 5 | **Add `Clutter` + `VanillaGrass` scopes** | scrub calls into `ClutterManager.ClearInRadius` + `ClutterSystem.ResetGrass` | Matches RPG-maker behavior on terrain delete. |
| 6 | **Anchor-per-zone spawn at `ZoneSystem.SpawnZone`** | `TileAnchorRegistry.cs` (new postfix), `TileAnchor.cs` | Now `wipe_*` can use `ClearAllData` not destroy. |
| 7 | **Rewire wipe commands to clear-not-destroy** | `TileDeleteOps.cs` | Anchor-per-zone makes this safe. |
| 8 | **RPC fanout for scrub** | new `RegionScrubberRPC.cs` (mirror `TileBrushRPC.cs`) | One broadcast per op, receivers call scrub locally with `source=Rpc`. |
| 9 | **Vanilla TerrainOp postfix → scrub vanilla biome** | new `VanillaTerrainOpPatches.cs` | User's "paint dirt should clear lava overlay" requirement. |
| 10 | **Lava routing cleanup** (single-source, ditch double-paint) | `TileBrushCommands.cs` | Becomes trivial once scope flags exist. |
| 11 | **`Vegetation` + `Pieces` scopes** (opt-in for big wipes) | scrub `ZDOMan` walk | Nice-to-have for full slate. |
| 12 | **Decommission `TileDeleteOps.TryDestroyAnchor`** (becomes private cleanup-only) | `TileDeleteOps.cs` | Last step — once nothing user-facing destroys anchors anymore. |

Steps 1–4 are the unblocking slice: ghost-overlay bug fixed, single API in place, old code still works. Steps 5–8 close the parity gap with RPG-maker AND give us anchor-per-zone. Steps 9–12 finish the polish.

## 4. Custom water parity (designed, not built yet)

When CustomWater integrates back in:
- `IRegionAnchor` interface — `TileAnchor` and `WaterMeshCombinerAnchor` both implement it. Methods: `ClearData(Bounds, Scope)`, `IsEmpty`, `TileXZ` (or `ZoneXZ`).
- `RegionScrubber` walks both registries. Same scope enum gains `WaterCells` / `WaterDisks` flags.
- Move water from per-disk-cluster to **per-zone anchor** with segment children for big bodies (the current v3 segment system already proves this works).
- Kill `RemoveWaterDisk`'s auto-destroy. Water clear goes through scrub, anchor stays alive empty.
- Mirror the `WaterCombinerCache .bin` MP-wipe RPC pattern for our tile cache if/when we add one.

## 5. Things we explicitly defer

- **Per-tile material instances** (Task #73) — orthogonal to lifecycle. Visual stitching can wait.
- **Edge stitching of biome overlays** (Task #84) — visual polish, handle after the lifecycle settles.
- **TerrainOp-based vanilla terrain depression** for lava on vanilla — defer. With anchor-per-zone, the custom tile depression already covers the painted area cleanly because anchor data lives wherever the brush touches, not just under existing tiles.
- **Lava water disk auto-spawn** on AshLands paint — defer until water re-integrates.

## 6. Risks / open questions

1. **`ZoneSystem.SpawnZone` storm** — proactively spawning an anchor per zone means N anchors for a megaworld. Mitigate: anchor's ZDO is ~16 bytes empty; ZDOMan already holds millions of these for vanilla TerrainComp. Should be fine, but worth verifying with `va_terrain_count` before/after.
2. **Backwards compat** — existing worlds have orphan VATile ZDOs from the destroy-on-empty era. Step 6 needs a one-time migration: on `ZoneSystem.SpawnZone`, if a VATile ZDO already exists for the zone, adopt it; else spawn empty.
3. **Server authority** — `RegionScrubberRPC` must respect the same `ConfigServerAuthority` toggle the rest of the mod uses. Clients send a *request*, server applies and broadcasts the actual scrub.
4. **Undo integration** — `TileUndoLog` already snapshots tile vertex state. Scrub-driven clears need to push undo entries the same way brush ops do. Wire at the `RegionScrubber` entry point so every clear is undoable for free.

---

## What I need from you

**Approve the plan** (or push back on specific steps) before I write any more code. The fragmented patching is causing more bugs than it fixes; landing this in order — even just steps 1-4 — closes the ghost-overlay class of bugs and makes the lava routing trivial instead of weird.

After approval I'd execute steps 1–4 in one focused pass, then check in for live test before continuing to 5+.
