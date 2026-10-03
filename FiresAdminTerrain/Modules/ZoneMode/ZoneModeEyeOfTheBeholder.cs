using FiresCore.UI;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VerdantsAscent.Terrain.Zones
{
    // The Eye of the Beholder: worn while the Chunk Hammer is aimed at a closed zone with a seed to plant, it names the
    // land the world planned there, top centre, so a world can be rebuilt faithfully or scrambled on purpose. Over a
    // parked zone of a converted world it also says what stood there, and wearing it is what lets a seed that does not
    // match raise other land in its place (ZoneModePark).
    internal static class ZoneModeEyeOfTheBeholder
    {
        internal const string HelmetPrefab = "FiresEyeOfTheBeholder";
        internal const string HelmetName = "The Eye of the Beholder";
        private const float TickSeconds = 0.1f;

        // Readable on Valheim's parchment and against sky and sea, and distinguishable for the common
        // red-green colour blindness because the SENTENCE below still says which it is - the colour is
        // a second channel on a message that already reads in words, never the only one.
        private const string RightColour = "#7CD96B";     // this seed restores or matches the land
        private const string WrongColour = "#FF5A4D";     // planting overwrites parked land for good
        private const string NeutralColour = "#F0C36B";   // a choice, but nothing is lost

        private static TextMeshProUGUI s_label;
        private static float s_nextTick;
        private static Vector2s s_zone;
        private static string s_seed;
        private static bool s_shown;

        internal static void ClientTick()
        {
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + TickSeconds;
            var player = Player.m_localPlayer;
            if (player == null || !IsWorn(player) || !ZoneModeChunkSpawner.TryAimedSeed(player, out var seed)
                || !ZoneModeCommands.TryGetLookedAtZone(out var zone) || ZoneModeRegistry.IsOpen(zone))
            {
                Hide();
                return;
            }
            if (s_shown && zone.x == s_zone.x && zone.y == s_zone.y && seed.Prefab == s_seed) return;
            if (!EnsureLabel()) return;
            s_zone = zone;
            s_seed = seed.Prefab;
            ZoneModeChunkTiers.ZoneTier(zone, out var planned);
            string meant = planned == Heightmap.Biome.Ocean ? "open sea" : BiomeTitle(planned);

            // RED MEANS YOU ARE ABOUT TO LOSE SOMETHING. The biome name is the one word being read at
            // a glance while aiming, so it carries the warning rather than the sentence below it.
            // Red is reserved for an actual overwrite of parked land - if it also went red for merely
            // raising a different biome on empty sea it would be red most of the time and mean nothing.
            bool overwrites = ZoneModeChunkSpawner.WouldOverwriteParkedLand(seed, zone, true, out _);
            bool right = ZoneModeChunkSpawner.SeedMatchesIntent(seed, zone, planned);
            string colour = overwrites ? WrongColour : right ? RightColour : NeutralColour;

            s_label.text = $"<size=30>Meant to be: <color={colour}>{meant}</color></size>"
                + $"\n<size=19>{Outcome(seed, zone, planned)}</size>";
            s_label.gameObject.SetActive(true);
            s_shown = true;
        }

        private static string Outcome(ChunkSeed seed, Vector2s zone, Heightmap.Biome planned)
        {
            if (ZoneModePark.TryGetParked(zone, out var park))
            {
                string stood = park.Pieces > 0 ? $"{park.Pieces} built pieces stood here." : "The old land waits here.";
                return ZoneModePark.SeedMatches(seed, zone, park)
                    ? $"{stood} Your {seed.Name} brings it back as it was."
                    : $"{stood} Your {seed.Name} raises {seed.Land} instead, and what stood here is lost.";
            }
            if (seed.IsLocation) return $"Your {seed.Name} raises land holding {seed.Title} here.";
            return seed.RestoresOnly || planned == seed.Tier.Biome
                ? $"Your {seed.Name} raises it as it was meant to be."
                : $"Your {seed.Name} raises {seed.Tier.Title} land here instead.";
        }

        internal static bool IsWorn(Player player)
        {
            var helmet = player.m_helmetItem;
            if (helmet == null) return false;
            return (helmet.m_dropPrefab != null && helmet.m_dropPrefab.name == HelmetPrefab) || helmet.m_shared?.m_name == HelmetName;
        }

        internal static string BiomeTitle(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.BlackForest: return "Black Forest";
                case Heightmap.Biome.AshLands: return "Ashlands";
                case Heightmap.Biome.DeepNorth: return "Deep North";
                default: return biome.ToString();
            }
        }

        private static bool EnsureLabel()
        {
            if (s_label != null) return true;
            var hud = Hud.instance;
            if (hud == null || hud.m_rootObject == null) return false;
            s_label = UIBuilderHelper.CreateLabel(hud.m_rootObject.transform, "FiresEyeOfTheBeholder", "", 30f,
                new Color(1f, 0.93f, 0.78f), new Vector2(0.25f, 0.86f), new Vector2(0.75f, 0.96f));
            s_label.raycastTarget = false;
            s_label.gameObject.SetActive(false);
            return true;
        }

        private static void Hide()
        {
            if (!s_shown) return;
            s_shown = false;
            s_seed = null;
            if (s_label != null) s_label.gameObject.SetActive(false);
        }

        internal static void Reset()
        {
            s_shown = false;
            s_seed = null;
            if (s_label != null) Object.Destroy(s_label.gameObject);
            s_label = null;
        }
    }
}
