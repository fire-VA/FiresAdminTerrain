# Vanilla 1.0 Snow Integration - Survey and Plan

Written 2026-09-23. Fire's instruction was "we will need to remake our snow to work with 1.0's
snow since they made their own."

Status 2026-09-23: Fire chose Option A (vanilla owns the G channel).
**ALL STEPS DONE AND ON THE RIGS.** Step 7 was SKIPPED (no Mountain snow); every other step is built
and deployed, verified by probing the shipped binaries rather than the build summaries:

```
steps 1-3   FAT 0.2.514 / 0.2.515
step 4      Core 0.2.87 (now 0.2.90) + FiresRPGmaker 0.0.466
step 5      FiresTossinShade 0.3.144
step 6      FiresTossinShade 0.3.146
step 8      FiresEasyBakeMeshes 1.2.40 (CanShowSnow + SnowReaches in the binary)
            + Core 0.2.90 (IsSnowCap in the binary)
step 9      Core 0.2.90 (CharacterSnowCover in the binary)
```

Step 4 also removed public API from FiresUnifiedCore.

Every vanilla claim below is cited to `valheimRip2\assembly_valheimREF1.0\*.cs` (the 1.0.7 client
decompile) or to the shipped 1.0 assets under `NewProject2026\ValheimRip1.0\Assets\`. Nothing
here is inferred from pre-1.0 sources.

---

## 0. The spine: who owns the paint mask's GREEN channel

Everything else in this plan is downstream of one fact.

`Heightmap.GetCultivationMask(worldPos)` returns `m_paintMask.GetPixel(x, y).g`
(`Heightmap.cs:744-750`). In 1.0 that same channel is **snow depth in Deep North**:

- `Heightmap.GetHeightOffset` - `DeepNorth: Lerp(offset(Snow), offset(SnowVeryDeep), cultivationMask)`, i.e. 0.1 m to **2.1 m** (`Heightmap.cs:762-775`, `Heightmap.cs:1004-1021`).
- `Heightmap.GetGroundMaterial` - `cultivationMask > 0.6 -> SnowVeryDeep`, `> 0.12 -> SnowDeep` (`Heightmap.cs:986-996`).
- `Character` movement - deep-snow slow, sink test and `m_deepSnowWalkObj` trail spawn all read `GetCultivationMask * m_deepSnowSlowMaxHeight` (`Character.cs:1172-1194`).
- `SnowRoller` - snowball growth reads the same field (`SnowRoller.cs:41-58`).
- `Player` placement - `m_allowedInDeepSnow` / `m_requireDeepSnow` gate on `GetCultivationMask(point) > m_deepSnowBuildHeight` (0.15) (`Player.cs:2935-2937`, `Piece.cs:45-46`, `Player.cs:126`, `Player.PlacementStatus.DeepSnow` at `Player.cs:5798`).

It is authored by worldgen, not saved: `WorldGenerator.GetDeepNorthHeight` writes
`mask = new Color(0, g, 0, 0)` with `g` in roughly **0.3 - 0.6** (`WorldGenerator.cs:1106-1107`),
and `HeightmapBuilder` bilinearly blends that out-param into `m_baseMask`
(`HeightmapBuilder.cs:113-148`). Terrain is generated, never saved - so a patch that drops the
out-param drops the snow field for good.

Vanilla accepts the aliasing and works around it rather than separating the channels:

- `TerrainComp.PaintCleared` sets `isChangingSnow = IsDeepnorth(x, z) && paintType == Cultivate`
  (`TerrainComp.cs:454`). When true, only channels whose target colour is non-zero are written
  (so only G), and the write becomes `Clamp01(from + value)` instead of `Lerp(from, to, value)`
  (`TerrainComp.cs:537-541`, `TerrainComp.cs:585`). **In Deep North the hoe's Cultivate op is the
  additive snow-depth brush.**
- `TerrainModifier.PaintType.DeepSnow` is new (`TerrainModifier.cs:238-246`) and stamps
  `Heightmap.m_paintMaskDeepSnow = (1,1,1,1)` (`Heightmap.cs:63`, `Heightmap.cs:700-701`,
  `TerrainComp.cs:530-531`). It is **not** routed through `isChangingSnow`, so it lerps R, G and B
  to 1 together - dirt + cultivated + paved + full snow. No C# caller: it is authored on location
  TerrainModifier prefabs.
- `TerrainComp.SetSnowMask(float depth = -1f)` (`TerrainComp.cs:786-799`) is a public whole-tile
  authoring entry point with **no caller in the assembly**. This is the clean API for us.
  Caveat: it passes `world.y` where `GetBiomeHeight` wants `wy` (`TerrainComp.cs:791`), so the
  sampled mask is always taken at world Z = 0. Harmless only when `depth != -1` is passed,
  because `GetDeepNorthHeight` writes zero into R, B and A anyway. **Always pass an explicit
  depth.**

**Decision required before any code lands.** Only one of these can be true:

| Option | What it means | Cost |
| --- | --- | --- |
| **A. Vanilla owns G** (recommended) | FAT stops treating G as "cultivated" inside Deep North. Cultivate paint in DN becomes the snow brush, exactly as vanilla. Every FAT absolute paint write learns a DN branch. | Touches `BiomePaintPersistence`, `TileTerrainOpBlocker`, tile/voxel mask encode. FAT loses cultivated-look ground in DN - which vanilla also does not have. |
| **B. FAT owns G, vanilla snow disabled in FAT worlds** | Keep FAT's paint semantics, patch out vanilla's DN reads. | Breaks placement gating, ground materials, snow trails, SnowRoller and camera offset. Re-implements a system Iron Gate already shipped. Against the standing "exact vanilla first" preference. |
| **C. Split channels** | Move FAT's cultivated bit elsewhere (a side mask). | Largest change; FAT's tile mask is already at 4 channels (see section 2). |

**Fire chose Option A on 2026-09-23.** This plan is written for it.

---

## 1. What vanilla 1.0 actually ships

### 1.1 Terrain deep snow (Deep North only)

Covered in section 0. Scope check: `GetHeightOffset` handles only `AshLands` and `DeepNorth`
(`Heightmap.cs:764-774`); the deep-snow branch of `GetGroundMaterial` is guarded by
`biome == DeepNorth` (`Heightmap.cs:986-996`); the whole `Character` deep-snow block sits inside
`if (m_lastBiome == Heightmap.Biome.DeepNorth)` (`Character.cs:1172`). **Mountain gets no new
snow system in 1.0.** Mountain therefore has no deep snow at all - Fire declined ours in step 7.

The visible surface is raised above the collision surface. That is not an inference from the
shader - it is why these three exist:

- `GameCamera` pushes the camera out and up over snow/lava (`GameCamera.cs:28-30`, `:74-75`, `:322-323`, `:340`, `:367`, `:391`, via `ZoneSystem.GetGroundOffset`).
- `FloatingTerrain` floats objects on top of the offset surface (`FloatingTerrain.cs:46-48`).
- `TreeLog` uses it for resting height (`TreeLog.cs:106`).
- Characters sink: the slow factor is computed from how far below `groundHeight + depth` the character is (`Character.cs:1186-1189`).

`ZoneSystem.GetGroundOffset` (two overloads, `ZoneSystem.cs:2044-2061`) is the public read.

### 1.2 Piece snow (Deep North only)

`WearNTear` grew a complete snow system:

- Three authored renderers on the prefab: `m_snow`, `m_snowWorn`, `m_snowBroken` (`WearNTear.cs:23-25`). `Awake` expands their bounds by `c_ExtraSnowMeshBoundsHeight = 1.25f` and disables them (`WearNTear.cs:124`, `:168-190`).
- Buildup state on the ZDO: `ZDOVars.s_snow` (float) and `ZDOVars.s_preSnow` (bool) (`ZDOVars.cs:121`, `:148`; read at `WearNTear.cs:192-194`).
- Accumulation runs only when `m_biome == DeepNorth` (`WearNTear.cs:407`), rate `EnvMan.GetSnowBuildup()` (from `EnvSetup.m_snowBuildup`, `EnvMan.cs:574`, `:844-847`, `EnvSetup.cs:24`) times `Game.m_snowBuildupSpeed` (`Game.cs:82`).
- Roofed or shield-covered pieces get none: `CanHaveSnow` returns `!isShielded && !m_haveRoof` (`WearNTear.cs:523-536`).
- Unroofed, poorly supported pieces take **snow damage**: `Game.m_snowDamage` 10, `Game.m_snowSupportLevel` 0.25, effect interval 1-1.5 s (`Game.cs:83-86`, `WearNTear.cs:427-441`). Global keys `NoHeavySnow` / `AllHeavySnow` override (`GlobalKeys.cs:48-49`).
- Visual: `UpdateSnowVisual` switches the three renderers on above 0.25 buildup and drives `_SnowLevel` remapped 0.25..1 -> 0..1 through `MaterialMan` (`WearNTear.cs:556-570`).
- Mutation: `ChangeSnow(delta)` + `RPC_SetSnow` (`WearNTear.cs:538-554`). `AddPreSnow()` makes a piece spawn fully snowed (`WearNTear.cs:879`); `ZoneSystem` sets `s_preSnow = true` on **every** location-spawned WearNTear, world-wide (`ZoneSystem.cs:1712-1713`).
- Players brush snow off pieces they walk past, Deep North only (`Character.cs:1195-1208`, using `m_snowWalkPieceDist/Radius/Min/Remove` at `Character.cs:309-312`).

Shader: **`Valheim/Snow Mesh`**, shipped at `Assets/Shaders/SnowMesh.asset`. Properties:
`_TintColor _MainTex _BumpMap _TextureSize _SnowLevel _SnowLevelStrength _ScaleOutFactor
_ScaleOutPoint _ObjectCenterHeight _BumpScale _Glossiness _OffsetNoise _OffsetNoiseChannel
_OffsetNoiseSize _OffsetNoiseStrengthLow _OffsetNoiseStrengthHigh`. Six shipped materials use it:
`snow`, `snow_flat`, `snow_flat_low`, `snow_flat_low2`, `snow_rooftop`, `snow_wall`
(`Assets/GameElements/Pieces/_res/`). This is the exact shader to reuse - we must not build a
lookalike.

### 1.3 Character snow cover

`VisEquipment` drives `_SnowCover` per-object through `MaterialMan`
(`VisEquipment.cs:1134-1147`). The `SnowLevel` setter has **no caller in the assembly** -
authored-for-later. It is a free hook if we want snow on players and NPCs.

### 1.4 Snow props

- `SnowDestruction` - destructible snow piles, `Damage(strength)` rolls against `m_destructionChanceMultiplier` (`SnowDestruction.cs`).
- `SnowRoller` - rolling snowballs that grow with deep-snow depth (`SnowRoller.cs`).

### 1.5 Snow glint

`EnvMan` publishes eight globals every frame: `_SnowGlintOffsetNoise` (Texture3D),
`_SnowGlintSize/Density/Strength/NoiseDensity/NoiseAmount/ViewAmount/TimeAmount`
(`EnvMan.cs:53-61`, `:116-123`, `:195-202`). The shipped `Heightmap_basematerial.mat` also carries
per-material copies (`_SnowGlintStrength: 4`, the rest at EnvMan's defaults).

### 1.6 MaterialMan is the per-renderer property owner

`MaterialMan.PropertyContainer.UpdateBlock` calls `m_propertyBlock.Clear()`, refills it from its
own dictionary, and pushes it to **every** MeshRenderer and SkinnedMeshRenderer under the
GameObject (`MaterialMan.cs:122-153`). One shared `MaterialPropertyBlock` instance for the whole
game. Consequence: any `renderer.SetPropertyBlock(...)` we do on a piece is erased the next time
MaterialMan touches that object, and our write erases vanilla's `_SnowLevel`, `_Color` and
`_EmissionColor` (`WearNTear.cs:303-304`, `:569`). **The only safe way to tint or snow a piece is
`MaterialMan.instance.SetValue` / `ResetValue`.**

### 1.7 Two vanilla bugs to route around, not "fix"

- `TerrainComp.SetSnowMask` samples at `world.y` instead of `world.z` (`TerrainComp.cs:791`). Pass an explicit depth and it does not matter.
- `EnvMan` calls `IsDeepnorth(position.x, position.y)` - camera Y as world Z (`EnvMan.cs:423`, `:490`). Patching it would change weather selection for every player; leave it alone and do not copy it.

### 1.8 Gap in our references

`FiresNPCs\FiresNPCs\Debug\DebugRefs\Heightmap.shader` is a **pre-1.0** dump: it has
`_LodHideDistance` where 1.0 has `_LodHideModifier`, and no `_SnowGlint*` at all. The 1.0
`Custom/Heightmap` exists only as compiled bytecode
(`ValheimRip1.0\Assets\Shaders\Heightmap.asset`, 30 `_SnowGlint` hits, so it does read the
globals). **How 1.0 renders the raised snow surface on terrain is unverified** - whether the
depth comes from the tessellation/displacement path or from a shell offset is not readable from
the property list. Section 4 gates the terrain-shader work on closing this.

---

## 2. What we have

| # | Where | Mechanism | Verdict |
| --- | --- | --- | --- |
| 2.1 | `FiresTossinShade\...\Rendering\FiresDeepSnow.cs` (536 lines) | Parallel deep-snow system: `Custom/FiresHeightmap` raises a drift-noise snow shell above collision ground gated by Mountain/DeepNorth weight; two 1024 R8 player-centred RTs (`_FiresSnowTrampleTex`, `_FiresSnowDriftTex`) for walked trails and drift against `Piece` footprints; ~18 `_FiresSnow*` shader properties; config section "21 - Deep Snow" | **Conflicting** (DN half) / **still needed** (Mountain half) |
| 2.2 | `FiresTossinShade\...\Rendering\FiresHeightmapShaderLoader.cs` | Swaps every `Custom/Heightmap` material to `Custom/FiresHeightmap`, a **reconstruction of the pre-1.0 shader**. Config section 17 | **Conflicting** |
| 2.3 | `FiresAdminTerrain\...\VoxelTerrain\FiresWorldGenBase.cs:4005-4020` | Harmony Prefix on `WorldGenerator.GetBiomeHeight` sets `mask = SampleLavaMask(wx, wy)`, which returns `Color.clear` for every biome except AshLands (`:544-558`) | **Conflicting - hard breakage** |
| 2.4 | `FiresAdminTerrain\...\ZoneMode\ZoneModeGate.cs:253-283` | Gated `GetBiomeHeight`: closed zones return `ClearMask = (0,0,0,0)`; coast band lerps the real mask toward it | **Conflicting** |
| 2.5 | `FiresAdminTerrain\...\Persistence\TileTerrainOpBlocker.cs:245-263` | `MapPaintTypeToColor` handles Dirt / Cultivate / Paved only; `DeepSnow` and `ClearVegetation` both fall through to `(0,0,0,255)` | **Conflicting** |
| 2.6 | `FiresAdminTerrain\...\Biome\BiomePaintPersistence.cs:497-769` | Writes `TerrainComp.m_paintMask` by reflection with **absolute** colours (`OverwriteVanillaPaintMask`, `m_paintMaskPaved`, `Color.black` init) | **Conflicting** |
| 2.7 | `FiresAdminTerrain\...\CustomTerrain\Snow\` - `SnowClutterConfig`, `SnowGroundMesh`, `SnowCommands`, `SnowTrailController`, `SnowTrailPaint` (1635 lines) | Fake deep snow out of ClutterSystem instances: cloned instanced-grass prefab + hex ground discs + a 16x128 triangle-particle mound mesh; runtime trail stamping hides instances, calls `ClutterSystem.ResetGrass` and adds non-persistent Mountain-biome paint. Registered at `FiresAdminTerrain.cs:308` | **Redundant** |
| 2.8 | `FiresAdminTerrain\...\CustomWater\Ice\Land\` - `LandSnowAnchor`, `LandSnowAnchorPrefab`, `LandSnowCombiner`, `LandSnowCommands`, `LandSnowController`, `LandSnowFootstepPatch` | Admin-placed snow as ZDO-anchored mesh disks (`VALandSnow`), combined and skirted like the ice system. Registered at `FiresAdminTerrain.cs:341` | **Still needed** |
| 2.9 | `FiresNPCs\...\Territories\TerritorySnowManager.cs` (302) + `MaterialVariantSystem.cs` | Per-territory fake piece snow: `Physics.OverlapSphere` for pieces, `WearNTear.HaveRoof()` check, then raw `renderer.SetPropertyBlock` lerping `_Color` toward white. Wired at `TerritoryRuntime.Environment.cs:1436-1481` | **Conflicting** |
| 2.10 | `FiresAdminTerrain\...\Utilities\TerritorySnowManager.cs` + `...\Utilities\MaterialVariantSystem.cs` | Same two files, FAT copy, wired at `Bridge\EnvironmentBoxTerrainProvider.cs:82-87` | **Conflicting** |
| 2.11 | `FiresValheimGalaxies\...\Utilities\MaterialVariantSystem.cs`, `FiresVAngarde_PRESTRIP_BACKUP\...\MaterialVariantSystem.cs` | Two more copies of the same file | **Conflicting** (Galaxies) / dead backup (VAngarde) |
| 2.12 | `FiresEasyBakeMeshes\...\MeshBaker.cs` | Combines and GPU-instances live `WearNTear` pieces per zone. No knowledge of `m_snow` / `m_snowWorn` / `m_snowBroken`, of the 1.25 m bounds expansion, or of MaterialMan's per-GameObject `_SnowLevel` registration | **Still needed** (new work) |
| 2.13 | `FiresUnifiedCore\...\Pieces\WearLooks.cs` | Resolves which of `m_new` / `m_worn` / `m_broken` a placed copy shows. Does not model the three snow renderers | **Still needed** (new work) |
| 2.14 | `FiresValcast\...\Modules\WeatherControl.cs` | EnvSetup override by name (SnowStorm etc.) | No change; it now indirectly drives `GetSnowBuildup` |

Not snow, but in the blast radius: `DistantPaintCache`, `TileMeshRenderer`, `TileMeshUnderside`,
`FarQuilt`, `VoxelChunkView` and `VoxelWorld` all bind their own `_ClearedMaskTex` on
`Custom/Heightmap` materials, so whatever G ends up meaning has to mean the same thing there.

---

## 3. Classification

### Redundant - delete outright (no back-compat shim)

- **2.7 the clutter snow stack.** Vanilla now gives depth (`GetCultivationMask`), footprints
  (`Character.m_deepSnowWalkObj`), slow, ground materials and a real sinking surface. Our version
  is instanced grass pretending. `PLAN_SnowIceAnchorMigration.md` section 5.2 already recorded
  Fire's answer as "fold into LandSnowController"; 1.0 makes the fold pointless for Deep North -
  delete the five files, their `FiresAdminTerrain.csproj` entries and the registration at
  `FiresAdminTerrain.cs:303-315`. `SnowTrailPaint`'s dependency on `BiomePaintPersistence`
  disappears with it.
- **2.9 / 2.10 / 2.11 TerritorySnowManager + MaterialVariantSystem.** Vanilla ships real snow
  meshes on the exact shader with a ZDO-backed buildup value and a roof test that is better than
  ours (it also honours ShieldGenerator). Delete all four copies rather than repairing them.

### Conflicting - one side must yield

- **2.3 / 2.4 the `GetBiomeHeight` mask.** On a FiresWorldGenBase world every Deep North point
  gets `mask = Color.clear`, so `g = 0`: no depth, no slow, no snow ground material, no footprint
  trails, no placement gating, no snowballs. This is the single highest-value fix in the plan and
  it is three lines. **Vanilla yields nothing; we yield.**
- **2.5 `MapPaintTypeToColor`.** Painting DeepSnow on a FAT tile currently erases the tile's
  paint. `ClearVegetation` is wrong too - vanilla's is `(0,0,0,0)`, ours `(0,0,0,255)`.
- **2.6 absolute paint writes.** Any `OverwriteVanillaPaintMask` inside Deep North wipes the snow
  field for that area, permanently as far as the player is concerned (terrain is generated, the
  TerrainComp delta is what persists).
- **2.2 the terrain shader swap.** `Custom/FiresHeightmap` was reconstructed from the pre-1.0
  `Custom/Heightmap`. Swapping it onto 1.0 materials drops `_SnowGlint*` and whatever 1.0 added
  for deep-snow rendering. Standing preference is the exact vanilla shader; the swap exists only
  to add Ashlands lava emission. **The swap must yield in snow biomes until it is re-derived from
  the 1.0 shader.**
- **2.1 the Deep North half of FiresDeepSnow.** Two systems raising a snow shell over the same
  ground, with two different depth fields and two different trail stores, will disagree about
  where the surface is - and only vanilla's is the one `GameCamera`, `FloatingTerrain` and
  `Character` read. Ours yields in Deep North.

### Still needed - rebuild on top of vanilla, not beside it

- ~~**Mountain snow.**~~ **NOT WANTED - Fire decided 2026-09-23, see step 7.** This entry was the
  only thing keeping anything of ours alive for Mountain. With it declined, Mountain is pure
  vanilla: no depth, no sink, no slow, no footprint trails. It is also what made retiring
  `FiresDeepSnow` (step 6) the whole system rather than half of it.
- **2.8 LandSnow anchors.** Admin-placed drifts anywhere in the world, at arbitrary heights, on
  cliffs - vanilla cannot do that at all. Keep. It should learn to *not* fight vanilla in Deep
  North, and to offer "set the vanilla depth field here" as an alternative to a mesh disk.
- **2.12 EasyBake.** New work: a combined or instanced piece must either keep dynamic snow or be
  excluded from baking when its ZDO `s_snow > 0` / `s_preSnow`.
- **2.13 Core WearLooks.** New work: teach it the three snow renderers so FAP blueprints and FAT
  bakes reproduce the right look.
- **Character snow cover.** `VisEquipment.SnowLevel` is an unused vanilla hook. Optional, cheap,
  and belongs in Core per the shared-code rule.

---

## 4. Order of work

Each step is small, independently checkable, and ordered so that nothing later depends on an
unverified assumption.

### Step 0 - decide G-channel ownership (blocking, Fire's call)

Option A / B / C in section 0. Everything below assumes A.

### Step 1 - stop destroying the snow field (FAT, source + diff only) - DONE 2026-09-23, source only, not built

Fire chose Option A on 2026-09-23.

1. NEW `Modules\VoxelTerrain\DeepNorthSnowMask.cs` - reproduces
   `WorldGenerator.GetDeepNorthHeight`'s depth term operation for operation (offset 100000 +
   `m_offset3` in double then narrowed, `DUtils.Fbm(p * 0.01f, 3, 2.0, 0.5)` narrowed to float,
   then `0.3f + (fbm + 1f) / 2f * 0.3f`). `m_offset3` is public in
   `assembly_valheim_publicized.dll`; `DUtils` comes from the already-referenced `assembly_utils`.
   Added to `FiresAdminTerrain.csproj`.
2. `FiresWorldGenBase.Patch_WorldGenerator_GetBiomeHeight_FiresGen.Prefix` - the mask is now
   `biome == DeepNorth ? DeepNorthSnowMask.Sample(...) : SampleLavaMask(...)`. Nothing else
   changed; the prefix already declared `ref Color mask` (correct per the out-param clobber rule)
   and is the only patch on that method in the whole tree.

**`ZoneModeGate` needed no change - the original plan item was wrong.** Its bypassed
`GetBiomeHeight` calls still run the FiresWorldGenBase prefix (`t_bypassDepth` only suppresses the
gate's own view), so `realMask` is repaired by item 2. The closed-zone `ClearMask` and the
coast-band `Color.Lerp(realMask, ClearMask, fall)` are both correct as written: a closed zone is
sea floor with no land to snow, and the coast band fades snow out exactly as the land drops into
the water, reaching `fall = 0` (full `realMask`) at the open edge.

**Flagged, NOT changed - needs Fire's call.** The same line returns `Color.clear` for every
non-Ashlands biome, where vanilla's base is `Color.black`. The difference is the ALPHA:
`Heightmap.GetVegetationMask` is `m_paintMask.a`, and `ClutterSystem` filters on it
(`m_minVegetation` / `m_maxVegetation`, `ClutterSystem.cs:313-317`). Fires worlds therefore report
vegetation 0 everywhere, where vanilla reports 1 outside Deep North / Ashlands / Mistlands. The
existing comment shows the `Color.clear` was a blanket fix for Ashlands reading alpha as lava; the
vanilla-faithful shape is per-biome (`black` normally, lava alpha in Ashlands, snow green in Deep
North). `IsCleared` is unaffected - it reads r/g/b, not alpha. Worth one check against the clutter
prefabs before changing anything.

**In-game check (Fire):** on a Fires-generated world, stand in Deep North on untouched ground.
`Character.m_deepSnowSlowMax` must produce a "Snow slow" row in the test list (needs
`Terminal.m_showTests`), footprint objects must spawn as you walk, and the camera must lift.
Before the fix none of those happen. A screenshot of the same spot before and after is the proof.

### Step 2 - make DeepSnow paint representable end to end (FAT, source + diff only) - DONE 2026-09-23, source only, not built

The survey for this step found the vanilla-mask side in better shape than the plan assumed, and
the tile side worse. What was actually wrong: `TerrainModifier.PaintType.DeepSnow` had no
representation anywhere in FAT, so it fell through every mapping to "no paint".

1. `FiresUnifiedPaint.ColorToPaintType` - added `DeepSnow`, tested first. It is the only canonical
   mask with every colour channel high, so under `Match`'s +/-8 tolerance nothing else can swallow
   it and it cannot swallow anything else. Without this, a `(255,255,255,255)` brush colour
   returned null and the stroke never reached the vanilla side at all.
2. `TileTerrainOpBlocker.MapPaintTypeToColor` - added `DeepSnow -> (255,255,255,255)` and
   `ClearVegetation -> (0,0,0,0)`. Both previously fell through `default` to `(0,0,0,255)`, so a
   vanilla DeepSnow or ClearVegetation op routed over a FAT tile ERASED that tile's paint.
3. `TileMeshRenderer.RebuildPaintMaskAndBind` - the legacy auto-heal promoted any all-zero texel
   to `(0,0,0,255)`, which also swallowed a deliberate `m_paintMaskClearVegetation`. The heal is
   now gated on `PaintModified[i]`, so untouched vertices still heal (the barren-tile fix it was
   written for) but a painted `(0,0,0,0)` survives. `ApplyClearToTile` already resets `Paint` and
   `PaintModified` together, so an erased vertex heals again. This is what makes item 2's
   `ClearVegetation` mapping mean anything on a tile.
4. `EnvironmentBoxClutterSpawner` - `GetPaintColor` had no `DeepSnow` case (fell to
   `m_paintMaskNothing`) and `GetPaintTypeAt` tested `r > 0.5` first, which reads `(1,1,1,1)` as
   Dirt. Both fixed, so the box's own mask round-trips the type.

No tile-side Deep North branch is needed: vanilla's snow consumers read
`Heightmap.GetCultivationMask` off the VANILLA heightmap, never off a FAT tile mask, so a tile's G
channel is purely the shader's cultivated texture and carries no snow semantics.

**`BiomePaintPersistence` needed no change - the original plan item was wrong on both counts.**

- `PaintVanillaHeightmapMask` really does write `m_paintMaskPaved` over Deep North and zero G
  (its own comment names DeepNorth in that branch), but it is **dead code**: `private`, and the
  only references anywhere in the tree are comments in `LavaPlacement` and `TileBrushCommands`.
  Flagged for deletion, not repaired.
- `OverwriteVanillaPaintMask` is the live `va_brush_paint` path over vanilla terrain, and its
  absolute overwrite is vanilla-consistent. In `TerrainComp.PaintCleared` the channel-preserving
  `isChangingSnow` rule applies to **Cultivate only**; Dirt, Paved, Reset and ClearVegetation all
  lerp every channel, so vanilla itself clears snow when you pave or reset in Deep North. An
  admin typing an explicit RGBA gets what they typed.
- The live paint path that matters, `FiresUnifiedPaint.MirrorPaint`, drives vanilla's own
  `DoOperation` -> `PaintCleared` on the comp owner, so it already inherits `isChangingSnow`
  including the additive Cultivate-as-snow-brush behaviour. Nothing to port.

**Flagged, NOT changed.** `FiresUnifiedPaint.Match` compares R, G and B only. `m_paintMaskNothing`
(0,0,0,1) and `m_paintMaskClearVegetation` (0,0,0,0) differ solely in alpha, so ClearVegetation
cannot be classified on the vanilla side and is deliberately absent from `ColorToPaintType`.
Adding an alpha term would reclassify existing non-canonical-alpha brush colours from a paint type
to null (tile-only), which is a behaviour change on every paint command. Fire's call.

**In-game check:** on vanilla Deep North ground, `va_brush_paint 255 255 255 255` - the stroke
should now mirror to the vanilla heightmap as deep snow (deeper sink, snow footsteps) instead of
being swallowed as a tile-only stroke. Over a FAT tile, run a vanilla DeepSnow or
ClearVegetation TerrainOp: the tile keeps its paint instead of going blank, and a
ClearVegetation stroke leaves grass-free ground that survives leaving and re-entering the zone.
Then paint dirt and paved on Deep North and confirm they still clear snow, which is vanilla's own
behaviour and not a regression.

### Step 3 - delete the clutter snow stack (FAT + Galaxies, source + diff only) - DONE 2026-09-23, source only, not built

Deleted `Modules\CustomTerrain\Snow\` entirely:
`SnowClutterConfig.cs`, `SnowCommands.cs`, `SnowGroundMesh.cs`, `SnowTrailController.cs`,
`SnowTrailPaint.cs` (1635 lines) and the folder's own `SNOW_CLUTTER_PLAN.md`, which documented the
deleted system and survives in three `CodeBackups` snapshots.

Also removed, because nothing else referenced them:
- the five `<Compile Include>` entries in `FiresAdminTerrain.csproj`;
- the registration block in `FiresAdminTerrain.cs` plus its `snowCmdsOK` tally and the
  `commandSetsRegistered` term that summed it;
- six `ClutterManager` members that existed only for the snow stack and were all no-op stubs:
  `InvalidateCache`, `ShowAllHiddenClutter`, `HasSnowClutterNear`, `HideClutterInRadius`,
  `ClearAllClutter`, `ClearClutter`. Verified: the `ClearAllClutter` hits elsewhere in FAT are the
  unrelated instance method on `EnvironmentBoxClutterSpawner`, and the `InvalidateCache` hit is
  `SaveSystem.InvalidateCache`.
- the class-header bullet in `ClutterManager` describing snow-trail behaviour.

Console commands that disappear: `paint_snow_clutter`, `clear_snow_clutter`, `snow_trail`.

`ClutterManager.ClearAllPainted` is now uncalled but was left in place: unlike the six above it is
not labelled snow-only, and the class header marks it as a Phase D5 placeholder.

`SnowTrailPaint` only consumed the public `BiomePaintPersistence.BiomePaintData` type, which has
other users, so nothing in `BiomePaintPersistence` became dead.

**`FiresValheimGalaxies` done too (2026-09-23, on Fire's say-so).** Galaxies is a FAT fork and
carried the same five files under `Modules\CustomTerrain\Snow\` (no `SNOW_CLUTTER_PLAN.md`), the
same five csproj entries, the same registration block in its own `FiresAdminTerrain.cs` (with the
tally named `commandSubsystems` rather than `commandSetsRegistered`), and the same six
`ClutterManager` stubs. All removed identically; a symbol sweep over its `.cs` and `.csproj` comes
back clean. Its `ClearAllPainted` was likewise already uncalled and was likewise left alone.

Both copies also lost a stale `ClutterManager` header line naming "the Snow trio" as a reason the
shim class still exists.

**In-game check:** `paint_snow_clutter` and `snow_trail` are gone from the console; no snow
clutter appears anywhere; Deep North still looks and walks correctly (that is Step 1's snow, not
ours).

### Step 4 - delete the fake piece snow (FiresNPCs, FAT, Galaxies, Core) - DONE 2026-09-23, source only, not built

`TerritorySnowManager.cs` + `MaterialVariantSystem.cs` deleted from all three live copies
(FiresNPCs `Modules\Territories\`, FAT and Galaxies `Modules\CustomTerrain\Utilities\`) with their
six csproj entries. `FiresVAngarde_PRESTRIP_BACKUP` still has a copy and was left alone - it is a
backup tree, not a live mod.

Call sites removed:
- **FiresNPCs** `TerritoryRuntime.Environment.cs` - `UpdateSnowForPlayer` and its call from the
  per-player effects tick, `ClearSnowObjectCache` and its call from
  `InvalidateSnowTerritoryCache`, and `ApplySnowToObjectsInTerritory` which was **already dead**
  (declared, never called). Four fields went with them, all provably unreferenced:
  `_snowAppliedObjects`, `_lastSnowObjectScanTime`, `SnowObjectScanInterval`,
  `_snowAmountPropertyId`, `_addSnowPropertyId`. `_cachedSnowMaskTerritories` and its builder are
  KEPT - they feed the terrain-snow vertex-colour postfix, which is a different mechanism.
- **FAT** `EnvironmentBoxTerrainProvider` - the `ApplySnowToPieces` / `RemoveSnowFromPieces`
  implementations, the `VerdantsAscent.Terrain.Utilities` using, and the snow mentions in the
  class doc.
- **Galaxies** - files and csproj entries only; it had no call sites at all, so its copies were
  already dead weight.

**This removed public API from FiresUnifiedCore.** The whole Core env-box snow seam existed only
to reach `TerritorySnowManager`, so leaving it would have meant exactly the dead-stub pattern
step 3 just cleaned out of `ClutterManager`. Gone from Core:
`IEnvironmentBoxTerrainProvider.ApplySnowToPieces` / `.RemoveSnowFromPieces`, the two
`EnvironmentBoxBridge` forwarders, and `EnvironmentBoxController.ApplySnowToBuildingPieces` /
`.RemoveSnowFromBuildingPieces` with their calls from `OnPlayerEnter` / `OnPlayerExit`. FAT was
the only implementer and Core itself the only caller, both in-tree. Core's version bump is the
Core owner's call, not made here.

**FOLLOW-UP 2026-09-23 22:16 - FAT carries two temporary no-op stubs that must be swept.** FAT
compiles against Core's SHIPPED DLL (`csproj` HintPath `..\..\FiresUnifiedCore\FiresUnifiedCore\
bin\Release\FiresUnifiedCore.dll`), not against Core source. That DLL was built 22:06, four
minutes before the interface shrank, so it still declares seven members and FAT failed to build
with CS0535. Session `[9d309d]` re-added `ApplySnowToPieces` / `RemoveSnowFromPieces` to
`EnvironmentBoxTerrainProvider` as documented empty methods to unblock itself - the right call,
and harmless either way.

**CLOSED 2026-09-23 22:47.** Core 0.2.87 built 22:45:34 and deployed 5/5; its shipped DLL now
declares exactly five interface members and contains no `ApplySnowToPieces` string at all
(verified against `bin\Release\FiresUnifiedCore.dll`, not against source). `[9d309d]` swept both
stubs and their comment block at 22:47:09. `EnvironmentBoxTerrainProvider` is back to the step 4
state, braces balanced. Nothing left to do here.

This is the compile-time face of the same ordering rule in `MODSTACK_COORDINATION.md`: the
runtime hazard is a rebuilt FAT loading against old Core (TypeLoadException on the provider); the
compile-time hazard is FAT building against old Core at all (CS0535). Both say Core first.

**Feature loss, stated plainly.** Territories and env boxes no longer whiten building pieces at
all. Vanilla's real piece snow is Deep North only (`WearNTear.cs:407`), so a snow territory in
Meadows now shows nothing on pieces. The replacement is cheap and is open question 4:
`WearNTear.ChangeSnow` and `AddPreSnow` are **not** biome-gated - only the accumulation loop is -
so a Core helper that calls them over a bounds gets real `Valheim/Snow Mesh` geometry anywhere in
the world, ZDO-backed and multiplayer-correct, which is strictly better than the tint ever was.

**Deliberately NOT touched.** Both Core's `ApplyBiomeShaders` and FiresNPCs'
`ApplyTerritoryEffects` set the globals `_GlobalSnowAmount`, `_SnowAmount` and `_AddSnow`. That is
a different mechanism from the MPB tint and partly real: `_AddSnow` is a genuine shipped property
on `Custom/StaticRock`, `Custom/Vegetation` and `Yggdrasil_root` (checked across all 48 shader
assets in the 1.0 rip). `_GlobalSnowAmount` and `_SnowAmount` appear in no 1.0 shader and in no
1.0 assembly code - those two writes are dead. Worth a separate look: because `_AddSnow` is a
declared material property, a material that serializes its own value ignores the global, so the
rock/tree snowing is at best partial today.

**In-game check:** in Deep North, build an unroofed wall and a roofed one and wait out a snow
environment. The unroofed one grows real snow geometry that gets brushed off when you walk past
it (`m_snowWalkPieceRemove`); the roofed one stays clean. Vanilla's support-colour red/green
overlay must still work - that is the `_Color` / `_EmissionColor` pair our old code was
clobbering.

### Step 5 - make the terrain shader swap safe (FTS) - DONE 2026-09-23, SHIPPED in FTS 0.3.144

`FiresHeightmapShaderLoader` now holds Mountain and Deep North zones at the vanilla shader.
Mountain is included from the start rather than waiting on step 7: vanilla 1.0's `_SnowGlint*`
globals apply to all snow terrain, not only Deep North, so the reconstruction loses them on
Mountain too.

- New `SnowBiomes` const = `DeepNorth | Mountain`. `Heightmap.Biome` is a flags enum
  (`Mountain = 4`, `DeepNorth = 64`), so this is one `HaveBiome` call per zone.
- New `RebuildSnowMaterials()` walks `Heightmap.s_heightmaps` and collects the instance id of each
  zone's `m_materialInstance` whose heightmap `HaveBiome(SnowBiomes)`. `ApplyToAllTerrain` skips
  any material in that set, alongside the existing distant-LOD and FAT-tile skips.
- **Unclassified counts as snow.** `Heightmap.Initialize` creates `m_materialInstance` *before*
  `Heightmap.Generate` fills `m_cornerBiomes` from the build data, so a heightmap with
  `m_buildData == null` cannot be classified yet. A wrong "not snow" answer swaps that zone
  permanently, while a wrong "snow" answer only defers to the next pass - and the driver re-scans
  about once a second. So the gate blocks until it knows.
- The set is rebuilt each pass rather than cached per material, and the gate is only consulted at
  the swap decision, which is sound because a zone's corner biomes never change after generation.
- `LastSkippedSnow` added beside the existing counters; `va_terrain status` now reports
  `snowZones=N (Mountain/DeepNorth held at vanilla)`.
- The section 17 config description now says Mountain and Deep North stay vanilla and why, so the
  behaviour is visible without reading code.

Nothing the swap exists for is lost: its whole purpose is Ashlands lava emission.

Shipped in FTS 0.3.144 (built 22:54:53, SHA FBE87BD8..., 3/3 targets verified). Dedi-safe:
`RebuildSnowMaterials` only runs inside `ApplyToAllTerrain`, which is gated on `Enabled` (section
17 is off by default), and every dereference in it is null-checked - `s_heightmaps`, the heightmap,
and `m_materialInstance`. `HaveBiome` reads `m_cornerBiomes`, which is initialised at its
declaration, so it cannot throw on an ungenerated zone. This matters because FTS does ship to the
plain dedi (see below), contrary to its own "client-only" description.

**Still gated, unchanged:** re-deriving `Custom/FiresHeightmap` from the 1.0 `Custom/Heightmap`
needs `Tools\Build-ShaderDossier.ps1 -Shader "Custom/Heightmap"` against 1.0 dumps plus a RenderDoc
capture in Deep North. Until that lands we cannot say how vanilla renders the raised snow surface
(section 1.8), and reconstructing blind is the lookalike the project rule forbids. This step buys
the time to do it properly rather than doing it.

**In-game check:** with FTS section 17 on, stand at a Deep North / Mountain border. Ashlands lava
glow still works where it should; snow terrain keeps vanilla glint and the raised deep-snow
surface, and there is no shader seam that moves as you walk. `va_terrain status` should report a
non-zero `snowZones` while you are in or near snow country, and zero out in Meadows.

### Step 6 - retire FiresDeepSnow ENTIRELY (FTS) - DONE 2026-09-23, SHIPPED in FTS 0.3.146

**The step as written was impossible, and that changed the decision.** It assumed the Deep North
half could be peeled off and the Mountain half kept. It cannot: the shell's biome gate lives in
`Custom/FiresHeightmap`, keyed on the green vertex-colour channel, and
`Heightmap.GetBiomeColor` returns `(0,255,0,0)` for **both** `Mountain` and `DeepNorth`
(`Heightmap.cs:443-452`) - byte-identical. The shader cannot tell them apart and the bundle cannot
be rebuilt here. So it was all-or-nothing. Fire chose to retire it.

**It was already unreachable.** Step 5 holds Mountain and Deep North at the vanilla shader, and
those two biomes are exactly the set the green gate covers. From FTS 0.3.144 onward
`FiresDeepSnow` could not render a single pixel anywhere - while still running, on every client,
every frame: two 1024 R8 RenderTextures, trample stamps every 0.05 s, a heal pass every 4 s, and a
drift rebuild scanning up to 256 `Piece`s every 2.5 s, with its config defaulting ON. Pure cost
for zero output. Its own `LateUpdate` gates on `!Application.isBatchMode`, so the cost was
client-side only.

Deleted: `Modules\CustomWater\Rendering\FiresDeepSnow.cs` (536 lines, 18 config entries forming
config section "21 - Deep Snow"), its `<Compile Include>`, the `FiresDeepSnow.Bind(Config)` call
and the `AddComponent<FiresDeepSnow>()` line. Verified afterwards: no `FiresDeepSnow` or
`_FiresSnow*` reference remains in any `.cs` or `.csproj`, braces balanced, csproj still valid XML.
The config section vanishes with the file because all 18 binds lived inside it.

**Residual risk, stated rather than hidden.** The class pushed "look knob" globals
(`_FiresSnowTint`, `_FiresSnowBright`, `_FiresSnowSmooth`, `_FiresSnowSparkle`,
`_FiresSnowPackedTint` and friends) unconditionally, with a comment warning that leaving them
unset at 0 "would blacken snow". Nothing pushes them now. The reasoning that this is safe: those
globals modulate snowy pixels **by snow weight**, snow weight is the green vertex channel, step 5
holds every green-channel zone at the vanilla shader, and a zone with no Mountain/DeepNorth corner
has green = 0 across all its vertices - so every surface still wearing `Custom/FiresHeightmap`
multiplies them by zero.

That chain is read off the C# and vanilla's `GetBiomeColor`, **not** off the shader itself. The
bundle is LZ4-compressed inside the DLL (`UnityFS` + `LZ4` present; even `_ClearedMaskTex`, which
the shader certainly declares, is not findable as a raw string), so no string search can confirm
which shaders read these globals. Treat the in-game check below as the real verification.

**In-game check:** with section 17 ON, stand in Meadows or Plains and confirm terrain is unchanged
- no darkening, no black patches. Then walk to a Mountain or Deep North border: snow terrain is
vanilla (step 5) and should show vanilla glint and depth, with no seam that moves as you walk.
Console should no longer show `[FiresDeepSnow] ACTIVE`, and config section "21 - Deep Snow" should
be gone. If anything darkens, the look-globals chain above is wrong and the fix is to push their
identity defaults once at startup.

### Step 7 - Mountain deep snow - SKIPPED 2026-09-23 on Fire's decision

Not wanted. Mountain keeps vanilla 1.0's behaviour, which means no deep snow there at all: no
sink, no slow, no footprint trails, no snowballs. Deep North remains the only biome with depth,
exactly as Iron Gate shipped it. Nothing to build, and the plan is closed on this.

Had it gone ahead it would have widened `GetHeightOffset`, `GetGroundMaterial` and `Character`'s
deep-snow block to Mountain off the same G channel. Recorded only so a later reader knows the
route was considered and declined, not overlooked.

**DO NOT revert step 5's Mountain gate because of this.** They are unrelated, and the gate looks
like step-7 scaffolding if you only read the biome list. Step 5 holds Mountain at the vanilla
terrain shader because `Custom/FiresHeightmap` is a **pre-1.0 reconstruction** that lacks the
`_SnowGlint*` globals EnvMan publishes for all snow terrain - Mountain included. That reason is
about vanilla 1.0's own glint, not about anything of ours, and it survives this decision
untouched. The gate comes out only when the shader is re-derived from the 1.0 original.

### Step 8 - bakes and blueprints (EBM, Core, FAP) - DONE 2026-09-23, source only, not built

Two files. FAP needed none - it already routes blueprint capture through `WearLooks.IsShown`
(`VAFapBlueprintMeshBaker.cs:564`, `:593`), so the Core fix reaches it for free, which is why the
fix went to Core rather than into each consumer.

**`FiresEasyBakeMeshes\EasyBake\SkipEligibility.cs`** - a snow cap is not static, so a snow-capable
piece cannot stay uncreated where snow reaches it. Both failure directions were real: a skipped
piece never runs `UpdateSnowVisual` and sits bare while live neighbours whiten, and a piece baked
*with* snow wears it forever because a combined mesh has neither the switchable renderers nor the
per-object `_SnowLevel`. Added `PrefabInfo.CanShowSnow` (`wear.m_snow != null`, so nearly every
prefab is unaffected and pays nothing), a `SnowReaches(zdo)` gate in `ZdoMatches`, and a matching
branch in `DescribeMismatch` so `ebm_census` explains the refusal in words.

The trap inside it: the two ZDO keys need different treatment. `s_snow` is only ever written inside
`WearNTear`'s DeepNorth branch, so it scopes itself. `s_preSnow` does **not** - `ZoneSystem.cs:1712`
sets it on every location-spawned WearNTear in the world and only Deep North consumes it, so
gating on it alone would have held every location piece on the map out of the bake. It is guarded
with `WorldGenerator.IsDeepnorth`, which is a world angle and a magnitude, no noise sampling, and
static.

**`FiresUnifiedCore\Pieces\WearLooks.cs`** - `IsSwitchedOn` fell through to `node.activeSelf` for
anything that is not a wear model, so a prefab shipping its snow caps enabled would have baked them
into every blueprint and every combined mesh permanently. `WearNTear.Awake` switches all three off
unconditionally and only `UpdateSnowVisual` turns one back on, so for a copy reproduced from a look
alone - no ZDO, no buildup - off is the faithful answer. Returns false for the three snow renderers
now. **This is shared Core API surface; whoever takes 0.2.89 carries it.**

Verified rather than assumed: every API used is public in the NON-publicized `assembly_valheim.dll`
that EBM references (`m_snow`/`m_snowWorn`/`m_snowBroken` are `public MeshRenderer`, `ZDOVars.s_snow`
and `s_preSnow` are `public static readonly int`, `IsDeepnorth` is `public static`).

**CROSS-MOD DEPENDENCY, recorded 2026-09-24 after `[6f2d07]` flagged it.** `WearLooks.IsSnowCap` returns
false for the three snow renderers UNCONDITIONALLY. That is a statement that a baked or blueprinted copy
never shows snow - and it is true only because EBM refuses to bake a snow-capable piece where snow can
reach it (`PrefabInfo.CanShowSnow` + `SnowReaches` in `SkipEligibility`). **Those two halves live in
different mods and nothing links them but intent.** If the EBM gate is ever relaxed, or if vanilla's
ZDO-backed snow ever needs to reach a baked piece rather than a live one, `IsSnowCap` is where it breaks -
and it breaks SILENTLY, as a permanently bare piece, not an error. Left as constant-false deliberately: it
is correct while the gate holds, and speculative machinery for a case that does not exist would be worse.
Recorded here so it is discoverable from the Core side, which is where someone would be standing.

**FAP follow-up, not a code change:** existing `.bakebundle` / `.bpghost` caches keep the looks they
were baked with, so a blueprint captured from a snowed piece keeps its snow until `fap_rebake_all`.
The ghost cache is ~9.9 GB on SDGValheim-repro, so that is the FAP owner's call, not this step's.

2. ~~`WearLooks`: model the snow renderers.~~ Done above.
3. ~~Check FAP blueprint capture.~~ Covered by the Core fix; only the cache rebake remains.

Original notes kept below for the record:

1. `MeshBaker`: exclude pieces whose ZDO has `s_snow > 0` or `s_preSnow`, or carry the snow
   renderers and the `_SnowLevel` MaterialMan registration through the bake.
2. `WearLooks`: model `m_snow` / `m_snowWorn` / `m_snowBroken` alongside the wear models.
3. Check FAP blueprint capture against `c_ExtraSnowMeshBoundsHeight` and the prefab's snow
   renderers being active at capture time.

**In-game check:** build a snowed structure in Deep North, let EBM bake the zone, then walk past
it. Snow must not freeze at the baked value or vanish; a blueprint of it must place with the same
look.

### Step 9 - character snow cover (Core) - DONE 2026-09-23, source only, not built

NEW `FiresUnifiedCore\Appearance\CharacterSnowCover.cs` + its `<Compile Include>`.

`VisEquipment` has a public `SnowLevel` setter that clamps 0..1 and pushes `_SnowCover` to every
renderer under the character through `MaterialMan` (`VisEquipment.cs:1134-1147`) - and **nothing in
the entire assembly calls it**. The shader work shipped inert. A Harmony postfix on
`Character.CustomFixedUpdate` now drives it from the same conditions vanilla uses for pieces:
Deep North only (`WearNTear.cs:407`), not under a roof (matching `CanHaveSnow`'s `!m_haveRoof`), at
`EnvMan.GetSnowBuildup() * Game.m_snowBuildupSpeed`, so gear whitens in step with the structures
around it and a clear Deep North night adds nothing to either.

Where it deliberately differs from pieces, and why:
- **Shedding.** Pieces never shed on their own; a character who walks indoors or leaves the biome
  must, or the cover would be permanent. Caught while re-reading: shedding must **not** read
  `GetSnowBuildup()`, because that is zero in fair weather, so a character who whitened in a
  blizzard and then walked somewhere clear would have kept it forever - the step would be zero.
  Shedding runs at the unscaled `m_snowBuildupSpeed` instead, which equals the gather rate at full
  snowfall, so the two directions stay comparable without shedding depending on the sky.
- **No ShieldGenerator test**, which `CanHaveSnow` applies to pieces. Anyone inside a shielded base
  already reads as roofed, and a per-character shield query every tick is not worth its cost.

Visual only, client only: `_SnowCover` is a material property, nothing reaches a ZDO or a peer, and
it no-ops under `Application.isBatchMode` and when `MaterialMan.instance` is null, so a headless
server does nothing. `CharacterSnowCover.Enabled = false` drives the level back to zero once and
then leaves gear alone.

Two things verified rather than assumed:
- `Character.CustomFixedUpdate`'s parameter is named `dt`, not `fixedDeltaTime`, and **Harmony
  binds by name** - the first draft would have silently failed to bind.
- The patch targets the base method, so `Player` must not override it and `Humanoid`'s override must
  chain: confirmed `Player` has no override and `Humanoid.CustomFixedUpdate` calls
  `base.CustomFixedUpdate`, so it reaches players and humanoid NPCs both.

**In-game check:** stand in a Deep North snowstorm; gear whitens over roughly the same time a nearby
unroofed wall does. Step under a roof, or walk out of Deep North into clear weather - it clears at a
steady rate either way, including on a calm day. Outside Deep North nothing should ever whiten.

---

## 5. Build and ownership notes

- FAT work is **source and diff only** in this session's scope. Only the dedicated FiresAdminTerrain session builds and deploys FAT.
- Old-style csprojs need an explicit `<Compile Include>` per new file, and the entries for deleted files must be removed.
- No version bumps, no deploys, no branch work as part of this plan.
- Every new source file: ASCII only (`LC_ALL=C grep -n '[^ -~\t]' <file>`), named constants, no rationale comments.

## 6. Open questions for Fire

1. **Section 0**: ANSWERED 2026-09-23 - Option A, vanilla owns G.
2. **Step 1 flag**: ANSWERED 2026-09-24 - Fire said fix it. `FiresWorldGenBase`'s GetBiomeHeight prefix is
   now PER-BIOME: DeepNorth keeps the snow mask, AshLands keeps `SampleLavaMask` (so the molten-crescent bug
   the blanket `Color.clear` was guarding against stays fixed), Mistlands deliberately keeps `Color.clear`
   because vanilla's alpha there is a blend factor we do not reproduce, and **everything else is now
   `Color.black` - alpha 1, matching vanilla**. Verified against the shipped `assembly_valheim.dll`: the
   method opens with `mask = Color.black` and only those three biomes overwrite it.
   Why it mattered: `Heightmap.GetVegetationMask` IS that alpha, and `ClutterSystem` filters on it
   (`m_minVegetation` / `m_maxVegetation`), so alpha 0 world-wide suppressed clutter vanilla spawns.
   FAT source only - the FAT-owner session builds it.
   NOT changed: `ZoneModeGate`'s `ClearMask` (0,0,0,0) for closed zones and the coast fade. Those are sea
   floor where vegetation does not matter, it is zone-mode-only code I do not own, and forcing alpha 1 into
   an Ashlands coast band would reintroduce the molten edge. Flagged rather than touched.
3. **Step 7**: ANSWERED 2026-09-23 - NO Mountain snow. Step 7 skipped, Mountain stays pure vanilla.
4. **Step 4**: do territories still need a snow override? Vanilla covers Deep North only, so a Meadows "snow territory" now shows nothing on pieces. A Core helper over WearNTear.ChangeSnow / AddPreSnow would work anywhere - say the word and I will build it.
5. **Step 5**: is re-deriving `Custom/FiresHeightmap` from the 1.0 shader worth it, or should the Ashlands lava emission move to a narrower mechanism so we stop forking the terrain shader?
