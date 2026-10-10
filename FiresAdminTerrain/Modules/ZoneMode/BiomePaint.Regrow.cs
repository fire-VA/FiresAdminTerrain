using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // Server: a changed circle grows again as the world would have grown it under its sectors now.
    //  1. Only zones the server has loaded take part: PlaceVegetation needs a zone's live Heightmap, and a zone that
    //     cannot grow again must keep its old growth rather than be left bare.
    //  2. The natural vegetation standing in the circle goes through vanilla DestroyZDO: never a ZDO with a creator,
    //     nothing inside a location's footprint, nothing on a skyland, none of the start objects.
    //  3. A moment later, once the destroyed objects' colliders are gone (vanilla's block check would refuse the new growth
    //     where the old stood), vanilla PlaceVegetation runs for each of those zones, with InsideClearArea refusing every
    //     point outside the circle or inside a location. Vanilla seeds it per zone, so what grows is the world's own
    //     growth for that blend.
    // Locations swap first (BiomePaint.Locations), so the footprints kept out are the new ones; the ground an old
    // location held outside the circle grows again as a circle of its own.
    internal static partial class BiomePaint
    {
        private const float GrowDelaySeconds = 1f;
        private const float LocationMargin = 2f;

        private struct PendingGrowth
        {
            internal Circle Circle;
            internal List<Vector2s> Zones;
            internal int Cleared;
            internal long Asker;
            internal float Due;
            // What the log calls it (the circle's brush when empty), and a circle whose own growth covers the rest.
            internal string Label;
            internal bool HasExcept;
            internal Circle Except;
            internal int Waits;   // 0.2.765: ticks spent waiting for a ghost zone's terrain data
        }

        private static readonly List<PendingGrowth> s_pending = new List<PendingGrowth>();
        private static bool s_regrowing;
        private static Circle s_regrowCircle;
        private static bool s_regrowHasExcept;
        private static Circle s_regrowExcept;
        private static readonly List<(Vector3 Centre, float Radius)> s_keepOut = new List<(Vector3, float)>();

        // FiresSkyIslandProps stands down while this lays a circle, so a zone's skyland props are never laid twice.
        internal static bool Regrowing => s_regrowing;

        private static FieldInfo s_zonesField;
        private static FieldInfo s_rootField;
        private static MethodInfo s_placeVegetation;
        private static Type s_clearAreaType;
        private static ZoneSystem s_vegetationFor;
        private static readonly HashSet<int> s_vegetation = new HashSet<int>();

        // Clears the circle now and queues its growth. `asker` hears the outcome (0: the log only).
        private static string Regrow(in Circle circle, long asker = 0L)
        {
            var zones = ZoneSystem.instance;
            if (zones == null || ZDOMan.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return string.Empty;
            if (!Resolve()) return "Its growth could not be renewed: ZoneSystem's internals changed shape.";
            string swapped = SwapLocations(circle);
            // 0.2.765 (Fire 10-06: "our alt biome tools don't actually work ... 0 growth cleared, 0 grown again in 0 zone(s)"): a
            // dedicated server keeps LOADED zones (a live Heightmap) only round its own reference point; round its players it
            // makes ghost zones. Every zone a player painted was "not loaded here", so nothing was cleared or grown. Now every
            // zone the circle reaches takes part: the growth standing there is cleared (its ZDOs are on the server either way),
            // and a zone with no loaded Heightmap grows again through a temporary zone root in Ghost mode, the way vanilla grows
            // a player's zone on a dedicated server (GrowPending).
            var all = new List<Vector2s>(ZonesOf(circle));
            CollectKeepOut(zones, circle);
            int cleared = ClearGrowth(circle, all);
            s_pending.Add(new PendingGrowth { Circle = circle, Zones = all, Cleared = cleared, Asker = asker, Due = Time.time + GrowDelaySeconds });
            return $"{cleared} growth cleared; it grows again in a moment." + swapped;
        }

        // From Tick, on the server.
        private static void GrowPending()
        {
            if (s_pending.Count == 0 || Time.time < s_pending[0].Due) return;
            var pending = s_pending[0];
            s_pending.RemoveAt(0);
            var zones = ZoneSystem.instance;
            if (zones == null || ZDOMan.instance == null) return;
            CollectKeepOut(zones, pending.Circle);
            // A ghost-grown zone needs its terrain data built (vanilla SpawnZone's own check); until it is, the circle waits.
            foreach (var zone in pending.Zones)
            {
                if (HeightmapOf(zones, zone) != null || TerrainReady(zones, zone)) continue;
                if (++pending.Waits <= MaxTerrainWaits)
                {
                    pending.Due = Time.time + GrowDelaySeconds;
                    s_pending.Add(pending);
                    return;
                }
                break;   // waited long enough: grow what is ready, report the rest
            }
            int before = ZDOMan.instance.NrOfObjects();
            int grownZones = 0, missing = 0, ghosted = 0;
            var holder = new GameObject("fires_biomepaint_regrow");
            s_regrowing = true;
            t_regrow++;
            s_regrowCircle = pending.Circle;
            s_regrowHasExcept = pending.HasExcept;
            s_regrowExcept = pending.Except;
            try
            {
                foreach (var zone in pending.Zones)
                {
                    var hmap = HeightmapOf(zones, zone);
                    GameObject ghostRoot = null;
                    var mode = ZoneSystem.SpawnMode.Full;
                    if (hmap == null)
                    {
                        if (!TerrainReady(zones, zone))
                        {
                            missing++;
                            continue;
                        }
                        // The zone's own root, as vanilla SpawnZone makes it, kept only while its vegetation is laid.
                        ghostRoot = UnityEngine.Object.Instantiate(zones.m_zonePrefab, ZoneSystem.GetZonePos(zone), Quaternion.identity);
                        hmap = ghostRoot.GetComponentInChildren<Heightmap>();
                        mode = ZoneSystem.SpawnMode.Ghost;
                    }
                    var clearAreas = Activator.CreateInstance(typeof(List<>).MakeGenericType(s_clearAreaType));
                    var spawned = new List<GameObject>();
                    try
                    {
                        if (hmap == null) { missing++; continue; }
                        s_placeVegetation.Invoke(zones, new object[]
                        {
                            zone, ZoneSystem.GetZonePos(zone), holder.transform, hmap, clearAreas, mode, spawned,
                        });
                        grownZones++;
                        if (mode == ZoneSystem.SpawnMode.Ghost) ghosted++;
                    }
                    catch (Exception ex)
                    {
                        FiresLogger.LogWarning($"[BiomePaint] zone ({zone.x},{zone.y}) could not grow again: {ex.InnerException?.Message ?? ex.Message}");
                    }
                    finally
                    {
                        // Ghost mode: the growth stays as ZDOs (every peer near it makes its own instance); this one goes.
                        if (mode == ZoneSystem.SpawnMode.Ghost)
                            foreach (var go in spawned) if (go != null) UnityEngine.Object.Destroy(go);
                        if (ghostRoot != null) UnityEngine.Object.Destroy(ghostRoot);
                    }
                }
            }
            finally
            {
                s_regrowing = false;
                t_regrow--;
                UnityEngine.Object.Destroy(holder);
            }
            int grown = Mathf.Max(0, ZDOMan.instance.NrOfObjects() - before);
            string report = $"{pending.Label ?? Describe(pending.Circle)} round ({pending.Circle.X:0},{pending.Circle.Z:0}): {pending.Cleared} growth cleared, "
                + $"{grown} grown again in {grownZones} zone(s)" + (ghosted > 0 ? $" ({ghosted} not loaded on the server, grown as ghost zones)" : "")
                + (missing > 0 ? $", {missing} zone(s) whose terrain was not ready keep the cleared ground until painted again" : "")
                + $"; {s_keepOut.Count} location footprint(s) left alone.";
            FiresLogger.LogInfo("[BiomePaint] " + report);
            if (pending.Asker != 0L) ZoneModeChunkTools.Notify(pending.Asker, report);
        }

        // vanilla SpawnZone's gate: the zone's terrain data is built (HeightmapBuilder), so a root made there has its heights.
        private const int MaxTerrainWaits = 10;

        private static bool TerrainReady(ZoneSystem zones, Vector2s zone)
        {
            var template = zones.m_zonePrefab != null ? zones.m_zonePrefab.GetComponentInChildren<Heightmap>() : null;
            return template != null && HeightmapBuilder.instance != null && WorldGenerator.instance != null
                && HeightmapBuilder.instance.IsTerrainReady(ZoneSystem.GetZonePos(zone), template.m_width, template.m_scale, template.IsDistantLod, WorldGenerator.instance);
        }

        private static Heightmap HeightmapOf(ZoneSystem zones, Vector2s zone)
        {
            var loaded = s_zonesField.GetValue(zones) as IDictionary;
            var root = loaded != null && loaded.Contains(zone) ? s_rootField.GetValue(loaded[zone]) as GameObject : null;
            return root != null ? root.GetComponentInChildren<Heightmap>() : null;
        }

        private static int ClearGrowth(in Circle circle, List<Vector2s> zones)
        {
            EnsureVegetation();
            int removed = 0;
            var found = new List<ZDO>();
            foreach (var zone in zones)
            {
                found.Clear();
                ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), found);
                foreach (var zdo in found)
                {
                    if (zdo == null || !s_vegetation.Contains(zdo.GetPrefab())) continue;
                    var at = zdo.GetPosition();
                    if (!circle.Holds(at.x, at.z) || zdo.GetLong(ZDOVars.s_creator) != 0L || InKeepOut(at)) continue;
                    if (ZoneModeChunkMachines.IsProtected(zdo) || FiresSkyIslands.TrySkylandAt(at, out _)) continue;
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    ZDOMan.instance.DestroyZDO(zdo);
                    removed++;
                }
            }
            return removed;
        }

        private static IEnumerable<Vector2s> ZonesOf(Circle circle)
        {
            var low = ZoneSystem.GetZone(new Vector3(circle.X - circle.R, 0f, circle.Z - circle.R));
            var high = ZoneSystem.GetZone(new Vector3(circle.X + circle.R, 0f, circle.Z + circle.R));
            for (int z = low.y; z <= high.y; z++)
                for (int x = low.x; x <= high.x; x++)
                    yield return new Vector2s(x, z);
        }

        // Every location standing in or beside the circle's zones keeps its own ground: its objects stay, and nothing grows
        // inside its footprint.
        private static void CollectKeepOut(ZoneSystem zones, in Circle circle)
        {
            s_keepOut.Clear();
            var low = ZoneSystem.GetZone(new Vector3(circle.X - circle.R, 0f, circle.Z - circle.R));
            var high = ZoneSystem.GetZone(new Vector3(circle.X + circle.R, 0f, circle.Z + circle.R));
            for (int z = low.y - 1; z <= high.y + 1; z++)
                for (int x = low.x - 1; x <= high.x + 1; x++)
                {
                    if (!zones.m_locationInstances.TryGetValue(new Vector2s(x, z), out var instance) || instance.m_location == null) continue;
                    float radius = Mathf.Max(instance.m_location.m_exteriorRadius, instance.m_location.m_interiorRadius) + LocationMargin;
                    s_keepOut.Add((instance.m_position, radius));
                }
        }

        private static bool InKeepOut(Vector3 point)
        {
            foreach (var (centre, radius) in s_keepOut)
            {
                float dx = point.x - centre.x, dz = point.z - centre.z;
                if (dx * dx + dz * dz < radius * radius) return true;
            }
            return false;
        }

        private static void EnsureVegetation()
        {
            var zones = ZoneSystem.instance;
            if (ReferenceEquals(zones, s_vegetationFor)) return;
            s_vegetationFor = zones;
            s_vegetation.Clear();
            if (zones?.m_vegetation == null) return;
            foreach (var veg in zones.m_vegetation)
                if (veg?.m_prefab != null) s_vegetation.Add(veg.m_prefab.name.GetStableHashCode());
        }

        private static bool Resolve()
        {
            s_zonesField ??= AccessTools.Field(typeof(ZoneSystem), "m_zones");
            s_placeVegetation ??= AccessTools.Method(typeof(ZoneSystem), "PlaceVegetation");
            s_clearAreaType ??= AccessTools.Inner(typeof(ZoneSystem), "ClearArea");
            var zoneData = AccessTools.Inner(typeof(ZoneSystem), "ZoneData");
            s_rootField ??= zoneData != null ? AccessTools.Field(zoneData, "m_root") : null;
            return s_zonesField != null && s_placeVegetation != null && s_clearAreaType != null && s_rootField != null;
        }

        [HarmonyPatch(typeof(ZoneSystem), "InsideClearArea")]
        private static class ZoneSystem_InsideClearArea
        {
            private static bool Prefix(Vector3 point, ref bool __result)
            {
                if (!s_regrowing) return true;
                if (s_regrowCircle.Holds(point.x, point.z) && !InKeepOut(point) && !(s_regrowHasExcept && s_regrowExcept.Holds(point.x, point.z)))
                    return true;
                __result = true;
                return false;
            }
        }
    }
}
