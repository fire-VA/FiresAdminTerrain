using System;
using System.Collections.Generic;

namespace VerdantsAscent.Terrain.Zones
{
    // Which 64 m zones are open, as an immutable bitset. The terrain gate reads it from the
    // HeightmapBuilder worker and the main thread at the same time, so a change never mutates
    // a snapshot: it builds a new one and swaps the reference.
    internal sealed class ZoneOpenSet
    {
        internal const int ZoneExtent = 170;
        internal const float ZoneSize = 64f;
        private const double ZoneSizeDouble = 64.0;
        private const double HalfZoneDouble = 32.0;
        private const float HalfZone = 32f;
        private const int Span = ZoneExtent * 2 + 1;
        internal const int WordCount = (Span * Span + 63) / 64;

        private readonly ulong[] _bits;

        // Open zones whose land comes from another zone of the world (a chunk seed of a different biome):
        // zone index -> source zone index. Zones not listed show their own land.
        private readonly Dictionary<int, int> _sources;

        internal int Version { get; }
        internal int OpenCount { get; }
        internal int SourceCount => _sources?.Count ?? 0;

        internal static readonly ZoneOpenSet Empty = new ZoneOpenSet(new ulong[WordCount], 0, 0, null);

        private ZoneOpenSet(ulong[] bits, int version, int openCount, Dictionary<int, int> sources)
        {
            _bits = bits;
            Version = version;
            OpenCount = openCount;
            _sources = sources;
        }

        internal static ZoneOpenSet Build(IEnumerable<Vector2s> zones, int version, IEnumerable<KeyValuePair<Vector2s, Vector2s>> sources = null)
        {
            var bits = new ulong[WordCount];
            int count = 0;
            foreach (var zone in zones)
            {
                if (!TryIndex(zone.x, zone.y, out int index)) continue;
                ulong mask = 1UL << (index & 63);
                if ((bits[index >> 6] & mask) != 0) continue;
                bits[index >> 6] |= mask;
                count++;
            }
            return new ZoneOpenSet(bits, version, count, IndexSources(sources));
        }

        internal static ZoneOpenSet FromWords(ulong[] words, int version, IEnumerable<KeyValuePair<Vector2s, Vector2s>> sources = null)
        {
            if (words == null || words.Length != WordCount) return null;
            int count = 0;
            foreach (ulong word in words) count += PopCount(word);
            return new ZoneOpenSet((ulong[])words.Clone(), version, count, IndexSources(sources));
        }

        private static Dictionary<int, int> IndexSources(IEnumerable<KeyValuePair<Vector2s, Vector2s>> sources)
        {
            if (sources == null) return null;
            Dictionary<int, int> map = null;
            foreach (var pair in sources)
            {
                if (pair.Key.x == pair.Value.x && pair.Key.y == pair.Value.y) continue;
                if (!TryIndex(pair.Key.x, pair.Key.y, out int zone) || !TryIndex(pair.Value.x, pair.Value.y, out int source)) continue;
                (map ?? (map = new Dictionary<int, int>()))[zone] = source;
            }
            return map;
        }

        internal bool TryGetSource(int zx, int zy, out int sx, out int sy)
        {
            sx = zx;
            sy = zy;
            if (_sources == null || !TryIndex(zx, zy, out int index) || !_sources.TryGetValue(index, out int source)) return false;
            sx = source % Span - ZoneExtent;
            sy = source / Span - ZoneExtent;
            return true;
        }

        internal IEnumerable<KeyValuePair<Vector2s, Vector2s>> Sources()
        {
            if (_sources == null) yield break;
            foreach (var pair in _sources)
                yield return new KeyValuePair<Vector2s, Vector2s>(
                    new Vector2s(pair.Key % Span - ZoneExtent, pair.Key / Span - ZoneExtent),
                    new Vector2s(pair.Value % Span - ZoneExtent, pair.Value / Span - ZoneExtent));
        }

        // The zone ZoneSystem.GetZone puts a point in.
        internal static void ZoneOf(float x, float z, out int zx, out int zy)
        {
            zx = (int)Math.Floor(ZoneCoordinate(x));
            zy = (int)Math.Floor(ZoneCoordinate(z));
        }

        internal ulong[] CopyWords() => (ulong[])_bits.Clone();

        internal static bool InRange(int zx, int zy)
            => zx >= -ZoneExtent && zx <= ZoneExtent && zy >= -ZoneExtent && zy <= ZoneExtent;

        private static bool TryIndex(int zx, int zy, out int index)
        {
            if (!InRange(zx, zy)) { index = -1; return false; }
            index = (zy + ZoneExtent) * Span + (zx + ZoneExtent);
            return true;
        }

