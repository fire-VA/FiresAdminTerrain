# Vanilla Terrain Zone-Stitching — How It Actually Works

## 0. TL;DR

Vanilla does **not** stitch borders after the fact. It avoids the seam by
(a) routing every terrain op through **every overlapping zone's
TerrainComp** so border verts are written N times — once per neighbor zone
— with **identical math** that is purely a function of world XZ, and
(b) letting each zone's `Heightmap` regenerate its base heights via the
deterministic `HeightmapBuilder` (also a function of world XZ). Adjacent
zones therefore arrive at the **same Y** at the shared world XZ without
any cross-zone read. There is no "stitch pass", no neighbor read at
render time, no geometry weld. The pipeline guarantees equality by
construction.

## 1. The data model

### 1.1 Heightmap fields

| Field | File:Line | Notes |
| --- | --- | --- |
| `m_width = 32` | `Heightmap.cs:74` | tile is 32 quads across at scale 2 → 64m |
| `m_scale = 1f` (overridden to 2 in prefab) | `Heightmap.cs:75` | 2m vertex spacing |
| `m_heights` (`List<float>`, length `(m_width+1)^2 = 1089`) | `Heightmap.cs:83` | per-vertex Y after modifiers applied. Cleared/rebuilt every `Generate()`. |
| `m_paintMask` (`Texture2D 33×33`) | `Heightmap.cs:85` | cleared mask + cultivated/dirt/paved RGBA |
| `m_buildData.m_baseHeights` | `HeightmapBuilder.cs:111-149` | the worldgen-deterministic base. Identical at shared XZ across neighbors. |
| `m_cornerBiomes[4]` | `Heightmap.cs:92` | 4 corners, used for biome blend |

Heightmap GameObject's transform.position sits at the zone center
(`ZoneSystem.GetZonePos`, `ZoneSystem.cs:1825-1828`). Vertex (i, j) world
XZ is `zoneCenter + (-32 + i*scale, _, -32 + j*scale)`.

### 1.2 What's authoritative vs derived

- **Authoritative**: `m_buildData.m_baseHeights` (worldgen, deterministic
  on world XZ) + `TerrainComp.m_levelDelta[]` and `m_smoothDelta[]`
  (player edits, persisted on the comp's ZDO as `ZDOVars.s_TCData`,
  `TerrainComp.cs:76-110`).
- **Derived**: `Heightmap.m_heights[idx] = baseHeights[idx] +
  levelDelta[idx] + smoothDelta[idx]`, clamped to `±8m` from base
  (`TerrainComp.ApplyToHeightmap`, `TerrainComp.cs:209-245`).
- **Derived**: the rendered mesh (`m_renderMesh`) and collision mesh
  (`m_collisionMesh`) are rebuilt from `m_heights` on every `Regenerate()`.

Key fact: the `Heightmap` itself doesn't persist anything. The
`TerrainComp` (the prefab Heightmap spawns when first edited,
`Heightmap.cs:992-996`) owns the delta arrays and is what gets saved /
synced. The Heightmap is a pure render-from-buildData+comp object.

### 1.3 Border vertex ownership — answer to **Q1**

**(b) — Two separate vertices that the edit pipeline GUARANTEES will
have the same value.** Each zone owns its own 33×33 grid; the
easternmost column of zone A and the westernmost column of zone B are
physically separate floats in different `List<float>`s in different
GameObjects. They land on the same world XZ because zone spacing (64m)
equals `m_width * m_scale` (32 × 2 = 64m) exactly, and they receive the
same value because:

1. `HeightmapBuilder.Build` computes height purely from `(wx, wy)`
   (`HeightmapBuilder.cs:118-149`) — same world XZ → same base float.
2. `TerrainComp.RaiseTerrain` / `LevelTerrain` / `SmoothTerrain` all
   convert world XZ → local vertex index via
   `m_hmap.WorldToVertex(worldPos, out x, out y)`
   (`TerrainComp.cs:296, 330, 389`). The math inside the loop uses the
   per-vertex world distance from the brush center (the *world*
   distance, via `Vector2.Distance` on grid coords with deterministic
   spacing) → same edit at same world XZ produces same delta.

