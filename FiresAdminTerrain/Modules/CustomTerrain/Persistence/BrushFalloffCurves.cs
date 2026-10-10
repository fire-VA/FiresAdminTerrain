using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    /// <summary>
    /// 2026-05-26 round-31 PHASE B-4 — Blender brush falloff curve library.
    ///
    /// Direct port of Blender's `BKE_brush_curve_strength` presets
    /// (`source/blender/blenkernel/intern/brush.cc:2463-2528`). Input is
    /// `p = 1 - distance/radius` (1 at brush center, 0 at edge); output
    /// is the falloff WEIGHT used to scale brush intensity at that vertex.
    ///
    /// All curves are continuous in `[0, 1]` and return 0 at the edge
    /// (`p=0`) and 1 at the center (`p=1`) so they can be swapped without
    /// changing brush calibration. Some (Smoother / Sphere) have zero
    /// 1st and 2nd derivatives at the endpoints — those produce
    /// "invisible blend into untouched terrain" looks at the brush edge.
    ///
    /// Default is `Smooth` (classic Hermite smoothstep, `3p² - 2p³`).
    /// Matches what Blender uses for Draw / Clay / Inflate / Grab — the
    /// "doesn't feel like there's an edge" curve. Tools can opt into
    /// other presets per-op (or globally via config) without rewriting
    /// their brush kernels.
    /// </summary>
    public static class BrushFalloffCurves
    {
        public enum Preset
        {
            /// <summary>Hermite smoothstep `3p² - 2p³`. Blender default.</summary>
            Smooth = 0,
            /// <summary>Perlin smootherstep `p³(p(6p-15)+10)`. Zero first
            /// and second derivatives at both endpoints — stamps blend
            /// invisibly into untouched terrain.</summary>
            Smoother = 1,
            /// <summary>`p²`. Soft center, sharp falloff — chisel/strike feel.</summary>
            Sharp = 2,
            /// <summary>Linear `p`. No easing.</summary>
            Linear = 3,
            /// <summary>`sqrt(p)`. Wide soft hit at any distance.</summary>
            Root = 4,
            /// <summary>`1.0` constant. Inside the radius full strength, edge cliff.
            /// Equivalent to FAT's old hard-cutoff. Useful for square slabs.</summary>
            Constant = 5,
            /// <summary>`sqrt(2p - p²)`. Hemispherical dome — peaks naturally form.</summary>
            Sphere = 6,
            /// <summary>`p⁴`. Very pointy — sharp peaks.</summary>
            Pow4 = 7,
            /// <summary>`p · (2 - p)`. Inverse-square — soft falloff with broad shoulder.</summary>
            InvSquare = 8,
        }

        /// <summary>
        /// Evaluate the named curve at `p = 1 - distance/radius`.
        /// Returns clamped [0, 1] falloff weight.
        /// </summary>
        public static float Evaluate(Preset preset, float p)
        {
            p = Mathf.Clamp01(p);
            if (p <= 0f) return 0f;
            if (p >= 1f) return 1f;
            switch (preset)
            {
                case Preset.Smooth:    return p * p * (3f - 2f * p);
                case Preset.Smoother:  return p * p * p * (p * (6f * p - 15f) + 10f);
                case Preset.Sharp:     return p * p;
                case Preset.Linear:    return p;
                case Preset.Root:      return Mathf.Sqrt(p);
                case Preset.Constant:  return 1f;
                case Preset.Sphere:    return Mathf.Sqrt(Mathf.Max(0f, 2f * p - p * p));
                case Preset.Pow4:      return p * p * p * p;
                case Preset.InvSquare: return p * (2f - p);
                default:               return p * p * (3f - 2f * p); // Smooth fallback
            }
        }

        /// <summary>
        /// Convenience: evaluate at a world-space distance / radius pair.
        /// Returns 0 outside the radius.
        /// </summary>
        public static float EvaluateAtDist(Preset preset, float dist, float radius)
        {
            if (radius <= 0.0001f) return 0f;
            if (dist >= radius) return 0f;
            float p = 1f - dist / radius;
            return Evaluate(preset, p);
        }

        /// <summary>
        /// Map a string name (lowercase) to a Preset enum. Used by
        /// console commands that take `curve=smooth` style args.
        /// </summary>
        public static Preset Parse(string name)
        {
            if (string.IsNullOrEmpty(name)) return Preset.Smooth;
            switch (name.ToLowerInvariant())
            {
                case "smooth":    return Preset.Smooth;
                case "smoother":  return Preset.Smoother;
                case "sharp":     return Preset.Sharp;
                case "linear":    return Preset.Linear;
                case "lin":       return Preset.Linear;
                case "root":      return Preset.Root;
                case "constant":  return Preset.Constant;
                case "flat":      return Preset.Constant;
                case "sphere":    return Preset.Sphere;
                case "dome":      return Preset.Sphere;
                case "pow4":      return Preset.Pow4;
                case "invsquare": return Preset.InvSquare;
                case "invsq":     return Preset.InvSquare;
                default:          return Preset.Smooth;
            }
        }
    }
}
