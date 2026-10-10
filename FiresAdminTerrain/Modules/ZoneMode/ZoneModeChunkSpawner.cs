using System.Collections.Generic;
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

        // ONE ANSWER TO "IS THIS THE RIGHT SEED", asked by the popup and by the Eye's label. Two
        // copies of this rule would eventually disagree, and the one place that must never be wrong
        // is the one telling you whether you are about to destroy a chunk you built.
        //
        // Overwriting means: the zone was parked (there IS something to come back), the seed does not
        // match what was parked, and the player is choosing land rather than letting it return. A zone
        // that was never parked has nothing to lose - raising it as the "wrong" biome is a choice, not
        // a loss, so it is not red and does not ask twice.
        internal static bool WouldOverwriteParkedLand(ChunkSeed seed, Vector2s zone, bool choosingLand, out int lostPieces)
        {
            lostPieces = 0;
            if (!ZoneModePark.TryGetParked(zone, out var park)) return false;
            if (!choosingLand || ZoneModePark.SeedMatches(seed, zone, park)) return false;
            lostPieces = park.Pieces;
            return true;
        }

        /// <summary>True when this seed raises the land the zone was meant to hold (or restores what was parked).</summary>
        internal static bool SeedMatchesIntent(ChunkSeed seed, Vector2s zone, Heightmap.Biome planned)
        {
            if (seed.RestoresOnly) return true;
            if (ZoneModePark.TryGetParked(zone, out var park)) return ZoneModePark.SeedMatches(seed, zone, park);
            if (seed.IsLocation) return true;   // a location seed is always a deliberate choice
            return planned == seed.Tier.Biome;
        }

        private static string Promise(ChunkSeed seed, Vector2s zone, bool choosingLand)
        {
            string cost = $" This uses one {seed.Name}.";
            // Fire (10-03, popup b): say plainly why a seed restores instead of raising new land.
            string restoresOnly = seed.RestoresOnly
                ? $" The {seed.Name} restores only: to raise other land, plant a biome seed (and on parked land, wear the Eye of the Beholder)."
                : string.Empty;
            if (!ZoneModePark.TryGetParked(zone, out var park)) return $"Zone ({zone.x},{zone.y}) rises as {seed.Land}.{restoresOnly}{cost}";
            if (!choosingLand || ZoneModePark.SeedMatches(seed, zone, park))
            {
                string why = seed.RestoresOnly ? restoresOnly
                    : ZoneModePark.SeedMatches(seed, zone, park) ? string.Empty
                    : $" This land is parked: wear the Eye of the Beholder to raise {seed.Land} here instead (this seed will return it as it was).";
                return $"Zone ({zone.x},{zone.y}) returns as it was"
                    + (park.Pieces > 0 ? $", with the {park.Pieces} pieces built there." : ".") + why + cost;
            }
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
            if (!plant || player == null) return;

            // SECOND ASK, ONLY WHEN SOMETHING IS ACTUALLY DESTROYED. The first popup describes the
            // trade; this one makes the destructive answer cost a separate, deliberate click, because
            // the two dialogs are otherwise identical in shape and a habitual Yes reaches both. It is
            // NOT shown for an ordinary planting - a confirmation that appears every time is one
            // nobody reads by the third chunk.
            if (WouldOverwriteParkedLand(seed, zone, choosingLand, out int lostPieces) && UnifiedPopup.IsAvailable())
            {
                s_confirming = true;
                InputBlock.Block(true);
                string what = lostPieces > 0
                    ? $"The {lostPieces} piece(s) built in zone ({zone.x},{zone.y}) will be REMOVED FROM THE WORLD."
                    : $"Whatever was built in zone ({zone.x},{zone.y}) will be REMOVED FROM THE WORLD.";
                // Says what is actually true today: the chunk is kept on disk as a .override file, so it is
                // recoverable in principle, but nothing restores it yet. Promising an undo that does not exist
                // would be worse than promising nothing.
                UnifiedPopup.Push(new YesNoPopup("Overwrite what is already there?",
                    $"{what}\n\nThis raises {seed.Land} over the original land. The old chunk is set aside on "
                    + "disk rather than deleted, but there is no way to put it back in-game yet - treat this as "
                    + "permanent.\n\nPlant anyway?",
                    () => AnswerOverwrite(true, seed, zone, choosingLand),
                    () => AnswerOverwrite(false, seed, zone, choosingLand), false));
                return;
            }

            Plant(player, seed, zone, choosingLand);
        }

        private static void AnswerOverwrite(bool plant, ChunkSeed seed, Vector2s zone, bool choosingLand)
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
            if (player != null)
            {
                if (opened && seed != null) UseOne(player, seed);
                Tell(player, message);
            }
            ChunkGeneratorApi.RaiseSeedResult(zone, opened, message);
        }

        // ChunkGeneratorApi.PlantSeed: the hammer's "Yes" without the popup, for a bot. Never an overwrite choice (choosingLand
        // false: a parked zone comes back as it was). Null when the request went out, else why not.
        internal static string PlantForBot(Player player, string seedPrefab, Vector2s zone)
        {
            if (player == null) return "No player.";
            if (!ZoneModeRegistry.ModeActive) return "Chunk seeds only grow in a one-zone-at-a-time world.";
            if (s_pending && Time.time - s_pendingSince < ReplyTimeoutSeconds) return "The land is still answering the last seed.";
            if (!ZoneModeSeeds.TryGet(seedPrefab, out var seed)) return $"'{seedPrefab}' is not a chunk seed.";
            if (FindSeed(player, seed.Prefab) == null) return $"No {seed.Name} in the inventory.";
            string refusal = Check(zone, player.transform.position, true);
            if (refusal != null) return refusal;
            Plant(player, seed, zone, false);
            return null;
        }

        // Closed zones that can open now (inside the world, beside open land), nearest first, for ChunkGeneratorApi.Frontier.
        // The open set is a bitset with no list, so this scans the square of zones around `from` with the same Check the
        // hammer uses (a few hundred bit reads).
        private const int FrontierScanZones = 12;

        internal static List<(Vector2s Zone, float Distance)> Frontier(Vector3 from, int max)
        {
            var found = new List<(Vector2s Zone, float Distance)>();
            Vector2s center = ZoneSystem.GetZone(from);
            for (int dx = -FrontierScanZones; dx <= FrontierScanZones; dx++)
                for (int dy = -FrontierScanZones; dy <= FrontierScanZones; dy++)
                {
                    var zone = new Vector2s(center.x + dx, center.y + dy);
                    if (Check(zone, from, false) == null) found.Add((zone, DistanceToZone(from, zone)));
                }
            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (max > 0 && found.Count > max) found.RemoveRange(max, found.Count - max);
            return found;
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
