# Custom Liquid System Expansion Plan

## Overview

Expand the custom water system to support **multiple liquid types** with unique behaviors, materials, and interactions. This builds on the existing `WaterMeshCombiner` architecture to add:

1. **Tar** (fix pink shader issue)
2. **Frozen Water / Ice** (solid, breakable surface)
3. **Boiling Water** (Ashlands-style with damage)
4. **Freezing Water** (Deep North-style with cold damage)
5. **Lava** (swimable with fire damage)

---

## Current Issues to Fix

### Issue 1: Tar Showing Pink ? NEEDS FIX

**Problem**: Tar material is showing pink (missing shader).

**Root Cause**: `WaterMaterialManager.CaptureVanillaMaterials()` looks for `LiquidVolume` instances, but:
1. Tar pits might not be loaded when capture is attempted
2. The tar shader might be on a different component
3. Need to search ZNetScene prefabs for tar material

**Solution**: Update `WaterMaterialManager` to:
1. Search ZNetScene for tar pit prefabs (`TarLiquid`, `Tar_blob`, etc.)
2. Fall back to searching by shader name (`Custom/Tar` or similar)
3. Create a better fallback material using Standard shader with proper settings

### Issue 2: Cliff Face Spider-Web Mesh ? FIXED (2025-XX-XX)

**Problem**: When placing water disks at different heights, the cliff face geometry created a broken spider-web mesh with overlapping triangles.

**Root Cause**: The `AddCliffFaceGeometry()` method was creating individual quad strips for EVERY edge cell that detected a cliff, causing massive overlap when many adjacent cells all created their own strips pointing in similar directions.

**Solution Implemented**:
1. Filter cliff cells to skip inter-disk stretches (most problematic)
2. Require minimum spacing (2m) between cliff strips
3. Limit total cliff strips to 10 per combiner
4. Simplify each strip to just 4 steps instead of many detailed steps
5. For now, rely on VFX system for visual waterfall effects instead of mesh

**Future Improvement**: Implement proper edge loop detection like terrain system:
- Detect the OUTER boundary of the water mesh
- Create a single continuous edge extrusion
- Follow terrain contour smoothly

### Issue 3: ZNetScene.RemoveObjects NullReferenceException ? FIXED (2025-XX-XX)

**Problem**: Spam of `NullReferenceException` in `ZNetScene.RemoveObjects` during gameplay.

**Root Cause**: The VFX system was using complex pooling with ZNetView stripping, but `Instantiate()` triggers `ZNetView.Awake()` which registers the ZDO with ZNetScene BEFORE we could strip it. The timing window caused invalid ZDOs.

**Solution Implemented**:
- Simplified VFX system to use normal `Instantiate()` without stripping
- VFX are parented to a container GameObject per combiner
- When combiner is destroyed, container is automatically destroyed with all child VFX
- No pooling, no ZNetView manipulation - just simple spawn/destroy
- VFX prefabs from ZNetScene are templates - instances work fine without networking

**Key Insight**: VFX prefabs in Valheim are designed to work this way. Most don't have ZNetView, and those that do handle it properly. The over-engineering with stripping was the problem.

---

## New Liquid Types

### Extended LiquidType Enum

Since Valheim's `LiquidType` enum only has `Water` and `Tar`, we need our own extended enum:

```csharp
public enum CustomLiquidType
{
    Water = 0,           // Standard water (matches vanilla)
    Tar = 1,             // Standard tar (matches vanilla)
    Ice = 2,             // Frozen water surface (solid)
    BoilingWater = 3,    // Ashlands-style hot water
    FreezingWater = 4,   // Deep North-style cold water
    Lava = 5,            // Swimable lava/magma
    // Future possibilities:
    // Acid = 6,
    // Poison = 7,
    // HolyWater = 8,
}
```

---

## Type 1: Ice / Frozen Water

### Concept
- Convert an existing water mesh to a **solid frozen surface**
- Player can walk on ice
- Ice can be **broken** to reveal water beneath
- Uses Valheim's ice material/shader

### Visual Reference
- Valheim ice lakes in Mountains biome
- Frozen rivers
- Ice sheet on ponds

### Architecture

