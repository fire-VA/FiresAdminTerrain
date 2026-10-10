using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FiresCore.Materials;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Modules.Utilities;
using VerdantsAscent.Terrain.Voxel;
using Object = UnityEngine.Object;

namespace VerdantsAscent.Terrain.Zones
{
    // Skylands in a one-zone-at-a-time world. The island registry holds them (FiresSkyIslands.Zones.cs);
    // this drives them:
    //   server — whenever the open set changes, switch on every skyland site now within reach, publish the
    //            grown set, and dress each new skyland once with real objects (it floats over closed sea,
    //            where vanilla population never runs), including the vanilla location a skyland may hold;
    //   client — a far-view mesh of every skyland on the horizon (its ponds and rivers dug in and filled) until
    //            its voxel chunks are in, and the short fall from its rim that thins out in mid-air.
    internal static class ZoneModeSkylands
    {
        private const float ServerCheckSeconds = 2f;
        private const float ClientTickSeconds = 0.5f;
        private const float VisualRange = 1600f;
        private const float FallFade = 0.8f;
        private const float FallLipOut = 0.6f;
        private const string RisingRpc = "FiresZones_SkylandsRising";

        private static int s_lastVersion = -1;
        private static float s_nextServerCheck;
        private static bool s_dressedLoaded;
        private static readonly Dictionary<uint, DressRecord> s_dressed = new Dictionary<uint, DressRecord>();
        private static readonly Dictionary<Heightmap.Biome, int> s_dressedBiomes = new Dictionary<Heightmap.Biome, int>();
        private static readonly HashSet<string> s_missingPrefabs = new HashSet<string>();
        private static readonly List<int> s_added = new List<int>();
        private static readonly List<Disc> s_rising = new List<Disc>();
        private static ZRoutedRpc s_risingBus;
        private static readonly List<Vector3> s_pos = new List<Vector3>(128);
        private static readonly List<Quaternion> s_rot = new List<Quaternion>(128);
        private static readonly List<string> s_name = new List<string>(128);
        private static readonly List<Vector3> s_scale = new List<Vector3>(128);
        private const float GrassLayerRange = 200f;
        private const float GrassResetRadius = 160f;
        private static bool s_cameraAboveIslands;
        private static readonly HashSet<uint> s_sitesPending = new HashSet<uint>();
        private static readonly HashSet<string> s_missingSites = new HashSet<string>();
        private static readonly List<GameObject> s_siteObjects = new List<GameObject>();

        private delegate GameObject SpawnLocationCall(ZoneSystem zoneSystem, ZoneSystem.ZoneLocation location, int seed, Vector3 position,
            Quaternion rotation, ZoneSystem.SpawnMode mode, List<GameObject> spawned, bool cheated);

        private static SpawnLocationCall s_spawnLocation;
        private static bool s_spawnLocationResolved;

        private static SpawnLocationCall VanillaSpawnLocation()
        {
            if (s_spawnLocationResolved) return s_spawnLocation;
            s_spawnLocationResolved = true;
            var method = AccessTools.Method(typeof(ZoneSystem), "SpawnLocation", new[]
            {
                typeof(ZoneSystem.ZoneLocation), typeof(int), typeof(Vector3), typeof(Quaternion), typeof(ZoneSystem.SpawnMode),
                typeof(List<GameObject>), typeof(bool),
            });
            if (method == null) FiresLogger.LogWarning("[FiresZones] ZoneSystem.SpawnLocation was not found; skylands hold no locations.");
            else s_spawnLocation = AccessTools.MethodDelegate<SpawnLocationCall>(method);
            return s_spawnLocation;
        }

        // What a skyland was dressed with. Biome None marks a record from before skylands had biomes: its dressing
        // came from the old rules, so it is cleared and dressed again. DressSiteLayout marks dressing laid out around
        // the skyland's location clearing, DressLocation a location placed (or none to place), DressRolledRims dressing
        // laid out on rims that roll over into the underside (older dressing stood on the hard-edged rim), DressGround dressing
        // laid out on the hills, cliffs and waters a big skyland carries (older dressing stood on its plain dome). DressNames dressing
        // laid out with every palette name resolving to a networked prefab (an Ashlands tree was named after its asset file).
        private readonly struct DressRecord
        {
            internal readonly int Layers;
            internal readonly Heightmap.Biome Biome;

            internal DressRecord(int layers, Heightmap.Biome biome)
            {
                Layers = layers;
                Biome = biome;
            }
        }

        private sealed class FarPiece
        {
            internal Vector2s Zone;
            internal GameObject View;
            internal Mesh Mesh;
        }

        private sealed class Visual
        {
            internal Heightmap.Biome Biome;
            internal readonly List<FarPiece> FarPieces = new List<FarPiece>();
            internal readonly List<FarPiece> FarWater = new List<FarPiece>();
            internal bool WaterBuilt;
            internal GameObject Fall;
            internal Mesh FallMesh;
        }

        private static readonly Dictionary<uint, Visual> s_visuals = new Dictionary<uint, Visual>();
        private static readonly List<uint> s_nearIds = new List<uint>();
        private static readonly List<int> s_nearIndices = new List<int>();
        private static readonly HashSet<uint> s_nearSet = new HashSet<uint>();
        private static readonly List<uint> s_dropIds = new List<uint>();
        private static readonly List<Color32> s_pieceColours = new List<Color32>();
        private static readonly List<FiresWaterfalls.Column> s_fallColumns = new List<FiresWaterfalls.Column>(8);
        private static readonly List<FiresSkyIslands.SkylandFall> s_falls = new List<FiresSkyIslands.SkylandFall>(4);
        private static readonly List<Vector3> s_fallSites = new List<Vector3>(1);
        private static readonly List<FiresWaterfallMist.Site> s_mistSites = new List<FiresWaterfallMist.Site>(2);
        private static GameObject s_root;
        private static float s_nextClientTick;
        private static bool s_headless;
        private static bool s_headlessKnown;

