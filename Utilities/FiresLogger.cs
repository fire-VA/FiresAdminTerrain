using System;
using UnityEngine;

namespace VerdantsAscent
{
    public static class FiresLogger
    {
        public static bool VerboseEnabled
        {
            get
            {
                try
                {
                    var cfg = VerdantsAscent.Managers.ConfigManager.Instance;
                    if (cfg != null && cfg.configVerboseLogging != null)
                        return cfg.configVerboseLogging.Value;
                }
                catch { }
                return false; // default to false - verbose logging should be opt-in
            }
        }

        public static void LogInfo(string message)
        {
            Debug.Log($"[FiresAdminTerrain] {message}");
        }

        public static void LogVerbose(string message)
        {
            if (!VerboseEnabled) return;
            Debug.Log($"[FiresAdminTerrain] {message}");
        }

        public static void LogWarning(string message)
        {
            Debug.LogWarning($"[FiresAdminTerrain] {message}");
        }

        public static void LogError(string message)
        {
            Debug.LogError($"[FiresAdminTerrain] {message}");
        }
    }
}