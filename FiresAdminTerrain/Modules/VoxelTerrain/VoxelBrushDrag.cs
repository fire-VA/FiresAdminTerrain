using UnityEngine;
using VerdantsAscent.Terrain.Lifecycle;
using VerdantsAscent.Terrain.Persistence;

namespace VerdantsAscent.Terrain.Voxel
{
    // Round-88 — voxel brush DRAG + 3D GIZMO tools (WaterBrushDrag pattern).
    //
    // Two tool families, gated by the selected hoe piece name (from
    // infinity_tools.yaml):
    //   * FLAT RULER tools (Vcarve/Vfill/Vdelete): flattened-cylinder brushes.
    //     Drag is armed by the console command firing (RecordCylinder) and
    //     pumps constant-arc-length dabs while LMB is held — same feel as the
    //     water/terrain brushes.
    //   * 3D GIZMO tools (Vcube/Vsphere): a wireframe cube/sphere follows the
    //     aim point as a true 3D placement visual (cookie-style). Ctrl+Scroll
    //     resizes, LMB stamps + drags (3D spacing — strokes work up walls and
    //     through air, spheres dragged = smooth tubes), hold LeftAlt to CARVE
    //     instead of fill (gizmo turns red).
    public sealed class VoxelBrushDrag : MonoBehaviour
    {
        private const float DRAG_FRESHNESS_SECONDS = 2f;
        private const float DAB_SPACING_RATIO = 0.3f;
        private const int MAX_DABS_PER_FRAME = 8;

        public static float GizmoSize = 4f;   // cube edge / sphere diameter (Ctrl+Scroll)

        private enum Tool { None, Cylinder, Cube, Sphere }

        // Cylinder-tool state, armed by the console command.
        private static VoxelBrushMode s_cylMode;
        private static float s_cylRadius;
        private static float s_cylSignedHeight;
        private static float s_lastCommandTime = -100f;

        public static void RecordCylinder(VoxelBrushMode mode, float radius, float signedHeight)
        {
            s_cylMode = mode;
            s_cylRadius = radius;
            s_cylSignedHeight = signedHeight;
            s_lastCommandTime = Time.realtimeSinceStartup;
        }

        private Vector3 _lastDabPos;
        private bool _strokeActive;
        private float _dabBudget;
        private Tool _prevTool = Tool.None;
        private bool _armAfterRelease;   // a fresh tool selection must not double as a carve click

        private GameObject _gizmoCube;
        private GameObject _gizmoSphere;
        private Material _gizmoMat;

        private void Update()
        {
            var player = Player.m_localPlayer;
            Tool tool = player != null ? SelectedTool(player) : Tool.None;
            // Just selected/switched a V-tool (from the build menu) → require the
            // mouse to be released before the first carve, so the click that
            // picked the tool doesn't also dig. Cleared on the next LMB-up below.
            if (tool != Tool.None && tool != _prevTool) _armAfterRelease = true;
            _prevTool = tool;
            if (tool == Tool.None || (FiresVoxelConfig.Enabled != null && !FiresVoxelConfig.Enabled.Value))
            {
                SetGizmo(Tool.None, Vector3.zero, false);
                SetRulerVisualHidden(false);
                _strokeActive = false;
                return;
            }

            // THE IH DIVORCE — our own cursor (crosshair raycast) and our own
            // scroll radius. No ruler, no console arming.
            Vector3 cursor;
            if (!TileBrush.TryGetPointerWorldPosition(out cursor))
            {
                SetGizmo(Tool.None, Vector3.zero, false);
                return;
            }

            bool carveMod = Input.GetKey(KeyCode.LeftAlt);
            SetRulerVisualHidden(true);   // IH's white ring never draws under our ghosts

            // Plain scroll = UNIFORM size (camera zoom is suppressed by the
            // VoxelToolPieces ZInput patch while a V-tool is selected): the
            // whole shape scales — radius AND cylinder height keep their
            // aspect. PgUp/PgDn changes the aspect (explicit height).
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f
                && (UnityEngine.EventSystems.EventSystem.current == null
                    || !UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject()))
            {
                float factor = 1f + scroll * 1.4f;
                VoxelToolPieces.ToolRadius = Mathf.Clamp(VoxelToolPieces.ToolRadius * factor, 0.5f, 32f);
                if (Mathf.Abs(VoxelToolPieces.CylHeight) > 0.01f)
                    VoxelToolPieces.CylHeight = Mathf.Clamp(VoxelToolPieces.CylHeight * factor, -24f, 24f);
            }
            if (tool == Tool.Cylinder)
            {
                if (Input.GetKeyDown(KeyCode.PageUp)) VoxelToolPieces.CylHeight = Mathf.Clamp(VoxelToolPieces.CylHeight + 1f, -24f, 24f);
                if (Input.GetKeyDown(KeyCode.PageDown)) VoxelToolPieces.CylHeight = Mathf.Clamp(VoxelToolPieces.CylHeight - 1f, -24f, 24f);
            }

