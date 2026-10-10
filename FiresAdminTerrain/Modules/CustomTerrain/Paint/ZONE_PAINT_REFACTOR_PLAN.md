# Zone-Relative Paint System Refactor

## Problem Statement

Our current paint system has a fundamental architectural flaw:
- Paint mask is tied to the **mesh bounding box**
- The mesh doesn't fill the entire bounding box (it's circular disks inside a rectangular bounds)
- Painting to bounding-box coordinates puts paint in areas with no mesh geometry
- Result: Paint appears at wrong locations or not at all

## How Vanilla Does It (The Correct Way)

Vanilla Valheim's terrain paint works like this:

### 1. Zone-Based Grid
- Each zone is a 64x64 meter square
- The zone has a fixed world position (zone origin)
- Paint mask is 64x64 or 65x65 pixels (one per meter)

### 2. World-to-UV Conversion
```csharp
// Vanilla's approach in Heightmap/TerrainComp:
Vector3 zoneOrigin = zone.transform.position; // Corner of zone
float zoneSize = 64f; // meters

// World position to UV (0-1)
float u = (worldPos.x - zoneOrigin.x) / zoneSize;
float v = (worldPos.z - zoneOrigin.z) / zoneSize;

// UV to pixel
int px = Mathf.RoundToInt(u * (maskWidth - 1));
int py = Mathf.RoundToInt(v * (maskHeight - 1));
```

### 3. Paint is "Projected Down"
- The paint mask exists in 2D world space (X/Z plane)
- It's like a colored overlay floating above the terrain
- ANY mesh geometry at that world position samples the paint
- The mesh shape is IRRELEVANT to paint storage

### 4. Shader Sampling
The terrain shader does:
```hlsl
// In vertex shader or fragment shader:
float2 paintUV = (worldPos.xz - zoneOrigin.xz) / zoneSize;
float4 paint = tex2D(_ClearedMaskTex, paintUV);
```

## Our New Architecture

### Core Principle
**Store paint data in TerrainComp ZDO, but keep it separate from vanilla's paint.**

