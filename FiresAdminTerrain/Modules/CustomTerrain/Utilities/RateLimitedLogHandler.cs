using System;
using UnityEngine;

namespace VerdantsAscent.Terrain.Utilities
{
    /// <summary>
    /// Filters Unity logs to reduce spam for known noisy messages.
    /// Currently throttles:
    /// - "Failed to send data k_EResultLimitExceeded" to once every 30 seconds
    /// - "Failed to find expected binary shader data" (suppressed entirely - we handle shader replacement)
    /// - Known Valheim NRE bugs that spam logs (ShieldGenerator.OnDestroy, ArcheryTarget.Start)
    /// - "Setting linear/angular velocity of a kinematic body" (suppressed - BepInEx path via logHandler)
    /// NOTE: Native physics engine warnings emitted outside the Debug.Log pipeline are suppressed
    /// at the source by ensuring rigidbodies are non-kinematic before Character.AttachStart.
    /// </summary>
    internal sealed class RateLimitedLogHandler : ILogHandler
    {
        private readonly ILogHandler _inner;
        private DateTime _lastLimitExceeded = DateTime.MinValue;
        private static readonly TimeSpan LimitInterval = TimeSpan.FromSeconds(30);

        // Count of suppressed shader warnings for diagnostic purposes
        private int _suppressedShaderWarnings = 0;

        // Count of suppressed Valheim NRE bugs
        private int _suppressedValheimNreBugs = 0;

        // Count of suppressed kinematic velocity warnings (native physics engine spam)
        private int _suppressedKinematicWarnings = 0;

        public RateLimitedLogHandler(ILogHandler inner)
        {
            _inner = inner ?? Debug.unityLogger.logHandler;
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            if (ShouldSuppress(format, args, logType)) return;
            _inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            // Suppress known Valheim NRE bugs that spam logs
            if (exception is NullReferenceException && ShouldSuppressValheimNre(exception))
            {
                _suppressedValheimNreBugs++;
                // Log a summary occasionally instead of every error
                if (_suppressedValheimNreBugs == 1 || _suppressedValheimNreBugs % 50 == 0)
                {
                    _inner.LogFormat(LogType.Log, null,
                        "[FiresAdminTerrain] Suppressed {0} known Valheim NRE bugs (ShieldGenerator, ArcheryTarget, etc.)",
                        _suppressedValheimNreBugs);
                }
                return;
            }

            _inner.LogException(exception, context);
        }

        /// <summary>
        /// Checks if an exception is a known Valheim bug that should be suppressed.
        /// These are NullReferenceExceptions in Valheim's own code that spam logs.
        /// </summary>
        private bool ShouldSuppressValheimNre(Exception exception)
        {
            if (exception?.StackTrace == null) return false;

            string stackTrace = exception.StackTrace;

            // Known Valheim NRE bugs - these are in Valheim's code, not ours
            // ShieldGenerator.OnDestroy() - happens when shield generators are destroyed
            if (stackTrace.Contains("ShieldGenerator.OnDestroy"))
                return true;

            // ArcheryTarget.Start() - happens when archery targets spawn
            if (stackTrace.Contains("ArcheryTarget.Start"))
                return true;

            // Add more known Valheim bugs here as needed

            return false;
        }

        private bool ShouldSuppress(string format, object[] args, LogType logType)
        {
            string message = format;
            if (args != null && args.Length > 0)
            {
                try { message = string.Format(format, args); }
                catch { /* fallback to raw format */ }
            }

            if (string.IsNullOrEmpty(message)) return false;

            // Suppress kinematic velocity warnings entirely - harmless but spammy.
            // These are emitted by the native Unity physics engine when Valheim's own
            // Character/BaseAI code calls SetMoveDir or sets rb.velocity on a kinematic
            // rigidbody (e.g. while a companion is attached to a chair via AttachStart).
            if (logType == LogType.Warning &&
                message.IndexOf("Setting linear velocity of a kinematic body is not supported", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _suppressedKinematicWarnings++;
                return true;
            }

            if (logType == LogType.Warning &&
                message.IndexOf("Setting angular velocity of a kinematic body is not supported", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _suppressedKinematicWarnings++;
                return true;
            }

            // Suppress shader binary data warnings entirely - we handle shader replacement ourselves
            // These spam during asset bundle loading before we can replace the shaders
            if (logType == LogType.Warning &&
                message.IndexOf("Failed to find expected binary shader data", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _suppressedShaderWarnings++;
                // Log a summary occasionally instead of every warning
                if (_suppressedShaderWarnings == 1 || _suppressedShaderWarnings % 100 == 0)
                {
                    _inner.LogFormat(LogType.Log, null, "[FiresAdminTerrain] Suppressed {0} shader binary data warnings (shader replacement handles this)", _suppressedShaderWarnings);
                }
                return true;
            }

            // Throttle network limit exceeded messages
            if (message.IndexOf("Failed to send data k_EResultLimitExceeded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var now = DateTime.UtcNow;
                if (now - _lastLimitExceeded < LimitInterval)
                {
                    return true;
                }
                _lastLimitExceeded = now;
            }

            return false;
        }
    }
}
