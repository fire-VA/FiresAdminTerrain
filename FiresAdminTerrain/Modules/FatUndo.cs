using System;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.Undo;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain
{
    /// <summary>
    /// FAT's edits in the ONE undo history (Fire 10-05: numpad 7 = undo, numpad 9 = redo, as Infinity Hammer; "undo for every FAT
    /// edit"). The history is Core's (FiresCore.Undo.UndoHistory, 0.2.296): it forwards every step into Server Devcommands' UndoManager
    /// when that is installed (so IH's keys and the console undo / redo step through FAT's edits mixed with IH's and WEC's), and keeps
    /// them itself otherwise. FAT only records steps here:
    /// - chunk / area / skyland placements and biome paint: the server's history, so the step asks the server (ZoneModeChunkKeys);
    /// - a PLAYER's terrain stroke: every V-tool (carve / fill / cube / sphere / level / mop / fall / river / pour / pool), a sky-island
    ///   op (IH / WEC / the hoe on an island, FiresSkyIslandTerrainEdit) and a console water placement. Each chunk the stroke reached
    ///   keeps its voxel edit blob (edit bounds + the water-mop tail) and its waterfalls (vv_falls) from before the stroke, laid back
    ///   through the anchors every peer restores from; each drawn water disk the stroke created is removed by its stable hash (and made
    ///   again on redo). A stroke closes after StrokeIdleSeconds without a dab, so a drag is one step.
    /// Only a player's edit is recorded: the hooks open a scope around the tool's own dispatch, and VoxelWorld.ApplyBrush's hook
    /// records nothing outside one (the location level guard, island stamps, tests and a dedicated server never make steps).
    /// Not undone: a drawn disk an edit only resized or deepened (it was not created by the stroke).
    /// Debug lines: Core's "[Undo] + step / undo / redo", plus "[FatUndo] stroke …".
    /// </summary>
    public static class FatUndo
    {
        /// <summary>Record one FAT step.</summary>
        public static void Record(string what, Func<string> undo, Func<string> redo) => UndoHistory.Record(what, undo, redo);

        /// <summary>Per frame on a client (ZoneModeDiagnostics' tick): closes a finished stroke.</summary>
        internal static void ClientTick() => CloseIdleStroke();

        // ── the player-edit scope ───────────────────────────────────────────────────────────────────────────────────────
        private static int s_scope;
        private static string s_scopeWhat;

        private static bool OnClient => ZNet.instance != null && !ZNet.instance.IsDedicated() && Player.m_localPlayer != null;

        /// <summary>A player's tool edit starts (scopes nest; the outermost names the stroke).</summary>
        public static void BeginPlayerEdit(string what)
        {
            if (s_scope++ == 0) s_scopeWhat = what;
        }

        public static void EndPlayerEdit()
        {
            if (s_scope > 0 && --s_scope == 0) s_scopeWhat = null;
        }

        // ── strokes ─────────────────────────────────────────────────────────────────────────────────────────────────────
        private const float StrokeIdleSeconds = 0.4f;

        private sealed class Stroke
        {
            public string What;
            public float LastDab;
            public readonly Dictionary<Vector3Int, ChunkState> Before = new Dictionary<Vector3Int, ChunkState>();
            public readonly List<DiskNote> Disks = new List<DiskNote>();
        }

        // A drawn water disk the stroke created: its stable hash (undo removes it) and its serialized self (redo makes it again).
        private sealed class DiskNote
        {
            public int Hash;
            public byte[] Data;
            public bool Custom;
        }

        private static Stroke s_stroke;
        private static bool s_laying;

        private static Stroke Current()
        {
            if (s_stroke == null) s_stroke = new Stroke { What = s_scopeWhat ?? "a terrain edit" };
            s_stroke.LastDab = Time.unscaledTime;
            return s_stroke;
        }

        /// <summary>
        /// Call before a voxel edit is applied (VoxelWorld.ApplyBrush, VoxelWorld.MopWater, a fall's removal): inside a player's edit,
        /// each chunk the box reaches has its state kept, the first time this stroke touches it. Undo and redo never come through here.
        /// </summary>
        public static void BeforeVoxelEdit(string what, Vector3 center, float radius, float height)
        {
            if (s_laying || s_scope == 0 || !OnClient) return;
            try
            {
                float r = Mathf.Max(radius, 0.5f) + 1f, h = Mathf.Max(height, radius) + 1f;
                Vector3Int min = Vector3Int.FloorToInt(center - new Vector3(r, h, r)), max = Vector3Int.CeilToInt(center + new Vector3(r, h, r));
                var stroke = Current();
                int cells = VoxelChunk.CELLS;
                for (int kz = Floor(min.z, cells); kz <= Floor(max.z, cells); kz++)
                    for (int ky = Floor(min.y, cells); ky <= Floor(max.y, cells); ky++)
                        for (int kx = Floor(min.x, cells); kx <= Floor(max.x, cells); kx++)
                        {
                            var key = new Vector3Int(kx, ky, kz);
                            if (stroke.Before.ContainsKey(key)) continue;
                            stroke.Before[key] = ChunkState.Take(key);
                        }
            }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] {what} capture threw: {ex.Message}"); }
        }

        // Late water ([seasons], 10-05): Vpool's pool (and Vfall's plunge pool) is laid by WaterShapingTools.LayWater up to 10 s after
        // the click, once the carved bed shows, so after its stroke closed. A late disk joins the stroke whose chunks hold its centre
        // (still open, or closed within RecentSeconds: the step's undo/redo read the same disk list); else it is a step of its own.
        private const float RecentSeconds = 30f;
        private const int RecentKept = 8;
        private static int s_late;

        private sealed class Closed
        {
            public HashSet<Vector3Int> Keys;
            public List<DiskNote> Disks;
            public string What;
            public float At;
        }

        private static readonly List<Closed> s_recent = new List<Closed>();

        private static void NoteDisk(int hash, WaterDisk disk, bool custom)
        {
            if (s_laying || !OnClient || hash == 0 || disk == null || (s_scope == 0 && s_late == 0)) return;
            try
            {
                var note = new DiskNote { Hash = hash, Data = disk.Serialize(), Custom = custom };
                if (s_scope > 0) { Current().Disks.Add(note); return; }
                var key = KeyOf(disk.Position);
                if (s_stroke != null && s_stroke.Before.ContainsKey(key)) { s_stroke.Disks.Add(note); return; }
                s_recent.RemoveAll(c => Time.unscaledTime - c.At > RecentSeconds);
                for (int i = s_recent.Count - 1; i >= 0; i--)
                {
                    if (!s_recent[i].Keys.Contains(key)) continue;
                    s_recent[i].Disks.Add(note);
                    FiresLogger.LogDebug($"[FatUndo] late water disk {hash} joined the step {s_recent[i].What} ({Time.unscaledTime - s_recent[i].At:0.0} s after it closed)");
                    return;
                }
                var alone = new List<DiskNote> { note };
                FiresLogger.LogDebug($"[FatUndo] late water disk {hash} matched no recent stroke; its own step");
                Record("a pool's water (1 water disk)", () => Lay(new Dictionary<Vector3Int, ChunkState>(), alone, false),
                                                       () => Lay(new Dictionary<Vector3Int, ChunkState>(), alone, true));
            }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] water disk note threw: {ex.Message}"); }
        }

        private static Vector3Int KeyOf(Vector3 p)
        {
            int cells = VoxelChunk.CELLS;
            return new Vector3Int(Floor(Mathf.FloorToInt(p.x), cells), Floor(Mathf.FloorToInt(p.y), cells), Floor(Mathf.FloorToInt(p.z), cells));
        }

        private static void CloseIdleStroke()
        {
            if (s_stroke == null || Time.unscaledTime - s_stroke.LastDab < StrokeIdleSeconds) return;
            var stroke = s_stroke;
            s_stroke = null;
            var before = new Dictionary<Vector3Int, ChunkState>();
            var after = new Dictionary<Vector3Int, ChunkState>();
            foreach (var pair in stroke.Before)
            {
                var now = ChunkState.Take(pair.Key);
                if (pair.Value.Blob == null || now.Blob == null || pair.Value.SameAs(now)) continue;   // unchanged: not part of the step
                before[pair.Key] = pair.Value;
                after[pair.Key] = now;
            }
            var disks = new List<DiskNote>(stroke.Disks);
            if (before.Count == 0 && disks.Count == 0)
            {
                FiresLogger.LogDebug($"[FatUndo] stroke {stroke.What} changed nothing ({stroke.Before.Count} chunk(s) looked at); no step");
                return;
            }
            FiresLogger.LogDebug($"[FatUndo] stroke {stroke.What}: {before.Count} of {stroke.Before.Count} chunk(s) changed, {disks.Count} water disk(s) drawn");
            string what = disks.Count > 0 ? $"{stroke.What} ({before.Count} chunk(s), {disks.Count} water disk(s))" : $"{stroke.What} ({before.Count} chunk(s))";
            Record(what, () => Lay(before, disks, false), () => Lay(after, disks, true));
            // Late water (a pool laid once its bed shows) still joins this step: the lambdas above read the same list.
            s_recent.Add(new Closed { Keys = new HashSet<Vector3Int>(stroke.Before.Keys), Disks = disks, What = what, At = Time.unscaledTime });
            if (s_recent.Count > RecentKept) s_recent.RemoveAt(0);
        }

        // Every chunk back to the kept state, saved to its anchor (the record every peer restores from); the stroke's disks removed
        // (undo) or made again (redo).
        private static string Lay(Dictionary<Vector3Int, ChunkState> chunks, List<DiskNote> disks, bool redo)
        {
            s_laying = true;
            try
            {
                int laid = 0, water = 0;
                foreach (var pair in chunks)
                    if (pair.Value.Put(pair.Key)) laid++;
                foreach (var disk in disks)
                    if (redo ? Redraw(disk) : Remove(disk)) water++;
                return disks.Count > 0 ? $"{laid} of {chunks.Count} chunk(s) of terrain laid back, {water} of {disks.Count} water disk(s) {(redo ? "drawn again" : "removed")}"
                                       : $"{laid} of {chunks.Count} chunk(s) of terrain laid back";
            }
            finally { s_laying = false; }
        }

        private static bool Remove(DiskNote note)
        {
            try { WaterOperationRPC.RequestRemoveWater(note.Hash); return true; }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] removing water disk {note.Hash} threw: {ex.Message}"); return false; }
        }

        private static readonly MethodInfo CreateInternal = AccessTools.Method(typeof(WaterOperationRPC), "RequestCreateWaterInternal");

        private static bool Redraw(DiskNote note)
        {
            try
            {
                var disk = WaterDisk.Deserialize(note.Data);
                if (disk == null) return false;
                int hash = note.Custom ? WaterOperationRPC.RequestCreateWaterWithCustomType(disk.Position, disk, disk.CustomLiquidType)
                    : CreateInternal != null ? (int)CreateInternal.Invoke(null, new object[] { disk }) : 0;
                if (hash != 0) note.Hash = hash;
                return hash != 0;
            }
            catch (Exception ex) { Debug.LogWarning($"[FatUndo] redrawing water disk {note.Hash} threw: {ex.InnerException?.Message ?? ex.Message}"); return false; }
        }

        // One chunk as the anchor records it: the brush-edit blob (edit bounds + mop tail) and its waterfalls (vv_falls, [seasons]'
        // ToolFalls). Put restores the blob first, then the falls, so the one anchor save carries both to the peers.
        private struct ChunkState
        {
            public byte[] Blob, Falls;

            public static ChunkState Take(Vector3Int key) => new ChunkState
            {
                Blob = VoxelWorld.SnapshotEdits(key),
                Falls = VerdantsAscent.Water.Tools.ToolFalls.Snapshot(key),
            };

            public bool SameAs(ChunkState other) => Same(Blob, other.Blob) && Same(Falls, other.Falls);

            public bool Put(Vector3Int key)
            {
                if (!VoxelWorld.RestoreEdits(key, Blob)) return false;
                VerdantsAscent.Water.Tools.ToolFalls.Restore(key, Falls);
                return true;
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static int Floor(int v, int cells) => v >= 0 ? v / cells : -((-v + cells - 1) / cells);

        // ── where a player's edit starts (Harmony, so the tools' own files stay their owners') ───────────────────────────────

        // Every V-tool dab and click: VoxelBrushDrag.DispatchNow(tool, pos, carveMod).
        [HarmonyPatch(typeof(VoxelBrushDrag), "DispatchNow")]
        private static class VoxelBrushDrag_DispatchNow_Undo
        {
            private static void Prefix(object[] __args) => BeginPlayerEdit($"a V{(__args != null && __args.Length > 0 ? __args[0]?.ToString()?.ToLowerInvariant() : "tool")} stroke");
            private static void Finalizer() => EndPlayerEdit();
        }

        // A sky-island op (Infinity Hammer / World Edit Commands' terrain line, or the hoe, on an island): voxels only, which IH's own
        // undo cannot see.
        [HarmonyPatch(typeof(FiresSkyIslandTerrainEdit), nameof(FiresSkyIslandTerrainEdit.Reshape))]
        private static class SkyIsland_Reshape_Undo
        {
            private static void Prefix(string op) => BeginPlayerEdit($"a sky-island {op}");
            private static void Finalizer() => EndPlayerEdit();
        }

        [HarmonyPatch(typeof(FiresSkyIslandTerrainEdit), nameof(FiresSkyIslandTerrainEdit.Restore))]
        private static class SkyIsland_Restore_Undo
        {
            private static void Prefix() => BeginPlayerEdit("a sky-island restore");
            private static void Finalizer() => EndPlayerEdit();
        }

        // The console water placements (va_brush_water place / place type).
        [HarmonyPatch]
        private static class WaterPlacement_Undo
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { "PlaceWater", "PlaceWaterType" })
                {
                    var method = AccessTools.Method(typeof(VerdantsAscent.Water.Tools.WaterPlacementTool), name);
                    if (method != null) yield return method;
                }
            }

            private static void Prefix() => BeginPlayerEdit("a water placement");
            private static void Finalizer() => EndPlayerEdit();
        }

        // A fall's removal changes only vv_falls (no carve): its chunks are kept before it.
        [HarmonyPatch(typeof(VerdantsAscent.Water.Tools.ToolFalls), nameof(VerdantsAscent.Water.Tools.ToolFalls.RemoveNear))]
        private static class ToolFalls_RemoveNear_Undo
        {
            private static void Prefix(Vector3 cursor, float radius) => BeforeVoxelEdit("a Vfall removal", cursor, radius + 2f, radius + 2f);
        }

        // A queued pool's water laid after its stroke (WaterShapingTools.DrainPendingWater -> LayWater): its disks are late.
        [HarmonyPatch(typeof(VerdantsAscent.Water.Tools.WaterShapingTools), "LayWater")]
        private static class WaterShapingTools_LayWater_Undo
        {
            private static void Prefix() => s_late++;
            private static void Finalizer() { if (s_late > 0) s_late--; }
        }

        // Every drawn water disk is made through one of these two (WaterPlacementTool, CustomWaterManager's factories).
        [HarmonyPatch(typeof(WaterOperationRPC), "RequestCreateWaterInternal")]
        private static class WaterCreateInternal_Undo
        {
            private static void Postfix(WaterDisk disk, int __result) => NoteDisk(__result, disk, false);
        }

        [HarmonyPatch(typeof(WaterOperationRPC), nameof(WaterOperationRPC.RequestCreateWaterWithCustomType))]
        private static class WaterCreateCustom_Undo
        {
            private static void Postfix(WaterDisk disk, int __result) => NoteDisk(__result, disk, true);
        }
    }
}