```csharp
public class FrozenWaterController : MonoBehaviour
{
    private WaterMeshCombiner _waterCombiner;  // Reference to underlying water
    private Mesh _iceMesh;                      // Solid ice surface mesh
    private MeshCollider _iceCollider;          // For walking on
    private Material _iceMaterial;              // Captured from Valheim
    
    // Ice state
    private bool _isFrozen = false;
    private float[] _iceThickness;              // Per-cell ice thickness
    private const float MIN_ICE_THICKNESS = 0.1f;
    private const float MAX_ICE_THICKNESS = 1.0f;
    
    // Breaking mechanics
    private List<IceHole> _holes = new List<IceHole>();
    
    public struct IceHole
    {
        public Vector3 Center;
        public float Radius;
        public float TimeCreated;
    }
}
```

### Key Features

| Feature | Description |
|---------|-------------|
| **Freeze** | Convert water surface to solid ice mesh with collider |
| **Unfreeze** | Remove ice, restore water waves |
| **Walk On** | Players/creatures can walk on frozen surface |
| **Break Ice** | Attacking ice creates holes (circular cutouts) |
| **Ice Thickness** | Thicker ice = harder to break |
| **Cracking VFX** | Visual cracks appear before breaking |
| **Sound Effects** | Creaking ice sounds when walking |
| **Fall Through** | Breaking ice near player drops them in water |

### Material Capture Strategy

```csharp
// Locations to search for ice material:
// 1. Mountain biome ice lakes
// 2. Frozen river pieces
// 3. Ice block destructibles
// 4. Frost effects/shaders

private static readonly string[] ICE_PREFAB_NAMES = {
    "ice_floor",
    "ice_rock",
    "FrozenLake",
    "mountainkit_ice",
    "ice_ground",
    "Iceblock",
    "IceFloor"
};
```

### Infinity Hammer Tools

```yaml
- name: Freeze Water
  description: |-
    <#00FFFF>Freeze</color> custom water into solid ice.
    Click on existing water to freeze.
    Ice can be walked on and broken.
  icon: FreezeGland
  command: freeze_water
  instant: false

- name: Thaw Water  
  description: |-
    <#FF6600>Thaw</color> frozen ice back to water.
    Click on frozen water to thaw.
  icon: Torch
  command: thaw_water
  instant: false

- name: Break Ice
  description: |-
    <#FFFFFF>Create hole</color> in frozen ice.
    <#FFFF00>Scroll</color> to adjust hole radius.
    Creates circular opening in ice.
  icon: PickaxeIron
  command: break_ice <area>
  continuous: false
  snapGround: false
```

---

## Type 2: Boiling Water (Ashlands)

### Concept
- Hot, bubbling water with steam effects
- Deals **fire/burn damage** to players in water
- Visual steam/bubble particle effects
- Possibly spawns fire particles at surface

### Visual Reference
- Ashlands lava pools (but water-colored)
- Hot springs with steam
- Geysers

### Architecture

```csharp
public class BoilingWaterController : MonoBehaviour
{
    private WaterMeshCombiner _waterCombiner;
    
    // Damage settings
    public float DamagePerSecond = 10f;
    public HitData.DamageType DamageType = HitData.DamageType.Fire;
    public float DamageInterval = 0.5f;
    
    // Visual effects
    private ParticleSystem _steamParticles;
    private ParticleSystem _bubbleParticles;
    
    // Material settings
    public Color WaterTint = new Color(1f, 0.3f, 0.1f, 0.8f); // Orangish tint
    public float BubbleIntensity = 1.0f;
}
```

### Key Features

| Feature | Description |
|---------|-------------|
| **Heat Damage** | Continuous fire damage while in water |
| **Steam VFX** | Rising steam particles at surface |
| **Bubble VFX** | Bubbles rising from bottom |
| **Heat Shimmer** | Post-process effect near surface |
| **No Wet Debuff** | Player dries instantly (too hot) |
| **Cook Food** | Raw food turns cooked when dropped in |

### Material/Shader Approach

Option A: Tint existing water material orange/red
Option B: Capture Ashlands lava material, reduce opacity
Option C: Custom shader with animated bubbles

```csharp
// Capture from Ashlands lava areas
private static readonly string[] BOILING_PREFAB_NAMES = {
    "lava_blob",
    "LavaPool", 
    "AshlandsLava",
    "fx_lava_bubble"
};
```

---

## Type 3: Freezing Water (Deep North)

### Concept
- Extremely cold water that deals **frost damage**
- Slows movement while swimming
- Applies **Freezing** status effect
- Ice crystal particles at surface

