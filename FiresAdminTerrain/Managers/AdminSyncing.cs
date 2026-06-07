using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    public static class AdminSyncing
    {
        private static bool _isServer;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class AdminStatusSyncPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance)
            {
                _isServer = __instance.IsServer();
                if (FiresAdminTerrain.Instance == null) return;

                ZRoutedRpc.instance.Register<ZPackage>(FiresAdminTerrain.PluginName + " AdminStatusSync", RPC_AdminStatusSync);

                if (_isServer)
                    __instance.StartCoroutine(WatchAdminListChanges());
            }
        }

        private static IEnumerator WatchAdminListChanges()
        {
            var adminList = (SyncedList)AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
            var currentAdmins = new HashSet<string>(adminList.GetList());

            while (true)
            {
                yield return new WaitForSeconds(30f);

                var newAdmins = new HashSet<string>(adminList.GetList());
                if (newAdmins.SetEquals(currentAdmins)) continue;

                var peers = ZNet.instance.GetPeers();
                var adminPeers = peers.Where(p => newAdmins.Contains(p.m_rpc.GetSocket().GetHostName())).ToList();
                var nonAdminPeers = peers.Except(adminPeers).ToList();

                SendAdminStatus(nonAdminPeers, false);
                SendAdminStatus(adminPeers, true);

                currentAdmins = newAdmins;
            }
        }

        private static void SendAdminStatus(List<ZNetPeer> peers, bool isAdmin)
        {
            if (!peers.Any()) return;

            var package = new ZPackage();
            package.Write(isAdmin);

            ZNet.instance.StartCoroutine(SendZPackage(peers, package));
        }

        private static void RPC_AdminStatusSync(long sender, ZPackage package)
        {
            if (_isServer)
            {
                var peer = ZNet.instance.GetPeer(sender);
                if (peer == null) return;

                var adminList = (SyncedList)AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
                if (!adminList.Contains(peer.m_rpc.GetSocket().GetHostName())) return;

                var pkg = new ZPackage();
                pkg.Write(true);

                peer.m_rpc.Invoke(FiresAdminTerrain.PluginName + " AdminStatusSync", pkg);
                return;
            }

            // Client: Receive admin status
            try
            {
                bool isAdmin = package.ReadBool();
                ConfigSync.lockExempt = isAdmin;

                if (ConfigManager.Instance.configVerboseLogging?.Value == true)
                    Debug.Log($"{FiresAdminTerrain.PluginName}: Admin status received: {(isAdmin ? "ADMIN" : "NON-ADMIN")}");

                // Call the method on the plugin instance
                FiresAdminTerrain.Instance?.GetType()
                    .GetMethod("OnConfigLockChanged", BindingFlags.NonPublic | BindingFlags.Instance)?
                    .Invoke(FiresAdminTerrain.Instance, null);
            }
            catch { }
        }

        private static IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package)
        {
            if (!ZNet.instance) yield break;

            byte[] data = package.GetArray();
            if (data.Length > 10000)
            {
                var compressed = new ZPackage();
                compressed.Write(4);

                using var output = new MemoryStream();
                using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal))
                    deflate.Write(data, 0, data.Length);

                compressed.Write(output.ToArray());
                package = compressed;
            }

            foreach (var peer in peers.Where(p => p.IsReady()))
            {
                if (_isServer)
                    peer.m_rpc.Invoke(FiresAdminTerrain.PluginName + " AdminStatusSync", package);
                else
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_server ? 0L : peer.m_uid, FiresAdminTerrain.PluginName + " AdminStatusSync", package);
            }
        }

        public static bool IsAdmin(long senderId)
        {
            if (!ZNet.instance) return false;
            var peer = ZNet.instance.GetPeer(senderId);
            if (peer == null) return false;
            var adminList = (SyncedList)AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
            return adminList.Contains(peer.m_rpc.GetSocket().GetHostName());
        }
    }
}