using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // The shore around a lifted zone's sea (ZoneModeGate.TryLiftedSea), for the wet-shore paint
    // (VoxelZoneStreamer.PaintWet01): its water is swamp water on swamp land and a lake elsewhere, so it wears the shore
    // band those have. Per lifted zone, a grid over the zone and a margin as wide as the paint reaches holds each point's
    // distance to that sea's edge (0 on the water). It is built on first use and again after any zone opens, since a
    // neighbour that continues the zone's land takes the bank, and the water with it, off their shared edge.
    internal static class ZoneModeLiftShore
    {
        internal const float Reach = 12f;
        private const float Cell = 2f;
        private const float HalfCell = Cell * 0.5f;
        private const float FarDistance = Reach + Cell;

        private static readonly int Margin = Mathf.CeilToInt(Reach / Cell);
        private static readonly int ZoneCells = Mathf.RoundToInt(ZoneOpenSet.ZoneSize / Cell);
        private static readonly int Span = ZoneCells + 2 * Margin + 1;
        private static readonly Dictionary<Vector2s, Field> s_fields = new Dictionary<Vector2s, Field>();

        private sealed class Field
        {
            internal int Version;
            internal float X0, Z0, Level;
            internal bool AnyWet;
            internal float[] Distance;
            internal byte[] Class;
        }

        // The lifted sea nearest (wx, wz) within `reach` of its edge: its class, its level and the distance to its edge.
        internal static bool TryNearest(float wx, float wz, float reach, out byte cls, out float level, out float dist)
        {
            cls = FiresWorldGenBase.WC_NONE;
            level = float.MinValue;
            dist = float.MaxValue;
            ZoneOpenSet.ZoneOf(wx, wz, out int zx, out int zy);
            float half = ZoneOpenSet.ZoneSize * 0.5f;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    float cx = (zx + dx) * ZoneOpenSet.ZoneSize, cz = (zy + dy) * ZoneOpenSet.ZoneSize;
                    float outX = Mathf.Max(0f, Mathf.Abs(wx - cx) - half), outZ = Mathf.Max(0f, Mathf.Abs(wz - cz) - half);
                    if (outX * outX + outZ * outZ > reach * reach || ZoneModeGate.LiftAt(cx, cz) <= 0f) continue;
                    if (!TrySample(new Vector2s(zx + dx, zy + dy), wx, wz, out float d, out byte c, out float l) || d >= dist) continue;
                    dist = d;
                    cls = c;
                    level = l;
                }
            return dist <= reach;
        }

        private static bool TrySample(Vector2s zone, float wx, float wz, out float dist, out byte cls, out float level)
        {
            dist = float.MaxValue;
            cls = FiresWorldGenBase.WC_NONE;
            level = float.MinValue;
            Field field;
            lock (s_fields)
            {
                if (!s_fields.TryGetValue(zone, out field) || field.Version != ZoneModeRegistry.Version)
                    s_fields[zone] = field = Build(zone);
            }
            if (!field.AnyWet) return false;
            float u = (wx - field.X0) / Cell, v = (wz - field.Z0) / Cell;
            int i = Mathf.FloorToInt(u), j = Mathf.FloorToInt(v);
            if (i < 0 || j < 0 || i >= Span - 1 || j >= Span - 1) return false;
            float fu = u - i, fv = v - j;
            int at = j * Span + i;
            var d = field.Distance;
            dist = Mathf.Lerp(Mathf.Lerp(d[at], d[at + 1], fu), Mathf.Lerp(d[at + Span], d[at + Span + 1], fu), fv);
            cls = field.Class[Mathf.RoundToInt(v) * Span + Mathf.RoundToInt(u)];
            level = field.Level;
            return cls != FiresWorldGenBase.WC_NONE;
        }

        private static Field Build(Vector2s zone)
        {
            float half = ZoneOpenSet.ZoneSize * 0.5f;
            float minX = zone.x * ZoneOpenSet.ZoneSize - half, minZ = zone.y * ZoneOpenSet.ZoneSize - half;
            var field = new Field
            {
                Version = ZoneModeRegistry.Version,
                X0 = minX - Margin * Cell,
                Z0 = minZ - Margin * Cell,
                Level = float.MinValue,
                Distance = new float[Span * Span],
                Class = new byte[Span * Span],
            };
            var wet = new List<KeyValuePair<int, byte>>();
            var generator = WorldGenerator.instance;
            for (int j = Margin; j < Margin + ZoneCells; j++)
                for (int i = Margin; i < Margin + ZoneCells; i++)
                {
                    float x = field.X0 + i * Cell, z = field.Z0 + j * Cell;
                    if (!ZoneModeGate.TryLiftedSea(x, z, out float seaLevel, out _)) continue;
                    field.Level = seaLevel;
                    wet.Add(new KeyValuePair<int, byte>(j * Span + i, generator != null && generator.GetBiome(x, z) == Heightmap.Biome.Swamp
                        ? FiresWorldGenBase.WC_SWAMP
                        : FiresWorldGenBase.WC_LAKE));
                }
            field.AnyWet = wet.Count > 0;
            Spread(wet, field.Distance, field.Class);
            return field;
        }

        // Each grid point's distance to the nearest wet point's edge (half a cell out from it; 0 on the water) and that
        // point's class, within the margin; FarDistance beyond it.
        private static void Spread(List<KeyValuePair<int, byte>> wet, float[] distance, byte[] cls)
        {
            for (int at = 0; at < distance.Length; at++)
            {
                distance[at] = FarDistance;
                cls[at] = FiresWorldGenBase.WC_NONE;
            }
            foreach (var cell in wet)
            {
                int si = cell.Key % Span, sj = cell.Key / Span;
                for (int j = Mathf.Max(0, sj - Margin); j <= Mathf.Min(Span - 1, sj + Margin); j++)
                    for (int i = Mathf.Max(0, si - Margin); i <= Mathf.Min(Span - 1, si + Margin); i++)
                    {
                        int at = j * Span + i;
                        float d = at == cell.Key ? 0f : Mathf.Max(0f, Mathf.Sqrt((i - si) * (i - si) + (j - sj) * (j - sj)) * Cell - HalfCell);
                        if (d >= distance[at]) continue;
                        distance[at] = d;
                        cls[at] = cell.Value;
                    }
            }
        }

        internal static void Reset()
        {
            lock (s_fields) s_fields.Clear();
        }
    }
}
