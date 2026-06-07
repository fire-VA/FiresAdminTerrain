# Auto-Generated Rivers & Water — Implementation Plan

## Overview

Use Valheim's vanilla `WorldGenerator` river/stream/lake data (as documented by Expand World Rivers)
to automatically generate custom water bodies using our existing `WaterDisk` / `WaterMeshCombiner` system.

**Goal:** When the world generates, intercept the river/stream/lake data and spawn our custom water
along those paths — no manual placement needed. Rivers flow between lakes with proper width curves,
streams connect elevated areas to shorelines, and lakes fill terrain depressions.

**Key Insight from EWR:** Vanilla rivers are NOT water objects. They're just terrain height depressions
that sit below the global ocean `m_waterLevel` (default 30). The ocean mesh fills them passively.
Our `WaterMeshCombiner` creates actual water meshes with waves, swimming triggers, and proper depth.

**TWO DETECTION MODES:**

**Mode A — BC Heightmap Direct Height Analysis** (preferred when BC heightmap is present):
- Reads BC's `ImageMapFloat.Map` (float[] of normalized heights) directly via reflection
- Uses BC's **exact height formula**: `worldY = (pixelValue * HeightmapAmount - 0.15 + SeaLevelAdjustment) * 200`
- This is the SAME formula BC's `GetBiomeHeightWithHeight` postfix uses — perfect terrain alignment
- Computes neighborhood average height for relative depression detection (catches rivers at ANY elevation)
- Connected depressed pixel regions = water bodies (rivers, lakes, deltas)
- Classifies by shape: fill ratio + aspect ratio + average width analysis (not just bounding box)
- For rivers: traces centerline via weighted cross-section scanning, produces **3 height references per point**:
  1. **HeightmapTerrainY** — exact bed height from heightmap's lowest bed pixel (darkest = deepest)
  2. **DepressionDepth** — channel depth below banks in meters (from depression map). Darkest depression
     pixels at highest elevations = river origin springs that flow downhill to lower elevations
  3. **BankY** — lowest non-depressed edge pixel height at each cross-section (the actual bank terrain)
- For lakes: finds fill level from lowest adjacent bank pixel, exact surface height from heightmap
- **3-source cross-reference pass** (main thread): at each centerline point samples real terrain via
  `WorldGenerator.GetHeight()` AND real bank terrain at channel edges, then applies 3 constraints:
  - `bedY = min(heightmapBed, realTerrain)` — water never above actual ground
  - `surfaceY = bedY + offset` — water hugs the riverbed
  - `surfaceY <= min(heightmapBank, realBank) - margin` — water never crests the banks
- Runs entirely on the float[] copy — background thread safe (detection), main thread (cross-reference)
- File: `River/BCHeightmapWaterDetector.cs`
- Reference: `Debug/DebugRefs/BC_IMAGEMAPFLOAT_REFERENCE.txt`

**Mode B — GetRiverWeight-Based Channel Detection** (vanilla worlds / BC without heightmap):
- Phase 1 computes a sinusoidal "guide path" (rough corridor)
- Phase 2 uses `WorldGenerator.GetRiverWeight(wx,wy, out weight, out width)` to find the ACTUAL river channel
  - Vanilla signature: `private void GetRiverWeight(float wx, float wy, out float weight, out float width)`
  - This is the EXACT same function every biome height method calls via `AddRivers()`
  - `weight`: 0-1 river influence at any XZ position (0 = not river, 1 = center)
  - `width`: weighted average river width at that point
  - Where weight > 0, the terrain is carved — that's the real river
- Falls back to cross-section terrain sampling if GetRiverWeight is not accessible

**Both modes then:**
- Water surface = `max(waterLevel, terrain + offset)` — starts at Y=30, rises with terrain
- Monotonic flow enforcement: water rises smoothly from sea-level end toward higher terrain
- Terrain sampling on main thread, disk placement rate-limited

---

## Reference Files