No geometry weld; the meshes are independent. The data is guaranteed
equal because the inputs to the equations are equal.

## 2. The edit pipeline

### 2.1 TerrainOp.Apply walkthrough

`TerrainOp.Awake` (`TerrainOp.cs:24-34`) is the entire entry point. When
the player throws a hoe/pickaxe op:

```
List<Heightmap> heightmaps = new();
Heightmap.FindHeightmap(this.transform.position, this.GetRadius(), heightmaps); // <-- ALL overlapping
foreach (Heightmap heightmap in heightmaps)
    heightmap.GetAndCreateTerrainCompiler().ApplyOperation(this);              // <-- one ApplyOperation per zone
```

`Heightmap.FindHeightmap(pos, radius, list)` (`Heightmap.cs:865-872`)
collects every heightmap whose 64m bounds intersect the brush sphere via
`IsPointInside(point, radius)` (`Heightmap.cs:846-851`). A 10m brush at
a boundary returns 2-4 heightmaps; each gets the SAME `TerrainOp` ref.

`TerrainComp.ApplyOperation` (`TerrainComp.cs:247-253`) serializes the
op + world position into a ZPackage and sends an RPC to the comp's
owner. On the owner, `RPC_ApplyOperation` (`TerrainComp.cs:255-263`)
calls `DoOperation` → `InternalDoOperation` → `RaiseTerrain` /
`LevelTerrain` / `SmoothTerrain` / `PaintCleared` (`TerrainComp.cs:265-290`).

### 2.2 Multi-zone propagation — answer to **Q2**

**(b)** — vanilla iterates over each overlapping zone's TerrainComp and
re-walks the affected vertices inside that comp. The single
`TerrainOp.Awake` loop above is the dispatcher: one RPC per
neighbor-zone, each comp computes its own per-vertex deltas
independently, both write to their own delta arrays at the shared
world XZ.

The crucial detail: in `TerrainComp.RaiseTerrain`
(`TerrainComp.cs:321-383`), the per-vertex loop accepts
`x2 ∈ [0, m_width]` and `y2 ∈ [0, m_width]` inclusive — that is, it
**writes the border vert at i=32 in zone A and the border vert at i=0
in zone B**. Both compute the same `delta * falloff` because their
world distance from the brush center is identical (the brush center is
the same Vector3, the world positions of the two verts are identical).

There is NO leak / NO copy-from-neighbor / NO post-op stitch. Each comp
writes its own copy. Equality is by construction.

### 2.3 ZDO sync timing — answer to **Q3**

`DoOperation` (`TerrainComp.cs:265-275`) calls `Save()` synchronously
after writing the deltas. `Save()` (`TerrainComp.cs:76-110`) serializes
the entire `m_modifiedHeight`/`m_levelDelta`/`m_smoothDelta`/
`m_modifiedPaint`/`m_paintMask` set into one byte array on the
comp's ZDO `s_TCData` key, then bumps `DataRevision`. The `Update` loop
on every other client polls `CheckLoad()` (`TerrainComp.cs:56-126`) and
re-decodes on a rev mismatch, then calls `m_hmap.Poke(false)` to
trigger `Regenerate` → `RebuildRenderMesh`.

There is no "sync borders before save" step. Each comp's save is
independent. Because each comp received its own `RPC_ApplyOperation`
call, each comp's save fires independently, and the receiving client
re-renders each affected zone independently.

## 3. The render pipeline

### 3.1 RebuildRenderMesh walkthrough

`Heightmap.Regenerate` (`Heightmap.cs:221-234`) does:

1. `Generate()` — rebuilds `m_heights` from `m_buildData.m_baseHeights`,
   applies `TerrainModifier`s, then applies the matching `TerrainComp`'s
   delta arrays via `terrainCompiler.ApplyToHeightmap(...)`
   (`Heightmap.cs:355-383`).
2. `RebuildCollisionMesh` (`Heightmap.cs:459-511`) — walks (m_width+1)²
   vertices via `CalcVertex(x, y)` (`Heightmap.cs:414-421`), which
   reads `m_heights[y*num+x]`. **Border vertices included.**
