using UnityEngine;

namespace VerdantsAscent.Modules.Utilities
{
    /// <summary>
    /// Generic-namespace helper for destroying GameObjects that may carry a
    /// <see cref="ZNetView"/>. This exists so any module (Companions, Water,
    /// Terrain, Territories, etc.) can route through the proper vanilla
    /// network-aware teardown path instead of calling <c>Object.Destroy</c>
    /// directly.
    ///
    /// WHY THIS MATTERS:
    /// Valheim's <c>ZNetScene</c> tracks every networked object in a dictionary
    /// keyed by <see cref="ZDO"/>. Calling <c>UnityEngine.Object.Destroy</c> on a
    /// GameObject that owns a <see cref="ZNetView"/> tears down the Unity side
    /// without removing the entry from <c>ZNetScene.m_instances</c>. The next
    /// time <c>ZNetScene.RemoveObjects</c> iterates that dictionary it hits a
    /// destroyed Unity reference and throws a NullReferenceException - the
    /// exact stack trace you see when a wild creature dies, a water/terrain
    /// combiner gets cleaned up, etc.
    ///
    /// USAGE:
    ///   - Replace <c>UnityEngine.Object.Destroy(thing.gameObject)</c> with
    ///     <c>NetworkObjectHelper.SafeDestroy(thing.gameObject)</c> for any
    ///     object that *might* carry a ZNetView.
    ///   - Pure local-only objects (UI, textures, materials, particles without
    ///     a ZNetView) should keep using <c>Object.Destroy</c> - this helper
    ///     also handles them but adds a tiny GetComponent overhead.
    /// </summary>
    public static class NetworkObjectHelper
    {
        // A networked prefab's ZDO, set up the way ZNetView.Awake sets one, for an object no peer has built yet.
        // ZDOMan.CreateNewZDO leaves the prefab at 0: such a ZDO is never built on any machine and no prefab scan finds it.
        public static ZDO CreateZdo(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            var view = prefab != null ? prefab.GetComponent<ZNetView>() : null;
            if (view == null || ZDOMan.instance == null) return null;
            int hash = prefab.name.GetStableHashCode();
            var zdo = ZDOMan.instance.CreateNewZDO(position, hash);
            zdo.Persistent = view.m_persistent;
            zdo.Type = view.m_type;
            zdo.Distant = view.m_distant;
            zdo.SetPrefab(hash);
            zdo.SetRotation(rotation);
            return zdo;
        }

        /// <summary>
        /// Destroys a GameObject through <see cref="ZNetScene"/> if it carries a
        /// valid <see cref="ZNetView"/>; otherwise falls back to
        /// <see cref="Object.Destroy(Object)"/>. Safe to call with null.
        /// </summary>
        public static void SafeDestroy(GameObject go)
        {
            if (go == null) return;

            ZNetView nview = null;
            try { nview = go.GetComponent<ZNetView>(); } catch { /* destroyed mid-frame */ }

            // No ZNetView - purely local Unity object, regular Destroy is fine.
            if (nview == null)
            {
                Object.Destroy(go);
                return;
            }

            // Claim ownership if we can (only meaningful when the ZDO is still
            // valid). Failures here are non-fatal - vanilla's Destroy is
            // tolerant of non-owner calls in single-player / shutdown.
            if (nview.IsValid() && !nview.IsOwner())
            {
                try { nview.ClaimOwnership(); } catch { /* ownership races are recoverable */ }
            }

            // ALWAYS route through ZNetScene.Destroy when a ZNetView is
            // present - vanilla handles both the valid-ZDO and null-ZDO paths
            // and is the ONLY way to keep ZNetScene.m_instances consistent.
            // Doing raw Object.Destroy on a ZNetView'd GO whose dict entry
            // hasn't been cleaned leaves a Unity-destroyed Component in
            // m_instances, which NREs in ZNetScene.RemoveObjects every tick.
            if (ZNetScene.instance != null)
            {
                ZNetScene.instance.Destroy(go);
            }
            else
            {
                // ZNetScene is gone (e.g. shutting down). Last resort - at
                // least the Unity GameObject won't be left dangling. The
                // matching ZDO will be cleaned up on next world load.
                Object.Destroy(go);
            }
        }
    }
}
