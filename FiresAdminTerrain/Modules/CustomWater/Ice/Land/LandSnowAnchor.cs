using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// ZDO-backed anchor for land-snow persistence. Mirrors LandIceAnchor
    /// exactly; only the keyspace (`va_landsnow_*`) and the linked
    /// controller type differ.
    ///
    /// Round-26 plan §2.2.
    /// </summary>
    public class LandSnowAnchor : MonoBehaviour
    {
        #region ZDO Keys

        private static readonly int s_isAnchorHash    = "va_landsnow_is_anchor".GetStableHashCode();
        private static readonly int s_versionHash     = "va_landsnow_v".GetStableHashCode();
        private static readonly int s_footprintsHash  = "va_landsnow_fp".GetStableHashCode();
        private static readonly int s_noiseSeedHash   = "va_landsnow_seed".GetStableHashCode();
        private static readonly int s_heightsHash     = "va_landsnow_heights".GetStableHashCode();
        private static readonly int s_gridParamsHash  = "va_landsnow_grid".GetStableHashCode();

        private const int CURRENT_VERSION = 1;

        #endregion

        #region Fields

        private ZNetView m_nview;
        private LandSnowController m_linkedController;
        private bool m_isStripped;
        private bool m_isNewlyCreated;
        private bool m_restoreAttempted;

        #endregion

        public LandSnowController LinkedController => m_linkedController;
        public ZNetView NView => m_nview;

        #region Unity Lifecycle

        private void Awake()
        {
            m_nview = GetComponent<ZNetView>();
            if (m_nview == null)
            {
                FiresLogger.LogError("[LandSnowAnchor] No ZNetView on anchor GO!");
                Destroy(this);
                return;
            }
            if (m_nview.GetZDO() == null) return;
            StripToInvisible();
        }

        private void Start()
        {
            if (m_nview == null || m_nview.GetZDO() == null) return;
            if (!m_isStripped) StripToInvisible();

            if (m_isNewlyCreated || m_linkedController != null) return;
            if (m_restoreAttempted) return;
            m_restoreAttempted = true;

            int version = m_nview.GetZDO().GetInt(s_versionHash, 0);
            bool isAnchor = m_nview.GetZDO().GetBool(s_isAnchorHash, false);
            if (!isAnchor || version <= 0) return;

            EnsureLinkedController();
            if (m_linkedController != null)
                m_linkedController.RestoreFromAnchor(this);
        }

        private void OnDestroy()
        {
            m_linkedController = null;
        }

        #endregion

        private void StripToInvisible()
        {
            if (m_isStripped) return;
            m_isStripped = true;

            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r != null) r.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>(true))
                if (c != null) c.enabled = false;

            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (child != null) child.gameObject.SetActive(false);
            }

            gameObject.layer = 2;
        }

        public void MarkAsNewAnchor()
        {
            m_isNewlyCreated = true;
            m_restoreAttempted = true;
        }

        public void LinkToController(LandSnowController controller)
        {
            m_linkedController = controller;

            if (m_nview != null && m_nview.IsValid() && m_nview.IsOwner())
            {
                var zdo = m_nview.GetZDO();
                if (zdo != null)
                {
                    zdo.Set(s_isAnchorHash, true);
                    zdo.Set(s_versionHash, CURRENT_VERSION);
                }
            }
        }

        private void EnsureLinkedController()
        {
            if (m_linkedController != null) return;
            m_linkedController = gameObject.GetComponent<LandSnowController>();
            if (m_linkedController == null)
                m_linkedController = gameObject.AddComponent<LandSnowController>();
            m_linkedController.BindAnchor(this);
        }

        public void WriteData(float noiseSeed, byte[] footprintsPacked, byte[] gridParamsPacked, byte[] heightsPacked)
        {
            if (m_nview == null || !m_nview.IsValid() || !m_nview.IsOwner()) return;
            var zdo = m_nview.GetZDO();
            if (zdo == null) return;

            try
            {
                zdo.Set(s_isAnchorHash, true);
                zdo.Set(s_versionHash, CURRENT_VERSION);
                zdo.Set(s_noiseSeedHash, noiseSeed);
                if (footprintsPacked  != null) zdo.Set(s_footprintsHash,  footprintsPacked);
                if (gridParamsPacked  != null) zdo.Set(s_gridParamsHash,  gridParamsPacked);
                if (heightsPacked     != null) zdo.Set(s_heightsHash,     heightsPacked);
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[LandSnowAnchor] WriteData failed: {ex.Message}");
            }
        }

        public bool ReadData(out float noiseSeed, out byte[] footprintsPacked, out byte[] gridParamsPacked, out byte[] heightsPacked)
        {
            noiseSeed = 0f;
            footprintsPacked = null;
            gridParamsPacked = null;
            heightsPacked = null;

            if (m_nview == null) return false;
            var zdo = m_nview.GetZDO();
            if (zdo == null) return false;
            if (!zdo.GetBool(s_isAnchorHash, false)) return false;

            try
            {
                noiseSeed         = zdo.GetFloat(s_noiseSeedHash, 0f);
                footprintsPacked  = zdo.GetByteArray(s_footprintsHash);
                gridParamsPacked  = zdo.GetByteArray(s_gridParamsHash);
                heightsPacked     = zdo.GetByteArray(s_heightsHash);
                return footprintsPacked != null && footprintsPacked.Length > 0;
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[LandSnowAnchor] ReadData failed: {ex.Message}");
                return false;
            }
        }

        public void DestroyPermanently()
        {
            m_linkedController = null;

            if (m_nview != null && m_nview.IsValid())
            {
                var zdo = m_nview.GetZDO();
                if (zdo != null)
                {
                    zdo.Set(s_isAnchorHash, false);
                    zdo.Set(s_versionHash, 0);
                }
            }

            if (ZNetScene.instance != null && m_nview != null && m_nview.IsValid())
            {
                try
                {
                    ZNetScene.instance.Destroy(gameObject);
                    return;
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[LandSnowAnchor] ZNetScene.Destroy failed: {ex.Message}");
                }
            }

            Destroy(gameObject);
        }

        /// <summary>
        /// Spawns a fresh VALandSnow anchor + LandSnowController pair at
        /// `position`. Callers then call Initialize(position, radius)
        /// on the returned controller.
        /// </summary>
        public static LandSnowController SpawnNew(Vector3 position)
        {
            if (ZNetScene.instance == null)
            {
                FiresLogger.LogError("[LandSnowAnchor] SpawnNew: ZNetScene null.");
                return null;
            }

            LandSnowAnchorPrefab.Ensure();
            var prefab = LandSnowAnchorPrefab.Prefab;
            if (prefab == null)
            {
                FiresLogger.LogError("[LandSnowAnchor] SpawnNew: VALandSnow prefab not available.");
                return null;
            }

            var go = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            go.name = $"LandSnow_{DateTime.UtcNow.Ticks}";

            var anchor = go.GetComponent<LandSnowAnchor>();
            if (anchor == null) anchor = go.AddComponent<LandSnowAnchor>();
            anchor.MarkAsNewAnchor();

            var controller = go.GetComponent<LandSnowController>();
            if (controller == null) controller = go.AddComponent<LandSnowController>();

            anchor.LinkToController(controller);
            controller.BindAnchor(anchor);

            return controller;
        }
    }
}
