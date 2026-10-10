using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // Client-side cave clutter: pebbles and bone bits on cave floors, small roots and ice shards on walls and ceilings,
    // drawn instanced from the meshes and materials of vanilla ZNetScene prefabs. Nothing is networked and nothing
    // collides. A chunk's clutter comes from its own density - every lattice edge the surface crosses - and a hash of
    // the world seed, chunk and cell, so every client draws the same pieces; it is rebuilt when the chunk's live mesh
    // changes. Only rock with rock above it counts (the inside of caves, the ground's or a skyland's), never the open
    // ground, a river bed or a skyland's outside.
    // Pieces go on the rock by VoxelSurfaceSnap's attach faces, so a shard on a ceiling hangs the way it would in
    // vanilla.
    internal static class VoxelCaveClutter
    {
        private const string Section = "VoxelTerrain Cave Clutter";
        private const float MaintainSeconds = 0.25f;
        private const float RangeMargin = 16f;
        private const int MaxInstancesPerDraw = 1023;
        private const float NormalStep = 0.5f;
        private const float OpenSurfaceBand = 0.3f;
        private const float GroundAboveChunk = 2f;
        private const float ReprojectLift = 0.4f;
        private const float ReprojectReach = 0.8f;
        private const float ReprojectStep = 0.1f;
        private const float FloorFacing = 0.5f;
        private const float PatchLayerHeight = 16f;
        private const float PatchEdge = 1f;
        private const float PatchRadiusSpread = 0.3f;
        private const int PatchHashTag = 3;
        private const float SkylandBand = 0.5f;
        private const int MaxSpans = 4;
        private const int Cells = VoxelChunk.CELLS;
        private const int Samples = VoxelChunk.SAMPLES;

        private enum CaveSurface { Floor, Wall, Ceiling }

        private sealed class Layer
        {
            public GameObject Prefab;
            public CaveSurface On;
            public float PerSquareMetre, MinSize, MaxSize;
        }

        private struct Part
        {
            public Mesh Mesh;
            public int SubMesh;
            public Matrix4x4 FromRoot;
            public RenderParams Params;
        }

        private sealed class Batch
        {
            public Part Part;
            public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>();
        }

        private sealed class ChunkClutter
        {
            public VoxelChunk Chunk;
            public int BuiltFor;
            public Bounds Bounds;
            public readonly List<Batch> Batches = new List<Batch>();
            public int Count;
        }

        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<float> s_density, s_range;
        private static ConfigEntry<int> s_buildsPerTick;
        private static ConfigEntry<float> s_patchSpacing, s_patchChance, s_patchRadius;
        private static readonly Dictionary<Heightmap.Biome, ConfigEntry<string>> s_biomeEntries = new Dictionary<Heightmap.Biome, ConfigEntry<string>>();
        private static readonly Dictionary<Heightmap.Biome, List<Layer>> s_layers = new Dictionary<Heightmap.Biome, List<Layer>>();
        private static readonly Dictionary<Heightmap.Biome, string> s_parsedFrom = new Dictionary<Heightmap.Biome, string>();
        private static readonly Dictionary<GameObject, Part[]> s_parts = new Dictionary<GameObject, Part[]>();
        private static readonly Dictionary<Vector3Int, ChunkClutter> s_chunks = new Dictionary<Vector3Int, ChunkClutter>();
        private static readonly HashSet<string> s_warned = new HashSet<string>();
        private static readonly List<Vector3Int> s_drop = new List<Vector3Int>();
        private static readonly List<(float distance, VoxelChunk chunk)> s_candidates = new List<(float, VoxelChunk)>();
        private static readonly Dictionary<(Mesh, int, Material), Batch> s_batchByPart = new Dictionary<(Mesh, int, Material), Batch>();
        private static readonly sbyte[] s_voxels = new sbyte[Samples * Samples * Samples];
        private static readonly sbyte[] s_seed = new sbyte[Samples * Samples * Samples];
        private static readonly Color32[] s_biome = new Color32[Samples * Samples];
        private static readonly float[] s_ground = new float[Samples * Samples];
        private static readonly float[] s_openTop = new float[Cells * Cells];
        private static readonly Heightmap.Biome[] s_columnBiome = new Heightmap.Biome[Cells * Cells];
        private static readonly List<(float bottom, float top, Heightmap.Biome biome)> s_spanScratch = new List<(float, float, Heightmap.Biome)>(MaxSpans);
        private static readonly int[] s_spanCount = new int[Cells * Cells];
        private static readonly float[] s_spanBottom = new float[Cells * Cells * MaxSpans];
        private static readonly float[] s_spanTop = new float[Cells * Cells * MaxSpans];
        private static readonly Heightmap.Biome[] s_spanBiome = new Heightmap.Biome[Cells * Cells * MaxSpans];
        private static float s_nextMaintain;
        private static (float, float, float, float) s_builtWith = (-1f, -1f, -1f, -1f);
        private static bool s_failedDraw;

        private static readonly (Heightmap.Biome Biome, string Key, string Layers)[] Defaults =
        {
            (Heightmap.Biome.Meadows, "Meadows", "Rock_4:floor:2.2:0.06-0.22,LargeBone_half01:floor:0.08:0.25-0.45,root07:ceiling:0.1:0.5-1.0"),
            (Heightmap.Biome.BlackForest, "Black Forest", "rock4_forest:floor:2.4:0.06-0.22,LargeBone_half02:floor:0.08:0.25-0.45,root07:ceiling:0.12:0.5-1.1"),
            (Heightmap.Biome.Swamp, "Swamp", "Rock_4:floor:2.6:0.06-0.22,LargeBone_half01:floor:0.08:0.25-0.45,root07:ceiling:0.12:0.5-1.1"),
            (Heightmap.Biome.Mountain, "Mountain", "rock1_mountain:floor:2.2:0.06-0.22,caverock_ice_stalagtite:ceiling:0.3:0.3-0.9,caverock_ice_stalagmite:wall:0.15:0.3-0.6"),
            (Heightmap.Biome.Plains, "Plains", "rock4_heath:floor:2.4:0.06-0.22,LargeBone_half02:floor:0.08:0.25-0.45"),
            (Heightmap.Biome.Mistlands, "Mistlands", "rock_mistlands1:floor:2.2:0.06-0.22"),
            (Heightmap.Biome.AshLands, "Ashlands", "Ashlands_rock2:floor:2.6:0.06-0.22"),
            (Heightmap.Biome.DeepNorth, "Deep North", "Rock_3_deepnorth:floor:2.0:0.06-0.22,caverock_ice_stalagtite:ceiling:0.3:0.3-0.9,caverock_ice_stalagmite:wall:0.15:0.3-0.6"),
        };

        internal static void Bind(ConfigFile cfg)
        {
            s_enabled = cfg.Bind(Section, "Enabled", true,
                "Draw small clutter in caves on this machine: pebbles and bone bits on floors, roots and ice shards on walls and "
                + "ceilings. Visual only, per machine: nothing is networked, nothing collides.");
            s_density = cfg.Bind(Section, "Density", 1f, new ConfigDescription(
                "Multiplier on every biome list's pieces per square metre.", new AcceptableValueRange<float>(0f, 3f)));
            s_range = cfg.Bind(Section, "Range", 40f, new ConfigDescription(
                "How far from the camera clutter is drawn (metres).", new AcceptableValueRange<float>(8f, 96f)));
            s_patchSpacing = cfg.Bind(Section, "Floor patch spacing", 12f, new ConfigDescription(
                "Floor clutter comes in scattered batches, not an even carpet: at most one patch in each square of this size (metres). "
                + "Floor outside a patch stays bare. Walls and ceilings are not patched.", new AcceptableValueRange<float>(4f, 64f)));
            s_patchChance = cfg.Bind(Section, "Floor patch chance", 0.5f, new ConfigDescription(
                "Chance a square holds a floor patch.", new AcceptableValueRange<float>(0f, 1f)));
            s_patchRadius = cfg.Bind(Section, "Floor patch radius", 3f, new ConfigDescription(
                "Rough radius of a floor patch (metres); each varies by about a third and fades out over its last metre.",
                new AcceptableValueRange<float>(0.5f, 16f)));
            s_buildsPerTick = cfg.Bind(Section, "Chunks built per tick", 2, new ConfigDescription(
                "Chunks whose clutter is (re)built every quarter second, nearest first.", new AcceptableValueRange<int>(1, 16)));
            foreach (var (biome, key, layers) in Defaults)
                s_biomeEntries[biome] = cfg.Bind(Section, key, layers,
                    "prefab:surface:per square metre:min-max size in metres, comma separated. surface is floor, wall or ceiling. "
                    + "Any vanilla prefab with a mesh works; it is drawn at the given size and put on the rock by its attach face "
                    + "('VoxelTerrain Surface Mounting / Attach sides').");
        }

        internal static void Clear()
        {
            s_chunks.Clear();
            s_parts.Clear();
            s_layers.Clear();
            s_parsedFrom.Clear();
        }

        // Every frame on a client: keep the clutter of chunks near the camera current, then draw it.
        internal static void Tick()
        {
            if (s_enabled == null || !s_enabled.Value || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                if (s_chunks.Count > 0) s_chunks.Clear();
                return;
            }
            var cam = Utils.GetMainCamera();
            if (cam == null || ZNetScene.instance == null || WorldGenerator.instance == null) return;
            var eye = cam.transform.position;
            float range = s_range.Value;
            var built = (s_density.Value, s_patchSpacing.Value, s_patchChance.Value, s_patchRadius.Value);
            if (built != s_builtWith)
            {
                s_builtWith = built;
                s_chunks.Clear();
            }
            if (Time.time >= s_nextMaintain)
            {
                s_nextMaintain = Time.time + MaintainSeconds;
                Maintain(eye, range);
            }
            if (s_failedDraw) return;
            float range2 = range * range;
            foreach (var clutter in s_chunks.Values)
            {
                if (clutter.Count == 0 || clutter.Bounds.SqrDistance(eye) > range2) continue;
                for (int b = 0; b < clutter.Batches.Count; b++)
                {
                    var batch = clutter.Batches[b];
                    var rp = batch.Part.Params;
                    rp.worldBounds = clutter.Bounds;
                    int count = batch.Matrices.Count;
                    try
                    {
                        for (int start = 0; start < count; start += MaxInstancesPerDraw)
                            Graphics.RenderMeshInstanced(in rp, batch.Part.Mesh, batch.Part.SubMesh, batch.Matrices,
                                Mathf.Min(MaxInstancesPerDraw, count - start), start);
                    }
                    catch (Exception ex)
                    {
                        s_failedDraw = true;
                        FiresLogger.LogWarning($"[FiresVoxel] cave clutter drawing stopped: {ex.Message}");
                        return;
                    }
                }
            }
        }

        private static void Maintain(Vector3 eye, float range)
        {
            float keep = range + RangeMargin;
            s_drop.Clear();
            foreach (var pair in s_chunks)
            {
                if (!VoxelWorld.TryGetChunk(pair.Key, out var live) || !ReferenceEquals(live, pair.Value.Chunk)
                    || pair.Value.Bounds.SqrDistance(eye) > keep * keep)
                    s_drop.Add(pair.Key);
            }
            foreach (var key in s_drop) s_chunks.Remove(key);

            s_candidates.Clear();
            int reach = Mathf.CeilToInt(range / Cells) + 1;
            int cx = Mathf.FloorToInt(eye.x / Cells), cy = Mathf.FloorToInt(eye.y / Cells), cz = Mathf.FloorToInt(eye.z / Cells);
            for (int dz = -reach; dz <= reach; dz++)
                for (int dy = -reach; dy <= reach; dy++)
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        var key = new Vector3Int(cx + dx, cy + dy, cz + dz);
                        if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null || chunk.Kind != ChunkKind.Full || chunk.AppliedVersion < 0)
                            continue;
                        if (s_chunks.TryGetValue(key, out var built) && ReferenceEquals(built.Chunk, chunk) && built.BuiltFor == chunk.AppliedVersion)
                            continue;
                        var bounds = new Bounds(chunk.WorldOrigin + Vector3.one * (Cells * 0.5f), Vector3.one * Cells);
                        float distance = bounds.SqrDistance(eye);
                        if (distance <= range * range) s_candidates.Add((distance, chunk));
                    }
            if (s_candidates.Count == 0) return;
            s_candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
            int builds = Mathf.Min(s_buildsPerTick.Value, s_candidates.Count);
            for (int i = 0; i < builds; i++) s_chunks[s_candidates[i].chunk.Key] = Build(s_candidates[i].chunk);
        }

        private static ChunkClutter Build(VoxelChunk chunk)
        {
            var clutter = new ChunkClutter { Chunk = chunk, BuiltFor = chunk.AppliedVersion };
            for (int i = 0; i < s_ground.Length; i++) s_ground[i] = float.NaN;
            if (chunk.Snapshot(s_voxels, s_seed, s_biome, null, out _, s_ground) == VoxelChunk.SnapshotUniform) return clutter;

            var origin = chunk.WorldOrigin;
            PrepareColumns(chunk, origin);
            s_batchByPart.Clear();
            int seed = WorldGenerator.instance.GetSeed();
            float density = s_density.Value;
            var min = Vector3.one * float.MaxValue;
            var max = Vector3.one * float.MinValue;

            for (int z = 0; z < Cells; z++)
                for (int y = 0; y < Cells; y++)
                    for (int x = 0; x < Cells; x++)
                    {
                        float here = D(x, y, z);
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int nx = x + (axis == 0 ? 1 : 0), ny = y + (axis == 1 ? 1 : 0), nz = z + (axis == 2 ? 1 : 0);
                            float next = D(nx, ny, nz);
                            if (here > 0f == next > 0f) continue;
                            float t = here / (here - next);
                            var local = new Vector3(x + (nx - x) * t, y + (ny - y) * t, z + (nz - z) * t);
                            int airX = here > 0f ? nx : x, airZ = here > 0f ? nz : z;
                            if (!TryCaveSurface(origin, airX, airZ, local.y, out var biome)) continue;
                            var normal = Normal(local);
                            var rng = new CellRandom(seed, chunk.Key, x, y, z, axis);
                            PlaceAt(clutter, origin, local, normal, biome, density, seed, ref rng, ref min, ref max);
                        }
                    }

            if (clutter.Count > 0)
            {
                foreach (var batch in s_batchByPart.Values) clutter.Batches.Add(batch);
                clutter.Bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 2f);
            }
            return clutter;
        }

        private static void PlaceAt(ChunkClutter clutter, Vector3 origin, Vector3 local, Vector3 normal, Heightmap.Biome biome,
            float density, int seed, ref CellRandom rng, ref Vector3 min, ref Vector3 max)
        {
            var layers = LayersFor(biome);
            if (layers == null) return;
            var on = normal.y > FloorFacing ? CaveSurface.Floor : normal.y < -FloorFacing ? CaveSurface.Ceiling : CaveSurface.Wall;
            if (on == CaveSurface.Floor) density *= PatchWeight(origin + local, seed);
            if (density <= 0f) return;
            float areaShare = 1f / (Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z));
            var tangent = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var bitangent = Vector3.Cross(normal, tangent);
            for (int l = 0; l < layers.Count; l++)
            {
                var layer = layers[l];
                if (layer.On != on) continue;
                float expected = layer.PerSquareMetre * density * areaShare;
                int count = Mathf.FloorToInt(expected) + (rng.Next() < expected - Mathf.Floor(expected) ? 1 : 0);
                for (int i = 0; i < count; i++)
                {
                    var start = local + tangent * (rng.Next() - 0.5f) + bitangent * (rng.Next() - 0.5f);
                    if (!Reproject(start, normal, out var seat)) continue;
                    var seatNormal = Normal(seat);
                    var anchor = VoxelSurfaceSnap.Anchor(layer.Prefab);
                    if (!anchor.Valid) continue;
                    var size = anchor.Mesh.size;
                    float extent = Mathf.Max(0.01f, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
                    float scale = Mathf.Lerp(layer.MinSize, layer.MaxSize, rng.Next()) / extent;
                    if (Sample(seat + seatNormal * (anchor.Reach * scale)) > 0f) continue;
                    var rotation = VoxelSurfaceSnap.Orient(anchor, seatNormal, rng.Next(), rng.Next());
                    var world = origin + seat - seatNormal * VoxelSurfaceSnap.SeatDepth(anchor, scale) - rotation * (anchor.FaceCenter * scale);
                    AddInstance(clutter, layer.Prefab, world, rotation, scale);
                    min = Vector3.Min(min, world);
                    max = Vector3.Max(max, world);
                }
            }
        }

        // Floor clutter comes in batches: each square of 'Floor patch spacing' (in 16 m layers of height) may hold one
        // round patch, present on a hash of the world seed and the square, placed wholly inside it, full inside its
        // radius and fading out over the last metre. Every client draws the same patches.
        private static float PatchWeight(Vector3 p, int seed)
        {
            float spacing = s_patchSpacing.Value;
            var square = new Vector3Int(Mathf.FloorToInt(p.x / spacing), Mathf.FloorToInt(p.y / PatchLayerHeight), Mathf.FloorToInt(p.z / spacing));
            var rng = new CellRandom(seed, square, 0, 0, 0, PatchHashTag);
            if (rng.Next() >= s_patchChance.Value) return 0f;
            float radius = s_patchRadius.Value * (1f + (rng.Next() * 2f - 1f) * PatchRadiusSpread);
            float margin = Mathf.Min(radius + PatchEdge, spacing * 0.5f);
            float cx = square.x * spacing + margin + rng.Next() * (spacing - 2f * margin);
            float cz = square.z * spacing + margin + rng.Next() * (spacing - 2f * margin);
            float dx = p.x - cx, dz = p.z - cz;
            return 1f - Mathf.Clamp01((Mathf.Sqrt(dx * dx + dz * dz) - radius) / PatchEdge);
        }

        private static void AddInstance(ChunkClutter clutter, GameObject prefab, Vector3 position, Quaternion rotation, float scale)
        {
            var parts = PartsOf(prefab);
            if (parts.Length == 0) return;
            var root = Matrix4x4.TRS(position, rotation, prefab.transform.localScale * scale);
            for (int p = 0; p < parts.Length; p++)
            {
                var part = parts[p];
                var key = (part.Mesh, part.SubMesh, part.Params.material);
                if (!s_batchByPart.TryGetValue(key, out var batch)) s_batchByPart[key] = batch = new Batch { Part = part };
                batch.Matrices.Add(root * part.FromRoot);
            }
            clutter.Count++;
        }

        // The LOD0 meshes of a prefab, relative to its root, with their vanilla materials set to instance (the same
        // switch EasyBake flips; the rock materials ship with it on).
        private static Part[] PartsOf(GameObject prefab)
        {
            if (s_parts.TryGetValue(prefab, out var cached)) return cached;
            var renderers = new List<MeshRenderer>();
            var lodGroup = prefab.GetComponentInChildren<LODGroup>(true);
            var lods = lodGroup != null ? lodGroup.GetLODs() : null;
            if (lods != null && lods.Length > 0)
            {
                foreach (var renderer in lods[0].renderers)
                    if (renderer is MeshRenderer mesh) renderers.Add(mesh);
            }
            else renderers.AddRange(prefab.GetComponentsInChildren<MeshRenderer>(true));

            var parts = new List<Part>();
            var toRoot = prefab.transform.worldToLocalMatrix;
            foreach (var renderer in renderers)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length && m < filter.sharedMesh.subMeshCount; m++)
                {
                    var material = materials[m];
                    if (material == null) continue;
                    material.enableInstancing = true;
                    parts.Add(new Part
                    {
                        Mesh = filter.sharedMesh,
                        SubMesh = m,
                        FromRoot = toRoot * renderer.transform.localToWorldMatrix,
                        Params = new RenderParams(material)
                        {
                            shadowCastingMode = ShadowCastingMode.Off,
                            receiveShadows = true,
                            layer = renderer.gameObject.layer,
                            lightProbeUsage = LightProbeUsage.Off,
                            reflectionProbeUsage = renderer.reflectionProbeUsage,
                        },
                    });
                }
            }
            var result = parts.ToArray();
            if (result.Length == 0 && s_warned.Add(prefab.name))
                FiresLogger.LogWarning($"[FiresVoxel] cave clutter: '{prefab.name}' has no mesh to draw - skipped.");
            s_parts[prefab] = result;
            return result;
        }

        // Per column: the skylands over it (their undersides and tops in chunk-local metres), and the ground's open
        // surface - its highest solid-to-air crossing in this chunk outside every skyland, or none at all when the
        // ground is above the chunk (then every crossing in it is underground).
        private static void PrepareColumns(VoxelChunk chunk, Vector3 origin)
        {
            float chunkTop = origin.y + Cells;
            for (int z = 0; z < Cells; z++)
                for (int x = 0; x < Cells; x++)
                {
                    int column = x + Cells * z;
                    s_columnBiome[column] = (Heightmap.Biome)(-1);
                    int spans = Mathf.Min(FiresSkyIslands.GetIslandSpans(origin.x + x, origin.z + z, s_spanScratch), MaxSpans);
                    s_spanCount[column] = spans;
                    for (int s = 0; s < spans; s++)
                    {
                        var span = s_spanScratch[s];
                        s_spanBottom[column * MaxSpans + s] = span.bottom - origin.y;
                        s_spanTop[column * MaxSpans + s] = span.top - origin.y;
                        s_spanBiome[column * MaxSpans + s] = span.biome;
                    }
                    float ground = s_ground[(x + 1) + Samples * (z + 1)];
                    if (float.IsNaN(ground) && !VoxelZoneStreamer.TryGroundHeight(origin.x + x, origin.z + z, out ground))
                        ground = float.MinValue;
                    if ((D(x, Cells, z) > 0f && !NearSkyland(column, Cells)) || ground > chunkTop + GroundAboveChunk)
                    {
                        s_openTop[column] = float.MaxValue;
                        continue;
                    }
                    float open = float.MinValue;
                    for (int y = Cells - 1; y >= -1; y--)
                    {
                        float below = D(x, y, z), above = D(x, y + 1, z);
                        if (below <= 0f || above > 0f) continue;
                        float crossing = y + below / (below - above);
                        if (NearSkyland(column, crossing)) continue;
                        open = crossing;
                        break;
                    }
                    s_openTop[column] = open;
                }
        }

        // A cave surface: strictly inside a skyland (between its underside and top), or rock-roofed under the ground's
        // open surface. A skyland's own outside - top, cliffs and underside - never counts.
        private static bool TryCaveSurface(Vector3 origin, int x, int z, float localY, out Heightmap.Biome biome)
        {
            int column = Mathf.Clamp(x, 0, Cells - 1) + Cells * Mathf.Clamp(z, 0, Cells - 1);
            for (int s = 0; s < s_spanCount[column]; s++)
            {
                float bottom = s_spanBottom[column * MaxSpans + s], top = s_spanTop[column * MaxSpans + s];
                if (localY < bottom - SkylandBand || localY > top + SkylandBand) continue;
                biome = s_spanBiome[column * MaxSpans + s];
                return localY > bottom + SkylandBand && localY < top - SkylandBand;
            }
            biome = ColumnBiome(origin, x, z);
            return localY < s_openTop[column] - OpenSurfaceBand;
        }

        private static bool NearSkyland(int column, float localY)
        {
            for (int s = 0; s < s_spanCount[column]; s++)
                if (localY >= s_spanBottom[column * MaxSpans + s] - SkylandBand && localY <= s_spanTop[column * MaxSpans + s] + SkylandBand)
                    return true;
            return false;
        }

        private static Heightmap.Biome ColumnBiome(Vector3 origin, int x, int z)
        {
            x = Mathf.Clamp(x, 0, Cells - 1);
            z = Mathf.Clamp(z, 0, Cells - 1);
            int column = x + Cells * z;
            var biome = s_columnBiome[column];
            if ((int)biome == -1)
            {
                biome = FiresCaveBiome.RegionBiome(origin.x + x, origin.z + z, WorldGenerator.instance.GetBiome(origin.x + x, origin.z + z));   // 0.2.797: the cave region's biome
                s_columnBiome[column] = biome;
            }
            return biome;
        }

        private static bool Reproject(Vector3 start, Vector3 normal, out Vector3 seat)
        {
            var from = start + normal * ReprojectLift;
            seat = from;
            float previous = Sample(from);
            if (previous > 0f) return false;
            for (float d = ReprojectStep; d <= ReprojectReach; d += ReprojectStep)
            {
                float here = Sample(from - normal * d);
                if (here <= 0f)
                {
                    previous = here;
                    continue;
                }
                seat = from - normal * (d - ReprojectStep + ReprojectStep * previous / (previous - here));
                return true;
            }
            return false;
        }

        private static Vector3 Normal(Vector3 local)
        {
            var gradient = new Vector3(
                Sample(local + Vector3.right * NormalStep) - Sample(local - Vector3.right * NormalStep),
                Sample(local + Vector3.up * NormalStep) - Sample(local - Vector3.up * NormalStep),
                Sample(local + Vector3.forward * NormalStep) - Sample(local - Vector3.forward * NormalStep));
            return gradient.sqrMagnitude > 1e-6f ? -gradient.normalized : Vector3.up;
        }

        // Trilinear density over the snapshot, in chunk-local metres; the one-sample apron covers [-1, 32].
        private static float Sample(Vector3 p)
        {
            float px = Mathf.Clamp(p.x, -1f, Cells), py = Mathf.Clamp(p.y, -1f, Cells), pz = Mathf.Clamp(p.z, -1f, Cells);
            int x0 = Mathf.Min(Mathf.FloorToInt(px), Cells - 1), y0 = Mathf.Min(Mathf.FloorToInt(py), Cells - 1), z0 = Mathf.Min(Mathf.FloorToInt(pz), Cells - 1);
            float fx = px - x0, fy = py - y0, fz = pz - z0;
            float c00 = Mathf.Lerp(D(x0, y0, z0), D(x0 + 1, y0, z0), fx);
            float c10 = Mathf.Lerp(D(x0, y0 + 1, z0), D(x0 + 1, y0 + 1, z0), fx);
            float c01 = Mathf.Lerp(D(x0, y0, z0 + 1), D(x0 + 1, y0, z0 + 1), fx);
            float c11 = Mathf.Lerp(D(x0, y0 + 1, z0 + 1), D(x0 + 1, y0 + 1, z0 + 1), fx);
            return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
        }

        private static float D(int x, int y, int z) => s_voxels[(x + 1) + Samples * ((y + 1) + Samples * (z + 1))];

        // A biome's layers, re-read whenever its cfg line changes; every chunk is rebuilt with them.
        private static List<Layer> LayersFor(Heightmap.Biome biome)
        {
            if (!s_biomeEntries.TryGetValue(biome, out var entry)) return null;
            string csv = entry.Value ?? "";
            bool reread = s_parsedFrom.TryGetValue(biome, out var parsed);
            if (reread && parsed == csv) return s_layers[biome];
            s_parsedFrom[biome] = csv;
            var layers = new List<Layer>();
            foreach (var raw in csv.Split(','))
            {
                var item = raw.Trim();
                if (item.Length == 0) continue;
                var fields = item.Split(':');
                var sizes = fields.Length == 4 ? fields[3].Split('-') : null;
                if (sizes == null || sizes.Length != 2 || !TryParseSurface(fields[1], out var on)
                    || !float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float perSquareMetre)
                    || !float.TryParse(sizes[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float minSize)
                    || !float.TryParse(sizes[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float maxSize))
                {
                    if (s_warned.Add(item))
                        FiresLogger.LogWarning($"[FiresVoxel] cave clutter '{entry.Definition.Key}': '{item}' is not prefab:surface:per m2:min-max - skipped.");
                    continue;
                }
                var prefab = ZNetScene.instance.GetPrefab(fields[0].Trim());
                if (prefab == null)
                {
                    if (s_warned.Add(item))
                        FiresLogger.LogWarning($"[FiresVoxel] cave clutter '{entry.Definition.Key}': no prefab '{fields[0].Trim()}' - skipped.");
                    continue;
                }
                layers.Add(new Layer { Prefab = prefab, On = on, PerSquareMetre = perSquareMetre, MinSize = minSize, MaxSize = Mathf.Max(minSize, maxSize) });
            }
            s_layers[biome] = layers;
            if (reread) s_chunks.Clear();
            return layers;
        }

        private static bool TryParseSurface(string text, out CaveSurface surface)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "floor": surface = CaveSurface.Floor; return true;
                case "wall": surface = CaveSurface.Wall; return true;
                case "ceiling": surface = CaveSurface.Ceiling; return true;
                default: surface = CaveSurface.Floor; return false;
            }
        }

        // Deterministic per lattice edge: the same world, chunk and cell give the same pieces on every client.
        private struct CellRandom
        {
            private uint m_state;

            public CellRandom(int seed, Vector3Int key, int x, int y, int z, int axis)
            {
                uint h = (uint)seed * 0x9E3779B1u;
                h = Mix(h ^ (uint)key.x * 0x85EBCA77u);
                h = Mix(h ^ (uint)key.y * 0xC2B2AE3Du);
                h = Mix(h ^ (uint)key.z * 0x27D4EB2Fu);
                h = Mix(h ^ (uint)(x | y << 6 | z << 12 | axis << 18) * 0x165667B1u);
                m_state = h == 0 ? 0x6D2B79F5u : h;
            }

            public float Next()
            {
                m_state ^= m_state << 13;
                m_state ^= m_state >> 17;
                m_state ^= m_state << 5;
                return (m_state >> 8) * (1f / 16777216f);
            }

            private static uint Mix(uint h)
            {
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return h;
            }
        }
    }
}
