using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // One chunk generator and one chunk seed per biome, in progression order. A generator makes one seed from any
    // two different items of its biome's list, 50 of each, or from 100 each of the previous tier's basic pair; the
    // Meadows generator takes every biome's list. A seed raises any closed zone beside open land as its own biome's
    // land. Meadows is buildable from the start; every later generator appears after the previous biome's boss.
    internal sealed class ChunkTier
    {
        internal readonly int Level;
        internal readonly string Id;
        internal readonly string Title;
        internal readonly string BasicA;
        internal readonly string BasicB;
        internal readonly string[] Inputs;
        internal readonly string BossKey;
        internal readonly KeyValuePair<string, int>[] Cost;

        internal ChunkTier(int level, string id, string title, string basicA, string basicB, string[] inputs, string bossKey,
            params KeyValuePair<string, int>[] cost)
        {
            Level = level;
            Id = id;
            Title = title;
            BasicA = basicA;
            BasicB = basicB;
            Inputs = inputs;
            BossKey = bossKey;
            Cost = cost;
        }

        internal string MachinePrefab => "FiresChunkMachine_" + Id;
        internal string SpawnerPrefab => "FiresChunkSpawner_" + Id;
        internal string MachineName => Title + " Chunk Generator";
        internal string SpawnerName => Title + " Chunk Seed";
        internal Heightmap.Biome Biome => ZoneModeChunkTiers.TierBiome(Level);
    }

    internal static class ZoneModeChunkTiers
    {
        internal const int PairAmount = 50;
        internal const int DoubleAmount = 100;
        private const float SampleOffset = 24f;

        // The item lists are the user-approved recipe draft (Tools\FAT_ZONE_MODE_PLAN.md), with every ID taken from
        // the 1.0 data in Tools\ZONE_MODE_BIOME_DATA.md.
        internal static readonly ChunkTier[] All =
        {
            new ChunkTier(0, "Meadows", "Meadows", "Stone", "Wood",
                new[] { "Stone", "Wood", "Flint", "Raspberry", "RawMeat", "NeckTail", "BeechSeeds", "Dandelion" },
                null, Cost("Stone", 20), Cost("Wood", 10)),
            new ChunkTier(1, "BlackForest", "Black Forest", "RoundLog", "CopperOre",
                new[] { "RoundLog", "CopperOre", "TinOre", "GreydwarfEye", "TrollHide", "Blueberries", "PineCone", "AncientSeed", "BjornPaw" },
                "defeated_eikthyr", Cost("Stone", 30), Cost("RoundLog", 10)),
            new ChunkTier(2, "Swamp", "Swamp", "IronScrap", "ElderBark",
                new[] { "IronScrap", "ElderBark", "Guck", "Bloodbag", "Ooze", "WitheredBone", "Chain", "Root", "TurnipSeeds", "WrithanRoots" },
                "defeated_gdking", Cost("Stone", 30), Cost("ElderBark", 10)),
            new ChunkTier(3, "Mountain", "Mountain", "SilverOre", "Obsidian",
                new[] { "SilverOre", "Obsidian", "WolfPelt", "WolfFang", "FreezeGland", "Crystal", "OnionSeeds", "WolfHairBundle" },
                "defeated_bonemass", Cost("Stone", 30), Cost("Obsidian", 10)),
            new ChunkTier(4, "Plains", "Plains", "BlackMetalScrap", "Flax",
                new[] { "BlackMetalScrap", "Flax", "Barley", "LoxPelt", "Needle", "Cloudberry", "Tar", "UndeadBjornRibcage" },
                "defeated_dragon", Cost("Stone", 30), Cost("Tar", 10)),
            new ChunkTier(5, "Mistlands", "Mistlands", "YggdrasilWood", "BlackMarble",
                new[] { "YggdrasilWood", "BlackMarble", "Softtissue", "Carapace", "Sap", "MushroomMagecap", "MushroomJotunPuffs", "Bilebag", "ScaleHide" },
                "defeated_goblinking", Cost("BlackMarble", 20), Cost("YggdrasilWood", 10)),
            new ChunkTier(6, "Ashlands", "Ashlands", "Blackwood", "Grausten",
                new[] { "Blackwood", "Grausten", "FlametalOreNew", "CharredBone", "AskHide", "MorgenSinew", "SulfurStone", "ProustitePowder", "Vineberry", "Fiddleheadfern" },
                "defeated_queen", Cost("Grausten", 20), Cost("Blackwood", 10)),
            new ChunkTier(7, "DeepNorth", "Deep North", "Frostwood", "GoldOre",
                new[] { "Frostwood", "GoldOre", "Ice", "MooseHide", "SealHide", "NornThread", "Lingonberry", "KaleSeeds", "FrostCore" },
                "defeated_fader", Cost("Grausten", 30), Cost("FlametalNew", 10)),
        };

        private static KeyValuePair<string, int> Cost(string item, int amount) => new KeyValuePair<string, int>(item, amount);

        internal static ChunkTier ByLevel(int level)
        {
            foreach (var tier in All) if (tier.Level == level) return tier;
            return null;
        }

        // Items this tier's generator accepts: its own list for the 50 + 50 recipe (every tier's list for Meadows),
        // plus the previous tier's basic pair for the 100 + 100 recipe.
        internal static IEnumerable<string> AcceptedInputs(ChunkTier tier)
        {
            var seen = new HashSet<string>();
            foreach (var list in PairLists(tier))
                foreach (var item in list)
                    if (seen.Add(item)) yield return item;
            var previous = ByLevel(tier.Level - 1);
            if (previous == null) yield break;
            if (seen.Add(previous.BasicA)) yield return previous.BasicA;
            if (seen.Add(previous.BasicB)) yield return previous.BasicB;
        }

        internal static IEnumerable<string[]> PairLists(ChunkTier tier)
        {
            if (tier.Level != Starter.Level) { yield return tier.Inputs; yield break; }
            foreach (var any in All) yield return any.Inputs;
        }

        internal static ChunkTier Starter => All[0];

        internal static bool TryByMachineName(string pieceName, out ChunkTier tier)
        {
            foreach (var candidate in All)
                if (candidate.MachineName == pieceName) { tier = candidate; return true; }
            tier = null;
            return false;
        }

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
