using BepInEx;
using FiresCore.Sync;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Terrain.Persistence;
using VerdantsAscent.Terrain.Utilities;

namespace VerdantsAscent
{
    // Standalone Fires-* mod hosting CustomTerrain + CustomWater. Clutter is
    // delegated to FAP (source-linked) Ã¢â‚¬â€ see csproj for the linked files.
    //
    // Companion / NPC / Quest / Guild / Discord / Wayshrine / Territory init
    // intentionally absent: this mod is admin terrain + water tools only.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    // Soft dependencies Ã¢â‚¬â€ these mods are not required for FAT to load, but
    // we integrate with them when present. Declaring them gives r2modman /
    // Thunderstore proper "depends on" metadata AND lets BepInEx order our
    // Awake AFTER theirs, so reflective lookups (AdminCheck, IH ruler
    // patches, WEC undo bindings) hit fully-initialized targets.
    [BepInDependency("Azumatt.InfinityHammer",             BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.serversync",     BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.worldeditcommands", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.serverdevcommands", BepInDependency.DependencyFlags.SoftDependency)]
    // FiresTossinShade owns the water SHADERS/bundle; FAT owns the water SYSTEM
    // (clock, CalcWave, gen liquid). SOFT, not hard: FAT degrades to vanilla
    // water material when FTS is absent (WaterMaterialManager). The only
    // cross-call is via FiresUnifiedCore's WaterShaderBridge (reflection, no
    // assembly ref), so this attribute is purely load-ordering (FTS Awake
    // before ours, so the bridge is live when we ask) + Thunderstore metadata.
    [BepInDependency("com.Fire.FiresTossinShade", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.Fire.FiresAdvancedPlayerMovement", BepInDependency.DependencyFlags.SoftDependency)]   // 0.2.1438: APM (swimming) loads first so PlayerSwim.RoofAbove can be shared
    [BepInDependency(FiresCore.FiresUnifiedCore.PluginGUID, "0.2.816")]   // 0.2.1048: FatHaloSpawns calls Core Surface.Point (0.2.816+)
    // ValheimPerformanceOptimizations replaces vanilla's zone spawn and heightmap render / collision builds outright, which
    // undoes FAT's own terrain pipeline (voxel ground, zone mode, the dedi's off-map refusal). FAT does its own optimizing;
    // It is listed as incompatible, so BepInEx refuses to load FAT beside it.
    [BepInIncompatibility("dev.ontrigger.vpo")]
    public class FiresAdminTerrain : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresAdminTerrain";
        public const string PluginName = "FiresAdminTerrain";
        public const string PluginVersion = "0.2.1520";   // 0.2.1520 ([worldgen] 11-25, Valheim 1.0.30): SoftReference<GameObject>.Name -> .name (renamed in 1.0.30), no other change.
        // was 0.2.1519: 0.2.1519 ([worldgen], [seasons] 11-24 for Fire's shovel report): FatApi adds read-only ShovelPatchApiVersion = 1, ShovelPatchRadius (live patch width / 2) and ShovelPatchCentre(at) (ring centre, FatShovelPatch.SnapToTexel(at, false)) so Vedr's ground dig matches FAT's shovel. No behaviour change. Built on 0.2.1518.
        //                                                     // 0.2.1518 ([worldgen], Fire 11-23 snow seams at chunk borders on HD ground): FatIslandGroundGuard keeps a modifier off a heightmap only when it stands ON a skyland; on normal ground its one-sided height test split shared border vertices. Built on 0.2.1517.
        //                                                     // 0.2.1517 ([worldgen], [lead] 11-23 allocation capture: EBM rebuilt all far tiles on every FarEditRevision bump, 54 bumps in Fire's hand test with no edits): DistantHeightCache captures are deterministic and quiet. A zone is re-captured only when its TerrainComp data revision or the set of height-changing TerrainModifiers over it changed (count + position/level hash), and only while every object in its 3x3 zones is instantiated (vanilla IsAreaReady rule, own list), so a half-loaded zone never replaces a fuller capture. Stored value stays final - worldgen base INCLUDING modifiers. Revision moves only when a stored delta changes. Info line per 60 s with captures. Built on 0.2.1513 (deployed); 1514 / 1515 / 1516 not included. // 0.2.1513 ([saddles], [lead] 11-21 1512 shots): New World panel: rows that cannot change stay in place greyed (label + control alpha 0.4) instead of hidden (no gap under Sky islands); the generator shows "Set by Super flat" greyed while locked; Gaea mode keeps its generator lock (Refresh no longer undoes it); each toggle row is clickable across its whole width (invisible RowHitArea). // 0.2.1512 ([saddles], Fire 11-21 "still shit", [2ic]-approved): New World panel: label column widened (controls from 45% of the row, field >= 180), the tick shows at once (no fade) on the cloned toggles; Debug lines for the dropdown Show state and OnWorldStart called/after (Fire's 1510 NRE and "Start did nothing" stay UNCONFIRMED until they recur). // 0.2.1510 ([saddles], Fire 11-21 "new world ui ... isnt even readable", [2ic]-approved): New World panel rows: dropdown text in vanilla seed-text ink (was Core ParchmentInk, dark on wood), toggles = clones of vanilla public-server toggle (shared BcConvertDialog.CloneVanillaToggle, Automatic nav), rows sized to their wrapped label, 2-line auto-size status box. // 0.2.1509 ([saddles], [lead] 11-21, [2ic]-approved): while EBM 1.2.483+ owns the far ground (EbmApi.FarGroundOwned, soft bind, FarTerrainVanilla.cs EbmFarGround) FAT builds no ExtendedReach second TerrainLod and no far-quilt rings 2/3, destroys live ones, and brings them back after 5 s of not-owned; FatApi far data unchanged. // 0.2.1508 ([saddles], Fire 11-21 build-once goal, [2ic]-approved design): the paint re-colour pass after a paint change (client join RPC_List, local edits) pokes only heightmaps painted under the current OR a replaced circle set (BiomePaint.RefreshPainted), not every loaded heightmap; FatApi.RefreshBiomeOverrides keeps the full pass. 0.2.1507 = outside-refs move (csproj + lib only). // 0.2.1506 ([saddles], [lead]/[2ic] nits on 1505): the pre-camp restore line no longer names fat_precamp_check as if release had it ("test builds only: ..."); s_lastRestored (read only by fat_precamp_check) is FIRES_TESTS-only (no release warning). FULL list of what only a FiresTests build compiles (1505 + 1506): ModulesTesting*.cs (23 files: AdminTools*, CaveEnvTest, CavesTest, ClutterFloatTest, FatBlackPaintTest, FatCircleTest, FatOpenGroundSearch, FatRound1339Tests, FatTestBot, FatWaterPersistTests, FishWaterTest, LoadTest, NoClipTest, OceanTest, RiverCarveTest, SkylandCheck, VanillaOps, WaterLifeTest, WaterLookTest); VoxelTerrainFatWetMemoCheck, VoxelTerrainFatShorePoses, ZoneModeFatContentTests, ZoneModeZoneModeChunkGeneratorTest, HeightmapSnowFatTrailFieldTest, HeightmapSnowFatZonePaintDump, CustomWaterFishCheckCommand; in FatTerrainSnapshot only the commands fat_snapshot_test / fat_precamp_check / fat_camp_residue_clear (+ CheckLast, ClearResidue, s_lastRestored); their registers in VoxelBrushDrag, ZoneModeChunkGenerator, FatVanillaHooks, FatTerminalCommands, FatApi. 0.2.1505 ([saddles], Fire 11-20 build switch): test code is compiled in only with /p:FiresTests=true (rig / dev build) AND the Testing folder present; default = release without FIRES_TESTS. FatBlackPaintTest moves into the test group; its Register and FatWaterPersistTests.RegisterRpc calls are inside FIRES_TESTS; also test-build only ([lead] / Fire 11-20): ZoneModeChunkGeneratorTest, fat_trail_field_test, fat_zone_paint_dump, fat_snapshot_test, fat_precamp_check, fat_camp_residue_clear, va_fish_check, va_fish_spawn (the snapshot Capture / Stamp / Apply used by the water-shaping tools stay). 0.2.1504 ([worldgen], [2ic] / [lead] 11-20 option (a); every rig load leg logged "[Spawn] restored ... ON THE GROUND: the saved point was 16.7 m up with nothing under it after 20 s"): the login-spawn "raised" hold (FatLoginSpawn) ends at once when nothing can still arrive under the saved point. Host / single player only; every valid ZDO in the 3x3 zones already instantiated (vanilla's IsAreaReady rule, re-checked because mods patch IsAreaReady); no voxel chunk / voxel-only / swapped zone; no sky islands and the island set in; more than 9.5 m above the ground (TerrainComp edit reach +-8 m level, +-1 m smooth). The player lands where the 20 s timeout put them, without the wait. Each hold end logs why: support found / nothing can arrive / timeout, with seconds. Clients keep the full wait.
        // was 0.2.1503:   // 0.2.1503 ([worldgen], [2ic] / [lead] option C, NOT built until asked): FAT's 10 AccessTools.TypeByName lookups of Core / FAT types (9 FiresCore.*, 1 FAT BiomePaint) ask the owning assembly directly (Utilities\FatTypeLookup.cs: typeof(FiresCore.FiresUnifiedCore).Assembly.GetType(name, false) / FAT's own). HarmonyX's TypeByName missed with Type.GetType from its own assembly and then walked every loaded assembly's GetTypes(); now there's no scan. Same Type when present, null when an older Core lacks it (every site null-checks), no throw; only the "Could not find type named" Warn on a miss is gone. Left as they were: ExpandWorldData (third-party), ServerDevcommands (Testing).
        // was 0.2.1502:   // 0.2.1502 ([worldgen], [2ic] / [lead] load-time cut, rows "FatUndo+IceWaterCommands_Undo 116.7 / 115.8 ms", "FatUndo+IceSheetEdits_Undo 26.9", "FatUndo+WaterPlacement_Undo 17.1"): FatUndo's 16 Harmony patch classes patched FAT's OWN methods (71 methods). They are gone; each of those methods now calls FatUndo directly with the same arguments, order and exception handling (prefix + finalizer -> try { Begin...; body } finally { End... } through a <name>_Body split; prefix -> first statement; postfix -> after a normal return). Undo records are unchanged. Proof table: Tools\REPORT_worldgen_fat1502_undo_proof_20261120.md. Also: FatTerminalCommands' calls into the test-only files (FatWetMemoCheck, FatShorePoses, FatContentTests: csproj compiles them only with Modules\Testing) are inside #if FIRES_TESTS (1501 called them unconditionally, which breaks a build without the Testing folder), and the two Testing InitTerminal classes (FatCircleTest, FatWaterPersistTests) are merged there too: ONE InitTerminal patch in every build.
        // was 0.2.1501:   // 0.2.1501 ([worldgen], [2ic] / [lead] load-time candidate, OPTION build until the load_pfa_info [FatLoad] rows say so): 13 of FAT's 16 Terminal.InitTerminal patch classes merged into ONE (Modules\FatTerminalCommands.cs) that calls their bodies unchanged in csproj order, each in its own try/catch; the 13 lose their own attributes. The 2 Testing classes keep their own patch (Modules\Testing compiles only with AdminToolsTest.cs); FiresVegRegen's is not compiled. Harmony rebuilds the patched method once per added patch, so the start-up patch step loses 12 rebuilds of InitTerminal.
        // was 0.2.1500:   // 0.2.1500 ([worldgen], [lead] FAT perf audit Tools\REPORT_worldgen_fat_perf_audit_20261119.md; gates on real conditions, same answers): (row 2) FatGenTiming's 14 timer patch classes install only with the new [FiresAdminTerrain 99 Diagnostics] "Generator timing" key (default OFF, Harmony Prepare; sync-wait helpers gated too); (row 5) BodyHold's ItemDrop/Floating TerrainCheck + StaticPhysics.SUpdate prefixes test the empty held set before the native reads; (row 6) the skyland hold's no-islands skip runs before its timer; (row 9) FiresCaveBiome.InCave answers false before counters / stack sampling with no voxel ground and Debug off, the caller StackTrace sampler is Debug-only; (row 10) floater lookups (FatWaterLevelGate.Apply, GeneratedWaterFeeder.Feed / RaiseToFatWater, FatWaterChecks.FatOnlySurfaceAt) return at once under NoFatWaterAnywhere; (row 11) ice-restore ZNetView.Awake decides the name match once per prefab hash; (row 13) the shovel ring check keeps the last weapon's answer, no name string per frame; (row 17) the terrain-op timing prefix runs only with FatNativeTrace or FatDiag Terraform on; (row 19) VoxelSupportHold answers before GetInstanceID / transform with the streamer off and nothing held; (row 23) the snow-trail refusal line is Debug-gated before its string; (row 1, [seasons] option A) the ice cover reads Vedr lake ice for the whole rect with ONE VedrApi.FillLakeIce call per fill (VedrApi 40, Vedr 0.8.303; bound by presence, an older Vedr keeps the per-texel IsLakeIceAt).
        // was 0.2.1499:   // 0.2.1499 ([worldgen], [lead] fat1498-pin-r1 "[FatLoad] plugin start 11617 ms: ... config binds + sync + player-facing 9484 ... Harmony patch classes 1692"): BepInEx 5.4.23.5 ConfigFile.Bind saves the whole .cfg after every new key while SaveOnConfigSet is true (decompiled Bind), so the bind block wrote the big file once per key. SaveOnConfigSet is held false from ConfigManager.Initialize through the ServerSync entries and put back in a finally, then ONE Config.Save() (before the admin gate / player-facing passes, so the file written is the same as before). [FatLoad] splits the old step: config binds (N entries, save held), config retire, ServerSync entries, config save (once), admin gate + player-facing; the Harmony part splits patched vs skipped types. No cfg key, no value change.
        // was 0.2.1498:   // 0.2.1498 ([worldgen], [lead] 11-19 split legs: "in world after" same7 54.5 s -> FAT only 72.6 s, chainloader up 18.3 s -> 30.4 s with FAT the only plugin added, and FAT's plugin start had no step timings): load clock (Modules\Performance\FatLoadClock.cs), read-only. One Info line "[FatLoad] plugin start N ms" with each Awake / FinishInit step and the Harmony loop (types walked, total ms, the 12 slowest patch classes); one Info line at the local player's first spawn "[FatLoad] FAT's measured load contribution: plugin start + world entry = N ms" with FAT's self-timed world-entry steps (ZNetScene.Awake steps + anchors, world init, water guards freeze). No behaviour change; cuts follow from these rows.
        // was 0.2.1497:   // 0.2.1497 ([worldgen], [lead] vf-pin-r1 summer town on a vanilla world, FAT's own timed rows): (1) StaticPhysics.SUpdate prefix ("263192 call(s)/min: cave check 0.27 us, skyland check 0.60 us" ~3.8 ms/s): the skyland check and its transform read run only while the world has a skyland island or a rising skyland (same answer: IsOnZoneSkyland is false without FiresSkyIslands.Enabled, RisingAt false without a disc), split timer samples 1 call in 64. (2) Character swim checks ("WaterLiquid 1.33 ms/s, 45270 calls"; census 0 FAT water): with no legacy water, combiner, pour or voxel chunk (and the legacy mesh key off) GetValidWaterLevelForCharacter answers "none" before the memo and timers; the FatHooks line counts those answers. No key, no cfg write.
        // was 0.2.1496:   // 0.2.1496 (winter split step 1, phase A, [seasons] agreed): FatApi.RegisterSeasonalIce(owner, biomeMask, exposure, derive, revision, snowing) + LegacyIceExposure + SeasonalIceApiVersion 1; with a provider registered FAT seasonal ice (IceField F integration / broadcast / steps, VanillaIce season rule, IceFrost snowing, FatSeasons instant ice reach) stands down; Deep North biome ice, caves, fire melt, holes, frost cells stay FAT; no provider = unchanged. 0.2.1495 ([lead] perf): (a) no snow lying (no snow biomes or gradual fill 0) = every FatSeasons winterize path returns its input (voxel RenderColors, distant tiles, FarQuilt/painted/world winter, FatApi TryFarColor), so summer equals 0.2.1491; a positive Vedr fill under one step is kept at 0.0001 so the distant-ring reset still runs; (b) TryGetBiomePaint keys cached per circle id (no string per call). 0.2.1494 (God Complex Phase 1, Fire "yes go on phase 1", [ghost]): FatApi StudioApiVersion 7, add-only: BiomePaintCount / BiomePaintLastId / TryGetBiomePaint / BiomePaintUndo / BiomePaintUndoIds / BiomePaintRedo over BiomePaint.TryTakeBack / TryPutBack (server side, own 64-group redo stack). 0.2.1493 (Fire 11-19 "yes do the log line then D, and fix the studio"): every Studio "not loaded:" refusal (18 sites) logs one rate-limited Info line "[StudioRefusal] <reason> | at (x,z) zone: IsZoneLoaded, IsZoneGenerated, FindHeightmap, ground raycast; peer kind" via FatStudioNotLoaded; reason text unchanged. 0.2.1492 (FatApi v5 TryFarTrueColor for EBM 1.2.470 shader snow; distant-ring snow-splat reset while gradual; [lead] Fire 11-19 "all snow in all forms should gradually melt ... never a hard switch": FAT ground snow = Vedr snow CLASS mask (where) x winter-ground fill (how much, 0.05 steps) via Core SeasonBridge SnowClassMask / WorldSnowFill / GradualSnow / SnowFillRevision, reflection-bound, absent = 0.2.1491 behaviour; voxel ground, FarQuilt, painted circles, skylands/tiles/env boxes, distant ring and FatApi far colour; each step recolours nearest first 8 a frame incl. distant tiles and FarQuilt tiles). 0.2.1491 ([lead] Fire order 11-17 Vfall: the player's Vfall places ONLY the waterfall visual, no spillway notch, no landing pool ("Vfall digs a landing pool" option removed), saved as its own fires_vfall object (PlacedFalls.cs; proof line "[Vfall] placed: ... saved as fires_vfall ..., ground untouched"), drawn with the Skylands falls' AppendCurtain arguments; dedi keeps the ZDO only). 0.2.1490 ([lead] 11-19: 1488 rebased onto 1489 = BC biome map as byte indices + 256-entry table, 64 -> 16 MiB on VADN, proof line "[BcImport] biome map held as byte indices"; streamer driver counter: zone scan its own stage, "far impostors" relabelled "distant paint LOD"; counting only). 0.2.1489 ([lead] G5e, FatApi v4 with [visual]: FarVegetationCounts / FarVegetationDensity / FarVegetationRevision, the density scale server-synced; on 1487, without 1488). 0.2.1487 ([lead] RAM audit 11-19: encoded BC map bytes released after decode, pooled band scratch freed after load, dedi skips the display-only deep-ground tint). 0.2.1486 ([worldgen], [seasons] Vedr tide, lead-approved): FatApi.RiversCarved (add-only): true when vanilla's AddRivers carves this world's river and stream lists; false on a converted BetterContinents world whose data turns rivers off (VADN) and on a FAT-generated world, where the lists still exist but name channels the ground never gets. Nothing else changes from 0.2.1485.
        // was 0.2.1484:   // 0.2.1484 ([worldgen], [lead] 11-18: 1483 stood down on Fire's full stack, Expand World Data 1.79 patches WorldGenerator.Initialize / Pregenerate / VersionSetup): the far-gen "not ours" rule is now proven per method. At FarGenInit the set of WorldGenerator / HeightmapBuilder methods HeightmapBuilder.Build reaches is read from the original IL; not-ours patches on that set run off the main thread in vanilla too and are INCLUDED (listed once in the ready line); patches outside it (EWD's world setup) are never reached and ignored. It refuses only if a game update takes GetBiomeSector out of Build's reach. The ready / stand-down line is logged once per distinct answer (1483 logged it on every call). FarGenForeignPatches is informational.
        // was 0.2.1483:   // 0.2.1483 ([worldgen], Fire 11-18 via [lead]: "we will need to build the API and the vanilla read path ... ebm would need to be able to properly read the world information from FAT when both are installed"): FatApi FarDataApiVersion 3 adds FarGenInit / FarGenFlags / FarSampleZone / FarSampleSector / FarBiomeAt / FarForestFactor / FarGenForeignPatches (FatFarGen.cs; contract agreed with [visual]): world generation as FAT makes it, for EBM's far vegetation workers, by thread-safe paths that run FAT's own code (the real HeightmapBuilder.Build on the caller's thread, the zone gate + vanilla sector table + BiomePaint.Paint, BiomeOverride.TryAt, vanilla forest noise + the BC forest map). Stands down when a mod that is not ours patches world generation. FatGenTiming never logs from those threads. FIX: FatApi.TryFarHeight (EBM far tiles) now runs in the builder's corner scope (it ran FatBiomeResolver's unlocked TileAnchorRegistry read on EBM's thread).
        // was 0.2.1482:   // 0.2.1482 ([worldgen], Fire 11-18 via [lead]: after EBM 1.2.456 proved its own far trees exact with no FAT, 81/81 and 62/62): FAT's [Far Forest] is REMOVED. Gone: FarForest (kinds, RNG self-test, draw census), FarReplay, FarRng, FarRngSelfTest, FarMatchTest, FarBridge (the EBM / Core impostor feed, the canopy sink, fat_farveg_compare, InitCoreFar), FarTreePlacement, their 3 vanilla hook calls and the 8 [Far Forest] keys. FAT places, feeds and draws no far tree; EBM reads FAT only on FAT custom / voxel worlds (FatApi). Saved [Far Forest] lines stay in the cfg, unread. 0.2.1481 (the held bridge fix) was never built. Archive: F:_archive_fat_farforest_20261118.
        // was 0.2.1480:   // 0.2.1480 ([worldgen], Fire 11-18 on the vista2 black ridge lines: "remove all of the ... [far] billboards from fat ... move it all to EBM where it works correctly. Get rid of it"): FAT draws no far tree any more. Removed: FarVegImpostors (its drawing, billboard bakes, GPU resident instances), its 9 [Voxel Terrain - Performance] Impostor* keys, the [Shaders] Far billboard / Far billboard GPU shader keys, the fatimpostorgpu bundle and its load, the streamer tick, the VoxelWorld Invalidate calls, and FatHaloFarTrees (old halo ring silhouettes from those bakes). Kept: the far tree PLACEMENT, moved unchanged into FarTreePlacement for [Far Forest], which feeds EBM's impostor engine (EbmApi.FarForest*), so EBM still draws the far trees. Saved cfg lines for the removed keys stay, unread.
        // was 0.2.1479:   // 0.2.1479 ([worldgen], lead 11-18, Fire: ring work may begin; RING SAFETY rule "any patches that only apply to ringworld need to only run when we're actually connected to a ringworld"): the 13 static FatHalo* [HarmonyPatch] classes (GetBiome postfix, StaticPhysics.SUpdate, 4 Minimap, SpawnSystem x3 incl. the Spawn transpiler, CreatureSpawner.Spawn, Character.InInterior, Game.FindSpawnPoint, ZNet.OnDestroy) are now DYNAMIC (Modules\Ring\FatRingPatches: applied on joining a ring world, removed per method on leaving or ZNet.OnDestroy); FatHaloArc.Tick returns first thing off a ring world (no RPC registration, ring file read or server ask); ring commands refuse off a ring world. Also: ring world profile flag (FiresWorldProfile.Ring, handshake v7 only on a ring world, -fat-new-ringtire); [FarShade] logs "on" again after a stand-down; test-only Water Test Place "fall x z width [facing]" (pit + FAT Vfall); one Info line per chunk fall record (N falls vs EMPTY).
        // was 0.2.1478:   // 0.2.1478 ([worldgen], lead 11-18, Fire: "If any of the mods functions rely on a shader provided by the mod to work, then they should be gated by that shader being toggled on"): new [Shaders] section, one key per FAT shader (default true; saved values never rewritten). Far ring, far billboards, far billboard GPU, far sun shade, inland water, poured liquid, sky rainbow, waterfall mist (and inland steam), waterfall curtains stand down when their key is off or the shader did not load (no stock fallbacks), one Info line per change; every dependent key says "Needs [Shaders] X = true"; voxel keys say FAT's voxel shader is required and always on (ToolsAUDIT_shader_gates_FAT_20261118.md).
        // was 0.2.1477:   // 0.2.1477 ([worldgen], lead overnight 11-18: Fire's queued "tune our other shader", _work\PLAN_fat_inlandwater_vanilla_tune_20261118.md steps 1 + phase 1): (1) Info dump at load of every value vanilla's sea pass reads, from the pristine zone water material (colours, depth fade, shore / foam depth, normal scale / power, glossiness, metallic, refraction scale / max, wave vel, tess, textures + scale, keywords). (2) Key [FiresInlandWater 0 Vanilla match] "Vanilla-tuned look" (default OFF, live) + "Vanilla-tuned depth value" (0.25): FAT's OWN shader with material values taken from vanilla's at runtime (one colour = lerp(_ColorBottomShallow, _ColorBottom, depth value) for shallow and deep, clear depth least-squares fitted to sqrt(d / _DepthFade), absorption 0, opacity 1/1, murk 0, refraction = min(_RefractionScale x 5, _RefractionMax), normal tiling 0.2 x / 1 x _NormalScale, strength _NormalPower, proc ripple 0, ripple breathing off, fresnel 5, reflection 1 untinted, smoothness _Glossiness, foam colour); class / biome tables zeroed while on; flow, chips, rapids, eddy, sea fade, ice / lava untouched. No shader change; the cfg is never written. // was 0.2.1476 ([worldgen], lead: CLEAN build after proof join 4 - dedi self-test 15/23, every record column hit, carved column y 46 under base 50): 1473 behaviour with 1474's real version tags; the 1475 self-test and caller-stack lines REMOVED; kept at Debug: ground census (with sample positions), decode line (now with its own ms), first column hit / miss per zone, anchor dump (max 6, on a peer-zone change). NEW: on a dedi every voxel record is decoded ahead, one per server ground read (PrewarmOne), so first decodes (6.3 ms seen once in join 4) do not land inside a FiresGodComplex row read; "dedi pre-decode" / "pre-decode done" lines at Debug. // was 0.2.1475 ([worldgen], lead proof join 3: records found, server reads all near the origin, FGC path unexercised): Debug proof only, no behaviour change. One-shot dedi self-test at the first anchor dump that finds records: FatApi.TryGroundSurfaceY on 5 columns per record zone (2 record chunk centres, zone centre, +-16 m), hit / miss, y, base ground, record column, us. The first two dedi ground reads in zones without records log their managed caller stack. // was 0.2.1474 ([worldgen], lead proof join 2: 0 records cached, every dedi ground read "in zones without records", no decode lines): Debug proof only, no behaviour change. The dedi ground census carries sample positions (first / last no-record read, last read in a record zone) and the real version; a dump (at a census when the peers' zones changed, max 6) lists every VAVoxel anchor the dedi holds (count, zones), the 3x3 zones round each peer's reference position and zone -36,13: per anchor key, position, the zone its key maps to, edit bytes, ver, rev; per zone ZoneHasEdits and the streamer's IsZoneKnown (FatApi.IsFatZone answers yes on either). // was 0.2.1473 ([worldgen], lead yes / [generator]: dedi voxel read cost, same answers): FatApi.TryGroundSurfaceY on a dedi with no live chunks asks DediVoxelRead.ColumnHasRecords first (chunk columns holding a record, per zone sector, one sector walk per 2 s) and returns false there with no ground work; where a record exists the base ground (TryBaseGround, WorldGenerator.GetHeight on SeamFlat) is computed once and passed into TrySurface (1472 ran it twice). The dedi GetGroundHeight read does the same. // was 0.2.1472 ([worldgen], lead: the 1470 dedi census read 0/4225 voxel columns in zones holding edit records): code-proven cause, the base ground came from VoxelZoneStreamer.TryGroundHeight (no Heightmap on a dedi; HeightmapBuildCache false while empty, filled only with caves / sky islands on) else FiresWorldGenBase.TryGetGenGround (null without the Fires generator), so on SeamFlat1462 (Vanilla generation, caves and sky islands off) FatApi.TryGroundSurfaceY and DediVoxelRead returned false before any record was read. New DediVoxelRead.TryBaseGround adds WorldGenerator.GetHeight (what HeightmapBuilder builds base heights from) as the last fallback; used by FatApi.TryGroundSurfaceY on a dedi, the 1471 GetGroundHeight read and the column read. Debug: one line per record decode (chunk, zone, bytes, rev, edited samples, edited y range) and per zone the first column hit y or first miss reason. ALSO ([generator] Q3): on a dedi FatApi.WaterSurfacesRevision now bumps when a placed water body appears, changes or goes in a sector already read (WaterMeshCombinerAnchor.DediSweep from WaterLookHost.Tick, every 2 s). // was 0.2.1471 ([worldgen], lead: the water half of the dedi gap): FatApi.TryWaterBodyAt answers tool-placed ponds / rivers on a dedicated server, where no WaterMeshCombiner exists (no GameObjects where the players are): WaterMeshCombinerAnchor.DediTryWaterBodyAt reads the bodies' anchor ZDOs (va_is_water_combiner_anchor + va_water_disks via ReadZdoDisks) READ-ONLY per zone sector and its 8 neighbours, decoded once per ZDO data revision (re-checked every 2 s), fresh water only; smallest body covering the point, surface = its highest covering disk. ALSO 0.2.1471 ([lead]: the server's own decisions ignored the voxel edits): on a dedicated server, ZoneSystem.GetGroundHeight (FAT's one hook, VoxelGroundHeight) in a zone holding voxel edit records answers from DediVoxelRead (read-only, cached), every world mode; zones without records untouched; main thread only; Debug census every 30 s (calls, voxel answers, no-record zones, off-thread, ms). Key [Voxel Terrain - World] Dedicated server reads voxel ground (default ON, live). // was 0.2.1470 ([worldgen], lead / [generator]: FiresGodComplex on the dedi logged "voxel 0/4225, FAT zone no, heightmap none" for zones that are voxel on the client): a dedicated server holds no GameObjects where the players are (VoxelAnchorPrefab.FindZdo's note), so no chunk anchor ever applied its edits there and VoxelWorld stayed empty. New DediVoxelRead (VoxelWorld.cs) reads the chunk anchor ZDOs (vv_edits) READ-ONLY on demand, decoded once per record revision (re-checked every 2 s, cache capped), and clamps their edit bounds over the column's base density (ground from VoxelZoneStreamer.TryGroundHeight, else the generator) to find the surface. FatApi.TryGroundSurfaceY falls back to it on a dedi; FatApi.IsFatZone answers on a dedi from "the zone holds edit records" (VoxelAnchorPrefab.AnyInSector). VoxelEditRevision bumps on a dedi when a changed anchor record arrives (VoxelAnchorGuard.Check) or a decoded record's revision changes. Not reproduced: generator shaping outside the edit bounds (caves, river-bed carves, cracks). StudioApiVersion stays 6. // was 0.2.1469 ([worldgen], Fire on 1468: "the fucking problem isnt the preview its exactly what i said, that it only raycasts when looking at ground making it fucking useless to place in a river"): FAT's water sheets have no collider and its swim triggers sit on the "Water" layer, outside the pointer ray's mask, so the Vfall cursor went through every FAT river / lake to the bed. ToolFalls.AimAtWater lifts the cursor onto the FAT / sea water surface along the camera ray (FatWaterChecks.TryWaterAt, includeSea); the fall's TOP is now where the cursor points (ground or water), no edge search, no snap, nothing automatic (Fire: "we select the size and the position and it places where we select"); the top stays exactly at the cursor; facing = wheel, width = Shift+wheel, straight down to the ground or water below; a top on water starts at the surface and its spillway notch goes into the bed under it (the fall rides that chunk's anchor). The one limit left is the curtain's own 2 m minimum, told TopLeft. Program callers (ConnectChannel, admintools test) keep the edge-walk plan. ALSO 0.2.1469 ([lead] for FiresGodComplex's Studio 3D view on a dedi): FatApi.TryGroundSurfaceY(x, z, out y), add-only: FAT's voxel ground surface (VoxelGroundTruth.Sample's logic: base ground from the heightmap, else the zone's heightmap build data (dedicated servers), else the generator; then VoxelWorld.TrySampleSurfaceY in [h-200, h+24], no physics); false where the column has no voxel data. FatApi.VoxelEditRevision (global) + VoxelEditRevisionAt(Vector2s zone): bumped whenever voxel edits land on this peer (VoxelChunk.ApplyShape / ApplyDensityStamp / ApplyMaskedStamp / ApplyEditBounds when changed, ImportBrushEdits for synced / anchor / undo edits), any thread, no allocation. StudioApiVersion 6. // was 0.2.1468 ([worldgen], Fire on 1467: "this fucking tool is useless if it only ever shows up when raycasting at fucking ground"): the Vfall cursor ray already took water and empty air (TileBrush.TryGetPointerTarget: WaterVolume layer, player-plane fallback); what refused was the plan, whose lip walk started AT the cursor and walked forward. Now, when the cursor's own plan fails (cursor on water, the low bank or past the edge), the lip is found BEHIND the cursor (walking back against the facing to the first column standing 2 m above the cursor's ground) and the normal plan runs from there, for the ghost and the click alike; the refusal ring sits on the water surface, and the reason is told TopLeft (once per reason, 2 s); the ghost material logs which property took its alpha. // was 0.2.1467 ([worldgen], Fire 11-18: "our waterfall tool sucks really bad ... it should show a preview waterfall, scroll wheel should ROTATE the waterfall and shift scroll should scale it, and then obviously it falls until it hits land or water"): Vfall's facing is its own state turned by the wheel (15 deg a notch, starting at the camera heading when Vfall is picked up), Shift+wheel = width; the preview (the real curtain, 75% opaque, was 45%) and the click use the same facing and plan, so the click lays what the preview shows; the big cylinder brush ring and spokes are gone for Vfall; Vfall-tool curtains hang STRAIGHT down from the lip to the landing the click measured (ground or water) instead of hugging the terrain profile, which laid them flat over gentle banks as white slabs (falls format 2 = + Straight per fall; format 1 still read; river falls keep the profile). Hints and tooltip updated. // was 0.2.1466 ([worldgen], lead GO PLAN A 11-17 after Fire's 1465 hand test "if you think these two waters are the same youre insane"; Tools\FAT_WATER_VANILLA_MATERIAL_PLAN_20261117.md): [FiresInlandWater 0 Vanilla match] Vanilla-match look (default OFF, live) now draws FAT's rivers / lakes / placed water with vanilla's OWN water material: a copy of the zone prefab's Custom/Water material taken at ZoneSystem.Awake (before any re-shader), as FAT's own provider on its water look host (priority 100; EBM 300 / Vedr 200 still win while Hand Off To Look Mods is on; the hand-off key no longer gates FAT's own provider). FAT feeds what WaterVolume feeds vanilla's material: _WaterTime every frame (ZNet wrapped day time), _depth[4] per renderer (new key Vanilla wave depth, default 0 = calm), _UseGlobalWind 1, _IsLod 0; constant tangents on FAT's water meshes while it draws (vanilla's pass reads TANGENT; FAT's meshes had none). The 1462-1465 overrides of FAT's own shader (vanilla bottom colours as surface colours, opacity 1, tight ripples) are removed. Rivers show no current under vanilla's material. ALSO 0.2.1466 ([worldgen], Fire 11-17 SeamFlat with Vanilla Border Stitch on: "the seam is closed, but theres still a square of mesh missing from the corners"): at a zone corner where three voxel zones meet one vanilla zone, the corner column's Y-edge belongs to the diagonal voxel chunk, whose stitch pulls its corner cell (in THIS zone) onto the border, and both orphan loops stopped one short, so a 0.5 x 0.5 m square was drawn by nobody; a stitched orphan face now also emits that corner Y-edge from its own (already stitched) cells (VoxelMesherSurfaceNets.Build). // was 0.2.1465 ([worldgen], lead GO 11-17 after water1464: the pristine capture at ZNetScene.Awake found nothing, the zone water is no ZNetScene prefab): vanilla-match reads vanilla's zone water colours from ZoneSystem.m_zonePrefab's WaterVolume material in a ZoneSystem.Awake postfix (Priority.First, before any zone water enables and before EBM's first swap); logs only + colour source, the EQUAL / DIFFERENT proof line unchanged. // was 0.2.1464 ([worldgen], lead water1463 leg: the dedi refused "not loaded: no ground at (240,40)" and the colours came from EBM's swapped material): Water Test Place on a dedi CLIENT places the test water on that client through the admin tool path (Lake carve = WaterShapingTools.StudioPond, Vriver click carve = WaterToolDriver.StudioRiver; ground from the client's colliders; the server authorises the water disks as for any admin tool), the 1463 RPC is removed; vanilla-match captures vanilla's water colours from the prefab water materials in a ZNetScene.Awake postfix at Priority.First, before EBM's Priority.Last swap, and logs them next to the live material's values (EQUAL / DIFFERENT). // was 0.2.1463 ([worldgen], lead water1462 leg VOID: the test placement was refused on a dedi client, and the vanilla colours were read from EBM's re-shadered sea material): Water Test Place on a dedi client now goes to the SERVER over a routed RPC (FiresAdminTerrain_TestWaterPlace; the server checks AdminCheck and places); vanilla-match reads vanilla's colours from the zone prefab's own material on Custom/Water first, then a live Custom/Water material, and only then a re-shadered one (logged as such, with the shader name). // was 0.2.1462 ([worldgen], lead overnight 11-17, Fire: "re tune its current custom water shader ... it looks fake right now"; audit ToolsFAT_INLANDWATER_LOOK_AUDIT_20261117.md C): NEW [FiresInlandWater 0 Vanilla match] Vanilla-match look (default OFF, live) for FAT's inland water when EBM / Vedr are not drawing it: vanilla's own water colours read at runtime (_ColorBottomShallow / _ColorBottom, logged), class and biome tints / looks zeroed (one depth model), opacity 1/1 + clear depth 3 (depth absorption owns clarity: no doubled bed, never clear straight down), no turbidity / churn, reflection 0.57 untinted, ripples 8.3 / 3.2 m with no breathing, swell 1.5 cm, foam collar 0.12; _FiresSeaLevelY set to the sea level only while nobody else writes it. Material values only: the shader is unchanged (it sits in Core's bundle, a 9:30 move item). TEST-ONLY [Voxel Terrain - World] Water Test Place ("pond x z r depth" / "river x1 z1 x2 z2 width depth" set live = FatApi.WaterPlacePond / WaterPlaceRiver there, for unattended water look legs). // was 0.2.1461 ([worldgen], lead GO 11-17 after farveg1459 showed "Vanilla Species And Density" 2-4x denser - stays OFF): NEW [Far Forest] Vanilla Species Mix (default OFF until its A/B leg; live): the tree / no-tree decision per 8 m cell is the old one (old density, one tree per cell), but WHICH species stands there is a weighted pick across every entry passing its gates (altitude, tilt, forest band), weight = vanilla per-zone share, entries of the same prefab merged so duplicates do not stack. "Vanilla Species And Density" wins if both are on. // was 0.2.1460 ([worldgen], lead GO 11-17 after stitch1459 proved 8/8 on the rig: 8 faces stitched, 0 overhang, 0 coplanar, on-border worst |dy| 0.000 m): [Voxel Terrain - World] Vanilla Border Stitch defaults ON for new cfgs; a cfg that already saved a value keeps it (BepInEx applies a Bind default only where the key is absent). No other change. // was 0.2.1459 [ghost] (rivers diff, merged by [worldgen]): R1 Vriver ghost path while a chain stroke is held (dry run of RiverBedProfile: bed, banks, water level, "waterfall here: N m", "cuts N m into the ground", end label via FatApi.TryWaterBodyAt / WaterClassAt, red "blocked" for no-build / wards; R2: a see-through strip where the water will lie and orange where banks will be built up, "builds the banks up N m"; R4: red uphill-end warning; draws only, edits nothing); help panel water lines fixed (dump path, auto-water status / generate / undo), va_water_dump text names the cache folder it really writes to; R3 ghost shows "digs about N m3, fills about N m3" and "clears N plant(s) / tree(s)" (read-only mirror of ClearCarvedVegetation); R11 water edits are acked to the sender (server authorises FIRST, then parses; refusals show their reason; no ack in 10 s = one disk resync per 64 m cell per 10 s); vegetation a Vriver carve clears is now in the stroke's undo / redo (FatUndoVeg); Vfall landing pool behind [Voxel Terrain - Tools] Vfall digs a landing pool (default OFF; menu text no longer promises a pool otherwise); va_auto_water_undo: va_auto_water_generate backs up every water anchor (payload keys only, file written off the main thread) before its wipe, and the undo recreates them through the anchor path (not on Numpad 7). // was 0.2.1458 ([worldgen], lead decision B 11-17, Fire confirms 9:30; ToolsFARVEG_SPECIES_20261117.md: the spawn mountain drew Pine 71 vs 7 real and Fir 0 vs 7 real): NEW [Far Forest] Vanilla Species And Density (default OFF until the A/B leg; live, far tiles + bridge cells rebuild). On: every far species is placed on its own roll at the game's own per-zone density (vanilla seeds each vegetation entry separately), with no DensityMul / BiomeCoverage look boosts and no "first species wins" - the old BuildTile used ONE roll per 8 m cell and the tallest-common-first order, so Pine took every spot it shared with Fir. Off = the old look, unchanged. ALSO FIX (stitch1457 leg: 6 of 8 faces, the zone's +X/+Z corner chunk never stitched): VoxelMeshPipeline.ComputeFrontierMask mapped a neighbour chunk column to its zone as FloorDiv(k, 2), one column off VoxelZoneStreamer.ZoneOfChunk's FloorDiv(k + 1, 2), so that chunk saw its vanilla neighbour as its own voxel zone (covered, no frontier bit); fixed, and Vanilla Border Stitch now judges every face by its own zone test (no longer via the frontier mask). // was 0.2.1457 ([worldgen], lead 11-17, stitch1454 leg: the edit chunk landed after the 3 s audit): flipping [Vanilla Border Stitch] also audits the vanilla borders 20 s after the flip, when every border remesh has landed. No other change. // was 0.2.1456 [ghost] (Voxheim diff, merged by [worldgen]): Vlevel Left Alt is a true smooth (slopes kept, strength key); Vcube turns 15 deg on Q/E; Vramp slope steps (Numpad *); undo/redo keep terrain someone changed since; va_voxel_* brushes are undo steps; va_voxel_seamdump OBJ dump; fix: soft brush reach (seam/undo) since 1454. // was 0.2.1455 ([worldgen], lead overnight 11-17, Fire: "re target our custom water to use the ebm water or vedr water ..."): water look hand-off, FAT hosts it (design Tools\DESIGN_fat_water_handoff_20261117.md 3.1b, frozen with [visual] + [seasons]). FatApi add-only: WaterLookApiVersion 1, RegisterWaterLook(owner, priority, active, build, applyBody, release) / UnregisterWaterLook / RefreshWaterLook, WaterSurfacesRevision + WaterSurfacesChanged, TryWaterBodyAt(p, out surfaceY, out size, out kind) from FAT data (tool-water bodies first, then generated water above the sea; dedicated-server safe), types FatWaterKind / FatWaterShape / FatWaterLookRequest. New CustomWater\Core\WaterLookHost.cs: providers sorted by priority (EBM 300, Vedr 200), the top live one (active() true; a throwing provider is dropped with one Warning) re-checked on register / unregister / refresh and every 1 s; FAT's own renderers only (generated sheets from ApplyGenWater / view destroy, placed sheets from FinishBuild / Drop, combiner bodies from SetupMaterial / liquid type change / OnDestroy, never a scene search) wear the provider's material (cached per FAT material, null = not yet), applyBody per renderer, every original back and release() once per material on a change or withdraw. Never offered: lava / tar / ice / snow materials, anything under a ZNetView, waterfalls, non-water bodies, sheets whose state holds ice / lava / crust (WaterStateField.HoldsIceLavaCrust, re-checked when the state refills; Bind tests FAT's own material while a look mod draws). FAT's re-assignments (PlacedWaterSheets.MeshFor, combiner SetupMaterial) leave a held renderer alone; va_voxelwater_mat re-adopts. NEW [Custom Water] Hand Off To Look Mods (default ON, live; off = FAT's own shader always). Info line per provider change, Debug census every 30 s. No provider registered = 0.2.1454 behaviour. ALSO 0.2.1455 ([worldgen], lead GO 11-17, report only for Fire's dense-vs-true far-tree choice): FarBridge logs, at Debug, real vs placement tree counts by species for each zone the sparse guard holds at placement (first 24 per session), and the console command fat_farveg_compare [radius] prints the same for every loaded zone around the player (plus Info lines for a leg log). No behaviour change. // was 0.2.1454 ([worldgen], lead overnight 11-17, Fire: "on our va flat world ... the zone borders between voxel and vanilla heightmap do not agree and they are slightly off, causing tearing at the borders"): toward a vanilla heightmap zone the voxel skin overhung the border by half a cell (-X/-Z: cell -1 vertices 0.5 m inside the neighbour, flat and coplanar with its ground = z-fight on flat ground; +X/+Z: orphan stand-ins 0.5 m past it, tilted 15 deg = a crease). NEW [Voxel Terrain - World] Vanilla Border Stitch (default OFF until a leg proves it, live): those border vertices move onto the border at the neighbour heightmap's own edge heights (same topology, no overlap, no gap); faces with caves / overhangs at the border keep the old edge; a zone swapping next to a stitched chunk waits (5 s cap) for that chunk to re-mesh, and the heal re-checks stitched faces. NEW va_voxel_seamaudit vanilla [radius]: overhang / coplanar (z-fight) / on-edge numbers per voxel-vanilla face. TEST-ONLY [Voxel Terrain - World] Seam Test Carve ("x z radius" set live = one carve there, for unattended border legs); the stitch flip logs the border audit at the flip and 3 s later. ALSO 0.2.1454 [ghost] (Voxheim diff, lead 11-17): server puts back non-admin voxel writes beyond hand-tool size or inside another player's ward (Server rejects non-admin voxel writes, default ON); Softness/Strength keys on Vcarve/Vfill/Vcube/Vsphere (default = the old hard edge); key hints for every V-tool and the help rebuilt from the tool list. // was 0.2.1453 ([worldgen], lead 11-17, [visual] proved from code + log on BlueHills HandTest: Fire's SPAWN mountain showed bare far terrain once he moved away): FarBridge's seen-trees read at spawn could snapshot a zone whose tree ZDOs were still arriving / uncreated, and that sparse snapshot replaced the placement for the session. A read now commits only for a complete zone (no spawn / teleport / active-area load in the last 5 s, and every creatable ZDO created - vanilla IsAreaReady's test - or two reads 10 s apart match; otherwise re-read each second, placement kept). The worker keeps a committed zone's real trees only if they reach half the placement there (or within 3 trees) or the zone proves removal (a stump / log of a bridge species from its TreeBase, or a drop between two committed reads of one load). Info line at bridge start, up to 8 Info lines for held zones, counters on the Debug bridge line. Dead commented FAP source-link block dropped from the csproj. // was 0.2.1452 ([worldgen], lead GO 11-16: VA-Chunk clients logged 28 "Placed 0/... altbiome", and Fire's client biome cache held 256 land points of 4,194,304): the zone gate's planning exemption was per thread, and FiresFixes' Parallel Biome Points builds AltBiomeWorldData's grid on ThreadPool workers inside VerifyBiomeData, so on a zone-mode CLIENT (view from the handshake) every closed point read Ocean / sea floor and FF cached that grid. New grid-build scope (ZoneModeGate.EnterGridBuild/ExitGridBuild, an AsyncLocal that ThreadPool / Task / Parallel carry into the work queued inside the build, plus a global count for a one-read hot path): the build's own threads read the real world; threads already running (the pinned HeightmapBuilder, voxel / far-tile workers) stay gated. VerifyBiomeData's prefix takes Priority.First so the server's synced grid is taken before any other prefix builds one. One Info proof line per build on a zone-mode world. Server path unchanged (no view during its grid build). // was 0.2.1451 ([worldgen], lead 11-16, Fire: the world clone must work properly to ship; VA DN -> VA-Chunk dropped Fire's painted biomes, hand-removed water and ice): FiresWorldClone carries every per-world record that describes the world - _biomepaint.bin and _regenqueue.txt (both keyed to the world uid, rewritten to the copy's: BiomePaint.RetargetRecord / RetargetRegenQueue), _water_removed.bin, _ice.txt, .halo, .halozones, _locationlevel.txt, _locreplan.txt, and _sinkfalls.txt with the bake - never overwriting; every record still left behind is listed with its reason beside CarriedRecordSuffixes. FIX (Fire 11-16 "clicking convert does nothing", SHIP BLOCKER): the world list's "Convert..." row was built DISABLED for any world already carrying FAT's terrain record (BcWorldData.Exists; Verdant's Ascent DN has firesgen_<world>.bcdata from its own conversion), and Core's menu ignores clicks on a disabled row without drawing its tooltip, so the click did nothing and logged nothing. The row, the dialog and BcConvert.TryConvert no longer refuse a converted world: its copy keeps FAT's record (records-folder form by CarryRecords, in-folder form by the new BcWorldData.TryCopyRecord) and TryConvert fails loudly if the copy lacks it. Open logs its entry, build success and any build failure. // was 0.2.1450 ([worldgen], lead 11-16, Fire: the » Biome pill covered vanilla's biome / zone / coordinates readout): FatBiomeMapTool.PlacePill docks to LiveMap 0.1.8+'s measured anchor (LiveMapApi.DockAnchor, by name, cached delegate; null on any miss or throw -> vanilla m_biomeNameLarge as before). Territory-first stacking unchanged. // was 0.2.1449 ([worldgen], lead ruling 11-16): log wording only. RiverSync's server warning for 0 recorded river passes names the real cause (another mod restored the rivers without vanilla's search, i.e. FiresFixes' River Cache; FF 0.1.168 stands it down on a dedicated server when RenderRivers carries another owner's patch), and the client line says "the server had no river lists to send" instead of blaming an old FAT. No logic change. // was 0.2.1448 ([worldgen], lead / [visual] spec, EBM 1.2.422 + FTS 0.3.402 read it): _FiresIceCover channel B = ice proximity (fixed-ice cover R blurred over ~32 m, tent of two box passes on the 2 m grid, zero outside the rect, border ring 0, linear), texture RGBA8, global _FiresIceProxOn 1 while valid (0 on Off); FatApi.TryIceProxAt(x, z, out prox01) any-thread snapshot like TryCalmAt. VANILLA WATER (Fire: "fat needs the code for its own custom water, but vanilla oceans and water will be handled by vedr or vanilla or any other mod hooking them"): removed the WaterVolume.UpdateWaterTime / CalcWave / Depth patches (FAT clock writes into vanilla statics, wave function, sea wind, wave multiplier, calm scaling, depth match + probe RPC, wavematch_test), FiresWaves (WaveIntensity / WaveWeatherMap, _FiresWaveMult / _FiresSeaState, RPC; FatApi wave members are no-ops), FiresSeaWind and the Ship.Awake FX guard; FAT keeps a private clock and writes the _WaterTime global only while no Vedr water clock owns it; the GetWaterSurface amplitude postfix is gated to FAT liquids (_FiresLiquidGrid 1); the SetLiquidLevel cave gate counts voxel ground only; raven / land-spawn / red-ghost / fish checks read FAT water only. // was 0.2.1447 ([worldgen], lead GO 11-16, Fire's ice rule physics half): FatApi.TryCalmAt(x, z, out calm) + CalmRevision (any thread, immutable snapshot of _FiresIceCover.g, smoothstep 0.1-0.9; client only) and FAT's CalcWave scales waves by (1 - calm) while FAT owns waves, so boats / swimmers in ice-enclosed open water do not bob on undrawn waves. // was 0.2.1446 ([worldgen], Fire 11-16 "no snow on the ground" with EBM far tiles on): FIX FatApi.TryFarColor (1444) called Heightmap.GetBiomeColor(BiomeSector) / (Biome) from EBM's builder thread; Vedr patches those overloads with a plain static paint-depth counter (LandscapeSeasons.cs:369-396, ++/-- not atomic), so the race left it off by one and every main-thread chunk paint drew summer colours. TryFarColor + FatSeasons.WinterizeAnyThread now use FatFarData.SplatOf / SectorSplat (vanilla's own colour table, pure, unpatched): never a patched vanilla call off the main thread; FatApi.FarDataApiVersion 2 (= these calls are thread-safe; EBM 1.2.420 uses TryFarColor on its thread only at >= 2). ICE (Fire's spec, [seasons] + [visual] agreed): Vedr sea floes no longer join _FiresIceCover (they ride the real tide + waves; the cover holds fixed ice only); _FiresIceCover is RG8: R unchanged, G = calm (open water a border-seeded flood through open water cannot reach = enclosed by ice R >= 0.5 and / or land; water shaders scale tide + waves by 1 - max(R, G)); land = ground over sea + 1 m and not FAT generated water. // was 0.2.1445 ([worldgen], lead 11-16: FiresVoxelHeightmap far look): the embedded fatvoxelshader bundle (F:ModArchiveFatShaderLinear, FatVoxelShaderBundleBuilder, LINEAR, now D3D11 + Vulkan per the 11-11 project setting) honours the GLOBAL _FiresFarLook set by EBM [Far] FarLook - the same semantics as EBM 1.2.418 / Vedr 0.8.262: no distant albedo/smoothness matte [D8177-8180] and no 0.2 grey distant snow [D8184-8190], detail layers past 400 m (f944), mipped array taps by screen footprint; distant normal flatten kept; off / unset = the same pixels. _FiresShaderRev 1445. // was 0.2.1444 ([worldgen], lead 11-16: all far drawing lives in EBM, FAT supplies data only): FatApi far-terrain DATA for EBM, safe from any thread - TryFarHeight (vanilla near-zone corner method), TryFarEditDelta + FarEditRevision (DistantHeightCache, published copy every 0.5 s after a change), TryFarColor (sector colour, biome paint, Vedr winter) + FarWinterRevision, FarFiresWorld, FarDataApiVersion 1. The 1442 ground underlay is NOT in this build (parked); its two fixes are. // was 0.2.1442 ([worldgen], Fire 11-16: "If we can render the trees at that distance we can render the ground"): Modules\VoxelTerrain\FarGroundUnderlay.cs, one more vanilla distant-LOD heightmap (64 x 128 m, about +-4 km), built on vanilla's HeightmapBuilder thread, 4 m lower, its _LodHideDistance following the real coverage of the finished vanilla + FAT far tiles, so far trees / rocks always have ground under them (arrival, teleport, fast flight). Carries edits (1440) and winter (1441). Key [Far Terrain] GroundUnderlay (default true; off = no object, no work; live). FIX: 1440/1441 refreshed far tiles from Heightmap.GetAllHeightmaps(), which is vanilla's NEAR list (the IsDistantLod setter removes distant tiles), so far-tile refreshes after edits, the key toggles and the season rebuild found nothing; now DistantHeightCache.DistantTiles() walks Heightmap.Instances. FIX: the [FiresVoxel] drawn-heightmaps Debug line labels ZoneSystem's own zone-loading distance and prints ZNet's synced one beside it. // 0.2.1441 ([worldgen], Fire 11-16 frozen lake x -1338 z 726: far terrain dark / black in winter while near ground and far trees are white): vanilla distant-LOD tiles take worldgen biome colours only and Vedr's ground snow skips every distant-LOD heightmap, so nothing put winter on them. FatSeasons.WinterizeDistantLod (FAT's RebuildRenderMesh hook, Priority.Last - 10) runs the same Winterize the voxel ground and FarQuilt tiles use on each distant tile's vertex colours; season change / key flip rebuilds the distant tiles once. Key [Far Terrain] WinterSnow (default true; off = vanilla summer colour far away; live). // 0.2.1440 ([worldgen], Fire 11-16: "build the distant terrain cache with a config toggle to cleanly turn it completely off"): Modules\CustomTerrain\Paint\DistantHeightCache.cs. Visited zones' terrain edits (final height minus worldgen, cm) are captured in the one Heightmap.ApplyModifiers postfix while the zone's TerrainComp is loaded, persisted per world (BepInEx\cache\FiresAdminTerrain\DistantHeights\<world>.fdh), and added to distant-LOD tiles (vanilla TerrainLod, FarTerrainVanilla, FarQuilt) as the mean over each vertex's footprint, so leveled builds stop sinking into the far terrain. Far tiles over a changed zone rebuild once it has left the near ring. Key [DistantHeightCache] Enabled (build default true for testing; OFF = vanilla, nothing runs; live toggle). // 0.2.1439 ([generator], [2ic] audit A1 / [lead] 11-16): FiresWaves' 10 s sea-shader check walks vanilla's WaterVolume.Instances instead of scanning the whole scene (Object.FindObjectsByType<WaterVolume>, the 42-51 ms Game.Update hitch); same timer, same behaviour. FiresWaterAuthority's Vedr lookup is silent (FatSoftTypes.Find, was AccessTools.TypeByName every 5 s) and stops once a world has loaded without Vedr. | 0.2.1438 ([generator], Fire 11-16: water code leaves Core for Advanced Player Movement): the cave-roof share (VoxelCaveEnv) and the breath head-under read (OceanKit) look up APM's FiresAPM.Swimming.PlayerSwim first (Core's only as a fallback); soft dependency on APM for load order. | was 0.2.1437: 0.2.1437 ([ghost], lead 11-13 LoopTimers row "ZoneMode_ZoneSystem_Update.Postfix 18.89 ms/s"): deep-cell cache cap eviction keeps live-job cells, floats the cap (hard 600k), never invalidates tints; 10 s probe line.   // 0.2.1436 ([ghost], lead 11-13 profiler: PlacedWaterField.Canonical 14.9 MB/min at the shore): the bed question no longer builds a closure + delegate per call (per-thread args, one cached delegate); IceField.TryIceAt updates its hold in place (a ConcurrentDictionary set allocated a node per call, 9.8 MB/min); deep-cell queue pre-sized (EnqueueCell growth 4 MB); Debug-only allocation probe on the generated water lookup (GenCacheOn / cache / SurfaceAt bytes per 10 s). // 0.2.1435 ([ghost], lead 11-13 profiler: BiomePaint tint 33.9 MB/min at the dock): tint pump allocation-free per step (pre-sized cell dictionaries, pooled cell ranges, colour and edge arrays reused per heightmap / zone, static sort comparer, key-parts string only with Debug). // 0.2.1434 ([ghost], lead 11-13, Fire's TAA teal wash): instanced far-veg + clutter draws use motionVectorMode = Camera (out of Unity's per-object motion-vector pass). 0.2.1433 ([ghost], lead 11-12, Fire run_6 bare mountain): FarBridge binds EBM's public ImpostorEngine (reflection, by presence) when Core's is gone; bridge line names the engine.
        // The oldest FAT whose server-synced config keys match this one's (ConfigSync's MinimumRequiredVersion). Raise it ONLY when a
        // synced key's name, type or meaning changes, never with every build: it forces every player and server to update together.
        internal const string SyncedConfigSince = "0.2.974";   // The synced "VoxelTerrain World" keys moved to "Voxel Terrain - World"

        public static readonly Harmony harmony = new Harmony(PluginGUID);
        public static FiresAdminTerrain Instance { get; private set; }

        public static AssetBundle assetBundle;
        public static GameObject RootObject;
        public static GameObject PrefabContainer { get; private set; }

        public ConfigSync configSync;
        private static ILogHandler _originalLogHandler;

        // BepInEx log source Ã¢â‚¬â€ banner emitters route through this (not Debug.Log)
        // so banner lines don't stdout-echo a raw white console duplicate.
        public static BepInEx.Logging.ManualLogSource Log;

        private static bool s_applicationQuitting;

        /// <summary>the game is quitting (Application.quitting has fired): teardown must not touch the scene's managers.</summary>
        internal static bool ApplicationQuitting => s_applicationQuitting;

        private void Awake()
        {
            VerdantsAscent.Terrain.Performance.FatLoadClock.Begin();   // 0.2.1498: FAT's plugin start, step by step
            Log = Logger;
            VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.s_wetMainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;   // PaintWet01 memo is main-thread only
            // No caches under BepInEx config: move FAT's old config-folder caches to BepInEx/cache/
            // FiresAdminTerrain once, before anything reads them (the water cache restore needs its .bin). Copy + verify, then the old
            // folder moves to BepInEx/cache/FiresAdminTerrain/_migrated_from_config_<date> as the backup; nothing is deleted.
            try
            {
                string cfg = BepInEx.Paths.ConfigPath;
                System.Action<string> info = m => Logger.LogInfo(m), warn = m => Logger.LogWarning(m);
                FiresCachePaths.Migrate(System.IO.Path.Combine(cfg, "FiresAdminTerrain"), FiresCachePaths.For("FiresAdminTerrain"), info, warn);
                FiresCachePaths.Migrate(System.IO.Path.Combine(cfg, "VerdantsAscent", "WaterCache"), FiresCachePaths.For("FiresAdminTerrain", "WaterCache"), info, warn);
                FiresCachePaths.Migrate(System.IO.Path.Combine(cfg, "FiresDistantPaint"), FiresCachePaths.For("FiresAdminTerrain", "DistantPaint"), info, warn);
                FiresCachePaths.Migrate(System.IO.Path.Combine(cfg, "BiomeData"), FiresCachePaths.For("FiresAdminTerrain", "BiomeData"), info, warn);
                Logger.LogInfo($"[FiresCache] FAT caches at {FiresCachePaths.For("FiresAdminTerrain")}");
                if (!Application.isBatchMode) VerdantsAscent.Terrain.Voxel.FatStatsSection.TryRegister();   // Hub Stats card (clients)
            }
            catch (Exception ex) { Logger.LogWarning($"[FiresCache] migration skipped: {ex.Message}"); }
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("cache migrate + stats card");
            // BIG floating-island banner Ã¢â‚¬â€ fires FIRST so it sits at
            // the top of the FAT console output as the load
            // announcement. The compact tree-island-waterfall banner
            // fires at the end of FinishInit to bookend the load with
            // a "ready" confirmation. See VAFatBanner for the two-
            // banner rationale + content.
            try { VerdantsAscent.Utilities.VAFatBanner.PrintBig(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"VAFatBanner.PrintBig failed: {ex.Message}");
            }

            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("big banner");
            FiresLogger.LogInfo($"Awake() entered - version {PluginVersion}");
            Instance = this;
            Application.quitting += () => s_applicationQuitting = true;
            VerdantsAscent.Terrain.Voxel.VoxelLayerPaint.NoteMainThread();   // Layered biome lookups answer on the main thread only

            // Declared before any world loads, so the offer is ready the moment a peer connects. (A studio bake goes through
            // FAT's own join sync, before PeerInfo: BcJoinSync.)
            try { VerdantsAscent.Terrain.Voxel.FiresSkyIslands.RegisterSync(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"Sky island set sync registration failed: {ex.Message} Ã¢â‚¬â€ clients will hold "
                                       + "islands off until their wait times out.");
            }
            try { VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.RegisterWearGround(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"Wear gate registration failed: {ex.Message} Ã¢â‚¬â€ pieces on voxel ground keep vanilla's wear timing.");
            }
            try { VerdantsAscent.Terrain.Voxel.FatSeasons.Register(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"Season hookup failed: {ex.Message}; FAT ground keeps its summer look under Vedr.");
            }
            try { VerdantsAscent.Terrain.Voxel.FiresWaterfalls.OfferShaders(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"Waterfall mist shader offer failed: {ex.Message}; other mods draw without it.");
            }

            InstallLogFilter();
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("sync + bridge registrations");

            RootObject = new GameObject("_FiresAdminTerrainRoot");
            UnityEngine.Object.DontDestroyOnLoad(RootObject);

            // Rendering drivers (water shader, sky, moon, planet, orbitals,
            // aurora, caustics, fog, underwater) moved to FiresTossinShade.
            // FAT keeps only the gameplay material provider (WaterMaterialManager),
            // which pulls the FiresWater wrap through FiresCore's WaterShaderBridge
            // when the shader mod is installed; otherwise it serves vanilla water.

            PrefabContainer = new GameObject("FiresAdminTerrain_Prefabs");
            PrefabContainer.transform.parent = RootObject.transform;
            PrefabContainer.SetActive(false);

            // 0.2.1499 ([lead] fat1498-pin-r1: "[FatLoad] plugin start 11617 ms: ... config binds + sync + player-facing 9484"): BepInEx 5.4.23.5
            // ConfigFile.Bind saves the WHOLE .cfg after every new entry while SaveOnConfigSet is true (its default; decompiled Bind: "if
            // (SaveOnConfigSet) Save();"), and so does every Value set (OnSettingChanged). FAT binds several hundred keys here, so the big file was
            // rewritten once per key. Saving is held off for this block and done ONCE at its end: the file written is the same (Save writes every
            // entry plus the orphans either way); FatConfigRetire's own explicit Save calls still run. The setting is put back in a finally.
            bool cfgSaveWas = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;
            try
            {
            ConfigManager.Instance.Initialize(Config);
            FatCoreSettingsOwned.Bind(Config);   // 0.2.1335: Core's snow / loot / undo keys, owned here
            VerdantsAscent.Terrain.Voxel.FatShaders.Bind(Config);   // 0.2.1478 [Shaders]: one key per FAT shader (Fire 11-18); features stand down when theirs is off
            VerdantsAscent.Water.Core.FiresLiquidConfig.Bind(Config);   // FiresLiquid look sliders
            VerdantsAscent.Water.Core.FiresInlandWaterConfig.Bind(Config);   // per-layer ripple controls for the inland sheet
            // 0.2.1482 (Fire 11-18, after EBM 1.2.456 proved its own far trees exact): FAT's [Far Forest] is REMOVED (FarForest, FarBridge, FarReplay, FarRng,
            // FarRngSelfTest, FarMatchTest, FarTreePlacement; archived in F:_archive_fat_farforest_20261118). FAT places, feeds and draws no far tree; EBM's
            // [Far Trees] places its own. Saved [Far Forest] lines stay in the cfg, unread. One Info line says so.
            FiresLogger.LogInfo("[FarForest] removed in 0.2.1482: FAT places and feeds no far trees (EBM places its own). Saved [Far Forest] keys are ignored.");
            VerdantsAscent.Terrain.Voxel.FarQuilt.BindFarTerrain(Config);   // 0.2.1414 FarLands #3 [Far Terrain], default off
            VerdantsAscent.Terrain.Voxel.FatFarShade.Bind(Config);   // 0.2.1416 FarLands #8 + #10 [Far Shadows] ([generator]), all keys default off
            // 0.2.1482: FarBridge.InitCoreFar removed with [Far Forest] (it initialized Core's old Far types, absent since EBM 1.2.210 owns them; it was a no-op).
            VerdantsAscent.Water.Core.InlandLakeProbe.Bind(Config);   // Lake reflection (Probe on by default)
            VerdantsAscent.Water.Core.InlandLookConfig.Bind(Config);   // Per class / per biome looks
            VerdantsAscent.Water.Core.InlandSteam.Bind(Config);        // Steam over warm still water (Plains, Ashlands)
            VerdantsAscent.Water.Core.FiresWaterAuthority.Bind(Config); // 0.2.1448: no keys (FAT no longer touches the sea; Vedr owns waves / tides / clock)
            VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.Bind(Config); // voxel P0 prototype toggles
            VerdantsAscent.Terrain.Voxel.FatSuperFlatGrid.Bind(Config);   // The super flat build grid
            StartCoroutine(VerdantsAscent.Terrain.Voxel.DediTextureTrim.Run());   // Headless dedi only, opt-in
            VerdantsAscent.Terrain.Zones.ZoneModeConfig.Bind(Config);
            VerdantsAscent.Terrain.Zones.FatLocationAccelerator.Bind(Config);
            VerdantsAscent.Terrain.Voxel.FiresCaveBiome.Bind(Config);   // Cave biome variety + cave environments
            VerdantsAscent.Terrain.Voxel.FatUndergroundLocations.Bind(Config);   // Underground locations
            VerdantsAscent.Terrain.Voxel.FatGrottos.Bind(Config);   // Grottos
            VerdantsAscent.Terrain.Voxel.FatCaveRivers.Bind(Config);   // Underground rivers
            VerdantsAscent.Terrain.Voxel.FatSinkFalls.Bind(Config);   // River waterfall sinkholes
            VerdantsAscent.Terrain.Zones.ZoneModeCoreBridge.Register();
            VerdantsAscent.Terrain.Paint.DistantPaintCache.Bind(Config); // paint on distant-LOD terrain
            VerdantsAscent.Terrain.Voxel.FatSeasons.BindDistantSnow(Config); // 0.2.1441: [Far Terrain] WinterSnow (Vedr winter on distant-LOD tiles)
            VerdantsAscent.Terrain.Paint.DistantHeightCache.Bind(Config); // 0.2.1440: terrain edits (heights) on distant-LOD terrain, [DistantHeightCache] Enabled
            VerdantsAscent.Terrain.Persistence.TileAnchorHeal.Bind(Config); // duplicate tile anchors: DryRun (default) | Apply
            VerdantsAscent.Terrain.Zones.OffMapZones.Bind(Config); // saved objects beyond the world edge: Off (default) | List | Remove
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("config binds (" + Config.Count + " entries, save held)");   // 0.2.1499
            FatConfigRetire.MoveSections(Config);                   // "VoxelTerrain X" sections -> "Voxel Terrain - X", values carried
            FatConfigRetire.Retire(Config);                        // The named dead keys leave the .cfg
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("config retire (moves + removals)");   // 0.2.1499

            ConfigManager.Instance.configPresetToLoad.SettingChanged += (_, __) => ApplySelectedPreset();
            ConfigManager.Instance.configPresetSaveNow.SettingChanged += (_, __) => SaveNamedPreset();
            ConfigManager.Instance.configServerPreset.SettingChanged += (_, __) => ApplyServerPreset();

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                // A FIXED floor, raised only when a synced key's name, type or meaning changes. An earlier version
                // renamed the synced [Voxel Terrain] section (was "VoxelTerrain (P0 prototype)"), so peers below it disagree on it.
                MinimumRequiredVersion = SyncedConfigSince
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            configSync.AddLockingConfigEntry(ConfigManager.Instance.configServerAuthority);
            configSync.AddConfigEntry(ConfigManager.Instance.configVerboseLogging);
            configSync.AddConfigEntry(ConfigManager.Instance.configServerPreset);
            configSync.AddConfigEntry(ConfigManager.Instance.configAdminOnlyTuning);
            configSync.AddConfigEntry(ConfigManager.Instance.configHeightCacheResolution);
            FatCoreSettingsOwned.SyncServerKeys(configSync);   // 0.2.1335: the four [Loot] keys, server-synced as in Core
            configSync.AddConfigEntry(ConfigManager.Instance.configHeightCacheSamplesPerFrame);
            configSync.AddConfigEntry(ConfigManager.Instance.configUseNewTilePersistence);
            VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.RegisterServerAuthority(configSync);
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("ServerSync entries");   // 0.2.1499
            }
            finally
            {
                // 0.2.1499: the one save, before the admin gate and the player-facing pass below change descriptions in memory (as before, the
                // file keeps the full descriptions those passes do not save); saving is back on for them and for every later Bind / Value set.
                Config.SaveOnConfigSet = cfgSaveWas;
                try { Config.Save(); }
                catch (Exception ex) { FiresLogger.LogWarning($"[Config] the one save after binding failed: {ex.Message}"); }
            }
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("config save (once)");   // 0.2.1499
            // 0.2.1338: AFTER every AddConfigEntry. The gate's first SetReadOnly makes FiresConfigVisibility cache each entry's tags; registered earlier, that cache had no
            // sync tag, MarkAdvanced below rebuilt the descriptions from it, and the server sent 0 FAT configs to every client.
            FiresCore.Config.FiresAdminConfigGate.Register(Config,
                () => ConfigManager.Instance.configAdminOnlyTuning.Value, ConfigManager.IsAdvancedTuning);
            // Player-facing config rules, applied after the last Bind (Ocean included). Basics only without
            // Show Advanced, one-line descriptions, built-in lists hidden. The admin gate above keeps IsAdvancedTuning.
            FiresCore.Config.FiresConfigVisibility.MarkAdvanced(Config, d => VerdantsAscent.Managers.FatPlayerFacingConfig.IsPlayerAdvanced(Config, d));
            VerdantsAscent.Managers.FatPlayerFacingConfig.Apply(Config);
            FatCoreSettingsOwned.LogSyncTags(configSync);   // 0.2.1338
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("admin gate + player-facing");   // 0.2.1499

            LoadAssets();
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("asset bundles");

            // Register the env-box terrain provider so Core's portable EnvironmentBoxController routes its
            // heightmap/clutter/snow work back into FAT's systems. Without this, Core env boxes still apply the
            // env/skybox atmosphere but do no terrain/biome paint (the dark-crypt case needs no terrain anyway).
            try { FiresCore.Bridge.EnvironmentBoxBridge.Register(new VerdantsAscent.Utilities.EnvironmentBoxTerrainProvider()); }
            catch (Exception ex) { FiresLogger.LogWarning($"env-box provider register failed: {ex.Message}"); }

            FiresCore.Materials.TerrainMaterialCache.MaterialCaptured += OnTerrainMaterialCaptured;

            // Tile-restore batch tracker Ã¢â‚¬â€ starts an infinite polling
            // coroutine that emits the Ã°Å¸ÂªÂ¨ TILES banner whenever a wave
            // of tile restores (TileAnchor.Load calls) has settled.
            // Per-tile success logs are verbose-only; the batch banner
            // is the default-visible summary. See VATileRestoreBatch
            // for the debounce model.
            StartCoroutine(VerdantsAscent.Utilities.VATileRestoreBatch.PollAndFlushCoroutine());
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("env-box + material hooks");

            if (ZNet.instance && ZNet.instance.IsDedicated())
                StartCoroutine(DelayedDedicatedServerInit());
            else
                FinishInit();
        }



        private void InstallLogFilter()
        {
            // FiresUnifiedCore installs the one shared rate-limit/suppression log handler now
            // (FiresCore.Logging.RateLimitedLogHandler). This per-mod copy was collapsed into Core so the handler
            // chain stays a single wrapper instead of one-per-mod Ã¢â‚¬â€ intentionally a no-op.
        }

        private IEnumerator DelayedDedicatedServerInit()
        {
            while (ZNetScene.instance == null || ObjectDB.instance == null)
                yield return null;
            yield return new WaitForEndOfFrame();
            FinishInit();
        }

        // Patch classes whose method bodies reference client-only Unity types
        // (Hud, Minimap, InventoryGui, FejdStartup, etc.). On a dedicated
        // server the Mono runtime crashes natively when Harmony's IL rewriter
        // tries to resolve those references. Same pattern as Ascend.cs in the
        // original FiresRPGmaker mod.
        private static readonly HashSet<string> _clientOnlyPatchTypes = new HashSet<string>
        {
            // Phase F: legacy DirtFloorClientPatches entry removed (the
            // DirtFloorPatches type is deleted). Add new client-only patch
            // type names here as we identify them on the dedi path.
        };

        private static bool IsClientOnlyPatch(Type type)
        {
            for (var t = type; t != null; t = t.DeclaringType)
            {
                if (_clientOnlyPatchTypes.Contains(t.FullName))
                    return true;
            }
            return false;
        }

        private void FinishInit()
        {
            // The dedicated server executable, not "no graphics device": the headless test bot is a client, so it gets the
            // menus and the ice, snow and biome-painter pipelines a player has, and reads what a player would
            // (admintools_test).
            bool isDedicatedServer = FiresCore.Lifecycle.FiresMod.IsDedicatedServer;
            FiresCore.Logging.StatusBanner.Register("FAT", VerdantsAscent.Terrain.FatStatus.Line);
            if (!isDedicatedServer) VerdantsAscent.Terrain.Voxel.FiresWorldMenu.Register();
            if (!isDedicatedServer) VerdantsAscent.Terrain.BcImport.BcWorldListMenu.Register();
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("menus");

            try
            {
                int applied = 0;
                int failed = 0;
                int skipped = 0;
                var assembly = Assembly.GetExecutingAssembly();

                foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
                {
                    if (isDedicatedServer && IsClientOnlyPatch(type))
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        // HarmonyX returns null for a type without a class-level [HarmonyPatch].
                        long pt0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1498: per patch class, for the load clock
                        bool patched = harmony.CreateClassProcessor(type).Patch() != null;
                        VerdantsAscent.Terrain.Performance.FatLoadClock.PatchClass(type, System.Diagnostics.Stopwatch.GetTimestamp() - pt0, patched);
                        if (patched)
                            applied++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        FiresLogger.LogWarning($"Failed to apply Harmony patch {type.FullName}: {ex.Message}");
                    }
                }

                FiresLogger.LogInfo($"Harmony: {applied} patch class(es) applied, {failed} failed"
                    + (skipped > 0 ? $", {skipped} client-only type(s) skipped on the dedicated server." : "."));
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"harmony.PatchAll threw an exception: {ex}");
            }
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("Harmony patch classes");

            // Initialize the legacy biome / paint / terrain RPC systems that
            // were ported verbatim from RPG-maker. The [HarmonyPatch] classes
            // inside these files auto-register via harmony.PatchAll above, but
            // their RPC handlers + console commands + file-cache loaders need
            // explicit Initialize calls.
            //
            // Without these:
            //   * TerrainPaintRPC's biome/clutter/paint RPCs aren't registered,
            //     so MP biome sync silently no-ops on the receiver side.
            //   * BiomeFilePersistence's on-disk biome-paint cache is never
            //     loaded, so biome paints on vanilla terrain don't survive
            //     world reload.
            //   * BiomeOverlayPainter's paint_lava / paint_snow / paint_biome
            //     console commands aren't registered.
            //
            // Phase F: legacy TerrainPaintRPC removed. RPC sync now flows
            // through TileBrushRPC (tile ops) + RegionScrubberRPC (clear).

            // Track which sub-system inits succeeded so the Ã°Å¸â€Â§ COMMANDS
            // mini-banner at the end can summarize. Each int is 1 if
            // the corresponding Initialize/RegisterCommands call
            // returned without throwing, 0 otherwise. Per-success
            // detail logs are verbose-only Ã¢â‚¬â€ the banner conveys the
            // aggregate count for normal loads.
            int biomePersistOK = 0, biomePainterOK = 0, iceToolOK = 0, landSnowOK = 0;

            try
            {
                VerdantsAscent.Terrain.Biome.BiomeFilePersistence.Initialize();
                biomePersistOK = 1;
                if (VerdantsAscent.Utilities.VAFatLoadSummary.VerboseEnabled)
                    FiresLogger.LogInfo($"BiomeFilePersistence initialized.");
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"BiomeFilePersistence.Initialize threw: {ex.Message}");
            }

            if (!isDedicatedServer)
            {
                try
                {
                    VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.RegisterCommands();
                    biomePainterOK = 1;
                    if (VerdantsAscent.Utilities.VAFatLoadSummary.VerboseEnabled)
                        FiresLogger.LogInfo($"BiomeOverlayPainter console commands registered.");
                }
                catch (Exception ex)
                {
                    FiresLogger.LogError($"BiomeOverlayPainter.RegisterCommands threw: {ex.Message}");
                }

                // Wire up the LandIce / FloatingIce
                // pipeline. IcePlacementTool.Initialize() registers
                // place_ice / place_ice_land / place_ice_water / delete_ice /
                // break_standalone_ice, and chains to LandIceCommands +
                // FloatingIceCommands which register the height-mod
                // commands (ice_land_raise / lower / level / smooth /
                // delete_ice_land + floating equivalents). Also registers
                // the new LandSnowCommands for the terrain-anchor snow.
                try
                {
                    VerdantsAscent.Water.Tools.IcePlacementTool.Initialize();
                    iceToolOK = 1;
                    if (VerdantsAscent.Utilities.VAFatLoadSummary.VerboseEnabled)
                        FiresLogger.LogInfo($"IcePlacementTool + LandIce/FloatingIce commands registered.");
                }
                catch (Exception ex)
                {
                    FiresLogger.LogError($"IcePlacementTool.Initialize threw: {ex.Message}");
                }

                try
                {
                    VerdantsAscent.Water.Tools.LandSnowCommands.Initialize();
                    landSnowOK = 1;
                    if (VerdantsAscent.Utilities.VAFatLoadSummary.VerboseEnabled)
                        FiresLogger.LogInfo($"LandSnowCommands registered (terrain-anchor snow, no water).");
                }
                catch (Exception ex)
                {
                    FiresLogger.LogError($"LandSnowCommands.Initialize threw: {ex.Message}");
                }
            }

            // Ã°Å¸â€Â§ COMMANDS mini-banner Ã¢â‚¬â€ single visual summary of which
            // FAT sub-systems came up successfully. Replaces the 5-line
            // "X console commands registered" cluster above with one
            // box. Per-line details remain available via the verbose
            // gate.
            VerdantsAscent.Utilities.VAFatLoadSummary.EmitCommands(
                dedicated: isDedicatedServer,
                commandSetsRegistered: biomePainterOK + iceToolOK + landSnowOK,
                commandSets: 4,
                biomePersistence: biomePersistOK == 1);

            try { VerdantsAscent.Utilities.FatHelpContent.Register(); }
            catch (Exception ex) { FiresLogger.LogWarning($"Help registration failed: {ex.Message}"); }
            VerdantsAscent.Terrain.Performance.FatLoadClock.Step("biome persistence + commands + help");
            VerdantsAscent.Terrain.Performance.FatLoadClock.EndStart();   // 0.2.1498: one [FatLoad] plugin start line

            // Compact "ready" banner Ã¢â‚¬â€ small floating island with
            // brand panel. Deferred to WORLD LOAD time (when
            // ZNetScene is up) so it fires in lockstep with FAP's
            // and FGN's compact banners. The BIG "loading" banner
            // already fired at the top of Awake; the compact one
            // bookends the load once the world is actually live.
            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        // Ã°Å¸â€œÂ¡ RPCs mini-banner Ã¢â‚¬â€ one visual summary of the session's RPC
        // handler registrations (ConfigSync + RegionScrubberRPC +
        // TileBrushRPC), from the Priority.Last part of FAT's one ZNet.Awake
        // hook (FatVanillaHooks), after every registration, so the counts are
        // real. Replaces the per-system "Registered N handlers" lines.
        internal static void EmitRpcSummary()
        {
            try
            {
                VerdantsAscent.Utilities.VAFatLoadSummary.EmitRpcs(
                    configSync: ConfigSync.configSyncs.Contains(Instance.configSync) ? 1 : 0,
                    scrubber: VerdantsAscent.Terrain.Lifecycle.RegionScrubberRPC.RegisteredHandlers,
                    tileBrush: TileBrushRPC.RegisteredHandlers);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"RPC summary banner failed: {ex.Message}");
            }
        }

        // Waits for ZNetScene + prefab table to be live, then emits
        // the compact floating-island brand banner. Failure is
        // non-fatal Ã¢â‚¬â€ falls back to the plain "mod loaded" log so
        // the load event is still recorded.
        private System.Collections.IEnumerator EmitCompactBannerWhenZNetReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            yield return new WaitForEndOfFrame();

            try { VerdantsAscent.Utilities.VAFatBanner.Print(); }
            catch (Exception ex)
            {
                FiresLogger.LogInfo($"mod loaded. (banner failed: {ex.Message})");
            }
        }

