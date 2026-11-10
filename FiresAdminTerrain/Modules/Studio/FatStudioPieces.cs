using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Studio
{
    // 0.2.1261 (round Q: a Studio terrain brush broke 2 stone walls and its undo could not bring them back - the snapshots leave out
    // anything a player built; Fire 10-29 "Warn + undo restores"). For a terrain / water edit:
    //   - Hits: every player-built piece (creator != 0) within the edit's reach + Margin, nearest first ("inside" = in the edit's
    //     radius, "undercut" = in the margin, where the ground or support under it can still move). The edit is refused without
    //     ConfirmPieces when there are any.
    //   - Capture: those pieces' full network data and the item drops already lying there, appended to the edit's snapshot as a
    //     trailer (old readers stop before it; the snapshot hash covers it).
    //   - Restore (after the edit's own restore succeeded): a captured piece that is gone is made again from its data; an item drop
    //     inside the reach that was not there before (what a broken piece dropped) is removed. A piece still standing is left as is.
    internal static class FatStudioPieces
    {
        internal const float Margin = 4f;
        private const int Magic = 0x31434550;   // "PEC1"
        private const int MaxPieces = 5000, MaxDrops = 20000;
        private const float SamePos = 0.05f;
        private static readonly string Tag = "[FatStudio]";
        private static readonly int PlayerHash = "Player".GetStableHashCode();
        private static readonly int TombstoneHash = "Player_tombstone".GetStableHashCode();
        private static readonly Dictionary<int, int> s_kind = new Dictionary<int, int>();   // 0 other, 1 piece, 2 item drop

        internal struct Reach { public Vector3[] Path; public float R; }

        internal static Reach Of(Vector3[] path, float r) => new Reach { Path = path ?? new Vector3[0], R = r };

        // The XZ distance from p to the polyline (one point = a circle).
        private static float Dist(Reach a, Vector3 p)
        {
            float best = float.MaxValue;
            var path = a.Path;
            if (path.Length == 1) return Flat(p - path[0]);
            for (int i = 0; i + 1 < path.Length; i++)
            {
                Vector2 s = new Vector2(path[i].x, path[i].z), e = new Vector2(path[i + 1].x, path[i + 1].z), q = new Vector2(p.x, p.z);
                Vector2 d = e - s; float l = d.sqrMagnitude, t = l > 1e-6f ? Mathf.Clamp01(Vector2.Dot(q - s, d) / l) : 0f;
                best = Mathf.Min(best, (q - (s + d * t)).magnitude);
            }
            return best;
        }

        private static float Flat(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);

        private static int KindOf(int prefab)
        {
            if (s_kind.TryGetValue(prefab, out int k)) return k;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            k = go == null || go.GetComponent<Character>() != null ? 0 : go.GetComponent<ItemDrop>() != null ? 2 : go.GetComponent<Piece>() != null ? 1 : 0;
            s_kind[prefab] = k;
            return k;
        }

        private static IEnumerable<ZDO> Near(Reach a, float extra)
        {
            if (a.Path.Length == 0) yield break;
            float r = a.R + extra;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in a.Path) { minX = Mathf.Min(minX, p.x - r); maxX = Mathf.Max(maxX, p.x + r); minZ = Mathf.Min(minZ, p.z - r); maxZ = Mathf.Max(maxZ, p.z + r); }
            Vector2s lo = ZoneSystem.GetZone(new Vector3(minX, 0f, minZ)), hi = ZoneSystem.GetZone(new Vector3(maxX, 0f, maxZ));
            var one = new List<ZDO>();
            for (int x = lo.x; x <= hi.x; x++)
                for (int z = lo.y; z <= hi.y; z++)
                {
                    one.Clear();
                    ZDOMan.instance.FindSectorObjects(new Vector2s(x, z), new SimulationDistance(0, 0), one);
                    foreach (var o in one) if (o != null && o.IsValid() && Dist(a, o.GetPosition()) <= r) yield return o;
                }
        }

        private static bool IsPlayerPiece(ZDO z)
        {
            int prefab = z.GetPrefab();
            return prefab != PlayerHash && prefab != TombstoneHash && z.GetLong(ZDOVars.s_creator) != 0L && KindOf(prefab) == 1;
        }

        internal static string Key(int prefab, Vector3 p)
            => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}@{1:0.00},{2:0.00},{3:0.00}", prefab, p.x, p.y, p.z);

        internal static FatPieceHit[] Hits(Reach a, bool log = false)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var hits = new List<(float d, FatPieceHit h)>();
            if (ZDOMan.instance == null || ZNetScene.instance == null) return new FatPieceHit[0];
            foreach (var z in Near(a, Margin))
            {
                if (!IsPlayerPiece(z)) continue;
                var p = z.GetPosition(); float d = Dist(a, p);
                var go = ZNetScene.instance.GetPrefab(z.GetPrefab());
                hits.Add((d, new FatPieceHit { Key = Key(z.GetPrefab(), p), Name = go != null ? go.name : z.GetPrefab().ToString(), CreatorId = z.GetLong(ZDOVars.s_creator), Pos = p, Why = d <= a.R ? "inside" : "undercut" }));
            }
            hits.Sort((x, y) => x.d.CompareTo(y.d));
            var res = new FatPieceHit[hits.Count];
            for (int i = 0; i < res.Length; i++) res[i] = hits[i].h;
            if (log) FiresLogger.LogDebug($"{Tag} PieceHits: {res.Length} player piece(s) within {a.R:0.0} + {Margin:0} m of {a.Path.Length} point(s) in {Ms(t0):0.0} ms");
            return res;
        }

        // null = go on; else the refusal (no ConfirmPieces and pieces in reach)
        internal static string Gate(Reach a, bool confirm, out int count)
        {
            var hits = Hits(a, true);
            count = hits.Length;
            if (count == 0) return null;
            if (confirm) { FiresLogger.LogDebug($"{Tag} pieces: {count} player piece(s) in reach, confirmed: the edit goes ahead and its undo restores them"); return null; }
            return $"pieces: {count} player piece(s) would be undercut or broken; send confirmPieces:true (FatApi.PieceHits lists them)";
        }

        // Appends the trailer to the snapshot and re-hashes it.
        internal static void Append(FatSnapshot s, Reach a)
        {
            if (s == null || s.Bytes == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int np = 0, nd = 0;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(s.Bytes);
                    long start = ms.Position;
                    w.Write(a.R); w.Write(a.Path.Length);
                    foreach (var p in a.Path) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
                    var pieces = new List<ZDO>(); var drops = new List<ZDOID>();
                    foreach (var z in Near(a, Margin))
                    {
                        if (IsPlayerPiece(z)) { if (pieces.Count < MaxPieces) pieces.Add(z); }
                        else if (KindOf(z.GetPrefab()) == 2 && drops.Count < MaxDrops) drops.Add(z.m_uid);
                    }
                    w.Write(pieces.Count);
                    foreach (var z in pieces)
                    {
                        var pkg = new ZPackage(); z.Serialize(pkg); byte[] data = pkg.GetArray();
                        var p = z.GetPosition(); var e = z.GetRotation().eulerAngles;
                        w.Write(z.GetPrefab()); w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(e.x); w.Write(e.y); w.Write(e.z);
                        w.Write(data.Length); w.Write(data);
                    }
                    w.Write(drops.Count);
                    foreach (var id in drops) { w.Write(id.UserID); w.Write(id.ID); }
                    np = pieces.Count; nd = drops.Count;
                    w.Write((int)(ms.Position - start)); w.Write(Magic);
                }
                s.Bytes = ms.ToArray();
            }
            using (var sha = System.Security.Cryptography.SHA256.Create()) s.Hash = sha.ComputeHash(s.Bytes);
            FiresLogger.LogDebug($"{Tag} pieces: snapshot {s.Id} keeps {np} player piece(s) + {nd} item drop id(s) within {a.R:0.0} + {Margin:0} m ({Ms(t0):0.0} ms)");
        }

        // The snapshot without its trailer, for the kind's own reader (an old snapshot comes back unchanged, trailer null).
        internal static byte[] Split(byte[] bytes, out byte[] trailer)
        {
            trailer = null;
            if (bytes == null || bytes.Length < 8 || BitConverter.ToInt32(bytes, bytes.Length - 4) != Magic) return bytes;
            int len = BitConverter.ToInt32(bytes, bytes.Length - 8);
            if (len < 0 || len > bytes.Length - 8) return bytes;
            int body = bytes.Length - 8 - len;
            trailer = new byte[len]; Buffer.BlockCopy(bytes, body, trailer, 0, len);
            var head = new byte[body]; Buffer.BlockCopy(bytes, 0, head, 0, body);
            return head;
        }

        internal static void Restore(byte[] trailer, string snapId)
        {
            if (trailer == null || ZDOMan.instance == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int made = 0, standing = 0, removed = 0;
            try
            {
                Reach a;
                var saved = new List<(int prefab, Vector3 pos, Vector3 euler, byte[] data)>();
                var before = new HashSet<ZDOID>();
                using (var r = new BinaryReader(new MemoryStream(trailer)))
                {
                    float rad = r.ReadSingle(); int n = r.ReadInt32(); if (n < 0 || n > 100000) return;
                    var path = new Vector3[n];
                    for (int i = 0; i < n; i++) path[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    a = Of(path, rad);
                    int np = r.ReadInt32(); if (np < 0 || np > MaxPieces) return;
                    for (int i = 0; i < np; i++)
                    {
                        int prefab = r.ReadInt32();
                        var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); var e = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        int len = r.ReadInt32(); if (len < 0 || len > 1024 * 1024) return;
                        saved.Add((prefab, p, e, r.ReadBytes(len)));
                    }
                    int nd = r.ReadInt32(); if (nd < 0 || nd > MaxDrops) return;
                    for (int i = 0; i < nd; i++) before.Add(new ZDOID(r.ReadInt64(), r.ReadUInt32()));
                }
                var live = new List<ZDO>(Near(a, Margin + 1f));
                long me = ZDOMan.GetSessionID();
                foreach (var s in saved)
                {
                    bool there = false;
                    foreach (var z in live) if (z.GetPrefab() == s.prefab && (z.GetPosition() - s.pos).sqrMagnitude <= SamePos * SamePos) { there = true; break; }
                    if (there) { standing++; continue; }
                    var nz = ZDOMan.instance.CreateNewZDO(s.pos, s.prefab);
                    nz.Deserialize(new ZPackage(s.data));
                    nz.SetOwner(me); nz.SetPosition(s.pos); nz.SetRotation(Quaternion.Euler(s.euler));
                    made++;
                }
                // what broke pieces dropped: item drops inside the reach that were not there before (only when a piece was missing)
                if (made > 0)
                    foreach (var z in live)
                        if (KindOf(z.GetPrefab()) == 2 && !before.Contains(z.m_uid)) { z.SetOwner(me); ZDOMan.instance.DestroyZDO(z); removed++; }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{Tag} pieces: restore of snapshot {snapId} failed: {ex.GetType().Name}: {ex.Message}"); return; }
            FiresLogger.LogDebug($"{Tag} pieces: undo {snapId}: {made} player piece(s) made again, {standing} still standing, {removed} item drop(s) the break left removed ({Ms(t0):0.0} ms)");
        }

        private static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }
}