        // ── World init: a zone world with stock generation owns an island registry too ─────────────
        internal static void OnWorldInitialized(World world)
        {
            if (world == null || world.m_menu || !ZoneModeRegistry.ModeActive || FiresWorldGenBase.Active) return;
            if (!FiresVoxelConfig.BuildsSkyIslands) return;
            FiresSkyIslands.ShapeSeed = world.m_seed;
            FiresSkyIslands.Initialize(world);
        }

        // ── Server ──────────────────────────────────────────────────────────────────────────────────
        internal static void ServerTick()
        {
            EnsureRisingRpc();
            if (Time.time < s_nextServerCheck) return;
            s_nextServerCheck = Time.time + ServerCheckSeconds;
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || ZNet.World == null || ZNetScene.instance == null || ZDOMan.instance == null) return;
            if (!ZoneModeRegistry.Seeded || !ZoneModeRegistry.WorldLoaded || !FiresVoxelConfig.BuildsSkyIslands
                || !FiresSkyIslands.ZoneSkylandsReady) return;
            var view = ZoneModeGate.Current;
            if (view == null) return;
            EnsureDressedLoaded();
            if (s_sitesPending.Count > 0) PlaceSites();
            if (ZoneModeRegistry.Version == s_lastVersion) return;
            s_lastVersion = ZoneModeRegistry.Version;

            var before = FiresSkyIslands.SnapshotSet();
            FiresSkyIslands.ActivateZoneSkylands(view.Open.OpenZones(), ZNet.World.m_seed, RealGround, RealWet, s_added);
            if (s_added.Count > 0)
            {
                AnnounceRising(s_added);
                var lines = new List<string>();
                FiresSkyIslands.PublishSkylands(before, lines);
                FiresLogger.LogInfo($"[FiresZones] {s_added.Count} skyland(s) rose around the open land. {string.Join(" ", lines)}");
            }
            DressUndressed();
        }

        private static float RealGround(float x, float z)
        {
            var generator = WorldGenerator.instance;
            if (generator == null) return FiresWaterfalls.SeaLevel;
            ZoneModeGate.EnterPlanning();
            try { return generator.GetHeight(x, z); }
            finally { ZoneModeGate.ExitPlanning(); }
        }

        private static bool RealWet(float x, float z)
            => GenWaterField.TryWaterSurfaceSmooth(x, z, out float level, out float depth) && depth > 0.1f && level > FiresWaterfalls.SeaLevel;

        private static void DressUndressed()
        {
            FiresSkyIslands.SkylandsNear(Vector3.zero, float.MaxValue, s_nearIds, s_nearIndices);
            int dressed = 0, objects = 0, cleared = 0;
            bool changed = false;
            s_dressedBiomes.Clear();
            for (int i = 0; i < s_nearIds.Count; i++)
            {
                uint id = s_nearIds[i];
                int index = s_nearIndices[i];
                var biome = FiresSkyIslands.SkylandBiome(index);
                bool holdsSite = FiresSkyIslands.TryGetSkylandSite(index, out _, out _, out _, out _);
                s_dressed.TryGetValue(id, out var record);
                int done = record.Layers;
                bool laidOutBlind = holdsSite && (done & FiresSkyIslands.DressSiteLayout) == 0;
                bool hardRims = (done & FiresSkyIslands.DressRolledRims) == 0;
                bool bareGround = (done & FiresSkyIslands.DressGround) == 0 && FiresSkyIslands.SkylandCarriesGround(index);
                bool wrongNames = (done & FiresSkyIslands.DressNames) == 0 && biome == Heightmap.Biome.AshLands;
                // 0.2.763 (Fire 10-06: placed veg mismatching the shown terrain): dressing laid before every item went on the
                // voxels' density surface (belly roots and rocks on the analytic underside, vines on the unrounded rim) goes once.
                bool offSurface = (done & FiresSkyIslands.DressSurface) == 0;
                if ((done & FiresSkyIslands.DressAll) != 0 && (record.Biome != biome || laidOutBlind || hardRims || bareGround || wrongNames || offSurface))
                {
                    cleared += ClearDressing(index);
                    done &= FiresSkyIslands.DressLocation;
                }
                done |= FiresSkyIslands.DressSiteLayout | FiresSkyIslands.DressRolledRims | FiresSkyIslands.DressGround | FiresSkyIslands.DressNames
                    | FiresSkyIslands.DressSurface;
                if (!holdsSite) done |= FiresSkyIslands.DressLocation;
                int missing = FiresSkyIslands.DressAll & ~done;
                if (missing != 0)
                {
                    objects += Dress(index, missing);
                    done |= missing;
                    s_dressedBiomes.TryGetValue(biome, out int count);
                    s_dressedBiomes[biome] = count + 1;
                    dressed++;
                }
                if ((done & FiresSkyIslands.DressLocation) == 0) s_sitesPending.Add(id);
                if (done == record.Layers && biome == record.Biome) continue;
                s_dressed[id] = new DressRecord(done, biome);
                changed = true;
            }
            if (changed) SaveDressed();
            if (dressed == 0) return;
            var biomes = new StringBuilder();
            foreach (var kv in s_dressedBiomes) biomes.Append(biomes.Length > 0 ? ", " : "").Append(kv.Key).Append(' ').Append(kv.Value);
            FiresLogger.LogInfo($"[FiresZones] dressed {dressed} skyland(s) ({biomes}) with {objects} tree(s), plant(s), rock(s), root(s) and vine(s)"
                + (cleared > 0 ? $"; first removed {cleared} object(s) dressed under older rules or another biome." : ".")
                + (s_sitesPending.Count > 0 ? $" {s_sitesPending.Count} skyland location(s) to place." : ""));
        }