        private bool m_applyingPreset;

        private void ApplySelectedPreset()
        {
            if (m_applyingPreset) return;
            string presetName = ConfigManager.Instance.configPresetToLoad.Value;
            if (string.IsNullOrEmpty(presetName) || presetName == ConfigManager.NoPresetOption) return;

            m_applyingPreset = true;
            try
            {
                int changed = FiresCore.Config.FiresConfigPresets.ApplyPreset(
                    ConfigManager.PresetModName, Config, presetName);
                FiresLogger.LogInfo($"Preset '{presetName}' applied ({changed} setting(s) changed).");
            }
            finally
            {
                m_applyingPreset = false;
            }
        }

        /// <summary>Applies the preset the SERVER names. Skipped on the server itself, where it is authored.</summary>
        private void ApplyServerPreset()
        {
            string presetName = ConfigManager.Instance.configServerPreset.Value;
            if (string.IsNullOrWhiteSpace(presetName)) return;
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;

            if (m_applyingPreset) return;
            m_applyingPreset = true;
            try
            {
                int changed = FiresCore.Config.FiresConfigPresets.ApplyPreset(
                    ConfigManager.PresetModName, Config, presetName);
                FiresLogger.LogInfo($"Server preset '{presetName}' applied ({changed} setting(s) changed).");
            }
            finally
            {
                m_applyingPreset = false;
            }
        }

