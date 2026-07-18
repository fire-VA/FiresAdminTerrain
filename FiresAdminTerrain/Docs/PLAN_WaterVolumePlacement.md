# Water Volume Placement — Two-Form Water System

> **ROUND-61 ARCHITECTURE PIVOT:** the Water Volume tool now spawns the
> **VANILLA `LiquidVolume`** sim (`FiresLiquidWaterPrefab` = TarLiquid
> clone with `LiquidType.Water` + the vanilla `water_liquid` material)
> instead of the custom grid sim. Rationale: the reference result
> (cookie milk's screenshots) is the vanilla liquid system — real settle
> physics, MeshCollider, native swim via LiquidSurface, ZDO-persisted
> heights (Awake → HaveSavedData ? CheckLoad : InitializeLevels), and
> the shipped `water_liquid` look (Custom/Tar shader, deep blue, gloss
> 0.95). Our grid-sim (§2 below) remains in place powering the SPRING
> and as reference; its swimmable-but-invisible defect (wrapped-material
> rendering on custom combiner meshes) is moot for the Volume tool now.
> Pour control: `m_initialVolume = π·r²·(rulerY − ground)`,
> `m_initialArea ≈ 2r/m_scale`, set pre-Awake via inactive instantiate.
> Repeat pours stack for depth.

## 0. Goal (user spec, 2026-07-09)

Two distinct placement forms for custom water:

1. **Floating water** — the existing draw-a-mesh form. Brush/drag disks
   at the ruler Y; surface stays where you drew it; balloon-shell walls
   bridge down to terrain. Already shipped (rounds W-1 → 55).

2. **Water VOLUME** — new. One click places a *volume* of water defined
   by the IH ruler: radius = ruler circle, fill height = ruler Y
   (height above vanilla or custom terrain). The water then **settles
   into the terrain depression over ~1–2 seconds** (visible flow
   animation) and **"gels"** — viscosity ramps to infinity, the water
   stops flowing and permanently holds its settled shape. It must NOT
   completely run off and vanish on a slope; the gel catches it.

   > Inspiration: vanilla lets you spawn liquid water and freeze it,
   > giving shaped swimmable water. We mimic that: pour → settle → set.

Both forms are swimmable, persisted, and pick up the FiresWater shader
+ Crest-derived upgrades automatically (see §4).

## 1. What the survey found (2026-07-09)

- `WaterMeshCombiner` grid: ≤128×128 cells, `_cellSize` from
  3 verts/m; holds `_surfaceHeights[]`, `_terrainHeights[]`,
  `_waterDepths[]`.
- **Persistence already stores the surface heightfield**, not just
  disks: ZDO anchor (`va_water_grid_data`, `va_water_surface_heights`,
  segments for big grids) + file cache (`WaterCombinerCache`, binary
  v1, `SurfaceHeights[]`/`TerrainHeights[]` per group).
- `RestoreFromAnchorData()` (WaterMeshCombiner.cs:1756) rebuilds the
  mesh **directly from the stored grid — no disk recombination**. This
  is exactly the playback path a gelled volume needs.
- Shader: FAT no longer owns the water shader. `WaterMaterialManager.
  GetWrappedWaterOrRaw()` asks `FiresCore.Services.WaterShaderBridge`
  to wrap the captured vanilla material; FiresTossinShade registers the
  wrapper. FTS installed → FiresWater/Crest look; not installed →
  vanilla. Nothing to do per-feature.
- Pond-mode averaging + gravity edge pull-down are already disabled
  (rounds W-5/55) — surface respects placement Y; the shell wall does
  the visual bridge to terrain.

## 2. Design — the settle sim

New partial `WaterMeshCombiner.Volume` (file
`Modules/CustomWater/Volume/WaterVolumeSettle.cs`) so the sim has
private access to the grid.

### Placement
`CreateSettlingVolume(center, radius, liquid)`:
- `center.y` = ruler Y = fill level.
- Creates ONE bookkeeping `WaterDisk` (delete tools keep working) and a
  DEDICATED combiner — never absorbed into an existing one.
- Bounds = disk bounds + `SETTLE_MARGIN` (8 m) so water can spread
  outward into depressions; grid + terrain sampled up front.

### Sim (per-frame coroutine, ~1.5 s)
- State: per-cell water **column** (m). Init: inside radius →
  `max(0, fillY − terrain)`, else 0. Conserved-volume flow.
- Each frame, N relaxation passes (double-buffered): each cell pushes
  water to lower-surface 4-neighbours,
  `flow = min(column·share, Δsurface·k·viscosity)`.
- `viscosity` ramps 1 → 0 over the settle window (ease-out). That IS
  the gel: early the water pours fast, late it thickens, at 0 it sets.
  On a slope some water escapes downhill (and drains at the grid edge
  = runoff) but the ramp guarantees a pool remains.
- Every 2nd frame: write `_surfaceHeights`/`_waterDepths` from columns
  (`column < 0.02 m` → −100 sentinel = no water) and `BuildWaterMesh()`
  → the visible settling animation.

### Gel
- `_isGelled = true`; final mesh + `SetupMaterial` + `SetupSwimTrigger`
  + VFX + `SaveToAnchor()` (grid persists exactly as drawn-water grids
  already do).

### Grid-authority guards (the important wiring)
- `RegenerateMesh()` / `RegenerateMeshAsync()` / legacy coroutine:
  settling → skip; gelled → `RebuildFromStoredGrid()` (depths from
  stored grids + `BuildWaterMesh` + setups — same shape as
  `RestoreFromAnchorData`'s tail). A gelled surface is data, never
  recomputed from disks.
- `BuildWaterMesh` footprint cull (`IsWithinAnyDiskShape`, MeshGen
  2089/2261): bypass when settling/gelled — settled water legitimately
  lives outside the original disk circle.
- `GetOrCreateCombinerFor`: never absorb new disks into a
  settling/gelled combiner.

## 3. Persistence

- **Anchor**: new independent ZDO key `va_water_is_gelled` (bool).
  Written in `SaveToZDO`; on restore, set the flag BEFORE
  `RestoreFromAnchorData` so any later dirty→regen routes to the grid
  rebuild. Old anchors: key absent → false → unchanged behaviour.
- **Cache**: bump binary version 1 → 2; per-group `IsGelled` bool
  (written after `IsPondMode`). Reader accepts v1 (flag defaults
  false) and v2.
- The known "0 disks found on login" flakiness is a separate verify
  pass AFTER this lands — volume placement leans on the same
  grid-persistence path drawn water uses, so one fix covers both.

## 4. Shader / Crest (no new work in FAT)

- Combiner materials already route through the FUC `WaterShaderBridge`
  → FiresWater shader + Crest lifts (SSS, absorption, foam, meniscus,
  planar reflection) when FTS is installed.
- KNOWN GAP (pre-existing, all FAT water): vanilla only ticks
  `_WaterTime` on `WaterVolume` materials; FAT combiner materials may
  need a `_WaterTime` driver for wave animation parity. Track as a
  follow-up — verify in-game whether the wrapped material animates.

## 5. Phases

| Phase | Content | Status |
| --- | --- | --- |
| V-1 | Settle sim + gel (`WaterVolumeSettle.cs`) | round-56 |
| V-2 | Pipeline guards (regen, footprint cull, absorption) | round-56 |
| V-3 | Persistence: anchor key + cache v2 | round-56 |
| V-4 | `va_brush_water_volume` + YAML "Water Volume" tool | round-56 |
| V-5 | IH ruler contract audit: `height=<h>` on all liquid tools + ring colors (green terrain / blue water, `RulerColorDriver`) | round-57 |
| V-6 | SPRING — infinite source, constant-thickness downhill flow, gels at equilibrium (`CreateSpring`, `va_brush_water_spring`, `thickness=` YAML arg) | round-58 |
| V-7 | Verify persistence end-to-end ("0 disks" hunt) | pending |
| V-8 | `_WaterTime` driver check for combiner materials | pending |
| V-9 | Polish: settle/spring config knobs (cfg hot-reload standard), VFX splash, dedicated tool icons | pending |

### Spring notes (round-58)
- Source cells re-pin to `thickness` every frame (infinite emitter);
  every cell caps at `thickness` — excess passes onward, so the stream
  runs downhill as a sheet instead of deepening.
- Gel trigger: flow-front equilibrium (total moved < ε for 20
  consecutive frames) or 8 s timeout. Stream range ~40 m (grid-bounded).
- v1 limitation: the cap means a stream can't fill a depression deeper
  than `thickness` and overflow the far rim — it ends there. Use a
  Water Volume in the bowl.

### Round-66 — pour destroy-tracer + shader direction (2026-07-10)
- **Destroy bug (OPEN, keystone):** every vanilla pour spawned 07-10 was
  destroyed <0.5 s after spawn (zero T+ diagnostics; dump found only the
  pool template; dedi clean; LiquidVolume never self-destroys). Tracers
  now prefix all four kill paths (ZNetScene.OnZDODestroyed /
  ZNetView.ResetZDO / ZNetScene.Destroy / ZDOMan.DestroyZDO) and print
  the caller stack for FiresLiquidWater victims.
- **Shader direction (USER DIRECTIVE):** custom water should ultimately
  wear the FiresWater shader everywhere. Combiner meshes already default
  to the FiresWater wrap (mode 0 = normal). Pours: water_liquid stays
  the DEFAULT control case until the destroy bug is fixed;
  `va_water_pour_shader 0|1` toggles FiresWater on live + future pours
  (UseFiresWaterShader). Once pours survive + render, flip the default
  and adjust FTS (\_WaterTime driver, depth params) as needed.
- A/B tool fixes: mode 1 now = vanilla water_liquid (captured "vanilla"
  water was already FTS-swapped — comparing FiresWater to itself);
  mode 2 walks a shader candidate list + logs (Standard was silently
  null in-game).
- Restored-anchor bug CONFIRMED user-visible: 5 combiners restored with
  disks=0 / verts=0 (V-7 hunt is now priority after the destroy bug).

### Round-67 — ground probe fix (2026-07-10)
- ROOT CAUSE of "water far below the ruler" + wrong swim triggers +
  vertical mesh sheets: `SampleRealGroundAt` used
  `ZoneSystem.GetGroundHeight`, which is HEIGHTMAP-ONLY (blind to FAT
  custom tiles / rocks / pieces — see reference_groundheight_heightmap_only).
  On a built cliff the sim's terrain was the meadow heightmap ~21m
  below the player (log: terrain=[23.3..56.6] under a y≈51 ruler).
- Fix: fill-anchored physics probe — RaycastNonAlloc down from
  fillY+40 over fillY−200 on Default/static_solid/piece/terrain/
  Default_small, take highest hit ≤ fill+0.25 (resting ground), else
  lowest hit above (blocking land), else heightmap → procedural
  rescue. `_groundProbeFillY` set by both settle + spring coroutines
  (restored springs included — set inside the coroutine).

### Round-68 — ruler Y input fix (2026-07-10)
- SECOND root cause of "water under the ruler": IH's `from=` Y is
  ground-snapped by heightmap-blind logic. Log proof: springs fired
  with height=0 from.y=33.7/38.7/39.1 while the ring draped on built
  terrain meters above (grid terrain max ≈ from.y each time = the
  heightmap, not the visible ground). Our sim then honestly filled to
  the too-low Y.
- Fix: `RulerArgs.CorrectRulerYFromCursor` — at command time the
  crosshair raycast (real colliders) IS the ring the user sees. Same
  XZ → keep IH XZ, take cursor Y; far XZ → take the cursor point
  wholesale (IH's blind ray sailed past the custom terrain); cursor
  miss → live projector Y fallback. Wired into ALL water commands
  (disk/lava/snow/volume/spring/waterfall/delete); terrain tools
  intentionally untouched.
- Purged remaining GetGroundHeight uses on the water path:
  waterfall first-dab + drag dabs and FiresLiquidWaterPrefab.Spawn
  now use WaterBrushDrag.TryProbeGroundBelow (physics, real
  colliders).
- Volume settle now HOLDS the fill level inside the pour circle until
  gel (pour cells re-topped every frame) — conservation used to drain
  the level below the ring as water spread; spec is "fills to the
  ring, settles into the depression, gels".

### Round-69/70 — from= is LAW + spring look fixes (2026-07-10)
- ROUND-69: crosshair "correction" DELETED. from= IS the tooltip
  readout (proven: tooltip 333.4/258.2/35.5 == from= verbatim). The
  corrector raycast our own gelled water (hit swim boxes at y=40.0
  exact) and once moved a pour 9m off-ring. Water goes EXACTLY where
  the tool says. If IH's own snap ever reads wrong on custom terrain,
  patch IH's snap — never mangle our inputs.
- ROUND-70a: sim ground probe mask = TERRAIN ONLY (vanilla
  m_groundLayer parity). Rocks/pieces in the mask put 1m water caps
  on every beach boulder.
- ROUND-70b: spring source EMITS (SPRING_EMIT_RATE 0.05/frame toward
  fill cap) instead of hard-pinning to fillY — the pin held standing
  gel plateaus on flat ground/bank edges (the "gumdrop"). Pools now
  rise only where terrain contains them; open ground stays a sheet.

### Round-71 — 0.2.10 = 0.2.8 restored (2026-07-10, USER DIRECTIVE)
- Round-70 changes REVERTED on user order: probe mask back to
  Default/static_solid/piece/terrain/Default_small (water probes
  pieces/rocks/custom terrain again), spring source back to the
  fillY pin. 0.2.10 behavior == 0.2.8: from= verbatim placement +
  round-67 fill-anchored physics ground probe. Nothing else.
- LOG FINDING (why 0.2.8 looked unfixed): the 19:22 test session had
  no new FAT Awake line and still emitted [RulerArgs] re-anchor
  corrections — the game process predated the 0.2.8/0.2.9 deploys
  and was STILL RUNNING 0.2.7. BepInEx loads the DLL at launch;
  deploys require a game restart to take effect.

### Round-72/73 — world-Y ledger + SPRING → VANILLA LIQUID (2026-07-10)
- Round-72 (0.2.11): diagnostics remade — CENTER line (terrain/column/
  surfaceWorldY at ring center + diff vs fillY + mesh world-Y span) and
  PROBE ledger (every collider hit: name/layer/worldY + picked Y) at
  center and +4x/+4z.
- Round-73 (0.2.12, USER DIRECTIVE): va_brush_water_spring now pours
  the vanilla FiresLiquidWater prefab (Spawn overload with
  volumeMultiplier = thickness hint). ONE water backend — the custom
  grid-sim spring (CreateSpring/SpringFlowCoroutine) is retired from
  the command (code dormant). BuildWaterMesh vertex math was verified
  correct (surfaceWorldY − transform.y, same _surfaceHeights the swim
  trigger uses) — the wrongness was the grid sim's SHAPE on real
  terrain, not the mesh transform.
- NEXT GATE: pour survival — destroy tracers (0.2.4) + pour debug
  (T+0.5..15s) + world-Y ledger all armed on the vanilla path.

### Round-74 — DESTROY BUG SOLVED (2026-07-10, 0.2.13)
- Tracer evidence (20:34 log): `TRACE pour ResetZDO ... pos=(0.00,
  0.00, 0.00)` immediately after every Spawn. Root cause: the
  inactive-parent spawn pattern ACTIVATED the clone at SetParent(null)
  — before position was set — so ZNetView.Awake registered the ZDO at
  world origin; LiquidVolume has no transform sync, so the ZDO stayed
  in zone (0,0) and ZNetScene's zone sweep destroyed the pour within a
  tick. Every pour since 0.2.0 died this way.
- Fix: flip the TEMPLATE inactive → Instantiate(pos, rot) → clone born
  activeSelf=false AT the ruler position → set m_initialVolume/Area →
  SetActive(true) → Awake/ZDO in the correct zone. Template restored
  to activeSelf=true (ZNetScene world-reload restore path needs it).
- Spawn log now prints zdo uid + zdoPos for one-glance verification.

### Round-75 — REAL PREFAB + infinite spring + no bubbles (2026-07-10, 0.2.14)
- Bundle REBUILT headlessly (Run-HeadlessUnity.ps1, owner fat-water) —
  adminterrain md5 57c53c2d (was bff130c0 / FAT-root copy Jun 7). The
  authored FiresLiquidWater.prefab now ships; loader takes the bundle
  path ("Registered ... from FAT bundle"), runtime TarLiquid clone is
  fallback-only. (Regex bubble-strip ate the MeshFilter YAML doc —
  repaired by hand; prefab structurally verified.)
- Bubbles: m_randomEffectList emptied in the authored prefab AND
  cleared on the runtime-clone fallback (LiquidVolume.UpdateEffects
  spawns the EffectList on wet cells — bubbles were never children).
- Infinite source: FiresLiquidSpringEmitter (owner-only, injects into
  grid-center cells under m_meshDataLock, marks m_dirty+m_needsSaving,
  idles ≥90% m_maxDepth; ZDO va_spring_rate + LiquidVolume.Awake
  postfix re-attach on world reload). va_brush_water_spring emits
  2 m³/s × thickness=.
- Swim verified from source: LiquidSurface.UpdateFloaters pushes
  GetSurface (live settled height) into IWaterInteractable per
  FixedUpdate — settled surface is natively swimmable, tracks the sim.
- NEXT (user-sequenced): floating drawn water — custom mesh Y at ruler.

### Round-76 — magenta fix + pour removal (2026-07-10, 0.2.15)
- MAGENTA: the bundle prefab's material references the PROJECT's ripped
  Custom/Tar shader — bytecode doesn't survive the bundle. Fix:
  registration now re-binds renderer materials to the runtime-resolved
  REAL water_liquid on BOTH paths (bundle + clone). Structure = authored
  prefab; material = live vanilla asset (exact-vanilla-shader-first).
- va_brush_water_delete now also removes pours: FiresLiquidWater roots
  whose grid footprint intersects the ring — ClaimOwnership +
  ZNetScene.Destroy (proper networked destroy). Existing magenta pours
  re-render CORRECTLY after restart (template material fixed) and can
  then be deleted with the tool.
- IH undo does not track our console-spawned pours — WEC undo-bridge
  integration is a future item; the delete tool is the removal path.

### Round-77 — material ground-truth + basin fill + FiresWater liquid mode (2026-07-10, FAT 0.2.17 + FTS 0.3.82)
- MAGENTA ROOT CAUSE (log: pour mat queue=2000 vs real tar 2899): the
  name-search picked OUR bundle's broken water_liquid copy once the
  bundle loaded. Resolver now anchors on the LIVE TarLiquid prefab's
  shader INSTANCE (accept name-match only on that instance + queue
  ≥2500, else rebuild from live tar material).
- "Spread past ruler" was 2 bugs: broken shader ignored the wet-mask
  vertex alpha (rendered the whole 64m sheet — spring wet area was
  actually ring-sized), and the volume pour was a π·r²·depth CYLINDER.
  Spawn now BASIN-INTEGRATES: physics-probe grid over the ring,
  Σ max(0, fillY − ground)·cellArea → pours exactly what the hovered
  indent holds (floor 5m³, cap πr²·maxDepth).
- FiresWater liquid-grid mode (FTS 0.3.82): _FiresLiquidGrid toggle —
  clips dry cells via vertex alpha (v2f.vertAlpha TEXCOORD8), inland
  depth floor 0.5 so ponds aren't shore-faded invisible; zone water
  untouched (never consumed vertex color). FAT builds a per-pour CLONE
  of the wrap (_FiresLiquidGrid=1, _WaveAmplitude 0.15) — NEVER set the
  flag on the shared wrap. va_water_pour_shader 1 = FiresWater skin;
  default flips after user sign-off.

### Round-78 — tools + bank cull + stencil fix + GLASS-ATLAS CATCH (2026-07-11)
- SHADER (from logs, not guessing): va_water_pour_shader 1 DID apply
  (log: 7 pours "skinned with FiresWater_LiquidPour queue=2901").
  Invisible because FiresWater's dedup STENCIL (Comp Equal/Pass Invert,
  "first mesh claims pixel") culls inland pours vs the zone ocean plane
  and each other; vanilla water_liquid has no stencil → showed. Fix:
  property-driven stencil (_FiresStencilComp/_FiresStencilWriteMask);
  pour material sets Comp Always(8) + WriteMask 0 → never culled.
- Delete Water → RULER-LOCAL: FiresLiquidGridOps.RemoveInRadius zeros
  m_depths cells inside the ring; pour destroyed only if fully drained.
- MOP restored (va_brush_water_mop): soft 50%-per-pass local removal +
  legacy combiner MopWaterAt pass.
- Bank-respect cull (FiresLiquidSpringEmitter.CullUnbankedSpill): thin
  wet cells (<0.35m) with no nearby terrain rising ≥0.5m (no true bank)
  are zeroed → water stays in cut channels, no z-fight sheet on flats.
- **GLASS-ATLAS CATCH (cross-session):** CreateAssetBundles.
  BuildAllAssetBundles shipped tossinshade at 65 MB — MISSING the
  FiresGlassProjAtlas (glass windows/neon would go grey). Parallel
  glass session ships 102 MB. FIX: bake FiresWater changes via
  FiresGlassBuilder.RebuildAtlasAndBundle (regenerates atlas +
  ForceRebuild real bytecode) so glass atlas AND shader change both
  ship. NEVER deploy a 65 MB tossinshade. See [[reference_headless_bundle_build]].

### Round-79 — spring levels at ruler fillY + real spill kill (2026-07-11, 0.2.19)
Three distinct bugs behind "spills onto flats / delete won't remove / persists after reload":
- SPILL ROOT CAUSE: emitter pumped source cells until center depth hit
  0.9×m_maxDepth = 18m — blind to the ruler height. In a 3m cut it kept
  rising until it overflowed the rim onto the flat. FIX: emitter now TOPS
  the source cells toward the RULER fillY (center.y) and no higher — the
  pool levels out at the ruler level, overflows only where fillY clears
  the lowest bank. fillY persisted in ZDO va_spring_fill (survives reload).
- CULL: was gated behind the emit early-returns (accumulate<0.25, fullness)
  and its bank check found the cut wall BEHIND the spill → kept it. FIX:
  FiresLiquidGridOps.CullFlatSpill runs EVERY tick (5 Hz), direction-
  independent RELIEF test — thin cell (<0.35m) on locally-flat terrain
  (max-min height <0.5m over 3-cell scan) is zeroed; deep water + real
  cuts kept. Scrubs persisted spill too, not just new.
- PERSISTS AFTER DELETE+RELOAD: ruler-local delete never cleared the
  emitter, so it re-pumped on reload; and the sim reflows into zeroed
  cells. FIX: delete OVER THE SOURCE (ring covers pour center) now KILLS
  the spring — StopSpringEmitter zeros va_spring_rate ZDO (re-attach skips
  rate<=0) + removes the component + network-destroys the pour. Delete/mop
  elsewhere trims + runs CullPour to scrub thin remnants. Mop never kills.
- CAVEAT (sim reality): local carving of a CONNECTED pool still partially
  reflows (vanilla LiquidVolume conserves/evens depth) — inherent. To
  remove a pool/spring, aim delete at its centre. Old pre-0.2.19 springs
  have flooded ZDO data + a live emitter — delete-over-centre to clear,
  then re-place.
- NOT DONE: true multi-level contained CASCADE down a slope (series of
  banked pools stepping down). fillY-cap + cull approximates it (source at
  ruler level, overflow down, flat spill culled); full cascade is a later
  pass.

### Round-80 — SPRING back to custom sheet sim, bank-relative fill (2026-07-11, 0.2.20)
- USER DECISION: vanilla LiquidVolume can ONLY settle to one flat Y — it
  physically cannot make a sloped river. So Spring is OFF vanilla, back on
  the custom WaterMeshCombiner sheet sim. (Water Volume STAYS vanilla for
  flat pools.)
- NEW FILL RULE (user spec "stop settling within X of the bank"): each
  non-source cell fills to (localBankHeight − X) instead of a fixed
  thickness. localBank = max terrain in an 8-cell scan (the rim); X =
  thickness= arg ("height off bank", default 1m). So:
  * sloped channel → bank slopes → water surface slopes with it (no flat
    endpoint).
  * flat ground → bank ≈ terrain → cap = max(0, −X) = 0 → stays DRY (kills
    the spill onto the path).
  * source cells held at the ruler fillY (the pool).
  Mechanism: source re-fed every frame + per-cell cap at bank−X → excess
  can't pile above bank−X so it spreads ONWARD downhill, filling each cell
  to its cap. Endpoint tracks the banks down the slope.
- Combiner spring delete: RemoveWaterDisk on the single source disk →
  DestroyPermanently (clears anchor + cache) → delete-over-source removes
  the whole sloped sheet. Mop uses MopWaterAt.
- thickness= arg REPURPOSED: was layer thickness (depth above ground), now
  bankOffset X (depth below rim). Tooltip updated.
- OPEN QUESTIONS to watch in test: bank scan radius (8 cells) may overfill
  broad open slopes (max-in-scan finds uphill terrain); wide channels
  (>~10m) may under-fill centre. Both tunable. Settling animation is the
  existing FlowPass (vanilla-like) — endpoint is the sloped fill.

### Round-82 — FiresLiquidRiver: sloped spring on the vanilla liquid engine (2026-07-11, 0.2.22)
Plan: C:\Users\travi\.claude\plans\abundant-cuddling-origami.md (user-approved).
- Vanilla liquid = material (Custom/Tar, replaceable w/ FiresWater) + C# class
  (LiquidVolume: sim + threaded mesh builder = the look). We keep the class,
  replace ONLY its per-tick sim.
- NEW FiresLiquidRiverController (MonoBehaviour on spring pours) + Harmony
  PREFIX on LiquidVolume.UpdateLiquid → runs controller.SimStep and returns
  false (vanilla flatten never runs for our pours). Prefix gated by a
  thread-safe registry (s_byVolume) so tar pits + Water Volume are untouched.
  This kills the main-thread-vs-worker fight that broke the old emitter.
- SimStep (worker thread, no Unity API): (1) pin source disk to ruler fillY,
  (2) EvenDepth flow passes (ported vanilla), (3) cap each non-source cell at
  bank−X (bankRel = max terrain in 8-cell scan) → follows slope, flat ground
  caps to 0 = dry, (4) drain borders. Transform-derived data (source mask,
  bank heights, baseY) precomputed MAIN-thread in Init() once m_haveHeights.
- Spawn: SpawnSpring(center,radius,bankOffset) attaches the controller;
  persists va_spring_rate/fill/bankoff/radius; re-attach patch on
  LiquidVolume.Awake restores it after reload.
- PlaceSpring → SpawnSpring (was WaterMeshCombiner.CreateSpring). Combiner
  CreateSpring + FiresLiquidSpringEmitter RETIRED (emitter removed from
  csproj compile so its conflicting re-attach patch doesn't register).
- Default skin still water_liquid (isolate sim); va_water_pour_shader 1 =
  FiresWater. Swim = round-81 collider-disable (unchanged).
- NEXT: Phase 3 multi-spring chaining, Phase 4 building-piece damming,
  Phase 5 flow-current shader (needs glass-safe tossinshade rebuild).

### Round-83 — Custom/FiresLiquid dedicated shader + swim diag (2026-07-12, FAT 0.2.24 + FTS 0.3.87)
- User-confirmed: dedicated Custom/FiresLiquid for ALL custom water (not
  spring-only), decoupled from the ocean FiresWater.
- Vanilla-liquid = material (Custom/Tar) + C# class. FiresWater applied but
  went INVISIBLE on inland pours — root cause (user's tide hunch was right):
  (1) _FiresTideOffset (shader line 813) shifts surface Y by global tide →
  low tide sinks the pour below terrain; (2) inland SampleShoreField returns
  ~0 depth/garbage → shore-faded to nothing.
- FiresLiquid = copy of FiresWater (Shaders/Fires/FiresLiquid.shader, guid
  fac9d2b4e084f6a8c1d3e5b7a9c0f200, tagged tossinshade). Liquid strip so far:
  gate _FiresTideOffset OFF and bypass SampleShoreField (fixed attn=1
  depth=0.7) when _FiresLiquidGrid>0.5. Defaults: LiquidGrid=1, StencilComp=
  Always, WriteMask=0.
- GUID GOTCHA: first bundle silently EXCLUDED the shader — I typo'd a 33-char
  GUID (Unity needs exactly 32). Build log: "does not have a valid GUID ...
  ignored". Fixed → compiled real bytecode (d3d11 8 progs). WATCH: low variant
  count (unreferenced shader, stripped) — if runtime magenta, add a reference
  .mat/ShaderVariantCollection to keep variants.
- FAT BuildFiresWaterPourMaterial now prefers Shader.Find("Custom/FiresLiquid")
  (copies normal/foam textures from the FiresWater ocean mat), falls back to
  the FiresWater wrap. Springs default to it (ApplyPourMaterial true).
- SWIM: debug proved water is deep/swimmable (maxColumn 2-4m) + visible, so the
  LiquidSurface trigger isn't driving swim. Added swim/trigger diag to
  FiresLiquidWaterDebug.Describe: LiquidSurface present/enabled + player
  InWater/liquidLevel/below/IsSwimming. Next test names the break.
- REMAINING Pass-2: strip clipmap/tessellation/Ashlands (perf), flow currents.
