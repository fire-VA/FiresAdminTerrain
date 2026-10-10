using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Biome swaps: a chunk seed raises its own biome's land.
    //   - A zone whose planned land is the seed's biome opens as itself, so a world can be rebuilt faithfully.
    //   - Any other zone takes the land of a source zone of that biome, copied whole through the terrain gate:
    //     heights, biome, plants, spawns, weather. Where chunks of different land meet, the ground steps as a
    //     straight cliff.
    //   - A zone beside open land swapped to the same biome continues that land's source, so a run of mountain
    //     seeds grows one mountainside; otherwise the source is the whole-biome zone nearest a point hashed from
    //     the world seed and the zone.
    // The choice is a pure function of the world, the open set and its sources, so a client holding a seed
    // previews exactly the land the server will raise.
    internal static class ZoneModeSwaps
    {
        private const int Spread = 40;
        private const float WorldRadius = 9500f;
        private const float SampleStep = 32f;

        private sealed class Index
        {
            internal readonly Dictionary<Heightmap.Biome, Vector2s[]> Whole = new Dictionary<Heightmap.Biome, Vector2s[]>();
        }

        private static Index s_index;
        private static WorldGenerator s_indexedFor;
        private static bool s_building;

        internal static bool Ready => Volatile.Read(ref s_index) != null;

        // Main thread, every zone-mode tick: index every whole-biome zone of this world once, off the main thread.
        internal static void EnsureIndex()
        {
            var generator = WorldGenerator.instance;
            if (generator == null || s_building || (s_indexedFor == generator && Ready)) return;
            s_indexedFor = generator;
            Volatile.Write(ref s_index, null);
            s_building = true;
            var started = DateTime.UtcNow;
            Task.Run(() =>
            {
                try
                {
                    var index = Build(generator);
                    Volatile.Write(ref s_index, index);
                    var counts = new List<string>();
                    foreach (var pair in index.Whole) counts.Add($"{pair.Key} {pair.Value.Length}");
                    FiresLogger.LogInfo($"[FiresZones] seed sources indexed in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms: "
                        + $"whole-biome zones {string.Join(", ", counts)}.");
                }
                catch (Exception ex) { FiresLogger.LogError($"[FiresZones] seed source index failed: {ex.Message}"); }
                finally { s_building = false; }
            });
        }

        private static Index Build(WorldGenerator generator)
        {
            var lists = new Dictionary<Heightmap.Biome, List<Vector2s>>();
            ZoneModeGate.EnterPlanning();
            try
            {
                int extent = ZoneOpenSet.ZoneExtent;
                for (int zy = -extent; zy <= extent; zy++)
                {
                    for (int zx = -extent; zx <= extent; zx++)
                    {
                        float cx = zx * ZoneOpenSet.ZoneSize, cz = zy * ZoneOpenSet.ZoneSize;
                        if (cx * cx + cz * cz > WorldRadius * WorldRadius) continue;
                        var biome = generator.GetBiome(cx, cz);
                        if (biome == Heightmap.Biome.Ocean || biome == Heightmap.Biome.None || !Whole(generator, cx, cz, biome)) continue;
                        if (!lists.TryGetValue(biome, out var list)) lists[biome] = list = new List<Vector2s>();
                        list.Add(new Vector2s(zx, zy));
                    }
                }
            }
            finally { ZoneModeGate.ExitPlanning(); }
            var index = new Index();
            foreach (var pair in lists) index.Whole[pair.Key] = pair.Value.ToArray();
            return index;
        }

        private static bool Whole(WorldGenerator generator, float cx, float cz, Heightmap.Biome biome)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx != 0 || dz != 0) && generator.GetBiome(cx + dx * SampleStep, cz + dz * SampleStep) != biome) return false;
            return true;
        }

        // The land a seed of `biome` raises at `zone`. False while the source index is still being built.
        internal static bool TryChooseSource(Vector2s zone, Heightmap.Biome biome, out Vector2s source, out bool faithful)
        {
            source = zone;
            ZoneModeChunkTiers.ZoneTier(zone, out var planned);
            faithful = planned == biome;
            if (faithful) return true;

            for (int i = 0; i < 4; i++)
            {
                var neighbour = new Vector2s(zone.x + (i == 0 ? 1 : i == 1 ? -1 : 0), zone.y + (i == 2 ? 1 : i == 3 ? -1 : 0));
                if (!ZoneModeRegistry.IsOpen(neighbour) || !ZoneModeRegistry.TryGetSource(neighbour, out var theirs)) continue;
                if (ZoneModeRegistry.ShiftOf(neighbour).Slides) continue;
                var continued = new Vector2s(theirs.x + (zone.x - neighbour.x), theirs.y + (zone.y - neighbour.y));
                if (!ZoneOpenSet.InRange(continued.x, continued.y)) continue;
                ZoneModeChunkTiers.ZoneTier(continued, out var continuedBiome);
                if (continuedBiome != biome) continue;
                source = continued;
                return true;
            }

            var index = Volatile.Read(ref s_index);
            if (index == null || !index.Whole.TryGetValue(biome, out var whole) || whole.Length == 0) return false;
            Aim(zone, out int targetX, out int targetY);
            long best = long.MaxValue;
            foreach (var candidate in whole)
            {
                if (candidate.x == zone.x && candidate.y == zone.y) continue;
                long dx = candidate.x - targetX, dy = candidate.y - targetY;
                long distance = dx * dx + dy * dy;
                if (distance >= best) continue;
                best = distance;
                source = candidate;
            }
            return best != long.MaxValue;
        }

        // A point near the zone, fixed by the world seed, so fresh land differs from zone to zone.
        private static void Aim(Vector2s zone, out int x, out int y)
        {
            unchecked
            {
                uint hash = (uint)(ZNet.World?.m_seed ?? 0) * 0x9E3779B1u;
                hash ^= (uint)zone.x * 0x85EBCA77u;
                hash = (hash << 13) | (hash >> 19);
                hash ^= (uint)zone.y * 0xC2B2AE3Du;
                hash *= 0x27D4EB2Fu;
                hash ^= hash >> 15;
                int span = Spread * 2 + 1;
                x = zone.x + (int)(hash % (uint)span) - Spread;
                y = zone.y + (int)((hash >> 16) % (uint)span) - Spread;
            }
        }

        // Server: the land a location seed raises at `zone`. A zone the world planned that location in opens as itself.
        // Otherwise the source is a zone holding a planned instance of it (through its own source when that zone was
        // itself swapped), the one nearest the seed-hashed aim point, and `instance` is that location moved into
        // `zone`. A zone kept for a boss altar, a progression location or a one-per-world place refuses the seed.
        // The location stands at the zone's centre (Fire, 2026-09-27: "A location seed centres its location in the chunk"):
        // the source square is the 64 m square round the location's real spot, which is `source` (the zone holding that
        // spot) slid by `centring`, so the location and everything it levels stay whole inside the zone.
        internal static bool TryChooseLocationSource(Vector2s zone, string location, out Vector2s source, out ZoneShift centring,
            out bool faithful, out ZoneSystem.LocationInstance instance, out string refusal)
        {
            source = zone;
            centring = default;
            faithful = false;
            instance = default;
            refusal = null;
            var instances = ZoneSystem.instance?.m_locationInstances;
            if (instances == null) return false;
            if (instances.TryGetValue(zone, out var own) && own.m_location != null)
            {
                if (NameOf(own.m_location) == location)
                {
                    faithful = true;
                    return true;
                }
                if (MustStay(own.m_location))
                {
                    refusal = "This land is kept for a place of its own. Plant the seed on another zone.";
                    return false;
                }
            }

            Aim(zone, out int targetX, out int targetY);
            long best = long.MaxValue;
            var bestAt = zone;
            bool found = false;
            var centre = ZoneSystem.GetZonePos(zone);
            foreach (var pair in instances)
            {
                var candidate = pair.Value;
                if (candidate.m_location == null || NameOf(candidate.m_location) != location) continue;
                var at = pair.Key;
                var spot = RealSpot(at, candidate.m_position);
                var real = ZoneSystem.GetZone(spot);
                if (real.x == zone.x && real.y == zone.y) continue;
                long dx = at.x - targetX, dy = at.y - targetY;
                long distance = dx * dx + dy * dy;
                if (distance > best || (distance == best && (at.x > bestAt.x || (at.x == bestAt.x && at.y > bestAt.y)))) continue;
                best = distance;
                bestAt = at;
                source = real;
                var realCentre = ZoneSystem.GetZonePos(real);
                centring = new ZoneShift(0f, spot.x - realCentre.x, spot.z - realCentre.z);
                instance = new ZoneSystem.LocationInstance
                {
                    m_location = candidate.m_location,
                    m_position = new Vector3(centre.x, candidate.m_position.y, centre.z),
                    m_placed = false,
                };
                found = true;
            }
            return found;
        }

        // Server, right after swapped zones open and before vanilla populates them: the zone's own planned location
        // leaves with its land, and the source's location comes with the new land. Boss altars, the start temple
        // and other one-per-world locations never move or copy, except the location a location seed brings, which
        // is installed whatever it is.
        internal static void OnOpened(IList<Vector2s> opened, IDictionary<Vector2s, Vector2s> sources,
            IDictionary<Vector2s, ZoneSystem.LocationInstance> brought = null)
        {
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return;
            var instances = zoneSystem.m_locationInstances;
            foreach (var zone in opened)
            {
                if (!sources.TryGetValue(zone, out var source) || (source.x == zone.x && source.y == zone.y)) continue;
                string left = null, came = null;
                if (instances.TryGetValue(zone, out var own) && own.m_location != null && !MustStay(own.m_location))
                {
                    instances.Remove(zone);
                    left = NameOf(own.m_location);
                }
                if (brought != null && brought.TryGetValue(zone, out var seeded))
                {
                    if (!instances.ContainsKey(zone))
                    {
                        instances[zone] = seeded;
                        came = NameOf(seeded.m_location);
                    }
                }
                else if (!instances.ContainsKey(zone) && instances.TryGetValue(source, out var theirs) && theirs.m_location != null
                    && !MustStay(theirs.m_location))
                {
                    var offset = new Vector3((zone.x - source.x) * ZoneOpenSet.ZoneSize, 0f, (zone.y - source.y) * ZoneOpenSet.ZoneSize);
                    instances[zone] = new ZoneSystem.LocationInstance
                    {
                        m_location = theirs.m_location,
                        m_position = theirs.m_position + offset,
                        m_placed = false,
                    };
                    came = NameOf(theirs.m_location);
                }
                if (left != null || came != null) ZoneModeIslands.InvalidateIndex();
                FiresLogger.LogInfo($"[FiresZones] ({zone.x},{zone.y}) raised with the land of ({source.x},{source.y})"
                    + (left != null ? $"; its planned {left} left with its land" : "")
                    + (came != null ? $"; {came} came with the new land" : "") + ".");
            }
        }

        // Placing a one-per-world location makes vanilla drop every unplaced instance of it. Instances a seed put in
        // open swapped zones are land already raised for that location, so they survive it.
        [HarmonyPatch(typeof(ZoneSystem), "RemoveUnplacedLocations")]
        private static class ZoneSystem_RemoveUnplacedLocations_SeededCopies
        {
            private static void Prefix(ZoneSystem __instance, ZoneSystem.ZoneLocation location,
                out List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>> __state)
            {
                __state = null;
                if (!ZoneModeRegistry.ModeActive) return;
                foreach (var pair in __instance.m_locationInstances)
                {
                    if (pair.Value.m_location != location || pair.Value.m_placed || !ZoneModeRegistry.IsOpen(pair.Key)
                        || !ZoneModeRegistry.TryGetSource(pair.Key, out _)) continue;
                    (__state ??= new List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>>()).Add(pair);
                }
            }

            private static void Postfix(ZoneSystem __instance, List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>> __state)
            {
                if (__state == null) return;
                foreach (var pair in __state) __instance.m_locationInstances[pair.Key] = pair.Value;
            }
        }

        // Where a location planned in zone `at` stands in the real world: its own spot, or, when `at` shows other land,
        // the spot of that land its instance was moved onto (the source zone, slid by that zone's centring).
        private static Vector3 RealSpot(Vector2s at, Vector3 position)
        {
            if (!ZoneModeRegistry.TryGetSource(at, out var theirs)) return position;
            var shift = ZoneModeRegistry.ShiftOf(at);
            return position + new Vector3((theirs.x - at.x) * ZoneOpenSet.ZoneSize + shift.Dx, 0f, (theirs.y - at.y) * ZoneOpenSet.ZoneSize + shift.Dz);
        }

        internal static bool MustStay(ZoneSystem.ZoneLocation location)
            => location.m_unique || ZoneModeIslands.IsGuaranteed(NameOf(location)) || NameOf(location) == "StartTemple";

        internal static string NameOf(ZoneSystem.ZoneLocation location)
            => string.IsNullOrEmpty(location.m_prefabName) ? location.m_name : location.m_prefabName;

        internal static void Reset()
        {
            Volatile.Write(ref s_index, null);
            s_indexedFor = null;
        }
    }
}
