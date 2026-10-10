using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // The Chaos Hammer: the hammer that plants land torn out of other worlds, and the admin's own tool. A swing places
    // a chaos chunk; right click opens its blueprint menu for an admin. A fuling laughs whenever it is drawn or swung,
    // borrowed straight off the Goblin's own idle chatter.
    internal static class ZoneModeChaosHammer
    {
        private const string GoblinPrefab = "Goblin";
        private const float GiggleCooldownSeconds = 0.6f;

        private static GameObject s_giggle;
        private static bool s_giggleLooked;
        private static float s_nextGiggle;

        internal static bool IsChaosHammer(ItemDrop.ItemData item)
            => item?.m_dropPrefab != null && item.m_dropPrefab.name == ZoneModePrefabs.ChaosHammerPrefab;

        internal static bool IsHolding(Player player)
            => player != null && IsChaosHammer(player.GetRightItem());

        // Drawing it: the same laugh a fuling gives when it notices you.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class Humanoid_EquipItem_ChaosHammer
        {
            private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
            {
                if (!__result || __instance != Player.m_localPlayer || !IsChaosHammer(item)) return;
                Giggle(__instance.transform.position);
                ZoneModeChunkTools.RefreshNames();
            }
        }

        // A tool run from the hammer's own build menu laughs like a swing did.
        internal static void Laugh(Player player)
        {
            if (player != null) Giggle(player.transform.position);
        }

        // One laugh at a time: a swing that follows a draw closely would otherwise stack two.
        private static void Giggle(Vector3 at)
        {
            if (Time.time < s_nextGiggle) return;
            s_nextGiggle = Time.time + GiggleCooldownSeconds;
            var source = GiggleSource();
            if (source == null) return;
            Object.Instantiate(source, at, Quaternion.identity);
        }

        // The Goblin's idle chatter, taken off the Goblin itself rather than looked up by name: sound prefabs are not
        // in ZNetScene's own list.
        private static GameObject GiggleSource()
        {
            if (s_giggle != null || s_giggleLooked) return s_giggle;
            s_giggleLooked = true;
            var goblin = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(GoblinPrefab) : null;
            var ai = goblin != null ? goblin.GetComponent<MonsterAI>() : null;
            var effects = ai?.m_idleSound?.m_effectPrefabs;
            if (effects != null)
                foreach (var effect in effects)
                    if (effect?.m_prefab != null) { s_giggle = effect.m_prefab; break; }
            if (s_giggle == null)
                FiresLogger.LogWarning("[FiresZones] the Goblin's idle sound is not where it was: the Chaos Hammer laughs silently.");
            return s_giggle;
        }

        internal static void Reset()
        {
            s_giggle = null;
            s_giggleLooked = false;
            s_nextGiggle = 0f;
        }
    }
}
