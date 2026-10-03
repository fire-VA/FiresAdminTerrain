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
    //   water   -> CustomWaterManager.CreateDiskOfType (through WaterOperationRPC to the server: anchor + cache, 0.2.681)
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
        // A click ON existing water (generated hydrology, a FAT pond, a lake or the sea: FatWaterChecks.TryWaterAt, at least
        // ExistingWaterDepth deep) adopts that water instead of digging a pool: its surface becomes the anchor and nothing is
        // carved or poured there, so two clicks on two waters cut and fill a river between them (Fire 10-01: "click on two
        // different water sources and automatically carve and fill a river between them").
        private const float ExistingWaterDepth = 0.3f;

        /// <summary>Whether the last Vpool click adopted existing water (river_carve_test reads it).</summary>
        internal static bool LastClickAdopted { get; private set; }

        public static void PoolClick(Vector3 surfacePos, float radius)
        {
            if (!Sane(surfacePos, radius, "Vpool")) return;
            var center = new Vector3(surfacePos.x, surfacePos.y + LevelOffset, surfacePos.z);

            LastClickAdopted = FatWaterChecks.TryWaterAt(surfacePos.x, surfacePos.z, ExistingWaterDepth, true, out float waterLevel, out float waterBed);
            if (LastClickAdopted) center.y = waterLevel;
            else ShapePool(center, radius, tiered: true);

            string what = LastClickAdopted
                ? $"existing water adopted at ({center.x:F0},{center.y:F1},{center.z:F0}), {waterLevel - waterBed:F1}m deep"
                : $"pool placed at ({center.x:F0},{center.y:F0},{center.z:F0}) r={radius:F1}";
            if (s_hasAnchor)
            {
                int segments = ConnectChannel(s_anchor, s_anchorRadius, center, radius);
                FiresLogger.LogInfo(
                    $"[FiresWater] {what}; connected from ({s_anchor.x:F0},{s_anchor.y:F1},{s_anchor.z:F0}) "
                    + $"| drop {s_anchor.y - center.y:F1}m, {segments} segment(s).");
            }
            else
            {
                FiresLogger.LogInfo($"[FiresWater] {what} — click downstream (a new spot or another water) to connect.");
            }

            s_anchor = center;
            s_anchorRadius = radius;
            s_hasAnchor = true;
        }

        // A pool on a hillside would leave the flat surface floating out of the
        // slope, so the lip is cleared the way a location pad flattens its site:
        // everything inside the ring above the waterline goes first, then the
        // bowl is dug under it.
        // A POOL (a click, tiered) is cut for life, from vanilla's own runtime bands (R47, 10-01; FiresClutterWaterEcology):
        // an outer bowl FishDepth deep (Fish1 spawns on a bed 1.5-3 m down) around an inner well WellDepth deep across
        // WellRatio of the radius (instanced_waterlilies needs a bed 3.2-4.5 m down, instanced_vass 3.1-4.0 m). A pool
        // smaller than TieredMinRadius, and every channel stamp, keeps the single bowl of before.
        private const float FishDepth = 2.2f, WellDepth = 3.8f, WellRatio = 0.55f, TieredMinRadius = 4f;

        private static void ShapePool(Vector3 center, float radius, bool tiered = false)
        {
            bool twoTier = tiered && radius >= TieredMinRadius;
            float depth = twoTier ? FishDepth : Mathf.Max(PoolMinDepth, radius * PoolDepthRatio);

            var lip = new Vector3(center.x, center.y + LevelClearHeight * 0.5f, center.z);
            VoxelWorld.ApplyBrush(lip, radius, LevelClearHeight, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);

            var bowl = new Vector3(center.x, center.y - depth * 0.5f, center.z);
            VoxelWorld.ApplyBrush(bowl, radius, depth, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
            if (twoTier)
            {
                var well = new Vector3(center.x, center.y - WellDepth * 0.5f, center.z);
                VoxelWorld.ApplyBrush(well, radius * WellRatio, WellDepth, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
                depth = WellDepth;
            }

            RefreshClutter(center, radius);
            QueueWater(center, radius, depth);
        }

        // 0.2.678 (H5, 10-01: "pool placed … r=6.0" yet the water read "no water", depth -43.7 m). The WaterMeshCombiner shapes
        // a disk to the terrain it samples when it builds (min of the generator height and ZoneSystem.GetGroundHeight, a
        // raycast onto the colliders), and the carve's colliders are rebuilt asynchronously, so a disk laid in the same frame
        // as its carve sat on the UNCARVED ground, about 0 m deep. The water now waits until a terrain ray at the pool centre
        // reaches the carved bed (below centre - depth/2), at most WaterWaitCap seconds, then is laid and logged.
        private const float WaterWaitMin = 0.3f, WaterWaitCap = 10f, BedProbeAbove = 50f, BedProbeLength = 100f;
        private static int s_terrainMask;

        private sealed class PendingWater { public Vector3 Center; public float Radius, Depth, QueuedAt; }
        private static readonly System.Collections.Generic.List<PendingWater> s_pendingWater = new System.Collections.Generic.List<PendingWater>();

        private static void QueueWater(Vector3 center, float radius, float depth)
            => s_pendingWater.Add(new PendingWater { Center = center, Radius = radius, Depth = depth, QueuedAt = Time.realtimeSinceStartup });

        private static void DrainPendingWater()
        {
            if (s_pendingWater.Count == 0) return;
            if (s_terrainMask == 0) s_terrainMask = LayerMask.GetMask("terrain");
            float now = Time.realtimeSinceStartup;
            for (int i = s_pendingWater.Count - 1; i >= 0; i--)
            {
                var w = s_pendingWater[i];
                float waited = now - w.QueuedAt;
                if (waited < WaterWaitMin) continue;
                float bedLine = w.Center.y - w.Depth * 0.5f;
                float bed = float.NaN;
                if (Physics.Raycast(new Vector3(w.Center.x, w.Center.y + BedProbeAbove, w.Center.z), Vector3.down, out RaycastHit hit, BedProbeLength, s_terrainMask))
                    bed = hit.point.y;
                bool seen = !float.IsNaN(bed) && bed <= bedLine;
                if (!seen && waited < WaterWaitCap) continue;
                s_pendingWater.RemoveAt(i);
                LayWater(w.Center, w.Radius, w.Depth);
                FiresLogger.LogInfo($"[FiresWater] pool water laid at ({w.Center.x:F0},{w.Center.y:F1},{w.Center.z:F0}) r={w.Radius:F1} d={w.Depth:F1} "
                    + (seen ? $"after {waited:F1} s (bed seen at y {bed:F1})"
                            : $"after the {WaterWaitCap:F0} s cap, bed still at y {(float.IsNaN(bed) ? "none" : bed.ToString("F1"))} (wanted <= {bedLine:F1})"));
            }
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
            DrainPendingWater();
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
