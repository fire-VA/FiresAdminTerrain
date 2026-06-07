using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Clutter;

namespace VerdantsAscent.Terrain.Snow
{
    /// <summary>
    /// Stamps runtime-only trails behind the player as they walk through snow.
    ///
    /// Each stamp:
    ///   1. Hides snow clutter instances in a small radius (disables rendering,
    ///      does NOT destroy � they reappear on zone reload).
    ///   2. Clears vanilla ClutterSystem grass
    ///      (<see cref="ClutterSystem.ResetGrass"/>).
    ///   3. Adds a non-persistent "dirty snow" biome paint so the terrain
    ///      shader shows packed snow underneath the cleared area
    ///      (<see cref="SnowTrailPaint.AddTrailPaint"/>).
    ///
    /// Trails are non-persistent � they stay in memory until the zone is
    /// unloaded, giving the player a fresh blanket of snow when they return.
    /// </summary>
    public class SnowTrailController : MonoBehaviour
    {
        /// <summary>
        /// Radius of each trail stamp (meters). Narrow footprint trail.
        /// </summary>
        [Header("Trail Settings")]
        public float TrailRadius = 0.35f;

        /// <summary>
        /// Minimum distance the player must move before a new stamp is placed.
        /// Prevents spamming stamps while standing still or micro-moving.
        /// </summary>
        public float MinStampDistance = 0.25f;

        /// <summary>
        /// Whether to also paint the non-persistent dirty-snow biome paint
        /// underneath cleared clutter for a packed-snow visual.
        /// </summary>
        public bool PaintDirtySnow = true;

        private Vector3 _lastStampPos;
        private bool _initialized;
        private int _frameCounter;

        /// <summary>All stamp positions this session � for debug / future use.</summary>
        private readonly List<Vector3> _trailPositions = new List<Vector3>();

        /// <summary>
        /// Global singleton. Only one controller needed since we track the
        /// local player. Created by the snow console commands.
        /// </summary>
        private static SnowTrailController _instance;
        public static SnowTrailController Instance => _instance;

        /// <summary>
        /// Whether trail stamping is currently active.
        /// </summary>
        public static bool IsActive => _instance != null && _instance._initialized;

        /// <summary>
        /// Enables snow trail stamping. Creates the singleton if needed.
        /// </summary>
        public static void Enable()
        {
            if (_instance == null)
            {
                var go = new GameObject("SnowTrailController");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<SnowTrailController>();
            }
            _instance._initialized = true;
            _instance._lastStampPos = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position
                : Vector3.zero;

            Debug.Log("[SnowTrailController] Enabled");
        }

        /// <summary>
        /// Disables trail stamping and clears all trail paints (snow respawns).
        /// Also re-shows any hidden clutter objects.
        /// </summary>
        public static void Disable()
        {
            if (_instance != null)
            {
                _instance._initialized = false;
                _instance._trailPositions.Clear();
            }
            SnowTrailPaint.ClearAllTrailPaints();
            VerdantsAscent.Terrain.Clutter.ClutterManager.ShowAllHiddenClutter();
            Debug.Log("[SnowTrailController] Disabled � trail paints cleared");
        }

        /// <summary>
        /// Clears trail paints and re-shows hidden clutter without disabling the controller.
        /// Snow clutter will respawn on next zone load from its persistent data.
        /// </summary>
        public static void ResetTrails()
        {
            if (_instance != null)
                _instance._trailPositions.Clear();
            SnowTrailPaint.ClearAllTrailPaints();
            VerdantsAscent.Terrain.Clutter.ClutterManager.ShowAllHiddenClutter();
            Debug.Log("[SnowTrailController] Trails reset");
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                SnowTrailPaint.ClearAllTrailPaints();
                VerdantsAscent.Terrain.Clutter.ClutterManager.ShowAllHiddenClutter();
                _instance = null;
            }
        }

        private void Update()
        {
            // Tick debounced heightmap regeneration
            SnowTrailPaint.TickRegen();

            if (!_initialized) return;

            // Check every 3 frames to save CPU
            _frameCounter++;
            if (_frameCounter % 3 != 0) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            Vector3 playerPos = player.transform.position;

            // Only stamp if moved enough
            float dist = Vector3.Distance(
                new Vector3(playerPos.x, 0, playerPos.z),
                new Vector3(_lastStampPos.x, 0, _lastStampPos.z));

            if (dist < MinStampDistance) return;

            // Only stamp if on the ground (not swimming, not falling)
            if (player.IsSwimming() || !player.IsOnGround()) return;

            // Only stamp if the player is in a snow clutter area
            if (!IsInSnowArea(playerPos)) return;

            StampTrail(playerPos);
            _lastStampPos = playerPos;
        }

        /// <summary>
        /// Checks if there are any painted snow clutter instances near the position.
        /// Uses HasSnowClutterNear which also detects hidden (but not destroyed) objects.
        /// </summary>
        private bool IsInSnowArea(Vector3 pos)
        {
            return VerdantsAscent.Terrain.Clutter.ClutterManager.HasSnowClutterNear(pos, 3f);
        }

        private void StampTrail(Vector3 position)
        {
            // 1. Hide snow clutter (our painted instances) in the stamp radius.
            //    Runtime-only: disables rendering, does not destroy objects or persistence.
            //    Hidden objects re-appear when the zone reloads from persistent data.
            VerdantsAscent.Terrain.Clutter.ClutterManager.HideClutterInRadius(position, TrailRadius);

            // 2. Clear vanilla ClutterSystem grass in the stamp radius
            if (ClutterSystem.instance != null)
                ClutterSystem.instance.ResetGrass(position, TrailRadius);

            // 3. Optionally paint dirty-snow biome overlay (non-persistent)
            if (PaintDirtySnow)
                SnowTrailPaint.AddTrailPaint(position, TrailRadius);

            _trailPositions.Add(position);
        }
    }
}
