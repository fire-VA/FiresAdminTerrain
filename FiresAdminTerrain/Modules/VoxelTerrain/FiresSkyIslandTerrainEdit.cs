using System;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // A TERRAIN OP AIMED AT A SKY ISLAND EDITS THAT ISLAND, AND ONLY THAT ISLAND (Fire, 10-05: "level and raise and slope etc
    // should ONLY effect the sky island not the terrain below it").
    //
    // Every height op in the game lands on the GROUND zone's TerrainComp: vanilla's hoe/cultivator (TerrainOp) and World Edit
    // Commands' `terrain` command, which Infinity Hammer's level/raise/slope tools run with the ghost's Y. Over an island that Y
    // is the island's altitude, so a level wrote (island Y - ground height), some 200 m, as a delta into the ground below, and
    // the ground swallowed the island. The island itself is voxel density, built from its shape, with brush edits saved per
    // chunk anchor like any V-tool stroke.
    //
    // So an op whose point is on an island (FiresSkyIslands.IsOnIsland) is done here instead, with voxel brushes in that
    // island's own columns: a grid of small cylinders over the op's circle, each moving the island's live surface to the op's
    // target there (fill up to it, cut down to it), never more than MaxStep, never closer than UndersideKeep to the island's
    // underside, and never outside the island (a column the island does not cover is left alone - no blobs in the air, no
    // ground). The ground's TerrainComp is not touched. One line per op at Debug:
    //   [FiresSkyIslands] terrain op on a sky island at (x,y,z) via <route>: <op> r=R -> island voxels only: ...
    //
    // KeepUnderIsland is the other half: a ground op under an island (a raise on a hill below it) can lift the ground the
    // voxel columns are built on into the island's union band. The ground a column is built on never rises past the island's
    // underside - GroundGap, unless the generated ground already stands there.
    internal static class FiresSkyIslandTerrainEdit
    {
        private const float StampRadius = 2f, StampSpacing = 2f;
        private const float SurfaceReach = 12f;
        private const float MaxStep = 16f;
        private const float MinStep = 0.05f;
        private const float BedOverlap = 1f;
        private const float UndersideKeep = 1.5f;
        private const float MaxRadius = 40f;

        internal static bool Targets(Vector3 point)
            => VoxelZoneStreamer.IsVoxelWorldActive && FiresSkyIslands.Enabled
               && (FiresSkyIslands.IsOnIslandExact(point) || AtIslandHeightBeside(point));

        // 0.2.737 (Run C 10-05, Fire's "level and slope tools drag up the land below the sky islands"): an op at an island's
        // height NEXT to one is an island op too, never a ground op. Run C's ops stood at y 204.33 beside a skyland whose
        // recorded underside there was 207.67 (the voxel body reaches lower than its shape's bottom): IsOnIsland said no, the
        // op went to the ground 224 m below, and level pulled that ground up to 204.33. Now such an op is reshaped as island
        // voxels where the island covers a column and does nothing elsewhere; the ground far below is never written.
        private const float BesideReach = 16f, BesideBelow = 12f, AboveGround = 30f;

        private static bool AtIslandHeightBeside(Vector3 point)
        {
            if (!FiresSkyIslands.TryLowestBottomNear(point, BesideReach, out float bottom) || point.y < bottom - BesideBelow) return false;
            return point.y > VoxelZoneStreamer.GeneratedGround(point.x, point.z) + AboveGround;
        }

        // target(dx, dz, surface) = the height that column ends at (NaN: leave it). The line logged, also returned.
        internal static string Reshape(string op, string route, Vector3 center, float radius, Func<float, float, float, float> target)
        {
            float r = Mathf.Clamp(radius, StampRadius, MaxRadius);
            int filled = 0, cut = 0, already = 0, offIsland = 0, unloaded = 0, refused = 0, threw = 0;
            string firstThrow = null;
            float half = StampSpacing * 0.5f;
            for (float dz = -r + half; dz < r; dz += StampSpacing)
                for (float dx = -r + half; dx < r; dx += StampSpacing)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    float x = center.x + dx, z = center.z + dz;
                    if (!FiresSkyIslands.TryExactBottomAt(x, z, out float bottom)) { offIsland++; continue; }
                    if (!VoxelWorld.TryFindSurfaceY(x, z, center.y, SurfaceReach, out float surface)) { unloaded++; continue; }
                    if (surface < bottom) { offIsland++; continue; }   // the surface found is the ground below, not the island
                    float t = target(dx, dz, surface);
                    if (float.IsNaN(t)) { already++; continue; }
                    t = Mathf.Max(Mathf.Clamp(t, surface - MaxStep, surface + MaxStep), bottom + UndersideKeep);
                    if (Mathf.Abs(t - surface) < MinStep) { already++; continue; }
                    bool fill = t > surface;
                    float lo = fill ? surface - BedOverlap : t, hi = fill ? t : surface + BedOverlap;
                    // The island body under a cut must be live voxels: a chunk the brush has to seed is seeded from the
                    // ground, which has no island in it, and would leave a hole through the island.
                    if (!VoxelWorld.TryGetChunk(KeyAt(x, lo, z), out _)) { unloaded++; continue; }
                    try
                    {
                        string done = VoxelWorld.ApplyBrush(new Vector3(x, (lo + hi) * 0.5f, z), StampRadius, hi - lo,
                            VoxelBrushShape.Cylinder, fill ? VoxelBrushMode.Fill : VoxelBrushMode.Carve);
                        if (done != null && done.StartsWith("Refused", StringComparison.Ordinal)) refused++;
                        else if (fill) filled++;
                        else cut++;
                    }
                    catch (Exception ex)
                    {
                        threw++;
                        if (firstThrow == null)
                        {
                            firstThrow = $"{ex.GetType().Name}: {ex.Message}";
                            FiresLogger.LogError($"[FiresSkyIslands] island terrain stamp threw at ({x:0.0},{z:0.0}): {ex}");
                        }
                    }
                }
            string line = $"[FiresSkyIslands] terrain op on a sky island at ({center.x:0.0},{center.y:0.0},{center.z:0.0}) via {route}: "
                + $"{op} r={r:0.#}{(radius > MaxRadius ? $" (asked {radius:0.#}, capped)" : "")} -> island voxels only: {filled} filled, "
                + $"{cut} cut, {already} already there, {offIsland} off the island, {unloaded} not loaded (skipped)"
                + (refused > 0 ? $", {refused} refused (closed zone)" : "")
                + (threw > 0 ? $", {threw} THREW (first: {firstThrow})" : "")
                + "; the ground below untouched";
            FiresLogger.LogDebug(line);
            return line;
        }

        // Back to the island's generated shape over a circle: every brush edit in its columns, from just under its underside
        // to MaxStep above the op, is restored (WEC's `reset`). Restore never seeds a chunk.
        internal static string Restore(string route, Vector3 center, float radius)
        {
            float r = Mathf.Clamp(radius, StampRadius, MaxRadius);
            float low = float.MaxValue;
            for (float dz = -r; dz <= r; dz += StampSpacing)
                for (float dx = -r; dx <= r; dx += StampSpacing)
                    if (dx * dx + dz * dz <= r * r && FiresSkyIslands.TryExactBottomAt(center.x + dx, center.z + dz, out float b)) low = Mathf.Min(low, b);
            if (low == float.MaxValue) low = center.y - MaxStep;
            float lo = low - BedOverlap, hi = center.y + MaxStep + BedOverlap;
            string done = VoxelWorld.ApplyBrush(new Vector3(center.x, (lo + hi) * 0.5f, center.z), r, hi - lo, VoxelBrushShape.Cylinder, VoxelBrushMode.Restore);
            string line = $"[FiresSkyIslands] terrain op on a sky island at ({center.x:0.0},{center.y:0.0},{center.z:0.0}) via {route}: "
                + $"reset r={r:0.#} -> island voxels only, restored {lo:0.#}-{hi:0.#} ({done}); the ground below untouched";
            FiresLogger.LogDebug(line);
            return line;
        }

        // Vanilla's hoe/cultivator op: level to the op's y (+ its level offset), then raise with its falloff. Smooth has no
        // voxel brush. Returns the lines.
        internal static string FromTerrainOp(TerrainOp.Settings s, Vector3 position)
        {
            string lines = null;
            if (s.m_level)
            {
                float level = position.y + s.m_levelOffset;
                lines = Reshape("level", "vanilla TerrainOp", position, s.m_levelRadius, (dx, dz, surface) => level);
            }
            if (s.m_raise)
            {
                float r = Mathf.Max(s.m_raiseRadius, StampRadius), delta = s.m_raiseDelta, power = Mathf.Max(s.m_raisePower, 0.01f);
                float cap = position.y + delta;
                string raise = Reshape(delta >= 0f ? "raise" : "lower", "vanilla TerrainOp", position, r, (dx, dz, surface) =>
                {
                    float w = Mathf.Pow(1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) / r), power);
                    float t = surface + delta * w;
                    return delta >= 0f ? Mathf.Min(t, Mathf.Max(cap, surface)) : Mathf.Max(t, Mathf.Min(cap, surface));
                });
                lines = lines == null ? raise : lines + "\n" + raise;
            }
            if (s.m_smooth)
            {
                string skip = $"[FiresSkyIslands] terrain op on a sky island at ({position.x:0.0},{position.y:0.0},{position.z:0.0}) via vanilla "
                    + $"TerrainOp: smooth r={s.m_smoothRadius:0.#} skipped (no voxel smooth); the ground below untouched";
                FiresLogger.LogDebug(skip);
                lines = lines == null ? skip : lines + "\n" + skip;
            }
            return lines;
        }

        private static Vector3Int KeyAt(float x, float y, float z)
            => new Vector3Int(Mathf.FloorToInt(x / 32f), Mathf.FloorToInt(y / 32f), Mathf.FloorToInt(z / 32f));

        // ── The ground under an island ──────────────────────────────────────────────────────────────
        // The island is unioned into a column only above ground + 8 m (VoxelChunk), so ground lifted to within 8 m of the
        // underside eats the island from below. GroundGap keeps a margin over that.
        private const float GroundGap = 10f;
        private const float ClampReportSeconds = 5f;
        private static int s_clamped;
        private static float s_worstCut, s_lastX, s_lastZ, s_nextReport;

        internal static float KeepUnderIsland(float wx, float wz, float ground)
        {
            if (!FiresSkyIslands.TryBottomAt(wx, wz, out float bottom)) return ground;
            float cap = bottom - GroundGap;
            if (ground <= cap) return ground;
            float keep = Mathf.Max(cap, VoxelZoneStreamer.GeneratedGround(wx, wz));   // generated ground is never moved
            if (ground <= keep) return ground;
            s_clamped++;
            if (ground - keep > s_worstCut) s_worstCut = ground - keep;
            s_lastX = wx;
            s_lastZ = wz;
            float now = Time.realtimeSinceStartup;
            if (now >= s_nextReport)
            {
                s_nextReport = now + ClampReportSeconds;
                FiresLogger.LogDebug($"[FiresSkyIslands] kept edited ground under a sky island: {s_clamped} column(s) held at the island's "
                    + $"underside - {GroundGap:0} m (largest cut-back {s_worstCut:0.0} m, last at ({s_lastX:0},{s_lastZ:0})); a ground op "
                    + "never reaches up into an island");
                s_clamped = 0;
                s_worstCut = 0f;
            }
            return keep;
        }
    }
}
