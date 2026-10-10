using UnityEngine;
using FiresCore.Materials;

namespace VerdantsAscent.Terrain.Persistence
{
    // Per-tile mesh generator. Builds a 65x65 vertex grid (64x64 quads,
    // 8192 triangles) from a TerrainTile's Heights + Paint arrays and
    // renders it via MeshFilter + MeshRenderer parented under the Tile.
    //
    // Independent of DirtFloorMeshCombiner — each tile renders its own
    // mesh slice. Border vertices coincide with neighbor tiles' borders by
    // construction (same world XZ → same vertex value), so adjacent
    // standalone tile meshes appear visually contiguous without explicit
    // weld logic. Phase 8c/8d may consolidate adjacent tiles' meshes into
    // one combined draw call for perf; for 8b, one draw per tile is fine.
    //
    // Material strategy:
    //   1. Look up "FiresAdminTerrain_Tile" in the asset bundle.
    //   2. Fall back to a Standard-shader instance (visible, lit, untextured).
    // Phase 8c may add per-tile material variants based on paint mask.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TerrainTile))]
    public sealed class TileMeshRenderer : MonoBehaviour
    {
        private TerrainTile _tile;
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private MeshCollider _meshCollider;
        // SEPARATE meshes for visual vs physics. Earlier we shared one Mesh
        // between MeshFilter + MeshCollider; nulling the collider's sharedMesh
        // for LOD release looked like it was making Unity drop render-side
        // bookkeeping for the same Mesh object (visible derender at ~200m
        // even though m_distant should keep us alive past 600m). Independent
        // meshes mean visual is never touched once attached — only the
        // physics mesh gets the null/assign cycle.
        private Mesh _renderMesh;     // assigned to MeshFilter, never nulled
        private Mesh _colliderMesh;   // assigned to MeshCollider only when in LOD range
        private TileMeshUnderside _underside;

        // Cached buffers re-used across rebuilds so we don't churn the GC
        // on every paint stroke.
        private Vector3[] _vertices;
        private Vector2[] _uvs;
        private Color32[] _colors;
        private int[] _triangles;

        // 2026-05-25 round-15 — per-tile paint mask + material instance.
        //
        // BEFORE: every tile shared the SAME cached Custom/Heightmap material
        // and we never bound a `_ClearedMaskTex`. Result: Paint[] data was
        // persisted but never visible — the shader had no per-tile texture
        // to sample paved/dirt/cultivated channels from.
        //
        // AFTER: each TileMeshRenderer owns
        //   * `_paintMask` — a Texture2D sized to VERTS_PER_SIDE × VERTS_PER_SIDE
        //     (RGBA32, Clamp) filled from `_tile.Paint[]` on every rebuild.
        //   * `_materialInstance` — a clone of the shared cached material,
        //     used as the renderer's sharedMaterial. The instance is the
        //     only one with our paint texture bound, so other tiles aren't
        //     polluted.
        //
        // Pattern is the exact port from vanilla Heightmap.RebuildRenderMesh
        // (m_materialInstance.SetTexture("_ClearedMaskTex", m_paintMask)) and
        // RPG-maker's CustomHeightmap.cs. UV0 is also switched from world-tiled
        // to 0..1 across the tile so the shader samples paint correctly.
        private Texture2D _paintMask;
        private Material _materialInstance;
        // Scratch buffer for the auto-heal pass before SetPixels32.
        private Color32[] _paintWork;
        // Cached shader property id; resolved once per session.
        private static int s_clearedMaskTexProp;

        private void EnsurePaintWorkBuffer(int size)
        {
            if (_paintWork == null || _paintWork.Length != size)
                _paintWork = new Color32[size];
        }

        // Lazy LOD state — both render and collider meshes get assigned/nulled
        // independently based on player distance. Separate Mesh objects (see
        // _renderMesh / _colliderMesh) so each side's null+reassign cycle
        // doesn't touch the other.
        //
        //   * RENDER mesh activates within 600m of tile center per user
        //     requirement. 12m hysteresis (assign at 600m, release at 612m)
        //     so a player wandering near the edge doesn't flicker.
        //   * COLLIDER mesh activates within 60m of tile EDGE — 32m tile
        //     half-size + 60m = 92m from center. 6m hysteresis.
        //
        // Both are dirty-tracked so a brush stroke at close range re-cooks
        // immediately on the next LOD tick. Cook budget is per-frame global
        // for collider only (PhysX cook is the slow op — ~10-50ms). Render
        // mesh swap is cheap (reference change) so unbudgeted.
        private int _meshTriCount;          // last computed triangle count
        private bool _colliderMeshDirty;    // collider mesh changed since last cook
        private bool _renderMeshDirty;      // render mesh changed since last assign
        private bool _colliderAssigned;     // collider currently has our mesh
        private bool _renderAssigned = true; // render starts assigned (set in Awake)
        private float _nextLodCheckTime;    // throttle for distance polls
        // 0.25s = 4 checks/sec. Was 0.5s, halved 2026-05-25 so a player
        // entering range gets collision within ~0.25s instead of ~0.5s.
        // Per-check cost is one Vector3 distance + bool checks; negligible
        // even with 50+ tile renderers active.
        private const float LOD_CHECK_INTERVAL = 0.25f;

