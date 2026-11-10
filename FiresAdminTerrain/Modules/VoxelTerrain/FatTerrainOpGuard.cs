using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1089: the terrain-op storm at Fire's Deep North hut (~20 ops/s at (162.6, 681.4) r 10, every one a Save + Poke +
    // ResetGrass, so clutter flashed and heightmaps rebuilt nonstop) is VANILLA 1.0: Fireplace.Awake starts
    // InvokeRepeating("UpdateSnowMelt", .., m_snowMelterInterval) on every burning fire in the Deep North, and UpdateSnowMelt
    // instantiates its m_snowMelter (snow_fire_big: a TerrainOp, Cultivate paint r 10, strength -0.2). fire_pit, hearth and
    // bonfire use 0.2 s (5 ops/s each) and it runs on EVERY peer that has the fire loaded (no owner check), each sending
    // RPC_ApplyOperation to the terrain owner. The paint converges after a few ops, so the rest change nothing but still cost.
    // Guard: an identical op (same position within 0.1 m, same settings) from the same sender more than 3 times in 10 s is
    // dropped, on the sending side (TerrainComp.ApplyOperation) and on the receiving side (RPC_ApplyOperation). A hoe or
    // pickaxe moves between strokes, so normal use never repeats an identical op. Debug lines name the sender, the op and,
    // for a local op, the managed stack.
    internal static class FatTerrainOpGuard
    {
        private const int MaxRepeats = 3;
        private const float Window = 10f;

        private struct Seen { public float First; public int Count; public int Dropped; }

        private static readonly Dictionary<long, Seen> s_seen = new Dictionary<long, Seen>();
        private static float s_purgeAt, s_logRecvAt, s_logSendAt, s_logDropAt;
        private static int s_droppedTotal;
        private static bool s_said;

        private static long Key(long sender, Vector3 pos, TerrainOp.Settings s, string what)
        {
            unchecked
            {
                long h = sender * 1000003L + (what.StartsWith("send") ? 7 : 0);
                h = h * 31 + Mathf.RoundToInt(pos.x * 10f);
                h = h * 31 + Mathf.RoundToInt(pos.y * 10f);
                h = h * 31 + Mathf.RoundToInt(pos.z * 10f);
                h = h * 31 + (s.m_level ? 1 : 0) + (s.m_raise ? 2 : 0) + (s.m_smooth ? 4 : 0) + (s.m_paintCleared ? 8 : 0) + ((int)s.m_paintType << 4);
                h = h * 31 + Mathf.RoundToInt(s.m_paintRadius * 100f);
                h = h * 31 + Mathf.RoundToInt(s.m_paintStrength * 100f);
                h = h * 31 + Mathf.RoundToInt(s.m_levelRadius * 100f);
                h = h * 31 + Mathf.RoundToInt(s.m_raiseRadius * 100f);
                h = h * 31 + Mathf.RoundToInt(s.m_raiseDelta * 100f);
                h = h * 31 + Mathf.RoundToInt(s.m_levelOffset * 100f);
                return h;
            }
        }

        // True = let the op through.
        internal static bool Allow(long sender, Vector3 pos, TerrainOp.Settings s, string what)   // internal: FatTrailBatch guards each batched op too
        {
            if (!s_said) { s_said = true; FiresLogger.LogInfo("[OpStorm] terrain op repeat guard executing (TerrainComp.ApplyOperation send + RPC_ApplyOperation receive; identical op > 3 in 10 s per sender = dropped)"); }
            float now = Time.unscaledTime;
            if (now >= s_purgeAt)
            {
                s_purgeAt = now + 30f;
                var stale = new List<long>();
                foreach (var kv in s_seen) if (now - kv.Value.First > Window) stale.Add(kv.Key);
                foreach (var k in stale) s_seen.Remove(k);
            }
            long key = Key(sender, pos, s, what);
            s_seen.TryGetValue(key, out Seen e);
            if (e.Count == 0 || now - e.First > Window) { e.First = now; e.Count = 0; e.Dropped = 0; }
            e.Count++;
            bool allow = e.Count <= MaxRepeats;
            if (!allow) { e.Dropped++; s_droppedTotal++; }
            s_seen[key] = e;
            if (!allow && now >= s_logDropAt)
            {
                s_logDropAt = now + 60f;   // 0.2.1102: a 60 s summary (was 10 s)
                FiresLogger.LogInfo($"[OpStorm] dropped repeated terrain op ({what}) from {Who(sender)} at ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}): {Describe(s)}; {e.Count} identical in {now - e.First:0.0} s, {s_droppedTotal} dropped this session (vanilla Deep North fire snow melter repeats every 0.2 s)");
            }
            return allow;
        }

        private static string Who(long sender)
        {
            var zn = ZNet.instance;
            if (zn == null) return sender.ToString();
            if (sender == ZNet.GetUID()) return $"this peer ({sender}{(zn.IsServer() ? ", server" : "")})";
            var peer = zn.GetPeer(sender);
            if (peer != null) return $"{(string.IsNullOrEmpty(peer.m_playerName) ? "peer" : peer.m_playerName)} ({sender}{(peer.m_server ? ", server" : "")})";
            return sender.ToString();
        }

        internal static string Describe(TerrainOp.Settings s) =>
            $"level {s.m_level} r {s.m_levelRadius:0.0}, raise {s.m_raise} d {s.m_raiseDelta:0.00}, smooth {s.m_smooth}, paintCleared {s.m_paintCleared} {s.m_paintType} r {s.m_paintRadius:0.0} strength {s.m_paintStrength:0.00}";

        private static string Stack()
        {
            var st = new StackTrace(2, false);
            var sb = new System.Text.StringBuilder();
            int n = 0;
            for (int i = 0; i < st.FrameCount && n < 8; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                if (m == null) continue;
                if (n++ > 0) sb.Append(" < ");
                sb.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?").Append('.').Append(m.Name);
            }
            return sb.ToString();
        }

        [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyOperation))]
        private static class Patch_Send
        {
            private static bool Prefix(TerrainOp modifier)
            {
                if (modifier == null || ZNet.instance == null) return true;
                Vector3 pos = modifier.transform.position;
                if (FatDiag.OpStorm && Time.unscaledTime >= s_logSendAt)
                {
                    s_logSendAt = Time.unscaledTime + 5f;
                    FiresLogger.LogDebug($"[OpStorm] send '{modifier.gameObject.name}' at ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) from {Who(ZNet.GetUID())}: {Describe(modifier.m_settings)}{(FatDiag.OpStormStacks ? "; stack " + Stack() : "")}");
                }
                return Allow(ZNet.GetUID(), pos, modifier.m_settings, "send " + modifier.gameObject.name);
            }
        }

        [HarmonyPatch(typeof(TerrainComp), "RPC_ApplyOperation")]
        private static class Patch_Receive
        {
            private static bool Prefix(TerrainComp __instance, long sender, ZPackage pkg)
            {
                if (pkg == null) return true;
                int at = pkg.GetPos();
                Vector3 pos;
                TerrainOp.Settings s;
                try
                {
                    pos = pkg.ReadVector3();
                    if (pkg.ReadBool()) pkg.ReadVector3();
                    s = TerrainOp.Settings.Deserialize(pkg);
                }
                catch { pkg.SetPos(at); return true; }
                pkg.SetPos(at);
                if (s == null) return true;
                if (FatDiag.OpStorm && Time.unscaledTime >= s_logRecvAt)
                {
                    s_logRecvAt = Time.unscaledTime + 5f;
                    FiresLogger.LogDebug($"[OpStorm] receive from {Who(sender)} at ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}): {Describe(s)}");
                }
                return Allow(sender, pos, s, "receive");
            }
        }
    }
}
