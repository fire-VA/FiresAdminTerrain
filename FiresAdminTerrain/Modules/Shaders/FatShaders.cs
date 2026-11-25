using System.Collections.Generic;
using BepInEx.Configuration;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1478 (Fire 11-18: "If any of the mods functions rely on a shader provided by the mod to work, then they should be gated
    // by that shader being toggled on. And they should say so plainly"; "The features will stand down when the shader is not
    // toggled on"): one key per FAT shader in [Shaders]. A feature that draws with one of them asks On(...) and stands down
    // (no stock fallback) when its key is off or the shader did not load, with one Info line per change.
    // Defaults are true, so nothing changes for anyone; a saved value is never rewritten.
    // FAT's voxel terrain shader (Custom/FiresVoxelHeightmap) has no key: voxel ground cannot draw without it, so it is always on.
    internal static class FatShaders
    {
        internal const string Section = "Shaders";

        internal const string FarRingKey = "Far ring shader";
        internal const string FarSunShadeKey = "Far sun shade shader";
        internal const string InlandWaterKey = "Inland water shader";
        internal const string LiquidKey = "Liquid shader";
        internal const string SkyRainbowKey = "Sky rainbow shader";
        internal const string WaterfallMistKey = "Waterfall mist shader";
        internal const string WaterfallKey = "Waterfall shader";

        // Description text for FAT's voxel keys (the voxel shader has no key).
        internal const string VoxelShaderAlwaysOn = "Needs FAT's voxel terrain shader (Custom/FiresVoxelHeightmap), which is always on: voxel ground cannot draw without it.";

        internal static ConfigEntry<bool> FarRing, FarSunShade, InlandWater, Liquid, SkyRainbow, WaterfallMist, Waterfall;

        /// <summary>"Needs [Shaders] key = true." for a dependent key's description.</summary>
        internal static string Needs(string key) => $"Needs [{Section}] {key} = true.";

        private static bool s_bound;

        internal static void Bind(ConfigFile cfg)
        {
            if (s_bound || cfg == null) return;
            s_bound = true;
            // 0.2.1480: the two far billboard shader keys left with FAT's far billboards (Fire 11-18); saved lines stay, unread.
            FarRing = cfg.Bind(Section, FarRingKey, true,
                "Custom/FiresHaloFar. Off: the far side of the ring on halo worlds (and its far tree silhouettes) stands down. Applies at once.");
            FarSunShade = cfg.Bind(Section, FarSunShadeKey, true,
                "Hidden/FAT/FarSunShade. Off: [Far Shadows] Mountain shadows and Canopy shade stand down. Applies at once.");
            InlandWater = cfg.Bind(Section, InlandWaterKey, true,
                "Custom/FiresInlandWater (shipped in Core's shader bundle, used only by FAT). Off: FAT's river, lake and placed water surfaces stand down (not drawn; swimming is unchanged), and every [FiresInlandWater *], [Inland look *] and [Ocean - Inland look] key does nothing. Takes effect on the next world load.");
            Liquid = cfg.Bind(Section, LiquidKey, true,
                "Custom/FiresLiquid (shipped in Core's shader bundle, used only by FAT). Off: FAT's poured liquid water stands down (not drawn; swimming is unchanged) and every [FiresLiquid Look] key does nothing. Takes effect on the next world load.");
            SkyRainbow = cfg.Bind(Section, SkyRainbowKey, true,
                "Custom/FiresRainbowSky. Off: [Rainbow] Sky rainbow stands down. Applies at once.");
            WaterfallMist = cfg.Bind(Section, WaterfallMistKey, true,
                "Custom/FiresWaterfallMist. Off: [Voxel Terrain - World] Waterfall Mist (and the rainbow in the mist) stands down. Applies at once.");
            Waterfall = cfg.Bind(Section, WaterfallKey, true,
                "Custom/FiresWaterfall. Off: falling water curtains stand down (not drawn) and [Voxel Terrain - World] Waterfall Brightness does nothing. Takes effect on the next world load.");
        }

        private static readonly Dictionary<string, string> s_said = new Dictionary<string, string>();

        /// <summary>
        /// True when the feature may draw: its [Shaders] key is on (or not bound yet) and its shader loaded.
        /// One Info line each time the answer (or the reason) changes for that feature.
        /// </summary>
        internal static bool On(ConfigEntry<bool> key, bool shaderLoaded, string feature, string shaderName)
        {
            string why = key != null && !key.Value ? $"needs [{Section}] {key.Definition.Key} = true"
                       : !shaderLoaded ? $"its shader {shaderName} did not load or is not supported on this GPU"
                       : null;
            string state = why ?? "";
            if (!s_said.TryGetValue(feature, out var was) || was != state)
            {
                bool first = was == null;
                s_said[feature] = state;
                if (why != null) FiresLogger.LogInfo($"[FAT] {feature} stood down: {why}.");
                else if (!first) FiresLogger.LogInfo($"[FAT] {feature} back on ({shaderName}).");
            }
            return why == null;
        }
    }
}