        private const float RENDER_ACTIVATE_DIST = 600f;
        private const float RENDER_DEACTIVATE_DIST = 612f;
        private const float COLLIDER_ACTIVATE_DIST = 92f;
        private const float COLLIDER_DEACTIVATE_DIST = 98f;

        // Per-frame cook quota across ALL TileMeshRenderer instances.
        private static int s_cookFrameNum = -1;
        private static int s_cooksThisFrame;
        private const int MAX_COOKS_PER_FRAME = 1;

        // ── Material cache ────────────────────────────────────────────────
        //
        // Single shared Material across ALL tile renderers. Cached once when
        // the first live Custom/Heightmap is found and reused forever — same
        // pattern Valheim itself uses for the heightmap shader. This keeps
        // memory low (one Material vs N) AND lets paint+biome blends render
        // identically across the world because every tile points at the same
        // material instance.
        //
        // _cachedHeightmapMaterial is ONLY ever set to a real Custom/Heightmap
        // material — never to a Standard fallback. That way `null` is a
        // reliable signal that "we haven't acquired the real one yet, retry".
        //
        // _cachedFallbackMaterial is the transitional render — tiles that
        // Awake before any heightmap is loaded get this (Standard dirtfloor
        // look). Once _cachedHeightmapMaterial lands, an upgrade pass
        // re-assigns every tile to it in one sweep.
        // Material acquisition is centralized in FiresUnifiedCore's
        // TerrainMaterialCache. This file keeps no cache field or retry
        // timer — when the cache captures a real Custom/Heightmap material
        // (via Harmony postfix on Heightmap.RebuildRenderMesh), FAT's
        // MaterialCaptured handler calls UpgradeAllRenderersToCachedMaterial
        // below in one pass. Until
        // capture lands, GetCurrentMaterial returns a visible Standard
        // fallback so tiles aren't pink/invisible.
        //
        // The old per-tile retry + scene-search + paint-mask binding all
        // moved into TerrainMaterialCache (single source of truth, capture
        // happens ONCE at the moment a vanilla heightmap finalizes its
        // material).

        private void Awake()
        {
            _tile = GetComponent<TerrainTile>();

            _meshFilter = GetComponent<MeshFilter>() ?? gameObject.AddComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>() ?? gameObject.AddComponent<MeshRenderer>();
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _meshRenderer.receiveShadows = true;

            // MeshCollider so the player can walk on the terrain top face.
            // Non-convex so it conforms to any tile shape. Single-sided
            // collision is fine for walking; the underside (TileMeshUnderside)
            // has its own MeshCollider for cave ceiling collision + raycasts.
            _meshCollider = GetComponent<MeshCollider>() ?? gameObject.AddComponent<MeshCollider>();
            _meshCollider.convex = false;

            // Underside renderer — separate child GO with its own mesh,
            // dark-rock material from vanilla prefabs, and MeshCollider.
            // 1:1 port of DirtFloorMeshCombiner.Underside.cs pattern.
            _underside = GetComponent<TileMeshUnderside>() ?? gameObject.AddComponent<TileMeshUnderside>();
            _underside.EnsureSetup(_tile);

            // PUT THE TILE ON THE "terrain" LAYER.
            //
            // Vanilla heightmap meshes live on the "terrain" layer. Vanilla
            // systems that depend on "ground" detection check this layer
            // specifically:
            //   * ClutterSystem (vegetation/grass spawn raycasts) — needs
            //     "terrain" to be the first hit. Without this, seeds + grass
            //     + plants placed on a custom tile fall through to the
            //     vanilla ground below or refuse to spawn.
            //   * WearNTear support checks (workbench / wall / piece
            //     placement) — Physics.OverlapBox on a layer mask that
            //     includes "terrain". Without this, every piece placed on
            //     a custom tile reports "no support" and decays.
            //   * ZoneSystem.GetGroundHeight raycasts similarly.
            //
            // Setting layer here in Awake covers every tile (lazy-spawn
            // and proactive). The underside child stays on the "piece"
            // layer (set in TileMeshUnderside) so player-collision works
            // correctly from below.
            int terrainLayer = LayerMask.NameToLayer("terrain");
            if (terrainLayer >= 0)
            {
                gameObject.layer = terrainLayer;
            }
            else
            {
                FiresLogger.LogWarning("[TileMeshRenderer] LayerMask.NameToLayer(\"terrain\") returned -1 — vegetation/support systems may not detect the tile correctly.");
            }

            // CRITICAL ORDER: create meshes BEFORE attempting material lookup.
            // If EnsureSharedMaterial fails (e.g. all shader Find() calls return
            // null, which is what happens when Unity strips shaders from the
            // production build), we still want valid meshes — otherwise
            // RebuildMesh sees nulls and bails for the entire session.
            _renderMesh = new Mesh { name = $"TileMesh_{_tile.TileXZ.x}_{_tile.TileXZ.y}_render" };
            _colliderMesh = new Mesh { name = $"TileMesh_{_tile.TileXZ.x}_{_tile.TileXZ.y}_collider" };
            // 65x65 = 4225 vertices fits comfortably in u16 index range (max 65535).
            // Triangle count maxes at ~49K indices (4096 quads * 12 indices double-sided).
            // u16 is plenty; explicit format keeps SetTriangles fast.
            _renderMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            _colliderMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            _renderMesh.MarkDynamic();
            _colliderMesh.MarkDynamic();
            _meshFilter.sharedMesh = _renderMesh;

            try
            {
                // Single source of truth: TerrainMaterialCache. Returns the
                // captured Custom/Heightmap clone when available, otherwise
                // a visible Standard fallback. When the capture later fires
                // via Heightmap.RebuildRenderMesh postfix, MaterialCaptured calls
                // UpgradeAllRenderersToCachedMaterial below to swap every
                // tile's material in one pass.
                _meshRenderer.sharedMaterial = TerrainMaterialCache.GetCurrentMaterial();
                var mat = _meshRenderer.sharedMaterial;
                // Per-tile Awake log is verbose-only — folded into
                // the 🪨 TILES batch banner by VATileRestoreBatch.
                FiresLogger.LogVerbose($"[TileMeshRenderer] tile=({_tile.TileXZ.x},{_tile.TileXZ.y}) Awake OK | renderMesh='{_renderMesh.name}' colliderMesh='{_colliderMesh.name}' material='{(mat != null ? mat.name : "<null>")}' shader='{mat?.shader?.name ?? "<none>"}'");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogError($"[TileMeshRenderer] tile=({_tile.TileXZ.x},{_tile.TileXZ.y}) material assign threw — {ex.Message}. Mesh will render with default (magenta) material until shader resolves.");
                _meshRenderer.sharedMaterial = null;
            }
        }

