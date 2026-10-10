using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    // BetterContinents pins locations from its location map (the boss altars a map maker placed by hand) and can move the
    // start temple. When Valheim places an older world's locations again it first drops every location not yet placed;
    // with BetterContinents installed the pinned ones were then registered again. A converted map has no BC, so without
    // this its unexplored altars were rolled anew by vanilla's rules. A pin whose zone already holds a location, or was
    // already explored, is left alone: vanilla never spawns a location into a zone that is already generated. The world's
    // "skip default locations" setting is honoured too: a location the map does not pin counts as already placed.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md, Tools/BC_CHUNK_CONVERSION.md (D3)
    internal static class BcLocationPatches
    {
        private const string StartLocationName = "StartTemple";
        private const int LoggedSample = 8;
        private const float NormalizedCentre = 0.5f;

        private static readonly MethodInfo RegisterLocationMethod = AccessTools.Method(typeof(ZoneSystem), "RegisterLocation");
        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZonesRef =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");

        private sealed class PinTally
        {
            internal int Pinned;
            internal int Registered;
            internal int AlreadyThere;
            internal int Explored;
            internal readonly List<string> ZoneTaken = new List<string>();
            internal readonly List<string> NoSuchLocation = new List<string>();
        }

        [HarmonyPatch(typeof(ZoneSystem), "ClearNonPlacedLocations")]
        private static class ReapplyPinnedLocations
        {
            private static void Postfix(ZoneSystem __instance) => Reapply(__instance);
        }

        [HarmonyPatch(typeof(ZoneSystem), "CountNrOfLocation")]
        private static class SkipDefaultLocations
        {
            private static bool Prefix(ZoneSystem.ZoneLocation location, ref int __result)
            {
                var settings = BcMigratedWorld.Generator?.Settings;
                if (settings == null || !settings.SkipDefaultLocations || location == null || location.m_prefabName == StartLocationName)
                    return true;
                __result = location.m_quantity;
                return false;
            }
        }

        private static void Reapply(ZoneSystem zoneSystem)
        {
            var settings = BcMigratedWorld.Generator?.Settings;
            if (settings == null || zoneSystem == null || WorldGenerator.instance == null) return;
            var pins = settings.LocationMap?.RemainingAreas;
            bool hasPins = pins != null && pins.Count > 0;
            if (!hasPins && !settings.OverrideStartPosition) return;

            var locations = zoneSystem.m_locations
                .Where(location => location != null && location.m_enable && location.m_quantity != 0)
                .OrderByDescending(location => location.m_prioritized)
                .ToList();
            var tally = new PinTally();

            if (hasPins)
            {
                foreach (var pin in pins)
                {
                    var location = locations.Find(candidate => candidate.m_prefabName == pin.Key);
                    if (location == null)
                    {
                        tally.NoSuchLocation.Add($"{pin.Key} x{pin.Value.Count}");
                        continue;
                    }
                    foreach (var spot in pin.Value) Pin(zoneSystem, location, WorldPoint(settings, spot), tally);
                }
            }

            string start = "";
            if (settings.OverrideStartPosition)
            {
                var temple = locations.Find(candidate => candidate.m_prefabName == StartLocationName);
                if (temple != null)
                {
                    Pin(zoneSystem, temple, new Vector2(settings.StartPositionX, settings.StartPositionY), tally);
                    start = $" Start temple held at ({settings.StartPositionX:0},{settings.StartPositionY:0}).";
                }
                else start = $" There is no {StartLocationName} location to hold at the start override.";
            }

            FiresLogger.LogInfo($"[BcImport] location map re-applied for '{BcMigratedWorld.WorldName}': {tally.Registered} of "
                + $"{tally.Pinned} pinned spot(s) registered, {tally.AlreadyThere} already there, {tally.Explored} in explored "
                + $"zones left alone, {tally.ZoneTaken.Count} in a zone another location holds"
                + (tally.ZoneTaken.Count > 0 ? $" ({string.Join(", ", tally.ZoneTaken.Take(LoggedSample).ToArray())})" : "")
                + (tally.NoSuchLocation.Count > 0 ? $"; no such location in this game: {string.Join(", ", tally.NoSuchLocation.ToArray())}" : "")
                + "." + start);
        }

        private static Vector2 WorldPoint(BcWorldSettings settings, Vector2 normalized)
            => (normalized - Vector2.one * NormalizedCentre) * settings.TotalSize;

        private static void Pin(ZoneSystem zoneSystem, ZoneSystem.ZoneLocation location, Vector2 point, PinTally tally)
        {
            tally.Pinned++;
            var zone = ZoneSystem.GetZone(new Vector3(point.x, 0f, point.y));
            if (zoneSystem.m_locationInstances.TryGetValue(zone, out var existing))
            {
                if (existing.m_location != null && existing.m_location.m_prefabName == location.m_prefabName) tally.AlreadyThere++;
                else tally.ZoneTaken.Add($"{location.m_prefabName} at ({zone.x},{zone.y}) under {existing.m_location?.m_prefabName ?? "?"}");
                return;
            }
            if (GeneratedZonesRef(zoneSystem).Contains(zone))
            {
                tally.Explored++;
                return;
            }
            float height = WorldGenerator.instance.GetHeight(point.x, point.y);
            RegisterLocationMethod.Invoke(zoneSystem, new object[] { location, new Vector3(point.x, height, point.y), false });
            tally.Registered++;
        }
    }
}
