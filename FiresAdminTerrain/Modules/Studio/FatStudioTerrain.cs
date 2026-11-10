using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Studio
{
    // 0.2.1242 (Fire 10-29, "Studio FAT first"; signatures settled with [visual], plan §0d.4a/§0d.4b): the World Studio's terrain
    // edit + exact undo. An edit is vanilla's own TerrainOp settings run through TerrainComp.DoOperation as the comp's owner (the
    // owner path of RPC_ApplyOperation, as the water tool's vanilla cut does), so it saves and syncs exactly like the hoe. Before
    // the edit every TerrainComp the stroke reaches is snapshotted WHOLE: its ZDO "TCData" bytes, or "absent" when the edit is
    // what creates it. Restore writes those bytes back verbatim (Load + Poke on the host; peers reload through CheckLoad), and a
    // comp the edit created is emptied, saved and then destroyed, so the world's terrain data is byte-identical to before.
    // Stale check: the snapshot carries each comp's TCData hash AFTER the edit; Restore refuses "stale" when any differs now.
    internal static class FatStudioTerrain
    {
        internal const string Kind = "terrain";
        internal const int Version = 2;   // 0.2.1248: v2 = footprint stale check + footprint write-back (v1 = whole zone, still restored)
        private const int MaxZones = 9;
        private const float MaxRadius = 64f;
        private const float DestroyDelay = 3f;
        private const string Tag = "[FatStudio]";

        private static readonly AccessTools.FieldRef<TerrainComp, ZNetView> CompNview = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> CompHmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> LevelDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> SmoothDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedHeight = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> PaintMask = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedPaint = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly System.Reflection.MethodInfo DoOperation = AccessTools.Method(typeof(TerrainComp), "DoOperation",
            new[] { typeof(Vector3), typeof(Vector3), typeof(TerrainOp.Settings) });
        private static readonly System.Reflection.MethodInfo SaveMethod = AccessTools.Method(typeof(TerrainComp), "Save");
        private static readonly System.Reflection.MethodInfo LoadMethod = AccessTools.Method(typeof(TerrainComp), "Load");
        private static readonly List<Heightmap> s_maps = new List<Heightmap>();

        private struct Entry { public Vector2s Zone; public byte[] Before; public byte[] AfterHash; public List<Foot> Foot; }
        // 0.2.1248: one vertex of the edit's footprint, its raw TerrainComp values before and after the edit
        private struct Vals { public bool MH, MP; public float L, S; public Color P; }
        private struct Foot { public int Idx; public Vals B, A; }
        private const float FootMargin = 2f;

        internal static bool Edit(FatTerrainEditArgs a, out FatSnapshot before, out string reason)
        {
            before = null;
            if (!Ready(out reason)) return false;
            if (a == null || a.Points == null || a.Points.Length == 0) { reason = "bad args: no points"; return false; }
            if (!(a.Radius > 0f) || a.Radius > MaxRadius) { reason = $"bad args: radius {a.Radius} (0 < r <= {MaxRadius})"; return false; }
            if (VoxelOnlyMode.Active) { reason = "unsupported: voxel-only world (Studio terrain v1 edits the heightmap)"; return false; }
            if (!Settings(a, out var settings, out reason)) return false;
            if (!Begin(a.Points, a.Radius, out var st, out reason)) return false;
            foreach (var p in a.Points)
            {
                Vector3 at = a.Op == FatTerrainOp.Level ? new Vector3(p.x, a.Height, p.z) : p;
                foreach (var kv in st.Comps)
                {
                    if (!Reaches(st.Maps[kv.Key], at, a.Radius + 2f)) continue;
                    DoOperation.Invoke(kv.Value, new object[] { at, Vector3.zero, settings });
                }
            }
            before = Finish(st);
            reason = $"ok: {a.Op} {a.Points.Length} point(s) r {a.Radius:0.0} on {st.Comps.Count} TerrainComp(s)";
            FiresLogger.LogDebug($"{Tag} TerrainEdit {reason}; snapshot {before.Id} {before.Bytes.Length} bytes");
            return true;
        }

        // 0.2.1293 ([lead], Studio walk mode: the hoe / cultivator's vanilla terrain ops get an undo): TerrainEdit's own snapshot halves
        // around ANY edit - the before bytes + comps owned (Begin), the caller's edit (e.g. vanilla Player.PlacePiece of a TerrainOp
        // entry, which applies on the owned comps at once), then Save + the after hash + footprint (Finish) that Restore's stale check needs.
        internal static bool Use(Vector3 center, float radius, System.Action edit, out FatSnapshot before, out string reason)
        {
            before = null;
            if (!Ready(out reason)) return false;
            if (edit == null || !(radius > 0f) || radius > MaxRadius) { reason = $"bad args: radius {radius} (0 < r <= {MaxRadius})"; return false; }
            if (VoxelOnlyMode.Active) { reason = "unsupported: voxel-only world (Studio terrain v1 edits the heightmap)"; return false; }
            var pts = new[] { center };
            if (!Begin(pts, radius, out var st, out reason)) return false;
            edit();
            before = Finish(st);
            reason = $"ok: edit at ({center.x:0.0},{center.z:0.0}) r {radius:0.0} on {st.Comps.Count} TerrainComp(s)";
            FiresLogger.LogDebug($"{Tag} TerrainUse {reason}; snapshot {before.Id} {before.Bytes.Length} bytes");
            return true;
        }

        private sealed class EditState { public Dictionary<Vector2s, Heightmap> Maps; public Dictionary<Vector2s, TerrainComp> Comps; public List<Entry> Entries; public Dictionary<Vector2s, List<Foot>> Feet; }

        private static bool Begin(Vector3[] points, float radius, out EditState st, out string reason)
        {
            st = null; reason = null;
            // Every heightmap the stroke reaches, by zone; each must be loaded here.
            var maps = new Dictionary<Vector2s, Heightmap>();
            foreach (var p in points)
            {
                s_maps.Clear();
                Heightmap.FindHeightmap(p, radius + 2f, s_maps);
                if (s_maps.Count == 0) { reason = $"not loaded: no heightmap at ({p.x:0},{p.z:0})"; return false; }
                foreach (var hm in s_maps) if (hm != null) maps[ZoneSystem.GetZone(hm.transform.position)] = hm;
                if (maps.Count > MaxZones) { reason = $"tooLarge: the stroke reaches more than {MaxZones} zones, split it"; return false; }
            }

            // Snapshot first (whole comps), then create / own them all, then edit.
            var entries = new List<Entry>();
            var comps = new Dictionary<Vector2s, TerrainComp>();
            foreach (var kv in maps)
            {
                var existing = TerrainComp.FindTerrainCompiler(kv.Value.transform.position);
                entries.Add(new Entry { Zone = kv.Key, Before = existing != null ? Data(existing) ?? new byte[0] : null });
            }
            foreach (var kv in maps)
            {
                TerrainComp comp = null;
                try { comp = kv.Value.GetAndCreateTerrainCompiler(); } catch { }
                var nview = comp != null ? CompNview(comp) : null;
                if (nview == null || !nview.IsValid()) { reason = $"not loaded: no terrain compiler in zone {kv.Key.x},{kv.Key.y}"; return false; }
                if (!nview.IsOwner()) nview.ClaimOwnership();
                if (!nview.IsOwner()) { reason = $"not owner: zone {kv.Key.x},{kv.Key.y}"; return false; }
                comps[kv.Key] = comp;
            }
            // 0.2.1248 (round H: a raise's undo went "stale" because the snow-trail refill rewrote the same zone 2 s later): the
            // footprint's vertices before the edit; the stale check and the write-back use only these.
            var feet = new Dictionary<Vector2s, List<Foot>>();
            foreach (var kv in comps) feet[kv.Key] = FootOf(maps[kv.Key], kv.Value, points, radius + FootMargin);
            st = new EditState { Maps = maps, Comps = comps, Entries = entries, Feet = feet };
            return true;
        }

        private static FatSnapshot Finish(EditState st)
        {
            var comps = st.Comps; var entries = st.Entries; var feet = st.Feet;
            // 0.2.1244 (round F, 1243: 0/5 "stale: zone -5,2"; the log's "[FiresFixes] terrain saves batched ... 1 save(s) avoided"):
            // FiresFixes' TerrainSaveBatch runs the op but defers Save to the comp's next Update, so the hash below was taken from
            // the PRE-edit bytes and the deferred save then made every restore stale. Save each comp here (vanilla Save writes the
            // current arrays; the batch's later flush rewrites the same bytes), so the post-edit hash is the edit's real data.
            foreach (var comp in comps.Values) SaveMethod.Invoke(comp, new object[] { false });
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                e.AfterHash = Sha(Data(comps[e.Zone]));
                var fl = feet[e.Zone];
                for (int k = 0; k < fl.Count; k++) { var f = fl[k]; f.A = Read(comps[e.Zone], f.Idx); fl[k] = f; }
                e.Foot = fl;
                entries[i] = e;
            }
            return Pack(entries);
        }

        internal static bool Restore(FatSnapshot s, out string reason) => Restore(s, false, out reason);

        // force (0.2.1248, FatApi.RestoreForce): no stale check; a v1 snapshot is written back whole, a v2 one over its footprint.
        internal static bool Restore(FatSnapshot s, bool force, out string reason)
        {
            if (!Ready(out reason)) return false;
            if (!Unpack(s, out var entries, out reason)) return false;
            if (s.Version >= 2) return RestoreFoot(s, entries, force, out reason);
            // Stale check over every zone first: nothing is written unless all match.
            var found = new Dictionary<Vector2s, Heightmap>();
            foreach (var e in entries)
            {
                Vector3 zp = ZoneSystem.GetZonePos(e.Zone);
                s_maps.Clear();
                Heightmap.FindHeightmap(zp, 1f, s_maps);
                Heightmap hm = null;
                foreach (var m in s_maps) if (m != null && ZoneSystem.GetZone(m.transform.position) == e.Zone) { hm = m; break; }
                if (hm == null) { reason = $"not loaded: zone {e.Zone.x},{e.Zone.y}"; return false; }
                var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
                byte[] now = comp != null ? Sha(Data(comp)) : null;
                if (!force && !SameHash(now, e.AfterHash)) { reason = $"stale: zone {e.Zone.x},{e.Zone.y} changed since the edit"; return false; }
                found[e.Zone] = hm;
            }
            int written = 0, emptied = 0;
            foreach (var e in entries)
            {
                var hm = found[e.Zone];
                var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
                if (comp == null) continue;   // created and already gone: nothing to do
                var nview = CompNview(comp);
                if (nview == null || !nview.IsValid()) { reason = $"not loaded: zone {e.Zone.x},{e.Zone.y}"; return false; }
                if (!nview.IsOwner()) nview.ClaimOwnership();
                if (e.Before != null)
                {
                    nview.GetZDO().Set(ZDOVars.s_TCData, e.Before);
                    LoadMethod.Invoke(comp, null);
                    written++;
                }
                else
                {
                    Array.Clear(ModifiedHeight(comp), 0, ModifiedHeight(comp).Length);
                    Array.Clear(LevelDelta(comp), 0, LevelDelta(comp).Length);
                    Array.Clear(SmoothDelta(comp), 0, SmoothDelta(comp).Length);
                    Array.Clear(ModifiedPaint(comp), 0, ModifiedPaint(comp).Length);
                    Array.Clear(PaintMask(comp), 0, PaintMask(comp).Length);
                    SaveMethod.Invoke(comp, new object[] { false });
                    DestroyLater(nview);
                    emptied++;
                }
                (CompHmap(comp) ?? hm).Poke(1);
                ClutterSystem.instance?.ResetGrass(ZoneSystem.GetZonePos(e.Zone), 45f);
            }
            reason = $"ok: {written} TerrainComp(s) written back, {emptied} created by the edit removed";
            FiresLogger.LogDebug($"{Tag} Restore {s.Id}: {reason}");
            return true;
        }

        // v2: "stale" only when a footprint vertex differs from its after-edit value; the write-back puts back only the footprint's
        // before values (snow trails and other edits elsewhere in the zone stay). A comp the edit created goes again once nothing
        // else in it was ever modified (then the zone is byte-identical to before).
        private static bool RestoreFoot(FatSnapshot s, List<Entry> entries, bool force, out string reason)
        {
            var found = new Dictionary<Vector2s, TerrainComp>();
            foreach (var e in entries)
            {
                var hm = MapOf(e.Zone);
                if (hm == null) { reason = $"not loaded: zone {e.Zone.x},{e.Zone.y}"; return false; }
                var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
                if (comp == null)
                {
                    if (e.Before == null && !force) continue;   // created by the edit and already gone
                    if (!force) { reason = $"stale: zone {e.Zone.x},{e.Zone.y} lost its terrain data since the edit"; return false; }
                    try { comp = hm.GetAndCreateTerrainCompiler(); } catch { }
                    if (comp == null) { reason = $"not loaded: zone {e.Zone.x},{e.Zone.y}"; return false; }
                }
                // 0.2.1255 (round N: undo refused "stale" while it snowed: the snow refill touched an UNCHANGED footprint vertex): only the
                // vertices the edit changed (before != after) are checked and written back; a refill on their neighbours is not the edit's.
                if (!force && e.Foot != null)
                    foreach (var f in e.Foot)
                        if (!Same(f.B, f.A) && (f.Idx >= ModifiedHeight(comp).Length || !Same(Read(comp, f.Idx), f.A)))
                        { reason = $"stale: the edited ground in zone {e.Zone.x},{e.Zone.y} changed since the edit"; return false; }
                found[e.Zone] = comp;
            }
            int vertices = 0, removed = 0;
            foreach (var e in entries)
            {
                if (!found.TryGetValue(e.Zone, out var comp)) continue;
                var nview = CompNview(comp);
                if (nview == null || !nview.IsValid()) { reason = $"not loaded: zone {e.Zone.x},{e.Zone.y}"; return false; }
                if (!nview.IsOwner()) nview.ClaimOwnership();
                SaveMethod.Invoke(comp, new object[] { false });   // a deferred save must not land over the write-back
                if (e.Foot != null) foreach (var f in e.Foot) { if (f.Idx < ModifiedHeight(comp).Length && (force || !Same(f.B, f.A))) { Write(comp, f.Idx, f.B); vertices++; } }
                SaveMethod.Invoke(comp, new object[] { false });
                if (e.Before == null && !AnyModified(comp)) { DestroyLater(nview); removed++; }
                var hmap = CompHmap(comp) ?? MapOf(e.Zone);
                if (hmap != null) hmap.Poke(1);
                ClutterSystem.instance?.ResetGrass(ZoneSystem.GetZonePos(e.Zone), 45f);
            }
            reason = $"ok: {vertices} footprint vertex(es) written back in {found.Count} zone(s), {removed} TerrainComp(s) the edit created removed{(force ? " (forced)" : "")}";
            FiresLogger.LogDebug($"{Tag} Restore v2 {s.Id}: {reason}");
            return true;
        }

        // Every vertex of comp's grid within r of any point (on a zone border each comp keeps its own copy of a shared edge vertex).
        private static List<Foot> FootOf(Heightmap hm, TerrainComp comp, Vector3[] points, float r)
        {
            var set = new HashSet<int>();
            var list = new List<Foot>();
            int len = ModifiedHeight(comp).Length, w = Mathf.RoundToInt(Mathf.Sqrt(len));
            foreach (var p in points)
            {
                hm.WorldToVertex(new Vector3(p.x - r, 0f, p.z - r), out int x0, out int y0);
                hm.WorldToVertex(new Vector3(p.x + r, 0f, p.z + r), out int x1, out int y1);
                for (int y = Mathf.Max(0, y0); y <= Mathf.Min(w - 1, y1); y++)
                    for (int x = Mathf.Max(0, x0); x <= Mathf.Min(w - 1, x1); x++)
                    {
                        int idx = y * w + x;
                        if (!set.Add(idx)) continue;
                        list.Add(new Foot { Idx = idx, B = Read(comp, idx) });
                    }
            }
            return list;
        }

        private static Vals Read(TerrainComp c, int i) => new Vals { MH = ModifiedHeight(c)[i], L = LevelDelta(c)[i], S = SmoothDelta(c)[i], MP = ModifiedPaint(c)[i], P = PaintMask(c)[i] };
        private static void Write(TerrainComp c, int i, Vals v) { ModifiedHeight(c)[i] = v.MH; LevelDelta(c)[i] = v.L; SmoothDelta(c)[i] = v.S; ModifiedPaint(c)[i] = v.MP; PaintMask(c)[i] = v.P; }
        private static bool Same(Vals a, Vals b) => a.MH == b.MH && a.MP == b.MP && a.L == b.L && a.S == b.S && a.P == b.P;
        private static bool AnyModified(TerrainComp c) { foreach (bool b in ModifiedHeight(c)) if (b) return true; foreach (bool b in ModifiedPaint(c)) if (b) return true; return false; }
        private static void WriteVals(BinaryWriter w, Vals v) { w.Write((byte)((v.MH ? 1 : 0) | (v.MP ? 2 : 0))); w.Write(v.L); w.Write(v.S); w.Write(v.P.r); w.Write(v.P.g); w.Write(v.P.b); w.Write(v.P.a); }
        private static Vals ReadVals(BinaryReader r) { byte f = r.ReadByte(); return new Vals { MH = (f & 1) != 0, MP = (f & 2) != 0, L = r.ReadSingle(), S = r.ReadSingle(), P = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) }; }

        // ── 0.2.1249: the region snapshots' terrain as a FOOTPRINT (round I: the whole-zone write-back reverted the snow-trail refill
        // across the zone and re-grounded trees 20 m away). Bytes: byte 0 = no TerrainComp then; byte 1 + int n + n x (int idx, Vals).
        internal static bool CaptureFoot(Vector2s zone, List<FatStudioRegion.Circle> circles, out byte[] data)
        {
            data = null;
            var hm = MapOf(zone);
            if (hm == null) return false;
            var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms))
                {
                    if (comp == null) w.Write((byte)0);
                    else
                    {
                        SaveMethod.Invoke(comp, new object[] { false });   // a deferred save lands first (0.2.1244)
                        var foot = FootOfCircles(hm, comp, circles);
                        w.Write((byte)1); w.Write(foot.Count);
                        foreach (var f in foot) { w.Write(f.Idx); WriteVals(w, f.B); }
                    }
                }
                data = ms.ToArray();
            }
            return true;
        }

        internal static bool WriteFoot(Vector2s zone, List<FatStudioRegion.Circle> circles, byte[] data, out string reason)
        {
            reason = null;
            var hm = MapOf(zone);
            if (hm == null) { reason = $"not loaded: zone {zone.x},{zone.y}"; return false; }
            var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
            bool had = data.Length > 0 && data[0] == 1;
            if (comp == null && !had) return true;
            if (comp == null) { try { comp = hm.GetAndCreateTerrainCompiler(); } catch { } }
            var nview = comp != null ? CompNview(comp) : null;
            if (nview == null || !nview.IsValid()) { reason = $"not loaded: zone {zone.x},{zone.y}"; return false; }
            if (!nview.IsOwner()) nview.ClaimOwnership();
            SaveMethod.Invoke(comp, new object[] { false });
            int len = ModifiedHeight(comp).Length;
            if (had)
                using (var r = new BinaryReader(new MemoryStream(data, 1, data.Length - 1)))
                {
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++) { int idx = r.ReadInt32(); var v = ReadVals(r); if (idx >= 0 && idx < len) Write(comp, idx, v); }
                }
            else foreach (var f in FootOfCircles(hm, comp, circles)) Write(comp, f.Idx, default(Vals));   // the edit made this comp: its footprint goes back to unmodified
            SaveMethod.Invoke(comp, new object[] { false });
            if (!had && !AnyModified(comp)) DestroyLater(nview);
            var hmap = CompHmap(comp) ?? hm;
            hmap.Poke(1);
            ClutterSystem.instance?.ResetGrass(ZoneSystem.GetZonePos(zone), 45f);
            return true;
        }

        private static List<Foot> FootOfCircles(Heightmap hm, TerrainComp comp, List<FatStudioRegion.Circle> circles)
        {
            var seen = new HashSet<int>(); var all = new List<Foot>();
            foreach (var c in circles)
                foreach (var f in FootOf(hm, comp, new[] { new Vector3(c.X, 0f, c.Z) }, c.R))
                    if (seen.Add(f.Idx)) all.Add(f);
            return all;
        }

        // ── 0.2.1246: the same per-zone capture / write-back for the Studio's region snapshots (water, vegetation) ──
        internal static Heightmap MapOf(Vector2s zone)
        {
            s_maps.Clear();
            Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zone), 1f, s_maps);
            foreach (var m in s_maps) if (m != null && ZoneSystem.GetZone(m.transform.position) == zone) return m;
            return null;
        }

        // The zone's TCData now (null = no TerrainComp there). false = its heightmap is not loaded.
        internal static bool ZoneData(Vector2s zone, out byte[] data)
        {
            data = null;
            var hm = MapOf(zone);
            if (hm == null) return false;
            var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
            if (comp != null)
            {
                SaveMethod.Invoke(comp, new object[] { false });   // a FiresFixes-deferred save lands first (0.2.1244)
                data = Data(comp) ?? new byte[0];
            }
            return true;
        }

        // Write a zone's TCData back as captured (null = it had none: the comp is emptied, then removed), exactly as Restore does.
        internal static bool WriteZone(Vector2s zone, byte[] before, out string reason)
        {
            reason = null;
            var hm = MapOf(zone);
            if (hm == null) { reason = $"not loaded: zone {zone.x},{zone.y}"; return false; }
            var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
            if (comp == null && before == null) return true;
            if (comp == null) { try { comp = hm.GetAndCreateTerrainCompiler(); } catch { } }
            var nview = comp != null ? CompNview(comp) : null;
            if (nview == null || !nview.IsValid()) { reason = $"not loaded: zone {zone.x},{zone.y}"; return false; }
            if (!nview.IsOwner()) nview.ClaimOwnership();
            if (before != null)
            {
                SaveMethod.Invoke(comp, new object[] { false });   // flush a deferred save so it cannot land over the restore
                nview.GetZDO().Set(ZDOVars.s_TCData, before);
                LoadMethod.Invoke(comp, null);
            }
            else
            {
                Array.Clear(ModifiedHeight(comp), 0, ModifiedHeight(comp).Length);
                Array.Clear(LevelDelta(comp), 0, LevelDelta(comp).Length);
                Array.Clear(SmoothDelta(comp), 0, SmoothDelta(comp).Length);
                Array.Clear(ModifiedPaint(comp), 0, ModifiedPaint(comp).Length);
                Array.Clear(PaintMask(comp), 0, PaintMask(comp).Length);
                SaveMethod.Invoke(comp, new object[] { false });
                DestroyLater(nview);
            }
            (CompHmap(comp) ?? hm).Poke(1);
            ClutterSystem.instance?.ResetGrass(ZoneSystem.GetZonePos(zone), 45f);
            return true;
        }

        internal static uint Revision(Vector2s zone)
        {
            var comp = TerrainComp.FindTerrainCompiler(ZoneSystem.GetZonePos(zone));
            var nview = comp != null ? CompNview(comp) : null;
            return nview != null && nview.IsValid() ? nview.GetZDO().DataRevision : 0u;
        }

        // `fat_studio_terrain_test` (host, admin): at a spot 12 m in front of you, each op (raise, dig, smooth, level, paint) is
        // applied and restored; PASS = the terrain data changed after the edit and is byte-identical to before after the restore
        // (a comp the edit created must be gone again). Every line says why it passed or failed.
        // 0.2.1252 ([lead] / Fire 10-29: "are these terrain edits supposed to be here still?", the Studio tests' leftovers on RPGWorld):
        // `fat_studio_reset_ground <x> <z> <radius> [<x> <z> <radius> ...]` (host, admin) puts every TerrainComp vertex inside each
        // circle back to the GENERATED ground: height edits (level + smooth deltas) and paint cleared, the comp saved and redrawn, the
        // same write as the Studio's footprint restore. Each circle logs what it reset and the ground height at its centre before /
        // after. A comp left with no edits at all is removed, as vanilla never had one there.
        private static void RegisterResetCommand()
        {
            new Terminal.ConsoleCommand("fat_studio_reset_ground", "<x> <z> <radius> [...] - host: reset the terrain in each circle to the generated ground (height + paint)",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (!Ready(out string why)) { Say(args.Context, $"refused: {why}"); return; }
                    if (args.Length < 4 || (args.Length - 1) % 3 != 0) { Say(args.Context, "usage: fat_studio_reset_ground <x> <z> <radius> [<x> <z> <radius> ...]"); return; }
                    for (int i = 1; i + 2 < args.Length; i += 3)
                    {
                        if (!float.TryParse(args[i], System.Globalization.NumberStyles.Float, inv, out float x) || !float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, inv, out float z)
                            || !float.TryParse(args[i + 2], System.Globalization.NumberStyles.Float, inv, out float r) || !(r > 0f) || r > MaxRadius)
                        { Say(args.Context, $"bad circle at argument {i}: radius 0..{MaxRadius}"); continue; }
                        Say(args.Context, ResetCircle(x, z, r));
                    }
                }, isCheat: true);
        }

        // 0.2.1253 (Fire 10-29 "Yes, restore them": only our test edits on RPGWorld's town, from the pre-test 2-Aug copy):
        // `fat_studio_restore_ground_ref <file.tsv> <minX> <maxX> <minZ> <maxZ>` (host, admin). The tsv is `wsr tcref`'s output
        // (Tools/WorldSaveReader, read-only on both saves): one line per vertex, zone x, zone z, index, then the REFERENCE save's
        // raw TerrainComp values. Every vertex inside the box whose live value differs is written back to the reference value
        // (nothing outside the box, nothing already equal); per zone it logs the vertices restored (height / paint) and the box
        // centre's ground before -> after.
        private static void RegisterRestoreRefCommand()
        {
            new Terminal.ConsoleCommand("fat_studio_restore_ground_ref", "<file.tsv> <minX> <maxX> <minZ> <maxZ> - host: write back the reference terrain values (wsr tcref) inside the box",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (!Ready(out string why)) { Say(args.Context, $"refused: {why}"); return; }
                    if (args.Length != 6 || !float.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out float x0) || !float.TryParse(args[3], System.Globalization.NumberStyles.Float, inv, out float x1)
                        || !float.TryParse(args[4], System.Globalization.NumberStyles.Float, inv, out float z0) || !float.TryParse(args[5], System.Globalization.NumberStyles.Float, inv, out float z1))
                    { Say(args.Context, "usage: fat_studio_restore_ground_ref <file.tsv> <minX> <maxX> <minZ> <maxZ>"); return; }
                    string path = args[1].Trim('"');
                    if (!Path.IsPathRooted(path)) path = Path.Combine(FiresCachePaths.For("FiresAdminTerrain", "Studio"), path);
                    if (!File.Exists(path)) { Say(args.Context, $"refused: no file {path}"); return; }
                    foreach (var line in RestoreFromRef(path, Mathf.Min(x0, x1), Mathf.Max(x0, x1), Mathf.Min(z0, z1), Mathf.Max(z0, z1))) Say(args.Context, line);
                }, isCheat: true);
        }

        private static List<string> RestoreFromRef(string path, float x0, float x1, float z0, float z1)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var byZone = new Dictionary<Vector2s, List<(int idx, Vals v)>>();
            int lines = 0, bad = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                var f = raw.Split('\t');
                if (f.Length < 11) { bad++; continue; }
                try
                {
                    var zone = new Vector2s(int.Parse(f[0], inv), int.Parse(f[1], inv));
                    var v = new Vals { MH = f[3] == "1", L = float.Parse(f[4], inv), S = float.Parse(f[5], inv), MP = f[6] == "1", P = new Color(float.Parse(f[7], inv), float.Parse(f[8], inv), float.Parse(f[9], inv), float.Parse(f[10], inv)) };
                    if (!byZone.TryGetValue(zone, out var l)) byZone[zone] = l = new List<(int, Vals)>();
                    l.Add((int.Parse(f[2], inv), v));
                    lines++;
                }
                catch (FormatException) { bad++; }
            }
            var outLines = new List<string> { $"restore from {Path.GetFileName(path)}: {lines} reference vertex line(s) ({bad} unreadable), box x {x0:0.#}..{x1:0.#} z {z0:0.#}..{z1:0.#}" };
            var c = new Vector3((x0 + x1) * 0.5f, 0f, (z0 + z1) * 0.5f);
            float before = Heightmap.GetHeight(c, out float hb) ? hb : float.NaN;
            int total = 0;
            foreach (var kv in byZone)
            {
                var hm = MapOf(kv.Key);
                if (hm == null) { outLines.Add($"  zone {kv.Key.x},{kv.Key.y}: NOT loaded, skipped (stand near it and run again)"); continue; }
                var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
                if (comp == null) { try { comp = hm.GetAndCreateTerrainCompiler(); } catch { } }
                var nview = comp != null ? CompNview(comp) : null;
                if (nview == null || !nview.IsValid()) { outLines.Add($"  zone {kv.Key.x},{kv.Key.y}: no terrain compiler, skipped"); continue; }
                if (!nview.IsOwner()) nview.ClaimOwnership();
                SaveMethod.Invoke(comp, new object[] { false });
                int len = ModifiedHeight(comp).Length, w = Mathf.RoundToInt(Mathf.Sqrt(len));
                Vector3 zc = ZoneSystem.GetZonePos(kv.Key);
                int inBox = 0, same = 0, rh = 0, rp = 0;
                foreach (var (idx, v) in kv.Value)
                {
                    if (idx < 0 || idx >= len) continue;
                    float wx = zc.x - 32f + idx % w, wz = zc.z - 32f + idx / w;
                    if (wx < x0 || wx > x1 || wz < z0 || wz > z1) continue;
                    inBox++;
                    var now = Read(comp, idx);
                    if (Same(now, v)) { same++; continue; }
                    if (now.MH != v.MH || now.L != v.L || now.S != v.S) rh++;
                    if (now.MP != v.MP || now.P != v.P) rp++;
                    Write(comp, idx, v);
                }
                SaveMethod.Invoke(comp, new object[] { false });
                hm.Poke(1);
                ClutterSystem.instance?.ResetGrass(zc, 45f);
                total += rh + rp > 0 ? 1 : 0;
                outLines.Add($"  zone {kv.Key.x},{kv.Key.y}: {inBox} reference vertex(es) in the box, {same} already equal, restored {rh} height / {rp} paint");
            }
            float after = Heightmap.GetHeight(c, out float ha) ? ha : float.NaN;
            outLines.Add($"box centre ({c.x:0.#},{c.z:0.#}) heightmap ground {before:0.00} -> {after:0.00} m (the mesh rebuild finishes next frame; read it again to see the final height)");
            return outLines;
        }

        private static void Say(Terminal ctx, string line) { FiresLogger.LogInfo($"{Tag} {line}"); ctx?.AddString($"{Tag} {line}"); }

        private static string ResetCircle(float x, float z, float r)
        {
            var c = new Vector3(x, 0f, z);
            float before = ZoneSystem.instance.GetGroundHeight(c);
            s_maps.Clear();
            Heightmap.FindHeightmap(c, r, s_maps);
            var maps = new List<Heightmap>(s_maps);
            if (maps.Count == 0) return $"reset ({x:0},{z:0}) r {r:0}: not loaded (no heightmap there)";
            int verts = 0, hadH = 0, hadP = 0, comps = 0, removed = 0;
            foreach (var hm in maps)
            {
                if (hm == null) continue;
                var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
                if (comp == null) continue;
                var nview = CompNview(comp);
                if (nview == null || !nview.IsValid()) continue;
                if (!nview.IsOwner()) nview.ClaimOwnership();
                SaveMethod.Invoke(comp, new object[] { false });
                foreach (var f in FootOf(hm, comp, new[] { c }, r))
                {
                    hm.WorldToVertex(c, out int cx, out int cy);
                    int w = Mathf.RoundToInt(Mathf.Sqrt(ModifiedHeight(comp).Length)), vx = f.Idx % w, vy = f.Idx / w;
                    if ((vx - cx) * (vx - cx) + (vy - cy) * (vy - cy) > r * r) continue;   // the circle, not its square
                    if (f.B.MH) hadH++;
                    if (f.B.MP) hadP++;
                    Write(comp, f.Idx, default(Vals));
                    verts++;
                }
                SaveMethod.Invoke(comp, new object[] { false });
                comps++;
                if (!AnyModified(comp)) { DestroyLater(nview); removed++; }
                hm.Poke(1);
                ClutterSystem.instance?.ResetGrass(c, r + 2f);
            }
            float after = ZoneSystem.instance.GetGroundHeight(c);
            return $"reset ({x:0},{z:0}) r {r:0}: {verts} vertex(es) back to generated ({hadH} had height edits, {hadP} had paint) on {comps} TerrainComp(s), {removed} left empty and removed; centre ground {before:0.00} -> {after:0.00} m (the rebuild finishes next frame)";
        }

        internal static void RegisterCommand()
        {
            RegisterResetCommand();
            RegisterRestoreRefCommand();
            new Terminal.ConsoleCommand("fat_studio_terrain_test", "- host: Studio terrain edit + restore round trip for every op in front of you; PASS = byte-identical after restore",
                args =>
                {
                    var me = Player.m_localPlayer;
                    if (me == null || FiresAdminTerrain.Instance == null) { args.Context?.AddString($"{Tag} no player"); return; }
                    Vector3 at = me.transform.position + me.transform.forward * 12f;
                    FiresAdminTerrain.Instance.StartCoroutine(SelfTest(at, args.Context));
                }, isCheat: true);
        }

        private static System.Collections.IEnumerator SelfTest(Vector3 at, Terminal ctx)
        {
            int pass = 0, fail = 0;
            void Say(string line) { FiresLogger.LogInfo($"{Tag} {line}"); ctx?.AddString($"{Tag} {line}"); }
            // 0.2.1253 (round d / F left a +10..+11 m Level on RPGWorld's town, cause not proven yet): the Level target is the
            // HEIGHTMAP's own surface (Heightmap.GetHeight: terrain + its TerrainComp edits), never a physics ray; both are logged,
            // so a ray hitting something above the ground shows in the line.
            float rayGround = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(at) : at.y;
            float ground = Heightmap.GetHeight(at, out float hmGround) ? hmGround : rayGround;
            Say($"ground at ({at.x:0},{at.z:0}): heightmap {ground:0.00} m, terrain ray {rayGround:0.00} m{(Mathf.Abs(rayGround - ground) > 0.5f ? "  (they DIFFER)" : "")}; Level targets heightmap + 2 m");
            at.y = ground;
            foreach (FatTerrainOp op in Enum.GetValues(typeof(FatTerrainOp)))
            {
                var a = new FatTerrainEditArgs { Op = op, Points = new[] { at, at + new Vector3(3f, 0f, 0f) }, Radius = 4f, Strength = 1.5f, Height = ground + 2f, Paint = (int)TerrainModifier.PaintType.Paved };
                var before = Fingerprint(a.Points, a.Radius + FootMargin);
                if (!Edit(a, out var snap, out string why)) { fail++; Say($"FAIL {op}: edit refused: {why}"); continue; }
                yield return null;
                var edited = Fingerprint(a.Points, a.Radius + FootMargin);
                if (edited == before) { fail++; Say($"FAIL {op}: the edit changed nothing ({why})"); continue; }
                if (!Restore(snap, out string rwhy)) { fail++; Say($"FAIL {op}: restore refused: {rwhy}"); continue; }
                yield return new WaitForSecondsRealtime(DestroyDelay + 1f);
                var after = Fingerprint(a.Points, a.Radius + FootMargin);
                if (after == before) { pass++; Say($"PASS {op}: changed by the edit, byte-identical after restore ({rwhy}; snapshot {snap.Bytes.Length} bytes)"); }
                else { fail++; Say($"FAIL {op}: not identical after restore: before [{before}] after [{after}]"); }
            }
            Say($"fat_studio_terrain_test done: {pass} PASS, {fail} FAIL at ({at.x:0},{at.z:0})");
        }

        // zone:sha-prefix (or zone:absent) for every heightmap within r of at, sorted; equal strings = identical terrain data.
        // 0.2.1248: the footprint's raw vertex values per zone (a snow-trail refill elsewhere in the zone is not the edit's)
        private static string Fingerprint(Vector3[] points, float r)
        {
            s_maps.Clear();
            foreach (var p in points) Heightmap.FindHeightmap(p, r, s_maps);
            var parts = new List<string>();
            foreach (var hm in s_maps)
            {
                if (hm == null) continue;
                var z = ZoneSystem.GetZone(hm.transform.position);
                var comp = TerrainComp.FindTerrainCompiler(hm.transform.position);
                string key = $"{z.x},{z.y}:";
                if (parts.Exists(x => x.StartsWith(key, StringComparison.Ordinal))) continue;
                if (comp == null) { parts.Add(key + "absent"); continue; }
                var sb = new System.Text.StringBuilder();
                foreach (var f in FootOf(hm, comp, points, r)) { var v = f.B; sb.Append(f.Idx).Append(v.MH ? "h" : "-").Append(v.L.ToString("R")).Append(v.S.ToString("R")).Append(v.MP ? "p" : "-").Append(v.P.ToString("F4")).Append(';'); }
                parts.Add(key + BitConverter.ToString(Sha(System.Text.Encoding.UTF8.GetBytes(sb.ToString())), 0, 6));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts);
        }

        private static bool Ready(out string reason)
        {
            reason = null;
            if (ZNet.instance == null || ZoneSystem.instance == null) { reason = "no world"; return false; }
            if (!ZNet.instance.IsServer()) { reason = "not host: Studio terrain runs on the server/host only"; return false; }
            if (DoOperation == null || SaveMethod == null || LoadMethod == null) { reason = "unsupported: this Valheim version's TerrainComp is missing DoOperation/Save/Load"; return false; }
            return true;
        }

        private static bool Settings(FatTerrainEditArgs a, out TerrainOp.Settings s, out string reason)
        {
            s = new TerrainOp.Settings();
            reason = null;
            float strength = a.Strength;
            switch (a.Op)
            {
                case FatTerrainOp.Raise:
                case FatTerrainOp.Dig:
                    if (!(strength > 0f)) { reason = "bad args: strength must be > 0 for raise / dig"; return false; }
                    s.m_raise = true; s.m_raiseRadius = a.Radius; s.m_raisePower = 3f;
                    s.m_raiseDelta = a.Op == FatTerrainOp.Raise ? strength : -strength;
                    return true;
                case FatTerrainOp.Smooth:
                    s.m_smooth = true; s.m_smoothRadius = a.Radius; s.m_smoothPower = strength > 0f ? strength : 3f;
                    return true;
                case FatTerrainOp.Level:
                    s.m_level = true; s.m_levelRadius = a.Radius;
                    s.m_smooth = true; s.m_smoothRadius = a.Radius + 1.5f; s.m_smoothPower = 3f;
                    return true;
                case FatTerrainOp.Paint:
                    if (!Enum.IsDefined(typeof(TerrainModifier.PaintType), a.Paint)) { reason = $"bad args: paint {a.Paint}"; return false; }
                    s.m_paintCleared = true; s.m_paintRadius = a.Radius; s.m_paintType = (TerrainModifier.PaintType)a.Paint;
                    return true;
                default:
                    reason = $"bad args: op {a.Op}"; return false;
            }
        }

        private static bool Reaches(Heightmap hm, Vector3 at, float r)
        {
            Vector3 c = hm.transform.position;
            float half = ZoneSystem.c_ZoneSize * 0.5f + r;
            return Mathf.Abs(at.x - c.x) <= half && Mathf.Abs(at.z - c.z) <= half;
        }

        private static byte[] Data(TerrainComp comp)
        {
            var nview = CompNview(comp);
            return nview != null && nview.IsValid() ? nview.GetZDO().GetByteArray(ZDOVars.s_TCData) : null;
        }

        private static void DestroyLater(ZNetView nview)
        {
            // Peers first reload the emptied comp (CheckLoad), then the comp goes, leaving no TerrainComp ZDO behind.
            if (FiresAdminTerrain.Instance == null) { ZNetScene.instance.Destroy(nview.gameObject); return; }
            FiresAdminTerrain.Instance.StartCoroutine(DestroyAfter(nview));
        }

        private static System.Collections.IEnumerator DestroyAfter(ZNetView nview)
        {
            yield return new WaitForSecondsRealtime(DestroyDelay);
            if (nview == null || !nview.IsValid() || ZNetScene.instance == null) yield break;
            if (!nview.IsOwner()) nview.ClaimOwnership();
            ZNetScene.instance.Destroy(nview.gameObject);
        }

        // ── snapshot blob: int count; per zone: short x, short z, int beforeLen (-1 = absent) + bytes, byte hasAfter + 32-byte hash ──
        private static FatSnapshot Pack(List<Entry> entries)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(entries.Count);
                    foreach (var e in entries)
                    {
                        w.Write(e.Zone.x); w.Write(e.Zone.y);
                        w.Write(e.Before == null ? -1 : e.Before.Length);
                        if (e.Before != null) w.Write(e.Before);
                        w.Write((byte)(e.AfterHash != null ? 1 : 0));
                        if (e.AfterHash != null) w.Write(e.AfterHash);
                        int nf = e.Foot != null ? e.Foot.Count : 0;
                        w.Write(nf);
                        for (int k = 0; k < nf; k++) { w.Write(e.Foot[k].Idx); WriteVals(w, e.Foot[k].B); WriteVals(w, e.Foot[k].A); }
                    }
                }
                byte[] bytes = ms.ToArray();
                return new FatSnapshot { Id = Guid.NewGuid().ToString("N"), Kind = Kind, Version = Version, Bytes = bytes, Hash = Sha(bytes) };
            }
        }

        private static bool Unpack(FatSnapshot s, out List<Entry> entries, out string reason)
        {
            entries = new List<Entry>();
            reason = null;
            if (s == null || s.Bytes == null) { reason = "bad snapshot: empty"; return false; }
            if (s.Kind != Kind || (s.Version != 1 && s.Version != 2)) { reason = $"bad snapshot: kind {s.Kind} v{s.Version}, expected {Kind} v1 or v2"; return false; }
            if (s.Hash != null && !SameHash(Sha(s.Bytes), s.Hash)) { reason = "bad snapshot: hash mismatch"; return false; }
            try
            {
                using (var r = new BinaryReader(new MemoryStream(s.Bytes)))
                {
                    int n = r.ReadInt32();
                    if (n < 0 || n > MaxZones) { reason = $"bad snapshot: {n} zones"; return false; }
                    for (int i = 0; i < n; i++)
                    {
                        var e = new Entry { Zone = new Vector2s(r.ReadInt16(), r.ReadInt16()) };   // 0.2.1243: Vector2s x/y are shorts (written as 2 bytes each); 1242 read 4 -> EndOfStream
                        int len = r.ReadInt32();
                        if (len > 4 * 1024 * 1024) { reason = "bad snapshot: zone data too large"; return false; }
                        e.Before = len < 0 ? null : r.ReadBytes(len);
                        if (r.ReadByte() == 1) e.AfterHash = r.ReadBytes(32);
                        if (s.Version >= 2)
                        {
                            int nf = r.ReadInt32();
                            if (nf < 0 || nf > 65 * 65) { reason = $"bad snapshot: {nf} footprint vertices"; return false; }
                            e.Foot = new List<Foot>(nf);
                            for (int k = 0; k < nf; k++) e.Foot.Add(new Foot { Idx = r.ReadInt32(), B = ReadVals(r), A = ReadVals(r) });
                        }
                        entries.Add(e);
                    }
                }
                return true;
            }
            catch (Exception ex) { reason = $"bad snapshot: {ex.GetType().Name}"; return false; }
        }

        private static byte[] Sha(byte[] data)
        {
            if (data == null) return null;
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        private static bool SameHash(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
