using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // Underwater clutter on lakebeds and river beds: reeds at the
    // shallow edges, swaying weeds mid-depth, sparse dark tufts deep and silt patches, on the beds of FAT's GENERATED water.
    // Art: first-party clutter cards (16 atlases, embedded: Assets\Underwater\<kind>_<biome>.png). Meshes are built here
    // (2 crossed planes, or 1 flat quad for silt; one mesh per atlas cell), drawn GPU-instanced with a clone of vanilla's instanced
    // GRASS clutter material, so wind sway, lighting and fade match vanilla grass. Placement: 32 m world cells around the camera;
    // per sample the water level + depth come from GenWaterField (the field the water sheet is drawn from) and the bed from a
    // terrain ray, so nothing is placed where the water or the collider is not there. Submerged growth (weed, deep) is clamped
    // under the surface; reeds may break it. [Voxel Terrain] "Underwater clutter" (default OFF).
    internal static class VoxelUnderwaterClutter
    {
        private const string S = "Voxel Terrain";
        private const float Cell = 32f, MaintainSeconds = 0.25f, RangeMargin = 24f;
        private const int MaxInstancesPerDraw = 1023, MaxPerCell = 1600, CellsPerTick = 2;

        private enum Band { Shallow, Mid, Deep, Any }
        private sealed class Layer { public string Kind; public Band Band; public float PerM2, Min, Max; }
        private sealed class Batch { public Mesh Mesh; public RenderParams Params; public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>(); }
        private sealed class CellClutter { public Bounds Bounds; public readonly List<Batch> Batches = new List<Batch>(); public int Count; }

        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<float> s_range;
        private static readonly Dictionary<string, ConfigEntry<string>> s_biomeLayers = new Dictionary<string, ConfigEntry<string>>();
        private static readonly Dictionary<Vector2Int, CellClutter> s_cells = new Dictionary<Vector2Int, CellClutter>();
        private static readonly Dictionary<Vector2Int, float> s_retryAt = new Dictionary<Vector2Int, float>();
        private static readonly Dictionary<string, Mesh[]> s_meshes = new Dictionary<string, Mesh[]>();
        private static readonly Dictionary<string, RenderParams> s_params = new Dictionary<string, RenderParams>();
        private static readonly Dictionary<string, List<Layer>> s_parsed = new Dictionary<string, List<Layer>>();
        private static readonly Dictionary<string, string> s_parsedFrom = new Dictionary<string, string>();
        private static Material s_grassBase;
        private static bool s_failed, s_loggedSource;
        private static float s_nextMaintain, s_reportAt;
        private static int s_terrainMask;
        private static readonly int[] s_kindCount = new int[4];
        private static readonly string[] Kinds = { "reed", "weed", "deep", "silt" };

        // biome palette key -> default layers ("kind:band:per square metre:min-max metres")
        private static readonly (string Palette, string Key, string Layers)[] Defaults =
        {
            ("meadows",  "Underwater clutter - Meadows",  "reed:shallow:0.6:1.2-2.0, weed:mid:0.25:0.8-1.5, deep:deep:0.06:0.3-0.6, silt:any:0.05:0.8-1.6"),
            ("plains",   "Underwater clutter - Plains",   "reed:shallow:0.7:1.2-2.0, weed:mid:0.3:0.8-1.5, deep:deep:0.08:0.3-0.6, silt:any:0.06:0.8-1.6"),
            ("swamp",    "Underwater clutter - Swamp",    "reed:shallow:0.9:1.2-2.0, weed:mid:0.45:0.8-1.5, deep:deep:0.12:0.3-0.6, silt:any:0.04:0.8-1.6"),
            ("mountain", "Underwater clutter - Mountain", "reed:shallow:0.15:1.2-2.0, weed:mid:0.08:0.8-1.5, deep:deep:0.03:0.3-0.6, silt:any:0.08:0.8-1.6"),
        };

        internal static void Bind(ConfigFile cfg)
        {
            s_enabled = cfg.Bind(S, "Underwater clutter", false,
                "OFF (default): lake and river beds stay bare. ON: reeds at the shallow edges, swaying weeds mid-depth, sparse dark tufts deep down and silt patches on the beds of generated water, drawn like vanilla grass (wind sway, distance fade). One Info line a minute with counts.");
            s_range = cfg.Bind(S, "Underwater clutter range", 60f, new ConfigDescription("Draw distance (metres).", new AcceptableValueRange<float>(16f, 160f)));
            foreach (var d in Defaults)
                s_biomeLayers[d.Palette] = cfg.Bind(S, d.Key, d.Layers,
                    "kind:band:per square metre:min-max size in metres, comma separated. kind = reed, weed, deep or silt; band = shallow (0.2-0.8 m deep), mid (0.8-3 m), deep (over 3 m) or any.");
        }

        internal static void Clear() { s_cells.Clear(); s_retryAt.Clear(); }

        internal static void Tick()
        {
            if (s_enabled == null || !s_enabled.Value || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || s_failed)
            {
                if (s_cells.Count > 0) Clear();
                return;
            }
            var cam = Utils.GetMainCamera();
            if (cam == null || WorldGenerator.instance == null || !EnsureMaterialBase()) return;
            Vector3 eye = cam.transform.position;
            float range = s_range.Value;
            if (Time.time >= s_nextMaintain) { s_nextMaintain = Time.time + MaintainSeconds; Maintain(eye, range); }
            float r2 = range * range;
            foreach (var c in s_cells.Values)
            {
                if (c.Count == 0 || c.Bounds.SqrDistance(eye) > r2) continue;
                foreach (var b in c.Batches)
                {
                    var rp = b.Params; rp.worldBounds = c.Bounds;
                    int n = b.Matrices.Count;
                    try
                    {
                        for (int s = 0; s < n; s += MaxInstancesPerDraw)
                            Graphics.RenderMeshInstanced(in rp, b.Mesh, 0, b.Matrices, Mathf.Min(MaxInstancesPerDraw, n - s), s);
                    }
                    catch (Exception ex) { s_failed = true; FiresLogger.LogWarning($"[FiresVoxel] underwater clutter drawing stopped: {ex.Message}"); return; }
                }
            }
            if (Time.time >= s_reportAt)
            {
                s_reportAt = Time.time + 60f;
                int cells = 0; Array.Clear(s_kindCount, 0, 4);
                foreach (var c in s_cells.Values) if (c.Count > 0) cells++;
                FiresLogger.LogInfo($"[FiresVoxel] underwater clutter: {s_cells.Count} cell(s) built, {cells} with clutter; material from '{s_grassBase?.name}' ({s_grassBase?.shader?.name}) (Underwater clutter = ON)");
            }
        }

        private static void Maintain(Vector3 eye, float range)
        {
            float keep = range + RangeMargin;
            var drop = new List<Vector2Int>();
            foreach (var kv in s_cells) if (kv.Value.Bounds.SqrDistance(eye) > keep * keep) drop.Add(kv.Key);
            foreach (var k in drop) s_cells.Remove(k);
            int cr = Mathf.CeilToInt(range / Cell), cx0 = Mathf.FloorToInt(eye.x / Cell), cz0 = Mathf.FloorToInt(eye.z / Cell);
            var want = new List<(float d, Vector2Int k)>();
            for (int dz = -cr; dz <= cr; dz++)
                for (int dx = -cr; dx <= cr; dx++)
                {
                    var k = new Vector2Int(cx0 + dx, cz0 + dz);
                    if (s_cells.ContainsKey(k)) continue;
                    if (s_retryAt.TryGetValue(k, out float t) && Time.time < t) continue;
                    var centre = new Vector2((k.x + 0.5f) * Cell, (k.y + 0.5f) * Cell);
                    float d = (centre - new Vector2(eye.x, eye.z)).magnitude;
                    if (d <= range + Cell) want.Add((d, k));
                }
            want.Sort((a, b) => a.d.CompareTo(b.d));
            for (int i = 0; i < want.Count && i < CellsPerTick; i++) BuildCell(want[i].k);
        }

        private static void BuildCell(Vector2Int k)
        {
            float x0 = k.x * Cell, z0 = k.y * Cell;
            // dry cell (no generated water at its 5 sample points): nothing to place, cache it empty
            bool anyWater = false;
            for (int i = 0; i < 5 && !anyWater; i++)
            {
                float sx = x0 + (i == 0 ? Cell * 0.5f : ((i & 1) != 0 ? 2f : Cell - 2f)), sz = z0 + (i == 0 ? Cell * 0.5f : ((i & 2) != 0 ? 2f : Cell - 2f));
                anyWater = GenWaterField.TryWaterSurfaceSmooth(sx, sz, out _, out float dd) && dd > 0.2f;
            }
            var cell = new CellClutter { Bounds = new Bounds(new Vector3(x0 + Cell * 0.5f, 0f, z0 + Cell * 0.5f), Vector3.zero) };
            if (!anyWater) { cell.Bounds = new Bounds(cell.Bounds.center, new Vector3(Cell, 1f, Cell)); s_cells[k] = cell; return; }

            string palette = PaletteAt(x0 + Cell * 0.5f, z0 + Cell * 0.5f);
            if (palette == null) { s_cells[k] = cell; return; }   // ashlands etc.: none
            var layers = LayersFor(palette);
            var rng = new System.Random(k.x * 73856093 ^ k.y * 19349663);
            int tries = 0, misses = 0;
            bool boundsSet = false;
            var batches = new Dictionary<(string, int), Batch>();
            foreach (var layer in layers)
            {
                int n = Mathf.Min(MaxPerCell, Mathf.RoundToInt(layer.PerM2 * Cell * Cell));
                for (int i = 0; i < n && cell.Count < MaxPerCell; i++)
                {
                    float x = x0 + (float)rng.NextDouble() * Cell, z = z0 + (float)rng.NextDouble() * Cell;
                    if (!GenWaterField.TryWaterSurfaceSmooth(x, z, out float level, out float depth) || depth < 0.2f) continue;
                    if (!InBand(layer.Band, depth)) continue;
                    tries++;
                    if (!Physics.Raycast(new Vector3(x, level + 0.5f, z), Vector3.down, out RaycastHit hit, depth + 6f, s_terrainMask)) { misses++; continue; }
                    float bed = hit.point.y;
                    if (bed > level - 0.1f) continue;   // the bed is at/above the surface here: not under water
                    float size = Mathf.Lerp(layer.Min, layer.Max, (float)rng.NextDouble());
                    float waterAbove = level - bed;
                    if (layer.Kind == "weed" || layer.Kind == "deep") size = Mathf.Min(size, waterAbove - 0.15f);   // stays submerged
                    if (size < 0.1f) continue;
                    int variant = rng.Next(4);
                    string key = layer.Kind + "_" + palette;
                    if (!batches.TryGetValue((key, variant), out var batch))
                    {
                        var mesh = MeshFor(layer.Kind, variant);
                        if (mesh == null || !TryParams(key, out var rp)) continue;
                        batch = new Batch { Mesh = mesh, Params = rp };
                        batches[(key, variant)] = batch;
                        cell.Batches.Add(batch);
                    }
                    var pos = new Vector3(x, bed + (layer.Kind == "silt" ? 0.03f : -0.05f), z);
                    var rot = layer.Kind == "silt" ? Quaternion.FromToRotation(Vector3.up, hit.normal) * Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f)
                                                   : Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                    batch.Matrices.Add(Matrix4x4.TRS(pos, rot, Vector3.one * size));
                    cell.Count++;
                    s_kindCount[Array.IndexOf(Kinds, layer.Kind)]++;
                    var b = new Bounds(pos + Vector3.up * size * 0.5f, new Vector3(size, size, size));
                    if (!boundsSet) { cell.Bounds = b; boundsSet = true; } else cell.Bounds.Encapsulate(b);
                }
            }
            // the bed collider was not there for most samples (not streamed yet): try again shortly, don't cache a bare cell
            if (tries > 8 && misses * 2 > tries) { s_retryAt[k] = Time.time + 2f; return; }
            if (!boundsSet) cell.Bounds = new Bounds(new Vector3(x0 + Cell * 0.5f, 0f, z0 + Cell * 0.5f), new Vector3(Cell, 1f, Cell));
            s_cells[k] = cell;
            if (cell.Count > 0)
                FiresLogger.LogDebug($"[FiresVoxel] underwater clutter cell ({k.x},{k.y}) {palette}: {cell.Count} piece(s) in {cell.Batches.Count} batch(es), {misses}/{tries} bed rays missed");
        }

        private static bool InBand(Band band, float depth)
        {
            switch (band)
            {
                case Band.Shallow: return depth >= 0.2f && depth < 0.8f;
                case Band.Mid: return depth >= 0.8f && depth < 3f;
                case Band.Deep: return depth >= 3f;
                default: return depth >= 0.2f;
            }
        }

        private static string PaletteAt(float x, float z)
        {
            switch (WorldGenerator.instance.GetBiome(x, z))
            {
                case Heightmap.Biome.Meadows: case Heightmap.Biome.BlackForest: return "meadows";
                case Heightmap.Biome.Plains: return "plains";
                case Heightmap.Biome.Swamp: return "swamp";
                case Heightmap.Biome.Mountain: case Heightmap.Biome.DeepNorth: case Heightmap.Biome.Mistlands: return "mountain";
                default: return null;
            }
        }

        private static List<Layer> LayersFor(string palette)
        {
            string text = s_biomeLayers[palette].Value ?? "";
            if (s_parsedFrom.TryGetValue(palette, out string was) && was == text) return s_parsed[palette];
            var list = new List<Layer>();
            foreach (var raw in text.Split(','))
            {
                var p = raw.Trim().Split(':');
                if (p.Length != 4) continue;
                var mm = p[3].Split('-');
                if (Array.IndexOf(Kinds, p[0]) < 0 || mm.Length != 2) continue;
                Band band = p[1] == "shallow" ? Band.Shallow : p[1] == "mid" ? Band.Mid : p[1] == "deep" ? Band.Deep : Band.Any;
                if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float per)
                    || !float.TryParse(mm[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float lo)
                    || !float.TryParse(mm[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float hi)) continue;
                list.Add(new Layer { Kind = p[0], Band = band, PerM2 = Mathf.Max(0f, per), Min = lo, Max = Mathf.Max(lo, hi) });
            }
            s_parsed[palette] = list; s_parsedFrom[palette] = text;
            s_cells.Clear();   // a changed layer string rebuilds
            return list;
        }

        // vanilla's instanced grass clutter material (Custom/Grass family): its wind sway, lighting and fade
        private static bool EnsureMaterialBase()
        {
            if (s_grassBase != null) return true;
            if (ClutterSystem.instance == null) return false;
            s_terrainMask = LayerMask.GetMask("terrain");
            foreach (var c in ClutterSystem.instance.m_clutter)
            {
                if (c == null || c.m_prefab == null || !c.m_instanced) continue;
                var ir = c.m_prefab.GetComponent<InstanceRenderer>();
                if (ir == null || ir.m_material == null || ir.m_material.shader == null) continue;
                if (ir.m_material.shader.name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) < 0) continue;
                s_grassBase = ir.m_material;
                if (!s_loggedSource)
                {
                    s_loggedSource = true;
                    var cols = ir.m_mesh != null ? ir.m_mesh.colors : null;
                    string colInfo = cols != null && cols.Length > 0 ? $"vertex colours present ({cols.Length}; first {cols[0]}, last {cols[cols.Length - 1]})" : "no vertex colours";
                    FiresLogger.LogInfo($"[FiresVoxel] underwater clutter: material base '{s_grassBase.name}' shader '{s_grassBase.shader.name}' from clutter '{c.m_name}'; its mesh has {colInfo}");
                }
                return true;
            }
            if (!s_loggedSource) { s_loggedSource = true; FiresLogger.LogWarning("[FiresVoxel] underwater clutter: no instanced grass clutter material found; nothing drawn"); }
            return false;
        }

        private static bool TryParams(string key, out RenderParams rp)
        {
            if (s_params.TryGetValue(key, out rp)) return true;
            var tex = LoadTexture(key);
            if (tex == null) return false;
            var mat = new Material(s_grassBase) { name = "FAT_underwater_" + key, mainTexture = tex, enableInstancing = true };
            rp = new RenderParams(mat)
            {
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = true,
                layer = LayerMask.NameToLayer("InstanceRenderer") >= 0 ? LayerMask.NameToLayer("InstanceRenderer") : 0,
                lightProbeUsage = LightProbeUsage.Off,
            };
            s_params[key] = rp;
            return true;
        }

        private static readonly System.Reflection.MethodInfo s_loadImage = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });

        private static Texture2D LoadTexture(string key)
        {
            var asm = typeof(VoxelUnderwaterClutter).Assembly;
            string name = "FAT.Underwater." + key + ".png";
            using (var st = asm.GetManifestResourceStream(name))
            {
                if (st == null) { FiresLogger.LogWarning($"[FiresVoxel] underwater clutter: embedded texture {name} missing"); return null; }
                var bytes = new byte[st.Length];
                int read = 0; while (read < bytes.Length) { int r = st.Read(bytes, read, bytes.Length - read); if (r <= 0) break; read += r; }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = key, wrapMode = TextureWrapMode.Clamp };
                if (!(s_loadImage != null && s_loadImage.Invoke(null, new object[] { tex, bytes }) is bool ok && ok)) return null;   // ImageConversion by reflection (CS1705, see ZoneModeBlueprintIcons)
                tex.Compress(true);
                tex.Apply(true, true);
                return tex;
            }
        }

        // 2 crossed planes (or 1 flat quad for silt), base at the origin, height 1, cell `variant` of the atlas;
        // vertex colour alpha = uv.y (the sway weight convention of the cards: base pinned, tip sways), RGB white.
        private static Mesh MeshFor(string kind, int variant)
        {
            if (!s_meshes.TryGetValue(kind, out var arr)) { arr = new Mesh[4]; s_meshes[kind] = arr; }
            if (arr[variant] != null) return arr[variant];
            var m = new Mesh { name = "FAT_uw_" + kind + variant };
            if (kind == "silt")
            {
                float u0 = (variant % 2) * 0.5f, v0 = (variant / 2) * 0.5f, h = 0.5f;
                m.vertices = new[] { new Vector3(-h, 0, -h), new Vector3(h, 0, -h), new Vector3(h, 0, h), new Vector3(-h, 0, h) };
                m.uv = new[] { new Vector2(u0, v0), new Vector2(u0 + 0.5f, v0), new Vector2(u0 + 0.5f, v0 + 0.5f), new Vector2(u0, v0 + 0.5f) };
                m.colors = new[] { new Color(1, 1, 1, 0), new Color(1, 1, 1, 0), new Color(1, 1, 1, 0), new Color(1, 1, 1, 0) };
                m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            }
            else
            {
                float w = 0.25f, u0 = variant * 0.25f, u1 = u0 + 0.25f;
                var v = new List<Vector3>(); var uv = new List<Vector2>(); var col = new List<Color>(); var tri = new List<int>(); var nrm = new List<Vector3>();
                for (int p = 0; p < 2; p++)
                {
                    var right = p == 0 ? Vector3.right : Vector3.forward;
                    var n = p == 0 ? Vector3.forward : Vector3.left;
                    int b0 = v.Count;
                    v.Add(-right * w); v.Add(right * w); v.Add(right * w + Vector3.up); v.Add(-right * w + Vector3.up);
                    uv.Add(new Vector2(u0, 0)); uv.Add(new Vector2(u1, 0)); uv.Add(new Vector2(u1, 1)); uv.Add(new Vector2(u0, 1));
                    col.Add(new Color(1, 1, 1, 0)); col.Add(new Color(1, 1, 1, 0)); col.Add(new Color(1, 1, 1, 1)); col.Add(new Color(1, 1, 1, 1));
                    for (int q = 0; q < 4; q++) nrm.Add(Vector3.up);   // grass-style: lit from above, both faces alike
                    tri.AddRange(new[] { b0, b0 + 2, b0 + 1, b0, b0 + 3, b0 + 2, b0, b0 + 1, b0 + 2, b0, b0 + 2, b0 + 3 });   // both sides
                }
                m.SetVertices(v); m.SetUVs(0, uv); m.SetColors(col); m.SetNormals(nrm); m.SetTriangles(tri, 0);
            }
            m.RecalculateBounds();
            arr[variant] = m;
            return m;
        }
    }
}
