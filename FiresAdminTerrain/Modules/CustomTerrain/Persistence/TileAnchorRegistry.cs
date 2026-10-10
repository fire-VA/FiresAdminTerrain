using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Static lookup table: Vector2i tile index -> TileAnchor.
    //
    // Replaces DirtFloorZoneRegistry's connectivity-based grouping with
    // deterministic tile-index addressing. Same world position always maps
    // to the same tile, so there's no proximity matching, no "destroy
    // duplicate" logic — the dictionary key is the identity.
    //
    // Anchors register on Awake (after ZNetScene streams them in) and
    // unregister on OnDestroy. Lookups are O(1). World-rect overlap queries
    // (for cross-tile ops like a paint disk that spans 4 tiles) iterate
    // the candidate index range — bounded by op size, typically 1-4 entries.
    public static class TileAnchorRegistry
    {
        private static readonly Dictionary<Vector2i, TileAnchor> _byIndex
            = new Dictionary<Vector2i, TileAnchor>();

        // Every live anchor per tile, duplicates included (OPEN_BUGS #47): the heal needs all of them, and when the
        // registered one unloads another takes its place instead of the server spawning a third.
        private static readonly Dictionary<Vector2i, List<TileAnchor>> _allByIndex
            = new Dictionary<Vector2i, List<TileAnchor>>();

        internal static List<TileAnchor> AllAt(Vector2i tileXZ)
        {
            var live = new List<TileAnchor>();
            if (_allByIndex.TryGetValue(tileXZ, out var list))
                foreach (var a in list) if (a != null) live.Add(a);
            return live;
        }

        public static int Count => _byIndex.Count;

        public static IEnumerable<TileAnchor> All => _byIndex.Values;

        public static TileAnchor GetAt(Vector2i tileXZ)
        {
            _byIndex.TryGetValue(tileXZ, out var anchor);
            return anchor;
        }

        public static TileAnchor GetAtWorld(Vector3 worldPos)
        {
            return GetAt(WorldToTileIndex(worldPos));
        }

        // Phase 8a stub. Phase 8c will instantiate a TileAnchor prefab here
        // for ops that need to write to a tile not currently loaded (e.g. a
        // disk-place op that overlaps an unloaded neighbor tile). For 8a,
        // returns null — callers must handle missing anchors gracefully.
        public static TileAnchor GetOrCreate(Vector2i tileXZ)
        {
            if (_byIndex.TryGetValue(tileXZ, out var existing))
                return existing;
            // 8c TODO: spawn the persistent ZNetView prefab at WorldCenterForTile(tileXZ)
            // with ClaimOwnership, register, return. For now we don't auto-spawn.
            return null;
        }

        // Enumerate every tile index whose 64x64m world rect intersects the
        // given world-space XZ rect. Caller passes rect.x/y as world XZ.
        public static IEnumerable<Vector2i> TilesOverlapping(Rect worldXZRect)
        {
            int minX = Mathf.FloorToInt(worldXZRect.xMin / TerrainTile.TILE_SIZE);
            int maxX = Mathf.FloorToInt(worldXZRect.xMax / TerrainTile.TILE_SIZE);
            int minZ = Mathf.FloorToInt(worldXZRect.yMin / TerrainTile.TILE_SIZE);
            int maxZ = Mathf.FloorToInt(worldXZRect.yMax / TerrainTile.TILE_SIZE);

            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                yield return new Vector2i(x, z);
            }
        }

        // Convenience: tile rect derived from a center + radius circular op.
        public static IEnumerable<Vector2i> TilesOverlappingCircle(Vector3 center, float radius)
        {
            var rect = new Rect(
                center.x - radius,
                center.z - radius,
                radius * 2f,
                radius * 2f);
            return TilesOverlapping(rect);
        }

        // World XZ -> tile index. Negative coordinates floor correctly
        // (Mathf.FloorToInt(-0.1f / 64f) = -1, which is what we want).
        public static Vector2i WorldToTileIndex(Vector3 worldPos)
        {
            return new Vector2i(
                Mathf.FloorToInt(worldPos.x / TerrainTile.TILE_SIZE),
                Mathf.FloorToInt(worldPos.z / TerrainTile.TILE_SIZE));
        }

        // World XZ center of a tile. Y is the anchor's fixed Y (set by TileAnchor).
        public static Vector3 WorldCenterForTile(Vector2i tileXZ)
        {
            return new Vector3(
                tileXZ.x * TerrainTile.TILE_SIZE + TerrainTile.TILE_SIZE * 0.5f,
                0f,
                tileXZ.y * TerrainTile.TILE_SIZE + TerrainTile.TILE_SIZE * 0.5f);
        }

        // World-position → tile height lookup. Returns true + height if the
        // tile at this position is loaded AND has a modified vertex at the
        // nearest grid cell. Used by IH ruler-snap to lift the ruler ring
        // onto our floating platforms.
        public static bool TrySampleHeightAtWorld(Vector3 worldPos, out float height)
        {
            height = 0f;
            var tileXZ = WorldToTileIndex(worldPos);
            var anchor = GetAt(tileXZ);
            if (anchor == null || anchor.Tile == null) return false;
            return anchor.Tile.TrySampleHeightAtWorld(worldPos, out height);
        }

        public static bool TrySamplePaintAtWorld(Vector3 worldPos, out Color32 color)
        {
            color = default;
            var anchor = GetAt(WorldToTileIndex(worldPos));
            return anchor != null && anchor.Tile != null && anchor.Tile.TrySamplePaintAtWorld(worldPos, out color);
        }

        // The tile's painted biome at a world position (0 = none painted there), per vertex with the tile-wide override
        // as the fallback, as TileBiomeOverride reads it.
        public static ushort SampleBiomeAtWorld(Vector3 worldPos)
        {
            var anchor = GetAt(WorldToTileIndex(worldPos));
            if (anchor == null || anchor.Tile == null) return 0;
            ushort raw = anchor.Tile.SampleBiomeAtWorld(worldPos);
            return raw != 0 ? raw : anchor.Tile.BiomeOverride;
        }

        internal static void Register(TileAnchor anchor)
        {
            if (anchor == null) return;
            var key = anchor.TileXZ;
            if (_byIndex.TryGetValue(key, out var existing) && existing != anchor && existing != null)
            {
                // Two anchors at the same tile index — should not happen with
                // deterministic identity. Log and keep the newer one (existing
                // will get destroyed by ZNetScene on the next sync since one
                // of them has the wrong ZDO).
                FiresLogger.LogWarning($"[TileAnchorRegistry] Duplicate anchor at tile ({key.x},{key.y}) — replacing previous registration.");
            }
            _byIndex[key] = anchor;
            if (!_allByIndex.TryGetValue(key, out var all)) _allByIndex[key] = all = new List<TileAnchor>();
            all.RemoveAll(a => a == null);
            if (!all.Contains(anchor)) all.Add(anchor);
            if (all.Count > 1) TileAnchorHeal.Note(key);
            FiresLogger.LogVerbose($"[TileAnchorRegistry] Registered anchor at ({key.x},{key.y}) | total={_byIndex.Count}");
        }

        internal static void Unregister(TileAnchor anchor)
        {
            if (anchor == null) return;
            var key = anchor.TileXZ;
            TileAnchor successor = null;
            if (_allByIndex.TryGetValue(key, out var all))
            {
                all.RemoveAll(a => a == null || a == anchor);
                if (all.Count == 0) _allByIndex.Remove(key);
                else successor = all[all.Count - 1];
            }
            if (_byIndex.TryGetValue(key, out var existing) && existing == anchor)
            {
                if (successor != null) _byIndex[key] = successor;
                else _byIndex.Remove(key);
                FiresLogger.LogVerbose($"[TileAnchorRegistry] Unregistered anchor at ({key.x},{key.y}) | remaining={_byIndex.Count}");
            }
        }

        // For tests / debug commands. Wipes the table — does NOT destroy
        // anchor GameObjects (caller's responsibility if desired).
        public static void Clear() { _byIndex.Clear(); _allByIndex.Clear(); }

        // Drop entries pointing at fake-null Unity objects (TileAnchor MBs
        // that were destroyed during a session teardown but whose OnDestroy
        // didn't fire cleanly, leaving stale dict entries). Called by
        // TileAnchorPrefab.Ensure on session-change so a logout-and-back-in
        // doesn't carry stale refs into the new session.
        //
        // Returns the count removed.
        public static int ScrubFakeNulls()
        {
            List<Vector2i> deadKeys = null;
            foreach (var kvp in _byIndex)
            {
                // Unity's overloaded == catches fake-null (destroyed but
                // C# reference still set). Real-null is impossible here
                // (Register guards against it).
                if (kvp.Value == null)
                    (deadKeys ??= new List<Vector2i>()).Add(kvp.Key);
            }

            if (deadKeys == null) return 0;

            foreach (var k in deadKeys)
                _byIndex.Remove(k);

            FiresLogger.LogInfo($"[TileAnchorRegistry] ScrubFakeNulls: removed {deadKeys.Count} stale entries | remaining={_byIndex.Count}");
            return deadKeys.Count;
        }
    }

    // The duplicate-anchor heal (OPEN_BUGS #47). Several anchors for one tile each hold part of its edits (a multiplayer race
    // before FAT 0.2.644) and a reload shows only one of them. Per tile, N-way: the anchor with the lowest ZDOID is kept, so
    // every peer picks the same one, and each vertex takes the MAJORITY value among the anchors that edited it, ties going to
    // the keeper: the duplicates are the same ops applied on different peers, one of which missed some (R71: two anchors held
    // the same 38 vertices, the third 19 with 4 older values). Every peer logs the plan; only the server applies it, and only
    // with 'Heal duplicate tile anchors' = Apply: a snapshot of the world's save first (refusing without one), the merged
    // payload written to the keeper and read back, and only then the losers destroyed, at most MaxMergesPerSession tiles a
    // session. A healed tile has one anchor left, so it plans nothing again.
    internal enum TileHealMode { DryRun, Apply, Purge }

    internal static class TileAnchorHeal
    {
        private const float PayloadWaitSeconds = 30f;
        private const float HeightTolerance = 0.01f;
        private const int MaxMergesPerSession = 50;
        private const string SnapshotSuffix = ".tileheal-";

        private static BepInEx.Configuration.ConfigEntry<TileHealMode> s_mode;
        private static readonly Dictionary<Vector2i, float> s_pending = new Dictionary<Vector2i, float>();
        private static readonly List<Vector2i> s_scratch = new List<Vector2i>();
        private static int s_frame = -1;
        private static ZNet s_session;
        private static int s_merges;
        private static bool s_capLogged;
        private static string s_snapshot;
        private static bool s_snapshotFailed;

        internal static void Bind(BepInEx.Configuration.ConfigFile cfg)
        {
            s_mode = cfg.Bind("08 - Tile Anchors", "01 Heal duplicate tile anchors", TileHealMode.DryRun,
                "Before FAT 0.2.644 a multiplayer race could give one FAT tile several anchors, each holding part of its edits; a "
                + "reload shows only one. DryRun (default) logs each such tile's merge plan ('[TileHeal] DRY RUN plan') and changes "
                + "nothing. Apply (acts on the server only): snapshots the world's save into FiresWorldGen, merges each tile into its "
                + "lowest-ZDOID anchor (per vertex the majority value, ties to that anchor) and removes the others, at most "
                + MaxMergesPerSession + " tiles a session. Purge (server only, any world type; FAT 0.2.651 retired the 2D tile layer): "
                + "once at world load, snapshots the save and every anchor's payload into FiresWorldGen, then removes EVERY tile "
                + "anchor of the world ('[TileHeal] PURGED tile …' per tile and a total). The admin console command "
                + PurgeCommand + " does the same on demand.");
        }

        private static bool Applying => s_mode != null && s_mode.Value == TileHealMode.Apply
                                        && ZNet.instance != null && ZNet.instance.IsServer();

        internal static void Note(Vector2i tile)
        {
            if (!s_pending.ContainsKey(tile)) s_pending[tile] = Time.realtimeSinceStartup;
        }

        // Once a frame, from any TileAnchor's Update; one tile per frame.
        internal static void Tick()
        {
            if (s_session != ZNet.instance)
            {
                s_session = ZNet.instance;
                s_merges = 0;
                s_capLogged = false;
                s_snapshot = null;
                s_snapshotFailed = false;
            }
            if (s_pending.Count == 0 || s_frame == Time.frameCount) return;
            s_frame = Time.frameCount;
            s_scratch.Clear();
            s_scratch.AddRange(s_pending.Keys);
            foreach (var tile in s_scratch)
            {
                var anchors = TileAnchorRegistry.AllAt(tile);
                if (anchors.Count < 2) { s_pending.Remove(tile); continue; }
                bool synced = anchors.TrueForAll(a => a.StoredPayload != null && a.StoredPayload.Length > 0);
                if (!synced && Time.realtimeSinceStartup - s_pending[tile] < PayloadWaitSeconds) continue;
                if (Applying && ZNet.instance.IsSaving()) return;   // never snapshot or write while a world save runs
                s_pending.Remove(tile);
                try { Heal(tile, anchors); }
                catch (System.Exception ex)
                {
                    FiresLogger.LogError($"[TileHeal] tile ({tile.x},{tile.y}) threw, nothing more done to it: {ex.GetType().Name}: {ex.Message}");
                }
                return;
            }
        }

        private static void Heal(Vector2i tile, List<TileAnchor> anchors)
        {
            anchors.RemoveAll(a => a.Zdo == null);
            if (anchors.Count < 2) return;
            anchors.Sort((x, y) => Compare(x.Zdo.m_uid, y.Zdo.m_uid));
            var datas = new TileData[anchors.Count];
            for (int i = 0; i < anchors.Count; i++) datas[i] = Decode(anchors[i].StoredPayload);
            int k = System.Array.FindIndex(datas, d => d != null);
            string mode = Applying ? "APPLY" : "DRY RUN";
            string all = string.Join("/", anchors.ConvertAll(a => Id(a.Zdo.m_uid)));
            var shown = TileAnchorRegistry.GetAt(tile);
            string showing = shown != null && shown.Zdo != null ? Id(shown.Zdo.m_uid) : "none";
            if (k < 0)
            {
                FiresLogger.LogWarning($"[TileHeal] {mode} plan: tile ({tile.x},{tile.y}): {anchors.Count} anchors {all}, none with a readable "
                                       + "payload; left as they are.");
                return;
            }
            var keeper = anchors[k];
            var losers = new List<TileAnchor>();
            for (int i = 0; i < anchors.Count; i++) if (i != k) losers.Add(anchors[i]);
            string loserIds = string.Join("/", losers.ConvertAll(a => Id(a.Zdo.m_uid) + (datas[anchors.IndexOf(a)] == null ? " (no data)" : "")));
            var merged = Merge(datas, k, anchors, out string layers);
            FiresLogger.LogWarning($"[TileHeal] {mode} plan: tile ({tile.x},{tile.y}): {anchors.Count} anchors {all} -> keep {Id(keeper.Zdo.m_uid)}, "
                                   + $"losers {loserIds}; {layers}; this peer shows {showing}."
                                   + (Applying ? "" : " Nothing written."));
            if (Applying) Apply(tile, anchors, keeper, losers, datas[k], merged);
        }

        private static void Apply(Vector2i tile, List<TileAnchor> anchors, TileAnchor keeper, List<TileAnchor> losers, TileData before, TileData merged)
        {
            if (s_merges >= MaxMergesPerSession)
            {
                if (!s_capLogged)
                    FiresLogger.LogWarning($"[TileHeal] APPLY cap reached: {MaxMergesPerSession} tiles healed this session; the rest only plan until the next session.");
                s_capLogged = true;
                return;
            }
            if (!EnsureSnapshot(out string snapshotFailure))
            {
                FiresLogger.LogError($"[TileHeal] APPLY REFUSED for tile ({tile.x},{tile.y}): no save snapshot ({snapshotFailure}). Nothing written.");
                return;
            }
            string payloadsFailure = SavePayloads(tile, anchors);
            if (payloadsFailure != null)
            {
                FiresLogger.LogError($"[TileHeal] APPLY REFUSED for tile ({tile.x},{tile.y}): its anchors' payloads could not be kept ({payloadsFailure}). Nothing written.");
                return;
            }
            byte[] payload = TerrainTileCodec.Encode(merged);
            if (!keeper.WriteHealedPayload(payload, out string writeFailure))
            {
                FiresLogger.LogError($"[TileHeal] APPLY FAILED for tile ({tile.x},{tile.y}): writing the keeper {Id(keeper.Zdo.m_uid)} failed ({writeFailure}); no anchor removed.");
                return;
            }
            var removed = new List<string>();
            var kept = new List<string>();
            foreach (var loser in losers)
            {
                string id = Id(loser.Zdo.m_uid);
                if (loser.DestroyAsLoser(out string destroyFailure)) removed.Add(id);
                else kept.Add($"{id} ({destroyFailure})");
            }
            s_merges++;
            FiresLogger.LogWarning($"[TileHeal] APPLIED tile ({tile.x},{tile.y}): keeper {Id(keeper.Zdo.m_uid)} heights {Count(before.HeightModified)} -> "
                                   + $"{Count(merged.HeightModified)}, paint {Count(before.PaintModified)} -> {Count(merged.PaintModified)}, biome "
                                   + $"{Count(before.BiomeModified)} -> {Count(merged.BiomeModified)}, disks {before.Disks?.Count ?? 0} -> {merged.Disks.Count} "
                                   + $"({payload.Length} B written, read back OK); removed {(removed.Count > 0 ? string.Join("/", removed) : "none")}"
                                   + (kept.Count > 0 ? $"; NOT removed {string.Join(", ", kept)}" : "")
                                   + $"; merge {s_merges}/{MaxMergesPerSession} this session; snapshot {s_snapshot}.");
        }

        // One copy per session of the world's save as it stands on disk (the state before any merge is saved): the world
        // folder of a 1.0 save, or the flat files with their .old copies, into FAT's own records folder.
        private static bool EnsureSnapshot(out string failure)
        {
            failure = null;
            if (s_snapshot != null) return true;
            if (s_snapshotFailed) { failure = "this session's snapshot failed earlier"; return false; }
            s_snapshotFailed = true;
            var world = ZNet.World;
            if (world == null) { failure = "no world"; return false; }
            if (world.m_fileSource != FileHelpers.FileSource.Local) { failure = $"the save is {world.m_fileSource}, not local"; return false; }
            string folder = VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(world.m_name,
                SnapshotSuffix + System.DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string root = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
                string chunked = System.IO.Path.Combine(root, world.m_name);
                var sources = new List<string>();
                if (System.IO.Directory.Exists(chunked))
                    sources.AddRange(System.IO.Directory.GetFiles(chunked));
                else
                    foreach (string ending in new[] { ".db", ".fwl", ".db.old", ".fwl.old" })
                    {
                        string file = System.IO.Path.Combine(root, world.m_name + ending);
                        if (System.IO.File.Exists(file)) sources.Add(file);
                    }
                if (sources.Count == 0) { failure = $"no save files found for '{world.m_name}' under {root}"; return false; }
                System.IO.Directory.CreateDirectory(folder);
                long bytes = 0;
                foreach (string file in sources)
                {
                    string target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(file));
                    System.IO.File.Copy(file, target, overwrite: false);
                    bytes += new System.IO.FileInfo(target).Length;
                }
                s_snapshot = folder;
                s_snapshotFailed = false;
                FiresLogger.LogWarning($"[TileHeal] snapshot: {sources.Count} file(s), {bytes / 1048576.0:0.0} MB of '{world.m_name}' copied to {folder} "
                                       + $"in {clock.ElapsedMilliseconds} ms, before the first merge of this session.");
                return true;
            }
            catch (System.Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        // Each involved anchor's stored payload, as it was, beside the snapshot: the exact data a merge replaces.
        private static string SavePayloads(Vector2i tile, List<TileAnchor> anchors)
        {
            try
            {
                foreach (var anchor in anchors)
                {
                    byte[] bytes = anchor.StoredPayload;
                    if (bytes == null) continue;
                    var id = anchor.Zdo.m_uid;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(s_snapshot, $"tile_{tile.x}_{tile.y}_{id.UserID}_{id.ID}.bin"), bytes);
                }
                return null;
            }
            catch (System.Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
        }

        private static TileData Merge(TileData[] datas, int k, List<TileAnchor> anchors, out string layers)
        {
            var keep = datas[k];
            int n = TerrainTileCodec.VERTEX_COUNT;
            var m = new TileData
            {
                TileXZ = keep.TileXZ,
                Heights = new float[n], HeightModified = new bool[n],
                Paint = new Color32[n], PaintModified = new bool[n],
                Biome = new ushort[n], BiomeModified = new bool[n],
                Disks = new List<DiskRecord>(),
                BiomeOverride = keep.BiomeOverride,
                FloatingIslandMode = keep.FloatingIslandMode,
            };
            var outvoted = new int[datas.Length];
            string heights = Layer(datas, k, d => d.HeightModified, (d, i) => d.Heights, (a, b) => Mathf.Abs(a - b) <= HeightTolerance,
                                   m.HeightModified, m.Heights, outvoted);
            string paint = Layer(datas, k, d => d.PaintModified, (d, i) => d.Paint, (a, b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a,
                                 m.PaintModified, m.Paint, outvoted);
            string biome = Layer(datas, k, d => d.BiomeModified, (d, i) => d.Biome, (a, b) => a == b, m.BiomeModified, m.Biome, outvoted);

            // Disks by StableId: the keeper's own record where it has one, otherwise the lowest-ZDOID anchor's.
            var seen = new HashSet<int>();
            int diskDiffers = 0;
            for (int order = 0; order < datas.Length; order++)
            {
                int j = order == 0 ? k : (order <= k ? order - 1 : order);
                var d = datas[j];
                if (d?.Disks == null) continue;
                foreach (var disk in d.Disks)
                {
                    if (seen.Add(disk.StableId)) m.Disks.Add(disk);
                    else if (!m.Disks.Exists(x => x.StableId == disk.StableId && x.Equals(disk))) diskDiffers++;
                }
            }
            // Tile-wide fields (the biome override, the floating-island mode): only anchors that HOLD edits vote. The empty
            // duplicates were spawned blank by other peers and carry the defaults, so letting them vote, or keeping a blank
            // keeper's value, would undo a tile-wide choice (R72 (-29,-28): the edited anchor had the island's mode, the two
            // empty ones did not). Majority among the editing anchors, a tie to the keeper when it edits, else the lowest.
            var editors = new List<int>();
            for (int j = 0; j < datas.Length; j++) if (HasEdits(datas[j])) editors.Add(j);
            if (editors.Count == 0) for (int j = 0; j < datas.Length; j++) if (datas[j] != null) editors.Add(j);
            var tally = new List<(ushort biome, bool island, int count, bool keeper, int first)>();
            foreach (int j in editors)
            {
                var d = datas[j];
                int g = tally.FindIndex(t => t.biome == d.BiomeOverride && t.island == d.FloatingIslandMode);
                if (g < 0) { tally.Add((d.BiomeOverride, d.FloatingIslandMode, 0, false, j)); g = tally.Count - 1; }
                var t0 = tally[g];
                tally[g] = (t0.biome, t0.island, t0.count + 1, t0.keeper || j == k, t0.first);
            }
            int win = 0;
            for (int g = 1; g < tally.Count; g++)
            {
                var a = tally[g]; var b = tally[win];
                if (a.count > b.count || (a.count == b.count && ((a.keeper && !b.keeper) || (!a.keeper && !b.keeper && a.first < b.first)))) win = g;
            }
            m.BiomeOverride = tally[win].biome;
            m.FloatingIslandMode = tally[win].island;
            bool tileWideChanged = m.BiomeOverride != keep.BiomeOverride || m.FloatingIslandMode != keep.FloatingIslandMode;

            var voted = new List<string>();
            for (int j = 0; j < outvoted.Length; j++)
                if (outvoted[j] > 0) voted.Add($"{Id(anchors[j].Zdo.m_uid)} x{outvoted[j]}");
            layers = $"heights {heights}; paint {paint}; biome {biome}; disks union {m.Disks.Count}"
                     + (diskDiffers > 0 ? $" ({diskDiffers} differing copies, the keeper's kept)" : "")
                     + $"; tile-wide: biome override {m.BiomeOverride}, island mode {(m.FloatingIslandMode ? "on" : "off")} "
                     + $"(voted by {editors.Count} anchor(s) with edits{(tally.Count > 1 ? ", they disagreed" : "")}{(tileWideChanged ? "; differs from the keeper's own" : "")})"
                     + (voted.Count > 0 ? $"; outvoted: {string.Join(", ", voted)}" : "")
                     + "; per anchor: " + string.Join(", ", Enumerable(datas.Length).ConvertAll(j => Describe(anchors[j], datas[j], j == k)));
            return m;
        }

        private static bool HasEdits(TileData d)
            => d != null && (Count(d.HeightModified) > 0 || Count(d.PaintModified) > 0 || Count(d.BiomeModified) > 0 || (d.Disks?.Count ?? 0) > 0);

        private static List<int> Enumerable(int n)
        {
            var list = new List<int>(n);
            for (int i = 0; i < n; i++) list.Add(i);
            return list;
        }

        // "1:61530 keeper (h19 p0 b0 d1, biome 0, island on)" / "1:61531 (empty, biome 0, island off)"
        private static string Describe(TileAnchor anchor, TileData d, bool keeper)
        {
            string id = Id(anchor.Zdo.m_uid) + (keeper ? " keeper" : "");
            if (d == null) return $"{id} (unreadable)";
            string counts = HasEdits(d)
                ? $"h{Count(d.HeightModified)} p{Count(d.PaintModified)} b{Count(d.BiomeModified)} d{d.Disks?.Count ?? 0}"
                : "empty";
            return $"{id} ({counts}, biome {d.BiomeOverride}, island {(d.FloatingIslandMode ? "on" : "off")})";
        }

        // Per vertex: the anchors that edited it vote; the largest group of equal values wins, a tie going to the group with
        // the keeper in it, else the lowest-ZDOID voter's. "union U (C majority-resolved)".
        private static string Layer<T>(TileData[] datas, int k, System.Func<TileData, bool[]> modified, System.Func<TileData, int, T[]> values,
                                       System.Func<T, T, bool> same, bool[] outModified, T[] outValues, int[] outvoted)
        {
            int union = 0, resolved = 0;
            var voters = new List<int>();
            var groupValue = new List<T>();
            var groupCount = new List<int>();
            var groupFirst = new List<int>();
            var voterGroup = new List<int>();
            for (int i = 0; i < outModified.Length; i++)
            {
                voters.Clear();
                for (int j = 0; j < datas.Length; j++)
                {
                    var d = datas[j];
                    if (d == null) continue;
                    var mod = modified(d);
                    var val = values(d, i);
                    if (mod == null || val == null || i >= mod.Length || i >= val.Length || !mod[i]) continue;
                    voters.Add(j);
                }
                if (voters.Count == 0) continue;
                union++;
                groupValue.Clear(); groupCount.Clear(); groupFirst.Clear(); voterGroup.Clear();
                foreach (int j in voters)
                {
                    T v = values(datas[j], i)[i];
                    int g = groupValue.FindIndex(x => same(x, v));
                    if (g < 0) { groupValue.Add(v); groupCount.Add(0); groupFirst.Add(j); g = groupValue.Count - 1; }
                    groupCount[g]++;
                    voterGroup.Add(g);
                }
                int best = 0;
                for (int g = 1; g < groupValue.Count; g++)
                {
                    bool more = groupCount[g] > groupCount[best];
                    bool tie = groupCount[g] == groupCount[best];
                    bool keeperHere = voters.Exists(j => j == k && voterGroup[voters.IndexOf(j)] == g);
                    bool keeperBest = voters.Exists(j => j == k && voterGroup[voters.IndexOf(j)] == best);
                    if (more || (tie && keeperHere && !keeperBest) || (tie && !keeperHere && !keeperBest && groupFirst[g] < groupFirst[best])) best = g;
                }
                if (groupValue.Count > 1)
                {
                    resolved++;
                    for (int v = 0; v < voters.Count; v++) if (voterGroup[v] != best) outvoted[voters[v]]++;
                }
                outModified[i] = true;
                outValues[i] = groupValue[best];
            }
            return $"union {union} ({resolved} majority-resolved)";
        }

        private static int Count(bool[] flags)
        {
            if (flags == null) return 0;
            int c = 0;
            foreach (bool f in flags) if (f) c++;
            return c;
        }

        private static TileData Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try { return TerrainTileCodec.Decode(bytes); }
            catch (System.Exception) { return null; }
        }

        private static string Id(ZDOID id) => $"{id.UserID}:{id.ID}";

        private static int Compare(ZDOID a, ZDOID b) => a.UserID != b.UserID ? a.UserID.CompareTo(b.UserID) : a.ID.CompareTo(b.ID);

        // ── Purge ────────────────────────────────────────────────────────────────────────────────────────────────
        // The 2D tile layer is retired (Fire, 2026-09-29: "we will clean them from any world they may exist in"). Purge removes
        // every tile anchor of the loaded world, on any world type, on the server only: the world's save is snapshotted into
        // FiresWorldGen first and every anchor's raw payload is kept beside it; without both nothing is removed, and nothing runs
        // while a world save does. Two ways in: mode Purge (once at world load) and the admin command fat_tile_purge.
        private const string PurgeCommand = "fat_tile_purge";
        private const string PurgeRpc = "FAT_TilePurge", PurgeReplyRpc = "FAT_TilePurgeReply";
        private const float PurgeWaitSeconds = 5f;
        private static readonly int K_TILE_DATA = "va_tile_data".GetStableHashCode();
        private static bool s_purgeRpcs;

        // ZNet.Start postfix: the world's ZDOs are loaded, so a cfg purge can run once they have settled.
        internal static void OnWorldLoaded()
        {
            if (s_mode == null || s_mode.Value != TileHealMode.Purge || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var plugin = global::VerdantsAscent.FiresAdminTerrain.Instance;
            if (plugin == null) { FiresLogger.LogError("[TileHeal] PURGE (cfg) not started: no plugin instance to run it."); return; }
            plugin.StartCoroutine(PurgeWhenSettled());
        }

        private static System.Collections.IEnumerator PurgeWhenSettled()
        {
            yield return new WaitForSecondsRealtime(PurgeWaitSeconds);
            while (ZNet.instance != null && ZNet.instance.IsSaving()) yield return null;
            if (ZNet.instance == null) yield break;
            FiresLogger.LogWarning("[TileHeal] " + Purge(confirm: true, "cfg '01 Heal duplicate tile anchors' = Purge"));
        }

        internal static void EnsureRpcs()
        {
            if (ZRoutedRpc.instance == null) return;
            // One registration per ZRoutedRpc (a new one each session).
            if (s_purgeRpcs && s_rpcOwner == ZRoutedRpc.instance) return;
            s_rpcOwner = ZRoutedRpc.instance;
            s_purgeRpcs = true;
            ZRoutedRpc.instance.Register<bool>(PurgeRpc, OnPurgeAsked);
            ZRoutedRpc.instance.Register<string>(PurgeReplyRpc, (sender, text) => Say(text));
        }

        private static ZRoutedRpc s_rpcOwner;

        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand(PurgeCommand,
                "[confirm] - the 2D tile layer is retired: lists this world's tile anchors, or with 'confirm' removes them all "
                + "(server, admin; save snapshot + every anchor's payload into FiresWorldGen first)",
                args =>
                {
                    bool confirm = args.Args.Length > 1 && args.Args[1].Equals("confirm", System.StringComparison.OrdinalIgnoreCase);
                    if (ZNet.instance == null) { args.Context.AddString($"{PurgeCommand}: no world loaded."); return; }
                    if (ZNet.instance.IsServer()) { Say(Purge(confirm, $"console ({PurgeCommand}{(confirm ? " confirm" : "")})")); return; }
                    if (ZRoutedRpc.instance == null) { args.Context.AddString($"{PurgeCommand}: not connected."); return; }
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), PurgeRpc, confirm);
                    args.Context.AddString($"{PurgeCommand}: asked the server{(confirm ? " to purge" : " for a count")}; its answer follows.");
                });
        }

        private static void OnPurgeAsked(long sender, bool confirm)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string text = VerdantsAscent.Terrain.Lifecycle.AdminCheck.IsAdmin(sender)
                ? Purge(confirm, $"admin peer {sender} ({PurgeCommand}{(confirm ? " confirm" : "")})")
                : $"{PurgeCommand} refused: peer {sender} is not an admin.";
            if (!text.StartsWith(PurgeCommand)) FiresLogger.LogWarning("[TileHeal] " + text);
            ZRoutedRpc.instance?.InvokeRoutedRPC(sender, PurgeReplyRpc, text);
        }

        private static void Say(string text)
        {
            FiresLogger.LogWarning("[TileHeal] " + text);
            try { Console.instance?.Print("[TileHeal] " + text); } catch (System.Exception) { }
        }

        // Without confirm: a count only. With it: snapshot, payloads, then every anchor removed; one line per tile, and the
        // total returned.
        private static string Purge(bool confirm, string why)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return "PURGE: server only.";
            if (ZNet.instance.IsSaving()) return "PURGE REFUSED: a world save is running; ask again after it.";
            string world = ZNet.World?.m_name ?? "?";
            var zdos = new List<ZDO>();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(TileAnchorPrefab.PrefabName, zdos, ref index)) { }
            zdos.RemoveAll(z => z == null || !z.IsValid());
            var byTile = new SortedDictionary<Vector2i, List<ZDO>>(Comparer<Vector2i>.Create((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y)));
            foreach (var zdo in zdos)
            {
                var tile = TileAnchorRegistry.WorldToTileIndex(zdo.GetPosition());
                if (!byTile.TryGetValue(tile, out var list)) byTile[tile] = list = new List<ZDO>();
                list.Add(zdo);
            }
            if (zdos.Count == 0) return $"PURGE ({why}): '{world}' has no tile anchors; nothing to do.";
            if (!confirm)
                return $"PURGE count ({why}): '{world}' has {zdos.Count} tile anchor(s) on {byTile.Count} tile(s); '{PurgeCommand} confirm' removes them.";

            if (!EnsureSnapshot(out string snapshotFailure))
                return $"PURGE REFUSED ({why}): no save snapshot ({snapshotFailure}). Nothing removed.";
            try
            {
                foreach (var zdo in zdos)
                {
                    byte[] bytes = zdo.GetByteArray(K_TILE_DATA);
                    if (bytes == null || bytes.Length == 0) continue;
                    var tile = TileAnchorRegistry.WorldToTileIndex(zdo.GetPosition());
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(s_snapshot, $"tile_{tile.x}_{tile.y}_{zdo.m_uid.UserID}_{zdo.m_uid.ID}.bin"), bytes);
                }
            }
            catch (System.Exception ex)
            {
                return $"PURGE REFUSED ({why}): the anchors' payloads could not be kept ({ex.GetType().Name}: {ex.Message}). Nothing removed.";
            }

            int removed = 0, failed = 0;
            long session = ZDOMan.GetSessionID();
            foreach (var entry in byTile)
            {
                var ids = new List<string>();
                var notRemoved = new List<string>();
                int heights = 0, paint = 0, biome = 0;
                foreach (var zdo in entry.Value)
                {
                    var data = Decode(zdo.GetByteArray(K_TILE_DATA));
                    if (data != null) { heights += Count(data.HeightModified); paint += Count(data.PaintModified); biome += Count(data.BiomeModified); }
                    string id = Id(zdo.m_uid);
                    try
                    {
                        var view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                        zdo.SetOwner(session);
                        if (view != null) ZNetScene.instance.Destroy(view.gameObject);
                        else ZDOMan.instance.DestroyZDO(zdo);
                        ids.Add(id);
                        removed++;
                    }
                    catch (System.Exception ex)
                    {
                        notRemoved.Add($"{id} ({ex.GetType().Name}: {ex.Message})");
                        failed++;
                    }
                }
                FiresLogger.LogWarning($"[TileHeal] PURGED tile ({entry.Key.x},{entry.Key.y}): {ids.Count} anchor(s) {string.Join("/", ids)}; "
                                       + $"heights {heights}, paint {paint}, biome {biome} vertex edit(s) removed"
                                       + (notRemoved.Count > 0 ? $"; NOT removed {string.Join(", ", notRemoved)}" : "") + ".");
            }
            return $"PURGE done ({why}): '{world}': {byTile.Count} tile(s), {removed} anchor(s) removed, {failed} failed; snapshot and payloads in "
                   + $"{s_snapshot}. The removal is in the world's next save.";
        }
    }
}
