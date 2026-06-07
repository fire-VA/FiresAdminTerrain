# Blender Sculpt Integration Gameplan

> Research deep-dive into Blender's sculpt/brush internals (GPL v3, GitHub `blender/blender` main branch as of 2026-05-26) to identify portable techniques for FAT's 65x65 vertex terrain tiles. Companion to `PLAN_DrawingFeelParity.md` which closed ~70% of the gap with RPG-maker techniques; this plan targets the remaining ~30% "continuous sculpt" feel.

## 0. TL;DR

Blender's "creamy" feel is **not** a better averaging kernel — it uses plain uniform 1-ring Laplacian, simpler than FAT's weighted 2-pass Gaussian. The magic is three things stacked: (1) a **constant-arc-length dab spacing pump** that fires dabs every `0.2 * radius` of cursor travel regardless of mouse speed, (2) an **iteration_strengths splitter** that turns a single dab into up to 4 sub-passes of smoothing for fractional convergence, and (3) a **lazy-pointer cursor filter** (`smooth_stroke_factor = 0.9`) that EMA-style follows the input cursor only when distance exceeds a radius. The top three to port to FAT are, in order: **constant-spacing dab pump (B-1)**, **iteration_strengths smoothing (B-2)**, and **lazy-pointer cursor (B-3)**. PBVH spatial indexing is overkill for our 4225-vert tiles and should be skipped.

## 1. Blender's Smooth brush algorithm

### 1.1 Kernel + neighbor selection

**File:** `source/blender/editors/sculpt_paint/mesh/sculpt_smooth.cc`

The averaging kernel is **uniform-weight 1-ring Laplacian**. From `neighbor_data_average_mesh` (lines 47-57):

```cpp
for (const int i : vert_neighbors.index_range()) {
  dst[i] = bke::attribute_math::mix_indices(src, vert_neighbors[i]);
}
```

`mix_indices` averages the listed neighbor positions with equal weight (divides sum by count). No cotangent weighting, no diagonal-vs-cardinal distinction, no center-bias — every direct topology neighbor counts as `1.0`. Only the 1-ring is sampled (no 2-ring traversal).

**Per-vertex application:** `source/blender/editors/sculpt_paint/mesh/brushes/smooth.cc` (the brush dispatch). The displacement is decomposed:

```
translation_i = (neighbor_avg_i - orig_pos_i)
translation_i *= factors_i       // factors = strength * falloff_curve * pressure * masks
new_pos_i      = orig_pos_i + translation_i
```

`factors[i]` is the product of per-vertex brush strength, falloff curve at that vertex's distance from the dab center, sculpt mask, automasking, and pressure. So effectively:

```
new_pos = lerp(orig_pos, neighbor_avg, strength * falloff)
```

### 1.2 Why it feels creamy

Five concrete reasons, in descending order of impact:

1. **`iteration_strengths` splitter (`smooth.cc` lines 30-45).** A single dab runs **up to 4 smoothing iterations**. Strength is clamped to `[0,1]`, then `count = floor(strength * 4)` full passes at strength `1.0` are queued plus one fractional pass at the remainder. This is the single biggest contributor to the "creamy" feel — repeated Laplacian passes converge geometry toward C2-continuous surfaces *within one dab*. FAT currently does 2 passes total per dab.
2. **Constant-arc-length dab spacing.** Dabs fire every `0.2 * radius` of cursor travel (see Section 3), so the iteration_strengths convergence stacks smoothly along the stroke instead of clustering when the mouse stops.
3. **Strength is clamped to 1.0 per dab.** No runaway accumulation; the kernel can't overshoot into mush, but multiple dabs slowly converge toward the smoothed limit.
4. **Position deltas, not absolute writes.** Applying `translation_i` separately from reading `orig_pos_i` means each pass within a dab reads the *post-previous-pass* state — Jacobi-style iteration that gives smoother convergence than the simultaneous re-read FAT does.
5. **Uniform weight (not Gaussian).** Counterintuitively, uniform weight is better here because it diffuses faster per iteration; the iteration count is the volume knob.

## 2. Brush falloff curves

### 2.1 The curve preset library

