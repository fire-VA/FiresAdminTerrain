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
    // sessions saved out there. Since 0.2.719 the dedi takes the saved off-map _ZoneCtrl out of the world and its save at load
    // (they came back every session otherwise); removing any other off-map object is a separate cfg choice, off by default.
    internal static class OffMapZones
    {
        internal enum SavedObjectsMode { Off, List, Remove }

        // A peer sailing at the edge loads zones a few hundred metres past it. 0.2.721: from the world's RECORDED size
        // (FatWorldSize; 10,500 + 1,500 = 12,000 m on a vanilla-size world), never a fixed 12 km, which on an Expand World
        // Size world would have refused real zones and, with Remove on, destroyed real objects. Until the world's profile is
        // applied the size is not known: then a finite fallback beyond any supported world counts as off the map, so the
        // refusal and the _ZoneCtrl purge still catch the dedi's parking spot (about 1,000,000 m out) from its first frame
        // ([worldgen] 10-05: any gap regenerates the zones 0.2.719 purged).
        private const float EdgeMargin = 1500f;
        private const float UnresolvedOffMapMetres = 50000f;

        // 0.2.731: what the dedi refuses and purges ON ITS OWN is keyed to its PARKING SPOT, never to the world's size: a zone
        // (or a _ZoneCtrl) counts only when its centre lies past ParkingMetres. The dedi parks at (1,000,000, 0, 1,000,000) and no
        // world FAT supports reaches 100 km, so this catches the parked grid and nothing in any real world. The size-based edge
        // below missed a BetterContinents world's own radius: on a BC world larger than 12 km a dedi on 0.2.719-0.2.730 purged
        // its REAL _ZoneCtrl (2,984 on SkyDryConvertTest, 10-05) and refused its real zones past 12 km.
        private const float ParkingMetres = 100000f;

        private static bool SizeKnown => Voxel.FiresWorldProfile.Current != null;

        // The world's edge plus a margin, for the report and the opt-in Remove only: the recorded size (vanilla / Expand World
        // Size), or a migrated BetterContinents world's own total radius when that is larger.
        private static float OffMapMetres
        {
            get
            {
                float edge = SizeKnown ? Voxel.FatWorldSize.EdgeRadius + EdgeMargin : UnresolvedOffMapMetres;
                var bc = BcImport.BcMigratedWorld.Generator?.Settings;
                if (bc != null && bc.TotalRadius + EdgeMargin > edge) edge = bc.TotalRadius + EdgeMargin;
                return edge;
            }
        }
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
                + "position. Remove (server only): destroys every object whose position is more than 1500 m past the world's edge "
                + "(12000 m from the centre on a vanilla-size world; the world's own size on an Expand World Size or converted "
                + "BetterContinents world). The dedi's own parked zones (past 100,000 m) are refused and cleared whatever this says), "
                + "logging each; nothing inside the map is ever touched.");
        }

        // First in FAT's ZoneSystem.SpawnZone prefix: true = do not spawn this zone at all (no terrain, vegetation,
        // locations or spawners). Only on a dedicated server; a client never pokes zones that far out.
        internal static bool Refuses(Vector2s zoneID)
        {
            if (!FiresCore.Lifecycle.FiresMod.IsDedicatedServer) return false;
            Vector3 centre = ZoneSystem.GetZonePos(zoneID);
            if (new Vector2(centre.x, centre.z).magnitude <= ParkingMetres) return false;
            if (s_refusedFor != ZNet.instance)
            {
                s_refusedFor = ZNet.instance;
                FiresLogger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[FiresZones] dedi: skipped off-map zone generation around ({0:0}, {1:0}) (zone {2},{3}); a dedicated server parks "
                    + "its own reference position there, and no zone more than {4:0} m out is spawned.", centre.x, centre.z, zoneID.x, zoneID.y, ParkingMetres));
            }
            return true;
        }

        // From FAT's ZNet.Start hook, once the server's world is loaded.
        internal static void ReportSaved()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || s_reportedFor == ZNet.instance) return;
            s_reportedFor = ZNet.instance;
            if (!SizeKnown)
                FiresLogger.LogWarning($"[FiresZones] saved objects beyond the world edge: the world's size is not known yet (no world "
                    + $"profile applied), so only objects past the {UnresolvedOffMapMetres:0} m fallback count as off the map this time.");
            var offMap = ObjectsById(ZDOMan.instance).Values.Where(IsOffMap).ToList();
            SavedObjectsMode mode = s_mode != null ? s_mode.Value : SavedObjectsMode.Off;
            if (offMap.Count == 0)
            {
                FiresLogger.LogInfo("[FiresZones] saved objects beyond the world edge: none.");
                return;
            }
            string byPrefab = string.Join(", ", offMap.GroupBy(PrefabName).OrderByDescending(g => g.Count()).Take(ListedPrefabs)
                .Select(g => $"{g.Key} x{g.Count()}"));
            // 0.2.734 (the lead 10-05, a clean log): when everything out there is the dedi's own parked grid of _ZoneCtrl, this
            // is the expected one-time clean-up of what a pre-0.2.657 session left (or a clone inherited; ATomTestCloseChunks
            // 22:10 held the same 81 ATomTest lost at 19:29), so it is said at Info. Anything else off the map stays a Warning.
            bool onlyParkedControllers = FiresCore.Lifecycle.FiresMod.IsDedicatedServer
                && offMap.TrueForAll(z => z.GetPrefab() == ZoneCtrlHash && IsParked(z));
            string found = $"[FiresZones] saved objects beyond the world edge ({OffMapMetres:0} m out): {offMap.Count} ({byPrefab}); "
                + $"'01 Saved objects beyond the world edge' = {mode}.";
            if (onlyParkedControllers) FiresLogger.LogInfo(found + " All are the dedi's own parked zone controllers from an older session; removed below.");
            else FiresLogger.LogWarning(found);
            // 0.2.719 (Fire 10-05: "why do these zone controls always come back"): the off-map _ZoneCtrl are vanilla's markers of
            // zones a dedi generated round its parking spot before FAT 0.2.657 refused them. Those zones stay in the generated set,
            // so nothing places them again; the dedi takes them out of the save on every load until none are left. A plain
            // DestroyZDO (FDT's CorruptScan) never stuck: it emptied their save chunk, and vanilla reloads an emptied chunk's old
            // file (ZoneModePark.PurgeFromSave). Everything else off the map still follows the cfg.
            bool remove = mode == SavedObjectsMode.Remove;
            var purge = new List<ZDO>();
            foreach (var zdo in offMap)
            {
                bool zoneCtrl = zdo.GetPrefab() == ZoneCtrlHash;
                // Automatically only the parked grid's controllers (past ParkingMetres); anything else only with Remove.
                bool parked = zoneCtrl && IsParked(zdo);
                bool goes = FiresCore.Lifecycle.FiresMod.IsDedicatedServer && parked || remove;
                if (goes) purge.Add(zdo);
                if (!goes && mode == SavedObjectsMode.Off) continue;
                Vector3 p = zdo.GetPosition();
                string line = string.Format(CultureInfo.InvariantCulture, "[FiresZones] off-map {0} {1} at ({2:0.0}, {3:0.0}, {4:0.0}), sector {5}, owner {6}",
                    goes ? "REMOVED" : "object", PrefabName(zdo), p.x, p.y, p.z, zdo.GetSectorIndex().Sector, zdo.GetOwner());
                if (zoneCtrl && !remove) FiresLogger.LogDebug(line);
                else FiresLogger.LogInfo(line);
            }
            if (purge.Count == 0) return;
            int purged = ZoneModePark.PurgeFromSave(purge);
            string removed = $"[FiresZones] removed {purged}/{purge.Count} saved object(s) beyond the world edge from the world AND its "
                + "save; the next save rewrites every chunk, and the next load deletes the old chunk files as orphans (vanilla's "
                + "\"Removing orphan CHUNK file\" lines).";
            if (onlyParkedControllers && purged == purge.Count) FiresLogger.LogInfo(removed);
            else FiresLogger.LogWarning(removed);
        }

        private static readonly int ZoneCtrlHash = "_ZoneCtrl".GetStableHashCode();

        private static readonly int s_registryMirrorHash = ZoneModeRegistryMirror.PrefabName.GetStableHashCode();

        private static bool IsOffMap(ZDO zdo)
        {
            if (zdo == null) return false;
            // 0.2.775 (fat_load_test Q1: "saved objects beyond the world edge: 1 (FiresZonesRegistry x1)"): FAT's own registry
            // mirror lives past the edge on purpose (ZoneModeRegistryMirror, 15 km out, where no peer loads it). It is neither
            // reported nor removed.
            if (zdo.GetPrefab() == s_registryMirrorHash) return false;
            Vector3 p = zdo.GetPosition();
            return new Vector2(p.x, p.z).magnitude > Mathf.Min(OffMapMetres, ParkingMetres);
        }

        // 0.2.736 (fat_load_test): loaded objects past the dedi's parking radius, which nothing in a world should hold.
        internal static int ParkedCount()
            => ZDOMan.instance == null ? 0 : ObjectsById(ZDOMan.instance).Values.Count(z => z != null && IsParked(z));

        private static bool IsParked(ZDO zdo)
        {
            Vector3 p = zdo.GetPosition();
            return new Vector2(p.x, p.z).magnitude > ParkingMetres;
        }

        private static string PrefabName(ZDO zdo)
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : "#" + zdo.GetPrefab().ToString(CultureInfo.InvariantCulture);
        }
    }
}
