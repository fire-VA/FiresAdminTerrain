using System;

namespace VerdantsAscent.Terrain
{
    // 0.2.1059 ([classes-ui]'s load/save audit M1-M5, after Fire's "Error loading world file"): no FAT step on the world
    // load / create / startup path may throw out of its patch. A throw there aborts vanilla's load (or skips every later
    // mod's postfix), so each step runs here: a failure is one Warning naming the step, and vanilla carries on.
    // A deliberate refusal (FiresWorldLoadGuard setting ZNet.m_loadError) is not an exception and is untouched.
    internal static class FatLoadGuard
    {
        internal static bool Step(string name, Action step)
        {
            try { step(); return true; }
            catch (Exception ex) { Failed(name, ex); return false; }
        }

        internal static void Failed(string name, Exception ex)
        {
            if (ex is AggregateException agg && agg.InnerException != null) ex = agg.GetBaseException();
            FiresLogger.LogWarning($"[FAT] load-path step '{name}' failed ({ex.GetType().Name}: {ex.Message}); skipped, vanilla continues");
        }
    }
}
