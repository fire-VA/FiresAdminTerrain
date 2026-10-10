using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Pond and lake BOWLS for the hydrology network (Tools/FAT_RIVER_CARVE_SPEC.md).
    //
    // The solve works on a 20m grid, and a grid that coarse cannot hold the
    // shape of a pond: the first pass used each basin's raw cell footprint, and
    // with an average of 2.3 cells per basin that produced 20x46m slivers with
    // hard diagonal edges — the "broken mesh" ponds.
    //
    // So a basin is only a SITE. Each one becomes a round bowl of its own:
    //   * centre  — the basin's cell centroid
    //   * radius  — from the basin's area, with a floor so a one-cell site is
    //               still a believable pond rather than a single quad
    //   * level   — the lowest SPILL point on its rim, less a freeboard, so the
    //               fill sits just under the banks ("level across the banks")
    //   * floor   — level minus the pond/lake depth from the water style table
    // The carve tapers the floor back up to the caller's real 1m ground at the
    // rim, so the bowl is watertight at any resolution, and the pour placement
    // then fills it exactly like any other contained water.
    //
    // Deterministic from the solve's arrays; nothing persists.
    internal static class VoxelLakeBasins
    {
        private const float SEA = 30f;
        private const float SeaFloorMin = 30.1f;
        private const float MinLevelAboveSea = 1f;   // a waterline notch is not a pond
        private const float MinBasinDepth = 0.6f;
        private const float MinRadius = 11f;
        // 80m = a 160m lake, and it still fits inside one wrap radius, so every
        // zone the bowl touches is converted while the player stands in it. The
        // streamer starts ONE zone per tick, so a wider bowl spends longer with
        // a live carve frontier running through it.
        private const float MaxRadius = 80f;
        private const float Freeboard = 0.35f;       // fill sits this far under the spill point
        private const int PondCellLimit = 6;         // cells at/below this use the pond depth style
        private const float BucketSize = 64f;

        private struct Bowl
        {
            public float X, Z;
            public float Radius;
            public float Level;
            public float Depth;
        }

        private static Bowl[] s_bowls = new Bowl[0];
        private static Dictionary<int, int[]> s_index = new Dictionary<int, int[]>();
        private static HashSet<Vector2s> s_zones = new HashSet<Vector2s>();
        private static volatile bool s_ready;

        public static bool Ready => s_ready;
        public static int BasinCount { get; private set; }
        public static int BasinCells { get; private set; }
        public static float LevelLo { get; private set; }
        public static float LevelHi { get; private set; }
        public static float RadiusLo { get; private set; }
        public static float RadiusHi { get; private set; }
        public static int GuardedBowls { get; private set; }
        public static int SkippedBowls { get; private set; }

        public static bool TouchesZone(Vector2s zone) => s_ready && s_zones.Contains(zone);

        public static void Clear()
        {
            s_ready = false;
            s_bowls = new Bowl[0];
            s_index = new Dictionary<int, int[]>();
            s_zones = new HashSet<Vector2s>();
            BasinCount = 0;
            BasinCells = 0;
            GuardedBowls = SkippedBowls = 0;
            LevelLo = LevelHi = RadiusLo = RadiusHi = 0f;
        }

        public static void Build(float[] ground, float[] still, int res, float cell, float radius, float maxCut,
            List<HydrologyGuards.Disc> guards)
        {
            var bowls = new List<Bowl>(256);
            var zones = new HashSet<Vector2s>();
            var seen = new bool[res * res];
            var stack = new Stack<int>();
            var cells = new List<int>(256);
            int[] dx4 = { -1, 1, 0, 0 };
            int[] dz4 = { 0, 0, -1, 1 };
            int basinCells = 0, guarded = 0, skipped = 0;
            float lLo = float.MaxValue, lHi = float.MinValue, rLo = float.MaxValue, rHi = float.MinValue;

            bool IsBasin(int i) => still[i] > float.MinValue + 1f
                && still[i] >= SEA + MinLevelAboveSea
                && still[i] - ground[i] >= MinBasinDepth;

            float minHold = FiresVoxelConfig.WaterMinChannelDepth != null
                ? FiresVoxelConfig.WaterMinChannelDepth.Value : 1.5f;
            float relief = FiresVoxelConfig.CoarseBankRelief;

            for (int start = 0; start < seen.Length; start++)
            {
                if (seen[start] || !IsBasin(start)) continue;
                cells.Clear();
                stack.Push(start);
                seen[start] = true;

                // Flood the connected depression, and while doing it find the
                // lowest point on its rim — the SPILL point the water levels
                // to — and whether the sea is one of its neighbours, in which
                // case the depression drains and is not a pond at all.
                float spill = float.MaxValue;
                float deepest = float.MaxValue;
                bool touchesSea = false;
                double sumX = 0.0, sumZ = 0.0;

                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    cells.Add(cur);
                    int cx = cur % res, cz = cur / res;
                    sumX += cx;
                    sumZ += cz;
                    if (ground[cur] < deepest) deepest = ground[cur];
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = cx + dx4[k], nz = cz + dz4[k];
                        if (nx < 0 || nz < 0 || nx >= res || nz >= res) continue;
                        int ni = nz * res + nx;
                        if (ground[ni] <= SEA) touchesSea = true;
                        if (IsBasin(ni))
                        {
                            if (seen[ni]) continue;
                            seen[ni] = true;
                            stack.Push(ni);
                        }
                        else if (ground[ni] < spill) spill = ground[ni];   // rim cell
                    }
                }

                if (touchesSea || spill == float.MaxValue) continue;

                // Freeboard plus the coarse-grid allowance: the spill point came
                // off a 20m sample, so it can sit above the real 1m rim.
                float level = Mathf.Min(spill, still[start]) - Freeboard - relief;
                if (level < SEA + MinLevelAboveSea) continue;
                if (level - deepest < MinBasinDepth) continue;

                float wx = -radius + ((float)(sumX / cells.Count) + 0.5f) * cell;
                float wz = -radius + ((float)(sumZ / cells.Count) + 0.5f) * cell;

                // The basin's OWN biome picks its style — depth, shore reach
                // and paint weight all differ, and a plains lake read as a
                // meadows one until now (WaterStyleFor(0) was hardcoded).
                var style = FiresWorldGenBase.WaterStyleFor(GenWaterField.BiomeCodeAt(wx, wz));
                float area = cells.Count * cell * cell;
                float r = Mathf.Clamp(Mathf.Sqrt(area / Mathf.PI) * 1.25f, MinRadius, MaxRadius);
                // A bowl never reaches the start location or the start cave: it is pulled back to stop short of them,
                // and dropped when that leaves less than a pond.
                float allowed = HydrologyGuards.BowlRadius(wx, wz, r, guards);
                if (allowed < MinRadius) { skipped++; continue; }
                if (allowed < r) { r = allowed; guarded++; }
                float depth = cells.Count <= PondCellLimit
                    ? Mathf.Max(1.4f, style.PondDepth)
                    : Mathf.Max(2.5f, style.LakeDepth);
                // Never shallower than the depth a channel is guaranteed to
                // hold, plus a metre: a bowl at the style minimum reads as a
                // damp dent rather than water you can swim in.
                depth = Mathf.Max(depth, minHold + 1f);
                depth = Mathf.Min(Mathf.Min(depth, maxCut), level - SeaFloorMin);
                if (depth < 1f) continue;

                bowls.Add(new Bowl { X = wx, Z = wz, Radius = r, Level = level, Depth = depth });
                basinCells += cells.Count;
                if (level < lLo) lLo = level;
                if (level > lHi) lHi = level;
                if (r < rLo) rLo = r;
                if (r > rHi) rHi = r;

                // EVERY zone the bowl reaches must convert, or the carve stops
                // at the boundary of the last converted zone and leaves a
                // straight 64m-aligned wall through the lake — the terrain tear
                // at a carve edge. Walk the zone INDICES across the bowl's
                // bounding box (plus a margin for the query's own reach): the
                // old 32m sampling from the centre covered only to the last
                // whole step, so at r=110 the outer 28m band was never
                // registered, which is why only the large lakes tore.
                const float ZoneMargin = 8f;
                int zx0 = Mathf.FloorToInt((wx - r - ZoneMargin + 32f) / 64f);
                int zx1 = Mathf.FloorToInt((wx + r + ZoneMargin + 32f) / 64f);
                int zz0 = Mathf.FloorToInt((wz - r - ZoneMargin + 32f) / 64f);
                int zz1 = Mathf.FloorToInt((wz + r + ZoneMargin + 32f) / 64f);
                for (int zz = zz0; zz <= zz1; zz++)
                    for (int zx = zx0; zx <= zx1; zx++)
                        zones.Add(new Vector2s(zx, zz));
            }

            var index = new Dictionary<int, List<int>>(bowls.Count * 2);
            for (int b = 0; b < bowls.Count; b++)
            {
                var bw = bowls[b];
                int bx0 = Mathf.FloorToInt((bw.X - bw.Radius) / BucketSize);
                int bx1 = Mathf.FloorToInt((bw.X + bw.Radius) / BucketSize);
                int bz0 = Mathf.FloorToInt((bw.Z - bw.Radius) / BucketSize);
                int bz1 = Mathf.FloorToInt((bw.Z + bw.Radius) / BucketSize);
                for (int bz = bz0; bz <= bz1; bz++)
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        int key = BucketKey(bx, bz);
                        if (!index.TryGetValue(key, out var list)) index[key] = list = new List<int>(4);
                        list.Add(b);
                    }
            }
            var flat = new Dictionary<int, int[]>(index.Count);
            foreach (var kv in index) flat[kv.Key] = kv.Value.ToArray();

            s_bowls = bowls.ToArray();
            s_index = flat;
            s_zones = zones;
            BasinCount = bowls.Count;
            BasinCells = basinCells;
            GuardedBowls = guarded;
            SkippedBowls = skipped;
            LevelLo = bowls.Count > 0 ? lLo : 0f;
            LevelHi = bowls.Count > 0 ? lHi : 0f;
            RadiusLo = bowls.Count > 0 ? rLo : 0f;
            RadiusHi = bowls.Count > 0 ? rHi : 0f;
            s_ready = bowls.Count > 0;
        }

        private static int BucketKey(int bx, int bz) => (bx * 73856093) ^ (bz * 19349663);

        // The bowl covering a point, highest water level wins where two overlap.
        private static bool Find(float wx, float wz, out Bowl bowl, out float t)
        {
            bowl = default;
            t = 1f;
            if (!s_ready) return false;
            if (!s_index.TryGetValue(BucketKey(Mathf.FloorToInt(wx / BucketSize), Mathf.FloorToInt(wz / BucketSize)),
                                     out int[] ids)) return false;

            bool hit = false;
            for (int i = 0; i < ids.Length; i++)
            {
                var b = s_bowls[ids[i]];
                float dx = wx - b.X, dz = wz - b.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 >= b.Radius * b.Radius) continue;
                if (hit && b.Level <= bowl.Level) continue;
                bowl = b;
                t = Mathf.Sqrt(d2) / b.Radius;
                hit = true;
            }
            return hit;
        }

        // Radial bowl profile: full depth at the centre, zero at the rim, with a
        // smoothstep between so the floor leaves the bank tangentially instead
        // of stepping down.
        private static float DepthAt(in Bowl b, float t)
        {
            float s = Mathf.Clamp01(t);
            return b.Depth * (1f - s * s * (3f - 2f * s));
        }

        public static bool TryLevelAt(float wx, float wz, out float level)
        {
            level = float.MinValue;
            if (!Find(wx, wz, out Bowl b, out _)) return false;
            level = b.Level;
            return true;
        }

        // The nearest bowl's water level and the distance from this point to its
        // WET EDGE — 0 inside, metres outside. The shore paint integrates over
        // that distance, so answering only inside the bowl (as Find does) left
        // every lake with no beach band around it at all.
        public static bool TryNearestWet(float wx, float wz, float reach, out float level, out float dist)
        {
            level = float.MinValue;
            dist = float.MaxValue;
            if (!s_ready) return false;

            int bx0 = Mathf.FloorToInt((wx - reach) / BucketSize);
            int bx1 = Mathf.FloorToInt((wx + reach) / BucketSize);
            int bz0 = Mathf.FloorToInt((wz - reach) / BucketSize);
            int bz1 = Mathf.FloorToInt((wz + reach) / BucketSize);
            for (int bz = bz0; bz <= bz1; bz++)
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    if (!s_index.TryGetValue(BucketKey(bx, bz), out int[] ids)) continue;
                    for (int i = 0; i < ids.Length; i++)
                    {
                        var b = s_bowls[ids[i]];
                        float dx = wx - b.X, dz = wz - b.Z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz) - b.Radius;
                        if (d < 0f) d = 0f;
                        if (d > reach || d > dist) continue;
                        dist = d;
                        level = b.Level;
                    }
                }
            return level > float.MinValue + 1f;
        }

        // Level plus the water depth the carved bowl provides here, which is
        // what the pour placement's containment test needs to see.
        public static bool TryWaterAt(float wx, float wz, out float level, out float waterDepth)
        {
            level = float.MinValue;
            waterDepth = 0f;
            if (!Find(wx, wz, out Bowl b, out float t)) return false;
            level = b.Level;
            waterDepth = DepthAt(b, t);
            return waterDepth > 0f;
        }

        // The bowl floor at a column: full depth below the fill level in the
        // middle, rising back to the fill level at the rim — so the cut simply
        // stops where the real ground crosses the waterline and the bank is
        // left standing. Never below the ocean plane (the basin carries its own
        // surface, so one sheet owns the column).
        public static bool TryCarveAt(float wx, float wz, float groundY, out float carvedY, out float level)
        {
            carvedY = groundY;
            level = float.MinValue;
            // A lifted chunk (ZoneModeLift) carries its bowls up with its ground: the level and floor are its source's.
            Zones.ZoneModeGate.TranslateToSource(ref wx, ref wz, out float lift);
            if (!Find(wx, wz, out Bowl b, out float t)) return false;
            level = b.Level + lift;

            float target = b.Level - DepthAt(b, t);
            if (target < SeaFloorMin) target = SeaFloorMin;
            target += lift;
            if (target >= groundY) return false;
            carvedY = target;
            return true;
        }
    }
}
