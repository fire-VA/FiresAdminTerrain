using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── World lifecycle ─────────────────────────────────────────────────────
    // From FAT's one WorldGenerator.Initialize hook (FatVanillaHooks): Begin is its Priority.First prefix, so the
    // registry and the gate view exist before FAT's own Initialize prefix and before vanilla's pregeneration, which
    // then runs inside a planning scope; AfterInitialize is its postfix and End its finalizer.
    internal static class ZoneModeWorldInit
    {
        internal static void Begin(World world)
        {
            try { ZoneModeRegistry.OnWorldInitialize(world); }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] world init failed: {ex}"); }
            ZoneModeGate.EnterPlanning();
        }

        internal static Exception End(Exception exception)
        {
            ZoneModeGate.ExitPlanning();
            return exception;
        }

        // After FAT's own Initialize prefix has cleared the island set for the new world.
        internal static void AfterInitialize(World world)
        {
            try { ZoneModeSkylands.OnWorldInitialized(world); }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] skyland registry init failed: {ex}"); }
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Load))]
    internal static class ZoneMode_ZoneSystem_Load
    {
        private static void Postfix(ZoneSystem __instance)
        {
            try { ZoneModeRegistry.OnSaveLoaded(__instance); }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] save check failed: {ex}"); }
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "Update")]
    internal static class ZoneMode_ZoneSystem_Update
    {
        private static void Postfix() => ZoneModeDiagnostics.Tick();
    }

    // A dedicated server has no creation popup: `-fireszones` on its command line makes a world it
    // creates from scratch a zone-mode world, unless the world has a profile chosen for it, which
    // decides on its own. An existing world is never converted. Called from FAT's one
    // World.GetCreateWorld hook (FatVanillaHooks), after the load guard.
    internal static class ZoneModeNewWorld
    {
        internal static void AfterCreateWorld(World world, bool existed)
        {
            if (existed || world == null || !ZoneModeRegistry.LaunchFlagPresent()) return;
            var profile = Voxel.FiresWorldProfile.Read(world.m_name);
            if (!ZoneModeRegistry.LaunchFlagDecides(profile))
            {
                FiresLogger.LogInfo($"[FiresZones] {ZoneModeRegistry.LaunchFlag}: '{world.m_name}' has its own profile "
                    + $"({profile.Describe()}), which decides: " + (profile.ZoneMode ? "one zone at a time." : "the whole world."));
                return;
            }
            if (ZoneModeRegistry.CreateForNewWorld(world, out string path))
                FiresLogger.LogInfo($"[FiresZones] {ZoneModeRegistry.LaunchFlag}: created '{world.m_name}' as a ONE ZONE AT A "
                    + $"TIME world ({path}).");
            else
                FiresLogger.LogError($"[FiresZones] {ZoneModeRegistry.LaunchFlag}: could not write the zone registry for "
                    + $"'{world.m_name}' — it will run as a normal world.");
        }
    }

    // ── Planning scopes: these passes must read the real world ─────────────
    // VerifyBiomeData's scope lives in FatVanillaHooks, beside the join sync's biome grid on the same method.
    [HarmonyPatch(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints))]
    internal static class ZoneMode_AltBiome_GenerateBiomePoints
    {
        private static void Prefix() => ZoneModeGate.EnterPlanning();

        private static Exception Finalizer(Exception __exception)
        {
            ZoneModeGate.ExitPlanning();
            return __exception;
        }
    }

    // Location placement is a time-sliced coroutine interleaved with other main-thread work, so the
    // scope covers each resumption of its iterators, never "while generating".
    [HarmonyPatch]
    internal static class ZoneMode_LocationPlacement_MoveNext
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(ZoneSystem).GetMethods(AccessTools.all))
                if (method.Name == "GenerateLocationsTimeSliced")
                    yield return AccessTools.EnumeratorMoveNext(method);
        }

        private static void Prefix() => ZoneModeGate.EnterPlanning();

        private static Exception Finalizer(Exception __exception)
        {
            ZoneModeGate.ExitPlanning();
            return __exception;
        }
    }

    // Fills WorldGenerator's static biome cache; it must only ever hold real answers.
    [HarmonyPatch(typeof(WorldGenerator), "GetBiome", typeof(Vector2s))]
    internal static class ZoneMode_WorldGenerator_CachedBiome
    {
        private static void Prefix() => ZoneModeGate.EnterPlanning();

        private static Exception Finalizer(Exception __exception)
        {
            ZoneModeGate.ExitPlanning();
            return __exception;
        }
    }

    // ── Biome sectors: the runtime biome identity ──────────────────────────
    // Heightmap corners, the environment, the player's biome and spawns all read the alt-biome
    // grid through these two overloads. The grid itself is built ungated (a planning scope).
    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(float), typeof(float), typeof(bool))]
    internal static class ZoneMode_WorldGenerator_GetBiomeSector_Point
    {
        private static bool Prefix(WorldGenerator __instance, float wx, float wy, bool clamp, ref BiomeSector __result)
            => !ZoneModeGate.TryGateSector(__instance, wx, wy, clamp, ref __result);
    }

    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(Vector3), typeof(bool))]
    internal static class ZoneMode_WorldGenerator_GetBiomeSector_Position
    {
        private static bool Prefix(WorldGenerator __instance, Vector3 worldPos, bool clamp, ref BiomeSector __result)
            => !ZoneModeGate.TryGateSector(__instance, worldPos.x, worldPos.z, clamp, ref __result);
    }

    // ── Heightmap builds ────────────────────────────────────────────────────
    [HarmonyPatch(typeof(HeightmapBuilder), "Build")]
    internal static class ZoneMode_HeightmapBuilder_Build
    {
        private static void Prefix(HeightmapBuilder.HMBuildData data) => ZoneModeGate.PinForBuild(data);

        private static Exception Finalizer(Exception __exception, HeightmapBuilder.HMBuildData data)
        {
            ZoneModeGate.UnpinAfterBuild(data);
            return __exception;
        }
    }

    // A build finished under an older view must never be handed out: it would show the zone as it
    // was before the change. Checked where the main thread takes ready builds.
    internal static class ZoneModeBuilderCache
    {
        private static readonly AccessTools.FieldRef<HeightmapBuilder, object> LockRef =
            AccessTools.FieldRefAccess<HeightmapBuilder, object>("m_lock");
        private static readonly AccessTools.FieldRef<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>> ReadyRef =
            AccessTools.FieldRefAccess<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>>("m_ready");

        internal static int DroppedStale { get; private set; }

        internal static void DropStaleBuilds(HeightmapBuilder builder)
        {
            if (builder == null || !ZoneModeRegistry.ModeActive) return;
            object gate = LockRef(builder);
            if (gate == null) return;
            lock (gate)
            {
                var ready = ReadyRef(builder);
                for (int i = ready.Count - 1; i >= 0; i--)
                {
                    if (!ZoneModeGate.IsStaleBuild(ready[i])) continue;
                    ready.RemoveAt(i);
                    DroppedStale++;
                }
            }
        }

        internal static int TakeDroppedStale()
        {
            int dropped = DroppedStale;
            DroppedStale = 0;
            return dropped;
        }

        // A synchronous heightmap request that found its build ready returns in microseconds; anything slower blocked
        // the main thread while the builder thread worked.
        private static readonly long BlockedWaitTicks = Stopwatch.Frequency / 10000;
        private static int s_syncRequests;
        private static int s_syncBlocked;
        private static long s_syncBlockedTicks;
        private static long s_syncWorstTicks;

        internal static void RecordSyncRequest(long elapsedTicks)
        {
            s_syncRequests++;
            if (elapsedTicks < BlockedWaitTicks) return;
            s_syncBlocked++;
            s_syncBlockedTicks += elapsedTicks;
            if (elapsedTicks > s_syncWorstTicks) s_syncWorstTicks = elapsedTicks;
        }

        internal static string TakeSyncWaitReport()
        {
            string report = s_syncBlocked == 0 ? null
                : $"{s_syncRequests} synchronous heightmap request(s), {s_syncBlocked} blocked the main thread for "
                    + $"{TicksToMs(s_syncBlockedTicks):F1} ms in total (worst {TicksToMs(s_syncWorstTicks):F1} ms) waiting on the builder thread";
            s_syncRequests = s_syncBlocked = 0;
            s_syncBlockedTicks = s_syncWorstTicks = 0;
            return report;
        }

        private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }

    [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.IsTerrainReady),
        typeof(Vector3), typeof(int), typeof(float), typeof(bool), typeof(WorldGenerator))]
    internal static class ZoneMode_HeightmapBuilder_IsTerrainReady
    {
        private static void Prefix(HeightmapBuilder __instance) => ZoneModeBuilderCache.DropStaleBuilds(__instance);
    }

    // RequestTerrainSync spins on the private RequestTerrain (50,000+ calls/s) until the builder thread delivers, so the
    // stale drop runs once per request here instead of on every spin, where it re-took the builder's own lock.
    [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.RequestTerrainSync))]
    internal static class ZoneMode_HeightmapBuilder_RequestTerrainSync
    {
        private static void Prefix(HeightmapBuilder __instance, out long __state)
        {
            ZoneModeBuilderCache.DropStaleBuilds(__instance);
            __state = Stopwatch.GetTimestamp();
        }

        private static void Postfix(long __state) => ZoneModeBuilderCache.RecordSyncRequest(Stopwatch.GetTimestamp() - __state);
    }

    // ── Minimap: gated through the samplers; this only reports the result ──
    // From FAT's one Minimap.GenerateWorldMap hook (FatVanillaHooks).
    internal static class ZoneModeMinimapReport
    {
        internal static Stopwatch Start()
        {
            var clock = ZoneModeGate.Active ? Stopwatch.StartNew() : null;
            if (clock != null) ZoneModeDiagnostics.FlushMainThreadTally();
            return clock;
        }

        internal static void Finish(Stopwatch clock)
        {
            if (clock == null) return;
            var tally = ZoneModeGate.TakeMainThreadTally();
            FiresLogger.LogInfo($"[FiresZones] world minimap drawn in {clock.ElapsedMilliseconds} ms with closed zones as sea "
                + $"({tally.ClosedHeight + tally.CoastHeight:N0} closed pixels).");
        }
    }
}
