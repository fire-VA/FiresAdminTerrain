using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Everything the Chunk Hammer fires, as ammo: the plain Chunk Seed, one biome seed per chunk generator, and the
    // location seeds forged from the biome seeds, where the pair of biome seeds picks the location. A unique seed also costs a location seed and copies a
    // one-per-world location, so those can repeat. Names, descriptions and every recipe live here; the bundle carries
    // only the looks.
    internal sealed class ChunkSeed
    {
        internal readonly string Prefab;
        internal readonly string Name;
        internal readonly ChunkTier Tier;
        internal readonly string Location;
        internal readonly string Title;
        internal readonly ChunkTier Second;
        internal readonly int PrimaryAmount;
        internal readonly int SecondAmount;
        internal readonly string Ingredient;
        internal readonly int IngredientAmount;
        internal readonly string Note;

        internal ChunkSeed(string prefab, string name, ChunkTier tier, string location = null, string title = null,
            ChunkTier second = null, int primaryAmount = 0, int secondAmount = 0, string ingredient = null,
            int ingredientAmount = 0, string note = null)
        {
            Prefab = prefab;
            Name = name;
            Tier = tier;
            Location = location;
            Title = title;
            Second = second;
            PrimaryAmount = primaryAmount;
            SecondAmount = secondAmount;
            Ingredient = ingredient;
            IngredientAmount = ingredientAmount;
            Note = note;
        }

        internal bool IsLocation => Location != null;

        // The plain Chunk Seed: it brings a zone back as the world made it and can never raise other land over it.
        internal bool RestoresOnly => !IsLocation && Tier.RestoresOnly;

        internal string Land => IsLocation ? $"land holding {Title}" : RestoresOnly ? "the land the world made there" : $"{Tier.Title} land";
        internal bool Unique => Ingredient != null;

        internal string Description
        {
            get
            {
                if (RestoresOnly)
                    return "Chunk Hammer ammo. Strike closed land beside open land and that chunk comes back as the world made "
                        + "it: its own land, and anything that stood there before. It never raises other land. "
                        + $"Made by the {ZoneModeChunkTiers.Starter.MachineName}.";
                if (!IsLocation)
                    return $"Chunk Hammer ammo. Strike closed land beside open land to raise a {Tier.Title} chunk there. "
                        + $"Made by the {ZoneModeChunkTiers.Starter.MachineName}.";
                string text = $"Chunk Hammer ammo. Raises a chunk holding {Title}, on land copied from a place this world "
                    + "planned one.";
                if (Unique) text += " A one-of-a-kind place: the world keeps any it already has, and this makes another.";
                return Note != null ? text + " " + Note : text;
            }
        }
    }

    internal static class ZoneModeSeeds
    {
        internal const string AmmoType = "Chunk Seeds";
        internal const string HammerName = "Chunk Hammer";
        private const string HammerDescription = "Plants chunk seeds. Load seeds as its ammo, strike the closed land beside "
            + "open land you want to raise and confirm: the chunk rises and one seed is used.";
        private const string VanillaHammer = "Hammer";
        private const string ForgePrefab = "forge";
        private const string WorkbenchPrefab = "piece_workbench";
        private const int ForgeLevel = 1;

        private static readonly int[] Boss = { 6, 3 };
        private static readonly int[] Dungeon = { 4, 2 };
        private static readonly int[] Landmark = { 2, 1 };
        private const int UniqueAmount = 15;
        private const int UniqueIngredients = 3;
        private const int LesserUniqueAmount = 10;
        private const int LesserUniqueIngredients = 2;

        private static readonly KeyValuePair<string, int>[] EyeCost =
        {
            new KeyValuePair<string, int>("HardAntler", 1),
            new KeyValuePair<string, int>("TrophyDeer", 2),
            new KeyValuePair<string, int>("LeatherScraps", 10),
            new KeyValuePair<string, int>("Resin", 10),
        };

        // A hammer that drags land out of other worlds costs what the deep places give up: black metal for the head,
        // crystal for the runes, and a fuling totem for the laugh in it.
        private static readonly KeyValuePair<string, int>[] ChaosHammerCost =
        {
            new KeyValuePair<string, int>("BlackMetal", 20),
            new KeyValuePair<string, int>("Crystal", 10),
            new KeyValuePair<string, int>("TrophyGoblinKing", 1),
        };

        internal static readonly List<ChunkSeed> All = new List<ChunkSeed>();
        private static readonly Dictionary<string, ChunkSeed> s_byPrefab = new Dictionary<string, ChunkSeed>();
        private static readonly Dictionary<string, ChunkSeed> s_byName = new Dictionary<string, ChunkSeed>();
        private static readonly Dictionary<string, Recipe> s_recipes = new Dictionary<string, Recipe>();
        private static readonly HashSet<Recipe> s_ours = new HashSet<Recipe>();
        private static readonly HashSet<string> s_reportedMissing = new HashSet<string>();

        static ZoneModeSeeds()
        {
            foreach (var tier in ZoneModeChunkTiers.All) Add(new ChunkSeed(tier.SpawnerPrefab, tier.SpawnerName, tier));

            Location(0, 1, "Eikthyrnir", "Eikthyr's Altar", Boss);
            Location(0, 2, "WoodVillage1", "Abandoned Village", Landmark);
            Location(0, 3, "StoneCircle", "Stone Circle", Landmark);
            Location(0, 4, "ShipSetting01", "Stone Ship Setting", Landmark);
            Location(0, 5, "CombatRuin01", "Old Battlefield", Landmark);
            Location(0, 6, "WoodFarm1", "Abandoned Farm", Landmark);

            Location(1, 0, "Crypt2", "Burial Chambers", Dungeon);
            Location(1, 2, "GDKing", "The Elder's Altar", Boss);
            Location(1, 3, "TrollCave02", "Troll Cave", Dungeon);
            Location(1, 4, "StoneTowerRuins03", "Stone Tower Ruins", Landmark);
            Location(1, 5, "Greydwarf_camp1", "Greydwarf Camp", Landmark);
            Location(1, 6, "Ruin2", "Forest Ruins", Landmark);

            Location(2, 0, "SunkenCrypt4", "Sunken Crypt", Dungeon);
            Location(2, 1, "SwampHut1", "Swamp Hut", Landmark);
            Location(2, 3, "Bonemass", "Bonemass's Altar", Boss);
            Location(2, 4, "FireHole", "Surtling Fire Hole", Dungeon);
            Location(2, 5, "SwampRuin1", "Swamp Ruins", Landmark);
            Location(2, 6, "SwampWell1", "Swamp Well", Landmark);

            Location(3, 0, "DrakeNest01", "Drake Nest", Dungeon);
            Location(3, 1, "MountainCave02", "Frost Cave", Dungeon);
            Location(3, 2, "AbandonedLogCabin04", "Log Cabin", Landmark);
            Location(3, 4, "Dragonqueen", "Moder's Altar", Boss);
            Location(3, 5, "StoneTowerRuins04", "Mountain Tower Ruins", Landmark);
            Location(3, 6, "MountainGrave01", "Mountain Grave", Landmark);

            Location(4, 0, "GoblinCamp2", "Fuling Village", Dungeon);
            Location(4, 1, "StoneTower1", "Stone Tower", Landmark);
            Location(4, 2, "TarPit1", "Tar Pit", Dungeon);
            Location(4, 3, "StoneHenge1", "Stone Henge", Landmark);
            Location(4, 5, "GoblinKing", "Yagluth's Altar", Boss);
            Location(4, 6, "Ruin3", "Ancient Ruins", Landmark);

            Location(5, 0, "Mistlands_DvergrTownEntrance1", "Infested Mine", Dungeon);
            Location(5, 1, "Mistlands_Excavation1", "Dvergr Excavation", Dungeon);
            Location(5, 2, "Mistlands_Giant1", "Giant's Remains", Landmark);
            Location(5, 3, "Mistlands_GuardTower1_new", "Dvergr Guard Tower", Landmark);
            Location(5, 4, "Mistlands_Harbour1", "Dvergr Harbour", Landmark);
            Location(5, 6, "Mistlands_DvergrBossEntrance1", "The Queen's Citadel", Boss);

            Location(6, 0, "CharredFortress", "Charred Fortress", Dungeon);
            Location(6, 1, "LeviathanLava", "Lava Leviathan", Dungeon);
            Location(6, 2, "MorgenHole1", "Morgen Hole", Landmark);
            Location(6, 3, "CharredTowerRuins1", "Charred Tower Ruins", Landmark);
            Location(6, 4, "VoltureNest", "Volture Nest", Landmark);
            Location(6, 5, "FaderLocation", "Fader's Altar", Boss);
            Location(6, 7, "SulfurArch", "Sulfur Arch", Landmark);

            Location(7, 0, "MorkBorg", "Mork Borg", Dungeon, "A Jotun fortress behind a gate that takes an Intricate Key.");
            Location(7, 1, "DN_gammeltrollFrac01", "Petrified Troll", Dungeon, "Its fractured remains hold Petrified Tissue.");
            Location(7, 2, "TheHole01", "The Hole", Landmark);
            Location(7, 3, "NorthVillage", "Northern Village", Landmark);
            Location(7, 4, "IcePond1", "Ice Pond", Landmark);
            Location(7, 5, "LumberCamp", "Lumber Camp", Landmark);
            Location(7, 6, "DN_Bossroom", "Aesir Passage", Boss, "Kall Fimbulbringer is summoned there.");

            Location(0, 7, "WoodVillage2", "Meadow Village", Landmark);
            Location(1, 7, "StoneTowerRuins07", "Forest Tower Ruins", Landmark);
            Location(2, 7, "Grave1", "Swamp Grave", Landmark);
            Location(3, 7, "MountainWell1", "Mountain Well", Landmark);
            Location(4, 7, "StoneHenge5", "Standing Stones", Landmark);
            Location(5, 7, "Mistlands_Lighthouse1_new", "Dvergr Lighthouse", Landmark);

            Unique(1, 0, UniqueAmount, "Crypt2", UniqueIngredients, "Vendor_BlackForest", "Haldor's Camp",
                "Haldor the trader keeps it.");
            Unique(0, 2, UniqueAmount, "WoodVillage1", UniqueIngredients, "Hildir_camp", "Hildir's Camp",
                "Hildir the trader keeps it.");
            Unique(2, 3, UniqueAmount, "SwampHut1", UniqueIngredients, "BogWitch_Camp", "the Bog Witch's Camp",
                "The Bog Witch trades there.");
            Unique(3, 4, UniqueAmount, "MountainCave02", UniqueIngredients, "AncientUpgradeStation", "the Forge of Potential",
                "The only forge that upgrades gear past its usual limit.");
            Unique(6, 5, UniqueAmount, "CharredFortress", UniqueIngredients, "PlaceofMystery1", "a Place of Mystery",
                "The tip of Dyrnwyn lies there.");
            Unique(6, 4, UniqueAmount, "CharredTowerRuins1", UniqueIngredients, "PlaceofMystery2", "a Place of Mystery",
                "The blade of Dyrnwyn lies there.");
            Unique(6, 3, UniqueAmount, "MorgenHole1", UniqueIngredients, "PlaceofMystery3", "a Place of Mystery",
                "A charred warrior there carries the hilt of Dyrnwyn.");
            Unique(0, 1, LesserUniqueAmount, "StoneCircle", LesserUniqueIngredients, "BigRockClearing", "the Rock Clearing",
                "A clearing strewn with loose stone.");
        }

        private static void Location(int primary, int second, string location, string title, int[] cost, string note = null)
        {
            var tier = ZoneModeChunkTiers.ByLevel(primary);
            Add(new ChunkSeed(ZoneModePrefabs.LocationSeedPrefix + location, title + " Seed", tier, location, title,
                ZoneModeChunkTiers.ByLevel(second), cost[0], cost[1], note: note));
        }

        private static void Unique(int primary, int second, int amount, string ingredient, int ingredientAmount,
            string location, string title, string note)
        {
            string name = UniqueName(location, title);
            Add(new ChunkSeed(ZoneModePrefabs.LocationSeedPrefix + location, name, ZoneModeChunkTiers.ByLevel(primary), location,
                title, ZoneModeChunkTiers.ByLevel(second), amount, amount, ingredient, ingredientAmount, note));
        }

        private static string UniqueName(string location, string title)
        {
            switch (location)
            {
                case "PlaceofMystery1": return "Place of Mystery Seed (Tip)";
                case "PlaceofMystery2": return "Place of Mystery Seed (Blade)";
                case "PlaceofMystery3": return "Place of Mystery Seed (Hilt)";
                default:
                    string bare = title.StartsWith("the ") ? title.Substring(4) : title;
                    return char.ToUpperInvariant(bare[0]) + bare.Substring(1) + " Seed";
            }
        }

        private static void Add(ChunkSeed seed)
        {
            All.Add(seed);
            s_byPrefab[seed.Prefab] = seed;
            s_byName[seed.Name] = seed;
        }

        internal static bool TryGet(string prefab, out ChunkSeed seed)
        {
            seed = null;
            return prefab != null && s_byPrefab.TryGetValue(prefab, out seed);
        }

        internal static bool TryGet(ItemDrop.ItemData item, out ChunkSeed seed)
        {
            seed = null;
            if (item?.m_shared == null) return false;
            if (item.m_dropPrefab != null && s_byPrefab.TryGetValue(item.m_dropPrefab.name, out seed)) return true;
            return s_byName.TryGetValue(item.m_shared.m_name, out seed);
        }

        internal static bool IsHammer(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            return item.m_dropPrefab != null ? item.m_dropPrefab.name == ZoneModePrefabs.ChunkHammerPrefab : item.m_shared.m_name == HammerName;
        }

        // ── Names, descriptions and recipes, on every world ObjectDB ─────────────────────────────
        internal static void Register(ObjectDB db)
        {
            if (db == null || db.m_items == null || db.m_items.Count == 0) return;
            Describe();
            var forge = Station(ForgePrefab);
            var workbench = Station(WorkbenchPrefab);
            int added = 0;
            foreach (var seed in All)
            {
                if (!seed.IsLocation || forge == null) continue;
                var item = Drop(seed.Prefab);
                var primary = Drop(seed.Tier.SpawnerPrefab);
                var second = Drop(seed.Second.SpawnerPrefab);
                if (item == null || primary == null || second == null) continue;
                var cost = new List<Piece.Requirement> { Requirement(primary, seed.PrimaryAmount), Requirement(second, seed.SecondAmount) };
                if (seed.Unique)
                {
                    var ingredient = Drop(ZoneModePrefabs.LocationSeedPrefix + seed.Ingredient);
                    if (ingredient == null) continue;
                    cost.Add(Requirement(ingredient, seed.IngredientAmount));
                }
                if (Put(db, item, forge, ForgeLevel, cost.ToArray())) added++;
            }
            if (workbench != null && Drop(ZoneModeEyeOfTheBeholder.HelmetPrefab) is ItemDrop eye)
            {
                var cost = new List<Piece.Requirement>();
                foreach (var part in EyeCost)
                {
                    var item = db.GetItemPrefab(part.Key)?.GetComponent<ItemDrop>();
                    if (item != null) cost.Add(Requirement(item, part.Value));
                }
                if (cost.Count == EyeCost.Length && Put(db, eye, workbench, 1, cost.ToArray())) added++;
            }
            if (Drop(ZoneModePrefabs.ChunkHammerPrefab) is ItemDrop hammer)
            {
                var vanilla = db.m_recipes.Find(r => r != null && r.m_item != null && r.m_item.name == VanillaHammer);
                if (vanilla != null)
                {
                    var cost = new Piece.Requirement[vanilla.m_resources.Length];
                    for (int i = 0; i < cost.Length; i++) cost[i] = Copy(vanilla.m_resources[i]);
                    if (Put(db, hammer, vanilla.m_craftingStation, vanilla.m_minStationLevel, cost, vanilla.m_repairStation)) added++;
                }
            }
            if (forge != null && Drop(ZoneModePrefabs.ChaosHammerPrefab) is ItemDrop chaos)
            {
                var cost = new List<Piece.Requirement>();
                foreach (var part in ChaosHammerCost)
                {
                    var item = db.GetItemPrefab(part.Key)?.GetComponent<ItemDrop>();
                    if (item != null) cost.Add(Requirement(item, part.Value));
                }
                if (cost.Count == ChaosHammerCost.Length && Put(db, chaos, forge, ForgeLevel, cost.ToArray(), forge)) added++;
            }
            if (added > 0) FiresLogger.LogInfo($"[FiresZones] {added} chunk recipe(s) added to this world's ObjectDB.");
        }

        private static void Describe()
        {
            foreach (var seed in All)
            {
                var shared = Drop(seed.Prefab)?.m_itemData?.m_shared;
                if (shared == null) continue;
                shared.m_name = seed.Name;
                shared.m_description = seed.Description;
            }
            var hammer = Drop(ZoneModePrefabs.ChunkHammerPrefab)?.m_itemData?.m_shared;
            if (hammer == null) return;
            hammer.m_name = HammerName;
            hammer.m_description = HammerDescription;
        }

        private static bool Put(ObjectDB db, ItemDrop item, CraftingStation station, int stationLevel, Piece.Requirement[] cost,
            CraftingStation repairStation = null)
        {
            string name = "Recipe_" + item.name;
            if (!s_recipes.TryGetValue(name, out var recipe) || recipe == null)
            {
                recipe = ScriptableObject.CreateInstance<Recipe>();
                recipe.name = name;
                s_recipes[name] = recipe;
                s_ours.Add(recipe);
            }
            recipe.m_item = item;
            recipe.m_amount = 1;
            recipe.m_enabled = true;
            recipe.m_craftingStation = station;
            recipe.m_repairStation = repairStation;
            recipe.m_minStationLevel = Mathf.Max(1, stationLevel);
            recipe.m_resources = cost;
            if (db.m_recipes.Contains(recipe)) return false;
            db.m_recipes.Add(recipe);
            return true;
        }

        private static Piece.Requirement Requirement(ItemDrop item, int amount)
            => new Piece.Requirement { m_resItem = item, m_amount = amount, m_amountPerLevel = 0, m_recover = true };

        private static Piece.Requirement Copy(Piece.Requirement from) => new Piece.Requirement
        {
            m_resItem = from.m_resItem,
            m_amount = from.m_amount,
            m_extraAmountOnlyOneIngredient = from.m_extraAmountOnlyOneIngredient,
            m_amountPerLevel = from.m_amountPerLevel,
            m_upgraderResource = from.m_upgraderResource,
            m_recover = from.m_recover,
        };

        private static ItemDrop Drop(string prefab)
        {
            if (ZoneModePrefabs.TryGet(prefab, out var go)) return go.GetComponent<ItemDrop>();
            if (s_reportedMissing.Add(prefab))
                FiresLogger.LogWarning($"[FiresZones] '{prefab}' is not in the fireszones bundle — it and its recipe are left out.");
            return null;
        }

        private static CraftingStation Station(string prefab)
        {
            var station = ZNetScene.instance?.GetPrefab(prefab)?.GetComponent<CraftingStation>();
            if (station != null) return station;
            var table = ObjectDB.instance?.GetItemPrefab(VanillaHammer)?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
            if (table == null) return null;
            foreach (var piece in table.m_pieces)
                if (piece != null && piece.name == prefab) return piece.GetComponent<CraftingStation>();
            return null;
        }

        // Chunk recipes appear only in zone-mode worlds, and the Chunk Hammer's only after its owner has held one: the
        // first comes from a chest in a cave beneath the starting land. The Chaos Hammer carries every admin terrain
        // tool, so it is an admin's recipe in any world and nobody else's (Fire, 2026-09-27).
        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) })]
        private static class Player_HaveRequirements_ChunkRecipes
        {
            private static void Postfix(Player __instance, Recipe recipe, bool discover, ref bool __result)
            {
                if (!__result || recipe == null || !s_ours.Contains(recipe)) return;
                if (recipe.m_item != null && recipe.m_item.name == ZoneModePrefabs.ChaosHammerPrefab)
                {
                    if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) __result = false;
                    return;
                }
                if (!ZoneModeRegistry.ModeActive) { __result = false; return; }
                if (discover && recipe.m_item != null && recipe.m_item.name == ZoneModePrefabs.ChunkHammerPrefab
                    && !__instance.IsMaterialKnown(recipe.m_item.m_itemData.m_shared.m_name))
                    __result = false;
            }
        }
    }
}
