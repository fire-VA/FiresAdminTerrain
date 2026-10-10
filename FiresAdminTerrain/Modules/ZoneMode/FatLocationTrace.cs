using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // FAT 0.2.787 ([lead] 10-06, the Infested Mine pairs on ATomTestCloseChunks: zones (-28,25), (-28,26) and (-28,27)
    // each came out of their FIRST generation holding two LocationProxy and two interiors). Two parts:
    //  1) Trace, at Debug: every ZoneSystem.SpawnZone that will generate a zone (Ghost/Full on a zone not yet generated)
    //     and every PlaceLocations that will place a location, with the zone, mode, location, its position, m_placed
    //     and a short caller stack, so one round names the path that generates a zone twice.
    //  2) Guard: a SpawnZone for a zone whose generation is still running (re-entered from inside its own
    //     PlaceLocations / PlaceVegetation) is refused with a Warning, so a zone can never be generated twice at once.
    [HarmonyPatch]
    internal static class FatLocationTrace
    {
        private const int StackFrames = 8;
        private static readonly HashSet<Vector2s> s_generating = new HashSet<Vector2s>();
        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZones =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        [HarmonyPrefix]
        private static bool SpawnZonePrefix(ZoneSystem __instance, Vector2s zoneID, ZoneSystem.SpawnMode mode,
            ref GameObject root, ref bool __result, out bool __state)
        {
            __state = false;
            if (mode != ZoneSystem.SpawnMode.Ghost && mode != ZoneSystem.SpawnMode.Full) return true;
            var generated = GeneratedZones(__instance);
            if (generated == null || generated.Contains(zoneID)) return true;
            if (s_generating.Contains(zoneID))
            {
                FiresLogger.LogWarning($"[LocationTrace] SpawnZone({zoneID.x},{zoneID.y}, {mode}) re-entered while that zone's "
                    + $"generation is still running; refused so its location is not placed twice. Caller:{Caller()}");
                root = null;
                __result = false;
                return false;
            }
            s_generating.Add(zoneID);
            __state = true;
            return true;
        }

        // 0.2.789: the line is written only when the zone really was generated (vanilla returns early, ungenerated, while
        // the terrain is not ready; the prefix logged those retries as generations).
        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        [HarmonyFinalizer]
        private static void SpawnZoneFinalizer(ZoneSystem __instance, Vector2s zoneID, ZoneSystem.SpawnMode mode, bool __state)
        {
            if (!__state) return;
            s_generating.Remove(zoneID);
            var generated = GeneratedZones(__instance);
            if (generated == null || !generated.Contains(zoneID)) return;
            __instance.m_locationInstances.TryGetValue(zoneID, out var instance);
            FiresLogger.LogDebug($"[LocationTrace] SpawnZone({zoneID.x},{zoneID.y}, {mode}) generated the zone"
                + (instance.m_location != null ? $"; location {instance.m_location.m_prefabName} at ({instance.m_position.x:0},{instance.m_position.z:0}), placed {instance.m_placed}" : "; no location")
                + $". Caller:{Caller()}");
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        [HarmonyPrefix]
        private static void PlaceLocationsPrefix(ZoneSystem __instance, Vector2s zoneID, ZoneSystem.SpawnMode mode)
        {
            if (!__instance.m_locationInstances.TryGetValue(zoneID, out var instance) || instance.m_location == null || instance.m_placed) return;
            FiresLogger.LogDebug($"[LocationTrace] PlaceLocations({zoneID.x},{zoneID.y}, {mode}) places {instance.m_location.m_prefabName} "
                + $"at ({instance.m_position.x:0.0},{instance.m_position.y:0.0},{instance.m_position.z:0.0}). Caller:{Caller()}");
        }

        private static string Caller()
        {
            var sb = new StringBuilder();
            var frames = new StackTrace(2, false).GetFrames();
            if (frames == null) return " ?";
            int shown = 0;
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method == null || method.DeclaringType == typeof(FatLocationTrace)) continue;
                sb.Append(shown == 0 ? " " : " < ").Append(method.DeclaringType?.Name ?? "?").Append('.').Append(method.Name);
                if (++shown >= StackFrames) break;
            }
            return sb.ToString();
        }
    }
}
