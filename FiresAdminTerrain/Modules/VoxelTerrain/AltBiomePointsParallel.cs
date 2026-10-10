using System;
using System.Diagnostics;
using System.Threading.Tasks;
using HarmonyLib;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // Valheim 1.0's AltBiomeWorldData.GenerateBiomePoints samples a 2048x2048 grid, 4.2 M GetBiome + GetBiomeHeight, one
    // after another on the main thread. A client pays it inside ZNet.RPC_PeerInfo on every join (its handshake World carries
    // no save version, so VerifyBiomeData always runs): 5.7 s of an 11 s join freeze on a BC world ([perf], 2026-09-27).
    // Every point is independent, so this runs vanilla's loop across the cores: the same calls per point, each worker inside
    // the zone gate's planning scope exactly as the serial loop is (ZoneMode_AltBiome_GenerateBiomePoints), each writing only
    // its own row. The generators already answer these calls from HeightmapBuilder's worker. A prefix that already replaced
    // the method (another mod) is left alone. fires_biomepoints_check compares this against a serial run cell by cell.
    [HarmonyPatch(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints))]
    internal static class AltBiomePointsParallel
    {
        private const int GridSize = 2048;
        // 0.2.721: the world's own edge (FatWorldSize); 10500^2 = 110250000 on a vanilla-size world.
        private static float EdgeRadiusSquared => FatWorldSize.EdgeRadius * FatWorldSize.EdgeRadius;
        private const float BeyondEdgeHeight = -1000f;
        private const string StartStep = "start";
        private const string DoneStep = "AltBiome.GenerateBiomePoints";
        private const string CheckCommand = "fires_biomepoints_check";

        [HarmonyPriority(Priority.Low)]
        private static bool Prefix(World world, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var generator = WorldGenerator.instance;
            if (generator == null) return true;
            var data = new AltBiomeWorldData(GridSize);
            data.dt = DateTime.Now;
            data.dbStep(StartStep);
            Fill(generator, data, parallel: true);
            data.PointsGenerated = true;
            data.dbStep(DoneStep);
            world.m_biomeData = data;
            data.m_world = world;
            return false;
        }

        private static void Fill(WorldGenerator generator, AltBiomeWorldData data, bool parallel)
        {
            var ocean = Heightmap.Biome.Ocean.ToBiomeIndex();
            float edgeSq = EdgeRadiusSquared;   // once per fill ([worldgen] 10-05: one edge for every row, read on this thread)
            if (parallel) Parallel.For(0, GridSize, row => FillRow(generator, data, row, ocean, edgeSq));
            else for (int row = 0; row < GridSize; row++) FillRow(generator, data, row, ocean, edgeSq);
        }

        // The grid is vanilla's own (2048 px x 12 m, +-12288 m, constants inside AltBiomeWorldData): this mirrors vanilla's
        // generation; on a world past that span only the edge mask moves (with a 20.5 km edge it masks nothing).
        private static void FillRow(WorldGenerator generator, AltBiomeWorldData data, int row, Heightmap.BiomeIndex ocean, float edgeSq)
        {
            ZoneModeGate.EnterPlanning();
            try
            {
                float y = AltBiomeWorldData.MapSpaceToWorldSpace(row);
                for (int column = 0; column < GridSize; column++)
                {
                    float x = AltBiomeWorldData.MapSpaceToWorldSpace(column);
                    if (x * x + y * y > edgeSq)
                    {
                        data.PointBiomes[column, row] = ocean;
                        data.PointHeights[column, row] = BeyondEdgeHeight;
                        continue;
                    }
                    Heightmap.Biome biome = generator.GetBiome(x, y);
                    data.PointBiomes[column, row] = biome.ToBiomeIndex();
                    data.PointHeights[column, row] = generator.GetBiomeHeight(biome, x, y, out _);
                }
            }
            finally { ZoneModeGate.ExitPlanning(); }
        }

        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand(CheckCommand,
                "Builds this world's alt-biome grid serially and in parallel and compares every cell (D11 check).",
                args => Report(args.Context, Check()), isCheat: true);
        }

        private static void Report(Terminal context, string line)
        {
            context?.AddString(line);
            FiresLogger.LogInfo("[FiresBiomePoints] " + line);
        }

        private static string Check()
        {
            var generator = WorldGenerator.instance;
            if (generator == null) return "no world generator: load a world first.";
            var serial = new AltBiomeWorldData(GridSize);
            var parallel = new AltBiomeWorldData(GridSize);
            var clock = Stopwatch.StartNew();
            Fill(generator, serial, parallel: false);
            long serialMs = clock.ElapsedMilliseconds;
            clock.Restart();
            Fill(generator, parallel, parallel: true);
            long parallelMs = clock.ElapsedMilliseconds;
            int biomeMismatches = 0, heightMismatches = 0;
            for (int row = 0; row < GridSize; row++)
                for (int column = 0; column < GridSize; column++)
                {
                    if (serial.PointBiomes[column, row] != parallel.PointBiomes[column, row]) biomeMismatches++;
                    if (BitConverter.ToInt32(BitConverter.GetBytes(serial.PointHeights[column, row]), 0)
                        != BitConverter.ToInt32(BitConverter.GetBytes(parallel.PointHeights[column, row]), 0)) heightMismatches++;
                }
            return $"{GridSize * GridSize:N0} cells: {biomeMismatches} biome and {heightMismatches} height mismatch(es), bitwise; "
                + $"serial {serialMs} ms, parallel {parallelMs} ms on {Environment.ProcessorCount} core(s).";
        }
    }
}