| File | Contents |
|------|----------|
| `Debug/DebugRefs/EWR_WORLD_GENERATION_REFERENCE.txt` | Lake finding, river placement, stream placement algorithms, WorldGenerator data structures |
| `Debug/DebugRefs/EWR_WATER_VOLUME_REFERENCE.txt` | Water level management, wave patches, WaterVolume system |
| `Debug/DebugRefs/BC_RIVER_COMPATIBILITY_REFERENCE.txt` | Better Continents compatibility analysis — what BC patches, HeightmapOverrideAll scenario, detection code |
| `Debug/DebugRefs/BC_IMAGEMAPFLOAT_REFERENCE.txt` | Decompiled BC ImageMapFloat class, exact height pipeline formula, coordinate system, pixel?world conversion |

---

## Architecture

### Data Source: `WorldGenerator`

After `WorldGenerator.Pregenerate()` runs, these lists are populated:

```
WorldGenerator.instance.m_lakes   : List<Vector2>              — lake center positions (XZ)
WorldGenerator.instance.m_rivers  : List<WorldGenerator.River>  — river paths between lakes
WorldGenerator.instance.m_streams : List<WorldGenerator.River>  — stream paths (shorter, narrower)
```

Each `WorldGenerator.River` contains:
- `p0`, `p1` — start/end Vector2 (lake positions)
- `center` — midpoint Vector2
- `widthMin`, `widthMax` — width range
- `curveWidth`, `curveWavelength` — sinusoidal curve parameters

### Our System

We place `WaterDisk` objects along the paths and let `WaterMeshCombiner` handle mesh generation.
Each river/stream becomes a series of overlapping `WaterDisk` objects that combine into one mesh.

```
WorldGenerator.River ? Calculate path points ? Place WaterDisks along path ? WaterMeshCombiner
```

---

## Phase 1: River Path Sampling

### Goal
Convert `WorldGenerator.River` data into a series of world-space points with widths.

### Algorithm

```
For each River in m_rivers:
  1. Calculate a DENSE guide path between p0 and p1 (every 4m):
     - Base direction = (p1 - p0).normalized
     - Perpendicular = rotate base 90°
     - At each step along the path:
       offset = curveWidth * sin(distance / curveWavelength)
       point = lerp(p0, p1, t) + perpendicular * offset
  
  2. At each guide point, scan perpendicular using GetRiverWeight():
     - Scan ±60m perpendicular to path direction in 2m steps
     - Find weighted center of channel (where weight is highest)
     - Find actual width (extent where weight > 0.05)
     - This gives us the REAL channel center and width
  
  3. At each refined point:
     - TerrainY = sample terrain at channel center (WorldGenerator.GetHeight)
     - Surface Y = max(waterLevel, terrainY + RiverSurfaceOffset)
       - Where terrain < waterLevel ? water sits at waterLevel (matches ocean)
       - Where terrain >= waterLevel ? thin water layer on terrain
```

### Key Considerations
- **GetRiverWeight() is the source of truth** — it's the EXACT same function Heightmap.Generate() uses
  to carve the terrain. Our sinusoidal curve is just a rough guide; GetRiverWeight tells us the REAL channel.
- River paths are 2D (XZ only) — we sample terrain cross-sections for Y (lowest point = riverbed)
- Rivers in vanilla sit at `m_waterLevel` (30) — they're ocean-level channels
- Our water anchors to `ZoneSystem.m_waterLevel` — starts at Y=30 where terrain is below it
- Where terrain rises above water level, water follows terrain with `RiverSurfaceOffset` (0.3m)
- Monotonic flow: surface never dips below running minimum from sea-level end to high end
- Dense sampling (every 4m) eliminates the patchy gaps from the old approach (every 18-30m)
- Works with Better Continents: `GetRiverWeight()` includes BC modifications automatically
- Falls back to cross-section terrain sampling if `GetRiverWeight` is not accessible via reflection

---

## Phase 2: WaterDisk Placement

### Goal
Place `WaterDisk` objects along the sampled path.

### Strategy

```
For each sampled point along a river path:
  Create WaterDisk:
    - Position: (point.x, surfaceY, point.z)
    - Radius: width / 2 (half the river width at this point)
    - MaxDepth: configurable (default 5m)
    - LiquidType: Water (or configurable)
    - Shape: Circle (natural river look)
  
  Spacing: Place disks every (radius * 0.6) meters for good overlap
  This ensures WaterDisk.OverlapsWith() returns true ? single combiner
```

