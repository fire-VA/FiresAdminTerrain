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
        // 0.2.815: Custom/FiresInlandWater names them _FarClip / _FarCenter ([generator], FiresInlandWater.shader): the
        // _FiresFarClip* names matched nothing, so the near hole never clipped and the static 8 m far quilt drew through the
        // shallows next to the waving near sheet (Fire's "hard stepping edge that doesn't move"). Both pairs are set.
        private static readonly int P_FarClip = Shader.PropertyToID("_FarClip"), P_FarCenter = Shader.PropertyToID("_FarCenter");
        private static readonly int P_FarClipCenter = Shader.PropertyToID("_FiresFarClipCenter");


        // 0.2.817: `fat_farwater on|off` (client render only, this session): hides or shows every far-water tile, the A/B
        // for "is this static stepped water layer the far quilt?" (Fire's pale squares along a shore).
        internal static bool Hidden;
        private static bool s_clipLogged;

        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand("fat_farwater", "on|off - show or hide the far-water tiles (render only, this session)",
                args =>
                {
                    if (args.Args.Length > 1) Hidden = args.Args[1].Equals("off", System.StringComparison.OrdinalIgnoreCase);
                    if (s_root != null) s_root.SetActive(!Hidden);
                    string line = $"[FiresFarWater] far tiles {(Hidden ? "HIDDEN" : "shown")} ({s_tiles.Count} tile(s))";
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                });
        }
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
            if (s_root != null && s_root.activeSelf == Hidden) s_root.SetActive(!Hidden);
            if (!s_clipLogged)
            {
                s_clipLogged = true;   // 0.2.817: which near-hole names the LIVE shader has (the source on disk may be older than the bundle)
                var m = VoxelChunkView.BoundSheetMaterial;
                FiresLogger.LogInfo($"[FiresFarWater] sheet shader {(m.shader != null ? m.shader.name : "null")}: _FarClip {m.HasProperty("_FarClip")}, _FarCenter {m.HasProperty("_FarCenter")}, _FiresFarClipRadius {m.HasProperty("_FiresFarClipRadius")}, _FiresFarClipCenter {m.HasProperty("_FiresFarClipCenter")}");
            }
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
            UpdateHole(center);
            if (found) BuildTile(best);
            else if (s_holeStale.Count > 0) RebuildNearest(s_holeStale, center);
            else if (s_stale.Count > 0) RebuildNearestStale(center);

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
            s_mpb.SetFloat(P_FarClip, clipR);
            s_mpb.SetVector(P_FarCenter, new Vector4(center.x, 0f, center.z, 0f));
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
            s_stale.Clear();
        }

        // Vedr seasons: every live tile is rebuilt in place, nearest first, one per tick once no tile is missing. The old
        // tile is destroyed in the same frame its replacement is built, so distant water never blinks out.
        private static readonly HashSet<Vector2Int> s_stale = new HashSet<Vector2Int>();

        internal static void RebuildForSeason()
        {
            foreach (var kv in s_tiles) s_stale.Add(kv.Key);
        }

        private static void RebuildNearestStale(Vector3 center) => RebuildNearest(s_stale, center);

        // The nearest tile of `set` rebuilt in place (BuildTile replaces the old one only once the new one exists).
        private static void RebuildNearest(HashSet<Vector2Int> set, Vector3 center)
        {
            Vector2Int best = default; float bestD = float.MaxValue; bool found = false;
            foreach (var key in set)
            {
                float dx = (key.x + 0.5f) * TILE - center.x, dz = (key.y + 0.5f) * TILE - center.z;
                float d = dx * dx + dz * dz;
                if (d >= bestD) continue;
                bestD = d; best = key; found = true;
            }
            if (!found) return;
            set.Remove(best);
            if (!s_tiles.ContainsKey(best)) return;
            BuildTile(best);
        }

        // ── 0.2.818: the NEAR HOLE, on the CPU ───────────────────────────────────────────────────────────────────────
        // Fire 10-07: a static, 8 m-stepped water layer under the waving near sheet at every shore. The live
        // Custom/FiresInlandWater (Core's built-in port) has NO far-clip property of any name ([generator]), so the MPB clip
        // below never did anything and every far tile drew right under the player's feet, 0.18 m under the near sheet,
        // showing through its translucent shallows. Tiles are now built WITHOUT the cells inside a hole around the player
        // (HoleRadius = the streamer's guaranteed near coverage less HoleMargin, which also absorbs the player's movement
        // between rebuilds); once the player has moved HoleStep m, the tiles touching the old or the new hole are rebuilt in
        // place, nearest first.
        private const float HoleMargin = 40f, HoleStep = 24f;
        private static readonly HashSet<Vector2Int> s_holeStale = new HashSet<Vector2Int>();
        private static Vector2 s_hole;
        private static bool s_holeSet;
        private static float s_holeR;

        private static float HoleRadius
            => Mathf.Max(0f, (FiresVoxelConfig.WrapKeepRadius != null ? FiresVoxelConfig.WrapKeepRadius.Value : 128f) - HoleMargin);

        private static bool InHole(float x, float z)
        {
            if (!s_holeSet || s_holeR <= 0f) return false;
            float dx = x - s_hole.x, dz = z - s_hole.y, edge = s_holeR - 6f;   // the cell centre, a cell's half-diagonal inside
            return dx * dx + dz * dz < edge * edge;
        }

        private static void UpdateHole(Vector3 center)
        {
            var now = new Vector2(center.x, center.z);
            if (s_holeSet && (now - s_hole).sqrMagnitude < HoleStep * HoleStep) return;
            var before = s_hole;
            bool had = s_holeSet;
            s_hole = now;
            s_holeSet = true;
            s_holeR = HoleRadius;
            float reach = s_holeR + TILE;   // a tile within this of either hole centre may hold hole cells
            foreach (var key in s_tiles.Keys)
            {
                var tc = new Vector2((key.x + 0.5f) * TILE, (key.y + 0.5f) * TILE);
                if ((tc - now).magnitude < reach || (had && (tc - before).magnitude < reach)) s_holeStale.Add(key);
            }
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
                    if (InHole(wx + CELL * 0.5f, wz + CELL * 0.5f)) continue;   // 0.2.818: the near system draws this water
                    if (!FiresWorldGenBase.TryGetWaterSurfaceSmooth(wx + CELL * 0.5f, wz + CELL * 0.5f,
                            out float lc, out _)) continue;
                    // corners bilinear off the same surface; fall back to the
                    // centre where a corner has no coverage
                    float y00 = Corner(wx, wz, lc), y10 = Corner(wx + CELL, wz, lc);
                    float y01 = Corner(wx, wz + CELL, lc), y11 = Corner(wx + CELL, wz + CELL, lc);
                    bool ice = cls == FiresWorldGenBase.WC_FROZEN || FiresWorldGenBase.SeasonalFrozenAt(wx + CELL * 0.5f, wz + CELL * 0.5f);
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
            if (s_tiles.TryGetValue(key, out var previous) && previous != null) Object.Destroy(previous);   // 0.2.818: replaced in place, never blinks
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
