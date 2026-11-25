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
        public const string PluginVersion = "0.2.1452";   // 0.2.1452 ([worldgen], lead GO 11-16: VA-Chunk clients logged 28 "Placed 0/... altbiome", and Fire's client biome cache held 256 land points of 4,194,304): the zone gate's planning exemption was per thread, and FiresFixes' Parallel Biome Points builds AltBiomeWorldData's grid on ThreadPool workers inside VerifyBiomeData, so on a zone-mode CLIENT (view from the handshake) every closed point read Ocean / sea floor and FF cached that grid. New grid-build scope (ZoneModeGate.EnterGridBuild/ExitGridBuild, an AsyncLocal that ThreadPool / Task / Parallel carry into the work queued inside the build, plus a global count for a one-read hot path): the build's own threads read the real world; threads already running (the pinned HeightmapBuilder, voxel / far-tile workers) stay gated. VerifyBiomeData's prefix takes Priority.First so the server's synced grid is taken before any other prefix builds one. One Info proof line per build on a zone-mode world. Server path unchanged (no view during its grid build). // was 0.2.1451 ([worldgen], lead 11-16, Fire: the world clone must work properly to ship; VA DN -> VA-Chunk dropped Fire's painted biomes, hand-removed water and ice): FiresWorldClone carries every per-world record that describes the world - _biomepaint.bin and _regenqueue.txt (both keyed to the world uid, rewritten to the copy's: BiomePaint.RetargetRecord / RetargetRegenQueue), _water_removed.bin, _ice.txt, .halo, .halozones, _locationlevel.txt, _locreplan.txt, and _sinkfalls.txt with the bake - never overwriting; every record still left behind is listed with its reason beside CarriedRecordSuffixes. FIX (Fire 11-16 "clicking convert does nothing", SHIP BLOCKER): the world list's "Convert..." row was built DISABLED for any world already carrying FAT's terrain record (BcWorldData.Exists; Verdant's Ascent DN has firesgen_<world>.bcdata from its own conversion), and Core's menu ignores clicks on a disabled row without drawing its tooltip, so the click did nothing and logged nothing. The row, the dialog and BcConvert.TryConvert no longer refuse a converted world: its copy keeps FAT's record (records-folder form by CarryRecords, in-folder form by the new BcWorldData.TryCopyRecord) and TryConvert fails loudly if the copy lacks it. Open logs its entry, build success and any build failure. // was 0.2.1450 ([worldgen], lead 11-16, Fire: the » Biome pill covered vanilla's biome / zone / coordinates readout): FatBiomeMapTool.PlacePill docks to LiveMap 0.1.8+'s measured anchor (LiveMapApi.DockAnchor, by name, cached delegate; null on any miss or throw -> vanilla m_biomeNameLarge as before). Territory-first stacking unchanged. // was 0.2.1449 ([worldgen], lead ruling 11-16): log wording only. RiverSync's server warning for 0 recorded river passes names the real cause (another mod restored the rivers without vanilla's search, i.e. FiresFixes' River Cache; FF 0.1.168 stands it down on a dedicated server when RenderRivers carries another owner's patch), and the client line says "the server had no river lists to send" instead of blaming an old FAT. No logic change. // was 0.2.1448 ([worldgen], lead / [visual] spec, EBM 1.2.422 + FTS 0.3.402 read it): _FiresIceCover channel B = ice proximity (fixed-ice cover R blurred over ~32 m, tent of two box passes on the 2 m grid, zero outside the rect, border ring 0, linear), texture RGBA8, global _FiresIceProxOn 1 while valid (0 on Off); FatApi.TryIceProxAt(x, z, out prox01) any-thread snapshot like TryCalmAt. VANILLA WATER (Fire: "fat needs the code for its own custom water, but vanilla oceans and water will be handled by vedr or vanilla or any other mod hooking them"): removed the WaterVolume.UpdateWaterTime / CalcWave / Depth patches (FAT clock writes into vanilla statics, wave function, sea wind, wave multiplier, calm scaling, depth match + probe RPC, wavematch_test), FiresWaves (WaveIntensity / WaveWeatherMap, _FiresWaveMult / _FiresSeaState, RPC; FatApi wave members are no-ops), FiresSeaWind and the Ship.Awake FX guard; FAT keeps a private clock and writes the _WaterTime global only while no Vedr water clock owns it; the GetWaterSurface amplitude postfix is gated to FAT liquids (_FiresLiquidGrid 1); the SetLiquidLevel cave gate counts voxel ground only; raven / land-spawn / red-ghost / fish checks read FAT water only. // was 0.2.1447 ([worldgen], lead GO 11-16, Fire's ice rule physics half): FatApi.TryCalmAt(x, z, out calm) + CalmRevision (any thread, immutable snapshot of _FiresIceCover.g, smoothstep 0.1-0.9; client only) and FAT's CalcWave scales waves by (1 - calm) while FAT owns waves, so boats / swimmers in ice-enclosed open water do not bob on undrawn waves. // was 0.2.1446 ([worldgen], Fire 11-16 "no snow on the ground" with EBM far tiles on): FIX FatApi.TryFarColor (1444) called Heightmap.GetBiomeColor(BiomeSector) / (Biome) from EBM's builder thread; Vedr patches those overloads with a plain static paint-depth counter (LandscapeSeasons.cs:369-396, ++/-- not atomic), so the race left it off by one and every main-thread chunk paint drew summer colours. TryFarColor + FatSeasons.WinterizeAnyThread now use FatFarData.SplatOf / SectorSplat (vanilla's own colour table, pure, unpatched): never a patched vanilla call off the main thread; FatApi.FarDataApiVersion 2 (= these calls are thread-safe; EBM 1.2.420 uses TryFarColor on its thread only at >= 2). ICE (Fire's spec, [seasons] + [visual] agreed): Vedr sea floes no longer join _FiresIceCover (they ride the real tide + waves; the cover holds fixed ice only); _FiresIceCover is RG8: R unchanged, G = calm (open water a border-seeded flood through open water cannot reach = enclosed by ice R >= 0.5 and / or land; water shaders scale tide + waves by 1 - max(R, G)); land = ground over sea + 1 m and not FAT generated water. // was 0.2.1445 ([worldgen], lead 11-16: FiresVoxelHeightmap far look): the embedded fatvoxelshader bundle (F:ModArchiveFatShaderLinear, FatVoxelShaderBundleBuilder, LINEAR, now D3D11 + Vulkan per the 11-11 project setting) honours the GLOBAL _FiresFarLook set by EBM [Far] FarLook - the same semantics as EBM 1.2.418 / Vedr 0.8.262: no distant albedo/smoothness matte [D8177-8180] and no 0.2 grey distant snow [D8184-8190], detail layers past 400 m (f944), mipped array taps by screen footprint; distant normal flatten kept; off / unset = the same pixels. _FiresShaderRev 1445. // was 0.2.1444 ([worldgen], lead 11-16: all far drawing lives in EBM, FAT supplies data only): FatApi far-terrain DATA for EBM, safe from any thread - TryFarHeight (vanilla near-zone corner method), TryFarEditDelta + FarEditRevision (DistantHeightCache, published copy every 0.5 s after a change), TryFarColor (sector colour, biome paint, Vedr winter) + FarWinterRevision, FarFiresWorld, FarDataApiVersion 1. The 1442 ground underlay is NOT in this build (parked); its two fixes are. // was 0.2.1442 ([worldgen], Fire 11-16: "If we can render the trees at that distance we can render the ground"): Modules\VoxelTerrain\FarGroundUnderlay.cs, one more vanilla distant-LOD heightmap (64 x 128 m, about +-4 km), built on vanilla's HeightmapBuilder thread, 4 m lower, its _LodHideDistance following the real coverage of the finished vanilla + FAT far tiles, so far trees / rocks always have ground under them (arrival, teleport, fast flight). Carries edits (1440) and winter (1441). Key [Far Terrain] GroundUnderlay (default true; off = no object, no work; live). FIX: 1440/1441 refreshed far tiles from Heightmap.GetAllHeightmaps(), which is vanilla's NEAR list (the IsDistantLod setter removes distant tiles), so far-tile refreshes after edits, the key toggles and the season rebuild found nothing; now DistantHeightCache.DistantTiles() walks Heightmap.Instances. FIX: the [FiresVoxel] drawn-heightmaps Debug line labels ZoneSystem's own zone-loading distance and prints ZNet's synced one beside it. // 0.2.1441 ([worldgen], Fire 11-16 frozen lake x -1338 z 726: far terrain dark / black in winter while near ground and far trees are white): vanilla distant-LOD tiles take worldgen biome colours only and Vedr's ground snow skips every distant-LOD heightmap, so nothing put winter on them. FatSeasons.WinterizeDistantLod (FAT's RebuildRenderMesh hook, Priority.Last - 10) runs the same Winterize the voxel ground and FarQuilt tiles use on each distant tile's vertex colours; season change / key flip rebuilds the distant tiles once. Key [Far Terrain] WinterSnow (default true; off = vanilla summer colour far away; live). // 0.2.1440 ([worldgen], Fire 11-16: "build the distant terrain cache with a config toggle to cleanly turn it completely off"): Modules\CustomTerrain\Paint\DistantHeightCache.cs. Visited zones' terrain edits (final height minus worldgen, cm) are captured in the one Heightmap.ApplyModifiers postfix while the zone's TerrainComp is loaded, persisted per world (BepInEx\cache\FiresAdminTerrain\DistantHeights\<world>.fdh), and added to distant-LOD tiles (vanilla TerrainLod, FarTerrainVanilla, FarQuilt) as the mean over each vertex's footprint, so leveled builds stop sinking into the far terrain. Far tiles over a changed zone rebuild once it has left the near ring. Key [DistantHeightCache] Enabled (build default true for testing; OFF = vanilla, nothing runs; live toggle). // 0.2.1439 ([generator], [2ic] audit A1 / [lead] 11-16): FiresWaves' 10 s sea-shader check walks vanilla's WaterVolume.Instances instead of scanning the whole scene (Object.FindObjectsByType<WaterVolume>, the 42-51 ms Game.Update hitch); same timer, same behaviour. FiresWaterAuthority's Vedr lookup is silent (FatSoftTypes.Find, was AccessTools.TypeByName every 5 s) and stops once a world has loaded without Vedr. | 0.2.1438 ([generator], Fire 11-16: water code leaves Core for Advanced Player Movement): the cave-roof share (VoxelCaveEnv) and the breath head-under read (OceanKit) look up APM's FiresAPM.Swimming.PlayerSwim first (Core's only as a fallback); soft dependency on APM for load order. | was 0.2.1437: 0.2.1437 ([ghost], lead 11-13 LoopTimers row "ZoneMode_ZoneSystem_Update.Postfix 18.89 ms/s"): deep-cell cache cap eviction keeps live-job cells, floats the cap (hard 600k), never invalidates tints; 10 s probe line.   // 0.2.1436 ([ghost], lead 11-13 profiler: PlacedWaterField.Canonical 14.9 MB/min at the shore): the bed question no longer builds a closure + delegate per call (per-thread args, one cached delegate); IceField.TryIceAt updates its hold in place (a ConcurrentDictionary set allocated a node per call, 9.8 MB/min); deep-cell queue pre-sized (EnqueueCell growth 4 MB); Debug-only allocation probe on the generated water lookup (GenCacheOn / cache / SurfaceAt bytes per 10 s). // 0.2.1435 ([ghost], lead 11-13 profiler: BiomePaint tint 33.9 MB/min at the dock): tint pump allocation-free per step (pre-sized cell dictionaries, pooled cell ranges, colour and edge arrays reused per heightmap / zone, static sort comparer, key-parts string only with Debug). // 0.2.1434 ([ghost], lead 11-13, Fire's TAA teal wash): instanced far-veg + clutter draws use motionVectorMode = Camera (out of Unity's per-object motion-vector pass). 0.2.1433 ([ghost], lead 11-12, Fire run_6 bare mountain): FarBridge binds EBM's public ImpostorEngine (reflection, by presence) when Core's is gone; bridge line names the engine.
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

            ConfigManager.Instance.Initialize(Config);
            FatCoreSettingsOwned.Bind(Config);   // 0.2.1335: Core's snow / loot / undo keys, owned here
            VerdantsAscent.Water.Core.FiresLiquidConfig.Bind(Config);   // FiresLiquid look sliders
            VerdantsAscent.Water.Core.FiresInlandWaterConfig.Bind(Config);   // per-layer ripple controls for the inland sheet
            VerdantsAscent.Terrain.FarForest.FarForest.Bind(Config);   // 0.2.1412 FarLands #2, all keys default off
            VerdantsAscent.Terrain.Voxel.FarQuilt.BindFarTerrain(Config);   // 0.2.1414 FarLands #3 [Far Terrain], default off
            VerdantsAscent.Terrain.Voxel.FatFarShade.Bind(Config);   // 0.2.1416 FarLands #8 + #10 [Far Shadows] ([generator]), all keys default off
            VerdantsAscent.Terrain.FarForest.FarBridge.InitCoreFar(Config);   // 0.2.1415: Core's ImpostorEngine / FarCache / FarLights / FarBuildings bind their keys (owner FAT)
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
            FatConfigRetire.MoveSections(Config);                   // "VoxelTerrain X" sections -> "Voxel Terrain - X", values carried
            FatConfigRetire.Retire(Config);                        // The named dead keys leave the .cfg

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
            // 0.2.1338: AFTER every AddConfigEntry. The gate's first SetReadOnly makes FiresConfigVisibility cache each entry's tags; registered earlier, that cache had no
            // sync tag, MarkAdvanced below rebuilt the descriptions from it, and the server sent 0 FAT configs to every client.
            FiresCore.Config.FiresAdminConfigGate.Register(Config,
                () => ConfigManager.Instance.configAdminOnlyTuning.Value, ConfigManager.IsAdvancedTuning);
            // Player-facing config rules, applied after the last Bind (Ocean included). Basics only without
            // Show Advanced, one-line descriptions, built-in lists hidden. The admin gate above keeps IsAdvancedTuning.
            FiresCore.Config.FiresConfigVisibility.MarkAdvanced(Config, d => VerdantsAscent.Managers.FatPlayerFacingConfig.IsPlayerAdvanced(Config, d));
            VerdantsAscent.Managers.FatPlayerFacingConfig.Apply(Config);
            FatCoreSettingsOwned.LogSyncTags(configSync);   // 0.2.1338

            LoadAssets();

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
                        if (harmony.CreateClassProcessor(type).Patch() != null)
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
                // 0.2.1399: far billboards with their matrices on the GPU (embedded bundle 'fatimpostorgpu', built LINEAR by
                // FatImpostorGpuBundleBuilder). Without it FarVegImpostors keeps sending matrices each frame.
                try
                {
                    var gpuBundle = GetAssetBundleFromResources("fatimpostorgpu");
                    var gpuShader = gpuBundle != null ? gpuBundle.LoadAsset<Shader>("FiresImpostorGPU") : null;
                    if (gpuShader != null && gpuShader.isSupported)
                    {
                        VerdantsAscent.Terrain.Voxel.FarVegImpostors.GpuShader = gpuShader;
                        FiresLogger.LogInfo($"Far billboard GPU shader: '{gpuShader.name}' from the fatimpostorgpu bundle ({gpuShader.passCount} pass, supported {gpuShader.isSupported}).");
                    }
                    else FiresLogger.LogInfo($"fatimpostorgpu: Custom/FiresImpostorGPU {(gpuShader == null ? "missing" : "not supported on this GPU")}; far billboards send their matrices each frame.");
                }
                catch (Exception ex) { FiresLogger.LogWarning($"fatimpostorgpu bundle failed to load ({ex.Message}); far billboards send their matrices each frame."); }
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


























