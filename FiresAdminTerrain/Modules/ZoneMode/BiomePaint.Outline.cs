using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1375 (Fire: "draw on the map"; a tool "like our territory tool"): a drawn area is one shape, not a fill of circles: one stroke, one undo, an exact edge.
    // A Circle with an Outline keeps X, Z, R as the circle that bounds it, so everything that only asks "could this touch here?" (zones, cells,
    // refreshes) keeps working; Holds and the distance to the rim ask the polygon. GetBiome reads Holds very often, so a coarse grid answers
    // most points without walking the edges. Immutable once built: any thread reads it.
    internal sealed class PaintOutline
    {
        internal const int MinPoints = 3, MaxPoints = 2000;
        internal const float MaxBoundRadius = 512f;
        private const float GridCell = 16f;
        private const byte Out = 0, In = 1, Edge = 2;

        internal readonly float[] Points;   // x0,z0,x1,z1,...; closed, the first point not repeated
        internal readonly float Area;
        private readonly float m_x0, m_z0;
        private readonly int m_w, m_h;
        private readonly byte[] m_cells;
        private readonly float[] m_rim;     // the rim's distance from each cell's centre

        private PaintOutline(float[] points)
        {
            Points = points;
            int n = points.Length / 2;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue, area = 0f;
            for (int i = 0; i < n; i++)
            {
                float x = points[2 * i], z = points[2 * i + 1];
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
                int j = (i + 1) % n;
                area += x * points[2 * j + 1] - points[2 * j] * z;
            }
            Area = Mathf.Abs(area) * 0.5f;
            m_x0 = minX; m_z0 = minZ;
            m_w = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / GridCell));
            m_h = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / GridCell));
            m_cells = new byte[m_w * m_h];
            m_rim = new float[m_w * m_h];
            float halfDiagonal = GridCell * 0.7072f;
            for (int cz = 0; cz < m_h; cz++)
                for (int cx = 0; cx < m_w; cx++)
                {
                    float x = minX + (cx + 0.5f) * GridCell, z = minZ + (cz + 0.5f) * GridCell;
                    float rim = EdgeDistance(x, z);
                    m_rim[cz * m_w + cx] = rim;
                    m_cells[cz * m_w + cx] = rim <= halfDiagonal ? Edge : InsideExact(x, z) ? In : Out;
                }
        }

        // false + why for anything that isn't a simple polygon within the limits. The circle that bounds it comes back too.
        internal static bool TryCreate(float[] points, out PaintOutline outline, out float cx, out float cz, out float r, out string why)
        {
            outline = null; cx = cz = r = 0f; why = string.Empty;
            if (points == null || points.Length % 2 != 0 || points.Length / 2 < MinPoints || points.Length / 2 > MaxPoints)
            {
                why = $"An outline needs {MinPoints}-{MaxPoints} points.";
                return false;
            }
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (float v in points) if (float.IsNaN(v) || float.IsInfinity(v)) { why = "That outline has a bad point."; return false; }
            int n = points.Length / 2;
            for (int i = 0; i < n; i++)
            {
                minX = Mathf.Min(minX, points[2 * i]); maxX = Mathf.Max(maxX, points[2 * i]);
                minZ = Mathf.Min(minZ, points[2 * i + 1]); maxZ = Mathf.Max(maxZ, points[2 * i + 1]);
            }
            cx = (minX + maxX) * 0.5f; cz = (minZ + maxZ) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                float dx = points[2 * i] - cx, dz = points[2 * i + 1] - cz;
                r = Mathf.Max(r, Mathf.Sqrt(dx * dx + dz * dz));
            }
            r = Mathf.Ceil(r) + 1f;
            if (r > MaxBoundRadius) { why = $"That outline is too big: it has to fit in a circle of {MaxBoundRadius:0} m radius (this one needs {r:0} m). Paint it in parts."; return false; }
            if (Crosses(points)) { why = "That outline crosses itself."; return false; }
            outline = new PaintOutline(points);
            if (outline.Area < 1f) { outline = null; why = "That outline has no area."; return false; }
            return true;
        }

        private static bool Crosses(float[] p)
        {
            int n = p.Length / 2;
            for (int i = 0; i < n; i++)
            {
                int i2 = (i + 1) % n;
                for (int j = i + 1; j < n; j++)
                {
                    int j2 = (j + 1) % n;
                    if (i == j2 || i2 == j) continue;   // neighbours share a point
                    if (SegmentsCross(p[2 * i], p[2 * i + 1], p[2 * i2], p[2 * i2 + 1], p[2 * j], p[2 * j + 1], p[2 * j2], p[2 * j2 + 1])) return true;
                }
            }
            return false;
        }

        private static bool SegmentsCross(float ax, float az, float bx, float bz, float cx, float cz, float dx, float dz)
        {
            float d1 = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            float d2 = (bx - ax) * (dz - az) - (bz - az) * (dx - ax);
            float d3 = (dx - cx) * (az - cz) - (dz - cz) * (ax - cx);
            float d4 = (dx - cx) * (bz - cz) - (dz - cz) * (bx - cx);
            return ((d1 > 0f) != (d2 > 0f)) && ((d3 > 0f) != (d4 > 0f));
        }

        private bool TryCell(float wx, float wz, out int index)
        {
            int cx = Mathf.FloorToInt((wx - m_x0) / GridCell), cz = Mathf.FloorToInt((wz - m_z0) / GridCell);
            index = cz * m_w + cx;
            return cx >= 0 && cz >= 0 && cx < m_w && cz < m_h;
        }

        internal bool Holds(float wx, float wz)
        {
            if (!TryCell(wx, wz, out int index)) return false;
            byte cell = m_cells[index];
            return cell == Edge ? InsideExact(wx, wz) : cell == In;
        }

        // How far inside the rim a point is; 0 or less outside. Only as exact as `enough` asks: past it, the cell's own answer is good.
        internal float Rim(float wx, float wz, float enough)
        {
            if (!TryCell(wx, wz, out int index) || m_cells[index] == Out) return 0f;
            if (m_cells[index] == In && m_rim[index] - GridCell >= enough) return enough;
            if (!InsideExact(wx, wz)) return 0f;
            return EdgeDistance(wx, wz);
        }

        private bool InsideExact(float x, float z)
        {
            var p = Points;
            int n = p.Length / 2;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float zi = p[2 * i + 1], zj = p[2 * j + 1];
                if ((zi > z) != (zj > z) && x < (p[2 * j] - p[2 * i]) * (z - zi) / (zj - zi) + p[2 * i]) inside = !inside;
            }
            return inside;
        }

        private float EdgeDistance(float x, float z)
        {
            var p = Points;
            int n = p.Length / 2;
            float best = float.MaxValue;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float ax = p[2 * j], az = p[2 * j + 1], ex = p[2 * i] - ax, ez = p[2 * i + 1] - az;
                float len = ex * ex + ez * ez;
                float t = len > 0f ? Mathf.Clamp01(((x - ax) * ex + (z - az) * ez) / len) : 0f;
                float dx = x - (ax + t * ex), dz = z - (az + t * ez);
                float d = dx * dx + dz * dz;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }
    }

    internal static partial class BiomePaint
    {
        // Debounced by the map's own tick: the map's colours for custom paint. No custom paint = nothing stamped, as before.
        internal static int StampMap(Color32[] pixels, int size, float pixel, System.Func<Heightmap.Biome, Color32> colourOf)
        {
            var snapshot = s_snapshot;
            bool any = false;
            foreach (var c in snapshot.Circles) if (c.Brush == Brush.Custom) { any = true; break; }
            if (!any) return 0;
            int half = size / 2, stamped = 0;
            var colours = new Dictionary<Heightmap.Biome, Color32>();
            foreach (var c in snapshot.Circles)
            {
                if (c.Brush == Brush.Restore) continue;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((c.X - c.R) / pixel) + half), x1 = Mathf.Min(size - 1, Mathf.CeilToInt((c.X + c.R) / pixel) + half);
                int z0 = Mathf.Max(0, Mathf.FloorToInt((c.Z - c.R) / pixel) + half), z1 = Mathf.Min(size - 1, Mathf.CeilToInt((c.Z + c.R) / pixel) + half);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float wx = (x - half) * pixel + pixel * 0.5f, wz = (z - half) * pixel + pixel * 0.5f;
                        if (!c.Holds(wx, wz) || !TryGround(wx, wz, out var ground)) continue;
                        if (!colours.TryGetValue(ground, out var colour)) colours[ground] = colour = colourOf(ground);
                        pixels[z * size + x] = colour;
                        stamped++;
                    }
            }
            return stamped;
        }
    }
}
