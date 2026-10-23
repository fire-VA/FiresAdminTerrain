using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain
{
    /// <summary>
    /// Undo for liquid POURS (FiresLiquidWater, vanilla LiquidVolume grids) that
    /// the IH water-delete and mop tools drain or destroy (WaterBrushCommands.RemoveLiquidLocal). Disks have their own undo (FatUndoIce); a pour is
    /// not a disk, so its whole network record is kept instead: before the tool touches a pour, its LiquidVolume grid is saved into its
    /// ZDO (the vanilla Save the volume runs itself) and that ZDO is serialized (ZDO.Serialize, the format the world save uses). Undo /
    /// redo lay a pour back the way the world load brings one in: the pour's current object (if any) goes, a new ZDO at the same spot
    /// takes the kept record (ZDO.Deserialize), and ZNetScene makes the object from it. One slot per pour, shared by every step, as
    /// the sheets' (FatUndoIce).
    /// </summary>
    public static partial class FatUndo
    {
        internal sealed class PourState
        {
            public int Prefab;
            public Vector3 Pos;
            public byte[] Record;

            public bool SameAs(PourState o) => o != null && Prefab == o.Prefab && Pos == o.Pos && Same(Record, o.Record);
        }

        internal sealed class PourSlot
        {
            public ZDOID Current;
        }

        internal sealed class PourEntry
        {
            public PourSlot Slot;
            public PourState Before, After;
        }

        private static readonly Dictionary<ZDOID, PourSlot> s_pourSlots = new Dictionary<ZDOID, PourSlot>();
        private static readonly MethodInfo LiquidSave = AccessTools.Method(typeof(LiquidVolume), "Save");
        private static readonly MethodInfo IncreaseRevision = AccessTools.Method(typeof(ZDO), "IncreaseDataRevision");

        private static PourSlot PourSlotFor(ZDOID uid)
        {
            if (s_pourSlots.TryGetValue(uid, out var slot)) return slot;
            slot = new PourSlot { Current = uid };
            s_pourSlots[uid] = slot;
            return slot;
        }

        private static void BeforePourEdit(GameObject root)
        {
            if (s_laying || s_scope == 0 || !OnClient || root == null) return;
            try
            {
                var zdo = root.GetComponent<ZNetView>()?.GetZDO();
                if (zdo == null) return;
                var stroke = Current();
                var slot = PourSlotFor(zdo.m_uid);
                if (stroke.PourBefore.ContainsKey(slot)) return;
                stroke.PourBefore[slot] = TakePour(slot.Current);
            }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] pour capture threw: {ex.Message}"); }
        }

        private static List<PourEntry> ClosePours(Stroke stroke)
        {
            var list = new List<PourEntry>();
            foreach (var pair in stroke.PourBefore)
            {
                var now = TakePour(pair.Key.Current);
                if (pair.Value == null ? now == null : pair.Value.SameAs(now)) continue;
                list.Add(new PourEntry { Slot = pair.Key, Before = pair.Value, After = now });
            }
            return list;
        }

        private static string LayPours(List<PourEntry> pours, bool redo)
        {
            if (pours.Count == 0) return "";
            s_laying = true;
            try
            {
                int laid = 0;
                foreach (var entry in pours)
                    if (PutPour(entry.Slot, redo ? entry.After : entry.Before)) laid++;
                return $", {laid} of {pours.Count} liquid pour(s) laid back";
            }
            finally { s_laying = false; }
        }

        // The pour's record now: its grid saved into its ZDO first (a live volume keeps depths in memory between its own saves).
        private static PourState TakePour(ZDOID uid)
        {
            if (uid == ZDOID.None || ZDOMan.instance == null) return null;
            var zdo = ZDOMan.instance.GetZDO(uid);
            if (zdo == null) return null;
            var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (go != null && LiquidSave != null)
                foreach (var lv in go.GetComponentsInChildren<LiquidVolume>(true))
                    try { LiquidSave.Invoke(lv, null); } catch { }
            var pkg = new ZPackage();
            zdo.Serialize(pkg);
            return new PourState { Prefab = zdo.GetPrefab(), Pos = zdo.GetPosition(), Record = pkg.GetArray() };
        }

        private static bool PutPour(PourSlot slot, PourState state)
        {
            try
            {
                var zdo = slot.Current != ZDOID.None ? ZDOMan.instance.GetZDO(slot.Current) : null;
                if (zdo != null)
                {
                    var go = ZNetScene.instance.FindInstance(zdo);
                    if (go != null)
                    {
                        go.ClaimOwnership();
                        ZNetScene.instance.Destroy(go.gameObject);
                    }
                    else
                    {
                        zdo.SetOwner(ZDOMan.GetSessionID());
                        ZDOMan.instance.DestroyZDO(zdo);
                    }
                }
                slot.Current = ZDOID.None;
                if (state == null) return true;
                var made = ZDOMan.instance.CreateNewZDO(state.Pos, state.Prefab);
                made.Deserialize(new ZPackage(state.Record));
                made.SetPosition(state.Pos);
                IncreaseRevision?.Invoke(made, null);   // so the server and the other peers take the record
                slot.Current = made.m_uid;
                s_pourSlots[made.m_uid] = slot;
                return true;
            }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] laying a liquid pour back threw: {ex.InnerException?.Message ?? ex.Message}"); return false; }
        }

        // The IH water-delete and mop tools: every pour the tool's ring can reach is kept before it is drained or destroyed
        // (the same reach test RemoveLiquidLocal uses).
        [HarmonyPatch(typeof(VerdantsAscent.Water.Tools.WaterBrushCommands), "RemoveLiquidLocal")]
        private static class RemoveLiquidLocal_Undo
        {
            private static void Prefix(Vector3 center, float radius)
            {
                if (s_laying || s_scope == 0) return;
                var centerXZ = new Vector2(center.x, center.z);
                foreach (var lv in UnityEngine.Object.FindObjectsByType<LiquidVolume>(FindObjectsSortMode.None))
                {
                    if (lv == null) continue;
                    var root = lv.transform.root.gameObject;
                    if (!root.name.StartsWith(FiresLiquidWaterPrefab.PrefabName, StringComparison.Ordinal)) continue;
                    float reach = radius + lv.m_width * lv.m_scale * 0.5f;
                    if (Vector2.Distance(new Vector2(root.transform.position.x, root.transform.position.z), centerXZ) > reach) continue;
                    BeforePourEdit(root);
                }
            }
        }
    }
}
