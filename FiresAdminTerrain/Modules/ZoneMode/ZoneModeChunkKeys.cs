using System;
using FiresCore.Sync;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Undo and redo for chunk placement, on the keys Infinity Hammer uses (numpad 7 and 9 by default), so one key
    // takes back whichever kind of edit an admin just made. The stack lives on the server, so the key asks it and the
    // answer comes back to the admin who pressed it. Raw key reads, not a BepInEx shortcut, which would not fire while
    // a movement key is held.
    internal static class ZoneModeChunkKeys
    {
        private const string RpcStep = "FiresAdminTerrain_ChunkStep";
        private const string RpcReply = "FiresAdminTerrain_ChunkStepReply";

        private static ZRoutedRpc s_registeredOn;

        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_registeredOn, rpc)) return;
            try
            {
                rpc.Register<bool>(RpcStep, RPC_Step);
                rpc.Register<ZPackage>(RpcReply, RPC_Reply);
                s_registeredOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] chunk step RPC register failed: {ex.Message}"); }
        }

        internal static void ClientTick()
        {
            if (ZoneModeConfig.ChunkUndoKey == null || Player.m_localPlayer == null || Typing()) return;
            if (Input.GetKeyDown(ZoneModeConfig.ChunkUndoKey.Value)) Ask(redo: false);
            else if (Input.GetKeyDown(ZoneModeConfig.ChunkRedoKey.Value)) Ask(redo: true);
        }

        private static bool Typing()
            => (Chat.instance != null && Chat.instance.HasFocus())
               || Console.IsVisible() || InventoryGui.IsVisible() || Menu.IsVisible() || TextInput.IsVisible();

        // With nothing of ours to take back the key says nothing: it is Infinity Hammer's key too, and its answer
        // should be the one the admin sees.
        private static void Ask(bool redo)
        {
            var net = ZNet.instance;
            if (net == null) return;
            if (net.IsServer())
            {
                bool stepped = redo ? ZoneModeChunkBlueprint.TryRedo(out string report) : ZoneModeChunkBlueprint.TryUndo(out report);
                if (stepped) Show(report);
                return;
            }
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcStep, redo);
        }

        private static void RPC_Step(long sender, bool redo)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string reply;
            bool stepped;
            if (!AdminSyncing.IsAdmin(sender))
            {
                reply = "Taking back a chunk placement is admin only.";
                stepped = true;
            }
            else stepped = redo ? ZoneModeChunkBlueprint.TryRedo(out reply) : ZoneModeChunkBlueprint.TryUndo(out reply);
            var pkg = new ZPackage();
            pkg.Write(stepped);
            pkg.Write(reply ?? string.Empty);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcReply, pkg);
        }

        private static void RPC_Reply(long sender, ZPackage pkg)
        {
            bool stepped = pkg.ReadBool();
            string message = pkg.ReadString();
            if (stepped) Show(message);
        }

        private static void Show(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            FiresLogger.LogInfo("[FiresZones] " + message);
            if (MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, message);
        }
    }
}
