using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Ice
{
    /// <summary>
    /// Shared marching-squares mesh generation utilities used by both
    /// <see cref="LandIceController"/> and <see cref="FloatingIceController"/>.
    ///
    /// Shared mesh generation code used by both ice controllers, avoiding
    /// duplication of ~400 lines of edge-smoothing mesh code.
    /// </summary>
    public static class IceMeshUtility
    {
        #region Constants

        /// <summary>Width of the perimeter band where edge bevel is applied.</summary>
        public const float EDGE_BEVEL_WIDTH = 1.2f;

        /// <summary>Maximum downward displacement at the very edge.</summary>
        public const float EDGE_BEVEL_DROP = 0.12f;

        /// <summary>Perlin noise amplitude for organic edge wobble (fraction of radius).</summary>
        public const float EDGE_NOISE_AMPLITUDE = 0.12f;

        /// <summary>Perlin noise frequency for edge randomization.</summary>
        public const float EDGE_NOISE_FREQUENCY = 0.7f;

        #endregion

        #region Signed Distance Field

        /// <summary>
        /// Tests if (wx,wz) is inside the union with Perlin-noise-randomized edges.
        /// </summary>
        public static bool IsInsideAnyFootprintNoisy(IReadOnlyList<IceDiskFootprint> footprints, float wx, float wz, float noiseSeed)
        {
            for (int i = 0; i < footprints.Count; i++)
            {
                var fp = footprints[i];
                float dx = wx - fp.Center.x;
                float dz = wz - fp.Center.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                float angle = Mathf.Atan2(dz, dx);
                float noiseInput = angle * EDGE_NOISE_FREQUENCY + noiseSeed + i * 53.7f;
                float noise = Mathf.PerlinNoise(noiseInput * 3f + 100f, noiseSeed + i * 17.3f + 200f);
                float wobble = (noise - 0.5f) * 2f * EDGE_NOISE_AMPLITUDE * fp.Radius;

                if (dist <= fp.Radius + wobble)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the signed distance from (wx,wz) to the nearest noisy footprint boundary.
        /// Positive = inside, negative = outside.
        /// </summary>
        public static float GetEdgeDistance(IReadOnlyList<IceDiskFootprint> footprints, float wx, float wz, float noiseSeed)
        {
            float bestDist = float.MinValue;
            for (int i = 0; i < footprints.Count; i++)
            {
                var fp = footprints[i];
                float dx = wx - fp.Center.x;
                float dz = wz - fp.Center.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                float angle = Mathf.Atan2(dz, dx);
                float noiseInput = angle * EDGE_NOISE_FREQUENCY + noiseSeed + i * 53.7f;
                float noise = Mathf.PerlinNoise(noiseInput * 3f + 100f, noiseSeed + i * 17.3f + 200f);
                float wobble = (noise - 0.5f) * 2f * EDGE_NOISE_AMPLITUDE * fp.Radius;

                float effectiveR = fp.Radius + wobble;
                float signedDist = effectiveR - dist;
                if (signedDist > bestDist)
                    bestDist = signedDist;
            }
            return bestDist;
        }

        #endregion

        #region Edge Displacement

        /// <summary>
        /// Displaces a fully-inside grid corner toward the nearest footprint boundary
        /// to smooth the staircase pattern on fully-inside cells near the edge.
        /// </summary>
        public static void DisplaceCornerTowardBoundary(
            IReadOnlyList<IceDiskFootprint> footprints, float noiseSeed,
            ref float cx, ref float cz, float edgeDist, float origX, float origZ)
        {
            if (edgeDist >= EDGE_BEVEL_WIDTH || edgeDist <= 0f) return;

            float bestFootprintDist = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < footprints.Count; i++)
            {
                var fp = footprints[i];
                float dx = origX - fp.Center.x;
                float dz = origZ - fp.Center.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                float angle = Mathf.Atan2(dz, dx);
                float noiseInput = angle * EDGE_NOISE_FREQUENCY + noiseSeed + i * 53.7f;
                float noise = Mathf.PerlinNoise(noiseInput * 3f + 100f, noiseSeed + i * 17.3f + 200f);
                float wobble = (noise - 0.5f) * 2f * EDGE_NOISE_AMPLITUDE * fp.Radius;
                float effectiveR = fp.Radius + wobble;

                float signedDist = effectiveR - dist;
                if (signedDist > 0f && signedDist < bestFootprintDist)
                {
                    bestFootprintDist = signedDist;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) return;

            var bestFp = footprints[bestIdx];
            float fdx = origX - bestFp.Center.x;
            float fdz = origZ - bestFp.Center.z;
            float fDist = Mathf.Sqrt(fdx * fdx + fdz * fdz);
            if (fDist < 0.001f) return;

            float dirX = fdx / fDist;
            float dirZ = fdz / fDist;

            float fAngle = Mathf.Atan2(fdz, fdx);
            float fNoiseInput = fAngle * EDGE_NOISE_FREQUENCY + noiseSeed + bestIdx * 53.7f;
            float fNoise = Mathf.PerlinNoise(fNoiseInput * 3f + 100f, noiseSeed + bestIdx * 17.3f + 200f);
            float fWobble = (fNoise - 0.5f) * 2f * EDGE_NOISE_AMPLITUDE * bestFp.Radius;
            float fEffectiveR = bestFp.Radius + fWobble;

            float boundaryX = bestFp.Center.x + dirX * fEffectiveR;
            float boundaryZ = bestFp.Center.z + dirZ * fEffectiveR;

            float blend = edgeDist / EDGE_BEVEL_WIDTH;
            blend = blend * blend;

            cx = Mathf.Lerp(boundaryX, origX, blend);
            cz = Mathf.Lerp(boundaryZ, origZ, blend);
        }

        #endregion

        #region Marching Squares Primitives

        /// <summary>
        /// Linearly interpolates between two points along an edge where the signed distance
        /// field crosses zero. Returns the world-space XZ position of the crossing.
        /// </summary>
        public static Vector2 LerpEdgeCrossing(float x0, float z0, float d0, float x1, float z1, float d1)
        {
            float t = d0 / (d0 - d1);
            t = Mathf.Clamp01(t);
            return new Vector2(
                x0 + (x1 - x0) * t,
                z0 + (z1 - z0) * t);
        }

        /// <summary>
        /// Computes the bevel (top surface downward displacement) for a given edge distance.
        /// </summary>
        public static float ComputeBevel(float edgeDist)
        {
            if (edgeDist >= EDGE_BEVEL_WIDTH) return 0f;
            if (edgeDist <= 0f) return EDGE_BEVEL_DROP;
            float t = 1f - edgeDist / EDGE_BEVEL_WIDTH;
            return t * t * EDGE_BEVEL_DROP;
        }

        /// <summary>
        /// Projects a point onto the line segment (a?b) and returns the closest point.
        /// </summary>
        public static Vector2 ProjectOntoSegment(float px, float pz, float ax, float az, float bx, float bz)
        {
            float abx = bx - ax, abz = bz - az;
            float lenSq = abx * abx + abz * abz;
            if (lenSq < 0.0001f) return new Vector2(ax, az);
            float t = ((px - ax) * abx + (pz - az) * abz) / lenSq;
            t = Mathf.Clamp01(t);
            return new Vector2(ax + abx * t, az + abz * t);
        }

        /// <summary>
        /// Adds a vertex for the ice shell and returns its index.
        /// </summary>
        public static int AddIceShellVert(List<Vector3> verts, List<Vector2> uvs, List<Vector3> norms,
            float lx, float lz, float y, float wx, float wz, Vector3 normal)
        {
            int idx = verts.Count;
            verts.Add(new Vector3(lx, y, lz));
            if (uvs != null) uvs.Add(new Vector2(wx * 0.1f, wz * 0.1f));
            norms.Add(normal);
            return idx;
        }

        #endregion

        #region Full Quad Emitters

        /// <summary>
        /// Emits marching-squares triangles for a single quad (top face, normal winding).
        /// </summary>
        public static void EmitMarchingSquaresQuad(
            List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Vector3> norms,
            float x0, float z0, float d0,
            float x1, float z1, float d1,
            float x2, float z2, float d2,
            float x3, float z3, float d3,
            float offX, float offZ,
            Func<float, float> yFunc,
            Vector3 normal,
            bool[] inHole, int i0, int i1, int i2, int i3, bool skipHoles)
        {
            bool b0 = d0 > 0f, b1 = d1 > 0f, b2 = d2 > 0f, b3 = d3 > 0f;
            int caseIndex = (b0 ? 1 : 0) | (b1 ? 2 : 0) | (b2 ? 4 : 0) | (b3 ? 8 : 0);

            if (caseIndex == 0) return;
            if (caseIndex == 15)
            {
                if (skipHoles && inHole != null && (inHole[i0] || inHole[i1] || inHole[i2] || inHole[i3]))
                    return;
                int v0 = AddIceShellVert(verts, uvs, norms, x0 - offX, z0 - offZ, yFunc(d0), x0, z0, normal);
                int v1 = AddIceShellVert(verts, uvs, norms, x1 - offX, z1 - offZ, yFunc(d1), x1, z1, normal);
                int v2 = AddIceShellVert(verts, uvs, norms, x2 - offX, z2 - offZ, yFunc(d2), x2, z2, normal);
                int v3 = AddIceShellVert(verts, uvs, norms, x3 - offX, z3 - offZ, yFunc(d3), x3, z3, normal);
                tris.Add(v0); tris.Add(v2); tris.Add(v1);
                tris.Add(v1); tris.Add(v2); tris.Add(v3);
                return;
            }

            // Boundary quad
            Vector2 eBot = default, eLeft = default, eRight = default, eTop = default;
            bool hasBot = b0 != b1, hasLeft = b0 != b2, hasRight = b1 != b3, hasTop = b2 != b3;

            if (hasBot) eBot = LerpEdgeCrossing(x0, z0, d0, x1, z1, d1);
            if (hasLeft) eLeft = LerpEdgeCrossing(x0, z0, d0, x2, z2, d2);
            if (hasRight) eRight = LerpEdgeCrossing(x1, z1, d1, x3, z3, d3);
            if (hasTop) eTop = LerpEdgeCrossing(x2, z2, d2, x3, z3, d3);

            var allCrossings = new List<Vector2>(4);
            if (hasBot) allCrossings.Add(eBot);
            if (hasRight) allCrossings.Add(eRight);
            if (hasTop) allCrossings.Add(eTop);
            if (hasLeft) allCrossings.Add(eLeft);

            var polyX = new List<float>(6);
            var polyZ = new List<float>(6);
            var polyD = new List<float>(6);

            if (b0) { polyX.Add(x0); polyZ.Add(z0); polyD.Add(d0); }
            if (hasBot) { polyX.Add(eBot.x); polyZ.Add(eBot.y); polyD.Add(0f); }
            if (b1) { polyX.Add(x1); polyZ.Add(z1); polyD.Add(d1); }
            if (hasRight) { polyX.Add(eRight.x); polyZ.Add(eRight.y); polyD.Add(0f); }
            if (b3) { polyX.Add(x3); polyZ.Add(z3); polyD.Add(d3); }
            if (hasTop) { polyX.Add(eTop.x); polyZ.Add(eTop.y); polyD.Add(0f); }
            if (b2) { polyX.Add(x2); polyZ.Add(z2); polyD.Add(d2); }
            if (hasLeft) { polyX.Add(eLeft.x); polyZ.Add(eLeft.y); polyD.Add(0f); }

            if (polyX.Count < 3) return;

            if (allCrossings.Count >= 2)
            {
                float segAx = allCrossings[0].x, segAz = allCrossings[0].y;
                float segBx = allCrossings[1].x, segBz = allCrossings[1].y;

                for (int p = 0; p < polyX.Count; p++)
                {
                    float d = polyD[p];
                    if (d <= 0f) continue;
                    if (d < EDGE_BEVEL_WIDTH)
                    {
                        float blend = d / EDGE_BEVEL_WIDTH;
                        blend = blend * blend;
                        Vector2 proj = ProjectOntoSegment(polyX[p], polyZ[p], segAx, segAz, segBx, segBz);
                        polyX[p] = Mathf.Lerp(proj.x, polyX[p], blend);
                        polyZ[p] = Mathf.Lerp(proj.y, polyZ[p], blend);
                    }
                }
            }

            int firstVi = AddIceShellVert(verts, uvs, norms,
                polyX[0] - offX, polyZ[0] - offZ, yFunc(polyD[0]), polyX[0], polyZ[0], normal);
            int prevVi = AddIceShellVert(verts, uvs, norms,
                polyX[1] - offX, polyZ[1] - offZ, yFunc(polyD[1]), polyX[1], polyZ[1], normal);

            for (int p = 2; p < polyX.Count; p++)
            {
                int curVi = AddIceShellVert(verts, uvs, norms,
                    polyX[p] - offX, polyZ[p] - offZ, yFunc(polyD[p]), polyX[p], polyZ[p], normal);
                tris.Add(firstVi); tris.Add(prevVi); tris.Add(curVi);
                prevVi = curVi;
            }
        }

        /// <summary>
        /// Emits marching-squares triangles for the bottom face with REVERSED winding.
        /// </summary>
        public static void EmitMarchingSquaresQuadFlipped(
            List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Vector3> norms,
            float x0, float z0, float d0,
            float x1, float z1, float d1,
            float x2, float z2, float d2,
            float x3, float z3, float d3,
            float offX, float offZ,
            Func<float, float> yFunc,
            Vector3 normal,
            bool[] inHole, int i0, int i1, int i2, int i3, bool skipHoles)
        {
            bool b0 = d0 > 0f, b1 = d1 > 0f, b2 = d2 > 0f, b3 = d3 > 0f;
            int caseIndex = (b0 ? 1 : 0) | (b1 ? 2 : 0) | (b2 ? 4 : 0) | (b3 ? 8 : 0);

            if (caseIndex == 0) return;
            if (caseIndex == 15)
            {
                if (skipHoles && inHole != null && (inHole[i0] || inHole[i1] || inHole[i2] || inHole[i3]))
                    return;
                int v0 = AddIceShellVert(verts, uvs, norms, x0 - offX, z0 - offZ, yFunc(d0), x0, z0, normal);
                int v1 = AddIceShellVert(verts, uvs, norms, x1 - offX, z1 - offZ, yFunc(d1), x1, z1, normal);
                int v2 = AddIceShellVert(verts, uvs, norms, x2 - offX, z2 - offZ, yFunc(d2), x2, z2, normal);
                int v3 = AddIceShellVert(verts, uvs, norms, x3 - offX, z3 - offZ, yFunc(d3), x3, z3, normal);
                tris.Add(v0); tris.Add(v1); tris.Add(v2);
                tris.Add(v1); tris.Add(v3); tris.Add(v2);
                return;
            }

            Vector2 eBot = default, eLeft = default, eRight = default, eTop = default;
            bool hasBot = b0 != b1, hasLeft = b0 != b2, hasRight = b1 != b3, hasTop = b2 != b3;

            if (hasBot) eBot = LerpEdgeCrossing(x0, z0, d0, x1, z1, d1);
            if (hasLeft) eLeft = LerpEdgeCrossing(x0, z0, d0, x2, z2, d2);
            if (hasRight) eRight = LerpEdgeCrossing(x1, z1, d1, x3, z3, d3);
            if (hasTop) eTop = LerpEdgeCrossing(x2, z2, d2, x3, z3, d3);

            var allCrossings = new List<Vector2>(4);
            if (hasBot) allCrossings.Add(eBot);
            if (hasRight) allCrossings.Add(eRight);
            if (hasTop) allCrossings.Add(eTop);
            if (hasLeft) allCrossings.Add(eLeft);

            var polyX = new List<float>(6);
            var polyZ = new List<float>(6);
            var polyD = new List<float>(6);

            if (b0) { polyX.Add(x0); polyZ.Add(z0); polyD.Add(d0); }
            if (hasBot) { polyX.Add(eBot.x); polyZ.Add(eBot.y); polyD.Add(0f); }
            if (b1) { polyX.Add(x1); polyZ.Add(z1); polyD.Add(d1); }
            if (hasRight) { polyX.Add(eRight.x); polyZ.Add(eRight.y); polyD.Add(0f); }
            if (b3) { polyX.Add(x3); polyZ.Add(z3); polyD.Add(d3); }
            if (hasTop) { polyX.Add(eTop.x); polyZ.Add(eTop.y); polyD.Add(0f); }
            if (b2) { polyX.Add(x2); polyZ.Add(z2); polyD.Add(d2); }
            if (hasLeft) { polyX.Add(eLeft.x); polyZ.Add(eLeft.y); polyD.Add(0f); }

            if (polyX.Count < 3) return;

            if (allCrossings.Count >= 2)
            {
                float segAx = allCrossings[0].x, segAz = allCrossings[0].y;
                float segBx = allCrossings[1].x, segBz = allCrossings[1].y;

                for (int p = 0; p < polyX.Count; p++)
                {
                    float d = polyD[p];
                    if (d <= 0f) continue;
                    if (d < EDGE_BEVEL_WIDTH)
                    {
                        float blend = d / EDGE_BEVEL_WIDTH;
                        blend = blend * blend;
                        Vector2 proj = ProjectOntoSegment(polyX[p], polyZ[p], segAx, segAz, segBx, segBz);
                        polyX[p] = Mathf.Lerp(proj.x, polyX[p], blend);
                        polyZ[p] = Mathf.Lerp(proj.y, polyZ[p], blend);
                    }
                }
            }

            int firstVi = AddIceShellVert(verts, uvs, norms,
                polyX[0] - offX, polyZ[0] - offZ, yFunc(polyD[0]), polyX[0], polyZ[0], normal);
            int prevVi = AddIceShellVert(verts, uvs, norms,
                polyX[1] - offX, polyZ[1] - offZ, yFunc(polyD[1]), polyX[1], polyZ[1], normal);

            for (int p = 2; p < polyX.Count; p++)
            {
                int curVi = AddIceShellVert(verts, uvs, norms,
                    polyX[p] - offX, polyZ[p] - offZ, yFunc(polyD[p]), polyX[p], polyZ[p], normal);
                tris.Add(firstVi); tris.Add(curVi); tris.Add(prevVi);
                prevVi = curVi;
            }
        }

        /// <summary>
        /// Emits smooth side-wall quads along the marching-squares boundary of a quad.
        /// </summary>
        public static void EmitMarchingSquaresSideWalls(
            List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Vector3> norms,
            float x0, float z0, float d0,
            float x1, float z1, float d1,
            float x2, float z2, float d2,
            float x3, float z3, float d3,
            float offX, float offZ,
            Func<float, float> topYFunc, float bottomY)
        {
            bool b0 = d0 > 0f, b1 = d1 > 0f, b2 = d2 > 0f, b3 = d3 > 0f;
            int caseIndex = (b0 ? 1 : 0) | (b1 ? 2 : 0) | (b2 ? 4 : 0) | (b3 ? 8 : 0);

            if (caseIndex == 0 || caseIndex == 15) return;

            bool hasBot = b0 != b1, hasLeft = b0 != b2, hasRight = b1 != b3, hasTop = b2 != b3;
            var crossings = new List<Vector2>(4);

            if (hasBot) crossings.Add(LerpEdgeCrossing(x0, z0, d0, x1, z1, d1));
            if (hasRight) crossings.Add(LerpEdgeCrossing(x1, z1, d1, x3, z3, d3));
            if (hasTop) crossings.Add(LerpEdgeCrossing(x3, z3, d3, x2, z2, d2));
            if (hasLeft) crossings.Add(LerpEdgeCrossing(x2, z2, d2, x0, z0, d0));

            float insideCX = 0f, insideCZ = 0f;
            int insideCount = 0;
            if (b0) { insideCX += x0; insideCZ += z0; insideCount++; }
            if (b1) { insideCX += x1; insideCZ += z1; insideCount++; }
            if (b2) { insideCX += x2; insideCZ += z2; insideCount++; }
            if (b3) { insideCX += x3; insideCZ += z3; insideCount++; }
            if (insideCount > 0) { insideCX /= insideCount; insideCZ /= insideCount; }

            for (int c = 0; c + 1 < crossings.Count; c += 2)
            {
                var ca = crossings[c];
                var cb = crossings[c + 1];
                float lxA = ca.x - offX, lzA = ca.y - offZ;
                float lxB = cb.x - offX, lzB = cb.y - offZ;

                float edx = cb.x - ca.x, edz = cb.y - ca.y;
                float len = Mathf.Sqrt(edx * edx + edz * edz);
                Vector3 wallNormal;
                if (len > 0.001f)
                {
                    float midX = (ca.x + cb.x) * 0.5f;
                    float midZ = (ca.y + cb.y) * 0.5f;
                    float candNx = -edz / len, candNz = edx / len;
                    float toInsideX = insideCX - midX, toInsideZ = insideCZ - midZ;
                    float dot = candNx * toInsideX + candNz * toInsideZ;
                    if (dot > 0f) { candNx = -candNx; candNz = -candNz; }
                    wallNormal = new Vector3(candNx, 0f, candNz);
                }
                else
                    wallNormal = Vector3.forward;

                float topYA = topYFunc(0f);
                float topYB = topYA;

                int sv = verts.Count;
                verts.Add(new Vector3(lxA, topYA, lzA));
                verts.Add(new Vector3(lxB, topYB, lzB));
                verts.Add(new Vector3(lxA, bottomY, lzA));
                verts.Add(new Vector3(lxB, bottomY, lzB));
                uvs.Add(new Vector2(ca.x * 0.1f, topYA));
                uvs.Add(new Vector2(cb.x * 0.1f, topYB));
                uvs.Add(new Vector2(ca.x * 0.1f, bottomY));
                uvs.Add(new Vector2(cb.x * 0.1f, bottomY));
                norms.Add(wallNormal); norms.Add(wallNormal);
                norms.Add(wallNormal); norms.Add(wallNormal);

                tris.Add(sv); tris.Add(sv + 1); tris.Add(sv + 2);
                tris.Add(sv + 1); tris.Add(sv + 3); tris.Add(sv + 2);
            }
        }

        #endregion

        #region Bounds Calculation

        /// <summary>
        /// Recalculates axis-aligned bounds for a set of footprints with noise padding.
        /// </summary>
        public static void RecalcBounds(IReadOnlyList<IceDiskFootprint> footprints, float cellSize,
            out float minX, out float maxX, out float minZ, out float maxZ)
        {
            if (footprints.Count == 0)
            {
                minX = maxX = minZ = maxZ = 0f;
                return;
            }

            var fp0 = footprints[0];
            float pad = EDGE_NOISE_AMPLITUDE * fp0.Radius + cellSize * 2f;
            minX = fp0.Center.x - fp0.Radius - pad;
            maxX = fp0.Center.x + fp0.Radius + pad;
            minZ = fp0.Center.z - fp0.Radius - pad;
            maxZ = fp0.Center.z + fp0.Radius + pad;

            for (int i = 1; i < footprints.Count; i++)
            {
                var fp = footprints[i];
                float p = EDGE_NOISE_AMPLITUDE * fp.Radius + cellSize * 2f;
                if (fp.Center.x - fp.Radius - p < minX) minX = fp.Center.x - fp.Radius - p;
                if (fp.Center.x + fp.Radius + p > maxX) maxX = fp.Center.x + fp.Radius + p;
                if (fp.Center.z - fp.Radius - p < minZ) minZ = fp.Center.z - fp.Radius - p;
                if (fp.Center.z + fp.Radius + p > maxZ) maxZ = fp.Center.z + fp.Radius + p;
            }
        }

        #endregion

        #region ZDO Helpers

        /// <summary>
        /// Packs footprints into a byte array for ZDO storage: [cx, cz, radius, ...]
        /// </summary>
        public static byte[] PackFootprints(IReadOnlyList<IceDiskFootprint> footprints)
        {
            byte[] data = new byte[footprints.Count * 3 * 4];
            for (int i = 0; i < footprints.Count; i++)
            {
                var fp = footprints[i];
                int offset = i * 12;
                Buffer.BlockCopy(System.BitConverter.GetBytes(fp.Center.x), 0, data, offset, 4);
                Buffer.BlockCopy(System.BitConverter.GetBytes(fp.Center.z), 0, data, offset + 4, 4);
                Buffer.BlockCopy(System.BitConverter.GetBytes(fp.Radius), 0, data, offset + 8, 4);
            }
            return data;
        }

        /// <summary>
        /// Unpacks footprints from a byte array stored in ZDO.
        /// </summary>
        public static List<IceDiskFootprint> UnpackFootprints(byte[] data)
        {
            var footprints = new List<IceDiskFootprint>();
            if (data == null || data.Length < 12) return footprints;

            int count = data.Length / 12;
            for (int i = 0; i < count; i++)
            {
                int offset = i * 12;
                float cx = System.BitConverter.ToSingle(data, offset);
                float cz = System.BitConverter.ToSingle(data, offset + 4);
                float r = System.BitConverter.ToSingle(data, offset + 8);
                footprints.Add(new IceDiskFootprint { Center = new Vector3(cx, 0f, cz), Radius = r });
            }
            return footprints;
        }

        #endregion

        #region Footprint Queries

        /// <summary>
        /// Checks if a position is inside any footprint (no noise).
        /// </summary>
        public static bool IsInsideAnyFootprint(IReadOnlyList<IceDiskFootprint> footprints, float wx, float wz)
        {
            for (int i = 0; i < footprints.Count; i++)
            {
                var fp = footprints[i];
                float dx = wx - fp.Center.x;
                float dz = wz - fp.Center.z;
                if (dx * dx + dz * dz <= fp.Radius * fp.Radius)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if a disk at (center, radius) overlaps with any footprint.
        /// </summary>
        public static bool OverlapsWithAnyFootprint(IReadOnlyList<IceDiskFootprint> footprints, Vector3 center, float radius)
        {
            for (int i = 0; i < footprints.Count; i++)
            {
                var fp = footprints[i];
                float dx = fp.Center.x - center.x;
                float dz = fp.Center.z - center.z;
                float combinedR = fp.Radius + radius;
                if (dx * dx + dz * dz <= combinedR * combinedR)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if point is inside any footprint except the excluded one.
        /// </summary>
        public static bool IsInsideAnyFootprintExcept(IReadOnlyList<IceDiskFootprint> footprints, float wx, float wz, IceDiskFootprint exclude)
        {
            for (int i = 0; i < footprints.Count; i++)
            {
                var fp = footprints[i];
                if (fp.Center.x == exclude.Center.x && fp.Center.z == exclude.Center.z
                    && Mathf.Approximately(fp.Radius, exclude.Radius))
                    continue;

                float dx = wx - fp.Center.x;
                float dz = wz - fp.Center.z;
                float r = fp.Radius - 0.3f;
                if (r > 0f && dx * dx + dz * dz <= r * r)
                    return true;
            }
            return false;
        }

        #endregion
    }
}
