using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Creates a thin water material overlay above the lava surface mesh.
    /// 
    /// The lava material viewed through the vanilla water shader produces a
    /// phenomenal molten-glass look - the water's refraction and transparency
    /// tints and distorts the bright lava underneath. This controller duplicates
    /// the combiner's water mesh vertices, shifts them up by a small offset,
    /// and renders them with the vanilla water material on the "Water" layer
    /// so the engine's water rendering pipeline (depth, waves, refraction) is
    /// applied on top of the opaque lava surface.
    /// </summary>
    public class LavaWaterOverlayController : MonoBehaviour
    {
        /// <summary>
        /// Height of the water overlay above the lava surface (meters).
        ///
        /// 2026-05-25 round-4: TIGHTENED from 0.25f → 0.002f per user.
        /// At 0.25m there was a visible pinkish-orange glow at the lava
        /// edges where the wider water-overlay mesh extended past the
        /// lava and over surrounding terrain. At 0.002m the overlay is
        /// effectively co-planar with the lava — the water shader's
        /// refraction still tints + warps the lava beneath for the
        /// molten-glass effect, but there's no visible edge gap.
        /// </summary>
        private const float OVERLAY_HEIGHT = 0.002f;

        private GameObject _overlayGO;
        private MeshFilter _overlayMeshFilter;
        private MeshRenderer _overlayRenderer;
        private Mesh _overlayMesh;
        private Material _waterMaterial;

        /// <summary>
        /// Initialises the overlay. Call once after the combiner's water mesh
        /// has been built at least once.
        /// </summary>
        public void Initialize()
        {
            _waterMaterial = WaterMaterialManager.GetMaterial(CustomLiquidType.Water);
            CreateOverlayGameObject();
        }

        /// <summary>
        /// Rebuilds the overlay mesh by copying the source mesh vertices and
        /// shifting them up by <see cref="OVERLAY_HEIGHT"/>.
        /// Call this after every mesh rebuild on the parent combiner.
        /// </summary>
        public void RebuildOverlay(Mesh sourceMesh)
        {
            if (sourceMesh == null || sourceMesh.vertexCount == 0)
            {
                if (_overlayMesh != null) _overlayMesh.Clear();
                return;
            }

            if (_overlayMesh == null)
            {
                _overlayMesh = new Mesh { name = "LavaWaterOverlay" };
                if (_overlayMeshFilter != null)
                    _overlayMeshFilter.sharedMesh = _overlayMesh;
            }

            // Copy all mesh data
            var verts = sourceMesh.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                verts[i].y += OVERLAY_HEIGHT;
            }

            _overlayMesh.Clear();
            _overlayMesh.vertices = verts;

            // Copy triangles per sub-mesh
            _overlayMesh.subMeshCount = sourceMesh.subMeshCount;
            for (int s = 0; s < sourceMesh.subMeshCount; s++)
            {
                _overlayMesh.SetTriangles(sourceMesh.GetTriangles(s), s);
            }

            // Copy UVs and normals
            _overlayMesh.uv = sourceMesh.uv;
            _overlayMesh.normals = sourceMesh.normals;
            if (sourceMesh.colors != null && sourceMesh.colors.Length > 0)
                _overlayMesh.colors = sourceMesh.colors;

            _overlayMesh.RecalculateBounds();
        }

        /// <summary>
        /// Cleans up the overlay child objects and mesh.
        /// </summary>
        public void Cleanup()
        {
            if (_overlayGO != null)
            {
                if (_overlayMeshFilter != null)
                    _overlayMeshFilter.sharedMesh = null;
                Destroy(_overlayGO);
                _overlayGO = null;
            }
            if (_overlayMesh != null)
            {
                _overlayMesh.Clear();
                Destroy(_overlayMesh);
                _overlayMesh = null;
            }
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void CreateOverlayGameObject()
        {
            if (_overlayGO != null) return;

            _overlayGO = new GameObject("LavaWaterOverlay");
            _overlayGO.transform.SetParent(transform);
            _overlayGO.transform.localPosition = Vector3.zero;
            _overlayGO.transform.localRotation = Quaternion.identity;
            _overlayGO.transform.localScale = Vector3.one;
            _overlayGO.layer = LayerMask.NameToLayer("Water");

            _overlayMeshFilter = _overlayGO.AddComponent<MeshFilter>();
            _overlayRenderer = _overlayGO.AddComponent<MeshRenderer>();

            if (_waterMaterial != null)
                _overlayRenderer.material = _waterMaterial;

            // Start with an empty mesh - RebuildOverlay will populate it
            _overlayMesh = new Mesh { name = "LavaWaterOverlay" };
            _overlayMeshFilter.sharedMesh = _overlayMesh;
        }
    }
}
