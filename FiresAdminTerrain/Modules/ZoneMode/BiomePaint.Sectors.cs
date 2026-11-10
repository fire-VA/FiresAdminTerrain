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
        // Set while BiomePaint.Regrow re-seeds a painted circle. Vanilla PlaceVegetation places an alt-biome's own plants
        // (AltBiome.m_addVegetation, tagged AltBiomeParent) only where WorldGenerator.GetBiomeSector(point).AltBiomes holds
        // that blend, so the paint must answer those lookups during the regrow even inside a zone-mode source lookup, or the
        // circle re-grows as its plain ground biome (otherwise Dark Meadows / Lantern paint as plain Meadows / Swamp).
        [ThreadStatic] private static int t_regrow;

        // One shared sector per (ground, blend), so every circle of a brush reads as one place (its name, its weather).
        private static readonly Dictionary<string, BiomeSector> s_sectors = new Dictionary<string, BiomeSector>();

        // Main thread only: built when a list is installed, read by any thread afterwards.
        private static BiomeSector SectorFor(in Circle circle)
        {
            if (circle.Brush == Brush.Restore || (circle.Brush == Brush.Custom && circle.Ground == Heightmap.Biome.None)) return null;
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
            if (PaintCircles(wx, wz, ref result)) return;
            // No circle here: a registered override provider (a ForceBiome territory) answers with a plain sector of its biome.
            if (result == null || BiomeOverride.ProviderCount == 0 || !OverrideLookupAllowed || result.Biome == Heightmap.Biome.Ocean) return;
            if (!BiomeOverride.TryProviderSector(wx, wz, out var forced, out var biome)) return;
            if (forced != null) result = forced;                                   // the provider's own sector (keeps its alt biome)
            else if (biome != result.Biome) result = PlainSector(biome);
        }

        // True when a paint circle decided the sector (painted, or a Restore circle keeping the world's answer).
        private static bool PaintCircles(float wx, float wz, ref BiomeSector result)
        {
            var snapshot = s_snapshot;
            if (snapshot.Circles.Length == 0 || result == null || t_resolving > 0 || (ZoneModeGate.InSourceLookup && t_regrow == 0)) return false;
            Interlocked.Increment(ref s_checked);
            if (!snapshot.Cells.TryGetValue(CellKey(wx, wz), out var candidates)) return false;
            Interlocked.Increment(ref s_inCell);
            foreach (int index in candidates)
            {
                var circle = snapshot.Circles[index];
                if (!circle.Holds(wx, wz)) continue;
                if (circle.Brush == Brush.Restore) return true;
                var sector = snapshot.Sectors[index];
                if (sector == null || (t_cornerScope > 0 && result.Biome != circle.Ground)) continue;
                result = sector;
                Interlocked.Increment(ref s_painted);
                return true;
            }
            return false;
        }

        private static readonly Dictionary<Heightmap.Biome, BiomeSector> s_plainSectors = new Dictionary<Heightmap.Biome, BiomeSector>();

        // 0.2.1429 ([worldgen], Fire: "Yes fix the env box code"): the env-box corner override and the biome overlay painter take their corner sectors here too
        // (one shared plain sector per biome, never a world sector edited in place).
        internal static BiomeSector PlainSectorFor(Heightmap.Biome biome) => PlainSector(biome);

        private static BiomeSector PlainSector(Heightmap.Biome biome)
        {
            lock (s_plainSectors)
            {
                if (!s_plainSectors.TryGetValue(biome, out var sector)) s_plainSectors[biome] = sector = new BiomeSector(null, biome);
                return sector;
            }
        }

        // The one guard every override lookup shares: never while resolving the world's own answer, inside a heightmap's corner
        // scope (the zone keeps its own ground shape), or inside a zone-mode source lookup (outside a paint regrow).
        internal static bool OverrideLookupAllowed => t_resolving == 0 && t_cornerScope == 0 && !(ZoneModeGate.InSourceLookup && t_regrow == 0);

        // The newest paint circle over the point, as a biome (a Restore circle = none). Thread-safe: reads the published snapshot.
        internal static bool TryPaintedGround(float wx, float wz, out Heightmap.Biome ground)
        {
            ground = Heightmap.Biome.None;
            if (!OverrideLookupAllowed || !TryNewest(wx, wz, out var circle, out _) || circle.Brush == Brush.Restore) return false;
            ground = circle.Ground;
            return true;
        }

        // 0.2.1086 (Fire: "painted biomes should get normal goddamn biome logic" on super flat): the painted ground and sector also
        // inside a heightmap's corner scope, for FatSuperFlat only (the flat world has no ground shape of its own to keep; the height
        // stays flat, only the biome follows the paint). Not while resolving or inside a zone-mode source lookup.
        internal static bool TryPaintedSectorAnyScope(float wx, float wz, out Heightmap.Biome ground, out BiomeSector sector)
        {
            ground = Heightmap.Biome.None;
            if (t_resolving > 0 || (ZoneModeGate.InSourceLookup && t_regrow == 0)) { sector = null; return false; }
            if (!TryNewest(wx, wz, out var circle, out sector) || circle.Brush == Brush.Restore || sector == null) { sector = null; return false; }
            ground = circle.Ground;
            return true;
        }

        internal static bool InCornerScope => t_cornerScope > 0;

        // Per minute, for performance accounting: lookups that met a painted world, those whose 64 m cell holds a circle, and
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
            NoteHeightCost();
            if (checkedCount > 0)
                FiresLogger.LogDebug($"[BiomePaint] sector lookups last minute: {checkedCount:N0} with paint in the world, {inCell:N0} in a painted "
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
            private static void Postfix(Vector3 worldPos, ref BiomeSector __result)
            {
                Paint(worldPos.x, worldPos.z, ref __result);
                OnLayer(worldPos, ref __result);
            }
        }

        // The minimap overlay did not follow a sky island's biome while weather did:
        // the HUD's biome name and the player's current biome come from GetBiomeSector(position), which answered by XZ
        // alone, so a skyland over the sea read "Ocean". A point standing on a skyland, or on a cave floor with a biome painted
        // on it (VoxelLayerPaint), answers that layer's biome (painted or its own), as Heightmap.GetBiome already does.
        private static readonly Dictionary<Heightmap.Biome, BiomeSector> s_layerSectors = new Dictionary<Heightmap.Biome, BiomeSector>();

        private static void OnLayer(Vector3 point, ref BiomeSector result)
        {
            if (result == null || t_resolving > 0) return;
            if (!LayerBiome(point, out var biome) || biome == result.Biome) return;
            lock (s_layerSectors)
            {
                if (!s_layerSectors.TryGetValue(biome, out var sector)) s_layerSectors[biome] = sector = new BiomeSector(null, biome);
                result = sector;
            }
        }

        // Vanilla's Player.UpdateBiome compares the sector with WorldGenerator.GetBiome(position) every second and logs a
        // "GetBiome error" when they differ: the position overload answers the same layer.
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(Vector3))]
        private static class WorldGenerator_GetBiome_Position
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Vector3 point, ref Heightmap.Biome __result)
            {
                if (t_resolving > 0) return;
                if (LayerBiome(point, out var biome)) __result = biome;
            }
        }

        // The layer a point stands on answers its biome. Skylands and painted floors as before (zone-mode worlds); a
        // point in a cave answers the cave's own biome (FiresCaveBiome: painted, else its region's) on any voxel world.
        private static bool LayerBiome(Vector3 point, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (ZoneModeRegistry.ModeActive && VoxelTerrainLayerBiome(point, out biome)) return true;
            t_resolving++;
            try { return VerdantsAscent.Terrain.Voxel.FiresCaveBiome.TryCaveBiome(point, out biome); }
            finally { t_resolving--; }
        }

        private static bool VoxelTerrainLayerBiome(Vector3 point, out Heightmap.Biome biome)
            => VerdantsAscent.Terrain.Voxel.FiresSkyIslands.TrySkylandBiomeAt(point, out biome)
               || VerdantsAscent.Terrain.Voxel.VoxelLayerPaint.TryPaintedBiomeAt(point, out biome);

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
