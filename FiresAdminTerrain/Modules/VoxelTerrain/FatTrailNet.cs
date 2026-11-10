using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1182 ([fgn] round 45: TerrainComp.RPC_ApplyOperation 5.1 ms/s = 4.5 ops/s x 1.11 ms each, from 2 peers; senders
    // [SnowTrail] 930, [Cultivated] 295, [TrailPredict] 279): every trail stamp is a networked TerrainOp, and a received op costs
    // ~1 ms on each peer. Two cuts here: (1) a stamp whose cell AND the segment midpoint already read G >= AlreadyCutG on this
    // peer's paint mask (an existing trench) is not sent at all - the trench is already there; (2) the receive handler is timed
    // so the next session names its cost. Creature stamps are also spaced 3 m / capped 6 per second (FatCreatureTrail).
    internal static class FatTrailNet
    {
        internal const float AlreadyCutG = 0.95f;
        internal static int Sent, Stamps, Skipped;
        private static int s_rpcN;
        private static double s_rpcMs, s_rpcMax;
        private static float s_logAt;

        // True when both p and the midpoint to the previous stamp are already cut on this peer (only peers with paint masks
        // can tell; elsewhere false = send as before).
        internal static bool AlreadyCut(Vector3 prev, Vector3 p)
        {
            if (!VoxelWorld.TryTrailG(p, out float g) || g < AlreadyCutG) return false;
            if (prev.x < 1e30f && (prev - p).sqrMagnitude < 36f)
            {
                if (!VoxelWorld.TryTrailG((prev + p) * 0.5f, out float gm) || gm < AlreadyCutG) return false;
            }
            return true;
        }

        internal static void NoteStamp(bool sent) { Stamps++; if (sent) Sent++; else Skipped++; Summary(); }

        private static void Summary()
        {
            if (Time.unscaledTime < s_logAt) return;
            if (s_logAt > 0f && FiresLogger.DebugEnabled)
            {
                float span = 10f;
                FiresLogger.LogDebug($"[TrailNet] ops sent {Sent / span:0.0}/s (from {Stamps} stamp(s)), skipped unchanged {Skipped} (cell + segment already G >= {AlreadyCutG:0.00}); "
                    + $"received op handler {s_rpcN} call(s), ms avg {(s_rpcN > 0 ? s_rpcMs / s_rpcN : 0):0.00} / max {s_rpcMax:0.00} (0.2.1182)");
            }
            s_logAt = Time.unscaledTime + 10f;
            Sent = Stamps = Skipped = 0; s_rpcN = 0; s_rpcMs = 0; s_rpcMax = 0;
        }

        [HarmonyPatch(typeof(TerrainComp), "RPC_ApplyOperation")]
        private static class Patch_RpcTiming
        {
            private static void Prefix(out long __state) => __state = System.Diagnostics.Stopwatch.GetTimestamp();
            private static void Postfix(long __state)
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - __state) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                s_rpcN++; s_rpcMs += ms; if (ms > s_rpcMax) s_rpcMax = ms;
                Summary();
            }
        }
    }
}
