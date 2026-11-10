using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1157 (Fire: "Infinity Hammer can level vanilla terrain so fast. You barely even see it happen. There's no reason why we
    // cannot smooth out and trench some snow just as cleanly"; "As you said on the client side it should be almost instant and then
    // it syncs to the server"). Tools\TRAIL_LATENCY_PLAN_20261027.md section 5. CLIENT-PREDICTED TRAILS:
    //  1. One stamp function (StampG) for the owner's write (FatSnowTrail.WriteField) and the walker's prediction: same inputs, both
    //     max-merged, so applying the same op twice changes nothing (no double cut).
    //  2. The walker's own client writes its walk stamp into a client-side overlay at spawn, re-composes only the touched paint
    //     texels (VoxelWorld.PokeTrailTexels) and queues only the chunk(s) under the stamp as one atomic group
    //     (VoxelMeshPipeline.EnqueueTrailGroup): no 9-column paint compose, no 1 s per-column G cooldown, no 1.2-4 s remesh debounce.
    //  3. Drawn G = max(owner G, overlay G) everywhere the paint is composed, so nothing flickers back while the op is in flight.
    //     An overlay cell is dropped once the owner's G covers it; one the owner never confirms in 3 s fades over 0.5 s (logged).
    //  4. A heightmap regenerate caused only by trail stamps (this peer's own owner write, or a received ZDO whose only change is
    //     paint G) takes the same small path - touched texels + the chunk(s) under them - instead of the 9-column compose and the
    //     zone rewrap. Anything else (heights, other paint channels, big areas) keeps the old path.
    //  5. The refill only touches owner (comp) G, so overlay cells are never refilled.
    // Timers ([TrailLatency], Debug only): spawn -> owner write -> ZDO arrive -> paint upload -> remesh queued -> mesh applied, per op.
    // Cost ([TrailPredict], Debug, every 5 s): the main-thread ms per frame of prediction + texel upload + group apply.
    internal static class FatTrailFast
    {
        internal const float HoldSeconds = 3f;
        private const int MaxSmallTexels = 16;   // a regenerate whose G change spans more than this (texels per side) keeps the old path

        // ── 1. the shared stamp ──────────────────────────────────────────────────────────────────────────────────────────────
        // The walked-line field at a texel centre p: G = 0.5 + (half - d) x slope, d = distance to the segment a-b, clamped 0-1.
        internal static float StampG(Vector2 p, Vector2 a, Vector2 b, float half, float slope)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            float d = Vector2.Distance(p, a + ab * t);
            return Mathf.Clamp01(0.5f + (half - d) * slope);
        }

        // ── 2. the overlay ───────────────────────────────────────────────────────────────────────────────────────────────────
        private struct Pred { public float G, At, Seen; }   // 0.2.1312: Seen = highest owner cut seen at the cell
        private static readonly Dictionary<Vector2Int, Pred> s_pred = new Dictionary<Vector2Int, Pred>();
        internal static bool HasOverlay => s_pred.Count > 0;
        internal static int OverlayCount => s_pred.Count;

        // The drawn overlay G at a world texel (a fading cell ramps down), 0 when none. Main thread.
        internal static float OverlayG(int x, int z)
        {
            if (!s_pred.TryGetValue(new Vector2Int(x, z), out var p)) return 0f;
            float age = Time.unscaledTime - p.At;
            return age <= HoldSeconds ? p.G : 0f;   // 0.2.1160: past the hold it is about to be dropped (one re-compose, no ramp)
        }

        // The walker's own step (FatSnowTrail's trigger, right after the snow_walk op is instantiated). The owner write (when this
        // peer owns the comp) already ran inside the Instantiate, so Recent.Prev returns the very link it used.
        internal static void PredictWalk(Player player, Vector3 pos, GameObject walkObj)
        {
            if (player == null || player != Player.m_localPlayer || !Renders) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var op = walkObj != null ? walkObj.GetComponent<TerrainOp>() : null;
                var s = op != null ? op.m_settings : null;
                if (s == null || FatSnowTrail.Kind(s) != FatSnowTrail.OpKind.Walk) return;
                if (FatSnowTrail.VedrOwnsAt(pos)) return;   // 0.2.1419: Vedr draws this chunk, Vedr owns the trail (no FAT prediction either)
                var trace = TraceOn ? NewTrace(pos) : null;
                Vector3 a = FatSnowTrail.Recent.Prev(pos), b = pos;
                if (s.m_halfOffset) { a.x -= 0.5f; a.z -= 0.5f; b.x -= 0.5f; b.z -= 0.5f; }
                float half = FatSnowTrail.TrenchWidth * 0.5f + FatSnowTrail.WalkCoreBias;
                float reach = half + 0.5f / FatSnowTrail.FieldSlope;
                var a2 = new Vector2(a.x, a.z); var b2 = new Vector2(b.x, b.z);
                int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) - reach), x1 = Mathf.CeilToInt(Mathf.Max(a.x, b.x) + reach);
                int z0 = Mathf.FloorToInt(Mathf.Min(a.z, b.z) - reach), z1 = Mathf.CeilToInt(Mathf.Max(a.z, b.z) + reach);
                float now = Time.unscaledTime;
                int cells = 0;
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float g = StampG(new Vector2(x, z), a2, b2, half, FatSnowTrail.FieldSlope);
                        if (g <= 0f) continue;
                        var k = new Vector2Int(x, z);
                        if (s_pred.TryGetValue(k, out var old) && old.G >= g && now - old.At <= HoldSeconds) continue;
                        s_pred[k] = new Pred { G = Mathf.Max(g, old.G), At = now, Seen = old.Seen };
                        if (TestTrace) s_testPred[k] = Mathf.Max(g, s_testPred.TryGetValue(k, out float tg) ? tg : 0f);
                        cells++;
                    }
                s_predOps++; s_predCells += cells;
                if (cells == 0) { if (trace != null) { trace.TPaint = Time.realtimeSinceStartup; trace.FPaint = Time.frameCount; } return; }
                Small(x0, z0, x1, z1, pos.y, trace, true);
                if (!s_predSaid)
                {
                    s_predSaid = true;
                    FiresLogger.LogDebug($"[TrailPredict] first predicted trail drawn on this client at ({pos.x:0.0},{pos.z:0.0}): {cells} texel(s) re-composed and the chunk(s) under them queued as one group this frame; the owner's G replaces it when it arrives (0.2.1157, Fire: \"on the client side it should be almost instant and then it syncs to the server\") - once per session");
                }
            }
            catch (System.Exception ex) { FiresLogger.LogDebug($"[TrailPredict] prediction skipped: {ex.Message}"); }
            finally { AddCost(t0); AddStage(StPredict, t0); }
        }

        // Re-compose the touched texels, then (if any G changed) remesh the chunk(s) under them as one atomic group.
        private static readonly List<Vector2Int> s_cols = new List<Vector2Int>(8);
        private static readonly List<VoxelChunk> s_chunks = new List<VoxelChunk>(8);
        // 0.2.1160 (PERF, round 36: "per-frame cost avg 7-17 ms / max 37-90 ms"; Fire's overlay 12 fps, 384 ms spikes): every stamp
        // used to queue its own group at once - the prediction AND this peer's owner regenerate of the same op (2 groups/step),
        // groups chaining into each other (sizes up to 12 chunks) and landing up to 4 a frame at ~5.7 ms per chunk apply. Stamps now
        // only re-compose texels (cheap) and mark their chunks dirty; Flush (from Tick, every CoalesceSeconds) queues the dirty
        // chunks not already building, split into touching sets: at most one urgent build per chunk in flight, ~6-7 a second.
        private const float CoalesceSeconds = 0.15f;
        private static readonly Dictionary<Vector3Int, VoxelChunk> s_dirty = new Dictionary<Vector3Int, VoxelChunk>();
        private static readonly List<Trace> s_dirtyTraces = new List<Trace>();
        private static float s_flushAt;
        private static void Small(int x0, int z0, int x1, int z1, float y, Trace trace, bool predicted)
        {
            s_cols.Clear();
            long tp = System.Diagnostics.Stopwatch.GetTimestamp();
            bool changed = VoxelWorld.PokeTrailTexels(x0, z0, x1, z1, s_cols);
            AddStage(StPoke, tp);
            s_pokes++;
            if (trace != null && trace.TPaint < 0f) { trace.TPaint = Time.realtimeSinceStartup; trace.FPaint = Time.frameCount; }
            if (!changed) return;
            s_chunks.Clear();
            VoxelWorld.CollectTrailChunks(x0, z0, x1, z1, y, s_chunks);
            if (s_chunks.Count == 0) { if (trace != null) trace.Note = "no chunk under the stamp"; return; }
            foreach (var c in s_chunks) if (c != null) s_dirty[c.Key] = c;
            if (trace != null && !s_dirtyTraces.Contains(trace)) s_dirtyTraces.Add(trace);
            if (predicted) s_predStamps++; else s_regenStamps++;
        }

        private static readonly List<VoxelChunk> s_ready = new List<VoxelChunk>(16);
        private static readonly List<VoxelChunk> s_set = new List<VoxelChunk>(8);
        private static readonly List<Vector3Int> s_flushed = new List<Vector3Int>(16);
        private static void Flush()
        {
            if (s_dirty.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < s_flushAt) return;
            s_flushAt = now + CoalesceSeconds;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            s_ready.Clear(); s_flushed.Clear();
            foreach (var kv in s_dirty)
            {
                if (VoxelMeshPipeline.InTrailGroup(kv.Key)) { s_held++; continue; }   // one build in flight per chunk: stays dirty
                s_flushed.Add(kv.Key);
                if (kv.Value != null && VoxelWorld.IsLive(kv.Value)) s_ready.Add(kv.Value);
            }
            foreach (var k in s_flushed) s_dirty.Remove(k);
            float tq = Time.realtimeSinceStartup;
            foreach (var t in s_dirtyTraces) if (t.TQueued < 0f) t.TQueued = tq;
            var traces = s_dirtyTraces.Count > 0 ? s_dirtyTraces.ToArray() : null;
            s_dirtyTraces.Clear();
            // touching sets (a seam needs both sides in one swap; far-apart stamps stay separate small groups)
            while (s_ready.Count > 0)
            {
                s_set.Clear(); s_set.Add(s_ready[s_ready.Count - 1]); s_ready.RemoveAt(s_ready.Count - 1);
                for (int i = 0; i < s_set.Count; i++)
                    for (int j = s_ready.Count - 1; j >= 0; j--)
                    {
                        var a = s_set[i].Key; var b = s_ready[j].Key;
                        if (Mathf.Abs(a.x - b.x) <= 1 && Mathf.Abs(a.y - b.y) <= 1 && Mathf.Abs(a.z - b.z) <= 1) { s_set.Add(s_ready[j]); s_ready.RemoveAt(j); }
                    }
                var tr = traces;
                VoxelMeshPipeline.EnqueueTrailGroup(s_set, tr == null ? (System.Action<int>)null : f =>
                {
                    float ta = Time.realtimeSinceStartup;
                    foreach (var t in tr) if (t.TApplied < 0f) { t.TApplied = ta; t.FApplied = f; }
                });
                s_groupsQueued++;
                if (s_set.Count > s_groupMax) s_groupMax = s_set.Count;
            }
            AddCost(t0); AddStage(StQueue, t0);
        }

        // ── 3. reconcile (main thread, every frame from the pipeline driver) ───────────────────────────────────────────────────
        private static readonly List<Vector2Int> s_scratch = new List<Vector2Int>(256);
        private static float s_reconcileAt;
        private static readonly List<KeyValuePair<Vector2Int, float>> s_seenUp = new List<KeyValuePair<Vector2Int, float>>(64);   // 0.2.1312
        private static bool s_ownerDropSaid;   // 0.2.1312
        internal static void Tick()
        {
            RollCost();
            Summary();
            if (Time.unscaledTime >= s_fadeMinuteAt)
            {
                s_fadeMinuteAt = Time.unscaledTime + 60f;
                if (s_fadesThisMinute > 0) FiresLogger.LogDebug($"[TrailPredict] {s_fadesThisMinute} predicted cell(s) faded in the last minute (the owner never confirmed them; max one line a minute)");
                s_fadesThisMinute = 0;
            }
            Flush();
            if (s_pred.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < s_reconcileAt) return;
            s_reconcileAt = now + 0.1f;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                s_scratch.Clear();
                int fx0 = int.MaxValue, fz0 = int.MaxValue, fx1 = int.MinValue, fz1 = int.MinValue;
                Vector2s curZone = new Vector2s(short.MinValue, short.MinValue); Heightmap curHm = null;
                int scanned = 0;
                foreach (var kv in s_pred)
                {
                    if (++scanned > ReconcilePerTick) break;   // 0.2.1160: bounded; confirmed cells leave, so the rest come up next tick
                    float owner = OwnerG(kv.Key.x, kv.Key.y, ref curZone, ref curHm);
                    float age = now - kv.Value.At;
                    if (owner >= kv.Value.G - 0.01f) { s_scratch.Add(kv.Key); s_reconciled++; continue; }
                    // 0.2.1312: the owner's cut DROPPED here (Core 1066 SnowRefill lowered the edited G): the prediction must not hold a
                    // trench the owner already refilled, so the cell leaves now (one re-compose), not after the hold.
                    if (owner > kv.Value.Seen) s_seenUp.Add(new KeyValuePair<Vector2Int, float>(kv.Key, owner));
                    else if (kv.Value.Seen > 0.05f && owner < kv.Value.Seen - 0.02f)
                    {
                        s_scratch.Add(kv.Key);
                        if (kv.Key.x < fx0) fx0 = kv.Key.x; if (kv.Key.x > fx1) fx1 = kv.Key.x;
                        if (kv.Key.y < fz0) fz0 = kv.Key.y; if (kv.Key.y > fz1) fz1 = kv.Key.y;
                        if (!s_ownerDropSaid) { s_ownerDropSaid = true; FiresLogger.LogDebug($"[TrailPredict] overlay cleared at ({kv.Key.x},{kv.Key.y}): owner cut fell {kv.Value.Seen:0.00} -> {owner:0.00} (refilled by the owner), predicted G {kv.Value.G:0.00} dropped (0.2.1312)"); }
                        continue;
                    }
                    if (age <= HoldSeconds) continue;
                    // 0.2.1160: an unconfirmed cell is dropped once (one re-compose), not re-drawn every tick through a 0.5 s ramp
                    if (kv.Key.x < fx0) fx0 = kv.Key.x; if (kv.Key.x > fx1) fx1 = kv.Key.x;
                    if (kv.Key.y < fz0) fz0 = kv.Key.y; if (kv.Key.y > fz1) fz1 = kv.Key.y;
                    {
                        s_scratch.Add(kv.Key); s_faded++;
                        if (!s_fadeInfoSaid) { s_fadeInfoSaid = true; FiresLogger.LogDebug($"[TrailPredict] first predicted cell faded at ({kv.Key.x},{kv.Key.y}): predicted G {kv.Value.G:0.00}, owner G {owner:0.00} {HoldSeconds:0} s after the step - the owner refused / never received the op, or linked the step differently (0.2.1157)"); }
                        s_fadesThisMinute++;
                        if (FiresLogger.DebugEnabled && now >= s_fadeLogAt)
                        {
                            s_fadeLogAt = now + 5f;
                            FiresLogger.LogDebug($"[TrailPredict] predicted cell ({kv.Key.x},{kv.Key.y}) G {kv.Value.G:0.00} faded: the owner's G there is {owner:0.00} {HoldSeconds:0} s after the step (the owner refused or never received the op, or linked the step differently)");
                        }
                    }
                }
                foreach (var k in s_scratch) s_pred.Remove(k);
                foreach (var su in s_seenUp) if (s_pred.TryGetValue(su.Key, out var pv)) { pv.Seen = su.Value; s_pred[su.Key] = pv; }
                s_seenUp.Clear();
                if (fx0 <= fx1) Small(fx0, fz0, fx1, fz1, Player.m_localPlayer != null ? Player.m_localPlayer.transform.position.y : 0f, null, true);
            }
            finally { AddCost(t0); AddStage(StReconcile, t0); }
        }

        // The owner's (comp) G at a world texel as this peer's heightmap holds it (the same read as the paint compose).
        internal static float OwnerG(int x, int z, ref Vector2s curZone, ref Heightmap curHm)
        {
            var probe = new Vector3(x + 0.013f, 0f, z + 0.013f);
            var zone = ZoneSystem.GetZone(probe);
            if (curHm == null || zone.x != curZone.x || zone.y != curZone.y) { curZone = zone; curHm = Heightmap.FindHeightmap(probe); }
            if (curHm == null) return 0f;
            curHm.WorldToVertexMask(new Vector3(x, 0f, z), out int mx, out int my);
            return VoxelWorld.CutG(curHm, mx, my);   // 0.2.1312: proportional cut (edited G over build-data base G), same as the compose
        }

        internal static float OwnerG(int x, int z)
        {
            var zn = new Vector2s(short.MinValue, short.MinValue); Heightmap hm = null;
            return OwnerG(x, z, ref zn, ref hm);
        }

        // ── 4. small regenerates ─────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class Regen { public bool Full; public int X0 = int.MaxValue, Z0 = int.MaxValue, X1 = int.MinValue, Z1 = int.MinValue; public float Y = float.NaN; public bool Owner; }
        private static readonly Dictionary<Heightmap, Regen> s_regen = new Dictionary<Heightmap, Regen>();
        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> CompHmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> CompPaint = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> CompLevel = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> CompSmooth = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> CompModH = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
        private static readonly AccessTools.FieldRef<TerrainComp, int> CompWidth = AccessTools.FieldRefAccess<TerrainComp, int>("m_width");

        // Rendering peers only: the dedicated server keeps the old path (its zone rebuild bakes the colliders).
        private static bool Renders => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        private static Regen EntryFor(Heightmap hm)
        {
            if (!s_regen.TryGetValue(hm, out var e)) { e = new Regen(); s_regen[hm] = e; }
            if (s_regen.Count > 64) { s_regen.Clear(); s_regen[hm] = e; }   // stale entries (a regenerate that never came)
            return e;
        }

        private static void Grow(Regen e, int x0, int z0, int x1, int z1)
        {
            if (x0 < e.X0) e.X0 = x0; if (z0 < e.Z0) e.Z0 = z0; if (x1 > e.X1) e.X1 = x1; if (z1 > e.Z1) e.Z1 = z1;
        }

        // 0.2.1162 (the lead: ClutterSystem.LateUpdate 34 ms/s with Fire standing still, GC every 1-2 s): vanilla resets grass after
        // EVERY terrain op (DoOperation: the op's radius) and on every received revision (CheckLoad: the whole 64 m heightmap when
        // more than one op arrived) - with player + creature trails that is a grass rebuild (m_forceRebuild, all reset patches in one
        // frame, allocating) many times a second. A trail-only change is narrowed here; ClutterResetWatch applies it.
        // Valid only inside the DoOperation / CheckLoad call that set it (both clear it on entry and exit).
        internal static bool TrailGrass;
        internal static Vector3 TrailGrassCenter;
        internal static float TrailGrassRadius;   // < 0: nothing changed, skip the reset
        private static void NoteTrailGrass(Vector3 c, float r)
        {
            if (TrailGrass && TrailGrassRadius >= 0f && r >= 0f)
            {
                // a second trail op inside the same call: grow to cover both
                float d = Vector3.Distance(new Vector3(c.x, 0f, c.z), new Vector3(TrailGrassCenter.x, 0f, TrailGrassCenter.z));
                if (d + r <= TrailGrassRadius) return;
                float nr = (d + r + TrailGrassRadius) * 0.5f;
                TrailGrassCenter = d > 0.001f ? Vector3.Lerp(TrailGrassCenter, c, (nr - TrailGrassRadius) / d) : TrailGrassCenter;
                TrailGrassRadius = nr;
                return;
            }
            TrailGrass = true; TrailGrassCenter = c; TrailGrassRadius = r;
        }
        internal static void ClearTrailGrass() { TrailGrass = false; TrailGrassRadius = 0f; }

        // Owner side: every op this peer applies. Only our trail ops (walk / creature / shovel patch, paint-only) stay small.
        [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation")]
        private static class Patch_OwnerOp
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier)
            {
                try
                {
                    bool paintOnly = modifier != null && modifier.m_paintCleared && !modifier.m_level && !modifier.m_raise && !modifier.m_smooth;
                    // 0.2.1162: a trail-only op (paint G, no height) narrows the grass reset vanilla DoOperation calls right after this
                    if (paintOnly && FatSnowTrail.Kind(modifier) != FatSnowTrail.OpKind.Other)
                        NoteTrailGrass(pos, Mathf.Max(modifier.m_paintRadius, 1f) + FatSnowTrail.Recent.FallbackMetres + 0.5f);
                    if (!Renders || __instance == null) return;
                    var hm = CompHmap(__instance);
                    if (hm == null) return;
                    var e = EntryFor(hm);
                    e.Owner = true;
                    if (!paintOnly || FatSnowTrail.Kind(modifier) == FatSnowTrail.OpKind.Other) { e.Full = true; return; }
                    // the walk segment can reach back 2.5 m (Recent's fallback); a patch / creature op its own radius
                    float r = Mathf.Max(modifier.m_paintRadius, 1f) + FatSnowTrail.Recent.FallbackMetres + 1.5f;
                    Grow(e, Mathf.FloorToInt(pos.x - r), Mathf.FloorToInt(pos.z - r), Mathf.CeilToInt(pos.x + r), Mathf.CeilToInt(pos.z + r));
                    if (float.IsNaN(e.Y)) e.Y = pos.y;
                    s_lastOwnerOp = pos; s_lastOwnerOpFrame = Time.frameCount;
                    if (TraceOn) { var t = FindTrace(pos, 0.05f); if (t != null && t.TOwner < 0f) t.TOwner = Time.realtimeSinceStartup; }
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[TrailPredict] owner-op note skipped: {ex.Message}"); }
            }
        }

        // Received side: a comp loading a new ZDO revision. Only a paint-G-only change stays small.
        [HarmonyPatch(typeof(TerrainComp), "Load")]
        private static class Patch_Load
        {
            private static Color[] s_paint = new Color[0];
            private static float[] s_level = new float[0], s_smooth = new float[0];
            private static bool[] s_modH = new bool[0];
            private static TerrainComp s_comp;

            private static void Prefix(TerrainComp __instance)
            {
                s_comp = null;
                try
                {
                    if (!Renders || __instance == null || CompHmap(__instance) == null) return;
                    var p = CompPaint(__instance); var l = CompLevel(__instance); var sm = CompSmooth(__instance); var mh = CompModH(__instance);
                    if (p == null || l == null || sm == null || mh == null) return;
                    if (s_paint.Length != p.Length) s_paint = new Color[p.Length];
                    if (s_level.Length != l.Length) s_level = new float[l.Length];
                    if (s_smooth.Length != sm.Length) s_smooth = new float[sm.Length];
                    if (s_modH.Length != mh.Length) s_modH = new bool[mh.Length];
                    System.Array.Copy(p, s_paint, p.Length); System.Array.Copy(l, s_level, l.Length);
                    System.Array.Copy(sm, s_smooth, sm.Length); System.Array.Copy(mh, s_modH, mh.Length);
                    s_comp = __instance;
                }
                catch { s_comp = null; }
            }

            private static void Postfix(TerrainComp __instance, bool __result)
            {
                if (s_comp != __instance) return;
                s_comp = null;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    if (!__result) return;
                    var hm = CompHmap(__instance);
                    var p = CompPaint(__instance); var l = CompLevel(__instance); var sm = CompSmooth(__instance); var mh = CompModH(__instance);
                    var e = EntryFor(hm);
                    for (int i = 0; i < l.Length; i++)
                        if (l[i] != s_level[i] || sm[i] != s_smooth[i] || mh[i] != s_modH[i]) { e.Full = true; return; }
                    int pitch = CompWidth(__instance) + 1, n = 0;
                    Vector3 o = hm.transform.position;
                    int num = pitch / 2;
                    for (int i = 0; i < p.Length; i++)
                    {
                        Color a = s_paint[i], b = p[i];
                        if (a.r != b.r || a.b != b.b || a.a != b.a) { e.Full = true; return; }
                        if (a.g == b.g) continue;
                        n++;
                        int x = Mathf.RoundToInt(o.x + (i % pitch - num) * hm.m_scale), z = Mathf.RoundToInt(o.z + (i / pitch - num) * hm.m_scale);
                        Grow(e, x, z, x, z);
                    }
                    if (n == 0) { NoteTrailGrass(Vector3.zero, -1f); return; }   // 0.2.1162: nothing changed: no grass reset at all
                    if (e.X1 - e.X0 > MaxSmallTexels || e.Z1 - e.Z0 > MaxSmallTexels) { e.Full = true; return; }
                    // 0.2.1162: a G-only load: vanilla CheckLoad resets grass over the WHOLE 64 m heightmap when more than one op arrived
                    // in one revision; narrow it to the changed texels
                    NoteTrailGrass(new Vector3((e.X0 + e.X1) * 0.5f, 0f, (e.Z0 + e.Z1) * 0.5f), 0.5f * Mathf.Sqrt((e.X1 - e.X0) * (e.X1 - e.X0) + (e.Z1 - e.Z0) * (e.Z1 - e.Z0)) + 1.5f);
                    if (TraceOn)
                        foreach (var t in s_traces)
                            if (t.TZdo < 0f && t.Pos.x >= e.X0 - 1 && t.Pos.x <= e.X1 + 1 && t.Pos.z >= e.Z0 - 1 && t.Pos.z <= e.Z1 + 1) t.TZdo = Time.realtimeSinceStartup;
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[TrailPredict] load diff skipped: {ex.Message}"); }
                finally { AddCost(t0); AddStage(StLoad, t0); }
            }
        }

        // From VoxelZoneStreamer's Regenerate hook, BEFORE the 9-column paint dirty and the zone rewrap: true = handled here
        // (a G-only trail change: its texels re-composed, the chunk(s) under it grouped), so the caller skips the old path.
        internal static bool OnRegenerate(Heightmap hm)
        {
            if (hm == null || !s_regen.TryGetValue(hm, out var e)) return false;
            s_regen.Remove(hm);
            if (e.Full || !Renders || e.X0 > e.X1) { if (e.Full) s_regenFull++; return false; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                float y = e.Y;
                if (float.IsNaN(y))
                {
                    // a received change: the walkers' own traces first, else the ground under its centre
                    var c = new Vector3((e.X0 + e.X1) * 0.5f, 0f, (e.Z0 + e.Z1) * 0.5f);
                    var t = FindTrace(c, 8f);
                    if (t != null) y = t.Pos.y;
                    else if (hm.GetWorldHeight(c, out float h)) y = h;
                    else y = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position.y : 0f;
                }
                Small(e.X0, e.Z0, e.X1, e.Z1, y, null, false);
                s_regenSmall++;
                if (!s_regenSaid)
                {
                    s_regenSaid = true;
                    FiresLogger.LogDebug($"[TrailPredict] first trail-only terrain regenerate taken on the small path: {(e.Owner ? "this peer's own trail op" : "a received ZDO whose only change is paint G")} over texels ({e.X0},{e.Z0})-({e.X1},{e.Z1}) re-composed, the chunk(s) under them grouped; the 9-column paint compose and the zone rewrap skipped (heights unchanged) - once per session");
                }
                return true;
            }
            catch (System.Exception ex) { FiresLogger.LogDebug($"[TrailPredict] small regenerate fell back: {ex.Message}"); return false; }
            finally { AddCost(t0); AddStage(StRegen, t0); }
        }

        // ── timers ([TrailLatency]) ──────────────────────────────────────────────────────────────────────────────────────────
        internal sealed class Trace
        {
            public Vector3 Pos; public float T0; public int F0;
            public float TOwner = -1f, TZdo = -1f, TPaint = -1f, TQueued = -1f, TApplied = -1f;
            public int FPaint = -1, FApplied = -1;
            public bool Logged; public string Note;
        }
        private static readonly List<Trace> s_traces = new List<Trace>();
        internal static bool TestTrace;   // the trail_latency test turns the timers on for its own walk (no config)
        internal static bool TraceOn => TestTrace || FiresLogger.DebugEnabled;
        internal static readonly List<Trace> TestTraces = new List<Trace>();
        private static readonly Dictionary<Vector2Int, float> s_testPred = new Dictionary<Vector2Int, float>();
        internal static Dictionary<Vector2Int, float> TestPredicted => s_testPred;

        private static Trace NewTrace(Vector3 pos)
        {
            var t = new Trace { Pos = pos, T0 = Time.realtimeSinceStartup, F0 = Time.frameCount };
            // this peer owns the comp: the owner write ran inside the op's Instantiate, just before this trace began
            if (s_lastOwnerOpFrame == Time.frameCount && (new Vector2(s_lastOwnerOp.x - pos.x, s_lastOwnerOp.z - pos.z)).sqrMagnitude < 0.0025f) t.TOwner = t.T0;
            if (s_traces.Count >= 32) s_traces.RemoveAt(0);
            s_traces.Add(t);
            if (TestTrace) TestTraces.Add(t);
            return t;
        }

        private static Trace FindTrace(Vector3 pos, float within)
        {
            float best = within * within; Trace bt = null;
            for (int i = s_traces.Count - 1; i >= 0; i--)
            {
                var t = s_traces[i];
                if (Time.realtimeSinceStartup - t.T0 > 5f) continue;
                float d = (new Vector2(t.Pos.x, t.Pos.z) - new Vector2(pos.x, pos.z)).sqrMagnitude;
                if (d <= best) { best = d; bt = t; }
            }
            return bt;
        }

        private static float s_traceLogAt;
        private static void FlushTraces()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < s_traces.Count; i++)
            {
                var t = s_traces[i];
                if (t.Logged || (t.TApplied < 0f && now - t.T0 < 5f)) continue;
                t.Logged = true;
                if (!FiresLogger.DebugEnabled || now < s_traceLogAt) continue;
                s_traceLogAt = now + 1f;
                string Ms(float at) => at < 0f ? "-" : $"{(at - t.T0) * 1000f:0}";
                FiresLogger.LogDebug($"[TrailLatency] op at ({t.Pos.x:0.0},{t.Pos.z:0.0}): spawn 0 ms; owner write {Ms(t.TOwner)} ms; ZDO arrive {Ms(t.TZdo)} ms; "
                    + $"paint upload (predicted visible) {Ms(t.TPaint)} ms ({(t.FPaint < 0 ? "-" : (t.FPaint - t.F0).ToString())} frame(s)); remesh queued {Ms(t.TQueued)} ms; "
                    + $"mesh applied {Ms(t.TApplied)} ms ({(t.FApplied < 0 ? "-" : (t.FApplied - t.F0).ToString())} frame(s)){(t.Note != null ? "; " + t.Note : "")} (owner write / ZDO '-' = the other side of the network)");
            }
            s_traces.RemoveAll(t => t.Logged && now - t.T0 > 10f);
        }

        // ── cost ([TrailPredict]) ────────────────────────────────────────────────────────────────────────────────────────────
        private static double s_frameMs; private static int s_costFrame = -1;
        private static double s_winSum, s_winMax; private static int s_winFrames;
        private static double s_testSum, s_testMax; private static int s_testFrames;

        internal static void AddCost(long startTicks)
            => AddCostMs((System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        // 0.2.1160: per-stage ms (5 s sums + max) for the [TrailPredict] cost line, so the next round shows which stage costs what.
        private const int StPredict = 0, StPoke = 1, StQueue = 2, StReconcile = 3, StLoad = 4, StRegen = 5, StApply = 6, StCount = 7;
        private static readonly string[] StName = { "predict", "texel upload", "queue", "reconcile", "ZDO load diff", "small regen", "group apply" };
        private static readonly double[] s_stSum = new double[StCount], s_stMax = new double[StCount];
        private const int ReconcilePerTick = 256;
        private static int s_predStamps, s_regenStamps, s_groupsQueued, s_groupMax, s_held;
        internal static void AddStage(int stage, long startTicks)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            s_stSum[stage] += ms; if (ms > s_stMax[stage]) s_stMax[stage] = ms;
        }
        internal static void AddApplyCost(long startTicks) { AddCost(startTicks); AddStage(StApply, startTicks); }

        internal static void AddCostMs(double ms)
        {
            if (Time.frameCount != s_costFrame) RollCost();
            s_frameMs += ms;
        }

        private static void RollCost()
        {
            if (Time.frameCount == s_costFrame) return;
            if (s_frameMs > 0.0)
            {
                s_winFrames++; s_winSum += s_frameMs; if (s_frameMs > s_winMax) s_winMax = s_frameMs;
                if (TestTrace) { s_testFrames++; s_testSum += s_frameMs; if (s_frameMs > s_testMax) s_testMax = s_frameMs; }
            }
            s_frameMs = 0.0; s_costFrame = Time.frameCount;
        }

        internal static void TestBegin()
        {
            TestTraces.Clear(); s_testPred.Clear(); s_testSum = s_testMax = 0.0; s_testFrames = 0; TestTrace = true;
        }

        internal static void TestEnd(out double avgMs, out double maxMs, out int frames)
        {
            RollCost();
            TestTrace = false;
            frames = s_testFrames; avgMs = s_testFrames > 0 ? s_testSum / s_testFrames : 0.0; maxMs = s_testMax;
        }

        private static bool s_predSaid, s_regenSaid;
        private static Vector3 s_lastOwnerOp; private static int s_lastOwnerOpFrame = -1;
        private static int s_predOps, s_predCells, s_pokes, s_regenSmall, s_regenFull, s_reconciled, s_faded;
        private static float s_summaryAt, s_fadeLogAt;
        private static bool s_fadeInfoSaid;
        private static int s_fadesThisMinute;
        private static float s_fadeMinuteAt;
        private static string StageText()
        {
            var sb = new System.Text.StringBuilder("stage ms (5 s sum / max one call):");
            for (int i = 0; i < StCount; i++) sb.Append($" {StName[i]} {s_stSum[i]:0.0}/{s_stMax[i]:0.00}");
            return sb.ToString();
        }
        private static void Summary()
        {
            FlushTraces();
            float now = Time.unscaledTime;
            if (now < s_summaryAt) return;
            s_summaryAt = now + 5f;
            if (FiresLogger.DebugEnabled && (s_predOps > 0 || s_regenSmall > 0 || s_winFrames > 0))
            {
                VoxelMeshPipeline.TrailGroupStats(out int groups, out int minSize, out int maxSize, out int waited, out int timedOut, out int capped);
                FiresLogger.LogDebug($"[TrailPredict] 5 s: {s_predOps} op(s) predicted ({s_predCells} cell(s) held), reconciled {s_reconciled}, faded {s_faded} (owner never confirmed in {HoldSeconds:0} s), overlay {s_pred.Count} cell(s); "
                    + $"texel pokes {s_pokes}; small regenerates {s_regenSmall} (zone rewrap + 9-column compose skipped), full {s_regenFull}; "
                    + $"stamps predicted {s_predStamps} / regen {s_regenStamps} coalesced into {s_groupsQueued} group(s) (max {s_groupMax} chunk(s); dirty chunk held behind its in-flight build {s_held}); landed {groups} (size {minSize}-{maxSize}, waited > 2 frames {waited}, timed out {timedOut}, held by the 1-group/frame cap {capped}); "
                    + StageText() + "; "
                    + $"per-frame cost avg {(s_winFrames > 0 ? s_winSum / s_winFrames : 0.0):0.000} ms / max {s_winMax:0.000} ms over {s_winFrames} frame(s) with trail work");
            }
            s_predStamps = s_regenStamps = s_groupsQueued = s_groupMax = s_held = 0;
            for (int i = 0; i < StCount; i++) { s_stSum[i] = 0.0; s_stMax[i] = 0.0; }
            s_predOps = s_predCells = s_pokes = s_regenSmall = s_regenFull = s_reconciled = s_faded = 0;
            s_winSum = s_winMax = 0.0; s_winFrames = 0;
        }
    }
}
