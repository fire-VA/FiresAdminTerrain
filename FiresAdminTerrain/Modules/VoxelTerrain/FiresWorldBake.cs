using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Terrain.BcImport;

namespace VerdantsAscent.Terrain.Voxel
{
    // Turns a world's HEIGHTMAP into a voxel field, once, as part of converting it - so "full voxel" means the
    // ground was converted rather than wrapped at runtime. The same path serves every source, because the source
    // is only ever "whatever this world's heights are": vanilla's generator on a stock world, BetterContinents on
    // a migrated one, the Fires generator on a generated one. FAT's own GetBaseHeight/GetBiome patches already
    // route the right answer, so nothing here special-cases them.
    //
    // The output is the same file an offline WorldGen Studio bake produces - FiresWorldGenBase.SaveImportedCache,
    // loaded by world NAME and seed-agnostic - which is why a converted world loads instantly afterwards and why
    // an already-baked world (Craters had one) looked correct before any of this existed.
    //
    // It does NOT call WorldGenerator.Initialize. That is hooked by FAT and would start a generator init for a
    // world with no grid yet; the private constructor gives a fully formed generator (it sets its own offsets)
    // while WorldGenerator.instance stays whatever the menu is using.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal static class FiresWorldBake
    {
        // A pure vanilla world is procedural and has no native grid, so there is nothing to match: this is the
        // configured resolution, whose default of 10501 is 2 m cells over the 21 km world.
        private const int FallbackResolution = 10501;
        private const float VanillaWorldRadius = 10000f;
        private const int RowsPerYield = 16;
        private const float PercentScale = 100f;

        private static readonly Heightmap.Biome[] CodeOrder =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp,
            Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands,
            Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean,
        };

