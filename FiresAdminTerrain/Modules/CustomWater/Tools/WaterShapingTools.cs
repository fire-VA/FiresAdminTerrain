using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain;
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
    //   water   -> CustomWaterManager.CreateDiskOfType (through WaterOperationRPC to the server: anchor + cache)
    //   clutter -> ClutterSystem.ResetGrass         (client-local by design)
    //
    // Clutter is deliberately the one thing NOT synced: vanilla regenerates it
    // from paint/biome/height, and FiresClutterWaterEcology + FiresVegWaterEcology
    // already retarget vanilla's sea-level constant to the local water surface.
    // So shaping the bed and laying the surface is enough for vass, reeds and
    // lilies to appear correctly on every client, at zero network cost.
    //
    // (Until 0.2.1052; carves now paint their bed and banks, see PaintCarve. The reasoning below held for vanilla ground only.)
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
        private const float RiverDefaultDepth = 4.0f;   // 0.2.1073 (Fire on 1072: "the water line sits a bit high ... about a meter lower"): ~3 m of water under a 1.0 m freeboard (was 3.3 / 0.3)
        private const float RiverMinDepth = 0.5f;
        private const float RiverMaxDepth = 32f;
        private const float PourDepth = 3f;
        private const float PoolDepthRatio = 0.35f;
        private const float PoolMinDepth = 1.5f;
        private const float PondFishMinDepth = 2.0f;   // 0.2.1092: clicked pools (Vpool/Vpond), Fish1 spawns on a bed 1.5-3 m down
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

        // ── the ground cut: voxel where the ground IS voxel, the vanilla heightmap everywhere else ─────────────────────────
        // 0.2.1053 (Fire 10-26: "Ideally the tool should work no matter whether it's voxel ground or not... It's not making caves.
        // It's digging ponds and rivers that could be done on vanilla terrain too"). Before this every carve went to
        // VoxelWorld.ApplyBrush; on a peer whose Terrain Mode can't convert the zone (Off) the edit was stored but never shown and
        // the vanilla ground stayed on top. Now a cut over a zone that is already voxel (the streamer has swapped it) is the voxel
        // brush as before; anywhere else it is vanilla's own "level ground" TerrainOp (the hoe's op: level + smooth), sent to each
        // touched TerrainComp through vanilla's ApplyOperation RPC, so it persists and syncs exactly like the hoe. The heightmap
        // is only ever LOWERED here (a cut whose bed is at or above the ground under its centre is skipped). Undo: the touched
        // TerrainComp vertices are captured before the cut (FatTerrainSnapshot, the camps' verbatim restore) and the step goes
        // into the one undo history (numpad 7 / 9); redo sends the same op again.

        // 0.2.1057: a zone still converting (known to the streamer, not swapped yet) is voxel ground too: a vanilla cut there would
        // land on the heightmap the swap is about to hide.
        // 0.2.1064 (Fire 10-26, "river mesh doesnt fill in completely"; proven from his 1062 log): one river stroke took BOTH
        // paths: dabs in an already-voxel zone carved voxels, the rest of the same stroke carved the vanilla heightmap (26
        // "carve on VANILLA ground" cuts). The water grid samples the voxel density (WaterMeshCombiner.TryVoxelGround), which
        // on the vanilla-cut zones was still the uncarved ground, so those cells read as bank and were clipped: water only in
        // the voxel zone, cut off along the 64 m zone edge. Now a peer that turns brushed zones voxel (ZoneReplace /
        // FullVoxel) always carves voxels, so the zone converts like any voxel brush; the vanilla heightmap cut is only for a
        // peer that cannot show voxel edits (Terrain Mode Off).
        // 0.2.1256 (Fire 10-29, round N's black zone -5,2: a Studio pond on a ZoneReplace world carved with the voxel brush, which
        // converted the whole zone to voxel ground without the season's snow colour): while a STUDIO water edit runs, ground that is
        // not voxel yet is carved on the vanilla heightmap (TerrainComp level ops, as on a Terrain Mode Off peer), so a Studio pond
        // never converts a zone. A zone that is already voxel (or a voxel-only world) still takes the voxel brush. FAT's own in-game
        // water tools never set the flag: unchanged.
        internal static bool StudioHeightmap;
        internal static bool OnVoxel(Vector3 at)
        {
            Vector2s zone = ZoneSystem.GetZone(at);
            if (StudioHeightmap && !VoxelOnlyMode.Active && !VoxelZoneStreamer.IsZoneKnown(zone)) return false;
            return FiresVoxelConfig.StreamerRuns && (VoxelOnlyMode.Active || FiresVoxelConfig.WrapsBrushedZones || FiresVoxelConfig.WrapsAllTerrain
                || VoxelZoneStreamer.IsZoneKnown(zone));
        }

        /// <summary>Cut a cylinder (centre = its middle, as ApplyBrush): the voxel brush on voxel ground, vanilla's level op elsewhere.</summary>
        internal static string Cut(Vector3 center, float radius, float height)
        {
            if (OnVoxel(center)) return VoxelWorld.ApplyBrush(center, radius, height, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
            float bed = center.y - height * 0.5f;
            int comps = StampLower(new Vector3(center.x, bed, center.z), radius, radius, 0f, bed);
            if (comps < 0)
            {
                VoxelWorld.WarnIfUnseenLog("no vanilla heightmap under the cut and the zone is not voxel");
                return "no ground to cut here";
            }
            return comps == 0 ? "nothing to cut (the ground is at or under the bed)" : $"cut vanilla ground on {comps} comps";
        }

        // 0.2.1331 (Fire, super flat: giant spikes along a river with Terrain Mode Off). SmoothBowl fired up to 32 vanilla level ops per dab, several
        // dabs a frame; LevelTerrain takes its delta from a height stale until the Poke, so they stacked and oscillated, and under
        // [HeightmapOverride] +/-1000 nothing stopped them. "Only lowered" was tested at the centre alone. Now one stamp per dab: every
        // vertex goes to min(its height, the bowl), FatTerrainSnapshot.Stamp holding a height the Poke cannot make stale. Same save + sync (TerrainComp ZDO).
        // Bowl: flat at `bed` inside `floor`, smoothstep up to `bed + depth` at `outer`. Returns comps written, -1 = no ground loaded.
        private static int StampLower(Vector3 at, float outer, float floor, float depth, float bed)
        {
            float rise = outer > floor ? outer - floor : 0f;
            System.Func<float, float, float> bowl = (d, height) =>
            {
                float t = rise > 0f ? Mathf.Clamp01((d - floor) / rise) : 0f;
                return bed + depth * (t * t * (3f - 2f * t));
            };
            return StampUndoable(at, outer, false, bowl);
        }

        // Also fills: FillTo's level op had the same stale read (and no undo).
        private static int StampUndoable(Vector3 at, float reach, bool raise, System.Func<float, float, float> target)
        {
            if (Heightmap.FindHeightmap(at) == null) return -1;
            byte[] before = FatTerrainSnapshot.Capture(at, 0f, reach + 3f);
            int comps = FatTerrainSnapshot.Stamp(at, reach, raise, target, ref s_stamp);
            s_stampDabs++;
            s_lastCut = Time.unscaledTime;
            if (comps > 0 && before != null)
                s_cuts.Add((before, at, reach, () =>
                {
                    var redo = default(FatTerrainSnapshot.StampCount);
                    return FatTerrainSnapshot.Stamp(at, reach, raise, target, ref redo);
                }));
            if (!s_stampInfoSaid) { s_stampInfoSaid = true; FiresLogger.LogInfo("[FiresWater] water tools on heightmap ground: one lowering stamp per dab, never raised, no stacked level ops (0.2.1331)"); }
            return comps;
        }

        private static FatTerrainSnapshot.StampCount s_stamp;
        private static int s_stampDabs;
        private static bool s_stampInfoSaid;

        private static void CloseStampStroke()
        {
            if (s_stampDabs == 0 || Time.unscaledTime - s_lastCut < CutIdleSeconds) return;
            FiresLogger.LogDebug($"[FiresWater] heightmap stroke: {s_stampDabs} dab(s), {s_stamp.Lowered} vertices lowered, max lowered {s_stamp.MaxLowered:0.00} m, {s_stamp.WouldRaise} vertices would have been raised (0 applied); bank fills (the river's dip fill, a separate stamp) {s_stamp.Filled} vertices");
            s_stampDabs = 0;
            s_stamp = default;
            FatTerrainSnapshot.EndStampStroke();
        }

        // The vanilla cuts of one stroke / click are ONE undo step (as FatUndo does for voxel strokes): closed CutIdleSeconds
        // after the last cut. Undo lays each cut's captured vertices back newest first; redo cuts them again in order.
        private const float CutIdleSeconds = 0.4f;
        private static readonly System.Collections.Generic.List<(byte[] before, Vector3 at, float radius, System.Func<int> redo)> s_cuts
            = new System.Collections.Generic.List<(byte[] before, Vector3 at, float radius, System.Func<int> redo)>();
        private static float s_lastCut;

        private static void CloseCutStep()
        {
            CloseStampStroke();
            if (s_cuts.Count == 0 || Time.unscaledTime - s_lastCut < CutIdleSeconds) return;
            var cuts = s_cuts.ToArray();
            s_cuts.Clear();
            var area = new Bounds(cuts[0].at, Vector3.zero);
            foreach (var c in cuts) area.Encapsulate(new Bounds(c.at, new Vector3(c.radius * 2f + 6f, c.radius + 6f, c.radius * 2f + 6f)));
            FatUndo.Record($"a water-tool cut on vanilla ground ({cuts.Length} cut(s))", area,
                () =>
                {
                    for (int i = cuts.Length - 1; i >= 0; i--) FatTerrainSnapshot.Apply(cuts[i].before, cuts[i].at, 0f);
                    return $"vanilla ground restored under {cuts.Length} water-tool cut(s)";
                },
                () =>
                {
                    int sent = 0;
                    foreach (var c in cuts) sent += c.redo();
                    return $"{cuts.Length} water-tool cut(s) made again ({sent} comps)";
                });
            FiresLogger.LogDebug($"[FiresWater] vanilla-ground cut step recorded: {cuts.Length} cut(s), numpad 7 undoes it");
        }


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
            ProfileCut(surfacePos, radius, depth);   // 0.2.1064: sloped banks, not a cylinder
            RefreshClutter(surfacePos, radius * BankSpread, true);
        }

        // ── THE THREE WATER TOOLS (Fire 10-24: "one tool that just lays water at the height selected ... one tool that actually
        // carves and lays the mesh (the river tool) and THEN ... a lake/pond tool that auto carves out the hole") ─────────────

        // WATER: water at one level; the ground is never touched. A dab whose ground stands at or above the level stays dry.
        internal const float WaterMinDepth = 0.1f, WaterSkirt = 0.3f;

        // 0.2.1026 (Fire 10-24: "it should just show a round disk at the level the cursor raycasts at"): the dab fills the whole
        // disc at the level. It used to skip a dab whose CENTRE ground stood at or above the level, so a drag past a log or an
        // island left jagged gaps beside it; now only a disc with no ground under the level anywhere stays dry, and the depth
        // reaches the deepest ground found under the disc (straight down from the level, any surface: terrain, voxel, cave floor).
        private const float WaterDepthProbe = 40f;
        private static int s_waterBedMask;

        public static void WaterLayerDab(Vector3 ground, float radius, float level)
        {
            if (!Sane(ground, radius, "Water")) return;
            if (s_waterBedMask == 0) s_waterBedMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
            float deepest = float.MaxValue;
            int wet = 0;
            for (int i = 0; i <= 12; i++)
            {
                float x = ground.x, z = ground.z;
                if (i > 0)
                {
                    float a = (i - 1) * Mathf.PI / 6f, r = i <= 6 ? radius * 0.5f : radius * 0.95f;
                    x += Mathf.Cos(a) * r; z += Mathf.Sin(a) * r;
                }
                if (!Physics.Raycast(new Vector3(x, level + 0.05f, z), Vector3.down, out RaycastHit hit, WaterDepthProbe, s_waterBedMask, QueryTriggerInteraction.Ignore)) continue;
                if (level - hit.point.y < WaterMinDepth) continue;
                wet++;
                deepest = Mathf.Min(deepest, hit.point.y);
            }
            if (wet == 0)
            {
                FiresLogger.LogDebug($"[FiresWater] Water dab at ({ground.x:F1},{ground.z:F1}) dry: no ground under level {level:F2} in r={radius:F1}");
                return;
            }
            LayWater(new Vector3(ground.x, level, ground.z), radius, level - deepest + WaterSkirt);
            RefreshClutter(ground, radius);
        }

        // RIVER: the bed carved from the ground as it was before the stroke (surfacePos.y), `depth` deep, then water laid at
        // `waterLevel` once the carved bed is there (QueueWater waits for the new colliders).
        internal const float RiverFreeboard = 1.0f;   // 0.2.1073: the water surface sits ~1 m under the original bank top (was 0.3 m)

        // 0.2.1064 (Fire 10-26: "rivers dont need to dig down so deep"; "2-4m of water is fine"; and the near-vertical banks with
        // stair-stepped edges in his shots): the dab is a sloped profile, not one cylinder. BankRings concentric cuts step the bed
        // from `depth` at the centre to nothing at radius * BankSpread along a smoothstep, each ring at an ABSOLUTE height taken
        // from the original surface, so overlapping dabs agree instead of digging deeper. Returns true when water was queued.
        internal const float RiverMinWater = 0.6f;
        // 0.2.1067 (Fire 10-26, "stepped contour rings" in the bed): six rings made six flat terraces about depth / 6 apart,
        // taller than a voxel cell, so they showed as contour steps. A ring per RingSpacing metres of bank (at least 6, at most
        // 32) keeps each step under a voxel cell, and the mesher smooths it into a slope.
        private const int BankRingsMin = 6, BankRingsMax = 32;
        private const float RingSpacing = 0.6f;
        internal const float BankSpread = 1.5f;

        internal static float ClampRiverDepth(float depth) => Mathf.Clamp(depth, RiverMinDepth, RiverMaxDepth);

        public static bool RiverDabFill(Vector3 surfacePos, float radius, float depth, float waterLevel)
        {
            if (!Sane(surfacePos, radius, "River")) return false;
            depth = ClampRiverDepth(depth);
            ProfileCut(surfacePos, radius, depth);
            RefreshClutter(surfacePos, radius * BankSpread, true);
            float bedY = surfacePos.y - depth;
            if (waterLevel - bedY <= WaterMinDepth) return false;
            QueueWaterEntry(new Vector3(surfacePos.x, waterLevel, surfacePos.z), radius, waterLevel - bedY).Kind = WaterDiskKind.River;   // 0.2.1091
            return true;
        }

        // RIVER ON SLOPES (Fire 10-26: "river carves dragged over slopes ... laying the mesh WAY wider than we intended and breaking
        // it up etc while not carving the bed into the terrain properly, this is where they should be alowed to carve and fill
        // differently"). A dab no longer digs `depth` under its own spot with a water level that only clamps down: the bed is ONE
        // profile along the path, never rising, and each dab cuts AND fills to it. Where the ground is above the bank the bowl is
        // cut into it; where the path crosses a dip or leaves a cliff the banks are filled up to the water level + freeboard first,
        // so the channel holds its width and the water never spreads onto lower ground beside it. A drop of FallDrop or more at a
        // FallSlope or steeper is a cliff: FAT's waterfall (ToolFalls) goes there and the upstream water is pulled back from the
        // lip; a gentler steep run is rapids (the dabs' levels step down). Water disks are the channel's own width.
        internal const float FallDrop = 2.5f, FallSlope = 0.8f, RapidSlope = 0.3f;

        internal sealed class RiverRun
        {
            internal bool Has;
            internal Vector3 Prev;        // the previous dab (xz) at its bed height
            internal float PrevWidth;
            internal PendingWater PrevWater;
            internal int Cut, Fill, Falls, Rapids, Water, Dabs;
            internal float FirstBed = float.NaN, LastBed, MaxSlope;
            internal bool NextIsPool;   // 0.2.1343: the chain carve's widened start / end run: this dab's water is a flat Pond

            internal string Summary(float length, float width)
                => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[FiresWater] river profile: {0:0} m, bed {1:0.00}->{2:0.00} (max slope {3:0.00}), cut {4} / fill {5} dabs, falls {6}, rapids {7}, water cells {8} (clamped to width {9:0.0})",
                    length, FirstBed, LastBed, MaxSlope, Cut, Fill, Falls, Rapids, Water, width);
        }

        // One dab of a river on the profile: `surface` is the ORIGINAL ground (y), `bed` the profile's bed there (already never
        // above the previous dab's), `depth` the chosen depth (bank = bed + depth, water = bank - freeboard).
        private const float EndPoolEdge = 2.5f, EndPoolLogWidth = 6f;
        private static float s_endPoolLogAt;

        // [Pond] a wide river point (the chain's end pool): the planned bed vs what the carve left, sampled at the centre and
        // at 0.8 of the water radius (the voxel data, right after the cut). Rate-limited.
        private static void NoteEndPool(Vector3 surface, float width, float bed, float level, float top, bool pool)
        {
            if (Time.unscaledTime < s_endPoolLogAt) return;
            s_endPoolLogAt = Time.unscaledTime + 2f;
            string At(float dx)
            {
                float x = surface.x + dx, z = surface.z;
                return VoxelWorld.TrySampleSurfaceY(x, z, top + 1f, bed - 2f, out float y, out _) ? y.ToString("0.00") : "-";
            }
            FiresLogger.LogInfo($"[Pond] river end pool at ({surface.x:0},{surface.z:0}) r {width:0.0}: carve planned bed {bed:0.00} (full depth to {width - EndPoolEdge:0.0} m) / applied centre {At(0f)}, at 0.8r {At(width * 0.8f)}; water level {level:0.00} (kind {(pool ? "Pond, flat at its level" : "River, capped to its banks")})");
        }

        internal static void RiverStep(RiverRun run, Vector3 surface, float width, float bed, float depth)
        {
            if (!Sane(surface, width, "River")) return;
            float bank = bed + depth;
            float level = bank - RiverFreeboard;
            float outer = width * BankSpread;
            float top = surface.y;
            if (top < bank - 0.05f)
            {
                // A dip, a cliff foot or open air under the path: build the banks up to the bank height first.
                FillTo(surface, outer, top - 0.5f, bank);
                top = bank;
                run.Fill++;
            }
            // 0.2.1100 (Fire, image 564: "our pond at the end of our river laid its mesh out all fucked up"): a chain point's
            // width also sizes its water disk, but the bowl's full-depth floor was only width x 0.35, the rest a long smoothstep
            // out to width x 1.5. Narrow channels didn't notice; the wide end point (his log: radius 16.0) got a shallow dish
            // whose outer half lay barely under the water level, so the water filmed over grass that was hardly dug. The floor
            // now runs full depth to within EndPoolEdge of the water's edge on wide points (unchanged on narrow ones).
            float floor = Mathf.Max(width * 0.35f, width - EndPoolEdge);
            SmoothBowl(new Vector3(surface.x, top, surface.z), outer, top - bed, floor);
            if (width >= EndPoolLogWidth) NoteEndPool(surface, width, bed, level, top, run.NextIsPool);
            run.Cut++;
            RefreshClutter(surface, outer, true);

            bool fall = false;
            if (run.Has)
            {
                float run2 = new Vector2(surface.x - run.Prev.x, surface.z - run.Prev.z).magnitude;
                float drop = run.Prev.y - bed;
                float slope = run2 > 0.01f ? drop / run2 : 0f;
                run.MaxSlope = Mathf.Max(run.MaxSlope, slope);
                if (drop >= FallDrop && slope >= FallSlope)
                {
                    var view = new Vector3(surface.x - run.Prev.x, 0f, surface.z - run.Prev.z);
                    // 0.2.1071: lip = the upstream dab's water level, landing = this dab's water level, in a plunge pool here.
                    float prevLevel = run.Prev.y + depth - RiverFreeboard;
                    PlungePool(new Vector3(surface.x, level, surface.z), Mathf.Max(width, run.PrevWidth));
                    if (s_finishOpen != null && FallsLastOn)
                    {
                        // 0.2.1348: the fall waits for the real surfaces (RiverFinish); only the plan is kept here.
                        s_finishOpen.Falls.Add(new RiverFinish.PlannedFall { Prev = new Vector3(run.Prev.x, 0f, run.Prev.z), Landing = new Vector3(surface.x, 0f, surface.z), Out = view.normalized, PlannedLip = prevLevel, PlannedLand = level, Run = run2, Width = run.PrevWidth });
                        run.Falls++;
                        FiresLogger.LogDebug("[FiresWater] river profile: cliff (drop " + drop.ToString("0.0") + " m over " + run2.ToString("0.0") + " m): fall planned, placed last (0.2.1348)");
                    }
                    else
                    {
                    string said = ToolFalls.PlaceAt(new Vector3(run.Prev.x, prevLevel, run.Prev.z), view, run.PrevWidth, level);
                    if (said == null || !said.StartsWith("[FiresWater] Vfall refused")) run.Falls++;
                    FiresLogger.LogDebug("[FiresWater] river profile: cliff (drop " + drop.ToString("0.0") + " m over " + run2.ToString("0.0") + " m): " + said);
                    }
                    if (run.PrevWater != null) run.PrevWater.Radius = Mathf.Min(run.PrevWater.Radius, run.PrevWidth * 0.6f);   // no sheet over the lip
                    fall = true;
                }
                else if (slope >= RapidSlope) run.Rapids++;
            }
            run.PrevWater = null;
            if (level - bed > WaterMinDepth)   // 0.2.1071: the landing dab keeps its water (the fall drops into it)
            {
                // 0.2.1343: the disk runs ShoreOverlap past where this bowl's ground crosses the water level, so the bank buries the edge
                // (vanilla's method: the level is fixed, terrain buries the edge). The carve itself is unchanged.
                float sheet = ShoreRadius(outer, floor, top, bed, level, width);
                run.PrevWater = QueueWaterEntry(new Vector3(surface.x, level, surface.z), sheet, level - bed);
                // 0.2.1343: a widened start / end pool is flat (Pond, 1321's rule); the channel stays River and keeps the bank cap.
                run.PrevWater.Kind = run.NextIsPool ? WaterDiskKind.Pond : WaterDiskKind.River;   // 0.2.1091
                run.PrevWater.NeedsCarve = true;
                if (Time.unscaledTime >= s_shoreLogAt && FiresLogger.DebugEnabled)
                {
                    s_shoreLogAt = Time.unscaledTime + 2f;
                    FiresLogger.LogDebug($"[Pond] river disk at ({surface.x:0},{surface.z:0}): width {width:0.0} -> sheet r {sheet:0.0} (ground crosses level {level:0.00} at the bowl, + {ShoreOverlap:0.0} m, at most the carve's reach {outer:0.0}), kind {run.PrevWater.Kind} (0.2.1343)");
                }
                run.Water++;
            }
            if (s_finishOpen != null)   // 0.2.1348
            {
                s_finishOpen.LastStep = Time.realtimeSinceStartup;
                s_finishOpen.Dabs.Add(new RiverFinish.Dab { X = surface.x, Z = surface.z, Level = level, Width = width, Water = run.PrevWater });
                s_finishOpen.Zones.Add(ZoneSystem.GetZone(surface));
            }
            if (float.IsNaN(run.FirstBed)) run.FirstBed = bed;
            run.LastBed = bed;
            run.Prev = new Vector3(surface.x, bed, surface.z);
            run.PrevWidth = width;
            run.Has = true;
            run.Dabs++;
        }

        // ---- 0.2.1348 (Fire: "our placement of our mesh on slopes is poor, we should fill in where the meshes poke out"; "our waterfall placement still does not match the river itself, it needs to
        // be the last thing to lay in so it places itself correctly"; "our river beds also dont properly clear the green paint and put in the wet sandy beds"). One stroke finishes in order, event by
        // event, from WaterShapingTools.Tick: carve, water, bank fill, falls, paint, and only then ClutterResetWatch's one grass pass. Every wait is capped, so a refused disk still resolves.
        internal sealed class RiverFinish
        {
            internal sealed class Dab { public float X, Z, Level, Width; public PendingWater Water; }
            internal sealed class PlannedFall { public Vector3 Prev, Landing, Out; public float PlannedLip, PlannedLand, Run, Width; }
            internal readonly System.Collections.Generic.List<Dab> Dabs = new System.Collections.Generic.List<Dab>();
            internal readonly System.Collections.Generic.List<PendingWater> Water = new System.Collections.Generic.List<PendingWater>();
            internal readonly System.Collections.Generic.List<PlannedFall> Falls = new System.Collections.Generic.List<PlannedFall>();
            internal readonly System.Collections.Generic.HashSet<Vector2s> Zones = new System.Collections.Generic.HashSet<Vector2s>();
            internal bool CarveDone, Studio, PaintRetried;
            internal int Phase, ScanDab, FillColumns, FillBrushes, SkipNoFloor, SkipTooDeep, SkipForeign, Refused, FoundDeep;
            internal float PhaseAt, NextCheck, WorstGap, LastStep;
            internal string PaintWhy = "";
            internal readonly System.Collections.Generic.HashSet<long> Seen = new System.Collections.Generic.HashSet<long>();
            internal readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<Dab>> Grid = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<Dab>>();
            internal readonly System.Collections.Generic.List<WaterSheets.DiskInfo> Foreign = new System.Collections.Generic.List<WaterSheets.DiskInfo>();
        }

        private const float FinishWaterCap = 60f, FinishSettleCap = 20f, FinishPaintCap = 10f, FinishCheckSeconds = 0.1f, FinishOpenIdle = 30f;   // the water's own wait is 45 s at most
        private const float BankFillRadius = 1.2f, BankFillMinGap = 0.1f, BankFillMaxGap = 6f, BankFillCell = 8f, SheetSkirt = 3f;
        private const double FinishFillBudgetMs = 4.0;
        private const float FallWalkStep = 0.5f, FallSameLevel = 1f, FallBand = 6f, FallMergeLevel = 0.75f, FallMergeReach = 3f;
        private static RiverFinish s_finishOpen;
        private static readonly System.Collections.Generic.List<RiverFinish> s_finishes = new System.Collections.Generic.List<RiverFinish>();
        private static int s_finishN;
        private static bool s_fillProof, s_fallProof, s_paintProof;

        private const float BankFillRadiusMore = 1.6f, BankFillMoreGap = 12f, BankFillExtraReach = 2f;
        private static float FillExtra => FiresVoxelConfig.RiverBankFillMore != null && FiresVoxelConfig.RiverBankFillMore.Value ? BankFillExtraReach : 0f;

