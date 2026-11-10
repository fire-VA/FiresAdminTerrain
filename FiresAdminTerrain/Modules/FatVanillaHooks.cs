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
                VoxelBrushDrag.RegisterLevelCommand(); VerdantsAscent.Terrain.Voxel.FatSeamSuspects.Register(); VerdantsAscent.Terrain.Voxel.FatSeamSuspects.RegisterRewrap();   // fat_seam_suspect; fat_rewrap_area   // va_voxel_level: a flat pad for a drill
                VoxelBrushDrag.RegisterRampCommand();    // va_voxel_ramp: a tilted plane for a test
                SiteScanCommand.Register();              // va_site_scan: rank base spots near spawn (read-only)
                HomesteadScanCommand.Register();         // va_homestead_scan: homestead test-base sites (read-only)
                FatGroundProbe.RegisterCommand();            // va_ground_probe: what the SERVER stands on at a spot
                FatGroundProbe.RegisterSurfaceProbe();
                global::VerdantsAscent.Water.FatWaterLevelGate.RegisterCommand();
                global::VerdantsAscent.Water.Core.ToolWaterStencil.RegisterCommand();   // 0.2.1068
                global::VerdantsAscent.Terrain.Voxel.VoxelWaterSurface.RegisterSwimProbe();   // fat_swim_probe   // fat_waterlevel_ab (the zone gate A/B)       // fat_surface_probe: every voxel surface crossing in a column
                FiresCaveBiome.RegisterProbe();              // fat_biome_probe: every biome reader at a point
                VerdantsAscent.Performance.ClutterGroundProbe.RegisterProbe();   // fat_clutter_probe: grass by layer
                FatUndergroundLocations.RegisterDump();      // fat_location_dump: underground placements by type
                FatSinkFalls.RegisterDump();                 // fat_sinkfall_dump: waterfall sinkholes near the camera
                FiresFarWater.RegisterCommand();             // fat_farwater on|off: far-tile A/B
                VerdantsAscent.Terrain.Utilities.WhatRendersProbe.Register();   // fat_what_renders: who draws here
                VerdantsAscent.Terrain.Utilities.WhatRendersProbe.RegisterHide();   // fat_hide: switch suspects off
                FatWorldSize.RegisterProbe();                // fat_worldsize_probe: the world's size and a ray to its edge
                FatWaterLine.Register();                     // fat_water_line: generated vs stored water across a river
                Zones.ZoneModeChunkSpawner.RegisterAimProbe();   // fat_hammer_aim: the Chunk Hammer's target zone
                global::VerdantsAscent.Water.Core.FishCheckCommand.Register();   // va_fish_check / va_fish_spawn: inland fish proof (admin)
                FiresSkyIslands.RegisterCommands();
                FatHaloArc.RegisterCommand();   // 0.2.1027 fat_halo_arc: the halo ring spike arc
                FiresWorldClone.RegisterCommands();
                FiresWorldCloudArchive.RegisterCommands();
                global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.RegisterSelfTest();   // fat_hammer_selftest: every Chaos Hammer tool selects itself
                AltBiomePointsParallel.RegisterCommand();
                FiresBakeDrift.Register();
                TileDeleteOps.Register();
                TileAnchorHeal.RegisterCommand();   // fat_tile_purge: the retired 2D tile layer's cleanup
                BcImport.BcImportCommands.Register();
                FatWorldgenTest.Register();
#if FIRES_TESTS
                Testing.AdminToolsTest.Register();
                Testing.FishWaterTest.Register();
                Testing.RiverCarveTest.Register();
                Testing.WaterLifeTest.Register();
                Testing.NoClipTest.Register();
                Testing.CavesTest.Register();       // caves_test: the cave hanger pool, nearest dressed cave, ceiling shot
                Testing.CaveEnvTest.Register();     // cave_env_test: cave env on/off in a chunk-by-chunk world cave
                Testing.OceanTest.Register();       // ocean_test: one ocean owner, Ocean keys, Pearl effects, breath
                Testing.WaterLookTest.Register();   // water_look_test: alpine lake look before/after, floor, lake probe
                Testing.LoadTest.Register();        // fat_load_test: load, log, config, save and sync checks
                Testing.SkylandCheck.Register();    // skyland_check: far shape vs near voxels, floating dressing, pad slope
                Testing.ClutterFloatTest.Register();   // clutterfloat_test: grass floating over edited ground
                Testing.WaveMatchTest.Register();      // wavematch_test: swim/float wave vs the drawn wave