        // Each skyland location once its prefab is loaded, through vanilla's own spawn, so its spawners, random parts
        // and proxy come with it exactly as on the ground. It is not registered as the zone's location: the zone
        // below keeps its own.
        private static void PlaceSites()
        {
            var zoneSystem = ZoneSystem.instance;
            var spawnLocation = VanillaSpawnLocation();
            if (zoneSystem == null || spawnLocation == null) return;
            FiresSkyIslands.SkylandsNear(Vector3.zero, float.MaxValue, s_nearIds, s_nearIndices);
            bool changed = false;
            for (int i = 0; i < s_nearIds.Count; i++)
            {
                uint id = s_nearIds[i];
                if (!s_sitesPending.Contains(id)) continue;
                int index = s_nearIndices[i];
                if (!FiresSkyIslands.TryGetSkylandSite(index, out string name, out var position, out var rotation, out int seed))
                {
                    s_sitesPending.Remove(id);
                    continue;
                }
                var location = FindLocation(zoneSystem, name);
                if (location == null)
                {
                    if (s_missingSites.Add(name))
                        FiresLogger.LogWarning($"[FiresZones] skyland location '{name}' is not in this game's location lists; its skyland keeps an empty clearing.");
                }
                else
                {
                    if (zoneSystem.ShouldDelayProxyLocationSpawning(location.Hash)) continue;
                    s_siteObjects.Clear();
                    spawnLocation(zoneSystem, location, seed, position, rotation, ZoneSystem.SpawnMode.Full, s_siteObjects, false);
                    FiresLogger.LogInfo($"[FiresZones] {name} placed on the {FiresSkyIslands.SkylandBiome(index)} skyland at ({position.x:0},{position.y:0},{position.z:0}).");
                }
                s_sitesPending.Remove(id);
                s_dressed.TryGetValue(id, out var record);
                s_dressed[id] = new DressRecord(record.Layers | FiresSkyIslands.DressLocation, record.Biome);
                changed = true;
            }
            if (changed) SaveDressed();
        }

        private static ZoneSystem.ZoneLocation FindLocation(ZoneSystem zoneSystem, string name)
        {
            foreach (var location in zoneSystem.m_locations)
                if (location.m_enable && location.m_prefab.IsValid && location.m_prefab.Name == name) return location;
            return null;
        }

        // Fire 10-05: dressing is removed only where the skyland code PLACED it, never by sweeping an area (a sapling a
        // player grew into a Beech on the island, or anything built there, must stay). Since 0.2.710 every dressing object
        // carries DressTag = island id + 1. Dressing from before the tag is recognised by recomputing where the previous
        // rule (the uncarved top) put it: the same prefab within LegacyMatchMetres of such a spot. Dressing laid by even older
        // rules matches neither and stays; it is counted as "left" so it shows.
        internal static readonly int DressTag = "fires_skyland_dress".GetStableHashCode();
        private const float LegacyMatchMetres = 0.15f;

        // The automatic re-dress when the dressing RULES change (biome, layout, rims, surface …) clears what the skyland code
        // placed on this island: objects carrying its DressTag (= island id + 1), and nothing else. 0.2.763 ([lead] 10-06, the
        // standing rule: remove only what we recorded placing, never by prefab or radius): an object of a dressing prefab with no
        // tag (a player's tree or rock, or dressing from before tagging existed) is left in place and counted in a Debug line.
        // The admin re-dress uses ClearPlacedDressing.
        private static int ClearDressing(int index)
        {
            var sectors = ZoneModeChunkMachines.ObjectsBySector(ZDOMan.instance);
            if (sectors == null) return 0;
            if (s_dressingPrefabs.Count == 0)
                foreach (var name in FiresSkyIslands.AllDressingPrefabs()) s_dressingPrefabs.Add(name.GetStableHashCode());
            uint id = FiresSkyIslands.IslandId(index);
            long want = (long)id + 1L;
            int untagged = 0;
            FiresSkyIslands.SkylandZones(index, s_dressingZones, FiresSkyIslands.DressingOverhang);
            s_doomed.Clear();
            foreach (var zone in s_dressingZones)
            {
                var sector = ZoneSystem.SectorToIndex(zone).Sector;
                if (sector < 0 || sector >= sectors.Length || sectors[sector] == null) continue;
                foreach (var zdo in sectors[sector])
                {
                    long tag = zdo.GetLong(DressTag, 0L);
                    if (tag == want) { s_doomed.Add(zdo); continue; }
                    if (tag == 0L && s_dressingPrefabs.Contains(zdo.GetPrefab()) && FiresSkyIslands.InSkylandDressing(index, zdo.GetPosition()))
                        untagged++;
                }
            }
            long self = ZDOMan.GetSessionID();
            foreach (var zdo in s_doomed)
            {
                zdo.SetOwner(self);
                ZDOMan.instance.DestroyZDO(zdo);
            }
            if (untagged > 0)
                FiresLogger.LogDebug($"[FiresZones] skyland {id:x8} re-dress: {s_doomed.Count} tagged dressing object(s) removed; "
                    + $"{untagged} untagged dressing-prefab object(s) left in place (not recorded as placed by the skyland code).");
            return s_doomed.Count;
        }

