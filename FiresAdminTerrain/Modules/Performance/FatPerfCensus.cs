using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain
{
    // 0.2.1088 (Fire, live 1086 at the homestead: "why is our performance so bad right now"; ZNetScene.CreateObjects ~47 ms/s and
    // ~2,150 draws where earlier sessions had ~350-550). Two Debug lines every 30 s, client only:
    //  * [FatPerf] objects: the top 10 prefabs ZNetScene created and destroyed in the window (create/destroy churn shows as the
    //    same prefab high in both);
    //  * [FatPerf] draws: enabled / visible renderers by FAT category, each with its shadow casters, plus FAT's instanced
    //    draw calls per frame (underwater, cave and halo clutter, far vegetation), and everything not FAT in one bucket.
    // The renderer walk is one FindObjectsOfType every 30 s and only while Debug is on.
    internal static class FatPerfCensus
    {
        private const float Window = 30f;
        private static readonly Dictionary<int, int> s_created = new Dictionary<int, int>(), s_destroyed = new Dictionary<int, int>();
        private static float s_nextAt;
        private static int s_frames;
        internal static int InstancedCalls;   // FAT's Graphics.RenderMeshInstanced / DrawMeshInstanced calls, summed per window

        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        private static class Patch_Create
        {
            private static void Postfix(ZDO zdo, GameObject __result)
            {
                if (__result == null || zdo == null || !VerdantsAscent.Terrain.Voxel.FatDiag.PerfCensus) return;
                int h = zdo.GetPrefab();
                s_created.TryGetValue(h, out int n); s_created[h] = n + 1;
            }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy))]
        private static class Patch_Destroy
        {
            private static void Prefix(GameObject go)
            {
                if (go == null || !VerdantsAscent.Terrain.Voxel.FatDiag.PerfCensus) return;
                var nv = go.GetComponent<ZNetView>();
                var zdo = nv != null ? nv.GetZDO() : null;
                if (zdo == null) return;
                int h = zdo.GetPrefab();
                s_destroyed.TryGetValue(h, out int n); s_destroyed[h] = n + 1;
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Update")]
        private static class Patch_Tick
        {
            private static void Postfix()
            {
                if (!VerdantsAscent.Terrain.Voxel.FatDiag.PerfCensus || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
                s_frames++;
                if (Time.unscaledTime < s_nextAt) return;
                bool first = s_nextAt == 0f;
                s_nextAt = Time.unscaledTime + Window;
                if (!first) { LogObjects(); LogDraws(); }
                s_created.Clear(); s_destroyed.Clear(); InstancedCalls = 0; s_frames = 0;
            }
        }

        private static string PrefabName(int hash)
        {
            var p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            return p != null ? p.name : hash.ToString();
        }

        private static void Top(StringBuilder sb, Dictionary<int, int> counts)
        {
            var list = new List<KeyValuePair<int, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            int total = 0;
            foreach (var kv in list) total += kv.Value;
            sb.Append(total).Append(" (");
            for (int i = 0; i < list.Count && i < 10; i++) sb.Append(PrefabName(list[i].Key)).Append(" x").Append(list[i].Value).Append("; ");
            sb.Append(')');
        }

        private static void LogObjects()
        {
            var sb = new StringBuilder("[FatPerf] objects last 30 s: created ");
            Top(sb, s_created);
            sb.Append(", destroyed ");
            Top(sb, s_destroyed);
            FiresLogger.LogDebug(sb.ToString());
        }

        private enum Cat { VoxelChunk, ChunkWater, IslandFar, PlacedWater, WaterCombiner, Falls, Ghosts, Gizmos, FarQuilt, NotFat, Count }
        private static readonly int[] s_on = new int[(int)Cat.Count], s_vis = new int[(int)Cat.Count], s_shadow = new int[(int)Cat.Count];

        private static Cat Classify(Renderer r)
        {
            var t = r.transform;
            string n = t.name;
            if (n == "genwater" || n == "IceShell") return Cat.ChunkWater;
            if (r.GetComponent<VoxelChunkView>() != null) return Cat.VoxelChunk;
            if (n.StartsWith("fat_island_far")) return Cat.IslandFar;
            if (n.StartsWith("fires_placed_water")) return Cat.PlacedWater;
            if (n.StartsWith("WaterMeshCombiner")) return Cat.WaterCombiner;
            if (n.EndsWith("_ghost") || n == "fires_pool_hint") return Cat.Ghosts;
            if (n.StartsWith("fires_vfall")) return Cat.Falls;
            if (r is LineRenderer || n.StartsWith("FiresVoxelGizmo") || n.StartsWith("FiresGizmo") || n == "line" || n == "cage") return Cat.Gizmos;
            if (n.StartsWith("FarQuiltTile")) return Cat.FarQuilt;
            var p = t.parent;
            if (p != null)
            {
                if (p.GetComponent<VoxelChunkView>() != null) return Cat.ChunkWater;
                string pn = p.name;
                if (pn.StartsWith("fires_vfall")) return pn.EndsWith("_ghost") ? Cat.Ghosts : Cat.Falls;
                if (pn.StartsWith("fires_placed_water")) return Cat.PlacedWater;
                if (pn.StartsWith("WaterMeshCombiner")) return Cat.WaterCombiner;
            }
            return Cat.NotFat;
        }

        private static void LogDraws()
        {
            System.Array.Clear(s_on, 0, s_on.Length); System.Array.Clear(s_vis, 0, s_vis.Length); System.Array.Clear(s_shadow, 0, s_shadow.Length);
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                int c = (int)Classify(r);
                s_on[c]++;
                if (r.isVisible) s_vis[c]++;
                if (r.shadowCastingMode != ShadowCastingMode.Off) s_shadow[c]++;
            }
            var sb = new StringBuilder("[FatPerf] draws (enabled / visible / shadow casters): ");
            for (int i = 0; i < (int)Cat.Count; i++)
                sb.Append((Cat)i).Append(' ').Append(s_on[i]).Append('/').Append(s_vis[i]).Append('/').Append(s_shadow[i]).Append("; ");
            sb.Append("FAT instanced draw calls ").Append(s_frames > 0 ? (InstancedCalls / (float)s_frames).ToString("0.0") : "0").Append(" per frame");
            FiresLogger.LogDebug(sb.ToString());
        }
    }
}
