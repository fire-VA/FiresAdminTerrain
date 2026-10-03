using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // A dedicated server parks its own reference position far off the map: the dedi build's Game.FixedUpdate ends with
    // ZNet.SetReferencePosition(new Vector3(1000000f, 0f, 1000000f)) every physics frame, so the server has no local area.
    // Its ZoneSystem.Update still runs CreateLocalZones and CreateGhostZones round that point, which spawned the off-map
    // ocean zones near (15625, 15625) and the Leviathans their vegetation places, saved as ZDOs in every world a dedi ran
    // (the lead / Fire 2026-09-30: "prevent it from loading the zone and placing objects"). Here the dedi refuses every
    // zone whose centre lies beyond OffMapMetres, first in FAT's one ZoneSystem.SpawnZone hook, and reports what earlier
    // sessions saved out there. Removing those objects is a separate cfg choice, off by default.
    internal static class OffMapZones
    {
        internal enum SavedObjectsMode { Off, List, Remove }

        // The world ends at about 10,500 m; a peer sailing at the edge loads zones a few hundred metres further.
        private const float OffMapMetres = 12000f;
        private const int ListedPrefabs = 8;

        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        private static ConfigEntry<SavedObjectsMode> s_mode;
        private static ZNet s_reportedFor;
        private static ZNet s_refusedFor;

        internal static void Bind(ConfigFile cfg)
        {
            s_mode = cfg.Bind("09 - Off-map objects", "01 Saved objects beyond the world edge", SavedObjectsMode.Off,
                "A dedicated server used to spawn ocean zones round its own off-map reference position (1,000,000, 1,000,000) and "
                + "save what they grew there (Leviathans). FAT 0.2.657 stops that; this handles what older sessions saved. Off "
                + "(default): one line at world load counts them and nothing is changed. List: also logs each object's prefab and "
                + "position. Remove (server only): destroys every object whose position is more than " + OffMapMetres.ToString("0", CultureInfo.InvariantCulture)
                + " m from the world centre, logging each; nothing inside the map is ever touched.");
        }

        // First in FAT's ZoneSystem.SpawnZone prefix: true = do not spawn this zone at all (no terrain, vegetation,
        // locations or spawners). Only on a dedicated server; a client never pokes zones that far out.
        internal static bool Refuses(Vector2s zoneID)
        {
            if (!FiresCore.Lifecycle.FiresMod.IsDedicatedServer) return false;
            Vector3 centre = ZoneSystem.GetZonePos(zoneID);
            if (new Vector2(centre.x, centre.z).magnitude <= OffMapMetres) return false;
            if (s_refusedFor != ZNet.instance)
            {
                s_refusedFor = ZNet.instance;
                FiresLogger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[FiresZones] dedi: skipped off-map zone generation around ({0:0}, {1:0}) (zone {2},{3}); a dedicated server parks "
                    + "its own reference position there, and no zone more than {4:0} m out is spawned.", centre.x, centre.z, zoneID.x, zoneID.y, OffMapMetres));
            }
            return true;
        }

        // From FAT's ZNet.Start hook, once the server's world is loaded.
        internal static void ReportSaved()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || s_reportedFor == ZNet.instance) return;
            s_reportedFor = ZNet.instance;
            var offMap = ObjectsById(ZDOMan.instance).Values.Where(IsOffMap).ToList();
            SavedObjectsMode mode = s_mode != null ? s_mode.Value : SavedObjectsMode.Off;
            if (offMap.Count == 0)
            {
                FiresLogger.LogInfo("[FiresZones] saved objects beyond the world edge: none.");
                return;
            }
            string byPrefab = string.Join(", ", offMap.GroupBy(PrefabName).OrderByDescending(g => g.Count()).Take(ListedPrefabs)
                .Select(g => $"{g.Key} x{g.Count()}"));
            FiresLogger.LogWarning($"[FiresZones] saved objects beyond the world edge ({OffMapMetres:0} m out): {offMap.Count} ({byPrefab}); "
                + $"'01 Saved objects beyond the world edge' = {mode}.");
            if (mode == SavedObjectsMode.Off) return;
            bool remove = mode == SavedObjectsMode.Remove;
            int removed = 0;
            foreach (var zdo in offMap)
            {
                Vector3 p = zdo.GetPosition();
                FiresLogger.LogInfo(string.Format(CultureInfo.InvariantCulture, "[FiresZones] off-map {0} {1} at ({2:0.0}, {3:0.0}, {4:0.0})",
                    remove ? "REMOVED" : "object", PrefabName(zdo), p.x, p.y, p.z));
                if (!remove) continue;
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
                removed++;
            }
            if (remove) FiresLogger.LogWarning($"[FiresZones] removed {removed} saved object(s) beyond the world edge.");
        }

        private static bool IsOffMap(ZDO zdo)
        {
            if (zdo == null) return false;
            Vector3 p = zdo.GetPosition();
            return new Vector2(p.x, p.z).magnitude > OffMapMetres;
        }

        private static string PrefabName(ZDO zdo)
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : "#" + zdo.GetPrefab().ToString(CultureInfo.InvariantCulture);
        }
    }
}
