using BepInEx.Configuration;

namespace VerdantsAscent.Terrain.Voxel
{
    // The whole-world terrain mode (Stage W in the plan). Vanilla = untouched
    // vanilla heightmap zones (safe default for release). VoxelWrap = the W1+
    // streamer wraps each zone's live vanilla heightfield as voxel density and
    // carves procedural features FROM it (caves/grottos/riverbeds) — vanilla
    // surface, locations and biomes stay valid because we only subtract.
    public enum VoxelWorldMode { Vanilla, VoxelWrap, VoxelOnly }

    // W4 base-generation gate. Auto = worlds named "Fires*" use the Fires
    // generator, everything else stays vanilla (deterministic on every peer
    // with zero network sync — the world name arrives in the handshake).
    // ForceOn exists for renamed test worlds; NEVER use it on a world that
    // already has builds — the terrain shifts wholesale.
    public enum FiresWorldGenMode { Auto, ForceOn, Off }

    // Voxel config. The whole module is additive + gated so it can be switched
    // off without touching the tile terrain or water systems.
    // Plan: Tools/FIRES_VOXEL_TERRAIN_PLAN.md
    public static class FiresVoxelConfig
    {
        private const string S = "VoxelTerrain (P0 prototype)";
        private const string SW = "VoxelTerrain World";

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> LogTimings;
        public static ConfigEntry<int> MaxLoadedChunks;

        // Stage W master switch — DESIGNED IN FROM THE START (user directive):
        // the whole-world voxel wrap must always be a config choice. Default
        // VoxelWrap while we build/test; flips to Vanilla as the safe default
        // before any public release. The admin BRUSH tools work in BOTH modes
        // (they are sparse, additive and independent of the streamer).
        public static ConfigEntry<VoxelWorldMode> WorldMode;
        public static ConfigEntry<bool> WrapAllLoaded;
        public static ConfigEntry<float> WrapRadius;
        public static ConfigEntry<bool> GenerateCaves;
        public static ConfigEntry<float> CaveMinY;
        public static ConfigEntry<float> CaveMaxDepth;
        public static ConfigEntry<float> CaveWidth;
        public static ConfigEntry<float> CaveFrequency;
        public static ConfigEntry<float> CaveFrequencyY;
        public static ConfigEntry<float> CaveSurfacePinch;
        public static ConfigEntry<bool> CaveEnvEnabled;
        public static ConfigEntry<string> CaveEnvironment;
        public static ConfigEntry<bool> SeamLips;
        public static ConfigEntry<bool> FrontierLips;
        public static ConfigEntry<bool> SeamAuditAuto;
        public static ConfigEntry<float> BrushDrawSpeed;
        public static ConfigEntry<bool> FarQuiltEnabled;
        public static ConfigEntry<float> FarQuiltNearRadius;
        public static ConfigEntry<float> FarQuiltFarRadius;
        public static ConfigEntry<FiresWorldGenMode> WorldGenMode;
        public static ConfigEntry<int> WorldGenResolution;
        public static ConfigEntry<int> WorldGenDroplets;
        public static ConfigEntry<string> WorldGenVahcBase;
        public static ConfigEntry<string> WorldGenImageBase;
        public static ConfigEntry<float> WorldGenImageMinY;
        public static ConfigEntry<float> WorldGenImageMaxY;
        public static ConfigEntry<string> WorldGenBiomeImage;
        public static ConfigEntry<bool> HydrologyEnabled;
        public static ConfigEntry<bool> HydrologyPlaceWater;
        public static ConfigEntry<float> RiverThreshold;
        public static ConfigEntry<float> RiverMaxDepth;
        public static ConfigEntry<float> SpringSpacing;
        public static ConfigEntry<float> ClutterDensityScale;
        public static ConfigEntry<float> GrassRenderDistance;
        public static ConfigEntry<float> ImpostorRadius;
        public static ConfigEntry<float> ImpostorForwardRadius;
        public static ConfigEntry<float> ImpostorNearSkip;
        public static ConfigEntry<float> ImpostorDensity;

        private static bool _bound;

