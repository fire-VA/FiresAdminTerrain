using System.Collections.Generic;
using FiresCore.Bridge;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // Ground turning (Fire's popup, 2026-09-27: "Turn the ground too"; the circle is exact and its edge a sharp line). A
    // painted circle makes its ground the brush's biome for everything that asks what ground a point is, never for the
    // land's shape: WorldGenerator.GetBiome is left alone, because vanilla and FAT both build heights from it
    // (GetBiomeHeight), and so do the water plan and the alt-biome map. What turns, the way FAT's environment boxes force a
    // biome:
    //  - Heightmap.GetBiome (vegetation placement, creature spawns, footsteps), FindBiomeClutter and ClutterSystem's patch
    //    biomes (grass), and Heightmap.HaveBiome (which vegetation and spawn lists a zone runs at all);
    //  - the look: the heightmap's vertex colours after vanilla builds its mesh, edited voxel ground at render time (as
    //    winter is: nothing painted is saved to chunk data or the chunk cache), and the far terrain's tiles;
    //  - the sectors (BiomePaint.Sectors): the weather, music and the biome's name, over the whole circle.
    // A heightmap's four corner sectors keep the world's ground (only a matching blend paints them), so the colours outside
    // the circle never lean toward it; inside, every vertex takes the ground's colour.
    // Water keeps its Ocean: a paint turns ground, not sea.
    internal static partial class BiomePaint
    {
        [System.ThreadStatic] private static int t_cornerScope;

        // The ground a point has been painted, when the newest circle over it turns ground. Restore gives the world's own.
        internal static bool TryGround(float wx, float wz, out Heightmap.Biome ground) => TryGround(wx, wz, out ground, out _);

        // The same, with the circle that turned it (FatSeasons blends winter snow across its rim).
        internal static bool TryGround(float wx, float wz, out Heightmap.Biome ground, out Circle turnedBy)
        {
            ground = Heightmap.Biome.None;
            turnedBy = default;
            var snapshot = s_snapshot;
            if (snapshot.Circles.Length == 0 || !snapshot.Cells.TryGetValue(CellKey(wx, wz), out var candidates)) return false;
            foreach (int index in candidates)
            {
                var circle = snapshot.Circles[index];
                if (!circle.Holds(wx, wz)) continue;
                if (circle.Brush == Brush.Restore) return false;
                if (snapshot.Sectors[index] == null) continue;
                ground = circle.Ground;
                turnedBy = circle;
                return true;
            }
            return false;
        }

        // Every ground a circle touching this square turns to (a quick yes/no for whole-zone questions).
        private static Heightmap.Biome GroundsIn(Vector3 centre, float size)
        {
            var snapshot = s_snapshot;
            if (snapshot.Circles.Length == 0) return Heightmap.Biome.None;
            float half = size * 0.5f;
            var grounds = Heightmap.Biome.None;
            for (int i = 0; i < snapshot.Circles.Length; i++)
            {
                var c = snapshot.Circles[i];
                if (c.Brush == Brush.Restore || snapshot.Sectors[i] == null) continue;
                float nx = Mathf.Clamp(c.X, centre.x - half, centre.x + half) - c.X;
                float nz = Mathf.Clamp(c.Z, centre.z - half, centre.z + half) - c.Z;
                if (nx * nx + nz * nz <= c.R * c.R) grounds |= c.Ground;
            }
            return grounds;
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetBiome), typeof(Vector3), typeof(float), typeof(bool))]
        private static class Heightmap_GetBiome
        {
            // Before the environment boxes' and skylands' Priority.Last postfix, so a forced box and a skyland still win.
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Vector3 point, ref Heightmap.Biome __result)
            {
                // 0.2.758: a biome painted on a cave floor is that floor's (VoxelLayerPaint; a skyland's answers in its own postfix).
                if (VoxelLayerPaint.TryPaintedBiomeAt(point, out var layered)) { __result = layered; return; }
                if (__result != Heightmap.Biome.Ocean && TryGround(point.x, point.z, out var ground)) __result = ground;
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.HaveBiome))]
        private static class Heightmap_HaveBiome
        {
            private static void Postfix(Heightmap __instance, Heightmap.Biome biome, ref bool __result)
            {
                if (__result || !Any) return;
                if ((GroundsIn(__instance.transform.position, __instance.m_width * __instance.m_scale) & biome) != 0) __result = true;
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.FindBiomeClutter), typeof(Vector3))]
        private static class Heightmap_FindBiomeClutter
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Vector3 point, ref Heightmap.Biome __result)
            {
                if (__result != Heightmap.Biome.None && __result != Heightmap.Biome.Ocean && TryGround(point.x, point.z, out var ground))
                    __result = ground;
            }
        }

        [HarmonyPatch(typeof(ClutterSystem), "GetPatchBiomes")]
        private static class ClutterSystem_GetPatchBiomes
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Vector3 center, float halfSize, ref Heightmap.Biome __result)
            {
                if (__result == Heightmap.Biome.None || !Any) return;
                __result |= GroundsIn(center, halfSize * 2f);
            }
        }

        // ── The look ────────────────────────────────────────────────────────
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> RenderMesh = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_renderMesh");
        private static readonly List<Color32> s_hmapColors = new List<Color32>();

        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        private static class Heightmap_RebuildRenderMesh
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Heightmap __instance) => TintHeightmap(__instance);
        }

        // Vertex order is vanilla's: row by row along z, each row along x, from the heightmap's low corner.
        private static void TintHeightmap(Heightmap hmap)
        {
            if (!Any || hmap == null) return;
            float size = hmap.m_width * hmap.m_scale;
            var centre = hmap.transform.position;
            if (GroundsIn(centre, size) == Heightmap.Biome.None) return;
            var mesh = RenderMesh(hmap);
            if (mesh == null) return;
            int row = hmap.m_width + 1;
            mesh.GetColors(s_hmapColors);
            if (s_hmapColors.Count != row * row) return;
            float x0 = centre.x - size * 0.5f, z0 = centre.z - size * 0.5f;
            bool changed = false;
            for (int z = 0; z < row; z++)
                for (int x = 0; x < row; x++)
                {
                    float wx = x0 + x * hmap.m_scale, wz = z0 + z * hmap.m_scale;
                    if (!TryGround(wx, wz, out var ground, out var circle)) continue;
                    s_hmapColors[z * row + x] = FatSeasons.WinterizePainted(Heightmap.GetBiomeColor(ground), ground, circle, wx, wz);
                    changed = true;
                }
            if (changed) mesh.SetColors(s_hmapColors);
        }


        // A season change makes Vedr repaint the ground; painted heightmaps rebuild with it so their circles follow.
        private static bool s_seasonHooked;

        private static void HookSeasons()
        {
            if (s_seasonHooked) return;
            s_seasonHooked = true;
            SeasonBridge.VisualsChanged += OnSeasonChanged;
        }

        private static void OnSeasonChanged(int season, int stage)
        {
            if (!Any || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            int poked = 0;
            foreach (var hmap in Heightmap.GetAllHeightmaps())
            {
                if (hmap == null || GroundsIn(hmap.transform.position, hmap.m_width * hmap.m_scale) == Heightmap.Biome.None) continue;
                hmap.Poke(HeightmapRebuildDelay);
                poked++;
            }
            if (poked > 0) FiresLogger.LogInfo($"[BiomePaint] season changed: {poked} painted heightmap(s) rebuild.");
        }

        // Voxel ground takes its turned colour per vertex in VoxelLayerPaint.Render (0.2.758: ground vertices only, so a cave
        // floor or a skyland over a painted circle keeps its own).

        // The far terrain's tiles (FarQuilt), one colour at a time.
        internal static Color32 Tint(Color32 color, float wx, float wz)
            => Any && TryGround(wx, wz, out var ground) ? Heightmap.GetBiomeColor(ground) : color;

        // ── After a change ──────────────────────────────────────────────────
        // Loaded heightmaps under a changed circle rebuild (colours, and their corner sectors for spawns), grass there is
        // laid again, edited voxel chunks under it remesh, and the far tiles recolour.
        private const int HeightmapRebuildDelay = 1;

        private static void RefreshGround()
        {
            if (s_changed.Count == 0 || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            int heightmaps = 0, chunks = 0;
            foreach (var hmap in Heightmap.GetAllHeightmaps())
            {
                if (hmap == null || !Touched(hmap.transform.position, hmap.m_width * hmap.m_scale)) continue;
                hmap.Poke(HeightmapRebuildDelay);
                heightmaps++;
            }
            foreach (VoxelChunk chunk in VoxelWorld.AllChunks)
            {
                if (chunk == null) continue;
                var centre = chunk.WorldOrigin + new Vector3(VoxelChunk.CELLS * 0.5f, 0f, VoxelChunk.CELLS * 0.5f);
                if (!Touched(centre, VoxelChunk.CELLS)) continue;
                VoxelMeshPipeline.EnqueueRemesh(chunk);
                VoxelWorld.MarkPaintDirty(new Vector2Int(chunk.Key.x, chunk.Key.z));   // 0.2.770: the column mask's lava alpha follows the ground
                chunks++;
            }
            if (ClutterSystem.instance != null)
                foreach (var c in s_changed) ClutterSystem.instance.ResetGrass(new Vector3(c.X, 0f, c.Z), c.R);
            FarQuilt.RecolourForSeason();
            FiresLogger.LogInfo($"[BiomePaint] ground turned: {heightmaps} heightmap(s) rebuild, {chunks} voxel chunk(s) remesh, grass laid again.");
        }
    }
}
