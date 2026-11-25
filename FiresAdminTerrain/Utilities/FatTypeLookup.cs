using System;
using System.Reflection;

namespace VerdantsAscent
{
    // 0.2.1503 ([2ic] / [lead] option C): FAT looked up Core's types (and one of its own) by name with HarmonyX AccessTools.TypeByName. That first
    // tries Type.GetType(name) from HarmonyX's own assembly (misses: the type lives in Core / FAT), then walks AllTypes() - every loaded
    // assembly's GetTypes() - by full name, then by short name, and logs a Warn on a miss (decompiled 0Harmony 2.9.0.0 AccessTools.TypeByName).
    // FAT references Core at compile time ([BepInDependency(FiresCore.FiresUnifiedCore.PluginGUID, "0.2.816")]: BepInEx never loads FAT
    // without Core), so the type is asked of Core's own assembly directly: same Type when it exists, null when this Core has no such type
    // (an older Core), never a throw. The only difference: no "Could not find type named" Warn line on a miss.
    internal static class FatTypeLookup
    {
        private static readonly Assembly s_core = typeof(FiresCore.FiresUnifiedCore).Assembly;
        private static readonly Assembly s_fat = typeof(FatTypeLookup).Assembly;

        /// <summary>A type in FiresUnifiedCore by full name, or null (no scan of other assemblies).</summary>
        internal static Type Core(string fullName) => s_core.GetType(fullName, false);

        /// <summary>A type in FAT itself by full name, or null.</summary>
        internal static Type Fat(string fullName) => s_fat.GetType(fullName, false);
    }
}
