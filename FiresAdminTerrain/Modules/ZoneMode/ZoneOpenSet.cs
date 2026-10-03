using System;
using System.Collections.Generic;

namespace VerdantsAscent.Terrain.Zones
{
    // How a swapped zone's land is moved beyond the whole-zone offset to its source (FAT_CHUNK_BLUEPRINTS_PLAN.md): Lift
    // raises or lowers it as a whole to meet its neighbours (ZoneModeLift, "height middle ground"), and Dx, Dz slide the
    // source square so a location seed's location stands at the zone's centre ("location centring"). Metres.
    internal readonly struct ZoneShift
    {
        internal readonly float Lift, Dx, Dz;

        internal ZoneShift(float lift, float dx, float dz)
        {
            Lift = lift;
            Dx = dx;
            Dz = dz;
        }

        internal bool IsZero => Lift == 0f && Dx == 0f && Dz == 0f;
        internal bool Slides => Dx != 0f || Dz != 0f;
        internal bool IsFinite => !float.IsNaN(Lift) && !float.IsInfinity(Lift) && !float.IsNaN(Dx) && !float.IsInfinity(Dx)
                                  && !float.IsNaN(Dz) && !float.IsInfinity(Dz);
        internal bool SameAs(in ZoneShift other) => Lift == other.Lift && Dx == other.Dx && Dz == other.Dz;
    }

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

        // Swapped zones whose land is moved beyond the whole-zone offset (ZoneShift). A swapped zone not listed shows its
        // source zone as it stands.
        private readonly Dictionary<int, ZoneShift> _shifts;

        internal int Version { get; }
        internal int OpenCount { get; }
        internal int SourceCount => _sources?.Count ?? 0;

        // Shifted zones, and those among them raised or lowered: the gate's fast exits.
        internal int ShiftCount => _shifts?.Count ?? 0;
        internal int LiftCount { get; }

        internal static readonly ZoneOpenSet Empty = new ZoneOpenSet(new ulong[WordCount], 0, 0, null, null);

        private ZoneOpenSet(ulong[] bits, int version, int openCount, Dictionary<int, int> sources, Dictionary<int, ZoneShift> shifts)
        {
            _bits = bits;
            Version = version;
            OpenCount = openCount;
            _sources = sources;
            _shifts = shifts;
            if (shifts != null) foreach (var shift in shifts.Values) if (shift.Lift != 0f) LiftCount++;
        }

        internal static ZoneOpenSet Build(IEnumerable<Vector2s> zones, int version, IEnumerable<KeyValuePair<Vector2s, Vector2s>> sources = null,
            IEnumerable<KeyValuePair<Vector2s, ZoneShift>> shifts = null)
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
            var indexed = IndexSources(sources);
            return new ZoneOpenSet(bits, version, count, indexed, IndexShifts(shifts, indexed));
        }

        internal static ZoneOpenSet FromWords(ulong[] words, int version, IEnumerable<KeyValuePair<Vector2s, Vector2s>> sources = null,
            IEnumerable<KeyValuePair<Vector2s, ZoneShift>> shifts = null)
        {
            if (words == null || words.Length != WordCount) return null;
            int count = 0;
            foreach (ulong word in words) count += PopCount(word);
            var indexed = IndexSources(sources);
            return new ZoneOpenSet((ulong[])words.Clone(), version, count, indexed, IndexShifts(shifts, indexed));
        }

        // Only a swapped zone carries a shift.
        private static Dictionary<int, ZoneShift> IndexShifts(IEnumerable<KeyValuePair<Vector2s, ZoneShift>> shifts, Dictionary<int, int> sources)
        {
            if (shifts == null || sources == null) return null;
            Dictionary<int, ZoneShift> map = null;
            foreach (var pair in shifts)
            {
                if (pair.Value.IsZero || !pair.Value.IsFinite) continue;
                if (!TryIndex(pair.Key.x, pair.Key.y, out int zone) || !sources.ContainsKey(zone)) continue;
                (map ?? (map = new Dictionary<int, ZoneShift>()))[zone] = pair.Value;
            }
            return map;
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

        internal bool TryGetSource(int zx, int zy, out int sx, out int sy, out ZoneShift shift)
        {
            shift = default;
            if (!TryGetSource(zx, zy, out sx, out sy)) return false;
            if (_shifts != null && TryIndex(zx, zy, out int index)) _shifts.TryGetValue(index, out shift);
            return true;
        }

        internal ZoneShift ShiftOf(int zx, int zy)
            => _shifts != null && TryIndex(zx, zy, out int index) && _shifts.TryGetValue(index, out var shift) ? shift : default;

        internal float LiftOf(int zx, int zy) => ShiftOf(zx, zy).Lift;

        internal IEnumerable<KeyValuePair<Vector2s, ZoneShift>> Shifts()
        {
            if (_shifts == null) yield break;
            foreach (var pair in _shifts)
                yield return new KeyValuePair<Vector2s, ZoneShift>(new Vector2s(pair.Key % Span - ZoneExtent, pair.Key / Span - ZoneExtent), pair.Value);
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
                if (_shifts != null)
                {
                    uint shifts = 0u;
                    foreach (var pair in _shifts)
                        shifts += ((uint)pair.Key * 2654435761u) ^ Bits(pair.Value.Lift) ^ (Bits(pair.Value.Dx) * 16777619u) ^ (Bits(pair.Value.Dz) * 2246822519u);
                    hash = (hash ^ shifts) * 16777619u;
                }
                return hash;
            }
        }

        private static uint Bits(float value) => (uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

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
