using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // (x, z) world -> the water surface over that point and its depth; false = dry there. It must be continuous across
    // chunks: the builder samples it out to 3 corners past a chunk's edge, and two chunks sharing a corner must read one height.
    internal delegate bool WaterSurfaceProvider(float x, float z, out float level, out float depth);

    // THE WATER SHEET MESH, SHARED: the procedural water mesh logic of the skylands, reused for
    // caves and the V tools. The builder generated and skyland
    // water already use (VoxelChunkView.BuildSheet: the store dilated into an ocean-style skirt the banks bury, the crest and
    // cave-mouth guards, the smoothed corner field with its 3-corner halo, flow), callable for any per-chunk level field:
    //   levels34 / ground34: SAMPLES x SAMPLES (34 x 34, apron +1: index (lx + 1) + 34 * (lz + 1), lx/lz -1..32) - a level
    //     > 0 where the sheet is, 0 dry, < 0 a mopped column (never lidded); the bed under each column (0 = unknown).
    //   origin: the chunk's sample (0,0,0) in world space (VoxelChunk.WorldOrigin). The mesh comes out in WORLD space (its
    //     vertices are origin + local, as the chunk views' own sheets are), so draw it from a GameObject at (0,0,0).
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
            long up0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1344: the mesh upload, apart from the builder
            bool any = VoxelChunkView.CopySheetTo(into);
            UploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - up0;
            return any;
        }
        internal static long UploadTicks;   // 0.2.1344: PlacedWaterSheets' phase timer, main thread

        // 0.2.1344 (r22: 400+ sheet rebuilds / 10 s, 3.4 s of work, standing): Version moves on a border mirror too, so the 1165 gate let
        // remeshes through that moved no ground. The signature is a hash of the chunk's density as meshed. Same bytes on the same chunk
        // object = no ground read can answer differently: no event (no cap tiles dropped, no sheet rebuilt). Any density change fires; a
        // chunk object's first mesh always fires, also when a reloaded chunk happens to come back at the Version noted for its key.
        internal static bool GroundSigOn => FiresVoxelConfig.WaterGroundSignature != null && FiresVoxelConfig.WaterGroundSignature.Value;
        internal static int SkippedSameGround, FiredWindow;
        private static bool s_sigProof;
        internal static uint DensityHash(sbyte[] d)
        {
            uint h = 2166136261u;
            for (int i = 0; i < d.Length; i++) { h ^= (byte)d[i]; h *= 16777619u; }
            return h;
        }
        internal static void NoteChunkMeshed(VoxelChunk chunk, int version, bool hasSig, uint sig)
        {
            if (!hasSig || !GroundSigOn) { NoteChunkMeshed(chunk.Key, version); return; }
            if (!chunk.GroundSigSet)
            {
                chunk.GroundSigSet = true; chunk.GroundSig = sig;
                s_notedVersion[chunk.Key] = version;
                Fired++;
                NoteChunkMeshed(chunk.Key);
                return;
            }
            if (chunk.GroundSig == sig)
            {
                SkippedSameGround++;
                if (!s_sigProof) { s_sigProof = true; FiresLogger.LogInfo("[WaterTools] a remesh with unchanged ground no longer drops cap tiles or rebuilds placed water (0.2.1344)"); }
                return;
            }
            chunk.GroundSig = sig;
            NoteChunkMeshed(chunk.Key, version);
        }

        // A chunk's view took a new mesh (a carve, a fill, a regenerate): its key, on the main thread, after the apply.
        internal static event Action<Vector3Int> ChunkMeshed;

        // 0.2.1165 (the lead, Fire idle at the homestead: "[WaterTools] water tick ... sheets 265-366 ms, rebuilt 39-77 chunk(s)"
        // with the disk index kept = no water change): every remesh of a voxel chunk under placed water (trail stamps, paint G, seam
        // groups, snow refinement) re-dirtied its sheet, and a sheet chunk costs 11-30 ms. Only a change of the chunk's VOXEL DATA
        // (Version: carve / fill / regenerate) can move the bed the sheet reads; a paint-only remesh keeps the same Version, so it
        // no longer fires the event.
        private static readonly Dictionary<Vector3Int, int> s_notedVersion = new Dictionary<Vector3Int, int>();
        internal static int Fired, SkippedSameVersion;
        internal static void NoteChunkMeshed(Vector3Int key, int version)
        {
            if (s_notedVersion.TryGetValue(key, out int v) && v == version) { SkippedSameVersion++; return; }
            s_notedVersion[key] = version;
            if (s_notedVersion.Count > 65536) s_notedVersion.Clear();
            Fired++;
            NoteChunkMeshed(key);
        }

        internal static void NoteChunkMeshed(Vector3Int key)
        {
            FiredWindow++;   // 0.2.1344
            var handlers = ChunkMeshed;
            if (handlers == null) return;
            try { handlers(key); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] a ChunkMeshed handler threw for {key}: {ex.Message}"); }
        }
    }
}
