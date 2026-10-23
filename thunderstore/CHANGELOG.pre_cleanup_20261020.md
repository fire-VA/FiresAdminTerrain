## 0.2.976
- The rig-proven build of 2026-10-20.

## 0.2.974
- The rig-proven build of 2026-10-20.

* v0.2.971 - dedicated servers no longer try to draw instanced grass (Instancing is not supported spam with server-side simulation and the cutter); the Super flat build grid survives moving between worlds

* v0.2.969 - Super flat: zone grid lines drawn camera-facing (no eye-height flicker), accepts the voxel wrap and full voxel toggles, no auto clutter; spawn temple only

* v0.2.960 - Super flat worlds place the spawn temple (only), so a new character can spawn; New World option: Super flat admin build world (flat, 25% size, no other locations, plants or spawns)

* v0.2.959 - Super flat worlds place the spawn temple (only), so a new character can spawn; New World option: Super flat admin build world (flat, 25% size, no other locations, plants or spawns)

* v0.2.958 - Super flat worlds place the spawn temple (only), so a new character can spawn; New World option: Super flat admin build world (flat, 25% size, no other locations, plants or spawns)

* v0.2.957 - Super flat worlds place the spawn temple (only), so a new character can spawn; New World option: Super flat admin build world (flat, 25% size, no other locations, plants or spawns)

* v0.2.956 - New World option: Super flat admin build world (flat, 25% size, no locations, plants or spawns)

* v0.2.955 - New World option: Super flat admin build world (flat, 25% size, no locations, plants or spawns)

## 0.2.955
- Fixed a native crash: a voxel chunk mesh is now freed only after its physics bake finishes.
- Fixed a running FPS drop while terrain streams; players stuck inside solid terrain on load are lifted to the first open floor.
- Water surfaces are cached per world (the second load skips the full rebuild); caches live in BepInEx\cache\FiresAdminTerrain.
- Chunk borders heal after pads, ramps and camp levelling; clients keep a minimap cache per server world.
- Built and tested on the 10-15 rig. Requires FiresUnifiedCore 0.2.547. The plugin DLL reports 0.2.954.1 (same build; the package version is 0.2.955 because Thunderstore needs Major.Minor.Patch).

* v0.2.874 - updates and fixes
  - better caves, sinkholes, waterfalls and location placement

* v0.2.728 - first release for Valheim 1.0
  - sculpt voxel terrain, caves, rivers, waterfalls and whole worlds from the hammer, Expand World Size worlds included
