using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    // The shaping half of the river / pour / pool tools. VoxelBrushDrag owns the
    // cursor, the scroll radius and the stroke pacing; everything that actually
    // changes the world lives here, and every edit is routed through the existing
    // authority for its data so it persists and replicates exactly as a console
    // command's would:
    //   ground  -> VoxelWorld.ApplyBrush            (VoxelChunkAnchor ZDO edit blob)
    //   water   -> CustomWaterManager.CreateDiskOfType (combiner anchor + cache)
    //   clutter -> ClutterSystem.ResetGrass         (client-local by design)
    //
    // Clutter is deliberately the one thing NOT synced: vanilla regenerates it
    // from paint/biome/height, and FiresClutterWaterEcology + FiresVegWaterEcology
    // already retarget vanilla's sea-level constant to the local water surface.
    // So shaping the bed and laying the surface is enough for vass, reeds and
    // lilies to appear correctly on every client, at zero network cost.
    //
    // NOTHING IS PAINTED, and both halves of that are deliberate:
    //   * There is no sand to paint. Vanilla's PaintTypes are Dirt / Cultivate /
    //     Paved / Reset / ClearVegetation / DeepSnow — no sand among them. Shore
    //     sand is the terrain shader blending by height against the water level,
    //     so carving the bed below the surface IS the sand.
    //   * ClearVegetation cannot work from here anyway (see RefreshClutter), and
    //     scarring the banks is the opposite of the goal: vanilla clutter around
    //     the water is what we are after.
    //
    // DIAGNOSTICS. ApplyBrush already reports its own failures loudly (it warns
    // with a density probe when a stroke changed nothing, and errors when the
    // chunk cap refuses chunks, i.e. a carve with no floor), so it is not wrapped
    // here. The call that CAN fail silently is water placement, so a null disk is
    // reported. Per-dab failures are throttled, because a drag fires many dabs a
    // second and an unthrottled warning would bury the log.
    internal static class WaterShapingTools
    {
        private const float RiverDefaultDepth = 2.5f;
        private const float RiverMinDepth = 0.5f;
        private const float RiverMaxDepth = 32f;
        private const float PourDepth = 3f;
        private const float PoolDepthRatio = 0.35f;
        private const float PoolMinDepth = 1.5f;
        private const float ClutterRefreshMargin = 1.25f;
        private const float LevelClearHeight = 12f;
        private const float ChannelSpacingRatio = 0.5f;
        private const float ChannelMinSpacing = 2f;
        private const int ChannelMaxSteps = 96;
        private const float WarnThrottleSeconds = 3f;
        private const float ClutterFlushSeconds = 0.25f;

        // Signed Y offset driven by PgUp/PgDn, shared by all three tools and
        // always in the same direction: PgUp raises, PgDn lowers.
        public static float LevelOffset;

        private static bool s_hasAnchor;
        private static Vector3 s_anchor;
        private static float s_anchorRadius;

        private static float s_lastWaterWarn = -100f;
        private static float s_lastClutterWarn = -100f;

        private static bool s_clutterPending;
        private static Vector3 s_clutterCenter;
        private static float s_clutterRadius;
        private static float s_lastClutterFlush = -100f;

        public static bool TryGetAnchor(out Vector3 position, out float radius)
        {
            position = s_anchor;
            radius = s_anchorRadius;
            return s_hasAnchor;
        }

        public static void ClearAnchor()
        {
            if (!s_hasAnchor) return;
            s_hasAnchor = false;
            FiresLogger.LogInfo("[FiresWater] pool chain ended — next click starts a new run.");
        }

        // ── Vriver — carve the bed; the ecology patch handles what grows ────
        public static void RiverDab(Vector3 surfacePos, float radius)
        {
            if (!Sane(surfacePos, radius, "Vriver")) return;
            // The offset reads as bed depth here, in the same direction as the
            // other two: PgDn sinks the bed, PgUp shallows it. Clamping rather
            // than Max() so PgUp is not silently inert once it passes zero.
            float depth = Mathf.Clamp(RiverDefaultDepth - LevelOffset, RiverMinDepth, RiverMaxDepth);
            var bed = new Vector3(surfacePos.x, surfacePos.y - depth * 0.5f, surfacePos.z);
            VoxelWorld.ApplyBrush(bed, radius, depth, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
            RefreshClutter(surfacePos, radius);
        }

        // ── Vpour — lay the surface ribbon ──────────────────────────────────
        public static void PourDab(Vector3 surfacePos, float radius)
        {
            if (!Sane(surfacePos, radius, "Vpour")) return;
            var at = new Vector3(surfacePos.x, surfacePos.y + LevelOffset, surfacePos.z);
            LayWater(at, radius, PourDepth);
        }

        // ── Vpool — level, carve, fill, reseed; connect on the next click ──
        public static void PoolClick(Vector3 surfacePos, float radius)
        {
            if (!Sane(surfacePos, radius, "Vpool")) return;
            var center = new Vector3(surfacePos.x, surfacePos.y + LevelOffset, surfacePos.z);

            ShapePool(center, radius);

            if (s_hasAnchor)
            {
                int segments = ConnectChannel(s_anchor, s_anchorRadius, center, radius);
                FiresLogger.LogInfo(
                    $"[FiresWater] pool connected ({s_anchor.x:F0},{s_anchor.y:F0},{s_anchor.z:F0}) "
                    + $"-> ({center.x:F0},{center.y:F0},{center.z:F0}) | drop {s_anchor.y - center.y:F1}m, {segments} segment(s).");
            }
            else
            {
                FiresLogger.LogInfo(
                    $"[FiresWater] pool placed at ({center.x:F0},{center.y:F0},{center.z:F0}) r={radius:F1} "
                    + "— click downstream to connect the next one.");
            }

            s_anchor = center;
            s_anchorRadius = radius;
            s_hasAnchor = true;
        }

        // A pool on a hillside would leave the flat surface floating out of the
        // slope, so the lip is cleared the way a location pad flattens its site:
        // everything inside the ring above the waterline goes first, then the
        // bowl is dug under it.
        private static void ShapePool(Vector3 center, float radius)
        {
            float depth = Mathf.Max(PoolMinDepth, radius * PoolDepthRatio);

            var lip = new Vector3(center.x, center.y + LevelClearHeight * 0.5f, center.z);
            VoxelWorld.ApplyBrush(lip, radius, LevelClearHeight, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);

            var bowl = new Vector3(center.x, center.y - depth * 0.5f, center.z);
            VoxelWorld.ApplyBrush(bowl, radius, depth, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);

            RefreshClutter(center, radius);
            LayWater(center, radius, depth);
        }

        // Straight-line grade from one pool to the next: lerping the anchor Y
        // into the new centre Y is what makes the channel descend instead of
        // stepping, so a chain of pools reads as one run of water.
        private static int ConnectChannel(Vector3 from, float fromRadius, Vector3 to, float toRadius)
        {
            float span = Vector3.Distance(
                new Vector3(from.x, 0f, from.z), new Vector3(to.x, 0f, to.z));
            if (span < ChannelMinSpacing) return 0;

            float spacing = Mathf.Max(ChannelMinSpacing, Mathf.Min(fromRadius, toRadius) * ChannelSpacingRatio);
            int steps = Mathf.CeilToInt(span / spacing);
            if (steps > ChannelMaxSteps)
            {
                FiresLogger.LogWarning(
                    $"[FiresWater] pool connect spans {span:F0}m — capped at {ChannelMaxSteps} segments "
                    + $"(wanted {steps}); the channel will be coarse. Place an intermediate pool for a longer run.");
                steps = ChannelMaxSteps;
            }

            for (int i = 1; i < steps; i++)
            {
                float t = i / (float)steps;
                ShapePool(Vector3.Lerp(from, to, t), Mathf.Lerp(fromRadius, toRadius, t));
            }
            return steps;
        }

        private static void LayWater(Vector3 center, float radius, float depth)
        {
            WaterDisk disk = null;
            try { disk = CustomWaterManager.CreateDiskOfType(center, radius, depth, CustomLiquidType.Water); }
            catch (System.Exception ex)
            {
                FiresLogger.LogError(
                    $"[FiresWater] water placement THREW at ({center.x:F0},{center.y:F0},{center.z:F0}) "
                    + $"r={radius:F1} d={depth:F1}: {ex}");
                return;
            }
            if (disk != null) return;
            if (Time.realtimeSinceStartup - s_lastWaterWarn < WarnThrottleSeconds) return;
            s_lastWaterWarn = Time.realtimeSinceStartup;
            FiresLogger.LogWarning(
                $"[FiresWater] no water disk was created at ({center.x:F0},{center.y:F0},{center.z:F0}) "
                + $"r={radius:F1} d={depth:F1} — the bed is carved but the surface is missing.");
        }

        // Clutter is REGENERATED, not painted away.
        //
        // Two vanilla paint paths disagree about alpha, and the difference decides
        // this: Heightmap.Paint (generation-time) has an explicit
        //     if (paintType != ClearVegetation) color.a = a2;
        // so ClearVegetation may write alpha there, while TerrainComp.PaintCleared
        // — the runtime player-op path our MirrorPaint rides — restores alpha
        // unconditionally. Alpha IS the vegetation mask (Heightmap.GetVegetationMask
        // returns m_paintMask.a, and ClutterSystem filters clutter against it), so a
        // ClearVegetation paint through the runtime path is silently a no-op.
        //
        // We would not want it even if it worked. Clearing alpha scars the banks,
        // and the ask is vanilla clutter AROUND the water. Below the surface,
        // FiresClutterWaterEcology already retargets vanilla's hardcoded sea level
        // to the LOCAL water surface, so a regenerated patch drops grass and offers
        // vass and lilies on its own. Resetting the patch is the whole job.
        // Queued, never called straight through. ResetGrass walks EVERY loaded
        // patch and raises m_forceRebuild, so it costs the same whether one dab
        // or ninety-six ask for it — and one rebuild covers them all. A river
        // drag asks up to eight times a frame and a long pool connect once per
        // segment, so the region is accumulated here and flushed by Tick() at a
        // fixed interval instead.
        private static void RefreshClutter(Vector3 center, float radius)
        {
            float wanted = radius * ClutterRefreshMargin;
            if (!s_clutterPending)
            {
                s_clutterPending = true;
                s_clutterCenter = center;
                s_clutterRadius = wanted;
                return;
            }
            // Grow the pending region to cover both, so nothing queued is lost.
            float gap = Vector3.Distance(
                new Vector3(s_clutterCenter.x, 0f, s_clutterCenter.z), new Vector3(center.x, 0f, center.z));
            s_clutterCenter = Vector3.Lerp(s_clutterCenter, center, 0.5f);
            s_clutterRadius = Mathf.Max(s_clutterRadius, wanted) + gap * 0.5f;
        }

        // Driven from the tool pump so a queued refresh still lands when the
        // player stops dragging and nothing else calls in.
        public static void Tick()
        {
            if (!s_clutterPending) return;
            if (Time.realtimeSinceStartup - s_lastClutterFlush < ClutterFlushSeconds) return;

            s_lastClutterFlush = Time.realtimeSinceStartup;
            s_clutterPending = false;

            if (ClutterSystem.instance == null)
            {
                if (Time.realtimeSinceStartup - s_lastClutterWarn < WarnThrottleSeconds) return;
                s_lastClutterWarn = Time.realtimeSinceStartup;
                FiresLogger.LogWarning(
                    "[FiresWater] no ClutterSystem — the bed and surface are correct, but shore clutter "
                    + "will not refresh until the zone reloads.");
                return;
            }
            ClutterSystem.instance.ResetGrass(s_clutterCenter, s_clutterRadius);
        }

        // A NaN cursor or a degenerate radius would carve garbage into chunks and
        // persist it, so both are refused at the door rather than partway through.
        private static bool Sane(Vector3 pos, float radius, string tool)
        {
            if (radius > 0.05f && !float.IsNaN(pos.x) && !float.IsNaN(pos.y) && !float.IsNaN(pos.z)
                && !float.IsInfinity(pos.x) && !float.IsInfinity(pos.y) && !float.IsInfinity(pos.z))
                return true;

            FiresLogger.LogWarning(
                $"[FiresWater] {tool} refused a bad stroke — pos=({pos.x},{pos.y},{pos.z}) r={radius}.");
            return false;
        }
    }
}
