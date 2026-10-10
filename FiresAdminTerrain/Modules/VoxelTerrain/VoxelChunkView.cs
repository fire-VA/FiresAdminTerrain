using UnityEngine;
using FiresCore.Materials;

namespace VerdantsAscent.Terrain.Voxel
{
    // Voxel P0 — scene presence of one chunk: MeshFilter/Renderer + non-convex
    // MeshCollider on the piece layer (the proven TileMeshUnderside recipe —
    // players walk on it, bump ceilings, raycasts hit from any side).
    //
    // Material = TerrainMaterialCache.GetCurrentMaterial() — the SAME captured
    // vanilla Custom/Heightmap material the tile/disk terrain renders with
    // (user: voxel surfaces must match the disks). The heightmap shader blends
    // rock onto steep faces by itself, so carve walls/ceilings read as rock
    // while tops read as grass — consistent with vanilla terrain and our tiles.
    // Vertex color = biome mask (Meadows (0,0,0,0) default, like unmodified
    // tiles); UV0 = chunk-local 0..1 (paint-mask convention).
    [DisallowMultipleComponent]
    public sealed class VoxelChunkView : MonoBehaviour
    {
        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private MeshCollider _collider;
        private Mesh _mesh;
        private Color32[] _seasonFreeColors;
        private int _seasonFreeCount;

        // True from apply→bake dispatch until the baked collider is assigned —
        // the pipeline defers re-applies so the mesh is never mutated while
        // PhysX reads it on the worker.
        public bool BakeInFlight;

        // W1 — the zone streamer swaps vanilla off only once every chunk's
        // collider is live (or the chunk has no surface). False while a fresh
        // apply's bake is still in flight.
        public bool ColliderReady { get; private set; }

        // W1.2 — renderer suppressed until the zone swap flips voxel-on /
        // vanilla-off in the same frame (no coplanar z-fight window).
        public bool RenderSuppressed;

        public void SetRenderSuppressed(bool suppressed)
        {
            RenderSuppressed = suppressed;
            RefreshRenderVisibility();
        }

        // The one place this view's renderers are switched on or off: a surface
        // to draw, the swap's suppression, and vanilla's overworld render group
        // (hidden while the player is inside an interior). Colliders are never
        // touched - vanilla keeps its terrain collidable in there too.
        internal void RefreshRenderVisibility()
        {
            bool overworldVisible = VoxelRenderGroup.OverworldVisible;
            // NEVER DRAW A SURFACE YOU CANNOT STAND ON. When HasCollidableTriangle rejects a surface as
            // sliver-only the collider is cleared but the render mesh keeps its triangles, so the chunk
            // draws ground that nothing can collide with - a visible layer the player falls straight
            // through. Reported on sky islands whose water carve leaves a thin, nearly-flat surface,
            // which is exactly the shape the 0.002 area test throws out.
            //
            // Hiding it is the honest half of the trade: a hole where the sliver was is obviously wrong
            // and gets fixed, whereas solid-looking ground that is not solid reads as a physics bug and
            // costs an hour every time. The mesher keeping the surface out of the buffer in the first
            // place is the real fix; this stops it lying in the meantime.
            if (_renderer != null)
                _renderer.enabled = VertexCount > 0 && !_surfaceNotCollidable && !RenderSuppressed && overworldVisible;
            // 0.2.763: the water and ice sheets wait for the swap with their ground (a skyland lake no longer shows over its
            // far piece before the island does).
            if (_waterRenderer != null) _waterRenderer.enabled = overworldVisible && !RenderSuppressed;
            if (_iceRenderer != null) _iceRenderer.enabled = overworldVisible && !RenderSuppressed;
        }

        public int VertexCount { get; private set; }
        public int TriangleCount { get; private set; }

        // Seam auditor — the ACTUAL rendered mesh, and the material identity
        // (mismatched instances at a face = the material-mixing seam class).
        public Mesh AuditMesh => _mesh;
        public int MaterialInstanceId()
            => _renderer != null && _renderer.sharedMaterial != null ? _renderer.sharedMaterial.GetInstanceID() : 0;

        // Each chunk that casts shadows is submitted to the shadow pass a second
        // time; with several hundred chunks live that is a large share of the
        // frame's draw calls. Config-driven and applied live so the trade can be
        // judged in place rather than guessed at.
        internal static void ApplyShadowMode(MeshRenderer r)
        {
            if (r == null) return;
            bool cast = FiresVoxelConfig.TerrainCastShadows == null
                || FiresVoxelConfig.TerrainCastShadows.Value;
            r.shadowCastingMode = cast
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        internal void RefreshShadowMode() => ApplyShadowMode(_renderer);

        internal Vector3Int Key;   // the chunk this view draws (VoxelWorld.GetOrCreateView)

        public void Init()
        {
            // TERRAIN layer, same as the tile meshes (TileMeshRenderer): vanilla
            // ground queries raycast it, grass/clutter grows on it, build pieces
            // find support on it — and LiquidVolume pours drape their grids onto
            // it, which is what lets our custom water flow along carved cave
            // floors. Piece layer was the P0 stand-in.
            int terrainLayer = LayerMask.NameToLayer("terrain");
            if (terrainLayer >= 0) gameObject.layer = terrainLayer;
            else
            {
                int pieceLayer = LayerMask.NameToLayer("piece");
                if (pieceLayer >= 0) gameObject.layer = pieceLayer;
            }

            _filter = gameObject.AddComponent<MeshFilter>();
            // W3-A3 — headless dedi: colliders ARE the product; no renderer,
            // no material lookups (shader/bundle access is a client concern).
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                _renderer = gameObject.AddComponent<MeshRenderer>();
                _renderer.sharedMaterial = TerrainMaterialCache.GetCurrentMaterial();
                ApplyShadowMode(_renderer);
            }

            _collider = gameObject.AddComponent<MeshCollider>();
            _collider.convex = false;

            _mesh = new Mesh
            {
                name = gameObject.name,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            _filter.sharedMesh = _mesh;
        }

        // Fill the render mesh from worker output. Returns the mesh instance id
        // for the off-thread PhysX bake, or 0 when the chunk has no surface
        // (fully solid/air) — renderer hidden, collider cleared, nothing to bake.
        // A surface made only of sliver triangles is not baked either: PhysX
        // welds them away and rejects the empty result ("cleaning the mesh
        // failed"), once on the bake worker and again on the main-thread assign.
        public int ApplyBuffers(MeshBuffers buf)
        {
            VertexCount = buf.Verts.Count;
            TriangleCount = buf.Tris.Count / 3;

            if (VertexCount == 0)
            {
                _mesh.Clear(false);
                _collider.sharedMesh = null;
                _surfaceNotCollidable = false;   // no surface at all is not a sliver surface
                ColliderReady = true;   // nothing to collide with — ready by definition
                ApplyGenWater(buf);     // an all-air/solid layer can still OWN the sheet band
                RefreshRenderVisibility();
                return 0;
            }
            ColliderReady = false;

            _mesh.Clear(false);
            // The three handoffs where the engine allocates on OUR main-thread frame, split so a profile names which:
            // [perf] pinned 601 blocks of 39,336 B a run to "(native code) under StreamerDriver.Update" with no managed
            // frame below, and set-vertices / set-triangles / collider-assign are the candidates.
            using (VertsMarker().Auto())
            {
            _mesh.SetVertices(buf.Verts);
            // Headless dedi: the mesher fills only Verts+Tris (CollidersOnly), so
            // the render attributes are empty — a MeshCollider needs neither.
            // Setting them would be waste at best, count-mismatched at worst.
            if (_renderer != null)
            {
                _mesh.SetNormals(buf.Normals);
                // Analytic world-anchored tangents from the mesher — per-mesh
                // RecalculateTangents gave coincident border vertices different
                // tangents (each mesh averages only its own triangles) and drew
                // the groove lines along every 32m border.
                _mesh.SetTangents(buf.Tangents);
                // 0.2.758: each vertex wears its own layer's paint - the ground the column's mask and turned-ground circles,
                // a cave floor or a skyland the paint laid on it (VoxelLayerPaint; UV0 into the mask's palette texels).
                VoxelLayerPaint.Render(Key, buf.Verts, buf.Uvs, buf.Uv1, buf.Colors);
                _mesh.SetUVs(0, buf.Uvs);
                _mesh.SetUVs(1, buf.Uv1);   // UV1.x = per-vertex inland waterline (FiresVoxelHeightmap reads v.texcoord1.x)
                KeepSeasonFreeColors(buf.Colors);
                _mesh.SetColors(FatSeasons.RenderColors(buf.Colors, buf.Verts));
            }
            }
            using (TrisMarker().Auto()) _mesh.SetTriangles(buf.Tris, 0, calculateBounds: true);
            if (_renderer != null)
            {
                // Self-heal: if the vanilla heightmap material captured AFTER this
                // view spawned (Standard fallback), pick it up on the next remesh.
                var mat = TerrainMaterialCache.GetCurrentMaterial();
                if (_renderer.sharedMaterial != mat) _renderer.sharedMaterial = mat;
            }
            bool collidable = HasCollidableTriangle(buf);
            _surfaceNotCollidable = !collidable;
            if (!collidable)
            {
                _collider.sharedMesh = null;
                ColliderReady = true;
                ReportSliverOnlySurface();
            }
            ApplyGenWater(buf);
            RefreshRenderVisibility();
            return collidable ? _mesh.GetInstanceID() : 0;
        }

        private const float MinCollidableTriangleArea = 0.002f;

        // Vedr seasons: the mesher's colours are the true biome colours; winter is added only for the renderer, and the
        // season-free copy lets a season change recolour this mesh without remeshing or rebaking its collider.
        internal Vector3 WorldCentre => _mesh != null ? _mesh.bounds.center : transform.position;

        private void KeepSeasonFreeColors(System.Collections.Generic.List<Color32> colors)
        {
            if (!FatSeasons.Tracking) return;
            if (_seasonFreeColors == null || _seasonFreeColors.Length < colors.Count) _seasonFreeColors = new Color32[colors.Count];
            colors.CopyTo(_seasonFreeColors);
            _seasonFreeCount = colors.Count;
        }

        internal void RecolourForSeason()
        {
            if (_renderer == null || _seasonFreeColors == null || _seasonFreeCount != _mesh.vertexCount) return;
            _mesh.SetColors(FatSeasons.RenderColors(_seasonFreeColors, _seasonFreeCount, _mesh));
        }
        private const float MinCollidableCrossSqr = 4f * MinCollidableTriangleArea * MinCollidableTriangleArea;

        private bool _reportedSliverOnlySurface;

        // Set by ApplyBuffers when the surface is sliver-only, so the renderer gate can keep it hidden.
        // Reset on every apply, because a chunk that was slivers can become real ground on the next edit.
        private bool _surfaceNotCollidable;

        private static bool HasCollidableTriangle(MeshBuffers buf)
        {
            var verts = buf.Verts;
            var tris = buf.Tris;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                var corner = verts[tris[i]];
                var cross = Vector3.Cross(verts[tris[i + 1]] - corner, verts[tris[i + 2]] - corner);
                if (cross.sqrMagnitude > MinCollidableCrossSqr) return true;
            }
            return false;
        }

