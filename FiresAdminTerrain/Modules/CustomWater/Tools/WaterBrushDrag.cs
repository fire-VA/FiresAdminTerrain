using UnityEngine;
using VerdantsAscent.Terrain.Lifecycle;
using VerdantsAscent.Terrain.Persistence;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    // Brush-style drag pump for water placement. Mirrors TileBrushDrag's
    // architecture from the terrain side, just dispatching water-disk
    // placements instead of terrain ops. Lets the user hold LMB and
    // paint a continuous river / pond / waterfall instead of having to
    // click-click-click every disk.
    //
    // STROKE LIFECYCLE
    //   1. WaterBrushCommands.PlaceDisk runs (IH fired the console
    //      command). It calls RecordLastBrush + dispatches the FIRST
    //      disk.
    //   2. Holding LMB keeps the cursor over the world. This
    //      MonoBehaviour's Update polls mouse-held, walks the cursor
    //      segment in fixed `radius * DAB_SPACING_RATIO` steps, and
    //      emits one disk per step (same B-1 constant-arc-length pump
    //      as the terrain brush).
    //   3. Releasing LMB stops the dispatch. Next console-fire re-primes
    //      the state from RecordLastBrush.
    //
    // KIND DIFFERENCES
    //   * Disk  — normal water. Dab Y = the recorded center.y (the
    //     ruler's chosen surface level). Drag horizontally to paint a
    //     river/pond; the surface stays flat at the ruler Y. Terrain
    //     can clip through (that's the user-requested "bubble" look).
    //   * Waterfall — drag down a slope. Dab Y = terrain raycast Y at
    //     the dab XZ + WATERFALL_OFFSET. Each dab sits a uniform
    //     distance above whatever terrain is directly under it, so
    //     the water surface follows the slope's shape.
    //
    // Sits on the player GO same as TileBrushDrag.
    public sealed class WaterBrushDrag : MonoBehaviour
    {
        // Honor the same drag freshness window as the terrain brush
        // (drag dies if the console command hasn't fired in ~2s).
        public const float DRAG_FRESHNESS_SECONDS = 2f;

        // Dab spacing as a fraction of brush radius. Same default as the
        // terrain brush (Blender's 10% spacing on a 0..1 scale → 0.2).
        // For waterfall we tighten this so the stream reads as
        // continuous on a vertical cliff.
        private const float DAB_SPACING_RATIO = 0.2f;
        private const float WATERFALL_DAB_SPACING_RATIO = 0.15f;

        private const int MAX_DABS_PER_FRAME = 16;

        // Reuse the terrain brush's resistance defaults so the feel
        // matches. User explicitly liked the round-40 resistance bump
        // and asked for waterfall to feel the same.
        private const float LAZY_POINTER_RADIUS = 0.5f;
        private const float LAZY_POINTER_FACTOR = 0.75f;
        private const float MAX_BRUSH_JUMP_PER_FRAME_RATIO = 0.5f;

        // How far above the raycast-terrain point each waterfall dab
        // sits. Small enough that the water reads as "hugging" the
        // slope, not "floating above" it.
        private const float WATERFALL_OFFSET = 0.5f;

        // Default depth for waterfall dabs — shallow so it reads as a
        // sheet of flowing water, not a bottomless pool clinging to
        // the rock face.
        private const float WATERFALL_DAB_DEPTH = 0.3f;

        public enum BrushKind { None, Disk, Waterfall }

        private static BrushKind _kind = BrushKind.None;
        private static float _radius;
        private static float _depth;
        private static CustomLiquidType _liquid;
        private static float _surfaceY;
        private static float _lastBrushTime = -100f;

        private Vector3 _brushPos;
        private Vector3 _lastDabPos;
        private bool _strokeActive;

        // Called by the water brush commands on every console-driven
        // dispatch. Snapshots the parameters so drag can re-fire the
        // same op as the cursor moves.
        public static void RecordLastBrush(
            BrushKind kind,
            float radius,
            float depth,
            CustomLiquidType liquid,
            float surfaceY)
        {
            _kind = kind;
            _radius = radius;
            _depth = depth;
            _liquid = liquid;
            _surfaceY = surfaceY;
            _lastBrushTime = Time.realtimeSinceStartup;
        }

        // Water ghost — the dotted sphere cage wearing THE WATER SHADER at
        // the cursor, scaled by the live ruler radius. You see the water you
        // are about to place, and it follows the raycast while you hold.
        private GameObject _ghost;

        private void UpdateGhost(Player player)
        {
            bool show = player != null && IsOurBrushToolSelected(player);
            Vector3 cursor = Vector3.zero;
            if (show)
            {
                if (!InfinityHammerRulerProbe.TryGetRulerPosition(out cursor)
                    && !TileBrush.TryGetPointerWorldPosition(out cursor))
                    show = false;
            }
            if (!show)
            {
                if (_ghost != null && _ghost.activeSelf) _ghost.SetActive(false);
                return;
            }
            if (_ghost == null)
            {
                _ghost = Terrain.Voxel.VoxelBrushDrag.BuildSphereGizmo(transform, Terrain.Voxel.VoxelBrushDrag.SharedLineMat());
                Terrain.Voxel.VoxelBrushDrag.AddSolidChild(_ghost, PrimitiveType.Sphere);
                var blue = new Color(0.35f, 0.75f, 1f, 1f);
                foreach (var lr in _ghost.GetComponentsInChildren<LineRenderer>(true))
                {
                    lr.startColor = blue;
                    lr.endColor = blue;
                }
            }
            _ghost.SetActive(true);
            _ghost.transform.position = cursor;
            _ghost.transform.localScale = Vector3.one * (Terrain.Voxel.VoxelBrushDrag.EffectiveGizmoRadius() * 2f);
            var cage = _ghost.transform.Find("cage");
            if (cage != null)
                cage.localRotation = Quaternion.Euler(0f, Time.time * Terrain.Voxel.VoxelBrushDrag.CAGE_SPIN_DEG_PER_SEC, 0f);
            var solid = _ghost.transform.Find("solid");
            if (solid != null)
            {
                var mr = solid.GetComponent<MeshRenderer>();
                var mat = Water.Core.FiresLiquidWaterPrefab.GetSharedPourMaterial();
                if (mr != null && mat != null && mr.sharedMaterial != mat) mr.sharedMaterial = mat;
            }
        }

        private void Update()
        {
            UpdateGhost(Player.m_localPlayer);

            if (_kind == BrushKind.None) return;
            if (Time.realtimeSinceStartup - _lastBrushTime > DRAG_FRESHNESS_SECONDS) return;

            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!IsOurBrushToolSelected(player)) return;

            if (!VerdantsAscent.Terrain.FatInput.PrimaryHeld)
            {
                if (_strokeActive) _strokeActive = false;
                return;
            }

            // A tool selection is never also a tool use — the hammer-menu click
            // is still held on the next frame and this is a level read. Also
            // covers the pointer sitting over any UI element.
            if (!FiresCore.Input.FiresPlaceGate.ReadyToAct()) return;

            // Prefer the IH ruler position over our own raycast so the
            // water brush stays exactly where the user sees the ring
            // (same round-40 ruler-stays-locked behavior the terrain
            // brush uses).
            Vector3 rawCursor;
            if (!InfinityHammerRulerProbe.TryGetRulerPosition(out rawCursor))
            {
                if (!TileBrush.TryGetPointerWorldPosition(out rawCursor)) return;
            }

            // Lazy-pointer filter + dead zone + max-jump clamp — same
            // resistance the terrain brush uses.
            if (!_strokeActive)
            {
                _brushPos = rawCursor;
                _lastDabPos = rawCursor;
                _strokeActive = true;
                // First-dab dispatch handled by the console command path
                // (the click that triggered the brush already placed a
                // disk). The drag pump picks up from the NEXT spacing
                // step.
                return;
            }
            else
            {
                float dx = rawCursor.x - _brushPos.x;
                float dz = rawCursor.z - _brushPos.z;
                float distSqHoriz = dx * dx + dz * dz;
                if (distSqHoriz < LAZY_POINTER_RADIUS * LAZY_POINTER_RADIUS) return;

                Vector3 lerped = Vector3.Lerp(rawCursor, _brushPos, LAZY_POINTER_FACTOR);

                if (_radius > 0.01f)
                {
                    float moveX = lerped.x - _brushPos.x;
                    float moveZ = lerped.z - _brushPos.z;
                    float moveLen = Mathf.Sqrt(moveX * moveX + moveZ * moveZ);
                    float maxMove = _radius * MAX_BRUSH_JUMP_PER_FRAME_RATIO;
                    if (moveLen > maxMove && moveLen > 0.001f)
                    {
                        float scale = maxMove / moveLen;
                        lerped = new Vector3(
                            _brushPos.x + moveX * scale,
                            lerped.y,
                            _brushPos.z + moveZ * scale);
                    }
                }
                _brushPos = lerped;
            }

            // Constant-arc-length dab pump.
            if (_radius < 0.01f) return;
            float spacingRatio = (_kind == BrushKind.Waterfall)
                ? WATERFALL_DAB_SPACING_RATIO
                : DAB_SPACING_RATIO;
            float spacing = _radius * spacingRatio;
            if (spacing < 0.01f) return;

            // Round-63 — same draw-speed throttle as the terrain pump
            // (shared TileBrushModes.DrawSpeed, `va_brush_drawspeed`).
            // 0.2.712: per second, not per frame (see TileBrushDrag).
            _dabBudget = Mathf.Min(
                _dabBudget + VerdantsAscent.Terrain.Persistence.TileBrushModes.DrawSpeed * MAX_DABS_PER_FRAME
                    * VerdantsAscent.Terrain.Persistence.TileBrushDrag.DRAW_SPEED_REFERENCE_FPS * Time.deltaTime,
                MAX_DABS_PER_FRAME);

            while (_dabBudget >= 1f)
            {
                float pdx = _brushPos.x - _lastDabPos.x;
                float pdz = _brushPos.z - _lastDabPos.z;
                float length = Mathf.Sqrt(pdx * pdx + pdz * pdz);
                if (length < spacing) break;

                float frac = spacing / length;
                Vector3 nextDab = new Vector3(
                    _lastDabPos.x + pdx * frac,
                    Mathf.Lerp(_lastDabPos.y, _brushPos.y, frac),
                    _lastDabPos.z + pdz * frac);

                DispatchAt(nextDab);
                _lastDabPos = nextDab;
                _dabBudget -= 1f;
            }
        }

        // Round-63 — fractional dab allowance for the draw-speed throttle.
        private float _dabBudget;

        private void DispatchAt(Vector3 cursor)
        {
            Vector3 placeCenter;
            float placeDepth;

            switch (_kind)
            {
                case BrushKind.Disk:
                    // Normal water: surface stays at the ruler's chosen Y
                    // so dragging across a slope produces a flat-topped
                    // pond/river. Terrain pokes through if the slope rises
                    // above _surfaceY — that's the user-requested clipping
                    // behavior.
                    placeCenter = new Vector3(cursor.x, _surfaceY, cursor.z);
                    placeDepth = _depth;
                    break;

                case BrushKind.Waterfall:
                    // Waterfall: each dab sits a constant distance above
                    // the local terrain. Surface FOLLOWS the slope so the
                    // water reads as flowing down the cliff face at
                    // uniform thickness. Round-68: physics probe against
                    // real colliders — GetGroundHeight is heightmap-blind
                    // and put dabs meters under custom cliffs.
                    if (TryProbeGroundBelow(cursor, out float terrainY))
                        placeCenter = new Vector3(cursor.x, terrainY + WATERFALL_OFFSET, cursor.z);
                    else
                        placeCenter = cursor;
                    placeDepth = WATERFALL_DAB_DEPTH;
                    break;

                default:
                    return;
            }

            try
            {
                CustomWaterManager.CreateDiskOfType(placeCenter, _radius, placeDepth, _liquid);
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[WaterBrushDrag] DispatchAt threw at {placeCenter}: {ex.Message}");
            }
        }

        // Round-68 — ground probe against REAL colliders for waterfall
        // dabs (and the waterfall command's first dab). The dab point
        // comes from a crosshair raycast, so it already sits ON the
        // visible surface — probe a short window around it to normalize
        // onto the exact collider height. Never uses GetGroundHeight
        // (heightmap-blind — meters off on FAT custom terrain).
        private static readonly RaycastHit[] s_probeHits = new RaycastHit[8];
        private static int s_probeMask = -1;

        public static bool TryProbeGroundBelow(Vector3 point, out float groundY)
        {
            groundY = point.y;
            if (s_probeMask < 0)
                s_probeMask = LayerMask.GetMask(
                    "Default", "static_solid", "piece", "terrain", "Default_small");

            var origin = new Vector3(point.x, point.y + 2f, point.z);
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, s_probeHits, 60f,
                s_probeMask, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                float y = s_probeHits[i].point.y;
                if (y > best) best = y;
            }
            if (float.IsNegativeInfinity(best)) return false;
            groundY = best;
            return true;
        }

        // Tools where drag should re-fire water dispatches. Match
        // against the currently-selected hammer piece m_name (same
        // pattern TileBrushDrag uses for its tool gating). Names come
        // from Docs/infinity_tools.yaml's water section.
        private static bool IsOurBrushToolSelected(Player player)
        {
            var pt = player.m_buildPieces;
            if (pt == null) return false;
            var piece = pt.GetSelectedPiece();
            if (piece == null) return false;
            switch (piece.m_name)
            {
                case "Water":
                case "Lava":
                case "Swimmable Lava":
                case "Tar":
                case "Boiling Water":
                case "Freezing Water":
                case "Ice":
                // Round-W-2 — new waterfall brush.
                case "Waterfall":
                    return true;
                default:
                    return false;
            }
        }

        // Bootstrap helper — attach this MonoBehaviour to the plugin
        // root (DontDestroyOnLoad). Mirrors TileBrushDrag.EnsureAttached.
        // Wired from TileAnchorPrefab.Ensure (one-shot prefab register
        // pass, runs after IH has finished loading + before any player
        // spawn).
        public static void EnsureAttached()
        {
            var root = FiresAdminTerrain.RootObject;
            if (root == null) return;
            if (root.GetComponent<WaterBrushDrag>() != null) return;
            root.AddComponent<WaterBrushDrag>();
            FiresLogger.LogInfo("[WaterBrushDrag] attached to plugin root (DontDestroyOnLoad).");
        }
    }
}
