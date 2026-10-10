using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Cave pop-out guard. The voxel skin is a thin open shell: jam the
    // player capsule's center past a ceiling face (crouch-jump into a low
    // roof) and Unity's depenetration ejects it to the NEAREST face — which
    // for a thin roof is UP, teleporting the player onto the surface.
    // Two layers:
    //   1. maxDepenetrationVelocity clamp — overlaps resolve as a gentle
    //      push (≤2 m/s), never an instant ejection.
    //   2. Snap-back — a legit move never gains more than ~0.3m in one
    //      physics tick; going from "well under the heightmap surface" to
    //      "at/above it" with Δy > 2.5 in one tick is a pop: restore the
    //      last in-cave position and zero velocity. Teleports are exempt.
    internal static class VoxelCavePopGuard
    {
        private static Rigidbody s_body;
        private static Player s_bodyOwner;
        private static Vector3 s_lastPos;
        private static bool s_lastUnderSurface;
        private static bool s_hasLast;
        private static float s_nextLog;

        public static void FixedTick()
        {
            var player = Player.m_localPlayer;
            if (player == null || !VoxelOnlyMode.Active) { s_hasLast = false; return; }
            if (player.IsTeleporting()) { s_hasLast = false; return; }
            if (FatNoClip.Skips(player, "the cave pop-out guard")) { s_hasLast = false; return; }

            if (s_bodyOwner != player)
            {
                s_body = player.GetComponent<Rigidbody>();
                s_bodyOwner = player;
                if (s_body != null) s_body.maxDepenetrationVelocity = 2f;
            }

            Vector3 pos = player.transform.position;
            if (!Heightmap.GetHeight(pos, out float surf)) { s_hasLast = false; return; }
            bool underSurface = pos.y < surf - 2f;

            if (s_hasLast && s_lastUnderSurface && !underSurface
                && pos.y - s_lastPos.y > 2.5f && pos.y > surf - 1f)
            {
                player.transform.position = s_lastPos;
                if (s_body != null)
                {
                    s_body.position = s_lastPos;
                    s_body.linearVelocity = Vector3.zero;
                }
                if (Time.time >= s_nextLog)
                {
                    s_nextLog = Time.time + 5f;
                    FiresLogger.LogInfo($"[FiresVoxel] cave pop-out caught at ({pos.x:0},{pos.z:0}): " +
                              $"Δy +{pos.y - s_lastPos.y:0.0}m in one tick (surface {surf:0.0}) — restored.");
                }
                return;   // keep last state — still in the cave
            }

            s_lastPos = pos;
            s_lastUnderSurface = underSurface;
            s_hasLast = true;
        }
    }
}
