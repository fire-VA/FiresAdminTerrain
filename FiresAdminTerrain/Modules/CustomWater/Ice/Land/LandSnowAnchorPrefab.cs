using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Builds and registers the "VALandSnow" ZNetView prefab — the
    // anchor for terrain-conforming snow drifts. Mirrors LandIceAnchorPrefab
    // exactly; only the prefab name and the attached anchor MonoBehaviour
    // type differ.
    //
    // Round-26 plan §2.1. See LandIceAnchorPrefab for the architectural
    // rationale (one ZDO per disk-cluster, dedicated namespace, no
    // ice1 host / no per-load strip dance).
    public static class LandSnowAnchorPrefab
    {
        public const string PrefabName = "VALandSnow";
        public const string SourcePrefabName = "piece_dirtfloor_vad";

        private static GameObject _prefab;
        private static GameObject _inactiveRoot;
        private static bool _registered;

        public static GameObject Prefab => _prefab;
        public static bool IsRegistered => _registered && _prefab != null;

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
                FiresLogger.LogVerbose($"[LandSnowAnchorPrefab] Re-inserted cached '{PrefabName}' into new ZNetScene dict (post-login).");
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
                _inactiveRoot = new GameObject("FiresAdminTerrain_LandSnowPrefabPool");
                _inactiveRoot.SetActive(false);
                Object.DontDestroyOnLoad(_inactiveRoot);
            }

            var clone = Object.Instantiate(source, _inactiveRoot.transform);
            clone.name = PrefabName;

            StripVisualsAndBehaviors(clone);

            var nview = clone.GetComponent<ZNetView>();
            if (nview == null)
            {
                FiresLogger.LogWarning("[LandSnowAnchorPrefab] Source prefab had no ZNetView; adding one.");
                nview = clone.AddComponent<ZNetView>();
            }
            nview.m_persistent = true;
            nview.m_syncInitialScale = false;

            if (clone.GetComponent<LandSnowAnchor>() == null)
            {
                clone.AddComponent<LandSnowAnchor>();
            }

            int hash = PrefabName.GetStableHashCode();
            ZNetScene.instance.m_namedPrefabs[hash] = clone;
            _prefab = clone;
            _registered = true;
            FiresLogger.LogVerbose($"[LandSnowAnchorPrefab] Registered '{PrefabName}' in ZNetScene.m_namedPrefabs (hash={hash}).");
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

            FiresLogger.LogError($"[LandSnowAnchorPrefab] Source prefab '{SourcePrefabName}' not available; cannot register '{PrefabName}'.");
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
