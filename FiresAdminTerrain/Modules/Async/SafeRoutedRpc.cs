using System;
using System.Collections;
using UnityEngine;

namespace VerdantsAscent.Async
{
    /// <summary>
    /// Centralised guard-rail for every ZRoutedRpc.InvokeRoutedRPC call in the mod.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// Valheim's Steam transport (<c>ZSteamSocket</c> ? <c>SteamNetworkingSockets</c>) has
    /// a hard per-message ceiling of 512 KB (<c>k_cbMaxSteamNetworkingSocketsMessageSizeSend</c>).
    /// When we call <see cref="ZRoutedRpc.InvokeRoutedRPC(long, string, object[])"/> multiple
    /// times in the same frame, the routed packages get queued and then SHIPPED TOGETHER
    /// by <c>ZSteamSocket.SendQueuedPackages</c> as a single <c>SendMessageToConnection</c>
    /// call. If the concatenated payload exceeds 512 KB Steam fires the infamous:
    ///
    ///     steamnetworkingsockets_connections.cpp (2095) : Assertion Failed:
    ///     Message size XXXXXXX is too big. Max is 524288
    ///
    /// The assertion spams, the server drops chunks, and downstream features that rely
    /// on the truncated bytes (VAngarde log disk cache, sprite sync, UI layout push, ?)
    /// silently corrupt.
    ///
    /// RULES ENFORCED
    /// --------------
    ///  * <see cref="MaxSafeMessageBytes"/> ? we refuse to dispatch any single ZPackage
    ///    whose serialised payload exceeds this budget. Senders MUST chunk below it.
    ///  * <see cref="PacedChunkedSend"/> ? coroutine-driven chunk pump that yields a
    ///    frame between chunks so <c>ZSteamSocket</c> actually flushes each chunk as
    ///    its own Steam message instead of aggregating them.
    ///
    /// EVERY call site that invokes a routed RPC should go through <see cref="InvokeSafe"/>
    /// (or the paced chunker). The size-check is defence in depth ? it prevents a
    /// regression somewhere else in the codebase from ever reaching Steam's assertion.
    /// </summary>
    public static class SafeRoutedRpc
    {
        /// <summary>
        /// Hard ceiling for a single routed RPC ZPackage payload. Steam's per-message
        /// cap is 512 KB (524288 B); we leave ~44 KB of headroom for Valheim's routed
        /// RPC envelope (header, sender/target IDs, method name) plus multiple queued
        /// packages that <c>ZSteamSocket</c> may concatenate inside a single frame.
        /// </summary>
        public const int MaxSafeMessageBytes = 480 * 1024;

        /// <summary>
        /// Recommended chunk size for <see cref="PacedChunkedSend"/>. Matches the
        /// 400 KB convention used by RuntimeSpriteSync and VAngarde chunked log send.
        /// </summary>
        public const int DefaultChunkBytes = 400 * 1024;

        /// <summary>
        /// Size-gated wrapper around <see cref="ZRoutedRpc.InvokeRoutedRPC(long, string, object[])"/>.
        /// Returns true iff the package was actually handed off to Steam; returns false
        /// (and logs a warning) when the package is null, over-size, or the RPC layer
        /// is unavailable. Never throws.
        /// </summary>
        public static bool InvokeSafe(long target, string method, ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null)
            {
                Debug.LogWarning($"[SafeRoutedRpc] Dropping '{method}' ? ZRoutedRpc.instance is null (target={target}).");
                return false;
            }
            if (pkg == null)
            {
                Debug.LogWarning($"[SafeRoutedRpc] Dropping '{method}' ? ZPackage is null (target={target}).");
                return false;
            }

            int size = 0;
            try
            {
                byte[] raw = pkg.GetArray();
                size = raw?.Length ?? 0;
            }
            catch
            {
                // If we can't measure it, refuse to send ? better a dropped RPC than a
                // Steam assertion cascade that desyncs every connected peer.
                Debug.LogWarning($"[SafeRoutedRpc] Dropping '{method}' ? ZPackage.GetArray() threw (target={target}).");
                return false;
            }

