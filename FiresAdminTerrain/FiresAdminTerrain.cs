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
    // delegated to FAP (source-linked) â€” see csproj for the linked files.
    //
    // Companion / NPC / Quest / Guild / Discord / Wayshrine / Territory init
    // intentionally absent: this mod is admin terrain + water tools only.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    // Soft dependencies â€” these mods are not required for FAT to load, but
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
    [BepInDependency(FiresCore.FiresUnifiedCore.PluginGUID, "0.2.138")]
    // ValheimPerformanceOptimizations replaces vanilla's zone spawn and heightmap render / collision builds outright, which
    // undoes FAT's own terrain pipeline (voxel ground, zone mode, the dedi's off-map refusal). FAT does its own optimizing;
    // Fire 2026-09-30: listed as incompatible, so BepInEx refuses to load FAT beside it.
    [BepInIncompatibility("dev.ontrigger.vpo")]
    public class FiresAdminTerrain : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresAdminTerrain";
        public const string PluginName = "FiresAdminTerrain";
        public const string PluginVersion = "0.2.873";

        public static readonly Harmony harmony = new Harmony(PluginGUID);
        public static FiresAdminTerrain Instance { get; private set; }

        public static AssetBundle assetBundle;
        public static GameObject RootObject;
        public static GameObject PrefabContainer { get; private set; }

        public ConfigSync configSync;
        private static ILogHandler _originalLogHandler;

        // BepInEx log source â€” banner emitters route through this (not Debug.Log)
        // so banner lines don't stdout-echo a raw white console duplicate.
        public static BepInEx.Logging.ManualLogSource Log;

        private static bool s_applicationQuitting;

        /// <summary>0.2.701: the game is quitting (Application.quitting has fired): teardown must not touch the scene's managers.</summary>
        internal static bool ApplicationQuitting => s_applicationQuitting;

        private void Awake()
        {
            Log = Logger;
            // BIG floating-island banner â€” fires FIRST so it sits at
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

            FiresLogger.LogInfo($"Awake() entered â€” version {PluginVersion}");
            Instance = this;
            Application.quitting += () => s_applicationQuitting = true;
            VerdantsAscent.Terrain.Voxel.VoxelLayerPaint.NoteMainThread();   // 0.2.758: layered biome lookups answer on the main thread only

            // Declared before any world loads, so the offer is ready the moment a peer connects. (A studio bake goes through
            // FAT's own join sync, before PeerInfo: BcJoinSync, D14.)
            try { VerdantsAscent.Terrain.Voxel.FiresSkyIslands.RegisterSync(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"Sky island set sync registration failed: {ex.Message} â€” clients will hold "
                                       + "islands off until their wait times out.");
            }
            try { VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.RegisterWearGround(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"Wear gate registration failed: {ex.Message} â€” pieces on voxel ground keep vanilla's wear timing.");
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
            VerdantsAscent.Water.Core.FiresLiquidConfig.Bind(Config);   // round-84 FiresLiquid look sliders
            VerdantsAscent.Water.Core.FiresInlandWaterConfig.Bind(Config);   // per-layer ripple controls for the inland sheet
            VerdantsAscent.Water.Core.FiresWaterAuthority.Bind(Config); // FAT owns the wave clock + CalcWave (client+dedi)
            VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.Bind(Config); // voxel P0 prototype toggles
            VerdantsAscent.Terrain.Zones.ZoneModeConfig.Bind(Config);
            VerdantsAscent.Terrain.Zones.FatLocationAccelerator.Bind(Config);
            VerdantsAscent.Terrain.Voxel.FiresCaveBiome.Bind(Config);   // 0.2.797: cave biome variety + cave environments
            VerdantsAscent.Terrain.Voxel.FatUndergroundLocations.Bind(Config);   // 0.2.799: underground locations
            VerdantsAscent.Terrain.Voxel.FatGrottos.Bind(Config);   // 0.2.800: grottos
            VerdantsAscent.Terrain.Voxel.FatCaveRivers.Bind(Config);   // 0.2.801: underground rivers
            VerdantsAscent.Terrain.Voxel.FatSinkFalls.Bind(Config);   // 0.2.802: river waterfall sinkholes
            VerdantsAscent.Terrain.Zones.ZoneModeCoreBridge.Register();
            VerdantsAscent.Terrain.Paint.DistantPaintCache.Bind(Config); // paint on distant-LOD terrain
            VerdantsAscent.Terrain.Persistence.TileAnchorHeal.Bind(Config); // duplicate tile anchors: DryRun (default) | Apply
            VerdantsAscent.Terrain.Zones.OffMapZones.Bind(Config); // saved objects beyond the world edge: Off (default) | List | Remove

            ConfigManager.Instance.configPresetToLoad.SettingChanged += (_, __) => ApplySelectedPreset();
            ConfigManager.Instance.configPresetSaveNow.SettingChanged += (_, __) => SaveNamedPreset();
            ConfigManager.Instance.configServerPreset.SettingChanged += (_, __) => ApplyServerPreset();

            FiresCore.Config.FiresConfigVisibility.MarkAdvanced(Config, ConfigManager.IsAdvancedTuning);
            FiresCore.Config.FiresAdminConfigGate.Register(Config,
                () => ConfigManager.Instance.configAdminOnlyTuning.Value, ConfigManager.IsAdvancedTuning);

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            configSync.AddLockingConfigEntry(ConfigManager.Instance.configServerAuthority);
            configSync.AddConfigEntry(ConfigManager.Instance.configVerboseLogging);
            configSync.AddConfigEntry(ConfigManager.Instance.configServerPreset);
            configSync.AddConfigEntry(ConfigManager.Instance.configAdminOnlyTuning);
            configSync.AddConfigEntry(ConfigManager.Instance.configHeightCacheResolution);
            configSync.AddConfigEntry(ConfigManager.Instance.configHeightCacheSamplesPerFrame);
            configSync.AddConfigEntry(ConfigManager.Instance.configUseNewTilePersistence);
            VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.RegisterServerAuthority(configSync);

            LoadAssets();

            // Register the env-box terrain provider so Core's portable EnvironmentBoxController routes its
            // heightmap/clutter/snow work back into FAT's systems. Without this, Core env boxes still apply the
            // env/skybox atmosphere but do no terrain/biome paint (the dark-crypt case needs no terrain anyway).
            try { FiresCore.Bridge.EnvironmentBoxBridge.Register(new VerdantsAscent.Utilities.EnvironmentBoxTerrainProvider()); }
            catch (Exception ex) { FiresLogger.LogWarning($"env-box provider register failed: {ex.Message}"); }

            FiresCore.Materials.TerrainMaterialCache.MaterialCaptured += OnTerrainMaterialCaptured;

            // Tile-restore batch tracker â€” starts an infinite polling
            // coroutine that emits the ðŸª¨ TILES banner whenever a wave
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
            // chain stays a single wrapper instead of one-per-mod â€” intentionally a no-op.
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
            // ([visual] 2026-09-28, admintools_test).
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

            // Track which sub-system inits succeeded so the ðŸ”§ COMMANDS
            // mini-banner at the end can summarize. Each int is 1 if
            // the corresponding Initialize/RegisterCommands call
            // returned without throwing, 0 otherwise. Per-success
            // detail logs are verbose-only â€” the banner conveys the
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

                // 2026-05-25 round-19: WIRE UP the LandIce / FloatingIce
                // pipeline that's been sitting dead in the assembly since
                // the original port. IcePlacementTool.Initialize() registers
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

            // ðŸ”§ COMMANDS mini-banner â€” single visual summary of which
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

            // Compact "ready" banner â€” small floating island with
            // brand panel. Deferred to WORLD LOAD time (when
            // ZNetScene is up) so it fires in lockstep with FAP's
            // and FGN's compact banners. The BIG "loading" banner
            // already fired at the top of Awake; the compact one
            // bookends the load once the world is actually live.
            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        // ðŸ“¡ RPCs mini-banner â€” one visual summary of the session's RPC
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
        // non-fatal â€” falls back to the plain "mod loaded" log so
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
                ConfigManager.PresetModName, Config, ConfigManager.Instance.configPresetSaveName.Value);
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
            // and other teardown hooks (R44-R46). Application.quitting fires first, and the process is ending anyway.
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
                    FiresLogger.LogWarning($"Embedded asset bundle 'adminterrain' not found â€” terrain/water bundle-dependent features will be unavailable.");
                    return;
                }
                FiresLogger.LogInfo($"Loaded asset bundle: {assetBundle.name}");

                // The voxel-mesh terrain shader (Custom/FiresVoxelHeightmap):
                // vanilla layer logic with voxel-proper projection â€” ceilings
                // wear cliff rock instead of top-down grass, steep faces
                // triplanar-blend the ground/paint/snow/ash layers instead of
                // smearing them. Handed to the material cache; it swaps the
                // shared terrain material's shader (flat ground renders
                // identical to vanilla by construction).
                var voxelShader = assetBundle.LoadAsset<Shader>("FiresVoxelHeightmap");
                if (voxelShader != null)
                    FiresCore.Materials.TerrainMaterialCache.SetShaderOverride(voxelShader);
                else
                    FiresLogger.LogWarning($"Custom/FiresVoxelHeightmap missing from adminterrain bundle â€” voxel skin stays on the vanilla heightmap shader (top-down projection).");
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


























