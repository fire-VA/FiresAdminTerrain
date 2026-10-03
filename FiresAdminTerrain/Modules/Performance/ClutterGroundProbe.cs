using HarmonyLib;
using UnityEngine;
using FiresCore.Utilities;
using VerdantsAscent.Terrain.Persistence;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Performance
{
    // Sole owner of ClutterSystem.GetGroundInfo. Resolves the heightmap under a clutter candidate on vanilla
    // ground, voxel chunks and FAT terrain tiles, then applies the Fires biome rules; vanilla never runs.
    [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo))]
    internal static class ClutterGroundProbe
    {
        private const float ProbeStartHeight = 500f;
        private const float ProbeDistance = 1000f;
        private const float VoxelReach = 40f, VoxelBelowTolerance = 0.2f;
        // 0.2.691 shipped the measurement with this OFF (the baseline [lead] asked to see fail); ON since 0.2.692.
        private const bool CorrectToVoxel = true;
        private static bool s_voxelLogged;
        // Grass candidates moved down onto the voxel surface this session (read by clutterfloat_test).
        internal static int s_voxelCorrected;
        // 0.2.693: GetGroundInfo failures by reason (clutterfloat_test): no terrain-layer hit, or a hit on a collider that
        // resolves to no heightmap (and its name, the last one).
        internal static int s_noHit, s_unresolved;
        internal static string s_lastUnresolved = "";

        private static readonly AccessTools.FieldRef<ClutterSystem, int> s_placeRayMask =
            AccessTools.FieldRefAccess<ClutterSystem, int>("m_placeRayMask");

        [HarmonyPrefix]
        private static bool Prefix(ClutterSystem __instance, Vector3 p, out Vector3 point, out Vector3 normal,
            out Heightmap hmap, out Heightmap.Biome biome, ref bool __result)
        {
            point = p;
            normal = Vector3.up;
            hmap = null;
            biome = Heightmap.Biome.Meadows;
            __result = false;

            if (!Physics.Raycast(p + Vector3.up * ProbeStartHeight, Vector3.down, out RaycastHit hit, ProbeDistance, s_placeRayMask(__instance)))
                { s_noHit++; return false; }

            // A one-zone-at-a-time skyland puts a second surface over the ground. Grass only draws near the
            // camera, so it goes on the surface the camera is at: the ground while the camera is below the
            // island, the island's top once it is up there (ZoneModeSkylands resets grass on the switch).
            if (VerdantsAscent.Terrain.Zones.ZoneModeRegistry.ModeActive && FiresSkyIslands.TryBottomAt(p.x, p.z, out float islandBottom)
                && hit.point.y > islandBottom && CameraY() < islandBottom
                && Physics.Raycast(new Vector3(p.x, islandBottom - 0.5f, p.z), Vector3.down, out RaycastHit below,
                    ProbeDistance, s_placeRayMask(__instance)))
                hit = below;

            Heightmap ground = ResolveHeightmap(hit.collider, hit.point, out bool isTerrainTile);
            if (ground == null)
                { s_unresolved++; s_lastUnresolved = hit.collider != null ? hit.collider.name : "?"; return false; }

            // 0.2.691 (Fire 10-01, HR5b: "grass floating over the leveled terrain at the original terrain height"): in a zone
            // already swapped to voxel ground, a ray that lands on anything but a voxel collider (vanilla's heightmap
            // collider still enabled, a tile, a stray terrain-layer mesh) while the voxel surface in that column lies lower
            // is not the ground the player stands on. Grass goes on the voxel surface there instead.
            if (CorrectToVoxel && VoxelWorld.ChunkCount > 0 && hit.collider.GetComponent<VoxelChunkView>() == null
                && VoxelZoneStreamer.IsZoneSolid(ZoneSystem.GetZone(hit.point))
                && VoxelWorld.TrySampleSurfaceY(hit.point.x, hit.point.z, hit.point.y + 0.5f, hit.point.y - VoxelReach, out float voxelY, out _)
                && voxelY < hit.point.y - VoxelBelowTolerance)
            {
                hit.point = new Vector3(hit.point.x, voxelY, hit.point.z);
                s_voxelCorrected++;
                if (!s_voxelLogged && FiresLogger.DebugEnabled)
                {
                    s_voxelLogged = true;
                    FiresLogger.LogDebug($"[FiresVoxel] clutter: a grass candidate at ({hit.point.x:0}, {hit.point.z:0}) hit '{hit.collider.name}' "
                        + $"(not a voxel collider) {hit.point.y - voxelY:0.0} m above the voxel ground in a voxel zone; grass is placed on the voxel ground. "
                        + "First of this session (count in clutterfloat_test).");
                }
            }

            point = hit.point;
            normal = hit.normal;
            hmap = ground;
            biome = ResolveBiome(ground, hit.point, isTerrainTile);
            if (VerdantsAscent.Terrain.Zones.ZoneModeRegistry.ModeActive && FiresSkyIslands.TrySkylandBiomeAt(hit.point, out var skylandBiome))
                biome = skylandBiome;
            __result = true;
            return false;
        }

        private static int s_cameraFrame = -1;
        private static float s_cameraY;

        private static float CameraY()
        {
            if (s_cameraFrame == Time.frameCount) return s_cameraY;
            s_cameraFrame = Time.frameCount;
            var camera = global::Utils.GetMainCamera();
            s_cameraY = camera != null ? camera.transform.position.y : 0f;
            return s_cameraY;
        }

        private static Heightmap ResolveHeightmap(Collider collider, Vector3 hitPoint, out bool isTerrainTile)
        {
            isTerrainTile = false;
            var heightmap = collider.GetComponent<Heightmap>();
            if (heightmap != null)
                return heightmap;

            if (VoxelWorld.ChunkCount > 0 && collider.GetComponent<VoxelChunkView>() != null)
                return Heightmap.FindHeightmap(hitPoint);

            if (TerrainTile.AnyActive && collider.GetComponentInParent<TerrainTile>() != null)
            {
                isTerrainTile = true;
                return Heightmap.FindHeightmap(hitPoint);
            }

            return null;
        }

        // Heightmap.GetBiome already carries the environment-box override (EnvironmentBoxPatches.GetBiome_Postfix);
        // tiles read the world generator instead, so they apply the box themselves.
        private static Heightmap.Biome ResolveBiome(Heightmap ground, Vector3 hitPoint, bool isTerrainTile)
        {
            if (VoxelOnlyMode.Active && FiresWorldGenBase.IsWetAt(hitPoint.x, hitPoint.z))
                return Heightmap.Biome.Ocean;

            if (!isTerrainTile)
                return ground.GetBiome(hitPoint);

            var box = EnvironmentBoxController.GetEnvBoxAtPosition(hitPoint);
            if (box != null && box.ForcedBiome != Heightmap.Biome.None)
                return box.ForcedBiome;

            return WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(hitPoint) : Heightmap.Biome.Meadows;
        }
    }
}
