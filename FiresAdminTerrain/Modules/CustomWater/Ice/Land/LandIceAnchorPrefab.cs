using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Builds and registers the "VALandIce" ZNetView prefab that backs every
    // land-ice disk. Mirrors WaterAnchorPrefab's pattern — clone
    // piece_dirtfloor_vad from the bundle (so we inherit a known-good
    // SoftReference / AssetID and avoid the runtime-prefab-trap), strip
    // its visuals + colliders, rename to "VALandIce", attach a
    // LandIceAnchor MonoBehaviour, register in ZNetScene.m_namedPrefabs.
    //
    // Why this exists: round-26 plan §1 — land ice today rides on a cloned
    // ice1 (vanilla Floating + Rigidbody + LOD group + colliders we have
    // to strip every load), AND its ZDO shares the namespace with anything
    // else that lives on ice1 clones. Splitting it into a dedicated
    // VALandIce prefab gives one ZDO per disk-cluster, decoupled from
    // every other system, and lets us drop the StripOriginalIcePrefabVisuals
    // dance.
    //
    // INVISIBLE BY DESIGN — anchor lives at Y=spawn position with all
    // renderers / colliders / behaviours disabled. The LandIceController
    // (added by IcePlacementTool at placement, or by LandIceAnchor.Start
    // at world reload) builds and parents the actual ice mesh.
    public static class LandIceAnchorPrefab
    {
        public const string PrefabName = "VALandIce";
        public const string SourcePrefabName = "piece_dirtfloor_vad";

        private static GameObject _prefab;
        private static GameObject _inactiveRoot;
        private static bool _registered;

        public static GameObject Prefab => _prefab;
        public static bool IsRegistered => _registered && _prefab != null;

        // Idempotent. Survives logout/login by re-inserting the cached
        // prefab into the new ZNetScene's dict if it was lost.
        public static bool Ensure()
        {
            if (ZNetScene.instance == null) return false;

            int hash = PrefabName.GetStableHashCode();

            if (_registered && _prefab != null
                && ZNetScene.instance.m_namedPrefabs != null
                && ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out var inScene)
                && inScene == _prefab)
            {
                return true;
            }

            if (_prefab != null && ZNetScene.instance.m_namedPrefabs != null
                && !ZNetScene.instance.m_namedPrefabs.ContainsKey(hash))
            {
                ZNetScene.instance.m_namedPrefabs[hash] = _prefab;
                FiresLogger.LogVerbose($"[LandIceAnchorPrefab] Re-inserted cached '{PrefabName}' into new ZNetScene dict (post-login).");
                _registered = true;
                return true;
            }

            return BuildAndRegister();
        }

        private static bool BuildAndRegister()
        {
            var source = LoadSourcePrefab();
            if (source == null) return false;

            if (_inactiveRoot == null)
            {
                _inactiveRoot = new GameObject("FiresAdminTerrain_LandIcePrefabPool");
                _inactiveRoot.SetActive(false);
                Object.DontDestroyOnLoad(_inactiveRoot);
            }

            var clone = Object.Instantiate(source, _inactiveRoot.transform);
            clone.name = PrefabName;

            StripVisualsAndBehaviors(clone);

            var nview = clone.GetComponent<ZNetView>();
            if (nview == null)
            {
                FiresLogger.LogWarning("[LandIceAnchorPrefab] Source prefab had no ZNetView; adding one.");
                nview = clone.AddComponent<ZNetView>();
            }
            nview.m_persistent = true;
            nview.m_syncInitialScale = false;

            // Attach the anchor behaviour — when the prefab spawns from a ZDO,
            // LandIceAnchor.Awake fires and binds (lazily creates the
            // LandIceController + mesh from the persisted data).
            if (clone.GetComponent<LandIceAnchor>() == null)
            {
                clone.AddComponent<LandIceAnchor>();
            }

            int hash = PrefabName.GetStableHashCode();
            ZNetScene.instance.m_namedPrefabs[hash] = clone;
            _prefab = clone;
            _registered = true;
            FiresLogger.LogVerbose($"[LandIceAnchorPrefab] Registered '{PrefabName}' in ZNetScene.m_namedPrefabs (hash={hash}).");
            return true;
        }

        private static GameObject LoadSourcePrefab()
        {
            try
            {
                var bundle = VerdantsAscent.FiresAdminTerrain.assetBundle;
                if (bundle != null)
                {
                    var src = bundle.LoadAsset<GameObject>(SourcePrefabName);
                    if (src != null) return src;
                }
            }
            catch { /* fall through */ }

            var fromScene = ZNetScene.instance.GetPrefab(SourcePrefabName);
            if (fromScene != null) return fromScene;

            FiresLogger.LogError($"[LandIceAnchorPrefab] Source prefab '{SourcePrefabName}' not available; cannot register '{PrefabName}'.");
            return null;
        }

        private static void StripVisualsAndBehaviors(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var a in go.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
            foreach (var p in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var emission = p.emission;
                emission.enabled = false;
            }
            foreach (var l in go.GetComponentsInChildren<Light>(true)) l.enabled = false;

            foreach (var p in go.GetComponentsInChildren<Piece>(true))         Object.DestroyImmediate(p);
            foreach (var w in go.GetComponentsInChildren<WearNTear>(true))     Object.DestroyImmediate(w);
            foreach (var tm in go.GetComponentsInChildren<TerrainModifier>(true)) Object.DestroyImmediate(tm);
        }
    }
}
