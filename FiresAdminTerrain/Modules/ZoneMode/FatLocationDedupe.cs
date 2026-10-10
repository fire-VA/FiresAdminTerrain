using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // fat_location_dedupe (FAT 0.2.788; [lead] 10-06, the Infested Mine pairs on ATomTestCloseChunks): a location that
    // was generated twice stands as two LocationProxy ZDOs at one spot, and its interior's objects stand twice over at
    // the same positions, so the two copies cannot be told apart by place. This finds every doubled location (proxies
    // of one location hash within PairRadius) and, inside that location's footprint (the ground radius round the proxy
    // and the interior column 5000 m above it), every EXACT duplicate: the same prefab within SameSpot of another.
    // It keeps the first of each group (lowest ZDOID) and removes the others. Creatures are left alone (they move, so
    // they are never exact duplicates); player-built pieces (a creator set) are never touched.
    //   fat_location_dedupe          dry run: logs every group it would thin, removes nothing
    //   fat_location_dedupe apply    removes them (server or single-player host only)
    // The listing goes to the BepInEx log; the console gets the totals.
    internal static class FatLocationDedupe
    {
        private const float PairRadius = 1f, SameSpot = 0.05f, GroundRadius = 48f, InteriorRadius = 96f;
        private const float InteriorLift = 5000f, InteriorBand = 400f;
        private static readonly int ProxyPrefab = "LocationProxy".GetStableHashCode();
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fat_location_dedupe",
                "[apply] - find locations generated twice (two proxies at one spot) and the exact duplicate objects in their "
                + "footprint; dry run unless 'apply' (server/host, admin)",
                args => Run(args.Length > 1 && args[1].Equals("apply", StringComparison.OrdinalIgnoreCase), args.Context),
                isCheat: true);
        }

        private static int CompareIds(ZDO a, ZDO b)
        {
            int byUser = a.m_uid.UserID.CompareTo(b.m_uid.UserID);
            return byUser != 0 ? byUser : a.m_uid.ID.CompareTo(b.m_uid.ID);
        }

        private static void Run(bool apply, Terminal context)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null || ZNet.instance == null) { context?.AddString("[LocationDedupe] no world loaded."); return; }
            if (apply && !ZNet.instance.IsServer()) { context?.AddString("[LocationDedupe] apply runs on the server or a single-player host only."); return; }
            var all = ObjectsById(zdoMan).Values.Where(z => z != null).ToList();

            // Doubled locations: proxies of one location hash within PairRadius of each other.
            var proxies = all.Where(z => z.GetPrefab() == ProxyPrefab).ToList();
            proxies.Sort(CompareIds);
            var sites = new List<(Vector3 at, List<ZDO> copies)>();
            foreach (var proxy in proxies)
            {
                int hash = proxy.GetInt(ZDOVars.s_location);
                var p = proxy.GetPosition();
                int index = sites.FindIndex(s => s.copies[0].GetInt(ZDOVars.s_location) == hash && Utils.DistanceXZ(s.at, p) <= PairRadius);
                if (index >= 0) sites[index].copies.Add(proxy);
                else sites.Add((p, new List<ZDO> { proxy }));
            }
            var doubled = sites.Where(s => s.copies.Count > 1).ToList();

            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            int removed = 0, groups = 0;
            var seen = new HashSet<ZDOID>();
            foreach (var site in doubled)
            {
                sb.AppendLine(string.Format(inv, "  location hash {0} at ({1:0.0}, {2:0.0}): {3} proxies ({4})", site.copies[0].GetInt(ZDOVars.s_location),
                    site.at.x, site.at.z, site.copies.Count, string.Join(", ", site.copies.Select(c => $"{c.m_uid.UserID}:{c.m_uid.ID}"))));
                var inside = all.Where(z => !seen.Contains(z.m_uid) && InFootprint(site.at, z.GetPosition())).ToList();
                var buckets = new Dictionary<int, List<ZDO>>();
                foreach (var z in inside)
                {
                    if (z.GetLong(ZDOVars.s_creator, 0L) != 0L) continue;                 // a player built it
                    var prefab = ZNetScene.instance?.GetPrefab(z.GetPrefab());
                    if (prefab != null && prefab.GetComponent<Character>() != null) continue;   // creatures move
                    if (!buckets.TryGetValue(z.GetPrefab(), out var list)) buckets[z.GetPrefab()] = list = new List<ZDO>();
                    list.Add(z);
                }
                foreach (var pair in buckets)
                {
                    var list = pair.Value;
                    list.Sort(CompareIds);
                    var kept = new List<ZDO>();
                    foreach (var z in list)
                    {
                        var p = z.GetPosition();
                        if (!kept.Any(k => (k.GetPosition() - p).sqrMagnitude <= SameSpot * SameSpot)) { kept.Add(z); continue; }
                        seen.Add(z.m_uid);
                        removed++;
                        string name = ZNetScene.instance?.GetPrefab(z.GetPrefab())?.name ?? pair.Key.ToString(inv);
                        sb.AppendLine(string.Format(inv, "    {0} {1} {2}:{3} at ({4:0.00}, {5:0.00}, {6:0.00})", apply ? "removed" : "would remove",
                            name, z.m_uid.UserID, z.m_uid.ID, p.x, p.y, p.z));
                        if (apply) Remove(z);
                    }
                    if (kept.Count < list.Count) groups++;
                }
            }
            string head = $"[LocationDedupe] {(apply ? "APPLIED" : "dry run")}: {sites.Count} location(s), {doubled.Count} doubled; "
                + $"{removed} exact duplicate object(s) in {groups} group(s) {(apply ? "removed" : "would be removed")}.";
            FiresLogger.LogInfo(head + (sb.Length > 0 ? Environment.NewLine + sb : ""));
            context?.AddString(head + " The list is in the BepInEx log." + (apply ? " Save the world to keep it." : " Run 'fat_location_dedupe apply' to remove them."));
        }

        private static bool InFootprint(Vector3 site, Vector3 p)
        {
            float flat = Utils.DistanceXZ(site, p);
            if (p.y > InteriorLift - InteriorBand) return flat <= InteriorRadius;
            return flat <= GroundRadius;
        }

        private static void Remove(ZDO zdo)
        {
            zdo.SetOwner(ZDOMan.GetSessionID());
            var instance = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (instance != null) ZNetScene.instance.Destroy(instance.gameObject);
            else ZDOMan.instance.DestroyZDO(zdo);
        }
    }
}
