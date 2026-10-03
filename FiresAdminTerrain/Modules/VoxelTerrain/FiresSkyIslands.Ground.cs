using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // The ground a big skyland carries, and the great skylands that carry the most of it: hills and ridges, cliff bands
    // where the land steps up in terraces (on a great one also broad tiers split by high cliffs), ponds in its hollows, and
    // rivers that cut down to the rim and spill off it. A small skyland keeps its plain dome. Everything here is derived on
    // every peer from the persisted island, so the registry format is unchanged.
    internal static partial class FiresSkyIslands
    {
        private const float SingleMinRadius = 18f;
        private const float SingleMaxRadius = 80f;
        private const float GreatChance = 0.08f;
        private const float GreatMinRadius = 110f;
        private const float GreatMaxRadius = 160f;
        private const float GreatKeelCap = 90f;
        private static readonly float[] GreatFamilyWeights = { 25f, 20f, 0f, 0f, 0f, 0f, 15f, 40f };

        private const float ReliefFrom = 16f;
        private const float ReliefFull = 50f;
        private const float HillShare = 0.4f;
        private const float HillMax = 34f;
        private const float RidgeShare = 0.22f;
        private const float RidgeMax = 22f;
        private const float ReliefFeature = 0.55f;
        private const float ReliefRimFade = 0.35f;
        private const float TerraceMin = 4.5f;
        private const float TerraceSpan = 4.5f;
        private const float TerraceRiser = 0.14f;
        private const float TerraceShare = 0.5f;
        private const float TierFrom = 0.7f;
        private const float TierMin = 10f;
        private const float TierSpan = 6f;
        private const float TierRiser = 0.1f;
        private const float BodyFloor = 8f;
        private const float CalmMargin = 4f;
        private const float CalmBlend = 12f;
        private const float SteepSlope = 1.1f;
        private const int ReliefSalt = 0x5C80;

        private const float WaterFrom = 0.5f;
        private const float PondAreaPer = 7000f;
        private const int MaxPonds = 5;
        private const float PondSpacing = 36f;
        private const float PondMinRadius = 5f;
        private const float PondMaxRadius = 12f;
        private const float PondInterior = 0.6f;
        private const float PondLip = 2.5f;

        // The horizontal run the pond rim climbs over, shared by BOTH pond carves (this file's ground
        // model and Sdf's density) so the two cannot drift apart. It exists because a rim with no run
        // is a vertical wall, and a vertical wall one cell wide meshes into slivers PhysX will not bake.
        internal const float PondRim = 2.5f;
        private const float RiverShare = 0.6f;
        private const float RiverDrop = 0.03f;
        private const float RiverBankWidth = 2.5f;
        private const float RiverDepth = 0.8f;
        private const float RiverSpacing = 2f;
        private const float RiverHalfWidthMin = 1.6f;
        private const float RiverHalfWidthMax = 3.2f;
        private const float RiverGoal = 0.9f;
        private const float RiverBin = 16f;
        private const float ClimbCost = 6f;
        private const int WaterSalt = 0x5C90;
        private const int SiteTries = 16;
        private const float SiteWaterGap = 4f;

        private sealed class GroundWaters
        {
            internal readonly List<PondSpot> Ponds = new List<PondSpot>();
            internal readonly List<River> Rivers = new List<River>();
        }

        private readonly struct PondSpot
        {
            internal readonly Vector2 Centre;
            internal readonly float Radius, Level;

            internal PondSpot(Vector2 centre, float radius, float level)
            {
                Centre = centre;
                Radius = radius;
                Level = level;
            }
        }

        // A river's course, its water level at every point, and a coarse grid of which stretches pass each cell, so a
        // column finds its nearest stretch without walking the whole course.
        private sealed class River
        {
            internal readonly Vector2[] Path;
            internal readonly float[] Level;
            internal readonly float HalfWidth;
            private readonly float m_x0, m_z0;
            private readonly int m_nx, m_nz;
            private readonly int[][] m_bins;

            internal River(Vector2[] path, float[] level, float halfWidth)
            {
                Path = path;
                Level = level;
                HalfWidth = halfWidth;
                float reach = halfWidth + RiverBankWidth + 1f;
                float x1 = float.MinValue, z1 = float.MinValue;
                m_x0 = float.MaxValue;
                m_z0 = float.MaxValue;
                foreach (var p in path)
                {
                    m_x0 = Mathf.Min(m_x0, p.x - reach); x1 = Mathf.Max(x1, p.x + reach);
                    m_z0 = Mathf.Min(m_z0, p.y - reach); z1 = Mathf.Max(z1, p.y + reach);
                }
                m_nx = Mathf.CeilToInt((x1 - m_x0) / RiverBin) + 1;
                m_nz = Mathf.CeilToInt((z1 - m_z0) / RiverBin) + 1;
                var bins = new List<int>[m_nx * m_nz];
                for (int i = 0; i < path.Length - 1; i++)
                {
                    int i0 = (int)((Mathf.Min(path[i].x, path[i + 1].x) - reach - m_x0) / RiverBin);
                    int i1 = (int)((Mathf.Max(path[i].x, path[i + 1].x) + reach - m_x0) / RiverBin);
                    int j0 = (int)((Mathf.Min(path[i].y, path[i + 1].y) - reach - m_z0) / RiverBin);
                    int j1 = (int)((Mathf.Max(path[i].y, path[i + 1].y) + reach - m_z0) / RiverBin);
                    for (int j = Mathf.Max(0, j0); j <= Mathf.Min(m_nz - 1, j1); j++)
                        for (int k = Mathf.Max(0, i0); k <= Mathf.Min(m_nx - 1, i1); k++)
                            (bins[j * m_nx + k] ??= new List<int>(4)).Add(i);
                }
                m_bins = new int[bins.Length][];
                for (int b = 0; b < bins.Length; b++) m_bins[b] = bins[b]?.ToArray();
            }

            // The nearest point of the course within the banks: the water level there and how far it lies across.
            internal bool Near(float wx, float wz, out float level, out float across)
            {
                level = 0f;
                across = float.MaxValue;
                int bi = (int)((wx - m_x0) / RiverBin), bj = (int)((wz - m_z0) / RiverBin);
                if (wx < m_x0 || wz < m_z0 || bi >= m_nx || bj >= m_nz) return false;
                var stretch = m_bins[bj * m_nx + bi];
                if (stretch == null) return false;
                foreach (int i in stretch)
                {
                    var a = Path[i];
                    float abx = Path[i + 1].x - a.x, abz = Path[i + 1].y - a.y;
                    float t = Mathf.Clamp01(((wx - a.x) * abx + (wz - a.y) * abz) / Mathf.Max(1e-6f, abx * abx + abz * abz));
                    float dx = a.x + abx * t - wx, dz = a.y + abz * t - wz;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d >= across) continue;
                    across = d;
                    level = Mathf.Lerp(Level[i], Level[i + 1], t);
                }
                return across < HalfWidth + RiverBankWidth;
            }
        }

        // A great skyland's core with two to four peninsulas running out to points, bays between them.
        private static void Massif(List<Bone> bones, int key, float h1, float h2, float h3)
        {
            bones.Add(new Bone(-0.25f, 0f, 0.25f, 0.15f * (h1 - 0.5f), 0.8f, 0.74f));
            int lobes = 2 + (int)(h2 * 3f);
            for (int k = 0; k < lobes; k++)
            {
                float a = h3 * Mathf.PI * 2f + k * Mathf.PI * 2f / lobes + (Hash(k, 2, key, ShapeSalt + 11) - 0.5f) * 0.9f;
                var start = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.5f;
                Tail(bones, key + k * 7919, start, a, 2, 0.32f + 0.2f * Hash(k, 3, key, ShapeSalt + 12), 0.45f,
                    0.06f + 0.1f * Hash(k, 4, key, ShapeSalt + 13));
            }
        }

        // ── Relief ──────────────────────────────────────────────────────────────────────────────────
        // How much ground a skyland carries: none on a small one, all of it from a 50 m half-width up.
        private static float ReliefStrength(in Island isl)
            => isl.Shape == null ? 0f : Mathf.SmoothStep(0f, 1f, (isl.Shape.HeartWidth - ReliefFrom) / (ReliefFull - ReliefFrom));

        // Hills and ridges, stepped into terraces with cliff risers in patches, easing out towards the rim and around the
        // skyland's own pond and rill.
        private static float Relief(in Island isl, float wx, float wz, float rr)
        {
            float strength = ReliefStrength(in isl);
            if (strength <= 0f) return 0f;
            float size = isl.Shape.HeartWidth;
            float f = 1f / Mathf.Max(18f, size * ReliefFeature);
            float hills = (N(in isl, wx * f + 301f, isl.Seed + 131f, wz * f) - 0.5f) * 1.4f
                          + (N(in isl, wx * f * 2.3f + 17f, isl.Seed + 137f, wz * f * 2.3f) - 0.5f) * 0.6f;
            float crest = 1f - Mathf.Abs(2f * N(in isl, wx * f * 0.8f + 91f, isl.Seed + 139f, wz * f * 0.8f) - 1f);
            float height = hills * Mathf.Min(HillMax, size * HillShare) + crest * crest * Mathf.Min(RidgeMax, size * RidgeShare);
            float step = TerraceMin + TerraceSpan * Hash(0, 0, isl.Variety, ReliefSalt);
            float cliffs = Mathf.SmoothStep(0f, 1f, (N(in isl, wx * f * 0.7f + 55f, isl.Seed + 149f, wz * f * 0.7f) - TerraceShare) / 0.12f);
            height = Mathf.Lerp(height, Terrace(height, step, TerraceRiser), cliffs);
            if (height < 0f)
            {
                float keel = SpireDepth(in isl, wx, wz, rr);
                height *= Mathf.Clamp01((keel - BodyFloor) / (Mathf.Min(HillMax, size * HillShare) + step));
            }
            height += Tiers(in isl, wx, wz, f, strength);
            float fade = Mathf.SmoothStep(0f, 1f, (1f - rr) / ReliefRimFade);
            return height * fade * strength * Calm(in isl, wx, wz);
        }

        // On a great skyland the land also breaks into broad tiers, each a 10-16 m cliff above the next.
        private static float Tiers(in Island isl, float wx, float wz, float f, float strength)
        {
            if (strength <= TierFrom) return 0f;
            float step = TierMin + TierSpan * Hash(0, 0, isl.Variety, ReliefSalt + 1);
            float lay = (N(in isl, wx * f * 0.45f + 7f, isl.Seed + 157f, wz * f * 0.45f) - 0.35f) * 2.2f * step;
            return Mathf.Max(0f, Terrace(lay, step, TierRiser)) * Mathf.SmoothStep(0f, 1f, (strength - TierFrom) / (1f - TierFrom));
        }

        // Flat benches with a steep riser at the end of each step.
        private static float Terrace(float height, float step, float riser)
        {
            float q = height / step, floor = Mathf.Floor(q);
            return (floor + Mathf.SmoothStep(0f, 1f, (q - floor - (1f - riser)) / riser)) * step;
        }

        // The most the relief can lift a skyland's top, for its vertical band.
        private static float ReliefCeiling(in Island isl)
        {
            float strength = ReliefStrength(in isl);
            if (strength <= 0f) return 0f;
            float size = isl.Shape.HeartWidth;
            float tiers = strength > TierFrom ? 2f * (TierMin + TierSpan) : 0f;
            return strength * (Mathf.Min(HillMax, size * HillShare) + Mathf.Min(RidgeMax, size * RidgeShare) + TerraceMin + TerraceSpan) + tiers;
        }

        // 0 at the skyland's own pond and along its rill, 1 away from them: the relief never lifts or buries them.
        private static float Calm(in Island isl, float wx, float wz)
        {
            if (isl.PondLvl <= 0f) return 1f;
            float dx = wx - isl.PondX, dz = wz - isl.PondZ;
            float calm = Mathf.SmoothStep(0f, 1f, (Mathf.Sqrt(dx * dx + dz * dz) - isl.PondR - CalmMargin) / CalmBlend);
            if (float.IsNaN(isl.RillAng)) return calm;
            float dxr = Mathf.Cos(isl.RillAng), dzr = Mathf.Sin(isl.RillAng);
            float along = Mathf.Clamp(dx * dxr + dz * dzr, 0f, isl.RillLen);
            float ex = dx - dxr * along, ez = dz - dzr * along;
            return Mathf.Min(calm, Mathf.SmoothStep(0f, 1f, (Mathf.Sqrt(ex * ex + ez * ez) - CalmMargin) / CalmBlend));
        }

        // Too steep for a tree: cliff risers take rocks instead.
        private static bool Steep(in Island isl, float wx, float wz)
        {
            const float d = 0.75f;
            float gx = TopAt(in isl, wx + d, wz) - TopAt(in isl, wx - d, wz);
            float gz = TopAt(in isl, wx, wz + d) - TopAt(in isl, wx, wz - d);
            return Mathf.Sqrt(gx * gx + gz * gz) / (2f * d) > SteepSlope;
        }

        // ── Waters ──────────────────────────────────────────────────────────────────────────────────
        // Ponds in the lowest hollows and rivers from them to the rim, laid out once from the skyland's own ground.
        private static GroundWaters DeriveWaters(in Island isl)
        {
            if (isl.Shape == null || isl.PondLvl > 0f || ReliefStrength(in isl) < WaterFrom) return null;
            var shape = isl.Shape;
            float g = Mathf.Max(4f, shape.HeartWidth / 8f);
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var p in shape.Coast)
            {
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                z0 = Mathf.Min(z0, p.y); z1 = Mathf.Max(z1, p.y);
            }
            int nx = Mathf.CeilToInt((x1 - x0) / g) + 1, nz = Mathf.CeilToInt((z1 - z0) / g) + 1;
            var height = new float[nx * nz];
            var interior = new float[nx * nz];
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    float x = x0 + i * g, z = z0 + j * g;
                    interior[j * nx + i] = shape.Interior(x, z);
                    height[j * nx + i] = interior[j * nx + i] < 1f ? NaturalTopAt(in isl, x, z) : float.MaxValue;
                }

            var hollows = new List<(float h, int cell)>();
            for (int j = 1; j < nz - 1; j++)
                for (int i = 1; i < nx - 1; i++)
                {
                    int c = j * nx + i;
                    if (interior[c] >= PondInterior) continue;
                    bool lowest = true;
                    for (int dj = -1; dj <= 1 && lowest; dj++)
                        for (int di = -1; di <= 1; di++)
                            if ((di != 0 || dj != 0) && height[c + dj * nx + di] <= height[c]) { lowest = false; break; }
                    if (lowest) hollows.Add((height[c], c));
                }
            hollows.Sort((a, b) => a.h != b.h ? a.h.CompareTo(b.h) : a.cell.CompareTo(b.cell));

            var waters = new GroundWaters();
            int wanted = Mathf.Clamp(Mathf.RoundToInt(shape.Area / PondAreaPer), 1, MaxPonds);
            foreach (var hollow in hollows)
            {
                if (waters.Ponds.Count >= wanted) break;
                var centre = new Vector2(x0 + hollow.cell % nx * g, z0 + hollow.cell / nx * g);
                bool crowded = false;
                foreach (var other in waters.Ponds) crowded |= Vector2.Distance(other.Centre, centre) < PondSpacing;
                if (crowded) continue;
                int k = waters.Ponds.Count;
                float radius = Mathf.Clamp(shape.HeartWidth * 0.12f, PondMinRadius, PondMaxRadius) * (0.8f + 0.4f * Hash(k, 1, isl.Variety, WaterSalt));
                waters.Ponds.Add(new PondSpot(centre, radius, NaturalTopAt(in isl, centre.x, centre.y) - 0.4f));
            }

            for (int k = 0; k < waters.Ponds.Count; k++)
            {
                if (k > 0 && Hash(k, 2, isl.Variety, WaterSalt + 1) >= RiverShare) continue;
                var path = OutletPath(in isl, height, interior, nx, nz, x0, z0, g, waters.Ponds[k].Centre);
                if (path != null) waters.Rivers.Add(MakeRiver(in isl, path, waters.Ponds[k].Level));
            }
            return waters.Ponds.Count > 0 ? waters : null;
        }

        // The cheapest way from a pond to the coast over the skyland's ground: every metre costs, every metre climbed costs
        // more, so the river finds the low way out and cuts a gorge only where it has to.
        private static List<Vector2> OutletPath(in Island isl, float[] height, float[] interior, int nx, int nz, float x0, float z0, float g,
            Vector2 from)
        {
            int n = nx * nz;
            var cost = new float[n];
            var prev = new int[n];
            var done = new bool[n];
            for (int c = 0; c < n; c++) { cost[c] = float.MaxValue; prev[c] = -1; }
            int start = Mathf.Clamp(Mathf.RoundToInt((from.y - z0) / g), 0, nz - 1) * nx + Mathf.Clamp(Mathf.RoundToInt((from.x - x0) / g), 0, nx - 1);
            cost[start] = 0f;
            int goal = -1;
            var open = new SortedSet<(float, int)> { (0f, start) };
            while (open.Count > 0)
            {
                var (at, c) = open.Min;
                open.Remove(open.Min);
                if (done[c]) continue;
                done[c] = true;
                if (interior[c] >= RiverGoal) { goal = c; break; }
                int ci = c % nx, cj = c / nx;
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int i = ci + di, j = cj + dj;
                        if (i < 0 || j < 0 || i >= nx || j >= nz) continue;
                        int m = j * nx + i;
                        if (done[m] || interior[m] >= 1f) continue;
                        float next = at + g * ((di != 0 && dj != 0) ? 1.4142f : 1f) + ClimbCost * Mathf.Max(0f, height[m] - height[c]);
                        if (next >= cost[m]) continue;
                        cost[m] = next;
                        prev[m] = c;
                        open.Add((next, m));
                    }
            }
            if (goal < 0) return null;
            var cells = new List<Vector2>();
            for (int c = goal; c >= 0; c = prev[c]) cells.Add(new Vector2(x0 + c % nx * g, z0 + c / nx * g));
            cells.Reverse();
            cells[0] = from;
            if (cells.Count < 2) return null;
            var way = (cells[cells.Count - 1] - cells[cells.Count - 2]).normalized;
            var end = cells[cells.Count - 1];
            for (float d = 0f; d < g * 3f && isl.Shape.Interior(end.x + way.x * 0.5f, end.y + way.y * 0.5f) < SkylandRimReach; d += 0.5f)
                end += way * 0.5f;
            cells[cells.Count - 1] = end;
            return Resample(Chaikin(Chaikin(cells)), RiverSpacing);
        }

        private static List<Vector2> Chaikin(List<Vector2> points)
        {
            var smooth = new List<Vector2> { points[0] };
            for (int i = 0; i < points.Count - 1; i++)
            {
                smooth.Add(Vector2.Lerp(points[i], points[i + 1], 0.25f));
                smooth.Add(Vector2.Lerp(points[i], points[i + 1], 0.75f));
            }
            smooth.Add(points[points.Count - 1]);
            return smooth;
        }

        private static List<Vector2> Resample(List<Vector2> points, float spacing)
        {
            var even = new List<Vector2> { points[0] };
            float carry = 0f;
            for (int i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                float length = Vector2.Distance(a, b);
                for (float d = spacing - carry; d < length; d += spacing) even.Add(Vector2.Lerp(a, b, d / length));
                carry = (carry + length) % spacing;
            }
            if (Vector2.Distance(even[even.Count - 1], points[points.Count - 1]) > spacing * 0.25f) even.Add(points[points.Count - 1]);
            else even[even.Count - 1] = points[points.Count - 1];
            return even;
        }

        // Water that only ever runs downhill and never stands above the ground beside it.
        private static River MakeRiver(in Island isl, List<Vector2> path, float source)
        {
            var level = new float[path.Count];
            level[0] = source;
            for (int i = 1; i < path.Count; i++)
            {
                float ds = Vector2.Distance(path[i - 1], path[i]);
                level[i] = Mathf.Min(level[i - 1] - RiverDrop * ds, NaturalTopAt(in isl, path[i].x, path[i].y) - 0.5f);
            }
            return new River(path.ToArray(), level, Mathf.Clamp(isl.Shape.HeartWidth * 0.05f, RiverHalfWidthMin, RiverHalfWidthMax));
        }

        // Pond bowls with a low lip round them, then river channels with banks up to the ground, cutting through the lips.
        private static float CarveWaters(in Island isl, float wx, float wz, float top)
        {
            foreach (var pond in isl.Waters.Ponds)
            {
                float dx = wx - pond.Centre.x, dz = wz - pond.Centre.y;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                // Continuous rim, matching Sdf's pond exactly. These two carves MUST agree: this one
                // shapes the ground model the water system reads, Sdf writes the voxel density, and a
                // disagreement between them is water standing over solid or a bed with no water in it.
                // See the note in Sdf for why the old two-branch version produced uncollidable slivers.
                if (d < pond.Radius)
                {
                    float rp = d / pond.Radius;
                    top = Mathf.Min(top, pond.Level - (0.7f + 1.3f * (1f - rp * rp)));
                }
                else if (d < pond.Radius + PondRim)
                {
                    float t = (d - pond.Radius) / PondRim;
                    top = Mathf.Max(top, Mathf.Lerp(pond.Level - 0.7f, pond.Level + 0.5f, t * t * (3f - 2f * t)));
                }
            }
            foreach (var river in isl.Waters.Rivers)
            {
                if (!river.Near(wx, wz, out float level, out float across)) continue;
                top = Mathf.Min(top, RiverBed(river, level, across));
            }
            return top;
        }

        private static float RiverBed(River river, float level, float across)
        {
            if (across >= river.HalfWidth) return level + (across - river.HalfWidth) * 0.9f;
            float t = across / river.HalfWidth;
            return level - RiverDepth * (1f - t * t) - 0.15f;
        }

        // The water standing over a column of the skyland's own ponds and rivers, and how deep its bed lies under it.
        private static bool GroundWaterAt(in Island isl, float wx, float wz, out float level, out float depth)
        {
            level = 0f;
            depth = 0f;
            if (isl.Waters == null) return false;
            foreach (var pond in isl.Waters.Ponds)
            {
                float dx = wx - pond.Centre.x, dz = wz - pond.Centre.y;
                float d2 = dx * dx + dz * dz, r2 = pond.Radius * pond.Radius;
                if (d2 >= r2 || pond.Level <= level) continue;
                level = pond.Level;
                depth = 0.7f + 1.3f * (1f - d2 / r2);
            }
            foreach (var river in isl.Waters.Rivers)
            {
                if (!river.Near(wx, wz, out float riverLevel, out float across) || across >= river.HalfWidth || riverLevel <= level) continue;
                level = riverLevel;
                depth = riverLevel - RiverBed(river, riverLevel, across);
            }
            return level > 0f;
        }

        private static bool InGroundWater(in Island isl, float wx, float wz)
        {
            if (isl.Waters == null) return false;
            foreach (var pond in isl.Waters.Ponds)
            {
                float dx = wx - pond.Centre.x, dz = wz - pond.Centre.y;
                if (dx * dx + dz * dz < (pond.Radius + 2f) * (pond.Radius + 2f)) return true;
            }
            foreach (var river in isl.Waters.Rivers)
                if (river.Near(wx, wz, out _, out float across) && across < river.HalfWidth + 1.5f) return true;
            return false;
        }

        // How far a point stands from the nearest of the skyland's ponds and rivers, measured from their banks.
        private static float WaterClearance(in Island isl, float wx, float wz)
        {
            float clearance = float.MaxValue;
            if (isl.Waters == null) return clearance;
            var p = new Vector2(wx, wz);
            foreach (var pond in isl.Waters.Ponds) clearance = Mathf.Min(clearance, Vector2.Distance(pond.Centre, p) - pond.Radius - PondLip);
            foreach (var river in isl.Waters.Rivers)
                for (int i = 0; i < river.Path.Length - 1; i++)
                {
                    var a = river.Path[i];
                    var ab = river.Path[i + 1] - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
                    clearance = Mathf.Min(clearance, Vector2.Distance(a + ab * t, p) - river.HalfWidth - RiverBankWidth);
                }
            return clearance;
        }

        // Where a location of reach `reach` may stand on a skyland that carries water: the first of a spiral of spots round
        // the heart that keeps its whole plateau on dry land and inside the coast.
        private static bool TryDrySite(in Island isl, float room, float reach, out Vector2 site)
        {
            var heart = isl.Shape != null ? isl.Shape.Heart : new Vector2(isl.X, isl.Z);
            float turn = Hash(0, 0, isl.Variety, WaterSalt + 3) * Mathf.PI * 2f;
            float spread = Mathf.Max(room - reach, (isl.Shape != null ? isl.Shape.HeartWidth : isl.R) * 0.9f);
            for (int k = 0; k < SiteTries; k++)
            {
                float a = turn + k * 2.3999632f;
                float d = spread * Mathf.Sqrt((k + 0.5f) / SiteTries);
                site = heart + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Mathf.Max(0f, d);
                if (WaterClearance(in isl, site.x, site.y) < reach + SiteWaterGap) continue;
                if (isl.Shape != null && isl.Shape.Sample(site.x, site.y, out float width) > 1f - (reach + SiteWaterGap) / Mathf.Max(1f, width)) continue;
                return true;
            }
            site = default;
            return false;
        }

        // ── Falls ───────────────────────────────────────────────────────────────────────────────────
        internal struct SkylandFall
        {
            public Vector3 Lip;
            public Vector3 Outward;
            public float Width;
            public float Drop;
        }

        private const float RillFallWidth = 3.6f;
        private const float FallDropSmall = 10f;
        private const float FallDropLarge = 44f;
        private const float FallDropJitter = 2f;
        private const float FallDropJitterLarge = 4f;
        private const float RiverFallFrom = 0.15f;

        // Every fall off a skyland's rim: its own rill's, and one where each river spills over. The drop grows with the
        // water feeding it: a rill falls 10-12 m, the widest river 44-50 m, each still fading out in mid-air.
        internal static void CollectSkylandFalls(int index, List<SkylandFall> falls)
        {
            falls.Clear();
            var isl = s_islands[index];
            if (!IsSkyland(isl.Kind)) return;
            if (!float.IsNaN(isl.RillAng) && isl.PondLvl > 0f)
            {
                float dxr = Mathf.Cos(isl.RillAng), dzr = Mathf.Sin(isl.RillAng);
                falls.Add(new SkylandFall
                {
                    Lip = new Vector3(isl.PondX + dxr * isl.RillLen, isl.RillEndLvl, isl.PondZ + dzr * isl.RillLen),
                    Outward = new Vector3(dxr, 0f, dzr),
                    Width = RillFallWidth,
                    Drop = FallDrop(0f, Hash(0, 0, isl.Variety, FallDropSalt)),
                });
            }
            for (int k = 0; isl.Waters != null && k < isl.Waters.Rivers.Count; k++)
            {
                var river = isl.Waters.Rivers[k];
                int last = river.Path.Length - 1;
                var way = (river.Path[last] - river.Path[last - 1]).normalized;
                float flow = Mathf.Lerp(RiverFallFrom, 1f, Mathf.InverseLerp(RiverHalfWidthMin, RiverHalfWidthMax, river.HalfWidth));
                falls.Add(new SkylandFall
                {
                    Lip = new Vector3(river.Path[last].x, river.Level[last], river.Path[last].y),
                    Outward = new Vector3(way.x, 0f, way.y),
                    Width = river.HalfWidth * 1.8f,
                    Drop = FallDrop(flow, Hash(k, 5, isl.Variety, WaterSalt)),
                });
            }
            ShiftLips(in isl, falls);
        }

        private static void ShiftLips(in Island isl, List<SkylandFall> falls)
        {
            if (!IsMoved(in isl)) return;
            var offset = new Vector3(isl.OffX, isl.OffY, isl.OffZ);
            for (int k = 0; k < falls.Count; k++)
            {
                var fall = falls[k];
                fall.Lip += offset;
                falls[k] = fall;
            }
        }

        private static float FallDrop(float flow, float roll)
            => Mathf.Lerp(FallDropSmall, FallDropLarge, flow) + roll * Mathf.Lerp(FallDropJitter, FallDropJitterLarge + FallDropJitter, flow);

        // Is this coast point close enough to one of the skyland's falls that rim dressing would hang in the water?
        private static bool NearAnyFall(in Island isl, Vector2 coast)
        {
            if (NearFallAt(in isl, coast)) return true;
            if (isl.Waters == null) return false;
            foreach (var river in isl.Waters.Rivers)
            {
                var lip = river.Path[river.Path.Length - 1];
                if ((lip - coast).sqrMagnitude < FallClearance * FallClearance) return true;
            }
            return false;
        }

        // Does this skyland carry ground of its own (hills, cliffs, ponds, rivers)? Its dressing was laid out without them
        // before the ground existed.
        internal static bool SkylandCarriesGround(int index) => ReliefStrength(s_islands[index]) > 0f;
    }
}
