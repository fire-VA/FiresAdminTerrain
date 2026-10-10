using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // fat_zdo_near (FAT 0.2.782; [generator] / [lead] 10-06: an Infested Mine placed several times over on ATomTestCloseChunks, and
    // the question is WHO made each copy). READ-ONLY: it walks this peer's ZDO table and prints; it changes, claims, loads or saves
    // nothing. Every ZDO of the named prefabs within the radius, with its ZDOID (uid:id - NOTE vanilla re-keys every ZDO loaded from the save to uid 1,
    // "unknown former user"; only ZDOs made THIS run carry a real session uid), owner, the creator field Valheim stamps on player-placed objects, revision and
    // position, sorted so copies at one spot sit together. Runs on a client (the ZDOs synced round it) or a server console.
    //   fat_zdo_near <x> <z> <radius> <prefab>[,|+<prefab>...]
    //   fat_zdo_near -1792 1585 100 DG_DvergrTown|LocationProxy|Spawner_Seeker_respawn_240
    // The full listing goes to the BepInEx log at Info (one block); the console gets the summary.
    public static class FatZdoNear
    {
        private const int MaxListed = 400;

        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        public static void Register()
        {
            new Terminal.ConsoleCommand("fat_zdo_near",
                "fat_zdo_near <x> <z> <radius> <prefab>[,|+<prefab>...] - READ-ONLY: every ZDO of those prefabs within the radius, with ZDOID uid:id (creating session), owner, creator, revision (log)",
                args => Run(args));
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            var inv = CultureInfo.InvariantCulture;
            if (args.Length < 5 || !float.TryParse(args[1], NumberStyles.Float, inv, out float x)
                || !float.TryParse(args[2], NumberStyles.Float, inv, out float z) || !float.TryParse(args[3], NumberStyles.Float, inv, out float radius))
            {
                args.Context?.AddString("usage: fat_zdo_near <x> <z> <radius> <prefab>[,|+<prefab>...]");
                return;
            }
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) { args.Context?.AddString("no ZDOMan (not in a world)"); return; }
            var names = new Dictionary<int, string>();
            // '|' and '+' as well as ',' (0.2.783): FDT's AutoCensus splits its command list on ',' so a comma list never arrives whole.
            foreach (string raw in string.Join(" ", args.Args.Skip(4)).Split(',', '|', '+'))
            {
                string name = raw.Trim();
                if (name.Length > 0) names[name.GetStableHashCode()] = name;
            }
            if (names.Count == 0) { args.Context?.AddString("name at least one prefab"); return; }

            var found = new List<ZDO>();
            float r2 = radius * radius;
            foreach (var zdo in ObjectsById(zdoMan).Values)
            {
                if (zdo == null || !names.ContainsKey(zdo.GetPrefab())) continue;
                Vector3 p = zdo.GetPosition();
                float dx = p.x - x, dz = p.z - z;
                if (dx * dx + dz * dz <= r2) found.Add(zdo);
            }
            found.Sort((a, b) =>
            {
                int byName = a.GetPrefab().CompareTo(b.GetPrefab());
                if (byName != 0) return byName;
                Vector3 pa = a.GetPosition(), pb = b.GetPosition();
                int byX = Mathf.RoundToInt(pa.x).CompareTo(Mathf.RoundToInt(pb.x));
                if (byX != 0) return byX;
                int byZ = Mathf.RoundToInt(pa.z).CompareTo(Mathf.RoundToInt(pb.z));
                return byZ != 0 ? byZ : a.m_uid.UserID.CompareTo(b.m_uid.UserID) != 0 ? a.m_uid.UserID.CompareTo(b.m_uid.UserID) : a.m_uid.ID.CompareTo(b.m_uid.ID);
            });

            var sb = new StringBuilder(found.Count * 120 + 256);
            sb.AppendLine(string.Format(inv, "[FatZdoNear] {0} ZDO(s) of {1} within {2:0} m of ({3:0.0}, {4:0.0}) on this {5} (session {6}); read-only:",
                found.Count, string.Join(",", names.Values), radius, x, z, ZNet.instance != null && ZNet.instance.IsServer() ? "server" : "client",
                ZDOMan.GetSessionID()));
            var sessions = new Dictionary<long, int>();
            int listed = 0;
            foreach (var zdo in found)
            {
                sessions.TryGetValue(zdo.m_uid.UserID, out int n);
                sessions[zdo.m_uid.UserID] = n + 1;
                if (listed++ >= MaxListed) continue;
                Vector3 p = zdo.GetPosition();
                sb.AppendLine(string.Format(inv, "  {0,-28} {1}:{2,-8} owner {3,-20} creator {4,-20} rev {5,-6} at ({6:0.00}, {7:0.00}, {8:0.00})",
                    names[zdo.GetPrefab()], zdo.m_uid.UserID, zdo.m_uid.ID, zdo.GetOwner(), zdo.GetLong(ZDOVars.s_creator, 0L), zdo.DataRevision,
                    p.x, p.y, p.z));
            }
            if (found.Count > MaxListed) sb.AppendLine($"  ... {found.Count - MaxListed} more not listed");
            sb.Append("  by creating session (ZDOID uid): ").Append(string.Join(", ", sessions.OrderByDescending(s => s.Value).Select(s => $"{s.Key} x{s.Value}")));
            FiresLogger.LogInfo(sb.ToString());
            args.Context?.AddString($"[FatZdoNear] {found.Count} ZDO(s); {sessions.Count} creating session(s); the listing is in the BepInEx log.");
        }
    }
}
