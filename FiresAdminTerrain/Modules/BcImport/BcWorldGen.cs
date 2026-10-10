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

    // The ground of a converted BetterContinents world, computed by FAT with BetterContinents not installed. Which recipe a
    // world gets follows its saved settings version (VariantFor):
    //  - V1 / V2 (settings 1-6): a procedural shape - two products of Perlin octaves for broad land, a domain-warped ridge
    //    field mixed in by a logistic weight, a mountain curve, two detail products scaled by the height itself, then the
    //    world's sea line; V2 adds the ridges instead of interpolating to them and honours a flat map (Shape below).
    //  - V3 (settings 7+): the height map, then the layered noise (BcNoiseField), then the sea line.
    // All three then sink the rim past the playable radius into a cliff at the world's edge and cap mountains near the
    // centre. The maps (height, rough, flat, paint, lava, moss, heat, forest, biome) shape the rest as the map maker drew
    // them. Floats are kept in the order the format needs, because BcRefTest compares this ground with the original mod's
    // to the millimetre. Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal sealed class BcWorldGen
    {
        private const float WaterLineProcedural = 0.07f;
        private const float WaterLineImage = 0.15f;
        private const float MountainShift = 0.05f;
        private const float RimFloor = -0.2f;
        private const float CliffFloor = -2f;
        private const float CliffWidth = 10f;
        private const float CentreCapStart = 0.28f;
        private const float CentreCapTop = 0.38f;
        private const float CentreCapRange = 0.099999994f;
        private const float CentreCapFade = 400f;
        private const float ChannelBandLow = 0.02f;
        private const float ChannelBandHigh = 0.12f;
        private const float ChannelFadeStart = 744f;
        private const float ChannelFadeEnd = 1000f;
        private const float WarpFrequency = 0.001f;
        private const float NoiseOrigin = 100000f;
        private const float ForestFactorLow = 0.145071f;
        private const float ForestFactorHigh = 1.850145f;

        private readonly BcWorldSettings _settings;
        private readonly BcDecodedMaps _maps;
        private readonly BcNoiseField _layers;

        public BcWorldGen(BcWorldSettings settings, BcDecodedMaps maps, int worldSeed)
        {
            _settings = settings;
            _maps = maps;
            _layers = new BcNoiseField(settings.TotalSize, worldSeed, settings.BaseHeightNoise, settings.GlobalScale);
        }

        public BcWorldSettings Settings => _settings;
        public BcDecodedMaps Maps => _maps;

        /// <summary>Which recipe a world's base height follows, from its SETTINGS version (not Valheim's). A v7+ world
        /// with neither a height map nor noise layers keeps vanilla's base height.</summary>
        public static BcBaseHeightVariant VariantFor(BcWorldSettings settings)
        {
            if (settings == null || !settings.EnabledForThisWorld) return BcBaseHeightVariant.None;
            if (settings.Version >= 1 && settings.Version <= 2) return BcBaseHeightVariant.V1;
            if (settings.Version >= 3 && settings.Version <= 6) return BcBaseHeightVariant.V2;
            return settings.HeightMap != null || settings.BaseHeightNoise.Layers.Count > 0 ? BcBaseHeightVariant.V3 : BcBaseHeightVariant.None;
        }

        /// <summary>The base height (vanilla's units, x200 = metres) at a world point; offsets and the mountain-free radius
        /// are the world generator's own.</summary>
        public float GetBaseHeight(float wx, float wy, float offset0, float offset1, float minMountainDistance)
        {
            switch (VariantFor(_settings))
            {
                case BcBaseHeightVariant.V1: return Shape(wx, wy, offset0, offset1, minMountainDistance, false);
                case BcBaseHeightVariant.V2: return Shape(wx, wy, offset0, offset1, minMountainDistance, true);
                case BcBaseHeightVariant.V3: return ImageAndLayers(wx, wy, minMountainDistance);
                default: return 0f;
            }
        }

        // V3: the height map, the noise layers over it, the sea line, then the rim and the centre cap.
        private float ImageAndLayers(float wx, float wy, float minMountainDistance)
        {
            float fromCentre = Utils.Length(wx, wy);
            float h = HeightFromImage(_settings.Normalize(wx), _settings.Normalize(wy), 0f);
            if (_layers != null) h = _layers.Apply(wx, wy, h);
            h -= WaterLineImage;
            h += _settings.SeaLevelAdjustment;
            if (!_settings.DisableMapEdgeDropoff && fromCentre > _settings.WorldSize) h = SinkRim(h, fromCentre);
            if (!_settings.MountainsAllowedAtCenter && fromCentre < minMountainDistance && h > CentreCapStart)
                h = CapNearCentre(h, fromCentre, minMountainDistance);
            return h;
        }

        // V1 and V2. `additive` is V2: ridges are added on top (V1 interpolates toward them), a flat map applies, the edge
        // drop-off can be switched off and the centre cap follows the mountains-at-centre setting. V1 lets a height map that
        // overrides everything switch the ridges, ocean channels and centre cap off instead.
        private float Shape(float wx, float wy, float offset0, float offset1, float minMountainDistance, bool additive)
        {
            float fromCentre = Utils.Length(wx, wy);
            float mapX = _settings.Normalize(wx);
            float mapY = _settings.Normalize(wy);
            bool imageRules = _settings.HeightMapOverridesEverything;

            wx *= _settings.GlobalScale;
            wy *= _settings.GlobalScale;
            float warpStep = WarpFrequency * _settings.RidgeScale;
            float ridgeX = (Mathf.PerlinNoise(wx * warpStep, wy * warpStep) - 0.5f) * _settings.TotalRadius;
            float ridgeY = (Mathf.PerlinNoise(wx * warpStep + 2f, wy * warpStep + 3f) - 0.5f) * _settings.TotalRadius;
            wx += NoiseOrigin + offset0;
            wy += NoiseOrigin + offset1;

            float land = HeightFromImage(mapX, mapY, OctavePair(wx, wy, 0.002f, 0.003f, 0.5f));
            float ridges = OctavePair(ridgeX, ridgeY, 0.002f, 0.003f, 0.5f) * _settings.MaxRidgeHeight;
            float ridgeWeight = !additive && imageRules ? 0 : Mathf.Clamp01(Logistic(
                Mathf.PerlinNoise(wx * 0.005f - 10000, wy * 0.005f - 5000) - _settings.RidgeBlendSigmoidXOffset, _settings.RidgeBlendSigmoidB));
            float relief = additive ? Mathf.Clamp01(land + ridges * ridgeWeight) : Mathf.Clamp01(Mathf.Lerp(land, ridges, ridgeWeight));

            float h = 0f;
            h += MountainCurve(relief - MountainShift, _settings.MountainsAmount) + MountainShift;
            h += OctavePair(wx, wy, 0.002f, 0.003f, 1f) * h * 0.9f;
            h += OctavePair(wx, wy, 0.005f, 0.01f, 1f) * 0.5f * h;
            if (additive) h = FlattenFromImage(mapX, mapY, land, h);
            h -= WaterLineProcedural;
            h += _settings.SeaLevelAdjustment;

            if (_settings.OceanChannelsEnabled && (additive || !imageRules)) h = CutChannels(h, wx, wy, fromCentre);
            bool rim = additive ? !_settings.DisableMapEdgeDropoff : true;
            if (rim && fromCentre > _settings.WorldSize) h = SinkRim(h, fromCentre);
            bool capAllowed = additive ? !_settings.MountainsAllowedAtCenter : !imageRules;
            if (capAllowed && fromCentre < minMountainDistance && h > CentreCapStart) h = CapNearCentre(h, fromCentre, minMountainDistance);
            return h;
        }

        // Two Perlin octaves multiplied, at frequencies f1 and f2, both scaled by k.
        private static float OctavePair(float x, float y, float f1, float f2, float k)
            => Mathf.PerlinNoise(x * f1 * k, y * f1 * k) * Mathf.PerlinNoise(x * f2 * k, y * f2 * k);

        private static float Logistic(float x, float steepness) => 1 / (1 + Mathf.Exp(steepness * x));

        // Raises the shoulders of a 0..1 relief: n (the mountains amount) sharpens the peaks.
        private static float MountainCurve(float x, float n) => x * (1 - Mathf.Pow(1 - x, 1.2f + n * 0.8f)) + x * (1 - x);

        /// <summary>The height map over `under`: blended in, masked by itself and added, each by the world's own amounts.
        /// With no map, or all three amounts zero, `under` is returned as it is.</summary>
        private float HeightFromImage(float mapX, float mapY, float under)
        {
            if (_maps?.Height == null || (_settings.HeightmapBlend == 0 && _settings.HeightmapAdd == 0 && _settings.HeightmapMask == 0))
                return under;
            float pixel = _maps.Height.GetValue(mapX, mapY);
            float blended = Mathf.Lerp(under, pixel * _settings.HeightmapAmount, _settings.HeightmapBlend);
            return Mathf.Lerp(blended, blended * pixel, _settings.HeightmapMask) + pixel * _settings.HeightmapAdd;
        }

        // Where the flat map (or the inverted rough map) is white, the broad land shape replaces the detailed height.
        // A height map that overrides everything makes the whole world the broad shape.
        private float FlattenFromImage(float mapX, float mapY, float broad, float detailed)
        {
            if (_settings.HeightMapOverridesEverything) return broad;
            BcFloatMap source = _settings.UseRoughInvertedAsFlat ? _maps?.Rough : _maps?.Flat;
            if (source == null) return detailed;
            float flatness = source.GetValue(mapX, mapY);
            if (_settings.UseRoughInvertedAsFlat) flatness = 1 - flatness;
            return Mathf.Lerp(detailed, broad, flatness * _settings.FlatmapBlend);
        }

        // Ocean channels: a band where two offset Perlin fields nearly agree is pressed toward 0, faded in from the centre.
        private static float CutChannels(float h, float wx, float wy, float fromCentre)
        {
            float gap = Mathf.Abs(
                Mathf.PerlinNoise(wx * 0.002f * 0.25f + 0.123f, wy * 0.002f * 0.25f + 0.15123f)
                - Mathf.PerlinNoise(wx * 0.002f * 0.25f + 0.321f, wy * 0.002f * 0.25f + 0.231f));
            float inBand = 1f - Utils.LerpStep(ChannelBandLow, ChannelBandHigh, gap);
            return h * (1f - inBand * Utils.SmoothStep(ChannelFadeStart, ChannelFadeEnd, fromCentre));
        }

        // Past the playable radius the ground slopes down to the rim height, and its last ten metres drop into a cliff.
        private float SinkRim(float h, float fromCentre)
        {
            h = Mathf.Lerp(h, RimFloor, Utils.LerpStep(_settings.WorldSize, _settings.TotalRadius, fromCentre));
            float cliffStart = _settings.TotalRadius - CliffWidth;
            if (fromCentre > cliffStart) h = Mathf.Lerp(h, CliffFloor, Utils.LerpStep(cliffStart, _settings.TotalRadius, fromCentre));
            return h;
        }

        // Inside the mountain-free radius a high point is squeezed into the 0.28..0.38 band, fading out over its last 400 m.
        private static float CapNearCentre(float h, float fromCentre, float minMountainDistance)
        {
            float squeezed = Mathf.Lerp(CentreCapStart, CentreCapTop, Mathf.Clamp01((h - CentreCapStart) / CentreCapRange));
            return Mathf.Lerp(squeezed, h, Utils.LerpStep(minMountainDistance - CentreCapFade, minMountainDistance, fromCentre));
        }

        /// <summary>The ground height a converted world's maps give on top of vanilla's biome height (`height`, in metres, on
        /// the way in). A height map that overrides everything makes the ground the base height x 200; a rough map blends
        /// that smooth height into the biome height; paint, lava, moss and vegetation maps colour the terrain mask first.
        /// Without this a converted world kept vanilla's biome height everywhere (Fire 10-05: dirt over builds, since a
        /// save's terrain edits are deltas from it).</summary>
        public void ApplyBiomeHeight(WorldGenerator generator, Heightmap.Biome biome, float wx, float wy, ref Color mask, ref float height)
        {
            if (_settings == null || !_settings.EnabledForThisWorld) return;
            bool paints = _settings.PaintMap != null || _settings.LavaMap != null || _settings.MossMap != null || _settings.VegetationMap != null;
            if (_settings.HeightMapOverridesEverything)
            {
                if (paints) PaintMask(wx, wy, biome, ref mask);
                height = generator.GetBaseHeight(wx, wy, false) * 200f;
                return;
            }
            if (_settings.RoughMap != null)
            {
                float smooth = generator.GetBaseHeight(wx, wy, false) * 200f;
                if (paints) PaintMask(wx, wy, biome, ref mask);
                height = _maps?.Rough == null
                    ? height
                    : Mathf.Lerp(smooth, height, _maps.Rough.GetValue(_settings.Normalize(wx), _settings.Normalize(wy)) * _settings.RoughmapBlend);
                return;
            }
            if (paints) PaintMask(wx, wy, biome, ref mask);
        }

        // The paint map's colour onto the terrain mask (its alpha too, unless fully opaque), then the lava map in the Ashlands
        // or the moss map in the Mistlands as the mask's alpha.
        private void PaintMask(float wx, float wy, Heightmap.Biome biome, ref Color mask)
        {
            float x = _settings.Normalize(wx), y = _settings.Normalize(wy);
            if (_maps?.Paint != null && _maps.Paint.TryGetValue(x, y, out Color painted))
            {
                mask.r = painted.r;
                mask.g = painted.g;
                mask.b = painted.b;
                if (painted.a != 1f) mask.a = painted.a;
            }
            if (_maps?.Lava != null && biome == Heightmap.Biome.AshLands) mask.a = _maps.Lava.GetValue(x, y);
            else if (_maps?.Moss != null && biome == Heightmap.Biome.Mistlands) mask.a = _maps.Moss.GetValue(x, y);
        }

        /// <summary>The heat map's value at a normalised map point, times the world's heat scale (0 with no heat map).
        /// Above 0 means Ashlands.</summary>
        public float HeatAt(float mapX, float mapY) => _settings.HeatMapScale * (_maps?.Heat != null ? _maps.Heat.GetValue(mapX, mapY) : 0f);

        /// <summary>Vanilla's forest factor reshaped by the forest map (multiplied and added by the world's amounts, in the
        /// factor's 0..1 span), then offset and kept inside vanilla's range.</summary>
        public float ForestFactorAt(float mapX, float mapY, float vanillaFactor)
        {
            float factor = vanillaFactor;
            if (_maps?.Forest != null)
            {
                float share = Mathf.InverseLerp(ForestFactorHigh, ForestFactorLow, vanillaFactor);
                float drawn = _maps.Forest.GetValue(mapX, mapY);
                float reshaped = Mathf.Lerp(share, share * drawn, _settings.ForestmapMultiply) + drawn * _settings.ForestmapAdd;
                factor = Mathf.Lerp(ForestFactorHigh, ForestFactorLow, reshaped);
            }
            return Mathf.Clamp(factor + _settings.ForestAmountOffset, ForestFactorLow, ForestFactorHigh);
        }

        /// <summary>The biome the map maker drew at a normalised map point; None where the map says nothing (or there is
        /// no biome map), which leaves vanilla's rules to decide.</summary>
        public Heightmap.Biome MapBiomeAt(float mapX, float mapY) => _maps?.Biome == null ? Heightmap.Biome.None : _maps.Biome.GetValue(mapX, mapY);
    }
}
