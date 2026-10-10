using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using FiresCore.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Server-side memory of the caves: which zones are dressed (and under which content version), every restocking chest's
    // state, every restocking pickable's spot and every re-arming monster spawner's clock. Lives beside the world's other
    // FiresWorldGen records, so a zone is dressed once per world and restock clocks survive restarts, even while an
    // object is gone. It is written when the world is saved, so a crash rolls it back with the world.
    //
    // An entry's object is found by its prefab and spot, the id being only a shortcut within one session: 1.0 gives every
    // ZDO a new id at each load (ZDO.Load: m_uid.SetID(++ZDOID.m_loadID), user "unknown former"). Until 0.2.625 the ids
    // were trusted across restarts, found nothing, and the restock rebuilt pickables and chests beside the originals; the
    // first load binds every entry again and removes those copies where that is safe. Tools\SAVE_SAFETY_AUDIT.md
    internal static class VoxelCaveRecord
    {
        internal sealed class Chest
        {
            public string Prefab;
            public Vector3 Position;
            public float Yaw;
            public ZDOID Id;
            public int StockedHash;
            public long RestockAtMs;
        }

        internal sealed class Pickable
        {
            public string Prefab;
            public Vector3 Position;
            public Quaternion Rotation;
            public float Scale;
            public CaveContentKind Kind;
            public ZDOID Id;
            public long RestockAtMs;
        }

        internal sealed class Spawner
        {
            public string Prefab;
            public Vector3 Position;
            public ZDOID Id;
            public long RespawnAtMs;
        }

        private const string ZoneTag = "Z";
        private const string ChestTag = "C";
        private const string PickableTag = "P";
        private const string SpawnerTag = "S";
        private const int LegacySpawnerParts = 4;
        private const int SpawnerParts = 8;
        private const float SameSpot = 0.05f;

        private static readonly Dictionary<Vector2s, int> s_zones = new Dictionary<Vector2s, int>();
        private static readonly List<Chest> s_chests = new List<Chest>();
        private static readonly List<Pickable> s_pickables = new List<Pickable>();
        private static readonly List<Spawner> s_spawners = new List<Spawner>();
        private static readonly List<ZDO> s_copies = new List<ZDO>();
        private static string s_loadedFor;
        private static string s_boundFor;
        private static int s_legacySpawnerLines;
        private static bool s_dirty;

        // The restock ticks wait for this, so none of them acts on an entry before the load has bound it.
        internal static bool Bound => s_boundFor != null && s_boundFor == ZNet.World?.m_name;

        internal static List<Chest> Chests
        {
            get
            {
                EnsureLoaded();
                return s_chests;
            }
        }

        internal static List<Pickable> Pickables
        {
            get
            {
                EnsureLoaded();
                return s_pickables;
            }
        }

        internal static List<Spawner> Spawners
        {
            get
            {
                EnsureLoaded();
                return s_spawners;
            }
        }

        internal static void AddPickable(Pickable pickable)
        {
            EnsureLoaded();
            s_pickables.Add(pickable);
            s_dirty = true;
        }

        internal static void AddSpawner(string prefab, Vector3 position, ZDOID id)
        {
            EnsureLoaded();
            s_spawners.Add(new Spawner { Prefab = prefab, Position = position, Id = id });
            s_dirty = true;
        }

        // A one-zone-at-a-time world parks a closed zone's objects, so its entries find nothing until it opens again: the
        // restock leaves them alone, instead of rebuilding into a zone that is parked.
        internal static bool InOpenZone(Vector3 position) => Zones.ZoneModeRegistry.IsOpen(ZoneSystem.GetZone(position));

        internal static ZDO Resolve(Chest chest) => Resolve(ref chest.Id, chest.Prefab, chest.Position);

        internal static ZDO Resolve(Pickable pickable) => Resolve(ref pickable.Id, pickable.Prefab, pickable.Position);

        internal static ZDO Resolve(Spawner spawner) => Resolve(ref spawner.Id, spawner.Prefab, spawner.Position);

        // The id while it still names the object, else the cave object of the prefab at the spot, which becomes the id.
        private static ZDO Resolve(ref ZDOID id, string prefab, Vector3 position)
        {
            var zdo = id.IsNone() ? null : ZDOMan.instance.GetZDO(id);
            if (zdo != null) return zdo;
            var copies = CopiesAt(prefab, position);
            zdo = copies.Count > 0 ? copies[0] : null;
            id = zdo != null ? zdo.m_uid : ZDOID.None;
            return zdo;
        }

        // Only objects FAT's cave dressing made (tagged CaveContentKey), so a player's chest on the spot is never taken.
        private static List<ZDO> CopiesAt(string prefab, Vector3 position)
        {
            s_copies.Clear();
            if (string.IsNullOrEmpty(prefab)) return s_copies;
            int hash = prefab.GetStableHashCode();
            foreach (var zdo in VoxelCaveContent.ZoneObjects(ZoneSystem.GetZone(position)))
                if (zdo.GetPrefab() == hash && zdo.GetInt(VoxelCaveContent.CaveContentKey) != 0
                    && (zdo.GetPosition() - position).sqrMagnitude <= SameSpot * SameSpot)
                    s_copies.Add(zdo);
            return s_copies;
        }

        // From FAT's ZNet.Start hook, once the server's world is loaded. Copies at one spot: extra pickables go; an extra chest
        // goes only when it holds nothing. A restock clock the lost id started on an untouched object is stopped.
        internal static void BindAfterLoad()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;
            EnsureLoaded();
            if (s_loadedFor == null || s_boundFor == s_loadedFor) return;
            var clock = Stopwatch.StartNew();
            int entries = s_chests.Count + s_pickables.Count + s_spawners.Count;
            int bound = 0, pickableCopies = 0, emptyChests = 0, chestsWithItems = 0, clocksStopped = 0;
            foreach (var chest in s_chests)
            {
                var copies = CopiesAt(chest.Prefab, chest.Position);
                chest.Id = ZDOID.None;
                if (copies.Count == 0) continue;
                var kept = copies.FirstOrDefault(VoxelCaveChests.HoldsItems) ?? copies[0];
                chest.Id = kept.m_uid;
                bound++;
                foreach (var copy in copies.Where(c => c != kept).ToList())
                {
                    if (VoxelCaveChests.HoldsItems(copy)) { chestsWithItems++; continue; }
                    VoxelCaveContent.DestroyServerZdo(copy);
                    emptyChests++;
                }
                if (chest.RestockAtMs == 0 || !VoxelCaveChests.IsAsStocked(kept, chest)) continue;
                chest.RestockAtMs = 0;
                clocksStopped++;
            }
            foreach (var pickable in s_pickables)
            {
                var copies = CopiesAt(pickable.Prefab, pickable.Position);
                pickable.Id = ZDOID.None;
                if (copies.Count == 0) continue;
                var kept = copies.FirstOrDefault(c => !IsPicked(c)) ?? copies[0];
                pickable.Id = kept.m_uid;
                bound++;
                foreach (var copy in copies.Where(c => c != kept).ToList())
                {
                    VoxelCaveContent.DestroyServerZdo(copy);
                    pickableCopies++;
                }
                if (pickable.RestockAtMs == 0 || IsPicked(kept)) continue;
                pickable.RestockAtMs = 0;
                clocksStopped++;
            }
            int spawnersGone = s_spawners.RemoveAll(spawner => InOpenZone(spawner.Position) && Resolve(spawner) == null);
            bound += s_spawners.Count;
            s_boundFor = s_loadedFor;
            s_dirty = true;
            FiresLogger.LogInfo($"[FiresVoxel] cave record '{s_loadedFor}': re-bound {bound} of {entries} entries, removed {pickableCopies} "
                + $"duplicate pickables / {emptyChests} empty chests, dropped {s_legacySpawnerLines} stale spawner lines "
                + $"({spawnersGone} spawner(s) gone from the world, {chestsWithItems} extra chest(s) holding items left in place, "
                + $"{clocksStopped} restock clock(s) stopped) in {clock.ElapsedMilliseconds} ms.");
        }

        private static bool IsPicked(ZDO zdo) => zdo.GetBool(ZDOVars.s_picked);

        internal static void RemoveSpawner(Spawner spawner)
        {
            if (s_spawners.Remove(spawner)) s_dirty = true;
        }

        internal static bool TryGetZoneVersion(Vector2s zone, out int version)
        {
            EnsureLoaded();
            return s_zones.TryGetValue(zone, out version);
        }

        internal static void MarkZoneDressed(Vector2s zone, int version)
        {
            EnsureLoaded();
            s_zones[zone] = version;
            s_dirty = true;
        }

        internal static void AddChest(Chest chest)
        {
            EnsureLoaded();
            s_chests.Add(chest);
            s_dirty = true;
        }

        internal static void RemoveRestockingInZone(Vector2s zone)
        {
            EnsureLoaded();
            if (s_chests.RemoveAll(c => ZoneSystem.GetZone(c.Position) == zone) > 0) s_dirty = true;
            if (s_pickables.RemoveAll(p => ZoneSystem.GetZone(p.Position) == zone) > 0) s_dirty = true;
        }

        internal static void MarkDirty() => s_dirty = true;

        // From FAT's ZNet.SaveWorld hook: the moment vanilla captures the world for its save.
        internal static void SaveWithWorld()
        {
            if (!s_dirty || s_loadedFor == null || s_loadedFor != ZNet.World?.m_name) return;
            s_dirty = false;
            try
            {
                var lines = new List<string>(s_zones.Count + s_chests.Count + s_pickables.Count + s_spawners.Count);
                foreach (var kv in s_zones)
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", ZoneTag, kv.Key.x, kv.Key.y, kv.Value));
                foreach (var chest in s_chests)
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2:0.###} {3:0.###} {4:0.###} {5:0.#} {6} {7} {8} {9}",
                        ChestTag, chest.Prefab, chest.Position.x, chest.Position.y, chest.Position.z, chest.Yaw,
                        chest.Id.UserID, chest.Id.ID, chest.StockedHash, chest.RestockAtMs));
                foreach (var p in s_pickables)
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0} {1} {2:0.###} {3:0.###} {4:0.###} {5:0.#####} {6:0.#####} {7:0.#####} {8:0.#####} {9:0.###} {10} {11} {12} {13}",
                        PickableTag, p.Prefab, p.Position.x, p.Position.y, p.Position.z,
                        p.Rotation.x, p.Rotation.y, p.Rotation.z, p.Rotation.w, p.Scale, (int)p.Kind, p.Id.UserID, p.Id.ID, p.RestockAtMs));
                foreach (var spawner in s_spawners)
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2:0.###} {3:0.###} {4:0.###} {5} {6} {7}",
                        SpawnerTag, spawner.Prefab, spawner.Position.x, spawner.Position.y, spawner.Position.z,
                        spawner.Id.UserID, spawner.Id.ID, spawner.RespawnAtMs));
                string path = RecordPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AtomicFile.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                s_dirty = true;
                FiresLogger.LogWarning($"[FiresVoxel] could not save the cave record: {ex.Message}");
            }
        }

        internal static void Reset()
        {
            s_zones.Clear();
            s_chests.Clear();
            s_pickables.Clear();
            s_spawners.Clear();
            s_loadedFor = null;
            s_boundFor = null;
            s_legacySpawnerLines = 0;
            s_dirty = false;
        }

        private static void EnsureLoaded()
        {
            string world = ZNet.World?.m_name;
            if (world == null || world == s_loadedFor) return;
            Reset();
            s_loadedFor = world;
            try
            {
                string path = RecordPath();
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path)) ParseLine(line);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] could not read the cave record: {ex.Message}"); }
        }

        private static void ParseLine(string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var inv = CultureInfo.InvariantCulture;
            if (parts.Length >= 4 && parts[0] == ZoneTag
                && short.TryParse(parts[1], NumberStyles.Integer, inv, out short zx)
                && short.TryParse(parts[2], NumberStyles.Integer, inv, out short zy)
                && int.TryParse(parts[3], NumberStyles.Integer, inv, out int version))
            {
                s_zones[new Vector2s(zx, zy)] = version;
                return;
            }
            if (parts.Length >= 10 && parts[0] == ChestTag
                && float.TryParse(parts[2], NumberStyles.Float, inv, out float x)
                && float.TryParse(parts[3], NumberStyles.Float, inv, out float y)
                && float.TryParse(parts[4], NumberStyles.Float, inv, out float z)
                && float.TryParse(parts[5], NumberStyles.Float, inv, out float yaw)
                && int.TryParse(parts[8], NumberStyles.Integer, inv, out int stocked)
                && long.TryParse(parts[9], NumberStyles.Integer, inv, out long restockAt))
            {
                s_chests.Add(new Chest
                {
                    Prefab = parts[1],
                    Position = new Vector3(x, y, z),
                    Yaw = yaw,
                    Id = ZDOID.None,
                    StockedHash = stocked,
                    RestockAtMs = restockAt,
                });
                return;
            }
            if (parts.Length >= 14 && parts[0] == PickableTag
                && float.TryParse(parts[2], NumberStyles.Float, inv, out float px)
                && float.TryParse(parts[3], NumberStyles.Float, inv, out float py)
                && float.TryParse(parts[4], NumberStyles.Float, inv, out float pz)
                && float.TryParse(parts[5], NumberStyles.Float, inv, out float qx)
                && float.TryParse(parts[6], NumberStyles.Float, inv, out float qy)
                && float.TryParse(parts[7], NumberStyles.Float, inv, out float qz)
                && float.TryParse(parts[8], NumberStyles.Float, inv, out float qw)
                && float.TryParse(parts[9], NumberStyles.Float, inv, out float scale)
                && int.TryParse(parts[10], NumberStyles.Integer, inv, out int kind)
                && long.TryParse(parts[13], NumberStyles.Integer, inv, out long pickRestockAt))
            {
                s_pickables.Add(new Pickable
                {
                    Prefab = parts[1],
                    Position = new Vector3(px, py, pz),
                    Rotation = new Quaternion(qx, qy, qz, qw),
                    Scale = scale,
                    Kind = (CaveContentKind)kind,
                    Id = ZDOID.None,
                    RestockAtMs = pickRestockAt,
                });
                return;
            }
            if (parts.Length >= SpawnerParts && parts[0] == SpawnerTag
                && float.TryParse(parts[2], NumberStyles.Float, inv, out float sx)
                && float.TryParse(parts[3], NumberStyles.Float, inv, out float sy)
                && float.TryParse(parts[4], NumberStyles.Float, inv, out float sz)
                && long.TryParse(parts[7], NumberStyles.Integer, inv, out long respawnAt))
            {
                s_spawners.Add(new Spawner { Prefab = parts[1], Position = new Vector3(sx, sy, sz), Id = ZDOID.None, RespawnAtMs = respawnAt });
                return;
            }
            if (parts.Length == LegacySpawnerParts && parts[0] == SpawnerTag) s_legacySpawnerLines++;
        }

        private static string RecordPath()
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(s_loadedFor ?? "world", "_caves.txt");
        }
    }

    // The one clock cave chests, pickables and spawners restock on: in-game days, rolled per object, due only while no
    // player is close enough to have the object loaded.
    internal static class CaveRestockClock
    {
        private const float MillisecondsPerSecond = 1000f;
        private const float ZoneSize = 64f;

        internal static long NowMilliseconds() => (long)(ZNet.instance.GetTimeSeconds() * MillisecondsPerSecond);

        internal static long RollMilliseconds(float minDays, float maxDays)
        {
            float dayLength = EnvMan.instance != null ? EnvMan.instance.m_dayLengthSec : CaveContentConfig.FallbackDayLengthSeconds;
            float days = UnityEngine.Random.Range(minDays, Mathf.Max(minDays, maxDays));
            return (long)(days * dayLength * MillisecondsPerSecond);
        }

        internal static bool PlayerNear(Vector3 at)
        {
            float loaded = (FiresCore.Bridge.SimulationDistanceAccess.Near + 1) * ZoneSize;
            float clearance = Mathf.Max(loaded, CaveContentConfig.RestockClearance);
            float reach = clearance * clearance;
            var local = Player.m_localPlayer;
            if (local != null && FlatDistanceSquared(local.transform.position, at) < reach) return true;
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer != null && FlatDistanceSquared(peer.m_refPos, at) < reach) return true;
            return false;
        }

        private static float FlatDistanceSquared(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
