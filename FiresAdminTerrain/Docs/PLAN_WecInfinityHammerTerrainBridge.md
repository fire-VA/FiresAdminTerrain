# PLAN - World Edit Commands / Infinity Hammer terrain bridge, and the vanilla hoe on FAT tiles

Ported by hand into the local tree (FiresAdminTerrain 0.2.513) from the proposal written on
`feat/wec-ih-terrain-bridge` (head `2c12721`) against the stale GitHub copy (`d9fc99a`).
Read against World Edit Commands 1.67.0 source (`valheimRip2/WorldEditCommands`), Infinity Hammer
(`valheimRip2/InfinityHammer`), and Valheim 1.0.7's `TerrainComp` / `TerrainOp` IL.

Pairs with FiresUnifiedCore 0.2.81's heightmap override (+/-1000 m, on by default, **not** suppressed in
voxel worlds). FiresAdminTerrain must never register a `HeightmapOverrideLimits` suppressor.

---

## Goal

Every terrain paint and every hoe-style edit - level, raise, lower, smooth, slope, paint, cultivate -
works on FAT tiles and on voxel terrain, from the vanilla hoe / cultivator / pickaxe, World Edit
Commands, and Infinity Hammer.

---

## What vanilla actually does (1.0.7 IL, confirmed)

`TerrainComp.InternalDoOperation` applies the flags in this order, and applies **all** of them:

```
if (m_level)       LevelTerrain (pos + up * m_levelOffset, m_levelRadius, m_square)
if (m_raise)       RaiseTerrain (pos,                      m_raiseRadius, m_raiseDelta, m_square, m_raisePower)
if (m_smooth)      SmoothTerrain(pos + up * m_levelOffset, m_smoothRadius, m_square, m_smoothPower)
if (m_paintCleared)PaintCleared (pos, rot, modifier)
```

`LevelTerrain` and `RaiseTerrain` each clamp `m_levelDelta` once with `Mathf.Clamp(x, -8f, 8f)`;
`ApplyToHeightmap` clamps the rendered height with `base - 8 .. base + 8`. All three are patched by
Core, so edits above 8 m now survive.

This settles the proposal's open question about the level offset and the square footprint:
`TerrainOp.Settings.m_levelOffset`, `m_square`, `m_smoothPower` all exist.
`m_levelOffset` is now carried to tiles (level and smooth). `m_square` is **not**: the tile
`square` path is a hard-edged stamp with no falloff, and both the tile side and
`FiresUnifiedPaint.MirrorPaint`'s vanilla side must share one falloff curve or a stroke crossing a
tile border gets a visible seam. Carrying `m_square` is a separate, visible change - it needs Fire's
call.

---

## Voxels - already working

Voxel terrain is built from the heightmap, and every tool edits the heightmap:

- `Patch_Player_PieceRayTest_VoxelTerrain` resolves a heightmap for any collider carrying a
  `VoxelChunkView`, so hoe and cultivator ghosts are valid on voxel chunks.
- `TerrainOp` -> the zone's `TerrainComp` -> heightmap data -> `Heightmap.Regenerate` ->
  `VoxelZoneStreamer` rewraps the zone and its neighbours. WEC's `Poke()` takes the same route.
- Paint reaches the voxels through the same `Regenerate` hook (`MarkZonePaintDirty`), and
  `IsCultivated` answers from the resolved zone heightmap's data layer.

Two limits, both handled:

- Anything over 8 m used to be flattened by `ApplyToHeightmap` before the rewrap sampled it. Lifted
  by Core, which must stay **on** in voxel worlds.
- WEC's `void` writes NaN heights straight into the density field. It is now refused in voxel worlds
  (in `VoxelWrap` the vanilla heightmap is authoritative, so a void would hole the server's terrain
  while voxel clients stand on ground), and `TryGroundHeight` treats a non-finite sample as generated
  ground with a one-time warning. It deliberately does **not** set `usedFallback`: a void never
  resolves, so a provisional border would rewrap forever. The guard sits in `TryGroundHeight` rather
  than in `GatherColumns` because every voxel consumer of a ground height goes through it (cave
  content, clutter, waterfalls, zone-mode placement), and one central answer beats a dozen local ones.

