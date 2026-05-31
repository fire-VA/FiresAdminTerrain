using System;
using BepInEx.Configuration;

namespace VerdantsAscent.Managers
{
    // Slim ConfigManager for FiresAdminTerrain — exposes only the configs
    // that CustomTerrain and CustomWater actually consume. The original
    // FiresRPGmaker ConfigManager had hundreds of entries for companions,
    // quests, territory, progression, etc. — none of which apply here.
    //
    // ConfigSync integration: configServerAuthority is the locking toggle
    // (server forces clients to match); the rest are unlocked client-side
    // tunables. Hook configSync.lockedConfigChanged → OnConfigLockChanged
    // in the plugin Awake so admin changes propagate.
    public sealed class ConfigManager : IDisposable
    {
        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ??= new ConfigManager();

        public ConfigEntry<bool> configVerboseLogging;
        public ConfigEntry<bool> configServerAuthority;
        public ConfigEntry<int> configHeightCacheResolution;
        public ConfigEntry<int> configHeightCacheSamplesPerFrame;

        // Phase 8d toggle: route terrain ops through the new TileAnchor system
        // (Y=-5000 tile-aligned ZDOs) instead of the legacy TerrainCombinerAnchor
        // path. Default OFF until the new path is validated in multiplayer.
        // When ON, op entry points (disk place / paint / height ops) write through
        // TileAnchor.Spawn + ApplyOpAndSave per overlapping 64m tile; the old
        // TerrainCompStorage / TerrainPersistence / DirtFloorSaveManager calls
        // become no-ops. See TERRAIN_PERSISTENCE_REDESIGN_PLAN.md §6.2.
        public ConfigEntry<bool> configUseNewTilePersistence;

        // Custom/FiresWater shader controls. Plugin Awake binds settingChanged
        // handlers to push these into the matching static fields on
        // FiresWaterShaderLoader / FiresUnderwaterEnv. Default ON during
        // bring-up so flipping the bundle in immediately produces visible
        // changes; flip off in the BepInEx config UI if you need to A/B
        // against vanilla.
        public ConfigEntry<bool> configFiresWaterEnabled;
        public ConfigEntry<bool> configFiresWaterSwapVanilla;
        public ConfigEntry<bool> configFiresWaterUnderwaterEnv;

        // Active-zone-area expansion while sailing over deep ocean. Extends
        // the inner ring of animated WaterVolume meshes so storm-wave crests
        // don't reach the vanilla LOD-plane boundary and visually cut off.
        // Reverts to vanilla the moment the player returns near shore.
        public ConfigEntry<bool>  configOceanRingExtendEnabled;
        public ConfigEntry<int>   configOceanRingExtendedActiveArea;
        public ConfigEntry<float> configOceanRingDepthThreshold;

        // Hide vanilla's WaterPlane / water_lod renderer entirely so the
        // "circle of different water" outside our zone ring goes away.
        // Whatever's behind it (skydome / skybox / void) shows through —
        // first test to see if that's visually acceptable.
        public ConfigEntry<bool> configHideVanillaLodWater;

        // Custom/SkyboxProcedural runtime overrides — for A/B-testing which
        // property creates the visible "horizon-haze band" past the water.
        // Defaults are no-op (mul=1, offsets=0, blend=0) so flipping Override
        // ON alone changes nothing visually. Tweak the sliders in-game via
        // BepInEx ConfigurationManager and watch the horizon react.
        // Aurora — Path A (skybox glow) + Path B (curtain ribbons).
        // Two layers that work together: the glow paints a colored hemisphere
        // backdrop, the curtains add flowing noise ribbons in front.
        public ConfigEntry<bool>  configAuroraGlowEnabled;
        public ConfigEntry<float> configAuroraGlowIntensity;
        public ConfigEntry<float> configAuroraGlowSpeed;
        public ConfigEntry<float> configAuroraGlowAtmosphereMul;
        public ConfigEntry<bool>  configAuroraCurtainsEnabled;
        public ConfigEntry<float> configAuroraCurtainsIntensity;
        public ConfigEntry<bool>  configAuroraNightOnly;
        public ConfigEntry<bool>  configAuroraDuringStorms;

        public ConfigEntry<bool>  configFiresSkyboxOverride;
        public ConfigEntry<float> configFiresSkyboxAtmosphereThicknessMul;
        public ConfigEntry<float> configFiresSkyboxFogHeightMinOffset;
        public ConfigEntry<float> configFiresSkyboxFogHeightMaxOffset;
        public ConfigEntry<float> configFiresSkyboxGroundColorBlendToSky;

        public event Action ConfigLockChanged;

