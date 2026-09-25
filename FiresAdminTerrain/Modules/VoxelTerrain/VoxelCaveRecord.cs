using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Server-side memory of the caves: which zones are dressed (and under which content version), every restocking chest's
    // state, every restocking pickable's spot and every re-arming monster spawner's clock. Lives beside the world's other
    // FiresWorldGen records, so a zone is dressed once per world and restock clocks survive restarts, even while an
    // object is gone.
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
            public ZDOID Id;
            public long RespawnAtMs;
        }

        private const string ZoneTag = "Z";
        private const string ChestTag = "C";
        private const string PickableTag = "P";
        private const string SpawnerTag = "S";

        private static readonly Dictionary<Vector2s, int> s_zones = new Dictionary<Vector2s, int>();
        private static readonly List<Chest> s_chests = new List<Chest>();
        private static readonly List<Pickable> s_pickables = new List<Pickable>();
        private static readonly List<Spawner> s_spawners = new List<Spawner>();
        private static string s_loadedFor;
        private static bool s_dirty;

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

        internal static void AddSpawner(ZDOID id)
        {
            EnsureLoaded();
            s_spawners.Add(new Spawner { Id = id });
            s_dirty = true;
        }

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

        internal static void SaveIfDirty()
        {
            if (!s_dirty || s_loadedFor == null) return;
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
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}",
                        SpawnerTag, spawner.Id.UserID, spawner.Id.ID, spawner.RespawnAtMs));
                string path = RecordPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] could not save the cave record: {ex.Message}"); }
        }

        internal static void Reset()
        {
            s_zones.Clear();
            s_chests.Clear();
            s_pickables.Clear();
            s_spawners.Clear();
            s_loadedFor = null;
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
                && long.TryParse(parts[6], NumberStyles.Integer, inv, out long user)
                && uint.TryParse(parts[7], NumberStyles.Integer, inv, out uint id)
                && int.TryParse(parts[8], NumberStyles.Integer, inv, out int stocked)
                && long.TryParse(parts[9], NumberStyles.Integer, inv, out long restockAt))
            {
                s_chests.Add(new Chest
                {
                    Prefab = parts[1],
                    Position = new Vector3(x, y, z),
                    Yaw = yaw,
                    Id = new ZDOID(user, id),
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
                && long.TryParse(parts[11], NumberStyles.Integer, inv, out long pickUser)
                && uint.TryParse(parts[12], NumberStyles.Integer, inv, out uint pickId)
                && long.TryParse(parts[13], NumberStyles.Integer, inv, out long pickRestockAt))
            {
                s_pickables.Add(new Pickable
                {
                    Prefab = parts[1],
                    Position = new Vector3(px, py, pz),
                    Rotation = new Quaternion(qx, qy, qz, qw),
                    Scale = scale,
                    Kind = (CaveContentKind)kind,
                    Id = new ZDOID(pickUser, pickId),
                    RestockAtMs = pickRestockAt,
                });
                return;
            }
            if (parts.Length >= 4 && parts[0] == SpawnerTag
                && long.TryParse(parts[1], NumberStyles.Integer, inv, out long spawnerUser)
                && uint.TryParse(parts[2], NumberStyles.Integer, inv, out uint spawnerId)
                && long.TryParse(parts[3], NumberStyles.Integer, inv, out long respawnAt))
                s_spawners.Add(new Spawner { Id = new ZDOID(spawnerUser, spawnerId), RespawnAtMs = respawnAt });
        }

        private static string RecordPath()
        {
            string safe = string.Concat((s_loadedFor ?? "world").Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "FiresWorldGen", "firesgen_" + safe + "_caves.txt");
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