---

## FAT tiles - the three blocker bugs

`TileTerrainOpBlocker` intercepts `TerrainOp.Awake` over a FAT tile (from FAT's one
`TerrainOp.Awake` hook in `FatVanillaHooks`) and reroutes the op to the tile.

1. **A silent no-op on a dedicated server.** It called `TileBrush.*` directly. Only the owner of a
   tile writes its ZDO, and in strict-server MP the player is a non-host client that owns none - so
   the edit was skipped after the blocker had already destroyed the vanilla op. Nothing happened, for
   admins too. Now routed through `TileBrushRPC.Send*`: broadcast to everybody including the sender,
   the owner applies, ZDO replication does the rest. Without networking `Send*` calls `TileBrush`
   directly, so single-player is unchanged.
2. **Only the first flag applied.** It returned after the first of level / raise / smooth / paint that
   was set, so a tool that levels and paves never paved, and one that levels and cultivates never
   marked the ground cultivated (planting failed). Now all flags, in vanilla's order.
3. **Smooth deleted FAT tile.** Smooth went to `BrushClear`, which clears the modified flags - and
   tile terrain only exists where vertices are modified. Now `BrushSmooth`, one pass per op
   (`VanillaSmoothStrength = 0.25`), matching vanilla's incremental smoothing.

A non-admin used to lose the op silently; now `TileBrushRPC.LocalPeerMayEdit()` is checked **before**
routing, the player is told, and neither the tile nor the vanilla ground below changes. The local
unified-paint mirror (`FiresUnifiedPaint.MirrorPaint`) stays where it was, after the route, so it is
skipped for a non-admin too.

Tile paint rendering is not a problem any more: `TileMeshRenderer.RebuildPaintMaskAndBind` builds a
per-tile `_ClearedMaskTex` from `Paint[]` on every rebuild. `TERRAIN_PARITY_AUDIT.md`'s paint rows are
stale (they predate round-15).

---

## The bridge - `WecTerrainBridge`

Infinity Hammer's terrain tools (`tool_terrain`, `tool_terrain_to`, `tool_slope`) run World Edit
Commands' `terrain` command. WEC writes `TerrainComp`'s arrays directly and `Poke()`s - it never goes
through `TerrainOp.Awake`, so the blocker never saw it, and over a FAT tile it edited the ground below.

A Harmony prefix on WEC's private `TerrainCommand.GetCompilers(TerrainParameters pars)` - the first
call after WEC parses the command, carrying the full intent, before any heightmap node is gathered.
Returning an empty `TerrainComp[]` leaves the rest of the command running over nothing: every
`Terrain.*Terrain` call iterates an empty node list, `Terrain.Save` has nothing to save, and the undo
entry it records is empty. WEC is reached by reflection only (`AccessTools.TypeByName`), so there is no
hard dependency and no assembly reference.

**Target:** the FAT tile if one is at the command's position and the command's Y is at least as close
to it as to the ground; otherwise the ground. Aiming WEC at the ground *under* a FAT tile still edits
the ground - something the vanilla-hoe blocker cannot do.

**Initialisation** at `ZNetScene.Awake` beside `TileHammerCompat.Initialize()`, at both of its call
sites: plugins load alphabetically, so WEC is not in the AppDomain at FiresAdminTerrain's own init.
Re-entry is guarded by `s_initialized` - a doubled prefix would send every FAT tile edit twice. WEC
present but its command shape changed is an **error**, and the bridge stays off.

On a FAT tile, in WEC's own order, each through `TileBrushRPC`:

| WEC | Tile op |
|---|---|
| `slope` (skipped by WEC when `level` is also given) | `SendSlope` - new |
| `level` | `SendFlat` |
| `raise` / `lower` | `SendRaise` |
| `void` | `SendHolePunch` |
| `paint` | `SendPaint` - WEC's own named paints, or `r,g,b[,a]` |
| `reset`, `set`, `min`, `max`, rect shape and rotation, `blockcheck`/`within`/`chance`, `smooth` on level and raise | reported, not applied |

