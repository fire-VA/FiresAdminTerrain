using System;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // THE TERRAIN GATE. A closed zone reads as deep sea from WorldGenerator's samplers, for every
    // consumer that builds or draws terrain: zone heightmaps and the distant LOD (on
    // HeightmapBuilder's worker), the minimap, the environment and the player's biome. It is a
    // VIEW, never a mutation: opening a zone only stops gating it.
    //
    // Planning passes read the real world. They run inside a per-thread planning scope
    // (pregeneration, the alt-biome grid, location placement, zone population), because their
    // answers drive seeded random sequences and one gated sample would reshuffle the world plan.
    //
    // A heightmap build pins ONE view for all of its samples, so a zone opening mid-build can never
    // tear a mesh; the epoch it pinned is remembered and a build older than the current view is
    // dropped before anything consumes it.
    internal static class ZoneModeGate
    {
        private const float WorldHeightScale = 200f;
        private const float EdgeFadeStart = 10000f;
        private const float EdgeCliffStart = 10490f;
        private const float EdgeFadeEnd = 10500f;
        private const float EdgeFadeBase = -0.2f;
        private const float EdgeCliffBase = -2f;
        private const float BeyondEdgeHeight = -400f;

        private static readonly Color ClearMask = new Color(0f, 0f, 0f, 0f);

        internal sealed class View
        {
            internal readonly ZoneOpenSet Open;
            internal readonly float CoastWidth;
            internal readonly float SeaFloorY;
            internal readonly int Epoch;

            internal View(ZoneOpenSet open, float coastWidth, float seaFloorY, int epoch)
            {
                Open = open;
                CoastWidth = coastWidth;
                SeaFloorY = seaFloorY;
                Epoch = epoch;
            }
        }

        internal sealed class SampleTally
        {
            internal long ClosedBiome;
            internal long ClosedHeight;
            internal long CoastHeight;
            internal long ClosedSector;
            internal long PlanningClosedReads;

            internal long Gated => ClosedBiome + ClosedHeight + CoastHeight + ClosedSector;

            internal void Clear()
            {
                ClosedBiome = ClosedHeight = CoastHeight = ClosedSector = PlanningClosedReads = 0;
            }
        }

        private sealed class EpochTag
        {
            internal readonly int Epoch;
            internal EpochTag(int epoch) { Epoch = epoch; }
        }

        private static View s_view;
        private static int s_epoch;
        private static BiomeSector s_oceanSector;
        private static readonly ConditionalWeakTable<HeightmapBuilder.HMBuildData, EpochTag> s_buildEpochs =
            new ConditionalWeakTable<HeightmapBuilder.HMBuildData, EpochTag>();

        private static long s_workerBuilds;
        private static long s_workerBuildsTouchingClosed;
        private static long s_workerClosedHeight;
        private static long s_workerCoastHeight;
        private static long s_workerClosedBiome;
        private static long s_workerClosedSector;

        [ThreadStatic] private static int t_planningDepth;
        [ThreadStatic] private static int t_bypassDepth;
        [ThreadStatic] private static View t_pinnedView;
        [ThreadStatic] private static bool t_pinned;
        [ThreadStatic] private static SampleTally t_tally;
        [ThreadStatic] private static bool t_buildZoneSet;
        [ThreadStatic] private static int t_buildZoneX;
        [ThreadStatic] private static int t_buildZoneY;
        [ThreadStatic] private static bool t_populationSet;
        [ThreadStatic] private static int t_populationX;
        [ThreadStatic] private static int t_populationY;

        internal static bool Active => Volatile.Read(ref s_view) != null;
        internal static View Current => Volatile.Read(ref s_view);
        internal static int CurrentEpoch => Volatile.Read(ref s_epoch);
        internal static bool InPlanningScope => t_planningDepth > 0;

        // True inside the gate's own translated call for a swapped zone's source point, where a world-position rule (the
        // Chaos Hammer's biome paint) must not apply a second time at the source's coordinates.
        internal static bool InSourceLookup => t_bypassDepth > 0;

        private static SampleTally Tally => t_tally ?? (t_tally = new SampleTally());

        internal static void SetView(ZoneOpenSet open, float coastWidth, float seaFloorY)
        {
            if (s_oceanSector == null) s_oceanSector = new BiomeSector(null, Heightmap.Biome.Ocean);
            int epoch = Interlocked.Increment(ref s_epoch);
            Volatile.Write(ref s_view, open == null ? null : new View(open, coastWidth, seaFloorY, epoch));
        }

        internal static void ClearView()
        {
            Interlocked.Increment(ref s_epoch);
            Volatile.Write(ref s_view, null);
        }

        internal static void EnterPlanning() => t_planningDepth++;

        internal static void ExitPlanning()
        {
            if (t_planningDepth > 0) t_planningDepth--;
        }

        internal static int SuspendPlanning()
        {
            int depth = t_planningDepth;
            t_planningDepth = 0;
            return depth;
        }

        internal static void ResumePlanning(int depth) => t_planningDepth = depth;

        // The view a sample is gated against, or null when it must read the real world.
        private static View SampleView(float wx, float wy)
        {
            if (t_bypassDepth > 0) return null;
            var view = t_pinned ? t_pinnedView : Volatile.Read(ref s_view);
            if (view == null || view.Open.IsRealAt(wx, wy)) return null;
            if (t_planningDepth > 0)
            {
                Tally.PlanningClosedReads++;
                return null;
            }
            return view;
        }

        internal static bool IsClosedForTerrain(float wx, float wy)
        {
            var view = Volatile.Read(ref s_view);
            return view != null && !view.Open.IsRealAt(wx, wy);
        }

        // ── Swapped zones: an open zone opened with a seed of another biome shows the land of its source zone.
        // Its samples read the real world there, with the gate bypassed. A zone's own heightmap build reads its
        // source for every sample, edges included, so the zone is an exact copy of real land; everywhere else a
        // point goes by the zone it lies in. Planning passes still read the world's own plan.
        // While the server populates a swapped zone (a planning scope), the zone's own points still read its source,
        // so its plants, spawns and locations follow the land it shows.
        internal static void EnterPopulation(Vector2s zone)
        {
            t_populationSet = true;
            t_populationX = zone.x;
            t_populationY = zone.y;
        }

        internal static void ExitPopulation() => t_populationSet = false;

        private static View LiveView()
        {
            if (t_bypassDepth > 0 || (t_planningDepth > 0 && !t_populationSet)) return null;
            return t_pinned ? t_pinnedView : Volatile.Read(ref s_view);
        }

        // True when this thread's samples describe the terrain peers will stand on: a gate view exists and no source read
        // of the gate itself is in progress. Planning passes count too, since what they plan (the hydrology, a zone's
        // population) stands on that terrain; only the gate's own real-world reads for coasts and swapped sources do not.
        internal static bool ReadsLiveTerrain
            => t_bypassDepth == 0 && (t_pinned ? t_pinnedView : Volatile.Read(ref s_view)) != null;

        // A point of a swapped zone: the zone, the zone its land comes from, and how that land is moved beyond the whole
        // zone (ZoneShift: its lift, and the slide that centres a location seed's location).
        private struct Swap
        {
            internal int Zx, Zy, SrcX, SrcY;
            internal ZoneShift Shift;
            internal float Lift => Shift.Lift;
        }

        private static bool TrySourcePoint(View view, float wx, float wy, out float sx, out float sy)
            => TrySourcePoint(view, wx, wy, out sx, out sy, out _);

        private static bool TrySourcePoint(View view, float wx, float wy, out float sx, out float sy, out Swap swap)
        {
            sx = wx;
            sy = wy;
            swap = default;
            if (view == null || view.Open.SourceCount == 0) return false;
            int zx, zy;
            if (t_pinned && t_buildZoneSet) { zx = t_buildZoneX; zy = t_buildZoneY; }
            else ZoneOpenSet.ZoneOf(wx, wy, out zx, out zy);
            if (t_planningDepth > 0 && (zx != t_populationX || zy != t_populationY)) return false;
            if (!view.Open.TryGetSource(zx, zy, out int srcX, out int srcY, out ZoneShift shift)) return false;
            sx = wx + (srcX - zx) * ZoneOpenSet.ZoneSize + shift.Dx;
            sy = wy + (srcY - zy) * ZoneOpenSet.ZoneSize + shift.Dz;
            swap = new Swap { Zx = zx, Zy = zy, SrcX = srcX, SrcY = srcY, Shift = shift };
            return true;
        }

        // A swapped zone's ground at a point: its source's `height`, raised by the zone's lift and held up by its bank.
        private static float Raise(View view, in Swap swap, float wx, float wy, float height)
            => swap.Lift == 0f ? height : ZoneModeLift.Banked(view.Open, swap.Zx, swap.Zy, swap.SrcX, swap.SrcY, swap.Shift, wx, wy, height + swap.Lift);

        // For FiresAdminTerrain's own terrain data (generated water, river and lake carves): where to read it for
        // a point of a swapped zone.
        internal static bool TranslateToSource(ref float wx, ref float wy) => TranslateToSource(ref wx, ref wy, out _);

        // As above, with how far the zone's land was raised: absolute levels read at the source rise by it.
        internal static bool TranslateToSource(ref float wx, ref float wy, out float lift)
        {
            lift = 0f;
            if (t_bypassDepth > 0 || (t_planningDepth > 0 && !t_populationSet)) return false;
            var view = Volatile.Read(ref s_view);
            if (view == null || view.Open.SourceCount == 0) return false;
            ZoneOpenSet.ZoneOf(wx, wy, out int zx, out int zy);
            if (t_planningDepth > 0 && (zx != t_populationX || zy != t_populationY)) return false;
            if (!view.Open.TryGetSource(zx, zy, out int srcX, out int srcY, out ZoneShift shift)) return false;
            wx += (srcX - zx) * ZoneOpenSet.ZoneSize + shift.Dx;
            wy += (srcY - zy) * ZoneOpenSet.ZoneSize + shift.Dz;
            lift = shift.Lift;
            return true;
        }

        // ── Lifted chunks (ZoneModeLift) ────────────────────────────────────
        // The real world's height at a point, the gate bypassed: a swapped zone's source as it stands in the world.
        internal static float RealHeight(WorldGenerator generator, float wx, float wy)
        {
            t_bypassDepth++;
            try { return generator.GetHeight(wx, wy); }
            finally { t_bypassDepth--; }
        }

        // How far the land of the zone holding a point was raised; 0 outside a lifted zone. For altitude bands (plants,
        // creatures), which follow the zone's sea wherever they are judged.
        internal static float LiftAt(float wx, float wy)
        {
            var view = t_pinned ? t_pinnedView : Volatile.Read(ref s_view);
            if (view == null || view.Open.LiftCount == 0) return 0f;
            ZoneOpenSet.ZoneOf(wx, wy, out int zx, out int zy);
            return view.Open.LiftOf(zx, zy);
        }

        // A height the Fires generator's sampler read at a swapped zone's source (after SamplerSeesClosed or
        // TryClosedHeight moved the point there), raised as the zone's own land. (wx, wy) is the point BEFORE the move.
        internal static float Lifted(float wx, float wy, float height)
        {
            var live = LiveView();
            return live != null && live.Open.LiftCount > 0 && TrySourcePoint(live, wx, wy, out _, out _, out var swap)
                ? Raise(live, swap, wx, wy, height)
                : height;
        }

        // A water level the Fires generator's sampler read at a swapped zone's source (after SamplerSeesClosed moved the
        // point there), raised with the zone, and the zone's lifted sea wherever that stands higher. (wx, wy) is the point
        // BEFORE the move; `found` is what the sampler answered.
        internal static bool LiftWater(float wx, float wy, bool found, ref float level, ref float depth)
        {
            var live = LiveView();
            if (live == null || live.Open.LiftCount == 0 || !TrySourcePoint(live, wx, wy, out _, out _, out var swap) || swap.Lift == 0f)
                return found;
            if (found) level += swap.Lift;
            if (!TryLiftedSea(wx, wy, out float seaLevel, out float seaFloor) || (found && level >= seaLevel)) return found;
            level = seaLevel;
            depth = seaLevel - seaFloor;
            return true;
        }

        // True where a lifted zone's sea stands, for the Fires generator's wet, corridor and class doors, which otherwise
        // answer from the source, where that water is open sea.
        internal static bool IsLiftedSeaAt(float wx, float wy) => TryLiftedSea(wx, wy, out _, out _);

        // The water a lifted zone keeps (Fire, 15:00): where its source's ground lies under the sea, the zone shows water
        // at the lifted sea level over its raised ground. Banks along its edges stand clear of it.
        internal static bool TryLiftedSea(float wx, float wy, out float level, out float floor)
        {
            level = floor = 0f;
            var live = LiveView();
            var generator = WorldGenerator.instance;
            if (live == null || generator == null || live.Open.LiftCount == 0) return false;
            if (!TrySourcePoint(live, wx, wy, out _, out _, out var swap) || swap.Lift <= 0f) return false;
            float ground = ZoneModeLift.LiftedGround(live.Open, swap.Zx, swap.Zy, swap.SrcX, swap.SrcY, swap.Shift, wx, wy, generator);
            level = ZoneSystem.c_WaterLevel + swap.Lift;
            if (ground >= level) return false;
            floor = ground;
            return true;
        }

        // ── The Fires generator's samplers (FiresWorldGenBase) ──────────────
        // On a Fires world everything that draws or plays on the land reads the generator's grid through its own
        // samplers: far terrain, far trees and water, the map's rivers, full-voxel ground, generated water. Each public
        // sampler asks here first, so zone mode holds there exactly as it does for GetBiomeHeight: a swapped zone's
        // point moves to its source (true is never returned for it), and a closed point is sea, which the sampler
        // answers itself. Planning, population and the gate's own real reads pass through untouched. The samplers'
        // Real twins skip this, for generation that must not depend on which zones are open.
        internal static bool SamplerSeesClosed(ref float wx, ref float wy)
        {
            if (!t_pinned && Volatile.Read(ref s_view) == null) return false;
            var live = LiveView();
            if (live != null && TrySourcePoint(live, wx, wy, out float sx, out float sy))
            {
                wx = sx;
                wy = sy;
                return false;
            }
            return SampleView(wx, wy) != null;
        }

        // As SamplerSeesClosed, for a height sampler: a closed point gets the sea floor, eased up to the real ground
        // across the coast band exactly as TryGateBiomeHeight eases it. `real` is only read inside that band.
        internal static bool TryClosedHeight(ref float wx, ref float wy, Func<float, float, float> real, out float height)
        {
            height = 0f;
            if (!t_pinned && Volatile.Read(ref s_view) == null) return false;
            var live = LiveView();
            if (live != null && TrySourcePoint(live, wx, wy, out float sx, out float sy))
            {
                wx = sx;
                wy = sy;
                return false;
            }
            var view = SampleView(wx, wy);
            if (view == null) return false;
            if (!InCoastBand(view, wx, wy, out float floor, out float fall))
            {
                height = floor;
                return true;
            }
            float ground = real(wx, wy);
            height = DUtils.Lerp(ground, Mathf.Min(ground, floor), fall);
            return true;
        }

        // A closed point's sea floor, and how far across the coast band it lies (0 at the open edge, 1 at its far side).
        private static bool InCoastBand(View view, float wx, float wy, out float floor, out float fall)
        {
            floor = SeaFloorAt(view.SeaFloorY, wx, wy);
            float distance = view.Open.DistanceToOpen(wx, wy, view.CoastWidth);
            fall = distance >= view.CoastWidth ? 1f : DUtils.SmoothStep(0f, 1f, distance / view.CoastWidth);
            return distance < view.CoastWidth;
        }

        internal static bool TryGateBiome(WorldGenerator generator, float wx, float wy, float oceanLevel, bool waterAlwaysOcean,
            ref Heightmap.Biome result)
        {
            var live = LiveView();
            if (live != null && TrySourcePoint(live, wx, wy, out float sx, out float sy))
            {
                t_bypassDepth++;
                try { result = generator.GetBiome(sx, sy, oceanLevel, waterAlwaysOcean); }
                finally { t_bypassDepth--; }
                return true;
            }
            if (SampleView(wx, wy) == null) return false;
            result = Heightmap.Biome.Ocean;
            Tally.ClosedBiome++;
            return true;
        }

        internal static bool TryGateSector(WorldGenerator generator, float wx, float wy, bool clamp, ref BiomeSector result)
        {
            var live = LiveView();
            if (live != null && TrySourcePoint(live, wx, wy, out float sx, out float sy))
            {
                t_bypassDepth++;
                try { result = generator.GetBiomeSector(sx, sy, clamp); }
                finally { t_bypassDepth--; }
                return true;
            }
            if (SampleView(wx, wy) == null) return false;
            result = s_oceanSector;
            Tally.ClosedSector++;
            return true;
        }

        // Inside the coast band the real height is sampled (with the gate bypassed, so any other
        // terrain mod's patches still shape it) and eased down to the sea floor; the band never
        // rises above the real ground, so opening the zone later can only raise it.
        // A swapped zone keeps the biome it is asked for: callers got it from the gate already translated, and a
        // zone build asks once per source corner biome and blends the four, exactly as the source zone was built.
        internal static bool TryGateBiomeHeight(WorldGenerator generator, Heightmap.Biome biome, float wx, float wy,
            bool preGeneration, bool riverPreDN, ref Color mask, ref float result)
        {
            var live = LiveView();
            if (live != null && TrySourcePoint(live, wx, wy, out float sx, out float sy, out var swap))
            {
                t_bypassDepth++;
                try { result = generator.GetBiomeHeight(biome, sx, sy, out mask, preGeneration, riverPreDN); }
                finally { t_bypassDepth--; }
                result = Raise(live, swap, wx, wy, result);
                return true;
            }

            var view = SampleView(wx, wy);
            if (view == null) return false;

            if (!InCoastBand(view, wx, wy, out float floor, out float fall))
            {
                result = floor;
                mask = ClearMask;
                Tally.ClosedHeight++;
                return true;
            }

            float real;
            Color realMask;
            t_bypassDepth++;
            try { real = generator.GetBiomeHeight(biome, wx, wy, out realMask, preGeneration, riverPreDN); }
            finally { t_bypassDepth--; }

            result = DUtils.Lerp(real, Mathf.Min(real, floor), fall);
            mask = Color.Lerp(realMask, ClearMask, fall);
            Tally.CoastHeight++;
            return true;
        }

        // The configured floor inside the world; toward the rim it deepens exactly the way vanilla's
        // base height does, so the closed sea runs into the world edge like the real one.
        internal static float SeaFloorAt(float seaFloorY, float wx, float wy)
        {
            float radius = Mathf.Sqrt(wx * wx + wy * wy);
            if (radius <= EdgeFadeStart) return seaFloorY;
            if (radius > EdgeFadeEnd) return BeyondEdgeHeight;
            float floorBase = seaFloorY / WorldHeightScale;
            float faded = DUtils.Lerp(floorBase, EdgeFadeBase, DUtils.LerpStep(EdgeFadeStart, EdgeFadeEnd, radius));
            if (radius > EdgeCliffStart)
                faded = DUtils.Lerp(faded, EdgeCliffBase, DUtils.LerpStep(EdgeCliffStart, EdgeFadeEnd, radius));
            return Mathf.Min(seaFloorY, faded * WorldHeightScale);
        }

        // ── Heightmap builds (HeightmapBuilder worker) ──────────────────────
        internal static void PinForBuild(HeightmapBuilder.HMBuildData data)
        {
            t_pinnedView = Volatile.Read(ref s_view);
            t_pinned = true;
            t_buildZoneSet = data != null && !data.m_distantLod
                && Mathf.Abs(data.m_width * data.m_scale - ZoneOpenSet.ZoneSize) < 0.01f;
            if (t_buildZoneSet) ZoneOpenSet.ZoneOf(data.m_center.x, data.m_center.z, out t_buildZoneX, out t_buildZoneY);
            Tally.Clear();
        }

        internal static void UnpinAfterBuild(HeightmapBuilder.HMBuildData data)
        {
            var tally = Tally;
            var pinned = t_pinnedView;
            t_pinned = false;
            t_pinnedView = null;
            t_buildZoneSet = false;
            if (data != null)
            {
                s_buildEpochs.Remove(data);
                s_buildEpochs.Add(data, new EpochTag(pinned?.Epoch ?? CurrentEpoch));
            }
            if (pinned == null) return;
            Interlocked.Increment(ref s_workerBuilds);
            if (tally.Gated > 0) Interlocked.Increment(ref s_workerBuildsTouchingClosed);
            Interlocked.Add(ref s_workerClosedHeight, tally.ClosedHeight);
            Interlocked.Add(ref s_workerCoastHeight, tally.CoastHeight);
            Interlocked.Add(ref s_workerClosedBiome, tally.ClosedBiome);
            Interlocked.Add(ref s_workerClosedSector, tally.ClosedSector);
            tally.Clear();
        }

        internal static bool IsStaleBuild(HeightmapBuilder.HMBuildData data)
            => data != null && s_buildEpochs.TryGetValue(data, out var tag) && tag.Epoch != CurrentEpoch;

        // ── Diagnostics ─────────────────────────────────────────────────────
        internal struct WorkerTotals
        {
            internal long Builds, BuildsTouchingClosed, ClosedHeight, CoastHeight, ClosedBiome, ClosedSector;

            internal bool Any => Builds > 0;
        }

        internal static WorkerTotals TakeWorkerTotals()
        {
            return new WorkerTotals
            {
                Builds = Interlocked.Exchange(ref s_workerBuilds, 0),
                BuildsTouchingClosed = Interlocked.Exchange(ref s_workerBuildsTouchingClosed, 0),
                ClosedHeight = Interlocked.Exchange(ref s_workerClosedHeight, 0),
                CoastHeight = Interlocked.Exchange(ref s_workerCoastHeight, 0),
                ClosedBiome = Interlocked.Exchange(ref s_workerClosedBiome, 0),
                ClosedSector = Interlocked.Exchange(ref s_workerClosedSector, 0),
            };
        }

        // Main thread only: the caller's own tally.
        internal static SampleTally TakeMainThreadTally()
        {
            var taken = new SampleTally();
            var tally = Tally;
            taken.ClosedBiome = tally.ClosedBiome;
            taken.ClosedHeight = tally.ClosedHeight;
            taken.CoastHeight = tally.CoastHeight;
            taken.ClosedSector = tally.ClosedSector;
            taken.PlanningClosedReads = tally.PlanningClosedReads;
            tally.Clear();
            return taken;
        }
    }
}
