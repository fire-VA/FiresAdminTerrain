using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.820: the per-column inland waterline (MeshBuffers.WetLine -> UV1.x -> the terrain shader's wet-shore band) seeded
    // from the generated water in WORLD space: each column takes the highest generated surface (above the sea) at itself or
    // on circles of WetRadii around it. A pure function of world position, so two chunks agree on every shared column, and
    // the coverage edge is round instead of the 4-neighbour dilation's diamonds. Worker-safe: GenWaterField reads the
    // immutable load-time arrays (the same reads RunPendingGenerate makes on the mesh workers).
    internal static class WetLineSeed
    {
        private const float SeaGate = 30.25f, GateStep = 8f;
        private static readonly float[] WetRadii = { 2f, 4f, 6f };
        private const int Directions = 8;
        private static readonly float[] s_dx, s_dz;

        static WetLineSeed()
        {
            s_dx = new float[Directions];
            s_dz = new float[Directions];
            for (int k = 0; k < Directions; k++)
            {
                float a = k * Mathf.PI * 2f / Directions;
                s_dx[k] = Mathf.Cos(a);
                s_dz[k] = Mathf.Sin(a);
            }
        }

        // False when the generated field isn't there (the caller keeps its own dilation). `store` = the chunk's own water
        // levels (or null); `wet` gets the waterline per column, 0 = none.
        internal static bool Fill(Vector3 origin, float[] store, float[] wet)
        {
            if (!FiresWorldGenBase.Active || !GenWaterField.Ready) return false;
            int S = VoxelChunk.SAMPLES;
            float reach = WetRadii[WetRadii.Length - 1];
            // Cheap gate: no store water and no generated water anywhere within reach of this chunk = no band at all.
            bool any = false;
            if (store != null) for (int i = 0; i < store.Length && !any; i++) any = store[i] > SeaGate;
            for (float gz = -1f - reach; gz <= VoxelChunk.CELLS + reach && !any; gz += GateStep)
                for (float gx = -1f - reach; gx <= VoxelChunk.CELLS + reach && !any; gx += GateStep)
                    any = Level(origin.x + gx, origin.z + gz) > SeaGate;
            if (!any) { System.Array.Clear(wet, 0, wet.Length); return true; }
            for (int lz = -1; lz <= VoxelChunk.CELLS; lz++)
                for (int lx = -1; lx <= VoxelChunk.CELLS; lx++)
                {
                    int i = (lx + 1) + S * (lz + 1);
                    float x = origin.x + lx, z = origin.z + lz;
                    float best = store != null && store[i] > SeaGate ? store[i] : 0f;
                    best = Mathf.Max(best, Level(x, z));
                    foreach (float r in WetRadii)
                        for (int k = 0; k < Directions; k++)
                            best = Mathf.Max(best, Level(x + s_dx[k] * r, z + s_dz[k] * r));
                    wet[i] = best > SeaGate ? best : 0f;
                }
            return true;
        }

        private static float Level(float x, float z)
            => GenWaterField.TryWaterSurfaceSmooth(x, z, out float level, out float depth) && depth > 0f ? level : 0f;
    }
}
