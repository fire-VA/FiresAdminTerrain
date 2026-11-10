using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1100 (Fire: "still no paths in snow ... vanilla does this, we just need to mimic the same function but better").
    // Vanilla's deep-snow trail, two halves (assembly_valheimREF1.0.15):
    //  * trigger: Character.UpdateWalking (Character.cs:1172-1183) spawns m_deepSnowWalkObj (snow_walk: Cultivate paint r 1, strength 10,
    //    exp 0, centre/point multiplication 1, median max 1.3) every m_deepSnowWalkObjDist (1 m) when the biome is Deep North, the vanilla
    //    heightmap's paint G x m_deepSnowSlowMaxHeight > m_deepSnowSlowStartHeight (0.3) and IsOnHeightmap();
    //  * writer: TerrainComp.PaintCleared (TerrainComp.cs:447-590). In Deep North a Cultivate op runs in "snow mode" (isChangingSnow):
    //    G = from + strength x centreG x from, so it only reshapes G where vanilla's deep-snow G already is.
    // On voxel / super-flat Deep North the vanilla paint G is 0 (the snow is Core's field), so the trigger rarely passes (no
    // heightmap under the player, G 0) and when an op does arrive the writer multiplies by 0: "g written 0 cells" in Fire's log.
    // FAT mirrors both halves on voxel ground only: the same trigger with Core's snow depth in place of the paint G, and the same op
    // written with vanilla's plain (non-snow) Lerp branch, so the cells under the step go to G 1 - saved and synced like vanilla's
    // own trail paint (the op runs on the TerrainComp owner inside DoOperation, before its Save). Idempotent: G only ever rises.
    internal static class FatSnowTrail
    {
        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> Hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> PaintMask = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedPaint = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly AccessTools.FieldRef<TerrainComp, int> Width = AccessTools.FieldRefAccess<TerrainComp, int>("m_width");
        private static readonly AccessTools.FieldRef<Character, ZNetView> NView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");

        // 0.2.1107: Check = where the gates last ran. Before, a character that never spawned (every non-Deep-North walker) kept
        // Pos at infinity, so it passed the spacing test EVERY physics step and paid IsOnHeightmap + two GetBiome + the voxel test
        // each time. The gates now run at most once per m_deepSnowWalkObjDist moved (vanilla's own spacing), spawns unchanged.
        private sealed class Last { public Vector3 Pos = new Vector3(float.MaxValue, 0f, 0f), Check = new Vector3(float.MaxValue, 0f, 0f); }
        private static readonly ConditionalWeakTable<Character, Last> s_last = new ConditionalWeakTable<Character, Last>();
        private static float s_writeLogAt, s_spawnLogAt, s_shovelLogAt;
        private static bool s_conditionSaid;
        private static int s_spawned, s_written;
        // 0.2.1113 (round #13: unarmed swings "G changed at 3 of 81 points"): G writes this session by op kind, so the proof says
        // WHICH op touched G in its window (a walk op from the player's own step, a late shovel patch, or any other Cultivate op).
        internal static int WalkWrites, PatchWrites, OtherWrites, CreatureWrites;   // 0.2.1129: + creature trail ops
        // 0.2.1116: where this peer wrote walk ops (x, unscaled time, z), so the proof can tell the player's own steps from a swing's write.
        internal static readonly System.Collections.Generic.List<Vector3> WalkAt = new System.Collections.Generic.List<Vector3>();

        private static bool VoxelGround(Vector3 p)
            => FiresVoxelConfig.StreamerRuns && (VoxelOnlyMode.Active || VoxelZoneStreamer.IsZoneKnown(ZoneSystem.GetZone(p)));
        // The owner-side gate (also on a dedicated server, where the zone streamer is client-only): this world's ground is voxel.
        internal static bool VoxelWorldHere(Vector3 p)
            => FiresVoxelConfig.StreamerRuns && (FatSuperFlat.On || FiresVoxelConfig.WrapsAllTerrain || VoxelOnlyMode.Active || VoxelZoneStreamer.IsZoneKnown(ZoneSystem.GetZone(p)));

        // 0.2.1140 (Fire: Mountain snow = "Visual + trails only"): the voxel trail/shovel/refill gates were "biome == Deep North";
        // they now take Mountain too, where Core's field has snow. Deep North keeps its old rule (any depth gate stays with the caller);
        // a Mountain point must also carry Core snow (so the cultivated paint, which shares G, is never touched off the snow).
        // The sink/slow is NOT gated here - it stays Deep North only.
        internal const float MountainSnowMinDepth = 0.05f;
        internal static bool SnowTrailBiome(Vector3 p)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return false;
            // 0.2.1160 (PERF): one biome call first, the depth next, the 2D biome only when both said no (was two GetBiome every call)
            if (wg.GetBiome(p) == Heightmap.Biome.DeepNorth) return true;
            // 0.2.1154 (Fire: "its because this section is meadows that the trail does not draw"): trails cut wherever there IS snow
            // depth (painted Deep North snow on Meadows, Mountain, Core 935 winter), not by biome name.
            if (VoxelSnowDepth.DepthMetresAt(p) > MountainSnowMinDepth) return true;
            return wg.GetBiome(p.x, p.z) == Heightmap.Biome.DeepNorth;
        }
        internal static bool IsSnowTrailBiome(Heightmap.Biome b) => b == Heightmap.Biome.DeepNorth || b == Heightmap.Biome.Mountain;

        // 0.2.1149 (round 34: "walked 11.7 m on Mountain snow 0.65 m; centre G 0.00"): the op is APPLIED on the TerrainComp owner (the
        // dedicated server), and SnowTrailBiome's Mountain branch asks THAT peer's Core depth - which the walker already checked on its
        // own side (the trigger spawns the op only over snow > the start height). On the owner the Mountain depth can read 0 (a zone
        // the server has not built up / a test build-up held on the client only), so every Mountain trail op was refused there.
        // The owner gate is now: Deep North as before; Mountain when the op is one of OUR snow ops (walk / creature / shovel patch) -
        // the spawner's own depth check is the snow test. A plain cultivator op (Kind Other) on Mountain is never touched.
        private static bool s_ownerMountainSaid;
        internal static bool OwnerSnowGate(Vector3 pos, TerrainOp.Settings modifier)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return false;
            // 0.2.1154: any biome (Fire: Meadows with snow refused every op = the trench stopped dead at the biome line); still OUR ops only.
            // 0.2.1160 (PERF): our own ops pass in any biome, so the biome is asked only for a plain (cultivator) op
            bool ours = Kind(modifier) != OpKind.Other;
            if (!ours) return wg.GetBiome(pos) == Heightmap.Biome.DeepNorth || wg.GetBiome(pos.x, pos.z) == Heightmap.Biome.DeepNorth;
            Heightmap.Biome b3;
            if (!s_ownerMountainSaid && FiresLogger.DebugEnabled && (b3 = wg.GetBiome(pos)) != Heightmap.Biome.DeepNorth)
            {
                s_ownerMountainSaid = true;
                FiresLogger.LogDebug($"[SnowTrail] non-Deep-North ({b3}) trail op applied on this peer at ({pos.x:0.0},{pos.z:0.0}) (kind {Kind(modifier)}; this peer's Core depth {VoxelSnowDepth.DepthMetresAt(pos):0.00} m, not gated: the walker checked the snow) - once per session");
            }
            return true;
        }

        // ── 0.2.1111 trench geometry ──────────────────────────────────────────────────────────────────────────────────────────
        // Fire: "The trench can be 1 m wide ... nearly straight walls on the side like a little mini canyon ... no leveling around you".
        // G (1 m paint texels, vanilla's grid) holds a distance field to the walked line: G = 0.5 + (half - d) x FieldSlope, clamped.
        // Bilinear sampling of a field that is linear in d gives back d exactly between texels on a straight line, so the shader's
        // step at G 0.5 (+- WallHalfG) puts each wall half the width from the line within WallHalfG / FieldSlope metres - inside
        // one 0.25 m refined cell - whatever the walking direction. Everything beyond the contour keeps its snow.
        // 0.2.1418 (Fire: "player trails in the snow are still FAR too fat"; [lead]: Vedr's rule): a key, default 0.8 m wall to wall (was 1.0).
        internal static float TrenchWidth => FiresVoxelConfig.WalkTrenchWidth != null ? FiresVoxelConfig.WalkTrenchWidth.Value : 0.8f;
        // 0.2.1113 (round #13: walked trench 1.08 m wide but "wall slope 6 deg (run 9.00 m)" = the cut never reached 0.9 on a side):
        // at 0.4 G/m a texel inside the 1 m channel holds at most G 0.7, and the bilinear fold across the centre line (the nearest
        // inner texel can sit on the far side of the line) left the middle near G 0.5, so the shader's step (0.46-0.54) cut the snow
        // only part way and the far wall ran out over metres. A 400-alignment model of the 1 m texel grid (random line angle and
        // offset, the shader's bilinear + smoothstep): 0.4 G/m fails 175/400 (173 soft walls), 1.5 G/m fails 37/400 (17 soft) -
        // the best the 1 m paint grid allows (steeper clamps the field inside its reach and shifts the walls). The field now
        // reaches 0 at half + 0.33 m: nothing beyond 0.83 m from the line is touched.
        internal const float FieldSlope = 1.5f;    // G per metre
        // 0.2.1150 (Fire: "our real deep snow does not clear the path completely"): on the 1 m paint grid a line running half a texel
        // off the texel rows had both rows at d = 0.5 -> G exactly 0.5 = the MIDDLE of the shader's 0.46-0.54 cut, so the whole
        // centre of that stretch was only half cut (the ledges/blocks left in the path; zig-zagging as the line crossed texel rows).
        // A 400-line model on the grid (the shader's bilinear sample): bias 0 -> centre G min 0.500, 32/400 lines not fully cut,
        // width 0.53-1.11 m; bias 0.04 m -> centre G min 0.561, 0/400 not fully cut, width 0.82-1.21 m (the 1 m target +- 0.25).
        internal const float WalkCoreBias = 0.04f;
        // 0.2.1154 ([wishbone] + the lead): walk / creature / snowball trails spawn over more than this much snow, BELOW the slow's
        // m_deepSnowSlowStartHeight (0.3 m): Core 935 winter snow defaults to 0.3 m, which the old gate (> 0.3) never trailed.
        internal const float TrailMinDepthMetres = 0.05f;
        private static bool s_walkNonDnSaid;
        // 0.2.1309 (Fire: "the Deep North way", with Core 0.2.1061): on vanilla HEIGHTMAP ground FAT stands down completely - Core puts
        // seasonal snow in the heightmap BASE mask like Deep North, vanilla's own UpdateWalking lays snow_walk, vanilla's PaintCleared
        // paints it and Core refills toward base. Removed: the 0.2.1301/1307 Core SnowTrail.Stamp branch (CoreHeightmapTrail +
        // Patch_RemoteTrail), the writer's 1307 refusal of snow_walk outside Deep North, FAT's heightmap trail mask stamps and the
        // FAT snow skin. Voxel ground keeps FAT's own trail system unchanged.
        private static bool s_hmStandDownSaid;
        internal const float WallHalfG = 0.04f;    // the shader's smoothstep(0.46, 0.54): 0.08 / 1.5 = a 0.05 m wall on a straight line
        internal enum OpKind { Other, Walk, Patch, Creature }

        private static TerrainOp.Settings s_walkSettings;
        private static bool s_walkLearned;
        // The walk op is the Player prefab's m_deepSnowWalkObj (snow_walk); deserialised settings are copies, so compare values.
        // A FAT shovel patch (FatShovelPatch) is the same op spawned with a radius of half the patch width + RadiusTag
        // (0.2.1112: 0.5-4 m wide in 0.5 m steps, the wheel).
        internal static OpKind Kind(TerrainOp.Settings s)
        {
            if (s == null || s.m_paintType != TerrainModifier.PaintType.Cultivate || s.m_paintStrength <= 0f || s.m_level || s.m_raise || s.m_smooth) return OpKind.Other;
            if (!s_walkLearned)
            {
                var w = WalkPrefab();
                if (w != null) { s_walkLearned = true; s_walkSettings = w.GetComponent<TerrainOp>()?.m_settings; }
            }
            var ws = s_walkSettings;
            if (ws != null && Mathf.Abs(s.m_paintStrength - ws.m_paintStrength) > 0.001f) return OpKind.Other;
            if (ws == null && s.m_paintStrength < 1f) return OpKind.Other;
            if (FatCreatureTrail.HalfFromRadius(s.m_paintRadius, out _)) return OpKind.Creature;   // 0.2.1129: r = width / 2 + 0.007
            if (FatShovelPatch.HalfFromRadius(s.m_paintRadius, out _)) return OpKind.Patch;   // first: a 2 m patch is r 1 + the tag
            if (ws != null ? Mathf.Abs(s.m_paintRadius - ws.m_paintRadius) <= 0.001f : s.m_paintRadius > 0.75f && s.m_paintRadius <= 1.5f) return OpKind.Walk;
            return OpKind.Other;
        }

        internal static GameObject WalkPrefab()
        {
            var scene = ZNetScene.instance;
            var player = scene != null ? scene.GetPrefab("Player")?.GetComponent<Player>() : null;
            return player != null ? player.m_deepSnowWalkObj : null;
        }

        // Vanilla's PaintCleared runs before the postfix and writes its own r 1 disc (2 m) into G (or, in its Deep North snow mode,
        // scales the G already there). On voxel Deep North ground that G is ours, so the prefix keeps the cells the op can reach
        // and the postfix puts them back before writing the field. Owner peer, main thread, one op at a time.
        private static class Snapshot
        {
            private static TerrainComp s_comp;
            private static int s_x0, s_y0, s_x1, s_y1, s_pitch;
            private static float[] s_g = new float[0];

            internal static void Take(TerrainComp comp, Heightmap hm, Color[] paint, int pitch, Vector3 wp, float radius)
            {
                s_comp = null;
                hm.WorldToVertexMask(wp, out int cx, out int cy);
                int r = Mathf.CeilToInt(radius / hm.m_scale) + 1;
                s_x0 = Mathf.Max(0, cx - r); s_y0 = Mathf.Max(0, cy - r);
                s_x1 = Mathf.Min(pitch - 1, cx + r); s_y1 = Mathf.Min(pitch - 1, cy + r);
                if (s_x1 < s_x0 || s_y1 < s_y0) return;
                int n = (s_x1 - s_x0 + 1) * (s_y1 - s_y0 + 1);
                if (s_g.Length < n) s_g = new float[n];
                int k = 0;
                for (int y = s_y0; y <= s_y1; y++)
                    for (int x = s_x0; x <= s_x1; x++)
                    {
                        int i = y * pitch + x;
                        s_g[k++] = i < paint.Length ? paint[i].g : 0f;
                    }
                s_pitch = pitch;
                s_comp = comp;
            }

            internal static int Restore(TerrainComp comp, Color[] paint, bool[] mod)
            {
                if (s_comp != comp) return 0;
                s_comp = null;
                int k = 0, n = 0;
                for (int y = s_y0; y <= s_y1; y++)
                    for (int x = s_x0; x <= s_x1; x++)
                    {
                        int i = y * s_pitch + x;
                        float g = s_g[k++];
                        if (i >= paint.Length || Mathf.Abs(paint[i].g - g) < 0.0001f) continue;
                        var c = paint[i]; c.g = g; paint[i] = c; mod[i] = true; n++;
                    }
                return n;
            }
        }

        // The walked line: each walk op joins the previous one 0.3-1.8 m away and at most 4 s old (vanilla spawns one every
        // m_deepSnowWalkObjDist, 1 m). The same op reaches every comp it overlaps, so a repeat of the same point reuses its link.
        internal static class Recent   // 0.2.1157: internal - the walker's prediction (FatTrailFast) links with the same list
        {
            private struct P { public Vector3 At, Prev; public float Time; }
            private static readonly System.Collections.Generic.List<P> s_pts = new System.Collections.Generic.List<P>();

            // 0.2.1155 (Fire: "not consistently making trails"; ~1/3 of walk ops linked a 0 m segment = a 0.54 m dot, the thin spots
            // the refill ate first): no link 0.3-1.8 m / 4 s -> the walker's own last op, up to FallbackMetres and FallbackSeconds.
            internal const float FallbackMetres = 3.0f, FallbackSeconds = 6f;   // 0.2.1163: 2.5 -> 3.0 (player stamps every 2.4 m)
            internal const float LinkMetres = 2.6f;   // 0.2.1163: 1.8 -> 2.6, so a 2.4 m player step still links its segment
            internal static int Fallbacks, Dots;

            internal static Vector3 Prev(Vector3 at)
            {
                float now = Time.unscaledTime;
                while (s_pts.Count > 0 && now - s_pts[0].Time > FallbackSeconds) s_pts.RemoveAt(0);   // 0.2.1163: oldest first, no closure allocation per op
                for (int i = s_pts.Count - 1; i >= 0; i--)
                    if ((s_pts[i].At - at).sqrMagnitude < 0.0025f) return s_pts[i].Prev;
                Vector3 prev = at;
                bool linked = false;
                for (int i = s_pts.Count - 1; i >= 0; i--)
                {
                    if (now - s_pts[i].Time > 4f) continue;
                    float d = Vector2.Distance(new Vector2(s_pts[i].At.x, s_pts[i].At.z), new Vector2(at.x, at.z));
                    if (d >= 0.3f && d <= LinkMetres) { prev = s_pts[i].At; linked = true; break; }
                }
                if (!linked && s_pts.Count > 0)
                {
                    // the newest earlier op (an op carries no walker identity; the newest is this walker's in all but crowds)
                    var last = s_pts[s_pts.Count - 1];
                    float d = Vector2.Distance(new Vector2(last.At.x, last.At.z), new Vector2(at.x, at.z));
                    if (d > 0.05f && d <= FallbackMetres) { prev = last.At; linked = true; Fallbacks++; }
                }
                if (!linked) Dots++;
                if (s_pts.Count >= 32) s_pts.RemoveAt(0);
                s_pts.Add(new P { At = at, Prev = prev, Time = now });
                return prev;
            }
        }

        // 0.2.1129: an op carries no creature identity (prefab name + position only), so a creature op joins the nearest earlier
        // creature op of the SAME width 0.3-1.8 m away and < 4 s old (each creature sends one per metre). A repeat of the same point
        // (the op reaching a second comp) reuses its link.
        private static class CreatureRecent
        {
            private struct P { public Vector3 At, Prev; public float Half, Time; }
            private static readonly System.Collections.Generic.List<P> s_pts = new System.Collections.Generic.List<P>();

            internal static Vector3 Prev(Vector3 at, float half)
            {
                float now = Time.unscaledTime;
                s_pts.RemoveAll(p => now - p.Time > 4f);
                for (int i = s_pts.Count - 1; i >= 0; i--)
                    if ((s_pts[i].At - at).sqrMagnitude < 0.0025f && Mathf.Abs(s_pts[i].Half - half) < 0.01f) return s_pts[i].Prev;
                // 0.2.1131 (round #28: the deer and boar lines merged, blocky zig-zags): a join must continue its candidate's own line -
                // reject it when the new segment turns > 60 deg from the candidate's previous segment, and prefer the straightest
                // continuation (score = distance x (2 - cos turn)), so neighbouring creatures don't cross-link.
                Vector3 prev = at; float best = float.MaxValue;
                for (int i = s_pts.Count - 1; i >= 0; i--)
                {
                    if (Mathf.Abs(s_pts[i].Half - half) >= 0.01f) continue;
                    Vector2 c = new Vector2(s_pts[i].At.x, s_pts[i].At.z), q = new Vector2(at.x, at.z);
                    float d = Vector2.Distance(c, q);
                    if (d < 0.3f || d > 3.4f) continue;   // 0.2.1182: creatures stamp every 3 m (link 3.4 m)   // 0.2.1171: creatures stamp every 2.4 m now (was 1 m, link 1.8 m)
                    float cos = 1f;
                    Vector2 back = c - new Vector2(s_pts[i].Prev.x, s_pts[i].Prev.z);
                    if (back.sqrMagnitude > 0.01f)
                    {
                        cos = Vector2.Dot(back.normalized, (q - c) / d);
                        if (cos < 0.5f) continue;   // > 60 deg turn
                    }
                    float score = d * (2f - cos);
                    if (score < best) { best = score; prev = s_pts[i].At; }
                }
                if (s_pts.Count >= 128) s_pts.RemoveAt(0);
                s_pts.Add(new P { At = at, Prev = prev, Half = half, Time = now });
                return prev;
            }
        }

        // The distance field to segment a-b (in the op's mask space), max-combined with what's there: G only rises.
        private static int WriteField(Heightmap hm, Color[] paint, bool[] mod, int pitch, Vector3 a, Vector3 b, float half)
        {
            float reach = half + 0.5f / FieldSlope;   // where the field reaches 0
            float s = hm.m_scale;
            Vector3 o = hm.transform.position;
            int num = (pitch) / 2;   // vanilla WorldToVertexMask: (m_width + 1) / 2, pitch = m_width + 1
            var a2 = new Vector2(a.x, a.z); var b2 = new Vector2(b.x, b.z);
            hm.WorldToVertexMask(new Vector3(Mathf.Min(a.x, b.x) - reach, 0f, Mathf.Min(a.z, b.z) - reach), out int x0, out int y0);
            hm.WorldToVertexMask(new Vector3(Mathf.Max(a.x, b.x) + reach, 0f, Mathf.Max(a.z, b.z) + reach), out int x1, out int y1);
            x0 = Mathf.Max(0, x0); y0 = Mathf.Max(0, y0); x1 = Mathf.Min(pitch - 1, x1); y1 = Mathf.Min(pitch - 1, y1);
            Vector2 ab = b2 - a2;
            float len2 = ab.sqrMagnitude;
            int n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(o.x + (x - num) * s, o.z + (y - num) * s);   // the texel's centre in the op's mask space
                    float g = FatTrailFast.StampG(p, a2, b2, half, FieldSlope);   // 0.2.1157: the one stamp the walker's prediction uses too
                    int i = y * pitch + x;
                    if (i >= paint.Length || g <= paint[i].g + 0.001f) continue;
                    var c = paint[i]; c.g = g; paint[i] = c; mod[i] = true; n++;
                }
            return n;
        }

        // 0.2.1120 (round #17 shovel_patch: "along width 0.52 m ... no trench across (G there 0.42)", cells 2): a POINT field on the
        // 1 m paint grid is a cone, and the bilinear sample of a cone between 1 m texels sits below the cone (convex), so a 1 m
        // patch came out 0-1 m wide depending on where the hit fell between texels (a 30-offset model: 0.55 m mean, 0.00-0.98).
        // The patch now centres on the nearest texel (the hit point snapped, FatShovelPatch.SnapToTexel) with G = 0.5 + (half - d)
        // per metre, clamped: along the texel rows the bilinear 0.5 contour lands exactly half the width out (texels 1 m apart, the
        // field 1 G/m), on the diagonals the 1 m grid rounds it in (1 m: 0.83 m, 2 m: 1.47 m, 4 m: 3.88 m). The walk trench keeps WriteField.
        internal const float PatchSlope = 1f;   // G per metre
        // 0.2.1381: on heightmap ground G is the snow itself (on voxel ground it is the trench field, WritePatchField). Snow goes to 0 inside the patch radius, with the 1 m paint grid's
        // softest edge centred on that radius, so the cut matches the ruler. Paint that is edited and not a trail mark may be a garden (cultivated soil shares G): it stays.
        private static void WriteHeightmapPatch(TerrainComp comp, Vector3 pos, TerrainOp.Settings modifier)
        {
            var hm = Hmap(comp); var paint = PaintMask(comp); var mod = ModifiedPaint(comp);
            if (hm == null || paint == null || mod == null || !FatShovelPatch.HalfFromRadius(modifier.m_paintRadius, out float half)) return;
            if (!WorldGenerator.IsDeepnorth(pos.x, pos.z) && VoxelSnowDepth.DepthMetresAt(pos) <= 0f)
            {
                FiresLogger.LogDebug($"[Shovel] heightmap patch at ({pos.x:0.0},{pos.z:0.0}): no snow here, nothing cut");
                return;
            }
            if (modifier.m_halfOffset) { pos.x -= 0.5f; pos.z -= 0.5f; }
            int pitch = Width(comp) + 1, num = pitch / 2;
            float s = hm.m_scale;
            Vector3 o = hm.transform.position;
            var c2 = new Vector2(o.x + Mathf.Round((pos.x - o.x) / s) * s, o.z + Mathf.Round((pos.z - o.z) / s) * s);
            float reach = half + 0.5f;
            hm.WorldToVertexMask(new Vector3(c2.x - reach, 0f, c2.y - reach), out int x0, out int y0);
            hm.WorldToVertexMask(new Vector3(c2.x + reach, 0f, c2.y + reach), out int x1, out int y1);
            x0 = Mathf.Max(0, x0); y0 = Mathf.Max(0, y0); x1 = Mathf.Min(pitch - 1, x1); y1 = Mathf.Min(pitch - 1, y1);
            int cut = 0, kept = 0;
            float deepest = 0f, farthest = 0f;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * pitch + x;
                    if (i >= paint.Length) continue;
                    float d = Vector2.Distance(new Vector2(o.x + (x - num) * s, o.z + (y - num) * s), c2);
                    float t = Mathf.Clamp01(reach - d);
                    if (t <= 0f) continue;
                    if (mod[i] && !FatTrailCut.IsSet(comp, i)) { kept++; continue; }
                    Color c = mod[i] ? paint[i] : hm.GetPaintMask(x, y);
                    float g = c.g * (1f - t);
                    if (c.g - g < 0.0005f) continue;
                    if (c.g > deepest) deepest = c.g;
                    if (d > farthest) farthest = d;
                    c.g = g; paint[i] = c; mod[i] = true; cut++;
                }
            FiresLogger.LogDebug($"[Shovel] heightmap patch at ({c2.x:0.0},{c2.y:0.0}): requested r {half:0.00} m, {cut} texel(s) cut out to {farthest:0.00} m, top G before {deepest:0.00}, {kept} edited texel(s) kept (0.2.1381)");
        }

        private static int WritePatchField(Heightmap hm, Color[] paint, bool[] mod, int pitch, Vector3 at, float half)
        {
            float reach = half + 0.5f / PatchSlope;
            float s = hm.m_scale;
            Vector3 o = hm.transform.position;
            int num = pitch / 2;
            Vector3 hitAt = at;
            var c2 = new Vector2(o.x + Mathf.Round((at.x - o.x) / s) * s, o.z + Mathf.Round((at.z - o.z) / s) * s);   // on a texel
            at = new Vector3(c2.x, at.y, c2.y);
            hm.WorldToVertexMask(new Vector3(at.x - reach, 0f, at.z - reach), out int x0, out int y0);
            hm.WorldToVertexMask(new Vector3(at.x + reach, 0f, at.z + reach), out int x1, out int y1);
            x0 = Mathf.Max(0, x0); y0 = Mathf.Max(0, y0); x1 = Mathf.Min(pitch - 1, x1); y1 = Mathf.Min(pitch - 1, y1);
            int n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(o.x + (x - num) * s, o.z + (y - num) * s);
                    float g = Mathf.Clamp01(0.5f + (half - Vector2.Distance(p, c2)) * PatchSlope);
                    int i = y * pitch + x;
                    if (i >= paint.Length || g <= paint[i].g + 0.001f) continue;
                    var c = paint[i]; c.g = g; paint[i] = c; mod[i] = true; n++;
                }
            // 0.2.1122 (round #20: the 1 m patch 1.30 m along X, 0.98 along Z): what was written, so a round shows the texel rows themselves
            hm.WorldToVertexMask(at, out int ti, out int tj);
            string RowG(int di, int dj)
            {
                var sb = new System.Text.StringBuilder();
                for (int k = -2; k <= 2; k++)
                {
                    int xi = ti + di * k, yi = tj + dj * k, ii = yi * pitch + xi;
                    if (k > -2) sb.Append(' ');
                    sb.Append(xi >= 0 && yi >= 0 && xi < pitch && yi < pitch && ii < paint.Length ? paint[ii].g.ToString("0.00") : "-");
                }
                return sb.ToString();
            }
            FiresLogger.LogDebug($"[Shovel] patch write: hit ({hitAt.x:0.00},{hitAt.z:0.00}) snapped ({c2.x:0.00},{c2.y:0.00}) texel ({ti},{tj}) half {half:0.00}; G row X: {RowG(1, 0)}; G row Z: {RowG(0, 1)} ({n} cell(s) raised)");
            return n;
        }

        // 0.2.1309 (Fire: "the Deep North way", Core 0.2.1061 contract; restored from 0.2.1304): vanilla TerrainComp.PaintCleared decides
        // its snow mode with `WorldGenerator.IsDeepnorth(x, z) && Cultivate`. For a WALK / CREATURE snow op on HEIGHTMAP ground (OurOp,
        // set by Patch_Write's prefix around that one op) the test also answers true wherever Core has snow (SnowField.DepthAt via
        // VoxelSnowDepth.DepthMetresAt > 0), so vanilla's snow_walk reshapes the snow G there exactly as in Deep North and never tills.
        // Every other op (hoe, cultivator, vanilla's own code) is untouched.
        [HarmonyPatch(typeof(TerrainComp), "PaintCleared")]
        internal static class Patch_SnowMode
        {
            [System.ThreadStatic] internal static bool OurOp;
            private static bool s_said; private static int s_widened;
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var isDn = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.IsDeepnorth), new[] { typeof(float), typeof(float) });
                var mine = AccessTools.Method(typeof(Patch_SnowMode), nameof(SnowModeAt));
                int n = 0;
                foreach (var ci in code)
                {
                    if (isDn != null && ci.Calls(isDn)) { ci.operand = mine; n++; }
                    yield return ci;
                }
                if (n != 1) FiresLogger.LogWarning($"[SnowTrail] PaintCleared snow-mode test found {n} time(s), expected 1 - heightmap snow trails may till the soil (0.2.1309)");
                else FiresLogger.LogDebug("[SnowTrail] PaintCleared patched: walk / creature snow ops run vanilla's Deep North snow mode wherever Core has snow (0.2.1309)");
            }
            private static bool SnowModeAt(float x, float z)
            {
                if (WorldGenerator.IsDeepnorth(x, z)) return true;
                if (!OurOp) return false;
                bool snow = false;
                try { snow = VoxelSnowDepth.DepthMetresAt(new Vector3(x, 0f, z)) > 0f; } catch { }
                if (snow)
                {
                    s_widened++;
                    if (!s_said) { s_said = true; FiresLogger.LogDebug($"[SnowTrail] heightmap snow_walk in vanilla snow mode at ({x:0.0},{z:0.0}) (not Deep North; Core snow there) - no tilling (0.2.1309)"); }
                }
                return snow;
            }
        }

        [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation")]
        private static class Patch_Write
        {
            // 0.2.1111: keep the G vanilla's PaintCleared is about to change (walk, FAT patch and every swing's snow op) on voxel Deep North.
            // 0.2.1116: vanilla's melee snow op (Player.m_snowShovelDefault / Strong) is not applied at all on FAT voxel ground (see
            // FatShovelPatch.IsVanillaMeleeSnowOp); __state tells the postfix (Harmony runs postfixes after a skipping prefix).
            private static bool Prefix(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier, out bool __state)
            {
                __state = false;
                try
                {
                    if (FatShovelPatch.IsVanillaMeleeSnowOp(modifier, out string meleeName) && (VoxelWorldHere(pos) || (FatShovelPatch.OnHeightmapToo && FatShovelPatch.IsShovelStrongOp(modifier))))
                    {
                        __state = true;
                        FatShovelPatch.NoteBlocked(pos, modifier, meleeName);
                        return false;
                    }
                    if (modifier == null || !modifier.m_paintCleared || modifier.m_paintType != TerrainModifier.PaintType.Cultivate) return true;
                    if (modifier.m_paintStrength > 0f && Kind(modifier) == OpKind.Other) return true;
                    // 0.2.1381 (Fire: "shovel doesnt actually remove snow in the radius of its ruler"): vanilla's PaintCleared fades to nothing at the op's rim, so a patch never reached the
                    // ruler. On heightmap ground the patch is written here and vanilla's op does not run. __state tells the postfix (Harmony runs postfixes after a skipping prefix).
                    if (!VoxelWorldHere(pos) && Kind(modifier) == OpKind.Patch) { __state = true; WriteHeightmapPatch(__instance, pos, modifier); return false; }
                    // 0.2.1309 (the Deep North way): a walk / creature snow op on HEIGHTMAP ground runs vanilla's PaintCleared in snow mode where Core has snow
                    if (!VoxelWorldHere(pos) && (Kind(modifier) == OpKind.Walk || Kind(modifier) == OpKind.Creature || Kind(modifier) == OpKind.Patch)) { Patch_SnowMode.OurOp = true; return true; }   // 0.2.1376: + the shovel patch, or it tills outside Deep North
                    if (!VoxelWorldHere(pos)) return true;
                    var wg = WorldGenerator.instance;
                    if (wg == null || !OwnerSnowGate(pos, modifier)) return true;   // 0.2.1149: owner gate (Mountain = our ops only)
                    var hm = Hmap(__instance); var paint = PaintMask(__instance);
                    if (hm == null || paint == null) return true;
                    Vector3 wp = pos;
                    if (modifier.m_halfOffset) { wp.x -= 0.5f; wp.z -= 0.5f; }
                    Snapshot.Take(__instance, hm, paint, Width(__instance) + 1, wp, modifier.m_paintRadius);
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[SnowTrail] bail: snapshot threw: {ex.Message}"); }
                return true;
            }

            private static void Postfix(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier, bool __state)
            {
                Patch_SnowMode.OurOp = false;   // 0.2.1309: the snow-mode widening is for this one op only

                try
                {
                    if (__state) return;   // 0.2.1116: a vanilla melee snow op the prefix kept off voxel ground
                    if (modifier == null || !modifier.m_paintCleared || modifier.m_paintType != TerrainModifier.PaintType.Cultivate) return;
                    // 0.2.1101 (Fire: "still no fucking paths dug in the snow"; his client log: 220 snow_walk spawns, ZERO "G written" lines):
                    // the op is applied on the TerrainComp OWNER - the dedicated server here (its log: "[OpStorm] receive from Vafire") -
                    // where (1) WorldGenerator.IsDeepnorth is vanilla's raw geography (not the painted biome super flat uses) and (2) the
                    // client-only zone streamer knows no zones, so the old gate refused every op. Now: the paint-aware biome at the op, and a
                    // world-level voxel check that holds on the dedi. Negative strengths (the snow shovel's snow_decrease r 3 -0.2, a fire's
                    // melter) dig too: on voxel snow every Deep North Cultivate op lowers the snow through G at the paint grid's 1 m cells.
                    if (Mathf.Abs(modifier.m_paintStrength) < 0.0001f) return;
                    // 0.2.1111 (Fire: "the shovel clears an even wider area"): vanilla runs its snow clear on EVERY Deep North melee swing
                    // (Attack.cs:1174-1185: Player.m_snowShovelDefault/Strong at the swing point) - snow_decrease, Cultivate r 3 strength -0.2,
                    // which in vanilla's snow mode only scales the deep-snow G (TerrainComp.cs:454, 536-541). FAT turned it into a full
                    // r 3 trench (a 6 m patch on any weapon). Negative ops now write nothing; the snow shovel's own 1 m patch is FatShovelPatch.
                    if (modifier.m_paintStrength < 0f)
                    {
                        // on voxel Deep North the G vanilla's snow mode just scaled is our trench field: put it back (the snow there is Core's)
                        var pm = PaintMask(__instance); var md = ModifiedPaint(__instance);
                        int back = pm != null && md != null ? Snapshot.Restore(__instance, pm, md) : 0;
                        if (Time.unscaledTime >= s_shovelLogAt)
                        {
                            s_shovelLogAt = Time.unscaledTime + 2f;
                            FiresLogger.LogDebug($"[Shovel] vanilla swing snow op at ({pos.x:0.0},{pos.z:0.0}) r {modifier.m_paintRadius:0.0} strength {modifier.m_paintStrength:0.00}: no FAT patch, {back} trench cell(s) kept");
                        }
                        return;
                    }
                    // 0.2.1105 (round #3: 0 "G written" lines on the dedi while Core stamped the same ops there): every gate is named in an
                    // always-on line, and the biome test also takes the 2D (x,z) answer - the 3D one stands aside for points it treats as
                    // off the overworld surface (FatSuperFlat.OffSurface: 12 m over the flat height under a skyland cell), e.g. a hut on a
                    // raised build site or a trail on a hill.
                    bool voxel = VoxelWorldHere(pos);
                    var wg = WorldGenerator.instance;
                    var b3 = wg != null ? wg.GetBiome(pos) : Heightmap.Biome.None;
                    var b2 = wg != null ? wg.GetBiome(pos.x, pos.z) : Heightmap.Biome.None;
                    bool dn = OwnerSnowGate(pos, modifier);   // 0.2.1149: owner gate (was SnowTrailBiome: the owner's Mountain depth refused the ops)
                    var hm = Hmap(__instance);
                    var paint = PaintMask(__instance);
                    var mod = ModifiedPaint(__instance);
                    string gates = $"voxel {voxel}, biome 3D {b3} / 2D {b2}, heightmap {(hm != null)}, paint mask {(paint != null)}";
                    if (!voxel || !dn || hm == null || paint == null || mod == null)
                    {
                        Applied(0, pos, modifier, "REFUSED: " + gates);
                        return;
                    }
                    // 0.2.1419 ([lead] decision 11-08 on the double claim, Fire's "trails FAR too fat"): where Vedr draws the chunk, Vedr owns
                    // the trail. The split is the one Vedr already uses (its SnowFat.OwnsZone): FAT owns a zone only when FatApi.IsHeightmapHidden
                    // says FAT hid that Heightmap and its voxel mesh draws; a data-only voxel zone (vanilla's Heightmap still draws) is Vedr's.
                    // Gated on Vedr's LIVE GroundSnowActive. A dedicated server draws nothing, so it keeps cutting there (no change).
                    if (StandDownForVedr(__instance, hm, pos))
                    {
                        Applied(0, pos, modifier, "stood down: Vedr draws this chunk");   // nothing written: the G is Vedr's to handle (it puts vanilla's paint back itself)
                        return;
                    }
                    int pitch = Width(__instance) + 1;
                    Vector3 wp = pos;
                    if (modifier.m_halfOffset) { wp.x -= 0.5f; wp.z -= 0.5f; }
                    hm.WorldToVertexMask(wp, out int cx, out int cy);
                    float f = modifier.m_paintRadius / hm.m_scale;
                    int r = Mathf.CeilToInt(f);
                    var centre = new Vector2(cx, cy);
                    // 0.2.1109 (round #7: 71 ops "G written 0"): snow_walk is Cultivate r 1 strength 10, so vanilla's own PaintCleared
                    // (this postfix runs after it) may already have set the centre to G 1 - then FAT adds nothing and that's fine. The
                    // line now says which: the centre G vanilla left, or "off this comp" when the op centre isn't on its mask.
                    int ci = cy * pitch + cx;
                    string vanillaCentre = cx >= 0 && cy >= 0 && cx < pitch && cy < pitch && ci < paint.Length ? paint[ci].g.ToString("0.00") : "off this comp";
                    gates += $", centre G after vanilla {vanillaCentre}";
                    if (modifier.m_paintStrength >= 1f && modifier.m_paintRadius <= 1.5f)   // a walk op (vanilla snow_walk: r 1, strength 10)
                    {
                        s_walkOps++;
                        if (vanillaCentre == "off this comp") s_walkOpsOff++;
                        else if (paint[ci].g >= 0.99f) s_walkOpsVanillaSet++;
                    }
                    int n = 0;
                    string peer = ZNet.instance == null ? "?" : ZNet.instance.IsServer() ? (ZNet.instance.IsDedicated() ? "dedicated server" : "host") : "client " + ZDOMan.GetSessionID();
                    var kind = Kind(modifier);
                    if (kind != OpKind.Other)
                    {
                        // 0.2.1111 (Fire: "no leveling around you. It carves an actual trench with nearly straight walls ... 1 m wide"):
                        // vanilla's PaintCleared (just run) wrote snow_walk's r 1 disc - a 2 m circle per step, then the 1097 blur made it ~4 m.
                        // Its G is put back first (the prefix's snapshot), then G is written as a distance field to the walked line:
                        // G = 0.5 + (half width - d) x FieldSlope (1.5 G/m since 0.2.1113), so the 0.5 contour sits exactly half the width from the line (any direction,
                        // bilinear-exact on a straight line) and the shader's step at 0.5 is the wall.
                        int restored = Snapshot.Restore(__instance, paint, mod);
                        Vector3 a = pos, b = pos;
                        float half = kind == OpKind.Walk ? TrenchWidth * 0.5f
                            : kind == OpKind.Creature ? (FatCreatureTrail.HalfFromRadius(modifier.m_paintRadius, out float chh) ? chh : 0.5f)
                            : FatShovelPatch.HalfFromRadius(modifier.m_paintRadius, out float ph) ? ph : 0.5f;
                        if (kind == OpKind.Walk) a = Recent.Prev(pos);
                        else if (kind == OpKind.Creature) a = CreatureRecent.Prev(pos, half);
                        if (modifier.m_halfOffset) { a.x -= 0.5f; a.z -= 0.5f; b.x -= 0.5f; b.z -= 0.5f; }
                        n = kind == OpKind.Patch ? WritePatchField(hm, paint, mod, pitch, a, half) : WriteField(hm, paint, mod, pitch, a, b, kind == OpKind.Walk ? half + WalkCoreBias : half);
                        s_written += n;
                        if (n > 0) FatSnowTrailRefill.NoteTrailWrite(__instance, hm, pitch, a, b, half);   // 0.2.1155: 10 min refill grace
                        if (n > 0) { if (kind == OpKind.Walk) WalkWrites++; else if (kind == OpKind.Creature) CreatureWrites++; else PatchWrites++; }
                        if (kind == OpKind.Walk) { if (WalkAt.Count >= 64) WalkAt.RemoveAt(0); WalkAt.Add(new Vector3(pos.x, Time.unscaledTime, pos.z)); }
                        Applied(n, pos, modifier, "passed: " + gates + $", vanilla disc undone on {restored} cell(s)");
                        // 0.2.1418 ([lead]: "Debug line per op: owner, radius, texels changed/put back"; FAT owns the trail on its zones)
                        if (kind != OpKind.Patch && Time.unscaledTime >= s_opLineAt)
                        {
                            s_opLineAt = Time.unscaledTime + 2f;
                            FiresLogger.LogDebug($"[SnowTrail] op (owner FAT, {peer}) {kind} at ({pos.x:0.0},{pos.z:0.0}): op r {modifier.m_paintRadius:0.000}, trench {half * 2f:0.00} m wall to wall " +
                                                 $"(field to {half + (kind == OpKind.Walk ? WalkCoreBias : 0f) + 0.5f / FieldSlope:0.00} m either side); vanilla's own paint put back on {restored} texel(s), {n} trench cell(s) written");
                        }
                        if (kind == OpKind.Walk)
                        {
                            if (Time.unscaledTime >= s_writeLogAt)
                            {
                                s_writeLogAt = Time.unscaledTime + 5f;
                                float seg = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                                FiresLogger.LogDebug($"[SnowTrail] trench width {TrenchWidth:0.00} m (key Walk trench width; owner FAT, field to {TrenchWidth * 0.5f + WalkCoreBias + 0.5f / FieldSlope:0.00} m either side), wall cut at G 0.5 +- {WallHalfG:0.00} = {WallHalfG / FieldSlope * 2f:0.00} m: segment {seg:0.0} m to ({pos.x:0.0},{pos.z:0.0}), G cells {n} (owner peer {peer}; {s_written} this session)");
                            }
                        }
                        else if (kind == OpKind.Patch)
                            FiresLogger.LogDebug($"[Shovel] ground hit at ({pos.x:0.0},{pos.z:0.0}): patch {half * 2f:0.0} m, cells {n} (owner peer {peer})");
                    }
                    else
                    {
                        for (int y = Mathf.Max(0, cy - r); y <= Mathf.Min(pitch - 1, cy + r); y++)
                            for (int x = Mathf.Max(0, cx - r); x <= Mathf.Min(pitch - 1, cx + r); x++)
                            {
                                float t = Vector2.Distance(centre, new Vector2(x, y)) / f;
                                if (t > 1f) continue;
                                // vanilla's non-snow branch: Lerp(from, cultivated G 1, pow(1 - t, exp) x strength)
                                float v = (modifier.m_paintCurve == null || modifier.m_paintCurve.length == 0
                                    ? Mathf.Pow(1f - Mathf.Clamp01(t), modifier.m_paintExp) : modifier.m_paintCurve.Evaluate(t)) * Mathf.Abs(modifier.m_paintStrength);
                                int i = y * pitch + x;
                                if (i < 0 || i >= paint.Length) continue;
                                var c = paint[i];
                                float g = Mathf.Max(c.g, Mathf.Lerp(c.g, 1f, Mathf.Clamp01(v)));
                                if (g <= c.g + 0.001f) continue;
                                c.g = g;
                                paint[i] = c;
                                mod[i] = true;
                                n++;
                            }
                        s_written += n;
                        if (n > 0) OtherWrites++;
                        Applied(n, pos, modifier, "passed: " + gates);
                        if (n > 0 && Time.unscaledTime >= s_writeLogAt)
                        {
                            s_writeLogAt = Time.unscaledTime + 5f;
                            FiresLogger.LogDebug($"[SnowTrail] G written {n} cells (owner peer {peer}) at ({pos.x:0.0},{pos.z:0.0}) by a Cultivate op r {modifier.m_paintRadius:0.0} strength {modifier.m_paintStrength:0.00} (not the walk op): vanilla's Lerp branch ({s_written} this session)");
                        }
                    }
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[SnowTrail] bail: writer threw: {ex.Message}"); }
            }
        }

        private static float s_walkLogAt;
        private static int s_walkSpawned;
        // 0.2.1105: always on, at most one line every 10 s while a player walks: spawns since the last line and the last gate result.
        // 0.2.1106 (round #4: "spawned 37 op(s), last at (306.1,939.7)" read as spawns at the homestead; the 37 were at the hut and
        // the position was the LAST REFUSAL's): the line now gives the last SPAWN's position and the refusals counted per gate.
        private static readonly System.Collections.Generic.Dictionary<string, int> s_walkRefused = new System.Collections.Generic.Dictionary<string, int>();
        private static Vector3 s_lastSpawnAt;
        private static float s_refuseLogAt;   // 0.2.1309
        private static void Walk(Character c, Vector3 p, float depth, string gate)
        {
            if (!(c is Player)) return;
            if (gate == "spawned") { s_walkSpawned++; s_lastSpawnAt = p; }
            else
            {
                string k = gate.StartsWith("snow ") ? "snow too shallow" : gate;
                s_walkRefused[k] = s_walkRefused.TryGetValue(k, out int n) ? n + 1 : 1;
                // 0.2.1309: every refused step is visible at Debug (rate-limited 1 / 2 s) - the census line only prints with Snow diagnostics on
                if (c == Player.m_localPlayer && Time.unscaledTime >= s_refuseLogAt)
                {
                    s_refuseLogAt = Time.unscaledTime + 2f;
                    FiresLogger.LogDebug($"[SnowTrail] walk step refused at ({p.x:0.0},{p.z:0.0}): {gate} (snow {depth:0.00} m, on heightmap collider {c.IsOnHeightmap()}, voxel world {VoxelWorldHere(p)}) (0.2.1309)");
                }
            }
            s_lastGate = gate; s_lastDepth = depth;
            Flush(p);
        }

        // 0.2.1109 (rounds #6/#7: "spawned 0; refused: not Deep North 4" while walking 10 m at the hut = a SILENT exit): every exit
        // path of Patch_Trigger is counted (players only; ints, no allocation) and the line is flushed from the postfix itself, so
        // a walk that never reaches a gate still prints why.
        private static int s_calls, s_exNoWalkObj, s_exNoSlow, s_exNotOwner, s_exSpacing, s_exCheck, s_exThrew, s_gated;
        private static string s_lastGate = "none";
        private static float s_lastDepth, s_threwLogAt;
        private static void Flush(Vector3 p)
        {
            // 0.2.1110: the 1109 exit census is a diagnostic: Snow diagnostics only (default off); counters still reset every 10 s.
            if (!FatDiag.Snow) { if (Time.unscaledTime >= s_walkLogAt) { s_walkLogAt = Time.unscaledTime + 10f; s_walkSpawned = 0; s_walkRefused.Clear(); s_calls = s_exNoWalkObj = s_exNoSlow = s_exNotOwner = s_exSpacing = s_exCheck = s_exThrew = s_gated = 0; } return; }
            if (Time.unscaledTime < s_walkLogAt) return;
            s_walkLogAt = Time.unscaledTime + 10f;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in s_walkRefused) sb.Append(sb.Length > 0 ? ", " : "").Append(kv.Key).Append(' ').Append(kv.Value);
            FiresLogger.LogDebug($"[SnowTrail] walk: calls {s_calls}, exits: noWalkObj {s_exNoWalkObj}, noSlow {s_exNoSlow}, notOwner {s_exNotOwner}, spacing {s_exSpacing}, check {s_exCheck}, threw {s_exThrew}; gated {s_gated}: {(sb.Length > 0 ? sb.ToString() : "none")}; spawned {s_walkSpawned}{(s_walkSpawned > 0 ? $" (last at ({s_lastSpawnAt.x:0.0},{s_lastSpawnAt.z:0.0}))" : "")} (now at ({p.x:0.0},{p.z:0.0}), last gate {s_lastGate}, snow {s_lastDepth:0.00} m)");
            s_walkSpawned = 0; s_walkRefused.Clear();
            s_calls = s_exNoWalkObj = s_exNoSlow = s_exNotOwner = s_exSpacing = s_exCheck = s_exThrew = s_gated = 0;
        }

        private static float s_appliedLogAt;
        private static int s_appliedOps, s_appliedCells, s_walkOps, s_walkOpsVanillaSet, s_walkOpsOff;
        // 0.2.1105: always on, at most one line every 10 s: each Deep North cultivate op this peer applies, what it wrote and why.
        private static float s_opLineAt;   // 0.2.1418
        private static float s_vedrLineAt; private static int s_vedrStoodDown;   // 0.2.1419

        // 0.2.1419: the same test by position (FatTrailFast's prediction): no line, no count.
        internal static bool VedrOwnsAt(Vector3 pos)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return false;
            if (!VedrLooks.HasDigs && !VedrLooks.GroundSnowActive) return false;
            var hm = Heightmap.FindHeightmap(pos) ?? VoxelZoneStreamer.ZoneHeightmap(pos);
            if (hm == null) return false;
            var digs = VedrLooks.DigsSnowTrail(hm);   // 0.2.1420
            return digs.HasValue ? digs.Value : !FatApi.IsHeightmapHidden(hm);
        }

        // 0.2.1419: true = Vedr draws this chunk's ground snow, so FAT's trail writer stands down (one Debug line per 2 s with the count).
        private static bool StandDownForVedr(TerrainComp comp, Heightmap hm, Vector3 pos)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return false;   // dedicated server: unchanged
            var digs = VedrLooks.DigsSnowTrail(hm);   // 0.2.1420: Vedr's own per-step answer (Vedr 0.8.202+), else the 1419 split
            if (digs.HasValue) { if (!digs.Value) return false; }
            else
            {
                if (!VedrLooks.GroundSnowActive) return false;                                                   // Vedr's ground snow off: FAT is the only trail
                if (FatApi.IsHeightmapHidden(hm)) return false;                                                  // FAT draws this zone: FAT owns the trail
            }
            s_vedrStoodDown++;
            if (Time.unscaledTime >= s_vedrLineAt)
            {
                s_vedrLineAt = Time.unscaledTime + 2f;
                FiresLogger.LogDebug($"[SnowTrail] stood down: Vedr draws this chunk at ({pos.x:0.0},{pos.z:0.0}) (zone {ZoneSystem.GetZone(pos)}: vanilla Heightmap still drawn, Vedr ground snow live); {s_vedrStoodDown} op(s) left to Vedr so far");
            }
            return true;
        }

        private static void Applied(int n, Vector3 pos, TerrainOp.Settings m, string gates)
        {
            s_appliedOps++; s_appliedCells += n;
            if (!FatDiag.Snow) return;   // 0.2.1110: per-op detail is a diagnostic (Snow, default off); "G written" + [Shovel] stay always on
            if (Time.unscaledTime < s_appliedLogAt) return;
            s_appliedLogAt = Time.unscaledTime + 10f;
            string peer = ZNet.instance == null ? "?" : ZNet.instance.IsServer() ? (ZNet.instance.IsDedicated() ? "dedicated server" : "host") : "client";
            FiresLogger.LogDebug($"[SnowTrail] op applied on {peer}: G written {n} at ({pos.x:0.0},{pos.z:0.0}), op r {m.m_paintRadius:0.0} strength {m.m_paintStrength:0.00} ({gates}); {s_appliedOps} op(s), {s_appliedCells} cell(s) since the last line; walk ops {s_walkOps} (centre already G 1 by vanilla {s_walkOpsVanillaSet}, centre off this comp {s_walkOpsOff}); walk links: {Recent.Fallbacks} by the 2.5 m / 6 s fallback, {Recent.Dots} dot(s) (0.2.1155)");
            s_appliedOps = 0; s_appliedCells = 0; s_walkOps = s_walkOpsVanillaSet = s_walkOpsOff = 0; Recent.Fallbacks = Recent.Dots = 0;
        }

        // Trigger half: vanilla's own spacing and start height, Core's snow depth in place of the vanilla paint G.
        [HarmonyPatch(typeof(Character), "UpdateWalking")]
        private static class Patch_Trigger
        {
            internal const int CreatureStride = 5;
            internal const float PlayerStampMetres = 2.4f;
            internal static int SkippedCreature;
            private static void Postfix(Character __instance)
            {
                // 0.2.1163 (live 1162: "Trigger 10.82 (9700 calls, 0 acted; local player 0.09 ms/s, 485 calls)" = ~1.1 us per CREATURE
                // step doing nothing): a creature is checked on 1 physics step in CreatureStride (staggered by instance id). Its
                // spacing is 1 m and no creature covers that in CreatureStride steps (0.1 s at 50 Hz), so no trail stamp is lost.
                // 0.2.1182 ([ghost]: Trigger ~2.8 ms/s over ALL characters): creatures exit first when creature trails are off.
                if (!(__instance is Player) && (FiresVoxelConfig.CreatureSnowTrails == null || !FiresVoxelConfig.CreatureSnowTrails.Value)) return;
                if (!(__instance is Player) && (((__instance.GetInstanceID() + (int)(Time.fixedTime / Time.fixedDeltaTime + 0.5f)) & 0x7FFFFFFF) % CreatureStride) != 0) { SkippedCreature++; return; }
                long t0 = FatHooks.Start();
                bool acted = false;
                try
                {
                    var c = __instance;
                    // 0.2.1108 (round #6: 3 spawns at the hut's teleport point, then NOTHING over the 10 m walk - no spawn and no
                    // refusal, i.e. a silent early return): vanilla's own trail spawn (Character.cs:1172-1183) has no IsOnGround
                    // test; ours did, and a character wading through deep voxel snow is often not "on ground" (the snow is a
                    // visual raise over the voxel collider, and the deep-snow slow keeps it bobbing). Dropped, as vanilla; the
                    // early returns that stay are now counted in the walk line too.
                    bool pl = c is Player;   // 0.2.1109: the exit census counts players only (the walk line is about players)
                    if (!pl) { FatCreatureTrail.Tick(c); return; }   // 0.2.1129: creatures (vanilla gives only the Player an m_deepSnowWalkObj)
                    if (pl) s_calls++;
                    if (c.m_deepSnowWalkObj == null) { if (pl) { s_exNoWalkObj++; Flush(c.transform.position); } return; }
                    if (c.m_deepSnowSlowMax == 0f) { if (pl) { s_exNoSlow++; Flush(c.transform.position); } return; }
                    var nv = NView(c);
                    if (nv == null || !nv.IsValid() || !nv.IsOwner()) { if (pl) s_exNotOwner++; return; }   // remote players: no flush
                    Vector3 p = c.transform.position;
                    var last = s_last.GetOrCreateValue(c);
                    // 0.2.1163 (RPC census: "_TerrainCompiler x43 38.1 ms in 5 s", ~0.9 ms per received trail op): the player stamps
                    // every 2.4 m instead of vanilla's 1.5 m - 37 % fewer ops (each one a TerrainComp apply + Save + RPC on the owner);
                    // the owner links the segment up to Recent.LinkMetres, so the trench is drawn continuous exactly as before.
                    float dist = Mathf.Max(c.m_deepSnowWalkObjDist, PlayerStampMetres), dist2 = dist * dist;
                    if ((last.Pos - p).sqrMagnitude <= dist2) { if (pl) { s_exSpacing++; Flush(p); } return; }
                    if ((last.Check - p).sqrMagnitude <= dist2) { if (pl) { s_exCheck++; Flush(p); } return; }   // 0.2.1107: gates at most once per spacing step
                    last.Check = p;
                    acted = true;
                    if (pl) s_gated++;
                    // 0.2.1309 (Fire's SuperFlatTest8 log on 1307: "heightmap trail: 'Human' on Meadows at (1879.4,614.5)" while "G at the player
                    // (1880.9,612.6) 0.00" - on FAT voxel worlds the player's ground collider can still be a vanilla Heightmap, so
                    // IsOnHeightmap() was true on VOXEL ground and the 1299+ heightmap branch swallowed the voxel walk: no snow_walk op, no G,
                    // no prediction = no voxel trail). The heightmap branch now runs only off FAT voxel ground.
                    if (c.IsOnHeightmap() && !VoxelWorldHere(p))
                    {
                        // 0.2.1309: heightmap ground = vanilla's own snow_walk (Core 1061 base-mask snow, Core refill); FAT lays and stamps nothing
                        if (!s_hmStandDownSaid) { s_hmStandDownSaid = true; FiresLogger.LogDebug($"[SnowTrail] heightmap ground: vanilla snow_walk paints (FAT stands down: no Core stamp, no refusal, no FAT refill) at ({p.x:0.0},{p.z:0.0}) (0.2.1309)"); }
                        Walk(c, p, 0f, "heightmap ground: vanilla snow_walk (FAT stands down, 0.2.1309)");
                        return;
                    }
                    var wg = WorldGenerator.instance;
                    // 0.2.1105: the 2D (x,z) biome counts too (see the writer); each refusal is named in the always-on walk line
                    bool dn = wg != null && SnowTrailBiome(p) && !global::VerdantsAscent.Water.Core.IceFrost.Underwater(p);   // 0.2.1154: Deep North, or snow > 0.05 m in any biome; 0.2.1179: not on a bed under water
                    if (!VoxelGround(p)) { Walk(c, p, 0f, "not voxel ground"); return; }
                    if (!dn) { Walk(c, p, 0f, "no snow to cut (not Deep North, depth <= 0.05 m)"); return; }
                    float depth = VoxelSnowDepth.DepthMetresAt(p);
                    if (depth <= TrailMinDepthMetres) Walk(c, p, depth, $"snow {depth:0.00} m <= {TrailMinDepthMetres:0.00} m (trail threshold; the slow keeps {c.m_deepSnowSlowStartHeight:0.00} m)");
                    if (!s_conditionSaid)
                    {
                        s_conditionSaid = true;
                        FiresLogger.LogDebug($"[SnowTrail] deep-snow walk condition: vanilla Character.UpdateWalking false on voxel ground because it needs IsOnHeightmap() and the vanilla paint G x {c.m_deepSnowSlowMaxHeight:0.0} > {c.m_deepSnowSlowStartHeight:0.00} (G is 0 here; the snow is Core's field). FAT mirror: Core snow depth {depth:0.00} m > {c.m_deepSnowSlowStartHeight:0.00} m, every {c.m_deepSnowWalkObjDist:0.0} m");
                    }
                    if (depth <= TrailMinDepthMetres) return;   // 0.2.1154: trails at > 0.05 m (winter snow, Core 935 default 0.3 m)
                    // 0.2.1182: an already-cut stretch (cell + segment midpoint G >= 0.95 here) sends no networked op - the trench is there.
                    if (FatTrailNet.AlreadyCut(last.Pos, p)) { last.Pos = p; FatTrailNet.NoteStamp(false); return; }
                    last.Pos = p;
                    FatTrailNet.NoteStamp(true);
                    if (!FatTrailBatch.TryQueue(c.m_deepSnowWalkObj, p)) Object.Instantiate(c.m_deepSnowWalkObj, p, c.transform.rotation);   // batched when on (FatTrailBatch)
                    FatTrailFast.PredictWalk(c as Player, p, c.m_deepSnowWalkObj);   // 0.2.1157: drawn on this client now, synced after
                    s_spawned++;
                    Walk(c, p, depth, "spawned");
                    if (!s_walkNonDnSaid && c == Player.m_localPlayer && WorldGenerator.instance != null)
                    {
                        var wb = WorldGenerator.instance.GetBiome(p.x, p.z);
                        if (wb != Heightmap.Biome.DeepNorth)
                        {
                            s_walkNonDnSaid = true;
                            FiresLogger.LogDebug($"[SnowTrail] first walk trail spawned on {wb} (not Deep North) at ({p.x:0.0},{p.z:0.0}), snow {depth:0.00} m > {TrailMinDepthMetres:0.00} m - Meadows gate fix 0.2.1154: trails cut wherever there is snow, any biome");
                        }
                    }
                    if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_spawnLogAt)
                    {
                        s_spawnLogAt = Time.unscaledTime + 10f;
                        if (FatDiag.Snow) FiresLogger.LogDebug($"[SnowTrail] snow_walk spawned for '{c.m_name}' at ({p.x:0.0},{p.z:0.0}), snow {depth:0.00} m ({s_spawned} this session)");
                    }
                }
                catch (System.Exception ex)
                {
                    // 0.2.1109: Info, at most one line every 10 s, and counted in the walk line (was Debug only: invisible)
                    if (__instance is Player) s_exThrew++;
                    if (Time.unscaledTime >= s_threwLogAt) { s_threwLogAt = Time.unscaledTime + 10f; FiresLogger.LogDebug($"[SnowTrail] bail: trigger threw: {ex.GetType().Name}: {ex.Message}"); }
                }
                finally { FatHooks.Stop(FatHooks.Hook.Trigger, t0, acted, __instance == Player.m_localPlayer); }
            }
        }
    }
}