        private void SaveNamedPreset()
        {
            if (!ConfigManager.Instance.configPresetSaveNow.Value) return;

            FiresCore.Config.FiresConfigPresets.SaveCurrentValuesAsPreset(
                ConfigManager.PresetModName, Config, string.IsNullOrWhiteSpace(ConfigManager.Instance.configPresetSaveName.Value) ? "MyLook" : ConfigManager.Instance.configPresetSaveName.Value);
            ConfigManager.Instance.configPresetSaveNow.Value = false;
        }

        private void OnDestroy()
        {
            FiresCore.Materials.TerrainMaterialCache.MaterialCaptured -= OnTerrainMaterialCaptured;
            FiresCore.Materials.TerrainMaterialCache.SetShaderOverride(null);

            if (assetBundle != null)
            {
                try { assetBundle.Unload(true); }
                catch (Exception ex) { FiresLogger.LogError($"AssetBundle unload error: {ex}"); }
                finally { assetBundle = null; }
            }

            if (RootObject != null)
            {
                UnityEngine.Object.Destroy(RootObject);
                RootObject = null;
            }

            // On an application quit Unity destroys the plugins before ZNet; unpatching here would drop FAT's ZNet.OnDestroy
            // and other teardown hooks. Application.quitting fires first, and the process is ending anyway.
            if (!s_applicationQuitting)
                try { harmony?.UnpatchSelf(); } catch { }

            try { ConfigManager.Instance?.Dispose(); } catch { }

            configSync = null;
            Instance = null;
        }

