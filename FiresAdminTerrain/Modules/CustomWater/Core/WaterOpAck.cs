using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // ── Water edits always hear back (rivers diff R11; [ghost], agreed with [worldgen]) ───────────────────────────────────────────────
    // A client's water edit (create / remove / modify / clear zone) is applied locally at once and sent to the server, which broadcasts
    // the confirmation to everyone EXCEPT the sender (SafeRoutedRpc.InvokeSafeToAllExcept). So a refused edit (not an admin) or a lost
    // one left the sender showing water the server does not have, silently. Now:
    //   * the server sends ONE ack to the sender after it handled the request: ok, or refused with the reason (WaterOperationRPC);
    //     "ok" means the WATER op was accepted, nothing about any terrain carve around it (that is the voxel path, VoxelAnchorGuard);
    //   * the client keeps each sent edit pending; a refusal shows the server's reason on screen (once per reason per 5 s, not per dab);
    //   * no ack in 10 s, or a refusal: a refused / unconfirmed CREATE is taken back locally, then the client asks the server for its
    //     copy of the water there (RequestDiskSyncForPosition), at most once per 64 m cell per 10 s, one message per area: the client
    //     converges to the server's truth instead of guessing.
    // Host and single player never track anything (their edits are the server's). Debug line every 60 s with the counts.
    internal static class WaterOpAck
    {
        internal const byte Create = 1, Remove = 2, Modify = 3, ClearZone = 4;
        private const string RpcAck = "FiresAdminTerrain_Water_Ack";
        private const float Timeout = 10f, CellSize = 64f, CellCooldown = 10f, ReasonCooldown = 5f, CountsEvery = 60f;

        private struct Pending { public byte Kind; public int Hash; public float At; public Vector3 Where; }
        private static readonly Dictionary<long, Pending> s_pending = new Dictionary<long, Pending>();
        private static readonly Dictionary<long, float> s_cellSyncedAt = new Dictionary<long, float>();
        private static readonly Dictionary<string, float> s_reasonSaidAt = new Dictionary<string, float>();
        private static readonly List<long> s_scratch = new List<long>();
        private static int s_sent, s_acked, s_refused, s_timedOut, s_resyncs;
        private static float s_countsAt = -1f;
        private static Ticker s_ticker;

        private static long Key(byte kind, int hash) => ((long)kind << 32) | (uint)hash;

        internal static int ClearZoneHash(Vector3 center, float radius)
            => unchecked(Mathf.RoundToInt(center.x * 4f) * 73856093 ^ Mathf.RoundToInt(center.z * 4f) * 19349663 ^ Mathf.RoundToInt(radius * 4f) * 83492791);

        /// <summary>From WaterOperationRPC.Initialize, with the other water RPCs (both sides run the same FAT).</summary>
        internal static void Register(ZRoutedRpc rpc) => rpc.Register<ZPackage>(RpcAck, HandleAck);

        // ── server ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        internal static void Send(long sender, byte kind, int hash, bool ok, string reason)
        {
            if (ZRoutedRpc.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (sender == 0L || sender == ZDOMan.GetSessionID()) return;   // the host's own edits need no ack
            var pkg = new ZPackage();
            pkg.Write(kind);
            pkg.Write(hash);
            pkg.Write(ok);
            pkg.Write(reason ?? "");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcAck, pkg);
        }

        // ── client ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        internal static void Track(byte kind, int hash, Vector3 where)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            s_pending[Key(kind, hash)] = new Pending { Kind = kind, Hash = hash, At = Time.realtimeSinceStartup, Where = where };
            s_sent++;
            if (s_ticker == null)
            {
                var go = new GameObject("FAT_WaterOpAck");
                UnityEngine.Object.DontDestroyOnLoad(go);
                s_ticker = go.AddComponent<Ticker>();
            }
        }

        private static void HandleAck(long sender, ZPackage pkg)
        {
            if (pkg == null || ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (!FatNetGuard.FromServer(sender, "water ack")) return;
            try
            {
                byte kind = pkg.ReadByte();
                int hash = pkg.ReadInt();
                bool ok = pkg.ReadBool();
                string reason = pkg.ReadString();
                long key = Key(kind, hash);
                // a refusal is acked with hash 0 (the server authorises before it reads the request, [worldgen]): it is this client's OLDEST
                // pending edit of that kind (one connection, in order); a wrong match is corrected by the 10 s resync anyway
                if (!ok && hash == 0)
                {
                    float oldest = float.MaxValue;
                    foreach (var kv in s_pending)
                        if (kv.Value.Kind == kind && kv.Value.At < oldest) { oldest = kv.Value.At; key = kv.Key; }
                    if (oldest == float.MaxValue) return;
                }
                if (!s_pending.TryGetValue(key, out var p)) return;   // late (already timed out and resynced)
                s_pending.Remove(key);
                if (ok) { s_acked++; return; }
                s_refused++;
                Say($"Water edit refused by the server: {(string.IsNullOrEmpty(reason) ? "no reason given" : reason)}", reason ?? "");
                Converge(p, "refused");
            }
            catch (Exception ex) { FiresLogger.LogDebug($"[WaterOpAck] bad ack from the server: {ex.Message}"); }
        }

        private static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (s_pending.Count > 0)
            {
                s_scratch.Clear();
                foreach (var kv in s_pending) if (now - kv.Value.At > Timeout) s_scratch.Add(kv.Key);
                if (s_scratch.Count > 0)
                {
                    int converged = 0; Vector3 first = Vector3.zero;
                    foreach (var key in s_scratch)
                    {
                        var p = s_pending[key];
                        s_pending.Remove(key);
                        s_timedOut++;
                        if (converged == 0) first = p.Where;
                        if (Converge(p, "no answer")) converged++;
                    }
                    Say($"The server did not confirm {s_scratch.Count} water edit(s) near ({first.x:0},{first.z:0}): asked it for its copy of the water there", "timeout");
                }
            }
            if (s_countsAt < 0f) s_countsAt = now;
            else if (now - s_countsAt >= CountsEvery)
            {
                if (s_sent > 0 || s_timedOut > 0)
                    FiresLogger.LogDebug($"[WaterOpAck] water acks: {s_sent} sent / {s_acked} acked / {s_refused} refused / {s_timedOut} timed out -> {s_resyncs} resync(s), {s_pending.Count} pending (last {now - s_countsAt:0} s)");
                s_sent = s_acked = s_refused = s_timedOut = s_resyncs = 0;
                s_countsAt = now;
            }
        }

        // a refused / unconfirmed create is taken back here; then the server's copy of the water in that 64 m cell is asked for (throttled)
        private static bool Converge(Pending p, string why)
        {
            if (p.Kind == Create) WaterOperationRPC.ApplyRemovalLocal(p.Hash);
            long cell = ((long)Mathf.FloorToInt(p.Where.x / CellSize) << 32) | (uint)Mathf.FloorToInt(p.Where.z / CellSize);
            float now = Time.realtimeSinceStartup;
            if (s_cellSyncedAt.TryGetValue(cell, out float at) && now - at < CellCooldown) return false;
            s_cellSyncedAt[cell] = now;
            if (s_cellSyncedAt.Count > 256) s_cellSyncedAt.Clear();
            s_resyncs++;
            WaterOperationRPC.RequestDiskSyncForPosition(p.Where, null);
            FiresLogger.LogDebug($"[WaterOpAck] water edit kind {p.Kind} hash {p.Hash} {why}: resync asked at ({p.Where.x:0},{p.Where.z:0})");
            return true;
        }

        private static void Say(string line, string reasonKey)
        {
            float now = Time.realtimeSinceStartup;
            if (s_reasonSaidAt.TryGetValue(reasonKey, out float at) && now - at < ReasonCooldown) { FiresLogger.LogDebug("[WaterOpAck] " + line); return; }
            s_reasonSaidAt[reasonKey] = now;
            FiresLogger.LogInfo("[WaterOpAck] " + line);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, line, 0, null, false);
        }

        /// <summary>Logout: nothing pending carries into the next world.</summary>
        internal static void ForgetSession() { s_pending.Clear(); s_cellSyncedAt.Clear(); }

        private sealed class Ticker : MonoBehaviour
        {
            private float _next;
            private void Update()
            {
                if (Time.realtimeSinceStartup < _next) return;
                _next = Time.realtimeSinceStartup + 1f;
                if (ZNet.instance == null) { ForgetSession(); return; }
                Tick();
            }
        }
    }
}