        private void ReportSliverOnlySurface()
        {
            if (_reportedSliverOnlySurface) return;
            _reportedSliverOnlySurface = true;
            FiresLogger.LogInfo($"[FiresVoxel] {name}: {VertexCount} vertices / {TriangleCount} triangles, none over " +
                                $"{MinCollidableTriangleArea} m^2 - collider cleared instead of cooked.");
        }

        private static readonly int s_clearedMaskProp = Shader.PropertyToID("_ClearedMaskTex");
        private static readonly int s_localWaterProp = Shader.PropertyToID("_FiresLocalWaterY");
        private static MaterialPropertyBlock s_mpb;
        private Texture2D _paintTex;
        private float _localWaterY;   // 0 = no gen water over this chunk (shader collapses to vanilla)

        // W3.3 — per-column paint mask, bound per-renderer so the SHARED
        // material (tiles, FarQuilt, distant-LOD) keeps its blank mask. The
        // property block lives on the renderer, so it survives both remeshes
        // and the sharedMaterial self-heal above. Also carries the per-chunk
        // gen waterline (_FiresLocalWaterY) so beds below an inland river/lake
        // surface get the vanilla sub-30 wet-shore treatment.
        public void ApplyPaintMask(Texture2D tex)
        {
            _paintTex = tex;
            PushBlock();
        }

        // Local gen-water surface over this chunk (max over its footprint), or
        // 0 when dry. Set from VoxelWorld.RefreshPaintMask alongside the mask.
        public void SetLocalWaterY(float y)
        {
            if (Mathf.Approximately(_localWaterY, y)) return;
            _localWaterY = y;
            PushBlock();
        }

        private void PushBlock()
        {
            if (_renderer == null) return;
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            s_mpb.Clear();
            if (_paintTex != null) s_mpb.SetTexture(s_clearedMaskProp, _paintTex);
            s_mpb.SetFloat(s_localWaterProp, _localWaterY);
            _renderer.SetPropertyBlock(s_mpb);
        }

        public void AssignCollider()
        {
            if (_collider == null || _mesh == null || VertexCount == 0) return;
            using (ColliderMarker().Auto())
            {
                _collider.sharedMesh = null;
                _collider.sharedMesh = _mesh;
            }
            ColliderReady = true;
            FatClutterRegrow.Collider(_collider.bounds);   // 0.2.690/0.2.693: the grass over this chunk re-rolls on its new surface (vanilla's ResetGrass after an edit)
            // 0.2.689 ([lead] RIDEg pit): when a dedicated server's voxel collider is (re)built, at Debug.
            if (ZNet.instance != null && ZNet.instance.IsDedicated() && FiresLogger.DebugEnabled)
                FiresLogger.LogDebug($"[FiresVoxel] server collider built: '{name}' at ({_collider.bounds.center.x:F0},{_collider.bounds.center.y:F0},{_collider.bounds.center.z:F0}), "
                    + $"{VertexCount} vertices, t {Time.time:F1}.");
        }

        // Made on first use, never in a field initializer — a marker built before the profiler subsystem is up is a
        // handle to nothing.
        private static bool s_markersMade;
        private static Unity.Profiling.ProfilerMarker s_verts, s_tris, s_collider;

        private static void EnsureMarkers()
        {
            if (s_markersMade) return;
            s_verts = new Unity.Profiling.ProfilerMarker("FAT mesh apply: set-vertices");
            s_tris = new Unity.Profiling.ProfilerMarker("FAT mesh apply: set-triangles");
            s_collider = new Unity.Profiling.ProfilerMarker("FAT mesh apply: collider-assign");
            s_markersMade = true;
        }

        private static Unity.Profiling.ProfilerMarker VertsMarker() { EnsureMarkers(); return s_verts; }
        private static Unity.Profiling.ProfilerMarker TrisMarker() { EnsureMarkers(); return s_tris; }
        private static Unity.Profiling.ProfilerMarker ColliderMarker() { EnsureMarkers(); return s_collider; }

        // ── Generated water — carve + fill in ONE pass ───────────────────
        // The water sheet for this chunk's columns, emitted from the SAME
        // reconciled level the bed carve was cut for (VoxelChunk._genWater).
        // Static child mesh: no sim, no threads, no raycasts, no settle, no
        // ZDO — it applies WITH the terrain and dies with the view. Dry/wet
        // classification is deliberately absent: the sheet overshoots into
        // the banks and the Z-BUFFER cuts the shoreline against the terrain,
        // exactly how vanilla's ocean gets its pixel-perfect edge.
        private GameObject _waterGO;
        private GameObject _fallsGO;
        private Mesh _fallsMesh;
        private Mesh _waterMesh;
        private MeshRenderer _waterRenderer;
        internal MeshRenderer WaterRenderer => _waterRenderer;   // 0.2.821: FatShaftUnderwater sets its underwater MPB
        // FROZEN-class columns render as ICE instead: walkable MeshCollider
        // on a GO named "IceShell" — IceSlidingPatches detects ground
        // colliders by that name, so generated lakes slide exactly like the
        // Place Ice hammer pieces.
        private GameObject _iceGO;
        private Mesh _iceMesh;
        private MeshRenderer _iceRenderer;
        private MeshCollider _iceCollider;
        private static bool s_iMatWarned, s_iFirstSheet;
        private static bool s_wMatBound, s_wMatWarned, s_wFirstSheet;
        internal static Material BoundSheetMaterial;
        private static float[] s_wExtA, s_wExtB;
        // Ground at a chunk-local column (apron offset +1), from the snapshot
        // that built this mesh. Outside the apron there is no data — the caller
        // treats that as "unknown" and leaves its guard off rather than acting
        // on a guess.
        private static bool TryBufGround(MeshBuffers buf, int lx, int lz, out float g)
        {
            g = 0f;
            int S = VoxelChunk.SAMPLES;
            int ix = lx + 1, iz = lz + 1;
            if (ix < 0 || iz < 0 || ix >= S || iz >= S) return false;
            g = buf.Ground[ix + S * iz];
            return g != 0f;
        }

