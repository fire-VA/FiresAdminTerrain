using System.Diagnostics;
using HarmonyLib;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══ WHERE DOES ZONE GENERATION ACTUALLY GO? MEASURE IT. ═════════════════
    // The first cut of this only watched PlaceVegetation and only spoke when a
    // zone broke 25ms, and it printed NOTHING across a whole session. That was
    // not "vegetation is fast" — ZoneSystem.SpawnZone (1.0 source, line 699)
    // only calls PlaceLocations/PlaceVegetation when the mode is Ghost or Full
    // AND the zone is not already generated. A client on a dedicated server
    // runs SpawnMode.Client, so it never calls either; and a server that loads
    // an already-generated world never calls them again. Silence was the
    // profiler being asked a question it could not have answered.
    //
    // So: measure the WHOLE spawn path, not one method inside it, and report on
    // a timer whether or not anything was slow. A run that generates no zones
    // now says so out loud instead of looking identical to a fast one.
    [HarmonyPatch]
    internal static class FiresVegProfiler
    {
        internal static bool Inside;
        internal static int GroundCalls;
        internal static long GroundTicks;
        internal static int VoxelProbes;

        private static readonly Stopwatch s_veg = new Stopwatch();
        private static readonly Stopwatch s_loc = new Stopwatch();
        private static readonly Stopwatch s_zone = new Stopwatch();

        private static int s_vegZones, s_locZones, s_spawnCalls, s_generated;
        private static double s_vegMs, s_locMs, s_zoneMs, s_worstVegMs;
        private static int s_groundCalls, s_voxelProbes;
        private static double s_groundMs;

        private const double SLOW_MS = 8.0;
        private const double REPORT_EVERY_S = 20.0;
        private static readonly Stopwatch s_since = Stopwatch.StartNew();

        // BeforeZone and AfterZone come from FAT's one ZoneSystem.SpawnZone hook (FatVanillaHooks): its first prefix
        // and its Priority.Last postfix, so the clock brackets every other hook on the method.
        internal static void BeforeZone()
        {
            if (!FiresWorldGenBase.Active) return;
            s_spawnCalls++;
            s_zone.Restart();
        }

        internal static void AfterZone()
        {
            if (!FiresWorldGenBase.Active) return;
            s_zone.Stop();
            s_zoneMs += s_zone.Elapsed.TotalMilliseconds;
            Report();
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void BeforeLoc()
        {
            if (!FiresWorldGenBase.Active) return;
            s_loc.Restart();
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void AfterLoc()
        {
            if (!FiresWorldGenBase.Active) return;
            s_loc.Stop();
            s_locZones++;
            s_locMs += s_loc.Elapsed.TotalMilliseconds;
        }

        // BeforeVegetation and AfterVegetation are the Priority.First prefix and Priority.Last postfix of FAT's one
        // ZoneSystem.PlaceVegetation hook (FatVanillaHooks), so the clock and the Inside flag bracket everything.
        internal static void BeforeVegetation()
        {
            if (!FiresWorldGenBase.Active) return;
            Inside = true; GroundCalls = 0; GroundTicks = 0; VoxelProbes = 0;
            s_veg.Restart();
        }

        internal static void AfterVegetation()
        {
            if (!Inside) return;
            s_veg.Stop();
            Inside = false;
            double ms = s_veg.Elapsed.TotalMilliseconds;
            double groundMs = GroundTicks * 1000.0 / Stopwatch.Frequency;

            s_vegZones++; s_generated++;
            s_vegMs += ms; s_groundMs += groundMs;
            s_groundCalls += GroundCalls; s_voxelProbes += VoxelProbes;
            if (ms > s_worstVegMs) s_worstVegMs = ms;

            if (ms < SLOW_MS) return;
            FiresLogger.LogInfo($"[FiresVegPerf] zone took {ms:0.0}ms — {GroundCalls:N0} ground query(ies) " +
                      $"costing {groundMs:0.0}ms ({(ms > 0.01 ? groundMs / ms * 100.0 : 0):0}% of it), " +
                      $"{VoxelProbes:N0} of them ran a voxel probe.");
        }

        // Speaks on a timer, ALWAYS, so "no zones were generated" is a reading
        // rather than an absence of one.
        private static void Report()
        {
            if (s_since.Elapsed.TotalSeconds < REPORT_EVERY_S) return;
            s_since.Restart();

            if (s_generated == 0)
            {
                FiresLogger.LogInfo($"[FiresVegPerf] {s_spawnCalls:N0} SpawnZone call(s), {s_zoneMs:0}ms total, and NOT ONE " +
                          "generated a zone — SpawnZone only places locations and vegetation for Ghost/Full mode on an " +
                          "ungenerated zone. A client on a dedicated server is SpawnMode.Client, and a server that loaded " +
                          "an existing world has nothing left to generate. Vegetation cost cannot be measured from this run.");
                s_spawnCalls = 0; s_zoneMs = 0;
                return;
            }

            FiresLogger.LogInfo($"[FiresVegPerf] {s_generated:N0} zone(s) generated in {s_zoneMs:0}ms total " +
                      $"({s_zoneMs / System.Math.Max(1, s_spawnCalls):0.0}ms per SpawnZone over {s_spawnCalls:N0} call(s)) — " +
                      $"vegetation {s_vegMs:0}ms ({s_vegMs / System.Math.Max(1, s_vegZones):0.0}ms mean, {s_worstVegMs:0.0}ms worst), " +
                      $"locations {s_locMs:0}ms ({s_locMs / System.Math.Max(1, s_locZones):0.0}ms mean). " +
                      $"Ground queries inside vegetation: {s_groundCalls:N0} costing {s_groundMs:0}ms " +
                      $"({(s_vegMs > 0.01 ? s_groundMs / s_vegMs * 100.0 : 0):0}% of vegetation), " +
                      $"{s_voxelProbes:N0} of them ran a voxel probe " +
                      $"({(s_groundCalls > 0 ? 100.0 * s_voxelProbes / s_groundCalls : 0):0}%).");

            s_spawnCalls = 0; s_generated = 0; s_vegZones = 0; s_locZones = 0;
            s_zoneMs = 0; s_vegMs = 0; s_locMs = 0; s_worstVegMs = 0;
            s_groundCalls = 0; s_voxelProbes = 0; s_groundMs = 0;
        }
    }
}