        private static int ClearPlacedDressing(int index, uint id, out int tagged, out int legacy, out int left)
        {
            tagged = legacy = left = 0;
            var sectors = ZoneModeChunkMachines.ObjectsBySector(ZDOMan.instance);
            if (sectors == null) return 0;
            if (s_dressingPrefabs.Count == 0)
                foreach (var name in FiresSkyIslands.AllDressingPrefabs()) s_dressingPrefabs.Add(name.GetStableHashCode());
            // Where the previous rule placed this island's dressing (prefab hash -> spots).
            s_legacySpots.Clear();
            FiresSkyIslands.CollectLegacySkylandDressing(index, s_pos, s_rot, s_name, s_scale);
            for (int i = 0; i < s_pos.Count; i++)
            {
                int hash = s_name[i].GetStableHashCode();
                if (!s_legacySpots.TryGetValue(hash, out var spots)) s_legacySpots[hash] = spots = new List<Vector3>();
                spots.Add(s_pos[i]);
            }
            long want = (long)id + 1L;
            FiresSkyIslands.SkylandZones(index, s_dressingZones, FiresSkyIslands.DressingOverhang);
            s_doomed.Clear();
            foreach (var zone in s_dressingZones)
            {
                var sector = ZoneSystem.SectorToIndex(zone).Sector;
                if (sector < 0 || sector >= sectors.Length || sectors[sector] == null) continue;
                foreach (var zdo in sectors[sector])
                {
                    long tag = zdo.GetLong(DressTag, 0L);
                    if (tag == want) { s_doomed.Add(zdo); tagged++; continue; }
                    if (tag != 0L || !s_dressingPrefabs.Contains(zdo.GetPrefab())) continue;
                    Vector3 at = zdo.GetPosition();
                    if (!FiresSkyIslands.InSkylandDressing(index, at)) continue;
                    bool match = false;
                    if (s_legacySpots.TryGetValue(zdo.GetPrefab(), out var spots))
                        foreach (var s in spots)
                            if ((s - at).sqrMagnitude <= LegacyMatchMetres * LegacyMatchMetres) { match = true; break; }
                    if (match) { s_doomed.Add(zdo); legacy++; }
                    else left++;
                }
            }
            long self = ZDOMan.GetSessionID();
            foreach (var zdo in s_doomed)
            {
                zdo.SetOwner(self);
                ZDOMan.instance.DestroyZDO(zdo);
            }
            return s_doomed.Count;
        }

        private static readonly Dictionary<int, List<Vector3>> s_legacySpots = new Dictionary<int, List<Vector3>>();

