using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // Where the start really is for FAT's start objects (Fire, 2026-09-27, Tools\FAT_START_ITEMS.md): the spot a player
    // actually stands on at the StartTemple, height included. On a converted map the start is the player's own town and
    // a player spawns on top of its builds, well above the temple's ground; measured from the temple, the starter and the
    // hammer chest landed tens of metres below the player. A fresh world spawns the player on the temple's own ground.
    internal static class ZoneModeStartSpawn
    {
        private const float SpawnReach = 48f;
        private const float SettledDrift = 0.5f;
        private const float SettleSeconds = 2f;

        private static bool s_known;
        private static Vector3 s_spawn;
        private static Vector3 s_candidate;
        private static float s_candidateSince = -1f;

        internal static bool Known => s_known;

        // The start as a player stands on it. A converted map waits for the first player to stand still within
        // SpawnReach of the StartTemple; a fresh world takes the temple's ground when no player is there yet.
        internal static bool TryGet(Vector3 temple, out Vector3 spawn)
        {
            if (!s_known) Watch(temple);
            if (!s_known && !ZoneModePark.Converted && VoxelZoneStreamer.TryGroundHeight(temple.x, temple.z, out float ground))
            {
                spawn = new Vector3(temple.x, ground, temple.z);
                return true;
            }
            spawn = s_spawn;
            return s_known;
        }

        private static void Watch(Vector3 temple)
        {
            if (!TryFindPlayerNear(temple, out var at))
            {
                s_candidateSince = -1f;
                return;
            }
            if (s_candidateSince < 0f || (at - s_candidate).sqrMagnitude > SettledDrift * SettledDrift)
            {
                s_candidate = at;
                s_candidateSince = Time.time;
                return;
            }
            if (Time.time - s_candidateSince < SettleSeconds) return;
            s_known = true;
            s_spawn = s_candidate;
            FiresLogger.LogInfo($"[FiresZones] the start is where a player stands at the StartTemple: ({s_spawn.x:0},{s_spawn.y:0},{s_spawn.z:0}), "
                + $"{s_spawn.y - temple.y:0} m over the temple.");
        }

        private static bool TryFindPlayerNear(Vector3 temple, out Vector3 at)
        {
            var local = Player.m_localPlayer;
            if (local != null && Flat(local.transform.position, temple) <= SpawnReach)
            {
                at = local.transform.position;
                return true;
            }
            at = default;
            var net = ZNet.instance;
            if (net == null || ZDOMan.instance == null) return false;
            foreach (var peer in net.GetPeers())
            {
                if (peer == null || peer.m_characterID.IsNone()) continue;
                var character = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character == null || Flat(character.GetPosition(), temple) > SpawnReach) continue;
                at = character.GetPosition();
                return true;
            }
            return false;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        internal static void Reset()
        {
            s_known = false;
            s_spawn = default;
            s_candidateSince = -1f;
        }
    }
}
