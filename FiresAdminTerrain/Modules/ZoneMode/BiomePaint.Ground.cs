using System.Collections.Generic;
using FiresCore.Bridge;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // Ground turning (the circle is exact and its edge a sharp line). A
    // painted circle makes its ground the brush's biome for everything that asks what ground a point is, never for the
    // land's shape: WorldGenerator.GetBiome is left alone, because vanilla and FAT both build heights from it
    // (GetBiomeHeight), and so do the water plan and the alt-biome map. What turns, the way FAT's environment boxes force a
    // biome:
    //  - Heightmap.GetBiome (vegetation placement, creature spawns, footsteps), FindBiomeClutter and ClutterSystem's patch
    //    biomes (grass), and Heightmap.HaveBiome (which vegetation and spawn lists a zone runs at all);
    //  - the look: the heightmap's vertex colours after vanilla builds its mesh, edited voxel ground at render time (as
    //    winter is: nothing painted is saved to chunk data or the chunk cache), and the far terrain's tiles;
    //  - the sectors (BiomePaint.Sectors): the weather, music and the biome's name, over the whole circle.
    // A heightmap's four corner sectors keep the world's ground (only a matching blend paints them), so the colours outside
    // the circle never lean toward it; inside, every vertex takes the ground's colour.
    // Water keeps its Ocean: a paint turns ground, not sea.
    internal static partial class BiomePaint
    {
        [System.ThreadStatic] private static int t_cornerScope;

        // The ground a point has been painted, when the newest circle over it turns ground. Restore gives the world's own.
        internal static bool TryGround(float wx, float wz, out Heightmap.Biome ground) => TryGround(wx, wz, out ground, out _);

        // The same, with the circle that turned it (FatSeasons blends winter snow across its rim).
        internal static bool TryGround(float wx, float wz, out Heightmap.Biome ground, out Circle turnedBy)
        {
            ground = Heightmap.Biome.None;
            turnedBy = default;
            var snapshot = s_snapshot;
            if (snapshot.Circles.Length == 0 || !snapshot.Cells.TryGetValue(CellKey(wx, wz), out var candidates)) return false;
            foreach (int index in candidates)
            {
                var circle = snapshot.Circles[index];
                if (!circle.Holds(wx, wz)) continue;
                if (circle.Brush == Brush.Restore) return false;
                if (snapshot.Sectors[index] == null) continue;
                ground = circle.Ground;
                turnedBy = circle;
                return true;
            }
            return false;
        }

        // Every ground a circle touching this square turns to (a quick yes/no for whole-zone questions).
        private static Heightmap.Biome GroundsIn(Vector3 centre, float size)
        {
            var snapshot = s_snapshot;
            if (snapshot.Circles.Length == 0) return Heightmap.Biome.None;
            float half = size * 0.5f;
            var grounds = Heightmap.Biome.None;
            for (int i = 0; i < snapshot.Circles.Length; i++)
            {
                var c = snapshot.Circles[i];
                if (c.Brush == Brush.Restore || snapshot.Sectors[i] == null) continue;
                float nx = Mathf.Clamp(c.X, centre.x - half, centre.x + half) - c.X;
                float nz = Mathf.Clamp(c.Z, centre.z - half, centre.z + half) - c.Z;
                if (nx * nx + nz * nz <= c.R * c.R) grounds |= c.Ground;
            }
            return grounds;
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetBiome), typeof(Vector3), typeof(float), typeof(bool))]
        private static class Heightmap_GetBiome
        {
            // Before the environment boxes' and skylands' Priority.Last postfix, so a forced box and a skyland still win.
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Vector3 point, ref Heightmap.Biome __result)
            {
                // A biome painted on a cave floor is that floor's (VoxelLayerPaint; a skyland's answers in its own postfix).
                if (VoxelLayerPaint.TryPaintedBiomeAt(point, out var layered)) { __result = layered; return; }
                if (__result != Heightmap.Biome.Ocean && BiomeOverride.TryAt(point.x, point.z, out var ground)) __result = ground;   // paint, then territories
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.HaveBiome))]
        private static class Heightmap_HaveBiome
        {
            private static void Postfix(Heightmap __instance, Heightmap.Biome biome, ref bool __result)
            {
                if (__result || !Any) return;
                if ((GroundsIn(__instance.transform.position, __instance.m_width * __instance.m_scale) & biome) != 0) __result = true;
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.FindBiomeClutter), typeof(Vector3))]
        private static class Heightmap_FindBiomeClutter
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Vector3 point, ref Heightmap.Biome __result)
            {
                if (__result != Heightmap.Biome.None && __result != Heightmap.Biome.Ocean && BiomeOverride.TryAt(point.x, point.z, out var ground))
                    __result = ground;
            }
        }

        [HarmonyPatch(typeof(ClutterSystem), "GetPatchBiomes")]
        private static class ClutterSystem_GetPatchBiomes
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Vector3 center, float halfSize, ref Heightmap.Biome __result)
            {
                if (__result == Heightmap.Biome.None || !Any) return;
                __result |= GroundsIn(center, halfSize * 2f);
            }
        }

        // ── The look ────────────────────────────────────────────────────────
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> RenderMesh = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_renderMesh");
        private static readonly List<Color32> s_hmapColors = new List<Color32>();

        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        private static class Heightmap_RebuildRenderMesh
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Heightmap __instance)
            {
                // 0.2.1086: timed. FDT 1028 (Fire, live 1085): 11 heightmaps in one frame spent 5,570 ms here (longest 2,489 ms)
                // while Core logged ~16,000 snow-base coarse builds a second with as many evictions (its 1500-zone cache thrashing).
                // The cost was DeepGroundAt per 4 m cell, re-asked on every rebuild because the cell answers were cleared per heightmap.
                // 0.2.1088 (Fire, live 1086: 20-40 ms a call, ~600 calls / 10 s): 1086 counted every per-vertex lookup past its
                // budget (4 per vertex, 16,900 a heightmap) and re-poked every such heightmap each frame, so each paid vanilla's
                // RebuildRenderMesh plus this walk again: a self-sustaining loop. Now no Core question is asked here at all.
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                s_cFill = s_cDecide = s_cBlocks = s_cUpload = s_cCache = s_cKey = 0;   // 0.2.1317: per-call phase timing
                TintHeightmap(__instance);
                NoteTint(__instance, t0, 0, s_lastUnknown);
                NoteTintPhases(__instance, t0);
                ZoneUpdateTimes.AddIfInside(ZoneUpdateTimes.Cause.HmTint, t0);   // 0.2.1089 [ZoneUpdate]
            }
        }

        // Vertex order is vanilla's: row by row along z, each row along x, from the heightmap's low corner.
        // 0.2.1187 (the lead, FAT 1186 load: MAIN-THREAD STALL 1624 ms, FAT Heightmap_RebuildRenderMesh.Postfix 915 ms, 78 ms in one
        // call; [BiomePaint] "9 call(s) ... worst 23.6 ms; blocks walked 0" and "63 call(s), 1096 ms, worst 192.7 ms; walked 3005,
        // skipped 964"): two costs on super flat's ~800 m ground heightmap (~200 x 200 deep cells, ~640k vertices):
        //   (a) the SETUP ran before any early-out: FillHeightmapCells read ~41k deep cells from the dictionary every call (the
        //       23-30 ms with 0 blocks walked), then GetColors copied every vertex colour;
        //   (b) around a Deep North base most blocks really need tint (deep cells near nearly every block), so the 1185 block skip
        //       could not cut the per-vertex walk.
        // The tint is now a resumable JOB per heightmap with a shared per-frame budget (TintBudgetMs): cell fill, decision and
        // block walk all resume from cursors across frames, the heightmap keeps vanilla's colours until the job finishes, and the
        // finished colours are uploaded ONCE. A rebuild while a job runs restarts that heightmap's job. Same final colours.
        private sealed class TintJob
        {
            public Heightmap Hmap; public Mesh Mesh; public int Row; public float Size, X0, Z0; public Vector3 Centre;
            public int Phase;                 // 0 fill cells, 1 decide + read colours, 2 blocks, 3 apply
            public int CellCursor, BlockCursor;
            public bool[] Range = new bool[0]; public int RX0, RZ0, RW, RH, Unknown; public bool AnyDeep;
            public bool Paint, Forced, Changed;
            public Color32[] Cols;
            public double RingMs;   // 0.2.1303: this chunk's synchronous ring asks
            public int Painted, DeepNorth, Territory, DeepGround, Kept, Skipped, KeptPaint, KeptDeep, KeptProv, Frames;
            public Heightmap.Biome ForcedBiome;
        }
        private const double TintBudgetMs = 2.0;
        private static readonly Dictionary<Heightmap, TintJob> s_tintJobs = new Dictionary<Heightmap, TintJob>();
        private static readonly List<Heightmap> s_tintJobKeys = new List<Heightmap>();
        private static int s_tintBudgetFrame = -1; private static double s_tintSpentMs;
        private static int s_jobsStarted, s_jobsDone, s_jobsDeferred, s_jobsRestarted, s_jobsMaxFrames;
        private static long s_keptPaint, s_keptDeep, s_keptProv;
        private static double s_phaseFillMs, s_phaseDecideMs, s_phaseBlocksMs, s_phaseApplyMs;

        // 0.2.1188 (live 1187: "restarted by a rebuild 26, running 31" per 10 s at rest): vanilla rebuilds a heightmap's render mesh
        // often (pokes, paint-only regenerates), and each rebuild threw the running job away and started over, so most jobs never
        // finished and the pump spent its allowance every frame on work it then discarded. The tint's colours depend only on the
        // paint version, the deep-cell answers, the override providers and the season - not on the heights - so:
        //   * a FINISHED tint is cached per heightmap with its input key; a rebuild with the same key re-applies the cached colours
        //     in one upload (or does nothing when there was nothing to tint);
        //   * a rebuild while a job with the same key runs keeps that job (progress kept; it uploads to the current mesh at the end);
        //   * only a changed key restarts.
        // The pump also works nearest-first and draws from FatBudget (one shared allowance for FAT background work).
        private sealed class CachedTint { public int Key; public Vector3 Pos; public Color32[] Cols; }
        private static readonly Dictionary<Heightmap, CachedTint> s_tintCache = new Dictionary<Heightmap, CachedTint>();
        private static readonly Dictionary<Heightmap, int> s_jobKey = new Dictionary<Heightmap, int>();
        private static int s_deepClearRev, s_jobsCoalesced, s_cacheReapplied, s_cacheNothing;
        private const int TintCacheCap = 96;

        private static int TintKey()
        {
            unchecked
            {
                int k = BiomePaintVersion.Current;
                k = k * 397 ^ s_deepClearRev;
                k = k * 397 ^ BiomeOverride.ProviderCount;
                k = k * 397 ^ VerdantsAscent.Water.Core.VanillaIce.SeasonSignature;
                k = k * 397 ^ (Any ? 1 : 0);
                return k;
            }
        }

        // 0.2.1195 (live 1193 at rest: "restarted by a rebuild 29" per 10 s, "rebuilds kept the running job 0", "cache 0"): a
        // restart means the key changed, so which input changed is logged (Debug, at most every 10 s) before the re-tint is redesigned.
        private static readonly Dictionary<Heightmap, string> s_jobKeyParts = new Dictionary<Heightmap, string>();
        private static float s_restartWhyAt;
        private static string TintKeyParts() => $"paint {BiomePaintVersion.Current} / deep-clear rev {s_deepClearRev} / providers {BiomeOverride.ProviderCount} / season {VerdantsAscent.Water.Core.VanillaIce.SeasonSignature} / any {(Any ? 1 : 0)}";

        private static void TintHeightmap(Heightmap hmap)
        {
            if (hmap == null) return;
            long tk = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1317: per-call phase (key)
            int key = TintKey();
            s_cKey += Ms(tk);
            if (s_tintCache.TryGetValue(hmap, out var cached) && cached.Key == key && cached.Pos == hmap.transform.position)
            {
                if (cached.Cols == null) { s_cacheNothing++; return; }
                var mesh = RenderMesh(hmap);
                if (mesh != null && mesh.vertexCount == cached.Cols.Length)
                {
                    long tu = System.Diagnostics.Stopwatch.GetTimestamp();
                    mesh.SetColors(cached.Cols);
                    double ms = Ms(tu);
                    s_phaseApplyMs += ms; s_cCache += ms;   // 0.2.1317
                    VerdantsAscent.Terrain.Voxel.FatBudget.Spend(VerdantsAscent.Terrain.Voxel.FatBudget.Who.Tint, ms);
                    s_cacheReapplied++;
                    return;
                }
            }
            if (s_tintJobs.ContainsKey(hmap))
            {
                if (s_jobKey.TryGetValue(hmap, out int running) && running == key) { s_jobsCoalesced++; return; }   // keep its progress
                s_jobsRestarted++;
                if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_restartWhyAt)
                {
                    s_restartWhyAt = Time.unscaledTime + 10f;
                    s_jobKeyParts.TryGetValue(hmap, out string was);
                    FiresLogger.LogDebug($"[BiomePaint] tint job restarted at ({hmap.transform.position.x:0},{hmap.transform.position.z:0}) because the tint key changed: was [{was ?? "?"}], now [{TintKeyParts()}] (0.2.1195)");
                }
            }
            float size = hmap.m_width * hmap.m_scale;
            var centre = hmap.transform.position;
            var job = new TintJob { Hmap = hmap, Size = size, Centre = centre, Row = hmap.m_width + 1, X0 = centre.x - size * 0.5f, Z0 = centre.z - size * 0.5f };
            s_tintJobs[hmap] = job;
            s_jobKey[hmap] = key;
            s_jobKeyParts[hmap] = TintKeyParts();
            s_jobsStarted++;
            RunTintJob(job);
        }

        // Every frame from TickDeepCells: continue the unfinished jobs, nearest heightmap first, inside FatBudget.
        private static void PumpTintJobs()
        {
            if (s_tintJobs.Count == 0) return;
            if (BudgetLeftMs() <= 0.05) return;
            s_tintJobKeys.Clear();
            foreach (var kv in s_tintJobs) s_tintJobKeys.Add(kv.Key);
            var cam = Utils.GetMainCamera();
            if (cam != null && s_tintJobKeys.Count > 1)
            {
                Vector3 p = cam.transform.position;
                s_tintJobKeys.Sort((a, b) => (a == null ? float.MaxValue : (a.transform.position - p).sqrMagnitude)
                    .CompareTo(b == null ? float.MaxValue : (b.transform.position - p).sqrMagnitude));
            }
            for (int i = 0; i < s_tintJobKeys.Count && BudgetLeftMs() > 0.05; i++)
            {
                var h = s_tintJobKeys[i];
                if (h == null) { s_tintJobs.Remove(h); s_jobKey.Remove(h); s_jobKeyParts.Remove(h); continue; }
                if (s_tintJobs.TryGetValue(h, out var job)) RunTintJob(job);
            }
        }

        private static double BudgetLeftMs() => VerdantsAscent.Terrain.Voxel.FatBudget.LeftMs();

        private static readonly List<Heightmap> s_pruneScratch = new List<Heightmap>();
        private static void PruneTintCache()
        {
            s_pruneScratch.Clear();
            foreach (var kv in s_tintCache) if (kv.Key == null) s_pruneScratch.Add(kv.Key);
            foreach (var h in s_pruneScratch) s_tintCache.Remove(h);
            if (s_tintCache.Count >= TintCacheCap) s_tintCache.Clear();   // still full: start over (rare; ~96 loaded heightmaps)
        }

        private static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static void RunTintJob(TintJob job)
        {
            double left = BudgetLeftMs();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long deadline = t0 + (long)(System.Math.Max(0.05, left) * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            job.Frames++;
            var hmap = job.Hmap;
            if (hmap == null) { s_tintJobs.Remove(hmap); return; }
            // the cell statics (CellInRange / DeepWeight / DeepInRect) read this job's range
            s_rangeDeep = job.Range; s_rX0 = job.RX0; s_rZ0 = job.RZ0; s_rW = job.RW; s_rH = job.RH;
            bool done = false;
            try
            {
                if (job.Phase == 0)
                {
                    long tp = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (FillCellsStep(job, deadline)) job.Phase = 1;
                    { double pm = Ms(tp); s_phaseFillMs += pm; s_cFill += pm; }   // 0.2.1317: + per-call phase
                }
                if (job.Phase == 1 && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
                {
                    long tp = System.Diagnostics.Stopwatch.GetTimestamp();
                    s_lastUnknown = job.Unknown;
                    if (job.Unknown > 0) s_tintWaiting[hmap] = new WaitRange { X0 = job.RX0, Z0 = job.RZ0, W = job.RW, H = job.RH, Y = job.Centre.y };
                    job.Paint = Any && GroundsIn(job.Centre, job.Size) != Heightmap.Biome.None;
                    job.Forced = job.AnyDeep || ProvidersIn(job.Centre, job.Size);
                    if (!job.Paint && !job.Forced) done = true;   // nothing to tint: vanilla's colours stay, no colour read or upload
                    else
                    {
                        job.Mesh = RenderMesh(hmap);
                        if (job.Mesh == null) done = true;
                        else
                        {
                            mesh_GetColors(job);
                            if (job.Cols == null) done = true; else job.Phase = 2;
                        }
                    }
                    { double pm = Ms(tp); s_phaseDecideMs += pm; s_cDecide += pm; }   // 0.2.1317
                }
                if (!done && job.Phase == 2 && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
                {
                    long tp = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (BlocksStep(job, deadline)) job.Phase = 3;
                    { double pm = Ms(tp); s_phaseBlocksMs += pm; s_cBlocks += pm; }   // 0.2.1317
                }
                if (!done && job.Phase == 3)
                {
                    long tp = System.Diagnostics.Stopwatch.GetTimestamp();
                    job.Mesh = RenderMesh(hmap);   // 0.2.1188: a coalesced rebuild may have swapped the mesh; upload to the current one
                    if (job.Changed && job.Mesh != null && job.Mesh.vertexCount == job.Cols.Length)
                        job.Mesh.SetColors(job.Cols);
                    FinishLogs(job);
                    { double pm = Ms(tp); s_phaseApplyMs += pm; s_cUpload += pm; }   // 0.2.1317
                    done = true;
                }
            }
            finally
            {
                // keep the job's own range (FillCellsStep may have grown it)
                job.Range = s_rangeDeep;
                VerdantsAscent.Terrain.Voxel.FatBudget.Spend(VerdantsAscent.Terrain.Voxel.FatBudget.Who.Tint, Ms(t0));
            }
            if (done)
            {
                s_tintJobs.Remove(hmap);
                // 0.2.1188: cache the finished result (only when every deep cell was answered; otherwise the re-poke redoes it)
                if (job.Unknown == 0 && s_jobKey.TryGetValue(hmap, out int jk))
                {
                    if (s_tintCache.Count >= TintCacheCap) PruneTintCache();
                    s_tintCache[hmap] = new CachedTint { Key = jk, Pos = job.Centre, Cols = job.Changed ? job.Cols : null };
                }
                s_jobKey.Remove(hmap);
                s_jobKeyParts.Remove(hmap);
                s_jobsDone++;
                if (job.Frames > 1) s_jobsDeferred++;
                if (job.Frames > s_jobsMaxFrames) s_jobsMaxFrames = job.Frames;
                s_keptPaint += job.KeptPaint; s_keptDeep += job.KeptDeep; s_keptProv += job.KeptProv;
            }
        }

        private static void mesh_GetColors(TintJob job)
        {
            int n = job.Row * job.Row;
            job.Mesh.GetColors(s_hmapColors);
            if (s_hmapColors.Count != n) { job.Cols = null; return; }
            job.Cols = new Color32[n];
            s_hmapColors.CopyTo(job.Cols);
        }

        // FillHeightmapCells as a resumable step (one row of cells at a time, deadline-checked).
        private static bool FillCellsStep(TintJob job, long deadline)
        {
            if (job.CellCursor == 0)
            {
                DeepGroundAt(job.Centre);   // binds Core once
                job.AnyDeep = false; job.Unknown = 0;
                if (s_deepGroundAt == null) { job.RW = 0; job.RH = 0; s_rW = 0; s_rH = 0; return true; }
                job.RX0 = Mathf.FloorToInt((job.X0 - DeepCell) / DeepCell); job.RZ0 = Mathf.FloorToInt((job.Z0 - DeepCell) / DeepCell);
                job.RW = Mathf.FloorToInt((job.X0 + job.Size + DeepCell) / DeepCell) - job.RX0 + 1;
                job.RH = Mathf.FloorToInt((job.Z0 + job.Size + DeepCell) / DeepCell) - job.RZ0 + 1;
                int n = job.RW * job.RH;
                if (job.Range.Length < n) job.Range = new bool[n];
                s_rangeDeep = job.Range; s_rX0 = job.RX0; s_rZ0 = job.RZ0; s_rW = job.RW; s_rH = job.RH;
            }
            while (job.CellCursor < job.RH)
            {
                int j = job.CellCursor++;
                for (int i = 0; i < job.RW; i++)
                {
                    long key = CellKey(job.RX0 + i, job.RZ0 + j);
                    if (s_deepCells.TryGetValue(key, out bool d)) { job.Range[j * job.RW + i] = d; job.AnyDeep |= d; continue; }
                    // 0.2.1302 ([lead] seam root cause, Fire's capture: vanilla's terrain domain shader adds snow height = vertex-colour snow
                    // weight x paint G x 2.1, so a shared chunk-edge vertex coloured snow on one side and not on the other tears): the cells
                    // the chunk's outer ring reads (within 3 cells of the range edge, on both sides of the border) are asked SYNCHRONOUSLY
                    // and cached, so both chunks colour their shared edge from the same world-position answers - never a pending "no"
                    if (i <= 2 || j <= 2 || i >= job.RW - 3 || j >= job.RH - 3)
                    {
                        // 0.2.1303 ([lead] perf: Core 1052 questions avg 0.058 ms, worst 0.22 ms -> ~60 per chunk = 3.5-13 ms): at most
                        // RingFrameMs of synchronous asks per frame; past it the job resumes this row next frame (answers so far are cached)
                        if (Time.frameCount != s_ringFrame) { s_ringFrame = Time.frameCount; s_ringFrameMs = 0.0; }
                        if (s_ringFrameMs >= RingFrameMs) { job.CellCursor = j; s_ringDeferred++; return false; }
                        long tq = System.Diagnostics.Stopwatch.GetTimestamp();
                        // 0.2.1319: a deferred ring answer is not cached; the cell takes the pump path below (Unknown, queued), so the
                        // heightmap waits and is poked ONCE when its cells resolve deep (ScanWaiting) - no 1086/1088 re-poke loop
                        bool final = TryDeepGround(key, new Vector3((job.RX0 + i + 0.5f) * DeepCell, job.Centre.y, (job.RZ0 + j + 0.5f) * DeepCell), out bool rd);
                        double qms = (System.Diagnostics.Stopwatch.GetTimestamp() - tq) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        s_ringFrameMs += qms; job.RingMs += qms; s_ringMsTotal += qms;
                        if (!final)
                        {
                            s_deferRing++;
                            job.Range[j * job.RW + i] = false;
                            job.Unknown++;
                            if (s_pendingCells.Add(key)) { if (EnqueueCell(key)) s_cellY[key] = job.Centre.y; s_queuedTotal++; }
                            continue;
                        }
                        if (s_deepCells.Count >= DeepCacheCap) { NoteDeepClear("cache cap"); s_deepCells.Clear(); s_capClears++; }
                        s_deepCells[key] = rd; s_ringSync++;
                        job.Range[j * job.RW + i] = rd; job.AnyDeep |= rd;
                        continue;
                    }
                    job.Range[j * job.RW + i] = false;
                    job.Unknown++;
                    if (s_pendingCells.Add(key)) { if (EnqueueCell(key)) s_cellY[key] = job.Centre.y; s_queuedTotal++; }
                }
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) break;
            }
            return job.CellCursor >= job.RH;
        }

        // The 1185 block walk as a resumable step (one block at a time, deadline-checked). Per-vertex logic unchanged.
        private static bool BlocksStep(TintJob job, long deadline)
        {
            // 0.2.1302: DeepWeight / CellInRange read the static range; a resumable job continues frames later, possibly after another
            // chunk's FillCellsStep moved those statics - so this job's own range is put back first (else a chunk tints with a neighbour's
            // cells: the stepped 4 m lines along borders)
            s_rangeDeep = job.Range; s_rX0 = job.RX0; s_rZ0 = job.RZ0; s_rW = job.RW; s_rH = job.RH;
            var hmap = job.Hmap;
            int row = job.Row;
            int perRow = (row + TintBlock - 1) / TintBlock, total = perRow * perRow;
            var cols = job.Cols;
            while (job.BlockCursor < total)
            {
                int b = job.BlockCursor++;
                int bx = (b % perRow) * TintBlock, bz = (b / perRow) * TintBlock;
                int ex = Mathf.Min(row, bx + TintBlock), ez = Mathf.Min(row, bz + TintBlock);
                float bx0 = job.X0 + bx * hmap.m_scale, bz0 = job.Z0 + bz * hmap.m_scale;
                float bx1 = job.X0 + (ex - 1) * hmap.m_scale, bz1 = job.Z0 + (ez - 1) * hmap.m_scale;
                var bc = new Vector3((bx0 + bx1) * 0.5f, job.Centre.y, (bz0 + bz1) * 0.5f);
                bool bPaint = job.Paint && GroundsIn(bc, Mathf.Max(bx1 - bx0, bz1 - bz0) + 2f) != Heightmap.Biome.None;
                bool bDeep = job.Forced && job.AnyDeep && DeepInRect(bx0, bz0, bx1, bz1);
                bool bProv = job.Forced && BiomeOverride.ProviderCount > 0 && ProviderInBlock(bx0, bz0, bx1, bz1);
                if (!bPaint && !bDeep && !bProv) { job.Skipped++; s_tintBlocksSkipped++; continue; }
                job.Kept++; s_tintBlocksKept++;
                if (bPaint) job.KeptPaint++; if (bDeep) job.KeptDeep++; if (bProv) job.KeptProv++;
                for (int z = bz; z < ez; z++)
                    for (int x = bx; x < ex; x++)
                    {
                        float wx = job.X0 + x * hmap.m_scale, wz = job.Z0 + z * hmap.m_scale;
                        if (!bPaint || !TryGround(wx, wz, out var ground, out var circle))
                        {
                            if (!bDeep && !bProv) continue;
                            float w = DeepWeight(wx, job.Centre.y, wz);
                            if (w > 0f)
                            {
                                cols[z * row + x] = Color32.Lerp(cols[z * row + x], DeepNorthColour, w);
                                job.Changed = true;
                                if (w >= 0.5f) job.DeepGround++;
                                continue;
                            }
                            if (BiomeOverride.ProviderCount > 0 && BiomeOverride.TryProviderSector(wx, wz, out _, out var fb) && fb != Heightmap.Biome.None)
                            {
                                cols[z * row + x] = Heightmap.GetBiomeColor(fb);
                                job.Changed = true;
                                job.Territory++;
                                job.ForcedBiome = fb;
                            }
                            continue;
                        }
                        cols[z * row + x] = FatSeasons.WinterizePainted(Heightmap.GetBiomeColor(ground), ground, circle, wx, wz);
                        job.Changed = true;
                        job.Painted++;
                        if (ground == Heightmap.Biome.DeepNorth) job.DeepNorth++;
                    }
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) break;
            }
            return job.BlockCursor >= total;
        }

        // 0.2.1302 ([lead] ask 3): each tinted chunk keeps its 4 edge rows of vertex colours; a chunk compares its edges with the
        // neighbours' stored opposite edges and counts the vertices whose colour differs (the tear's cause), Debug every 30 s
        private static readonly Dictionary<Vector2s, Color32[][]> s_edgeCols = new Dictionary<Vector2s, Color32[][]>();
        private static int s_ringSync, s_ringCompared, s_ringDiffer, s_ringDeferred, s_ringFrame = -1, s_ringChunks; private static float s_ringLogAt;
        private static double s_ringFrameMs, s_ringMsTotal, s_ringChunkWorst;
        private const double RingFrameMs = 2.0;   // 0.2.1303: per-frame cap on the synchronous ring asks
        private static void NoteRing(TintJob job)
        {
            int row = job.Row; var cols = job.Cols; if (cols == null || row < 2) return;
            var zone = ZoneSystem.GetZone(job.Centre);
            if (job.RingMs > 0.0) { s_ringChunks++; if (job.RingMs > s_ringChunkWorst) s_ringChunkWorst = job.RingMs; }
            var e = new Color32[4][];   // 0 S (z 0), 1 N (z row-1), 2 W (x 0), 3 E (x row-1)
            for (int k = 0; k < 4; k++) e[k] = new Color32[row];
            for (int t = 0; t < row; t++) { e[0][t] = cols[t]; e[1][t] = cols[(row - 1) * row + t]; e[2][t] = cols[t * row]; e[3][t] = cols[t * row + row - 1]; }
            s_edgeCols[zone] = e;
            var dirs = new[] { (0, -1, 0, 1), (0, 1, 1, 0), (-1, 0, 2, 3), (1, 0, 3, 2) };
            foreach (var (dx, dz, mine, theirs) in dirs)
            {
                if (!s_edgeCols.TryGetValue(new Vector2s(zone.x + dx, zone.y + dz), out var n) || n[theirs].Length != row) continue;
                for (int t = 0; t < row; t++)
                {
                    s_ringCompared++;
                    var a = e[mine][t]; var b = n[theirs][t];
                    if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) s_ringDiffer++;
                }
            }
            if (s_edgeCols.Count > 400) s_edgeCols.Clear();
            if (Time.unscaledTime >= s_ringLogAt)
            {
                s_ringLogAt = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[BiomePaint] chunk ring last 30 s: {s_ringSync} ring cell(s) asked synchronously in {s_ringMsTotal:0.0} ms "
                    + $"({s_ringChunks} chunk(s), avg {(s_ringChunks > 0 ? s_ringMsTotal / s_ringChunks : 0.0):0.00} ms, worst {s_ringChunkWorst:0.00} ms per chunk; {s_ringDeferred} row(s) carried to the next frame at the {RingFrameMs:0.0} ms cap), "
                    + $"{s_ringDiffer} of {s_ringCompared} shared edge vertices differ in colour from the neighbour's (0 = no colour tear) (0.2.1303)");
                s_ringSync = s_ringCompared = s_ringDiffer = s_ringDeferred = s_ringChunks = 0; s_ringMsTotal = s_ringChunkWorst = 0.0;
            }
        }

        private static void FinishLogs(TintJob job)
        {
            NoteRing(job);
            var centre = job.Centre;
            if (s_passEndsAt >= 0f && job.DeepGround > 0) { s_passZones++; s_passGreen += job.DeepGround; }
            // 0.2.1058 ([seasons]' snow diag row b): one line per heightmap and paint version proves the paint look in a log.
            if (job.Painted > 0 && VerdantsAscent.Terrain.Voxel.FatDiag.BiomePaint)
            {
                var key = (ZoneSystem.GetZone(centre), BiomePaintVersion.Current);
                if (s_tintLogged.Add(key))
                    FiresLogger.LogDebug($"[BiomePaint] heightmap vertex biome from paint for zone ({key.Item1.x},{key.Item1.y}): {job.Painted} painted vertices, DeepNorth = {(job.DeepNorth > 0 ? "yes" : "no")} ({job.DeepNorth})");
            }
            if ((job.Territory > 0 || job.DeepGround > 0) && VerdantsAscent.Terrain.Voxel.FatDiag.BiomePaint && s_forcedLogged.Add(ZoneSystem.GetZone(centre)))
            {
                var zone = ZoneSystem.GetZone(centre);
                if (job.Territory > 0)
                    FiresLogger.LogDebug($"[BiomePaint] territory vertex colour: zone ({zone.x},{zone.y}) {job.Territory} vertices -> {job.ForcedBiome} (territory override provider)");
                if (job.DeepGround > 0)
                    FiresLogger.LogDebug($"[BiomePaint] deep ground vertex colour: zone ({zone.x},{zone.y}) {job.DeepGround} vertices -> DeepNorth (Core DeepGroundAt)");
            }
        }

        private static readonly HashSet<(Vector2s, int)> s_tintLogged = new HashSet<(Vector2s, int)>();
        private static readonly HashSet<Vector2s> s_forcedLogged = new HashSet<Vector2s>();

        // ── 0.2.1062 ([seasons], lead GO): territories and snow bases carry their biome's vertex colour ────────────────────
        // A heightmap's corner biomes are built in t_cornerScope, where the override providers (RPGM ForceBiome territories,
        // BiomeOverride) don't answer, so a forced territory kept the world's vertex colour; vanilla's Custom/Heightmap draws
        // paint-mask G as snow only on Deep North's colour, so a Winter territory showed Core's raised G as tilled ground.
        // Here, after vanilla's build: a vertex no paint circle turned (paint keeps priority) takes
        //  - Deep North where Core's FiresCore.Snow.SnowField.DeepGroundAt(Vector3) is true (Core 0.2.829+, bound by reflection;
        //    an older Core = no change), covering Winter-flag territories and ForceBiome Deep North alike;
        //  - else the biome an override provider forces there (non-Deep North ForceBiome territories).
        // The ground shape and the corner sectors are untouched. A 9 x 9 probe over the heightmap gates the per-vertex pass.
        private const int ForcedProbe = 9;
        private static System.Func<Vector3, bool> s_deepGroundAt;
        private static bool s_deepGroundBound;

        private static bool DeepGroundAt(Vector3 p)
        {
            if (!s_deepGroundBound)
            {
                s_deepGroundBound = true;
                var type = AccessTools.TypeByName("FiresCore.Snow.SnowField");
                var method = type != null ? AccessTools.Method(type, "DeepGroundAt", new[] { typeof(Vector3) }) : null;
                if (method != null && method.IsStatic && method.ReturnType == typeof(bool))
                {
                    s_deepGroundAt = (System.Func<Vector3, bool>)System.Delegate.CreateDelegate(typeof(System.Func<Vector3, bool>), method);
                    FiresLogger.LogDebug("[BiomePaint] Core SnowField.DeepGroundAt bound: snow-base ground takes Deep North's vertex colour");
                }
                // 0.2.1319 (Core 0.2.1075 TryDeepGroundAt): false = Core has no answer yet (deferred: never cached, asked again),
                // true = final with deep set. Bound softly; an older Core keeps DeepGroundAt (which caches a deferred cell as "no").
                var tm = type != null ? AccessTools.Method(type, "TryDeepGroundAt", new[] { typeof(Vector3), typeof(bool).MakeByRefType() }) : null;
                if (tm != null && tm.IsStatic && tm.ReturnType == typeof(bool))
                {
                    try { s_tryDeepGroundAt = (TryDeepFn)System.Delegate.CreateDelegate(typeof(TryDeepFn), tm); } catch (System.Exception) { s_tryDeepGroundAt = null; }
                    if (s_tryDeepGroundAt != null)
                    {
                        // ONE-TIME: the cache may hold deferred "no" answers from before the bind
                        NoteDeepClear("Core TryDeepGroundAt bound (0.2.1319)");
                        s_deepCells.Clear();
                        FiresLogger.LogInfo("[BiomePaint] Core SnowField.TryDeepGroundAt bound: deferred deep-ground cells are no longer cached, asked again until Core answers (0.2.1319)");
                    }
                }
            }
            if (!s_winterBound)
            {
                // 0.2.1301 (Fire 10-30: "YES": seasonal-winter vanilla heightmap ground is coloured as snow, so Core's raised snow shows in
                // plain Meadows winter): Core's SnowField.WinterGroundAt(Vector3) (its winter build, after 0.2.1051) is ORed in; an older
                // Core has none and nothing changes
                s_winterBound = true;
                var wt = AccessTools.TypeByName("FiresCore.Snow.SnowField");
                var wm = wt != null ? AccessTools.Method(wt, "WinterGroundAt", new[] { typeof(Vector3) }) : null;
                if (wm != null && wm.IsStatic && wm.ReturnType == typeof(bool))
                {
                    s_winterGroundAt = (System.Func<Vector3, bool>)System.Delegate.CreateDelegate(typeof(System.Func<Vector3, bool>), wm);
                    FiresLogger.LogInfo("[BiomePaint] Core SnowField.WinterGroundAt bound: seasonal-winter ground takes Deep North's vertex colour too (0.2.1301)");
                }
            }
            bool deep = false;
            if (s_deepGroundAt != null) { try { deep = s_deepGroundAt(p); } catch (System.Exception) { s_deepGroundAt = null; } }
            // 0.2.1304 (Fire: "copy what vanilla does for Deep North"): seasonal-winter colour on vanilla heightmaps is vanilla's own corner method (Vedr's corner biomes), never these 4 m cells - WinterGroundAt is no longer asked here
            return deep;
        }
        private static bool s_winterBound;
        private static System.Func<Vector3, bool> s_winterGroundAt;

        // 0.2.1319 (Core 0.2.1075): TryDeepGroundAt when Core has it. Returns false = deferred (do not cache), true = final.
        // No TryDeepGroundAt = DeepGroundAt's answer, always final (old behaviour).
        private delegate bool TryDeepFn(Vector3 p, out bool deep);
        private static TryDeepFn s_tryDeepGroundAt;
        private const float DeepDeferMaxSeconds = 30f;   // bounded retry: a cell Core defers this long takes DeepGroundAt's answer (pre-1319 behaviour)
        private static readonly Dictionary<long, float> s_deferTries = new Dictionary<long, float>();   // cell -> first deferral (unscaled time)
        private static int s_deferPump, s_deferRing, s_deferGaveUp; private static float s_deferLogAt;
        private static bool TryDeepGround(long key, Vector3 p, out bool deep)
        {
            if (s_tryDeepGroundAt == null) { deep = DeepGroundAt(p); return true; }
            bool final;
            try { final = s_tryDeepGroundAt(p, out deep); }
            catch (System.Exception) { s_tryDeepGroundAt = null; deep = DeepGroundAt(p); return true; }
            if (final) { if (s_deferTries.Count > 0) s_deferTries.Remove(key); return true; }
            float now = Time.unscaledTime;
            if (s_deferTries.TryGetValue(key, out float first))
            {
                if (now - first >= DeepDeferMaxSeconds) { s_deferTries.Remove(key); s_deferGaveUp++; deep = DeepGroundAt(p); return true; }
            }
            else
            {
                if (s_deferTries.Count >= DeepCacheCap) s_deferTries.Clear();
                s_deferTries[key] = now;
            }
            deep = false;
            return false;
        }
        private static void DeferLog()
        {
            float now = Time.unscaledTime;
            if (now < s_deferLogAt) return;
            s_deferLogAt = now + 10f;
            if (s_deferPump + s_deferRing + s_deferGaveUp > 0)
                FiresLogger.LogDebug($"[BiomePaint] deep-ground deferred last 10 s: {s_deferPump} pump + {s_deferRing} ring answer(s) deferred by Core (not cached, asked again), {s_deferGaveUp} cell(s) took DeepGroundAt after {DeepDeferMaxSeconds:0} s deferred, {s_deferTries.Count} cell(s) still deferred (0.2.1319)");
            s_deferPump = s_deferRing = s_deferGaveUp = 0;
        }

        private static bool TryForced(float wx, float y, float wz, out Heightmap.Biome biome, out bool deep)
        {
            deep = false;
            if (DeepGroundAt(new Vector3(wx, y, wz))) { biome = Heightmap.Biome.DeepNorth; deep = true; return true; }
            return BiomeOverride.TryProviderSector(wx, wz, out _, out biome) && biome != Heightmap.Biome.None;
        }

        // 0.2.1063 ([seasons], Fire's live log): the gate probes Core's own 4 m cells over the heightmap plus one cell of margin,
        // so no snow base slips between probe points (1062's 9 x 9 at 8 m could). Answers are kept per cell for the vertex pass.
        private const float DeepCell = 4f;
        private static readonly Color32 DeepNorthColour = new Color32(0, 255, 0, 0);
        private static readonly Dictionary<long, bool> s_deepCells = new Dictionary<long, bool>();

        // 0.2.1086: the cell answers persist across heightmaps and rebuilds (neighbours share their margin cells, and a zone
        // rebuilds many times), cleared when the snow base can change (paint pass, overrides refresh, season) and every 60 s.
        // New questions to Core get DeepBudgetMs per frame; a cell past the budget counts as not deep for now (not cached), and
        // its heightmap is poked again so it is coloured right a few frames later. Same final colours, no multi-second frame.
        // 0.2.1089 (Fire, live 1088 overlay: ZoneSystem.Update 288 ms/s; FDT stall frames: 4,307 of ZoneSystem.Update's 4,549 ms in
        // FAT's ZoneSystem.Update postfix): the pump took its 4 ms EVERY frame while cells were queued (4 ms x ~70 fps = 280 ms/s), and
        // the queue never drained: the 60 s wholesale clear re-queued every loaded heightmap's cells, and a single Core question can
        // cost more than the whole budget (1086: ~6.9 ms each). Now a time bucket: DeepRateMsPerSecond of Core time a second
        // (at most DeepBurstMs saved up), so the pump cannot pass ~20 ms/s whatever the queue holds; the answers live 300 s (the
        // paint / override / season clears still drop them at once when the snow base changes).
        private const float DeepRateMsPerSecond = 20f, DeepBurstMs = 4f;   // 0.2.1100: no timed cache life (the wholesale clear re-queued every loaded heightmap)
        private const int DeepCacheCap = 200000;
        private static double s_deepCreditMs;
        private const double DeepFrameCapMs = 2.0;
        private const int WaitScanCells = 2048, WaitQueuePerScan = 64;   // 0.2.1100: cache reads a frame over all waiting heightmaps; cells queued per visit
        private static int s_capClears;
        private static long s_tickTicks;
        private static double s_avgQuestionMs = 0.5, s_worstQuestionMs;
        private static int s_waitCursor;

        // 0.2.1088: one heightmap's cell answers, filled from the cache (row-major, z then x).
        private static bool[] s_rangeDeep = new bool[512];
        private static int s_rX0, s_rZ0, s_rW, s_rH, s_lastUnknown;
        private struct WaitRange { public int X0, Z0, W, H, Next; public float Y; public bool Deep; }   // 0.2.1100: Next = resume index, Deep = any answered cell deep
        private static readonly HashSet<long> s_pendingCells = new HashSet<long>();
        private static readonly Queue<long> s_cellQueue = new Queue<long>();
        private static readonly Dictionary<long, float> s_cellY = new Dictionary<long, float>();
        private static readonly Dictionary<Heightmap, WaitRange> s_tintWaiting = new Dictionary<Heightmap, WaitRange>();
        private static readonly List<Heightmap> s_waitScratch = new List<Heightmap>();
        private static int s_pumpAsked, s_queuedTotal;

        private static long CellKey(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        // 0.2.1305 ([lead] GO: the pump sat at its 10 % idle rate with 303k cells queued, the player's own zone at "cell 0/361" behind
        // everything queued earlier): (b) a NEAR queue - cells within one zone of the camera - answered before the far queue; (c) a zone
        // Core says can hold no snow base (Core's SnowField.ZoneMayHaveDeepGround, soft by reflection; no such method = always ask) is
        // answered "no" at once and never queued.
        private static readonly Queue<long> s_nearQueue = new Queue<long>();
        private static readonly List<long> s_deferScratch = new List<long>(); private static readonly List<bool> s_deferScratchNear = new List<bool>(); private static readonly List<float> s_deferScratchY = new List<float>();   // 0.2.1319
        private static System.Func<Vector2s, bool> s_zoneMay; private static bool s_zoneMayLooked;
        private static int s_zoneSkipped, s_nearAsked, s_logAnswered;
        private static bool EnqueueCell(long key)
        {
            int cx = (int)(key >> 32), cz = (int)(uint)key;
            float wx = (cx + 0.5f) * DeepCell, wz = (cz + 0.5f) * DeepCell;
            var zone = ZoneSystem.GetZone(new Vector3(wx, 0f, wz));
            if (!s_zoneMayLooked)
            {
                s_zoneMayLooked = true;
                var t = AccessTools.TypeByName("FiresCore.Snow.SnowField");
                // 0.2.1369 (winter audit: 0 zones skipped, queue mean 164k, FatBudget up to 30 ms/s): ZoneMayHaveDeepGround says yes everywhere while Vedr's winter cover is up, for
                // the winter ground FAT stopped asking about in 0.2.1304. A deep-ground answer has no winter term (Core's Evaluate reads biome + providers), so the snow-base-only
                // question gives the same tint. Bound softly: an older Core keeps the old question.
                var keyOn = global::VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.DeepQueueSnowBasesOnly;
                var sb = t != null && (keyOn == null || keyOn.Value) ? AccessTools.Method(t, "ZoneMayHaveSnowBase", new[] { typeof(Vector2s) }) : null;
                bool snowBaseOnly = sb != null && sb.IsStatic && sb.ReturnType == typeof(bool);
                var m = snowBaseOnly ? sb : t != null ? AccessTools.Method(t, "ZoneMayHaveDeepGround", new[] { typeof(Vector2s) }) : null;
                if (m != null && m.IsStatic && m.ReturnType == typeof(bool))
                {
                    s_zoneMay = (System.Func<Vector2s, bool>)System.Delegate.CreateDelegate(typeof(System.Func<Vector2s, bool>), m);
                    FiresLogger.LogInfo(snowBaseOnly
                        ? "[BiomePaint] Core SnowField.ZoneMayHaveSnowBase bound: zones without a snow base are never queued, in winter too (0.2.1369)"
                        : "[BiomePaint] Core SnowField.ZoneMayHaveDeepGround bound: zones without a snow base are never queued, except in winter (0.2.1305)");
                }
            }
            if (s_zoneMay != null)
            {
                bool may = true;
                try { may = s_zoneMay(zone); } catch { s_zoneMay = null; }
                if (!may) { s_pendingCells.Remove(key); s_deepCells[key] = false; s_zoneSkipped++; return false; }
            }
            var cam = Utils.GetMainCamera();
            bool near = cam != null && Mathf.Abs(zone.x - ZoneSystem.GetZone(cam.transform.position).x) <= 1 && Mathf.Abs(zone.y - ZoneSystem.GetZone(cam.transform.position).y) <= 1;
            (near ? s_nearQueue : s_cellQueue).Enqueue(key);
            return true;
        }

        // The cell range a heightmap's vertices sample (DeepWeight's bilinear pair) plus one cell of margin (1063's gate):
        // floor((x0 - 4) / 4) .. floor((x0 + size + 4) / 4) = 19 cells a side for 64 m. Returns the cells not yet answered
        // (queued for the pump, each once).
        private static int FillHeightmapCells(Heightmap hmap, Vector3 centre, float size, out bool anyDeep)
        {
            DeepGroundAt(centre);   // binds Core once
            anyDeep = false;
            if (s_deepGroundAt == null) { s_rW = 0; s_rH = 0; return 0; }
            float x0 = centre.x - size * 0.5f, z0 = centre.z - size * 0.5f;
            s_rX0 = Mathf.FloorToInt((x0 - DeepCell) / DeepCell); s_rZ0 = Mathf.FloorToInt((z0 - DeepCell) / DeepCell);
            s_rW = Mathf.FloorToInt((x0 + size + DeepCell) / DeepCell) - s_rX0 + 1;
            s_rH = Mathf.FloorToInt((z0 + size + DeepCell) / DeepCell) - s_rZ0 + 1;
            int n = s_rW * s_rH;
            if (s_rangeDeep.Length < n) s_rangeDeep = new bool[n];
            int unknown = 0;
            for (int j = 0; j < s_rH; j++)
                for (int i = 0; i < s_rW; i++)
                {
                    long key = CellKey(s_rX0 + i, s_rZ0 + j);
                    if (s_deepCells.TryGetValue(key, out bool d)) { s_rangeDeep[j * s_rW + i] = d; anyDeep |= d; continue; }
                    s_rangeDeep[j * s_rW + i] = false;
                    unknown++;
                    if (s_pendingCells.Add(key)) { if (EnqueueCell(key)) s_cellY[key] = centre.y; s_queuedTotal++; }
                }
            return unknown;
        }

        private static bool CellInRange(int cx, int cz)
        {
            int i = cx - s_rX0, j = cz - s_rZ0;
            return i >= 0 && j >= 0 && i < s_rW && j < s_rH && s_rangeDeep[j * s_rW + i];
        }

        // Every frame from BiomePaint.Tick: the budgeted pump of queued cells (no heightmap rebuilds), then a bounded scan of
        // the waiting heightmaps; a heightmap whose cells are all answered is poked ONCE, and only if one of them is deep.
        // 0.2.1100 (Fire, live 1098 overlay: ZoneSystem.Update 320 ms/s, FPS 38; his [ZoneUpdate] line: BiomeDeepCells 382.9 ms/s
        // over 423 calls = ~9 ms EVERY frame, while Core logged DeepGroundAt at avg 0.0001 ms and the pump's own questions at
        // ~0.00 ms). Proven cause: the WAIT SCAN, not Core. A super-flat ground heightmap here is ~800 m ("[BiomePaint] heightmap
        // tint ... 22760 of 41209 cell(s) not answered"), and the scan restarted at cell 0 every frame, walking every already-
        // answered cell (up to 41,209 dictionary reads per heightmap, 4 heightmaps a frame) to find the first unanswered one and
        // queue ONE cell. Now each waiting heightmap keeps a resume index, the scan reads at most WaitScanCells cells a frame
        // across all heightmaps, and queues up to WaitQueuePerScan unanswered cells per heightmap per visit (so a big one fills
        // in seconds, not minutes). The pump is a credit bucket with debt: credit += dt x DeepRateMsPerSecond (cap DeepBurstMs),
        // a question starts only while credit > 0, and the time actually spent is subtracted, so no Core cost can exceed ~20 ms/s.
        // The wholesale 300 s cache clear is gone (it re-queued every loaded heightmap); the cache drops only on a real snow-base
        // change (paint / override / season clears below) or at DeepCacheCap.
        internal static void TickDeepCells()
        {
            // 0.2.1268 (live 1267 pump line: "queue 275592 -> 306591, cache 17 -> 18, Core questions avg 0.0009 ms", [FatBudget] "tint 159 ms / 10 s, deep-cell pump 0.1"):
            // the tint ran first and spent the whole shared FatBudget, the pump (capped by what was left) answered ~1 cell per 10 s, and the
            // tint waits for those answers, so neither ever finished: a 15 ms/s loop forever. Key "Deep-cell pump before the tint": the pump
            // runs first on its OWN credit (a question costs ~0.001 ms, so the queue drains in seconds), then the tint finishes and both idle.
            bool pumpFirst = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.DeepPumpFirst == null || VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.DeepPumpFirst.Value;
            if (!pumpFirst) PumpTintJobs();   // 0.2.1187: continue the budgeted heightmap tint jobs
            long tTick = System.Diagnostics.Stopwatch.GetTimestamp();
            int resolved = 0;
            bool idle = PumpIdle();   // 0.2.1267
            s_deepCreditMs = System.Math.Min(DeepBurstMs, s_deepCreditMs + Time.unscaledDeltaTime * DeepRateMsPerSecond * (idle ? 0.1f : 1f));
            PumpLog(idle);
            // 0.2.1106 (round #4 at the Deep North hut: BiomeDeepCells 189.8 ms/s, FAT ZoneSystem.Update 392.6 ms/s): the credit
            // was charged only the Core call's own time (~0.0001 ms each), never the loop around it (dequeue, dictionary and pending
            // bookkeeping), so with a big queue the loop ran to the 2 ms frame cap EVERY frame (2 ms x ~95 fps = ~190 ms/s). Now the
            // whole elapsed loop (and the wait scan below) is charged, so pump + scan cannot pass DeepRateMsPerSecond a second.
            if (s_nearQueue.Count + s_cellQueue.Count > 0 && s_deepGroundAt != null && s_deepCreditMs > 0)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                // 0.2.1188: also inside FatBudget (one shared allowance with the tint and the wet mask)
                double creditAtStart = pumpFirst ? s_deepCreditMs : System.Math.Min(s_deepCreditMs, VerdantsAscent.Terrain.Voxel.FatBudget.LeftMs());   // 0.2.1268
                while (s_nearQueue.Count + s_cellQueue.Count > 0)
                {
                    double spent = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (spent >= creditAtStart || spent >= DeepFrameCapMs) break;
                    bool fromNear = s_nearQueue.Count > 0;
                    long key = fromNear ? s_nearQueue.Dequeue() : s_cellQueue.Dequeue();   // 0.2.1305: near first
                    if (fromNear) s_nearAsked++;
                    s_pendingCells.Remove(key);
                    s_cellY.TryGetValue(key, out float y);
                    s_cellY.Remove(key);
                    if (s_deepCells.ContainsKey(key)) continue;
                    int cx = (int)(key >> 32), cz = (int)(uint)key;
                    if (s_deepCells.Count >= DeepCacheCap) { NoteDeepClear("cache cap"); s_deepCells.Clear(); s_capClears++; }
                    long q0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    // 0.2.1319: deferred = not stored; the cell stays pending and is queued again AFTER this loop (asked once per call)
                    if (TryDeepGround(key, new Vector3((cx + 0.5f) * DeepCell, y, (cz + 0.5f) * DeepCell), out bool dg)) s_deepCells[key] = dg;
                    else { s_deferPump++; s_deferScratch.Add(key); s_deferScratchNear.Add(fromNear); s_deferScratchY.Add(y); }
                    long q1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    double qMs = (q1 - q0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    s_avgQuestionMs = s_avgQuestionMs * 0.9 + qMs * 0.1;
                    if (qMs > s_worstQuestionMs) s_worstQuestionMs = qMs;
                    resolved++; s_logAnswered++;
                }
                // 0.2.1319: the deferred cells go back, still pending, for the next pump call
                for (int d = 0; d < s_deferScratch.Count; d++)
                {
                    long dk = s_deferScratch[d];
                    if (s_deepCells.ContainsKey(dk) || !s_pendingCells.Add(dk)) continue;
                    s_cellY[dk] = s_deferScratchY[d];
                    (s_deferScratchNear[d] ? s_nearQueue : s_cellQueue).Enqueue(dk);
                }
                s_deferScratch.Clear(); s_deferScratchNear.Clear(); s_deferScratchY.Clear();
                double pumpMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                VerdantsAscent.Terrain.Voxel.FatBudget.Spend(VerdantsAscent.Terrain.Voxel.FatBudget.Who.DeepPump, pumpMs);
                // debt allowed: the WHOLE loop is charged, so a slow question (or a long queue) silences the pump until the bucket refills
                s_deepCreditMs -= (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                s_pumpAsked += resolved;
            }
            // 0.2.1195 (live 1193: [ZoneUpdate] BiomeDeepCells 21-36 ms/s while [FatBudget] charged only 13-16 ms/s - the wait scan
            // was charged to the pump's own credit but never to FatBudget): the scan now also needs FatBudget time left and is spent there.
            if (s_tintWaiting.Count > 0 && s_deepCreditMs > 0 && VerdantsAscent.Terrain.Voxel.FatBudget.LeftMs() > 0.0)
            {
                long ts = System.Diagnostics.Stopwatch.GetTimestamp();
                ScanWaiting();
                double scanMs = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                s_deepCreditMs -= scanMs;
                VerdantsAscent.Terrain.Voxel.FatBudget.Spend(VerdantsAscent.Terrain.Voxel.FatBudget.Who.DeepPump, scanMs);
            }
            if (pumpFirst) PumpTintJobs();   // 0.2.1268: after the pump
            DeferLog();   // 0.2.1319
            s_tickTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tTick;
        }

        // 0.2.1267 ([fgn] / Fire 10-30 standing fps: "[FatBudget] deep-cell pump 147.8 ms / 10 s ... cap 15 ms a second" while he
        // stood still, with only 1 cache clear all session - the pump never drained). Two things: (1) a Debug line every 10 s,
        // NOT behind the diag key, that says why (queue, asked, avg / worst Core question, waiting heightmaps); (2) key "Deep-cell
        // pump idles when standing": a tenth of the rate while the camera stayed within 4 m for 5 s and no new heightmap is waiting.
        private static Vector3 s_idleAnchor; private static float s_idleSince, s_pumpLogAt; private static int s_idleWaitCount = -1, s_idleFrames, s_pumpFrames;
        private static int s_logAsked; private static double s_logWorstQ;
        private static bool PumpIdle()
        {
            var key = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.DeepPumpIdle;
            if (key == null || !key.Value) return false;
            if (s_nearQueue.Count > 0) return false;   // 0.2.1305 (a): never idle while cells near the camera wait
            var cam = Utils.GetMainCamera();
            if (cam == null) return false;
            Vector3 p = cam.transform.position;
            float now = Time.unscaledTime;
            if ((p - s_idleAnchor).sqrMagnitude > 16f || s_tintWaiting.Count > s_idleWaitCount) { s_idleAnchor = p; s_idleSince = now; }
            s_idleWaitCount = s_tintWaiting.Count;
            return now - s_idleSince >= 5f;
        }

        private static void PumpLog(bool idle)
        {
            s_pumpFrames++; if (idle) s_idleFrames++;
            float now = Time.unscaledTime;
            if (now < s_pumpLogAt) return;
            s_pumpLogAt = now + 10f;
            if (s_cellQueue.Count + s_nearQueue.Count > 0 || s_tintWaiting.Count > 0 || s_pumpAsked > 0)
            {
                string oldest = "-";
                foreach (var kv in s_tintWaiting) { if (kv.Key != null) { var z = ZoneSystem.GetZone(kv.Key.transform.position); oldest = $"{z.x},{z.y} cell {kv.Value.Next}/{kv.Value.W * kv.Value.H}"; } break; }
                FiresLogger.LogDebug($"[BiomePaint] deep-cell pump last 10 s: queue {s_cellQueue.Count} far + {s_nearQueue.Count} near camera, {s_logAnswered / 10f:0} answer(s)/s ({s_nearAsked} near), {s_zoneSkipped} cell(s) skipped (zone holds no snow base), Core questions avg {s_avgQuestionMs:0.0000} ms worst {s_worstQuestionMs:0.000} ms, "
                    + $"waiting heightmaps {s_tintWaiting.Count} (first: zone {oldest}), cache {s_deepCells.Count}, idle {s_idleFrames} of {s_pumpFrames} frame(s) (0.2.1267)");
            }
            s_idleFrames = s_pumpFrames = 0; s_worstQuestionMs = 0; s_logAnswered = s_nearAsked = s_zoneSkipped = 0;
        }

        // Bounded resume scan: at most WaitScanCells cache reads a frame over all waiting heightmaps, each continuing where it stopped.
        private static void ScanWaiting()
        {
            s_waitScratch.Clear();
            foreach (var kv in s_tintWaiting) s_waitScratch.Add(kv.Key);
            int budget = WaitScanCells, n = s_waitScratch.Count;
            int start = s_waitCursor % n;
            for (int w = 0; w < n && budget > 0; w++)
            {
                var hmap = s_waitScratch[(start + w) % n];
                if (hmap == null) { s_tintWaiting.Remove(hmap); continue; }
                var r = s_tintWaiting[hmap];
                int total = r.W * r.H, idx = r.Next, queued = 0;
                while (idx < total && budget > 0)
                {
                    budget--;
                    long key = CellKey(r.X0 + idx % r.W, r.Z0 + idx / r.W);
                    if (s_deepCells.TryGetValue(key, out bool d)) { r.Deep |= d; idx++; continue; }
                    if (s_pendingCells.Add(key)) { if (EnqueueCell(key)) s_cellY[key] = r.Y; }   // not asked yet, or dropped by a clear
                    if (++queued >= WaitQueuePerScan) break;
                    // the cell stays unanswered: look ahead to queue more, but resume at this index next frame
                    int ahead = idx + 1;
                    while (ahead < total && budget > 0 && queued < WaitQueuePerScan)
                    {
                        budget--;
                        long k2 = CellKey(r.X0 + ahead % r.W, r.Z0 + ahead / r.W);
                        if (!s_deepCells.ContainsKey(k2) && s_pendingCells.Add(k2)) { if (EnqueueCell(k2)) s_cellY[k2] = r.Y; queued++; }
                        ahead++;
                    }
                    break;
                }
                s_waitCursor = (start + w + 1) % n;
                if (idx >= total)
                {
                    s_tintWaiting.Remove(hmap);
                    if (r.Deep) { hmap.Poke(HeightmapRebuildDelay); s_repoked++; }
                    continue;
                }
                r.Next = idx;
                s_tintWaiting[hmap] = r;
            }
        }

        // 0.2.1317 (PLAN_fat_frame_spread item 1: one tint call 174.5 ms on zone (-3,6), 0 cells deferred): log-prove WHICH phase of a
        // single postfix call is slow before any slicing. Per call: key (TintKey), cache re-apply (SetColors), fill, decide+read,
        // blocks, upload, other (= job setup / TintKeyParts / anything outside those). Timing reads only, no behaviour change.
        private static double s_cFill, s_cDecide, s_cBlocks, s_cUpload, s_cCache, s_cKey;
        private static double s_phWorst, s_phTotal; private static int s_phFrames, s_phFrame = -1;
        private static string s_phWorstWhat;
        private static float s_phLogAt, s_phSlowAt; private static bool s_phInfoSaid;

        private static void NoteTintPhases(Heightmap hmap, long t0)
        {
            double ms = Ms(t0);
            s_phTotal += ms;
            if (Time.frameCount != s_phFrame) { s_phFrame = Time.frameCount; s_phFrames++; }
            if (!FiresLogger.DebugEnabled && (s_phInfoSaid || ms < 20.0)) return;
            double other = System.Math.Max(0.0, ms - (s_cFill + s_cDecide + s_cBlocks + s_cUpload + s_cCache + s_cKey));
            string what = null;
            if (ms > s_phWorst || ms >= 20.0)
            {
                string zone = "?";
                try { if (hmap != null) { var z = ZoneSystem.GetZone(hmap.transform.position); zone = $"({z.x},{z.y})"; } } catch { }
                string slowest = "fill"; double sm = s_cFill;
                if (s_cDecide > sm) { slowest = "decide+read"; sm = s_cDecide; }
                if (s_cBlocks > sm) { slowest = "blocks"; sm = s_cBlocks; }
                if (s_cUpload > sm) { slowest = "upload"; sm = s_cUpload; }
                if (s_cCache > sm) { slowest = "cache re-apply"; sm = s_cCache; }
                if (s_cKey > sm) { slowest = "key"; sm = s_cKey; }
                if (other > sm) { slowest = "other (setup outside the phases)"; sm = other; }
                what = $"{ms:0.0} ms zone {zone}, slowest phase {slowest} {sm:0.0} ms (fill {s_cFill:0.0} / decide+read {s_cDecide:0.0} / blocks {s_cBlocks:0.0} / upload {s_cUpload:0.0} / cache {s_cCache:0.0} / key {s_cKey:0.0} / other {other:0.0})";
                if (ms > s_phWorst) { s_phWorst = ms; s_phWorstWhat = what; }
            }
            if (what != null && ms >= 20.0)
            {
                if (!s_phInfoSaid) { s_phInfoSaid = true; FiresLogger.LogInfo($"[BiomePaint] tint phase timing on: first slow call {what} (0.2.1317)"); }
                else if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_phSlowAt) { s_phSlowAt = Time.unscaledTime + 1f; FiresLogger.LogDebug($"[BiomePaint] tint slow call {what} (0.2.1317)"); }
            }
            if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_phLogAt)
            {
                s_phLogAt = Time.unscaledTime + 10f;
                if (s_phWorstWhat != null) FiresLogger.LogDebug($"[BiomePaint] tint phases 10 s: worst call {s_phWorstWhat}; total {s_phTotal:0.0} ms over {s_phFrames} frame(s) (0.2.1317)");
                s_phWorst = 0; s_phTotal = 0; s_phFrames = 0; s_phWorstWhat = null;
            }
        }

        // Debug: one line per slow call (>= 20 ms, at most one a second) and a 10 s summary.
        private static int s_tintFrame = -1, s_tintInFrame, s_tintMaxInFrame, s_tintCalls, s_repoked, s_summaryAsked, s_summaryDeferred;
        private static double s_tintMs, s_tintWorst;
        private static float s_slowLogAt, s_summaryAt;

        private static void NoteTint(Heightmap hmap, long t0, int asked, int deferred)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (Time.frameCount != s_tintFrame) { s_tintFrame = Time.frameCount; s_tintInFrame = 0; }
            s_tintInFrame++;
            if (s_tintInFrame > s_tintMaxInFrame) s_tintMaxInFrame = s_tintInFrame;
            s_tintCalls++; s_tintMs += ms; if (ms > s_tintWorst) s_tintWorst = ms;
            s_summaryAsked += asked; s_summaryDeferred += deferred;
            // 0.2.1185: the block-culled tint's proof line (Debug, every 10 s, not tied to the BiomePaint diag switch).
            if (ms > s_tintWorst1185) s_tintWorst1185 = ms;
            s_tintCalls1185++; s_tintMs1185 += ms;
            if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_tintLog1185At)
            {
                s_tintLog1185At = Time.unscaledTime + 10f;
                FiresLogger.LogDebug($"[BiomePaint] heightmap tint 10 s: {s_tintCalls1185} call(s), {s_tintMs1185:0.0} ms total, worst {s_tintWorst1185:0.0} ms; blocks walked {s_tintBlocksKept}, skipped {s_tintBlocksSkipped} ({TintBlock}x{TintBlock} vertices, 0.2.1185); 0.2.1187 jobs: started {s_jobsStarted}, done {s_jobsDone}, spread over frames {s_jobsDeferred} (max {s_jobsMaxFrames} frames), restarted by a rebuild {s_jobsRestarted}, running {s_tintJobs.Count}; blocks kept by paint {s_keptPaint} / deep {s_keptDeep} / provider {s_keptProv}; phase ms fill {s_phaseFillMs:0.0} / decide+read {s_phaseDecideMs:0.0} / blocks {s_phaseBlocksMs:0.0} / upload {s_phaseApplyMs:0.0}; 0.2.1188: rebuilds kept the running job {s_jobsCoalesced}, cached tint re-applied {s_cacheReapplied}, nothing-to-tint cached {s_cacheNothing}, cache {s_tintCache.Count}; budget = FatBudget (shared {VerdantsAscent.Terrain.Voxel.FatBudget.FrameMs} ms a frame, {VerdantsAscent.Terrain.Voxel.FatBudget.SecondMs} ms a second)");
                s_jobsStarted = s_jobsDone = s_jobsDeferred = s_jobsRestarted = s_jobsMaxFrames = 0; s_jobsCoalesced = s_cacheReapplied = s_cacheNothing = 0; s_keptPaint = s_keptDeep = s_keptProv = 0; s_phaseFillMs = s_phaseDecideMs = s_phaseBlocksMs = s_phaseApplyMs = 0;
                s_tintCalls1185 = 0; s_tintMs1185 = 0; s_tintWorst1185 = 0; s_tintBlocksKept = 0; s_tintBlocksSkipped = 0;
            }
            if (!VerdantsAscent.Terrain.Voxel.FatDiag.BiomePaint) return;
            if (ms >= 20.0 && Time.unscaledTime >= s_slowLogAt && hmap != null)
            {
                s_slowLogAt = Time.unscaledTime + 1f;
                var z = ZoneSystem.GetZone(hmap.transform.position);
                FiresLogger.LogDebug($"[BiomePaint] heightmap tint zone ({z.x},{z.y}) {ms:0.0} ms: {deferred} of {s_rW * s_rH} cell(s) not answered yet (queued for the pump); call {s_tintInFrame} this frame");
            }
            if (Time.unscaledTime >= s_summaryAt)
            {
                s_summaryAt = Time.unscaledTime + 10f;
                if (s_tintCalls > 0 || s_tintWaiting.Count > 0 || s_pumpAsked > 0)
                    FiresLogger.LogDebug($"[BiomePaint] heightmap tint last 10 s: {s_tintCalls} call(s), {s_tintMs:0} ms total, worst {s_tintWorst:0.0} ms, most in one frame {s_tintMaxInFrame}; Core DeepGroundAt {s_pumpAsked} asked by the pump, {s_queuedTotal} cell(s) queued, {s_cellQueue.Count} still queued, {s_tintWaiting.Count} heightmap(s) waiting, {s_repoked} re-poked (deep found); {s_summaryDeferred} unanswered cell(s) seen at tint time; {s_deepCells.Count} cell(s) cached; Core question avg {s_avgQuestionMs:0.000} ms, worst {s_worstQuestionMs:0.00} ms; pump+scan {s_tickTicks * 100.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms/s, questions {s_pumpAsked / 10f:0}/s, queue {s_cellQueue.Count}, cap clears {s_capClears}"); s_worstQuestionMs = 0;
                s_tintCalls = 0; s_tintMs = 0; s_tintWorst = 0; s_tintMaxInFrame = 0; s_summaryAsked = 0; s_summaryDeferred = 0; s_repoked = 0; s_pumpAsked = 0; s_queuedTotal = 0; s_tickTicks = 0; s_capClears = 0;
            }
        }

        // Bilinear weight of the four 4 m cells around the point (cell centres as samples): 1 deep inside, 0 outside, a fade
        // across one cell at the edge. 0.2.1088: from this heightmap's cell array (FillHeightmapCells), no dictionary per vertex.
        // 0.2.1185: the tint walk's block size (vertices) and its cheap block tests.
        private const int TintBlock = 8;
        private static int s_tintCalls1185; private static double s_tintMs1185, s_tintWorst1185; private static float s_tintLog1185At;
        private static long s_tintBlocksSkipped, s_tintBlocksKept;

        // Any deep cell within one cell of the block (DeepWeight blends across one cell, so its margin is covered).
        private static bool DeepInRect(float wx0, float wz0, float wx1, float wz1)
        {
            if (s_deepGroundAt == null || s_rW == 0) return false;
            int cx0 = Mathf.FloorToInt(wx0 / DeepCell) - 1, cx1 = Mathf.FloorToInt(wx1 / DeepCell) + 1;
            int cz0 = Mathf.FloorToInt(wz0 / DeepCell) - 1, cz1 = Mathf.FloorToInt(wz1 / DeepCell) + 1;
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                    if (CellInRange(cx, cz)) return true;
            return false;
        }

        // An override provider answering at the block's corners or centre (providers are territories, far larger than a block).
        private static bool ProviderInBlock(float wx0, float wz0, float wx1, float wz1)
        {
            return ProbeProvider(wx0, wz0) || ProbeProvider(wx1, wz0) || ProbeProvider(wx0, wz1) || ProbeProvider(wx1, wz1)
                || ProbeProvider((wx0 + wx1) * 0.5f, (wz0 + wz1) * 0.5f);
        }
        private static bool ProbeProvider(float wx, float wz) => BiomeOverride.TryProviderSector(wx, wz, out _, out var fb) && fb != Heightmap.Biome.None;

        private static float DeepWeight(float wx, float y, float wz)
        {
            if (s_deepGroundAt == null || s_rW == 0) return 0f;
            float gx = wx / DeepCell - 0.5f, gz = wz / DeepCell - 0.5f;
            int cx = Mathf.FloorToInt(gx), cz = Mathf.FloorToInt(gz);
            float fx = gx - cx, fz = gz - cz;
            float a = CellInRange(cx, cz) ? 1f : 0f, b = CellInRange(cx + 1, cz) ? 1f : 0f;
            float c = CellInRange(cx, cz + 1) ? 1f : 0f, d = CellInRange(cx + 1, cz + 1) ? 1f : 0f;
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
        }

        // The override providers' 9 x 9 probe (the deep cells are FillHeightmapCells' job now).
        private static bool ProvidersIn(Vector3 centre, float size)
        {
            if (BiomeOverride.ProviderCount == 0) return false;
            float x0 = centre.x - size * 0.5f, z0 = centre.z - size * 0.5f;
            float step = size / (ForcedProbe - 1);
            for (int i = 0; i < ForcedProbe; i++)
                for (int j = 0; j < ForcedProbe; j++)
                    if (BiomeOverride.TryProviderSector(x0 + i * step, z0 + j * step, out _, out var fb)
                        && fb != Heightmap.Biome.None) return true;
            return false;
        }

        // ── 0.2.1063: re-colour after the paint changes (the server's snapshot arriving, or any paint version bump) ──────
        // The first heightmap builds ran before the server's paint arrived and before Core's base cache saw it, and nothing
        // re-coloured after. Now a paint change (debounced 0.5 s) drops Core's snow-base cache first
        // (FiresCore.Snow.SnowField.InvalidateBaseCache, by reflection; an older Core = skipped), THEN re-pokes the loaded
        // heightmaps. One summary line per pass, written once the pokes have rebuilt (3 s window).
        private const float RecolourDebounce = 0.5f, PassWindow = 3f;
        private static int s_recolourSeen = int.MinValue;
        private static float s_recolourAt = -1f, s_passEndsAt = -1f;
        private static int s_passZones, s_passGreen;
        private static System.Action s_invalidateBase;
        private static bool s_invalidateBound;

        internal static void ScheduleDeepRecolour() => s_recolourAt = Time.time + RecolourDebounce;

        // Every frame from BiomePaint.Tick (main thread).
        // 0.2.1301 ([wishbone] Core 0.2.1052: a FULL rebuild of every loaded chunk when SnowField.WinterCover crosses 0): the cached cell
        // answers (WinterGroundAt is ORed into them) are dropped at that same moment, so the rebuild re-asks and the winter colour follows
        private static System.Reflection.PropertyInfo s_winterCover; private static bool s_winterCoverLooked; private static int s_winterOn = -1;
        private static void WatchWinterCover()
        {
            if (!s_winterCoverLooked) { s_winterCoverLooked = true; s_winterCover = AccessTools.TypeByName("FiresCore.Snow.SnowField")?.GetProperty("WinterCover", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); }
            if (s_winterCover == null) return;
            int on;
            try { on = (float)s_winterCover.GetValue(null, null) > 0f ? 1 : 0; } catch { s_winterCover = null; return; }
            if (on == s_winterOn) return;
            bool first = s_winterOn < 0;
            s_winterOn = on;
            if (first) return;
            NoteDeepClear(on == 1 ? "winter cover started" : "winter cover ended");
            s_deepCells.Clear();
            FiresLogger.LogInfo($"[BiomePaint] winter cover {(on == 1 ? "started" : "ended")}: cached snow-ground answers dropped; Core's full rebuild re-colours the loaded ground (0.2.1301)");
        }

        internal static void TickDeepRecolour()
        {
            // 0.2.1304: no winter-cover watch (the 4 m cells hold snow bases only, never seasonal winter)
            int v = BiomePaintVersion.Current;
            if (v != s_recolourSeen)
            {
                bool first = s_recolourSeen == int.MinValue;
                s_recolourSeen = v;
                if (!first) ScheduleDeepRecolour();
            }
            if (s_recolourAt >= 0f && Time.time >= s_recolourAt)
            {
                s_recolourAt = -1f;
                InvalidateCoreBase();
                NoteDeepClear("biome paint version changed");
                s_deepCells.Clear();
                s_passZones = 0; s_passGreen = 0;
                s_passEndsAt = Time.time + PassWindow;
                RefreshForced();
            }
            if (s_passEndsAt >= 0f && Time.time >= s_passEndsAt)
            {
                s_passEndsAt = -1f;
                string line = $"[BiomePaint] deep ground vertex colour pass: {s_passZones} zone(s), {s_passGreen} vertices green";
                if (s_passGreen > 0) FiresLogger.LogInfo(line); else FiresLogger.LogDebug(line);
            }
        }

        private static void InvalidateCoreBase()
        {
            if (!s_invalidateBound)
            {
                s_invalidateBound = true;
                var type = AccessTools.TypeByName("FiresCore.Snow.SnowField");
                var method = type != null ? AccessTools.Method(type, "InvalidateBaseCache", System.Type.EmptyTypes) : null;
                if (method != null && method.IsStatic)
                    s_invalidateBase = (System.Action)System.Delegate.CreateDelegate(typeof(System.Action), method);
            }
            if (s_invalidateBase == null) return;
            try { s_invalidateBase(); }
            catch (System.Exception ex) { s_invalidateBase = null; FiresLogger.LogWarning($"[BiomePaint] Core SnowField.InvalidateBaseCache failed ({ex.GetType().Name}); no longer called"); }
        }

        // A territory or snow base changed at runtime (FatApi.RefreshBiomeOverrides): every loaded heightmap rebuilds once.
        internal static void RefreshForced()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            NoteDeepClear("biome overrides refreshed");
            s_deepCells.Clear();
            int poked = 0;
            foreach (var hmap in Heightmap.GetAllHeightmaps())
            {
                if (hmap == null) continue;
                hmap.Poke(HeightmapRebuildDelay);
                poked++;
            }
            s_forcedLogged.Clear();
            FiresLogger.LogDebug($"[BiomePaint] biome overrides changed: {poked} heightmap(s) rebuild their vertex colours.");
        }


        // A season change makes Vedr repaint the ground; painted heightmaps rebuild with it so their circles follow.
        private static bool s_seasonHooked;

        private static void HookSeasons()
        {
            if (s_seasonHooked) return;
            s_seasonHooked = true;
            SeasonBridge.VisualsChanged += OnSeasonChanged;
        }

        // 0.2.1106: VisualsChanged can fire repeatedly with the same season/stage (cover/weather nudges during a storm); each
        // wholesale clear re-queued every loaded heightmap's cells and re-poked them. Only a real season/stage change clears now.
        private static int s_lastSeason = int.MinValue, s_lastStage = int.MinValue;

        internal static void NoteDeepClear(string cause)
        {
            s_deepClearRev++;   // 0.2.1188: invalidates the cached tints
            int n = s_deepCells.Count;
            s_deepClears++;
            if (Time.unscaledTime >= s_clearLogAt)
            {
                s_clearLogAt = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[BiomePaint] deep-cell cache cleared: {cause} ({n} cell(s) dropped; {s_deepClears} clear(s) this session)");
            }
        }
        private static int s_deepClears;
        private static float s_clearLogAt;

        private static void OnSeasonChanged(int season, int stage)
        {
            if (season == s_lastSeason && stage == s_lastStage) return;
            s_lastSeason = season; s_lastStage = stage;
            NoteDeepClear($"season {season} stage {stage}");
            s_deepCells.Clear();
            if (!Any || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            int poked = 0;
            foreach (var hmap in Heightmap.GetAllHeightmaps())
            {
                if (hmap == null || GroundsIn(hmap.transform.position, hmap.m_width * hmap.m_scale) == Heightmap.Biome.None) continue;
                hmap.Poke(HeightmapRebuildDelay);
                poked++;
            }
            if (poked > 0) FiresLogger.LogInfo($"[BiomePaint] season changed: {poked} painted heightmap(s) rebuild.");
        }

        // Voxel ground takes its turned colour per vertex in VoxelLayerPaint.Render (ground vertices only, so a cave
        // floor or a skyland over a painted circle keeps its own).

        // The far terrain's tiles (FarQuilt), one colour at a time.
        internal static Color32 Tint(Color32 color, float wx, float wz)
            => Any && TryGround(wx, wz, out var ground) ? Heightmap.GetBiomeColor(ground) : color;

        // ── After a change ──────────────────────────────────────────────────
        // Loaded heightmaps under a changed circle rebuild (colours, and their corner sectors for spawns), grass there is
        // laid again, edited voxel chunks under it remesh, and the far tiles recolour.
        private const int HeightmapRebuildDelay = 1;

        private static void RefreshGround()
        {
            // 0.2.1373: a dedicated server draws nothing, but a height paint changes the ground its colliders and growth stand on.
            if (s_changed.Count > 0 && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null && (s_snapshot.AnyHeight || AnyHeightIn(s_changed)))
            {
                foreach (var hmap in Heightmap.GetAllHeightmaps())
                    if (hmap != null && Touched(hmap.transform.position, hmap.m_width * hmap.m_scale)) hmap.Poke(HeightmapRebuildDelay);
            }
            if (s_changed.Count == 0 || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            int heightmaps = 0, chunks = 0;
            foreach (var hmap in Heightmap.GetAllHeightmaps())
            {
                if (hmap == null || !Touched(hmap.transform.position, hmap.m_width * hmap.m_scale)) continue;
                hmap.Poke(HeightmapRebuildDelay);
                heightmaps++;
            }
            foreach (VoxelChunk chunk in VoxelWorld.AllChunks)
            {
                if (chunk == null) continue;
                var centre = chunk.WorldOrigin + new Vector3(VoxelChunk.CELLS * 0.5f, 0f, VoxelChunk.CELLS * 0.5f);
                if (!Touched(centre, VoxelChunk.CELLS)) continue;
                VoxelMeshPipeline.EnqueueRemesh(chunk);
                VoxelWorld.MarkPaintDirty(new Vector2Int(chunk.Key.x, chunk.Key.z));   // the column mask's lava alpha follows the ground
                chunks++;
            }
            if (ClutterSystem.instance != null)
                foreach (var c in s_changed) ClutterSystem.instance.ResetGrass(new Vector3(c.X, 0f, c.Z), c.R);
            FarQuilt.RecolourForSeason();
            FiresLogger.LogDebug($"[BiomePaint] ground turned: {heightmaps} heightmap(s) rebuild, {chunks} voxel chunk(s) remesh, grass laid again.");
        }
    }
}
