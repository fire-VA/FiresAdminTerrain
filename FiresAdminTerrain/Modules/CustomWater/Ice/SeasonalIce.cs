using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // ── 0.2.1496 WINTER SPLIT, step 1: freeze (Fire 11-19: "all special winter code [should] live in ONLY vedr"; plan Tools\PLAN_winter_split_vedr_fat_20261119.md;
    // seam agreed with [seasons]) ──
    // The SEASONAL ice decision (which biomes' water can freeze now, the world freeze exposure F, and the exposure x depth -> ice rule, plus the
    // "snowing" gate for frost) comes from a provider. Vedr registers its provider through FatApi.RegisterSeasonalIce. Until one is registered,
    // FAT's built-in provider (IceField's exposure integration + VanillaIce's season rule, the code as before) answers, so nothing changes.
    // Phase B deletes the built-in seasonal code once Vedr's provider ships.
    // What FAT keeps whatever the provider: BIOME ice (Deep North is always frozen, Fire: "deep north water is meant to freeze regardless of
    // vedr because of the biome itself"), and the physical facts of FAT's water: caves never freeze (WaterStateField roof test), fires melt
    // (FatFireBullseye), broken holes (IceBreaks: F - fb), frost cells, the walkable threshold, look, colliders, no-liquid.
    // Shown ice = Deep North biome ice, or the seasonal ice elsewhere (WaterStateField.Sample / IceDetail): the max of the two per column.
    internal static class SeasonalIce
    {
        internal const int ApiVersion = 1;

        private static Func<int> s_biomeMask, s_revision;
        private static Func<float> s_exposure;
        private static Func<float, float, bool, Vector2> s_derive;
        private static Func<bool> s_snowing;
        private static string s_owner;
        private static int s_lastRev = int.MinValue, s_lastMask = int.MinValue;

        /// <summary>A provider is registered: every seasonal answer is its, FAT's built-in season / exposure code stands down.</summary>
        internal static bool External => s_derive != null;
        internal static string Owner => s_owner ?? "FAT built-in";

        internal static bool Register(string owner, Func<int> biomeMask, Func<float> exposure, Func<float, float, bool, Vector2> derive, Func<int> revision, Func<bool> snowing, out string report)
        {
            if (biomeMask == null || exposure == null || derive == null || revision == null)
            {
                report = "RegisterSeasonalIce refused: biomeMask, exposure, derive and revision are required (snowing may be null).";
                FiresLogger.LogWarning("[SeasonalIce] " + report);
                return false;
            }
            s_owner = string.IsNullOrEmpty(owner) ? "unnamed" : owner;
            s_biomeMask = biomeMask; s_exposure = exposure; s_revision = revision; s_snowing = snowing; s_derive = derive;
            s_lastRev = int.MinValue; s_lastMask = int.MinValue;
            report = $"seasonal ice provider '{s_owner}' registered: FAT's built-in season / exposure / weather rule stands down; Deep North biome ice, caves, fire melt, holes and frost stay FAT's (0.2.1496)";
            FiresLogger.LogInfo("[SeasonalIce] " + report);
            IceField.BumpRevision();
            if (ZNet.instance != null && ZNet.World != null) WaterStateField.IceChanged();   // registered mid-world: the ice follows now; at load the first tiles read it
            return true;
        }

        // ── the provider's answers (any thread unless noted); each falls back safely if the provider throws ──
        internal static int BiomeMask { get { try { return s_biomeMask(); } catch { return 0; } } }

        internal static float Exposure { get { try { float f = s_exposure(); return float.IsNaN(f) || float.IsInfinity(f) ? 0f : Mathf.Max(0f, f); } catch { return 0f; } } }

        internal static void Derive(float f, float depth, bool river, out float cover, out float thick)
        {
            Vector2 r;
            try { r = s_derive(f, depth, river); } catch { r = Vector2.zero; }
            cover = float.IsNaN(r.x) ? 0f : Mathf.Clamp01(r.x);
            thick = float.IsNaN(r.y) ? 0f : Mathf.Max(0f, r.y);
        }

        internal static int Revision { get { try { return s_revision(); } catch { return 0; } } }

        /// <summary>Server, main thread: frost may grow. Null provider gate = not snowing.</summary>
        internal static bool Snowing { get { try { return s_snowing != null && s_snowing(); } catch { return false; } } }

        /// <summary>MAIN thread, from IceField.Tick (2 s gate): a provider revision bump refills / remeshes like an ice step.</summary>
        internal static void Tick()
        {
            if (!External) return;
            int rev = Revision;
            if (rev == s_lastRev) return;
            int mask = BiomeMask;
            bool maskChanged = s_lastMask != int.MinValue && mask != s_lastMask;
            bool first = s_lastRev == int.MinValue;
            s_lastRev = rev; s_lastMask = mask;
            IceField.BumpRevision();
            if (!first) WaterStateField.IceChanged();   // the derived ice everywhere follows the new exposure (the tile drop on a mask change runs through VanillaIce.SeasonSignature)
            FiresLogger.LogDebug($"[SeasonalIce] provider '{s_owner}' revision {rev}: exposure {Exposure:0.00}, biome mask {(Heightmap.Biome)mask}{(maskChanged ? " (changed)" : "")} (0.2.1496)");
        }
    }
}
