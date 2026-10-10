using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Verbatim world-edit overlays authored in the WorldGen Studio. A ".fedit"
    // is a small region patch (height + water surface, per-cell edit flags) keyed
    // to a seed. At the END of generation — after all procedural seeding/
    // reconciliation but before the grid is packed for runtime — each patch is
    // STAMPED onto the base 2m grid, so exactly what you sculpted appears in the
    // real world. Untouched cells already equal the gen, so patch edges are
    // seamless; the edit mask holds the stamped height against detail noise.
    internal static class FiresWorldEdits
    {
        const uint MAGIC = 0x46444557;   // "WDF" — Fires world-edit binary

        sealed class Patch
        {
            public int seed;
            public float centerX, centerZ, cellM;
            public int vN, minGx, minGz, W, H;
            public float[] h, wl;
            public byte[] e;   // bit0 = set height, bit1 = set water, bit2 = set biome
            public byte[] b;   // biome codes (v2+), null for v1 patches
        }

        static byte[] _editMask;   // n*n, 1 where a height was stamped (kills detail noise there)
        internal static byte[] EditMask => _editMask;

        internal static void Clear() { _editMask = null; }

        static string EditsDir()
            => Path.Combine(FiresCore.Storage.FiresConfigPaths.Cache, "WorldEdits");

        // Stamp every patch authored for this seed onto the base fields. Called
        // from OnWorldInitialize after AuditWater(), before PackI16.
        internal static void Apply(int seed, float[] grid, float[] water, byte[] biome, int n, float half)
        {
            _editMask = null;
            string dir = EditsDir();
            List<Patch> patches;
            try
            {
                if (!Directory.Exists(dir)) return;
                patches = new List<Patch>();
                foreach (string f in Directory.GetFiles(dir, "*.fedit"))
                {
                    var p = Read(f);
                    if (p != null && (p.seed == seed || p.seed == 0)) patches.Add(p);   // seed 0 = apply to any world
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresWorldEdits] scan failed: {ex.Message}"); return; }
            if (patches.Count == 0) return;

            var mask = new byte[n * n];
            float cell = (2f * half) / (n - 1);
            long sH = 0, sW = 0, sB = 0;
            foreach (var p in patches)
            {
                // World coord of the patch's (minGx,minGz) corner cell.
                float wx0 = p.centerX + (p.minGx - (p.vN - 1) * 0.5f) * p.cellM;
                float wz0 = p.centerZ + (p.minGz - (p.vN - 1) * 0.5f) * p.cellM;
                float wx1 = wx0 + (p.W - 1) * p.cellM, wz1 = wz0 + (p.H - 1) * p.cellM;
                // Base-grid cell range covering the patch bbox.
                int bx0 = Mathf.Clamp(Mathf.FloorToInt((wx0 + half) / cell), 0, n - 1);
                int bx1 = Mathf.Clamp(Mathf.CeilToInt((wx1 + half) / cell), 0, n - 1);
                int bz0 = Mathf.Clamp(Mathf.FloorToInt((wz0 + half) / cell), 0, n - 1);
                int bz1 = Mathf.Clamp(Mathf.CeilToInt((wz1 + half) / cell), 0, n - 1);
                for (int bz = bz0; bz <= bz1; bz++)
                {
                    float wz = -half + bz * cell;
                    float pfz = (wz - wz0) / p.cellM;
                    int pz = Mathf.RoundToInt(pfz);
                    if (pz < 0 || pz >= p.H) continue;
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        float wx = -half + bx * cell;
                        float pfx = (wx - wx0) / p.cellM;
                        int px = Mathf.RoundToInt(pfx);
                        if (px < 0 || px >= p.W) continue;
                        int pi = pz * p.W + px; byte e = p.e[pi];
                        if (e == 0) continue;
                        int bi = bz * n + bx;
                        if ((e & 1) != 0) { grid[bi] = p.h[pi]; mask[bi] = 1; sH++; }
                        if ((e & 2) != 0 && water != null)
                        {
                            float wl = p.wl[pi];
                            water[bi] = wl > 0f ? wl : grid[bi];   // surface, or ground = dry
                            sW++;
                        }
                        if ((e & 4) != 0 && biome != null && p.b != null) { biome[bi] = p.b[pi]; sB++; }
                    }
                }
            }
            _editMask = mask;
            FiresLogger.LogInfo($"[FiresWorldEdits] stamped {patches.Count} patch(es) for seed {seed}: {sH:N0} height + {sW:N0} water + {sB:N0} biome cells.");
        }

        static Patch Read(string file)
        {
            try
            {
                using var fs = File.OpenRead(file);
                using var br = new BinaryReader(fs);
                if (br.ReadUInt32() != MAGIC) { FiresLogger.LogWarning($"[FiresWorldEdits] {Path.GetFileName(file)}: bad magic — skipped."); return null; }
                int ver = br.ReadInt32();
                var p = new Patch
                {
                    seed = br.ReadInt32(),
                    centerX = br.ReadSingle(), centerZ = br.ReadSingle(), cellM = br.ReadSingle(),
                    vN = br.ReadInt32(),
                    minGx = br.ReadInt32(), minGz = br.ReadInt32(), W = br.ReadInt32(), H = br.ReadInt32()
                };
                int c = p.W * p.H;
                if (c <= 0 || c > 60_000_000) { FiresLogger.LogWarning($"[FiresWorldEdits] {Path.GetFileName(file)}: implausible size {p.W}x{p.H} — skipped."); return null; }
                p.h = new float[c]; p.wl = new float[c];
                var hb = br.ReadBytes(c * 4); Buffer.BlockCopy(hb, 0, p.h, 0, hb.Length);
                var wb = br.ReadBytes(c * 4); Buffer.BlockCopy(wb, 0, p.wl, 0, wb.Length);
                p.e = br.ReadBytes(c);
                if (ver >= 2) p.b = br.ReadBytes(c);   // v2+ biome channel
                FiresLogger.LogInfo($"[FiresWorldEdits] loaded {Path.GetFileName(file)}: seed {p.seed}, {p.W}x{p.H} @ ({p.centerX:F0},{p.centerZ:F0}) {p.cellM:F2}m/cell{(p.b != null ? " +biome" : "")}.");
                return p;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresWorldEdits] {Path.GetFileName(file)}: read failed — {ex.Message}"); return null; }
        }
    }
}
