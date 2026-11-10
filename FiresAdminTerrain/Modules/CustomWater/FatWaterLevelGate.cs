using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water
{
    // FAT's Floating.GetWaterLevel postfix was the top patch by self-time. A
    // split timer showed that the cost is the SCAN: "combiner+legacy scan 7.9 µs, generated field 0.4 µs, pours 0.03 µs per
    // call" with 271 water combiners in the world, and that the same scan sits under the next top three (Floating.CustomFixedUpdate
    // = GeneratedWaterFeeder.Feed, Floating.SetLiquidLevel = RaiseToFatWater, Character.GetLiquidLevel). Every FAT water lookup walked
    // every placed water body for every caller.
    //
    // The ZONE INDEX: 64 m zones -> the combiners and
    // legacy waters whose bounds (+16 m margin) touch each zone, in their list order. Indexed lookups (CustomWaterManager.ScannedLevelAt,
    // GetWaterAt, WaterMeshCombiner.GetCombinerAt) check only the point's zone list, so the same water is found as by the full scan.
    // The index rebuilds on the next lookup after a combiner's bounds are set (WaterMeshCombiner.BoundsVersion: mesh build, anchor
    // restore, so moved/resized water), after the combiner or legacy count changes, and every 2 s for legacy boxes. Each rebuild logs
    // one Debug line. Caching the LEVEL is not possible: waves move every frame.
    //
    // Proof: fat_waterlevel_ab(_test) compares every path with the index OFF vs ON at the same points in the same frame; any path
    // that differs by more than 1 mm (or finds a different water body) FAILs.
    internal static class FatWaterLevelGate
    {
        private const float ZoneSize = 64f, Margin = 16f, LegacyRecheckSeconds = 2f, ReportSeconds = 60f;
        private static readonly Dictionary<long, List<WaterMeshCombiner>> s_combZones = new Dictionary<long, List<WaterMeshCombiner>>();
        private static readonly Dictionary<long, List<CustomWaterBase>> s_legacyZones = new Dictionary<long, List<CustomWaterBase>>();
        private static readonly List<WaterMeshCombiner> s_noCombiners = new List<WaterMeshCombiner>();
        private static readonly List<CustomWaterBase> s_noLegacy = new List<CustomWaterBase>();
        private static int s_builtVersion = -1, s_combiners = -1, s_legacy = -1, s_rebuilds, s_rebuildFrame = -1;
        private static string s_lastWhy = "";
        private static float s_nextLegacyCheck;

        private static long s_tPour, s_tScan, s_tGen, s_calls;
        private static float s_nextReport;
        private static readonly double TicksToMicros = 1e6 / Stopwatch.Frequency;

        internal static bool IndexOn => true;   // always on

        private static long Key(int zx, int zz) => ((long)zx << 32) ^ (uint)zz;
        private static long KeyAt(Vector3 p) => Key(Mathf.FloorToInt(p.x / ZoneSize), Mathf.FloorToInt(p.z / ZoneSize));

        internal static List<WaterMeshCombiner> CombinersAt(Vector3 p)
        {
            EnsureIndex();
            return s_combZones.TryGetValue(KeyAt(p), out var l) ? l : s_noCombiners;
        }

        internal static List<CustomWaterBase> LegacyAt(Vector3 p)
        {
            EnsureIndex();
            return s_legacyZones.TryGetValue(KeyAt(p), out var l) ? l : s_noLegacy;
        }

        // The Floating.GetWaterLevel postfix body: pours, the (indexed or full) combiner/legacy scan, the generated field; timed per part.
        internal static void Apply(Vector3 p, ref float result)
        {
            CustomWaterPatches.RaiseToPourSurface(p, ref result);   // cheap (0.03 µs) and reads the caller's own result: never memoised
            // 0.2.1167 (Fire: "fixed update is over 100ms/f"; 1166 log "[FatWaterLevel] 244976 call(s)/min: combiner+legacy lookup
            // 3.67 µs"): Floating, Character.InWater/IsSwimming/GetLiquidLevel and the feeders ask the same point many times in one
            // physics step, and every ask re-ran each overlapping placed-water combiner's PlacedWaterField.TrySurface (up to 17 in one
            // zone at the homestead pond). The combined level (combiners + legacy + generated field) is memoised per 0.25 m cell
            // (x, y, z) per physics step: the same step and cell return the first answer. Waves only move between steps.
            float t = Time.inFixedTimeStep ? Time.fixedTime : Time.time;
            int cx = Mathf.FloorToInt(p.x * 4f), cy = Mathf.FloorToInt(p.y * 4f), cz = Mathf.FloorToInt(p.z * 4f);
            long key = ((long)(cx & 0xFFFFF) << 40) | ((long)(cy & 0xFFFFF) << 20) | (long)(cz & 0xFFFFF);
            int slot = (int)((ulong)(key * unchecked((long)0x9E3779B97F4A7C15UL)) >> 52);   // 4096 slots
            s_memoCalls++;
            if (s_memoKey[slot] == key && s_memoT[slot] == t && s_memoValid[slot])
            {
                s_memoHits++;
                float m = s_memoV[slot];
                if (m > result) result = m;
                if (FiresLogger.DebugEnabled) Report();
                return;
            }
            float v;
            if (!FiresLogger.DebugEnabled)   // the per-part timing only with Debug
            {
                float sc = CustomWaterManager.ScannedLevelAt(p);
                float gn = CustomWaterManager.GeneratedLevelAt(p);
                v = sc > gn ? sc : gn;
            }
            else
            {
                long t1 = Stopwatch.GetTimestamp();
                float scanned = CustomWaterManager.ScannedLevelAt(p);
                long t2 = Stopwatch.GetTimestamp();
                float gen = CustomWaterManager.GeneratedLevelAt(p);
                long t3 = Stopwatch.GetTimestamp();
                v = scanned > gen ? scanned : gen;
                s_tScan += t2 - t1; s_tGen += t3 - t2; s_calls++;
            }
            s_memoKey[slot] = key; s_memoT[slot] = t; s_memoV[slot] = v; s_memoValid[slot] = true;
            if (v > result) result = v;
            if (FiresLogger.DebugEnabled) Report();
        }

        private static readonly long[] s_memoKey = new long[4096];
        private static readonly float[] s_memoT = new float[4096], s_memoV = new float[4096];
        private static readonly bool[] s_memoValid = new bool[4096];
        private static long s_memoCalls, s_memoHits;

        private static void EnsureIndex()
        {
            var combiners = WaterMeshCombiner.AllCombiners;
            var legacy = CustomWaterManager.AllWater;
            int nc = combiners?.Count ?? 0, nl = legacy?.Count ?? 0;
            string why = null;
            if (WaterMeshCombiner.BoundsVersion != s_builtVersion) why = "a combiner's bounds were set";
            else if (nc != s_combiners) why = "combiner count changed";
            else if (nl != s_legacy) why = "legacy water count changed";
            else if (nl > 0 && Time.time >= s_nextLegacyCheck) why = "legacy recheck";
            if (why == null) return;
            // At most one rebuild a frame (a load can trigger hundreds, each of the whole index). A load sets bounds on
            // every combiner in a burst; the first lookup of the next frame rebuilds once for all of them.
            if (Time.frameCount == s_rebuildFrame && s_builtVersion >= 0) return;
            s_rebuildFrame = Time.frameCount;
            s_builtVersion = WaterMeshCombiner.BoundsVersion; s_combiners = nc; s_legacy = nl;
            s_nextLegacyCheck = Time.time + LegacyRecheckSeconds;
            s_combZones.Clear(); s_legacyZones.Clear();
            if (combiners != null)
                for (int i = 0; i < combiners.Count; i++)
                {
                    var cb = combiners[i];
                    if (cb == null) continue;
                    var b = cb.GetCombinedBounds();
                    Mark(s_combZones, cb, b.min.x, b.min.z, b.max.x, b.max.z);
                }
            if (legacy != null)
                for (int i = 0; i < legacy.Count; i++)
                {
                    var w = legacy[i];
                    if (w == null) continue;
                    var b = w.GetWorldBounds();
                    float r = b.extents.magnitude;   // the box may be rotated: cover its diagonal
                    Mark(s_legacyZones, w, b.center.x - r, b.center.z - r, b.center.x + r, b.center.z + r);
                }
            s_rebuilds++; s_lastWhy = why;
            if (why != "legacy recheck")
            {
                // the index's own size (to rule it out of a heap delta): entries = zone->body links; ~bytes = dictionary
                // entries (~40 B) + one list per zone (~56 B) + 8 B per link
                int links = 0;
                foreach (var l in s_combZones.Values) links += l.Count;
                foreach (var l in s_legacyZones.Values) links += l.Count;
                int zones = s_combZones.Count + s_legacyZones.Count;
                long bytes = zones * 96L + links * 8L;
                FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                    "[FatWaterLevel] index rebuilt ({0}): {1} zone(s), {2} link(s), ~{3:0.0} KB; {4} combiner(s), {5} legacy water(s); index {6} ([Voxel Terrain] Water level zone gate)",
                    why, zones, links, bytes / 1024.0, nc, nl, IndexOn ? "ON" : "OFF"));
            }
        }

        private static void Mark<T>(Dictionary<long, List<T>> map, T item, float x0, float z0, float x1, float z1)
        {
            int zx0 = Mathf.FloorToInt((x0 - Margin) / ZoneSize), zx1 = Mathf.FloorToInt((x1 + Margin) / ZoneSize);
            int zz0 = Mathf.FloorToInt((z0 - Margin) / ZoneSize), zz1 = Mathf.FloorToInt((z1 + Margin) / ZoneSize);
            if ((long)(zx1 - zx0 + 1) * (zz1 - zz0 + 1) > 4096) { zx1 = zx0 + 63; zz1 = zz0 + 63; }   // guard a runaway bounds
            for (int zz = zz0; zz <= zz1; zz++)
                for (int zx = zx0; zx <= zx1; zx++)
                {
                    long k = Key(zx, zz);
                    if (!map.TryGetValue(k, out var l)) map[k] = l = new List<T>(2);
                    l.Add(item);
                }
        }

        private static void Report()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextReport) return;
            bool first = s_nextReport == 0f;
            s_nextReport = now + ReportSeconds;
            if (first || s_calls == 0) { s_tPour = s_tScan = s_tGen = s_calls = 0; s_memoCalls = s_memoHits = 0; return; }
            EnsureIndex();
            double per = TicksToMicros / s_calls;
            int maxList = s_combZones.Count == 0 ? 0 : s_combZones.Values.Max(l => l.Count);
            double msPerSec = (s_tScan + s_tGen) * TicksToMicros / 1000.0 / ReportSeconds;
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[FatWaterLevel] {0} call(s)/min, memo hit {1:0.0} % (0.25 m cell per physics step, 0.2.1167); {2} miss(es): combiner+legacy {3} {4:0.00} µs, generated field {5:0.00} µs per miss; lookups {6:0.00} ms/s; " +
                "index {7} ([Voxel Terrain] Water level zone gate): {8} zone(s), at most {9} combiner(s) in one zone, of {10} combiner(s), {11} legacy",
                s_memoCalls, s_memoCalls > 0 ? 100.0 * s_memoHits / s_memoCalls : 0.0, s_calls, IndexOn ? "lookup" : "scan", s_tScan * per, s_tGen * per, msPerSec,
                IndexOn ? "ON" : "OFF", s_combZones.Count, maxList, s_combiners, s_legacy));
            s_tPour = s_tScan = s_tGen = s_calls = 0; s_memoCalls = s_memoHits = 0;
        }

        // ── fat_waterlevel_ab(_test): every indexed path OFF vs ON at the same points, same frame ─────────────────────────────────
        private const string TestCommand = "fat_waterlevel_ab_test", TestTag = "[FatWaterLevelABTest]";
        private const string CensusCommand = "fat_water_census_test", CensusTag = "[FatWaterCensusTest]";
        private const float Tolerance = 0.001f;
        private const string FloaterCommand = "fat_floater_census_test", FloaterTag = "[FatFloaterCensus]";
        private const string SheetCommand = "fat_sheet_probe_test", SheetTag = "[FatSheetProbe]";
        private const string SeamCommand = "fat_terrain_seam_test", SeamTag = "[FatTerrainSeam]";

        // Terrain seam probe (thin straight dark lines on a big levelled pad):
        // read-only. Every 32 m chunk border inside the radius: terrain rays every 0.5 m along it at +/-0.05 m either side. Reports
        // rays that hit nothing (holes), the largest height step across the border, the worst spots, and for the worst border the
        // voxel edit records (VoxelChunkAnchor) on both sides and whether each is applied.
        private static IEnumerable<string> TerrainSeams(float radius, Vector2? centre = null)
        {
            var inv = CultureInfo.InvariantCulture;
            Vector3 p = Player.m_localPlayer.transform.position;
            if (centre.HasValue) p = new Vector3(centre.Value.x, p.y, centre.Value.y);   // measure a spot you look AT from range (the quilt only shows from afar)
            int mask = LayerMask.GetMask("terrain");
            float C = VoxelChunk.CELLS;
            int holes = 0, rays = 0, borders = 0;
            float worst = 0f, worstAngle = 0f;
            var spots = new List<(float step, string where)>();
            for (int axis = 0; axis < 2; axis++)
            {
                // borders x = k*32 (axis 0, running along z) and z = k*32 (axis 1, running along x)
                float a0 = (axis == 0 ? p.x : p.z) - radius, a1 = (axis == 0 ? p.x : p.z) + radius;
                for (float b = Mathf.Ceil(a0 / C) * C; b <= a1; b += C)
                {
                    borders++;
                    float s0 = (axis == 0 ? p.z : p.x) - radius, s1 = (axis == 0 ? p.z : p.x) + radius;
                    for (float s = s0; s <= s1; s += 0.5f)
                    {
                        Vector3 A = axis == 0 ? new Vector3(b - 0.05f, p.y + 60f, s) : new Vector3(s, p.y + 60f, b - 0.05f);
                        Vector3 B = axis == 0 ? new Vector3(b + 0.05f, p.y + 60f, s) : new Vector3(s, p.y + 60f, b + 0.05f);
                        rays += 2;
                        bool ha = Physics.Raycast(A, Vector3.down, out RaycastHit hA, 160f, mask), hb = Physics.Raycast(B, Vector3.down, out RaycastHit hB, 160f, mask);
                        if (!ha || !hb)
                        {
                            holes++;
                            if (spots.Count < 64) spots.Add((999f, string.Format(inv, "HOLE at ({0:0.0},{1:0.0}) side {2}", (A.x + B.x) * 0.5f, (A.z + B.z) * 0.5f, !ha ? "low" : "high")));
                            continue;
                        }
                        float step = Mathf.Abs(hA.point.y - hB.point.y);
                        if (step > worst) worst = step;
                        float ang = Vector3.Angle(hA.normal, hB.normal);   // a lighting seam shows as a normal break across the border
                        if (ang > worstAngle) worstAngle = ang;
                        if (step > 0.02f && spots.Count < 64) spots.Add((step, string.Format(inv, "step {0:0.000} m at ({1:0.0},{2:0.0}) y {3:0.00}/{4:0.00}", step, (A.x + B.x) * 0.5f, (A.z + B.z) * 0.5f, hA.point.y, hB.point.y)));
                    }
                }
            }
            yield return string.Format(inv, "{0} at ({1:0.0},{2:0.0}) r {3:0} m: {4} chunk border line(s), {5} rays; {6} HOLE pair(s) (a ray hit nothing); max height step across a border {7:0.0000} m",
                SeamTag, p.x, p.z, radius, borders, rays, holes, worst);
            yield return string.Format(inv, "{0}   max normal break across a border {1:0.0} deg (collider normals; > ~5 deg on flat ground reads as a light/dark line)", SeamTag, worstAngle);
            // COLLIDER face normals differ across any slight kink, so they can't
            // tell a lighting seam. This compares what the SHADER lights: the render meshes' vertex normals of the vertices both
            // neighbouring chunks emit on their shared 32 m plane (same position within 1 mm).
            {
                var allViews = new List<VoxelChunkView>(); VoxelWorld.CollectViews(allViews);
                var byKey = new Dictionary<Vector3Int, VoxelChunkView>();
                int pcx2 = Mathf.FloorToInt(p.x / C), pcz2 = Mathf.FloorToInt(p.z / C), rC = Mathf.CeilToInt(radius / C);
                foreach (var v in allViews) if (v != null && Mathf.Abs(v.Key.x - pcx2) <= rC && Mathf.Abs(v.Key.z - pcz2) <= rC) byKey[v.Key] = v;
                int matched = 0, unmatched = 0, pairs2 = 0; float maxPos = 0f, maxNrm = 0f; string worstNrm = "-";
                var angs = new List<float>(); var flatAngs = new List<float>();   // the distribution, flat ground on its own (one cliff outlier can hide the answer)
                foreach (var kv in byKey)
                    for (int d = 0; d < 2; d++)
                    {
                        var nk = kv.Key + (d == 0 ? new Vector3Int(1, 0, 0) : new Vector3Int(0, 0, 1));
                        if (!byKey.TryGetValue(nk, out var nb)) continue;
                        Mesh ma = kv.Value.GroundMesh, mb = nb.GroundMesh;
                        if (ma == null || mb == null || !ma.isReadable || !mb.isReadable) continue;
                        var va = ma.vertices; var na = ma.normals; var vb = mb.vertices; var nbm = mb.normals;
                        if (na.Length != va.Length || nbm.Length != vb.Length) continue;
                        pairs2++;
                        // a vertex both chunks emit (the duplicated border cells) sits at the same position in both meshes
                        var lookup = new Dictionary<(int, int, int), int>();
                        for (int i = 0; i < vb.Length; i++)
                            lookup[(Mathf.RoundToInt(vb[i].x * 1000f), Mathf.RoundToInt(vb[i].y * 1000f), Mathf.RoundToInt(vb[i].z * 1000f))] = i;
                        float plane = (d == 0 ? nk.x : nk.z) * C;
                        for (int i = 0; i < va.Length; i++)
                        {
                            float c0 = d == 0 ? va[i].x : va[i].z;
                            if (Mathf.Abs(c0 - plane) > 1.5f) continue;   // only the band next to the shared face
                            if (!lookup.TryGetValue((Mathf.RoundToInt(va[i].x * 1000f), Mathf.RoundToInt(va[i].y * 1000f), Mathf.RoundToInt(va[i].z * 1000f)), out int j)) { unmatched++; continue; }
                            matched++;
                            float ang = Vector3.Angle(na[i], nbm[j]);
                            angs.Add(ang); if ((na[i].y + nbm[j].y) * 0.5f > 0.9f) flatAngs.Add(ang);
                            if (ang > maxNrm) { maxNrm = ang; worstNrm = string.Format(inv, "({0:0.0},{1:0.0},{2:0.0})", va[i].x, va[i].y, va[i].z); }
                        }
                    }
                yield return string.Format(inv, "{0}   RENDER border vertices: {1} chunk pair(s), {2} vertex(es) both meshes emit, {3} within 1.5 m of the face on one side only (normal: quads owned by one chunk), max position gap {4:0.0000} m, max vertex-normal break {5:0.00} deg at {6}",
                    SeamTag, pairs2, matched, unmatched, maxPos, maxNrm, worstNrm);
                angs.Sort(); flatAngs.Sort();
                float Pct(List<float> l, float q) => l.Count == 0 ? 0f : l[Mathf.Clamp(Mathf.CeilToInt(q * l.Count) - 1, 0, l.Count - 1)];
                int flatOver5 = 0; foreach (var a in flatAngs) if (a > 5f) flatOver5++;
                yield return string.Format(inv, "{0}   RENDER normal-break distribution: all {1} vertex(es) p50 {2:0.00} p95 {3:0.00} max {4:0.00} deg; NEAR-FLAT ground (normal y > 0.9) {5} vertex(es) p50 {6:0.00} p95 {7:0.00} max {8:0.00} deg, {9} over 5 deg (a light/dark line needs many > 5 deg on flat ground)",
                    SeamTag, angs.Count, Pct(angs, 0.5f), Pct(angs, 0.95f), angs.Count > 0 ? angs[angs.Count - 1] : 0f, flatAngs.Count, Pct(flatAngs, 0.5f), Pct(flatAngs, 0.95f), flatAngs.Count > 0 ? flatAngs[flatAngs.Count - 1] : 0f, flatOver5);
            }
            // The far-terrain quilt (render-only tiles from the GENERATOR's height) drawn ABOVE the edited voxel ground pokes
            // through a levelled pad as dark slivers at range and vanishes up close when its tile hides. Every 2 m: is an active quilt
            // tile over this spot, and does its surface stand above the voxel ground here?
            int quiltCovered = 0, quiltAbove = 0; float quiltWorst = 0f; string quiltWhere = "", quiltKinds = "";
            for (float qx = p.x - radius; qx <= p.x + radius; qx += 2f)
                for (float qz = p.z - radius; qz <= p.z + radius; qz += 2f)
                {
                    if (!Physics.Raycast(new Vector3(qx, p.y + 60f, qz), Vector3.down, out RaycastHit hq, 160f, mask)) continue;
                    int nt = FarQuilt.ActiveTilesAt(qx, qz, out float qy, out string kinds);
                    if (nt == 0) continue;
                    quiltCovered++;
                    float over = qy - hq.point.y;
                    if (over > 0.05f) { quiltAbove++; if (over > quiltWorst) { quiltWorst = over; quiltWhere = string.Format(inv, "({0:0},{1:0}) quilt {2:0.00} over ground {3:0.00}", qx, qz, qy, hq.point.y); quiltKinds = kinds.Trim(); } }
                }
            yield return string.Format(inv, "{0}   far quilt: {1} of the 2 m grid points have an ACTIVE quilt tile over them, {2} where the quilt stands above the voxel ground (worst +{3:0.00} m at {4}, tile {5})", SeamTag, quiltCovered, quiltAbove, quiltWorst, quiltAbove > 0 ? quiltWhere : "-", quiltAbove > 0 ? quiltKinds : "-");
            spots.Sort((x, y) => y.step.CompareTo(x.step));
            foreach (var sp in spots.Take(6)) yield return SeamTag + "   " + sp.where;
            if (spots.Count > 0)
            {
                // the edit records either side of the worst spot (every layer near the surface)
                yield return SeamTag + "   (edit records near the worst spot: see the chunk keys in the next lines)";
                int cx = Mathf.FloorToInt(p.x / C), cz = Mathf.FloorToInt(p.z / C), cy = Mathf.FloorToInt(p.y / C);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 0; dy++)
                        {
                            var key = new Vector3Int(cx + dx, cy + dy, cz + dz);
                            if (VoxelChunkAnchor.TryGet(key, out var an) && an != null)
                                yield return string.Format(inv, "{0}   chunk ({1},{2},{3}) has an edit record, applied {4}", SeamTag, key.x, key.y, key.z, an.Applied ? "yes" : "NO");
                        }
            }
        }

        // Sheet probe (square patches, steps, pop-in, a dry sand square inside a lake): read-only,
        // the generated water sheet of every voxel chunk view in the 5x5 chunk columns around the player: drawn or not, its corner
        // heights (min/max), the chunk's state. The runner moves the player first (FDT goto); this only reads.
        private static IEnumerable<string> SheetProbe()
        {
            var inv = CultureInfo.InvariantCulture;
            var me = Player.m_localPlayer;
            Vector3 p = me.transform.position;
            int pcx = Mathf.FloorToInt(p.x / VoxelChunk.CELLS), pcz = Mathf.FloorToInt(p.z / VoxelChunk.CELLS);
            float gen = VoxelWaterSurface.SurfaceAt(p);
            yield return string.Format(inv, "{0} player ({1:0.0},{2:0.0},{3:0.0}) in chunk column ({4},{5}); generated surface here {6}",
                SheetTag, p.x, p.y, p.z, pcx, pcz, gen > -1000f ? gen.ToString("0.00", inv) : "none");
            var views = new List<VoxelChunkView>();
            VoxelWorld.CollectViews(views);
            for (int dz = 2; dz >= -2; dz--)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int cx = pcx + dx, cz = pcz + dz;
                    var sb = new System.Text.StringBuilder();
                    int layers = 0;
                    foreach (var v in views)
                    {
                        if (v == null || v.Key.x != cx || v.Key.z != cz) continue;
                        layers++;
                        float lo = float.MaxValue, hi = float.MinValue; int wet = 0, n = 0;
                        if (v.SheetCornerH != null)
                            foreach (float h in v.SheetCornerH) { n++; if (h > -1000f) { wet++; if (h < lo) lo = h; if (h > hi) hi = h; } }
                        string state = "no chunk";
                        if (VoxelWorld.TryGetChunk(v.Key, out var ch) && ch != null)
                            state = string.Format(inv, "{0}{1}{2}", ch.Kind, ch.PendingGenerate ? " PENDING" : "", ch.HasGenWater ? " genWater" : " noGenWater");
                        var r = v.WaterRenderer;
                        sb.AppendFormat(inv, " [y{0}: sheet {1} verts {2}, corners wet {3}/{4}{5}; {6}; view {7}]",
                            v.Key.y, r == null ? "none" : (r.enabled && r.gameObject.activeInHierarchy ? "DRAWN" : "hidden"), v.WaterVerts, wet, n,
                            wet > 0 ? string.Format(inv, ", level {0:0.00}..{1:0.00}", lo, hi) : "", state, v.gameObject.activeInHierarchy ? "active" : "INACTIVE");
                    }
                    yield return string.Format(inv, "{0}   ({1},{2}){3}: {4}", SheetTag, cx, cz, dx == 0 && dz == 0 ? " PLAYER" : "",
                        layers == 0 ? "NO VIEW (not built/streamed)" : sb.ToString());
                }
            // Seam check (the max seam gap between neighbouring sheets): every pair of side-by-side DRAWN sheets
            // in the same layer, along their shared edge: the sheet heights (both sides' corners) and the big-water field
            // (_FiresInlandField, one world sample both sheets' shaders read). The height gap must be 0 for a seam-free swell.
            var drawn = new Dictionary<Vector3Int, VoxelChunkView>();
            foreach (var v in views)
                if (v != null && v.WaterRenderer != null && v.WaterRenderer.enabled && v.SheetCornerH != null
                    && Mathf.Abs(v.Key.x - pcx) <= 2 && Mathf.Abs(v.Key.z - pcz) <= 2) drawn[v.Key] = v;
            int nC = VoxelChunk.CELLS + 1, pairs = 0;
            float maxH = 0f, maxField = 0f;
            bool fieldOn = Shader.GetGlobalFloat("_FiresInlandFieldOn") > 0.5f;
            var ftex = Shader.GetGlobalTexture("_FiresInlandField") as Texture2D;
            Vector4 fc = Shader.GetGlobalVector("_FiresInlandFieldCenter");
            float fe = Mathf.Max(Shader.GetGlobalFloat("_FiresInlandFieldExtent"), 1f);
            foreach (var kv in drawn)
                for (int d = 0; d < 2; d++)
                {
                    var nk = kv.Key + (d == 0 ? new Vector3Int(1, 0, 0) : new Vector3Int(0, 0, 1));
                    if (!drawn.TryGetValue(nk, out var nb)) continue;
                    pairs++;
                    for (int s = 0; s < nC; s++)
                    {
                        // east edge of A (cx = 32) vs west edge of B (cx = 0), or north edge (cz = 32) vs south edge (cz = 0)
                        int ia = d == 0 ? (nC - 1) + nC * s : s + nC * (nC - 1);
                        int ib = d == 0 ? 0 + nC * s : s;
                        float ha = kv.Value.SheetCornerH[ia], hb = nb.SheetCornerH[ib];
                        if (ha > -1000f && hb > -1000f && ha != 0f && hb != 0f) maxH = Mathf.Max(maxH, Mathf.Abs(ha - hb));
                        if (fieldOn && ftex != null)
                        {
                            float wx = (nk.x * VoxelChunk.CELLS) + (d == 0 ? 0f : s), wz = (nk.z * VoxelChunk.CELLS) + (d == 0 ? s : 0f);
                            float u = (wx - fc.x) / (2f * fe) + 0.5f, w = (wz - fc.z) / (2f * fe) + 0.5f;
                            // both sheets' shaders read this one world point (no per-sheet input), so the field cannot differ across the
                            // edge by construction; report the largest body weight seen on edges to show the field is live there
                            maxField = Mathf.Max(maxField, ftex.GetPixelBilinear(u, w).r);
                        }
                    }
                }
            yield return string.Format(inv, "{0} seam check: {1} neighbouring drawn sheet pair(s); max sheet height gap {2:0.0000} m on shared edges; big-water field {3} (largest body weight on the edges {4:0.00}; world-sampled, same on both sides by construction)",
                SheetTag, pairs, maxH, fieldOn ? "ON" : "off (_FiresInlandFieldOn = 0)", maxField);
        }

        // What the Floating.CustomFixedUpdate feed prefix runs on (many thousands of Floating updates/s).
        // Read-only; one FindObjectsOfType when asked.
        private static IEnumerable<string> FloaterCensus()
        {
            var inv = CultureInfo.InvariantCulture;
            var me = Player.m_localPlayer;
            Vector3 c = me != null ? me.transform.position : Vector3.zero;
            int asleep = 0, inFat = 0, items = 0;
            var rows = new Dictionary<string, int[]>();   // prefab -> {count, in FAT water, asleep, within 64 m}
            foreach (var f in UnityEngine.Object.FindObjectsOfType<Floating>())
            {
                if (f == null || !f.isActiveAndEnabled) continue;
                string name = f.gameObject.name.Replace("(Clone)", "").Trim();
                var rb = f.GetComponent<Rigidbody>();
                bool sleep = rb != null && rb.IsSleeping();
                bool wet = CustomWaterManager.ScannedLevelAt(f.transform.position, true) > float.MinValue;
                if (f.GetComponent<ItemDrop>() != null) items++;
                if (sleep) asleep++;
                if (wet) inFat++;
                if (!rows.TryGetValue(name, out var r)) rows[name] = r = new int[4];
                r[0]++; if (wet) r[1]++; if (sleep) r[2]++; if ((f.transform.position - c).sqrMagnitude < 64f * 64f) r[3]++;
            }
            int active = rows.Values.Sum(r => r[0]);
            int nc = WaterMeshCombiner.AllCombiners?.Count(x => x != null) ?? 0;
            yield return string.Format(inv, "{0} {1} active Floating ({2} ItemDrop, {3} asleep, {4} in FAT water); {5} water combiner(s) loaded; feed lookups ~{6:0}/s at 50 Hz",
                FloaterTag, active, items, asleep, inFat, nc, active * 50f / 4f);
            foreach (var kv in rows.OrderByDescending(kv => kv.Value[0]).Take(15))
                yield return string.Format(inv, "{0}   {1}: {2} (in FAT water {3}, asleep {4}, within 64 m of you {5})",
                    FloaterTag, kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]);
        }

        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand("fat_waterlevel_ab",
                "[radius=200] [n=400] - every FAT water lookup with the zone index OFF vs ON at the same points, same frame (read-only)",
                args =>
                {
                    if (Player.m_localPlayer == null) { args.Context?.AddString("[FatWaterLevelAB] join a world first."); return; }
                    string line = RunAB(args, out _);
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                },
                isCheat: true);
            new Terminal.ConsoleCommand(TestCommand,
                "[F5 test] [no relaunch] read-only, same frame: every FAT water lookup with the zone index OFF vs ON (1 mm), plus a rebuild leg. Args: [radius=200] [n=400] - fat_waterlevel_ab as a runner test (BEGIN/step/END lines + FiresTests\\" + TestCommand + ".txt)",
                args =>
                {
                    if (!Begin(TestCommand, TestTag)) return;
                    string line = RunAB(args, out bool pass);
                    Say(TestCommand, $"{TestTag} 1/1 waterlevel_ab {(pass ? "PASS" : "FAIL")} - {line}");
                    Say(TestCommand, $"{TestTag} END {(pass ? "PASS" : "FAIL")}: {(pass ? 1 : 0)} pass, {(pass ? 0 : 1)} fail, 0 skip in {Time.realtimeSinceStartup - s_testStarted:0.0} s");
                },
                isCheat: true);
            new Terminal.ConsoleCommand(CensusCommand,
                "[F5 test] [no relaunch] read-only: every placed FAT water body (combiner) and legacy water: count, place, size, kind, creator (read-only; runner form)",
                args =>
                {
                    if (!Begin(CensusCommand, CensusTag)) return;
                    foreach (var line in Census()) Say(CensusCommand, line);
                    Say(CensusCommand, $"{CensusTag} 1/1 census PASS - read-only listing");
                    Say(CensusCommand, $"{CensusTag} END PASS: 1 pass, 0 fail, 0 skip in {Time.realtimeSinceStartup - s_testStarted:0.0} s");
                },
                isCheat: true);
            new Terminal.ConsoleCommand(SeamCommand,
                "[F5 test] [no relaunch] read-only: terrain seams on chunk borders (holes, height steps, normal breaks, far-quilt tiles above the ground), [radius=40] [x z] (default: around you)",
                args =>
                {
                    if (!Begin(SeamCommand, SeamTag)) return;
                    float rad = args.Length > 1 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float rr) ? Mathf.Clamp(rr, 8f, 128f) : 40f;
                    Vector2? at = args.Length > 3 && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float ax) && float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float az) ? new Vector2(ax, az) : (Vector2?)null;
                    foreach (var line in TerrainSeams(rad, at)) Say(SeamCommand, line);
                    Say(SeamCommand, $"{SeamTag} 1/1 terrain_seam PASS - read-only listing");
                    Say(SeamCommand, $"{SeamTag} END PASS: 1 pass, 0 fail, 0 skip in {Time.realtimeSinceStartup - s_testStarted:0.0} s");
                },
                isCheat: true);
            new Terminal.ConsoleCommand(SheetCommand,
                "[F5 test] [no relaunch] read-only: the generated water sheet of each voxel chunk in the 5x5 columns around you (drawn, corner levels, chunk state). Move there first (goto).",
                args =>
                {
                    if (!Begin(SheetCommand, SheetTag)) return;
                    foreach (var line in SheetProbe()) Say(SheetCommand, line);
                    Say(SheetCommand, $"{SheetTag} 1/1 sheet_probe PASS - read-only listing");
                    Say(SheetCommand, $"{SheetTag} END PASS: 1 pass, 0 fail, 0 skip in {Time.realtimeSinceStartup - s_testStarted:0.0} s");
                },
                isCheat: true);
            new Terminal.ConsoleCommand(FloaterCommand,
                "[F5 test] [no relaunch] read-only: every active Floating by prefab (top 15), how many sit in FAT water or asleep, and the combiner count",
                args =>
                {
                    if (!Begin(FloaterCommand, FloaterTag)) return;
                    foreach (var line in FloaterCensus()) Say(FloaterCommand, line);
                    Say(FloaterCommand, $"{FloaterTag} 1/1 floater_census PASS - read-only listing");
                    Say(FloaterCommand, $"{FloaterTag} END PASS: 1 pass, 0 fail, 0 skip in {Time.realtimeSinceStartup - s_testStarted:0.0} s");
                },
                isCheat: true);
        }

        // The F5 contract (FDT FiresTests.cs) needs "[F5 test]" in the description,
        // BEGIN "…: N step(s), ~S s" and END "… skip in T s".
        private static float s_testStarted;

        private static bool Begin(string command, string tag)
        {
            s_testStarted = Time.realtimeSinceStartup;
            var me = Player.m_localPlayer;
            if (me == null) { Say(command, tag + " SKIP - join a world first."); return false; }
            string world = ZNet.World != null ? ZNet.World.m_name : "?";
            Say(command, string.Format(CultureInfo.InvariantCulture, "{0} BEGIN {1} {2} ({3}) on client '{4}' world '{5}' at ({6:0}, {7:0}): 1 step, ~5 s",
                tag, global::VerdantsAscent.FiresAdminTerrain.PluginName, global::VerdantsAscent.FiresAdminTerrain.PluginVersion,
                global::VerdantsAscent.Terrain.FatDllHash.Value, me.GetPlayerName(), world, me.transform.position.x, me.transform.position.z));
            return true;
        }

        private static void Say(string command, string line)
        {
            FiresLogger.LogInfo(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, command + ".txt"),
                    $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{System.Environment.NewLine}");
            }
            catch (System.Exception) { }
        }

        private static string RunAB(Terminal.ConsoleEventArgs args, out bool pass)
        {
            var me = Player.m_localPlayer;
            var inv = CultureInfo.InvariantCulture;
            float radius = args.Length > 1 && float.TryParse(args[1], NumberStyles.Float, inv, out float r) ? Mathf.Clamp(r, 10f, 2000f) : 200f;
            int n = args.Length > 2 && int.TryParse(args[2], out int k) ? Mathf.Clamp(k, 10, 5000) : 400;
            Vector3 c = me.transform.position;
            var pts = new List<Vector3>();
            int centres = 0;
            var combiners = WaterMeshCombiner.AllCombiners;
            if (combiners != null)
                foreach (var cb in combiners)
                {
                    if (cb == null) continue;
                    var b = cb.GetCombinedBounds();
                    if ((b.center - c).sqrMagnitude > radius * radius) continue;
                    pts.Add(new Vector3(b.center.x, b.max.y, b.center.z));                          // on the surface
                    pts.Add(new Vector3(b.min.x + 0.5f, b.max.y, b.min.z + 0.5f));                 // a corner, near the zone edge
                    // The bounds centre can be dry (disks are round and off-centre), so also sample on up to 3 disks,
                    // 0.3 m under the surface read there.
                    var disks = cb.WaterDisks;
                    if (disks != null)
                        for (int di = 0; di < disks.Count && di < 3; di++)
                        {
                            if (disks[di] == null) continue;
                            Vector3 dp = disks[di].Position;
                            float sh = cb.GetWaterSurfaceAt(new Vector3(dp.x, b.max.y, dp.z));
                            pts.Add(new Vector3(dp.x, sh > float.MinValue ? sh - 0.3f : dp.y, dp.z));
                        }
                    centres++;
                }
            int side = Mathf.Max(2, Mathf.RoundToInt(Mathf.Sqrt(n / 2f)));
            for (int iz = 0; iz < side; iz++)
                for (int ix = 0; ix < side; ix++)
                {
                    float x = c.x - radius + 2f * radius * ix / (side - 1), z = c.z - radius + 2f * radius * iz / (side - 1);
                    float g = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : c.y;
                    pts.Add(new Vector3(x, g + 0.5f, z));
                    pts.Add(new Vector3(x, ZoneSystem.c_WaterLevel, z));
                }
            float worst = 0f; int refDiff = 0, inWater = 0;
            var bad = new List<string>();
            foreach (var p in pts)
            {
                float sOff = CustomWaterManager.ScannedLevelAt(p, false), sOn = CustomWaterManager.ScannedLevelAt(p, true);
                var cOff = WaterMeshCombiner.GetCombinerAt(p, false); var cOn = WaterMeshCombiner.GetCombinerAt(p, true);
                var lOff = CustomWaterManager.GetWaterAt(p, false); var lOn = CustomWaterManager.GetWaterAt(p, true);
                if (sOff > float.MinValue) inWater++;
                float d = (sOff == float.MinValue && sOn == float.MinValue) ? 0f : Mathf.Abs(sOff - sOn);
                if (float.IsInfinity(d) || float.IsNaN(d)) d = 9999f;
                bool refsDiffer = !ReferenceEquals(cOff, cOn) || !ReferenceEquals(lOff, lOn);
                if (refsDiffer) refDiff++;
                if (d > worst) worst = d;
                if ((d > Tolerance || refsDiffer) && bad.Count < 5)
                    bad.Add(string.Format(inv, "({0:0.0},{1:0.0},{2:0.0}) level off {3:0.000} on {4:0.000}{5}", p.x, p.y, p.z, sOff, sOn,
                        refsDiffer ? " (a different water body found)" : ""));
            }
            // REBUILD LEG (the compare alone never exercises a rebuild): in ONE frame, shift one combiner's bounds 70 m east
            // (into another zone) through SetBoundsData, its anchor-restore path, which bumps BoundsVersion. Then the same OFF/ON compare at
            // the old and the new spot; the index must have rebuilt in between ("a combiner's bounds were set"); then restore the bounds.
            // Runtime-only and same-frame: no ZDO, mesh or world change, so there is nothing to clean up.
            // The skip says how many combiners were in range but dry at their bounds centre.
            int inRange = combiners?.Count(x => x != null && (x.GetCombinedBounds().center - c).sqrMagnitude <= radius * radius) ?? 0;
            string leg = string.Format(inv, "rebuild leg SKIP (no combiner within the radius wet at its bounds centre; {0} in range)", inRange);
            bool legOk = true;
            WaterMeshCombiner mover = null;
            if (combiners != null)
                foreach (var cb in combiners)
                    if (cb != null && (cb.GetCombinedBounds().center - c).sqrMagnitude <= radius * radius && cb.GetWaterSurfaceAt(new Vector3(cb.GetCombinedBounds().center.x, cb.GetCombinedBounds().max.y, cb.GetCombinedBounds().center.z)) > float.MinValue) { mover = cb; break; }
            if (mover != null)
            {
                var b0 = mover.GetCombinedBounds();
                var shift = new Vector3(70f, 0f, 0f);
                var oldSpot = new Vector3(b0.center.x, b0.max.y, b0.center.z);
                var newSpot = oldSpot + shift;
                CombinersAt(oldSpot);   // the index is current before the move
                int before = s_rebuilds;
                try
                {
                    mover.SetBoundsData(b0.min + shift, b0.max + shift);
                    s_rebuildFrame = -1;   // lift the once-a-frame cap so this same-frame check can see the rebuild
                    float newOff = CustomWaterManager.ScannedLevelAt(newSpot, false), newOn = CustomWaterManager.ScannedLevelAt(newSpot, true);
                    float oldOff = CustomWaterManager.ScannedLevelAt(oldSpot, false), oldOn = CustomWaterManager.ScannedLevelAt(oldSpot, true);
                    bool rebuilt = s_rebuilds > before && s_lastWhy == "a combiner's bounds were set";
                    float dNew = (newOff == float.MinValue && newOn == float.MinValue) ? 0f : Mathf.Abs(newOff - newOn);
                    float dOld = (oldOff == float.MinValue && oldOn == float.MinValue) ? 0f : Mathf.Abs(oldOff - oldOn);
                    // The disks stay where they are (GetWaterSurfaceAt also checks them), so the level at the new spot is not the point:
                    // the INDEX must follow the bounds (the combiner listed in the new spot's zone), and both paths must still agree.
                    bool indexed = CombinersAt(newSpot).Contains(mover);
                    legOk = rebuilt && indexed && dNew <= Tolerance && dOld <= Tolerance;
                    leg = string.Format(inv, "rebuild leg {0}: combiner at ({1:0},{2:0}) bounds moved 70 m east by SetBoundsData; index rebuilt {3} ({4}); listed in the new zone {5}; new spot off {6:0.000} on {7:0.000}, old spot off {8:0.000} on {9:0.000}",
                        legOk ? "PASS" : "FAIL", b0.center.x, b0.center.z, rebuilt ? "yes" : "NO", s_lastWhy, indexed ? "yes" : "NO", newOff, newOn, oldOff, oldOn);
                }
                finally { mover.SetBoundsData(b0.min, b0.max); s_rebuildFrame = -1; CombinersAt(oldSpot); }   // restored and re-indexed in the same frame
            }
            // A compare with no point in FAT water proves nothing.
            pass = worst <= Tolerance && refDiff == 0 && legOk && inWater > 0;
            if (inWater == 0) leg += "; NOTHING COMPARED: no sampled point is in FAT water, run it near placed water";
            return string.Format(inv,
                "[FatWaterLevelAB] {0} point(s) within {1:0} m ({2} combiner(s) sampled at centre + corner; {3} point(s) in FAT water): " +
                "level max |delta| {4:0.0000} m (tolerance {5:0.000}), {6} point(s) found a different water body; {9} - {7}{8}",
                pts.Count, radius, centres, inWater, worst, Tolerance, refDiff, pass ? "PASS" : "FAIL",
                bad.Count > 0 ? "; worst: " + string.Join(" | ", bad) : "", leg);
        }

        // ── fat_water_census_test: what placed water exists (for cleanup decisions) ───────────────────────────────────
        private static IEnumerable<string> Census()
        {
            var inv = CultureInfo.InvariantCulture;
            var combiners = WaterMeshCombiner.AllCombiners;
            var legacy = CustomWaterManager.AllWater;
            int nc = combiners?.Count(c => c != null) ?? 0, nl = legacy?.Count(w => w != null) ?? 0;
            yield return string.Format(inv, "{0} {1} water combiner(s), {2} legacy water(s) loaded (only bodies in loaded areas)", CensusTag, nc, nl);
            if (combiners == null) yield break;
            // grouped by 64 m zone, biggest groups first
            var groups = combiners.Where(c => c != null).GroupBy(c => KeyAt(c.GetCombinedBounds().center)).OrderByDescending(g => g.Count()).ToList();
            yield return string.Format(inv, "{0} {1} zone(s) hold them; the 10 fullest:", CensusTag, groups.Count);
            foreach (var g in groups.Take(10))
            {
                var first = g.First().GetCombinedBounds();
                yield return string.Format(inv, "{0}   zone at ({1:0},{2:0}): {3} combiner(s)", CensusTag, first.center.x, first.center.z, g.Count());
            }
            // How many hold no water at all, by creator object (the GameObject name each creation site gives it)
            int empty = 0;
            var bySite = new Dictionary<string, int[]>();   // site -> {count, empty}
            foreach (var cb in combiners)
            {
                if (cb == null) continue;
                cb.WetCells(out int w, out _);
                string site = string.Join("_", cb.CreatedBy.Split(new[] { " <- " }, System.StringSplitOptions.None)[0].Split('_').Take(2));   // drop the per-group id
                if (!bySite.TryGetValue(site, out var s)) bySite[site] = s = new int[2];
                s[0]++; if (w == 0) { s[1]++; empty++; }
            }
            yield return string.Format(inv, "{0} {1} of {2} combiner(s) hold NO wet cell (depth >= {3} m); by creator object:", CensusTag, empty, nc, WaterMeshCombiner.MinWaterDepth);
            foreach (var kv in bySite.OrderByDescending(kv => kv.Value[0]))
                yield return string.Format(inv, "{0}   {1}: {2} ({3} empty)", CensusTag, kv.Key, kv.Value[0], kv.Value[1]);
            int listed = 0;
            foreach (var cb in combiners)
            {
                if (cb == null) continue;
                if (++listed > 300) { yield return CensusTag + "   … (more not listed)"; break; }
                var b = cb.GetCombinedBounds();
                var nv = cb.GetComponentInParent<ZNetView>();
                var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
                long creator = zdo != null ? zdo.GetLong(ZDOVars.s_creator, 0L) : 0L;
                cb.WetCells(out int wc, out int tc);
                yield return string.Format(inv, "{0}   #{1} ({2:0.0},{3:0.0},{4:0.0}) size {5:0}x{6:0} m, {7}, zdo {8}, creator {9}, wet cells {10}/{11}, made by {12}",
                    CensusTag, listed, b.center.x, b.max.y, b.center.z, b.size.x, b.size.z, cb.CustomLiquidType,
                    zdo != null ? zdo.m_uid.ToString() : "none", creator != 0L ? creator.ToString(inv) : "none", wc, tc, cb.CreatedBy);
            }
        }
    }
}