            if (size > MaxSafeMessageBytes)
            {
                Debug.LogError(
                    $"[SafeRoutedRpc] REFUSING to send oversize RPC '{method}' " +
                    $"({size:N0} B > {MaxSafeMessageBytes:N0} B cap, target={target}). " +
                    "Caller must chunk this payload via SafeRoutedRpc.PacedChunkedSend.");
                return false;
            }

            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(target, method, pkg);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SafeRoutedRpc] InvokeRoutedRPC('{method}') threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Overload for the target-less <c>ZRoutedRpc.InvokeRoutedRPC(methodName, params)</c>
        /// form (routes the package via Valheim's default target resolution ? typically
        /// to the server when called from a client). Same size-gating semantics as
        /// <see cref="InvokeSafe(long, string, ZPackage)"/>.
        /// </summary>
        public static bool InvokeSafe(string method, ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null)
            {
                Debug.LogWarning($"[SafeRoutedRpc] Dropping '{method}' ? ZRoutedRpc.instance is null.");
                return false;
            }
            if (pkg == null)
            {
                Debug.LogWarning($"[SafeRoutedRpc] Dropping '{method}' ? ZPackage is null.");
                return false;
            }

            int size = 0;
            try { size = pkg.GetArray()?.Length ?? 0; }
            catch
            {
                Debug.LogWarning($"[SafeRoutedRpc] Dropping '{method}' ? ZPackage.GetArray() threw.");
                return false;
            }

