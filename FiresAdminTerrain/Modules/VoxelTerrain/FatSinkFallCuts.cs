using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // The world-generation half of the BIG waterfall sinkholes (FAT 0.2.805; split out of FatSinkFalls so the World Gen
    // Studio, which links FiresWorldGenBase, compiles it too: no game types here, only the plan, plain maths and a file).
    // Arm(...) before a water plan is made; BuildWaterPlan then calls Run(plan, seed), which (when armed) picks seeded runs,
    // truncates each at its cut node BEFORE the levels and the carve see it (the run then ends BASIN, so the channel below is
    // never made), logs a lake that lost its only inflow ("tarn"), and writes the sidecar when the arm gave it a path.
    //   * game, generated world: FiresWorldGenBase arms with FiresWorldRecords.PathFor(world, Suffix).
    //   * game, converted world: FiresWorldBake arms the same way, with the world's own Caves answer.
    //   * studio import: Program arms with no path before PlanImportedWorld; /bake writes Text() beside the _baked.bin and
    //     FiresGaeaStudio.Install copies it into the world's folder.
    // The file is "seed <n>" then "big x z flowX flowZ width" lines; Load refuses a file made for another seed.
    internal static class FatSinkFallCuts
    {
        internal const string Suffix = "_sinkfalls.txt";
        internal const float MinWidth = 4f, StartClear = 200f, EndClear = 150f, JunctionClear = 40f;

        // Big: (X,Z) is where the river ends, Flow its direction. LargeOverflow (0.2.806, faithful studio bakes): (X,Z) is the
        // sinkhole's centre, Flow points from it TO the river, Level is the river surface there; the river is not cut.
        internal struct Cut { public float X, Z, FlowX, FlowZ, Width, Level; public bool LargeOverflow; }

        internal static readonly List<Cut> Cuts = new List<Cut>();
        internal static int Seed;
        internal static int Generation;          // bumped on every Run/Load, so the client re-plans its domes and curtains
        internal static float Chance = 0.1f;     // the game sets it from 'Big river sinkhole chance'

        private static bool s_armed, s_caves;
        private static string s_path;

        internal static void Arm(string sidecarPath, bool caves) { s_armed = true; s_caves = caves; s_path = sidecarPath; }
        internal static void Disarm() { s_armed = false; s_path = null; }

        // ── room for a big sinkhole (0.2.859: shared by the planner below and FatSinkFalls.TryPlanBig, one rule) ──────────────
        // The pond floor sits RoomDepth under the ground, never under `floorMin`; the walls shrink on a shallow site; the dome
        // (walls + RoomCapShare x RoomDome over the floor) keeps RoomRingRock of rock under the LOWEST ground on a ring at
        // RoomDome + RoomRingRock, lowered to fit when it can, and the hole is at least RoomLeastDepth deep.
        internal const float RoomDepth = 30f, RoomDome = 24f, RoomCapShare = 0.3f, RoomWallMax = 10f, RoomWallMin = 4f,
                             RoomRock = 3f, RoomRingRock = 5f, RoomLeastDepth = 15f;
        internal enum Room { Fits, GroundTooLow, RingTooLow }

        internal static Room RoomFor(float surface, float ringLow, float floorMin, out float floor, out float walls)
        {
            floor = Math.Max(surface - RoomDepth, floorMin);
            walls = Clamp(surface - floor - RoomRock - RoomCapShare * RoomDome, RoomWallMin, RoomWallMax);
            if (surface < floorMin + RoomLeastDepth) return Room.GroundTooLow;
            float topOver = walls + RoomCapShare * RoomDome;
            if (floor + topOver > ringLow - RoomRingRock)
            {
                float lowered = ringLow - RoomRingRock - topOver;
                if (lowered < floorMin || surface - lowered < RoomLeastDepth) { floor = lowered; return Room.RingTooLow; }
                floor = lowered;
            }
            return Room.Fits;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        // 0.2.859 (Fire 10-08: "a later version picks higher ground"): the cut node is only taken where the sinkhole will
        // actually fit, judged on the planning ground (`ground`, metres, before the rivers are carved) with the shaft and the
        // dome ring placed exactly as TryPlanBig places them. Ring points over this run's own channel read the river bed
        // (its level - BedDepth), which the carve will dig there. Before 0.2.859 a run was cut at any node over the floor
        // limit and the server then skipped the sinkhole for lack of room, leaving the river ending in a dead end (SinkTestGen9
        // #1-#4). Worlds already generated keep their sidecar list; this applies to worlds generated or converted from now on.
        private const float BedDepth = 1.5f;
        private const int RoomRing = 16;

        private static Room NodeRoom(WaterConnection c, int i, Func<float, float, float> ground, out float surface, out float ringLow)
        {
            float x = c.Nodes[i * 4], z = c.Nodes[i * 4 + 1], width = c.Nodes[i * 4 + 2];
            float fx = x - c.Nodes[(i - 1) * 4], fz = z - c.Nodes[(i - 1) * 4 + 1], fl = (float)Math.Sqrt(fx * fx + fz * fz);
            surface = ringLow = float.NaN;
            if (fl < 1e-4f) return Room.GroundTooLow;
            fx /= fl; fz /= fl;
            float r = width * 0.5f + ShaftPad, cx = x + fx * (r - 1f), cz = z + fz * (r - 1f);
            surface = ground(cx, cz);
            for (int k = 0; k < 8; k++)
            {
                float a = k * (float)Math.PI / 4f;
                surface = Math.Max(surface, ground(cx + (float)Math.Cos(a) * r, cz + (float)Math.Sin(a) * r));
            }
            ringLow = float.MaxValue;
            float R = RoomDome + RoomRingRock, reach = width * 0.5f + 3f, reach2 = reach * reach;
            for (int k = 0; k < RoomRing; k++)
            {
                float a = k * (float)Math.PI * 2f / RoomRing, px = cx + (float)Math.Cos(a) * R, pz = cz + (float)Math.Sin(a) * R;
                float g = ground(px, pz);
                for (int j = 0; j <= i; j++)   // the kept channel (upstream of the cut) crosses the ring: its bed, not the bank
                {
                    float dx = c.Nodes[j * 4] - px, dz = c.Nodes[j * 4 + 1] - pz;
                    if (dx * dx + dz * dz < reach2) { g = Math.Min(g, c.Nodes[j * 4 + 3] - BedDepth); break; }
                }
                ringLow = Math.Min(ringLow, g);
            }
            return RoomFor(surface, ringLow, FatSinkFalls.BigFloorMin, out _, out _);
        }

        internal static void Run(FiresWaterPlan plan, int seed, Func<float, float, float> ground = null)
        {
            if (!s_armed) return;   // studio non-import paths, a plan the caller did not arm: nothing is cut
            Cuts.Clear();
            Seed = seed;
            Generation++;
            int tarns = 0, noRoom = 0;
            if (plan != null && Chance > 0f && s_caves)
            {
                var canyonRuns = new HashSet<int>();
                foreach (var cr in plan.Canyons) canyonRuns.Add(cr.ConnectionId);
                for (int ci = 0; ci < plan.Connections.Count; ci++)
                {
                    var c = plan.Connections[ci];
                    int count = c.Nodes != null ? c.NodeCount : 0;
                    if (count < 3 || canyonRuns.Contains(c.Id)) continue;
                    if (Hash01(seed, c.Id, 0x5FA) >= Chance) continue;
                    var along = new float[count];
                    for (int i = 1; i < count; i++)
                        along[i] = along[i - 1] + Dist(c.Nodes[(i - 1) * 4], c.Nodes[(i - 1) * 4 + 1], c.Nodes[i * 4], c.Nodes[i * 4 + 1]);
                    float total = along[count - 1];
                    int start = (int)(Hash01(seed, c.Id, 0x5FB) * count), k = -1;
                    var fits = ground != null ? new List<(int node, float surface)>() : null;
                    for (int t = 0; t < count && (k < 0 || fits != null); t++)
                    {
                        int i = (start + t) % count;
                        if (i == 0 || i == count - 1 || along[i] < StartClear || total - along[i] < EndClear || c.Nodes[i * 4 + 2] < MinWidth) continue;
                        if (c.Nodes[i * 4 + 3] < FatSinkFalls.MinBigSurface) continue;   // 0.2.834: a river too low for a sinkhole floor over Cave Min Y
                        if (NearOtherRun(plan, ci, c.Nodes[i * 4], c.Nodes[i * 4 + 1]) || LosesATributary(plan, ci, c, i)) continue;
                        if (fits == null) { k = i; continue; }
                        if (NodeRoom(c, i, ground, out float surface, out _) == Room.Fits) fits.Add((i, surface));
                    }
                    if (fits != null)
                    {
                        // 0.2.859: higher ground first. Of the nodes with room, the seeded pick is made among the higher half
                        // (by ground at the shaft), so the hole sits up the valley where the dome has rock round it.
                        if (fits.Count == 0)
                        {
                            noRoom++;
                            FiresLogger.LogDebug($"[SinkFall] big: run {c.Id} has no node with room for a sinkhole (ground or the dome ring too low); it keeps its whole river");
                            continue;
                        }
                        fits.Sort((p, q) => q.surface.CompareTo(p.surface));
                        int top = (fits.Count + 1) / 2;
                        k = fits[Math.Min(top - 1, (int)(Hash01(seed, c.Id, 0x5FC) * top))].node;
                    }
                    if (k < 0) continue;
                    float ax = c.Nodes[k * 4], az = c.Nodes[k * 4 + 1];
                    float fx = ax - c.Nodes[(k - 1) * 4], fz = az - c.Nodes[(k - 1) * 4 + 1], fl = (float)Math.Sqrt(fx * fx + fz * fz);
                    if (fl < 1e-4f) continue;
                    float width = c.Nodes[k * 4 + 2];
                    var kept = new float[(k + 1) * 4];
                    Array.Copy(c.Nodes, kept, kept.Length);
                    c.Nodes = kept;
                    c.MinX = c.MinZ = float.MaxValue; c.MaxX = c.MaxZ = float.MinValue;
                    for (int i = 0; i <= k; i++)
                    {
                        c.MinX = Math.Min(c.MinX, kept[i * 4]); c.MaxX = Math.Max(c.MaxX, kept[i * 4]);
                        c.MinZ = Math.Min(c.MinZ, kept[i * 4 + 1]); c.MaxZ = Math.Max(c.MaxZ, kept[i * 4 + 1]);
                    }
                    int lost = c.ToBody;
                    c.ToBody = WaterConnection.BASIN;
                    Cuts.Add(new Cut { X = ax, Z = az, FlowX = fx / fl, FlowZ = fz / fl, Width = width });
                    FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                        "[SinkFall] big: run {0} ends in a sinkhole at ({1:0},{2:0}), width {3:0.0} m, {4:0} m from its start; {5:0} m below dropped",
                        c.Id, ax, az, width, along[k], total - along[k]));
                    if (lost >= 0 && !HasOtherInflow(plan, ci, lost))
                    {
                        tarns++;
                        FiresLogger.LogDebug($"[SinkFall] tarn: lake {lost} lost its only inflow (run {c.Id} now ends in the big sinkhole at ({ax:0},{az:0}))");
                    }
                }
            }
            if (s_path != null) Write(s_path);
            FiresLogger.LogDebug($"[SinkFall] big: {Cuts.Count} river(s) end in a sinkhole, {tarns} tarn(s)"
                + (ground != null ? $", {noRoom} picked river(s) had no room anywhere (kept whole)" : ", room not judged (no planning ground)")
                + (s_path != null ? $"; written to {Path.GetFileName(s_path)}" : ""));
        }

        internal static string Text()
        {
            var sb = new StringBuilder("# FAT big waterfall sinkholes: seed, then big x z flowX flowZ width / large x z toRiverX toRiverZ width level\n");
            sb.Append("seed ").Append(Seed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var b in Cuts)
                sb.Append(b.LargeOverflow
                    ? string.Format(CultureInfo.InvariantCulture, "large {0:R} {1:R} {2:R} {3:R} {4:R} {5:R}\n", b.X, b.Z, b.FlowX, b.FlowZ, b.Width, b.Level)
                    : string.Format(CultureInfo.InvariantCulture, "big {0:R} {1:R} {2:R} {3:R} {4:R}\n", b.X, b.Z, b.FlowX, b.FlowZ, b.Width));
            return sb.ToString();
        }

        // ── LARGE OVERFLOW (0.2.806; Fire 10-06): a faithful studio bake's rivers are burned into its heights with no plan, so
        // nothing can be cut. Instead a large sinkhole opens BESIDE a river (sized like the big kind) and the river keeps
        // flowing; its side pours over the hole's lip. Picked at /bake from the bake's own arrays (the game cannot ask a
        // faithful bake where its water is): one seeded roll per OverflowCell m cell; the cell's lowest-hash river samples are
        // tried until one has dry ground beside it that holds the whole shaft plus a LipRock rock lip, clear of every water
        // cell. `waterClass` 2 = river (the studio's WC_RIVER); `water` is the surface level, `heights` the ground.
        internal const float OverflowCell = 400f, OverflowChance = 0.15f, LipRock = 3f, ShaftPad = 5f, SeaLevel = 30f;
        private const int OverflowTries = 6, Directions = 16;
        private struct Sample { public float H; public int I; }

        internal static int PlanOverflows(float[] heights, byte[] waterClass, float[] water, int n, float half, int seed)
        {
            Cuts.RemoveAll(c => c.LargeOverflow);
            if (heights == null || waterClass == null || water == null || n < 3) return 0;
            Seed = seed;
            Generation++;
            float cellM = 2f * half / (n - 1);
            int stride = Math.Max(1, (int)(8f / cellM));
            int cellsPerSide = (int)Math.Ceiling(2f * half / OverflowCell);
            // Per site cell: the OverflowTries river samples with the lowest hash (seeded, so a bake re-made picks the same).
            var best = new Dictionary<int, List<Sample>>();
            for (int z = 1; z < n - 1; z += stride)
                for (int x = 1; x < n - 1; x += stride)
                {
                    int i = z * n + x;
                    if (waterClass[i] != 2) continue;
                    float wx = x * cellM - half, wz = z * cellM - half;
                    int cx = (int)((wx + half) / OverflowCell), cz = (int)((wz + half) / OverflowCell), key = cz * cellsPerSide + cx;
                    if (Hash01(seed, key, 0x6A1) >= OverflowChance) continue;
                    float h = Hash01(seed, i, 0x6A2);
                    if (!best.TryGetValue(key, out var list)) best[key] = list = new List<Sample>(OverflowTries + 1);
                    if (list.Count == OverflowTries && h >= list[list.Count - 1].H) continue;
                    int at = list.FindIndex(e => e.H > h);
                    list.Insert(at < 0 ? list.Count : at, new Sample { H = h, I = i });
                    if (list.Count > OverflowTries) list.RemoveAt(list.Count - 1);
                }
            bool Wet(int x, int z) { int i = z * n + x; return waterClass[i] != 0 || water[i] > heights[i] + 0.05f; }
            int placed = 0;
            foreach (var kv in best)
                foreach (var cand in kv.Value)
                {
                    int rx = cand.I % n, rz = cand.I / n;
                    // The nearest bank (march to dry ground) and the river's width across it.
                    float bestD = float.MaxValue, dirX = 0f, dirZ = 0f;
                    for (int k = 0; k < Directions; k++)
                    {
                        double a = k * Math.PI * 2 / Directions;
                        float dx = (float)Math.Cos(a), dz = (float)Math.Sin(a);
                        float d = March(rx, rz, dx, dz, cellM, n, Wet);
                        if (d < bestD) { bestD = d; dirX = dx; dirZ = dz; }
                    }
                    if (bestD == float.MaxValue) continue;
                    float back = March(rx, rz, -dirX, -dirZ, cellM, n, Wet);
                    if (back == float.MaxValue) continue;
                    float width = bestD + back;
                    if (width < MinWidth) continue;
                    float r = width * 0.5f + ShaftPad;
                    float ex = rx * cellM - half + dirX * bestD, ez = rz * cellM - half + dirZ * bestD;   // the bank
                    float sx = ex + dirX * (LipRock + r), sz = ez + dirZ * (LipRock + r);                 // the shaft's centre
                    if (!DryDisk(sx, sz, r + LipRock - 0.5f, heights, n, half, cellM, Wet)) continue;
                    bool clash = false;
                    foreach (var c in Cuts) if (Dist(c.X, c.Z, sx, sz) < 2f * OverflowCell * 0.25f) { clash = true; break; }
                    if (clash) continue;
                    float level = water[cand.I];
                    if (level <= SeaLevel + 1f) continue;   // a river above the sea, never an estuary
                    Cuts.Add(new Cut { X = sx, Z = sz, FlowX = -dirX, FlowZ = -dirZ, Width = width, Level = level, LargeOverflow = true });
                    placed++;
                    FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                        "[SinkFall] large-overflow: planned at ({0:0},{1:0}) beside a {2:0.0} m river (surface {3:0.0}), shaft r {4:0.0}",
                        sx, sz, width, level, r));
                    break;
                }
            FiresLogger.LogDebug($"[SinkFall] large-overflow: {placed} sinkhole(s) beside rivers in {best.Count} rolled cell(s)");
            return placed;
        }

        // Metres from the river cell to the first dry cell along (dx, dz); MaxValue when none within 40 m.
        private static float March(int rx, int rz, float dx, float dz, float cellM, int n, Func<int, int, bool> wet)
        {
            for (float d = cellM; d <= 40f; d += cellM * 0.5f)
            {
                int x = rx + (int)Math.Round(dx * d / cellM), z = rz + (int)Math.Round(dz * d / cellM);
                if (x < 1 || z < 1 || x >= n - 1 || z >= n - 1) return float.MaxValue;
                if (!wet(x, z)) return d;
            }
            return float.MaxValue;
        }

        private static bool DryDisk(float cx, float cz, float r, float[] heights, int n, float half, float cellM, Func<int, int, bool> wet)
        {
            int x0 = (int)((cx - r + half) / cellM), x1 = (int)((cx + r + half) / cellM);
            int z0 = (int)((cz - r + half) / cellM), z1 = (int)((cz + r + half) / cellM);
            if (x0 < 1 || z0 < 1 || x1 >= n - 1 || z1 >= n - 1) return false;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    float wx = x * cellM - half, wz = z * cellM - half;
                    if ((wx - cx) * (wx - cx) + (wz - cz) * (wz - cz) > r * r) continue;
                    if (wet(x, z) || heights[z * n + x] < SeaLevel + 2f) return false;
                }
            return true;
        }

        internal static bool Write(string path)
        {
            try { File.WriteAllText(path, Text()); return true; }
            catch (Exception ex) { FiresLogger.LogWarning($"[SinkFall] could not write {path}: {ex.Message} - the big sinkholes will be missing after a reload."); return false; }
        }

        // A cache load: the cuts this world was planned with (none for a world made before 0.2.803, a faithful or a file for
        // another seed). A studio bake carries the seed it was imported with, which is the world's seed only by chance, so
        // a bake's file (seedless check) is accepted by name: `anySeed` true.
        internal static void Load(string path, int seed, bool anySeed)
        {
            Cuts.Clear();
            Generation++;
            try
            {
                if (!File.Exists(path)) return;
                int fileSeed = int.MinValue;
                foreach (var line in File.ReadAllLines(path))
                {
                    var f = line.Split(' ');
                    if (f.Length == 2 && f[0] == "seed") { int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out fileSeed); continue; }
                    bool large = f.Length == 7 && f[0] == "large";
                    if (!large && (f.Length != 6 || f[0] != "big")) continue;
                    float P(int i) => float.Parse(f[i], CultureInfo.InvariantCulture);
                    Cuts.Add(new Cut { X = P(1), Z = P(2), FlowX = P(3), FlowZ = P(4), Width = P(5), Level = large ? P(6) : 0f, LargeOverflow = large });
                }
                if (!anySeed && fileSeed != int.MinValue && fileSeed != seed)
                {
                    FiresLogger.LogDebug($"[SinkFall] big: {Path.GetFileName(path)} was made for seed {fileSeed}, not {seed} - ignored");
                    Cuts.Clear();
                    return;
                }
                Seed = seed;
                FiresLogger.LogDebug($"[SinkFall] big: {Cuts.Count} sinkhole(s) read from {Path.GetFileName(path)}");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[SinkFall] could not read {path}: {ex.Message} - no big sinkholes this session."); Cuts.Clear(); }
        }

        private static bool NearOtherRun(FiresWaterPlan plan, int self, float x, float z)
        {
            float r2 = JunctionClear * JunctionClear;
            for (int j = 0; j < plan.Connections.Count; j++)
            {
                if (j == self) continue;
                var o = plan.Connections[j];
                if (o.Nodes == null || x < o.MinX - JunctionClear || x > o.MaxX + JunctionClear || z < o.MinZ - JunctionClear || z > o.MaxZ + JunctionClear) continue;
                for (int i = 0; i < o.NodeCount; i++)
                {
                    float dx = o.Nodes[i * 4] - x, dz = o.Nodes[i * 4 + 1] - z;
                    if (dx * dx + dz * dz < r2) return true;
                }
            }
            return false;
        }

        // A tributary that ends on this run below the cut would be left pouring into a channel that is never made.
        private static bool LosesATributary(FiresWaterPlan plan, int self, WaterConnection c, int k)
        {
            float r2 = JunctionClear * JunctionClear;
            for (int j = 0; j < plan.Connections.Count; j++)
            {
                var o = plan.Connections[j];
                if (j == self || o.ToBody != WaterConnection.CONFLUENCE || o.Nodes == null || o.NodeCount < 1) continue;
                float ex = o.Nodes[(o.NodeCount - 1) * 4], ez = o.Nodes[(o.NodeCount - 1) * 4 + 1];
                if (ex < c.MinX - JunctionClear || ex > c.MaxX + JunctionClear || ez < c.MinZ - JunctionClear || ez > c.MaxZ + JunctionClear) continue;
                for (int i = k; i < c.NodeCount; i++)
                {
                    float dx = c.Nodes[i * 4] - ex, dz = c.Nodes[i * 4 + 1] - ez;
                    if (dx * dx + dz * dz < r2) return true;
                }
            }
            return false;
        }

        private static bool HasOtherInflow(FiresWaterPlan plan, int self, int body)
        {
            for (int j = 0; j < plan.Connections.Count; j++)
                if (j != self && plan.Connections[j].ToBody == body) return true;
            return false;
        }

        // 0.2.814 (Fire 10-06, overruling 0.2.813's dry shaft: "go back to the full mesh over the river, stop the swim trigger at
        // or just below the river bed height where the sinkhole drops in"): a BIG sinkhole's shaft footprint (geometry as
        // FatSinkFalls.TryPlanBig: r = width / 2 + ShaftPad, centred r - 1 downstream of the cut, + ShaftMargin) and the river
        // BED at its lip (the generated ground under the cut). The river's sheet and surface stay as they are; only the swim
        // query (CustomWaterManager.GetWaterLevelAt) ignores the generated river for a point in the shaft BELOW the bed, so the
        // shaft is air from the bed down to the pond, whose own sheet is the water there. Thread-safe: immutable arrays,
        // swapped when the cuts change; the bed heights are sampled once the generated world is up.
        private const float ShaftMargin = 0.5f;
        private static float[] s_shafts = new float[0];   // x, z, r^2, bed per shaft (bed NaN until sampled)
        private static int s_shaftsFor = -1;

        internal static bool InShaft(float x, float z) => InShaft(x, z, out _);

        internal static bool InShaft(float x, float z, out float bed)
        {
            bed = float.NaN;
            if (s_shaftsFor != Generation) RebuildShafts();
            var shafts = s_shafts;
            for (int i = 0; i + 3 < shafts.Length; i += 4)
            {
                float dx = x - shafts[i], dz = z - shafts[i + 1];
                if (dx * dx + dz * dz >= shafts[i + 2]) continue;
                if (float.IsNaN(shafts[i + 3]) && FiresWorldGenBase.Active) shafts[i + 3] = BedAt(i / 4);
                bed = shafts[i + 3];
                return true;
            }
            return false;
        }

        // 0.2.821 (FatShaftUnderwater): which shaft (index into the shaft list) covers (x, z), or -1; and a shaft's centre,
        // radius (without the margin) and river bed.
        internal static int ShaftIndexAt(float x, float z)
        {
            if (s_shaftsFor != Generation) RebuildShafts();
            var shafts = s_shafts;
            for (int i = 0; i + 3 < shafts.Length; i += 4)
            {
                float dx = x - shafts[i], dz = z - shafts[i + 1];
                if (dx * dx + dz * dz < shafts[i + 2]) return i / 4;
            }
            return -1;
        }

        internal static bool TryShaft(int index, out Vector2 centre, out float radius, out float bed)
        {
            centre = Vector2.zero; radius = 0f; bed = float.NaN;
            if (s_shaftsFor != Generation) RebuildShafts();
            var shafts = s_shafts;
            int i = index * 4;
            if (index < 0 || i + 3 >= shafts.Length) return false;
            centre = new Vector2(shafts[i], shafts[i + 1]);
            radius = Mathf.Sqrt(shafts[i + 2]) - ShaftMargin;
            if (float.IsNaN(shafts[i + 3]) && FiresWorldGenBase.Active) shafts[i + 3] = BedAt(index);
            bed = shafts[i + 3];
            return !float.IsNaN(bed);
        }

        // True when p is inside a big shaft and below its river bed (less `margin`): the generated river is not water there.
        internal static bool BelowShaftBed(Vector3 p, float margin)
            => InShaft(p.x, p.z, out float bed) && !float.IsNaN(bed) && p.y < bed - margin;

        private static float BedAt(int shaft)
        {
            lock (Cuts)
            {
                int k = 0;
                foreach (var c in Cuts)
                {
                    if (c.LargeOverflow || !Fits(c)) continue;   // 0.2.834: no room = no shaft
                    if (k++ == shaft) return FiresWorldGenBase.SampleFinalMeters(c.X, c.Z);
                }
            }
            return float.NaN;
        }

        private static void RebuildShafts()
        {
            var list = new List<float>();
            lock (Cuts)
            {
                foreach (var c in Cuts)
                {
                    if (c.LargeOverflow || !Fits(c)) continue;   // 0.2.834: no room = no shaft
                    float r = c.Width * 0.5f + ShaftPad;
                    float cx = c.X + c.FlowX * (r - 1f), cz = c.Z + c.FlowZ * (r - 1f);
                    float rr = r + ShaftMargin;
                    list.Add(cx); list.Add(cz); list.Add(rr * rr); list.Add(float.NaN);
                }
                s_shafts = list.ToArray();
                s_shaftsFor = Generation;
            }
        }

        // 0.2.834: a big cut whose sinkhole has room under its ground (FatSinkFalls.TryPlanBig's own test: the highest ground at
        // the shaft's centre and on its rim). One that has none is never built, so it cuts no shaft out of the river either.
        internal static bool Fits(in Cut c)
        {
            if (c.LargeOverflow) return true;
            // 0.2.845: the planner's whole verdict (room under the ground AND the dome's side walls), so a site the server skips
            // never cuts a shaft out of the river or hangs a curtain on any peer.
            int index = Cuts.IndexOf(c);
            if (index >= 0 && FiresWorldGenBase.Active && WorldGenerator.instance != null) return FatSinkFalls.TryPlanBig(index, out _);
            float r = c.Width * 0.5f + ShaftPad, cx = c.X + c.FlowX * (r - 1f), cz = c.Z + c.FlowZ * (r - 1f);
            float surface = FiresWorldGenBase.SampleFinalMeters(cx, cz);
            for (int i = 0; i < 8; i++)
            {
                float a = i * (float)Math.PI / 4f;
                surface = Math.Max(surface, FiresWorldGenBase.SampleFinalMeters(cx + (float)Math.Cos(a) * r, cz + (float)Math.Sin(a) * r));
            }
            return surface >= FatSinkFalls.MinBigSurface;
        }

        private static float Dist(float ax, float az, float bx, float bz) => (float)Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));

        // The same hash FatSinkFalls used in 0.2.803/0.2.804 (seed, (id, 0), salt), so a seed picks the same runs.
        private static float Hash01(int seed, int id, int salt)
        {
            unchecked
            {
                uint h = (uint)(seed * 73856093 ^ id * 19349663 ^ 0 * 83492791 ^ salt * 2654435761);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }
    }
}
