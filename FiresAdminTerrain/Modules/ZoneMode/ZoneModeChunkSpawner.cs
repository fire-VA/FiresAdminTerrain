using FiresCore.UI;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // The Chunk Hammer plants chunk seeds, its ammo: look over closed land that borders open land, within reach, and
    // strike. The strike asks for confirmation; on yes the server is asked to open that zone through the normal opening
    // pipeline (every peer rebuilds, then vanilla populates). One seed and one point of durability are used only when
    // the server confirms, so a refused or lost request costs nothing. ZoneModeSeedGhost shows the target zone while
    // the hammer is in hand with a seed to fire.
    internal static class ZoneModeChunkSpawner
    {
        private const float MaxReach = 48f;
        private const float ReplyTimeoutSeconds = 15f;

        private static bool s_pending;
        private static float s_pendingSince;
        private static string s_pendingSeed;
        private static bool s_confirming;

        [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
        private static class Attack_OnAttackTrigger_ChunkHammer
        {
            private static bool Prefix(Attack __instance)
            {
                // The Chaos Hammer swings inside its own build menu now, so its click belongs to the tool it carries.
                if (!ZoneModeSeeds.IsHammer(__instance.m_weapon)) return true;
                if (__instance.m_character is Player player && player == Player.m_localPlayer) Strike(player);
                return false;
            }
        }

        // The seed the hammer in hand would plant: the equipped ammo when it is a chunk seed, otherwise the first chunk
        // seed in the inventory (the one vanilla equips on the next swing).
        internal static bool TryAimedSeed(Player player, out ChunkSeed seed)
        {
            seed = null;
            if (player == null || !ZoneModeSeeds.IsHammer(player.GetRightItem())) return false;
            var inventory = player.GetInventory();
            var ammo = player.GetAmmoItem();
            if (ammo != null && inventory.ContainsItem(ammo) && ZoneModeSeeds.TryGet(ammo, out seed)) return true;
            ammo = inventory.GetAmmoItem(ZoneModeSeeds.AmmoType);
            return ammo != null && ZoneModeSeeds.TryGet(ammo, out seed);
        }

        private static void Strike(Player player)
        {
            if (s_confirming || !TryAimedSeed(player, out var seed)) return;
            if (!ZoneModeRegistry.ModeActive) { Tell(player, "Chunk seeds only grow in a one-zone-at-a-time world."); return; }
            if (s_pending && Time.time - s_pendingSince < ReplyTimeoutSeconds) { Tell(player, "The land is still answering your last seed..."); return; }
            if (!ZoneModeCommands.TryGetLookedAtZone(out var zone)) { Tell(player, "Look out over the closed land you want to raise."); return; }
            string refusal = Check(zone, player.transform.position, true);
            if (refusal != null) { Tell(player, refusal); return; }
            bool choosingLand = ZoneModeEyeOfTheBeholder.IsWorn(player);
            if (!UnifiedPopup.IsAvailable())
            {
                Plant(player, seed, zone, choosingLand);
                return;
            }
            s_confirming = true;
            InputBlock.Block(true);
            UnifiedPopup.Push(new YesNoPopup($"Plant the {seed.Name}?", Promise(seed, zone, choosingLand),
                () => Answer(true, seed, zone, choosingLand), () => Answer(false, seed, zone, choosingLand), false));
        }

        private static string Promise(ChunkSeed seed, Vector2s zone, bool choosingLand)
        {
            string cost = $" This uses one {seed.Name}.";
            if (!ZoneModePark.TryGetParked(zone, out var park)) return $"Zone ({zone.x},{zone.y}) rises as {seed.Land}.{cost}";
            if (!choosingLand || ZoneModePark.SeedMatches(seed, zone, park))
                return $"Zone ({zone.x},{zone.y}) returns as it was"
                    + (park.Pieces > 0 ? $", with the {park.Pieces} pieces built there." : ".") + cost;
            return $"Zone ({zone.x},{zone.y}) rises as {seed.Land} INSTEAD of returning as it was: "
                + (park.Pieces > 0 ? $"the {park.Pieces} pieces built there are lost for good." : "what stood there is lost for good.") + cost;
        }

        private static void Answer(bool plant, ChunkSeed seed, Vector2s zone, bool choosingLand)
        {
            if (!s_confirming) return;
            s_confirming = false;
            UnifiedPopup.Pop();
            InputBlock.Block(false);
            var player = Player.m_localPlayer;
            if (plant && player != null) Plant(player, seed, zone, choosingLand);
        }

        private static void Plant(Player player, ChunkSeed seed, Vector2s zone, bool choosingLand)
        {
            if (FindSeed(player, seed.Prefab) == null) { Tell(player, $"You have no {seed.Name} left."); return; }
            string refusal = Check(zone, player.transform.position, true);
            if (refusal != null) { Tell(player, refusal); return; }
            s_pending = true;
            s_pendingSince = Time.time;
            s_pendingSeed = seed.Prefab;
            ZoneModeOpening.RequestSeedOpen(zone, seed.Prefab, choosingLand);
            Tell(player, $"The {seed.Name} takes root... ({zone.x},{zone.y})");
        }

        // A popup closed by anything but its own buttons must not leave the controls blocked.
        internal static void ClientTick()
        {
            if (Time.time >= s_nextHintCheck)
            {
                s_nextHintCheck = Time.time + HintCheckSeconds;
                var player = Player.m_localPlayer;
                if (player != null && ZoneModeSeeds.IsHammer(player.GetRightItem())) ZoneModeRavenHints.ShowHammer(player);
            }
            if (!s_confirming || UnifiedPopup.IsVisible()) return;
            s_confirming = false;
            InputBlock.Block(false);
        }

        private const float HintCheckSeconds = 0.5f;
        private static float s_nextHintCheck;

        // Shared by the client (before asking) and the server (before opening): null means allowed.
        internal static string Check(Vector2s zone, Vector3 user, bool checkReach)
        {
            if (!ZoneOpenSet.InRange(zone.x, zone.y)) return "That is beyond the edge of the world.";
            if (ZoneModeRegistry.IsOpen(zone)) return "That land is already open — look out over the closed land.";
            if (!ZoneModeOpening.HasOpenEdgeNeighbour(zone)) return "A zone only rises beside open land.";
            if (checkReach && DistanceToZone(user, zone) > MaxReach) return "Get closer to the edge of that zone.";
            return null;
        }

        private static float DistanceToZone(Vector3 point, Vector2s zone)
        {
            float outsideX = Mathf.Max(0f, Mathf.Abs(point.x - zone.x * ZoneOpenSet.ZoneSize) - ZoneOpenSet.ZoneSize * 0.5f);
            float outsideZ = Mathf.Max(0f, Mathf.Abs(point.z - zone.y * ZoneOpenSet.ZoneSize) - ZoneOpenSet.ZoneSize * 0.5f);
            return Mathf.Sqrt(outsideX * outsideX + outsideZ * outsideZ);
        }

        // The server's answer to this client's request.
        internal static void OnResult(bool opened, Vector2s zone, string message)
        {
            var player = Player.m_localPlayer;
            string seed = s_pendingSeed;
            s_pending = false;
            s_pendingSeed = null;
            if (player == null) return;
            if (opened && seed != null) UseOne(player, seed);
            Tell(player, message);
        }

        private static void UseOne(Player player, string prefab)
        {
            var item = FindSeed(player, prefab);
            if (item != null) player.GetInventory().RemoveItem(item, 1);
            var hammer = player.GetRightItem();
            if (!ZoneModeSeeds.IsHammer(hammer) || !hammer.m_shared.m_useDurability) return;
            hammer.m_durability = Mathf.Max(0f, hammer.m_durability - hammer.m_shared.m_useDurabilityDrain * Game.m_durabilityRate);
        }

        private static ItemDrop.ItemData FindSeed(Player player, string prefab)
        {
            var inventory = player.GetInventory();
            var ammo = player.GetAmmoItem();
            if (ammo?.m_dropPrefab != null && ammo.m_dropPrefab.name == prefab && inventory.ContainsItem(ammo)) return ammo;
            foreach (var item in inventory.GetAllItems())
                if (item?.m_dropPrefab != null && item.m_dropPrefab.name == prefab) return item;
            return null;
        }

        private static void Tell(Player player, string message)
            => player.Message(MessageHud.MessageType.Center, message, 0, null, false);

        internal static void Reset()
        {
            s_pending = false;
            s_pendingSeed = null;
            if (!s_confirming) return;
            s_confirming = false;
            if (UnifiedPopup.IsVisible()) UnifiedPopup.Pop();
            InputBlock.Block(false);
        }
    }
}
