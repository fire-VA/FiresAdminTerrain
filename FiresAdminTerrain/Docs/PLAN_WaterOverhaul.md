# Water Mesh Overhaul — Brush + Waterfall

## 0. Goal (user spec, 2026-05-26)

> "we want similar function to our terrain blender style, but our water
> mesh should lay out on the terrain kind of like a bubble when placed
> floating or not against a riverbank edge, allow it to clip through
> terrain at the ruler height we are at with normal water placement
> then give us a seperate waterfall handling tool that allows us to
> smooth drag up or down a slope to apply our water to it (this water
> should hold the form of the slope standing off the solid edge a
> consistent amount the whole way) so it can look like a real proper
> water source and not a janky mesh please."
>
> "water placement like terrain should respect our infinity hammer
> ruler position for y level when placing normal rivers (smoothing
> down slopes naturally) but when using waterfall tools it should
> respect y level of the ruler center but swap to a raycast ruler ring
> that shows the raycast on the terrain"

Three concrete deliverables:

1. **Brush-style normal water** — drag-paint rivers/ponds the same way
   the terrain blender brushes paint, with the ruler Y as the water
   surface. Water clips through terrain (it's a surface, not a
   solid) — what you see is where the surface meets terrain.
2. **Waterfall tool** — separate brush. Drag along a slope; each dab
   places water that follows the terrain's local Y plus a small
   constant offset, so the water hugs the slope at a consistent
   thickness.
3. **Visual: bubble cap** — surface tension look so the water reads
   as a body of water rather than a thin sheet, especially at the
   edges where it meets terrain.

## 1. Current state (round-41)

- `va_brush_water` console command places ONE disk per invocation.
  Single-click only — no drag-pump.
- `WaterDisk` data object: center + radius + depth + liquid type.
- `WaterMeshCombiner` per region: merges all disks of a single
  `CustomLiquidType` into one merged mesh. Anchored via
  `WaterAnchorPrefab`. Persisted via `WaterPersistence`.
- Disks render as flat circles at their `Position.y`. No bubble cap,
  no slope-following.
- `IcePlacementTool`, `LandSnow`/`LandIce` brushes use the same
  single-shot pattern.

No equivalent to `TileBrushDrag` exists for water — there's no
MonoBehaviour polling mouse-held and re-firing the placement.

## 2. Phased plan

### Phase W-1 — Brush-drag pattern for normal water

Mirror `TileBrushDrag` for the water brushes. Build:

- `WaterBrushDrag` MonoBehaviour with the same dab-pump architecture:
  lazy pointer, constant-arc-length dab spacing, IH ruler
  integration (the same `InfinityHammerRulerProbe` from round-40).
- `WaterBrushCommands.PlaceDisk` records the last placement (liquid
  type + radius + depth + center.y) into the drag MonoBehaviour as
  the "last brush" so subsequent drag ticks fire the same kind of
  placement at the moving cursor.
- Per dab: create a small disk (e.g. radius matching the dab
  spacing) at the dab position. The combiner naturally merges them
  into one continuous surface. Y stays at the click's center.y so
  rivers look horizontal as you drag.

Affects: `WaterBrushCommands.cs`, new `WaterBrushDrag.cs`,
`FiresAdminTerrain.csproj`.

### Phase W-2 — Waterfall tool

New brush kind that follows terrain Y on each dab. Build:

- `va_brush_waterfall` command (and per-liquid variants if needed,
  start with just water).
- New `BrushKind.Waterfall` in `WaterBrushDrag`.
- Per dab: raycast terrain Y at cursor XZ, place a disk at
  `(cursor.x, terrainY + WATERFALL_OFFSET, cursor.z)`. Offset is a
  small fixed value (start with 0.5m) so the water sits ABOVE the
  ground by a uniform amount along the slope.
- Smaller per-dab radius (e.g. 2m) gives a continuous stream;
  larger radius gives a wide cascade.

Affects: `WaterBrushCommands.cs`, `WaterBrushDrag.cs`.

### Phase W-3 — IH ruler ring raycast for waterfall

Waterfall mode is the only one where the ruler ring should follow
terrain instead of staying horizontal. IH supports both — we need
to toggle the projector mode when the waterfall tool is active.

- Extend `InfinityHammerRulerProbe` with a reflective setter for
  the ring's "terrain-snap" mode (find the right static field on
  `InfinityTools.Ruler` or `RulerShape`).
- Toggle on when `WaterBrushDrag._kind == Waterfall`, off otherwise.

Affects: `InfinityHammerRulerProbe.cs`, `WaterBrushDrag.cs`.