### Combiner Strategy
- All disks along one river should end up in ONE `WaterMeshCombiner`
- Use `WaterMeshCombiner.GetOrCreateCombinerFor()` — overlap detection handles grouping
- Rivers are in RIVER MODE (height variance > 3m tolerance) ? smooth height interpolation
- Short rivers or lake fills are in POND MODE (flat surface)

---

## Phase 3: Lake Generation

### Goal
Fill detected lakes with our custom water.

### Strategy

```
For each lake position in m_lakes:
  1. The position is the lake CENTER (Vector2 ? Vector3 with terrain Y)
  2. Sample terrain in a grid around the center to find the depression extent
  3. Place a large WaterDisk (or multiple) to cover the depression
  4. Surface Y = water level at this area
  
  Lakes are ponds ? POND MODE in WaterMeshCombiner (flat surface)
```

### Lake Detection Enhancement
- Vanilla lakes are just terrain below `m_waterLevel`
- We can scan outward from the center to find the actual lake boundary
- Place one large disk or several overlapping disks to fill the area
- MaxDepth should match the terrain depression depth

---

## Phase 4: Stream Generation

### Goal
Place thin water paths for streams (shorter, narrower than rivers).

### Strategy
Same as rivers but:
- Narrower (width 10-20m vs 60-100m)
- Shorter (80-200m vs potentially kilometers)
- Start near water level, end at higher elevation
- Surface Y follows terrain more closely (streams flow downhill)

---

## Phase 5: Integration Point

### When to Generate

**Option A: Patch `WorldGenerator.Pregenerate()` Postfix**
```csharp
[HarmonyPatch(typeof(WorldGenerator), "Pregenerate")]
[HarmonyPostfix]
static void GenerateCustomWater(WorldGenerator __instance)
{
    if (__instance.m_world.m_menu) return;
    
    AutoWaterGenerator.GenerateFromWorldData(
        __instance.m_lakes,
        __instance.m_rivers,
        __instance.m_streams
    );
}
```

**Option B: On Zone Load (Lazy)**
```csharp
// When a zone loads, check if it intersects any river/stream/lake paths
// Only generate water for visible zones
// Pro: Lower memory, only creates water near player
// Con: Water appears/disappears as zones load/unload
```

**Recommended: Option A** — Generate all water data at world init, use our existing
zone-based streaming (`ZonePaintManager.StreamLayers()`) for memory management.

### Persistence — Generate Once, Persist Via Anchors

Auto-generated water **MUST persist** to ZDO anchors, same as player-placed water. Reasons:

1. **Mop tool edits survive logout** — If a player mops away part of a river, that edit
   lives in the anchor's saved grid state. Non-persistent water would lose mop edits.
2. **No duplicate conflicts** — Player-placed water can merge with auto-gen water via
   `GetOrCreateCombinerFor()`. If we re-generated each session, we'd create duplicates
   on top of already-merged combiners.
3. **Faster load times** — Restoring from anchor ZDOs (deserialize arrays ? rebuild mesh)
   is much faster than re-running terrain sampling + disk placement + mesh generation.
4. **Multiplayer sync** — Anchors sync via ZDO automatically. Regenerating independently
   per-client risks inconsistent results if terrain state differs.
5. **One code path** — No special-casing for "auto-gen vs player-placed" in the combiner,
   anchor, save, or mop logic. Same water, same persistence.

**Strategy:**

```
First world load (no existing auto-gen anchors):
  1. WorldGenerator.Pregenerate() populates m_rivers, m_streams, m_lakes
  2. AutoWaterGenerator reads those lists, places WaterDisks
  3. GetOrCreateCombinerFor() creates combiners + anchors (debounced save)
  4. Anchors save to ZDOs ? persist like any other water

Subsequent loads (anchors already exist):
  1. Anchors restore from ZDO automatically (existing system)
  2. AutoWaterGenerator checks for existing anchors near river paths
  3. Skips generation where persistent water already exists
  4. Only generates for NEW rivers (e.g., if EWR config changed and added more)
```

