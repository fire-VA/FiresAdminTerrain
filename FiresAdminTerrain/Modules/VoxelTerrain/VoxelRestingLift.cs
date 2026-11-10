using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1061 ([fgn] log-proved on SuperFlatTest8, Fire's "hop"): vanilla Floating.TerrainCheck (carts, ships) and
    // ItemDrop.TerrainCheck lift an object to GetGroundHeight + 1 / + 0.5 when it sits more than 1 / 0.5 m below that height.
    // On a voxel world GetGroundHeight is FAT's ground truth (VoxelGroundTruth), which can answer above the collider the body
    // actually rests on; every 30 s the cart was lifted and dropped back ("Moved up item Cart(Clone)").
    // Guard: before a lift, a short ray down on the terrain layer from just above the object. When a real terrain collider
    // is right under it (the object rests on the ground it collides with), the lift is skipped. A body that really fell
    // through (nothing under it) is still lifted, as vanilla does. Voxel-only worlds only; elsewhere vanilla is unchanged.
    internal static class VoxelRestingLift
    {
        private const float Above = 0.6f, Reach = 2.5f, RestSlack = 0.5f, LogEvery = 60f;
        private static int s_mask = -1;
        private static readonly Dictionary<int, float> s_nextLog = new Dictionary<int, float>();

        private static bool RestsOnTerrain(Component c, float liftBelow)
        {
            if (!VoxelOnlyMode.Active || c == null) return false;
            Vector3 pos = c.transform.position;
            float gh = ZoneSystem.instance.GetGroundHeight(pos);
            if (pos.y - gh >= -liftBelow) return false;   // vanilla wouldn't lift: nothing to guard
            if (s_mask == -1) s_mask = LayerMask.GetMask("terrain");
            if (!Physics.Raycast(pos + Vector3.up * Above, Vector3.down, out RaycastHit hit, Reach, s_mask, QueryTriggerInteraction.Ignore))
                return false;
            if (pos.y < hit.point.y - RestSlack) return false;   // below the collider it should stand on: a real fall-through
            int id = c.gameObject.GetInstanceID();
            if (FiresLogger.DebugEnabled && (!s_nextLog.TryGetValue(id, out float next) || Time.time >= next))
            {
                s_nextLog[id] = Time.time + LogEvery;
                FiresLogger.LogDebug($"[FiresVoxel] resting lift skipped: {Utils.GetPrefabName(c.gameObject)} at ({pos.x:0.0}, {pos.y:0.00}, {pos.z:0.0}) "
                    + $"rests on terrain y {hit.point.y:0.00} ('{hit.collider.name}'); GetGroundHeight said {gh:0.00} ({VoxelGroundTruth.LastBranch ?? "vanilla"})");
            }
            return true;
        }

        [HarmonyPatch(typeof(Floating), "TerrainCheck")]
        private static class Patch_Floating_TerrainCheck
        {
            private static bool Prefix(Floating __instance) => !RestsOnTerrain(__instance, 1f);
        }

        [HarmonyPatch(typeof(ItemDrop), "TerrainCheck")]
        private static class Patch_ItemDrop_TerrainCheck
        {
            private static bool Prefix(ItemDrop __instance) => !RestsOnTerrain(__instance, 0.5f);
        }
    }
}
