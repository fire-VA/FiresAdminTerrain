using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Modules.Utilities
{
    /// <summary>
    /// Centralised "is this player object safe to write to right now?" gate
    /// + a small main-thread queue that defers work until the gate opens.
    ///
    /// CONTEXT
    /// -------
    /// We write to <see cref="Player.m_customData"/> in many places - vault,
    /// per-player companion roster, periodic save ticks, async RPC handlers
    /// triggered by the spawn pipeline. Several of those writes were observed
    /// to fire during lifecycle windows where the engine is still
    /// initialising the player object - fresh-world first spawn, bed
    /// respawn, dungeon teleport. Modifying customData during that window
    /// has been observed to hard-crash the game (no managed exception,
    /// Player.log truncates mid-spawn).
    ///
    /// DESIGN
    /// ------
    /// We do NOT skip writes when the gate is closed - that would silently
    /// drop authoritative server data, roster updates, vault sync, etc.
    /// Instead, callers wrap their write in <see cref="RunWhenReady"/>:
    /// the work runs immediately if the gate is open, and is otherwise
    /// queued on a hidden driver MonoBehaviour that polls each frame and
    /// fires the action the moment the player becomes ready (or after a
    /// per-call timeout, in which case the action is dropped with a
    /// warning - bounded so a never-ready player can't grow the queue
    /// without limit).
    ///
    /// All queue operations run on the Unity main thread; the queue is a
    /// plain <see cref="List{T}"/> with no locking.
    /// </summary>
    public static class PlayerSpawnGate
    {
        private const string LogPrefix = "[PlayerSpawnGate]";

        /// <summary>
        /// When true, ready/not-ready transitions and timeouts are logged.
        /// Off by default; the production callers do their own logging.
        /// </summary>
        public static bool VerboseLogging = false;

        /// <summary>
        /// Returns true when <paramref name="p"/> is fully spawned and it
        /// is safe to mutate <see cref="Player.m_customData"/> on it.
        ///
        /// All five conditions must hold:
        /// 1. Player reference is non-null and the underlying GameObject
        ///    has not been destroyed.
        /// 2. <c>m_customData</c> dictionary is initialised.
        /// 3. The player's <see cref="ZNetView"/> is valid (live ZDO,
        ///    network identity established).
        /// 4. <see cref="ZNet"/> is up and either we're the server or the
        ///    client has reached <c>Connected</c> status.
        /// 5. The player is not currently teleporting (loading screen
        ///    transitions destroy/recreate the Player MonoBehaviour and
        ///    writes mid-transition can race with engine teardown).
        /// </summary>
        public static bool IsReadyForCustomDataWrite(Player p)
        {
            if (p == null) return false;

            // Detect destroyed-but-not-null reference (common around respawn
            // and bed-teleport - the C# reference survives a frame or two
            // after Unity has torn down the GameObject).
            try { if (!p) return false; }
            catch { return false; }

            if (p.m_customData == null) return false;

            try
            {
                if (p.m_nview == null) return false;
                if (!p.m_nview.IsValid()) return false;
            }
            catch { return false; }

            if (ZNet.instance == null) return false;
            if (!ZNet.instance.IsServer() &&
                ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected)
            {
                return false;
            }

            try { if (p.IsTeleporting()) return false; }
            catch { return false; }

            return true;
        }

        /// <summary>
        /// Runs <paramref name="work"/> against <paramref name="player"/>
        /// the moment <see cref="IsReadyForCustomDataWrite"/> returns true,
        /// or after at most <paramref name="timeoutSeconds"/> of waiting
        /// (in which case the action is dropped with a warning - never
        /// executed against a not-ready player).
        ///
        /// CRITICAL: The action is ALWAYS deferred to at least the next
        /// frame, even if the gate predicate is already satisfied. This
        /// is intentional: every observed crash in this code path was
        /// triggered by work running synchronously from inside
        /// <c>Player.OnSpawned</c>. The predicate alone reports the
        /// player as "ready" at that point (m_nview valid, m_customData
        /// non-null, ZNet up, not teleporting) but the engine has not
        /// finished its post-spawn finalisation - self-routed RPCs that
        /// write <c>m_customData</c> from inside the spawn call stack
        /// hard-crash the game with no managed exception. Running on the
        /// next frame's Update gets us out of that call stack.
        ///
        /// Safe to call from Harmony postfixes, RPC handlers, coroutines,
        /// and Update methods. Not thread-safe - call from the Unity main
        /// thread only (which all Harmony patches and ZRoutedRpc handlers
        /// already do).
        /// </summary>
        public static void RunWhenReady(Player player, float timeoutSeconds, Action<Player> work)
        {
            if (work == null) return;
            if (player == null)
            {
                if (VerboseLogging)
                    FiresLogger.LogInfo($"{LogPrefix} RunWhenReady called with null player - dropping.");
                return;
            }

            Driver.GetOrCreate().Enqueue(player, timeoutSeconds, work);
        }

        /// <summary>
        /// Convenience overload that targets <see cref="Player.m_localPlayer"/>
        /// and re-resolves it at the moment the gate opens. Use this in RPC
        /// handlers that need to apply data to "whichever player is local
        /// right now" - re-resolving handles the rare case where the
        /// localPlayer reference changes (character switch) while we were
        /// waiting.
        ///
        /// Same always-defer semantics as <see cref="RunWhenReady"/>.
        /// </summary>
        public static void RunWhenLocalReady(float timeoutSeconds, Action<Player> work)
        {
            if (work == null) return;
            Driver.GetOrCreate().EnqueueLocal(timeoutSeconds, work);
        }

        /// <summary>
        /// Convenience wrapper around <see cref="RunWhenReady"/> for the common
        /// single-key case. Defers writing <c>player.m_customData[key] = value</c>
        /// until the spawn gate opens. Use <see cref="RunWhenReady"/> directly
        /// for paired writes (e.g. vault JSON + dirty flag), read-modify-write,
        /// or when the write is coupled with another stateful side effect that
        /// must run inside the same deferred frame.
        /// </summary>
        public static void SafeSetCustomData(Player player, string key, string value, float timeoutSeconds = 30f)
        {
            if (string.IsNullOrEmpty(key)) return;
            RunWhenReady(player, timeoutSeconds, p => p.m_customData[key] = value);
        }

        /// <summary>
        /// Local-player variant of <see cref="SafeSetCustomData"/>. Re-resolves
        /// <see cref="Player.m_localPlayer"/> at the moment the gate opens.
        /// </summary>
        public static void SafeSetLocalCustomData(string key, string value, float timeoutSeconds = 30f)
        {
            if (string.IsNullOrEmpty(key)) return;
            RunWhenLocalReady(timeoutSeconds, p => p.m_customData[key] = value);
        }

        private static void InvokeWork(Player p, Action<Player> work)
        {
            // DIAG (login-freeze): bracket each queued callback's execution so we
            // can see, in the next repro log, which deferred work fires immediately
            // post-spawn and whether each one returns. A queued callback that
            // ENTERs and never EXITs is the freeze.
            string targetDesc = "<null player>";
            try { if (p != null) targetDesc = p.GetPlayerName() ?? "<unnamed>"; } catch { targetDesc = "<unnameable>"; }
            string workDesc = work?.Method?.DeclaringType?.FullName + "." + (work?.Method?.Name ?? "<unknown>");
            FiresLogger.LogInfo($"[LoginFreeze][DIAG] PlayerSpawnGate.InvokeWork ENTER target={targetDesc} work={workDesc}");
            try { work(p); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} deferred action threw: {ex.Message}");
            }
            FiresLogger.LogInfo($"[LoginFreeze][DIAG] PlayerSpawnGate.InvokeWork EXIT target={targetDesc} work={workDesc}");
        }

        // =========================== DRIVER ===========================

        /// <summary>
        /// Hidden MonoBehaviour that polls queued entries every frame.
        /// Lazily created on first use.
        /// </summary>
        private class Driver : MonoBehaviour
        {
            private static Driver _instance;

            public static Driver GetOrCreate()
            {
                if (_instance != null) return _instance;
                var go = new GameObject("FiresRPGmaker_PlayerSpawnGateDriver");
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _instance = go.AddComponent<Driver>();
                return _instance;
            }

            private struct Entry
            {
                public Player Target;
                public bool UseLocal;
                public float DeadlineUnscaled;
                public Action<Player> Work;
            }

            private readonly List<Entry> _queue = new List<Entry>(8);

            // Hard cap so a stuck-not-ready player can't grow the queue
            // unbounded if some caller spams enqueue-per-frame.
            private const int MAX_QUEUED = 256;

            public void Enqueue(Player target, float timeoutSeconds, Action<Player> work)
            {
                if (_queue.Count >= MAX_QUEUED)
                {
                    FiresLogger.LogWarning($"{LogPrefix} queue at MAX_QUEUED ({MAX_QUEUED}) - dropping enqueue.");
                    return;
                }

                _queue.Add(new Entry
                {
                    Target = target,
                    UseLocal = false,
                    DeadlineUnscaled = Time.unscaledTime + Mathf.Max(0.1f, timeoutSeconds),
                    Work = work,
                });
            }

            public void EnqueueLocal(float timeoutSeconds, Action<Player> work)
            {
                if (_queue.Count >= MAX_QUEUED)
                {
                    FiresLogger.LogWarning($"{LogPrefix} queue at MAX_QUEUED ({MAX_QUEUED}) - dropping enqueue.");
                    return;
                }

                _queue.Add(new Entry
                {
                    Target = null,
                    UseLocal = true,
                    DeadlineUnscaled = Time.unscaledTime + Mathf.Max(0.1f, timeoutSeconds),
                    Work = work,
                });
            }

            private void Update()
            {
                if (_queue.Count == 0) return;

                float now = Time.unscaledTime;

                // Walk back-to-front so RemoveAt(i) doesn't break iteration.
                for (int i = _queue.Count - 1; i >= 0; i--)
                {
                    var e = _queue[i];

                    Player target = e.UseLocal ? Player.m_localPlayer : e.Target;

                    if (IsReadyForCustomDataWrite(target))
                    {
                        _queue.RemoveAt(i);
                        InvokeWork(target, e.Work);
                        continue;
                    }

                    if (now >= e.DeadlineUnscaled)
                    {
                        _queue.RemoveAt(i);
                        if (VerboseLogging)
                        {
                            FiresLogger.LogWarning($"{LogPrefix} deferred action timed out before player became ready " +
                                $"(useLocal={e.UseLocal}, target={(target == null ? "null" : SafeName(target))}) - dropping.");
                        }
                        continue;
                    }
                }
            }

            private static string SafeName(Player p)
            {
                try { return p.GetPlayerName() ?? "<unnamed>"; }
                catch { return "<unnameable>"; }
            }
        }
    }
}
