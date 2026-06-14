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
    [BepInDependency(FiresCore.FiresUnifiedCore.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
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

            // Rendering drivers (water shader, sky, moon, planet, orbitals,
            // aurora, caustics, fog, underwater) moved to FiresTossinShade.
            // FAT keeps only the gameplay material provider (WaterMaterialManager),
            // which pulls the FiresWater wrap through FiresCore's WaterShaderBridge
            // when the shader mod is installed; otherwise it serves vanilla water.

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

            LoadAssets();

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



        private void InstallLogFilter()
        {
            // FiresUnifiedCore installs the one shared rate-limit/suppression log handler now
            // (FiresCore.Logging.RateLimitedLogHandler). This per-mod copy was collapsed into Core so the handler
            // chain stays a single wrapper instead of one-per-mod — intentionally a no-op.
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
                assetBundle = GetAssetBundleFromResources("adminterrain");
                if (assetBundle == null)
                {
                    Debug.LogWarning($"[{PluginName}] Embedded asset bundle 'adminterrain' not found — terrain/water bundle-dependent features will be unavailable.");
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
