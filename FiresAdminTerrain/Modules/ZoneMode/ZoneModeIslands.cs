using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── LOCATION ISLANDS ────────────────────────────────────────────────────
    // The whole location plan exists from world creation. Once a second the server checks every
    // player; a planned location within reveal distance that won its island roll has its own zone
    // opened, so it rises out of the sea as a single-zone island. Vanilla centres every location at
    // least its exterior radius inside its zone, so one zone holds it.
    //
    // The roll is a fixed hash of the world seed and the location's zone, never a live random
    // number: a location that lost it stays under the sea however often players pass by, and it
    // still spawns normally once its zone is opened some other way.
    internal static class ZoneModeIslands
    {
        private const float ScanIntervalSeconds = 1f;
        private const float BucketSize = 512f;
        private const int MaxNamesInLog = 5;
        private const float HashScale = 1f / 16777216f;

        private sealed class Candidate
        {
            public Vector2s Zone;
            public Vector3 Position;
            public string Name;
            public float Roll;
        }

        private static readonly Dictionary<long, List<Candidate>> s_buckets = new Dictionary<long, List<Candidate>>();
        private static readonly HashSet<string> s_optOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // One entry of the "Progression locations" list: any of its names counts, and Count of them must stand.
        private sealed class Guarantee
        {
            internal readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<Candidate> Instances = new List<Candidate>();
            internal int Count = 1;
            internal string Label;
        }

        private static readonly HashSet<string> s_guaranteed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Guarantee> s_guarantees = new List<Guarantee>();
        private static readonly List<Vector3> s_viewers = new List<Vector3>();
        private static int s_indexedInstanceCount = -1;
        private static int s_indexedCount;
        private static string s_optOutSource;
        private static string s_guaranteedSource;
        private static float s_nextScan;

        internal static int RevealedLocations { get; private set; }
        internal static int IndexedLocations => s_indexedCount;

        internal static bool IsGuaranteed(string name)
        {
            RefreshGuaranteed();
            return name != null && s_guaranteed.Contains(name);
        }

        private static void RefreshGuaranteed()
        {
            string guaranteed = ZoneModeConfig.GuaranteedLocations?.Value ?? "";
            if (guaranteed == s_guaranteedSource) return;
            s_guaranteedSource = guaranteed;
            s_guaranteed.Clear();
            s_guarantees.Clear();
            foreach (string raw in guaranteed.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;
                var guarantee = new Guarantee { Label = entry };
                int space = entry.LastIndexOf(' ');
                if (space > 0 && entry.Length > space + 2 && (entry[space + 1] == 'x' || entry[space + 1] == 'X')
                    && int.TryParse(entry.Substring(space + 2), out int count) && count > 0)
                {
                    guarantee.Count = count;
                    entry = entry.Substring(0, space).Trim();
                }
                foreach (string name in entry.Split('|'))
                {
                    if (name.Trim().Length == 0) continue;
                    guarantee.Names.Add(name.Trim());
                    s_guaranteed.Add(name.Trim());
                }
                if (guarantee.Names.Count > 0) s_guarantees.Add(guarantee);
            }
            s_indexedInstanceCount = -1;
        }

        // A swap can replace a zone's planned location without changing how many there are.
        internal static void InvalidateIndex() => s_indexedInstanceCount = -1;

        internal static void Reset()
        {
            s_buckets.Clear();
            s_guarantees.Clear();
            s_guaranteed.Clear();
            s_indexedInstanceCount = -1;
            s_indexedCount = 0;
            s_guaranteedSource = null;
            s_nextScan = 0f;
            RevealedLocations = 0;
        }

        internal static void Tick()
        {
            if (!ZoneModeRegistry.ModeActive || !ZoneModeRegistry.IsAuthority || !ZoneModeRegistry.Seeded) return;
            if (Time.time < s_nextScan) return;
            s_nextScan = Time.time + ScanIntervalSeconds;
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null || !zoneSystem.LocationsGenerated) return;
            EnsureIndex(zoneSystem);

            float reach = ZoneModeConfig.RevealDistance.Value;
            float chance = ZoneModeConfig.RevealChance.Value;
            bool islands = ZoneModeConfig.RevealLocations != null && ZoneModeConfig.RevealLocations.Value;
            var toOpen = new List<Vector2s>();
            var names = new List<string>();
            s_viewers.Clear();
            s_viewers.AddRange(ViewerPositions());
            RaiseGuaranteed(reach, toOpen, names);
            foreach (Vector3 viewer in s_viewers)
            {
                if (!islands) break;
                foreach (var candidate in Nearby(viewer, reach))
                {
                    if (candidate.Roll >= chance || s_optOut.Contains(candidate.Name)) continue;
                    if (ZoneModeRegistry.IsOpen(candidate.Zone) || toOpen.Contains(candidate.Zone)) continue;
                    float dx = candidate.Position.x - viewer.x, dz = candidate.Position.z - viewer.z;
                    if (dx * dx + dz * dz > reach * reach) continue;
                    toOpen.Add(candidate.Zone);
                    names.Add(candidate.Name);
                }
            }
            if (toOpen.Count == 0) return;
            RevealedLocations += toOpen.Count;
            string listed = string.Join(", ", names.GetRange(0, Math.Min(MaxNamesInLog, names.Count)))
                + (names.Count > MaxNamesInLog ? $" +{names.Count - MaxNamesInLog} more" : "");
            ZoneModeOpening.Open(toOpen, ZoneOpenReason.LocationReveal, 0L, $"location island: {listed}");
        }

        // Progression can never be blocked: until enough of a guaranteed location stand in open zones, the instance
        // nearest a player within reach rises, whatever its roll or the opt-out list say. One per entry per scan.
        private static void RaiseGuaranteed(float reach, List<Vector2s> toOpen, List<string> names)
        {
            float reach2 = reach * reach;
            foreach (var guarantee in s_guarantees)
            {
                int standing = 0;
                Candidate nearest = null;
                float nearest2 = reach2;
                foreach (var candidate in guarantee.Instances)
                {
                    if (ZoneModeRegistry.IsOpen(candidate.Zone) || toOpen.Contains(candidate.Zone)) { standing++; continue; }
                    foreach (var viewer in s_viewers)
                    {
                        float dx = candidate.Position.x - viewer.x, dz = candidate.Position.z - viewer.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 > nearest2) continue;
                        nearest2 = d2;
                        nearest = candidate;
                    }
                }
                if (standing >= guarantee.Count || nearest == null) continue;
                toOpen.Add(nearest.Zone);
                names.Add($"{nearest.Name} (guaranteed {standing + 1}/{guarantee.Count})");
            }
        }

        private static IEnumerable<Vector3> ViewerPositions()
        {
            var net = ZNet.instance;
            if (net == null) yield break;
            if (Player.m_localPlayer != null) yield return Player.m_localPlayer.transform.position;
            foreach (var peer in net.GetPeers())
                if (peer != null && peer.IsReady()) yield return peer.GetRefPos();
        }

        private static IEnumerable<Candidate> Nearby(Vector3 viewer, float reach)
        {
            int minX = Mathf.FloorToInt((viewer.x - reach) / BucketSize), maxX = Mathf.FloorToInt((viewer.x + reach) / BucketSize);
            int minZ = Mathf.FloorToInt((viewer.z - reach) / BucketSize), maxZ = Mathf.FloorToInt((viewer.z + reach) / BucketSize);
            for (int bz = minZ; bz <= maxZ; bz++)
                for (int bx = minX; bx <= maxX; bx++)
                    if (s_buckets.TryGetValue(BucketKey(bx, bz), out var list))
                        foreach (var candidate in list) yield return candidate;
        }

        private static long BucketKey(int bx, int bz) => ((long)bx << 32) ^ (uint)bz;

        // Stable per world and zone: the same seed always rolls the same islands.
        private static float IslandRoll(int worldSeed, Vector2s zone)
        {
            unchecked
            {
                uint hash = (uint)worldSeed * 0x9E3779B1u;
                hash ^= (uint)zone.x * 0x85EBCA77u;
                hash = (hash << 13) | (hash >> 19);
                hash ^= (uint)zone.y * 0xC2B2AE3Du;
                hash *= 0x27D4EB2Fu;
                hash ^= hash >> 15;
                hash *= 0x165667B1u;
                hash ^= hash >> 13;
                return (hash & 0xFFFFFFu) * HashScale;
            }
        }

        private static void EnsureIndex(ZoneSystem zoneSystem)
        {
            string optOut = ZoneModeConfig.RevealOptOut?.Value ?? "";
            if (optOut != s_optOutSource)
            {
                s_optOutSource = optOut;
                s_optOut.Clear();
                foreach (string name in optOut.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (name.Trim().Length > 0) s_optOut.Add(name.Trim());
            }

            RefreshGuaranteed();

            var instances = zoneSystem.m_locationInstances;
            if (instances.Count == s_indexedInstanceCount) return;
            s_indexedInstanceCount = instances.Count;
            s_buckets.Clear();
            foreach (var guarantee in s_guarantees) guarantee.Instances.Clear();
            s_indexedCount = 0;
            int worldSeed = ZNet.World?.m_seed ?? 0;
            float chance = ZoneModeConfig.RevealChance.Value;
            int winners = 0;
            foreach (var pair in instances)
            {
                var location = pair.Value.m_location;
                if (location == null) continue;
                var candidate = new Candidate
                {
                    Zone = pair.Key,
                    Position = pair.Value.m_position,
                    Name = string.IsNullOrEmpty(location.m_prefabName) ? location.m_name : location.m_prefabName,
                    Roll = IslandRoll(worldSeed, pair.Key),
                };
                if (candidate.Roll < chance) winners++;
                long key = BucketKey(Mathf.FloorToInt(candidate.Position.x / BucketSize), Mathf.FloorToInt(candidate.Position.z / BucketSize));
                if (!s_buckets.TryGetValue(key, out var list)) s_buckets[key] = list = new List<Candidate>();
                list.Add(candidate);
                s_indexedCount++;
                if (!s_guaranteed.Contains(candidate.Name)) continue;
                foreach (var guarantee in s_guarantees)
                    if (guarantee.Names.Contains(candidate.Name)) guarantee.Instances.Add(candidate);
            }
            var guaranteedCounts = new List<string>();
            foreach (var guarantee in s_guarantees)
                guaranteedCounts.Add(guarantee.Instances.Count >= guarantee.Count
                    ? $"{guarantee.Label} ({guarantee.Instances.Count} planned)"
                    : $"{guarantee.Label} ONLY {guarantee.Instances.Count} PLANNED");
            FiresLogger.LogInfo($"[FiresZones] location islands indexed: {s_indexedCount} planned location(s), {winners} of them "
                + $"({chance * 100f:0.#}% chance) rise as single-zone islands within {ZoneModeConfig.RevealDistance.Value:0} m of a player"
                + (s_optOut.Count > 0 ? $"; {s_optOut.Count} name(s) opted out" : "")
                + $". Guaranteed (the first one a player nears rises): {string.Join(", ", guaranteedCounts)}.");
        }
    }
}
