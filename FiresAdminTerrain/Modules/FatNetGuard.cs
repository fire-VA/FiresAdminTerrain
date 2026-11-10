using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent
{
    // Shared checks for FAT's network handlers. A routed RPC can be sent by any connected peer to any other peer, so a handler
    // that changes local state on a client must only act on what the SERVER sent, and every count read from the wire is
    // clamped before anything is allocated or looped over. Refusals log one warning per handler per minute.
    internal static class FatNetGuard
    {
        private const float RefusalLogSeconds = 60f;
        private static readonly Dictionary<string, double> s_loggedAt = new Dictionary<string, double>();
        private static readonly System.Diagnostics.Stopwatch s_clock = System.Diagnostics.Stopwatch.StartNew();   // 0.2.1333: FatInflate can refuse on a worker, where Unity's clock is not for use

        // True when `sender` is the server (or this peer is the server and sent it to itself).
        internal static bool FromServer(long sender, string handler)
        {
            var routed = ZRoutedRpc.instance;
            if (routed != null && (sender == routed.GetServerPeerID() || (ZNet.instance != null && ZNet.instance.IsServer() && sender == ZNet.GetUID())))
                return true;
            Refuse(handler, $"from {sender}, who is not the server");
            return false;
        }

        // A wire count clamped to [0, max]; logs when it had to clamp.
        internal static int Count(int value, int max, string handler)
        {
            if (value >= 0 && value <= max) return value;
            Refuse(handler, $"count {value} outside 0..{max}");
            return value < 0 ? 0 : max;
        }

        internal static void Refuse(string handler, string why)
        {
            double now = s_clock.Elapsed.TotalSeconds;
            lock (s_loggedAt)
            {
                if (s_loggedAt.TryGetValue(handler, out double at) && now - at < RefusalLogSeconds) return;
                s_loggedAt[handler] = now;
            }
            FiresLogger.LogWarning($"[FatNet] {handler}: refused ({why}).");
        }
    }
}
