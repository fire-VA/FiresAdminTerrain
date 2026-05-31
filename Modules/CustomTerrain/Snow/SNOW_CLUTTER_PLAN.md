# Snow Clutter System — Design & Implementation Plan

## Date: 2025-07-15
## Status: ?? IN PROGRESS

---

## Concept

Paintable, interactive snow that has **visible depth**, bends when walked through
like grass, and leaves **persistent trails** that don't bounce back until the
player leaves the zone (zone unload or manual reset).

### Visual Goals

| Aspect | Description |
|--------|-------------|
| **At rest** | Dense white instanced billboards (same shader as grass), short & thick, creating the look of a snowy blanket covering the ground |
| **Player walks through** | Snow bends aside via the vanilla `_PlayerPosition` push, AND a "dirty snow" biome paint is laid behind the player, clearing clutter in the trail |
| **Trail persistence** | Paint stays, clutter stays cleared — the trail doesn't fill back in |
| **Trail reset** | On zone unload, the non-persistent paint is discarded and snow clutter respawns fresh |
| **Dirty snow look** | The paint underneath uses the Mountain biome vertex color (snow texture), but with the vanilla paint mask set to "paved" so the terrain shader shows a slightly dirty/packed snow texture where clutter was removed |

---

## Architecture — The Hybrid Approach

### Why Hybrid?

We already have two systems that do exactly what we need:

1. **`ClutterPaintTool`** — spawns `InstanceRenderer`-based grass clutter that
   gets the vanilla player-push shader effect for free.
2. **`BiomePaintPersistence`** — paints biome vertex colors AND clears
   `ClutterSystem.ResetGrass()` in the painted area, which removes clutter.

The hybrid combines them:

```
???????????????????????????????????????????????????????
?                   SNOW ZONE                         ?
?                                                     ?
?   1. Snow clutter (InstanceRenderer)                ?
?      - Dense white short billboards                 ?
?      - Same shader as grass ? free player push      ?
?      - Painted via paint_clutter system             ?
?      - PERSISTENT (survives zone reload)            ?
?                                                     ?
?   2. Trail paint (non-persistent biome paint)       ?
?      - Painted BEHIND player as they walk           ?
?      - Mountain biome color ? dirty snow texture    ?
?      - Clears vanilla ClutterSystem.ResetGrass()    ?
?      - Clears OUR snow clutter instances in radius  ?
?      - NOT persisted ? resets on zone unload        ?
?                                                     ?
?   Result: Player walks ? trail paint clears snow    ?
?           clutter ? you see packed/dirty snow        ?
?           underneath ? trail stays until zone exit   ?
???????????????????????????????????????????????????????
```

### What Triggers Trail Painting?

A new component, **`SnowTrailController`**, attached to a zone-level
GameObject (similar to `EnvironmentBoxController`). It:

1. Runs in `Update()` — checks player position every N frames
2. If player has moved ? 0.3m since last stamp:
   - Calls `SnowTrailController.StampTrail(playerPos, trailRadius)`
   - This internally calls `ClutterPaintTool.ClearClutterInternal()` for
     our snow instances in a small radius (~0.6m)
   - Calls `ClutterSystem.instance.ResetGrass()` for vanilla grass
   - Optionally applies a non-persistent Mountain biome paint for the
     packed-snow look under the cleared area
3. Tracks all stamp positions in a `List<Vector3>`
4. On zone exit / cleanup, clears the list — snow clutter is respawned
   naturally on next zone load from its persistent paint data

### Why Non-Persistent Trail Paint?

The trail paint is stored **only in memory** (added to
`BiomePaintPersistence._activePaints` but NOT saved to ZDO). This means:

- While in the zone: trails are visible, clutter stays cleared
- Leave the zone: the in-memory trail paints are discarded
- Return to the zone: snow clutter is restored from persistent ZDO data,
  trails are gone, fresh snow everywhere

This is the exact behavior requested: "doesn't bounce right back and
refresh until we leave the zone."

---

## Components

### 1. `SnowClutterConfig.cs` (Static Config)

Defines the snow clutter prefab configuration. We don't create a new
prefab — we clone an existing instanced grass entry from `ClutterSystem`
and override its material/scale to look like snow.

