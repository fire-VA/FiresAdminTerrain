using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Utilities;

namespace VerdantsAscent.Terrain.Voxel
{
    // Register a LOCATION PAD as each location is built (server placement AND
    // client proxy rebuild both route through this private SpawnLocation, so
    // every peer that owns the zone bakes the same pad). We flatten only
    // locations whose PREFAB carries a leveling TerrainModifier — the authored
    // "this wants flat ground" signal. Scatter props (runestones, spawners)
    // have none and keep their natural, correctly-anchored ground. Snap-to-water
    // locations sit on the y30 sea by design and are left to the ocean.
    // The postfix of FAT's one ZoneSystem.SpawnLocation hook (FatVanillaHooks).
    internal static class LocationSpawnPads
    {
        // Per-prefab verdict cache: {wants flatten, pad radius}. Scanning the
        // prefab's children every spawn is wasteful — locations repeat.
        private static readonly Dictionary<string, float> s_padRadius
            = new Dictionary<string, float>();

        internal static void OnSpawned(ZoneSystem.ZoneLocation location, Vector3 pos,
            ZoneSystem.SpawnMode mode)
        {
            try
            {
                if (!VoxelOnlyMode.Active) return;
                // Ghost = world-gen preview with no live heightmaps/voxels to
                // level; only the real Full build gets a pad.
                // 0.2.841: Ghost too - a dedicated server generates zones as Ghost and never Full-spawns them later (0.2.831's finding).
                if (mode != ZoneSystem.SpawnMode.Full && !(mode == ZoneSystem.SpawnMode.Ghost && VerdantsAscent.Terrain.Zones.FatLocationAccelerator.LevelingOn)) return;   // 0.2.841b: new worlds only
                if (location == null || location.m_snapToWater) return;
                // 0.2.740 (Fire 10-05: skylands "reshape/recarve themselves when we get close"): a location on a skyland is
                // levelled by the skyland's own shape (its site pad, flat before anything is carved), so it gets no GROUND
                // pad. Registered at the island's height, that pad lifted the ground columns round it to ~215 m on spawn:
                // the near voxels changed under the island's edge, and the far view never had them.
                if (FiresSkyIslands.IsOnIslandExact(pos)) return;
                // 0.2.799: an underground location's floor is levelled by its own dome (FatUndergroundLocations); a ground pad
                // at its depth would level the SURFACE columns down to it.
                if (FatUndergroundLocations.IsUnderground(pos)) return;

                string name = !string.IsNullOrEmpty(location.m_name)
                    ? location.m_name : location.m_prefabName;
                float radius;
                if (string.IsNullOrEmpty(name)) radius = ComputePadRadius(location);
                else if (!s_padRadius.TryGetValue(name, out radius))
                {
                    radius = ComputePadRadius(location);
                    s_padRadius[name] = radius;
                }
                // 0.2.841 (Fire 10-07: "relax the terrain delta, and just level the area the location is placed"): a site the
                // accelerator accepted over the location's own terrain delta gets a pad over its whole footprint.
                if (VerdantsAscent.Terrain.Zones.FatLocationAccelerator.TryLevelSite(pos, out var site))
                {
                    radius = Mathf.Max(radius, site.Radius);
                    float skirt = Mathf.Clamp(radius * 0.5f, 6f, 20f);   // FiresLocationPads.Register's apron
                    FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[LocationLevel] {0} at ({1:0},{2:0},{3:0}): footprint r {4:0.0}, delta before max {5:0.0} m -> levelled, skirt {6:0.0} m",
                        location.m_prefabName, pos.x, pos.y, pos.z, radius, site.Delta, skirt));
                }
                if (radius <= 0f) return;   // no leveling intent — natural ground

                FiresLocationPads.Register(pos, radius, LocationPadMode.SurfaceLevel);
                VoxelZoneStreamer.MarkAreaDirty(pos, radius);
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxel] location pad register threw: {ex.Message}");
            }
        }

        // A location wants flat ground when its prefab bakes a leveling
        // TerrainModifier. The pad radius is the location's exterior (clear)
        // radius — the extent it already reserves — never smaller than the
        // widest modifier it ships.
        private static float ComputePadRadius(ZoneSystem.ZoneLocation location)
        {
            var asset = location.m_prefab.IsValid ? location.m_prefab.Asset : null;
            if (asset == null) return 0f;
            var mods = asset.GetComponentsInChildren<TerrainModifier>(true);
            if (mods == null || mods.Length == 0) return 0f;
            float modR = 0f; bool levels = false;
            foreach (var m in mods)
            {
                if (m == null) continue;
                if (m.m_level) { levels = true; if (m.m_levelRadius > modR) modR = m.m_levelRadius; }
                if (m.m_smooth && m.m_smoothRadius > modR) modR = m.m_smoothRadius;
            }
            if (!levels) return 0f;
            return Mathf.Max(location.m_exteriorRadius, modR);
        }
    }
}
