using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── FatTerrainSnapshot (for VABackpacks camps) ────────────────────────────────────────
    // A camp keeps the player's terrain work inside its footprint and lays it down again where it is deployed next, turned with
    // the camp; at pack-up it puts the ground back exactly as it was.
    // Floating slabs and holes after pack-up, and restored ground that normal tools could not level:
    // v1/v2 wrote VOXEL BRUSH edits, an override layer over the ground the tools edit. On a VoxelWrap world the hoe and Infinity
    // Hammer edit the TerrainComp HEIGHTMAP (TerrainOp -> TerrainComp.ApplyOperation) and FAT rebuilds the voxels from it, so the
    // snapshot now reads and writes THAT layer: each TerrainComp vertex's level delta, smooth delta and paint. The ground it lays
    // down is ordinary ground (editable, synced through the TerrainComp ZDO), redrawn once per zone by the terraform path.
    // Public, stable, called by reflection (a soft dependency); the signatures are unchanged:
    //   byte[] Capture(Vector3 origin, float yaw, float radius)
    //   string Apply(byte[] snapshot, Vector3 origin, float yaw)
    //   string Level(Vector3 centre, float radius, float y, int paint)    paint: 0 none (reset), 1 dirt, 2 cultivated, 3 paved
    // Yaw turns in 90-degree steps (camps snap to 90 degrees), which maps vertex onto vertex exactly. Apply at the
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
        // Camp levelling must come out clean: the flat disc is measured from the TRUE
        // centre (was snapped to the 1 m grid, so the rim staircased and sat up to 0.5 m off), and a feathered skirt
        // blends it back into the ground (was a hard step/wall at the rim on any slope). The skirt is at least
        // SkirtMin metres and widens with the height change (SkirtPerMetre x |Δ|), smoothstep-eased; paint fades across
        // it. Config "Camp level skirt" OFF = the old hard-edged disc. Callers that restore later must capture
        // LevelReach(...) metres, not just the disc: the skirt is edited ground too.
        private const float SkirtMin = 1.5f, SkirtPerMetre = 1.5f, SkirtMax = 8f;

        public static float SkirtWidth(Vector3 centre, float radius, float y)
        {
            return 0f;   // The old skirt (the 11 m spire) is deleted; the chunk-border heal covers camp pad edges
            float worst = 0f;
            float r = Mathf.Clamp(radius, 0.5f, MaxRadius);
            for (int a = 0; a < 16; a++)   // the rim's largest height change sets the skirt
            {
                float ang = a * Mathf.PI / 8f;
                var p = new Vector3(centre.x + Mathf.Cos(ang) * r, y, centre.z + Mathf.Sin(ang) * r);
                if (Heightmap.GetHeight(p, out float g)) worst = Mathf.Max(worst, Mathf.Abs(g - y));
            }
            return Mathf.Clamp(Mathf.Max(SkirtMin, worst * SkirtPerMetre), 0f, SkirtMax);
        }

        // The full radius Level edits (disc + skirt): capture at least this much before levelling, so a pack-up restores it all.
        public static float LevelReach(Vector3 centre, float radius, float y)
            => Mathf.Clamp(radius, 0.5f, MaxRadius) + SkirtWidth(centre, radius, y);

        public static string Level(Vector3 centre, float radius, float y, int paint)
        {
            radius = Mathf.Clamp(radius, 0.5f, MaxRadius);
            // The voxel surface at the centre BEFORE this level (the camp mound hunt; the heal passes log it after)
            string voxBefore = VoxelWorld.TryFindSurfaceY(centre.x, centre.z, y, 24f, out float vb) ? vb.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "none";
            float skirt = SkirtWidth(centre, radius, y), reach = radius + skirt;
            var touched = new Dictionary<TerrainComp, Heightmap>();
            int n = Mathf.CeilToInt(reach) + 1, ox = Mathf.FloorToInt(centre.x), oz = Mathf.FloorToInt(centre.z), written = 0, unread = 0, skirted = 0;
            bool modP = paint >= 1 && paint <= 3;
            for (int j = -n; j <= n; j++)
                for (int i = -n; i <= n; i++)
                {
                    var p = new Vector3(ox + i, y, oz + j);
                    float d = Mathf.Sqrt((p.x - centre.x) * (p.x - centre.x) + (p.z - centre.z) * (p.z - centre.z));
                    if (d > reach) continue;
                    if (d <= radius)
                    {
                        if (WriteRelative(p, y, modP, PaintColour(p, paint), touched) == 0) unread++; else written++;
                        continue;
                    }
                    // skirt: ease from the pad height back to this vertex's CURRENT height, not the unmodified ground: a camp
                    // levels its main pad and then a small pad per piece, and a skirt that reverted to the original slope would
                    // drag the main pad's flat ground back up/down under every piece.
                    if (!TryRead(p, out var v)) { unread++; continue; }
                    float ground = p.y + v.Rel;   // the current ground at this vertex (earlier pads included)
                    float t = Mathf.SmoothStep(0f, 1f, (d - radius) / Mathf.Max(0.01f, skirt));
                    float target = Mathf.Lerp(y, ground, t);
                    var c = PaintColour(p, paint);
                    if (modP) { c.r *= 1f - t; c.g *= 1f - t; c.b *= 1f - t; }   // fade the dirt/cultivated/paved channels; alpha is not paint strength
                    if (WriteRelative(p, target, modP && t < 0.95f, c, touched) == 0) unread++; else { written++; skirted++; }
                }
            int comps = Commit(touched, centre, reach);
            // Report logs before this suffix, so the applied pad/skirt values and
            // the config key they came from get their own Debug line at the point of use.
            string applied = string.Format(CultureInfo.InvariantCulture,
                "; pad y {0:0.00} radius {1:0.0} m, skirt {2:0.0} m ({3} vertex(es)), reach {4:0.0} m ([Voxel Terrain] Camp level skirt = {5})",
                y, radius, skirt, skirted, reach, "deleted in 0.2.941");
            FiresLogger.LogDebug(Tag + " level applied" + applied);
            // Camp pads showed the same chunk-border gaps: the camp levelling rebuilds the voxel
            // zones under it too, so the same border heal runs after it (always on).
            string voxAfter = VoxelWorld.TryFindSurfaceY(centre.x, centre.z, y, 24f, out float va) ? va.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "none";
            string hmAfter = TryRead(new Vector3(Mathf.Round(centre.x), y, Mathf.Round(centre.z)), out var hv) ? (y + hv.Rel).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "unread";
            FiresLogger.LogInfo($"{Tag} camp level write at ({centre.x:0.0},{centre.z:0.0}) r {radius:0.0} to y {y:0.00}: heightmap (+TerrainComp) at the centre now {hmAfter}; voxel surface before {voxBefore}, after {voxAfter} (the voxel rebuild from the heightmap follows when the heightmap regenerates)");
            VoxelBrushDrag.ScheduleBorderHeal(centre.x, centre.z, reach + 2f, reach + 2f, Mathf.Min(centre.y, y) - 8f, Mathf.Max(centre.y, y) + 8f, "camp level");
            return Report("levelled", centre, 0f, written, 0, unread, comps) + applied;
        }

        // ── pre-camp capture / restore: packing up a camp restores the terrain to what it was
        // before the camp was placed, whether it was vanilla or edited already ──
        // ADD-ONLY, called by reflection from VABackpacks:
        //   byte[] CapturePreCamp(Vector3 centre, float radius)   BEFORE the camp's Level; null = nothing loaded there (no blob)
        //   string RestorePreCamp(byte[] blob)                    at pack-up; "ok ..." or "fail: <why>" (caller falls back)
        // The blob is self-contained (absolute world vertices, no yaw): every heightmap vertex within radius + 2 m keeps its raw
        // TerrainComp values (height flag, level/smooth deltas, paint flag + RGBA) and the heightmap + voxel surface Y there.
        // The voxel ground needs no copy of its own: a camp deploy writes only the TerrainComp layer (Level/Apply) and the voxel
        // zones are re-derived from it on the heightmap regenerate (RewrapZone, brush bounds re-applied), so writing the raw
        // values back IS the voxel restore; the stored surface Ys let fat_precamp_check prove it.
        // Size: deflated; over 16 KB the data goes to the world's own store
        // (<worlds_local>/<world>.fat_camps/<id>.bin, server/host only) and the blob is a small ref.
        private const byte PreCampInline = 0x50, PreCampRef = 0x52;   // 'P', 'R'
        private const int PreCampRefOver = 16 * 1024;
        private const float PreCampMargin = 2f;
        private static byte[] s_lastRestored;   // for fat_precamp_check

        public static byte[] CapturePreCamp(Vector3 centre, float radius)
        {
            try
            {
                float r = Mathf.Clamp(radius, 1f, MaxRadius) + PreCampMargin;
                int n = Mathf.CeilToInt(r), ox = Mathf.RoundToInt(centre.x), oz = Mathf.RoundToInt(centre.z), read = 0, cols = 0;
                byte[] raw;
                using (var ms = new MemoryStream())
                {
                    using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                    {
                        w.Write(ox); w.Write(oz); w.Write(n); w.Write(r); w.Write(centre.y);
                        for (int j = -n; j <= n; j++)
                            for (int i = -n; i <= n; i++)
                            {
                                if (i * i + j * j > r * r) continue;
                                cols++;
                                var p = new Vector3(ox + i, centre.y, oz + j);
                                if (!TryRead(p, out var v)) { w.Write((byte)0); continue; }
                                read++;
                                float sy = VoxelWorld.TryFindSurfaceY(p.x, p.z, p.y + v.Rel, 24f, out float s) ? s : float.NaN;
                                w.Write((byte)(1 | (v.ModH ? 2 : 0) | (v.ModP ? 4 : 0)));
                                w.Write(v.Level); w.Write(v.Smooth);
                                w.Write(v.Paint.r); w.Write(v.Paint.g); w.Write(v.Paint.b); w.Write(v.Paint.a);   // floats: the restore is exact
                                w.Write(p.y + v.Rel); w.Write(sy);
                            }
                    }
                    raw = ms.ToArray();
                }
                if (read == 0) { FiresLogger.LogWarning($"{Tag} pre-camp capture at ({ox},{oz}) r {r:0.0}: no loaded heightmap, nothing captured (null)"); return null; }
                byte[] packed;
                using (var outMs = new MemoryStream())
                {
                    using (var df = new DeflateStream(outMs, CompressionMode.Compress, true)) df.Write(raw, 0, raw.Length);
                    packed = outMs.ToArray();
                }
                byte[] inline = new byte[packed.Length + 1]; inline[0] = PreCampInline; Buffer.BlockCopy(packed, 0, inline, 1, packed.Length);
                string mode = "inline";
                byte[] result = inline;
                if (inline.Length > PreCampRefOver)
                {
                    if (TryStorePreCamp(inline, out string id, out string why)) { result = MakeRef(id); mode = "ref " + id; }
                    else { mode = "inline (over 16 KB, not stored: " + why + ")"; FiresLogger.LogWarning($"{Tag} pre-camp capture over 16 KB stays inline: {why}"); }
                }
                FiresLogger.LogInfo(string.Format(CultureInfo.InvariantCulture, "{0} pre-camp capture at ({1},{2}) r {3:0.0}: {4} cols ({5} read), raw {6:0.0} KB, deflated {7:0.0} KB, {8}, blob {9} bytes",
                    Tag, ox, oz, r, cols, read, raw.Length / 1024f, inline.Length / 1024f, mode, result.Length));
                return result;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{Tag} pre-camp capture failed: {ex.GetType().Name}: {ex.Message}"); return null; }
        }

        public static string RestorePreCamp(byte[] blob)
        {
            try
            {
                if (blob == null || blob.Length < 2) return PcSay("fail: empty blob");
                if (blob[0] == PreCampRef)
                {
                    string id = System.Text.Encoding.UTF8.GetString(blob, 1, blob.Length - 1);
                    if (!TryLoadPreCamp(id, out blob, out string why)) return PcSay("fail: ref " + id + ": " + why);
                }
                if (blob[0] != PreCampInline) return PcSay($"fail: not a pre-camp blob (byte 0x{blob[0]:X2})");
                byte[] raw = Inflate(blob);
                if (raw == null) return PcSay("fail: the snapshot was refused (size cap or corrupt)");   // 0.2.1333
                var touched = new Dictionary<TerrainComp, Heightmap>();
                int written = 0, unread = 0, ox, oz; float r, cy;
                using (var rd = new BinaryReader(new MemoryStream(raw)))
                {
                    ox = rd.ReadInt32(); oz = rd.ReadInt32(); int n = rd.ReadInt32(); r = rd.ReadSingle(); cy = rd.ReadSingle();
                    // 0.2.1333: this loop reads nothing outside the circle, so a huge n would spin; CapturePreCamp writes n = ceil(r), r <= MaxRadius + margin
                    if (n < 0 || n > Mathf.CeilToInt(MaxRadius + PreCampMargin) || !(r >= 0f) || r > MaxRadius + PreCampMargin) return PcSay($"fail: snapshot says n {n}, r {r}");
                    for (int j = -n; j <= n; j++)
                        for (int i = -n; i <= n; i++)
                        {
                            if (i * i + j * j > r * r) continue;
                            byte f = rd.ReadByte();
                            if ((f & 1) == 0) continue;
                            var v = new Vert { ModH = (f & 2) != 0, ModP = (f & 4) != 0, Level = rd.ReadSingle(), Smooth = rd.ReadSingle() };
                            v.Paint = new Color(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
                            rd.ReadSingle(); rd.ReadSingle();   // heightmap + voxel surface Y: fat_precamp_check reads them
                            if (WriteRaw(new Vector3(ox + i, cy, oz + j), v, touched) == 0) unread++; else written++;
                        }
                }
                int comps = Commit(touched, new Vector3(ox, cy, oz), r);
                VoxelBrushDrag.ScheduleBorderHeal(ox, oz, r + 2f, r + 2f, cy - 24f, cy + 24f, "camp pre-camp restore");
                s_lastRestored = blob;
                return PcSay(string.Format(CultureInfo.InvariantCulture, "ok: pre-camp restore at ({0},{1}) r {2:0.0}: {3} vertex(es) written exact, {4} TerrainComp(s) saved{5}; border heal scheduled; prove it with fat_precamp_check after the rebuild",
                    ox, oz, r, written, comps, unread > 0 ? $", {unread} with no loaded heightmap (NOT restored)" : ""));
            }
            catch (Exception ex) { return PcSay($"fail: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static byte[] Inflate(byte[] blob)
        {
            return FatInflate.Deflate(blob, 1, blob.Length - 1, "pre-camp snapshot");   // 0.2.1333: null = refused
        }

        // ── 0.2.1331: one stamp on the TerrainComp layer (the water tools on heightmap ground) ─────────────────────────────
        // Vanilla's LevelTerrain takes its delta from m_heights, stale until the delayed Poke lands, so several ops inside that window
        // stack, oscillate, and under [HeightmapOverride] +/-1000 turn into spikes. WriteRelative shares that stale read. Here each vertex's
        // ground (height minus deltas) is read once per stroke, while its heightmap is current, and height = ground + delta from then on.
        internal struct StampCount { public int Lowered, Filled, WouldRaise; public float MaxLowered; }
        private static readonly Dictionary<TerrainComp, float[]> s_stampGround = new Dictionary<TerrainComp, float[]>();
        private static readonly List<Heightmap> s_stampMaps = new List<Heightmap>();
        private static float s_lastStamp;
        private const float StampIdle = 1f;

        internal static void EndStampStroke() => s_stampGround.Clear();

        // The limits ApplyToHeightmap draws with. A stored delta past them is drawn clamped, and ground + delta would stop being the height.
        private static System.Reflection.MethodInfo s_coreMin, s_coreMax;
        private static bool s_coreLimitsBound;
        private static void DeltaLimits(out float min, out float max)
        {
            if (!s_coreLimitsBound)
            {
                s_coreLimitsBound = true;
                var core = Type.GetType("FiresCore.Terrain.HeightmapOverrideLimits, FiresUnifiedCore");   // 0.2.1335: the override is Core's again (1086+); an older Core has none, so vanilla +/-8 m is what is drawn
                if (core != null) { s_coreMin = AccessTools.Method(core, "Min"); s_coreMax = AccessTools.Method(core, "Max"); }
            }
            min = s_coreMin != null ? (float)s_coreMin.Invoke(null, null) : -8f;
            max = s_coreMax != null ? (float)s_coreMax.Invoke(null, null) : 8f;
        }

        // Every vertex within `reach` of centre (xz) gets height target(distance, height). raise false: a target above the height is counted,
        // never applied. Saved, synced and redrawn like every other write here (Commit). Returns the TerrainComps touched.
        internal static int Stamp(Vector3 centre, float reach, bool raise, Func<float, float, float> target, ref StampCount count)
        {
            if (Time.unscaledTime - s_lastStamp > StampIdle) s_stampGround.Clear();
            s_lastStamp = Time.unscaledTime;
            DeltaLimits(out float minDelta, out float maxDelta);
            var touched = new Dictionary<TerrainComp, Heightmap>();
            s_stampMaps.Clear();
            Heightmap.FindHeightmap(centre, reach + 1f, s_stampMaps);
            foreach (var hm in s_stampMaps)
            {
                if (hm == null) continue;
                var tc = hm.GetAndCreateTerrainCompiler();
                if (tc == null || !Initialized(tc)) continue;
                var nv = tc.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) continue;
                if (!nv.IsOwner()) nv.ClaimOwnership();   // TerrainComp.Save writes only on the owner
                if (!nv.IsOwner()) continue;
                int w = Width(tc) + 1;
                if (!s_stampGround.TryGetValue(tc, out float[] grounds))
                {
                    if (hm.m_doLateUpdate != 0) hm.Regenerate();   // someone else's poke is pending: read this stroke's grounds from current heights
                    grounds = new float[w * w];
                    for (int i = 0; i < grounds.Length; i++) grounds[i] = float.NaN;
                    s_stampGround[tc] = grounds;
                }
                float[] level = LevelDelta(tc), smooth = SmoothDelta(tc);
                bool[] mod = ModifiedHeight(tc);
                hm.WorldToVertex(centre, out int cx, out int cy);
                int n = Mathf.CeilToInt(reach / hm.m_scale) + 1, half = hm.m_width / 2;
                Vector3 hp = hm.transform.position;
                bool any = false;
                for (int y = Mathf.Max(0, cy - n); y <= Mathf.Min(w - 1, cy + n); y++)
                    for (int x = Mathf.Max(0, cx - n); x <= Mathf.Min(w - 1, cx + n); x++)
                    {
                        float dx = hp.x + (x - half) * hm.m_scale - centre.x, dz = hp.z + (y - half) * hm.m_scale - centre.z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d > reach) continue;
                        int idx = y * w + x;
                        if (idx >= level.Length) continue;
                        float delta = mod[idx] ? level[idx] + smooth[idx] : 0f;
                        if (float.IsNaN(grounds[idx])) grounds[idx] = hp.y + hm.GetHeight(x, y) - delta;
                        float height = grounds[idx] + delta;
                        float want = target(d, height);
                        if (float.IsNaN(want)) continue;
                        if (want > height + 0.001f && !raise) { count.WouldRaise++; continue; }
                        if (Mathf.Abs(want - height) <= 0.001f || (raise && want < height)) continue;
                        float newDelta = Mathf.Clamp(want - grounds[idx], minDelta, maxDelta);
                        level[idx] = newDelta; smooth[idx] = 0f; mod[idx] = true;
                        if (want < height) { count.Lowered++; if (height - want > count.MaxLowered) count.MaxLowered = height - want; }
                        else count.Filled++;
                        any = true;
                    }
                if (any) touched[tc] = hm;
            }
            return Commit(touched, centre, reach);
        }

        private static string PcSay(string s)
        {
            if (s.StartsWith("fail", StringComparison.Ordinal)) FiresLogger.LogWarning($"{Tag} pre-camp restore {s}"); else FiresLogger.LogInfo($"{Tag} {s}");
            return s;
        }

        private static byte[] MakeRef(string id) { var b = System.Text.Encoding.UTF8.GetBytes(id); var o = new byte[b.Length + 1]; o[0] = PreCampRef; Buffer.BlockCopy(b, 0, o, 1, b.Length); return o; }

        private static string PreCampDir(out string why)
        {
            why = null;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { why = "not the server/host (the world store lives with the world)"; return null; }
            string world = ZNet.instance.GetWorldName();
            if (string.IsNullOrEmpty(world)) { why = "no world name"; return null; }
            return Path.GetFullPath(Path.Combine(SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local), world + ".fat_camps"));
        }

        private static bool TryStorePreCamp(byte[] data, out string id, out string why)
        {
            id = null;
            string dir = PreCampDir(out why);
            if (dir == null) return false;
            Directory.CreateDirectory(dir);
            id = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string file = Path.Combine(dir, id + ".bin"), part = file + ".partial";
            File.WriteAllBytes(part, data);
            File.Move(part, file);
            return true;
        }

        private static bool TryLoadPreCamp(string id, out byte[] data, out string why)
        {
            data = null;
            foreach (char c in id) if (!char.IsLetterOrDigit(c) && c != '_') { why = "bad id"; return false; }
            string dir = PreCampDir(out why);
            if (dir == null) return false;
            string file = Path.Combine(dir, id + ".bin");
            if (!File.Exists(file)) { why = "missing " + file; return false; }
            data = File.ReadAllBytes(file);   // kept after the restore (never deleted): a re-run restores the same ground
            return true;
        }

        // `fat_precamp_check [tol=0.02]`: after the last RestorePreCamp's rebuild, compare each restored vertex's heightmap height,
        // voxel surface and paint with the capture. 0 compared points = FAIL.
        internal static void RegisterPreCampCheck()
        {
            new Terminal.ConsoleCommand("fat_precamp_check", "[tol=0.02] - compare the ground round the last pre-camp restore with its capture (heightmap, voxel surface, paint)",
                args =>
                {
                    float tol = args.Length > 1 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float tv) ? tv : 0.02f;
                    string line;
                    try { line = CheckLast(tol); } catch (Exception ex) { line = $"{Tag} fat_precamp_check FAIL: {ex.GetType().Name}: {ex.Message}"; }
                    FiresLogger.LogInfo(line); args.Context?.AddString(line);
                }, isCheat: true);
        }

        private static string CheckLast(float tol)
        {
            if (s_lastRestored == null) return $"{Tag} fat_precamp_check FAIL: no pre-camp restore this session";
            byte[] raw = Inflate(s_lastRestored);
            if (raw == null) return $"{Tag} fat_precamp_check FAIL: the last snapshot was refused";   // 0.2.1333
            int cmpH = 0, cmpV = 0, badH = 0, badV = 0, badP = 0; float maxH = 0f, maxV = 0f;
            var dv = new List<float>();
            using (var rd = new BinaryReader(new MemoryStream(raw)))
            {
                int ox = rd.ReadInt32(), oz = rd.ReadInt32(), n = rd.ReadInt32(); float r = rd.ReadSingle(), cy = rd.ReadSingle();
                for (int j = -n; j <= n; j++)
                    for (int i = -n; i <= n; i++)
                    {
                        if (i * i + j * j > r * r) continue;
                        byte f = rd.ReadByte();
                        if ((f & 1) == 0) continue;
                        rd.ReadSingle(); rd.ReadSingle();
                        var paint = new Color(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
                        float h0 = rd.ReadSingle(), s0 = rd.ReadSingle();
                        var p = new Vector3(ox + i, cy, oz + j);
                        if (TryRead(p, out var now))
                        {
                            cmpH++; float dh = Mathf.Abs(p.y + now.Rel - h0); maxH = Mathf.Max(maxH, dh); if (dh > tol) badH++;
                            var want = (f & 4) != 0 ? paint : Color.clear; var got = now.ModP ? now.Paint : Color.clear;
                            if (Mathf.Abs(want.r - got.r) + Mathf.Abs(want.g - got.g) + Mathf.Abs(want.b - got.b) + Mathf.Abs(want.a - got.a) > 1e-4f) badP++;
                        }
                        if (!float.IsNaN(s0) && VoxelWorld.TryFindSurfaceY(p.x, p.z, h0, 24f, out float s1))
                        { cmpV++; float d = Mathf.Abs(s1 - s0); dv.Add(d); maxV = Mathf.Max(maxV, d); if (d > tol) badV++; }
                    }
            }
            dv.Sort();
            float p95 = dv.Count == 0 ? 0f : dv[Mathf.Clamp(Mathf.CeilToInt(0.95f * dv.Count) - 1, 0, dv.Count - 1)];
            bool pass = cmpH > 0 && cmpV > 0 && badH == 0 && badV == 0 && badP == 0;
            return string.Format(CultureInfo.InvariantCulture, "{0} fat_precamp_check {1}: heightmap {2} compared, {3} over {4:0.000} m (max {5:0.000}); voxel surface {6} compared, {7} over (p95 {8:0.000}, max {9:0.000} m); paint {10} compared, {11} differ{12}",
                Tag, pass ? "PASS" : "FAIL", cmpH, badH, tol, maxH, cmpV, badV, p95, maxV, cmpH, badP, cmpH == 0 || cmpV == 0 ? " (0 compared = FAIL)" : "");
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

        // ADD-ONLY public read of the paint RGBA at the
        // heightmap vertex nearest (x, z) (0..1 per channel, the value Capture stores; on voxel ground FAT draws this same paint).
        public static bool TryReadPaint(float x, float z, out Color paint)
        {
            // Read-only: never creates a TerrainComp (TryRead does, for writers). No comp there = never painted = Color.clear, true.
            paint = Color.clear;
            var p = new Vector3(Mathf.Round(x), 0f, Mathf.Round(z));
            var hm = Heightmap.FindHeightmap(p);
            if (hm == null) return false;
            var tc = TerrainComp.FindTerrainCompiler(p);
            if (tc == null) return true;
            if (!Initialized(tc) || !Index(hm, tc, p, out int idx)) return false;
            if (ModifiedPaint(tc)[idx]) paint = PaintMask(tc)[idx];
            return true;
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
        // A player fell through the world packing a camp: this ran on EVERY Apply, a 160 m-tall Restore over
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

        // `fat_camp_residue_clear <x> <z> <radius>` (admin): the one-time clear of the voxel-brush edits an early camp
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
