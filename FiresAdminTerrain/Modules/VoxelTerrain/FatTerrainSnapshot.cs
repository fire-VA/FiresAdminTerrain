using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── FatTerrainSnapshot (0.2.860, [seasons] for VABackpacks camps; v3 0.2.863) ────────────────────────────────────────
    // A camp keeps the player's terrain work inside its footprint and lays it down again where it is deployed next, turned with
    // the camp; at pack-up it puts the ground back exactly as it was.
    // 0.2.863 (Fire 10-08 hand test: floating slabs and holes after pack-up, restored ground that normal tools could not level):
    // v1/v2 wrote VOXEL BRUSH edits, an override layer over the ground the tools edit. On a VoxelWrap world the hoe and Infinity
    // Hammer edit the TerrainComp HEIGHTMAP (TerrainOp -> TerrainComp.ApplyOperation) and FAT rebuilds the voxels from it, so the
    // snapshot now reads and writes THAT layer: each TerrainComp vertex's level delta, smooth delta and paint. The ground it lays
    // down is ordinary ground (editable, synced through the TerrainComp ZDO), redrawn once per zone by the terraform path.
    // Public, stable, called by reflection (a soft dependency); the signatures are unchanged:
    //   byte[] Capture(Vector3 origin, float yaw, float radius)
    //   string Apply(byte[] snapshot, Vector3 origin, float yaw)
    //   string Level(Vector3 centre, float radius, float y, int paint)    paint: 0 none (reset), 1 dirt, 2 cultivated, 3 paved
    // Yaw turns in 90-degree steps (Fire 10-08: camps snap to 90 degrees), which maps vertex onto vertex exactly. Apply at the
    // capture's own spot and facing writes the captured vertices back VERBATIM (an exact restore); elsewhere it lays the captured
    // ground heights (relative to the origin) and paint on the new ground. Each Apply also clears any voxel-brush edits in the
    // footprint once (the residue the v1/v2 snapshots left). Blob v3 = byte 3 + gzip(header + grid); v1/v2 blobs are refused.
    public static class FatTerrainSnapshot
    {
        private const byte Version = 3;
        private const float MaxRadius = 48f, ResidueHeight = 160f, SamePlace = 0.6f;
        private const string Tag = "[FatTerrainSnapshot]";
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> LevelDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> SmoothDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedHeight = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> PaintMask = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedPaint = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly AccessTools.FieldRef<TerrainComp, int> Width = AccessTools.FieldRefAccess<TerrainComp, int>("m_width");
        private static readonly AccessTools.FieldRef<TerrainComp, bool> Initialized = AccessTools.FieldRefAccess<TerrainComp, bool>("m_initialized");
        private static readonly System.Reflection.MethodInfo SaveMethod = AccessTools.Method(typeof(TerrainComp), "Save");

        // One vertex as captured: its final height relative to the origin, and the TerrainComp's raw values there.
        private struct Vert { public float Rel, Level, Smooth; public bool ModH, ModP; public Color Paint; }

        public static byte[] Capture(Vector3 origin, float yaw, float radius)
        {
            radius = Mathf.Clamp(radius, 1f, MaxRadius);
            int n = Mathf.CeilToInt(radius), ox = Mathf.RoundToInt(origin.x), oz = Mathf.RoundToInt(origin.z);
            int side = 2 * n + 1, read = 0;
            using (var raw = new MemoryStream())
            {
                using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
                {
                    w.Write(radius); w.Write(n); w.Write((float)ox); w.Write(origin.y); w.Write((float)oz); w.Write(Quarter(yaw));
                    for (int j = -n; j <= n; j++)
                        for (int i = -n; i <= n; i++)
                        {
                            bool inside = i * i + j * j <= radius * radius;
                            if (!inside || !TryRead(new Vector3(ox + i, origin.y, oz + j), out var v)) { w.Write((byte)0); continue; }
                            read++;
                            w.Write((byte)(1 | (v.ModH ? 2 : 0) | (v.ModP ? 4 : 0)));
                            w.Write(v.Rel); w.Write(v.Level); w.Write(v.Smooth);
                            w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(v.Paint.r) * 255f)); w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(v.Paint.g) * 255f));
                            w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(v.Paint.b) * 255f)); w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(v.Paint.a) * 255f));
                        }
                }
                if (read == 0) return null;
                using (var outMs = new MemoryStream())
                {
                    outMs.WriteByte(Version);
                    using (var gz = new GZipStream(outMs, CompressionMode.Compress, true)) raw.WriteTo(gz);
                    FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture, "{0} captured {1} vertex(es) round ({2},{3:0.0},{4}) r {5:0}: {6} bytes", Tag, read, ox, origin.y, oz, radius, outMs.Length));
                    return outMs.ToArray();
                }
            }
        }

        public static string Apply(byte[] snapshot, Vector3 origin, float yaw)
        {
            if (snapshot == null || snapshot.Length < 2) return $"{Tag} refused: empty snapshot";
            if (snapshot[0] != Version)
                return $"{Tag} refused: snapshot v{snapshot[0]} (this FAT reads v{Version}; v1/v2 wrote the voxel layer and are not replayed)";
            var touched = new Dictionary<TerrainComp, Heightmap>();
            int written = 0, exact = 0, unread = 0;
            float radius;
            try
            {
                using (var gz = new GZipStream(new MemoryStream(snapshot, 1, snapshot.Length - 1), CompressionMode.Decompress))
                using (var r = new BinaryReader(gz))
                {
                    radius = r.ReadSingle(); int n = r.ReadInt32();
                    float cx = r.ReadSingle(), cy = r.ReadSingle(), cz = r.ReadSingle(); int cq = r.ReadInt32();
                    int ox = Mathf.RoundToInt(origin.x), oz = Mathf.RoundToInt(origin.z);
                    int q = ((Quarter(yaw) - cq) % 4 + 4) % 4;
                    bool same = q == 0 && Mathf.Abs(ox - cx) < SamePlace && Mathf.Abs(oz - cz) < SamePlace && Mathf.Abs(origin.y - cy) < SamePlace;
                    for (int j = -n; j <= n; j++)
                        for (int i = -n; i <= n; i++)
                        {
                            byte flags = r.ReadByte();
                            if ((flags & 1) == 0) continue;
                            var v = new Vert { ModH = (flags & 2) != 0, ModP = (flags & 4) != 0, Rel = r.ReadSingle(), Level = r.ReadSingle(), Smooth = r.ReadSingle() };
                            v.Paint = new Color(r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f);
                            int ti = i, tj = j;
                            for (int k = 0; k < q; k++) { int t = ti; ti = tj; tj = -t; }   // a quarter turn (Unity's +90 about y)
                            var p = new Vector3(ox + ti, origin.y, oz + tj);
                            int w = same ? WriteRaw(p, v, touched) : WriteRelative(p, origin.y + v.Rel, v.ModP, v.Paint, touched);
                            if (w == 0) unread++; else { written++; if (same) exact++; }
                        }
                }
            }
            catch (Exception ex) { return $"{Tag} refused: unreadable snapshot ({ex.GetType().Name}: {ex.Message})"; }
            int comps = Commit(touched, origin, radius);
            return Report("applied", origin, yaw, written, exact, unread, comps);
        }

        // Level a disc to `y` on the TerrainComp layer and paint it (0 reset, 1 dirt, 2 cultivated, 3 paved).
        public static string Level(Vector3 centre, float radius, float y, int paint)
        {
            radius = Mathf.Clamp(radius, 0.5f, MaxRadius);
            var touched = new Dictionary<TerrainComp, Heightmap>();
            int n = Mathf.CeilToInt(radius), ox = Mathf.RoundToInt(centre.x), oz = Mathf.RoundToInt(centre.z), written = 0, unread = 0;
            bool modP = paint >= 1 && paint <= 3;
            for (int j = -n; j <= n; j++)
                for (int i = -n; i <= n; i++)
                {
                    if (i * i + j * j > radius * radius) continue;
                    var p = new Vector3(ox + i, y, oz + j);
                    var c = PaintColour(p, paint);
                    if (WriteRelative(p, y, modP, c, touched) == 0) unread++; else written++;
                }
            int comps = Commit(touched, centre, radius);
            return Report("levelled", centre, 0f, written, 0, unread, comps);
        }

        // ── the TerrainComp layer ─────────────────────────────────────────────────────────────────────────────────────
        private static int Quarter(float yaw) => ((Mathf.RoundToInt(yaw / 90f) % 4) + 4) % 4;

        private static bool Index(Heightmap hm, TerrainComp tc, Vector3 p, out int idx)
        {
            idx = -1;
            hm.WorldToVertex(p, out int x, out int y);
            int w = Width(tc) + 1;
            if (x < 0 || y < 0 || x >= w || y >= w) return false;
            idx = y * w + x;
            return LevelDelta(tc) != null && idx < LevelDelta(tc).Length;
        }

        private static bool TryRead(Vector3 p, out Vert v)
        {
            v = default;
            var hm = Heightmap.FindHeightmap(p);
            if (hm == null) return false;
            var tc = hm.GetAndCreateTerrainCompiler();
            if (tc == null || !Initialized(tc) || !Index(hm, tc, p, out int idx)) return false;
            hm.WorldToVertex(p, out int x, out int y);
            v.ModH = ModifiedHeight(tc)[idx]; v.Level = LevelDelta(tc)[idx]; v.Smooth = SmoothDelta(tc)[idx];
            v.ModP = ModifiedPaint(tc)[idx]; v.Paint = PaintMask(tc)[idx];
            v.Rel = hm.GetHeight(x, y) - p.y;
            return true;
        }

        // Every heightmap whose grid holds this vertex (zone borders share theirs), and its TerrainComp, owned by us.
        private static List<(Heightmap hm, TerrainComp tc, int idx)> Targets(Vector3 p, Dictionary<TerrainComp, Heightmap> touched)
        {
            var list = new List<(Heightmap, TerrainComp, int)>();
            var hms = new List<Heightmap>();
            Heightmap.FindHeightmap(p, 0.1f, hms);
            foreach (var hm in hms)
            {
                if (hm == null) continue;
                var tc = hm.GetAndCreateTerrainCompiler();
                if (tc == null || !Initialized(tc) || !Index(hm, tc, p, out int idx)) continue;
                if (!touched.ContainsKey(tc))
                {
                    var nv = tc.GetComponent<ZNetView>();
                    if (nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();   // TerrainComp.Save writes only on the owner
                    touched[tc] = hm;
                }
                list.Add((hm, tc, idx));
            }
            return list;
        }

        private static int WriteRaw(Vector3 p, Vert v, Dictionary<TerrainComp, Heightmap> touched)
        {
            int k = 0;
            foreach (var (hm, tc, idx) in Targets(p, touched))
            {
                ModifiedHeight(tc)[idx] = v.ModH; LevelDelta(tc)[idx] = v.Level; SmoothDelta(tc)[idx] = v.Smooth;
                ModifiedPaint(tc)[idx] = v.ModP; PaintMask(tc)[idx] = v.Paint;
                k++;
            }
            return k;
        }

        // The vertex's final height becomes `target` (its level delta over the ground it stands on; smoothing cleared).
        private static int WriteRelative(Vector3 p, float target, bool modP, Color paint, Dictionary<TerrainComp, Heightmap> touched)
        {
            int k = 0;
            foreach (var (hm, tc, idx) in Targets(p, touched))
            {
                hm.WorldToVertex(p, out int x, out int y);
                float current = hm.GetHeight(x, y);
                float delta = ModifiedHeight(tc)[idx] ? LevelDelta(tc)[idx] + SmoothDelta(tc)[idx] : 0f;
                float ground = current - delta;
                LevelDelta(tc)[idx] = target - ground; SmoothDelta(tc)[idx] = 0f; ModifiedHeight(tc)[idx] = true;
                ModifiedPaint(tc)[idx] = modP; if (modP) PaintMask(tc)[idx] = paint;
                k++;
            }
            return k;
        }

        private static Color PaintColour(Vector3 p, int paint)
        {
            float a = 1f;
            var hm = Heightmap.FindHeightmap(p);
            var tc = hm != null ? hm.GetAndCreateTerrainCompiler() : null;
            if (tc != null && Initialized(tc) && Index(hm, tc, p, out int idx)) a = PaintMask(tc)[idx].a;
            return paint == 1 ? new Color(1f, 0f, 0f, a) : paint == 2 ? new Color(0f, 1f, 0f, a) : paint == 3 ? new Color(0f, 0f, 1f, a) : new Color(0f, 0f, 0f, a);
        }

        // Save each touched TerrainComp (its ZDO carries the ground to every peer) and redraw its heightmap once.
        private static int Commit(Dictionary<TerrainComp, Heightmap> touched, Vector3 at, float radius)
        {
            foreach (var kv in touched)
            {
                try { SaveMethod?.Invoke(kv.Key, new object[] { false }); } catch (Exception ex) { FiresLogger.LogWarning($"{Tag} TerrainComp save failed: {ex.Message}"); }
                kv.Value.Poke(1);   // vanilla DoOperation pokes the same way (one delayed rebuild)
            }
            if (ClutterSystem.instance != null) ClutterSystem.instance.ResetGrass(at, radius + 2f);
            return touched.Count;
        }

        // The v1/v2 snapshots wrote voxel-brush edits; one Restore cylinder over the footprint removes them, so the ground there
        // is the TerrainComp's again.
        // 0.2.864 (Fire 10-09: fell through the world packing a camp on 0.2.863): this ran on EVERY Apply, a 160 m-tall Restore over
        // the whole footprint, so every chunk there was rebuilt twice (the Restore, then the TerrainComp's terraform rebuild) and
        // a chunk the Restore returned to pristine could drop and come back between the two. v3 never writes brush edits, so the
        // clear now runs only where a brush edit actually exists in the footprint (an old v1/v2 site): no longer from Apply, only from the admin command fat_camp_residue_clear (ran on every
        // Apply before, and the wrapped world has anchors in nearly every column, so a "has edits" test could not narrow it).
        private static void ClearResidue(Vector3 at, float radius)
        {
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture, "{0} clearing old voxel-brush edits in the footprint at ({1:0},{2:0}) r {3:0}", Tag, at.x, at.z, radius + 1f));
            try { VoxelWorld.ApplyBrush(new Vector3(at.x, at.y, at.z), radius + 1f, ResidueHeight, VoxelBrushShape.Cylinder, VoxelBrushMode.Restore); }
            catch (Exception ex) { FiresLogger.LogWarning($"{Tag} voxel residue clear failed: {ex.Message}"); }
        }

        private static string Report(string verb, Vector3 at, float yaw, int written, int exact, int unread, int comps)
        {
            string line = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} vertex(es) at ({3:0.0},{4:0.0},{5:0.0}) yaw {6:0}: {7} exact, {8} TerrainComp(s) saved{9}",
                Tag, verb, written, at.x, at.y, at.z, yaw, exact, comps, unread > 0 ? $", {unread} with no loaded heightmap" : "");
            FiresLogger.LogDebug(line);
            return line;
        }

        // `fat_camp_residue_clear <x> <z> <radius>` (admin): the one-time clear of the voxel-brush edits a 0.2.860-0.2.862 camp
        // snapshot left at a site (floating slabs, holes, ground the tools cannot level). The ground there falls back to the
        // TerrainComp layer and rebuilds once.
        internal static void RegisterResidueCommand()
        {
            new Terminal.ConsoleCommand("fat_camp_residue_clear", "<x> <z> <radius> - clear old camp-snapshot voxel-brush leftovers there (ground falls back to the heightmap) (admin)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    if (args.Length < 4 || !float.TryParse(args[1], NumberStyles.Float, inv, out float x) || !float.TryParse(args[2], NumberStyles.Float, inv, out float z)
                        || !float.TryParse(args[3], NumberStyles.Float, inv, out float r)) { args.Context?.AddString($"{Tag} usage: fat_camp_residue_clear <x> <z> <radius>"); return; }
                    float y = VoxelWorld.TryFindSurfaceY(x, z, ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z)) : 30f, 64f, out float s) ? s : 30f;
                    ClearResidue(new Vector3(x, y, z), Mathf.Clamp(r, 1f, MaxRadius));
                    string line = string.Format(inv, "{0} residue cleared at ({1:0},{2:0}) r {3:0}", Tag, x, z, r);
                    FiresLogger.LogInfo(line); args.Context?.AddString(line);
                }, isCheat: true);
        }

        // `fat_snapshot_test [radius=6]` (read-only): captures round the player and prints the blob's size; no edit.
        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand("fat_snapshot_test", "[radius=6] - read-only: capture the terrain round you as a camp snapshot (TerrainComp layer) and print its size (no edit)",
                args =>
                {
                    var me = Player.m_localPlayer;
                    if (me == null) { args.Context?.AddString($"{Tag} no player"); return; }
                    float rr = args.Length > 1 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 6f;
                    var p = me.transform.position;
                    var blob = Capture(p, me.transform.eulerAngles.y, rr);
                    string line = blob == null ? $"{Tag} capture at ({p.x:0},{p.z:0}) r {rr:0}: nothing read"
                        : string.Format(CultureInfo.InvariantCulture, "{0} capture at ({1:0},{2:0}) r {3:0}: {4} bytes, v{5}", Tag, p.x, p.z, rr, blob.Length, blob[0]);
                    FiresLogger.LogInfo(line); args.Context?.AddString(line);
                });
        }
    }
}
