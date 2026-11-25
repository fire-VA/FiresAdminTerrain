using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Water.Core;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Terrain
{
    /// <summary>
    /// Undo for ice, snow and removed water, in the same strokes as the voxel and
    /// drawn-water steps (FatUndo.cs). Reused from FatUndo's chunk pattern: each sheet a stroke touches keeps its state from before the
    /// stroke, taken from the sheet's own anchor record (LandIceAnchor / LandSnowAnchor ReadData, the floating ice's va_float_ice_* keys),
    /// and undo / redo lay a sheet back the way the world load does (SpawnNew + WriteData + RestoreFromAnchor; a floating sheet's Start
    /// reads its keys). A drawn water disk the stroke removed keeps its serialized self and is drawn again on undo (removed on redo).
    /// Scopes: every ice / snow / water console command (the Infinity Hammer drives these) and WaterSheets.Remove, so seasonal freezing
    /// and the combiners' own merges outside a player's command never make steps.
    /// </summary>
    public static partial class FatUndo
    {
        // A sheet as its anchor records it. Null state = no sheet (made by the stroke, or removed by it).
        internal sealed class IceState
        {
            public int Kind;                // 0 land ice, 1 land snow, 2 floating ice
            public Vector3 Pos;
            public float Seed, WaterY;
            public byte[] Fp, Grid, Heights;

            public bool SameAs(IceState o) => o != null && Kind == o.Kind && Pos == o.Pos && Seed == o.Seed && WaterY == o.WaterY
                                              && Same(Fp, o.Fp) && Same(Grid, o.Grid) && Same(Heights, o.Heights);
        }

        // Undo / redo REPLACE a sheet's object, and
        // every step about that sheet must reach the replacement, not only the step that made it. One slot per sheet, shared by every
        // step (else a second undo finds the placed ice still there); each object the sheet has had is registered to it by instance id.
        internal sealed class SheetSlot
        {
            public Component Current;
        }

        private static readonly Dictionary<int, SheetSlot> s_slots = new Dictionary<int, SheetSlot>();

        private static SheetSlot SlotFor(Component sheet)
        {
            int id = sheet.GetInstanceID();
            if (s_slots.TryGetValue(id, out var slot)) return slot;
            slot = new SheetSlot { Current = sheet };
            s_slots[id] = slot;
            return slot;
        }

        // The sheet a step's entry is about, through its slot.
        internal sealed class IceEntry
        {
            public SheetSlot Slot;
            public IceState Before, After;
        }

        private static readonly AccessTools.FieldRef<LandIceController, LandIceAnchor> IceAnchor = AccessTools.FieldRefAccess<LandIceController, LandIceAnchor>("_anchor");
        private static readonly AccessTools.FieldRef<LandSnowController, LandSnowAnchor> SnowAnchor = AccessTools.FieldRefAccess<LandSnowController, LandSnowAnchor>("_anchor");
        private static readonly MethodInfo CloneIcePrefab = AccessTools.Method(typeof(IcePlacementTool), "CloneIcePrefab");

        private static readonly int FloatFlag = "va_float_ice".GetStableHashCode();
        private static readonly int FloatFp = "va_float_ice_fp".GetStableHashCode();
        private static readonly int FloatSeed = "va_float_ice_seed".GetStableHashCode();
        private static readonly int FloatHeights = "va_float_ice_heights".GetStableHashCode();
        private static readonly int FloatGrid = "va_float_ice_grid".GetStableHashCode();
        private static readonly int FloatWaterY = "va_float_ice_wy".GetStableHashCode();

        /// <summary>Before a sheet changes inside a player's command: its state is kept, the first time this stroke touches it.</summary>
        private static void BeforeIceEdit(Component sheet, bool made)
        {
            if (s_laying || s_scope == 0 || !OnClient || sheet == null) return;
            try
            {
                var stroke = Current();
                var slot = SlotFor(sheet);
                if (stroke.IceBefore.ContainsKey(slot)) return;
                stroke.IceBefore[slot] = made ? null : TakeIce(sheet);
            }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] ice/snow capture threw: {ex.Message}"); }
        }

        private static List<IceEntry> CloseIce(Stroke stroke)
        {
            var list = new List<IceEntry>();
            foreach (var pair in stroke.IceBefore)
            {
                var cur = pair.Key.Current;
                var now = cur != null ? TakeIce(cur) : null;                     // a removed sheet's object is gone: no sheet now
                if (pair.Value == null ? now == null : pair.Value.SameAs(now)) continue;
                list.Add(new IceEntry { Slot = pair.Key, Before = pair.Value, After = now });
            }
            return list;
        }

        private static string LayMore(List<DiskNote> gone, List<IceEntry> ice, bool redo)
        {
            if (gone.Count == 0 && ice.Count == 0) return "";
            s_laying = true;
            try
            {
                int water = 0, sheets = 0;
                foreach (var disk in gone)
                    if (redo ? Remove(disk) : Redraw(disk)) water++;
                foreach (var entry in ice)
                    if (PutIce(entry, redo ? entry.After : entry.Before)) sheets++;
                return $", {water} of {gone.Count} removed water disk(s) {(redo ? "removed again" : "drawn back")}, {sheets} of {ice.Count} ice/snow sheet(s) laid back";
            }
            finally { s_laying = false; }
        }

        private static IceState TakeIce(Component sheet)
        {
            switch (sheet)
            {
                case LandIceController ice:
                {
                    var anchor = IceAnchor(ice);
                    if (anchor == null || !anchor.ReadData(out float seed, out byte[] fp, out byte[] grid, out byte[] h)) return null;
                    return new IceState { Kind = 0, Pos = ice.transform.position, Seed = seed, Fp = fp, Grid = grid, Heights = h };
                }
                case LandSnowController snow:
                {
                    var anchor = SnowAnchor(snow);
                    if (anchor == null || !anchor.ReadData(out float seed, out byte[] fp, out byte[] grid, out byte[] h)) return null;
                    return new IceState { Kind = 1, Pos = snow.transform.position, Seed = seed, Fp = fp, Grid = grid, Heights = h };
                }
                case FloatingIceController floating:
                {
                    var zdo = floating.GetComponent<ZNetView>()?.GetZDO();
                    if (zdo == null || !zdo.GetBool(FloatFlag, false)) return null;
                    return new IceState
                    {
                        Kind = 2, Pos = floating.transform.position, Seed = zdo.GetFloat(FloatSeed, 0f), WaterY = zdo.GetFloat(FloatWaterY, 0f),
                        Fp = zdo.GetByteArray(FloatFp), Grid = zdo.GetByteArray(FloatGrid), Heights = zdo.GetByteArray(FloatHeights),
                    };
                }
            }
            return null;
        }

        // The sheet's object (if any) removed, then the kept state laid as a new sheet the way the world load restores one.
        private static bool PutIce(IceEntry entry, IceState state)
        {
            try
            {
                var slot = entry.Slot;
                switch (slot.Current)
                {
                    case LandIceController ice when ice != null: ice.Remove(); break;
                    case LandSnowController snow when snow != null: snow.Remove(); break;
                    case FloatingIceController floating when floating != null: floating.Remove(); break;
                }
                slot.Current = null;
                if (state == null) return true;
                switch (state.Kind)
                {
                    case 0:
                    {
                        var ctrl = LandIceAnchor.SpawnNew(state.Pos);
                        var anchor = ctrl != null ? IceAnchor(ctrl) : null;
                        if (anchor == null) return false;
                        anchor.WriteData(state.Seed, state.Fp, state.Grid, state.Heights);
                        ctrl.RestoreFromAnchor(anchor);
                        Bind(slot, ctrl);
                        return true;
                    }
                    case 1:
                    {
                        var ctrl = LandSnowAnchor.SpawnNew(state.Pos);
                        var anchor = ctrl != null ? SnowAnchor(ctrl) : null;
                        if (anchor == null) return false;
                        anchor.WriteData(state.Seed, state.Fp, state.Grid, state.Heights);
                        ctrl.RestoreFromAnchor(anchor);
                        Bind(slot, ctrl);
                        return true;
                    }
                    default:
                    {
                        var go = CloneIcePrefab?.Invoke(null, new object[] { state.Pos }) as GameObject;
                        var zdo = go != null ? go.GetComponent<ZNetView>()?.GetZDO() : null;
                        if (zdo == null) return false;
                        zdo.Set(FloatFlag, true);
                        zdo.Set(FloatSeed, state.Seed);
                        zdo.Set(FloatWaterY, state.WaterY);
                        if (state.Fp != null) zdo.Set(FloatFp, state.Fp);
                        if (state.Grid != null) zdo.Set(FloatGrid, state.Grid);
                        if (state.Heights != null) zdo.Set(FloatHeights, state.Heights);
                        Bind(slot, go.GetComponent<FloatingIceController>() ?? go.AddComponent<FloatingIceController>());   // its Start reads the keys
                        return true;
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] laying an ice/snow sheet back threw: {ex.InnerException?.Message ?? ex.Message}"); return false; }
        }

        private static void Bind(SheetSlot slot, Component sheet)
        {
            slot.Current = sheet;
            if (sheet != null) s_slots[sheet.GetInstanceID()] = slot;
        }

        // A drawn disk a player's command removes keeps its serialized self (undo draws it again).
        private static void NoteGone(WaterDisk disk)
        {
            if (s_laying || s_scope == 0 || !OnClient || disk == null || disk.StableHash == 0) return;
            try
            {
                var stroke = Current();
                if (stroke.Gone.Exists(n => n.Hash == disk.StableHash)) return;
                int made = stroke.Disks.FindIndex(n => n.Hash == disk.StableHash);
                if (made >= 0) { stroke.Disks.RemoveAt(made); return; }        // drawn and removed in one stroke: nothing to lay
                stroke.Gone.Add(new DiskNote { Hash = disk.StableHash, Data = disk.Serialize(), Custom = true });
            }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] removed water disk note threw: {ex.Message}"); }
        }

        private static IEnumerable<WaterDisk> DisksWhere(Func<WaterDisk, bool> match)
        {
            foreach (var combiner in new List<WaterMeshCombiner>(WaterMeshCombiner.AllCombiners))
            {
                if (combiner == null) continue;
                foreach (var disk in combiner.WaterDisks)
                    if (disk != null && match(disk)) yield return disk;
            }
        }

        // ── scopes: the ice / snow / water console commands (IH runs these) ────────────────────────────
        // 0.2.1502: these were Harmony patches on FAT's own methods (see FatUndo.cs for why); each call site now calls the hook below.
        //   IceWaterCommands_Undo  -> every static ConsoleEventArgs command of LandIceCommands, LandSnowCommands, FloatingIceCommands,
        //                             IcePlacementTool, WaterBrushCommands, WaterPlacementTool (the old TargetMethods filter: static, not ParseArgs,
        //                             first parameter Terminal.ConsoleEventArgs, no out / ref): scope BeginCommandEdit(args) / EndPlayerEdit()
        //   WaterSheetsRemove_Undo -> WaterSheets.Remove: scope "a water sheet removal"
        //   IceSheetEdits_Undo     -> LandIce / LandSnow ModifyHeights, LevelHeights, SmoothHeights, AddFootprint, Remove; FloatingIce ModifyTopHeights,
        //                             SmoothTopHeights, BreakIceAt, AddFootprint, Remove: prefix IceSheetBefore(this, false)
        //   IceSheetMade_Undo      -> LandIce / LandSnow Initialize, FloatingIce InitializeFloating: prefix IceSheetBefore(this, true)
        //   WaterRemove_Undo       -> WaterOperationRPC.RequestRemoveWater: prefix WaterRemoveBefore(stableHash)
        //   WaterClearZone_Undo    -> WaterOperationRPC.RequestClearZoneWater: prefix WaterClearZoneBefore(center, radius)
        //   CombinerRemoveDisk_Undo-> WaterMeshCombiner.RemoveWaterDisk: prefix CombinerRemoveBefore(disk)

        /// <summary>An ice / snow / water console command starts (was IceWaterCommands_Undo's prefix; same text).</summary>
        internal static void BeginCommandEdit(Terminal.ConsoleEventArgs args)
            => BeginPlayerEdit($"the {(args?.Args != null && args.Args.Length > 0 ? args.Args[0] : "ice/water")} command");

        /// <summary>An ice / snow sheet is about to be edited or made (was IceSheetEdits_Undo / IceSheetMade_Undo's prefix).</summary>
        internal static void IceSheetBefore(Component sheet, bool made) => BeforeIceEdit(sheet, made);

        /// <summary>WaterOperationRPC.RequestRemoveWater is about to remove a disk (was WaterRemove_Undo's prefix; same body).</summary>
        internal static void WaterRemoveBefore(int stableHash)
        {
            if (s_laying || s_scope == 0) return;
            foreach (var disk in DisksWhere(d => d.StableHash == stableHash)) { NoteGone(disk); break; }
        }

        /// <summary>WaterOperationRPC.RequestClearZoneWater is about to clear disks (was WaterClearZone_Undo's prefix; same body).</summary>
        internal static void WaterClearZoneBefore(Vector3 center, float radius)
        {
            if (s_laying || s_scope == 0) return;
            foreach (var disk in DisksWhere(d => Vector3.Distance(d.Position, center) <= radius)) NoteGone(disk);
        }

        // The player's water delete (va_brush_water_delete, the IH water-delete tool) takes disks out of
        // their combiner directly, never through RequestRemoveWater, so the disk is noted where every removal ends.
        /// <summary>WaterMeshCombiner.RemoveWaterDisk is about to drop a disk (was CombinerRemoveDisk_Undo's prefix; same body).</summary>
        internal static void CombinerRemoveBefore(WaterDisk disk)
        {
            if (s_laying || s_scope == 0) return;
            NoteGone(disk);
        }
    }
}
