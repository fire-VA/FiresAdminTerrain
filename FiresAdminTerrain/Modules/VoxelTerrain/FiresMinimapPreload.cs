using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // Vanilla loads the cached world minimap in Minimap.Update's first frame with a world: it reads four files, inflates three
    // of them into full-size pixel arrays and uploads those, 316 ms on the main thread right after a client's PeerInfo (R40).
    // The reading and inflating are plain managed work, so they start on a worker as soon as the world is known (FAT's
    // WorldGenerator.Initialize hook, ahead of FAT's own world init), and the load only uploads. When anything differs (other
    // paths, a file changed or dropped as stale since, another seed or cache version) vanilla's own load runs as before.
    internal static class FiresMinimapPreload
    {
        private const string MaskName = "cacheMinimapMask";
        private const string BiomeName = "cacheMinimapBiome";
        private const string HeightName = "cacheMinimapHeight";
        private const string MetaName = "cacheMinimapMeta";
        private const int MetaSeedOffset = 0;
        private const int MetaVersionOffset = 4;
        private const int MetaBytes = 8;
        private const int UploadWaitMs = 10000;

        private static readonly AccessTools.FieldRef<Minimap, Texture2D> ForestMaskTexture =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> HeightTexture =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");

        private readonly struct Stamp
        {
            internal readonly string Path;
            private readonly long _length;
            private readonly DateTime _written;

            internal Stamp(string path)
            {
                Path = path;
                var info = new FileInfo(path);
                _length = info.Exists ? info.Length : -1;
                _written = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            }

            internal bool Unchanged()
            {
                var info = new FileInfo(Path);
                return info.Exists && info.Length == _length && info.LastWriteTimeUtc == _written;
            }
        }

        private sealed class Decoded
        {
            internal Stamp Mask, Biome, Height, Meta;
            internal int Seed;
            internal int Version;
            internal Color32[] MaskPixels, BiomePixels;
            internal Color[] HeightPixels;
            internal long DecodeMs;
        }

        private static Task<Decoded> s_decode;

        internal static void Start(World world)
        {
            s_decode = null;
            if (world == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            string dir = world.GetSaveDirectory(FileHelpers.FileSource.Local);
            string mask = FiresMinimapCache.CompletePath(dir, MaskName);
            string biome = FiresMinimapCache.CompletePath(dir, BiomeName);
            string height = FiresMinimapCache.CompletePath(dir, HeightName);
            string meta = FiresMinimapCache.CompletePath(dir, MetaName);
            if (!File.Exists(mask) || !File.Exists(biome) || !File.Exists(height) || !File.Exists(meta)) return;
            s_decode = Task.Run(() => Decode(mask, biome, height, meta));
        }

        // From FAT's TryLoadMinimapTextureData prefix, after FiresMinimapCache.BeforeLoad. True when the arrays were uploaded,
        // so vanilla's load is skipped with its result true.
        internal static bool TryUpload(Minimap minimap, int worldSeed)
        {
            var task = s_decode;
            s_decode = null;
            if (task == null || ZNet.World == null || ZNet.World.m_worldVersion != global::Version.World.DeepNorth) return false;
            try
            {
                if (!task.Wait(UploadWaitMs) || task.Result == null) return false;
            }
            catch (AggregateException ex)
            {
                FiresLogger.LogWarning($"[FiresMinimap] reading the cached minimap on a worker failed ({ex.InnerException?.Message}); vanilla loads it.");
                return false;
            }
            var decoded = task.Result;
            if (!FiresMinimapCache.HasPaths(minimap, decoded.Mask.Path, decoded.Biome.Path, decoded.Height.Path, decoded.Meta.Path)
                || !decoded.Mask.Unchanged() || !decoded.Biome.Unchanged() || !decoded.Height.Unchanged() || !decoded.Meta.Unchanged()
                || decoded.Seed != worldSeed || decoded.Version != (int)global::Version.CachedMinimap.Original)
                return false;
            var clock = Stopwatch.StartNew();
            var forest = ForestMaskTexture(minimap);
            forest.SetPixels32(decoded.MaskPixels);
            forest.Apply();
            minimap.m_mapTexture.SetPixels32(decoded.BiomePixels);
            minimap.m_mapTexture.Apply();
            var heights = HeightTexture(minimap);
            heights.SetPixels(decoded.HeightPixels);
            heights.Apply();
            FiresLogger.LogInfo($"[FiresMinimap] cached minimap uploaded in {clock.ElapsedMilliseconds} ms; its files were read on a worker "
                + $"({decoded.DecodeMs} ms) while the world initialised.");
            return true;
        }

        private static Decoded Decode(string mask, string biome, string height, string meta)
        {
            var clock = Stopwatch.StartNew();
            var decoded = new Decoded
            {
                Mask = new Stamp(mask),
                Biome = new Stamp(biome),
                Height = new Stamp(height),
                Meta = new Stamp(meta),
            };
            byte[] metaBytes = File.ReadAllBytes(meta);
            if (metaBytes.Length < MetaBytes) return null;
            decoded.Seed = BitConverter.ToInt32(metaBytes, MetaSeedOffset);
            decoded.Version = BitConverter.ToInt32(metaBytes, MetaVersionOffset);
            decoded.MaskPixels = Utils.CompressedBufferToColors(File.ReadAllBytes(mask));
            decoded.BiomePixels = Utils.CompressedBufferToColors(File.ReadAllBytes(biome));
            decoded.HeightPixels = Utils.CompressedHalfBufferToRedChannel(File.ReadAllBytes(height));
            decoded.DecodeMs = clock.ElapsedMilliseconds;
            return decoded;
        }
    }
}
