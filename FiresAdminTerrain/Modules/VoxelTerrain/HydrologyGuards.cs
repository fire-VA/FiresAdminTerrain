using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Ground the hydrology solve must never put still water on: the start location, and a chunk world's start cave, plus
    // the world's frozen build and location guards (FiresWaterGuards). The solve runs after vanilla has planned its
    // locations, so it waits until the start location and the guards are known: a server reads its own plan once
    // location generation has finished, and a client reads the start location's map icon, which vanilla sends before the
    // player may spawn, and takes the guards from the handshake. Every peer therefore plans the same water.
    internal static class HydrologyGuards
    {
        internal struct Disc
        {
            public float X, Z, Radius;
            public string Name;
        }

        private const float StartGuard = 36f;
        private const float CaveMargin = 4f;
        private const float WaitSeconds = 180f;
        private const string DefaultStartLocation = "StartTemple";

        private static float s_firstAsk = -1f;
        private static bool s_warnedUnknown;

        internal static void Reset()
        {
            s_firstAsk = -1f;
            s_warnedUnknown = false;
        }

        // False while the start location is still unknown and the wait has not run out.
        internal static bool TryCollect(List<Disc> into)
        {
            into.Clear();
            var zones = ZoneSystem.instance;
            string startName = Game.instance != null && !string.IsNullOrEmpty(Game.instance.m_StartLocation)
                ? Game.instance.m_StartLocation : DefaultStartLocation;
            Vector3 start = Vector3.zero;
            bool known = zones != null && TryStartLocation(zones, startName, out start);
            if (known) into.Add(new Disc { X = start.x, Z = start.z, Radius = StartGuard, Name = startName });
            var cave = Zones.ZoneModeStartCave.Current;
            if (cave != null) into.Add(new Disc { X = cave.X, Z = cave.Z, Radius = cave.Reach + CaveMargin, Name = "start cave" });
            if (known && FiresWaterGuards.Known) return true;

            if (s_firstAsk < 0f) s_firstAsk = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - s_firstAsk < WaitSeconds) return false;
            if (!s_warnedUnknown)
            {
                s_warnedUnknown = true;
                FiresLogger.LogWarning($"[FiresVoxel] hydrology: after {WaitSeconds:0} s "
                    + (!known ? $"the '{startName}' location is still unknown" : "the world's water guards never arrived")
                    + "; the water is planned without them.");
            }
            return true;
        }

        private static bool TryStartLocation(ZoneSystem zones, string name, out Vector3 position)
        {
            position = Vector3.zero;
            var net = ZNet.instance;
            if (net != null && net.IsServer())
            {
                if (!zones.LocationsGenerated) return false;
                foreach (var instance in zones.m_locationInstances.Values)
                {
                    var location = instance.m_location;
                    if (location == null || (location.m_prefabName != name && location.m_name != name)) continue;
                    position = instance.m_position;
                    return true;
                }
                return false;
            }
            return zones.GetLocationIcon(name, out position);
        }

        // Still water off the guarded discs, before bowls and channels are read from the grid.
        internal static int ClearStill(float[] still, int res, float cell, float worldRadius, List<Disc> discs)
        {
            int cleared = 0;
            foreach (var d in discs)
            {
                int x0 = Mathf.Max(0, Mathf.FloorToInt((d.X - d.Radius + worldRadius) / cell));
                int x1 = Mathf.Min(res - 1, Mathf.CeilToInt((d.X + d.Radius + worldRadius) / cell));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((d.Z - d.Radius + worldRadius) / cell));
                int z1 = Mathf.Min(res - 1, Mathf.CeilToInt((d.Z + d.Radius + worldRadius) / cell));
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = z * res + x;
                        if (still[i] <= float.MinValue + 1f) continue;
                        float wx = -worldRadius + (x + 0.5f) * cell, wz = -worldRadius + (z + 0.5f) * cell;
                        float dx = wx - d.X, dz = wz - d.Z;
                        if (dx * dx + dz * dz > d.Radius * d.Radius) continue;
                        still[i] = float.MinValue;
                        cleared++;
                    }
            }
            return cleared;
        }

        // The radius a bowl may have without reaching a guarded disc or a build's guarded ground (FiresWaterGuards):
        // unchanged when clear, pulled back when it would reach one, 0 when its centre lies inside one.
        internal static float BowlRadius(float wx, float wz, float radius, List<Disc> discs)
        {
            foreach (var d in discs)
            {
                float dx = wx - d.X, dz = wz - d.Z;
                float room = Mathf.Sqrt(dx * dx + dz * dz) - d.Radius;
                if (room <= 0f) return 0f;
                if (room < radius) radius = room;
            }
            return FiresWaterGuards.RoomAround(wx, wz, radius);
        }

        internal static string Describe(List<Disc> discs)
        {
            if (discs.Count == 0) return "none";
            var parts = new List<string>(discs.Count);
            foreach (var d in discs) parts.Add($"{d.Name} ({d.X:0},{d.Z:0}) r{d.Radius:0}");
            return string.Join(", ", parts);
        }
    }
}
