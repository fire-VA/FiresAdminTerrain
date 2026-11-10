using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1070 (Fire 10-26: "why do our skylands derender so quickly?"). An island's chunks are voxel chunks of the zone under
    // it: they exist only while that zone is wrapped, and a zone unwraps when vanilla unloads its heightmap or the player is
    // past the wrap radius + 96 m (VoxelZoneStreamer's drop loop). So an island vanished about 250 m away. When a wrapped zone
    // unwraps, its ISLAND chunk meshes (chunks overlapping an island's band, well above the ground) are kept as render-only
    // copies: no collider, no data, same material and layer. They stay until the zone is wrapped and swapped again (the live
    // chunks are back) or the player is FarKeep metres away horizontally. Islands already visited stay in view; one never
    // built this session still appears only when its zone first wraps.
    internal static class FiresSkyIslandFar
    {
        private const float FarKeep = 2000f, CheckEvery = 1f, LogEvery = 30f;
        private const int MaxPieces = 3000;

        private sealed class Piece
        {
            public GameObject Go;
            public Mesh Mesh;
            public Vector3 Centre;
        }

        private static readonly Dictionary<Vector2s, List<Piece>> s_byZone = new Dictionary<Vector2s, List<Piece>>();
        private static readonly List<Vector2s> s_scratch = new List<Vector2s>();
        private static int s_pieces;
        private static float s_nextCheck, s_nextLog;
        private static GameObject s_root;

        // Called by VoxelZoneStreamer.Unwrap BEFORE the zone's chunks are dropped.
        internal static void Capture(Vector2s zone, IEnumerable<Vector3Int> keys)
        {
            if (!FiresSkyIslands.Enabled || Application.isBatchMode) return;
            List<Piece> list = null;
            foreach (var key in keys)
            {
                if (s_pieces >= MaxPieces) break;
                if (!VoxelWorld.TryGetView(key, out var view) || view == null) continue;
                Mesh mesh = view.GroundMesh;
                MeshRenderer rend = view.GroundRenderer;
                if (mesh == null || rend == null || mesh.vertexCount == 0) continue;
                var origin = new Vector3(key.x * VoxelChunk.CELLS, key.y * VoxelChunk.CELLS, key.z * VoxelChunk.CELLS);
                if (!FiresSkyIslands.ChunkOverlaps(origin)) continue;
                if (FiresWorldGenBase.TryGetGenGround(origin.x + 16f, origin.z + 16f, out float ground)
                    && origin.y + VoxelChunk.CELLS < ground + 8f) continue;   // the ground under the island, not the island
                if (s_root == null) { s_root = new GameObject("fat_island_far"); Object.DontDestroyOnLoad(s_root); }
                var go = new GameObject("fat_island_far_" + key.x + "_" + key.y + "_" + key.z);
                go.layer = view.gameObject.layer;
                go.transform.SetParent(s_root.transform, false);
                go.transform.SetPositionAndRotation(view.transform.position, view.transform.rotation);
                go.transform.localScale = view.transform.lossyScale;
                var copy = Object.Instantiate(mesh);
                go.AddComponent<MeshFilter>().sharedMesh = copy;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = rend.sharedMaterials;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // 0.2.1088: a far copy draws once, no shadow pass
                r.receiveShadows = rend.receiveShadows;
                if (list == null && !s_byZone.TryGetValue(zone, out list)) { list = new List<Piece>(); s_byZone[zone] = list; }
                list.Add(new Piece { Go = go, Mesh = copy, Centre = rend.bounds.center });
                s_pieces++;
            }
        }

        // Called from the streamer tick: releases a zone's copies once its live chunks are back (swapped) or it is far away.
        internal static void Tick(Vector3 player, System.Func<Vector2s, bool> zoneLive)
        {
            if (s_byZone.Count == 0 || Time.time < s_nextCheck) return;
            s_nextCheck = Time.time + CheckEvery;
            s_scratch.Clear();
            float nearest = float.MaxValue;
            foreach (var kv in s_byZone)
            {
                if (zoneLive(kv.Key)) { s_scratch.Add(kv.Key); continue; }
                bool keep = false;
                foreach (var p in kv.Value)
                {
                    float dx = p.Centre.x - player.x, dz = p.Centre.z - player.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d < nearest) nearest = d;
                    if (d <= FarKeep) keep = true;
                }
                if (!keep) s_scratch.Add(kv.Key);
            }
            foreach (var z in s_scratch) Release(z);
            if (s_pieces > 0 && FiresLogger.DebugEnabled && Time.time >= s_nextLog)
            {
                s_nextLog = Time.time + LogEvery;
                FiresLogger.LogDebug($"[FiresSkyIslands] island keep-alive: {s_pieces} island chunk(s) held as far copies in {s_byZone.Count} zone(s) "
                    + $"(nearest {(nearest < float.MaxValue ? nearest.ToString("0") : "-")} m horizontal; kept to {FarKeep:0} m)");
            }
        }

        internal static void Release(Vector2s zone)
        {
            if (!s_byZone.TryGetValue(zone, out var list)) return;
            foreach (var p in list)
            {
                if (p.Go != null) Object.Destroy(p.Go);
                if (p.Mesh != null) Object.Destroy(p.Mesh);
            }
            s_pieces -= list.Count;
            s_byZone.Remove(zone);
        }

        internal static void Clear()
        {
            s_scratch.Clear();
            foreach (var kv in s_byZone) s_scratch.Add(kv.Key);
            foreach (var z in s_scratch) Release(z);
            s_pieces = 0;
        }

        [HarmonyLib.HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ZNet_OnDestroy_ClearFar
        {
            private static void Postfix() => Clear();
        }
    }
}