        // fires_islands_redress (server side): every zone skyland within the radius loses the dressing its code placed and is
        // dressed again on the carved top; its location and record flags are kept.
        internal static List<string> Redress(Vector3 center, float radius)
        {
            var lines = new List<string>();
            if (!ZoneModeRegistry.ModeActive) { lines.Add("[Islands] Re-dress works on zone-mode skylands only (this world is not in zone mode)."); return lines; }
            EnsureDressedLoaded();
            var ids = new List<uint>();
            var indices = new List<int>();
            FiresSkyIslands.SkylandsNear(center, radius, ids, indices);
            int islands = 0, removedTagged = 0, removedLegacy = 0, kept = 0, placed = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                uint id = ids[i];
                int index = indices[i];
                s_dressed.TryGetValue(id, out var record);
                if ((record.Layers & FiresSkyIslands.DressAll) == 0) continue;   // never dressed yet: the normal pass dresses it
                ClearPlacedDressing(index, id, out int tagged, out int legacy, out int left);
                int laid = Dress(index, FiresSkyIslands.DressAll, id);
                removedTagged += tagged; removedLegacy += legacy; kept += left; placed += laid;
                islands++;
                FiresLogger.LogDebug($"[FiresSkyIslands] re-dressed skyland {id} ({FiresSkyIslands.SkylandBiome(index)}): removed {tagged} tagged + {legacy} "
                    + $"untagged at their old spots, left {left} other object(s) of dressing kinds, placed {laid} on the voxels' surface");
            }
            lines.Add(islands == 0
                ? $"[Islands] No dressed zone skyland within {radius:0} m."
                : $"[Islands] Re-dressed {islands} skyland(s): removed {removedTagged} tagged + {removedLegacy} untagged dressing object(s) "
                  + $"at the spots the skyland code placed them, placed {placed} on the voxels' surface; left {kept} other object(s) of dressing "
                  + "kinds (placed by players, or by much older rules) untouched.");
            FiresLogger.LogInfo(lines[lines.Count - 1]);
            return lines;
        }

        private static readonly HashSet<int> s_dressingPrefabs = new HashSet<int>();
        private static readonly List<Vector2s> s_dressingZones = new List<Vector2s>();
        private static readonly List<ZDO> s_doomed = new List<ZDO>();

        private static int Dress(int index, int layers) => Dress(index, layers, FiresSkyIslands.IslandId(index));

        private static int Dress(int index, int layers, uint id)
        {
            FiresSkyIslands.CollectSkylandDressing(index, layers, s_pos, s_rot, s_name, s_scale);
            int placed = 0;
            for (int i = 0; i < s_pos.Count; i++)
            {
                var zdo = NetworkObjectHelper.CreateZdo(ZNetScene.instance.GetPrefab(s_name[i]), s_pos[i], s_rot[i]);
                if (zdo == null)
                {
                    if (s_missingPrefabs.Add(s_name[i]))
                        FiresLogger.LogWarning($"[FiresZones] skyland dressing '{s_name[i]}' is not a networked prefab here — skipped.");
                    continue;
                }
                if (s_scale[i] != Vector3.one) zdo.Set(ZDOVars.s_scaleHash, s_scale[i]);
                zdo.Set(DressTag, (long)id + 1L);   // what the skyland code placed, so a later clear takes only this
                placed++;
            }
            return placed;
        }

        private static string DressedPath()
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(ZNet.World?.m_name ?? "world", "_skylands.txt");
        }

        private static void EnsureDressedLoaded()
        {
            if (s_dressedLoaded) return;
            s_dressedLoaded = true;
            s_dressed.Clear();
            try
            {
                string path = DressedPath();
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0 || !uint.TryParse(parts[0], out uint id)) continue;
                    int layers = parts.Length > 1 && int.TryParse(parts[1], out int mask) ? mask : FiresSkyIslands.DressBase;
                    var biome = parts.Length > 2 && Enum.TryParse(parts[2], out Heightmap.Biome named) ? named : Heightmap.Biome.None;
                    s_dressed[id] = new DressRecord(layers, biome);
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] could not read the skyland dressing record: {ex.Message}"); }
        }

        private static void SaveDressed()
        {
            try
            {
                string path = DressedPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>(s_dressed.Count);
                foreach (var kv in s_dressed) lines.Add(kv.Key + " " + kv.Value.Layers + " " + kv.Value.Biome);
                string temp = path + ".tmp";
                File.WriteAllLines(temp, lines);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] could not save the skyland dressing record: {ex.Message}"); }
        }

        // A skyland raised from a blueprint (ZoneModeSkylandBlueprint) brings its own saved objects, its location's pieces
        // included, so it is recorded as fully dressed and neither dressed nor given a location a second time.
        internal static void MarkDressed(uint id, Heightmap.Biome biome)
        {
            EnsureDressedLoaded();
            s_dressed[id] = new DressRecord(FiresSkyIslands.DressAll | FiresSkyIslands.DressSiteLayout | FiresSkyIslands.DressLocation
                | FiresSkyIslands.DressRolledRims | FiresSkyIslands.DressGround | FiresSkyIslands.DressNames | FiresSkyIslands.DressSurface, biome);
            s_sitesPending.Remove(id);
            SaveDressed();
        }

        // ── Client ──────────────────────────────────────────────────────────────────────────────────
        internal static void ClientTick()
        {
            EnsureRisingRpc();
            if (!s_headlessKnown)
            {
                s_headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
                s_headlessKnown = true;
            }
            if (s_headless) return;
            if (s_heldDressing.Count > 0) ReleaseSwappedDressing();   // 0.2.763: every frame, so held dressing never lags its ground
            if (Time.time < s_nextClientTick) return;
            s_nextClientTick = Time.time + ClientTickSeconds;
            if (!FiresSkyIslands.Enabled) { if (s_visuals.Count > 0) ClearVisuals(); return; }
            var cam = Utils.GetMainCamera();
            if (cam == null) return;

            Vector3 eye = cam.transform.position;
            bool above = FiresSkyIslands.TryLowestBottomNear(eye, GrassLayerRange, out float lowest) && eye.y > lowest;
            if (above != s_cameraAboveIslands)
            {
                s_cameraAboveIslands = above;
                ClutterSystem.instance?.ResetGrass(eye, GrassResetRadius);
            }

            FiresSkyIslands.SkylandsNear(cam.transform.position, VisualRange, s_nearIds, s_nearIndices);
            s_nearSet.Clear();
            for (int i = 0; i < s_nearIds.Count; i++)
            {
                uint id = s_nearIds[i];
                s_nearSet.Add(id);
                if (!s_visuals.TryGetValue(id, out var visual))
                {
                    visual = Build(s_nearIndices[i]);
                    s_visuals[id] = visual;
                }
                if (!visual.WaterBuilt && VoxelChunkView.BoundSheetMaterial != null) BuildFarWater(s_nearIndices[i], visual);
                ShowFarPieces(visual.FarPieces);
                ShowFarPieces(visual.FarWater);
            }
            s_dropIds.Clear();
            foreach (var kv in s_visuals) if (!s_nearSet.Contains(kv.Key)) s_dropIds.Add(kv.Key);
            foreach (uint id in s_dropIds) { Destroy(s_visuals[id]); s_visuals.Remove(id); }
        }

        // 0.2.763 (Fire 10-06: "we're STILL reshaping skylands on first arrival ... Veg and clutter grasses shouldn't be drawn
        // until after the terrain reshapes"): a far piece stays up until its zone SWAPS (its voxel chunks, held hidden until then,
        // all draw from that frame), not from the moment the zone starts wrapping: the gap where neither was drawn, and the island
        // building up chunk by chunk over a hidden far piece, were the "reshape". OnZoneSwapped flips it in the swap's own frame.
        private static void ShowFarPieces(List<FarPiece> pieces)
        {
            foreach (var piece in pieces)
            {
                bool voxelShown = VoxelZoneStreamer.IsZoneSolid(piece.Zone);
                if (piece.View.activeSelf == voxelShown) piece.View.SetActive(!voxelShown);
            }
        }

        // The swap of a zone on this client (VoxelZoneStreamer.TrySwap): its far pieces go and its held dressing shows, in the
        // same frame its voxel chunks start drawing.
        internal static void OnZoneSwapped(Vector2s zone)
        {
            foreach (var kv in s_visuals)
            {
                HideFarPiecesIn(kv.Value.FarPieces, zone);
                HideFarPiecesIn(kv.Value.FarWater, zone);
            }
            ReleaseDressing(zone);
        }

        private static void HideFarPiecesIn(List<FarPiece> pieces, Vector2s zone)
        {
            foreach (var piece in pieces)
                if (piece.Zone.x == zone.x && piece.Zone.y == zone.y && piece.View.activeSelf) piece.View.SetActive(false);
        }

        // ── Dressing waits for its ground (0.2.763) ──────────────────────────────────────────────────────────────
        // A skyland's trees, rocks, roots and vines (DressTag) are networked objects: a client creates them when their zone's
        // objects load, which was before the zone's voxel island was drawn, so they stood in the air or beside a far piece's
        // coarser coast. One created while its zone has not swapped keeps its renderers off until the swap (OnZoneSwapped),
        // and is re-checked every client tick in case the swap came by another path.
        private sealed class HeldDressing
        {
            internal GameObject Go;
            internal Vector2s Zone;
            internal List<Renderer> Off;
        }

        private static readonly List<HeldDressing> s_heldDressing = new List<HeldDressing>();
        private static readonly List<Renderer> s_rendererScratch = new List<Renderer>();
        private static int s_dressingHeld, s_dressingReleased;

        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        private static class ZNetScene_CreateObject
        {
            private static void Postfix(ZDO zdo, GameObject __result) => HoldDressing(zdo, __result);
        }

        private static void HoldDressing(ZDO zdo, GameObject go)
        {
            if (go == null || zdo == null || !ZoneModeRegistry.ModeActive || !FiresSkyIslands.Enabled) return;
            if (zdo.GetLong(DressTag, 0L) == 0L || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var zone = ZoneSystem.GetZone(go.transform.position);
            if (VoxelZoneStreamer.IsZoneSolid(zone)) return;
            go.GetComponentsInChildren(true, s_rendererScratch);
            var off = new List<Renderer>(s_rendererScratch.Count);
            foreach (var r in s_rendererScratch)
            {
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                off.Add(r);
            }
            s_heldDressing.Add(new HeldDressing { Go = go, Zone = zone, Off = off });
            s_dressingHeld++;
        }

        private static void ReleaseDressing(Vector2s zone)
        {
            for (int i = s_heldDressing.Count - 1; i >= 0; i--)
            {
                var held = s_heldDressing[i];
                if (held.Go != null && (held.Zone.x != zone.x || held.Zone.y != zone.y)) continue;
                Release(held);
                s_heldDressing.RemoveAt(i);
            }
        }

        // Every client tick: dressing whose zone swapped by any path shows; destroyed objects drop out.
        private static void ReleaseSwappedDressing()
        {
            for (int i = s_heldDressing.Count - 1; i >= 0; i--)
            {
                var held = s_heldDressing[i];
                if (held.Go != null && !VoxelZoneStreamer.IsZoneSolid(held.Zone)) continue;
                Release(held);
                s_heldDressing.RemoveAt(i);
            }
        }

        private static void Release(HeldDressing held)
        {
            if (held.Go == null) return;
            foreach (var r in held.Off) if (r != null) r.enabled = true;
            if (++s_dressingReleased % 200 == 1)
                FiresLogger.LogDebug($"[FiresZones] skyland dressing shown with its ground: {s_dressingReleased} of {s_dressingHeld} held "
                    + $"object(s) released so far, {s_heldDressing.Count - 1} still waiting for their zone's swap.");
        }

        // The water sheet's material is bound by the near system some time after load, so the far water waits for it
        // (a piece built without it would render in the error colour).
        private static void BuildFarWater(int index, Visual visual)
        {
            visual.WaterBuilt = true;
            string name = "SkylandFarWater_" + FiresSkyIslands.IslandId(index).ToString("x8");
            var whole = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            if (FiresSkyIslands.BuildSkylandFarWater(index, whole)) SplitFarMeshByZone(whole, name, visual.FarWater, water: true);
            Object.Destroy(whole);
        }

        // One far-view piece per zone, so each piece hides as soon as its own zone turns voxel instead of the whole island
        // waiting on zones past the wrap radius that never build. Normals come from the whole mesh, so pieces meet smoothly.
        private static void SplitFarMeshByZone(Mesh whole, string name, List<FarPiece> pieces, bool water = false)
        {
            var vertices = whole.vertices;
            var normals = whole.normals;
            var tangents = whole.tangents;
            var colors = whole.colors32;
            var uv0 = new List<Vector2>();
            var uv1 = new List<Vector2>();
            whole.GetUVs(0, uv0);
            whole.GetUVs(1, uv1);
            var triangles = whole.triangles;

            var triangleStartsByZone = new Dictionary<Vector2s, List<int>>();
            for (int start = 0; start < triangles.Length; start += 3)
            {
                var centroid = (vertices[triangles[start]] + vertices[triangles[start + 1]] + vertices[triangles[start + 2]]) / 3f;
                var zone = ZoneSystem.GetZone(centroid);
                if (!triangleStartsByZone.TryGetValue(zone, out var starts)) triangleStartsByZone[zone] = starts = new List<int>();
                starts.Add(start);
            }

            var pieceIndexOf = new Dictionary<int, int>();
            foreach (var zoneTriangles in triangleStartsByZone)
            {
                pieceIndexOf.Clear();
                var pieceVertices = new List<Vector3>();
                var pieceNormals = new List<Vector3>();
                var pieceTangents = new List<Vector4>();
                var pieceColors = new List<Color32>();
                var pieceUv0 = new List<Vector2>();
                var pieceUv1 = new List<Vector2>();
                var pieceTriangles = new List<int>(zoneTriangles.Value.Count * 3);
                foreach (int start in zoneTriangles.Value)
                {
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int source = triangles[start + corner];
                        if (!pieceIndexOf.TryGetValue(source, out int target))
                        {
                            target = pieceVertices.Count;
                            pieceIndexOf[source] = target;
                            pieceVertices.Add(vertices[source]);
                            pieceNormals.Add(normals[source]);
                            pieceTangents.Add(tangents[source]);
                            pieceColors.Add(colors[source]);
                            pieceUv0.Add(uv0[source]);
                            pieceUv1.Add(uv1[source]);
                        }
                        pieceTriangles.Add(target);
                    }
                }

                var zone = zoneTriangles.Key;
                var mesh = new Mesh { name = name + "_" + zone.x + "_" + zone.y };
                mesh.SetVertices(pieceVertices);
                mesh.SetNormals(pieceNormals);
                mesh.SetTangents(pieceTangents);
                mesh.SetColors(pieceColors);
                mesh.SetUVs(0, pieceUv0);
                mesh.SetUVs(1, pieceUv1);
                mesh.SetTriangles(pieceTriangles, 0, calculateBounds: true);

                var view = new GameObject(mesh.name);
                view.transform.SetParent(s_root.transform, false);
                view.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = view.AddComponent<MeshRenderer>();
                if (water)
                {
                    renderer.sharedMaterial = VoxelChunkView.BoundSheetMaterial;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
                else
                {
                    renderer.sharedMaterial = TerrainMaterialCache.GetCurrentMaterial();
                    VoxelChunkView.ApplyShadowMode(renderer);
                }
                VoxelRenderGroup.Apply(renderer);
                pieces.Add(new FarPiece { Zone = zone, View = view, Mesh = mesh });
            }
        }

        private static Visual Build(int index)
        {
            if (s_root == null) s_root = new GameObject("FiresSkylands");
            var visual = new Visual { Biome = FiresSkyIslands.SkylandBiome(index) };
            uint islandId = FiresSkyIslands.IslandId(index);
            string farName = "SkylandFar_" + islandId.ToString("x8");
            var whole = new Mesh { name = farName };
            FiresSkyIslands.BuildSkylandFarMesh(index, whole, FatSeasons.BiomeColour(visual.Biome));
            SplitFarMeshByZone(whole, farName, visual.FarPieces);
            Object.Destroy(whole);
            // 0.2.763: the shared terrain material's blank mask has alpha 1, which is lava on Ashlands ground; a far Ashlands
            // skyland wears a bare mask instead (its voxels do the same through VoxelLayerPaint's palette).
            if (visual.Biome == Heightmap.Biome.AshLands)
                foreach (var piece in visual.FarPieces) VoxelLayerPaint.BindBare(piece.View.GetComponent<MeshRenderer>());
            FiresLogger.LogVerbose($"[FiresZones] skyland {islandId:x8}: far view built as {visual.FarPieces.Count} zone piece(s).");

            var material = FiresWaterfalls.Material;
            FiresSkyIslands.CollectSkylandFalls(index, s_falls);
            if (material != null && s_falls.Count > 0)
            {
                FiresWaterfalls.BeginMesh();
                s_fallSites.Clear();
                s_mistSites.Clear();
                int inland = 0, caught = 0;
                foreach (var fall in s_falls)
                {
                    if (!IsSpillway(fall.Lip, fall.Outward)) { inland++; continue; }
                    var across = new Vector3(-fall.Outward.z, 0f, fall.Outward.x);
                    s_fallColumns.Clear();
                    for (int k = 0; k <= 4; k++)
                    {
                        float offset = (k / 4f - 0.5f) * fall.Width;
                        s_fallColumns.Add(new FiresWaterfalls.Column { Base = fall.Lip + across * offset, LipY = fall.Lip.y, FallLength = fall.Drop });
                    }
                    if (!FiresWaterfalls.AppendCurtain(s_fallColumns, fall.Outward, FallLipOut, fall.Drop * FallFade,
                            Mathf.Repeat(fall.Lip.x * 0.37f + fall.Lip.z * 0.11f, 97f), SkylandFace))
                    {
                        caught++;
                        continue;
                    }
                    s_fallSites.Add(FiresWaterfalls.SoundSite(s_fallColumns, fall.Outward));
                    s_mistSites.Add(FiresWaterfalls.LastSite);
                }
                FiresLogger.LogInfo($"[FiresZones] skyland {islandId:x8}: drew {s_fallSites.Count} of {s_falls.Count} mouth fall(s)"
                    + (inland > 0 ? $"; {inland} stop short of the rim" : "")
                    + (caught > 0 ? $"; {caught} caught by the rock at the lip" : "") + ".");
                visual.FallMesh = new Mesh { name = "SkylandFall" };
                if (FiresWaterfalls.EndMesh(visual.FallMesh) > 0)
                {
                    visual.Fall = new GameObject("SkylandFall");
                    visual.Fall.transform.SetParent(s_root.transform, false);
                    visual.Fall.AddComponent<MeshFilter>().sharedMesh = visual.FallMesh;
                    var fallRenderer = visual.Fall.AddComponent<MeshRenderer>();
                    fallRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    fallRenderer.receiveShadows = false;
                    fallRenderer.sharedMaterials = FiresWaterfalls.Materials;
                    VoxelRenderGroup.Apply(fallRenderer);
                    FiresWaterfalls.SetSounds(visual.Fall.transform, s_fallSites);
                    FiresWaterfallMist.SetEmitters(visual.Fall.transform, s_mistSites);
                }
            }
            return visual;
        }

        // How far out from the lip a curtain row at height y must hang to clear the skyland's own rock: the island density
        // the mesh is built from, probed outward along the fall. 0 under an undercut lip or a sheer face, more where the
        // rolled rim or a belly knob bulges out below the lip, and negative where rock fills the whole reach, which cuts
        // the fall there.
        private const float FaceReach = 12f;
        private const float FaceStep = 0.5f;
        private const float FaceClearance = 0.35f;

        private static float SkylandFace(Vector3 basePos, Vector3 outward, float y)
        {
            for (float d = 0f; d <= FaceReach; d += FaceStep)
                if (FiresSkyIslands.Sdf(basePos.x + outward.x * d, y, basePos.z + outward.z * d) <= 0f) return d + FaceClearance;
            return -1f;
        }

        // Is this lip a spillway? Within a few metres outward the island has to fall away, with nothing solid for the
        // first metres of the drop. A rill or river that stops short of the rim spills nowhere: its curtain would follow
        // the island's own slope outward and downward, draping a sheet of water across the grass.
        private const float RimProbeOut = 3f;
        private const float RimProbeStep = 0.5f;
        private const float RimDrop = 6f;
        private const float RimDropStep = 1f;

        private static bool IsSpillway(Vector3 lip, Vector3 outward)
        {
            for (float d = RimProbeStep; d <= RimProbeOut; d += RimProbeStep)
            {
                float x = lip.x + outward.x * d, z = lip.z + outward.z * d;
                bool clear = true;
                for (float down = 0f; down <= RimDrop; down += RimDropStep)
                    if (FiresSkyIslands.Sdf(x, lip.y - down, z) > 0f) { clear = false; break; }
                if (clear) return true;
            }
            return false;
        }

        private static void Destroy(Visual visual)
        {
            DestroyPieces(visual.FarPieces);
            DestroyPieces(visual.FarWater);
            if (visual.FallMesh != null) Object.Destroy(visual.FallMesh);
            if (visual.Fall != null) Object.Destroy(visual.Fall);
        }

        private static void DestroyPieces(List<FarPiece> pieces)
        {
            foreach (var piece in pieces)
            {
                Object.Destroy(piece.Mesh);
                Object.Destroy(piece.View);
            }
            pieces.Clear();
        }

        private static void ClearVisuals()
        {
            foreach (var kv in s_visuals) Destroy(kv.Value);
            s_visuals.Clear();
        }

        // Vedr seasons: a skyland's far view is one colour, its biome's or the snow splat, repainted in place.
        internal static void CollectSeasonRecolours(List<FatSeasons.Recolour> into)
        {
            foreach (var kv in s_visuals)
            {
                var biome = kv.Value.Biome;
                foreach (var piece in kv.Value.FarPieces)
                {
                    var target = piece;
                    into.Add(new FatSeasons.Recolour(target.Mesh.bounds.center, () => RecolourPiece(target, biome)));
                }
            }
        }

        private static void RecolourPiece(FarPiece piece, Heightmap.Biome biome)
        {
            if (piece.Mesh == null) return;
            var colour = FatSeasons.BiomeColour(biome);
            s_pieceColours.Clear();
            for (int i = 0; i < piece.Mesh.vertexCount; i++) s_pieceColours.Add(colour);
            piece.Mesh.SetColors(s_pieceColours);
        }

        internal static void RefreshVisibility()
        {
            foreach (var kv in s_visuals)
            {
                foreach (var piece in kv.Value.FarPieces) VoxelRenderGroup.Apply(piece.View.GetComponent<MeshRenderer>());
                foreach (var piece in kv.Value.FarWater) VoxelRenderGroup.Apply(piece.View.GetComponent<MeshRenderer>());
                if (kv.Value.Fall != null) VoxelRenderGroup.Apply(kv.Value.Fall.GetComponent<MeshRenderer>());
            }
        }

        internal static void Reset()
        {
            ClearVisuals();
            if (s_root != null) Object.Destroy(s_root);
            s_root = null;
            s_lastVersion = -1;
            s_dressedLoaded = false;
            s_dressed.Clear();
            s_sitesPending.Clear();
            s_rising.Clear();
        }

        // ── Rising skylands: announced ahead of their dressing ──────────────────────────────────────────
        // A new skyland's dressing reaches clients as ZDOs before the new island set does (that comes as a file), so a
        // client would build its vines and veg over what it still takes for open air: static physics dropped the veg to
        // the ground far below and the vines broke for want of support. The server announces each rising skyland's disc
        // first, on the same connection and ahead of the dressing; the client holds wear and static physics inside it
        // until the island set lands, then the voxel wear gate holds on until the island's chunks are built.
        private readonly struct Disc
        {
            internal readonly float X, Z, R;

            internal Disc(float x, float z, float r)
            {
                X = x;
                Z = z;
                R = r;
            }

            internal bool Holds(Vector3 point)
            {
                float dx = point.x - X, dz = point.z - Z;
                return dx * dx + dz * dz <= R * R;
            }

            internal bool Touches(Vector3 zoneCentre)
            {
                float nx = Mathf.Clamp(X, zoneCentre.x - ZoneSystem.c_ZoneSizeHalf, zoneCentre.x + ZoneSystem.c_ZoneSizeHalf) - X;
                float nz = Mathf.Clamp(Z, zoneCentre.z - ZoneSystem.c_ZoneSizeHalf, zoneCentre.z + ZoneSystem.c_ZoneSizeHalf) - Z;
                return nx * nx + nz * nz <= R * R;
            }
        }

        private static void EnsureRisingRpc()
        {
            var bus = ZRoutedRpc.instance;
            if (bus == null || bus == s_risingBus) return;
            bus.Register<ZPackage>(RisingRpc, OnRising);
            s_risingBus = bus;
            s_rising.Clear();
        }

        private static void AnnounceRising(List<int> added)
        {
            if (s_risingBus == null) return;
            var pkg = new ZPackage();
            pkg.Write(added.Count);
            foreach (int index in added)
            {
                FiresSkyIslands.SkylandDisc(index, out float x, out float z, out float radius);
                pkg.Write(x);
                pkg.Write(z);
                pkg.Write(radius);
            }
            s_risingBus.InvokeRoutedRPC(ZRoutedRpc.Everybody, RisingRpc, pkg);
        }

        // The server holds the island set already; only clients wait for it.
        private static void OnRising(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++)
            {
                float x = pkg.ReadSingle(), z = pkg.ReadSingle(), radius = pkg.ReadSingle();
                s_rising.Add(new Disc(x, z, radius));
            }
            FiresLogger.LogInfo($"[FiresZones] {count} skyland(s) rising: vines and veg under them wait until the island set arrives.");
        }

        /// <summary>The island set arrived: the voxel wear gate takes over from the rising discs.</summary>
        internal static void RisingArrived()
        {
            if (s_rising.Count == 0) return;
            FiresLogger.LogInfo($"[FiresZones] island set arrived: {s_rising.Count} rising skyland(s) handed to the voxel wear gate.");
            s_rising.Clear();
        }

        internal static bool RisingAt(Vector3 point)
        {
            foreach (var disc in s_rising) if (disc.Holds(point)) return true;
            return false;
        }

        internal static bool RisingOver(Vector2s zone)
        {
            if (s_rising.Count == 0) return false;
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            foreach (var disc in s_rising) if (disc.Touches(centre)) return true;
            return false;
        }

        // A terrain modifier standing on a skyland (a location's levelling, smoothing and paint) belongs to the skyland,
        // which is levelled around its location already. It never edits the ground heightmap far below.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.TerrainVSModifier))]
        private static class Heightmap_TerrainVSModifier_SkylandModifiers
        {
            private static void Postfix(TerrainModifier modifier, ref bool __result)
            {
                if (__result && FiresSkyIslands.IsOnZoneSkyland(modifier.transform.position)) __result = false;
            }
        }

        // Vanilla snaps to the first terrain hit below y=6000, which is the ground under a skyland whose colliders are
        // not built yet. On a skyland the ground is its top.
        [HarmonyPatch(typeof(SnapToGround), nameof(SnapToGround.Snap))]
        private static class SnapToGround_Snap_SkylandTop
        {
            private static bool Prefix(SnapToGround __instance)
            {
                var position = __instance.transform.position;
                if (!FiresSkyIslands.TrySkylandTopAt(position, out float top)) return true;
                position.y = top + __instance.m_offset;
                __instance.transform.position = position;
                var view = __instance.GetComponent<ZNetView>();
                if (view != null && view.IsOwner()) view.GetZDO().SetPosition(position);
                return false;
            }
        }
    }
}
