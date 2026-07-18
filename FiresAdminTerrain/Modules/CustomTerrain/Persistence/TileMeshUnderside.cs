using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Underside renderer for a single TerrainTile. 1:1 port of the proven
    // DirtFloorMeshCombiner.Underside.cs pattern from the original RPG-maker
    // mod — see Modules/CustomTerrain/Core/DirtFloorMeshCombiner.Underside.cs
    // for the reference implementation.
    //
    // PATTERN
    //   * Child GameObject parented under the tile GO (NOT a partial on
    //     TileMeshRenderer — keeps lifecycles independent and lets the
    //     underside fail without breaking the top mesh).
    //   * Separate Mesh with the same vertex positions as the top mesh,
    //     offset DOWN by 0.02m to prevent Z-fighting, with inverted
    //     triangle winding so backface culling renders it visible from below.
    //   * Separate MeshCollider on the 'piece' layer — critical for
    //     - players bumping into cave ceilings (otherwise they'd clip through)
    //     - raycasts from below registering hits (cave detection etc.)
    //   * Separate dark-rock material captured from vanilla prefabs at
    //     first use (rock4_forest preferred, several fallbacks). Cached in
    //     a static so all tiles share the same material instance.
    //   * World-space UVs (worldX * 0.5, worldZ * 0.5) for seamless rock
    //     tiling across tile boundaries.
    //
    // Auto-attached by TileMeshRenderer.Awake. SyncFrom(topMesh) is called
    // from TileMeshRenderer.RebuildMesh whenever the top changes.
    [DisallowMultipleComponent]
    public sealed class TileMeshUnderside : MonoBehaviour
    {
        // Z-fighting prevention. The underside sits 2cm below the top so
        // overlapping triangles don't flicker at far camera distances.
        private const float UNDERSIDE_Y_OFFSET = -0.02f;

        // Tile UV scaling — matches the proven value from Underside.cs.
        // Rock textures tile every 2 world meters at 0.5×, which looks
        // natural at the scales players see caves at.
        private const float UNDERSIDE_UV_SCALE = 0.5f;

        // ── LOD gating (matches DirtFloorLODManager pattern, lite version) ──
        //
        // Underside renderer + collider are EXPENSIVE in steady state:
        //   * Each non-convex MeshCollider is a static physics object Unity
        //     traverses on every Player.GroundContact raycast and similar.
        //   * Each MeshRenderer issues a draw call even if frustum-culled.
        //
        // Most tiles don't need their underside active most of the time —
        // the player is on top, not below. Gate by distance + Y:
        //   * Renderer: enable when within RENDER_DISTANCE (player can see it)
        //   * Collider: enable only when player is BELOW the tile's max Y
        //               (cave-ceiling collision / raycasts from below)
        //
        // Throttled update at LOD_CHECK_INTERVAL — checks are cheap but no
        // need to do them every frame. Hysteresis prevents thrashing at the
        // boundary.
        private const float UNDERSIDE_RENDER_DISTANCE = 120f;
        private const float UNDERSIDE_COLLIDER_DISTANCE = 40f;
        private const float UNDERSIDE_Y_HEADROOM = 10f; // collider stays on if player is within 10m below tile top
        private const float LOD_CHECK_INTERVAL = 0.5f;
        private const float LOD_HYSTERESIS = 6f;

        private float _nextLodCheckTime;
        // Cached tile-mesh max Y, computed on SyncFrom — used to decide if
        // player is "below" the tile without re-scanning the height grid every
        // LOD check.
        private float _meshMaxY = float.NegativeInfinity;
        // Cached underside triangle count. 0 = no valid collision geometry,
        // collider stays nulled regardless of LOD state. Prevents PhysX
        // "must have at least one non-degenerate triangle" spam after
        // BrushClear empties all quads.
        private int _undersideMeshTriCount;
        private bool _rendererCurrentlyEnabled;
        private bool _colliderCurrentlyEnabled;

        private GameObject _undersideGO;
        private MeshFilter _undersideMeshFilter;
        private MeshRenderer _undersideRenderer;
        private MeshCollider _undersideCollider;
        private Mesh _undersideMesh;

        private static Material s_sharedUndersideMaterial;
        private static bool s_captureAttempted;

        private TerrainTile _tile;

        // Setup runs on TileMeshRenderer.Awake call to EnsureUnderside.
        public void EnsureSetup(TerrainTile tile)
        {
            _tile = tile;
            if (_undersideGO != null) return;

            // Create child GO under the TerrainTile transform.
            _undersideGO = new GameObject("Underside");
            _undersideGO.transform.SetParent(transform, worldPositionStays: false);
            _undersideGO.transform.localPosition = Vector3.zero;
            _undersideGO.transform.localRotation = Quaternion.identity;
            _undersideGO.transform.localScale = Vector3.one;

            // 'piece' layer — collision with players + raycasts work like vanilla pieces.
            int pieceLayer = LayerMask.NameToLayer("piece");
            if (pieceLayer >= 0) _undersideGO.layer = pieceLayer;

            _undersideMeshFilter = _undersideGO.AddComponent<MeshFilter>();
            _undersideRenderer = _undersideGO.AddComponent<MeshRenderer>();
            _undersideRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _undersideRenderer.receiveShadows = true;

            _undersideMesh = new Mesh
            {
                name = $"UndersideMesh_{tile.TileXZ.x}_{tile.TileXZ.y}",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt16,
            };
            _undersideMesh.MarkDynamic();
            _undersideMeshFilter.sharedMesh = _undersideMesh;

            // Default underside material = dark cave rock (the captured
            // rock4_forest prefab material). The standard "stretch to
            // terrain" underside acts as a cave ceiling — should stay
            // dark inside caves. BuildDomedUnderside swaps to the
            // Custom/Heightmap material at SyncFrom time when the tile
            // is in floating-island mode, so floating islands get the
            // natural cliff-stone slope-blended look. Cave use stays dark.
            _undersideRenderer.sharedMaterial = GetOrCreateUndersideMaterial();

            // Non-convex collider — critical for cave ceiling collision +
            // raycasts from below. SharedMesh assigned in SyncFrom.
            _undersideCollider = _undersideGO.AddComponent<MeshCollider>();
            _undersideCollider.convex = false;

            // Default both OFF — LOD update will enable them as needed.
            // Saves steady-state cost for tiles the player isn't near or below.
            _undersideRenderer.enabled = false;
            _undersideCollider.enabled = false;
            _rendererCurrentlyEnabled = false;
            _colliderCurrentlyEnabled = false;

            // Per-tile underside-setup log is verbose-only — the
            // wave gets summarized in the 🪨 TILES batch banner.
            FiresLogger.LogVerbose($"[TileMeshUnderside] tile=({tile.TileXZ.x},{tile.TileXZ.y}) setup complete | layer={pieceLayer} material='{_undersideRenderer.sharedMaterial?.name}' (renderer+collider start DISABLED, LOD will enable as needed)");
        }

        // Throttled LOD check — runs at most every LOD_CHECK_INTERVAL seconds.
        // Toggles renderer / collider based on player distance + Y position.
        // Cheap: 2 float comparisons + maybe 2 component .enabled writes.
        private void Update()
        {
            if (Time.time < _nextLodCheckTime) return;
            _nextLodCheckTime = Time.time + LOD_CHECK_INTERVAL;

            var player = Player.m_localPlayer;
            if (player == null || _tile == null)
            {
                SetRendererEnabled(false);
                SetColliderEnabled(false);
                return;
            }

            Vector3 playerPos = player.transform.position;
            Vector3 tileCenter = _tile.WorldCenter;

            // 2D distance (XZ) from player to tile center. Tile is 64m wide
            // so the center-to-edge distance is ~45m; render distance of 120m
            // catches tiles within ~75m of any tile edge.
            float dx = playerPos.x - tileCenter.x;
            float dz = playerPos.z - tileCenter.z;
            float dist2D = Mathf.Sqrt(dx * dx + dz * dz);

            // Renderer: enable if within view distance (with hysteresis).
            float renderEnableDist = UNDERSIDE_RENDER_DISTANCE;
            float renderDisableDist = UNDERSIDE_RENDER_DISTANCE + LOD_HYSTERESIS;
            bool wantRenderer = _rendererCurrentlyEnabled
                ? dist2D <= renderDisableDist
                : dist2D <= renderEnableDist;
            SetRendererEnabled(wantRenderer);

            // Collider: enable only when player is close enough AND below or
            // near-below the tile's top. The underside is for catching cave
            // ceiling collision + raycasts from below — useless when player
            // is far above the terrain.
            bool wantCollider = false;
            if (dist2D <= UNDERSIDE_COLLIDER_DISTANCE && _meshMaxY > float.NegativeInfinity)
            {
                float colliderActivateY = _meshMaxY + UNDERSIDE_Y_HEADROOM;
                float colliderDeactivateY = _meshMaxY + UNDERSIDE_Y_HEADROOM + LOD_HYSTERESIS;
                wantCollider = _colliderCurrentlyEnabled
                    ? playerPos.y <= colliderDeactivateY
                    : playerPos.y <= colliderActivateY;
            }
            SetColliderEnabled(wantCollider);
        }

        private void SetRendererEnabled(bool enabled)
        {
            if (_rendererCurrentlyEnabled == enabled) return;
            _rendererCurrentlyEnabled = enabled;
            if (_undersideRenderer != null) _undersideRenderer.enabled = enabled;
            FiresLogger.LogVerbose($"[TileMeshUnderside] tile=({_tile?.TileXZ.x ?? 0},{_tile?.TileXZ.y ?? 0}) renderer → {enabled}");
        }

        private void SetColliderEnabled(bool enabled)
        {
            if (_colliderCurrentlyEnabled == enabled) return;
            _colliderCurrentlyEnabled = enabled;
            if (_undersideCollider != null)
            {
                if (enabled)
                {
                    // Lazy cook: assign the mesh now if SyncFrom deferred it
                    // because the collider was disabled at sync time. GUARD:
                    // only cook if we have actual triangles — PhysX rejects
                    // empty meshes with "non-degenerate triangle" error.
                    if (_undersideCollider.sharedMesh == null && _undersideMesh != null && _undersideMeshTriCount > 0)
                    {
                        _undersideCollider.sharedMesh = _undersideMesh;
                    }
                    _undersideCollider.enabled = true;
                }
                else
                {
                    _undersideCollider.enabled = false;
                }
            }
            FiresLogger.LogVerbose($"[TileMeshUnderside] tile=({_tile?.TileXZ.x ?? 0},{_tile?.TileXZ.y ?? 0}) collider → {enabled}");
        }

        // ── 2026-05-26 round-30 — Floating Island Mode ─────────────────
        //
        // When TerrainTile.FloatingIslandMode == true AND the tile's top
        // sits ≥20m above the local terrain (no nearby lower tile to
        // bridge to), the underside generates a DOMED TEARDROP bottom
        // instead of mirroring the top mesh. Default mode (false) is the
        // current "stretch down to terrain" behavior.
        //
        // The dome is computed in `BuildDomedUnderside`. Per-vertex bottom
        // Y = topY − Lerp(EDGE_INSET, MAX_DOME_DEPTH, smoothstep(1 − r/maxR)).
        // Result: edges sit ~1m below top (subtle lip); center bulges
        // down to MAX_DOME_DEPTH (~30% of the island's XZ extent) →
        // upside-down teardrop / rock-bottom look.
        // 2026-05-26 round-33: trigger lowered 50m → 20m per user request.
        // Anything 20m+ above local terrain now auto-rounds into the
        // teardrop underside; below 20m the mesh stretches down to terrain
        // as before. Catches realistic floating-platform heights (10-30m
        // sky islands, mid-level shelves) that the 50m default missed.
        private const float FLOATING_ISLAND_TRIGGER_HEIGHT = 20f;

        // 2026-05-26 round-34 — dome geometry bumped substantially.
        // Previously the dome was almost invisible (0.5m edge inset, 4m
        // min dome depth) so floating islands looked like "a bedsheet
        // hanging over the edge of a bed" (user's words). The new values
        // produce visible 3D rock-bottom geometry:
        //
        //   FLOATING_ISLAND_SIDE_WALL_DEPTH = 5m   — vertical-ish side
        //     wall height between the top boundary and the dome's
        //     boundary ring. Actual side walls are emitted as a closed
        //     mesh around the boundary edges, then the dome attaches
        //     5m below the top edge.
        //
        //   FLOATING_ISLAND_DOME_DEPTH_RATIO = 0.50  — dome depth (from
        //     side-wall bottom to dome apex) is 50% of horizontal
        //     extent. For a 10m disk: 5m side wall + 5m dome = 10m
        //     total depth — a real teardrop.
        //
        //   FLOATING_ISLAND_MIN_DOME_DEPTH = 10m  — even small disks
        //     get a noticeable dome. Hides the "small disk = sheet"
        //     degenerate case.
        //
        //   FLOATING_ISLAND_INWARD_TAPER = 0.35  — dome verts pulled
        //     toward centroid by up to 35% of their horizontal distance,
        //     scaled by depth. Gives the teardrop its narrowing bottom.
        //
        // 2026-05-26 round-38 — reverted rounds 36–37 (material swap +
        // side-wall removal) per user request: the proven round-34/35
        // geometry with always-cave-rock material is the keeper. The
        // image they screenshotted with the dark cliff-side rock + grass
        // top is exactly this configuration.
        private const float FLOATING_ISLAND_SIDE_WALL_DEPTH = 5f;
        private const float FLOATING_ISLAND_DOME_DEPTH_RATIO = 0.50f;
        private const float FLOATING_ISLAND_MIN_DOME_DEPTH = 10f;
        private const float FLOATING_ISLAND_INWARD_TAPER = 0.35f;

        // Sync underside geometry from a freshly-rebuilt top mesh. Called
        // by TileMeshRenderer.RebuildMesh after the top mesh is built and
        // its triangles/vertices/UVs are stable.
        //
        // Default mode: same vertex positions (-0.02 Y), inverted triangle
        // winding (so backface culling shows the underside from below),
        // world-space UVs. Floating-island mode: domed teardrop bottom.
        public void SyncFrom(Mesh topMesh, Vector3 worldTransformOffset)
        {
            if (_undersideMesh == null || topMesh == null) return;

            // Round-93 (user directive) — the underside "cave layer" exists ONLY
            // for FLOATING terrain disks now. On grounded tiles it drew a jet-
            // black darkened sheet under/around real voxel caves and confused
            // the env/underwater systems. Real caves are voxel geometry; their
            // env comes from VoxelCaveEnv's honest up-raycast instead.
            if (_tile == null || !_tile.FloatingIslandMode)
            {
                _undersideMesh.Clear();
                if (_undersideCollider != null) _undersideCollider.sharedMesh = null;
                if (_undersideRenderer != null) _undersideRenderer.enabled = false;
                if (_undersideCollider != null) _undersideCollider.enabled = false;
                _rendererCurrentlyEnabled = false;
                _colliderCurrentlyEnabled = false;
                return;
            }

            int vertCount = topMesh.vertexCount;
            if (vertCount == 0)
            {
                // Empty top → empty underside.
                _undersideMesh.Clear();
                if (_undersideCollider != null) _undersideCollider.sharedMesh = null;
                return;
            }

            Vector3[] topVerts = topMesh.vertices;
            int[] topTris = topMesh.triangles;

            // Floating-island branch: domed underside if the tile is marked
            // for island mode AND sits ≥20m above local terrain. Falls
            // back to the standard mirror-with-offset (dark cave rock) if
            // either condition fails — caves stay dark.
            //
            // 2026-05-26 round-39 — per-mode material swap (floating
            // ONLY). The dome reuses the TOP renderer's per-tile
            // material (Custom/Heightmap shader + _ClearedMaskTex
            // paint mask + vertex-color biome blend). The SIDE WALLS
            // get the dark cave-rock material because the Custom/
            // Heightmap shader tiles its diffuse texture by world
            // XZ — on a vertical wall XZ doesn't change with Y, so
            // the heightmap shader streaks the texture in long
            // vertical strips. The cave-rock material is a plain
            // standard shader sampling via UV (which we feed world-
            // tiled coords) so the wall textures tile cleanly.
            // Round-40 split: two submeshes, two materials.
            //
            // Caves / non-floating undersides stay on single-material
            // cave rock — handled in the mirror branch below.
            if (ShouldUseDomedUnderside(topVerts, worldTransformOffset))
            {
                var topRenderer = GetComponent<MeshRenderer>();
                var topMat = topRenderer != null ? topRenderer.sharedMaterial : null;
                // Round-43 — wall material is the UNDARKENED rock so the
                // cliff face doesn't read as a black hole in overhead
                // lighting. Cave-ceiling underside (mirror branch below)
                // still uses the darkened version.
                var wallMat = GetOrCreateWallMaterial();
                if (_undersideRenderer != null && topMat != null && wallMat != null)
                {
                    var current = _undersideRenderer.sharedMaterials;
                    bool needsAssign = current == null
                                       || current.Length != 2
                                       || current[0] != topMat
                                       || current[1] != wallMat;
                    if (needsAssign)
                        _undersideRenderer.sharedMaterials = new Material[] { topMat, wallMat };
                }
                BuildDomedUnderside(topMesh, topVerts, topTris, worldTransformOffset, vertCount);
                return;
            }

            // Mirror branch — single-material cave dark rock. Reset to
            // a length-1 sharedMaterials array in case this tile was
            // previously in floating mode (had length-2 with topMat
            // at index 0).
            if (_undersideRenderer != null)
            {
                var caveMat = GetOrCreateUndersideMaterial();
                if (caveMat != null)
                {
                    var current = _undersideRenderer.sharedMaterials;
                    bool needsAssign = current == null
                                       || current.Length != 1
                                       || current[0] != caveMat;
                    if (needsAssign)
                        _undersideRenderer.sharedMaterials = new Material[] { caveMat };
                }
            }

            // Offset vertices down by Y_OFFSET to prevent Z-fighting with top.
            // Track max world Y while we're scanning so the LOD gate knows
            // when the player is "below" the tile without re-scanning.
            Vector3[] undersideVerts = new Vector3[vertCount];
            Vector2[] undersideUVs = new Vector2[vertCount];
            float maxWorldY = float.NegativeInfinity;
            for (int i = 0; i < vertCount; i++)
            {
                var v = topVerts[i];
                undersideVerts[i] = new Vector3(v.x, v.y + UNDERSIDE_Y_OFFSET, v.z);

                // World-space UV for seamless rock tiling across tile borders.
                float worldX = v.x + worldTransformOffset.x;
                float worldZ = v.z + worldTransformOffset.z;
                undersideUVs[i] = new Vector2(worldX * UNDERSIDE_UV_SCALE, worldZ * UNDERSIDE_UV_SCALE);

                float worldY = v.y + worldTransformOffset.y;
                if (worldY > maxWorldY) maxWorldY = worldY;
            }
            _meshMaxY = maxWorldY;

            // Inverted winding: swap index 1 and 2 of each triangle so the
            // face points DOWN (backface culling shows it from below).
            int triCount = topTris.Length;
            int[] undersideTris = new int[triCount];
            for (int i = 0; i < triCount; i += 3)
            {
                undersideTris[i] = topTris[i];
                undersideTris[i + 1] = topTris[i + 2];
                undersideTris[i + 2] = topTris[i + 1];
            }

            _undersideMesh.Clear();
            _undersideMesh.vertices = undersideVerts;
            _undersideMesh.triangles = undersideTris;
            _undersideMesh.uv = undersideUVs;
            _undersideMesh.RecalculateNormals();
            _undersideMesh.RecalculateBounds();

            // Force collider re-cook from the new mesh data — but only if
            // collider is currently active. Cooking a disabled collider is
            // wasted work; LOD update will cook it on demand when activated.
            //
            // GUARD: PhysX rejects meshes with 0 non-degenerate triangles
            // (BrushClear empties all quad modifications → topTris.Length
            // = 0 even though vertexCount = 4225). Skip the collider
            // assignment entirely in that case so we don't spam
            // "must have at least one non-degenerate triangle" errors.
            // The LOD path also checks this so the cook doesn't fire on
            // re-activation either. Spotted 2026-05-25.
            if (_undersideCollider != null && triCount > 0 && _colliderCurrentlyEnabled)
            {
                _undersideCollider.sharedMesh = null;
                _undersideCollider.sharedMesh = _undersideMesh;
            }
            else if (_undersideCollider != null)
            {
                // Either inactive (LOD will cook on activation if triCount>0)
                // or triCount==0 (mesh has no valid collision geometry).
                _undersideCollider.sharedMesh = null;
            }
            _undersideMeshTriCount = triCount;

            FiresLogger.LogVerbose($"[TileMeshUnderside] tile=({_tile?.TileXZ.x ?? -999},{_tile?.TileXZ.y ?? -999}) synced | verts={vertCount} tris={triCount / 3} maxY={maxWorldY:F1}");
        }

        // SetColliderEnabled needs to cook the mesh into the collider when
        // it flips from disabled → enabled (we deferred the cook in SyncFrom
        // for inactive colliders). Override to handle this.

        // ── Floating-island domed underside (round-30) ─────────────────
        //
        // Decision: use domed bottom only if the tile is flagged for
        // floating-island mode AND its lowest valid top vertex is at
        // least FLOATING_ISLAND_TRIGGER_HEIGHT above local terrain. The
        // "no nearby lower combiner" check from the user's spec is the
        // implicit fallback: if any adjacent tile has a lower top, the
        // mesh-build pass already welds them together via the SDF blend,
        // so this gate naturally lets the mesh stretch to that neighbor
        // instead of doming.
        private bool ShouldUseDomedUnderside(Vector3[] topVerts, Vector3 worldTransformOffset)
        {
            if (_tile == null) return false;
            if (!_tile.FloatingIslandMode) return false;
            if (topVerts.Length == 0) return false;

            // Find the lowest top-vertex Y (world space) that's actually
            // part of the floating mesh (HeightModified=true). Use that
            // as the "island bottom" reference height for the trigger.
            float minTopWorldY = float.PositiveInfinity;
            int gridSize = TerrainTileCodec.VERTEX_GRID_SIZE;
            for (int j = 0; j < gridSize; j++)
            for (int i = 0; i < gridSize; i++)
            {
                int idx = j * gridSize + i;
                if (idx >= topVerts.Length) break;
                if (_tile.HeightModified == null || idx >= _tile.HeightModified.Length) break;
                if (!_tile.HeightModified[idx]) continue;
                float worldY = topVerts[idx].y + worldTransformOffset.y;
                if (worldY < minTopWorldY) minTopWorldY = worldY;
            }
            if (float.IsPositiveInfinity(minTopWorldY)) return false;

            // Sample local terrain Y under the tile center. ZoneSystem
            // GetGroundHeight returns the vanilla heightmap Y; if a custom
            // tile exists below, the same world raycast through ZoneSystem
            // will return that lower tile's surface Y.
            Vector3 tileCenter = _tile.WorldCenter;
            float terrainY = 0f;
            if (ZoneSystem.instance != null)
                ZoneSystem.instance.GetGroundHeight(tileCenter, out terrainY);

            float heightAboveTerrain = minTopWorldY - terrainY;
            return heightAboveTerrain >= FLOATING_ISLAND_TRIGGER_HEIGHT;
        }

        private void BuildDomedUnderside(Mesh topMesh, Vector3[] topVerts, int[] topTris, Vector3 worldTransformOffset, int vertCount)
        {
            // 2026-05-26 round-35: compute centroid + maxRadius across THIS
            // tile AND its 8 cardinal+diagonal neighbors that also have
            // FloatingIslandMode set. Without this, a multi-tile floating
            // island had each tile compute its OWN centroid + dome — domes
            // didn't line up at the tile borders, producing the wonky
            // mesh stretching the user sees at tile seams.
            //
            // The shared centroid means all tiles in the island use the
            // SAME dome geometry math (deterministic on world XZ); border
            // verts of one tile's dome match the border verts of the
            // neighbor's dome.
            float sumX = 0f, sumZ = 0f;
            int modCount = 0;
            float maxRadius = 0.001f;

            int gridSize = TerrainTileCodec.VERTEX_GRID_SIZE;

            // Walk this tile + 8 neighbors. For each tile that exists and
            // is in floating-island mode, accumulate its modified verts.
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var ntileXZ = new Vector2i(_tile.TileXZ.x + dx, _tile.TileXZ.y + dz);
                TerrainTile nt;
                Vector3[] nVerts;
                if (dx == 0 && dz == 0)
                {
                    nt = _tile;
                    nVerts = topVerts;
                }
                else
                {
                    var nAnchor = TileAnchorRegistry.GetAt(ntileXZ);
                    if (nAnchor == null) continue;
                    nt = nAnchor.GetComponent<TerrainTile>();
                    if (nt == null || nt.HeightModified == null) continue;
                    if (!nt.FloatingIslandMode) continue;
                    // For neighbors, synthesize vertex world XZ from grid coords
                    // (we don't have their mesh; we just need positions).
                    nVerts = null;
                }

                float nOriginX = ntileXZ.x * TerrainTile.TILE_SIZE;
                float nOriginZ = ntileXZ.y * TerrainTile.TILE_SIZE;
                for (int j = 0; j < gridSize; j++)
                for (int i = 0; i < gridSize; i++)
                {
                    int idx = j * gridSize + i;
                    if (idx >= nt.HeightModified.Length) break;
                    if (!nt.HeightModified[idx]) continue;
                    float wx, wz;
                    if (nVerts != null)
                    {
                        wx = nVerts[idx].x; wz = nVerts[idx].z; // local space — own tile
                    }
                    else
                    {
                        // For neighbor tiles convert their grid XZ into
                        // OUR tile's local space (subtract our tile's world
                        // origin) so all coords are consistent.
                        float nWorldX = nOriginX + i * TerrainTile.VERTEX_SPACING;
                        float nWorldZ = nOriginZ + j * TerrainTile.VERTEX_SPACING;
                        wx = nWorldX - worldTransformOffset.x;
                        wz = nWorldZ - worldTransformOffset.z;
                    }
                    sumX += wx;
                    sumZ += wz;
                    modCount++;
                }
            }

            if (modCount == 0)
            {
                // No modified verts in this tile OR neighbors → empty mesh.
                _undersideMesh.Clear();
                if (_undersideCollider != null) _undersideCollider.sharedMesh = null;
                _meshMaxY = float.NegativeInfinity;
                _undersideMeshTriCount = 0;
                return;
            }
            float centerX = sumX / modCount;
            float centerZ = sumZ / modCount;

            // Max horizontal distance — also across neighbor tiles.
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var ntileXZ = new Vector2i(_tile.TileXZ.x + dx, _tile.TileXZ.y + dz);
                TerrainTile nt;
                if (dx == 0 && dz == 0)
                {
                    nt = _tile;
                }
                else
                {
                    var nAnchor = TileAnchorRegistry.GetAt(ntileXZ);
                    if (nAnchor == null) continue;
                    nt = nAnchor.GetComponent<TerrainTile>();
                    if (nt == null || nt.HeightModified == null) continue;
                    if (!nt.FloatingIslandMode) continue;
                }

                float nOriginX = ntileXZ.x * TerrainTile.TILE_SIZE;
                float nOriginZ = ntileXZ.y * TerrainTile.TILE_SIZE;
                for (int j = 0; j < gridSize; j++)
                for (int i = 0; i < gridSize; i++)
                {
                    int idx = j * gridSize + i;
                    if (idx >= nt.HeightModified.Length) break;
                    if (!nt.HeightModified[idx]) continue;
                    float wx = (nt == _tile) ? topVerts[idx].x : (nOriginX + i * TerrainTile.VERTEX_SPACING - worldTransformOffset.x);
                    float wz = (nt == _tile) ? topVerts[idx].z : (nOriginZ + j * TerrainTile.VERTEX_SPACING - worldTransformOffset.z);
                    float dx2 = wx - centerX;
                    float dz2 = wz - centerZ;
                    float r = Mathf.Sqrt(dx2 * dx2 + dz2 * dz2);
                    if (r > maxRadius) maxRadius = r;
                }
            }

            // ── Dome math (round-34) ──────────────────────────────────────
            //
            // Two-stage Y drop:
            //   1. SIDE WALL: from top Y down by FLOATING_ISLAND_SIDE_WALL_DEPTH
            //      (5m). Boundary verts sit here.
            //   2. DOME: from side-wall bottom further down by domeDepth
            //      (ratio × maxRadius, min FLOATING_ISLAND_MIN_DOME_DEPTH).
            //      Centroid vert sits at side-wall bottom MINUS domeDepth.
            //
            // The dome verts also taper INWARD toward the centroid based
            // on radial position — mid-radius verts move ~35% of their
            // distance to centroid, producing the narrowing teardrop
            // bottom. Boundary verts (r/maxR ≈ 1) get NO inward taper
            // so they remain aligned under the top boundary (side walls
            // attach cleanly).
            float domeDepth = maxRadius * FLOATING_ISLAND_DOME_DEPTH_RATIO;
            domeDepth = Mathf.Max(domeDepth, FLOATING_ISLAND_MIN_DOME_DEPTH);

            // Pull UV0 + vertex colors off the top mesh — round-39 hands
            // these straight through to the underside so the per-tile
            // _ClearedMaskTex (paint mask) and biome-blend mask sample
            // the SAME data on the underside as on the top. Result:
            // floating-island bottoms render with the same grass/biome
            // and any paint the user laid on top, visible from below.
            Vector2[] topUVs = topMesh.uv;
            Color32[] topColors = topMesh.colors32;

            Vector3[] undersideVerts = new Vector3[vertCount];
            Vector2[] undersideUVs = new Vector2[vertCount];
            Color32[] undersideColors = new Color32[vertCount];
            float maxWorldY = float.NegativeInfinity;

            for (int i = 0; i < vertCount; i++)
            {
                var v = topVerts[i];
                float dx = v.x - centerX;
                float dz = v.z - centerZ;
                float r = Mathf.Sqrt(dx * dx + dz * dz);
                float t = Mathf.Clamp01(r / maxRadius);    // 0 at center, 1 at edge
                float tInv = 1f - t;                       // 1 at center, 0 at edge

                // Drop = side-wall depth + smoothstepped dome depth.
                // At edge (t=1): drop = SIDE_WALL_DEPTH (5m), aligns with side wall bottom.
                // At center (t=0): drop = SIDE_WALL_DEPTH + domeDepth (full teardrop apex).
                float ramp = tInv * tInv * (3f - 2f * tInv); // smoothstep over 1-t
                float drop = FLOATING_ISLAND_SIDE_WALL_DEPTH + ramp * domeDepth;

                // Inward taper: peaks at mid-radius (sin curve). Edges stay
                // put (so side walls align); center can't taper (already AT
                // centroid). The mid-band gets pulled INWARD up to
                // FLOATING_ISLAND_INWARD_TAPER * its current distance from center.
                float taperWeight = Mathf.Sin(t * Mathf.PI); // 0 at t=0 and t=1, max at t=0.5
                float inwardFrac = taperWeight * FLOATING_ISLAND_INWARD_TAPER;
                float vx = v.x - dx * inwardFrac;
                float vz = v.z - dz * inwardFrac;

                undersideVerts[i] = new Vector3(vx, v.y - drop, vz);

                // UV0 + vertex color copied straight from the top vert at
                // the same grid index. The dome vert's XZ may be tapered
                // inward, but the UV reflects the GRID position that this
                // vert derives from on the top mesh — so a point on the
                // underside reads the paint mask + biome at the same
                // grid coord as the top surface above it. Slightly
                // off-axis under taper, but visually it reads as the
                // underside of that top point, which is what we want.
                undersideUVs[i] = (topUVs != null && i < topUVs.Length)
                    ? topUVs[i]
                    : Vector2.zero;
                undersideColors[i] = (topColors != null && i < topColors.Length)
                    ? topColors[i]
                    : new Color32(0, 0, 0, 255);

                float worldY = undersideVerts[i].y + worldTransformOffset.y;
                if (worldY > maxWorldY) maxWorldY = worldY;
            }
            _meshMaxY = maxWorldY;

            // ── Boundary edge detection ───────────────────────────────────
            //
            // For side walls we need to know which edges of the top mesh
            // lie on the perimeter (used by only 1 triangle). Walk topTris
            // and count each unique edge.
            int topTriCount = topTris.Length / 3;
            var edgeCounts = new System.Collections.Generic.Dictionary<long, int>(topTriCount * 2);
            for (int t = 0; t < topTris.Length; t += 3)
            {
                int va = topTris[t], vb = topTris[t + 1], vc = topTris[t + 2];
                BumpEdge(edgeCounts, va, vb);
                BumpEdge(edgeCounts, vb, vc);
                BumpEdge(edgeCounts, vc, va);
            }
            var boundaryEdges = new System.Collections.Generic.List<(int, int)>();
            foreach (var kv in edgeCounts)
            {
                if (kv.Value == 1)
                {
                    long key = kv.Key;
                    int a = (int)(key >> 32);
                    int b = (int)(key & 0xFFFFFFFFL);
                    boundaryEdges.Add((a, b));
                }
            }

            // ── Build mesh with TWO submeshes ─────────────────────────────
            //
            // Submesh 0 (heightmap material): dome triangles. Uses dome
            //   verts with their tile-local UV0 + biome-color from the
            //   top mesh — so the shader's paint mask + biome blend
            //   render the underside as a flipped copy of the top.
            //
            // Submesh 1 (cave-rock material): side wall triangles. Walls
            //   get 4 NEW verts per boundary edge (2 top + 2 bottom, NOT
            //   reusing the dome verts at indices a/b) so they can carry
            //   world-space UVs WITHOUT clashing with the dome's
            //   tile-local UVs. Cave-rock material samples its texture
            //   via UV0 in world units, which gives the wall a properly
            //   tiling rock surface as the eye travels DOWN — no more
            //   vertical-streak artifact the heightmap shader produced
            //   on vertical geometry. Wall vertex colors set to white
            //   so the standard shader's vertex-tint pass doesn't darken
            //   the rock.
            int wallVertCount = boundaryEdges.Count * 4; // 2 top + 2 bottom per edge
            int wallTriIndexCount = boundaryEdges.Count * 12; // 4 tris × 3 indices per edge (double-sided)
            int totalVertCount = vertCount + wallVertCount;

            var allVerts = new Vector3[totalVertCount];
            var allUVs = new Vector2[totalVertCount];
            var allColors = new Color32[totalVertCount];
            var domeTris = new int[topTris.Length];
            var wallTris = new int[wallTriIndexCount];

            // Copy dome verts/UVs/colors to the front of the buffer.
            for (int i = 0; i < vertCount; i++)
            {
                allVerts[i] = undersideVerts[i];
                allUVs[i] = undersideUVs[i];
                allColors[i] = undersideColors[i];
            }

            // Inverted dome winding (dome faces DOWN — visible from below).
            for (int i = 0; i < topTris.Length; i += 3)
            {
                domeTris[i] = topTris[i];
                domeTris[i + 1] = topTris[i + 2];
                domeTris[i + 2] = topTris[i + 1];
            }

            // Wall verts: 4 NEW verts per boundary edge.
            //   - topA/topB: at top mesh boundary Y (just below by 0.02m)
            //   - botA/botB: at dome boundary Y (re-mirroring dome vert at a/b)
            // All four carry world-space UVs so the cave-rock material
            // tiles properly both horizontally AND vertically along the wall.
            var white = new Color32(255, 255, 255, 255);
            int wallVertWrite = vertCount;
            int wallTriWrite = 0;
            foreach (var (a, b) in boundaryEdges)
            {
                int topAIdx = wallVertWrite++;
                int topBIdx = wallVertWrite++;
                int botAIdx = wallVertWrite++;
                int botBIdx = wallVertWrite++;

                var topA = topVerts[a];
                var topB = topVerts[b];
                var botA = undersideVerts[a]; // dome vert at index a
                var botB = undersideVerts[b];

                allVerts[topAIdx] = new Vector3(topA.x, topA.y - 0.02f, topA.z); // 2cm below top to prevent Z-fight
                allVerts[topBIdx] = new Vector3(topB.x, topB.y - 0.02f, topB.z);
                allVerts[botAIdx] = botA;
                allVerts[botBIdx] = botB;

                // 2026-05-27 round-43 — proper vertical-wall UV mapping.
                // Previously the wall used (worldX, worldZ) UVs which
                // don't vary with Y → on a vertical surface the entire
                // wall sampled the SAME texture pixel → solid color
                // smear + N·L≈0 lighting on the horizontal-normal wall
                // made the whole thing nearly black.
                //
                // New mapping: U follows the edge's dominant world axis
                // (X if the edge runs mostly along X, else Z) so adjacent
                // walls in the same direction are continuous; V follows
                // worldY so the texture tiles vertically as the eye
                // travels DOWN the wall. Result: real rock striations.
                float dxAB = topB.x - topA.x;
                float dzAB = topB.z - topA.z;
                float topAU, topBU, botAU, botBU;
                if (Mathf.Abs(dxAB) >= Mathf.Abs(dzAB))
                {
                    // Edge runs mostly along X — U comes from world X.
                    topAU = (topA.x + worldTransformOffset.x) * UNDERSIDE_UV_SCALE;
                    topBU = (topB.x + worldTransformOffset.x) * UNDERSIDE_UV_SCALE;
                    botAU = (botA.x + worldTransformOffset.x) * UNDERSIDE_UV_SCALE;
                    botBU = (botB.x + worldTransformOffset.x) * UNDERSIDE_UV_SCALE;
                }
                else
                {
                    // Edge runs mostly along Z — U comes from world Z.
                    topAU = (topA.z + worldTransformOffset.z) * UNDERSIDE_UV_SCALE;
                    topBU = (topB.z + worldTransformOffset.z) * UNDERSIDE_UV_SCALE;
                    botAU = (botA.z + worldTransformOffset.z) * UNDERSIDE_UV_SCALE;
                    botBU = (botB.z + worldTransformOffset.z) * UNDERSIDE_UV_SCALE;
                }
                float topAV = (topA.y + worldTransformOffset.y) * UNDERSIDE_UV_SCALE;
                float topBV = (topB.y + worldTransformOffset.y) * UNDERSIDE_UV_SCALE;
                float botAV = (botA.y + worldTransformOffset.y) * UNDERSIDE_UV_SCALE;
                float botBV = (botB.y + worldTransformOffset.y) * UNDERSIDE_UV_SCALE;

                allUVs[topAIdx] = new Vector2(topAU, topAV);
                allUVs[topBIdx] = new Vector2(topBU, topBV);
                allUVs[botAIdx] = new Vector2(botAU, botAV);
                allUVs[botBIdx] = new Vector2(botBU, botBV);

                allColors[topAIdx] = white;
                allColors[topBIdx] = white;
                allColors[botAIdx] = white;
                allColors[botBIdx] = white;

                // Wall quad (double-sided — visible from both inside and
                // outside the island). 4 triangles per edge:
                //   side 1: topA → botA → topB,  topB → botA → botB
                //   side 2: topA → topB → botA,  topB → botB → botA
                wallTris[wallTriWrite++] = topAIdx; wallTris[wallTriWrite++] = botAIdx; wallTris[wallTriWrite++] = topBIdx;
                wallTris[wallTriWrite++] = topBIdx; wallTris[wallTriWrite++] = botAIdx; wallTris[wallTriWrite++] = botBIdx;
                wallTris[wallTriWrite++] = topAIdx; wallTris[wallTriWrite++] = topBIdx; wallTris[wallTriWrite++] = botAIdx;
                wallTris[wallTriWrite++] = topBIdx; wallTris[wallTriWrite++] = botBIdx; wallTris[wallTriWrite++] = botAIdx;
            }

            _undersideMesh.Clear();
            _undersideMesh.vertices = allVerts;
            _undersideMesh.uv = allUVs;
            _undersideMesh.colors32 = allColors;
            _undersideMesh.subMeshCount = 2;
            _undersideMesh.SetTriangles(domeTris, submesh: 0);
            _undersideMesh.SetTriangles(wallTris, submesh: 1);
            _undersideMesh.RecalculateNormals();
            _undersideMesh.RecalculateBounds();

            int totalTriCount = domeTris.Length + wallTris.Length;

            int meshTriCount = totalTriCount;
            if (_undersideCollider != null && meshTriCount > 0 && _colliderCurrentlyEnabled)
            {
                _undersideCollider.sharedMesh = null;
                _undersideCollider.sharedMesh = _undersideMesh;
            }
            else if (_undersideCollider != null)
            {
                _undersideCollider.sharedMesh = null;
            }
            _undersideMeshTriCount = meshTriCount;

            FiresLogger.LogVerbose($"[TileMeshUnderside] tile=({_tile?.TileXZ.x ?? -999},{_tile?.TileXZ.y ?? -999}) DOMED underside | center=({centerX:F1},{centerZ:F1}) maxR={maxRadius:F1} sideWall={FLOATING_ISLAND_SIDE_WALL_DEPTH} domeDepth={domeDepth:F1} boundaryEdges={boundaryEdges.Count}");
        }

        // Helper: pack (a, b) edge into a unique long key (smaller index first)
        // and increment its count in the dictionary.
        private static void BumpEdge(System.Collections.Generic.Dictionary<long, int> dict, int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            long key = ((long)lo << 32) | (uint)hi;
            dict[key] = dict.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        private void OnDestroy()
        {
            // Disconnect mesh refs before destroying to avoid NavMesh / physics
            // dangling-reference warnings.
            if (_undersideMeshFilter != null) _undersideMeshFilter.sharedMesh = null;
            if (_undersideCollider != null) _undersideCollider.sharedMesh = null;
            if (_undersideRenderer != null) _undersideRenderer.enabled = false;

            if (_undersideMesh != null)
            {
                _undersideMesh.Clear();
                DestroyImmediate(_undersideMesh);
                _undersideMesh = null;
            }

            if (_undersideGO != null)
            {
                Destroy(_undersideGO);
                _undersideGO = null;
            }
        }

        // ── Material capture (cached static — every tile shares one instance) ──

        // Same prefab fallback list as DirtFloorMeshCombiner.Underside.cs.
        // Ordered by preference: forest rock = dark cave-ish texture.
        // (Round-35 reorder REVERTED round-36 — user clarified the lighter
        // material was ONLY for floating-island domes, not for the cave-
        // ceiling underside that stretches to terrain. Standard underside
        // keeps the dark cave look so caves stay dark inside.)
        private static readonly string[] s_undersidePrefabCandidates = new[]
        {
            "rock4_forest",
            "rock4_coast",
            "rock1_mountain",
            "CaveRock",
            "cliff_segment",
            "rock4_copper",
            "highstone",
            "Stone_Pile",
        };

        private static Material GetOrCreateUndersideMaterial()
        {
            if (s_sharedUndersideMaterial != null) return s_sharedUndersideMaterial;

            if (!s_captureAttempted)
            {
                s_captureAttempted = true;
                TryCaptureUndersideMaterial();
            }

            if (s_sharedUndersideMaterial != null) return s_sharedUndersideMaterial;

            // Final fallback: dark gray Standard shader.
            s_sharedUndersideMaterial = CreateFallbackUndersideMaterial();
            return s_sharedUndersideMaterial;
        }

        // Round-43 — a separate, NON-DARKENED rock material for the
        // floating-island side walls. The cave-ceiling material above
        // gets darkened to 0.4× because dark cave aesthetics; the wall
        // material is the source color unscaled because a sunlit cliff
        // face is supposed to read as bright rock, not cave shadow.
        // Without this split the wall + cave use the same darkened
        // material and the cliff face goes nearly black under overhead
        // sun (N·L ≈ 0 + 0.4× source color = invisible).
        private static Material s_sharedWallMaterial;

        public static Material GetOrCreateWallMaterial()
        {
            if (s_sharedWallMaterial != null) return s_sharedWallMaterial;

            if (ZNetScene.instance == null)
            {
                FiresLogger.LogWarning("[TileMeshUnderside] GetOrCreateWallMaterial: ZNetScene.instance is null — falling back to cave-rock.");
                return GetOrCreateUndersideMaterial();
            }

            foreach (string prefabName in s_undersidePrefabCandidates)
            {
                var prefab = ZNetScene.instance.GetPrefab(prefabName);
                if (prefab == null) continue;
                var rend = prefab.GetComponentInChildren<MeshRenderer>();
                if (rend == null || rend.sharedMaterial == null) continue;
                var shader = rend.sharedMaterial.shader;
                if (shader == null || shader.name.Contains("InternalErrorShader")) continue;

                // Copy the source rock material AS-IS (no darkening). This
                // is the floating-island cliff side wall — should read as
                // a bright rugged rock face, not a cave ceiling.
                s_sharedWallMaterial = new Material(rend.sharedMaterial)
                {
                    name = $"TileWall_From_{prefabName}",
                };
                // 2026-05-27 round-44 — override the texture scale so
                // our UV0 mapping is authoritative. The source rock
                // prefab's material typically bakes _MainTex_ST to ~4×
                // or 10× (matching the prefab's own mesh proportions);
                // compounding that with our (worldX, worldY) UVs gave
                // the user's "tiling a billion times" artifact. Force
                // (1, 1) so 1 UV unit == 1 texture tile, then our UV
                // scale alone controls how densely the rock pattern
                // repeats on the wall.
                if (s_sharedWallMaterial.HasProperty("_MainTex"))
                {
                    s_sharedWallMaterial.SetTextureScale("_MainTex", new Vector2(1f, 1f));
                    s_sharedWallMaterial.SetTextureOffset("_MainTex", Vector2.zero);
                }
                if (s_sharedWallMaterial.HasProperty("_BumpMap"))
                {
                    s_sharedWallMaterial.SetTextureScale("_BumpMap", new Vector2(1f, 1f));
                    s_sharedWallMaterial.SetTextureOffset("_BumpMap", Vector2.zero);
                }

                // 2026-05-27 round-46 — diffuse-only lightening.
                //
                // Round-45 tried adding _EmissionColor at 0.20 to lift
                // the wall off pitch-black in shadow, but the result
                // was the OPPOSITE: in shadow / night the wall glowed
                // pure white because the emission swamped the diffuse.
                // Even with _EmissionMap = MainTex modulation, Standard
                // shader at our brightness level was too aggressive.
                //
                // Reverted to diffuse-only: bump the _Color tint to
                // ~1.8× (no upper clamp so the texture brightens beyond
                // source level instead of clipping). No emission.
                // Shadowed areas stay dark — that's expected on a
                // vertical cliff — but lit areas read MUCH brighter.
                // If still too dark in shadow after testing, the
                // texture swap (rock1_mountain instead of rock4_forest)
                // is the next lever.
                if (s_sharedWallMaterial.HasProperty("_Color"))
                {
                    var c = s_sharedWallMaterial.GetColor("_Color");
                    s_sharedWallMaterial.SetColor("_Color", new Color(
                        c.r * 1.8f, c.g * 1.8f, c.b * 1.8f, c.a));
                }
                // Explicitly DISABLE emission in case it was on in the
                // source material (some Valheim rock prefabs carry a
                // small _EmissionColor for moss).
                if (s_sharedWallMaterial.HasProperty("_EmissionColor"))
                {
                    s_sharedWallMaterial.DisableKeyword("_EMISSION");
                    s_sharedWallMaterial.SetColor("_EmissionColor", Color.black);
                    s_sharedWallMaterial.globalIlluminationFlags =
                        UnityEngine.MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                }

                FiresLogger.LogInfo($"[TileMeshUnderside] Captured wall material from '{prefabName}' (shader='{shader.name}', _MainTex_ST forced 1:1, color 1.8× boost, emission DISABLED).");
                return s_sharedWallMaterial;
            }

            // Fall back to the darkened cave material if no source rock.
            FiresLogger.LogWarning("[TileMeshUnderside] No prefab rock available for wall material — using darkened cave material.");
            return GetOrCreateUndersideMaterial();
        }

        private static void TryCaptureUndersideMaterial()
        {
            if (ZNetScene.instance == null)
            {
                FiresLogger.LogWarning("[TileMeshUnderside] TryCaptureUndersideMaterial: ZNetScene.instance is null — skipping.");
                return;
            }

            foreach (string prefabName in s_undersidePrefabCandidates)
            {
                var prefab = ZNetScene.instance.GetPrefab(prefabName);
                if (prefab == null) continue;

                var rend = prefab.GetComponentInChildren<MeshRenderer>();
                if (rend == null || rend.sharedMaterial == null) continue;

                var shader = rend.sharedMaterial.shader;
                if (shader == null || shader.name.Contains("InternalErrorShader")) continue;

                // Copy + darken to 0.4× (matches RPG maker pattern — cave ceiling vibe).
                s_sharedUndersideMaterial = new Material(rend.sharedMaterial)
                {
                    name = $"TileUnderside_From_{prefabName}",
                };
                if (s_sharedUndersideMaterial.HasProperty("_Color"))
                {
                    var c = s_sharedUndersideMaterial.GetColor("_Color");
                    s_sharedUndersideMaterial.SetColor("_Color", c * 0.4f);
                }

                FiresLogger.LogInfo($"[TileMeshUnderside] Captured underside material from '{prefabName}' (shader='{shader.name}', darkened to 0.4× of source color).");
                return;
            }

            FiresLogger.LogWarning("[TileMeshUnderside] Could not capture from any vanilla rock prefab — using fallback gray material.");
        }

        private static Material CreateFallbackUndersideMaterial()
        {
            // Try Standard, then any lit shader we can find.
            var shader = Shader.Find("Standard")
                      ?? Shader.Find("Custom/Piece")
                      ?? Shader.Find("Diffuse")
                      ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                FiresLogger.LogError("[TileMeshUnderside] No usable shader found for fallback underside material — underside will render pink.");
                return null;
            }

            var mat = new Material(shader)
            {
                name = "TileUnderside_Fallback",
            };
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", new Color(0.15f, 0.12f, 0.10f, 1f));
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.1f);
            if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0f);
            return mat;
        }
    }
}
