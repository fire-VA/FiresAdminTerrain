using BepInEx.Configuration;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1102 (overnight #9, Fire: "our excessive logging alone is a perf hit ... quieted ... so we can toggle them back on"):
    // per-feature diagnostic switches, all OFF by default. A line behind one of these is only built (string and all) when its
    // switch is on AND Core's Debug log level is on. Nothing is deleted; turn a switch on to get the line back. One-shot
    // "executing"/"bound" lines, warnings, errors and the per-feature summary lines Fire asked for stay on without a switch.
    internal static class FatDiag
    {
        private const string S = "FiresAdminTerrain 99 Diagnostics";
        private static ConfigEntry<bool> s_terraform, s_waterClutter, s_opStorm, s_opStormStacks, s_clutterGate, s_snow, s_waterField, s_biomePaint, s_perfCensus, s_looks;

        internal static void Bind(ConfigFile cfg)
        {
            s_terraform = B(cfg, "Terraform", "Voxel terraform lines (terraform: zone ... heightmap changed / visible / rebuilt with no chunk, applied chunk ... from).");
            s_waterClutter = B(cfg, "Water clutter", "Underwater clutter cells, [WaterLife] and [FiresWater] clutter refresh / wet re-stamp lines.");
            s_opStorm = B(cfg, "Op storm", "[OpStorm] per-op send/receive lines (the 60 s 'dropped' summary stays on without this).");
            s_opStormStacks = B(cfg, "Op storm stacks", "Add the managed call stack to [OpStorm] send lines (needs 'Op storm'). Large.");
            s_clutterGate = B(cfg, "Clutter gate", "[ClutterGate] GenerateVegPatch 30 s summaries and DoOperation / ResetGrass reset lines.");
            s_snow = B(cfg, "Snow", "[VoxelSnow] bound / rebind, [SnowSeam], [SnowTrail] trench-at-player and spawned lines.");
            s_waterField = B(cfg, "Water field", "[WaterField] chunk, [WaterEdge], [FatWaterLevel] lines.");
            s_biomePaint = B(cfg, "Biome paint", "[BiomePaint] tint / deep-ground lines and [FatSuperFlat] per-entry biome lines.");
            s_perfCensus = B(cfg, "Perf census", "[FatPerf] draws / objects census, [FatSpike] spike lines.");
            s_looks = B(cfg, "Looks", "[Rainbow] per-show lines, [Cultivated] per-op lines, [VoxelZoneLook] per-zone lines.");
        }

        private static ConfigEntry<bool> B(ConfigFile cfg, string key, string what)
            => cfg.Bind(S, key, false, "Diagnostic log lines (player-facing: no). OFF = quiet. ON (with Core's Debug log level) = " + what);

        private static bool On(ConfigEntry<bool> e) => e != null && e.Value && FiresLogger.DebugEnabled;
        internal static bool Terraform => On(s_terraform);
        internal static bool WaterClutter => On(s_waterClutter);
        internal static bool OpStorm => On(s_opStorm);
        internal static bool OpStormStacks => On(s_opStorm) && s_opStormStacks != null && s_opStormStacks.Value;
        internal static bool ClutterGate => On(s_clutterGate);
        internal static bool Snow => On(s_snow);
        internal static bool WaterField => On(s_waterField);
        internal static bool BiomePaint => On(s_biomePaint);
        internal static bool PerfCensus => On(s_perfCensus);
        internal static bool Looks => On(s_looks);
    }
}
