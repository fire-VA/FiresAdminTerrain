using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Cave pickables that never come back in vanilla - stones, bone piles, crystals, the random treasure finds, the core
    // stands and hangers - go on the cave restock clock. Once one is picked (its object is gone, or it is flagged picked)
    // it returns at its recorded spot after its own roll of in-game days, and only while no player is near. Mushrooms,
    // flint, glow worms and the rest regrow by themselves and are left to vanilla.
    internal static class VoxelCavePickables
    {
        private const float TickSeconds = 30f;

        private static readonly Dictionary<GameObject, bool> s_restockable = new Dictionary<GameObject, bool>();
        private static readonly List<VoxelCaveRecord.Pickable> s_scratch = new List<VoxelCaveRecord.Pickable>();
        private static float s_nextTick;

        // Stays picked forever in vanilla: a Pickable with no regrow time, or a PickableItem (always removed on pick).
        internal static bool IsRestockable(GameObject prefab)
        {
            if (prefab == null) return false;
            if (s_restockable.TryGetValue(prefab, out bool known)) return known;
            var pickable = prefab.GetComponent<Pickable>();
            bool restockable = pickable != null ? pickable.m_respawnTimeMinutes <= 0f : prefab.GetComponent<PickableItem>() != null;
            s_restockable[prefab] = restockable;
            return restockable;
        }

        internal static void Track(ZDO zdo, GameObject prefab, Vector3 at, Quaternion rotation, float scale, CaveContentKind kind)
        {
            if (zdo == null || !CaveContentConfig.PickablesRestock || !IsRestockable(prefab)) return;
            VoxelCaveRecord.AddPickable(new VoxelCaveRecord.Pickable
            {
                Prefab = prefab.name,
                Position = at,
                Rotation = rotation,
                Scale = scale,
                Kind = kind,
                Id = zdo.m_uid,
            });
        }

        internal static void ServerTick()
        {
            FatUndergroundLocations.ServerTick();   // 0.2.832: the delayed surface checks over underground locations
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + TickSeconds;
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || ZNetScene.instance == null) return;
            if (!CaveContentConfig.PickablesRestock || ZNet.World == null || !VoxelCaveRecord.Bound) return;

            long now = CaveRestockClock.NowMilliseconds();
            int started = 0, returned = 0;
            s_scratch.Clear();
            s_scratch.AddRange(VoxelCaveRecord.Pickables);
            foreach (var item in s_scratch)
            {
                if (!VoxelCaveRecord.InOpenZone(item.Position)) continue;
                var zdo = VoxelCaveRecord.Resolve(item);
                if (item.RestockAtMs == 0)
                {
                    if (zdo != null && !zdo.GetBool(ZDOVars.s_picked)) continue;
                    item.RestockAtMs = now + CaveRestockClock.RollMilliseconds(
                        CaveContentConfig.PickableRestockMinDays, CaveContentConfig.PickableRestockMaxDays);
                    VoxelCaveRecord.MarkDirty();
                    started++;
                    continue;
                }
                if (now < item.RestockAtMs || CaveRestockClock.PlayerNear(item.Position) || !Return(item, zdo)) continue;
                item.RestockAtMs = 0;
                VoxelCaveRecord.MarkDirty();
                returned++;
            }
            if (returned > 0)
                FiresLogger.LogInfo($"[FiresVoxel] cave pickables: {returned} back at their spots"
                    + (started > 0 ? $"; {started} newly picked start their clock" : "") + ".");
        }

        // A pickable that hides its visual when picked is un-flagged in place; one that removed itself is placed again.
        private static bool Return(VoxelCaveRecord.Pickable item, ZDO zdo)
        {
            if (zdo != null)
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                zdo.Set(ZDOVars.s_picked, false);
                return true;
            }
            var prefab = ZNetScene.instance.GetPrefab(item.Prefab);
            if (prefab == null) return false;
            var spawned = VoxelCaveContent.Spawn(prefab, item.Position, item.Rotation, item.Scale, item.Kind);
            if (spawned == null) return false;
            item.Id = spawned.m_uid;
            return true;
        }
    }
}