```
Snow Clutter Properties:
- Prefab base: Clone of "instanced_meadows_grass_short"
- Material: White/snow-colored (captured from mountain terrain or ice)
- Scale: 0.2–0.4 (short, dense piles)
- Density: 2.0x normal grass (thick coverage)
- Biome: Any (manually placed, not biome-restricted)
- Instanced: true (GPU instanced for performance)
```

**Fallback**: If we can't capture a proper snow material, we create a
simple white Standard shader material with slight transparency.

### 2. `SnowTrailController.cs` (MonoBehaviour)

Manages trail painting behind the player when they walk through snow zones.

```
Fields:
- _trailPositions: List<Vector3>     — all stamp positions this session
- _lastStampPos: Vector3             — debounce: skip if < 0.3m moved
- _trailRadius: float = 0.6f        — radius of each trail stamp
- _snowZoneBounds: Bounds            — the area that has snow clutter
- _isActive: bool                    — only stamp when in a snow zone

Update Loop (every 3 frames):
  1. if (!_isActive || player == null) return
  2. if (distance(player.pos, _lastStampPos) < 0.3) return
  3. if (!_snowZoneBounds.Contains(player.pos)) return
  4. StampTrail(player.pos)
  5. _lastStampPos = player.pos

StampTrail(pos):
  1. Clear snow clutter instances within _trailRadius of pos
     ? ClutterPaintTool.ClearClutterInternal(pos, _trailRadius)
  2. Clear vanilla grass
     ? ClutterSystem.instance.ResetGrass(pos, _trailRadius)
  3. Add non-persistent biome paint (dirty snow look)
     ? SnowTrailPaint.AddTrailPaint(pos, _trailRadius)
  4. _trailPositions.Add(pos)

Cleanup():
  1. Remove all non-persistent trail paints from _activePaints
  2. Clear _trailPositions
  3. (Snow clutter will respawn on next zone load from ZDO)
```

### 3. `SnowTrailPaint.cs` (Static Helper)

Manages the non-persistent "dirty snow" paint stamps.

```
AddTrailPaint(pos, radius):
  1. Create BiomePaintData with Biome = Mountain
  2. Add to BiomePaintPersistence._activePaints (in-memory only)
  3. Set _hasAnyPaintedAreas = true
  4. Do NOT save to ZDO (non-persistent)
  5. Force heightmap regeneration so vertex colors update

ClearAllTrailPaints():
  1. Remove all entries we added from _activePaints
  2. Update _hasAnyPaintedAreas flag
  3. (Called on zone exit)
```

### 4. Console Commands

```
paint_snow [radius] [density]
  — Paints snow clutter at cursor (persistent)
  — Same as paint_clutter but auto-selects snow type

clear_snow [radius|all]
  — Clears snow clutter in radius

snow_trail [on|off]
  — Enables/disables trail painting for current zone
  — Auto-enables when entering a snow-painted area
```

### 5. Snow Material Capture

Add to `WaterMaterialManager` or create a new `SnowMaterialManager`:

```
Capture Priority:
1. Mountain biome terrain material (snow texture)
2. Ice material with white tint
3. Fallback: Standard shader, white, slight alpha
```

For the snow clutter billboards, we want a material that:
- Is white/light blue
- Has alpha cutout (billboard grass shape but snowy)
- Works with the instanced grass shader (vertex animation for wind sway)

**Key insight**: We can potentially just use the existing grass prefab
but swap the material to a snow-colored version. The grass billboard
shape actually works well for snow mounds when short and dense.

---

## Integration Points

### Existing Systems Used

| System | How We Use It |
|--------|---------------|
| `ClutterPaintTool.PaintClutter()` | Paint snow clutter (persistent) |
| `ClutterPaintTool.ClearClutterInternal()` | Clear snow in trail stamps |
| `BiomePaintPersistence._activePaints` | Non-persistent trail paint |
| `BiomePaintPersistence.GetPaintAtPosition()` | Query for trail paint in RebuildRenderMesh |
| `ClutterSystem.ResetGrass()` | Clear vanilla grass in trail |
| `InstanceRenderer.AddInstance()` | GPU-instanced snow rendering |
| `_PlayerPosition` shader global | Free player-push interaction |
| `Heightmap.GetBiomeColor(Mountain)` | Snow vertex color for trail paint |

### New Files

