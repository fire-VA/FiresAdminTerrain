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
        private const float PoolAdoptReach = 6f;   // m between the clicked ground and existing water's surface

        /// <summary>Whether the last Vpool click adopted existing water (river_carve_test reads it).</summary>
        internal static bool LastClickAdopted { get; private set; }

        public static void PoolClick(Vector3 surfacePos, float radius)
        {
            if (!Sane(surfacePos, radius, "Vpool")) return;
            var center = new Vector3(surfacePos.x, surfacePos.y + LevelOffset, surfacePos.z);

            // 0.2.712 (Fire 10-05: our own rivers and pools on the sky islands): the water check asks by XZ, so a click on an
            // island over a lake used to adopt the lake 500 m below. Only water near the clicked height is adopted.
            LastClickAdopted = FatWaterChecks.TryWaterAt(surfacePos.x, surfacePos.z, ExistingWaterDepth, true, out float waterLevel, out float waterBed)
                && Mathf.Abs(waterLevel - surfacePos.y) <= PoolAdoptReach;
            if (LastClickAdopted) center.y = waterLevel;
            else ShapePool(center, radius, tiered: true);

            string what = LastClickAdopted
                ? $"existing water adopted at ({center.x:F0},{center.y:F1},{center.z:F0}), {waterLevel - waterBed:F1}m deep"
                : $"pool placed at ({center.x:F0},{center.y:F0},{center.z:F0}) r={radius:F1}";
            // 0.2.749 (Fire 10-05: a pond over a cliff or skyland edge turns the overhang into a waterfall): a pool dug at an edge
            // hangs a fall from each lip its circle crosses.
            if (!LastClickAdopted)
            {
                string falls = PondTool.EdgeFalls(center, center.y, radius + EdgeLook);   // just outside its own carve
                if (falls != null) what += "; " + falls;
            }
            Chain(center, radius, what);
        }

        // The two-click river's memory, shared by Vpool and Vpond (0.2.749; Fire 10-05: "we want the pond connector tool to work,
        // so that we can place two ponds and link them and a river carve and fill auto connects them"): with an anchor from the
        // last click (AnchorStaleSeconds at most), carve and fill the routed channel from it; either way this water is the next anchor.
        private const float AnchorStaleSeconds = 300f;
        private const float EdgeLook = 0.75f;   // a pool's edge check reads this far outside its carve
        private const float EdgeDrop = 3f;      // channel route ground this far under the bed line = past an edge: a fall
        private static float s_anchorTime = -1000f;

        internal static void Chain(Vector3 center, float radius, string what)
        {
            if (s_hasAnchor && Time.time - s_anchorTime > AnchorStaleSeconds)
            {
                s_hasAnchor = false;
                FiresLogger.LogDebug($"[FiresWater] pool chain anchor older than {AnchorStaleSeconds:0} s dropped: this click starts a new run.");
            }
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
            s_anchorTime = Time.time;
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

        // Vfall's landing (0.2.715): the bowl and its water only, nothing cleared above, so the cliff behind it stands.
        internal static void PlungePool(Vector3 center, float radius)
        {
            if (!Sane(center, radius, "Vfall pool")) return;
            float depth = Mathf.Max(PoolMinDepth, radius * PoolDepthRatio);
            VoxelWorld.ApplyBrush(new Vector3(center.x, center.y - depth * 0.5f, center.z), radius, depth, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
            RefreshClutter(center, radius);
            QueueWater(center, radius, depth);
        }

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

        // The two-click river (Fire 10-01 / 10-05: "click one water source and then click a second water source and have a river
        // auto carve between the two of them"): Vpool's second click connects to the first.
        // 0.2.715: the channel is ROUTED, not a straight line. From A it steps toward B one spacing at a time, taking of five
        // headings (straight, 30 and 60 degrees either side, each one still closing on B) the one over the lowest ground, so it
        // follows a valley instead of trenching through a hill. The bed then falls at one fixed grade from A's surface to B's
        // along the route's own length (a chain of pools reads as one run of water). B higher than A is refused nothing but
        // warned: water laid on a climbing grade does not run uphill. Ground is the voxel surface near each step's height, so
        // a river between two pools on a sky island stays on that island.
        private const float RouteTurnPenalty = 0.6f;   // m of ground a 60-degree turn must save to be taken
        private static readonly float[] RouteHeadings = { 0f, 30f, -30f, 60f, -60f };

        private static int ConnectChannel(Vector3 from, float fromRadius, Vector3 to, float toRadius)
        {
            float span = Vector3.Distance(
                new Vector3(from.x, 0f, from.z), new Vector3(to.x, 0f, to.z));
            if (span < ChannelMinSpacing) return 0;

            float spacing = Mathf.Max(ChannelMinSpacing, Mathf.Min(fromRadius, toRadius) * ChannelSpacingRatio);
            var route = Route(from, to, spacing, out int turned);
            if (route.Count - 1 > ChannelMaxSteps)
            {
                FiresLogger.LogWarning(
                    $"[FiresWater] pool connect spans {span:F0}m — capped at {ChannelMaxSteps} segments "
                    + $"(wanted {route.Count - 1}); the channel will be coarse. Place an intermediate pool for a longer run.");
                route = Thin(route, ChannelMaxSteps + 1);
            }
            if (to.y > from.y + 0.3f)
            {
                string warn = $"B ({to.y:F1}) is {to.y - from.y:F1} m higher than A ({from.y:F1}): the channel climbs at a fixed grade and its water will not flow uphill. Click the higher water first.";
                FiresLogger.LogWarning("[FiresWater] two-click river: " + warn);
                if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.Center, warn, 0, null, false);
            }

            float length = 0f;
            var along = new float[route.Count];
            for (int i = 1; i < route.Count; i++)
            {
                length += Vector2.Distance(new Vector2(route[i - 1].x, route[i - 1].z), new Vector2(route[i].x, route[i].z));
                along[i] = length;
            }
            float deepestCut = 0f;
            var falls = new System.Collections.Generic.List<string>();
            bool overEdge = false;
            for (int i = 1; i < route.Count - 1; i++)
            {
                float t = length > 0f ? along[i] / length : i / (float)(route.Count - 1);
                var at = new Vector3(route[i].x, Mathf.Lerp(from.y, to.y, t), route[i].z);
                // 0.2.749 (Fire 10-05: water over a cliff or a skyland's edge becomes a waterfall): a route point whose ground lies
                // far under the bed line is past an edge. The channel stops at the lip with a fall hanging from it, and starts
                // again where the route meets ground near the line.
                if (route[i].y < at.y - EdgeDrop)
                {
                    if (!overEdge)
                    {
                        var dir = new Vector3(route[i].x - route[i - 1].x, 0f, route[i].z - route[i - 1].z);
                        if (dir.sqrMagnitude > 0.01f)
                            falls.Add(ToolFalls.Place(route[i - 1], dir.normalized, Mathf.Lerp(fromRadius, toRadius, t)));
                        overEdge = true;
                    }
                    continue;
                }
                overEdge = false;
                deepestCut = Mathf.Max(deepestCut, route[i].y - at.y);
                ShapePool(at, Mathf.Lerp(fromRadius, toRadius, t));
            }
            if (falls.Count > 0) FiresLogger.LogDebug("[FiresWater] two-click river crosses " + falls.Count + " edge(s): " + string.Join(" | ", falls.ToArray()));
            if (deepestCut > LevelClearHeight)
                FiresLogger.LogWarning($"[FiresWater] two-click river: the ground on the route stands {deepestCut:F1} m over the bed at its highest, "
                    + $"more than the {LevelClearHeight:F0} m a channel stamp clears; the channel is walled there. Place a pool on the far side first.");
            FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[FiresWater] two-click river: A ({0:0.0},{1:0.00},{2:0.0}) -> B ({3:0.0},{4:0.00},{5:0.0}): route {6} point(s), {7:0.0} m long "
                + "({8:0.0} m straight, {9} step(s) turned off the line), grade {10:+0.000;-0.000} m/m, deepest cut {11:0.0} m",
                from.x, from.y, from.z, to.x, to.y, to.z, route.Count, length, span, turned,
                length > 0f ? (to.y - from.y) / length : 0f, deepestCut));
            return route.Count - 1;
        }

        // A..B in steps of `spacing`, each over the lowest ground of the headings that still close on B (y = that ground).
        private static System.Collections.Generic.List<Vector3> Route(Vector3 from, Vector3 to, float spacing, out int turned)
        {
            turned = 0;
            var route = new System.Collections.Generic.List<Vector3> { from };
            Vector3 p = from;
            float total = Vector2.Distance(new Vector2(from.x, from.z), new Vector2(to.x, to.z));
            for (int guard = 0; guard < ChannelMaxSteps * 3; guard++)
            {
                var toB = new Vector2(to.x - p.x, to.z - p.z);
                float dist = toB.magnitude;
                if (dist <= spacing * 1.2f) break;
                toB /= dist;
                float hint = Mathf.Lerp(from.y, to.y, 1f - Mathf.Clamp01(dist / Mathf.Max(total, 0.01f)));
                Vector3 best = Vector3.zero;
                float bestScore = float.MaxValue, bestAngle = 0f;
                foreach (float angle in RouteHeadings)
                {
                    float rad = angle * Mathf.Deg2Rad, c = Mathf.Cos(rad), s = Mathf.Sin(rad);
                    var dir = new Vector2(toB.x * c - toB.y * s, toB.x * s + toB.y * c);
                    float qx = p.x + dir.x * spacing, qz = p.z + dir.y * spacing;
                    if (Vector2.Distance(new Vector2(qx, qz), new Vector2(to.x, to.z)) > dist - spacing * 0.35f) continue;
                    float ground = RouteGround(qx, qz, hint);
                    float score = ground + Mathf.Abs(angle) / 60f * RouteTurnPenalty;
                    if (score < bestScore) { bestScore = score; best = new Vector3(qx, ground, qz); bestAngle = angle; }
                }
                if (bestScore == float.MaxValue) break;
                if (bestAngle != 0f) turned++;
                p = best;
                route.Add(p);
            }
            route.Add(to);
            return route;
        }

        private static float RouteGround(float x, float z, float hint)
        {
            if (VoxelWorld.TryFindSurfaceY(x, z, hint, 24f, out float y)) return y;
            return WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : hint;
        }

        private static System.Collections.Generic.List<Vector3> Thin(System.Collections.Generic.List<Vector3> route, int keep)
        {
            var thin = new System.Collections.Generic.List<Vector3>(keep);
            for (int i = 0; i < keep; i++) thin.Add(route[Mathf.RoundToInt(i * (route.Count - 1) / (float)(keep - 1))]);
            return thin;
        }

        // ── Vpool + LeftAlt: fill a depression to its rim (Fire 10-05: "We're meant to be able to carve and then fill a
        // depression with water") ───────────────────────────────────────────────────────────────────────────────────────
        // Nothing is carved. From the click, 16 rays walk out over the voxel ground; each ray's rim is the highest ground it
        // crosses within reach, and the depression spills at the LOWEST of those rims, so the water is laid just under it,
        // as wide as the wettest ray. Open ground (a ray that never climbs above the floor) is refused: there is no bowl.
        private const int BowlRays = 16;
        private const float BowlStep = 0.5f, BowlReachMin = 6f, BowlReachMax = 32f, BowlFreeboard = 0.1f, BowlMinDepth = 0.3f;

        internal static string FillBowl(Vector3 click, float radius)
        {
            if (!Sane(click, radius, "Vpool fill")) return "[FiresWater] bowl fill refused: a bad click";
            if (!VoxelWorld.TryFindSurfaceY(click.x, click.z, click.y, 8f, out float floor))
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[FiresWater] bowl fill refused: no voxel ground at ({0:0.0},{1:0.0},{2:0.0})", click.x, click.y, click.z);
            float reach = Mathf.Clamp(radius * 4f, BowlReachMin, BowlReachMax);
            float spill = float.MaxValue, spillBearing = 0f;
            var rimAt = new float[BowlRays];
            for (int r = 0; r < BowlRays; r++)
            {
                float a = r * 2f * Mathf.PI / BowlRays, dx = Mathf.Sin(a), dz = Mathf.Cos(a);
                float rim = floor, last = floor;
                for (float d = BowlStep; d <= reach; d += BowlStep)
                {
                    if (!VoxelWorld.TryFindSurfaceY(click.x + dx * d, click.z + dz * d, last, 8f, out float g)) break;
                    last = g;
                    if (g > rim) rim = g;
                }
                rimAt[r] = rim;
                if (rim < spill) { spill = rim; spillBearing = a * Mathf.Rad2Deg; }
            }
            float level = spill - BowlFreeboard;
            if (level - floor < BowlMinDepth)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[FiresWater] bowl fill refused at ({0:0.0},{1:0.0}): the ground spills at {2:0.00} (bearing {3:0}) only {4:0.00} m over the floor {5:0.00} - no depression to fill",
                    click.x, click.z, spill, spillBearing, spill - floor, floor);

            // The water's reach: along each ray, as far as the ground stays under the level.
            float wet = 0f;
            for (int r = 0; r < BowlRays; r++)
            {
                float a = r * 2f * Mathf.PI / BowlRays, dx = Mathf.Sin(a), dz = Mathf.Cos(a), last = floor;
                for (float d = BowlStep; d <= reach; d += BowlStep)
                {
                    if (!VoxelWorld.TryFindSurfaceY(click.x + dx * d, click.z + dz * d, last, 8f, out float g) || g >= level) break;
                    last = g;
                    wet = Mathf.Max(wet, d);
                }
            }
            float waterRadius = Mathf.Max(radius, wet + 0.5f);
            LayWater(new Vector3(click.x, level, click.z), waterRadius, level - floor + 0.5f);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[FiresWater] bowl filled at ({0:0.0},{1:0.0}): floor {2:0.00}, rim spills at {3:0.00} (bearing {4:0}), water at {5:0.00}, r {6:0.0} m; nothing carved",
                click.x, click.z, floor, spill, spillBearing, level, waterRadius);
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
            if (disk != null)
            {
                // Evidence (Fire 10-05, at Debug): the surface this dab laid.
                FiresLogger.LogDebug($"[FiresWater] water disk laid at ({center.x:F0},{center.y:F1},{center.z:F0}) r={radius:F1} d={depth:F1}");
                return;
            }
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
            WaterSheets.Tick();   // 0.2.761: keep the placed-water surface index fresh (the wet band's SurfaceAt)
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

    // ── Vfall: a waterfall the player places (Fire 10-05: "make sure that we have the chaos tools properly built to place
    // our own waterfalls and rivers ... on the sky islands our selves") ─────────────────────────────────────────────────
    // A click near a drop finds the lip (walking out along the view until the ground falls away), the landing (straight
    // down past the lip), cuts a small spillway notch at the lip and hangs a curtain of the brush's width from it to the
    // landing, with mist (FiresWaterfalls / FiresWaterfallMist, [wishbone]'s 0.2.714 curtain), a source pour at the lip and
    // a pool where it lands. LeftAlt + click removes the falls whose lip is in the brush.
    //
    // PERSISTENCE rides the spillway's chunk anchor (VAVoxel): the falls of a chunk are one more key on its ZDO, written
    // with the chunk's edits by VoxelWorld.SaveChunk and read back by VoxelChunkAnchor.Apply on every peer and on load. So a
    // fall saves, syncs and streams exactly like the notch under it. Older FAT ignores the key.
    internal static class ToolFalls
    {
        internal const string ZdoFalls = "vv_falls";
        private const int FormatVersion = 1;
        private const float LipWalkStep = 0.5f, LipWalkReach = 12f, LipDrop = 2f, LipSurfaceReach = 4f;
        private const float MinDrop = 2f, LandingReach = 1200f, LandingOut = 1.5f;
        private const float NotchDepth = 0.6f, NotchMaxRadius = 2f;
        private const int SoundsPerChunk = 2;

        internal struct Fall
        {
            internal Vector3 Lip;        // the lip's water height in y
            internal Vector2 Out;        // unit, XZ
            internal float Width;
            internal float LandY;
            internal bool OntoGround;
        }

        // Every chunk this peer knows falls for (read from its anchor, or placed here); an entry, even empty, means this
        // peer's list is the truth for that chunk's anchor.
        private static readonly System.Collections.Generic.Dictionary<Vector3Int, System.Collections.Generic.List<Fall>> s_byChunk =
            new System.Collections.Generic.Dictionary<Vector3Int, System.Collections.Generic.List<Fall>>();
        private static readonly System.Collections.Generic.Dictionary<Vector3Int, GameObject> s_objects =
            new System.Collections.Generic.Dictionary<Vector3Int, GameObject>();

        private static Vector3Int ChunkOf(Vector3 p)
            => new Vector3Int(Mathf.FloorToInt(p.x / VoxelChunk.CELLS), Mathf.FloorToInt(p.y / VoxelChunk.CELLS), Mathf.FloorToInt(p.z / VoxelChunk.CELLS));

        private static string F(string format, params object[] args)
            => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);

        // The Vfall click. Returns the Debug line (starts "[FiresWater] Vfall refused" when nothing was placed).
        internal static string Place(Vector3 cursor, Vector3 view, float radius)
        {
            var flat = new Vector2(view.x, view.z);
            if (flat.sqrMagnitude < 0.01f) return Refused("look across the drop, not straight down");
            flat.Normalize();
            var out3 = new Vector3(flat.x, 0f, flat.y);

            // The lip: walk out along the view from the cursor while the ground stays within LipDrop of the cursor.
            if (!VoxelWorld.TryFindSurfaceY(cursor.x, cursor.z, cursor.y, LipSurfaceReach, out float lipY))
                return Refused(F("no voxel ground at the cursor ({0:0.0},{1:0.0},{2:0.0})", cursor.x, cursor.y, cursor.z));
            Vector3 lip = new Vector3(cursor.x, lipY, cursor.z);
            bool edge = false;
            for (float d = LipWalkStep; d <= LipWalkReach; d += LipWalkStep)
            {
                float x = cursor.x + out3.x * d, z = cursor.z + out3.z * d;
                if (!VoxelWorld.TryFindSurfaceY(x, z, lipY, LipSurfaceReach, out float y) || y < lipY - LipDrop)
                {
                    edge = true;
                    break;
                }
                lip = new Vector3(x, y, z);
                lipY = Mathf.Max(lipY, y);
            }
            if (!edge) return Refused(F("no drop within {0:0} m past the cursor along the view - aim at the ground just before an edge", LipWalkReach));
            lip.y = lipY;

            // The landing: straight down past the lip, onto terrain or into water.
            Vector3 probe = lip + out3 * LandingOut;
            int mask = LayerMask.GetMask("terrain", "Default", "static_solid");
            if (!Physics.Raycast(probe + Vector3.down * 0.5f, Vector3.down, out var hit, LandingReach, mask))
                return Refused(F("nothing below the lip at ({0:0.0},{1:0.0},{2:0.0}) within {3:0} m", probe.x, lip.y, probe.z, LandingReach));
            float landY = hit.point.y;
            bool onto = true;
            if (FatWaterChecks.TryWaterAt(hit.point.x, hit.point.z, 0.05f, false, out float water, out _)
                && water > landY && water < lip.y - MinDrop && water - landY < 40f)
            {
                landY = water;
                onto = false;
            }
            float drop = lip.y - landY;
            if (drop < MinDrop) return Refused(F("the drop at the lip is {0:0.0} m (a fall needs {1:0} m)", drop, MinDrop));

            // The spillway: the notch the water leaves by. It is also what makes the chunk an edited one, whose anchor
            // carries the fall.
            float notchR = Mathf.Min(radius, NotchMaxRadius);
            Vector3 notch = new Vector3(lip.x, lip.y - NotchDepth * 0.5f, lip.z);
            string cut = VoxelWorld.ApplyBrush(notch, notchR, NotchDepth, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
            var key = ChunkOf(notch);
            if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null || !chunk.HasBrushEdits)
                return Refused(F("the spillway notch at ({0:0.0},{1:0.0},{2:0.0}) did not take ({3}), so there is no chunk record to keep the fall in",
                    notch.x, notch.y, notch.z, cut));

            var fall = new Fall { Lip = new Vector3(lip.x, lip.y - NotchDepth, lip.z), Out = flat, Width = Mathf.Max(2f, radius * 2f), LandY = landY, OntoGround = onto };
            ListFor(key).Add(fall);
            VoxelWorld.RequestSave(key);
            Rebuild(key);

            // 0.2.752 (Fire 10-05: "v fall shouldnt be placing any river mesh, this looks like shit, it should just place a fall
            // that falls down the steep terrain under it"): the fall is only its curtain (and mist). No pour at the lip and no
            // plunge pool at the foot: water above or below it is drawn with the water tools when it is wanted.
            return F("[FiresWater] Vfall placed: lip ({0:0.0},{1:0.0},{2:0.0}) out ({3:0.00},{4:0.00}) width {5:0.0} m, drop {6:0.0} m onto {7} at y {8:0.0}; chunk {9} holds {10} fall(s)",
                fall.Lip.x, fall.Lip.y, fall.Lip.z, flat.x, flat.y, fall.Width, fall.Lip.y - landY, onto ? "ground" : "water", landY, key, s_byChunk[key].Count);
        }

        // LeftAlt + Vfall click: the falls whose lip is within the brush.
        internal static string RemoveNear(Vector3 cursor, float radius)
        {
            int removed = 0;
            var r = radius + 2f;
            var lo = ChunkOf(cursor - Vector3.one * r);
            var hi = ChunkOf(cursor + Vector3.one * r);
            for (int kx = lo.x; kx <= hi.x; kx++)
                for (int ky = lo.y; ky <= hi.y; ky++)
                    for (int kz = lo.z; kz <= hi.z; kz++)
                    {
                        var key = new Vector3Int(kx, ky, kz);
                        if (!s_byChunk.TryGetValue(key, out var list)) continue;
                        int n = list.RemoveAll(f => (f.Lip - cursor).sqrMagnitude <= r * r);
                        if (n == 0) continue;
                        removed += n;
                        VoxelWorld.RequestSave(key);
                        Rebuild(key);
                    }
            return removed > 0
                ? F("[FiresWater] Vfall removed {0} fall(s) within {1:0.0} m of ({2:0.0},{3:0.0},{4:0.0})", removed, r, cursor.x, cursor.y, cursor.z)
                : Refused(F("no fall within {0:0.0} m of ({1:0.0},{2:0.0},{3:0.0}) to remove", r, cursor.x, cursor.y, cursor.z));
        }

        private static string Refused(string why) => "[FiresWater] Vfall refused: " + why;

        // admintools_test's FallsNear probe: falls whose lip is within `radius` of `at`, as this peer holds them.
        internal static int CountNear(Vector3 at, float radius)
        {
            int n = 0;
            foreach (var kv in s_byChunk)
                foreach (var f in kv.Value)
                    if ((f.Lip - at).sqrMagnitude <= radius * radius) n++;
            return n;
        }

        private static System.Collections.Generic.List<Fall> ListFor(Vector3Int key)
        {
            if (!s_byChunk.TryGetValue(key, out var list)) s_byChunk[key] = list = new System.Collections.Generic.List<Fall>();
            return list;
        }

        // VoxelWorld.SaveChunk: this chunk's falls go on its anchor with its edits. Only a chunk this peer has a list for
        // is written, so a peer that never read the key cannot wipe it.
        internal static void WriteTo(Vector3Int key, ZDO zdo)
        {
            if (!s_byChunk.TryGetValue(key, out var list)) return;
            zdo.Set(ZdoFalls, Pack(list));
        }

        // VoxelChunkAnchor.Apply: the anchor's falls, for a chunk this peer just applied.
        internal static void ReadFrom(Vector3Int key, ZDO zdo)
        {
            var bytes = zdo.GetByteArray(ZdoFalls);
            if (bytes == null)
            {
                if (s_byChunk.ContainsKey(key)) { s_byChunk.Remove(key); Rebuild(key); }
                return;
            }
            if (!Unpack(key, bytes, out var list)) return;
            s_byChunk[key] = list;
            Rebuild(key);
            FiresLogger.LogDebug($"[FiresWater] applied {list.Count} fall(s) from chunk {key}'s shared record.");
        }

        // [generator]'s FatUndo (0.2.716, one numpad 7/9 history): a chunk's falls before a stroke, and laying them back
        // on undo/redo beside the chunk's edit blob, so undoing a Vfall takes its curtain with its notch. Snapshot is null
        // when this peer holds no list for the chunk; Restore(null) leaves the chunk with none. Restore saves the chunk's
        // anchor (the peers follow through the ZDO) and redraws the curtain here.
        internal static byte[] Snapshot(Vector3Int key)
            => s_byChunk.TryGetValue(key, out var list) ? Pack(list) : null;

        internal static void Restore(Vector3Int key, byte[] bytes)
        {
            System.Collections.Generic.List<Fall> list;
            if (bytes == null) list = new System.Collections.Generic.List<Fall>();
            else if (!Unpack(key, bytes, out list)) return;
            s_byChunk[key] = list;
            VoxelWorld.RequestSave(key);
            Rebuild(key);
            FiresLogger.LogDebug($"[FiresWater] chunk {key}: falls restored by undo/redo, {list.Count} fall(s).");
        }

        private static byte[] Pack(System.Collections.Generic.List<Fall> list)
        {
            var pkg = new ZPackage();
            pkg.Write(FormatVersion);
            pkg.Write(list.Count);
            foreach (var f in list)
            {
                pkg.Write(f.Lip);
                pkg.Write(f.Out.x); pkg.Write(f.Out.y);
                pkg.Write(f.Width);
                pkg.Write(f.LandY);
                pkg.Write(f.OntoGround);
            }
            return pkg.GetArray();
        }

        private static bool Unpack(Vector3Int key, byte[] bytes, out System.Collections.Generic.List<Fall> list)
        {
            list = new System.Collections.Generic.List<Fall>();
            try
            {
                var pkg = new ZPackage(bytes);
                int ver = pkg.ReadInt();
                if (ver != FormatVersion)
                {
                    FiresLogger.LogWarning($"[FiresWater] falls on chunk {key} are format {ver} (this FAT reads {FormatVersion}); not shown.");
                    return false;
                }
                int n = pkg.ReadInt();
                for (int i = 0; i < n; i++)
                    list.Add(new Fall
                    {
                        Lip = pkg.ReadVector3(),
                        Out = new Vector2(pkg.ReadSingle(), pkg.ReadSingle()),
                        Width = pkg.ReadSingle(),
                        LandY = pkg.ReadSingle(),
                        OntoGround = pkg.ReadBool(),
                    });
                return true;
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWater] falls on chunk {key} could not be read: {ex.Message}");
                return false;
            }
        }

        // The anchor left this peer (zone unloaded): drop the curtain; the list comes back with the anchor.
        internal static void Unload(Vector3Int key)
        {
            s_byChunk.Remove(key);
            if (s_objects.TryGetValue(key, out var go) && go != null) Object.Destroy(go);
            s_objects.Remove(key);
        }

        private static readonly System.Collections.Generic.List<FiresWaterfalls.Column> s_columns = new System.Collections.Generic.List<FiresWaterfalls.Column>();
        private static readonly System.Collections.Generic.List<FiresWaterfallMist.Site> s_sites = new System.Collections.Generic.List<FiresWaterfallMist.Site>();
        private static readonly System.Collections.Generic.List<Vector3> s_sounds = new System.Collections.Generic.List<Vector3>();

        private static void Rebuild(Vector3Int key)
        {
            if (s_objects.TryGetValue(key, out var old) && old != null) Object.Destroy(old);
            s_objects.Remove(key);
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;   // a dedicated server keeps the list only
            if (!s_byChunk.TryGetValue(key, out var list) || list.Count == 0) return;
            if (FiresWaterfalls.Material == null)
            {
                FiresLogger.LogWarning($"[FiresWater] {list.Count} fall(s) on chunk {key} not drawn: the waterfall material is not loaded.");
                return;
            }

            FiresWaterfalls.BeginMesh();
            s_sites.Clear();
            s_sounds.Clear();
            int drawn = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                var out3 = new Vector3(f.Out.x, 0f, f.Out.y);
                var across = new Vector3(-f.Out.y, 0f, f.Out.x);
                int n = Mathf.Max(2, Mathf.RoundToInt(f.Width) + 1);
                s_columns.Clear();
                for (int c = 0; c < n; c++)
                {
                    float t = -f.Width * 0.5f + f.Width * c / (n - 1);
                    s_columns.Add(new FiresWaterfalls.Column
                    {
                        Base = f.Lip + across * t,
                        LipY = f.Lip.y,
                        FallLength = f.Lip.y - f.LandY + (f.OntoGround ? 0f : FiresWaterfalls.PlungeDepth),
                        Outward = Vector3.zero,
                        OntoGround = f.OntoGround,
                    });
                }
                float seed = (key.x * 73856093 ^ key.y * 19349663 ^ key.z * 83492791 ^ i) & 0xFFFF;
                if (!FiresWaterfalls.AppendCurtain(s_columns, out3, FiresWaterfalls.EdgeLipOut, 0f, seed, FiresWaterfalls.TerrainProfile, approach: false))
                    continue;
                drawn++;
                s_sites.Add(FiresWaterfalls.LastSite);
                if (s_sounds.Count < SoundsPerChunk) s_sounds.Add(FiresWaterfalls.SoundSite(s_columns, out3));
            }

            var go = new GameObject($"fires_vfall_{key.x}_{key.y}_{key.z}");
            go.layer = 0;
            var mesh = new Mesh { name = "vfall_curtains" };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterials = FiresWaterfalls.Materials;
            VoxelRenderGroup.Apply(renderer);
            int verts = FiresWaterfalls.EndMesh(mesh);
            FiresWaterfalls.SetSounds(go.transform, s_sounds);
            FiresWaterfallMist.SetEmitters(go.transform, s_sites);
            s_objects[key] = go;
            FiresLogger.LogDebug($"[FiresWater] chunk {key}: {drawn}/{list.Count} fall curtain(s) drawn, {verts} vertices, {s_sites.Count} mist site(s).");
        }
    }
}
