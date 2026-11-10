using System;

namespace VerdantsAscent
{
    // Types from optional mods (FiresTossinShade, ValheimGalaxies), found WITHOUT HarmonyX's AccessTools.TypeByName: that call logs
    // a warning on every miss, and a soft dependency that is not installed is a normal case, not something to warn about.
    internal static class FatSoftTypes
    {
        // Every optional mod has loaded by the time the first minute of the game has passed; after that a missing type stays missing.
        internal const float GiveUpAfterSeconds = 60f;

        internal static Type Find(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName, false); }
                catch (Exception) { continue; }   // a dynamic or half-loaded assembly
                if (type != null) return type;
            }
            return null;
        }
    }
}
