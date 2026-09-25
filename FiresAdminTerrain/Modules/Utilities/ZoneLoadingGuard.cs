using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Utilities
{
    // ZoneSystem.UnsetLoadingInZone indexes a dictionary with a key it recomputes from the ZDO's CURRENT position:
    //
    //   SetLoadingInZone(zdo)   { var sector = zdo.GetSector(); ...add under sector... }
    //   UnsetLoadingInZone(zdo) { var sector = zdo.GetSector(); m_loadingObjectsInZones[sector].Remove(zdo); }
    //
    // So a ZDO that changes sector between the async room load starting and finishing is looked up under a key it
    // was never filed under, and the indexer throws KeyNotFoundException. Seen live as key '0,0' - the world
    // ORIGIN - out of DungeonGenerator.ReleaseHeldReferences, which then dies partway and leaks every reference it
    // was holding.
    //
    // Removing a ZDO from a sector it is not filed under is a no-op by definition, so the exception accomplishes
    // nothing except killing the caller's cleanup. This lets the cleanup finish and says which ZDO moved and where
    // it ended up, because the mover is the actual bug and nothing in FAT touches LoadingInZone.
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.UnsetLoadingInZone))]
    internal static class Patch_ZoneSystem_UnsetLoadingInZone_Guard
    {
        private static readonly AccessTools.FieldRef<ZoneSystem, Dictionary<Vector2s, List<ZDO>>> LoadingRef =
            AccessTools.FieldRefAccess<ZoneSystem, Dictionary<Vector2s, List<ZDO>>>("m_loadingObjectsInZones");

        private static bool Prefix(ZoneSystem __instance, ZDO zdo)
        {
            if (__instance == null || zdo == null) return false;

            Dictionary<Vector2s, List<ZDO>> loading = LoadingRef(__instance);
            if (loading == null) return false;

            Vector2s sector = zdo.GetSector();
            if (loading.ContainsKey(sector)) return true;

            FiresLogger.LogWarning($"[ZoneLoad] {Describe(zdo)} finished loading in sector {sector.x},{sector.y}, which "
                + "it was never registered under - it changed sector while its room loaded. Vanilla would throw here "
                + "and abandon the rest of DungeonGenerator.ReleaseHeldReferences; the cleanup is allowed to finish "
                + "instead. Whatever moved this ZDO mid-load is the real fault.");
            return false;
        }

        private static string Describe(ZDO zdo)
        {
            string name = "(unknown prefab)";
            var scene = ZNetScene.instance;
            if (scene != null)
            {
                GameObject prefab = scene.GetPrefab(zdo.GetPrefab());
                if (prefab != null) name = prefab.name;
            }
            Vector3 at = zdo.GetPosition();
            return $"'{name}' at ({at.x:0.#}, {at.y:0.#}, {at.z:0.#})";
        }
    }
}
