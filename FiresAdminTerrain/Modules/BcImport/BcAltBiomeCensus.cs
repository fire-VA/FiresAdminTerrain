using System.Collections.Generic;
using System.Text;
using HarmonyLib;

namespace VerdantsAscent.Terrain.BcImport
{
    // Why an alt-biome location cannot place, said in numbers instead of guessed at.
    //
    // A location with an AltBiomeParent places only where the biome SECTOR under the point carries an AltBiome of
    // that name (ZoneSystem, errorAltBiomeMissing):
    //
    //   if (location.AltBiomeParent != null && !biomeSector.AltBiomes.Any(x => x.m_name == location.AltBiomeParent))
    //       { errorAltBiomeMissing++; continue; }
    //
    // A converted world logged 96,686 of those, and every _1 / _sunk / _leet variant failed at roughly 100% while
    // ordinary locations placed fine. Three explanations fit that shape and the logs cannot tell them apart:
    // no alt-biomes are DEFINED at all, they are defined but never ASSIGNED to sectors, or they are assigned to
    // sectors whose biome no location asks for. This prints supply and demand side by side, so the next run says
    // which one it is.
    //
    // Runs off AltBiomeWorldData.GenerateAltBiomes, the moment assignment finishes, which is also the only moment
    // the answer is complete.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    [HarmonyPatch(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateAltBiomes))]
    internal static class Patch_AltBiomeWorldData_GenerateAltBiomes_Census
    {
        private const int NamesListed = 12;

        [HarmonyPostfix]
        private static void Postfix(AltBiomeWorldData __instance)
        {
            try { Report(__instance); }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[AltBiome] the census threw and was skipped ({ex.GetType().Name}: {ex.Message}). "
                    + "Nothing else is affected.");
            }
        }

        private static void Report(AltBiomeWorldData data)
        {
            // Only where locations get placed (server or singleplayer); on a joining client the answer changes nothing.
            if (ZNet.instance != null && !ZNet.instance.IsServer()) return;
            int defined = AltBiomeList.m_altBiomes != null ? AltBiomeList.m_altBiomes.Count : 0;
            if (defined == 0)
            {
                FiresLogger.LogWarning("[AltBiome] AltBiomeList.m_altBiomes is EMPTY: this world defines no alt-biomes at "
                    + "all, so every location with an AltBiomeParent fails with errorAltBiomeMissing no matter what the "
                    + "terrain looks like. That is a content/registration problem, not a biome map one.");
                return;
            }

            var supply = new Dictionary<string, int>();
            int sectors = 0;
            int withAlt = 0;
            if (data != null && data.Sectors != null)
            {
                foreach (BiomeSector sector in data.Sectors)
                {
                    if (sector == null) continue;
                    sectors++;
                    if (sector.AltBiomes == null || sector.AltBiomes.Count == 0) continue;
                    withAlt++;
                    foreach (AltBiome alt in sector.AltBiomes)
                    {
                        if (alt == null || string.IsNullOrEmpty(alt.m_name)) continue;
                        int seen;
                        supply[alt.m_name] = supply.TryGetValue(alt.m_name, out seen) ? seen + 1 : 1;
                    }
                }
            }

            FiresLogger.LogInfo($"[AltBiome] {defined} alt-biome(s) defined; {withAlt} of {sectors} biome sector(s) carry "
                + $"at least one. Placed: {Describe(supply)}");

            var demand = Demand();
            if (demand.Count == 0) return;
            FiresLogger.LogInfo($"[AltBiome] locations asking for one: {Describe(demand)}");

            var starved = new List<string>();
            foreach (KeyValuePair<string, int> want in demand)
                if (!supply.ContainsKey(want.Key)) starved.Add($"{want.Key} ({want.Value} location(s))");

            if (starved.Count == 0)
            {
                FiresLogger.LogInfo("[AltBiome] every alt-biome a location asks for exists somewhere, so placement "
                    + "failures are about where those sectors are, not whether they exist.");
                return;
            }

            // Info, not Warning: an alt-biome with no sector is normal for some seeds (Fire 10-10).
            FiresLogger.LogInfo($"[AltBiome] {starved.Count} alt-biome(s) are asked for by locations but exist in NO "
                + $"sector, so those locations can never place: {string.Join(", ", starved.ToArray())}.");
        }

        /// <summary>How many locations want each alt-biome parent. Nothing wants one before ZoneSystem is up, so an
        /// empty answer there is not a finding.</summary>
        private static Dictionary<string, int> Demand()
        {
            var demand = new Dictionary<string, int>();
            var zones = ZoneSystem.instance;
            if (zones == null || zones.m_locations == null) return demand;

            foreach (ZoneSystem.ZoneLocation location in zones.m_locations)
            {
                if (location == null || string.IsNullOrEmpty(location.AltBiomeParent)) continue;
                int seen;
                demand[location.AltBiomeParent] = demand.TryGetValue(location.AltBiomeParent, out seen) ? seen + 1 : 1;
            }
            return demand;
        }

        private static string Describe(Dictionary<string, int> counts)
        {
            if (counts.Count == 0) return "none";
            var ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort((left, right) => right.Value.CompareTo(left.Value));

            var text = new StringBuilder();
            for (int index = 0; index < ordered.Count && index < NamesListed; index++)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append($"{ordered[index].Key} x{ordered[index].Value}");
            }
            if (ordered.Count > NamesListed) text.Append($", and {ordered.Count - NamesListed} more");
            return text.ToString();
        }
    }
}
