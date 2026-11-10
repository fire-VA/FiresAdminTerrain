using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    // Calm still-water sheets for anything that carves a bed and wants it filled (cave water beyond
    // just pools, carved and filled with the calm sheet mesh, never poured; the cave content + the V-tools).
    // A sheet is one water disk flat at its surface height, on the calm Custom/FiresInlandWater material with no waves; its mesh is
    // clipped to the voxel floor under it, so it fills exactly the carved bed below that height and never pours or spreads.
    // It is created through WaterOperationRPC, so on the server it is applied, saved to its anchor and broadcast, and a client
    // request goes to the server the same way. Sheet ids are a hash of the quantized centre / level / radius: the same call gives
    // the same id on every peer and every run, and a repeated call finds the sheet and adds nothing.
    public static class WaterSheets
    {
        private const float FloorReach = 32f;          // how far under the surface the bed is looked for
        private const float DepthMargin = 0.5f;        // the disk reaches this far under the deepest bed it was laid over
        private const float ChannelSpacing = 0.6f;     // channel disks every radius * this
        private const float IndexCell = 32f;
        private const float IndexMaxAge = 1f;
        private const float BlendBand = 0.5f;   // FreshSurfaceAt blends only disks this close to the highest one

        // A calm sheet at surfaceY over the bed under centre. Returns its id (> 0), or 0 with the reason.
        public static int Lay(Vector3 centre, float surfaceY, float radius, string why, out string refusal)
        {
            refusal = null;
            if (float.IsNaN(surfaceY) || float.IsInfinity(surfaceY) || float.IsNaN(centre.x) || float.IsNaN(centre.z) || radius < 0.25f || radius > 64f)
            {
                refusal = F("bad numbers (surface {0}, radius {1})", surfaceY, radius);
                return 0;
            }
            if (!VoxelWorld.TrySampleWaterFloorY(centre.x, centre.z, surfaceY + 0.05f, surfaceY - FloorReach, out float bed, out bool wall) || wall)
            {
                refusal = F("no voxel floor under ({0:0.0},{1:0.0}) within {2:0} m below {3:0.00}", centre.x, centre.z, FloorReach, surfaceY);
                return 0;
            }
            int id = SheetId(centre, surfaceY, radius);
            if (FindDisk(id, out _)) return id;   // already laid (this run, or restored from the world)

            var at = new Vector3(centre.x, surfaceY, centre.z);
            var disk = new WaterDisk(at, radius, surfaceY - bed + DepthMargin, CustomLiquidType.Water, id) { Kind = WaterDiskKind.Pond };   // 0.2.1091: a calm sheet keeps its level
            try { WaterOperationRPC.RequestCreateWaterWithCustomType(at, disk, CustomLiquidType.Water); }
            catch (System.Exception ex) { refusal = "the water request threw: " + ex.Message; return 0; }
            s_indexBuilt = -100f;
            FiresLogger.LogDebug(F("[FiresWater] sheet laid at ({0:0.0},{1:0.00},{2:0.0}) r {3:0.0}, bed {4:0.00} (id {5}; {6})",
                at.x, at.y, at.z, radius, bed, id, string.IsNullOrEmpty(why) ? "no reason given" : why));
            return id;
        }

        // Sheets along a carved channel: one disk every radius * ChannelSpacing, each at its own level (spine[i] -> surfaceY[i],
        // interpolated between points). Returns how many were laid or already there.
        public static int LayChannel(IList<Vector3> spine, IList<float> surfaceY, float radius, string why, float spacing = ChannelSpacing)   // spacing (radius x), cave rivers lay denser
        {
            if (spine == null || surfaceY == null || spine.Count == 0 || spine.Count != surfaceY.Count) return 0;
            int n = 0;
            float step = Mathf.Max(0.25f, radius * spacing);
            if (Lay(spine[0], surfaceY[0], radius, why, out _) > 0) n++;
            for (int i = 1; i < spine.Count; i++)
            {
                Vector3 a = spine[i - 1], b = spine[i];
                float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                int parts = Mathf.Max(1, Mathf.CeilToInt(len / step));
                for (int k = 1; k <= parts; k++)
                {
                    float t = k / (float)parts;
                    if (Lay(Vector3.Lerp(a, b, t), Mathf.Lerp(surfaceY[i - 1], surfaceY[i], t), radius, why, out _) > 0) n++;
                }
            }
            return n;
        }

        // Removes one sheet (synced). False when no sheet has that id here.
        public static bool Remove(int id)
        {
            if (id == 0 || !FindDisk(id, out _)) return false;
            WaterOperationRPC.RequestRemoveWater(id);
            s_indexBuilt = -100f;
            return true;
        }

        // The highest placed-water surface over (x, z) inside [yLow, yHigh], or NaN: the wet band's query. Any placed disk counts
        // (sheets and the water tools' disks). Reads a snapshot index, safe from any thread; the main thread refreshes it.
        public static float SurfaceAt(float x, float z, float yLow, float yHigh)
        {
            if (IsMainThread && Time.realtimeSinceStartup - s_indexBuilt > IndexMaxAge) Rebuild();
            var index = s_index;
            if (index == null || !index.TryGetValue(CellKey(x, z), out var list)) return float.NaN;
            // 0.2.1091: fresh water answers from PlacedWaterField (the drawn, bank-capped surface; one implementation); other
            // placed liquids (lava, ice, snow disks) keep their disk level.
            float best = float.NaN;
            bool anyWater = false;
            foreach (var d in list)
            {
                if (d.Y < yLow || d.Y > yHigh) continue;
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz > d.R * d.R) continue;
                if (d.Water) { anyWater = true; continue; }
                if (float.IsNaN(best) || d.Y > best) best = d.Y;
            }
            if (anyWater && PlacedWaterField.TrySurface(x, z, yLow, yHigh, PlacedWaterField.Consumer.Paint, out float field, out _)
                && (float.IsNaN(best) || field > best)) best = field;
            return best;
        }

        // The surface the skyland-builder render of placed fresh water uses (PlacedWaterSheets): over (x, z) inside
        // [yLow, yHigh], a blend of the covering fresh-water disks weighted (1 - d/R)^2 by distance from each centre, so a single
        // disk is flat at its height and overlapping dabs at different heights (a pour down a slope) ramp smoothly instead of
        // stepping. NaN where no fresh-water disk covers the point.
        internal static float FreshSurfaceAt(float x, float z, float yLow, float yHigh)
        {
            if (IsMainThread && Time.realtimeSinceStartup - s_indexBuilt > IndexMaxAge) Rebuild();
            var index = s_index;
            if (index == null || !index.TryGetValue(CellKey(x, z), out var list)) return float.NaN;
            // A cave pool disk at 38.79 inside a river's 43.06 sheets would blend to
            // 41.1, chunk surfaces 2 m apart with 4 m skirts = "mini waterfalls". So only disks within BlendBand of the HIGHEST disk
            // covering the point blend; a lower disk under a higher one is just that water's bed.
            float top = float.NegativeInfinity;
            bool topPond = false;
            foreach (var d in list)
            {
                if (!d.Water || d.Y < yLow || d.Y > yHigh) continue;
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz > d.R * d.R) continue;
                if (d.Y > top) { top = d.Y; topPond = d.Pond; }
                else if (d.Y == top && d.Pond) topPond = true;
            }
            if (float.IsNegativeInfinity(top)) return float.NaN;
            // 0.2.1321 (log-proven: "column (220,959): 2 disk levels on one floor (bed 46.75): 49.00, 47.11, kept 48.07"; chunk
            // (224,960) levels 47.15..49.00 - river-start / end ponds not flat): the lower river disk was faded out only deep inside the
            // pond (keep = 1 - Clamp01(th * 4)) and pulled the outer quarter of the pond down. Inside a POND disk that is the highest
            // covering disk, the surface is that pond's own level - no averaging with river / lower disks; the river-to-pond step
            // happens outside the pond circle, where only river disks blend.
            if (topPond)
            {
                if (FiresLogger.DebugEnabled && IsMainThread) NotePondFlat(x, z, top, list, yLow, yHigh);   // 0.2.1347: the river blend only for a column not counted yet
                return top;
            }
            return BlendRivers(list, x, z, yLow, yHigh, top);
        }

        // The pre-1321 weighted blend, now over river (non-pond) disks only.
        private static float BlendRivers(List<Entry> list, float x, float z, float yLow, float yHigh, float top)
        {
            // 0.2.1192 (RenderDoc 10-28, Tools\RDC_ICE_SHORE_20261028.md: every placed sheet split into two flat plateaus ~0.40 m
            // apart, and streams stepped 2-4 m with standing panels). PROVEN cause: the hard exclusion "d.Y < top - BlendBand". Stacked
            // pours (log: "disk levels on one floor (bed 47.91): 49.00, 48.50, 48.00, kept 48.54") switch which disks count at the EDGE
            // of the highest disk's circle: inside it the 48.00 disk is excluded, one column outside it is included -> a level step
            // on the circle line, every time. Now a lower disk is faded out CONTINUOUSLY by how deep the point sits inside each higher
            // disk that is more than BlendBand above it (zero at that disk's rim), so the surface ramps across circle edges instead of
            // stepping. A lone disk is still flat at its own height.
            float sum = 0f, weights = 0f;
            foreach (var d in list)
            {
                if (!d.Water || d.Y < yLow || d.Y > yHigh) continue;
                float dx = x - d.X, dz = z - d.Z, r2 = d.R * d.R, dd = dx * dx + dz * dz;
                if (dd > r2) continue;
                if (d.Pond) continue;   // 0.2.1321: river disks blend only with river disks
                float keep = 1f;
                if (d.Y < top - BlendBand)
                {
                    foreach (var h in list)
                    {
                        if (!h.Water || h.Y > yHigh || h.Y - d.Y <= BlendBand) continue;
                        float hx = x - h.X, hz = z - h.Z, hd = hx * hx + hz * hz;
                        if (hd > h.R * h.R) continue;
                        float th = 1f - Mathf.Sqrt(hd) / Mathf.Max(0.01f, h.R);   // 0 at h's rim, 1 at its centre
                        keep = Mathf.Min(keep, 1f - Mathf.Clamp01(th * 4f));     // gone within the outer quarter of h
                    }
                    if (keep <= 0f) continue;
                }
                float t = 1f - Mathf.Sqrt(dd) / Mathf.Max(0.01f, d.R);
                float w = (t * t + 1e-4f) * keep;
                sum += d.Y * w;
                weights += w;
            }
            return weights > 0f ? sum / weights : float.NaN;
        }

        // 0.2.1073 (Fire shots 471/474: river panels poking past the channel and hanging in the air): the surface at (x,z) that
        // its own disks can HOLD. A disk at level L reaches MaxDepth under L; where the floor under a point lies deeper than that
        // (the disk's rim past a slope break, a cliff foot, a lower reach of the same river), that disk does not cover the point
        // and the next lower disk is tried. bedAt(level) = the floor under (x,z) below level, NaN when there is none (left to the
        // caller, as before). NaN = no water here.
        internal const float SupportSlack = 0.75f;
        internal static float SupportedSurfaceAt(float x, float z, float yLow, float yHigh, System.Func<float, float> bedAt)
        {
            var index = s_index;
            for (int tries = 0; tries < 6; tries++)
            {
                float level = FreshSurfaceAt(x, z, yLow, yHigh);
                if (float.IsNaN(level) || index == null || !index.TryGetValue(CellKey(x, z), out var list)) return level;
                float bed = bedAt(level);
                if (float.IsNaN(bed)) return level;
                float reach = 0f, lowest = float.PositiveInfinity;
                foreach (var d in list)
                {
                    if (!d.Water || d.Y < yLow || d.Y > yHigh || d.Y < level - BlendBand - 0.01f) continue;
                    float dx = x - d.X, dz = z - d.Z;
                    if (dx * dx + dz * dz > d.R * d.R) continue;
                    if (d.D > reach) reach = d.D;
                    if (d.Y < lowest) lowest = d.Y;
                }
                if (float.IsPositiveInfinity(lowest) || level - bed <= reach + SupportSlack) return level;
                yHigh = lowest - 0.001f;   // these disks can't hold water this deep here: try the ones below them
                if (yHigh < yLow) return float.NaN;
            }
            return float.NaN;
        }

        // A placed disk as the renderer sees it.
        internal struct DiskInfo { public float X, Z, Y, R; public bool Water, Pond; public int Hash, Order; public WaterDiskKind Kind; public string Combiner; }   // 0.2.1094: Hash/Order/Kind/Combiner for fat_water_layers
        private static readonly List<DiskInfo> s_disks = new List<DiskInfo>();
        private static long s_signature;

        // From the water tools' per-frame tick (and any main-thread caller): keep the index fresh, then let the renderer of
        // placed fresh water catch up.
        internal static void Tick()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Time.realtimeSinceStartup - s_indexBuilt > IndexMaxAge) Rebuild();
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            int queued = PlacedWaterSheets.Tick(s_disks, s_signature, out int rebuilt);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            // 0.2.1102 (overnight #2): the water tick's own cost, so slow V-tool frames name their stage.
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            s_tIndex += (t1 - t0) * f; s_tSheets += (t2 - t1) * f; s_tRebuilt += rebuilt; s_tTicks++;
            if (queued > s_tQueue) s_tQueue = queued;
            if (Time.realtimeSinceStartup >= s_tLogAt)
            {
                s_tLogAt = Time.realtimeSinceStartup + 10f;
                // 0.2.1165: Debug (Fire: proof lines are Debug only), with why chunks were queued.
                if (FiresLogger.DebugEnabled && (s_tRebuilt > 0 || s_tIndex + s_tSheets > 50.0))
                    FiresLogger.LogDebug($"[WaterTools] water tick last 10 s: {s_tIndex + s_tSheets:0} ms ({s_tTicks} tick(s); index {s_tIndex:0} ms, rebuilt {s_indexBuilds} / kept {s_indexSkips}; sheets {s_tSheets:0} ms), rebuilt {s_tRebuilt} chunk(s) (queued by: disk change {PlacedWaterSheets.ReasonDisk}, voxel remesh {PlacedWaterSheets.ReasonMeshed}, state field {PlacedWaterSheets.ReasonState}, tools {PlacedWaterSheets.ReasonTool}), voxel remesh events fired {VerdantsAscent.Terrain.Voxel.VoxelWaterSheetMesh.Fired} / skipped same voxel version {VerdantsAscent.Terrain.Voxel.VoxelWaterSheetMesh.SkippedSameVersion} (0.2.1165), worst chunk {PlacedWaterSheets.WorstChunkMs:0.0} ms, queue {s_tQueue}");
                PlacedWaterSheets.ReasonDisk = PlacedWaterSheets.ReasonMeshed = PlacedWaterSheets.ReasonState = PlacedWaterSheets.ReasonTool = 0; PlacedWaterSheets.WorstChunkMs = 0;
                VerdantsAscent.Terrain.Voxel.VoxelWaterSheetMesh.Fired = VerdantsAscent.Terrain.Voxel.VoxelWaterSheetMesh.SkippedSameVersion = 0;
                s_tIndex = s_tSheets = 0; s_tRebuilt = s_tTicks = s_tQueue = s_indexBuilds = s_indexSkips = 0;
            }
        }
        private static double s_tIndex, s_tSheets;
        private static int s_tRebuilt, s_tTicks, s_tQueue;
        private static float s_tLogAt;

        // 0.2.1091: the index refreshed for a caller that is not the water tools' tick (PlacedWaterNav, from Pathfinding.Update on
        // every peer that paths, the dedicated server included). Main thread.
        internal static IReadOnlyList<DiskInfo> FreshDisks()
        {
            if (IsMainThread && Time.realtimeSinceStartup - s_indexBuilt > IndexMaxAge) Rebuild();
            return s_disks;
        }

        // 0.2.1091: the disk set's signature (PlacedWaterField drops its caches when it changes).
        internal static long Signature => s_signature;

        // 0.2.1091: a pond-mode disk (its combiner settled to one level) covers (x,z) at this level: the field keeps it uncapped.
        internal static bool PondCovers(float x, float z, float level)
        {
            var index = s_index;
            if (index == null || !index.TryGetValue(CellKey(x, z), out var list)) return false;
            foreach (var d in list)
            {
                if (!d.Pond || !d.Water || Mathf.Abs(d.Y - level) > BlendBand + 0.01f) continue;
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz <= d.R * d.R) return true;
            }
            return false;
        }

        // 0.2.1343: any fresh disk of this water body (a level within the blend band, river or pond) covers (x,z): PlacedWaterField's bank walk.
        internal static bool BodyCovers(float x, float z, float level)
        {
            var index = s_index;
            if (index == null || !index.TryGetValue(CellKey(x, z), out var list)) return false;
            foreach (var d in list)
            {
                if (!d.Water || Mathf.Abs(d.Y - level) > BlendBand + 0.01f) continue;
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz <= d.R * d.R) return true;
            }
            return false;
        }

        // 0.2.1321: the highest fresh disk covering (x,z) within [yLow, yHigh] is a pond: its own level.
        internal static bool PondTopAt(float x, float z, float yLow, float yHigh, out float pondY)
        {
            pondY = float.NaN;
            var index = s_index;
            if (index == null || !index.TryGetValue(CellKey(x, z), out var list)) return false;
            float top = float.NegativeInfinity; bool pond = false;
            foreach (var d in list)
            {
                if (!d.Water || d.Y < yLow || d.Y > yHigh) continue;
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz > d.R * d.R) continue;
                if (d.Y > top) { top = d.Y; pond = d.Pond; }
                else if (d.Y == top && d.Pond) pond = true;
            }
            if (!pond) return false;
            pondY = top;
            return true;
        }

        // 0.2.1321 proof: per pond level, columns answered flat and how many of them a river disk also covered (blended before 1321).
        private sealed class PondStat { public int Columns, Blended; public float X, Z; }
        private static readonly Dictionary<int, PondStat> s_pondStats = new Dictionary<int, PondStat>();
        private static readonly HashSet<long> s_pondCols = new HashSet<long>();
        private static float s_pondLogAt;
        private static bool s_pondProof;
        private static int s_pondStill;
        internal static void NotePondStill(int n) { if (IsMainThread) s_pondStill += n; }
        private static void NotePondFlat(float x, float z, float level, List<Entry> list, float yLow, float yHigh)
        {
            if (!s_pondProof) { s_pondProof = true; FiresLogger.LogInfo("[WaterSheets] ponds are flat: inside the highest pond disk the surface is the pond's own level, river disks blend only with river disks, pond vertices carry no flow (0.2.1321)"); }
            long col = ((long)Mathf.FloorToInt(x) << 32) ^ (uint)Mathf.FloorToInt(z);
            int key = Mathf.RoundToInt(level * 100f);
            if (s_pondCols.Count < 200000 && s_pondCols.Add(col ^ ((long)key << 20)))
            {
                float riverBlend = BlendRivers(list, x, z, yLow, yHigh, level);
                if (!s_pondStats.TryGetValue(key, out var st)) s_pondStats[key] = st = new PondStat { X = x, Z = z };
                st.Columns++;
                if (!float.IsNaN(riverBlend)) st.Blended++;
            }
            if (Time.realtimeSinceStartup < s_pondLogAt || s_pondStats.Count == 0) return;
            s_pondLogAt = Time.realtimeSinceStartup + 10f;
            foreach (var kv in s_pondStats)
                FiresLogger.LogDebug($"[WaterSheets] pond near ({kv.Value.X:0},{kv.Value.Z:0}): flat level {kv.Key / 100f:0.00}, columns {kv.Value.Columns}, previously blended {kv.Value.Blended} (a river disk also covered them); pond sheet vertices given flow 0 so far {s_pondStill} (0.2.1321)");
            s_pondStats.Clear(); s_pondCols.Clear();
        }

        // ── the snapshot index ──────────────────────────────────────────────────────────────────────────────────────────────
        private struct Entry { public float X, Z, Y, R, D; public bool Water, Pond; }   // D = the disk's MaxDepth (0.2.1073 clip)
        private static volatile Dictionary<long, List<Entry>> s_index;
        private static float s_indexBuilt = -100f;
        private static int s_mainThread = -1;

        internal static bool IsMainThread
        {
            get
            {
                int id = System.Threading.Thread.CurrentThread.ManagedThreadId;
                if (s_mainThread < 0) { try { if (Application.isPlaying) s_mainThread = id; } catch { return false; } }
                return id == s_mainThread;
            }
        }

        // 0.2.1102 (overnight #2: the water tick rebuilt the whole disk index every second, ~650 disks into fresh lists): a cheap
        // fingerprint of everything Rebuild reads (per disk: hash, position, radius/size, depth, kind; per combiner: liquid
        // types, pond mode). An unchanged fingerprint keeps the index; any change rebuilds it exactly as before.
        private static long s_fingerprint = long.MinValue;
        private static int s_indexSkips, s_indexBuilds;
        private static long Fingerprint()
        {
            long f = 1469598103934665603L;
            unchecked
            {
                foreach (var combiner in WaterMeshCombiner.AllCombiners)
                {
                    if (combiner == null) { f = f * 1099511628211L + 7; continue; }
                    f = f * 1099511628211L + ((int)combiner.CustomLiquidType * 31 + (int)combiner.LiquidType * 7 + (combiner.IsPondMode ? 1 : 0));
                    foreach (var disk in combiner.WaterDisks)
                    {
                        if (disk == null) { f = f * 1099511628211L + 3; continue; }
                        var p = disk.Position;
                        f = f * 1099511628211L + disk.StableHash;
                        f = f * 1099511628211L + Mathf.RoundToInt(p.x * 100f);
                        f = f * 1099511628211L + Mathf.RoundToInt(p.y * 100f);
                        f = f * 1099511628211L + Mathf.RoundToInt(p.z * 100f);
                        f = f * 1099511628211L + Mathf.RoundToInt(disk.Radius * 100f) + Mathf.RoundToInt(disk.Width * 100f) * 3 + Mathf.RoundToInt(disk.Length * 100f) * 5;
                        f = f * 1099511628211L + Mathf.RoundToInt(disk.MaxDepth * 100f) + (int)disk.Kind * 11;
                    }
                }
            }
            return f;
        }

        private static void Rebuild()
        {
            if (!IsMainThread) return;
            s_indexBuilt = Time.realtimeSinceStartup;
            long fp = Fingerprint();
            if (fp == s_fingerprint && s_index != null) { s_indexSkips++; return; }
            s_fingerprint = fp;
            s_indexBuilds++;
            var index = new Dictionary<long, List<Entry>>();
            s_disks.Clear();
            long signature = 17;
            int kRivers = 0, kPonds = 0, kTagged = 0, kInferred = 0, kInferredSpan = 0;
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null || combiner.CustomLiquidType.IsSolid()) continue;
                bool water = combiner.CustomLiquidType == CustomLiquidType.Water && combiner.LiquidType == LiquidType.Water;
                // 0.2.1091 (Fire: ponds keep their level): the cap applies to RIVER disks only. The kind is the placing tool's tag
                // (WaterDisk.Kind, saved since 0.2.1091); an untagged disk (laid before) takes its combiner's shape: disks at
                // different levels (a river chain stepping down) = river, one level = pond. A pond-mode combiner is a pond.
                float yMin = float.MaxValue, yMax = float.MinValue;
                foreach (var disk in combiner.WaterDisks)
                    if (disk != null) { yMin = Mathf.Min(yMin, disk.Position.y); yMax = Mathf.Max(yMax, disk.Position.y); }
                bool inferredRiver = yMax - yMin > InferRiverSpan;
                foreach (var disk in combiner.WaterDisks)
                {
                    if (disk == null) continue;
                    bool tagged = disk.Kind != WaterDiskKind.Unknown;
                    // 0.2.1094 (Fire, shot 544: "we filled this section in with v pour last session and its broken again"): the
                    // shape guess called his untagged Vpour pond a river (its combiner also holds stepped river dabs, 428 of 1227
                    // restored disks went "river"), so the bank cap dropped whole sections of it dry. Water laid before the kind tag
                    // keeps exactly the level it was made with: untagged = pond (never capped). Only tool-tagged rivers are capped.
                    bool pond = combiner.IsPondMode || !tagged || disk.Kind == WaterDiskKind.Pond;
                    if (water) { if (pond) kPonds++; else kRivers++; if (tagged) kTagged++; else kInferred++; }
                    if (water && !tagged && inferredRiver) kInferredSpan++;
                    var e = new Entry { X = disk.Position.x, Z = disk.Position.z, Y = disk.Position.y, R = Mathf.Max(disk.Radius, Mathf.Max(disk.Width, disk.Length) * 0.5f), D = disk.MaxDepth, Water = water, Pond = pond };
                    s_disks.Add(new DiskInfo { X = e.X, Z = e.Z, Y = e.Y, R = e.R, Water = water, Pond = pond, Hash = disk.StableHash, Order = s_disks.Count, Kind = disk.Kind, Combiner = combiner.name });
                    unchecked { signature = signature * 31 + disk.StableHash * 7 + (water ? 1 : 0) + (pond ? 2 : 0) + Mathf.RoundToInt(e.Y * 100f); }
                    int x0 = Mathf.FloorToInt((e.X - e.R) / IndexCell), x1 = Mathf.FloorToInt((e.X + e.R) / IndexCell);
                    int z0 = Mathf.FloorToInt((e.Z - e.R) / IndexCell), z1 = Mathf.FloorToInt((e.Z + e.R) / IndexCell);
                    for (int cx = x0; cx <= x1; cx++)
                        for (int cz = z0; cz <= z1; cz++)
                        {
                            long key = ((long)cx << 32) ^ (uint)cz;
                            if (!index.TryGetValue(key, out var list)) index[key] = list = new List<Entry>();
                            list.Add(e);
                        }
                }
            }
            s_index = index;
            if (signature != s_signature && (kRivers + kPonds) > 0 && Time.realtimeSinceStartup >= s_kindsLogAt)
            {
                s_kindsLogAt = Time.realtimeSinceStartup + 10f;
                FiresLogger.LogInfo($"[WaterField] disk kinds: rivers {kRivers}, ponds {kPonds} (source: tool tag {kTagged}, restored untagged {kInferred} kept as ponds (laid before the kind tag: never capped; combiners spanning > {InferRiverSpan:0.00} m: {kInferredSpan}); only tool-tagged river disks are capped to their banks");
            }
            s_signature = signature;
        }

        private const float InferRiverSpan = 0.25f;
        private static float s_kindsLogAt;

        private static long CellKey(float x, float z) => ((long)Mathf.FloorToInt(x / IndexCell) << 32) ^ (uint)Mathf.FloorToInt(z / IndexCell);

        private static bool FindDisk(int id, out WaterDisk found)
        {
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null) continue;
                foreach (var disk in combiner.WaterDisks)
                    if (disk != null && disk.StableHash == id) { found = disk; return true; }
            }
            found = null;
            return false;
        }

        // FNV-1a over the quantized centre (0.1 m), level (0.01 m) and radius (0.1 m); never 0.
        private static int SheetId(Vector3 c, float y, float r)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (int v in new[] { Mathf.RoundToInt(c.x * 10f), Mathf.RoundToInt(c.z * 10f), Mathf.RoundToInt(y * 100f), Mathf.RoundToInt(r * 10f), 0x5EE7 })
                {
                    h = (h ^ (uint)v) * 16777619u;
                    h = (h ^ (uint)(v >> 16)) * 16777619u;
                }
                int id = (int)(h & 0x7FFFFFFF);
                return id == 0 ? 1 : id;
            }
        }

        private static string F(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
