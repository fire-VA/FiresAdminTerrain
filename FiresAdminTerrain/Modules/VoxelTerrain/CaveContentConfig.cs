using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // What the server dresses caves with, per biome, and how much of it. Lists are prefab names with an optional
    // ":weight" (default 1). Unknown names are skipped with one warning, so lists may name prefabs a modlist lacks.
    // Read at use time, so a hot-reloaded cfg edit applies to the next zone dressed.
    internal static class CaveContentConfig
    {
        internal sealed class BiomeTable
        {
            public WeightedPrefabs Floor, Ceiling, Wall, Rubble, Props, Ores, Chests, Spawners;
            public WeightedPrefabs Dark;   // 0.2.829: mushrooms for dark floors
        }

        // 0.2.829 (Fire 10-07: "the darker areas, or longer tunnels deeper and further from the light, get the mushroom placements";
        // "cave mushrooms should be pickable and respawn over time"): each biome's own vanilla mushroom pickables.
        private static readonly Dictionary<string, string> DarkDefaults = new Dictionary<string, string>
        {
            { "Meadows", "Pickable_Mushroom" },
            { "Black Forest", "Pickable_Mushroom_yellow:2,Pickable_Mushroom_blue:1,Pickable_Mushroom:1" },
            { "Swamp", "Pickable_Mushroom_yellow" },
            { "Mountain", "Pickable_Mushroom_blue" },
            { "Plains", "Pickable_Mushroom" },
            { "Mistlands", "Pickable_Mushroom_JotunPuffs:2,Pickable_Mushroom_Magecap:1" },
            { "Ashlands", "" },
            { "Deep North", "Pickable_Mushroom_blue" },
        };

        private static ConfigEntry<float> s_mushroomChance;
        private static ConfigEntry<int> s_mushroomsPerZone;
        internal static float MushroomChance => s_mushroomChance.Value;
        internal static int MushroomsPerZone => s_mushroomsPerZone.Value;

        internal const float FallbackDayLengthSeconds = 1200f;
        internal const float ChestIdealHeadroom = 6f;
        internal const float PoolSpacing = 24f;
        internal const float PoolSourceLift = 0.6f;

        private const string Section = "VoxelTerrain Cave Content";
        private const string BiomeSectionPrefix = "VoxelTerrain Cave Content ";

        private static ConfigEntry<float> s_floorChance, s_ceilingChance, s_wallChance, s_wallScaleMin, s_wallScaleMax,
            s_rubbleChance, s_propChance, s_oreChance;
        private static ConfigEntry<int> s_oresPerZone;
        private static ConfigEntry<bool> s_chestsEnabled;
        private static ConfigEntry<float> s_chestChance, s_restockMinDays, s_restockMaxDays, s_restockClearance;
        private static ConfigEntry<bool> s_pickablesRestock;
        private static ConfigEntry<float> s_pickableRestockMinDays, s_pickableRestockMaxDays;
        private static ConfigEntry<bool> s_monstersEnabled, s_monstersRespawn;
        private static ConfigEntry<float> s_monsterRespawnMinDays, s_monsterRespawnMaxDays;
        private static ConfigEntry<float> s_spawnerZoneChance, s_spawnerSpacing;
        private static ConfigEntry<int> s_spawnersPerZone;
        private static readonly Dictionary<Heightmap.Biome, BiomeTable> s_tables = new Dictionary<Heightmap.Biome, BiomeTable>();

        internal static bool Enabled => FiresVoxelConfig.CaveContentEnabled != null && FiresVoxelConfig.CaveContentEnabled.Value;
        internal static float Density => FiresVoxelConfig.CaveContentDensity != null ? FiresVoxelConfig.CaveContentDensity.Value : 1f;
        internal static float RubbleScale => FiresVoxelConfig.CaveRockScale != null ? FiresVoxelConfig.CaveRockScale.Value : 0.45f;
        internal static bool PoolsEnabled => FiresVoxelConfig.CaveWaterEnabled != null && FiresVoxelConfig.CaveWaterEnabled.Value;
        internal static float PoolChance => FiresVoxelConfig.CaveWaterChance != null ? FiresVoxelConfig.CaveWaterChance.Value : 0.35f;
        internal static float PoolMinHeadroom => FiresVoxelConfig.CaveWaterMinHeadroom != null ? FiresVoxelConfig.CaveWaterMinHeadroom.Value : 5.5f;
        internal static float PoolRadius => FiresVoxelConfig.CaveWaterRadius != null ? FiresVoxelConfig.CaveWaterRadius.Value : 8f;

        internal static float FloorChance => s_floorChance.Value;
        internal static float CeilingChance => s_ceilingChance.Value;
        internal static float WallChance => s_wallChance.Value;
        internal static float WallScaleMin => s_wallScaleMin.Value;
        internal static float WallScaleMax => Mathf.Max(s_wallScaleMin.Value, s_wallScaleMax.Value);
        internal static float RubbleChance => s_rubbleChance.Value;
        internal static float PropChance => s_propChance.Value;
        internal static float OreChance => s_oreChance.Value;
        internal static int OresPerZone => s_oresPerZone.Value;
        internal static bool ChestsEnabled => s_chestsEnabled.Value;
        internal static float ChestChance => s_chestChance.Value;
        internal static float RestockMinDays => s_restockMinDays.Value;
        internal static float RestockMaxDays => s_restockMaxDays.Value;
        internal static float RestockClearance => s_restockClearance.Value;
        internal static bool PickablesRestock => s_pickablesRestock.Value;
        internal static float PickableRestockMinDays => s_pickableRestockMinDays.Value;
        internal static float PickableRestockMaxDays => s_pickableRestockMaxDays.Value;
        internal static bool MonstersEnabled => s_monstersEnabled.Value;
        internal static bool MonstersRespawn => s_monstersRespawn.Value;
        internal static float MonsterRespawnMinDays => s_monsterRespawnMinDays.Value;
        internal static float MonsterRespawnMaxDays => s_monsterRespawnMaxDays.Value;
        internal static float SpawnerZoneChance => s_spawnerZoneChance.Value;
        internal static int SpawnersPerZone => s_spawnersPerZone.Value;
        internal static float SpawnerSpacing => s_spawnerSpacing.Value;

        internal static BiomeTable For(Heightmap.Biome biome) => s_tables.TryGetValue(biome, out var table) ? table : null;

        // Every biome's table (caves_test reads the Ceiling lists).
        internal static IEnumerable<KeyValuePair<Heightmap.Biome, BiomeTable>> AllTables => s_tables;

        internal static void Bind(ConfigFile cfg)
        {
            VoxelSurfaceSnap.Bind(cfg);
            VoxelCaveClutter.Bind(cfg);
            CaveLight.Bind(cfg);   // 0.2.828: cave grass by light
            s_floorChance = cfg.Bind(Section, "Floor pickable chance", 0.10f, new ConfigDescription(
                "Chance per cave floor spot (one every ~4 m) of a floor pickable or prop from the biome's Floor list. Small ones come in clumps of 1-3.",
                new AcceptableValueRange<float>(0f, 1f)));
            s_ceilingChance = cfg.Bind(Section, "Ceiling hanger chance", 0.22f, new ConfigDescription(
                "Chance per cave floor spot of a hanger from the biome's Ceiling list, mounted by its attach face on the rock overhead and shortened so a player still fits underneath (skipped if it cannot be).",
                new AcceptableValueRange<float>(0f, 1f)));
            s_wallChance = cfg.Bind(Section, "Wall piece chance", 0.14f, new ConfigDescription(
                "Chance per cave floor spot of a piece from the biome's Wall list laid on the nearest roofed rock wall by its attach face: roots, ice spikes, creep. It never reaches more than part of the way across the passage.",
                new AcceptableValueRange<float>(0f, 1f)));
            s_wallScaleMin = cfg.Bind(Section, "Wall piece scale min", 0.35f, new ConfigDescription(
                "Smallest scale a wall piece is drawn at (prefabs that sync their scale).", new AcceptableValueRange<float>(0.1f, 2f)));
            s_wallScaleMax = cfg.Bind(Section, "Wall piece scale max", 0.7f, new ConfigDescription(
                "Largest scale a wall piece is drawn at (prefabs that sync their scale).", new AcceptableValueRange<float>(0.1f, 2f)));
            s_rubbleChance = cfg.Bind(Section, "Rubble chance", 0.10f, new ConfigDescription(
                "Chance per cavern floor spot (5.5 m of headroom or more) of a rock from the biome's Rubble list, shrunk by 'Cave Rock Scale'.",
                new AcceptableValueRange<float>(0f, 1f)));
            s_propChance = cfg.Bind(Section, "Floor prop chance", 0.06f, new ConfigDescription(
                "Chance per cave floor spot of a prop from the biome's Props list (skulls, bones, ice spikes, debris) at its own size, where its footprint fits.",
                new AcceptableValueRange<float>(0f, 1f)));
            s_oreChance = cfg.Bind(Section, "Ore node chance", 0.08f, new ConfigDescription(
                "Chance per cave floor spot of an ore node from the biome's Ore list. A node is only placed where its whole footprint fits: headroom, a flat floor and room on every side.",
                new AcceptableValueRange<float>(0f, 1f)));
            s_oresPerZone = cfg.Bind(Section, "Ore nodes per zone", 3, new ConfigDescription(
                "Most ore nodes one 64 m zone's caves may hold.", new AcceptableValueRange<int>(0, 32)));
            s_chestsEnabled = cfg.Bind(Section, "Chests", true,
                "Place vanilla treasure chests in caverns, filled from the chest's own loot table like a location chest, and restock them after they are looted.");
            s_chestChance = cfg.Bind(Section, "Chest chance per zone", 0.35f, new ConfigDescription(
                "Chance that a zone whose caves have room for one gets a chest (at most one per zone).",
                new AcceptableValueRange<float>(0f, 1f)));
            s_restockMinDays = cfg.Bind(Section, "Chest restock min days", 3f, new ConfigDescription(
                "Fewest in-game days before a looted chest restocks. Each chest rolls its own wait between min and max every time it is looted.",
                new AcceptableValueRange<float>(0.1f, 100f)));
            s_restockMaxDays = cfg.Bind(Section, "Chest restock max days", 10f, new ConfigDescription(
                "Most in-game days before a looted chest restocks.", new AcceptableValueRange<float>(0.1f, 100f)));
            s_restockClearance = cfg.Bind(Section, "Chest restock clearance", 200f, new ConfigDescription(
                "A chest, pickable or spawner only restocks while every player is at least this far away (metres), and never closer than the ring the game keeps loaded around a player.",
                new AcceptableValueRange<float>(0f, 2000f)));
            s_pickablesRestock = cfg.Bind(Section, "Pickables restock", true,
                "Cave pickables that never regrow in vanilla (stones, bone piles, crystals, random treasure finds, core stands and hangers) "
                + "come back at their spot after their own roll of in-game days, counted while no player is near (the chest rule). "
                + "Mushrooms, flint, glow worms and the rest regrow by themselves.");
            s_pickableRestockMinDays = cfg.Bind(Section, "Pickable restock min days", 3f, new ConfigDescription(
                "Fewest in-game days before a picked cave pickable comes back.", new AcceptableValueRange<float>(0.1f, 100f)));
            s_pickableRestockMaxDays = cfg.Bind(Section, "Pickable restock max days", 10f, new ConfigDescription(
                "Most in-game days before a picked cave pickable comes back.", new AcceptableValueRange<float>(0.1f, 100f)));
            s_monstersEnabled = cfg.Bind(Section, "Monsters", true,
                "Place vanilla dungeon spawners in caverns large enough for the creature each one spawns. They fire for players who are underground nearby.");
            s_monstersRespawn = cfg.Bind(Section, "Monsters respawn", true,
                "Vanilla dungeon spawners fire once. A cave spawner whose creature has died re-arms after its own roll of in-game days, counted while no player is near (the same rule as chests).");
            s_monsterRespawnMinDays = cfg.Bind(Section, "Monster respawn min days", 3f, new ConfigDescription(
                "Fewest in-game days before a cave spawner re-arms.", new AcceptableValueRange<float>(0.1f, 100f)));
            s_monsterRespawnMaxDays = cfg.Bind(Section, "Monster respawn max days", 10f, new ConfigDescription(
                "Most in-game days before a cave spawner re-arms.", new AcceptableValueRange<float>(0.1f, 100f)));
            s_spawnerZoneChance = cfg.Bind(Section, "Monster spawner chance per zone", 0.6f, new ConfigDescription(
                "Chance that a zone's caves get any spawners at all.", new AcceptableValueRange<float>(0f, 1f)));
            s_spawnersPerZone = cfg.Bind(Section, "Monster spawners per zone", 2, new ConfigDescription(
                "Most spawners one zone's caves may hold.", new AcceptableValueRange<int>(0, 16)));
            s_spawnerSpacing = cfg.Bind(Section, "Monster spawner spacing", 24f, new ConfigDescription(
                "Least distance between two spawners in the same zone (metres).", new AcceptableValueRange<float>(4f, 64f)));

            s_mushroomChance = cfg.Bind(Section, "Dark mushroom chance", 0.12f, new ConfigDescription(
                "Chance per cave floor spot of a clump of the biome's 'Dark floor' mushrooms, times (1 - light)^2: none near cave mouths, "
                + "sun shafts and sinkholes, the most deep in long tunnels. Mushrooms on the biome's Floor list only come this way.",
                new AcceptableValueRange<float>(0f, 1f)));
            s_mushroomsPerZone = cfg.Bind(Section, "Dark mushrooms per zone", 6, new ConfigDescription(
                "Most dark-floor mushroom clumps one zone gets.", new AcceptableValueRange<int>(0, 64)));

            foreach (var defaults in DefaultTables)
            {
                string section = BiomeSectionPrefix + defaults.Key;
                var ceiling = cfg.Bind(section, "Ceiling", defaults.Ceiling,
                    "Things hanging from cave ceilings, as prefab:weight (the weights are this biome's mix). Each goes on the rock by the "
                    + "face 'VoxelTerrain Surface Mounting / Attach sides' names for it (its bottom unless listed), and one longer than the "
                    + "cave is tall is scaled down to fit when the prefab syncs its scale (a giant Yggdrasil root becomes a mini one).");
                // 0.2.720 (Fire 10-05: "a couple different kinds of roots"): a list still at its pre-0.2.720 default takes the wider
                // pool; one anybody edited is left as it is.
                if (OldCeilingDefaults.TryGetValue(defaults.Key, out string oldCeiling) && ceiling.Value == oldCeiling && oldCeiling != defaults.Ceiling)
                {
                    ceiling.Value = defaults.Ceiling;
                    FiresLogger.LogInfo($"[FiresVoxel] '{section} / Ceiling' was the old default: now {defaults.Ceiling}.");
                }
                s_tables[defaults.Biome] = new BiomeTable
                {
                    Floor = new WeightedPrefabs(cfg.Bind(section, "Floor", defaults.Floor,
                        "Pickables and small props on cave floors.")),
                    Ceiling = new WeightedPrefabs(ceiling),
                    Wall = new WeightedPrefabs(cfg.Bind(section, "Wall", defaults.Wall,
                        "Pieces laid on cave walls (roots, ice spikes, creep) by their attach face.")),
                    Rubble = new WeightedPrefabs(cfg.Bind(section, "Rubble", defaults.Rubble,
                        "Rocks for cavern floors, shrunk by 'Cave Rock Scale' when the prefab syncs its scale.")),
                    Props = new WeightedPrefabs(cfg.Bind(section, "Props", defaults.Props,
                        "Floor props at their own size (skulls, bones, ice spikes, debris), placed only where their footprint fits.")),
                    Ores = new WeightedPrefabs(cfg.Bind(section, "Ore", defaults.Ores,
                        "Mineable nodes, placed only where their whole footprint fits.")),
                    Chests = new WeightedPrefabs(cfg.Bind(section, "Chests", defaults.Chests,
                        "Treasure chest prefabs; the chest's own Container loot table fills it.")),
                    Spawners = new WeightedPrefabs(cfg.Bind(section, "Monster spawners", defaults.Spawners,
                        "Vanilla creature spawner prefabs. A spawner is placed only where its creature fits.")),
                    Dark = new WeightedPrefabs(cfg.Bind(section, "Dark floor", DarkDefaults.TryGetValue(defaults.Key, out var dark) ? dark : "",
                        "Pickables for dark cave floors (mushrooms), weighted toward the darkest tunnels ('Dark mushroom chance').")),
                };
            }
        }

        private sealed class TableDefaults
        {
            public Heightmap.Biome Biome;
            public string Key, Floor, Ceiling, Wall, Rubble, Props, Ores, Chests, Spawners;
        }

        private static readonly Dictionary<string, string> OldCeilingDefaults = new Dictionary<string, string>
        {
            { "Meadows", "root07:2,root11:1" },
            { "Black Forest", "root07:2,root11:1,Greydwarf_Root:0.3" },
            { "Swamp", "root07:2,root11:1" },
            { "Mistlands", "CreepProp_hanging01:2,CreepProp_egg_hanging01:1,CreepProp_egg_hanging02:1" },
        };

        private static readonly TableDefaults[] DefaultTables =
        {
            new TableDefaults
            {
                Biome = Heightmap.Biome.Meadows, Key = "Meadows",
                Floor = "Pickable_Mushroom:4,Pickable_Stone:3,Pickable_Flint:1,Pickable_ForestCryptRemains01:1,Pickable_ForestCryptRemains02:1",
                Ceiling = "root07:2,root11:1,root08:1,root12:1,YggdrasilRoot:0.7",
                Wall = "root08:1,root12:1",
                Rubble = "Rock_4:2,Rock_3:1",
                Props = "Skull1:1,LargeBone_half01:0.5",
                Ores = "",
                Chests = "TreasureChest_meadows",
                Spawners = "Spawner_Skeleton_Meadows",
            },
            new TableDefaults
            {
                Biome = Heightmap.Biome.BlackForest, Key = "Black Forest",
                Floor = "Pickable_Mushroom_yellow:4,Pickable_Mushroom_blue:1,Pickable_ForestCryptRemains01:2,Pickable_ForestCryptRemains02:2,Pickable_ForestCryptRemains03:1,Pickable_ForestCryptRemains04:1,Pickable_ForestCryptRandom:1,Pickable_Stone:2,Pickable_SurtlingCoreStand:0.5",
                Ceiling = "root07:2,root11:1,root08:1,root12:1,Greydwarf_Root:0.6,YggdrasilRoot:0.7",
                Wall = "root08:1,root12:1",
                Rubble = "Rock_3:2,Rock_4:1",
                Props = "Skull1:1,LargeBone_half01:0.5,LargeBone_half02:0.5",
                Ores = "MineRock_Tin:3,rock4_copper:1",
                Chests = "TreasureChest_forestcrypt:3,TreasureChest_blackforest:1,TreasureChest_trollcave:1",
                Spawners = "Spawner_Skeleton:4,Spawner_Skeleton_poison:1,Spawner_Ghost:1,Spawner_Troll:0.3",
            },
            new TableDefaults
            {
                Biome = Heightmap.Biome.Swamp, Key = "Swamp",
                Floor = "Pickable_Mushroom_yellow:3,Pickable_SunkenCryptRandom:2,Pickable_ForestCryptRemains01:1,Pickable_ForestCryptRemains02:1,Pickable_Stone:1",
                Ceiling = "root07:2,root11:1,root08:1,root12:1,Greydwarf_Root:0.3,YggdrasilRoot:0.7",
                Wall = "root08:1,root12:1",
                Rubble = "Rock_4",
                Props = "LargeBone_half01:1,LargeBone_half02:1,Skull1:1",
                Ores = "mudpile:3,mudpile2:1",
                Chests = "TreasureChest_sunkencrypt:3,TreasureChest_swamp:1",
                Spawners = "Spawner_Draugr:4,Spawner_Draugr_Elite:1,Spawner_Draugr_Ranged:1,Spawner_Skeleton_Swamp:2,Spawner_Blob:2,Spawner_Wraith:1",
            },
            new TableDefaults
            {
                Biome = Heightmap.Biome.Mountain, Key = "Mountain",
                Floor = "Pickable_MountainCaveCrystal:2,Pickable_MountainCaveObsidian:2,Pickable_MountainCaveRandom:1,Pickable_Obsidian:1",
                Ceiling = "caverock_ice_stalagtite:4,hanging_hairstrands:1",
                Wall = "caverock_ice_stalagmite",
                Rubble = "Rock_3",
                Props = "Skull1:0.5",
                Ores = "MineRock_Obsidian:3,silvervein:2",
                Chests = "TreasureChest_mountaincave:3,TreasureChest_mountains:1",
                Spawners = "Spawner_Bat:3,Spawner_Ulv:2,Spawner_Cultist:1,Spawner_StoneGolem:0.3",
            },
            new TableDefaults
            {
                Biome = Heightmap.Biome.Plains, Key = "Plains",
                Floor = "Pickable_Stone:2,Pickable_ForestCryptRemains01:1,Pickable_Tar:1",
                Ceiling = "",
                Wall = "",
                Rubble = "Rock_4",
                Props = "LargeBone_half01:1,LargeBone_half02:1,goblin_trashpile:0.3,Skull1:0.5",
                Ores = "",
                Chests = "TreasureChest_heath:2,TreasureChest_plains_stone:1",
                Spawners = "Spawner_Goblin:3,Spawner_GoblinArcher:2,Spawner_GoblinShaman:1,Spawner_GoblinBrute:0.5",
            },
            new TableDefaults
            {
                Biome = Heightmap.Biome.Mistlands, Key = "Mistlands",
                Floor = "Pickable_RoyalJelly:2,Pickable_DvergrMineTreasure:1,Pickable_Mushroom_Magecap:1,Pickable_BlackCoreStand:0.3",
                Ceiling = "CreepProp_hanging01:2,CreepProp_egg_hanging01:1,CreepProp_egg_hanging02:1,YggdrasilRoot:1.5",
                Wall = "CreepProp_wall01",
                Rubble = "",
                Props = "LargeBone_half02:0.5",
                Ores = "",
                Chests = "TreasureChest_dvergrtown:2,TreasureChest_dvergr_loose_stone:1",
                Spawners = "Spawner_Seeker:3,Spawner_Tick:2,Spawner_SeekerBrute:1",
            },
            new TableDefaults
            {
                Biome = Heightmap.Biome.AshLands, Key = "Ashlands",
                Floor = "Pickable_Charredskull:2,Pickable_Pot_Shard:2,Pickable_Ashstone:2,Pickable_SulfurRock:2,Pickable_SmokePuff:1,Pickable_MoltenCoreStand:0.2",
                Ceiling = "",
                Wall = "",
                Rubble = "",
                Props = "",
                Ores = "UnstableLavaRock",
                Chests = "TreasureChest_ashland_stone:2,TreasureChest_charredfortress:1",
                Spawners = "Spawner_Twitcher:3,Spawner_Charred:2,Spawner_Charred_Archer:1,Spawner_Charred_Mage:0.5,Spawner_Morgen:0.3",
            },
            new TableDefaults
            {
                Biome = Heightmap.Biome.DeepNorth, Key = "Deep North",
                Floor = "Pickable_GlowWorm:2,Pickable_MorkHallaTreasure:0.5",
                Ceiling = "caverock_ice_stalagtite:4,Pickable_FrostCoreHanger:1",
                Wall = "caverock_ice_stalagmite",
                Rubble = "Rock_3_deepnorth",
                Props = "FrozenSkeleton_Pose1:0.5,FrozenSkeleton_Pose2:0.5,Morkhalla_Rubble1:0.3",
                Ores = "",
                Chests = "TreasureChest_morkhalla:1,TreasureChest_deepnorth_village:1",
                Spawners = "Spawner_GoblinDeepNorth:3,Spawner_JotunWarrior:1,Spawner_JotunWitch:1,Spawner_DvergerDeepNorth:0.5,Spawner_ShadowPerson:0.5,Spawner_Frysling:1",
            },
        };
    }

    // A config list of prefab names with optional ":weight", resolved against ZNetScene on first use after each edit.
    internal sealed class WeightedPrefabs
    {
        private readonly ConfigEntry<string> m_entry;
        private readonly List<GameObject> m_prefabs = new List<GameObject>();
        private readonly List<float> m_weights = new List<float>();
        private static readonly HashSet<string> s_warned = new HashSet<string>();
        private string m_resolvedFrom;

        internal WeightedPrefabs(ConfigEntry<string> entry) { m_entry = entry; }

        internal bool TryPick(System.Random rng, out GameObject prefab) => TryPick(rng, null, out prefab);

        // The resolved prefabs and their weights (prefabs the game lacks are already left out).
        internal List<KeyValuePair<GameObject, float>> Entries()
        {
            Resolve();
            var list = new List<KeyValuePair<GameObject, float>>();
            for (int i = 0; i < m_prefabs.Count; i++) list.Add(new KeyValuePair<GameObject, float>(m_prefabs[i], m_weights[i]));
            return list;
        }

        internal bool TryPick(System.Random rng, Func<GameObject, bool> accept, out GameObject prefab)
        {
            prefab = null;
            Resolve();
            float total = 0f;
            for (int i = 0; i < m_prefabs.Count; i++)
                if (accept == null || accept(m_prefabs[i])) total += m_weights[i];
            if (total <= 0f) return false;
            float roll = (float)rng.NextDouble() * total;
            for (int i = 0; i < m_prefabs.Count; i++)
            {
                if (accept != null && !accept(m_prefabs[i])) continue;
                roll -= m_weights[i];
                if (roll > 0f) continue;
                prefab = m_prefabs[i];
                return true;
            }
            return false;
        }

        private void Resolve()
        {
            string csv = m_entry.Value ?? "";
            if (csv == m_resolvedFrom || ZNetScene.instance == null) return;
            m_resolvedFrom = csv;
            m_prefabs.Clear();
            m_weights.Clear();
            foreach (var raw in csv.Split(','))
            {
                var item = raw.Trim();
                if (item.Length == 0) continue;
                float weight = 1f;
                int colon = item.LastIndexOf(':');
                if (colon > 0 && float.TryParse(item.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                {
                    weight = parsed;
                    item = item.Substring(0, colon).Trim();
                }
                if (weight <= 0f) continue;
                var prefab = ZNetScene.instance.GetPrefab(item);
                if (prefab == null || prefab.GetComponent<ZNetView>() == null)
                {
                    if (s_warned.Add(item))
                        FiresLogger.LogWarning($"[FiresVoxel] cave content '{m_entry.Definition.Section} / {m_entry.Definition.Key}': "
                            + $"'{item}' is not a networked prefab here - skipped.");
                    continue;
                }
                m_prefabs.Add(prefab);
                m_weights.Add(weight);
            }
        }
    }
}
