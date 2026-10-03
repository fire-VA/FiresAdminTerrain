using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // A picture for every saved blueprint in the Chaos Hammer's menu (Fire, 2026-09-27, Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md).
    //   * MAP: a top-down map drawn on the server from the blueprint's own data - surface height, biome, paint, water
    //     and every built piece - so every blueprint has one, console saves and older files included, and a headless
    //     dedicated server can draw it: no camera, no GPU. Kept beside the blueprint as <name>.map.
    //   * SNAPSHOT: a camera render of the real chunk, taken by the admin's own client when they save it with the
    //     hammer and sent with the save. Kept as <name>.snap, and shown in place of the map.
    internal static class ZoneModeBlueprintIcons
    {
        internal enum Kind : byte { None = 0, Map = 1, Snapshot = 2 }

        internal const string MapExtension = ".map";
        internal const string SnapshotExtension = ".snap";

        private const int MapSize = (int)ZoneOpenSet.ZoneSize;
        private const int BytesPerTexel = 3;
        private const float WaterLevel = 30f;
        private const float FullWaterDepth = 6f;
        private const float ShadeFloor = 0.55f;
        private const float ShadeGain = 0.45f;
        private const float SlopeScale = 0.35f;
        private const float PaintVisible = 0.6f;
        private const int SnapshotSize = 128;
        private const float SnapshotFieldOfView = 34f;
        private const float SnapshotPitch = 52f;
        private const float SnapshotYaw = 45f;
        private const float SnapshotFit = 1.25f;
        private const float SnapshotFarClip = 600f;
        private const int MaxSnapshotBytes = 256 * 1024;

        private static readonly Vector3 LightFrom = new Vector3(-1f, 1.4f, 1f).normalized;
        private static readonly Color32 BuiltTexel = new Color32(242, 190, 90, 255);
        private static readonly Color32 FrameTexel = new Color32(24, 20, 16, 255);
        private static readonly Color WaterTint = new Color(0.16f, 0.34f, 0.52f);
        private static readonly Color DirtTint = new Color(0.45f, 0.35f, 0.24f);
        private static readonly Color CultivatedTint = new Color(0.33f, 0.23f, 0.16f);
        private static readonly Color PavedTint = new Color(0.56f, 0.55f, 0.53f);

        // GenWaterField.BiomeCodeAt: Meadows, Black Forest, Swamp, Mountain, Plains, Mistlands, Ashlands, Deep North, Ocean.
        private static readonly Color[] BiomeTints =
        {
            new Color(0.47f, 0.60f, 0.28f),
            new Color(0.22f, 0.32f, 0.19f),
            new Color(0.35f, 0.32f, 0.21f),
            new Color(0.84f, 0.86f, 0.90f),
            new Color(0.77f, 0.69f, 0.38f),
            new Color(0.41f, 0.39f, 0.46f),
            new Color(0.44f, 0.23f, 0.19f),
            new Color(0.80f, 0.86f, 0.95f),
            new Color(0.20f, 0.35f, 0.50f),
        };

        // ── The map, on the server ───────────────────────────────────────────
        // `volume` is a chunk blueprint's zone volume (VoxelWorld.CaptureZoneVolume layout); `paint` and `biome` its
        // 65x65 surface; `builtAt` the positions of its built pieces relative to the zone's minimum corner.
        internal static byte[] DrawMap(sbyte[] volume, int chunkCountY, int chunkMinY, Color32[] paint, byte[] biome, int pitch,
            List<Vector2> builtAt)
        {
            var heights = SurfaceHeights(volume, chunkCountY, chunkMinY);
            var texels = new Color32[MapSize * MapSize];
            for (int z = 0; z < MapSize; z++)
            {
                for (int x = 0; x < MapSize; x++)
                {
                    float h = heights[z * MapSize + x];
                    int surface = z * pitch + x;
                    var color = Tint(biome != null && surface < biome.Length ? biome[surface] : (byte)0);
                    if (paint != null && surface < paint.Length) color = Painted(color, paint[surface]);
                    color *= Shade(heights, x, z);
                    if (h < WaterLevel) color = Color.Lerp(color, WaterTint, Mathf.Clamp01((WaterLevel - h) / FullWaterDepth));
                    color.a = 1f;
                    texels[z * MapSize + x] = color;
                }
            }
            if (builtAt != null)
                foreach (var at in builtAt)
                {
                    int x = Mathf.FloorToInt(at.x), z = Mathf.FloorToInt(at.y);
                    if (x >= 0 && z >= 0 && x < MapSize && z < MapSize) texels[z * MapSize + x] = BuiltTexel;
                }
            for (int i = 0; i < MapSize; i++)
            {
                texels[i] = FrameTexel;
                texels[(MapSize - 1) * MapSize + i] = FrameTexel;
                texels[i * MapSize] = FrameTexel;
                texels[i * MapSize + MapSize - 1] = FrameTexel;
            }
            return Pack(texels);
        }

        // The topmost solid sample of every metre column, from the blueprint's own volume: what the chunk really is,
        // dug and built land included, rather than what the generator would make there.
        private static float[] SurfaceHeights(sbyte[] volume, int chunkCountY, int chunkMinY)
        {
            var heights = new float[MapSize * MapSize];
            int cells = VoxelChunk.CELLS, samples = VoxelChunk.SAMPLES;
            for (int z = 0; z < MapSize; z++)
            {
                for (int x = 0; x < MapSize; x++)
                {
                    int dx = x / cells, dz = z / cells, lx = x % cells, lz = z % cells;
                    float surface = chunkMinY * cells;
                    bool hit = false;
                    for (int layer = chunkCountY - 1; layer >= 0 && !hit; layer--)
                    {
                        int slot = (layer * VoxelWorld.ZoneChunksPerAxis + dz) * VoxelWorld.ZoneChunksPerAxis + dx;
                        int baseIndex = slot * VoxelChunk.SampleCount;
                        for (int ly = cells - 1; ly >= 0; ly--)
                        {
                            sbyte density = volume[baseIndex + (lx + 1) + samples * ((ly + 1) + samples * (lz + 1))];
                            if (density <= 0) continue;
                            sbyte above = volume[baseIndex + (lx + 1) + samples * ((ly + 2) + samples * (lz + 1))];
                            float crossing = above < 0 ? density / (float)(density - above) : 0f;
                            surface = (chunkMinY + layer) * cells + ly + crossing;
                            hit = true;
                            break;
                        }
                    }
                    heights[z * MapSize + x] = surface;
                }
            }
            return heights;
        }

        private static Color Tint(byte code) => code < BiomeTints.Length ? BiomeTints[code] : BiomeTints[0];

        // Vanilla's paint mask: red is dirt, green cultivated ground, blue paved road.
        private static Color Painted(Color color, Color32 paint)
        {
            float dirt = paint.r / 255f, cultivated = paint.g / 255f, paved = paint.b / 255f;
            color = Color.Lerp(color, DirtTint, dirt * PaintVisible);
            color = Color.Lerp(color, CultivatedTint, cultivated * PaintVisible);
            return Color.Lerp(color, PavedTint, paved * PaintVisible);
        }

        private static float Shade(float[] heights, int x, int z)
        {
            float left = heights[z * MapSize + Mathf.Max(0, x - 1)], right = heights[z * MapSize + Mathf.Min(MapSize - 1, x + 1)];
            float down = heights[Mathf.Max(0, z - 1) * MapSize + x], up = heights[Mathf.Min(MapSize - 1, z + 1) * MapSize + x];
            var normal = new Vector3((left - right) * SlopeScale, 1f, (down - up) * SlopeScale).normalized;
            return ShadeFloor + ShadeGain * Mathf.Clamp01(Vector3.Dot(normal, LightFrom));
        }

        private static byte[] Pack(Color32[] texels)
        {
            var raw = new byte[texels.Length * BytesPerTexel];
            for (int i = 0; i < texels.Length; i++)
            {
                raw[i * BytesPerTexel] = texels[i].r;
                raw[i * BytesPerTexel + 1] = texels[i].g;
                raw[i * BytesPerTexel + 2] = texels[i].b;
            }
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress)) deflate.Write(raw, 0, raw.Length);
                return output.ToArray();
            }
        }

        // ── Files beside the blueprint ───────────────────────────────────────
        internal static string SidecarPath(string blueprintPath, Kind kind)
            => Path.ChangeExtension(blueprintPath, kind == Kind.Snapshot ? SnapshotExtension : MapExtension);

        // The picture to show for a blueprint: its snapshot when it has one, otherwise its map.
        internal static bool TryLoad(string blueprintPath, out Kind kind, out byte[] data)
        {
            foreach (var candidate in new[] { Kind.Snapshot, Kind.Map })
            {
                string path = SidecarPath(blueprintPath, candidate);
                if (!File.Exists(path)) continue;
                try
                {
                    data = File.ReadAllBytes(path);
                    kind = candidate;
                    return data.Length > 0;
                }
                catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] blueprint picture {Path.GetFileName(path)} unreadable: {ex.Message}"); }
            }
            kind = Kind.None;
            data = null;
            return false;
        }

        internal static bool IsCurrent(string blueprintPath, Kind kind)
        {
            string path = SidecarPath(blueprintPath, kind);
            return File.Exists(path) && File.GetLastWriteTimeUtc(path) >= File.GetLastWriteTimeUtc(blueprintPath);
        }

        internal static void Write(string blueprintPath, Kind kind, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            try { File.WriteAllBytes(SidecarPath(blueprintPath, kind), data); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] could not keep the {kind} for {Path.GetFileName(blueprintPath)}: {ex.Message}"); }
        }

        internal static void Delete(string blueprintPath)
        {
            foreach (var kind in new[] { Kind.Map, Kind.Snapshot })
            {
                string path = SidecarPath(blueprintPath, kind);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { FiresLogger.LogVerbose($"[FiresZones] could not remove {Path.GetFileName(path)}: {ex.Message}"); }
            }
        }

        // FNV-1a over the bytes: a client asks for a picture again only when this changes.
        internal static uint Hash(byte[] data)
        {
            if (data == null) return 0u;
            uint hash = 2166136261u;
            foreach (byte b in data) hash = (hash ^ b) * 16777619u;
            return hash == 0u ? 1u : hash;
        }

        internal static bool IsAcceptableSnapshot(byte[] png) => png != null && png.Length > 0 && png.Length <= MaxSnapshotBytes;

        // ── Pictures, on the client ──────────────────────────────────────────
        internal static Sprite ToSprite(Kind kind, byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                Texture2D texture;
                if (kind == Kind.Snapshot)
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    if (!Png.Load(texture, data)) return null;
                }
                else
                {
                    texture = new Texture2D(MapSize, MapSize, TextureFormat.RGB24, false);
                    texture.LoadRawTextureData(Unpack(data, MapSize * MapSize * BytesPerTexel));
                    texture.Apply(false, true);
                }
                texture.wrapMode = TextureWrapMode.Clamp;
                return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] a blueprint {kind} could not be shown ({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
        }

        private static byte[] Unpack(byte[] packed, int count)
        {
            var bytes = new byte[count];
            using (var input = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                int read = 0;
                while (read < count)
                {
                    int got = input.Read(bytes, read, count - read);
                    if (got <= 0) break;
                    read += got;
                }
            }
            return bytes;
        }

        // A zone's snapshot is framed on its centre at ground height.
        internal static byte[] TakeSnapshot(Vector2s zone)
        {
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            if (VoxelZoneStreamer.TryGroundHeight(centre.x, centre.z, out float ground)) centre.y = ground;
            return TakeSnapshot(centre, ZoneOpenSet.ZoneSize);
        }

        // The admin's own view of what they are saving, `span` metres across round `focus`, from above and to one side,
        // so builds read as builds. Rendered with a throwaway camera copied from the game's own, into a small texture;
        // null when there is no camera to copy.
        internal static byte[] TakeSnapshot(Vector3 focus, float span)
        {
            var source = Utils.GetMainCamera();
            if (source == null) return null;
            var holder = new GameObject("fires_blueprint_snapshot");
            var target = new RenderTexture(SnapshotSize, SnapshotSize, 24, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                var camera = holder.AddComponent<Camera>();
                camera.CopyFrom(source);
                camera.enabled = false;
                camera.targetTexture = target;
                camera.fieldOfView = SnapshotFieldOfView;
                camera.farClipPlane = SnapshotFarClip;

                float distance = span * SnapshotFit * 0.5f / Mathf.Tan(SnapshotFieldOfView * 0.5f * Mathf.Deg2Rad);
                var rotation = Quaternion.Euler(SnapshotPitch, SnapshotYaw, 0f);
                holder.transform.SetPositionAndRotation(focus - rotation * Vector3.forward * distance, rotation);

                camera.Render();
                RenderTexture.active = target;
                var texture = new Texture2D(SnapshotSize, SnapshotSize, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0f, 0f, SnapshotSize, SnapshotSize), 0, 0);
                texture.Apply(false);
                byte[] png = Png.Encode(texture);
                UnityEngine.Object.Destroy(texture);
                return png;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresZones] the chunk snapshot could not be taken ({ex.GetType().Name}: {ex.Message}); the map is used.");
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                target.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(holder);
            }
        }

        // ImageConversion by reflection: a compile-time reference to UnityEngine.ImageConversionModule pulls in
        // netstandard 2.1 against FAT's 2.0 (CS1705), the same clash Core works round in FiresModIcons.
        private static class Png
        {
            private const string ConversionType = "UnityEngine.ImageConversion, UnityEngine.ImageConversionModule";
            private static readonly Type Conversion = Type.GetType(ConversionType);
            private static readonly System.Reflection.MethodInfo LoadMethod =
                Conversion?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
            private static readonly System.Reflection.MethodInfo EncodeMethod =
                Conversion?.GetMethod("EncodeToPNG", new[] { typeof(Texture2D) });

            internal static bool Load(Texture2D texture, byte[] data)
                => LoadMethod != null && LoadMethod.Invoke(null, new object[] { texture, data }) is bool loaded && loaded;

            internal static byte[] Encode(Texture2D texture)
                => EncodeMethod?.Invoke(null, new object[] { texture }) as byte[];
        }
    }
}
