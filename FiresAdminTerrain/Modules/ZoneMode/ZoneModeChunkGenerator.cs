using System;
using System.Collections.Generic;
using System.Text;
using FiresCore.Pieces;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;

namespace VerdantsAscent.Terrain.Zones
{
    // The one Chunk Generator (Tools\CHUNK_GENERATOR.md): the starter's prefab from the "fireszones" bundle, wired here
    // and listed on the hammer in zone-mode worlds. Its front door takes fuel, picked from a grid of what the player
    // carries; its ore hopper picks the recipe (ZoneModeChunkTiers). The ZDO owner burns one fuel every two seconds and
    // turns each full load into seeds, with a rare location seed when a biome recipe burns later biomes' materials.
    // Changing the recipe drops every loaded or burned fuel the new recipe cannot burn beside the machine. Vanilla's
    // smelting loop never runs for it; vanilla's fire, sound and animation follow the loaded count.
    internal static class ZoneModeChunkGenerator
    {
        internal const int Capacity = 100;
        private const float SecondsPerFuel = 2f;
        private const float MaxCatchUpSeconds = 3600f;
        private const float BonusPerTierStep = 0.0002f;
        private const float BonusCap = 0.1f;
        private const float LuckyHintRange = 30f;
        private const float DropSide = 1.6f;
        private const float DropSpread = 0.3f;
        private const float DropLift = 1f;
        private const int PercentScale = 100;
        internal const string FuelSwitch = "add_wood";
        internal const string RecipeSwitch = "add_ore";
        private const string AddFuelRpc = "FiresChunkGen_AddFuel";
        private const string RecipeRpc = "FiresChunkGen_Recipe";
        private const string LuckyRpc = "FiresChunkGen_Lucky";
        private const string ForceBonusRpc = "FiresChunkGen_ForceBonus";
        private const string ForceBonusCommand = "fires_chunkgen_bonus";
        private const float CommandReach = 8f;
        private const string LegacyQueueKey = "item";
        private const string Description = "An engine of the old ones that burns what the land gives into Chunk Seeds. "
            + "Feed it at its door; choose what it makes at its hopper. Every full load of 100 makes two plain Chunk Seeds, "
            + "or one seed of a biome whose guardian has fallen.";

        internal static readonly int PrefabHash = ZoneModeChunkTiers.Starter.MachinePrefab.GetStableHashCode();
        private static readonly int RecipeHash = "fires_chunk_recipe".GetStableHashCode();
        private static readonly int FuelHash = "fires_chunk_fuel".GetStableHashCode();
        private static readonly int CountsHash = "fires_chunk_counts".GetStableHashCode();
        private static readonly int ForceBonusHash = "fires_chunk_forcebonus".GetStableHashCode();

        private static readonly Action<Smelter> UpdateState =
            AccessTools.MethodDelegate<Action<Smelter>>(AccessTools.Method(typeof(Smelter), "UpdateState"));

        private static GameObject s_prefab;
        private static readonly Dictionary<string, ItemDrop> s_fuel = new Dictionary<string, ItemDrop>();
        private static readonly Dictionary<int, ChunkTier> s_legacy = new Dictionary<int, ChunkTier>();
        private static readonly Dictionary<int, ChunkSeed> s_bonusSeeds = new Dictionary<int, ChunkSeed>();
        private static readonly HashSet<string> s_reportedMissing = new HashSet<string>();
        private static bool s_liveSwapDone;
        private static bool s_tickFaultLogged;
        private static bool s_commandRegistered;

        internal static bool TryGetPrefab(out GameObject prefab)
        {
            prefab = s_prefab;
            return prefab != null;
        }

        // ── Registration (FAT's ZNetScene.Awake / ObjectDB.Awake hooks) ──────────────────────────────────────────
        internal static void OnZNetSceneAwake()
        {
            // ZNetScene.Awake's "chunk generator" step is 47-75 ms of one join frame (R45-R47); this says which part.
            var steps = new StepTimes();
            try
            {
                bool wasLoaded = ZoneModePrefabs.IsLoaded;
                ZoneModePrefabs.EnsureLoaded();
                steps.Lap(wasLoaded ? "bundle (already loaded)" : "bundle load");
                ZoneModePrefabs.RegisterInScene(ZNetScene.instance);
                steps.Lap("scene wiring");
                Wire(steps);
                RegisterInObjectDB(ObjectDB.instance);
                steps.Lap("objectdb");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] chunk generator registration (ZNetScene) threw: {ex.Message}"); }
            FiresLogger.LogInfo($"[FiresZones] chunk generator steps: {steps}.");
        }

        internal static void OnObjectDBAwake(ObjectDB db)
        {
            if (HammerPieces.IsMainMenuObjectDB(db)) return;
            try
            {
                Wire();
                RegisterInObjectDB(db);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] chunk generator registration (ObjectDB) threw: {ex.Message}"); }
        }

