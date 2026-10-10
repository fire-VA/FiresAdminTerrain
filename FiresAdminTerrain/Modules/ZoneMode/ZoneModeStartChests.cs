using UnityEngine;
using VerdantsAscent.Modules.Utilities;

namespace VerdantsAscent.Terrain.Zones
{
    // A vanilla chest ZDO for one of FAT's start chests (the hammer chest, the Cultivator chest), its inventory written
    // before any peer builds it, so it never rolls loot of its own, and flagged with the chest's own mark.
    internal static class ZoneModeStartChests
    {
        private const int FallbackWidth = 4;
        private const int FallbackHeight = 2;
        private const float YawFromX = 7.1f;
        private const float YawFromZ = 3.3f;
        private const float FullTurn = 360f;

        internal static bool TryPlace(string chestPrefab, Vector3 at, GameObject item, int markHash)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(chestPrefab) : null;
            if (prefab == null || item == null) return false;
            var yaw = Quaternion.Euler(0f, Mathf.Abs(at.x * YawFromX + at.z * YawFromZ) % FullTurn, 0f);
            var zdo = NetworkObjectHelper.CreateZdo(prefab, at, yaw);
            if (zdo == null) return false;
            var container = prefab.GetComponentInChildren<Container>(true);
            var inventory = new Inventory(chestPrefab, null, container != null ? container.m_width : FallbackWidth,
                container != null ? container.m_height : FallbackHeight);
            inventory.AddItem(item, 1);
            var package = new ZPackage();
            inventory.Save(package);
            zdo.Set(ZDOVars.s_items, package.GetArray());
            zdo.Set(ZDOVars.s_addedDefaultItems, true);
            zdo.Set(markHash, true);
            return true;
        }
    }
}