3. `RebuildRenderMesh` (`Heightmap.cs:513-556`) — takes the collision
   mesh's vertices verbatim and adds biome colors + UVs.

### 3.2 Border vertex resolution — answer to **Q4**

**The render path is strictly self-contained.** `CalcVertex` reads only
`this.m_heights[idx]`. There is no read from any neighbor heightmap at
mesh build time. The mesh on zone A's east edge and the mesh on zone
B's west edge meet because their respective `m_heights` floats are
equal — guaranteed by section 2.2.

`ZoneSystem.GetGroundHeight` / `GetSolidHeight`
(`ZoneSystem.cs:1608-1688`) don't query heightmaps at all — they
raycast against the collision mesh. So even the "which zone owns this
XZ?" question doesn't come up at query time; whichever zone's collision
mesh the ray hits answers.

## 4. Why this works (and FAT's current approach doesn't)

1. **FAT's brush math is per-tile-local, vanilla's is per-zone-local
   with shared world inputs.** Both look the same on paper, but FAT's
   `ApplyRaiseToTile` is called once per overlapping tile with the same
   brush center — same as vanilla. So that part is fine.
2. **FAT runs a post-op `SmoothHeightsInRadius` per tile (`TileBrush.cs:108`
   and `:57`).** This is the divergence point. The smoothing kernel
   reads `srcHeights` of the current tile only (`TileBrush.cs:723-784`),
   and at the border it asks `AccumulateNeighbor` for the cross-tile
   neighbor (`TileBrush.cs:796-840`). Tile A's border vert smooths
   against B's *pre-smooth* values; tile B's border vert smooths against
   A's *pre-smooth* values; the two pass results are NOT equal because
   the input source for each side is different.
   **Vanilla doesn't smooth this way.** `TerrainComp.SmoothTerrain`
   (`TerrainComp.cs:385-414`) writes to `m_smoothDelta` (a sparse
   delta), not directly to heights, and the smoothing math is also
   purely a function of world XZ from the brush center.
