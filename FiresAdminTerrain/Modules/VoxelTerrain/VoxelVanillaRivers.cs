using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Where VANILLA already put rivers ─────────────────────────────────
    // The generator carves its own river network and pulls those beds to just
    // under the sea plane (WorldGenerator.AddRivers drags the normalised height
    // down to ~0.12-0.14), which is why a vanilla river holds the ocean mesh at
    // y=30 with no water system of its own.
    //
    // That makes them the natural OUTLET for everything we solve: a channel we
    // carve should terminate in a vanilla river and grade to its waterline,
    // exactly as it does at the coast — and we must not carve INSIDE one, since
    // vanilla already did and a second cut only fights the sea plane (a channel
    // surface pinned above 30.3 over a bed clamped at 30.1 is the wall of water
    // against the ocean plane).
    //
    // WorldGenerator hands out the segment list directly (GetRivers/GetStreams),
    // so this is read from the world's own generator, not re-derived.
    internal static class VoxelVanillaRivers
    {
        private const float SEA = 30f;
        private const float BucketSize = 128f;

        private struct Seg
        {
            public Vector2 A, B;
            public float Half;      // widest half-width, plus the curve amplitude
        }

        private static Seg[] s_segs = new Seg[0];
        private static Dictionary<int, int[]> s_index = new Dictionary<int, int[]>();
        private static volatile bool s_ready;

        public static bool Ready => s_ready;
        public static int SegmentCount => s_segs.Length;

        public static void Clear()
        {
            s_ready = false;
            s_segs = new Seg[0];
            s_index = new Dictionary<int, int[]>();
        }

        // Worker-safe: reads the generator's own lists once, then only arrays.
        public static void Build(WorldGenerator wg)
        {
            Clear();
            if (wg == null) return;

            var segs = new List<Seg>(256);
            Collect(wg.GetRivers(), segs);
            Collect(wg.GetStreams(), segs);
            if (segs.Count == 0) return;

            var index = new Dictionary<int, List<int>>(segs.Count * 2);
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                float minX = Mathf.Min(s.A.x, s.B.x) - s.Half, maxX = Mathf.Max(s.A.x, s.B.x) + s.Half;
                float minZ = Mathf.Min(s.A.y, s.B.y) - s.Half, maxZ = Mathf.Max(s.A.y, s.B.y) + s.Half;
                for (int bz = Mathf.FloorToInt(minZ / BucketSize); bz <= Mathf.FloorToInt(maxZ / BucketSize); bz++)
                    for (int bx = Mathf.FloorToInt(minX / BucketSize); bx <= Mathf.FloorToInt(maxX / BucketSize); bx++)
                    {
                        int key = BucketKey(bx, bz);
                        if (!index.TryGetValue(key, out var list)) index[key] = list = new List<int>(4);
                        list.Add(i);
                    }
            }

            var flat = new Dictionary<int, int[]>(index.Count);
            foreach (var kv in index) flat[kv.Key] = kv.Value.ToArray();

            s_segs = segs.ToArray();
            s_index = flat;
            s_ready = true;
        }

        private static void Collect(List<WorldGenerator.River> rivers, List<Seg> into)
        {
            if (rivers == null) return;
            for (int i = 0; i < rivers.Count; i++)
            {
                var r = rivers[i];
                if (r == null) continue;
                // The generator meanders each river by up to curveWidth about
                // its centreline, so the corridor it can occupy is the widest
                // half-width plus that amplitude.
                float half = Mathf.Max(r.widthMin, r.widthMax) * 0.5f + r.curveWidth * 0.5f;
                into.Add(new Seg { A = r.p0, B = r.p1, Half = half });
            }
        }

        private static int BucketKey(int bx, int bz) => (bx * 73856093) ^ (bz * 19349663);

        private static float DistToSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-6f) return Vector2.Distance(p, a);
            float u = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return Vector2.Distance(p, a + ab * u);
        }

        // Inside a vanilla river corridor? `t` is 0 at the corridor edge and 1
        // on the centreline, so callers can feather a hand-off instead of
        // stepping at the boundary.
        public static bool Inside(float wx, float wz, out float t)
        {
            t = 0f;
            if (!s_ready) return false;
            if (!s_index.TryGetValue(BucketKey(Mathf.FloorToInt(wx / BucketSize), Mathf.FloorToInt(wz / BucketSize)),
                                     out int[] ids)) return false;

            var p = new Vector2(wx, wz);
            for (int i = 0; i < ids.Length; i++)
            {
                var s = s_segs[ids[i]];
                float d = DistToSeg(p, s.A, s.B);
                if (d >= s.Half) continue;
                float ti = 1f - d / s.Half;
                if (ti > t) t = ti;
            }
            return t > 0f;
        }

        // How strongly this column should be handed to vanilla's own water: 1
        // deep inside a river corridor, feathering to 0 over the outer third so
        // our surface grades to the sea plane across the junction rather than
        // meeting it at a step.
        public static float HandoffAt(float wx, float wz)
        {
            if (!Inside(wx, wz, out float t)) return 0f;
            return Mathf.Clamp01(t / 0.34f);
        }

        public static float SeaLevel => SEA;
    }
}
