using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // The base heights HeightmapBuilder hands a zone's Heightmap (before TerrainComp and TerrainModifier edits),
    // kept without a Heightmap. A dedicated server holds no heightmaps near its players, so its voxel data and every
    // underground query stand on these: the same builder, the same zone-mode build pin, the same numbers a client's
    // heightmap starts from. Main thread only.
    internal static class HeightmapBuildCache
    {
        private const int MaxCachedZones = 512;
        private const int EvictBatch = 64;
        private const float ProbeEpsilon = 0.013f;

        private sealed class Entry
        {
            public float[] Heights;
            public int Epoch;
            public float LastUsed;
        }

        private static readonly Dictionary<Vector2s, Entry> s_entries = new Dictionary<Vector2s, Entry>();
        private static readonly List<KeyValuePair<Vector2s, Entry>> s_evictScratch = new List<KeyValuePair<Vector2s, Entry>>();
        private static WorldGenerator s_generator;
        private static int s_width;
        private static float s_scale;

        internal static int CachedZoneCount => s_entries.Count;

        // True once the zone and its eight neighbours are cached for the current zone-mode view: a zone's voxel
        // columns reach one sample past its edge, into every neighbour. Missing or stale ones are requested.
        internal static bool EnsureZoneAndNeighbours(Vector2s zone)
        {
            bool ready = true;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!EnsureZone(new Vector2s(zone.x + dx, zone.y + dy))) ready = false;
            return ready;
        }

        // A zone missing from the cache, or cached under an older zone-mode view (every zone opening starts a new one),
        // is requested again on the spot and answers once its build is ready.
        internal static bool TryHeight(float wx, float wz, out float height)
        {
            height = 0f;
            if (s_entries.Count == 0) return false;
            float px = wx + ProbeEpsilon, pz = wz + ProbeEpsilon;
            var zone = ZoneSystem.GetZone(new Vector3(px, 0f, pz));
            if ((!s_entries.TryGetValue(zone, out var entry) || entry.Epoch != Zones.ZoneModeGate.CurrentEpoch)
                && (!EnsureZone(zone) || !s_entries.TryGetValue(zone, out entry)))
                return false;
            var center = ZoneSystem.GetZonePos(zone);
            int half = s_width / 2;
            int vx = Mathf.FloorToInt((px - center.x) / s_scale + 0.5f) + half;
            int vz = Mathf.FloorToInt((pz - center.z) / s_scale + 0.5f) + half;
            int row = s_width + 1;
            if (vx < 0 || vz < 0 || vx >= row || vz >= row) return false;
            entry.LastUsed = Time.time;
            height = entry.Heights[vz * row + vx] + center.y;
            return true;
        }

        internal static void Clear()
        {
            s_entries.Clear();
            s_generator = null;
        }

        private static bool EnsureZone(Vector2s zone)
        {
            var generator = WorldGenerator.instance;
            var builder = HeightmapBuilder.instance;
            if (generator == null || builder == null || !ResolveZoneShape()) return false;
            if (!ReferenceEquals(generator, s_generator))
            {
                s_entries.Clear();
                s_generator = generator;
            }
            int epoch = Zones.ZoneModeGate.CurrentEpoch;
            if (s_entries.TryGetValue(zone, out var cached) && cached.Epoch == epoch)
            {
                cached.LastUsed = Time.time;
                return true;
            }
            var center = ZoneSystem.GetZonePos(zone);
            if (!builder.IsTerrainReady(center, s_width, s_scale, false, generator)) return false;
            var data = builder.RequestTerrainSync(center, s_width, s_scale, false, generator);
            if (data?.m_baseHeights == null) return false;
            s_entries[zone] = new Entry { Heights = data.m_baseHeights.ToArray(), Epoch = epoch, LastUsed = Time.time };
            if (s_entries.Count > MaxCachedZones) EvictOldest();
            return true;
        }

        private static bool ResolveZoneShape()
        {
            if (s_width > 0) return true;
            var zonePrefab = ZoneSystem.instance != null ? ZoneSystem.instance.m_zonePrefab : null;
            var heightmap = zonePrefab != null ? zonePrefab.GetComponentInChildren<Heightmap>() : null;
            if (heightmap == null) return false;
            s_width = heightmap.m_width;
            s_scale = heightmap.m_scale;
            return s_width > 0 && s_scale > 0f;
        }

        private static void EvictOldest()
        {
            s_evictScratch.Clear();
            s_evictScratch.AddRange(s_entries);
            s_evictScratch.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
            for (int i = 0; i < EvictBatch && i < s_evictScratch.Count; i++) s_entries.Remove(s_evictScratch[i].Key);
            s_evictScratch.Clear();
        }
    }
}
