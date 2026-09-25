using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// ZDO-backed anchor for land-ice persistence.
    ///
    /// ARCHITECTURE (round-26 plan §1.2):
    /// - ONE invisible VALandIce anchor per land-ice disk-cluster
    /// - Anchor stores footprints, noise seed, height grid, bounds in ZDO
    /// - On world load, anchor.Start lazily attaches a LandIceController
    ///   and replays the stored data via Restore()
    /// - On placement, IcePlacementTool clones VALandIce → adds anchor
    ///   (via the prefab) + LandIceController → controller calls
    ///   Save() each time heights/footprints change
    ///
    /// HASH KEYS are deliberately re-namespaced from the legacy `va_land_ice_*`
    /// scheme so any orphan keys on old ice1 ZDOs are ignored — user
    /// confirmed existing snow/ice is deleted before migration so no
    /// backward-compat shim is needed.
    /// </summary>
    public class LandIceAnchor : MonoBehaviour
    {
        #region ZDO Keys

        private static readonly int s_isAnchorHash      = "va_landice_is_anchor".GetStableHashCode();
        private static readonly int s_versionHash       = "va_landice_v".GetStableHashCode();
        private static readonly int s_footprintsHash    = "va_landice_fp".GetStableHashCode();
        private static readonly int s_noiseSeedHash     = "va_landice_seed".GetStableHashCode();
        private static readonly int s_heightsHash       = "va_landice_heights".GetStableHashCode();
        private static readonly int s_gridParamsHash    = "va_landice_grid".GetStableHashCode();

        private const int CURRENT_VERSION = 1;

        #endregion

        #region Fields

        private ZNetView m_nview;
        private LandIceController m_linkedController;
        private bool m_isStripped;
        private bool m_isNewlyCreated;
        private bool m_restoreAttempted;

        #endregion

        #region Properties

        public LandIceController LinkedController => m_linkedController;
        public ZNetView NView => m_nview;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            m_nview = GetComponent<ZNetView>();
            if (m_nview == null)
            {
                FiresLogger.LogError("[LandIceAnchor] No ZNetView on anchor GO!");
                Destroy(this);
                return;
            }

            // Wait for ZDO to be valid before strip / restore.
            if (m_nview.GetZDO() == null) return;

            StripToInvisible();
        }

        private void Start()
        {
            if (m_nview == null || m_nview.GetZDO() == null) return;
            if (!m_isStripped) StripToInvisible();

            // Newly created by IcePlacementTool — controller already attached
            // and initialized. Don't try to restore.
            if (m_isNewlyCreated || m_linkedController != null) return;
            if (m_restoreAttempted) return;
            m_restoreAttempted = true;

            // Spawned from a persisted ZDO — try to restore.
            int version = m_nview.GetZDO().GetInt(s_versionHash, 0);
            bool isAnchor = m_nview.GetZDO().GetBool(s_isAnchorHash, false);
            if (!isAnchor || version <= 0) return;

            EnsureLinkedController();
            if (m_linkedController != null)
            {
                m_linkedController.RestoreFromAnchor(this);
            }
        }

        private void OnDestroy()
        {
            m_linkedController = null;
        }

        #endregion

        #region Visibility Strip

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

            gameObject.layer = 2; // Ignore Raycast
        }

        #endregion

        #region Linking

        public void MarkAsNewAnchor()
        {
            m_isNewlyCreated = true;
            m_restoreAttempted = true;
        }

        public void LinkToController(LandIceController controller)
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
            m_linkedController = gameObject.GetComponent<LandIceController>();
            if (m_linkedController == null)
                m_linkedController = gameObject.AddComponent<LandIceController>();
            m_linkedController.BindAnchor(this);
        }

        #endregion

        #region Data Accessors

        // Called by LandIceController.Save() each time the disk mutates.
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
                FiresLogger.LogError($"[LandIceAnchor] WriteData failed: {ex.Message}");
            }
        }

        // Called by LandIceController.RestoreFromAnchor() on world load.
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
                FiresLogger.LogError($"[LandIceAnchor] ReadData failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Permanent Deletion

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
                    FiresLogger.LogWarning($"[LandIceAnchor] ZNetScene.Destroy failed: {ex.Message}");
                }
            }

            Destroy(gameObject);
        }

        #endregion

        #region Static Factory

        /// <summary>
        /// Spawns a fresh VALandIce anchor at `position` and adds a
        /// LandIceController + this LandIceAnchor pair. Called by
        /// IcePlacementTool.PlaceIce on the land-ice branch.
        ///
        /// Returns the controller; callers then call Initialize(...)
        /// on it as normal — Save() will route to this anchor's ZDO.
        /// </summary>
        public static LandIceController SpawnNew(Vector3 position)
        {
            if (ZNetScene.instance == null)
            {
                FiresLogger.LogError("[LandIceAnchor] SpawnNew: ZNetScene null.");
                return null;
            }

            // Ensure prefab is registered (idempotent — also runs on ZNetScene.Awake).
            LandIceAnchorPrefab.Ensure();

            var prefab = LandIceAnchorPrefab.Prefab;
            if (prefab == null)
            {
                FiresLogger.LogError("[LandIceAnchor] SpawnNew: VALandIce prefab not available.");
                return null;
            }

            var go = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            go.name = $"LandIce_{DateTime.UtcNow.Ticks}";

            var anchor = go.GetComponent<LandIceAnchor>();
            if (anchor == null) anchor = go.AddComponent<LandIceAnchor>();
            anchor.MarkAsNewAnchor();

            var controller = go.GetComponent<LandIceController>();
            if (controller == null) controller = go.AddComponent<LandIceController>();

            anchor.LinkToController(controller);
            controller.BindAnchor(anchor);

            return controller;
        }

        #endregion
    }
}
