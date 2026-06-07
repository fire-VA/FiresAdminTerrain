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
