using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using FiresCore.Materials;

namespace VerdantsAscent.Terrain.Voxel
{
    // W3-A4 — the FAR-TERRAIN QUILT. With vanilla near terrain never building
    // (A1) and heights fully owned, this retires the last vanilla
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
        private static int s_triRebuildsThisTick;
        private const int BUILD_BUDGET = 1;        // tiles per tick (~13 tiles turn over at each 64 m crossing)
        private const int NEAR_VERTS = 33;         // 2m step over 64m
        private const int FAR_VERTS = 33;          // 8m step over 256m

        private sealed class Tile
        {
            public GameObject Go;
            public Mesh Mesh;
            public Texture2D Mask;      // per-tile paint mask (cache + wet band), pooled with the tile
            public bool MaskDone;       // false = mask (re)build queued
            public long SolidMask = -1; // Far supertile: which zones under it were solid when its triangles were built
        }

        // ── 0.2.1414 FarLands #3 (Tools\DESIGN_FAT_FarForest_FarTerrain_20261108.md): [Far Terrain] ExtendedReach ──────────────
        // Two more rings of the same 33x33 tiles, same material, masks and height function: ring 2 = 512 m tiles (16 m step) out
        // to 3072 m, ring 3 = 1024 m tiles (32 m step) out to ReachMeters. Off = rings 0/1 exactly as before.
        // Each outer ring hides where the ring inside it fully covers it (the ring-1 rule) and sits a little lower (ring 1 -2 m,
        // ring 2 -4 m, ring 3 -8 m; shoreline clamp just above the 30 m plane, lower per ring), so overlaps never z-fight.
        internal static BepInEx.Configuration.ConfigEntry<bool> ExtendedReach;
        internal static BepInEx.Configuration.ConfigEntry<float> ReachMeters;
        private const float Ring2Max = 3072f;

        internal static void BindFarTerrain(BepInEx.Configuration.ConfigFile cfg)
        {
            ExtendedReach = cfg.Bind("Far Terrain", "ExtendedReach", false,
                "FarLands: draw the far terrain out to ReachMeters with two more rings (512 m tiles to 3072 m, 1024 m tiles beyond), same look as the far quilt. Fires worlds only. Default off.");
            ReachMeters = cfg.Bind("Far Terrain", "ReachMeters", 4096f,
                new BepInEx.Configuration.ConfigDescription("How far the extended far terrain reaches (m). Fog and the camera's far clip decide how much of it shows.",
                    new BepInEx.Configuration.AcceptableValueRange<float>(1536f, 6000f)));
        }

        private static bool ExtOn => ExtendedReach != null && ExtendedReach.Value;
        private static float RingSize(int ring) => ring == 0 ? 64f : ring == 1 ? 256f : ring == 2 ? 512f : 1024f;
        private static float RingRadius(int ring, float near, float far)
        {
            if (ring == 0) return near;
            if (ring == 1) return far;
            float reach = ReachMeters != null ? ReachMeters.Value : 4096f;
            return ring == 2 ? Mathf.Min(Ring2Max, reach) : reach;
        }
        internal static float RingDrop(int ring) => ring == 1 ? 2f : ring == 2 ? 4f : 8f;            // baked into vertices
        internal static float RingShoreClamp(int ring) => ring == 1 ? 30.1f : ring == 2 ? 30.08f : 30.06f;
        private static float RingOrigin(int ring, int k) => ring == 0 ? k * 64f - 32f : k * RingSize(ring);
        private static float s_tickFar = 1536f;   // this tick's Far Radius, for the ring-2/3 visibility rule
        private static bool s_extSaid;

