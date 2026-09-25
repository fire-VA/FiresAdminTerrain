using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Builds and registers the "CustomRiver" ZNetView prefab that backs every
    // WaterMeshCombinerAnchor. Mirrors TileAnchorPrefab's pattern — clone
    // piece_dirtfloor_vad from the bundle (so we inherit a known-good
    // SoftReference / AssetID and avoid the runtime-prefab-trap), strip its
    // visuals + colliders, rename to "CustomRiver", attach a
    // WaterMeshCombinerAnchor, register in ZNetScene.m_namedPrefabs only.
    //
    // Why this exists: WaterMeshCombinerAnchor.FindPrefab looks for a
    // ZNetScene-registered prefab named "CustomRiver". In the legacy mod
    // FiresNPCs the VAPieceManager registered it; FAT doesn't have that
    // class, so without this file every water disk fails to persist
    // (errors visible in user's log 2026-05-25 as "CustomRiver prefab
    // NOT found in ZNetScene! Water persistence will FAIL!").
    //
    // INVISIBLE BY DESIGN — water anchor lives at Y=-10 with all renderers
    // / colliders / behaviours disabled. The WaterMeshCombiner driven by
    // the anchor renders the actual water mesh at the disk position.
    public static class WaterAnchorPrefab
    {
        public const string PrefabName = "CustomRiver";
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

            // Already in current scene? no-op.
            if (_registered && _prefab != null
                && ZNetScene.instance.m_namedPrefabs != null
                && ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out var inScene)
                && inScene == _prefab)
            {
                return true;
            }

            // Session changed — re-insert cached prefab if we have it.
            if (_prefab != null && ZNetScene.instance.m_namedPrefabs != null
                && !ZNetScene.instance.m_namedPrefabs.ContainsKey(hash))
            {
                ZNetScene.instance.m_namedPrefabs[hash] = _prefab;
                FiresLogger.LogVerbose($"[WaterAnchorPrefab] Re-inserted cached '{PrefabName}' into new ZNetScene dict (post-login).");
                _registered = true;
                return true;
            }

            // Fresh build.
            return BuildAndRegister();
        }

        private static bool BuildAndRegister()
        {
            var source = LoadSourcePrefab();
            if (source == null) return false;

            // Inactive parent so the clone doesn't Awake before we finish stripping.
            if (_inactiveRoot == null)
            {
                _inactiveRoot = new GameObject("FiresAdminTerrain_WaterPrefabPool");
                _inactiveRoot.SetActive(false);
                Object.DontDestroyOnLoad(_inactiveRoot);
            }

            var clone = Object.Instantiate(source, _inactiveRoot.transform);
            clone.name = PrefabName;

            // Strip everything that would interfere with an invisible anchor.
            StripVisualsAndBehaviors(clone);

            // Make sure ZNetView is intact — the anchor MUST persist via ZDO.
            var nview = clone.GetComponent<ZNetView>();
            if (nview == null)
            {
                FiresLogger.LogWarning("[WaterAnchorPrefab] Source prefab had no ZNetView; adding one.");
                nview = clone.AddComponent<ZNetView>();
            }
            nview.m_persistent = true;
            nview.m_syncInitialScale = false;

            // Attach the anchor behaviour — when the prefab spawns from a ZDO,
            // WaterMeshCombinerAnchor.Awake fires and binds to its combiner.
            if (clone.GetComponent<WaterMeshCombinerAnchor>() == null)
            {
                clone.AddComponent<WaterMeshCombinerAnchor>();
            }

            // Register only in m_namedPrefabs — never in m_prefabs (the
            // PieceTable / build-menu iteration surface). This anchor should
            // never appear in any UI.
            int hash = PrefabName.GetStableHashCode();
            ZNetScene.instance.m_namedPrefabs[hash] = clone;
            _prefab = clone;
            _registered = true;
            FiresLogger.LogVerbose($"[WaterAnchorPrefab] Registered '{PrefabName}' in ZNetScene.m_namedPrefabs (hash={hash}).");
            return true;
        }

        // Two-source loader: try the FAT bundle first (matches what
        // TileAnchorPrefab does — guaranteed-available), then fall back to
        // ZNetScene.GetPrefab in case some other mod's bundle exposes it.
        private static GameObject LoadSourcePrefab()
        {
            // 1. FAT bundle.
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

            // 2. ZNetScene registry.
            var fromScene = ZNetScene.instance.GetPrefab(SourcePrefabName);
            if (fromScene != null) return fromScene;

            FiresLogger.LogError($"[WaterAnchorPrefab] Source prefab '{SourcePrefabName}' not available; cannot register '{PrefabName}'.");
            return null;
        }

        // Disable (don't destroy) Renderer, Collider, AudioSource, ParticleSystem,
        // Light. Drop visual MonoBehaviours like Piece, WearNTear, etc. that would
        // try to attach UI / construction state.
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

            // Strip Piece / WearNTear / TerrainModifier components that would
            // try to register with build / piece systems.
            foreach (var p in go.GetComponentsInChildren<Piece>(true))   Object.DestroyImmediate(p);
            foreach (var w in go.GetComponentsInChildren<WearNTear>(true)) Object.DestroyImmediate(w);
            foreach (var tm in go.GetComponentsInChildren<TerrainModifier>(true)) Object.DestroyImmediate(tm);
        }
    }
}
