using BepInEx.Configuration;

namespace VerdantsAscent.Terrain.Zones
{
    // Server-side settings for "one zone at a time" worlds. The terrain-shaping values are
    // delivered to clients inside the connection handshake with the zone state itself, so a
    // client always shapes closed zones exactly like its server, before its first heightmap.
    internal static class ZoneModeConfig
    {
        private const string Section = "Zone Mode";
        private const float DefaultCoastWidth = 1f;
        private const float MinCoastWidth = 1f;
        private const float MaxCoastWidth = 32f;
        private const float DefaultSeaFloorDepth = 50f;
        private const float MinSeaFloorDepth = 32f;
        private const float MaxSeaFloorDepth = 150f;
        private const int DefaultStartAreaRadius = 1;
        // 6 = a 13x13 start, 169 zones. Past that a new world pays for hundreds of populated zones before anyone moves.
        private const int MaxStartAreaRadius = 6;
        private const float DefaultRevealDistance = 400f;
        private const float MinRevealDistance = 64f;
        private const float MaxRevealDistance = 1500f;
        private const float DefaultRevealChance = 0.1f;
        private const string DefaultGuaranteedLocations =
            "Eikthyrnir, GDKing, Crypt2|Crypt3|Crypt4, Bonemass, SunkenCrypt4 x2, Dragonqueen, DrakeNest01 x3, "
            + "GoblinKing, GoblinCamp2 x2, Mistlands_DvergrBossEntrance1, Mistlands_DvergrTownEntrance1|Mistlands_DvergrTownEntrance2 x4, "
            + "Mistlands_Excavation1|Mistlands_Excavation2, FaderLocation, CharredFortress x3, LeviathanLava, DN_Bossroom";

        internal static ConfigEntry<bool> OfferAtWorldCreation;
        internal static ConfigEntry<bool> BuildOverClosedZones;
        internal static ConfigEntry<float> CoastWidth;
        internal static ConfigEntry<float> SeaFloorDepth;
        internal static ConfigEntry<int> StartAreaRadius;
        internal static ConfigEntry<bool> RevealLocations;
        internal static ConfigEntry<float> RevealDistance;
        internal static ConfigEntry<float> RevealChance;
        internal static ConfigEntry<string> RevealOptOut;
        internal static ConfigEntry<string> GuaranteedLocations;
        internal static ConfigEntry<bool> SummaryLogging;

        private static bool s_bound;

        internal static void Bind(ConfigFile cfg)
        {
            if (s_bound || cfg == null) return;
            s_bound = true;

            BuildOverClosedZones = cfg.Bind(Section, "Build over closed zones", true,
                "Let players build over a zone that is not open yet (piers and platforms down to the water). Such a piece is "
                + "taken down when its zone opens, and its materials go into a chest with the builder's name at the nearest "
                + "dry ground. Terrain tools are never allowed there. Off: building over a closed zone is refused. Live.");
            OfferAtWorldCreation = cfg.Bind(Section, "Offer at world creation", true,
                "Ask 'one zone at a time?' when a new world is created from the main menu. The answer is stored "
                + "with that world and cannot be changed later. Existing worlds are never affected.");
            CoastWidth = cfg.Bind(Section, "Coast width", DefaultCoastWidth,
                new ConfigDescription("Metres a closed zone takes to fall from an open zone's edge to the sea floor. "
                    + "1 = a sheer cut, the chunk look; larger values give a sloping shore inside the closed zone. "
                    + "Open zones are never reshaped. Applies from the next world load.",
                    new AcceptableValueRange<float>(MinCoastWidth, MaxCoastWidth)));
            SeaFloorDepth = cfg.Bind(Section, "Sea floor depth", DefaultSeaFloorDepth,
                new ConfigDescription("Depth in metres of the sea floor under closed zones, below the water surface. "
                    + "Kept deeper than any vanilla sea floor so opening a zone only ever raises the ground. "
                    + "Applies from the next world load.",
                    new AcceptableValueRange<float>(MinSeaFloorDepth, MaxSeaFloorDepth)));
            StartAreaRadius = cfg.Bind(Section, "Start area radius", DefaultStartAreaRadius,
                new ConfigDescription("Zones opened around the spawn zone when a new zone-mode world is first seeded, as a "
                    + "radius in zones: 1 = 3x3 (9 zones), 2 = 5x5 (25), 3 = 7x7 (49), 4 = 9x9 (81), 5 = 11x11 (121), "
                    + "6 = 13x13 (169). Only read once, at seeding.",
                    new AcceptableValueRange<int>(0, MaxStartAreaRadius)));
            RevealLocations = cfg.Bind(Section, "Location islands", true,
                "Some planned locations rise out of the sea as a single-zone island when a player comes within the "
                + "reveal distance. Server-side.");
            RevealChance = cfg.Bind(Section, "Location island chance", DefaultRevealChance,
                new ConfigDescription("Share of planned locations that become islands (0.1 = one in ten). Each location's "
                    + "roll is fixed by the world seed, so raising this adds islands and lowering it stops new ones; "
                    + "zones already opened stay open. Server-side, live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            RevealDistance = cfg.Bind(Section, "Location island reveal distance", DefaultRevealDistance,
                new ConfigDescription("Metres from a player at which a location that won its roll becomes an island. Its "
                    + "terrain shows on the distant view right away; vanilla adds trees within about 250 m and buildings "
                    + "within about 130 m. Server-side, live.",
                    new AcceptableValueRange<float>(MinRevealDistance, MaxRevealDistance)));
            RevealOptOut = cfg.Bind(Section, "Location island opt-out", "",
                "Comma-separated location prefab names that never become islands (for example Runestone_Meadows, "
                + "Mistlands_RoadPost1). They still spawn normally once their zone is opened. Server-side, live.");
            GuaranteedLocations = cfg.Bind(Section, "Progression locations", DefaultGuaranteedLocations,
                "Comma-separated locations progression depends on: the boss altars and the places their summon items, "
                + "Surtling Cores, Sealbreaker fragments and the like come from. 'A|B' counts either name; ' xN' asks for N "
                + "of them. Until enough stand in open zones, the nearest one a player comes within the reveal distance of "
                + "rises with its zone, whatever its island roll or the opt-out list say, and a biome swap never moves them. "
                + "Server-side, live.");
            SummaryLogging = cfg.Bind(Section, "Summary logging", false,
                "Write a [FiresZones] activity summary to the log once a minute while anything happened: terrain "
                + "samples gated, closed zones kept empty, refused edits, planning passes left ungated. Off by default "
                + "(a diagnostics aid for testing).");
        }
    }
}
