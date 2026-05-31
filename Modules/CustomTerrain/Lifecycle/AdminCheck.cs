using System;
using System.Reflection;
using HarmonyLib;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Shared reflective wrapper around ServerSync.AdminSyncing.IsAdmin(long).
    // Lifted out of RegionScrubberRPC so every RPC handler in the mod uses the
    // exact same check + caching + fallback semantics — no per-class duplication.
    //
    // Behavior:
    //   * If ServerSync (the BepInDependency soft-dep) is present, calls
    //     `ServerSync.AdminSyncing.IsAdmin(senderPeerId)`. ServerSync replicates
    //     the admin list to peers so this returns the same answer on every
    //     machine for a given sender — the basis for the request-pattern
    //     trust model we use across RegionScrubberRPC + TileBrushRPC +
    //     TerrainPaintRPC.
    //   * If ServerSync is missing, fall back to TRUST (return true). Mods
    //     running without ServerSync are typically SP/host-only setups
    //     where the player IS the only peer and is implicitly admin.
    //   * sender == 0 is the local-server self-call (loopback) — always
    //     trusted, never goes through reflection (avoids a needless call).
    //
    // Re-resolution happens once per session; the resolved MethodInfo is
    // cached for the lifetime of the AppDomain.
    public static class AdminCheck
    {
        private static MethodInfo _isAdminMethod;
        private static bool _resolved;

        // Returns true when the sender peer is admin (or the call is a
        // local-server loopback, sender==0). Returns false to REJECT the
        // op — RPC handlers should log + skip when this is false.
        public static bool IsAdmin(long sender)
        {
            // sender 0 = the local server invoked the RPC itself; trust.
            if (sender == 0L) return true;

            EnsureResolved();
            if (_isAdminMethod == null)
            {
                // ServerSync missing → trust. SP-only / host-only mod loadouts.
                return true;
            }

            try
            {
                return (bool)_isAdminMethod.Invoke(null, new object[] { sender });
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[AdminCheck] AdminSyncing.IsAdmin threw for sender {sender}: {ex.Message} — defaulting to DENY.");
                return false;
            }
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("ServerSync.AdminSyncing");
                    if (t == null) continue;
                    _isAdminMethod = t.GetMethod("IsAdmin",
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(long) }, null);
                    if (_isAdminMethod != null)
                    {
                        FiresLogger.LogInfo("[AdminCheck] ServerSync.AdminSyncing.IsAdmin bound — non-admin RPC handlers will reject.");
                        return;
                    }
                }
                FiresLogger.LogInfo("[AdminCheck] ServerSync not detected — all peers will be treated as admin (SP/host-only mod loadout).");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[AdminCheck] Reflection lookup threw: {ex.Message} — defaulting to TRUST.");
            }
        }

        // For test / diagnostic purposes — force re-resolution on next call.
        // Not used in production paths.
        public static void Reset()
        {
            _resolved = false;
            _isAdminMethod = null;
        }
    }
}
