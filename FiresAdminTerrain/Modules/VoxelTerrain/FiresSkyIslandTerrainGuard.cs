using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // NOTHING RESHAPES A SKY ISLAND.
    //
    // A sky island is voxel terrain generated from the world seed: it holds no editable
    // per-vertex data. A height-changing terrain op on one reaches the GROUND zone's
    // TerrainComp, which writes a level delta of (op Y - generated ground height) - so
    // the ground far below rises by the island's altitude. Paint is the one exception:
    // the voxel paint mask is per XZ column, so an island already wears whatever is
    // painted at its XZ, and paint-only ops keep working.
    // Plan: Docs/PLAN_WecInfinityHammerTerrainBridge.md
    internal static class FiresSkyIslandTerrainGuard
    {
        private const float MessageCooldownSeconds = 1.5f;
        private const float NearbyOpMessageDistance = 12f;
        private const string RefusedMessage = "Sky islands can't be reshaped - it would raise the ground far below.";

        private static float s_lastMessageTime = -100f;
        private static bool s_reported;

        // From the Priority.Low prefix of FAT's one TerrainOp.Awake hook (FatVanillaHooks),
        // after the closed-zone refusal and the FAT tile route have both passed on the op.
        internal static bool AllowTerrainOp(TerrainOp op)
        {
            if (op == null || TerrainOp.m_forceDisableTerrainOps) return true;
            if (Zones.ZoneModeRefusals.WasCancelled(op) || Persistence.TileTerrainOpBlocker.WasRoutedToTile(op)) return false;

            var settings = op.m_settings;
            if (settings == null || !(settings.m_level || settings.m_raise || settings.m_smooth)) return true;

            Vector3 position = op.transform.position;
            if (!FiresSkyIslands.IsOnIsland(position)) return true;

            Report(position);
            TellLocalPlayer(position);
            UnityEngine.Object.Destroy(op.gameObject);
            return false;
        }

        // Loud once, then quiet: a held brush would otherwise log every stroke.
        private static void Report(Vector3 position)
        {
            string line = $"[FiresSkyIslands] refused a terrain op at ({position.x:0},{position.y:0},{position.z:0}) - it sits "
                + "on a sky island, and levelling, raising or smoothing there would have moved the ground below instead. "
                + "Paint and cultivate still work on islands.";
            if (s_reported)
            {
                FiresLogger.LogVerbose(line);
                return;
            }
            s_reported = true;
            FiresLogger.LogWarning(line);
        }

        private static void TellLocalPlayer(Vector3 position)
        {
            var player = Player.m_localPlayer;
            if (player == null || Vector3.Distance(player.transform.position, position) >= NearbyOpMessageDistance) return;
            if (Time.time - s_lastMessageTime < MessageCooldownSeconds) return;
            s_lastMessageTime = Time.time;
            player.Message(MessageHud.MessageType.Center, RefusedMessage);
        }
    }
}
