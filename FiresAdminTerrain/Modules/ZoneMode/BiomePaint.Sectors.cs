using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // The painted sectors. Every runtime reader of the alt-biome map (heightmap corners and so creature spawns, the
    // environment, music, the player's biome name, vegetation placement, level-up chances) goes through the two float
    // WorldGenerator.GetBiomeSector overloads. A postfix on each paints over the final answer (vanilla's, or zone mode's
    // gate's, which runs as a prefix: Harmony 2 runs every prefix, so a prefix here would be overwritten by it). It works
    // at the point's real world position, and never inside the gate's own translated call for a swapped zone's source.
    internal static partial class BiomePaint
    {
        [ThreadStatic] private static int t_resolving;

        // One shared sector per (ground, blend), so every circle of a brush reads as one place (its name, its weather).
        private static readonly Dictionary<string, BiomeSector> s_sectors = new Dictionary<string, BiomeSector>();

        // Main thread only: built when a list is installed, read by any thread afterwards.
        private static BiomeSector SectorFor(in Circle circle)
        {
            if (circle.Brush == Brush.Restore) return null;
            string key = KeyOf(circle.Brush, circle.Ground, circle.Blend);
            if (s_sectors.TryGetValue(key, out var sector)) return sector;
            AltBiome blend = null;
            if (circle.Brush == Brush.Blend && (blend = FindBlend(circle.Blend)) == null) return null;
            sector = new BiomeSector(null, circle.Ground);
            if (blend != null) sector.AltBiomes.Add(blend);
            s_sectors[key] = sector;
            return sector;
        }

        // The newest circle holding the point decides: Restore leaves the world's answer, and a brush turns the point to its
        // ground and blend. Inside a heightmap's corner scope (HeightmapBuilder.Build, RefreshCorners) only a brush whose
        // ground matches the world's sector paints, so a zone's corners keep the world's ground and its colours outside a
        // circle never lean toward the circle; the circle itself turns point by point (BiomePaint.Ground).
        private static void Paint(float wx, float wz, ref BiomeSector result)
        {
            var snapshot = s_snapshot;
            if (snapshot.Circles.Length == 0 || result == null || t_resolving > 0 || ZoneModeGate.InSourceLookup) return;
            Interlocked.Increment(ref s_checked);
            if (!snapshot.Cells.TryGetValue(CellKey(wx, wz), out var candidates)) return;
            Interlocked.Increment(ref s_inCell);
            foreach (int index in candidates)
            {
                var circle = snapshot.Circles[index];
                if (!circle.Holds(wx, wz)) continue;
                if (circle.Brush == Brush.Restore) return;
                var sector = snapshot.Sectors[index];
                if (sector == null || (t_cornerScope > 0 && result.Biome != circle.Ground)) continue;
                result = sector;
                Interlocked.Increment(ref s_painted);
                return;
            }
        }

        // Per minute, for [perf]'s pricing: lookups that met a painted world, those whose 64 m cell holds a circle, and
        // those answered with a painted sector. Logged only while anything is painted.
        private const float CountSeconds = 60f;
        private static long s_checked, s_inCell, s_painted;
        private static float s_countAt;

        private static void LogCounts()
        {
            if (Time.time < s_countAt) return;
            s_countAt = Time.time + CountSeconds;
            long checkedCount = Interlocked.Exchange(ref s_checked, 0L);
            long inCell = Interlocked.Exchange(ref s_inCell, 0L);
            long painted = Interlocked.Exchange(ref s_painted, 0L);
            if (checkedCount > 0)
                FiresLogger.LogInfo($"[BiomePaint] sector lookups last minute: {checkedCount:N0} with paint in the world, {inCell:N0} in a painted "
                    + $"cell, {painted:N0} answered painted ({s_snapshot.Circles.Length} circle(s)).");
        }

        // The world's own sector here, paint left out.
        private static Heightmap.Biome OriginalBiome(WorldGenerator generator, float wx, float wz)
        {
            t_resolving++;
            try { return generator.GetBiomeSector(wx, wz, false)?.Biome ?? Heightmap.Biome.None; }
            finally { t_resolving--; }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(float), typeof(float), typeof(bool))]
        private static class WorldGenerator_GetBiomeSector_Point
        {
            private static void Postfix(float wx, float wy, ref BiomeSector __result) => Paint(wx, wy, ref __result);
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(Vector3), typeof(bool))]
        private static class WorldGenerator_GetBiomeSector_Position
        {
            private static void Postfix(Vector3 worldPos, ref BiomeSector __result) => Paint(worldPos.x, worldPos.z, ref __result);
        }

        // ── Terrain that already read the old map ───────────────────────────
        // A heightmap takes its four corner sectors when it is built, and its creature spawn list and alt-biome spawns come
        // from them (vanilla's own rule: a zone's spawns follow its corners). After a change, every loaded heightmap and
        // every finished build waiting in the builder's cache that a changed circle touches takes its corners again. It is
        // done once more a moment later, for a build that was in flight on the builder's thread.
        private const float RefreshAgainSeconds = 2f;
        private static readonly List<Circle> s_changed = new List<Circle>();
        private static float s_refreshAgainAt = -1f;

        private static readonly AccessTools.FieldRef<Heightmap, BiomeSector[]> CornerBiomes =
            AccessTools.FieldRefAccess<Heightmap, BiomeSector[]>("m_cornerBiomes");
        private static readonly AccessTools.FieldRef<Heightmap, HeightmapBuilder.HMBuildData> BuildData =
            AccessTools.FieldRefAccess<Heightmap, HeightmapBuilder.HMBuildData>("m_buildData");
        private static readonly AccessTools.FieldRef<HeightmapBuilder, object> BuilderLock =
            AccessTools.FieldRefAccess<HeightmapBuilder, object>("m_lock");
        private static readonly AccessTools.FieldRef<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>> BuilderReady =
            AccessTools.FieldRefAccess<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>>("m_ready");

        private static void RefreshTerrain(Circle[] before, Circle[] after)
        {
            s_changed.Clear();
            var kept = new HashSet<uint>();
            foreach (var c in after) kept.Add(c.Id);
            foreach (var c in before) if (!kept.Contains(c.Id)) s_changed.Add(c);
            var had = new HashSet<uint>();
            foreach (var c in before) had.Add(c.Id);
            foreach (var c in after) if (!had.Contains(c.Id)) s_changed.Add(c);
            if (s_changed.Count == 0) return;
            RefreshCorners();
            RefreshGround();
            s_refreshAgainAt = Time.time + RefreshAgainSeconds;
        }

        [HarmonyPatch(typeof(HeightmapBuilder), "Build")]
        private static class HeightmapBuilder_Build
        {
            private static void Prefix() => t_cornerScope++;

            private static System.Exception Finalizer(System.Exception __exception)
            {
                t_cornerScope--;
                return __exception;
            }
        }

        private static void TickTerrainRefresh()
        {
            if (s_refreshAgainAt < 0f || Time.time < s_refreshAgainAt) return;
            s_refreshAgainAt = -1f;
            RefreshCorners();
            s_changed.Clear();
        }

        private static void RefreshCorners()
        {
            var generator = WorldGenerator.instance;
            if (generator == null || s_changed.Count == 0) return;
            int heightmaps = 0;
            foreach (var hmap in Heightmap.GetAllHeightmaps())
            {
                if (hmap == null || !Touched(hmap.transform.position, hmap.m_width * hmap.m_scale)) continue;
                var corners = CornerBiomes(hmap);
                if (corners == null || corners.Length != CornerCount) continue;
                Corners(generator, hmap.transform.position, hmap.m_width * hmap.m_scale, corners);
                var data = BuildData(hmap);
                if (data != null) data.m_cornerBiomes = (BiomeSector[])corners.Clone();
                hmap.m_cornerBiomeList.Clear();
                hmap.m_cornerAltBiomes.Clear();
                foreach (var corner in corners)
                {
                    if (corner == null) continue;
                    if (!hmap.m_cornerBiomeList.Contains(corner)) hmap.m_cornerBiomeList.Add(corner);
                    foreach (var alt in corner.AltBiomes)
                        if (!hmap.m_cornerAltBiomes.Contains(alt)) hmap.m_cornerAltBiomes.Add(alt);
                }
                heightmaps++;
            }
            int builds = RefreshReadyBuilds(generator);
            if (heightmaps + builds > 0)
                FiresLogger.LogInfo($"[BiomePaint] {heightmaps} loaded heightmap(s) and {builds} cached build(s) took their corners again.");
        }

        private static int RefreshReadyBuilds(WorldGenerator generator)
        {
            var builder = HeightmapBuilder.instance;
            if (builder == null) return 0;
            object gate = BuilderLock(builder);
            if (gate == null) return 0;
            int refreshed = 0;
            lock (gate)
            {
                foreach (var data in BuilderReady(builder))
                {
                    if (data?.m_cornerBiomes == null || data.m_cornerBiomes.Length != CornerCount) continue;
                    if (!Touched(data.m_center, data.m_width * data.m_scale)) continue;
                    Corners(generator, data.m_center, data.m_width * data.m_scale, data.m_cornerBiomes);
                    refreshed++;
                }
            }
            return refreshed;
        }

        // The builder's order: (-,-), (+,-), (-,+), (+,+) round the centre.
        private const int CornerCount = 4;

        private static void Corners(WorldGenerator generator, Vector3 centre, float size, BiomeSector[] into)
        {
            float x0 = centre.x - size * 0.5f, z0 = centre.z - size * 0.5f;
            t_cornerScope++;
            try
            {
                into[0] = generator.GetBiomeSector(x0, z0);
                into[1] = generator.GetBiomeSector(x0 + size, z0);
                into[2] = generator.GetBiomeSector(x0, z0 + size);
                into[3] = generator.GetBiomeSector(x0 + size, z0 + size);
            }
            finally { t_cornerScope--; }
        }

        private static bool Touched(Vector3 centre, float size)
        {
            float half = size * 0.5f;
            foreach (var c in s_changed)
            {
                float nx = Mathf.Clamp(c.X, centre.x - half, centre.x + half) - c.X;
                float nz = Mathf.Clamp(c.Z, centre.z - half, centre.z + half) - c.Z;
                if (nx * nx + nz * nz <= c.R * c.R) return true;
            }
            return false;
        }
    }
}