        public AssetBundle GetAssetBundle() => assetBundle;

        // A capture (or va_terrain_recapture) lands after tiles, voxel chunk
        // views and live FarQuilt tiles already hold the fallback or the old
        // instance; move them all now so no two instances meet at a border.
        private static void OnTerrainMaterialCaptured(Material material)
        {
            TileMeshRenderer.UpgradeAllRenderersToCachedMaterial();
            VerdantsAscent.Terrain.Voxel.VoxelWorld.RefreshAllViewMaterials(material);
            VerdantsAscent.Terrain.Voxel.FarQuilt.RefreshAllTileMaterials(material);
        }

        private void LoadAssets()
        {
            if (assetBundle != null) return;

            try
            {
                assetBundle = GetAssetBundleFromResources("adminterrain");
                if (assetBundle == null)
                {
                    FiresLogger.LogWarning($"Embedded asset bundle 'adminterrain' not found Ã¢â‚¬â€ terrain/water bundle-dependent features will be unavailable.");
                    return;
                }
                FiresLogger.LogInfo($"Loaded asset bundle: {assetBundle.name}");

                // The voxel-mesh terrain shader (Custom/FiresVoxelHeightmap):
                // vanilla layer logic with voxel-proper projection Ã¢â‚¬â€ ceilings
                // wear cliff rock instead of top-down grass, steep faces
                // triplanar-blend the ground/paint/snow/ash layers instead of
                // smearing them. Handed to the material cache; it swaps the
                // shared terrain material's shader (flat ground renders
                // identical to vanilla by construction).
                var voxelShader = assetBundle.LoadAsset<Shader>("FiresVoxelHeightmap");
                // 0.2.1025: the fixed shader ships in its own embedded bundle 'fatvoxelshader' (built by FatVoxelShaderBundleBuilder;
                // it has a ShadowCaster pass, so voxel chunks reach the camera depth texture and the shadow map - the pale swapped
                // zones, RenderDoc 10-23). Preferred over adminterrain's copy, which has none. Same shader name, so it is loaded
                // under the same asset name; the bundle stays loaded for the session.
                try
                {
                    var fixedBundle = GetAssetBundleFromResources("fatvoxelshader");
                    var fixedShader = fixedBundle != null ? fixedBundle.LoadAsset<Shader>("FiresVoxelHeightmap") : null;
                    if (fixedShader != null)
                    {
                        int passes = fixedShader.passCount;
                        bool caster = false;
                        for (int i = 0; i < passes; i++)
                            if (fixedShader.FindPassTagValue(i, new UnityEngine.Rendering.ShaderTagId("LightMode")).name.Equals("ShadowCaster", StringComparison.OrdinalIgnoreCase)) caster = true;
                        FiresLogger.LogInfo($"Voxel terrain shader: '{fixedShader.name}' from the fatvoxelshader bundle ({passes} passes, ShadowCaster {(caster ? "present" : "MISSING")}, supported on this GPU {fixedShader.isSupported}; built in a LINEAR project, 0.2.1025); adminterrain's copy not used.");
                        if (fixedShader.isSupported || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) voxelShader = fixedShader;
                        else FiresLogger.LogWarning("fatvoxelshader: Custom/FiresVoxelHeightmap is NOT supported on this GPU; keeping adminterrain's copy.");
                    }
                    else FiresLogger.LogWarning("fatvoxelshader bundle missing or empty: the voxel skin uses adminterrain's FiresVoxelHeightmap (no ShadowCaster pass: no depth, no shadows).");
                }
                catch (Exception ex) { FiresLogger.LogWarning($"fatvoxelshader bundle failed to load ({ex.Message}); using adminterrain's FiresVoxelHeightmap."); }
                // 0.2.1046 far ring low LOD phase 2: the halo ring's far side draws with Custom/FiresHaloFar (embedded bundle
                // 'fathalofar', built LINEAR by FatHaloFarBundleBuilder): the view-ray squeeze runs on the GPU, lit by the sun
                // against the ring's local up, with its own fog. Without it FatHaloSky keeps the phase 1 CPU path.
                try
                {
                    var farBundle = GetAssetBundleFromResources("fathalofar");
                    var farShader = farBundle != null ? farBundle.LoadAsset<Shader>("FiresHaloFar") : null;
                    if (farShader != null && (farShader.isSupported || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null))
                    {
                        VerdantsAscent.Terrain.Voxel.FatHaloSky.FarShader = farShader;
                        FiresLogger.LogInfo($"Halo far ring shader: '{farShader.name}' from the fathalofar bundle ({farShader.passCount} pass, supported {farShader.isSupported}).");
                    }
                    else FiresLogger.LogWarning($"fathalofar: Custom/FiresHaloFar {(farShader == null ? "missing" : "not supported on this GPU")}; the far ring stays on the CPU path.");
                }
                catch (Exception ex) { FiresLogger.LogWarning($"fathalofar bundle failed to load ({ex.Message}); the far ring stays on the CPU path."); }
                // 0.2.1480: the fatimpostorgpu bundle (Custom/FiresImpostorGPU) left with FAT's far billboards (Fire 11-18).
                // FarLands #8 + #10 ([generator] 11-08, merged 0.2.1416): far mountain shadows + canopy shade on the sun's shadow mask
                // (embedded bundle 'fatfarshade', built LINEAR by FatFarShadeBundleBuilder). Without it [Far Shadows] stands down.
                try
                {
                    bool has = Assembly.GetExecutingAssembly().GetManifestResourceNames().Any(n => n.EndsWith("fatfarshade", StringComparison.OrdinalIgnoreCase));
                    var shadeBundle = has ? GetAssetBundleFromResources("fatfarshade") : null;
                    var shadeShader = shadeBundle != null ? shadeBundle.LoadAsset<Shader>("FarSunShade") : null;
                    VerdantsAscent.Terrain.Voxel.FatFarShade.OfferShader(shadeShader, !has ? "this build has no fatfarshade bundle" : shadeShader == null ? "no FarSunShade in the fatfarshade bundle" : null);
                    if (shadeShader != null) FiresLogger.LogInfo($"Far sun shade shader: '{shadeShader.name}' from the fatfarshade bundle ({shadeShader.passCount} pass, supported {shadeShader.isSupported}).");
                }
                catch (Exception ex) { VerdantsAscent.Terrain.Voxel.FatFarShade.OfferShader(null, $"fatfarshade failed to load ({ex.Message})"); }
                VerdantsAscent.Terrain.Voxel.VoxelZoneLook.VoxelShader = voxelShader;
                if (voxelShader != null)
                    FiresCore.Materials.TerrainMaterialCache.SetShaderOverride(voxelShader);
                else
                    FiresLogger.LogWarning($"Custom/FiresVoxelHeightmap missing from adminterrain bundle Ã¢â‚¬â€ voxel skin stays on the vanilla heightmap shader (top-down projection).");
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"Asset load failed: {ex}");
            }
        }

        internal static AssetBundle GetAssetBundleFromResources(string name)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                FiresLogger.LogError($"No embedded resource ending with '{name}'.");
                return null;
            }

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;
                using (var copy = new System.IO.MemoryStream())
                {
                    stream.CopyTo(copy);
                    return AssetBundle.LoadFromMemory(copy.ToArray());
                }
            }
        }
    }
}


























