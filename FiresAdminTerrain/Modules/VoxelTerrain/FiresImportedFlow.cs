using System;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══ AN IMPORTED WORLD'S AUTHORED FLOW FIELD ═════════════════════════════
    // The water mesh normally works out which way a river runs from the water
    // SURFACE gradient. That is sound on a generated world, where the surface
    // descends visibly along each run — but a river surface is deliberately
    // near-flat (monotone, gently graded), so on a DEM import the gradient is
    // dominated by noise: flow points the wrong way and changes per quad, which
    // reads as little squares of current shearing against each other.
    //
    // Gaea already solved the flow and exports it. The importer samples that
    // field and the bake writes it beside the cache as `*.flow`; this reads it
    // back. Absent file = nothing changes and the gradient still decides, so a
    // generated world is untouched.
    //
    // Two signed bytes per cell on a coarse grid — flow is smooth, so 2048² over
    // a 20km world (~10m) is ample and costs 8MB. Direction only: magnitude
    // still comes from the local surface, which is what varies run to run.
    internal static class FiresImportedFlow
    {
        private static sbyte[] s_xz;
        private static int s_n;
        private static float s_half, s_cell;

        internal static bool Active => s_xz != null;

        internal static void Clear() { s_xz = null; s_n = 0; }

        // Sidecar for a baked cache: same path, ".flow" instead of ".bin".
        internal static void TryLoadFor(string cachePath) => TryLoad(Path.ChangeExtension(cachePath, ".flow"));

        // A flow field at its own path (a client's copy of the server's, from the join sync); none when null or absent.
        internal static void TryLoad(string p)
        {
            Clear();
            try
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return;
                using (var br = new BinaryReader(File.OpenRead(p)))
                {
                    if (br.ReadUInt32() != 0x574F4C46u) return;   // "FLOW"
                    int n = br.ReadInt32();
                    float half = br.ReadSingle();
                    if (n < 16 || n > 16384 || half < 100f) return;
                    var xz = br.ReadBytes(n * n * 2);
                    if (xz.Length != n * n * 2) return;
                    var s = new sbyte[xz.Length];
                    Buffer.BlockCopy(xz, 0, s, 0, xz.Length);
                    s_xz = s; s_n = n; s_half = half; s_cell = (2f * half) / n;
                }
                FiresLogger.LogInfo($"[FiresVoxelWater] authored flow field loaded: {s_n}² @ {s_cell:0.#}m — " +
                          "imported rivers run the way the source says, not the way the surface gradient guesses.");
            }
            catch (Exception ex)
            {
                Clear();
                FiresLogger.LogWarning($"[FiresVoxelWater] flow sidecar unreadable ({ex.Message}) — falling back to the surface gradient.");
            }
        }

        // Unit direction at a world position, or false where nothing was authored
        // (off-river, or a cell whose channel carried no step). Bilinear across
        // the coarse grid so the current turns smoothly instead of per cell —
        // the per-quad patching is exactly what this replaces.
        internal static bool TryGetDir(float wx, float wz, out Vector2 dir)
        {
            dir = Vector2.zero;
            var a = s_xz;
            if (a == null) return false;
            float fx = (wx + s_half) / s_cell, fz = (wz + s_half) / s_cell;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            if (x0 < 0 || z0 < 0 || x0 >= s_n - 1 || z0 >= s_n - 1) return false;
            float tx = fx - x0, tz = fz - z0;
            float sx = 0f, sz = 0f, wsum = 0f;
            for (int oz = 0; oz <= 1; oz++)
                for (int ox = 0; ox <= 1; ox++)
                {
                    int i = ((z0 + oz) * s_n + (x0 + ox)) * 2;
                    float dx = a[i] / 127f, dz = a[i + 1] / 127f;
                    if (dx == 0f && dz == 0f) continue;          // nothing authored here
                    float w = (ox == 0 ? 1f - tx : tx) * (oz == 0 ? 1f - tz : tz);
                    sx += dx * w; sz += dz * w; wsum += w;
                }
            if (wsum < 1e-4f) return false;
            var v = new Vector2(sx, sz);
            float m = v.magnitude;
            if (m < 1e-4f) return false;
            dir = v / m;
            return true;
        }
    }
}