#endif
                Utilities.LocationRepair.RegisterCommand();   // va_location_repair: re-point damaged location proxies
            }
        }

#if FIRES_TESTS
        // The test bot's shouted replies, for every FAT test that drives it (Testing.FatTestBot).
        [HarmonyPatch(typeof(Chat), nameof(Chat.OnNewChatMessage))]
        private static class Chat_OnNewChatMessage
        {
            private static void Postfix(long senderID, string text) => Testing.FatTestBot.OnChatMessage(senderID, text);
        }
#endif

        // Two modules: the zone gate's planning scope around the whole pass (it must read the real world), and the join sync's
        // biome grid (BiomeGridSync). A joined client takes the server's grid instead of sampling 4.2 M points of its own,
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
                // 0.2.1059 (load/save audit M3): a throw here means "no opinion": vanilla creates or loads as it would.
                try { return FiresWorldLoadGuard.BeforeCreateWorld(name, ref __result); }
                catch (Exception ex) { FatLoadGuard.Failed("world create guard", ex); return true; }
            }

            private static void Postfix(World __result, bool __state)
                => FatLoadGuard.Step("zone-mode new world", () => ZoneModeNewWorld.AfterCreateWorld(__result, existed: __state));
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
            // 0.2.1059 (load/save audit M5): one guarded step per registration, so a throw skips only that one.
            private static void Postfix()
            {
                FatLoadGuard.Step("ZoneModeOpening RPCs", ZoneModeOpening.EnsureRpcs);
                FatLoadGuard.Step("ZoneModeCommands RPCs", ZoneModeCommands.EnsureRpcs);
                FatLoadGuard.Step("ZoneModeCloseable RPCs", ZoneModeCloseable.EnsureRpcs);
#if FIRES_TESTS
                FatLoadGuard.Step("CavesTest RPCs", Testing.CavesTest.EnsureRpcs);
#endif
                FatLoadGuard.Step("OceanKit start", VerdantsAscent.Ocean.OceanKit.DecideAndStart);   // Once - dormant while RPGClasses still runs the ocean
#if FIRES_TESTS
                FatLoadGuard.Step("LoadTest RPCs", Testing.LoadTest.EnsureRpcs);
#endif
                FatLoadGuard.Step("ZoneModeChunkKeys RPCs", ZoneModeChunkKeys.EnsureRpcs);
                FatLoadGuard.Step("ZoneModeChunkTools RPCs", ZoneModeChunkTools.EnsureRpcs);
                FatLoadGuard.Step("ZoneModeStartPins RPCs", ZoneModeStartPins.EnsureRpcs);
                FatLoadGuard.Step("sky island edit RPCs", FiresSkyIslands.EnsureEditRpcs);
                FatLoadGuard.Step("RegionScrubber RPC", () => { RegionScrubberRPC.ResetInitialized(); RegionScrubberRPC.Register(); });
                FatLoadGuard.Step("BcRegen RPCs", BcImport.BcRegenCommand.EnsureRpcs);   // 0.2.1219
                // TileBrushRPC is not registered: the 2D tile layer is retired; only its purge answers.
                FatLoadGuard.Step("TileAnchorHeal RPCs", TileAnchorHeal.EnsureRpcs);
                FatLoadGuard.Step("FatWorldgenTest RPCs", FatWorldgenTest.EnsureRpcs);
#if FIRES_TESTS
                FatLoadGuard.Step("AdminToolsProbe RPCs", Testing.AdminToolsProbe.EnsureRpcs);
#endif
                FatLoadGuard.Step("sea wind init", global::VerdantsAscent.Water.Core.FiresSeaWind.Init);
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

            // Shutdown saves the world synchronously, so the lock can go here. The OnDestroy postfix
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
            // Both postfixes run inside the main scene's activation frame (270 ms of a 5.8 s frame once), so
            // each logs its steps once to show which one to make cheaper.
            private static void Postfix()
            {
                var steps = new StepTimes();
                // 0.2.1059 (load/save audit M5): each step guarded.
                FatLoadGuard.Step("tile anchor prefab", () => TileAnchorPrefab.Ensure());
                steps.Lap("tile anchor");
                FatLoadGuard.Step("chunk generator prefab", ZoneModeChunkGenerator.OnZNetSceneAwake);
                steps.Lap("chunk generator");
                FatLoadGuard.Step("sapling prefabs", TreeSaplingPrefabs.OnZNetSceneAwake);
                steps.Lap("saplings");
                FatLoadGuard.Step("seed drops", TreeSeedDrops.OnZNetSceneAwake);
                steps.Lap("seed drops");
                FatLoadGuard.Step("armor stand fix", Utilities.ArmorStandEffectFix.OnZNetSceneAwake);
                steps.Lap("armor stands");
                FiresLogger.LogInfo($"[FiresAdminTerrain] ZNetScene.Awake steps: {steps}.");
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void EnsureAnchors()
            {
                var steps = new StepTimes();
                FatLoadGuard.Step("water anchor prefab", () => WaterAnchorPrefab.Ensure());
                steps.Lap("water anchor");
                FatLoadGuard.Step("liquid water prefab", () => FiresLiquidWaterPrefab.Ensure());
                steps.Lap("liquid water");
                FatLoadGuard.Step("land ice anchor prefab", () => LandIceAnchorPrefab.Ensure());
                steps.Lap("land ice");
                FatLoadGuard.Step("land snow anchor prefab", () => LandSnowAnchorPrefab.Ensure());
                steps.Lap("land snow");
                FatLoadGuard.Step("voxel anchor prefab", () => VoxelAnchorPrefab.Ensure());
                steps.Lap("voxel anchor");
                FiresLogger.LogInfo($"[FiresAdminTerrain] ZNetScene.Awake anchor steps: {steps}.");
            }
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
        private static class ObjectDB_Awake
        {
            private static void Postfix(ObjectDB __instance)
            {
                // 0.2.1059 (load/save audit M5): each step guarded.
                FatLoadGuard.Step("voxel tool pieces", () => VoxelToolPieces.OnObjectDBAwake(__instance));
                FatLoadGuard.Step("chunk generator items", () => ZoneModeChunkGenerator.OnObjectDBAwake(__instance));
                FatLoadGuard.Step("chunk tool items", () => ZoneModeChunkTools.OnObjectDBAwake(__instance));
                FatLoadGuard.Step("sapling items", TreeSaplingPrefabs.OnObjectDBAwake);
                FatLoadGuard.Step("tool icons", VAFatToolIcons.Register);
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
                ZoneModeRegistry.OnPlayerSpawned(__instance);   // Zone mode refused for this world's size, said once
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
            // The same pour + combiner/legacy + generated reads as RaiseToPourSurface + RaiseToCustomWater, timed per part,
            // with the place gate ([Voxel Terrain] "Water level zone gate", default OFF): FatWaterLevelGate.
            private static void Postfix(Vector3 p, ref float __result)
                => global::VerdantsAscent.Water.FatWaterLevelGate.Apply(p, ref __result);
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
                LiquidVoxelSurface.FlattenUnderground(__instance);   // An underground location's liquid lies flat (tar pits)
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
                VerdantsAscent.Terrain.Voxel.FatHaloMap.MapReady();   // 0.2.1037 HaloFlat: the ring only
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
                VerdantsAscent.Terrain.Voxel.FatHaloMap.MapReady();   // 0.2.1037 HaloFlat: the ring only
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
            // used to divert into is retired.)

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
            // lists skips vanilla's search.
            // 0.2.1059 (load/save audit M4): a throw here = no replay; vanilla runs its own search.
            private static bool Prefix(WorldGenerator __instance)
            {
                try
                {
                    BcImport.RiverSync.BeforePregenerate(__instance);
                    return !BcImport.RiverSync.TryReplay(__instance);
                }
                catch (Exception ex) { FatLoadGuard.Failed("river replay", ex); return true; }
            }

            private static void Postfix(WorldGenerator __instance)
                => FatLoadGuard.Step("river record", () => BcImport.RiverSync.AfterPregenerate(__instance));

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            private static void AutoWater(WorldGenerator __instance)
                => FatLoadGuard.Step("auto water data", () => AutoWaterPatches.OnPregenerated(__instance));
        }

        [HarmonyPatch(typeof(ZoneSystem), "Start")]
        private static class ZoneSystem_Start
        {
            private static void Postfix(ZoneSystem __instance)
            {
                // 0.2.1059 (load/save audit M5): each step guarded.
                FatLoadGuard.Step("custom water start", () => CustomWaterPatches.OnZoneSystemStart(__instance));
                FatLoadGuard.Step("zone seeding", ZoneModeRegistry.SubscribeSeeding);
                FatLoadGuard.Step("far quilt start", FarQuilt.OnZoneSystemStart);
                FatLoadGuard.Step("placevegetation patch report", ReportPlaceVegetationPatches);   // 0.2.1415 (EWD proof)
                FatLoadGuard.Step("far forest start", () => VerdantsAscent.Terrain.FarForest.FarForest.OnZoneSystemStart(__instance));   // 0.2.1412
                FatLoadGuard.Step("voxel streamer start", VoxelZoneStreamer.OnZoneSystemStart);
                FatLoadGuard.Step("vegetation exemptions", () => ZoneSystem_PlaceVegetation_Density.WarmExemptions(__instance));
                FatLoadGuard.Step("vegetation warm-up", () => VegetationWarmUp.OnZoneSystemStart(__instance));
                // HERE, not ZNet.Start. Measured on the dedi: ZNet.Start's postfix runs at log line 642
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
                VerdantsAscent.Water.Core.PlacedWaterField.OnHeightmapRegenerate(__instance);   // 0.2.1331
            }
        }

        // 0.2.1350 (proof only, Debug): the zone's spawn stamp for the zone stage line. Reads, sets nothing on the heightmap.
        [HarmonyPatch(typeof(Heightmap), "OnEnable")]
        private static class Heightmap_OnEnable
        {
            private static void Postfix(Heightmap __instance)
            {
                if (!FiresLogger.DebugEnabled) return;
                try { VoxelZoneStreamer.NoteHeightmapEnabled(__instance); }
                catch (Exception ex) { FiresLogger.LogDebug($"[FiresVoxel] heightmap spawn proof threw: {ex.Message}"); }
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
            {
                long t0 = ZoneUpdateTimes.Now();
                global::VerdantsAscent.Utilities.EnvironmentBoxPatches.AfterRebuildRenderMesh(__instance);
                ZoneUpdateTimes.AddIfInside(ZoneUpdateTimes.Cause.HmEnvBox, t0);   // 0.2.1089 [ZoneUpdate]
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last - 5)]
            private static void BiomePaint(Heightmap __instance)
            {
                long t0 = ZoneUpdateTimes.Now();
                global::VerdantsAscent.Terrain.Biome.BiomePaintPersistence.BiomePaintPatches.ApplyPaintColors(__instance);
                ZoneUpdateTimes.AddIfInside(ZoneUpdateTimes.Cause.HmPaintColours, t0);   // 0.2.1089 [ZoneUpdate]
            }
        }

        [HarmonyPatch(typeof(ClutterSystem), "GenerateVegPatch")]
        private static class ClutterSystem_GenerateVegPatch
        {
            // All three run, as separate prefixes did: the water-level move and the spread takeover still happen when a
            // dedicated server skips the build. A server without graphics grows no grass at all (without this,
            // patches were built on the headless dedi); no patch is vanilla's own "nothing here".
            // 0.2.1089 (FDT, live 1088: this prefix + finalizer were among the hottest hooks, 372 calls/s): the headless answer
            // is asked of the engine once, the finalizer does work only when an exception is present or the water band was moved,
            // and the gate stages are timed for the [ClutterGate] hook line (every 30 s, Debug).
            private static int s_headless = -1;   // -1 unknown, 0 no, 1 yes
            private static int s_calls, s_headlessSkips, s_flatBlocked, s_spreadBuilt, s_vanillaRan;
            private static long s_gateTicks, s_buildTicks;
            private static float s_logAt;

            private static bool Prefix(ClutterSystem __instance, Vector2Int patchID, float size, ref object __result)
            {
                if (s_headless < 0) s_headless = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ? 1 : 0;
                s_calls++;
                if (s_headless == 1)
                {
                    s_headlessSkips++;
                    __result = null;
                    return false;
                }
                long t0 = Stopwatch.GetTimestamp();
                // Super flat grows no AUTO clutter (without this: 64 patches / 169 objects). This gate must come before
                // ClutterSpread, which builds its own patch and filled it after FatSuperFlat.NoClutter had nulled the result (Harmony runs
                // every prefix). FAP-painted clutter is placed objects, not ClutterSystem patches, so it is untouched.
                if (global::VerdantsAscent.Terrain.Voxel.FatSuperFlat.On && !global::VerdantsAscent.Terrain.Voxel.FatSuperFlat.ClutterAllowed(patchID, size))
                {
                    global::VerdantsAscent.Terrain.Voxel.FatSuperFlat.NoteAutoClutterBlocked();
                    s_flatBlocked++;
                    s_gateTicks += Stopwatch.GetTimestamp() - t0;
                    Report();
                    __result = null;
                    return false;
                }
                bool runOriginal = global::VerdantsAscent.Utilities.EnvironmentBoxPatches.ClutterRunsHere();
                ClutterSystem_GenerateVegPatch_WaterEcology.Apply(__instance, patchID);
                long t1 = Stopwatch.GetTimestamp();
                s_gateTicks += t1 - t0;
                if (!global::VerdantsAscent.Performance.ClutterSpread.OnGenerateVegPatch(__instance, patchID, size, ref __result))
                {
                    runOriginal = false;
                    s_spreadBuilt++;
                }
                else if (runOriginal) s_vanillaRan++;
                s_buildTicks += Stopwatch.GetTimestamp() - t1;
                Report();
                return runOriginal;
            }

            private static Exception Finalizer(ClutterSystem __instance, Exception __exception, Vector2Int patchID)
            {
                if (__exception != null)
                    __exception = global::VerdantsAscent.Utilities.EnvironmentBoxPatches.SuppressClutterNre(__exception, patchID);
                ClutterSystem_GenerateVegPatch_WaterEcology.Restore(__instance);   // returns at once unless Apply moved the band
                return __exception;
            }

            // [ClutterGate] hook line: how many patches were asked for, what the gates answered, and the time the gates
            // (super flat + water memo) and the build (ClutterSpread / vanilla) took. A high patch count with a cheap gate
            // means grass is being RESET, not that the gate is slow: the ResetGrass line names who resets.
            private static void Report()
            {
                float now = Time.unscaledTime;
                if (now < s_logAt) return;
                bool first = s_logAt == 0f;
                s_logAt = now + 30f;
                if (first) { FiresLogger.LogDebug("[ClutterGate] GenerateVegPatch hook executing"); return; }
                if (FatDiag.ClutterGate)
                {
                    double ms = 1000.0 / Stopwatch.Frequency;
                    FiresLogger.LogDebug($"[ClutterGate] GenerateVegPatch last 30 s: {s_calls} patch(es) asked ({s_calls / 30f:0.0}/s); "
                        + $"super-flat gate refused {s_flatBlocked}, ClutterSpread built {s_spreadBuilt}, vanilla built {s_vanillaRan}, headless skipped {s_headlessSkips}; "
                        + $"gates {s_gateTicks * ms:0.0} ms total ({(s_calls > 0 ? s_gateTicks * ms / s_calls : 0):0.000} ms a patch), builds {s_buildTicks * ms:0.0} ms; "
                        + $"water memo {ClutterSystem_GenerateVegPatch_WaterEcology.TakeMemoStats()}");
                }
                s_calls = s_headlessSkips = s_flatBlocked = s_spreadBuilt = s_vanillaRan = 0;
                s_gateTicks = s_buildTicks = 0;
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
                // 0.2.1029 HaloFlat: inside the ring's gravity field "ground" is the shell beside/under you along the ring's up,
                // not the world-down ray vanilla casts from y 6000 (it would hit the wall above and snap the character onto it).
                if (__instance != null && VerdantsAscent.Terrain.Voxel.FatHaloArc.Active
                    && VerdantsAscent.Terrain.Voxel.FatHaloArc.TryGetUp(__instance.transform.position, out Vector3 ringUp))
                {
                    // A13 ([fgn]): a player stuck inside the ring's shell goes up along the ring's up (VoxelUnderWorld.RingLiftIfEmbedded).
                    VerdantsAscent.Terrain.Voxel.VoxelUnderWorld.RingLiftIfEmbedded(__instance, __instance.transform.position, ringUp);
                    return false;
                }
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

            // 0.2.1257 ([lead] round N: "ZoneSystem.Update threw ... ArgumentOutOfRangeException at ZoneSystem.DMD<ZoneSystem::SpawnZone> at
            // ZoneSystem.CreateGhostZones" with no deeper frame): Harmony rethrows a finalizer's exception from the DMD, which resets its
            // trace, so the real thrower inside SpawnZone (vanilla or any mod's patch it calls) was hidden. The ORIGINAL trace + the zone
            // and mode are logged here first (every 10 s at most).
            private static float s_nextTrace;
            private static Exception Finalizer(Exception __exception, bool __state, Vector2s zoneID, ZoneSystem.SpawnMode mode)
            {
                if (__exception != null && UnityEngine.Time.realtimeSinceStartup >= s_nextTrace)
                {
                    s_nextTrace = UnityEngine.Time.realtimeSinceStartup + 10f;
                    FiresLogger.LogWarning($"[FiresZones] SpawnZone({zoneID.x},{zoneID.y}, {mode}) threw (the original trace, before Harmony rethrows it from the DMD): {__exception}");
                }
                return ZoneMode_ZoneSystem_SpawnZone.Finally(__exception, __state);
            }
        }

        // 0.2.1415 ([lead] URGENT 11-08, Expand World Data's PlaceVegetation transpiler was dropped under FAT 1412-1414): one Info line
        // per world start listing every owner's patches on ZoneSystem.PlaceVegetation from Harmony's own patch info, so the log proves
        // which transpilers are live (EWD's expand_world_data among them when it patched cleanly).
        private static void ReportPlaceVegetationPatches()
        {
            var m = AccessTools.Method(typeof(ZoneSystem), "PlaceVegetation");
            var info = m != null ? Harmony.GetPatchInfo(m) : null;
            if (info == null) { FiresLogger.LogInfo("[FatHooks] PlaceVegetation: no Harmony patches."); return; }
            string Owners(IEnumerable<Patch> ps) { var l = new List<string>(); foreach (var p in ps) l.Add(p.owner); return l.Count == 0 ? "none" : string.Join(", ", l); }
            FiresLogger.LogInfo($"[FatHooks] PlaceVegetation patches: transpilers [{Owners(info.Transpilers)}]; prefixes [{Owners(info.Prefixes)}]; postfixes [{Owners(info.Postfixes)}]; finalizers [{Owners(info.Finalizers)}].");
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
                VerdantsAscent.Terrain.FarForest.FarForest.BeginPlaceVegetation();   // 0.2.1415: census depth flag (no IL change)
                ZoneSystem_PlaceVegetation_Density.Apply(__instance);
                ZoneSystem_PlaceVegetation_WaterEcology.Apply(__instance);
                ZoneModeStartCave.KeepMouthClear(zoneCenterPos, clearAreas);
                FatUnderWaterPlacement.BeginZone(zoneCenterPos);
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => FatUnderWaterPlacement.Transpile(ZoneSystem_PlaceVegetation_WaterEcology.Transpile(instructions));   // 0.2.1415: the 1412-1414 census wrap is GONE (it replaced the two Instantiate calls Expand World Data anchors on)

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
                VerdantsAscent.Terrain.FarForest.FarForest.EndPlaceVegetation();
                ZoneSystem_PlaceVegetation_Density.Restore(__instance);
                ZoneSystem_PlaceVegetation_WaterEcology.Restore(__instance);
            }
        }

        // Zone mode refuses teleports into closed zones (prefix); the voxel void net remembers where a body was
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

        // Vanilla's location suitability test (ZoneSystem.cs:1271, and FAT's BiomePaint.Locations) refuses a site
        // whose area overlaps FAT water (FatUnderWaterPlacement).
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetTerrainDelta))]
        private static class WorldGenerator_GetTerrainDelta
        {
            private static void Postfix(Vector3 center, float radius, ref float delta)
                => FatUnderWaterPlacement.TerrainDeltaPostfix(center, radius, ref delta);
        }

        // 0.2.1331: the scope ZoneSystem_SpawnLocation reads. The finalizer clears it on an exception too; __state means a finalizer
        // whose prefix never ran (another mod's skip) cannot unbalance it.
        // 0.2.1338: calls and refusals per proxy (location hash + position) while fat_morgenhole_retry_test watches.
        internal sealed class ProxyWatch
        {
            internal readonly Dictionary<KeyValuePair<int, Vector3>, int> Calls = new Dictionary<KeyValuePair<int, Vector3>, int>();
            internal int Total, Refusals;
            internal void Call(int hash, Vector3 pos) { Total++; var k = new KeyValuePair<int, Vector3>(hash, pos); Calls.TryGetValue(k, out int n); Calls[k] = n + 1; }
        }
        internal static ProxyWatch WatchProxies { set { ZoneSystem_SpawnProxyLocation.s_watch = value; } }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnProxyLocation))]
        private static class ZoneSystem_SpawnProxyLocation
        {
            private static int s_depth;
            private static bool s_infoSaid;
            private static readonly HashSet<string> s_said = new HashSet<string>();
            internal static bool Active => s_depth > 0;

            internal static void NoteLetThrough(string prefab, Vector3 pos)
            {
                if (!s_infoSaid) { s_infoSaid = true; FiresLogger.LogInfo("[FAT] super flat: an existing LocationProxy's location passes the surface rule; planned locations stay refused (0.2.1331)"); }
                if (s_said.Add(prefab ?? "")) FiresLogger.LogDebug($"[FAT] super flat: location {prefab} at ({pos.x:0},{pos.y:0},{pos.z:0}) let through: LocationProxy spawn (ZoneSystem.SpawnProxyLocation scope), no per-frame retry");
            }

            [HarmonyPriority(Priority.First)]
            private static void Prefix(int hash, Vector3 pos, out bool __state) { s_depth++; __state = true; s_watch?.Call(hash, pos); }

            // 0.2.1338 (fat_morgenhole_retry_test): the postfix sees vanilla's answer; a null is a refused proxy call. Only while a test watches.
            private static void Postfix(GameObject __result) { if (s_watch != null && __result == null) s_watch.Refusals++; }
            internal static ProxyWatch s_watch;

            [HarmonyPriority(Priority.Last)]
            private static void Finalizer(bool __state) { if (__state && s_depth > 0) s_depth--; }
        }

        // 0.2.1340: the client's redraw of a no-level location. ZoneSystem.SpawnLocation only sees pos + location; the proxy's ZDO says it carries vanilla's
        // TerrainModifier field override (FAP/FDM proxies, PlanBuild blueprint rows): the same three steps stand aside as inside FatApi.BeginNoLevelSpawn.
        [HarmonyPatch(typeof(LocationProxy), "SpawnLocation")]
        private static class LocationProxy_SpawnLocation
        {
            private const string FieldKey = "HasFieldsTerrainModifier";
            private static readonly FieldInfo s_nview = AccessTools.Field(typeof(LocationProxy), "m_nview");
            private static bool s_missingSaid;
            private static int s_depth;
            internal static bool Active => s_depth > 0;

            [HarmonyPriority(Priority.First)]
            private static void Prefix(LocationProxy __instance, out bool __state)
            {
                __state = false;
                if (s_nview == null)
                {
                    if (!s_missingSaid) { s_missingSaid = true; FiresLogger.LogWarning("[FAT] LocationProxy.m_nview not found: proxy no-level check off"); }
                    return;
                }
                var zdo = (s_nview.GetValue(__instance) as ZNetView)?.GetZDO();
                if (zdo == null || !zdo.GetBool(FieldKey)) return;
                s_depth++;
                __state = true;
            }

            [HarmonyPriority(Priority.Last)]
            private static void Finalizer(bool __state) { if (__state && s_depth > 0) s_depth--; }
        }

        [HarmonyPatch]
        private static class ZoneSystem_SpawnLocation
        {
            private static readonly HashSet<string> s_noLevelSaid = new HashSet<string>();
            private static bool s_noLevelInfoSaid;

            // Scope or proxy field: this location sits exactly at its pos (0.2.1340).
            private static bool NoLevel(ZoneSystem.ZoneLocation location, Vector3 pos)
            {
                bool scope = FatApi.NoLevelSpawn, field = LocationProxy_SpawnLocation.Active;
                if (!scope && !field) return false;
                if (!s_noLevelInfoSaid) { s_noLevelInfoSaid = true; FiresLogger.LogInfo("[FAT] locations in FatApi.BeginNoLevelSpawn, or redrawn from a proxy with the TerrainModifier field, are not levelled (0.2.1340)"); }
                if (s_noLevelSaid.Add(location?.m_prefabName ?? "")) FiresLogger.LogDebug($"[FAT] location {location?.m_prefabName} at ({pos.x:0},{pos.y:0},{pos.z:0}): no levelling ({(scope ? "scope" : "proxy field")})");
                return true;
            }

            private static MethodBase TargetMethod()
                => AccessTools.Method(typeof(ZoneSystem), "SpawnLocation", new[]
                {
                    typeof(ZoneSystem.ZoneLocation), typeof(int), typeof(Vector3), typeof(Quaternion),
                    typeof(ZoneSystem.SpawnMode), typeof(List<GameObject>), typeof(bool),
                });

            // First, so a location's pad levels to the corrected height rather than the generated one.
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(ZoneSystem.ZoneLocation location, ref Vector3 pos, bool cheated, ref GameObject __result, out bool __state)
            {
                bool noLevel = NoLevel(location, pos);
                if (!noLevel) FiresLocationHeight.Correct(location, ref pos);
                // 0.2.1075 (Fire: only the overworld is flat): on super flat a location may stand in a cave or on a sky island, never on
                // the flat ground (the start temple and the allowed extras excepted).
                // 0.2.1325 ([saddles] launch 2: FAP dungeons / `location` / FDM doors refused, dedi "PlaceofMystery3 ... refused"): the rule is
                // for world-gen placement only; a deliberate spawn (vanilla's cheated flag, or a mod inside FatApi.BeginDeliberateSpawn) passes.
                bool deliberate = cheated || FatApi.DeliberateSpawn;
                bool refused = !deliberate && VerdantsAscent.Terrain.Voxel.FatSuperFlat.RefusesSurfaceLocation(location, pos);
                // 0.2.1331 (proxy loop): vanilla LocationProxy retries every frame while its location is not spawned, so a refusal here
                // looped ~113 times a second for 12 h and could hold SetLoadingInZone. A proxy already stands in the world: drawing its
                // location is not world-gen placement. ONLY ZoneSystem.SpawnProxyLocation's scope passes; PlaceZone's planned locations
                // (also mode Client, also through here) stay refused.
                if (refused && ZoneSystem_SpawnProxyLocation.Active) { refused = false; ZoneSystem_SpawnProxyLocation.NoteLetThrough(location?.m_prefabName, pos); }
                __state = !refused;
                if (deliberate && VerdantsAscent.Terrain.Voxel.FatSuperFlat.On) FatApi.NoteDeliberateSpawn(location?.m_prefabName, pos, cheated);
                if (!__state) { __result = null; return false; }
                if (!noLevel) VerdantsAscent.Terrain.Zones.FatLocationAccelerator.NoteSpawn(location, pos);   // Level sites, before any modifier wakes
                return true;
            }

            private static void Postfix(ZoneSystem.ZoneLocation location, Vector3 pos, ZoneSystem.SpawnMode mode, bool __state)
            {
                if (!__state) return;
                if (!NoLevel(location, pos)) LocationSpawnPads.OnSpawned(location, pos, mode);
                FatUndergroundLocations.OnSpawned(location, pos, mode);   // An underground location's dome, floor and shaft
            }
        }
    }
}