        private static void SayExtended(float far)
        {
            if (s_extSaid) return;
            s_extSaid = true;
            var cam = Camera.main;
            float reach = ReachMeters.Value, clip = cam != null ? cam.farClipPlane : -1f;
            FiresLogger.LogInfo($"[FarTerrain] ExtendedReach on: far quilt rings to {reach:0} m (512 m tiles {far:0}-{Mathf.Min(Ring2Max, reach):0} m" +
                                (reach > Ring2Max ? $", 1024 m tiles to {reach:0} m" : "") + $"); camera far clip {(clip > 0 ? clip.ToString("0") + " m" : "unknown")}" +
                                (clip > 0 && clip < reach ? $" (WHY less shows: terrain past {clip:0} m is clipped by the camera)" : "") + ".");
        }
        private static int s_extBuilt; private static double s_extBuildMs; private static float s_extNextLog;

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
        internal static bool DebugHidden;   // fat_seam_suspect
        internal static void ApplyDebugHidden()
        {
            foreach (var kv in s_tiles) { var go = kv.Value != null ? kv.Value.Go : null; if (go != null && DebugHidden && go.activeSelf) go.SetActive(false); }
        }

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

        // THE FIRES LOW-LOD (we own generation, terrain and
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
        internal static float FarHeight(float wx, float wz)   // 0.2.1415: internal ([generator] #8/#10 sample the drawn far height)
        {
            if (FatSuperFlat.On) return FatSuperFlat.HeightAt(wx, wz);   // The far terrain is the same flat land + shore + sea floor
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
            FarTerrainVanilla.Tick(Enabled);   // 0.2.1418: ExtendedReach on worlds the far quilt doesn't draw (vanilla TerrainLod)
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
            s_tickFar = far;

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
            // 0.2.1414: rings 2 and 3 (ExtendedReach). Wanted AFTER rings 0/1, so the build budget still fills near-first.
            if (ExtOn)
            {
                SayExtended(far);
                for (int ring = 2; ring <= 3; ring++)
                {
                    float r = RingRadius(ring, near, far), inner = RingRadius(ring - 1, near, far), sz = RingSize(ring);
                    if (r <= inner) continue;
                    int a0 = Mathf.FloorToInt((c.x - r) / sz), a1 = Mathf.FloorToInt((c.x + r) / sz);
                    int b0 = Mathf.FloorToInt((c.z - r) / sz), b1 = Mathf.FloorToInt((c.z + r) / sz);
                    for (int tz = b0; tz <= b1; tz++)
                        for (int tx = a0; tx <= a1; tx++)
                        {
                            // Tiles wholly inside the inner ring's square would never show: not built at all.
                            float cx = tx * sz + sz * 0.5f, cz = tz * sz + sz * 0.5f, half = sz * 0.5f;
                            if (Mathf.Abs(cx - c.x) + half <= inner && Mathf.Abs(cz - c.z) + half <= inner) continue;
                            s_want.Add(new Vector3Int(tx, ring, tz));
                        }
                }
            }
            else s_extSaid = false;

            // Drop out-of-range tiles.
            s_drop.Clear();
            foreach (var kv in s_tiles)
            {
                if (kv.Key.y >= 2 && !ExtOn) { s_drop.Add(kv.Key); continue; }
                float r = RingRadius(kv.Key.y, near, far);
                TileCenter(kv.Key, out float cx, out float cz, out float size);
                if (Mathf.Abs(cx - c.x) > r + size || Mathf.Abs(cz - c.z) > r + size)
                    s_drop.Add(kv.Key);
            }
            foreach (var k in s_drop) Release(k);

            // Visibility: near tiles hide while their zone is voxel-solid;
            // far tiles hide where they overlap the near ring.
            s_triRebuildsThisTick = 0;
            foreach (var kv in s_tiles)
                UpdateVisibility(kv.Key, kv.Value, c, near);

            // Build missing, budgeted. The flat 2/tick budget took ~85s to
            // fill a fresh 338-tile horizon (boot-3 log) — the player stared
            // at holes for a minute. Ramp hard while the deficit is large
            // (login/teleport), settle to the trickle for steady-state.
            int missing = s_want.Count - s_tiles.Count;
            int budget = missing > 60 ? 4 : BUILD_BUDGET;   // login/teleport ramp 12 -> 4 (each build is ~1.1k sampled vertices + a mesh upload)
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
                    // exact artifact that was seen in game. Never silent.
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
            int budget = pending > 60 ? 6 : 2;   // Mask builds (65x65 sampling + Texture2D.Apply) 4 -> 2 a tick, ramp 12 -> 6
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
            float size = RingSize(key.y);
            float ox = RingOrigin(key.y, key.x);
            float oz = RingOrigin(key.y, key.z);
            float texel = size / (MASK_RES - 1);

            if (s_maskPx == null) s_maskPx = new Color32[MASK_RES * MASK_RES];
            var px = s_maskPx;
            // In ASHLANDS the mask ALPHA is LAVA (vanilla GetVegetationMask
            // semantics) — the old flat 255 default rendered every far tile
            // in the crescent fully molten (all low-LOD
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

        // Pad seams that fix themselves up close: read-only, for fat_terrain_seam_test. The ACTIVE quilt
        // tiles whose square contains (x,z), as "near"/"far" + their y offset, and the quilt surface height there (the generator's
        // height + that offset), so the test can say where a drawn quilt tile stands ABOVE the edited voxel ground (poking through a
        // levelled pad at range).
        internal static int ActiveTilesAt(float x, float z, out float quiltY, out string kinds)
        {
            int n = 0; quiltY = float.MinValue; kinds = "";
            foreach (var kv in s_tiles)
            {
                var t = kv.Value;
                if (t == null || t.Go == null || !t.Go.activeInHierarchy) continue;
                TileCenter(kv.Key, out float cx, out float cz, out float size);
                float h = size * 0.5f;
                if (x < cx - h || x > cx + h || z < cz - h || z > cz + h) continue;
                n++;
                float y = FiresWorldGenBase.SampleFinalMeters(x, z) + t.Go.transform.position.y - (kv.Key.y >= 1 ? RingDrop(kv.Key.y) : 0f);   // far tiles bake -2 m (ring 2 -4, ring 3 -8) into their vertices
                if (y > quiltY) quiltY = y;
                kinds += kv.Key.y == 0 ? "near " : "far ";
            }
            return n;
        }

        // Pad seams at 20-30 m: the far ring (256 m supertiles at the generator's height, -2 m) has its quads
        // over SOLID voxel zones left out, the way near tiles already hide per zone; quads over zones still streaming stay (no hole in
        // the world). Mask bit = zone index inside the 5x5 zone window the supertile can touch.
        private static int s_holedRebuilds;
        internal static int HoledRebuilds => s_holedRebuilds;

        private static long FarSolidMask(float ox, float oz)
        {
            long m = 0; int zx0 = Mathf.FloorToInt((ox + 32f) / 64f), zz0 = Mathf.FloorToInt((oz + 32f) / 64f), bit = 0;
            for (int dz = 0; dz < 5; dz++)
                for (int dx = 0; dx < 5; dx++, bit++)
                    if (VoxelZoneStreamer.IsZoneSolid(new Vector2s(zx0 + dx, zz0 + dz))) m |= 1L << bit;
            return m;
        }

        private static int[] BuildTris(bool far, int verts, float step, float ox, float oz)
        {
            var list = new List<int>((verts - 1) * (verts - 1) * 6);
            for (int z = 0; z < verts - 1; z++)
                for (int x = 0; x < verts - 1; x++)
                {
                    if (far)
                    {
                        float wx = ox + (x + 0.5f) * step, wz = oz + (z + 0.5f) * step;
                        if (VoxelZoneStreamer.IsZoneSolid(new Vector2s(Mathf.FloorToInt((wx + 32f) / 64f), Mathf.FloorToInt((wz + 32f) / 64f)))) continue;
                    }
                    int i = z * verts + x;
                    list.Add(i); list.Add(i + verts); list.Add(i + 1);
                    list.Add(i + 1); list.Add(i + verts); list.Add(i + verts + 1);
                }
            return list.ToArray();
        }

        private static void TileCenter(Vector3Int key, out float cx, out float cz, out float size)
        {
            if (key.y == 0) { size = 64f; cx = key.x * 64f; cz = key.z * 64f; }
            else { size = RingSize(key.y); cx = key.x * size + size * 0.5f; cz = key.z * size + size * 0.5f; }
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
                show = !(FatSuperFlat.On && !FiresVoxelConfig.WrapsAllTerrain) && !VoxelZoneStreamer.IsZoneSolid(new Vector2s(key.x, key.z));   // No near quilt on a HEIGHTMAP super flat (the vanilla heightmaps are the ground); a voxel super flat keeps the normal wrap-in tiles (no void ring)
            }
            else
            {
                // Hide a far supertile ONLY when the near-tile square fully
                // covers it. The old center-distance test hid tiles whose
                // centers sat inside `near` while their outer halves were the
                // sole cover for the 448–512m annulus — a land-free band that
                // tracked the player (the "banding"). Overlap is
                // harmless (far ring sits −2m under the near tiles); a gap is
                // a hole in the world.
                TileCenter(key, out float cx, out float cz, out float fsize);
                float half = fsize * 0.5f;
                float inner = key.y == 1 ? near : RingRadius(key.y - 1, near, s_tickFar);   // 0.2.1414: ring k hides under ring k-1 alike
                show = Mathf.Abs(cx - c.x) + half > inner || Mathf.Abs(cz - c.z) + half > inner;
            }
            // A far supertile re-triangulates when a zone under it turns solid (or stops being solid), so it never draws over
            // live voxel ground: no generator-height quilt poking through a levelled pad at range.
            if (key.y == 1 && show && t.Mesh != null)
            {
                TileCenter(key, out float tcx, out float tcz, out float tsize);
                float tox = tcx - tsize * 0.5f, toz = tcz - tsize * 0.5f;
                long m = FarSolidMask(tox, toz);
                // At most one far re-triangulation a tick; the others keep their old SolidMask and go next tick.
                if (m != t.SolidMask && s_triRebuildsThisTick++ < 1)
                {
                    t.Mesh.triangles = BuildTris(true, FAR_VERTS, tsize / (FAR_VERTS - 1), tox, toz);
                    t.SolidMask = m;
                    s_holedRebuilds++;
                }
            }
            if (DebugHidden) show = false;   // fat_seam_suspect quilt off (live, not saved)
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
            long tBuild = System.Diagnostics.Stopwatch.GetTimestamp();
            float size = RingSize(key.y);
            int verts = key.y == 0 ? NEAR_VERTS : FAR_VERTS;   // rings 1-3 all 33x33 (8 / 16 / 32 m steps)
            float ox = RingOrigin(key.y, key.x);
            float oz = RingOrigin(key.y, key.z);
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
            if (key.y >= 1 && maxH < 27f)
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
            long solidMask = key.y == 1 ? FarSolidMask(ox, oz) : 0L;
            var tris = BuildTris(key.y == 1, verts, step, ox, oz);

            // Far ring: bake the −2.0m chord budget into the VERTICES (after
            // the normals loop — shading reads raw heights) and clamp samples
            // that are genuinely above water to just above the 30m plane. A
            // uniform transform drop was sinking distant SHORELINES under the
            // water plane; deep/concave ground keeps the full budget (hidden
            // by water anyway).
            if (key.y >= 1)
            {
                float drop = RingDrop(key.y), clamp = RingShoreClamp(key.y);   // ring 1 = -2 m / 30.1 as before
                for (int i = 0; i < vs.Length; i++)
                {
                    float h = vs[i].y;
                    float y = h - drop;
                    if (h > 30.05f) y = Mathf.Max(y, clamp);
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
            tile.Go.transform.position = new Vector3(ox, key.y >= 1 ? 0f : -0.8f, oz);
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
            tile.SolidMask = solidMask;
            tile.Go.SetActive(true);
            s_tiles[key] = tile;
            UpdateVisibility(key, tile, c, near);
            if (key.y >= 2)
            {
                s_extBuilt++;
                s_extBuildMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tBuild) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (Time.time >= s_extNextLog)
                {
                    s_extNextLog = Time.time + 30f;
                    int r2 = 0, r3 = 0;
                    foreach (var kv in s_tiles) { if (kv.Value.Go == null) continue; if (kv.Key.y == 2) r2++; else if (kv.Key.y == 3) r3++; }
                    FiresLogger.LogDebug($"[FarTerrain] extended rings: {r2} x 512 m + {r3} x 1024 m tile(s) live; {s_extBuilt} built so far, {s_extBuildMs / s_extBuilt:0.00} ms avg build (main thread)");
                }
            }
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