`reset` deliberately does not map to `BrushClear` - that would delete the FAT tile. Each tile op
records its own undo entry; `TileUndoLog` does not nest (a nested `BeginOperation` overwrites the open
one), so the bridge does not wrap them.

In a voxel world, `void` is refused (`pars.Void` cleared; the rest of the command still runs).

### Why `TileHammerCompat` gained a second lift scope

This is the one change the proposal did not have, and it is needed because the local copy diverged.

WEC resolves a command's Y in the `TerrainParameters` constructor:
`Position.y = ZoneSystem.instance.GetGroundHeight(Position)`, unless the command carries an explicit
`from=x,z,y`. `TileHammerCompat` patches `GetGroundHeight` to answer the FAT tile's surface - but the
local copy gates that lift behind `s_rulerSnapDepth > 0`, so it answers only inside Infinity Hammer's
`BaseRuler.Snap`. That gate exists for a good reason (an ungated lift moved vegetation, AI and
character grounding onto skylands - the 2026-05-24 bug), and the stale GitHub copy the proposal was
written against did not have it.

Consequence on the local copy without a fix: Infinity Hammer's tools work, because
`InfinityTools.CommandValue` always substitutes `from=<x>,<z>,<y>` from the ghost / ruler position,
which `BaseRuler.Snap` has already lifted onto the tile. A **typed** WEC command does not, so its Y is
the vanilla ground's and `IsTargetingTile` routes it to the ground - the bridge would be dead for
everything but Infinity Hammer.

Fix: a second, independent counter `s_terrainCommandDepth`, opened by a prefix on WEC's
`TerrainParameters` constructor and closed by a **finalizer** (the constructor throws when the zone is
not loaded, so a postfix would leak the scope). `GroundHeightSingle` / `GroundHeightOut` now lift while
either scope is open. Nothing else changes: outside a ruler snap and outside a terrain command the lift
is as closed as before.

The visible consequence, which is also the proposal's intended design: a WEC command whose XZ has a
FAT tile now targets the tile by default. To reach the ground beneath a tile, give the ground's Y
explicitly - `terrain raise=3 from=x,z,<ground y>`.

### Tile slope - `TileBrush.BrushSlope` + `TileBrushRPC.SendSlope`

WEC's `Terrain.SlopeTerrain` sets each node to
`altitude - generatedHeight + CalculateSlope(angle, nx, nz) * CalculateSmooth(smooth, d) * amount / 2`
as a level delta, i.e. a final height of `altitude + tilt`, where for a circle
`nx = dx / radius`, `nz = dz / radius`, `d = |d| / radius`, and
`CalculateSmooth(smooth, d) = (1 - d) < smooth ? (1 - d) / smooth : 1` - the smoothing fades only the
tilt, never the altitude. `ApplySlopeToTile` writes exactly that, so a tile ramp and a vanilla ramp
from the same command agree. It follows `BrushFlat`'s pattern (per-vertex writes, `ApplyOpAndSave`,
undo entry, `StitchTileBorders`) but needs no neighbour-aware baseline: the height is a pure function
of world position, so tiles sharing a border vertex agree by construction.

`smooth = 0` is safe - `(1 - d) >= 0` holds for every vertex inside the footprint, so the division
never runs.

**The server must run this build**; an older server has no `VATileBrushSlope` handler and drops slopes.
`RegisteredHandlers` is now 11.

### Local divergences resolved while porting

- `TileAnchorPrefab` / `TileBrushRPC` / `TileTerrainOpBlocker` no longer own their own Harmony
  patches - FAT has one hook per vanilla method in `FatVanillaHooks`. The bridge's init calls went to
  the two `TileHammerCompat.Initialize()` call sites that remain in `TileAnchorPrefab.Ensure`, and the
  blocker's edits went into `OverTile.Allow`, the body behind `FatVanillaHooks`' prefix.
- `TileTerrainOpBlocker` has gained unified paint mirroring since the stale copy. It is kept, after
  the route, and is now also skipped for a non-admin.