        private static float[] s_wCornH;   // smooth-field corner heights, ring-extended
        private static float[] s_wCornS;   // ping-pong scratch for the surface-smoothing Jacobi passes
        private static float[] s_wCornD;   // smooth-field corner depths, ring-extended
        private static float[] s_wCornT;   // ping-pong scratch for the dry-corner extension
        private static float[] s_wCornHx;  // heights with dry corners RESOLVED ONCE (see the extension pass)
        private static float[] s_wCornDx;  // the matching depths
        private static Vector2[] s_wCornF; // per-corner downhill flow (m/m, depth-damped), ring-extended
        private static float[] s_wCornC;   // per-corner flow COHERENCE 0..1 (confluence detect), ring-extended

        // ── Sheet material modes (va_voxelwater_mat) ─────────────────────
        // The shared pour material is built FOR LiquidVolume's draped sim
        // meshes: _FiresLiquidGrid=1 drags in the whole grid behavior set —
        // vertex-alpha wet clip, stencil pixel-claiming between overlapping
        // volumes, camera-above back-face discard keyed on the submerged
        // global. Our static sheet has no dry cells and needs NONE of it, and
        // any of it can kill the draw in ways a source read can't prove. So:
        //   0 = shared pour material verbatim (grid mask ON — the pours' skin)
        //   1 = pour CLONE with _FiresLiquidGrid=0 (DEFAULT — plain water,
        //       zone/ocean semantics, no grid machinery)
        //   2 = Standard transparent blue (guaranteed-visible debug: if THIS
        //       doesn't draw, the bug is mesh/GO/layer, not the shader)
        // Modes swap live via the console command; the empirical answer beats
        // another day of shader archaeology.
        private static Material s_sheetMat;
        // FIELD-TESTED on screen: mode 0 — the shared pour material exactly as
        // the hand tools use it — is the one that renders our water correctly
        // with our shader. It is the default. Modes 1/2 exist as diagnostics
        // only; that they draw nothing/magenta is irrelevant.
        // 3 = Custom/FiresInlandWater, the ported sandbox inland shader, and the
        // DEFAULT now. It was mode 0 (the ocean's FiresLiquid) with 3 reachable
        // only by typing `va_voxelwater_mat 3` each session — so the whole port,
        // its depth-driven alpha and its ripple maps were never actually on the
        // water anyone looked at. Inland water is what this shader exists for;
        // 0 stays available for an A/B against the ocean material.
        // 3 = Custom/FiresInlandWater. It is the shader with the per-layer ripple
        // controls — tiling, scroll vector, rotation and tiling-break for EACH
        // normal — and FiresInlandWaterConfig now exposes all of them. FiresLiquid
        // cannot match that without editing it (one shared tiling, the detail
        // layer scaled by a literal 2.7), and that shader is off limits.
        private static int s_sheetMatMode = 3;

        // The sheet material in use, so the look config can re-apply live.
        internal static Material CurrentSheetMaterial => s_sheetMat;

        // OUR OWN RIPPLE MAPS. water_nrm / water_nrm_detail are the sandbox's water normals, shipped in Core's shader
        // bundle beside the shader they were authored against (FiresShaderBroker). Borrowing Valheim's liquid normals
        // worked, but these are the maps the port was tuned with. Falls back to the donor when absent.
        private static void CopyTex(Material from, Material to, string src, string dst)
        {
            if (from.HasProperty(src) && to.HasProperty(dst)) to.SetTexture(dst, from.GetTexture(src));
        }

        internal static Material ResolveSheetMaterial()   // 0.2.752: internal, placed water uses the calm inland sheet material too
        {
            if (s_sheetMat != null) return s_sheetMat;
            switch (s_sheetMatMode)
            {
                case 0:
                    s_sheetMat = VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.GetSharedPourMaterial();
                    break;
                // 3 — Custom/FiresInlandWater: the ported sandbox inland shader
                // (built-in RP port, from Core's shader bundle). Purpose-built for
                // shallow water — depth-driven HARDWARE alpha instead of the
                // ocean's opaque screen-space refraction, and no shared globals
                // with the ocean. Mode 0 (Custom/FiresLiquid) is untouched and
                // remains the default, so this is a live A/B.
                case 3:
                {
                    // 0.2.775 ([seasons] / fat_load_test Q1): a dedicated server has no graphics device, so no shader is ever
                    // live there and nothing draws the sheet: it takes the fallback quietly. The warning is for a CLIENT whose
                    // Core bundle lacks the shader (clients list firesinlandwater and bind it).
                    if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                    {
                        s_sheetMat = VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.GetSharedPourMaterial();
                        break;
                    }
                    var inland = FiresCore.Materials.FiresShaderBroker.Get("Custom/FiresInlandWater");
                    if (inland == null)
                    {
                        FiresLogger.LogWarning("[FiresVoxelWater] Custom/FiresInlandWater is not live (Core's shader bundle " +
                                               "lacks it); falling back to the FiresLiquid sheet material.");
                        s_sheetMat = VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.GetSharedPourMaterial();
                        break;
                    }
                    var im = new Material(inland) { name = "FiresInlandWater_Sheet" };
                    // Borrow the ripple/foam textures the liquid material already
                    // carries, so the port is not left sampling flat white.
                    var donor = VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.GetSharedPourMaterial();
                    if (donor != null)
                    {
                        // THE DONOR'S NAMES, NOT OURS. FiresLiquid calls its ripple
                        // maps _Normal and _NormalFine; the inland port calls them
                        // _BumpMap and _DetailBump. Asking the donor for "_BumpMap"
                        // matched nothing and CopyTex silently did nothing, so the
                        // sheet shaded against a FLAT normal — no ripple at all, which
                        // is the hard geometric look. _FoamTex happens to share a name
                        // and was the only map that ever arrived.
                        // Ours first, the donor only where ours is missing.
                        var nrm = FiresCore.Materials.FiresShaderBroker.GetTexture("water_nrm");
                        var nrmD = FiresCore.Materials.FiresShaderBroker.GetTexture("water_nrm_detail");
                        if (nrm != null) im.SetTexture("_BumpMap", nrm); else CopyTex(donor, im, "_Normal", "_BumpMap");
                        if (nrmD != null) im.SetTexture("_DetailBump", nrmD); else CopyTex(donor, im, "_NormalFine", "_DetailBump");
                        CopyTex(donor, im, "_FoamTex", "_FoamTex");
                        FiresLogger.LogInfo("[FiresVoxelWater] inland sheet maps — " +
                            $"bump={(im.GetTexture("_BumpMap") != null ? im.GetTexture("_BumpMap").name : "MISSING")}, " +
                            $"detail={(im.GetTexture("_DetailBump") != null ? im.GetTexture("_DetailBump").name : "MISSING")}, " +
                            $"foam={(im.GetTexture("_FoamTex") != null ? im.GetTexture("_FoamTex").name : "MISSING")}.");
                    }
                    VerdantsAscent.Water.Core.FiresInlandWaterConfig.Apply(im);
                    im.renderQueue = 3000;
                    s_sheetMat = im;
                    break;
                }
                case 2:
                    var std = Shader.Find("Standard");
                    if (std != null)
                    {
                        var dm = new Material(std) { name = "VoxelWater_Debug" };
                        dm.SetFloat("_Mode", 3f);
                        dm.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        dm.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        dm.SetInt("_ZWrite", 0);
                        dm.EnableKeyword("_ALPHABLEND_ON");
                        dm.renderQueue = 3000;
                        dm.color = new Color(0f, 0.55f, 0.85f, 0.6f);
                        s_sheetMat = dm;
                    }
                    break;
                default:
                {
                    var baseMat = VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.GetSharedPourMaterial();
                    if (baseMat == null)
                    {
                        try { VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.Ensure(); } catch { }
                        baseMat = VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.GetSharedPourMaterial();
                    }
                    if (baseMat != null)
                    {
                        var m = new Material(baseMat) { name = "FiresLiquid_Sheet" };
                        // CLONE, never the shared instance — the pours still
                        // need their grid mask; we just don't want it.
                        m.SetFloat("_FiresLiquidGrid", 0f);
                        s_sheetMat = m;
                    }
                    break;
                }
            }
            return s_sheetMat;
        }

        internal static void SetSheetMaterialMode(int mode)
        {
            s_sheetMatMode = Mathf.Clamp(mode, 0, 3);
            s_sheetMat = null;
            s_wMatBound = false;
            int reskinned = 0;
            var mat = ResolveSheetMaterial();
            foreach (var v in FindObjectsByType<VoxelChunkView>(FindObjectsSortMode.None))
            {
                if (v._waterRenderer == null) continue;
                v._waterRenderer.sharedMaterial = mat;
                reskinned++;
            }
            FiresLogger.LogInfo($"[FiresVoxelWater] sheet material mode -> {s_sheetMatMode} " +
                      $"({(mat != null ? mat.name : "NULL")}), re-skinned {reskinned} sheet(s).");
        }