**File:** `source/blender/blenkernel/intern/brush.cc`, `BKE_brush_curve_strength` lines 2463-2528. Input is `p = 1 - distance/radius` (1 at center, 0 at edge).

| Preset | Formula | Use case |
|---|---|---|
| `BRUSH_CURVE_CUSTOM` | user CurveMapping | bespoke |
| `BRUSH_CURVE_SHARP` | p^2 | hard edges, chisel |
| `BRUSH_CURVE_SMOOTH` | 3p^2 - 2p^3 (smoothstep) | **default for most brushes** |
| `BRUSH_CURVE_SMOOTHER` | p^3 * (p * (6p - 15) + 10) (Perlin smootherstep) | gentlest, organic |
| `BRUSH_CURVE_ROOT` | sqrt(p) | wide soft hit |
| `BRUSH_CURVE_LIN` | p | linear |
| `BRUSH_CURVE_CONSTANT` | 1.0 inside, 0.0 outside | masking/stamps |
| `BRUSH_CURVE_SPHERE` | sqrt(2p - p^2) | hemispherical dome |
| `BRUSH_CURVE_POW4` | p^4 | very pointy |
| `BRUSH_CURVE_INVSQUARE` | p * (2 - p) | soft falloff with broad shoulder |

### 2.2 What Blender uses by default

`BRUSH_CURVE_SMOOTH` (the cubic `3p^2 - 2p^3`, i.e. classic Hermite smoothstep). Smooth brush, Draw, Clay, Inflate, Grab — all default to this.

### 2.3 What FAT could adopt

FAT already uses smoothstep inside `BRUSH_EDGE_BLEND_DISTANCE = 1m` via `ApplyFlatToTile / ApplyRaiseToTile` — that *is* `BRUSH_CURVE_SMOOTH` applied to just the 1m edge band. Two upgrades worth considering:

- Apply the curve to the **entire brush radius** (not just the 1m edge), so the center-vs-edge falloff is one continuous smoothstep instead of "flat center, smoothstep edge ring." This eliminates the visible flat-top plateau on large brushes.
- Add **SMOOTHER (`p^3(p(6p-15)+10)`)** as a config option for the Smooth/Flatten tool only — its zero 1st and 2nd derivatives at p=0 and p=1 means stamps blend invisibly into untouched terrain.

## 3. Dab spacing pipeline

### 3.1 The constant-spacing trick

**File:** `source/blender/editors/sculpt_paint/paint_stroke.cc`, `PaintStroke::space_stroke` lines 1016-1089.

```cpp
float2 mouse_delta = final_mouse - this->last_mouse_position;
float length = normalize_v2(mouse_delta);   // remaining travel this event
while (length > 0.0f) {
  const float spacing = paint_space_stroke_spacing_variable(...);
  if (length >= spacing) {
    float2 mouse = this->last_mouse_position + mouse_delta * spacing;
    pressure = last_pressure_ + (spacing / length) * pressure_delta;
    this->add_step(C, op, mouse, pressure);
    length -= spacing;
    count++;
  } else {
    break;          // carry remainder to next event
  }
}
stroke_distance_ += spacing / zoom_2d_;
```

Spacing formula (`paint_space_stroke_spacing` lines 974-1003):

```cpp
spacing = brush->spacing;                                   // default 10
if (brush->flag & BRUSH_SPACING_PRESSURE)
    spacing *= (1.5f - pressure);                           // 0.5x..1.5x range
spacing *= zoom_2d;
return max_ff(zoom_2d, radius * spacing / 50.0f);           // -> 0.2 * radius at default 10
```

So at default `brush->spacing = 10`, the dab fires every **0.2 * radius** of cursor travel.

### 3.2 Why this is the "continuous stroke" magic

Three properties no single-tick stride scheme can match:

- **Mouse-speed independent.** Sweep slow, sweep fast, the spacing on disk is the same. Speed only changes how *quickly* you cover ground, not how *densely* dabs land.
- **Sub-event remainder is carried.** When `length < spacing` the leftover is kept (via `last_mouse_position` update) and combined with the next event's travel. There is no quantization to tick boundaries.
- **Pressure is lerped per dab**, not per event. Each dab gets `last_pressure + (spacing/length) * pressure_delta`, so a fast pen-down across one frame still produces a graded pressure ramp across the dabs.