        // Called from TerrainMaterialCache.MaterialCaptured once the cached material exists,
        // so every already-spawned tile renderer picks up the new material
        // in one sweep. Idempotent — tiles already on the cached material
        // are skipped.
        public static void UpgradeAllRenderersToCachedMaterial()
        {
            var cached = TerrainMaterialCache.GetCurrentMaterial();
            if (cached == null) return;
            int upgraded = 0;
            var all = Object.FindObjectsByType<TileMeshRenderer>(FindObjectsSortMode.None);
            foreach (var t in all)
            {
                if (t == null || t._meshRenderer == null) continue;
                if (t._meshRenderer.sharedMaterial == cached) continue;
                t._meshRenderer.sharedMaterial = cached;
                upgraded++;
            }
            if (upgraded > 0)
            {
                FiresLogger.LogInfo($"[TileMeshRenderer] Upgraded {upgraded} tile renderer(s) to cached Custom/Heightmap material.");
            }
        }

        private Color32 VertexColour(int idx) => Voxel.FatSeasons.BiomeColour(
            _tile.BiomeModified != null && _tile.BiomeModified[idx] ? (Heightmap.Biome)_tile.Biome[idx] : Heightmap.Biome.Meadows);

        // Vedr seasons: every live tile repaints its render mesh in place (the collider mesh carries no colour).
        internal static void CollectSeasonRecolours(System.Collections.Generic.List<Voxel.FatSeasons.Recolour> into)
        {
            foreach (var tile in Object.FindObjectsByType<TileMeshRenderer>(FindObjectsSortMode.None))
            {
                var target = tile;
                into.Add(new Voxel.FatSeasons.Recolour(target.transform.position, () => { if (target != null) target.RecolourForSeason(); }));
            }
        }

        // A tile that has not built its mesh yet takes the season when it does.
        private void RecolourForSeason()
        {
            if (_tile == null || _renderMesh == null || _colors == null || _colors.Length != _renderMesh.vertexCount) return;
            for (int idx = 0; idx < _colors.Length; idx++) _colors[idx] = VertexColour(idx);
            _renderMesh.colors32 = _colors;
        }

        private void OnDestroy()
        {
            if (_meshCollider != null) _meshCollider.sharedMesh = null;
            if (_meshFilter != null) _meshFilter.sharedMesh = null;
            if (_renderMesh != null) { DestroyImmediate(_renderMesh); _renderMesh = null; }
            if (_colliderMesh != null) { DestroyImmediate(_colliderMesh); _colliderMesh = null; }
            // Per-tile material instance + paint mask — both clones owned
            // by this renderer. Free them so we don't leak texture memory
            // when tiles unload (or this component is destroyed for any
            // other reason).
            if (_materialInstance != null) { DestroyImmediate(_materialInstance); _materialInstance = null; }
            if (_paintMask != null) { DestroyImmediate(_paintMask); _paintMask = null; }
        }

        // LOD driver for both visual (render) and physics (collider) meshes.
        //
        //   * Throttled to LOD_CHECK_INTERVAL (0.5s) — distance to local
        //     player. Polling per-frame would burn CPU on big builds.
        //   * RENDER mesh: assigned within 600m from tile center, released
        //     past 612m. 12m hysteresis. Cheap operation (reference swap)
        //     so unbudgeted — visual pops on the very next LOD tick when
        //     player enters range.
        //   * COLLIDER mesh: assigned within 92m (= 60m from tile edge),
        //     released past 98m. 6m hysteresis. PhysX cook is the slow
        //     op (~10-50ms) so we frame-budget to 1 cook/frame globally.
        //   * Both meshes are independent — releasing collider doesn't
        //     affect render and vice versa.
        //   * Idempotent — steady state (player far + both null OR player
        //     near + both assigned) does one Vector3 distance + a few
        //     bool checks per 0.5s.
        private void Update()
        {
            if (_renderMesh == null || _meshFilter == null) return;
            if (Time.unscaledTime < _nextLodCheckTime) return;
            _nextLodCheckTime = Time.unscaledTime + LOD_CHECK_INTERVAL;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                // No local player (loading screen, dedi) — release both.
                ReleaseRender();
                ReleaseCollider();
                return;
            }