### Visual Reference
- Deep North ocean
- Glacial meltwater
- Frost giant areas

### Architecture

```csharp
public class FreezingWaterController : MonoBehaviour
{
    private WaterMeshCombiner _waterCombiner;
    
    // Damage settings
    public float DamagePerSecond = 5f;
    public HitData.DamageType DamageType = HitData.DamageType.Frost;
    public float FreezingBuildupPerSecond = 20f; // Applies freezing status
    
    // Visual effects
    private ParticleSystem _frostParticles;
    private ParticleSystem _snowflakeParticles;
    
    // Status effects
    public string FreezingStatusEffect = "Freezing"; // Vanilla status effect
    
    // Movement penalty
    public float SwimSpeedMultiplier = 0.5f; // 50% slower swimming
}
```

### Key Features

| Feature | Description |
|---------|-------------|
| **Frost Damage** | Continuous cold damage while in water |
| **Freezing Status** | Builds up freezing status effect |
| **Slow Movement** | Swimming is significantly slower |
| **Frost VFX** | Ice crystals and snowflakes at surface |
| **Blue Tint** | Water has deep blue/cyan color |
| **Hypothermia** | Extended exposure = more damage |

### Material Approach

- Deep blue water tint
- Surface ice crystal particles
- Frost rim around edges
- Capture from Mountain/DeepNorth areas

---

## Type 4: Lava / Magma

### Concept
- **Swimable lava** (fantasy-style, not instant death)
- Deals heavy fire damage
- Uses Valheim's lava shader
- Glowing, animated surface

### Visual Reference
- Ashlands lava rivers
- Volcanic pools
- Muspelheim (mythological)

### Architecture

```csharp
public class LavaController : MonoBehaviour
{
    private WaterMeshCombiner _waterCombiner;
    
    // Damage settings
    public float DamagePerSecond = 50f;  // HEAVY damage
    public HitData.DamageType DamageType = HitData.DamageType.Fire;
    public float BurnDuration = 5f;      // Burning continues after exit
    
    // Visual effects
    public bool EmitsLight = true;
    public float LightIntensity = 2f;
    public Color LightColor = new Color(1f, 0.4f, 0f);
    
    // Swimming in lava
    public float SwimSpeedMultiplier = 0.3f;  // Very slow
    public bool CanSwim = true;               // Fantasy mode
    // public bool InstantDeath = false;      // Hardcore mode option
    
    // Ember particles
    private ParticleSystem _emberParticles;
    private ParticleSystem _smokeParticles;
    private Light _surfaceLight;
}
```

### Key Features

| Feature | Description |
|---------|-------------|
| **Heavy Fire Damage** | 50+ damage per second |
| **Burning Status** | Continues burning after exit |
| **Glow/Light** | Lava emits light around it |
| **Ember VFX** | Floating ember particles |
| **Smoke VFX** | Black smoke rising |
| **Lava Material** | Animated glowing orange surface |
| **Slow Swimming** | Very difficult to swim |
| **Fire Resistance** | Can be mitigated with fire resist |

### Material Capture Strategy

```csharp
// Capture from Ashlands
private static readonly string[] LAVA_PREFAB_NAMES = {
    "lava_floor",
    "lava_river",
    "LavaPool",
    "fx_lava_bubble",
    "Ashlands_lava",
    "magma"
};
```

---

## Implementation Architecture

### Updated WaterMeshCombiner

```csharp
public partial class WaterMeshCombiner
{
    // Extended liquid type
    public CustomLiquidType CustomLiquidType { get; set; } = CustomLiquidType.Water;
    
    // Type-specific controllers (only one active at a time)
    private FrozenWaterController _frozenController;
    private BoilingWaterController _boilingController;
    private FreezingWaterController _freezingController;
    private LavaController _lavaController;
    
    // State
    public bool IsFrozen => _frozenController != null && _frozenController.IsFrozen;
    
    /// <summary>
    /// Changes the liquid type of this combiner.
    /// </summary>
    public void SetLiquidType(CustomLiquidType newType)
    {
        if (CustomLiquidType == newType) return;
        
        // Clean up old controller
        CleanupLiquidController();
        
        CustomLiquidType = newType;
        
        // Setup new controller
        SetupLiquidController();
        
        // Update material
        UpdateMaterial();
    }
    
    private void SetupLiquidController()
    {
        switch (CustomLiquidType)
        {
            case CustomLiquidType.Ice:
                _frozenController = gameObject.AddComponent<FrozenWaterController>();
                _frozenController.Initialize(this);
                break;
            case CustomLiquidType.BoilingWater:
                _boilingController = gameObject.AddComponent<BoilingWaterController>();
                _boilingController.Initialize(this);
                break;
            case CustomLiquidType.FreezingWater:
                _freezingController = gameObject.AddComponent<FreezingWaterController>();
                _freezingController.Initialize(this);
                break;
            case CustomLiquidType.Lava:
                _lavaController = gameObject.AddComponent<LavaController>();
                _lavaController.Initialize(this);
                break;
        }
    }
}
```

