using System;
using HarmonyLib;

namespace VerdantsAscent.Terrain.Zones
{
    // ── HANDSHAKE DELIVERY ──────────────────────────────────────────────────
    // A client builds its WorldGenerator inside ZNet.RPC_PeerInfo and starts requesting terrain
    // right after, before any routed RPC can reach it. So the zone state travels on the
    // connection's own ZRpc, sent by the server immediately BEFORE its PeerInfo on the same ordered
    // channel: by the time the client's worldgen runs, it already knows which zones are open.
    // Every world answers (zone mode or not), so a client never guesses. An unknown ZRpc method is
    // silently ignored by vanilla, so a client without this build is unaffected.
    internal static class ZoneModeHandshake
    {
        internal const string RpcZoneState = "FiresAdminTerrain_ZoneState";

        // FAT's one ZNet.OnNewConnection hook: the zone state here, and the BetterContinents join sync, which on a server
        // holds the peer's PeerInfo until the world's terrain data has reached it.
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class RegisterOnConnection
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                try { BcImport.BcJoinSync.OnNewConnection(__instance, peer); }
                catch (Exception ex) { FiresLogger.LogError($"[BcImport] join sync could not be set up for a connection: {ex.Message}"); }
                if (__instance.IsServer() || peer?.m_rpc == null) return;
                peer.m_rpc.Register<ZPackage>(RpcZoneState, (rpc, pkg) => ZoneModeRegistry.ReceiveHandshake(pkg));
            }
        }

        [HarmonyPatch(typeof(ZNet), "SendPeerInfo")]
        private static class SendBeforePeerInfo
        {
            private static void Prefix(ZNet __instance, ZRpc rpc)
            {
                if (!__instance.IsServer() || rpc == null) return;
                try
                {
                    var pkg = ZoneModeRegistry.HandshakeFor(out string summary, out bool reused);
                    rpc.Invoke(RpcZoneState, pkg);
                    if (ZoneModeRegistry.ModeActive)
                        FiresLogger.LogInfo($"[FiresZones] handshake: sent zone state ({summary}) ahead of PeerInfo"
                            + (reused ? ", the same package as the last joining peer's." : ", built for this zone state."));
                }
                catch (Exception ex)
                {
                    FiresLogger.LogError($"[FiresZones] could not send the zone state to a joining peer: {ex.Message}. "
                        + "That client will build closed zones as real land.");
                }
            }
        }
    }
}
