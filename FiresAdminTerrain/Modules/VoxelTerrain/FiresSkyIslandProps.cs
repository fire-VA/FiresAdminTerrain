using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Natural dressing for sky islands: rocks/stumps/fallen logs around the
    // rim, embedded rocks + hanging roots on the belly. Placed as REAL world
    // objects (ZNetView prefabs → ZDOs) through the same once-per-zone
    // server-side pass vanilla vegetation uses, so they persist, sync, and
    // respect ghost pregeneration. Positions are pure island math — no
    // physics probes, so placement works before any chunk exists.
    // Runs from FAT's one ZoneSystem.PlaceVegetation hook (FatVanillaHooks),
    // after the ice-floe pass, so that pass never sees these props.
    internal static class FiresSkyIslandProps
    {
        private static readonly List<Vector3> s_pos = new List<Vector3>(96);
        private static readonly List<Quaternion> s_rot = new List<Quaternion>(96);
        private static readonly List<string> s_name = new List<string>(96);
        private static readonly HashSet<string> s_missing = new HashSet<string>();
        private static bool s_loggedFirst;

        internal static void OnPlaceVegetation(Vector2s zoneID, Vector3 zoneCenterPos,
            ZoneSystem.SpawnMode mode, List<GameObject> spawnedObjects)
        {
            if (mode == ZoneSystem.SpawnMode.Client) return;   // vanilla parity: server/ghost place, clients receive
            if (!FiresSkyIslands.Enabled || ZNetScene.instance == null || Zones.ZoneModeRegistry.ModeActive) return;
            if (Zones.BiomePaint.Regrowing) return;   // a painted circle re-grows its ground only; the zone's props stand
            FiresSkyIslands.CollectProps(zoneCenterPos.x - 32f, zoneCenterPos.z - 32f,
                zoneCenterPos.x + 32f, zoneCenterPos.z + 32f, s_pos, s_rot, s_name);
            if (s_pos.Count == 0) return;
            int placed = 0;
            for (int i = 0; i < s_pos.Count; i++)
            {
                var prefab = ZNetScene.instance.GetPrefab(s_name[i]);
                if (prefab == null)
                {
                    if (s_missing.Add(s_name[i]))
                        FiresLogger.LogWarning($"[FiresSkyIslands] prop prefab '{s_name[i]}' not in ZNetScene — skipped.");
                    continue;
                }
                if (mode == ZoneSystem.SpawnMode.Ghost) ZNetView.StartGhostInit();
                var go = Object.Instantiate(prefab, s_pos[i], s_rot[i]);
                if (mode == ZoneSystem.SpawnMode.Ghost)
                {
                    spawnedObjects.Add(go);
                    ZNetView.FinishGhostInit();
                }
                placed++;
            }
            if (!s_loggedFirst && placed > 0)
            {
                s_loggedFirst = true;
                FiresLogger.LogInfo($"[FiresSkyIslands] first prop batch: {placed} rim/belly prop(s) in zone ({zoneID.x},{zoneID.y}).");
            }
        }
    }
}