        private static void Wire(StepTimes steps = null)
        {
            ZoneModePark.ZoneRestored -= OnZoneRestored;
            ZoneModePark.ZoneRestored += OnZoneRestored;
            RegisterCommand();
            ZoneModeChunkGeneratorTest.RegisterRpc();
            if (s_legacy.Count == 0)
                foreach (var tier in ZoneModeChunkTiers.All)
                    if (!tier.RestoresOnly) s_legacy[tier.MachinePrefab.GetStableHashCode()] = tier;
            steps?.Lap("command + rpc");
            var starter = ZoneModeChunkTiers.Starter;
            s_prefab = null;
            if (!ZoneModePrefabs.TryGet(starter.MachinePrefab, out var machine) || !WireMachine(machine, starter)) return;
            steps?.Lap("machine");
            ZoneModeRavenHints.RetargetGuidePoint(machine);
            ZoneModeChunkGeneratorLook.Dress(machine);
            steps?.Lap("raven + look");
            s_prefab = machine;
        }

        // What the machine takes, holds and costs. Fuels this game lacks are only left out of their recipes; a missing
        // build cost or switch keeps the machine off the hammer.
        private static bool WireMachine(GameObject machine, ChunkTier starter)
        {
            foreach (var tier in ZoneModeChunkTiers.All)
                if (tier.Inputs != null)
                    foreach (string name in tier.Inputs)
                    {
                        var item = FindItem(name);
                        if (item != null) s_fuel[name] = item;
                    }
            var requirements = new List<Piece.Requirement>();
            foreach (var cost in starter.Cost)
            {
                var item = FindItem(cost.Key);
                if (item == null) return false;
                requirements.Add(new Piece.Requirement { m_resItem = item, m_amount = cost.Value, m_recover = true });
            }
            var smelter = machine.GetComponent<Smelter>();
            var door = SwitchAt(machine, FuelSwitch);
            var hopper = SwitchAt(machine, RecipeSwitch);
            if (smelter == null || door == null || hopper == null || s_fuel.Count == 0)
            {
                if (s_reportedMissing.Add(machine.name))
                    FiresLogger.LogWarning($"[FiresZones] {machine.name} lacks its smelter, its '{FuelSwitch}'/'{RecipeSwitch}' switch "
                        + "or any fuel this game knows: no Chunk Generator on the hammer.");
                return false;
            }

            door.gameObject.SetActive(true);
            door.m_holdRepeatInterval = -1f;
            hopper.m_holdRepeatInterval = -1f;
            smelter.m_fuelItem = null;
            smelter.m_maxFuel = 0;
            smelter.m_fuelPerProduct = 0;
            smelter.m_maxOre = Capacity;
            smelter.m_secPerProduct = SecondsPerFuel;
            smelter.m_spawnStack = false;
            smelter.m_conversion = new List<Smelter.ItemConversion>();
            smelter.m_addOreSwitch = null;
            smelter.m_addWoodSwitch = null;
            smelter.m_emptyOreSwitch = null;
            smelter.m_name = starter.MachineName;
            foreach (var light in machine.GetComponentsInChildren<Light>(true)) light.shadows = LightShadows.None;

            var piece = machine.GetComponent<Piece>();
            piece.m_name = starter.MachineName;
            piece.m_description = Description;
            piece.m_resources = requirements.ToArray();
            return true;
        }

        private static void RegisterInObjectDB(ObjectDB db)
        {
            if (db == null || db.m_items == null || db.m_items.Count == 0) return;
            ZoneModePrefabs.RegisterItems(db);
            ZoneModeSeeds.Register(db);
            var table = HammerPieces.HammerTable();
            if (table != null && s_prefab != null) HammerPieces.AddToTable(table, s_prefab);
        }

        private static ItemDrop FindItem(string name)
        {
            var drop = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name)?.GetComponent<ItemDrop>() : null;
            if (drop == null && ObjectDB.instance != null) drop = ObjectDB.instance.GetItemPrefab(name)?.GetComponent<ItemDrop>();
            if (drop == null && ObjectDB.instance != null && ObjectDB.instance.m_items.Count > 0 && s_reportedMissing.Add(name))
                FiresLogger.LogWarning($"[FiresZones] Chunk Generator: item '{name}' does not exist in this game; recipes burn it no more.");
            return drop;
        }

        private static Switch SwitchAt(GameObject machine, string child)
        {
            var node = machine.transform.Find(child);
            return node != null ? node.GetComponent<Switch>() : null;
        }

        // ── Reading a machine ────────────────────────────────────────────────────────────────────────────────────
        internal static bool IsOurs(ZNetView nview)
            => nview != null && nview.GetZDO() != null && nview.GetZDO().GetPrefab() == PrefabHash;

        internal static ChunkTier CurrentRecipe(ZDO zdo) => ZoneModeChunkTiers.ByRecipeId(zdo?.GetString(RecipeHash, ""));

        internal static int Loaded(ZDO zdo) => zdo != null ? LoadedFuel(zdo).Total : 0;

        private static FuelLoad LoadedFuel(ZDO zdo) => FuelLoad.Read(zdo.GetString(FuelHash, ""), false);

        private static FuelLoad BurnedCounts(ZDO zdo) => FuelLoad.Read(zdo.GetString(CountsHash, ""), true);