        // Render-chain dump of the sheet nearest to `pos` — the diagnostic the
        // invisible-water hunt actually needs: what material/shader/queue is ON
        // the mesh, is the renderer enabled, where are its bounds.
        internal static string DumpNearestSheet(Vector3 pos)
        {
            VoxelChunkView best = null;
            float bestD = float.MaxValue;
            foreach (var v in FindObjectsByType<VoxelChunkView>(FindObjectsSortMode.None))
            {
                if (v._waterGO == null) continue;
                // Views sit at (0,0,0) — transform distance is meaningless.
                // Measure the sheet's actual world bounds.
                float d = v._waterRenderer != null
                    ? Vector3.Distance(v._waterRenderer.bounds.center, pos)
                    : Vector3.Distance(v.transform.position, pos);
                if (d < bestD) { bestD = d; best = v; }
            }
            if (best == null) return "no sheet objects exist anywhere (emission problem, not material)";
            var go = best._waterGO;
            var mr = best._waterRenderer;
            var mesh = best._waterMesh;
            var mat2 = mr != null ? mr.sharedMaterial : null;
            return $"nearest sheet {bestD:0}m away on view '{best.name}': goActive={go.activeInHierarchy} " +
                   $"layer={go.layer} verts={(mesh != null ? mesh.vertexCount : -1)} tris={(mesh != null ? mesh.triangles.Length / 3 : -1)} " +
                   $"bounds={(mr != null ? mr.bounds.center.ToString("F0") + "/" + mr.bounds.size.ToString("F0") : "?")} " +
                   $"rendererEnabled={(mr != null && mr.enabled)} visible={(mr != null && mr.isVisible)} " +
                   $"mat={(mat2 != null ? mat2.name : "NULL")} shader={(mat2 != null && mat2.shader != null ? mat2.shader.name : "?")} " +
                   $"queue={(mat2 != null ? mat2.renderQueue : -1)} grid={(mat2 != null && mat2.HasProperty("_FiresLiquidGrid") ? mat2.GetFloat("_FiresLiquidGrid") : -1f)}";
        }
        private static readonly System.Collections.Generic.List<Vector3> s_wVerts = new System.Collections.Generic.List<Vector3>(2048);
        private static readonly System.Collections.Generic.List<Vector3> s_wNorms = new System.Collections.Generic.List<Vector3>(2048);
        private static readonly System.Collections.Generic.List<Vector2> s_wUvs = new System.Collections.Generic.List<Vector2>(2048);
        // UV1 = downhill flow (.xy world m/m, depth-damped) + flow coherence (.z,
        // 0 at confluences) — the shader's flow-mapping input. Water sheet only;
        // ice and other meshes omit the stream and read (0,0,0) = still.
        private static readonly System.Collections.Generic.List<Vector4> s_wUv1 = new System.Collections.Generic.List<Vector4>(2048);
        private static readonly System.Collections.Generic.List<Vector4> s_wUv2 = new System.Collections.Generic.List<Vector4>(2048);
        private static readonly System.Collections.Generic.List<int> s_wTris = new System.Collections.Generic.List<int>(4096);
        private static readonly System.Collections.Generic.List<Color32> s_wCols = new System.Collections.Generic.List<Color32>(2048);
        private static readonly System.Collections.Generic.List<Vector3> s_iVerts = new System.Collections.Generic.List<Vector3>(512);
        private static readonly System.Collections.Generic.List<Vector3> s_iNorms = new System.Collections.Generic.List<Vector3>(512);
        private static readonly System.Collections.Generic.List<Vector2> s_iUvs = new System.Collections.Generic.List<Vector2>(512);
        private static readonly System.Collections.Generic.List<int> s_iTris = new System.Collections.Generic.List<int>(1024);

        // Called from INSIDE ApplyBuffers — water and terrain are ONE apply,
        // one snapshot, one lifecycle. There is no separate emission pass left
        // to diverge: if the snapshot's columns say water, the sheet is built
        // in the same call that builds the ground under it.
        private void ApplyGenWater(MeshBuffers buf)
        {
            bool on = FiresVoxelConfig.VoxelWater == null || FiresVoxelConfig.VoxelWater.Value;
            if (!on || buf == null || !buf.HasWater || _renderer == null)
            {
                if (_waterGO != null) _waterGO.SetActive(false);
                return;
            }
            BuildSheet(buf, null);
            ApplyIceSheet();
            if (s_wTris.Count == 0)
            {
                if (_waterGO != null) _waterGO.SetActive(false);
                return;
            }
            if (_waterGO == null)
            {
                _waterGO = new GameObject("genwater");
                // Default layer — the layer the LEGACY gen mesh and the hand
                // pours both rendered on (proven visible). The Water layer
                // interacts with vanilla's own water systems; not worth it.
                _waterGO.transform.SetParent(transform, false);
                var mf = _waterGO.AddComponent<MeshFilter>();
                _waterRenderer = _waterGO.AddComponent<MeshRenderer>();
                _waterRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _waterRenderer.receiveShadows = false;
                _waterMesh = new Mesh { name = "genwater" };
                mf.sharedMesh = _waterMesh;
            }
            // MATERIAL RETRY, not a one-shot, and MODE-based — see
            // ResolveSheetMaterial. Re-resolve on every apply until a material
            // binds, and say so in the log either way.
            if (_waterRenderer != null && _waterRenderer.sharedMaterial == null)
            {
                var mat = ResolveSheetMaterial();
                if (mat != null)
                {
                    _waterRenderer.sharedMaterial = mat;
                    BoundSheetMaterial = mat;   // far-water quilt reuses THE sheet material
                    if (!s_wMatBound)
                    {
                        s_wMatBound = true;
                        // Say MESH plainly. The material is the shared one the
                        // pour prefab also uses, so its NAME carries "Pour" —
                        // and a log line reading "FiresLiquid_Pour" next to
                        // water work reads as "we went back to pours". We did
                        // not: generated water is this chunk mesh sheet and
                        // nothing queues a pour for it.
                        FiresLogger.LogInfo($"[FiresVoxelWater] chunk water MESH sheet bound (mode {s_sheetMatMode}) — " +
                                  $"no pours are placed for generated water; material '{mat.name}' is just the shared " +
                                  $"liquid material: {(mat.shader != null ? mat.shader.name : "null")} queue={mat.renderQueue}");
                    }
                }
                else if (!s_wMatWarned)
                {
                    s_wMatWarned = true;
                    FiresLogger.LogWarning("[FiresVoxelWater] water sheets are building but no sheet material " +
                                     "resolves yet — sheets invisible until it does; retrying on every apply.");
                }
            }
            _waterGO.SetActive(true);
            _waterMesh.Clear(false);
            _waterMesh.SetVertices(s_wVerts);
            _waterMesh.SetNormals(s_wNorms);
            _waterMesh.SetUVs(0, s_wUvs);
            _waterMesh.SetUVs(1, s_wUv1);
            _waterMesh.SetUVs(2, s_wUv2);
            _waterMesh.SetColors(s_wCols);
            _waterMesh.SetTriangles(s_wTris, 0, calculateBounds: true);
            VoxelSheetFalls.Build(buf, _waterGO, ref _fallsGO, ref _fallsMesh);
            if (!s_wFirstSheet)
            {
                s_wFirstSheet = true;
                FiresLogger.LogInfo($"[FiresVoxelWater] first water sheet: view '{name}' " +
                          $"{s_wTris.Count / 6} quad(s) at {_waterMesh.bounds.center:F0}, " +
                          $"material {(_waterRenderer != null && _waterRenderer.sharedMaterial != null ? _waterRenderer.sharedMaterial.name : "PENDING")}.");
            }
        }

        // 0.2.765 ([seasons] 0.2.766): the sheet builder, lifted out of ApplyGenWater unchanged so placed water (WaterSheets, the
        // V-tools) renders through the same mesh as generated and skyland water (VoxelWaterSheetMesh.Build). It fills the s_w*
        // scratch lists from the chunk snapshot `buf` (WaterLvl = where the sheet is, Ground = the bed, <0 = mopped). `surface` (null =
        // the world's: GenWaterField + the skylands) gives the height at every corner, out to the 3-corner halo past the chunk.
        // The last BuildSheet's lists into a mesh (VoxelWaterSheetMesh.Build). False when it built nothing.
        internal static bool CopySheetTo(Mesh into)
        {
            if (s_wTris.Count == 0) return false;
            into.SetVertices(s_wVerts);
            into.SetNormals(s_wNorms);
            into.SetUVs(0, s_wUvs);
            into.SetUVs(1, s_wUv1);
            into.SetUVs(2, s_wUv2);
            into.SetColors(s_wCols);
            into.SetTriangles(s_wTris, 0, calculateBounds: true);
            return true;
        }

