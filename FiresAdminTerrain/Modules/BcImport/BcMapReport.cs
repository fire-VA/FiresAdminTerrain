using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    // What a migrated world's BetterContinents data actually holds, said once when it loads.
    //
    // Without this a missing map is invisible. BcImageMaps only logs when a decode FAILS, and an ABSENT map does
    // not fail - BcWorldGen.MapBiomeAt returns Heightmap.Biome.None when _maps.Biome is null, which is the
    // same answer it gives for a black pixel on a real map. Vanilla's ring rules then take over, so biomes end up
    // where vanilla would put them on a map whose land is somewhere else entirely. The visible symptom is
    // location placement: 'GoblinHut02 placed 1 out of 30 with 56,583' is a Plains location failing 56,583 times
    // because there is no Plains where it is allowed to look.
    //
    // The two cases are NOT the same and the report must not conflate them. A black pixel is deliberate - it
    // means "keep supplying the height here, let vanilla or a biome mod decide the biome", which is how a custom
    // Expand World biome shows through a BetterContinents map. No biome map at all means every point behaves that
    // way whether the author wanted it to or not.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal static class BcMapReport
    {
        private const int CensusSamples = 128;
        private const float PercentScale = 100f;
        private const string Absent = "-";

        // Black is not "unpainted". In a BetterContinents biome map it is the deliberate "leave this to whatever
        // biome is underneath" marker - the map keeps supplying the HEIGHT there while vanilla, or a biome mod
        // like Expand World Biomes, decides the biome. Calling it NOT PAINTED in a report would read as damage.
        private const string VanillaUnderneath = "vanilla underneath (black)";

        internal static void Log(string worldName, BcWorldSettings settings, BcDecodedMaps maps, BcWorldGen generator)
        {
            try
            {
                FiresLogger.LogInfo($"[BcImport] '{worldName}' maps: {Describe(maps)}");

                if (maps == null || maps.Biome == null)
                {
                    FiresLogger.LogWarning($"[BcImport] '{worldName}' has NO biome map, so biomes come from vanilla's "
                        + "ring rules laid over BetterContinents land. Expect locations to place badly or not at all "
                        + "where the two disagree - a Plains location cannot place if vanilla puts no Plains where "
                        + "this map has its land.");
                    return;
                }

                FiresLogger.LogInfo($"[BcImport] '{worldName}' biome census, {CensusSamples}x{CensusSamples} samples "
                    + $"across {settings.WorldSize * 2f}m: {Census(settings, generator)}");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BcImport] '{worldName}': the map report threw and was skipped "
                    + $"({ex.GetType().Name}: {ex.Message}). Nothing else is affected.");
            }
        }

        private static string Describe(BcDecodedMaps maps)
        {
            if (maps == null) return "none decoded";
            var parts = new List<string>
            {
                Float("height", maps.Height),
                Float("rough", maps.Rough),
                Float("flat", maps.Flat),
                Float("forest", maps.Forest),
                Float("heat", maps.Heat),
                Float("lava", maps.Lava),
                Float("moss", maps.Moss),
                maps.Biome != null ? $"biome {maps.Biome.Size}" : $"biome {Absent}",
                maps.Terrain != null ? "terrain yes" : $"terrain {Absent}",
                maps.Paint != null ? "paint yes" : $"paint {Absent}",
                maps.Spawn != null ? "spawn yes" : $"spawn {Absent}",
                maps.Vegetation != null ? "vegetation yes" : $"vegetation {Absent}",
                maps.Location != null ? "location yes" : $"location {Absent}",
            };
            return string.Join(", ", parts.ToArray());
        }

        private static string Float(string name, BcFloatMap map)
            => map != null ? $"{name} {map.Size}" : $"{name} {Absent}";

        // Samples the biome field the way GetBiome does, so this is what the game will actually see rather than
        // what the image file happens to contain.
        private static string Census(BcWorldSettings settings, BcWorldGen generator)
        {
            var counts = new Dictionary<Heightmap.Biome, int>();
            float half = settings.WorldSize;
            float step = half * 2f / (CensusSamples - 1);
            int total = 0;

            for (int row = 0; row < CensusSamples; row++)
            {
                float wy = -half + row * step;
                for (int column = 0; column < CensusSamples; column++)
                {
                    float wx = -half + column * step;
                    Heightmap.Biome biome = generator.MapBiomeAt(settings.Normalize(wx), settings.Normalize(wy));
                    int seen;
                    counts[biome] = counts.TryGetValue(biome, out seen) ? seen + 1 : 1;
                    total++;
                }
            }

            var ordered = new List<KeyValuePair<Heightmap.Biome, int>>(counts);
            ordered.Sort((left, right) => right.Value.CompareTo(left.Value));

            var text = new StringBuilder();
            foreach (var entry in ordered)
            {
                if (text.Length > 0) text.Append(", ");
                string name = entry.Key == Heightmap.Biome.None ? VanillaUnderneath : entry.Key.ToString();
                text.Append($"{name} {entry.Value * PercentScale / total:0.0}%");
            }
            return text.ToString();
        }
    }
}