        public void Initialize(ConfigFile config)
        {
            if (config == null) return;

            configVerboseLogging = config.Bind(
                "General", "VerboseLogging", false,
                "Enable verbose log output for terrain/water diagnostics.");

            configServerAuthority = config.Bind(
                "General", "ServerAuthority", true,
                "When true, the server forces clients to match its config (ConfigSync lock).");

            configHeightCacheResolution = config.Bind(
                "Water", "HeightCacheResolution", 4096,
                "Grid resolution (X by X) for WorldHeightCache used by auto-water river detection. Range [1024, 20480].");

            configHeightCacheSamplesPerFrame = config.Bind(
                "Water", "HeightCacheSamplesPerFrame", 5000,
                "Samples per frame while building WorldHeightCache. Range [500, 20000].");

            configUseNewTilePersistence = config.Bind(
                "Terrain", "UseNewTilePersistence", false,
                "When true, terrain ops save through the new tile-anchor system (64m anchors at Y=-5000) "
                + "instead of the legacy TerrainCombinerAnchor at Y=0. Default OFF until validated. "
                + "See Docs/TERRAIN_PERSISTENCE_REDESIGN_PLAN.md.");

            configFiresWaterEnabled = config.Bind(
                "FiresWater", "Enabled", true,
                "Master switch for the Custom/FiresWater shader. When true, FAT custom water meshes "
                + "render with the two-pass shader (visible from below with proper wave-synced surface). "
                + "When false, FAT water uses the captured vanilla material as-is (one-sided).");

            configFiresWaterSwapVanilla = config.Bind(
                "FiresWater", "SwapVanillaOcean", true,
                "When true (and Enabled is also true), every vanilla WaterVolume's surface shader is swapped "
                + "to Custom/FiresWater. Affects ALL ocean rendering in the world. The swap is value-preserving "
                + "(all 33 vanilla water properties transfer to the new shader). Flip off for A/B comparison.");

            configFiresWaterUnderwaterEnv = config.Bind(
                "FiresWater", "UnderwaterEnv", true,
                "When true, a custom 'FiresUnderwater' environment is registered with EnvMan and force-"
                + "activated whenever the local player's eye drops below a water surface. Drives fog, sun "
                + "color, ambient light, and wet-status together via the game's own env-blend system "
                + "(2-second crossfade). Independent of the shader Enabled toggle.");

            configOceanRingExtendEnabled = config.Bind(
                "FiresWater", "OceanRingExtendEnabled", true,
                "When true, bump ZoneSystem.m_activeArea while the local player is over deep ocean so the "
                + "animated FiresWater zone meshes cover a wider ring and storm waves don't visibly cut "
                + "off at the vanilla LOD-plane boundary. Reverts to vanilla the moment the player returns "
                + "near shore. Client-only — does not affect other players.");

            configOceanRingExtendedActiveArea = config.Bind(
                "FiresWater", "OceanRingExtendedActiveArea",
                2,
                new ConfigDescription(
                    "ZoneSystem.m_activeArea value used while over deep ocean. 1=vanilla (3x3 zones, ~96m "
                    + "radius), 2=5x5 (~160m), 3=7x7 (~224m). Larger values load more zones around the "
                    + "player so cost more memory/CPU/network while at sea.",
                    new AcceptableValueRange<int>(1, 4)));

            configOceanRingDepthThreshold = config.Bind(
                "FiresWater", "OceanRingDepthThreshold",
                10f,
                new ConfigDescription(
                    "Water column depth (meters below sea level Y=30) at the player's position above which "
                    + "the ring expansion activates. Lower = activates closer to shore (more aggressive). "
                    + "Higher = only in true deep ocean.",
                    new AcceptableValueRange<float>(1f, 30f)));

            configHideVanillaLodWater = config.Bind(
                "FiresWater", "HideVanillaLodWater", false,
                "When true, hide vanilla's WaterPlane (water_lod) renderer entirely. The flat outer-ring "
                + "of mismatched water disappears; whatever's behind it (skydome / skybox / void) shows "
                + "through at the horizon. First test before deciding whether we need to spawn our own "
                + "replacement geometry. Client-only.");

            // Procedural skybox overrides for A/B-testing the horizon haze band.
            // Defaults are no-op (multiplier=1, offsets=0, blend=0) so flipping
            // Override ON alone changes nothing visually — tweak the sliders
            // and watch the horizon to identify which property drives the band.
            configAuroraGlowEnabled = config.Bind(
                "FiresAurora", "GlowEnabled", false,
                "When true, animate the procedural skybox's _GroundColor through a green→cyan→purple "
                + "aurora gradient and clamp _AtmosphereThickness low so the colored hemisphere bleeds "
                + "up at the horizon. Backdrop layer for the curtain ribbons (see CurtainsEnabled). "
                + "Night-only gate via AuroraNightOnly.");

            configAuroraGlowIntensity = config.Bind(
                "FiresAurora", "GlowIntensity",
                1.0f,
                new ConfigDescription(
                    "Brightness multiplier for the aurora horizon glow. 0 = off, 1 = default, "
                    + "higher = more saturated.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configAuroraGlowSpeed = config.Bind(
                "FiresAurora", "GlowSpeed",
                0.05f,
                new ConfigDescription(
                    "Color-cycle speed for the glow gradient. Lower = slower morphing. 0.05 ≈ one "
                    + "full cycle every ~80 seconds.",
                    new AcceptableValueRange<float>(0.005f, 0.5f)));

            configAuroraGlowAtmosphereMul = config.Bind(
                "FiresAurora", "GlowAtmosphereMultiplier",
                0.25f,
                new ConfigDescription(
                    "Multiplier on _AtmosphereThickness while glow is active. Lower = thinner "
                    + "atmosphere = more aurora bleed-through. 0.25 ≈ vanilla×0.25 (sharp glow); "
                    + "1.0 = vanilla atmosphere (no extra bleed).",
                    new AcceptableValueRange<float>(0f, 1f)));

            configAuroraCurtainsEnabled = config.Bind(
                "FiresAurora", "CurtainsEnabled", false,
                "When true, render the Custom/FiresAurora curtain shader on a cylinder around the "
                + "camera. Adds flowing noise-driven ribbons over the sky — the curtain layer on top "
                + "of the backdrop glow (see GlowEnabled).");

            configAuroraCurtainsIntensity = config.Bind(
                "FiresAurora", "CurtainsIntensity",
                1.0f,
                new ConfigDescription(
                    "Brightness multiplier for the aurora curtain ribbons. 0 = off, 1 = default.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configAuroraNightOnly = config.Bind(
                "FiresAurora", "NightOnly", true,
                "When true, both glow and curtains only show at night (EnvMan.IsNight). When false, "
                + "they show whenever enabled regardless of time-of-day.");

            configAuroraDuringStorms = config.Bind(
                "FiresAurora", "DuringStorms", false,
                "When true, curtains render even during rain/thunder/storm envs. When false (default), "
                + "they hide during storms because they don't read well through heavy precipitation.");

            configFiresSkyboxOverride = config.Bind(
                "FiresWater", "SkyboxOverride", false,
                "When true, apply runtime overrides to Custom/SkyboxProcedural to test which property "
                + "is responsible for the horizon-haze band. Use the AtmosphereThicknessMul / FogHeight* / "
                + "GroundColorBlendToSky sliders below to tweak in-game and watch the horizon react. "
                + "Default OFF (does nothing until you flip it on and adjust a slider).");

            configFiresSkyboxAtmosphereThicknessMul = config.Bind(
                "FiresWater", "SkyboxAtmosphereThicknessMul",
                1.0f,
                new ConfigDescription(
                    "Multiplier applied to the captured original _AtmosphereThickness when SkyboxOverride "
                    + "is true. 1.0 = unchanged. 0.0 = no atmospheric scattering (sharp horizon line). "
                    + "> 1.0 = thicker atmosphere (heavier horizon haze).",
                    new AcceptableValueRange<float>(0f, 5f)));

            configFiresSkyboxFogHeightMinOffset = config.Bind(
                "FiresWater", "SkyboxFogHeightMinOffset",
                0f,
                new ConfigDescription(
                    "World-meters offset added to the captured _FogHeightMin. Push positive to move the "
                    + "bottom of the height-fog band higher (e.g. above sea level Y=30 so the band stops "
                    + "mixing with horizon waves).",
                    new AcceptableValueRange<float>(-500f, 500f)));

            configFiresSkyboxFogHeightMaxOffset = config.Bind(
                "FiresWater", "SkyboxFogHeightMaxOffset",
                0f,
                new ConfigDescription(
                    "World-meters offset added to the captured _FogHeightMax. Pair with FogHeightMinOffset "
                    + "to translate the entire fog band up or down without changing its width.",
                    new AcceptableValueRange<float>(-500f, 500f)));

            configFiresSkyboxGroundColorBlendToSky = config.Bind(
                "FiresWater", "SkyboxGroundColorBlendToSky",
                0f,
                new ConfigDescription(
                    "Lerp factor from captured _GroundColor toward captured _SkyTint. 0.0 = unchanged "
                    + "(vanilla colors, visible horizon-color discontinuity). 1.0 = ground color exactly "
                    + "matches sky tint (kills the discontinuity entirely).",
                    new AcceptableValueRange<float>(0f, 1f)));
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
