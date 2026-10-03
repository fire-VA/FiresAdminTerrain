using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── MINIMAP CACHE ON DEDICATED-SERVER CLIENTS ───────────────────────────
    // Vanilla draws the world minimap by sampling GetBiome + GetBiomeHeight for
    // every pixel (5.7 s on RPGWorld's login frame) and caches the result beside
    // the world save. On single player and on a listen host that cache works. On
    // a DEDICATED-SERVER CLIENT it never does, so every join redraws the whole map:
    //
    //   1. Minimap.Start sets m_saveDirectory and the four cache paths only when
    //      ZNet.World != null — and a client's world arrives later, in
    //      RPC_PeerInfo. The paths stay empty.
    //   2. GenerateWorldMap saves only when m_saveDirectory is set, so (1) means
    //      the client generates the full map and throws it away. Nothing is ever
    //      written; there is no cache to load next time.
    //   3. TryLoadMinimapTextureData also requires ZNet.World.m_worldVersion ==
    //      DeepNorth. A client builds its World from the handshake, which carries
    //      name/seed/uid/worldGenVersion but never the save version, so it stays
    //      0 and the load is refused even when the files exist.
    //
    // The fix fills in the paths (so the next generate SAVES) and satisfies the
    // version gate for that one call on clients only. On a client the version is
    // not stale, just unknown: the map is drawn by this build's worldgen, which is
    // exactly what the gate is protecting. Servers and single player keep
    // vanilla's check untouched.
    //
    // FAT's WATER OVERLAY stays correct under caching, and it is worth knowing
    // why before touching either file: SaveMapTextureDataToDisk runs INSIDE
    // GenerateWorldMap, before FiresMinimapRivers' postfix stamps water onto the
    // textures, so the cache holds the plain base map. FiresMinimapRivers stamps
    // again after every successful TryLoad, so water is always drawn fresh on top
    // of whichever base map is in use. The identity below therefore only has to
    // describe the base terrain.
    //
    // STALENESS. Vanilla validates a cached map by seed and format version only.
    // FAT's worldgen patches can change the terrain under an unchanged seed (a
    // re-baked world keeps its name), so a sidecar records FAT's terrain identity
    // when the map is saved and a mismatch deletes the cache — vanilla then
    // redraws and re-saves it once.
    internal static class FiresMinimapCache
    {
        private const string SidecarName = "cacheMinimapFires";
        private const string VanillaIdentity = "vanilla";
        private const int GridSampleStride = 4093;   // prime, so the sample walks the whole grid unevenly

        private static readonly AccessTools.FieldRef<Minimap, string> SaveDirectory =
            AccessTools.FieldRefAccess<Minimap, string>("m_saveDirectory");
        private static readonly AccessTools.FieldRef<Minimap, string> MaskPath =
            AccessTools.FieldRefAccess<Minimap, string>("m_cachedMinimapMaskTexturePath");
        private static readonly AccessTools.FieldRef<Minimap, string> BiomePath =
            AccessTools.FieldRefAccess<Minimap, string>("m_cachedMinimapBiomeTexturePath");
        private static readonly AccessTools.FieldRef<Minimap, string> HeightPath =
            AccessTools.FieldRefAccess<Minimap, string>("m_cachedMinimapHeightTexturePath");
        private static readonly AccessTools.FieldRef<Minimap, string> MetaPath =
            AccessTools.FieldRefAccess<Minimap, string>("m_cachedMinimapMetaPath");

        private static System.Reflection.MethodInfo s_completePath;

        // The prefix and finalizer of FAT's one Minimap.TryLoadMinimapTextureData hook (FatVanillaHooks).
        internal static void BeforeLoad(Minimap minimap, out Version.World borrowedFrom)
        {
            borrowedFrom = (Version.World)(-1);
            var world = ZNet.World;
            if (world == null) return;
            try
            {
                EnsurePaths(minimap, world);
                DropIfStale(minimap);
                if (IsDedicatedClient() && world.m_worldVersion != Version.World.DeepNorth)
                {
                    borrowedFrom = world.m_worldVersion;
                    world.m_worldVersion = Version.World.DeepNorth;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresMinimap] cache prep failed ({ex.Message}) — vanilla behaviour this join.");
            }
        }

        // A finalizer, not a postfix: the version must be restored even if the
        // load throws, or the client's World keeps a borrowed value.
        internal static Exception AfterLoad(Exception exception, Version.World borrowedFrom, bool loaded)
        {
            var world = ZNet.World;
            if (world != null && (int)borrowedFrom != -1) world.m_worldVersion = borrowedFrom;
            if (exception == null && loaded && IsDedicatedClient())
                FiresLogger.LogInfo("[FiresMinimap] loaded the cached world minimap — no redraw this join.");
            return exception;
        }

        // Stamp FAT's terrain identity beside every freshly saved map, so the
        // cache can later be told apart from one drawn over different terrain.
        [HarmonyPatch(typeof(Minimap), "SaveMapTextureDataToDisk")]
        private static class Minimap_Save_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(Minimap __instance)
            {
                try
                {
                    string dir = SaveDirectory(__instance);
                    if (string.IsNullOrEmpty(dir)) return;
                    File.WriteAllText(Path.Combine(dir, SidecarName), TerrainIdentity());
                    if (IsDedicatedClient())
                        FiresLogger.LogInfo("[FiresMinimap] world minimap saved — later joins load it instead of redrawing.");
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresMinimap] could not stamp the minimap cache: {ex.Message}");
                }
            }
        }

        // Mirrors Minimap.Start's own path setup, which a dedicated client skips.
        private static void EnsurePaths(Minimap map, World world)
        {
            if (!string.IsNullOrEmpty(SaveDirectory(map))) return;

            string dir = world.GetSaveDirectory(FileHelpers.FileSource.Local);
            if (FileHelpers.LocalStorageSupport == LocalStorageSupport.Supported) Directory.CreateDirectory(dir);
            SaveDirectory(map) = dir;
            MaskPath(map) = CompletePath(dir, "cacheMinimapMask");
            BiomePath(map) = CompletePath(dir, "cacheMinimapBiome");
            HeightPath(map) = CompletePath(dir, "cacheMinimapHeight");
            MetaPath(map) = CompletePath(dir, "cacheMinimapMeta");
        }

        internal static bool HasPaths(Minimap map, string mask, string biome, string height, string meta)
            => MaskPath(map) == mask && BiomePath(map) == biome && HeightPath(map) == height && MetaPath(map) == meta;

        internal static string CompletePath(string dir, string name)
        {
            if (s_completePath == null)
                s_completePath = AccessTools.Method(typeof(Minimap), "GetCompleteTexturePath");
            return (string)s_completePath.Invoke(null, new object[] { dir, name });
        }

        private static void DropIfStale(Minimap map)
        {
            string dir = SaveDirectory(map);
            string meta = MetaPath(map);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(meta) || !File.Exists(meta)) return;

            string sidecar = Path.Combine(dir, SidecarName);
            string stamped = File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : null;
            string current = TerrainIdentity();
            if (stamped == current) return;

            // Unstamped (drawn before this build) or drawn over different terrain.
            foreach (var path in new[] { MaskPath(map), BiomePath(map), HeightPath(map), meta, sidecar })
            {
                try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
            }
            FiresLogger.LogInfo(stamped == null
                ? "[FiresMinimap] cached minimap predates the terrain stamp — redrawing once and stamping it."
                : $"[FiresMinimap] cached minimap was drawn over different terrain ({stamped} -> {current}) — redrawing.");
        }

        // What the map's pixels actually depend on beyond seed + format: whether
        // FAT's generator owns the terrain, and if so which grid it is serving —
        // plus, on a one-zone-at-a-time world, which zones are open.
        private static string TerrainIdentity()
        {
            string zones = Zones.ZoneModeRegistry.MapIdentity();
            return zones == null ? GeneratorIdentity() : GeneratorIdentity() + "|" + zones;
        }

        private static string GeneratorIdentity()
        {
            if (!FiresWorldGenBase.Active) return VanillaIdentity;
            var grid = FiresWorldGenBase.PackedGridForIdentity;
            if (grid == null) return "fires:unknown";
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < grid.Length; i += GridSampleStride) h = (h ^ (ushort)grid[i]) * 16777619u;
                return $"fires:{grid.Length}:{FiresWorldGenBase.GridHalf:0}:{h:x8}";
            }
        }

        private static bool IsDedicatedClient()
            => ZNet.instance != null && !ZNet.instance.IsServer();
    }
}
