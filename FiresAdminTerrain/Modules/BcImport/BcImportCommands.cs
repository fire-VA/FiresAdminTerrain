using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    /// <summary>
    /// `fires_bc_scan` and `fires_bc_maps` - reads this world's BetterContinents settings file with FAT's own
    /// reader and reports what is in it. Registered from FAT's one Terminal.InitTerminal hook
    /// (FatVanillaHooks). Spec: Docs/BETTERCONTINENTS_IMPORT.md
    /// </summary>
    internal static class BcImportCommands
    {
        private const string ScanCommand = "fires_bc_scan";
        private const string MapsCommand = "fires_bc_maps";

        private static bool s_registered;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;

            new Terminal.ConsoleCommand(ScanCommand,
                "What BetterContinents settings this world carries. Usage: " + ScanCommand
                + " [worldName] - defaults to the loaded world.",
                args => Report(Describe(WorldNameFrom(args), false), args),
                isCheat: true);

            new Terminal.ConsoleCommand(MapsCommand,
                "As " + ScanCommand + ", and decodes every embedded map to report its size and biome coverage."
                + " Usage: " + MapsCommand + " [worldName]",
                args => Report(Describe(WorldNameFrom(args), true), args),
                isCheat: true);

            new Terminal.ConsoleCommand(HeightCommand,
                "How BetterContinents' biome height shapes the ground here (read-only). Usage: " + HeightCommand
                + " [x z] - defaults to where you stand.",
                args => Report(HeightAt(args), args),
                isCheat: true);

            RegisterDnCensus();   // 0.2.1205
            BcRegenCommand.Register();   // 0.2.1216
            global::VerdantsAscent.Terrain.FatMeshAtCommand.Register();   // 0.2.1207
        }

        private const string HeightCommand = "fires_bc_height";
        private const string DnCensusCommand = "fires_bc_dn_census";

        // 0.2.1205 (lead / Fire 10-28: "a deep North update on the VA DN world"): READ-ONLY. Which zones read Deep North through the LIVE
        // generator (the patched GetBiome: BC's biome map where it paints, vanilla's rules under its black pixels), so the Deep North
        // update knows its zones before anything writes. 5 samples a zone (centre + 4 points 16 m in from the corners) over the
        // playable radius, spread over frames (8 ms a frame). Writes nothing to the world: the zone list goes to a text file in
        // FAT's cache folder; one Info line with the totals and one Debug proof line.
        internal static void RegisterDnCensus()
        {
            new Terminal.ConsoleCommand(DnCensusCommand,
                "Read-only: lists the zones that read Deep North through the live generator (BetterContinents map + vanilla underneath) "
                + "and writes the list to FAT's cache folder. Usage: " + DnCensusCommand,
                args =>
                {
                    if (s_censusRunning) { args.Context?.AddString(DnCensusCommand + ": already running."); return; }
                    if (WorldGenerator.instance == null || Game.instance == null) { args.Context?.AddString(DnCensusCommand + ": no world loaded."); return; }
                    args.Context?.AddString(DnCensusCommand + ": started (read-only; spread over frames, result in the log).");
                    Game.instance.StartCoroutine(DnCensus());
                },
                isCheat: true);
        }

        private static bool s_censusRunning;
        private const string FatVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;

        private static System.Collections.IEnumerator DnCensus()
        {
            s_censusRunning = true;
            var wg = WorldGenerator.instance;
            var gen = BcMigratedWorld.Generator;
            string world = ZNet.instance?.GetWorldName() ?? "?";
            float radius = gen != null ? gen.Settings.WorldSize : 10000f;
            int zr = Mathf.CeilToInt(radius / ZoneSystem.c_ZoneSize);
            var clock = Stopwatch.StartNew();
            var frame = Stopwatch.StartNew();
            int zones = 0, dn = 0, dnMap = 0, dnVanilla = 0, samples = 0;
            var list = new StringBuilder();
            list.AppendLine($"# {DnCensusCommand} {world} {DateTime.Now:yyyy-MM-dd HH:mm} FAT {FatVersion}: BcImport active {BcMigratedWorld.Active}"
                + (gen != null ? $", settings v{gen.Settings.Version}" : "") + $", seed {wg.GetSeed()}; zone x,z  samples-DN/5  source");
            for (int zx = -zr; zx <= zr; zx++)
            {
                for (int zz = -zr; zz <= zr; zz++)
                {
                    Vector3 c = ZoneSystem.GetZonePos(new Vector2s((short)zx, (short)zz));
                    if (new Vector2(c.x, c.z).magnitude > radius + ZoneSystem.c_ZoneSize) continue;
                    zones++;
                    int hit = 0; bool fromMap = false;
                    for (int s = 0; s < 5; s++)
                    {
                        float x = c.x + (s == 0 ? 0f : (s == 1 || s == 3 ? -16f : 16f)), z = c.z + (s == 0 ? 0f : (s <= 2 ? -16f : 16f));
                        samples++;
                        if (wg.GetBiome(x, z) != Heightmap.Biome.DeepNorth) continue;
                        hit++;
                        if (BcMigratedWorld.TryGetBiome(x, z, out var painted) && painted == Heightmap.Biome.DeepNorth) fromMap = true;
                    }
                    if (hit > 0)
                    {
                        dn++; if (fromMap) dnMap++; else dnVanilla++;
                        list.AppendLine($"{zx},{zz}  {hit}/5  {(fromMap ? "map" : "vanilla-rule")}");
                    }
                    if (frame.ElapsedMilliseconds >= 8) { yield return null; frame.Restart(); if (WorldGenerator.instance != wg) { s_censusRunning = false; yield break; } }
                }
            }
            string path = null;
            try
            {
                string dir = global::VerdantsAscent.FiresCachePaths.For("FiresAdminTerrain", "Census");
                System.IO.Directory.CreateDirectory(dir);
                path = System.IO.Path.Combine(dir, $"dn_census_{string.Concat(world.Split(System.IO.Path.GetInvalidFileNameChars()))}.txt");
                System.IO.File.WriteAllText(path, list.ToString());
            }
            catch (Exception ex) { path = "(not written: " + ex.Message + ")"; }
            FiresLogger.LogInfo($"[BcImport] {DnCensusCommand} '{world}': {dn} of {zones} zone(s) read Deep North ({dnMap} from the BetterContinents map, {dnVanilla} by vanilla's rule under unpainted pixels); list: {path}");
            FiresLogger.LogDebug($"[BcImport] {DnCensusCommand} proof: BcImport active {BcMigratedWorld.Active}{(gen != null ? $", settings v{gen.Settings.Version}, world {gen.Settings.WorldSize}" : " (NOT a converted world here: this is vanilla's answer)")}, seed {wg.GetSeed()}, {samples} GetBiome sample(s) through the live (patched) generator in {clock.ElapsedMilliseconds} ms; read-only, nothing written to the world (0.2.1205)");
            s_censusRunning = false;
        }

        // The ground at one spot, BC side by side with vanilla: BC's smooth height (base x 200), the roughmap weight, the
        // generated height (with BC's blend), vanilla's own biome height (solved back from the blend), and the live
        // ground (with the save's terrain edits). Evidence for the roughmap fix.
        private static List<string> HeightAt(Terminal.ConsoleEventArgs args)
        {
            var lines = new List<string>();
            var gen = BcMigratedWorld.Generator;
            var wg = WorldGenerator.instance;
            if (gen == null || wg == null) { lines.Add($"{HeightCommand}: this world is not a converted BetterContinents world here."); return lines; }
            Vector3 p = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (args.Length > 2 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, inv, out float ax)
                && float.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out float az))
                p = new Vector3(ax, 0f, az);
            Heightmap.Biome biome = wg.GetBiome(p.x, p.z);
            float generated = wg.GetBiomeHeight(biome, p.x, p.z, out Color _);
            float smooth = wg.GetBaseHeight(p.x, p.z, false) * 200f;
            var s = gen.Settings;
            float k = gen.Maps?.Rough != null ? gen.Maps.Rough.GetValue(s.Normalize(p.x), s.Normalize(p.z)) * s.RoughmapBlend : -1f;
            string vanilla = s.HeightMapOverridesEverything ? "not used (Override All)"
                : k < 0f ? "= generated (no roughmap)"
                : k > 0.01f ? ((generated - smooth * (1f - k)) / k).ToString("0.000", inv) : "not solvable (roughmap ~0 here)";
            string live = Heightmap.GetHeight(p, out float h) ? h.ToString("0.000", inv) : "zone not loaded";
            lines.Add(string.Format(inv, "{0} at ({1:0.0}, {2:0.0}) {3}: BC smooth {4:0.000}, roughmap weight {5}, generated {6:0.000}, vanilla biome height {7}, live ground {8}",
                HeightCommand, p.x, p.z, biome, smooth, k < 0f ? "none" : k.ToString("0.000", inv), generated, vanilla, live));
            return lines;
        }

        private static string WorldNameFrom(Terminal.ConsoleEventArgs args) =>
            args.Length > 1 ? args[1] : ZNet.instance?.GetWorldName();

        private static void Report(List<string> lines, Terminal.ConsoleEventArgs args)
        {
            var log = new StringBuilder();
            foreach (string line in lines)
            {
                args.Context?.AddString(line);
                log.AppendLine(line);
            }
            FiresLogger.LogInfo("[BcImport]\n" + log);
        }

        private static List<string> Describe(string worldName, bool decodeMaps)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(worldName))
            {
                lines.Add("No world loaded, and no world name given. Usage: " + ScanCommand + " [worldName]");
                return lines;
            }

            List<BcSaveLocation> found = BcSaveLocator.FindAll(worldName);
            lines.Add($"world '{worldName}': {found.Count} BetterContinents file(s) found");
            foreach (BcSaveLocation location in found) lines.Add("  " + location.Describe());
            if (found.Count == 0)
            {
                lines.Add("  Nothing to convert: this world has no BetterContinents settings in any storage root.");
                return lines;
            }

            BcSaveLocation chosen = found[0];
            var timer = Stopwatch.StartNew();
            if (!BcSaveReader.TryReadFile(chosen, out BcWorldSettings settings, out string failure))
            {
                lines.Add("REFUSED: " + failure);
                return lines;
            }
            lines.Add($"read {chosen.Kind} in {timer.ElapsedMilliseconds} ms: version {settings.Version}, "
                + (settings.EnabledForThisWorld ? "enabled for this world" : "DISABLED for this world"));
            if (!settings.EnabledForThisWorld) return lines;

            DescribeSettings(settings, lines);
            if (decodeMaps) DescribeMaps(settings, lines);
            else lines.Add($"run {MapsCommand} to decode the maps as well");
            return lines;
        }

        private static void DescribeSettings(BcWorldSettings settings, List<string> lines)
        {
            lines.Add($"world size {settings.WorldSize} + edge {settings.EdgeSize} -> total radius {settings.TotalRadius}");
            lines.Add($"scale {settings.GlobalScale}  sea level {settings.SeaLevelAdjustment}  mountains {settings.MountainsAmount}");
            lines.Add($"ridges: max height {settings.MaxRidgeHeight}  scale {settings.RidgeScale}  "
                + $"sigmoid b {settings.RidgeBlendSigmoidB} x-offset {settings.RidgeBlendSigmoidXOffset}");
            lines.Add($"heightmap: amount {settings.HeightmapAmount}  blend {settings.HeightmapBlend}  "
                + $"add {settings.HeightmapAdd}  mask {settings.HeightmapMask}  "
                + $"overrides everything {settings.HeightMapOverridesEverything}  alpha {settings.HeightMapAlpha}");
            lines.Add($"rough blend {settings.RoughmapBlend}  flat blend {settings.FlatmapBlend}  "
                + $"rough inverted as flat {settings.UseRoughInvertedAsFlat}");
            lines.Add($"forest: scale {settings.ForestScale}  offset {settings.ForestAmountOffset}  "
                + $"multiply {settings.ForestmapMultiply}  add {settings.ForestmapAdd}  "
                + $"override all trees {settings.ForestFactorOverrideAllTrees}");
            lines.Add($"rivers {settings.RiversEnabled}  ocean channels {settings.OceanChannelsEnabled}  "
                + $"edge dropoff {!settings.DisableMapEdgeDropoff}  mountains at centre {settings.MountainsAllowedAtCenter}");
            lines.Add($"biome precision {settings.BiomePrecision}  heat scale {settings.HeatMapScale}  "
                + $"ashlands gap {settings.AshlandsGapEnabled}  deep north gap {settings.DeepNorthGapEnabled}");
            lines.Add(settings.OverrideStartPosition
                ? $"start position forced to ({settings.StartPositionX}, {settings.StartPositionY})"
                : "start position from the world generator");
            lines.Add($"skip default locations {settings.SkipDefaultLocations}  fix water colour {settings.FixWaterColor}  "
                + $"noise layers {settings.BaseHeightNoise.Layers.Count}");

            lines.Add("maps present: " + string.Join(", ", MapNames(settings).ToArray()));
            if (settings.LocationMap != null)
                lines.Add($"location map: {settings.LocationMap.RemainingAreas.Count} group(s)");
        }

        private static List<string> MapNames(BcWorldSettings settings)
        {
            var names = new List<string>();
            AddIfPresent(names, "height", settings.HeightMap);
            AddIfPresent(names, "rough", settings.RoughMap);
            AddIfPresent(names, "flat", settings.FlatMap);
            AddIfPresent(names, "forest", settings.ForestMap);
            AddIfPresent(names, "heat", settings.HeatMap);
            AddIfPresent(names, "lava", settings.LavaMap);
            AddIfPresent(names, "moss", settings.MossMap);
            AddIfPresent(names, "terrain", settings.TerrainMap);
            AddIfPresent(names, "paint", settings.PaintMap);
            if (settings.BiomeIndexMap != null) names.Add($"biome ({settings.BiomeIndexMap.Indices.Length} index bytes)");
            if (settings.LegacyBiomeImage != null) names.Add($"biome image ({settings.LegacyBiomeImage.Data.Length} B)");
            if (settings.SpawnMap != null) names.Add($"spawn ({settings.SpawnMap.Palette.Count} palette entries)");
            if (settings.VegetationMap != null) names.Add($"vegetation ({settings.VegetationMap.Palette.Count} palette entries)");
            if (names.Count == 0) names.Add("none");
            return names;
        }

        private static void AddIfPresent(List<string> names, string name, BcMapImage image)
        {
            if (image?.Data == null || image.Data.Length == 0) return;
            names.Add($"{name} ({image.Data.Length} B)");
        }

        private static void DescribeMaps(BcWorldSettings settings, List<string> lines)
        {
            var timer = Stopwatch.StartNew();
            if (!BcImageMaps.TryDecode(settings, out BcDecodedMaps maps, out string failure))
            {
                lines.Add("MAP DECODE FAILED: " + failure);
                return;
            }
            lines.Add($"decoded in {timer.ElapsedMilliseconds} ms");

            DescribeFloatMap(lines, "height", maps.Height);
            DescribeFloatMap(lines, "rough", maps.Rough);
            DescribeFloatMap(lines, "flat", maps.Flat);
            DescribeFloatMap(lines, "forest", maps.Forest);
            DescribeFloatMap(lines, "heat", maps.Heat);
            DescribeFloatMap(lines, "lava", maps.Lava);
            DescribeFloatMap(lines, "moss", maps.Moss);
            if (maps.Terrain != null) lines.Add($"terrain map {maps.Terrain.Size} x {maps.Terrain.Size}");
            if (maps.Paint != null) lines.Add($"paint map {maps.Paint.Size} x {maps.Paint.Size}");
            if (maps.Biome != null) DescribeBiomeCoverage(lines, maps.Biome);
        }

        private static void DescribeFloatMap(List<string> lines, string name, BcFloatMap map)
        {
            if (map == null) return;
            float lowest = float.MaxValue;
            float highest = float.MinValue;
            double total = 0.0;
            for (int index = 0; index < map.Count; index++)
            {
                float value = map[index];
                if (value < lowest) lowest = value;
                if (value > highest) highest = value;
                total += value;
            }
            lines.Add($"{name} map {map.Size} x {map.Size}: min {lowest:0.####} max {highest:0.####} "
                + $"mean {total / map.Count:0.####}");
        }

        private static void DescribeBiomeCoverage(List<string> lines, BcBiomeMap map)
        {
            var counts = new Dictionary<Heightmap.Biome, int>();
            foreach (Heightmap.Biome biome in map.Biomes)
            {
                counts.TryGetValue(biome, out int count);
                counts[biome] = count + 1;
            }

            lines.Add($"biome map {map.Size} x {map.Size}, {counts.Count} distinct biome(s):");
            foreach (KeyValuePair<Heightmap.Biome, int> entry in counts)
                lines.Add($"   {entry.Key} ({(int)entry.Key}): {entry.Value * 100f / map.Biomes.Length:0.##}%");
        }
    }
}