### 3.3 Compared to FAT's current cursor interp

FAT (`TileBrushDrag.cs Update`) fires at a fixed **10 Hz tick**, then between ticks emits **up to 8 intermediate stamps at `radius * 0.5` spacing**. Three gaps:

| Aspect | FAT today | Blender |
|---|---|---|
| Spacing density | `0.5 * radius` (gappy) | `0.2 * radius` (2.5x denser) |
| Cap on stamps per tick | 8 (truncates fast sweeps) | unlimited |
| Sub-tick remainder | dropped | carried in `last_mouse_position` |
| Tick decoupling | 10 Hz hard tick | per-event with sub-event remainder |

The 10 Hz tick is fine as the *physics/mesh-rebuild* tick. The dab emission should be **decoupled from the tick** and run on every cursor event with the carried remainder. This is **B-1** below.

## 4. PBVH spatial indexing

### 4.1 What it does

**File:** `source/blender/blenkernel/intern/pbvh.cc`. PBVH is a binary BVH built over mesh faces. Leaf threshold is `leaf_limit = 2500` faces (line 236), depth-capped at 100. Each leaf bounds a set of vertices; brush operations call `pbvh_iter_begin` with a sphere test callback (line 575) and Blender visits only the leaves whose AABB intersects the brush sphere. This is what makes 5M-vert sculpts interactive.

### 4.2 Is it relevant for FAT's 65x65 grid?

**No. Skip it.** Reasoning:

- FAT's 65x65 = 4225 verts per tile. Blender's *leaf* threshold alone is 2500 faces. The entire tile fits in less than two Blender leaf nodes; the BVH wouldn't have any useful structure to walk.
- FAT verts are on a **regular 1m grid**, so a brush-radius query is `O(1)` integer arithmetic (`for (int dx = -r; dx <= r; dx++) for (int dz = ...)`). There is no spatial query problem to solve. We already do this in `SmoothHeightsInRadius`.
- The cross-tile case (brush straddles tile boundary) is solved by **iterating only the 1-4 affected tiles**, not by building a BVH over tiles. The tile manager already maps `world -> tile` in `O(1)`.

