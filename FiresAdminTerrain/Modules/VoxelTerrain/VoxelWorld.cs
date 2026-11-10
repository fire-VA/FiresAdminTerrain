using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Terrain.Voxel
{
    // Voxel P0 — sparse chunk registry + brush dispatch. MAIN THREAD only
    // (commands + driver). Chunks exist only where a brush has touched;
    // each seeds itself from the live ground on first touch so unedited
    // volume reproduces the current surface exactly.
    public static class VoxelWorld
    {
        private static readonly Dictionary<Vector3Int, VoxelChunk> s_chunks = new Dictionary<Vector3Int, VoxelChunk>();
        private static readonly Dictionary<Vector3Int, VoxelChunkView> s_views = new Dictionary<Vector3Int, VoxelChunkView>();
        // XZ column index → chunks stacked at that key (fast per-column
        // queries for the liquid-water bridge).
        private static readonly Dictionary<Vector2Int, List<VoxelChunk>> s_columns = new Dictionary<Vector2Int, List<VoxelChunk>>();

        public static int ChunkCount => s_chunks.Count;

        // Memory line: chunk array bytes, view mesh bytes and paint-mask texture bytes held right now.
        internal static void MemoryReport(out long chunkBytes, out int views, out int meshes, out long meshBytes, out int masks, out long maskBytes)
        {
            chunkBytes = 0;
            foreach (var c in s_chunks.Values) if (c != null) chunkBytes += c.HeldBytes();
            views = 0; meshes = 0; meshBytes = 0;
            foreach (var v in s_views.Values)
            {
                if (v == null) continue;
                views++;
                meshBytes += v.MeshBytes(out int n);
                meshes += n;
            }
            masks = s_paintMasks.Count; maskBytes = 0;
            foreach (var t in s_paintMasks.Values) if (t != null) maskBytes += (long)t.width * t.height * 4L;
        }
        public static IEnumerable<VoxelChunk> AllChunks => s_chunks.Values;

        // What the memory governor budgets against: chunks that actually hold
        // density arrays + a mesh/collider. Uniform (all-solid/all-air) chunks
        // carry no arrays and no GameObject, so they cost almost nothing and do
        // not count toward the RAM ceiling. s_fullChunkCount tracks it directly
        // (a running counter beats scanning s_chunks every governor pass).
        public static int BudgetedChunkCount => s_fullChunkCount;
        internal static int s_fullChunkCount;

        // Highest voxel surface (solid→air crossing) at (wx,wz) within world-Y
        // [botY, topY], reading the edited density directly — no physics, no
        // raycast-miss defaults, no async-collider races. solidAtTop = the
        // column is solid rock at topY (treat as a full-height wall).
        public static bool TrySampleSurfaceY(float wx, float wz, float topY, float botY,
            out float surfY, out bool solidAtTop)
            => SampleSurface(wx, wz, topY, botY, FiresVoxelConfig.ProbeMatchesMesh == null || FiresVoxelConfig.ProbeMatchesMesh.Value, out surfY, out solidAtTop);

        // The water callers (sheets, ponds, the liquid bridge) keep the old single-column read unless
        // "Water edges use the mesh-matched probe" is on, so the approved water look does not move.
        public static bool TrySampleWaterFloorY(float wx, float wz, float topY, float botY,
            out float surfY, out bool solidAtTop)
            => SampleSurface(wx, wz, topY, botY, FiresVoxelConfig.WaterProbeMatchesMesh != null && FiresVoxelConfig.WaterProbeMatchesMesh.Value, out surfY, out solidAtTop);

        private static float s_deltaWindow;
        private static int s_deltaLines;

        private static bool SampleSurface(float wx, float wz, float topY, float botY, bool mesh,
            out float surfY, out bool solidAtTop)
        {
            if (!mesh) return SampleSurfaceColumn(wx, wz, topY, botY, out surfY, out solidAtTop);
            surfY = float.MinValue;
            solidAtTop = false;
            var key = new Vector2Int(FloorDiv(Mathf.RoundToInt(wx), 32), FloorDiv(Mathf.RoundToInt(wz), 32));
            if (!s_columns.TryGetValue(key, out var stack)) return false;
            bool found = false;
            int fellBack = 0;
            for (int i = 0; i < stack.Count; i++)
            {
                // A chunk that cannot answer the mesh way (a neighbour cell outside its ring) answers the old way.
                bool viaMesh = stack[i].SampleColumnSurfaceMesh(wx, wz, topY, botY, out float s, out bool wall);
                bool ok = viaMesh || stack[i].SampleColumnSurface(wx, wz, topY, botY, out s, out wall);
                if (ok && !viaMesh) fellBack++;
                if (ok && s > surfY) { surfY = s; found = true; }
                if (wall) solidAtTop = true;
            }
            if (found && FiresVoxelConfig.ProbeDeltaLog != null && FiresVoxelConfig.ProbeDeltaLog.Value)
            {
                float now = (float)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);   // thread-safe: the liquid bridge samples off the main thread
                if (now - s_deltaWindow > 60f) { s_deltaWindow = now; s_deltaLines = 0; }
                if (s_deltaLines < 20 && SampleSurfaceColumn(wx, wz, topY, botY, out float old, out _))
                {
                    s_deltaLines++;
                    FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[FatProbe] ({0:0.0},{1:0.0}) mesh-matched {2:0.000} vs single-column {3:0.000} (Δ {4:+0.000;-0.000;0.000} m); path {5}; keys [Voxel Terrain] Ground probe matches the mesh = {6}, Water edges use the mesh-matched probe = {7}",
                        wx, wz, surfY, old, surfY - old,
                        fellBack > 0 ? fellBack + " chunk(s) fell back to single-column (cell outside the ring)" : "mesh",
                        FiresVoxelConfig.ProbeMatchesMesh != null ? FiresVoxelConfig.ProbeMatchesMesh.Value.ToString() : "unbound",
                        FiresVoxelConfig.WaterProbeMatchesMesh != null ? FiresVoxelConfig.WaterProbeMatchesMesh.Value.ToString() : "unbound"));
                }
            }
            return found;
        }

        private static bool SampleSurfaceColumn(float wx, float wz, float topY, float botY,
            out float surfY, out bool solidAtTop)
        {
            surfY = float.MinValue;
            solidAtTop = false;
            var key = new Vector2Int(FloorDiv(Mathf.RoundToInt(wx), 32), FloorDiv(Mathf.RoundToInt(wz), 32));
            if (!s_columns.TryGetValue(key, out var stack)) return false;

            bool found = false;
            for (int i = 0; i < stack.Count; i++)
            {
                if (stack[i].SampleColumnSurface(wx, wz, topY, botY, out float s, out bool wall))
                {
                    if (s > surfY) { surfY = s; found = true; }
                }
                if (wall) solidAtTop = true;
            }
            return found;
        }

        public static bool IsLive(VoxelChunk chunk)
            => chunk != null && s_chunks.TryGetValue(chunk.Key, out var c) && ReferenceEquals(c, chunk);

        public static bool TryGetChunk(Vector3Int key, out VoxelChunk chunk)
            => s_chunks.TryGetValue(key, out chunk);

        public static bool TryGetView(Vector3Int key, out VoxelChunkView view)
            => s_views.TryGetValue(key, out view);

        // 0.2.1089 ([VoxelSnow]): re-push every live chunk that never got its snow binding. Returns how many; total = live views.
        private static readonly List<VoxelChunkView> s_rebindTmp = new List<VoxelChunkView>();
        internal static int RebindSnow(out int total)
        {
            s_rebindTmp.Clear();
            foreach (var v in s_views.Values) if (v != null && !v.SnowBound) s_rebindTmp.Add(v);
            total = s_views.Count;
            foreach (var v in s_rebindTmp) v.RefreshBlock();
            return s_rebindTmp.Count;
        }

        // Live ky extent of a column's chunk stack — the cross-zone Y-range
        // union reads this so a face's quad OWNER always exists (audit §4-1:
        // per-column ranges made owners routinely absent at zone borders).
        public static bool TryGetColumnKyRange(Vector2Int col, out int kyLo, out int kyHi)
        {
            kyLo = int.MaxValue; kyHi = int.MinValue;
            if (!s_columns.TryGetValue(col, out var stack) || stack.Count == 0) return false;
            for (int i = 0; i < stack.Count; i++)
            {
                if (stack[i] == null) continue;
                int ky = stack[i].Key.y;
                if (ky < kyLo) kyLo = ky;
                if (ky > kyHi) kyHi = ky;
            }
            return kyHi >= kyLo;
        }

        // W1 — create an UNSEEDED chunk for the zone streamer (generation runs
        // on the worker via PrepareGenerate/RunPendingGenerate). Registers in
        // the chunk + column indices like a brush chunk.
        public static VoxelChunk CreateStreamed(Vector3Int key)
        {
            if (s_chunks.TryGetValue(key, out var existing))
            {
                PromoteToFull(existing);
                return existing;
            }
            return RegisterNewChunk(new VoxelChunk(key));
        }

        // Deep-solid / high-air stack: a chunk that participates in the Y-range
        // union + border ownership but holds no arrays, mesh or collider. An
        // existing FULL chunk is only downgraded when it carries no brush edits
        // (brushwork needs its arrays); an existing uniform chunk just retunes
        // its kind (solid↔air is free).
        public static VoxelChunk EnsureUniformChunk(Vector3Int key, ChunkKind kind)
        {
            if (s_chunks.TryGetValue(key, out var existing))
            {
                if (existing.Kind == ChunkKind.Full)
                {
                    if (existing.HasBrushEdits) return existing;
                    DropView(key);
                    existing.MakeUniform(kind);
                    if (s_fullChunkCount > 0) s_fullChunkCount--;
                }
                else existing.RetuneUniform(kind);
                return existing;
            }
            return RegisterNewChunk(VoxelChunk.NewUniform(key, kind));
        }

        public static void PromoteToFull(VoxelChunk chunk)
        {
            if (chunk == null || chunk.Kind == ChunkKind.Full) return;
            chunk.MakeFull();
            s_fullChunkCount++;
        }

        // Drop a chunk's scene presence (mesh + collider) WITHOUT dropping the
        // chunk — used when a Full chunk reclassifies to uniform.
        public static void DropView(Vector3Int key)
        {
            if (s_views.TryGetValue(key, out var view))
            {
                s_views.Remove(key);
                if (view != null) Object.Destroy(view.gameObject);
            }
        }

        // Single registration point for a freshly-constructed chunk: the chunk
        // dictionary, the per-column stack (Y-range union + liquid queries), and
        // the governor's full-chunk counter all stay in lockstep here.
        private static VoxelChunk RegisterNewChunk(VoxelChunk chunk)
        {
            s_chunks[chunk.Key] = chunk;
            var colKey = new Vector2Int(chunk.Key.x, chunk.Key.z);
            if (!s_columns.TryGetValue(colKey, out var stack))
                s_columns[colKey] = stack = new List<VoxelChunk>(2);
            stack.Add(chunk);
            if (chunk.HoldsArrays) s_fullChunkCount++;
            return chunk;
        }

        // W1 — the zone streamer drops a wrapped zone's chunks on unwrap.
        public static void DropChunkKey(Vector3Int key) => DropChunk(key);

        // P1 — anchor restore path: ensure a chunk exists (seeded from live
        // ground so the edited-gate + Vdelete semantics track current terrain),
        // ready for ImportBrushEdits.
        public static VoxelChunk GetOrCreateForRestore(Vector3Int key)
        {
            if (s_chunks.TryGetValue(key, out var existing))
            {
                // A persisted edit landing on a deep uniform chunk needs a real
                // array to import into — promote before anything reads _density.
                PromoteToFull(existing);
                // Streamed chunk still awaiting worker generation: the import
                // clamps the edits over the CURRENT density, which is all-zero
                // until generation runs, and neighbours would mirror that zero
                // seed into their rings. Run generation NOW (whoever-gets-there-
                // first contract, same as the brush path) before the import lands.
                if (existing.PendingGenerate)
                {
                    existing.SetBorderOverrides(VoxelChunk.CaptureBorderOverrides(key));
                    existing.RunPendingGenerate();
                }
                return existing;
            }
            if (s_fullChunkCount >= FiresVoxelConfig.MaxLoadedChunks.Value) return null;
            var chunk = new VoxelChunk(key);
            chunk.SeedFromGround();
            return RegisterNewChunk(chunk);
        }

        // ── P1 — debounced persistence flush ─────────────────────────────────
        // Brushed chunks save 0.75s after the last dab (not per-dab — a drag
        // stroke writes once when it settles), ≤1 chunk per frame. Called from
        // the pipeline driver's Update.
        private static readonly Dictionary<VoxelChunk, float> s_saveDirty = new Dictionary<VoxelChunk, float>();
        private static readonly List<VoxelChunk> s_saveScratch = new List<VoxelChunk>();

        private static void MarkSaveDirty(VoxelChunk chunk)
        {
            if (chunk != null) s_saveDirty[chunk] = Time.realtimeSinceStartup;
        }

        public static void FlushSaves()
        {
            if (s_saveDirty.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            s_saveScratch.Clear();
            foreach (var kv in s_saveDirty)
                if (now - kv.Value > 0.75f) s_saveScratch.Add(kv.Key);

            foreach (var chunk in s_saveScratch)
            {
                s_saveDirty.Remove(chunk);
                if (!IsLive(chunk)) continue;
                try { SaveChunk(chunk); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] save {chunk.Key} failed: {ex.Message}"); }
                break;   // ≤1 save per frame — Compress is ~1ms on 36KB
            }
        }

        // (Vfall): save a loaded chunk's anchor on the usual debounce, for a change that is not a density edit
        // (a fall added to or removed from it).
        internal static void RequestSave(Vector3Int key)
        {
            if (TryGetChunk(key, out var chunk) && chunk != null) MarkSaveDirty(chunk);
        }

        private static void SaveChunk(VoxelChunk chunk)
        {
            var center = chunk.WorldOrigin + new Vector3(16f, 16f, 16f);
            VoxelChunkAnchor anchor;
            ZDO zdo;
            if (VoxelChunkAnchor.TryGet(chunk.Key, out anchor))
            {
                var nv = anchor.Nview;
                if (nv == null || !nv.IsValid()) { FiresLogger.LogDebug($"[FiresVoxel] NOT shared: chunk {chunk.Key}'s anchor has no valid view"); return; }
                if (!nv.IsOwner()) nv.ClaimOwnership();
                zdo = nv.GetZDO();
            }
            else
            {
                // The chunk's anchor may exist with no instance here at all — a dedicated server holds none where the
                // players are. Writing into it keeps ONE anchor per chunk, so a stamp always wins over what it replaces.
                zdo = VoxelAnchorPrefab.FindZdo(chunk.Key, center);
                if (zdo != null) zdo.SetOwner(ZDOMan.GetSessionID());
                else
                {
                    var go = VoxelAnchorPrefab.Spawn(center);
                    if (go == null) return;
                    anchor = go.GetComponent<VoxelChunkAnchor>();
                    if (anchor == null) return;
                    anchor.AdoptLocal(chunk.Key);
                    var nv = anchor.Nview;
                    if (nv == null || !nv.IsValid()) return;
                    if (!nv.IsOwner()) nv.ClaimOwnership();
                    zdo = nv.GetZDO();
                }
            }

            zdo.Set(VoxelAnchorPrefab.ZDO_KX, chunk.Key.x);
            zdo.Set(VoxelAnchorPrefab.ZDO_KY, chunk.Key.y);
            zdo.Set(VoxelAnchorPrefab.ZDO_KZ, chunk.Key.z);
            zdo.Set(VoxelAnchorPrefab.ZDO_VER, VoxelAnchorPrefab.EditsFormatVersion);
            var packed = Utils.Compress(chunk.ExportBrushEdits());
            zdo.Set(VoxelAnchorPrefab.ZDO_EDITS, packed);
            VerdantsAscent.Water.Tools.ToolFalls.WriteTo(chunk.Key, zdo);   // the chunk's Vfall falls ride its anchor
            if (anchor != null) anchor.MarkSelfWrite();
            // Evidence (at Debug): the edit is now in the shared chunk record that the server and the other
            // peers read. A brush whose dab shows locally but never reaches this line never left this client.
            if (FiresLogger.DebugEnabled) FiresLogger.LogDebug($"[FiresVoxel] shared chunk {chunk.Key}: {packed.Length} bytes packed, rev {zdo.DataRevision}, owner {(zdo.GetOwner() == ZDOMan.GetSessionID() ? "me" : zdo.GetOwner().ToString())}");
        }

        // MOP on GENERATED water — the mop tool works on carve+fill water
        // too: columns are marked dry (persisted as a sparse tail on the
        // anchor blob), then the touched chunks save and remesh exactly like
        // a brush stroke. Returns columns mopped.
        // The layers around a mop, so a sky island's water (y 600) can be mopped, and a mop up there leaves the
        // ground's water below alone. A mop below y 224 scans exactly the old ky -2..7.
        private static void MopLayers(Vector3 center, float radius, out int kyLo, out int kyHi)
        {
            kyLo = -2; kyHi = 7;
            if (center.y - radius > 7 * VoxelChunk.CELLS)
            {
                kyLo = Mathf.FloorToInt((center.y - radius) / VoxelChunk.CELLS) - 1;
                kyHi = Mathf.FloorToInt((center.y + radius) / VoxelChunk.CELLS) + 1;
            }
        }

        // 0.2.1334: does a mop at this centre reach a water surface at height y? The same layers MopWater masks.
        internal static bool MopReachesY(Vector3 center, float radius, float y)
        {
            MopLayers(center, radius, out int kyLo, out int kyHi);
            return y >= kyLo * VoxelChunk.CELLS && y < (kyHi + 1) * VoxelChunk.CELLS;
        }

        // 0.2.1334: the chunks of one column (null when none); read only.
        internal static List<VoxelChunk> ColumnChunks(Vector2Int column)
            => s_columns.TryGetValue(column, out var stack) ? stack : null;

        // Some loaded chunk holds a mop mask (set by a mop or an imported anchor): until then drawn water skips the lookup.
        internal static volatile bool AnyMops;

        // Vmop: mask (or, restore = true, unmask) every column in the disc, in the layers around the mop. The mask
        // persists on the chunk anchors (the mop tail of the edit blob), so a regen, reload, rejoin or peer keeps it.
        // Returns columns changed; wetGen = of those, how many held generated water.
        // 0.2.1324 (unmop on lay): captureUndo = false skips this call's own capture, for a caller that already kept the chunks
        // in the stroke it belongs to (UnmopUnderNewWater: the water stroke's undo step, not a Vmop step).
        public static int MopWater(Vector3 center, float radius, bool restore, out int wetGen, bool captureUndo = true)
        {
            if (captureUndo) FatUndo.BeforeVoxelEdit(restore ? "a Vmop restore stroke" : "a Vmop stroke", center, radius, radius);   // one undo step per stroke
            wetGen = 0;
            int total = 0;
            int x0 = Mathf.FloorToInt((center.x - radius) / VoxelChunk.CELLS), x1 = Mathf.FloorToInt((center.x + radius) / VoxelChunk.CELLS);
            int z0 = Mathf.FloorToInt((center.z - radius) / VoxelChunk.CELLS), z1 = Mathf.FloorToInt((center.z + radius) / VoxelChunk.CELLS);
            MopLayers(center, radius, out int kyLo, out int kyHi);
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                    for (int ky = kyLo; ky <= kyHi; ky++)
                    {
                        if (!TryGetChunk(new Vector3Int(cx, ky, cz), out var chunk) || chunk == null) continue;
                        int wet = 0;
                        int n = restore ? chunk.UnmopColumns(center, radius) : chunk.MopColumns(center, radius, out wet);
                        if (n <= 0) continue;
                        total += n;
                        wetGen += wet;
                        MarkSaveDirty(chunk);
                        VoxelMeshPipeline.EnqueueRemesh(chunk);
                    }
            return total;
        }

        // 0.2.1324 (unmop on lay; log-proven: Fire's Vriver stroke laid 10 disks but every probe said "DRY: mopped" because 21
        // earlier Vmop strokes had masked those columns): placing water overrides an earlier mop. Every player water-disk lay
        // (CustomWaterManager.Place, WaterPlacementTool) clears the mask in the new disk's disc through the same chunk mask,
        // MarkSaveDirty (the anchor's mop tail: save, reload, peers) and remesh as a Vmop restore. Undo: the chunks are kept in
        // the OPEN stroke first (BeforeVoxelEdit only adds to the current stroke, first touch wins) and MopWater skips its own
        // capture, so the water stroke's one undo step lays the blobs back WITH their old mop tail (mask as it was, disks
        // removed); redo lays the after-blobs (unmopped) and redraws the disks.
        private static float s_unmopLogAt = -100f;
        private static bool s_unmopInfoDone;

        internal static int UnmopUnderNewWater(Vector3 center, float radius)
        {
            if (!AnyMops || radius <= 0f) return 0;
            FatUndo.BeforeVoxelEdit("a water lay's unmop", center, radius, radius);   // joins the open water stroke (no step of its own)
            int cols = MopWater(center, radius, true, out _, captureUndo: false);
            if (cols <= 0) return 0;
            int drawn = VerdantsAscent.Water.Core.WaterMeshCombiner.RefreshMopsIn(center, radius);
            VerdantsAscent.Water.Core.PlacedWaterSheets.MarkDirtyIn(center, radius);
            VerdantsAscent.Water.Core.PlacedWaterSolids.MarkAllDirty("unmop on lay");
            if (Time.unscaledTime - s_unmopLogAt > 1f)
            {
                s_unmopLogAt = Time.unscaledTime;
                FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[FiresWater] unmopped {0} column(s) under new water (disk at {1:0.0},{2:0.0} r {3:0.0}); {4} drawn water bod(ies) rebuilt",
                    cols, center.x, center.z, radius, drawn));
            }
            if (!s_unmopInfoDone)
            {
                s_unmopInfoDone = true;
                FiresLogger.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[FiresWater] 0.2.1324 unmop on lay: new water cleared {0} mopped column(s) (disk at {1:0.0},{2:0.0} r {3:0.0})",
                    cols, center.x, center.z, radius));
            }
            return cols;
        }

        // Is the column at (x, z) mopped in the layer holding height y (or the one under it)?
        internal static bool IsMoppedAt(float x, float y, float z)
        {
            if (!AnyMops) return false;
            int cx = FloorDiv(Mathf.RoundToInt(x), 32), cz = FloorDiv(Mathf.RoundToInt(z), 32);
            int ky = FloorDiv(Mathf.FloorToInt(y), 32);
            for (int k = ky - 1; k <= ky; k++)
                if (TryGetChunk(new Vector3Int(cx, k, cz), out var chunk) && chunk != null && chunk.IsColumnMopped(x, z))
                    return true;
            return false;
        }

        public static int MopGeneratedWater(Vector3 center, float radius)
        {
            int total = 0;
            int x0 = Mathf.FloorToInt((center.x - radius) / VoxelChunk.CELLS);
            int x1 = Mathf.FloorToInt((center.x + radius) / VoxelChunk.CELLS);
            int z0 = Mathf.FloorToInt((center.z - radius) / VoxelChunk.CELLS);
            int z1 = Mathf.FloorToInt((center.z + radius) / VoxelChunk.CELLS);
            MopLayers(center, radius, out int kyLo, out int kyHi);
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                    for (int ky = kyLo; ky <= kyHi; ky++)
                    {
                        if (!TryGetChunk(new Vector3Int(cx, ky, cz), out var chunk) || chunk == null) continue;
                        int nMopped = chunk.MopGenWater(center, radius);
                        if (nMopped <= 0) continue;
                        total += nMopped;
                        MarkSaveDirty(chunk);
                        VoxelMeshPipeline.EnqueueRemesh(chunk);
                    }
            return total;
        }

        // Shape brush across every overlapping chunk. Carve/Fill seed missing
        // chunks on demand; Restore only ever touches EXISTING chunks (deleting
        // where nothing exists is a no-op) and drops chunks that return fully
        // to their seeded ground state. Returns a console-ready summary line.
        // The chunk columns a 64 m zone stands on: two by two, since a zone's corners fall on chunk boundaries.
        internal const int ZoneChunksPerAxis = 2;

        internal static void ZoneChunkOrigin(Vector2s zone, out int kx, out int kz)
        {
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            kx = FloorDiv(Mathf.FloorToInt(centre.x - Zones.ZoneOpenSet.ZoneSize * 0.5f), 32);
            kz = FloorDiv(Mathf.FloorToInt(centre.z - Zones.ZoneOpenSet.ZoneSize * 0.5f), 32);
        }

        // A zone's whole volume between floorY and ceilY, caves and overhangs included, for a blueprint. Chunks that
        // are not built yet are generated here, so a zone saves the same whether or not anyone has stood in it.
        internal static sbyte[] CaptureZoneVolume(Vector2s zone, float floorY, float ceilY, out int chunkMinY, out int chunkCountY)
        {
            ZoneChunkOrigin(zone, out int kx0, out int kz0);
            chunkMinY = FloorDiv(Mathf.FloorToInt(floorY), 32);
            int chunkMaxY = FloorDiv(Mathf.CeilToInt(ceilY), 32);
            chunkCountY = chunkMaxY - chunkMinY + 1;
            var volume = new sbyte[ZoneChunksPerAxis * ZoneChunksPerAxis * chunkCountY * VoxelChunk.SampleCount];
            int slot = 0;
            for (int ky = chunkMinY; ky <= chunkMaxY; ky++)
            {
                for (int dz = 0; dz < ZoneChunksPerAxis; dz++)
                {
                    for (int dx = 0; dx < ZoneChunksPerAxis; dx++, slot++)
                    {
                        var chunk = EnsureFullChunk(new Vector3Int(kx0 + dx, ky, kz0 + dz));
                        var density = chunk?.ExportDensity();
                        if (density == null) continue;
                        System.Buffer.BlockCopy(density, 0, volume, slot * VoxelChunk.SampleCount, VoxelChunk.SampleCount);
                    }
                }
            }
            return volume;
        }

        // ── Area blueprints: any whole-metre box, masked ─────────────────────
        // A box of world samples, one per metre, x fastest then y then z. `keep(x, y, z)` (world metres) says which
        // samples belong to the capture; the rest are VoxelChunk.KeepSample. Chunks not built yet are generated here.
        internal static sbyte[] CaptureBox(Vector3Int origin, Vector3Int size, System.Func<int, int, int, bool> keep)
        {
            var grid = new sbyte[size.x * size.y * size.z];
            for (int i = 0; i < grid.Length; i++) grid[i] = VoxelChunk.KeepSample;
            int cells = VoxelChunk.CELLS;
            Vector3Int lo = ChunkOf(origin), hi = ChunkOf(origin + size - Vector3Int.one);
            for (int kz = lo.z; kz <= hi.z; kz++)
                for (int ky = lo.y; ky <= hi.y; ky++)
                    for (int kx = lo.x; kx <= hi.x; kx++)
                    {
                        var chunk = EnsureChunkWithEdits(new Vector3Int(kx, ky, kz));
                        var density = chunk?.ExportDensity();
                        if (density == null) continue;
                        for (int lz = 0; lz < cells; lz++)
                            for (int ly = 0; ly < cells; ly++)
                                for (int lx = 0; lx < cells; lx++)
                                {
                                    int wx = kx * cells + lx, wy = ky * cells + ly, wz = kz * cells + lz;
                                    int gx = wx - origin.x, gy = wy - origin.y, gz = wz - origin.z;
                                    if (gx < 0 || gy < 0 || gz < 0 || gx >= size.x || gy >= size.y || gz >= size.z) continue;
                                    if (!keep(wx, wy, wz)) continue;
                                    grid[gx + size.x * (gy + size.y * gz)] = density[VoxelChunk.SampleIndex(lx, ly, lz)];
                                }
                    }
            return grid;
        }

        // A captured box laid down with its origin at `origin`. Only the samples it holds change; every chunk that
        // shares one of them, ring samples included, takes it, so the seams agree.
        internal static int StampBox(sbyte[] grid, Vector3Int origin, Vector3Int size)
        {
            int cells = VoxelChunk.CELLS, touched = 0;
            Vector3Int lo = ChunkOf(origin - Vector3Int.one), hi = ChunkOf(origin + size);
            var wanted = new sbyte[VoxelChunk.SampleCount];
            for (int kz = lo.z; kz <= hi.z; kz++)
                for (int ky = lo.y; ky <= hi.y; ky++)
                    for (int kx = lo.x; kx <= hi.x; kx++)
                    {
                        bool any = false;
                        for (int lz = -1; lz <= cells; lz++)
                            for (int ly = -1; ly <= cells; ly++)
                                for (int lx = -1; lx <= cells; lx++)
                                {
                                    int gx = kx * cells + lx - origin.x, gy = ky * cells + ly - origin.y, gz = kz * cells + lz - origin.z;
                                    sbyte value = VoxelChunk.KeepSample;
                                    if (gx >= 0 && gy >= 0 && gz >= 0 && gx < size.x && gy < size.y && gz < size.z)
                                        value = grid[gx + size.x * (gy + size.y * gz)];
                                    wanted[VoxelChunk.SampleIndex(lx, ly, lz)] = value;
                                    any |= value != VoxelChunk.KeepSample;
                                }
                        if (!any) continue;
                        var chunk = EnsureChunkWithEdits(new Vector3Int(kx, ky, kz));
                        if (chunk == null || !chunk.ApplyMaskedStamp(wanted)) continue;
                        touched++;
                        MarkSaveDirty(chunk);
                        VoxelMeshPipeline.EnqueueRemesh(chunk);
                        VoxelZoneStreamer.MarkZoneBrushed(chunk.Key);
                    }
            if (touched > 0)
            {
                var centre = new Vector3(origin.x + size.x * 0.5f, origin.y + size.y * 0.5f, origin.z + size.z * 0.5f);
                FarVegImpostors.Invalidate(centre.x, centre.z);
                try { VerdantsAscent.Terrain.Clutter.ClutterManager.RespawnInRadius(centre, Mathf.Max(size.x, size.z) * 0.75f); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] clutter refresh threw: {ex.Message}"); }
            }
            return touched;
        }

        private static Vector3Int ChunkOf(Vector3Int world)
            => new Vector3Int(FloorDiv(world.x, VoxelChunk.CELLS), FloorDiv(world.y, VoxelChunk.CELLS), FloorDiv(world.z, VoxelChunk.CELLS));

        // A chunk to read or stamp, carrying every edit its anchor records. One built fresh here starts from its generated
        // land alone: captured like that it loses its edits, and stamped and saved like that it writes its anchor over with
        // the stamp alone, taking every other edit in the chunk with it. A loaded chunk already carries its anchor's edits.
        private static VoxelChunk EnsureChunkWithEdits(Vector3Int key)
        {
            bool loaded = s_chunks.ContainsKey(key);
            var chunk = EnsureFullChunk(key);
            if (chunk == null || loaded) return chunk;
            var packed = ReadAnchorEdits(key);
            if (packed != null) chunk.ImportBrushEdits(FatInflate.Gzip(packed, "voxel edits ZDO"));   // 0.2.1333: null = refused, the import rejects it
            return chunk;
        }

        // ── Undo snapshots (FatUndo) ────────────────────────────────────────────────────────────────────────────────────
        /// <summary>
        /// A chunk's whole brush-edit blob (edit bounds + water-mop tail, as its anchor stores it), with no side effects: a loaded chunk's
        /// live edits, else its anchor's, else the no-edits blob.
        /// </summary>
        internal static byte[] SnapshotEdits(Vector3Int key)
        {
            if (s_chunks.TryGetValue(key, out var chunk) && chunk != null && chunk.Kind == ChunkKind.Full) return chunk.ExportBrushEdits();
            var packed = ReadAnchorEdits(key);
            return (packed != null ? FatInflate.Gzip(packed, "voxel edits ZDO") : null) ?? NoEditsBlob();   // 0.2.1333
        }

        /// <summary>Lay a kept blob back on its chunk and save it to the anchor (peers restore from it); dry mops re-show their water.</summary>
        internal static bool RestoreEdits(Vector3Int key, byte[] blob)
        {
            if (blob == null) return false;
            var chunk = EnsureChunkWithEdits(key);
            if (chunk == null || !chunk.ImportBrushEdits(blob)) return false;
            MarkSaveDirty(chunk);
            VoxelMeshPipeline.EnqueueRemesh(chunk);
            VoxelZoneStreamer.MarkZoneBrushed(chunk.Key);
            try { VerdantsAscent.Water.Core.WaterMeshCombiner.RefreshMopsIn(ChunkCentre(key), VoxelChunk.CELLS); } catch { }
            return true;
        }

        // Every sample unedited (VoxelChunk's EditLoNone / EditHiNone), no mop tail.
        private static byte[] NoEditsBlob()
        {
            int n = VoxelChunk.SampleCount;
            var blob = new byte[n * 2];
            for (int i = 0; i < n; i++) { blob[i] = unchecked((byte)(sbyte)-127); blob[n + i] = 127; }
            return blob;
        }

        private static byte[] ReadAnchorEdits(Vector3Int key)
        {
            var zdo = VoxelAnchorPrefab.FindZdo(key, ChunkCentre(key));
            if (zdo == null || zdo.GetInt(VoxelAnchorPrefab.ZDO_VER, 0) != VoxelAnchorPrefab.EditsFormatVersion) return null;
            return zdo.GetByteArray(VoxelAnchorPrefab.ZDO_EDITS);
        }

        // The point SaveChunk files a chunk's anchor under.
        private static Vector3 ChunkCentre(Vector3Int key)
        {
            float half = VoxelChunk.CELLS * 0.5f;
            return new Vector3(key.x * VoxelChunk.CELLS + half, key.y * VoxelChunk.CELLS + half, key.z * VoxelChunk.CELLS + half);
        }

        // ── Skyland blueprints: edits travel as bounds ───────────────────────
        internal struct EditBound
        {
            internal int X, Y, Z;
            internal sbyte Lo, Hi;
        }

        // Every edited sample of the chunks between `min` and `max` (world metres) that `keep` accepts, at its world
        // sample. A loaded chunk with edits is read live; any other from its anchor, the record every peer restores from.
        internal static void CaptureEditBounds(Vector3Int min, Vector3Int max, System.Func<int, int, int, bool> keep, List<EditBound> into)
        {
            into.Clear();
            int cells = VoxelChunk.CELLS;
            Vector3Int lo = ChunkOf(min), hi = ChunkOf(max);
            for (int kz = lo.z; kz <= hi.z; kz++)
                for (int ky = lo.y; ky <= hi.y; ky++)
                    for (int kx = lo.x; kx <= hi.x; kx++)
                    {
                        var key = new Vector3Int(kx, ky, kz);
                        byte[] raw;
                        if (s_chunks.TryGetValue(key, out var chunk) && chunk.HasBrushEdits) raw = chunk.ExportBrushEdits();
                        else
                        {
                            var packed = ReadAnchorEdits(key);
                            raw = packed != null ? FatInflate.Gzip(packed, "voxel edits ZDO") : null;   // 0.2.1333
                        }
                        if (!VoxelChunk.TryReadEditBounds(raw, out var los, out var his)) continue;
                        for (int lz = 0; lz < cells; lz++)
                            for (int ly = 0; ly < cells; ly++)
                                for (int lx = 0; lx < cells; lx++)
                                {
                                    int i = VoxelChunk.SampleIndex(lx, ly, lz);
                                    if (!VoxelChunk.IsEdited(los[i], his[i])) continue;
                                    int wx = kx * cells + lx, wy = ky * cells + ly, wz = kz * cells + lz;
                                    if (keep(wx, wy, wz)) into.Add(new EditBound { X = wx, Y = wy, Z = wz, Lo = los[i], Hi = his[i] });
                                }
                    }
        }

        // Captured bounds laid down `shift` metres from where they were taken, or, with `clear`, taken off there. Only the
        // samples named change. Returns the chunks touched.
        internal static int StampEditBounds(List<EditBound> bounds, Vector3Int shift, bool clear)
        {
            int cells = VoxelChunk.CELLS;
            var byChunk = new Dictionary<Vector3Int, (List<int> Samples, List<sbyte> Lo, List<sbyte> Hi)>();
            foreach (var bound in bounds)
            {
                var at = new Vector3Int(bound.X + shift.x, bound.Y + shift.y, bound.Z + shift.z);
                var key = ChunkOf(at);
                if (!byChunk.TryGetValue(key, out var group))
                    byChunk[key] = group = (new List<int>(), new List<sbyte>(), new List<sbyte>());
                group.Samples.Add(VoxelChunk.SampleIndex(at.x - key.x * cells, at.y - key.y * cells, at.z - key.z * cells));
                group.Lo.Add(bound.Lo);
                group.Hi.Add(bound.Hi);
            }
            int touched = 0;
            foreach (var pair in byChunk)
            {
                var chunk = EnsureChunkWithEdits(pair.Key);
                if (chunk == null || !chunk.ApplyEditBounds(pair.Value.Samples, pair.Value.Lo, pair.Value.Hi, clear)) continue;
                touched++;
                MarkSaveDirty(chunk);
                VoxelMeshPipeline.EnqueueRemesh(chunk);
                VoxelZoneStreamer.MarkZoneBrushed(chunk.Key);
            }
            return touched;
        }

        // Every loaded chunk of a zone's columns forgotten, edits and all, so the streamer seeds them again from what
        // the world generates there now. A destroyed anchor never reverts its chunk, so a zone taken back with the Chaos
        // Hammer's remover runs this on every peer once its anchors are gone.
        internal static int DropZoneChunks(Vector2s zone)
        {
            ZoneChunkOrigin(zone, out int kx0, out int kz0);
            var keys = new List<Vector3Int>();
            foreach (var key in s_chunks.Keys)
                if (key.x >= kx0 && key.x < kx0 + ZoneChunksPerAxis && key.z >= kz0 && key.z < kz0 + ZoneChunksPerAxis)
                    keys.Add(key);
            foreach (var key in keys)
            {
                if (s_chunks.TryGetValue(key, out var chunk)) s_saveDirty.Remove(chunk);
                DropChunk(key);
            }
            if (keys.Count > 0) FarVegImpostors.Invalidate(ZoneSystem.GetZonePos(zone).x, ZoneSystem.GetZonePos(zone).z);
            return keys.Count;
        }

        // A saved volume, dropped on a zone as it was. Chunks above what was saved are cleared to air and chunks below
        // it filled with rock, so the saved land meets the target's own ground instead of floating over it or being
        // buried. Nothing is blended into the zones around it - a step at the border is the point.
        internal static string StampZoneVolume(Vector2s zone, sbyte[] volume, int chunkMinY, int chunkCountY,
            float clearToY, float fillFromY, out int touched)
        {
            touched = 0;
            ZoneChunkOrigin(zone, out int kx0, out int kz0);
            int kMinY = Mathf.Min(chunkMinY, FloorDiv(Mathf.FloorToInt(fillFromY), 32));
            int kMaxY = Mathf.Max(chunkMinY + chunkCountY - 1, FloorDiv(Mathf.CeilToInt(clearToY), 32));
            var saved = new sbyte[VoxelChunk.SampleCount];
            int filled = 0, cleared = 0;

            for (int ky = kMinY; ky <= kMaxY; ky++)
            {
                for (int dz = 0; dz < ZoneChunksPerAxis; dz++)
                {
                    for (int dx = 0; dx < ZoneChunksPerAxis; dx++)
                    {
                        var chunk = EnsureFullChunk(new Vector3Int(kx0 + dx, ky, kz0 + dz));
                        if (chunk == null) continue;
                        int layer = ky - chunkMinY;
                        sbyte[] wanted;
                        if (layer >= 0 && layer < chunkCountY)
                        {
                            int slot = (layer * ZoneChunksPerAxis + dz) * ZoneChunksPerAxis + dx;
                            System.Buffer.BlockCopy(volume, slot * VoxelChunk.SampleCount, saved, 0, VoxelChunk.SampleCount);
                            wanted = saved;
                        }
                        else if (ky > chunkMinY + chunkCountY - 1) { wanted = UniformAir; cleared++; }
                        else { wanted = UniformRock; filled++; }
                        if (!chunk.ApplyDensityStamp(wanted)) continue;
                        touched++;
                        MarkSaveDirty(chunk);
                        VoxelMeshPipeline.EnqueueRemesh(chunk);
                        VoxelZoneStreamer.MarkZoneBrushed(chunk.Key);
                    }
                }
            }

            Vector3 centre = ZoneSystem.GetZonePos(zone);
            if (touched > 0)
            {
                FarVegImpostors.Invalidate(centre.x, centre.z);
                try { VerdantsAscent.Terrain.Clutter.ClutterManager.RespawnInRadius(centre, Zones.ZoneOpenSet.ZoneSize * 0.75f); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] clutter refresh threw: {ex.Message}"); }
            }
            return $"{touched} chunk(s) stamped"
                + (cleared > 0 ? $", {cleared} cleared above it" : "")
                + (filled > 0 ? $", {filled} filled below it" : "");
        }

        private static readonly sbyte[] UniformAir = Uniform(-127);
        private static readonly sbyte[] UniformRock = Uniform(127);

        private static sbyte[] Uniform(sbyte value)
        {
            var blob = new sbyte[VoxelChunk.SampleCount];
            for (int i = 0; i < blob.Length; i++) blob[i] = value;
            return blob;
        }

        // A chunk with real arrays and its generation done, made here when the world has not built it yet.
        private static VoxelChunk EnsureFullChunk(Vector3Int key)
        {
            if (!s_chunks.TryGetValue(key, out var chunk))
            {
                if (s_fullChunkCount >= FiresVoxelConfig.MaxLoadedChunks.Value) return null;
                chunk = new VoxelChunk(key);
                chunk.SeedFromGround();
                RegisterNewChunk(chunk);
            }
            if (chunk.Kind != ChunkKind.Full) PromoteToFull(chunk);
            if (chunk.PendingGenerate)
            {
                chunk.SetBorderOverrides(VoxelChunk.CaptureBorderOverrides(key));
                chunk.RunPendingGenerate();
            }
            return chunk;
        }

        // > 0 while a V-tool edit runs (VoxelBrushDrag); main thread.
        internal static int VToolScope;

        private const float SeaLine = 30f;
        private static float s_nextUnderSeaNote;

        // 0.2.1052 (Fire 10-26: the river carve "left the vanilla terrain and then dug our terrain underneath"): with the
        // voxel module switched off on this peer (0.2.1336: the only case left; every world swaps edited zones, FiresVoxelConfig.Mode) a V-tool edit is
        // stored but no zone converts, so vanilla's ground stays on top and the carve is invisible. Never silent: a Warning in
        // the log and a centre message, once per 10 s. The edit itself still lands (whether to refuse it is Fire's call).
        private static float s_nextUnseenWarn;

        private static void WarnIfUnseen()
        {
            if (FiresVoxelConfig.StreamerRuns && (FiresVoxelConfig.WrapsBrushedZones || FiresVoxelConfig.WrapsAllTerrain)) return;
            if (Time.unscaledTime < s_nextUnseenWarn) return;
            s_nextUnseenWarn = Time.unscaledTime + 10f;
            string line = $"[FiresVoxel] V-tool edit stored but NOT shown: the voxel module is switched off on this peer (Voxel enabled / Fires Custom Generation)";
            FiresLogger.LogWarning(line);   // 0.2.1053: a log line only (the water tools cut vanilla ground themselves now)
        }

        // 0.2.1053: the water tools' fallback when neither the voxel nor the vanilla cut can land (no heightmap, not voxel).
        internal static void WarnIfUnseenLog(string why)
        {
            if (Time.unscaledTime < s_nextUnseenWarn) return;
            s_nextUnseenWarn = Time.unscaledTime + 10f;
            FiresLogger.LogWarning($"[FiresVoxel] V-tool edit NOT shown: {why} (mode {FiresVoxelConfig.Mode})");
        }

        public static string ApplyBrush(Vector3 center, float radius, float height, VoxelBrushShape shape, VoxelBrushMode mode)
        {
            // Players can draw their own little floating terrain that merges with the
            // chunk if it is restored: a V-tool stroke (VToolScope) may reach over a closed zone. Its edits land in the chunk
            // anchors as anywhere else, stand as the player's own terrain over the closed sea (synced, undoable), stay where they
            // are when a zone closes, and re-apply onto the land when the zone is opened or restored (the edit bounds compose with
            // whatever ground a chunk generates, so nothing is doubled or lost). Hoe / IH / WEC terrain ops stay refused there.
            if (VToolScope <= 0 && Zones.ZoneModeRefusals.TerrainEditTouchesClosed(center, radius))
                return "Refused: the brush reaches into a closed zone of this one-zone-at-a-time world.";
            if (VToolScope > 0) WarnIfUnseen();
            // One undo step per stroke (FatUndo keeps the touched chunks' edit blobs before the first dab).
            FatUndo.BeforeVoxelEdit($"a V-tool {mode} stroke", center, radius, height);
            long carveT0 = VoxelMeshPipeline.CarveTiming.On ? Stopwatch.GetTimestamp() : 0L;

            // Persist-until-edited: a far-impostor tile is a cached function of
            // the analytic ground — an edit here changes that ground, so drop
            // the overlapping tile (it rebuilds off-thread). Cheap; far tiles
            // are out of reach so this almost never actually drops one.
            FarVegImpostors.Invalidate(center.x, center.z);

            float halfH = shape == VoxelBrushShape.Cylinder ? height * 0.5f : radius;

            // World-space AABB of affected SAMPLES (+1 for boundary lerp).
            Vector3 min = new Vector3(center.x - radius - 1f, center.y - halfH - 1f, center.z - radius - 1f);
            Vector3 max = new Vector3(center.x + radius + 1f, center.y + halfH + 1f, center.z + radius + 1f);

            // Chunk key k covers world samples [k*32 - 1, k*32 + 32].
            int kMinX = FloorDiv(Mathf.FloorToInt(min.x) - 32, 32), kMaxX = FloorDiv(Mathf.CeilToInt(max.x) + 1, 32);
            int kMinY = FloorDiv(Mathf.FloorToInt(min.y) - 32, 32), kMaxY = FloorDiv(Mathf.CeilToInt(max.y) + 1, 32);
            int kMinZ = FloorDiv(Mathf.FloorToInt(min.z) - 32, 32), kMaxZ = FloorDiv(Mathf.CeilToInt(max.z) + 1, 32);

            int touched = 0, seeded = 0, capped = 0;
            float seedMs = 0f;
            var sw = new Stopwatch();

            for (int kz = kMinZ; kz <= kMaxZ; kz++)
            {
                for (int ky = kMinY; ky <= kMaxY; ky++)
                {
                    for (int kx = kMinX; kx <= kMaxX; kx++)
                    {
                        var key = new Vector3Int(kx, ky, kz);
                        // Precise overlap test vs the chunk's sample AABB.
                        float ox = kx * 32f, oy = ky * 32f, oz = kz * 32f;
                        if (ox - 1f > max.x || ox + 32f < min.x) continue;
                        if (oy - 1f > max.y || oy + 32f < min.y) continue;
                        if (oz - 1f > max.z || oz + 32f < min.z) continue;

                        if (!s_chunks.TryGetValue(key, out var chunk))
                        {
                            if (mode == VoxelBrushMode.Restore) continue;   // nothing here to delete
                            if (s_fullChunkCount >= FiresVoxelConfig.MaxLoadedChunks.Value) { capped++; continue; }
                            chunk = new VoxelChunk(key);
                            sw.Restart();
                            chunk.SeedFromGround();
                            sw.Stop();
                            seedMs += (float)sw.Elapsed.TotalMilliseconds;
                            RegisterNewChunk(chunk);
                            seeded++;
                        }

                        // Carve/fill landing on a deep uniform chunk needs real
                        // arrays; Restore on pristine (uniform) ground is a no-op.
                        if (chunk.Kind != ChunkKind.Full)
                        {
                            if (mode == VoxelBrushMode.Restore) continue;
                            PromoteToFull(chunk);
                        }

                        // Streamed chunk still awaiting worker generation —
                        // run it now (main thread, few ms) so the brush edits
                        // the GENERATED terrain instead of being overwritten.
                        // Canonical borders first, same as the worker path.
                        if (chunk.PendingGenerate)
                        {
                            chunk.SetBorderOverrides(VoxelChunk.CaptureBorderOverrides(key));
                            chunk.RunPendingGenerate();
                        }

                        if (chunk.ApplyShape(center, radius, height, shape, mode))
                        {
                            touched++;
                            if (mode == VoxelBrushMode.Restore && chunk.IsFullyRestored())
                            {
                                // No edits left — remove the anchor. A brush-only
                                // chunk goes too; a wrapped zone's chunk IS its
                                // ground, so it stays and remeshes.
                                if (VoxelChunkAnchor.TryGet(key, out var anchor) && anchor != null
                                    && ZNetScene.instance != null)
                                {
                                    var nv = anchor.Nview;
                                    if (nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();
                                    ZNetScene.instance.Destroy(anchor.gameObject);
                                }
                                s_saveDirty.Remove(chunk);
                                if (chunk.MeshAll) { VoxelMeshPipeline.MarkEditUrgent(chunk.Key); VoxelMeshPipeline.EnqueueRemesh(chunk); }
                                else DropChunk(key);
                                VoxelZoneStreamer.RefreshBrushedZone(key);
                                continue;
                            }
                            MarkSaveDirty(chunk);
                            VoxelMeshPipeline.MarkEditUrgent(chunk.Key);   // a player edit is never deferred
                            if (carveT0 != 0L) VoxelMeshPipeline.CarveTiming.Dab(chunk.Key);
                            VoxelMeshPipeline.EnqueueRemesh(chunk);
                            if (mode != VoxelBrushMode.Restore) VoxelZoneStreamer.MarkZoneBrushed(key);
                        }
                    }
                }
            }

            // Round-92 — re-roll grass/clutter over the edited area so blades
            // don't float over fresh holes or sit buried under fills (the tile
            // system's NotifyMutated does exactly this).
            if (touched > 0)
            {
                try
                {
                    float reach = Mathf.Max(radius, halfH) + 4f;
                    VerdantsAscent.Terrain.Clutter.ClutterManager.RespawnInRadius(center, reach);
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresVoxel] clutter refresh threw: {ex.Message}");
                }
            }

            // Fire 10-23: "after a certain point the mesh just breaks and drops you under the world into the ocean". The log showed no
            // chunk failure: the shaft had reached y 29, under the sea line (30), where the game's ocean (water volume, underwater
            // camera) starts whatever the voxel ground around it is. One Debug line names it when a carve reaches under it.
            if (touched > 0 && mode == VoxelBrushMode.Carve && center.y - halfH < SeaLine && Time.time >= s_nextUnderSeaNote && FiresLogger.DebugEnabled)
            {
                s_nextUnderSeaNote = Time.time + 10f;
                FiresLogger.LogDebug($"[FiresVoxel] carve reached y {center.y - halfH:0.0} at ({center.x:0},{center.z:0}), under the sea line ({SeaLine:0}): "
                    + "the game's ocean (swimming, underwater view) starts there, even inside dry voxel ground.");
            }
            if (carveT0 != 0L) VoxelMeshPipeline.CarveTiming.BrushDone((Stopwatch.GetTimestamp() - carveT0) * 1000.0 / Stopwatch.Frequency, seeded);

            string shapeTxt = shape == VoxelBrushShape.Cylinder ? $"cyl r={radius:F1} h={height:F1}"
                : shape == VoxelBrushShape.Cube ? $"cube half={radius:F1}" : $"sphere r={radius:F1}";
            string verb = mode == VoxelBrushMode.Fill ? "fill" : mode == VoxelBrushMode.Carve ? "carve" : "delete";

            // Field diagnostic: a brush that changes NOTHING names its cause
            // (aiming at air, outside voxel range, etc.) instead of feeling
            // silently dead.
            if (touched == 0 && capped == 0)
            {
                var ck = new Vector3Int(FloorDiv(Mathf.FloorToInt(center.x), 32),
                    FloorDiv(Mathf.FloorToInt(center.y), 32), FloorDiv(Mathf.FloorToInt(center.z), 32));
                string probe = s_chunks.TryGetValue(ck, out var cc)
                    ? $"chunk {ck} exists, density@center={cc.SampleDensityWorld(center)} (>0 solid, <0 air)"
                    : $"NO chunk at {ck} — center is outside the voxel field";
                // ~50 a launch, mostly re-applied pads already at their shape, so a no-op on an existing chunk is
                // Debug; a stamp outside the voxel field (no chunk) is a real miss and stays a Warning.
                if (cc == null) FiresLogger.LogWarning($"[FiresVoxel] {verb} changed nothing at ({center.x:F0},{center.y:F0},{center.z:F0}) r={radius:F1} — {probe}");
                else FiresLogger.LogDebug($"[FiresVoxel] {verb} changed nothing at ({center.x:F0},{center.y:F0},{center.z:F0}) r={radius:F1} — {probe}");
            }
            string summary = $"{verb} {shapeTxt} at ({center.x:F0},{center.y:F0},{center.z:F0}) → {touched} chunk(s), {seeded} seeded";
            if (seeded > 0) summary += $" ({seedMs:F0}ms seed)";
            if (capped > 0)
            {
                summary += $" | {capped} REFUSED (MaxLoadedChunks={FiresVoxelConfig.MaxLoadedChunks.Value})";
                // A refused chunk under a carve = a HOLE with no floor — the
                // fall-out-of-the-world report. Never let this pass quietly.
                FiresLogger.LogError($"[FiresVoxel] brush hit the chunk cap: {capped} chunk(s) SKIPPED at ({center.x:F0},{center.y:F0},{center.z:F0}) — carved areas may have NO FLOOR. Raise 'Max loaded chunks' (now {FiresVoxelConfig.MaxLoadedChunks.Value}, live {s_chunks.Count}).");
            }
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                FiresLogger.LogInfo($"[FiresVoxel] {summary} | live chunks={s_chunks.Count}");
            return summary;
        }

        // Surface crossing (density 0) in the column at (wx, wz), scanning
        // from yHint outward up to ±range metres. Reads live chunk data — the
        // truth the brushes edit — so flat tools can anchor to the REAL
        // ground even when the ruler's Y rode an offset/stale collider.
        public static bool TryFindSurfaceY(float wx, float wz, float yHint, float range, out float ySurf)
        {
            ySurf = yHint;
            // A13 ([fgn], halo ring): inside the ring's field the floor is along the ring's up (sideways on HaloFlat's vertical
            // wall), so a world-Y column scan finds band edges, not the floor. No Y answers it there: "no surface" (callers keep
            // their own fallback) and one Debug line. Ring-aware callers use TryFindSurfacePoint.
            if (FatHaloArc.Active && FatHaloArc.TryGetUp(new Vector3(wx, yHint, wz), out _))
            {
                if (!s_ringSurfaceNoted) { s_ringSurfaceNoted = true; FiresLogger.LogDebug($"[FatHalo] TryFindSurfaceY asked inside the ring's field at ({wx:0}, {yHint:0}, {wz:0}): no world-Y answer on the ring (callers fall back); ring callers use TryFindSurfacePoint"); }
                return false;
            }
            var colKey = new Vector2Int(FloorDiv(Mathf.FloorToInt(wx), 32), FloorDiv(Mathf.FloorToInt(wz), 32));
            if (!s_columns.TryGetValue(colKey, out var stack) || stack == null || stack.Count == 0) return false;

            int y0 = Mathf.FloorToInt(yHint - range), y1 = Mathf.CeilToInt(yHint + range);
            float bestY = float.MinValue, bestDist = float.MaxValue;
            sbyte prev = 0;
            bool hasPrev = false;
            for (int y = y0; y <= y1; y++)
            {
                sbyte d = SampleColumnDensity(stack, wx, y, wz, out bool ok);
                if (!ok) { hasPrev = false; continue; }
                if (hasPrev && (prev > 0) != (d > 0))
                {
                    float t = prev / (float)(prev - d);
                    float cy = (y - 1) + t;
                    float dist = Mathf.Abs(cy - yHint);
                    if (dist < bestDist) { bestDist = dist; bestY = cy; }
                }
                prev = d;
                hasPrev = true;
            }
            if (bestY == float.MinValue) return false;
            ySurf = bestY;
            return true;
        }

        private static bool s_ringSurfaceNoted;

        /// <summary>A13 ([fgn]): the surface nearest <paramref name="at"/> along the local up (the ring's up in its field, world up
        /// elsewhere), from the smooth density field, within ±range. point = the floor (solid below, air above along up).</summary>
        public static bool TryFindSurfacePoint(Vector3 at, float range, out Vector3 point, out Vector3 up)
        {
            point = at;
            if (!FatHaloArc.TryGetUp(at, out up)) up = Vector3.up;
            float best = float.MaxValue; bool found = false;
            float prev = 0f; bool hasPrev = false;
            for (float s = -range; s <= range; s += 0.5f)
            {
                if (!TrySampleDensitySmooth(at + up * s, out float d)) { hasPrev = false; continue; }
                if (hasPrev && prev > 0f && d <= 0f)
                {
                    float t = prev / (prev - d);
                    float cs = s - 0.5f + 0.5f * t;
                    if (Mathf.Abs(cs) < best) { best = Mathf.Abs(cs); point = at + up * cs; found = true; }
                }
                prev = d; hasPrev = true;
            }
            return found;
        }

        // (fat_surface_probe): every density-0 crossing in the column between yLo and yHi, sampled as
        // TryFindSurfaceY samples it, as "54.90 up" (solid below, air above: a floor) or "61.20 down" (a ceiling / underside).
        public static string DescribeColumn(float wx, float wz, float yLo, float yHi)
        {
            var colKey = new Vector2Int(FloorDiv(Mathf.FloorToInt(wx), 32), FloorDiv(Mathf.FloorToInt(wz), 32));
            if (!s_columns.TryGetValue(colKey, out var stack) || stack == null || stack.Count == 0) return "column not loaded";
            var parts = new List<string>();
            sbyte prev = 0;
            bool hasPrev = false;
            for (int y = Mathf.FloorToInt(yLo); y <= Mathf.CeilToInt(yHi); y++)
            {
                sbyte d = SampleColumnDensity(stack, wx, y, wz, out bool ok);
                if (!ok) { hasPrev = false; continue; }
                if (hasPrev && (prev > 0) != (d > 0))
                    parts.Add(((y - 1) + prev / (float)(prev - d)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + (prev > 0 ? " up" : " down"));
                prev = d;
                hasPrev = true;
            }
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        // (Vskin): is the voxel at a world point solid (density > 0)? false when the column is not loaded.
        /// <summary>
        /// The density at a world point interpolated from the 8 grid samples around it (> 0 = solid): the same field the
        /// mesh surface is drawn from. TryIsSolid reads ONE grid point, which can be solid while the surface lies under it.
        /// False when any of the 8 samples is not loaded.
        /// </summary>
        public static bool TrySampleDensitySmooth(Vector3 w, out float density)
        {
            density = 0f;
            int x0 = Mathf.FloorToInt(w.x), y0 = Mathf.FloorToInt(w.y), z0 = Mathf.FloorToInt(w.z);
            float fx = w.x - x0, fy = w.y - y0, fz = w.z - z0;
            float sum = 0f;
            for (int i = 0; i < 8; i++)
            {
                int x = x0 + (i & 1), y = y0 + ((i >> 1) & 1), z = z0 + ((i >> 2) & 1);
                var colKey = new Vector2Int(FloorDiv(x, 32), FloorDiv(z, 32));
                if (!s_columns.TryGetValue(colKey, out var stack) || stack == null || stack.Count == 0) return false;
                sbyte d = SampleColumnDensity(stack, x + 0.5f, y, z + 0.5f, out bool ok);
                if (!ok) return false;
                float wgt = ((i & 1) != 0 ? fx : 1f - fx) * (((i >> 1) & 1) != 0 ? fy : 1f - fy) * (((i >> 2) & 1) != 0 ? fz : 1f - fz);
                sum += d * wgt;
            }
            density = sum;
            return true;
        }

        public static bool TryIsSolid(float wx, float wy, float wz, out bool solid)
        {
            solid = false;
            var colKey = new Vector2Int(FloorDiv(Mathf.FloorToInt(wx), 32), FloorDiv(Mathf.FloorToInt(wz), 32));
            if (!s_columns.TryGetValue(colKey, out var stack) || stack == null || stack.Count == 0) return false;
            sbyte d = SampleColumnDensity(stack, wx, Mathf.RoundToInt(wy), wz, out bool ok);
            if (!ok) return false;
            solid = d > 0;
            return true;
        }

        // A client once read solid inside a sinkhole shaft the server had carved, so the chunk that OWNS
        // the sample (y inside its own 0..CELLS-1 band) answers first. A sample on a chunk border is also the neighbour's
        // mirrored ring sample, and the first chunk in the stack used to answer even from a ring that had not been re-mirrored
        // since the owner's edits arrived. Ring samples answer only when no owner is loaded.
        private static sbyte SampleColumnDensity(List<VoxelChunk> stack, float wx, int y, float wz, out bool ok)
        {
            int ky = FloorDiv(y, VoxelChunk.CELLS);
            for (int i = 0; i < stack.Count; i++)
            {
                var c = stack[i];
                if (c == null || c.Key.y != ky) continue;
                var v = c.SampleDensityWorld(new Vector3(wx, y, wz));
                if (v != sbyte.MinValue) { ok = true; return v; }
            }
            for (int i = 0; i < stack.Count; i++)
            {
                var c = stack[i];
                if (c == null) continue;
                var v = c.SampleDensityWorld(new Vector3(wx, y, wz));
                if (v != sbyte.MinValue) { ok = true; return v; }
            }
            ok = false;
            return 0;
        }

        private static void DropChunk(Vector3Int key)
        {
            // A dropped brush chunk's anchor stays loaded with its revision
            // already committed — without this, a rewrap regenerates pristine
            // terrain and the brushwork stays invisible until relog. Poking
            // the anchor makes its next poll re-Apply (restore + re-freeze).
            VoxelChunkAnchor.InvalidateKey(key);
            if (s_chunks.TryGetValue(key, out var chunk))
            {
                s_chunks.Remove(key);
                chunk.Dropped = true;   // a worker holding this queued chunk skips it on dequeue
                if (chunk.HoldsArrays && s_fullChunkCount > 0) s_fullChunkCount--;
                var colKey = new Vector2Int(key.x, key.z);
                if (s_columns.TryGetValue(colKey, out var stack))
                {
                    stack.Remove(chunk);
                    if (stack.Count == 0)
                    {
                        s_columns.Remove(colKey);
                        DropPaintMask(colKey);
                    }
                }
            }
            if (s_views.TryGetValue(key, out var view))
            {
                s_views.Remove(key);
                // Queued, ViewDestroysPerFrame a frame (DrainViewDestroys), not ~16 per zone in one frame. A
                // queued view of a zone the player just left keeps drawing for a frame or two, far behind them.
                if (view != null) s_viewDestroyQueue.Enqueue(view.gameObject);
            }
        }

        private static readonly Queue<GameObject> s_viewDestroyQueue = new Queue<GameObject>();
        internal const int ViewDestroysPerFrame = 4;
        internal static int ViewDestroysQueued => s_viewDestroyQueue.Count;

        internal static void DrainViewDestroys(int max)
        {
            for (int n = 0; n < max && s_viewDestroyQueue.Count > 0; n++)
            {
                var go = s_viewDestroyQueue.Dequeue();
                if (go != null) Object.Destroy(go);
            }
        }

        // Material capture/recapture fan-out: chunk views only self-heal
        // their material on remesh — sweep them all so adjacent chunks never
        // wear different instances meeting at a 32m border.
        // Push the terrain shadow-cast setting onto every live chunk so the
        // config applies without a relog (the cost/benefit is only judgeable
        // by toggling it in place).
        public static void RefreshAllViewShadows()
        {
            foreach (var kv in s_views)
                if (kv.Value != null) kv.Value.RefreshShadowMode();
        }

        // Every live chunk view, for a pass spread over frames (Vedr season recolour).
        internal static void CollectViews(List<VoxelChunkView> into)
        {
            into.Clear();
            foreach (var kv in s_views)
                if (kv.Value != null) into.Add(kv.Value);
        }

        // Vanilla's overworld render group flipped (interior entered or left).
        public static void RefreshAllViewVisibility()
        {
            foreach (var kv in s_views)
                if (kv.Value != null) kv.Value.RefreshRenderVisibility();
        }

        public static void RefreshAllViewMaterials(Material mat)
        {
            if (mat == null) return;
            foreach (var kv in s_views)
            {
                var v = kv.Value;
                if (v == null) continue;
                var rend = v.GetComponent<MeshRenderer>();
                if (rend != null && rend.sharedMaterial != mat) rend.sharedMaterial = mat;
            }
        }

        // 0.2.1322 (PLAN_fat_driver_stalls step 1, proof only): view create and paint finish phase timers.
        private static readonly StallPhaseTimer s_viewCreateTimer = new StallPhaseTimer("[FiresVoxelPipe] view create", 20.0,
            "new GameObject", "SetParent", "AddComponent view", "layer", "AddComponent filter", "AddComponent renderer", "material+shadow", "AddComponent collider", "mesh+bounds", "register", "paint bind");
        private static readonly StallPhaseTimer s_paintFinishTimer = new StallPhaseTimer("[FiresVoxelPipe] paint finish", 8.0,
            "palette", "SetPixels", "Apply upload", "G signature", "remesh G neighbours", "view re-bind", "remesh queue");
        private static readonly StallPhaseTimer s_paintBeginTimer = new StallPhaseTimer("[FiresVoxelPipe] paint begin", 8.0,
            "texture create", "pixel buffer", "dry check");

        public static VoxelChunkView GetOrCreateView(VoxelChunk chunk)
        {
            if (s_views.TryGetValue(chunk.Key, out var view) && view != null) return view;
            // 0.2.1322 (PLAN_fat_driver_stalls F2 proof, no behaviour change: view create measured 50-100 ms on 0-vert chunks): a phase
            // timer per view creation + the gen-0 GC count across it, so a GC or a nested hook inside shows (Debug 10 s, > 20 ms flagged).
            var vt = s_viewCreateTimer.T; int gc0 = StallPhaseTimer.Gc();
            vt[0] = StallPhaseTimer.Now();
            var go = new GameObject($"VoxelChunk_{chunk.Key.x}_{chunk.Key.y}_{chunk.Key.z}");
            vt[1] = StallPhaseTimer.Now();
            go.transform.SetParent(VoxelMeshPipeline.Root, worldPositionStays: true);
            vt[2] = StallPhaseTimer.Now();
            view = go.AddComponent<VoxelChunkView>();
            vt[3] = StallPhaseTimer.Now();
            view.Key = chunk.Key;
            view.Init();
            var it = VoxelChunkView.InitT;
            for (int i = 1; i <= 6; i++) vt[3 + i] = it[i];   // layer .. mesh + bounds (Init's own stamps; it[0] ~= vt[3])
            VoxelZoneStreamer.HideEditChunkUntilSwap(chunk);   // 0.2.1334: an edit chunk in a zone about to wrap waits for the swap
            view.RenderSuppressed = chunk.SuppressRenderUntilSwap;
            s_views[chunk.Key] = view;
            vt[10] = StallPhaseTimer.Now();
            BindOrQueuePaintMask(chunk.Key, view);
            vt[11] = StallPhaseTimer.Now();
            s_viewCreateTimer.Note(StallPhaseTimer.Gc() - gc0, $"chunk ({chunk.Key.x},{chunk.Key.y},{chunk.Key.z})");
            return view;
        }

        // World unload (driver OnDestroy) — views die with the scene; drop refs.
        public static void Clear()
        {
            s_chunks.Clear();
            VoxelZoneLook.Clear();
            VoxelChunkAnchor.ClearApplied();
            s_views.Clear();
            while (s_viewDestroyQueue.Count > 0) { var go = s_viewDestroyQueue.Dequeue(); if (go != null) Object.Destroy(go); }
            s_columns.Clear();
            s_fullChunkCount = 0;
            foreach (var kv in s_paintMasks)
                if (kv.Value != null) Object.Destroy(kv.Value);
            s_paintMasks.Clear();
            s_paintDirty.Clear();
        }

        // ------------------------------------------------------------------
        // Terrain paint on the voxel skin. Paint DATA already
        // round-trips (TerrainComp → ZDO → Heightmap.m_paintMask; planting and
        // clutter read it) — only the render was missing: every chunk shared
        // one material with a permanent 1x1 "no paint" _ClearedMaskTex. Each
        // XZ chunk column gets a 33x33 RGBA32 mask copied from the zone
        // heightmap's live paint data (UV0 is already chunk-local 0..1 — the
        // shader samples the mask with raw UV0, no ST uniform exists) and is
        // bound per-renderer via MaterialPropertyBlock so the shared material
        // stays untouched. Full RGBA is copied: alpha carries the worldgen
        // vegetation/lava mask (Ashlands lava needs it).
        private static readonly Dictionary<Vector2Int, Texture2D> s_paintMasks = new Dictionary<Vector2Int, Texture2D>();
        private static readonly HashSet<Vector2Int> s_paintDirty = new HashSet<Vector2Int>();
        private static readonly List<Vector2Int> s_paintScratch = new List<Vector2Int>(16);
        private static Color[] s_paintPixels;

        private static bool PaintCapable =>
            SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        internal static bool PaintColumnPending(Vector2Int colKey) => s_paintDirty.Contains(colKey) || (s_sliceActive && s_sliceKey == colKey);   // 0.2.1346

        public static void MarkPaintDirty(Vector2Int colKey)
        {
            if (!PaintCapable) return;
            // Only columns that actually have voxel chunks — everything else
            // renders vanilla (which shows paint by itself).
            if (s_columns.ContainsKey(colKey)) s_paintDirty.Add(colKey);
        }

        private static void BindOrQueuePaintMask(Vector3Int chunkKey, VoxelChunkView view)
        {
            if (!PaintCapable) return;
            var colKey = new Vector2Int(chunkKey.x, chunkKey.z);
            if (s_paintMasks.TryGetValue(colKey, out var tex) && tex != null)
                view.ApplyPaintMask(tex);
            else
            {
                view.ApplyPaintMask(VoxelLayerPaint.BlankMask);   // no lava flash on a skyland before its column's mask
                s_paintDirty.Add(colKey);
            }
        }

        // Drain from the streamer tick. Paint-only ops need NO remesh and no
        // collider bake — just a 1089-texel copy + Apply + rebind.
        public static void DrainPaintDirty(int budget)
        {
            if (!PaintCapable || s_paintDirty.Count == 0) return;
            s_paintScratch.Clear();
            foreach (var k in s_paintDirty)
            {
                s_paintScratch.Add(k);
                if (s_paintScratch.Count >= budget) break;
            }
            for (int i = 0; i < s_paintScratch.Count; i++)
            {
                s_paintDirty.Remove(s_paintScratch[i]);
                RefreshPaintMask(s_paintScratch[i]);
            }
        }

        // A TIME-SLICED drain. One column is composed row by row (33 rows x 33 texels) across frames
        // until a Stopwatch passes budgetMs; the texture upload + rebind happen once its last row is done. A column dropped meanwhile is
        // abandoned; one re-dirtied meanwhile is simply refreshed again later. Called every frame from VoxelPipelineDriver.
        private static Vector2Int s_sliceKey;
        private static bool s_sliceActive;
        private static int s_sliceRow;
        private static Vector2s s_sliceZone;
        private static Heightmap s_sliceHm;
        private static bool s_sliceDry;   // this column has no shore paint within a zone (baked flags)
        private static Texture2D s_sliceTex;
        private static readonly System.Diagnostics.Stopwatch s_sliceSw = new System.Diagnostics.Stopwatch();
        internal static long PaintSliceColumns, PaintSliceFrames;

        public static void DrainPaintDirtyTimed(float budgetMs)
        {
            if (!PaintCapable) { s_sliceActive = false; return; }
            if (s_gPending.Count > 0 && Time.unscaledTime >= s_gPendingNext)   // 0.2.1101: G re-mesh cooldowns that ran out
            {
                s_gPendingNext = Time.unscaledTime + 0.25f;
                s_gPendingScratch.Clear();
                foreach (var kv in s_gPending) if (Time.unscaledTime >= kv.Value) s_gPendingScratch.Add(kv.Key);
                foreach (var k in s_gPendingScratch) { s_gPending.Remove(k); s_paintDirty.Add(k); }
            }
            if (!s_sliceActive && s_paintDirty.Count == 0) return;
            s_sliceSw.Restart();
            PaintSliceFrames++;
            while (s_sliceSw.Elapsed.TotalMilliseconds < budgetMs)
            {
                if (!s_sliceActive)
                {
                    if (s_paintDirty.Count == 0) return;
                    Vector2Int next = default;
                    foreach (var k in s_paintDirty) { next = k; break; }
                    s_paintDirty.Remove(next);
                    if (!BeginPaintMask(next)) continue;
                }
                if (!s_columns.TryGetValue(s_sliceKey, out var st) || st.Count == 0) { s_sliceActive = false; continue; }   // dropped mid-slice
                ComposePaintRow(s_sliceKey, s_sliceRow);
                if (++s_sliceRow > 32) { FinishPaintMask(s_sliceKey); s_sliceActive = false; PaintSliceColumns++; VoxelZoneStreamer.NotePaintColumnLanded(s_sliceKey); }   // 0.2.1353: proof, Debug only
            }
        }

        private static bool BeginPaintMask(Vector2Int colKey)
        {
            if (!s_columns.TryGetValue(colKey, out var stack) || stack.Count == 0) return false;
            var bt = s_paintBeginTimer.T; int gc0 = StallPhaseTimer.Gc();   // 0.2.1322 proof only: texture create / pixel buffer / dry check
            bt[0] = StallPhaseTimer.Now();
            if (!s_paintMasks.TryGetValue(colKey, out var tex) || tex == null)
            {
                tex = new Texture2D(VoxelLayerPaint.MaskWidth, 33, TextureFormat.RGBA32, mipChain: false)
                {
                    name = $"VoxelPaint_{colKey.x}_{colKey.y}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                s_paintMasks[colKey] = tex;
            }
            bt[1] = StallPhaseTimer.Now();
            if (s_paintPixels == null) s_paintPixels = new Color[VoxelLayerPaint.MaskWidth * 33];
            s_sliceKey = colKey; s_sliceTex = tex; s_sliceRow = 0; s_sliceActive = true;
            s_sliceZone = new Vector2s(short.MinValue, short.MinValue); s_sliceHm = null;
            bt[2] = StallPhaseTimer.Now();
            s_sliceDry = Patch_Heightmap_WetMask.IsDryForPaint(colKey.x * 32f, colKey.y * 32f, 32f);
            bt[3] = StallPhaseTimer.Now();
            s_paintBeginTimer.Note(StallPhaseTimer.Gc() - gc0, $"column ({colKey.x},{colKey.y})");
            return true;
        }

        private static void RefreshPaintMask(Vector2Int colKey)
        {
            // the untimed path (unchanged result): every row at once. An in-flight slice shares the pixel buffer, so it is re-queued.
            if (s_sliceActive) { if (s_sliceKey != colKey) s_paintDirty.Add(s_sliceKey); s_sliceActive = false; }
            if (!BeginPaintMask(colKey)) return;
            for (int j = 0; j <= 32; j++) ComposePaintRow(colKey, j);
            FinishPaintMask(colKey);
            s_sliceActive = false;
        }

        private static void ComposePaintRow(Vector2Int colKey, int j)
        {
            float baseX = colKey.x * 32f, baseZ = colKey.y * 32f;
            var curZone = s_sliceZone;
            Heightmap curHm = s_sliceHm;
            if (curHm == null) curZone = new Vector2s(short.MinValue, short.MinValue);   // none yet, or destroyed since the last slice
            for (int i = 0; i <= 32; i++)
                s_paintPixels[j * VoxelLayerPaint.MaskWidth + i] = ComposeTexel(baseX + i, baseZ + j, ref curZone, ref curHm, s_sliceDry);
            s_sliceZone = curZone; s_sliceHm = curHm;
        }

        // One ground texel of a column's paint mask (world texel at x, z). 0.2.1157: shared by the column compose and the trail
        // texel poke (PokeTrailTexels), so a poked texel is exactly what a full compose would write; the walker's predicted trail
        // G (FatTrailFast overlay) is max-merged in before the wet band, as if the owner's G had already arrived.
        private static Color ComposeTexel(float wx, float wz, ref Vector2s curZone, ref Heightmap curHm, bool dry)
        {
            {
                {
                    // +ε probe = the GatherColumns border idiom: a sample on a
                    // zone boundary reads the zone that owns it.
                    var world = new Vector3(wx, 0f, wz);
                    var probe = new Vector3(world.x + 0.013f, 0f, world.z + 0.013f);
                    var zone = ZoneSystem.GetZone(probe);
                    if (zone.x != curZone.x || zone.y != curZone.y)
                    {
                        curZone = zone;
                        curHm = Heightmap.FindHeightmap(probe);
                    }
                    Color c;
                    if (curHm != null)
                    {
                        curHm.WorldToVertexMask(world, out int mx, out int my);
                        c = curHm.GetPaintMask(mx, my);
                        c.g = CutG(curHm, mx, my);   // 0.2.1312: proportional cut over the build-data base G (was 1310 edited G as-is)
                    }
                    else c = Heightmap.m_paintMaskNothing;
                    if (FatTrailFast.HasOverlay)
                    {
                        float og = FatTrailFast.OverlayG(Mathf.RoundToInt(wx), Mathf.RoundToInt(wz));
                        if (og > c.g) c.g = og;
                    }
                    // Ashlands must not be all lava with no land: alpha is lava on Ashlands ground. The
                    // heightmap's mask holds the WORLD's biome's alpha (1 everywhere but Ashlands), so ground turned to Ashlands
                    // takes the Ashlands lava rule there (VoxelLayerPaint.AshlandsLava), ground turned away from it takes
                    // vanilla's plain 1, both eased over the circle's edge; a column with no heightmap on real Ashlands gets the
                    // rule too (the "nothing" fallback's alpha 1 was lava).
                    bool effAsh;
                    if (Zones.BiomePaint.Any && Zones.BiomePaint.TryGround(world.x, world.z, out var turnedTo, out var turnedBy))
                    {
                        effAsh = turnedTo == Heightmap.Biome.AshLands;
                        float aTurned = effAsh ? VoxelLayerPaint.AshlandsLava(world.x, world.z) : 1f;
                        c.a = Mathf.Lerp(c.a, aTurned, VoxelLayerPaint.TurnedWeight(in turnedBy, world.x, world.z));
                    }
                    else
                    {
                        effAsh = FiresWorldGenBase.BiomeCodeAt(world.x, world.z) == 6;
                        if (curHm == null && effAsh) c.a = VoxelLayerPaint.AshlandsLava(world.x, world.z);
                    }
                    // WET-SHORE BAND (near-field). The [FiresWetMask] stamp
                    // blends PaintWet01 into the heightmap PAINT TEXTURE, but
                    // this voxel copy re-composes from the CPU GetPaintMask
                    // ARRAY — a different surface — so the band never reached
                    // the ground you stand on (full-LOD hid the blend the far
                    // quilt showed). Blend it here too, world-continuous (per
                    // vertex, NOT the dead per-column constant → no rectangles),
                    // same recipe as the quilt/stamp: r=max(r,wet); alpha→wet
                    // except Ashlands (alpha=lava); player paint (g/b) wins.
                    float wet = dry ? 0f : VoxelZoneStreamer.PaintWet01(world.x, world.z);   // dry column (no ZW_PAINT within a zone) = no band
                    if (wet > 0.03f && c.g <= 0.25f && c.b <= 0.25f)
                    {
                        c.r = Mathf.Max(c.r, wet);
                        if (!effAsh)   // the ground's own biome, turned or not
                            c.a = Mathf.Lerp(c.a, wet, wet);
                    }
                    return c;
                }
            }
        }

        // 0.2.1094 (Fire: "Yes, smooth trench"; shot 538: the Deep North walk trail in blocky 0.5-1 m squares): vanilla's deep-snow
        // walk trail is Character.m_deepSnowWalkObj = snow_walk.prefab, a TerrainOp with m_paintType 1 (Cultivate), m_paintRadius 1,
        // stamped every 0.25 m (Player m_deepSnowWalkObjDist). It lands in the paint mask's G channel at vanilla's 1 m texels, so a
        // 1 m disc rasterises as squares. In a snowy column (Mountain / Deep North at its centre, painted biomes included) G is
        // blurred twice with a 3x3 tent over the 33 x 33 ground texels; other channels stay exact. The voxel shader sinks the snow
        // raise along G (_FiresSnowTrench), so the blurred trail is a soft trench. Compose time only.
        // 0.2.1097 [SnowTrail] why-line: the composed (smoothed) trail G the shader reads at a world point, from the column's mask.
        // 0.2.1101: the composed paint mask's G at world (x, z), bilinear over the 1 m texels (texel (i, j) = world (col*32 + i, col*32 + j)),
        // the value the voxel shader reads at the same point. Main thread; the column's pixels are copied once per frame.
        // 0.2.1310 (Fire's log on 1309 + Core 1061: "paint G 0.591 … walkable 1.00", "depth raw 0.69 m, after path 0.00 m" where
        // nobody walked): Core 1061 writes its snow amount into the heightmap BASE G, and GetPaintMask = base + edits, so FAT read the
        // whole snow as a trodden trench (real Deep North voxel ground too: world-gen base G 0.3-0.6). The voxel trail G is now the
        // TerrainComp's player-edited paint only (m_modifiedPaint set = an op painted it; otherwise 0 = no trench), as 1158 behaved.
        private static readonly HarmonyLib.AccessTools.FieldRef<TerrainComp, Color[]> TcPaint = HarmonyLib.AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly HarmonyLib.AccessTools.FieldRef<TerrainComp, bool[]> TcModPaint = HarmonyLib.AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly HarmonyLib.AccessTools.FieldRef<TerrainComp, int> TcWidth = HarmonyLib.AccessTools.FieldRefAccess<TerrainComp, int>("m_width");
        private static readonly Dictionary<Heightmap, TerrainComp> s_hmComp = new Dictionary<Heightmap, TerrainComp>();
        private static int s_hmCompFrame = -1;
        internal static float EditedG(Heightmap hm, int mx, int my)
        {
            if (hm == null) return 0f;
            if (s_hmCompFrame != Time.frameCount && s_hmComp.Count > 256) { s_hmComp.Clear(); }
            s_hmCompFrame = Time.frameCount;
            if (!s_hmComp.TryGetValue(hm, out var tc) || tc == null)
            {
                tc = TerrainComp.FindTerrainCompiler(hm.transform.position);
                if (tc == null) return 0f;   // no comp = nothing edited here (not cached: a comp may appear later)
                s_hmComp[hm] = tc;
            }
            var paint = TcPaint(tc); var mod = TcModPaint(tc);
            if (paint == null || mod == null) return 0f;
            int idx = my * (TcWidth(tc) + 1) + mx;
            if ((uint)idx >= (uint)paint.Length || (uint)idx >= (uint)mod.Length || !mod[idx]) return 0f;
            float g = paint[idx].g;
            if (!s_editedGSaid)
            {
                s_editedGSaid = true;
                float bg = hm.GetPaintMask(mx, my).g;
                FiresLogger.LogDebug($"[SnowTrail] voxel trail G from TerrainComp paint only (base snow G ignored): first edited texel G {g:0.00} (heightmap base+edits {bg:0.00}) (0.2.1310)");
            }
            return g;
        }
        private static bool s_editedGSaid;

        // 0.2.1312 (Fire 10-31: trails flip filled <-> trodden; the 1310 cut was all-or-nothing on edited G): the cut is
        // proportional, cut = saturate((editedG - baseG) / (1 - baseG)), baseG = the heightmap's BUILD-DATA base mask G (world
        // snow, never base+edits). Core 1066 SnowRefill lowers edited G toward base, so the cut closes as it refills. The shader
        // still cuts with smoothstep(0.46,0.54,G): the trench shows while cut > 0.5 and closes at 0.5 (a gradual close = shader pass).
        private static readonly System.Reflection.FieldInfo HmBuildData = HarmonyLib.AccessTools.Field(typeof(Heightmap), "m_buildData");
        private static System.Reflection.FieldInfo s_baseMaskField;
        internal static float BaseG(Heightmap hm, int mx, int my)
        {
            if (hm == null || HmBuildData == null) return 0f;
            var bd = HmBuildData.GetValue(hm);
            if (bd == null) return 0f;
            if (s_baseMaskField == null) s_baseMaskField = HarmonyLib.AccessTools.Field(bd.GetType(), "m_baseMask");
            if (!(s_baseMaskField?.GetValue(bd) is Color[] bm)) return 0f;
            int idx = my * (hm.m_width + 1) + mx;
            return (uint)idx < (uint)bm.Length ? bm[idx].g : 0f;
        }
        internal static float CutG(Heightmap hm, int mx, int my) => CutG(hm, mx, my, out _, out _);
        internal static float CutG(Heightmap hm, int mx, int my, out float e, out float b)
        {
            e = EditedG(hm, mx, my); b = 0f;
            if (e <= 0f) return 0f;
            b = BaseG(hm, mx, my);
            return Mathf.Clamp01((e - b) / Mathf.Max(1e-3f, 1f - b));
        }

        private static readonly Dictionary<Vector2Int, Color32[]> s_gPixels = new Dictionary<Vector2Int, Color32[]>();
        private static int s_gPixelsFrame = -1;
        internal static float PaintGAt(float x, float z)
        {
            var col = new Vector2Int(Mathf.FloorToInt(x / 32f), Mathf.FloorToInt(z / 32f));
            return SampleG(PaintPixelsOf(col), col, x, z);
        }

        // 0.2.1197: the column's composed pixels, this frame's GetPixels32 copy (never written after), so the off-thread G refinement
        // can hold them as its snapshot. null = no readable mask. Main thread.
        internal static Color32[] PaintPixelsOf(Vector2Int col)
        {
            if (s_gPixelsFrame != Time.frameCount) { s_gPixelsFrame = Time.frameCount; s_gPixels.Clear(); }
            if (!s_gPixels.TryGetValue(col, out var px))
            {
                px = null;
                if (s_paintMasks.TryGetValue(col, out var tex) && tex != null && tex.isReadable) px = tex.GetPixels32();
                s_gPixels[col] = px;
            }
            return px;
        }

        // PaintGAt's bilinear read of one column's pixels; pure, any thread.
        internal static float SampleG(Color32[] px, Vector2Int col, float x, float z)
        {
            if (px == null) return 0f;
            int w = VoxelLayerPaint.MaskWidth;
            float fx = Mathf.Clamp(x - col.x * 32f, 0f, 32f), fz = Mathf.Clamp(z - col.y * 32f, 0f, 32f);
            int i0 = Mathf.Min((int)fx, 31), j0 = Mathf.Min((int)fz, 31);
            float tx = fx - i0, tz = fz - j0;
            float g00 = px[j0 * w + i0].g, g10 = px[j0 * w + i0 + 1].g, g01 = px[(j0 + 1) * w + i0].g, g11 = px[(j0 + 1) * w + i0 + 1].g;
            return Mathf.Lerp(Mathf.Lerp(g00, g10, tx), Mathf.Lerp(g01, g11, tx), tz) / 255f;
        }

        internal static bool TryTrailG(Vector3 p, out float g)
        {
            g = 0f;
            var col = new Vector2Int(Mathf.FloorToInt(p.x / 32f), Mathf.FloorToInt(p.z / 32f));
            if (!s_paintMasks.TryGetValue(col, out var tex) || tex == null || !tex.isReadable) return false;
            int i = Mathf.Clamp(Mathf.RoundToInt(p.x - col.x * 32f), 0, 32), j = Mathf.Clamp(Mathf.RoundToInt(p.z - col.y * 32f), 0, 32);
            g = tex.GetPixel(i, j).g;
            return true;
        }

        private static void FinishPaintMask(Vector2Int colKey)
        {
            if (!s_columns.TryGetValue(colKey, out var stack) || stack.Count == 0) return;
            var tex = s_sliceTex;
            if (tex == null) return;
            var ft = s_paintFinishTimer.T; int fgc0 = StallPhaseTimer.Gc();   // 0.2.1322 proof only: palette / SetPixels / Apply upload / G / re-bind / remesh queue
            ft[0] = StallPhaseTimer.Now();
            // 0.2.1111 (Fire: "no leveling around you ... a 1 m trench with nearly straight walls"): SmoothTrailChannel (1097: 3x3 max + tent,
            // a 1 m line grew to ~3 m) is gone. G is composed raw; FatSnowTrail writes it as a distance field whose 0.5 contour IS the
            // trench wall (1.0 m apart), and the shader cuts at that contour.
            VoxelLayerPaint.FillPalette(s_paintPixels);   // the cave and skyland paint texels beside the ground's
            ft[1] = StallPhaseTimer.Now();
            tex.SetPixels(s_paintPixels);
            ft[2] = StallPhaseTimer.Now();
            tex.Apply(false, false);
            ft[3] = StallPhaseTimer.Now();

            // NEAR-FIELD INLAND WATERLINE BAND: DEAD. The uniform is one
            // constant per 32m column and the shader wets EVERY vertex below
            // it — beside a pond on flat ground that painted the entire
            // column as wet sand, razor-cut at the column border where the
            // neighbour's probe found no water (64m sand
            // rectangles on Plains, sand-with-foam squares on snowy
            // mountainsides). A per-column constant can never blend; shore
            // sand is the WET MASK's job — world-continuous, real distance
            // falloff, identical values across zone borders, the vanilla
            // way. The far quilt keeps its band (blockiness invisible at
            // horizon distance).
            const float localWaterY = 0f;

            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                FiresLogger.LogInfo($"[FiresVoxel] paint mask refreshed for column ({colKey.x},{colKey.y}).");

            // 0.2.1101: the G channel (cultivated soil, walk trails, shovel / melt digs) decides which ground triangles the mesh refines
            // to 0.25 m (VoxelGTessellation), so the cultivated rows and the snow trench have vertices to move. When the set of G cells
            // changes the column's chunks re-mesh once; otherwise only the texture is re-bound.
            long sig = GSignature();
            bool gChanged = !s_gSig.TryGetValue(colKey, out long old) ? sig != 0 : old != sig;
            // at most one G re-mesh a second per column (a walked trail paints a new cell every metre): a change inside the cooldown
            // re-queues the column's paint compose, which looks again once the second is up
            // 0.2.1171 (Fire at base: ~33-41 chunk applies/s standing still, refined chunks ~90-115 ms each): G changes that are
            // not the local player's own trail (companions/creatures walking, cultivate ops, the refill decaying G) re-mesh a
            // column at most every 4 s; the column the player stands near keeps the 1 s cooldown (his trench stays responsive).
            float cool = GCooldown(colKey);
            ft[4] = StallPhaseTimer.Now();   // G signature + cooldown decision done; RemeshGNeighbours (if any) is the next phase
            if (gChanged && s_gRemeshAt.TryGetValue(colKey, out float lastAt) && Time.unscaledTime - lastAt < cool)
            {
                gChanged = false;
                s_gPending[colKey] = lastAt + cool;   // look again when the cooldown is up
                s_gHeld++;
            }
            else
            {
                s_gSig[colKey] = sig;
                if (gChanged) { s_gRemeshAt[colKey] = Time.unscaledTime; RemeshGNeighbours(colKey); }
            }
            ft[5] = StallPhaseTimer.Now();
            long rebindTicks = 0, remeshTicks = 0, lap;
            for (int i = 0; i < stack.Count; i++)
                if (stack[i] != null && s_views.TryGetValue(stack[i].Key, out var view) && view != null)
                {
                    lap = StallPhaseTimer.Now();
                    view.ApplyPaintMask(tex);
                    view.SetLocalWaterY(localWaterY);
                    rebindTicks += StallPhaseTimer.Now() - lap;
                    if (gChanged)
                    {
                        lap = StallPhaseTimer.Now();
                        VoxelMeshPipeline.EnqueueRemesh(stack[i]);
                        remeshTicks += StallPhaseTimer.Now() - lap;
                        if (Time.unscaledTime >= s_gRebindLogAt)
                        {
                            s_gRebindLogAt = Time.unscaledTime + 5f;
                            float trench = FiresVoxelConfig.SnowTrailTrench != null ? FiresVoxelConfig.SnowTrailTrench.Value : 0.85f;
                            FiresLogger.LogDebug($"[SnowTrail] chunk ({stack[i].Key.x},{stack[i].Key.y},{stack[i].Key.z}) paint re-bound after G change (trench {trench:0.00}); re-meshed for the 0.25 m G refinement");
                        }
                    }
                }
            ft[6] = ft[5] + rebindTicks; ft[7] = ft[6] + remeshTicks;   // 0.2.1322: the loop's re-bind and remesh-queue shares
            s_paintFinishTimer.Note(StallPhaseTimer.Gc() - fgc0, $"column ({colKey.x},{colKey.y}) {stack.Count} chunk(s){(gChanged ? " G changed" : "")}");
        }

        internal static int s_gHeld;
        private static float GCooldown(Vector2Int colKey)
        {
            var p = Player.m_localPlayer;
            if (p == null) return 4f;
            Vector3 pos = p.transform.position;
            float cx = (colKey.x + 0.5f) * 32f - pos.x, cz = (colKey.y + 0.5f) * 32f - pos.z;
            return cx * cx + cz * cz <= 40f * 40f ? 1f : 4f;
        }

        private static readonly Dictionary<Vector2Int, long> s_gSig = new Dictionary<Vector2Int, long>();
        private static readonly Dictionary<Vector2Int, float> s_gRemeshAt = new Dictionary<Vector2Int, float>();
        private static readonly Dictionary<Vector2Int, float> s_gPending = new Dictionary<Vector2Int, float>();
        private static readonly List<Vector2Int> s_gPendingScratch = new List<Vector2Int>();
        private static float s_gPendingNext;
        private static float s_gRebindLogAt;

        // Which ground texels carry G >= the refinement threshold (a hash of the set; 0 = none).
        // 0.2.1119 (round #16 mesh_seams, Fire: "the mesh must not break around the trench"): a chunk's mesh rim runs through the
        // vertices of its cells -1 and 31, so the edges along a column border are built by BOTH chunks, and the cells on the minus
        // side read the NEIGHBOUR column's G (the split decision and the trench snap). Only the column whose G set changed used to
        // re-mesh, so the chunk across the border kept the old split/snap of the same shared edge = a T-junction crack along the
        // column border. Now, when G near a border (3 texels) is or was there, the neighbour column across it re-meshes too.
        private static readonly Dictionary<Vector2Int, int> s_gBorder = new Dictionary<Vector2Int, int>();
        private static float s_gNeighbourLogAt;

        private static int GBorderMask()
        {
            int w = VoxelLayerPaint.MaskWidth, mask = 0;
            for (int j = 0; j < 33; j++)
                for (int i = 0; i < 33; i++)
                {
                    if (s_paintPixels[j * w + i].g == 0) continue;
                    int sx = i <= 2 ? -1 : i >= 30 ? 1 : 0, sz = j <= 2 ? -1 : j >= 30 ? 1 : 0;
                    if (sx == 0 && sz == 0) continue;
                    // bits: the 8 neighbour directions; a corner texel also counts for both sides
                    if (sx != 0) mask |= 1 << Dir(sx, 0);
                    if (sz != 0) mask |= 1 << Dir(0, sz);
                    if (sx != 0 && sz != 0) mask |= 1 << Dir(sx, sz);
                }
            return mask;
        }

        private static int Dir(int dx, int dz) => (dz + 1) * 3 + (dx + 1);

        private static void RemeshGNeighbours(Vector2Int colKey)
        {
            int now = GBorderMask();
            s_gBorder.TryGetValue(colKey, out int before);
            s_gBorder[colKey] = now;
            int mask = now | before;
            if (mask == 0) return;
            int chunks = 0, cols = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0 && dz == 0) || (mask & (1 << Dir(dx, dz))) == 0) continue;
                    if (!s_columns.TryGetValue(new Vector2Int(colKey.x + dx, colKey.y + dz), out var nst) || nst == null) continue;
                    cols++;
                    foreach (var ch in nst)
                        if (ch != null && ch.MeshAll) { VoxelMeshPipeline.EnqueueRemesh(ch); chunks++; }
                }
            if (chunks > 0 && Time.unscaledTime >= s_gNeighbourLogAt)
            {
                s_gNeighbourLogAt = Time.unscaledTime + 5f;
                FiresLogger.LogDebug($"[SnowTrail] G change at column ({colKey.x},{colKey.y}) reaches its border: {chunks} chunk(s) in {cols} neighbour column(s) re-meshed too, so the shared rim edges split and snap the same on both sides");
            }
        }

        private static long GSignature()
        {
            long h = 0;
            int w = VoxelLayerPaint.MaskWidth;
            for (int j = 0; j < 33; j++)
                for (int i = 0; i < 33; i++)
                    if (s_paintPixels[j * w + i].g >= VoxelGTessellation.GSplit)
                        unchecked { h = h * 1000003L + (j * 64 + i + 1); }
            return h;
        }

        // 0.2.1157 (Fire: trails "almost instant" on the walker's client; TRAIL_LATENCY_PLAN_20261027 4b): re-compose ONLY the world
        // texels x0..x1, z0..z1 in every column mask that holds them (a texel on a column border sits in both), upload them and
        // re-bind - a few dozen texels instead of 9 whole columns through the 2 ms/frame compose queue. The column's G signature and
        // border mask move with it, so a later full compose of the same column sees no G change (no second re-mesh). Returns true
        // when any texel's G changed; changedCols gets those columns. Main thread.
        internal static bool PokeTrailTexels(int x0, int z0, int x1, int z1, List<Vector2Int> changedCols)
        {
            if (!PaintCapable || x1 < x0 || z1 < z0) return false;
            bool any = false;
            int cx0 = FloorDiv(x0 - 1, 32), cx1 = FloorDiv(x1, 32), cz0 = FloorDiv(z0 - 1, 32), cz1 = FloorDiv(z1, 32);
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    var col = new Vector2Int(cx, cz);
                    if (!s_columns.TryGetValue(col, out var stack) || stack.Count == 0) continue;
                    if (!s_paintMasks.TryGetValue(col, out var tex) || tex == null || !tex.isReadable) { s_paintDirty.Add(col); continue; }   // first compose takes the overlay too
                    int i0 = Mathf.Max(0, x0 - cx * 32), i1 = Mathf.Min(32, x1 - cx * 32);
                    int j0 = Mathf.Max(0, z0 - cz * 32), j1 = Mathf.Min(32, z1 - cz * 32);
                    if (i1 < i0 || j1 < j0) continue;
                    int w = i1 - i0 + 1, h = j1 - j0 + 1;
                    var before = tex.GetPixels(i0, j0, w, h);
                    var block = new Color[w * h];
                    var zone = new Vector2s(short.MinValue, short.MinValue); Heightmap hm = null;
                    bool dry = Patch_Heightmap_WetMask.IsDryForPaint(cx * 32f, cz * 32f, 32f);
                    bool changed = false;
                    for (int j = 0; j < h; j++)
                        for (int i = 0; i < w; i++)
                        {
                            var c = ComposeTexel(cx * 32f + i0 + i, cz * 32f + j0 + j, ref zone, ref hm, dry);
                            block[j * w + i] = c;
                            if (Mathf.Abs(c.g - before[j * w + i].g) > 0.5f / 255f) changed = true;
                        }
                    tex.SetPixels(i0, j0, w, h, block);
                    tex.Apply(false, false);
                    // an in-flight slice of this column would overwrite the poke with rows composed before it: give it the same texels
                    if (s_sliceActive && s_sliceKey == col)
                        for (int j = 0; j < h; j++)
                            for (int i = 0; i < w; i++)
                                s_paintPixels[(j0 + j) * VoxelLayerPaint.MaskWidth + i0 + i] = block[j * w + i];
                    if (!changed) continue;
                    any = true;
                    changedCols?.Add(col);
                    s_gPixelsFrame = -1;   // PaintGAt re-reads this frame
                    var px = tex.GetPixels32();
                    s_gSig[col] = GSignature32(px);
                    s_gBorder[col] = GBorderMask32(px);
                    for (int k = 0; k < stack.Count; k++)
                        if (stack[k] != null && s_views.TryGetValue(stack[k].Key, out var view) && view != null) view.ApplyPaintMask(tex);
                }
            return any;
        }

        // The chunks a trail stamp over world texels x0..x1, z0..z1 at height y re-meshes: every MeshAll chunk whose column lies within
        // 4 m of the stamp (1 m bilinear reach + the 1119 rule: a chunk's rim reads 3 texels of the neighbour column's G) and whose
        // Y range holds y +- 3 m. A stamp touching a column border therefore names the chunks on both sides - one atomic group.
        internal static void CollectTrailChunks(int x0, int z0, int x1, int z1, float y, List<VoxelChunk> into)
        {
            // 0.2.1160 (PERF): the neighbour column only when the stamp is within the 3 texels its rim reads (was 4 m), and the stack
            // chunk(s) whose Y range holds y +- 1 m (was 3 m) - groups were up to 12 chunks at ~5.7 ms an apply.
            int cx0 = FloorDiv(x0 - 3, 32), cx1 = FloorDiv(x1 + 3, 32), cz0 = FloorDiv(z0 - 3, 32), cz1 = FloorDiv(z1 + 3, 32);
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (!s_columns.TryGetValue(new Vector2Int(cx, cz), out var stack) || stack == null) continue;
                    foreach (var ch in stack)
                    {
                        if (ch == null || !ch.MeshAll) continue;
                        float oy = ch.WorldOrigin.y;
                        if (y < oy - 1f || y > oy + VoxelChunk.CELLS + 1f) continue;
                        into.Add(ch);
                    }
                }
        }

        private static long GSignature32(Color32[] px)
        {
            long h = 0;
            int w = VoxelLayerPaint.MaskWidth;
            byte split = (byte)Mathf.CeilToInt(VoxelGTessellation.GSplit * 255f);
            for (int j = 0; j < 33; j++)
                for (int i = 0; i < 33; i++)
                    if (px[j * w + i].g >= split)
                        unchecked { h = h * 1000003L + (j * 64 + i + 1); }
            return h;
        }

        private static int GBorderMask32(Color32[] px)
        {
            int w = VoxelLayerPaint.MaskWidth, mask = 0;
            for (int j = 0; j < 33; j++)
                for (int i = 0; i < 33; i++)
                {
                    if (px[j * w + i].g == 0) continue;
                    int sx = i <= 2 ? -1 : i >= 30 ? 1 : 0, sz = j <= 2 ? -1 : j >= 30 ? 1 : 0;
                    if (sx == 0 && sz == 0) continue;
                    if (sx != 0) mask |= 1 << Dir(sx, 0);
                    if (sz != 0) mask |= 1 << Dir(0, sz);
                    if (sx != 0 && sz != 0) mask |= 1 << Dir(sx, sz);
                }
            return mask;
        }

        private static void DropPaintMask(Vector2Int colKey)
        {
            s_paintDirty.Remove(colKey);
            if (s_paintMasks.TryGetValue(colKey, out var tex))
            {
                s_paintMasks.Remove(colKey);
                if (tex != null) Object.Destroy(tex);
            }
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);
    }
}
