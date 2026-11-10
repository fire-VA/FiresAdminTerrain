using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1097: the walkable surfaces of PLACED water, per 32 m tile, per 1 m column, from WaterStateField over PlacedWaterField:
    //   ice  (ice >= _FiresIceSolid): a slip collider (VanillaIce.OptIn: static_solid + vanilla SlipperyIce);
    //   crust (cooled lava, crust >= 0.5 below _FiresLavaHot): a walkable non-slip collider (static_solid, default friction).
    // Collider only: the drawn sheet shows ice / crust from its state texture. Those columns have no liquid level
    // (PlacedWaterField.TrySurface), so you walk them instead of swimming. Replaces 0.2.1095's whole-body seasonal ice collider
    // (FrozenWaterController on a frozen fresh-water combiner). Every peer (characters simulate where they are owned).
    internal static class PlacedWaterSolids
    {
        private const float Tile = 32f;
        private sealed class Pair { public GameObject Ice, Crust; }
        private static readonly Dictionary<long, Pair> s_tiles = new Dictionary<long, Pair>();
        private static readonly HashSet<long> s_dirty = new HashSet<long>();
        private static readonly Dictionary<long, Vector2> s_tileY = new Dictionary<long, Vector2>();
        private static bool s_allDirty = true;
        private static long s_seenSig = long.MinValue;
        private static float s_logAt;
        private static readonly List<Vector3> s_iv = new List<Vector3>(), s_cv = new List<Vector3>();
        private static readonly List<int> s_it = new List<int>(), s_ct = new List<int>();

        // 0.2.1315 (Fire 10-31, Meadows x307-309 z875-877: solid-looking ice with no collider; the 1189 ray probe went from gaps 0 to
        // gaps 3 at the spot while the drawn texture stayed 0.99): every collider rebuild says WHY its tile was dirty, the solid cells
        // before -> after, and the heat sources in range, so one walk names what took the collider away.
        private static readonly Dictionary<long, string> s_why = new Dictionary<long, string>();
        private static readonly Dictionary<long, int> s_lastSolid = new Dictionary<long, int>();
        private static string s_allWhy = "first build";
        internal static void MarkAllDirty(string why = "unspecified") { s_allDirty = true; s_allWhy = why; }

        // 0.2.1170: one tile (a broken hole or its refreeze), rebuilt by the next Tick.
        internal static void MarkDirtyAt(float x, float z, string why = "unspecified")
        {
            long k = ((long)Mathf.FloorToInt(x / Tile) << 32) ^ (uint)Mathf.FloorToInt(z / Tile);
            if (s_tiles.ContainsKey(k) || s_tileY.ContainsKey(k)) { s_dirty.Add(k); s_why[k] = why; }
        }

        private static void NoteRebuild(long key, int tx, int tz, int solidNow, WaterStateField.Grid grid)
        {
            int before = s_lastSolid.TryGetValue(key, out int b) ? b : -1;
            s_lastSolid[key] = solidNow;
            string why = s_why.TryGetValue(key, out var w) ? w : s_allWhy;
            s_why.Remove(key);
            if (!FiresLogger.DebugEnabled || before == solidNow) return;
            int heatIn = 0; string heat = "";
            var hs = VerdantsAscent.Terrain.Voxel.FatFireBullseye.HeatSnapshot(out int hn);
            float cx = tx * Tile + Tile * 0.5f, cz = tz * Tile + Tile * 0.5f;
            for (int i = 0; i < hn && hs != null && i < hs.Length; i++)
            {
                float dx = Mathf.Max(0f, Mathf.Abs(hs[i].x - cx) - Tile * 0.5f), dz = Mathf.Max(0f, Mathf.Abs(hs[i].z - cz) - Tile * 0.5f);
                if (dx * dx + dz * dz <= hs[i].w * hs[i].w) { heatIn++; if (heat.Length < 120) heat += $" ({hs[i].x:0.0},{hs[i].z:0.0}) r {hs[i].w:0.0}"; }
            }
            Vector3 pl = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            FiresLogger.LogDebug($"[WaterIce] collider rebuild: placed tile ({tx},{tz}) why: {why}; solid ice sub-cells {(before < 0 ? "none (first build)" : before.ToString())} -> {solidNow}; grid {(grid != null ? $"v{grid.Version} levels {grid.LoY:0.00}..{grid.HiY:0.00}" : "none (per-column fallback)")}; heat sources in range {heatIn}{heat}; player at ({pl.x:0.0},{pl.y:0.0},{pl.z:0.0}) (0.2.1315)");
        }

        // 0.2.1321 (Fire under the ice at 02:33:19, the "none (first build)" tiles): the visual (WaterStateField.Fill of a placed
        // sheet chunk) and the collider were built in different frames - the collider only by Tick, one tile per 0.25 s, so a fresh
        // freeze over N tiles left the last one drawn solid with no collider for up to N x 0.25 s. Now the fill calls this: when its
        // grid has MORE solid texels than this tile's collider was last built for (first build, or a rebuild that adds solid), the
        // collider is built here, in the same frame as the texture upload. Fewer / equal solid texels keep the queued path.
        private static readonly Dictionary<long, int> s_visSolid = new Dictionary<long, int>();
        private static bool s_sameFrameProof;
        private static float s_sameFrameLogAt;
        internal static void BuildWithVisual(float chunkX, float chunkZ, WaterStateField.Grid g)
        {
            if (g == null) return;
            float solid = WaterStateField.IceSolid;
            int solidTex = 0;
            for (int k = 0; k < g.Ice.Length; k++) if (g.Ice[k] >= solid && g.Lava[k] < WaterStateField.LavaHot) solidTex++;
            int tx = Mathf.FloorToInt((chunkX + 1f) / Tile), tz = Mathf.FloorToInt((chunkZ + 1f) / Tile);
            long key = ((long)tx << 32) ^ (uint)tz;
            int before = s_visSolid.TryGetValue(key, out int b) ? b : -1;
            bool hasCollider = s_tiles.TryGetValue(key, out var pair) && pair.Ice != null;
            if (solidTex == 0 || (hasCollider && before >= 0 && solidTex <= before)) { s_visSolid[key] = solidTex; return; }
            if (!s_tileY.ContainsKey(key) && g.LoY <= g.HiY) s_tileY[key] = new Vector2(g.LoY, g.HiY);
            bool first = !hasCollider || before < 0;
            s_why[key] = first ? "first build with the visual (same frame)" : $"visual added solid ({before} -> {solidTex} texels, same frame)";
            Build(key);
            s_dirty.Remove(key);
            s_visSolid[key] = solidTex;
            if (!s_sameFrameProof) { s_sameFrameProof = true; FiresLogger.LogInfo("[WaterIce] placed ice collider builds in the same frame as its visual when the visual adds solid ice (0.2.1321)"); }
            if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_sameFrameLogAt)
            {
                s_sameFrameLogAt = Time.unscaledTime + 1f;
                bool built = s_tiles.TryGetValue(key, out var p2) && p2.Ice != null;
                FiresLogger.LogDebug($"[WaterIce] {(first ? "first build" : "solid added")}: visual+collider same frame {Time.frameCount}: placed tile ({tx},{tz}) solid texels {(before < 0 ? "none" : before.ToString())} -> {solidTex}, collider {(built ? "built" : "NOT built (no ice tris)")} (0.2.1321)");
            }
        }

        // One tile per BuildEvery (SeasonalWaterFreeze.Tick, every frame).
        private static float s_next;
        internal static void Tick()
        {
            ProbeAroundPlayer();   // 0.2.1189 probe only (Debug, every 5 s)
            if (Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 0.25f;
            if (WaterSheets.Signature != s_seenSig) { s_seenSig = WaterSheets.Signature; s_allDirty = true; }
            if (s_allDirty)
            {
                s_allDirty = false;
                var want = new HashSet<long>();
                s_tileY.Clear();
                foreach (var d in WaterSheets.FreshDisks())
                {
                    if (!d.Water) continue;
                    for (int tx = Mathf.FloorToInt((d.X - d.R) / Tile); tx <= Mathf.FloorToInt((d.X + d.R) / Tile); tx++)
                        for (int tz = Mathf.FloorToInt((d.Z - d.R) / Tile); tz <= Mathf.FloorToInt((d.Z + d.R) / Tile); tz++)
                        {
                            long k = ((long)tx << 32) ^ (uint)tz;
                            want.Add(k);
                            if (!s_tileY.TryGetValue(k, out var yy)) s_tileY[k] = yy = new Vector2(d.Y, d.Y);
                            s_tileY[k] = new Vector2(Mathf.Min(yy.x, d.Y), Mathf.Max(yy.y, d.Y));
                        }
                }
                foreach (var k in new List<long>(s_tiles.Keys)) if (!want.Contains(k)) { Clear(k); s_dirty.Remove(k); }
                foreach (var k in want) s_dirty.Add(k);
            }
            if (s_dirty.Count == 0) return;
            long key = 0;
            foreach (var k in s_dirty) { key = k; break; }
            s_dirty.Remove(key);
            Build(key);
        }

        private static void Build(long key)
        {
            int tx = (int)(key >> 32), tz = (int)(uint)key;
            float ox = tx * Tile, oz = tz * Tile;
            float yLow = -10000f, yHigh = 10000f;
            if (s_tileY.TryGetValue(key, out var yr)) { yLow = yr.x - 4f; yHigh = yr.y + 1f; }
            s_iv.Clear(); s_it.Clear(); s_cv.Clear(); s_ct.Clear();
            float solid = WaterStateField.IceSolid, hot = WaterStateField.LavaHot;
            // 0.2.1192 (RenderDoc 10-28 + Fire: "a smooth mesh just like the water was just frozen solid"; "marching squares ... a smooth
            // advancing curved edge"): the walkable ice / crust is ONE continuous surface per tile, not a flat quad per column at that
            // column's own height (the 1189 probe's open seams at every step and shore). Columns -1..32 are sampled once; each lattice
            // corner takes the MEAN surface of its wet adjacent columns (the same smoothing the drawn sheet's corners get) and the mean
            // ice / crust value; each 1 m cell is clipped by marching squares at the solid threshold with the edge point interpolated
            // along the cell edge, so the freeze front is a curve. A cell with dry corners (the shore) extends its wet corners' height
            // flat under the bank (the skirt the terrain buries), so there is no gap where the ice meets the ground.
            const int N = 34;   // columns -1..32
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    int c = i + N * j;
                    float x = ox + i - 1, z = oz + j - 1;
                    s_has[c] = false; s_iceF[c] = 0f; s_crustF[c] = 0f;
                    if (!PlacedWaterField.TrySurface(x + 0.5f, z + 0.5f, yLow, yHigh, PlacedWaterField.Consumer.Sheet, out float s, out _)) continue;
                    var st = WaterStateField.Sample(x + 0.5f, z + 0.5f, s);
                    s_has[c] = true; s_surf[c] = s;
                    bool hotLava = st.Lava >= hot;
                    if (!hotLava && st.Crust >= 0.5f && st.Crust < 0.85f && i >= 1 && i <= 32 && j >= 1 && j <= 32) WaterSteam.Note(x, s, z);
                    s_crustF[c] = hotLava ? 0f : st.Crust;
                    s_iceF[c] = hotLava || st.Crust >= 0.5f ? 0f : st.Ice;
                }
            const int K = 33;   // lattice corners 0..32
            for (int cj = 0; cj < K; cj++)
                for (int ci = 0; ci < K; ci++)
                {
                    float hs = 0f, fi = 0f, fc = 0f; int n = 0;
                    for (int dj = 0; dj < 2; dj++)
                        for (int di = 0; di < 2; di++)
                        {
                            int c = (ci + di) + N * (cj + dj);   // columns ci-1+di+1 = ci+di in the N grid
                            if (!s_has[c]) continue;
                            hs += s_surf[c]; fi += s_iceF[c]; fc += s_crustF[c]; n++;
                        }
                    int k = ci + K * cj;
                    s_cornH[k] = n > 0 ? hs / n : float.NaN;
                    s_cornI[k] = n > 0 ? fi / n : 0f;
                    s_cornC[k] = n > 0 ? fc / n : 0f;
                }
            int segI = 0, segC = 0, skirt = 0;
            // 0.2.1313 one source: the placed sheet's column grid (the state texture's values, each column at its own level) cuts the ice.
            WaterStateField.TryGetGrid(WaterStateField.GridSrc.Placed, ox, oz, out var grid);
            int cVis = 0, cCol = 0, cVisOnly = 0, cColOnly = 0; string cFirst = null;
            for (int j = 0; j < (int)Tile; j++)
                for (int i = 0; i < (int)Tile; i++)
                {
                    int k00 = i + K * j, k10 = k00 + 1, k01 = k00 + K, k11 = k01 + 1;
                    float h00 = s_cornH[k00], h10 = s_cornH[k10], h01 = s_cornH[k01], h11 = s_cornH[k11];
                    int valid = (float.IsNaN(h00) ? 0 : 1) + (float.IsNaN(h10) ? 0 : 1) + (float.IsNaN(h01) ? 0 : 1) + (float.IsNaN(h11) ? 0 : 1);
                    if (valid == 0) continue;
                    if (valid < 4)
                    {
                        float m = ((float.IsNaN(h00) ? 0f : h00) + (float.IsNaN(h10) ? 0f : h10) + (float.IsNaN(h01) ? 0f : h01) + (float.IsNaN(h11) ? 0f : h11)) / valid;
                        if (float.IsNaN(h00)) h00 = m; if (float.IsNaN(h10)) h10 = m; if (float.IsNaN(h01)) h01 = m; if (float.IsNaN(h11)) h11 = m;
                        skirt++;
                    }
                    float x = ox + i, z = oz + j;
                    if (grid != null) IceGridClip.ClipCell(grid, i, j, x, z, h00, h10, h01, h11, solid, s_iv, s_it, null, null, ref cVis, ref cCol, ref cVisOnly, ref cColOnly, ref cFirst);
                    else segI += ClipCell(x, z, h00, h10, h01, h11, s_cornI[k00], s_cornI[k10], s_cornI[k01], s_cornI[k11], solid, s_iv, s_it);
                    segC += ClipCell(x, z, h00, h10, h01, h11, s_cornC[k00], s_cornC[k10], s_cornC[k01], s_cornC[k11], 0.5f, s_cv, s_ct);
                }
            if (grid != null) WaterStateField.NoteCells("placed", grid, cVis, cCol, cVisOnly, cColOnly, cFirst);
            NoteRebuild(key, tx, tz, grid != null ? cCol : s_it.Count / 3, grid);   // 0.2.1315 data
            if (FiresLogger.DebugEnabled && s_it.Count > 0 && Time.unscaledTime >= s_contourLogAt)
            {
                s_contourLogAt = Time.unscaledTime + 5f;
                FiresLogger.LogDebug($"[WaterIce] ice collider: tile ({tx},{tz}), one continuous surface ({s_it.Count / 3} tri(s)), contour segments {segI} (marching squares at ice {solid:0.00}, interpolated), crust contour {segC}, skirt cells {skirt} (shore corners extended flat under the bank) (0.2.1192)");
            }
            if (FiresLogger.DebugEnabled && s_it.Count > 0) ProbeTile(tx, tz, ox, oz, yLow, yHigh, solid, hot);   // 0.2.1189: PROBE ONLY
            if (!s_tiles.TryGetValue(key, out var p)) s_tiles[key] = p = new Pair();
            p.Ice = Apply(p.Ice, s_iv, s_it, $"fires_placed_ice_{tx}_{tz}", true);
            p.Crust = Apply(p.Crust, s_cv, s_ct, $"fires_placed_crust_{tx}_{tz}", false);
            if ((s_it.Count > 0 || s_ct.Count > 0) && Time.unscaledTime >= s_logAt)
            {
                s_logAt = Time.unscaledTime + 5f;
                FiresLogger.LogDebug($"[WaterState] placed tile ({tx},{tz}): ice {s_it.Count / 3} tri(s) slippery via {VanillaIce.Mechanism}, crust {s_ct.Count / 3} walkable tri(s) (0.2.1192: one continuous clipped surface)");
            }
        }

        // ── 0.2.1189 PROBE ONLY (Fire: "the companion fell through where the mesh got messed up around the shores and ... stepping").
        // No collider/mesh logic changes. (1) Per built tile: the per-column ice quads are flat at each column's own surface s, so
        // compare each iced column with its 4 neighbours: a different s (> 5 cm) = a vertical step with NO side face between the two
        // quads; an iced column next to a column with no surface (shore) or a non-solid column = an open edge.
        private static float s_probeLogAt;
        private static void ProbeTile(int tx, int tz, float ox, float oz, float yLow, float yHigh, float solid, float hot)
        {
            if (Time.unscaledTime < s_probeLogAt) return;
            s_probeLogAt = Time.unscaledTime + 5f;
            const int N = 34;   // the tile + one column of neighbours each side
            var s = new float[N * N]; var ice = new bool[N * N]; var has = new bool[N * N];
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    float x = ox + i - 1, z = oz + j - 1;
                    if (!PlacedWaterField.TrySurface(x + 0.5f, z + 0.5f, yLow, yHigh, PlacedWaterField.Consumer.Sheet, out float sy, out _)) continue;
                    has[i + N * j] = true; s[i + N * j] = sy;
                    var st = WaterStateField.Sample(x + 0.5f, z + 0.5f, sy);
                    ice[i + N * j] = st.Lava < hot && st.Ice >= solid;
                }
            int cols = 0, steps = 0, shore = 0, openWater = 0; float maxStep = 0f, mx = 0f, mz = 0f;
            for (int j = 1; j < N - 1; j++)
                for (int i = 1; i < N - 1; i++)
                {
                    int c = i + N * j;
                    if (!ice[c]) continue;
                    cols++;
                    for (int d = 0; d < 4; d++)
                    {
                        int n = d == 0 ? c + 1 : d == 1 ? c - 1 : d == 2 ? c + N : c - N;
                        if (!has[n]) { shore++; continue; }
                        if (!ice[n]) { openWater++; continue; }
                        float step = Mathf.Abs(s[c] - s[n]);
                        if (step > 0.05f) { steps++; if (step > maxStep) { maxStep = step; mx = ox + i - 1; mz = oz + j - 1; } }
                    }
                }
            FiresLogger.LogDebug($"[WaterIce] collider probe (placed tile {tx},{tz}): {cols} iced column(s); neighbour edges: surface steps > 5 cm {steps} (max {maxStep:0.00} m at ({mx:0},{mz:0})), open shore edges (no surface next door) {shore}, edges onto non-solid water {openWater}; quads are flat per column with no side faces, so every step edge and shore edge is an open seam (0.2.1189 probe, no logic change)");
        }

        // (2) Around the local player every 5 s while on/near placed ice: raycast down at the 7x7 column centres and compare the collider
        // top hit with the column's water surface and with the neighbours' hits (probe only).
        private static float s_rayAt;
        internal static void ProbeAroundPlayer()
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_rayAt) return;
            s_rayAt = Time.unscaledTime + 5f;
            var pl = Player.m_localPlayer;
            if (pl == null) return;
            Vector3 p = pl.transform.position;
            const int R = 3, N = 2 * R + 1;
            var top = new float[N * N]; var iced = new bool[N * N];
            int cols = 0, gaps = 0, steps = 0; float maxStep = 0f; Vector2 at = default;
            float solid = WaterStateField.IceSolid;
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    float x = Mathf.Floor(p.x) + i - R + 0.5f, z = Mathf.Floor(p.z) + j - R + 0.5f;
                    top[i + N * j] = float.NaN;
                    if (!PlacedWaterField.TrySurface(x, z, p.y - 6f, p.y + 3f, PlacedWaterField.Consumer.Sheet, out float sy, out _)) continue;
                    if (WaterStateField.Sample(x, z, sy).Ice < solid) continue;
                    iced[i + N * j] = true; cols++;
                    if (Physics.Raycast(new Vector3(x, sy + 1.5f, z), Vector3.down, out var hit, 2.5f, ~0, QueryTriggerInteraction.Ignore)
                        && hit.collider != null && (hit.collider.name.StartsWith("fires_placed_ice") || hit.collider.name == "IceShell"))
                        top[i + N * j] = hit.point.y;
                    else gaps++;
                }
            for (int j = 0; j < N; j++)
                for (int i = 0; i + 1 < N; i++)
                {
                    float a = top[i + N * j], b = top[i + 1 + N * j];
                    if (float.IsNaN(a) || float.IsNaN(b)) continue;
                    float d = Mathf.Abs(a - b); if (d > 0.05f) { steps++; if (d > maxStep) { maxStep = d; at = new Vector2(Mathf.Floor(p.x) + i - R, Mathf.Floor(p.z) + j - R); } }
                }
            if (cols == 0) return;
            FiresLogger.LogDebug($"[WaterIce] collider probe: {cols} columns, steps > 5 cm {steps}, gaps {gaps}, max step {maxStep:0.00} m at ({at.x:0},{at.y:0}) (player at ({p.x:0.0},{p.y:0.0},{p.z:0.0}); 0.2.1189 probe, no logic change)");
            BorderProbe(p);
            PlacedWaterSheets.MeshSeamProbe(p);   // 0.2.1197: the render mesh (BorderProbe reads the collider tiles)
        }

        // 0.2.1195 PROBE ONLY (Fire 10-28: "ice meshes still break a bit in some spots, we need them to stitch together and smooth on
        // slopes"; live 1193 collider probe "steps > 5 cm 10, gaps 3, max step 0.25 m at (310,873)"). Each tile samples its columns
        // (its -1..32 ring included) inside ITS OWN height window (s_tileY: its disks' min - 4 .. max + 1), so the two tiles meeting at
        // a border can resolve the same border column to a different surface - or one finds it and the other does not - and their
        // shared corners then disagree: a step or a gap at the tile seam. This compares, for the tile under the player and each
        // neighbour, the two border rows each tile reads, through BOTH windows. No logic change.
        private static void BorderProbe(Vector3 p)
        {
            int tx = Mathf.FloorToInt(p.x / Tile), tz = Mathf.FloorToInt(p.z / Tile);
            long a = ((long)tx << 32) ^ (uint)tz;
            if (!s_tileY.TryGetValue(a, out var ya)) return;
            var sb = new System.Text.StringBuilder();
            int totalMiss = 0, totalDiff = 0; float worst = 0f; Vector2 worstAt = default;
            for (int d = 0; d < 4; d++)
            {
                int nx = tx + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = tz + (d == 2 ? 1 : d == 3 ? -1 : 0);
                long b = ((long)nx << 32) ^ (uint)nz;
                if (!s_tileY.TryGetValue(b, out var yb)) continue;
                int miss = 0, diff = 0, both = 0;
                for (int t = -1; t <= (int)Tile; t++)
                    for (int side = 0; side < 2; side++)   // the last column of this tile and the first of the neighbour
                    {
                        float x, z;
                        if (d < 2) { x = (d == 0 ? (tx + 1) * Tile : tx * Tile) + (side == 0 ? -0.5f : 0.5f); z = tz * Tile + t + 0.5f; }
                        else { z = (d == 2 ? (tz + 1) * Tile : tz * Tile) + (side == 0 ? -0.5f : 0.5f); x = tx * Tile + t + 0.5f; }
                        bool ha = PlacedWaterField.TrySurface(x, z, ya.x - 4f, ya.y + 1f, PlacedWaterField.Consumer.Sheet, out float sa, out _);
                        bool hb = PlacedWaterField.TrySurface(x, z, yb.x - 4f, yb.y + 1f, PlacedWaterField.Consumer.Sheet, out float sbv, out _);
                        if (!ha && !hb) continue;
                        if (ha != hb) { miss++; continue; }
                        both++;
                        float dd = Mathf.Abs(sa - sbv);
                        if (dd > 0.05f) { diff++; if (dd > worst) { worst = dd; worstAt = new Vector2(x, z); } }
                    }
                totalMiss += miss; totalDiff += diff;
                sb.Append($" tile ({nx},{nz}) window {yb.x - 4f:0.0}..{yb.y + 1f:0.0}: border columns read by both {both}, found by one window only {miss}, different surface {diff};");
            }
            if (sb.Length == 0) return;
            FiresLogger.LogDebug($"[WaterIce] tile border probe at ({p.x:0},{p.z:0}): tile ({tx},{tz}) window {ya.x - 4f:0.0}..{ya.y + 1f:0.0};{sb} totals: one-window-only {totalMiss}, different surface {totalDiff} (worst {worst:0.00} m at ({worstAt.x:0.0},{worstAt.y:0.0})) - non-zero = the two tiles build that seam from different water (0.2.1195 probe, no logic change)");
        }

        private static readonly bool[] s_has = new bool[34 * 34];
        private static readonly float[] s_surf = new float[34 * 34], s_iceF = new float[34 * 34], s_crustF = new float[34 * 34];
        private static readonly float[] s_cornH = new float[33 * 33], s_cornI = new float[33 * 33], s_cornC = new float[33 * 33];
        private static readonly Vector3[] s_poly = new Vector3[8];
        private static readonly Vector3[] s_quad = new Vector3[4];
        private static readonly float[] s_qv = new float[4];
        private static float s_contourLogAt;

        // 0.2.1192: one 1 m cell of the smoothed surface, clipped to field >= iso (marching squares by polygon clipping: each
        // crossing point is interpolated along its cell edge, height included). Returns 1 when the cell is cut by the contour.
        // 0.2.1313: internal + cell size (IceGridClip clips the generated and placed ice on 0.5 m sub-cells of the one column grid).
        internal static int ClipCell(float x, float z, float h00, float h10, float h01, float h11,
                                    float f00, float f10, float f01, float f11, float iso, List<Vector3> v, List<int> t, float size = 1f)
        {
            bool i00 = f00 >= iso, i10 = f10 >= iso, i01 = f01 >= iso, i11 = f11 >= iso;
            if (!i00 && !i10 && !i01 && !i11) return 0;
            // corners counter-clockwise seen from above: (x,z) (x+s,z) (x+s,z+s) (x,z+s)
            s_quad[0] = new Vector3(x, h00, z); s_quad[1] = new Vector3(x + size, h10, z);
            s_quad[2] = new Vector3(x + size, h11, z + size); s_quad[3] = new Vector3(x, h01, z + size);
            s_qv[0] = f00; s_qv[1] = f10; s_qv[2] = f11; s_qv[3] = f01;
            int n = 0;
            for (int a = 0; a < 4; a++)
            {
                int b = (a + 1) & 3;
                bool ina = s_qv[a] >= iso, inb = s_qv[b] >= iso;
                if (ina) s_poly[n++] = s_quad[a];
                if (ina != inb)
                {
                    float tt = (iso - s_qv[a]) / (s_qv[b] - s_qv[a]);
                    s_poly[n++] = Vector3.Lerp(s_quad[a], s_quad[b], Mathf.Clamp01(tt));
                }
            }
            if (n < 3) return 0;
            int baseIdx = v.Count;
            for (int p = 0; p < n; p++) v.Add(s_poly[p]);
            for (int p = 1; p + 1 < n; p++) { t.Add(baseIdx); t.Add(baseIdx + p + 1); t.Add(baseIdx + p); }   // same facing as the old quads
            return (i00 && i10 && i01 && i11) ? 0 : 1;
        }

        private static GameObject Apply(GameObject go, List<Vector3> v, List<int> t, string name, bool slip)
        {
            if (t.Count == 0)
            {
                if (go != null) { var old = go.GetComponent<MeshCollider>(); if (old != null && old.sharedMesh != null) Object.Destroy(old.sharedMesh); Object.Destroy(go); }
                return null;
            }
            if (go == null)
            {
                go = new GameObject(name);
                Object.DontDestroyOnLoad(go);
                go.transform.position = Vector3.zero;
                var c = go.AddComponent<MeshCollider>();
                if (slip) VanillaIce.OptIn(c);
                else { int solid = LayerMask.NameToLayer("static_solid"); if (solid >= 0) go.layer = solid; }
            }
            var mc = go.GetComponent<MeshCollider>();
            var prev = mc.sharedMesh;
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mc.sharedMesh = mesh;
            if (prev != null) Object.Destroy(prev);
            return go;
        }

        private static void Clear(long key)
        {
            if (!s_tiles.TryGetValue(key, out var p)) return;
            Apply(p.Ice, s_iv, new List<int>(), "", true);
            Apply(p.Crust, s_cv, new List<int>(), "", false);
            s_tiles.Remove(key);
        }
    }
}
