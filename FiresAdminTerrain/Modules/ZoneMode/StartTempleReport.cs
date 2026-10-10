using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // A character with no bed and no logout point spawns at the StartTemple's location icon (Game.FindSpawnPoint), which
    // the server sends to each peer when it joins (ZoneSystem.OnNewPeer) and after a location with an icon is placed. On a
    // BetterContinents world FAT places the temple at BC's start (BcLocationPatches), and a peer on a BC world only gets
    // its icons after FAT's join sync (BcJoinSync holds PeerInfo). These lines say where the temple is on the server and
    // where each client will spawn, so "waiting on (0,0,0)" can be told apart from a missing or doubled temple (R53).
    internal static class StartTempleReport
    {
        private const string TempleName = "StartTemple";
        private static bool s_serverReported;

        [HarmonyPatch(typeof(ZoneSystem), "SendLocationIcons")]
        private static class ZoneSystem_SendLocationIcons
        {
            private static void Prefix(ZoneSystem __instance)
            {
                if (s_serverReported || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                s_serverReported = true;
                var temples = new List<Vector3>();
                foreach (var instance in __instance.m_locationInstances.Values)
                    if (instance.m_location != null && instance.m_location.m_prefabName == TempleName) temples.Add(instance.m_position);
                if (temples.Count == 1)
                    FiresLogger.LogInfo($"[FiresWorld] StartTemple at ({temples[0].x:0},{temples[0].z:0}); new characters spawn there.");
                else if (temples.Count == 0)
                    FiresLogger.LogError($"[FiresWorld] this world has NO {TempleName} location: a character with no bed has nowhere to spawn.");
                else
                    FiresLogger.LogWarning($"[FiresWorld] this world has {temples.Count} {TempleName} locations ("
                        + string.Join(", ", temples.ConvertAll(p => $"({p.x:0},{p.z:0})").ToArray())
                        + "); a client spawns at whichever icon it reads first.");
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "RPC_LocationIcons")]
        private static class ZoneSystem_RPC_LocationIcons
        {
            private static void Postfix(ZoneSystem __instance)
            {
                if (__instance.GetLocationIcon(TempleName, out var temple))
                    FiresLogger.LogInfo($"[FiresWorld] spawn target: {TempleName} ({temple.x:0},{temple.z:0}).");
                else
                    FiresLogger.LogWarning($"[FiresWorld] the server's location icons hold no {TempleName}: a character with no bed "
                        + "waits at (0,0,0) until one arrives.");
            }
        }

        internal static void OnWorldTeardown() => s_serverReported = false;
    }
}
