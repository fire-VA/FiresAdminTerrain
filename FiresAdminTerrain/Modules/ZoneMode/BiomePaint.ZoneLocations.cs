using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // FatApi.SwapZoneLocations (used by RPGM ForceBiome territories, so the forced biome's
    // locations follow too). A zone's location follows the biome the world answers there NOW (WorldGenerator.GetBiome, so a mod's forced
    // biome counts), by the paint swap's own rules (BiomePaint.Locations): a location that still belongs stays; otherwise one of
    // vanilla's locations for that ground that passes vanilla's placement test at the spot takes its place (seeded by world + zone,
    // weighted by quantity), and when none fits the zone loses it. Protected / one-per-world places never swap, nor at sea.
    // A built zone's old location goes through RemovePlaced (DestroyZDO; never a build, tombstone or tamed creature; chest items
    // dropped, dungeon things lifted) and the new one is placed by vanilla a moment later; a zone nobody has built only changes its
    // plan. A built zone the server has no live heightmap for, or with someone in its dungeon, is left alone and reported (call again).
    // Unlike a paint, nothing is remembered in the paint file: the new location is simply the world's location for that zone now.
    internal static partial class BiomePaint
    {
        // counts: two ints per zone in input order (duplicates dropped): [swapped (or would be) 0/1, kept 0/1].
        internal static int[] SwapZoneLocations(IList<Vector2s> zones, bool dryRun, out string report)
        {
            report = string.Empty;
            var zs = ZoneSystem.instance;
            var generator = WorldGenerator.instance;
            if (zs == null || generator == null || ZDOMan.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) { report = "not the server"; return null; }
            if (!zs.LocationsGenerated) { report = "the world's locations are not generated yet"; return null; }
            if (!ResolveLocations() || !Resolve()) { report = "ZoneSystem's internals changed shape"; return null; }
            var list = new List<Vector2s>();
            var set = new HashSet<Vector2s>();
            foreach (var z in zones) if (set.Add(z)) list.Add(z);
            var counts = new int[list.Count * 2];
            var tally = new SwapTally();
            var lines = new List<string>();
            int swapped = 0, kept = 0, waiting = 0, none = 0;
            var keptWhy = new Dictionary<string, int>();   // "<location>: <rule>" -> zones
            for (int i = 0; i < list.Count; i++)
            {
                var zone = list[i];
                var instances = zs.m_locationInstances;
                if (!instances.TryGetValue(zone, out var current) || current.m_location == null) { none++; continue; }
                var own = current.m_location;
                var at = current.m_position;
                var ground = generator.GetBiome(at);
                string why = ZoneModeSwaps.MustStay(own) ? "protected / one-per-world"
                    : ground == Heightmap.Biome.Ocean ? "at sea"
                    : Belongs(own, ground, null) ? $"still belongs (its biomes {own.m_biome} include the ground {ground})" : null;
                if (why != null)
                {
                    counts[2 * i + 1] = 1; kept++;
                    string key = $"{LocationName(own)} ({own.m_biome}): {why}";
                    keptWhy[key] = keptWhy.TryGetValue(key, out int n) ? n + 1 : 1;
                    continue;
                }
                bool built = GeneratedZones(zs).Contains(zone);
                bool placed = current.m_placed;
                if (built && placed && (HeightmapOf(zs, zone) == null || PlayerInside(zone)))
                {
                    waiting++;
                    lines.Add($"zone ({zone.x},{zone.y}) {LocationName(own)}: not loaded on the server or someone is in its dungeon, call again");
                    continue;
                }
                var want = TryPickZone(zs, zone, at, ground, out var wantAt) ? s_pickZone : null;
                counts[2 * i] = 1; swapped++;
                string line = want == null ? $"zone ({zone.x},{zone.y}) {LocationName(own)} is removed: nothing of {ground} fits there"
                    : $"zone ({zone.x},{zone.y}) {LocationName(own)} becomes {LocationName(want)} at ({wantAt.x:0},{wantAt.z:0})";
                lines.Add(line + (built ? (placed ? "" : " (built, never placed: plan only)") : " (planned: placed when the zone is built)"));
                if (dryRun) continue;
                if (built && placed) RemovePlaced(zs, zone, current, tally, default, false);
                if (want == null) instances.Remove(zone);
                else instances[zone] = new ZoneSystem.LocationInstance { m_location = want, m_position = wantAt, m_placed = false };
                if (built && placed && want != null)
                {
                    ClearFootprint(zs, zone, want, wantAt);
                    QueuePlacement(zs, zone);
                }
                tally.Icons |= HasIcon(own) || HasIcon(want);
            }
            if (!dryRun && tally.Icons) SendIcons(zs);
            report = $"FatApi.SwapZoneLocations ({list.Count} zone(s)){(dryRun ? " dry run" : "")}: {swapped} location(s) {(dryRun ? "would swap" : "swapped")}, {kept} kept, "
                + $"{none} zone(s) with no location, {waiting} waiting" + (lines.Count > 0 ? ". " + string.Join("; ", lines) : "")
                + (keptWhy.Count > 0 ? ". Kept: " + string.Join("; ", Linq(keptWhy)) : "")
                + (tally.Objects > 0 ? $". {tally.Objects} old object(s) went, {tally.Items} chest item(s) at their centres, {tally.Lifted} lifted out of a dungeon" : "") + ".";
            FiresLogger.LogInfo("[BiomePaint] " + report);
            return counts;
        }

        private static IEnumerable<string> Linq(Dictionary<string, int> d) { foreach (var kv in d) yield return $"{kv.Key} x{kv.Value}"; }

        private static ZoneSystem.ZoneLocation s_pickZone;

        // TryPick without a paint circle: the ground is the world's biome there, no blend, seeded by world + zone.
        private static bool TryPickZone(ZoneSystem zones, Vector2s zone, Vector3 spot, Heightmap.Biome ground, out Vector3 at)
        {
            s_pickZone = null;
            at = spot;
            s_fitting.Clear();
            s_fittingAt.Clear();
            long total = 0;
            foreach (var location in s_locationPool)
            {
                if (!Eligible(location)) continue;
                if (s_planTries != null)   // the placement search is capped per location type per run
                {
                    string key = LocationName(location);
                    s_planTries.TryGetValue(key, out int tries);
                    if (tries >= PlanTriesPerType) continue;
                    s_planTries[key] = tries + 1;
                }
                if (!Fits(zones, location, zone, spot, ground, null, out var fitAt)) continue;
                s_fitting.Add(location);
                s_fittingAt.Add(fitAt);
                total += location.m_quantity;
            }
            if (total == 0) return false;
            uint h = Mix(HashBasis, (uint)(WorldGenerator.instance?.GetSeed() ?? 0));
            h = Mix(Mix(Mix(h, (uint)zone.x), (uint)zone.y), 0x5A0E993u);
            long roll = h % total;
            for (int i = 0; i < s_fitting.Count; i++)
            {
                roll -= s_fitting[i].m_quantity;
                if (roll >= 0) continue;
                s_pickZone = s_fitting[i];
                at = s_fittingAt[i];
                return true;
            }
            return false;
        }
    }
}
