using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Water.Core
{
    // Placed water drawn like the skyland water, which lays a smooth mesh, so caves and the V tools
    // use the same mesh logic. Every fresh-water disk the tools placed (Vpour, Vpool, Vpond,
    // WaterSheets) renders through VoxelWaterSheetMesh.Build, the builder of the generated and skyland sheets: per
    // voxel chunk, a level field (wet where a disk covers the column and the voxel floor lies under its surface, mopped columns
    // never lidded), the floor under each column, and the disks' own surface heights as the provider - flat at each disk's height,
    // blended where disks overlap, continuous across chunks, so shared corners match and nothing seams. The builder runs the
    // skirt past the shore that the banks bury, the crest and cave-mouth guards and the smoothing; the material is the calm inland
    // sheet. The disks' WaterMeshCombiners keep their swim triggers; their level and this sheet both come from PlacedWaterField
    // (0.2.1091), and they build no grid mesh of their own any more.
    // A chunk rebuilds when the set of disks changes or its voxels change (VoxelWaterSheetMesh.ChunkMeshed), at most
    // RebuildsPerFrame a frame. Graphics peers only: a dedicated server draws nothing.
    internal static class PlacedWaterSheets
    {
        private const int RebuildsPerFrame = 8;   // 0.2.1102: a hard cap; the real limit is BuildBudgetMs
        private const double BuildBudgetMs = 4.0;
        // 0.2.1191 (Fire: "it takes many second for water to show up when we enter an area"; round 03:31: "42 placed-water chunk(s)
        // rebuilt ... in 372.7 ms of work, 3069 ms after the change"): a chunk never drawn yet is a FIRST SHOW - it builds ahead of
        // re-builds, with its own larger budget, so ponds appear with the zone; re-builds (voxel remesh, state, tools) keep 4 ms.
        private const double FirstShowBudgetMs = 14.0;
        private static readonly Dictionary<Vector3Int, float> s_wantedAt = new Dictionary<Vector3Int, float>();
        private static int s_showN; private static double s_showSumMs, s_showMaxMs; private static float s_showLogAt, s_showOneAt;
        private const float BandBelow = 2f, BandAbove = 34f;   // a chunk draws the disks whose surface lies in its y band

        private static readonly Dictionary<Vector3Int, GameObject> s_drawn = new Dictionary<Vector3Int, GameObject>();
        private static readonly HashSet<Vector3Int> s_dirty = new HashSet<Vector3Int>();
        private static readonly HashSet<Vector3Int> s_wanted = new HashSet<Vector3Int>();
        private static long s_diskSignature;
        private static bool s_hooked, s_loggedFirst;

        // True where this peer draws placed water itself (the combiners then hide their grid mesh).
        internal static bool Active => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        // From WaterSheets.Tick (the water tools' per-frame tick), after the disk index refresh.
        internal static int Tick(IReadOnlyList<WaterSheets.DiskInfo> disks, long signature, out int rebuilt)
        {
            rebuilt = 0;
            if (!Active) return 0;
            if (!s_hooked) { s_hooked = true; VoxelWaterSheetMesh.ChunkMeshed += OnChunkMeshed; }
            if (signature != s_diskSignature)
            {
                // 0.2.1100 (Fire: a Vpour fix "just took a bit to apply"): every disk change used to re-dirty EVERY drawn
                // placed-water chunk in the world, rebuilt RebuildsPerFrame a frame in arbitrary order, so the chunk under the new
                // water waited behind all the others (and a drag re-dirtied them all on every dab). Now only chunks overlapping a
                // disk that was added, removed or moved rebuild (plus chunks that are newly wanted), nearest the camera first.
                bool first = s_diskSignature == 0;
                s_diskSignature = signature;
                s_wanted.Clear();
                s_capChunks.Clear();   // 0.2.1343
                s_changedDisks.Clear();
                var now = new Dictionary<int, WaterSheets.DiskInfo>();
                foreach (var d in disks)
                {
                    if (!d.Water) continue;
                    now[d.Hash] = d;
                    ForChunks(d, k => s_wanted.Add(k));
                    if (!d.Pond) ForChunks(d, k => s_capChunks.Add(k));   // 0.2.1343
                }
                float tWant = Time.realtimeSinceStartup;   // 0.2.1191: first-show clock per newly wanted, undrawn chunk
                foreach (var k in s_wanted) if (!s_drawn.ContainsKey(k) && !s_wantedAt.ContainsKey(k)) s_wantedAt[k] = tWant;
                foreach (var key in new List<Vector3Int>(s_drawn.Keys))
                    if (!s_wanted.Contains(key)) { Drop(key); }
                int changed = 0;
                if (first) { foreach (var key in s_wanted) s_dirty.Add(key); changed = now.Count; }
                else
                {
                    foreach (var kv in now)
                        if (!s_lastDisks.TryGetValue(kv.Key, out var old) || old.X != kv.Value.X || old.Z != kv.Value.Z || old.Y != kv.Value.Y || old.R != kv.Value.R || old.Pond != kv.Value.Pond)
                        { changed++; s_changedDisks.Add(kv.Value); ForChunks(kv.Value, k => { s_dirty.Add(k); MarkLive(k); Why(k, W_Disk); }); if (s_lastDisks.TryGetValue(kv.Key, out old)) ForChunks(old, k => { if (s_wanted.Contains(k)) { s_dirty.Add(k); MarkLive(k); Why(k, W_Disk); } }); }
                    foreach (var kv in s_lastDisks)
                        if (!now.ContainsKey(kv.Key)) { changed++; s_changedDisks.Add(kv.Value); ForChunks(kv.Value, k => { if (s_wanted.Contains(k)) { s_dirty.Add(k); MarkLive(k); Why(k, W_Disk); } }); }
                    foreach (var key in s_wanted) if (!s_drawn.ContainsKey(key)) s_dirty.Add(key);
                }
                if (FiresLogger.DebugEnabled)   // 0.2.1350 (proof: when this client first knows each disk; before s_lastDisks moves on)
                    foreach (var kv in now)
                        if (!s_lastDisks.ContainsKey(kv.Key)) VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.NoteWaterDisk(kv.Value.X, kv.Value.Z, kv.Value.R);
                s_lastDisks = now;
                QueueCapsNearChanged(now);   // 0.2.1343
                ReasonDisk += s_dirty.Count;
                if (changed > 0) { s_changeAt = Time.realtimeSinceStartup; s_changeDisks += changed; s_changeChunks = s_dirty.Count; s_buildMs = 0; s_firstBuildAt = -1f; }
            }
            DrainHeld();    // 0.2.1344
            DrainLater();   // 0.2.1331
            NoteTimers();   // 0.2.1343
            int built = 0;
            Vector3 cam = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            // 0.2.1102 (overnight #2: Vpour 11-20 ms per sheet chunk, 2 a frame = 30-40 ms frames): a time budget instead of a count;
            // the nearest chunk always builds, more only while the frame has budget left.
            long frame0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long budget = (long)(BuildBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            long firstBudget = (long)(FirstShowBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            while ((s_dirty.Count > 0 || s_sliceActive) && built < RebuildsPerFrame + 8)
            {
                // 0.2.1344: one sliced build at a time; a tool stroke's sheet (immediate) takes the shared scratch, the slice restarts later.
                if (s_sliceActive)
                {
                    if (!s_wanted.Contains(s_sliceKey)) { s_sliceActive = false; s_sliceAborted++; }
                    else if (s_live.Count > 0) { s_dirty.Add(s_sliceKey); s_sliceActive = false; s_sliceAborted++; }
                    else
                    {
                        long deadline = frame0 + (s_sliceFirstShow ? firstBudget : budget);
                        if (built > 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) break;
                        if (!ContinueSlice(deadline)) break;   // out of budget: the next frame goes on
                        s_sliceActive = false;
                        AfterBuilt(s_sliceKey, s_sliceMs, s_sliceFirstShow);
                        built++;
                        continue;
                    }
                    if (s_dirty.Count == 0) break;
                }
                // 0.2.1191: first shows (wanted, never drawn) go first, nearest first; then re-builds, nearest first.
                Vector3Int key = default;
                float best = float.MaxValue; bool firstShow = false;
                foreach (var k in s_dirty)
                {
                    if (s_live.Count > 0 && !s_live.Contains(k)) continue;   // 0.2.1344: a tool stroke's sheets first
                    bool fs = s_wanted.Contains(k) && !s_drawn.ContainsKey(k);
                    if (firstShow && !fs) continue;
                    float dx = (k.x + 0.5f) * VoxelChunk.CELLS - cam.x, dz = (k.z + 0.5f) * VoxelChunk.CELLS - cam.z;
                    float dd = dx * dx + dz * dz;
                    if ((fs && !firstShow) || dd < best) { best = dd; key = k; firstShow = fs; }
                }
                if (best == float.MaxValue) { s_live.Clear(); continue; }   // 0.2.1344: nothing live left in the dirty set
                long spent = System.Diagnostics.Stopwatch.GetTimestamp() - frame0;
                if (built > 0 && spent >= (firstShow ? firstBudget : budget)) break;
                if (!firstShow && built >= RebuildsPerFrame) break;
                s_dirty.Remove(key);
                bool live = s_live.Remove(key);   // 0.2.1344
                if (!s_wanted.Contains(key)) continue;
                if (!live && SliceOn) { StartSlice(key, firstShow); continue; }   // 0.2.1344: the loop's top runs it
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                BuildChunk(key);
                double one = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                AfterBuilt(key, one, firstShow);
                built++;
            }
            if (s_dirty.Count == 0 && !s_sliceActive && s_changeAt >= 0f)
            {
                float now2 = Time.realtimeSinceStartup;
                FiresLogger.LogInfo($"[Vpour] applied: {s_changeChunks} placed-water chunk(s) rebuilt for {s_changeDisks} changed disk(s) in {s_buildMs:0.0} ms of work, "
                    + $"{(now2 - s_changeAt) * 1000f:0} ms after the change (first chunk queued {(s_firstBuildAt < 0f ? 0f : (s_firstBuildAt - s_changeAt) * 1000f):0} ms; only chunks the change touches, nearest first)");
                s_changeAt = -1f; s_changeDisks = 0;
            }
            rebuilt = built;
            return s_dirty.Count + (s_sliceActive ? 1 : 0);
        }

        // 0.2.1344: one finished build, whole or sliced.
        private static void AfterBuilt(Vector3Int key, double one, bool firstShow)
        {
            s_buildMs += one; if (one > WorstChunkMs) WorstChunkMs = one;
            s_tBuildMs += one; s_tBuildCount++; if (one > s_tBuildWorst) s_tBuildWorst = one;   // 0.2.1343
            if (s_firstBuildAt < 0f && s_changeAt >= 0f) s_firstBuildAt = Time.realtimeSinceStartup;
            if (firstShow) NoteFirstShow(key);
            s_builtAt[key] = Time.realtimeSinceStartup;
            s_repeat.TryGetValue(key, out int n); s_repeat[key] = n + 1;
            Why(key, W_Built);   // 0.2.1347
        }

        private static Dictionary<int, WaterSheets.DiskInfo> s_lastDisks = new Dictionary<int, WaterSheets.DiskInfo>();
        private static float s_changeAt = -1f, s_firstBuildAt = -1f;
        private static int s_changeDisks, s_changeChunks;
        private static double s_buildMs;

        private static void ForChunks(WaterSheets.DiskInfo d, System.Action<Vector3Int> f)
        {
            int ky = Mathf.FloorToInt(d.Y / VoxelChunk.CELLS);
            int x0 = Mathf.FloorToInt((d.X - d.R - 4f) / VoxelChunk.CELLS), x1 = Mathf.FloorToInt((d.X + d.R + 4f) / VoxelChunk.CELLS);
            int z0 = Mathf.FloorToInt((d.Z - d.R - 4f) / VoxelChunk.CELLS), z1 = Mathf.FloorToInt((d.Z + d.R + 4f) / VoxelChunk.CELLS);
            for (int kx = x0; kx <= x1; kx++)
                for (int kz = z0; kz <= z1; kz++)
                    f(new Vector3Int(kx, ky, kz));
        }

        // 0.2.1097: WaterStateField changed (paint, season, band): rebuild every drawn chunk so its state texture refills.
        internal static void MarkAllDirty() { foreach (var key in s_wanted) if (s_dirty.Add(key)) { ReasonState++; Why(key, W_State); } }

        // 0.2.1165: why chunks were queued (the [WaterTools] Debug line), and the worst single build.
        internal static int ReasonDisk, ReasonMeshed, ReasonState, ReasonTool;
        internal static double WorstChunkMs;

        // 0.2.1097 (Vmop): every drawn chunk whose columns overlap the ring, any layer, rebuilds now. Returns chunks marked.
        // 0.2.1344: live = a tool stroke (builds whole, at once); a record's mask arriving is not. builtSince >= 0: only sheets built since then
        // (the same mop mask coming back with a reloaded chunk: a sheet older than the chunk's drop already has it).
        internal static int MarkDirtyIn(Vector3 center, float radius, bool live = true, float builtSince = -1f)
        {
            s_tMarkCalls++;   // 0.2.1343
            int n = 0;
            float c = VoxelChunk.CELLS;
            foreach (var key in s_wanted)
            {
                float x0 = key.x * c - 1f, x1 = (key.x + 1) * c + 1f, z0 = key.z * c - 1f, z1 = (key.z + 1) * c + 1f;
                float dx = Mathf.Max(x0 - center.x, 0f, center.x - x1), dz = Mathf.Max(z0 - center.z, 0f, center.z - z1);
                if (dx * dx + dz * dz > radius * radius) continue;
                if (builtSince >= 0f && (!s_builtAt.TryGetValue(key, out float at) || at < builtSince)) continue;
                if (live) MarkLive(key);
                if (s_dirty.Add(key)) { n++; ReasonTool++; s_tMarked++; Why(key, W_Tool); }
                if (WaterStateField.SeamlessSheets) s_spreadOnBuild.Add(key);   // 0.2.1198; 0.2.1201: neighbours only if its border changed
            }
            return n;
        }

        // 0.2.1344 (r22: a zone meshing toward its swap re-queued its sheets on every chunk it landed, each pass reading half-built ground):
        // while a zone is mid-swap its chunks' remeshes only note their keys; the swap (or a give-up: 20 s, or the zone left mid-swap) runs them once.
        private const float HoldGiveUpSeconds = 20f;
        private sealed class HeldZone { public readonly HashSet<Vector3Int> Beds = new HashSet<Vector3Int>(), Sheets = new HashSet<Vector3Int>(); public float Since; }
        private static readonly Dictionary<Vector2s, HeldZone> s_held = new Dictionary<Vector2s, HeldZone>();
        private static readonly List<Vector2s> s_heldDue = new List<Vector2s>();
        private static float s_heldCheckAt;
        private static int s_hHeld, s_hFlushes, s_hGaveUp, s_hRan;
        private static double s_hFlushMs, s_hFlushWorst;
        private static bool s_holdProof;
        private static bool HoldOn => FiresVoxelConfig.WaterHoldUntilSwap != null && FiresVoxelConfig.WaterHoldUntilSwap.Value;
        private static Vector2s ZoneOf(Vector3Int key) => ZoneSystem.GetZone(new Vector3((key.x + 0.5f) * VoxelChunk.CELLS, 0f, (key.z + 0.5f) * VoxelChunk.CELLS));
        private static HeldZone HoldFor(Vector2s zone)
        {
            if (!s_held.TryGetValue(zone, out var h)) { h = new HeldZone { Since = Time.realtimeSinceStartup }; s_held[zone] = h; }
            s_hHeld++;
            if (!s_holdProof) { s_holdProof = true; FiresLogger.LogInfo("[WaterTools] a zone mid-swap holds its placed-water rebuilds until the swap lands (0.2.1344)"); }
            return h;
        }
        private static void Flush(Vector2s zone, bool swapped = false)
        {
            if (!s_held.TryGetValue(zone, out var h)) return;
            s_held.Remove(zone);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var k in h.Beds) FanOut(k);
            // 0.2.1346: after a swap a held neighbour goes back through the delayed queue, so a sheet between several swapping zones rebuilds once, after the last of them.
            // A give-up builds now (a zone that will not swap must not hold it again).
            foreach (var k in h.Sheets)
            {
                Why(k, W_Flush);   // 0.2.1347
                if (TrimOn && swapped) { if (QuietOn) PushLater(k, Time.realtimeSinceStartup); else s_later[k] = Time.realtimeSinceStartup; }
                else if (s_wanted.Contains(k) && s_dirty.Add(k)) ReasonMeshed++;   // a give-up builds now, past the quiet window too: through s_later its zone would hold it again
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            s_hFlushes++; s_hRan += h.Beds.Count + h.Sheets.Count; s_hFlushMs += ms; if (ms > s_hFlushWorst) s_hFlushWorst = ms;
        }
        // From VoxelZoneStreamer.TrySwap, the frame the zone swaps.
        internal static void NoteZoneSwapped(Vector2s zone) { if (s_held.Count > 0) Flush(zone, true); }

        // 0.2.1346: a wrap starting takes the sheets already drawn in its zone (built before the zone knew it would swap, e.g. before its saved chunks arrived); ZoneReveal shows them in the swap frame.
        internal static void HoldDrawnInZone(Vector2s zone)
        {
            foreach (var kv in s_drawn)
                if (kv.Value != null && ZoneOf(kv.Key).Equals(zone)) ZoneReveal.HoldRenderer(zone, kv.Value.GetComponent<MeshRenderer>());
        }

        // 0.2.1346 fan-out trim (plan: 3-8x water tick, sheets rebuilt 73-680 per 10 s, 'queued by voxel remesh' 389 of 426).
        private static bool TrimOn => FiresVoxelConfig.WaterTrimFanOut != null && FiresVoxelConfig.WaterTrimFanOut.Value;
        private static readonly FatSpreadStat s_fanStat = new FatSpreadStat("placed water fan-out");
        private static int s_trimUndrawn, s_trimChained, s_fanKept;
        private static bool s_trimProof;
        // A zone mid-swap within the sheet's reach (its cap walk for a capped chunk, the rim otherwise): its ground is still about to change the answer.
        private static bool ReachedByMidSwap(Vector3Int key, out Vector2s zone)
        {
            float reach = s_capChunks.Contains(key) ? PlacedWaterField.BankWalk : PlacedWaterField.RimReach, c = VoxelChunk.CELLS;
            var z0 = ZoneSystem.GetZone(new Vector3(key.x * c - reach, 0f, key.z * c - reach));
            var z1 = ZoneSystem.GetZone(new Vector3((key.x + 1) * c + reach, 0f, (key.z + 1) * c + reach));
            for (int zz = z0.y; zz <= z1.y; zz++)
                for (int zx = z0.x; zx <= z1.x; zx++)
                {
                    zone = new Vector2s(zx, zz);
                    if (VoxelZoneStreamer.ZoneMidSwap(zone)) return true;
                }
            zone = default;
            return false;
        }
        private static void DrainHeld()
        {
            if (s_held.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < s_heldCheckAt) return;
            s_heldCheckAt = now + 1f;
            s_heldDue.Clear();
            foreach (var kv in s_held)
                if (now - kv.Value.Since >= HoldGiveUpSeconds || !VoxelZoneStreamer.ZoneMidSwap(kv.Key)) s_heldDue.Add(kv.Key);
            foreach (var z in s_heldDue) { s_hGaveUp++; Flush(z); }
        }

        private static void OnChunkMeshed(Vector3Int key)
        {
            if (HoldOn)
            {
                var zone = ZoneOf(key);
                if (VoxelZoneStreamer.ZoneMidSwap(zone)) { HoldFor(zone).Beds.Add(key); return; }
            }
            FanOut(key);
        }

        private static void FanOut(Vector3Int key)
        {
            long fan0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1346
            bool trim = TrimOn;
            if (trim && !s_trimProof) { s_trimProof = true; FiresLogger.LogInfo("[WaterTools] a ground change rebuilds only drawn sheets it can reach, once after the zones around them swap (0.2.1346)"); }
            if (s_wanted.Contains(key)) { Why(key, W_Own); AskGround(key); }   // 0.2.1347
            if (WaterStateField.SeamlessSheets) s_spreadOnBuild.Add(key);   // 0.2.1198; 0.2.1201: neighbours only if its border changed
            // 0.2.1331: the sheet over this bed can sit one chunk up (the sheet key comes from the disk's y, this key from the bed), and
            // PlacedWaterField's cap walk reads RimReach past the chunk, so its tiles dropped with no rebuild. Layer above: now.
            // Neighbours: through the delayed queue, so a stroke's remeshes cost one rebuild each (1201's lesson).
            var up = new Vector3Int(key.x, key.y + 1, key.z);
            if (s_wanted.Contains(up)) { Why(up, W_Up); AskGround(up); }   // 0.2.1347
            int reach = Mathf.CeilToInt(PlacedWaterField.RimReach / VoxelChunk.CELLS);
            int far = Mathf.CeilToInt(PlacedWaterField.BankWalk / VoxelChunk.CELLS);   // 0.2.1343: the wider walk: past RimReach only chunks with capped (river) water
            float due = Time.realtimeSinceStartup + LaterSeconds;
            for (int y = key.y; y <= key.y + 1; y++)
                for (int dz = -far; dz <= far; dz++)
                    for (int dx = -far; dx <= far; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        var near = new Vector3Int(key.x + dx, y, key.z + dz);
                        if ((Mathf.Abs(dx) > reach || Mathf.Abs(dz) > reach) && !s_capChunks.Contains(near)) continue;
                        if (trim && s_wanted.Contains(near) && !s_drawn.ContainsKey(near)) { s_trimUndrawn++; continue; }   // 0.2.1346: never drawn = already queued as a first show
                        if (s_wanted.Contains(near) && QuietOn) { Why(near, W_Near); PushLater(near, due - LaterSeconds); s_fanKept++; }   // 0.2.1347
                        else if (s_wanted.Contains(near) && !s_later.ContainsKey(near)) { Why(near, W_Near); s_later[near] = due; s_fanKept++; }
                    }
            s_fanStat.Note((System.Diagnostics.Stopwatch.GetTimestamp() - fan0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        // 0.2.1347 (1345 log: up to 17 rebuilds of one chunk in 10 s, 441 of 1901 chunks 3+ times): s_later kept the FIRST due time, so a capped chunk with neighbours landing one by
        // up to two chunks away (BankWalk: 25 columns, 2 layers) came due every 0.5 s for as long as the load ran, each rebuild reading ground still arriving; its own remesh and a border
        // change from a neighbour's build skipped the queue altogether. Now per chunk: the first ground change after a quiet spell builds at once, a change within QuietSeconds of the last
        // build or request moves it to 0.5 s after the last request, at most QuietMaxWait after the first, so it still lands during an endless load. First shows and tool strokes never wait.
        private const float QuietSeconds = 0.5f, QuietMaxWait = 4f;
        private static readonly Dictionary<Vector3Int, float> s_laterFirst = new Dictionary<Vector3Int, float>();
        private static int s_quietHeld, s_quietNow, s_quietMaxed;
        private static bool s_quietProof;
        private static bool QuietOn => FiresVoxelConfig.WaterQuietWindow != null && FiresVoxelConfig.WaterQuietWindow.Value;

        private static void PushLater(Vector3Int k, float now)
        {
            if (!s_laterFirst.TryGetValue(k, out float first)) { first = now; s_laterFirst[k] = now; }
            float due = now + QuietSeconds, cap = first + QuietMaxWait;
            if (due > cap) { due = cap; s_quietMaxed++; }
            s_later[k] = due;
            s_quietHeld++;
            if (!s_quietProof) { s_quietProof = true; FiresLogger.LogInfo("[WaterTools] a drawn sheet rebuilt within 0.5 s waits for a quiet 0.5 s, at most 4 s (0.2.1347)"); }
        }

        // A ground change asks for this sheet. Off = straight into the dirty set (1346).
        private static void AskGround(Vector3Int k)
        {
            if (!s_wanted.Contains(k)) return;
            if (!QuietOn || !s_drawn.ContainsKey(k)) { if (s_dirty.Add(k)) ReasonMeshed++; return; }
            if (s_dirty.Contains(k)) return;
            float now = Time.realtimeSinceStartup;
            if (s_later.ContainsKey(k) || (s_builtAt.TryGetValue(k, out float at) && now - at < QuietSeconds)) { PushLater(k, now); return; }
            s_quietNow++;
            if (s_dirty.Add(k)) ReasonMeshed++;
        }

        // 0.2.1347: why a chunk was asked for, a small ring per chunk, Debug only, read for the top chunk each 10 s and cleared.
        private const byte W_Own = 0, W_Up = 1, W_Near = 2, W_Border = 3, W_Disk = 4, W_Tool = 5, W_State = 6, W_Heightmap = 7, W_Cap = 8, W_Flush = 9, W_Built = 10;
        private static readonly string[] s_whyNames = { "own remesh", "chunk below remeshed", "neighbour remesh", "neighbour border", "disk", "tool", "state", "heightmap", "cap near changed disk", "swap flush", "BUILT" };
        private static readonly int[] s_whyCount = new int[11];
        private sealed class WhyRing { public readonly byte[] R = new byte[32]; public readonly int[] F = new int[32]; public int N; }
        private static readonly Dictionary<Vector3Int, WhyRing> s_why = new Dictionary<Vector3Int, WhyRing>();
        private static void Why(Vector3Int k, byte reason)
        {
            if (!FiresLogger.DebugEnabled) return;
            s_whyCount[reason]++;
            if (!s_why.TryGetValue(k, out var r)) s_why[k] = r = new WhyRing();
            r.R[r.N % 32] = reason; r.F[r.N % 32] = Time.frameCount; r.N++;
        }
        private static void LogWhyTop(Vector3Int top, int rebuilds)
        {
            var sb = new System.Text.StringBuilder(512);
            sb.Append($"[WaterTools] rebuild reasons last 10 s, all chunks:");
            for (int i = 0; i < s_whyCount.Length; i++) if (s_whyCount[i] > 0) sb.Append($" {s_whyNames[i]} {s_whyCount[i]};");
            sb.Append($" top chunk ({top.x},{top.y},{top.z}) built {rebuilds} time(s):");
            if (s_why.TryGetValue(top, out var r))
            {
                int from = Mathf.Max(0, r.N - 32);
                for (int i = from; i < r.N; i++) sb.Append(' ').Append(s_whyNames[r.R[i % 32]]).Append(" f").Append(r.F[i % 32]).Append(',');
                if (from > 0) sb.Append($" ({from} older dropped)");
            }
            sb.Append($"; quiet window: {s_quietNow} at once, {s_quietHeld} held, {s_quietMaxed} at the 4 s cap (0.2.1347)");
            FiresLogger.LogDebug(sb.ToString());
        }

        // 0.2.1331: rebuilds that may wait: a chunk's neighbours, and sheets over a heightmap that regenerated. One entry per chunk,
        // at most LaterPerTick moved to the dirty set per tick.
        private const float LaterSeconds = 0.5f;
        private const int LaterPerTick = 8;
        private static readonly Dictionary<Vector3Int, float> s_later = new Dictionary<Vector3Int, float>();
        private static readonly List<Vector3Int> s_laterDue = new List<Vector3Int>();

        internal static void MarkDirtyLater(Vector3 center, float radius)
        {
            float c = VoxelChunk.CELLS, due = Time.realtimeSinceStartup + LaterSeconds;
            foreach (var key in s_wanted)
            {
                float x0 = key.x * c - 1f, x1 = (key.x + 1) * c + 1f, z0 = key.z * c - 1f, z1 = (key.z + 1) * c + 1f;
                float dx = Mathf.Max(x0 - center.x, 0f, center.x - x1), dz = Mathf.Max(z0 - center.z, 0f, center.z - z1);
                if (dx * dx + dz * dz <= radius * radius) { Why(key, W_Heightmap); if (QuietOn) PushLater(key, due - LaterSeconds); else s_later[key] = due; }   // a heightmap still changing keeps pushing it back; 0.2.1347: to the 4 s cap
            }
        }

        // 0.2.1343: chunks holding capped (river) disks. A disk or ground change up to BankWalk away can move their cap, so they rebuild
        // through the delayed queue (a drag keeps pushing them back). Pond-only chunks have no cap and never queue for this.
        private static readonly HashSet<Vector3Int> s_capChunks = new HashSet<Vector3Int>();
        private static readonly List<WaterSheets.DiskInfo> s_changedDisks = new List<WaterSheets.DiskInfo>();

        private static void QueueCapsNearChanged(Dictionary<int, WaterSheets.DiskInfo> now)
        {
            if (s_changedDisks.Count == 0) return;
            float due = Time.realtimeSinceStartup + LaterSeconds;
            foreach (var kv in now)
            {
                var r = kv.Value;
                if (r.Pond) continue;
                foreach (var c in s_changedDisks)
                {
                    float dx = r.X - c.X, dz = r.Z - c.Z, reach = r.R + c.R + PlacedWaterField.BankWalk;
                    if (dx * dx + dz * dz > reach * reach) continue;
                    ForChunks(r, k => { Why(k, W_Cap); s_later[k] = due; });
                    break;
                }
            }
            s_changedDisks.Clear();
        }

        internal static void MarkCapChunksLater(Vector3 center, float radius)
        {
            float c = VoxelChunk.CELLS, due = Time.realtimeSinceStartup + LaterSeconds;
            foreach (var key in s_capChunks)
            {
                float x0 = key.x * c - 1f, x1 = (key.x + 1) * c + 1f, z0 = key.z * c - 1f, z1 = (key.z + 1) * c + 1f;
                float dx = Mathf.Max(x0 - center.x, 0f, center.x - x1), dz = Mathf.Max(z0 - center.z, 0f, center.z - z1);
                if (dx * dx + dz * dz <= radius * radius) { Why(key, W_Cap); if (QuietOn) PushLater(key, due - LaterSeconds); else s_later[key] = due; }   // 0.2.1347
            }
        }

        // 0.2.1343 [WaterTools] sheet timer (Debug, 10 s): the rebuilds, MarkDirtyIn calls, and the mop refresh a chunk's record arrival runs.
        private static double s_tBuildMs, s_tBuildWorst, s_tMopMs, s_tMopWorst;
        private static int s_tBuildCount, s_tMarkCalls, s_tMarked, s_tMopEvents, s_tMopRebuilt, s_tMopSheets;
        private static float s_tTimerLogAt;
        internal static void NoteMopRefresh(double ms, int rebuilt, int sheets)
        {
            s_tMopMs += ms; s_tMopEvents++; s_tMopRebuilt += rebuilt; s_tMopSheets += sheets;
            if (ms > s_tMopWorst) s_tMopWorst = ms;
        }
        private static void NoteTimers()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_tTimerLogAt) return;
            s_tTimerLogAt = now + 10f;
            if (FiresLogger.DebugEnabled && (s_tBuildCount > 0 || s_tMarkCalls > 0 || s_tMopEvents > 0))
                FiresLogger.LogDebug($"[WaterTools] placed sheets last 10 s: {s_tBuildCount} rebuild(s), total {s_tBuildMs:0.0} ms, worst {s_tBuildWorst:0.0} ms; MarkDirtyIn {s_tMarkCalls} call(s), {s_tMarked} chunk(s) queued; mop-mask events (VoxelChunkAnchor) {s_tMopEvents}, total {s_tMopMs:0.0} ms, worst {s_tMopWorst:0.00} ms, {s_tMopRebuilt} bod(ies), {s_tMopSheets} sheet chunk(s) (0.2.1343)");
            s_tBuildMs = s_tBuildWorst = s_tMopMs = s_tMopWorst = 0;
            s_tBuildCount = s_tMarkCalls = s_tMarked = s_tMopEvents = s_tMopRebuilt = s_tMopSheets = 0;
            NotePhases();   // 0.2.1344
        }

        // 0.2.1344 [WaterTools] Debug, 10 s: the fixes' own numbers. Phases (ms): columns = the whole per-column pass, of which ground / bed / cap
        // are the field's uncached reads; corners and skirt = the surface asks around the builder; builder = BuildSheet with its provider
        // misses; upload = the mesh copy; publish = swap + state bind.
        private static double s_pColumns, s_pCorners, s_pBuilder, s_pSkirt, s_pPublish, s_pSliceWorst;
        private static int s_pSlices;
        private static long s_pGround0, s_pBed0, s_pCap0, s_pUpload0;
        private static void NotePhases()
        {
            int chunks = s_repeat.Count, most = 0, three = 0;
            Vector3Int topKey = default;   // 0.2.1347
            foreach (var kv in s_repeat) { if (kv.Value > most) { most = kv.Value; topKey = kv.Key; } if (kv.Value >= 3) three++; }
            s_repeat.Clear();
            long g = PlacedWaterField.GroundTicks, b = PlacedWaterField.BedTicks, c = PlacedWaterField.CapTicks, u = VoxelWaterSheetMesh.UploadTicks;
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (FiresLogger.DebugEnabled && (chunks > 0 || s_hHeld > 0 || VoxelWaterSheetMesh.SkippedSameGround > 0))
            {
                FiresLogger.LogDebug($"[WaterTools] sheet phases last 10 s: columns {s_pColumns:0.0} ms (all callers: ground {(g - s_pGround0) * f:0.0}, bed {(b - s_pBed0) * f:0.0}, cap walk {(c - s_pCap0) * f:0.0}), corners {s_pCorners:0.0}, builder {s_pBuilder:0.0} (upload {(u - s_pUpload0) * f:0.0}), skirt {s_pSkirt:0.0}, publish {s_pPublish:0.0}; slices {s_pSlices}, worst slice {s_pSliceWorst:0.00} ms, {s_sliceAborted} restarted; {chunks} chunk(s) rebuilt, most repeats on one {most}, rebuilt 3+ times {three} (0.2.1344)");
                // 0.2.1347: the column fixes' numbers, the worst slice by stage, and the top chunk's trigger ring.
                FiresLogger.LogDebug($"[WaterTools] column reads last 10 s: bed cache hits {PlacedWaterField.BedHits}, misses {PlacedWaterField.BedMisses}, worst miss {PlacedWaterField.BedMissWorstMs:0.000} ms; column level reused {s_rawReused}; worst slice by stage: columns {s_pStageWorst[0]:0.00}, corners {s_pStageWorst[1]:0.00}, builder {s_pStageWorst[2]:0.00}, skirt {s_pStageWorst[3]:0.00}, publish {s_pStageWorst[4]:0.00} ms (0.2.1347)");
                if (chunks > 0) LogWhyTop(topKey, most);
                FiresLogger.LogDebug($"[WaterTools] fan-out last 10 s: {s_fanKept} neighbour rebuild(s) queued, {s_trimUndrawn} skipped (not drawn yet), {s_trimChained} waited for a swap around them, {PlacedWaterField.RegenSkipped} heightmap regenerate(s) skipped, {PlacedWaterField.RegenFired} fired (0.2.1346)");
                FiresLogger.LogDebug($"[WaterTools] rebuild triggers last 10 s: remeshes with unchanged ground skipped {VoxelWaterSheetMesh.SkippedSameGround}, fired {VoxelWaterSheetMesh.FiredWindow}; mid-swap holds {s_hHeld}, zone flushes {s_hFlushes} ({s_hGaveUp} gave up or left), {s_hRan} key(s) run, flush total {s_hFlushMs:0.00} ms worst {s_hFlushWorst:0.00} ms (0.2.1344)");
            }
            PlacedWaterField.BedHits = PlacedWaterField.BedMisses = 0; PlacedWaterField.BedMissWorstMs = 0; s_rawReused = 0;   // 0.2.1347
            System.Array.Clear(s_pStageWorst, 0, s_pStageWorst.Length); System.Array.Clear(s_whyCount, 0, s_whyCount.Length); s_why.Clear(); s_quietNow = s_quietHeld = s_quietMaxed = 0;
            s_fanKept = s_trimUndrawn = s_trimChained = 0; PlacedWaterField.RegenSkipped = PlacedWaterField.RegenFired = 0;   // 0.2.1346
            s_pGround0 = g; s_pBed0 = b; s_pCap0 = c; s_pUpload0 = u;
            s_pColumns = s_pCorners = s_pBuilder = s_pSkirt = s_pPublish = s_pSliceWorst = 0; s_pSlices = 0; s_sliceAborted = 0;
            VoxelWaterSheetMesh.SkippedSameGround = 0; VoxelWaterSheetMesh.FiredWindow = 0;
            s_hHeld = s_hFlushes = s_hGaveUp = s_hRan = 0; s_hFlushMs = s_hFlushWorst = 0;
        }

        private static void DrainLater()
        {
            if (s_later.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            s_laterDue.Clear();
            foreach (var kv in s_later)
                if (kv.Value <= now) { s_laterDue.Add(kv.Key); if (s_laterDue.Count >= LaterPerTick) break; }
            foreach (var key in s_laterDue)
            {
                s_later.Remove(key); s_laterFirst.Remove(key);   // 0.2.1347
                if (HoldOn && s_wanted.Contains(key) && s_drawn.ContainsKey(key))   // 0.2.1344: a neighbour's rebuild over a zone mid-swap waits too; a first show never
                {
                    var zone = ZoneOf(key);
                    if (VoxelZoneStreamer.ZoneMidSwap(zone)) { HoldFor(zone).Sheets.Add(key); continue; }
                    if (TrimOn && ReachedByMidSwap(key, out var around)) { HoldFor(around).Sheets.Add(key); s_trimChained++; continue; }   // 0.2.1346
                }
                if (s_wanted.Contains(key) && s_dirty.Add(key)) ReasonMeshed++;
            }
        }

        private static readonly float[] s_levels = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
        private static readonly float[] s_ground = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];

        // 0.2.1204 (lead, fly56: water tick 57.5 s / 3871 builds = 14.9 ms a build, fly54 8.0): 1198's clamp and 1202's skirt asked
        // PlacedWaterField.TrySurface again for every vertex (~750 a build) at the corners the builder had just asked. One build reads
        // one band, so its answers are kept per (x, z) to 1 mm for that build: the same values, asked once.
        private struct SurfHit { public bool Ok; public float Level, Bed; }
        private static readonly Dictionary<long, SurfHit> s_surf = new Dictionary<long, SurfHit>(4096);
        internal static long SurfAsked, SurfReused;
        // 0.2.1347: the column pass already has this column's canonical level for this band: the surface from it, the same answer Surf gives, one Canonical fewer.
        private static int s_rawReused;
        private static bool SurfFromRaw(float x, float z, float raw, out float level, out float bed)
        {
            long k = ((long)Mathf.RoundToInt(x * 1000f) << 32) ^ (uint)Mathf.RoundToInt(z * 1000f);
            if (s_surf.TryGetValue(k, out var h)) { SurfReused++; level = h.Level; bed = h.Bed; return h.Ok; }
            SurfAsked++; s_rawReused++;
            bool ok = PlacedWaterField.TrySurfaceFromRaw(x, z, raw, PlacedWaterField.Consumer.Sheet, out level, out bed);
            s_surf[k] = new SurfHit { Ok = ok, Level = level, Bed = bed };
            return ok;
        }

        private static bool Surf(float x, float z, float yLow, float yHigh, out float level, out float bed)
        {
            long k = ((long)Mathf.RoundToInt(x * 1000f) << 32) ^ (uint)Mathf.RoundToInt(z * 1000f);   // 1 mm: the builder and the vertices share corner coordinates
            if (s_surf.TryGetValue(k, out var h)) { SurfReused++; level = h.Level; bed = h.Bed; return h.Ok; }
            SurfAsked++;
            bool ok = PlacedWaterField.TrySurface(x, z, yLow, yHigh, PlacedWaterField.Consumer.Sheet, out level, out bed);
            s_surf[k] = new SurfHit { Ok = ok, Level = level, Bed = bed };
            return ok;
        }

        // 0.2.1344 (r22: worst build 80.7 ms against a 4 ms budget that was only checked BETWEEN builds): the state of the one build in progress. s_levels /
        // s_ground / s_surf stay its scratch until it publishes. A sliced build fills a spare mesh and swaps it into the filter in its last
        // step, so nothing half-built is ever drawn. Not moved to a worker: IsMoppedAt, the cap walk and the bed reads are main-thread
        // (off the main thread TrySurface answers uncapped), so slicing is what keeps one surface.
        private static bool SliceOn => FiresVoxelConfig.WaterSheetSliced != null && FiresVoxelConfig.WaterSheetSliced.Value;
        private static readonly HashSet<Vector3Int> s_live = new HashSet<Vector3Int>();
        private static readonly Dictionary<Vector3Int, float> s_builtAt = new Dictionary<Vector3Int, float>();
        private static readonly Dictionary<Vector3Int, int> s_repeat = new Dictionary<Vector3Int, int>();
        private static void MarkLive(Vector3Int k) { if (SliceOn) s_live.Add(k); }
        private static bool s_sliceActive, s_sliceFirstShow, s_sliceProof, s_sliceAny;
        private static Vector3Int s_sliceKey;
        private static int s_sliceStage, s_sliceCursor, s_sliceAborted;
        private static double s_sliceMs;
        private static Mesh s_spareMesh;
        private static Vector3[] s_sliceVerts;
        private static Vector3 s_bOrigin;
        private static float s_bLow, s_bHigh, s_bSLo, s_bSHi, s_bBLo, s_bBHi;
        private static int s_bWet, s_bKept, s_bLowered, s_bDropped, s_bPonds;
        private static WaterSurfaceProvider s_bProvider;
        private const int CornerReach = 3;   // the builder reads corners out to 3 past the chunk (VoxelWaterSheetMesh)

        private static void BeginBuild(Vector3Int key)
        {
            s_surf.Clear();   // 0.2.1204: answers are per build (one band)
            s_bOrigin = new Vector3(key.x * VoxelChunk.CELLS, key.y * VoxelChunk.CELLS, key.z * VoxelChunk.CELLS);
            s_bLow = s_bOrigin.y - BandBelow; s_bHigh = s_bOrigin.y + BandAbove;
            s_bWet = s_bKept = s_bLowered = s_bDropped = s_bPonds = 0;
            s_bSLo = float.MaxValue; s_bSHi = float.MinValue; s_bBLo = float.MaxValue; s_bBHi = float.MinValue;
            if (s_bProvider == null)
                s_bProvider = (float x, float z, out float level, out float depth) =>
                {
                    depth = 0f;
                    if (!Surf(x, z, s_bLow, s_bHigh, out level, out float bed)) return false;
                    if (!float.IsNaN(bed)) depth = Mathf.Max(0f, level - bed);
                    return true;
                };
        }

        // One column of the level / bed grid, by its index.
        private static void BuildColumn(int i)
        {
            int S = VoxelChunk.SAMPLES;
            float x = s_bOrigin.x + (i % S) - 1, z = s_bOrigin.z + (i / S) - 1;
            s_levels[i] = 0f;
            s_ground[i] = 0f;
            // 0.2.1091: the column from PlacedWaterField, the one placed-water surface every consumer reads (the 1090 rim
            // cap moved there). The raw level only decides "mopped" and the counts.
            float raw = PlacedWaterField.Canonical(x, z, s_bLow, s_bHigh, true);   // 0.2.1094: one surface per floor, as TrySurface
            if (float.IsNaN(raw)) return;
            if (VoxelWorld.IsMoppedAt(x, raw, z) || VoxelWorld.IsMoppedAt(x, raw + VoxelChunk.CELLS, z)) { s_levels[i] = -1f; return; }   // 0.2.1097: + the layer above
            bool once = FiresVoxelConfig.WaterColumnAsksOnce != null && FiresVoxelConfig.WaterColumnAsksOnce.Value;   // 0.2.1347
            float level, bed;
            if (once ? !SurfFromRaw(x, z, raw, out level, out bed) : !Surf(x, z, s_bLow, s_bHigh, out level, out bed)) { s_bDropped++; return; }
            if (WaterSheets.PondCovers(x, z, raw)) s_bPonds++;
            if (level < raw - 0.001f) s_bLowered++; else s_bKept++;
            if (!float.IsNaN(bed))
            {
                s_ground[i] = bed;
                if (bed < level)
                {
                    s_levels[i] = level; s_bWet++;
                    s_bSLo = Mathf.Min(s_bSLo, level); s_bSHi = Mathf.Max(s_bSHi, level); s_bBLo = Mathf.Min(s_bBLo, bed); s_bBHi = Mathf.Max(s_bBHi, bed);
                }
            }
        }

        private static double MsSince(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static void BuildChunk(Vector3Int key)
        {
            BeginBuild(key);
            int S = VoxelChunk.SAMPLES;
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < S * S; i++) BuildColumn(i);
            s_pColumns += MsSince(p0);
            if (FiresLogger.DebugEnabled && s_bWet > 0 && Time.unscaledTime >= s_smoothLogAt) NoteSmoothed(key, S);
            Mesh mesh = MeshFor(key, s_bOrigin);
            p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool any = s_bWet > 0 && VoxelWaterSheetMesh.Build(s_levels, s_ground, s_bOrigin, s_bProvider, mesh);
            s_pBuilder += MsSince(p0);
            FinishBuild(key, mesh, any);
        }

        private static void StartSlice(Vector3Int key, bool firstShow)
        {
            BeginBuild(key);
            s_sliceActive = true; s_sliceKey = key; s_sliceFirstShow = firstShow;
            s_sliceStage = 0; s_sliceCursor = 0; s_sliceMs = 0; s_sliceVerts = null;
            if (s_spareMesh == null) s_spareMesh = new Mesh { name = "placed_water_sheet" };
            if (!s_sliceProof) { s_sliceProof = true; FiresLogger.LogInfo("[WaterTools] placed-water sheet builds run in slices inside the frame budget and go live whole in one frame (0.2.1344)"); }
        }

        // True = published. False = out of budget, the next frame goes on. Stages: 0 columns, 1 corner answers, 2 builder into the spare mesh,
        // 3 vertex + skirt answers, 4 snap + swap + bind. Stages 1 and 3 only fill s_surf, so the builder and the snap in stages 2 and 4 ask nothing new.
        private static bool ContinueSlice(long deadline)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool done = RunSlice(deadline);
            double ms = MsSince(t0);
            s_sliceMs += ms; s_pSlices++; if (ms > s_pSliceWorst) s_pSliceWorst = ms;
            int stage = done ? 4 : Mathf.Clamp(s_sliceStage, 0, 4);   // 0.2.1347: the stage it stopped in holds nearly all of the slice
            if (ms > s_pStageWorst[stage]) s_pStageWorst[stage] = ms;
            return done;
        }

        private static readonly double[] s_pStageWorst = new double[5];   // 0.2.1347
        private static bool s_sliceFineProof;
        private static bool RunSlice(long deadline)
        {
            int S = VoxelChunk.SAMPLES;
            // 0.2.1347 (1345 log: worst slice 58 ms against a 4 ms budget): the columns already stop on every column, but 16 corners or 8 skirt vertices ran between checks, and one
            // uncached ask can run four bank walks (each up to 7.6 ms in that log). Now every ask.
            bool fine = FiresVoxelConfig.WaterSliceEveryAsk != null && FiresVoxelConfig.WaterSliceEveryAsk.Value;
            if (fine && !s_sliceFineProof) { s_sliceFineProof = true; FiresLogger.LogInfo("[WaterTools] a sliced sheet build checks its budget on every corner and skirt ask (0.2.1347)"); }
            int cornerMask = fine ? 0 : 15, skirtMask = fine ? 0 : 7;
            if (s_sliceStage == 0)
            {
                long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int n = S * S, did = 0;
                while (s_sliceCursor < n)
                {
                    BuildColumn(s_sliceCursor++);
                    if (++did >= 1 && s_sliceCursor < n && System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) { s_pColumns += MsSince(p0); return false; }
                }
                s_pColumns += MsSince(p0);
                s_sliceStage = 1; s_sliceCursor = 0;
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) return false;
            }
            if (s_sliceStage == 1)
            {
                if (s_bWet > 0)
                {
                    long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    int side = VoxelChunk.CELLS + 1 + 2 * CornerReach, n = side * side;
                    while (s_sliceCursor < n)
                    {
                        int i = s_sliceCursor++;
                        Surf(s_bOrigin.x + (i % side) - CornerReach, s_bOrigin.z + (i / side) - CornerReach, s_bLow, s_bHigh, out _, out _);
                        if ((i & cornerMask) == cornerMask && s_sliceCursor < n && System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) { s_pCorners += MsSince(p0); return false; }
                    }
                    s_pCorners += MsSince(p0);
                }
                s_sliceStage = 2;
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) return false;
            }
            if (s_sliceStage == 2)
            {
                if (FiresLogger.DebugEnabled && s_bWet > 0 && Time.unscaledTime >= s_smoothLogAt) NoteSmoothed(s_sliceKey, S);
                long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (s_bWet > 0) s_sliceAny = VoxelWaterSheetMesh.Build(s_levels, s_ground, s_bOrigin, s_bProvider, s_spareMesh);
                else { s_sliceAny = false; s_spareMesh.Clear(false); }
                s_pBuilder += MsSince(p0);
                s_sliceStage = 3; s_sliceCursor = 0;
                s_sliceVerts = s_sliceAny && WaterStateField.SeamlessSheets ? s_spareMesh.vertices : null;
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) return false;
            }
            if (s_sliceStage == 3)
            {
                if (s_sliceVerts != null)
                {
                    long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (s_sliceCursor < s_sliceVerts.Length)
                    {
                        Vector3 v = s_sliceVerts[s_sliceCursor++];
                        if (!Surf(v.x, v.z, s_bLow, s_bHigh, out _, out _)) SkirtHeight(v.x, v.z, s_bLow, s_bHigh, out _);
                        if ((s_sliceCursor & skirtMask) == 0 && s_sliceCursor < s_sliceVerts.Length && System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) { s_pSkirt += MsSince(p0); return false; }
                    }
                    s_pSkirt += MsSince(p0);
                }
                s_sliceVerts = null;
                s_sliceStage = 4;
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) return false;
            }
            // Stage 4: the swap. MeshFor first, so a new chunk has its object; the old mesh becomes the next spare.
            Mesh built = s_spareMesh;
            Mesh old = MeshFor(s_sliceKey, s_bOrigin);
            if (s_drawn.TryGetValue(s_sliceKey, out var go0) && go0 != null)
            {
                FinishSwap(go0, built);
                s_spareMesh = old;
            }
            FinishBuild(s_sliceKey, built, s_sliceAny);
            return true;
        }

        // The snap runs on the spare mesh just before FinishBuild reads it: FinishBuild does the snap, so the filter swap and the snap land in this one frame.
        private static void FinishSwap(GameObject go, Mesh built)
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = built;
        }

        private static void FinishBuild(Vector3Int key, Mesh mesh, bool any)
        {
            Vector3 origin = s_bOrigin;
            float yLow = s_bLow, yHigh = s_bHigh, sLo = s_bSLo, sHi = s_bSHi, bLo = s_bBLo, bHi = s_bBHi;
            int wet = s_bWet, kept = s_bKept, lowered = s_bLowered, dropped = s_bDropped, ponds = s_bPonds;
            if (WaterStateField.SeamlessSheets)   // 0.2.1198; 0.2.1201: the border signature decides the neighbour rebuild
            {
                long ps = System.Diagnostics.Stopwatch.GetTimestamp();
                uint sig = any ? SnapToSurface(key, mesh, yLow, yHigh) : 0u;
                s_pSkirt += MsSince(ps);
                bool changed = !s_borderSig.TryGetValue(key, out uint was) || was != sig;
                s_borderSig[key] = sig;
                if (s_spreadOnBuild.Remove(key)) { if (changed) DirtyNeighbours(key); else s_borderSame++; }
            }
            long pb = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1344
            if (s_drawn.TryGetValue(key, out var go) && go != null) go.SetActive(any);
            // 0.2.1097: the per-column ice / lava / crust state on the sheet (Core FiresInlandWater draws it; WaterStateField)
            // 0.2.1313: + the per-column levels: the texture and the placed ice collider (PlacedWaterSolids) read ONE column grid.
            if (any && go != null) WaterStateField.Bind(go.GetComponent<MeshRenderer>(), origin, sLo <= sHi ? (sLo + sHi) * 0.5f : origin.y, "placed", s_levels);
            if (FiresLogger.DebugEnabled && wet > 0 && Time.unscaledTime >= s_rimLogAt)
            {
                s_rimLogAt = Time.unscaledTime + 2f;
                if (FatDiag.WaterField) FiresLogger.LogDebug($"[WaterField] chunk ({key.x},{key.z}) y {key.y}: {wet} column(s), surface {sLo:0.00}..{sHi:0.00}, bed {bLo:0.00}..{bHi:0.00}, capped {lowered} (kept {kept}, dropped dry {dropped}), ponds {ponds} - the drawn sheet, from PlacedWaterField");
                if (dropped > 0) if (FatDiag.WaterField) FiresLogger.LogDebug($"[WaterEdge] chunk ({key.x},{key.z}): cells dropped at banks {dropped} (reason: the ground rises to or above the smooth capped level - the shoreline; 0.2.1100 no longer drops a column by its own cap)");
            }
            if (any && !s_loggedFirst)
            {
                s_loggedFirst = true;
                FiresLogger.LogDebug($"[FiresWater] placed water drawn with the skyland sheet builder (first: chunk {key}, {wet} wet column(s)); the disks' combiners build no grid mesh (0.2.1091).");
            }
            s_pPublish += MsSince(pb);   // 0.2.1344
        }

        // 0.2.1198 (lead, Fire: a crack in the stream ice at (300,875); probe v2: 4 border steps up to 0.40 m, 17 unpaired border
        // vertices, 81 slope vertices up to 0.40 m off the water): a sheet chunk reads its border corners through a 3 m halo, so
        // when one chunk rebuilds alone (its voxel bed remeshed, a tool ring) its drawn neighbours kept the old border. They now
        // rebuild with it (same layer, drawn ones only; a neighbour's own rebuild does not spread further).
        private static int DirtyNeighbours(Vector3Int key)
        {
            int n = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var k = new Vector3Int(key.x + dx, key.y, key.z + dz);
                    if (!s_wanted.Contains(k) || !s_drawn.ContainsKey(k)) continue;
                    Why(k, W_Border);   // 0.2.1347: through the quiet window; a border only moves its neighbours' seams, and the later build fixes them
                    if (QuietOn) { if (!s_dirty.Contains(k)) { AskGround(k); n++; s_neighbourQueued++; } }
                    else if (s_dirty.Add(k)) { n++; s_neighbourQueued++; }
                }
            return n;
        }

        // 0.2.1198: the smoothed corner field (VoxelChunkView.BuildSheet) leaves slope vertices off the water and lets two chunks
        // settle a shared border differently. Every top vertex is now held to the placed-water surface at its own XZ (TrySurface, the one
        // function every consumer reads), so a vertex two chunks share gets one height on both sides. Vertices with no water under
        // them, or hanging more than SnapReach below it (the bank skirt), stay where the builder put them.
        // 0.2.1201 (lead, fly55 vs fly54: water tick 20.1 s -> 107.7 s, 2511 -> 10110 sheet chunk builds): 1198 rebuilt all 8 drawn
        // neighbours on EVERY voxel remesh / tool mark. A neighbour only reads this chunk's border, so it now rebuilds only when this
        // chunk's border vertices (XZ + height, 1 cm) differ from its last build.
        private static readonly HashSet<Vector3Int> s_spreadOnBuild = new HashSet<Vector3Int>();
        private static readonly Dictionary<Vector3Int, uint> s_borderSig = new Dictionary<Vector3Int, uint>();
        private static int s_borderSame;
        private const float SnapReach = 0.75f, RaiseCap = 0.03f;   // the smoothing may lift the sheet at most 3 cm over the water, never below it
        private static int s_snapRaised, s_neighbourQueued, s_snapChunks, s_snapMoved, s_snapKept;
        private static float s_snapWorst, s_snapLogAt;
        // 0.2.1202: the shared skirt height for a dry border vertex: the mean wet placed-water surface on a 1 m grid within SkirtReach
        // (the builder's skirt is the mean of wet neighbours, 3 rings). World coordinates only, so both chunks get one answer.
        private const int SkirtReach = 3;
        private static int s_skirtMoved, s_deepRaised, s_skirtRaised, s_welded;
        private static readonly System.Collections.Generic.Dictionary<long, float> s_weld = new System.Collections.Generic.Dictionary<long, float>();   // 0.2.1213, main thread
        private static bool SkirtHeight(float x, float z, float yLow, float yHigh, out float h)
        {
            float sum = 0f; int n = 0;
            for (int dz = -SkirtReach; dz <= SkirtReach; dz++)
                for (int dx = -SkirtReach; dx <= SkirtReach; dx++)
                    if (Surf(x + dx, z + dz, yLow, yHigh, out float l, out float bed) && (float.IsNaN(bed) || bed < l)) { sum += l; n++; }
            h = n > 0 ? sum / n : 0f;
            return n > 0;
        }

        private static uint SnapToSurface(Vector3Int key, Mesh mesh, float yLow, float yHigh)
        {
            var v = mesh.vertices;
            int moved = 0;
            uint sig = 2166136261u;
            for (int i = 0; i < v.Length; i++)
            {
                if (!Surf(v[i].x, v[i].z, yLow, yHigh, out float level, out _))
                {
                    // 0.2.1202 (ice55: 2 border steps up to 0.40 m + 14 loners, all bank-skirt vertices with no water under them): the
                    // builder dilates the sheet 3 cells into the dry bank, but each chunk sees only a 1-column apron of its neighbour,
                    // so two chunks extend one border skirt differently (49.00 vs 48.60). A DRY border vertex now takes one shared
                    // skirt height, the mean placed-water surface within SkirtReach (the same world function on both sides).
                    float sx = v[i].x, sz = v[i].z, sc = VoxelChunk.CELLS;
                    bool dryBorder = Mathf.Abs(sx - Mathf.Round(sx / sc) * sc) < 0.02f || Mathf.Abs(sz - Mathf.Round(sz / sc) * sc) < 0.02f;
                    if (dryBorder && SkirtHeight(sx, sz, yLow, yHigh, out float skirt) && Mathf.Abs(skirt - v[i].y) <= SnapReach)
                    {
                        if (Mathf.Abs(skirt - v[i].y) > 0.001f) { moved++; s_skirtMoved++; s_snapWorst = Mathf.Max(s_snapWorst, Mathf.Abs(skirt - v[i].y)); v[i].y = skirt; }
                        sig = (sig ^ (uint)Mathf.RoundToInt(sx * 100f)) * 16777619u; sig = (sig ^ (uint)Mathf.RoundToInt(sz * 100f)) * 16777619u; sig = (sig ^ (uint)Mathf.RoundToInt(skirt * 100f)) * 16777619u;
                        continue;
                    }
                    // 0.2.1211 (iceshot77 on 1208: the wedge at 306,876 unchanged; mesh79 there listed fires_placed_water_9_1_27 vertices at
                    // 48.86 / 48.89 / 48.99 on the SAME x z as 49.00 ones - inside SnapReach, so the old clamp would have fixed them had the
                    // water field called them wet: they are dry skirt vertices, and one sat lower than its neighbours' -> a fold at the
                    // triangle edges). An INTERIOR dry vertex below the shared skirt height is raised to it (never lowered; Fire's rule).
                    if (!dryBorder && SkirtHeight(sx, sz, yLow, yHigh, out float under) && v[i].y < under - 0.001f && under - v[i].y <= SnapReach)
                    {
                        moved++; s_skirtRaised++; s_snapWorst = Mathf.Max(s_snapWorst, under - v[i].y); v[i].y = under;
                        continue;
                    }
                    s_snapKept++; continue;
                }
                float d = level - v[i].y;
                // 0.2.1208 (mesh79 fires_mesh_at 306 876 = the wedge pixel in Fire's framing: fires_placed_water_9_1_27 had vertices at 48.11..48.99
                // over WET water at 49.00 - the ice dropped up to 0.9 m; a vertex more than SnapReach under the water was left as the builder
                // made it). Fire: "it should not drop it" - a wet vertex under the water is now raised to it however far; above stays as before.
                if (d > SnapReach) s_deepRaised++;
                if (d < -SnapReach) { s_snapKept++; continue; }
                // Fire 10-28: "it can raise the ice a tiny bit ... but it should not drop it": the smoothed height is kept inside
                // [water, water + RaiseCap]; a chunk-border vertex takes the water height itself, so both chunks give it one height.
                float x = v[i].x, z = v[i].z, c = VoxelChunk.CELLS;
                bool border = Mathf.Abs(x - Mathf.Round(x / c) * c) < 0.02f || Mathf.Abs(z - Mathf.Round(z / c) * c) < 0.02f;
                float y = border ? level : Mathf.Clamp(v[i].y, level, level + RaiseCap);
                if (y != v[i].y) { moved++; if (v[i].y < level) s_snapRaised++; s_snapWorst = Mathf.Max(s_snapWorst, Mathf.Abs(y - v[i].y)); v[i].y = y; }
                if (border) { sig = (sig ^ (uint)Mathf.RoundToInt(x * 100f)) * 16777619u; sig = (sig ^ (uint)Mathf.RoundToInt(z * 100f)) * 16777619u; sig = (sig ^ (uint)Mathf.RoundToInt(y * 100f)) * 16777619u; }
            }
            // 0.2.1213 (iceshot85 on 1211: the wedge block gone, a thin diagonal line left along a triangle edge; mesh79 at 306,876
            // listed copies of ONE x z at 48.86 / 48.89 / 48.99 / 49.00): the builder gives each triangle its own vertex copies, and the
            // passes above judge every copy by its own height (a copy over the skirt height is kept, one under it is raised), so two
            // copies of a corner could still part by a few cm and open a crack along the edge. Every copy of an x z now takes the
            // HIGHEST height among them (never lowered; Fire's rule), so the triangles meeting there share one corner again.
            s_weld.Clear();
            for (int i = 0; i < v.Length; i++)
            {
                long k = ((long)Mathf.RoundToInt(v[i].x * 1000f) << 32) ^ (uint)Mathf.RoundToInt(v[i].z * 1000f);
                if (!s_weld.TryGetValue(k, out float top) || v[i].y > top) s_weld[k] = v[i].y;
            }
            for (int i = 0; i < v.Length; i++)
            {
                long k = ((long)Mathf.RoundToInt(v[i].x * 1000f) << 32) ^ (uint)Mathf.RoundToInt(v[i].z * 1000f);
                float top = s_weld[k];
                if (top - v[i].y > 0.0005f && top - v[i].y <= SnapReach) { s_welded++; s_snapWorst = Mathf.Max(s_snapWorst, top - v[i].y); v[i].y = top; moved++; }
            }
            if (moved > 0) { mesh.vertices = v; mesh.RecalculateBounds(); }   // the builder's normals stay (water normals; a per-chunk recalc would split them at borders)
            s_snapChunks++; s_snapMoved += moved;
            if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_snapLogAt)
            {
                s_snapLogAt = Time.unscaledTime + 10f;
                FiresLogger.LogDebug($"[WaterIce] seamless sheets: {s_snapChunks} chunk build(s), {s_snapMoved} vertex(es) clamped to water..water+{RaiseCap * 100f:0} cm ({s_snapRaised} were below the water; worst move {s_snapWorst:0.00} m; border vertices = the water height), {s_skirtMoved} dry border skirt vertex(es) set to the shared skirt height (0.2.1202), {s_deepRaised} vertex(es) more than {SnapReach:0.00} m under the water raised to it (0.2.1208), {s_skirtRaised} interior dry skirt vertex(es) raised to the shared skirt height (0.2.1211), {s_welded} vertex copies raised to the highest copy at their x z (0.2.1213), {s_snapKept} left (dry or bank skirt), water lookups {SurfAsked} asked / {SurfReused} reused this window (0.2.1204), {s_neighbourQueued} neighbour rebuild(s) queued (only when a rebuilt chunk's border moved: {s_borderSame} rebuild(s) kept their border, no neighbours; 0.2.1201); last chunk ({key.x},{key.y},{key.z}) (0.2.1198)");
                s_snapChunks = 0; s_snapMoved = 0; s_snapKept = 0; s_neighbourQueued = 0; s_borderSame = 0; s_skirtMoved = 0; SurfAsked = 0; SurfReused = 0; s_snapRaised = 0; s_snapWorst = 0f;
            }
            return sig;
        }

        // 0.2.1192: the column surfaces after the continuous disk blend (WaterSheets.FreshSurfaceAt): distinct levels (5 cm bins) and
        // the largest step between neighbouring wet columns. Before 1192 the captures showed two plateaus ~0.40 m apart per sheet and
        // 2-4 m steps on streams.
        private static float s_smoothLogAt;
        private static readonly HashSet<int> s_levelBins = new HashSet<int>();
        private static void NoteSmoothed(Vector3Int key, int S)
        {
            s_smoothLogAt = Time.unscaledTime + 5f;
            s_levelBins.Clear();
            float maxStep = 0f;
            for (int z = 0; z < S; z++)
                for (int x = 0; x < S; x++)
                {
                    float a = s_levels[x + S * z];
                    if (a <= 0f) continue;
                    s_levelBins.Add(Mathf.RoundToInt(a * 20f));
                    if (x + 1 < S) { float b = s_levels[x + 1 + S * z]; if (b > 0f) maxStep = Mathf.Max(maxStep, Mathf.Abs(a - b)); }
                    if (z + 1 < S) { float b = s_levels[x + S * (z + 1)]; if (b > 0f) maxStep = Mathf.Max(maxStep, Mathf.Abs(a - b)); }
                }
            FiresLogger.LogDebug($"[WaterIce] sheet smoothed: chunk ({key.x},{key.y},{key.z}), {s_levelBins.Count} distinct level(s) (5 cm bins), max neighbour step {maxStep:0.00} m (0.2.1192: stacked disks fade continuously; before: two plateaus ~0.40 m, streams 2-4 m)");
        }

        private static float s_rimLogAt;   // 0.2.1091: the [WaterField] chunk line; the 1090 rim cap moved to PlacedWaterField

        private static Mesh MeshFor(Vector3Int key, Vector3 origin)
        {
            if (!s_drawn.TryGetValue(key, out var go) || go == null)
            {
                go = new GameObject($"fires_placed_water_{key.x}_{key.y}_{key.z}");
                Object.DontDestroyOnLoad(go);
                // At the world origin. The builder's vertices are WORLD-space (VoxelChunkView's convention: the view
                // sits at 0,0,0); placed at the chunk origin, every placed sheet would draw offset by its own
                // coordinates, far from the water.
                go.transform.position = Vector3.zero;
                go.AddComponent<MeshFilter>().sharedMesh = new Mesh { name = "placed_water_sheet" };
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                s_drawn[key] = go;
                ZoneReveal.HoldRenderer(ZoneOf(key), mr);   // 0.2.1346: a first show waits for its zone's swap
            }
            var renderer = go.GetComponent<MeshRenderer>();
            // 0.2.1094 ([seasons], RenderDoc fat1089: some inland sheets bound UnityBlackCube = no sky reflection): blend probes AND
            // the skybox (a sheet outside every probe falls back to the sky, never black), anchored on its own bounds, as the
            // generated sheets already do (VoxelChunkView.cs:779).
            if (renderer != null && renderer.reflectionProbeUsage != UnityEngine.Rendering.ReflectionProbeUsage.BlendProbesAndSkybox)
            {
                s_probeFixedFrom[(int)renderer.reflectionProbeUsage]++;
                renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbesAndSkybox;
                renderer.probeAnchor = null;
                s_probeFixed++;
                if (Time.unscaledTime >= s_probeLogAt)
                {
                    s_probeLogAt = Time.unscaledTime + 30f;
                    FiresLogger.LogInfo($"[FiresWater] sheets: {s_drawn.Count} placed renderer(s), probe usage fixed on {s_probeFixed} (were Off {s_probeFixedFrom[0]}, BlendProbes {s_probeFixedFrom[1]}, Simple {s_probeFixedFrom[3]}) -> BlendProbesAndSkybox");
                }
            }
            var mat = VoxelChunkView.ResolveSheetMaterial();
            if (renderer != null && mat != null && renderer.sharedMaterial != mat) renderer.sharedMaterial = mat;
            SayMaterial("placed water sheet", renderer);
            ToolWaterStencil.OnSheet(renderer, key);   // 0.2.1068: stable draw order + the stencil proof toggle
            return go.GetComponent<MeshFilter>().sharedMesh;
        }

        // 0.2.1086 (lead, Fire: placed water "see-through, colourless"): one line per (sheet kind, shader) saying which material draws it.
        // The sheets wear VoxelChunkView.ResolveSheetMaterial (Custom/FiresInlandWater), tuned by FiresInlandWaterConfig; FTS never reaches them.
        private static readonly HashSet<string> s_saidMat = new HashSet<string>();
        private static readonly int[] s_probeFixedFrom = new int[4];
        private static int s_probeFixed;
        private static float s_probeLogAt;
        internal static void SayMaterial(string what, Renderer r)
        {
            var m = r != null ? r.sharedMaterial : null;
            string shader = m != null && m.shader != null ? m.shader.name : "no material";
            if (!s_saidMat.Add(what + "|" + shader)) return;
            FiresLogger.LogInfo($"[Water] {what} '{r?.name}' uses {shader} (material '{m?.name}'), driven by FTS: no (FiresInlandWaterConfig)");
        }

        // The sheet renderers drawn now (ToolWaterStencil's toggle and status).
        internal static IEnumerable<MeshRenderer> Renderers()
        {
            foreach (var go in s_drawn.Values)
            {
                var r = go != null ? go.GetComponent<MeshRenderer>() : null;
                if (r != null) yield return r;
            }
        }

        // 0.2.1191: the first-show delay per chunk (Debug, at most one line a second) and a 30 s summary.
        private static void NoteFirstShow(Vector3Int key)
        {
            float now = Time.realtimeSinceStartup;
            if (!s_wantedAt.TryGetValue(key, out float at)) return;
            s_wantedAt.Remove(key);
            double ms = (now - at) * 1000.0;
            s_showN++; s_showSumMs += ms; if (ms > s_showMaxMs) s_showMaxMs = ms;
            if (now >= s_showOneAt)
            {
                s_showOneAt = now + 1f;
                FiresLogger.LogDebug($"[WaterShow] zone ({Mathf.FloorToInt(key.x * VoxelChunk.CELLS / 64f)},{Mathf.FloorToInt(key.z * VoxelChunk.CELLS / 64f)}) chunk {key}: water shown {ms:0} ms after the chunk was wanted (path: placed sheet, first-show priority; 0.2.1191)");
            }
            if (now >= s_showLogAt)
            {
                if (s_showLogAt > 0f && s_showN > 0)
                    FiresLogger.LogDebug($"[WaterShow] 30 s: {s_showN} placed-water chunk(s) first shown, avg {s_showSumMs / s_showN:0} ms / max {s_showMaxMs:0} ms after wanted (first-show budget {FirstShowBudgetMs:0} ms a frame, re-builds {BuildBudgetMs:0} ms; 0.2.1191)");
                s_showLogAt = now + 30f; s_showN = 0; s_showSumMs = 0; s_showMaxMs = 0;
            }
        }

        // 0.2.1197 PROBE ONLY (Fire 10-28 shot 284 near (310,873): "ice meshes still break a bit in some spots, we need them to stitch
        // together and smooth on slopes"). The 1195 tile border probe read the COLLIDER tiles (PlacedWaterSolids) and found the two
        // tiles reading the same water (24/24 lines zero). The ice Fire sees is these sheets (Core FiresInlandWater draws the ice state
        // on them, WaterStateField.Bind). Once a minute near the player: every drawn sheet chunk within ProbeReach, the RENDER mesh's
        // top vertices (world space, highest per position), (1) at chunk borders: matched across the two chunks by XZ (2 cm), the
        // height step between them, and border vertices whose drawn neighbour chunk has no vertex there; (2) every top vertex against
        // the placed-water surface at its XZ (TrySurface): how far the drawn sheet misses the water on slopes. No logic change.
        private const float ProbeReach = 24f;
        private static float s_meshProbeAt;
        internal static void MeshSeamProbe(Vector3 p)
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_meshProbeAt) return;
            s_meshProbeAt = Time.unscaledTime + 60f;
            const float C = VoxelChunk.CELLS;
            var keys = new List<Vector3Int>();
            foreach (var kv in s_drawn)
            {
                if (kv.Value == null || !kv.Value.activeSelf) continue;
                float x0 = kv.Key.x * C, z0 = kv.Key.z * C;
                float dx = Mathf.Max(0f, Mathf.Max(x0 - p.x, p.x - (x0 + C))), dz = Mathf.Max(0f, Mathf.Max(z0 - p.z, p.z - (z0 + C)));
                if (dx * dx + dz * dz <= ProbeReach * ProbeReach) keys.Add(kv.Key);
            }
            if (keys.Count == 0) return;
            var drawnXZ = new HashSet<Vector2Int>();
            foreach (var k in keys) drawnXZ.Add(new Vector2Int(k.x, k.z));
            // top[xzKey] per chunk index: the highest vertex there (the skirt past the shore hangs below the same XZ)
            var tops = new List<Dictionary<long, Vector3>>();
            int verts = 0;
            foreach (var k in keys)
            {
                var top = new Dictionary<long, Vector3>();
                var mesh = s_drawn[k].GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh != null && mesh.isReadable)
                    foreach (var v in mesh.vertices)
                    {
                        if (Mathf.Abs(v.x - p.x) > ProbeReach || Mathf.Abs(v.z - p.z) > ProbeReach) continue;
                        long xz = ((long)Mathf.RoundToInt(v.x * 50f) << 32) ^ (uint)Mathf.RoundToInt(v.z * 50f);
                        if (!top.TryGetValue(xz, out var o) || v.y > o.y) top[xz] = v;
                        verts++;
                    }
                tops.Add(top);
            }
            int pairs = 0, steps = 0, unmatched = 0, dryEnds = 0, slopeN = 0, slopeMiss = 0, slopeNoWater = 0;
            float worstStep = 0f, sumStep = 0f, worstMiss = 0f; Vector3 stepAt = default, missAt = default;
            var counted = new HashSet<long>();
            var lonerDetail = new List<string>(); var missDetail = new List<string>();   // 0.2.1200
            for (int c = 0; c < keys.Count; c++)
            {
                float x0 = keys[c].x * C, z0 = keys[c].z * C;
                foreach (var kv in tops[c])
                {
                    Vector3 v = kv.Value;
                    // (2) slope: the drawn top against the water surface there
                    if (PlacedWaterField.TrySurface(v.x, v.z, v.y - 2f, v.y + 2f, PlacedWaterField.Consumer.Sheet, out float lvl, out _))
                    {
                        slopeN++;
                        float miss = Mathf.Abs(v.y - lvl);
                        if (miss > 0.05f) { slopeMiss++; if (miss > worstMiss) { worstMiss = miss; missAt = v; } if (missDetail.Count < 8) missDetail.Add(MissDetail(keys, c, v, lvl)); }
                    }
                    else slopeNoWater++;
                    // (1) borders
                    int bx = Mathf.Abs(v.x - x0) < 0.02f ? -1 : Mathf.Abs(v.x - (x0 + C)) < 0.02f ? 1 : 0;
                    int bz = Mathf.Abs(v.z - z0) < 0.02f ? -1 : Mathf.Abs(v.z - (z0 + C)) < 0.02f ? 1 : 0;
                    if (bx == 0 && bz == 0) continue;
                    bool partner = false, neighbourDrawn = false; int nIdx = -1;
                    for (int o = 0; o < keys.Count; o++)
                    {
                        if (o == c || keys[o].y != keys[c].y) continue;
                        int ox = keys[o].x - keys[c].x, oz = keys[o].z - keys[c].z;
                        bool across = (bx != 0 && ox == bx && (oz == 0 || oz == bz)) || (bz != 0 && oz == bz && (ox == 0 || ox == bx));
                        if (!across) continue;
                        neighbourDrawn = true; if (nIdx < 0) nIdx = o;
                        if (!tops[o].TryGetValue(kv.Key, out var w)) continue;
                        partner = true;
                        if (o < c || !counted.Add(kv.Key ^ ((long)o << 56) ^ ((long)c << 48))) continue;
                        pairs++;
                        float d = Mathf.Abs(v.y - w.y); sumStep += d;
                        if (d > 0.01f) { steps++; if (d > worstStep) { worstStep = d; stepAt = v; } }
                    }
                    if (neighbourDrawn && !partner && nIdx >= 0 && DrySkirtEnd(keys[c], keys[nIdx], v)) dryEnds++;   // 0.2.1205: a buried skirt end, not a crack
                    else if (neighbourDrawn && !partner) { unmatched++; if (lonerDetail.Count < 8) lonerDetail.Add(LonerDetail(keys, tops, c, nIdx, v, bx, bz)); }
                }
            }
            string ice(Vector3 at) => at == default ? "-" : WaterStateField.Sample(at.x, at.z, at.y).Ice.ToString("0.00");
            FiresLogger.LogDebug($"[WaterIce] mesh seam probe at ({p.x:0},{p.z:0}): {keys.Count} sheet chunk(s) within {ProbeReach:0} m, {verts} render vertices; "
                + $"chunk borders: {pairs} vertex pair(s) matched, steps > 1 cm {steps} (mean {(pairs > 0 ? sumStep / pairs : 0f):0.000} m, worst {worstStep:0.00} m at ({stepAt.x:0.0},{stepAt.y:0.00},{stepAt.z:0.0}) ice {ice(stepAt)}), "
                + $"border vertices with no partner in the drawn neighbour {unmatched} (+ {dryEnds} dry bank-skirt end(s), no water in either chunk: buried in the bank, not cracks; 0.2.1205); slope check: {slopeN} top vertices over water, off the water surface > 5 cm {slopeMiss} "
                + $"(worst {worstMiss:0.00} m at ({missAt.x:0.0},{missAt.y:0.00},{missAt.z:0.0}) ice {ice(missAt)}), no water under {slopeNoWater} - "
                + "seam cause: non-zero steps/no-partner = chunks disagree at the border; off-surface = the sheet leaves the water on the slope (0.2.1197 probe, no logic change)");
            foreach (var s in lonerDetail) FiresLogger.LogDebug("[WaterIce] seam probe, border vertex with no partner: " + s + " (0.2.1200)");
            foreach (var s in missDetail) FiresLogger.LogDebug("[WaterIce] seam probe, vertex off the water: " + s + " (0.2.1200)");
        }

        // 0.2.1200 (lead, ice54: 5 unpaired border vertices + 3 vertices 5-10 cm off the water left after 1199's clamp): what each one
        // is. A loner: its own chunk's band surface (provider, the clamp's input) and whether it is a skirt vertex (no water or more
        // than SnapReach under it); the neighbour's nearest vertices on the same border line (spacing = T-junction when the neighbour
        // has vertices either side but none here), and the neighbour's own band surface at that XZ. A miss: the probe's surface (v.y
        // +-2 m) vs the chunk's band surface (what the clamp used), and other drawn chunks (other layers) with a top vertex there.
        // 0.2.1205 (lead, ice56: the 14 left were all skirt ends with no water in either band, past the neighbour's sheet end): counted
        // apart from the real loners. The builder keeps a skirt cell only where its own ground is at most 5 cm under it
        // (VoxelChunkView.BuildSheet: skg < v - 0.05 -> dropped), so such an end sits in the bank.
        private static bool DrySkirtEnd(Vector3Int own, Vector3Int neighbour, Vector3 v)
            => !BandSurfaceOk(own, v.x, v.z) && !BandSurfaceOk(neighbour, v.x, v.z);
        private static bool BandSurfaceOk(Vector3Int k, float x, float z) { BandSurface(k, x, z, out bool ok); return ok; }

        private static float BandSurface(Vector3Int k, float x, float z, out bool ok)
        {
            float yLow = k.y * VoxelChunk.CELLS - BandBelow, yHigh = k.y * VoxelChunk.CELLS + BandAbove;
            ok = PlacedWaterField.TrySurface(x, z, yLow, yHigh, PlacedWaterField.Consumer.Sheet, out float level, out _);
            return ok ? level : float.NaN;
        }

        private static string LonerDetail(List<Vector3Int> keys, List<Dictionary<long, Vector3>> tops, int c, int n, Vector3 v, int bx, int bz)
        {
            var kc = keys[c];
            float own = BandSurface(kc, v.x, v.z, out bool ownOk);
            string kind = !ownOk ? "no water in its band (skirt / dry corner)" : (own - v.y > SnapReach ? $"skirt ({own - v.y:0.00} m under the water)" : $"sheet ({v.y - own:+0.00;-0.00} m vs water)");
            if (n < 0) return $"({v.x:0.00},{v.y:0.00},{v.z:0.00}) chunk ({kc.x},{kc.y},{kc.z}) {kind}; no neighbour index";
            var kn = keys[n];
            bool alongZ = bx != 0;   // on an x border the shared line runs along z
            float bestLo = float.MaxValue, bestHi = float.MaxValue; Vector3 lo = default, hi = default; int onLine = 0;
            foreach (var w in tops[n].Values)
            {
                if (alongZ ? Mathf.Abs(w.x - v.x) > 0.02f : Mathf.Abs(w.z - v.z) > 0.02f) continue;
                onLine++;
                float d = alongZ ? w.z - v.z : w.x - v.x;
                if (d < 0f && -d < bestLo) { bestLo = -d; lo = w; }
                if (d > 0f && d < bestHi) { bestHi = d; hi = w; }
            }
            float nb = BandSurface(kn, v.x, v.z, out bool nbOk);
            string shore = "";   // 0.2.1202: the neighbour column dry (bed at or above the water) = the shoreline runs along the border, not a crack
            if (nbOk) { float y0 = kn.y * VoxelChunk.CELLS; if (PlacedWaterField.TrySurface(v.x, v.z, y0 - BandBelow, y0 + BandAbove, PlacedWaterField.Consumer.Sheet, out float nl, out float nbed) && !float.IsNaN(nbed) && nbed >= nl) shore = $"; neighbour column DRY (bed {nbed:0.00} >= water {nl:0.00}) = shoreline on the border, not a crack"; }
            string near = onLine == 0 ? "the neighbour has NO vertex on this border line"
                : $"neighbour on the line: {onLine} vertex(es), nearest {(bestLo < float.MaxValue ? $"-{bestLo:0.00} m (y {lo.y:0.00})" : "none")} / {(bestHi < float.MaxValue ? $"+{bestHi:0.00} m (y {hi.y:0.00})" : "none")}"
                  + (bestLo < float.MaxValue && bestHi < float.MaxValue ? " = T-junction (neighbour edge spans this vertex)" : " = this vertex is past the neighbour's sheet end");
            return $"({v.x:0.00},{v.y:0.00},{v.z:0.00}) chunk ({kc.x},{kc.y},{kc.z}) {kind}; neighbour ({kn.x},{kn.y},{kn.z}): {near}; neighbour band water here {(nbOk ? nb.ToString("0.00") : "none")}{shore}";
        }

        private static string MissDetail(List<Vector3Int> keys, int c, Vector3 v, float probeLevel)
        {
            var kc = keys[c];
            float band = BandSurface(kc, v.x, v.z, out bool ok);
            string clamp = !ok ? "no water in the chunk's band (clamp skipped it)"
                : Mathf.Abs(band - v.y) > SnapReach ? $"band water {band:0.00}, {band - v.y:0.00} m away = outside SnapReach (skirt; clamp skipped it)"
                : $"band water {band:0.00} (clamp range {band:0.00}..{band + RaiseCap:0.00}, vertex {(v.y >= band - 0.001f && v.y <= band + RaiseCap + 0.001f ? "inside" : "OUTSIDE")})";
            return $"({v.x:0.00},{v.y:0.00},{v.z:0.00}) chunk ({kc.x},{kc.y},{kc.z}) seamless {(WaterStateField.SeamlessSheets ? "on" : "off")}: probe water (y+-2 m) {probeLevel:0.00} (off {v.y - probeLevel:+0.00;-0.00} m); {clamp}"
                + (Mathf.Abs(band - probeLevel) > 0.01f && ok ? " = the probe and the chunk band read DIFFERENT water floors" : "");
        }

        private static void Drop(Vector3Int key)
        {
            s_borderSig.Remove(key); s_spreadOnBuild.Remove(key);   // 0.2.1201
            s_live.Remove(key); s_builtAt.Remove(key);   // 0.2.1344 (a slice in progress for it sees it is no longer wanted)
            if (s_drawn.TryGetValue(key, out var go) && go != null)
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Object.Destroy(mf.sharedMesh);
                Object.Destroy(go);
            }
            s_drawn.Remove(key);
            s_dirty.Remove(key);
            s_wantedAt.Remove(key);   // 0.2.1191
            s_laterFirst.Remove(key);   // 0.2.1347
        }
    }
}
