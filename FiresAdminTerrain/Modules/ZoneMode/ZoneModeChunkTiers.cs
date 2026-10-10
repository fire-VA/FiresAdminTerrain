using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // The recipes of the one Chunk Generator (Tools\CHUNK_GENERATOR.md), in progression order: the plain Chunk Seed,
    // which only ever brings a zone back as the world made it (Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md), and one seed per
    // biome, which raises its biome over any closed zone beside open land. A full load of fuel makes two plain seeds or
    // one biome seed. The plain recipe burns any biome's materials; a biome recipe burns its own biome's and every later
    // biome's, and is learned when the previous boss falls. The tier also names the old per-biome generator prefab its
    // recipe replaces, which ZoneModeChunkGenerator swaps for the one machine wherever a world still holds one.
    internal sealed class ChunkTier
    {
        private const string MachinePrefix = "FiresChunkMachine_";
        private const string SpawnerPrefix = "FiresChunkSpawner_";
        private const int PlainSeedsPerBatch = 2;
        private const int BiomeSeedsPerBatch = 1;

        internal readonly int Level;
        internal readonly string Id;
        internal readonly string Title;
        internal readonly string[] Inputs;
        internal readonly string BossKey;
        internal readonly KeyValuePair<string, int>[] Cost;
        internal readonly string MachinePrefab;
        internal readonly string SpawnerPrefab;
        internal readonly string MachineName;
        internal readonly string SpawnerName;
        internal readonly bool RestoresOnly;

        internal ChunkTier(int level, string id, string title, string[] inputs, string bossKey)
            : this(level, id, title, inputs, bossKey, MachinePrefix + id, SpawnerPrefix + id,
                title + " Chunk Generator", title + " Chunk Seed", false, null)
        {
        }

        private ChunkTier(int level, string id, string title, string[] inputs, string bossKey, string machinePrefab,
            string spawnerPrefab, string machineName, string spawnerName, bool restoresOnly, KeyValuePair<string, int>[] cost)
        {
            Level = level;
            Id = id;
            Title = title;
            Inputs = inputs;
            BossKey = bossKey;
            Cost = cost;
            MachinePrefab = machinePrefab;
            SpawnerPrefab = spawnerPrefab;
            MachineName = machineName;
            SpawnerName = spawnerName;
            RestoresOnly = restoresOnly;
        }

        // The one machine keeps the prefab id every world's starter was placed with, so each starter stays where it is.
        internal static ChunkTier Plain(int level, KeyValuePair<string, int>[] cost)
            => new ChunkTier(level, "Plain", "Chunk", null, null, MachinePrefix + "Meadows", SpawnerPrefix + "Plain",
                "Chunk Generator", "Chunk Seed", true, cost);

        // The Meadows recipe's old generator had moved to a new prefab id when its first id went to the plain generator.
        internal static ChunkTier Moved(int level, string id, string title, string machineId, string[] inputs, string bossKey)
            => new ChunkTier(level, id, title, inputs, bossKey, MachinePrefix + machineId, SpawnerPrefix + id,
                title + " Chunk Generator", title + " Chunk Seed", false, null);

        internal Heightmap.Biome Biome => ZoneModeChunkTiers.TierBiome(Level);

        internal int SeedsPerBatch => RestoresOnly ? PlainSeedsPerBatch : BiomeSeedsPerBatch;

        internal string RecipeName => SpawnerName;
    }

    internal static class ZoneModeChunkTiers
    {
        internal const int BatchFuel = 100;
        private const float SampleOffset = 24f;
        private const int PlainLevel = -1;

        // The item lists are the user-approved recipe draft (Tools\FAT_ZONE_MODE_PLAN.md), with every ID taken from
        // the 1.0 data in Tools\ZONE_MODE_BIOME_DATA.md. No item is in two lists.
        internal static readonly ChunkTier[] All =
        {
            ChunkTier.Plain(PlainLevel, new[] { Cost("Stone", 20), Cost("Wood", 10) }),
            ChunkTier.Moved(0, "Meadows", "Meadows", "MeadowsBiome",
                new[] { "Stone", "Wood", "Flint", "Raspberry", "RawMeat", "NeckTail", "BeechSeeds", "Dandelion" },
                "defeated_eikthyr"),
            new ChunkTier(1, "BlackForest", "Black Forest",
                new[] { "RoundLog", "CopperOre", "TinOre", "GreydwarfEye", "TrollHide", "Blueberries", "PineCone", "AncientSeed", "BjornPaw" },
                "defeated_eikthyr"),
            new ChunkTier(2, "Swamp", "Swamp",
                new[] { "IronScrap", "ElderBark", "Guck", "Bloodbag", "Ooze", "WitheredBone", "Chain", "Root", "TurnipSeeds", "WrithanRoots" },
                "defeated_gdking"),
            new ChunkTier(3, "Mountain", "Mountain",
                new[] { "SilverOre", "Obsidian", "WolfPelt", "WolfFang", "FreezeGland", "Crystal", "OnionSeeds", "WolfHairBundle" },
                "defeated_bonemass"),
            new ChunkTier(4, "Plains", "Plains",
                new[] { "BlackMetalScrap", "Flax", "Barley", "LoxPelt", "Needle", "Cloudberry", "Tar", "UndeadBjornRibcage" },
                "defeated_dragon"),
            new ChunkTier(5, "Mistlands", "Mistlands",
                new[] { "YggdrasilWood", "BlackMarble", "Softtissue", "Carapace", "Sap", "MushroomMagecap", "MushroomJotunPuffs", "Bilebag", "ScaleHide" },
                "defeated_goblinking"),
            new ChunkTier(6, "Ashlands", "Ashlands",
                new[] { "Blackwood", "Grausten", "FlametalOreNew", "CharredBone", "AskHide", "MorgenSinew", "SulfurStone", "ProustitePowder", "Vineberry", "Fiddleheadfern" },
                "defeated_queen"),
            new ChunkTier(7, "DeepNorth", "Deep North",
                new[] { "Frostwood", "GoldOre", "Ice", "MooseHide", "SealHide", "NornThread", "Lingonberry", "KaleSeeds", "FrostCore" },
                "defeated_fader"),
        };

        private static readonly Dictionary<string, int> s_itemLevel = new Dictionary<string, int>();

        private static KeyValuePair<string, int> Cost(string item, int amount) => new KeyValuePair<string, int>(item, amount);

        internal static ChunkTier ByLevel(int level)
        {
            foreach (var tier in All) if (tier.Level == level) return tier;
            return null;
        }

        // A saved recipe id that no longer exists falls back to the plain recipe, as a machine with none does.
        internal static ChunkTier ByRecipeId(string id)
        {
            if (!string.IsNullOrEmpty(id))
                foreach (var tier in All) if (tier.Id == id) return tier;
            return Starter;
        }

        // Everything a recipe burns: every biome's list for the plain recipe; the recipe's own list and every later
        // biome's for a biome recipe.
        internal static IEnumerable<string> Burns(ChunkTier tier)
        {
            foreach (var list in All)
                if (list.Inputs != null && (tier.RestoresOnly || list.Level >= tier.Level))
                    foreach (var item in list.Inputs) yield return item;
        }

        internal static bool Burns(ChunkTier tier, string item)
        {
            int level = ItemLevel(item);
            return level >= 0 && (tier.RestoresOnly || level >= tier.Level);
        }

        // The biome tier whose list holds the item, or -1.
        internal static int ItemLevel(string item)
        {
            if (s_itemLevel.Count == 0)
                foreach (var tier in All)
                    if (tier.Inputs != null)
                        foreach (var input in tier.Inputs) s_itemLevel[input] = tier.Level;
            return item != null && s_itemLevel.TryGetValue(item, out int level) ? level : -1;
        }

        internal static ChunkTier Starter => ByLevel(PlainLevel);

        internal static int BiomeTier(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.BlackForest: return 1;
                case Heightmap.Biome.Swamp: return 2;
                case Heightmap.Biome.Mountain: return 3;
                case Heightmap.Biome.Plains: return 4;
                case Heightmap.Biome.Mistlands: return 5;
                case Heightmap.Biome.AshLands: return 6;
                case Heightmap.Biome.DeepNorth: return 7;
                default: return 0;
            }
        }

        internal static Heightmap.Biome TierBiome(int level)
        {
            switch (level)
            {
                case 1: return Heightmap.Biome.BlackForest;
                case 2: return Heightmap.Biome.Swamp;
                case 3: return Heightmap.Biome.Mountain;
                case 4: return Heightmap.Biome.Plains;
                case 5: return Heightmap.Biome.Mistlands;
                case 6: return Heightmap.Biome.AshLands;
                case 7: return Heightmap.Biome.DeepNorth;
                default: return Heightmap.Biome.Meadows;
            }
        }

        internal static string TierTitle(int level)
        {
            foreach (var tier in All) if (tier.Level == level) return tier.Title;
            return "Deep North";
        }

        // The zone's land as the real world has it: the biome most of nine samples stand in, ignoring sea
        // (all sea opens with any spawner). Read in a planning scope so the closed zone's gate doesn't
        // answer "sea" for it.
        internal static int ZoneTier(Vector2s zone, out Heightmap.Biome dominant)
        {
            dominant = Heightmap.Biome.Ocean;
            var generator = WorldGenerator.instance;
            if (generator == null) return 0;
            var counts = new Dictionary<Heightmap.Biome, int>();
            ZoneModeGate.EnterPlanning();
            try
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        float x = zone.x * ZoneOpenSet.ZoneSize + dx * SampleOffset;
                        float z = zone.y * ZoneOpenSet.ZoneSize + dz * SampleOffset;
                        var sector = generator.GetBiomeSector(x, z, false);
                        var biome = sector != null ? sector.Biome : generator.GetBiome(x, z);
                        if (biome == Heightmap.Biome.Ocean || biome == Heightmap.Biome.None) continue;
                        counts.TryGetValue(biome, out int count);
                        counts[biome] = count + 1;
                    }
                }
            }
            finally { ZoneModeGate.ExitPlanning(); }

            int best = 0;
            foreach (var kv in counts)
            {
                if (kv.Value > best || (kv.Value == best && BiomeTier(kv.Key) > BiomeTier(dominant)))
                {
                    best = kv.Value;
                    dominant = kv.Key;
                }
            }
            return best == 0 ? 0 : BiomeTier(dominant);
        }
    }
}
