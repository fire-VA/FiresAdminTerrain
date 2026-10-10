using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    // Calm still-water sheets for anything that carves a bed and wants it filled (FAT 0.2.759; Fire 10-06: cave water "more than
    // just pools", carved and filled with our calm sheet mesh, never poured; [worldgen]'s cave content + the V-tools).
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
        private const float BlendBand = 0.5f;   // 0.2.827: FreshSurfaceAt blends only disks this close to the highest one

        // A calm sheet at surfaceY over the bed under centre. Returns its id (> 0), or 0 with the reason.
        public static int Lay(Vector3 centre, float surfaceY, float radius, string why, out string refusal)
        {
            refusal = null;
            if (float.IsNaN(surfaceY) || float.IsInfinity(surfaceY) || float.IsNaN(centre.x) || float.IsNaN(centre.z) || radius < 0.25f || radius > 64f)
            {
                refusal = F("bad numbers (surface {0}, radius {1})", surfaceY, radius);
                return 0;
            }
            if (!VoxelWorld.TrySampleSurfaceY(centre.x, centre.z, surfaceY + 0.05f, surfaceY - FloorReach, out float bed, out bool wall) || wall)
            {
                refusal = F("no voxel floor under ({0:0.0},{1:0.0}) within {2:0} m below {3:0.00}", centre.x, centre.z, FloorReach, surfaceY);
                return 0;
            }
            int id = SheetId(centre, surfaceY, radius);
            if (FindDisk(id, out _)) return id;   // already laid (this run, or restored from the world)

            var at = new Vector3(centre.x, surfaceY, centre.z);
            var disk = new WaterDisk(at, radius, surfaceY - bed + DepthMargin, CustomLiquidType.Water, id);
            try { WaterOperationRPC.RequestCreateWaterWithCustomType(at, disk, CustomLiquidType.Water); }
            catch (System.Exception ex) { refusal = "the water request threw: " + ex.Message; return 0; }
            s_indexBuilt = -100f;
            FiresLogger.LogDebug(F("[FiresWater] sheet laid at ({0:0.0},{1:0.00},{2:0.0}) r {3:0.0}, bed {4:0.00} (id {5}; {6})",
                at.x, at.y, at.z, radius, bed, id, string.IsNullOrEmpty(why) ? "no reason given" : why));
            return id;
        }

        // Sheets along a carved channel: one disk every radius * ChannelSpacing, each at its own level (spine[i] -> surfaceY[i],
        // interpolated between points). Returns how many were laid or already there.
        public static int LayChannel(IList<Vector3> spine, IList<float> surfaceY, float radius, string why, float spacing = ChannelSpacing)   // 0.2.816: spacing (radius x), cave rivers lay denser
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
            float best = float.NaN;
            foreach (var d in list)
            {
                if (d.Y < yLow || d.Y > yHigh) continue;
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz > d.R * d.R) continue;
                if (float.IsNaN(best) || d.Y > best) best = d.Y;
            }
            return best;
        }

        // 0.2.766: the surface the skyland-builder render of placed fresh water uses (PlacedWaterSheets): over (x, z) inside
        // [yLow, yHigh], a blend of the covering fresh-water disks weighted (1 - d/R)^2 by distance from each centre, so a single
        // disk is flat at its height and overlapping dabs at different heights (a pour down a slope) ramp smoothly instead of
        // stepping. NaN where no fresh-water disk covers the point.
        internal static float FreshSurfaceAt(float x, float z, float yLow, float yHigh)
        {
            if (IsMainThread && Time.realtimeSinceStartup - s_indexBuilt > IndexMaxAge) Rebuild();
            var index = s_index;
            if (index == null || !index.TryGetValue(CellKey(x, z), out var list)) return float.NaN;
            // 0.2.827 (round 8, cave river at (-422,-3484): a cave pool disk at 38.79 inside the river's 43.06 sheets blended to
            // 41.1, chunk surfaces 2 m apart with 4 m skirts = "mini waterfalls"): only disks within BlendBand of the HIGHEST disk
            // covering the point blend; a lower disk under a higher one is just that water's bed.
            float top = float.NegativeInfinity;
            foreach (var d in list)
            {
                if (!d.Water || d.Y < yLow || d.Y > yHigh) continue;
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz <= d.R * d.R && d.Y > top) top = d.Y;
            }
            if (float.IsNegativeInfinity(top)) return float.NaN;
            float sum = 0f, weights = 0f;
            foreach (var d in list)
            {
                if (!d.Water || d.Y < yLow || d.Y > yHigh || d.Y < top - BlendBand) continue;
                float dx = x - d.X, dz = z - d.Z, r2 = d.R * d.R, dd = dx * dx + dz * dz;
                if (dd > r2) continue;
                float t = 1f - Mathf.Sqrt(dd) / Mathf.Max(0.01f, d.R);
                float w = t * t + 1e-4f;
                sum += d.Y * w;
                weights += w;
            }
            return weights > 0f ? sum / weights : float.NaN;
        }

        // A placed disk as the renderer sees it.
        internal struct DiskInfo { public float X, Z, Y, R; public bool Water; }
        private static readonly List<DiskInfo> s_disks = new List<DiskInfo>();
        private static long s_signature;

        // From the water tools' per-frame tick (and any main-thread caller): keep the index fresh, then let the renderer of
        // placed fresh water catch up (0.2.766).
        internal static void Tick()
        {
            if (Time.realtimeSinceStartup - s_indexBuilt > IndexMaxAge) Rebuild();
            PlacedWaterSheets.Tick(s_disks, s_signature);
        }

        // ── the snapshot index ──────────────────────────────────────────────────────────────────────────────────────────────
        private struct Entry { public float X, Z, Y, R; public bool Water; }
        private static volatile Dictionary<long, List<Entry>> s_index;
        private static float s_indexBuilt = -100f;
        private static int s_mainThread = -1;

        private static bool IsMainThread
        {
            get
            {
                int id = System.Threading.Thread.CurrentThread.ManagedThreadId;
                if (s_mainThread < 0) { try { if (Application.isPlaying) s_mainThread = id; } catch { return false; } }
                return id == s_mainThread;
            }
        }

        private static void Rebuild()
        {
            if (!IsMainThread) return;
            s_indexBuilt = Time.realtimeSinceStartup;
            var index = new Dictionary<long, List<Entry>>();
            s_disks.Clear();
            long signature = 17;
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null || combiner.CustomLiquidType.IsSolid()) continue;
                bool water = combiner.CustomLiquidType == CustomLiquidType.Water && combiner.LiquidType == LiquidType.Water;
                foreach (var disk in combiner.WaterDisks)
                {
                    if (disk == null) continue;
                    var e = new Entry { X = disk.Position.x, Z = disk.Position.z, Y = disk.Position.y, R = Mathf.Max(disk.Radius, Mathf.Max(disk.Width, disk.Length) * 0.5f), Water = water };
                    s_disks.Add(new DiskInfo { X = e.X, Z = e.Z, Y = e.Y, R = e.R, Water = water });
                    unchecked { signature = signature * 31 + disk.StableHash * 7 + (water ? 1 : 0) + Mathf.RoundToInt(e.Y * 100f); }
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
            s_signature = signature;
        }

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