            // Tile center = our transform.position (set to WorldCenter on
            // TerrainTile.Initialize). XZ distance only — Y doesn't matter
            // for either LOD band since both are about gameplay distance.
            Vector3 a = player.transform.position;
            Vector3 b = transform.position;
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            float distSqrXZ = dx * dx + dz * dz;

            // ── RENDER band (600m / 612m) ────────────────────────────────
            float renderActSqr = RENDER_ACTIVATE_DIST * RENDER_ACTIVATE_DIST;
            float renderDeactSqr = RENDER_DEACTIVATE_DIST * RENDER_DEACTIVATE_DIST;
            if (_renderAssigned)
            {
                if (distSqrXZ > renderDeactSqr)
                {
                    ReleaseRender();
                }
                else if (_renderMeshDirty)
                {
                    // Mesh data changed (brush stroke); reassign so the
                    // renderer picks up new bounds/verts. Cheap.
                    _meshFilter.sharedMesh = _renderMesh;
                    _renderMeshDirty = false;
                }
            }
            else
            {
                if (distSqrXZ <= renderActSqr && _meshTriCount > 0)
                {
                    _meshFilter.sharedMesh = _renderMesh;
                    _renderAssigned = true;
                    _renderMeshDirty = false;
                }
            }

            // ── COLLIDER band (92m / 98m, cook-budgeted) ─────────────────
            if (_meshCollider == null) return;
            float colActSqr = COLLIDER_ACTIVATE_DIST * COLLIDER_ACTIVATE_DIST;
            float colDeactSqr = COLLIDER_DEACTIVATE_DIST * COLLIDER_DEACTIVATE_DIST;
            if (_colliderAssigned)
            {
                if (distSqrXZ > colDeactSqr)
                {
                    ReleaseCollider();
                }
                else if (_colliderMeshDirty)
                {
                    if (TryConsumeCookSlot())
                    {
                        // In-place swap — keep the previous cooked mesh
                        // attached until the new one is bound. PhysX
                        // recooks on assignment of the modified mesh.
                        // Never leave sharedMesh null while assigned, or
                        // the player falls through during dirty refresh.
                        if (_meshTriCount > 0)
                        {
                            _meshCollider.sharedMesh = _colliderMesh;
                            _colliderMeshDirty = false;
                        }
                        else
                        {
                            // Geometry actually went to zero — safe to null.
                            _meshCollider.sharedMesh = null;
                            _colliderAssigned = false;
                            _colliderMeshDirty = false;
                        }
                    }
                }
            }
            else
            {
                if (distSqrXZ <= colActSqr && _meshTriCount > 0)
                {
                    if (TryConsumeCookSlot())
                    {
                        _meshCollider.sharedMesh = _colliderMesh;
                        _colliderAssigned = true;
                        _colliderMeshDirty = false;
                    }
                }
            }
        }

        private void ReleaseRender()
        {
            if (!_renderAssigned) return;
            if (_meshFilter != null) _meshFilter.sharedMesh = null;
            _renderAssigned = false;
        }

        private void ReleaseCollider()
        {
            if (!_colliderAssigned) return;
            if (_meshCollider != null) _meshCollider.sharedMesh = null;
            _colliderAssigned = false;
        }

        // Returns true + decrements the per-frame budget. Returns false
        // when budget exhausted — caller retries next LOD tick.
        private static bool TryConsumeCookSlot()
        {
            int f = Time.frameCount;
            if (s_cookFrameNum != f) { s_cookFrameNum = f; s_cooksThisFrame = 0; }
            if (s_cooksThisFrame >= MAX_COOKS_PER_FRAME) return false;
            s_cooksThisFrame++;
            return true;
        }

        // True until the first rebuild that produces at least one triangle
        // (i.e. the mesh actually became visible). Used to emit a one-shot
        // latency log so we can see how long after spawn the tile pops in.
        private bool _firstVisibleLogged;

