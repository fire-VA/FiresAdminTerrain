using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // ── REBUILDING TERRAIN AFTER THE VIEW CHANGES ───────────────────────────
    // Vanilla never re-samples the generator for a loaded heightmap: Generate reuses the heightmap's
    // cached build data while its centre, size and generator are unchanged, and the builder keeps up
    // to 16 finished builds. So a zone that opens needs, on every peer:
    //   1. fresh builds for its heightmap and the CLOSED neighbours whose shared edges it just made
    //      real (queued first and awaited, so the swap itself costs no main-thread wait);
    //   2. each heightmap's cached build data dropped, then a full Poke, which rebuilds heights,
    //      collider, corner depths and colours in one frame (stale builds are dropped by epoch);
    //   3. grass reset over the zone, the distant LOD re-requested, and the zone's minimap pixels
    //      redrawn through the same per-pixel path vanilla uses.
    internal static class ZoneModeTerrainRefresh
    {
        private const float PrewarmTimeoutSeconds = 5f;
        private const float VoxelSettleTimeoutSeconds = 10f;
        private const float GrassResetRadius = 48f;
        private const float LodReach = 1300f;
        private const int MinimapMarginPixels = 1;
        private static readonly Vector3 LodForceRebuildPoint = new Vector3(99999f, 0f, 99999f);

        private static readonly AccessTools.FieldRef<Heightmap, HeightmapBuilder.HMBuildData> BuildDataRef =
            AccessTools.FieldRefAccess<Heightmap, HeightmapBuilder.HMBuildData>("m_buildData");
        private static readonly AccessTools.FieldRef<TerrainLod, Vector3> LodLastPointRef =
            AccessTools.FieldRefAccess<TerrainLod, Vector3>("m_lastPoint");
        private static readonly AccessTools.FieldRef<Minimap, bool> MinimapDrawnRef =
            AccessTools.FieldRefAccess<Minimap, bool>("m_hasGenerated");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> ForestMaskTextureRef =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> HeightTextureRef =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");
        private static readonly Func<Minimap, Heightmap.Biome, Color> GetPixelColor =
            AccessTools.MethodDelegate<Func<Minimap, Heightmap.Biome, Color>>(AccessTools.Method(typeof(Minimap), "GetPixelColor"));
        private static readonly Func<Minimap, float, float, float, Heightmap.Biome, Color> GetMaskColor =
            AccessTools.MethodDelegate<Func<Minimap, float, float, float, Heightmap.Biome, Color>>(
                AccessTools.Method(typeof(Minimap), "GetMaskColor"));

        // Whole-world refresh, for seeding: everything built before it assumed no zone was closed.
        internal static void AfterViewChanged(string reason)
        {
            var clock = Stopwatch.StartNew();
            int zoneMaps = 0, lodMaps = 0;
            foreach (var heightmap in UnityEngine.Object.FindObjectsOfType<Heightmap>())
            {
                BuildDataRef(heightmap) = null;
                if (heightmap.IsDistantLod) { lodMaps++; continue; }
                heightmap.Poke(0, false);
                zoneMaps++;
            }
            int lods = Headless ? 0 : ForceLodRebuild();
            string map = Headless ? ", " + HeadlessNote : RedrawWorldMap();
            FiresLogger.LogInfo($"[FiresZones] terrain refreshed ({reason}): {zoneMaps} zone heightmap(s) rebuilt, "
                + $"{lodMaps} distant-LOD heightmap(s) re-requested across {lods} LOD system(s)"
                + map + $" in {clock.ElapsedMilliseconds} ms.");
        }

        // A server with no graphics device draws nothing: its minimap, grass and distant LOD are never seen, so a refresh
        // leaves them alone there ([perf], 23:1x: a 2048 x 2048 minimap upload on the dedi during every zone open).
        private const string HeadlessNote = "no minimap, grass or distant LOD on a server without graphics";
        private static bool Headless => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        // A minimap that has not drawn yet has no textures (Minimap.Start makes them), and a converted world seeds before
        // that. Its first draw sees the zones as they are then: FiresMinimapCache drops a cached map from another zone view.
        private static string RedrawWorldMap()
        {
            var minimap = Minimap.instance;
            if (minimap == null || WorldGenerator.instance == null) return "";
            if (!MinimapDrawnRef(minimap)) return ", world minimap drawn when it starts";
            minimap.ForceRegen();
            return ", world minimap redrawn";
        }

        // Targeted refresh after zones open. Calls onDone once this peer shows the new terrain.
        internal static void RebuildOpenedZones(IList<Vector2s> opened, Action onDone)
        {
            var host = FiresAdminTerrain.Instance;
            if (host == null || opened == null || opened.Count == 0)
            {
                onDone?.Invoke();
                return;
            }
            host.StartCoroutine(RebuildRoutine(new List<Vector2s>(opened), onDone));
        }

        // 0.2.753 (Fire 10-06: "closing a chunk still shows its low LOD version, unlike chunk by chunk where closed chunks are
        // nothing until opened"): the builder keeps up to 16 FINISHED builds and hands one back to any heightmap of the same
        // centre and size, so the distant LOD re-requested below got its pre-change heights back. Every zone change empties
        // that list first, so each heightmap, the distant LOD included, is built from the ground as it is now.
        private static readonly AccessTools.FieldRef<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>> ReadyBuildsRef =
            AccessTools.FieldRefAccess<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>>("m_ready");
        private static readonly AccessTools.FieldRef<HeightmapBuilder, object> BuilderLockRef =
            AccessTools.FieldRefAccess<HeightmapBuilder, object>("m_lock");

        private static int ForgetFinishedBuilds()
        {
            var builder = HeightmapBuilder.instance;
            if (builder == null) return 0;
            var ready = ReadyBuildsRef(builder);
            var gate = BuilderLockRef(builder);
            if (ready == null || gate == null) return 0;
            lock (gate)
            {
                int n = ready.Count;
                ready.Clear();
                return n;
            }
        }

        private static IEnumerator RebuildRoutine(List<Vector2s> opened, Action onDone)
        {
            var clock = Stopwatch.StartNew();
            ForgetFinishedBuilds();
            var affected = AffectedZones(opened);
            var heightmaps = new List<Heightmap>();
            foreach (var heightmap in Heightmap.GetAllHeightmaps())
            {
                if (heightmap == null || heightmap.IsDistantLod) continue;
                if (affected.Contains(ZoneSystem.GetZone(heightmap.transform.position))) heightmaps.Add(heightmap);
            }

            var builder = HeightmapBuilder.instance;
            var generator = WorldGenerator.instance;
            float deadline = Time.realtimeSinceStartup + PrewarmTimeoutSeconds;
            bool allReady = builder == null || generator == null;
            while (!allReady && Time.realtimeSinceStartup < deadline)
            {
                allReady = true;
                foreach (var heightmap in heightmaps)
                {
                    if (heightmap == null) continue;
                    if (!builder.IsTerrainReady(heightmap.transform.position, heightmap.m_width, heightmap.m_scale,
                            heightmap.IsDistantLod, generator))
                        allReady = false;
                }
                if (!allReady) yield return null;
            }
            float prewarmMs = clock.ElapsedMilliseconds;

            int rebuilt = 0;
            foreach (var heightmap in heightmaps)
            {
                if (heightmap == null) continue;
                BuildDataRef(heightmap) = null;
                heightmap.Poke(0, false);
                rebuilt++;
            }

            int lods = 0, pixels = 0;
            if (!Headless)
            {
                var clutter = ClutterSystem.instance;
                if (clutter != null)
                    foreach (var zone in opened) clutter.ResetGrass(ZoneSystem.GetZonePos(zone), GrassResetRadius);
                lods = ForceLodRebuildIfNear(opened);
                pixels = RedrawMinimapZones(affected);
            }

            // On voxel terrain the heightmap is only the data source: the zone is really on
            // screen once the voxel streamer's in-place rewrap has swapped in.
            float voxelDeadline = Time.realtimeSinceStartup + VoxelSettleTimeoutSeconds;
            bool voxelSettled = false;
            while (Time.realtimeSinceStartup < voxelDeadline)
            {
                voxelSettled = opened.TrueForAll(VoxelZoneStreamer.IsZoneTerrainSettled);
                if (voxelSettled) break;
                yield return null;
            }
            VoxelZoneStreamer.OnZonesOpened(opened);

            FiresLogger.LogInfo($"[FiresZones] terrain rebuilt for {opened.Count} opened zone(s): {rebuilt} loaded heightmap(s) "
                + $"swapped (builds ready in {prewarmMs:0} ms{(allReady ? "" : ", some timed out and were built in place")}), "
                + (Headless ? HeadlessNote + ", " : $"{(lods > 0 ? "distant LOD re-requested, " : "")}{pixels} minimap pixel(s) redrawn, ")
                + "voxel terrain "
                + (voxelSettled ? "current" : $"still rewrapping after {VoxelSettleTimeoutSeconds:0}s")
                + $", {clock.ElapsedMilliseconds} ms total.");
            onDone?.Invoke();
        }

        // An opened zone plus every CLOSED neighbour: their shared edges are real now.
        private static HashSet<Vector2s> AffectedZones(List<Vector2s> opened)
        {
            var affected = new HashSet<Vector2s>();
            foreach (var zone in opened)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var candidate = new Vector2s(zone.x + dx, zone.y + dy);
                        if ((dx == 0 && dy == 0) || !ZoneModeRegistry.IsOpen(candidate)) affected.Add(candidate);
                    }
                }
            }
            foreach (var zone in opened) affected.Add(zone);
            return affected;
        }

        private static int ForceLodRebuild()
        {
            int count = 0;
            foreach (var lod in UnityEngine.Object.FindObjectsOfType<TerrainLod>())
            {
                LodLastPointRef(lod) = LodForceRebuildPoint;
                count++;
            }
            return count;
        }

        private static int ForceLodRebuildIfNear(List<Vector2s> opened)
        {
            var camera = Utils.GetMainCamera();
            if (camera == null) return 0;
            Vector3 eye = camera.transform.position;
            bool near = false;
            foreach (var zone in opened)
            {
                Vector3 center = ZoneSystem.GetZonePos(zone);
                if (Mathf.Abs(center.x - eye.x) < LodReach && Mathf.Abs(center.z - eye.z) < LodReach) { near = true; break; }
            }
            if (!near) return 0;
            foreach (var heightmap in UnityEngine.Object.FindObjectsOfType<Heightmap>())
                if (heightmap.IsDistantLod) BuildDataRef(heightmap) = null;
            return ForceLodRebuild();
        }

        // Replays GenerateWorldMap's per-pixel body over the zones' pixel rectangles only.
        private static int RedrawMinimapZones(HashSet<Vector2s> zones)
        {
            var map = Minimap.instance;
            var generator = WorldGenerator.instance;
            if (map == null || generator == null || map.m_mapTexture == null) return 0;
            var mask = ForestMaskTextureRef(map);
            var height = HeightTextureRef(map);
            if (mask == null || height == null) return 0;

            int size = map.m_textureSize;
            float pixel = map.m_pixelSize;
            int half = size / 2;
            float halfPixel = pixel / 2f;
            int drawn = 0;
            foreach (var zone in zones)
            {
                Vector3 center = ZoneSystem.GetZonePos(zone);
                int minX = Mathf.Clamp(Mathf.FloorToInt((center.x - 32f - halfPixel) / pixel) + half - MinimapMarginPixels, 0, size - 1);
                int maxX = Mathf.Clamp(Mathf.CeilToInt((center.x + 32f - halfPixel) / pixel) + half + MinimapMarginPixels, 0, size - 1);
                int minY = Mathf.Clamp(Mathf.FloorToInt((center.z - 32f - halfPixel) / pixel) + half - MinimapMarginPixels, 0, size - 1);
                int maxY = Mathf.Clamp(Mathf.CeilToInt((center.z + 32f - halfPixel) / pixel) + half + MinimapMarginPixels, 0, size - 1);
                int width = maxX - minX + 1, rows = maxY - minY + 1;
                var biomePixels = new Color32[width * rows];
                var maskPixels = new Color32[width * rows];
                var heightPixels = new Color[width * rows];
                for (int y = 0; y < rows; y++)
                {
                    float wy = (minY + y - half) * pixel + halfPixel;
                    for (int x = 0; x < width; x++)
                    {
                        float wx = (minX + x - half) * pixel + halfPixel;
                        Heightmap.Biome biome = generator.GetBiome(wx, wy);
                        float biomeHeight = generator.GetBiomeHeight(biome, wx, wy, out Color _);
                        int index = y * width + x;
                        biomePixels[index] = GetPixelColor(map, biome);
                        maskPixels[index] = GetMaskColor(map, wx, wy, biomeHeight, biome);
                        heightPixels[index] = new Color(biomeHeight, 0f, 0f, 0f);
                    }
                }
                map.m_mapTexture.SetPixels32(minX, minY, width, rows, biomePixels);
                mask.SetPixels32(minX, minY, width, rows, maskPixels);
                height.SetPixels(minX, minY, width, rows, heightPixels);
                drawn += width * rows;
            }
            if (drawn > 0)
            {
                map.m_mapTexture.Apply();
                mask.Apply();
                height.Apply();
            }
            return drawn;
        }
    }
}