private static bool FillOn => FiresVoxelConfig.RiverBankFill != null && FiresVoxelConfig.RiverBankFill.Value;
        private static bool FallsLastOn => FiresVoxelConfig.RiverFallsLayLast != null && FiresVoxelConfig.RiverFallsLayLast.Value;
        private static bool PaintOn => FiresVoxelConfig.RiverPaintFollowsWater != null && FiresVoxelConfig.RiverPaintFollowsWater.Value;

        /// <summary>A finish is still running: the stroke's grass pass and its undo step wait for it.</summary>
        internal static bool RiverFinishPending => s_finishOpen != null || s_finishes.Count > 0;

        // 0.2.1354 (review: statics surviving logout): world-bound work must not run in the next login, which may be another world: a carve job, queued water, a finish's fill and falls are
        // real edits at the old coordinates; held shore pieces, an open grass stroke and an open undo step are closed here too. Idempotent; from Game.Logout and the streamer's OnDestroy.
        internal static void ForgetSession()
        {
            if (s_finishOpen != null || s_finishes.Count > 0 || s_pendingWater.Count > 0 || s_cuts.Count > 0)
                FiresLogger.LogDebug($"[RiverFinish] session ended: dropped {(s_finishOpen != null ? 1 : 0) + s_finishes.Count} unfinished stroke(s), {s_pendingWater.Count} queued water disk(s), {s_cuts.Count} open cut(s) (0.2.1354)");
            s_finishOpen = null;
            s_finishes.Clear();
            s_pendingWater.Clear();
            s_waterExtended.Clear();
            s_cuts.Clear();
            StudioHeightmap = false;
            WaterToolDriver.ForgetCarveJob();
            ZoneReveal.Clear();
            ClutterResetWatch.Clear();
            VerdantsAscent.Terrain.FatUndo.ForgetSession();
        }

        internal static void RiverFinishBegin(bool studio)
        {
            if (!FillOn && !FallsLastOn && !PaintOn) return;
            if (s_finishOpen == null) s_finishOpen = new RiverFinish { Studio = studio, LastStep = Time.realtimeSinceStartup };
        }

        internal static void RiverFinishEnd()
        {
            var f = s_finishOpen;
            if (f == null) return;
            s_finishOpen = null;
            f.CarveDone = true;
            f.PhaseAt = Time.realtimeSinceStartup;
            s_finishes.Add(f);
        }

        private static bool FinishZonesBusy(RiverFinish f, out string which)
        {
            which = null;
            foreach (var z in f.Zones) if (VoxelZoneStreamer.ZoneEditBusy(z)) { which = $"zone ({z.x},{z.y})"; return true; }
            return false;
        }

        private static void TickRiverFinish()
        {
            // A drag whose tool went away without its End must not hold the grass pass and the undo step for ever.
            if (s_finishOpen != null && !WaterToolDriver.StudioRiverCarving && Time.realtimeSinceStartup - s_finishOpen.LastStep >= FinishOpenIdle)
            {
                FiresLogger.LogDebug($"[RiverFinish] a stroke had no dab for {FinishOpenIdle:0} s and no carve running: finishing what it laid (0.2.1348)");
                RiverFinishEnd();
            }
            if (s_finishes.Count == 0) return;
            var f = s_finishes[0];
            VerdantsAscent.Terrain.FatUndo.BeginPlayerEdit("a Vriver stroke"); VoxelWorld.VToolScope++;
            StudioHeightmap = f.Studio;
            try { if (StepFinish(f)) s_finishes.RemoveAt(0); }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[RiverFinish] stroke {s_finishN} stopped in phase {f.Phase}: {ex.GetType().Name}: {ex.Message}; what was not placed is dropped (0.2.1348)");
                s_finishes.RemoveAt(0);
            }
            finally { VoxelWorld.VToolScope--; VerdantsAscent.Terrain.FatUndo.EndPlayerEdit(); StudioHeightmap = false; }
        }

        // True when the stroke is finished.
        private static bool StepFinish(RiverFinish f)
        {
            float now = Time.realtimeSinceStartup;
            if (f.Phase != 1)
            {
                if (now < f.NextCheck) return false;
                f.NextCheck = now + FinishCheckSeconds;
            }
            switch (f.Phase)
            {
                case 0:   // the water: every disk laid or refused, its zones swapped, the wet checks answered
                {
                    string waiting = null;
                    foreach (var w in f.Water) if (!w.Gone) { waiting = "water"; break; }
                    if (waiting == null && VoxelZoneStreamer.PendingWetCount > 0) waiting = "water answering";
                    if (waiting == null) FinishZonesBusy(f, out waiting);
                    bool capped = waiting != null && now - f.PhaseAt >= FinishWaterCap;
                    if (waiting != null && !capped) return false;
                    s_finishN++;
                    foreach (var w in f.Water) if (w.Refused) f.Refused++;
                    FiresLogger.LogDebug($"[RiverFinish] stroke {s_finishN}: water stage done after {now - f.PhaseAt:0.0} s: {f.Water.Count} disk(s) asked, {f.Refused} refused{(capped ? $", CAPPED still waiting on {waiting}" : "")} (0.2.1348)");
                    f.Phase = 1; f.PhaseAt = now;
                    if (FillOn) PrepareFill(f); else f.ScanDab = f.Dabs.Count;
                    return false;
                }
                case 1:   // the bank fill, budgeted
                    if (!ScanFill(f)) return false;
                    FiresLogger.LogDebug($"[RiverFinish] stroke {s_finishN}: bank fill {f.FillColumns} column(s) in {f.FillBrushes} brush(es), worst gap {f.WorstGap:0.00} m, {f.FoundDeep} found past the bed reach (0.2.1349); left alone: {f.SkipNoFloor} no floor or a wall, {f.SkipTooDeep} over {BankFillMaxGap:0} m deep, {f.SkipForeign} other water or a fall (0.2.1348)");
                    f.Phase = 2; f.PhaseAt = now;
                    return false;
                case 2:   // the fill's ground lands, then the falls
                {
                    bool busy = FinishZonesBusy(f, out string which);
                    if (busy && now - f.PhaseAt < FinishSettleCap) return false;
                    if (busy) FiresLogger.LogDebug($"[RiverFinish] stroke {s_finishN}: fill settle CAPPED {FinishSettleCap:0} s, still waiting on {which} (0.2.1348)");
                    PlaceFalls(f);
                    if (!PaintOn) return true;
                    f.Phase = 3; f.PhaseAt = now;
                    RepaintStroke(f);
                    return false;
                }
                default:   // the paint: the stamps finished, one more write if a wet column still reads dry
                {
                    bool done = true;
                    foreach (var d in f.Dabs) if (!Patch_Heightmap_WetMask.StampsDone(new Vector3(d.X, 0f, d.Z), d.Width)) { done = false; break; }
                    bool capped = !done && now - f.PhaseAt >= FinishPaintCap;
                    if (!done && !capped) return false;
                    if (capped) f.PaintWhy += $" stamps CAPPED {FinishPaintCap:0} s;";
                    CountPaint(f, out int wet, out int dry);
                    if (dry > 0 && !f.PaintRetried)
                    {
                        f.PaintRetried = true;
                        f.PaintWhy += $" {dry} dry on the first read, written again;";
                        f.PhaseAt = now;
                        RepaintStroke(f);
                        return false;
                    }
                    FiresLogger.LogDebug($"[RiverFinish] stroke {s_finishN}: bed paint read at {wet + dry} channel column(s): {wet} wet + sand, {dry} still dry;{f.PaintWhy} (0.2.1348)");
                    return true;
                }
            }
        }

        private static void RepaintStroke(RiverFinish f)
        {
            if (!s_paintProof) { s_paintProof = true; FiresLogger.LogInfo("[RiverFinish] a river's bed paint is written again once its water and ground have landed, before its grass pass (0.2.1348)"); }
            float lastX = float.NaN, lastZ = 0f;
            for (int i = 0; i < f.Dabs.Count; i++)
            {
                var d = f.Dabs[i];
                if (i + 1 < f.Dabs.Count && !float.IsNaN(lastX) && (d.X - lastX) * (d.X - lastX) + (d.Z - lastZ) * (d.Z - lastZ) < 16f) continue;
                lastX = d.X; lastZ = d.Z;
                VoxelZoneStreamer.RepaintPlacedWater(new Vector3(d.X, 0f, d.Z), (d.Water != null ? d.Water.Radius : d.Width) + 2f);
            }
        }

        // The centre and four points at half width of every 4 m of channel: what the band reads now.
        private static void CountPaint(RiverFinish f, out int wet, out int dry)
        {
            wet = dry = 0;
            float lastX = float.NaN, lastZ = 0f;
            foreach (var d in f.Dabs)
            {
                if (d.Water == null) continue;
                if (!float.IsNaN(lastX) && (d.X - lastX) * (d.X - lastX) + (d.Z - lastZ) * (d.Z - lastZ) < 16f) continue;
                lastX = d.X; lastZ = d.Z;
                for (int i = 0; i < 5; i++)
                {
                    float x = d.X + (i == 1 ? d.Width * 0.5f : i == 2 ? -d.Width * 0.5f : 0f), z = d.Z + (i == 3 ? d.Width * 0.5f : i == 4 ? -d.Width * 0.5f : 0f);
                    if (!FatWaterChecks.TryWaterAt(x, z, 0.05f, false, out _, out _)) continue;   // no water here: not a bed column
                    if (VoxelZoneStreamer.PaintWet01Uncached(x, z) >= 0.5f) wet++; else dry++;
                }
            }
        }

        private static long FillCell(float x, float z) => ((long)Mathf.FloorToInt(x / BankFillCell) << 32) ^ (uint)Mathf.FloorToInt(z / BankFillCell);

        private static void PrepareFill(RiverFinish f)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var d in f.Dabs)
            {
                float reach = (d.Water != null ? d.Water.Radius : d.Width) + SheetSkirt + FillExtra + BankFillRadiusMore;   // 0.2.1354 (review: the keep can use the 1.6 m brush, so the grid must list a dab that far out)
                for (int cx = Mathf.FloorToInt((d.X - reach) / BankFillCell); cx <= Mathf.FloorToInt((d.X + reach) / BankFillCell); cx++)
                    for (int cz = Mathf.FloorToInt((d.Z - reach) / BankFillCell); cz <= Mathf.FloorToInt((d.Z + reach) / BankFillCell); cz++)
                    {
                        long k = ((long)cx << 32) ^ (uint)cz;
                        if (!f.Grid.TryGetValue(k, out var l)) f.Grid[k] = l = new System.Collections.Generic.List<RiverFinish.Dab>();
                        l.Add(d);
                    }
                minX = Mathf.Min(minX, d.X - reach); maxX = Mathf.Max(maxX, d.X + reach); minZ = Mathf.Min(minZ, d.Z - reach); maxZ = Mathf.Max(maxZ, d.Z + reach);
            }
            // Water that is not this stroke's: a river running into a lake must not wall itself off from it.
            foreach (var disk in WaterSheets.FreshDisks())
            {
                if (!disk.Water || disk.X + disk.R < minX || disk.X - disk.R > maxX || disk.Z + disk.R < minZ || disk.Z - disk.R > maxZ) continue;
                bool ours = false;
                foreach (var w in f.Water)
                    if (Mathf.Abs(w.Center.x - disk.X) < 0.5f && Mathf.Abs(w.Center.z - disk.Z) < 0.5f && Mathf.Abs(w.Center.y - disk.Y) < 0.1f) { ours = true; break; }
                if (!ours) f.Foreign.Add(disk);
            }
        }

        // One slice. True when every dab has been walked. A column is filled when it lies outside every dab's channel width (the wet interior stays open), inside the
        // drawn sheet's reach (the disk + its skirt), and the ground there stands under the water drawn there.
        private static bool ScanFill(RiverFinish f)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long budget = (long)(FinishFillBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            while (f.ScanDab < f.Dabs.Count)
            {
                var d = f.Dabs[f.ScanDab++];
                if (d.Water == null) continue;
                int r = Mathf.CeilToInt(d.Water.Radius + SheetSkirt + FillExtra);
                int cx = Mathf.FloorToInt(d.X), cz = Mathf.FloorToInt(d.Z);
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        long key = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                        if (f.Seen.Add(key)) FillColumn(f, cx + dx + 0.5f, cz + dz + 0.5f);
                    }
                if (System.Diagnostics.Stopwatch.GetTimestamp() - t0 >= budget) return false;
            }
            return true;
        }

        private static void FillColumn(RiverFinish f, float x, float z)
        {
            if (!f.Grid.TryGetValue(FillCell(x, z), out var near)) return;
            float level = float.MinValue;
            float brush = FiresVoxelConfig.RiverBankFillMore != null && FiresVoxelConfig.RiverBankFillMore.Value ? BankFillRadiusMore : BankFillRadius;   // 0.2.1354 (review: the channel keep used 1.2 m under the 1.6 m brush, so a fill bit 0.4 m into the channel)
            foreach (var d in near)
            {
                float ddx = x - d.X, ddz = z - d.Z, d2 = ddx * ddx + ddz * ddz, keep = d.Width + brush;
                if (d2 < keep * keep) return;   // the channel
                if (d.Water == null) continue;
                float reach = d.Water.Radius + SheetSkirt + FillExtra;
                if (d2 <= reach * reach && d.Level > level) level = d.Level;
            }
            if (level == float.MinValue) return;
            foreach (var pf in f.Falls)
            {
                float fx = x - pf.Prev.x, fz = z - pf.Prev.z, along = fx * pf.Out.x + fz * pf.Out.z, side = Mathf.Abs(fx * pf.Out.z - fz * pf.Out.x);
                if (along > -brush && along < pf.Run + pf.Width && side < pf.Width + brush) { f.SkipForeign++; return; }   // the curtain's path (0.2.1354: the active brush, as the channel keep)
            }
            foreach (var o in f.Foreign)
            {
                float ox = x - o.X, oz = z - o.Z;
                if (ox * ox + oz * oz <= o.R * o.R && o.Y >= level - PlacedWaterField.RimFreeboard) { f.SkipForeign++; return; }
            }
            if (GenWaterField.Ready && GenWaterField.TryWaterSurfaceSmooth(x, z, out _, out float genDepth) && genDepth > 0.05f) { f.SkipForeign++; return; }
            float ground = PlacedWaterField.BedUnder(x, z, level, true);
            // 0.2.1349 (Fire on 1348: "the edges need to fill in a little more"; log: 381 "no floor or a wall" in one count, 4 too deep): a column whose floor is past the bed reach is not a wall, it is the worst poke-out, so its real ground is found and filled.
            bool more = FiresVoxelConfig.RiverBankFillMore != null && FiresVoxelConfig.RiverBankFillMore.Value;
            if (float.IsNaN(ground))
            {
                if (!more || (VoxelWorld.TryIsSolid(x, level, z, out bool solidAtLevel) && solidAtLevel)) { f.SkipNoFloor++; return; }
                if (!VoxelWorld.TryFindSurfaceY(x, z, level - BankFillMoreGap * 0.5f, BankFillMoreGap * 0.5f, out ground)) { f.SkipNoFloor++; return; }
                f.FoundDeep++;
            }

            float gap = level - ground;
            if (gap < BankFillMinGap) return;
            if (gap > (more ? BankFillMoreGap : BankFillMaxGap)) { f.SkipTooDeep++; return; }
            float top = level + RiverFreeboard;
            FillTo(new Vector3(x, top, z), brush, ground - 0.5f, top);
            var zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
            f.Zones.Add(zone);
            ClutterResetWatch.StrokeZone(zone);
            f.FillColumns++; f.FillBrushes++;
            if (gap > f.WorstGap) f.WorstGap = gap;
            // The brush covers the four columns beside it: they need no brush of their own.
            int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
            if (f.Seen.Add(((long)(ix + 1) << 32) ^ (uint)iz)) f.FillColumns++;
            if (f.Seen.Add(((long)(ix - 1) << 32) ^ (uint)iz)) f.FillColumns++;
            if (f.Seen.Add(((long)ix << 32) ^ (uint)(iz + 1))) f.FillColumns++;
            if (f.Seen.Add(((long)ix << 32) ^ (uint)(iz - 1))) f.FillColumns++;
            if (!s_fillProof) { s_fillProof = true; FiresLogger.LogInfo("[RiverFinish] a river's banks are filled where the ground stands under its drawn water, with the stroke's undo (0.2.1348)"); }
        }

        private const float FallLipSlip = 0.35f, FallWidthOver = 1.25f;
        private static bool s_fallShapeProof;
        // 0.2.1354 (review: a curtain spanned open air past the water): a step is open only inside the reach of a sheet this stroke laid at about this level (disk + skirt, as the bank fill), not wherever the ground lies low.
        private static float FallOpenTo(RiverFinish f, float x, float z, float dx, float dz, float level, float max)
        {
            float open = 0f;
            for (float t = FallWalkStep; t <= max; t += FallWalkStep)
            {
                float px = x + dx * t, pz = z + dz * t;
                float g = PlacedWaterField.BedUnder(px, pz, level, true);
                if (float.IsNaN(g) || g >= level || !InSheetReach(f, px, pz, level)) break;
                open = t;
            }
            return open;
        }

        private static bool InSheetReach(RiverFinish f, float x, float z, float level)
        {
            foreach (var d in f.Dabs)
            {
                if (d.Water == null || d.Water.Refused || Mathf.Abs(d.Level - level) > FallBand) continue;
                float ddx = x - d.X, ddz = z - d.Z, reach = d.Water.Radius + SheetSkirt;
                if (ddx * ddx + ddz * ddz <= reach * reach) return true;
            }
            return false;
        }

        private struct ResolvedFall { public Vector3 Lip, Out; public float LandY, Ground, Width, PlannedLip, PlannedLand; }

        private static bool FallSurface(float x, float z, float level, out float surface)
            => PlacedWaterField.TrySurface(x, z, level - FallBand, level + FallBand, PlacedWaterField.Consumer.Sheet, out surface, out _);

        private static void PlaceFalls(RiverFinish f)
        {
            if (f.Falls.Count == 0) return;
            var ready = new System.Collections.Generic.List<ResolvedFall>();
            foreach (var p in f.Falls)
            {
                if (TryResolveFall(f, p, out var r, out string why)) ready.Add(r);
                else FiresLogger.LogDebug($"[RiverFinish] stroke {s_finishN}: fall at ({p.Prev.x:0.0},{p.Prev.z:0.0}) DROPPED: {why}; planned lip {p.PlannedLip:0.00} land {p.PlannedLand:0.00} (0.2.1348)");
            }
            // Stacked curtains only: the next fall starts where this one lands, right there, the same way down. Anything else stays its own fall.
            for (int i = 0; i + 1 < ready.Count; i++)
            {
                var a = ready[i]; var b = ready[i + 1];
                if (Mathf.Abs(a.LandY - b.Lip.y) > FallMergeLevel || new Vector2(a.Lip.x - b.Lip.x, a.Lip.z - b.Lip.z).magnitude > FallMergeReach || Vector3.Dot(a.Out, b.Out) < 0.7f) continue;
                FiresLogger.LogDebug($"[RiverFinish] stroke {s_finishN}: falls at ({a.Lip.x:0.0},{a.Lip.z:0.0}) and ({b.Lip.x:0.0},{b.Lip.z:0.0}) are stacked curtains (land {a.LandY:0.00} = lip {b.Lip.y:0.00}): one fall (0.2.1348)");
                a.LandY = b.LandY; a.PlannedLand = b.PlannedLand; a.Width = Mathf.Max(a.Width, b.Width);
                ready[i] = a; ready.RemoveAt(i + 1); i--;
            }
            foreach (var r in ready)
            {
                string said = ToolFalls.PlaceRiverFinal(r.Lip, r.Ground, r.Out, r.Width, r.LandY);
                FiresLogger.LogDebug($"[RiverFinish] stroke {s_finishN}: fall lip planned {r.PlannedLip:0.00} final {r.Lip.y:0.00}, land planned {r.PlannedLand:0.00} final {r.LandY:0.00}, width {r.Width:0.0}: {said} (0.2.1348)");
            }
            if (!s_fallProof) { s_fallProof = true; FiresLogger.LogInfo("[RiverFinish] a river's falls are placed last, from the water surfaces that landed (0.2.1348)"); }
        }

        // Walk down the river from the upstream dab: the lip is the last column on the upper water, the landing the first column on lower water. Then across, for the water's real width.
        private static bool TryResolveFall(RiverFinish f, RiverFinish.PlannedFall p, out ResolvedFall r, out string why)
        {
            r = default; why = null;
            float sx = p.Prev.x, sz = p.Prev.z, upper = float.NaN;
            for (float back = 0f; back <= 3f && float.IsNaN(upper); back += 1f)
            {
                sx = p.Prev.x - p.Out.x * back; sz = p.Prev.z - p.Out.z * back;
                if (FallSurface(sx, sz, p.PlannedLip, out float s)) upper = s;
            }
            if (float.IsNaN(upper)) { why = "no water on the upper side (its disk was refused, or capped dry)"; return false; }
            // 0.2.1349 (1348 log: lip planned 60.30 final 58.94; a 2.6 m fall DROPPED "final drop is only -0.48 m"): each step was compared with the LAST step, so the lip chased the sheet down its ramp. Now against the upper water itself; the landing is where the descent ends.
            bool anchored = FiresVoxelConfig.RiverFallShape == null || FiresVoxelConfig.RiverFallShape.Value;
            float lipX = sx, lipZ = sz, lipY = upper, land = float.NaN;
            float reach = p.Run + p.Width + SheetSkirt;
            for (float t = FallWalkStep; t <= reach; t += FallWalkStep)
            {
                float x = sx + p.Out.x * t, z = sz + p.Out.z * t;
                if (anchored)
                {
                    bool nearUpper = FallSurface(x, z, upper, out float near);
                    if (float.IsNaN(land) && nearUpper && Mathf.Abs(near - upper) <= FallLipSlip) { lipX = x; lipZ = z; continue; }
                    float below = float.NaN;
                    if (nearUpper && near < upper - FallLipSlip) below = near;
                    if (FallSurface(x, z, p.PlannedLand, out float low2) && low2 < upper - FallLipSlip && (float.IsNaN(below) || low2 < below)) below = low2;
                    // 0.2.1354 (review: the land search walked to its reach and kept the lowest water it met, so a landing followed the river's next ramp or a lower sheet): once it has a landing,
                    // a gap, or reaching the planned landing level, ends the descent.
                    if (float.IsNaN(below)) { if (float.IsNaN(land)) continue; break; }
                    if (float.IsNaN(land) || below < land - 0.05f) land = below; else break;   // the descent has ended
                    if (land <= p.PlannedLand + FallLipSlip) break;
                    continue;
                }
                if (FallSurface(x, z, lipY, out float here) && Mathf.Abs(here - lipY) <= FallSameLevel) { lipX = x; lipZ = z; lipY = here; continue; }
                if (FallSurface(x, z, p.PlannedLand, out float low) && low < lipY - FallSameLevel) { land = low; break; }
            }
            if (float.IsNaN(land) && FallSurface(p.Landing.x, p.Landing.z, p.PlannedLand, out float planned)) land = planned;
            if (float.IsNaN(land)) { why = "no water on the lower side (its disk was refused, or capped dry)"; return false; }
            if (lipY - land < 1f) { why = $"the final drop is only {lipY - land:0.00} m"; return false; }
            float ground = PlacedWaterField.BedUnder(lipX, lipZ, lipY, true);
            if (float.IsNaN(ground)) ground = lipY;
            float acrossX = -p.Out.z, acrossZ = p.Out.x, plus = 0f, minus = 0f;
            for (float t = FallWalkStep; t <= p.Width; t += FallWalkStep)
            { if (FallSurface(lipX - p.Out.x + acrossX * t, lipZ - p.Out.z + acrossZ * t, lipY, out float s) && Mathf.Abs(s - lipY) <= FallSameLevel) plus = t; else break; }
            for (float t = FallWalkStep; t <= p.Width; t += FallWalkStep)
            { if (FallSurface(lipX - p.Out.x - acrossX * t, lipZ - p.Out.z - acrossZ * t, lipY, out float s) && Mathf.Abs(s - lipY) <= FallSameLevel) minus = t; else break; }
            // 0.2.1349 (1348 log: "width 2.0" on a river of width 5.7; Fire's shots: a thin strip in a wide channel): the lip sits on the rim of a round disk, where its chord is short. The width is the open channel between the banks: the first wall or ground over the water each way.
            if (anchored)
            {
                plus = FallOpenTo(f, lipX - p.Out.x, lipZ - p.Out.z, acrossX, acrossZ, lipY, p.Width * FallWidthOver);
                minus = FallOpenTo(f, lipX - p.Out.x, lipZ - p.Out.z, -acrossX, -acrossZ, lipY, p.Width * FallWidthOver);
                FiresLogger.LogDebug($"[RiverFinish] fall width: open {plus:0.0} + {minus:0.0} m across the lip, river width {p.Width:0.0}; lip {lipY:0.00}, land {land:0.00} (0.2.1349)");
                if (!s_fallShapeProof) { s_fallShapeProof = true; FiresLogger.LogInfo("[RiverFinish] a river's fall spans the open channel at its real brink (0.2.1349)"); }
            }
            float shift = (plus - minus) * 0.5f;
            r = new ResolvedFall
            {
                Lip = new Vector3(lipX + acrossX * shift, lipY, lipZ + acrossZ * shift), Out = p.Out, LandY = land, Ground = ground,
                Width = Mathf.Clamp(plus + minus, 2f, p.Width * 2f * (anchored ? FallWidthOver : 1f)), PlannedLip = p.PlannedLip, PlannedLand = p.PlannedLand,
            };
            return true;
        }

        // 0.2.1343: SmoothBowl's ground is top - (top - bed) * smoothstep(t), t = (outer - r) / (outer - floor). The radius where that ground
        // equals `level` (inverse smoothstep), plus ShoreOverlap; never past the carve (natural ground there can lie under the level) and never
        // under the width the tool asked for. r 10.8 + bed 46, level 49, top 50: shore 13.6, disk 14.6. r 3.8: shore 4.3, disk 5.3.
        internal const float ShoreOverlap = 1f;
        private static float s_shoreLogAt;
        internal static float ShoreRadius(float outer, float floor, float top, float bed, float level, float width)
        {
            float span = top - bed;
            if (span <= 0.01f || outer <= floor) return width;
            float w = Mathf.Clamp01((top - level) / span);
            float t = 0.5f - Mathf.Sin(Mathf.Asin(1f - 2f * w) / 3f);
            float shore = outer - t * (outer - floor);
            return Mathf.Max(width, Mathf.Min(shore + ShoreOverlap, outer));
        }

        // The profile for a whole path: each sample's bed is its ground - depth, held to never rise along the path, then eased
        // (a short running mean, held monotone again) so the bed is a slope, not a staircase following every bump.
        internal static float[] RiverBedProfile(System.Collections.Generic.List<Vector3> samples, float depth)
        {
            int n = samples.Count;
            var bed = new float[n];
            for (int i = 0; i < n; i++) bed[i] = samples[i].y - depth;
            for (int i = 1; i < n; i++) bed[i] = Mathf.Min(bed[i], bed[i - 1]);
            var raw = (float[])bed.Clone();
            for (int i = 1; i < n - 1; i++)
            {
                // Ease small steps only: a cliff (a drop of FallDrop or more to the next sample) keeps its edge for the fall.
                if (raw[i - 1] - raw[i + 1] >= FallDrop) continue;
                bed[i] = (raw[i - 1] + raw[i] + raw[i + 1]) / 3f;
            }
            for (int i = 1; i < n; i++) bed[i] = Mathf.Min(bed[i], bed[i - 1]);
            return bed;
        }

        // Fill a cylinder of ground from `bottom` up to `top` (the voxel brush on voxel ground, vanilla's level op elsewhere).
        private static void FillTo(Vector3 at, float radius, float bottom, float top)
        {
            float h = top - bottom;
            if (h <= 0.05f) return;
            if (OnVoxel(at)) { VoxelWorld.ApplyBrush(new Vector3(at.x, bottom + h * 0.5f, at.z), radius, h, VoxelBrushShape.Cylinder, VoxelBrushMode.Fill); return; }
            StampUndoable(new Vector3(at.x, top, at.z), radius, true, (d, height) => top);   // 0.2.1331: was a level op (stale height, see StampLower); only raises
        }

        // The bed profile: ring k reaches radius * BankSpread * (1 - k / BankRings) and cuts down to surface - depth * w, w the
        // smoothstep of how far in that ring is; the innermost ring is the full-depth floor (radius * BankSpread / BankRings
        // wide at least the dab's core). Each cut is from 0.5 m above the original surface down to its absolute bed height.
        private static void ProfileCut(Vector3 surfacePos, float radius, float depth)
            => SmoothBowl(surfacePos, radius * BankSpread, depth, radius * 0.35f);

        // 0.2.1067 (Fire: "smooth it out a bit so its not so stepped too on the ponds"): ONE continuous depth profile for every
        // basin the water tools dig (river dabs, lakes, pools, plunge pools). Dense concentric cuts, one per RingSpacing metres,
        // each from 0.5 m above the surface down to an ABSOLUTE height on a smoothstep from 0 at `outer` to `depth` at `floor`
        // (flat inside `floor`). Steps stay under a voxel cell, so the mesher draws a slope, never terraces.
        private static void SmoothBowl(Vector3 surfacePos, float outer, float depth, float floor)
        {
            if (outer <= 0.05f || depth <= 0f) return;
            // 0.2.1331: heightmap ground takes the same smoothstep as ONE stamp; the rings stay for the voxel brush.
            if (!OnVoxel(surfacePos)) { StampLower(surfacePos, outer, Mathf.Min(floor, outer), depth, surfacePos.y - depth); return; }
            int rings = Mathf.Clamp(Mathf.CeilToInt(outer / RingSpacing), BankRingsMin, BankRingsMax);
            float top = surfacePos.y + 0.5f;
            for (int k = 0; k < rings; k++)
            {
                float r = Mathf.Max(outer * (1f - k / (float)rings), floor);
                float t = outer > floor ? Mathf.Clamp01((outer - r + outer / rings) / (outer - floor)) : 1f;   // deeper toward the centre
                float w = t * t * (3f - 2f * t);
                float bed = surfacePos.y - depth * w;
                float h = top - bed;
                if (h <= 0.05f) continue;
                Cut(new Vector3(surfacePos.x, bed + h * 0.5f, surfacePos.z), r, h);
                if (r <= floor) break;   // the flat floor is cut once
            }
        }
        // LAKE: a basin `depth` deep at the clicked ground, sloped in three rings (full width a third deep, three quarters two
        // thirds, the middle half full depth), filled to just under the rim.
        internal const float LakeRimFreeboard = 0.25f;

        public static string LakeClick(Vector3 ground, float radius, float depth)
        {
            if (!Sane(ground, radius, "Lake")) return "[FiresWater] Lake refused: no ground under the cursor";
            depth = Mathf.Clamp(depth, RiverMinDepth, RiverMaxDepth);
            SmoothBowl(ground, radius, depth, radius * 0.5f);   // 0.2.1067: one smooth slope, the middle half full depth (was 3 terraces)
            RefreshClutter(ground, radius, true);
            float level = ground.y - LakeRimFreeboard;
            QueueWater(new Vector3(ground.x, level, ground.z), radius, depth - LakeRimFreeboard);
            ScatterShore(ground, radius, level);   // 0.2.1067: rocks / stones / stumps on the shore
            return $"[FiresWater] Lake carved at ({ground.x:F0},{ground.y:F1},{ground.z:F0}) r={radius:F1} depth {depth:F1} m, filling to y {level:F1}";
        }

        // 0.2.1246 (World Studio water.place pond): the Lake tool's basin, with the Studio's water level (NaN = the Lake tool's rim -
        // freeboard) and the shore pieces optional. The surface lays from the pending-water queue like every tool's.
        internal static string StudioPond(Vector3 ground, float radius, float depth, float level, bool shore)
        {
            StudioHeightmap = true;
            try { return StudioPondCarve(ground, radius, depth, level, shore); }
            finally { StudioHeightmap = false; }
        }

        private static string StudioPondCarve(Vector3 ground, float radius, float depth, float level, bool shore)
        {
            if (!Sane(ground, radius, "Lake")) return null;
            depth = Mathf.Clamp(depth, RiverMinDepth, RiverMaxDepth);
            SmoothBowl(ground, radius, depth, radius * 0.5f);
            RefreshClutter(ground, radius, true);
            float rim = ground.y - LakeRimFreeboard;
            float lvl = float.IsNaN(level) ? rim : Mathf.Min(level, rim);
            float bed = ground.y - depth;
            if (lvl - bed <= WaterMinDepth) return null;
            QueueWater(new Vector3(ground.x, lvl, ground.z), radius, lvl - bed);
            if (shore) ScatterShore(ground, radius, lvl);
            return $"pond carved at ({ground.x:F0},{ground.y:F1},{ground.z:F0}) r {radius:F1} depth {depth:F1} m, filling to y {lvl:F1}";
        }

        // LAKE-TO-LAKE RIVER (Fire 10-24: "select a starting pond/pool/lake with a radius tool ... shift click or alt click spine
        // points from that pond to the next one, and set an end pool, then it would auto carve and fill the two pools and the river
        // connecting them following the terrain heights"). With the Lake tool: a click digs a lake and starts a chain; LeftAlt+click
        // adds a spine point; the next click (with at least one spine point) digs the end lake and carves + fills the river through
        // the spine points, the water easing from the start lake's level to the end lake's. Each leg is ConnectChannel (routed over
        // the low ground, carved and filled). No spine point: the two lakes stay apart. A chain older than ChainStaleSeconds is dropped.
        private const float ChainStaleSeconds = 300f, ChainRiverRatio = 0.4f, ChainRiverMin = 1.5f;
        private static bool s_chainActive;
        private static Vector3 s_chainStart;     // the start lake's centre at its water level
        private static float s_chainStartRadius, s_chainAt;
        private static readonly System.Collections.Generic.List<Vector3> s_spine = new System.Collections.Generic.List<Vector3>();

        public static string LakeChainClick(Vector3 ground, float radius, float depth)
        {
            if (s_chainActive && Time.time - s_chainAt > ChainStaleSeconds) { s_chainActive = false; s_spine.Clear(); }
            string lake = LakeClick(ground, radius, depth);
            var here = new Vector3(ground.x, ground.y - LakeRimFreeboard, ground.z);
            if (!s_chainActive || s_spine.Count == 0)
            {
                s_chainActive = true; s_chainStart = here; s_chainStartRadius = radius; s_chainAt = Time.time; s_spine.Clear();
                return lake + "; LeftAlt+click to add river spine points, then click the end lake";
            }
            // end lake: start -> spine -> end, levels eased by distance along the points
            var points = new System.Collections.Generic.List<Vector3> { s_chainStart };
            points.AddRange(s_spine);
            points.Add(here);
            float total = 0f;
            var along = new float[points.Count];
            for (int i = 1; i < points.Count; i++) { total += Vector2.Distance(new Vector2(points[i - 1].x, points[i - 1].z), new Vector2(points[i].x, points[i].z)); along[i] = total; }
            float river = Mathf.Max(ChainRiverMin, Mathf.Min(s_chainStartRadius, radius) * ChainRiverRatio);
            int steps = 0;
            for (int i = 0; i < points.Count; i++)
                points[i] = new Vector3(points[i].x, Mathf.Lerp(s_chainStart.y, here.y, total > 0f ? along[i] / total : 0f), points[i].z);
            for (int i = 1; i < points.Count; i++) steps += ConnectChannel(points[i - 1], river, points[i], river);
            string done = $"{lake}; river carved and filled from the start lake through {s_spine.Count} spine point(s): {total:F0} m, {steps} step(s), "
                + $"water {s_chainStart.y:F1} -> {here.y:F1}";
            s_chainActive = false;
            s_spine.Clear();
            return done;
        }

        public static string LakeChainSpine(Vector3 ground)
        {
            if (!s_chainActive || Time.time - s_chainAt > ChainStaleSeconds) return "[FiresWater] spine point refused: click a start lake first (Lake tool), then LeftAlt+click the river's path";
            s_spine.Add(ground);
            s_chainAt = Time.time;
            return $"[FiresWater] river spine point {s_spine.Count} at ({ground.x:F0},{ground.z:F0}) - LeftAlt+click more, or click the end lake";
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
        // carved or poured there, so two clicks on two waters cut and fill a river between them.
        private const float ExistingWaterDepth = 0.3f;
        private const float PoolAdoptReach = 6f;   // m between the clicked ground and existing water's surface

        /// <summary>Whether the last Vpool click adopted existing water (river_carve_test reads it).</summary>
        internal static bool LastClickAdopted { get; private set; }

        public static void PoolClick(Vector3 surfacePos, float radius)
        {
            if (!Sane(surfacePos, radius, "Vpool")) return;
            var center = new Vector3(surfacePos.x, surfacePos.y + LevelOffset, surfacePos.z);

            // The water check asks by XZ, so a click on an
            // island over a lake would adopt the lake 500 m below. Only water near the clicked height is adopted.
            LastClickAdopted = FatWaterChecks.TryWaterAt(surfacePos.x, surfacePos.z, ExistingWaterDepth, true, out float waterLevel, out float waterBed)
                && Mathf.Abs(waterLevel - surfacePos.y) <= PoolAdoptReach;
            if (LastClickAdopted) center.y = waterLevel;
            else ShapePool(center, radius, tiered: true);

            string what = LastClickAdopted
                ? $"existing water adopted at ({center.x:F0},{center.y:F1},{center.z:F0}), {waterLevel - waterBed:F1}m deep"
                : $"pool placed at ({center.x:F0},{center.y:F0},{center.z:F0}) r={radius:F1}";
            // A pool dug at a cliff or skyland edge
            // hangs a fall from each lip its circle crosses.
            if (!LastClickAdopted)
            {
                string falls = PondTool.EdgeFalls(center, center.y, radius + EdgeLook);   // just outside its own carve
                if (falls != null) what += "; " + falls;
            }
            Chain(center, radius, what);
        }

        // The two-click river's memory, shared by Vpool and Vpond (place two ponds and a carved, filled river
        // auto-connects them): with an anchor from the
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
        // A POOL (a click, tiered) is cut for life, from vanilla's own runtime bands (FiresClutterWaterEcology):
        // an outer bowl FishDepth deep (Fish1 spawns on a bed 1.5-3 m down) around an inner well WellDepth deep across
        // WellRatio of the radius (instanced_waterlilies needs a bed 3.2-4.5 m down, instanced_vass 3.1-4.0 m). A pool
        // smaller than TieredMinRadius, and every channel stamp, keeps the single bowl of before.
        private const float FishDepth = 2.2f, WellDepth = 3.8f, WellRatio = 0.55f, TieredMinRadius = 4f;

        // Vfall's landing: the bowl and its water only, nothing cleared above, so the cliff behind it stands.
        internal static void PlungePool(Vector3 center, float radius)
        {
            if (!Sane(center, radius, "Vfall pool")) return;
            float depth = Mathf.Max(PoolMinDepth, radius * PoolDepthRatio);
            SmoothBowl(center, radius, depth, radius * 0.4f);   // 0.2.1067: a smooth bowl, not a cylinder
            RefreshClutter(center, radius, true);
            QueueWater(center, radius, depth);
        }

        private static void ShapePool(Vector3 center, float radius, bool tiered = false)
        {
            bool twoTier = tiered && radius >= TieredMinRadius;
            // 0.2.1092 (Fire: pools deep enough for fish): a clicked pool smaller than TieredMinRadius was max(1.5, r x 0.35) =
            // 1.5 m, the shallow edge of Fish1's 1.5-3 m band; now at least PondFishMinDepth in the middle. Channel stamps
            // (not tiered) keep PoolMinDepth.
            float depth = twoTier ? FishDepth : Mathf.Max(tiered ? PondFishMinDepth : PoolMinDepth, radius * PoolDepthRatio);
            if (tiered)
            {
                float centre = twoTier ? WellDepth : depth;
                FiresLogger.LogInfo($"[WaterLife] pond at ({center.x:0.0},{center.z:0.0}) r {radius:0.0}: depth at centre {centre:0.0} m{(twoTier ? $" (fish bowl {FishDepth:0.0} m)" : "")}, Fish1 band 1.5-3 m {((twoTier ? FishDepth : depth) >= 1.5f && (twoTier ? FishDepth : depth) <= 3f ? "ok" : "NOT met")}");
            }

            var lip = new Vector3(center.x, center.y + LevelClearHeight * 0.5f, center.z);
            Cut(lip, radius, LevelClearHeight);

            SmoothBowl(center, radius, depth, radius * 0.4f);   // 0.2.1067: smooth bowl + well (were flat cylinders = terraces)
            if (twoTier)
            {
                SmoothBowl(center, radius * (WellRatio + 0.2f), WellDepth, radius * WellRatio * 0.6f);
                depth = WellDepth;
            }

            ScatterShore(center, radius, center.y);   // 0.2.1067
            RefreshClutter(center, radius, true);
            QueueWater(center, radius, depth);
        }

        // The WaterMeshCombiner shapes
        // a disk to the terrain it samples when it builds (min of the generator height and ZoneSystem.GetGroundHeight, a
        // raycast onto the colliders), and the carve's colliders are rebuilt asynchronously, so a disk laid in the same frame
        // as its carve would sit on the UNCARVED ground, about 0 m deep (no water). The water waits until a terrain ray at the pool centre
        // reaches the carved bed (below centre - depth/2), at most WaterWaitCap seconds, then is laid and logged.
        private const float WaterWaitMin = 0.3f, WaterWaitCap = 10f, BedProbeAbove = 50f, BedProbeLength = 100f;
        private static int s_terrainMask;

        internal sealed class PendingWater { public Vector3 Center; public float Radius, Depth, QueuedAt; public WaterDiskKind Kind = WaterDiskKind.Pond; public bool NeedsCarve, Gone, Refused; }   // 0.2.1343: NeedsCarve = a river step's water, whichever kind it is tagged

        // 0.2.1091: the kind LayWater stamps on its disk (DrainPendingWater sets it from the queued entry).
        private static WaterDiskKind s_layKind = WaterDiskKind.Pond;
        private static readonly System.Collections.Generic.List<PendingWater> s_pendingWater = new System.Collections.Generic.List<PendingWater>();

        private static void QueueWater(Vector3 center, float radius, float depth) => QueueWaterEntry(center, radius, depth);

        // The entry stays editable until it is laid (a river step pulls the water back from a waterfall's lip).
        private static PendingWater QueueWaterEntry(Vector3 center, float radius, float depth)
        {
            var w = new PendingWater { Center = center, Radius = radius, Depth = depth, QueuedAt = Time.realtimeSinceStartup };
            s_pendingWater.Add(w);
            s_finishOpen?.Water.Add(w);   // 0.2.1348
            return w;
        }

        internal static int PendingWaterCount => s_pendingWater.Count;   // 0.2.1346
        // 0.2.1346 (r22: disks refused at the cap "bed at y 50.0, wanted <= 47.5" while the carve applied bed 46, and a 156-chunk swap landed after it): the cap was blind to the
        // ground still arriving. Past it a river disk waits while its zone is still building or swapping, bounded; a final refusal tells the admin and logs a Warning.
        private const float WaterSwapWaitCap = 45f, RefusedTellSeconds = 5f;
        private static int s_waterSwapWaits, s_waterRefused, s_waterRefusedUntold; private static double s_waterSwapWaitSum, s_waterSwapWaitWorst;
        private static float s_refusedTellAt, s_waterWaitLogAt; private static bool s_waterWaitProof;
        private static readonly System.Collections.Generic.HashSet<PendingWater> s_waterExtended = new System.Collections.Generic.HashSet<PendingWater>();
        private static bool WaterWaitsForSwap => FiresVoxelConfig.RiverWaterWaitsForSwap != null && FiresVoxelConfig.RiverWaterWaitsForSwap.Value;
        private static void NoteWaterExtended(PendingWater w, float waited)
        {
            s_waterExtended.Remove(w);
            s_waterSwapWaits++; s_waterSwapWaitSum += waited; if (waited > s_waterSwapWaitWorst) s_waterSwapWaitWorst = waited;
        }
        private static void TellRefused(Vector3 at, string text)
        {
            s_waterRefused++; s_waterRefusedUntold++;
            float now = Time.realtimeSinceStartup;
            if (now < s_refusedTellAt || Player.m_localPlayer == null) return;
            s_refusedTellAt = now + RefusedTellSeconds;
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, s_waterRefusedUntold > 1 ? $"River water not laid in {s_waterRefusedUntold} spots, near ({at.x:F0},{at.z:F0}): {text}" : $"River water not laid at ({at.x:F0},{at.z:F0}): {text}", 0, null, false);
            s_waterRefusedUntold = 0;
        }

        private static void DrainPendingWater()
        {
            if (s_pendingWater.Count == 0) return;
            if (s_terrainMask == 0) s_terrainMask = LayerMask.GetMask("terrain");
            float now = Time.realtimeSinceStartup;
            if (FiresLogger.DebugEnabled && now >= s_waterWaitLogAt)
            {
                s_waterWaitLogAt = now + 10f;
                if (s_waterSwapWaits > 0 || s_waterRefused > 0) FiresLogger.LogDebug($"[Pond] river water past the 10 s cap, 10 s: {s_waterSwapWaits} disk(s) waited on their ground swap, worst {s_waterSwapWaitWorst:0.0} s, total {s_waterSwapWaitSum:0.0} s; refused {s_waterRefused} (0.2.1346)");
                s_waterSwapWaits = 0; s_waterRefused = 0; s_waterSwapWaitSum = 0; s_waterSwapWaitWorst = 0;
            }
            for (int i = s_pendingWater.Count - 1; i >= 0; i--)
            {
                var w = s_pendingWater[i];
                float waited = now - w.QueuedAt;
                if (waited < WaterWaitMin) continue;
                float bedLine = w.Center.y - w.Depth * 0.5f;
                float bed = float.NaN;
                string hitName = "nothing";   // 0.2.1346
                if (Physics.Raycast(new Vector3(w.Center.x, w.Center.y + BedProbeAbove, w.Center.z), Vector3.down, out RaycastHit hit, BedProbeLength, s_terrainMask))
                { bed = hit.point.y; hitName = hit.collider != null ? hit.collider.name : "no collider"; }
                bool seen = !float.IsNaN(bed) && bed <= bedLine;
                if (!seen && waited < WaterWaitCap) continue;
                bool riverWater = w.Kind == WaterDiskKind.River || w.NeedsCarve;
                if (!seen && riverWater && WaterWaitsForSwap && waited < WaterSwapWaitCap && VoxelZoneStreamer.ZoneEditBusy(ZoneSystem.GetZone(w.Center)))
                {
                    if (!s_waterWaitProof) { s_waterWaitProof = true; FiresLogger.LogInfo("[Pond] river water past 10 s waits on its zone's ground swap, up to 45 s; a final refusal shows on screen (0.2.1346)"); }
                    s_waterExtended.Add(w);
                    continue;   // the swap's landing shows as seen on a later tick
                }
                if (s_waterExtended.Contains(w)) NoteWaterExtended(w, waited);
                s_pendingWater.RemoveAt(i);
                w.Gone = true;   // 0.2.1348
                // 0.2.1100: a river's water is laid only over its carved bed. If the carve never showed up by the cap (refused,
                // blocked, or never applied), the river water is not laid on undug ground (ponds/pours keep the old behaviour).
                if (!seen && (w.Kind == WaterDiskKind.River || w.NeedsCarve))
                {
                    w.Refused = true;   // 0.2.1348
                    string refused = $"[Pond] river water NOT laid at ({w.Center.x:F0},{w.Center.y:F1},{w.Center.z:F0}) r={w.Radius:F1}: its carve never reached the bed after {waited:F0} s (bed at y {(float.IsNaN(bed) ? "none" : bed.ToString("F1"))}, wanted <= {bedLine:F1}, ray hit {hitName})";
                    if (WaterWaitsForSwap)
                    {
                        FiresLogger.LogWarning(refused);
                        TellRefused(w.Center, $"the ground there did not dig down (y {(float.IsNaN(bed) ? "none" : bed.ToString("F1"))}, needs {bedLine:F1}). Carve it again.");
                    }
                    else FiresLogger.LogInfo(refused);
                    continue;
                }
                s_layKind = w.Kind;
                try { LayWater(w.Center, w.Radius, w.Depth); }
                finally { s_layKind = WaterDiskKind.Pond; }
                FiresLogger.LogInfo($"[FiresWater] pool water laid at ({w.Center.x:F0},{w.Center.y:F1},{w.Center.z:F0}) r={w.Radius:F1} d={w.Depth:F1} "
                    + (seen ? $"after {waited:F1} s (bed seen at y {bed:F1})"
                            : $"after the {WaterWaitCap:F0} s cap, bed still at y {(float.IsNaN(bed) ? "none" : bed.ToString("F1"))} (wanted <= {bedLine:F1})"));
            }
        }

        // The two-click river (click one water source, then a second, and a river
        // auto-carves between the two of them): Vpool's second click connects to the first.
        // The channel is ROUTED, not a straight line. From A it steps toward B one spacing at a time, taking of five
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
                // Water over a cliff or a skyland's edge becomes a waterfall: a route point whose ground lies
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

        // ── Vpool + LeftAlt: fill a depression to its rim (carve, then fill a
        // depression with water) ───────────────────────────────────────────────────────────────────────────────────────
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
            CustomWaterManager.NextKind = s_layKind;   // 0.2.1091: River from the river tools' queue, Pond for everything else
            try { disk = CustomWaterManager.CreateDiskOfType(center, radius, depth, CustomLiquidType.Water); }
            catch (System.Exception ex)
            {
                CustomWaterManager.NextKind = WaterDiskKind.Pond;
                FiresLogger.LogError(
                    $"[FiresWater] water placement THREW at ({center.x:F0},{center.y:F0},{center.z:F0}) "
                    + $"r={radius:F1} d={depth:F1}: {ex}");
                return;
            }
            if (disk != null)
            {
                // At Debug: the surface this dab laid.
                FiresLogger.LogDebug($"[FiresWater] water disk laid at ({center.x:F0},{center.y:F1},{center.z:F0}) r={radius:F1} d={depth:F1}");
                VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.NotePlacedWater(center, radius);   // the wet bed / shore paint follows the new water
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
        // 0.2.1080 (Fire: "we are STILL not ... removing the old clutter to draw our own"; shot 502: Meadows ferns standing in the
        // carved bed): the world's own vegetation inside a carve (plants, bushes, saplings, trees: the same set the drowned-
        // vegetation sweep clears; never a player's piece, rocks or water plants) is destroyed when the carve cuts under it,
        // instead of waiting for water to drown it. The bed then only grows what the water tools and the water ecology add.
        private static int s_carveVegCleared;
        private static void ClearCarvedVegetation(Vector3 center, float radius)
        {
            if (ZNetScene.instance == null || ZNetScene.instance.m_instances == null) return;
            float r2 = radius * radius;
            var doomed = new System.Collections.Generic.List<GameObject>();
            foreach (var kv in ZNetScene.instance.m_instances)
            {
                var view = kv.Value;
                if (view == null) continue;
                var go = view.gameObject;
                var p = go.transform.position;
                float dx = p.x - center.x, dz = p.z - center.z;
                if (dx * dx + dz * dz > r2 || Mathf.Abs(p.y - center.y) > radius + 6f) continue;
                if (!VerdantsAscent.Terrain.Voxel.FatUnderWaterPlacement.IsRefusedInstance(go)) continue;
                var zdo = view.GetZDO();
                if (zdo != null && zdo.GetLong(ZDOVars.s_creator) != 0L) continue;
                doomed.Add(go);
            }
            foreach (var go in doomed) FiresCore.Net.NetworkObjectHelper.SafeDestroy(go);
            if (doomed.Count > 0)
            {
                s_carveVegCleared += doomed.Count;
                FiresLogger.LogDebug($"[FiresWater] carve cleared {doomed.Count} vegetation object(s) at ({center.x:F0},{center.z:F0}) r {radius:F1} ({s_carveVegCleared} this session)");
            }
        }

        // 0.2.1081 (Fire: "grass ferns and shrubs can be on the shores out of the water, not in it"): once the placed water really
        // answers (VoxelZoneStreamer.TickPendingWet), vegetation objects standing UNDER it are removed; the dry bank keeps its own.
        internal static int ClearUnderWater(Vector3 center, float reach)
        {
            if (ZNetScene.instance == null || ZNetScene.instance.m_instances == null) return 0;
            float r2 = reach * reach;
            var doomed = new System.Collections.Generic.List<GameObject>();
            foreach (var kv in ZNetScene.instance.m_instances)
            {
                var view = kv.Value;
                if (view == null) continue;
                var go = view.gameObject;
                var p = go.transform.position;
                float dx = p.x - center.x, dz = p.z - center.z;
                if (dx * dx + dz * dz > r2) continue;
                if (!VerdantsAscent.Terrain.Voxel.FatUnderWaterPlacement.IsRefusedInstance(go)) continue;
                var zdo = view.GetZDO();
                if (zdo != null && zdo.GetLong(ZDOVars.s_creator) != 0L) continue;
                if (!VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(p.x, p.z, 0.05f, false, out float surface, out _) || surface <= p.y + 0.05f) continue;
                doomed.Add(go);
            }
            foreach (var go in doomed) FiresCore.Net.NetworkObjectHelper.SafeDestroy(go);
            if (doomed.Count > 0) s_carveVegCleared += doomed.Count;
            return doomed.Count;
        }

        private static void RefreshClutter(Vector3 center, float radius, bool carved = false)
        {
            if (carved) { PaintCarve(center, radius); ClearCarvedVegetation(center, radius); }
            FatSuperFlat.AllowWaterClutter(center, radius * BankPaintRatio);
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
            WaterSheets.Tick();   // keep the placed-water surface index fresh (the wet band's SurfaceAt)
            DrainPendingWater();
            TickRiverFinish();   // 0.2.1348
            CloseCutStep();
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
            if (FatDiag.WaterClutter) FiresLogger.LogDebug($"[FiresWater] clutter refresh at ({s_clutterCenter.x:F0},{s_clutterCenter.z:F0}) r {s_clutterRadius:F1}; paint {CarvePaint} on {s_paintComps} comps");
            if (s_paintCarves > 0)
            {
                // 0.2.1080: no more estimated cell/patch counts (they read as success while nothing showed). The real sand/wet
                // writes are counted by the wet-mask stamp itself ("[FiresWetMask] placed-water band: ..." lines).
                FiresLogger.LogDebug($"[FiresWater] river paint: own paint reset on {s_paintCarves} carve(s), radius {s_clutterRadius:F0} m; the sand bed + wet band are written by the wet-mask stamp once the water exists");
            }
            s_paintComps = 0;
            s_paintCarves = 0;
        }

        // 0.2.1052 (Fire 10-26: "our water tools are not properly laying in the paint and clutter"): every carve now paints its
        // bank ring (radius x BankPaintRatio) and then its bed, through the same vanilla TerrainComp path as va_voxel_paint
        // (FiresUnifiedPaint.MirrorPaint), so the paint persists in the zone and syncs to every client. The note at the top of
        // this file ("NOTHING IS PAINTED") relied on vanilla's height blend, which voxel ground never gets. Vanilla has no sand
        // paint: the bank is Dirt (bare earth along the water) and the bed is Cultivate (the darker, wetter earth).
        // 0.2.1067 (Fire 10-26: "a sandy/ocean type terrain paint (like vanilla rivers) and ... our clutter draws on it"): that
        // Dirt / Cultivate pair was what his river showed: a pale tilled bed and a brown dirt edge, and it hid the sand. A vertex
        // with ground paint of its own never wears the wet-shore band (VoxelLayerPaint: "a vertex with no paint of its own"),
        // and the band is the sandy bed + wet shore the placed water already drives (VoxelZoneStreamer.PlacedWet01, 0.2.1023,
        // the same mask path generated rivers use). Dirt and Cultivate also thin vanilla grass. So a carve now RESETS the paint
        // over its bed and bank (persisted + synced through the same TerrainComp path) and lets the sand and the clutter show.
        private const float BankPaintRatio = 1.25f;
        private const TerrainModifier.PaintType CarvePaint = TerrainModifier.PaintType.Reset;
        private static int s_paintComps, s_paintCarves;

        private static void PaintCarve(Vector3 center, float radius)
        {
            s_paintComps += VerdantsAscent.Terrain.Paint.FiresUnifiedPaint.MirrorPaint(center, radius * BankPaintRatio, CarvePaint, false);
            s_paintCarves++;
        }

        // 0.2.1067 (Fire: "make sure it gets ... the rocks and stumps"): vanilla seeds its rocks and stumps once, when a zone
        // generates (ZoneSystem.PlaceVegetation), so water carved later never got any. After a carve the tool scatters a few
        // vanilla shore pieces on the bank and in the shallows: pickable stones and flint, small rocks, a stump. They are real
        // ZNetView prefabs, so they persist in the save and sync to every peer like anything a player places; placed once by the
        // peer that carved, from a seed made of the spot's position, so the same carve always lays the same pieces (never a
        // per-client roll). Pieces already within ShoreSpacing are not doubled.
        private static readonly string[] ShorePieces = { "Pickable_Flint", "Pickable_Flint", "Pickable_Stone" };   // 0.2.1081 (Fire: "pickable flint should dot the banks")
          // 0.2.1071 (Fire: "much smaller rocks"): no boulders (Rock_3, rock4_coast) or stumps
        private const float ShoreSpacing = 4f;
        private static int s_shorePlaced;

        internal static void ScatterShore(Vector3 centre, float radius, float waterLevel)
        {
            if (ZNetScene.instance == null || radius < 1f) return;
            if (ZoneReveal.Queue(ZoneSystem.GetZone(centre), () => ScatterShore(centre, radius, waterLevel))) return;   // 0.2.1346: on the swapped ground, in the swap frame
            var rng = new System.Random(unchecked(Mathf.RoundToInt(centre.x * 4f) * 73856093 ^ Mathf.RoundToInt(centre.z * 4f) * 19349663));
            int count = Mathf.Clamp(Mathf.RoundToInt(radius * 0.5f), 1, 6);
            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                string name = ShorePieces[rng.Next(ShorePieces.Length)];
                GameObject prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab == null) continue;
                float ang = (float)(rng.NextDouble() * Mathf.PI * 2.0), start = (float)rng.NextDouble();
                if (!TryShoreEdge(centre, radius, waterLevel, ang, start, out Vector3 at)) continue;
                if (Physics.CheckSphere(at + Vector3.up * 0.5f, ShoreSpacing * 0.5f, LayerMask.GetMask("Default", "static_solid", "piece"), QueryTriggerInteraction.Ignore)) continue;
                UnityEngine.Object.Instantiate(prefab, at, Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f));
                placed++;
            }
            s_shorePlaced += placed;
            if (placed > 0)
                FiresLogger.LogDebug($"[FiresWater] shore pieces: {placed} placed around ({centre.x:F0},{centre.z:F0}) r {radius:F1} on the dry bank ({ShoreAboveMin:0.00}-{ShoreAboveMax:0.0} m above the water at {waterLevel:F1}; flint / stones; {s_shorePlaced} this session)");
        }

        // 0.2.1072 (Fire 10-26: "keep them to the rivers edges"): a piece stands only where the ground is within ShoreEdgeBand of
        // the water level, i.e. the waterline and the shallows, never up the bank or out in deep water. Along the piece's angle
        // the ring from 0.6 to 1.6 x the radius is walked in ShoreEdgeSteps, starting at a seeded offset so pieces don't line up;
        // the first spot in the band wins, none = no piece.
        private const float ShoreEdgeBand = 0.4f;
        private const float ShoreAboveMin = 0.05f, ShoreAboveMax = 0.8f;
        private const int ShoreEdgeSteps = 8;

        private static bool TryShoreEdge(Vector3 centre, float radius, float waterLevel, float ang, float start, out Vector3 at)
        {
            at = default;
            float cos = Mathf.Cos(ang), sin = Mathf.Sin(ang);
            for (int s = 0; s < ShoreEdgeSteps; s++)
            {
                float t = ((start + s / (float)ShoreEdgeSteps) % 1f);
                float dist = radius * (0.6f + t);
                float x = centre.x + cos * dist, z = centre.z + sin * dist;
                if (!ZoneSystem.instance.GetGroundHeight(new Vector3(x, centre.y + 20f, z), out float gy)) continue;
                // 0.2.1081 (Fire: "flint should dot the banks"): the dry bank just above the line, never in the water.
                if (gy - waterLevel < ShoreAboveMin || gy - waterLevel > ShoreAboveMax) continue;
                at = new Vector3(x, gy, z);
                return true;
            }
            return false;
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

    // 0.2.1348: one river, all four fixes read at the player's spot. Console only (not an FDT direct command). "repaint" also writes the bed paint there again.
    internal static class RiverCheckCommand
    {
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Register
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand("fat_river_check", "[FiresAdminTerrain] [r] [repaint] placed water around you: bed paint, bank gaps, fall lip vs water, clutter refusals (Info)",
                    args => args.Context?.AddString(Run(args)), isCheat: true);
            }
        }

        private static string Run(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null) return "[RiverCheck] no local player";
            float r = 24f; bool repaint = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "repaint") repaint = true;
                else if (float.TryParse(args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) r = Mathf.Clamp(v, 4f, 64f);
            }
            Vector3 p = player.transform.position;
            if (repaint) VoxelZoneStreamer.RepaintPlacedWater(p, r);
            int bedWet = 0, bedDry = 0, gaps = 0; float worst = 0f;
            int ri = Mathf.CeilToInt(r);
            for (int dz = -ri; dz <= ri; dz++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    float x = Mathf.Floor(p.x) + dx + 0.5f, z = Mathf.Floor(p.z) + dz + 0.5f;
                    if (FatWaterChecks.TryWaterAt(x, z, 0.05f, false, out _, out _))
                    {
                        if (VoxelZoneStreamer.PaintWet01Uncached(x, z) >= 0.5f) bedWet++; else bedDry++;
                        continue;
                    }
                    // A dry column beside water whose ground stands under that water: the sheet's skirt shows there.
                    if (!FatWaterChecks.SolidGround(x, z, out float g)) continue;
                    float side = float.MinValue;
                    for (int k = 0; k < 4; k++)
                        if (FatWaterChecks.TryWaterAt(x + (k == 0 ? 1f : k == 1 ? -1f : 0f), z + (k == 2 ? 1f : k == 3 ? -1f : 0f), 0.05f, false, out float s, out _) && s > side) side = s;
                    if (side > g + 0.1f) { gaps++; worst = Mathf.Max(worst, side - g); }
                }
            var sb = new System.Text.StringBuilder();
            sb.Append($"[RiverCheck] ({p.x:0},{p.z:0}) r {r:0}{(repaint ? ", repainted first (the stamp follows within a second; run again)" : "")}: bed {bedWet} wet + sand, {bedDry} dry under water; bank gaps {gaps}, worst {worst:0.00} m; clutter refusals this session: ");
            sb.Append(VerdantsAscent.Performance.ClutterSpread.OwnWaterStats());
            ToolFalls.DescribeNear(p, r, sb);
            WaterMeshCombinerAnchor.DescribeNear(p, r, sb);   // 0.2.1349 (Fire: "every time we log back in its broken"): proves a relog
            string s2 = sb.ToString();
            FiresLogger.LogInfo(s2);
            return s2;
        }
    }

    // ── Vfall: a waterfall the player places (own waterfalls and rivers, also
    // on the sky islands) ─────────────────────────────────────────────────
    // A click near a drop finds the lip (walking out along the view until the ground falls away), the landing (straight
    // down past the lip), cuts a small spillway notch at the lip and hangs a curtain of the brush's width from it to the
    // landing, with mist (FiresWaterfalls / FiresWaterfallMist curtain), a source pour at the lip and
    // a pool where it lands. Remove (middle mouse) removes the falls whose lip is in the brush.
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

        // The ground near `hint` in the column at (x, z): the voxel density crossing when the column is loaded ("voxel"), else
        // the first collider a short ray down from just over the hint finds ("ray", a cave floor or a vanilla heightmap).
        // `how` says which, or why neither answered.
        private static int s_lipMask;

        private static bool SurfaceNear(float x, float z, float hint, out float y, out string how)
        {
            if (VoxelWorld.TryFindSurfaceY(x, z, hint, LipSurfaceReach, out y)) { how = "voxel"; return true; }
            if (s_lipMask == 0) s_lipMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            if (Physics.Raycast(new Vector3(x, hint + 1.5f, z), Vector3.down, out RaycastHit hit, LipSurfaceReach + 1.5f, s_lipMask, QueryTriggerInteraction.Ignore))
            {
                y = hit.point.y; how = "ray (no voxel crossing within " + LipSurfaceReach.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " m)";
                return true;
            }
            y = hint; how = "no voxel crossing and no collider within reach";
            return false;
        }

        // The Vfall click. Returns the Debug line (starts "[FiresWater] Vfall refused" when nothing was placed).
        internal static string Place(Vector3 cursor, Vector3 view, float radius)
        {
            string why = Plan(cursor, view, true, out Vector3 lip, out Vector2 flat, out float landY, out bool onto);
            if (why != null) return Refused(why);
            var out3 = new Vector3(flat.x, 0f, flat.y);

            // The spillway: the notch the water leaves by. It is also what makes the chunk an edited one, whose anchor
            // carries the fall.
            float notchR = Mathf.Min(radius, NotchMaxRadius);
            Vector3 notch = new Vector3(lip.x, lip.y - NotchDepth * 0.5f, lip.z);
            string cut = (WaterShapingTools.StudioHeightmap && !WaterShapingTools.OnVoxel(notch)) ? "notch skipped (Studio heightmap carve, 0.2.1256)" : VoxelWorld.ApplyBrush(notch, notchR, NotchDepth, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
            var key = ChunkOf(notch);
            if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null || !chunk.HasBrushEdits)
                return Refused(F("the spillway notch at ({0:0.0},{1:0.0},{2:0.0}) did not take ({3}), so there is no chunk record to keep the fall in",
                    notch.x, notch.y, notch.z, cut));

            var fall = new Fall { Lip = new Vector3(lip.x, lip.y - NotchDepth, lip.z), Out = flat, Width = Mathf.Max(2f, radius * 2f), LandY = landY, OntoGround = onto };
            ListFor(key).Add(fall);
            VoxelWorld.RequestSave(key);
            Rebuild(key);

            // The fall is only its curtain (and mist), falling down the steep terrain under it; no river mesh. No pour at the lip and no
            // plunge pool at the foot: water above or below it is drawn with the water tools when it is wanted.
            return F("[FiresWater] Vfall placed: lip ({0:0.0},{1:0.0},{2:0.0}) out ({3:0.00},{4:0.00}) width {5:0.0} m, drop {6:0.0} m onto {7} at y {8:0.0}; chunk {9} holds {10} fall(s)",
                fall.Lip.x, fall.Lip.y, fall.Lip.z, flat.x, flat.y, fall.Width, fall.Lip.y - landY, onto ? "ground" : "water", landY, key, s_byChunk[key].Count);
        }

        // 0.2.1086 (Fire: "v fall should show an actual ghost preview of what the fall will look like so we can position it"): the lip
        // walk + landing ray, shared by the click (Place) and the held-tool ghost (Preview), so the ghost is exactly what the click
        // lays. Returns null when a fall fits, else why not. Reads only; writes nothing.
        private static string Plan(Vector3 cursor, Vector3 view, bool log, out Vector3 lip, out Vector2 flat, out float landY, out bool onto)
        {
            lip = default; landY = 0f; onto = true;
            flat = new Vector2(view.x, view.z);
            if (flat.sqrMagnitude < 0.01f) return "look across the drop, not straight down";
            flat.Normalize();
            var out3 = new Vector3(flat.x, 0f, flat.y);

            // The lip: walk out along the view from the cursor while the ground stays within LipDrop of the cursor.
            // 0.2.1026 (Fire 10-24: "v fall just doesnt work in caves at all"): the cursor IS the crosshair hit (the carve tools' hit,
            // cave floors included), so the lip starts there; the voxel column scan only refines it, and a column it cannot read
            // falls back to a short ray down from just over the point.
            if (!SurfaceNear(cursor.x, cursor.z, cursor.y, out float lipY, out string how))
                return F("no ground at the cursor ({0:0.0},{1:0.0},{2:0.0}): {3}", cursor.x, cursor.y, cursor.z, how);
            if (log && how != "voxel") FiresLogger.LogDebug(F("[FiresWater] Vfall lip at the cursor by {0}", how));
            lip = new Vector3(cursor.x, lipY, cursor.z);
            bool edge = false;
            for (float d = LipWalkStep; d <= LipWalkReach; d += LipWalkStep)
            {
                float x = cursor.x + out3.x * d, z = cursor.z + out3.z * d;
                if (!SurfaceNear(x, z, lipY, out float y, out _) || y < lipY - LipDrop)
                {
                    edge = true;
                    break;
                }
                lip = new Vector3(x, y, z);
                lipY = Mathf.Max(lipY, y);
            }
            if (!edge) return F("no drop within {0:0} m past the cursor along the view - aim at the ground just before an edge", LipWalkReach);
            lip.y = lipY;

            // The landing: straight down past the lip, onto terrain or into water.
            Vector3 probe = lip + out3 * LandingOut;
            int mask = LayerMask.GetMask("terrain", "Default", "static_solid");
            if (!Physics.Raycast(probe + Vector3.down * 0.5f, Vector3.down, out var hit, LandingReach, mask))
                return F("nothing below the lip at ({0:0.0},{1:0.0},{2:0.0}) within {3:0} m", probe.x, lip.y, probe.z, LandingReach);
            landY = hit.point.y;
            onto = true;
            if (FatWaterChecks.TryWaterAt(hit.point.x, hit.point.z, 0.05f, false, out float water, out _)
                && water > landY && water < lip.y - MinDrop && water - landY < 40f)
            {
                landY = water;
                onto = false;
            }
            float drop = lip.y - landY;
            if (drop < MinDrop) return F("the drop at the lip is {0:0.0} m (a fall needs {1:0} m)", drop, MinDrop);
            return null;
        }

        // ── 0.2.1086: the Vfall ghost. While Vfall is held: the fall the click would lay, from Plan (same lip walk, same landing ray,
        // same width): the spillway notch as a ring at the lip, the curtain drawn by the falls' own mesher (AppendCurtain) in a
        // see-through copy of the falls' material from the lowered lip down to the landing, and a ring for the pool where it lands.
        // Rebuilt only when the cursor moves 0.25 m, the view turns 3 degrees or the width changes; nothing written.
        private static GameObject s_ghost;
        private static Mesh s_ghostMesh;
        private static MeshRenderer s_ghostRenderer;
        private static LineRenderer s_ghostSpill, s_ghostPool;
        private static Material[] s_ghostMats;
        private static Vector3 s_ghostCursor = new Vector3(float.NaN, 0f, 0f);
        private static Vector2 s_ghostView;
        private static float s_ghostRadius = -1f;
        private static bool s_ghostOk;
        private static string s_ghostWhy;
        private static float s_ghostLogAt;
        private const int GhostRingPoints = 32;
        private const float GhostAlpha = 0.45f;
        private static readonly Color GhostGood = new Color(0.35f, 1f, 0.45f, 0.9f), GhostBad = new Color(1f, 0.35f, 0.3f, 0.9f);

        // Returns null when the ghost shows a fall that fits, else why the click would be refused (the rings go red).
        internal static string Preview(Vector3 cursor, Vector3 view, float radius, Material lineMat)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            var v = new Vector2(view.x, view.z);
            if (v.sqrMagnitude > 0.0001f) v.Normalize();
            bool changed = float.IsNaN(s_ghostCursor.x) || (cursor - s_ghostCursor).sqrMagnitude > 0.0625f
                || Vector2.Dot(v, s_ghostView) < 0.9986f || Mathf.Abs(radius - s_ghostRadius) > 0.01f;
            EnsureGhost(lineMat);
            if (!s_ghost.activeSelf) s_ghost.SetActive(true);
            if (!changed) return s_ghostOk ? null : s_ghostWhy;
            s_ghostCursor = cursor; s_ghostView = v; s_ghostRadius = radius;

            string why = Plan(cursor, view, false, out Vector3 lip, out Vector2 flat, out float landY, out bool onto);
            s_ghostOk = why == null; s_ghostWhy = why;
            s_ghostMesh.Clear();
            if (!s_ghostOk)
            {
                s_ghostRenderer.enabled = false;
                s_ghostPool.gameObject.SetActive(false);
                Ring(s_ghostSpill, new Vector3(cursor.x, cursor.y + 0.1f, cursor.z), Mathf.Min(radius, NotchMaxRadius), GhostBad);
                return why;
            }

            // The same fall Place records and Rebuild draws.
            var f = new Fall { Lip = new Vector3(lip.x, lip.y - NotchDepth, lip.z), Out = flat, Width = Mathf.Max(2f, radius * 2f), LandY = landY, OntoGround = onto };
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
            int verts = 0;
            bool drew = false;
            if (s_ghostMats != null)
            {
                FiresWaterfalls.BeginMesh();
                drew = FiresWaterfalls.AppendCurtain(s_columns, out3, FiresWaterfalls.EdgeLipOut, 0f, 0f, FiresWaterfalls.TerrainProfile, approach: false, placed: true);
                verts = FiresWaterfalls.EndMesh(s_ghostMesh);
            }
            s_ghostRenderer.enabled = drew;
            Ring(s_ghostSpill, new Vector3(lip.x, lip.y + 0.05f, lip.z), Mathf.Min(radius, NotchMaxRadius), GhostGood);
            s_ghostPool.gameObject.SetActive(true);
            Ring(s_ghostPool, new Vector3(lip.x + out3.x * LandingOut, landY + 0.05f, lip.z + out3.z * LandingOut), f.Width * 0.5f + 1f, GhostGood);
            if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_ghostLogAt)
            {
                s_ghostLogAt = Time.unscaledTime + 2f;
                FiresLogger.LogDebug(F("[FiresWater] Vfall ghost rebuilt: lip ({0:0.0},{1:0.0},{2:0.0}) out ({3:0.00},{4:0.00}) width {5:0.0} m, drop {6:0.0} m onto {7} at y {8:0.0}; curtain {9} ({10} vertices)",
                    f.Lip.x, f.Lip.y, f.Lip.z, flat.x, flat.y, f.Width, f.Lip.y - landY, onto ? "ground" : "water", landY, drew ? "drawn" : "NOT drawn", verts));
            }
            return null;
        }

        internal static void HidePreview()
        {
            if (s_ghost != null && s_ghost.activeSelf) s_ghost.SetActive(false);
            s_ghostCursor = new Vector3(float.NaN, 0f, 0f);
        }

        private static void EnsureGhost(Material lineMat)
        {
            if (s_ghost != null) return;
            s_ghost = new GameObject("fires_vfall_ghost");
            Object.DontDestroyOnLoad(s_ghost);
            s_ghostMesh = new Mesh { name = "vfall_ghost" };
            s_ghost.AddComponent<MeshFilter>().sharedMesh = s_ghostMesh;
            s_ghostRenderer = s_ghost.AddComponent<MeshRenderer>();
            s_ghostRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s_ghostRenderer.receiveShadows = false;
            var src = FiresWaterfalls.Materials;
            if (src != null)
            {
                s_ghostMats = new Material[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    if (src[i] == null) continue;
                    var m = new Material(src[i]) { name = src[i].name + " (Vfall ghost)" };
                    foreach (var prop in new[] { "_Color", "_TintColor", "_BaseColor" })
                        if (m.HasProperty(prop)) { var c = m.GetColor(prop); c.a *= GhostAlpha; m.SetColor(prop, c); }
                    if (m.HasProperty("_Alpha")) m.SetFloat("_Alpha", m.GetFloat("_Alpha") * GhostAlpha);
                    s_ghostMats[i] = m;
                }
                s_ghostRenderer.sharedMaterials = s_ghostMats;
            }
            s_ghostSpill = GhostLine("spillway", lineMat);
            s_ghostPool = GhostLine("pool", lineMat);
        }

        private static LineRenderer GhostLine(string name, Material lineMat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(s_ghost.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = true;
            lr.widthMultiplier = 0.08f;
            lr.material = lineMat;
            lr.textureMode = LineTextureMode.Tile;
            lr.positionCount = GhostRingPoints;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return lr;
        }

        private static void Ring(LineRenderer lr, Vector3 centre, float r, Color col)
        {
            lr.startColor = col; lr.endColor = col;
            for (int i = 0; i < GhostRingPoints; i++)
            {
                float a = i * Mathf.PI * 2f / GhostRingPoints;
                lr.SetPosition(i, centre + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            }
        }

        // 0.2.1071 (Fire: "get the waterfalls laying in correctly"): the river's own fall. The river already KNOWS its lip (the
        // upstream dab's water level) and its landing (this dab's water level), so it places the fall there instead of re-detecting
        // an edge from the camera view (Place's lip walk refused in Fire's log: "no drop within 12 m past the cursor along the view",
        // "the drop at the lip is 1.6 m" - measured on ground the river had already cut).
        internal static string PlaceAt(Vector3 lip, Vector3 outward, float radius, float landY)
        {
            var flat = new Vector2(outward.x, outward.z);
            if (flat.sqrMagnitude < 0.01f) return Refused("no direction down the river at the lip");
            flat.Normalize();
            if (lip.y - landY < 1f) return Refused(F("the river's drop at ({0:0.0},{1:0.0}) is {2:0.0} m", lip.x, lip.z, lip.y - landY));
            float notchR = Mathf.Min(radius, NotchMaxRadius);
            Vector3 notch = new Vector3(lip.x, lip.y - NotchDepth * 0.5f, lip.z);
            string cut = (WaterShapingTools.StudioHeightmap && !WaterShapingTools.OnVoxel(notch)) ? "notch skipped (Studio heightmap carve, 0.2.1256)" : VoxelWorld.ApplyBrush(notch, notchR, NotchDepth, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
            var key = ChunkOf(notch);
            if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null || !chunk.HasBrushEdits)
                return Refused(F("the river fall's notch at ({0:0.0},{1:0.0},{2:0.0}) did not take ({3})", notch.x, notch.y, notch.z, cut));
            var fall = new Fall { Lip = new Vector3(lip.x, lip.y - NotchDepth, lip.z), Out = flat, Width = Mathf.Max(2f, radius * 2f), LandY = landY, OntoGround = false };
            ListFor(key).Add(fall);
            VoxelWorld.RequestSave(key);
            Rebuild(key);
            return F("[FiresWater] Vfall placed (river): lip ({0:0.0},{1:0.0},{2:0.0}) out ({3:0.00},{4:0.00}) width {5:0.0} m, drop {6:0.0} m into the pool at y {7:0.0}; chunk {8} holds {9} fall(s)",
                fall.Lip.x, fall.Lip.y, fall.Lip.z, flat.x, flat.y, fall.Width, fall.Lip.y - landY, landY, key, s_byChunk[key].Count);
        }

        // 0.2.1348: the river's fall from what landed: lip = the upper water's real surface at its real edge (a river's curtain starts AT the water, not NotchDepth under it as a ground lip does),
        // land = the lower water's real surface, the notch in the real ground under the lip. It keeps the chunk record, so undo and the Vfall remove treat it as any fall.
        internal static string PlaceRiverFinal(Vector3 lip, float ground, Vector3 outward, float width, float landY)
        {
            var flat = new Vector2(outward.x, outward.z);
            if (flat.sqrMagnitude < 0.01f) return Refused("no direction down the river at the lip");
            flat.Normalize();
            float notchR = Mathf.Min(width * 0.5f, NotchMaxRadius);
            Vector3 notch = new Vector3(lip.x, ground - NotchDepth * 0.5f, lip.z);
            string cut = (WaterShapingTools.StudioHeightmap && !WaterShapingTools.OnVoxel(notch)) ? "notch skipped (Studio heightmap carve, 0.2.1256)" : VoxelWorld.ApplyBrush(notch, notchR, NotchDepth, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
            var key = ChunkOf(notch);
            if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null || !chunk.HasBrushEdits)
                return Refused(F("the river fall's notch at ({0:0.0},{1:0.0},{2:0.0}) did not take ({3})", notch.x, notch.y, notch.z, cut));
            var fall = new Fall { Lip = lip, Out = flat, Width = Mathf.Max(2f, width), LandY = landY, OntoGround = false };
            ListFor(key).Add(fall);
            VoxelWorld.RequestSave(key);
            Rebuild(key);
            return F("[FiresWater] Vfall placed (river, last): lip ({0:0.0},{1:0.0},{2:0.0}) width {3:0.0} m, drop {4:0.0} m into the water at y {5:0.0}; chunk {6} holds {7} fall(s)", lip.x, lip.y, lip.z, fall.Width, lip.y - landY, landY, key, s_byChunk[key].Count);
        }

        // 0.2.1348 fat_river_check: the falls near a spot, their stored lip and land against the water there now.
        internal static void DescribeNear(Vector3 at, float radius, System.Text.StringBuilder sb)
        {
            foreach (var kv in s_byChunk)
                foreach (var f in kv.Value)
                {
                    float dx = f.Lip.x - at.x, dz = f.Lip.z - at.z;
                    if (dx * dx + dz * dz > radius * radius) continue;
                    bool up = PlacedWaterField.TrySurface(f.Lip.x - f.Out.x, f.Lip.z - f.Out.y, f.Lip.y - 6f, f.Lip.y + 6f, PlacedWaterField.Consumer.Sheet, out float us, out _);
                    bool dn = PlacedWaterField.TrySurface(f.Lip.x + f.Out.x * 2f, f.Lip.z + f.Out.y * 2f, f.LandY - 6f, f.LandY + 6f, PlacedWaterField.Consumer.Sheet, out float ds, out _);
                    sb.Append(F("\n  fall lip ({0:0.0},{1:0.00},{2:0.0}) vs water behind it {3}; land {4:0.00} vs water past it {5}; width {6:0.0}", f.Lip.x, f.Lip.y, f.Lip.z, up ? us.ToString("0.00") : "none", f.LandY, dn ? ds.ToString("0.00") : "none", f.Width));
                }
        }

        // Vfall Remove (vanilla middle mouse; 0.2.1072; was LeftAlt, which our key rules forbid): the falls whose lip is within the brush.
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

        // FatUndo (one numpad 7/9 history): a chunk's falls before a stroke, and laying them back
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
                if (!FiresWaterfalls.AppendCurtain(s_columns, out3, FiresWaterfalls.EdgeLipOut, 0f, seed, FiresWaterfalls.TerrainProfile, approach: false, placed: true))
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