        internal static int Burned(ZDO zdo) => zdo != null ? BurnedCounts(zdo).Total : 0;

        internal static IEnumerable<KeyValuePair<string, int>> FuelEntries(ZDO zdo) => LoadedFuel(zdo).Entries;

        internal static IEnumerable<KeyValuePair<string, int>> CountEntries(ZDO zdo) => BurnedCounts(zdo).Entries;

        internal static bool BonusArmed(ZDO zdo) => zdo != null && zdo.GetBool(ForceBonusHash);

        // The bonus request the admin command sends; the owner checks the sender is an admin.
        internal static bool RequestBonus(Smelter machine)
        {
            var nview = machine != null ? machine.GetComponent<ZNetView>() : null;
            if (!IsOurs(nview)) return false;
            nview.InvokeRPC(ForceBonusRpc);
            return true;
        }

        internal static bool IsKnown(ChunkTier tier)
            => tier.BossKey == null
               || ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(tier.BossKey)
               || Player.m_localPlayer != null && Player.m_localPlayer.HaveUniqueKey(tier.BossKey);

        internal static bool TryGetFuel(string prefab, out ItemDrop item) => s_fuel.TryGetValue(prefab, out item);

        // The fuels the recipe burns that the player carries, by biome then name, with how many are carried.
        internal static List<KeyValuePair<ItemDrop, int>> CarriedFuel(ChunkTier recipe, Player player)
        {
            var carried = new List<KeyValuePair<ItemDrop, int>>();
            if (player == null) return carried;
            var inventory = player.GetInventory();
            foreach (string name in ZoneModeChunkTiers.Burns(recipe))
            {
                if (!s_fuel.TryGetValue(name, out var item)) continue;
                int count = inventory.CountItems(item.m_itemData.m_shared.m_name, -1, false);
                if (count > 0) carried.Add(new KeyValuePair<ItemDrop, int>(item, count));
            }
            return carried;
        }

        // How many of the machine's loaded and burned fuels a switch to `recipe` would set beside it.
        internal static int WouldRefund(ZDO zdo, ChunkTier recipe)
        {
            if (zdo == null) return 0;
            int refund = 0;
            foreach (var entry in LoadedFuel(zdo).Entries)
                if (!ZoneModeChunkTiers.Burns(recipe, entry.Key)) refund += entry.Value;
            foreach (var entry in BurnedCounts(zdo).Entries)
                if (!ZoneModeChunkTiers.Burns(recipe, entry.Key)) refund += entry.Value;
            return refund;
        }

        // ── Asking the machine (client side; the owner acts) ─────────────────────────────────────────────────────
        internal static int AddFuel(Smelter smelter, Player player, ItemDrop item, int amount)
        {
            var nview = smelter != null ? smelter.GetComponent<ZNetView>() : null;
            if (!IsOurs(nview) || player == null || item == null) return 0;
            var inventory = player.GetInventory();
            string shared = item.m_itemData.m_shared.m_name;
            amount = Mathf.Min(amount, inventory.CountItems(shared, -1, false), Capacity - Loaded(nview.GetZDO()));
            if (amount <= 0) return 0;
            bool cheated = inventory.GetAllItems().Exists(i => i.m_shared.m_name == shared && i.m_cheated);
            inventory.RemoveItem(shared, amount, -1, false);
            nview.InvokeRPC(AddFuelRpc, item.name, amount, cheated);
            player.Message(MessageHud.MessageType.Center, $"$msg_added {shared} x{amount}");
            return amount;
        }

        internal static void ChooseRecipe(Smelter smelter, ChunkTier recipe)
        {
            var nview = smelter != null ? smelter.GetComponent<ZNetView>() : null;
            if (IsOurs(nview) && recipe != null) nview.InvokeRPC(RecipeRpc, recipe.Id);
        }

