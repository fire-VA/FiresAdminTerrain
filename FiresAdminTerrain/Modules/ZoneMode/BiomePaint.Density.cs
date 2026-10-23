using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // How many locations of one biome a stretch of land should get, shared by the territory location replace and the paint stroke.
    // First choice: this world's own density for the biome (its placed locations of that biome per zone of that biome, measured
    // once per world and cached). A world with none of the biome's locations (super flat plans none) falls back to the biome's
    // vanilla location table: the summed quantities of its locations spread over the share of a vanilla world that one biome gets.
    internal static partial class BiomePaint
    {
        internal sealed class LocationDensity
        {
            internal float PerZone;
            internal string Source = "this world's";
        }

        private const float VanillaWorldRadius = 10000f;
        private const float VanillaBiomeShare = 1f / 7f;          // about a seventh of a vanilla world's land per biome
        private const int DensityZonesPerStep = 512;              // zone centres sampled per job step while measuring

        private static readonly Dictionary<Heightmap.Biome, float> s_densityByBiome = new Dictionary<Heightmap.Biome, float>();
        private static ZoneSystem s_densityFor;

        // Fills `result` for `biome`; a time-sliced job step (res shows the phase).
        private static IEnumerator MeasureLocationDensity(ZoneSystem zs, WorldGenerator generator, Heightmap.Biome biome, ZoneJobResult res, LocationDensity result)
        {
            if (!ReferenceEquals(zs, s_densityFor)) { s_densityFor = zs; s_densityByBiome.Clear(); }
            if (!s_densityByBiome.TryGetValue(biome, out float density))
            {
                int half = Mathf.CeilToInt(VanillaWorldRadius / ZoneSystem.c_ZoneSize);
                int zonesOfBiome = 0, sampled = 0;
                res.Phase = "density"; res.Steps = (2 * half + 1) * (2 * half + 1);
                for (int z = -half; z <= half; z++)
                    for (int x = -half; x <= half; x++)
                    {
                        res.Step = ++sampled;
                        if (sampled % DensityZonesPerStep == 0) yield return null;
                        var centre = ZoneSystem.GetZonePos(new Vector2s(x, z));
                        if (centre.x * centre.x + centre.z * centre.z > VanillaWorldRadius * VanillaWorldRadius) continue;
                        if (generator.GetBiome(centre) == biome) zonesOfBiome++;
                    }
                int placed = 0;
                foreach (var instance in new List<ZoneSystem.LocationInstance>(zs.m_locationInstances.Values))
                {
                    var location = instance.m_location;
                    if (location == null || !Eligible(location) || (location.m_biome & biome) == Heightmap.Biome.None) continue;
                    if (generator.GetBiome(instance.m_position) == biome) placed++;
                }
                density = zonesOfBiome > 0 ? (float)placed / zonesOfBiome : 0f;
                s_densityByBiome[biome] = density;
                FiresLogger.LogInfo($"[BiomePaint] {biome} location density: {placed} location(s) over {zonesOfBiome} zone(s) = {density:0.000} per zone.");
            }
            result.PerZone = density;
            result.Source = "this world's";
            if (density > 0f) yield break;
            long quantity = 0;
            foreach (var location in s_locationPool)
                if (Eligible(location) && (location.m_biome & biome) != Heightmap.Biome.None) quantity += location.m_quantity;
            float vanillaZonesPerBiome = Mathf.PI * VanillaWorldRadius * VanillaWorldRadius / (ZoneSystem.c_ZoneSize * ZoneSystem.c_ZoneSize) * VanillaBiomeShare;
            result.PerZone = quantity / vanillaZonesPerBiome;
            result.Source = $"the {biome} location table's ({quantity} vanilla placements / {vanillaZonesPerBiome:0} zones)";
            FiresLogger.LogInfo($"[BiomePaint] {biome}: this world has none of its locations{(VerdantsAscent.Terrain.Voxel.FatSuperFlat.On ? " (super flat plans none)" : "")}; "
                + $"using {result.Source} density {result.PerZone:0.000} per zone.");
        }
    }
}
