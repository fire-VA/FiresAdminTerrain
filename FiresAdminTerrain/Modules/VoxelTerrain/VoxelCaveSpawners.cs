using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Cave monsters come from vanilla dungeon spawner prefabs (their creature, respawn timer, levels and trigger range
    // unchanged), placed by the server in caverns big enough for the creature each one spawns. A cave spawner fires
    // only for a player who is underground near it, and only once that peer's voxel ground under it is solid.
    internal static class VoxelCaveSpawners
    {
        private const float CreatureHeadMargin = 0.5f;
        private const float CreatureSideMargin = 0.5f;
        private const float GroundSettleSeconds = 3f;
        private const float UndergroundDepth = 1.5f;
        private const float TickSeconds = 30f;

        private static readonly List<int> s_order = new List<int>();
        private static readonly List<VoxelCaveRecord.Spawner> s_scratch = new List<VoxelCaveRecord.Spawner>();
        private static float s_nextTick;
        private static readonly List<Vector3> s_placed = new List<Vector3>();
        private static readonly Dictionary<GameObject, Vector2> s_creatureSizes = new Dictionary<GameObject, Vector2>();

        private static readonly AccessTools.FieldRef<CreatureSpawner, ZNetView> s_spawnerView =
            AccessTools.FieldRefAccess<CreatureSpawner, ZNetView>("m_nview");

        internal static void PlaceForZone(Vector2s zone, List<VoxelCaveContent.Spot> spots, System.Random rng, ref CaveDressTally tally)
        {
            if (!CaveContentConfig.MonstersEnabled || spots.Count == 0) return;
            if (rng.NextDouble() >= CaveContentConfig.SpawnerZoneChance) return;
            int budget = CaveContentConfig.SpawnersPerZone;
            float spacing = CaveContentConfig.SpawnerSpacing;
            s_placed.Clear();
            s_order.Clear();
            for (int i = 0; i < spots.Count; i++) s_order.Add(i);
            for (int i = s_order.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (s_order[i], s_order[j]) = (s_order[j], s_order[i]);
            }
            foreach (int index in s_order)
            {
                if (budget <= 0) break;
                var spot = spots[index];
                if (TooClose(spot.Floor, spacing) || Zones.ZoneModeStartCave.Contains(spot.Floor)) continue;
                var table = CaveContentConfig.For(spot.Biome);
                if (table == null) continue;
                if (!table.Spawners.TryPick(rng, spawner => CreatureFits(spawner, spot), out var chosen)) continue;
                var size = CreatureSize(chosen);
                var zdo = VoxelCaveContent.Spawn(chosen, spot.Floor, VoxelCaveContent.RandomYaw(rng), 1f, CaveContentKind.Spawner);
                if (zdo == null) continue;
                VoxelCaveRecord.AddSpawner(chosen.name, spot.Floor, zdo.m_uid);
                VoxelCaveContent.ClaimBigItem(spot.Floor, size.x);
                s_placed.Add(spot.Floor);
                tally.Spawners++;
                budget--;
            }
        }

        // Vanilla dungeon spawners fire once and never again. A cave spawner whose creature has died re-arms on the cave
        // restock clock, while no player is near; spawners with their own vanilla respawn timer are left to it.
        internal static void ServerTick()
        {
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + TickSeconds;
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || ZNetScene.instance == null) return;
            if (!CaveContentConfig.MonstersEnabled || !CaveContentConfig.MonstersRespawn || ZNet.World == null || !VoxelCaveRecord.Bound) return;

            long now = CaveRestockClock.NowMilliseconds();
            int rearmed = 0;
            s_scratch.Clear();
            s_scratch.AddRange(VoxelCaveRecord.Spawners);
            foreach (var entry in s_scratch)
            {
                if (!VoxelCaveRecord.InOpenZone(entry.Position)) continue;
                var zdo = VoxelCaveRecord.Resolve(entry);
                if (zdo == null)
                {
                    VoxelCaveRecord.RemoveSpawner(entry);
                    continue;
                }
                var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                var vanilla = prefab != null ? prefab.GetComponentInChildren<CreatureSpawner>(true) : null;
                if (vanilla == null || vanilla.m_respawnTimeMinuts > 0f) continue;
                var creature = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned);
                if (creature.IsNone() || ZDOMan.instance.GetZDO(creature) != null) continue;
                if (CaveRestockClock.PlayerNear(zdo.GetPosition())) continue;
                if (entry.RespawnAtMs == 0)
                {
                    entry.RespawnAtMs = now + CaveRestockClock.RollMilliseconds(CaveContentConfig.MonsterRespawnMinDays, CaveContentConfig.MonsterRespawnMaxDays);
                    VoxelCaveRecord.MarkDirty();
                    continue;
                }
                if (now < entry.RespawnAtMs) continue;
                zdo.SetOwner(ZDOMan.GetSessionID());
                zdo.SetConnection(ZDOExtraData.ConnectionType.None, ZDOID.None);
                entry.RespawnAtMs = 0;
                VoxelCaveRecord.MarkDirty();
                rearmed++;
            }
            if (rearmed > 0) FiresLogger.LogInfo($"[FiresVoxel] cave monsters: {rearmed} spawner(s) re-armed after their creature died.");
        }

        internal static bool MaySpawn(CreatureSpawner spawner)
        {
            var view = s_spawnerView(spawner);
            var zdo = view != null ? view.GetZDO() : null;
            if (zdo == null || zdo.GetInt(VoxelCaveContent.CaveContentKey) != (int)CaveContentKind.Spawner) return true;
            var at = spawner.transform.position;
            var zone = ZoneSystem.GetZone(at);
            if (VoxelZoneStreamer.IsZoneDataOnly(zone) || !VoxelZoneStreamer.IsZoneSolidFor(zone, GroundSettleSeconds)) return false;
            var local = Player.m_localPlayer;
            if (local != null && !FiresCore.Lifecycle.PlayerSpawnGate.IsReadyForCustomDataWrite(local)) return false;
            return UndergroundPlayerNear(at, spawner.m_triggerDistance);
        }

        private static bool UndergroundPlayerNear(Vector3 at, float range)
        {
            float reach = range * range;
            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null) continue;
                var p = player.transform.position;
                if ((p - at).sqrMagnitude > reach) continue;
                if (VoxelCaveContent.IsInsideRock(p, UndergroundDepth)) return true;
            }
            return false;
        }

        private static bool TooClose(Vector3 at, float spacing)
        {
            float reach = spacing * spacing;
            foreach (var other in s_placed)
                if ((other - at).sqrMagnitude < reach) return true;
            return false;
        }

        private static bool CreatureFits(GameObject spawnerPrefab, VoxelCaveContent.Spot spot)
        {
            var size = CreatureSize(spawnerPrefab);
            if (size.x <= 0f) return false;
            return VoxelCaveContent.FitsBigItem(spot, size.x + CreatureSideMargin, size.y + CreatureHeadMargin, VoxelCaveContent.SpawnerFloorRelief);
        }

        // Radius (x) and standing height (y) of the creature a spawner prefab makes, from the creature's capsule.
        internal static Vector2 CreatureSize(GameObject spawnerPrefab)
        {
            if (spawnerPrefab == null) return Vector2.zero;
            if (s_creatureSizes.TryGetValue(spawnerPrefab, out var cached)) return cached;
            var spawner = spawnerPrefab.GetComponentInChildren<CreatureSpawner>(true);
            var creature = spawner != null ? spawner.m_creaturePrefab : null;
            var size = Vector2.zero;
            if (creature != null)
            {
                CapsuleCollider body = creature.GetComponent<CapsuleCollider>();
                if (body == null)
                    foreach (var capsule in creature.GetComponentsInChildren<CapsuleCollider>(true))
                        if (!capsule.isTrigger && (body == null || capsule.height * capsule.radius > body.height * body.radius))
                            body = capsule;
                if (body != null)
                {
                    var scale = body.transform.lossyScale;
                    float radius = body.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                    float length = body.height * Mathf.Abs(body.direction == 1 ? scale.y : scale.z);
                    size = body.direction == 1
                        ? new Vector2(radius, Mathf.Max(length, radius * 2f))
                        : new Vector2(Mathf.Max(radius, length * 0.5f), radius * 2f);
                }
            }
            s_creatureSizes[spawnerPrefab] = size;
            return size;
        }
    }

    [HarmonyPatch(typeof(CreatureSpawner), "UpdateSpawner")]
    internal static class Patch_CreatureSpawner_UpdateSpawner_CaveGate
    {
        private static bool Prefix(CreatureSpawner __instance) => VoxelCaveSpawners.MaySpawn(__instance);
    }
}
