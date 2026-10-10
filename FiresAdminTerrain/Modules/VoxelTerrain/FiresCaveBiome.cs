using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // A cave's own biome (FAT 0.2.797; Fire 10-06 via the lead: "a cave can be a different biome from the surface above it",
    // with a worldgen roll on new worlds). Caves are grouped in regions of RegionSize m; each region's biome is the surface
    // biome, or, for a share of regions (Variety), another biome allowed at that distance from the world centre. It is a
    // pure function of the world seed and the region, so every peer answers the same with nothing stored or synced, and it
    // is safe on any thread. A biome painted on a cave floor (VoxelLayerPaint) still wins where the point is readable.
    // Ocean, Ashlands and Deep North surfaces keep their biome underground (no swamp caves under the sea).
    //
    // Who reads it: WorldGenerator.GetBiome(Vector3) / GetBiomeSector(Vector3) for a point standing in a cave (the HUD, the
    // player's biome, music, weather and anything vanilla asks by position), the cave environment (VoxelCaveEnv, per-biome
    // map), cave clutter (VoxelCaveClutter) and cave content + spawners (VoxelCaveContent). `fat_biome_probe <x> <y> <z>`
    // logs every reader's answer at a point.
    internal static class FiresCaveBiome
    {
        private const string Section = "09 - World generation";
        internal static ConfigEntry<float> Variety;
        internal static ConfigEntry<string> Environments;
        private const float RegionSize = 192f, InnerRing = 3000f, OuterRing = 6000f, GroundMargin = 2f, AirProbe = 0.5f;
        private static int s_mainThread = -1;

        internal static void Bind(ConfigFile cfg)
        {
            s_mainThread = Thread.CurrentThread.ManagedThreadId;
            Variety = cfg.Bind(Section, "Cave biome variety", 0.25f,
                new ConfigDescription("Share of cave regions (192 m) whose biome differs from the surface above them, rolled from the "
                    + "world seed (the same on every peer). 0 = every cave is its surface's biome.", new AcceptableValueRange<float>(0f, 1f)));
            Environments = cfg.Bind(Section, "Cave environments", "",
                "Environment per cave biome, as Biome:Environment pairs separated by commas (e.g. Swamp:SwampRain,Mistlands:Mistlands). "
                + "A cave biome not listed uses the voxel 'Cave environment'.");
            Environments.SettingChanged += (_, __) => s_envMap = null;
        }

        private static readonly Heightmap.Biome[] Inner = { Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp };
        private static readonly Heightmap.Biome[] Middle = { Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.Plains };
        private static readonly Heightmap.Biome[] Outer = { Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands };

        // The biome of the cave region over (x, z), whose surface biome is `surface`. Thread-safe.
        internal static Heightmap.Biome RegionBiome(float x, float z, Heightmap.Biome surface)
        {
            float variety = Variety != null ? Variety.Value : 0f;
            if (variety <= 0f || surface == Heightmap.Biome.None || surface == Heightmap.Biome.Ocean
                || surface == Heightmap.Biome.AshLands || surface == Heightmap.Biome.DeepNorth) return surface;
            int rx = Mathf.FloorToInt(x / RegionSize), rz = Mathf.FloorToInt(z / RegionSize);
            int seed = WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0;
            if (Hash01(seed, rx, rz, 0x5EED) >= variety) return surface;
            float r = Mathf.Sqrt(x * x + z * z);
            var options = r < InnerRing ? Inner : r < OuterRing ? Middle : Outer;
            int start = (int)(Hash01(seed, rx, rz, 0xB10E) * options.Length) % options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                var pick = options[(start + i) % options.Length];
                if (pick != surface) return pick;
            }
            return surface;
        }

        // The biome of a point standing in a cave (in air, under a voxel roof, below the column's ground): painted, else its
        // region's. Main thread only (it reads live voxels); false anywhere else, and for points not in a cave.
        internal static bool TryCaveBiome(Vector3 p, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (Thread.CurrentThread.ManagedThreadId != s_mainThread || WorldGenerator.instance == null) return false;
            if (!InCave(p)) return false;
            if (VoxelLayerPaint.TryPaintedBiomeAt(p, out biome)) return true;
            biome = RegionBiome(p.x, p.z, WorldGenerator.instance.GetBiome(p.x, p.z));
            return true;
        }

        // In a cave: below the ground top by a margin, in air (a point given with y = 0 or inside rock is not), roofed.
        internal static bool InCave(Vector3 p)
        {
            if (!VoxelZoneStreamer.TryGroundHeight(p.x, p.z, out float ground) || p.y > ground - GroundMargin) return false;
            if (!VoxelWorld.TryIsSolid(p.x, p.y + AirProbe, p.z, out bool solid) || solid) return false;
            return FiresTerrainLayers.TryRoofAbove(p + Vector3.up * AirProbe, out _);
        }

        // The environment a cave of this biome forces (the 'Cave environments' map, else the voxel 'Cave environment').
        internal static string EnvironmentFor(Heightmap.Biome biome)
        {
            var map = s_envMap ??= ParseEnvironments();
            return map.TryGetValue(biome, out var env) ? env : FiresVoxelConfig.CaveEnvironment.Value;
        }

        private static Dictionary<Heightmap.Biome, string> s_envMap;

        private static Dictionary<Heightmap.Biome, string> ParseEnvironments()
        {
            var map = new Dictionary<Heightmap.Biome, string>();
            foreach (var pair in (Environments?.Value ?? "").Split(','))
            {
                int colon = pair.IndexOf(':');
                if (colon <= 0) continue;
                if (Enum.TryParse(pair.Substring(0, colon).Trim(), true, out Heightmap.Biome b) && pair.Length > colon + 1)
                    map[b] = pair.Substring(colon + 1).Trim();
            }
            return map;
        }

        private static float Hash01(int seed, int x, int z, int salt)
        {
            unchecked
            {
                uint h = (uint)(seed * 73856093 ^ x * 19349663 ^ z * 83492791 ^ salt * 2654435761);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        // ── fat_biome_probe <x> <y> <z>: what every reader answers at a point (read-only) ─────────────────────────────
        internal static void RegisterProbe()
        {
            new Terminal.ConsoleCommand("fat_biome_probe",
                "<x> <y> <z> - the biome every reader answers at a point: surface, cave region, GetBiome(position), sector, environment (read-only)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    if (args.Length < 4 || !float.TryParse(args[1], NumberStyles.Float, inv, out float x)
                        || !float.TryParse(args[2], NumberStyles.Float, inv, out float y) || !float.TryParse(args[3], NumberStyles.Float, inv, out float z))
                    { args.Context?.AddString("fat_biome_probe <x> <y> <z>"); return; }
                    var wg = WorldGenerator.instance;
                    if (wg == null) { args.Context?.AddString("no world"); return; }
                    var p = new Vector3(x, y, z);
                    var surface = wg.GetBiome(x, z);
                    bool cave = InCave(p);
                    bool painted = VoxelLayerPaint.TryPaintedBiomeAt(p, out var paint);
                    var region = RegionBiome(x, z, surface);
                    var position = wg.GetBiome(p);
                    var sector = wg.GetBiomeSector(p)?.Biome ?? Heightmap.Biome.None;
                    string line = string.Format(inv, "[CaveBiome] ({0:0.0},{1:0.0},{2:0.0}): surface {3}; in cave {4}; cave region {5}; painted {6}; "
                        + "GetBiome(position) {7}; GetBiomeSector(position) {8}; cave environment {9}",
                        x, y, z, surface, cave, region, painted ? paint.ToString() : "none", position, sector,
                        cave ? EnvironmentFor(painted ? paint : region) : "(not in a cave)");
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                },
                isCheat: true);
        }
    }
}