**Duplicate Detection:**
```csharp
// Before placing disks for a river, check if water already exists along the path
bool HasExistingWaterAlongPath(Vector2 p0, Vector2 p1, float checkRadius)
{
    int samples = Mathf.CeilToInt((p1 - p0).magnitude / checkRadius);
    for (int i = 0; i <= samples; i++)
    {
        float t = (float)i / samples;
        Vector2 pos = Vector2.Lerp(p0, p1, t);
        Vector3 worldPos = new Vector3(pos.x, 0, pos.y);
        
        // Check if any existing combiner has water here
        if (WaterMeshCombiner.GetCombinerAt(worldPos) != null)
            return true;
    }
    return false;
}
```

**Cleanup on world gen change:**
- If a player regenerates the world (via EWR or similar), existing auto-gen anchors
  stay unless explicitly cleared. This is fine — the water is still valid geometry.
- Add a console command `va_clear_auto_water` to manually purge if needed.

---

## Phase 6: Configurable Parameters

### Config Settings

```
[Auto Water]
Enable auto rivers = true
Enable auto streams = true
Enable auto lakes = true
River disk spacing factor = 0.6     (fraction of radius for disk spacing)
River max depth = 5                  (meters)
Stream max depth = 2                 (meters)
Lake max depth = 8                   (meters)
River liquid type = Water            (Water, Tar, etc.)
Lake liquid type = Water
Stream liquid type = Water
```

---

## Implementation Order

| Step | Task | Status | Files |
|------|------|--------|-------|
| 1 | Create `AutoWaterGenerator.cs` with path sampling math | ? DONE | `River/AutoWaterGenerator.cs` |
| 2 | Add river path ? WaterDisk conversion | ? DONE | `River/AutoWaterGenerator.cs` |
| 3 | Add stream path ? WaterDisk conversion | ? DONE | Same (reuses `ComputeRiverPath`) |
| 4 | Add lake ? WaterDisk conversion | ? DONE | `River/AutoWaterGenerator.cs` |
| 5 | Add duplicate detection (`HasExistingWaterAlongPath`) | ? DONE | `River/AutoWaterGenerator.cs` |
| 6 | Hook into `WorldGenerator.Pregenerate()` postfix | ? DONE | `River/AutoWaterPatches.cs` |
| 7 | Add WorldGenerator field access (reflection) | ? DONE | `River/WorldGeneratorAccess.cs` |
| 8 | Add debug commands (`va_rivers`, `va_lakes`, `va_clear_auto_water`) | ? DONE | `Tools/WaterDebugCommands.cs` |
| 9 | **BC heightmap depression analysis** | ? DONE | `River/BCHeightmapWaterDetector.cs` |
| 10 | Wire heightmap detection into AutoWaterGenerator | ? DONE | `River/AutoWaterGenerator.cs` |
| 11 | Test with vanilla world generation | ? Pending | — |
| 12 | Test with BC HeightmapOverrideAll world | ? Pending | — |

### Files Created
- `Modules/CustomWater/River/AutoWaterGenerator.cs` — Core generation logic (path math on background thread, terrain sampling as coroutine, disk placement rate-limited). Three modes: BC heightmap, WorldHeightCache, or vanilla GetRiverWeight.
- `Modules/CustomWater/River/AutoWaterPatches.cs` — Harmony postfix on `WorldGenerator.Pregenerate()` + logout cleanup (clears WorldHeightCache)
- `Modules/CustomWater/River/WorldGeneratorAccess.cs` — Cached reflection accessors for private `m_rivers`/`m_streams`/`m_lakes`/`m_world` fields + BC state detection
- `Modules/CustomWater/River/BCHeightmapWaterDetector.cs` — Reads BC's heightmap `float[]` via reflection, uses BC's exact height formula `(pixel * Amount - 0.15 + SeaLevelAdj) * 200` for terrain heights, detects rivers/lakes/streams via relative depression analysis (neighborhood average comparison, flood-fill, centerline tracing). Produces 3 height references per river point: heightmap bed Y, depression depth (meters below banks), and bank Y (lowest adjacent non-depressed edge pixel). Cross-referenced by AutoWaterGenerator with real terrain sampling. Now supports `IsWorldHeightCache` flag for WorldHeightCache snapshots where Map[] values are already world Y heights.
- `Modules/CustomWater/River/WorldHeightCache.cs` — **NEW** Samples `WorldGenerator.GetHeight()` across the entire world into a grid, producing a `HeightmapSnapshot` with actual final terrain heights (includes ALL BC modifications: roughmap, flatmap, noise layers, river carving, biome heights). Used when BC's in-memory heightmap can't be read via reflection, or for vanilla worlds. Depression thresholds auto-scale for world-Y values.