            if (tool == Tool.Cube || tool == Tool.Sphere)
            {
                SetGizmo(tool, cursor, carveMod);
            }
            else
            {
                // Vcarve/Vdelete = dig ghost (lines only), Vfill = fill ghost
                // (terrain-shader solid). Always armed — no console freshness.
                SetGizmo(Tool.Cylinder, cursor, !SelectedIsFill(player));
                s_cylRadius = VoxelToolPieces.ToolRadius;
                s_cylSignedHeight = VoxelToolPieces.CylHeight;
                s_cylMode = SelectedCylMode(player);
            }

            if (!Input.GetMouseButton(0))
            {
                _strokeActive = false;
                _armAfterRelease = false;   // selecting-click released — a fresh press now carves
                return;
            }
            // Still holding the button that selected the tool: show the ghost but
            // don't carve until it's released and pressed again.
            if (_armAfterRelease) return;
            if (UnityEngine.EventSystems.EventSystem.current != null
                && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
                return;

            float dabSize = tool == Tool.Cylinder ? s_cylRadius : EffectiveGizmoRadius();
            if (dabSize < 0.05f) return;
            float spacing = Mathf.Max(dabSize * DAB_SPACING_RATIO, 0.25f);

            if (!_strokeActive)
            {
                // IH-free: WE place the first dab on click-down (there is no
                // console command doing it anymore), then the pump spaces the
                // rest of the stroke.
                Dispatch(tool, cursor, carveMod);
                _lastDabPos = cursor;
                _strokeActive = true;
                return;
            }

            _dabBudget = Mathf.Min(_dabBudget + TileBrushModes.DrawSpeed * MAX_DABS_PER_FRAME, MAX_DABS_PER_FRAME);
            while (_dabBudget >= 1f)
            {
                Vector3 delta = cursor - _lastDabPos;   // FULL 3D spacing — strokes climb walls
                float len = delta.magnitude;
                if (len < spacing) break;
                Vector3 dab = _lastDabPos + delta * (spacing / len);
                Dispatch(tool, dab, carveMod);
                _lastDabPos = dab;
                _dabBudget -= 1f;
            }
        }