### Updated WaterMaterialManager

```csharp
public static class WaterMaterialManager
{
    private static Material s_waterMaterial;
    private static Material s_tarMaterial;
    private static Material s_iceMaterial;
    private static Material s_lavaMaterial;
    private static Material s_boilingMaterial;
    private static Material s_freezingMaterial;
    
    /// <summary>
    /// Gets the appropriate material for a custom liquid type.
    /// </summary>
    public static Material GetMaterial(CustomLiquidType liquidType)
    {
        Initialize();
        
        return liquidType switch
        {
            CustomLiquidType.Water => s_waterMaterial,
            CustomLiquidType.Tar => s_tarMaterial,
            CustomLiquidType.Ice => s_iceMaterial,
            CustomLiquidType.BoilingWater => s_boilingMaterial ?? CreateBoilingMaterial(),
            CustomLiquidType.FreezingWater => s_freezingMaterial ?? CreateFreezingMaterial(),
            CustomLiquidType.Lava => s_lavaMaterial,
            _ => s_waterMaterial
        };
    }
    
    private static void CaptureMaterials()
    {
        // Water (existing)
        CaptureWaterMaterial();
        
        // Tar - IMPROVED
        CaptureTarMaterial();
        
        // Ice
        CaptureIceMaterial();
        
        // Lava
        CaptureLavaMaterial();
    }
    
    private static void CaptureTarMaterial()
    {
        // Search ZNetScene prefabs
        var prefabNames = new[] { "TarLiquid", "Tar_blob", "tar_", "TarPit" };
        
        foreach (var name in prefabNames)
        {
            // Search by partial name
            // ...
        }
        
        // Also search by shader name
        // Shader.Find("Custom/Tar") or similar
        
        // FALLBACK: Create proper tar material
        if (s_tarMaterial == null)
        {
            s_tarMaterial = CreateTarFallback();
        }
    }
    
    private static Material CreateTarFallback()
    {
        // Use Standard shader with proper settings
        var mat = new Material(Shader.Find("Standard"));
        mat.SetColor("_Color", new Color(0.05f, 0.03f, 0.02f, 0.95f));
        mat.SetFloat("_Metallic", 0.8f);  // Shiny/wet look
        mat.SetFloat("_Smoothness", 0.9f);
        mat.SetFloat("_Mode", 3); // Transparent
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.renderQueue = 3000;
        mat.name = "CustomWater_TarFallback";
        return mat;
    }
}
```

---

## Updated WaterDisk Struct

```csharp
[Serializable]
public class WaterDisk
{
    // Existing fields...
    public Vector3 Position;
    public float Radius;
    public float MaxDepth;
    public LiquidType LiquidType;  // Keep for vanilla compatibility
    
    // NEW: Extended liquid type
    public CustomLiquidType CustomLiquidType;
    
    // NEW: Frozen state
    public bool IsFrozen;
    public float IceThickness;
    
    // NEW: Damage properties (for boiling/freezing/lava)
    public float DamagePerSecond;
    public int DamageType;  // HitData.DamageType as int
    
    // NEW: Visual properties
    public Color TintColor;
    public float EffectIntensity;
}
```

---

## Persistence Updates

### WaterPersistence v3 Format

```csharp
// File format changes:
// - Add CustomLiquidType per disk
// - Add frozen state
// - Add damage properties
// - Add visual tint

private const int FILE_VERSION = 3;

// Per disk:
writer.Write((int)disk.CustomLiquidType);
writer.Write(disk.IsFrozen);
writer.Write(disk.IceThickness);
writer.Write(disk.DamagePerSecond);
writer.Write(disk.DamageType);
writer.Write(disk.TintColor.r);
writer.Write(disk.TintColor.g);
writer.Write(disk.TintColor.b);
writer.Write(disk.TintColor.a);
```

