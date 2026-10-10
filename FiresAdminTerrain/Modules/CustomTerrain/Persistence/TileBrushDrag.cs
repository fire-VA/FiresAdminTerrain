using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Click-and-drag for brush tools — like the RPG-maker terrain painter.
    //
    // How it works:
    //   1. User equips one of our brush tools (Floor, Raise, Paint Dirt, etc.)
    //   2. User clicks once: IH fires our va_brush_* command with the current
    //      scroll size and height offset. Cmd_Flat / Cmd_Raise / etc. call
    //      `TileBrushDrag.RecordLastBrush(cmd, radius, height, falloff, color)`
    //      to snapshot the args.
    //   3. User keeps left-mouse held: this MonoBehaviour's Update loop polls
    //      mouse-held, checks our tool is still active, and re-fires the
    //      cached brush at the current cursor position every DRAG_INTERVAL.
    //   4. Releasing mouse stops the dispatch. Next click re-primes the cache.
    //
    // The 2-second "freshness" guard means if you switch tools, IH dispatches
    // the new command, the snapshot updates, and drag uses the new tool's
    // parameters. If you don't click for 2 seconds (e.g. just left the tool
    // selected), drag won't fire on mouse-hold — safety net so we don't
    // accidentally paint when the user is just browsing.
    //
    // Attached to FiresAdminTerrain.Instance.gameObject on first Ensure so
    // there's exactly one in the world. DontDestroyOnLoad via the plugin root.
    public sealed class TileBrushDrag : MonoBehaviour
    {
        // ── 2026-05-26 round-31 BLENDER PORT (Phase B-1 + B-3) ─────────
        //
        // Rewrites the legacy 10 Hz tick model into Blender's constant-arc-
        // length dab pump (see Docs/PLAN_BlenderSculptIntegration.md §3).
        //
        // B-1 model: per-frame, walk the cursor segment in fixed `radius *
        // DAB_SPACING_RATIO` steps and emit one dab per step with REMAINDER
        // CARRY (the partial sub-step is preserved between frames via
        // `_lastDabPos`). Mouse speed no longer affects dab density.
        //
        // B-3 layer: lazy-pointer cursor filter (dead zone + lerp toward
        // lagging position) sits in front of B-1 so micro-jitter doesn't
        // turn into wasted dabs.
        //
        // The legacy DRAG_INTERVAL is retained ONLY as a safety throttle
        // — if a frame somehow needs to emit thousands of dabs (e.g. a
        // teleport), we cap to MAX_DABS_PER_FRAME so we don't lock the
        // main thread. Normal play hits 1-3 dabs/frame.
        public const float DRAG_FRESHNESS_SECONDS = 2f;

        /// <summary>
        /// Dab spacing as a fraction of brush radius. Matches Blender's
        /// `brush->spacing = 10` default → `0.2 * radius`. Smaller = denser
        /// dabs (smoother / heavier writes); larger = looser (stippled).
        /// </summary>
        private const float DAB_SPACING_RATIO = 0.2f;

        /// <summary>
        /// Safety cap on dabs emitted per Unity frame. With remainder
        /// carry the math can't normally blow up, but a teleport-style
        /// cursor jump (cross-map raycast change) would otherwise emit
        /// hundreds. 16 is plenty for normal sculpting.
        /// </summary>
        private const int MAX_DABS_PER_FRAME = 16;
        internal const float DRAW_SPEED_REFERENCE_FPS = 60f;   // DrawSpeed x 16 dabs a frame at 60 fps = the per-second rate

        /// <summary>
        /// Lazy-pointer dead zone in WORLD METERS. Cursor must move more
        /// than this from the current brush position before the brush
        /// follows. Round-40 bump 0.25 → 0.5: user reported "everything
        /// runs so smooth that its actually hard to control" — larger
        /// dead zone means the brush ignores micro-jitter, only commits
        /// to a new position when the cursor genuinely moves.
        /// </summary>
        private const float LAZY_POINTER_RADIUS = 0.5f;

        /// <summary>
        /// Lazy-pointer lerp factor. Round-40 bump 0.5 → 0.75: more
        /// resistance, brush visibly lags behind cursor jerks. Lerp
        /// argument order: `Lerp(rawCursor, brushPos, factor)` — factor
        /// = 1 leaves brush put (max lag), factor = 0 snaps to cursor
        /// (no lag). 0.75 keeps the brush visibly close to where the
        /// user is looking even on big cursor jumps.
        /// </summary>
        private const float LAZY_POINTER_FACTOR = 0.75f;

        /// <summary>
        /// Hard cap on how far the brush position can move in one
        /// frame, expressed as a fraction of brush radius. Defends
        /// against the "Infinity Hammer ruler stuck on a terrain edge
        /// for a second but our brush zoomed off into the distance"
        /// case the user described — if the cursor raycast suddenly
        /// jumps 50m because the camera found a new surface, the
        /// brush moves at most this fraction × radius toward the new
        /// position THIS FRAME and the rest gets absorbed over
        /// subsequent frames. With LAZY_POINTER_FACTOR=0.75 the lerp
        /// already softens this, but the clamp is a hard ceiling for
        /// truly pathological jumps.
        /// </summary>
        private const float MAX_BRUSH_JUMP_PER_FRAME_RATIO = 0.5f;

        /// <summary>
        /// Per-tool resistance multipliers on the lazy-pointer factor.
        /// Higher = more lag. Smooth / Grab / Noise are the
        /// "fine-control" brushes the user singled out as too
        /// sensitive; bumping their effective factor adds a perceptible
        /// drag that makes precise sculpting feel achievable.
        /// </summary>
        private static float ResistanceFactorFor(BrushKind kind)
        {
            switch (kind)
            {
                case BrushKind.Smooth: return 1.10f; // +10% lag — gentle
                case BrushKind.Grab:   return 1.12f;
                case BrushKind.Noise:  return 1.08f;
                default:               return 1f;
            }
        }

        private enum BrushKind { None, Flat, Raise, Lower, Paint, Clear, Delete, Smooth, Grab, Noise }

        private static BrushKind _kind = BrushKind.None;
        private static float _radius;
        private static float _height;          // for Flat (absolute Y) / Raise/Lower (delta)
        private static float _falloff = 1f;    // Raise/Lower
        private static Color32 _color;         // Paint
        private static float _lastBrushTime = -100f;
        // Round-30: when the last brush was a `*_island` variant, drag
        // re-dispatch needs to propagate the flag so peers tag tiles as
        // floating-island during the drag, not just on the initial click.
        private static bool _islandMode;

        // Round-37 B-6: Grab brush — cursor Y at stroke start. Each dab
        // dispatches `dragDeltaY = cursor.y - _grabAnchorCursorY` so the
        // grab follows cumulative cursor motion since LMB-down, not
        // per-frame deltas (matches Blender's grab brush feel).
        private float _grabAnchorCursorY;

        // Called by TileBrushCommands.Cmd_* on every console-driven brush
        // dispatch. Snapshots the parameters so drag can re-fire the same op.
        //
        // 2026-05-25 revert: removed the drag-session height lock that was
        // making subsequent clicks ignore the new ruler height. The lock
        // required Update() to detect mouse-release; when the polling missed
        // a release the lock stuck and Floor stopped respecting Page Up/Down.
        // Reverting to "always capture latest height" — IH doesn't continuous
        // -fire commands by default, so each fresh click captures its own
        // height naturally.
        public static void RecordLastBrush(string commandName, float radius, float height, float falloff, Color32 color)
        {
            _radius = radius;
            _height = height;
            _falloff = falloff;
            _color = color;
            _lastBrushTime = Time.realtimeSinceStartup;

            switch (commandName)
            {
                case "va_brush_flat":          _kind = BrushKind.Flat;   _islandMode = false; break;
                case "va_brush_raise":         _kind = BrushKind.Raise;  _islandMode = false; break;
                case "va_brush_lower":         _kind = BrushKind.Lower;  _islandMode = false; break;
                case "va_brush_paint":         _kind = BrushKind.Paint;  _islandMode = false; break;
                case "va_brush_clear":         _kind = BrushKind.Clear;  _islandMode = false; break;
                case "va_brush_delete":        _kind = BrushKind.Delete; _islandMode = false; break;
                case "va_brush_smooth":        _kind = BrushKind.Smooth; _islandMode = false; break;
                // Round-37 — Grab + Noise (Blender ports).
                case "va_brush_grab":          _kind = BrushKind.Grab;   _islandMode = false; break;
                case "va_brush_noise":         _kind = BrushKind.Noise;  _islandMode = false; break;
                // Round-30 island variants — same kinds, flag enabled.
                case "va_brush_flat_island":   _kind = BrushKind.Flat;   _islandMode = true;  break;
                case "va_brush_raise_island":  _kind = BrushKind.Raise;  _islandMode = true;  break;
                case "va_brush_lower_island":  _kind = BrushKind.Lower;  _islandMode = true;  break;
                default:                       _kind = BrushKind.None;   _islandMode = false; break;
            }
        }

        // Ensure exactly one drag handler exists in the world.
        public static void EnsureAttached()
        {
            var root = FiresAdminTerrain.RootObject;
            if (root == null) return;
            if (root.GetComponent<TileBrushDrag>() != null) return;
            root.AddComponent<TileBrushDrag>();
            FiresLogger.LogInfo("[TileBrushDrag] attached to plugin root (DontDestroyOnLoad).");
        }

        // ── Phase B-1 + B-3 state ───────────────────────────────────────
        //
        // _lastDabPos: world position of the last emitted dab (NOT raw
        // cursor — this is what the dab-pump pivots around for remainder
        // carry). Updated only when a dab actually fires.
        // _brushPos: lazy-pointer-filtered brush position (the rendered
        // brush center). Sits in front of the dab pump; the dab pump
        // works against _brushPos, not raw raycast.
        // _strokeActive: whether the user is mid-stroke (LMB held since
        // last release).
        private Vector3 _lastDabPos;
        private Vector3 _brushPos;
        private bool _strokeActive;

        private void Update()
        {
            if (_kind == BrushKind.None) return;
            if (Time.realtimeSinceStartup - _lastBrushTime > DRAG_FRESHNESS_SECONDS) return;

            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!IsOurBrushToolSelected(player)) return;

            // Use Unity's Input class — works regardless of ZInput state and
            // doesn't interfere with vanilla click handling.
            if (!VerdantsAscent.Terrain.FatInput.PrimaryHeld)
            {
                // Mouse released — end stroke. Reset all stroke-scoped
                // state so the NEXT stroke starts fresh (no carry-over
                // remainder, no stale brush position).
                if (_strokeActive)
                {
                    _strokeActive = false;
                    TileBrush.NotifyStrokeEnd();
                }
                return;
            }

            // A tool selection is never also a tool use: the click that picks the
            // tool out of the hammer menu is still HELD on the next frame, and
            // this is a level read, so without the gate it painted immediately.
            // Also covers the pointer sitting over any UI element.
            if (!FiresCore.Input.FiresPlaceGate.ReadyToAct()) return;

            // Round-40 — prefer Infinity Hammer's ruler position when its
            // ruler is currently active. IH does its own raycast for the
            // visible ruler circle; using THAT position as our brush
            // center means the brush stays exactly where the user sees
            // the ruler — solves the "ruler stuck on edge for a second
            // but brush zoomed off into the distance" case the user
            // flagged. Y component still comes from the ruler too
            // (Projector.transform.position is on the surface IH chose).
            // Falls back to our own camera raycast when IH isn't loaded
            // or the ruler isn't active.
            Vector3 rawCursor;
            if (!VerdantsAscent.Terrain.Lifecycle.InfinityHammerRulerProbe.TryGetRulerPosition(out rawCursor))
            {
                if (!TileBrush.TryGetPointerWorldPosition(out rawCursor)) return;
            }

            // ── B-3: Lazy-pointer cursor filter ─────────────────────────
            //
            // Sits in front of the dab pump. Two clauses, no EMA:
            //   1. Dead zone: if cursor hasn't moved more than
            //      LAZY_POINTER_RADIUS from current _brushPos, brush
            //      stays put (kills micro-jitter, allows holding still
            //      to converge a Smooth brush without creep).
            //   2. Lerp-toward-lagging: when cursor exits the dead zone,
            //      _brushPos catches up via
            //      `_brushPos = lerp(rawCursor, _brushPos, LAZY_POINTER_FACTOR)`.
            //      Note: lerp ARGS swap target/source compared to standard
            //      — high factor lerps the brush AWAY from the cursor,
            //      so 0.5 = light tether, 0.9 = heavy lag.
            //
            // Use horizontal distance only (Y can vary independently when
            // ray hits a sloped surface).
            if (!_strokeActive)
            {
                // First frame of a stroke — initialize brush + dab tracking
                // to the cursor position so we don't synthesize a phantom
                // sweep from (0,0,0).
                _brushPos = rawCursor;
                _lastDabPos = rawCursor;
                _strokeActive = true;
                // Round-37: Grab needs the cursor's Y at stroke start as
                // the anchor for cumulative drag-delta. Each subsequent
                // dab uses (currentCursor.Y - _grabAnchorCursorY) as the
                // grab delta. Reset on every fresh LMB-down.
                _grabAnchorCursorY = rawCursor.y;
                TileBrush.NotifyStrokeStart(_brushPos, _radius);
                // Emit the first dab immediately at the click position
                // (matches click-once-fire behavior of single clicks).
                DispatchBrushAt(_brushPos);
                return;
            }
            else
            {
                float dx = rawCursor.x - _brushPos.x;
                float dz = rawCursor.z - _brushPos.z;
                float distSqHoriz = dx * dx + dz * dz;
                if (distSqHoriz < LAZY_POINTER_RADIUS * LAZY_POINTER_RADIUS)
                {
                    // Inside dead zone — brush stays put, no dab.
                    return;
                }
                // Outside dead zone — lerp brush toward cursor. Factor
                // boosted per-tool for the high-precision brushes
                // (Smooth/Grab/Noise) so they feel weightier.
                float effectiveLag = Mathf.Clamp01(LAZY_POINTER_FACTOR * ResistanceFactorFor(_kind));
                Vector3 lerped = Vector3.Lerp(rawCursor, _brushPos, effectiveLag);

                // Max-jump clamp — defends against cursor teleports
                // (camera raycast suddenly finds a new surface 50m away,
                // IH ruler stuck on edge but our cursor escaped). Even
                // if the lerp would still move the brush a long way,
                // cap horizontal movement to MAX_BRUSH_JUMP_PER_FRAME_RATIO
                // × radius this frame.
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

            // ── B-1: Constant-arc-length dab pump ───────────────────────
            //
            // Walk from `_lastDabPos` to `_brushPos` in `spacing` steps,
            // emitting one dab per step. Sub-spacing remainder is carried
            // by NOT advancing `_lastDabPos` on the partial — next frame's
            // cursor delta combines with whatever's left over.
            //
            // Caps:
            //   * MAX_DABS_PER_FRAME — prevents teleport-style jumps
            //     from emitting hundreds of dabs in one frame.
            //   * Zero-radius gate — defensive.
            if (_radius < 0.01f) return;
            float spacing = _radius * DAB_SPACING_RATIO;
            if (spacing < 0.01f) return;

            // Round-63 — draw-speed throttle. The dab budget accrues
            // `DrawSpeed × MAX_DABS_PER_FRAME` per frame (capped) and
            // each dab spends 1. At DrawSpeed=1 this is the old
            // 16-dabs-per-frame behavior; at 0.25 the brush deposits a
            // quarter as fast, letting the user paint precisely.
            // 0.2.712 (the lead, 10-05: same bug class as the V-tools' 0.2.709 fix): the budget grew PER FRAME, so the
            // brush drew twice as fast at 120 fps as at 60. It now grows per SECOND, at the old 60 fps rate.
            _dabBudget = Mathf.Min(
                _dabBudget + TileBrushModes.DrawSpeed * MAX_DABS_PER_FRAME * DRAW_SPEED_REFERENCE_FPS * Time.deltaTime,
                MAX_DABS_PER_FRAME);

            while (_dabBudget >= 1f)
            {
                float pdx = _brushPos.x - _lastDabPos.x;
                float pdz = _brushPos.z - _lastDabPos.z;
                float length = Mathf.Sqrt(pdx * pdx + pdz * pdz);
                if (length < spacing) break; // carry remainder to next frame

                // Step one spacing along the segment.
                float frac = spacing / length;
                Vector3 nextDab = new Vector3(
                    _lastDabPos.x + pdx * frac,
                    Mathf.Lerp(_lastDabPos.y, _brushPos.y, frac),
                    _lastDabPos.z + pdz * frac);

                DispatchBrushAt(nextDab);
                _lastDabPos = nextDab;
                _dabBudget -= 1f;
            }
        }

        // Round-63 — fractional dab allowance for the draw-speed throttle.
        private float _dabBudget;

        // Dispatch the cached brush kind at a specific world position.
        // Used by both the per-frame final dispatch and the dab pump.
        //
        // 2026-05-26 round-32: honors `TileBrushModes` global toggles:
        //   * `HeightLock = Locked` — replaces the per-dab cursor.y with
        //     the first-click `_height` so dragging off a cliff keeps the
        //     brush at the original ruler plane (RPG-maker behavior).
        //   * `PlacementSkirt = Floating` — forces islandMode=true so
        //     all dabs in the stroke get the floating-island underside.
        //   * `PlacementSkirt = Connected` — default; islandMode comes
        //     from the per-tool flag (existing `*_island` commands).
        private void DispatchBrushAt(Vector3 center)
        {
            // Height-lock: in Locked mode, force center.y to the first-
            // click Y so the brush stays on the click plane.
            if (TileBrushModes.CurrentHeightLock == TileBrushModes.HeightLock.Locked
                && _strokeActive
                && _kind == BrushKind.Flat)
            {
                // Only Flat brush benefits from height-lock; Raise/Lower/
                // Smooth read their own "delta from current height"
                // semantics where locking the cursor Y would be wrong.
                center = new Vector3(center.x, _height, center.z);
            }

            // Placement skirt: Floating mode forces island flag for the dispatch.
            bool effectiveIslandMode = _islandMode
                || TileBrushModes.CurrentPlacementSkirt == TileBrushModes.PlacementSkirt.Floating;

            switch (_kind)
            {
                case BrushKind.Flat:
                    TileBrushRPC.SendFlat(center, _radius, _height, square: false, islandMode: effectiveIslandMode);
                    break;
                case BrushKind.Raise:
                    TileBrushRPC.SendRaise(center, _radius, _height, _falloff, islandMode: effectiveIslandMode);
                    break;
                case BrushKind.Lower:
                    TileBrushRPC.SendRaise(center, _radius, -Mathf.Abs(_height), _falloff, islandMode: effectiveIslandMode);
                    break;
                case BrushKind.Paint:
                    TileBrushRPC.SendPaint(center, _radius, _color, square: false);
                    break;
                case BrushKind.Clear:
                    TileBrushRPC.SendClear(center, _radius);
                    break;
                case BrushKind.Delete:
                    // Delete is intentionally not drag-repeatable — it's
                    // destructive and the user should explicitly click each
                    // tile they want gone. Treat it as a no-op for drag.
                    break;
                case BrushKind.Smooth:
                    // Phase B-2: dedicated Smooth brush. Strength comes
                    // from _falloff (repurposed as a 0..1 intensity arg
                    // from Cmd_Smooth) so the existing arg pipeline doesn't
                    // need new fields. TileBrush.BrushSmooth handles the
                    // iteration_strengths + origHeights-snapshot logic
                    // (Blender Smooth brush parity).
                    TileBrushRPC.SendSmooth(center, _radius, _falloff);
                    break;
                case BrushKind.Grab:
                    // Round-37 B-6: Grab brush. dragDeltaY = cursor's
                    // cumulative Y delta since stroke start. center.y
                    // here is the LIVE cursor's raycast Y; the anchor was
                    // captured on LMB-down. Brush snapshot's origHeights
                    // is the target baseline; this delta adds on top.
                    TileBrushRPC.SendGrab(center, _radius, center.y - _grabAnchorCursorY);
                    break;
                case BrushKind.Noise:
                    // Round-37 B-7: Noise brush. _height holds intensity,
                    // _falloff holds scale (Perlin frequency). Re-applied
                    // every dab — noise is deterministic on world XZ so
                    // the pattern stays stable across the stroke.
                    TileBrushRPC.SendNoise(center, _radius, _height, _falloff);
                    break;
            }
        }

        // Mirror of TileHammerCompat.IsOurToolActive, but we don't want a
        // cross-file static dependency to avoid load-order issues. Re-check
        // the same Piece.m_name set. Cheap (small HashSet lookup).
        private static bool IsOurBrushToolSelected(Player player)
        {
            var pt = player.m_buildPieces;
            if (pt == null) return false;
            var piece = pt.GetSelectedPiece();
            if (piece == null) return false;
            // Only drag-repeat the actual brush tools, not Delete or info.
            switch (piece.m_name)
            {
                case "Floor":
                case "Raise Terrain":
                case "Raise (Sharp)":
                case "Lower Terrain":
                case "Paint Dirt":
                case "Paint Cultivated":
                case "Paint Paved":
                case "Clear Brushed Area":
                // Round-30 island variants
                case "Floor (Floating Island)":
                case "Raise Terrain (Floating Island)":
                case "Lower Terrain (Floating Island)":
                // Round-31 B-2 — dedicated Smooth brush
                case "Smooth Terrain":
                // Round-37 — Grab + Noise (Blender ports)
                case "Grab Terrain":
                case "Noise Terrain":
                    return true;
                default:
                    return false;
            }
        }
    }
}