- `VoxelZoneStreamer.ModeActive` is `FiresVoxelConfig.StreamerRuns` now, not
  `WorldMode != Vanilla`. `IsVoxelWorldActive` is defined from it, so "a voxel world" means every
  Terrain Mode except Off, which is the right question for the void refusal.
- The proposal's `GeneratedGround(wg, wx, wz)` / non-finite branch in `GatherColumns` became a
  `GeneratedGround(wx, wz)` helper plus the guard inside `TryGroundHeight`, because the local
  `GatherColumns` goes through `TryGroundHeight` (heightmap, then `HeightmapBuildCache`, then location
  pads) rather than reading the heightmap directly. `GatherColumns`' own `30f` fallback is now the same
  named constant.
- `TileBrushRPC.LocalPeerMayEdit` asks `FiresCore.Sync.AdminSyncing.IsLocalAdmin()` - the same answer
  `AdminCheck.IsAdmin` gives for this machine's own session id - instead of re-deriving it from
  `ZNet.GetUID()`.
- `Console.instance.Print`, matching the ~142 other console lines in the mod. (An earlier draft of this
  plan claimed `Print` did not exist in 1.0.7 and was a latent `MissingMethodException`. That was wrong:
  `Print(string)` is declared on `Console`, not on `Terminal`, and it is present in both the live and the
  publicized 1.0.7 assemblies. Its body is `AddString(text)`.)

---

## Sky Islands

Sky islands in this tree are **voxel terrain generated from the world seed**
(`FiresSkyIslands.Sdf` / `TopAt`, built by `FiresSkyIslands.Build(seed)` and meshed by the voxel
streamer). They are not FAT tiles, they carry no per-vertex ZDO data, and no edit to them is persisted -
so they inherit the voxel path, not the tile path, and the tile-side fixes above do not reach them.

Against the proposal's checklist:

1. **Placement resolves.** Yes, by inheritance: an island chunk carries a `VoxelChunkView`, so
   `Patch_Player_PieceRayTest_VoxelTerrain` resolves a heightmap and the ghost is valid. But the
   heightmap it resolves is the **ground zone's** (`VoxelHeightmapResolver.FromChunkGO` ->
   `Heightmap.FindHeightmap(collider.bounds.center)`), so `IsCultivated`, `GetBiome` and the
   vegetation mask answer from the ground far below, not from the island.
2. **All four flags at the island's surface Y.** No - and it was dangerous. A hoe op on an island
   at Y=250 spawns a `TerrainOp` there, which reaches the ground zone's `TerrainComp`;
   `LevelTerrain` computes `worldPos.y - generatedHeight` and, with Core's clamp lifted, writes a
   ~220 m level delta. The ground under the island rises into a tower. Vanilla's +/-8 m hid this
   before Core 0.2.81. **Now refused** - see *The sky-island terrain guard* below. Height edits on an
   island are not supported at all; paint-only ops still apply.
3. **Writes go to the owner over an RPC.** N/A - there is no island data to write.
4. **Smooth smooths.** N/A for deletion: an island's existence is not tied to modified flags, so no
   clear or smooth can delete one. Smooth on an island edits the ground below, as in item 2.
5. **Paint renders.** The voxel skin samples the zone heightmap's paint mask, so painting "on" an
   island paints the ground's mask at that XZ. Whether the island's own surface picks that up needs a
   look in the mesher; there is no island-specific paint layer.
