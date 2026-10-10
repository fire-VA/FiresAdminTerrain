using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // The height middle ground for a swapped chunk (Fire, 2026-09-27 14:4x and 20:5x; Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md,
    // "Height middle ground"). A zone raised with other land moves up or down AS A WHOLE, once, when it rises:
    //  - its land's edge median meets its open neighbours' edge median, sampled just either side of each edge it shares
    //    with an open zone; no blending, the cliffs stay;
    //  - never so low that ground dry in its source goes under the water line;
    //  - a zone that continues a neighbour's land (a run of seeds of one biome) takes that neighbour's lift, so the
    //    run stays one piece;
    //  - no open neighbour, no lift.
    // The lift is stored with the zone's source (ZoneModeRegistry, section 4) and never changes after (a neighbour
    // opening later does not move it). Swapped zones saved before this carry none.
    //
    // A lifted zone keeps its water (Fire, 15:00): its source's sea-covered ground shows FAT's own water at the lifted
    // sea level (ZoneModeGate.TryLiftedSea, read by GenWaterField), and a narrow bank rises just over that water along
    // the zone's edges so it sits up there like a raised pond (Fire, 20:5x); an edge it shares with a neighbour that
    // continues its land at the same lift has no bank, so the water runs on. Plants and creatures judge their altitude
    // against the lifted sea, so the chunk grows and spawns as its source did (the ground probe keeps the land's biome
    // there, FiresWorldGenBase.IsGenWetAt), creature AI takes it for water, its ocean depth is its source's, and a
    // location that floats on the sea (snap-to-water) floats on the lifted sea.
    internal static class ZoneModeLift
    {
        private const float EdgeStep = 2f;
        private const float EdgeInset = 1f;
        private const float FloorStep = 4f;
        private const float LiftRounding = 10f;
        private const float BankFreeboard = 0.4f;
        private const float BankCrest = 1.5f;
        private const float BankGrade = 1f;
        private const float BankReach = 24f;

        private static readonly List<float> s_mine = new List<float>();
        private static readonly List<float> s_theirs = new List<float>();

        // Server, as a seed raises `zone` with the land of `source`, slid by `centring` when a location seed centres its
        // location. `how` says why, for the log and the answer.
        internal static float Choose(Vector2s zone, Vector2s source, in ZoneShift centring, out string how)
        {
            how = "no open neighbour: its source's own height";
            var generator = WorldGenerator.instance;
            if (generator == null) return 0f;
            for (int side = 0; !centring.Slides && side < 4; side++)
            {
                var neighbour = Neighbour(zone, side);
                if (!ZoneModeRegistry.IsOpen(neighbour) || !ZoneModeRegistry.TryGetSource(neighbour, out var theirs)) continue;
                if (ZoneModeRegistry.ShiftOf(neighbour).Slides) continue;
                if (theirs.x + (zone.x - neighbour.x) != source.x || theirs.y + (zone.y - neighbour.y) != source.y) continue;
                float inherited = ZoneModeRegistry.LiftOf(neighbour);
                how = $"it continues the land of ({neighbour.x},{neighbour.y}), so it takes that zone's lift";
                return inherited;
            }

            var offset = SourceOffset(zone, source, centring);
            s_mine.Clear();
            s_theirs.Clear();
            for (int side = 0; side < 4; side++)
                if (ZoneModeRegistry.IsOpen(Neighbour(zone, side))) SampleEdge(generator, zone, offset, side);
            if (s_theirs.Count == 0) return 0f;

            float mine = Median(s_mine), theirsMedian = Median(s_theirs);
            float lift = theirsMedian - mine;
            how = $"its edge median {mine:0.0} m meets its open neighbours' {theirsMedian:0.0} m";
            if (TryLowestDryGround(generator, zone, offset, out float dry) && lift < ZoneSystem.c_WaterLevel - dry)
            {
                lift = ZoneSystem.c_WaterLevel - dry;
                how += $", held so its lowest dry ground ({dry:0.0} m) stays above the water";
            }
            return Mathf.Round(lift * LiftRounding) / LiftRounding;
        }

        // From a point of `zone` to the point of the real world it shows.
        private static Vector2 SourceOffset(Vector2s zone, Vector2s source, in ZoneShift centring)
            => new Vector2((source.x - zone.x) * ZoneSystem.c_ZoneSize + centring.Dx, (source.y - zone.y) * ZoneSystem.c_ZoneSize + centring.Dz);

        // Points 1 m either side of the edge the zone shares with that neighbour: the neighbour's ground as it stands now,
        // and the source's own ground where the zone will show it.
        private static void SampleEdge(WorldGenerator generator, Vector2s zone, Vector2 offset, int side)
        {
            var centre = ZoneSystem.GetZonePos(zone);
            float half = ZoneSystem.c_ZoneSizeHalf;
            for (float along = -half + EdgeStep * 0.5f; along < half; along += EdgeStep)
            {
                Vector2 outward = SideNormal(side);
                Vector2 edge = new Vector2(centre.x, centre.z) + outward * half + new Vector2(-outward.y, outward.x) * along;
                Vector2 outside = edge + outward * EdgeInset, inside = edge - outward * EdgeInset + offset;
                s_theirs.Add(generator.GetHeight(outside.x, outside.y));
                s_mine.Add(ZoneModeGate.RealHeight(generator, inside.x, inside.y));
            }
        }

        private static bool TryLowestDryGround(WorldGenerator generator, Vector2s zone, Vector2 offset, out float lowest)
        {
            lowest = float.MaxValue;
            var centre = ZoneSystem.GetZonePos(zone);
            float half = ZoneSystem.c_ZoneSizeHalf;
            for (float dz = -half + FloorStep * 0.5f; dz < half; dz += FloorStep)
                for (float dx = -half + FloorStep * 0.5f; dx < half; dx += FloorStep)
                {
                    float height = ZoneModeGate.RealHeight(generator, centre.x + dx + offset.x, centre.z + dz + offset.y);
                    if (height >= ZoneSystem.c_WaterLevel && height < lowest) lowest = height;
                }
            return lowest != float.MaxValue;
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) * 0.5f;
        }

        private static Vector2s Neighbour(Vector2s zone, int side)
            => new Vector2s(zone.x + (side == 0 ? 1 : side == 1 ? -1 : 0), zone.y + (side == 2 ? 1 : side == 3 ? -1 : 0));

        private static Vector2 SideNormal(int side)
            => side == 0 ? Vector2.right : side == 1 ? Vector2.left : side == 2 ? Vector2.up : Vector2.down;

        // ── The bank ────────────────────────────────────────────────────────
        // A lifted zone's ground at a point: `height` (its source's, raised), and within reach of an edge never below a
        // bank whose flat top stands BankFreeboard over the lifted sea for BankCrest from the edge, then falls inward at
        // BankGrade until it meets the ground. Ground over the bank top is untouched, so only low (wet) ground rises.
        internal static float Banked(ZoneOpenSet open, int zx, int zy, int srcX, int srcY, in ZoneShift shift, float wx, float wy, float height)
        {
            if (shift.Lift <= 0f) return height;
            float top = ZoneSystem.c_WaterLevel + shift.Lift + BankFreeboard;
            if (height >= top) return height;
            float distance = BankedEdgeDistance(open, zx, zy, srcX, srcY, shift, wx, wy);
            if (distance >= BankReach) return height;
            return Mathf.Max(height, top - Mathf.Max(0f, distance - BankCrest) * BankGrade);
        }

        // To the nearest edge that needs a bank: any edge but one shared with a neighbour showing the very next square of
        // the same land, moved the same way.
        private static float BankedEdgeDistance(ZoneOpenSet open, int zx, int zy, int srcX, int srcY, in ZoneShift shift, float wx, float wy)
        {
            float half = ZoneSystem.c_ZoneSizeHalf, cx = zx * ZoneSystem.c_ZoneSize, cz = zy * ZoneSystem.c_ZoneSize;
            float nearest = float.MaxValue;
            for (int side = 0; side < 4; side++)
            {
                float distance = side == 0 ? cx + half - wx : side == 1 ? wx - (cx - half) : side == 2 ? cz + half - wy : wy - (cz - half);
                if (distance >= nearest || distance >= BankReach) continue;
                int nx = zx + (side == 0 ? 1 : side == 1 ? -1 : 0), ny = zy + (side == 2 ? 1 : side == 3 ? -1 : 0);
                if (open.TryGetSource(nx, ny, out int theirsX, out int theirsY, out ZoneShift theirs) && theirs.SameAs(shift)
                    && theirsX - (nx - zx) == srcX && theirsY - (ny - zy) == srcY) continue;
                nearest = distance;
            }
            return nearest;
        }

        // ── The lifted ground, kept ─────────────────────────────────────────
        // ZoneModeGate.TryLiftedSea weighs a lifted zone's raised, banked ground against its sea for every water door, wet
        // test and plant or clutter veto there, and each answer sampled the source's height through the world generator.
        // That ground is kept per open set and zone on the grid the zone's terrain is built on (its heightmap: 1 m cells),
        // each point sampled the first time it is needed and read bilinearly in between, as the terrain is. A new open set
        // (any zone opening, which can take a bank away) starts afresh.
        private const float GroundStep = 1f;
        private const long GroundReportSeconds = 60;
        private const int CornersPerRead = 4;
        private static readonly int GroundPoints = Mathf.RoundToInt(ZoneOpenSet.ZoneSize / GroundStep) + 1;
        private static readonly ConditionalWeakTable<ZoneOpenSet, Dictionary<long, float[]>> s_liftedGround =
            new ConditionalWeakTable<ZoneOpenSet, Dictionary<long, float[]>>();
        private static long s_groundReads, s_groundSamples, s_nextGroundReport;

        internal static float LiftedGround(ZoneOpenSet open, int zx, int zy, int srcX, int srcY, in ZoneShift shift, float wx, float wy, WorldGenerator generator)
        {
            float half = ZoneOpenSet.ZoneSize * 0.5f, minX = zx * ZoneOpenSet.ZoneSize - half, minZ = zy * ZoneOpenSet.ZoneSize - half;
            float toSourceX = (srcX - zx) * ZoneOpenSet.ZoneSize + shift.Dx, toSourceZ = (srcY - zy) * ZoneOpenSet.ZoneSize + shift.Dz;
            var zoneShift = shift;
            var grid = GroundGrid(open, zx, zy);
            GroundCell(wx - minX, out int i, out float fu);
            GroundCell(wy - minZ, out int j, out float fv);
            CountGroundRead();
            return Mathf.Lerp(Mathf.Lerp(At(i, j), At(i + 1, j), fu), Mathf.Lerp(At(i, j + 1), At(i + 1, j + 1), fu), fv);

            float At(int pi, int pj)
            {
                int at = pj * GroundPoints + pi;
                float ground = grid[at];
                if (!float.IsNaN(ground)) return ground;
                float x = minX + pi * GroundStep, z = minZ + pj * GroundStep;
                ground = Banked(open, zx, zy, srcX, srcY, zoneShift, x, z, ZoneModeGate.RealHeight(generator, x + toSourceX, z + toSourceZ) + zoneShift.Lift);
                grid[at] = ground;
                Interlocked.Increment(ref s_groundSamples);
                return ground;
            }
        }

        private static float[] GroundGrid(ZoneOpenSet open, int zx, int zy)
        {
            var grids = s_liftedGround.GetValue(open, _ => new Dictionary<long, float[]>());
            long key = ((long)zx << 32) | (uint)zy;
            lock (grids)
            {
                if (grids.TryGetValue(key, out var grid)) return grid;
                grid = new float[GroundPoints * GroundPoints];
                for (int at = 0; at < grid.Length; at++) grid[at] = float.NaN;
                grids[key] = grid;
                return grid;
            }
        }

        // The grid cell holding a zone-local coordinate, and how far across it the coordinate lies (the far edge is the
        // last cell's end).
        private static void GroundCell(float local, out int cell, out float across)
        {
            float steps = local / GroundStep;
            cell = Mathf.Clamp(Mathf.FloorToInt(steps), 0, GroundPoints - 2);
            across = Mathf.Clamp01(steps - cell);
        }

        // Once a minute (on the next read after it): how many reads the kept ground answered and how many points it sampled.
        private static void CountGroundRead()
        {
            Interlocked.Increment(ref s_groundReads);
            long now = Stopwatch.GetTimestamp(), due = Interlocked.Read(ref s_nextGroundReport);
            if (now < due || Interlocked.CompareExchange(ref s_nextGroundReport, now + GroundReportSeconds * Stopwatch.Frequency, due) != due) return;
            long reads = Interlocked.Exchange(ref s_groundReads, 0), samples = Interlocked.Exchange(ref s_groundSamples, 0);
            if (due == 0) return;
            FiresLogger.LogInfo($"[FiresZones] lifted ground since the last report: {reads} read(s), {samples} point(s) sampled from the "
                + $"generator ({100 - samples * 100 / (CornersPerRead * reads)}% of corner reads kept).");
        }

        // ── Creatures judge altitude against the lifted sea ─────────────────
        // SpawnSystem.IsSpawnPointGood measures a spawn's altitude as its height over y30; in a lifted zone that is the
        // lifted sea, so its creatures keep their source's altitude bands. (Plants: FiresVegWaterEcology.EffectiveWaterY.)
        [System.ThreadStatic] private static float t_spawnSea;

        // Called from transpiled IL in SpawnSystem.IsSpawnPointGood.
        public static float SpawnSea() => t_spawnSea;

        [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
        private static class SpawnSystem_IsSpawnPointGood
        {
            private static void Prefix(ref Vector3 spawnPoint) => t_spawnSea = ZoneSystem.c_WaterLevel + ZoneModeGate.LiftAt(spawnPoint.x, spawnPoint.z);

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceSeaLevel(instructions, nameof(SpawnSea), "SpawnSystem.IsSpawnPointGood", "creatures in a lifted chunk judge altitude against y30");
        }

        // ── Locations that float on the sea ─────────────────────────────────
        // ZoneSystem.PlaceLocations stands a snap-to-water location (a shipwreck and the like) on y30. A raised zone's sea
        // is the lifted sea, so its snap-to-water location floats on that; a lowered zone's sea stays the y30 sea.
        [System.ThreadStatic] private static float t_placeSea;

        // Called from transpiled IL in ZoneSystem.PlaceLocations.
        public static float PlaceSea() => t_placeSea;

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        private static class ZoneSystem_PlaceLocations
        {
            private static void Prefix(Vector3 zoneCenterPos) => t_placeSea = WaterAt(zoneCenterPos);

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceSeaLevel(instructions, nameof(PlaceSea), "ZoneSystem.PlaceLocations", "a snap-to-water location in a raised chunk stands on y30");
        }

        // ── Creatures find the lifted sea ───────────────────────────────────
        // BaseAI calls ground under y30 water: a leech ashore looks for it to swim back to (MoveToWater), and a creature
        // that keeps out of water skips it when it wanders (IsValidRandomMovePoint) or flees (Flee). A raised zone's water
        // is the lifted sea; a lowered zone's stays the y30 sea.
        [System.ThreadStatic] private static float t_aiSea;

        // Called from transpiled IL in BaseAI.MoveToWater, BaseAI.IsValidRandomMovePoint and BaseAI.Flee.
        public static float AiSea() => t_aiSea;

        [HarmonyPatch(typeof(BaseAI), "MoveToWater")]
        private static class BaseAI_MoveToWater
        {
            private static void Prefix(BaseAI __instance) => t_aiSea = WaterAt(__instance.transform.position);

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceSeaLevel(instructions, nameof(AiSea), "BaseAI.MoveToWater", "a leech ashore in a raised chunk finds no water to swim back to");
        }

        [HarmonyPatch(typeof(BaseAI), "IsValidRandomMovePoint")]
        private static class BaseAI_IsValidRandomMovePoint
        {
            private static void Prefix(Vector3 point) => t_aiSea = WaterAt(point);

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceSeaLevel(instructions, nameof(AiSea), "BaseAI.IsValidRandomMovePoint", "creatures that keep out of water wander into a raised chunk's sea");
        }

        [HarmonyPatch(typeof(BaseAI), "Flee")]
        private static class BaseAI_Flee
        {
            private static void Prefix(BaseAI __instance) => t_aiSea = WaterAt(__instance.transform.position);

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceSeaLevel(instructions, nameof(AiSea), "BaseAI.Flee", "creatures that keep out of water flee into a raised chunk's sea");
        }

        private static float WaterAt(Vector3 point) => ZoneSystem.c_WaterLevel + Mathf.Max(0f, ZoneModeGate.LiftAt(point.x, point.z));

        // ── Ocean depth ─────────────────────────────────────────────────────
        // Heightmap.UpdateCornerDepths reads a zone's ocean depth off its four corner heights, down from y30: the depth
        // bands of plants (most trees 0-2 m, coastal rocks 2-30 m), clutter and creatures (Neck lakes 0-1 m), and the
        // water's waves. A raised or lowered zone's corners stand at its lift or on its bank, so it takes its source's
        // depths instead, from its source's ground at those corners.
        [HarmonyPatch(typeof(Heightmap), "UpdateCornerDepths")]
        private static class Heightmap_UpdateCornerDepths
        {
            private const string DepthProperty = "_depth";
            private static readonly AccessTools.FieldRef<Heightmap, Material> MaterialRef =
                AccessTools.FieldRefAccess<Heightmap, Material>("m_materialInstance");
            private static readonly Vector2[] CornerSides = { new Vector2(-1f, 1f), new Vector2(1f, 1f), new Vector2(1f, -1f), new Vector2(-1f, -1f) };

            private static void Postfix(Heightmap __instance)
            {
                var generator = WorldGenerator.instance;
                if (generator == null || __instance.IsDistantLod) return;
                var center = __instance.transform.position;
                var zone = ZoneSystem.GetZone(center);
                var shift = ZoneModeRegistry.ShiftOf(zone);
                if (shift.Lift == 0f || !ZoneModeRegistry.TryGetSource(zone, out var source)) return;
                float half = __instance.m_width * __instance.m_scale * 0.5f;
                float shiftX = (source.x - zone.x) * ZoneOpenSet.ZoneSize + shift.Dx, shiftZ = (source.y - zone.y) * ZoneOpenSet.ZoneSize + shift.Dz;
                var depths = __instance.GetOceanDepth();
                for (int i = 0; i < depths.Length; i++)
                {
                    float ground = ZoneModeGate.RealHeight(generator, center.x + CornerSides[i].x * half + shiftX, center.z + CornerSides[i].y * half + shiftZ);
                    depths[i] = Mathf.Max(0f, ZoneSystem.c_WaterLevel - ground);
                }
                MaterialRef(__instance).SetFloatArray(DepthProperty, depths);
            }
        }

        // Vanilla's one y30 in `method` becomes a call to `seaGetter`; any other count leaves the method as it is and says
        // what that costs.
        private static IEnumerable<CodeInstruction> ReplaceSeaLevel(IEnumerable<CodeInstruction> instructions, string seaGetter, string method, string cost)
        {
            var code = new List<CodeInstruction>(instructions);
            var hits = new List<CodeInstruction>();
            foreach (var instruction in code)
                if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float value && value == ZoneSystem.c_WaterLevel) hits.Add(instruction);
            if (hits.Count != 1)
            {
                FiresLogger.LogWarning($"[FiresZones] {method} held {hits.Count} sea-level constant(s), expected 1: {cost}.");
                return code;
            }
            hits[0].opcode = OpCodes.Call;
            hits[0].operand = AccessTools.Method(typeof(ZoneModeLift), seaGetter);
            return code;
        }
    }
}
