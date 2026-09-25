using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    internal enum BcBaseHeightVariant
    {
        None,
        V1,
        V2,
        V3,
    }

    // BetterContinents' terrain, written against BC's OWN SOURCE (JereKuusela/BetterContinents 0.7.31,
    // BetterContinents.WorldGeneratorPatch.cs and BetterContinents.BetterContinentsSettings.cs) - not against a
    // decompile. That matters for more than tidiness: the decompiler renders BC's float arithmetic as double
    // expressions, and transcribing those changes the rounding, which showed up as ~1e-6 drift.
    //
    // BC's base-height patches are prefixes that return false, so they REPLACE vanilla's GetBaseHeight. That is
    // why this can be an exact reproduction with none of BC's code and BC not installed.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal sealed class BcWorldGen
    {
        private const float SeaLevel = 0.05f;
        private const float V1V2WaterLine = 0.07f;
        private const float V3WaterLine = 0.15f;
        private const float EdgeDropoffHeight = -0.2f;
        private const float EdgeCliffHeight = -2f;
        private const float EdgeCliffMargin = 10f;
        private const float MountainCapLow = 0.28f;
        private const float MountainCapHigh = 0.38f;
        private const float MountainCapSpan = 0.099999994f;
        private const float MountainCapDistance = 400f;
        private const float OceanChannelLow = 0.02f;
        private const float OceanChannelHigh = 0.12f;
        private const float OceanChannelNear = 744f;
        private const float OceanChannelFar = 1000f;
        private const float WarpScaleBase = 0.001f;
        private const float WorldOffset = 100000f;
        private const float ForestFactorMin = 0.145071f;
        private const float ForestFactorMax = 1.850145f;

        private readonly BcWorldSettings _settings;
        private readonly BcDecodedMaps _maps;
        private readonly BcNoiseStackRuntime _baseHeightNoise;

        public BcWorldGen(BcWorldSettings settings, BcDecodedMaps maps, int worldSeed)
        {
            _settings = settings;
            _maps = maps;
            _baseHeightNoise = new BcNoiseStackRuntime(settings.TotalSize, worldSeed, settings.BaseHeightNoise, settings.GlobalScale);
        }

        public BcWorldSettings Settings => _settings;
        public BcDecodedMaps Maps => _maps;

        /// <summary>Which base-height replacement a world gets, decided by the SETTINGS version - not Valheim's.
        /// A v7+ world with neither a heightmap nor noise layers keeps vanilla's base height entirely.</summary>
        public static BcBaseHeightVariant VariantFor(BcWorldSettings settings)
        {
            if (settings == null || !settings.EnabledForThisWorld) return BcBaseHeightVariant.None;
            switch (settings.Version)
            {
                case 1:
                case 2:
                    return BcBaseHeightVariant.V1;
                case 3:
                case 4:
                case 5:
                case 6:
                    return BcBaseHeightVariant.V2;
                default:
                    return settings.HeightMap != null || settings.BaseHeightNoise.Layers.Count > 0
                        ? BcBaseHeightVariant.V3
                        : BcBaseHeightVariant.None;
            }
        }

        public float GetBaseHeight(float wx, float wy, float offset0, float offset1, float minMountainDistance)
        {
            switch (VariantFor(_settings))
            {
                case BcBaseHeightVariant.V1: return GetBaseHeightV1(wx, wy, offset0, offset1, minMountainDistance);
                case BcBaseHeightVariant.V2: return GetBaseHeightV2(wx, wy, offset0, offset1, minMountainDistance);
                case BcBaseHeightVariant.V3: return GetBaseHeightV3(wx, wy, minMountainDistance);
                default: return 0f;
            }
        }

        public float GetBaseHeightV3(float wx, float wy, float minMountainDistance)
        {
            float distance = Utils.Length(wx, wy);
            float mapX = _settings.Normalize(wx);
            float mapY = _settings.Normalize(wy);

            float baseHeight = ApplyHeightmap(mapX, mapY, 0f);
            float finalHeight = _baseHeightNoise != null ? _baseHeightNoise.Apply(wx, wy, baseHeight) : baseHeight;
            finalHeight -= V3WaterLine;
            finalHeight += _settings.SeaLevelAdjustment;

            if (!_settings.DisableMapEdgeDropoff && distance > _settings.WorldSize)
                finalHeight = DropOffAtEdge(finalHeight, distance);
            if (!_settings.MountainsAllowedAtCenter && distance < minMountainDistance && finalHeight > MountainCapLow)
                finalHeight = CapCentreMountains(finalHeight, distance, minMountainDistance);
            return finalHeight;
        }

        public float GetBaseHeightV2(float wx, float wy, float offset0, float offset1, float minMountainDistance)
        {
            float distance = Utils.Length(wx, wy);
            float mapX = _settings.Normalize(wx);
            float mapY = _settings.Normalize(wy);

            wx *= _settings.GlobalScale;
            wy *= _settings.GlobalScale;

            float warpScale = WarpScaleBase * _settings.RidgeScale;
            float warpX = (Mathf.PerlinNoise(wx * warpScale, wy * warpScale) - 0.5f) * _settings.TotalRadius;
            float warpY = (Mathf.PerlinNoise(wx * warpScale + 2f, wy * warpScale + 3f) - 0.5f) * _settings.TotalRadius;

            wx += WorldOffset + offset0;
            wy += WorldOffset + offset1;

            float bigFeatureNoiseHeight = Mathf.PerlinNoise(wx * 0.002f * 0.5f, wy * 0.002f * 0.5f)
                * Mathf.PerlinNoise(wx * 0.003f * 0.5f, wy * 0.003f * 0.5f) * 1f;
            float bigFeatureHeight = ApplyHeightmap(mapX, mapY, bigFeatureNoiseHeight);
            float ridgeHeight = Mathf.PerlinNoise(warpX * 0.002f * 0.5f, warpY * 0.002f * 0.5f)
                * Mathf.PerlinNoise(warpX * 0.003f * 0.5f, warpY * 0.003f * 0.5f) * _settings.MaxRidgeHeight;

            float lerp = Mathf.Clamp01(SigmoidActivation(
                Mathf.PerlinNoise(wx * 0.005f - 10000, wy * 0.005f - 5000) - _settings.RidgeBlendSigmoidXOffset,
                0, _settings.RidgeBlendSigmoidB));

            float bigFeature = Mathf.Clamp01(bigFeatureHeight + ridgeHeight * lerp);
            float detailedFinalHeight = ApplyMountains(bigFeature - SeaLevel, _settings.MountainsAmount) + SeaLevel;

            detailedFinalHeight += Mathf.PerlinNoise(wx * 0.002f * 1f, wy * 0.002f * 1f)
                * Mathf.PerlinNoise(wx * 0.003f * 1f, wy * 0.003f * 1f) * detailedFinalHeight * 0.9f;
            detailedFinalHeight += Mathf.PerlinNoise(wx * 0.005f * 1f, wy * 0.005f * 1f)
                * Mathf.PerlinNoise(wx * 0.01f * 1f, wy * 0.01f * 1f) * 0.5f * detailedFinalHeight;

            float finalHeight = ApplyFlatmap(mapX, mapY, bigFeatureHeight, detailedFinalHeight);
            finalHeight -= V1V2WaterLine;
            finalHeight += _settings.SeaLevelAdjustment;

            if (_settings.OceanChannelsEnabled) finalHeight = CarveOceanChannels(finalHeight, wx, wy, distance);
            if (!_settings.DisableMapEdgeDropoff && distance > _settings.WorldSize)
                finalHeight = DropOffAtEdge(finalHeight, distance);
            if (!_settings.MountainsAllowedAtCenter && distance < minMountainDistance && finalHeight > MountainCapLow)
                finalHeight = CapCentreMountains(finalHeight, distance, minMountainDistance);
            return finalHeight;
        }

        public float GetBaseHeightV1(float wx, float wy, float offset0, float offset1, float minMountainDistance)
        {
            float distance = Utils.Length(wx, wy);
            float mapX = _settings.Normalize(wx);
            float mapY = _settings.Normalize(wy);

            wx *= _settings.GlobalScale;
            wy *= _settings.GlobalScale;

            float warpScale = WarpScaleBase * _settings.RidgeScale;
            float warpX = (Mathf.PerlinNoise(wx * warpScale, wy * warpScale) - 0.5f) * _settings.TotalRadius;
            float warpY = (Mathf.PerlinNoise(wx * warpScale + 2f, wy * warpScale + 3f) - 0.5f) * _settings.TotalRadius;

            wx += WorldOffset + offset0;
            wy += WorldOffset + offset1;

            float bigFeatureNoiseHeight = Mathf.PerlinNoise(wx * 0.002f * 0.5f, wy * 0.002f * 0.5f)
                * Mathf.PerlinNoise(wx * 0.003f * 0.5f, wy * 0.003f * 0.5f) * 1f;
            float bigFeatureHeight = ApplyHeightmap(mapX, mapY, bigFeatureNoiseHeight);
            float ridgeHeight = Mathf.PerlinNoise(warpX * 0.002f * 0.5f, warpY * 0.002f * 0.5f)
                * Mathf.PerlinNoise(warpX * 0.003f * 0.5f, warpY * 0.003f * 0.5f) * _settings.MaxRidgeHeight;

            float lerp = _settings.HeightMapOverridesEverything
                ? 0
                : Mathf.Clamp01(SigmoidActivation(
                    Mathf.PerlinNoise(wx * 0.005f - 10000, wy * 0.005f - 5000) - _settings.RidgeBlendSigmoidXOffset,
                    0, _settings.RidgeBlendSigmoidB));

            float finalHeight = 0f;
            float bigFeature = Mathf.Clamp01(Mathf.Lerp(bigFeatureHeight, ridgeHeight, lerp));

            finalHeight += ApplyMountains(bigFeature - SeaLevel, _settings.MountainsAmount) + SeaLevel;
            finalHeight += Mathf.PerlinNoise(wx * 0.002f * 1f, wy * 0.002f * 1f)
                * Mathf.PerlinNoise(wx * 0.003f * 1f, wy * 0.003f * 1f) * finalHeight * 0.9f;
            finalHeight += Mathf.PerlinNoise(wx * 0.005f * 1f, wy * 0.005f * 1f)
                * Mathf.PerlinNoise(wx * 0.01f * 1f, wy * 0.01f * 1f) * 0.5f * finalHeight;
            finalHeight -= V1V2WaterLine;
            finalHeight += _settings.SeaLevelAdjustment;

            if (_settings.OceanChannelsEnabled && !_settings.HeightMapOverridesEverything)
                finalHeight = CarveOceanChannels(finalHeight, wx, wy, distance);
            if (distance > _settings.WorldSize) finalHeight = DropOffAtEdge(finalHeight, distance);
            if (distance < minMountainDistance && finalHeight > MountainCapLow && !_settings.HeightMapOverridesEverything)
                finalHeight = CapCentreMountains(finalHeight, distance, minMountainDistance);
            return finalHeight;
        }

        public float ApplyHeightmap(float x, float y, float height)
        {
            if (_maps?.Height == null
                || (_settings.HeightmapBlend == 0 && _settings.HeightmapAdd == 0 && _settings.HeightmapMask == 0))
                return height;

            float h = _maps.Height.GetValue(x, y);
            float blendedHeight = Mathf.Lerp(height, h * _settings.HeightmapAmount, _settings.HeightmapBlend);
            return Mathf.Lerp(blendedHeight, blendedHeight * h, _settings.HeightmapMask) + h * _settings.HeightmapAdd;
        }

        public float ApplyRoughmap(float x, float y, float smoothHeight, float roughHeight)
        {
            if (_maps?.Rough == null) return roughHeight;
            return Mathf.Lerp(smoothHeight, roughHeight, _maps.Rough.GetValue(x, y) * _settings.RoughmapBlend);
        }

        /// <summary>0.7.31 dropped the RoughmapBlend == 0 short-circuit that 0.7.30 had here; this follows the
        /// current source, so a flat-map world differs from the 0.7.30 binary on that one gate.</summary>
        public float ApplyFlatmap(float x, float y, float flatHeight, float height)
        {
            if (_settings.HeightMapOverridesEverything) return flatHeight;
            BcFloatMap image = _settings.UseRoughInvertedAsFlat ? _maps?.Rough : _maps?.Flat;
            if (image == null) return height;

            float f = _settings.UseRoughInvertedAsFlat ? 1 - image.GetValue(x, y) : image.GetValue(x, y);
            return Mathf.Lerp(height, flatHeight, f * _settings.FlatmapBlend);
        }

        public float ApplyHeatmap(float x, float y) =>
            _settings.HeatMapScale * (_maps?.Heat != null ? _maps.Heat.GetValue(x, y) : 0f);

        public float ApplyForest(float x, float y, float forest)
        {
            float finalValue = forest;
            if (_maps?.Forest != null)
            {
                float normalizedForestValue = Mathf.InverseLerp(ForestFactorMax, ForestFactorMin, forest);
                float fmap = _maps.Forest.GetValue(x, y);
                float calculatedValue = Mathf.Lerp(normalizedForestValue, normalizedForestValue * fmap, _settings.ForestmapMultiply)
                    + fmap * _settings.ForestmapAdd;
                finalValue = Mathf.Lerp(ForestFactorMax, ForestFactorMin, calculatedValue);
            }
            return Mathf.Clamp(finalValue + _settings.ForestAmountOffset, ForestFactorMin, ForestFactorMax);
        }

        public Heightmap.Biome GetBiomeOverride(float mapX, float mapY) =>
            _maps?.Biome == null ? Heightmap.Biome.None : _maps.Biome.GetValue(mapX, mapY);

        private float CarveOceanChannels(float finalHeight, float wx, float wy, float distance)
        {
            float v = Mathf.Abs(
                Mathf.PerlinNoise(wx * 0.002f * 0.25f + 0.123f, wy * 0.002f * 0.25f + 0.15123f)
                - Mathf.PerlinNoise(wx * 0.002f * 0.25f + 0.321f, wy * 0.002f * 0.25f + 0.231f));
            return finalHeight * (1f - (1f - Utils.LerpStep(OceanChannelLow, OceanChannelHigh, v))
                * Utils.SmoothStep(OceanChannelNear, OceanChannelFar, distance));
        }

        private float DropOffAtEdge(float finalHeight, float distance)
        {
            float t = Utils.LerpStep(_settings.WorldSize, _settings.TotalRadius, distance);
            finalHeight = Mathf.Lerp(finalHeight, EdgeDropoffHeight, t);
            float edge = _settings.TotalRadius - EdgeCliffMargin;
            if (distance > edge)
            {
                float t2 = Utils.LerpStep(edge, _settings.TotalRadius, distance);
                finalHeight = Mathf.Lerp(finalHeight, EdgeCliffHeight, t2);
            }
            return finalHeight;
        }

        private static float CapCentreMountains(float finalHeight, float distance, float minMountainDistance)
        {
            float t3 = Mathf.Clamp01((finalHeight - MountainCapLow) / MountainCapSpan);
            return Mathf.Lerp(Mathf.Lerp(MountainCapLow, MountainCapHigh, t3), finalHeight,
                Utils.LerpStep(minMountainDistance - MountainCapDistance, minMountainDistance, distance));
        }

        private static float SigmoidActivation(float x, float a, float b) => 1 / (1 + Mathf.Exp(a + b * x));

        private static float ApplyMountains(float x, float n) =>
            x * (1 - Mathf.Pow(1 - x, 1.2f + n * 0.8f)) + x * (1 - x);
    }
}
