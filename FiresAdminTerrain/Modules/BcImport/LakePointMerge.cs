using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    // Vanilla WorldGenerator.MergePoints, which FindLakes runs over every low grid point, is quadratic: it takes each point off
    // the FRONT of a List (shifting the rest) and then rescans every remaining point for the closest one after each merge.
    // A world with a lot of sea has thousands of such points: 247 ms of every join and server start on CratersAshlands (R6,
    // PregenerationTiming). This is the same algorithm over the same list order, with a grid of range-sized cells so a search
    // only looks at the 3x3 cells a point can reach. The distances, the equality skip, the "strictly closer, so the lowest
    // index wins a tie" rule, the midpoint merge and the swap-removal are vanilla's own, so the lakes come out identical.
    [HarmonyPatch(typeof(WorldGenerator), "MergePoints")]
    internal static class LakePointMerge
    {
        private const float NoneCloser = 99999f;
        private const int NeighbourReach = 1;

        private static bool Prefix(List<Vector2> points, float range, ref List<Vector2> __result)
        {
            if (range <= 0f) return true;
            __result = Merge(points, range);
            return false;
        }

        internal static List<Vector2> Merge(List<Vector2> points, float range)
        {
            int count = points.Count;
            var positions = new Vector2[count];
            points.CopyTo(positions);
            var grid = new Dictionary<long, List<int>>();
            for (int at = 0; at < count; at++) CellOf(grid, positions[at], range, true).Add(at);

            var merged = new List<Vector2>();
            int head = 0, tail = count;
            while (tail > head)
            {
                Vector2 p = positions[head];
                CellOf(grid, p, range, false).Remove(head);
                head++;
                while (tail > head)
                {
                    int closest = FindClosest(grid, positions, head, tail, p, range);
                    if (closest < 0) break;
                    p = (p + positions[closest]) * 0.5f;
                    CellOf(grid, positions[closest], range, false).Remove(closest);
                    int last = tail - 1;
                    if (closest != last)
                    {
                        var moved = CellOf(grid, positions[last], range, false);
                        moved[moved.IndexOf(last)] = closest;
                        positions[closest] = positions[last];
                    }
                    tail--;
                }
                merged.Add(p);
            }
            points.Clear();
            return merged;
        }

        // Vanilla's FindClosest over the points a range-sized neighbourhood can hold, compared by list index so a tie goes to
        // the same point vanilla's front-to-back scan would pick.
        private static int FindClosest(Dictionary<long, List<int>> grid, Vector2[] positions, int head, int tail, Vector2 p, float range)
        {
            int best = -1;
            float bestDistance = NoneCloser;
            int cx = Cell(p.x, range), cy = Cell(p.y, range);
            for (int dy = -NeighbourReach; dy <= NeighbourReach; dy++)
                for (int dx = -NeighbourReach; dx <= NeighbourReach; dx++)
                {
                    if (!grid.TryGetValue(Key(cx + dx, cy + dy), out var cell)) continue;
                    foreach (int at in cell)
                    {
                        if (at < head || at >= tail || positions[at] == p) continue;
                        float distance = Vector2.Distance(p, positions[at]);
                        if (distance >= range) continue;
                        if (distance < bestDistance || (distance == bestDistance && at < best))
                        {
                            best = at;
                            bestDistance = distance;
                        }
                    }
                }
            return best;
        }

        private static List<int> CellOf(Dictionary<long, List<int>> grid, Vector2 point, float range, bool create)
        {
            long key = Key(Cell(point.x, range), Cell(point.y, range));
            if (grid.TryGetValue(key, out var cell)) return cell;
            cell = new List<int>();
            if (create) grid[key] = cell;
            return cell;
        }

        private static int Cell(float value, float range) => Mathf.FloorToInt(value / range);

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
    }
}
