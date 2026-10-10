using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Water.Core
{
    // Placed water drawn like the skyland water (FAT 0.2.766; Fire 10-06 session #4: "Vpool's mesh laying doesn't seem as smooth
    // ... Note how our procedural water on the skylands works, since that's basically perfect these days. If we transfer that same
    // mesh logic over to caves and our V tools, it would be ideal"). Every fresh-water disk the tools placed (Vpour, Vpool, Vpond,
    // WaterSheets) renders through [worldgen]'s VoxelWaterSheetMesh.Build, the builder of the generated and skyland sheets: per
    // voxel chunk, a level field (wet where a disk covers the column and the voxel floor lies under its surface, mopped columns
    // never lidded), the floor under each column, and the disks' own surface heights as the provider - flat at each disk's height,
    // blended where disks overlap, continuous across chunks, so shared corners match and nothing seams. The builder runs the
    // skirt past the shore that the banks bury, the crest and cave-mouth guards and the smoothing; the material is the calm inland
    // sheet. The disks' WaterMeshCombiners keep their swim triggers (same heights); their own grid mesh is not drawn.
    // A chunk rebuilds when the set of disks changes or its voxels change (VoxelWaterSheetMesh.ChunkMeshed), at most
    // RebuildsPerFrame a frame. Graphics peers only: a dedicated server draws nothing.
    internal static class PlacedWaterSheets
    {
        private const int RebuildsPerFrame = 2;
        private const float FloorReach = 32f;
        private const float BandBelow = 2f, BandAbove = 34f;   // a chunk draws the disks whose surface lies in its y band

        private static readonly Dictionary<Vector3Int, GameObject> s_drawn = new Dictionary<Vector3Int, GameObject>();
        private static readonly HashSet<Vector3Int> s_dirty = new HashSet<Vector3Int>();
        private static readonly HashSet<Vector3Int> s_wanted = new HashSet<Vector3Int>();
        private static long s_diskSignature;
        private static bool s_hooked, s_loggedFirst;

        // True where this peer draws placed water itself (the combiners then hide their grid mesh).
        internal static bool Active => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        // From WaterSheets.Tick (the water tools' per-frame tick), after the disk index refresh.
        internal static void Tick(IReadOnlyList<WaterSheets.DiskInfo> disks, long signature)
        {
            if (!Active) return;
            if (!s_hooked) { s_hooked = true; VoxelWaterSheetMesh.ChunkMeshed += OnChunkMeshed; }
            if (signature != s_diskSignature)
            {
                s_diskSignature = signature;
                s_wanted.Clear();
                foreach (var d in disks)
                {
                    if (!d.Water) continue;
                    int ky = Mathf.FloorToInt(d.Y / VoxelChunk.CELLS);
                    int x0 = Mathf.FloorToInt((d.X - d.R - 4f) / VoxelChunk.CELLS), x1 = Mathf.FloorToInt((d.X + d.R + 4f) / VoxelChunk.CELLS);
                    int z0 = Mathf.FloorToInt((d.Z - d.R - 4f) / VoxelChunk.CELLS), z1 = Mathf.FloorToInt((d.Z + d.R + 4f) / VoxelChunk.CELLS);
                    for (int kx = x0; kx <= x1; kx++)
                        for (int kz = z0; kz <= z1; kz++)
                            s_wanted.Add(new Vector3Int(kx, ky, kz));
                }
                foreach (var key in new List<Vector3Int>(s_drawn.Keys))
                    if (!s_wanted.Contains(key)) { Drop(key); }
                foreach (var key in s_wanted) s_dirty.Add(key);
            }
            int built = 0;
            while (built < RebuildsPerFrame && s_dirty.Count > 0)
            {
                Vector3Int key = default;
                foreach (var k in s_dirty) { key = k; break; }
                s_dirty.Remove(key);
                if (!s_wanted.Contains(key)) continue;
                BuildChunk(key);
                built++;
            }
        }

        private static void OnChunkMeshed(Vector3Int key)
        {
            if (s_wanted.Contains(key)) s_dirty.Add(key);
        }

        private static readonly float[] s_levels = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
        private static readonly float[] s_ground = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];

        private static void BuildChunk(Vector3Int key)
        {
            var origin = new Vector3(key.x * VoxelChunk.CELLS, key.y * VoxelChunk.CELLS, key.z * VoxelChunk.CELLS);
            float yLow = origin.y - BandBelow, yHigh = origin.y + BandAbove;
            int S = VoxelChunk.SAMPLES, wet = 0;
            for (int lz = -1; lz <= VoxelChunk.CELLS; lz++)
                for (int lx = -1; lx <= VoxelChunk.CELLS; lx++)
                {
                    int i = (lx + 1) + S * (lz + 1);
                    float x = origin.x + lx, z = origin.z + lz;
                    float level = WaterSheets.FreshSurfaceAt(x, z, yLow, yHigh);
                    s_levels[i] = 0f;
                    s_ground[i] = 0f;
                    if (float.IsNaN(level)) continue;
                    if (VoxelWorld.TrySampleSurfaceY(x, z, level + 0.05f, level - FloorReach, out float bed, out bool wall) && !wall)
                    {
                        s_ground[i] = bed;
                        if (bed < level) { s_levels[i] = level; wet++; }
                    }
                    if (VoxelWorld.IsMoppedAt(x, level, z)) s_levels[i] = -1f;
                }

            Mesh mesh = MeshFor(key, origin);
            bool any = wet > 0 && VoxelWaterSheetMesh.Build(s_levels, s_ground, origin, (float x, float z, out float level, out float depth) =>
            {
                level = WaterSheets.FreshSurfaceAt(x, z, yLow, yHigh);
                depth = 0f;
                if (float.IsNaN(level)) return false;
                if (VoxelWorld.TrySampleSurfaceY(x, z, level + 0.05f, level - FloorReach, out float bed, out bool wall) && !wall)
                    depth = Mathf.Max(0f, level - bed);
                return true;
            }, mesh);
            if (s_drawn.TryGetValue(key, out var go) && go != null) go.SetActive(any);
            if (any && !s_loggedFirst)
            {
                s_loggedFirst = true;
                FiresLogger.LogDebug($"[FiresWater] placed water drawn with the skyland sheet builder (first: chunk {key}, {wet} wet column(s)); the disks' grid meshes are hidden.");
            }
        }

        private static Mesh MeshFor(Vector3Int key, Vector3 origin)
        {
            if (!s_drawn.TryGetValue(key, out var go) || go == null)
            {
                go = new GameObject($"fires_placed_water_{key.x}_{key.y}_{key.z}");
                Object.DontDestroyOnLoad(go);
                // 0.2.771: at the world origin. The builder's vertices are WORLD-space (VoxelChunkView's convention: the view
                // sits at 0,0,0); 0.2.766 put this object at the chunk origin, so every placed sheet drew offset by its own
                // coordinates, far from the water (Fire's final session: Vpour / Vpool laid no visible mesh).
                go.transform.position = Vector3.zero;
                go.AddComponent<MeshFilter>().sharedMesh = new Mesh { name = "placed_water_sheet" };
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                s_drawn[key] = go;
            }
            var renderer = go.GetComponent<MeshRenderer>();
            var mat = VoxelChunkView.ResolveSheetMaterial();
            if (renderer != null && mat != null && renderer.sharedMaterial != mat) renderer.sharedMaterial = mat;
            return go.GetComponent<MeshFilter>().sharedMesh;
        }

        private static void Drop(Vector3Int key)
        {
            if (s_drawn.TryGetValue(key, out var go) && go != null)
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Object.Destroy(mf.sharedMesh);
                Object.Destroy(go);
            }
            s_drawn.Remove(key);
            s_dirty.Remove(key);
        }
    }
}
