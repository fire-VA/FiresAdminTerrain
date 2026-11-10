using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // HALO far ring low LOD phase 3 (0.2.1047; Fire GO 10-24, Tools\DESIGN_halo_far_ring_lod_20261024.md): tree silhouettes on
    // the far side of the ring. Reused from FarVegImpostors: its baked billboards (each species rendered once at load to a cutout
    // texture, the game's own vegetation table) and its crossed-quad shape. Placed ONCE per ring on a worker: a 40 m grid over the
    // band (rim lips left bare), each cell's biome from FatHaloBiome (the same field the near side and the far map use), kept by
    // that biome's forest cover, the species picked among the biome's tallest baked ones, a random yaw about the ring's local up
    // and a random size. One STATIC mesh per species (8 vertices a tree: two crossed quads, culling off), normals = the ring's
    // local up, drawn with Custom/FiresHaloFar like the far floor: the GPU squeeze and seam gap, sun lambert, own fog, the cutout,
    // and faded out inside HideNear where the real vegetation stands. One draw call per species, no shadows. Clients only, GPU path
    // only (without the fathalofar shader there is no far tree: they would sit past the far clip).
    internal static class FatHaloFarTrees
    {
        private const float Cell = 40f, RimSkip = 60f, HideNear = 300f, HideFade = 150f, Cutoff = 0.35f;
        private const int MaxTrees = 30000, SpeciesPerBiome = 2;
        private static readonly Heightmap.Biome[] Biomes =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Plains,
            Heightmap.Biome.Mistlands, Heightmap.Biome.Mountain, Heightmap.Biome.DeepNorth, Heightmap.Biome.AshLands,
        };

        // How much of a far cell carries a tree, per biome (BF and Mistlands are woods, Plains is open grass).
        private static float Cover(Heightmap.Biome b)
        {
            switch (b)
            {
                case Heightmap.Biome.BlackForest: return 0.80f;
                case Heightmap.Biome.Mistlands: return 0.60f;
                case Heightmap.Biome.Swamp: return 0.45f;
                case Heightmap.Biome.Meadows: return 0.25f;
                case Heightmap.Biome.Mountain: return 0.20f;
                case Heightmap.Biome.AshLands: return 0.10f;
                case Heightmap.Biome.Plains: return 0.05f;
                default: return 0f;
            }
        }

        private sealed class Kind
        {
            public string Prefab;
            public Texture Tex;
            public Vector3[] QV;   // the template's two front quads (8 vertices)
            public Vector2[] QU;
            public List<Vector3> V = new List<Vector3>();
            public List<Vector3> N = new List<Vector3>();
            public List<Vector2> U = new List<Vector2>();
            public List<Color32> C = new List<Color32>();
            public Mesh Mesh;
            public Material Mat;
        }

        private static string s_builtFor;
        private static Task<int> s_place;
        private static Kind[] s_kinds;
        private static bool s_ready, s_waitLogged;
        private static float s_started;

        internal static void Tick(string ringKey, Camera cam, Vector3 origin, float r, float halfLength, float farTarget)
        {
            if (FatHaloSky.FarShader == null || !FatHaloBiome.Ready) return;
            if (s_builtFor != ringKey)
            {
                if (!Begin(ringKey, origin, r, halfLength)) return;
            }
            if (!s_ready)
            {
                if (s_place == null || !s_place.IsCompleted) return;
                Finish();
                if (!s_ready) return;
            }
            for (int i = 0; i < s_kinds.Length; i++)
            {
                var k = s_kinds[i];
                if (k.Mesh == null) continue;
                k.Mat.SetFloat("_FarTarget", farTarget);
                Graphics.DrawMesh(k.Mesh, Matrix4x4.identity, k.Mat, 0, cam, 0, null, false, false, false);
            }
        }

        // Main thread: the species (baked billboards must exist) and the worker that places every tree.
        private static bool Begin(string ringKey, Vector3 origin, float r, float halfLength)
        {
            var kinds = new List<Kind>();
            var perBiome = new int[Biomes.Length][];
            var found = new List<(string prefab, Mesh quad, Texture tex, float height)>();
            for (int b = 0; b < Biomes.Length; b++)
            {
                FarVegImpostors.BakedSpeciesFor(Biomes[b], SpeciesPerBiome, found);
                var idx = new List<int>();
                foreach (var f in found)
                {
                    int at = kinds.FindIndex(k => k.Prefab == f.prefab);
                    if (at < 0)
                    {
                        var qv = f.quad.vertices; var qu = f.quad.uv;
                        if (qv.Length < 12 || qu.Length < 12) continue;
                        var k = new Kind { Prefab = f.prefab, Tex = f.tex, QV = new Vector3[8], QU = new Vector2[8] };
                        for (int j = 0; j < 4; j++) { k.QV[j] = qv[j]; k.QU[j] = qu[j]; k.QV[4 + j] = qv[8 + j]; k.QU[4 + j] = qu[8 + j]; }
                        kinds.Add(k);
                        at = kinds.Count - 1;
                    }
                    idx.Add(at);
                }
                perBiome[b] = idx.ToArray();
            }
            if (kinds.Count == 0)
            {
                if (!s_waitLogged) { s_waitLogged = true; FiresLogger.LogDebug($"[FatHalo] far trees waiting: no baked billboard species yet ({FarVegImpostors.SpeciesCount} species known)"); }
                return false;
            }
            s_builtFor = ringKey;
            s_ready = false;
            s_kinds = kinds.ToArray();
            s_started = Time.realtimeSinceStartup;
            var arr = s_kinds;
            float halfW = Mathf.Min(halfLength, FatHaloBiome.HalfWidth > 0f ? FatHaloBiome.HalfWidth : halfLength);
            s_place = Task.Run(() => Place(arr, perBiome, origin, r, halfW));
            return true;
        }

        // Worker: every tree's 8 vertices in world space. Pure maths (no Unity engine calls).
        private static int Place(Kind[] kinds, int[][] perBiome, Vector3 origin, float r, float halfW)
        {
            int cols = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * r / Cell));
            int rows = Mathf.Max(1, Mathf.FloorToInt((2f * halfW - 2f * RimSkip) / Cell));
            int placed = 0;
            for (int y = 0; y < rows && placed < MaxTrees; y++)
                for (int x = 0; x < cols && placed < MaxTrees; x++)
                {
                    float jx = Rand(x, y, 1) - 0.5f, jy = Rand(x, y, 2) - 0.5f;
                    float a = (x + 0.5f + jx * 0.8f) * Mathf.PI * 2f / cols;
                    float t = -halfW + RimSkip + (y + 0.5f + jy * 0.8f) * Cell;
                    var outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    Vector3 p = origin + outward * r + Vector3.up * t;
                    if (!FatHaloBiome.TryUnroll01(p, out float u01, out float v01)) continue;
                    FatHaloBiome.SampleAt01(u01, v01, out var ba, out var bb, out float bt);
                    var biome = bt > 0.5f ? bb : ba;
                    int bi = Array.IndexOf(Biomes, biome);
                    if (bi < 0 || perBiome[bi].Length == 0) continue;
                    if (Rand(x, y, 3) >= Cover(biome)) continue;
                    var k = kinds[perBiome[bi][Mathf.Min(perBiome[bi].Length - 1, (int)(Rand(x, y, 4) * perBiome[bi].Length))]];
                    // the ring's frame at p: up toward the axis, forward along the axis, turned by a random yaw
                    Vector3 up = -outward, f0 = Vector3.up, r0 = Vector3.Cross(up, f0).normalized;
                    float yaw = Rand(x, y, 5) * Mathf.PI * 2f, cy = Mathf.Cos(yaw), sy = Mathf.Sin(yaw);
                    Vector3 fw = f0 * cy + r0 * sy, rt = Vector3.Cross(up, fw).normalized;
                    float s = 0.8f + 0.45f * Rand(x, y, 6);
                    byte g = (byte)(205 + 50 * Rand(x, y, 7));
                    var tint = new Color32(g, g, g, 255);
                    for (int j = 0; j < 8; j++)
                    {
                        Vector3 q = k.QV[j] * s;
                        k.V.Add(p + rt * q.x + up * q.y + fw * q.z);
                        k.N.Add(up);
                        k.U.Add(k.QU[j]);
                        k.C.Add(tint);
                    }
                    placed++;
                }
            return placed;
        }

        private static float Rand(int x, int y, int salt)
        {
            unchecked
            {
                uint h = (uint)FatHaloBiome.Seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)y * 0xC2B2AE3Du ^ (uint)(salt + 101) * 0x27D4EB2Fu;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        // Main thread: one static mesh + one material per species.
        private static void Finish()
        {
            var task = s_place;
            s_place = null;
            if (task.IsFaulted)
            {
                FiresLogger.LogWarning($"[FatHalo] far trees off: placement failed ({task.Exception?.GetBaseException().Message})");
                s_kinds = new Kind[0];
                s_ready = true;
                return;
            }
            int trees = task.Result, meshes = 0, verts = 0;
            foreach (var k in s_kinds)
            {
                if (k.V.Count == 0) continue;
                int n = k.V.Count;
                var tris = new int[n / 8 * 12];
                for (int t = 0, i = 0; i < n; i += 8)
                {
                    tris[t++] = i; tris[t++] = i + 2; tris[t++] = i + 1; tris[t++] = i; tris[t++] = i + 3; tris[t++] = i + 2;
                    tris[t++] = i + 4; tris[t++] = i + 6; tris[t++] = i + 5; tris[t++] = i + 4; tris[t++] = i + 7; tris[t++] = i + 6;
                }
                k.Mesh = new Mesh { name = "FatHaloFarTrees_" + k.Prefab, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                k.Mesh.SetVertices(k.V);
                k.Mesh.SetNormals(k.N);
                k.Mesh.SetUVs(0, k.U);
                k.Mesh.SetColors(k.C);
                k.Mesh.triangles = tris;
                k.Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 40000f);
                k.Mesh.UploadMeshData(true);
                k.Mat = new Material(FatHaloSky.FarShader) { name = "FatHaloFarTrees_" + k.Prefab, renderQueue = 2450, mainTexture = k.Tex };
                k.Mat.SetFloat("_Cutoff", Cutoff);
                k.Mat.SetFloat("_HideNear", HideNear);
                k.Mat.SetFloat("_HideFade", HideFade);
                FatHaloSky.ApplyFarDefaults(k.Mat);
                meshes++; verts += n;
                k.V = null; k.N = null; k.U = null; k.C = null;
            }
            s_ready = true;
            FiresLogger.LogInfo($"[FatHalo] far trees placed: {trees} trees in {meshes} species mesh(es), {verts} verts, {(Time.realtimeSinceStartup - s_started) * 1000f:F0} ms (cap {MaxTrees}; hidden inside {HideNear:F0} m)");
        }
    }
}