        public static void Bind(ConfigFile cfg)
        {
            if (_bound || cfg == null) return;
            _bound = true;

            Enabled = cfg.Bind(S, "Enabled", true,
                "Voxel carve/fill tools (P0 prototype — runtime only, NOT persisted yet; relog clears carves).");
            const string PERF = "VoxelTerrain Performance";
            ClutterDensityScale = cfg.Bind(PERF, "Clutter density scale", 0.55f,
                new ConfigDescription("Multiplies grass/clutter instance count (ClutterSystem m_amountScale). 1 = vanilla; lower = fewer blades = far fewer tris. Hot-reloads on the next patch regen.",
                    new AcceptableValueRange<float>(0.1f, 1.5f)));
            GrassRenderDistance = cfg.Bind(PERF, "Grass render distance", 0f,
                new ConfigDescription("Override grass render distance in metres (ClutterSystem m_distance); grass tris scale with the SQUARE of this. 0 = keep vanilla. Try 60-80 to trim the carpet.",
                    new AcceptableValueRange<float>(0f, 160f)));
            ImpostorRadius = cfg.Bind(PERF, "Impostor radius", 2400f,
                new ConfigDescription("Far billboard trees/rocks draw radius (metres) around the player, all directions.",
                    new AcceptableValueRange<float>(600f, 6000f)));
            ImpostorForwardRadius = cfg.Bind(PERF, "Impostor forward radius", 3000f,
                new ConfigDescription("Far billboards extend to THIS radius in the direction you're looking (fades out to the sides), so you see trees/rocks up a distant mountain without paying for them behind you. Must be >= Impostor radius.",
                    new AcceptableValueRange<float>(600f, 10000f)));
            ImpostorNearSkip = cfg.Bind(PERF, "Impostor near skip", 300f,
                new ConfigDescription("Billboards start THIS far out (metres), past where the real trees render — closer impostors just hide behind the real forest and waste frames.",
                    new AcceptableValueRange<float>(96f, 800f)));
            ImpostorDensity = cfg.Bind(PERF, "Impostor density", 0.85f,
                new ConfigDescription("Scales how many billboards get placed. Lower = a thinner, scattered distant-forest feel (and cheaper); higher = continuous distant treeline coverage (distant mountains/forests read as covered, not sparse).",
                    new AcceptableValueRange<float>(0.1f, 1.5f)));
            LogTimings = cfg.Bind(S, "Log timings", false,
                "Log per-chunk seed/mesh/bake/apply milliseconds (measurement runs only — thousands of lines per session, and the logging itself costs frames).");
            MaxLoadedChunks = cfg.Bind(S, "Max loaded chunks", 2048,
                new ConfigDescription("Safety cap on live voxel chunks (32m cubes, ~83KB each). A capped-out brush would skip chunks and leave holes, so brushes warn loudly when the cap bites.",
                    new AcceptableValueRange<int>(16, 8192)));

            WorldMode = cfg.Bind(SW, "World Terrain Mode", VoxelWorldMode.VoxelWrap,
                "Whole-world terrain system. Vanilla = untouched vanilla zones. VoxelWrap = zones near the " +
                "player are wrapped: the vanilla heightfield becomes voxel terrain (same surface, same biomes, " +
                "same locations) with deterministic procedural caves carved from it, and the vanilla heightmap " +
                "render+collider are hidden per zone. Toggling back to Vanilla unwinds live. Voxel BRUSH tools " +
                "work in both modes. Generated caves are pure functions of the world seed — they regenerate " +
                "identically; player edit deltas persist independently (P1).");
            WrapAllLoaded = cfg.Bind(SW, "Wrap All Loaded Zones", true,
                "Wrap EVERY zone as it streams in (login + exploration), ignoring Wrap Radius. Zones only " +
                "unwrap when they unload. Off = radius-gated wrapping around the player.");
            WrapRadius = cfg.Bind(SW, "Wrap Radius", 160f,
                new ConfigDescription("Zones whose center is within this many metres of the player get wrapped (Chebyshev). 160 ≈ 5x5 zones — pushes the voxel↔vanilla frontier past the visible detail range.",
                    new AcceptableValueRange<float>(48f, 320f)));
            GenerateCaves = cfg.Bind(SW, "Generate Caves", true,
                "Carve deterministic spaghetti-cave networks under the surface of wrapped zones.");
            CaveMinY = cfg.Bind(SW, "Cave Min Y", 34f,
                new ConfigDescription("No caves below this world height. Default sits just above the y=30 water table — flooded caves come with the voxel-water milestone.",
                    new AcceptableValueRange<float>(-200f, 200f)));
            CaveMaxDepth = cfg.Bind(SW, "Cave Max Depth", 48f,
                new ConfigDescription("Caves only carve within this many metres below the surface.",
                    new AcceptableValueRange<float>(8f, 128f)));
            CaveWidth = cfg.Bind(SW, "Cave Width", 0.05f,
                new ConfigDescription("Noise band width — bigger = fatter, more frequent tunnels (0.05 ≈ 4m radius).",
                    new AcceptableValueRange<float>(0.01f, 0.2f)));
            CaveFrequency = cfg.Bind(SW, "Cave Frequency", 0.025f,
                new ConfigDescription("Horizontal noise frequency — smaller = longer, straighter tunnel systems.",
                    new AcceptableValueRange<float>(0.005f, 0.1f)));
            CaveFrequencyY = cfg.Bind(SW, "Cave Frequency Y", 0.04f,
                new ConfigDescription("Vertical noise frequency — higher than horizontal keeps tunnels flat-ish and walkable.",
                    new AcceptableValueRange<float>(0.005f, 0.15f)));
            CaveSurfacePinch = cfg.Bind(SW, "Cave Surface Pinch", 6f,
                new ConfigDescription("Tunnels pinch shut within this many metres of the surface; occasional breakthroughs become natural entrances.",
                    new AcceptableValueRange<float>(1f, 24f)));
            CaveEnvEnabled = cfg.Bind(SW, "Cave Environment Enabled", true,
                "Force a cave environment (dark/fog) while the player stands under a VOXEL ceiling (honest up-raycast; replaces the old underside-mesh trick). Underwater env wins while swimming.");
            CaveEnvironment = cfg.Bind(SW, "Cave Environment", "Crypt",
                "EnvMan environment name forced inside voxel caves (vanilla examples: Crypt, Caves, SunkenCrypt).");

            FarQuiltEnabled = cfg.Bind(SW, "Far Quilt Enabled", true,
                "W3-A4 — draw far terrain from OUR generator (render-only tiles in the voxel terrain " +
                "material) and stop rendering vanilla distant-LOD heightmaps. Off = vanilla distant LOD " +
                "renders our heights as fallback.");
            FarQuiltNearRadius = cfg.Bind(SW, "Far Quilt Near Radius", 384f,
                new ConfigDescription("Per-zone 64m quilt tiles out to this range (they also cover zones still " +
                    "streaming in). Voxel-solid zones hide their tile automatically.",
                    new AcceptableValueRange<float>(128f, 1024f)));
            FarQuiltFarRadius = cfg.Bind(SW, "Far Quilt Far Radius", 1536f,
                new ConfigDescription("256m supertiles out to this range beyond the near ring. Ocean-floor " +
                    "supertiles that never break the waterline are skipped.",
                    new AcceptableValueRange<float>(512f, 4096f)));
            SeamLips = cfg.Bind(SW, "Seam Lips (legacy)", false,
                "Legacy zone-edge shingle lips. Superseded by canonical border ownership (rings mirror the " +
                "owning chunk, so boundary geometry is bit-identical on both sides). Leave OFF; enable only " +
                "if residual hairlines appear, and report them (va_voxel_seamscan says which planes diverge).");
            FrontierLips = cfg.Bind(SW, "Frontier Lips", false,
                "Emit shingle lips on faces with no covering neighbour (wrap frontier). They seal the " +
                "half-cell trench at the loaded-world edge but render as bright ribbons on slopes and " +
                "wherever a neighbour zone is slow to swap — leave OFF unless the frontier trench bothers " +
                "you more than the ribbons.");
            SeamAuditAuto = cfg.Bind(SW, "Seam Audit Auto", false,
                "Background MESH-truth seam sentinel: every 8s, compare the actual rendered vertex buffers " +
                "of adjacent chunks near the player and log any face with tears/holes/shading mismatches " +
                "plus full forensics. Silent when clean. va_voxel_seamaudit runs it on demand at any radius.");
            BrushDrawSpeed = cfg.Bind(SW, "Brush Draw Speed", 0.5f,
                new BepInEx.Configuration.ConfigDescription(
                    "How fast held-click brush strokes deposit/remove material (terrain AND water tools). " +
                    "1 = full speed (the old behavior that got away from you), lower = proportionally fewer " +
                    "dabs per frame for precise work. Also settable in-game via va_brush_drawspeed.",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0.05f, 1f)));
            VerdantsAscent.Terrain.Persistence.TileBrushModes.DrawSpeed = BrushDrawSpeed.Value;
            BrushDrawSpeed.SettingChanged += (_, __) =>
                VerdantsAscent.Terrain.Persistence.TileBrushModes.DrawSpeed = BrushDrawSpeed.Value;
            WorldGenMode = cfg.Bind(SW, "World Generation", FiresWorldGenMode.Auto,
                "W4 — the Fires base worldgen (FBM continents + ridged ranges + droplet erosion replacing " +
                "WorldGenerator.GetBaseHeight; vanilla river carving off). Auto = only worlds whose NAME starts " +
                "with 'Fires' (deterministic on every peer, no sync needed — server AND clients must both run " +
                "this mod). ForceOn = every non-menu world (NEVER on a world with existing builds — terrain " +
                "shifts wholesale). Off = vanilla generation everywhere.");
            WorldGenResolution = cfg.Bind(SW, "WorldGen Resolution", 10501,
                new ConfigDescription("Base heightfield grid size (cells per side over the 21km world). 10501 = 2m " +
                    "cells — fine enough to carve 2m streams and 4-10m rivers NATIVELY (the whole point: the old " +
                    "2048/10m grid could not represent water narrower than a cell). The hydrology is resolution-" +
                    "independent, so this only changes fidelity + first-gen time (~2.5min at 2m, cached forever after). " +
                    "MUST match on server and all clients (they each generate the world deterministically). Changing " +
                    "this on an existing Fires world regenerates a DIFFERENT cache — leave it alone once a world exists.",
                    new AcceptableValueRange<int>(512, 12000)));
            WorldGenDroplets = cfg.Bind(SW, "WorldGen Erosion Droplets", 400000,
                new ConfigDescription("Droplet-erosion budget carving dendritic valley networks into the base grid at " +
                    "first generation, expressed at the 2048 REFERENCE grid — the engine scales it with the cell count " +
                    "so erosion DENSITY stays constant at any resolution (400000 → ~10.5M droplets at 2m). More = " +
                    "deeper filigree, longer first load. Cached after first build.",
                    new AcceptableValueRange<int>(0, 8000000)));
            WorldGenVahcBase = cfg.Bind(SW, "WorldGen VAHC Base", "",
                "Optional EXACT base map: filename or full path of a VAHC v2 heightcache (.bin). Relative names " +
                "resolve against config/FiresRPGmaker/Debug/Cache (the FUC one-root cache). When set, this map " +
                "replaces the random generator (erosion droplets still apply once). The file must exist on the " +
                "SERVER and EVERY CLIENT before the world's first load, or peers desync. Empty = the Fires random generator.");
            WorldGenImageBase = cfg.Bind(SW, "WorldGen Image Base", "",
                "Greyscale heightmap PNG (16-bit preferred, BC convention) used as the EXACT base map. Relative " +
                "paths resolve against config/FiresRPGmaker/Debug/Cache. Erosion + river carve still apply — paint " +
                "the macro design, rivers are derived. Must exist on SERVER and EVERY CLIENT before first load. " +
                "Takes precedence over VAHC Base.");
            WorldGenImageMinY = cfg.Bind(SW, "WorldGen Image Min Y", -40f,
                "World height (metres) for image BLACK. Water level is 30.");
            WorldGenImageMaxY = cfg.Bind(SW, "WorldGen Image Max Y", 200f,
                "World height (metres) for image WHITE.");
            WorldGenBiomeImage = cfg.Bind(SW, "WorldGen Biome Image", "",
                "Biome-color PNG (BetterContinents palette: Meadows 00FF00, BlackForest 007F00, Swamp 7F7F00, " +
                "Mountain FFFFFF, Plains FFFF00, Mistlands 7F7F7F, AshLands FF0000, DeepNorth 00FFFF, Ocean " +
                "0000FF). BLACK keeps the terrain-derived biome — paint only where you want control. Relative " +
                "paths resolve against config/FiresRPGmaker/Debug/Cache; needed on server AND all clients.");
            HydrologyEnabled = cfg.Bind(SW, "Hydrology Enabled", true,
                "W2 — bake world hydrology at login (flow accumulation over the world heightfield) and CARVE " +
                "river beds into wrapped voxel terrain. Deterministic from the world seed.");
            HydrologyPlaceWater = cfg.Bind(SW, "Hydrology Place Water", true,
                "Auto-place water in wrapped zones: bank-following SPRING pours at river headwaters and " +
                "settling water volumes on ponds. Idempotent (skips where a pour already exists).");
            RiverThreshold = cfg.Bind(SW, "River Threshold", 260f,
                new ConfigDescription("Catchment (hydrology cells) required before a river carves. Lower = more/denser streams.",
                    new AcceptableValueRange<float>(40f, 4000f)));
            RiverMaxDepth = cfg.Bind(SW, "River Max Depth", 6f,
                new ConfigDescription("Deepest bed carve (metres) for the largest rivers.",
                    new AcceptableValueRange<float>(1.5f, 16f)));
            SpringSpacing = cfg.Bind(SW, "Spring Spacing", 140f,
                new ConfigDescription("Minimum metres between auto-placed springs.",
                    new AcceptableValueRange<float>(48f, 512f)));
        }
    }
}
