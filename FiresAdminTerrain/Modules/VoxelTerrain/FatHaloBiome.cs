using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // HALO RING BIOMES (0.2.1040; Fire 10-24: "biome patches on the ring NOW ... INCLUDE Mountain / Ashlands / DeepNorth too").
    // The ring's surface gets vanilla-sized biome PATCHES laid out in the ring's UNROLLED coordinates (u = arc length round
    // the axis, v = position along it), so a patch is the same size wherever it sits on the 30 km x 3 km band:
    //   * jittered cells of CellSize metres (the count round the ring is a whole number, so the pattern wraps seamlessly at
    //     the seam); each cell rolls one biome from Weights, deterministic from the world seed;
    //   * the nearest two cell centres blend over BlendWidth metres, so a patch edge is a soft vanilla-like transition.
    // Pure math (no Unity objects): the mesher's worker threads colour every ring vertex from it (VoxelMesherSurfaceNets),
    // and WorldGenerator.GetBiome(Vector3) answers from it on the ring (HUD, FAT's biome queries, later clutter/vegetation).
    internal static class FatHaloBiome
    {
        private const float CellSize = 900f, Jitter = 0.38f, BlendWidth = 30f;
        private static readonly Heightmap.Biome[] Biomes =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Plains,
            Heightmap.Biome.Mistlands, Heightmap.Biome.Mountain, Heightmap.Biome.DeepNorth, Heightmap.Biome.AshLands,
        };
        private static readonly float[] Weights = { 2.2f, 2.0f, 1.2f, 1.6f, 1.1f, 1.0f, 0.7f, 0.7f };

        private static volatile int s_seed;
        private static volatile bool s_ready;
        private static Vector3 s_o, s_d, s_ref0, s_ref1;
        private static float s_r, s_w;
        private static int s_around;   // cells round the ring

        // Main thread (FatHaloArc.Tick): the ring's frame and the world seed, read by the workers.
        internal static void Set(bool active, Vector3 o, Vector3 d, float r, float w, Vector3 ref0, Vector3 ref1, int seed)
        {
            if (!active) { s_ready = false; return; }
            s_o = o; s_d = d; s_r = r; s_w = w; s_ref0 = ref0; s_ref1 = ref1; s_seed = seed;
            s_around = Mathf.Max(3, Mathf.RoundToInt(2f * Mathf.PI * r / CellSize));
            s_ready = true;
        }

        private static bool Unroll(Vector3 p, out float u, out float v, out float r)
        {
            u = v = r = 0f;
            if (!s_ready) return false;
            Vector3 rel = p - s_o;
            v = Vector3.Dot(rel, s_d);
            Vector3 radial = rel - v * s_d;
            r = radial.magnitude;
            if (r < 0.5f) return false;
            float ang = Mathf.Atan2(Vector3.Dot(radial, s_ref1), Vector3.Dot(radial, s_ref0));   // -pi..pi
            u = (ang < 0f ? ang + 2f * Mathf.PI : ang) * s_r;                                     // 0..circumference
            return true;
        }

        private static uint Hash(int a, int b, int salt)
        {
            unchecked
            {
                uint h = (uint)s_seed * 0x9E3779B1u ^ (uint)a * 0x85EBCA77u ^ (uint)b * 0xC2B2AE3Du ^ (uint)salt * 0x27D4EB2Fu;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return h;
            }
        }

        private static float Rand01(int a, int b, int salt) => (Hash(a, b, salt) & 0xFFFFFF) / 16777216f;

        private static Heightmap.Biome CellBiome(int cu, int cv)
        {
            float total = 0f;
            for (int i = 0; i < Weights.Length; i++) total += Weights[i];
            float pick = Rand01(cu, cv, 3) * total;
            for (int i = 0; i < Weights.Length; i++) { pick -= Weights[i]; if (pick <= 0f) return Biomes[i]; }
            return Biomes[0];
        }

        // The nearest two cells round (u, v): their biomes and how far the point is from their bisector.
        private static void Nearest(float u, float v, out Heightmap.Biome a, out Heightmap.Biome b, out float edge)
        {
            float circumference = s_around * CellSize;
            int cu0 = Mathf.FloorToInt(u / CellSize), cv0 = Mathf.FloorToInt(v / CellSize);
            float d1 = float.MaxValue, d2 = float.MaxValue;
            a = b = Biomes[0];
            for (int dv = -1; dv <= 1; dv++)
                for (int du = -1; du <= 1; du++)
                {
                    int cu = cu0 + du, cv = cv0 + dv;
                    int wrapped = ((cu % s_around) + s_around) % s_around;
                    float cx = (cu + 0.5f + (Rand01(wrapped, cv, 1) - 0.5f) * 2f * Jitter) * CellSize;
                    float cy = (cv + 0.5f + (Rand01(wrapped, cv, 2) - 0.5f) * 2f * Jitter) * CellSize;
                    float dx = Mathf.Abs(u - cx); dx = Mathf.Min(dx, circumference - dx);
                    float dy = v - cy;
                    float d = dx * dx + dy * dy;
                    var biome = CellBiome(wrapped, cv);
                    if (d < d1) { d2 = d1; b = a; d1 = d; a = biome; }
                    else if (d < d2) { d2 = d; b = biome; }
                }
            edge = (Mathf.Sqrt(d2) - Mathf.Sqrt(d1)) * 0.5f;   // metres from the bisector of the two nearest centres
        }

        // 0.2.1045 (far ring, FatHaloSky): the same field in the ring's unrolled frame, so the far ring's baked map lines up with
        // the near side exactly. u01 = 0..1 round the ring (Unroll's angle), v01 = 0..1 across the band.
        internal static bool Ready => s_ready;
        internal static int Seed => s_seed;
        internal static float HalfWidth => s_w;

        internal static bool TryUnroll01(Vector3 p, out float u01, out float v01)
        {
            u01 = v01 = 0f;
            if (!Unroll(p, out float u, out float v, out _)) return false;
            u01 = u / (2f * Mathf.PI * s_r);
            v01 = s_w > 0f ? (v + s_w) / (2f * s_w) : 0.5f;
            return true;
        }

        internal static void SampleAt01(float u01, float v01, out Heightmap.Biome a, out Heightmap.Biome b, out float t)
        {
            float u = u01 * 2f * Mathf.PI * s_r, v = v01 * 2f * s_w - s_w;
            Nearest(u, v, out a, out b, out float edge);
            t = a == b ? 0f : Mathf.Clamp01(0.5f - edge / BlendWidth);
        }

        // The ring biome at a point (the nearest cell's), when the point is in the ring's field.
        internal static bool TryBiomeAt(Vector3 p, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.Meadows;
            if (!Unroll(p, out float u, out float v, out float r) || r > s_r + 10f || Mathf.Abs(v) > s_w + 10f) return false;
            Nearest(u, v, out biome, out _, out _);
            return true;
        }

        // The vertex colour (vanilla's biome blend mask) at a point on the ring: the nearest cell's biome, blended toward the
        // second's within BlendWidth of their border.
        internal static bool TryColorAt(Vector3 p, out Color color)
        {
            color = default;
            if (!Unroll(p, out float u, out float v, out float r) || r > s_r + 10f || Mathf.Abs(v) > s_w + 10f) return false;
            Nearest(u, v, out var a, out var b, out float edge);
            Color ca = Heightmap.GetBiomeColor(a), cb = Heightmap.GetBiomeColor(b);
            float t = a == b ? 0f : Mathf.Clamp01(0.5f - edge / BlendWidth);
            color = Color.Lerp(ca, cb, t);
            return true;
        }

        // The HUD and every biome query on the ring read the same field.
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(Vector3))]
        private static class RingBiomeQuery
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Vector3 point, ref Heightmap.Biome __result)
            {
                try
                {
                    if (s_ready && FatHaloArc.RingOnly && TryBiomeAt(point, out var biome)) __result = biome;
                
                }
                catch (System.Exception e) { VerdantsAscent.Terrain.Zones.FatBiomeResolver.NoteThrow("FatHaloBiome RingBiomeQuery", point, e); throw; }   // 0.2.1201
            }
        }
    }
}