        internal bool IsOpen(int zx, int zy)
            => TryIndex(zx, zy, out int index) && (_bits[index >> 6] & (1UL << (index & 63))) != 0;

        internal bool IsOpen(Vector2s zone) => IsOpen(zone.x, zone.y);

        // Mirrors ZoneSystem.GetZone exactly (double sum, float cast, floor).
        private static float ZoneCoordinate(float worldCoordinate)
            => (float)((worldCoordinate + HalfZoneDouble) / ZoneSizeDouble);

        // Real terrain shows inside an open zone AND on its edges. Heightmaps share their border
        // vertices, so a closed neighbour must report the real height along a shared edge or the
        // two meshes split apart; the corner points are shared the same way.
        internal bool IsRealAt(float x, float z)
        {
            float fx = ZoneCoordinate(x), fz = ZoneCoordinate(z);
            int zx = (int)Math.Floor(fx), zy = (int)Math.Floor(fz);
            if (IsOpen(zx, zy)) return true;
            bool onWestEdge = fx == zx, onSouthEdge = fz == zy;
            if (onWestEdge && IsOpen(zx - 1, zy)) return true;
            if (onSouthEdge && IsOpen(zx, zy - 1)) return true;
            return onWestEdge && onSouthEdge && IsOpen(zx - 1, zy - 1);
        }

        // Horizontal distance from a point to the nearest open zone square, capped at `cap`.
        // Only the 3x3 neighbourhood is searched, so `cap` must not exceed one zone.
        internal float DistanceToOpen(float x, float z, float cap)
        {
            int zx = (int)Math.Floor(ZoneCoordinate(x)), zy = (int)Math.Floor(ZoneCoordinate(z));
            float best = cap;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = zx + dx, ny = zy + dy;
                    if (!IsOpen(nx, ny)) continue;
                    float outsideX = Math.Max(0f, Math.Abs(x - nx * ZoneSize) - HalfZone);
                    float outsideZ = Math.Max(0f, Math.Abs(z - ny * ZoneSize) - HalfZone);
                    float distance = (float)Math.Sqrt(outsideX * outsideX + outsideZ * outsideZ);
                    if (distance < best) best = distance;
                }
            }
            return best;
        }

        // True when any closed zone intersects the disc. Used for edits that reach across an edge.
        internal bool DiscTouchesClosed(float x, float z, float radius)
        {
            int minX = (int)Math.Floor(ZoneCoordinate(x - radius)), maxX = (int)Math.Floor(ZoneCoordinate(x + radius));
            int minZ = (int)Math.Floor(ZoneCoordinate(z - radius)), maxZ = (int)Math.Floor(ZoneCoordinate(z + radius));
            for (int zy = minZ; zy <= maxZ; zy++)
            {
                for (int zx = minX; zx <= maxX; zx++)
                {
                    if (IsOpen(zx, zy)) continue;
                    float outsideX = Math.Max(0f, Math.Abs(x - zx * ZoneSize) - HalfZone);
                    float outsideZ = Math.Max(0f, Math.Abs(z - zy * ZoneSize) - HalfZone);
                    if (outsideX * outsideX + outsideZ * outsideZ < radius * radius) return true;
                }
            }
            return false;
        }

        internal IEnumerable<Vector2s> OpenZones()
        {
            for (int word = 0; word < _bits.Length; word++)
            {
                ulong bits = _bits[word];
                while (bits != 0)
                {
                    int bit = TrailingZeroCount(bits);
                    bits &= bits - 1;
                    int index = (word << 6) + bit;
                    yield return new Vector2s(index % Span - ZoneExtent, index / Span - ZoneExtent);
                }
            }
        }

        internal uint ContentHash()
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (ulong word in _bits)
                {
                    hash = (hash ^ (uint)word) * 16777619u;
                    hash = (hash ^ (uint)(word >> 32)) * 16777619u;
                }
                if (_sources != null)
                {
                    uint sources = 0u;
                    foreach (var pair in _sources) sources += ((uint)pair.Key * 2654435761u) ^ (uint)pair.Value;
                    hash = (hash ^ sources) * 16777619u;
                }
                return hash;
            }
        }

        private static int PopCount(ulong value)
        {
            int count = 0;
            while (value != 0) { value &= value - 1; count++; }
            return count;
        }

        private static int TrailingZeroCount(ulong value)
        {
            int count = 0;
            while ((value & 1UL) == 0) { value >>= 1; count++; }
            return count;
        }
    }
}