        // Called by TerrainTile.NotifyMutated whenever Heights/Paint/Disks change.
        // Safe to call repeatedly — buffer reuse keeps allocations near zero on
        // steady-state mutation.
        public void RebuildMesh()
        {
            if (_tile == null || _tile.Heights == null || _renderMesh == null || _colliderMesh == null)
            {
                FiresLogger.LogWarning($"[TileMeshRenderer] RebuildMesh skipped: tile={_tile != null}, heights={_tile?.Heights != null}, renderMesh={_renderMesh != null}, colliderMesh={_colliderMesh != null}");
                return;
            }

            // Phase F+: material acquisition is now a single one-time
            // Heightmap.RebuildRenderMesh-postfix-driven capture in
            // TerrainMaterialCache. No per-tile retry; if the cache fires
            // mid-session, UpgradeAllRenderersToCachedMaterial fans out.

            float rebuildStart = Time.realtimeSinceStartup;

            int size = TerrainTileCodec.VERTEX_GRID_SIZE; // 65
            int vertCount = size * size;                  // 4225
            int quadCount = (size - 1) * (size - 1);      // 4096
            int triIndexCount = quadCount * INDICES_PER_QUAD; // 49152 (double-sided)

            EnsureBuffers(vertCount, triIndexCount);

            // Local origin at tile (0,0,0) — transform places this in world XZ.
            // Heights are stored in WORLD-Y; subtract the transform's Y so the
            // mesh local-vertex Y is relative to the tile transform (which sits
            // at the tile's center Y=0).
            float tileWorldYOffset = transform.position.y;

            // Per-vertex color pipeline (codec v3) — direct port of the
            // RPG-maker DirtFloorMeshCombiner.Paint.cs:1760-1850 pattern.
            //
            // Custom/Heightmap shader interprets vertex colors as a BIOME
            // BLEND MASK (NOT as paint amounts — paint is a separate system
            // that goes through the _ClearedMaskTex texture). Each Color32
            // channel corresponds to a different biome's blend weight; the
            // exact encoding comes from Heightmap.GetBiomeColor:
            //
            //   Meadows:     (  0,   0,   0,   0)   ← unmodified default
            //   Swamp:       (255,   0,   0,   0)
            //   Mountain:    (  0, 255,   0,   0)
            //   BlackForest: (  0,   0, 255,   0)
            //   Plains:      (  0,   0,   0, 255)
            //   AshLands:    (255,   0,   0, 255)
            //   DeepNorth:   (  0, 255,   0,   0)
            //   Mistlands:   (  0,   0, 255, 255)
            //
            // For each vertex:
            //   - If BiomeModified[idx] → use Heightmap.GetBiomeColor(biome)
            //     directly. Per-vertex granularity means a small brush only
            //     paints biome inside the circle.
            //   - Else → Meadows (0,0,0,0) so the shader renders the
            //     default biome texture (grass) — same default vanilla
            //     uses for unpainted heightmap verts.
            //   - In Vedr's winter a biome it snows over draws the snow
            //     splat instead (FatSeasons.BiomeColour).
            //
            // PAINT (dirt/cultivated/paved) belongs in the _ClearedMaskTex
            // texture per-tile, not in vertex colors. The Paint[] field
            // on TerrainTile is still stored and a per-tile mask-texture
            // pipeline is tracked as a follow-up. The current
            // `PaintModified` path that writes Paint colors into vertex
            // would clobber the biome — we intentionally do NOT do that
            // any more.

            for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                int idx = j * size + i;
                float h = _tile.HeightModified[idx] ? _tile.Heights[idx] : 0f;

                _vertices[idx] = new Vector3(
                    i * TerrainTile.VERTEX_SPACING - TerrainTile.TILE_SIZE * 0.5f,
                    h - tileWorldYOffset,
                    j * TerrainTile.VERTEX_SPACING - TerrainTile.TILE_SIZE * 0.5f);

                // 2026-05-25 round-15: UV0 = 0..1 across the tile, matching
                // vanilla Heightmap.RebuildRenderMesh:
                //   s_tempUVs.Add(new Vector2(i / m_width, j / m_width))
                // The Custom/Heightmap shader samples `_ClearedMaskTex`
                // (our per-tile paint mask) via UV0, so feeding it a
                // tile-local 0..1 range is required for paint to show at
                // the right vertex. World-tiled UVs were the previous
                // value and broke paint placement entirely (paint Y'd up
                // somewhere on the tile based on world coord modulo
                // texture wrap). The shader still tiles its diffuse
                // textures internally so the visual stays unchanged.
                _uvs[idx] = new Vector2(
                    i / (float)(size - 1),
                    j / (float)(size - 1));

                // Biome wins (this is the RPG-maker pattern from
                // DirtFloorMeshCombiner.Paint.cs). Vertex color = biome
                // texture-blend mask via Heightmap.GetBiomeColor. Paint
                // textures (dirt/cultivated/paved) are a SEPARATE channel
                // via the per-tile paint mask texture — not implemented
                // yet, so Paint[] is just persisted for the future port.
                _colors[idx] = VertexColour(idx);
            }

            // Triangle indices — two triangles per quad, CCW winding for
            // standard Unity backface culling. Quad (i, j) covers vertices
            // (i, j), (i+1, j), (i, j+1), (i+1, j+1).
            //
            // FLOATING TERRAIN: skip any quad where any of the 4 corners is
            // unmodified. This is the RPG-maker pattern — the brushed area
            // produces a clean floating platform with no drop-down skirt to
            // the anchor at Y=-5000. Edges stop where the brush stopped.
            int t = 0;
            int skippedQuads = 0;
            for (int j = 0; j < size - 1; j++)
            for (int i = 0; i < size - 1; i++)
            {
                int v00 = j * size + i;
                int v10 = j * size + (i + 1);
                int v01 = (j + 1) * size + i;
                int v11 = (j + 1) * size + (i + 1);

                if (!_tile.HeightModified[v00] || !_tile.HeightModified[v10]
                    || !_tile.HeightModified[v01] || !_tile.HeightModified[v11])
                {
                    skippedQuads++;
                    continue;
                }

                // Single-sided top face — CCW from above so the +Y normal is
                // visible from above. MeshCollider catches both sides for
                // physics regardless of winding, so the player still walks on
                // it and can hit raycasts from below.
                _triangles[t++] = v00;
                _triangles[t++] = v01;
                _triangles[t++] = v11;

                _triangles[t++] = v00;
                _triangles[t++] = v11;
                _triangles[t++] = v10;
            }

