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
    //   client — a far-view mesh of every skyland on the horizon until its voxel chunks are in, and the
    //            short fall from its rim that thins out in mid-air.
    internal static class ZoneModeSkylands
    {
        private const float ServerCheckSeconds = 2f;
        private const float ClientTickSeconds = 0.5f;
        private const float VisualRange = 1600f;
        private const float FallFade = 0.8f;
        private const float FallLipOut = 0.6f;

        private static int s_lastVersion = -1;
        private static float s_nextServerCheck;
        private static bool s_dressedLoaded;
        private static readonly Dictionary<uint, DressRecord> s_dressed = new Dictionary<uint, DressRecord>();
        private static readonly Dictionary<Heightmap.Biome, int> s_dressedBiomes = new Dictionary<Heightmap.Biome, int>();
        private static readonly HashSet<string> s_missingPrefabs = new HashSet<string>();
        private static readonly List<int> s_added = new List<int>();
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

        private sealed class Visual
        {
            internal GameObject Far;
            internal Mesh FarMesh;
            internal GameObject Fall;
            internal Mesh FallMesh;
            internal readonly List<Vector2s> Zones = new List<Vector2s>();
        }

        private static readonly Dictionary<uint, Visual> s_visuals = new Dictionary<uint, Visual>();
        private static readonly List<uint> s_nearIds = new List<uint>();
        private static readonly List<int> s_nearIndices = new List<int>();
        private static readonly HashSet<uint> s_nearSet = new HashSet<uint>();
        private static readonly List<uint> s_dropIds = new List<uint>();
        private static readonly List<FiresWaterfalls.Column> s_fallColumns = new List<FiresWaterfalls.Column>(8);
        private static readonly List<FiresSkyIslands.SkylandFall> s_falls = new List<FiresSkyIslands.SkylandFall>(4);
        private static readonly List<Vector3> s_fallSites = new List<Vector3>(1);
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
                if ((done & FiresSkyIslands.DressAll) != 0 && (record.Biome != biome || laidOutBlind || hardRims || bareGround || wrongNames))
                {
                    cleared += ClearDressing(index);
                    done &= FiresSkyIslands.DressLocation;
                }
                done |= FiresSkyIslands.DressSiteLayout | FiresSkyIslands.DressRolledRims | FiresSkyIslands.DressGround | FiresSkyIslands.DressNames;
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

        // Every object this skyland's dressing placed, in any biome, including the stumps of felled trees.
        private static int ClearDressing(int index)
        {
            var sectors = ZoneModeChunkMachines.ObjectsBySector(ZDOMan.instance);
            if (sectors == null) return 0;
            if (s_dressingPrefabs.Count == 0)
                foreach (var name in FiresSkyIslands.AllDressingPrefabs()) s_dressingPrefabs.Add(name.GetStableHashCode());
            FiresSkyIslands.SkylandZones(index, s_dressingZones, FiresSkyIslands.DressingOverhang);
            s_doomed.Clear();
            foreach (var zone in s_dressingZones)
            {
                var sector = ZoneSystem.SectorToIndex(zone).Sector;
                if (sector < 0 || sector >= sectors.Length || sectors[sector] == null) continue;
                foreach (var zdo in sectors[sector])
                    if (s_dressingPrefabs.Contains(zdo.GetPrefab()) && FiresSkyIslands.InSkylandDressing(index, zdo.GetPosition()))
                        s_doomed.Add(zdo);
            }
            long self = ZDOMan.GetSessionID();
            foreach (var zdo in s_doomed)
            {
                zdo.SetOwner(self);
                ZDOMan.instance.DestroyZDO(zdo);
            }
            return s_doomed.Count;
        }

        private static readonly HashSet<int> s_dressingPrefabs = new HashSet<int>();
        private static readonly List<Vector2s> s_dressingZones = new List<Vector2s>();
        private static readonly List<ZDO> s_doomed = new List<ZDO>();

        private static int Dress(int index, int layers)
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
                placed++;
            }
            return placed;
        }

        private static string DressedPath()
        {
            string world = ZNet.World?.m_name ?? "world";
            string safe = string.Concat(world.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "FiresWorldGen", "firesgen_" + safe + "_skylands.txt");
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

        // ── Client ──────────────────────────────────────────────────────────────────────────────────
        internal static void ClientTick()
        {
            if (!s_headlessKnown)
            {
                s_headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
                s_headlessKnown = true;
            }
            if (s_headless || Time.time < s_nextClientTick) return;
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
                bool voxelIn = true;
                foreach (var zone in visual.Zones)
                    if (!VoxelZoneStreamer.IsZoneSolid(zone)) { voxelIn = false; break; }
                if (visual.Far != null && visual.Far.activeSelf == voxelIn) visual.Far.SetActive(!voxelIn);
            }
            s_dropIds.Clear();
            foreach (var kv in s_visuals) if (!s_nearSet.Contains(kv.Key)) s_dropIds.Add(kv.Key);
            foreach (uint id in s_dropIds) { Destroy(s_visuals[id]); s_visuals.Remove(id); }
        }

        private static Visual Build(int index)
        {
            if (s_root == null) s_root = new GameObject("FiresSkylands");
            var visual = new Visual();
            FiresSkyIslands.SkylandZones(index, visual.Zones);

            visual.Far = new GameObject("SkylandFar_" + FiresSkyIslands.IslandId(index).ToString("x8"));
            visual.Far.transform.SetParent(s_root.transform, false);
            visual.FarMesh = new Mesh { name = visual.Far.name };
            FiresSkyIslands.BuildSkylandFarMesh(index, visual.FarMesh, Heightmap.GetBiomeColor(FiresSkyIslands.SkylandBiome(index)));
            visual.Far.AddComponent<MeshFilter>().sharedMesh = visual.FarMesh;
            var farRenderer = visual.Far.AddComponent<MeshRenderer>();
            farRenderer.sharedMaterial = TerrainMaterialCache.GetCurrentMaterial();
            VoxelChunkView.ApplyShadowMode(farRenderer);
            VoxelRenderGroup.Apply(farRenderer);

            var material = FiresWaterfalls.Material;
            FiresSkyIslands.CollectSkylandFalls(index, s_falls);
            if (material != null && s_falls.Count > 0)
            {
                FiresWaterfalls.BeginMesh();
                s_fallSites.Clear();
                int inland = 0;
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
                    FiresWaterfalls.AppendCurtain(s_fallColumns, fall.Outward, FallLipOut, fall.Drop * FallFade,
                        Mathf.Repeat(fall.Lip.x * 0.37f + fall.Lip.z * 0.11f, 97f), SkylandFace);
                    s_fallSites.Add(FiresWaterfalls.SoundSite(s_fallColumns, fall.Outward));
                }
                if (inland > 0)
                    FiresLogger.LogVerbose($"[FiresZones] skyland {FiresSkyIslands.IslandId(index):x8}: {inland} of {s_falls.Count} "
                        + "planned fall(s) do not reach the rim and are not drawn.");
                visual.FallMesh = new Mesh { name = "SkylandFall" };
                if (FiresWaterfalls.EndMesh(visual.FallMesh) > 0)
                {
                    visual.Fall = new GameObject("SkylandFall");
                    visual.Fall.transform.SetParent(s_root.transform, false);
                    visual.Fall.AddComponent<MeshFilter>().sharedMesh = visual.FallMesh;
                    var fallRenderer = visual.Fall.AddComponent<MeshRenderer>();
                    fallRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    fallRenderer.receiveShadows = false;
                    fallRenderer.sharedMaterial = material;
                    VoxelRenderGroup.Apply(fallRenderer);
                    FiresWaterfalls.SetSounds(visual.Fall.transform, s_fallSites);
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
            if (visual.FarMesh != null) Object.Destroy(visual.FarMesh);
            if (visual.Far != null) Object.Destroy(visual.Far);
            if (visual.FallMesh != null) Object.Destroy(visual.FallMesh);
            if (visual.Fall != null) Object.Destroy(visual.Fall);
        }

        private static void ClearVisuals()
        {
            foreach (var kv in s_visuals) Destroy(kv.Value);
            s_visuals.Clear();
        }

        internal static void RefreshVisibility()
        {
            foreach (var kv in s_visuals)
            {
                if (kv.Value.Far != null) VoxelRenderGroup.Apply(kv.Value.Far.GetComponent<MeshRenderer>());
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