### Files Modified
- `Modules/CustomWater/Tools/WaterDebugCommands.cs` — Added 9 commands: `va_auto_water_status`, `va_auto_water_generate`, `va_clear_auto_water`, `va_rivers`, `va_streams`, `va_lakes`, `va_find_water`, `va_dump_heightmap`, `va_build_height_cache`

### Height Data Priority (Most Accurate ? Least)

1. **WorldHeightCache** (`IsWorldHeightCache=true`): Samples `WorldGenerator.GetHeight()` on a grid. This is the **exact same function** Heightmap.Generate() uses. Includes ALL BC modifications (heightmap, roughmap, flatmap, noise layers, river carving, biome heights, sea level, edge dropoff). The grid values ARE world Y heights — no conversion formula needed.

2. **BC Heightmap Direct** (`IsWorldHeightCache=false`): Reads BC's `ImageMapFloat.Map[]` via reflection. Only includes the raw heightmap PNG data, NOT roughmap/flatmap/noise/river carving. Uses the simplified formula `(pixel * Amount - 0.15 + SeaLevelAdj) * 200` which misses several processing steps. Still useful as a fast first pass.

3. **GetRiverWeight + GetHeight**: Vanilla mode. Uses the WorldGenerator's river weight data for channel detection and GetHeight() for terrain sampling at individual points. No grid — samples per-point during Phase 2.

### Async/Performance Architecture
- **Phase 0 (main thread coroutine, optional):** WorldHeightCache build — samples GetHeight() across the world in batched frames (1000 samples/frame, ~18s for 1024² grid). Only runs when BC HeightmapOverrideAll is active and BC heightmap can't be read via reflection.
- **Phase 1 (background thread):** River/stream path curve math (sin, lerp, width interpolation) — all pure math, no Unity API
- **Phase 2 (main thread coroutine):** Terrain height sampling — batched 50 raycasts/frame via `WorldGenerator.GetHeight()` + `ZoneSystem.GetGroundHeight()`
- **Phase 3 (main thread, rate-limited):** WaterDisk creation + combiner registration — 5 disks/frame to prevent hitches
- **Duplicate detection:** Samples start/mid/end of each path for existing combiners before placing any disks
- **Persistence:** Uses exact same `GetOrCreateCombinerFor()` ? anchor ? ZDO pipeline as player-placed water

**Note:** No special flags or anchor-skipping needed. Auto-generated water uses the exact same
`GetOrCreateCombinerFor()` ? debounced save ? anchor creation path as player-placed water.
The only addition is duplicate detection (step 5) so we don't re-generate on subsequent loads.

---

## Key Math: River Path Curve Calculation

From vanilla `WorldGenerator.PlaceRivers()` (via EWR transpiler analysis):