6. **WEC and Infinity Hammer reach it.** No. `IsTargetingTile` asks only `TileAnchorRegistry`, so a
   command over an island resolves to the ground. The proposed extension still stands: a registration
   hook (surface name, a `Vector3 -> surface Y?` lookup, and the surface's op senders) so the bridge
   routes to whichever registered surface is nearest the command's Y without knowing about islands.
   Slope would reuse `BrushSlope`'s formula. It needs an island edit-persistence layer first, which
   does not exist (`va_voxel_carve` is runtime-only).
7. **The `GetGroundHeight` hazard.** Not present for islands: `VoxelGroundTruth.Sample` searches
   `h + 24 .. h - 200` around the ground column, so it never reports an island top, and nothing else
   lifts `GetGroundHeight` onto an island. WEC over an island therefore edits the ground at the
   ground's own Y - wrong target, but no +/-1000 m runaway. The vanilla hoe (item 2) is the runaway.
8. **The admin-gate policy decision.** Same as below.

---

## The sky-island terrain guard (approved, implemented)

`FiresSkyIslandTerrainGuard.AllowTerrainOp` refuses a vanilla terrain op that sits on a sky island,
from the `Priority.Low` prefix of FAT's one `TerrainOp.Awake` hook - last in FAT's chain, so the
closed-zone refusal and the FAT tile route both get the op first.

**Why last, and why it needs the markers.** Every prefix on a method still runs after one of them has
returned false, which is why `TileTerrainOpBlocker` already asked `ZoneModeRefusals.WasCancelled`. A FAT
tile can legitimately sit inside a sky island's band, and in that case the player is aiming at the tile,
not the island - so the tile route must win. The guard therefore runs last and asks both
`ZoneModeRefusals.WasCancelled(op)` and the new `TileTerrainOpBlocker.WasRoutedToTile(op)` before
treating the op as unclaimed. `WasRoutedToTile` is set on both of the blocker's claiming paths (the
route and the non-admin refusal), mirroring `ZoneModeRefusals`' own marker.

**Height flags only.** It refuses when `m_level || m_raise || m_smooth`, and lets paint-only ops
through. Paint is genuinely different: `VoxelWorld.RefreshPaintMask` builds one 33x33 mask per XZ
**chunk column** from the zone heightmap's paint data and binds it to every chunk in that column, so an
island already wears whatever is painted at its XZ - pathen, cultivate, grass and the rest keep working
on islands, and cultivate keeps feeding planting. The op's `m_settings` is the shared prefab instance
(`TerrainOp.Settings.Deserialize` returns `prefab.m_settings`), so stripping individual flags to keep
the paint half of a combined op is not an option - it would corrupt the prefab for the whole session.
An op that both levels and paints is refused whole.

**Cost.** `FiresSkyIslands.IsOnIsland` -> `TryBottomAt`, whose first test is
`Enabled` = `s_any && FiresVoxelConfig.BuildsSkyIslands`. With no islands built that is one bool and
one config read, so **no extra gating is needed** - the guard is free in every world that has no sky
islands, which includes every vanilla and every non-FullVoxel world. When islands do exist it is one
spatial-hash cell lookup plus a `Covers` test per island in that cell, once per terrain op (one hoe
swing), which is nothing next to the heightmap rebuild the op would have triggered.

**Reporting.** The first refusal is a warning naming the position and saying paint still works;
later ones go to verbose, because a held brush would otherwise log every stroke. The player gets a
centre message with the same 1.5 s cooldown and 12 m proximity rule `ZoneModeRefusals` uses.

**Config cannot substitute for this.** Lowering `MaxHeight`, setting `Enabled = false`, or turning
`Sky Islands` off only re-clamps on render - a level delta already written stays in the save.

**Not fixed by this:** WEC and Infinity Hammer over an island still edit the ground (they never reach
`TerrainOp.Awake`). They edit it at the ground's own Y, so there is no runaway, but it is the wrong
target. That is Sky Islands checklist item 6, and it needs the registration hook plus an island
edit-persistence layer.

---

## Vanilla 1.0 shipped its own deep snow - relevant to FAT's paint

Found while auditing the 1.0.7 assembly, and it bears on FAT's snow work:

- `TerrainModifier.PaintType.DeepSnow` is a new vanilla paint type, and
  `Heightmap.m_paintMaskDeepSnow = Color(1, 1, 1, 1)`.
- Snow **depth** is read from the paint mask's **G channel**: `Heightmap.GetCultivationMask(worldPos)`
  returns `m_paintMask.GetPixel(x, y).g`, and vanilla multiplies it by `m_deepSnowSlowMaxHeight`.
  `Piece.m_allowedInDeepSnow` / `m_requireDeepSnow` gate placement against `m_deepSnowBuildHeight`, with
  a new `Player.PlacementStatus.DeepSnow`. `SnowRoller` and `SnowDestruction` are new too, plus
  `_SnowCover` / `_SnowLevel` shader globals.
