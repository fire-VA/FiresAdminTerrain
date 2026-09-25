using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    /// <summary>
    /// 2026-05-26 round-32 — global brush placement modes.
    ///
    /// Two toggles that change how all `va_brush_flat / _raise / _lower`
    /// (and their drag re-dispatches) write tiles. The toggles are GLOBAL,
    /// session-scoped, controlled via console + (soon) a hammer right-
    /// click menu. Persisted to PlayerPrefs so they survive logout/login.
    ///
    /// `HeightLockMode = Locked`  — drag holds the ruler's first-click Y
    ///   regardless of cursor terrain. Matches RPG-maker behavior:
    ///   click on flat ground → drag off cliff → mesh extends FLAT at
    ///   the click Y, doesn't bob with the lower terrain. Useful for
    ///   building horizontal platforms across uneven terrain.
    ///
    /// `HeightLockMode = TrackCursor` — drag uses the current raycast Y
    ///   as the brush center. Matches what FAT did before this round.
    ///   Useful when you want the brush to ride the surface (smoothing,
    ///   following slopes).
    ///
    /// `PlacementSkirt = Connected` — mesh underside stretches down to
    ///   meet vanilla terrain (current behavior). Good for tile that
    ///   should look "grown into" the world.
    ///
    /// `PlacementSkirt = Floating` — mesh underside auto-rounds into a
    ///   teardrop dome when ≥50m above terrain (the existing FloatingIsland
    ///   mode, now controllable per-stroke instead of per-tool). When
    ///   floating + drag goes off a cliff, the new tile doesn't sprout a
    ///   skirt connecting to the lower ground.
    /// </summary>
    public static class TileBrushModes
    {
        public enum HeightLock
        {
            /// <summary>Brush re-samples cursor Y on every dab (default).</summary>
            TrackCursor = 0,
            /// <summary>Brush locks Y to the first-click ruler height for the whole stroke.</summary>
            Locked = 1,
        }

        public enum PlacementSkirt
        {
            /// <summary>Underside stretches down to terrain (default).</summary>
            Connected = 0,
            /// <summary>Underside auto-rounds into a teardrop when ≥50m above terrain.</summary>
            Floating = 1,
        }

        /// <summary>
        /// Whether the custom terrain-PAINT ops refresh the vanilla clutter grass.
        ///
        /// `Clear` (default) — after a paint, ClutterSystem.ResetGrass re-rolls the
        ///   grass in-radius so it matches the new paint: dirt / cultivated / paved
        ///   (any RGB channel painted) HIDE the grass; the grass-paint (0,0,0)
        ///   brings it BACK. This is how Infinity Hammer + vanilla terrain paint
        ///   behave, so painted areas read clean.
        ///
        /// `Keep` — paint ops leave the existing grass untouched, so you can lay a
        ///   dirt / paved texture UNDER the current grass for an "overgrown" /
        ///   "ruined" look. (Grass-paint always re-rolls regardless, since its whole
        ///   purpose is to restore grass.)
        /// </summary>
        public enum ClutterClear
        {
            /// <summary>Paint refreshes clutter grass to match the paint (default).</summary>
            Clear = 0,
            /// <summary>Leave existing grass in place (overgrown / ruined areas).</summary>
            Keep = 1,
        }

        private const string PREF_HEIGHT_LOCK = "VATileBrush_HeightLock";
        private const string PREF_PLACEMENT_SKIRT = "VATileBrush_PlacementSkirt";
        private const string PREF_CLUTTER_CLEAR = "VATileBrush_ClutterClear";
        private const string PREF_DRAW_SPEED = "VATileBrush_DrawSpeed";

        private static HeightLock _heightLock = HeightLock.TrackCursor;
        private static PlacementSkirt _skirt = PlacementSkirt.Connected;
        private static ClutterClear _clutterClear = ClutterClear.Clear;
        private static float _drawSpeed = 1f;
        private static bool _loaded;

        /// <summary>
        /// Round-63 — global draw-speed multiplier for the brush drag
        /// pumps (terrain AND water). 1.0 = full speed (default); lower
        /// values emit proportionally fewer dabs per frame so a drag
        /// stroke deposits slower — the user asked for a way to "slow
        /// down how fast it draws the mesh to allow us to be more
        /// precise". Clamped 0.05–1. Set via `va_brush_drawspeed`.
        /// </summary>
        public static float DrawSpeed
        {
            get { EnsureLoaded(); return _drawSpeed; }
            set
            {
                EnsureLoaded();
                _drawSpeed = Mathf.Clamp(value, 0.05f, 1f);
                PlayerPrefs.SetFloat(PREF_DRAW_SPEED, _drawSpeed);
                PlayerPrefs.Save();
                FiresLogger.LogVerbose($"[TileBrushModes] DrawSpeed → {_drawSpeed:F2}");
            }
        }

        public static HeightLock CurrentHeightLock
        {
            get { EnsureLoaded(); return _heightLock; }
            set
            {
                EnsureLoaded();
                _heightLock = value;
                PlayerPrefs.SetInt(PREF_HEIGHT_LOCK, (int)value);
                PlayerPrefs.Save();
                FiresLogger.LogInfo($"[TileBrushModes] HeightLock → {value}");
            }
        }

        public static PlacementSkirt CurrentPlacementSkirt
        {
            get { EnsureLoaded(); return _skirt; }
            set
            {
                EnsureLoaded();
                _skirt = value;
                PlayerPrefs.SetInt(PREF_PLACEMENT_SKIRT, (int)value);
                PlayerPrefs.Save();
                FiresLogger.LogInfo($"[TileBrushModes] PlacementSkirt → {value}");
            }
        }

        public static ClutterClear CurrentClutterClear
        {
            get { EnsureLoaded(); return _clutterClear; }
            set
            {
                EnsureLoaded();
                _clutterClear = value;
                PlayerPrefs.SetInt(PREF_CLUTTER_CLEAR, (int)value);
                PlayerPrefs.Save();
                FiresLogger.LogInfo($"[TileBrushModes] ClutterClear → {value}");
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _heightLock = (HeightLock)PlayerPrefs.GetInt(PREF_HEIGHT_LOCK, (int)HeightLock.TrackCursor);
            _skirt = (PlacementSkirt)PlayerPrefs.GetInt(PREF_PLACEMENT_SKIRT, (int)PlacementSkirt.Connected);
            _clutterClear = (ClutterClear)PlayerPrefs.GetInt(PREF_CLUTTER_CLEAR, (int)ClutterClear.Clear);
            _drawSpeed = Mathf.Clamp(PlayerPrefs.GetFloat(PREF_DRAW_SPEED, 1f), 0.05f, 1f);
        }

        /// <summary>
        /// Toggle the height-lock mode; returns the new mode.
        /// </summary>
        public static HeightLock ToggleHeightLock()
        {
            CurrentHeightLock = (CurrentHeightLock == HeightLock.TrackCursor)
                ? HeightLock.Locked
                : HeightLock.TrackCursor;
            return CurrentHeightLock;
        }

        /// <summary>
        /// Toggle the placement-skirt mode; returns the new mode.
        /// </summary>
        public static PlacementSkirt TogglePlacementSkirt()
        {
            CurrentPlacementSkirt = (CurrentPlacementSkirt == PlacementSkirt.Connected)
                ? PlacementSkirt.Floating
                : PlacementSkirt.Connected;
            return CurrentPlacementSkirt;
        }

        /// <summary>
        /// Toggle whether paint ops clear clutter grass; returns the new mode.
        /// </summary>
        public static ClutterClear ToggleClutterClear()
        {
            CurrentClutterClear = (CurrentClutterClear == ClutterClear.Clear)
                ? ClutterClear.Keep
                : ClutterClear.Clear;
            return CurrentClutterClear;
        }
    }
}
