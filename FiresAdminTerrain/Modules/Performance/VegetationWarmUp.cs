using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using Object = UnityEngine.Object;

namespace VerdantsAscent.Terrain.Performance
{
    // [perf] R32 / R33, 2026-09-28: a dedicated server's FIRST instance of a vegetation prefab costs up to half a second,
    // mostly blocked on disk (Beech1: 480 / 446 ms; Rock_4 98 ms), and it lands in the frame of whichever player first
    // walks or teleports into a zone that grows it: a stall for everyone and a jump in the world clock (R33 boat_test).
    // After the world loads, the server makes one instance of each vegetation prefab, far below the world and without a
    // network object (ZNetView.m_forceDisableInit, as vanilla does for its placement ghosts and item previews), and lets
    // it go at the end of the frame, one prefab a frame, so those costs are paid before anyone plays.
    // Vanilla's vegetation Awakes (TreeBase, Destructible, MineRock, MineRock5, Pickable, WearNTear, Beehive,
    // PickableItem) all return early on a ZNetView without a ZDO. The object is destroyed before its first Start.
    internal static class VegetationWarmUp
    {
        // Expand World Data rewrites the vegetation list after ZoneSystem.Start (R33: 164 entries at Start, 257 by the
        // first zone), so the list is read after this.
        private const float StartDelaySeconds = 10f;
        private const float BelowWorldY = -5000f;
        private const int SlowestNamed = 5;
        private const double MillisecondsPerSecond = 1000d;

        private static readonly Vector3 WarmPosition = new Vector3(0f, BelowWorldY, 0f);

        internal static void OnZoneSystemStart(ZoneSystem zoneSystem)
        {
            bool enabled = FiresVoxelConfig.VegetationWarmUp == null || FiresVoxelConfig.VegetationWarmUp.Value;
            if (!enabled || zoneSystem == null || !FiresCore.Lifecycle.FiresMod.IsDedicatedServer) return;
            zoneSystem.StartCoroutine(WarmAll(zoneSystem));
        }

        private static IEnumerator WarmAll(ZoneSystem zoneSystem)
        {
            yield return new WaitForSecondsRealtime(StartDelaySeconds);
            var prefabs = DistinctPrefabs(zoneSystem);
            var times = new List<KeyValuePair<string, long>>(prefabs.Count);
            var clock = Stopwatch.StartNew();
            long warmMs = 0;
            int failed = 0;
            foreach (var prefab in prefabs)
            {
                if (zoneSystem == null || ZNet.instance == null) yield break;
                if (prefab == null) continue;
                long ms = WarmOne(prefab, ref failed);
                warmMs += ms;
                times.Add(new KeyValuePair<string, long>(prefab.name, ms));
                yield return null;
            }
            times.Sort((a, b) => b.Value.CompareTo(a.Value));
            var slowest = new List<string>(SlowestNamed);
            for (int i = 0; i < times.Count && i < SlowestNamed; i++) slowest.Add($"{times[i].Key} {times[i].Value} ms");
            FiresLogger.LogInfo($"[FiresVeg] warmed {times.Count} vegetation prefab(s) on this server in {warmMs:N0} ms of main thread "
                + $"over {clock.ElapsedMilliseconds / MillisecondsPerSecond:0.0} s, one a frame, so the first new zone does not pay it; slowest: "
                + $"{string.Join(", ", slowest.ToArray())}" + (failed > 0 ? $"; {failed} could not be instantiated (see above)." : "."));
        }

        private static List<GameObject> DistinctPrefabs(ZoneSystem zoneSystem)
        {
            var seen = new HashSet<GameObject>();
            var prefabs = new List<GameObject>();
            if (zoneSystem.m_vegetation == null) return prefabs;
            foreach (var entry in zoneSystem.m_vegetation)
                if (entry != null && entry.m_enable && entry.m_prefab != null && seen.Add(entry.m_prefab)) prefabs.Add(entry.m_prefab);
            return prefabs;
        }

        private static long WarmOne(GameObject prefab, ref int failed)
        {
            var clock = Stopwatch.StartNew();
            GameObject instance = null;
            ZNetView.m_forceDisableInit = true;
            try { instance = Object.Instantiate(prefab, WarmPosition, Quaternion.identity); }
            catch (Exception ex)
            {
                failed++;
                FiresLogger.LogWarning($"[FiresVeg] warming vegetation prefab '{prefab.name}' threw {ex.GetType().Name}: {ex.Message}");
            }
            finally { ZNetView.m_forceDisableInit = false; }
            if (instance != null) Object.Destroy(instance);
            return clock.ElapsedMilliseconds;
        }
    }
}
