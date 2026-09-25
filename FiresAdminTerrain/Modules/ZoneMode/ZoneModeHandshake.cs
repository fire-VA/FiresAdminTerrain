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

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class RegisterOnClient
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
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
                    var pkg = ZoneModeRegistry.BuildHandshake(out string summary);
                    rpc.Invoke(RpcZoneState, pkg);
                    if (ZoneModeRegistry.ModeActive)
                        FiresLogger.LogInfo($"[FiresZones] handshake: sent zone state ({summary}) ahead of PeerInfo.");
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
