using System.Collections.Generic;
using FiresCore.Bridge;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    /// <summary>
    /// Vedr's earthquake fissures cut into voxel ground in 3D (Fire's popup: real cracks, not heightmap dents). Vedr leaves
    /// fissures out of the heightmap on FAT ground and answers where each open one runs (SeasonBridge.GroundCrackAt). On
    /// the main thread each chunk column gets a grid of (signed distance, half-width, depth) per sample column, cached
    /// until Vedr's scars change; its signature folds into the column hash, so a crack opening, widening or healing
    /// regenerates exactly the columns it crosses. The worker carves a V that narrows with depth into the generated
    /// density: noise pushes the walls in and out (undercuts, overhangs), the crack leans slowly along its length, and the
    /// floor is rough. The V keeps a wide floor and the wall noise scales with the width at each depth, so the noise
    /// roughens the walls but never closes the slot at FAT's 1 m resolution: a typical crack stays open about 4 m down
    /// (Fire's popup after R20's pictures: "Build it deeper"). Only density changes, never the seed or brush edits, and a
    /// scarred chunk skips the chunk cache (VoxelChunk), so a healed crack regenerates the ground it cut. On a dedicated
    /// server the scar event dirties the zones itself (no heightmap poke reaches it), so server-side physics sees the
    /// cracks too.
    /// </summary>
    internal static class FatCracks
    {
        private const int Samples = VoxelChunk.SAMPLES;
        private const int Stride = 3;
        private const int DistanceOffset = 0, HalfWidthOffset = 1, DepthOffset = 2;
        private const float MouthShareOfTrench = 0.9f;
        private const float FloorWidthShare = 0.4f;
        private const float WallRoughnessShare = 0.45f;
        private const float WallNoisePerMetre = 0.35f;
        private const float LeanPerMetreOfDepth = 0.35f;
        private const float LeanNoisePerMetre = 0.04f;
        private const float FloorRoughnessShare = 0.25f;
        private const float FloorNoisePerMetre = 0.2f;
        private const float MouthAboveGroundM = 0.5f;
        private const float SampleRingM = 1f;
        private const int WallNoiseSeed = 0x0C4A6C;
        private const int FloorNoiseSeed = WallNoiseSeed + 1;
        private const uint HashSalt = 0xC4AC4Cu;
        private const uint HashPrime = 16777619u;
        private const float HashUnitsPerMetre = 20f;
        private const int DensityLimit = 127;

        private static readonly Dictionary<Vector2Int, float[]> Columns = new Dictionary<Vector2Int, float[]>();

        /// <summary>
        /// The crack grid of one chunk column, or null where no fissure is open (main thread). The array is never changed
        /// once built, so worker threads may read it.
        /// </summary>
        internal static float[] ColumnCracks(int kx, int kz)
        {
            float minX = kx * VoxelChunk.CELLS - SampleRingM, minZ = kz * VoxelChunk.CELLS - SampleRingM;
            if (!SeasonBridge.IsGroundScarredIn(minX, minZ, kx * VoxelChunk.CELLS + VoxelChunk.CELLS, kz * VoxelChunk.CELLS + VoxelChunk.CELLS))
                return null;
            var key = new Vector2Int(kx, kz);
            if (Columns.TryGetValue(key, out float[] grid)) return grid;
            grid = Gather(kx, kz);
            Columns[key] = grid;
            return grid;
        }

        private static float[] Gather(int kx, int kz)
        {
            float[] grid = null;
            float originX = kx * VoxelChunk.CELLS, originZ = kz * VoxelChunk.CELLS;
            for (int z = -1; z <= VoxelChunk.CELLS; z++)
                for (int x = -1; x <= VoxelChunk.CELLS; x++)
                {
                    if (!SeasonBridge.GroundCrackAt(originX + x, originZ + z, out float distance, out float halfWidth, out float depth)) continue;
                    if (grid == null) grid = new float[Samples * Samples * Stride];
                    int i = ((x + 1) + Samples * (z + 1)) * Stride;
                    grid[i + DistanceOffset] = distance;
                    grid[i + HalfWidthOffset] = halfWidth * MouthShareOfTrench;
                    grid[i + DepthOffset] = depth;
                }
            return grid;
        }

        /// <summary>Mixes a chunk column's crack grid into its generation hash; unchanged where none are open.</summary>
        internal static uint Fold(uint hash, float[] grid)
        {
            if (grid == null) return hash;
            unchecked
            {
                uint folded = hash ^ HashSalt;
                foreach (float value in grid) folded = (folded ^ (uint)Mathf.RoundToInt(value * HashUnitsPerMetre)) * HashPrime;
                return folded;
            }
        }

        /// <summary>The deepest crack in a column grid (0 when none), so the column's chunk band reaches its floor.</summary>
        internal static float MaxDepth(float[] grid)
        {
            float deepest = 0f;
            if (grid == null) return deepest;
            for (int i = DepthOffset; i < grid.Length; i += Stride) deepest = Mathf.Max(deepest, grid[i]);
            return deepest;
        }

        /// <summary>
        /// Whether the crack can cut this sample column inside this chunk at all (worker thread): near enough to the crack
        /// allowing for the wall noise and the lean, and with the crack's depth band crossing the chunk's samples.
        /// </summary>
        internal static bool Reaches(float[] grid, int column, float ground, float chunkBottom)
        {
            int i = column * Stride;
            float depth = grid[i + DepthOffset];
            if (depth <= 0f) return false;
            float reach = grid[i + HalfWidthOffset] * (1f + WallRoughnessShare) + LeanPerMetreOfDepth * depth + SampleRingM;
            if (Mathf.Abs(grid[i + DistanceOffset]) > reach) return false;
            float lowestSample = chunkBottom - SampleRingM, highestSample = chunkBottom + VoxelChunk.CELLS + SampleRingM;
            return ground - depth - SampleRingM <= highestSample && ground + MouthAboveGroundM >= lowestSample;
        }

        /// <summary>From SeasonBridge.GroundScarsChanged: forget the grids and rebuild the zones the changed scar covers.</summary>
        internal static void OnGroundScarsChanged(float minX, float minZ, float maxX, float maxZ)
        {
            Columns.Clear();
            VoxelZoneStreamer.MarkRectDirty(minX, minZ, maxX, maxZ);
        }

        /// <summary>Leaving the world: Vedr drops its scars without an event, so a grid kept now would cut a healed crack next load.</summary>
        internal static void Forget() => Columns.Clear();

        /// <summary>One density sample with the crack cut in (worker thread). ground is the column's ground after river and bowl carving.</summary>
        internal static sbyte Carve(sbyte density, float[] grid, int column, float wx, float wy, float wz, float ground)
        {
            int i = column * Stride;
            float depth = grid[i + DepthOffset];
            if (depth <= 0f) return density;
            float below = ground - wy;
            if (below < -MouthAboveGroundM || below > depth + SampleRingM) return density;

            float halfWidth = grid[i + HalfWidthOffset];
            float lean = Signed(VoxelCaveNoise.Noise01(wx * LeanNoisePerMetre, 0f, wz * LeanNoisePerMetre, WallNoiseSeed))
                         * LeanPerMetreOfDepth * Mathf.Max(0f, below);
            float width = halfWidth * Mathf.Lerp(1f, FloorWidthShare, below / depth);
            float wall = Signed(VoxelCaveNoise.Noise01(wx * WallNoisePerMetre, wy * WallNoisePerMetre, wz * WallNoisePerMetre, WallNoiseSeed))
                         * WallRoughnessShare * width;
            float floor = depth * (1f - FloorRoughnessShare * VoxelCaveNoise.Noise01(wx * FloorNoisePerMetre, 0f, wz * FloorNoisePerMetre, FloorNoiseSeed));
            float sdf = Mathf.Max(Mathf.Abs(grid[i + DistanceOffset] - lean) + wall - width, below - floor);
            int carved = Mathf.Clamp(Mathf.RoundToInt(sdf * VoxelChunk.QUANT), -DensityLimit, DensityLimit);
            return carved < density ? (sbyte)carved : density;
        }

        private static float Signed(float noise01) => noise01 * 2f - 1f;
    }
}
