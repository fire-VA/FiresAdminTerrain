using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // The world's starter Chunk Generator (the machine itself is ZoneModeChunkGenerator). The server places it beside
    // the StartTemple once that zone's ground is built, on natural ground (ZoneModeStartGround: never a path, a field, a
    // paved road or among builds), or on a converted map where the player stands (Tools\FAT_START_ITEMS.md). It cannot
    // be damaged, worn or removed, and the server puts it back within a minute if it is ever missing.
    internal static class ZoneModeChunkMachines
    {
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

        internal static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> ObjectsBySector =
            AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");

        private static bool s_starterSettled;
        private static float s_nextStarterCheck;
        private static int s_starterReplacements;
        private static string s_starterRefusal;

        internal static string StarterStatus => s_starterSettled ? "starter stands" : "starter missing";

        // ── The starter machine cannot be worn, damaged or removed ──────────────────────────────────
        private static ZDO ZdoOf(Component component)
        {
            var nview = component != null ? component.GetComponent<ZNetView>() : null;
            return nview != null && nview.IsValid() ? nview.GetZDO() : null;
        }

        internal static bool IsStarter(Component component) => IsStarter(ZdoOf(component));

        internal static bool IsStarter(ZDO zdo)
            => zdo != null && ZoneModeChunkGenerator.PrefabHash != 0 && zdo.GetPrefab() == ZoneModeChunkGenerator.PrefabHash
               && zdo.GetBool(StarterHash, false);

        // The pieces a chunk world cannot lose: the starter generator and the first Chunk Hammer's chest. They take no
        // wear, no damage, no removal and no destruction.
        internal static bool IsProtected(Component component) => IsProtected(ZdoOf(component));

        internal static bool IsProtected(ZDO zdo)
            => zdo != null && (zdo.GetBool(ZoneModeHammerChest.HammerChestHash, false)
                               || zdo.GetBool(ZoneModeCultivatorChest.CultivatorChestHash, false) || IsStarter(zdo));

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
            if (!ZoneModeChunkGenerator.TryGetPrefab(out var prefab)) return;

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

            ZDO replaces = null;
            string where = "beside the StartTemple on unpainted ground clear of builds";
            Vector3 position;
            Quaternion rotation;
            Vector2s zone;
            if (ZoneModePark.Converted)
            {
                if (!TryChooseConvertedSpot(out position, out rotation, out replaces, out where)) return;
                zone = ZoneSystem.GetZone(position);
            }
            else if (!TryChooseStarterSpot(out position, out rotation, out zone)) return;
            if (!VoxelZoneStreamer.IsZoneGenerated(zone)) return;
            if (replaces != null) RetireSmelter(replaces);
            var created = NetworkObjectHelper.CreateZdo(prefab, position, rotation);
            if (created == null) return;
            created.Set(StarterHash, true);
            s_starterSettled = true;
            ZoneModeStartPins.Changed();
            FiresLogger.LogInfo($"[FiresZones] placed the indestructible starter {tier.MachineName} at "
                + $"({position.x:0},{position.y:0},{position.z:0}) in zone ({zone.x},{zone.y}), {where}.");
        }

        // ── Converted maps (Fire, 2026-09-27, Tools\FAT_START_ITEMS.md) ─────────────────────────────
        // The start is the player's own town, so the starter goes where the player really stands: when two or more of
        // the player's smelters stand within StartAreaReach zones of the start (Fire's popup 20:5x: "Search 1-2 zones
        // out", parked zones included), the one nearest the spawn becomes the starter (the generator is a restyled
        // smelter, so it fits the spot); beside a lone smelter otherwise; else on the nearest spot a player can stand at
        // the spawn's height, build floors included, off the StartTemple itself. Only when none of those exists does the
        // fresh-world rule apply. When the nearest smelter stands in a parked zone, that zone opens as part of the start
        // and the choice waits for it to return with its pieces.
        private const string SmelterQueueKey = "item";
        private const int StartAreaReach = 2;
        // Half the space the machine needs round its spot: nothing solid (a trunk, a rock, a wall) may stand in it.
        private const float StarterSpace = 1.4f;
        private const int SmeltersToSwap = 2;
        private static readonly float[] SmelterSides = { 2.6f, 3.4f };
        private static readonly float[] SmelterBearings = { 90f, -90f, 180f, 0f };
        private const float DropSpread = 0.8f;
        private const float DropLift = 0.5f;

        private static bool TryChooseConvertedSpot(out Vector3 position, out Quaternion rotation, out ZDO replaces, out string where)
        {
            position = default;
            rotation = Quaternion.identity;
            replaces = null;
            where = null;
            if (!ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out var temple)) return false;
            if (!ZoneModeStartSpawn.TryGet(temple.m_position, out var spawn))
            {
                NoteStarterRefusal("waiting for the first player to stand at the start of this converted map, to place the starter where they stand");
                return false;
            }
            var smelters = StartAreaSmelters(temple.m_position, spawn);
            var parked = ParkedSmelters(temple.m_position, spawn);
            if (parked.Count > 0 && (smelters.Count == 0
                || ZoneModeStartSpots.FlatDistanceSquared(parked[0].Position, spawn) < ZoneModeStartSpots.FlatDistanceSquared(smelters[0].GetPosition(), spawn)))
            {
                var zone = parked[0].Zone;
                if (!ZoneModeRegistry.IsOpen(zone))
                    ZoneModeOpening.Open(new[] { zone }, ZoneOpenReason.Start, 0L, $"the start: the player's {parked[0].Prefab} nearest the spawn stands there");
                NoteStarterRefusal($"waiting for parked zone ({zone.x},{zone.y}) to return with the player's {parked[0].Prefab}, the smelter nearest the spawn");
                return false;
            }
            int total = smelters.Count + parked.Count;
            if (smelters.Count > 0 && total >= SmeltersToSwap)
            {
                replaces = smelters[0];
                position = replaces.GetPosition();
                rotation = replaces.GetRotation();
                where = $"in place of the player's smelter nearest the spawn ({total - 1} other smelter(s) stay)";
                return true;
            }
            if (smelters.Count == 1 && TryBesideSmelter(smelters[0], out position))
            {
                rotation = smelters[0].GetRotation();
                where = $"beside the player's smelter near the spawn, {ZoneModeStartSpots.LastSupport}";
                return true;
            }
            bool chestPlaced = ZoneModeHammerChest.TryGetSpot(out var chest);
            if (ZoneModeStartSpots.TryRingSpot(spawn, ZoneModeStartSpots.SpawnMinRing, ZoneModeStartSpots.PastTemple(ZoneModeStartSpots.SpawnMaxRing),
                    ZoneModeStartSpots.SpawnRingStep, out position, StarterSpace, chest, chestPlaced ? ZoneModeStartSpots.ObjectsApart : 0f))
            {
                var face = spawn - position;
                face.y = 0f;
                rotation = face.sqrMagnitude > 0.01f ? Quaternion.LookRotation(face.normalized) : Quaternion.identity;
                where = $"{Mathf.Sqrt(ZoneModeStartSpots.FlatDistanceSquared(position, spawn)):0} m from the spawn, {ZoneModeStartSpots.LastSupport}"
                    + (chestPlaced ? $", {Mathf.Sqrt(ZoneModeStartSpots.FlatDistanceSquared(position, chest)):0} m from the hammer chest" : "")
                    + $" ({ZoneModeStartSpots.LastRefusals} nearer the spawn)";
                return true;
            }
            string refusals = ZoneModeStartSpots.LastRefusals;
            if (!TryChooseStarterSpot(out position, out rotation, out _)) return false;
            where = $"on the natural ground nearest the StartTemple: nothing within {ZoneModeStartSpots.SpawnMaxRing:0} m of the spawn could hold it ({refusals})";
            return true;
        }

        // One smelter or smelting station for the log, live or parked.
        private struct Candidate
        {
            internal string Prefab;
            internal Vector2s Zone;
            internal Vector3 Position;
            internal bool Parked;
        }

        // The player's smelters in the open zones within StartAreaReach of the start, at any height, nearest the spawn
        // first (Fire, 2026-09-27: "scanning the entire spawned chunks"). A converted town's carry no creator, so any
        // placed smelter piece counts (ZoneModeBuilds.IsPlaced). Any placed piece whose prefab is named for a smelter
        // counts: vanilla's own, and a modded or decorative one (SanctuaryAshlands, 2026-09-27: the exact "smelter" search
        // found neither of its two). The log names each, live or parked, and every other smelting station there (any
        // piece whose prefab carries a Smelter: kilns, blast furnaces, refineries, windmills, spinning wheels, modded
        // furnaces), which never becomes the starter.
        private static List<ZDO> StartAreaSmelters(Vector3 temple, Vector3 spawn)
        {
            var start = ZoneSystem.GetZone(temple);
            var near = new List<ZDO>();
            var found = new List<ZDO>();
            for (int dz = -StartAreaReach; dz <= StartAreaReach; dz++)
                for (int dx = -StartAreaReach; dx <= StartAreaReach; dx++)
                {
                    var zone = new Vector2s(start.x + dx, start.y + dz);
                    if (!ZoneModeRegistry.IsOpen(zone)) continue;
                    found.Clear();
                    ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), found);
                    foreach (var zdo in found)
                    {
                        if (zdo == null || !ZoneModeBuilds.IsPlaced(zdo) || IsStarter(zdo)) continue;
                        if (IsSmelterPrefab(zdo.GetPrefab())) near.Add(zdo);
                        else if (!s_smeltersLogged && IsSmeltingStation(zdo.GetPrefab())) s_stations.Add(Live(zdo));
                    }
                }
            near.Sort((a, b) => ZoneModeStartSpots.FlatDistanceSquared(a.GetPosition(), spawn).CompareTo(ZoneModeStartSpots.FlatDistanceSquared(b.GetPosition(), spawn)));
            if (!s_smeltersLogged) LogSmelters(near, ParkedSmelters(temple, spawn), start, spawn);
            return near;
        }

        // The player's smelters in PARKED zones within StartAreaReach of the start, read once a session through the
        // park's own reader (ZoneModePark.TryReadZone), nearest the spawn first. A zone that has returned since drops out.
        private static List<Candidate> s_parkedSmelters;
        private static readonly List<Candidate> s_stations = new List<Candidate>();

        private static List<Candidate> ParkedSmelters(Vector3 temple, Vector3 spawn)
        {
            if (s_parkedSmelters == null)
            {
                s_parkedSmelters = new List<Candidate>();
                var start = ZoneSystem.GetZone(temple);
                for (int dz = -StartAreaReach; dz <= StartAreaReach; dz++)
                    for (int dx = -StartAreaReach; dx <= StartAreaReach; dx++)
                    {
                        var zone = new Vector2s(start.x + dx, start.y + dz);
                        if (!ZoneModePark.TryGetParked(zone, out _)) continue;
                        ZoneModePark.TryReadZone(zone, (zdo, portal) =>
                        {
                            if (!ZoneModeBuilds.IsPlaced(zdo)) return;
                            bool smelter = IsSmelterPrefab(zdo.GetPrefab());
                            if (!smelter && !IsSmeltingStation(zdo.GetPrefab())) return;
                            var candidate = new Candidate { Prefab = PrefabName(zdo), Zone = zone, Position = zdo.GetPosition(), Parked = true };
                            if (smelter) s_parkedSmelters.Add(candidate);
                            else s_stations.Add(candidate);
                        });
                    }
                s_parkedSmelters.Sort((a, b) => ZoneModeStartSpots.FlatDistanceSquared(a.Position, spawn).CompareTo(ZoneModeStartSpots.FlatDistanceSquared(b.Position, spawn)));
            }
            return s_parkedSmelters.FindAll(candidate => ZoneModePark.TryGetParked(candidate.Zone, out _));
        }

        private static Candidate Live(ZDO zdo)
            => new Candidate { Prefab = PrefabName(zdo), Zone = ZoneSystem.GetZone(zdo.GetPosition()), Position = zdo.GetPosition() };

        private static readonly Dictionary<int, bool> s_stationByPrefab = new Dictionary<int, bool>();

        private static bool IsSmeltingStation(int prefabHash)
        {
            if (s_stationByPrefab.TryGetValue(prefabHash, out bool station)) return station;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            station = prefab != null && prefab.GetComponentInChildren<Smelter>(true) != null;
            s_stationByPrefab[prefabHash] = station;
            return station;
        }

        private const string SmelterWord = "smelter";
        private static readonly Dictionary<int, bool> s_smelterByPrefab = new Dictionary<int, bool>();

        private static bool IsSmelterPrefab(int prefabHash)
        {
            if (s_smelterByPrefab.TryGetValue(prefabHash, out bool smelter)) return smelter;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            smelter = prefab != null && prefab.name.IndexOf(SmelterWord, StringComparison.OrdinalIgnoreCase) >= 0;
            s_smelterByPrefab[prefabHash] = smelter;
            return smelter;
        }

        private static string PrefabName(ZDO zdo)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : zdo.GetPrefab().ToString();
        }

        private static bool s_smeltersLogged;

        private static void LogSmelters(List<ZDO> live, List<Candidate> parked, Vector2s start, Vector3 spawn)
        {
            s_smeltersLogged = true;
            var smelters = new List<Candidate>();
            foreach (var zdo in live) smelters.Add(Live(zdo));
            smelters.AddRange(parked);
            int span = StartAreaReach * 2 + 1;
            FiresLogger.LogInfo($"[FiresZones] smelters within {StartAreaReach} zone(s) of ({start.x},{start.y}) ({span}x{span}, open and parked): "
                + $"{Listed(smelters, spawn)}. Other smelting stations there (never the starter): {Listed(s_stations, spawn)}.");
        }

        private static string Listed(List<Candidate> pieces, Vector3 spawn)
        {
            if (pieces.Count == 0) return "none";
            var lines = new List<string>();
            foreach (var piece in pieces)
                lines.Add($"{piece.Prefab} in zone ({piece.Zone.x},{piece.Zone.y}) ({(piece.Parked ? "parked" : "live")}) at "
                    + $"({piece.Position.x:0},{piece.Position.y:0},{piece.Position.z:0}), "
                    + $"{Mathf.Sqrt(ZoneModeStartSpots.FlatDistanceSquared(piece.Position, spawn)):0} m from the spawn");
            return $"{lines.Count}: " + string.Join("; ", lines);
        }

        private static bool TryBesideSmelter(ZDO smelter, out Vector3 at)
        {
            var origin = smelter.GetPosition();
            var rotation = smelter.GetRotation();
            foreach (float bearing in SmelterBearings)
                foreach (float distance in SmelterSides)
                    if (ZoneModeStartSpots.TryStandingSpot(origin + rotation * (Quaternion.Euler(0f, bearing, 0f) * Vector3.forward * distance),
                            StarterSpace, out at))
                        return true;
            at = default;
            return false;
        }

        // The smelter the starter replaces goes the way vanilla removes an object; its coal, its queued ore and its
        // finished bars are left on the ground where it stood, as tearing it down would.
        private static void RetireSmelter(ZDO smelter)
        {
            var at = smelter.GetPosition();
            int dropped = 0;
            var smelterPrefab = ZNetScene.instance.GetPrefab(smelter.GetPrefab());
            var component = smelterPrefab != null ? smelterPrefab.GetComponent<Smelter>() : null;
            if (component != null && component.m_fuelItem != null)
                dropped += Drop(component.m_fuelItem.gameObject, Mathf.FloorToInt(smelter.GetFloat(ZDOVars.s_fuel)), at);
            int queued = smelter.GetInt(ZDOVars.s_queued);
            for (int i = 0; i < queued; i++) dropped += Drop(ObjectDB.instance.GetItemPrefab(smelter.GetString(SmelterQueueKey + i)), 1, at);
            dropped += Drop(ObjectDB.instance.GetItemPrefab(smelter.GetString(ZDOVars.s_spawnOre)), smelter.GetInt(ZDOVars.s_spawnAmount), at);
            smelter.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(smelter);
            FiresLogger.LogInfo($"[FiresZones] the player's smelter at ({at.x:0},{at.y:0},{at.z:0}) becomes the starter Chunk Generator; "
                + $"{dropped} item(s) it held lie on the ground beside it.");
        }

        private static int Drop(GameObject item, int amount, Vector3 at)
        {
            var drop = item != null ? item.GetComponent<ItemDrop>() : null;
            if (drop == null || amount <= 0) return 0;
            int left = amount, placed = 0;
            int stackSize = Mathf.Max(1, drop.m_itemData.m_shared.m_maxStackSize);
            while (left > 0)
            {
                int stack = Mathf.Min(left, stackSize);
                var offset = new Vector3(UnityEngine.Random.Range(-DropSpread, DropSpread), DropLift, UnityEngine.Random.Range(-DropSpread, DropSpread));
                var zdo = NetworkObjectHelper.CreateZdo(item, at + offset, Quaternion.identity);
                if (zdo == null) break;
                zdo.Set(ZDOVars.s_stack, stack);
                left -= stack;
                placed += stack;
            }
            return placed;
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
            s_smeltersLogged = false;
            s_parkedSmelters = null;
            s_stations.Clear();
            s_starterReplacements = 0;
            s_starterRefusal = null;
        }
    }
}
