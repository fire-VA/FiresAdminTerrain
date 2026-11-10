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
    [BepInDependency(FiresCore.FiresUnifiedCore.PluginGUID, "0.2.816")]   // 0.2.1048: FatHaloSpawns calls Core Surface.Point (0.2.816+)
    // ValheimPerformanceOptimizations replaces vanilla's zone spawn and heightmap render / collision builds outright, which
    // undoes FAT's own terrain pipeline (voxel ground, zone mode, the dedi's off-map refusal). FAT does its own optimizing;
    // It is listed as incompatible, so BepInEx refuses to load FAT beside it.
    [BepInIncompatibility("dev.ontrigger.vpo")]
    public class FiresAdminTerrain : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresAdminTerrain";
        public const string PluginName = "FiresAdminTerrain";
        public const string PluginVersion = "0.2.1430";   // 0.2.1430 ([worldgen]) + (Fire 11-10 2:58 AM DN: "far terrain ends up with tears in it at some seams"): FarTerrainVanilla sizes its second TerrainLod to a whole number of cells per region (8320 m = 5 x 52 x 32 m at ReachMeters 4096), so vanilla's region meshes meet exactly; 8192 m gave 1632 m meshes 1638.4 m apart = a 6.4 m sky gap along every inner region border. // ([worldgen], Fire's hand test 11-10: a relog 26 m above a flat world's ground fell to death): FatLoginSpawn samples the ground on every call until it answers (it was cached NaN from the first call); unknown ground waits (20 s, then vanilla's own no-ground rule: bed / start, never a drop); a terrain logout point more than 4 m above the ground waits for a collider under it and at the timeout lands on the ground; the post-spawn hold (no gravity, no damage) lasts until a collider is under the player, 10 s at most. 0.2.1429 ([worldgen], Fire: "Yes fix the env box code"; [saddles]' find): the env-box terrain-texture override read m_cornerBiomes as Biome[], but Valheim 1.0 has BiomeSector[], so it never applied. Now BiomeSector[], cloned, forced corners = BiomePaint.PlainSectorFor; Debug line when it applies. Same type fix in BiomeOverlayPainter (that corner path has no caller today). Built on 0.2.1425 (live, rig hash 9D2B65B86ADC). 0.2.1425 ([generator] M22): FarBridge finds Core's far types quietly (Core assembly GetType(name, false)); Core's Far\ moves to EBM 1.2.210. 0.2.1424: FatApi FF-compat members.
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
            VerdantsAscent.Water.Core.FiresWaterAuthority.Bind(Config); // FAT owns the wave clock + CalcWave (client+dedi)
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


