---

## Infinity Hammer Tools (Complete List)

### Water Placement Tools

```yaml
# ???????????????????????????????????????????????????????????????
# CUSTOM LIQUID TOOLS
# ???????????????????????????????????????????????????????????????

  - name: Paint Water
    description: |-
      Paint <#00AAFF>water</color> onto terrain.
      <#FFFF00>Scroll</color> to adjust brush size.
      Creates swimmable water.
    icon: FishRaw
    command: paint_water <area>
    continuous: -<mod1>
    snapGround: true

  - name: Paint Tar
    description: |-
      Paint <#332211>tar</color> onto terrain.
      <#FFFF00>Scroll</color> to adjust brush size.
      Slows movement, applies tar effect.
    icon: Tar
    command: paint_tar <area>
    continuous: -<mod1>
    snapGround: true

  - name: Paint Lava
    description: |-
      Paint <#FF4400>lava</color> onto terrain.
      <#FFFF00>Scroll</color> to adjust brush size.
      Swimable with heavy fire damage!
    icon: SurtlingCore
    command: paint_lava <area>
    continuous: -<mod1>
    snapGround: true

  - name: Paint Boiling Water
    description: |-
      Paint <#FF6600>boiling water</color> (Ashlands style).
      <#FFFF00>Scroll</color> to adjust brush size.
      Hot water that deals fire damage.
    icon: Coal
    command: paint_boiling_water <area>
    continuous: -<mod1>
    snapGround: true

  - name: Paint Freezing Water
    description: |-
      Paint <#00FFFF>freezing water</color> (Deep North style).
      <#FFFF00>Scroll</color> to adjust brush size.
      Cold water that deals frost damage.
    icon: FreezeGland
    command: paint_freezing_water <area>
    continuous: -<mod1>
    snapGround: true
```

### Ice/Frozen Tools

```yaml
  - name: Freeze Water
    description: |-
      <#00FFFF>Freeze</color> existing water to solid ice.
      Click on water to freeze.
      Creates walkable ice surface.
    icon: FreezeGland
    command: freeze_water
    instant: false

  - name: Thaw Ice
    description: |-
      <#FF6600>Thaw</color> frozen ice back to water.
      Click on ice to thaw.
    icon: Torch
    command: thaw_water
    instant: false

  - name: Break Ice Hole
    description: |-
      <#FFFFFF>Break hole</color> in frozen ice.
      <#FFFF00>Scroll</color> to adjust hole size.
      Creates opening to water below.
    icon: PickaxeIron
    command: break_ice <area>
    continuous: false
```

### Conversion Tools

```yaml
  - name: Convert Liquid Type
    description: |-
      <#AAAAFF>Convert</color> existing liquid to different type.
      Click on water/tar/lava to cycle types.
      Hold <mod1> to reverse cycle.
    icon: Crystal
    commands:
      - command: cycle_liquid_type forward
      - command: cycle_liquid_type backward
        keys: <mod1>
    instant: false
```

### Removal Tools

```yaml
  - name: Delete Liquid
    description: |-
      <#FF4444>Delete</color> custom liquid in area.
      <#FFFF00>Scroll</color> to adjust radius.
    icon: SledgeDemolisher
    command: delete_liquid <area>
    continuous: -<mod1>

  - name: Clear Zone Liquids
    description: |-
      <#FF4444>Clear ALL</color> custom liquids in current zone.
    icon: Hammer
    command: clear_zone_liquids
    instant: true
```

---

## Implementation Phases

### Phase A: Fix Tar Material (1-2 hours) ? COMPLETE
1. [x] Update `WaterMaterialManager.CaptureTarMaterial()` to search ZNetScene
2. [x] Add prefab name search for tar-related objects
3. [x] Create proper fallback material using Standard shader
4. [x] Test tar displays correctly

