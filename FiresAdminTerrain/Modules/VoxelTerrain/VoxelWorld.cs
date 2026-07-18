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
            if (s_chunks.TryGetValue(key, out var existing)) return existing;
            var chunk = new VoxelChunk(key);
            s_chunks[key] = chunk;
            var colKey = new Vector2Int(key.x, key.z);
            if (!s_columns.TryGetValue(colKey, out var stack))
                s_columns[colKey] = stack = new List<VoxelChunk>(2);
            stack.Add(chunk);
            return chunk;
        }

        // W1 — the zone streamer drops a wrapped zone's chunks on unwrap.
        public static void DropChunkKey(Vector3Int key) => DropChunk(key);

        // P1 — anchor restore path: ensure a chunk exists (seeded from live
        // ground so the edited-gate + Vdelete semantics track current terrain),
        // ready for ImportDensity.
        public static VoxelChunk GetOrCreateForRestore(Vector3Int key)
        {
            if (s_chunks.TryGetValue(key, out var existing))
            {
                // Streamed chunk still awaiting worker generation: ImportDensity
                // would set PendingGenerate=false and null the gather payload —
                // _seed (and interior biome) would stay ALL-ZERO forever, so
                // restore-to-seed carves voids and neighbours mirror zero seed
                // into their rings. Run generation NOW (whoever-gets-there-first
                // contract, same as the brush path) before the import lands.
                if (existing.PendingGenerate)
                {
                    existing.SetBorderOverrides(VoxelChunk.CaptureBorderOverrides(key));
                    existing.RunPendingGenerate();
                }
                return existing;
            }
            if (s_chunks.Count >= FiresVoxelConfig.MaxLoadedChunks.Value) return null;
            var chunk = new VoxelChunk(key);
            chunk.SeedFromGround();
            s_chunks[key] = chunk;
            var colKey = new Vector2Int(key.x, key.z);
            if (!s_columns.TryGetValue(colKey, out var stack))
                s_columns[colKey] = stack = new List<VoxelChunk>(2);
            stack.Add(chunk);
            return chunk;
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
                catch (System.Exception ex) { Debug.LogWarning($"[FiresVoxel] save {chunk.Key} failed: {ex.Message}"); }
                break;   // ≤1 save per frame — Compress is ~1ms on 36KB
            }
        }

        private static void SaveChunk(VoxelChunk chunk)
        {
            VoxelChunkAnchor anchor;
            if (!VoxelChunkAnchor.TryGet(chunk.Key, out anchor))
            {
                var center = chunk.WorldOrigin + new Vector3(16f, 16f, 16f);
                var go = VoxelAnchorPrefab.Spawn(center);
                if (go == null) return;
                anchor = go.GetComponent<VoxelChunkAnchor>();
                if (anchor == null) return;
                anchor.AdoptLocal(chunk.Key);
            }
            var nv = anchor.Nview;
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();

            var zdo = nv.GetZDO();
            zdo.Set(VoxelAnchorPrefab.ZDO_KX, chunk.Key.x);
            zdo.Set(VoxelAnchorPrefab.ZDO_KY, chunk.Key.y);
            zdo.Set(VoxelAnchorPrefab.ZDO_KZ, chunk.Key.z);
            zdo.Set(VoxelAnchorPrefab.ZDO_VER, 1);
            var packed = Utils.Compress(chunk.ExportDensity());
            zdo.Set(VoxelAnchorPrefab.ZDO_DENSITY, packed);
            anchor.MarkSelfWrite();
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                Debug.Log($"[FiresVoxel] saved chunk {chunk.Key} → anchor ({packed.Length} bytes packed).");
        }

        // Shape brush across every overlapping chunk. Carve/Fill seed missing
        // chunks on demand; Restore only ever touches EXISTING chunks (deleting
        // where nothing exists is a no-op) and drops chunks that return fully
        // to their seeded ground state. Returns a console-ready summary line.
        public static string ApplyBrush(Vector3 center, float radius, float height, VoxelBrushShape shape, VoxelBrushMode mode)
        {
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
                            if (s_chunks.Count >= FiresVoxelConfig.MaxLoadedChunks.Value) { capped++; continue; }
                            chunk = new VoxelChunk(key);
                            sw.Restart();
                            chunk.SeedFromGround();
                            sw.Stop();
                            seedMs += (float)sw.Elapsed.TotalMilliseconds;
                            s_chunks[key] = chunk;
                            var colKey = new Vector2Int(key.x, key.z);
                            if (!s_columns.TryGetValue(colKey, out var stack))
                                s_columns[colKey] = stack = new List<VoxelChunk>(2);
                            stack.Add(chunk);
                            seeded++;
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
                                // No edits left — remove chunk AND its anchor.
                                if (VoxelChunkAnchor.TryGet(key, out var anchor) && anchor != null
                                    && ZNetScene.instance != null)
                                {
                                    var nv = anchor.Nview;
                                    if (nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();
                                    ZNetScene.instance.Destroy(anchor.gameObject);
                                }
                                s_saveDirty.Remove(chunk);
                                DropChunk(key);
                                continue;
                            }
                            MarkSaveDirty(chunk);
                            VoxelMeshPipeline.EnqueueRemesh(chunk);
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
                    Debug.LogWarning($"[FiresVoxel] clutter refresh threw: {ex.Message}");
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
                Debug.LogWarning($"[FiresVoxel] {verb} changed nothing at ({center.x:F0},{center.y:F0},{center.z:F0}) r={radius:F1} — {probe}");
            }
            string summary = $"{verb} {shapeTxt} at ({center.x:F0},{center.y:F0},{center.z:F0}) → {touched} chunk(s), {seeded} seeded";
            if (seeded > 0) summary += $" ({seedMs:F0}ms seed)";
            if (capped > 0)
            {
                summary += $" | {capped} REFUSED (MaxLoadedChunks={FiresVoxelConfig.MaxLoadedChunks.Value})";
                // A refused chunk under a carve = a HOLE with no floor — the
                // fall-out-of-the-world report. Never let this pass quietly.
                Debug.LogError($"[FiresVoxel] brush hit the chunk cap: {capped} chunk(s) SKIPPED at ({center.x:F0},{center.y:F0},{center.z:F0}) — carved areas may have NO FLOOR. Raise 'Max loaded chunks' (now {FiresVoxelConfig.MaxLoadedChunks.Value}, live {s_chunks.Count}).");
            }
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                Debug.Log($"[FiresVoxel] {summary} | live chunks={s_chunks.Count}");
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

        private static sbyte SampleColumnDensity(List<VoxelChunk> stack, float wx, int y, float wz, out bool ok)
        {
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
                s_paintDirty.Add(colKey);
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
                tex = new Texture2D(33, 33, TextureFormat.RGBA32, mipChain: false)
                {
                    name = $"VoxelPaint_{colKey.x}_{colKey.y}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                s_paintMasks[colKey] = tex;
            }

            if (s_paintPixels == null) s_paintPixels = new Color[33 * 33];
            float baseX = colKey.x * 32f, baseZ = colKey.y * 32f;
            // FindHeightmap scans every live heightmap — 1089 calls per column
            // was a real hitch during regen bursts. A texel row crosses a zone
            // boundary at most once: cache the heightmap per zone and resolve
            // only when the zone id changes (≤4 lookups per column).
            var curZone = new Vector2i(int.MinValue, int.MinValue);
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
                    s_paintPixels[j * 33 + i] = c;
                }
            tex.SetPixels(s_paintPixels);
            tex.Apply(false, false);

            // Per-column gen waterline: the highest inland river/lake surface
            // over this 32m footprint (0 = dry). A coarse 5×5 max is plenty —
            // the field is smooth and the shader only wets vertices BELOW the
            // line, so the max just sets how high the wet-shore band can reach.
            float localWaterY = 0f;
            for (int j = 0; j <= 32; j += 8)
                for (int i = 0; i <= 32; i += 8)
                {
                    float wx = baseX + i, wz = baseZ + j;
                    if (FiresWorldGenBase.TryGetGenWaterSmooth(wx, wz, out float lvl, out float depth)
                        && depth >= 0.25f && lvl > 30.25f && lvl > localWaterY)
                        localWaterY = lvl;
                }

            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                Debug.Log($"[FiresVoxel] paint mask refreshed for column ({colKey.x},{colKey.y}).");

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
