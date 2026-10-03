using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Hugin's words for a chunk world, through vanilla's own tutorial system, so each is shown once per character and
    // the raven comes down for it: how the world works at the first spawn, the Chunk Hammer the first time it is held,
    // the Chunk Generator the first time one is looked at, and where the hidden Cultivator lies once the first hammer is
    // found (ZoneModeStartPins, Tools\FAT_START_ITEMS.md).
    internal static class ZoneModeRavenHints
    {
        internal const string WorldKey = "fires_chunk_world";
        internal const string HammerKey = "fires_chunk_hammer";
        internal const string GeneratorKey = "fires_chunk_generator";

        private const string WorldTopic = "A world one piece at a time";
        private const string WorldLabel = "Chunk by chunk";
        private const string WorldText =
            "Look about you, little one. Your land ends where the sea begins, and here the sea is everything: this world "
            + "is opened <color=orange>one piece at a time</color>. Beside the Stone Circle stands a machine of the old ones, "
            + "the <color=orange>Chunk Generator</color>. Feed it what your land gives and it makes <color=orange>Chunk "
            + "Seeds</color>. Strike a seed into the sea with the <color=orange>Chunk Hammer</color> and the land it carries "
            + "rises, joined to yours. ";
        private const string ChestInTheOpen =
            "The first hammer waits in a <color=orange>chest out in the open, a few steps from where you stand</color>; your "
            + "map marks it, and the Chunk Generator too.";

        internal const string CultivatorKey = "fires_chunk_cultivator";
        private const string CultivatorTopic = "A buried gift";
        private const string CultivatorLabel = "The Cultivator";

        private const string HammerTopic = "The Chunk Hammer";
        private const string HammerLabel = "Planting land";
        private const string HammerText =
            "That hammer builds nothing. It plants. Carry <color=orange>Chunk Seeds</color> as you would arrows, stand at "
            + "the edge of your land, look out over the closed sea and strike: the land the seed carries rises where you "
            + "aimed. A plain Chunk Seed only ever brings back the land the world made there. A seed of a biome raises that "
            + "biome, whatever was meant to be there, and a seed of a place raises the place itself. The chest "
            + "that held this hammer fills again tomorrow, and once you have held one you can craft another as you would "
            + "a hammer.";

        private const string GeneratorTopic = "The Chunk Generator";
        private const string GeneratorLabel = "Seeds from the land";
        private const string GeneratorText =
            "This is no smelter, whatever its shape. Feed it at its <color=orange>door</color> with what your land gives, and "
            + "choose at its <color=orange>hopper</color> what it makes. A full load of a hundred gives two plain "
            + "<color=orange>Chunk Seeds</color>, and those only ever bring back the land the world made there: nothing you "
            + "raise with them can change your world by accident. As the Forsaken fall it learns the seeds of the biomes, "
            + "the Meadows and the Black Forest first, and a seed of a biome raises that biome anywhere. The one beside the "
            + "Stone Circle cannot be broken.";

        internal const string LuckyKey = "fires_chunk_lucky";
        private const string LuckyTopic = "A seed of a place";
        private const string LuckyLabel = "A lucky seed";
        private const string LuckyText =
            "That is no plain seed the generator gave you, little one: it carries a whole <color=orange>place</color>. Such "
            + "seeds are forged, not grown: two biome seeds at a <color=orange>forge</color>, and the pair decides the place. "
            + "Yet the generator remembers the lands its fuel came from. Burn the riches of a land beyond the one you ask "
            + "for, and once in a long while it will hand you one of these as well. Do not count on it.";

        private static Tutorial s_registeredOn;

        // The texts join vanilla's list once per Tutorial instance (it is rebuilt with the scene).
        internal static void EnsureTexts()
        {
            var tutorial = Tutorial.instance;
            if (tutorial == null || ReferenceEquals(s_registeredOn, tutorial)) return;
            s_registeredOn = tutorial;
            Set(tutorial, WorldKey, WorldTopic, WorldLabel, WorldText + ChestInTheOpen);
            Set(tutorial, HammerKey, HammerTopic, HammerLabel, HammerText);
            Set(tutorial, GeneratorKey, GeneratorTopic, GeneratorLabel, GeneratorText);
            Set(tutorial, LuckyKey, LuckyTopic, LuckyLabel, LuckyText);
        }

        private static void Set(Tutorial tutorial, string key, string topic, string label, string text)
        {
            var existing = tutorial.m_texts.Find(t => t.m_name == key);
            if (existing != null)
            {
                existing.m_text = text;
                return;
            }
            tutorial.m_texts.Add(new Tutorial.TutorialText { m_name = key, m_topic = topic, m_label = label, m_text = text });
        }

        // A generator is a smelter clone and inherits the smelter's raven perch: it speaks of the generator instead.
        internal static void RetargetGuidePoint(GameObject machine)
        {
            var guide = machine != null ? machine.GetComponentInChildren<GuidePoint>(true) : null;
            if (guide == null || guide.m_text == null) return;
            guide.m_text.m_key = GeneratorKey;
            guide.m_text.m_topic = GeneratorTopic;
            guide.m_text.m_label = GeneratorLabel;
            guide.m_text.m_text = GeneratorText;
            guide.m_text.m_munin = false;
        }

        internal static void OnPlayerSpawned(Player player)
        {
            if (player == null || player != Player.m_localPlayer || !ZoneModeRegistry.ModeActive) return;
            Show(player, WorldKey);
        }

        // Holding the first hammer is finding it, so the Cultivator's hint follows the hammer's own.
        internal static void ShowHammer(Player player)
        {
            Show(player, HammerKey);
            ZoneModeStartPins.OfferCultivatorHint(player);
        }

        // The Cultivator's words carry its direction and distance from where the player stands, so they are written
        // afresh each time they are offered; vanilla shows them once per character.
        internal static void ShowCultivator(Player player, string text)
        {
            if (player == null || player != Player.m_localPlayer || !ZoneModeRegistry.ModeActive || player.HaveSeenTutorial(CultivatorKey)) return;
            EnsureTexts();
            if (Tutorial.instance == null) return;
            Set(Tutorial.instance, CultivatorKey, CultivatorTopic, CultivatorLabel, text);
            player.ShowTutorial(CultivatorKey);
        }

        internal static void ShowGenerator(Player player) => Show(player, GeneratorKey);

        // Only ever after a generator has dropped a location seed nearby (ZoneModeChunkGenerator), so the bonus stays a
        // surprise the first time.
        internal static void ShowLucky(Player player) => Show(player, LuckyKey);

        private static void Show(Player player, string key)
        {
            if (player == null || player != Player.m_localPlayer || !ZoneModeRegistry.ModeActive) return;
            EnsureTexts();
            if (Tutorial.instance == null) return;
            player.ShowTutorial(key);
        }
    }
}