### Phase W-4 — Bubble cap mesh

Currently the combiner emits a flat horizontal surface. For "bubble"
appearance:

- In `WaterMeshCombiner.MeshGen`, after generating the standard flat
  surface verts, add a slight Y bulge per disk: each vert that's
  within a disk's radius gets `y += BUBBLE_HEIGHT * (1 - r/R)` for
  the smoothstep falloff. Sum of contributions from overlapping
  disks gives natural-looking merged bulges.
- `BUBBLE_HEIGHT = 0.2m` is a reasonable starting value — visible
  surface tension without breaking the "still water" look.
- Don't apply to lava (it should stay flat-ish) or ice (already
  solid). Water and tar only.

Affects: `WaterMeshCombiner.MeshGen.cs`, possibly liquid-type config.

### Phase W-5 — Stitching across waterfall dabs (polish)

A long waterfall stroke produces many small disks. Each disk is its
own data object but they merge geometrically. If the combiner's
disk-footprint cull is too aggressive, narrow connections between
dabs might drop out. Investigate and tune the cull during testing.

## 3. Architecture decisions

### 3.1 Why per-dab disks instead of a single stroke entity

Mirrors the terrain brush model: each dab is independent data; the
combiner is what merges them visually. Keeps undo / deletion working
the same way users already understand. Allows partial deletion of
parts of a waterfall by mopping individual dabs.

### 3.2 Why waterfall doesn't reuse the terrain Apply* path

Terrain brush ops mutate heightmap vertices in 64m tiles. Waterfall
is a fluid surface — it shouldn't snap to a vertex grid. The water
combiner already works in continuous world XZ via its disk model, so
the waterfall is just "disk placement with a per-dab Y override".

### 3.3 Ruler integration

Both modes already plug into IH via the existing `RulerArgs` parser.
Phase W-1 just hooks the drag MonoBehaviour to call
`InfinityHammerRulerProbe` for the cursor position (same pattern as
`TileBrushDrag`). Phase W-3 extends the probe to control the ring's
terrain-snap mode.

## 3.5 Status

| Phase | Shipped | Notes |
| --- | --- | --- |
| W-1 brush drag | 2026-05-26 round-W-1 | `WaterBrushDrag` MonoBehaviour cloned from `TileBrushDrag`. Hooks into IH ruler position via `InfinityHammerRulerProbe`. Same lazy-pointer + max-jump-clamp resistance as the terrain brush. Drag tool gating matches the YAML piece names (Water / Lava / Tar / Boiling Water / Freezing Water / Ice / Waterfall). |
| W-2 waterfall | 2026-05-26 round-W-2 | New `va_brush_waterfall` console command + YAML entry. First dab samples terrain Y; drag dabs raycast terrain per dab so the stream hugs the slope. `WATERFALL_OFFSET=0.5m` above terrain, `WATERFALL_DAB_DEPTH=0.3m` shallow sheet, denser dab spacing (15% vs 20%) so the stream reads as continuous. |
| W-3 raycast ring | not yet | Requires extending `InfinityHammerRulerProbe` to toggle the projector's terrain-snap mode. IH already supports both modes (one is `RulerShape.Circle`, the other involves a different shape/projector setup). Needs IH source inspection to find the right knob. |
| W-4 bubble cap | not yet | Mesh combiner is complex (IDW, bank curl, fallback layers in `WaterMeshCombiner.MeshGen.cs`); a per-disk Y-bulge needs careful insertion into the surface generation pass so it doesn't break the bank-extension / curl logic. Tracked as the next water item. |
| W-5 stitching polish | not yet | Tune after W-2 testing — watch for narrow-stream segments dropping out of the combiner due to the 0.1m disk-footprint cull. |

## 4. Open questions

1. Should waterfall mode auto-flatten the WATER surface (so the
   water itself stays flat, not following the slope) and just place
   it at the terrain's local Y? That would look like "water sliding
   down a ramp" which might not be what the user wants.
   - User spec says "this water should hold the form of the slope
     standing off the solid edge a consistent amount". So the water
     surface DOES follow the slope. The mesh isn't flat — each dab
     is at its own Y matching the terrain underneath.

2. Should the dab spacing for waterfall be smaller than for normal
   water? Waterfalls tend to be narrow streams; small dabs make
   sense. Start with `radius * 0.15` (denser than the 0.2 default)
   so the waterfall reads as continuous.

3. Should waterfall depth differ from normal water? Yes — shallow
   (~0.3m) so it reads as a thin flowing sheet rather than a
   bottomless pool clinging to the cliff.
