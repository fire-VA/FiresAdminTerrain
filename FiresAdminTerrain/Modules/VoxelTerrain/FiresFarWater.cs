using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // FAR WATER QUILT — rivers/ponds visibly STOPPED at the voxel wrap ring
    // while the distant terrain (built from our replaced base heights, so
    // the carved channels are all there) ran on empty. Render-only water
    // built straight from the GLOBAL reconciled surface: 512m tiles of 8m
    // quads at the true owned levels, out to ~3km around the camera. Same
    // sheet/ice materials as the near system, NO colliders, no swim — and
    // sunk 0.18m so overlapping near sheets always win without z-fighting.
    internal static class FiresFarWater
    {
        private const float TILE = 512f;
        private const float BUILD_R = 3000f;
        private const float DROP_R = 3600f;
        private const float SINK = 0.18f;

        private static readonly Dictionary<Vector2Int, GameObject> s_tiles =
            new Dictionary<Vector2Int, GameObject>(128);
        private static readonly List<Vector2Int> s_scratch = new List<Vector2Int>(128);
        private static GameObject s_root;
        private static float s_nextTick;
        private static bool s_logged;

        private static readonly List<Vector3> s_v = new List<Vector3>(8192);
        private static readonly List<Vector2> s_uv = new List<Vector2>(8192);
        private static readonly List<int> s_t = new List<int>(12288);
        private static readonly List<Vector3> s_iv = new List<Vector3>(2048);
        private static readonly List<Vector2> s_iuv = new List<Vector2>(2048);
        private static readonly List<int> s_it = new List<int>(3072);

        // Near-hole clip plumbing (see the Tick block): per-renderer MPB, shared buffers.
        private static MaterialPropertyBlock s_mpb;
        private static readonly List<MeshRenderer> s_mrScratch = new List<MeshRenderer>(4);
        private static readonly int P_FarClipRadius = Shader.PropertyToID("_FiresFarClipRadius");
        private static readonly int P_FarClipCenter = Shader.PropertyToID("_FiresFarClipCenter");

        internal static void Tick(Vector3 center)
        {
            if (!FiresWorldGenBase.Active || !FiresWorldGenBase.WaterSurfaceReady)
            {
                if (s_tiles.Count > 0) Clear();
                return;
            }
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + 0.25f;
            // NEVER build without the material: a tile built before the near
            // system bound its sheet material rendered Unity's MAGENTA error
            // colour forever (user screenshots). Wait for the bind.
            if (VoxelChunkView.BoundSheetMaterial == null) return;
            if (s_root == null)
            {
                s_root = new GameObject("FiresFarWater");
                Object.DontDestroyOnLoad(s_root);
            }
            // drop far tiles
            s_scratch.Clear();
            foreach (var kv in s_tiles)
            {
                Vector3 tc = new Vector3((kv.Key.x + 0.5f) * TILE, 0f, (kv.Key.y + 0.5f) * TILE);
                if (Mathf.Abs(tc.x - center.x) > DROP_R || Mathf.Abs(tc.z - center.z) > DROP_R)
                    s_scratch.Add(kv.Key);
            }
            for (int i = 0; i < s_scratch.Count; i++)
            {
                Object.Destroy(s_tiles[s_scratch[i]]);
                s_tiles.Remove(s_scratch[i]);
            }
            // build ONE missing tile per tick, nearest first
            int t0x = Mathf.FloorToInt((center.x - BUILD_R) / TILE), t1x = Mathf.FloorToInt((center.x + BUILD_R) / TILE);
            int t0z = Mathf.FloorToInt((center.z - BUILD_R) / TILE), t1z = Mathf.FloorToInt((center.z + BUILD_R) / TILE);
            Vector2Int best = default; float bestD = float.MaxValue; bool found = false;
            for (int tz = t0z; tz <= t1z; tz++)
                for (int tx = t0x; tx <= t1x; tx++)
                {
                    var key = new Vector2Int(tx, tz);
                    if (s_tiles.ContainsKey(key)) continue;
                    float dx = (tx + 0.5f) * TILE - center.x, dz = (tz + 0.5f) * TILE - center.z;
                    float d = dx * dx + dz * dz;
                    if (d > BUILD_R * BUILD_R || d >= bestD) continue;
                    bestD = d; best = key; found = true;
                }
            if (found) BuildTile(best);

            // NEAR-HOLE clip: the quilt covers everywhere (baked meshes can't hold a
            // moving hole), so a MaterialPropertyBlock-driven radial discard follows the
            // player instead — quilt pixels inside the near voxel system's owned radius
            // are clipped in-shader (_FiresFarClipRadius/_FiresFarClipCenter, MPB-only so
            // the SHARED sheet material is untouched on the real near sheets). Radius =
            // WrapKeepRadius (the streamer's guaranteed floor) minus a safety margin, so
            // the hole can never exceed actual near coverage and open a water gap. Kills
            // the hard straight quilt seams at river banks inside loaded areas.
            float clipR = Mathf.Max(0f,
                (FiresVoxelConfig.WrapKeepRadius != null ? FiresVoxelConfig.WrapKeepRadius.Value : 128f) - 8f);
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            s_mpb.SetFloat(P_FarClipRadius, clipR);
            s_mpb.SetVector(P_FarClipCenter, new Vector4(center.x, 0f, center.z, 0f));
            foreach (var kv in s_tiles)
            {
                if (kv.Value == null) continue;
                kv.Value.GetComponentsInChildren(true, s_mrScratch);
                for (int i = 0; i < s_mrScratch.Count; i++)
                    if (s_mrScratch[i] != null) s_mrScratch[i].SetPropertyBlock(s_mpb);
            }
        }

        internal static void Clear()
        {
            foreach (var kv in s_tiles) if (kv.Value != null) Object.Destroy(kv.Value);
            s_tiles.Clear();
        }

        // Vanilla's overworld render group flipped (interior entered or left).
        // These sheets stand in for the zone WaterSurface, which carries the
        // same group in the vanilla zone prefab.
        internal static void RefreshVisibility()
        {
            foreach (var kv in s_tiles)
            {
                if (kv.Value == null) continue;
                kv.Value.GetComponentsInChildren(true, s_mrScratch);
                for (int i = 0; i < s_mrScratch.Count; i++) VoxelRenderGroup.Apply(s_mrScratch[i]);
            }
        }

        private static void BuildTile(Vector2Int key)
        {
            s_v.Clear(); s_uv.Clear(); s_t.Clear();
            s_iv.Clear(); s_iuv.Clear(); s_it.Clear();
            float x0 = key.x * TILE, z0 = key.y * TILE;
            const float CELL = 8f;
            const float UVS = 1f / 64f;
            int cells = (int)(TILE / CELL);
            for (int cz = 0; cz < cells; cz++)
            {
                float wz = z0 + cz * CELL;
                for (int cx = 0; cx < cells; cx++)
                {
                    float wx = x0 + cx * CELL;
                    byte cls = FiresWorldGenBase.WaterClassAt(wx + CELL * 0.5f, wz + CELL * 0.5f);
                    if (cls == FiresWorldGenBase.WC_NONE) continue;
                    if (!FiresWorldGenBase.TryGetWaterSurfaceSmooth(wx + CELL * 0.5f, wz + CELL * 0.5f,
                            out float lc, out _)) continue;
                    // corners bilinear off the same surface; fall back to the
                    // centre where a corner has no coverage
                    float y00 = Corner(wx, wz, lc), y10 = Corner(wx + CELL, wz, lc);
                    float y01 = Corner(wx, wz + CELL, lc), y11 = Corner(wx + CELL, wz + CELL, lc);
                    bool ice = cls == FiresWorldGenBase.WC_FROZEN;
                    var vl = ice ? s_iv : s_v;
                    var ul = ice ? s_iuv : s_uv;
                    var tl = ice ? s_it : s_t;
                    int b = vl.Count;
                    vl.Add(new Vector3(wx, y00 - SINK, wz));
                    vl.Add(new Vector3(wx + CELL, y10 - SINK, wz));
                    vl.Add(new Vector3(wx, y01 - SINK, wz + CELL));
                    vl.Add(new Vector3(wx + CELL, y11 - SINK, wz + CELL));
                    ul.Add(new Vector2(wx * UVS, wz * UVS));
                    ul.Add(new Vector2((wx + CELL) * UVS, wz * UVS));
                    ul.Add(new Vector2(wx * UVS, (wz + CELL) * UVS));
                    ul.Add(new Vector2((wx + CELL) * UVS, (wz + CELL) * UVS));
                    tl.Add(b); tl.Add(b + 2); tl.Add(b + 1);
                    tl.Add(b + 1); tl.Add(b + 2); tl.Add(b + 3);
                }
            }
            // Ice tiles wait for the ice material too — a missing material is
            // NEVER acceptable (magenta); the tile retries next tick.
            Material iceMat = null;
            if (s_it.Count > 0)
            {
                iceMat = VerdantsAscent.Water.Core.WaterMaterialManager.GetMaterial(
                    VerdantsAscent.Water.Core.CustomLiquidType.Ice);
                if (iceMat == null) return;
            }
            var go = new GameObject($"farwater_{key.x}_{key.y}");
            go.transform.SetParent(s_root.transform, false);
            s_tiles[key] = go;
            if (s_t.Count > 0)
                AddPart(go, "water", s_v, s_uv, s_t, VoxelChunkView.BoundSheetMaterial);
            if (s_it.Count > 0)
                AddPart(go, "ice", s_iv, s_iuv, s_it, iceMat);
            if (!s_logged && (s_t.Count > 0 || s_it.Count > 0))
            {
                s_logged = true;
                FiresLogger.LogInfo($"[FiresFarWater] first far tile built at ({key.x},{key.y}) — " +
                          $"{s_t.Count / 6} water + {s_it.Count / 6} ice quad(s), render-only.");
            }
        }

        private static float Corner(float wx, float wz, float fallback)
            => FiresWorldGenBase.TryGetWaterSurfaceSmooth(wx, wz, out float l, out _) ? l : fallback;

        private static void AddPart(GameObject parent, string name,
            List<Vector3> v, List<Vector2> uv, List<int> t, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            if (mat != null) mr.sharedMaterial = mat;
            VoxelRenderGroup.Apply(mr);
            var mesh = new Mesh { name = "farwater", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv);
            var norms = new Vector3[v.Count];
            for (int i = 0; i < norms.Length; i++) norms[i] = Vector3.up;
            mesh.SetNormals(norms);
            mesh.SetTriangles(t, 0, calculateBounds: true);
            mf.sharedMesh = mesh;
        }
    }
}
