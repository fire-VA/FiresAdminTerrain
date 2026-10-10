using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Fire 10-05: "in server dev commands there is a toggle for 'noclip with godmode flight' and we want that toggle to work for
    // us in FAT ... even if we have noclip on and we try to fly under the terrain we are forced back up unless there is a cave
    // below." While the local player flies with Server Devcommands' noclip, FAT's own lifts stand down: the void rescue, the
    // cave pop-out guard and the skyland hold. FAT asks Server Devcommands itself (FlyFeatures.NoClipPlayerEnabled: the local
    // player is debug-flying AND its "fly noclip" setting is on), so the toggle means exactly what it means there. Without Server
    // Devcommands nothing changes.
    internal static class FatNoClip
    {
        private const float LogEverySeconds = 5f;
        private const string SdcGuid = "server_devcommands";
        private static MethodInfo s_sdcNoClip;
        private static bool s_resolved;
        private static float s_nextLog;
        private static bool s_failed;

        /// <summary>True while `c` is the local player flying with Server Devcommands' noclip.</summary>
        internal static bool IsOn(Character c)
        {
            if (s_failed || c == null || c != Player.m_localPlayer) return false;
            if (!s_resolved)
            {
                s_resolved = true;
                // Only looked up when Server Devcommands is loaded, so a game without it never logs a missing type.
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(SdcGuid))
                {
                    Type flyFeatures = AccessTools.TypeByName("ServerDevcommands.FlyFeatures");
                    s_sdcNoClip = flyFeatures != null ? AccessTools.Method(flyFeatures, "NoClipPlayerEnabled") : null;
                }
            }
            if (s_sdcNoClip == null) return false;
            try { return (bool)s_sdcNoClip.Invoke(null, null); }
            catch (Exception ex)
            {
                s_failed = true;
                FiresLogger.LogWarning($"[FiresVoxel] reading Server Devcommands' noclip failed ({ex.GetBaseException().Message}); FAT's lifts stay on.");
                return false;
            }
        }

        /// <summary>True (and a Debug line at most every 5 s) when `what` must not move `c` because it flies with noclip.</summary>
        /// <summary>How many times a lift stood down for noclip this session (noclip_test's evidence).</summary>
        internal static int SkipCount { get; private set; }

        internal static bool Skips(Character c, string what)
        {
            if (!IsOn(c)) return false;
            SkipCount++;
            if (Time.time >= s_nextLog)
            {
                s_nextLog = Time.time + LogEverySeconds;
                Vector3 p = c.transform.position;
                FiresLogger.LogDebug($"[FiresVoxel] noclip: {what} skipped for {c.m_name} at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}) "
                    + "(Server Devcommands' noclip flight is on).");
            }
            return true;
        }
    }
}
