using System;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    // The layered base-height noise a BetterContinents world may carry (BcWorldSettings.BaseHeightNoise), evaluated so a
    // converted world's ground matches the map it was made with. What a layer does, as the saved settings describe it:
    //  - a FastNoiseLite field, its input warped by a second FastNoiseLite (a default one when the layer names none);
    //  - its raw output rescaled to 0..1 by the range a 100x100 sweep across the whole world finds, so the size and the
    //    seed shape the scaling too;
    //  - then optional invert, smooth threshold, remap into a range, hard threshold and opacity, in that order;
    //  - combined with the height so far by one of the usual image blend modes, and optionally faded in by a mask field.
    // The order of every float operation is kept as the format needs it: the BcRefTest proof compares this ground with the
    // original mod's to the millimetre. Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal sealed class BcNoiseField
    {
        private const int SweepSide = 100;
        private const int WarpSeedShift = 127;
        private const int MaskSeedFactor = 107;
        private const int LayerSeedFactor = 31;
        private const int LayerSeedBase = 3;

        // One FastNoiseLite source with its warp, its 0..1 rescale and the post-processing its settings ask for.
        private sealed class Channel
        {
            internal FastNoiseLite Source;
            internal FastNoiseLite Warp;
            internal BcNoiseSettings Shape;
            internal float Lift;    // added to a raw sample before the rescale
            internal float Gain;    // the rescale factor

            internal float Sample(float x, float y)
            {
                y *= Shape.Aspect;
                Warp.DomainWarp(ref x, ref y);
                float v = (Source.GetNoise(x, y) + Lift) * Gain;
                if (Shape.Invert) v = 1f - v;
                if (Shape.UseSmoothThreshold) v = Mathf.InverseLerp(Shape.SmoothThresholdStart, Shape.SmoothThresholdEnd, v);
                if (Shape.UseRange) v = Mathf.Lerp(Shape.RangeStart, Shape.RangeEnd, v);
                if (Shape.UseThreshold) v = v < Shape.Threshold ? 0f : 1f;
                if (Shape.UseOpacity) v *= Shape.Opacity;
                return v;
            }
        }

        private readonly Channel[] _fields;
        private readonly Channel[] _masks;      // null entries: that layer is unmasked
        private readonly int[] _modes;

        public BcNoiseField(float worldSize, int worldSeed, BcNoiseStack stack, float globalScale)
        {
            int count = stack?.Layers?.Count ?? 0;
            _fields = new Channel[count];
            _masks = new Channel[count];
            _modes = new int[count];
            for (int i = 0; i < count; i++)
            {
                BcNoiseLayer layer = stack.Layers[i];
                int layerSeed = worldSeed * LayerSeedFactor * (i + LayerSeedBase);
                _fields[i] = Build(layer.Noise, layer.NoiseWarp, layerSeed, worldSize, globalScale);
                _masks[i] = layer.Mask == null ? null : Build(layer.Mask, layer.MaskWarp, layerSeed * MaskSeedFactor, worldSize, globalScale);
                _modes[i] = layer.Noise.BlendMode;
            }
        }

        public int LayerCount => _fields.Length;

        // The running height through every layer in turn.
        public float Apply(float x, float y, float height)
        {
            for (int i = 0; i < _fields.Length; i++)
            {
                float mixed = Mix(_modes[i], height, _fields[i].Sample(x, y));
                height = _masks[i] == null ? mixed : Mathf.Lerp(height, mixed, _masks[i].Sample(x, y));
            }
            return height;
        }

        private static Channel Build(BcNoiseSettings shape, BcNoiseSettings warpShape, int seed, float worldSize, float globalScale)
        {
            var channel = new Channel
            {
                Shape = shape,
                Source = Configure(shape, seed, globalScale),
                Warp = warpShape != null ? Configure(warpShape, seed + WarpSeedShift, globalScale) : new FastNoiseLite(),
            };
            // The rescale comes from the extremes of a regular sweep over the world square (raw source, no warp).
            float lowest = float.MaxValue, highest = float.MinValue;
            for (int row = 0; row < SweepSide; row++)
            {
                float sy = (row / (float)SweepSide - 0.5f) * worldSize;
                for (int col = 0; col < SweepSide; col++)
                {
                    float sx = (col / (float)SweepSide - 0.5f) * worldSize;
                    float raw = channel.Source.GetNoise(sx, sy);
                    lowest = Mathf.Min(lowest, raw);
                    highest = Mathf.Max(highest, raw);
                }
            }
            channel.Lift = -lowest;
            channel.Gain = highest != lowest ? 1 / (highest - lowest) : 0f;
            return channel;
        }

        private static FastNoiseLite Configure(BcNoiseSettings s, int seed, float globalScale)
        {
            var n = new FastNoiseLite(seed);
            n.SetNoiseType((FastNoiseLite.NoiseType)s.NoiseType);
            n.SetFrequency(s.Frequency / globalScale);
            n.SetFractalType((FastNoiseLite.FractalType)s.FractalType);
            n.SetFractalOctaves(s.FractalOctaves);
            n.SetFractalLacunarity(s.FractalLacunarity);
            n.SetFractalGain(s.FractalGain);
            n.SetFractalWeightedStrength(s.FractalWeightedStrength);
            n.SetFractalPingPongStrength(s.FractalPingPongStrength);
            n.SetCellularDistanceFunction((FastNoiseLite.CellularDistanceFunction)s.CellularDistanceFunction);
            n.SetCellularReturnType((FastNoiseLite.CellularReturnType)s.CellularReturnType);
            n.SetCellularJitter(s.CellularJitter);
            n.SetDomainWarpType((FastNoiseLite.DomainWarpType)s.DomainWarpType);
            n.SetDomainWarpAmp(s.DomainWarpAmp);
            return n;
        }

        // The standard image blend formulas (base = what is below, top = the layer), by the mode number the format stores:
        // 0 normal, 1 darken, 2 multiply, 3 colour burn, 4 linear burn, 5 lighten, 6 screen, 7 colour dodge, 8 linear dodge,
        // 9 overlay, 10 hard light, 11 soft light, 12 vivid light, 13 linear light, 14 pin light, 15 hard mix, 16 difference,
        // 17 exclusion, 18 subtract, 19 divide. An unknown mode shows the layer as it is.
        private static readonly Func<float, float, float>[] BlendFormulas =
        {
            (lo, hi) => hi,
            (lo, hi) => Mathf.Min(lo, hi),
            (lo, hi) => lo * hi,
            (lo, hi) => 1f - (1f - lo) / hi,
            (lo, hi) => lo + hi - 1f,
            (lo, hi) => Mathf.Max(lo, hi),
            (lo, hi) => 1f - (1f - lo) * (1f - hi),
            (lo, hi) => lo / (1f - hi),
            (lo, hi) => lo + hi,
            (lo, hi) => lo > 0.5f ? 1f - 2f * (1f - lo) * (1f - hi) : 2f * lo * hi,
            (lo, hi) => hi > 0.5f ? 1f - 2f * (1f - lo) * (1f - hi) : 2f * lo * hi,
            (lo, hi) => (1f - 2f * hi) * lo * lo + 2f * hi * lo,
            (lo, hi) => hi > 0.5f ? lo / (2f * (1f - hi)) : 1f - (1f - lo) / (2f * hi),
            (lo, hi) => lo + 2f * hi - 1f,
            (lo, hi) => lo < 2f * hi - 1f ? 2f * hi - 1f : lo > 2f * hi ? 2f * hi : lo,
            (lo, hi) => hi < 1f - lo ? 0f : 1f,
            (lo, hi) => Mathf.Abs(lo - hi),
            (lo, hi) => lo + hi - 2f * lo * hi,
            (lo, hi) => lo - hi,
            (lo, hi) => lo / hi,
        };

        private static float Mix(int mode, float under, float layer)
            => mode >= 0 && mode < BlendFormulas.Length ? BlendFormulas[mode](under, layer) : layer;
    }
}