| File | Purpose |
|------|---------|
| `Modules/CustomTerrain/Snow/SNOW_CLUTTER_PLAN.md` | This document |
| `Modules/CustomTerrain/Snow/SnowTrailController.cs` | Trail painting on player movement |
| `Modules/CustomTerrain/Snow/SnowTrailPaint.cs` | Non-persistent trail paint management |
| `Modules/CustomTerrain/Snow/SnowClutterConfig.cs` | Snow clutter prefab configuration |

### Modified Files

| File | Changes |
|------|---------|
| `ClutterPaintTool.cs` | Add `"snow"` as a recognized clutter alias |
| `BiomePaintPersistence.cs` | Add `AddNonPersistentPaint()` API for trail paints |
| `Ascend.cs` | Register snow console commands |

---

## Implementation Order

### Phase 1: Snow Clutter Prefab & Painting (Core)
1. Create `SnowClutterConfig.cs` — registers a snow clutter type with
   `ClutterPaintTool` that uses a white/snow material
2. Add `paint_snow` console command
3. Test: Can paint snow that looks like snow and sways in wind

### Phase 2: Trail System (Interaction)
4. Create `SnowTrailPaint.cs` — non-persistent paint API
5. Create `SnowTrailController.cs` — stamps trails behind player
6. Test: Walking through snow leaves a trail that persists

### Phase 3: Zone Lifecycle (Reset)
7. Hook `SnowTrailController.Cleanup()` into zone unload
8. Verify snow clutter respawns fresh on zone re-entry
9. Test: Leave zone ? return ? fresh snow, no trails

### Phase 4: Polish
10. Tune snow material (color, alpha, scale)
11. Tune trail radius and stamp interval
12. Add creature trail support (not just player)
13. Infinity Hammer tool YAML entries

---

## Performance Considerations

| Concern | Mitigation |
|---------|------------|
| Dense snow instances (2x grass) | `InstanceRenderer` is GPU-instanced, handles thousands |
| Trail stamp every 0.3m | Only runs `ClearClutterInternal` (fast spatial check) |
| Non-persistent paint accumulation | Only in-memory, small footprint (~50 bytes per stamp) |
| Heightmap regeneration per stamp | Debounce — only regen every 0.5s, not per stamp |
| Zone reload respawn | Uses existing `ClutterPaintTool` persistence path |

---

## Open Questions

1. ~~**Snow material source**~~: **RESOLVED** — We use a runtime-generated solid
   white 4×4 texture with the donor grass shader. No external PNG needed.
   The material has _Cutoff=0 (disables alpha cutout ? solid rendering),
   white _Color, white _MainTex, and all wind/sway properties zeroed.

2. **Creature trails**: Should NPCs/creatures also leave trails? Would need
   to track all characters in the zone, not just the local player.
   Could be expensive with many creatures.

3. **Snow depth visual**: The instanced billboards give a "covering" look.
   For actual depth (sinking in), we'd need either:
   - Terrain height modification (complex, persistent)
   - Vertex displacement shader (requires custom shader)
   - For now, billboard density + trail clearing gives a good enough
     approximation of depth.

4. ~~**Wind sway**~~: **RESOLVED** — Wind sway is disabled (all sway/wind
   shader properties zeroed to 0). Player push (_PlayerPosition) is kept
   so snow still bends when walked through.

5. ~~**Material not applying to spawned instances**~~: **RESOLVED** —
   `InstanceRenderer.Awake()` re-initializes its material from the serialized
   field, overwriting our reflection-set snow material on the template prefab.
   Fix: `ApplySnowMaterialToLiveInstance()` is called AFTER `SetActive(true)`
   in both `SpawnInstancedClutter` and `PaintClutterBatch`, force-applying
   the snow material to the already-initialized live `InstanceRenderer`.

---

## References

- `ClutterPaintTool.cs` — Full clutter painting system
- `BiomePaintPersistence.cs` — Biome paint + persistence
- `GrassOverlaySystem.cs` — Per-piece grass overlay
- `CLUTTER_SYSTEM_REFERENCE.md` — Vanilla ClutterSystem docs
- `VANILLA_TERRAIN_ANALYSIS.md` — Paint mask + IsCleared() flow
- `ENVBOX_BIOME_FIX.md` — Biome override + clutter spawning
