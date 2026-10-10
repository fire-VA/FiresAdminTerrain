using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // P1 — the "VAVoxel" ZNetView prefab that persists one edited voxel chunk.
    // Follows the TileAnchorPrefab rules exactly:
    //   * cloned from the bundle piece (piece_dirtfloor_vad) — NEVER a fresh
    //     GameObject (runtime-prefab-trap);
    //   * visuals/behaviours DISABLED not destroyed;
    //   * registered in m_namedPrefabs ONLY (never m_prefabs);
    //   * session re-insert on new ZNetScene (statics survive relog);
    //   * spawns via the template-inactive flip (round-74 destroy-bug lesson:
    //     activating at origin registers the ZDO in zone (0,0) → swept).
    public static class VoxelAnchorPrefab
    {
        public const string PrefabName = "VAVoxel";
        internal static readonly int PrefabHash = PrefabName.GetStableHashCode();
        private const string SourcePrefabName = "piece_dirtfloor_vad";

        internal const string ZDO_KX = "vv_kx";
        internal const string ZDO_KY = "vv_ky";
        internal const string ZDO_KZ = "vv_kz";
        internal const string ZDO_VER = "vv_ver";
        internal const string ZDO_EDITS = "vv_edits";
        // 2 = brush edit bounds over live ground (1 stored the frozen density).
        internal const int EditsFormatVersion = 2;

        private static GameObject _prefab;
        private static GameObject _inactiveRoot;
        private static bool _registered;

        public static bool Ensure()
        {
            if (ZNetScene.instance == null) return false;
            int hash = PrefabName.GetStableHashCode();

            if (_registered && _prefab != null)
            {
                if (ZNetScene.instance.m_namedPrefabs != null
                    && ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out var inScene)
                    && inScene == _prefab)
                    return true;
                ZNetScene.instance.m_namedPrefabs[hash] = _prefab;   // session re-insert
                FiresLogger.LogInfo($"[FiresVoxel] re-inserted '{PrefabName}' into new ZNetScene dict.");
                return true;
            }

            GameObject src = null;
            try
            {
                var bundle = VerdantsAscent.FiresAdminTerrain.assetBundle;
                src = bundle != null ? bundle.LoadAsset<GameObject>(SourcePrefabName) : null;
            }
            catch { }
            if (src == null)
            {
                FiresLogger.LogError($"[FiresVoxel] anchor source '{SourcePrefabName}' missing from FAT bundle — voxel persistence unavailable.");
                return false;
            }

            if (_inactiveRoot == null)
            {
                _inactiveRoot = new GameObject("FiresVoxelAnchorPool");
                _inactiveRoot.SetActive(false);
                Object.DontDestroyOnLoad(_inactiveRoot);
            }

            var clone = Object.Instantiate(src, _inactiveRoot.transform);
            clone.name = PrefabName;
            // Tile-anchor hardening parity: validate the clone is a REAL Unity
            // object before it enters cross-mod prefab iteration surfaces.
            try { _ = clone.name; }
            catch
            {
                FiresLogger.LogError("[FiresVoxel] anchor clone failed name validation — not registering.");
                return false;
            }

            // Disable-not-destroy: invisible, non-colliding, non-interactive —
            // but Unity-object-valid for cross-mod prefab iteration.
            foreach (var r in clone.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var c in clone.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var a in clone.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(mb is ZNetView)) mb.enabled = false;

            var nv = clone.GetComponent<ZNetView>() ?? clone.AddComponent<ZNetView>();
            nv.m_persistent = true;

            clone.AddComponent<VoxelChunkAnchor>();

            ZNetScene.instance.m_namedPrefabs[hash] = clone;
            _prefab = clone;
            _registered = true;
            FiresLogger.LogInfo($"[FiresVoxel] registered anchor prefab '{PrefabName}' (named-only, from {SourcePrefabName}).");
            return true;
        }

        // The anchor ZDO for a chunk, found without a live instance. A dedicated server holds no GameObjects where the
        // players are (its reference position is parked off-world), so the component registry is empty there and every
        // save would otherwise write a SECOND anchor for the same chunk. The newest one then loses to the established
        // one on the clients, which is why a chunk stamped twice - a placement and its undo - kept the first land.
        internal static ZDO FindZdo(Vector3Int key, Vector3 at)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return null;
            var bySector = ObjectsBySectorRef(zdoMan);
            if (bySector == null) return null;
            uint sector = ZoneSystem.GetSectorIndex(at).Sector;
            if (sector >= bySector.Length) return null;
            var inSector = bySector[sector];
            if (inSector == null) return null;
            foreach (var zdo in inSector)
                if (zdo != null && zdo.GetPrefab() == PrefabHash
                    && zdo.GetInt(ZDO_KX, int.MinValue) == key.x
                    && zdo.GetInt(ZDO_KY, int.MinValue) == key.y
                    && zdo.GetInt(ZDO_KZ, int.MinValue) == key.z)
                    return zdo;
            return null;
        }

        private static readonly AccessTools.FieldRef<ZDOMan, System.Collections.Generic.List<ZDO>[]> ObjectsBySectorRef =
            AccessTools.FieldRefAccess<ZDOMan, System.Collections.Generic.List<ZDO>[]>("m_objectsBySector");

        public static GameObject Spawn(Vector3 pos)
        {
            if (!Ensure()) return null;
            GameObject go;
            bool wasActive = _prefab.activeSelf;
            _prefab.SetActive(false);
            try
            {
                go = Object.Instantiate(_prefab, pos, Quaternion.identity);
                go.name = PrefabName;
            }
            finally { _prefab.SetActive(wasActive); }
            go.SetActive(true);
            return go;
        }
    }
}