### Phase A.5: Custom Liquid Type Foundation ? COMPLETE
1. [x] Create `CustomLiquidType` enum (Water, Tar, Ice, BoilingWater, FreezingWater, Lava)
2. [x] Create `CustomLiquidTypeExtensions` (ToVanilla, IsHazardous, IsSolid, HasWaves, GetDisplayName)
3. [x] Add `CustomLiquidType` property to `WaterDisk` with backward-compatible serialization
4. [x] Add `CustomLiquidType` property to `WaterMeshCombiner` with `SetCustomLiquidType()` method
5. [x] Update `WaterMeshCombinerAnchor` to persist `CustomLiquidType` (ZDO save/load)
6. [x] Create `LiquidDamageHandler` component for hazardous liquids
7. [x] Wire `LiquidDamageHandler` into `WaterCombinerTrigger` (OnTriggerStay/OnTriggerExit)
8. [x] Update `WaterMaterialManager` with `GetMaterial(CustomLiquidType)` and all material capture/fallbacks
9. [x] Add `set_liquid_type` and `cycle_liquid_type` console commands
10. [x] All builds pass

### Phase B: Ice/Frozen System (6-8 hours)
1. [x] Create `FrozenWaterController` class
2. [x] Capture ice material from Mountain biome prefabs
3. [x] Generate ice mesh (copy of water surface, flattened)
4. [x] Add MeshCollider for walking
5. [x] Implement ice breaking mechanics
6. [x] Add ice hole tracking
7. [ ] Visual: cracking VFX, sound effects
8. [x] Infinity Hammer tools: freeze, thaw, break

### Phase B.5: Water Smoothing & Liquid Placement Tools ? COMPLETE
1. [x] Create `smooth_water` console command (distance-weighted average, strength parameter)
2. [x] Create `place_water_type` console command (all 6 liquid types, IH parameter format)
3. [x] Add Infinity Hammer YAML tool definitions for all liquid types
4. [x] Add Smooth Water IH tool, Mop Water IH tool, Delete Water IH tool
5. [x] Add Cycle Liquid Type IH tool (forward/backward with mod1)
6. [x] All builds pass

### Phase C: Damage-Dealing Liquids (4-6 hours)
1. [ ] Create `DamagingLiquidBase` abstract class
2. [ ] Implement damage tick system in swim trigger
3. [ ] Create `BoilingWaterController` with fire damage
4. [ ] Create `FreezingWaterController` with frost damage
5. [ ] Add status effect application
6. [ ] Steam/frost particle effects
7. [ ] Infinity Hammer tools: paint boiling, paint freezing

### Phase D: Lava System (4-6 hours)
1. [ ] Capture lava material from Ashlands prefabs
2. [ ] Create `LavaController` class
3. [ ] Implement heavy fire damage
4. [ ] Add light emission from lava surface
5. [ ] Ember/smoke particle effects
6. [ ] Burning status effect on exit
7. [ ] Infinity Hammer tool: paint lava

### Phase E: Extended Material Manager (2-4 hours)
1. [ ] Add material capture for all types
2. [ ] Create quality fallbacks for each material
3. [ ] Add material tinting options
4. [ ] Support for custom color overrides
5. [ ] Debug commands for material testing

### Phase F: Persistence & Multiplayer (2-4 hours)
1. [ ] Update WaterDisk with new properties
2. [ ] Update persistence format to v3
3. [ ] Add backwards compatibility for v2
4. [ ] Test multiplayer sync of new liquid types
5. [ ] Add liquid type to RPC operations

---

## File Structure (New & Updated Files)

```
FiresNPCs/Modules/CustomWater/
+-- Core/
?   +-- CustomLiquidType.cs           # ? Extended enum + extensions
?   +-- LiquidDamageHandler.cs        # ? Damage for hazardous liquids
?   +-- WaterDisk.cs                  # ? UPDATED - CustomLiquidType property + serialization
?   +-- WaterMeshCombiner.cs          # ? UPDATED - CustomLiquidType, SetCustomLiquidType(), damage handler
?   +-- WaterMeshCombiner.MeshGen.cs  # ? UPDATED - material selection, trigger damage integration
?   +-- WaterMeshCombinerAnchor.cs    # ? UPDATED - CustomLiquidType ZDO persistence
?   +-- (existing files unchanged)
+-- Ice/
?   +-- FrozenWaterController.cs      # ? DONE - Ice surface management, MeshCollider, hole tracking
?   +-- IceBreakingSystem.cs          # PLANNED - Advanced hole creation/tracking
+-- Hazard/
?   +-- BoilingWaterController.cs     # PLANNED - VFX + hot water specifics
?   +-- FreezingWaterController.cs    # PLANNED - VFX + cold water specifics
?   +-- HazardEffectsManager.cs       # PLANNED - Shared particle effects
+-- Lava/
?   +-- LavaController.cs             # PLANNED - Lava surface management
?   +-- LavaLightEmitter.cs           # PLANNED - Dynamic lighting
+-- Rendering/
?   +-- WaterMaterialManager.cs       # ? UPDATED - All 6 material types + capture/fallback
+-- Tools/
?   +-- WaterDebugCommands.cs         # ? UPDATED - set_liquid_type, cycle_liquid_type commands
+-- LIQUID_EXPANSION_PLAN.md          # This file
```

