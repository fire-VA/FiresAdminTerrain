using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Round-W-4 / W-4b — wraps the flat top-surface water mesh into a
    // continuous DROPLET shape, where the surface rolls under itself
    // at the boundary like a pool of water on a hard surface (surface
    // tension meniscus). NOT a slab/box — the bottom is INSET toward
    // the surface centroid so the wall connecting top edge to bottom
    // edge slopes inward, forming a smooth roll-over instead of a
    // sharp 90° corner.
    //
    //   * TOP face — the original surface mesh, unchanged.
    //   * BOTTOM face — a SHRUNKEN copy of the top: each vert is
    //     pulled toward the surface centroid by an amount proportional
    //     to its radial distance (centroid stays put; boundary verts
    //     pull in the most → bottom outline is smaller than top), AND
    //     offset DOWN per-vertex by the water depth. Where depth → 0
    //     at the shore, top and bottom converge to a thin lip.
    //   * SIDE WALLS — perimeter quads connecting top boundary to
    //     bottom boundary. Because the bottom is inset, these quads
    //     slope INWARD as they go down → smooth meniscus, not a sharp
    //     vertical cliff. Detected via boundary-edge counting.
    //
    // User spec (2026-05-27):
    //   "we basically want the mesh to look like a drop of water would
    //    or a pool of water on a hard surface where it rolls under
    //    itself and is a full mesh"
    //
    // Default behaviour: opt-in via Enabled flag (defaults true).
    // Toggle with `va_water_balloon_shell 0/1`.
    public static class WaterBalloonShell
    {
        // Master toggle. Console command `va_water_balloon_shell 0/1`
        // flips this — see WaterDebugCommands.
        // 0.2.752: OFF by default (Fire 10-05: placed water stood up as vertical sheets and square slabs - the shell's side
        // walls and bottom). `va_water_balloon_shell 1` still turns it on for a look.
        public static bool Enabled = false;

        // Floor on the per-vertex depth used for the shell. Even when
        // the surface job reports depth=0 at a vertex, we use this much
        // so the back face sits a thin sliver below the top, giving the
        // shell some thickness near the shore instead of fully collapsing
        // top onto bottom (which would leave the wall mesh missing those
        // verts entirely — visible holes at the shore).
        private const float MIN_SHELL_DEPTH = 0.05f;

        // Cap on per-vertex depth for the shell — without a cap, a very
        // deep dab (waterfall offset 0.5m + maxDepth 0.3m = 0.8m fine,
        // but a pond with 10m maxDepth would put the back face 10m below,
        // producing a huge submerged box that doesn't match the visible
        // water above). 3m matches the WaterMeshCombiner.DEFAULT_MAX_DEPTH
        // and reads as "river depth" without becoming a swimming pool.
        private const float MAX_SHELL_DEPTH = 3f;

        // ── Droplet roll-under tuning ────────────────────────────────────
        //
        // BOTTOM_INSET_RATIO controls how much the bottom mesh is pulled
        // toward the surface centroid relative to a vert's radial
        // distance. 0 = bottom is a pure vertical-drop mirror of the
        // top (slab/box look — what W-4 originally shipped). 0.3 = the
        // bottom is 30% smaller than the top → the perimeter wall slopes
        // inward at a noticeable angle as it goes down, giving the
        // meniscus / droplet "rolled-under" appearance the user asked
        // for. Higher = more dramatic roll; too high (>0.5) starts to
        // look like a teardrop hanging beneath the surface, which is
        // wrong for a pool sitting on a flat hard surface.
        private const float BOTTOM_INSET_RATIO = 0.3f;

        // Power applied to the radial-distance factor before insetting.
        // 1.0 = linear (boundary insets 30%, mid-radius insets 15%).
        // 2.0 = quadratic (boundary still 30%, mid-radius only 7.5% →
        // center stays "fat" longer, then rolls hard at the edge —
        // matches the surface-tension profile of a real drop).
        private const float INSET_POWER = 2.0f;

        // Result struct — caller assigns these to the Unity Mesh.
        public struct Output
        {
            public Vector3[] Vertices;
            public int[] Triangles;
            public Vector2[] UVs;
            public Vector3[] Normals;
            public Color[] Colors;
        }

        // Expand a top-surface mesh into a closed shell volume.
        //
        // Inputs come straight from the WaterAsyncMeshBuilder output —
        // the top mesh's verts/tris/UVs/normals/colors plus the per-
        // vertex water depths the job already computed (depth at vert i
        // = surfaceHeights[i] - terrainHeights[i] clamped ≥ 0).
        //
        // Returns the expanded data. Caller does mesh.Clear + SetVertices
        // + SetTriangles + ... as usual.
        public static Output Expand(
            Vector3[] topVerts,
            int[] topTris,
            Vector2[] topUVs,
            Vector3[] topNormals,
            Color[] topColors,
            float[] waterDepths)
        {
            int topVertCount = topVerts.Length;

            // 2026-05-27 round-49 — REWRITE per user correction.
            //
            // The water shader DOES handle closed 3D meshes — vanilla
            // water volumes spawned in the air render as proper
            // wrapped 3D shapes when frozen. Round-47/48's "back face
            // breaks the depth blend" theory was wrong; the bug was
            // that my topology wasn't actually a closed proper shape.
            //
            // What this builds now:
            //   TOP face — the original surface mesh, unchanged in
            //     position, UV, color, normal. Renders with the water
            //     shader as before.
            //   PERIMETER WALL — for each boundary edge of the top,
            //     drop two new verts to ground (terrain) Y under that
            //     XZ. The wall quad connects the top edge to the
            //     ground, closing the gap between water surface and
            //     the ground beneath. Ground IS the natural bottom of
            //     a body of water on a hard surface — we don't need
            //     to manufacture a separate bottom face.
            //   (NO bottom face — adding one was creating overlapping
            //     transparent layers that read as opaque grey.)
            //
            // Result: a proper 3D body of water with visible side
            // walls when you stand at the edge looking in, and the
            // top still renders as transparent blue via the standard
            // water shader. For waterfalls, the wall IS the back
            // face (terrain is the cliff), giving the tube look.
            // Diagnostic log demoted to verbose — round-50's `Debug.Log`
            // here fired every mesh regen which is multiple times per
            // second during a drag-paint stroke. Together with the
            // SWIM_DEBUG per-frame spam in WaterBoatSupport, it
            // collectively stalled the main thread.
            FiresLogger.LogVerbose($"[WaterBalloonShell] Shell invoked: topVerts={topVertCount} topTris={topTris.Length / 3} hasDepths={(waterDepths != null && waterDepths.Length > 0)}");

            // Shortcut: nothing to wrap.
            if (topVertCount == 0 || topTris.Length == 0)
            {
                return new Output
                {
                    Vertices = topVerts,
                    Triangles = topTris,
                    UVs = topUVs,
                    Normals = topNormals,
                    Colors = topColors,
                };
            }

            // ── Detect boundary edges ────────────────────────────────────
            var edgeCounts = new Dictionary<long, int>(topTris.Length);
            for (int t = 0; t < topTris.Length; t += 3)
            {
                int va = topTris[t], vb = topTris[t + 1], vc = topTris[t + 2];
                BumpEdge(edgeCounts, va, vb);
                BumpEdge(edgeCounts, vb, vc);
                BumpEdge(edgeCounts, vc, va);
            }
            var boundaryEdges = new List<(int a, int b)>();
            foreach (var kv in edgeCounts)
            {
                if (kv.Value == 1)
                {
                    long key = kv.Key;
                    int a = (int)(key >> 32);
                    int b = (int)(key & 0xFFFFFFFFL);
                    boundaryEdges.Add((a, b));
                }
            }

            // ── Build merged mesh: top + perimeter wall to ground ────────
            //
            // Wall verts: 2 NEW verts per boundary edge — placed at the
            // SAME XZ as the corresponding top vert, but Y = top.y -
            // waterDepth (the ground beneath the water). This drops the
            // wall down to meet terrain rather than mid-air. Where depth
            // is shallow (shore), the wall is short. Where depth is deep
            // (pool centre is far above ground), the wall is tall.
            //
            // The wall is rendered DOUBLE-SIDED (two triangle pairs per
            // edge with opposite winding) so it's visible from both
            // outside the water (player walks up to it) and inside
            // (player swims under it).
            int wallVertCount = boundaryEdges.Count * 2;
            int wallTriIndexCount = boundaryEdges.Count * 12; // 4 tris × 3 indices (double-sided quad)
            int totalVertCount = topVertCount + wallVertCount;
            int totalTriIndexCount = topTris.Length + wallTriIndexCount;

            var allVerts = new Vector3[totalVertCount];
            var allTris = new int[totalTriIndexCount];
            var allUVs = new Vector2[totalVertCount];
            var allNormals = new Vector3[totalVertCount];
            var allColors = new Color[totalVertCount];

            // Top: copy through unchanged.
            for (int i = 0; i < topVertCount; i++)
            {
                allVerts[i] = topVerts[i];
                allUVs[i] = i < (topUVs?.Length ?? 0) ? topUVs[i] : Vector2.zero;
                allNormals[i] = i < (topNormals?.Length ?? 0) ? topNormals[i] : Vector3.up;
                allColors[i] = i < (topColors?.Length ?? 0) ? topColors[i] : Color.white;
            }
            System.Array.Copy(topTris, 0, allTris, 0, topTris.Length);

            // 2026-05-28 round-52b — REVERT to round-49 minimal shell.
            //
            // Round-51 added a meniscus curl that pushed boundary verts
            // UP into the air whenever terrain was higher than the
            // water (because `Mathf.Max(groundY, ...)`), which gave
            // the broken floating-ring ribbon mesh the user saw.
            // Wiping that. This is now strictly: top unchanged, +
            // perimeter wall straight down to terrain Y. No
            // meniscus, no UV mutation on the top mesh.
            //
            // Wall UV: U follows the edge's dominant world axis, V
            // follows worldY (so the water shader's wave/foam texture
            // tiles vertically down the wall instead of smearing one
            // pixel). ONLY written to the new wall verts — top mesh
            // UVs are NEVER touched, so the "torn texture" ring of
            // round-50 stays gone.
            //
            // Wall depth is sampled from actual terrain (ZoneSystem
            // raycast) but CAPPED at MAX_WALL_DEPTH so floating-air
            // water gets a short visible side wall rather than a 50m
            // ribbon dangling to the ground below.
            const float WALL_UV_SCALE = 0.1f;
            const float MIN_WALL_DEPTH = 0.3f;
            const float MAX_WALL_DEPTH = 3f;

            int wallVertWrite = topVertCount;
            int wallTriWrite = topTris.Length;
            foreach (var (a, b) in boundaryEdges)
            {
                int wallAIdx = wallVertWrite++;
                int wallBIdx = wallVertWrite++;

                Vector3 topA = topVerts[a];
                Vector3 topB = topVerts[b];

                // Sample terrain Y under each boundary vert. Clamp so
                // wall is always BELOW top and never deeper than
                // MAX_WALL_DEPTH.
                float groundA = topA.y - MIN_WALL_DEPTH;
                float groundB = topB.y - MIN_WALL_DEPTH;
                if (ZoneSystem.instance != null)
                {
                    if (ZoneSystem.instance.GetGroundHeight(
                            new Vector3(topA.x, topA.y + 50f, topA.z), out float gA))
                    {
                        groundA = Mathf.Clamp(gA, topA.y - MAX_WALL_DEPTH, topA.y - MIN_WALL_DEPTH);
                    }
                    if (ZoneSystem.instance.GetGroundHeight(
                            new Vector3(topB.x, topB.y + 50f, topB.z), out float gB))
                    {
                        groundB = Mathf.Clamp(gB, topB.y - MAX_WALL_DEPTH, topB.y - MIN_WALL_DEPTH);
                    }
                }

                allVerts[wallAIdx] = new Vector3(topA.x, groundA, topA.z);
                allVerts[wallBIdx] = new Vector3(topB.x, groundB, topB.z);

                // 2026-05-30 round-54 — wall UV scheme. The water shader
                // samples its diffuse / wave / foam texture INTERNALLY
                // by world XZ, NOT by UV0; the UV is used for foam mask
                // and surface-detail patterns. Feeding it axis-projected
                // world-Y UVs (round-49/50/51) produced the pixelated
                // patchy texture the user keeps seeing — the shader's
                // foam mask landed in unexpected places.
                //
                // Inheriting the top boundary vert's UV verbatim gives
                // the wall the SAME foam-mask sample as the top edge.
                // Result: the wall renders as a continuous extension of
                // the top surface visually. The shader's world-XZ diffuse
                // sample is still constant down the wall (vertical
                // surface), which is a separate limitation of the shader
                // — but at least the texture isn't patchy / broken.
                allUVs[wallAIdx] = allUVs[a];
                allUVs[wallBIdx] = allUVs[b];

                allColors[wallAIdx] = allColors[a];
                allColors[wallBIdx] = allColors[b];

                float dxAB = topB.x - topA.x;
                float dzAB = topB.z - topA.z;
                Vector3 edgeDir = new Vector3(dxAB, 0, dzAB).normalized;
                Vector3 wallNormal = new Vector3(edgeDir.z, 0, -edgeDir.x);
                allNormals[wallAIdx] = wallNormal;
                allNormals[wallBIdx] = wallNormal;

                // Double-sided wall quad. Top edge = unchanged top
                // boundary verts; bottom edge = the new wall verts at
                // terrain Y.
                allTris[wallTriWrite++] = a;          allTris[wallTriWrite++] = wallAIdx;  allTris[wallTriWrite++] = b;
                allTris[wallTriWrite++] = b;          allTris[wallTriWrite++] = wallAIdx;  allTris[wallTriWrite++] = wallBIdx;
                allTris[wallTriWrite++] = a;          allTris[wallTriWrite++] = b;         allTris[wallTriWrite++] = wallAIdx;
                allTris[wallTriWrite++] = b;          allTris[wallTriWrite++] = wallBIdx;  allTris[wallTriWrite++] = wallAIdx;
            }

            return new Output
            {
                Vertices = allVerts,
                Triangles = allTris,
                UVs = allUVs,
                Normals = allNormals,
                Colors = allColors,
            };
        }

        // Round-47 LEGACY — closed-shell expansion (top + bottom +
        // walls). Disabled in favour of the round-48 edge-curl above
        // because the water shader couldn't render a closed volume.
        // Kept here behind ExpandLegacyClosedShell for reference and
        // to provide a path if we ever swap to a custom shader that
        // CAN render 3D water bodies.
        public static Output ExpandLegacyClosedShell(
            Vector3[] topVerts,
            int[] topTris,
            Vector2[] topUVs,
            Vector3[] topNormals,
            Color[] topColors,
            float[] waterDepths)
        {
            int topVertCount = topVerts.Length;

            // ── Step 1: detect boundary edges ────────────────────────────
            //
            // Walk every triangle's 3 edges, count occurrences. Edges
            // with count==1 are on the perimeter (only one triangle
            // uses them). count==2 → interior (shared between two
            // adjacent triangles). count>2 → degenerate (shouldn't
            // happen on a manifold mesh).
            int triCount = topTris.Length / 3;
            var edgeCounts = new Dictionary<long, int>(triCount * 2);
            for (int t = 0; t < topTris.Length; t += 3)
            {
                int va = topTris[t], vb = topTris[t + 1], vc = topTris[t + 2];
                BumpEdge(edgeCounts, va, vb);
                BumpEdge(edgeCounts, vb, vc);
                BumpEdge(edgeCounts, vc, va);
            }
            var boundaryEdges = new List<(int a, int b)>();
            foreach (var kv in edgeCounts)
            {
                if (kv.Value == 1)
                {
                    long key = kv.Key;
                    int a = (int)(key >> 32);
                    int b = (int)(key & 0xFFFFFFFFL);
                    boundaryEdges.Add((a, b));
                }
            }

            // ── Step 2: build the bottom face ────────────────────────────
            //
            // For each top vert, place a bottom vert at the same XZ but
            // offset DOWN by the per-vertex water depth (clamped to
            // [MIN, MAX]). For each top triangle, emit an inverted bottom
            // triangle (so backface culling keeps the bottom visible
            // from BELOW the water).
            int bottomVertStart = topVertCount;
            int wallVertStart = bottomVertStart + topVertCount;
            int wallVertCount = boundaryEdges.Count * 4; // 2 top + 2 bottom per edge
            int totalVertCount = topVertCount + topVertCount + wallVertCount;

            int wallTriIndexCount = boundaryEdges.Count * 6; // 2 tris per quad × 3 indices
            int totalTriIndexCount = topTris.Length + topTris.Length + wallTriIndexCount;

            var allVerts = new Vector3[totalVertCount];
            var allTris = new int[totalTriIndexCount];
            var allUVs = new Vector2[totalVertCount];
            var allNormals = new Vector3[totalVertCount];
            var allColors = new Color[totalVertCount];

            // Top: copy through unchanged.
            for (int i = 0; i < topVertCount; i++)
            {
                allVerts[i] = topVerts[i];
                allUVs[i] = i < (topUVs?.Length ?? 0) ? topUVs[i] : Vector2.zero;
                allNormals[i] = i < (topNormals?.Length ?? 0) ? topNormals[i] : Vector3.up;
                allColors[i] = i < (topColors?.Length ?? 0) ? topColors[i] : Color.white;
            }
            System.Array.Copy(topTris, 0, allTris, 0, topTris.Length);

            // ── Compute the surface centroid (XZ) and max radial extent.
            //
            // The bottom face will be a copy of the top, INSET toward
            // this centroid by an amount proportional to each vert's
            // radial distance. So a vert AT the centroid moves zero,
            // a boundary vert moves the full BOTTOM_INSET_RATIO ×
            // distance — producing the smooth roll-under shape.
            float centroidX = 0f, centroidZ = 0f;
            for (int i = 0; i < topVertCount; i++)
            {
                centroidX += topVerts[i].x;
                centroidZ += topVerts[i].z;
            }
            centroidX /= topVertCount;
            centroidZ /= topVertCount;
            // Max radial distance from centroid in XZ (for normalising
            // the inset falloff). Walk just the boundary verts since
            // they define the perimeter.
            float maxR = 0.001f;
            var boundaryVertSet = new HashSet<int>();
            foreach (var (a, b) in boundaryEdges)
            {
                boundaryVertSet.Add(a);
                boundaryVertSet.Add(b);
            }
            foreach (int idx in boundaryVertSet)
            {
                float dx = topVerts[idx].x - centroidX;
                float dz = topVerts[idx].z - centroidZ;
                float r = Mathf.Sqrt(dx * dx + dz * dz);
                if (r > maxR) maxR = r;
            }

            // Bottom: inset toward centroid + offset down. Inset amount
            // scales with the vert's radial distance from centroid,
            // raised to INSET_POWER so the center stays "fat" and the
            // boundary curls in sharply — matches a real water droplet's
            // surface tension profile.
            for (int i = 0; i < topVertCount; i++)
            {
                float depth = (waterDepths != null && i < waterDepths.Length)
                    ? Mathf.Clamp(waterDepths[i], MIN_SHELL_DEPTH, MAX_SHELL_DEPTH)
                    : MIN_SHELL_DEPTH;

                float dxc = topVerts[i].x - centroidX;
                float dzc = topVerts[i].z - centroidZ;
                float r = Mathf.Sqrt(dxc * dxc + dzc * dzc);
                float t = Mathf.Clamp01(r / maxR);
                // Inset fraction: BOTTOM_INSET_RATIO × t^power. At t=0
                // (centroid) → 0 inset. At t=1 (boundary) → full inset.
                float insetFrac = BOTTOM_INSET_RATIO * Mathf.Pow(t, INSET_POWER);
                float bottomX = topVerts[i].x - dxc * insetFrac;
                float bottomZ = topVerts[i].z - dzc * insetFrac;
                float bottomY = topVerts[i].y - depth;

                int b = bottomVertStart + i;
                allVerts[b] = new Vector3(bottomX, bottomY, bottomZ);
                // Reuse top UV — the shader's world-tiled diffuse
                // doesn't care about UV, and the foam/wave detail
                // staying consistent is better than recomputing.
                allUVs[b] = allUVs[i];
                // Flip normal — bottom faces DOWN.
                allNormals[b] = new Vector3(-allNormals[i].x, -allNormals[i].y, -allNormals[i].z);
                allColors[b] = allColors[i];
            }

            // Bottom triangles: same indices as top but with offset and
            // swapped (1, 2) for inverted winding (visible from below).
            int bottomTriWrite = topTris.Length;
            for (int t = 0; t < topTris.Length; t += 3)
            {
                allTris[bottomTriWrite + t]     = topTris[t]     + bottomVertStart;
                allTris[bottomTriWrite + t + 1] = topTris[t + 2] + bottomVertStart;
                allTris[bottomTriWrite + t + 2] = topTris[t + 1] + bottomVertStart;
            }

            // ── Step 3: build the side walls ─────────────────────────────
            //
            // Each boundary edge gets 4 NEW verts (2 top duplicates + 2
            // bottom duplicates) plus 2 triangles forming the quad. The
            // wall is single-sided — visible from OUTSIDE the volume —
            // which is correct: from inside the water you wouldn't see
            // the wall anyway (you'd be underwater looking up at the
            // top from below or out at the bottom from above).
            int wallTriWrite = topTris.Length + topTris.Length;
            int wallVertWrite = wallVertStart;
            foreach (var (a, b) in boundaryEdges)
            {
                int topAIdx = wallVertWrite++;
                int topBIdx = wallVertWrite++;
                int botAIdx = wallVertWrite++;
                int botBIdx = wallVertWrite++;

                Vector3 topA = topVerts[a];
                Vector3 topB = topVerts[b];
                Vector3 botA = allVerts[bottomVertStart + a];
                Vector3 botB = allVerts[bottomVertStart + b];

                allVerts[topAIdx] = topA;
                allVerts[topBIdx] = topB;
                allVerts[botAIdx] = botA;
                allVerts[botBIdx] = botB;

                // UV per-quad: U follows the dominant horizontal axis
                // of the edge (X if it runs mostly along X, Z otherwise)
                // so adjacent walls in the same direction stay
                // continuous; V follows worldY so the texture tiles
                // vertically as the eye travels down. Same convention
                // as the floating-island wall fix in round-43.
                float ux = topB.x - topA.x;
                float uz = topB.z - topA.z;
                float topAU, topBU, botAU, botBU;
                if (Mathf.Abs(ux) >= Mathf.Abs(uz))
                {
                    topAU = topA.x; topBU = topB.x;
                    botAU = botA.x; botBU = botB.x;
                }
                else
                {
                    topAU = topA.z; topBU = topB.z;
                    botAU = botA.z; botBU = botB.z;
                }
                const float WALL_UV_SCALE = 0.25f; // tiles every 4m
                allUVs[topAIdx] = new Vector2(topAU * WALL_UV_SCALE, topA.y * WALL_UV_SCALE);
                allUVs[topBIdx] = new Vector2(topBU * WALL_UV_SCALE, topB.y * WALL_UV_SCALE);
                allUVs[botAIdx] = new Vector2(botAU * WALL_UV_SCALE, botA.y * WALL_UV_SCALE);
                allUVs[botBIdx] = new Vector2(botBU * WALL_UV_SCALE, botB.y * WALL_UV_SCALE);

                // Normal: outward horizontal vector perpendicular to the
                // edge. RecalculateNormals would overwrite this anyway
                // for the wall verts; leave as zero and let the caller
                // skip RecalculateNormals (we're handing back authored
                // top + bottom normals).
                Vector3 edgeDir = new Vector3(ux, 0, uz).normalized;
                Vector3 wallNormal = new Vector3(edgeDir.z, 0, -edgeDir.x); // 90° CW in XZ
                allNormals[topAIdx] = wallNormal;
                allNormals[topBIdx] = wallNormal;
                allNormals[botAIdx] = wallNormal;
                allNormals[botBIdx] = wallNormal;

                // Color: take whatever the top edge verts had — water
                // shader uses vertex color for foam / depth blending.
                allColors[topAIdx] = allColors[a];
                allColors[topBIdx] = allColors[b];
                allColors[botAIdx] = allColors[a];
                allColors[botBIdx] = allColors[b];

                // Two triangles for the quad. CCW from outside the
                // volume (assuming the boundary was walked CCW when
                // generated — the surface mesh's edge winding sets
                // this for us).
                allTris[wallTriWrite++] = topAIdx;
                allTris[wallTriWrite++] = botAIdx;
                allTris[wallTriWrite++] = topBIdx;
                allTris[wallTriWrite++] = topBIdx;
                allTris[wallTriWrite++] = botAIdx;
                allTris[wallTriWrite++] = botBIdx;
            }

            return new Output
            {
                Vertices = allVerts,
                Triangles = allTris,
                UVs = allUVs,
                Normals = allNormals,
                Colors = allColors,
            };
        }

        private static void BumpEdge(Dictionary<long, int> dict, int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            long key = ((long)lo << 32) | (uint)hi;
            dict[key] = dict.TryGetValue(key, out var n) ? n + 1 : 1;
        }
    }
}
