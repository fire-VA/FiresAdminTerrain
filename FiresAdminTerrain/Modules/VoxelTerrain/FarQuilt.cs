using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using FiresCore.Materials;

namespace VerdantsAscent.Terrain.Voxel
{
    // W3-A4 — the FAR-TERRAIN QUILT. With vanilla near terrain never building
    // (A1) and heights fully owned (0.2.69), this retires the last vanilla
    // terrain in view: distant-LOD heightmaps stop rendering and the horizon
    // is drawn from OUR generator instead — render-only grid tiles in the
    // same captured terrain material as the voxel chunks.
    //
    //   near ring: one 64m tile per zone out to Near Radius, hidden the
    //              moment that zone's voxel colliders go live (which also
    //              papers over the wrap-in gap while a zone streams);
    //   far ring:  256m supertiles out to Far Radius; ocean-floor tiles that
    //              never break the waterline are skipped entirely.
    //
    // Tiles sample FiresWorldGenBase.SampleFinalMeters — the same function
    // heights, voxels, minimap and placement use — so near/far agree by
    // construction. No colliders; pure render.
    public static class FarQuilt
    {
        private const float TICK = 0.5f;
        private const int BUILD_BUDGET = 2;        // tiles per tick
        private const int NEAR_VERTS = 33;         // 2m step over 64m
        private const int FAR_VERTS = 33;          // 8m step over 256m

        private sealed class Tile
        {
            public GameObject Go;
            public Mesh Mesh;
            public Texture2D Mask;      // per-tile paint mask (cache + wet band), pooled with the tile
            public bool MaskDone;       // false = mask (re)build queued
        }

        private static readonly Dictionary<Vector3Int, Tile> s_tiles = new Dictionary<Vector3Int, Tile>();
        private static readonly List<Vector3Int> s_drop = new List<Vector3Int>();
        private static readonly List<Vector3Int> s_want = new List<Vector3Int>();
        private static readonly Stack<Tile> s_pool = new Stack<Tile>();
        private static GameObject s_root;
        private static float s_next;
        private static float s_nextStatus;
        private static bool s_gridWasUnready;
        private static readonly List<Vector3> s_recolourVerts = new List<Vector3>(NEAR_VERTS * NEAR_VERTS);
        private static readonly List<Color32> s_recolourCols = new List<Color32>(NEAR_VERTS * NEAR_VERTS);

        // Vanilla's overworld render group flipped (interior entered or left).
        // The distant LOD it stands in for is a Heightmap, so it follows the
        // same group. Tiles are render-only, so the renderer is the whole state.
        internal static void RefreshVisibility()
        {
            foreach (var kv in s_tiles)
            {
                var go = kv.Value != null ? kv.Value.Go : null;
                if (go != null) VoxelRenderGroup.Apply(go.GetComponent<MeshRenderer>());
            }
        }

        // Live (non-tombstone) tile count — the fail-open gate for the
        // vanilla distant-LOD handoff.
        public static int LiveTileCount
        {
            get
            {
                int c = 0;
                foreach (var kv in s_tiles) if (kv.Value.Go != null) c++;
                return c;
            }
        }

        // THE FIRES LOW-LOD (user mandate: we own generation, terrain and
        // shaders — distance too; vanilla's TerrainLod is fully suppressed).
        // Resurrected from the quilt with every fix its failures taught us:
        // never builds before the worldgen grid is ready, never builds or
        // shows a near tile over voxel territory (IsZoneKnown), heartbeat +
        // phantom detector so it can never fail silently, dynamic fill
        // budget, per-build material rebind, analytic tangents, far-ring
        // waterline clamp. Same material and same height function as the
        // voxel skin — one look, one owner.
        // From FAT's one ZoneSystem.Start hook (FatVanillaHooks).
        internal static void OnZoneSystemStart()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            if (s_root != null) return;
            s_root = new GameObject("FiresLod");
            s_root.AddComponent<QuiltDriver>();
        }

        // The far field plus a chunk world's start-cave hill, so the far mesh agrees with the near voxels there.
        private static float FarHeight(float wx, float wz)
        {
            float h = FiresWorldGenBase.SampleFinalMeters(wx, wz);
            Zones.ZoneModeStartCave.AddHill(wx, wz, ref h);
            return h;
        }

