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
                    if (args.Args.Length > 1 && float.TryParse(args.Args[1], out float r)) radius = Mathf.Clamp(r, 16f, 256f);
                    var p = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    int faces = AuditAround(p, radius, int.MaxValue, out int bad, out string worst);
                    args.Context.AddString($"seamaudit → {faces} face(s) audited, {bad} with findings." +
                        (bad > 0 ? $" Worst: {worst} (full reports in the log)" : " Meshes are vertex-identical at every audited face — a visible line now can only be material/lighting or a non-chunk surface (quilt/water)."));
                });
        }

        // Sentinel — called from the pipeline driver. Logs only on findings.
        private static float s_nextAuto;
        public static void AutoTick()
        {
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
