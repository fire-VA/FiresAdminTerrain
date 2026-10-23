using System.Collections.Generic;
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

        // Per-event diagnostics are debug logs, shown at
        // Core's [Logging] Log Level = Debug (FiresLog.Debug, Core 0.2.279+).
        public static bool DebugEnabled => FiresLogLevel.DebugOn;

        public static void LogDebug(string message) => Log.Debug(message);

        public static void LogWarning(string message) { Count(ref s_warnings, message); Log.Warning(message); }

        public static void LogError(string message) { Count(ref s_errors, message); Log.Error(message); }

        // For fat_load_test's clean-log check: how many warnings and errors FAT has said this process, and
        // the first few, so a test can read them without the log file.
        private static int s_warnings, s_errors;
        private static readonly List<string> s_first = new List<string>();
        private const int FirstKept = 5;
        internal static int Warnings => s_warnings;
        internal static int Errors => s_errors;

        internal static List<string> FirstProblems() { lock (s_first) return new List<string>(s_first); }

        private static void Count(ref int counter, string message)
        {
            System.Threading.Interlocked.Increment(ref counter);
            lock (s_first) { if (s_first.Count < FirstKept) s_first.Add(message ?? ""); }
        }
    }
}
