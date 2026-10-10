using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Biome
{
    /// <summary>
    /// Handles file-based persistence for biome overlay paints.
    /// 
    /// PERSISTENCE STRATEGY:
    /// - Server saves biome paint data to a world-specific file
    /// - Data is loaded when world starts
    /// - Multiplayer: Server is authoritative, clients receive sync on join
    /// 
    /// This complements the TerrainComp ZDO persistence by providing
    /// a file-based backup for biome paints that span multiple zones.
    /// 
    /// FILE FORMAT:
    /// - Header: Magic bytes + version + paint count
    /// - For each paint: Position, radius, biome type
    /// - Compressed with GZip for efficient storage
    /// 
    /// INTEGRATION:
    /// - Hook ZNet.SaveWorld to save biome data
    /// - Hook ZNet.Start to load biome data
    /// </summary>
    public static class BiomeFilePersistence
    {
        private const string FILE_EXTENSION = ".biomedata";
        private const int FILE_VERSION = 1;
        private const int MAGIC_HEADER = 0x42494F4D; // "BIOM"

        private static bool _initialized = false;

        // Server-side cache of all biome paint data
        private static Dictionary<int, BiomePaintEntry> _serverBiomeCache = new Dictionary<int, BiomePaintEntry>();

        /// <summary>
        /// Data structure for serialized biome paint.
        /// </summary>
        [Serializable]
        public class BiomePaintEntry
        {
            public int StableHash;
            public Vector3 Position;
            public float Radius;
            public Heightmap.Biome Biome;

            public BiomePaintEntry() { }

            public BiomePaintEntry(Vector3 position, float radius, Heightmap.Biome biome, int stableHash = 0)
            {
                Position = position;
                Radius = radius;
                Biome = biome;
                StableHash = stableHash != 0 ? stableHash : GenerateStableHash(position);
            }

            public static int GenerateStableHash(Vector3 position)
            {
                int qx = Mathf.RoundToInt(position.x * 10f);
                int qy = Mathf.RoundToInt(position.y * 10f);
                int qz = Mathf.RoundToInt(position.z * 10f);

                long ticks = DateTime.UtcNow.Ticks;
                int timePart = (int)(ticks & 0xFFFFFF);
                int random = UnityEngine.Random.Range(0, 256);

                unchecked
                {
                    int hash = 29;
                    hash = hash * 41 + qx;
                    hash = hash * 41 + qy;
                    hash = hash * 41 + qz;
                    hash = hash * 41 + timePart;
                    hash = hash * 41 + random;
                    return hash;
                }
            }

            public byte[] Serialize()
            {
                using (var ms = new MemoryStream())
                using (var writer = new BinaryWriter(ms))
                {
                    writer.Write(StableHash);
                    writer.Write(Position.x);
                    writer.Write(Position.y);
                    writer.Write(Position.z);
                    writer.Write(Radius);
                    writer.Write((int)Biome);
                    return ms.ToArray();
                }
            }

            public static BiomePaintEntry Deserialize(byte[] data)
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    var entry = new BiomePaintEntry();
                    entry.StableHash = reader.ReadInt32();
                    entry.Position = new Vector3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );
                    entry.Radius = reader.ReadSingle();
                    entry.Biome = (Heightmap.Biome)reader.ReadInt32();
                    return entry;
                }
            }
        }

        /// <summary>
        /// Initializes the persistence system.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            FiresLogger.LogInfo("[BiomeFilePersistence] Initialized biome persistence system");
        }

        // A local world's painted biomes: inside its own save folder (<worlds_local>\<world>\<world>.biomedata) when the world
        // is a 1.0 folder world, so worlds_local holds one folder per world (Fire 10-03); beside the save for an old
        // single-file world. A loose copy left by an older build moves into the folder (the newer one wins).
        internal static string LocalPathFor(string worldName)
            => InWorldFolder(SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local), worldName);

        private static string InWorldFolder(string root, string worldName)
        {
            string loose = Path.Combine(root, worldName + FILE_EXTENSION);
            string folder = Path.Combine(root, worldName);
            if (!Directory.Exists(folder)) return loose;
            string inside = Path.Combine(folder, worldName + FILE_EXTENSION);
            try
            {
                if (File.Exists(loose) && (!File.Exists(inside) || File.GetLastWriteTimeUtc(loose) > File.GetLastWriteTimeUtc(inside)))
                {
                    if (File.Exists(inside)) File.Delete(inside);
                    File.Move(loose, inside);
                    FiresLogger.LogInfo($"[BiomeFilePersistence] moved {worldName}{FILE_EXTENSION} into the world's folder.");
                }
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[BiomeFilePersistence] could not move {worldName}{FILE_EXTENSION}: {ex.Message}"); }
            return inside;
        }

        /// <summary>
        /// Gets the biome data file path for the current world.
        /// </summary>
        public static string GetBiomeDataPath()
        {
            string worldName = "unknown";

            if (ZNet.instance != null)
            {
                try
                {
                    var worldProp = typeof(ZNet).GetProperty("World");
                    if (worldProp != null)
                    {
                        var world = worldProp.GetValue(null) as World;
                        if (world != null)
                        {
                            worldName = world.m_name;
                            string worldFolder = SaveSystem.GetWorldsSaveRootPath(world.m_fileSource);
                            if (!string.IsNullOrEmpty(worldFolder) && !string.IsNullOrEmpty(worldName))
                            {
                                return InWorldFolder(worldFolder, worldName);
                            }
                        }
                    }
                }
                catch { }
            }

            // Last resort: BepInEx config path
            if (worldName != "unknown")
            {
                string fallbackPath = Path.Combine(BepInEx.Paths.ConfigPath, "BiomeData", worldName + FILE_EXTENSION);
                string dir = Path.GetDirectoryName(fallbackPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return fallbackPath;
            }

            return null;
        }

        /// <summary>
        /// Saves all biome data to disk.
        /// Called during world save.
        /// </summary>
        public static void SaveBiomeData()
        {
            if (!IsServer())
            {
                FiresLogger.LogInfo("[BiomeFilePersistence] Not server - skipping save");
                return;
            }

            string path = GetBiomeDataPath();
            if (string.IsNullOrEmpty(path))
            {
                FiresLogger.LogWarning("[BiomeFilePersistence] Cannot determine save path");
                return;
            }

            try
            {
                if (_serverBiomeCache.Count == 0)
                {
                    // No biome data - delete file if exists
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        FiresLogger.LogInfo("[BiomeFilePersistence] Deleted empty biome data file");
                    }
                    return;
                }

                FiresCore.IO.AtomicFile.Write(path, stream =>
                {
                    using (var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
                    using (var writer = new BinaryWriter(gzip))
                    {
                        // Write header
                        writer.Write(MAGIC_HEADER);
                        writer.Write(FILE_VERSION);
                        writer.Write(_serverBiomeCache.Count);

                        // Write each paint
                        foreach (var kvp in _serverBiomeCache)
                        {
                            byte[] entryData = kvp.Value.Serialize();
                            writer.Write(entryData.Length);
                            writer.Write(entryData);
                        }
                    }
                });

                FiresLogger.LogInfo($"[BiomeFilePersistence] Saved {_serverBiomeCache.Count} biome paints to {path}");
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[BiomeFilePersistence] Failed to save biome data: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads biome data from disk.
        /// Called when world starts.
        /// </summary>
        public static void LoadBiomeData()
        {
            if (!IsServer())
            {
                FiresLogger.LogInfo("[BiomeFilePersistence] Not server - skipping load");
                return;
            }

            string path = GetBiomeDataPath();
            if (string.IsNullOrEmpty(path))
            {
                FiresLogger.LogWarning("[BiomeFilePersistence] Cannot determine load path");
                return;
            }

            if (!File.Exists(path))
            {
                FiresLogger.LogInfo("[BiomeFilePersistence] No biome data file found - starting fresh");
                return;
            }

            try
            {
                var loadedPaints = new List<BiomePaintEntry>();

                using (var fs = new FileStream(path, FileMode.Open))
                using (var gzip = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress))
                using (var reader = new BinaryReader(gzip))
                {
                    // Read header
                    int magic = reader.ReadInt32();
                    if (magic != MAGIC_HEADER)
                    {
                        FiresLogger.LogWarning("[BiomeFilePersistence] Invalid biome data file - wrong magic header");
                        return;
                    }

                    int version = reader.ReadInt32();
                    if (version > FILE_VERSION)
                    {
                        FiresLogger.LogWarning($"[BiomeFilePersistence] Biome data file is from newer version ({version} > {FILE_VERSION})");
                    }

                    int paintCount = reader.ReadInt32();

                    // Read each paint
                    for (int i = 0; i < paintCount; i++)
                    {
                        int dataLength = reader.ReadInt32();
                        byte[] entryData = reader.ReadBytes(dataLength);

                        try
                        {
                            var entry = BiomePaintEntry.Deserialize(entryData);
                            loadedPaints.Add(entry);
                            _serverBiomeCache[entry.StableHash] = entry;
                        }
                        catch (Exception paintEx)
                        {
                            FiresLogger.LogWarning($"[BiomeFilePersistence] Failed to deserialize paint {i}: {paintEx.Message}");
                        }
                    }
                }

                // Restore biome paints (deferred until heightmaps are ready)
                if (loadedPaints.Count > 0)
                {
                    RestoreBiomePaintsDelayed(loadedPaints);
                }

                FiresLogger.LogInfo($"[BiomeFilePersistence] Loaded {loadedPaints.Count} biome paints from {path}");
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[BiomeFilePersistence] Failed to load biome data: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores biome paints after a delay to ensure heightmaps are ready.
        /// </summary>
        private static void RestoreBiomePaintsDelayed(List<BiomePaintEntry> paints)
        {
            // Use coroutine via Game instance
            if (Game.instance != null)
            {
                Game.instance.StartCoroutine(RestoreBiomePaintsCoroutine(paints));
            }
        }

        private static System.Collections.IEnumerator RestoreBiomePaintsCoroutine(List<BiomePaintEntry> paints)
        {
            // Wait for world to be fully initialized
            float timeout = 10f;
            float elapsed = 0f;

            while (ZoneSystem.instance == null && elapsed < timeout)
            {
                yield return new WaitForSeconds(0.5f);
                elapsed += 0.5f;
            }

            if (ZoneSystem.instance == null)
            {
                FiresLogger.LogWarning("[BiomeFilePersistence] ZoneSystem not available - cannot restore biome paints");
                yield break;
            }

            // Wait a bit more for heightmaps to be loaded
            yield return new WaitForSeconds(2f);

            // Restore each paint
            // Set _isRestoring to suppress per-item logging in PaintBiomeOverlayInternal
            // and ClutterManager.ClearInRadius (prevents 800+ log lines during load)
            BiomePaintPersistence.SetRestoring(true);
            int restored = 0;
            int failed = 0;
            try
            {
                foreach (var paint in paints)
                {
                    try
                    {
                        // Apply the biome paint using the internal method (no RPC)
                        BiomePaintPersistence.PaintBiomeOverlayInternal(paint.Position, paint.Radius, paint.Biome);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        if (failed <= 3)
                        {
                            FiresLogger.LogWarning($"[BiomeFilePersistence] Failed to restore biome at {paint.Position}: {ex.Message}");
                        }
                    }

                    // Spread restoration over multiple frames to avoid lag
                    if (restored % 5 == 0)
                    {
                        yield return null;
                    }
                }
            }
            finally
            {
                BiomePaintPersistence.SetRestoring(false);
            }

            FiresLogger.LogInfo($"[BiomeFilePersistence] Restored {restored}/{paints.Count} biome paints{(failed > 0 ? $" ({failed} failed)" : "")}");
        }

        /// <summary>
        /// Records a biome paint operation (adds to server cache).
        /// Call this when biome is painted.
        /// </summary>
        public static void RecordBiomePaint(Vector3 position, float radius, Heightmap.Biome biome)
        {
            var entry = new BiomePaintEntry(position, radius, biome);
            _serverBiomeCache[entry.StableHash] = entry;
        }

        /// <summary>
        /// Records biome reset operation (removes from server cache in radius).
        /// </summary>
        public static void RecordBiomeReset(Vector3 position, float radius)
        {
            var toRemove = new List<int>();

            foreach (var kvp in _serverBiomeCache)
            {
                float dist = Vector3.Distance(kvp.Value.Position, position);
                if (dist <= radius + kvp.Value.Radius)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var hash in toRemove)
            {
                _serverBiomeCache.Remove(hash);
            }
        }

        /// <summary>
        /// Gets the server cache (read-only).
        /// </summary>
        public static IReadOnlyDictionary<int, BiomePaintEntry> GetServerCache()
        {
            return _serverBiomeCache;
        }

        /// <summary>
        /// Resets the initialized flag so Initialize() runs again on next session.
        /// Called on logout to prevent stale state on reconnect.
        /// </summary>
        public static void ResetInitialized()
        {
            _initialized = false;
        }

        /// <summary>
        /// Clears all cached data on world unload.
        /// </summary>
        public static void ClearCaches()
        {
            _serverBiomeCache.Clear();
            FiresLogger.LogInfo("[BiomeFilePersistence] Cleared biome caches");
        }

        /// <summary>
        /// Checks if we are the server or single player.
        /// IMPORTANT: Returns FALSE if ZNet is not ready yet, to prevent clients acting as servers during load.
        /// </summary>
        public static bool IsServer()
        {
            // During loading, ZNet.instance is null - we must NOT assume we're the server
            if (ZNet.instance == null)
                return false;

            return ZNet.instance.IsServer();
        }
    }
}