```csharp
// Given a River with p0, p1, curveWidth, curveWavelength:

Vector2 direction = (river.p1 - river.p0);
float totalLength = direction.magnitude;
Vector2 normalized = direction / totalLength;
Vector2 perpendicular = new Vector2(-normalized.y, normalized.x);

// Sample points along the path:
float stepSize = diskRadius * spacingFactor;
int numSteps = Mathf.CeilToInt(totalLength / stepSize);

for (int i = 0; i <= numSteps; i++)
{
    float t = (float)i / numSteps;
    float distAlongPath = t * totalLength;
    
    // Sinusoidal curve offset
    float curveOffset = river.curveWidth * Mathf.Sin(distAlongPath / river.curveWavelength);
    
    // Base position (straight line)
    Vector2 basePos = Vector2.Lerp(river.p0, river.p1, t);
    
    // Apply curve
    Vector2 curvedPos = basePos + perpendicular * curveOffset;
    
    // Width at this point (interpolate between min and max)
    float width = Mathf.Lerp(river.widthMin, river.widthMax, 
                              0.5f + 0.5f * Mathf.Sin(distAlongPath * 0.1f));
    
    // Sample terrain height at this XZ (cross-section lowest point)
    float terrainY = SampleTerrainCrossSection(curvedPos.x, curvedPos.y, width);
    
    // WATER-LEVEL-ANCHORED: start at water level, only rise above where terrain does
    float waterLevel = ZoneSystem.instance.m_waterLevel; // 30 default
    float surfaceY;
    if (terrainY < waterLevel)
        surfaceY = waterLevel;  // Below sea level ? water at sea level
    else
        surfaceY = terrainY + RiverSurfaceOffset; // Above sea level ? thin layer
    
    // Create disk
    var disk = new WaterDisk(
        new Vector3(curvedPos.x, surfaceY, curvedPos.y),
        width * 0.5f,  // radius = half width
        maxDepth,
        liquidType
    );
}
```

---

## Better Continents Compatibility

### How BC Affects River Generation

Better Continents modifies terrain generation by patching `WorldGenerator.GetBaseHeight()` to blend
heightmap images into the terrain. This changes WHERE rivers get placed (different terrain = different
lakes = different river paths), but NOT how `GetRiverWeight()` works.

### What BC Patches (relevant to us)

| Function | Patched? | Impact |
|----------|----------|--------|
| `GetBaseHeight()` | ? Yes | Changes terrain heights ? different lake/river placement |
| `GetBiome()` | ? Yes (with biome map) | Changes biome assignments ? different height functions |
| `GetBiomeHeight()` | ? Yes (postfix) | Adds roughmap, heightmap, paint overlays |
| `AddRivers()` | ? Yes (when rivers disabled) | Skips river terrain carving |
| `GetRiverWeight()` | ? Never | Always uses vanilla logic on m_riverPoints |
| `PlaceRivers()` | ? Never by BC | EWR patches this, not BC |
| `Pregenerate()` | ? Never by BC | EWR patches this, not BC |

### Scenario Analysis

**Normal BC (heightmap + rivers enabled):** ? Fully compatible
- BC changes terrain ? different river paths in m_rivers
- GetRiverWeight() works on those paths ? our Phase 2 finds the real channels
- GetHeight() returns BC-modified terrain ? our terrain sampling is correct

**BC with rivers disabled:** ? Handled automatically
- BC's PlaceRivers prefix returns empty list ? m_rivers is empty
- Our code receives empty rivers ? generates nothing for rivers
- Lakes may still be generated from m_lakes (FindLakes still runs)

**BC with HeightmapOverrideAll:** ? Fully handled via heightmap depression analysis
- GetBiomeHeight postfix returns `GetBaseHeight() * 200` directly
- This BYPASSES AddRivers() ? terrain has NO vanilla river channels
- BUT the heightmap creator painted their own rivers as low channels in the PNG
- **SOLUTION:** BCHeightmapWaterDetector reads the heightmap float[] directly
  - Computes neighborhood average per pixel (fast box blur)
  - Finds relative depressions: pixels lower than their surroundings
  - Connected depressed regions ? river/stream/lake water bodies
  - Traces centerlines for elongated regions (rivers)
  - Generates disk grids for compact regions (lakes)
  - **3 height references per river point**: heightmap bed, depression depth, bank Y
  - Cross-referenced with real terrain via WorldGenerator.GetHeight()
  - Bank cap from both heightmap edge pixels AND real terrain at channel edges
- Rivers detected at ANY elevation — no absolute threshold needed
- Falls back to skipping if heightmap cannot be read via reflection

**BC with changed world size:** ? No issues
- BC can set WorldRadius to values other than 10000
- Our code reads positions from m_rivers, doesn't calculate them
- Scan radii are local per-river ? not affected by world size

