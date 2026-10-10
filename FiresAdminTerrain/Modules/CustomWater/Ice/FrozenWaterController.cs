using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Interactable climb point on the edge of an ice hole.
    /// Implements Hoverable + Interactable so the player sees "[E] Climb"
    /// and gets pulled up onto the ice surface from the water below.
    /// 
    /// Based on Seasons mod's IceFloeClimb approach:
    /// - Uses Vector3.Lerp + Physics.SyncTransforms() for smooth pull-up
    /// - Only shows hover text when the player is swimming below the ice edge
    /// - Each ice hole gets one of these on its parent wall container
    /// </summary>
    public class IceHoleClimbPoint : MonoBehaviour, Hoverable, Interactable
    {
        private float _iceSurfaceY;
        private Vector3 _holeCenter;
        private float _holeRadius;

        /// <summary>
        /// Maximum horizontal distance from the hole edge where the climb prompt appears.
        /// </summary>
        private const float USE_DISTANCE = 3f;

        public void Setup(float iceSurfaceY, Vector3 holeCenter, float holeRadius)
        {
            _iceSurfaceY = iceSurfaceY;
            _holeCenter = holeCenter;
            _holeRadius = holeRadius;
        }

        public string GetHoverText()
        {
            if (Player.m_localPlayer == null) return "";
            if (!InUseDistance(Player.m_localPlayer)) return "";
            return Localization.instance.Localize("[<color=yellow><b>$KEY_Use</b></color>] Climb");
        }

        public float GetHoverOffset() => 0f;

        public string GetHoverName()
        {
            return "";
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            if (!InUseDistance(user)) return false;

            // Find the nearest ice edge point from the player's XZ position,
            // then lerp them toward the ice surface just past the edge.
            Vector3 playerPos = user.transform.position;
            Vector3 toCenter = new Vector3(_holeCenter.x - playerPos.x, 0f, _holeCenter.z - playerPos.z);
            float dist = toCenter.magnitude;

            Vector3 climbDir;
            if (dist > 0.01f)
            {
                // Climb away from the hole center (outward onto the ice)
                climbDir = -toCenter.normalized;
            }
            else
            {
                // At dead center - use their forward direction
                climbDir = user.transform.forward;
                climbDir.y = 0f;
                climbDir.Normalize();
            }

            // Landing position: just past the hole edge on solid ice
            float climbDist = _holeRadius + 0.8f;
            Vector3 landingPos = new Vector3(
                _holeCenter.x + climbDir.x * climbDist,
                _iceSurfaceY + 0.1f,
                _holeCenter.z + climbDir.z * climbDist
            );

            // Seasons-style smooth pull-up: lerp toward the landing position
            // and nudge upward. Multiple presses pull the player closer.
            user.transform.position = Vector3.Lerp(playerPos, landingPos, 0.35f) + Vector3.up;
            Physics.SyncTransforms();

            return false;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            return false;
        }

        /// <summary>
        /// Checks if the character is in a valid position to climb:
        /// - Must be below the ice surface (swimming in the hole)
        /// - Must be within USE_DISTANCE of the hole perimeter
        /// </summary>
        private bool InUseDistance(Humanoid human)
        {
            // Must be below the ice surface to climb up
            if (_iceSurfaceY - human.transform.position.y < 0.5f)
                return false;

            // Check horizontal distance from the hole perimeter
            Vector3 offset = human.transform.position - _holeCenter;
            offset.y = 0f;
            float distFromCenter = offset.magnitude;

            // Must be near the hole perimeter (between inner edge and outer reach)
            return distFromCenter < _holeRadius + USE_DISTANCE;
        }
    }

    /// <summary>
    /// Controls the frozen ice surface on top of a <see cref="WaterMeshCombiner"/>.
    /// 
    /// When a water body is set to <see cref="CustomLiquidType.Ice"/>, this component
    /// is attached to create a solid, walkable ice surface above the water mesh.
    /// 
    /// KEY ARCHITECTURE:
    /// - The underlying <see cref="WaterMeshCombiner"/> mesh is hidden (waves disabled).
    /// - A separate ice mesh + MeshCollider is generated at the water surface level.
    /// - The MeshCollider allows players/creatures to walk on the ice.
    /// - Ice holes can be punched to reveal water beneath (future: breaking mechanics).
    /// 
    /// LIFECYCLE:
    /// - Created by <see cref="WaterMeshCombiner.SetCustomLiquidType"/> when type is set to Ice.
    /// - Destroyed when liquid type changes away from Ice, or when combiner is destroyed.
    /// 
    /// PERSISTENCE:
    /// - Ice state is persisted via the combiner's <see cref="CustomLiquidType"/> on the anchor ZDO.
    /// - Ice holes are NOT persisted (they reset on reload - holes refreeze).
    /// </summary>
    public class FrozenWaterController : MonoBehaviour
    {
        #region Constants

        /// <summary>
        /// Visual thickness of the ice slab (meters). The collider sits on top;
        /// the mesh extends this far downward for a translucent depth look.
        /// </summary>
        private const float ICE_THICKNESS = 0.15f;

        /// <summary>
        /// Offset above the resting water surface for the ice top face.
        /// Keeps the ice visually sitting ON the water, not submerged.
        /// </summary>
        private const float ICE_SURFACE_OFFSET = 0.05f;

        /// <summary>
        /// Maximum number of holes allowed per ice surface.
        /// Old holes are removed (refrozen) when this limit is reached.
        /// </summary>
        private const int MAX_HOLES = 20;

        /// <summary>
        /// Total height of the hole wall colliders (meters).
        /// Extends downward from below the ice surface to block swimming under the ice.
        /// The top of the wall sits below the ice surface so players on top can slide in.
        /// </summary>
        private const float HOLE_WALL_DEPTH = 0.5f;

        /// <summary>
        /// How far below the ice surface the top of the hole wall sits (meters).
        /// This gap allows players walking on the ice to slide/fall into holes naturally.
        /// </summary>
        private const float HOLE_WALL_TOP_INSET = 0.3f;

        /// <summary>
        /// Thickness of the invisible wall colliders around each hole (meters).
        /// </summary>
        private const float HOLE_WALL_THICKNESS = 0.25f;

        /// <summary>
        /// Number of wall segments used to approximate the circular hole perimeter.
        /// More segments = rounder wall, but more colliders.
        /// </summary>
        private const int HOLE_WALL_SEGMENTS = 8;

        #endregion

        #region State

        private WaterMeshCombiner _combiner;

        // Water volume (inner liquid visible through holes)
        private GameObject _waterGO;
        private MeshFilter _waterMeshFilter;
        private MeshRenderer _waterRenderer;
        private Mesh _waterMesh;

        // Ice shell (outer frozen casing)
        private GameObject _iceGO;
        private MeshFilter _iceMeshFilter;
        private MeshRenderer _iceRenderer;
        private MeshCollider _iceCollider;
        private Mesh _iceMesh;
        private Mesh _colliderMesh;

        private List<GameObject> _holeWalls = new List<GameObject>();
        private List<GameObject> _edgeWalls = new List<GameObject>();
        private Material _iceMaterial;
        private Material _waterMaterial;

        private List<IceHole> _holes = new List<IceHole>();
        private bool _meshDirty = true;

        /// <summary>
        /// Tracks whether the ice surface is currently active.
        /// </summary>
        public bool IsFrozen { get; private set; }

        #endregion

        #region Nested Types

        /// <summary>
        /// Represents a circular hole punched in the ice.
        /// </summary>
        public struct IceHole
        {
            public Vector3 Center;
            public float Radius;
            public float TimeCreated;
        }

        #endregion

        #region Public API

        /// <summary>
        /// True when this controller is acting as a snow surface (set via
        /// the combiner's CustomLiquidType == Snow). Skips the under-ice
        /// swim walls, edge climb points, and hole-breaking — snow is a
        /// solid surface with no underwater interaction.
        /// </summary>
        public bool IsSnowMode { get; private set; }

        /// <summary>
        /// Initializes the ice controller for a water combiner.
        /// Reads combiner.CustomLiquidType to decide whether to set up as
        /// Ice (with under-ice walls + climb + breakable holes) or as Snow
        /// (solid surface, none of those extras).
        /// </summary>
        public void Initialize(WaterMeshCombiner combiner)
        {
            _combiner = combiner;
            IsSnowMode = combiner != null && combiner.CustomLiquidType == CustomLiquidType.Snow;

            // Pick the right top material — snow uses the snow material,
            // ice uses ice. Water material is only used for the inner
            // volume visible through ice holes; snow doesn't have holes
            // so the volume mesh stays hidden anyway.
            _iceMaterial = IsSnowMode
                ? WaterMaterialManager.GetMaterial(CustomLiquidType.Snow)
                : WaterMaterialManager.GetMaterial(CustomLiquidType.Ice);
            _waterMaterial = WaterMaterialManager.GetMaterial(CustomLiquidType.Water);

            CreateGameObjects();
            RebuildIceMesh();

            IsFrozen = true;

            FiresLogger.LogInfo($"[FrozenWaterController] Initialized {(IsSnowMode ? "SNOW" : "ICE")} surface at {transform.position}");
        }

        /// <summary>
        /// Removes the ice surface and restores normal water.
        /// Called when liquid type changes away from Ice.
        /// </summary>
        public void Thaw()
        {
            IsFrozen = false;
            _holes.Clear();
            DestroyHoleWalls();
            DestroyEdgeWalls();

            if (_waterGO != null) { Destroy(_waterGO); _waterGO = null; }
            if (_iceGO != null) { Destroy(_iceGO); _iceGO = null; }
            if (_waterMesh != null) { Destroy(_waterMesh); _waterMesh = null; }
            if (_iceMesh != null) { Destroy(_iceMesh); _iceMesh = null; }
            if (_colliderMesh != null) { Destroy(_colliderMesh); _colliderMesh = null; }

            _waterMeshFilter = null;
            _waterRenderer = null;
            _iceMeshFilter = null;
            _iceRenderer = null;
            _iceCollider = null;

            FiresLogger.LogInfo($"[FrozenWaterController] Thawed ice at {transform.position}");
        }

        /// <summary>
        /// Called from WaterMeshCombiner.RegenerateMesh once the underlying
        /// surface heights have been recomputed. Rebuilds the ice/snow mesh
        /// + collider from the new data. Without this hook the controller's
        /// initial Initialize() (which fires before the first RegenerateMesh)
        /// builds against empty data and the visual surface never appears.
        /// </summary>
        public void OnCombinerMeshUpdated()
        {
            if (!IsFrozen || _combiner == null) return;
            RebuildIceMesh();
        }

        /// <summary>
        /// Punches a hole in the ice at the given world position.
        /// </summary>
        /// <param name="worldPos">Center of the hole in world space</param>
        /// <param name="radius">Radius of the hole in meters</param>
        /// <returns>True if a hole was created</returns>
        public bool BreakIceAt(Vector3 worldPos, float radius)
        {
            if (!IsFrozen || _combiner == null) return false;
            // Snow can't be broken to reveal water — it's a solid surface, no
            // underlying volume. Silently ignore the call.
            if (IsSnowMode) return false;

            // Check if position is actually over this ice
            if (!_combiner.ContainsPosition(worldPos)) return false;

            // Enforce hole limit - remove oldest hole (refreezes)
            if (_holes.Count >= MAX_HOLES)
            {
                _holes.RemoveAt(0);
            }

            _holes.Add(new IceHole
            {
                Center = worldPos,
                Radius = Mathf.Max(0.5f, radius),
                TimeCreated = Time.time
            });

            _meshDirty = true;
            RebuildIceMesh();

            FiresLogger.LogInfo($"[FrozenWaterController] Broke ice at {worldPos}, radius={radius:F1}m ({_holes.Count} holes)");
            return true;
        }

        /// <summary>
        /// Gets the number of holes in the ice.
        /// </summary>
        public int HoleCount => _holes.Count;

        /// <summary>
        /// Gets read-only access to the holes list.
        /// </summary>
        public IReadOnlyList<IceHole> Holes => _holes;

        #endregion

        #region Lifecycle

        private void OnDestroy()
        {
            DestroyHoleWalls();
            DestroyEdgeWalls();

            if (_waterGO != null) Destroy(_waterGO);
            if (_iceGO != null) Destroy(_iceGO);
            if (_waterMesh != null) Destroy(_waterMesh);
            if (_iceMesh != null) Destroy(_iceMesh);
            if (_colliderMesh != null) Destroy(_colliderMesh);
        }

        #endregion

        #region Mesh Generation

        /// <summary>
        /// Creates both the inner water volume and outer ice shell GameObjects.
        /// </summary>
        private void CreateGameObjects()
        {
            // --- Water volume (inner liquid, visible through holes) ---
            // Snow has no underwater layer (user 2026-05-25: "no water
            // effects, just the normal mountain snow effects") — skip
            // creating it entirely so nothing renders below the snow surface.
            if (_waterGO != null) Destroy(_waterGO);
            if (!IsSnowMode)
            {
                _waterGO = new GameObject("IceWaterVolume");
                _waterGO.transform.SetParent(transform);
                _waterGO.transform.localPosition = Vector3.zero;
                _waterGO.transform.localRotation = Quaternion.identity;
                _waterGO.transform.localScale = Vector3.one;
                _waterGO.layer = LayerMask.NameToLayer("Water");

                _waterMesh = new Mesh { name = "IceWaterVolumeMesh" };
                _waterMeshFilter = _waterGO.AddComponent<MeshFilter>();
                _waterMeshFilter.sharedMesh = _waterMesh;
                _waterRenderer = _waterGO.AddComponent<MeshRenderer>();
                if (_waterMaterial != null)
                    _waterRenderer.material = _waterMaterial;
            }

            // --- Ice/Snow shell (outer casing) ---
            if (_iceGO != null) Destroy(_iceGO);

            _iceGO = new GameObject(IsSnowMode ? "SnowShell" : "IceShell");
            _iceGO.transform.SetParent(transform);
            _iceGO.transform.localPosition = Vector3.zero;
            _iceGO.transform.localRotation = Quaternion.identity;
            _iceGO.transform.localScale = Vector3.one;
            // Both Ice and Snow live on "Default" layer. Earlier round-5
            // tried "piece" for ice on the theory that built-structure layer
            // gave better walk-on detection, but it actually broke collision
            // entirely (user 2026-05-25: "the ice is drawing the ice on top
            // now but not adding the collision to that mesh"). Default
            // collides cleanly with the character controller and works fine
            // with the slippery PhysicsMaterial.
            _iceGO.layer = LayerMask.NameToLayer("Default");

            _iceMesh = new Mesh { name = IsSnowMode ? "SnowShellMesh" : "IceShellMesh" };
            _iceMeshFilter = _iceGO.AddComponent<MeshFilter>();
            _iceMeshFilter.sharedMesh = _iceMesh;
            _iceRenderer = _iceGO.AddComponent<MeshRenderer>();
            if (_iceMaterial != null)
                _iceRenderer.material = _iceMaterial;

            // 2026-05-25 round-6: snow has NO collider. User wants "just snow
            // with no collision and no water effects (just the normal
            // mountain snow effects)" — a visual decal, not a walkable
            // surface. Skip the MeshCollider entirely so the player walks
            // on the underlying terrain.
            if (!IsSnowMode)
            {
                _iceCollider = _iceGO.AddComponent<MeshCollider>();
                _iceCollider.convex = false;
                // Slippery physics material so the player slides on ice
                // instead of sticking. User explicitly wanted "we can run
                // along it and slide" 2026-05-25.
                _iceCollider.sharedMaterial = GetOrCreateIcePhysicsMaterial();
            }
        }

        /// <summary>
        /// Lazy-init cache for the slippery ice physic material. One shared
        /// instance across all ice surfaces — they're all the same physical
        /// behaviour (very low friction, no bounce).
        /// </summary>
        private static PhysicsMaterial s_icePhysicsMaterial;

        private static PhysicsMaterial GetOrCreateIcePhysicsMaterial()
        {
            if (s_icePhysicsMaterial != null) return s_icePhysicsMaterial;

            s_icePhysicsMaterial = new PhysicsMaterial("IceSlippery")
            {
                dynamicFriction = 0.02f,
                staticFriction = 0.02f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Minimum,
            };
            return s_icePhysicsMaterial;
        }

        /// <summary>
        /// Rebuilds both meshes from the combiner's surface data.
        /// 
        /// ARCHITECTURE: Two separate meshes on two separate GameObjects:
        /// 
        /// 1. WATER VOLUME - A closed box (top + 4 sides + bottom) using the
        ///    water material. Always complete, never has holes. Sits slightly
        ///    inset from the ice shell so it's only visible through ice holes.
        /// 
        /// 2. ICE SHELL - A closed box (top + 4 sides + bottom) using the ice
        ///    material. The top face has circular cutouts where holes are punched.
        ///    Sides and bottom are always intact so the ice looks like a solid
        ///    block. The MeshCollider only covers the ice top (not holes) so
        ///    players fall through into the water.
        /// 
        /// The water volume top sits at (iceY - ICE_THICKNESS) so when looking
        /// down through a hole you see water, not air. The ice shell top sits
        /// at iceY.
        /// </summary>
        private void RebuildIceMesh()
        {
            if (_combiner == null) return;

            var surfaceHeights = _combiner.GetSurfaceHeights();
            if (surfaceHeights == null || surfaceHeights.Length == 0) return;

            int gridW = _combiner.GridWidth;
            int gridH = _combiner.GridHeight;
            float cellSize = _combiner.CellSize;
            Vector3 gridOrigin = _combiner.GetGridOrigin();

            if (gridW <= 0 || gridH <= 0) return;

            float iceY = _combiner.SettledWaterLevel + ICE_SURFACE_OFFSET;
            float bottomY = iceY - ICE_THICKNESS;

            // --- Classify grid cells ---
            var hasWater = new bool[gridW * gridH];
            var isHole = new bool[gridW * gridH];

            for (int z = 0; z < gridH; z++)
            {
                for (int x = 0; x < gridW; x++)
                {
                    int idx = z * gridW + x;
                    if (surfaceHeights[idx] < -50f) continue;
                    hasWater[idx] = true;

                    Vector3 worldPos = gridOrigin + new Vector3(x * cellSize, 0, z * cellSize);
                    for (int h = 0; h < _holes.Count; h++)
                    {
                        float dx = worldPos.x - _holes[h].Center.x;
                        float dz = worldPos.z - _holes[h].Center.z;
                        if (dx * dx + dz * dz <= _holes[h].Radius * _holes[h].Radius)
                        {
                            isHole[idx] = true;
                            break;
                        }
                    }
                }
            }

            // --- Determine which cells form valid quads ---
            var isValidQuadCorner = new bool[gridW * gridH];
            for (int z = 0; z < gridH; z++)
            {
                for (int x = 0; x < gridW; x++)
                {
                    int idx = z * gridW + x;
                    if (!hasWater[idx]) continue;

                    for (int dz = -1; dz <= 0; dz++)
                    {
                        for (int dx = -1; dx <= 0; dx++)
                        {
                            int qx = x + dx, qz = z + dz;
                            if (qx < 0 || qz < 0 || qx >= gridW - 1 || qz >= gridH - 1) continue;
                            if (hasWater[qz * gridW + qx] &&
                                hasWater[qz * gridW + qx + 1] &&
                                hasWater[(qz + 1) * gridW + qx] &&
                                hasWater[(qz + 1) * gridW + qx + 1])
                            {
                                isValidQuadCorner[idx] = true;
                                goto nextCell;
                            }
                        }
                    }
                nextCell:;
                }
            }

            // --- Detect edge cells (have at least one non-water neighbor) ---
            var isEdge = new bool[gridW * gridH];
            for (int z = 0; z < gridH; z++)
            {
                for (int x = 0; x < gridW; x++)
                {
                    int idx = z * gridW + x;
                    if (!isValidQuadCorner[idx]) continue;

                    if (x == 0 || x == gridW - 1 || z == 0 || z == gridH - 1)
                    {
                        isEdge[idx] = true;
                        continue;
                    }
                    // Check 4 neighbors
                    if (!hasWater[(z) * gridW + (x - 1)] ||
                        !hasWater[(z) * gridW + (x + 1)] ||
                        !hasWater[(z - 1) * gridW + (x)] ||
                        !hasWater[(z + 1) * gridW + (x)])
                    {
                        isEdge[idx] = true;
                    }
                }
            }

            // --- Build water volume mesh (complete box, no holes) ---
            // Snow skips the water volume entirely (no underwater layer to
            // see through holes anyway; no holes are allowed in snow).
            if (!IsSnowMode && _waterMesh != null)
            {
                BuildBoxMesh(_waterMesh, gridW, gridH, cellSize, gridOrigin,
                             hasWater, isValidQuadCorner, isEdge,
                             holeArray: null, // no holes - water is always complete
                             topY: bottomY + 0.01f, // water top just below ice bottom
                             botY: bottomY - ICE_THICKNESS);

                if (_waterRenderer != null && _waterMaterial != null)
                    _waterRenderer.material = _waterMaterial;
            }

            // --- Build ice shell mesh (box with holes cut from top) ---
            BuildBoxMesh(_iceMesh, gridW, gridH, cellSize, gridOrigin,
                         hasWater, isValidQuadCorner, isEdge,
                         holeArray: isHole, // holes cut from top face only
                         topY: iceY,
                         botY: bottomY);

            if (_iceRenderer != null && _iceMaterial != null)
                _iceRenderer.material = _iceMaterial;

            // --- Collider: ice top only (not holes, not sides/bottom) ---
            // Snow has no collider — skip.
            if (!IsSnowMode)
            {
                RebuildColliderMesh(gridW, gridH, cellSize, gridOrigin,
                                   hasWater, isValidQuadCorner, isHole, iceY);
            }

            _meshDirty = false;

            // Snow has no under-ice swim or climb behaviour — skip the
            // perimeter walls + climb points entirely. The top mesh +
            // collider is the whole story for snow.
            if (!IsSnowMode)
            {
                RebuildHoleWalls(iceY);
                RebuildEdgeWalls(gridW, gridH, cellSize, gridOrigin, hasWater, isEdge, iceY);
            }
        }

        /// <summary>
        /// Builds a closed box mesh (top face + bottom face + side walls) from
        /// the water grid. The top face can optionally have circular hole cutouts.
        /// 
        /// The sides are generated along the outer edge of the water footprint
        /// as vertical quads connecting topY to botY.
        /// </summary>
        private void BuildBoxMesh(
            Mesh targetMesh,
            int gridW, int gridH, float cellSize, Vector3 gridOrigin,
            bool[] hasWater, bool[] isValidQuadCorner, bool[] isEdge,
            bool[] holeArray, float topY, float botY)
        {
            if (targetMesh == null) return;

            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();
            var norms = new List<Vector3>();

            Vector3 myPos = transform.position;
            float localTopY = topY - myPos.y;
            float localBotY = botY - myPos.y;

            // --- Build top and bottom vertices ---
            // We need separate vertices for top and bottom because normals differ.
            // topVertIdx[gridIdx] = vertex index for top face at that grid cell
            // botVertIdx[gridIdx] = vertex index for bottom face at that grid cell
            var topVertIdx = new int[gridW * gridH];
            var botVertIdx = new int[gridW * gridH];
            for (int i = 0; i < topVertIdx.Length; i++)
            {
                topVertIdx[i] = -1;
                botVertIdx[i] = -1;
            }

            int vi = 0;

            for (int z = 0; z < gridH; z++)
            {
                for (int x = 0; x < gridW; x++)
                {
                    int idx = z * gridW + x;
                    if (!isValidQuadCorner[idx]) continue;

                    Vector3 worldPos = gridOrigin + new Vector3(x * cellSize, 0, z * cellSize);
                    float lx = worldPos.x - myPos.x;
                    float lz = worldPos.z - myPos.z;
                    Vector2 uv = new Vector2(worldPos.x * 0.1f, worldPos.z * 0.1f);

                    // Top vertex
                    verts.Add(new Vector3(lx, localTopY, lz));
                    uvs.Add(uv);
                    norms.Add(Vector3.up);
                    topVertIdx[idx] = vi++;

                    // Bottom vertex
                    verts.Add(new Vector3(lx, localBotY, lz));
                    uvs.Add(uv);
                    norms.Add(Vector3.down);
                    botVertIdx[idx] = vi++;
                }
            }

            // --- Top face triangles (with optional hole cutouts) ---
            for (int z = 0; z < gridH - 1; z++)
            {
                for (int x = 0; x < gridW - 1; x++)
                {
                    int i0 = z * gridW + x;
                    int i1 = z * gridW + x + 1;
                    int i2 = (z + 1) * gridW + x;
                    int i3 = (z + 1) * gridW + x + 1;

                    if (!hasWater[i0] || !hasWater[i1] || !hasWater[i2] || !hasWater[i3])
                        continue;

                    int v0 = topVertIdx[i0], v1 = topVertIdx[i1];
                    int v2 = topVertIdx[i2], v3 = topVertIdx[i3];
                    if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0) continue;

                    // If holeArray is provided, skip quads where any corner is a hole
                    if (holeArray != null && (holeArray[i0] || holeArray[i1] || holeArray[i2] || holeArray[i3]))
                        continue;

                    // Top face (normal up) - CCW winding
                    tris.Add(v0); tris.Add(v2); tris.Add(v1);
                    tris.Add(v1); tris.Add(v2); tris.Add(v3);
                }
            }

            // --- Bottom face triangles (always complete, no holes) ---
            for (int z = 0; z < gridH - 1; z++)
            {
                for (int x = 0; x < gridW - 1; x++)
                {
                    int i0 = z * gridW + x;
                    int i1 = z * gridW + x + 1;
                    int i2 = (z + 1) * gridW + x;
                    int i3 = (z + 1) * gridW + x + 1;

                    if (!hasWater[i0] || !hasWater[i1] || !hasWater[i2] || !hasWater[i3])
                        continue;

                    int v0 = botVertIdx[i0], v1 = botVertIdx[i1];
                    int v2 = botVertIdx[i2], v3 = botVertIdx[i3];
                    if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0) continue;

                    // Bottom face (normal down) - reversed winding
                    tris.Add(v0); tris.Add(v1); tris.Add(v2);
                    tris.Add(v1); tris.Add(v3); tris.Add(v2);
                }
            }

            // --- Side walls along edges ---
            // For each edge cell, check its 4 neighbors. Where a neighbor is
            // empty (no water), emit a vertical quad on that side.
            int[] ddx = { 0, 0, -1, 1 };
            int[] ddz = { -1, 1, 0, 0 };

            for (int z = 0; z < gridH; z++)
            {
                for (int x = 0; x < gridW; x++)
                {
                    int idx = z * gridW + x;
                    if (!isEdge[idx]) continue;

                    Vector3 worldPos = gridOrigin + new Vector3(x * cellSize, 0, z * cellSize);
                    float lx = worldPos.x - myPos.x;
                    float lz = worldPos.z - myPos.z;

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + ddx[d];
                        int nz = z + ddz[d];

                        // Emit a wall if neighbor is outside grid or has no water
                        bool needsWall;
                        if (nx < 0 || nx >= gridW || nz < 0 || nz >= gridH)
                            needsWall = true;
                        else
                            needsWall = !hasWater[nz * gridW + nx];

                        if (!needsWall) continue;

                        // Determine the wall edge offset from this cell's position
                        // The wall sits at the boundary between this cell and the empty neighbor
                        float halfCell = cellSize * 0.5f;
                        float wx0, wz0, wx1, wz1;

                        if (d == 0) // -Z neighbor
                        {
                            wx0 = lx - halfCell; wz0 = lz - halfCell;
                            wx1 = lx + halfCell; wz1 = lz - halfCell;
                        }
                        else if (d == 1) // +Z neighbor
                        {
                            wx0 = lx + halfCell; wz0 = lz + halfCell;
                            wx1 = lx - halfCell; wz1 = lz + halfCell;
                        }
                        else if (d == 2) // -X neighbor
                        {
                            wx0 = lx - halfCell; wz0 = lz + halfCell;
                            wx1 = lx - halfCell; wz1 = lz - halfCell;
                        }
                        else // +X neighbor
                        {
                            wx0 = lx + halfCell; wz0 = lz - halfCell;
                            wx1 = lx + halfCell; wz1 = lz + halfCell;
                        }

                        // 4 vertices for a vertical quad
                        int sv0 = verts.Count;
                        Vector3 normal;
                        if (d == 0) normal = Vector3.back;
                        else if (d == 1) normal = Vector3.forward;
                        else if (d == 2) normal = Vector3.left;
                        else normal = Vector3.right;

                        verts.Add(new Vector3(wx0, localTopY, wz0));
                        verts.Add(new Vector3(wx1, localTopY, wz1));
                        verts.Add(new Vector3(wx0, localBotY, wz0));
                        verts.Add(new Vector3(wx1, localBotY, wz1));
                        uvs.Add(new Vector2(wx0 * 0.1f, localTopY * 0.1f));
                        uvs.Add(new Vector2(wx1 * 0.1f, localTopY * 0.1f));
                        uvs.Add(new Vector2(wx0 * 0.1f, localBotY * 0.1f));
                        uvs.Add(new Vector2(wx1 * 0.1f, localBotY * 0.1f));
                        norms.Add(normal); norms.Add(normal);
                        norms.Add(normal); norms.Add(normal);

                        // Two triangles for the quad
                        tris.Add(sv0); tris.Add(sv0 + 1); tris.Add(sv0 + 2);
                        tris.Add(sv0 + 1); tris.Add(sv0 + 3); tris.Add(sv0 + 2);
                    }
                }
            }

            // --- Apply to mesh ---
            targetMesh.Clear();
            if (verts.Count > 0 && tris.Count > 0)
            {
                targetMesh.SetVertices(verts);
                targetMesh.SetTriangles(tris, 0);
                targetMesh.SetUVs(0, uvs);
                targetMesh.SetNormals(norms);
                targetMesh.RecalculateBounds();
            }
        }

        /// <summary>
        /// Builds the collider mesh for the ice top face only (no holes, no sides/bottom).
        /// Players walk on this and fall through holes.
        /// </summary>
        private void RebuildColliderMesh(
            int gridW, int gridH, float cellSize, Vector3 gridOrigin,
            bool[] hasWater, bool[] isValidQuadCorner, bool[] isHole, float iceY)
        {
            if (_colliderMesh == null)
            {
                _colliderMesh = new Mesh { name = "IceColliderMesh" };
            }

            var verts = new List<Vector3>();
            var tris = new List<int>();
            var norms = new List<Vector3>();

            Vector3 myPos = transform.position;
            float localY = iceY - myPos.y;

            var vertIdx = new int[gridW * gridH];
            for (int i = 0; i < vertIdx.Length; i++) vertIdx[i] = -1;

            int vi = 0;
            for (int z = 0; z < gridH; z++)
            {
                for (int x = 0; x < gridW; x++)
                {
                    int idx = z * gridW + x;
                    if (!isValidQuadCorner[idx]) continue;

                    Vector3 worldPos = gridOrigin + new Vector3(x * cellSize, 0, z * cellSize);
                    verts.Add(new Vector3(worldPos.x - myPos.x, localY, worldPos.z - myPos.z));
                    norms.Add(Vector3.up);
                    vertIdx[idx] = vi++;
                }
            }

            for (int z = 0; z < gridH - 1; z++)
            {
                for (int x = 0; x < gridW - 1; x++)
                {
                    int i0 = z * gridW + x;
                    int i1 = z * gridW + x + 1;
                    int i2 = (z + 1) * gridW + x;
                    int i3 = (z + 1) * gridW + x + 1;

                    if (!hasWater[i0] || !hasWater[i1] || !hasWater[i2] || !hasWater[i3])
                        continue;

                    // Skip hole quads so players fall through
                    if (isHole[i0] || isHole[i1] || isHole[i2] || isHole[i3])
                        continue;

                    int v0 = vertIdx[i0], v1 = vertIdx[i1];
                    int v2 = vertIdx[i2], v3 = vertIdx[i3];
                    if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0) continue;

                    tris.Add(v0); tris.Add(v2); tris.Add(v1);
                    tris.Add(v1); tris.Add(v2); tris.Add(v3);
                }
            }

            _colliderMesh.Clear();
            if (verts.Count > 0 && tris.Count > 0)
            {
                _colliderMesh.SetVertices(verts);
                _colliderMesh.SetTriangles(tris, 0);
                _colliderMesh.SetNormals(norms);
                _colliderMesh.RecalculateBounds();
            }

            if (_iceCollider != null)
            {
                _iceCollider.sharedMesh = null;
                _iceCollider.sharedMesh = _colliderMesh;
            }
        }

        #endregion

        #region Hole Walls & Climb Points

        /// <summary>
        /// Destroys all existing hole wall GameObjects.
        /// </summary>
        private void DestroyHoleWalls()
        {
            for (int i = 0; i < _holeWalls.Count; i++)
            {
                if (_holeWalls[i] != null)
                    Destroy(_holeWalls[i]);
            }
            _holeWalls.Clear();
        }

        /// <summary>
        /// Creates invisible wall colliders around each ice hole to prevent
        /// swimming under the ice, and attaches a climb interaction so the
        /// player can climb out of the hole onto the ice surface.
        /// 
        /// Each hole gets a ring of box colliders approximating its circular
        /// perimeter, extending from the ice surface down into the water.
        /// The parent GO carries an <see cref="IceHoleClimbPoint"/> component
        /// that implements Hoverable + Interactable for the "Climb" prompt.
        /// </summary>
        private void RebuildHoleWalls(float iceY)
        {
            DestroyHoleWalls();

            if (_holes.Count == 0) return;

            for (int h = 0; h < _holes.Count; h++)
            {
                var hole = _holes[h];

                // Create a container for this hole's walls
                var wallContainer = new GameObject($"IceHoleWall_{h}");
                wallContainer.transform.SetParent(transform);
                wallContainer.transform.position = new Vector3(hole.Center.x, iceY, hole.Center.z);
                wallContainer.layer = LayerMask.NameToLayer("piece");

                // Attach the climb interaction to the container
                var climbPoint = wallContainer.AddComponent<IceHoleClimbPoint>();
                climbPoint.Setup(iceY, hole.Center, hole.Radius);

                // Create wall segments around the hole perimeter
                float angleStep = 360f / HOLE_WALL_SEGMENTS;
                float segmentArcLength = 2f * Mathf.PI * hole.Radius / HOLE_WALL_SEGMENTS;
                // Box width = chord length of one segment
                float chordLength = 2f * hole.Radius * Mathf.Sin(Mathf.PI / HOLE_WALL_SEGMENTS);

                for (int s = 0; s < HOLE_WALL_SEGMENTS; s++)
                {
                    float angleDeg = s * angleStep;
                    float angleRad = angleDeg * Mathf.Deg2Rad;
                    float midAngleRad = (angleDeg + angleStep * 0.5f) * Mathf.Deg2Rad;

                    // Position at the midpoint of this arc segment on the hole perimeter
                    float wx = hole.Center.x + Mathf.Cos(midAngleRad) * hole.Radius;
                    float wz = hole.Center.z + Mathf.Sin(midAngleRad) * hole.Radius;
                    // Top of wall sits below ice surface by HOLE_WALL_TOP_INSET so players can slide in
                    float wallTopY = iceY - HOLE_WALL_TOP_INSET;
                    float wy = wallTopY - HOLE_WALL_DEPTH * 0.5f;

                    var wallSegGO = new GameObject($"WallSeg_{s}");
                    wallSegGO.transform.SetParent(wallContainer.transform);
                    wallSegGO.transform.position = new Vector3(wx, wy, wz);

                    // Rotate so the box is tangent to the circle
                    float tangentAngle = midAngleRad + Mathf.PI * 0.5f;
                    wallSegGO.transform.rotation = Quaternion.Euler(0f, -tangentAngle * Mathf.Rad2Deg, 0f);

                    wallSegGO.layer = LayerMask.NameToLayer("piece");

                    var box = wallSegGO.AddComponent<BoxCollider>();
                    box.size = new Vector3(chordLength, HOLE_WALL_DEPTH, HOLE_WALL_THICKNESS);
                    box.center = Vector3.zero;
                }

                _holeWalls.Add(wallContainer);
            }
        }

        /// <summary>
        /// Destroys all existing edge wall GameObjects.
        /// </summary>
        private void DestroyEdgeWalls()
        {
            for (int i = 0; i < _edgeWalls.Count; i++)
            {
                if (_edgeWalls[i] != null)
                    Destroy(_edgeWalls[i]);
            }
            _edgeWalls.Clear();
        }

        /// <summary>
        /// Creates invisible wall colliders along the outer edge of the ice surface
        /// and attaches climb points so players in the water can climb up onto the ice.
        /// 
        /// Edge walls extend from below the ice surface downward, allowing players
        /// to approach the edge while swimming and use the climb interaction.
        /// </summary>
        private void RebuildEdgeWalls(int gridW, int gridH, float cellSize, Vector3 gridOrigin,
                                       bool[] hasWater, bool[] isEdge, float iceY)
        {
            DestroyEdgeWalls();

            // Collect edge segments and compute the ice center for climb direction
            var edgeSegments = new List<Vector4>(); // x0, z0, x1, z1
            float centerX = 0f, centerZ = 0f;
            int waterCount = 0;

            int[] ddx = { 0, 0, -1, 1 };
            int[] ddz = { -1, 1, 0, 0 };

            for (int z = 0; z < gridH; z++)
            {
                for (int x = 0; x < gridW; x++)
                {
                    int idx = z * gridW + x;
                    if (!hasWater[idx]) continue;

                    Vector3 worldPos = gridOrigin + new Vector3(x * cellSize, 0, z * cellSize);
                    centerX += worldPos.x;
                    centerZ += worldPos.z;
                    waterCount++;

                    if (!isEdge[idx]) continue;

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + ddx[d];
                        int nz = z + ddz[d];

                        bool needsWall;
                        if (nx < 0 || nx >= gridW || nz < 0 || nz >= gridH)
                            needsWall = true;
                        else
                            needsWall = !hasWater[nz * gridW + nx];

                        if (!needsWall) continue;

                        float halfCell = cellSize * 0.5f;
                        float wx0, wz0, wx1, wz1;

                        if (d == 0) { wx0 = worldPos.x - halfCell; wz0 = worldPos.z - halfCell; wx1 = worldPos.x + halfCell; wz1 = worldPos.z - halfCell; }
                        else if (d == 1) { wx0 = worldPos.x + halfCell; wz0 = worldPos.z + halfCell; wx1 = worldPos.x - halfCell; wz1 = worldPos.z + halfCell; }
                        else if (d == 2) { wx0 = worldPos.x - halfCell; wz0 = worldPos.z + halfCell; wx1 = worldPos.x - halfCell; wz1 = worldPos.z - halfCell; }
                        else { wx0 = worldPos.x + halfCell; wz0 = worldPos.z - halfCell; wx1 = worldPos.x + halfCell; wz1 = worldPos.z + halfCell; }

                        edgeSegments.Add(new Vector4(wx0, wz0, wx1, wz1));
                    }
                }
            }

            if (edgeSegments.Count == 0 || waterCount == 0) return;

            centerX /= waterCount;
            centerZ /= waterCount;

            // Compute approximate radius from center to edge for climb points
            float maxEdgeDist = 0f;
            foreach (var seg in edgeSegments)
            {
                float mx = (seg.x + seg.z) * 0.5f;
                float mz = (seg.y + seg.w) * 0.5f;
                float d = Mathf.Sqrt((mx - centerX) * (mx - centerX) + (mz - centerZ) * (mz - centerZ));
                if (d > maxEdgeDist) maxEdgeDist = d;
            }

            // Create a single container for all edge walls
            var edgeContainer = new GameObject("IceEdgeWalls");
            edgeContainer.transform.SetParent(transform);
            edgeContainer.transform.position = new Vector3(centerX, iceY, centerZ);
            edgeContainer.layer = LayerMask.NameToLayer("piece");

            // Attach climb point for the ice edge (players in water can climb up)
            var edgeClimb = edgeContainer.AddComponent<IceHoleClimbPoint>();
            // Setup as edge climb: iceSurfaceY, center of ice, and radius
            // InUseDistance will check if player is below ice and within range
            edgeClimb.Setup(iceY, new Vector3(centerX, 0f, centerZ), maxEdgeDist + 1f);

            // Create box colliders for each edge segment
            float edgeWallHeight = 1.5f;
            float edgeWallTopY = iceY - 0.05f; // Slightly below ice surface
            float edgeWallCenterY = edgeWallTopY - edgeWallHeight * 0.5f;

            for (int i = 0; i < edgeSegments.Count; i++)
            {
                var seg = edgeSegments[i];
                float mx = (seg.x + seg.z) * 0.5f;
                float mz = (seg.y + seg.w) * 0.5f;
                float segLen = Mathf.Sqrt((seg.z - seg.x) * (seg.z - seg.x) + (seg.w - seg.y) * (seg.w - seg.y));

                if (segLen < 0.01f) continue;

                var wallGO = new GameObject($"EdgeWall_{i}");
                wallGO.transform.SetParent(edgeContainer.transform);
                wallGO.transform.position = new Vector3(mx, edgeWallCenterY, mz);

                // Rotate to align with the segment
                float angle = Mathf.Atan2(seg.w - seg.y, seg.z - seg.x) * Mathf.Rad2Deg;
                wallGO.transform.rotation = Quaternion.Euler(0f, -angle, 0f);

                wallGO.layer = LayerMask.NameToLayer("piece");

                var box = wallGO.AddComponent<BoxCollider>();
                box.size = new Vector3(segLen, edgeWallHeight, HOLE_WALL_THICKNESS);
                box.center = Vector3.zero;
            }

            _edgeWalls.Add(edgeContainer);
        }

        #endregion
    }
}
