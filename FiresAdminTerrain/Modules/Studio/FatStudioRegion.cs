using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Studio
{
    // 0.2.1246 (World Studio v3, water + vegetation): one undo for an edit whose effects are spread out and partly ASYNC (FAT's water
    // lays its surface ~1-2 s after the carve and its combiner anchors save after a debounce; a river carve runs as a budgeted job).
    // Before the edit the REGION is captured: each touched zone's TerrainComp data (FatStudioTerrain.ZoneData), every world object
    // inside the footprint circles, and every voxel / water anchor anywhere in those zones (a combiner or voxel chunk can sit outside
    // the circle and still change). Restore puts the region back as captured, whatever the edit did later: terrain written back;
    // objects that are new or whose data changed are destroyed, and every captured one that is missing or changed is made again
    // from its full network data (ZDO.Serialize; new ids). Never captured, never touched: players, tombstones, creatures, dropped
    // items, and anything a player built (creator != 0).
    internal static class FatStudioRegion
    {
        internal struct Circle { public float X, Z, R; }
        private const int MaxZones = 9, MaxObjects = 40000;
        private const float SamePos = 0.05f;
        private static readonly int PlayerHash = "Player".GetStableHashCode();
        private static readonly int TombstoneHash = "Player_tombstone".GetStableHashCode();
        private static readonly HashSet<int> ZoneWide = new HashSet<int>
        {
            "VAVoxel".GetStableHashCode(), "CustomRiver".GetStableHashCode(), "FiresLiquidWater".GetStableHashCode(),
            "VALandIce".GetStableHashCode(), "VALandSnow".GetStableHashCode(),
        };
        private static readonly Dictionary<int, bool> s_skip = new Dictionary<int, bool>();

        internal static bool Zones(List<Circle> circles, out List<Vector2s> zones, out string reason)
        {
            var set = new HashSet<Vector2s>();
            foreach (var c in circles)
            {
                Vector2s a = ZoneSystem.GetZone(new Vector3(c.X - c.R, 0f, c.Z - c.R)), b = ZoneSystem.GetZone(new Vector3(c.X + c.R, 0f, c.Z + c.R));
                for (int x = a.x; x <= b.x; x++) for (int z = a.y; z <= b.y; z++) { set.Add(new Vector2s(x, z)); if (set.Count > MaxZones) break; }
                if (set.Count > MaxZones) break;
            }
            zones = new List<Vector2s>(set);
            reason = set.Count > MaxZones ? $"tooLarge: the edit reaches more than {MaxZones} zones, split it" : null;
            return reason == null;
        }

        internal static bool Capture(List<Circle> circles, out byte[] bytes, out string reason)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1247: Debug timing (the reason carries the ms)
            bytes = null;
            if (!Zones(circles, out var zones, out reason)) return false;
            var objs = new List<ZDO>();
            foreach (var z in Objects(zones)) if (Kept(z, circles)) objs.Add(z);
            if (objs.Count > MaxObjects) { reason = $"tooLarge: more than {MaxObjects} objects in the area"; return false; }
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(circles.Count);
                    foreach (var c in circles) { w.Write(c.X); w.Write(c.Z); w.Write(c.R); }
                    w.Write(zones.Count);
                    foreach (var zone in zones)
                    {
                        if (!FatStudioTerrain.CaptureFoot(zone, circles, out byte[] tc)) { reason = $"not loaded: zone {zone.x},{zone.y}"; return false; }   // 0.2.1249: the footprint, not the whole zone
                        w.Write((int)zone.x); w.Write((int)zone.y);
                        w.Write(tc == null ? -1 : tc.Length);
                        if (tc != null) w.Write(tc);
                    }
                    w.Write(objs.Count);
                    foreach (var z in objs)
                    {
                        var pkg = new ZPackage(); z.Serialize(pkg); byte[] data = pkg.GetArray();
                        w.Write(z.GetPrefab()); WriteV3(w, z.GetPosition()); WriteV3(w, z.GetRotation().eulerAngles);
                        w.Write(data.Length); w.Write(data);
                    }
                }
                bytes = ms.ToArray();
            }
            reason = $"region: {zones.Count} zone(s), {objs.Count} object(s), {bytes.Length / 1024f:0.0} KB in {Ms(t0):0.0} ms";
            return true;
        }

        private struct Obj { public int Prefab; public Vector3 Pos, Euler; public byte[] Data; }

        internal static bool Restore(byte[] bytes, out string reason) => Restore(bytes, true, out reason);

        // foot = false: a 1246-1248 snapshot (its zones hold whole TCData, written back whole)
        internal static bool Restore(byte[] bytes, bool foot, out string reason)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var circles = new List<Circle>();
            var zones = new List<(Vector2s zone, byte[] tc)>();
            var before = new List<Obj>();
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes)))
                {
                    int nc = r.ReadInt32(); if (nc < 0 || nc > 4096) { reason = "bad snapshot: circles"; return false; }
                    for (int i = 0; i < nc; i++) circles.Add(new Circle { X = r.ReadSingle(), Z = r.ReadSingle(), R = r.ReadSingle() });
                    int nz = r.ReadInt32(); if (nz < 0 || nz > MaxZones) { reason = "bad snapshot: zones"; return false; }
                    for (int i = 0; i < nz; i++)
                    {
                        var zone = new Vector2s(r.ReadInt32(), r.ReadInt32());
                        int len = r.ReadInt32(); if (len > 4 * 1024 * 1024) { reason = "bad snapshot: zone data too large"; return false; }
                        zones.Add((zone, len < 0 ? null : r.ReadBytes(len)));
                    }
                    int no = r.ReadInt32(); if (no < 0 || no > MaxObjects) { reason = "bad snapshot: objects"; return false; }
                    for (int i = 0; i < no; i++)
                    {
                        var o = new Obj { Prefab = r.ReadInt32(), Pos = ReadV3(r), Euler = ReadV3(r) };
                        int len = r.ReadInt32(); if (len < 0 || len > 1024 * 1024) { reason = "bad snapshot: object data too large"; return false; }
                        o.Data = r.ReadBytes(len);
                        before.Add(o);
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is IOException) { reason = $"bad snapshot: {ex.GetType().Name}"; return false; }

            foreach (var (zone, _) in zones) if (FatStudioTerrain.MapOf(zone) == null) { reason = $"not loaded: zone {zone.x},{zone.y}"; return false; }
            foreach (var (zone, tc) in zones) if (!(foot ? FatStudioTerrain.WriteFoot(zone, circles, tc ?? new byte[] { 0 }, out reason) : FatStudioTerrain.WriteZone(zone, tc, out reason))) return false;

            // objects: an unchanged captured one stays; anything else in the region goes and the captured set comes back
            var zoneList = new List<Vector2s>(); foreach (var (zone, _) in zones) zoneList.Add(zone);
            var want = new List<Obj>(before);
            long me = ZDOMan.GetSessionID();
            int kept = 0, gone = 0, made = 0;
            foreach (var z in Objects(zoneList))
            {
                if (!Kept(z, circles)) continue;
                int hit = -1;
                for (int i = 0; i < want.Count; i++)
                    if (want[i].Prefab == z.GetPrefab() && (want[i].Pos - z.GetPosition()).sqrMagnitude <= SamePos * SamePos && SameData(want[i].Data, z)) { hit = i; break; }
                if (hit >= 0) { want.RemoveAt(hit); kept++; continue; }
                z.SetOwner(me); ZDOMan.instance.DestroyZDO(z); gone++;
            }
            foreach (var o in want)
            {
                var z = ZDOMan.instance.CreateNewZDO(o.Pos, o.Prefab);
                z.Deserialize(new ZPackage(o.Data));
                z.SetOwner(me); z.SetPosition(o.Pos); z.SetRotation(Quaternion.Euler(o.Euler));
                made++;
            }
            reason = $"ok: {zones.Count} zone(s) of terrain written back; objects {kept} unchanged, {gone} removed, {made} made again; {Ms(t0):0.0} ms";
            return true;
        }

        // Inside a footprint circle, or a voxel / water anchor anywhere in the zones; and never a player, tombstone, creature, item
        // or player-built object.
        private static bool Kept(ZDO z, List<Circle> circles)
        {
            int prefab = z.GetPrefab();
            if (prefab == PlayerHash || prefab == TombstoneHash || z.GetLong(ZDOVars.s_creator) != 0L || Skip(prefab)) return false;
            if (ZoneWide.Contains(prefab)) return true;
            var p = z.GetPosition();
            foreach (var c in circles) { float dx = p.x - c.X, dz = p.z - c.Z; if (dx * dx + dz * dz <= c.R * c.R) return true; }
            return false;
        }

        private static bool Skip(int prefab)
        {
            if (s_skip.TryGetValue(prefab, out bool skip)) return skip;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            skip = go == null || go.GetComponent<Character>() != null || go.GetComponent<ItemDrop>() != null;
            s_skip[prefab] = skip;
            return skip;
        }

        private static bool SameData(byte[] data, ZDO z)
        {
            var pkg = new ZPackage(); z.Serialize(pkg); byte[] now = pkg.GetArray();
            if (now.Length != data.Length) return false;
            for (int i = 0; i < now.Length; i++) if (now[i] != data[i]) return false;
            return true;
        }

        private static List<ZDO> Objects(List<Vector2s> zones)
        {
            var all = new List<ZDO>(); var one = new List<ZDO>();
            foreach (var zone in zones)
            {
                one.Clear();
                ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), one);
                foreach (var z in one) if (z != null && z.IsValid()) all.Add(z);
            }
            return all;
        }

        private static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
