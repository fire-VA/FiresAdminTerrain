using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Hugin's words for a chunk world, through vanilla's own tutorial system, so each is shown once per character and
    // the raven comes down for it: how the world works at the first spawn, the Chunk Hammer the first time it is held,
    // and the Chunk Generator the first time one is looked at.
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
        private const string ChestInStartCave =
            "The first hammer waits in a chest in the cave inside the hill nearby; the cave mouth faces the Stone Circle.";
        private const string ChestHidden =
            "The first hammer waits in a chest near the Stone Circle: in a cave beneath the land if it has one, otherwise "
            + "<color=orange>hidden above ground</color> among the trees and stones. Search close by.";

        private const string HammerTopic = "The Chunk Hammer";
        private const string HammerLabel = "Planting land";
        private const string HammerText =
            "That hammer builds nothing. It plants. Carry <color=orange>Chunk Seeds</color> as you would arrows, stand at "
            + "the edge of your land, look out over the closed sea and strike: the land the seed carries rises where you "
            + "aimed. A seed of another biome raises that biome, and a seed of a place raises the place itself. The chest "
            + "that held this hammer fills again tomorrow, and once you have held one you can craft another as you would "
            + "a hammer.";

        private const string GeneratorTopic = "The Chunk Generator";
        private const string GeneratorLabel = "Seeds from the land";
        private const string GeneratorText =
            "This is no smelter, whatever its shape. It burns nothing. Give it any two things from the list it shows, "
            + "<color=orange>fifty of each</color>, and it hands you a <color=orange>Chunk Seed</color> of its own biome; a "
            + "hundred each of the pair the land before it used will do too. The one beside the Stone Circle cannot be "
            + "broken, and it makes Meadows. Each biome has its own generator, and they wake one by one as the Forsaken fall.";

        private static Tutorial s_registeredOn;

        // The texts join vanilla's list once per Tutorial instance (it is rebuilt with the scene).
        internal static void EnsureTexts()
        {
            var tutorial = Tutorial.instance;
            if (tutorial == null || ReferenceEquals(s_registeredOn, tutorial)) return;
            s_registeredOn = tutorial;
            Set(tutorial, WorldKey, WorldTopic, WorldLabel, WorldText + ChestInStartCave);
            Set(tutorial, HammerKey, HammerTopic, HammerLabel, HammerText);
            Set(tutorial, GeneratorKey, GeneratorTopic, GeneratorLabel, GeneratorText);
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

        // The world's words say where the first hammer is: the start cave when the world has one, a hiding place otherwise.
        internal static void OnPlayerSpawned(Player player)
        {
            if (player == null || player != Player.m_localPlayer || !ZoneModeRegistry.ModeActive) return;
            EnsureTexts();
            if (Tutorial.instance != null)
                Set(Tutorial.instance, WorldKey, WorldTopic, WorldLabel,
                    WorldText + (ZoneModeStartCave.Current != null ? ChestInStartCave : ChestHidden));
            Show(player, WorldKey);
        }

        internal static void ShowHammer(Player player) => Show(player, HammerKey);

        internal static void ShowGenerator(Player player) => Show(player, GeneratorKey);

        private static void Show(Player player, string key)
        {
            if (player == null || player != Player.m_localPlayer || !ZoneModeRegistry.ModeActive) return;
            EnsureTexts();
            if (Tutorial.instance == null) return;
            player.ShowTutorial(key);
        }
    }
}