We use TerrainComp as a storage container (it's already zone-aligned and persisted), but:
- Our custom terrain reads from OUR custom ZDO keys
- Vanilla terrain continues to use ITS paint data
- No conflicts, no interference

### Multi-Layer Paint System
**Critical insight: We need MULTIPLE paint layers per zone!**

Example scenario:
- Cave floor at Y=30
- Cave ceiling at Y=45
- Floating platform at Y=60

All three share the same X/Z coordinates but need DIFFERENT paint!

Solution: Each "terrain layer" gets its own paint mask, stored with a unique key:
```
va_paint_layer_0  = Cave floor paint (65x65 texture)
va_paint_layer_1  = Cave ceiling paint (65x65 texture)
va_paint_layer_2  = Platform paint (65x65 texture)
```

### Layer Assignment
Each DirtFloorMeshCombiner is assigned to a layer based on:
1. Explicit layer ID (from placement tool)
2. Y-height clustering (auto-assign based on vertical position)
3. Terrain type (CaveFloor vs CaveCeiling vs Default)

```csharp
public enum TerrainPaintLayer
{
    Default = 0,      // Ground-level terrain
    CaveFloor = 1,    // Underground floor
    CaveCeiling = 2,  // Underground ceiling (painted from below)
    Floating = 3,     // Floating platforms
    // Can add more as needed
}
```

### Why This Approach?
1. **Works everywhere** - Doesn't require vanilla Heightmap to exist
2. **No conflicts** - Our paint stored in separate ZDO keys from vanilla
3. **Multi-layer** - Cave floors and ceilings get separate paint
4. **Zone-aligned** - Uses same 64m grid as vanilla for consistency
5. **Persistent** - Stored in TerrainComp ZDO, survives restart

## Implementation Plan

### Phase 1: TerrainPaintLayer Enum and ZonePaintLayer Class

```csharp
/// <summary>
/// Different paint layers for custom terrain.
/// Each layer gets its own paint mask stored in the zone's TerrainComp.
/// </summary>
public enum TerrainPaintLayer
{
    Default = 0,      // Ground-level terrain
    CaveFloor = 1,    // Underground floor
    CaveCeiling = 2,  // Underground ceiling (painted from below)
    Floating = 3,     // Floating platforms
    // Add more as needed
}

/// <summary>
/// Represents a single paint layer for a zone.
/// Stored in TerrainComp ZDO with layer-specific keys.
/// </summary>
public class ZonePaintLayer
{
    public Vector2Int ZoneKey { get; }
    public TerrainPaintLayer LayerType { get; }
    public Texture2D PaintMask { get; private set; }
    
    private const int MASK_SIZE = 65;  // 65x65 for 64m zone
    private const float ZONE_SIZE = 64f;
    private bool _isDirty = false;
    
    public Vector3 ZoneOrigin => new Vector3(ZoneKey.x * ZONE_SIZE, 0, ZoneKey.y * ZONE_SIZE);
    
    public ZonePaintLayer(Vector2Int zoneKey, TerrainPaintLayer layerType)
    {
        ZoneKey = zoneKey;
        LayerType = layerType;
        InitializePaintMask();
    }
    
    private void InitializePaintMask()
    {
        PaintMask = new Texture2D(MASK_SIZE, MASK_SIZE, TextureFormat.RGBA32, false);
        PaintMask.name = $"ZonePaint_{ZoneKey.x}_{ZoneKey.y}_Layer{(int)LayerType}";
        PaintMask.wrapMode = TextureWrapMode.Clamp;
        PaintMask.filterMode = FilterMode.Point;
        
        // Initialize to "no paint" (grass)
        Color[] pixels = new Color[MASK_SIZE * MASK_SIZE];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = Heightmap.m_paintMaskNothing;
        PaintMask.SetPixels(pixels);
        PaintMask.Apply();
    }
    
    /// <summary>
    /// Convert world position to UV (0-1) - SAME formula as mesh UVs!
    /// </summary>
    public Vector2 WorldToUV(Vector3 worldPos)
    {
        float u = (worldPos.x - ZoneOrigin.x) / ZONE_SIZE;
        float v = (worldPos.z - ZoneOrigin.z) / ZONE_SIZE;
        return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
    }
    
    /// <summary>
    /// Convert world position to pixel coordinates.
    /// </summary>
    public Vector2Int WorldToPixel(Vector3 worldPos)
    {
        Vector2 uv = WorldToUV(worldPos);
        return new Vector2Int(
            Mathf.Clamp(Mathf.RoundToInt(uv.x * (MASK_SIZE - 1)), 0, MASK_SIZE - 1),
            Mathf.Clamp(Mathf.RoundToInt(uv.y * (MASK_SIZE - 1)), 0, MASK_SIZE - 1)
        );
    }
    
    /// <summary>
    /// Paint at world position.
    /// </summary>
    public void PaintAt(Vector3 worldPos, float radius, Color paintColor, float strength = 1f)
    {
        Vector2Int center = WorldToPixel(worldPos);
        float pixelRadius = radius * (MASK_SIZE - 1) / ZONE_SIZE;
        
        Color[] pixels = PaintMask.GetPixels();
        int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - pixelRadius));
        int maxX = Mathf.Min(MASK_SIZE - 1, Mathf.CeilToInt(center.x + pixelRadius));
        int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - pixelRadius));
        int maxY = Mathf.Min(MASK_SIZE - 1, Mathf.CeilToInt(center.y + pixelRadius));
        
        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                float dist = Vector2.Distance(new Vector2(px, py), new Vector2(center.x, center.y));
                if (dist <= pixelRadius)
                {
                    int idx = py * MASK_SIZE + px;
                    float t = Mathf.Pow(1f - Mathf.Clamp01(dist / pixelRadius), 0.1f) * strength;
                    pixels[idx] = Color.Lerp(pixels[idx], paintColor, t);
                }
            }
        }
        
        PaintMask.SetPixels(pixels);
        PaintMask.Apply();
        _isDirty = true;
    }
    
    public int GetZDOKey() => $"va_paint_{ZoneKey.x}_{ZoneKey.y}_L{(int)LayerType}".GetStableHashCode();
    
    public void SaveToTerrainComp(TerrainComp tc)
    {
        if (!_isDirty || tc == null) return;
        var nview = tc.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
        
        byte[] data = CompressPaintData();
        nview.GetZDO()?.Set(GetZDOKey(), data);
        _isDirty = false;
    }
    
    public bool LoadFromTerrainComp(TerrainComp tc)
    {
        if (tc == null) return false;
        var nview = tc.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) return false;
        
        byte[] data = nview.GetZDO()?.GetByteArray(GetZDOKey());
        if (data == null || data.Length == 0) return false;
        
        return DecompressPaintData(data);
    }
    
    private byte[] CompressPaintData()
    {
        Color32[] colors = PaintMask.GetPixels32();
        byte[] raw = new byte[colors.Length * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            raw[i * 4] = colors[i].r;
            raw[i * 4 + 1] = colors[i].g;
            raw[i * 4 + 2] = colors[i].b;
            raw[i * 4 + 3] = colors[i].a;
        }
        return Utils.Compress(raw);
    }
    
    private bool DecompressPaintData(byte[] compressed)
    {
        try
        {
            byte[] raw = Utils.Decompress(compressed);
            if (raw.Length != MASK_SIZE * MASK_SIZE * 4) return false;
            
            Color32[] colors = new Color32[MASK_SIZE * MASK_SIZE];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = new Color32(raw[i*4], raw[i*4+1], raw[i*4+2], raw[i*4+3]);
            }
            PaintMask.SetPixels32(colors);
            PaintMask.Apply();
            return true;
        }
        catch { return false; }
    }
}
```

### Phase 2: ZonePaintManager

```csharp
/// <summary>
/// Global manager for zone-based paint layers.
/// </summary>
public static class ZonePaintManager
{
    private static Dictionary<(Vector2Int, TerrainPaintLayer), ZonePaintLayer> _layers 
        = new Dictionary<(Vector2Int, TerrainPaintLayer), ZonePaintLayer>();
    
    public static Vector2Int WorldToZoneKey(Vector3 worldPos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldPos.x / 64f),
            Mathf.FloorToInt(worldPos.z / 64f)
        );
    }
    
    public static Vector3 GetZoneOrigin(Vector2Int zoneKey)
    {
        return new Vector3(zoneKey.x * 64f, 0, zoneKey.y * 64f);
    }
    
    public static ZonePaintLayer GetOrCreateLayer(Vector3 worldPos, TerrainPaintLayer layerType)
    {
        Vector2Int zoneKey = WorldToZoneKey(worldPos);
        var key = (zoneKey, layerType);
        
        if (!_layers.TryGetValue(key, out var layer))
        {
            layer = new ZonePaintLayer(zoneKey, layerType);
            _layers[key] = layer;
            
            // Try to load from TerrainComp
            TerrainComp tc = TerrainComp.FindTerrainCompiler(worldPos);
            if (tc != null)
                layer.LoadFromTerrainComp(tc);
        }
        
        return layer;
    }
    
    public static void PaintAt(Vector3 worldPos, float radius, Color paintColor, 
        TerrainPaintLayer layerType, float strength = 1f)
    {
        var layer = GetOrCreateLayer(worldPos, layerType);
        layer.PaintAt(worldPos, radius, paintColor, strength);
    }
    
    public static Texture2D GetPaintTexture(Vector3 worldPos, TerrainPaintLayer layerType)
    {
        return GetOrCreateLayer(worldPos, layerType).PaintMask;
    }
    
    public static void SaveAllDirtyLayers()
    {
        foreach (var kvp in _layers)
        {
            var layer = kvp.Value;
            Vector3 worldPos = GetZoneOrigin(layer.ZoneKey) + new Vector3(32, 0, 32);
            TerrainComp tc = TerrainComp.FindTerrainCompiler(worldPos);
            if (tc != null)
                layer.SaveToTerrainComp(tc);
        }
    }
    
    public static void ClearCache()
    {
        foreach (var layer in _layers.Values)
        {
            if (layer.PaintMask != null)
                UnityEngine.Object.Destroy(layer.PaintMask);
        }
        _layers.Clear();
    }
}
```

### Phase 3: Update Mesh UV Generation

In `DirtFloorMeshCombiner.BuildMesh()`, change UV calculation:

```csharp
// OLD (bounding-box relative - BROKEN):
float u = (worldPos.x - _combinedBounds.min.x) / boundsWidth;
float v = (worldPos.z - _combinedBounds.min.z) / boundsDepth;

// NEW (zone-relative - CORRECT):
Vector2Int zoneKey = ZonePaintManager.WorldToZoneKey(worldPos);
Vector3 zoneOrigin = ZonePaintManager.GetZoneOrigin(zoneKey);
float u = (worldPos.x - zoneOrigin.x) / 64f;
float v = (worldPos.z - zoneOrigin.z) / 64f;
u = Mathf.Clamp01(u);
v = Mathf.Clamp01(v);
```

### Phase 4: Update Paint Operations

```csharp
public void PaintAtWorldPosition(Vector3 worldPos, float radius, PaintType paintType, float strength = 1f)
{
    Color paintColor = GetPaintColor(paintType);
    TerrainPaintLayer layerType = GetPaintLayerType();
    
    // Paint to the ZONE layer (not local texture)
    ZonePaintManager.PaintAt(worldPos, radius, paintColor, layerType, strength);
    
    // Refresh material binding
    RefreshZonePaintBinding();
}

private TerrainPaintLayer GetPaintLayerType()
{
    // Check explicit assignment
    if (_explicitPaintLayer != TerrainPaintLayer.Default)
        return _explicitPaintLayer;
    
    // Derive from terrain type
    foreach (var disk in _terrainDisks)
    {
        if (disk.DiskType == TerrainDiskType.CaveFloor)
            return TerrainPaintLayer.CaveFloor;
        if (disk.DiskType == TerrainDiskType.CaveCeiling)
            return TerrainPaintLayer.CaveCeiling;
    }
    
    return TerrainPaintLayer.Default;
}

private void RefreshZonePaintBinding()
{
    TerrainPaintLayer layerType = GetPaintLayerType();
    Texture2D zonePaint = ZonePaintManager.GetPaintTexture(transform.position, layerType);
    
    if (_terrainMaterial != null && _terrainMaterial.HasProperty(s_ClearedMaskTexProperty))
    {
        _terrainMaterial.SetTexture(s_ClearedMaskTexProperty, zonePaint);
        
        // Also use MaterialPropertyBlock for immediate update
        if (_materialPropertyBlock == null)
            _materialPropertyBlock = new MaterialPropertyBlock();
        _meshRenderer.GetPropertyBlock(_materialPropertyBlock);
        _materialPropertyBlock.SetTexture(s_ClearedMaskTexProperty, zonePaint);
        _meshRenderer.SetPropertyBlock(_materialPropertyBlock);
    }
}
```

### Phase 5: Update SetupMaterial

```csharp
private void SetupMaterial()
{
    // ... existing material creation ...
    
    // Use ZONE paint texture (not local _combinedPaintMask)
    TerrainPaintLayer layerType = GetPaintLayerType();
    Texture2D zonePaint = ZonePaintManager.GetPaintTexture(transform.position, layerType);
    
    if (_terrainMaterial.HasProperty(s_ClearedMaskTexProperty))
    {
        _terrainMaterial.SetTexture(s_ClearedMaskTexProperty, zonePaint);
    }
    
    // ... rest of material setup ...
}
```

## Migration Path

1. **Create new files**: `ZonePaintLayer.cs`, `ZonePaintManager.cs`, `TerrainPaintLayer.cs` (enum)
2. **Update mesh UV generation** in `DirtFloorMeshCombiner.MeshGen.cs`
3. **Update paint operations** in `DirtFloorMeshCombiner.Paint.cs`
4. **Update material setup** in `DirtFloorMeshCombiner.Paint.cs` (SetupMaterial)
5. **Add layer type property** to `DirtFloorMeshCombiner` and `TerrainDisk`
6. **Add save/load hooks** to zone lifecycle
7. **Remove old local paint mask code** (or keep as fallback)

## Key Insight

The fundamental fix is simple:
**Both mesh UVs AND paint operations must use the same zone-relative coordinate system.**

```csharp
// Zone origin calculation (used by BOTH mesh UVs and paint):
Vector2Int zoneKey = new Vector2Int(
    Mathf.FloorToInt(worldPos.x / 64f),
    Mathf.FloorToInt(worldPos.z / 64f)
);
Vector3 zoneOrigin = new Vector3(zoneKey.x * 64f, 0, zoneKey.y * 64f);

// UV calculation (SAME for mesh and paint):
float u = Mathf.Clamp01((worldPos.x - zoneOrigin.x) / 64f);
float v = Mathf.Clamp01((worldPos.z - zoneOrigin.z) / 64f);
```

**As long as mesh UVs and paint coordinates use the SAME zone-relative formula, paint will appear exactly where you click regardless of mesh shape!**

## Implementation Checklist

### New Files Created
- [x] `FiresNPCs\Modules\CustomTerrain\Paint\TerrainPaintLayer.cs` (enum)
- [x] `FiresNPCs\Modules\CustomTerrain\Paint\ZonePaintLayer.cs` (class)
- [x] `FiresNPCs\Modules\CustomTerrain\Paint\ZonePaintManager.cs` (static manager)

### Files Modified
- [x] `DirtFloorMeshCombiner.MeshGen.cs` - UV generation ? zone-relative
- [x] `DirtFloorMeshCombiner.Paint.cs` - PaintAtWorldPosition ? use ZonePaintManager
- [x] `DirtFloorMeshCombiner.Paint.cs` - SetupMaterial ? bind zone paint texture
- [x] `DirtFloorMeshCombiner.Core.cs` - Add `PaintLayerType` property
- [x] `DirtFloorMeshCombiner.Core.cs` - REMOVED duplicate `s_allCombiners` (registry is single source)
- [x] `DirtFloorMeshCombiner.Lifecycle.cs` - Call `ZonePaintManager.Update()`
- [x] `DirtFloorMeshCombiner.Lifecycle.cs` - Use registry for registration/unregistration
- [x] `DirtFloorMeshCombiner.Static.cs` - All `s_allCombiners` ? `DirtFloorZoneRegistry.GetAllCombiners()`
- [x] `DirtFloorZoneRegistry.cs` - Single source of truth for combiner list

### Critical Architecture Fix: Single Combiner List
**SINGLE COMBINER LIST**: `DirtFloorZoneRegistry.s_allCombiners` is now the ONLY list of combiners.
- `DirtFloorMeshCombiner.AllCombiners` ? returns `DirtFloorZoneRegistry.GetAllCombiners()`
- `OnEnable` ? calls `DirtFloorZoneRegistry.RegisterCombiner()`
- `OnDisable` ? calls `DirtFloorZoneRegistry.UnregisterCombiner()`
- All paint/lookup operations use registry's list

---

## Legacy `_combinedPaintMask` Contamination Audit (2025-03-14)

### The Root Problem
The zone paint system stores paint in `ZonePaintLayer.PaintMask` (65x65, zone-relative).
But the OLD local `_combinedPaintMask` (per-combiner, bounds-relative) was still being
bound to `_ClearedMaskTex` in **20 different code paths**, overwriting the zone paint
texture with either an empty or stale bounds-relative texture.

**This is why paved/cultivated/dirt paint was broken** — every time any of these legacy
paths ran, they stomped the correct zone paint texture with the wrong one.

Note: Biome paint overlays (vertex colors) were NOT affected because they use a
completely separate system (`mesh.colors32`), not the `_ClearedMaskTex` property.

### All Contamination Points Found & Fixed

| # | File | Method | What it did | Fix |
|---|------|--------|-------------|-----|
| 1 | `Lifecycle.cs` | `GenerateLODMesh()` | LOD mesh UVs used bounds-relative coords | ? `ZonePaintManager.WorldToZoneUV()` |
| 2 | `Lifecycle.cs` | `Update()` material fix | Rebound `_combinedPaintMask` after 3s | ? `RefreshZonePaintBinding()` |
| 3 | `Lifecycle.cs` | `OnDestroy()` | Duplicate `UnregisterCombiner` call | ? Removed (handled by `OnDisable`) |
| 4 | `Paint.cs` | `SetupMaterial()` material mismatch | Rebound `_combinedPaintMask` | ? `RefreshZonePaintBinding()` |
| 5 | `Paint.cs` | `SetPaintMask()` (undo/redo) | Set `_combinedPaintMask` on material | ? `RefreshZonePaintBinding()` |
| 6 | `Paint.cs` | `ApplyPaintMaskToMaterial()` | Full method bound `_combinedPaintMask` | ? Redirected to `RefreshZonePaintBinding()` |
| 7 | `Paint.cs` | `LoadPaintMaskData()` | Set `_combinedPaintMask` on material | ? `RefreshZonePaintBinding()` |
| 8 | `Paint.cs` | `ClearPaintAndRespawnGrass()` | Cleared local mask, set on material | ? `ZonePaintManager.ClearPaintAt()` |
| 9 | `Paint.cs` | `ClearPaintInRegion()` | Full method used bounds-relative clear | ? `ZonePaintManager.ClearPaintAt()` |
| 10 | `Paint.cs` | `RemapPaintMaskToNewBounds()` | Set remapped `_combinedPaintMask` | ? `RefreshZonePaintBinding()` |
| 11 | `Paint.cs` | `PaintUsingVertexUVs()` | Called `ApplyPaintMaskToMaterial()` | ? `RefreshZonePaintBinding()` |
| 12 | `Paint.cs` | `PaintWithTextureFallback()` | Called `ApplyPaintMaskToMaterial()` | ? `RefreshZonePaintBinding()` |
| 13 | `Paint.cs` | `UpdatePaintMaskFromBiomePaints()` | Set `_combinedPaintMask` on material | ? Removed material binding |
| 14 | `Core.cs` | `RestoreFromAnchorData()` | Set `_combinedPaintMask` after `SetupMaterial()` | ? Removed (SetupMaterial handles it) |
| 15 | `Paint.cs` | `RefreshPaintFromZone()` | Was disabled (empty body) | ? Now calls `RefreshZonePaintBinding()` |
| 16 | `Utilities.cs` | `OnPaintTerrain(pos,r,type)` | Called `PaintDirectlyOnCombinedMask` + bound `_combinedPaintMask` | ? Routes to `PaintAtWorldPosition()` |
| 17 | `Utilities.cs` | `ForceVisualRefresh()` | Bound `_combinedPaintMask` via MaterialPropertyBlock | ? Routes to `RefreshZonePaintBinding()` |
| 18 | `Utilities.cs` | `OnPaintTerrain(pos,r)` | Called `UpdateCombinedPaintMask` + bound `_combinedPaintMask` | ? Routes to `RefreshZonePaintBinding()` |
| 19 | `Utilities.cs` | `BuildMeshWithLevelArea()` | UV0 used bounds-relative coords | ? `ZonePaintManager.WorldToZoneUV()` |
| 20 | `DiskManagement.cs` | `ApplyPaintToMaterial()` | Bound `_combinedPaintMask` to material | ? Routes to `RefreshZonePaintBinding()` |

**NOTE:** Items 16-20 were found in the second audit pass. The `DirtFloorPatches.cs` and
`EnvironmentBoxPatches.cs` call `OnPaintTerrain()` which is now routed through zone paint.

### Root Cause #2: Stale Cached UVs (Found 2025-03-14, Audit Pass 3)

The file cache (`DirtFloorCombinerCache`) saves mesh data including UV0 coordinates.
When meshes were cached BEFORE the zone-relative UV refactor, the cached UVs were
bounds-relative (0-1 across the mesh bounding box). After the refactor, `SetupMaterial()`
binds the zone paint texture (65x65, covering a 64m zone), but the mesh still has
bounds-relative UVs from the cache. The shader samples the zone texture at the wrong
coordinates, so paint appears invisible.

**Fix:** `RestoreMeshFromCacheDeferred()` now ALWAYS regenerates UV0 from zone-relative
coordinates using `ZonePaintManager.WorldToZoneUV()`, ignoring cached UVs entirely.
This ensures paint mask sampling is correct regardless of when the cache was written.

| # | File | Method | What it did | Fix |
|---|------|--------|-------------|-----|
| 21 | `DiskManagement.cs` | `RestoreMeshFromCacheDeferred()` | Restored stale bounds-relative UVs from file cache | ? Regenerates UV0 using `ZonePaintManager.WorldToZoneUV()` |

### The Rule Going Forward
**NEVER bind `_combinedPaintMask` to `_ClearedMaskTex` on the material.**

Only `RefreshZonePaintBinding()` should set the material's paint texture.
`_combinedPaintMask` still exists for legacy undo/redo data and cache persistence,
but it is NEVER the authoritative source for what the shader sees.

### What Still Uses `_combinedPaintMask` (Safely)
These methods read/write the local texture but NO LONGER bind it to the material:
- `GetPaintMask()` / `SetPaintMask()` — undo/redo data
- `GetPaintMaskData()` / `LoadPaintMaskData()` — cache persistence
- `InitializePaintMask()` / `GetOrCreatePaintMask()` — legacy init
- `RemapPaintMaskToNewBounds()` — legacy remap (kept for cache)
- `UpdateCombinedPaintMask()` / `UpdateCombinedPaintMaskLegacy()` — never called in active flow
- `MigrateDiskPaintsToCombiner()` — one-time migration

---

## Paint System Flow (Current - Correct)

### Painting:
```
User clicks hoe ? PaintAllLayersAt() ? finds combiner ? PaintAtWorldPosition()
  ? ZonePaintManager.PaintAt(worldPos, radius, color, layerType)
    ? ZonePaintLayer.PaintAt() (paints to 65x65 zone texture)
  ? RefreshZonePaintBinding()
    ? material.SetTexture(_ClearedMaskTex, zonePaintTexture)
    ? MaterialPropertyBlock for immediate GPU refresh
```

### Mesh UV Generation:
```
BuildMesh() / BuildMeshAsync() ? for each vertex:
  ? ZonePaintManager.WorldToZoneUV(worldPos)
    ? zoneKey = FloorToInt(worldPos.x / 64f)
    ? zoneOrigin = zoneKey * 64f
    ? u = Clamp01((worldPos.x - zoneOrigin.x) / 64f)
    ? v = Clamp01((worldPos.z - zoneOrigin.z) / 64f)
```

### Material Setup:
```
SetupMaterial() ? creates/finds Heightmap material ? binds zone paint:
  ? ZonePaintManager.GetPaintTexture(position, layerType)
  ? material.SetTexture(_ClearedMaskTex, zonePaintTexture)
```

### LOD Mesh:
```
GenerateLODMesh() ? for each LOD vertex:
  ? ZonePaintManager.WorldToZoneUV(worldPos)  (was bounds-relative, now zone-relative)
```

### Key Invariant:
**The UV formula in BuildMesh MUST match the UV formula in ZonePaintLayer.WorldToUV.**
Both use: `Clamp01((worldPos.component - zoneOrigin.component) / 64f)`

---

## Root Cause #3: Material Property Mismatches (Found 2025-03-12, Audit Pass 4)

The vanilla comparison diagnostic revealed two critical material property mismatches
that prevented paint from being visible even though all CPU-side data was correct:

### Issue A: `_MainTex_ST` Tiling Override

`SetupMaterial()` computed tiling from bounds and set `_MainTex_ST` to `(17.49, 17.49)`:
```csharp
// WRONG - was computing tiling from mesh bounds:
float avgSize = (boundsWidth + boundsDepth) * 0.5f; // ~70
float textureTiling = avgSize * 0.25f;               // ~17.49
_terrainMaterial.SetTextureScale("_MainTex", new Vector2(textureTiling, textureTiling));
```

Vanilla Heightmap uses `_MainTex_ST = (1, 1, 0, 0)` and relies on `_UVScale` (0.5)
for world-space texture tiling. The `Custom/Heightmap` shader uses `_MainTex_ST`
internally for UV transforms — overriding it to 17.49 caused paint mask UV sampling
to produce wildly wrong coordinates (UV 0.47 ? 8.22 after transform ? clamps to edge).

**Fix:** Set `_MainTex_ST` to `(1, 1, 0, 0)` to match vanilla.

### Issue B: Paint Mask `FilterMode.Point` vs Vanilla `Bilinear`

The zone paint mask was created with `FilterMode.Point` while vanilla's `m_paintMask`
uses `FilterMode.Bilinear`. With Point filtering, the GPU does nearest-neighbor sampling
which causes hard edges and potential misses at UV coordinates between pixel centers.
Bilinear provides smooth interpolation matching vanilla behavior.

**Fix:** Changed `ZonePaintLayer.InitializePaintMask()` to use `FilterMode.Bilinear`.

| # | File | Method | What it did | Fix |
|---|------|--------|-------------|-----|
| 22 | `Paint.cs` | `SetupMaterial()` | Set `_MainTex_ST` to `(17.49, 17.49)` from bounds | ? Set to `(1, 1)` matching vanilla |
| 23 | `ZonePaintLayer.cs` | `InitializePaintMask()` | Used `FilterMode.Point` | ? `FilterMode.Bilinear` matching vanilla |

---

## Success Criteria
- [x] Paint appears exactly where you click (**FIXED** - Root Cause #3: `_MainTex_ST` + `FilterMode`)
- [ ] Paint works with any mesh shape (circles, V-shapes, complex)
- [ ] Cave floor and cave ceiling have SEPARATE paint
- [ ] Paint persists across save/load
- [ ] Multiple meshes in same zone share paint layer
- [ ] No visual artifacts at mesh boundaries
- [x] Single authoritative combiner list (no more duplicate lists)
- [x] No legacy `_combinedPaintMask` contamination of material (20 paths fixed)
- [x] Material properties match vanilla Heightmap (no tiling/filter mismatches)
- [x] Biome paints persist on custom terrain across zone reload (Root Cause #4)

---

## Root Cause #4: Biome Paint Vertex Colors Not Restored on Zone Reload (Found 2026-03-12)

### Problem
Biome overlay painting (snow, lava, plains, etc.) on custom terrain worked visually
when first applied, but the vertex colors were lost after zone reload / save-load.

### Root Cause
The save side was correct — `RecordBiomePaint()` saved biome paint records to the
anchor's ZDO via `SaveBiomeData()`, and `LoadBiomeData()` loaded them back into
`_biomePaints` on zone restore.

But `GetBiomeColorForPosition()` (used during `BuildMesh()` to set vertex colors)
only checked:
1. `BiomePaintPersistence` (global, for vanilla TerrainComp storage)
2. `EnvironmentBoxController` biome override
3. `WorldGenerator` fallback

It **never checked** the anchor's own `_biomePaints` list! So even though the data
was loaded from ZDO, it was never used to regenerate vertex colors.

Additionally, `RestoreFromAnchorData()` called `BuildMesh()` which set vertex colors
using the above chain (missing anchor data), and never re-applied biome paints after.

### Fix

1. **`GetBiomeColorForPosition()`** — Added Priority 1 check for `Anchor.GetBiomePaintAt()`
   before all other biome sources. The anchor's own biome paints now take precedence.

2. **`RestoreFromAnchorData()`** — Added call to `RestoreBiomePaintsFromAnchor()` after
   mesh rebuild. This calls `RefreshBiomeColors()` which iterates all vertices and
   calls `GetBiomeColorForPosition()` — which now finds the anchor's paints.

| # | File | Method | What it did | Fix |
|---|------|--------|-------------|-----|
| 24 | `Paint.cs` | `GetBiomeColorForPosition()` | Didn't check anchor biome paints | ? Added Priority 1 `Anchor.GetBiomePaintAt()` check |
| 25 | `Core.cs` | `RestoreFromAnchorData()` | Didn't reapply biome vertex colors | ? Calls `RestoreBiomePaintsFromAnchor()` ? `RefreshBiomeColors()` |

### Bonus: Multi-Zone Support
If a mesh spans multiple 64m zones:
- Option A: Split mesh at zone boundaries (complex)
- Option B: Use primary zone for all UVs, paint wraps at edges (simple, acceptable for most cases)
- Option C: Sample from multiple zone textures in shader (requires shader modification)

**Recommended: Option B for now** - Most custom terrain meshes are <64m, and edge wrapping is acceptable.