Verdict: PBVH is a beautiful answer to a problem FAT does not have. Document the fact (so future readers don't relitigate) and move on.

## 5. Multi-stroke accumulation behavior

### 5.1 Per-stroke clamp vs per-dab accumulate

**File:** `source/blender/editors/sculpt_paint/mesh/sculpt.cc`. Two relevant signals:

- `stroke_is_first_brush_step(cache)` (~line 1600): `cache.first_time && all symmetry passes == 0`. Marks the first dab of a stroke.
- `ss.cache->accum` flag (~line 2750): when **false**, the brush reads from `orig_position_data` (the geometry at stroke start, frozen for the stroke duration). When **true**, the brush reads from current live geometry.

This is the architectural split:

- **`accum = false` (per-stroke clamp).** The brush's effect saturates at the configured strength regardless of how many dabs land. Each dab targets `orig_pos`, so re-dabbing the same spot does nothing once you've hit the target. **Smooth, Grab, Inflate, Layer.**
- **`accum = true` (per-dab accumulate).** Each dab compounds onto the live geometry. Holding the brush in place keeps building. **Clay, Clay Strips, Draw (when accumulate is checked).**

### 5.2 Which mode does each brush use?

Brush dispatch in `brush_strength()` (~line 2245-2260, `sculpt.cc`):

- `SCULPT_BRUSH_TYPE_CLAY_STRIPS`: `powf(pressure, 1.5f) * 0.3f` — accumulate ON.
- `SCULPT_BRUSH_TYPE_SMOOTH`: `flip * alpha * pressure * feather`, no overlap multiplier — accumulate OFF, original-position read.
- `SCULPT_BRUSH_TYPE_DRAW`: respects user `BRUSH_ACCUMULATE` flag.

### 5.3 What FAT should choose for Raise / Flat / Smooth

| FAT tool | Mode | Reason |
|---|---|---|
| Raise / Lower | `accumulate = true` | terrain ought to keep building under a held brush; matches user expectation from RPGmaker |
| Flat | `accumulate = false` (target-toward) | flattening to a target height *is* a clamped operation; holding the brush should not punch through the target plane |
| Smooth | `accumulate = false` | per Blender; converges to the smoothed limit and stops |

FAT's current `TileBrush.cs SmoothHeightsInRadius` re-reads live `Heights[]` every pass and writes back, which is effectively `accumulate = true` — that explains why holding the smooth brush over a hill flattens it past the smoothed limit into actual depression. **B-2 fixes this** by snapshotting heights at stroke start.

## 6. Input smoothing / cursor filtering

**File:** `source/blender/editors/sculpt_paint/paint_stroke.cc`, `paint_smooth_stroke` (~lines 550-575):

```cpp
const float radius = brush.smooth_stroke_radius * zoom_2d;   // default 75 px
const float u      = brush.smooth_stroke_factor;             // default 0.9
if (math::distance_squared(last_mouse_position, sample->mouse) < square_f(radius)) {
  return false;                                              // don't move yet
}
r_mouse    = math::interpolate(sample->mouse, last_mouse_position, u);
r_pressure = math::interpolate(sample->pressure, last_pressure,    u);
```

This is the **lazy pointer**, not an EMA. Two clauses:

1. **Dead zone.** If the cursor hasn't moved farther than `smooth_stroke_radius` (75 px default) from where the brush actually is, the brush *doesn't move at all*. This kills jitter and tiny tremors. Once the cursor pulls outside the dead zone, the brush catches up.
2. **Lerp-toward-old-position.** `output = lerp(cursor, last_position, 0.9)` — note the high `u` lerps the output **toward the lagging position**, so the brush trails the cursor by a fraction of the distance per frame. This produces visible "rubber-band" follow and absorbs cursor noise.

Default values (from `DNA_brush_types.h`):
- `smooth_stroke_radius = 75` (pixels)
- `smooth_stroke_factor = 0.9` (range effectively 0.5-0.99)

Blender also coalesces input events per frame via `PaintStroke::calc_average_sample` (~line 1390), averaging mouse position and pressure across all samples received that frame before the lazy-pointer step. No one-euro filter; no predictive Kalman; just dead-zone + lerp + per-frame averaging.

## 7. Concrete porting plan

## STATUS (2026-05-26)

Implemented in one pass: **B-1, B-2, B-3, B-4**. B-5 (per-frame event coalescing) skipped — Unity already samples mouse once per frame, no events to coalesce until/unless we switch to the new Input System.

| Phase | Status | Notes |
|---|---|---|
| B-1 — Constant-spacing dab pump | ✅ done | Replaces the 10 Hz tick + 8-stamp cap. Per-frame walk with remainder carry. `DAB_SPACING_RATIO = 0.2` matches Blender's `brush->spacing = 10` default. Capped at `MAX_DABS_PER_FRAME = 16`. |
| B-2 (kernel) | ✅ done | Uniform 1-ring 8-neighbor Laplacian replaces the weighted Gaussian. `SmoothHeightsInRadius_Single` is the new core. |
| B-2 (iteration_strengths) | ✅ done | `SmoothHeightsInRadius_Iterated` splits strength into integer + fractional passes, max 4 full + 1 fractional per dab. |
| B-2 (origHeights snapshot) | ✅ done | `TileBrush.NotifyStrokeStart` snapshots Heights[] per touched tile on LMB-down; `NotifyStrokeEnd` clears on LMB-up. `BrushSmooth` reads from the snapshot when present so held brush converges. |
| B-2 (Smooth brush) | ✅ done | `BrushSmooth(center, radius, strength)` + `SendSmooth` RPC + `va_brush_smooth` console command + YAML `Smooth Terrain` tool. |
| B-3 — Lazy-pointer cursor | ✅ done | `LAZY_POINTER_RADIUS = 0.25m` dead zone + `LAZY_POINTER_FACTOR = 0.5` lerp toward lagging pos. Sits in front of the dab pump. |
| B-4 — Brush curve preset library | ✅ done | `BrushFalloffCurves.cs` ships all 9 Blender presets (Smooth / Smoother / Sharp / Linear / Root / Constant / Sphere / Pow4 / InvSquare). `BrushWeightCurve` helper added; tools opt in. |
| B-5 — Per-frame event coalescing | 🚫 skipped | Unity already samples mouse once per frame; nothing to coalesce. Revisit if FAT moves to Unity Input System. |
| B-6 — Grab brush (Blender Grab) | ✅ done (round-37) | `TileBrush.BrushGrab`, `Cmd_Grab`, `va_brush_grab`, YAML `Grab Terrain`. Cursor Y delta from stroke start drives the drag; per-stroke origHeights snapshot is the baseline so held brush waits for cursor motion instead of accumulating. RPC `RPC_GRAB`. Drag-handler captures `_grabAnchorCursorY` on LMB-down. |
| B-7 — Noise brush (Perlin) | ✅ done (round-37) | `TileBrush.BrushNoise`, `Cmd_Noise`, `va_brush_noise`, YAML `Noise Terrain`. Per-vertex `Mathf.PerlinNoise(wx*scale, wz*scale) * intensity * weight`, remapped to ±intensity. Deterministic on world XZ so repeated dabs produce the same pattern. RPC `RPC_NOISE`. |

### 7.1 Phase B-1: Constant-spacing dab pump

- **File:** `Persistence/TileBrushDrag.cs`
- **Math:**
  - On every `Update` (not on the 10 Hz tick), compute `mouseDelta = cursor - lastDabPos`, `length = mouseDelta.magnitude`.
  - `spacing = radius * 0.2f` (Blender's `brush->spacing = 10` default; expose as config `ConfigBrushSpacingPct`, range 5-50).
  - While `length >= spacing`: emit dab at `lastDabPos + (mouseDelta / length) * spacing`; `lastDabPos = dab position`; `length -= spacing`. Carry remainder by not updating `lastDabPos` on the partial.
  - Decouple from the 10 Hz tick — the tick is now only for **mesh rebuild batching**, not dab emission.
- **Effort:** Half a day. Single file, well-isolated.
- **Acceptance:**
  - Sweeping at any mouse speed produces visually identical dab density.
  - Holding the mouse still emits zero dabs.
  - No dropped dabs on a fast sweep across a full tile (replaces today's 8-stamp cap).
  - Smooth and Raise both feel like continuous painting, not stamping.

### 7.2 Phase B-2: Adaptive Smooth brush with iteration_strengths

- **File:** `TileBrush.cs SmoothHeightsInRadius` -> rename to `SmoothHeightsInRadius_Single`; add new `SmoothHeightsInRadius` orchestrator.
- **Math:**
  - At stroke start (`TileBrushDrag.Update` first event), snapshot `origHeights = Heights[].Clone()` for any tile the brush touches; refresh snapshot when a new tile enters the brush.
  - Per dab: compute `strength = brushStrength * pressure * dt`. Then:
    ```
    count = floor(clamp01(strength) * 4)
    fractional = clamp01(strength) * 4 - count
    for (i = 0; i < count; i++)  SmoothHeightsInRadius_Single(strength=1.0)
    if (fractional > 0)          SmoothHeightsInRadius_Single(strength=fractional)
    ```
  - `SmoothHeightsInRadius_Single` does ONE 8-neighbor pass (drop the current 2-pass; one pass per iteration is the Blender model).
  - **Replace the current weighted kernel (cardinals 1.0, diagonals 0.5, center 4.0) with uniform weight 1.0 on all 8 neighbors plus center 0.0.** This is Blender's `mix_indices`.
  - For the **target-toward-snapshot** behavior, the per-vertex blend should target `lerp(origHeights[v], neighborAvg, perPassStrength)`, not `lerp(currentHeights[v], neighborAvg, ...)`. This is the "accumulate = false" mode from Section 5.
- **Effort:** 1 day. Touches one method plus a snapshot dictionary on `TileBrushDrag`.
- **Acceptance:**
  - Holding Smooth on a hill converges to a smooth slope and **stops** (no further depression past the smoothed limit).
  - A single dab at full strength visibly smooths more than today (4x iterations).
  - Visible "tile boundary kink" goes away when smoothing across the boundary (uniform kernel does not bias toward axis-aligned diagonals like the weighted one does).

### 7.3 Phase B-3: Lazy-pointer cursor filter

- **File:** `Persistence/TileBrushDrag.cs` (sits in front of B-1's dab pump).
- **Math:**
  - Add `Vector3 _brushPos` (the rendered/active brush position), separate from `Input.mousePos` raycast hit.
  - Per `Update`:
    ```
    target = raycastHit;
    if ((target - _brushPos).sqrMagnitude < smoothStrokeRadius * smoothStrokeRadius) return;
    _brushPos = Vector3.Lerp(target, _brushPos, smoothStrokeFactor);
    ```
  - Feed `_brushPos` (not `target`) into the dab pump from B-1.
  - Defaults: `smoothStrokeFactor = 0.5f` (Blender's 0.9 is too laggy for FPS-perspective; 0.5 is the right tuning per playtesting in similar 3D sculpt mods). `smoothStrokeRadius` = 0.25m in world units (~1/4 of the 1m vertex grid).
  - Make both configurable. Allow `smoothStrokeFactor = 0` to disable (raw cursor).
- **Effort:** Half a day. Pure prepend in front of B-1.
- **Acceptance:**
  - Micro-jitter from raycast wobble disappears.
  - Sweeping the brush feels like dragging a tethered ball, not a laser pointer.
  - Holding the cursor still does NOT slowly creep the brush (dead zone works).
  - Can be turned off for users who hate the lag.

### 7.4 Phase B-4: Brush curve preset library (optional polish)

- **File:** new `Persistence/BrushFalloffCurves.cs` plus call site in `TileBrush.ApplyRaiseToTile / ApplyFlatToTile / ApplyDropToTile`.
- **Math:** All 9 named curves from Section 2.1. Input `p = 1 - dist/radius`. Return curve value, used as the strength multiplier instead of today's smoothstep-only edge band.
- **Effort:** 1 day including UI dropdown in the tool config.
- **Acceptance:** User can pick "Smooth", "Smoother", "Sphere", "Sharp" etc. per-tool and see different falloff shapes immediately.

### 7.5 Phase B-5: Per-frame event coalescing (low priority)

- **File:** `TileBrushDrag.cs Update`.
- **Math:** If FAT ever receives multiple cursor events per Unity `Update` (currently it does not; Unity samples mouse once per frame), average them via Blender's `calc_average_sample`. Skip until/unless we move to Unity Input System with high-frequency sampling.
- **Effort:** 2 hours when needed.

## 8. What FAT will still NOT have after porting (and why it's fine)

- **PBVH spatial indexing.** Skipped — the 1m grid + `O(1)` integer query is strictly better for our regular topology. Documented in Section 4.2.
- **Surface Smooth (HC Laplacian with volume preservation).** Skipped — the alpha/beta volume-preservation is for arbitrary watertight meshes; on a terrain heightfield "volume" is a column under each cell and a plain Laplacian doesn't melt it the way it melts a 3D mesh. We get the benefits for free from the heightfield constraint.
- **Custom CurveMapping editor.** Skipped — the 9 named presets cover the design space; a full bezier editor is YAGNI for terrain.
- **Pressure curve remapping.** Skipped — FAT has no pressure input source (no tablet integration). Strength is keyboard/UI scalar.
- **Sticky stroke.** Skipped — Blender's stickiness emerges from the stroke remainder carry (B-1 gives us this); the explicit BRUSH_STICKY flag is for masking strokes to a specific surface, not relevant.
- **Symmetry passes.** Skipped — terrain has no natural symmetry plane.

## 9. Open questions for the user

1. **Default `smoothStrokeFactor` for the lazy pointer.** Blender ships 0.9 which is heavy lag. For an FPS-perspective terrain sculpt I'd suggest 0.5 as default, but the right number is taste — do you want to playtest 0.3 / 0.5 / 0.7 and pick?
2. **B-2 snapshot lifetime.** Should the `origHeights` snapshot reset (a) when the mouse button is released, (b) when the brush stops moving for N ms, or (c) when the user explicitly clicks again? Blender uses (a). I'd recommend (a) for consistency, but (b) gives a "tap-and-pause-to-converge" workflow some users may prefer.
3. **Should B-4 (curve presets) be per-tool or global?** Blender is per-brush. FAT could keep it simple with a single global curve until the design surface area demands per-tool curves.