        private void Dispatch(Tool tool, Vector3 pos, bool carveMod)
        {
            try
            {
                switch (tool)
                {
                    case Tool.Cylinder:
                    {
                        float h = Mathf.Abs(s_cylSignedHeight);
                        if (h > 0.01f)
                        {
                            // Explicit height (PgUp/PgDn) = cylinder mode:
                            // trenches and shafts, offset toward the sign.
                            Vector3 c = pos;
                            c.y += s_cylSignedHeight * 0.5f;
                            VoxelWorld.ApplyBrush(c, s_cylRadius, h, VoxelBrushShape.Cylinder, s_cylMode);
                        }
                        else
                        {
                            // Default = TRUE SPHERE: the volume shown by the
                            // ghost is exactly the volume removed/placed —
                            // no thin-disc surprise, uniform by definition.
                            VoxelWorld.ApplyBrush(pos, s_cylRadius, 0f, VoxelBrushShape.Sphere, s_cylMode);
                        }
                        break;
                    }
                    case Tool.Cube:
                        VoxelWorld.ApplyBrush(pos, EffectiveGizmoRadius(), 0f, VoxelBrushShape.Cube,
                            carveMod ? VoxelBrushMode.Carve : VoxelBrushMode.Fill);
                        break;
                    case Tool.Sphere:
                        VoxelWorld.ApplyBrush(pos, EffectiveGizmoRadius(), 0f, VoxelBrushShape.Sphere,
                            carveMod ? VoxelBrushMode.Carve : VoxelBrushMode.Fill);
                        break;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[FiresVoxel] drag dispatch threw: {ex.Message}");
            }
        }

        // The IH ruler's white segmented ring reads as a second, wrong tool
        // visual under our ghosts. Hide its RENDER parts while a V-tool is
        // selected; the projector transform + radius keep updating (scroll
        // still resizes, TryGetRulerPosition still answers). Restore on
        // deselect.
        private static GameObject s_hiddenRuler;
        private static void SetRulerVisualHidden(bool hidden)
        {
            if (!hidden)
            {
                if (s_hiddenRuler != null)
                {
                    foreach (var cp2 in s_hiddenRuler.GetComponentsInChildren<CircleProjector>(true)) cp2.enabled = true;
                    foreach (var r in s_hiddenRuler.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                    s_hiddenRuler = null;
                }
                return;
            }
            if (!InfinityHammerRulerProbe.TryGetProjector(out var proj) || proj == null) return;
            if (s_hiddenRuler == proj)
            {
                // segments respawn each frame while the projector runs — keep them dark
                foreach (var r in proj.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled) r.enabled = false;
                return;
            }
            SetRulerVisualHidden(false);
            s_hiddenRuler = proj;
            foreach (var cp in proj.GetComponentsInChildren<CircleProjector>(true)) cp.enabled = false;
            foreach (var r in proj.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        }

        private static bool SelectedIsFill(Player player)
        {
            var piece = player?.m_buildPieces?.GetSelectedPiece();
            return piece != null && piece.m_name == "Vfill";
        }

        private static VoxelBrushMode SelectedCylMode(Player player)
        {
            var piece = player?.m_buildPieces?.GetSelectedPiece();
            switch (piece != null ? piece.m_name : "")
            {
                case "Vfill": return VoxelBrushMode.Fill;
                case "Vdelete": return VoxelBrushMode.Restore;
                default: return VoxelBrushMode.Carve;
            }
        }

        // ── Tool gating by selected hoe piece name (YAML `name:` fields) ────
        private static Tool SelectedTool(Player player)
        {
            var pt = player.m_buildPieces;
            if (pt == null) return Tool.None;
            var piece = pt.GetSelectedPiece();
            if (piece == null) return Tool.None;
            switch (piece.m_name)
            {
                case "Vcarve":
                case "Vfill":
                case "Vdelete":
                    return Tool.Cylinder;
                case "Vcube":
                    return Tool.Cube;
                case "Vsphere":
                    return Tool.Sphere;
                default:
                    return Tool.None;
            }
        }

        // ── 3D gizmo — the ruler's segmented green line, as a 3D shape ──────
        // The cube/sphere IS the ruler visual in 3D: same green, dashed
        // segments, and it scales live with the hoe ruler's radius. Ctrl+Scroll
        // sizing remains as the fallback when no ruler is active.
        private static readonly Color GIZMO_GREEN = new Color(0.30f, 0.95f, 0.35f, 1f);   // RulerColorDriver TERRAIN_GREEN
        private static readonly Color GIZMO_CARVE = new Color(1f, 0.25f, 0.2f, 1f);

        // IH-free: the ONE radius is ours (plain scroll while a V-tool is
        // selected). No projector probe, no ruler.
        internal static float EffectiveGizmoRadius()
            => Mathf.Clamp(VoxelToolPieces.ToolRadius, 0.5f, 32f);

        private void SetGizmo(Tool tool, Vector3 pos, bool carve)
        {
            if (tool != Tool.Cube && _gizmoCube != null) _gizmoCube.SetActive(false);
            if ((tool != Tool.Sphere && tool != Tool.Cylinder) && _gizmoSphere != null) _gizmoSphere.SetActive(false);
            if (tool == Tool.None) return;

            var go = tool == Tool.Cube ? EnsureCubeGizmo() : EnsureSphereGizmo();
            go.SetActive(true);
            go.transform.position = pos;
            if (tool == Tool.Cylinder)
            {
                // Ghost = THE brush volume. Default (no explicit height) is a
                // true sphere; PgUp/PgDn cylinder mode squashes to r × h.
                float r = s_cylRadius > 0.05f ? s_cylRadius : EffectiveGizmoRadius();
                float hAbs = Mathf.Abs(s_cylSignedHeight);
                go.transform.localScale = hAbs > 0.01f
                    ? new Vector3(r * 2f, hAbs, r * 2f)
                    : Vector3.one * (r * 2f);
            }
            else
            {
                go.transform.localScale = Vector3.one * (EffectiveGizmoRadius() * 2f);
            }
            // Slow ruler-style spin — cage only (the solid preview and the
            // cube's stamp volume stay world-aligned).
            var cage = go.transform.Find("cage");
            if (cage != null && tool != Tool.Cube)
                cage.localRotation = Quaternion.Euler(0f, Time.time * CAGE_SPIN_DEG_PER_SEC, 0f);
            // DIG = green dotted lines only; FILL = the same lines over a
            // solid preview wearing THE TERRAIN SHADER — you see exactly the
            // ground you are about to place. (User spec: no red state.)
            foreach (var lr in go.GetComponentsInChildren<LineRenderer>(true))
            {
                lr.startColor = GIZMO_GREEN;
                lr.endColor = GIZMO_GREEN;
            }
            var solid = go.transform.Find("solid");
            if (solid != null)
            {
                bool showSolid = !carve;
                if (solid.gameObject.activeSelf != showSolid) solid.gameObject.SetActive(showSolid);
                if (showSolid)
                {
                    var mr = solid.GetComponent<MeshRenderer>();
                    var mat = Utilities.TerrainMaterialCache.GetCurrentMaterial();
                    if (mr != null && mat != null && mr.sharedMaterial != mat) mr.sharedMaterial = mat;
                }
            }
        }

        // Solid preview child: a mesh copy of the Unity primitive (never
        // mutate the shared primitive mesh) with meadows vertex colors so the
        // terrain shader shows plain grass/rock instead of a biome mash.
        internal static void AddSolidChild(GameObject parent, PrimitiveType type)
        {
            var prim = GameObject.CreatePrimitive(type);
            var srcMesh = prim.GetComponent<MeshFilter>().sharedMesh;
            var mesh = Object.Instantiate(srcMesh);
            var cols = new Color32[mesh.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(0, 0, 0, 0);
            mesh.colors32 = cols;
            Object.Destroy(prim);

            var child = new GameObject("solid");
            child.transform.SetParent(parent.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = child.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static Texture2D _dashTex;
        private static Texture2D DashTex()
        {
            if (_dashTex != null) return _dashTex;
            _dashTex = new Texture2D(8, 2, TextureFormat.RGBA32, false);
            var px = new Color32[16];
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 8; x++)
                    px[y * 8 + x] = x < 5 ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            _dashTex.SetPixels32(px);
            _dashTex.Apply(false, false);
            _dashTex.wrapMode = TextureWrapMode.Repeat;
            _dashTex.filterMode = FilterMode.Point;
            return _dashTex;
        }

        private static Material s_sharedLineMat;
        internal static Material SharedLineMat()
        {
            if (s_sharedLineMat != null) return s_sharedLineMat;
            var sh = Shader.Find("Sprites/Default");
            s_sharedLineMat = new Material(sh) { name = "FiresVoxelGizmo", mainTexture = DashTex() };
            s_sharedLineMat.mainTextureScale = new Vector2(1.2f, 1f);   // ≈ one dash per 0.8m of line
            return s_sharedLineMat;
        }

        private Material GizmoMat() => SharedLineMat();

        private LineRenderer NewLine(GameObject parent, int positions)
        {
            var child = new GameObject("line");
            child.transform.SetParent(parent.transform, false);
            var lr = child.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = false;
            lr.widthMultiplier = 0.08f;
            lr.material = GizmoMat();
            lr.textureMode = LineTextureMode.Tile;
            lr.positionCount = positions;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return lr;
        }

        private GameObject EnsureCubeGizmo()
        {
            if (_gizmoCube != null) return _gizmoCube;
            _gizmoCube = new GameObject("FiresVoxelGizmo_Cube");
            _gizmoCube.transform.SetParent(transform, false);
            AddSolidChild(_gizmoCube, PrimitiveType.Cube);
            var cubeCage = new GameObject("cage");
            cubeCage.transform.SetParent(_gizmoCube.transform, false);
            cubeCage.transform.localScale = Vector3.one * CAGE_INFLATE;
            // One 16-point strip walks all 12 edges of a unit cube.
            var lr = NewLine(cubeCage, 16);
            float h = 0.5f;
            var p = new[]
            {
                new Vector3(-h,-h,-h), new Vector3( h,-h,-h), new Vector3( h,-h, h), new Vector3(-h,-h, h),
                new Vector3(-h,-h,-h), new Vector3(-h, h,-h), new Vector3( h, h,-h), new Vector3( h,-h,-h),
                new Vector3( h, h,-h), new Vector3( h, h, h), new Vector3( h,-h, h),
                new Vector3( h, h, h), new Vector3(-h, h, h), new Vector3(-h,-h, h),
                new Vector3(-h, h, h), new Vector3(-h, h,-h),
            };
            lr.SetPositions(p);
            return _gizmoCube;
        }

        private GameObject EnsureSphereGizmo()
        {
            if (_gizmoSphere != null) return _gizmoSphere;
            _gizmoSphere = BuildSphereGizmo(transform, GizmoMat());
            AddSolidChild(_gizmoSphere, PrimitiveType.Sphere);
            return _gizmoSphere;
        }

        // Shared builder (water tools reuse it): three orthogonal great
        // circles + four extra meridians — the dotted-cage look from the
        // user's reference shot. Lines live under a "cage" child scaled 2%
        // proud of the unit volume (never z-fights the solid preview or the
        // freshly-stamped terrain) and the cage slow-rotates like the IH
        // ruler (driven from SetGizmo/UpdateGhost each frame).
        internal const float CAGE_INFLATE = 1.02f;
        internal const float CAGE_SPIN_DEG_PER_SEC = 12f;

        internal static GameObject BuildSphereGizmo(Transform parent, Material lineMat)
        {
            var go = new GameObject("FiresGizmo_Sphere");
            go.transform.SetParent(parent, false);
            var cage = new GameObject("cage");
            cage.transform.SetParent(go.transform, false);
            cage.transform.localScale = Vector3.one * CAGE_INFLATE;
            const int SEG = 32;
            LineRenderer Line()
            {
                var child = new GameObject("line");
                child.transform.SetParent(cage.transform, false);
                var lr = child.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.loop = false;
                lr.widthMultiplier = 0.08f;
                lr.material = lineMat;
                lr.textureMode = LineTextureMode.Tile;
                lr.positionCount = SEG + 1;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                return lr;
            }
            for (int axis = 0; axis < 3; axis++)
            {
                var lr = Line();
                for (int i = 0; i <= SEG; i++)
                {
                    float a = i / (float)SEG * Mathf.PI * 2f;
                    float c = Mathf.Cos(a) * 0.5f, s = Mathf.Sin(a) * 0.5f;
                    Vector3 v = axis == 0 ? new Vector3(0f, c, s)
                        : axis == 1 ? new Vector3(c, 0f, s)
                        : new Vector3(c, s, 0f);
                    lr.SetPosition(i, v);
                }
            }
            for (int m = 0; m < 4; m++)
            {
                float yaw = (m + 1) * Mathf.PI / 4f;    // meridians at 45° steps (0/90 covered above)
                if (m == 1) continue;                    // 90° duplicates the axis-0 circle
                var lr = Line();
                float cy = Mathf.Cos(yaw), sy = Mathf.Sin(yaw);
                for (int i = 0; i <= SEG; i++)
                {
                    float a = i / (float)SEG * Mathf.PI * 2f;
                    float c = Mathf.Cos(a) * 0.5f, s = Mathf.Sin(a) * 0.5f;
                    lr.SetPosition(i, new Vector3(c * cy, s, c * sy));
                }
            }
            return go;
        }

        // Attach to the plugin root (DontDestroyOnLoad) — WaterBrushDrag pattern.
        public static void EnsureAttached()
        {
            var root = FiresAdminTerrain.RootObject;
            if (root == null) return;
            if (root.GetComponent<VoxelBrushDrag>() != null) return;
            root.AddComponent<VoxelBrushDrag>();
            Debug.Log("[FiresVoxel] VoxelBrushDrag attached (drag + gizmo tools).");
        }
    }
}
