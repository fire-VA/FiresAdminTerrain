using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Cave chests: vanilla TreasureChest prefabs whose loot the server rolls from the chest's own DropTable, the way a
    // location chest fills. Once a chest is looted (its contents change, or it removes itself when emptied) it restocks
    // after its own roll of in-game days, and only while no player is close enough to have it loaded.
    internal static class VoxelCaveChests
    {
        private const float TickSeconds = 30f;
        private const float ChestHeadroom = 1.6f;
        private const uint FnvOffset = 2166136261u;
        private const uint FnvPrime = 16777619u;

        private static readonly List<VoxelCaveRecord.Chest> s_scratch = new List<VoxelCaveRecord.Chest>();
        private static float s_nextTick;

        internal static void PlaceForZone(Vector2s zone, List<VoxelCaveContent.Spot> spots, System.Random rng, ref CaveDressTally tally)
        {
            if (!CaveContentConfig.ChestsEnabled || rng.NextDouble() >= CaveContentConfig.ChestChance) return;
            int best = -1;
            GameObject bestPrefab = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < spots.Count; i++)
            {
                var spot = spots[i];
                var table = CaveContentConfig.For(spot.Biome);
                if (table == null || !table.Chests.TryPick(rng, out var prefab)) continue;
                var footprint = VoxelCaveContent.Footprint(prefab);
                if (!VoxelCaveContent.FitsBigItem(spot, footprint.x, Mathf.Max(footprint.y, ChestHeadroom), VoxelCaveContent.ChestFloorRelief)) continue;
                float score = Mathf.Min(spot.Headroom, CaveContentConfig.ChestIdealHeadroom) + spot.Cover * 0.05f
                    + (float)rng.NextDouble();
                if (score <= bestScore) continue;
                best = i;
                bestScore = score;
                bestPrefab = prefab;
            }
            if (best < 0) return;
            var at = spots[best].Floor;
            float yaw = (float)rng.NextDouble() * 360f;
            var chest = new VoxelCaveRecord.Chest { Prefab = bestPrefab.name, Position = at, Yaw = yaw };
            var zdo = CreateStockedChest(chest);
            if (zdo == null) return;
            VoxelCaveRecord.AddChest(chest);
            VoxelCaveContent.ClaimBigItem(at, VoxelCaveContent.Footprint(bestPrefab).x);
            tally.Chests++;
        }

        // The streamer's cave tick: zones waiting for their ground heights retry, and pickables share the restock.
        internal static void ServerTick()
        {
            VoxelCaveContent.RetryWaiting();
            VoxelCavePickables.ServerTick();
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + TickSeconds;
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || ZNetScene.instance == null) return;
            if (!CaveContentConfig.ChestsEnabled || ZNet.World == null || !VoxelCaveRecord.Bound) return;

            long now = CaveRestockClock.NowMilliseconds();
            int started = 0, restocked = 0, recreated = 0;
            s_scratch.Clear();
            s_scratch.AddRange(VoxelCaveRecord.Chests);
            foreach (var chest in s_scratch)
            {
                if (!VoxelCaveRecord.InOpenZone(chest.Position)) continue;
                var zdo = VoxelCaveRecord.Resolve(chest);
                if (chest.RestockAtMs == 0)
                {
                    if (zdo != null && ItemsHash(zdo) == chest.StockedHash) continue;
                    chest.RestockAtMs = now + CaveRestockClock.RollMilliseconds(CaveContentConfig.RestockMinDays, CaveContentConfig.RestockMaxDays);
                    VoxelCaveRecord.MarkDirty();
                    started++;
                    continue;
                }
                if (now < chest.RestockAtMs || CaveRestockClock.PlayerNear(chest.Position)) continue;
                if (zdo == null)
                {
                    if (CreateStockedChest(chest) == null) continue;
                    recreated++;
                }
                else
                {
                    TopUp(zdo, chest);
                    restocked++;
                }
                chest.RestockAtMs = 0;
                VoxelCaveRecord.MarkDirty();
            }
            VoxelCaveContent.ReportIfDue();
            if (restocked + recreated > 0)
                FiresLogger.LogInfo($"[FiresVoxel] cave chests: {restocked} restocked, {recreated} rebuilt after being emptied"
                    + (started > 0 ? $"; {started} newly looted chest(s) start their clock" : "") + ".");
        }

        private static ZDO CreateStockedChest(VoxelCaveRecord.Chest chest)
        {
            var prefab = ZNetScene.instance.GetPrefab(chest.Prefab);
            var container = prefab != null ? prefab.GetComponentInChildren<Container>(true) : null;
            if (container == null) return null;
            var zdo = VoxelCaveContent.Spawn(prefab, chest.Position, Quaternion.Euler(0f, chest.Yaw, 0f), 1f, CaveContentKind.Chest);
            if (zdo == null) return null;
            var inventory = NewInventory(container);
            AddRolledLoot(container, inventory);
            WriteInventory(zdo, inventory);
            chest.Id = zdo.m_uid;
            chest.StockedHash = ItemsHash(zdo);
            return zdo;
        }

        // A restock adds a fresh roll to whatever is left, so nothing a player stored in the chest is ever removed.
        private static void TopUp(ZDO zdo, VoxelCaveRecord.Chest chest)
        {
            var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            var container = prefab != null ? prefab.GetComponentInChildren<Container>(true) : null;
            if (container == null) return;
            zdo.SetOwner(ZDOMan.GetSessionID());
            var inventory = NewInventory(container);
            var stored = zdo.GetByteArray(ZDOVars.s_items);
            if (stored != null) inventory.Load(new ZPackage(stored));
            AddRolledLoot(container, inventory);
            WriteInventory(zdo, inventory);
            chest.StockedHash = ItemsHash(zdo);
        }

        private static Inventory NewInventory(Container container)
            => new Inventory(container.m_name, container.m_bkg, container.m_width, container.m_height);

        private static void AddRolledLoot(Container container, Inventory inventory)
        {
            foreach (var item in container.m_defaultItems.GetDropListItems()) inventory.AddItem(item);
        }

        private static void WriteInventory(ZDO zdo, Inventory inventory)
        {
            var package = new ZPackage();
            inventory.Save(package);
            zdo.Set(ZDOVars.s_items, package.GetArray());
            zdo.Set(ZDOVars.s_addedDefaultItems, true);
        }

        // A chest vanilla would still fill with its default items when opened counts as holding items.
        internal static bool HoldsItems(ZDO zdo)
        {
            var bytes = zdo.GetByteArray(ZDOVars.s_items);
            if (bytes == null) return !zdo.GetBool(ZDOVars.s_addedDefaultItems);
            var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            var container = prefab != null ? prefab.GetComponentInChildren<Container>(true) : null;
            if (container == null) return true;
            var inventory = NewInventory(container);
            inventory.Load(new ZPackage(bytes));
            return inventory.NrOfItems() > 0;
        }

        internal static bool IsAsStocked(ZDO zdo, VoxelCaveRecord.Chest chest) => ItemsHash(zdo) == chest.StockedHash;

        private static int ItemsHash(ZDO zdo)
        {
            var bytes = zdo.GetByteArray(ZDOVars.s_items);
            if (bytes == null) return 0;
            uint hash = FnvOffset;
            unchecked
            {
                for (int i = 0; i < bytes.Length; i++) hash = (hash ^ bytes[i]) * FnvPrime;
            }
            return (int)hash;
        }
    }
}
