using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // A cave's own biome (a cave can be a different biome from the surface above it,
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
        internal static int s_mainThread = -1;

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
        // InCave was 68 % of FindHeightmap. Answers are cached per 1 m cell for 1 s; same cell = same answer.
        private static readonly Dictionary<Vector3Int, bool> s_inCave = new Dictionary<Vector3Int, bool>();
        private static float s_inCaveAt = -1000f; private static int s_inCaveCalls, s_inCaveComputed; private static float s_inCaveLogAt;

        internal static bool InCave(Vector3 p)
        {
            if (Thread.CurrentThread.ManagedThreadId != s_mainThread) return InCaveUncached(p);   // the cache is main-thread only
            if (Time.time - s_inCaveAt >= 1f || s_inCave.Count > 4096) { s_inCave.Clear(); s_inCaveAt = Time.time; }
            s_inCaveCalls++;
            if ((s_inCaveCalls & 1023) == 0) TallyCaller();   // Who asks 14k times a second (sampled 1 in 1024)
            if (Time.unscaledTime >= s_inCaveLogAt)
            {
                if (s_inCaveLogAt > 0f) FiresLogger.LogDebug($"[FiresCaveBiome] InCave /60s: {s_inCaveCalls} calls, {s_inCaveComputed} evaluated, {s_inCaveTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00} ms; player state {s_playerEvals} evaluations");
                if (s_callers.Count > 0) { var top = new List<KeyValuePair<string, int>>(s_callers); top.Sort((a, b) => b.Value.CompareTo(a.Value)); var sb = new System.Text.StringBuilder("[FiresCaveBiome] InCave callers (1 in 1024 sampled): "); for (int k = 0; k < top.Count && k < 6; k++) sb.Append(top[k].Key).Append(' ').Append(top[k].Value).Append("; "); FiresLogger.LogDebug(sb.ToString()); s_callers.Clear(); }
                s_inCaveCalls = s_inCaveComputed = s_playerEvals = 0; s_inCaveTicks = 0; s_inCaveLogAt = Time.unscaledTime + 60f;
            }
            var key = Vector3Int.FloorToInt(p);
            if (s_inCave.TryGetValue(key, out bool known)) return known;
            s_inCaveComputed++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool r = s_inCave[key] = InCaveUncached(p);
            s_inCaveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            return r;
        }
        private static long s_inCaveTicks;
        private static readonly Dictionary<string, int> s_callers = new Dictionary<string, int>();

        // The first frame outside FAT's cave/biome plumbing, as Type.Method (plus the FAT frame that led there).
        private static void TallyCaller()
        {
            var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
            if (frames == null) return;
            string fat = null, outside = null;
            foreach (var fr in frames)
            {
                var m = fr.GetMethod(); var t = m?.DeclaringType;
                if (t == null) continue;
                string n = t.Name + "." + m.Name;
                if (t.Namespace != null && t.Namespace.StartsWith("VerdantsAscent")) { if (fat == null && !n.StartsWith("FiresCaveBiome")) fat = n; continue; }
                if (n.StartsWith("DMD") || t.Name.StartsWith("<")) continue;
                outside = n; break;
            }
            string key = (outside ?? "?") + " via " + (fat ?? "FiresCaveBiome");
            s_callers.TryGetValue(key, out int c); s_callers[key] = c + 1;
        }

        // The cave check does not need to run constantly. The local player's cave state, one answer shared
        // by every per-player reader (cave environment, ambience): re-evaluated once a second, flat (the environment fades in and
        // out anyway, so the lazy check is enough).
        private static float s_playerNext;
        private static bool s_playerIn; private static Heightmap.Biome s_playerBiome; private static int s_playerEvals;

        internal static bool TryPlayerCaveBiome(Vector3 p, out Heightmap.Biome biome)
        {
            float now = Time.time;
            if (now >= s_playerNext)
            {
                s_playerNext = now + 1f; s_playerEvals++;
                s_playerIn = TryCaveBiome(p, out s_playerBiome);
            }
            biome = s_playerBiome;
            return s_playerIn;
        }

        private static bool InCaveUncached(Vector3 p)
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
