# ?? DO NOT MODIFY - WORKING BIOME PAINT IMPLEMENTATION ??

## Last Updated: 2026-03-12
## Status: ? WORKING - DO NOT CHANGE

---

## CRITICAL WARNING

**THIS IMPLEMENTATION IS CONFIRMED WORKING. DO NOT MODIFY THE BIOME PAINTING CODE UNLESS EXPLICITLY REQUESTED BY THE USER.**

The biome overlay painting system has been through multiple iterations and the current implementation is the ONLY one that works correctly. Any changes to this system will likely break it.

---

## How Biome Painting Works

### The Key Insight

**Biome painting modifies VERTEX COLORS, NOT the paint mask texture (_ClearedMaskTex).**

These are TWO COMPLETELY SEPARATE SYSTEMS:

| System | What It Controls | How It Works |
|--------|------------------|--------------|
| **Biome Overlay (Vertex Colors)** | Texture blending (snow, plains golden grass, swamp dark, etc.) | Modifies `mesh.colors32` directly |
| **Terrain Paint Mask (_ClearedMaskTex)** | Paved/Cultivated/Dirt/Grass textures | Modifies a texture sampled by shader |

### Implementation Details

#### For Custom Terrain (DirtFloorMeshCombiner):

```csharp
// In DirtFloorMeshCombiner.Paint.cs
public int PaintBiomeOverlayVertexColors(Vector3 center, float radius, Heightmap.Biome biome)
{
    // Get mesh vertices and colors
    Vector3[] vertices = _combinedMesh.vertices;
    Color32[] colors = _combinedMesh.colors32;
    
    // Get target biome color
    Color32 targetColor = Heightmap.GetBiomeColor(biome);
    
    // Paint vertices within radius
    for (int i = 0; i < vertices.Length; i++)
    {
        Vector3 worldPos = transform.position + vertices[i];
        float distSq = (worldPos - center).sqrMagnitude;
        
        if (distSq <= radius * radius)
        {
            colors[i] = targetColor;
        }
    }
    
    // Apply to mesh
    _combinedMesh.colors32 = colors;
}
```

#### For Vanilla Terrain (Heightmap):

The `BiomePaintPatches.Heightmap_RebuildRenderMesh_Postfix` patch modifies vertex colors after vanilla rebuilds the mesh.

---

## Files Involved

| File | Purpose |
|------|---------|
| `BiomePaintPersistence.cs` | Main entry point, persistence, routing to custom vs vanilla terrain |
| `DirtFloorMeshCombiner.Paint.cs` | `PaintBiomeOverlayVertexColors()` and `ResetBiomeOverlayVertexColors()` methods |
| `TerrainCombinerAnchor.cs` | Stores biome paint data for persistence |

---

## What NOT To Do

? Do NOT use `PaintAtWorldPosition()` for biome painting - that's for terrain paint mask (paved/cultivated/dirt)

? Do NOT modify `_ClearedMaskTex` for biome painting

? Do NOT call `RefreshBiomeAndClutterInRadius()` or similar methods that touch the paint mask

? Do NOT change the flow in `PaintBiomeOverlayInternal()` without explicit user request

---

## Confirmed Working Log Output

```
[BiomePaintPersistence] Painted Plains on CUSTOM terrain at (1710.48, 37.49, -157.09), radius 2.5, affected 34 vertices
[DirtFloorMeshCombiner] PaintBiomeOverlayVertexColors: Painted Plains on 34 vertices at (1710.48, 37.49, -157.09)
```

The key indicators of success:
1. `PaintBiomeOverlayVertexColors` is called (NOT `PaintAtWorldPosition`)
2. Vertices are affected (NOT pixels)
3. Visual result shows correct biome texture blending

---

## History

- **Initial Implementation**: Used paint mask texture - BROKEN
- **Attempt 2**: Tried zone-level paint storage - BROKEN (UV mapping issues)
- **Attempt 3**: Tried combiner-level paint mask - BROKEN (still UV issues)
- **Final Fix (2026-03-12)**: Direct vertex color modification - ? WORKING

The lesson: Biome painting was ALWAYS about vertex colors. The shader uses vertex colors to blend biome textures. The paint mask texture is a completely separate system for paved/cultivated/dirt overlays.

---

## Maintainer Notes

If you're reading this because something broke:

1. **Check if someone modified BiomePaintPersistence.PaintBiomeOverlayInternal()** - revert any changes
2. **Check if someone modified DirtFloorMeshCombiner.PaintBiomeOverlayVertexColors()** - revert any changes
3. **Verify the call chain**: `PaintBiomeOverlay()` ? `PaintBiomeOverlayInternal()` ? `combiner.PaintBiomeOverlayVertexColors()`
4. **DO NOT** try to "optimize" or "improve" this code without explicit user request

---

**REMEMBER: IF IT AIN'T BROKE, DON'T FIX IT.**
