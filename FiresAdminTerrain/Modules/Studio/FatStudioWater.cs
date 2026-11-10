using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using VerdantsAscent.Water.Core;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Terrain.Studio
{
    // 0.2.1246 (World Studio v3, Fire 10-29 "Placed water first"): water place / remove / replace and the vegetation brush, each
    // undone by a region snapshot (FatStudioRegion) taken before the edit. Ponds / lakes are the Lake tool's basin
    // (WaterShapingTools.StudioPond), rivers the River tool's click-mode carve along the Studio's path (WaterToolDriver.StudioRiver,
    // the same budgeted job), so the look and the water stay the one water authority's. A feature = the placed water disks of one
    // kind that touch each other (CustomWater combiners). Remove takes those disks away and fills the bed back up to the rim
    // around it (vanilla level op at the rim height, smoothed). Generated water (hydrology) is listed but refused for now.
    internal static class FatStudioWater
    {
        internal const string WaterKind = "water", VegKind = "vegetation";
        internal const int Version = 2;   // 0.2.1249: v2 = footprint terrain in the region (v1 = whole zones, still restored)
        private const int MaxPath = 256, MaxVegObjects = 4000;
        private const float Margin = 6f, MaxRadius = 64f;
        private const string Tag = "[FatStudio]";

        // ── water ──
        internal static bool PlacePond(FatPondArgs a, out FatWaterHandle h, out FatSnapshot s, out string reason)
        {
            h = null; s = null;
            if (!Ready(out reason)) return false;
            if (a == null || !(a.Radius >= 1f) || a.Radius > MaxRadius || !(a.Depth > 0f)) { reason = $"bad args: radius 1..{MaxRadius}, depth > 0"; return false; }
            if (!ZoneSystem.instance.GetGroundHeight(a.Center, out float ground)) { reason = $"not loaded: no ground at ({a.Center.x:0},{a.Center.z:0})"; return false; }
            var c = new Vector3(a.Center.x, ground, a.Center.z);
            if (!Snap(new List<FatStudioRegion.Circle> { Circle(c, a.Radius * WaterShapingTools.BankSpread + Margin) }, WaterKind, out s, out reason)) return false;
            string done = WaterShapingTools.StudioPond(c, a.Radius, a.Depth, a.Level, a.Shore);
            if (done == null) { reason = "refused: the pond would hold no water there (level under the bed)"; s = null; return false; }
            h = new FatWaterHandle { Kind = "pond", Position = c, Radius = a.Radius };
            reason = "ok: " + done + "; the surface lays in ~1-2 s";
            FiresLogger.LogDebug($"{Tag} WaterPlacePond {reason}; snapshot {s.Id} {s.Bytes.Length} bytes");
            return true;
        }

        internal static bool PlaceRiver(FatRiverArgs a, out FatWaterHandle h, out FatSnapshot s, out string reason)
        {
            h = null; s = null;
            if (!Ready(out reason)) return false;
            if (a == null || a.Path == null || a.Path.Length < 2) { reason = "bad args: a river needs a path of 2+ points"; return false; }
            if (a.Path.Length > MaxPath) { reason = $"tooLarge: more than {MaxPath} path points, split it"; return false; }
            if (!(a.Width >= 1f) || a.Width > 48f || !(a.Depth > 0f)) { reason = "bad args: width 1..48, depth > 0"; return false; }
            if (!float.IsNaN(a.StartLevel) || !float.IsNaN(a.EndLevel)) { reason = "unsupported: fixed river levels come in a later FatApi (leave StartLevel / EndLevel NaN: the bed follows the ground)"; return false; }
            if (WaterToolDriver.StudioRiverCarving) { reason = "busy: a river is still carving, send it again in a moment"; return false; }
            var path = new List<Vector3>();
            var circles = new List<FatStudioRegion.Circle>();
            float r = a.Width * 0.5f;
            foreach (var p in a.Path)
            {
                if (!ZoneSystem.instance.GetGroundHeight(p, out float g)) { reason = $"not loaded: no ground at ({p.x:0},{p.z:0})"; return false; }
                path.Add(new Vector3(p.x, g, p.z));
            }
            for (int i = 0; i < path.Count; i++)
            {
                circles.Add(Circle(path[i], r * WaterShapingTools.BankSpread + Margin));
                if (i == 0) continue;
                float len = Vector2.Distance(new Vector2(path[i - 1].x, path[i - 1].z), new Vector2(path[i].x, path[i].z));
                int n = Mathf.CeilToInt(len / Mathf.Max(2f, r));
                for (int k = 1; k < n; k++) circles.Add(Circle(Vector3.Lerp(path[i - 1], path[i], k / (float)n), r * WaterShapingTools.BankSpread + Margin));
            }
            if (!Snap(circles, WaterKind, out s, out reason)) return false;
            string done = WaterToolDriver.StudioRiver(path, r, a.Depth);
            if (done == null) { reason = "busy: a river is still carving, send it again in a moment"; s = null; return false; }
            var mid = path[path.Count / 2];
            float reach = 0f; foreach (var p in path) reach = Mathf.Max(reach, Vector2.Distance(new Vector2(p.x, p.z), new Vector2(mid.x, mid.z)));
            h = new FatWaterHandle { Kind = "river", Position = mid, Radius = reach + r };
            reason = "ok: " + done + "; the carve runs over the next frames and the water lays after it";
            FiresLogger.LogDebug($"{Tag} WaterPlaceRiver {reason}; snapshot {s.Id} {s.Bytes.Length} bytes");
            return true;
        }

        internal static bool Remove(FatWaterHandle h, out FatSnapshot s, out string reason)
        {
            s = null;
            if (!Ready(out reason)) return false;
            if (h == null) { reason = "bad args: no handle"; return false; }
            if (h.Kind == "generated") { reason = "unsupported: generated water removal comes in a later FatApi"; return false; }
            var disks = DisksOf(h);
            if (disks.Count == 0) { reason = $"not found: no placed {h.Kind} water at {h}"; return false; }
            var circles = new List<FatStudioRegion.Circle>();
            foreach (var d in disks) circles.Add(Circle(d.Position, d.Radius * WaterShapingTools.BankSpread + Margin));
            if (!Snap(circles, WaterKind, out s, out reason)) return false;
            int filled = 0;
            foreach (var d in disks)
            {
                WaterOperationRPC.RequestRemoveWater(d.StableHash);
                // the bed fills back to the ground just outside the bank (the rim), smoothed into its surroundings
                float rimR = d.Radius * WaterShapingTools.BankSpread + 1.5f;
                float rim = RimHeight(d.Position, rimR);
                if (!float.IsNaN(rim) && FatStudioTerrain.Edit(new FatTerrainEditArgs { Op = FatTerrainOp.Level, Points = new[] { d.Position }, Radius = rimR, Height = rim }, out _, out _)) filled++;
            }
            reason = $"ok: {disks.Count} {h.Kind} water disk(s) removed, {filled} bed(s) filled back to the rim";
            FiresLogger.LogDebug($"{Tag} WaterRemove {reason}; snapshot {s.Id} {s.Bytes.Length} bytes");
            return true;
        }

        internal static bool Replace(FatWaterHandle h, FatPondArgs pond, FatRiverArgs river, out FatWaterHandle nh, out FatSnapshot s, out string reason)
        {
            nh = null; s = null;
            if ((pond == null) == (river == null)) { reason = "bad args: give exactly one of pond / river"; return false; }
            if (!Ready(out reason)) return false;
            var disks = DisksOf(h);
            if (h == null || h.Kind == "generated") { reason = h == null ? "bad args: no handle" : "unsupported: generated water removal comes in a later FatApi"; return false; }
            if (disks.Count == 0) { reason = $"not found: no placed {h.Kind} water at {h}"; return false; }
            // one snapshot over the old feature AND the new shape, taken first: the remove's and the place's own snapshots are dropped
            var circles = new List<FatStudioRegion.Circle>();
            foreach (var d in disks) circles.Add(Circle(d.Position, d.Radius * WaterShapingTools.BankSpread + Margin));
            if (pond != null) circles.Add(Circle(pond.Center, pond.Radius * WaterShapingTools.BankSpread + Margin));
            else foreach (var p in river.Path ?? new Vector3[0]) circles.Add(Circle(p, river.Width * 0.5f * WaterShapingTools.BankSpread + Margin));
            if (!Snap(circles, WaterKind, out s, out reason)) return false;
            if (!Remove(h, out _, out string rwhy)) { reason = rwhy; s = null; return false; }
            bool ok = pond != null ? PlacePond(pond, out nh, out _, out string pwhy) : PlaceRiver(river, out nh, out _, out pwhy);
            if (!ok) { if (Split(s.Bytes, out _, out byte[] reg)) FatStudioRegion.Restore(reg, out _); reason = $"refused: the new shape could not be placed ({pwhy}); the old water was put back"; s = null; return false; }
            reason = $"ok: {h.Kind} replaced by a {nh.Kind}: {rwhy} / {pwhy}";
            return true;
        }

        internal static FatWaterHandle[] List(Vector3 centre, float radius)
        {
            var list = new List<FatWaterHandle>();
            var seen = new HashSet<WaterDisk>();
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null) continue;
                foreach (var d in combiner.WaterDisks)
                {
                    if (d == null || seen.Contains(d) || Flat(d.Position, centre) > radius + d.Radius) continue;
                    var group = Group(d, seen);
                    Vector3 sum = Vector3.zero; foreach (var g in group) sum += g.Position;
                    var mid = sum / group.Count;
                    float reach = 0f; foreach (var g in group) reach = Mathf.Max(reach, Flat(g.Position, mid) + g.Radius);
                    list.Add(new FatWaterHandle { Kind = d.Kind == WaterDiskKind.River ? "river" : "pond", Position = mid, Radius = reach });
                }
            }
            // generated water at the centre (hydrology), listed so the Studio can show it and grey out Remove
            if (FatApi.TryWaterAt(centre.x, centre.z, out float level, out float depth) && depth > 0.3f && CustomWaterManager.GetWaterAt(new Vector3(centre.x, level, centre.z)) == null)
                list.Add(new FatWaterHandle { Kind = "generated", Position = new Vector3(centre.x, level, centre.z), Radius = 0f });
            return list.ToArray();
        }

        internal static uint Revision(FatWaterHandle h)
        {
            uint rev = 0u;
            foreach (var d in DisksOf(h)) rev = unchecked(rev * 31u + (uint)d.StableHash + (uint)Mathf.RoundToInt(d.Radius * 100f) + (uint)Mathf.RoundToInt(d.MaxDepth * 100f));
            return rev;
        }

        // ── vegetation brush ──
        internal static bool Vegetation(FatVegArgs a, out FatSnapshot s, out string reason)
        {
            s = null;
            if (!Ready(out reason)) return false;
            if (a == null || a.Points == null || a.Points.Length == 0) { reason = "bad args: no points"; return false; }
            if (!(a.Radius > 0f) || a.Radius > MaxRadius) { reason = $"bad args: radius 0..{MaxRadius}"; return false; }
            var zs = ZoneSystem.instance;
            var veg = new Dictionary<int, ZoneSystem.ZoneVegetation>();
            foreach (var v in zs.m_vegetation) if (v != null && v.m_prefab != null) veg[v.m_prefab.name.GetStableHashCode()] = v;
            HashSet<int> only = null;
            if (a.Prefabs != null && a.Prefabs.Length > 0) { only = new HashSet<int>(); foreach (var n in a.Prefabs) if (!string.IsNullOrEmpty(n)) only.Add(n.GetStableHashCode()); }
            if (a.Mode == FatVegMode.Paint && (only == null || only.Count == 0)) { reason = "bad args: paint needs Prefabs (what to plant)"; return false; }
            if (a.Mode == FatVegMode.Paint) foreach (int hsh in only) if (!veg.ContainsKey(hsh)) { reason = "bad args: paint prefabs must be vegetation from this world's vegetation list"; return false; }
            var circles = new List<FatStudioRegion.Circle>();
            foreach (var p in a.Points) circles.Add(Circle(p, a.Radius));
            if (!Snap(circles, VegKind, out s, out reason)) return false;

            var rng = new System.Random(a.Seed != 0 ? a.Seed : Mathf.RoundToInt(a.Points[0].x * 7f) * 73856093 ^ Mathf.RoundToInt(a.Points[0].z * 7f) * 19349663);
            long me = ZDOMan.GetSessionID();
            int removed = 0, planted = 0;
            if (a.Mode == FatVegMode.Remove || a.Mode == FatVegMode.Thin)
            {
                if (!FatStudioRegion.Zones(circles, out var zones, out reason)) { s = null; return false; }
                var found = new List<ZDO>(); var one = new List<ZDO>();
                foreach (var zone in zones) { one.Clear(); ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), one); found.AddRange(one); }
                var inside = new List<ZDO>();
                foreach (var z in found)
                {
                    if (z == null || !z.IsValid() || z.GetLong(ZDOVars.s_creator) != 0L) continue;
                    int prefab = z.GetPrefab();
                    if (!veg.ContainsKey(prefab) || (only != null && !only.Contains(prefab))) continue;
                    var p = z.GetPosition(); bool hit = false;
                    foreach (var c in circles) { float dx = p.x - c.X, dz = p.z - c.Z; if (dx * dx + dz * dz <= c.R * c.R) { hit = true; break; } }
                    if (hit) inside.Add(z);
                }
                // Thin: keep about Density per 100 m² of the stroke's area, removing at random
                int keep = 0;
                if (a.Mode == FatVegMode.Thin) keep = Mathf.RoundToInt(Mathf.Max(0f, a.Density) * Area(circles) / 100f);
                for (int i = inside.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); var t = inside[i]; inside[i] = inside[j]; inside[j] = t; }
                for (int i = keep; i < inside.Count; i++) { if (removed >= MaxVegObjects) break; inside[i].SetOwner(me); ZDOMan.instance.DestroyZDO(inside[i]); removed++; }
            }
            else
            {
                var names = new List<int>(only);
                int want = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(0f, a.Density) * Area(circles) / 100f), 0, MaxVegObjects);
                for (int tries = 0; planted < want && tries < want * 6; tries++)
                {
                    var c = circles[rng.Next(circles.Count)];
                    double ang = rng.NextDouble() * Math.PI * 2.0, rr = Math.Sqrt(rng.NextDouble()) * c.R;
                    var at = new Vector3(c.X + (float)(Math.Cos(ang) * rr), 0f, c.Z + (float)(Math.Sin(ang) * rr));
                    var v = veg[names[rng.Next(names.Count)]];
                    if (!Suits(zs, v, ref at)) continue;
                    if (Place(v, at, (float)(rng.NextDouble() * 360.0), Mathf.Lerp(v.m_scaleMin, v.m_scaleMax, (float)rng.NextDouble()))) planted++;
                }
            }
            reason = a.Mode == FatVegMode.Paint ? $"ok: {planted} plant(s) placed" : $"ok: {removed} plant(s) removed";
            FiresLogger.LogDebug($"{Tag} VegetationEdit {a.Mode} {reason}; snapshot {s.Id} {s.Bytes.Length} bytes");
            return true;
        }

        // Ground that suits a vegetation entry, as vanilla's PlaceVegetation reads it: biome, altitude above sea, ocean depth.
        private static bool Suits(ZoneSystem zs, ZoneSystem.ZoneVegetation v, ref Vector3 at)
        {
            if (!zs.GetGroundHeight(at, out float y)) return false;
            at.y = y;
            var biome = WorldGenerator.instance.GetBiome(at);
            if ((v.m_biome & biome) == 0) return false;
            float alt = y - zs.m_waterLevel;
            if (alt < v.m_minAltitude || alt > v.m_maxAltitude) return false;
            return true;
        }

        private static bool Place(ZoneSystem.ZoneVegetation v, Vector3 at, float yaw, float scale)
        {
            var prefab = v.m_prefab; var nv = prefab != null ? prefab.GetComponent<ZNetView>() : null;
            if (nv == null) return false;
            var rot = Quaternion.Euler(0f, yaw, 0f);
            if (ZNet.instance.IsDedicated())
            {
                int hash = prefab.name.GetStableHashCode();
                var z = ZDOMan.instance.CreateNewZDO(at, hash);
                z.SetPrefab(hash); z.Persistent = nv.m_persistent; z.Type = nv.m_type; z.Distant = nv.m_distant; z.SetRotation(rot);
                if (Mathf.Abs(scale - 1f) > 0.001f) z.Set(ZDOVars.s_scaleScalarHash, scale);
                return true;
            }
            var go = UnityEngine.Object.Instantiate(prefab, at, rot);
            var view = go.GetComponent<ZNetView>();
            if (view != null && Mathf.Abs(scale - 1f) > 0.001f) view.SetLocalScale(Vector3.one * scale);
            return true;
        }

        // ── Restore for both kinds ──
        internal static bool Restore(FatSnapshot s, out string reason)
        {
            if (!Ready(out reason)) return false;
            if (s == null || s.Bytes == null || (s.Kind != WaterKind && s.Kind != VegKind) || (s.Version != 1 && s.Version != 2)) { reason = $"bad snapshot: kind {s?.Kind} v{s?.Version}"; return false; }
            if (s.Hash != null && !Same(Sha(s.Bytes), s.Hash)) { reason = "bad snapshot: hash mismatch"; return false; }
            if (s.Kind == WaterKind && WaterToolDriver.StudioRiverCarving) { reason = "busy: a river is still carving, undo it again in a moment"; return false; }
            if (!Split(s.Bytes, out var had, out byte[] region)) { reason = "bad snapshot: header"; return false; }
            if (s.Kind == WaterKind) foreach (int d in DisksIn(region, had)) WaterOperationRPC.RequestRemoveWater(d);   // the water laid since goes with its anchors
            bool ok = FatStudioRegion.Restore(region, s.Version >= 2, out reason);   // the reason carries the ms (0.2.1247)
            FiresLogger.LogDebug($"{Tag} Restore ({s.Kind}) {s.Id}: {reason}");
            return ok;
        }

        // The disks laid inside the snapshot's circles that the snapshot did not hold (its anchors come back from the region data;
        // the live combiners must drop the disks laid since, or their next debounced save would write them again).
        private static List<int> DisksIn(byte[] bytes, HashSet<int> before)
        {
            var hashes = new List<int>();
            var circles = new List<FatStudioRegion.Circle>();
            using (var r = new BinaryReader(new MemoryStream(bytes)))
            {
                int nc = r.ReadInt32();
                for (int i = 0; i < nc && i < 4096; i++) circles.Add(new FatStudioRegion.Circle { X = r.ReadSingle(), Z = r.ReadSingle(), R = r.ReadSingle() });
            }
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
            {
                if (combiner == null) continue;
                foreach (var d in combiner.WaterDisks)
                {
                    if (d == null || before.Contains(d.StableHash)) continue;
                    foreach (var c in circles) { float dx = d.Position.x - c.X, dz = d.Position.z - c.Z; if (dx * dx + dz * dz <= c.R * c.R) { hashes.Add(d.StableHash); break; } }
                }
            }
            return hashes;
        }

        // A snapshot's bytes: the water disks that existed when it was taken, then the region (FatStudioRegion.Capture).
        private static bool Split(byte[] bytes, out HashSet<int> had, out byte[] region)
        {
            had = new HashSet<int>(); region = null;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes)))
                {
                    int n = r.ReadInt32(); if (n < 0 || n > 1000000) return false;
                    for (int i = 0; i < n; i++) had.Add(r.ReadInt32());
                    region = r.ReadBytes((int)(bytes.Length - r.BaseStream.Position));
                }
                return true;
            }
            catch (EndOfStreamException) { return false; }
        }

        // ── helpers ──
        private static bool Snap(List<FatStudioRegion.Circle> circles, string kind, out FatSnapshot s, out string reason)
        {
            s = null;
            if (!FatStudioRegion.Capture(circles, out byte[] bytes, out reason)) return false;
            FiresLogger.LogDebug($"{Tag} {kind} snapshot taken: {reason}");   // 0.2.1247: the capture's size + ms
            var set = new List<int>();
            if (kind == WaterKind) foreach (var combiner in WaterMeshCombiner.AllCombiners) if (combiner != null) foreach (var d in combiner.WaterDisks) if (d != null) set.Add(d.StableHash);
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms)) { w.Write(set.Count); foreach (int hsh in set) w.Write(hsh); w.Write(bytes); }
                bytes = ms.ToArray();
            }
            s = new FatSnapshot { Id = Guid.NewGuid().ToString("N"), Kind = kind, Version = Version, Bytes = bytes, Hash = Sha(bytes) };
            return true;
        }

        private static List<WaterDisk> DisksOf(FatWaterHandle h)
        {
            var list = new List<WaterDisk>();
            if (h == null) return list;
            var kind = h.Kind == "river" ? WaterDiskKind.River : WaterDiskKind.Pond;
            WaterDisk seed = null; float best = float.MaxValue;
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
                if (combiner != null) foreach (var d in combiner.WaterDisks)
                    {
                        if (d == null || (d.Kind != kind && !(kind == WaterDiskKind.Pond && d.Kind == WaterDiskKind.Unknown))) continue;
                        float dist = Flat(d.Position, h.Position);
                        if (dist <= Mathf.Max(h.Radius, d.Radius) + 1f && dist < best) { best = dist; seed = d; }
                    }
            if (seed == null) return list;
            list.AddRange(Group(seed, new HashSet<WaterDisk>()));
            return list;
        }

        // The disks of seed's kind that touch it, transitively (one pond / one river).
        private static List<WaterDisk> Group(WaterDisk seed, HashSet<WaterDisk> seen)
        {
            var all = new List<WaterDisk>();
            foreach (var combiner in WaterMeshCombiner.AllCombiners) if (combiner != null) foreach (var d in combiner.WaterDisks) if (d != null && d.Kind == seed.Kind) all.Add(d);
            var group = new List<WaterDisk> { seed }; seen.Add(seed);
            for (int i = 0; i < group.Count && group.Count < 4000; i++)
                foreach (var d in all)
                    if (!seen.Contains(d) && Flat(d.Position, group[i].Position) <= d.Radius + group[i].Radius + 0.5f) { seen.Add(d); group.Add(d); }
            return group;
        }

        private static float RimHeight(Vector3 c, float r)
        {
            float sum = 0f; int n = 0;
            for (int k = 0; k < 16; k++)
            {
                float a = k * Mathf.PI / 8f;
                if (ZoneSystem.instance.GetGroundHeight(new Vector3(c.x + Mathf.Cos(a) * r, 0f, c.z + Mathf.Sin(a) * r), out float y)) { sum += y; n++; }
            }
            return n > 0 ? sum / n : float.NaN;
        }

        private static float Area(List<FatStudioRegion.Circle> circles)
        {
            // the union's area estimated on a 1 m grid over the circles' bounds (strokes overlap)
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            foreach (var c in circles) { x0 = Mathf.Min(x0, c.X - c.R); z0 = Mathf.Min(z0, c.Z - c.R); x1 = Mathf.Max(x1, c.X + c.R); z1 = Mathf.Max(z1, c.Z + c.R); }
            int n = 0;
            for (float x = x0 + 0.5f; x < x1; x += 1f)
                for (float z = z0 + 0.5f; z < z1; z += 1f)
                    foreach (var c in circles) { float dx = x - c.X, dz = z - c.Z; if (dx * dx + dz * dz <= c.R * c.R) { n++; break; } }
            return n;
        }

        private static FatStudioRegion.Circle Circle(Vector3 p, float r) => new FatStudioRegion.Circle { X = p.x, Z = p.z, R = r };
        private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        private static bool Ready(out string reason)
        {
            reason = null;
            if (ZNet.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null || WorldGenerator.instance == null) { reason = "no world"; return false; }
            if (!ZNet.instance.IsServer()) { reason = "not host: Studio water and vegetation run on the server/host only"; return false; }
            return true;
        }

        private static byte[] Sha(byte[] data) { using (var sha = SHA256.Create()) return sha.ComputeHash(data); }
        private static bool Same(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    }
}
