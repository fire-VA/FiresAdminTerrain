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

        // 0.2.715 (Vfall): save a loaded chunk's anchor on the usual debounce, for a change that is not a density edit
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
            VerdantsAscent.Water.Tools.ToolFalls.WriteTo(chunk.Key, zdo);   // 0.2.715: the chunk's Vfall falls ride its anchor
            if (anchor != null) anchor.MarkSelfWrite();
            // Evidence (Fire 10-05, at Debug): the edit is now in the shared chunk record that the server and the other
            // peers read. A brush whose dab shows locally but never reaches this line never left this client.
            FiresLogger.LogDebug($"[FiresVoxel] shared chunk {chunk.Key}: {packed.Length} bytes packed, rev {zdo.DataRevision}, owner {(zdo.GetOwner() == ZDOMan.GetSessionID() ? "me" : zdo.GetOwner().ToString())}");
        }

        // MOP on GENERATED water — the mop tool works on carve+fill water
        // too: columns are marked dry (persisted as a sparse tail on the
        // anchor blob), then the touched chunks save and remesh exactly like
        // a brush stroke. Returns columns mopped.
        // 0.2.715: the layers around a mop, so a sky island's water (y 600) can be mopped, and a mop up there leaves the
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

        // Some loaded chunk holds a mop mask (set by a mop or an imported anchor): until then drawn water skips the lookup.
        internal static volatile bool AnyMops;

        // 0.2.715 Vmop: mask (or, restore = true, unmask) every column in the disc, in the layers around the mop. The mask
        // persists on the chunk anchors (the mop tail of the edit blob), so a regen, reload, rejoin or peer keeps it.
        // Returns columns changed; wetGen = of those, how many held generated water.
        public static int MopWater(Vector3 center, float radius, bool restore, out int wetGen)
        {
            FatUndo.BeforeVoxelEdit(restore ? "a Vmop restore stroke" : "a Vmop stroke", center, radius, radius);   // one undo step per stroke (0.2.716)
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
            if (packed != null) chunk.ImportBrushEdits(Utils.Decompress(packed));
            return chunk;
        }

        // ── Undo snapshots (FAT 0.2.716, FatUndo) ──────────────────────────────────────────────────────────────────────
        /// <summary>
        /// A chunk's whole brush-edit blob (edit bounds + water-mop tail, as its anchor stores it), with no side effects: a loaded chunk's
        /// live edits, else its anchor's, else the no-edits blob.
        /// </summary>
        internal static byte[] SnapshotEdits(Vector3Int key)
        {
            if (s_chunks.TryGetValue(key, out var chunk) && chunk != null && chunk.Kind == ChunkKind.Full) return chunk.ExportBrushEdits();
            var packed = ReadAnchorEdits(key);
            return packed != null ? Utils.Decompress(packed) : NoEditsBlob();
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
                            raw = packed != null ? Utils.Decompress(packed) : null;
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
        // buried. Nothing is blended into the zones around it - a step at the border is the point
        // (Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md).
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

        public static string ApplyBrush(Vector3 center, float radius, float height, VoxelBrushShape shape, VoxelBrushMode mode)
        {
            // 0.2.771 (Fire 10-06: "we should be able to draw our own little floating terrain that can just be merged with the
            // chunk if we restore it"): a V-tool stroke (VToolScope) may reach over a closed zone. Its edits land in the chunk
            // anchors as anywhere else, stand as the player's own terrain over the closed sea (synced, undoable), stay where they
            // are when a zone closes, and re-apply onto the land when the zone is opened or restored (the edit bounds compose with
            // whatever ground a chunk generates, so nothing is doubled or lost). Hoe / IH / WEC terrain ops stay refused there.
            if (VToolScope <= 0 && Zones.ZoneModeRefusals.TerrainEditTouchesClosed(center, radius))
                return "Refused: the brush reaches into a closed zone of this one-zone-at-a-time world.";
            // One undo step per stroke (FatUndo keeps the touched chunks' edit blobs before the first dab; 0.2.716).
            FatUndo.BeforeVoxelEdit($"a V-tool {mode} stroke", center, radius, height);

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
                                if (chunk.MeshAll) VoxelMeshPipeline.EnqueueRemesh(chunk);
                                else DropChunk(key);
                                VoxelZoneStreamer.RefreshBrushedZone(key);
                                continue;
                            }
                            MarkSaveDirty(chunk);
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
                FiresLogger.LogWarning($"[FiresVoxel] {verb} changed nothing at ({center.x:F0},{center.y:F0},{center.z:F0}) r={radius:F1} — {probe}");
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

        // 0.2.793 (fat_surface_probe): every density-0 crossing in the column between yLo and yHi, sampled as
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

        // 0.2.751 (Vskin): is the voxel at a world point solid (density > 0)? false when the column is not loaded.
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

        // 0.2.813 (SinkTestGen2: a client read solid at y 64 inside a sinkhole shaft the server had carved): the chunk that OWNS
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
                if (view != null) Object.Destroy(view.gameObject);
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

        public static VoxelChunkView GetOrCreateView(VoxelChunk chunk)
        {
            if (s_views.TryGetValue(chunk.Key, out var view) && view != null) return view;
            var go = new GameObject($"VoxelChunk_{chunk.Key.x}_{chunk.Key.y}_{chunk.Key.z}");
            go.transform.SetParent(VoxelMeshPipeline.Root, worldPositionStays: true);
            view = go.AddComponent<VoxelChunkView>();
            view.Key = chunk.Key;
            view.Init();
            view.RenderSuppressed = chunk.SuppressRenderUntilSwap;
            s_views[chunk.Key] = view;
            BindOrQueuePaintMask(chunk.Key, view);
            return view;
        }

        // World unload (driver OnDestroy) — views die with the scene; drop refs.
        public static void Clear()
        {
            s_chunks.Clear();
            s_views.Clear();
            s_columns.Clear();
            s_fullChunkCount = 0;
            foreach (var kv in s_paintMasks)
                if (kv.Value != null) Object.Destroy(kv.Value);
            s_paintMasks.Clear();
            s_paintDirty.Clear();
        }

        // ------------------------------------------------------------------
        // W3.3 — terrain paint on the voxel skin. Paint DATA already
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
                view.ApplyPaintMask(VoxelLayerPaint.BlankMask);   // 0.2.763: no lava flash on a skyland before its column's mask
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

        private static void RefreshPaintMask(Vector2Int colKey)
        {
            if (!s_columns.TryGetValue(colKey, out var stack) || stack.Count == 0) return;

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

            if (s_paintPixels == null) s_paintPixels = new Color[VoxelLayerPaint.MaskWidth * 33];
            float baseX = colKey.x * 32f, baseZ = colKey.y * 32f;
            // FindHeightmap scans every live heightmap — 1089 calls per column
            // was a real hitch during regen bursts. A texel row crosses a zone
            // boundary at most once: cache the heightmap per zone and resolve
            // only when the zone id changes (≤4 lookups per column).
            var curZone = new Vector2s(short.MinValue, short.MinValue);
            Heightmap curHm = null;
            for (int j = 0; j <= 32; j++)
                for (int i = 0; i <= 32; i++)
                {
                    // +ε probe = the GatherColumns border idiom: a sample on a
                    // zone boundary reads the zone that owns it.
                    var world = new Vector3(baseX + i, 0f, baseZ + j);
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
                    }
                    else c = Heightmap.m_paintMaskNothing;
                    // 0.2.770 (Fire 10-06: "ashlands is just all lava, never any land"): alpha is lava on Ashlands ground. The
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
                    float wet = VoxelZoneStreamer.PaintWet01(world.x, world.z);
                    if (wet > 0.03f && c.g <= 0.25f && c.b <= 0.25f)
                    {
                        c.r = Mathf.Max(c.r, wet);
                        if (!effAsh)   // 0.2.770: the ground's own biome, turned or not
                            c.a = Mathf.Lerp(c.a, wet, wet);
                    }
                    s_paintPixels[j * VoxelLayerPaint.MaskWidth + i] = c;
                }
            VoxelLayerPaint.FillPalette(s_paintPixels);   // 0.2.758: the cave and skyland paint texels beside the ground's
            tex.SetPixels(s_paintPixels);
            tex.Apply(false, false);

            // NEAR-FIELD INLAND WATERLINE BAND: DEAD. The uniform is one
            // constant per 32m column and the shader wets EVERY vertex below
            // it — beside a pond on flat ground that painted the entire
            // column as wet sand, razor-cut at the column border where the
            // neighbour's probe found no water (user screenshots: 64m sand
            // rectangles on Plains, sand-with-foam squares on snowy
            // mountainsides). A per-column constant can never blend; shore
            // sand is the WET MASK's job — world-continuous, real distance
            // falloff, identical values across zone borders, the vanilla
            // way. The far quilt keeps its band (blockiness invisible at
            // horizon distance).
            const float localWaterY = 0f;

            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                FiresLogger.LogInfo($"[FiresVoxel] paint mask refreshed for column ({colKey.x},{colKey.y}).");

            for (int i = 0; i < stack.Count; i++)
                if (stack[i] != null && s_views.TryGetValue(stack[i].Key, out var view) && view != null)
                {
                    view.ApplyPaintMask(tex);
                    view.SetLocalWaterY(localWaterY);
                }
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
