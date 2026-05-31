using BepInEx;
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
    // delegated to FAP (source-linked) — see csproj for the linked files.
    //
    // Companion / NPC / Quest / Guild / Discord / Wayshrine / Territory init
    // intentionally absent: this mod is admin terrain + water tools only.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    // Soft dependencies — these mods are not required for FAT to load, but
    // we integrate with them when present. Declaring them gives r2modman /
    // Thunderstore proper "depends on" metadata AND lets BepInEx order our
    // Awake AFTER theirs, so reflective lookups (AdminCheck, IH ruler
    // patches, WEC undo bindings) hit fully-initialized targets.
    [BepInDependency("Azumatt.InfinityHammer",             BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.serversync",     BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.worldeditcommands", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.serverdevcommands", BepInDependency.DependencyFlags.SoftDependency)]
    public class FiresAdminTerrain : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresAdminTerrain";
        public const string PluginName = "FiresAdminTerrain";
        public const string PluginVersion = "0.1.0";

        public static readonly Harmony harmony = new Harmony(PluginGUID);
        public static FiresAdminTerrain Instance { get; private set; }

        public static AssetBundle assetBundle;
        public static GameObject RootObject;
        public static GameObject PrefabContainer { get; private set; }

        public ConfigSync configSync;
        private static ILogHandler _originalLogHandler;

        private void Awake()
        {
            // BIG floating-island banner — fires FIRST so it sits at
            // the top of the FAT console output as the load
            // announcement. The compact tree-island-waterfall banner
            // fires at the end of FinishInit to bookend the load with
            // a "ready" confirmation. See VAFatBanner for the two-
            // banner rationale + content.
            try { VerdantsAscent.Utilities.VAFatBanner.PrintBig(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] VAFatBanner.PrintBig failed: {ex.Message}");
            }

            Debug.Log($"[{PluginName}] Awake() entered — version {PluginVersion}");
            Instance = this;

            InstallLogFilter();

            RootObject = new GameObject("_FiresAdminTerrainRoot");
            UnityEngine.Object.DontDestroyOnLoad(RootObject);

            // Pushes EnvMan wind direction + wrapped day-time into Shader
            // globals each frame so Custom/FiresWater wave animation stays
            // welded to whatever vanilla ocean is doing. Component's Update
            // self-gates on FiresWaterShaderLoader.Enabled — a no-op if the
            // shader bundle isn't loaded, so attaching unconditionally is
            // free.
            RootObject.AddComponent<VerdantsAscent.Water.Core.WindGlobalDriver>();
            // Per-frame env-force driver: registers a "FiresUnderwater"
            // EnvSetup with EnvMan and forces it active whenever the local
            // player's eye is below a water surface. Replaces the simpler
            // UnderwaterFogDriver path — instead of mutating
            // RenderSettings.fog directly, this drives EnvMan so fog +
            // sun + ambient + wet status all switch together with vanilla's
            // 2-second blend. The old UnderwaterFogDriver file is retained
            // (unused) in case we want the raw fog-only path back later.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresUnderwaterEnvDriver>();
            // Per-frame procedural-skybox property overrider. No-op until
            // ConfigManager.configFiresSkyboxOverride flips on. Used to
            // diagnose which skybox property creates the horizon-haze band
            // visible past distant water — see FiresSkyboxDriver class docs.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresSkyboxDriverComponent>();
            // Polls player position; when over deep ocean, bumps ZoneSystem
            // m_activeArea so the FiresWater inner ring extends past the
            // wave-generation distance. Reverts near shore. See
            // OceanZoneRingExtender class docs.
            RootObject.AddComponent<VerdantsAscent.Water.Core.OceanZoneRingExtenderDriver>();
            // Aurora curtain — renders animated ribbon shader on a procedural
            // cylinder around the camera at night. Backdrop glow is driven
            // separately by FiresSkyboxDriver's aurora-glow code path.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresAuroraDriver>();

            PrefabContainer = new GameObject("FiresAdminTerrain_Prefabs");
            PrefabContainer.transform.parent = RootObject.transform;
            PrefabContainer.SetActive(false);

            ConfigManager.Instance.Initialize(Config);

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            configSync.AddLockingConfigEntry(ConfigManager.Instance.configServerAuthority);
            configSync.AddConfigEntry(ConfigManager.Instance.configVerboseLogging);
            configSync.AddConfigEntry(ConfigManager.Instance.configHeightCacheResolution);
            configSync.AddConfigEntry(ConfigManager.Instance.configHeightCacheSamplesPerFrame);
            configSync.AddConfigEntry(ConfigManager.Instance.configUseNewTilePersistence);
            configSync.AddConfigEntry(ConfigManager.Instance.configFiresWaterEnabled);
            configSync.AddConfigEntry(ConfigManager.Instance.configFiresWaterSwapVanilla);
            configSync.AddConfigEntry(ConfigManager.Instance.configFiresWaterUnderwaterEnv);
            // SkyboxOverride is intentionally NOT added to configSync — it's a
            // client-only diagnostic toggle. Each player should be able to
            // A/B-test the horizon haze on their own machine without the
            // server forcing a setting.

            // Push initial config values into the FiresWater static flags BEFORE
            // LoadAssets, so when WindGlobalDriver / FiresUnderwaterEnvDriver
            // tick on the first frame after Awake they see the right state.
            // Live updates from the BepInEx config UI route through the
            // SettingChanged hooks below.
            ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSwapVanilla.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUnderwaterEnv.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxOverride.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxAtmosphereThicknessMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxFogHeightMinOffset.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxFogHeightMaxOffset.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxGroundColorBlendToSky.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configOceanRingExtendEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configOceanRingExtendedActiveArea.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configOceanRingDepthThreshold.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configHideVanillaLodWater.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraGlowEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraGlowIntensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraGlowSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraGlowAtmosphereMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraCurtainsEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraCurtainsIntensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraNightOnly.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraDuringStorms.SettingChanged += (_, __) => ApplyFiresWaterConfig();

            LoadAssets();

            // Once the world is live, retroactively swap any WaterVolumes that
            // came online before our Harmony OnEnable hook started catching
            // them. Cheap idempotent scan — no-op when SwapVanillaOcean is
            // false. Started here so it runs in lockstep with the rest of the
            // mod's startup; the coroutine handles its own readiness wait.
            StartCoroutine(FiresWaterApplyWhenReady());

            // Tile-restore batch tracker — starts an infinite polling
            // coroutine that emits the 🪨 TILES banner whenever a wave
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

        // Push current config values into the static flags the FiresWater
        // shader plumbing reads. Called once at startup with the bound
        // defaults, and again any time a SettingChanged event fires.
        //
        // When SwapVanillaOcean transitions OFF -> we restore originals so
        // vanilla water comes back without needing a restart. When it
        // transitions ON we don't apply here directly — the coroutine
        // FiresWaterApplyWhenReady handles the initial scan, and the
        // Harmony hook on WaterVolume.OnEnable handles any volume that
        // streams in later.
        private void ApplyFiresWaterConfig()
        {
            try
            {
                bool prevSwap = VerdantsAscent.Water.Core.FiresWaterShaderLoader.SwapVanillaOcean;

                VerdantsAscent.Water.Core.FiresWaterShaderLoader.Enabled
                    = ConfigManager.Instance.configFiresWaterEnabled.Value;
                VerdantsAscent.Water.Core.FiresWaterShaderLoader.SwapVanillaOcean
                    = ConfigManager.Instance.configFiresWaterSwapVanilla.Value;
                VerdantsAscent.Water.Core.FiresUnderwaterEnv.Enabled
                    = ConfigManager.Instance.configFiresWaterUnderwaterEnv.Value;
                // Retired: UnderwaterFogDriver was the v1 fog-only path;
                // FiresUnderwaterEnv replaces it. Force the legacy driver
                // off in case anything else flipped it.
                VerdantsAscent.Water.Core.UnderwaterFogDriver.Enabled = false;

                // Push skybox-override values. Driver re-applies every
                // LateUpdate so a change to any slider takes visible effect
                // on the very next frame.
                VerdantsAscent.Water.Core.FiresSkyboxDriver.Enabled
                    = ConfigManager.Instance.configFiresSkyboxOverride.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AtmosphereThicknessMultiplier
                    = ConfigManager.Instance.configFiresSkyboxAtmosphereThicknessMul.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.FogHeightMinOffset
                    = ConfigManager.Instance.configFiresSkyboxFogHeightMinOffset.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.FogHeightMaxOffset
                    = ConfigManager.Instance.configFiresSkyboxFogHeightMaxOffset.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.GroundColorBlendToSky
                    = ConfigManager.Instance.configFiresSkyboxGroundColorBlendToSky.Value;

                VerdantsAscent.Water.Core.OceanZoneRingExtender.Enabled
                    = ConfigManager.Instance.configOceanRingExtendEnabled.Value;
                VerdantsAscent.Water.Core.OceanZoneRingExtender.ExtendedActiveArea
                    = ConfigManager.Instance.configOceanRingExtendedActiveArea.Value;
                VerdantsAscent.Water.Core.OceanZoneRingExtender.OceanDepthThreshold
                    = ConfigManager.Instance.configOceanRingDepthThreshold.Value;
                VerdantsAscent.Water.Core.OceanZoneRingExtender.HideLodWater
                    = ConfigManager.Instance.configHideVanillaLodWater.Value;

                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowEnabled
                    = ConfigManager.Instance.configAuroraGlowEnabled.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowIntensity
                    = ConfigManager.Instance.configAuroraGlowIntensity.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowSpeed
                    = ConfigManager.Instance.configAuroraGlowSpeed.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowAtmosphereMul
                    = ConfigManager.Instance.configAuroraGlowAtmosphereMul.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowNightOnly
                    = ConfigManager.Instance.configAuroraNightOnly.Value;

                VerdantsAscent.Water.Core.FiresAurora.Enabled
                    = ConfigManager.Instance.configAuroraCurtainsEnabled.Value;
                VerdantsAscent.Water.Core.FiresAurora.Intensity
                    = ConfigManager.Instance.configAuroraCurtainsIntensity.Value;
                VerdantsAscent.Water.Core.FiresAurora.NightOnly
                    = ConfigManager.Instance.configAuroraNightOnly.Value;
                VerdantsAscent.Water.Core.FiresAurora.DuringStorms
                    = ConfigManager.Instance.configAuroraDuringStorms.Value;

                // Apply turning ON or OFF immediately so the user sees
                // feedback when they flip the config. The retroactive
                // scan is cheap (a few materials at most).
                bool nowSwap = VerdantsAscent.Water.Core.FiresWaterShaderLoader.SwapVanillaOcean;
                if (prevSwap && !nowSwap)
                {
                    VerdantsAscent.Water.Core.FiresWaterShaderLoader.RestoreVanillaWater();
                }
                else if (!prevSwap && nowSwap)
                {
                    VerdantsAscent.Water.Core.FiresWaterShaderLoader.ApplyToAllVanillaWater();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresWaterConfig failed: {ex.Message}");
            }
        }

        // Wait for ZNetScene to be live before the retroactive water swap —
        // before that point WaterVolume.Instances and ZNetScene.m_prefabs
        // are both empty, so the scan would find nothing.
        private System.Collections.IEnumerator FiresWaterApplyWhenReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            yield return new WaitForEndOfFrame();

            try
            {
                int n = VerdantsAscent.Water.Core.FiresWaterShaderLoader.ApplyToAllVanillaWater();
                if (n > 0 && VerdantsAscent.Utilities.VAFatLoadSummary.VerboseEnabled)
                    Debug.Log($"[{PluginName}] FiresWater initial vanilla swap touched {n} material(s).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] FiresWaterApplyWhenReady scan failed: {ex.Message}");
            }
        }

        private void InstallLogFilter()
        {
            try
            {
                if (_originalLogHandler != null) return;
                _originalLogHandler = Debug.unityLogger.logHandler;
                Debug.unityLogger.logHandler = new RateLimitedLogHandler(_originalLogHandler);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] Failed to install log filter: {ex.Message}");
            }
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
            bool isDedicatedServer = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

            try
            {
                if (!isDedicatedServer)
                {
                    harmony.PatchAll();
                }
                else
                {
                    int patched = 0;
                    int skipped = 0;
                    var assembly = Assembly.GetExecutingAssembly();

                    foreach (var type in assembly.GetTypes())
                    {
                        if (IsClientOnlyPatch(type))
                        {
                            skipped++;
                            continue;
                        }

                        try
                        {
                            harmony.CreateClassProcessor(type).Patch();
                            patched++;
                        }
                        catch (Exception ex)
                        {
                            if (type.GetCustomAttribute<HarmonyPatch>() != null ||
                                type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
                            {
                                Debug.LogWarning($"[{PluginName}] Failed to apply Harmony patch {type.FullName}: {ex.Message}");
                            }
                        }
                    }

                    Debug.Log($"[{PluginName}] Dedicated server: patched {patched} types, skipped {skipped} client-only types");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{PluginName}] harmony.PatchAll threw an exception: {ex}");
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

            // Track which sub-system inits succeeded so the 🔧 COMMANDS
            // mini-banner at the end can summarize. Each int is 1 if
            // the corresponding Initialize/RegisterCommands call
            // returned without throwing, 0 otherwise. Per-success
            // detail logs are verbose-only — the banner conveys the
            // aggregate count for normal loads.
            int biomePersistOK = 0, biomePainterOK = 0, snowCmdsOK = 0, iceToolOK = 0, landSnowOK = 0;

            try
            {
                VerdantsAscent.Terrain.Biome.BiomeFilePersistence.Initialize();
                biomePersistOK = 1;
                if (VerdantsAscent.Utilities.VAFatLoadSummary.VerboseEnabled)
                    Debug.Log($"[{PluginName}] BiomeFilePersistence initialized.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{PluginName}] BiomeFilePersistence.Initialize threw: {ex.Message}");
            }

            if (!isDedicatedServer)
            {
                try
                {
                    VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.RegisterCommands();
                    biomePainterOK = 1;
                    if (VerdantsAscent.Utilities.VAFatLoadSummary.VerboseEnabled)
                        Debug.Log($"[{PluginName}] BiomeOverlayPainter console commands registered.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{PluginName}] BiomeOverlayPainter.RegisterCommands threw: {ex.Message}");
                }

                // D6 — register /snow_* console commands. Previously
                // SnowCommands.RegisterCommands() existed but was never
                // called from anywhere; every snow command silently
                // failed to bind.
                try
                {
                    VerdantsAscent.Terrain.Snow.SnowCommands.RegisterCommands();
                    snowCmdsOK = 1;
                    if (VerdantsAscent.Utilities.VAFatLoadSummary.VerboseEnabled)
                        Debug.Log($"[{PluginName}] SnowCommands console commands registered.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{PluginName}] SnowCommands.RegisterCommands threw: {ex.Message}");
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
                        Debug.Log($"[{PluginName}] IcePlacementTool + LandIce/FloatingIce commands registered.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{PluginName}] IcePlacementTool.Initialize threw: {ex.Message}");
                }

                try
                {
                    VerdantsAscent.Water.Tools.LandSnowCommands.Initialize();
                    landSnowOK = 1;
                    if (VerdantsAscent.Utilities.VAFatLoadSummary.VerboseEnabled)
                        Debug.Log($"[{PluginName}] LandSnowCommands registered (terrain-anchor snow, no water).");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{PluginName}] LandSnowCommands.Initialize threw: {ex.Message}");
                }
            }

            // 🔧 COMMANDS mini-banner — single visual summary of which
            // FAT sub-systems came up successfully. Replaces the 5-line
            // "X console commands registered" cluster above with one
            // box. Per-line details remain available via the verbose
            // gate.
            VerdantsAscent.Utilities.VAFatLoadSummary.EmitCommands(
                biomePersistence: biomePersistOK,
                biomePainter: biomePainterOK,
                snowCmds: snowCmdsOK,
                iceTool: iceToolOK,
                landSnow: landSnowOK);

            // Compact "ready" banner — small floating island with
            // brand panel. Deferred to WORLD LOAD time (when
            // ZNetScene is up) so it fires in lockstep with FAP's
            // and FGN's compact banners. The BIG "loading" banner
            // already fired at the top of Awake; the compact one
            // bookends the load once the world is actually live.
            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        // Waits for ZNetScene + prefab table to be live, then emits
        // the compact floating-island brand banner. Failure is
        // non-fatal — falls back to the plain "mod loaded" log so
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
                Debug.Log($"[{PluginName}] mod loaded. (banner failed: {ex.Message})");
            }
        }

        private void OnDestroy()
        {
            if (assetBundle != null)
            {
                try { assetBundle.Unload(true); }
                catch (Exception ex) { Debug.LogError($"[{PluginName}] AssetBundle unload error: {ex}"); }
                finally { assetBundle = null; }
            }

            if (RootObject != null)
            {
                UnityEngine.Object.Destroy(RootObject);
                RootObject = null;
            }

            try { harmony?.UnpatchSelf(); } catch { }

            try { ConfigManager.Instance?.Dispose(); } catch { }

            configSync = null;
            Instance = null;
        }

        public AssetBundle GetAssetBundle() => assetBundle;

        private void LoadAssets()
        {
            if (assetBundle != null) return;

            try
            {
                assetBundle = GetAssetBundleFromResources("firesnpcs");
                if (assetBundle == null)
                {
                    Debug.LogWarning($"[{PluginName}] Embedded asset bundle 'firesnpcs' not found — terrain/water bundle-dependent features will be unavailable.");
                    return;
                }
                Debug.Log($"[{PluginName}] Loaded asset bundle: {assetBundle.name}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{PluginName}] Asset load failed: {ex}");
            }
        }

        private AssetBundle GetAssetBundleFromResources(string name)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                Debug.LogError($"[{PluginName}] No embedded resource ending with '{name}'.");
                return null;
            }

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                return AssetBundle.LoadFromStream(stream);
            }
        }
    }
}