- **So vanilla deep snow and FAT's cultivated paint share the G channel.** In DeepNorth, cultivated
  ground reads as deep snow and deep snow reads as cultivated.
- `TileTerrainOpBlocker.MapPaintTypeToColor` only maps Dirt / Cultivate / Paved / Reset; every other
  `PaintType`, including `DeepSnow`, falls through to "no paint" `(0,0,0,255)`. So painting deep snow on
  a FAT tile currently **erases** the tile's paint. `WecTerrainBridge` inherits the gap from the other
  side: WEC's named-paint table has no deep-snow entry, so it would need `paint=1,1,1,1`.
- `TileMeshRenderer`'s `_ClearedMaskTex` would need the same `(1,1,1,1)` encoding to render it.

---

## Non-admins on FAT tiles (ruled, implemented)

Fire's line is **decorate vs reshape**: a player may finish a surface they already have, but may not
change the shape of the world. The test for any op added later is that question, not "is it a height op".

| Op | Non-admin |
|---|---|
| paint | allowed |
| level | allowed |
| smooth | allowed |
| raise / lower | denied |
| hole punch | never |
| clear, biome, hole patch, grab, noise, slope | denied |

`TileBrushRPC.TileOp` names every op an RPC can carry, and `NonAdminOps` is an **allow-list** of the
three permitted ones. A new op is a new enum member that no allow-list names, so it is denied by
default - a deny-list would have silently permitted it.

**The server-side gate is the authority.** `RecvWithAdminGate` now keys on `TileOp`: it applies when the
sender is an admin **or** the op is in `NonAdminOps`, and logs the rejection otherwise. It is the only
place the rule is enforced. `TileBrushRPC.LocalPeerMayEdit(TileOp)` is the client's early-out, there so
the refusal is visible before the vanilla op is destroyed; it only has to agree with the gate. The admin
path is untouched - admins keep every op. The permission check lives in those two members and nowhere
else in the brush code.

**Ops are applied whole or refused whole.** One vanilla op can carry level + raise + smooth + paint, and
one WEC command several ops. Half-applying would be worse than refusing, and flags cannot be stripped:
`op.m_settings` IS the shared prefab instance (`TerrainOp.Settings.Deserialize` returns
`prefab.m_settings`), so editing it would corrupt the prefab for the session.
`TileTerrainOpBlocker.PeerMayApplyEveryFlag` walks the four vanilla flags in vanilla's order and
`WecTerrainBridge.MayApplyEveryOp` walks the command's ops in WEC's order; the first denied one names
itself in the message and the log.

### Which refusal wins when two rules fire

Chain order on `TerrainOp.Awake` is closed-zone refusal, then the FAT tile route, then the sky-island
guard. Exactly one message ever reaches the player, and **whether a FAT tile claimed the op decides
which**:

| Case | What happens | Message |
|---|---|---|
| non-admin smooths a normal FAT tile | routed to the tile | none (it worked) |
| non-admin smooths a FAT tile that sits inside an island band | routed to the **tile** - the tile is what they aimed at, and the island is untouched | none (it worked) |
| non-admin smooths a sky island with no tile there | tile route passes, island guard refuses | the sky-island message |
| non-admin raises a FAT tile inside an island band | the blocker refuses first and marks `WasRoutedToTile`, so the guard stays silent | the admin-gate message |
| non-admin raises a sky island with no tile | island guard refuses | the sky-island message |
| admin raises a sky island with no tile | island guard refuses (it applies to everyone) | the sky-island message |

The two rules are orthogonal and both are needed: the admin gate answers *who may edit*, the island
guard answers *whether the target can be edited at all*. Smooth being permitted does not make it valid
on an island - there it would write `m_smoothDelta` on the ground zone's `TerrainComp`, not on the island.

### Scope gap - worth a ruling

Fire's wording was "their own base floors and farm plots", and the allow-list has no concept of "their
own". Where that is enforced today:

