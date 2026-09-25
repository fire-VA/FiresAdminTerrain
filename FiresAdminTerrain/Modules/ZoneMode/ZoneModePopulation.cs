using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── POPULATION GATE ─────────────────────────────────────────────────────
    // A closed zone keeps its root (heightmap + water, so there is sea to swim and sail on) but is
    // never populated and never marked generated:
    //   * Full  → spawned as Client, which is vanilla's own root-only path.
    //   * Ghost → refused with false, so CreateGhostZones moves on to the next zone instead of
    //             picking the same closed zone every tick.
    // An open zone populates exactly as vanilla, inside a planning scope so location pieces,
    // RandomSpawn biome checks and lava checks read the real world even at the zone's edges.
    // Before, After and Finally are the first prefix, a postfix and the finalizer of FAT's one
    // ZoneSystem.SpawnZone hook (FatVanillaHooks); `__state` there is this gate's "scope opened".
    internal static class ZoneMode_ZoneSystem_SpawnZone
    {
        private static readonly HashSet<Vector2s> s_reportedClosed = new HashSet<Vector2s>();

        internal static int ClosedKeptEmpty { get; private set; }
        internal static int GhostRefused { get; private set; }
        internal static int OpenPopulated { get; private set; }

        internal static bool Before(Vector2s zoneID, ref ZoneSystem.SpawnMode mode, ref GameObject root,
            ref bool __result, out bool __state)
        {
            __state = false;
            if (!ZoneModeRegistry.ModeActive || !ZoneModeRegistry.IsAuthority) return true;

            if (ZoneModeRegistry.IsOpenForPopulation(zoneID))
            {
                if (mode == ZoneSystem.SpawnMode.Client) return true;
                ZoneModeGate.EnterPlanning();
                if (ZoneModeRegistry.TryGetSource(zoneID, out _)) ZoneModeGate.EnterPopulation(zoneID);
                __state = true;
                return true;
            }

            if (mode == ZoneSystem.SpawnMode.Ghost)
            {
                root = null;
                __result = false;
                GhostRefused++;
                return false;
            }
            if (mode == ZoneSystem.SpawnMode.Full)
            {
                mode = ZoneSystem.SpawnMode.Client;
                ClosedKeptEmpty++;
                if (s_reportedClosed.Add(zoneID))
                    FiresLogger.LogVerbose($"[FiresZones] closed zone ({zoneID.x},{zoneID.y}) spawned as sea only — not populated, not generated.");
            }
            return true;
        }

        internal static void After(bool __result, bool __state)
        {
            if (__state && __result) OpenPopulated++;
        }

        internal static Exception Finally(Exception __exception, bool __state)
        {
            if (__state)
            {
                ZoneModeGate.ExitPopulation();
                ZoneModeGate.ExitPlanning();
            }
            return __exception;
        }

        internal static void ResetCounters()
        {
            ClosedKeptEmpty = 0;
            GhostRefused = 0;
            OpenPopulated = 0;
        }

        internal static void ForgetReported() => s_reportedClosed.Clear();
    }
}
