using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    // BetterContinents base-height noise, written against BC 0.7.31 SOURCE (Noise.cs, BlendOperations.cs) - a stack of
    // layers, each a normalised FastNoiseLite sample blended onto the running value and optionally masked by a
    // second sample. Every constant and every order of operations here is load-bearing for bit parity.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal static class BcBlend
    {
        private const int Normal = 0;
        private const int Darken = 1;
        private const int Multiply = 2;
        private const int ColorBurn = 3;
        private const int LinearBurn = 4;
        private const int Lighten = 5;
        private const int Screen = 6;
        private const int ColorDodge = 7;
        private const int LinearDodge = 8;
        private const int Overlay = 9;
        private const int HardLight = 10;
        private const int SoftLight = 11;
        private const int VividLight = 12;
        private const int LinearLight = 13;
        private const int PinLight = 14;
        private const int HardMix = 15;
        private const int Difference = 16;
        private const int Exclusion = 17;
        private const int Subtract = 18;
        private const int Divide = 19;

        public static float Apply(float a, float b, int mode)
        {
            switch (mode)
            {
                case Normal: return b;
                case Darken: return Mathf.Min(a, b);
                case Multiply: return a * b;
                case ColorBurn: return 1f - (1f - a) / b;
                case LinearBurn: return a + b - 1f;
                case Lighten: return Mathf.Max(a, b);
                case Screen: return 1f - (1f - a) * (1f - b);
                case ColorDodge: return a / (1f - b);
                case LinearDodge: return a + b;
                case Overlay: return a > 0.5f ? 1f - 2f * (1f - a) * (1f - b) : 2f * a * b;
                case HardLight: return b > 0.5f ? 1f - 2f * (1f - a) * (1f - b) : 2f * a * b;
                case SoftLight: return (1f - 2f * b) * a * a + 2f * b * a;
                case VividLight: return b > 0.5f ? a / (2f * (1f - b)) : 1f - (1f - a) / (2f * b);
                case LinearLight: return a + 2f * b - 1f;
                case PinLight: return a < 2f * b - 1f ? 2f * b - 1f : a > 2f * b ? 2f * b : a;
                case HardMix: return b < 1f - a ? 0f : 1f;
                case Difference: return Mathf.Abs(a - b);
                case Exclusion: return a + b - 2f * a * b;
                case Subtract: return a - b;
                case Divide: return a / b;
                default: return b;
            }
        }
    }

    internal sealed class BcWarpedNoise
    {
        private const int NormalisationSamples = 100;
        private const int WarpSeedOffset = 127;

        private readonly FastNoiseLite _noise;
        private readonly FastNoiseLite _warp;
        private readonly BcNoiseSettings _settings;
        private readonly float _add;
        private readonly float _mul;

        /// <summary>The normalisation sweep is part of the output: BC samples a 100x100 grid across the whole
        /// world to find this layer's range, then rescales every later sample into 0..1 with it. Change the world
        /// size or the seed and the scaling changes with them.</summary>
        public BcWarpedNoise(float size, int seed, BcNoiseSettings settings, BcNoiseSettings warpSettings, float globalScale)
        {
            _settings = settings;
            _noise = CreateNoise(settings, seed, globalScale);
            _warp = warpSettings != null
                ? CreateNoise(warpSettings, seed + WarpSeedOffset, globalScale)
                : new FastNoiseLite();

            float min = float.MaxValue;
            float max = float.MinValue;
            for (int y = 0; y < NormalisationSamples; y++)
            {
                float yp = (y / (float)NormalisationSamples - 0.5f) * size;
                for (int x = 0; x < NormalisationSamples; x++)
                {
                    float xp = (x / (float)NormalisationSamples - 0.5f) * size;
                    float sample = _noise.GetNoise(xp, yp);
                    min = Mathf.Min(min, sample);
                    max = Mathf.Max(max, sample);
                }
            }
            _add = -min;
            _mul = max != min ? 1 / (max - min) : 0f;
        }

        public float GetBlendedNoise(float x, float y, float baseNoise) =>
            BcBlend.Apply(baseNoise, GetNoise(x, y), _settings.BlendMode);

        /// <summary>The warp runs even when the layer has no warp settings: BC falls back to a default
        /// FastNoiseLite and still calls DomainWarp on it, so the default amplitude is part of the output.</summary>
        public float GetNoise(float x, float y)
        {
            y *= _settings.Aspect;
            _warp.DomainWarp(ref x, ref y);

            float value = (_noise.GetNoise(x, y) + _add) * _mul;
            if (_settings.Invert) value = 1f - value;
            if (_settings.UseSmoothThreshold)
                value = Mathf.InverseLerp(_settings.SmoothThresholdStart, _settings.SmoothThresholdEnd, value);

            float ranged = _settings.UseRange
                ? Mathf.Lerp(_settings.RangeStart, _settings.RangeEnd, value)
                : value;
            if (_settings.UseThreshold)
                ranged = ranged < _settings.Threshold ? 0f : 1f;
            return ranged * (_settings.UseOpacity ? _settings.Opacity : 1f);
        }

        private static FastNoiseLite CreateNoise(BcNoiseSettings settings, int seed, float globalScale)
        {
            var noise = new FastNoiseLite(seed);
            noise.SetNoiseType((FastNoiseLite.NoiseType)settings.NoiseType);
            noise.SetFrequency(settings.Frequency / globalScale);
            noise.SetFractalType((FastNoiseLite.FractalType)settings.FractalType);
            noise.SetFractalOctaves(settings.FractalOctaves);
            noise.SetFractalLacunarity(settings.FractalLacunarity);
            noise.SetFractalGain(settings.FractalGain);
            noise.SetFractalWeightedStrength(settings.FractalWeightedStrength);
            noise.SetFractalPingPongStrength(settings.FractalPingPongStrength);
            noise.SetCellularDistanceFunction((FastNoiseLite.CellularDistanceFunction)settings.CellularDistanceFunction);
            noise.SetCellularReturnType((FastNoiseLite.CellularReturnType)settings.CellularReturnType);
            noise.SetCellularJitter(settings.CellularJitter);
            noise.SetDomainWarpType((FastNoiseLite.DomainWarpType)settings.DomainWarpType);
            noise.SetDomainWarpAmp(settings.DomainWarpAmp);
            return noise;
        }
    }

    internal sealed class BcNoiseLayerRuntime
    {
        private const int MaskSeedFactor = 107;

        private readonly BcWarpedNoise _noise;
        private readonly BcWarpedNoise _mask;

        public BcNoiseLayerRuntime(float size, int seed, BcNoiseLayer layer, float globalScale)
        {
            _noise = new BcWarpedNoise(size, seed, layer.Noise, layer.NoiseWarp, globalScale);
            _mask = layer.Mask != null
                ? new BcWarpedNoise(size, seed * MaskSeedFactor, layer.Mask, layer.MaskWarp, globalScale)
                : null;
        }

        public float Apply(float wx, float wy, float inValue)
        {
            float blended = _noise.GetBlendedNoise(wx, wy, inValue);
            return _mask == null ? blended : Mathf.Lerp(inValue, blended, _mask.GetNoise(wx, wy));
        }
    }

    internal sealed class BcNoiseStackRuntime
    {
        private const int LayerSeedFactor = 31;
        private const int LayerSeedOffset = 3;

        private readonly List<BcNoiseLayerRuntime> _layers = new List<BcNoiseLayerRuntime>();

        public BcNoiseStackRuntime(float size, int seed, BcNoiseStack stack, float globalScale)
        {
            if (stack?.Layers == null) return;
            for (int index = 0; index < stack.Layers.Count; index++)
                _layers.Add(new BcNoiseLayerRuntime(size, seed * LayerSeedFactor * (index + LayerSeedOffset),
                    stack.Layers[index], globalScale));
        }

        public int LayerCount => _layers.Count;

        public float Apply(float x, float y, float inValue)
        {
            float value = inValue;
            foreach (BcNoiseLayerRuntime layer in _layers) value = layer.Apply(x, y, value);
            return value;
        }
    }
}
