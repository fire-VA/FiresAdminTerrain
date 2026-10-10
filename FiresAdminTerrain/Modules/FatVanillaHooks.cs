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
                ZoneModeCloseable.Register();
                VoxelBrushCommands.OnTerminalInit();
                VoxelBrushDrag.RegisterPressCommand();   // va_voxel_press: the test bot's virtual LMB on a V-tool
                VoxelBrushDrag.RegisterLevelCommand();   // va_voxel_level: a flat pad for a drill (FDT obstacle_field)
                VoxelBrushDrag.RegisterRampCommand();    // va_voxel_ramp: a tilted plane for a test (0.2.790)
                SiteScanCommand.Register();              // va_site_scan: rank base spots near spawn (read-only)
                HomesteadScanCommand.Register();         // va_homestead_scan: homestead test-base sites (read-only)
                FatGroundProbe.RegisterCommand();            // va_ground_probe: what the SERVER stands on at a spot (0.2.689)
                FatGroundProbe.RegisterSurfaceProbe();       // fat_surface_probe: every voxel surface crossing in a column (0.2.793)
                FiresCaveBiome.RegisterProbe();              // fat_biome_probe: every biome reader at a point (0.2.797)
                VerdantsAscent.Performance.ClutterGroundProbe.RegisterProbe();   // fat_clutter_probe: grass by layer (0.2.798)
                FatUndergroundLocations.RegisterDump();      // fat_location_dump: underground placements by type (0.2.799)
                FatSinkFalls.RegisterDump();                 // fat_sinkfall_dump: waterfall sinkholes near the camera (0.2.802)
                FiresFarWater.RegisterCommand();             // fat_farwater on|off: far-tile A/B (0.2.817)
                FatWorldSize.RegisterProbe();                // fat_worldsize_probe: the world's size and a ray to its edge (0.2.721)
                FatWaterLine.Register();                     // fat_water_line: generated vs stored water across a river (0.2.847)
                global::VerdantsAscent.Water.Core.FishCheckCommand.Register();   // va_fish_check / va_fish_spawn: inland fish proof (admin)
                FiresSkyIslands.RegisterCommands();
                FiresWorldClone.RegisterCommands();
                FiresWorldCloudArchive.RegisterCommands();
                global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.RegisterSelfTest();   // fat_hammer_selftest: every Chaos Hammer tool selects itself (0.2.769)
                AltBiomePointsParallel.RegisterCommand();
                FiresBakeDrift.Register();
                TileDeleteOps.Register();
                TileAnchorHeal.RegisterCommand();   // fat_tile_purge: the retired 2D tile layer's cleanup
                BcImport.BcImportCommands.Register();
                FatWorldgenTest.Register();
                Testing.AdminToolsTest.Register();
                Testing.FishWaterTest.Register();
                Testing.RiverCarveTest.Register();
                Testing.WaterLifeTest.Register();
                Testing.NoClipTest.Register();
                Testing.CavesTest.Register();       // caves_test: the cave hanger pool, nearest dressed cave, ceiling shot (0.2.732)
                Testing.LoadTest.Register();        // fat_load_test: load, log, config, save and sync checks (0.2.736)
                Testing.SkylandCheck.Register();    // skyland_check: far shape vs near voxels, floating dressing, pad slope (0.2.740)
                Testing.ClutterFloatTest.Register();   // clutterfloat_test: grass floating over edited ground (0.2.690)
                Testing.WaveMatchTest.Register();      // wavematch_test: swim/float wave vs the drawn wave (0.2.698)
                Utilities.LocationRepair.RegisterCommand();   // va_location_repair: re-point damaged location proxies (0.2.700)
            }
        }

        // The test bot's shouted replies, for every FAT test that drives it (Testing.FatTestBot).
        [HarmonyPatch(typeof(Chat), nameof(Chat.OnNewChatMessage))]
        private static class Chat_OnNewChatMessage
        {
            private static void Postfix(long senderID, string text) => Testing.FatTestBot.OnChatMessage(senderID, text);
        }

        // Two modules: the zone gate's planning scope around the whole pass (it must read the real world), and the join sync's
        // biome grid (BiomeGridSync, D12). A joined client takes the server's grid instead of sampling 4.2 M points of its own,
        // and a server prepares that grid once its own is in place.
        [HarmonyPatch(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.VerifyBiomeData))]
        private static class AltBiomeWorldData_VerifyBiomeData
        {
            private static bool Prefix(World world)
            {
                ZoneModeGate.EnterPlanning();
                return !BcImport.BiomeGridSync.TryTakeServerGrid(world);
            }

            private static void Postfix(World world) => BcImport.BiomeGridSync.OfferServerGrid(world);

            private static Exception Finalizer(Exception __exception)
            {
                ZoneModeGate.ExitPlanning();
                return __exception;
            }
        }

        // Two modules on a dedicated server's way into a world: the load guard sorts a crashed save's generations and refuses a
        // save that still does not load (vanilla would create a new world over it), and `-fireszones` makes a world created
        // from scratch a zone-mode world.
        [HarmonyPatch(typeof(World), nameof(World.GetCreateWorld))]
        private static class World_GetCreateWorld
        {
            private static bool Prefix(string name, ref World __result, out bool __state)
            {
                // Before ANY save lookup, HaveWorld included: vanilla's first one deletes a crashed save's other generations.
                try { FiresWorldLoadGuard.BeforeFirstLookup(name); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresWorldLoadGuard] sorting '{name}' before its first save lookup failed: {ex}"); }
                __state = World.HaveWorld(name);
                return FiresWorldLoadGuard.BeforeCreateWorld(name, ref __result);
            }

            private static void Postfix(World __result, bool __state) => ZoneModeNewWorld.AfterCreateWorld(__result, existed: __state);
        }

        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.GetWorldList))]
        private static class SaveSystem_GetWorldList
        {
            private static void Prefix()
            {
                try { FiresWorldLoadGuard.BeforeWorldList(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresWorldLoadGuard] sorting the world list's save folders failed: {ex}"); }
            }
        }

        // The world lock is normally released at ZNet.Shutdown; this is the fallback for a ZNet destroyed without one.
        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ZNet_OnDestroy
        {
            private static void Postfix()
            {
                FiresLogger.LogInfo("[FiresAdminTerrain] ZNet.OnDestroy postfix ran.");
                try { FiresWorldLoadGuard.ReleaseWorldLock(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresWorldLoadGuard] releasing the world lock failed: {ex}"); }
                try { ZoneModeRegistry.OnWorldTeardown(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresZones] world teardown failed: {ex}"); }
                StartTempleReport.OnWorldTeardown();
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        private static class ZNet_Awake
        {
            private static void Postfix()
            {
                ZoneModeOpening.EnsureRpcs();
                ZoneModeCommands.EnsureRpcs();
                ZoneModeCloseable.EnsureRpcs();
                Testing.CavesTest.EnsureRpcs();
                Testing.LoadTest.EnsureRpcs();
                ZoneModeChunkKeys.EnsureRpcs();
                ZoneModeChunkTools.EnsureRpcs();
                ZoneModeStartPins.EnsureRpcs();
                FiresSkyIslands.EnsureEditRpcs();
                RegionScrubberRPC.ResetInitialized();
                RegionScrubberRPC.Register();
                // TileBrushRPC is not registered: the 2D tile layer is retired (Fire, 2026-09-29); only its purge answers.
                TileAnchorHeal.EnsureRpcs();
                FatWorldgenTest.EnsureRpcs();
                Testing.AdminToolsProbe.EnsureRpcs();
                global::VerdantsAscent.Water.Core.FiresSeaWind.Init();
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
                try { VoxelCaveRecord.BindAfterLoad(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresVoxel] binding the cave record to the loaded world failed: {ex}"); }
                try { OffMapZones.ReportSaved(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresZones] checking the saved objects beyond the world edge failed: {ex}"); }
                try { TileAnchorHeal.OnWorldLoaded(); }
                catch (Exception ex) { FiresLogger.LogError($"[TileHeal] the purge check at world load failed: {ex}"); }
                CustomWaterPatches.OnZNetStart(__instance);
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            private static void AutoWater(ZNet __instance) => AutoWaterPatches.OnZNetStart(__instance);
        }

        // Two modules save with the world, at the moment vanilla captures it (it only gets here when the world may be saved):
        // the water data, and the cave record, so a crash rolls it back to the same save as the world.
        [HarmonyPatch(typeof(ZNet), "SaveWorld")]
        private static class ZNet_SaveWorld
        {
            private static void Postfix()
            {
                CustomWaterPatches.OnWorldSave();
                try { VoxelCaveRecord.SaveWithWorld(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresVoxel] saving the cave record with the world failed: {ex}"); }
            }
        }

        // Two modules turn the build ghost red after vanilla judged it: a closed zone of a one-zone world, and a piece that may
        // not stand in water placed in FAT water, which vanilla's water-layer test does not see.
        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        private static class Player_UpdatePlacementGhost
        {
            private static void Postfix(Player __instance)
            {
                ZoneModeRefusals.RedGhostOverClosedZone(__instance);
                FatWaterChecks.RedGhostInFatWater(__instance);
            }
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

            // Shutdown saves the world synchronously, so the lock can go here. R44 (0.2.626): the OnDestroy postfix
            // below logged nothing on the dedi, and every world lock there was left behind.
            private static void Postfix()
            {
                try { FiresWorldLoadGuard.ReleaseWorldLock(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresWorldLoadGuard] releasing the world lock failed: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static class ZNetScene_Awake
        {
            // Both postfixes run inside the main scene's activation frame ([generator] 2026-09-28: 270 ms of a 5.8 s frame), so
            // each logs its steps once to show which one to make cheaper.
            private static void Postfix()
            {
                var steps = new StepTimes();
                TileAnchorPrefab.Ensure();
                steps.Lap("tile anchor");
                ZoneModeChunkGenerator.OnZNetSceneAwake();
                steps.Lap("chunk generator");
                TreeSaplingPrefabs.OnZNetSceneAwake();
                steps.Lap("saplings");
                TreeSeedDrops.OnZNetSceneAwake();
                steps.Lap("seed drops");
                Utilities.ArmorStandEffectFix.OnZNetSceneAwake();
                steps.Lap("armor stands");
                FiresLogger.LogInfo($"[FiresAdminTerrain] ZNetScene.Awake steps: {steps}.");
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void EnsureAnchors()
            {
                var steps = new StepTimes();
                WaterAnchorPrefab.Ensure();
                steps.Lap("water anchor");
                FiresLiquidWaterPrefab.Ensure();
                steps.Lap("liquid water");
                LandIceAnchorPrefab.Ensure();
                steps.Lap("land ice");
                LandSnowAnchorPrefab.Ensure();
                steps.Lap("land snow");
                VoxelAnchorPrefab.Ensure();
                steps.Lap("voxel anchor");
                FiresLogger.LogInfo($"[FiresAdminTerrain] ZNetScene.Awake anchor steps: {steps}.");
            }
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
        private static class ObjectDB_Awake
        {
            private static void Postfix(ObjectDB __instance)
            {
                VoxelToolPieces.OnObjectDBAwake(__instance);
                ZoneModeChunkGenerator.OnObjectDBAwake(__instance);
                ZoneModeChunkTools.OnObjectDBAwake(__instance);
                TreeSaplingPrefabs.OnObjectDBAwake();
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
                ZoneModeRegistry.OnPlayerSpawned(__instance);   // 0.2.721: zone mode refused for this world's size, said once
            }
        }

        [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
        private static class PieceTable_UpdateAvailable
        {
            private static void Prefix(PieceTable __instance)
            {
                TileAnchorPrefab.ScrubBeforeUpdateAvailable();
                ZoneModeChunkTools.RememberSelection(__instance);
            }

            // Last: after every mod's pass (FAP's included), the Chaos Hammer's selection is the piece the player picked.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void KeepChaosHammerSelection(PieceTable __instance) => ZoneModeChunkTools.RestoreSelection(__instance);

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

        [HarmonyPatch(typeof(Floating), nameof(Floating.CustomFixedUpdate))]
        private static class Floating_CustomFixedUpdate
        {
            private static void Prefix(Floating __instance) => global::VerdantsAscent.Water.Core.GeneratedWaterFeeder.Feed(__instance);
        }

        [HarmonyPatch(typeof(Fish), nameof(Fish.CustomFixedUpdate))]
        private static class Fish_CustomFixedUpdate
        {
            private static void Prefix(Fish __instance) => global::VerdantsAscent.Water.Core.GeneratedWaterFeeder.Feed(__instance);
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
                LiquidVoxelSurface.FlattenUnderground(__instance);   // 0.2.834: an underground location's liquid lies flat (tar pits)
                FiresFrameAudit.CountUpdateHeights(__state);
                VoxelGenLiquid.ClampGuard(__instance);
            }
        }

        // Two modules: the cache's paths, staleness and version gate (FiresMinimapCache), and the upload of the arrays a worker
        // already read (FiresMinimapPreload), which skips vanilla's load with its result true.
        [HarmonyPatch(typeof(Minimap), "TryLoadMinimapTextureData")]
        private static class Minimap_TryLoadMinimapTextureData
        {
            private static bool Prefix(Minimap __instance, int worldSeed, ref bool __result, out Version.World __state)
            {
                FiresMinimapCache.BeforeLoad(__instance, out __state);
                if (!FiresMinimapPreload.TryUpload(__instance, worldSeed)) return true;
                __result = true;
                return false;
            }

            private static void Postfix(bool __result)
            {
                if (!__result) return;
                FiresMinimapRivers.MapReady("loaded from PNG cache");
                FiresMinimapIslands.MapReady();
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
                FiresMinimapIslands.MapReady();
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

            // (No tile re-route: a vanilla hoe, cultivator or Infinity Hammer op always edits the ground. The 2D tile layer it
            // used to divert into is retired - Fire, 2026-09-29.)

            // A placed location's devground, on voxel terrain, is levelled by OUR brush instead of
            // vanilla's heightmap - same circle, same target level, no sheet dragged up from the
            // generated ground through the caves. Ahead of the sky-island guard so an island's own
            // location still gets flat ground rather than a refusal.
            [HarmonyPrefix]
            private static bool RerouteLocationLevelling(TerrainOp __instance) => FiresVoxelLocationLevelGuard.AllowTerrainOp(__instance);

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

            // Ahead of FAT's world init, so the worker reading the cached minimap runs while the world initialises.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void MinimapPreload(World world) => FiresMinimapPreload.Start(world);

            [HarmonyPrefix]
            private static void FiresWorldGenInit(World world) => Voxel.FiresWorldGenInit.Run(world);

            // Before FiresWorldGen so a migrated BetterContinents world is known when its generator initialises.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.High)]
            private static void BcImportInit(World world) => BcImport.BcMigratedWorld.OnWorldInitialize(world);

            // After FiresWorldGen's prefix has cleared the island set for the new world. The join sync's loading-screen
            // line ("loading this world's terrain cache from this PC") ends here: what it named is loaded.
            private static void Postfix(World world)
            {
                ZoneModeWorldInit.AfterInitialize(world);
                BcImport.JoinSyncScreen.Clear();
            }

            private static Exception Finalizer(Exception __exception) => ZoneModeWorldInit.End(__exception);
        }

        [HarmonyPatch(typeof(WorldGenerator), "Pregenerate")]
        private static class WorldGenerator_Pregenerate
        {
            // The server records its own lakes, rivers and streams for joining clients; a client that replays the server's
            // lists (D13) skips vanilla's search.
            private static bool Prefix(WorldGenerator __instance)
            {
                BcImport.RiverSync.BeforePregenerate(__instance);
                return !BcImport.RiverSync.TryReplay(__instance);
            }

            private static void Postfix(WorldGenerator __instance) => BcImport.RiverSync.AfterPregenerate(__instance);

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            private static void AutoWater(WorldGenerator __instance) => AutoWaterPatches.OnPregenerated(__instance);
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
                ZoneSystem_PlaceVegetation_Density.WarmExemptions(__instance);
                VegetationWarmUp.OnZoneSystemStart(__instance);
                // HERE, not ZNet.Start. Measured 2026-09-26 on the dedi: ZNet.Start's postfix runs at log line 642
                // and FiresWorldGenBase's world-init profile branch at 644, so reading that branch's result from
                // ZNet.Start reads it two lines too early and silently does nothing.
                try { global::VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.SeedSkyIslandsOnVanillaGround(); }
                catch (Exception ex) { FiresLogger.LogError($"[FiresWorldGen] seeding sky islands on vanilla height failed: {ex}"); }
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
            // dedicated server skips the build. A server without graphics grows no grass at all ([perf], 2026-09-27 23:1x:
            // patches were built on the headless dedi); no patch is vanilla's own "nothing here".
            private static bool Prefix(ClutterSystem __instance, Vector2Int patchID, float size, ref object __result)
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    __result = null;
                    return false;
                }
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
            // Voxel ground truth. (The 2D tile lift for InfinityHammer's ruler is gone with the retired tile layer.)
            // Inside Infinity Hammer's snap / WEC's command Y the answer is the voxel surface near the aim (WecTerrainBridge).
            // It is asked here, first: HarmonyX runs every prefix, so a separate one was overwritten by the ground truth below.
            private static bool Prefix(Vector3 p, ref float __result)
            {
                if (WecTerrainBridge.TryHammerHeight(p, out float hammer)) { __result = hammer; return false; }
                bool runOriginal = VoxelGroundHeight.Single(p, ref __result);
                return runOriginal;
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight), new[] { typeof(Vector3), typeof(float) },
            new[] { ArgumentType.Normal, ArgumentType.Out })]
        private static class ZoneSystem_GetGroundHeightOut
        {
            private static bool Prefix(Vector3 p, ref float height, ref bool __result)
            {
                if (WecTerrainBridge.TryHammerHeight(p, out float hammer)) { height = hammer; __result = true; return false; }
                bool runOriginal = VoxelGroundHeight.WithOut(p, ref height, ref __result);
                return runOriginal;
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        private static class ZoneSystem_SpawnZone
        {
            // A dedicated server's off-map zones are refused before anything else (OffMapZones). The zone-mode gate then opens
            // its planning scope first and closes it in the finalizer, so it brackets every other hook; the profiler's clock
            // starts after it.
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Vector2s zoneID, ref ZoneSystem.SpawnMode mode, ref GameObject root, ref bool __result,
                out bool __state)
            {
                if (OffMapZones.Refuses(zoneID))
                {
                    root = null;
                    __result = false;
                    __state = false;
                    FiresVegProfiler.BeforeZone();
                    return false;
                }
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
                FatUnderWaterPlacement.BeginZone(zoneCenterPos);
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => FatUnderWaterPlacement.Transpile(ZoneSystem_PlaceVegetation_WaterEcology.Transpile(instructions));

            // The floe cull scans the spawned list before the skyland props join it.
            private static void Postfix(Vector2s zoneID, Vector3 zoneCenterPos, ZoneSystem.SpawnMode mode, List<GameObject> spawnedObjects)
            {
                ZoneSystem_PlaceVegetation_IceFloes.Cull(mode, spawnedObjects);
                FiresSkyIslandProps.OnPlaceVegetation(zoneID, zoneCenterPos, mode, spawnedObjects);
                FatUnderWaterPlacement.EndZone(zoneID);
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

        // 0.2.697: zone mode refuses teleports into closed zones (prefix); the voxel void net remembers where a body was
        // teleported to, so a fall-through right after a teleport lands on the floor nearest that target (postfix).
        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        private static class Player_TeleportTo
        {
            private static bool Prefix(Player __instance, Vector3 pos, ref bool __result)
                => ZoneModeRefusals.AllowTeleport(__instance, pos, ref __result);

            private static void Postfix(Player __instance, Vector3 pos, bool __result)
            {
                if (__result) VoxelUnderWorld.NoteTeleport(__instance, pos);
            }
        }

        // 0.2.686: vanilla's location suitability test (ZoneSystem.cs:1271, and FAT's BiomePaint.Locations) refuses a site
        // whose area overlaps FAT water (FatUnderWaterPlacement).
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetTerrainDelta))]
        private static class WorldGenerator_GetTerrainDelta
        {
            private static void Postfix(Vector3 center, float radius, ref float delta)
                => FatUnderWaterPlacement.TerrainDeltaPostfix(center, radius, ref delta);
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
            private static void Prefix(ZoneSystem.ZoneLocation location, ref Vector3 pos)
            {
                FiresLocationHeight.Correct(location, ref pos);
                VerdantsAscent.Terrain.Zones.FatLocationAccelerator.NoteSpawn(location, pos);   // 0.2.841: level sites, before any modifier wakes
            }

            private static void Postfix(ZoneSystem.ZoneLocation location, Vector3 pos, ZoneSystem.SpawnMode mode)
            {
                LocationSpawnPads.OnSpawned(location, pos, mode);
                FatUndergroundLocations.OnSpawned(location, pos, mode);   // 0.2.799: an underground location's dome, floor and shaft
            }
        }
    }
}
