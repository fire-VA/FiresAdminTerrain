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
                if (mode != ZoneSystem.SpawnMode.Full) return;
                if (location == null || location.m_snapToWater) return;

                string name = !string.IsNullOrEmpty(location.m_name)
                    ? location.m_name : location.m_prefabName;
                float radius;
                if (string.IsNullOrEmpty(name)) radius = ComputePadRadius(location);
                else if (!s_padRadius.TryGetValue(name, out radius))
                {
                    radius = ComputePadRadius(location);
                    s_padRadius[name] = radius;
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
