using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Bakes per-vertex "distance to nearest land" into a cloned water mesh's
    /// vertex colors. Read by Custom/FiresWater in the vertex shader to drive
    /// wave amplitude attenuation near shore.
    ///
    /// WHY: Wave amplitude using per-mesh <c>_depth[4]</c> bilinear had
    /// floating-point drift at zone boundaries → visible wave-height tearing
    /// at shallow water seams. Constant depth eliminates the tearing but
    /// loses shore wave attenuation (shore gets full-storm waves which look
    /// wrong). Per-vertex baked shore distance gives BOTH: each vertex's
    /// distance is computed from the SAME global <see cref="Heightmap"/>
    /// query at the SAME world XZ regardless of which mesh asks, so adjacent
    /// meshes' shared vertices get identical values — no drift, no tearing.
    /// AND it's a true geometric distance to the nearest land tile, so
    /// shore attenuation is accurate to actual shorelines (unlike a
    /// distance-from-world-center proxy).
    ///
    /// ENCODING: vertex color RED channel, normalized [0, MAX_DISTANCE_M]
    /// linearly to [0, 1]. 0 = at land (zero waves), 1 = fully deep
    /// (full waves). Other color channels unused (reserved for future
    /// per-vertex data like foam strength or shore-foam-amount).
    ///
    /// CACHING: results keyed by (sourceMesh, volumeWorldPosition). Two
    /// different WaterVolume instances at the same world position with the
    /// same source mesh hash to the same baked clone. Realistically meshes
    /// don't move and aren't shared across positions so the cache hit rate
    /// is high for the same world.
    /// </summary>
    public static class WaterMeshShoreBake
    {
        /// <summary>Valheim's constant sea level Y. Land = terrain above this.</summary>
        public const float SEA_LEVEL_Y = 30f;

        /// <summary>Max search distance for nearest land. Beyond this = "fully deep".</summary>
        public const float MAX_DISTANCE_M = 100f;

        /// <summary>Angular samples per ring during spiral search.</summary>
        private const int SAMPLES_PER_RING = 8;

        /// <summary>Ring step distance — finer = more accurate, slower.</summary>
        private const float RING_STEP_M = 6f;

        // Keyed by source mesh instance ID + quantized volume world XZ so
        // multiple WaterVolumes at the same world position share a baked
        // clone. Mesh refs persist for the session.
        private static readonly Dictionary<(int, int, int), Mesh> s_bakedCache
            = new Dictionary<(int, int, int), Mesh>();

        /// <summary>
        /// Bake and return a cloned mesh with per-vertex shore distance in
        /// vertex colors. Caller assigns the returned mesh to MeshFilter.
        /// Returns null on failure (caller should leave the original mesh
        /// alone — shader falls back to default vertex color 1.0 = full
        /// waves everywhere).
        ///
        /// NOTE: this bake is currently UNUSED by the shader. The shader's
        /// SampleDepth returns constant 1.0 and waveAttn is the constant
        /// 0.6 — both decisions made because depth-driven color/wave
        /// approaches tore at zone boundaries in subtle ways. The bake
        /// stays wired (the result lands in vertex colors) for future
        /// re-use if a tearing-free depth signal becomes available.
        /// </summary>
        public static Mesh BakeForVolume(MeshFilter meshFilter, Transform volumeTransform)
        {
            if (meshFilter == null || meshFilter.sharedMesh == null || volumeTransform == null)
                return null;

            var srcMesh = meshFilter.sharedMesh;
            // Quantize world position to 1m grid for cache key. Volumes at
            // the same zone position give the same bake.
            var pos = volumeTransform.position;
            var key = (srcMesh.GetInstanceID(),
                       Mathf.RoundToInt(pos.x),
                       Mathf.RoundToInt(pos.z));

            if (s_bakedCache.TryGetValue(key, out var cached) && cached != null)
                return cached;

            try
            {
                var clone = UnityEngine.Object.Instantiate(srcMesh);
                clone.name = srcMesh.name + "_FiresShoreBaked";

                var verts = clone.vertices;
                var colors = new Color[verts.Length];

                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 worldPos = volumeTransform.TransformPoint(verts[i]);
                    float distToLand = ComputeDistanceToLand(worldPos);
                    float normalized = Mathf.Clamp01(distToLand / MAX_DISTANCE_M);
                    // R = wave attenuation. 0 = no waves (at land), 1 = full.
                    colors[i] = new Color(normalized, 0f, 0f, 1f);
                }

                clone.colors = colors;
                clone.UploadMeshData(false); // keep CPU copy in case we re-bake
                s_bakedCache[key] = clone;
                return clone;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WaterMeshShoreBake] BakeForVolume failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Spiral-sample <see cref="Heightmap.GetHeight"/> outward from
        /// <paramref name="worldPos"/> until a land tile (height &gt; sea
        /// level) is found, or until MAX_DISTANCE_M is reached.
        ///
        /// Returns the distance in meters to the nearest land sample, or
        /// MAX_DISTANCE_M if none found within range.
        ///
        /// Why this is deterministic: <c>Heightmap.GetHeight(point)</c> is
        /// a static engine function. Same world XZ → same answer. So two
        /// adjacent water meshes baking the SAME boundary vertex
        /// (worldPos shared) compute the SAME ring of samples and arrive
        /// at the SAME distance. No floating-point drift between meshes.
        /// </summary>
        private static float ComputeDistanceToLand(Vector3 worldPos)
        {
            // Center sample first — if this very position is land, distance = 0.
            float here;
            if (Heightmap.GetHeight(worldPos, out here) && here > SEA_LEVEL_Y)
                return 0f;

            // Expanding rings. Step size + sample count chosen to balance
            // accuracy against query count (each ring = SAMPLES_PER_RING
            // GetHeight calls; total per-vertex = SAMPLES_PER_RING *
            // ceil(MAX_DISTANCE_M / RING_STEP_M) — currently 8 * 17 = 136).
            for (float r = RING_STEP_M; r <= MAX_DISTANCE_M; r += RING_STEP_M)
            {
                for (int i = 0; i < SAMPLES_PER_RING; i++)
                {
                    float angle = (i / (float)SAMPLES_PER_RING) * Mathf.PI * 2f;
                    Vector3 sample = new Vector3(
                        worldPos.x + Mathf.Cos(angle) * r,
                        0f,
                        worldPos.z + Mathf.Sin(angle) * r);
                    float h;
                    if (Heightmap.GetHeight(sample, out h) && h > SEA_LEVEL_Y)
                        return r;
                }
            }
            return MAX_DISTANCE_M;
        }

        /// <summary>
        /// Apply the bake to a WaterVolume's surface mesh. Idempotent — if
        /// the mesh has already been baked for this volume position the
        /// existing baked clone is reused. Call after FiresWaterShaderLoader
        /// has swapped the shader on the volume's material.
        /// </summary>
        public static void Apply(WaterVolume wv)
        {
            if (wv == null || wv.m_waterSurface == null) return;
            var meshFilter = wv.m_waterSurface.GetComponent<MeshFilter>();
            if (meshFilter == null) return;

            // Already baked? sharedMesh name suffix tells us.
            if (meshFilter.sharedMesh != null
                && meshFilter.sharedMesh.name != null
                && meshFilter.sharedMesh.name.EndsWith("_FiresShoreBaked"))
                return;

            var baked = BakeForVolume(meshFilter, wv.transform);
            if (baked != null) meshFilter.sharedMesh = baked;
        }
    }
}
