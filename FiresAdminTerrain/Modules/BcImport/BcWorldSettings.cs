using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    /// <summary>
    /// A BetterContinents world's settings, with BC's own defaults. The v11 writer emits a key only for a
    /// non-default value, so these defaults are part of the format. Spec: Docs/BETTERCONTINENTS_IMPORT.md
    /// </summary>
    internal sealed class BcWorldSettings
    {
        public const int DisabledVersion = -1;
        public const int FirstKeyedVersion = 11;

        public bool EnabledForThisWorld;
        public int Version;

        public float GlobalScale;
        public float MountainsAmount;
        public float SeaLevelAdjustment;
        public float MaxRidgeHeight;
        public float RidgeScale;
        public float RidgeBlendSigmoidB;
        public float RidgeBlendSigmoidXOffset;

        public float HeightmapAmount = 1f;
        public float HeightmapBlend = 1f;
        public float HeightmapAdd;
        public float HeightmapMask;
        public bool HeightmapOverrideAll = true;
        public bool HeightMapAlpha;

        public bool OceanChannelsEnabled = true;
        public bool RiversEnabled = true;
        public bool AshlandsGapEnabled;
        public bool DeepNorthGapEnabled;

        public float ForestScale = 1f;
        public float ForestAmountOffset;
        public float ForestmapMultiply = 1f;
        public float ForestmapAdd = 1f;
        public bool ForestFactorOverrideAllTrees;

        public float RoughmapBlend = 1f;
        public float FlatmapBlend;
        public bool UseRoughInvertedAsFlat;

        public int BiomePrecision;
        public bool OverrideStartPosition;
        public float StartPositionX;
        public float StartPositionY;
        public bool SkipDefaultLocations;

        public bool DisableMapEdgeDropoff;
        public bool MountainsAllowedAtCenter;

        public float HeatMapScale = 10f;
        public float WorldSize = 10000f;
        public float EdgeSize = 500f;
        public bool FixWaterColor = true;

        public BcMapImage HeightMap;
        public BcMapImage RoughMap;
        public BcMapImage FlatMap;
        public BcMapImage ForestMap;
        public BcMapImage HeatMap;
        public BcMapImage LavaMap;
        public BcMapImage MossMap;
        public BcMapImage TerrainMap;
        public BcMapImage PaintMap;
        public BcMapImage LegacyBiomeImage;

        public BcBiomeIndexMap BiomeIndexMap;
        public BcLocationMap LocationMap;
        public BcSpawnMap SpawnMap;
        public BcSpawnMap VegetationMap;
        public BcNoiseStack BaseHeightNoise = new BcNoiseStack();

        public float TotalRadius => WorldSize + EdgeSize;
        public float TotalSize => TotalRadius * 2f;
        public bool HeightMapOverridesEverything => HeightMap != null && HeightmapOverrideAll;

        public float Normalize(float world) => Mathf.Clamp(world / TotalSize + 0.5f, 0f, 1f);

        /// <summary>Resets the fields the legacy path assigns unconditionally, including the two whose
        /// legacy value is the opposite of the v11 default.</summary>
        public void ApplyLegacyBaseline()
        {
            RiversEnabled = true;
            ForestScale = 1f;
            ForestAmountOffset = 0f;
            OverrideStartPosition = false;
            StartPositionX = 0f;
            StartPositionY = 0f;
            SkipDefaultLocations = false;
            BaseHeightNoise = new BcNoiseStack();
            HeightmapOverrideAll = false;
            HeightmapMask = 0f;
            BiomePrecision = 0;
        }
    }

    /// <summary>The original image bytes as BC stored them, undecoded. ColorTable and Legacy8BitRed decide
    /// how BcImageMaps reads them.</summary>
    internal sealed class BcMapImage
    {
        public byte[] Data;
        public string FilePath = "";
        public string ColorTable = "";
        public bool Legacy8BitRed;
        public bool HasAlphaChannel;
    }

    /// <summary>v11 biome maps are a raw square array of bit indices, one byte per pixel - not an image.</summary>
    internal sealed class BcBiomeIndexMap
    {
        public string FilePath = "";
        public byte[] Indices;
        public int Size;
    }

    internal sealed class BcLocationMap
    {
        public string FilePath = "";
        public Dictionary<string, List<Vector2>> RemainingAreas = new Dictionary<string, List<Vector2>>();
    }

    internal sealed class BcSpawnMap
    {
        public string FilePath = "";
        public List<BcSpawnPaletteEntry> Palette = new List<BcSpawnPaletteEntry>();
        public byte[] Indices;
        public int Size;
    }

    internal struct BcSpawnPaletteEntry
    {
        public Color32 Color;
        public string Data;
    }

    internal sealed class BcNoiseStack
    {
        public List<BcNoiseLayer> Layers = new List<BcNoiseLayer>();
    }

    internal sealed class BcNoiseLayer
    {
        public int Version;
        public BcNoiseSettings Noise;
        public BcNoiseSettings NoiseWarp;
        public BcNoiseSettings Mask;
        public BcNoiseSettings MaskWarp;
    }

    /// <summary>A FastNoiseLite configuration exactly as BC serializes it. The enum members stay as ints
    /// until BcWorldGen brings FastNoiseLite in, so the reader never guesses an enum's numbering.</summary>
    internal sealed class BcNoiseSettings
    {
        public int Version;
        public int NoiseType;
        public float Frequency;
        public float Aspect;
        public int FractalType;
        public int FractalOctaves;
        public float FractalLacunarity;
        public float FractalGain;
        public float FractalWeightedStrength;
        public float FractalPingPongStrength;
        public int CellularDistanceFunction;
        public int CellularReturnType;
        public float CellularJitter;
        public int DomainWarpType;
        public float DomainWarpAmp;
        public bool Invert;
        public bool UseSmoothThreshold;
        public float SmoothThresholdStart;
        public float SmoothThresholdEnd;
        public bool UseThreshold;
        public float Threshold;
        public bool UseRange;
        public float RangeStart;
        public float RangeEnd;
        public bool UseOpacity;
        public float Opacity;
        public int BlendMode;
    }
}
