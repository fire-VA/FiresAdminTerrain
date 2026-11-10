using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    /// <summary>One far tree as canopy shade needs it: the ground point, the tree's height and its crown radius (metres).</summary>
    public struct CanopyTree
    {
        public Vector3 Ground; public float Height; public float CrownRadius;
        public CanopyTree(Vector3 ground, float height, float crownRadius) { Ground = ground; Height = height; CrownRadius = crownRadius; }
    }

    /// <summary>
    /// FarLands #10 canopy shade, the feed and the splat ([generator], 11-08; our own method). [saddles]' far forest (#2) hands the
    /// same cells it gives Core's impostor engine here, as CanopyTree arrays (any thread builds them; SetCell / ClearCell on any thread).
    /// The worker splats every crown into a 1024² grid (8 m texels, centred with the far-shade grid), moved along the sun by the
    /// crown's height so the shade falls where the sun puts it, and turns the cover into sunlight let through: exp(-k · cover).
    /// It re-runs at most twice a second, and only when the trees or the sun changed.
    /// </summary>
    public static class FatFarCanopy
    {
        internal const int Size = 1024;
        internal const float Texel = 8f;
        private const float Opacity = 0.75f, Absorb = 1.2f, MaxOffset = 240f;

        private static readonly object s_lock = new object();
        private static readonly Dictionary<long, CanopyTree[]> s_cells = new Dictionary<long, CanopyTree[]>();
        private static int s_version;

        /// <summary>Total trees held, for the Debug line.</summary>
        internal static int TreeCount { get { lock (s_lock) { int n = 0; foreach (var c in s_cells.Values) n += c.Length; return n; } } }
        internal static int Version { get { lock (s_lock) return s_version; } }

        /// <summary>Replaces one cell's trees (the caller must not change the array afterwards). Any thread.</summary>
        public static void SetCell(long cellKey, CanopyTree[] trees)
        {
            lock (s_lock) { if (trees == null || trees.Length == 0) s_cells.Remove(cellKey); else s_cells[cellKey] = trees; s_version++; }
        }

        /// <summary>Forgets one cell. Any thread.</summary>
        public static void ClearCell(long cellKey) { lock (s_lock) { if (s_cells.Remove(cellKey)) s_version++; } }

        /// <summary>Forgets every cell (world change). Any thread.</summary>
        public static void ClearAll() { lock (s_lock) { if (s_cells.Count > 0) { s_cells.Clear(); s_version++; } } }

        /// <summary>
        /// Worker: the light let through per texel (0 = full shade, 255 = open sky) over a Size² grid whose (0,0) corner is at
        /// (originX, originZ). toSun = unit direction to the light. Returns the trees splatted.
        /// </summary>
        internal static int Splat(float originX, float originZ, Vector3 toSun, byte[] outT, float[] cover)
        {
            Array.Clear(cover, 0, cover.Length);
            List<CanopyTree[]> cells;
            lock (s_lock) cells = new List<CanopyTree[]>(s_cells.Values);
            var flat = new Vector2(toSun.x, toSun.z);
            float horiz = flat.magnitude, tanE = horiz > 1e-4f ? Mathf.Max(0.05f, toSun.y / horiz) : 20f;
            Vector2 away = horiz > 1e-4f ? -flat / horiz : Vector2.zero;   // shade falls away from the sun
            float inv = 1f / Texel, area = Texel * Texel;
            int n = 0;
            foreach (var cell in cells)
                foreach (var t in cell)
                {
                    float crownH = t.Height * 0.6f, r = Mathf.Max(0.5f, t.CrownRadius);
                    float off = Mathf.Min(MaxOffset, crownH / tanE);
                    float cx = (t.Ground.x + away.x * off - originX) * inv, cz = (t.Ground.z + away.y * off - originZ) * inv;
                    if (cx < -2 || cz < -2 || cx > Size + 2 || cz > Size + 2) continue;
                    float rt = r * inv, amount = Mathf.PI * r * r / area * Opacity;
                    if (rt <= 0.75f)
                    {
                        // smaller than a texel: bilinear deposit, so the shade has no blocky steps
                        int x0 = Mathf.FloorToInt(cx - 0.5f), z0 = Mathf.FloorToInt(cz - 0.5f);
                        float fx = cx - 0.5f - x0, fz = cz - 0.5f - z0;
                        Add(cover, x0, z0, amount * (1 - fx) * (1 - fz)); Add(cover, x0 + 1, z0, amount * fx * (1 - fz));
                        Add(cover, x0, z0 + 1, amount * (1 - fx) * fz); Add(cover, x0 + 1, z0 + 1, amount * fx * fz);
                    }
                    else
                    {
                        int xa = Mathf.FloorToInt(cx - rt), xb = Mathf.CeilToInt(cx + rt), za = Mathf.FloorToInt(cz - rt), zb = Mathf.CeilToInt(cz + rt);
                        float per = Opacity, r2 = rt * rt;
                        for (int z = za; z <= zb; z++) for (int x = xa; x <= xb; x++)
                        {
                            float dx = x + 0.5f - cx, dz = z + 0.5f - cz;
                            if (dx * dx + dz * dz <= r2) Add(cover, x, z, per);
                        }
                    }
                    n++;
                }
            for (int i = 0; i < cover.Length; i++) outT[i] = (byte)Mathf.RoundToInt(255f * Mathf.Exp(-Absorb * cover[i]));
            return n;
        }

        private static void Add(float[] cover, int x, int z, float v) { if ((uint)x < Size && (uint)z < Size) cover[x + z * Size] += v; }
    }
}
