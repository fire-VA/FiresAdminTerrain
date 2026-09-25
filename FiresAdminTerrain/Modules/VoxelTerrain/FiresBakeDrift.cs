using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── BAKE DRIFT MEASUREMENT ──────────────────────────────────────────────
    // A baked cache stores the world as an n-grid of int16 heights and rebuilds
    // any point by bilinear interpolation of the 4 surrounding nodes. Vanilla's
    // heightmap has a vertex every metre, so baking a world that ALREADY EXISTS
    // re-samples the ground its structures were built on.
    //
    // This measures that, in metres, before anyone commits to a bake:
    //   drift(p) = | vanilla base height at p  -  grid reconstruction at p |
    //
    // Base height, not the live heightmap, is the right comparison: player
    // terraforming is stored as DELTAS on top of the base, so when the base
    // moves, a flattened build site moves with it by the same amount.
    //
    // It has to run in-game. Vanilla's terrain offsets come from
    // UnityEngine.Random seeded with the world seed (WorldGenerator..ctor), and
    // that is a native engine call — worldgen cannot be reproduced offline
    // without reimplementing Unity's RNG bit-exactly, which would risk measuring
    // a different world than the one you play.
    //
    // Resolution is a PARAMETER so a finer grid can simply be tried: the command
    // also prints what that resolution would cost in memory and on disk, since
    // the bake is one resident array per field for the whole world.
    internal static class FiresBakeDrift
    {
        private const string CommandName = "fires_bake_drift";
        private const float PACK = 46f;
        private const int DefaultSpan = 192;
        private const int MaxSpan = 1024;
        private const float FlatSlope = 0.10f;   // build-suitable ground

        private static bool s_registered;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(CommandName,
                "Measure how far a baked terrain cache would move this world's ground. Usage: "
                + CommandName + " [resolution] [span] — resolution defaults to the configured bake grid, "
                + "span to " + DefaultSpan + "m around you. Reports drift in metres overall, on flat "
                + "(build-suitable) ground, and at every placed structure in range.",
                args =>
                {
                    int res = (int)Parse(args, 1, FiresVoxelConfig.WorldGenResolution?.Value ?? 10501);
                    int span = Mathf.Clamp((int)Parse(args, 2, DefaultSpan), 16, MaxSpan);
                    foreach (var line in Run(res, span)) args.Context.AddString(line);
                }, isCheat: true);
        }

        private static float Parse(Terminal.ConsoleEventArgs args, int i, float fallback)
            => args.Length > i && float.TryParse(args[i], out float v) ? v : fallback;

        private static List<string> Run(int res, int span)
        {
            var lines = new List<string>();
            var wg = WorldGenerator.instance;
            var player = Player.m_localPlayer;
            if (wg == null || player == null)
            {
                lines.Add("[BakeDrift] Must be in a world.");
                return lines;
            }
            if (res < 2) { lines.Add("[BakeDrift] Resolution must be at least 2."); return lines; }

            float half = FiresWorldGenBase.GridHalf;
            float cell = (2f * half) / (res - 1);
            Vector3 centre = player.transform.position;
            float x0 = centre.x - span * 0.5f, z0 = centre.z - span * 0.5f;

            lines.Add($"[BakeDrift] grid {res} over +/-{half:0}m = {cell:0.###}m cells "
                      + $"(vanilla heightmap is 1m), int16 step {1000f / PACK:0.#}mm, truncating.");

            // Node heights are shared between neighbouring samples; memoise so a
            // 192m span costs ~9k generator calls instead of ~150k.
            var nodes = new Dictionary<long, float>(16384);
            float Node(int gx, int gz)
            {
                long key = ((long)gx << 32) ^ (uint)gz;
                if (nodes.TryGetValue(key, out float v)) return v;
                float h = wg.GetHeight(gx * cell - half, gz * cell - half);
                float q = h * PACK;
                short s = q >= 32767f ? (short)32767 : q <= -32768f ? (short)-32768 : (short)q;
                v = s / PACK;
                nodes[key] = v;
                return v;
            }

            float Reconstruct(float wx, float wz)
            {
                float gx = (wx + half) / cell, gz = (wz + half) / cell;
                if (gx < 0f) gx = 0f; else if (gx > res - 1.001f) gx = res - 1.001f;
                if (gz < 0f) gz = 0f; else if (gz > res - 1.001f) gz = res - 1.001f;
                int xi = (int)gx, zi = (int)gz;
                float fx = gx - xi, fz = gz - zi;
                return Node(xi, zi) * (1f - fx) * (1f - fz) + Node(xi + 1, zi) * fx * (1f - fz)
                     + Node(xi, zi + 1) * (1f - fx) * fz + Node(xi + 1, zi + 1) * fx * fz;
            }

            // Dense 1m field with a skirt, so slope can classify each sample.
            int d = span + 3;
            var exact = new float[d * d];
            for (int j = 0; j < d; j++)
                for (int i = 0; i < d; i++)
                    exact[j * d + i] = wg.GetHeight(x0 + i - 1, z0 + j - 1);

            var all = new List<float>(span * span);
            var flat = new List<float>();
            for (int j = 1; j < d - 1; j++)
            {
                for (int i = 1; i < d - 1; i++)
                {
                    float wx = x0 + i - 1, wz = z0 + j - 1;
                    float err = Mathf.Abs(exact[j * d + i] - Reconstruct(wx, wz));
                    all.Add(err);
                    float slope = Mathf.Max(
                        Mathf.Abs(exact[j * d + i + 1] - exact[j * d + i - 1]) * 0.5f,
                        Mathf.Abs(exact[(j + 1) * d + i] - exact[(j - 1) * d + i]) * 0.5f);
                    if (slope < FlatSlope) flat.Add(err);
                }
            }

            all.Sort(); flat.Sort();
            lines.Add($"[BakeDrift] GROUND  n={all.Count:N0}  mean {Mean(all):0.000}m  p50 {Pct(all, 0.50f):0.000}  "
                      + $"p95 {Pct(all, 0.95f):0.000}  p99 {Pct(all, 0.99f):0.000}  max {Pct(all, 1f):0.000}");
            lines.Add($"[BakeDrift] FLAT    n={flat.Count:N0} ({(all.Count == 0 ? 0f : 100f * flat.Count / all.Count):0.0}%)  "
                      + $"mean {Mean(flat):0.000}m  p95 {Pct(flat, 0.95f):0.000}  max {Pct(flat, 1f):0.000}   <- build-suitable ground");

            AppendStructureDrift(lines, centre, span, Reconstruct, wg);
            AppendCost(lines, res);
            return lines;
        }

        // The number that actually decides it: how far the ground moves under
        // things that are already built.
        private static void AppendStructureDrift(List<string> lines, Vector3 centre, int span,
                                                 Func<float, float, float> reconstruct, WorldGenerator wg)
        {
            var pieces = AllPieces();
            if (pieces == null)
            {
                lines.Add("[BakeDrift] STRUCTURES: piece list unavailable on this build — ground figures only.");
                return;
            }

            float reach = span * 0.5f;
            var errs = new List<float>();
            float worst = 0f; Vector3 worstAt = Vector3.zero; string worstName = null;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;
                var p = piece.transform.position;
                if (Mathf.Abs(p.x - centre.x) > reach || Mathf.Abs(p.z - centre.z) > reach) continue;
                float err = Mathf.Abs(wg.GetHeight(p.x, p.z) - reconstruct(p.x, p.z));
                errs.Add(err);
                if (err > worst) { worst = err; worstAt = p; worstName = piece.m_name; }
            }

            if (errs.Count == 0)
            {
                lines.Add($"[BakeDrift] STRUCTURES: none within {reach:0}m — stand in a base and run it again.");
                return;
            }
            errs.Sort();
            lines.Add($"[BakeDrift] BUILT    n={errs.Count:N0} piece(s)  mean {Mean(errs):0.000}m  p95 {Pct(errs, 0.95f):0.000}  "
                      + $"max {Pct(errs, 1f):0.000}");
            lines.Add($"[BakeDrift] worst piece '{worstName}' at ({worstAt.x:0},{worstAt.y:0},{worstAt.z:0}) would move {worst:0.000}m.");
        }

        private static void AppendCost(List<string> lines, int res)
        {
            double cells = (double)res * res;
            double packedMb = cells * 2 / (1024 * 1024);          // int16 resident, per field
            double floatMb = cells * 4 / (1024 * 1024);           // float array the generator builds first
            lines.Add($"[BakeDrift] cost at {res}: {cells / 1e6:0.#}M cells/field — {packedMb:0} MB resident (int16) "
                      + $"and {floatMb:0} MB as the float array it generates from, PER FIELD "
                      + "(heights, water, biome, river mask).");
            int clamp = 12000;
            if (res > clamp)
                lines.Add($"[BakeDrift] NOTE: the loader clamps resolution to {clamp} (Mathf.Clamp in OnWorldInitialize), "
                          + $"so {res} is a measurement only until that clamp is raised.");
        }

        // Piece.s_allPieces is private, and a publicized compile does not grant
        // runtime access. StaticFieldRefAccess(Type, name) returns the VALUE here,
        // not a ref delegate.
        private static List<Piece> AllPieces()
        {
            try { return AccessTools.StaticFieldRefAccess<List<Piece>>(typeof(Piece), "s_allPieces"); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[BakeDrift] could not read Piece.s_allPieces: {ex.Message}");
                return null;
            }
        }

        private static float Mean(List<float> v)
        {
            if (v.Count == 0) return 0f;
            double s = 0; for (int i = 0; i < v.Count; i++) s += v[i];
            return (float)(s / v.Count);
        }

        private static float Pct(List<float> sorted, float q)
        {
            if (sorted.Count == 0) return 0f;
            int i = Mathf.Clamp(Mathf.RoundToInt(q * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[i];
        }
    }
}
