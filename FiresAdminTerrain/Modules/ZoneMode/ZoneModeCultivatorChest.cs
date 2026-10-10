using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Zones
{
    // A vanilla Cultivator, hidden MinDistance to MaxDistance from the spawn (Fire, 2026-09-27, Tools\FAT_START_ITEMS.md):
    // in a cave pocket under the start where one is roomy enough (a shallow one reads as "just under the surface"),
    // otherwise above ground among the most trees, rocks and brush. Hugin points the way once the first Chunk Hammer is
    // found (ZoneModeStartPins), and the chest is pinned on the map of whoever opens it. One per world: it cannot be
    // destroyed while the Cultivator is in it, and once that is taken it is an ordinary chest.
    internal static class ZoneModeCultivatorChest
    {
        private const string ChestPrefab = "TreasureChest_meadows";
        private const string FallbackChestPrefab = "TreasureChest_trollcave";
        private const string CultivatorPrefab = "Cultivator";
        private const float TickSeconds = 2f;
        private const float TakenCheckSeconds = 10f;
        private const float SettleSeconds = 5f;
        private const float GiveUpSeconds = 180f;
        private const float MinDistance = 60f;
        private const float MaxDistance = 150f;
        private const float BestDistance = 100f;
        private const float Spacing = 4f;
        private const float MinHeadroom = 2.5f;
        private const float MinCover = 1.5f;
        private const float RoomyHeadroom = 8f;
        private const float HeadroomWeight = 2f;
        private const float DepthPenalty = 0.1f;
        private const float DistancePenalty = 0.02f;
        private const float FootprintOffset = 0.8f;
        private const float MaxFloorRelief = 0.6f;
        private const float FloorProbe = 1.5f;
        private const float CeilingGap = 0.1f;
        private const float PoolClearance = 10f;
        private const float ObjectClearance = 2f;
        private const float HideBuildClearance = 10f;
        private const float HideCoverRadius = 6f;
        private const int HideMinCover = 2;
        private const float HideDistancePenalty = 0.02f;
        private const int SearchZones = 3;

        internal static readonly int CultivatorChestHash = "fires_cultivator_chest".GetStableHashCode();
        private static readonly int CultivatorHash = CultivatorPrefab.GetStableHashCode();

        internal enum Hiding : byte { None = 0, Cave = 1, Surface = 2 }

        private struct Spot
        {
            public Vector3 At;
            public float Score;
        }

        private static readonly List<VoxelCaveContent.Void> s_voids = new List<VoxelCaveContent.Void>();
        private static readonly HashSet<Vector2s> s_scanned = new HashSet<Vector2s>();
        private static readonly List<Spot> s_spots = new List<Spot>();
        private static readonly List<Spot> s_zoneSpots = new List<Spot>();
        private static readonly List<ZDO> s_nearby = new List<ZDO>();
        private static bool s_done;
        private static bool s_taken;
        private static bool s_markerRead;
        private static float s_nextTick;
        private static float s_nextTakenCheck;
        private static float s_searchSince = -1f;
        private static Vector3 s_chestAt;
        private static Hiding s_hiding;

        // Where the chest is hidden and how, for Hugin's hint; false until it has been placed.
        internal static bool TryGetSpot(out Vector3 at, out Hiding hiding)
        {
            at = s_chestAt;
            hiding = s_hiding;
            return s_done && !s_taken;
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
            }
            if (s_done)
            {
                WatchTaken();
                return;
            }
            var cultivator = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(CultivatorPrefab) : null;
            if (cultivator == null) return;
            if (!ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out var temple)) return;
            if (!ZoneModeStartSpawn.TryGet(temple.m_position, out var spawn)) return;
            if (!GenWaterField.EnsureWaterKnown()) return;
            if (s_searchSince < 0f) s_searchSince = Time.time;

            var centre = ZoneSystem.GetZone(spawn);
            bool waiting = false;
            if (FiresVoxelConfig.GeneratesCaves)
            {
                for (int dz = -SearchZones; dz <= SearchZones; dz++)
                {
                    for (int dx = -SearchZones; dx <= SearchZones; dx++)
                    {
                        var zone = new Vector2s(centre.x + dx, centre.y + dz);
                        if (s_scanned.Contains(zone) || !ZoneModeRegistry.IsOpen(zone)) continue;
                        if (!VoxelZoneStreamer.IsZoneGenerated(zone) || !VoxelZoneStreamer.IsZoneSolidFor(zone, SettleSeconds)) { waiting = true; continue; }
                        if (!Scan(zone, spawn)) { waiting = true; continue; }
                        s_spots.AddRange(s_zoneSpots);
                        s_scanned.Add(zone);
                        return;
                    }
                }
            }
            if (waiting && Time.time - s_searchSince < GiveUpSeconds) return;

            if (TryClearCaveSpot(out var cave))
            {
                Place(cave, cultivator, Hiding.Cave, $"in a cave under the land, {Distance(spawn, cave):0} m {ZoneModeStartSpots.Bearing(spawn, cave)} of the spawn");
                return;
            }
            ZoneModeStartGround.Survey(spawn, SearchZones);
            ZoneModeStartSpots.CollectCover(spawn, SearchZones);
            if (TryHiddenSpot(spawn, out var hidden, out int cover))
            {
                Place(hidden, cultivator, Hiding.Surface, $"among {cover} tree(s), rock(s) and bush(es), {Distance(spawn, hidden):0} m "
                    + $"{ZoneModeStartSpots.Bearing(spawn, hidden)} of the spawn");
                return;
            }
            s_done = true;
            FiresLogger.LogWarning($"[FiresZones] no cave pocket and no natural ground {MinDistance:0}-{MaxDistance:0} m from the spawn could hide the "
                + "Cultivator chest; this world has none.");
            WriteMarker();
        }

        // Once the Cultivator is taken the chest is an ordinary one: it can be broken, and Hugin stops pointing at it.
        private static void WatchTaken()
        {
            if (s_taken || s_hiding == Hiding.None || Time.time < s_nextTakenCheck) return;
            s_nextTakenCheck = Time.time + TakenCheckSeconds;
            s_nearby.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(s_chestAt), new SimulationDistance(1, 0), s_nearby);
            ZDO chest = null;
            foreach (var zdo in s_nearby)
                if (zdo != null && zdo.GetBool(CultivatorChestHash, false)) chest = zdo;
            if (chest != null && ZoneModeHammerChest.HoldsItem(chest.GetByteArray(ZDOVars.s_items), CultivatorHash)) return;
            if (chest != null)
            {
                chest.SetOwner(ZDOMan.GetSessionID());
                chest.Set(CultivatorChestHash, false);
            }
            s_taken = true;
            WriteMarker();
            ZoneModeStartPins.Changed();
            FiresLogger.LogInfo("[FiresZones] the Cultivator was taken from its hidden chest; the chest is an ordinary one now.");
        }

        private static float Distance(Vector3 a, Vector3 b) => Mathf.Sqrt(ZoneModeStartSpots.FlatDistanceSquared(a, b));

        private static bool InRange(Vector3 spawn, Vector3 at)
        {
            float distance = Distance(spawn, at);
            return distance >= MinDistance && distance <= MaxDistance;
        }

        // The zone's cavern floor spots in range into s_zoneSpots. False when some column had no ground height to scan from.
        private static bool Scan(Vector2s zone, Vector3 spawn)
        {
            s_zoneSpots.Clear();
            bool complete = true;
            float minX = zone.x * ZoneOpenSet.ZoneSize - ZoneOpenSet.ZoneSize * 0.5f;
            float minZ = zone.y * ZoneOpenSet.ZoneSize - ZoneOpenSet.ZoneSize * 0.5f;
            for (float oz = Spacing * 0.5f; oz < ZoneOpenSet.ZoneSize; oz += Spacing)
            {
                for (float ox = Spacing * 0.5f; ox < ZoneOpenSet.ZoneSize; ox += Spacing)
                {
                    float wx = minX + ox, wz = minZ + oz;
                    if (!InRange(spawn, new Vector3(wx, 0f, wz))) continue;
                    if (!VoxelZoneStreamer.TryGroundHeight(wx, wz, out float surface)) { complete = false; continue; }
                    VoxelCaveContent.ScanVoids(wx, wz, surface, s_voids);
                    foreach (var pocket in s_voids)
                    {
                        if (pocket.Headroom < MinHeadroom || surface - pocket.Ceiling < MinCover) continue;
                        if (!TryFlatFloor(wx, wz, pocket, out float floor)) continue;
                        var at = new Vector3(wx, floor, wz);
                        if (FiresLiquidWaterPrefab.HasPourNear(at, PoolClearance)) continue;
                        float score = Mathf.Min(pocket.Headroom, RoomyHeadroom) * HeadroomWeight - (surface - floor) * DepthPenalty
                            - Mathf.Abs(Distance(spawn, at) - BestDistance) * DistancePenalty;
                        s_zoneSpots.Add(new Spot { At = at, Score = score });
                    }
                }
            }
            return complete;
        }

        private static bool TryFlatFloor(float wx, float wz, VoxelCaveContent.Void pocket, out float floor)
        {
            floor = 0f;
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 5; i++)
            {
                float x = wx + (i == 1 ? FootprintOffset : i == 2 ? -FootprintOffset : 0f);
                float z = wz + (i == 3 ? FootprintOffset : i == 4 ? -FootprintOffset : 0f);
                float top = Mathf.Min(pocket.Ceiling - CeilingGap, pocket.Floor + FloorProbe);
                if (!VoxelWorld.TrySampleSurfaceY(x, z, top, pocket.Floor - FloorProbe, out float y, out _)) return false;
                if (i == 0) floor = y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
            }
            return max - min <= MaxFloorRelief;
        }

        // The best-scoring cave spot with nothing within ObjectClearance: the cave dressing usually lands before the
        // chest, so its rubble, spawners and chests are avoided rather than overlapped.
        private static bool TryClearCaveSpot(out Vector3 at)
        {
            at = default;
            s_spots.Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (var spot in s_spots)
            {
                if (ZoneModeStartSpots.HasObjectNear(spot.At, ObjectClearance)) continue;
                at = spot.At;
                return true;
            }
            return false;
        }

        // The hiding place with the most cover: flat dry natural ground in an open zone, in range of the spawn, nothing
        // within ObjectClearance, nothing built within HideBuildClearance.
        private static bool TryHiddenSpot(Vector3 spawn, out Vector3 at, out int cover)
        {
            at = default;
            cover = 0;
            float best = float.MinValue;
            for (float dz = -MaxDistance; dz <= MaxDistance; dz += Spacing)
            {
                for (float dx = -MaxDistance; dx <= MaxDistance; dx += Spacing)
                {
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);
                    if (distance < MinDistance || distance > MaxDistance) continue;
                    if (!ZoneModeStartSpots.TryNaturalSpot(spawn.x + dx, spawn.z + dz, HideBuildClearance, ObjectClearance, out var candidate)) continue;
                    int around = ZoneModeStartSpots.CoverAround(candidate, HideCoverRadius);
                    if (around < HideMinCover) continue;
                    float score = around - Mathf.Abs(distance - BestDistance) * HideDistancePenalty;
                    if (score <= best) continue;
                    best = score;
                    at = candidate;
                    cover = around;
                }
            }
            return best > float.MinValue;
        }

        private static void Place(Vector3 at, GameObject cultivator, Hiding hiding, string where)
        {
            s_done = true;
            s_chestAt = at;
            s_hiding = hiding;
            if (!ZoneModeStartChests.TryPlace(ChestPrefab, at, cultivator, CultivatorChestHash)
                && !ZoneModeStartChests.TryPlace(FallbackChestPrefab, at, cultivator, CultivatorChestHash))
            {
                FiresLogger.LogWarning("[FiresZones] no chest prefab here could hold the Cultivator; this world has no Cultivator chest.");
                s_hiding = Hiding.None;
                WriteMarker();
                return;
            }
            WriteMarker();
            ZoneModeStartPins.Changed();
            FiresLogger.LogInfo($"[FiresZones] a Cultivator waits in a hidden chest at ({at.x:0},{at.y:0},{at.z:0}), {where}.");
        }

        // firesgen_<world>_cultivator.txt: the chest's spot, how it is hidden, and whether its Cultivator is gone.
        private static void WriteMarker()
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath()));
                FiresCore.IO.AtomicFile.WriteAllText(MarkerPath(),
                    $"{s_chestAt.x.ToString("0.0", inv)} {s_chestAt.y.ToString("0.0", inv)} {s_chestAt.z.ToString("0.0", inv)} {DateTime.UtcNow:O}\n"
                    + $"hiding {(byte)s_hiding}\n" + (s_taken ? "taken\n" : ""));
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] could not record the Cultivator chest: {ex.Message}"); }
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
                s_hiding = Hiding.None;
                s_taken = false;
                foreach (var line in lines)
                {
                    if (line.StartsWith("hiding ") && byte.TryParse(line.Substring(7).Trim(), out byte hiding)) s_hiding = (Hiding)hiding;
                    if (line.Trim() == "taken") s_taken = true;
                }
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] could not read the Cultivator chest record ({ex.Message}); hiding the chest afresh.");
                return false;
            }
        }

        private static string MarkerPath()
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(ZNet.World?.m_name ?? "world", "_cultivator.txt");
        }

        internal static void Reset()
        {
            s_done = false;
            s_taken = false;
            s_markerRead = false;
            s_nextTick = 0f;
            s_nextTakenCheck = 0f;
            s_searchSince = -1f;
            s_chestAt = default;
            s_hiding = Hiding.None;
            s_spots.Clear();
            s_zoneSpots.Clear();
            s_scanned.Clear();
        }
    }
}
