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
        internal static int s_noHit, s_unresolved, s_skylandHeld;   // s_skylandHeld: 0.2.763, skyland grass waiting for its zone's swap
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

            // 0.2.798 (Fire 10-06, layered clutter): grass is drawn on the layer the CAMERA is on. In a cave that is the cave's
            // floor: the ray starts just under the camera's cave roof at the candidate's own column, so the surface above is
            // never hit; a column whose start lies in rock has no cave there and grows nothing.
            if (CaveCamera(out float caveRoof))
                return CaveGround(__instance, p, caveRoof, out point, out normal, out hmap, out biome, ref __result);

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

            // 0.2.763 (Fire 10-06: "veg and clutter grasses shouldn't be drawn until after the terrain reshapes when we load the
            // chunk"): grass on a skyland waits for its zone's swap. Its voxel chunks are drawn from that frame on (their
            // colliders can exist earlier), and the swap lays the zone's grass again (FatClutterRegrow.Zone).
            if (VerdantsAscent.Terrain.Zones.ZoneModeRegistry.ModeActive && FiresSkyIslands.TryBottomAt(hit.point.x, hit.point.z, out float onBottom)
                && hit.point.y > onBottom && !VoxelZoneStreamer.IsZoneSolid(ZoneSystem.GetZone(hit.point)))
                { s_skylandHeld++; return false; }

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

        // ── 0.2.798: grass on the cave floor the camera stands in ─────────────────────────────────────────────────────
        private const float CaveRoofGap = 0.4f, CaveReach = 30f, CaveStartStep = 1f, CaveStartMax = 12f;
        internal static int s_caveGrass, s_caveRock;   // cave candidates placed / in rock (fat_clutter_probe)
        private static int s_caveFrame = -1;
        private static bool s_caveCamera, s_wasCave;
        internal static bool CameraInCave => CaveCamera(out _);
        private static float s_caveRoof;
        private const float CaveEnterHold = 0.5f, CaveLeaveHold = 1.5f, CaveDwell = 3f;
        private static float s_caveRawSince, s_caveSwitched = -100f;

        private static bool CaveCamera(out float roof)
        {
            if (s_caveFrame != Time.frameCount)
            {
                s_caveFrame = Time.frameCount;
                s_caveCamera = false;
                var camera = global::Utils.GetMainCamera();
                bool was = s_wasCave;
                if (camera != null && VoxelWorld.ChunkCount > 0)
                {
                    Vector3 c = camera.transform.position;
                    // 0.2.823 (Fire 10-07, out-river tunnel near (-393,41,-3461): "the cave clutter flashes in and out ... only as
                    // we're moving"; 67 relays, entered/left alternating on 1 m steps where the ground-top margin and the roof
                    // ray flicker by the tunnel mouth). The raw test only PROPOSES a switch: it must hold for CaveEnterHold s
                    // (in) or CaveLeaveHold s (out), and switches are at least CaveDwell s apart. Clutter and the ocean cull
                    // (FatCaveRivers via CameraInCave) share this one decision.
                    bool roofed = FiresTerrainLayers.TryRoofAbove(c, out float roofNow);
                    if (roofed) s_caveRoof = roofNow;
                    // 0.2.844 (Fire 10-07: flicker at tunnel mouths "worse than before"): the decision is CaveFactor's (a continuous
                    // cover x depth factor, in at 0.7, out at 0.3), shared with the environment and the player's cover; the 0.2.823
                    // timing holds only spaced the flips out. A cave needs a roof to lay grass under, so no roof keeps it out.
                    s_caveCamera = CaveFactor.InCave && (roofed || was);
                    // Grass patches are cached: crossing between the surface and a cave lays the camera's surroundings again.
                    if (s_caveCamera != was && ClutterSystem.instance != null)
                    {
                        ClutterSystem.instance.ResetGrass(c, ClutterSystem.instance.m_distance);
                        FiresLogger.LogDebug($"[FiresVoxel] clutter: camera {(s_caveCamera ? "entered a cave" : "left the caves")} at ({c.x:0},{c.y:0},{c.z:0}); grass relaid on its layer.");
                    }
                }
                s_wasCave = s_caveCamera;
            }
            roof = s_caveRoof;
            return s_caveCamera;
        }

        private static bool CaveGround(ClutterSystem cs, Vector3 p, float roof, out Vector3 point, out Vector3 normal,
            out Heightmap hmap, out Heightmap.Biome biome, ref bool result)
        {
            point = p; normal = Vector3.up; hmap = null; biome = Heightmap.Biome.Meadows; result = false;
            var start = new Vector3(p.x, roof - CaveRoofGap, p.z);
            if (!VoxelWorld.TryIsSolid(start.x, start.y, start.z, out bool solid)) { s_caveRock++; return false; }
            // 0.2.838 (Fire 10-07, Dolmen01's cavern: "it needs grasses"; none grew): the start is the CAMERA's roof height, and
            // a dome's roof is a cap that curves down away from its apex, so a few metres off-centre the start lay in the cap's
            // rock and nearly every candidate in the chamber was refused. Step down to the first air under it instead.
            for (float dropped = 0f; solid && dropped < CaveStartMax; dropped += CaveStartStep)
            {
                start.y -= CaveStartStep;
                if (!VoxelWorld.TryIsSolid(start.x, start.y, start.z, out solid)) { s_caveRock++; return false; }
            }
            if (solid) { s_caveRock++; return false; }
            if (!Physics.Raycast(start, Vector3.down, out RaycastHit hit, CaveReach, s_placeRayMask(cs))
                || hit.collider.GetComponent<VoxelChunkView>() == null || !FiresCaveBiome.InCave(hit.point + Vector3.up * 0.6f))
                { s_caveRock++; return false; }
            hmap = Heightmap.FindHeightmap(hit.point);
            if (hmap == null) { s_unresolved++; return false; }
            point = hit.point;
            normal = hit.normal;
            biome = FiresCaveBiome.TryCaveBiome(hit.point + Vector3.up * 0.6f, out var cave) ? cave : hmap.GetBiome(hit.point);
            s_caveGrass++;
            result = true;
            return false;
        }

        // fat_clutter_probe: the camera's layer and this session's grass candidates by outcome (read-only).
        internal static void RegisterProbe()
        {
            new Terminal.ConsoleCommand("fat_clutter_probe",
                "- the camera's layer and grass candidates this session: cave floor placed / in rock, surface voxel corrections, misses (read-only)",
                args =>
                {
                    var camera = global::Utils.GetMainCamera();
                    Vector3 c = camera != null ? camera.transform.position : Vector3.zero;
                    bool cave = CaveCamera(out float roof);
                    string biome = cave && FiresCaveBiome.TryCaveBiome(c, out var b) ? b.ToString() : "-";
                    string line = $"[ClutterProbe] camera ({c.x:0.0},{c.y:0.0},{c.z:0.0}) {(cave ? $"in a cave (roof {roof:0.0}, biome {biome})" : "on the surface")}; "
                        + $"cave grass placed {s_caveGrass}, cave candidates in rock {s_caveRock}; surface voxel corrections {s_voxelCorrected}, "
                        + $"no hit {s_noHit}, unresolved {s_unresolved}, skyland held {s_skylandHeld}";
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                });
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