**BC with sea level adjustment:** ? No issues
- BC adjusts SeaLevelAdjustment in GetBaseHeight() (shifts terrain relative to water)
- ZoneSystem.m_waterLevel stays at 30 ? our GetWaterLevel() is correct
- Rivers still carved to baseHeight ~0.12-0.14 (still below water level 30)

### Vanilla vs BC Curve Formula

Vanilla `RenderRivers()` uses a triple-sin product for organic curves:
```
offset = sin(a) * sin(a * 0.634) * sin(a * 0.3341) * curveWidth
```

Our Phase 1 uses a simpler single-sin approximation:
```
offset = curveWidth * sin(distAlongPath / curveWavelength)
```

This mismatch is harmless — Phase 2's GetRiverWeight() perpendicular scan corrects
the guide path to the REAL channel center. The single-sin just needs to be close enough
for the ±60m scan to find the actual channel.

### Detection Code

`WorldGeneratorAccess` provides BC detection via reflection:
- `IsBetterContinentsLoaded` — is the BC assembly present?
- `IsBetterContinentsEnabled` — is BC enabled for this world?
- `IsBCHeightmapOverrideAll` — is heightmap overriding all terrain?
- `IsBCRiversEnabled` — are rivers enabled in BC settings?
- `HasBCHeightmap` — is a heightmap loaded for this world?
- `GetBCStatusString()` — diagnostic string for logging

`BCHeightmapWaterDetector` provides heightmap reading and analysis:
- `CanReadHeightmap` — can we access BC's ImageMapFloat via reflection?
- `ReadHeightmap()` — copies the float[] + size + world params into a thread-safe snapshot
- `DetectWaterBodies(snapshot)` — runs depression analysis (background thread safe):
  1. Box blur ? neighborhood average at each pixel
  2. Depression map = neighborhoodAvg - localHeight (where > threshold)
  3. Flood-fill connected depressed regions
  4. Classify by shape: elongated ? river/stream, compact ? lake
  5. Trace centerlines / generate disk grids
- `GetStatusString()` — diagnostic string for logging

### Reference

See `Debug/DebugRefs/BC_RIVER_COMPATIBILITY_REFERENCE.txt` for the full decompiled analysis.

---

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Too many WaterDisks ? lag | Limit total disk count, use larger spacing for long rivers |
| Combiner grid size exceeded | Break long rivers into multiple combiners (already handled by overlap grouping) |
| Water conflicts with player-placed water | Uses same `GetOrCreateCombinerFor()` — merges naturally with player water |
| Duplicate water on reload | `HasExistingWaterAlongPath()` skips rivers where anchors already exist |
| Terrain sampling not available during Pregenerate | Sample lazily on first zone load, or use WorldGenerator.GetBaseHeight() directly |
| Rivers crossing custom terrain | Our water system handles terrain checking already — water clips at banks |
| BC HeightmapOverrideAll active | Read heightmap directly via `BCHeightmapWaterDetector` — find painted river channels via relative depression analysis |
| BC changes world size | Positions come from m_rivers (already in correct coords), no bounds issues |

---

## Notes

- EWR modifies vanilla constants via IL transpiler — we DON'T need to do that
- We just READ the vanilla-generated `m_rivers`, `m_streams`, `m_lakes` lists
- If EWR is installed alongside our mod, it modifies those lists — our code still works fine
- We can optionally add our OWN config to generate MORE rivers/streams beyond vanilla
- The `WorldGenerator.River` struct has all the data we need — no decompilation required at runtime
- Better Continents compatibility: two modes — heightmap depression analysis (preferred) or GetRiverWeight fallback
- BC heightmap mode reads the `float[]` directly and converts to exact world heights via `(pixel * Amount - 0.15 + SeaLevelAdj) * 200`
- BC's HeightmapOverrideAll fully supported: heightmap has painted channels, we detect them and compute exact surface heights
- Vanilla mode: BC never patches `GetRiverWeight()`, so our channel detection works as fallback
- BC's triple-sin curve formula differs from our single-sin guide, but Phase 2 corrects via GetRiverWeight