        /// <summary>Bakes <paramref name="world"/>'s own terrain into its voxel cache, reporting progress as it
        /// goes. Runs as a coroutine because a 16 to 110 million sample pass on the main thread with no yield is
        /// indistinguishable from the game having hung.</summary>
        internal static IEnumerator Run(World world, Action<string> progress, Action<bool, string> done)
        {
            if (world == null) { done(false, "There is no world to bake."); yield break; }

            int resolution = ResolutionFor(world.m_name, out float half, out string sourceKind);
            long cells = (long)resolution * resolution;
            var timer = Stopwatch.StartNew();
            progress($"Converting '{world.m_name}' terrain to voxel: {resolution} x {resolution} from {sourceKind}...");
            yield return null;

            WorldGenerator generator;
            try
            {
                generator = (WorldGenerator)Activator.CreateInstance(typeof(WorldGenerator),
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { world }, null);
            }
            catch (Exception ex)
            {
                done(false, $"The terrain could not be read to bake it ({ex.GetType().Name}: {ex.Message}).");
                yield break;
            }

            // The world's own data decides its heights: with its record loaded, FAT's GetBaseHeight patch answers
            // from BetterContinents for a migrated world and passes through to vanilla for a stock one.
            BcMigratedWorld.OnWorldInitialize(world);

            var heights = new float[cells];
            var biomes = new byte[cells];
            float cell = half * 2f / (resolution - 1);
            bool failed = false;
            string failure = null;

            for (int row = 0; row < resolution; row++)
            {
                try
                {
                    float wy = -half + row * cell;
                    int at = row * resolution;
                    for (int column = 0; column < resolution; column++)
                    {
                        float wx = -half + column * cell;
                        heights[at + column] = generator.GetHeight(wx, wy);
                        biomes[at + column] = CodeFor(generator.GetBiome(wx, wy));
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    failure = $"sampling row {row} of {resolution} threw ({ex.GetType().Name}: {ex.Message})";
                    break;
                }

                if (row % RowsPerYield != 0) continue;
                progress($"Converting terrain to voxel: row {row:N0} of {resolution:N0} "
                    + $"({row * PercentScale / resolution:0.0}%, {timer.Elapsed.TotalSeconds:0}s)");
                yield return null;
            }

            BcMigratedWorld.Reset();
            if (failed) { done(false, $"The terrain was NOT converted: {failure}. Nothing was written."); yield break; }

            progress($"Solving water over {cells:N0} cells...");
            yield return null;

            try
            {
                // 0.2.804: a converted world plans rivers like a generated one, so it gets the big sinkhole cuts too.
                var profile = FiresWorldProfile.Read(world.m_name);
                FatSinkFallCuts.Arm(FiresWorldRecords.PathFor(world.m_name, FatSinkFallCuts.Suffix), profile?.Caves ?? (FiresVoxelConfig.GenerateCaves != null && FiresVoxelConfig.GenerateCaves.Value));
                float[] waterLevel;
                byte[] riverMask;
                try
                {
                    FiresWorldGenBase.PlanImportedWorld(heights, biomes, null, resolution, half, world.m_seed,
                        out waterLevel, out riverMask);
                }
                finally { FatSinkFallCuts.Disarm(); }
                FiresWorldGenBase.SaveImportedCache(FiresWorldGenBase.BakedCachePath(world.m_name),
                    heights, biomes, riverMask, waterLevel, resolution, half);
            }
            catch (Exception ex)
            {
                done(false, $"The terrain was sampled but its cache could not be written "
                    + $"({ex.GetType().Name}: {ex.Message}). The world is unchanged apart from that.");
                yield break;
            }

            done(true, $"Converted '{world.m_name}' terrain to voxel: {resolution} x {resolution} from {sourceKind}, "
                + $"{cells:N0} cells in {timer.Elapsed.TotalSeconds:0.#}s.");
        }

        /// <summary>The world the save system knows by that name, matched on either of the two names a world
        /// carries, so a copy is found whether or not its save file name differs from the name inside it.</summary>
        internal static bool TryFindWorld(string worldName, out World world)
        {
            world = null;
            foreach (var candidate in SaveSystem.GetWorldList())
            {
                if (candidate == null) continue;
                if (!string.Equals(candidate.m_name, worldName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(candidate.m_worldName, worldName, StringComparison.OrdinalIgnoreCase)) continue;
                world = candidate;
                return true;
            }
            return false;
        }

        /// <summary>The resolution the world was made at. A BetterContinents world has one - its height image -
        /// and sampling finer than that invents detail while sampling coarser throws it away. A procedural world
        /// has none, so the configured resolution stands in.</summary>
        private static int ResolutionFor(string worldName, out float half, out string sourceKind)
        {
            // 0.2.721: a procedural world's span is ITS playable radius (FatWorldSize: the profile's record or Expand World
            // Size's), 10000 on a vanilla-size world as before.
            var size = FatWorldSize.SizeForWorld(worldName, out string sizeSource);
            half = size.Radius == FatWorldSize.VanillaRadius ? VanillaWorldRadius : size.Radius;
            sourceKind = half == VanillaWorldRadius ? "vanilla generation" : $"vanilla generation at radius {half:0} ({sizeSource})";
            try
            {
                if (BcWorldData.Exists(worldName, out BcSaveLocation location)
                    && BcWorldData.TryRead(location, out BcWorldSettings settings, out string _)
                    && BcImageMaps.TryDecode(settings, out BcDecodedMaps maps, out string _)
                    && maps.Height != null && maps.Height.Size > 1)
                {
                    half = settings.WorldSize;
                    sourceKind = $"its BetterContinents height map ({maps.Height.Size} px)";
                    return maps.Height.Size;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] could not read '{worldName}'s own resolution "
                    + $"({ex.Message}); the configured one is used instead.");
            }
            return Mathf.Clamp(FiresVoxelConfig.WorldGenResolution?.Value ?? FallbackResolution, 512, 21001);
        }

        private static byte CodeFor(Heightmap.Biome biome)
        {
            for (byte code = 0; code < CodeOrder.Length; code++)
                if (CodeOrder[code] == biome) return code;
            return (byte)(CodeOrder.Length - 1);   // Ocean: the code the generator itself falls back to
        }
    }
}
