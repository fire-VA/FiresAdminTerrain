using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // MESH-TRUTH seam auditor. Every prior seam claim was proven against the
    // DATA layer ("identical samples ⇒ identical geometry") — but the player
    // sees the MESHES, and something in the live pipeline keeps violating the
    // premises. This audits the actual rendered vertex buffers of adjacent
    // chunks along their shared face and reports, with numbers:
    //   TEAR  — coincident-face vertices whose positions differ between sides
    //   HOLE  — a vertex on one side with no counterpart on the other
    //   SHADE — positions match but normal/tangent/color differ (shading seam)
    // plus full forensic state for both chunks, so a visible stitch names its
    // own mechanism instead of being theorized about.
    //
    // On demand: va_voxel_seamaudit [radius]. Sentinel: every 8s around the
    // player (config 'Seam Audit Auto'), logging ONLY when something is found.
    public static class VoxelSeamAudit
    {
        private const float BAND = 1.05f;        // face band: duplicated cells produce verts within 1m of the plane
        private const float PAIR_DIST = 0.35f;   // in-plane pairing radius
        private const float TEAR_EPS = 0.005f;   // ≥5mm position delta = tear
        private const float SHADE_DOT = 0.995f;  // normal/tangent dot below this = shading seam

        private static readonly List<int> s_idxA = new List<int>(512);
        private static readonly List<int> s_idxB = new List<int>(512);

        public static void Register()
        {
            new Terminal.ConsoleCommand("va_voxel_seamaudit",
                "[va_voxel_seamaudit radius=64] Compare the ACTUAL RENDERED MESHES of adjacent chunks along every shared face: tears (position deltas), holes (one-sided verts), shading seams (normal/tangent/color mismatch), with per-chunk forensics. 'va_voxel_seamaudit auto' toggles the 8s background sentinel.",
                args =>
                {
                    if (args.Args.Length > 1 && args.Args[1].ToLowerInvariant() == "auto")
                    {
                        var e = FiresVoxelConfig.SeamAuditAuto;
                        if (e != null) { e.Value = !e.Value; args.Context.AddString($"seam audit sentinel: {(e.Value ? "ON (8s)" : "OFF")}"); }
                        return;
                    }
                    float radius = 64f;
                    var p = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    if (args.Args.Length > 1 && args.Args[1].ToLowerInvariant() == "vanilla")   // 0.2.1454
                    {
                        if (args.Args.Length > 2 && float.TryParse(args.Args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rv)) radius = Mathf.Clamp(rv, 16f, 256f);
                        args.Context.AddString(AuditVanillaBorders(p, radius));
                        return;
                    }
                    if (args.Args.Length > 1 && float.TryParse(args.Args[1], out float r)) radius = Mathf.Clamp(r, 16f, 256f);
                    int faces = AuditAround(p, radius, int.MaxValue, out int bad, out string worst);
                    args.Context.AddString($"seamaudit → {faces} face(s) audited, {bad} with findings." +
                        (bad > 0 ? $" Worst: {worst} (full reports in the log)" : " Meshes are vertex-identical at every audited face — a visible line now can only be material/lighting or a non-chunk surface (quilt/water)."));
                });
        }

        // Sentinel — called from the pipeline driver. Logs only on findings.
        private static float s_nextAuto;

        // 0.2.1454: [Vanilla Border Stitch] flipped -> the vanilla-border audit runs at once (the state before the re-mesh) and again
        // StitchAuditDelay s later (after it), around the local player, so a rig leg's log carries both numbers without a console step.
        private const float StitchAuditDelay = 3f;
        // 0.2.1457 ([lead] 11-17, stitch1454 leg: the edit chunk's remesh went through the deferred lane and landed after the 3 s audit, so its
        // two faces were never measured stitched): one more audit StitchAuditLate s after the flip, when every border remesh has landed.
        private const float StitchAuditLate = 20f;
        private static float s_stitchAuditAt = -1f, s_stitchAuditLateAt = -1f;
        internal static void OnStitchToggled()
        {
            var pl = Player.m_localPlayer;
            if (pl == null) return;
            AuditVanillaBorders(pl.transform.position, 64f, "at the flip, before the re-mesh");
            s_stitchAuditAt = Time.time + StitchAuditDelay;
            s_stitchAuditLateAt = Time.time + StitchAuditLate;   // 0.2.1457
        }

        // 0.2.1454 test-only: [Seam Test Carve] "x z radius" set live -> one va_voxel_carve-style disc (2 m deep, the flat-ruler default) at
        // the ground there. On a swap-on-edit world the brush marks the zone, so it converts (VoxelZoneStreamer.MarkZoneBrushed).
        internal static void RunTestCarve(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var w = spec.Split(new[] { ' ', ',', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 3 || !float.TryParse(w[0], System.Globalization.NumberStyles.Float, inv, out float x)
                || !float.TryParse(w[1], System.Globalization.NumberStyles.Float, inv, out float z)
                || !float.TryParse(w[2], System.Globalization.NumberStyles.Float, inv, out float r))
            { FiresLogger.LogWarning($"[FiresVoxel] Seam Test Carve '{spec}': want \"x z radius\"; nothing carved."); return; }
            if (Player.m_localPlayer == null || ZoneSystem.instance == null) { FiresLogger.LogWarning("[FiresVoxel] Seam Test Carve: no world / player yet; nothing carved."); return; }
            r = Mathf.Clamp(r, 0.5f, 8f);
            float y;
            if (!Heightmap.GetHeight(new Vector3(x, 0f, z), out y) && !VoxelWorld.TryFindSurfaceY(x, z, 60f, 200f, out y))
            { FiresLogger.LogWarning($"[FiresVoxel] Seam Test Carve at ({x:0.#}, {z:0.#}): no ground loaded there; nothing carved."); return; }
            try
            {
                string summary = VoxelWorld.ApplyBrush(new Vector3(x, y, z), r, 2f, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
                FiresLogger.LogWarning($"[FiresVoxel] Seam Test Carve at ({x:0.#}, {y:0.#}, {z:0.#}) r {r:0.#}: {summary} (zone {ZoneSystem.GetZone(new Vector3(x, 0f, z))}; test-only key)");
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] Seam Test Carve threw: {ex.Message}"); }
        }

        public static void AutoTick()
        {
            if (s_stitchAuditAt > 0f && Time.time >= s_stitchAuditAt)
            {
                s_stitchAuditAt = -1f;
                var p = Player.m_localPlayer;
                if (p != null) AuditVanillaBorders(p.transform.position, 64f, $"{StitchAuditDelay:0} s after the flip");
            }
            if (s_stitchAuditLateAt > 0f && Time.time >= s_stitchAuditLateAt)   // 0.2.1457
            {
                s_stitchAuditLateAt = -1f;
                var p = Player.m_localPlayer;
                if (p != null) AuditVanillaBorders(p.transform.position, 64f, $"{StitchAuditLate:0} s after the flip (every border remesh landed)");
            }
            if (FiresVoxelConfig.SeamAuditAuto == null || !FiresVoxelConfig.SeamAuditAuto.Value) return;
            if (Time.time < s_nextAuto) return;
            s_nextAuto = Time.time + 8f;
            var pl = Player.m_localPlayer;
            if (pl == null) return;
            AuditAround(pl.transform.position, 48f, 4, out _, out _);
        }

        private static readonly List<VoxelChunk> s_chunkScratch = new List<VoxelChunk>(64);

        private static int AuditAround(Vector3 center, float radius, int maxReports, out int badFaces, out string worstDesc)
        {
            badFaces = 0;
            worstDesc = "";
            float worstMag = 0f;
            int faces = 0, reported = 0;

            s_chunkScratch.Clear();
            foreach (var c in VoxelWorld.AllChunks)
            {
                if (c == null || c.PendingGenerate) continue;
                float dx = Mathf.Abs(c.WorldOrigin.x + 16f - center.x);
                float dz = Mathf.Abs(c.WorldOrigin.z + 16f - center.z);
                if (dx <= radius && dz <= radius) s_chunkScratch.Add(c);
            }

            foreach (var a in s_chunkScratch)
            {
                // +X, +Z and +Y faces (each shared face audited once, owned by
                // the lower-key side). Y-stack planes tear exactly like walls
                // — cave domes show the gap as a horizontal circle — and the
                // old "covered transitively" assumption was a blind spot.
                for (int axis = 0; axis < 3; axis++)
                {
                    var bKey = axis == 0 ? new Vector3Int(a.Key.x + 1, a.Key.y, a.Key.z)
                        : axis == 1 ? new Vector3Int(a.Key.x, a.Key.y, a.Key.z + 1)
                        : new Vector3Int(a.Key.x, a.Key.y + 1, a.Key.z);
                    if (!VoxelWorld.TryGetChunk(bKey, out var b) || b == null)
                    {
                        // The see-through class: A renders geometry at a face
                        // whose in-plane quads BELONG to a chunk that doesn't
                        // exist — nobody meshes the plane band. Report it.
                        // (X/Z only: a missing chunk ABOVE is the legitimate
                        // top of the column, not a defect.)
                        if (axis < 2 && a.MeshAll && reported < maxReports)
                        {
                            string miss = AuditMissingNeighbour(a, bKey, axis);
                            if (miss != null)
                            {
                                faces++; badFaces++; reported++;
                                FiresLogger.LogWarning(miss);
                            }
                        }
                        continue;
                    }
                    if (b.PendingGenerate) continue;
                    // Gated brush overlays legitimately mesh only edited cells.
                    if (!a.MeshAll || !b.MeshAll) continue;

                    faces++;
                    string report = AuditFace(a, b, axis, out float mag);
                    if (report != null)
                    {
                        badFaces++;
                        if (mag > worstMag) { worstMag = mag; worstDesc = report.Substring(0, Mathf.Min(120, report.Length)); }
                        if (reported < maxReports)
                        {
                            reported++;
                            FiresLogger.LogWarning(report);
                        }
                    }
                }
            }
            return faces;
        }

        // ---- 0.2.1454 (Fire 11-17, VA flat world tearing): the VOXEL / VANILLA border, which the face audit above never sees (it pairs
        // chunk with chunk). For every chunk face whose far side is a vanilla heightmap zone (another zone, not voxel territory), every
        // rendered vertex in the 1.05 m band is compared with that heightmap's surface at the same x, z (bilinear over its 1 m vertices):
        //   overhang = vertices PAST the border (inside the vanilla zone): coplanar (|dy| < 1 cm: the z-fight), above, below;
        //   on-edge  = vertices ON the border (|d| <= 1 cm; the stitch puts them there): their worst |dy| against vanilla's edge.
        // One Warning summary line + one per face with an overhang (first 6), so a rig leg's log carries the numbers.
        private static string AuditVanillaBorders(Vector3 center, float radius, string when = "on demand")
        {
            int faces = 0, facesOverhang = 0, over = 0, coplanar = 0, above = 0, below = 0, onEdge = 0, stitchedFaces = 0, reported = 0;
            float worstOver = 0f, worstEdge = 0f, sumOver = 0f;
            const float C = VoxelChunk.CELLS;
            var sb = new StringBuilder(256);
            foreach (var c in VoxelWorld.AllChunks)
            {
                if (c == null || c.PendingGenerate || !c.MeshAll || c.Kind != ChunkKind.Full) continue;
                var o = c.WorldOrigin;
                if (Mathf.Abs(o.x + 16f - center.x) > radius || Mathf.Abs(o.z + 16f - center.z) > radius) continue;
                if (!VoxelWorld.TryGetView(c.Key, out var view) || view == null) continue;
                var mesh = view.AuditMesh;
                if (mesh == null || view.VertexCount == 0) continue;
                var inside = ZoneSystem.GetZone(new Vector3(o.x + 16f, 0f, o.z + 16f));
                Vector3[] verts = null;
                for (int f = 0; f < 4; f++)
                {
                    bool xFace = f < 2, pos = (f & 1) == 0;
                    float plane = xFace ? (pos ? o.x + C : o.x) : (pos ? o.z + C : o.z);
                    float outward = pos ? 1f : -1f;
                    var mid = xFace ? new Vector3(plane + outward, 0f, o.z + 16f) : new Vector3(o.x + 16f, 0f, plane + outward);
                    var zone = ZoneSystem.GetZone(mid);
                    if (zone == inside || VoxelZoneStreamer.IsZoneKnown(zone)) continue;
                    var hm = Heightmap.FindHeightmap(mid);
                    if (hm == null) continue;
                    if (verts == null) verts = mesh.vertices;
                    faces++;
                    if ((c.AppliedStitchMask & (1 << f)) != 0) stitchedFaces++;
                    int fOver = 0, fCop = 0; float fWorst = 0f;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var v = verts[i];
                        float along = xFace ? v.z - o.z : v.x - o.x;
                        if (along < -0.6f || along > C + 0.6f) continue;
                        float dOut = ((xFace ? v.x : v.z) - plane) * outward;   // > 0 = past the border, into the vanilla zone
                        if (dOut < -1.05f || dOut > 1.05f) continue;
                        if (!VanillaSurfaceAt(hm, v.x, v.z, out float hv)) continue;
                        float dy = v.y - hv;
                        if (Mathf.Abs(dy) > 4f) continue;   // another layer (a cave roof / floor), not the border ground
                        if (dOut > 0.01f)
                        {
                            over++; fOver++; sumOver += Mathf.Abs(dy);
                            if (Mathf.Abs(dy) < 0.01f) { coplanar++; fCop++; } else if (dy > 0f) above++; else below++;
                            if (Mathf.Abs(dy) > worstOver) worstOver = Mathf.Abs(dy);
                            if (Mathf.Abs(dy) > fWorst) fWorst = Mathf.Abs(dy);
                        }
                        else if (dOut >= -0.01f)
                        {
                            onEdge++;
                            if (Mathf.Abs(dy) > worstEdge) worstEdge = Mathf.Abs(dy);
                        }
                    }
                    if (fOver > 0)
                    {
                        facesOverhang++;
                        if (reported++ < 6)
                            FiresLogger.LogWarning($"[FiresVoxel] SEAM AUDIT (vanilla border) chunk {c.Key} face {(xFace ? "X" : "Z")}{(pos ? "+" : "-")}={plane:F0} vs vanilla zone ({zone.x},{zone.y}): {fOver} vert(s) overhang the border, {fCop} coplanar within 1 cm (z-fight), worst |dy| {fWorst:F3} m; stitched={(c.AppliedStitchMask & (1 << f)) != 0}");
                    }
                }
            }
            sb.Append($"[FiresVoxel] SEAM AUDIT (vanilla border, {when}, at ({center.x:0}, {center.z:0}) r {radius:0} m, stitch {(FiresVoxelConfig.VanillaBorderStitch != null && FiresVoxelConfig.VanillaBorderStitch.Value ? "ON" : "OFF")}): {faces} voxel face(s) against a vanilla heightmap ({stitchedFaces} stitched); ");
            sb.Append($"{over} vertex(es) overhang the border into the vanilla zone ({coplanar} coplanar within 1 cm = z-fight, {above} above, {below} below; mean |dy| {(over > 0 ? sumOver / over : 0f):F3} m, worst {worstOver:F3} m) on {facesOverhang} face(s); ");
            sb.Append($"{onEdge} vertex(es) on the border, worst |dy| vs vanilla's edge {worstEdge:F3} m. Applies so far: {VoxelMeshPipeline.s_stitchApplies} stitched mesh(es), {VoxelMeshPipeline.s_stitchVertsApplied} vertex(es) moved.");
            FiresLogger.LogWarning(sb.ToString());
            return sb.ToString();
        }

        // The vanilla heightmap's surface at (x, z): bilinear over its 1 m vertices (vanilla's mesh is those vertices, triangulated).
        private static bool VanillaSurfaceAt(Heightmap hm, float x, float z, out float h)
        {
            h = 0f;
            try
            {
                var lp = new Vector3(x, 0f, z) - hm.transform.position;
                float s = hm.m_scale > 0f ? hm.m_scale : 1f;
                float fx = lp.x / s + hm.m_width / 2, fz = lp.z / s + hm.m_width / 2;
                int ix = Mathf.FloorToInt(fx), iz = Mathf.FloorToInt(fz);
                if (ix < 0 || iz < 0 || ix > hm.m_width || iz > hm.m_width) return false;
                int ix1 = Mathf.Min(ix + 1, hm.m_width), iz1 = Mathf.Min(iz + 1, hm.m_width);
                float tx = fx - ix, tz = fz - iz;
                float h00 = hm.GetHeight(ix, iz), h10 = hm.GetHeight(ix1, iz), h01 = hm.GetHeight(ix, iz1), h11 = hm.GetHeight(ix1, iz1);
                h = Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz) + hm.transform.position.y;
                return true;
            }
            catch (System.ArgumentOutOfRangeException) { return false; }
        }

        // A has geometry in the face band but the owning neighbour chunk does
        // not exist — the plane's quads are emitted by NOBODY (see-through).
        private static string AuditMissingNeighbour(VoxelChunk a, Vector3Int bKey, int axis)
        {
            if (!VoxelWorld.TryGetView(a.Key, out var va) || va == null) return null;
            var ma = va.AuditMesh;
            if (ma == null || va.VertexCount == 0) return null;
            float plane = axis == 0 ? (a.Key.x + 1) * 32f : (a.Key.z + 1) * 32f;
            var pa = ma.vertices;
            int band = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                float d = axis == 0 ? pa[i].x - plane : pa[i].z - plane;
                if (d > -BAND && d < 0.05f) band++;
            }
            if (band <= 8) return null;   // no meaningful surface at the face
            bool columnExists = false;
            foreach (var c in VoxelWorld.AllChunks)
                if (c != null && c.Key.x == bKey.x && c.Key.z == bKey.z) { columnExists = true; break; }
            return $"[FiresVoxel] SEAM AUDIT {(axis == 0 ? "X" : "Z")}={plane:F0}: chunk {a.Key} has {band} band vert(s) but NO NEIGHBOUR CHUNK at {bKey} (column exists={columnExists}) — the plane's quads are owned by a chunk that doesn't exist (see-through strip). A: v{a.Version} applied{a.AppliedVersion} meshAll={a.MeshAll} frontier={a.FrontierMask}";
        }

        // Returns null when the face is clean; otherwise the full report.
        private static string AuditFace(VoxelChunk a, VoxelChunk b, int axis, out float worst)
        {
            worst = 0f;
            if (!VoxelWorld.TryGetView(a.Key, out var va) || va == null) return null;
            if (!VoxelWorld.TryGetView(b.Key, out var vb) || vb == null) return null;
            var ma = va.AuditMesh; var mb = vb.AuditMesh;
            if (ma == null || mb == null) return null;
            if (va.VertexCount == 0 && vb.VertexCount == 0) return null;

            float plane = axis == 0 ? (a.Key.x + 1) * 32f
                : axis == 1 ? (a.Key.z + 1) * 32f
                : (a.Key.y + 1) * 32f;

            var pa = ma.vertices; var pb = mb.vertices;
            var na = ma.normals; var nb = mb.normals;
            var ta = ma.tangents; var tb = mb.tangents;
            var ca = ma.colors32; var cb = mb.colors32;

            s_idxA.Clear(); s_idxB.Clear();
            for (int i = 0; i < pa.Length; i++)
            {
                float d = axis == 0 ? pa[i].x - plane : axis == 1 ? pa[i].z - plane : pa[i].y - plane;
                if (d > -BAND && d < 0.05f) s_idxA.Add(i);
            }
            for (int i = 0; i < pb.Length; i++)
            {
                float d = axis == 0 ? pb[i].x - plane : axis == 1 ? pb[i].z - plane : pb[i].y - plane;
                if (d > -BAND && d < 0.05f) s_idxB.Add(i);
            }
            if (s_idxA.Count == 0 && s_idxB.Count == 0) return null;

            // One side has band geometry, the other has NONE at all → a hole
            // strip (the missing-quad class), unless the empty side is simply
            // a no-surface chunk.
            int tears = 0, holesA = 0, holesB = 0, shade = 0, exact = 0;
            float worstTear = 0f; Vector3 worstAt = Vector3.zero;
            float worstDot = 1f;

            // Pair A→B by nearest in-plane distance.
            var usedB = new bool[s_idxB.Count];
            for (int ia = 0; ia < s_idxA.Count; ia++)
            {
                var va3 = pa[s_idxA[ia]];
                int best = -1; float bestD = PAIR_DIST;
                for (int ib = 0; ib < s_idxB.Count; ib++)
                {
                    if (usedB[ib]) continue;
                    var vb3 = pb[s_idxB[ib]];
                    // in-plane distance (ignore the tear axis so displaced
                    // twins still pair and report as tears, not holes)
                    float du = axis == 0 ? va3.z - vb3.z : va3.x - vb3.x;
                    float dv = axis == 2 ? va3.z - vb3.z : va3.y - vb3.y;
                    float dPlane = axis == 0 ? va3.x - vb3.x : axis == 1 ? va3.z - vb3.z : va3.y - vb3.y;
                    float d = Mathf.Sqrt(du * du + dPlane * dPlane) + Mathf.Abs(dv) * 0.5f;
                    if (d < bestD) { bestD = d; best = ib; }
                }
                if (best < 0) { holesA++; continue; }
                usedB[best] = true;

                var vB = pb[s_idxB[best]];
                float delta = (va3 - vB).magnitude;
                if (delta > TEAR_EPS)
                {
                    tears++;
                    if (delta > worstTear) { worstTear = delta; worstAt = va3; }
                }
                else
                {
                    exact++;
                    int iA = s_idxA[ia], iB = s_idxB[best];
                    float nDot = (na.Length > iA && nb.Length > iB) ? Vector3.Dot(na[iA], nb[iB]) : 1f;
                    float tDot = (ta.Length > iA && tb.Length > iB)
                        ? Vector3.Dot(new Vector3(ta[iA].x, ta[iA].y, ta[iA].z), new Vector3(tb[iB].x, tb[iB].y, tb[iB].z)) : 1f;
                    bool colDiff = ca.Length > iA && cb.Length > iB
                        && (ca[iA].r != cb[iB].r || ca[iA].g != cb[iB].g || ca[iA].b != cb[iB].b || ca[iA].a != cb[iB].a);
                    if (nDot < SHADE_DOT || tDot < SHADE_DOT || colDiff)
                    {
                        shade++;
                        float dot = Mathf.Min(nDot, tDot);
                        if (dot < worstDot) worstDot = dot;
                    }
                }
            }
            for (int ib = 0; ib < s_idxB.Count; ib++) if (!usedB[ib]) holesB++;

            // Perfect symmetric one-vertex-per-cell faces pair 1:1; a few
            // unpaired verts are normal where the surface exits the band
            // diagonally. Only report when the counts are meaningful.
            bool holey = (holesA + holesB) > Mathf.Max(4, (s_idxA.Count + s_idxB.Count) / 8);
            if (tears == 0 && !holey && shade == 0) return null;

            worst = worstTear > 0 ? worstTear : (holey ? 0.5f : 1f - worstDot);

            var sb = new StringBuilder(512);
            sb.Append($"[FiresVoxel] SEAM AUDIT {(axis == 0 ? "X" : axis == 1 ? "Z" : "Y")}={plane:F0} between {a.Key} and {b.Key}: ");
            if (tears > 0) sb.Append($"{tears} TEAR(s) (worst {worstTear:F3}m at {worstAt}) ");
            if (holey) sb.Append($"{holesA}+{holesB} HOLE verts (of {s_idxA.Count}/{s_idxB.Count} band verts) ");
            if (shade > 0) sb.Append($"{shade} SHADING pair(s) (worst dot {worstDot:F4}) ");
            sb.Append($"| exact={exact}");
            sb.AppendLine();
            sb.Append($"  A {a.Key}: v{a.Version} applied{a.AppliedVersion} queued={a.Queued} meshAll={a.MeshAll} frontier={a.FrontierMask} brush={a.HasBrushEdits} verts={va.VertexCount} dataDiff={a.MirrorBordersNow(false)}");
            sb.AppendLine();
            sb.Append($"  B {b.Key}: v{b.Version} applied{b.AppliedVersion} queued={b.Queued} meshAll={b.MeshAll} frontier={b.FrontierMask} brush={b.HasBrushEdits} verts={vb.VertexCount} dataDiff={b.MirrorBordersNow(false)}");
            sb.AppendLine();
            sb.Append($"  materials: A#{va.MaterialInstanceId()} B#{vb.MaterialInstanceId()}{(va.MaterialInstanceId() != vb.MaterialInstanceId() ? "  << MISMATCH" : "")}");
            return sb.ToString();
        }
    }
}
