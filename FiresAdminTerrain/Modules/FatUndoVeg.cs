using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain
{
    // ── Vegetation a carve cleared comes back on undo ([ghost], lead 11-17: found while reading for rivers diff R7; found = fix) ─────────────
    // A Vriver carve destroys the world's own plants, bushes and trees inside each sample's outer bank (WaterShapingTools.
    // ClearCarvedVegetation), but the stroke's undo held only terrain, water, ice and pours, so Numpad 7 brought the ground and water
    // back and the vegetation stayed gone. Now each cleared object's prefab, position, rotation and scale is noted in the open stroke
    // (inside a player's edit only, the same rule as every other note), and the step's undo spawns them again the way the game places
    // any networked object (Instantiate -> its ZNetView makes the ZDO on this peer, which syncs to the server and everyone, as a
    // placed piece does). Redo removes exactly what the undo spawned (their ZDO ids). Nothing here runs on a dedicated server (FatUndo
    // records on a client only).
    public static partial class FatUndo
    {
        internal sealed class VegNote
        {
            public int Prefab;
            public Vector3 Pos, Scale;
            public Quaternion Rot;
            public ZDOID Spawned = ZDOID.None;   // the copy the last undo made (redo removes it)
        }

        /// <summary>From ClearCarvedVegetation just before it destroys `go`: noted in the open stroke inside a player's edit.</summary>
        internal static void NoteVegetationCleared(GameObject go)
        {
            if (s_laying || s_scope == 0 || !OnClient || go == null) return;
            try
            {
                var view = go.GetComponent<ZNetView>();
                var zdo = view != null ? view.GetZDO() : null;
                int prefab = zdo != null ? zdo.GetPrefab() : Utils.GetPrefabName(go).GetStableHashCode();
                Current().Veg.Add(new VegNote { Prefab = prefab, Pos = go.transform.position, Rot = go.transform.rotation, Scale = go.transform.localScale });
            }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] vegetation note threw: {ex.Message}"); }
        }

        // undo (redo = false): spawn each noted object again; redo: remove the copies the undo spawned
        private static string LayVeg(List<VegNote> veg, bool redo)
        {
            if (veg == null || veg.Count == 0 || ZNetScene.instance == null) return "";
            s_laying = true;
            int done = 0;
            try
            {
                foreach (var n in veg)
                {
                    try
                    {
                        if (!redo)
                        {
                            var prefab = ZNetScene.instance.GetPrefab(n.Prefab);
                            if (prefab == null) continue;
                            var go = UnityEngine.Object.Instantiate(prefab, n.Pos, n.Rot);
                            var view = go.GetComponent<ZNetView>();
                            if (view != null && (n.Scale - Vector3.one).sqrMagnitude > 1e-4f) view.SetLocalScale(n.Scale);
                            n.Spawned = view != null && view.GetZDO() != null ? view.GetZDO().m_uid : ZDOID.None;
                            done++;
                        }
                        else if (!n.Spawned.IsNone())
                        {
                            var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(n.Spawned) : null;
                            var inst = zdo != null ? ZNetScene.instance.FindInstance(zdo) : null;
                            if (inst != null) FiresCore.Net.NetworkObjectHelper.SafeDestroy(inst.gameObject);
                            else if (zdo != null) { zdo.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(zdo); }
                            n.Spawned = ZDOID.None;
                            done++;
                        }
                    }
                    catch (Exception ex) { Debug.LogWarning($"[FatUndo] vegetation {(redo ? "removal" : "respawn")} threw: {ex.Message}"); }
                }
            }
            finally { s_laying = false; }
            return redo ? $", {done} plant(s) / tree(s) cleared again" : $", {done} of {veg.Count} plant(s) / tree(s) back";
        }
    }
}
