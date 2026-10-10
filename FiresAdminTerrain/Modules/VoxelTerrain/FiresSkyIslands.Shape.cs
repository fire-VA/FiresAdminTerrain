using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Shaped skylands (kind 5): every new skyland, single or in an archipelago, stands on a skeleton of tapered bones
    // instead of a disc, so it can be a blob, a bean, a crescent round a bay, a spit or a star. The persisted R stays the
    // bounding radius, so every disc and band test still holds; the shape itself is derived on every peer from the
    // persisted fields (an atoll crescent from its formation), never stored.
    //
    // Archipelagos are rare formations on their own coarse lattice: chains budding off one another across narrow
    // channels, or atolls of crescents round an open lagoon. Each member floats its own height above the ground under it.
    //
    // Every skyland's rim rolls over into its underside, which changes character on its way round (a lip over a cut-back
    // underside, a hillside roll, a cliff), and a big skyland carries the ground's caves inside it.
    internal static partial class FiresSkyIslands
    {
        private const byte ShapedKind = 5;
        private const int ShapeSalt = 0x5C70;
        private const float SkylandCoastWobble = 0.10f;
        private const float FitMargin = 1.15f;
        private static readonly float[] ShapeFamilyWeights = { 14f, 18f, 14f, 16f, 10f, 16f, 12f };
        private const float ShapedDomeShare = 0.2f;
        private const float ShapedPondWidth = 12f;
        private const float ShapedPondChance = 0.4f;
        private const float ShapedPondShare = 0.3f;
        private const float ShapedPondShareSpan = 0.15f;
        private const float ShapedDepthMin = 1.4f;
        private const float ShapedDepthSpan = 0.8f;
        private const float RillStep = 0.5f;
        private const int GroundSamples = 24;
        private const float EdgeSoftness = 2.5f;
        private const float EdgeFeature = 0.45f;
        private const float EdgeContrast = 2.2f;
        private const float RollWidth = 7f;
        private const float RollCutout = 0.8f;
        private const float RollSlope = 3.5f;
        private const float RollCliff = 1.2f;
        private const float BrimCutout = 0.85f;
        private const float BrimSlope = 1.4f;
        private const float BrimCliff = 2.6f;
        private const float RimThickness = 1.5f;
        private const float CaveMinWidth = 20f;
        private const float CaveMinDepth = 35f;

        // The world seed the atoll crescents are laid out from; set before the registry loads.
        internal static int ShapeSeed;

        private static bool IsSkyland(byte kind) => kind == SkylandKind || kind == ShapedKind;

        private readonly struct Bone
        {
            internal readonly float Ax, Az, Bx, Bz, Ra, Rb;

            internal Bone(float ax, float az, float bx, float bz, float ra, float rb)
            {
                Ax = ax; Az = az; Bx = bx; Bz = bz; Ra = ra; Rb = rb;
            }
        }

        // A skyland's footprint as a skeleton of tapered bones: the island is everything within a bone's width of it, the
        // bones blended where they meet and the coast roughened by noise. Built once and never changed, so the voxel
        // workers can read it.
        private sealed class SkylandShape
        {
            private const double Blend = 0.12;
            private const float WobbleFeature = 0.6f;
            private const int CoastGrid = 128;
            private const float CoastSpacing = 1f;
            private const int CoastMaxPoints = 1024;
            private readonly Bone[] m_bones;
            private readonly float m_wobbleFreq, m_wobbleSeed;
            // The coast noise's world seed, for an island raised from another world; null is this world's.
            private readonly int? m_noiseSeed;
            internal readonly Vector2[] Coast;
            internal readonly Vector2[] CoastNormal;
            internal readonly float Perimeter;
            internal readonly float Area;
            internal readonly Vector2 Heart;
            internal readonly float HeartWidth;
            internal readonly float Reach;

            internal SkylandShape(Bone[] bones, float cx, float cz, float wobbleSeed, int? noiseSeed)
            {
                m_bones = bones;
                m_wobbleSeed = wobbleSeed;
                m_noiseSeed = noiseSeed;
                float widest = 0f, reach = 0f;
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                foreach (var b in bones)
                {
                    if (b.Ra > widest) { widest = b.Ra; Heart = new Vector2(b.Ax, b.Az); }
                    if (b.Rb > widest) { widest = b.Rb; Heart = new Vector2(b.Bx, b.Bz); }
                    float ra = b.Ra * (1f + SkylandCoastWobble), rb = b.Rb * (1f + SkylandCoastWobble);
                    x0 = Mathf.Min(x0, Mathf.Min(b.Ax - ra, b.Bx - rb)); x1 = Mathf.Max(x1, Mathf.Max(b.Ax + ra, b.Bx + rb));
                    z0 = Mathf.Min(z0, Mathf.Min(b.Az - ra, b.Bz - rb)); z1 = Mathf.Max(z1, Mathf.Max(b.Az + ra, b.Bz + rb));
                    reach = Mathf.Max(reach, Mathf.Max(new Vector2(b.Ax - cx, b.Az - cz).magnitude + ra, new Vector2(b.Bx - cx, b.Bz - cz).magnitude + rb));
                }
                HeartWidth = widest * (1f - SkylandCoastWobble);
                Reach = reach;
                m_wobbleFreq = 1f / Mathf.Max(2f, widest * WobbleFeature);
                TraceCoast(x0 - 2f, z0 - 2f, x1 + 2f, z1 + 2f, out Coast, out CoastNormal, out Perimeter, out Area);
            }

            // 0 on the skeleton, 1 on the coast, above 1 outside; `width` is the half-width of the island there.
            internal float Sample(float wx, float wz, out float width)
            {
                double sum = 0.0, weighted = 0.0;
                for (int i = 0; i < m_bones.Length; i++)
                {
                    var b = m_bones[i];
                    float abx = b.Bx - b.Ax, abz = b.Bz - b.Az;
                    float length2 = abx * abx + abz * abz;
                    float t = length2 > 1e-6f ? Mathf.Clamp01(((wx - b.Ax) * abx + (wz - b.Az) * abz) / length2) : 0f;
                    float qx = b.Ax + abx * t - wx, qz = b.Az + abz * t - wz;
                    float w = b.Ra + (b.Rb - b.Ra) * t;
                    double e = System.Math.Exp(-Mathf.Sqrt(qx * qx + qz * qz) / w / Blend);
                    sum += e;
                    weighted += e * w;
                }
                if (sum < 1e-30)
                {
                    width = HeartWidth;
                    return 99f;
                }
                width = (float)(weighted / sum);
                float interior = (float)(-Blend * System.Math.Log(sum));
                float wobble = m_noiseSeed.HasValue
                    ? VoxelCaveNoise.Noise01(wx * m_wobbleFreq, m_wobbleSeed, wz * m_wobbleFreq, m_noiseSeed.Value)
                    : VoxelCaveNoise.Noise01(wx * m_wobbleFreq, m_wobbleSeed, wz * m_wobbleFreq);
                return interior * (1f + SkylandCoastWobble * (2f * wobble - 1f));
            }

            internal float Interior(float wx, float wz) => Sample(wx, wz, out _);

            internal float Width(float wx, float wz)
            {
                Sample(wx, wz, out float width);
                return width;
            }

            // The coast a share of the way round it, and its outward normal there.
            internal Vector2 CoastAt(float along, out Vector2 normal)
            {
                float u = Mathf.Repeat(along, 1f) * Coast.Length;
                int i = Mathf.Min((int)u, Coast.Length - 1), j = (i + 1) % Coast.Length;
                normal = Vector2.Lerp(CoastNormal[i], CoastNormal[j], u - i).normalized;
                return Vector2.Lerp(Coast[i], Coast[j], u - i);
            }

            // Marching squares on the interior field, the longest loop kept, walked anticlockwise and resampled evenly.
            private void TraceCoast(float x0, float z0, float x1, float z1, out Vector2[] coast, out Vector2[] normals,
                out float perimeter, out float area)
            {
                float step = Mathf.Max(0.5f, Mathf.Max(x1 - x0, z1 - z0) / CoastGrid);
                int nx = Mathf.CeilToInt((x1 - x0) / step) + 1, nz = Mathf.CeilToInt((z1 - z0) / step) + 1;
                var field = new float[nx * nz];
                for (int j = 0; j < nz; j++)
                    for (int i = 0; i < nx; i++)
                        field[j * nx + i] = Interior(x0 + i * step, z0 + j * step) - 1f;
                var links = new Dictionary<int, int[]>();
                var points = new Dictionary<int, Vector2>();
                int Horizontal(int i, int j) => (j * nx + i) * 2;
                int Vertical(int i, int j) => (j * nx + i) * 2 + 1;
                Vector2 Cross(int edge)
                {
                    int cell = edge >> 1, i = cell % nx, j = cell / nx;
                    int i2 = (edge & 1) == 0 ? i + 1 : i, j2 = (edge & 1) == 0 ? j : j + 1;
                    float a = field[j * nx + i], b = field[j2 * nx + i2];
                    float t = Mathf.Clamp01(a / (a - b));
                    return new Vector2(x0 + (i + (i2 - i) * t) * step, z0 + (j + (j2 - j) * t) * step);
                }
                void Link(int a, int b)
                {
                    foreach (int edge in new[] { a, b })
                    {
                        if (!points.ContainsKey(edge)) points[edge] = Cross(edge);
                        if (!links.TryGetValue(edge, out var pair)) links[edge] = pair = new[] { -1, -1 };
                        pair[pair[0] < 0 ? 0 : 1] = edge == a ? b : a;
                    }
                }
                for (int j = 0; j < nz - 1; j++)
                    for (int i = 0; i < nx - 1; i++)
                    {
                        float a = field[j * nx + i], b = field[j * nx + i + 1], c = field[(j + 1) * nx + i + 1], d = field[(j + 1) * nx + i];
                        int kind = (a < 0f ? 1 : 0) | (b < 0f ? 2 : 0) | (c < 0f ? 4 : 0) | (d < 0f ? 8 : 0);
                        int bottom = Horizontal(i, j), right = Vertical(i + 1, j), top = Horizontal(i, j + 1), left = Vertical(i, j);
                        bool middle = (a + b + c + d) < 0f;
                        switch (kind)
                        {
                            case 1: case 14: Link(left, bottom); break;
                            case 2: case 13: Link(bottom, right); break;
                            case 3: case 12: Link(left, right); break;
                            case 4: case 11: Link(right, top); break;
                            case 6: case 9: Link(bottom, top); break;
                            case 7: case 8: Link(left, top); break;
                            case 5:
                                if (middle) { Link(bottom, right); Link(top, left); } else { Link(left, bottom); Link(right, top); }
                                break;
                            case 10:
                                if (middle) { Link(left, bottom); Link(right, top); } else { Link(bottom, right); Link(top, left); }
                                break;
                        }
                    }
                var visited = new HashSet<int>();
                List<Vector2> best = null;
                float bestArea = 0f;
                foreach (int start in links.Keys)
                {
                    if (visited.Contains(start)) continue;
                    var loop = new List<Vector2>();
                    int previous = -1, current = start;
                    while (current >= 0 && visited.Add(current))
                    {
                        loop.Add(points[current]);
                        var pair = links[current];
                        int next = pair[0] != previous ? pair[0] : pair[1];
                        previous = current;
                        current = next;
                    }
                    float signed = 0f;
                    for (int k = 0; k < loop.Count; k++)
                    {
                        var p = loop[k];
                        var q = loop[(k + 1) % loop.Count];
                        signed += p.x * q.y - q.x * p.y;
                    }
                    signed *= 0.5f;
                    if (Mathf.Abs(signed) <= bestArea) continue;
                    bestArea = Mathf.Abs(signed);
                    if (signed < 0f) loop.Reverse();
                    best = loop;
                }
                area = bestArea;
                perimeter = 0f;
                if (best == null || best.Count < 3)
                {
                    coast = new[] { Heart };
                    normals = new[] { Vector2.right };
                    return;
                }
                var arc = new float[best.Count + 1];
                for (int k = 0; k < best.Count; k++)
                {
                    perimeter += Vector2.Distance(best[k], best[(k + 1) % best.Count]);
                    arc[k + 1] = perimeter;
                }
                int count = Mathf.Clamp(Mathf.RoundToInt(perimeter / CoastSpacing), 32, CoastMaxPoints);
                coast = new Vector2[count];
                for (int s = 0, k = 0; s < count; s++)
                {
                    float target = perimeter * s / count;
                    while (k < best.Count - 1 && arc[k + 1] < target) k++;
                    float span = arc[k + 1] - arc[k];
                    coast[s] = Vector2.Lerp(best[k], best[(k + 1) % best.Count], span > 0f ? (target - arc[k]) / span : 0f);
                }
                normals = new Vector2[count];
                for (int s = 0; s < count; s++)
                {
                    var tangent = coast[(s + 1) % count] - coast[(s + count - 1) % count];
                    normals[s] = new Vector2(tangent.y, -tangent.x).normalized;
                }
            }
        }

        private static int ShapeKey(in Island isl)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)isl.X.GetHashCode()) * 16777619u;
                h = (h ^ (uint)isl.Z.GetHashCode()) * 16777619u;
                h = (h ^ (uint)isl.Seed.GetHashCode()) * 16777619u;
                h = (h ^ isl.Kind) * 16777619u;
                return (int)h;
            }
        }

        // The shape a shaped skyland derives from its persisted fields: the crescent its atoll gives it, or a family shape.
        private static SkylandShape ShapeFor(in Island isl)
        {
            if (TryRingArcAt(isl.X, isl.Z, ShapeSeedOf(in isl), out float cx, out float cz, out float radius, out var arc))
                return RingShape(cx, cz, radius, arc, ShapeKey(in isl), NoiseSeedOf(in isl));
            return FamilyShape(ShapeKey(in isl), isl.X, isl.Z, isl.R, isl.R >= GreatMinRadius, out _, NoiseSeedOf(in isl));
        }

        // One of the families (blob, bean, crescent, spit, star, ridge, twin, and for a great skyland the massif) from the key,
        // turned, and fitted inside the bounding radius r round (x, z).
        private static SkylandShape FamilyShape(int key, float x, float z, float r, bool great, out string family, int? noiseSeed = null)
        {
            var bones = new List<Bone>();
            var weights = great ? GreatFamilyWeights : ShapeFamilyWeights;
            float total = 0f;
            foreach (float weight in weights) total += weight;
            float roll = Hash(0, 0, key, ShapeSalt) * total;
            int pick = 0;
            while (pick < weights.Length - 1 && roll >= weights[pick]) roll -= weights[pick++];
            float h1 = Hash(0, 0, key, ShapeSalt + 1), h2 = Hash(0, 0, key, ShapeSalt + 2), h3 = Hash(0, 0, key, ShapeSalt + 3);
            switch (pick)
            {
                case 0:
                    family = "blob";
                    bones.Add(new Bone(-0.3f * h1, 0f, 0.3f * h1, 0f, 0.8f + 0.2f * h2, 0.65f + 0.25f * h3));
                    break;
                case 1:
                    family = "bean";
                    Arc(bones, 60f + 50f * h1, 1.2f + 0.4f * h2, 3, 0.5f + 0.12f * h3, 0.8f);
                    break;
                case 2:
                    family = "crescent";
                    Arc(bones, 130f + 70f * h1, 1.0f + 0.3f * h2, 6, 0.34f + 0.1f * h3, 0.25f);
                    break;
                case 3:
                    family = "spit";
                    bones.Add(new Bone(0f, 0f, 0.25f, 0f, 0.72f, 0.62f));
                    Tail(bones, key, new Vector2(0.55f, 0f), (h1 - 0.5f) * 0.8f, 3, 0.45f + 0.15f * h2, 0.3f, 0.05f);
                    break;
                case 4:
                    family = "star";
                    bones.Add(new Bone(0f, 0f, 0f, 0f, 0.48f, 0.48f));
                    int arms = 3 + (h1 < 0.45f ? 0 : 1) + (h1 > 0.85f ? 1 : 0);
                    for (int k = 0; k < arms; k++)
                    {
                        float a = h2 * Mathf.PI * 2f + k * Mathf.PI * 2f / arms + (Hash(k, 0, key, ShapeSalt + 4) - 0.5f) * 0.6f;
                        float length = 0.9f + 0.5f * Hash(k, 0, key, ShapeSalt + 5);
                        bones.Add(new Bone(0f, 0f, Mathf.Cos(a) * length, Mathf.Sin(a) * length, 0.34f, 0.07f));
                    }
                    break;
                case 5:
                    family = "ridge";
                    Tail(bones, key, new Vector2(-1.2f, 0f), 0f, 3, 0.8f + 0.2f * h1, 0.42f, 0.18f, 0.2f);
                    break;
                case 7:
                    family = "massif";
                    Massif(bones, key, h1, h2, h3);
                    break;
                default:
                    family = "twin";
                    var far = new Vector2(0.8f, (h1 - 0.5f) * 0.6f);
                    bones.Add(new Bone(-0.75f, 0f, -0.75f, 0f, 0.6f, 0.6f));
                    bones.Add(new Bone(far.x, far.y, far.x, far.y, 0.5f, 0.5f));
                    bones.Add(new Bone(-0.75f, 0f, far.x, far.y, 0.28f, 0.24f));
                    break;
            }
            return FitShape(bones, Hash(0, 0, key, ShapeSalt + 9) * Mathf.PI * 2f, x, z, r, key, noiseSeed);
        }

        // Bones along an arc (curving towards +z) of `degrees` on a circle of radius `bend`, widest in the middle.
        private static void Arc(List<Bone> bones, float degrees, float bend, int segments, float middle, float tipShare)
        {
            float half = degrees * Mathf.Deg2Rad * 0.5f, tip = middle * tipShare;
            for (int s = 0; s < segments; s++)
            {
                float t0 = (float)s / segments, t1 = (float)(s + 1) / segments;
                float a0 = -half + 2f * half * t0, a1 = -half + 2f * half * t1;
                bones.Add(new Bone(bend * Mathf.Sin(a0), bend - bend * Mathf.Cos(a0), bend * Mathf.Sin(a1), bend - bend * Mathf.Cos(a1),
                    tip + (middle - tip) * Mathf.Sin(Mathf.PI * t0), tip + (middle - tip) * Mathf.Sin(Mathf.PI * t1)));
            }
        }

        // A wandering run of bones from `start`, turning a little at each joint, its width easing from `from` to `to` (or
        // swelling from `ends` to `from` in the middle when `ends` is given).
        private static void Tail(List<Bone> bones, int key, Vector2 start, float heading, int segments, float length, float from, float to,
            float ends = -1f)
        {
            var at = start;
            for (int s = 0; s < segments; s++)
            {
                heading += (Hash(s, 1, key, ShapeSalt + 6) - 0.5f) * 1.0f;
                var next = at + new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * length;
                float t0 = (float)s / segments, t1 = (float)(s + 1) / segments;
                float w0 = ends < 0f ? Mathf.Lerp(from, to, t0) : ends + (from - ends) * Mathf.Sin(Mathf.PI * t0);
                float w1 = ends < 0f ? Mathf.Lerp(from, to, t1) : ends + (from - ends) * Mathf.Sin(Mathf.PI * t1);
                bones.Add(new Bone(at.x, at.y, next.x, next.y, Mathf.Max(0.04f, w0), Mathf.Max(0.04f, w1)));
                at = next;
            }
        }

        // Turns unit-size bones, centres their bounds on (x, z) and scales them so the whole coast fits in radius r.
        private static SkylandShape FitShape(List<Bone> bones, float spin, float x, float z, float r, int key, int? noiseSeed)
        {
            float c = Mathf.Cos(spin), s = Mathf.Sin(spin);
            Vector2 Turn(float px, float pz) => new Vector2(px * c - pz * s, px * s + pz * c);
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            var turned = new List<(Vector2 a, Vector2 b, float ra, float rb)>();
            foreach (var b in bones)
            {
                var a = Turn(b.Ax, b.Az);
                var e = Turn(b.Bx, b.Bz);
                turned.Add((a, e, b.Ra, b.Rb));
                x0 = Mathf.Min(x0, Mathf.Min(a.x - b.Ra, e.x - b.Rb)); x1 = Mathf.Max(x1, Mathf.Max(a.x + b.Ra, e.x + b.Rb));
                z0 = Mathf.Min(z0, Mathf.Min(a.y - b.Ra, e.y - b.Rb)); z1 = Mathf.Max(z1, Mathf.Max(a.y + b.Ra, e.y + b.Rb));
            }
            var middle = new Vector2((x0 + x1) * 0.5f, (z0 + z1) * 0.5f);
            float reach = 0f;
            foreach (var t in turned)
                reach = Mathf.Max(reach, Mathf.Max((t.a - middle).magnitude + t.ra, (t.b - middle).magnitude + t.rb));
            float scale = r / (reach * FitMargin);
            var fitted = new Bone[turned.Count];
            for (int i = 0; i < turned.Count; i++)
            {
                var t = turned[i];
                var a = (t.a - middle) * scale;
                var e = (t.b - middle) * scale;
                fitted[i] = new Bone(x + a.x, z + a.y, x + e.x, z + e.y, t.ra * scale, t.rb * scale);
            }
            return new SkylandShape(fitted, x, z, key * 0.0001f + 17.3f, noiseSeed);
        }

        // How far out a point sits: 0 on the skeleton (a round skyland's centre), 1 on the coast; and the half-width there.
        private static float Radial(in Island isl, float wx, float wz, out float width)
        {
            if (isl.Shape != null) return isl.Shape.Sample(wx, wz, out width);
            width = isl.R;
            float dx = wx - isl.X, dz = wz - isl.Z;
            return Mathf.Sqrt(dx * dx + dz * dz) / isl.R;
        }

        private static bool Covers(in Island isl, float wx, float wz)
        {
            float dx = wx - isl.X, dz = wz - isl.Z;
            if (dx * dx + dz * dz >= isl.R * isl.R) return false;
            return isl.Shape == null || isl.Shape.Interior(wx, wz) < 1f;
        }

        // ── Rims ────────────────────────────────────────────────────────────────────────────────────
        // How the rim meets the underside here: 0 cut back under an overhanging lip, ½ rolling down like a hillside, 1 a
        // sheer cliff. Low-frequency noise, so a skyland's rim changes character a few times on its way round.
        private static float EdgeStyle(in Island isl, float wx, float wz)
        {
            float size = isl.Shape != null ? isl.Shape.HeartWidth : isl.R;
            float f = 1f / Mathf.Max(6f, size * EdgeFeature);
            return Mathf.Clamp01((N(in isl, wx * f + 71f, isl.Seed + 61f, wz * f) - 0.5f) * EdgeContrast + 0.5f);
        }

        private static float Lerp3(float cutout, float slope, float cliff, float edge)
            => edge < 0.5f ? Mathf.Lerp(cutout, slope, edge * 2f) : Mathf.Lerp(slope, cliff, edge * 2f - 1f);

        // The flat of the island rolling over its rim, so it curves down into the underside instead of ending on a corner.
        private static float RimRoll(in Island isl, float wx, float wz, float rr, float width)
        {
            float band = Mathf.Min(0.5f, RollWidth / Mathf.Max(1f, width));
            float s = Mathf.SmoothStep(0f, 1f, (rr - (1f - band)) / band);
            return s * s * Lerp3(RollCutout, RollSlope, RollCliff, EdgeStyle(in isl, wx, wz));
        }

        // A smooth intersection: rounds the edge where two surfaces meet instead of leaving a crease.
        private static float SoftMin(float a, float b, float k)
        {
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
            return Mathf.Min(a, b) - h * h * k * 0.25f;
        }

        private static bool HoldsCaves(in Island isl)
            => IsSkyland(isl.Kind) && (isl.Shape != null ? isl.Shape.HeartWidth : isl.R) >= CaveMinWidth && isl.MaxDepth >= CaveMinDepth;

        // ── Archipelagos ────────────────────────────────────────────────────────────────────────────
        private const float ArchipelagoCell = 1728f;
        private const float ArchipelagoChance = 0.18f;
        private const float ArchipelagoRingShare = 0.4f;
        private const int ArchipelagoSalt = 0x5C60;
        private const int ArchipelagoMemberSalt = 0x5D00;
        private const int ArchipelagoMinMembers = 9;
        private const int ArchipelagoExtraMembers = 10;
        private const float ArchipelagoLargest = 50f;
        private const float ArchipelagoSmallest = 7f;
        private const float ArchipelagoSizeCurve = 0.55f;
        private const float ArchipelagoChannel = 4f;
        private const float ArchipelagoChannelSpan = 8f;
        private const float ArchipelagoSpread = 1.3f;
        private const float ChainPack = 0.8f;
        private const int ArchipelagoTries = 32;
        private const int ArchipelagoMemberStride = 256;
        private const int ArchipelagoMemberRolls = 128;
        private const float ArchipelagoLift = 45f;
        private const float ArchipelagoLiftSpan = 55f;
        private const float ArchipelagoLiftJitter = 30f;
        private const float ArchipelagoExtent = 340f;
        private const float RingRadius = 70f;
        private const float RingRadiusSpan = 70f;
        private const int RingMinArcs = 3;
        private const int RingExtraArcs = 4;
        private const float RingGapDegrees = 6f;
        private const float RingGapSpanDegrees = 14f;
        private const float RingBayChance = 0.5f;
        private const float RingBayDegrees = 70f;
        private const float RingBaySpanDegrees = 70f;
        private const float RingWidth = 9f;
        private const float RingWidthSpan = 9f;
        private const float RingTipShare = 0.15f;
        private const float RingSegmentDegrees = 12f;
        private const float RingLiftJitter = 8f;
        private const int LagoonIslets = 3;
        private const float LagoonIsletRadius = 7f;
        private const float LagoonIsletSpan = 7f;

        private struct RingArc
        {
            public float Mid, Half, Width;
        }

        // Where a formation's lattice cell puts it, when the cell holds one.
        private static bool TryArchipelagoCentre(int ax, int az, int seed, out float x, out float z)
        {
            x = (ax + 0.3f + 0.4f * Hash(ax, az, seed, ArchipelagoSalt + 1)) * ArchipelagoCell;
            z = (az + 0.3f + 0.4f * Hash(ax, az, seed, ArchipelagoSalt + 2)) * ArchipelagoCell;
            float limit = (FatWorldSize.Radius - 700f) - ArchipelagoExtent;   // 0.2.721: the rim, 9300 at vanilla size
            return Hash(ax, az, seed, ArchipelagoSalt) < ArchipelagoChance && x * x + z * z < limit * limit;
        }

        private static bool IsRing(int ax, int az, int seed) => Hash(ax, az, seed, ArchipelagoSalt + 9) < ArchipelagoRingShare;

        // Is this point inside the reach of an archipelago? Singles never rise there.
        private static bool InArchipelago(float x, float z, float margin, int seed)
        {
            int ax0 = Mathf.FloorToInt((x - ArchipelagoExtent - margin) / ArchipelagoCell), ax1 = Mathf.FloorToInt((x + ArchipelagoExtent + margin) / ArchipelagoCell);
            int az0 = Mathf.FloorToInt((z - ArchipelagoExtent - margin) / ArchipelagoCell), az1 = Mathf.FloorToInt((z + ArchipelagoExtent + margin) / ArchipelagoCell);
            for (int az = az0; az <= az1; az++)
                for (int ax = ax0; ax <= ax1; ax++)
                {
                    if (!TryArchipelagoCentre(ax, az, seed, out float cx, out float cz)) continue;
                    float dx = x - cx, dz = z - cz, reach = ArchipelagoExtent + margin;
                    if (dx * dx + dz * dz < reach * reach) return true;
                }
            return false;
        }

        // An atoll's crescents round its lagoon: the ring split by narrow channels, and half the time a wide bay left open.
        private static float RingArcs(int ax, int az, int seed, List<RingArc> arcs)
        {
            arcs.Clear();
            float radius = RingRadius + RingRadiusSpan * Hash(ax, az, seed, ArchipelagoSalt + 20);
            int count = RingMinArcs + (int)(Hash(ax, az, seed, ArchipelagoSalt + 21) * RingExtraArcs);
            float start = Hash(ax, az, seed, ArchipelagoSalt + 22) * Mathf.PI * 2f;
            bool bay = Hash(ax, az, seed, ArchipelagoSalt + 23) < RingBayChance;
            var gaps = new float[count];
            var weights = new float[count];
            float gapSum = 0f, weightSum = 0f;
            for (int i = 0; i < count; i++)
            {
                gaps[i] = (RingGapDegrees + RingGapSpanDegrees * Hash(ax, az, seed, ArchipelagoSalt + 32 + i * 4)) * Mathf.Deg2Rad;
                if (bay && i == count - 1)
                    gaps[i] = (RingBayDegrees + RingBaySpanDegrees * Hash(ax, az, seed, ArchipelagoSalt + 24)) * Mathf.Deg2Rad;
                weights[i] = 0.6f + Hash(ax, az, seed, ArchipelagoSalt + 33 + i * 4);
                gapSum += gaps[i];
                weightSum += weights[i];
            }
            float land = Mathf.PI * 2f - gapSum, at = start;
            for (int i = 0; i < count; i++)
            {
                float span = land * weights[i] / weightSum;
                arcs.Add(new RingArc
                {
                    Mid = at + span * 0.5f, Half = span * 0.5f,
                    Width = RingWidth + RingWidthSpan * Hash(ax, az, seed, ArchipelagoSalt + 34 + i * 4),
                });
                at += span + gaps[i];
            }
            return radius;
        }

        // One crescent of an atoll: the ring's arc, widest in its middle and running out to points at both ends.
        private static SkylandShape RingShape(float cx, float cz, float radius, RingArc arc, int key, int? noiseSeed = null)
        {
            int segments = Mathf.Max(3, Mathf.CeilToInt(arc.Half * 2f * Mathf.Rad2Deg / RingSegmentDegrees));
            var bones = new Bone[segments];
            float tip = arc.Width * RingTipShare;
            for (int s = 0; s < segments; s++)
            {
                float t0 = (float)s / segments, t1 = (float)(s + 1) / segments;
                float a0 = arc.Mid - arc.Half + 2f * arc.Half * t0, a1 = arc.Mid - arc.Half + 2f * arc.Half * t1;
                bones[s] = new Bone(cx + Mathf.Cos(a0) * radius, cz + Mathf.Sin(a0) * radius, cx + Mathf.Cos(a1) * radius, cz + Mathf.Sin(a1) * radius,
                    tip + (arc.Width - tip) * Mathf.Sin(Mathf.PI * t0), tip + (arc.Width - tip) * Mathf.Sin(Mathf.PI * t1));
            }
            return new SkylandShape(bones, cx + Mathf.Cos(arc.Mid) * radius, cz + Mathf.Sin(arc.Mid) * radius, key * 0.0001f + 17.3f, noiseSeed);
        }

        [System.ThreadStatic] private static List<RingArc> t_arcs;

        // The atoll crescent anchored exactly here in the world of `shapeSeed`, when this point is one.
        private static bool TryRingArcAt(float x, float z, int shapeSeed, out float cx, out float cz, out float radius, out RingArc arc)
        {
            radius = 0f;
            arc = default;
            int ax = Mathf.FloorToInt(x / ArchipelagoCell), az = Mathf.FloorToInt(z / ArchipelagoCell);
            if (!TryArchipelagoCentre(ax, az, shapeSeed, out cx, out cz) || !IsRing(ax, az, shapeSeed)) return false;
            var arcs = t_arcs ??= new List<RingArc>(8);
            radius = RingArcs(ax, az, shapeSeed, arcs);
            foreach (var candidate in arcs)
            {
                float dx = cx + Mathf.Cos(candidate.Mid) * radius - x, dz = cz + Mathf.Sin(candidate.Mid) * radius - z;
                if (dx * dx + dz * dz > 0.25f) continue;
                arc = candidate;
                return true;
            }
            return false;
        }

        // A formation's members: linked chains of islands, or an atoll of crescents round an open lagoon.
        private static void LayOutArchipelago(int ax, int az, int seed, float cx, float cz, System.Func<float, float, float> groundAt,
            System.Func<float, float, bool> wetAt, List<Island> members)
        {
            members.Clear();
            float lift = ArchipelagoLift + ArchipelagoLiftSpan * Hash(ax, az, seed, ArchipelagoSalt + 7);
            if (IsRing(ax, az, seed)) LayOutRing(ax, az, seed, cx, cz, lift, groundAt, wetAt, members);
            else LayOutChain(ax, az, seed, cx, cz, lift, groundAt, wetAt, members);
        }

        // Grown outward from the biggest skyland: each new one buds off a placed member across a narrow channel, mostly along
        // the formation's axis, packed against the real coasts.
        private static void LayOutChain(int ax, int az, int seed, float cx, float cz, float lift, System.Func<float, float, float> groundAt,
            System.Func<float, float, bool> wetAt, List<Island> members)
        {
            float heading = Hash(ax, az, seed, ArchipelagoSalt + 3) * Mathf.PI;
            int count = ArchipelagoMinMembers + (int)(Hash(ax, az, seed, ArchipelagoSalt + 8) * ArchipelagoExtraMembers);
            for (int i = 0; i < count; i++)
            {
                int salt = ArchipelagoMemberSalt + i * ArchipelagoMemberStride;
                float rank = count > 1 ? (float)i / (count - 1) : 0f;
                float r = Mathf.Lerp(ArchipelagoLargest, ArchipelagoSmallest, Mathf.Pow(rank, ArchipelagoSizeCurve))
                          * (0.85f + 0.3f * Hash(ax, az, seed, salt));
                float channel = ArchipelagoChannel + ArchipelagoChannelSpan * Hash(ax, az, seed, salt + 1);
                if (members.Count == 0)
                {
                    members.Add(FamilyMember(ax, az, seed, i, cx, cz, r, lift, ArchipelagoLiftJitter, groundAt, wetAt));
                    continue;
                }
                for (int attempt = 0; attempt < ArchipelagoTries; attempt++)
                {
                    var anchor = members[(int)(Hash(ax, az, seed, salt + 2 + attempt) * members.Count)];
                    float side = Hash(ax, az, seed, salt + 40 + attempt) < 0.5f ? 0f : Mathf.PI;
                    float bearing = heading + side + (2f * Hash(ax, az, seed, salt + 80 + attempt) - 1f) * ArchipelagoSpread;
                    float reach = (anchor.R + r) * ChainPack + channel;
                    float x = anchor.X + Mathf.Cos(bearing) * reach, z = anchor.Z + Mathf.Sin(bearing) * reach;
                    float ox = x - cx, oz = z - cz, room = ArchipelagoExtent - r;
                    if (ox * ox + oz * oz > room * room || Crowds(members, x, z, r, channel, true)) continue;
                    members.Add(FamilyMember(ax, az, seed, i, x, z, r, lift, ArchipelagoLiftJitter, groundAt, wetAt));
                    break;
                }
            }
        }

        private static void LayOutRing(int ax, int az, int seed, float cx, float cz, float lift, System.Func<float, float, float> groundAt,
            System.Func<float, float, bool> wetAt, List<Island> members)
        {
            var arcs = new List<RingArc>();
            float radius = RingArcs(ax, az, seed, arcs);
            float widest = 0f;
            for (int i = 0; i < arcs.Count; i++)
            {
                var arc = arcs[i];
                var isl = NewSkyland(ax, az, seed, i, cx + Mathf.Cos(arc.Mid) * radius, cz + Mathf.Sin(arc.Mid) * radius);
                isl.Shape = RingShape(cx, cz, radius, arc, ShapeKey(in isl));
                isl.R = isl.Shape.Reach;
                members.Add(Float(isl, ax, az, seed, i, lift, RingLiftJitter, groundAt, wetAt));
                widest = Mathf.Max(widest, arc.Width);
            }
            int islets = (int)(Hash(ax, az, seed, ArchipelagoSalt + 25) * (LagoonIslets + 1));
            for (int k = 0; k < islets; k++)
            {
                int index = arcs.Count + k, salt = ArchipelagoMemberSalt + index * ArchipelagoMemberStride;
                float r = LagoonIsletRadius + LagoonIsletSpan * Hash(ax, az, seed, salt);
                float room = radius - widest - r - 10f;
                if (room <= 0f) break;
                for (int attempt = 0; attempt < ArchipelagoTries; attempt++)
                {
                    float a = Hash(ax, az, seed, salt + 2 + attempt) * Mathf.PI * 2f;
                    float d = Mathf.Sqrt(Hash(ax, az, seed, salt + 40 + attempt)) * room;
                    float x = cx + Mathf.Cos(a) * d, z = cz + Mathf.Sin(a) * d;
                    if (Crowds(members, x, z, r, ArchipelagoChannel, true)) continue;
                    members.Add(FamilyMember(ax, az, seed, index, x, z, r, lift, ArchipelagoLiftJitter, groundAt, wetAt));
                    break;
                }
            }
        }

        // Too close to a member already placed? Shaped members are tested against their coasts, not their bounds.
        private static bool Crowds(List<Island> members, float x, float z, float r, float channel, bool coasts)
        {
            foreach (var other in members)
            {
                float dx = x - other.X, dz = z - other.Z, gap = r + other.R + channel;
                if (dx * dx + dz * dz >= gap * gap) continue;
                if (!coasts || other.Shape == null) return true;
                foreach (var p in other.Shape.Coast)
                {
                    float px = x - p.x, pz = z - p.y;
                    if (px * px + pz * pz < (r + channel) * (r + channel)) return true;
                }
            }
            return false;
        }

        private static Island NewSkyland(int ax, int az, int seed, int index, float x, float z)
            => new Island
            {
                X = x, Z = z, Kind = ShapedKind, RillAng = float.NaN,
                Seed = seed * 0.61803f + ax * 17.23f + az * 29.7f + index * 101.3f,
            };

        private static Island FamilyMember(int ax, int az, int seed, int index, float x, float z, float r, float lift, float jitter,
            System.Func<float, float, float> groundAt, System.Func<float, float, bool> wetAt)
        {
            var isl = NewSkyland(ax, az, seed, index, x, z);
            isl.R = r;
            isl.Shape = FamilyShape(ShapeKey(in isl), x, z, r, false, out _);
            return Float(isl, ax, az, seed, index, lift, jitter, groundAt, wetAt);
        }

        // An archipelago member floats `lift` (give or take `jitter`) above the highest ground under it.
        private static Island Float(Island isl, int ax, int az, int seed, int index, float lift, float jitter,
            System.Func<float, float, float> groundAt, System.Func<float, float, bool> wetAt)
        {
            int salt = ArchipelagoMemberSalt + index * ArchipelagoMemberStride + ArchipelagoMemberRolls;
            float ground = GroundUnder(isl.Shape, groundAt);
            return Hang(isl, Hash(ax, az, seed, salt + 60), ground + lift + (2f * Hash(ax, az, seed, salt + 61) - 1f) * jitter, ground,
                Hash(ax, az, seed, salt + 62), Hash(ax, az, seed, salt + 63), wetAt);
        }

        // The highest ground under a shaped skyland: at its heart, round its coast and halfway between.
        private static float GroundUnder(SkylandShape shape, System.Func<float, float, float> groundAt)
        {
            float ground = groundAt(shape.Heart.x, shape.Heart.y);
            int stride = Mathf.Max(1, shape.Coast.Length / GroundSamples);
            for (int k = 0; k < shape.Coast.Length; k += stride)
            {
                var p = shape.Coast[k];
                ground = Mathf.Max(ground, Mathf.Max(groundAt(p.x, p.y), groundAt((p.x + shape.Heart.x) * 0.5f, (p.y + shape.Heart.y) * 0.5f)));
            }
            return ground;
        }

        // Hangs a shaped skyland at `baseY`, raised until its keel clears the ground under it, deepens the keel with its
        // width, and sometimes digs it a pond with a fall.
        private static Island Hang(Island isl, float depthRoll, float baseY, float ground, float pondRoll, float sizeRoll,
            System.Func<float, float, bool> wetAt)
        {
            var shape = isl.Shape;
            isl.MaxDepth = Mathf.Min(GreatKeelCap, shape.HeartWidth * (ShapedDepthMin + ShapedDepthSpan * depthRoll)
                           * Mathf.Lerp(1f, SkylandBigDepthShare, Mathf.InverseLerp(SkylandDeepRadius, SkylandMaxRadius, shape.HeartWidth)));
            float lowest = baseY - isl.MaxDepth * 1.15f;
            if (lowest < ground + SkylandGroundClearance) baseY += ground + SkylandGroundClearance - lowest;
            isl.BaseY = baseY;
            isl.Top = baseY + shape.HeartWidth * ShapedDomeShare + 12f;
            isl.Bot = baseY - isl.MaxDepth * 1.15f - 6f;
            if (shape.HeartWidth >= ShapedPondWidth && pondRoll < ShapedPondChance && ReliefStrength(in isl) < WaterFrom)
                DigShapedPond(ref isl, sizeRoll, wetAt);
            return isl;
        }

        // A pond at the island's widest point, its rill walked out along the ground to the nearest coast.
        private static void DigShapedPond(ref Island isl, float sizeRoll, System.Func<float, float, bool> wetAt)
        {
            var shape = isl.Shape;
            float px = shape.Heart.x, pz = shape.Heart.y;
            float pr = shape.HeartWidth * (ShapedPondShare + ShapedPondShareSpan * sizeRoll);
            if (wetAt(px, pz) || wetAt(px + pr, pz) || wetAt(px - pr, pz) || wetAt(px, pz + pr) || wetAt(px, pz - pr)) return;
            float level = SkylandBaseTopAt(in isl, px, pz, out _) - 0.4f;
            isl.PondX = px; isl.PondZ = pz; isl.PondR = pr; isl.PondLvl = level;
            var nearest = shape.Coast[0];
            foreach (var p in shape.Coast)
                if ((p - shape.Heart).sqrMagnitude < (nearest - shape.Heart).sqrMagnitude) nearest = p;
            var way = (nearest - shape.Heart).normalized;
            float length = 0f;
            while (length < isl.R * 2f && shape.Interior(px + way.x * (length + RillStep), pz + way.y * (length + RillStep)) < SkylandRimReach)
                length += RillStep;
            if (length < pr + 4f) return;
            float endLevel = Mathf.Min(SkylandBaseTopAt(in isl, px + way.x * length, pz + way.y * length, out _) - 0.8f, level - 0.6f);
            isl.RillAng = Mathf.Atan2(way.y, way.x); isl.RillLen = length; isl.RillEndLvl = endLevel;
        }

        // ── Columns ─────────────────────────────────────────────────────────────────────────────────
        private const int SpanSteps = 12;

        // Every skyland span over a column, bottom-up: where its rock is solid (the rounded rim and underside trimmed off,
        // pond and rill dug in; its caves lie inside) and its biome. Pure math over the island table, so it answers before
        // any chunk exists.
        internal static int GetIslandSpans(float wx, float wz, List<(float bottom, float top, Heightmap.Biome biome)> into)
        {
            into.Clear();
            if (!Enabled || !s_index.TryGetValue(CellKey(wx, wz), out var list)) return 0;
            foreach (int k in list)
            {
                var isl = s_islands[k];
                if (!IsSkyland(isl.Kind) || !SkylandColumn(in isl, wx - isl.OffX, wz - isl.OffZ, out float top, out float bottom, out float side)) continue;
                if (!SolidSpan(top, bottom, side, out float low, out float high)) continue;
                into.Add((low + isl.OffY, high + isl.OffY, isl.Biome));
            }
            into.Sort((a, b) => a.bottom.CompareTo(b.bottom));
            return into.Count;
        }

        // The body is most solid midway between its top and underside and thins steadily towards each, so each face is
        // found by bisection. False when the column is all rounded-off rim.
        private static bool SolidSpan(float top, float bottom, float side, out float low, out float high)
        {
            float mid = (top + bottom) * 0.5f;
            low = high = mid;
            if (BodyDensity(mid, top, bottom, side) <= 0f) return false;
            float inside = mid, outside = top;
            for (int s = 0; s < SpanSteps; s++)
            {
                float y = (inside + outside) * 0.5f;
                if (BodyDensity(y, top, bottom, side) > 0f) inside = y;
                else outside = y;
            }
            high = inside;
            inside = mid;
            outside = bottom;
            for (int s = 0; s < SpanSteps; s++)
            {
                float y = (inside + outside) * 0.5f;
                if (BodyDensity(y, top, bottom, side) > 0f) inside = y;
                else outside = y;
            }
            low = inside;
            return true;
        }

        private static float BodyDensity(float wy, float top, float bottom, float side)
            => SoftMin(SoftMin(top - wy, wy - bottom, EdgeSoftness), side, EdgeSoftness);

        // One column of a skyland: its top (pond and rill dug in), its underside, and how far inside the coast it stands.
        private static bool SkylandColumn(in Island isl, float wx, float wz, out float top, out float bottom, out float side)
        {
            top = bottom = side = 0f;
            float dx = wx - isl.X, dz = wz - isl.Z;
            if (dx * dx + dz * dz >= isl.R * isl.R) return false;
            float rr = Radial(in isl, wx, wz, out float width);
            if (rr >= 0.999f) return false;
            top = TopAt(in isl, wx, wz);
            if (isl.PondLvl > 0f)
            {
                float pdx = wx - isl.PondX, pdz = wz - isl.PondZ;
                float pd = Mathf.Sqrt(pdx * pdx + pdz * pdz);
                if (pd < isl.PondR)
                {
                    float rp = pd / isl.PondR;
                    top = Mathf.Min(top, isl.PondLvl - (0.7f + 1.3f * (1f - rp * rp)));
                }
                else if (pd < isl.PondR + 2.5f) top = Mathf.Max(top, isl.PondLvl + 0.5f);
            }
            float rill = RillLevelAt(in isl, wx, wz, out float bed);
            if (rill > 0f) top = Mathf.Min(top, bed);
            else if (!float.IsNaN(isl.RillAng))
            {
                float dxr = Mathf.Cos(isl.RillAng), dzr = Mathf.Sin(isl.RillAng);
                float rx = wx - isl.PondX, rz = wz - isl.PondZ;
                float along = rx * dxr + rz * dzr, across = Mathf.Abs(rx * dzr - rz * dxr);
                if (along > isl.PondR * 0.6f + 1f && along < isl.RillLen && across < 3.4f)
                {
                    float t = Mathf.Clamp01((along - isl.PondR * 0.6f) / Mathf.Max(1f, isl.RillLen - isl.PondR * 0.6f));
                    top = Mathf.Max(top, Mathf.Lerp(isl.PondLvl, isl.RillEndLvl, t) + 0.45f);
                }
            }
            if (isl.Waters != null) top = CarveWaters(in isl, wx, wz, top);
            bottom = Mathf.Min(SkylandBaseTopAt(in isl, wx, wz, out _) - SpireDepth(in isl, wx, wz, Mathf.Clamp01(rr)), top - RimThickness);
            side = (1f - rr) * width * 0.9f;
            return true;
        }

        private sealed class ColumnCache
        {
            internal const int Size = 8;
            internal float X = float.NaN, Z = float.NaN;
            internal int Count;
            internal readonly int[] Island = new int[Size];
            internal readonly int[] Variety = new int[Size];
            internal readonly bool[] Solid = new bool[Size];
            internal readonly float[] Top = new float[Size], Bottom = new float[Size], Side = new float[Size];
        }

        [System.ThreadStatic] private static ColumnCache t_column;

        // The chunk workers walk a column from the bottom up, so the same column comes back for every voxel in it. Keyed by
        // the world column (wx, wz); the island is sampled at (sx, sz), the same column in its source frame.
        private static bool CachedColumn(int index, in Island isl, float wx, float wz, float sx, float sz, out float top, out float bottom, out float side)
        {
            var cache = t_column ??= new ColumnCache();
            if (cache.X != wx || cache.Z != wz)
            {
                cache.X = wx;
                cache.Z = wz;
                cache.Count = 0;
            }
            for (int c = 0; c < cache.Count; c++)
            {
                if (cache.Island[c] != index || cache.Variety[c] != isl.Variety) continue;
                top = cache.Top[c];
                bottom = cache.Bottom[c];
                side = cache.Side[c];
                return cache.Solid[c];
            }
            bool solid = SkylandColumn(in isl, sx, sz, out top, out bottom, out side);
            if (cache.Count < ColumnCache.Size)
            {
                int c = cache.Count++;
                cache.Island[c] = index;
                cache.Variety[c] = isl.Variety;
                cache.Solid[c] = solid;
                cache.Top[c] = top;
                cache.Bottom[c] = bottom;
                cache.Side[c] = side;
            }
            return solid;
        }

        // A skyland's density at a point of its column: solid inside, rounded where the top, underside and coast meet, and
        // on a big enough island carved by the ground's caves, sealed near its surfaces but for their entrances and never
        // shafted by a sinkhole, which would punch clean through.
        private static float SkylandDensity(in Island isl, float wx, float wy, float wz, float top, float bottom, float side)
        {
            float d = BodyDensity(wy, top, bottom, side);
            if (!HoldsCaves(in isl) || !FiresVoxelConfig.GeneratesCaves) return d;
            float depth = Mathf.Min(top - wy, wy - bottom);
            if (depth <= 0f) return d;
            float carve = isl.OwnSeeds ? VoxelCaveNoise.CarveSdf(wx, wy, wz, depth, isl.NoiseSeed) : VoxelCaveNoise.CarveSdf(wx, wy, wz, depth, false);
            return carve > -1.5f ? Mathf.Min(d, -carve) : d;
        }
    }
}
