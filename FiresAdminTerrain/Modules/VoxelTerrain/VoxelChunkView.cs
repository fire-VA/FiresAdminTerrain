using UnityEngine;
using VerdantsAscent.Terrain.Utilities;

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
            if (_renderer != null) _renderer.enabled = VertexCount > 0 && !suppressed;
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
        public int ApplyBuffers(MeshBuffers buf)
        {
            VertexCount = buf.Verts.Count;
            TriangleCount = buf.Tris.Count / 3;

            if (VertexCount == 0)
            {
                _mesh.Clear(false);
                if (_renderer != null) _renderer.enabled = false;
                _collider.sharedMesh = null;
                ColliderReady = true;   // nothing to collide with — ready by definition
                return 0;
            }
            ColliderReady = false;

            _mesh.Clear(false);
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
                _mesh.SetUVs(0, buf.Uvs);
                _mesh.SetColors(buf.Colors);
            }
            _mesh.SetTriangles(buf.Tris, 0, calculateBounds: true);
            if (_renderer != null)
            {
                _renderer.enabled = !RenderSuppressed;
                // Self-heal: if the vanilla heightmap material captured AFTER this
                // view spawned (Standard fallback), pick it up on the next remesh.
                var mat = TerrainMaterialCache.GetCurrentMaterial();
                if (_renderer.sharedMaterial != mat) _renderer.sharedMaterial = mat;
            }
            return _mesh.GetInstanceID();
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
            _collider.sharedMesh = null;
            _collider.sharedMesh = _mesh;
            ColliderReady = true;
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
