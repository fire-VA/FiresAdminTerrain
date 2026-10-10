using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══════════════════════════════════════════════════════════════════════════
    // THE WATER PLAN — the ONE authoritative representation of all inland water.
    // Ported from the FiresSandbox worldgen (Packages/com.fires.worldgen), whose
    // contract this file carries verbatim because the contract IS the fix.
    //
    // WATER IS AUTHORED, NOT DISCOVERED:
    //   • a still BODY (lake / pond / tarn / marsh) is a footprint plus ONE level.
    //     The level is a constant. It is chosen once, highest to lowest, and it
    //     NEVER changes — no settle, no walk, no clamp, no blur exists that can
    //     touch it.
    //   • a CONNECTION (river / stream) joins two bodies (or a spring to a body,
    //     or a body to the ocean): a polyline whose level profile runs MONOTONE
    //     from exactly the upstream body's level to exactly the downstream
    //     body's level. Flat across its cross-section; height changes only
    //     along the flow.
    //   • TERRAIN CONFORMS to the plan (bowls under levels, rims above them,
    //     beds under profiles, banks above them) — never the reverse.
    //   • RENDERERS OBEY the plan. "What level is the water here" is a LOOKUP,
    //     so disagreement between consumers is structurally impossible.
    //
    // Plain data + pure queries on the 2m world. No flood fill, no wet/dry
    // inference, no 8m anything.
    //
    // WHY THIS REPLACES WHAT WAS HERE: the old pipeline discovered water by
    // flood-fill and then ran eight repair passes over the same field (depth-film
    // drying, floor+6m capping, bank building, edge retreat, pool reshaping,
    // level blurring, waterfall breaks, class fill). Each repaired what the last
    // broke. Measured on seed -2062588818: 51,495 above-bank violations, lake
    // sheets blurred into tilted planes, and a class fill claiming 2.5M cells
    // where the basin walk owned 461k. None of those states can be represented
    // here — a body has one level and the terrain is cut to hold it.
    // ═══════════════════════════════════════════════════════════════════════════

    internal enum WaterBodyKind : byte
    {
        Lake = 0,
        Pond = 1,
        Tarn = 2,
        Marsh = 3,      // inland marsh — own level, one flat sheet, mounds clip through
        SeaMarsh = 4,   // sea-level marsh — the OCEAN plane owns its water (level == sea)
        // THE OCEAN IS A BODY. Every other destination a river can reach has a
        // real Level, so a connection terminates on it properly; the sea being
        // the one exception is what let profiles march on across the seabed. It
        // carries NO mask — its footprint is analytic (any column whose ground
        // lies under SEA), and a world-spanning 2m mask would be ~110MB — so
        // Contains() is false for it and the existing ocean path keeps those
        // queries.
        Ocean = 5,
    }

    // One still body: a rasterized footprint (exact, 2m cells) + THE level.
    internal sealed class WaterBody
    {
        public int Id;
        public WaterBodyKind Kind;
        public float Level;                  // THE constant. Water renders at exactly this y.
        public int Gx0, Gz0, W, H;           // footprint rect in 2m grid cells
        public byte[] Mask;                  // W*H, 1 = inside the footprint
        public float MinX, MinZ, MaxX, MaxZ; // world-space bounds (fast reject)

        public bool Contains(int gx, int gz)
        {
            // FOOTPRINTLESS = the analytic ocean. Test the footprint, not null: the
            // planner builds that body with Array.Empty<byte>(), so a null check alone
            // never fires and the sentinel silently does nothing.
            if (Mask == null || W <= 0 || H <= 0) return false;
            int lx = gx - Gx0, lz = gz - Gz0;
            if (lx < 0 || lz < 0 || lx >= W || lz >= H) return false;
            return Mask[lz * W + lx] != 0;
        }
    }

    // One flowing connection. Nodes are (x, z, width, level); level is monotone
    // non-increasing from the first node (upstream) to the last (downstream).
    internal sealed class WaterConnection
    {
        public const int SPRING = -1;      // no upstream body
        public const int OCEAN = -2;       // drains to the sea
        public const int CONFLUENCE = -3;  // ends on another river — a real destination, tied to the trunk it joins
        public const int BASIN = -4;       // pours into a bodyless depression and goes no further — valid and terminal

        public int Id;
        public int FromBody;
        public int ToBody;
        public float[] Nodes;               // packed x,z,width,level per node (stride 4)
        public float MinX, MinZ, MaxX, MaxZ;

        public int NodeCount => Nodes.Length / 4;
    }

    // A CANYON IS PLANNED, NOT DETECTED. It is a SPAN OF AN EXISTING RUN —
    // a connection id plus a node range — which is the safety property: annotating
    // a run cannot move a river, re-route it, or touch a body. Only the
    // cross-section changes, and only inside the declared span. Because it is
    // planned, both invariants are fixed once at siting instead of measured and
    // patched afterwards: InletY > OutletY (a canyon always descends), and the
    // outlet lands on the run's own ToBody so it meets still water cleanly.
    internal sealed class CanyonReach
    {
        public int Id;
        public int ConnectionId;      // the run this annotates — canyons are spans, never new geometry
        public int Node0, Node1;      // inclusive node span within that run, in FLOW ORDER
        public float InletY, OutletY; // its own descent, fixed at siting: InletY > OutletY, always
        public float FloorHalfW;      // half-width of the flat floor: channel + walkable shelf each side
        public float ShelfM;          // the walkable land each side of the water, inside FloorHalfW
        public float MinX, MinZ, MaxX, MaxZ;
    }

    internal sealed class FiresWaterPlan
    {
        public readonly List<WaterBody> Bodies = new List<WaterBody>();
        public readonly List<WaterConnection> Connections = new List<WaterConnection>();
        public readonly List<CanyonReach> Canyons = new List<CanyonReach>();

        // Coarse buckets (16m) so per-cell queries are O(few). Rebuilt after
        // load/plan; pure acceleration, carries no truth of its own.
        private const float BUCKET = 16f;
        private Dictionary<long, List<int>> _bodyBuckets;
        private Dictionary<long, List<int>> _connBuckets;
        private float _half;
        private float _cell;

        private static long BKey(int bx, int bz) => ((long)bx << 32) ^ (uint)bz;

        public void BuildIndex(int res, float half)
        {
            _half = half; _cell = (2f * half) / (res - 1);
            _bodyBuckets = new Dictionary<long, List<int>>(4096);
            _connBuckets = new Dictionary<long, List<int>>(4096);
            for (int i = 0; i < Bodies.Count; i++)
            {
                var b = Bodies[i];
                if (b.Mask == null || b.W <= 0 || b.H <= 0) continue;   // analytic ocean — never bucketed (see WaterBody.Contains)
                int bx0 = Mathf.FloorToInt(b.MinX / BUCKET), bx1 = Mathf.FloorToInt(b.MaxX / BUCKET);
                int bz0 = Mathf.FloorToInt(b.MinZ / BUCKET), bz1 = Mathf.FloorToInt(b.MaxZ / BUCKET);
                for (int bz = bz0; bz <= bz1; bz++)
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        long k = BKey(bx, bz);
                        if (!_bodyBuckets.TryGetValue(k, out var l)) _bodyBuckets[k] = l = new List<int>(2);
                        l.Add(i);
                    }
            }
            for (int i = 0; i < Connections.Count; i++)
            {
                var c = Connections[i];
                // segments can cut diagonally across buckets — bucket each segment's AABB
                for (int s = 0; s + 4 < c.Nodes.Length; s += 4)
                {
                    float ax = c.Nodes[s], az = c.Nodes[s + 1], wA = c.Nodes[s + 2];
                    float bx2 = c.Nodes[s + 4], bz2 = c.Nodes[s + 5], wB = c.Nodes[s + 6];
                    float rr = Mathf.Max(wA, wB) * 0.5f + 4f;
                    int qx0 = Mathf.FloorToInt((Mathf.Min(ax, bx2) - rr) / BUCKET);
                    int qx1 = Mathf.FloorToInt((Mathf.Max(ax, bx2) + rr) / BUCKET);
                    int qz0 = Mathf.FloorToInt((Mathf.Min(az, bz2) - rr) / BUCKET);
                    int qz1 = Mathf.FloorToInt((Mathf.Max(az, bz2) + rr) / BUCKET);
                    for (int bz = qz0; bz <= qz1; bz++)
                        for (int bx = qx0; bx <= qx1; bx++)
                        {
                            long k = BKey(bx, bz);
                            if (!_connBuckets.TryGetValue(k, out var l)) _connBuckets[k] = l = new List<int>(2);
                            if (l.Count == 0 || l[l.Count - 1] != i) l.Add(i);
                        }
                }
            }
        }

        // ── THE queries (renderers / conformance / paint / swim all use these) ──

        // The still body covering a world position, if any.
        public bool TryGetBodyAt(float wx, float wz, out WaterBody body)
        {
            body = null;
            if (_bodyBuckets == null) return false;
            if (!_bodyBuckets.TryGetValue(BKey(Mathf.FloorToInt(wx / BUCKET), Mathf.FloorToInt(wz / BUCKET)), out var l)) return false;
            int gx = Mathf.RoundToInt((wx + _half) / _cell);
            int gz = Mathf.RoundToInt((wz + _half) / _cell);
            for (int i = 0; i < l.Count; i++)
            {
                var b = Bodies[l[i]];
                if (wx < b.MinX || wx > b.MaxX || wz < b.MinZ || wz > b.MaxZ) continue;
                if (b.Contains(gx, gz)) { body = b; return true; }
            }
            return false;
        }

        // The river ribbon at a world position: nearest connection segment whose
        // half-width (+ margin) covers the point. Level is interpolated along the
        // segment — flat across, varying only along the flow. Margin lets the
        // ribbon overshoot into the banks so terrain clips it (never a gap).
        public bool TryGetRiverAt(float wx, float wz, float margin, out float level, out float width, out int connId)
        {
            level = 0f; width = 0f; connId = -1;
            if (_connBuckets == null) return false;
            if (!_connBuckets.TryGetValue(BKey(Mathf.FloorToInt(wx / BUCKET), Mathf.FloorToInt(wz / BUCKET)), out var l)) return false;
            for (int i = 0; i < l.Count; i++)
            {
                var c = Connections[l[i]];
                if (wx < c.MinX - 40f || wx > c.MaxX + 40f || wz < c.MinZ - 40f || wz > c.MaxZ + 40f) continue;
                var n = c.Nodes;
                for (int s = 0; s + 4 < n.Length; s += 4)
                {
                    float ax = n[s], az = n[s + 1], aw = n[s + 2], al = n[s + 3];
                    float bx = n[s + 4], bz = n[s + 5], bw = n[s + 6], bl = n[s + 7];
                    float ex = bx - ax, ez = bz - az;
                    float len2 = ex * ex + ez * ez;
                    float t = len2 > 1e-6f ? ((wx - ax) * ex + (wz - az) * ez) / len2 : 0f;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    float dx = wx - (ax + ex * t), dz = wz - (az + ez * t);
                    float d2 = dx * dx + dz * dz;
                    // SAME 1m half-width floor as the conformance rasterizer — the
                    // corridor the terrain digs and the corridor the mesh renders
                    // must be ONE shape, or a field-wet/mesh-dry strip survives as
                    // a floating edge.
                    float hw = Mathf.Max(1f, (aw + (bw - aw) * t) * 0.5f) + margin;
                    if (d2 <= hw * hw)
                    {
                        // LOWEST covering level wins, not the nearest segment: at a
                        // confluence real water rides the LOWER profile (the
                        // tributary blends down into the mainstem). Nearest-wins
                        // floated the mesh metres above both.
                        float lv = al + (bl - al) * t;
                        if (connId < 0 || lv < level)
                        {
                            level = lv;
                            width = aw + (bw - aw) * t;
                            connId = c.Id;
                        }
                    }
                }
            }
            return connId >= 0;
        }

        // One combined answer for "what water does the PLAN put here, at what
        // level". Body wins over ribbon (a ribbon entering a body ends at its
        // shore; inside, the body's constant is the truth). SeaMarsh reports the
        // sea: the OCEAN plane owns that water, so callers must not draw an
        // inland sheet there.
        public bool TryGetWaterAt(float wx, float wz, float ribbonMargin, out float level, out bool oceanOwned)
        {
            level = 0f; oceanOwned = false;
            if (TryGetBodyAt(wx, wz, out var b))
            {
                level = b.Level;
                oceanOwned = b.Kind == WaterBodyKind.SeaMarsh;
                return true;
            }
            if (TryGetRiverAt(wx, wz, ribbonMargin, out level, out _, out _)) return true;
            return false;
        }

        // ── persistence (rides in the gen cache alongside the height grid) ──
        public void Save(BinaryWriter bw)
        {
            bw.Write(Bodies.Count);
            for (int i = 0; i < Bodies.Count; i++)
            {
                var b = Bodies[i];
                bw.Write(b.Id); bw.Write((byte)b.Kind); bw.Write(b.Level);
                bw.Write(b.Gx0); bw.Write(b.Gz0); bw.Write(b.W); bw.Write(b.H);
                bw.Write(b.Mask != null ? b.Mask.Length : 0);
                if (b.Mask != null) bw.Write(b.Mask);
                bw.Write(b.MinX); bw.Write(b.MinZ); bw.Write(b.MaxX); bw.Write(b.MaxZ);
            }
            bw.Write(Connections.Count);
            for (int i = 0; i < Connections.Count; i++)
            {
                var c = Connections[i];
                bw.Write(c.Id); bw.Write(c.FromBody); bw.Write(c.ToBody);
                bw.Write(c.Nodes.Length);
                for (int k = 0; k < c.Nodes.Length; k++) bw.Write(c.Nodes[k]);
                bw.Write(c.MinX); bw.Write(c.MinZ); bw.Write(c.MaxX); bw.Write(c.MaxZ);
            }
            bw.Write(Canyons.Count);
            for (int i = 0; i < Canyons.Count; i++)
            {
                var q = Canyons[i];
                bw.Write(q.Id); bw.Write(q.ConnectionId); bw.Write(q.Node0); bw.Write(q.Node1);
                bw.Write(q.InletY); bw.Write(q.OutletY); bw.Write(q.FloorHalfW); bw.Write(q.ShelfM);
                bw.Write(q.MinX); bw.Write(q.MinZ); bw.Write(q.MaxX); bw.Write(q.MaxZ);
            }
        }

        public static FiresWaterPlan Load(BinaryReader br)
        {
            var p = new FiresWaterPlan();
            int nb = br.ReadInt32();
            for (int i = 0; i < nb; i++)
            {
                var b = new WaterBody
                {
                    Id = br.ReadInt32(),
                    Kind = (WaterBodyKind)br.ReadByte(),
                    Level = br.ReadSingle(),
                    Gx0 = br.ReadInt32(),
                    Gz0 = br.ReadInt32(),
                    W = br.ReadInt32(),
                    H = br.ReadInt32(),
                };
                int maskLen = br.ReadInt32();
                b.Mask = maskLen > 0 ? br.ReadBytes(maskLen) : System.Array.Empty<byte>();   // never null: the ocean body ships a zero-length mask
                b.MinX = br.ReadSingle(); b.MinZ = br.ReadSingle();
                b.MaxX = br.ReadSingle(); b.MaxZ = br.ReadSingle();
                p.Bodies.Add(b);
            }
            int ncn = br.ReadInt32();
            for (int i = 0; i < ncn; i++)
            {
                var c = new WaterConnection
                {
                    Id = br.ReadInt32(),
                    FromBody = br.ReadInt32(),
                    ToBody = br.ReadInt32(),
                };
                int len = br.ReadInt32();
                c.Nodes = new float[len];
                for (int k = 0; k < len; k++) c.Nodes[k] = br.ReadSingle();
                c.MinX = br.ReadSingle(); c.MinZ = br.ReadSingle();
                c.MaxX = br.ReadSingle(); c.MaxZ = br.ReadSingle();
                p.Connections.Add(c);
            }
            int nq = br.ReadInt32();
            for (int i = 0; i < nq; i++)
            {
                p.Canyons.Add(new CanyonReach
                {
                    Id = br.ReadInt32(),
                    ConnectionId = br.ReadInt32(),
                    Node0 = br.ReadInt32(),
                    Node1 = br.ReadInt32(),
                    InletY = br.ReadSingle(),
                    OutletY = br.ReadSingle(),
                    FloorHalfW = br.ReadSingle(),
                    ShelfM = br.ReadSingle(),
                    MinX = br.ReadSingle(),
                    MinZ = br.ReadSingle(),
                    MaxX = br.ReadSingle(),
                    MaxZ = br.ReadSingle(),
                });
            }
            return p;
        }
    }
}
