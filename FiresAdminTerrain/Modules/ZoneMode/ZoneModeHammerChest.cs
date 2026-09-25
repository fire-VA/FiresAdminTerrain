using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Zones
{
    // The world's first Chunk Hammer lies in a chest in the start cave, the cavern inside the hill beside the StartTemple
    // (ZoneModeStartCave). Worlds seeded without one fall back to the older search: once per world the server scans
    // the caves of the start 3x3 as each zone's voxels settle, keeps the roomiest cavern floor with rock overhead (a
    // shallower one nearer the StartTemple wins ties), and places a vanilla cave chest there holding the hammer. When no
    // start zone has a cavern (a converted world never gets the start cave), the chest is hidden above ground: on flat
    // dry natural ground 24-72 m from the StartTemple (ZoneModeStartGround: unpainted, clear of anything built), where
    // the most trees, rocks and brush stand around it. Only when no such spot exists does it stand beside the starter
    // Chunk Generator, then on the natural ground nearest the StartTemple (at once when the starter has no room beside
    // it, after five minutes when no starter stands), and beside the StartTemple itself only when the start has no
    // natural ground at all.
    // The chest and the hammer in it cannot be destroyed (ZoneModeChunkMachines.IsProtected). Once the hammer is taken
    // the chest goes, and it returns to the same spot the next in-game day with a new hammer.
    internal static class ZoneModeHammerChest
    {
        private const string ChestPrefab = "TreasureChest_trollcave";
        private const float TickSeconds = 2f;
        private const float RestockCheckSeconds = 10f;
        private const float SettleSeconds = 5f;
        private const float GiveUpSeconds = 120f;
        private const float Spacing = 4f;
        private const float MinHeadroom = 3f;
        private const float MinCover = 4f;
        private const float MaxFloorRelief = 0.6f;
        private const float FootprintOffset = 0.8f;
        private const float PoolClearance = 10f;
        private const float TempleOffset = 6f;
        private static readonly float[] StarterSides = { 4f, 6f };
        private const float StarterWaitSeconds = 300f;
        private const float NearestMinDistance = 8f;
        private const float OpenBuildClearance = 6f;

        private const float ObjectClearance = 2f;
        private const float HideMinDistance = 24f;
        private const float HideMaxDistance = 72f;
        private const float HideCoverRadius = 6f;
        private const float HideBuildClearance = 10f;
        private const int HideMinCover = 2;
        private const float HideDistancePenalty = 0.02f;
        private const float HideDryMargin = 1f;
        private const float SurfaceSettleRange = 3f;
        private const int HideSearchZones = 2;

        internal static readonly int HammerChestHash = "fires_hammer_chest".GetStableHashCode();

        internal static string Status => !s_done ? "hammer chest pending"
            : s_respawnDay >= 0 ? $"hammer chest returns day {s_respawnDay}" : "hammer chest stocked";
        private static readonly int HammerHash = ZoneModePrefabs.ChunkHammerPrefab.GetStableHashCode();

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
        private static bool s_markerRead;
        private static float s_nextTick;
        private static float s_nextRestockCheck;
        private static float s_generatedAt = -1f;
        private static float s_incompleteSince = -1f;
        private static Vector3 s_chestAt;
        private static int s_respawnDay = -1;

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
            if (!ZoneModePrefabs.TryGet(ZoneModePrefabs.ChunkHammerPrefab, out var hammer)) return;
            if (s_done)
            {
                Restock(hammer);
                return;
            }
            if (!ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out var temple)) return;

            if (ZoneModeStartCave.Active && FiresVoxelConfig.GeneratesCaves)
            {
                var cave = ZoneModeStartCave.ChamberZone;
                if (!VoxelZoneStreamer.IsZoneGenerated(cave) || !VoxelZoneStreamer.IsZoneSolidFor(cave, SettleSeconds)) return;
                Place(StartCaveSpot(), hammer, "in the start cave, inside the hill beside the StartTemple");
                return;
            }

            var center = ZoneSystem.GetZone(temple.m_position);
            bool allGenerated = true, waiting = false;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    var zone = new Vector2s(center.x + dx, center.y + dz);
                    if (s_scanned.Contains(zone)) continue;
                    if (!VoxelZoneStreamer.IsZoneGenerated(zone)) { allGenerated = false; continue; }
                    if (!VoxelZoneStreamer.IsZoneSolidFor(zone, SettleSeconds)) { waiting = true; continue; }
                    // A zone opening makes every cached ground height stale until it is rebuilt, so a scan with any
                    // column still missing its ground is retried, and only kept as it stands once GiveUpSeconds pass.
                    if (!Scan(zone, temple.m_position))
                    {
                        if (s_incompleteSince < 0f) s_incompleteSince = Time.time;
                        if (Time.time - s_incompleteSince < GiveUpSeconds) return;
                    }
                    s_incompleteSince = -1f;
                    s_spots.AddRange(s_zoneSpots);
                    s_scanned.Add(zone);
                    return;
                }
            }
            if (!allGenerated) return;
            if (s_generatedAt < 0f) s_generatedAt = Time.time;
            if (waiting && Time.time - s_generatedAt < GiveUpSeconds) return;

            if (TryClearSpot(out var spot))
            {
                Place(spot, hammer, $"in a cave under the start ({s_scanned.Count} start zone(s) scanned)");
                return;
            }
            if (!GenWaterField.EnsureWaterKnown()) return;
            ZoneModeStartGround.Survey(temple.m_position, HideSearchZones);
            if (TryHiddenSpot(temple.m_position, out var hidden, out int cover))
                Place(hidden, hammer, $"hidden above ground {Bearing(temple.m_position, hidden)} of the StartTemple among {cover} tree(s), "
                    + "rock(s) and bush(es) on unpainted ground: no clear cavern was found under the start");
            else if (TryBesideStarter(out var nearStarter, out bool starterStands))
                Place(nearStarter, hammer, "beside the starter Chunk Generator on unpainted ground: no clear cavern was found under the start");
            else if (!starterStands && Time.time - s_generatedAt < StarterWaitSeconds) return;
            else if (TryNearestNatural(temple.m_position, out var natural))
                Place(natural, hammer, $"on the unpainted ground nearest the StartTemple, {Bearing(temple.m_position, natural)} of it: no "
                    + "cavern, no hiding place and no starter generator were found");
            else
            {
                FiresLogger.LogWarning("[FiresZones] the start has no unpainted ground clear of builds within "
                    + $"{HideMaxDistance:0} m of the StartTemple; the first Chunk Hammer's chest stands beside the temple.");
                Place(BesideTemple(temple.m_position), hammer, "beside the StartTemple: no natural ground was found near the start");
            }
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
        internal static bool HoldsHammer(byte[] items)
        {
            if (items == null) return false;
            for (int i = 0; i + 4 <= items.Length; i++)
                if (BitConverter.ToInt32(items, i) == HammerHash) return true;
            return false;
        }

        // The first free floor point of the start cave, settled on the live floor when its voxels are held here; the
        // cavern's floor is flat at its site's FloorY otherwise.
        private static Vector3 StartCaveSpot()
        {
            Vector3 first = default;
            bool any = false;
            foreach (var spot in ZoneModeStartCave.ChestSpots())
            {
                var at = spot;
                if (VoxelWorld.TrySampleSurfaceY(at.x, at.z, at.y + 1.5f, at.y - 1.5f, out float y, out _)) at.y = y;
                if (!any) { first = at; any = true; }
                if (!HasObjectNear(at)) return at;
            }
            return first;
        }

        private static readonly List<Vector3> s_cover = new List<Vector3>();
        private static readonly Dictionary<int, bool> s_coverByPrefab = new Dictionary<int, bool>();

        // The hiding place with the most cover: flat dry natural ground in an open zone, HideMinDistance to
        // HideMaxDistance from the StartTemple, nothing within ObjectClearance, nothing built within HideBuildClearance.
        private static bool TryHiddenSpot(Vector3 temple, out Vector3 at, out int cover)
        {
            at = default;
            cover = 0;
            CollectCover(temple);
            float best = float.MinValue;
            for (float dz = -HideMaxDistance; dz <= HideMaxDistance; dz += Spacing)
            {
                for (float dx = -HideMaxDistance; dx <= HideMaxDistance; dx += Spacing)
                {
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);
                    if (distance < HideMinDistance || distance > HideMaxDistance) continue;
                    if (!TryNaturalSpot(temple.x + dx, temple.z + dz, HideBuildClearance, out var candidate)) continue;
                    int around = CoverAround(candidate);
                    if (around < HideMinCover) continue;
                    float score = around - distance * HideDistancePenalty;
                    if (score <= best) continue;
                    best = score;
                    at = candidate;
                    cover = around;
                }
            }
            return best > float.MinValue;
        }

        // Flat dry natural ground (ZoneModeStartGround, surveyed for this search) in an open zone, away from any pool.
        private static bool TryNaturalSpot(float wx, float wz, float buildClearance, out Vector3 spot)
        {
            spot = default;
            if (!ZoneModeRegistry.IsOpen(ZoneSystem.GetZone(new Vector3(wx, 0f, wz)))) return false;
            if (!TryFlatGround(wx, wz, out float ground) || ground < ZoneSystem.instance.m_waterLevel + HideDryMargin) return false;
            spot = new Vector3(wx, ground, wz);
            if (FiresLiquidWaterPrefab.HasPourNear(spot, PoolClearance) || ZoneModeStartGround.UnderGeneratedWater(spot)) return false;
            return ZoneModeStartGround.Check(spot, FootprintOffset, buildClearance, ObjectClearance) == ZoneModeStartGround.Ground.Natural;
        }

        // The trees, rocks, brush and ruins around the start that hide a chest. Builds, creatures, dropped items and
        // FAT's own start objects hide nothing.
        private static void CollectCover(Vector3 temple)
        {
            s_cover.Clear();
            s_nearby.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(temple), new SimulationDistance(HideSearchZones, 0), s_nearby);
            foreach (var zdo in s_nearby)
            {
                if (zdo == null || ZoneModeChunkMachines.IsProtected(zdo) || ZoneModeBuilds.IsBuilt(zdo)) continue;
                if (IsCover(zdo.GetPrefab())) s_cover.Add(zdo.GetPosition());
            }
        }

        private static bool IsCover(int prefabHash)
        {
            if (s_coverByPrefab.TryGetValue(prefabHash, out bool cover)) return cover;
            var prefab = ZNetScene.instance.GetPrefab(prefabHash);
            if (prefab == null) return false;
            cover = prefab.GetComponent<Character>() == null && prefab.GetComponent<ItemDrop>() == null;
            s_coverByPrefab[prefabHash] = cover;
            return cover;
        }

        private static int CoverAround(Vector3 at)
        {
            float cover2 = HideCoverRadius * HideCoverRadius;
            int around = 0;
            foreach (var item in s_cover)
                if (FlatDistanceSquared(item, at) < cover2) around++;
            return around;
        }

        private static float FlatDistanceSquared(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        // The ground at the spot, settled on the live voxel surface where one is held, when its footprint is flat enough.
        private static bool TryFlatGround(float wx, float wz, out float ground)
        {
            ground = 0f;
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 5; i++)
            {
                float x = wx + (i == 1 ? FootprintOffset : i == 2 ? -FootprintOffset : 0f);
                float z = wz + (i == 3 ? FootprintOffset : i == 4 ? -FootprintOffset : 0f);
                if (!VoxelZoneStreamer.TryGroundHeight(x, z, out float y)) return false;
                if (VoxelWorld.TrySampleSurfaceY(x, z, y + SurfaceSettleRange, y - SurfaceSettleRange, out float surface, out _)) y = surface;
                if (i == 0) ground = y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
            }
            return max - min <= MaxFloorRelief;
        }

        private static string Bearing(Vector3 from, Vector3 to)
        {
            float degrees = Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;
            string[] points = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return points[Mathf.RoundToInt(Mathf.Repeat(degrees, 360f) / 45f) % points.Length];
        }

        // Beside the starter generator, the one landmark every start has, on natural ground with nothing in the way.
        private static bool TryBesideStarter(out Vector3 at, out bool starterStands)
        {
            at = default;
            starterStands = ZoneModeChunkMachines.TryGetStarter(out var starter, out var rotation);
            if (!starterStands) return false;
            foreach (float distance in StarterSides)
            {
                for (int side = 0; side < 4; side++)
                {
                    var candidate = starter + rotation * (Quaternion.Euler(0f, 90f + side * 90f, 0f) * Vector3.forward * distance);
                    if (TryNaturalSpot(candidate.x, candidate.z, OpenBuildClearance, out at)) return true;
                }
            }
            return false;
        }

        // The natural ground nearest the StartTemple, ring by ring out to HideMaxDistance.
        private static bool TryNearestNatural(Vector3 temple, out Vector3 at)
        {
            at = default;
            for (float radius = NearestMinDistance; radius <= HideMaxDistance; radius += Spacing)
            {
                int steps = Mathf.Max(8, Mathf.CeilToInt(2f * Mathf.PI * radius / Spacing));
                for (int k = 0; k < steps; k++)
                {
                    float angle = k * 2f * Mathf.PI / steps;
                    if (TryNaturalSpot(temple.x + Mathf.Cos(angle) * radius, temple.z + Mathf.Sin(angle) * radius, OpenBuildClearance, out at))
                        return true;
                }
            }
            return false;
        }

        // The best-scoring cavern spot with nothing within ObjectClearance: the cave dressing usually lands before the
        // chest, so its rubble, spawners and chests are avoided rather than overlapped.
        private static bool TryClearSpot(out Vector3 at)
        {
            at = default;
            s_spots.Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (var spot in s_spots)
            {
                if (HasObjectNear(spot.At)) continue;
                at = spot.At;
                return true;
            }
            return false;
        }

        private static bool HasObjectNear(Vector3 at)
        {
            s_nearby.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(at), new SimulationDistance(1, 0), s_nearby);
            float clearance2 = ObjectClearance * ObjectClearance;
            foreach (var zdo in s_nearby)
                if (zdo != null && (zdo.GetPosition() - at).sqrMagnitude < clearance2) return true;
            return false;
        }

        // The zone's cavern spots into s_zoneSpots. False when some column had no ground height to scan from.
        private static bool Scan(Vector2s zone, Vector3 temple)
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
                    if (!VoxelZoneStreamer.TryGroundHeight(wx, wz, out float surface)) { complete = false; continue; }
                    VoxelCaveContent.ScanVoids(wx, wz, surface, s_voids);
                    foreach (var pocket in s_voids)
                    {
                        if (pocket.Headroom < MinHeadroom || surface - pocket.Ceiling < MinCover) continue;
                        if (!TryFlatFloor(wx, wz, pocket, out float floor)) continue;
                        var at = new Vector3(wx, floor, wz);
                        if (FiresLiquidWaterPrefab.HasPourNear(at, PoolClearance)) continue;
                        float score = Mathf.Min(pocket.Headroom, 8f) * 2f - (surface - floor) * 0.1f
                            - Vector3.Distance(new Vector3(wx, 0f, wz), new Vector3(temple.x, 0f, temple.z)) * 0.02f;
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
                float top = Mathf.Min(pocket.Ceiling - 0.1f, pocket.Floor + 1.5f);
                if (!VoxelWorld.TrySampleSurfaceY(x, z, top, pocket.Floor - 1.5f, out float y, out _)) return false;
                if (i == 0) floor = y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
            }
            return max - min <= MaxFloorRelief;
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
            var prefab = ZNetScene.instance.GetPrefab(ChestPrefab);
            var zdo = NetworkObjectHelper.CreateZdo(prefab, at, Quaternion.Euler(0f, Mathf.Abs(at.x * 7.1f + at.z * 3.3f) % 360f, 0f));
            if (zdo == null)
            {
                FiresLogger.LogWarning($"[FiresZones] '{ChestPrefab}' is not a networked prefab here: the first Chunk Hammer has no chest this session.");
                return;
            }
            var container = prefab.GetComponentInChildren<Container>(true);
            var inventory = new Inventory(ChestPrefab, null, container != null ? container.m_width : 4, container != null ? container.m_height : 2);
            inventory.AddItem(hammer, 1);
            var package = new ZPackage();
            inventory.Save(package);
            zdo.Set(ZDOVars.s_items, package.GetArray());
            zdo.Set(ZDOVars.s_addedDefaultItems, true);
            zdo.Set(HammerChestHash, true);
            WriteMarker();
            FiresLogger.LogInfo($"[FiresZones] the first Chunk Hammer waits in a chest at ({at.x:0},{at.y:0},{at.z:0}), {where}.");
        }

        // firesgen_<world>_hammer.txt: the chest's spot, when it was recorded, and the day it returns if it is gone.
        private static void WriteMarker()
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath()));
                File.WriteAllText(MarkerPath(),
                    $"{s_chestAt.x.ToString("0.0", inv)} {s_chestAt.y.ToString("0.0", inv)} {s_chestAt.z.ToString("0.0", inv)} {DateTime.UtcNow:O}\n"
                    + (s_respawnDay >= 0 ? $"respawn {s_respawnDay}\n" : ""));
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
                foreach (var line in lines)
                    if (line.StartsWith("respawn ") && int.TryParse(line.Substring(8).Trim(), out int day)) s_respawnDay = day;
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
            string world = ZNet.World?.m_name ?? "world";
            string safe = string.Concat(world.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "FiresWorldGen", "firesgen_" + safe + "_hammer.txt");
        }

        internal static void Reset()
        {
            s_done = false;
            s_markerRead = false;
            s_nextTick = 0f;
            s_nextRestockCheck = 0f;
            s_generatedAt = -1f;
            s_incompleteSince = -1f;
            s_respawnDay = -1;
            s_chestAt = default;
            s_spots.Clear();
            s_zoneSpots.Clear();
            s_scanned.Clear();
        }
    }
}
