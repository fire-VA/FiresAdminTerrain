using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;

namespace VerdantsAscent.Terrain.Zones
{
    // A copy of the zone registry inside the world save: one persistent ZDO at a fixed spot beyond the world's edge that
    // no peer ever loads, rewritten after every registry save. A world copied to another server without FAT's
    // FiresWorldGen folder still knows it is a zone world: when the registry file is missing, the server restores it
    // from this copy before any terrain is built (ZoneModeRegistry.MarkWorldLoaded). Inert until the fireszones bundle
    // carries the anchor prefab.
    internal static class ZoneModeRegistryMirror
    {
        internal const string PrefabName = "FiresZonesRegistry";
        private static readonly Vector3 Position = new Vector3(0f, 0f, -15000f);
        private static readonly int BytesHash = "fires_zones_registry".GetStableHashCode();
        private static readonly int UidHash = "fires_zones_uid".GetStableHashCode();
        private static readonly List<ZDO> s_scratch = new List<ZDO>();

        private static byte[] s_pending;
        private static long s_pendingUid;
        private static ZDOID s_mirror = ZDOID.None;
        private static bool s_reportedMissingPrefab;

        // Server, after every registry save. Held until the save's own objects are loaded, so an existing copy is found
        // and updated rather than a second one made.
        internal static void Write(byte[] bytes, long uid)
        {
            s_pending = bytes;
            s_pendingUid = uid;
            if (ZoneModeRegistry.WorldLoaded) Flush();
        }

        internal static void Flush()
        {
            if (s_pending == null || ZDOMan.instance == null) return;
            if (!ZoneModePrefabs.TryGet(PrefabName, out var anchor) || anchor.GetComponent<ZNetView>() == null)
            {
                if (!s_reportedMissingPrefab)
                    FiresLogger.LogWarning($"[FiresZones] '{PrefabName}' is not a networked prefab in the fireszones bundle: the zone "
                        + "registry has no copy inside the world save this session.");
                s_reportedMissingPrefab = true;
                return;
            }
            var zdo = Find(s_pendingUid) ?? Create(anchor);
            zdo.Set(BytesHash, s_pending);
            zdo.Set(UidHash, s_pendingUid);
            s_pending = null;
        }

        internal static bool TryRead(long uid, out byte[] bytes)
        {
            bytes = null;
            if (ZDOMan.instance == null) return false;
            var zdo = Find(uid);
            bytes = zdo?.GetByteArray(BytesHash);
            return bytes != null && bytes.Length > 0;
        }

        private static ZDO Find(long uid)
        {
            var man = ZDOMan.instance;
            if (!s_mirror.IsNone())
            {
                var cached = man.GetZDO(s_mirror);
                if (cached != null && cached.GetLong(UidHash) == uid) return cached;
                s_mirror = ZDOID.None;
            }
            int prefab = PrefabName.GetStableHashCode();
            s_scratch.Clear();
            man.FindSectorObjects(ZoneSystem.GetZone(Position), new SimulationDistance(0, 0), s_scratch);
            foreach (var zdo in s_scratch)
            {
                if (zdo == null || zdo.GetPrefab() != prefab || zdo.GetLong(UidHash) != uid) continue;
                s_mirror = zdo.m_uid;
                return zdo;
            }
            return null;
        }

        private static ZDO Create(GameObject anchor)
        {
            var zdo = NetworkObjectHelper.CreateZdo(anchor, Position, Quaternion.identity);
            zdo.Persistent = true;
            s_mirror = zdo.m_uid;
            return zdo;
        }

        internal static void Reset()
        {
            s_pending = null;
            s_mirror = ZDOID.None;
            s_reportedMissingPrefab = false;
        }
    }
}