3. **FAT writes `Heights[idx] = base + delta` directly, vanilla writes
   the delta and lets the render step combine.** FAT's `Heights[idx] =
   baseH + scaledDelta` (`TileBrush.cs:686`) folds base+delta together.
   Two tiles touching the same border vert with slightly different
   `existingH` baselines (e.g. one tile had a previous edit, the other
   didn't) produce different `Heights` results even with identical brush
   contribution.
4. **FAT's post-op stitch (`StitchTileBorders`, `TileBrush.cs:934-999`)
   averages border verts AFTER both have been written and smoothed.**
   Averaging is the wrong operation if the two sides used different
   baselines — averaging the *result* doesn't undo the divergent
   baselines. Worse, the stitch only runs at the brush's own touched
   tiles; if smoothing pulled a tile's borders outside the brush radius
   to a new value, the stitch may not even include those tile pairs.
5. **FAT may be re-rendering tiles before the stitch fires.** If
   `ApplyOpAndSave` triggers a mesh rebuild inside the brush loop
   (before the post-op `StitchTileBorders`), the mesh shows the
   pre-stitch divergence and the user sees a seam even though the data
   is "stitched" on the next frame. Worth verifying with a step log:
   does `wroteThisTile > 0` rebuild immediately, or queue?

## 5. Porting plan for FAT

### 5.1 Option A — Match vanilla's "delta + deterministic baseline"
**model**

Refactor FAT to store **per-vertex deltas** (`HeightDelta[]`), not final
heights. Final `Heights[idx] = baselineY(worldXZ) + HeightDelta[idx]`,
computed at render time. The baseline can be: ground height under the
tile when first created (snapshot once into `BaselineHeights[]` and
keep), OR computed live by raycasting vanilla terrain.

- **Effort**: Medium. Codec needs a new field for the baseline snapshot
  (or commit to live raycast at mesh-build), brush math changes from
  `Heights[idx] = ...` to `HeightDelta[idx] += ...`, mesh combiner reads
  the sum.
- **Risk**: Touches every brush op + the combiner. Floating island
  height-target logic needs to be reconsidered (FAT tiles aren't always
  near vanilla ground).
- **Payoff**: Border verts converge mathematically because the brush's
  delta contribution is purely a function of world XZ, and the baseline
  is the same at the shared XZ for both tiles. No stitch needed.

### 5.2 Option B — Fix the existing stitch

Three likely root causes of the current stitch failing, ordered by
suspicion:

1. **Mesh rebuild before stitch.** Add a `bool deferRebuild` parameter
   to `ApplyOpAndSave` so the brush loop writes data without rebuilding,
   then `StitchTileBorders` runs, then a final `RebuildMeshes` pass
   rebuilds all touched tiles. Verify via log: how many times does
   `RebuildMesh` (or the combiner's equivalent) fire for one brush op?
   Expected ≤ once per tile, AFTER stitch.
2. **`SmoothHeightsInRadius_Single` desyncs borders.** The
   `AccumulateNeighbor` cross-tile lookup is sound for ONE tile's smooth
   pass, but when each tile smooths independently, the two sides see
   different neighbor data. Fix: smooth ALL touched tiles into snapshots
   first, then write back — OR drop the smooth post-pass for border
   verts (don't smooth verts at i=0 / i=64 / j=0 / j=64).
3. **`existingH` baseline divergence in `ApplyRaiseToTile` /
   `ApplyFlatToTile`.** When `tile.HeightModified[idx]` is false on one
   tile but true on the other, the two sides start from different
   baselines (`center.y` vs the existing height). Fix: at the border,
   resolve `existingH` by querying the neighbor tile if the local vert
   is unmodified, OR (simpler) always use `center.y` as the baseline at
   the border.

Test cases to add:

- Single-stamp raise straddling a tile border, both tiles previously
  unmodified. Log: are the two border-vert Heights equal after the brush
  but before the stitch?
- Same but with one tile already modified at the border (from a prior
  op).
- Single-stamp raise on a 4-tile corner (verts at i=0,j=0 on one tile
  coincide with i=64,j=64 on the diagonal neighbor — verify all 4 tiles
  agree).
- Drag-stroke across a border with the smooth post-pass on. Compare the
  two sides' Heights after each dab.

### 5.3 Option C — Render-time stitch

At combiner weld time, for every border vert pair, average the two
sides' Heights and use the average for both tiles' mesh vertices. Same
as Option B's "post-op average" but moved to mesh-build instead of data-
write.

- **Pro**: doesn't change the data model. Stitch is purely visual.
- **Con**: queries via `Heightmap.GetHeight(worldXZ)` (e.g. piece snap,
  ZoneSystem.GetGroundHeight) still see the divergent underlying data.
  Player can fall through a seam even though it looks fine.

## 6. Recommended path

**Option B first (fix the stitch), then Option A as a follow-up.** The
data-divergence root causes in 5.2 are small, testable, and fixable
without a codec change. Specifically: defer mesh rebuilds until after
the stitch (root cause #1) and switch the post-op smooth to a
snapshot-then-write-back model (root cause #2) — those two changes
alone are likely to make the seams go away.

If after that there are still residual seams in long-stroke / re-edit
scenarios, escalate to Option A. Option A is strictly stronger but
costs a codec migration; do it only after the cheap fixes are exhausted.

## 7. Open questions for the user

1. Does `ApplyOpAndSave` currently trigger a mesh rebuild on the spot,
   or is it deferred to a `LateUpdate` poke? (If the former, root cause
   #1 in 5.2 is locked in.)
2. Are seams visible BEFORE the post-op stitch fires (one-frame
   artifact), or PERMANENT (data divergence)? Pause the game right
   after a problematic stroke and inspect `tile.Heights[idx]` at the
   shared border via a debug command.
3. Is the seam only along the vertical Y axis (ridge / cliff in the
   mesh) or also misaligned in XZ (verts not at the same world
   position)? If the latter, the bug is in the world-to-vertex math,
   not the height value.
4. Does this happen with `Square` brush stamps too, or only with the
   smoothstep falloff path? Square stamps skip `SmoothHeightsInRadius`
   (`TileBrush.cs:57`) — if they're seamless and the round ones aren't,
   that locks the bug to the smooth post-pass.
