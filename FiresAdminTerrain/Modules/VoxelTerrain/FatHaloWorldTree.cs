using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // HALO RING World Tree (Fire 10-24: "just put the vanilla world tree across the axel ... turn it 90* and use it").
    // Vanilla's own sky tree (_Environment/YggdrasilBranch/branch, layer 19 "skybox", drawn by the sky camera only; fixed world
    // geometry, no FollowPlayer: [visual]'s RESEARCH_yggdrasil_sky_20261024.md). On the full vertical ring (HaloFlat) the SAME
    // object is turned so its long local Z runs along the ring's axle and its bounds centre sits on the hub; mesh, material,
    // scale and layer stay vanilla's. Off the ring (or when the ring goes away) the vanilla pose is put back. Normal worlds:
    // nothing runs.
    internal static class FatHaloWorldTree
    {
        private const string Path = "_Environment/YggdrasilBranch/branch";
        private static Transform s_tree;
        private static Renderer s_rend;
        private static Vector3 s_pos, s_scale;
        private static Quaternion s_rot;
        private static bool s_placed, s_searched;
        private static string s_placedFor;

        internal static void Tick(bool active, string ringKey, Vector3 origin, Vector3 dir)
        {
            if (!active || Mathf.Abs(dir.y) < 0.999f) { Restore(); return; }
            if (s_tree == null)
            {
                if (s_searched && Time.frameCount % 300 != 0) return;
                s_searched = true;
                var go = GameObject.Find(Path);
                if (go == null) return;
                s_tree = go.transform;
                s_rend = go.GetComponent<Renderer>();
                s_pos = s_tree.position; s_rot = s_tree.rotation; s_scale = s_tree.localScale;
                s_placed = false;
            }
            if (s_placed && s_placedFor == ringKey) return;
            s_tree.rotation = Quaternion.LookRotation(dir.normalized, Vector3.forward);   // local Z = the axle
            if (s_rend != null) s_tree.position += origin - s_rend.bounds.center;        // bounds centre on the hub
            else s_tree.position = origin;
            s_placed = true; s_placedFor = ringKey;
            Vector3 c = s_rend != null ? s_rend.bounds.center : s_tree.position;
            FiresLogger.LogDebug($"[FatHalo] world tree: vanilla sky tree turned onto the axle, centre ({c.x:0}, {c.y:0}, {c.z:0}), size {(s_rend != null ? s_rend.bounds.size.ToString("F0") : "?")}, layer {s_tree.gameObject.layer}");
        }

        private static void Restore()
        {
            if (!s_placed || s_tree == null) { s_placed = false; return; }
            s_tree.SetPositionAndRotation(s_pos, s_rot);
            s_tree.localScale = s_scale;
            s_placed = false; s_placedFor = null;
            FiresLogger.LogDebug("[FatHalo] world tree: vanilla pose restored");
        }
    }
}
