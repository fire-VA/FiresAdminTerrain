using System;
using System.Collections.Generic;
using FiresCore.Pieces;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // Chunk generators: prefabs baked in the "fireszones" bundle (ZoneModePrefabs), wired per tier here and listed on
    // the hammer. A generator burns no fuel: it takes any item of its recipes, counts each processed item in its ZDO,
    // and turns out a seed whenever the counts complete a recipe (ZoneModeChunkTiers). The world's starter generator
    // (Meadows) is placed by the server beside the StartTemple once that zone's ground is built, on natural ground
    // (ZoneModeStartGround: never a path, a field, a paved road or among builds); it cannot be damaged, worn or removed,
    // and the server puts it back within a minute if it is ever missing.
    internal static class ZoneModeChunkMachines
    {
        private const float SecondsPerUnit = 2f;
        private const int QueueSize = 100;
        private const float StarterCheckSeconds = 10f;
        private const float StarterRecheckSeconds = 60f;
        private const int MaxStarterReplacements = 2;
        private const float StarterClearance = 4f;
        private const float StarterBuildClearance = 6f;
        private const float StarterMaxRelief = 1.5f;
        private const float StarterMinGround = 31.5f;
        private const float FootprintRadius = 2.5f;
        private const int StarterAngles = 16;
        private const float StarterMinRadius = 30f;
        private const float StarterMaxRadius = 90f;
        private const float StarterRadiusStep = 6f;
        private const int StarterSurveyZones = 2;

        private static readonly int StarterHash = "fires_chunk_starter".GetStableHashCode();
        private static readonly int CountsHash = "fires_chunk_counts".GetStableHashCode();

        internal static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> ObjectsBySector =
            AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");

        private static readonly Dictionary<string, GameObject> s_machines = new Dictionary<string, GameObject>();
        private static readonly Dictionary<int, ChunkTier> s_tierByMachine = new Dictionary<int, ChunkTier>();
        private static readonly Dictionary<int, string[]> s_pairItems = new Dictionary<int, string[]>();
        private static readonly HashSet<string> s_reportedMissing = new HashSet<string>();
        private static int s_starterPrefabHash;

        private static bool s_starterSettled;
        private static float s_nextStarterCheck;
        private static int s_starterReplacements;
        private static string s_starterRefusal;

        internal static string StarterStatus => s_starterSettled ? "starter stands" : "starter missing";

        // ── Registration (called from FAT's existing ZNetScene.Awake / ObjectDB.Awake hooks) ─────────
        internal static void OnZNetSceneAwake()
        {
            try
            {
                ZoneModePrefabs.RegisterInScene(ZNetScene.instance);
                WireMachines();
                RegisterInObjectDB(ObjectDB.instance);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] chunk generator registration (ZNetScene) threw: {ex.Message}"); }
        }

        internal static void OnObjectDBAwake(ObjectDB db)
        {
            if (HammerPieces.IsMainMenuObjectDB(db)) return;
            try
            {
                WireMachines();
                RegisterInObjectDB(db);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] chunk generator registration (ObjectDB) threw: {ex.Message}"); }
        }

        private static void WireMachines()
        {
            foreach (var tier in ZoneModeChunkTiers.All)
            {
                if (!ZoneModePrefabs.TryGet(tier.MachinePrefab, out var machine)) continue;
                s_tierByMachine[tier.MachinePrefab.GetStableHashCode()] = tier;
                if (tier.Level == ZoneModeChunkTiers.Starter.Level) s_starterPrefabHash = tier.MachinePrefab.GetStableHashCode();
                if (WireMachine(machine, tier))
                {
                    ZoneModeRavenHints.RetargetGuidePoint(machine);
                    s_machines[tier.MachinePrefab] = machine;
                }
                else s_machines.Remove(tier.MachinePrefab);
            }
        }

        // Everything a tier decides about its generator: what it takes in, how fast and how much it holds, what it costs
        // to build and what its description says. A tier whose seed or build cost this game lacks stays off the hammer;
        // inputs this game lacks are only left out of the recipe.
        private static bool WireMachine(GameObject machine, ChunkTier tier)
        {
            if (!ZoneModePrefabs.TryGet(tier.SpawnerPrefab, out var seedPrefab)) return false;
            var seed = seedPrefab.GetComponent<ItemDrop>();
            var conversion = new List<Smelter.ItemConversion>();
            foreach (string name in ZoneModeChunkTiers.AcceptedInputs(tier))
            {
                var item = FindItem(name, tier);
                if (item != null) conversion.Add(new Smelter.ItemConversion { m_from = item, m_to = seed });
            }
            var requirements = new List<Piece.Requirement>();
            foreach (var cost in tier.Cost)
            {
                var item = FindItem(cost.Key, tier);
                if (item == null) return false;
                requirements.Add(new Piece.Requirement { m_resItem = item, m_amount = cost.Value, m_recover = true });
            }
            if (seed == null || conversion.Count < 2) return false;

            var smelter = machine.GetComponent<Smelter>();
            smelter.m_fuelItem = null;
            smelter.m_maxFuel = 0;
            smelter.m_fuelPerProduct = 0;
            smelter.m_maxOre = QueueSize;
            smelter.m_secPerProduct = SecondsPerUnit;
            smelter.m_spawnStack = false;
            smelter.m_conversion = conversion;
            if (smelter.m_addWoodSwitch != null)
            {
                if (smelter.m_addWoodSwitch.gameObject != machine) smelter.m_addWoodSwitch.gameObject.SetActive(false);
                smelter.m_addWoodSwitch = null;
            }

            var piece = machine.GetComponent<Piece>();
            piece.m_description = MachineDescription(tier);
            piece.m_resources = requirements.ToArray();
            return true;
        }

        private static string MachineDescription(ChunkTier tier)
        {
            if (tier.Level == ZoneModeChunkTiers.Starter.Level)
                return $"Feed it any two different materials from any biome, {ZoneModeChunkTiers.PairAmount} of each, and it "
                    + $"turns out a {tier.SpawnerName}. It burns no fuel.";
            var names = new List<string>();
            foreach (string item in tier.Inputs) names.Add(ItemName(item));
            string text = $"Feed it any two different {tier.Title} materials, {ZoneModeChunkTiers.PairAmount} of each";
            var previous = ZoneModeChunkTiers.ByLevel(tier.Level - 1);
            if (previous != null)
                text += $", or {ZoneModeChunkTiers.DoubleAmount} each of {ItemName(previous.BasicA)} and {ItemName(previous.BasicB)}";
            return text + $", and it turns out a {tier.SpawnerName}. It burns no fuel. {tier.Title} materials: {string.Join(", ", names)}.";
        }

        private static string ItemName(string prefab)
        {
            var shared = ObjectDB.instance?.GetItemPrefab(prefab)?.GetComponent<ItemDrop>()?.m_itemData?.m_shared;
            return shared != null ? shared.m_name : prefab;
        }

        private static void RegisterInObjectDB(ObjectDB db)
        {
            if (db == null || db.m_items == null || db.m_items.Count == 0) return;
            ZoneModePrefabs.RegisterItems(db);
            ZoneModeSeeds.Register(db);
            var table = HammerPieces.HammerTable();
            if (table == null) return;
            foreach (var tier in ZoneModeChunkTiers.All)
                if (s_machines.TryGetValue(tier.MachinePrefab, out var machine))
                    HammerPieces.AddToTable(table, machine);
        }

        private static GameObject FindPrefab(string name)
        {
            var scene = ZNetScene.instance;
            var fromScene = scene != null ? scene.GetPrefab(name) : null;
            if (fromScene != null) return fromScene;
            var db = ObjectDB.instance;
            if (db == null) return null;
            var item = db.GetItemPrefab(name);
            if (item != null) return item;
            var table = db.GetItemPrefab("Hammer")?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
            if (table != null)
                foreach (var piece in table.m_pieces)
                    if (piece != null && piece.name == name) return piece;
            return null;
        }

        private static ItemDrop FindItem(string name, ChunkTier tier)
        {
            var drop = FindPrefab(name)?.GetComponent<ItemDrop>();
            if (drop == null && ObjectDB.instance != null && ObjectDB.instance.m_items.Count > 0 && s_reportedMissing.Add(tier.Id + ":" + name))
                FiresLogger.LogWarning($"[FiresZones] {tier.MachineName}: item '{name}' does not exist in this game — that tier is left out.");
            return drop;
        }

        // ── Recipes: every processed item is counted; completed recipes turn into seeds ─────────────
        [HarmonyPatch(typeof(Smelter), "Spawn")]
        private static class Smelter_Spawn_ChunkMachine
        {
            private static bool Prefix(Smelter __instance, string ore, int stack)
            {
                var zdo = ZdoOf(__instance);
                if (zdo == null || !s_tierByMachine.TryGetValue(zdo.GetPrefab(), out var tier)) return true;
                var counts = ReadCounts(zdo);
                counts.TryGetValue(ore, out int had);
                counts[ore] = had + stack;
                int made = 0;
                while (TryCompleteRecipe(tier, counts)) made++;
                WriteCounts(zdo, counts);
                if (made > 0) SpawnSeeds(__instance, zdo, tier, made);
                return false;
            }
        }

        // One seed from any two different items of the tier's pair list at PairAmount each (the two with the most
        // counted), otherwise from DoubleAmount each of the previous tier's basic pair.
        private static bool TryCompleteRecipe(ChunkTier tier, SortedDictionary<string, int> counts)
        {
            string first = null, second = null;
            int firstCount = 0, secondCount = 0;
            foreach (string item in PairItems(tier))
            {
                counts.TryGetValue(item, out int count);
                if (count < ZoneModeChunkTiers.PairAmount) continue;
                if (count > firstCount)
                {
                    second = first;
                    secondCount = firstCount;
                    first = item;
                    firstCount = count;
                }
                else if (count > secondCount)
                {
                    second = item;
                    secondCount = count;
                }
            }
            if (first != null && second != null)
            {
                counts[first] -= ZoneModeChunkTiers.PairAmount;
                counts[second] -= ZoneModeChunkTiers.PairAmount;
                return true;
            }
            var previous = ZoneModeChunkTiers.ByLevel(tier.Level - 1);
            if (previous == null) return false;
            counts.TryGetValue(previous.BasicA, out int a);
            counts.TryGetValue(previous.BasicB, out int b);
            if (a < ZoneModeChunkTiers.DoubleAmount || b < ZoneModeChunkTiers.DoubleAmount) return false;
            counts[previous.BasicA] = a - ZoneModeChunkTiers.DoubleAmount;
            counts[previous.BasicB] = b - ZoneModeChunkTiers.DoubleAmount;
            return true;
        }

        private static string[] PairItems(ChunkTier tier)
        {
            if (s_pairItems.TryGetValue(tier.Level, out var items)) return items;
            var list = new List<string>();
            foreach (var pairList in ZoneModeChunkTiers.PairLists(tier))
                foreach (string item in pairList)
                    if (!list.Contains(item)) list.Add(item);
            return s_pairItems[tier.Level] = list.ToArray();
        }

        private static SortedDictionary<string, int> ReadCounts(ZDO zdo)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string entry in zdo.GetString(CountsHash, "").Split(';'))
            {
                int colon = entry.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(entry.Substring(colon + 1), out int count) || count <= 0) continue;
                counts[entry.Substring(0, colon)] = count;
            }
            return counts;
        }

        private static void WriteCounts(ZDO zdo, SortedDictionary<string, int> counts)
        {
            var parts = new List<string>();
            foreach (var pair in counts)
                if (pair.Value > 0) parts.Add(pair.Key + ":" + pair.Value);
            zdo.Set(CountsHash, string.Join(";", parts));
        }

        // As vanilla turns out a product: the produce effect, then the seeds at the output point.
        private static void SpawnSeeds(Smelter smelter, ZDO zdo, ChunkTier tier, int count)
        {
            if (!ZoneModePrefabs.TryGet(tier.SpawnerPrefab, out var prefab)) return;
            smelter.m_produceEffects.Create(smelter.transform.position, smelter.transform.rotation);
            var at = smelter.m_outputPoint != null ? smelter.m_outputPoint : smelter.transform;
            var drop = UnityEngine.Object.Instantiate(prefab, at.position, at.rotation).GetComponent<ItemDrop>();
            drop.m_itemData.m_stack = count;
            ItemDrop.OnCreateNew(drop, (zdo.GetBool(ZDOVars.s_cheatedQueued) || zdo.GetBool(ZDOVars.s_cheated)) & !PlayerProfile.s_bypassCheatChecks);
        }

        [HarmonyPatch(typeof(Smelter), "OnHoverAddOre")]
        private static class Smelter_OnHoverAddOre_ChunkMachine
        {
            private static void Postfix(Smelter __instance, ref string __result)
            {
                var zdo = ZdoOf(__instance);
                if (zdo == null || !s_tierByMachine.TryGetValue(zdo.GetPrefab(), out var tier)) return;
                ZoneModeRavenHints.ShowGenerator(Player.m_localPlayer);
                var parts = new List<string>();
                var previous = ZoneModeChunkTiers.ByLevel(tier.Level - 1);
                foreach (var pair in ReadCounts(zdo))
                {
                    bool doubled = previous != null && Array.IndexOf(PairItems(tier), pair.Key) < 0
                        && (pair.Key == previous.BasicA || pair.Key == previous.BasicB);
                    int need = doubled ? ZoneModeChunkTiers.DoubleAmount : ZoneModeChunkTiers.PairAmount;
                    parts.Add($"{Localization.instance.Localize(ItemName(pair.Key))} {pair.Value}/{need}");
                }
                if (parts.Count > 0) __result += "\nCounted toward the next seed: " + string.Join(", ", parts);
            }
        }

        // A generator taken down hands back every counted item that has not become a seed yet.
        [HarmonyPatch(typeof(Smelter), "OnDestroyed")]
        private static class Smelter_OnDestroyed_ChunkMachine
        {
            private static void Postfix(Smelter __instance)
            {
                var nview = __instance.GetComponent<ZNetView>();
                var zdo = ZdoOf(__instance);
                if (zdo == null || !nview.IsOwner() || !s_tierByMachine.ContainsKey(zdo.GetPrefab())) return;
                foreach (var pair in ReadCounts(zdo)) DropItems(pair.Key, pair.Value, __instance.transform.position);
                zdo.Set(CountsHash, "");
            }
        }

        private static void DropItems(string prefabName, int amount, Vector3 at)
        {
            var prefab = ObjectDB.instance?.GetItemPrefab(prefabName);
            var shared = prefab?.GetComponent<ItemDrop>()?.m_itemData?.m_shared;
            if (shared == null) return;
            int max = Mathf.Max(1, shared.m_maxStackSize);
            while (amount > 0)
            {
                int stack = Mathf.Min(amount, max);
                amount -= stack;
                var drop = UnityEngine.Object.Instantiate(prefab, at + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.3f,
                    Quaternion.Euler(0f, UnityEngine.Random.Range(0, 360), 0f)).GetComponent<ItemDrop>();
                drop.m_itemData.m_stack = stack;
                ItemDrop.OnCreateNew(drop);
            }
        }

        // Machines exist only in zone-mode worlds; each later tier is learned once the previous biome's boss
        // has fallen in this world (its global key), so the recipe shows up the way vanilla recipes do.
        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
        private static class Player_HaveRequirements_ChunkTiers
        {
            private static void Postfix(Piece piece, ref bool __result)
            {
                if (!__result || piece == null || !ZoneModeChunkTiers.TryByMachineName(piece.m_name, out var tier)) return;
                if (!ZoneModeRegistry.ModeActive) { __result = false; return; }
                if (tier.BossKey != null && (ZoneSystem.instance == null || !ZoneSystem.instance.GetGlobalKey(tier.BossKey)))
                    __result = false;
            }
        }

        // ── The starter machine cannot be worn, damaged or removed ──────────────────────────────────
        private static ZDO ZdoOf(Component component)
        {
            var nview = component != null ? component.GetComponent<ZNetView>() : null;
            return nview != null && nview.IsValid() ? nview.GetZDO() : null;
        }

        internal static bool IsStarter(Component component) => IsStarter(ZdoOf(component));

        internal static bool IsStarter(ZDO zdo)
            => zdo != null && s_starterPrefabHash != 0 && zdo.GetPrefab() == s_starterPrefabHash && zdo.GetBool(StarterHash, false);

        // The pieces a chunk world cannot lose: the starter generator and the first Chunk Hammer's chest. They take no
        // wear, no damage, no removal and no destruction.
        internal static bool IsProtected(Component component) => IsProtected(ZdoOf(component));

        internal static bool IsProtected(ZDO zdo)
            => zdo != null && (zdo.GetBool(ZoneModeHammerChest.HammerChestHash, false) || IsStarter(zdo));

        [HarmonyPatch(typeof(WearNTear), "Awake")]
        private static class WearNTear_Awake_StarterMachine
        {
            private static void Postfix(WearNTear __instance)
            {
                if (!IsProtected(__instance)) return;
                // Vanilla's wear flags read backwards: true ENABLES rain and support wear, and an unsupported piece
                // loses all its health on the first wear tick through WearNTear.Destroy, which no RPC guard sees.
                __instance.m_noRoofWear = false;
                __instance.m_noSupportWear = false;
                __instance.m_ashDamageImmune = true;
                __instance.m_snowDamageImmune = true;
                var piece = __instance.GetComponent<Piece>();
                if (piece != null) piece.m_canBeRemoved = false;
            }
        }

        [HarmonyPatch(typeof(WearNTear), "RPC_Damage")]
        private static class WearNTear_RPC_Damage_StarterMachine
        {
            private static bool Prefix(WearNTear __instance) => !IsProtected(__instance);
        }

        [HarmonyPatch(typeof(WearNTear), "RPC_Remove")]
        private static class WearNTear_RPC_Remove_StarterMachine
        {
            private static bool Prefix(WearNTear __instance) => !IsProtected(__instance);
        }

        // ── Server: place the starter beside the StartTemple, or confirm it still stands ─────────────
        internal static void ServerTick()
        {
            if (Time.time < s_nextStarterCheck) return;
            s_nextStarterCheck = Time.time + (s_starterSettled ? StarterRecheckSeconds : StarterCheckSeconds);
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || ZDOMan.instance == null || ZoneSystem.instance == null) return;
            if (!ZoneModeRegistry.Seeded || !ZoneModeRegistry.WorldLoaded) return;
            var tier = ZoneModeChunkTiers.Starter;
            if (!s_machines.TryGetValue(tier.MachinePrefab, out var prefab)) return;

            var starters = FindStarters(tier);
            if (starters.Count > 0)
            {
                if (!s_starterSettled || starters.Count > 1) KeepOneStarter(starters, tier);
                s_starterSettled = true;
                return;
            }
            if (s_starterSettled)
            {
                s_starterSettled = false;
                s_nextStarterCheck = 0f;
                if (++s_starterReplacements > MaxStarterReplacements)
                {
                    s_nextStarterCheck = float.MaxValue;
                    FiresLogger.LogError($"[FiresZones] the starter {tier.MachineName} has vanished {s_starterReplacements} times this "
                        + "session: something destroys it. It is not placed again before the next server start; fires_zones_find "
                        + "shows what stands at the start.");
                    return;
                }
                FiresLogger.LogWarning($"[FiresZones] the starter {tier.MachineName} is gone from the world; placing it again.");
            }

            if (!TryChooseStarterSpot(out var position, out var rotation, out var zone)) return;
            if (!VoxelZoneStreamer.IsZoneGenerated(zone)) return;
            var created = NetworkObjectHelper.CreateZdo(prefab, position, rotation);
            if (created == null) return;
            created.Set(StarterHash, true);
            s_starterSettled = true;
            FiresLogger.LogInfo($"[FiresZones] placed the indestructible starter {tier.MachineName} at "
                + $"({position.x:0},{position.y:0},{position.z:0}) in zone ({zone.x},{zone.y}), beside the StartTemple on unpainted "
                + "ground clear of builds.");
        }

        internal static bool TryGetStarter(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (ZDOMan.instance == null) return false;
            var starters = FindStarters(ZoneModeChunkTiers.Starter);
            if (starters.Count == 0) return false;
            position = starters[0].GetPosition();
            rotation = starters[0].GetRotation();
            return true;
        }

        private static List<ZDO> FindStarters(ChunkTier tier)
        {
            var found = new List<ZDO>();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(tier.MachinePrefab, found, ref index)) { }
            var starters = new List<ZDO>();
            var seen = new HashSet<ZDOID>();
            foreach (var zdo in found)
                if (zdo != null && seen.Add(zdo.m_uid) && zdo.GetBool(StarterHash, false)) starters.Add(zdo);
            return starters;
        }

        // One world, one indestructible starter. Extras (a start scan that ran before the save loaded placed
        // one on top of the saved one) become ordinary machines; the lowest ZDO id is the one kept.
        private static void KeepOneStarter(List<ZDO> starters, ChunkTier tier)
        {
            starters.Sort((a, b) => a.m_uid.UserID != b.m_uid.UserID ? a.m_uid.UserID.CompareTo(b.m_uid.UserID) : a.m_uid.ID.CompareTo(b.m_uid.ID));
            Vector3 kept = starters[0].GetPosition();
            for (int i = 1; i < starters.Count; i++) starters[i].Set(StarterHash, false);
            FiresLogger.LogInfo($"[FiresZones] starter {tier.MachineName} stands at ({kept.x:0},{kept.y:0},{kept.z:0})"
                + (starters.Count > 1 ? $"; {starters.Count - 1} extra starter(s) turned into ordinary machines." : "."));
        }

        // The flattest spot nearest the StartTemple on natural ground (ZoneModeStartGround): never on a path, a field or
        // a paved road, nor among builds.
        private static bool TryChooseStarterSpot(out Vector3 position, out Quaternion rotation, out Vector2s zone)
        {
            position = default;
            rotation = Quaternion.identity;
            zone = default;
            if (!ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out var temple)) return false;
            if (!GenWaterField.EnsureWaterKnown())
            {
                NoteStarterRefusal("waiting for the world's water plan before choosing the starter's ground");
                return false;
            }
            Vector3 center = temple.m_position;
            ZoneModeStartGround.Survey(center, StarterSurveyZones);
            float bestScore = float.MaxValue;
            bool found = false;
            int painted = 0, built = 0, occupied = 0, unknown = 0;
            for (float radius = StarterMinRadius; radius <= StarterMaxRadius; radius += StarterRadiusStep)
            {
                for (int k = 0; k < StarterAngles; k++)
                {
                    float angle = k * Mathf.PI * 2f / StarterAngles;
                    var point = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                    var pointZone = ZoneSystem.GetZone(point);
                    if (!ZoneModeRegistry.IsOpen(pointZone)) continue;
                    if (ZoneModeStartCave.Covers(point, FootprintRadius + 3f)) continue;
                    if (!TryFootprint(point, out float ground, out float relief)) continue;
                    if (ground < StarterMinGround) continue;
                    if (ZoneModeStartGround.UnderGeneratedWater(new Vector3(point.x, ground, point.z))) continue;
                    var standing = ZoneModeStartGround.Check(point, FootprintRadius, StarterBuildClearance, StarterClearance);
                    if (standing == ZoneModeStartGround.Ground.Painted) { painted++; continue; }
                    if (standing == ZoneModeStartGround.Ground.Built) { built++; continue; }
                    if (standing == ZoneModeStartGround.Ground.Occupied) { occupied++; continue; }
                    if (standing == ZoneModeStartGround.Ground.Unknown) { unknown++; continue; }
                    float score = relief + radius * 0.02f;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    position = new Vector3(point.x, ground, point.z);
                    zone = pointZone;
                    found = true;
                }
            }
            if (!found)
            {
                NoteStarterRefusal($"no natural ground for the starter within {StarterMaxRadius:0} m of the StartTemple yet: {painted} "
                    + $"spot(s) painted (path, field or paved road), {built} among builds, {occupied} blocked by objects, "
                    + $"{unknown} on ground still loading");
                return false;
            }
            Vector3 face = center - position;
            face.y = 0f;
            rotation = face.sqrMagnitude > 0.01f ? Quaternion.LookRotation(face.normalized) : Quaternion.identity;
            return true;
        }

        // Logged once per distinct reason, so a start that waits says why without repeating itself every check.
        private static void NoteStarterRefusal(string refusal)
        {
            if (refusal != s_starterRefusal) FiresLogger.LogInfo($"[FiresZones] {refusal}.");
            s_starterRefusal = refusal;
        }

        // Only the terrain's real ground counts. The generator's own height misses the StartTemple's levelling, and a
        // machine set on it can float, so a spot whose ground is not built yet waits for the next check.
        private static bool TryFootprint(Vector3 point, out float ground, out float relief)
        {
            ground = relief = 0f;
            float min = float.MaxValue, max = float.MinValue, sum = 0f;
            for (int i = 0; i < 5; i++)
            {
                float ox = i == 1 ? FootprintRadius : i == 2 ? -FootprintRadius : 0f;
                float oz = i == 3 ? FootprintRadius : i == 4 ? -FootprintRadius : 0f;
                if (!VoxelZoneStreamer.TryGroundHeight(point.x + ox, point.z + oz, out float h)) return false;
                min = Mathf.Min(min, h);
                max = Mathf.Max(max, h);
                sum += h;
            }
            ground = sum / 5f;
            relief = max - min;
            return relief <= StarterMaxRelief;
        }

        internal static void Reset()
        {
            s_starterSettled = false;
            s_nextStarterCheck = 0f;
            s_starterReplacements = 0;
            s_starterRefusal = null;
        }
    }
}
