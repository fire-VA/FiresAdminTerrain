using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // FatApi.ReplaceZoneLocations: the forced biome's locations ACTUALLY replace the old ones,
    // judged by the FORCED biome, not the world's). For a zone list (an RPGM territory) and the biome forced over it:
    //  1. Only VanillaNeeds places (the start temple, boss altars, m_unique places; fuling villages and dungeons go) stay
    //     and are listed. A built zone with someone inside its dungeon is left alone this run (listed; call again).
    //  2. Every other location in those zones is taken away (BiomePaint.RemovePlaced for a placed one: DestroyZDO, never a build,
    //     tombstone or tamed creature; chest items dropped at its centre, dungeon things lifted out), whatever biome it belongs to.
    //  3. New ones: the forced biome's location density in the world (instances of locations of that biome per zone of that biome,
    //     sampled over the world's zones) times the zone count gives how many; zones are tried in a world+zone seeded order, and in
    //     each one vanilla's placement test (BiomePaint.Fits: biome, altitude, slope, forest, vegetation, distance rules and spacing
    //     from its like, the run's own picks included) picks one weighted by quantity. One location per zone, as vanilla.
    //  4. A built zone loaded on the server gets its new location placed by vanilla a moment later; a built zone not loaded is
    //     queued (saved, "loc" in the regrow queue) and placed when it next loads; an unbuilt zone only changes its plan.
    // dryRun: everything is worked out (picks included) and reported; nothing in the world changes.
    // An enumerator stepped by the time-sliced job runner (BiomePaint.ZoneJobs). Planning never changes the world's location
    // list: the old locations being replaced and the run's own picks reach the spacing test through s_planExclude / s_planExtra, set
    // only around each pick. The search is capped per location type (PlanTriesPerType). Apply = one zone per step, re-checked.
    internal static partial class BiomePaint
    {

        // counts: three ints per zone in input order (duplicates dropped): [old location removed 0/1, new location placed/planned 0/1, kept 0/1].
        internal static IEnumerator ReplaceZoneLocationsJob(IList<Vector2s> zones, Heightmap.Biome forced, bool dryRun, ZoneJobResult res)
        {
            var zs = ZoneSystem.instance;
            var generator = WorldGenerator.instance;
            if (zs == null || generator == null || ZDOMan.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) { res.Report = "not the server"; yield break; }
            if (!zs.LocationsGenerated) { res.Report = "the world's locations are not generated yet"; yield break; }
            if (forced == Heightmap.Biome.None || forced == Heightmap.Biome.Ocean) { res.Report = $"no locations for biome {forced}"; yield break; }
            if (!ResolveLocations() || !Resolve()) { res.Report = "ZoneSystem's internals changed shape"; yield break; }
            EnsureRegenWorld();
            var list = new List<Vector2s>();
            var set = new HashSet<Vector2s>();
            foreach (var z in zones) if (set.Add(z)) list.Add(z);
            var counts = new int[list.Count * 3];
            var original = new Dictionary<Vector2s, ZoneSystem.LocationInstance>();
            var protectedNames = new Dictionary<string, int>();
            var waitingLines = new List<string>();
            var free = new List<int>();
            var removeOld = new List<int>();
            var exclude = new HashSet<Vector2s>();
            res.Phase = "classify"; res.Steps = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                res.Step = i;
                if ((i & 63) == 63) yield return null;
                var zone = list[i];
                var instances = zs.m_locationInstances;
                bool has = instances.TryGetValue(zone, out var cur) && cur.m_location != null;
                if (has && VanillaNeeds(cur.m_location, out string needWhy))   // only what vanilla needs stays
                {
                    counts[3 * i + 2] = 1;
                    string n = LocationName(cur.m_location);
                    protectedNames[n] = protectedNames.TryGetValue(n, out int c) ? c + 1 : 1;
                    FiresLogger.LogInfo($"[BiomePaint] kept {n} in zone {zone.x}/{zone.y}: {needWhy}");
                    continue;
                }
                bool built = GeneratedZones(zs).Contains(zone);
                if (has && built && cur.m_placed && PlayerInside(zone))
                {
                    counts[3 * i + 2] = 1;
                    waitingLines.Add($"{LocationName(cur.m_location)} in zone ({zone.x},{zone.y}): someone is in its dungeon, call again");
                    continue;
                }
                if (has) { original[zone] = cur; removeOld.Add(i); exclude.Add(zone); }
                free.Add(i);
            }
            // How many: the forced biome's location density (this world's, else its vanilla table) times the zone count.
            var measured = new LocationDensity();
            yield return MeasureLocationDensity(zs, generator, forced, res, measured);
            float density = measured.PerZone;
            string densityFrom = measured.Source;
            int want = Mathf.RoundToInt(density * free.Count);
            var order = new List<int>(free);
            uint seed = (uint)generator.GetSeed();
            order.Sort((a, b) => ZoneOrder(seed, list[a]).CompareTo(ZoneOrder(seed, list[b])));
            var picks = new Dictionary<int, ZoneSystem.LocationInstance>();
            var extra = new List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>>();
            var tries = new Dictionary<string, int>();
            var pickedNames = new Dictionary<string, int>();
            res.Phase = "pick"; res.Steps = order.Count;
            for (int k0 = 0; k0 < order.Count && picks.Count < want; k0++)
            {
                res.Step = k0;
                yield return null;
                int i = order[k0];
                var zone = list[i];
                var centre = ZoneSystem.GetZonePos(zone);
                Vector3 at = centre;
                bool ok = false;
                uint h = ZoneOrder(seed ^ SpotSeedSalt, zone);
                for (int k = 0; k < SpotsPerZone && !ok; k++)
                {
                    if (k > 0) yield return null;   // one spot per step: a spot tests every fitting location type once
                    var spot = k == 0 ? centre : centre + new Vector3(((h >> (k * 6)) & SpotOffsetMask) - SpotOffsetCentre, 0f, ((h >> (k * 6 + 3)) & SpotOffsetMask) - SpotOffsetCentre);
                    s_planExclude = exclude; s_planExtra = extra; s_planTries = tries;
                    try { ok = TryPickZone(zs, zone, spot, forced, out at); }
                    finally { ClearPlan(); }
                }
                if (!ok) continue;
                var inst = new ZoneSystem.LocationInstance { m_location = s_pickZone, m_position = at, m_placed = false };
                picks[i] = inst;
                extra.Add(new KeyValuePair<Vector2s, ZoneSystem.LocationInstance>(zone, inst));
                string pn = LocationName(s_pickZone);
                pickedNames[pn] = pickedNames.TryGetValue(pn, out int pc) ? pc + 1 : 1;
            }
            foreach (int i in removeOld) counts[3 * i] = 1;
            var tally = new SwapTally();
            int placedNow = 0, queuedLoc = 0, planned = 0, changedMeanwhile = 0;
            if (!dryRun)
            {
                res.Phase = "apply"; res.Steps = list.Count;
                for (int i = 0; i < list.Count; i++)
                {
                    res.Step = i;
                    bool isOld = original.ContainsKey(list[i]);
                    bool isNew = picks.ContainsKey(i);
                    if (!isOld && !isNew) continue;
                    yield return null;
                    var zone = list[i];
                    var instances = zs.m_locationInstances;
                    bool has = instances.TryGetValue(zone, out var cur) && cur.m_location != null;
                    if (isOld)
                    {
                        var old = original[zone];
                        // The world moved on since the plan (placed, swapped, or someone went into its dungeon): leave this zone.
                        if (!has || !Same(cur.m_location, old.m_location) || cur.m_placed != old.m_placed || (old.m_placed && PlayerInside(zone)))
                        {
                            changedMeanwhile++;
                            counts[3 * i] = 0;
                            if (isNew) { picks.Remove(i); counts[3 * i + 1] = 0; }
                            continue;
                        }
                        if (old.m_placed && GeneratedZones(zs).Contains(zone)) RemovePlaced(zs, zone, old, tally, default, false);   // entrance AND its dungeon interior (~5000 m up)
                        instances.Remove(zone);
                        s_swaps.Remove(zone);
                        tally.Icons |= HasIcon(old.m_location);
                    }
                    else if (has) { changedMeanwhile++; picks.Remove(i); continue; }   // a location appeared there meanwhile
                    if (!isNew) { ZoneLine(zone, original[zone], default, null); continue; }
                    var pick = picks[i];
                    instances[zone] = pick;
                    tally.Icons |= HasIcon(pick.m_location);
                    string how;
                    if (!GeneratedZones(zs).Contains(zone)) { planned++; how = "planned (zone not generated yet)"; }
                    else if (HeightmapOf(zs, zone) != null)
                    {
                        ClearFootprint(zs, zone, pick.m_location, pick.m_position);
                        QueuePlacement(zs, zone);
                        placedNow++; how = "placed now";
                    }
                    else { QueueLocation(zone); queuedLoc++; how = "placed when the zone next loads"; }
                    ZoneLine(zone, isOld ? original[zone] : default, pick, how);
                    continue;
                }
                s_swapsChanged = true;
                Finish(zs, tally);
                SaveRegenQueue();
            }
            int removed = 0;
            foreach (int i in removeOld) removed += counts[3 * i];
            foreach (var kv in picks) counts[3 * kv.Key + 1] = 1;
            string capped = "";
            foreach (var kv in tries) if (kv.Value >= PlanTriesPerType) capped += (capped.Length > 0 ? ", " : "") + kv.Key;
            res.Counts = counts;
            res.Report = $"FatApi.ReplaceZoneLocations ({list.Count} zone(s), forced {forced}){(dryRun ? " dry run" : "")}: {removed} old location(s) {(dryRun ? "would be " : "")}removed; "
                + $"{picks.Count} of {want} wanted new {forced} location(s) {(dryRun ? "would be placed" : "placed")} ({densityFrom} density {density:0.000} per zone)"
                + (dryRun ? "" : $" ({placedNow} now, {queuedLoc} when their zone next loads, {planned} planned for unbuilt zones"
                    + (changedMeanwhile > 0 ? $", {changedMeanwhile} zone(s) left alone: changed during the run" : "") + ")")
                + (pickedNames.Count > 0 ? ": " + string.Join(", ", Linq(pickedNames)) : "")
                + $"; kept: {(protectedNames.Count > 0 ? string.Join(", ", Linq(protectedNames)) + " (start temple / boss altars / one-per-world, each logged with its reason)" : "no protected places")}"
                + (waitingLines.Count > 0 ? "; waiting: " + string.Join("; ", waitingLines) : "")
                + (capped.Length > 0 ? $"; search capped ({PlanTriesPerType} tests) for: {capped}" : "")
                + (tally.Objects > 0 ? $"; {tally.Objects} old object(s) went, {tally.Items} chest item(s) at their centres, {tally.Lifted} lifted out of a dungeon" : "") + ".";
            FiresLogger.LogInfo("[BiomePaint] " + res.Report);

        }

        // Synchronous form: runs the job to its end in one call. Blocks the frame on big zone lists: use the async API.
        internal static int[] ReplaceZoneLocations(IList<Vector2s> zones, Heightmap.Biome forced, bool dryRun, out string report)
        {
            var res = new ZoneJobResult();
            RunToEnd(ReplaceZoneLocationsJob(zones, forced, dryRun, res));
            report = res.Report;
            return res.Counts;
        }

        // Force biome clears and replaces all locations, fuling camps and dungeons included;
        // only what vanilla needs stays: the start temple, the boss altars / summoning sites and the
        // one-per-world places (m_unique: the traders and the like). Fuling villages, every dungeon, runestones and the rest go.
        private static readonly string[] BossSites = { "Eikthyrnir", "GDKing", "Bonemass", "Dragonqueen", "GoblinKing", "Mistlands_DvergrBossEntrance", "FaderLocation" };
        private static bool VanillaNeeds(ZoneSystem.ZoneLocation location, out string why)
        {
            string n = ZoneModeSwaps.NameOf(location);
            why = null;
            if (n == "StartTemple") why = "the start temple (vanilla needs one per world)";
            else foreach (var b in BossSites) if (n.StartsWith(b, System.StringComparison.Ordinal)) { why = "boss altar (vanilla needs one per world)"; break; }
            if (why == null && location.m_unique) why = "one-per-world place (vanilla places only one: traders and the like)";
            return why != null;
        }

        // One line per changed zone.
        private static void ZoneLine(Vector2s zone, ZoneSystem.LocationInstance old, ZoneSystem.LocationInstance pick, string how)
        {
            string removed = old.m_location != null ? $"1 location removed ({LocationName(old.m_location)})" : "0 locations removed";
            string placed = pick.m_location != null ? $"1 placed ({LocationName(pick.m_location)}, {how})" : "0 placed";
            FiresLogger.LogInfo($"[BiomePaint] zone {zone.x}/{zone.y}: {removed}, {placed}");
        }

        // Picks try the zone centre and then seeded spots up to ~16 m round it (vanilla tries many random spots per zone).
        private const int SpotsPerZone = 5;
        private const uint SpotOffsetMask = 31;            // 0..31 m
        private const float SpotOffsetCentre = 15.5f;      // so the offset spans -15.5..+15.5 m
        private const uint SpotSeedSalt = 0x9E3779B9u;     // keeps the spot sequence apart from the zone order

        private static uint ZoneOrder(uint seed, Vector2s zone) => Mix(Mix(Mix(HashBasis, seed), (uint)zone.x), (uint)zone.y);
    }
}
