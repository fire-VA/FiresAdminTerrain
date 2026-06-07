# Handoff — FiresWater: flat water plane overlapping boats + multi-layer mask stacking

> Pasteable into the FiresWater / FiresAdminTerrain CustomWater discussion. Self-contained:
> symptoms, repro, hypotheses, and where to start. No fix has been attempted yet — this is a
> diagnose-first report.

## TL;DR

While sailing, a **flat plane of water draws across/over the boat at the wrong height**. It's most
obvious when the camera is rotated to certain angles, and it gets worse in high waves — the
boat's water-mask cutout appears to **stack in multiple layers on top of itself** as the hull
rides up and down. It "feels like a water plane is attached to the camera."

Likely tied to either (a) the boat-interior dry-out water-mask / stencil setup, or (b) how we
handle the vanilla **low-LOD flat ocean plane (~y=30)**. Needs to be dialed in before anything else.

## Observed behavior (in-game)

1. **Flat water plane over the boat at the wrong level.** Turning the camera to the right angle
   reveals a clear flat sheet of water drawn overlapping the boat, sitting at the wrong height
   (not following the wave surface). See screenshots — the hull reads as half-submerged behind a
   flat transparent sheet.
2. **Multi-layer stacking over waves.** Going up and down over waves, the water-mask cutout thrown
   by the boat redraws in **multiple overlapping layers** instead of one clean cutout.
3. **Camera-attached feel.** The artifact tracks with camera movement in a way that makes it look
   like a water plane is parented to / following the camera rather than world-anchored.
4. **(Separate, later) underwater⇄overwater flicker.** In high waves, rotating the camera makes the
   view swap between the underwater and above-water states constantly. **Explicitly deferred** —
   fix the plane-overlap anomaly first.

## Leading hypothesis (from the report)

When a large wave distorts the boat/water mesh, the boat drops low enough to intersect the
**flat ocean plane at ~y=30**. That plane was previously expected **not** to render inside loaded
zones (it should only be the distant low-LOD ocean drawn *past* the loaded zone ring) — so either:

- that earlier assumption ("the y=30 low-LOD plane isn't drawn within loaded zones / is suppressed
  by our shader setup") was wrong, **or**
- the low-LOD ocean plane is still being handled incorrectly and bleeds into the near field.

Open question to settle first: **is the y≈30 flat plane supposed to be drawn within loaded zones at
all, and if not, what is actually suppressing it today?**

## Where to start (orientation)

- **Module:** `FiresAdminTerrain` → `Modules/CustomWater/` (Rendering subfolder in particular).
- **Components seen live in the latest log:**
  - `WaterMaterialManager` — captures the water/tar/ice/lava materials from vanilla prefabs.
  - `OceanZoneRing` / `OceanZoneRingExtender` (`Modules/CustomWater/Rendering/`) — the LOD water
    ring around loaded zones.
  - `FiresWaterExtendedRing` — spawns the custom water tiles (`sharedMaterial=Custom/FiresWater`).
  - `WaterPersistence` — restores water combiners from file cache.
- **Shader:** `Custom/FiresWater` (replacement for vanilla `Custom/Water`).
  - v2 surface pass (Fresnel + cubemap + dual-normal + ashlands + foam) and v4 underwater pass.
  - Uses vanilla `_GlobalWind1/2/Alpha` globals for wave sync.
  - **Dual-purpose stencil**: boat-interior dry-out **and** adjacent-mesh overlap claim, done in one
    block via the `Pass Invert` trick (`Ref 0 ReadMask 18 WriteMask 16 Comp Equal Pass Invert`).
    Vanilla `WaterMask` writes stencil **bit 1**; mod overlap claim uses **bit 4**; bits 6/7 are
    owned by the Unity built-in pipeline. The boat-interior dryness depends on the `WaterMask`
    mesh on the boat punching its stencil bit and the water shader testing against it.

### Concrete lead worth checking immediately

The latest boot log contains this warning right around player spawn:

```
[OceanZoneRing] RescanLodRenderers failed: Object reference not set to an instance of an object
```

If `RescanLodRenderers` is NRE-ing, the LOD water ring may not be (de)registering the renderers it's
supposed to manage — which is consistent with a stray flat LOD plane rendering where it shouldn't.
Worth confirming whether this NRE leaves a low-LOD ocean renderer un-culled near the boat.

## Suggested diagnostic angles (before any shader/stencil change)

1. **Identify every active water renderer near the boat at the moment of the artifact** — enumerate
   water-shader renderers within a few zones of the player and log their object name, world Y,
   bounds, shader/material, and stencil state. Find the flat one at ~y=30 and trace who spawned it.
2. **Confirm the y≈30 plane's owner** — is it the vanilla distant-ocean LOD, our `OceanZoneRing`
   extension, or a leftover/duplicate? Is it world-anchored or following the camera/player?
3. **Boat WaterMask stencil** — verify the boat's mask punches its stencil bit exactly once per
   frame and that the water pass's stencil test (the `Pass Invert` recipe) isn't allowing the
   distant plane to re-draw through the cutout when the hull dips below y≈30 in waves (the
   multi-layer stacking symptom).
4. **Camera-relative check** — confirm no water plane transform is parented to / offset from the
   camera; the "attached to camera" feel suggests a per-frame transform follow somewhere.

## Screenshots referenced

Three in-game shots provided (longship + karve while sailing). They show a flat, transparent water
sheet cutting across the hull at the waterline regardless of the actual wave surface, with the boat
reading as partially submerged behind it; the effect shifts with camera angle.

## Constraints / reminders carried over

- Diagnose before patching — this class of water-shader bug has burned a lot of time on guesses;
  use the on-disk sources (decompiled `WaterVolume`/`WaterPlane`, compiled shader `.asset` YAMLs,
  Steamy Dumps) rather than inferring.
- Don't go nuclear: touch only the plane/stencil path implicated by the diagnostics; don't strip
  adjacent water features speculatively.