- **The vanilla hoe / cultivator path IS scoped, by vanilla.** `Attack.SpawnOnHitTerrain` refuses to
  spawn a `TerrainOp` at all unless `PrivateArea.CheckAccess(hitPoint, op.GetRadius())` passes and the
  point is outside a no-build location, and the placement ghost checks the ward too. So a non-admin can
  only level, smooth and paint FAT tiles inside their own ward - which is exactly the intent.
- **The `va_brush_*` console commands and the WEC path are NOT scoped.** Both call `TileBrushRPC.Send*`
  directly with no ward check. The commands are `isCheat: true`, which is a client-side gate only. So a
  non-admin who can enable devcommands, or who has World Edit Commands, can level, smooth and paint any
  FAT tile anywhere in the world.

Two options if that should be closed: a `PrivateArea.CheckAccess` in the gate, or FAT's own territory
ownership. Either needs the op's centre, which the gate does not have today (it runs before the package
is deserialised), so it means passing the centre into `RecvWithAdminGate`. Not implemented - it is a
design choice, not a detail.

---

## Still open

1. **`m_square` on tile level / smooth** - carry vanilla's square footprint to tiles, or keep the
   circular smoothstep that the unified paint mirror matches? Carrying it would seam any stroke
   crossing a tile border, because `FiresUnifiedPaint.MirrorPaint`'s vanilla side keeps its own falloff.
2. **Scoping the non-admin allow-list to a ward or a territory** - see the scope gap above.
3. **Regenerating the publicized assembly.** `publicized_assemblies/assembly_valheim_publicized.dll` is
   dated 2026-09-17; the live `assembly_valheim.dll` is 2026-09-18, so FAT compiles against a surface one
   patch old. Screened every game API the shipped DLL calls (545 distinct members, from its IL) against
   the live assembly: no genuine absences, so nothing is hidden right now. That screen matches names, not
   signatures, and only covers the last build - regenerating and rebuilding is the complete answer, and
   it affects every mod, so it is not this session's call.

Answered and implemented: the non-admin op list, and refusing terrain ops on sky islands.

---

## Not done / unverified

- **WEC's `undo`** does not undo FAT tile edits - those live in `TileUndoLog`.
- **IH `terrainGrid`** snaps to heightmap nodes, not the tile grid.
- **IH terrain copy** over a FAT tile copies the ground below.
- **Voxel brush carves** (`va_voxel_carve`) are runtime-only; whether a nearby WEC-triggered rewrap
  preserves them is unverified.
- A WEC rect on a tile becomes a square of its longer side (reported at runtime).

---

## Verify (dedicated server, an admin client; needs the game)

1. Build with FiresUnifiedCore 0.2.81. `[WecTerrainBridge] Routing...` appears once, and
   `[TileBrushRPC] Registered 11 RPC handlers` lists slope.
2. **Vanilla hoe on a FAT tile:** level, raise, pathen, paved road, cultivate, then plant. Each takes
   effect and persists after a relog. Paved road both levels **and** paves. Level flattens at the
   tile's surface height, not the ground below.
3. **Smooth on a FAT tile:** the terrain smooths; nothing disappears.
4. **WEC:** `terrain slope=6 circle=5` on a FAT tile gives a clean ramp; `terrain level`, `raise=3`,
   `paint=paved`, `void` all hit the FAT tile, never the ground below. Typed commands work without
   `from=`.
5. **IH:** `tool_slope` and `tool_terrain` on a FAT tile behave like step 4.
6. **Ground under a FAT tile:** `terrain raise=3 from=x,z,<ground y>` edits the ground.
7. **Non-admin client:** hoe on a FAT tile shows "Only admins can reshape this terrain."; nothing
   changes above or below. WEC prints the admin-gated line.
8. **VoxelWrap and VoxelOnly:** repeat 2 and 4 on voxel ground - `terrain raise=20` reaches the full
   20 m, paint shows on the voxel surface, cultivate then plant works, `terrain void` is refused with
   a message and no NaN warning follows.
9. **Vegetation regression** for the new lift scope: run a WEC terrain command near a FAT tile, then
   walk into unloaded zones - trees must still plant on vanilla ground, not at the tile's height.
