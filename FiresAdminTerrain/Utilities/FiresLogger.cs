using FiresCore.Logging;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    // FiresAdminTerrain's tagged logger. Thin wrapper over the shared FiresCore FiresLog engine
    // (source-linked from FiresUnifiedCore).
    public static class FiresLogger
    {
        private static readonly FiresLog Log = new FiresLog(
            "FiresAdminTerrain",
            () => ConfigManager.Instance?.configVerboseLogging?.Value ?? false);

        public static bool VerboseEnabled => Log.VerboseEnabled;

        public static void LogInfo(string message) => Log.Info(message);

        public static void LogVerbose(string message) => Log.Verbose(message);

        // 0.2.695 (Fire 10-01: "put them as debug logs and set our configs to see them"): per-event diagnostics, shown at
        // Core's [Logging] Log Level = Debug (FiresLog.Debug, Core 0.2.279+); the rig runs at Debug.
        public static bool DebugEnabled => FiresLogLevel.DebugOn;

        public static void LogDebug(string message) => Log.Debug(message);

        public static void LogWarning(string message) => Log.Warning(message);

        public static void LogError(string message) => Log.Error(message);
    }
}