---

## Testing Checklist

### Tar
- [ ] Tar displays correct dark material (not pink)
- [ ] Tar applies tar status effect
- [ ] Tar persists through save/load

### Ice
- [ ] Water can be frozen
- [ ] Ice is solid (player walks on it)
- [ ] Ice can be broken (creates holes)
- [ ] Holes reveal water beneath
- [ ] Ice persists through save/load
- [ ] Ice thaws correctly

### Boiling Water
- [ ] Displays with orange/red tint
- [ ] Steam particles at surface
- [ ] Deals fire damage while swimming
- [ ] No wet effect (too hot)
- [ ] Persists through save/load

### Freezing Water
- [ ] Displays with blue tint
- [ ] Frost particles at surface
- [ ] Deals frost damage while swimming
- [ ] Applies freezing status
- [ ] Swimming is slower
- [ ] Persists through save/load

### Lava
- [ ] Displays with lava material (glowing orange)
- [ ] Emits light
- [ ] Ember particles
- [ ] Heavy fire damage while swimming
- [ ] Burning continues after exit
- [ ] Swimming is very slow
- [ ] Persists through save/load

### All Types
- [ ] Can convert between types
- [ ] Multiplayer sync works
- [ ] Infinity Hammer tools work
- [ ] VFX cleanup on logout

---

## Performance Considerations

### Ice
- Hole tracking uses spatial hashing for O(1) lookup
- Limit max holes per ice surface (e.g., 20)
- Old holes gradually re-freeze

### Damage Liquids
- Damage tick uses coroutine, not Update()
- Single damage check per IWaterInteractable
- Pool particle systems

### Lava
- Light uses single shared light per surface
- Ember particles are pooled
- LOD reduces particles at distance

---

## Questions to Resolve

1. **Ice Breaking**: Tool-based only, or any damage breaks ice?
2. **Ice Refreezing**: Should holes refreeze over time?
3. **Lava Swimming**: Fantasy mode (swimable) or instant death?
4. **Fire Resistance**: How much does it reduce lava damage?
5. **Boat Behavior**: Can boats sail on lava? (Probably burn!)
6. **Mixing Liquids**: What happens if water meets lava? (Steam explosion?)

---

## Estimated Total Effort

| Phase | Hours |
|-------|-------|
| Phase A: Fix Tar Material | 1-2 |
| Phase B: Ice/Frozen System | 6-8 |
| Phase C: Damage-Dealing Liquids | 4-6 |
| Phase D: Lava System | 4-6 |
| Phase E: Extended Material Manager | 2-4 |
| Phase F: Persistence & Multiplayer | 2-4 |

**Total: 19-30 hours**

---

## Changelog

| Date | Change |
|------|--------|
| 2025-XX-XX | Created LIQUID_EXPANSION_PLAN.md |
| 2025-XX-XX | Defined 5 new liquid types: Ice, Boiling, Freezing, Lava, (Tar fix) |
| 2025-XX-XX | Designed ice breaking mechanics |
| 2025-XX-XX | Planned damage system for hazard liquids |
| 2025-XX-XX | Created Infinity Hammer tool specifications |
| 2025-XX-XX | **IMPLEMENTED Phase A + A.5**: CustomLiquidType enum, LiquidDamageHandler, WaterMaterialManager expansion (ice/lava/boiling/freezing materials), WaterDisk + WaterMeshCombiner + WaterMeshCombinerAnchor CustomLiquidType persistence, set_liquid_type/cycle_liquid_type console commands, WaterCombinerTrigger damage integration |
| 2025-XX-XX | **IMPLEMENTED Phase B (partial)**: FrozenWaterController with solid ice mesh + MeshCollider for walking, ice hole punching, freeze_water/thaw_water/break_ice console commands, IH tool definitions for Freeze/Thaw/Break Ice. VFX/sound deferred to Phase B.7. |
