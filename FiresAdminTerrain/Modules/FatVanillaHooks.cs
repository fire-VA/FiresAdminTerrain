using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Lifecycle;
using VerdantsAscent.Terrain.Paint;
using VerdantsAscent.Terrain.Performance;
using VerdantsAscent.Terrain.Persistence;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Terrain.Zones;
using VerdantsAscent.Water.Core;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Terrain
{
    // FAT's one Harmony hook for each vanilla method that more than one of its modules needs. Each hook calls the modules
    // in the order they ran when they were separate patches. A module that needs its own priority against other mods
    // keeps its own patch method here, declared in run order. Modules expose plain methods and never patch these targets.
    internal static class FatVanillaHooks
    {
        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        private static class Terminal_InitTerminal
        {
            private static void Postfix()
            {
                WaterBrushCommands.Register();
                WaterDebugCommands.Register();
                WaterDumpCommand.Register();
                ZoneModeCommands.Register();
                VoxelBrushCommands.OnTerminalInit();
                FiresSkyIslands.RegisterCommands();
                FiresWorldClone.RegisterCommands();
                FiresBakeDrift.Register();
                TileDebugCommands.Register();
                TileBrushCommands.Register();
                TileDeleteOps.Register();
                BcImport.BcImportCommands.Register();
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        private static class ZNet_Awake
        {
            private static void Postfix()
            {
                ZoneModeOpening.EnsureRpcs();
                ZoneModeCommands.EnsureRpcs();
                ZoneModeChunkKeys.EnsureRpcs();
                ZoneModeChunkTools.EnsureRpcs();
                FiresSkyIslands.EnsureEditRpcs();
                RegionScrubberRPC.ResetInitialized();
                RegionScrubberRPC.Register();
                TileBrushRPC.ResetInitialized();
                TileBrushRPC.Register();
            }

            // After every registration, this mod's and ConfigSync's, so the counts it reports are real.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void ReportRpcs() => global::VerdantsAscent.FiresAdminTerrain.EmitRpcSummary();
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start
        {
            private static void Postfix(ZNet __instance)
            {
                try { global::VerdantsAscent.Terrain.Voxel.FiresWaterGuards.OnWorldLoaded(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresVoxel] freezing the world's water guards failed: {ex}"); }
                try { ZoneModeRegistry.MarkWorldLoaded(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresZones] the loaded world's zone checks failed: {ex}"); }
                CustomWaterPatches.OnZNetStart(__instance);
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            private static void AutoWater(ZNet __instance) => AutoWaterPatches.OnZNetStart(__instance);
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private static class ZNet_Shutdown
        {
            private static void Prefix()
            {
                CustomWaterPatches.SaveOnShutdown();
                ZoneStreamTiming.OnShutdown();
                try { DistantPaintCache.Save(); } catch { }
            }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static class ZNetScene_Awake
        {
            private static void Postfix()
            {
                TileAnchorPrefab.Ensure();
                ZoneModeChunkMachines.OnZNetSceneAwake();
                TreeSaplingPrefabs.OnZNetSceneAwake();
                TreeSeedDrops.OnZNetSceneAwake();
                Utilities.ArmorStandEffectFix.OnZNetSceneAwake();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void EnsureAnchors()
            {
                WaterAnchorPrefab.Ensure();
                FiresLiquidWaterPrefab.Ensure();
                LandIceAnchorPrefab.Ensure();
                LandSnowAnchorPrefab.Ensure();
                VoxelAnchorPrefab.Ensure();
            }
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
        private static class ObjectDB_Awake
        {
            private static void Postfix(ObjectDB __instance)
            {
                VoxelToolPieces.OnObjectDBAwake(__instance);
                ZoneModeChunkMachines.OnObjectDBAwake(__instance);
                ZoneModeChunkTools.OnObjectDBAwake(__instance);
                VAFatToolIcons.Register();
            }
        }

        [HarmonyPatch(typeof(Game), "Logout")]
        private static class Game_Logout
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.High)]
            private static void AutoWater() => AutoWaterPatches.OnLogout();

            private static void Postfix() => CustomWaterPatches.OnLogout();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Player_OnSpawned
        {
            private static void Postfix(Player __instance)
            {
                CustomWaterPatches.OnPlayerSpawned(__instance);
                VanillaTerrainOpScrubber.ResetSpawnTime();
                ZoneModeRavenHints.OnPlayerSpawned(__instance);
            }
        }

        [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
        private static class PieceTable_UpdateAvailable
        {
            private static void Prefix() => TileAnchorPrefab.ScrubBeforeUpdateAvailable();

            // High: ahead of FAP's LowerThanNormal visibility pass, which keeps the final word when it is installed.
            [HarmonyPriority(Priority.High)]
            private static void Postfix(PieceTable __instance)
            {
                VoxelToolPieces.ApplyToolVisibility(__instance);
                ZoneModeChunkTools.ApplyVisibility(__instance);
            }
        }

        [HarmonyPatch(typeof(Floating), nameof(Floating.GetWaterLevel))]
        private static class Floating_GetWaterLevel
        {
            private static void Postfix(Vector3 p, ref float __result)
            {
                CustomWaterPatches.RaiseToPourSurface(p, ref __result);
                WaterBoatSupport.RaiseToCustomWater(p, ref __result);
            }
        }

        [HarmonyPatch(typeof(LiquidVolume), "Awake")]
        private static class LiquidVolume_Awake
        {
            private static void Postfix(LiquidVolume __instance)
            {
                FiresLiquidWaterPrefab.OnLiquidVolumeAwake(__instance);
                FiresLiquidRiverController.ReattachRiver(__instance);
            }
        }

        [HarmonyPatch(typeof(LiquidVolume), "UpdateHeights")]
        private static class LiquidVolume_UpdateHeights
        {
            private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

            // The clamp guard judges the heights the voxel surface pass has just written, so the order is fixed.
            private static void Postfix(LiquidVolume __instance, long __state)
            {
                LiquidVoxelSurface.Apply(__instance);
                FiresFrameAudit.CountUpdateHeights(__state);
                VoxelGenLiquid.ClampGuard(__instance);
            }
        }

        [HarmonyPatch(typeof(Minimap), "TryLoadMinimapTextureData")]
        private static class Minimap_TryLoadMinimapTextureData
        {
            private static void Prefix(Minimap __instance, out Version.World __state) => FiresMinimapCache.BeforeLoad(__instance, out __state);

            private static void Postfix(bool __result)
            {
                if (__result) FiresMinimapRivers.MapReady("loaded from PNG cache");
            }

            // A finalizer, so the borrowed world version is restored even when the load throws.
            private static Exception Finalizer(Exception __exception, Version.World __state, bool __result)
                => FiresMinimapCache.AfterLoad(__exception, __state, __result);
        }

        [HarmonyPatch(typeof(Minimap), "GenerateWorldMap")]
        private static class Minimap_GenerateWorldMap
        {
            private static void Prefix(out Stopwatch __state) => __state = ZoneModeMinimapReport.Start();

            private static void Postfix(Stopwatch __state)
            {
                ZoneModeMinimapReport.Finish(__state);
                FiresMinimapRivers.MapReady("generated fresh");
            }
        }

        [HarmonyPatch(typeof(TerrainOp), "Awake")]
        private static class TerrainOp_Awake
        {
            // First: a refused op is destroyed before anything else acts on it. Later hooks check
            // ZoneModeRefusals.WasCancelled and TileTerrainOpBlocker.WasRoutedToTile, because every
            // prefix runs even after one has returned false.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static bool RefuseClosedZones(TerrainOp __instance) => ZoneModeRefusals.AllowTerrainOp(__instance);

            [HarmonyPrefix]
            private static bool BlockOverTiles(TerrainOp __instance) => TileTerrainOpBlocker.AllowTerrainOp(__instance);

            // Last: an op nothing above claimed, on a sky island, would reshape the ground below it.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.Low)]
            private static bool RefuseOnSkyIslands(TerrainOp __instance) => FiresSkyIslandTerrainGuard.AllowTerrainOp(__instance);

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            private static void ScrubPaintOverlay(TerrainOp __instance) => VanillaTerrainOpScrubber.OnTerrainOpAwake(__instance);
        }

        [HarmonyPatch(typeof(TerrainComp), "DoOperation")]
        private static class TerrainComp_DoOperation
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            private static void ScrubPaintOverlay(Vector3 pos, TerrainOp.Settings modifier) => VanillaTerrainOpScrubber.OnDoOperation(pos, modifier);

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void EnvBoxClutterMask(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier)
                => global::VerdantsAscent.Utilities.EnvironmentBoxPatches.OnTerrainOperation(__instance, pos, modifier);
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.Initialize))]
        private static class WorldGenerator_Initialize
        {
            // First, so the zone registry and the gate view exist before FiresWorldGen's prefix and before vanilla's
            // pregeneration, which then runs inside a planning scope.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void ZoneModeInit(World world) => ZoneModeWorldInit.Begin(world);

            [HarmonyPrefix]
            private static void FiresWorldGenInit(World world) => Voxel.FiresWorldGenInit.Run(world);

            // Before FiresWorldGen so a migrated BetterContinents world is known when its generator initialises.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.High)]
            private static void BcImportInit(World world) => BcImport.BcMigratedWorld.OnWorldInitialize(world);

            // After FiresWorldGen's prefix has cleared the island set for the new world.
            private static void Postfix(World world) => ZoneModeWorldInit.AfterInitialize(world);

            private static Exception Finalizer(Exception __exception) => ZoneModeWorldInit.End(__exception);
        }

        [HarmonyPatch(typeof(ZoneSystem), "Start")]
        private static class ZoneSystem_Start
        {
            private static void Postfix(ZoneSystem __instance)
            {
                CustomWaterPatches.OnZoneSystemStart(__instance);
                ZoneModeRegistry.SubscribeSeeding();
                FarQuilt.OnZoneSystemStart();
                VoxelZoneStreamer.OnZoneSystemStart();
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
        private static class Heightmap_Regenerate
        {
            private static void Postfix(Heightmap __instance)
            {
                VoxelZoneStreamer.OnHeightmapRegenerate(__instance);
                DistantPaintCache.OnHeightmapRegenerate(__instance);
            }
        }

        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        private static class Heightmap_RebuildRenderMesh
        {
            // First: the env-box corner biomes are set before anything builds from them.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void EnvBoxCorners(Heightmap __instance)
                => global::VerdantsAscent.Utilities.EnvironmentBoxPatches.BeforeRebuildRenderMesh(__instance);

            private static bool Prefix(Heightmap __instance) => VoxelOnlyRenderMesh.Allow(__instance);

            // The env-box colours at Last, then the persistent biome paint just below it, which has the final word on
            // painted vertices. Both run even when the voxel gate skipped the build.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void EnvBoxColours(Heightmap __instance)
                => global::VerdantsAscent.Utilities.EnvironmentBoxPatches.AfterRebuildRenderMesh(__instance);

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last - 5)]
            private static void BiomePaint(Heightmap __instance)
                => global::VerdantsAscent.Terrain.Biome.BiomePaintPersistence.BiomePaintPatches.ApplyPaintColors(__instance);
        }

        [HarmonyPatch(typeof(ClutterSystem), "GenerateVegPatch")]
        private static class ClutterSystem_GenerateVegPatch
        {
            // All three run, as separate prefixes did: the water-level move and the spread takeover still happen when a
            // dedicated server skips the build.
            private static bool Prefix(ClutterSystem __instance, Vector2Int patchID, float size, ref object __result)
            {
                bool runOriginal = global::VerdantsAscent.Utilities.EnvironmentBoxPatches.ClutterRunsHere();
                ClutterSystem_GenerateVegPatch_WaterEcology.Apply(__instance, patchID);
                if (!global::VerdantsAscent.Performance.ClutterSpread.OnGenerateVegPatch(__instance, patchID, size, ref __result))
                    runOriginal = false;
                return runOriginal;
            }

            private static Exception Finalizer(ClutterSystem __instance, Exception __exception, Vector2Int patchID)
            {
                var exception = global::VerdantsAscent.Utilities.EnvironmentBoxPatches.SuppressClutterNre(__exception, patchID);
                ClutterSystem_GenerateVegPatch_WaterEcology.Restore(__instance);
                return exception;
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetLava))]
        private static class Heightmap_GetLava
        {
            // Both answers are taken, in this order, as when they were separate First prefixes: the lava mask, written
            // second, stands whenever both apply.
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Vector3 worldPos, ref float __result)
            {
                bool runOriginal = VoxelOnlyLava.Shield(ref __result);
                if (!FiresLavaQueries.GetLava(worldPos, ref __result)) runOriginal = false;
                return runOriginal;
            }
        }

        [HarmonyPatch(typeof(Character), "UnderWorldCheck")]
        private static class Character_UnderWorldCheck
        {
            // Both checks always run, as separate prefixes did: the voxel one also resets the character's check timer.
            private static bool Prefix(Character __instance, float dt)
            {
                bool voxel = VoxelUnderWorld.Allow(__instance, dt);
                bool tiles = TileCharacterPhysicsPatches.AllowUnderWorldCheck(__instance);
                return voxel && tiles;
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight), typeof(Vector3))]
        private static class ZoneSystem_GetGroundHeight
        {
            // Voxel ground truth first, then the tile lift for InfinityHammer's ruler, which answers only inside a
            // ruler snap.
            private static bool Prefix(Vector3 p, ref float __result)
            {
                bool runOriginal = VoxelGroundHeight.Single(p, ref __result);
                if (!TileHammerCompat.GroundHeightSingle(p, ref __result)) runOriginal = false;
                return runOriginal;
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight), new[] { typeof(Vector3), typeof(float) },
            new[] { ArgumentType.Normal, ArgumentType.Out })]
        private static class ZoneSystem_GetGroundHeightOut
        {
            private static bool Prefix(Vector3 p, ref float height, ref bool __result)
            {
                bool runOriginal = VoxelGroundHeight.WithOut(p, ref height, ref __result);
                if (!TileHammerCompat.GroundHeightOut(p, ref height, ref __result)) runOriginal = false;
                return runOriginal;
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        private static class ZoneSystem_SpawnZone
        {
            // The zone-mode gate opens its planning scope first and closes it in the finalizer, so it brackets every
            // other hook; the profiler's clock starts after it.
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Vector2s zoneID, ref ZoneSystem.SpawnMode mode, ref GameObject root, ref bool __result,
                out bool __state)
            {
                bool runOriginal = ZoneMode_ZoneSystem_SpawnZone.Before(zoneID, ref mode, ref root, ref __result, out __state);
                FiresVegProfiler.BeforeZone();
                return runOriginal;
            }

            private static void Postfix(Vector2s zoneID, GameObject root, bool __result, bool __state)
            {
                ZoneMode_ZoneSystem_SpawnZone.After(__result, __state);
                ZoneStreamTiming.OnZoneSpawned(zoneID, root, __result);
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            private static void AutoWater() => AutoWaterPatches.OnZoneSpawned();

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Profile() => FiresVegProfiler.AfterZone();

            private static Exception Finalizer(Exception __exception, bool __state)
                => ZoneMode_ZoneSystem_SpawnZone.Finally(__exception, __state);
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation")]
        private static class ZoneSystem_PlaceVegetation
        {
            // The profiler's clock and its Inside flag, which the ground-data probe reads, bracket every other hook.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void StartClock() => FiresVegProfiler.BeforeVegetation();

            // clearAreas is vanilla's private List<ZoneSystem.ClearArea>, bound as object.
            private static void Prefix(ZoneSystem __instance, Vector3 zoneCenterPos, object clearAreas)
            {
                ZoneSystem_PlaceVegetation_Density.Apply(__instance);
                ZoneSystem_PlaceVegetation_WaterEcology.Apply(__instance);
                ZoneModeStartCave.KeepMouthClear(zoneCenterPos, clearAreas);
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => ZoneSystem_PlaceVegetation_WaterEcology.Transpile(instructions);

            // The floe cull scans the spawned list before the skyland props join it.
            private static void Postfix(Vector2s zoneID, Vector3 zoneCenterPos, ZoneSystem.SpawnMode mode, List<GameObject> spawnedObjects)
            {
                ZoneSystem_PlaceVegetation_IceFloes.Cull(mode, spawnedObjects);
                FiresSkyIslandProps.OnPlaceVegetation(zoneID, zoneCenterPos, mode, spawnedObjects);
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void StopClock() => FiresVegProfiler.AfterVegetation();

            private static void Finalizer(ZoneSystem __instance)
            {
                ZoneSystem_PlaceVegetation_Density.Restore(__instance);
                ZoneSystem_PlaceVegetation_WaterEcology.Restore(__instance);
            }
        }

        [HarmonyPatch]
        private static class ZoneSystem_SpawnLocation
        {
            private static MethodBase TargetMethod()
                => AccessTools.Method(typeof(ZoneSystem), "SpawnLocation", new[]
                {
                    typeof(ZoneSystem.ZoneLocation), typeof(int), typeof(Vector3), typeof(Quaternion),
                    typeof(ZoneSystem.SpawnMode), typeof(List<GameObject>), typeof(bool),
                });

            // First, so a location's pad levels to the corrected height rather than the generated one.
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZoneSystem.ZoneLocation location, ref Vector3 pos) => FiresLocationHeight.Correct(location, ref pos);

            private static void Postfix(ZoneSystem.ZoneLocation location, Vector3 pos, ZoneSystem.SpawnMode mode)
                => LocationSpawnPads.OnSpawned(location, pos, mode);
        }
    }
}