        private sealed class QuiltDriver : MonoBehaviour
        {
            private void Update()
            {
                if (Time.time < s_next) return;
                s_next = Time.time + TICK;
                try { Tick(); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] far quilt tick threw: {ex.Message}"); }
            }

            private void OnDestroy()
            {
                // GOs/meshes die with the scene, but HideAndDontSave mask
                // textures do NOT — destroy them or they leak across logins.
                foreach (var kv in s_tiles)
                    if (kv.Value.Mask != null) Destroy(kv.Value.Mask);
                while (s_pool.Count > 0)
                {
                    var t = s_pool.Pop();
                    if (t.Mask != null) Destroy(t.Mask);
                }
                s_tiles.Clear();
                s_root = null;
            }
        }

        private static bool Enabled =>
            VoxelOnlyMode.Active
            && FiresVoxelConfig.FarQuiltEnabled != null && FiresVoxelConfig.FarQuiltEnabled.Value;

        private static void Tick()
        {
            if (!Enabled)
            {
                if (s_tiles.Count > 0) ClearAll();
                return;
            }
            // NEVER build from an absent grid: SampleFinalMeters returns 0m
            // (sea floor) until worldgen finishes, and every tile built in
            // that window is garbage — far supertiles even tombstone as
            // "underwater" permanently. Wait, and rebuild anything that
            // slipped through on older builds.
            if (FiresWorldGenBase.Active && !FiresWorldGenBase.GridReady)
            {
                s_gridWasUnready = true;
                return;
            }
            if (s_gridWasUnready)
            {
                s_gridWasUnready = false;
                if (s_tiles.Count > 0)
                {
                    ClearAll();
                    FiresLogger.LogInfo("[FiresVoxel] far quilt: worldgen grid became ready — rebuilt all tiles (pre-grid tiles were sea-floor garbage).");
                }
            }
            var player = Player.m_localPlayer;
            if (player == null) return;
            Vector3 c = player.transform.position;

            float near = FiresVoxelConfig.FarQuiltNearRadius.Value;
            float far = FiresVoxelConfig.FarQuiltFarRadius.Value;

            // Desired tile set. Key = (x, ring, z); ring-0 keys are ZONE
            // coordinates (tiles sit exactly on vanilla zone squares, centers
            // at zone*64, so voxel-solid visibility maps 1:1).
            s_want.Clear();
            int nzMin = Mathf.FloorToInt((c.x - near + 32f) / 64f), nzMax = Mathf.FloorToInt((c.x + near + 32f) / 64f);
            int nzMinZ = Mathf.FloorToInt((c.z - near + 32f) / 64f), nzMaxZ = Mathf.FloorToInt((c.z + near + 32f) / 64f);
            for (int tz = nzMinZ; tz <= nzMaxZ; tz++)
                for (int tx = nzMin; tx <= nzMax; tx++)
                    s_want.Add(new Vector3Int(tx, 0, tz));
            int fMin = Mathf.FloorToInt((c.x - far) / 256f), fMax = Mathf.FloorToInt((c.x + far) / 256f);
            int fMinZ = Mathf.FloorToInt((c.z - far) / 256f), fMaxZ = Mathf.FloorToInt((c.z + far) / 256f);
            for (int tz = fMinZ; tz <= fMaxZ; tz++)
                for (int tx = fMin; tx <= fMax; tx++)
                    s_want.Add(new Vector3Int(tx, 1, tz));

            // Drop out-of-range tiles.
            s_drop.Clear();
            foreach (var kv in s_tiles)
            {
                float r = kv.Key.y == 0 ? near : far;
                TileCenter(kv.Key, out float cx, out float cz, out float size);
                if (Mathf.Abs(cx - c.x) > r + size || Mathf.Abs(cz - c.z) > r + size)
                    s_drop.Add(kv.Key);
            }
            foreach (var k in s_drop) Release(k);

            // Visibility: near tiles hide while their zone is voxel-solid;
            // far tiles hide where they overlap the near ring.
            foreach (var kv in s_tiles)
                UpdateVisibility(kv.Key, kv.Value, c, near);

            // Build missing, budgeted. The flat 2/tick budget took ~85s to
            // fill a fresh 338-tile horizon (boot-3 log) — the player stared
            // at holes for a minute. Ramp hard while the deficit is large
            // (login/teleport), settle to the trickle for steady-state.
            int missing = s_want.Count - s_tiles.Count;
            int budget = missing > 60 ? 12 : BUILD_BUDGET;
            int built = 0;
            foreach (var key in s_want)
            {
                if (built >= budget) break;
                if (s_tiles.ContainsKey(key)) continue;
                if (BuildTile(key, c, near)) built++;
            }

            DrainMasks();

            // Status heartbeat — the lod must never fail silently again.
            if (Time.time >= s_nextStatus)
            {
                s_nextStatus = Time.time + 10f;
                int live = 0, shown = 0, tomb = 0, phantom = 0;
                foreach (var kv in s_tiles)
                {
                    if (kv.Value.Go == null) { tomb++; continue; }
                    live++;
                    if (!kv.Value.Go.activeSelf) continue;
                    shown++;
                    // A VISIBLE near tile over SOLID voxel territory is the
                    // phantom analytic sheet (no carves, no collision) — the
                    // exact artifact the user screenshotted. Never silent.
                    if (kv.Key.y == 0 && VoxelZoneStreamer.IsZoneSolid(new Vector2s(kv.Key.x, kv.Key.z)))
                        phantom++;
                }
                if (phantom > 0)
                    FiresLogger.LogError($"[FiresVoxel] far quilt: {phantom} PHANTOM near tile(s) visible over voxel territory — analytic sheets are covering real carved ground!");
                if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                {
                    // COVERAGE PROOF, not just counts: what material the lod
                    // actually wears (a Standard-fallback tile renders broken/
                    // invisible) and the real distance to the nearest VISIBLE
                    // tile per ring from the player. Void at a distance the
                    // heartbeat claims covered = rendering problem; void with
                    // a matching gap here = logic problem. One line decides.
                    float nearestNear = float.MaxValue, nearestFar = float.MaxValue;
                    string matName = "none";
                    foreach (var kv in s_tiles)
                    {
                        var go = kv.Value.Go;
                        if (go == null || !go.activeSelf) continue;
                        if (matName == "none")
                        {
                            var mr3 = go.GetComponent<MeshRenderer>();
                            if (mr3 != null && mr3.sharedMaterial != null)
                                matName = $"{mr3.sharedMaterial.name}/{(mr3.sharedMaterial.shader != null ? mr3.sharedMaterial.shader.name : "?")}";
                        }
                        TileCenter(kv.Key, out float tcx, out float tcz, out float tsize);
                        float dTile = Mathf.Max(Mathf.Abs(tcx - c.x), Mathf.Abs(tcz - c.z)) - tsize * 0.5f;
                        if (kv.Key.y == 0) { if (dTile < nearestNear) nearestNear = dTile; }
                        else { if (dTile < nearestFar) nearestFar = dTile; }
                    }
                    // (coverage-proof heartbeat retired — quilt is stable; the
                    // PHANTOM error above still fires on real regressions)
                    _ = matName; _ = nearestNear; _ = nearestFar;
                }
            }
        }


