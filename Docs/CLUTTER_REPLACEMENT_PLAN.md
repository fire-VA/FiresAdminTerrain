# Clutter Replacement Plan — FAP Integration

**Status:** Plan draft for review (2026-05-24)
**Companion:** [TERRAIN_PERSISTENCE_REDESIGN_PLAN.md](TERRAIN_PERSISTENCE_REDESIGN_PLAN.md)

---

## 1. Goals

- Stop maintaining a parallel clutter painter inside FiresAdminTerrain.
- Reuse FAP's newer clutter system (`VAClutterRegistry`, `VAClutterPaintTool`, `VAClutterStandinFactory`, etc.) as the source of truth for paint-onto-vanilla-TerrainComp clutter.
- Keep grass-spawning-on-dirt-floor-meshes functional if it's still desired (the only feature FAP doesn't cover — its painter only touches vanilla TerrainComp).
- Pick up FAP's `ShaderReplacement` (newer) and `MaterialSwapper` (newer) at the same time — both are useful for terrain/water material work and you flagged you want them.

## 2. Current state

### What's in CustomTerrain/Clutter/ today
| File | Purpose | Lines |
|---|---|---|
| `ClutterManager.cs` | Facade calling DirtFloorMeshCombiner.Grass, ClutterPaintTool, GrassOverlayController, BiomePaintPersistence | 343 |
| `ClutterPaintInputHandler.cs` | Input wiring | 45 |
| `ClutterPaintTool.cs` | The painter (predates FAP's) | 1763 |
| `ClutterPersistence.cs` | ZDO persistence for painted clutter | 451 |

Plus:
- `Modules/CustomTerrain/GrassOverlaySystem.cs` (~750 lines) — per-disk grass overlay on the DirtFloor combiner mesh
- `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.Grass.cs` (~600 lines) — partial that spawns grass on a generated mesh
- `Managers/VAClutterManager.cs` (stub I added) — no-op `EnsureRegistered()` for ClutterPaintTool.cs:91 to compile

### What FAP ships (the would-be replacements)
| File | Lines | Notes |
|---|---|---|
| `VAClutterRegistry.cs` | 97 | Pure lookup, calls `VAExternalBundleLoader.EnsureRegistered()` at line 73 |
| `VAClutterStandinFactory.cs` | 193 | Hammer-tab stand-ins, no clean transitive deps |
| `VAClutterPaintTool.cs` | 1156 | **Has [HarmonyPatch] attributes** at lines 1106, 1134 |
| `VAClutterClearTool.cs` | 180 | Clear tool |
| `VAClutterBrushIndicator.cs` | 221 | Brush indicator MonoBehaviour |
| `VAClutterShaderRepair.cs` | 433 | Per-frame shader repair |
| `ShaderReplacement.cs` | 861 | Bundle shader replacement (newer than ours) |
| `MaterialSwapper.cs` | 362 | Material swap helpers (newer than ours) |
| Plus FAP `Patches/`: HammerClutterPaintPatch, VAClutterDirtPaintCleanup, VAClutterOrphanZdoCleanup, VAClutterSystemLogoutGuard | ~600 combined | All [HarmonyPatch]-decorated |

---

## 3. The source-link conflict problem

The original plan was source-link via `<Compile Include><Link>` (memory: `reference_source_link_pattern`). That pattern works perfectly for **non-Harmony utilities** like `PlayerSpawnGate`. It breaks for Harmony-patched code:

> If a user installs FiresAdminTerrain.dll **and** FiresAdminPrefabs.dll, both DLLs would carry copies of `TerrainComp_Awake_VAClutterRPCRegister_Patch`. Harmony would register both patches against vanilla `TerrainComp.Awake`. The RPC named `FAP_PaintClutter` would be registered twice on every TerrainComp's ZNetView — second registration fails / overrides / spams the log.

This applies to ~7 of FAP's clutter files (everything with `[HarmonyPatch]`). The non-Harmony files (VAClutterRegistry, VAClutterStandinFactory, ShaderReplacement, MaterialSwapper) are safe to source-link as-is.

**Conclusion:** the original "source-link all FAP clutter files" plan won't work cleanly. We need to pick one of three approaches.

---

## 4. Three approaches

### Approach A — Mixed: source-link utilities, keep our painter
- **Source-link only:** `VAClutterRegistry`, `VAClutterStandinFactory`, `ShaderReplacement`, `MaterialSwapper` (no Harmony, no conflicts)
- **Stub:** `VAExternalBundleLoader.EnsureRegistered → 0` in our `Managers/` (so VAClutterRegistry compiles)
- **Keep as-is:** our own `Modules/CustomTerrain/Clutter/*` painter system
- **Cost:** still maintain a clutter painter; FAP's newer painter improvements (sample-level v2 ZDO schema) don't reach us
- **Benefit:** zero conflict with FAP being installed alongside; both painters coexist (different ZDO schemas — paints stay separate, no overwrite)
- **User experience:** "if FAP is installed, use FAP's hammer tabs for clutter; if not, use our painter via console commands"

### Approach B — FAP as hard dependency
- **Add to plugin attribute:** `[BepInDependency("com.Fire.FiresAdminPrefabs", BepInDependency.DependencyFlags.HardDependency)]`
- **Reference FAP's DLL directly** in csproj (just like IH and EW)
- **Delete entirely:** `Modules/CustomTerrain/Clutter/*`, `GrassOverlaySystem.cs`, `DirtFloorMeshCombiner.Grass.cs`, `Managers/VAClutterManager.cs` stub
- **Refactor the ~15 files** that `using VerdantsAscent.Terrain.Clutter;` to use `VerdantsAscent.Utilities` (FAP's namespace for VAClutter* types) and call FAP's API instead
- **Cost:** FiresAdminTerrain won't load without FAP installed
- **Benefit:** zero duplication, FAP owns clutter end-to-end, our project stays slim
- **User experience:** "install both Fires admin mods, they work together"

### Approach C — Soft dependency + reflection (defensive)
- **Soft `BepInDependency`** so the load order is right if FAP is present
- **Reflect into FAP types** at runtime; if FAP missing, fall back to our painter (or silently no-op)
- **Cost:** most boilerplate, hardest to debug, most fragile
- **Benefit:** "works no matter what's installed"

---

## 5. Recommendation

**Approach B (FAP hard dep)** is the cleanest match for the project's stated direction:
- You've already accepted IH + EW as hard deps for FiresAdminTerrain. Adding FAP keeps the dependency story consistent.
- FAP is your own mod — you control the version compatibility.
- It deletes the most code (~3200 lines of CustomTerrain clutter + grass + stub), which is a net win.
- The refactor surface is bounded: ~15 files need their `using VerdantsAscent.Terrain.Clutter` swapped for `VerdantsAscent.Utilities` and a small per-call rewrite.

**Approach A** is the conservative fallback — if for some reason FAP can't be a hard dep, this keeps us building cleanly and ships a working (if duplicated) painter.

**Approach C** is overkill given you already accept hard deps.

---

## 6. Files needing refactor (under Approach B)

These currently `using VerdantsAscent.Terrain.Clutter;` and call into the old classes:

| File | What it uses | Refactor needed |
|---|---|---|
| `BiomeOverlayPainter.cs` | `ClutterManager.RefreshInRadius` | Swap to FAP's clutter refresh API (or no-op if FAP handles refresh internally) |
| `BiomePaintPersistence.cs` | `ClutterManager.ClearInRadius` | Same |
| `BiomeManager.cs` | `Clutter.ClutterManager.RefreshInRadius` (2 calls) | Same |
| `DirtFloorPatches.cs` | `ClutterPaintTool`, `ClutterPaintInputHandler` | Replace input wiring with FAP's `VAClutterBrushIndicator` |
| `DirtFloorTerrainManager.cs` | `ClutterManager` | Drop or rewire |
| `DirtFloorMeshCombiner.HeightOps.cs` | `Clutter` namespace | Drop using if no direct usage |
| `DirtFloorMeshCombiner.Lifecycle.cs` | `Clutter` namespace | Same |
| `DirtFloorMeshCombiner.Static.cs` | `Clutter` namespace | Same |
| `DirtFloorMeshCombiner.DiskManagement.cs` | `Clutter` namespace | Same (this file is being deleted anyway in Phase 8 — see persistence plan) |
| `DirtFloorMeshCombiner.Grass.cs` | All clutter — this IS the per-mesh grass spawner | **See §7 — open question** |
| `DirtFloorController.cs` | `Terrain.Clutter` | Drop using or swap call |
| `TerrainCombinerAnchor.cs` | `Clutter` namespace | Drop using (anchor is being rewritten in Phase 8 anyway) |
| `TerrainOperationRPC.cs` | `Clutter` namespace | Drop using (RPC is being rewritten in Phase 8) |
| `TerrainAPI.cs` | Public API surface using `ClutterManager` | Replace public API with FAP delegates |
| `TerrainUndoManager.cs` | Clutter undo records | Reroute undo through FAP's tool |
| `DirtFloorPlacementTool.cs` | `Terrain.Clutter` | Drop using or swap |
| `TerrainPaintManager.cs` | `Clutter` namespace | Drop using |
| `SnowCommands.cs` | `Clutter` namespace | Drop using; FAP has its own snow clutter |
| `SnowTrailController.cs` | `Clutter` namespace | Drop using; trail clutter may need bespoke handling |
| `VertexPaintSystem.cs` | `Clutter` namespace | Drop using |

Total: ~20 files. Most are `using` cleanup (1-2 lines each). The substantive rewrites are `BiomeManager`/`BiomePaintPersistence` (3-4 calls each), `DirtFloorPatches` (input rewiring), `TerrainAPI` (public surface), and `TerrainUndoManager` (undo schema migration).

---

## 7. The DirtFloor-grass open question

`DirtFloorMeshCombiner.Grass.cs` spawns grass **on the generated custom dirt-floor mesh**, not on vanilla TerrainComp. FAP's `VAClutterPaintTool` paints **on vanilla TerrainComp only** — it doesn't know about our custom mesh.

So under Approach B, deleting `Grass.cs` means **dirt floors no longer auto-sprout grass when you build them**. Three sub-options:

- **7.1 Accept it.** Dirt floors are dirt. If you want grass on a custom floor, paint it with FAP's clutter tool over the top — FAP's paint records sit on the vanilla TerrainComp underneath the custom mesh. *Works visually if the custom mesh is at the same Y as vanilla terrain, breaks if elevated.*
- **7.2 Keep `Grass.cs` and rewire to FAP's `VAClutterRegistry`.** Looks up grass prefabs via FAP's registry instead of our own lookup, but the per-mesh spawning logic stays ours. ~50 line change. Recommended if you want "raised dirt-floor platforms" to still have grass auto-applied.
- **7.3 Extend FAP's painter to support custom meshes.** Add a "paint on this collider, not on vanilla TerrainComp" mode to FAP's `VAClutterPaintTool`. Bigger lift; pushes the responsibility into FAP. Not recommended for V1 — defer.

**Recommended: 7.2.** Keep auto-grass-on-dirt-floor working, just look up prefabs via FAP. Small change, preserves the feature.

---

## 8. Code map (under Approach B + 7.2)

### Deleted entirely
- `Modules/CustomTerrain/Clutter/ClutterManager.cs`
- `Modules/CustomTerrain/Clutter/ClutterPaintInputHandler.cs`
- `Modules/CustomTerrain/Clutter/ClutterPaintTool.cs`
- `Modules/CustomTerrain/Clutter/ClutterPersistence.cs`
- `Modules/CustomTerrain/GrassOverlaySystem.cs`
- `Managers/VAClutterManager.cs` (stub no longer needed)

### Rewritten
- `Modules/CustomTerrain/Core/DirtFloorMeshCombiner.Grass.cs` — keep the per-mesh grass spawning logic, swap prefab-lookup to `VAClutterRegistry.ResolvePrefab`

### Touched (using-statement cleanup + small API swaps)
~20 files listed in §6. Mechanical work — bulk-replace `using VerdantsAscent.Terrain.Clutter;` with appropriate FAP namespace; spot-fix the 4-5 files that make substantive calls.

### Csproj
- Add `<Reference Include="FiresAdminPrefabs">` with `<HintPath>` pointing at FAP's built DLL
- Add `[BepInDependency("com.Fire.FiresAdminPrefabs", HardDependency)]` on the plugin attribute
- Remove the 4 deleted Clutter files + GrassOverlaySystem from `<Compile Include>` list
- Remove `Managers/VAClutterManager.cs` from `<Compile Include>` list

---

## 9. Locked decisions (2026-05-24)

1. **Approach: source-link all non-Harmony FAP utilities, keep our painter.**
   - Source-link into FiresAdminTerrain.dll: `VAClutterRegistry`, `VAClutterStandinFactory`, `ShaderReplacement`, `MaterialSwapper` (all newer versions, no Harmony, no conflicts).
   - Stub transitive FAP deps in our `Managers/` (`VAExternalBundleLoader.EnsureRegistered → 0` etc.) as needed to compile.
   - **Keep our painter** (`Modules/CustomTerrain/Clutter/*`) — FAP's painter has [HarmonyPatch] attributes that would double-register if FAP is also installed.
   - **No `BepInDependency` on FAP.** Mod ships standalone. If a user installs both, the two painters coexist (different ZDO schemas).
2. **Dirt-floor grass: dropped entirely** (Option 7.1). Delete `DirtFloorMeshCombiner.Grass.cs` and `GrassOverlaySystem.cs`. Dirt floors stay dirt. Players wanting grass on a custom platform can paint via our (or FAP's) clutter tool from the top.
3. **ShaderReplacement + MaterialSwapper:** source-linked into our DLL alongside the clutter utilities (same mechanism). No runtime FAP dep needed for these to work.
4. **Order:** persistence redesign first, clutter cleanup second. The two share files (`TerrainCombinerAnchor`, `TerrainOperationRPC`, `DirtFloorMeshCombiner.Lifecycle`) — persistence rewrites them, then clutter cleanup sweeps the remaining.

---

## 10. Phase plan (when we get here)

| Phase | Scope |
|---|---|
| **9a** | Add FAP `<Reference>` to csproj; add `[BepInDependency]`; verify FiresAdminTerrain still builds (FAP types now resolvable but unused). |
| **9b** | Refactor the ~20 files: drop `using VerdantsAscent.Terrain.Clutter;`, swap to FAP namespace, fix the 4-5 substantive call sites. Keep our painter code in place. |
| **9c** | Delete the 4 Clutter files + GrassOverlaySystem; rewrite `DirtFloorMeshCombiner.Grass.cs` to use `VAClutterRegistry`. |
| **9d** | Delete `Managers/VAClutterManager.cs` stub. Verify build. |
| **9e** | Live test: paint clutter via FAP's hammer tabs, confirm it appears on custom terrain; build a raised dirt-floor platform, confirm grass auto-sprouts via the rewritten Grass partial. |