            // Populate BOTH meshes with the same vertex/index data. The
            // visual (_renderMesh) and physics (_colliderMesh) need to
            // match geometrically — only their lifecycle differs (render
            // LOD is 600m, collider LOD is 92m, see Update). Independent
            // Mesh objects mean Unity tracks them separately so nulling
            // one for LOD doesn't affect the other.
            FillMesh(_renderMesh, t);
            FillMesh(_colliderMesh, t);

            // Round-63 — seam-consistent BORDER NORMALS. RecalculateNormals
            // (inside FillMesh) only sees THIS tile's triangles, so a
            // shared border vert gets a different normal on each side of
            // the seam → a hard lighting crease along every tile border
            // even when the heights are perfectly stitched (the "weird
            // hard edges" in the user's screenshots). Overwrite border-
            // vert normals with analytic central-difference normals whose
            // neighbor samples cross the tile boundary — both tiles then
            // compute IDENTICAL normals at shared verts by construction.
            ApplySeamNormals(_renderMesh, size);

            // 2026-05-25 round-15 — rebuild and bind the per-tile paint mask
            // so paved/dirt/cultivated colors from `_tile.Paint[]` actually
            // appear on the rendered surface. Before this hook the data was
            // persisted but the shader had no per-tile `_ClearedMaskTex` to
            // sample from, so paint was invisible.
            RebuildPaintMaskAndBind(size);

            // Force-set bounds explicitly to the tile's footprint so frustum
            // culling doesn't kick in early. RecalculateBounds() already
            // computes a tight AABB but Unity may underestimate when many
            // verts sit at Y=0 (unmodified) — explicit bounds guarantee
            // visibility from 600m+.
            //
            // Tile is 64m × 64m centered at our local origin. Y range
            // covers the heightmap span; we pad generously (skylands can
            // float at Y=500+ so the local-Y range can be large).
            // Local-space bounds: center on origin, size 64 × 1000 × 64.
            var hugeBounds = new Bounds(Vector3.zero, new Vector3(64f, 1000f, 64f));
            _renderMesh.bounds = hugeBounds;
            _colliderMesh.bounds = hugeBounds;

            _meshTriCount = t;
            _renderMeshDirty = true;     // LOD re-assigns to MeshFilter next tick
            _colliderMeshDirty = true;   // LOD re-cooks collider next tick

            // If renderer was currently assigned, swap to the freshly-filled
            // mesh immediately so the player sees the change without waiting
            // 0.5s for the next LOD poll. Cheap (reference swap).
            if (_meshFilter != null && _renderAssigned)
            {
                _meshFilter.sharedMesh = _renderMesh;
                _renderMeshDirty = false;
            }

            // If the collider was assigned, swap directly to the fresh mesh
            // IN-PLACE so the collider is NEVER null between frames. The
            // earlier "null + wait for LOD" pattern created a 0–250 ms gap
            // where the MeshCollider had no geometry — the player would fall
            // through the tile every time we re-painted, and the brush
            // raycast would slip past the top surface and hit the underside
            // (or vanilla terrain below), placing Floor at a totally wrong Y
            // and looking like "the mesh got pulled down".
            //
            // PhysX cooks on the assignment line synchronously, so even
            // though the mesh data changed (verts/tris re-filled in
            // FillMesh), reassigning the same sharedMesh reference triggers
            // a recook on the new contents. No falling-through window.
            //
            // We still respect the per-frame cook budget — if it's
            // exhausted, mark dirty and let LOD pick up next tick BUT do
            // NOT null the collider; the previous frame's cooked mesh keeps
            // serving as a stable surface until the new cook lands.
            if (_meshCollider != null && _colliderAssigned && _meshTriCount > 0)
            {
                if (TryConsumeCookSlot())
                {
                    _meshCollider.sharedMesh = _colliderMesh;
                    _colliderMeshDirty = false;
                }
                else
                {
                    _colliderMeshDirty = true;
                }
            }
            else if (_meshCollider != null && _colliderAssigned && _meshTriCount == 0)
            {
                // Geometry vanished (BrushClear / wipe). Now-empty collider
                // can't catch the player; safe to release.
                _meshCollider.sharedMesh = null;
                _colliderAssigned = false;
            }

            // Sync the underside renderer to match (separate mesh with offset
            // verts + inverted winding + dark rock material). Underside reads
            // verts off our render mesh — same geometric data.
            if (_underside != null)
            {
                _underside.SyncFrom(_renderMesh, transform.position);
            }

            float rebuildMs = (Time.realtimeSinceStartup - rebuildStart) * 1000f;
            FiresLogger.LogVerbose($"[TileMeshRenderer] tile=({_tile.TileXZ.x},{_tile.TileXZ.y}) rebuilt | verts={vertCount} tris={t / 3} (skipped {skippedQuads} unmodified quads → floating-terrain edge) bounds={_renderMesh.bounds.size} collider={(_meshCollider != null ? "set" : "missing")} underside={(_underside != null ? "synced" : "missing")} buildMs={rebuildMs:F1}");