        // Capture/recapture fan-out: live tiles keep their built-with material
        // otherwise — the stale-instance border class on the whole horizon.
        public static void RefreshAllTileMaterials(Material mat)
        {
            if (mat == null) return;
            foreach (var kv in s_tiles)
            {
                var go = kv.Value.Go;
                if (go == null) continue;   // tombstone
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null && mr.sharedMaterial != mat) mr.sharedMaterial = mat;
            }
        }

        // ── W3-A4b: per-tile PAINT masks ─────────────────────────────────
        // The quilt wears the shared captured material whose _ClearedMaskTex
        // is a 1x1 "no paint" — the horizon rendered NO paint at all: no
        // cached player paint, no worldgen river band, so painted river beds
        // cut off at the loaded-zone edge. Each tile gets a 65x65 mask
        // composed by DistantPaintCache.OverlayMask (visited-zone cache +
        // PaintWet01 wet band) and bound per-renderer via
        // MaterialPropertyBlock — the VoxelChunkView idiom, shared material
        // untouched — plus the tile's local gen waterline so inland shores
        // darken on the horizon exactly like the near field.
        private const int MASK_RES = 65;
        private static readonly int s_maskProp = Shader.PropertyToID("_ClearedMaskTex");
        private static readonly int s_localWaterProp = Shader.PropertyToID("_FiresLocalWaterY");
        private static Color32[] s_maskPx;
        private static MaterialPropertyBlock s_mpb;
        private static bool s_masksShutdown;

        private static void DrainMasks()
        {
            if (!Paint.DistantPaintCache.MaskFeatureOn)
            {
                // Live-toggle heal: clear every bound mask once, re-mask on re-enable.
                if (!s_masksShutdown)
                {
                    s_masksShutdown = true;
                    foreach (var kv in s_tiles)
                    {
                        var t = kv.Value;
                        if (t.Go == null) continue;
                        var mr = t.Go.GetComponent<MeshRenderer>();
                        if (mr != null) mr.SetPropertyBlock(null);
                        if (t.Mask != null) { Object.Destroy(t.Mask); t.Mask = null; }
                        t.MaskDone = false;
                    }
                }
                return;
            }
            s_masksShutdown = false;
            // Wet layer not queryable yet → every mask built now would need a
            // rebuild the moment it is. Wait — near paint appears at the same
            // readiness point, so the horizon stays consistent with the field.
            if (FiresWorldGenBase.Active && !FiresWorldGenBase.WaterSurfaceReady) return;
            int pending = 0;
            foreach (var kv in s_tiles)
                if (kv.Value.Go != null && !kv.Value.MaskDone) pending++;
            if (pending == 0) return;
            int budget = pending > 60 ? 12 : 4;
            foreach (var kv in s_tiles)
            {
                if (budget <= 0) break;
                var t = kv.Value;
                if (t.Go == null || t.MaskDone) continue;
                budget--;
                BuildMask(kv.Key, t);
            }
        }

        private static void BuildMask(Vector3Int key, Tile t)
        {
            t.MaskDone = true;
            float size = key.y == 0 ? 64f : 256f;
            float ox = key.y == 0 ? key.x * 64f - 32f : key.x * 256f;
            float oz = key.y == 0 ? key.z * 64f - 32f : key.z * 256f;
            float texel = size / (MASK_RES - 1);

            if (s_maskPx == null) s_maskPx = new Color32[MASK_RES * MASK_RES];
            var px = s_maskPx;
            // In ASHLANDS the mask ALPHA is LAVA (vanilla GetVegetationMask
            // semantics) — the old flat 255 default rendered every far tile
            // in the crescent fully molten (user screenshot: all low-LOD
            // Ashlands reads as lava). Ashlands texels take the WORLDGEN
            // lava value (caldera pools, flows, flats fields); everywhere
            // else keeps the 255 default the quilt shader expects.
            for (int j = 0; j < MASK_RES; j++)
                for (int i2 = 0; i2 < MASK_RES; i2++)
                {
                    float pxw = ox + i2 * texel, pzw = oz + j * texel;
                    byte aDef = 255;
                    if (FiresWorldGenBase.BiomeCodeAt(pxw, pzw) == 6)
                        aDef = (byte)(Mathf.Clamp01(FiresWorldGenBase.SampleLavaMask(pxw, pzw).a) * 255f);
                    px[j * MASK_RES + i2] = new Color32(0, 0, 0, aDef);
                }
            Paint.DistantPaintCache.OverlayMask(ox, oz, texel, MASK_RES, px,
                out bool changed, out bool wetPending);
            if (wetPending) { t.MaskDone = false; return; }

            // Local gen waterline — coarse max over the footprint, the
            // VoxelWorld.RefreshPaintMask recipe (0 = dry, shader collapses
            // to vanilla).
            float waterY = 0f;
            if (FiresWorldGenBase.Active && FiresWorldGenBase.WaterSurfaceReady)
            {
                float stepC = size * 0.25f;
                for (int j = 0; j <= 4; j++)
                    for (int i = 0; i <= 4; i++)
                    {
                        float pxw = ox + i * stepC, pzw = oz + j * stepC;
                        // Cold-biome water never feeds the sand band — snow
                        // shores stay snow on the horizon too.
                        byte pb = FiresWorldGenBase.BiomeCodeAt(pxw, pzw);
                        if (pb == 3 || pb == 7) continue;
                        if (FiresWorldGenBase.TryGetWaterSurfaceSmooth(pxw, pzw, out float lvl, out float depth)
                            && depth >= 0.25f && lvl > 30.25f && lvl > waterY)
                            waterY = lvl;
                    }
            }

            var mr = t.Go.GetComponent<MeshRenderer>();
            if (mr == null) return;
            if (changed)
            {
                if (t.Mask == null)
                    t.Mask = new Texture2D(MASK_RES, MASK_RES, TextureFormat.RGBA32, mipChain: false)
                    {
                        name = "FarQuiltPaint",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                t.Mask.SetPixels32(px);
                t.Mask.Apply(false, false);
            }
            else if (t.Mask != null)
            {
                // Pooled tile that carried paint at its old position — drop it.
                Object.Destroy(t.Mask);
                t.Mask = null;
            }
            if (t.Mask == null && waterY <= 0f) { mr.SetPropertyBlock(null); return; }
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            s_mpb.Clear();
            if (t.Mask != null) s_mpb.SetTexture(s_maskProp, t.Mask);
            s_mpb.SetFloat(s_localWaterProp, waterY);
            mr.SetPropertyBlock(s_mpb);
        }

        // A zone's paint changed (fresh TerrainComp capture) — the lod tiles
        // covering it re-mask. Called by DistantPaintCache.Capture.
        public static void OnZonePaintChanged(Vector2s zone)
        {
            if (s_tiles.Count == 0) return;
            if (s_tiles.TryGetValue(new Vector3Int(zone.x, 0, zone.y), out var n) && n.Go != null)
                n.MaskDone = false;
            // A 64m zone can straddle up to 2x2 far supertiles.
            float minX = zone.x * 64f - 32f, minZ = zone.y * 64f - 32f;
            for (int dz = 0; dz <= 1; dz++)
                for (int dx = 0; dx <= 1; dx++)
                {
                    var fk = new Vector3Int(
                        Mathf.FloorToInt((minX + dx * 63.9f) / 256f), 1,
                        Mathf.FloorToInt((minZ + dz * 63.9f) / 256f));
                    if (s_tiles.TryGetValue(fk, out var f) && f.Go != null)
                        f.MaskDone = false;
                }
        }

        private static void TileCenter(Vector3Int key, out float cx, out float cz, out float size)
        {
            if (key.y == 0) { size = 64f; cx = key.x * 64f; cz = key.z * 64f; }
            else { size = 256f; cx = key.x * 256f + 128f; cz = key.z * 256f + 128f; }
        }

        private static void UpdateVisibility(Vector3Int key, Tile t, Vector3 c, float near)
        {
            if (t.Go == null) return;
            bool show;
            if (key.y == 0)
            {
                // Hide only when the zone is actually SOLID (voxel meshes
                // live). Hiding at claim-time left a VOID RING at the loaded
                // edge — zones claimed by the streamer but minutes from
                // meshing had neither tile nor voxel. During the wrap-in
                // window the tile coexists with the growing voxel mesh
                // (analytic ≈ uncarved surface, −0.8m under it) — a far,
                // brief, tolerable overlap instead of a hole in the world.
                show = !VoxelZoneStreamer.IsZoneSolid(new Vector2s(key.x, key.z));
            }
            else
            {
                // Hide a far supertile ONLY when the near-tile square fully
                // covers it. The old center-distance test hid tiles whose
                // centers sat inside `near` while their outer halves were the
                // sole cover for the 448–512m annulus — a land-free band that
                // tracked the player (the "banding" screenshots). Overlap is
                // harmless (far ring sits −2m under the near tiles); a gap is
                // a hole in the world.
                TileCenter(key, out float cx, out float cz, out float fsize);
                float half = fsize * 0.5f;
                show = Mathf.Abs(cx - c.x) + half > near || Mathf.Abs(cz - c.z) + half > near;
            }
            if (t.Go.activeSelf != show) t.Go.SetActive(show);
        }

        private static bool BuildTile(Vector3Int key, Vector3 c, float near)
        {
            // Near tiles BUILD even over solid voxel territory — they spawn
            // hidden (UpdateVisibility at the end of this method) and the
            // per-frame visibility pass reveals them the instant the zone
            // unwraps. The old build-skip left zones the player MOVED AWAY
            // from with no tile at all: unwrap happened, the tile then had to
            // queue through the build budget, and the trailing gap showed
            // neither voxel nor lod ("chunks load too slow, show neither").
            float size = key.y == 0 ? 64f : 256f;
            int verts = key.y == 0 ? NEAR_VERTS : FAR_VERTS;
            float ox = key.y == 0 ? key.x * 64f - 32f : key.x * 256f;
            float oz = key.y == 0 ? key.z * 64f - 32f : key.z * 256f;
            float step = size / (verts - 1);

            var vs = new Vector3[verts * verts];
            var cols = new Color32[verts * verts];
            var uvs = new Vector2[verts * verts];
            float maxH = float.MinValue;
            var wg = WorldGenerator.instance;
            for (int z = 0; z < verts; z++)
            {
                for (int x = 0; x < verts; x++)
                {
                    float wx = ox + x * step, wz = oz + z * step;
                    float h = FarHeight(wx, wz);
                    if (h > maxH) maxH = h;
                    int i = z * verts + x;
                    vs[i] = new Vector3(wx - ox, h, wz - oz);
                    uvs[i] = new Vector2(x / (float)(verts - 1), z / (float)(verts - 1));
                    // Corner-blended like the voxel skin (vanilla parity) so
                    // the lod and the near field agree at the handoff and
                    // biome boundaries wear the same 64m gradients.
                    cols[i] = wg != null
                        ? FatSeasons.Winterize(Zones.BiomePaint.Tint(BiomeCornerBlend.Sample(wx, wz), wx, wz), wx, wz)
                        : new Color32(0, 0, 0, 0);
                }
            }
            // Ocean-floor supertiles that never break the surface: skip, the
            // water plane hides them anyway. (Near ring keeps them — shores.)
            if (key.y == 1 && maxH < 27f)
            {
                s_tiles[key] = new Tile();   // tombstone: counted as present
                return false;
            }

            // Normals sample BEYOND the tile edge (analytic — same function),
            // never clamped inside: clamping gave every tile a one-sided edge
            // normal and painted a visible shading seam along every tile
            // border (the "zone stitch" lines seen from the air).
            var norms = new Vector3[verts * verts];
            for (int z = 0; z < verts; z++)
            {
                for (int x = 0; x < verts; x++)
                {
                    int i = z * verts + x;
                    float hl = x > 0 ? vs[(x - 1) + z * verts].y : FarHeight(ox - step, oz + z * step);
                    float hr = x < verts - 1 ? vs[(x + 1) + z * verts].y : FarHeight(ox + size + step, oz + z * step);
                    float hd = z > 0 ? vs[x + (z - 1) * verts].y : FarHeight(ox + x * step, oz - step);
                    float hu = z < verts - 1 ? vs[x + (z + 1) * verts].y : FarHeight(ox + x * step, oz + size + step);
                    norms[i] = new Vector3(hl - hr, 2f * step, hd - hu).normalized;
                }
            }
            var tris = new int[(verts - 1) * (verts - 1) * 6];
            int ti = 0;
            for (int z = 0; z < verts - 1; z++)
            {
                for (int x = 0; x < verts - 1; x++)
                {
                    int i = z * verts + x;
                    tris[ti++] = i; tris[ti++] = i + verts; tris[ti++] = i + 1;
                    tris[ti++] = i + 1; tris[ti++] = i + verts; tris[ti++] = i + verts + 1;
                }
            }

            // Far ring: bake the −2.0m chord budget into the VERTICES (after
            // the normals loop — shading reads raw heights) and clamp samples
            // that are genuinely above water to just above the 30m plane. A
            // uniform transform drop was sinking distant SHORELINES under the
            // water plane; deep/concave ground keeps the full budget (hidden
            // by water anyway).
            if (key.y == 1)
            {
                for (int i = 0; i < vs.Length; i++)
                {
                    float h = vs[i].y;
                    float y = h - 2.0f;
                    if (h > 30.05f) y = Mathf.Max(y, 30.1f);
                    vs[i].y = y;
                }
            }

            var tile = s_pool.Count > 0 ? s_pool.Pop() : null;
            if (tile == null)
            {
                tile = new Tile();
                tile.Go = new GameObject("FarQuiltTile");
                tile.Go.transform.SetParent(s_root.transform, false);
                var mf = tile.Go.AddComponent<MeshFilter>();
                var mr = tile.Go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                tile.Mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt16 };
                mf.sharedMesh = tile.Mesh;
            }
            // Material on EVERY build (not just creation): pooled tiles kept
            // whatever instance they were born with — after a capture upgrade
            // or recapture, mixed instances met exactly at tile borders.
            {
                var mr2 = tile.Go.GetComponent<MeshRenderer>();
                var cur = TerrainMaterialCache.GetCurrentMaterial();
                if (mr2 != null && cur != null && mr2.sharedMaterial != cur) mr2.sharedMaterial = cur;
                // A pooled tile's mask belongs to its OLD position — never let
                // it show here. Cleared now, rebuilt by the mask drain.
                if (mr2 != null) mr2.SetPropertyBlock(null);
                VoxelRenderGroup.Apply(mr2);
                tile.MaskDone = false;
            }
            // Underlay depth must clear the surface-nets reconstruction band
            // AND the quilt's own chord overshoot: heightfields sampled at 2m
            // (near) / 8m (far) steps render the linear chord between samples,
            // which rises up to step²/8·|h''| above the analytic surface in
            // concave terrain — −1.0m far only budgeted the voxel ±0.5m band
            // and poked through the near quilt in valleys. Far tiles bake a
            // −2.0m offset into their VERTICES with a waterline clamp (above);
            // near stays a −0.80m transform (2m steps ⇒ 0.5·|h''| chord,
            // within budget). Offsets stay distinct — the coplanar z-fight
            // class stays banned.
            tile.Go.transform.position = new Vector3(ox, key.y == 1 ? 0f : -0.8f, oz);
            // Analytic world-anchored tangents (same formula as the voxel
            // mesher): pure function of the normal, so adjacent tiles and
            // voxel chunks share an identical normal-map basis — per-mesh
            // RecalculateTangents drew shading lines along tile borders.
            var tans = new Vector4[vs.Length];
            for (int i = 0; i < norms.Length; i++)
            {
                var nn = norms[i];
                Vector3 ax = (nn.x < 0.99f && nn.x > -0.99f) ? Vector3.right : Vector3.forward;
                Vector3 tan = ax - nn * Vector3.Dot(nn, ax);
                float tm = tan.magnitude;
                tan = tm > 1e-5f ? tan / tm : Vector3.forward;
                tans[i] = new Vector4(tan.x, tan.y, tan.z, -1f);
            }
            tile.Mesh.Clear();
            tile.Mesh.vertices = vs;
            tile.Mesh.triangles = tris;
            tile.Mesh.normals = norms;
            tile.Mesh.tangents = tans;
            tile.Mesh.uv = uvs;
            tile.Mesh.colors32 = cols;
            tile.Mesh.RecalculateBounds();
            tile.Go.SetActive(true);
            s_tiles[key] = tile;
            UpdateVisibility(key, tile, c, near);
            return true;
        }

        // Vedr seasons: live tiles take the new snow biomes in place (pooled tiles recolour when rebuilt).
        internal static void RecolourForSeason()
        {
            if (WorldGenerator.instance == null) return;
            foreach (var kv in s_tiles)
            {
                var t = kv.Value;
                if (t == null || t.Go == null || t.Mesh == null) continue;
                Vector3 origin = t.Go.transform.position;
                t.Mesh.GetVertices(s_recolourVerts);
                s_recolourCols.Clear();
                foreach (var v in s_recolourVerts)
                {
                    float wx = origin.x + v.x, wz = origin.z + v.z;
                    s_recolourCols.Add(FatSeasons.Winterize(Zones.BiomePaint.Tint(BiomeCornerBlend.Sample(wx, wz), wx, wz), wx, wz));
                }
                t.Mesh.SetColors(s_recolourCols);
            }
        }

        private static void Release(Vector3Int key)
        {
            if (!s_tiles.TryGetValue(key, out var t)) return;
            s_tiles.Remove(key);
            if (t.Go == null) return;   // tombstone
            t.Go.SetActive(false);
            s_pool.Push(t);
        }

        private static void ClearAll()
        {
            foreach (var kv in s_tiles)
            {
                if (kv.Value.Go != null) Object.Destroy(kv.Value.Go);
                if (kv.Value.Mesh != null) Object.Destroy(kv.Value.Mesh);
                if (kv.Value.Mask != null) Object.Destroy(kv.Value.Mask);
            }
            s_tiles.Clear();
            // Pooled tiles hold GOs/meshes/masks too — HideAndDontSave masks
            // survive scene unloads, so they MUST be destroyed explicitly.
            while (s_pool.Count > 0)
            {
                var t = s_pool.Pop();
                if (t.Go != null) Object.Destroy(t.Go);
                if (t.Mesh != null) Object.Destroy(t.Mesh);
                if (t.Mask != null) Object.Destroy(t.Mask);
            }
        }
    }
}
