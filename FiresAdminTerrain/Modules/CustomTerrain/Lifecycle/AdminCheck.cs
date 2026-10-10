using FiresCore.Sync;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Did an admin send this FAT edit RPC? Every answer fails closed.
    //   this machine's own loopback  - the local admin check;
    //   on the server                - Core's AdminSyncing against the server's admin list;
    //   on a client, from the server - trusted (a dedicated server or the listen host);
    //   on a client, from a client   - the admin list and player list the server syncs to every client.
    public static class AdminCheck
    {
        public static bool IsAdmin(long sender)
        {
            var net = ZNet.instance;
            if (net == null) return false;
            if (ZDOMan.instance != null && sender == ZDOMan.GetSessionID()) return AdminSyncing.IsLocalAdmin();
            if (net.IsServer()) return sender == 0L || AdminSyncing.IsAdmin(sender);
            var server = net.GetServerPeer();
            if (server != null && sender == server.m_uid) return true;
            foreach (var info in net.GetPlayerList())
                if (info.m_characterID.UserID == sender) return net.PlayerIsAdmin(info.m_userInfo.m_id);
            return false;
        }
    }
}