        // ── Vanilla hooks ────────────────────────────────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Smelter), "Awake")]
        private static class Smelter_Awake_ChunkGenerator
        {
            private static void Postfix(Smelter __instance)
            {
                var nview = __instance.GetComponent<ZNetView>();
                if (!IsOurs(nview)) return;
                var door = SwitchAt(__instance.gameObject, FuelSwitch);
                var hopper = SwitchAt(__instance.gameObject, RecipeSwitch);
                if (door != null)
                {
                    door.m_onUse = (sw, user, item) => ZoneModeChunkGeneratorMenu.OpenFuel(__instance, user as Player, item);
                    door.m_onHover = () => FuelHover(__instance);
                }
                if (hopper != null)
                {
                    hopper.m_onUse = (sw, user, item) => ZoneModeChunkGeneratorMenu.OpenRecipes(__instance, user as Player);
                    hopper.m_onHover = () => RecipeHover(__instance);
                }
                nview.Register<string, int, bool>(AddFuelRpc, (sender, prefab, amount, cheated) => RPC_AddFuel(__instance, sender, prefab, amount, cheated));
                nview.Register<string>(RecipeRpc, (sender, id) => RPC_Recipe(__instance, id));
                nview.Register<string>(LuckyRpc, (sender, seed) => RPC_Lucky(__instance, seed));
                nview.Register(ForceBonusRpc, sender => RPC_ForceBonus(__instance, sender));
                ZoneModeChunkGeneratorLook.OnAwake(__instance, CurrentRecipe(nview.GetZDO()));
            }
        }

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        private static class Smelter_UpdateSmelter_ChunkGenerator
        {
            private static bool Prefix(Smelter __instance)
            {
                var nview = __instance.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || nview.GetZDO().GetPrefab() != PrefabHash) return true;
                try { Tick(__instance, nview); }
                catch (Exception ex)
                {
                    if (!s_tickFaultLogged) FiresLogger.LogWarning($"[FiresZones] Chunk Generator tick threw: {ex}");
                    s_tickFaultLogged = true;
                }
                return false;
            }
        }

        // Taken down: the owner drops every loaded and burned fuel where it stood.
        [HarmonyPatch(typeof(Smelter), "OnDestroyed")]
        private static class Smelter_OnDestroyed_ChunkGenerator
        {
            private static bool Prefix(Smelter __instance)
            {
                var nview = __instance.GetComponent<ZNetView>();
                if (!IsOurs(nview)) return true;
                if (!nview.IsOwner()) return false;
                var zdo = nview.GetZDO();
                bool cheated = zdo.GetBool(ZDOVars.s_cheatedQueued);
                var at = __instance.transform.position + Vector3.up * DropLift;
                foreach (var entry in LoadedFuel(zdo).Entries) Drop(entry.Key, entry.Value, at, cheated);
                foreach (var entry in BurnedCounts(zdo).Entries) Drop(entry.Key, entry.Value, at, cheated);
                zdo.Set(FuelHash, "");
                zdo.Set(CountsHash, "");
                zdo.Set(ZDOVars.s_queued, 0);
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
        private static class Player_HaveRequirements_ChunkGenerator
        {
            private static void Postfix(Piece piece, ref bool __result)
            {
                if (__result && s_prefab != null && piece != null && piece.gameObject == s_prefab && !ZoneModeRegistry.ModeActive)
                    __result = false;
            }
        }

        private static string FuelHover(Smelter smelter)
        {
            var zdo = smelter.GetComponent<ZNetView>()?.GetZDO();
            ZoneModeRavenHints.ShowGenerator(Player.m_localPlayer);
            return Localization.instance.Localize($"{smelter.m_name} ({Loaded(zdo)}/{Capacity})\n"
                + "[<color=yellow><b>$KEY_Use</b></color>] Add fuel");
        }

        private static string RecipeHover(Smelter smelter)
        {
            var zdo = smelter.GetComponent<ZNetView>()?.GetZDO();
            var recipe = CurrentRecipe(zdo);
            ZoneModeRavenHints.ShowGenerator(Player.m_localPlayer);
            return Localization.instance.Localize($"{smelter.m_name}: {recipe.RecipeName}\n"
                + $"{Burned(zdo)}/{ZoneModeChunkTiers.BatchFuel} burned toward {recipe.SeedsPerBatch} seed(s)\n"
                + "[<color=yellow><b>$KEY_Use</b></color>] Choose a recipe");
        }

        // ── The owner: burning, batches, fuel and recipe changes ─────────────────────────────────────────────────
        // An empty machine writes nothing; its clock restarts when fuel next arrives, so it never "catches up" on time it
        // spent empty.
        private static void Tick(Smelter smelter, ZNetView nview)
        {
            var zdo = nview.GetZDO();
            UpdateState(smelter);
            ZoneModeChunkGeneratorLook.Refresh(smelter, CurrentRecipe(zdo));
            if (!nview.IsOwner() || zdo.GetInt(ZDOVars.s_queued) <= 0) return;

            var now = ZNet.instance.GetTime();
            var since = new DateTime(zdo.GetLong(ZDOVars.s_startTime, now.Ticks));
            zdo.Set(ZDOVars.s_startTime, now.Ticks);
            float elapsed = Mathf.Min(MaxCatchUpSeconds, zdo.GetFloat(ZDOVars.s_accTime) + (float)(now - since).TotalSeconds);
            int seconds = Mathf.FloorToInt(elapsed);
            zdo.Set(ZDOVars.s_accTime, elapsed - seconds);
            if (seconds <= 0) return;

            var recipe = CurrentRecipe(zdo);
            var fuel = LoadedFuel(zdo);
            var counts = BurnedCounts(zdo);
            float bake = zdo.GetFloat(ZDOVars.s_bakeTimer);
            bool burned = false;
            for (int s = 0; s < seconds && fuel.Total > 0; s++)
            {
                bake += 1f;
                if (bake < SecondsPerFuel) continue;
                bake = 0f;
                counts.Add(fuel.TakeFirst(), 1);
                burned = true;
                if (counts.Total >= ZoneModeChunkTiers.BatchFuel) CompleteBatch(smelter, zdo, recipe, counts);
            }
            zdo.Set(ZDOVars.s_bakeTimer, fuel.Total > 0 ? bake : 0f);
            if (!burned) return;
            Write(zdo, fuel, counts);
        }

        private static void CompleteBatch(Smelter smelter, ZDO zdo, ChunkTier recipe, FuelLoad counts)
        {
            var batch = counts.TakeFirst(ZoneModeChunkTiers.BatchFuel);
            bool cheated = (zdo.GetBool(ZDOVars.s_cheatedQueued) || zdo.GetBool(ZDOVars.s_cheated)) & !PlayerProfile.s_bypassCheatChecks;
            smelter.m_produceEffects.Create(smelter.transform.position, smelter.transform.rotation);
            SpawnAtOutput(smelter, recipe.SpawnerPrefab, recipe.SeedsPerBatch, cheated);
            var pos = smelter.transform.position;
            var log = new StringBuilder($"[FiresZones] chunk generator at ({pos.x:0},{pos.z:0}): batch of {recipe.RecipeName} -> "
                + $"{recipe.SeedsPerBatch} seed(s)");
            bool forced = !recipe.RestoresOnly && zdo.GetBool(ForceBonusHash);
            if (forced) zdo.Set(ForceBonusHash, false);
            var bonus = RollBonus(recipe, batch, forced, out int luck, out float chance);
            if (luck > 0) log.Append($" (luck {luck}, bonus chance {chance * PercentScale:0.#}%)");
            if (forced) log.Append(" (bonus forced by " + ForceBonusCommand + ")");
            if (bonus != null)
            {
                SpawnAtOutput(smelter, bonus.Prefab, 1, cheated);
                smelter.GetComponent<ZNetView>().InvokeRPC(ZRoutedRpc.Everybody, LuckyRpc, bonus.Prefab);
                log.Append($"; BONUS {bonus.Name}");
            }
            FiresLogger.LogInfo(log.Append('.').ToString());
        }

        // Each burned fuel from a later biome than the recipe's adds its tier gap to the batch's luck; the chance is
        // BonusPerTierStep per point, capped. A winning roll picks the fuel biome weighted by luck, and the seed is the
        // forge's location for (recipe biome, fuel biome). Never a unique. A forced roll (the admin test command) always
        // wins; with no later-biome fuel in the batch it picks any other biome at random.
        private static ChunkSeed RollBonus(ChunkTier recipe, FuelLoad batch, bool forced, out int luck, out float chance)
        {
            luck = 0;
            chance = 0f;
            if (recipe.RestoresOnly) return null;
            foreach (var entry in batch.Entries) luck += Gap(recipe, entry.Key) * entry.Value;
            chance = Mathf.Min(BonusCap, luck * BonusPerTierStep);
            if (luck <= 0)
                return forced ? BonusSeed(recipe.Level, OtherBiome(recipe.Level)) : null;
            if (!forced && UnityEngine.Random.value >= chance) return null;
            int pick = UnityEngine.Random.Range(0, luck);
            foreach (var entry in batch.Entries)
            {
                pick -= Gap(recipe, entry.Key) * entry.Value;
                if (pick < 0) return BonusSeed(recipe.Level, ZoneModeChunkTiers.ItemLevel(entry.Key));
            }
            return null;
        }

        private static int OtherBiome(int level)
        {
            var levels = new List<int>();
            foreach (var tier in ZoneModeChunkTiers.All)
                if (!tier.RestoresOnly && tier.Level != level) levels.Add(tier.Level);
            return levels[UnityEngine.Random.Range(0, levels.Count)];
        }

        // ── The admin test command: the next biome batch of the generator you look at wins its hidden roll ──────
        private static void RegisterCommand()
        {
            if (s_commandRegistered) return;
            s_commandRegistered = true;
            ZoneModeChunkGeneratorTest.Register();
            new Terminal.ConsoleCommand(ForceBonusCommand,
                "Admin test: the next biome-recipe batch of the Chunk Generator you look at (or the nearest within "
                + $"{CommandReach:0} m) wins its hidden location-seed roll.",
                args =>
                {
                    if (!AdminSyncing.IsLocalAdmin()) { args.Context.AddString("Admins only."); return; }
                    var machine = TargetMachine(Player.m_localPlayer);
                    if (machine == null) { args.Context.AddString($"No Chunk Generator in sight or within {CommandReach:0} m."); return; }
                    var zdo = machine.GetComponent<ZNetView>().GetZDO();
                    var recipe = CurrentRecipe(zdo);
                    RequestBonus(machine);
                    args.Context.AddString(recipe.RestoresOnly
                        ? $"Armed, but it is on {recipe.RecipeName}, which never rolls: pick a biome recipe at its hopper."
                        : $"Armed: its next {recipe.RecipeName} batch drops a location seed.");
                });
        }

        private static Smelter TargetMachine(Player player)
        {
            if (player == null) return null;
            var hovered = player.GetHoverObject();
            var aimed = hovered != null ? hovered.GetComponentInParent<Smelter>() : null;
            if (aimed != null && IsOurs(aimed.GetComponent<ZNetView>())) return aimed;
            Smelter nearest = null;
            float best = CommandReach * CommandReach;
            foreach (var smelter in UnityEngine.Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None))
            {
                if (!IsOurs(smelter.GetComponent<ZNetView>())) continue;
                float distance = (smelter.transform.position - player.transform.position).sqrMagnitude;
                if (distance > best) continue;
                best = distance;
                nearest = smelter;
            }
            return nearest;
        }

        private static void RPC_ForceBonus(Smelter smelter, long sender)
        {
            var nview = smelter.GetComponent<ZNetView>();
            if (!nview.IsOwner()) return;
            if (!AdminSyncing.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[FiresZones] {ForceBonusCommand} from a non-admin ({sender}) refused.");
                return;
            }
            nview.GetZDO().Set(ForceBonusHash, true);
            var pos = smelter.transform.position;
            FiresLogger.LogInfo($"[FiresZones] chunk generator at ({pos.x:0},{pos.z:0}): the next biome batch's bonus is forced by {ForceBonusCommand}.");
        }

        private static int Gap(ChunkTier recipe, string item) => Mathf.Max(0, ZoneModeChunkTiers.ItemLevel(item) - recipe.Level);

        private static ChunkSeed BonusSeed(int primary, int second)
        {
            if (s_bonusSeeds.Count == 0)
                foreach (var seed in ZoneModeSeeds.All)
                    if (seed.IsLocation && !seed.Unique && seed.Second != null)
                        s_bonusSeeds[PairKey(seed.Tier.Level, seed.Second.Level)] = seed;
            return s_bonusSeeds.TryGetValue(PairKey(primary, second), out var found) ? found : null;
        }

        private static int PairKey(int primary, int second) => primary * ZoneModeChunkTiers.All.Length + second;

        private static void RPC_AddFuel(Smelter smelter, long sender, string prefab, int amount, bool cheated)
        {
            var nview = smelter.GetComponent<ZNetView>();
            if (!nview.IsOwner() || amount <= 0) return;
            var zdo = nview.GetZDO();
            var fuel = LoadedFuel(zdo);
            bool burnable = s_fuel.ContainsKey(prefab) && ZoneModeChunkTiers.Burns(CurrentRecipe(zdo), prefab);
            int take = burnable ? Mathf.Min(amount, Capacity - fuel.Total) : 0;
            if (take > 0)
            {
                if (fuel.Total == 0)
                {
                    zdo.Set(ZDOVars.s_startTime, ZNet.instance.GetTime().Ticks);
                    zdo.Set(ZDOVars.s_accTime, 0f);
                    zdo.Set(ZDOVars.s_bakeTimer, 0f);
                }
                fuel.Add(prefab, take);
                if (cheated) zdo.Set(ZDOVars.s_cheatedQueued, true);
                Write(zdo, fuel, BurnedCounts(zdo));
                smelter.m_oreAddedEffects.Create(smelter.transform.position, smelter.transform.rotation);
            }
            if (amount > take) Drop(prefab, amount - take, Beside(smelter), cheated);

            // One line per delivery on the owner (Fire's bot chunk-filling runs, via the lead): what, from whom, and where the batch stands.
            var pos = smelter.transform.position;
            FiresLogger.LogInfo($"[FiresZones] chunk generator at ({pos.x:0},{pos.z:0}): +{take} {prefab} from {Sender(sender)}"
                + (amount > take ? $" ({amount - take} set beside it)" : "")
                + $" (loaded {LoadedFuel(zdo).Total}/{Capacity}, burned {Burned(zdo)}/{ZoneModeChunkTiers.BatchFuel}).");
        }

        private static string Sender(long sender)
        {
            if (ZNet.instance == null || sender == ZDOMan.GetSessionID()) return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "this peer";
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            return peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : $"peer {sender}";
        }

        private static void RPC_Recipe(Smelter smelter, string id)
        {
            var nview = smelter.GetComponent<ZNetView>();
            if (!nview.IsOwner()) return;
            var zdo = nview.GetZDO();
            var from = CurrentRecipe(zdo);
            var to = ZoneModeChunkTiers.ByRecipeId(id);
            if (to == from) return;
            var fuel = LoadedFuel(zdo);
            var counts = BurnedCounts(zdo);
            bool burningDropped = fuel.Total > 0 && !ZoneModeChunkTiers.Burns(to, fuel.First);
            var refunds = new FuelLoad(true);
            fuel.MoveOut(item => !ZoneModeChunkTiers.Burns(to, item), refunds);
            counts.MoveOut(item => !ZoneModeChunkTiers.Burns(to, item), refunds);
            bool cheated = zdo.GetBool(ZDOVars.s_cheatedQueued);
            var at = Beside(smelter);
            foreach (var entry in refunds.Entries) Drop(entry.Key, entry.Value, at, cheated);
            zdo.Set(RecipeHash, to.Id);
            if (burningDropped) zdo.Set(ZDOVars.s_bakeTimer, 0f);
            Write(zdo, fuel, counts);
            var pos = smelter.transform.position;
            FiresLogger.LogInfo($"[FiresZones] chunk generator at ({pos.x:0},{pos.z:0}): recipe {from.RecipeName} -> {to.RecipeName}; "
                + $"{refunds.Total} fuel(s) it cannot burn set beside it, {fuel.Total} loaded and {counts.Total} burned carry over.");
        }

        // The raven explains the lucky seed to whoever stood near enough to see it drop, once per character.
        private static void RPC_Lucky(Smelter smelter, string seed)
        {
            var player = Player.m_localPlayer;
            if (player == null || Vector3.Distance(player.transform.position, smelter.transform.position) > LuckyHintRange) return;
            ZoneModeRavenHints.ShowLucky(player);
        }

        private static void Write(ZDO zdo, FuelLoad fuel, FuelLoad counts)
        {
            zdo.Set(FuelHash, fuel.ToString());
            zdo.Set(CountsHash, counts.ToString());
            zdo.Set(ZDOVars.s_queued, fuel.Total);
            if (fuel.Total == 0 && counts.Total == 0) zdo.Set(ZDOVars.s_cheatedQueued, false);
        }

        private static void SpawnAtOutput(Smelter smelter, string prefabName, int count, bool cheated)
        {
            if (!ZoneModePrefabs.TryGet(prefabName, out var prefab)) return;
            var at = smelter.m_outputPoint != null ? smelter.m_outputPoint : smelter.transform;
            var drop = UnityEngine.Object.Instantiate(prefab, at.position, at.rotation).GetComponent<ItemDrop>();
            drop.m_itemData.m_stack = count;
            ItemDrop.OnCreateNew(drop, cheated);
        }

        internal static Vector3 Beside(Smelter smelter)
            => smelter.transform.position + smelter.transform.right * DropSide + Vector3.up * DropLift;

        private static void Drop(string prefabName, int amount, Vector3 at, bool cheated)
        {
            var item = s_fuel.TryGetValue(prefabName, out var known) ? known : ObjectDB.instance?.GetItemPrefab(prefabName)?.GetComponent<ItemDrop>();
            if (item == null) return;
            int max = Mathf.Max(1, item.m_itemData.m_shared.m_maxStackSize);
            while (amount > 0)
            {
                int stack = Mathf.Min(amount, max);
                amount -= stack;
                var drop = UnityEngine.Object.Instantiate(item.gameObject, at + UnityEngine.Random.insideUnitSphere * DropSpread,
                    Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f)).GetComponent<ItemDrop>();
                drop.m_itemData.m_stack = stack;
                ItemDrop.OnCreateNew(drop, cheated);
            }
        }

        // ── For generator_test: fuel sent the way the fuel window sends it (marked cheated), and the owner's counts set ──
        internal static void LoadForTest(Smelter smelter, string prefab, int amount)
        {
            var nview = smelter != null ? smelter.GetComponent<ZNetView>() : null;
            if (IsOurs(nview) && amount > 0) nview.InvokeRPC(AddFuelRpc, prefab, amount, true);
        }

        internal static void PrimeBatchForTest(ZDO zdo, string prefab, int burned)
        {
            var counts = new FuelLoad(true);
            counts.Add(prefab, burned);
            Write(zdo, LoadedFuel(zdo), counts);
        }

        internal static void EmptyForTest(ZDO zdo) => Write(zdo, new FuelLoad(false), new FuelLoad(true));

        // ── Old per-biome generators become the one machine where they stand (Fire, 2026-09-27) ──────────────────
        // Once per world load for the live objects, and for every parked zone as it comes back (ZoneModePark).
        internal static void ServerTick()
        {
            if (s_liveSwapDone || s_prefab == null || ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;
            if (!ZoneModeRegistry.WorldLoaded) return;
            s_liveSwapDone = true;
            var found = new List<ZDO>();
            foreach (var tier in s_legacy.Values)
            {
                int index = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(tier.MachinePrefab, found, ref index)) { }
            }
            int swapped = 0;
            foreach (var zdo in found) if (SwapLegacy(zdo)) swapped++;
            if (swapped > 0) FiresLogger.LogInfo($"[FiresZones] {swapped} old biome chunk generator(s) became the Chunk Generator where they stood.");
        }

        internal static void Reset() => s_liveSwapDone = false;

        private static void OnZoneRestored(Vector2s zone, List<ZDO> restored)
        {
            if (s_prefab == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var old = restored.FindAll(zdo => zdo != null && s_legacy.ContainsKey(zdo.GetPrefab()));
            int swapped = 0;
            foreach (var zdo in old) if (SwapLegacy(zdo)) swapped++;
            if (swapped > 0)
                FiresLogger.LogInfo($"[FiresZones] ({zone.x},{zone.y}) came back with {swapped} old biome chunk generator(s); each became the Chunk Generator.");
        }

        // A new machine at the same spot and facing, already on that biome's recipe, carrying the old queue and counts;
        // whatever the recipe cannot burn now lies beside it. Then the old object goes.
        private static bool SwapLegacy(ZDO old)
        {
            if (old == null || !old.IsValid() || !s_legacy.TryGetValue(old.GetPrefab(), out var tier)) return false;
            var created = NetworkObjectHelper.CreateZdo(s_prefab, old.GetPosition(), old.GetRotation());
            if (created == null) return false;
            var fuel = new FuelLoad(false);
            var refunds = new FuelLoad(true);
            int queued = old.GetInt(ZDOVars.s_queued);
            for (int i = 0; i < queued; i++)
            {
                string item = old.GetString(LegacyQueueKey + i);
                if (string.IsNullOrEmpty(item)) continue;
                if (ZoneModeChunkTiers.Burns(tier, item) && fuel.Total < Capacity) fuel.Add(item, 1);
                else refunds.Add(item, 1);
            }
            var counts = BurnedCounts(old);
            counts.MoveOut(item => !ZoneModeChunkTiers.Burns(tier, item), refunds);
            created.Set(ZDOVars.s_creator, old.GetLong(ZDOVars.s_creator));
            created.Set(RecipeHash, tier.Id);
            created.Set(ZDOVars.s_cheatedQueued, old.GetBool(ZDOVars.s_cheatedQueued));
            Write(created, fuel, counts);
            var at = old.GetPosition();
            foreach (var entry in refunds.Entries) DropZdo(entry.Key, entry.Value, at + old.GetRotation() * Vector3.right * DropSide);
            old.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(old);
            FiresLogger.LogInfo($"[FiresZones] the old {tier.MachineName} at ({at.x:0},{at.y:0},{at.z:0}) is now the Chunk Generator on "
                + $"{tier.RecipeName}: {fuel.Total} loaded and {counts.Total} burned carried over, {refunds.Total} set beside it.");
            return true;
        }

        // Away from any player the swap has no instance to drop items from, so the items are written as objects.
        private static void DropZdo(string prefabName, int amount, Vector3 at)
        {
            var item = s_fuel.TryGetValue(prefabName, out var known) ? known : ObjectDB.instance?.GetItemPrefab(prefabName)?.GetComponent<ItemDrop>();
            if (item == null) return;
            int max = Mathf.Max(1, item.m_itemData.m_shared.m_maxStackSize);
            while (amount > 0)
            {
                int stack = Mathf.Min(amount, max);
                var zdo = NetworkObjectHelper.CreateZdo(item.gameObject, at + Vector3.up * DropLift, Quaternion.identity);
                if (zdo == null) return;
                zdo.Set(ZDOVars.s_stack, stack);
                amount -= stack;
            }
        }

        // A "Prefab:count;Prefab:count" list. Loaded fuel keeps the order it was added in, so an item may come back after
        // another; a tally (the batch counts, the refunds) keeps one entry per item.
        internal sealed class FuelLoad
        {
            private const char EntrySeparator = ';';
            private const char CountSeparator = ':';

            private readonly List<KeyValuePair<string, int>> _entries = new List<KeyValuePair<string, int>>();
            private readonly bool _tally;

            internal FuelLoad(bool tally) { _tally = tally; }

            internal int Total { get; private set; }

            internal IEnumerable<KeyValuePair<string, int>> Entries => _entries;

            internal string First => _entries.Count > 0 ? _entries[0].Key : null;

            internal static FuelLoad Read(string text, bool tally)
            {
                var load = new FuelLoad(tally);
                if (string.IsNullOrEmpty(text)) return load;
                foreach (string entry in text.Split(EntrySeparator))
                {
                    int colon = entry.LastIndexOf(CountSeparator);
                    if (colon <= 0 || !int.TryParse(entry.Substring(colon + 1), out int count) || count <= 0) continue;
                    load.Add(entry.Substring(0, colon), count);
                }
                return load;
            }

            internal void Add(string item, int count)
            {
                if (string.IsNullOrEmpty(item) || count <= 0) return;
                int last = _entries.Count - 1;
                int index = _tally ? _entries.FindIndex(e => e.Key == item) : last >= 0 && _entries[last].Key == item ? last : -1;
                if (index >= 0) _entries[index] = new KeyValuePair<string, int>(item, _entries[index].Value + count);
                else _entries.Add(new KeyValuePair<string, int>(item, count));
                Total += count;
            }

            internal string TakeFirst()
            {
                var first = _entries[0];
                if (first.Value > 1) _entries[0] = new KeyValuePair<string, int>(first.Key, first.Value - 1);
                else _entries.RemoveAt(0);
                Total--;
                return first.Key;
            }

            internal FuelLoad TakeFirst(int count)
            {
                var taken = new FuelLoad(true);
                while (count > 0 && Total > 0)
                {
                    var first = _entries[0];
                    int take = Mathf.Min(count, first.Value);
                    taken.Add(first.Key, take);
                    if (take == first.Value) _entries.RemoveAt(0);
                    else _entries[0] = new KeyValuePair<string, int>(first.Key, first.Value - take);
                    Total -= take;
                    count -= take;
                }
                return taken;
            }

            internal void MoveOut(Predicate<string> leaves, FuelLoad into)
            {
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    if (!leaves(_entries[i].Key)) continue;
                    into.Add(_entries[i].Key, _entries[i].Value);
                    Total -= _entries[i].Value;
                    _entries.RemoveAt(i);
                }
            }

            public override string ToString()
            {
                var text = new StringBuilder();
                foreach (var entry in _entries)
                {
                    if (text.Length > 0) text.Append(EntrySeparator);
                    text.Append(entry.Key).Append(CountSeparator).Append(entry.Value);
                }
                return text.ToString();
            }
        }
    }
}