            // One-shot "first visible" latency log. Fires when the mesh first
            // produces at least one triangle (i.e. the player would see it now)
            // — tells us the FULL delay from "TileAnchor.Awake fired" to "mesh
            // on screen". Pin the pop-in cause: if this delta is tiny, the
            // delay is upstream (zone streaming queue). If it's big, the
            // delay is in our rebuild path.
            if (!_firstVisibleLogged && t > 0)
            {
                _firstVisibleLogged = true;
                var anchor = _tile.GetComponentInParent<TileAnchor>();
                if (anchor != null && anchor.AwakeRealtime > 0f)
                {
                    float spawnToVisibleMs = (Time.realtimeSinceStartup - anchor.AwakeRealtime) * 1000f;
                    int spawnToVisibleFrames = Time.frameCount - anchor.AwakeFrame;
                    float zoneAgeMs = ZoneStreamTiming.MillisSinceZoneActive(ZoneStreamTiming.ZoneFromAnchor(_tile.TileXZ));
                    // Per-tile FIRST_VISIBLE timing log is verbose-
                    // only. Useful for diagnosing slow zone-load /
                    // mesh-build hitches, but in normal operation
                    // it's noise. Verbose mode restores it.
                    FiresLogger.LogVerbose($"[TileMeshRenderer] tile=({_tile.TileXZ.x},{_tile.TileXZ.y}) FIRST_VISIBLE | T+{spawnToVisibleMs:F0}ms since Awake ({spawnToVisibleFrames} frames) | T+{zoneAgeMs:F0}ms since zone activation | buildMs={rebuildMs:F1} | tris={t / 3}");
                }
            }
        }

        // 2026-05-25 round-15 — rebuild the per-tile paint mask texture from
        // tile.Paint[] and bind it to the renderer's per-tile material
        // instance. Mirrors vanilla Heightmap.RebuildRenderMesh which does:
        //   m_paintMask.SetPixels(...);
        //   m_paintMask.Apply();
        //   m_materialInstance.SetTexture("_ClearedMaskTex", m_paintMask);
        //
        // CRITICAL: each tile needs its OWN material instance — sharing one
        // material across tiles would mean SetTexture on one tile paints
        // every tile. We clone the cached shared material lazily here.
        // When TerrainMaterialCache later upgrades the cached material
        // (e.g. ValheimHDTerrain finishes loading), the next rebuild
        // re-clones automatically.
        private void RebuildPaintMaskAndBind(int size)
        {
            if (_tile == null || _tile.Paint == null) return;

            // Lazy property-id resolve (Shader.PropertyToID is a sync op
            // we want to do once, not per tile per rebuild).
            if (s_clearedMaskTexProp == 0)
                s_clearedMaskTexProp = Shader.PropertyToID("_ClearedMaskTex");

            // Lazy create / re-size texture.
            if (_paintMask == null || _paintMask.width != size || _paintMask.height != size)
            {
                if (_paintMask != null) DestroyImmediate(_paintMask);
                _paintMask = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
                {
                    name = $"TilePaintMask_{_tile.TileXZ.x}_{_tile.TileXZ.y}",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }

            // 2026-05-25 round-16: copy Paint[] into texture pixels with
            // zero-alpha auto-heal. Vanilla convention is:
            //   (0,0,0,1)  = m_paintMaskNothing — no paint, vegetation present
            //                (this is what the default unmodified tile renders)
            //   (0,0,0,0)  = m_paintMaskClearVegetation — vegetation cleared
            //                but no paint applied (used to "kill grass")
            //
            // Our older Paint[] init left every pixel at (0,0,0,0) which
            // told the shader "vegetation cleared everywhere" — that's why
            // fresh tiles rendered as a barren/paved-looking surface
            // instead of meadows grass. We swap (0,0,0,0) for (0,0,0,1)
            // on the way to the texture so existing saves auto-heal on
            // first rebuild without a manual migration step. Painted
            // pixels (any non-zero R/G/B/A) pass through unchanged.
            //
            // The heal is gated on PaintModified because (0,0,0,0) is also a
            // REAL paint value - m_paintMaskClearVegetation. A vertex the
            // brush actually touched keeps whatever it was given; only
            // untouched vertices get promoted. ApplyClearToTile resets both
            // fields together, so an erased vertex heals again.
            var paintModified = _tile.PaintModified;
            EnsurePaintWorkBuffer(size * size);
            for (int i = 0; i < _tile.Paint.Length; i++)
            {
                var c = _tile.Paint[i];
                bool painted = paintModified != null && i < paintModified.Length && paintModified[i];
                if (!painted && c.r == 0 && c.g == 0 && c.b == 0 && c.a == 0)
                {
                    // Default — promote to "nothing painted, vegetation present".
                    _paintWork[i] = new Color32(0, 0, 0, 255);
                }
                else
                {
                    _paintWork[i] = c;
                }
            }
            _paintMask.SetPixels32(_paintWork);
            _paintMask.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            // (Re)materialize a per-tile material instance off the latest
            // cached shared material, then bind our paint mask. If the
            // cache hasn't captured yet, fall back to the renderer's
            // current sharedMaterial (which may be the Standard fallback).
            var cached = TerrainMaterialCache.GetCurrentMaterial();
            var sourceMat = cached != null ? cached : _meshRenderer.sharedMaterial;
            if (sourceMat == null) return;

            bool needNewInstance = _materialInstance == null
                                   || _materialInstance.shader != sourceMat.shader;
            if (needNewInstance)
            {
                if (_materialInstance != null) DestroyImmediate(_materialInstance);
                _materialInstance = new Material(sourceMat)
                {
                    name = $"TileMat_{_tile.TileXZ.x}_{_tile.TileXZ.y}",
                };
                _meshRenderer.sharedMaterial = _materialInstance;
            }

            _materialInstance.SetTexture(s_clearedMaskTexProp, _paintMask);
        }

        // Round-63 — analytic normals for border verts, sampled ACROSS
        // tile boundaries so adjacent tiles agree exactly at shared
        // verts. Central differences over the world height field:
        //   n = normalize(hL−hR, 2·spacing, hD−hU)
        // Neighbor heights come from this tile's grid when in-bounds and
        // modified, else from the adjacent tile via TileAnchorRegistry.
        // Unavailable samples fall back to the vert's own height, which
        // degrades the same way from BOTH sides (an unmodified cell is
        // unmodified in either tile's view) so consistency holds.
        private void ApplySeamNormals(Mesh mesh, int size)
        {
            if (_tile == null || _tile.Heights == null) return;

            var normals = mesh.normals;
            if (normals == null || normals.Length != size * size) return;

            float spacing = TerrainTile.VERTEX_SPACING;
            float originX = _tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = _tile.TileXZ.y * TerrainTile.TILE_SIZE;
            bool changed = false;

            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    bool isBorder = i == 0 || i == size - 1 || j == 0 || j == size - 1;
                    if (!isBorder) continue;

                    int idx = j * size + i;
                    if (!_tile.HeightModified[idx]) continue;

                    float h0 = _tile.Heights[idx];
                    float hL = SampleWorldHeight(i - 1, j, size, originX, originZ, h0);
                    float hR = SampleWorldHeight(i + 1, j, size, originX, originZ, h0);
                    float hD = SampleWorldHeight(i, j - 1, size, originX, originZ, h0);
                    float hU = SampleWorldHeight(i, j + 1, size, originX, originZ, h0);

                    var n = new Vector3(hL - hR, 2f * spacing, hD - hU);
                    normals[idx] = n.normalized;
                    changed = true;
                }
            }

            if (changed) mesh.normals = normals;
        }

        // World-height sample for the normal stencil: in-tile grid when
        // possible, cross-tile registry lookup at the boundary, fallback
        // = the center vert's own height.
        private float SampleWorldHeight(int i, int j, int size, float originX, float originZ, float fallback)
        {
            if (i >= 0 && i < size && j >= 0 && j < size)
            {
                int idx = j * size + i;
                return _tile.HeightModified[idx] ? _tile.Heights[idx] : fallback;
            }

            var probe = new Vector3(
                originX + i * TerrainTile.VERTEX_SPACING,
                0f,
                originZ + j * TerrainTile.VERTEX_SPACING);
            if (TileAnchorRegistry.TrySampleHeightAtWorld(probe, out float h))
                return h;
            return fallback;
        }

        // Helper to populate a Mesh with the current rebuild buffers.
        // Used by RebuildMesh to write the same geometry into both
        // _renderMesh and _colliderMesh.
        private void FillMesh(Mesh mesh, int triIndexCount)
        {
            mesh.Clear();
            mesh.vertices = _vertices;
            mesh.uv = _uvs;
            mesh.colors32 = _colors;
            mesh.SetTriangles(_triangles, 0, triIndexCount, submesh: 0);
            mesh.RecalculateNormals();
            // Bounds get overridden by the caller — RecalculateBounds is
            // skipped to avoid the extra full-vert scan.
        }

        // Triangle buffer is sized for SINGLE-SIDED top-face rendering:
        // each quad produces 2 triangles = 6 indices.
        //
        // We tried double-sided (12 idx/quad) for cave underside visibility
        // but Mesh.RecalculateNormals averages adjacent face normals — top
        // (+Y) and bottom (-Y) faces sharing vertices produced near-zero
        // normals → mesh rendered solid black + 2× the triangles tanked FPS.
        //
        // Cave underside visibility needs the full Underside.cs pattern:
        // separate child GameObject + separate Mesh (inverted winding) +
        // dark-rock material captured from game prefabs + dedicated
        // MeshCollider. Tracked as a future port.
        private const int INDICES_PER_QUAD = 6;

        private void EnsureBuffers(int vertCount, int triIndexCount)
        {
            if (_vertices == null || _vertices.Length != vertCount)
            {
                _vertices = new Vector3[vertCount];
                _uvs = new Vector2[vertCount];
                _colors = new Color32[vertCount];
            }
            if (_triangles == null || _triangles.Length != triIndexCount)
            {
                _triangles = new int[triIndexCount];
            }
        }

        // ── Material acquisition ─────────────────────────────────────────
        //
        // Single source of truth: TerrainMaterialCache. Static one-time
        // capture from any Heightmap that fires RebuildRenderMesh. Phase F+
        // simplification — the old per-tile retry + scene search + paint
        // mask binding all lived here, now consolidated upstream.
        //
        // Nothing more to add at this layer; the Awake assignment reads
        // TerrainMaterialCache.GetCurrentMaterial() and UpgradeAllRenderersToCachedMaterial
        // (above) fans the cache out to live tiles when capture lands.
    }
}
