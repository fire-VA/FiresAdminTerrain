using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1129: a terrain op travels as its PREFAB NAME only (TerrainOp.Settings.Serialize, TerrainOp.cs:103-107) and the owner
    // applies that prefab's own settings, so every FAT op width is its own registered op prefab: an inactive copy of the Player's
    // snow_walk with the paint radius set, put in ObjectDB.m_terrainOpsByHash by its name's hash on EVERY peer (client, host,
    // dedicated server). UpdateRegisters clears that table, so the sets are put back after it and checked before each received op.
    // Shared by FatShovelPatch (FAT_ShovelPatch_NN) and FatCreatureTrail (FAT_CreatureTrail_NNN).
    internal static class FatOpTemplates
    {
        private static Transform s_root;

        // Registers every (name, paint radius) of a set; `cache` keeps the copies. True when the set is in ObjectDB.
        internal static bool Ensure(Dictionary<int, TerrainOp> cache, IList<KeyValuePair<string, float>> set)
        {
            var db = ObjectDB.instance;
            if (db == null || set == null || set.Count == 0) return false;
            int first = set[0].Key.GetStableHashCode();
            if (cache.Count > 0 && cache.TryGetValue(first, out var mine) && db.m_terrainOpsByHash.TryGetValue(first, out var have) && have != null && have == mine) return true;
            var walk = FatSnowTrail.WalkPrefab();
            if (walk == null || walk.GetComponent<TerrainOp>() == null) return false;
            if (s_root == null)
            {
                var h = new GameObject("FAT_OpTemplates");
                h.SetActive(false);   // the copies never Awake (TerrainOp.Awake applies and destroys itself)
                Object.DontDestroyOnLoad(h);
                s_root = h.transform;
            }
            foreach (var kv in set)
            {
                int hash = kv.Key.GetStableHashCode();
                if (!cache.TryGetValue(hash, out var op) || op == null)
                {
                    var go = Object.Instantiate(walk, s_root);
                    go.name = kv.Key;
                    op = go.GetComponent<TerrainOp>();
                    op.m_settings.m_paintRadius = kv.Value;
                    cache[hash] = op;
                }
                db.m_terrainOpsByHash[hash] = op;
            }
            return true;
        }

        internal static string Peer()
            => ZNet.instance == null ? "no ZNet yet" : ZNet.instance.IsServer() ? (ZNet.instance.IsDedicated() ? "dedicated server" : "host") : "client";
    }
}
