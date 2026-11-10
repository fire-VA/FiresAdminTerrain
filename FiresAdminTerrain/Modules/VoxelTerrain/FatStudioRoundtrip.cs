using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1166 (Fire picked World Gen Studio steps 1-3: "studio == game"): `fat_studio_roundtrip [manifest]` reads the sample
    // manifest World Gen Studio writes beside each import bake (<bake>.samples.txt; FiresGaeaStudio.Install copies it beside the
    // world's baked cache) and compares, per point, the game's generated height, surface biome and generated water with what the
    // Studio baked. No argument = the loaded world's own manifest. Read-only: it writes nothing but log lines.
    internal static class FatStudioRoundtrip
    {
        internal const string ManifestExtension = ".samples.txt";
        private const string Tag = "[StudioRoundtrip]";

        internal static string ManifestFor(string worldName) => Path.ChangeExtension(FiresWorldGenBase.BakedCachePath(worldName), ManifestExtension);

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fat_studio_roundtrip",
                "[manifest path] - compare World Gen Studio's baked sample points with the loaded world (height, biome, water). No path = this world's manifest.",
                args =>
                {
                    string line;
                    try { line = Run(args.Length > 1 ? string.Join(" ", args.Args, 1, args.Length - 1).Trim('"') : null); }
                    catch (Exception ex) { line = $"{Tag} failed: {ex.Message}"; }
                    FiresLogger.LogInfo(line); args.Context?.AddString(line);
                }, isCheat: true);
        }

        private struct Miss { public string Kind; public float X, Z, Err; public string What; }

        private static string Run(string path)
        {
            var wg = WorldGenerator.instance;
            if (wg == null || ZNet.instance == null) return $"{Tag} join a world first.";
            if (string.IsNullOrEmpty(path)) path = ManifestFor(ZNet.instance.GetWorldName());
            if (!File.Exists(path)) return $"{Tag} no manifest at '{path}' (World Gen Studio writes <bake>.samples.txt with each import bake).";
            var inv = CultureInfo.InvariantCulture;
            int n = 0, biomeMiss = 0, waterMiss = 0, heightN = 0, waterN = 0;
            double hSum = 0, wSum = 0; float hMax = 0f, wMax = 0f;
            var worst = new List<Miss>();
            string header = null;
            foreach (string raw in File.ReadLines(path))
            {
                if (raw.StartsWith("#")) { if (header == null && raw.Contains("StudioRoundtrip")) header = raw; continue; }
                var f = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 6) continue;
                string kind = f[0];
                float x = float.Parse(f[1], inv), z = float.Parse(f[2], inv), h = float.Parse(f[3], inv), wl = float.Parse(f[5], inv);
                string biome = f[4];
                n++;
                // Height: the generated ground FAT builds the voxel world from (no brush edits), else vanilla's generator.
                float gh = FiresWorldGenBase.TryGetGenGround(x, z, out float g) ? g : wg.GetHeight(x, z);
                float he = Mathf.Abs(gh - h);
                hSum += he; heightN++; if (he > hMax) hMax = he;
                worst.Add(new Miss { Kind = kind, X = x, Z = z, Err = he, What = $"height studio {h:0.00} game {gh:0.00}" });
                // Biome: the surface biome (the 1145+ resolver behind the float overload).
                string gb = wg.GetBiome(x, z).ToString();
                if (!string.Equals(gb, biome, StringComparison.OrdinalIgnoreCase))
                {
                    biomeMiss++;
                    worst.Add(new Miss { Kind = kind, X = x, Z = z, Err = 1000f, What = $"biome studio {biome} game {gb}" });
                }
                // Water: inland generated water (rivers / lakes). The ocean is vanilla's sea, not compared.
                bool studioWet = wl > h + 0.05f && wl > ZoneSystem.instance.m_waterLevel + 0.05f;
                bool gameWet = FatApi.TryWaterAt(x, z, out float level, out _);
                if (studioWet != gameWet)
                {
                    waterMiss++;
                    worst.Add(new Miss { Kind = kind, X = x, Z = z, Err = 999f, What = $"water studio {(studioWet ? $"wet {wl:0.00}" : "dry")} game {(gameWet ? $"wet {level:0.00}" : "dry")}" });
                }
                else if (studioWet)
                {
                    float we = Mathf.Abs(level - wl);
                    wSum += we; waterN++; if (we > wMax) wMax = we;
                    worst.Add(new Miss { Kind = kind, X = x, Z = z, Err = we, What = $"water level studio {wl:0.00} game {level:0.00}" });
                }
            }
            if (n == 0) return $"{Tag} '{path}' has no sample points.";
            worst.Sort((a, b) => b.Err.CompareTo(a.Err));
            FiresLogger.LogDebug($"{Tag} manifest '{path}' ({header ?? "no header"})");
            for (int i = 0; i < Math.Min(10, worst.Count); i++)
            {
                var m = worst[i];
                FiresLogger.LogDebug($"{Tag} worst {i + 1}: {m.Kind} ({m.X:0.0},{m.Z:0.0}) {m.What}");
            }
            FiresLogger.LogDebug($"{Tag} per category: height avg {(heightN > 0 ? hSum / heightN : 0):0.000} max {hMax:0.000} m over {heightN}; "
                + $"biome mismatches {biomeMiss}/{n}; water wet/dry mismatches {waterMiss}, level avg {(waterN > 0 ? wSum / waterN : 0):0.000} max {wMax:0.000} m over {waterN} wet point(s)");
            return $"{Tag} {n} points: height max {hMax:0.00} avg {(heightN > 0 ? hSum / heightN : 0):0.00} m, biome mismatches {biomeMiss}, "
                + $"water max {wMax:0.00} m ({waterMiss} wet/dry mismatch(es))";
        }
    }
}
