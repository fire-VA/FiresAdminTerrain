using System;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // (x, z) world -> the water surface over that point and its depth; false = dry there. It must be continuous across
    // chunks: the builder samples it out to 3 corners past a chunk's edge, and two chunks sharing a corner must read one height.
    internal delegate bool WaterSurfaceProvider(float x, float z, out float level, out float depth);

    // THE WATER SHEET MESH, SHARED (FAT 0.2.765, for [seasons]' 0.2.766; Fire 10-06: the procedural water on the skylands is
    // "basically perfect ... transfer that same mesh logic over to caves and our V tools"). The builder generated and skyland
    // water already use (VoxelChunkView.BuildSheet: the store dilated into an ocean-style skirt the banks bury, the crest and
    // cave-mouth guards, the smoothed corner field with its 3-corner halo, flow), callable for any per-chunk level field:
    //   levels34 / ground34: SAMPLES x SAMPLES (34 x 34, apron +1: index (lx + 1) + 34 * (lz + 1), lx/lz -1..32) - a level
    //     > 0 where the sheet is, 0 dry, < 0 a mopped column (never lidded); the bed under each column (0 = unknown).
    //   origin: the chunk's sample (0,0,0) in world space (VoxelChunk.WorldOrigin). The mesh comes out in WORLD space (its
    //     vertices are origin + local, as the chunk views' own sheets are), so draw it from a GameObject at (0,0,0) (0.2.773: this
    //     said chunk-local; [seasons] found it was not).
    //   surface: the height at every corner (null = the world's own: GenWaterField + the skylands).
    // The material is VoxelChunkView.ResolveSheetMaterial() (the calm inland sheet). Main thread only (shared scratch).
    internal static class VoxelWaterSheetMesh
    {
        private static MeshBuffers s_scratch;

        // Fills `into` (cleared first). False = nothing to draw (no wet column, or the guards culled it all): `into` is empty.
        internal static bool Build(float[] levels34, float[] ground34, Vector3 origin, WaterSurfaceProvider surface, Mesh into)
        {
            if (into == null) return false;
            into.Clear(false);
            int n = VoxelChunk.SAMPLES * VoxelChunk.SAMPLES;
            if (levels34 == null || ground34 == null || levels34.Length < n || ground34.Length < n) return false;
            if (s_scratch == null) s_scratch = new MeshBuffers();
            Array.Copy(levels34, s_scratch.WaterLvl, n);
            Array.Copy(ground34, s_scratch.Ground, n);
            s_scratch.OriginX = origin.x;
            s_scratch.OriginY = origin.y;
            s_scratch.OriginZ = origin.z;
            s_scratch.HasWater = true;
            VoxelChunkView.BuildSheet(s_scratch, surface);
            return VoxelChunkView.CopySheetTo(into);
        }

        // A chunk's view took a new mesh (a carve, a fill, a regenerate): its key, on the main thread, after the apply.
        internal static event Action<Vector3Int> ChunkMeshed;

        internal static void NoteChunkMeshed(Vector3Int key)
        {
            var handlers = ChunkMeshed;
            if (handlers == null) return;
            try { handlers(key); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] a ChunkMeshed handler threw for {key}: {ex.Message}"); }
        }
    }
}
