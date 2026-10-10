using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Undo / redo for the new TerrainTile system.
    //
    // The legacy TerrainUndoManager in CustomTerrain/Tools is wired to
    // DirtFloorMeshCombiner + ZonePaintLayer — both being phased out by
    // Phase 8e. Rather than retrofit that code, this file gives the new
    // tile pipeline its own focused undo stack:
    //
    //   * Per-op snapshot of EVERY overlapping tile's Heights / HeightModified
    //     / Paint / PaintModified / Biome / BiomeModified in the brush
    //     radius. Snapshots are taken at BeginOperation and an after-state
    //     diff at EndOperation produces the delta list.
    //   * Stack of TileUndoEntry — each entry knows how to apply itself in
    //     either direction (undo writes Before, redo writes After). Each
    //     application walks the affected tiles, mutates the per-vertex state
    //     in-place via TileAnchor.ApplyOpAndSave so the change persists
    //     through ZDO + mesh re-renders.
    //   * Plugs into ServerDevcommands.UndoManager via the same dynamic
    //     IUndoAction proxy pattern TerrainUndoManager uses — that way the
    //     vanilla "undo" / "redo" console commands and any WEC keybind pick
    //     up our tile ops automatically.
    //   * Also exposes Undo() / Redo() statics so console commands or future
    //     IH hooks can drive it directly.
    //
    // The whole thing is local-only — undo is a per-player concept and we
    // do NOT broadcast undo to peers. If MP-sync is needed later, the same
    // RPC pipeline as TileBrushRPC can wrap the apply.
    public static class TileUndoLog
    {
        // Configurable cap so a marathon paint session doesn't eat unbounded
        // memory. Matches TerrainUndoManager.MAX_UNDO_HISTORY.
        private const int MAX_HISTORY = 100;

        private static readonly List<TileUndoEntry> _undoStack = new List<TileUndoEntry>();
        private static readonly List<TileUndoEntry> _redoStack = new List<TileUndoEntry>();

        private static PendingSnapshot _pending;

        // WEC integration — cached so we register entries with the same
        // ServerDevcommands.UndoManager that holds the legacy combiner
        // undo entries. The vanilla "undo" hotkey reads from that stack.
        private static bool _wecResolved;
        private static MethodInfo _wecAddMethod;
        private static Type _wecIUndoActionType;
        private static Type _proxyType;

        // ── Public API ───────────────────────────────────────────────────

        // Snapshot BEFORE state for every TerrainTile vertex in the radius.
        // Call ONCE at the start of a brush op. Tolerates nested calls by
        // dropping the outer (legacy ergonomics from TerrainUndoManager —
        // matches that flow so callers can pick either undo backend).
        public static void BeginOperation(string name, Vector3 center, float radius)
        {
            if (_pending != null)
            {
                // Drop the partial outer op — caller forgot End. Log so the
                // bug is visible.
                FiresLogger.LogWarning($"[TileUndoLog] BeginOperation('{name}') while previous op '{_pending.Name}' was still open — overwriting.");
            }

            _pending = new PendingSnapshot
            {
                Name = name,
                Center = center,
                Radius = radius,
                Before = new Dictionary<TileVertexKey, TileVertexSnapshot>(),
            };

            CaptureRadius(_pending.Before, center, radius);
        }

        // Diff the current tile state against the pending snapshot, build the
        // delta list, push onto the undo stack. No-op if nothing changed.
        public static void EndOperation()
        {
            if (_pending == null) return;
            var pending = _pending;
            _pending = null;

            var after = new Dictionary<TileVertexKey, TileVertexSnapshot>();
            CaptureKeys(after, pending.Before.Keys);

            var deltas = new List<TileVertexDelta>();
            foreach (var kv in pending.Before)
            {
                var before = kv.Value;
                if (!after.TryGetValue(kv.Key, out var afterSnap))
                {
                    // Tile unloaded between begin and end — skip.
                    continue;
                }
                if (before.Equals(afterSnap)) continue;
                deltas.Add(new TileVertexDelta
                {
                    Key = kv.Key,
                    Before = before,
                    After = afterSnap,
                });
            }

            if (deltas.Count == 0)
            {
                FiresLogger.LogVerbose($"[TileUndoLog] EndOperation('{pending.Name}') — 0 deltas, skipping push.");
                return;
            }

            var entry = new TileUndoEntry
            {
                Name = pending.Name,
                Center = pending.Center,
                Radius = pending.Radius,
                Deltas = deltas,
            };

            _undoStack.Insert(0, entry);
            _redoStack.Clear();
            while (_undoStack.Count > MAX_HISTORY)
                _undoStack.RemoveAt(_undoStack.Count - 1);

            TryRegisterWithWEC(entry);
            FiresLogger.LogInfo($"[TileUndoLog] Pushed '{entry.Name}' | {deltas.Count} vert deltas across radius {entry.Radius:F1} (stack depth = {_undoStack.Count})");
        }

        // Cancel without pushing. Use if a brush op aborted with no changes.
        public static void CancelOperation()
        {
            _pending = null;
        }

        // Undo the most recent op (writes Before to each affected vertex).
        // Returns a human label or null when the stack is empty.
        public static string Undo()
        {
            if (_undoStack.Count == 0) return null;
            var entry = _undoStack[0];
            _undoStack.RemoveAt(0);
            ApplyEntry(entry, useBefore: true);
            _redoStack.Insert(0, entry);
            FiresLogger.LogInfo($"[TileUndoLog] Undid '{entry.Name}' ({entry.Deltas.Count} verts) — undo depth {_undoStack.Count}, redo depth {_redoStack.Count}");
            return $"Undid: {entry.Name}";
        }

        // Redo the last undone op (writes After to each affected vertex).
        public static string Redo()
        {
            if (_redoStack.Count == 0) return null;
            var entry = _redoStack[0];
            _redoStack.RemoveAt(0);
            ApplyEntry(entry, useBefore: false);
            _undoStack.Insert(0, entry);
            FiresLogger.LogInfo($"[TileUndoLog] Redid '{entry.Name}' ({entry.Deltas.Count} verts) — undo depth {_undoStack.Count}, redo depth {_redoStack.Count}");
            return $"Redid: {entry.Name}";
        }

        public static int UndoCount => _undoStack.Count;
        public static int RedoCount => _redoStack.Count;

        public static void ClearHistory()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _pending = null;
            FiresLogger.LogInfo("[TileUndoLog] History cleared.");
        }

        // ── Snapshot + apply internals ──────────────────────────────────

        private static void CaptureRadius(Dictionary<TileVertexKey, TileVertexSnapshot> output,
            Vector3 center, float radius)
        {
            float r2 = radius * radius;
            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchorRegistry.GetAt(tileXZ);
                if (anchor == null) continue;
                var tile = anchor.GetComponentInChildren<TerrainTile>();
                if (tile == null || tile.Heights == null) continue;

                CaptureFromTile(output, tile, center, r2);
            }
        }

        private static void CaptureKeys(Dictionary<TileVertexKey, TileVertexSnapshot> output,
            IEnumerable<TileVertexKey> keys)
        {
            // Group keys by tile so we hit each TerrainTile once.
            var byTile = new Dictionary<Vector2i, List<TileVertexKey>>();
            foreach (var k in keys)
            {
                var tileKey = new Vector2i(k.TileX, k.TileZ);
                if (!byTile.TryGetValue(tileKey, out var list))
                {
                    list = new List<TileVertexKey>();
                    byTile[tileKey] = list;
                }
                list.Add(k);
            }

            foreach (var kv in byTile)
            {
                var anchor = TileAnchorRegistry.GetAt(kv.Key);
                if (anchor == null) continue;
                var tile = anchor.GetComponentInChildren<TerrainTile>();
                if (tile == null || tile.Heights == null) continue;

                foreach (var k in kv.Value)
                {
                    if (k.VertIdx < 0 || k.VertIdx >= tile.Heights.Length) continue;
                    output[k] = TileVertexSnapshot.From(tile, k.VertIdx);
                }
            }
        }

        // Inclusive-rectangle scan over the tile's vertex grid; only verts
        // inside the circle are captured. Mirrors TileBrush.ApplyBiomeToTile
        // bounds logic.
        private static void CaptureFromTile(Dictionary<TileVertexKey, TileVertexSnapshot> output,
            TerrainTile tile, Vector3 center, float r2)
        {
            int size = TerrainTileCodec.VERTEX_GRID_SIZE;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;

            // Compute vertex (i, j) range that covers the circle's bounding box.
            int sizeMaxIdx = size - 1;
            int iMin = Mathf.Clamp(Mathf.FloorToInt((center.x - Mathf.Sqrt(r2) - originX) / TerrainTile.VERTEX_SPACING), 0, sizeMaxIdx);
            int iMax = Mathf.Clamp(Mathf.CeilToInt((center.x + Mathf.Sqrt(r2) - originX) / TerrainTile.VERTEX_SPACING), 0, sizeMaxIdx);
            int jMin = Mathf.Clamp(Mathf.FloorToInt((center.z - Mathf.Sqrt(r2) - originZ) / TerrainTile.VERTEX_SPACING), 0, sizeMaxIdx);
            int jMax = Mathf.Clamp(Mathf.CeilToInt((center.z + Mathf.Sqrt(r2) - originZ) / TerrainTile.VERTEX_SPACING), 0, sizeMaxIdx);

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                if (dx * dx + dz * dz > r2) continue;

                int idx = j * size + i;
                var key = new TileVertexKey
                {
                    TileX = tile.TileXZ.x,
                    TileZ = tile.TileXZ.y,
                    VertIdx = idx,
                };
                output[key] = TileVertexSnapshot.From(tile, idx);
            }
        }

        // Walks the delta list grouped by tile, and uses TileAnchor.ApplyOpAndSave
        // so the changes get persisted to ZDO + trigger mesh re-render.
        private static void ApplyEntry(TileUndoEntry entry, bool useBefore)
        {
            // Group deltas by tile so we run one ApplyOpAndSave per tile.
            var byTile = new Dictionary<Vector2i, List<TileVertexDelta>>();
            foreach (var d in entry.Deltas)
            {
                var key = new Vector2i(d.Key.TileX, d.Key.TileZ);
                if (!byTile.TryGetValue(key, out var list))
                {
                    list = new List<TileVertexDelta>();
                    byTile[key] = list;
                }
                list.Add(d);
            }

            foreach (var kv in byTile)
            {
                var anchor = TileAnchor.Spawn(kv.Key);
                if (anchor == null) continue;

                anchor.ApplyOpAndSave(tile =>
                {
                    int wrote = 0;
                    foreach (var d in kv.Value)
                    {
                        if (d.Key.VertIdx < 0 || d.Key.VertIdx >= tile.Heights.Length) continue;
                        var snap = useBefore ? d.Before : d.After;
                        snap.ApplyTo(tile, d.Key.VertIdx);
                        wrote++;
                    }
                    return wrote;
                });
            }
        }

        // ── WEC plumbing — mirror TerrainUndoManager's pattern ──────────

        private static void TryRegisterWithWEC(TileUndoEntry entry)
        {
            try
            {
                if (!_wecResolved)
                {
                    _wecResolved = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name != "ServerDevcommands") continue;
                        var mgr = asm.GetType("ServerDevcommands.UndoManager");
                        var iface = asm.GetType("ServerDevcommands.IUndoAction");
                        if (mgr == null || iface == null) continue;
                        _wecIUndoActionType = iface;
                        _wecAddMethod = mgr.GetMethod("Add",
                            BindingFlags.Public | BindingFlags.Static,
                            null, new[] { iface }, null);
                        if (_wecAddMethod != null)
                        {
                            _proxyType = CompileProxyType(iface);
                        }
                        break;
                    }
                }
                if (_wecAddMethod == null || _proxyType == null) return;
                var proxy = Activator.CreateInstance(_proxyType, entry);
                _wecAddMethod.Invoke(null, new object[] { proxy });
            }
            catch (Exception ex)
            {
                FiresLogger.LogVerbose($"[TileUndoLog] WEC register failed: {ex.Message}");
            }
        }

        // Build a runtime type that implements IUndoAction by delegating to
        // entry.UndoApply / entry.RedoApply. Easier than dropping a hard
        // reference on ServerDevcommands. Mirrors TerrainUndoManager's
        // CustomTerrainUndoAction wrapper.
        private static Type CompileProxyType(Type iUndoActionInterface)
        {
            // The interface signature is:
            //   string Undo();
            //   string Redo();
            // ServerDevcommands invokes these from its undo console binding.
            // We compile a thin wrapper at runtime using System.Reflection.Emit.
            var asmName = new System.Reflection.AssemblyName("VATileUndoProxy");
            var asmBuilder = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(asmName,
                System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var modBuilder = asmBuilder.DefineDynamicModule("VATileUndoProxyModule");
            var typeBuilder = modBuilder.DefineType("VATileUndoProxyImpl",
                TypeAttributes.Public | TypeAttributes.Class);
            typeBuilder.AddInterfaceImplementation(iUndoActionInterface);

            var entryField = typeBuilder.DefineField("_entry", typeof(TileUndoEntry),
                FieldAttributes.Private);

            var ctorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public, CallingConventions.Standard,
                new[] { typeof(TileUndoEntry) });
            var ilCtor = ctorBuilder.GetILGenerator();
            ilCtor.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            ilCtor.Emit(System.Reflection.Emit.OpCodes.Call,
                typeof(object).GetConstructor(Type.EmptyTypes));
            ilCtor.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            ilCtor.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            ilCtor.Emit(System.Reflection.Emit.OpCodes.Stfld, entryField);
            ilCtor.Emit(System.Reflection.Emit.OpCodes.Ret);

            EmitForward(typeBuilder, entryField, iUndoActionInterface, "Undo",
                typeof(TileUndoEntry).GetMethod(nameof(TileUndoEntry.UndoApply)));
            EmitForward(typeBuilder, entryField, iUndoActionInterface, "Redo",
                typeof(TileUndoEntry).GetMethod(nameof(TileUndoEntry.RedoApply)));

            return typeBuilder.CreateType();
        }

        private static void EmitForward(System.Reflection.Emit.TypeBuilder typeBuilder,
            FieldInfo entryField, Type iface, string methodName, MethodInfo targetMethod)
        {
            var method = typeBuilder.DefineMethod(methodName,
                MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(string), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Ldfld, entryField);
            il.Emit(System.Reflection.Emit.OpCodes.Call, targetMethod);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            typeBuilder.DefineMethodOverride(method, iface.GetMethod(methodName));
        }
    }

    // ── Data types ──────────────────────────────────────────────────────

    internal class PendingSnapshot
    {
        public string Name;
        public Vector3 Center;
        public float Radius;
        public Dictionary<TileVertexKey, TileVertexSnapshot> Before;
    }

    public class TileUndoEntry
    {
        public string Name;
        public Vector3 Center;
        public float Radius;
        public List<TileVertexDelta> Deltas;

        // WEC proxy targets — these run on the same thread as the player's
        // console command, so direct in-place state writes are safe.
        public string UndoApply()
        {
            ApplyDirection(true);
            return $"Undid: {Name}";
        }

        public string RedoApply()
        {
            ApplyDirection(false);
            return $"Redid: {Name}";
        }

        private void ApplyDirection(bool useBefore)
        {
            var byTile = new Dictionary<Vector2i, List<TileVertexDelta>>();
            foreach (var d in Deltas)
            {
                var key = new Vector2i(d.Key.TileX, d.Key.TileZ);
                if (!byTile.TryGetValue(key, out var list))
                {
                    list = new List<TileVertexDelta>();
                    byTile[key] = list;
                }
                list.Add(d);
            }
            foreach (var kv in byTile)
            {
                var anchor = TileAnchor.Spawn(kv.Key);
                if (anchor == null) continue;
                anchor.ApplyOpAndSave(tile =>
                {
                    int wrote = 0;
                    foreach (var d in kv.Value)
                    {
                        if (d.Key.VertIdx < 0 || d.Key.VertIdx >= tile.Heights.Length) continue;
                        var snap = useBefore ? d.Before : d.After;
                        snap.ApplyTo(tile, d.Key.VertIdx);
                        wrote++;
                    }
                    return wrote;
                });
            }
        }
    }

    public struct TileVertexKey : IEquatable<TileVertexKey>
    {
        public int TileX;
        public int TileZ;
        public int VertIdx;

        public bool Equals(TileVertexKey other) =>
            TileX == other.TileX && TileZ == other.TileZ && VertIdx == other.VertIdx;
        public override bool Equals(object obj) => obj is TileVertexKey k && Equals(k);
        public override int GetHashCode() => unchecked(TileX * 397 ^ TileZ * 17 ^ VertIdx);
    }

    public struct TileVertexSnapshot : IEquatable<TileVertexSnapshot>
    {
        public float Height;
        public bool HeightModified;
        public Color32 Paint;
        public bool PaintModified;
        public ushort Biome;
        public bool BiomeModified;

        public static TileVertexSnapshot From(TerrainTile tile, int idx) => new TileVertexSnapshot
        {
            Height = tile.Heights[idx],
            HeightModified = tile.HeightModified[idx],
            Paint = tile.Paint[idx],
            PaintModified = tile.PaintModified[idx],
            Biome = tile.Biome != null && idx < tile.Biome.Length ? tile.Biome[idx] : (ushort)0,
            BiomeModified = tile.BiomeModified != null && idx < tile.BiomeModified.Length && tile.BiomeModified[idx],
        };

        public void ApplyTo(TerrainTile tile, int idx)
        {
            tile.Heights[idx] = Height;
            tile.HeightModified[idx] = HeightModified;
            tile.Paint[idx] = Paint;
            tile.PaintModified[idx] = PaintModified;
            if (tile.Biome != null && idx < tile.Biome.Length)
                tile.Biome[idx] = Biome;
            if (tile.BiomeModified != null && idx < tile.BiomeModified.Length)
                tile.BiomeModified[idx] = BiomeModified;
        }

        public bool Equals(TileVertexSnapshot other) =>
            Height == other.Height
            && HeightModified == other.HeightModified
            && Paint.r == other.Paint.r && Paint.g == other.Paint.g
            && Paint.b == other.Paint.b && Paint.a == other.Paint.a
            && PaintModified == other.PaintModified
            && Biome == other.Biome
            && BiomeModified == other.BiomeModified;
        public override bool Equals(object obj) => obj is TileVertexSnapshot s && Equals(s);
        public override int GetHashCode() => unchecked(
            Height.GetHashCode() * 397
            ^ (HeightModified ? 1 : 0)
            ^ (Paint.r << 16 | Paint.g << 8 | Paint.b)
            ^ (Biome << 4));
    }

    public struct TileVertexDelta
    {
        public TileVertexKey Key;
        public TileVertexSnapshot Before;
        public TileVertexSnapshot After;
    }
}