            if (size > MaxSafeMessageBytes)
            {
                Debug.LogError(
                    $"[SafeRoutedRpc] REFUSING to send oversize RPC '{method}' " +
                    $"({size:N0} B > {MaxSafeMessageBytes:N0} B cap). " +
                    "Caller must chunk this payload via SafeRoutedRpc.PacedChunkedSend.");
                return false;
            }

            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(method, pkg);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SafeRoutedRpc] InvokeRoutedRPC('{method}') threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Coroutine that splits <paramref name="payload"/> into <paramref name="chunkBytes"/>-sized
        /// slices and ships each as its own routed RPC, <b>yielding one frame between chunks</b>
        /// so Steam's send queue flushes between them. This is the only safe way to ship
        /// multi-MB data via routed RPC without tripping the 512 KB per-message cap.
        ///
        /// The caller supplies a <paramref name="fillPackage"/> delegate that gets a fresh
        /// <see cref="ZPackage"/> plus the current (chunkIndex, totalChunks) and the raw
        /// chunk bytes. The delegate is responsible for writing any per-call metadata
        /// (nonce, file name, total size, etc.) followed by the chunk payload ? keeping
        /// the wire format private to each caller.
        ///
        /// The produced ZPackage is dispatched via <see cref="InvokeSafe"/>, so any chunk
        /// that somehow balloons past <see cref="MaxSafeMessageBytes"/> (e.g. the
        /// first chunk carrying a large header) is refused instead of crashing Steam.
        /// </summary>
        /// <param name="target">Routed RPC target peer id (0 = server, -1 = everybody).</param>
        /// <param name="method">RPC method name registered on the receiver.</param>
        /// <param name="payload">Raw payload bytes to chunk. Never null (use empty array).</param>
        /// <param name="chunkBytes">Per-chunk payload ceiling. Defaults to <see cref="DefaultChunkBytes"/>.</param>
        /// <param name="fillPackage">
        /// (pkg, chunkIndex, totalChunks, chunkSlice) ? populate the package. Must not
        /// invoke the RPC; the coroutine does that.
        /// </param>
        /// <param name="onComplete">Optional callback fired after the last chunk is dispatched
        /// (or after an early abort). Receives the number of chunks successfully sent.</param>
        public static IEnumerator PacedChunkedSend(
            long target,
            string method,
            byte[] payload,
            int chunkBytes,
            Action<ZPackage, int, int, byte[]> fillPackage,
            Action<int> onComplete = null)
        {
            if (fillPackage == null)
            {
                Debug.LogWarning($"[SafeRoutedRpc] PacedChunkedSend('{method}') ? fillPackage delegate is null, aborting.");
                onComplete?.Invoke(0);
                yield break;
            }

            if (payload == null) payload = Array.Empty<byte>();
            if (chunkBytes <= 0 || chunkBytes > MaxSafeMessageBytes)
                chunkBytes = DefaultChunkBytes;

            // Always emit at least one chunk so receiver-side handlers see a completion
            // even on empty payloads (matches the existing VAngarde / sprite sync contract).
            int totalChunks = Math.Max(1, (int)Math.Ceiling((double)payload.Length / chunkBytes));
            int sent = 0;

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                if (ZRoutedRpc.instance == null)
                {
                    Debug.LogWarning($"[SafeRoutedRpc] PacedChunkedSend('{method}') aborted at chunk " +
                                     $"{chunkIndex + 1}/{totalChunks} ? ZRoutedRpc gone.");
                    break;
                }

                int offset = chunkIndex * chunkBytes;
                int length = Math.Min(chunkBytes, payload.Length - offset);
                if (length < 0) length = 0;

                byte[] slice;
                if (length == 0)
                {
                    slice = Array.Empty<byte>();
                }
                else
                {
                    slice = new byte[length];
                    Array.Copy(payload, offset, slice, 0, length);
                }

                ZPackage pkg;
                try
                {
                    pkg = new ZPackage();
                    fillPackage(pkg, chunkIndex, totalChunks, slice);
                }
                catch (Exception fillEx)
                {
                    Debug.LogWarning($"[SafeRoutedRpc] PacedChunkedSend('{method}') fillPackage threw on chunk " +
                                     $"{chunkIndex + 1}/{totalChunks}: {fillEx.Message}");
                    break;
                }

                if (InvokeSafe(target, method, pkg))
                {
                    sent++;
                }
                else
                {
                    // InvokeSafe already logged the reason (null, oversized, throw).
                    // Abort rather than pushing partial/corrupt state to the receiver.
                    break;
                }

                // Critical pacing step. A single `yield return null` is NOT enough:
                // when our coroutine runs AFTER ZNet's socket Update pass within a
                // given Unity frame, chunk N lands in ZSteamSocket's send queue but
                // stays there until the NEXT frame's socket update fires. If we
                // resumed immediately on the next frame and queued chunk N+1, both
                // chunks would sit in the queue together and the next
                // SendQueuedPackages call would concatenate them into one
                // SteamNetworkingSockets.SendMessageToConnection call ? blowing past
                // the 512 KB hard cap and triggering the infamous assertion cascade
                // ("Message size 683789 is too big. Max is 524288"). Yielding TWICE
                // guarantees at least one full Update tick ? and therefore one
                // SendQueuedPackages flush ? between successive chunk enqueues.
                yield return null;
                yield return null;
            }

            onComplete?.Invoke(sent);
        }

        // ----------------------------------------------------------------------
        // Targeted broadcast helpers  (Balrond's "userA -> server -> userB"
        // hygiene lecture).
        //
        // Broadcasting routed RPCs via ZRoutedRpc.Everybody fans the payload to
        // ALL peers INCLUDING the origin peer that just applied the op locally.
        // That both wastes bandwidth and forces every handler to run
        // deduplication logic (opId / stableHash caches that can desync). The
        // helpers below let a server-side broadcast site exclude the origin
        // client cleanly, and let handlers short-circuit when they're hearing
        // their own Everybody-broadcast come back.
        // ----------------------------------------------------------------------

        /// <summary>
        /// Returns true iff a routed-RPC handler's <c>sender</c> argument matches
        /// our own ZRoutedRpc peer id ? i.e. the handler is currently processing
        /// our own <c>Everybody</c> broadcast looping back around. Safe to call
        /// from anywhere; returns false when the RPC layer isn't initialised yet.
        /// </summary>
        public static bool IsLocalSender(long senderId)
        {
            return ZRoutedRpc.instance != null && senderId == ZRoutedRpc.instance.m_id;
        }

        /// <summary>
        /// SERVER-SIDE broadcast helper: dispatches <paramref name="pkg"/> to every
        /// connected client peer EXCEPT <paramref name="excludePeerId"/> (typically
        /// the origin client's m_uid, pulled from the incoming request). This is the
        /// proper "server relays to everyone else" shape and removes the need for
        /// per-op dedup caches on the sender side, because the sender simply never
        /// receives the confirmation RPC back over the wire.
        ///
        /// The ZPackage is snapshotted to raw bytes once, then each per-peer
        /// dispatch re-hydrates a fresh ZPackage from those bytes (ZPackage
        /// carries a read cursor, so re-using one instance across dispatches
        /// is unsafe). Every per-peer dispatch also goes through <see cref="InvokeSafe"/>
        /// so an oversize payload is still refused.
        ///
        /// No-ops (and returns 0) when called from a non-server peer ? enforce
        /// server-only via the caller if stricter semantics are required.
        /// </summary>
        /// <param name="excludePeerId">Peer id to skip. Pass <c>0</c> to send to all
        /// connected clients without exclusion.</param>
        /// <param name="method">Registered routed RPC method name.</param>
        /// <param name="pkg">Prebuilt ZPackage. Not mutated.</param>
        /// <returns>Number of peers the package was successfully dispatched to.</returns>
        public static int InvokeSafeToAllExcept(long excludePeerId, string method, ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null)
            {
                Debug.LogWarning($"[SafeRoutedRpc] InvokeSafeToAllExcept('{method}') ? ZRoutedRpc.instance is null.");
                return 0;
            }
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                Debug.LogWarning($"[SafeRoutedRpc] InvokeSafeToAllExcept('{method}') ? caller is not the server, refusing to fan out.");
                return 0;
            }
            if (pkg == null)
            {
                Debug.LogWarning($"[SafeRoutedRpc] InvokeSafeToAllExcept('{method}') ? ZPackage is null.");
                return 0;
            }

            byte[] snapshot;
            try { snapshot = pkg.GetArray(); }
            catch
            {
                Debug.LogWarning($"[SafeRoutedRpc] InvokeSafeToAllExcept('{method}') ? ZPackage.GetArray() threw.");
                return 0;
            }
            if (snapshot == null) snapshot = Array.Empty<byte>();

            if (snapshot.Length > MaxSafeMessageBytes)
            {
                Debug.LogError(
                    $"[SafeRoutedRpc] REFUSING fan-out of oversize RPC '{method}' " +
                    $"({snapshot.Length:N0} B > {MaxSafeMessageBytes:N0} B cap). " +
                    "Caller must chunk via SafeRoutedRpc.PacedChunkedSend.");
                return 0;
            }

            var peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return 0;

            int sent = 0;
            for (int i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                if (peer == null) continue;
                if (peer.m_uid == excludePeerId) continue;

                // Fresh ZPackage per dispatch ? ZPackage is stateful (internal
                // read/write cursor), so reusing one instance across multiple
                // InvokeRoutedRPC calls can deliver a half-read payload.
                ZPackage perPeer;
                try { perPeer = new ZPackage(snapshot); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SafeRoutedRpc] Failed to rebuild ZPackage for peer {peer.m_uid}: {ex.Message}");
                    continue;
                }

                try
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, method, perPeer);
                    sent++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SafeRoutedRpc] InvokeRoutedRPC('{method}') to peer {peer.m_uid} threw: {ex.Message}");
                }
            }
            return sent;
        }
    }
}
