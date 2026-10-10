using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // The world's first Chunk Hammer lies in a chest IN THE OPEN, close by the spawn (Fire, 2026-09-27,
    // Tools\FAT_START_ITEMS.md): on a converted map on the nearest spot a player can stand near the spawn's height, build
    // floors included; on a fresh world on flat natural ground OpenMinDistance to OpenMaxDistance from the spawn, within
    // OpenMaxRise of its height, where the least stands around it. It has a spot of its own, never tied to the starter
    // Chunk Generator's (Fire, 2026-09-27: "the hammer in one spot the chest in another"): it stands at least
    // ZoneModeStartSpots.ObjectsApart from the starter, and the log names both spots and the distance between them.
    // Every player's map is pinned (ZoneModeStartPins). The hidden search this chest used to make (a cave under the
    // start, or among the trees) now places the Cultivator chest (ZoneModeCultivatorChest).
    // The chest and the hammer in it cannot be destroyed (ZoneModeChunkMachines.IsProtected). Once the hammer is taken
    // the chest goes, and it returns to the same spot the next in-game day with a new hammer.
    internal static class ZoneModeHammerChest
    {
        private const string ChestPrefab = "TreasureChest_trollcave";
        private const float TickSeconds = 2f;
        private const float RestockCheckSeconds = 10f;
        private const float StarterWaitSeconds = 300f;
        private const float TempleOffset = 6f;
        private const int PlacementRule = 2;
        // A converted map's chest placed in front of the starter (rule 2) moves once to a spot of its own.
        private const int ConvertedPlacementRule = 3;

        private const float ObjectClearance = 2f;
        private const float OpenMinDistance = 8f;
        private const float OpenMaxDistance = 15f;
        private const float OpenBestDistance = 10f;
        private const float OpenMaxRise = 4f;
        private const float OpenStep = 1.5f;
        private const float OpenBuildClearance = 3f;
        private const float OpenCoverRadius = 5f;
        private const float OpenDistancePenalty = 0.2f;
        private const int SurveyZones = 1;
        // Half the space a chest needs round its spot.
        private const float ChestClearance = 0.6f;
        private const float NearestMinDistance = 4f;
        private const float NearestMaxDistance = 72f;
        private const float NearestStep = 4f;
        private const int NearestMinSteps = 8;

        internal static readonly int HammerChestHash = "fires_hammer_chest".GetStableHashCode();

        internal static string Status => !s_done ? "hammer chest pending"
            : s_respawnDay >= 0 ? $"hammer chest returns day {s_respawnDay}" : "hammer chest stocked";
        private static readonly int HammerHash = ZoneModePrefabs.ChunkHammerPrefab.GetStableHashCode();

        private static readonly List<ZDO> s_nearby = new List<ZDO>();
        private static bool s_done;
        private static bool s_markerRead;
        private static int s_markerRule;
        private static float s_nextTick;
        private static float s_nextRestockCheck;
        private static float s_waitingSince = -1f;
        private static Vector3 s_chestAt;
        private static int s_respawnDay = -1;

        // Where the chest stands, for the map pins; false until it has been placed.
        internal static bool TryGetSpot(out Vector3 at)
        {
            at = s_chestAt;
            return s_done;
        }

        internal static void ServerTick()
        {
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + TickSeconds;
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || ZDOMan.instance == null || ZoneSystem.instance == null || ZNetScene.instance == null) return;
            if (!ZoneModeRegistry.ModeActive || !ZoneModeRegistry.Seeded || !ZoneModeRegistry.WorldLoaded) return;
            if (!s_markerRead)
            {
                s_markerRead = true;
                s_done = ReadMarker();
                if (s_done && s_markerRule < RuleForThisWorld()) Retire();
            }
            if (!ZoneModePrefabs.TryGet(ZoneModePrefabs.ChunkHammerPrefab, out var hammer)) return;
            if (s_done)
            {
                Restock(hammer);
                return;
            }
            if (!ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out var temple)) return;
            if (!ZoneModeStartSpawn.TryGet(temple.m_position, out var spawn)) return;
            if (!GenWaterField.EnsureWaterKnown()) return;
            if (s_waitingSince < 0f) s_waitingSince = Time.time;
            s_starterStands = ZoneModeChunkMachines.TryGetStarter(out s_starterAt, out _);

            if (ZoneModePark.Converted)
            {
                if (!s_starterStands && Time.time - s_waitingSince < StarterWaitSeconds) return;
                if (ZoneModeStartSpots.TryRingSpot(spawn, ZoneModeStartSpots.SpawnMinRing, ZoneModeStartSpots.PastTemple(ZoneModeStartSpots.SpawnMaxRing),
                        ZoneModeStartSpots.SpawnRingStep, out var near, ChestClearance, s_starterAt, s_starterStands ? ZoneModeStartSpots.ObjectsApart : 0f))
                {
                    Place(near, hammer, $"{Mathf.Sqrt(ZoneModeStartSpots.FlatDistanceSquared(near, spawn)):0} m from the spawn, {ZoneModeStartSpots.LastSupport} "
                        + $"({ZoneModeStartSpots.LastRefusals} nearer the spawn)");
                    return;
                }
                FiresLogger.LogInfo($"[FiresZones] no spot within {ZoneModeStartSpots.SpawnMaxRing:0} m of the spawn holds the first Chunk Hammer's chest "
                    + $"({ZoneModeStartSpots.LastRefusals}); trying natural ground.");
            }
            else
            {
                ZoneModeStartGround.Survey(spawn, SurveyZones);
                ZoneModeStartSpots.CollectCover(spawn, SurveyZones);
                if (TryOpenSpot(spawn, out var open))
                {
                    Place(open, hammer, $"in the open {Mathf.Sqrt(ZoneModeStartSpots.FlatDistanceSquared(open, spawn)):0} m "
                        + $"{ZoneModeStartSpots.Bearing(spawn, open)} of the spawn");
                    return;
                }
            }

            ZoneModeStartGround.Survey(spawn, SurveyZones);
            if (TryNearestNatural(spawn, out var natural))
                Place(natural, hammer, $"on the natural ground nearest the spawn, {ZoneModeStartSpots.Bearing(spawn, natural)} of it: "
                    + "no open spot was found close by");
            else
            {
                FiresLogger.LogWarning("[FiresZones] the start has no ground to stand the first Chunk Hammer's chest on; it stands beside the StartTemple.");
                Place(BesideTemple(temple.m_position), hammer, "beside the StartTemple: nothing else near the spawn could hold it");
            }
        }

        // A chest placed by an older rule (in a cave or hidden among trees) that still holds its hammer is taken away, so
        // this rule places it again in the open; one whose hammer is gone is placed afresh at once.
        private static void Retire()
        {
            var chest = FindChest();
            if (chest != null)
            {
                chest.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(chest);
            }
            FiresLogger.LogInfo($"[FiresZones] the first Chunk Hammer's chest at ({s_chestAt.x:0},{s_chestAt.y:0},{s_chestAt.z:0}) was placed by an "
                + "older rule; it moves into the open by the spawn.");
            s_done = false;
            s_respawnDay = -1;
        }

        // On flat natural ground OpenMinDistance to OpenMaxDistance from the spawn and within OpenMaxRise of its height,
        // where the least stands around it: the chest is meant to be seen.
        private static bool TryOpenSpot(Vector3 spawn, out Vector3 at)
        {
            at = default;
            float best = float.MaxValue;
            for (float radius = OpenMinDistance; radius <= OpenMaxDistance; radius += OpenStep)
            {
                int steps = Mathf.CeilToInt(2f * Mathf.PI * radius / OpenStep);
                for (int k = 0; k < steps; k++)
                {
                    float angle = k * 2f * Mathf.PI / steps;
                    if (!ZoneModeStartSpots.TryNaturalSpot(spawn.x + Mathf.Cos(angle) * radius, spawn.z + Mathf.Sin(angle) * radius,
                            OpenBuildClearance, ObjectClearance, out var candidate)) continue;
                    if (Mathf.Abs(candidate.y - spawn.y) > OpenMaxRise || !ApartFromStarter(candidate)) continue;
                    float score = ZoneModeStartSpots.CoverAround(candidate, OpenCoverRadius) + Mathf.Abs(radius - OpenBestDistance) * OpenDistancePenalty;
                    if (score >= best) continue;
                    best = score;
                    at = candidate;
                }
            }
            return best < float.MaxValue;
        }

        // The starter Chunk Generator, when it stands, keeps the chest ObjectsApart away.
        private static bool s_starterStands;
        private static Vector3 s_starterAt;

        private static bool ApartFromStarter(Vector3 at)
            => !s_starterStands || ZoneModeStartSpots.FlatDistanceSquared(at, s_starterAt) >= ZoneModeStartSpots.ObjectsApart * ZoneModeStartSpots.ObjectsApart;

        private static int RuleForThisWorld() => ZoneModePark.Converted ? ConvertedPlacementRule : PlacementRule;

        // The natural ground nearest the spawn, ring by ring.
        private static bool TryNearestNatural(Vector3 spawn, out Vector3 at)
        {
            at = default;
            for (float radius = NearestMinDistance; radius <= NearestMaxDistance; radius += NearestStep)
            {
                int steps = Mathf.Max(NearestMinSteps, Mathf.CeilToInt(2f * Mathf.PI * radius / NearestStep));
                for (int k = 0; k < steps; k++)
                {
                    float angle = k * 2f * Mathf.PI / steps;
                    if (ZoneModeStartSpots.TryNaturalSpot(spawn.x + Mathf.Cos(angle) * radius, spawn.z + Mathf.Sin(angle) * radius,
                            OpenBuildClearance, ObjectClearance, out at) && ApartFromStarter(at))
                        return true;
                }
            }
            return false;
        }

        // Once the hammer is taken the chest goes; it returns to the same spot the next in-game day with a new hammer.
        // A chest that vanished any other way returns the same way.
        private static void Restock(GameObject hammer)
        {
            if (Time.time < s_nextRestockCheck) return;
            s_nextRestockCheck = Time.time + RestockCheckSeconds;
            var env = EnvMan.instance;
            if (env == null) return;
            int day = env.GetDay();
            var chest = FindChest();
            if (chest != null)
            {
                if (HoldsHammer(chest.GetByteArray(ZDOVars.s_items)))
                {
                    if (s_respawnDay >= 0) { s_respawnDay = -1; WriteMarker(); }
                    return;
                }
                chest.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(chest);
                s_respawnDay = day + 1;
                WriteMarker();
                FiresLogger.LogInfo($"[FiresZones] the first Chunk Hammer was taken; its chest is gone and returns on day {s_respawnDay} with a new one.");
                return;
            }
            if (s_respawnDay < 0)
            {
                s_respawnDay = day + 1;
                WriteMarker();
                FiresLogger.LogInfo($"[FiresZones] the Chunk Hammer chest is missing; it returns on day {s_respawnDay} with a new hammer.");
                return;
            }
            if (day < s_respawnDay) return;
            s_respawnDay = -1;
            Place(s_chestAt, hammer, $"back in its place for day {day}");
        }

        private static ZDO FindChest()
        {
            s_nearby.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(s_chestAt), new SimulationDistance(1, 0), s_nearby);
            foreach (var zdo in s_nearby)
                if (zdo != null && zdo.GetBool(HammerChestHash, false)) return zdo;
            return null;
        }

        // Valheim 1.0 stores each item by its prefab's stable hash (ItemDrop.ItemData.Save), never by its name.
        internal static bool HoldsHammer(byte[] items) => HoldsItem(items, HammerHash);

        internal static bool HoldsItem(byte[] items, int itemHash)
        {
            if (items == null) return false;
            for (int i = 0; i + sizeof(int) <= items.Length; i++)
                if (BitConverter.ToInt32(items, i) == itemHash) return true;
            return false;
        }

        private static Vector3 BesideTemple(Vector3 temple)
        {
            var at = temple + new Vector3(TempleOffset, 0f, 0f);
            at.y = VoxelZoneStreamer.TryGroundHeight(at.x, at.z, out float ground) ? ground : temple.y;
            return at;
        }

        // A vanilla chest ZDO whose inventory is written before any peer builds it, so it never rolls its own loot.
        private static void Place(Vector3 at, GameObject hammer, string where)
        {
            s_done = true;
            s_chestAt = at;
            s_markerRule = RuleForThisWorld();
            if (!ZoneModeStartChests.TryPlace(ChestPrefab, at, hammer, HammerChestHash))
            {
                FiresLogger.LogWarning($"[FiresZones] '{ChestPrefab}' is not a networked prefab here: the first Chunk Hammer has no chest this session.");
                return;
            }
            WriteMarker();
            ZoneModeStartPins.Changed();
            s_starterStands = ZoneModeChunkMachines.TryGetStarter(out s_starterAt, out _);
            string starter = s_starterStands
                ? $"; the starter Chunk Generator stands at ({s_starterAt.x:0},{s_starterAt.y:0},{s_starterAt.z:0}), "
                  + $"{Mathf.Sqrt(ZoneModeStartSpots.FlatDistanceSquared(at, s_starterAt)):0.0} m away"
                : "; the starter Chunk Generator does not stand yet";
            FiresLogger.LogInfo($"[FiresZones] the first Chunk Hammer waits in a chest at ({at.x:0},{at.y:0},{at.z:0}), {where}{starter}.");
        }

        // firesgen_<world>_hammer.txt: the chest's spot, when it was recorded, the day it returns if it is gone, and the
        // placement rule it was placed by.
        private static void WriteMarker()
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath()));
                FiresCore.IO.AtomicFile.WriteAllText(MarkerPath(),
                    $"{s_chestAt.x.ToString("0.0", inv)} {s_chestAt.y.ToString("0.0", inv)} {s_chestAt.z.ToString("0.0", inv)} {DateTime.UtcNow:O}\n"
                    + (s_respawnDay >= 0 ? $"respawn {s_respawnDay}\n" : "")
                    + $"rule {s_markerRule}\n");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] could not record the Chunk Hammer chest: {ex.Message}"); }
        }

        private static bool ReadMarker()
        {
            try
            {
                if (!File.Exists(MarkerPath())) return false;
                var lines = File.ReadAllLines(MarkerPath());
                if (lines.Length == 0) return false;
                var parts = lines[0].Split(' ');
                if (parts.Length < 3) return false;
                var inv = CultureInfo.InvariantCulture;
                s_chestAt = new Vector3(float.Parse(parts[0], inv), float.Parse(parts[1], inv), float.Parse(parts[2], inv));
                s_respawnDay = -1;
                s_markerRule = 1;
                foreach (var line in lines)
                {
                    if (line.StartsWith("respawn ") && int.TryParse(line.Substring(8).Trim(), out int day)) s_respawnDay = day;
                    if (line.StartsWith("rule ") && int.TryParse(line.Substring(5).Trim(), out int rule)) s_markerRule = rule;
                }
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] could not read the Chunk Hammer chest record ({ex.Message}); placing the chest afresh.");
                return false;
            }
        }

        private static string MarkerPath()
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(ZNet.World?.m_name ?? "world", "_hammer.txt");
        }

        internal static void Reset()
        {
            s_done = false;
            s_markerRead = false;
            s_markerRule = 0;
            s_nextTick = 0f;
            s_nextRestockCheck = 0f;
            s_waitingSince = -1f;
            s_respawnDay = -1;
            s_chestAt = default;
            s_starterStands = false;
            s_starterAt = default;
        }
    }
}
