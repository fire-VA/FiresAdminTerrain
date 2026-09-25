using System;
using BepInEx.Configuration;

namespace VerdantsAscent.Managers
{
    // Slim ConfigManager for FiresAdminTerrain — exposes only the configs
    // that CustomTerrain and CustomWater actually consume.
    //
    // ORGANIZATION (2026-06-01): sections + entry keys carry zero-padded
    // numeric prefixes ("01 - General", "01 VerboseLogging") so both the
    // generated .cfg and the in-game ConfigurationManager UI list them in
    // intent order rather than alphabetically. Field declarations below are
    // grouped to mirror the Initialize() section order 1:1.
    //
    // C# field names are STABLE — only the BepInEx section/key strings carry
    // the numbering, so renumbering never touches consuming code (the plugin
    // references the fields, not the strings). Changing section/key strings
    // does orphan previously-saved values in the .cfg; they regenerate at the
    // defaults below on next launch.
    //
    // ConfigSync: configServerAuthority is the locking toggle (server forces
    // clients to match); the rest are unlocked client-side tunables. Hook
    // configSync.lockedConfigChanged -> OnConfigLockChanged in plugin Awake.
    public sealed class ConfigManager : IDisposable
    {
        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ??= new ConfigManager();

        // ===== 00 - Presets =====
        public const string PresetModName = "FiresAdminTerrain";
        public const string NoPresetOption = "(none)";

        public ConfigEntry<string> configPresetToLoad;
        public ConfigEntry<string> configPresetSaveName;
        public ConfigEntry<bool> configPresetSaveNow;
        public ConfigEntry<string> configServerPreset;
        public ConfigEntry<bool> configAdminOnlyTuning;

        // FAT's shader-look sections. Terrain / Water Engine / VoxelTerrain are gameplay and engine settings,
        // not shader sliders, so they stay out of the Advanced filter and out of the admin lock.
        private static readonly string[] ShaderLookSectionPrefixes = { "FiresLiquid", "FiresInlandWater" };

        /// <summary>True for FAT's liquid-look sliders — everything in those sections bar the on/off toggles.</summary>
        public static bool IsAdvancedTuning(ConfigDefinition definition)
        {
            bool inShaderSection = false;
            foreach (string prefix in ShaderLookSectionPrefixes)
                if (definition.Section.StartsWith(prefix, StringComparison.Ordinal)) { inShaderSection = true; break; }

            if (!inShaderSection) return false;
            return definition.Key.IndexOf("Enabled", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static string[] BuildPresetOptions()
        {
            var options = new System.Collections.Generic.List<string> { NoPresetOption };
            options.AddRange(FiresCore.Config.FiresConfigPresets.ListPresetNames(PresetModName));
            return options.ToArray();
        }

        // ===== 01 - General =====
        public ConfigEntry<bool> configVerboseLogging;
        public ConfigEntry<bool> configServerAuthority;

        // ===== 02 - Terrain =====
        public ConfigEntry<bool> configUseNewTilePersistence;

        // ===== 03 - Water Engine =====
        public ConfigEntry<int> configHeightCacheResolution;
        public ConfigEntry<int> configHeightCacheSamplesPerFrame;

        public event Action ConfigLockChanged;

        public void Initialize(ConfigFile config)
        {
            if (config == null) return;

            // ===================================================================
            // 00 - Presets
            // ===================================================================
            configPresetToLoad = config.Bind(
                "00 - Presets", "01 Preset", NoPresetOption,
                new ConfigDescription(
                    "Pick a saved look and it applies immediately. Presets are plain .cfg files in "
                    + "BepInEx/config/FiresRPGmaker/Presets/FiresAdminTerrain/ — share one by copying the file. "
                    + "Files added while the game is running show up in this list after a restart.",
                    new AcceptableValueList<string>(BuildPresetOptions())));

            configPresetSaveName = config.Bind(
                "00 - Presets", "02 SaveAsName", "MyLook",
                "Name to save the CURRENT settings under when you tick '03 SavePreset' below.");

            configPresetSaveNow = config.Bind(
                "00 - Presets", "03 SavePreset", false,
                "Tick to write every current setting to a preset named by '02 SaveAsName'. Unticks itself "
                + "once the file is written.");

            configServerPreset = config.Bind(
                "00 - Presets", "04 ServerPreset", "",
                "SERVER-SET. When the server names a preset here, every connecting client applies it. Clients "
                + "need that preset file present locally. Empty = clients keep their own settings.");

            configAdminOnlyTuning = config.Bind(
                "00 - Presets", "05 AdminOnlyShaderTuning", false,
                "SERVER-SET. When on, only admins can move the liquid-look sliders. Everyone still sees them, "
                + "shown as locked.");

            // ===================================================================
            // 01 - General
            // ===================================================================
            configVerboseLogging = config.Bind(
                "01 - General", "01 VerboseLogging", false,
                "Enable verbose log output for terrain/water diagnostics.");

            configServerAuthority = config.Bind(
                "01 - General", "02 ServerAuthority", true,
                "When true, the server forces clients to match its config (ConfigSync lock).");

            // ===================================================================
            // 02 - Terrain
            // ===================================================================
            configUseNewTilePersistence = config.Bind(
                "02 - Terrain", "01 UseNewTilePersistence", false,
                "When true, terrain ops save through the new tile-anchor system (64m anchors at Y=-5000) "
                + "instead of the legacy TerrainCombinerAnchor at Y=0. Default OFF until validated. "
                + "See Docs/TERRAIN_PERSISTENCE_REDESIGN_PLAN.md.");

            // ===================================================================
            // 03 - Water Engine (WorldHeightCache for auto-water river detection)
            // ===================================================================
            configHeightCacheResolution = config.Bind(
                "03 - Water Engine", "01 HeightCacheResolution", 4096,
                "Grid resolution (X by X) for WorldHeightCache used by auto-water river detection. Range [1024, 20480].");

            configHeightCacheSamplesPerFrame = config.Bind(
                "03 - Water Engine", "02 HeightCacheSamplesPerFrame", 5000,
                "Samples per frame while building WorldHeightCache. Range [500, 20000].");
        }

        // Wired via configSync.lockedConfigChanged += Instance.OnConfigLockChanged.
        // ConfigSync's lockedConfigChanged is an Action (no payload) — fires whenever
        // the server's lock state changes; the locked vs unlocked decision lives on
        // ConfigSync.IsLocked / IsSourceOfTruth at the moment of inspection.
        public void OnConfigLockChanged()
        {
            ConfigLockChanged?.Invoke();
        }

        public void Dispose()
        {
            // Reserved for future FileSystemWatcher / async resources.
        }
    }
}
