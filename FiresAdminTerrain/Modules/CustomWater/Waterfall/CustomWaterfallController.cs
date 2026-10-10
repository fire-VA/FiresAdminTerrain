using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Custom waterfall - falling water from cliff edges.
    /// Usually spawned automatically by rivers when they detect cliff edges.
    /// 
    /// NOTE: Particle effects have been removed due to ZNetScene issues.
    /// Waterfalls now use mesh-based rendering only.
    /// 
    /// Use for:
    /// - Natural cliff waterfalls
    /// - Water features cascading down rocks
    /// - Dramatic terrain drops
    /// </summary>
    public class CustomWaterfallController : CustomWaterBase
    {
        #region Waterfall-Specific Configuration

        [Header("Waterfall Settings")]
        [Tooltip("Height of the waterfall drop in meters")]
        public float DropHeight = 10f;

        [Tooltip("Water flow rate (volume per second)")]
        public float FlowRate = 5f;

        [Tooltip("Width of the waterfall")]
        public float Width = 2f;

        [Tooltip("Parent river that spawned this waterfall")]
        public CustomRiverController ParentRiver;

        #endregion

        #region Runtime State

        private Vector3 _landingPosition;
        private bool _hasLanded = false;
        private float _landingCheckTimer = 0f;
        private const float LANDING_CHECK_INTERVAL = 0.2f;

        #endregion

        #region Implementation

        public override CustomWaterType GetWaterType() => CustomWaterType.Waterfall;

        protected override void Awake()
        {
            base.Awake();
        }

        protected override void Start()
        {
            base.Start();

            // Waterfalls can use mesh rendering
            // TODO: Implement proper waterfall mesh generation
        }

        public override float GetWaterSurfaceAt(Vector3 worldPos)
        {
            // Waterfalls don't have a traditional surface for swimming
            // But we detect if position is within the falling water column
            if (!IsInsideFallColumn(worldPos))
                return float.MinValue;

            // Return a value that triggers "in water" detection
            return worldPos.y;
        }

        public override void UpdateWater(float dt)
        {
            // Check for landing if not already landed
            if (!_hasLanded)
            {
                _landingCheckTimer += dt;
                if (_landingCheckTimer >= LANDING_CHECK_INTERVAL)
                {
                    CheckForLanding();
                    _landingCheckTimer = 0f;
                }
            }

            // Check for collisions with objects
            CheckObjectCollisions();
        }

        protected override void GenerateMesh()
        {
            // Setup fall column trigger
            SetupFallColumnTrigger();
        }

        #endregion

        #region Landing Detection

        private void CheckForLanding()
        {
            // Sample terrain at XZ of waterfall, not at fallEnd Y position
            Vector3 samplePos = new Vector3(transform.position.x, 0f, transform.position.z);
            float terrainHeight = SampleTerrainHeight(samplePos);

            // If terrain is invalid or very low, cap the drop height
            if (terrainHeight < -1000f)
            {
                if (DropHeight > 100f)
                {
                    _hasLanded = true; // Give up - no valid terrain found
                    FiresLogger.LogInfo($"[CustomWaterfall] No valid terrain found, capping at {DropHeight}m");
                }
                else
                {
                    DropHeight += 2f;
                    FiresLogger.LogInfo($"[CustomWaterfall] Extended drop to {DropHeight}m, terrain invalid");
                }
                return;
            }

            // Calculate actual drop from waterfall origin to terrain
            float actualDrop = transform.position.y - terrainHeight;

            // If we've reached terrain level, check slope
            if (actualDrop <= DropHeight + 2f)
            {
                Vector3 checkPos = new Vector3(transform.position.x, terrainHeight, transform.position.z);
                float slopeAngle = CalculateTerrainSlope(checkPos);

                if (slopeAngle < 30f)
                {
                    // Terrain is flat enough - we've landed
                    _landingPosition = checkPos;
                    _hasLanded = true;

                    // Set final drop height
                    DropHeight = Mathf.Max(actualDrop, 1f);

                    // Notify parent river
                    ParentRiver?.OnWaterfallLanded(this, _landingPosition, FlowRate);

                    FiresLogger.LogInfo($"[CustomWaterfall] Landed at {_landingPosition}, slope={slopeAngle:F1}\u00B0, drop={DropHeight:F1}m");
                }
                else if (DropHeight < 100f)
                {
                    // Still steep - extend drop height
                    DropHeight += 2f;
                    FiresLogger.LogInfo($"[CustomWaterfall] Extended drop to {DropHeight:F1}m, slope={slopeAngle:F1}\u00B0");
                }
                else
                {
                    // Cap at 100m
                    _hasLanded = true;
                    FiresLogger.LogInfo($"[CustomWaterfall] Capped at max drop height {DropHeight:F1}m");
                }
            }
            else if (DropHeight < 100f)
            {
                // Haven't reached terrain yet, extend
                DropHeight = Mathf.Min(actualDrop + 2f, 100f);
            }
        }

        private float CalculateTerrainSlope(Vector3 pos)
        {
            // Sample heights in 4 directions
            float offset = 1f;
            float centerH = SampleTerrainHeight(pos);
            float northH = SampleTerrainHeight(pos + Vector3.forward * offset);
            float southH = SampleTerrainHeight(pos + Vector3.back * offset);
            float eastH = SampleTerrainHeight(pos + Vector3.right * offset);
            float westH = SampleTerrainHeight(pos + Vector3.left * offset);

            // Calculate max slope
            float maxDiff = Mathf.Max(
                Mathf.Abs(northH - centerH),
                Mathf.Abs(southH - centerH),
                Mathf.Abs(eastH - centerH),
                Mathf.Abs(westH - centerH)
            );

            // Convert to angle
            return Mathf.Atan2(maxDiff, offset) * Mathf.Rad2Deg;
        }

        #endregion

        #region Collision Detection

        private bool IsInsideFallColumn(Vector3 worldPos)
        {
            // Check if position is within the falling water column
            Vector3 local = transform.InverseTransformPoint(worldPos);

            // Horizontal check (within width)
            if (Mathf.Abs(local.x) > Width * 0.5f)
                return false;
            if (Mathf.Abs(local.z) > 0.5f)
                return false;

            // Vertical check (within drop height)
            if (local.y > 0.5f || local.y < -DropHeight - 0.5f)
                return false;

            return true;
        }

        private void SetupFallColumnTrigger()
        {
            // Setup a trigger collider for the fall column
            if (m_swimCollider != null)
            {
                m_swimCollider.size = new Vector3(Width, DropHeight, 1f);
                m_swimCollider.center = new Vector3(0f, -DropHeight * 0.5f, 0f);
            }
        }

        private void CheckObjectCollisions()
        {
            // Check for objects passing through the waterfall column
            Collider[] hitColliders = Physics.OverlapBox(
                transform.position + Vector3.down * (DropHeight * 0.5f),
                new Vector3(Width * 0.5f, DropHeight * 0.5f, 0.5f),
                transform.rotation,
                LayerMask.GetMask("character", "item", "piece")
            );

            // Could spawn splash effects here if needed
        }

        #endregion
    }
}
