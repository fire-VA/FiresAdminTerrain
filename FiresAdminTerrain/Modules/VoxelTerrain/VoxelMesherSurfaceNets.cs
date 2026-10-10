using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Pooled per-job buffers — density snapshot in, mesh arrays out. One set
    // per in-flight worker job, recycled through the pipeline's pool.
    public sealed class MeshBuffers
    {
        public readonly sbyte[] Density = new sbyte[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
        public readonly sbyte[] Seed = new sbyte[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
        public readonly Color32[] Biome = new Color32[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];   // per column
        public readonly int[] CellIndex = new int[34 * 34 * 34];   // cells [-1..32]³ (+plane row for orphan faces)
        public readonly byte[] EditedMemo = new byte[34 * 34 * 34]; // 0 unknown, 1 edited, 2 clean
        public bool MeshAll;   // streamed zone-wrap chunk: mesh the whole surface (no edited gate)
        public bool CollidersOnly; // headless dedi: only Verts+Tris are filled (no renderer consumes normals/tangents/uv/colors)
        public int MeshYLo = -1, MeshYHi = 33;   // crossing Y-band (uniform layers skipped); full range by default
        public bool EmitLips;  // legacy config: lips on ALL 64m zone-edge faces
        public bool FrontierLips; // config: lips on frontier-mask faces (default OFF — bright ribbon artifacts)
        public byte FrontierMask; // faces with NO covering neighbour (1=+X,2=−X,4=+Z,8=−Z)
        public byte OrphanMask;   // +faces whose owner chunk DOESN'T EXIST (1=+X,4=+Z): WE mesh the plane
        public int KeyX, KeyZ; // chunk key (zone-edge face detection for border lips)
        public readonly List<Vector3> Verts = new List<Vector3>(4096);
        public readonly List<Vector3> Normals = new List<Vector3>(4096);
        public readonly List<Vector4> Tangents = new List<Vector4>(4096);
        public readonly List<Vector2> Uvs = new List<Vector2>(4096);
        public readonly List<Vector2> Uv1 = new List<Vector2>(4096);   // .x = per-vertex inland waterline (from WetLine) → shader fWL; 0 = vanilla
        public readonly List<Color32> Colors = new List<Color32>(4096);
        public readonly List<int> Tris = new List<int>(16384);
        // Carve+fill water: the per-column reconciled level rides the SAME
        // snapshot as the density, so the sheet is built in the same apply as
        // the terrain mesh — the two cannot exist without each other.
        public readonly float[] WaterLvl = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
        // Per-column INLAND waterline (FarQuilt recipe: max gen-water surface near
        // the column where depth≥0.25 & level>30.25 & biome∉{Mtn,DeepN}, else 0),
        // sampled main-thread in GatherColumns + dilated onto banks. Baked per-vertex
        // into UV1.x so the wet-shore band rides the river/lake surface (no rectangles).
        // The 2D ground baseline for these columns, from the same snapshot, so
        // the water sheet can judge a bank crest at column resolution instead
        // of probing a field per cell.
        public readonly float[] Ground = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
        public readonly float[] WetLine = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
        public readonly float[] WetScratch = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];   // dilation double-buffer for WetLine
        public bool HasWater;
        // Chunk world origin. Views sit at (0,0,0) — every mesh on them is
        // WORLD-space (the terrain verts leave the mesher with this added).
        public float OriginX, OriginY, OriginZ;

        public void ResetMesh()
        {
            Verts.Clear(); Normals.Clear(); Tangents.Clear(); Uvs.Clear(); Uv1.Clear(); Colors.Clear(); Tris.Clear();
        }
    }

    // Voxel P0 — NAIVE SURFACE NETS mesher (worker thread, pure math, no Unity
    // scene API — Vector3 math structs only).
    //
    // Chosen over classic marching cubes: identical smooth result on SDF
    // terrain (vertex sits at the mean of the cell's edge crossings) with NO
    // 4096-entry triangulation table to get wrong — every line here is
    // derivable, which beats table-transcription risk (no-corner-cutting rule).
    //
    // Chunking: cells [-1..31]³ may own one vertex each (computed from the
    // sample apron, coinciding bit-exactly with the neighbour chunk's copy);
    // quads are emitted only for sample-edges whose min corner lies in
    // [0..32)³ — each physical edge is owned by exactly ONE chunk, borders
    // covered, no double-draw, no cracks.
    //
    // Winding (Unity: clockwise = front face): for an edge with SOLID at the
    // min sample the outward normal points +axis; with (u,v) = cyclic pair of
    // the axis (X→YZ, Y→ZX, Z→XY) the clockwise quad is
    //   C(u-1,v-1) → C(u,v-1) → C(u,v) → C(u-1,v)
    // (derived from the flat-ground +Y case and verified for X/Z by the same
    // cyclic symmetry). Air-at-min reverses it.
    public static class VoxelMesherSurfaceNets
    {
        private const int S = VoxelChunk.SAMPLES;   // 34

        // 0.2.815: bilinear blend of four columns' inland waterlines, where 0 means "no waterline" (the vanilla look): only
        // the columns that have one are weighted, so a shore vertex takes the waterline smoothly instead of averaging it
        // with 0; no column with one gives 0.
        private static float BlendWetLine(float w00, float w10, float w01, float w11, float fx, float fz)
        {
            float a = (1f - fx) * (1f - fz), b = fx * (1f - fz), c = (1f - fx) * fz, d = fx * fz;
            float sum = 0f, weight = 0f;
            if (w00 != 0f) { sum += w00 * a; weight += a; }
            if (w10 != 0f) { sum += w10 * b; weight += b; }
            if (w01 != 0f) { sum += w01 * c; weight += c; }
            if (w11 != 0f) { sum += w11 * d; weight += d; }
            return weight > 1e-4f ? sum / weight : 0f;
        }
        private const int C = VoxelChunk.CELLS;     // 32

        // Corner c = dx | dy<<1 | dz<<2. Edges = corner pairs differing in one bit.
        private static readonly int[,] EDGES =
        {
            {0,1},{2,3},{4,5},{6,7},   // X edges
            {0,2},{1,3},{4,6},{5,7},   // Y edges
            {0,4},{1,5},{2,6},{3,7},   // Z edges
        };

        // Sample index, CLAMPED to the grid — orphan-plane cells at index 32
        // reference samples at 33 which don't exist; clamping continues the
        // last sample flat (zero gradient), which puts the stand-in vertex
        // half a cell past the plane, sealing it with real surface geometry.
        private static int DIdx(int x, int y, int z)
        {
            if (x > 32) x = 32; if (y > 32) y = 32; if (z > 32) z = 32;
            return (x + 1) + S * ((y + 1) + S * (z + 1));
        }
        private static int CIdx(int x, int y, int z) => (x + 1) + 34 * ((y + 1) + 34 * (z + 1));

        // EDGE-DRIVEN build (round-87 rework). The old two-pass form gated
        // VERTICES on "cell edited", which dropped quads wherever a crossing
        // edge bordered a cell whose corners happened to equal seed (brush SDF
        // ≈ seed there) — the visible mesh SPLITS/slivers. Now the gate is on
        // QUADS (≥1 of the 4 surrounding cells edited) and every referenced
        // cell's vertex is realized LAZILY — an emitted quad can never lack a
        // vertex, so gate-driven holes are structurally impossible. Unedited
        // ground still grows no skin (all-clean edges are skipped), and the
        // frontier ring overlaps the coincident vanilla surface by one quad
        // instead of leaving a gap.
        public static void Build(MeshBuffers buf, Vector3 origin)
        {
            buf.ResetMesh();
            var d = buf.Density;
            var seed = buf.Seed;
            var cellIndex = buf.CellIndex;
            var edited = buf.EditedMemo;
            for (int i = 0; i < cellIndex.Length; i++) cellIndex[i] = -1;
            System.Array.Clear(edited, 0, edited.Length);

            float[] cd = new float[8];

            // Memoized "any of the cell's 8 corners differs from seed".
            bool CellEdited(int cx, int cy, int cz)
            {
                int ci = CIdx(cx, cy, cz);
                if (edited[ci] != 0) return edited[ci] == 1;
                for (int c = 0; c < 8; c++)
                {
                    int di = DIdx(cx + (c & 1), cy + ((c >> 1) & 1), cz + ((c >> 2) & 1));
                    if (d[di] != seed[di]) { edited[ci] = 1; return true; }
                }
                edited[ci] = 2;
                return false;
            }

            // Lazily create the cell's surface-nets vertex on first reference.
            int Realize(int cx, int cy, int cz)
            {
                int ci = CIdx(cx, cy, cz);
                int existing = cellIndex[ci];
                if (existing >= 0) return existing;

                for (int c = 0; c < 8; c++)
                    cd[c] = d[DIdx(cx + (c & 1), cy + ((c >> 1) & 1), cz + ((c >> 2) & 1))];

                float px = 0f, py = 0f, pz = 0f; int crossings = 0;
                for (int e = 0; e < 12; e++)
                {
                    int a = EDGES[e, 0], b = EDGES[e, 1];
                    float da = cd[a], db = cd[b];
                    if ((da > 0f) == (db > 0f)) continue;
                    float t = da / (da - db);
                    px += (a & 1) + t * ((b & 1) - (a & 1));
                    py += ((a >> 1) & 1) + t * (((b >> 1) & 1) - ((a >> 1) & 1));
                    pz += ((a >> 2) & 1) + t * (((b >> 2) & 1) - ((a >> 2) & 1));
                    crossings++;
                }
                float inv = crossings > 0 ? 1f / crossings : 0f;
                if (crossings == 0) { px = py = pz = 0.5f; inv = 1f; }   // degenerate guard
                var world = new Vector3(
                    origin.x + cx + px * inv,
                    origin.y + cy + py * inv,
                    origin.z + cz + pz * inv);

                int idx = buf.Verts.Count;
                cellIndex[ci] = idx;
                buf.Verts.Add(world);

                // Headless colliders need position + triangles only; the render
                // attributes below are pure waste on the dedi (no camera reads
                // them, and they'd sit resident in every retained mesh).
                if (buf.CollidersOnly) return idx;

                // Outward normal = −density gradient (solid-positive).
                float gx = (cd[1] + cd[3] + cd[5] + cd[7]) - (cd[0] + cd[2] + cd[4] + cd[6]);
                float gy = (cd[2] + cd[3] + cd[6] + cd[7]) - (cd[0] + cd[1] + cd[4] + cd[5]);
                float gz = (cd[4] + cd[5] + cd[6] + cd[7]) - (cd[0] + cd[1] + cd[2] + cd[3]);
                var n = new Vector3(-gx, -gy, -gz);
                n = n.sqrMagnitude > 1e-10f ? n.normalized : Vector3.up;

                buf.Normals.Add(n);
                // ANALYTIC world-anchored tangent — a pure function of the
                // (bit-identical) normal, so coincident border vertices get
                // bit-identical tangents on both sides of every chunk face.
                // Unity's RecalculateTangents averages each mesh's OWN
                // triangles, which broke the shader's normal-map basis exactly
                // on the 32m borders (the groove lines). The terrain shader
                // samples detail in world space, so world +X (fallback +Z on
                // near-vertical-X walls) projected onto the surface is the
                // correct basis; w=-1 yields the +Z bitangent Unity expects.
                Vector3 ax = (n.x < 0.99f && n.x > -0.99f) ? Vector3.right : Vector3.forward;
                Vector3 tan = ax - n * Vector3.Dot(n, ax);
                float tm = tan.magnitude;
                tan = tm > 1e-5f ? tan / tm : Vector3.forward;
                buf.Tangents.Add(new Vector4(tan.x, tan.y, tan.z, -1f));
                // Heightmap-material conventions (match TileMeshRenderer):
                // UV0 = chunk-local 0..1; vertex color = surrounding biome's
                // blend mask (per-column, sampled at seed).
                buf.Uvs.Add(new Vector2((world.x - origin.x) / 32f, (world.z - origin.z) / 32f));
                // 0.2.815 (Fire 10-06: "the same similar step pattern in our biome blending between two paints", and the stepped
                // shore band): the vertex took the NEAREST column's paint mask and waterline (frac > 0.5 ? next : this), so both
                // switched on a cell line and every paint edge and wet band drew as 90-degree notches. Now the four columns
                // around the vertex are blended bilinearly by its position in the cell: a soft, continuous transition.
                float fx = Mathf.Clamp01(px * inv), fz = Mathf.Clamp01(pz * inv);
                int x0 = Mathf.Clamp(cx, -1, C), x1 = Mathf.Clamp(cx + 1, -1, C);
                int z0 = Mathf.Clamp(cz, -1, C), z1 = Mathf.Clamp(cz + 1, -1, C);
                int i00 = (x0 + 1) + S * (z0 + 1), i10 = (x1 + 1) + S * (z0 + 1), i01 = (x0 + 1) + S * (z1 + 1), i11 = (x1 + 1) + S * (z1 + 1);
                Color b0 = Color.Lerp(buf.Biome[i00], buf.Biome[i10], fx), b1 = Color.Lerp(buf.Biome[i01], buf.Biome[i11], fx);
                buf.Colors.Add(Color.Lerp(b0, b1, fz));
                buf.Uv1.Add(new Vector2(BlendWetLine(buf.WetLine[i00], buf.WetLine[i10], buf.WetLine[i01], buf.WetLine[i11], fx, fz), 0f));   // per-vertex inland waterline → shader fWL
                return idx;
            }

            // One quad per surface-crossing OWNED edge whose 4 cells include
            // at least one edited cell.
            void EmitEdge(int axis, int x, int y, int z)
            {
                int ux = axis == 0 ? 1 : 0, uy = axis == 1 ? 1 : 0, uz = axis == 2 ? 1 : 0;
                float d0 = d[DIdx(x, y, z)];
                float d1 = d[DIdx(x + ux, y + uy, z + uz)];
                bool s0 = d0 > 0f;
                if (s0 == (d1 > 0f)) return;

                int ax, ay, az, bx2, by, bz2, cx2, cy2, cz2, dx2, dy2, dz2;
                switch (axis)
                {
                    case 0:   // X-edge: cells at x, (u,v)=(y,z)
                        ax = x; ay = y - 1; az = z - 1;
                        bx2 = x; by = y; bz2 = z - 1;
                        cx2 = x; cy2 = y; cz2 = z;
                        dx2 = x; dy2 = y - 1; dz2 = z;
                        break;
                    case 1:   // Y-edge: cells at y, (u,v)=(z,x)
                        ax = x - 1; ay = y; az = z - 1;
                        bx2 = x - 1; by = y; bz2 = z;
                        cx2 = x; cy2 = y; cz2 = z;
                        dx2 = x; dy2 = y; dz2 = z - 1;
                        break;
                    default:  // Z-edge: cells at z, (u,v)=(x,y)
                        ax = x - 1; ay = y - 1; az = z;
                        bx2 = x; by = y - 1; bz2 = z;
                        cx2 = x; cy2 = y; cz2 = z;
                        dx2 = x - 1; dy2 = y; dz2 = z;
                        break;
                }

                if (!buf.MeshAll
                    && !CellEdited(ax, ay, az) && !CellEdited(bx2, by, bz2)
                    && !CellEdited(cx2, cy2, cz2) && !CellEdited(dx2, dy2, dz2))
                    return;

                int q0 = Realize(ax, ay, az);
                int q1 = Realize(bx2, by, bz2);
                int q2 = Realize(cx2, cy2, cz2);
                int q3 = Realize(dx2, dy2, dz2);

                if (s0)
                {
                    buf.Tris.Add(q0); buf.Tris.Add(q1); buf.Tris.Add(q2);
                    buf.Tris.Add(q0); buf.Tris.Add(q2); buf.Tris.Add(q3);
                }
                else
                {
                    buf.Tris.Add(q0); buf.Tris.Add(q2); buf.Tris.Add(q1);
                    buf.Tris.Add(q0); buf.Tris.Add(q3); buf.Tris.Add(q2);
                }
            }

            // Scan only the crossing Y-band (uniform air/solid layers above and
            // below produce nothing). ±2 margin over the reported band is pure
            // seam-safety insurance — scanning an extra uniform layer just
            // early-returns per edge. A uniform chunk (yEnd ≤ yStart) emits zero.
            int yStart = Mathf.Max(0, buf.MeshYLo - 2);
            int yEnd = Mathf.Min(C, buf.MeshYHi + 2);
            for (int axis = 0; axis < 3; axis++)
                for (int z = 0; z < C; z++)
                    for (int y = yStart; y < yEnd; y++)
                        for (int x = 0; x < C; x++)
                            EmitEdge(axis, x, y, z);

            // ORPHAN FACES — the structural end of the see-through class. The
            // in-plane quads of a +X/+Z face belong to the chunk on the
            // plane's positive side; when that chunk DOESN'T EXIST, nobody
            // meshed the plane and the border showed a see-through strip. Now
            // the surviving side emits REAL surface quads for the plane from
            // its own apron samples (the plane row exists in every chunk;
            // only the +1 row is clamped flat, placing stand-in vertices half
            // a cell past the plane). The stand-ins drop automatically on the
            // remesh triggered when the owner arrives (orphan-mask change).
            if (buf.MeshAll && (buf.OrphanMask & 1) != 0)
                for (int z = 0; z < C; z++)
                    for (int y = 0; y < C; y++)
                    {
                        EmitEdge(1, C, y, z);   // in-plane Y-edges at x=32
                        EmitEdge(2, C, y, z);   // in-plane Z-edges at x=32
                    }
            if (buf.MeshAll && (buf.OrphanMask & 4) != 0)
                for (int y = 0; y < C; y++)
                    for (int x = 0; x < C; x++)
                    {
                        EmitEdge(0, x, y, C);   // in-plane X-edges at z=32
                        EmitEdge(1, x, y, C);   // in-plane Y-edges at z=32
                    }

            // ── SHINGLE LIPS (round-92 mechanism, re-aimed). The in-plane
            // quads of every face belong to the neighbour on the positive
            // side; where no neighbour covers a face (wrap frontier, absent or
            // gated chunk) those quads are emitted by NOBODY and the skin
            // stops half a cell short of the plane — the dark trench. Frontier
            // faces (mask from the enqueue-time neighbour check) get a lip
            // ALWAYS; the legacy EmitLips config still lips every 64m
            // zone-edge face. Lips vanish on the remesh the neighbour's
            // arrival triggers (swap priority heal recomputes the mask).
            if (buf.MeshAll && (buf.EmitLips || (buf.FrontierLips && buf.FrontierMask != 0))) EmitZoneEdgeLips(buf);
        }

        // Round-95 — 0.8/0.12 poked through sloped neighbours (a 0.8m reach
        // dropping only 0.12m surfaces on any hill steeper than ~9°, showing a
        // dark seam). 0.5m out + 0.45m down (~42° tuck) stays buried under any
        // slope below ~42° while still spanning the observed gap width.
        private const float LIP_OUT = 0.5f;
        private const float LIP_DOWN = 0.45f;
        private const float LIP_MAX_STEP = 3f;

        private static void EmitZoneEdgeLips(MeshBuffers buf)
        {
            var cellIndex = buf.CellIndex;

            void LipLine(bool xFixed, int fixedCell, Vector3 outward)
            {
                int prevIdx = -1;
                for (int t = -1; t < C; t++)
                {
                    int found = -1;
                    for (int cy = C - 1; cy >= -1; cy--)
                    {
                        int ci = xFixed ? CIdx(fixedCell, cy, t) : CIdx(t, cy, fixedCell);
                        if (cellIndex[ci] >= 0) { found = cellIndex[ci]; break; }
                    }
                    if (found < 0) { prevIdx = -1; continue; }
                    if (prevIdx >= 0
                        && Mathf.Abs(buf.Verts[prevIdx].y - buf.Verts[found].y) <= LIP_MAX_STEP)
                        EmitLipQuad(buf, prevIdx, found, outward);
                    prevIdx = found;
                }
            }

            // Legacy config path: faces on 64m planes (chunk faces alternate
            // internal/zone at 32m; +X plane = (KeyX+1)*32, −X = KeyX*32).
            // Frontier path: exactly the faces the mask names, any plane.
            bool legacy = buf.EmitLips;
            bool frontier = buf.FrontierLips;
            if ((legacy && ((buf.KeyX + 1) & 1) == 0) || (frontier && (buf.FrontierMask & 1) != 0)) LipLine(true, C - 1, Vector3.right);
            if ((legacy && (buf.KeyX & 1) == 0) || (frontier && (buf.FrontierMask & 2) != 0)) LipLine(true, -1, Vector3.left);
            if ((legacy && ((buf.KeyZ + 1) & 1) == 0) || (frontier && (buf.FrontierMask & 4) != 0)) LipLine(false, C - 1, Vector3.forward);
            if ((legacy && (buf.KeyZ & 1) == 0) || (frontier && (buf.FrontierMask & 8) != 0)) LipLine(false, -1, Vector3.back);
        }

        private static void EmitLipQuad(MeshBuffers buf, int ia, int ib, Vector3 outward)
        {
            Vector3 shift = outward * LIP_OUT + Vector3.down * LIP_DOWN;
            int iaO = buf.Verts.Count;
            buf.Verts.Add(buf.Verts[ia] + shift);
            int ibO = buf.Verts.Count;
            buf.Verts.Add(buf.Verts[ib] + shift);
            if (!buf.CollidersOnly)
            {
                buf.Normals.Add(buf.Normals[ia]); buf.Tangents.Add(buf.Tangents[ia]);
                buf.Uvs.Add(buf.Uvs[ia]); buf.Colors.Add(buf.Colors[ia]);
                buf.Normals.Add(buf.Normals[ib]); buf.Tangents.Add(buf.Tangents[ib]);
                buf.Uvs.Add(buf.Uvs[ib]); buf.Colors.Add(buf.Colors[ib]);
            }

            // Double-sided sliver of overlap — chirality-proof.
            buf.Tris.Add(ia); buf.Tris.Add(ib); buf.Tris.Add(ibO);
            buf.Tris.Add(ia); buf.Tris.Add(ibO); buf.Tris.Add(iaO);
            buf.Tris.Add(ia); buf.Tris.Add(ibO); buf.Tris.Add(ib);
            buf.Tris.Add(ia); buf.Tris.Add(iaO); buf.Tris.Add(ibO);
        }
    }
}
