using System;
using FiresCore.Sync;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Undo and redo of chunk / area / skyland placements and biome paint, as steps in the ONE undo history (Core's UndoHistory,
    // through FatUndo; FAT 0.2.716). Those edits' history lives on the server, so each step asks the server to step its history
    // and the answer comes back to the admin who asked. The keys are Core's (numpad 7 / 9) or Infinity Hammer's, never FAT's own:
    // the old "Chunk undo key" / "Chunk redo key" are gone, so a press never fires twice.
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

        // Per frame on a client: a finished V-tool stroke becomes its undo step.
        internal static void ClientTick() => FatUndo.ClientTick();

        /// <summary>A placement or paint the server just made for this admin, as one step of the shared history.</summary>
        internal static void RecordServerStep(string what)
        {
            FatUndo.Record(what, () => { Ask(redo: false); return $"Undoing {what}…"; }, () => { Ask(redo: true); return $"Redoing {what}…"; });
        }

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
            Show(message);
        }

        private static void Show(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            FiresLogger.LogInfo("[FiresZones] " + message);
            if (MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, message);
        }
    }
}