        internal static void BuildSheet(MeshBuffers buf, WaterSurfaceProvider surface)
        {
            int S = VoxelChunk.SAMPLES, C = VoxelChunk.CELLS;

            // OCEAN-STYLE OVERSHOOT. Vanilla's sea doesn't stop at its
            // shoreline — it runs on under the terrain and the depth buffer
            // clips it. Ending our sheet at the last wet column stair-stepped
            // the edge into jagged shards. Dilate the level field 3 cells into
            // dry ground (flat extension of the nearest wet level) and let the
            // banks bury the skirt.
            int n2 = S * S;
            if (s_wExtA == null) { s_wExtA = new float[n2]; s_wExtB = new float[n2]; }
            System.Array.Copy(buf.WaterLvl, s_wExtA, n2);
            for (int pass = 0; pass < 3; pass++)
            {
                var src = s_wExtA; var dst = s_wExtB;
                for (int z = 0; z < S; z++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        int i = x + S * z;
                        float v = src[i];
                        if (v < 0f) { dst[i] = v; continue; }   // mopped column — never re-lid
                        if (v <= 0f)
                        {
                            // MEAN of wet neighbours, not max: max-dilation
                            // pushed every skirt cell to its upstream
                            // neighbourhood's highest level, so on sloped
                            // runs the bank columns wore stepped plates that
                            // rode up the bank faces (user screenshots). The
                            // mean extends the flow gradient sideways.
                            float sum = 0f; int cnt = 0;
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                int zz = z + dz; if (zz < 0 || zz >= S) continue;
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int xx = x + dx; if (xx < 0 || xx >= S) continue;
                                    float nv = src[xx + S * zz];
                                    if (nv > 0f) { sum += nv; cnt++; }
                                }
                            }
                            if (cnt > 0) v = sum / cnt;
                            // No overhang past a lip. Ring 1 (adjacent to
                            // real store water) tolerates up to 2m of hang —
                            // waterfall lips and undercut tucks are real.
                            // OUTER rings exist ONLY to bury into RISING
                            // banks: an outer skirt cell hanging at all is
                            // past a bank CREST, and keeping it draped the
                            // sheet over the crest and rolled it down the far
                            // side on steep runs (user screenshot). Grid
                            // index → world: arrays are apron-offset by +1.
                            // Ground from THIS chunk's snapshot (column
                            // resolution, no query): asking the generator meant
                            // a converted world skipped the crest guard
                            // entirely, so the skirt draped the sheet over bank
                            // crests and rolled it down the far side.
                            if (v > 0f && TryBufGround(buf, x - 1, z - 1, out float skg))
                            {
                                if (skg < v - (pass == 0 ? 2f : 0.05f)) v = 0f;
                                // And no fake lid over a cave mouth: the store
                                // gate holes shaft columns; the skirt must not
                                // re-cover them with swimless visual water.
                                // SHALLOW COVER ONLY (skg − v < 6): deep in a
                                // bank wall the probe hits legitimate buried
                                // tunnels and culled the wall-side skirt —
                                // the slits/notches where the sheet failed to
                                // clip into the bank (user screenshot).
                                else if (FiresVoxelConfig.GeneratesCaves
                                    && skg - (v - 0.75f) > 0.5f
                                    && skg - v < 6f
                                    && VoxelCaveNoise.CarveSdf(buf.OriginX + (x - 1), v - 0.75f,
                                           buf.OriginZ + (z - 1), skg - (v - 0.75f)) > 0.25f)
                                    v = 0f;
                            }
                        }
                        dst[i] = v;
                    }
                }
                var t = s_wExtA; s_wExtA = s_wExtB; s_wExtB = t;
            }
            var lvls = s_wExtA;

            // SMOOTH SURFACE, ALWAYS (user mandate). Corner heights come from
            // ONE pure world function — TryGetWaterSurfaceSmooth, the same
            // reconciled 2m field the bed carve is cut for — sampled once per
            // corner. The old per-corner mix of stored column levels, flat
            // overshoot and curl-down dives crumpled the shoreline into a
            // folded ribbon that read as TWO see-through water layers at
            // grazing angles (user screenshot at a bank). The mesh's only job
            // is a continuous sheet; rapids and flowing water are expressed by
            // the SHADER + VFX (slope foam, flow normals), never by splitting
            // or stepping the geometry. Curl-down is deleted with prejudice —
            // the dilation's lip guard above already culls true drop-offs.
            // APRON = FILTER SUPPORT. The smoothing below is TWO Jacobi passes,
            // each reading +-1 corner, so a shared corner's value depends on
            // world data +-2 corners away. With the old 1-corner ring, pass 2
            // at a chunk's edge averaged a RAW apron value where the neighbour
            // chunk averaged its own pass-1 SMOOTHED value for the very same
            // world corner — two heights for one vertex, i.e. a crack along
            // every chunk boundary (the river seams). A 2-corner ring, with
            // pass 1 smoothing the inner ring as well, makes every chunk derive
            // an identical value for a shared corner by construction.
            // RING 3, not 2. The flow pass below reads the BED +-3 corners away
            // and the dry-corner extension runs two dilation passes, so a shared
            // corner's final value depends on world data up to 3 corners out. Any
            // shallower halo and the two chunks that share that corner derive it
            // from different inputs, which is a crack.
            const int RING = 3;
            const float CornerBedReach = 32f;   // 0.2.815: how far under the surface a corner's voxel bed is looked for
            int R = C + 1 + RING * 2;
            if (s_wCornH == null || s_wCornH.Length < R * R)
            {
                s_wCornH = new float[R * R]; s_wCornD = new float[R * R];
                s_wCornHx = new float[R * R]; s_wCornDx = new float[R * R];
                s_wCornT = new float[R * R];
            }
            if (s_wCornF == null || s_wCornF.Length < R * R)
            { s_wCornF = new Vector2[R * R]; s_wCornC = new float[R * R]; }
            for (int cz = -RING; cz <= C + RING; cz++)
                for (int cx = -RING; cx <= C + RING; cx++)
                {
                    // THROUGH THE PROVIDER. This corner grid — and the Jacobi
                    // smoothing below it — is the whole reason a sheet flows
                    // down a slope instead of stepping. Asking the generator
                    // directly meant a CONVERTED world got zeros here, every
                    // cell fell back to the flat-lid path, and the river
                    // rendered as geometric terraces with hard risers.
                    bool ok;
                    float sl, sd;
                    if (surface != null)
                        ok = surface(buf.OriginX + cx, buf.OriginZ + cz, out sl, out sd) && sl > 0f;   // 0.2.765: placed water's own heights
                    else
                    {
                        ok = GenWaterField.TryWaterSurfaceSmooth(
                            buf.OriginX + cx, buf.OriginZ + cz, out sl, out sd) && sl > 0f;
                        if (FiresSkyIslands.WaterSurfaceAt(buf.OriginX + cx, buf.OriginZ + cz, out float il, out float idepth)
                            && il >= buf.OriginY - 2f && il <= buf.OriginY + 34f)
                        {
                            ok = true;
                            sl = il;
                            sd = idepth;
                        }
                    }
                    // 0.2.815 (Fire 10-06: pale squares/triangles and a "clear then hard blue" step in the shallows): the
                    // shader's opacity, shore fade and collar key off this corner depth (UV2.x), and it came from the
                    // generated field, which disagrees with the voxel bed wherever a bank was shaped by a bake or a carve:
                    // one corner read 0, its neighbour metres, and the fade jumped across one triangle. The depth is now the
                    // water column over the VOXEL bed under the corner (smoothed below with its neighbours).
                    if (ok && VoxelWorld.TrySampleSurfaceY(buf.OriginX + cx, buf.OriginZ + cz, sl + 0.05f, sl - CornerBedReach, out float bed, out bool wall) && !wall)
                        sd = Mathf.Max(0f, sl - bed);
                    // 0.2.837 ([generator], 109.webp): over a big sinkhole's shaft the voxel bed under the river is the grotto ~25 m
                    // down; the river's own column ends at its bed (the shaft's lip height). The shader tints the underside by this.
                    if (ok && FatSinkFallCuts.InShaft(buf.OriginX + cx, buf.OriginZ + cz, out float riverBed) && !float.IsNaN(riverBed))
                        sd = Mathf.Min(sd, Mathf.Max(0f, sl - riverBed));
                    int gi = (cx + RING) + R * (cz + RING);
                    s_wCornH[gi] = ok ? sl : 0f;
                    s_wCornD[gi] = ok ? sd : 0f;
                }
            // SMOOTH THE SURFACE (user: water must "SMOOTH and FLOW", not step
            // rigidly down a slope). The level field descends a steep river in
            // discrete reaches, and the mesh rendered each as a flat terrace
            // with a hard riser between. Two Jacobi passes average each WET
            // corner with its wet neighbours — dry (0) corners never
            // contribute and are never written, so the sheet EDGE stays put
            // (no shrink, no bank climb) while the interior gradient softens
            // into a continuous ramp. s_wCornS is scratch (ping-pong).
            // Pass `it` smooths the band that still has a full +-1 neighbourhood
            // of already-correct data: the first pass covers the inner ring too,
            // so the second pass sees pass-1 values on both sides of a shared
            // corner exactly as the neighbouring chunk does.
            if (s_wCornS == null || s_wCornS.Length < R * R) s_wCornS = new float[R * R];
            for (int it = 0; it < 2; it++)
            {
                int band = RING - 1 - it;   // 1 then 0
                int lo = -band, hi = C + band;
                for (int cz = lo; cz <= hi; cz++)
                    for (int cx = lo; cx <= hi; cx++)
                    {
                        int gi = (cx + RING) + R * (cz + RING);
                        float hc = s_wCornH[gi];
                        if (hc <= 0f) { s_wCornS[gi] = 0f; continue; }
                        float sum = hc; int cnt = 1;
                        float nA = s_wCornH[gi - 1]; if (nA > 0f) { sum += nA; cnt++; }
                        float nB = s_wCornH[gi + 1]; if (nB > 0f) { sum += nB; cnt++; }
                        float nC = s_wCornH[gi - R]; if (nC > 0f) { sum += nC; cnt++; }
                        float nD = s_wCornH[gi + R]; if (nD > 0f) { sum += nD; cnt++; }
                        s_wCornS[gi] = sum / cnt;
                    }
                for (int cz = lo; cz <= hi; cz++)
                    for (int cx = lo; cx <= hi; cx++)
                    {
                        int gi = (cx + RING) + R * (cz + RING);
                        if (s_wCornH[gi] > 0f) s_wCornH[gi] = s_wCornS[gi];
                    }
            }

            // ── DRY CORNERS RESOLVE ONCE, HERE — NOT PER QUAD ────────────────
            // Every sheet quad used to invent its own height for a dry corner
            // (nearest wet sibling by a fixed preference order, else the cell
            // max). Two quads sharing one dry corner can reach different
            // answers that way, and because sheet verts are unshared nothing
            // welds them back — so the shore broke into disjoint plates at
            // different levels, which is the hard step along every bank.
            // Resolving it on the ring grid gives a dry corner ONE height for
            // every quad that touches it and, being pure world data derived
            // inside the halo, the same height in the neighbouring chunk.
            if (s_wCornT == null || s_wCornT.Length < R * R) s_wCornT = new float[R * R];
            if (s_wCornHx == null || s_wCornHx.Length < R * R)
            { s_wCornHx = new float[R * R]; s_wCornDx = new float[R * R]; }
            System.Array.Copy(s_wCornH, s_wCornHx, R * R);
            System.Array.Copy(s_wCornD, s_wCornDx, R * R);
            for (int it = 0; it < RING - 1; it++)
            {
                // Same shrinking band as the Jacobi passes, and for the same
                // reason: a pass may only write where its own reads are already
                // identical on both sides of a chunk boundary.
                int band = RING - 1 - it;
                int lo = -band, hi = C + band;
                for (int cz = lo; cz <= hi; cz++)
                    for (int cx = lo; cx <= hi; cx++)
                    {
                        int gi = (cx + RING) + R * (cz + RING);
                        s_wCornS[gi] = s_wCornHx[gi];
                        s_wCornT[gi] = s_wCornDx[gi];
                        if (s_wCornHx[gi] > 0f) continue;
                        float sh = 0f, sd = 0f; int cnt = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int nj = gi + (k == 0 ? -1 : k == 1 ? 1 : k == 2 ? -R : R);
                            if (s_wCornHx[nj] <= 0f) continue;
                            sh += s_wCornHx[nj]; sd += s_wCornDx[nj]; cnt++;
                        }
                        if (cnt == 0) continue;
                        s_wCornS[gi] = sh / cnt;
                        s_wCornT[gi] = sd / cnt;
                    }
                for (int cz = lo; cz <= hi; cz++)
                    for (int cx = lo; cx <= hi; cx++)
                    {
                        int gi = (cx + RING) + R * (cz + RING);
                        s_wCornHx[gi] = s_wCornS[gi];
                        s_wCornDx[gi] = s_wCornT[gi];
                    }
            }

            // 0.2.815: one 1-2-1 pass over the corner depths (the shader's opacity / shore fade input), wet corners only, so the
            // fade is a smooth gradient across cells instead of following each triangle. World data on a grid both chunks share
            // inside the halo, so a shared corner gets the same value on both sides.
            for (int cz = -(RING - 1); cz <= C + RING - 1; cz++)
                for (int cx = -(RING - 1); cx <= C + RING - 1; cx++)
                {
                    int gi = (cx + RING) + R * (cz + RING);
                    s_wCornT[gi] = s_wCornDx[gi];
                    if (s_wCornHx[gi] <= 0f) continue;
                    float sum = 4f * s_wCornDx[gi], w = 4f;
                    int[] nb = { gi - 1, gi + 1, gi - R, gi + R };
                    foreach (int j in nb) if (s_wCornHx[j] > 0f) { sum += s_wCornDx[j]; w += 1f; }
                    s_wCornT[gi] = sum / w;
                }
            for (int cz = -(RING - 1); cz <= C + RING - 1; cz++)
                for (int cx = -(RING - 1); cx <= C + RING - 1; cx++)
                {
                    int gi = (cx + RING) + R * (cz + RING);
                    s_wCornDx[gi] = s_wCornT[gi];
                }

            // DOWNHILL FLOW per corner — pure world data, cross-chunk
            // continuous like the heights. Two regimes:
            //   * RIVER/STREAM: the flood-leveled surface is near-FLAT along
            //     whole reaches (surface gradient ~0 = "river doesn't flow",
            //     user screenshot), so channels take a minimum visible drift
            //     whose DIRECTION comes from the BED gradient (the carve
            //     always descends downstream; ±3m central differences smooth
            //     it down-channel). Real surface slope still wins when
            //     steeper — rapids stay fast.
            //   * Everything else (ponds, lakes, swamp): pure surface
            //     gradient — genuinely still water stays still.
            // Depth damp calms the shore fringe (banks drag), and a slope cap
            // keeps waterfall lips from strobing the shader.
            // BOUNDS: one corner inside the halo, because the differences below
            // read +-1. That leaves flow defined over -2..C+2, which is what the
            // coherence pass needs to give every corner in 0..C a FULL
            // neighbourhood — the truncated 3-neighbour average it used to do at
            // cx==0 and cx==C handed the two chunks sharing that corner different
            // coherence, hence a different FlowWeight, hence a different swell
            // amplitude and direction, hence two heights for one vertex. That is
            // the split along the zone borders.
            int FLO = -(RING - 1), FHI = C + (RING - 1);
            for (int cz = FLO; cz <= FHI; cz++)
                for (int cx = FLO; cx <= FHI; cx++)
                {
                    int gi = (cx + RING) + R * (cz + RING);
                    Vector2 fl = Vector2.zero;
                    float hC = s_wCornHx[gi];
                    if (hC > 0f)
                    {
                        float hXm = s_wCornHx[gi - 1], hXp = s_wCornHx[gi + 1];
                        float hZm = s_wCornHx[gi - R], hZp = s_wCornHx[gi + R];
                        if (hXm > 0f && hXp > 0f && hZm > 0f && hZp > 0f)
                            fl = new Vector2((hXm - hXp) * 0.5f, (hZm - hZp) * 0.5f);

                        float wcx = buf.OriginX + cx, wcz = buf.OriginZ + cz;
                        byte cls = GenWaterField.WaterClassAt(wcx, wcz);
                        if (cls == FiresWorldGenBase.WC_RIVER || cls == FiresWorldGenBase.WC_STREAM)
                        {
                            float smag = fl.magnitude;
                            const float DRIFT = 0.025f;   // slope-equivalent floor (~0.5 m/s in-shader)
                            // THE BED COMES FROM THE WORLD, NOT THE CHUNK BUFFER.
                            // TryBufGround only reaches one corner past the chunk,
                            // so a +-3 probe SUCCEEDED near the middle of a chunk
                            // and FAILED near its edge — the same world corner got
                            // a real bed gradient from one chunk and the bare floor
                            // from its neighbour. GenWaterField.TryGround answers
                            // from the generator's own field and cannot know or
                            // care which chunk is asking.
                            if (smag < DRIFT
                                && GenWaterField.TryGround(wcx - 3f, wcz, out float bxm)
                                && GenWaterField.TryGround(wcx + 3f, wcz, out float bxp)
                                && GenWaterField.TryGround(wcx, wcz - 3f, out float bzm)
                                && GenWaterField.TryGround(wcx, wcz + 3f, out float bzp))
                            {
                                var bed = new Vector2((bxm - bxp) * (1f / 6f), (bzm - bzp) * (1f / 6f));
                                float bmag = bed.magnitude;
                                if (bmag > 1e-4f) fl = bed * (DRIFT / bmag);
                                else if (smag > 1e-5f) fl *= DRIFT / smag;
                            }
                        }

                        // AUTHORED DIRECTION WINS. On an imported world the source
                        // solved the hydrology and its flow field is beside the cache;
                        // the surface gradient here is near-flat by construction and
                        // its direction is mostly noise. Keep the MAGNITUDE the
                        // gradient worked out (that is what varies reach to reach and
                        // drives the rapids threshold) and take only the DIRECTION
                        // from the source, so the current runs downstream everywhere
                        // instead of turning over per quad.
                        if (FiresImportedFlow.Active
                            && FiresImportedFlow.TryGetDir(wcx, wcz, out Vector2 authored))
                        {
                            // SPEED COMES FROM THE BED, NOT THE SURFACE. Keeping the
                            // surface-gradient magnitude here was wrong: that surface is
                            // deliberately flat (the monotone pass makes it so), giving
                            // |flow| ~ 0.012 and a scroll rate of 0.24 m/s — visually
                            // frozen — and dropping flowAmt low enough that the shader
                            // fell back to its WIND path, whose two layers scroll at
                            // fixed angles unrelated to the river. A river's pace follows
                            // the grade it runs down, so read the grade off the carved
                            // bed along the direction the source gave us.
                            // Same correction as the drift path, and this one was
                            // the worse offender: the probe point moves with the
                            // authored direction, so whether it fell inside the
                            // apron varied corner to corner. Where it fell out the
                            // grade read 0 and the magnitude collapsed to the floor
                            // while the neighbouring chunk, probing the same world
                            // point from the other side, got the real grade.
                            float grade = 0f;
                            if (GenWaterField.TryGround(wcx, wcz, out float bHere)
                                && GenWaterField.TryGround(wcx + authored.x * 3f, wcz + authored.y * 3f, out float bAhead))
                                grade = Mathf.Max(0f, (bHere - bAhead) * (1f / 6f));
                            // Floor keeps a lazy river drifting; the cap is the same
                            // rapids ceiling the gradient path uses.
                            fl = authored * Mathf.Clamp(grade, 0.045f, 0.35f);
                        }

                        float mag = fl.magnitude;
                        if (mag > 0.35f) fl *= 0.35f / mag;
                        fl *= Mathf.Clamp01(s_wCornDx[gi] / 0.5f);
                    }
                    s_wCornF[gi] = fl;
                }

            // FLOW COHERENCE — how aligned each corner's flow is with its 4
            // neighbours (mean dot of unit directions). ~1 in a straight
            // channel; drops toward 0 where rivers MEET — a flow-field
            // singularity the directional flow-map can't resolve and tears into
            // a radial "star" smear. Baked here (pure world data, cross-chunk
            // continuous) and handed to the shader in UV1.z, which fades the
            // directional map back to the calm ripple at the confluence.
            if (s_wCornC == null || s_wCornC.Length < R * R) s_wCornC = new float[R * R];
            for (int cz = FLO + 1; cz <= FHI - 1; cz++)
                for (int cx = FLO + 1; cx <= FHI - 1; cx++)
                {
                    int gi = (cx + RING) + R * (cz + RING);
                    Vector2 f = s_wCornF[gi];
                    float m = f.magnitude;
                    if (m < 1e-4f) { s_wCornC[gi] = 1f; continue; }   // still: coherence moot
                    Vector2 nf = f / m;
                    float sum = 0f; int cnt = 0;
                    // All four neighbours ALWAYS exist now — flow is defined one
                    // corner further out than this loop reaches. No edge case, so
                    // no edge answer that differs from the neighbouring chunk's.
                    for (int k = 0; k < 4; k++)
                    {
                        Vector2 g = s_wCornF[gi + (k == 0 ? -1 : k == 1 ? 1 : k == 2 ? -R : R)];
                        float gm = g.magnitude;
                        if (gm < 1e-4f) continue;
                        sum += Vector2.Dot(nf, g / gm);
                        cnt++;
                    }
                    s_wCornC[gi] = cnt > 0 ? Mathf.Clamp01(sum / cnt) : 1f;
                }

            // VIEW CONVENTION (GetOrCreateView): the view GameObject sits at
            // (0,0,0) and every mesh on it is WORLD-space — terrain verts leave
            // the mesher with WorldOrigin already added. The first sheet code
            // emitted LOCAL verts against that zero transform, which piled
            // every sheet in the world at the MAP ORIGIN ("nearest sheet 2668m
            // away" was the player's distance from (0,0)). The refactor then
            // read transform.position.y (= 0) for the ownership band, which
            // rejected every level ≥ 32 — zero sheets anywhere. World-space,
            // like the terrain, from the chunk origin carried in the buffers.
            float oy = buf.OriginY;
            s_wVerts.Clear(); s_wNorms.Clear(); s_wUvs.Clear(); s_wTris.Clear(); s_wCols.Clear(); s_wUv1.Clear(); s_wUv2.Clear();
            s_iVerts.Clear(); s_iNorms.Clear(); s_iUvs.Clear(); s_iTris.Clear();
            const float UVS = 1f / 64f;
            for (int z = 0; z < C; z++)
            {
                for (int x = 0; x < C; x++)
                {
                    float l00 = lvls[(x + 1) + S * (z + 1)];
                    float l10 = lvls[(x + 2) + S * (z + 1)];
                    float l01 = lvls[(x + 1) + S * (z + 2)];
                    float l11 = lvls[(x + 2) + S * (z + 2)];
                    float mx = Mathf.Max(Mathf.Max(l00, l10), Mathf.Max(l01, l11));
                    if (mx <= 0f) continue;   // coverage = dilated store mask (lip/cave guards intact)
                    // Heights from the smooth corner grid; a cell fully past the
                    // field's coverage (deep-bank skirt) keeps a flat lid at the
                    // dilated level — buried, the depth buffer clips it.
                    int gi0 = (x + RING) + R * (z + RING);
                    // s_wCornHx, not s_wCornH: dry corners were already resolved
                    // once, for every quad that touches them, by the extension
                    // pass above.
                    float h00 = s_wCornHx[gi0], h10 = s_wCornHx[gi0 + 1];
                    float h01 = s_wCornHx[gi0 + R], h11 = s_wCornHx[gi0 + R + 1];
                    float hmx = Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h01, h11));
                    if (hmx <= 0f) hmx = mx;
                    // ONE owner per column stack: the chunk whose Y band holds
                    // the sheet. Smooth heights are pure world data, so every
                    // stacked chunk computes the same owner — no double sheets,
                    // no gaps.
                    if (hmx < oy || hmx >= oy + C) continue;
                    // No per-quad guessing left. The only corners still at 0 here
                    // are further from water than the extension reaches — the
                    // deep-bank skirt, which the terrain buries anyway — and they
                    // take the cell's dilated level, the same value every quad in
                    // this cell sees.
                    float y00 = h00 > 0f ? h00 : hmx;
                    float y10 = h10 > 0f ? h10 : hmx;
                    float y01 = h01 > 0f ? h01 : hmx;
                    float y11 = h11 > 0f ? h11 : hmx;
                    float wx0 = buf.OriginX + x, wz0 = buf.OriginZ + z;
                    // ICE IS FLAT, PERIOD (user rule: the lake is leveled and
                    // frozen is a LOOK on the flat sheet — only hammer ice
                    // rides terrain). The 8m class lookup bleeds FROZEN onto
                    // neighbouring sloped CHANNEL columns (a stream running
                    // past the lake) and those quads rendered as giant ice
                    // ramps climbing the walls. Flatness is judged on the
                    // STORE columns only: the dilated/curled skirt corners
                    // vary on EVERY rim cell, so the old dilated-corner test
                    // rejected the whole edge ring and rendered a liquid
                    // staircase around each ice disc that never reached the
                    // banks (user screenshot). Store-flat cells — and the
                    // pure-skirt fringe past the last store column — snap
                    // every corner to ONE level, so the disc is coplanar,
                    // fills the bowl and clips into the banks; genuinely
                    // sloped channel cells still route to WATER.
                    // Class probe WIDENED ±4m: classes live on an 8m grid, and
                    // a frozen pond's rim quads often land in unclassified
                    // (or neighbouring-body) blocks — they routed to
                    // TRANSPARENT liquid and the snow-top sheet ended in
                    // square 8m cuts short of the banks (user screenshots).
                    // Any frozen block within 4m claims the quad; the
                    // store-flatness test below still rejects sloped channel
                    // bleed, so ice ramps cannot return.
                    bool frozen = FiresWorldGenBase.WaterClassAt(wx0 + 0.5f, wz0 + 0.5f)
                                  == FiresWorldGenBase.WC_FROZEN
                               || FiresWorldGenBase.WaterClassAt(wx0 + 4.5f, wz0 + 0.5f)
                                  == FiresWorldGenBase.WC_FROZEN
                               || FiresWorldGenBase.WaterClassAt(wx0 - 3.5f, wz0 + 0.5f)
                                  == FiresWorldGenBase.WC_FROZEN
                               || FiresWorldGenBase.WaterClassAt(wx0 + 0.5f, wz0 + 4.5f)
                                  == FiresWorldGenBase.WC_FROZEN
                               || FiresWorldGenBase.WaterClassAt(wx0 + 0.5f, wz0 - 3.5f)
                                  == FiresWorldGenBase.WC_FROZEN
                               || FiresWorldGenBase.SeasonalFrozenAt(wx0 + 0.5f, wz0 + 0.5f);
                    if (frozen)
                    {
                        float sw00 = buf.WaterLvl[(x + 1) + S * (z + 1)];
                        float sw10 = buf.WaterLvl[(x + 2) + S * (z + 1)];
                        float sw01 = buf.WaterLvl[(x + 1) + S * (z + 2)];
                        float sw11 = buf.WaterLvl[(x + 2) + S * (z + 2)];
                        float wmax = Mathf.Max(Mathf.Max(sw00, sw10), Mathf.Max(sw01, sw11));
                        float wmin = float.MaxValue;
                        if (sw00 > 0f && sw00 < wmin) wmin = sw00;
                        if (sw10 > 0f && sw10 < wmin) wmin = sw10;
                        if (sw01 > 0f && sw01 < wmin) wmin = sw01;
                        if (sw11 > 0f && sw11 < wmin) wmin = sw11;
                        frozen = wmax <= 0f || wmax - wmin <= 0.3f;
                        if (frozen)
                        {
                            float snap = wmax > 0f ? wmax : mx;
                            y00 = snap; y10 = snap; y01 = snap; y11 = snap;
                        }
                    }
                    var vl = frozen ? s_iVerts : s_wVerts;
                    var nl = frozen ? s_iNorms : s_wNorms;
                    var ul = frozen ? s_iUvs : s_wUvs;
                    var tl = frozen ? s_iTris : s_wTris;
                    int b = vl.Count;
                    vl.Add(new Vector3(wx0, y00, wz0));
                    vl.Add(new Vector3(wx0 + 1f, y10, wz0));
                    vl.Add(new Vector3(wx0, y01, wz0 + 1f));
                    vl.Add(new Vector3(wx0 + 1f, y11, wz0 + 1f));
                    ul.Add(new Vector2(wx0 * UVS, wz0 * UVS));
                    ul.Add(new Vector2((wx0 + 1f) * UVS, wz0 * UVS));
                    ul.Add(new Vector2(wx0 * UVS, (wz0 + 1f) * UVS));
                    ul.Add(new Vector2((wx0 + 1f) * UVS, (wz0 + 1f) * UVS));
                    nl.Add(Vector3.up); nl.Add(Vector3.up);
                    nl.Add(Vector3.up); nl.Add(Vector3.up);
                    if (!frozen)
                    {
                        // Explicit FULL alpha: the pour material's grid mode
                        // clips by vertex alpha, and a mesh with no colour
                        // stream leaves that to a default we don't control.
                        var wc = new Color32(255, 255, 255, 255);
                        s_wCols.Add(wc); s_wCols.Add(wc); s_wCols.Add(wc); s_wCols.Add(wc);
                        // UV1 downhill flow (.xy) + coherence (.z), corner order as the verts.
                        s_wUv1.Add(new Vector4(s_wCornF[gi0].x, s_wCornF[gi0].y, s_wCornC[gi0], 0f));
                        s_wUv1.Add(new Vector4(s_wCornF[gi0 + 1].x, s_wCornF[gi0 + 1].y, s_wCornC[gi0 + 1], 0f));
                        s_wUv1.Add(new Vector4(s_wCornF[gi0 + R].x, s_wCornF[gi0 + R].y, s_wCornC[gi0 + R], 0f));
                        s_wUv1.Add(new Vector4(s_wCornF[gi0 + R + 1].x, s_wCornF[gi0 + R + 1].y, s_wCornC[gi0 + R + 1], 0f));
                        // UV2.x — the TRUE water column (surface Y − bed Y) this corner
                        // stands in. Custom/FiresInlandWater keys its opacity, shore
                        // fade and bank collar off this instead of reconstructing depth
                        // from the buffer, which is view-dependent and breaks at grazing
                        // angles across an estuary. s_wCornD is the same smooth-field
                        // depth the corner heights came from, so the two cannot disagree.
                        s_wUv2.Add(new Vector4(Mathf.Max(0f, s_wCornDx[gi0]), 0f, 0f, 0f));
                        s_wUv2.Add(new Vector4(Mathf.Max(0f, s_wCornDx[gi0 + 1]), 0f, 0f, 0f));
                        s_wUv2.Add(new Vector4(Mathf.Max(0f, s_wCornDx[gi0 + R]), 0f, 0f, 0f));
                        s_wUv2.Add(new Vector4(Mathf.Max(0f, s_wCornDx[gi0 + R + 1]), 0f, 0f, 0f));
                    }
                    tl.Add(b); tl.Add(b + 2); tl.Add(b + 1);
                    tl.Add(b + 1); tl.Add(b + 2); tl.Add(b + 3);
                }
            }
        }

        // FROZEN sheet — same quads, ICE material, WALKABLE collider. The GO
        // name "IceShell" is load-bearing: IceSlidingPatches detects custom
        // ice by ground-collider name, so generated frozen lakes slide like
        // the Place Ice pieces with zero extra wiring. Swim skips frozen
        // columns (VoxelWaterSurface), so you walk this surface, not swim it.
        private void ApplyIceSheet()
        {
            if (s_iTris.Count == 0)
            {
                if (_iceGO != null) _iceGO.SetActive(false);
                return;
            }
            if (_iceGO == null)
            {
                _iceGO = new GameObject("IceShell");
                _iceGO.transform.SetParent(transform, false);
                var mf = _iceGO.AddComponent<MeshFilter>();
                _iceRenderer = _iceGO.AddComponent<MeshRenderer>();
                _iceRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _iceRenderer.receiveShadows = false;
                _iceCollider = _iceGO.AddComponent<MeshCollider>();
                _iceMesh = new Mesh { name = "genice" };
                mf.sharedMesh = _iceMesh;
            }
            if (_iceRenderer != null && _iceRenderer.sharedMaterial == null)
            {
                var mat = VerdantsAscent.Water.Core.WaterMaterialManager.GetMaterial(
                    VerdantsAscent.Water.Core.CustomLiquidType.Ice);
                if (mat != null)
                {
                    _iceRenderer.sharedMaterial = mat;
                    if (!s_iFirstSheet)
                    {
                        s_iFirstSheet = true;
                        FiresLogger.LogInfo($"[FiresVoxel] first ICE sheet: mat='{mat.name}' shader='{(mat.shader != null ? mat.shader.name : "?")}'.");
                    }
                }
                else if (!s_iMatWarned)
                {
                    s_iMatWarned = true;
                    FiresLogger.LogWarning("[FiresVoxel] ice material not resolvable yet — frozen sheets retry per apply.");
                }
            }
            _iceMesh.Clear();
            _iceMesh.SetVertices(s_iVerts);
            _iceMesh.SetNormals(s_iNorms);
            _iceMesh.SetUVs(0, s_iUvs);
            _iceMesh.SetTriangles(s_iTris, 0);
            _iceMesh.RecalculateBounds();
            if (_iceCollider != null)
            {
                _iceCollider.sharedMesh = null;
                _iceCollider.sharedMesh = _iceMesh;
            }
            _iceGO.SetActive(true);
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_waterMesh != null) Destroy(_waterMesh);
            if (_fallsMesh != null) Destroy(_fallsMesh);
            if (_iceMesh != null) Destroy(_iceMesh);
        }
    }
}
