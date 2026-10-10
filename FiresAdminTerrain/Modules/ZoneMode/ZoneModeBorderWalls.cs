using System.Collections.Generic;
using FiresCore.Materials;
using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // The cliff where an open zone meets a closed one. Every open-zone edge whose neighbour is closed
    // gets a flat wall in the border plane, down below y0 into the sea floor, drawn out to FarRange.
    // Where voxel chunks hold the border it is cut from their density (caves, river beds and digs open
    // through it) and gets a collider; farther out it follows the generator's ground at the border. When
    // the neighbour opens the wall is removed and the terrain joins as normal.
    internal static class ZoneModeBorderWalls
    {
        private const float TickSeconds = 0.25f;
        private const int VoxelBuildsPerTick = 2;
        private const int FarBuildsPerTick = 6;
        private const float FarRange = 1000f;
        private const float DropSlack = 128f;
        private const float WallInset = 0.02f;
        private const int SolidBias = 3;
        private const int WallTopDropMetres = 2;
        private const int AirDensity = -127;
        private const int BelowFloorMetres = 2;
        private const int ZoneCells = 64;
        private const int GroundHeadroom = 2;

        private enum ChunkState { Absent, Pending, Ready }

        // An open zone that only the vanilla distant LOD draws (its 10 m grid slumps inside the rim) gets a
        // top surface from the same ground the far walls follow, so the island reads as one solid block.
        private sealed class FarTop
        {
            internal GameObject Go;
            internal Mesh Mesh;
        }

        private const float FarLift = 0.25f;
        private const int TopCells = 32;
        private const int TopBuildsPerTick = 4;
        private static readonly Dictionary<Vector2s, FarTop> s_tops = new Dictionary<Vector2s, FarTop>();
        private static readonly List<Vector2s> s_topDrop = new List<Vector2s>();
        private static readonly List<Vector3> s_topVerts = new List<Vector3>((TopCells + 1) * (TopCells + 1));
        private static readonly List<Color32> s_topCols = new List<Color32>((TopCells + 1) * (TopCells + 1));
        private static readonly List<Vector2> s_topUvs = new List<Vector2>((TopCells + 1) * (TopCells + 1));
        private static readonly List<Vector2> s_topUv1 = new List<Vector2>((TopCells + 1) * (TopCells + 1));
        private static readonly List<int> s_topTris = new List<int>(TopCells * TopCells * 6);

        private sealed class Wall
        {
            internal GameObject Go;
            internal Mesh Mesh;
            internal MeshCollider Collider;
            internal uint Signature;
            internal bool Voxel;
            internal GameObject FallsGo;
            internal Mesh FallsMesh;
            internal int FallRuns;
        }

        private static readonly float[] s_grounds = new float[ZoneCells + 1];

        private static readonly List<FiresWaterfalls.Column> s_fallColumns = new List<FiresWaterfalls.Column>(80);
        private static readonly List<Vector3> s_fallSites = new List<Vector3>(8);
        private static readonly List<FiresWaterfallMist.Site> s_mistSites = new List<FiresWaterfallMist.Site>(8);
        private static readonly float[] s_levels = new float[ZoneCells + 1];
        private static readonly bool[] s_outflow = new bool[ZoneCells + 1];

        private static readonly Dictionary<(Vector2s zone, int edge), Wall> s_walls = new Dictionary<(Vector2s, int), Wall>();
        private static readonly HashSet<(Vector2s zone, int edge)> s_wanted = new HashSet<(Vector2s, int)>();
        private static readonly List<(Vector2s zone, int edge)> s_drop = new List<(Vector2s, int)>();
        private static readonly List<Vector3> s_verts = new List<Vector3>(4096);
        private static readonly List<Vector3> s_norms = new List<Vector3>(4096);
        private static readonly List<Vector4> s_tans = new List<Vector4>(4096);
        private static readonly List<Vector2> s_uvs = new List<Vector2>(4096);
        private static readonly List<Vector2> s_uv1 = new List<Vector2>(4096);
        private static readonly List<Color32> s_cols = new List<Color32>(4096);
        private static readonly List<int> s_tris = new List<int>(8192);
        private static readonly Vector2[] s_poly = new Vector2[8];
        private static readonly Vector2[] s_corners = new Vector2[4];
        private static readonly int[] s_values = new int[4];
        private static readonly Color32[] s_edgeColors = new Color32[ZoneCells + 1];
        private static readonly List<Vector3> s_recolourVerts = new List<Vector3>(4096);
        private static readonly List<Color32> s_recolourCols = new List<Color32>(4096);

        private static GameObject s_root;
        private static float s_nextTick;
        private static float s_nextFailureLog;
        private static bool s_flip;
        private static bool s_headless;
        private static bool s_headlessKnown;

        internal static int WallCount => s_walls.Count;

        internal static int FallCount
        {
            get
            {
                int count = 0;
                foreach (var kv in s_walls) count += kv.Value.FallRuns;
                return count;
            }
        }
        internal static int BuiltTotal { get; private set; }
        internal static int CaveCellsCut { get; private set; }

        internal static void Tick()
        {
            if (!s_headlessKnown)
            {
                s_headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
                s_headlessKnown = true;
            }
            if (s_headless) return;
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + TickSeconds;

            if (!ZoneModeRegistry.ModeActive || !FiresVoxelConfig.StreamerRuns)
            {
                if (s_walls.Count > 0 || s_tops.Count > 0) Reset();
                return;
            }
            FiresWaterfalls.Tick();

            var cam = Utils.GetMainCamera();
            Vector3 center = cam != null ? cam.transform.position
                : Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            if (cam == null && Player.m_localPlayer == null) return;

            CollectWanted(center, FarRange);

            s_drop.Clear();
            foreach (var kv in s_walls)
                if (!s_wanted.Contains(kv.Key) && !StillWanted(kv.Key, center, FarRange + DropSlack)) s_drop.Add(kv.Key);
            foreach (var key in s_drop) Destroy(key);

            TickFarTops(center);

            int voxelBuilt = 0, farBuilt = 0;
            foreach (var key in s_wanted)
            {
                var state = Probe(key.zone, key.edge, out uint signature);
                if (state == ChunkState.Pending) continue;
                bool voxel = state == ChunkState.Ready;
                if (!voxel) signature = FarSignature();
                if (s_walls.TryGetValue(key, out var existing) && existing.Signature == signature && existing.Voxel == voxel) continue;
                if (voxel ? voxelBuilt >= VoxelBuildsPerTick : farBuilt >= FarBuildsPerTick) continue;
                if (voxel) voxelBuilt++; else farBuilt++;
                try { Build(key.zone, key.edge, signature, voxel); }
                catch (System.Exception ex)
                {
                    if (Time.time >= s_nextFailureLog)
                    {
                        s_nextFailureLog = Time.time + 30f;
                        FiresLogger.LogWarning($"[FiresZones] border wall ({key.zone.x},{key.zone.y}) {"WESN"[key.edge]} failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        private static void TickFarTops(Vector3 center)
        {
            s_topDrop.Clear();
            foreach (var kv in s_tops)
                if (!ZoneModeRegistry.IsOpen(kv.Key) || !Within(kv.Key, center, FarRange + DropSlack)) s_topDrop.Add(kv.Key);
            foreach (var zone in s_topDrop) { DestroyTop(s_tops[zone]); s_tops.Remove(zone); }

            int built = 0;
            var lo = ZoneSystem.GetZone(new Vector3(center.x - FarRange, 0f, center.z - FarRange));
            var hi = ZoneSystem.GetZone(new Vector3(center.x + FarRange, 0f, center.z + FarRange));
            for (int zy = lo.y; zy <= hi.y; zy++)
            {
                for (int zx = lo.x; zx <= hi.x; zx++)
                {
                    var zone = new Vector2s(zx, zy);
                    if (!ZoneModeRegistry.IsOpen(zone)) continue;
                    bool drawnElsewhere = VoxelZoneStreamer.IsZoneSolid(zone) || NearHeightmapLoaded(zone);
                    if (s_tops.TryGetValue(zone, out var top))
                    {
                        if (top.Go != null && top.Go.activeSelf == drawnElsewhere) top.Go.SetActive(!drawnElsewhere);
                        continue;
                    }
                    if (drawnElsewhere || built >= TopBuildsPerTick) continue;
                    s_tops[zone] = BuildTop(zone);
                    built++;
                }
            }
        }

        private static bool Within(Vector2s zone, Vector3 center, float range)
            => Mathf.Abs(zone.x * 64f - center.x) <= range && Mathf.Abs(zone.y * 64f - center.z) <= range;

        private static bool NearHeightmapLoaded(Vector2s zone)
        {
            var hm = Heightmap.FindHeightmap(new Vector3(zone.x * 64f, 0f, zone.y * 64f));
            return hm != null && !hm.IsDistantLod;
        }

        private static FarTop BuildTop(Vector2s zone)
        {
            if (s_root == null) s_root = new GameObject("FiresZoneBorderWalls");
            s_topVerts.Clear(); s_topCols.Clear(); s_topUvs.Clear(); s_topUv1.Clear(); s_topTris.Clear();
            float x0 = zone.x * 64f - 32f, z0 = zone.y * 64f - 32f, step = 64f / TopCells;
            for (int j = 0; j <= TopCells; j++)
            {
                for (int i = 0; i <= TopCells; i++)
                {
                    float x = x0 + i * step, z = z0 + j * step;
                    s_topVerts.Add(new Vector3(x, AnalyticGround(x, z) + FarLift, z));
                    s_topCols.Add(TopColour(zone, x, z));
                    s_topUvs.Add(new Vector2(i / (float)TopCells, j / (float)TopCells));
                    s_topUv1.Add(Vector2.zero);
                }
            }
            int row = TopCells + 1;
            for (int j = 0; j < TopCells; j++)
            {
                for (int i = 0; i < TopCells; i++)
                {
                    int a = j * row + i, b = a + 1, c = a + row, d = c + 1;
                    s_topTris.Add(a); s_topTris.Add(c); s_topTris.Add(b);
                    s_topTris.Add(b); s_topTris.Add(c); s_topTris.Add(d);
                }
            }
            var go = new GameObject($"ZoneFarTop_{zone.x}_{zone.y}");
            go.transform.SetParent(s_root.transform, false);
            var mesh = new Mesh { name = go.name };
            mesh.SetVertices(s_topVerts);
            mesh.SetColors(s_topCols);
            mesh.SetUVs(0, s_topUvs);
            mesh.SetUVs(1, s_topUv1);
            mesh.SetTriangles(s_topTris, 0, calculateBounds: true);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = TerrainMaterialCache.GetCurrentMaterial();
            VoxelChunkView.ApplyShadowMode(renderer);
            VoxelRenderGroup.Apply(renderer);
            return new FarTop { Go = go, Mesh = mesh };
        }

        // A far top's colour at a vertex: the ground just inside the zone's rim, with its winter.
        private static Color32 TopColour(Vector2s zone, float x, float z)
        {
            float x0 = zone.x * 64f - 32f, z0 = zone.y * 64f - 32f;
            return FatSeasons.UnpaintedGroundColour(Mathf.Clamp(x, x0 + 0.5f, x0 + 63.5f), Mathf.Clamp(z, z0 + 0.5f, z0 + 63.5f));
        }

        // The colour each column of a wall wears: the ground half a metre inside the open zone, with its winter.
        private static void FillEdgeColours(Vector2s zone, int edge)
        {
            Vector3 inward = -Outward(edge);
            for (int t = 0; t <= ZoneCells; t++)
            {
                ColumnAt(zone, edge, t, out int wx, out int wz);
                s_edgeColors[t] = FatSeasons.UnpaintedGroundColour(wx + inward.x * 0.5f, wz + inward.z * 0.5f);
            }
        }

        private static int ColumnIndex(float along) => Mathf.Clamp(Mathf.RoundToInt(along), 0, ZoneCells);

        // Vedr seasons: live walls and far tops take the new ground colours in place; mesh and collider stay.
        internal static void CollectSeasonRecolours(List<FatSeasons.Recolour> into)
        {
            foreach (var kv in s_walls)
            {
                var key = kv.Key;
                into.Add(new FatSeasons.Recolour(ZoneCentre(key.zone), () => RecolourWall(key)));
            }
            foreach (var kv in s_tops)
            {
                var zone = kv.Key;
                into.Add(new FatSeasons.Recolour(ZoneCentre(zone), () => RecolourTop(zone)));
            }
        }

        private static Vector3 ZoneCentre(Vector2s zone) => new Vector3(zone.x * ZoneCells, 0f, zone.y * ZoneCells);

        // A wall vertex sits at start + column along its edge (MapPoint), so its column comes back from its position.
        private static void RecolourWall((Vector2s zone, int edge) key)
        {
            if (!s_walls.TryGetValue(key, out var wall) || wall.Mesh == null) return;
            FillEdgeColours(key.zone, key.edge);
            int start = AlongStart(key.zone, key.edge);
            bool alongX = AlongX(key.edge);
            wall.Mesh.GetVertices(s_recolourVerts);
            s_recolourCols.Clear();
            foreach (var v in s_recolourVerts) s_recolourCols.Add(s_edgeColors[ColumnIndex((alongX ? v.x : v.z) - start)]);
            wall.Mesh.SetColors(s_recolourCols);
        }

        private static void RecolourTop(Vector2s zone)
        {
            if (!s_tops.TryGetValue(zone, out var top) || top.Mesh == null) return;
            top.Mesh.GetVertices(s_recolourVerts);
            s_recolourCols.Clear();
            foreach (var v in s_recolourVerts) s_recolourCols.Add(TopColour(zone, v.x, v.z));
            top.Mesh.SetColors(s_recolourCols);
        }

        private static void DestroyTop(FarTop top)
        {
            if (top == null) return;
            if (top.Mesh != null) Object.Destroy(top.Mesh);
            if (top.Go != null) Object.Destroy(top.Go);
        }

        internal static void Reset()
        {
            foreach (var kv in s_tops) DestroyTop(kv.Value);
            s_tops.Clear();
            foreach (var kv in s_walls) DestroyWall(kv.Value);
            s_walls.Clear();
            s_wanted.Clear();
            if (s_root != null) Object.Destroy(s_root);
            s_root = null;
        }

        private static bool StillWanted((Vector2s zone, int edge) key, Vector3 center, float range)
        {
            if (!ZoneModeRegistry.IsOpen(key.zone) || ZoneModeRegistry.IsOpen(Neighbour(key.zone, key.edge))) return false;
            return Mathf.Abs(key.zone.x * 64f - center.x) <= range && Mathf.Abs(key.zone.y * 64f - center.z) <= range;
        }

        // A far wall follows the real ground only, so it never needs rebuilding while it stands.
        private static uint FarSignature() => 0xFA4u;

        private static void CollectWanted(Vector3 center, float range)
        {
            s_wanted.Clear();
            var lo = ZoneSystem.GetZone(new Vector3(center.x - range, 0f, center.z - range));
            var hi = ZoneSystem.GetZone(new Vector3(center.x + range, 0f, center.z + range));
            for (int zy = lo.y; zy <= hi.y; zy++)
            {
                for (int zx = lo.x; zx <= hi.x; zx++)
                {
                    var zone = new Vector2s(zx, zy);
                    if (!ZoneModeRegistry.IsOpen(zone)) continue;
                    for (int edge = 0; edge < 4; edge++)
                        if (!ZoneModeRegistry.IsOpen(Neighbour(zone, edge))) s_wanted.Add((zone, edge));
                }
            }
        }

        // 0 west (-x), 1 east (+x), 2 south (-z), 3 north (+z).
        private static Vector2s Neighbour(Vector2s zone, int edge)
        {
            switch (edge)
            {
                case 0: return new Vector2s(zone.x - 1, zone.y);
                case 1: return new Vector2s(zone.x + 1, zone.y);
                case 2: return new Vector2s(zone.x, zone.y - 1);
                default: return new Vector2s(zone.x, zone.y + 1);
            }
        }

        private static Vector3 Outward(int edge)
        {
            switch (edge)
            {
                case 0: return Vector3.left;
                case 1: return Vector3.right;
                case 2: return Vector3.back;
                default: return Vector3.forward;
            }
        }

        private static bool AlongX(int edge) => edge >= 2;

        private static int PlaneCoordinate(Vector2s zone, int edge)
        {
            switch (edge)
            {
                case 0: return zone.x * ZoneCells - ZoneCells / 2;
                case 1: return zone.x * ZoneCells + ZoneCells / 2;
                case 2: return zone.y * ZoneCells - ZoneCells / 2;
                default: return zone.y * ZoneCells + ZoneCells / 2;
            }
        }

        private static int AlongStart(Vector2s zone, int edge)
            => (AlongX(edge) ? zone.x : zone.y) * ZoneCells - ZoneCells / 2;

        private static void ColumnAt(Vector2s zone, int edge, int t, out int wx, out int wz)
        {
            int plane = PlaneCoordinate(zone, edge);
            int along = AlongStart(zone, edge) + t;
            if (AlongX(edge)) { wx = along; wz = plane; }
            else { wx = plane; wz = along; }
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        // Every chunk that holds a sample of the border plane: the owner columns on both sides of it.
        // Any of them generating, queued or changing moves the signature, and a voxel wall is only built
        // from settled data. No chunks at all means the border is out of voxel range.
        private static ChunkState Probe(Vector2s zone, int edge, out uint signature)
        {
            signature = 2166136261u;
            int plane = PlaneCoordinate(zone, edge);
            int start = AlongStart(zone, edge);
            int pk = FloorDiv(plane, VoxelChunk.CELLS);
            int a0 = FloorDiv(start, VoxelChunk.CELLS), a1 = FloorDiv(start + ZoneCells, VoxelChunk.CELLS);
            bool any = false;
            unchecked
            {
                for (int p = pk - 1; p <= pk; p++)
                {
                    for (int a = a0; a <= a1; a++)
                    {
                        var col = AlongX(edge) ? new Vector2Int(a, p) : new Vector2Int(p, a);
                        if (!VoxelWorld.TryGetColumnKyRange(col, out int kyLo, out int kyHi)) continue;
                        for (int ky = kyLo; ky <= kyHi; ky++)
                        {
                            if (!VoxelWorld.TryGetChunk(new Vector3Int(col.x, ky, col.y), out var chunk) || chunk == null) continue;
                            if (chunk.Kind == ChunkKind.Full && (chunk.PendingGenerate || chunk.Queued)) return ChunkState.Pending;
                            any = true;
                            signature = (signature ^ (uint)((ky & 0xFFFF) | ((int)chunk.Kind << 16))) * 16777619u;
                            signature = (signature ^ (uint)chunk.Version) * 16777619u;
                        }
                    }
                }
            }
            return any ? ChunkState.Ready : ChunkState.Absent;
        }

        // The ground a voxel column of this border would stand on, from the generator: the heightmap
        // data (or the generator where none is loaded), location pads, then the river and bowl carves.
        private static float AnalyticGround(float wx, float wz)
        {
            float g;
            if (!Heightmap.GetHeight(new Vector3(wx + 0.013f, 0f, wz + 0.013f), out g))
                g = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(wx, wz) : FiresWaterfalls.SeaLevel;
            if (FiresLocationPads.TrySample(wx, wz, g, out float padY)) g = padY;
            if (FiresVoxelConfig.CarvesRiverBeds && VoxelHydrology.Ready && !FiresWorldGenBase.Active)
            {
                if (VoxelRiverNetwork.TryCarveAt(wx, wz, g, out float carved, out _)) g = carved;
                if (VoxelLakeBasins.TryCarveAt(wx, wz, g, out float bowl, out _)) g = bowl;
            }
            return g;
        }

        // The generated-water store's own test, for a column no chunk holds yet.
        private static bool AnalyticWater(float wx, float wz, float ground, out float level)
        {
            level = 0f;
            if (!GenWaterField.TryWaterSurfaceSmooth(wx, wz, out float surface, out float depth)) return false;
            if (!((depth >= 0.15f || ground < surface - 0.05f) && surface > FiresWaterfalls.SeaLevel + 0.3f)) return false;
            level = surface;
            return true;
        }

        private static int AnalyticDensity(float ground, int wy)
            => Mathf.Clamp(Mathf.RoundToInt((ground - wy) * VoxelChunk.QUANT), -127, 127);

        private static bool TryDensity(int wx, int wy, int wz, out sbyte density)
        {
            int kx = FloorDiv(wx, VoxelChunk.CELLS), ky = FloorDiv(wy, VoxelChunk.CELLS), kz = FloorDiv(wz, VoxelChunk.CELLS);
            bool faceX = wx - kx * VoxelChunk.CELLS == 0;
            bool faceY = wy - ky * VoxelChunk.CELLS == 0;
            bool faceZ = wz - kz * VoxelChunk.CELLS == 0;
            for (int i = 0; i < 8; i++)
            {
                int ox = i & 1, oy = (i >> 1) & 1, oz = (i >> 2) & 1;
                if ((ox == 1 && !faceX) || (oy == 1 && !faceY) || (oz == 1 && !faceZ)) continue;
                if (!VoxelWorld.TryGetChunk(new Vector3Int(kx - ox, ky - oy, kz - oz), out var chunk) || chunk == null) continue;
                if (chunk.Kind == ChunkKind.Full && chunk.PendingGenerate) continue;
                sbyte value = chunk.SampleDensityWorld(new Vector3(wx, wy, wz));
                if (value == sbyte.MinValue) continue;
                density = value;
                return true;
            }
            density = 0;
            return false;
        }

        // Surface nets round the land's rim down into the border cliff, so a wall topped at the plane's crossing stands
        // proud of the grass as a dark lip seen from the land. The column's topmost solid profile moves down by
        // WallTopDropMetres; above it the voxel cliff face, meshed just outside the wall, closes the border.
        private static void LowerTop(int[] field, int offset, int rows)
        {
            int top = rows - 1;
            while (top >= 0 && field[offset + top] <= 0) top--;
            if (top < 0) return;
            for (int j = Mathf.Max(0, top - WallTopDropMetres); j < rows; j++)
                field[offset + j] = j + WallTopDropMetres < rows ? field[offset + j + WallTopDropMetres] : AirDensity;
        }

        // Rock below the lowest chunk of the column, air above the highest one.
        private static bool TryColumnSpan(int wx, int wz, out int bottom, out int top)
        {
            bottom = int.MaxValue; top = int.MinValue;
            int kx = FloorDiv(wx, VoxelChunk.CELLS), kz = FloorDiv(wz, VoxelChunk.CELLS);
            bool faceX = wx - kx * VoxelChunk.CELLS == 0, faceZ = wz - kz * VoxelChunk.CELLS == 0;
            for (int ox = 0; ox <= (faceX ? 1 : 0); ox++)
            {
                for (int oz = 0; oz <= (faceZ ? 1 : 0); oz++)
                {
                    if (!VoxelWorld.TryGetColumnKyRange(new Vector2Int(kx - ox, kz - oz), out int kyLo, out int kyHi)) continue;
                    bottom = Mathf.Min(bottom, kyLo * VoxelChunk.CELLS);
                    top = Mathf.Max(top, kyHi * VoxelChunk.CELLS + VoxelChunk.CELLS);
                }
            }
            return top > bottom;
        }

        private static bool Build(Vector2s zone, int edge, uint signature, bool voxel)
        {
            int columns = ZoneCells + 1;
            var bottoms = new int[columns];
            var tops = new int[columns];
            var fromVoxel = new bool[columns];
            int yHi = int.MinValue;
            for (int t = 0; t < columns; t++)
            {
                ColumnAt(zone, edge, t, out int wx, out int wz);
                s_grounds[t] = AnalyticGround(wx, wz);
                fromVoxel[t] = voxel && TryColumnSpan(wx, wz, out bottoms[t], out tops[t]);
                int top = fromVoxel[t] ? tops[t] : Mathf.CeilToInt(s_grounds[t]) + GroundHeadroom;
                if (top > yHi) yHi = top;
            }
            int yLo = Mathf.FloorToInt(Mathf.Min(ZoneModeRegistry.SeaFloorY, 0f)) - BelowFloorMetres;
            if (yHi <= yLo) return false;

            int rows = yHi - yLo + 1;
            var field = new int[columns * rows];
            int caveCells = 0;
            for (int t = 0; t < columns; t++)
            {
                ColumnAt(zone, edge, t, out int wx, out int wz);
                for (int j = 0; j < rows; j++)
                {
                    int wy = yLo + j;
                    int value;
                    if (!fromVoxel[t])
                    {
                        field[t * rows + j] = AnalyticDensity(s_grounds[t] + FarLift, wy);
                        continue;
                    }
                    if (wy < bottoms[t]) value = 127;
                    else if (wy >= tops[t]) value = -127;
                    else if (TryDensity(wx, wy, wz, out sbyte d)) value = d;
                    else value = 127;
                    field[t * rows + j] = value - SolidBias;
                }
                if (fromVoxel[t]) LowerTop(field, t * rows, rows);
                bool solidAbove = false;
                for (int j = rows - 1; j >= 0; j--)
                {
                    if (field[t * rows + j] > 0) solidAbove = true;
                    else if (solidAbove) caveCells++;
                }
            }

            Vector3 outward = Outward(edge);
            int plane = PlaneCoordinate(zone, edge);
            int start = AlongStart(zone, edge);
            FillEdgeColours(zone, edge);

            s_verts.Clear(); s_norms.Clear(); s_tans.Clear(); s_uvs.Clear(); s_uv1.Clear(); s_cols.Clear(); s_tris.Clear();
            Vector3 m0 = MapPoint(edge, plane, start, outward, Vector2.zero);
            Vector3 m1 = MapPoint(edge, plane, start, outward, new Vector2(1f, 0f));
            Vector3 m2 = MapPoint(edge, plane, start, outward, new Vector2(1f, 1f));
            s_flip = Vector3.Dot(Vector3.Cross(m1 - m0, m2 - m0), outward) < 0f;
            Vector3 axis = (outward.x < 0.99f && outward.x > -0.99f) ? Vector3.right : Vector3.forward;
            Vector3 tangent = (axis - outward * Vector3.Dot(outward, axis)).normalized;
            var tan4 = new Vector4(tangent.x, tangent.y, tangent.z, -1f);

            for (int t = 0; t < ZoneCells; t++)
            {
                int runStart = -1;
                for (int j = 0; j < rows - 1; j++)
                {
                    int a = field[t * rows + j], b = field[(t + 1) * rows + j];
                    int c = field[(t + 1) * rows + j + 1], e = field[t * rows + j + 1];
                    bool full = a > 0 && b > 0 && c > 0 && e > 0;
                    if (full)
                    {
                        if (runStart < 0) runStart = j;
                        continue;
                    }
                    if (runStart >= 0)
                    {
                        EmitQuad(zone, edge, plane, start, outward, tan4, t, yLo + runStart, yLo + j);
                        runStart = -1;
                    }
                    if (a <= 0 && b <= 0 && c <= 0 && e <= 0) continue;
                    EmitCell(zone, edge, plane, start, outward, tan4, t, yLo + j, a, b, c, e);
                }
                if (runStart >= 0) EmitQuad(zone, edge, plane, start, outward, tan4, t, yLo + runStart, yLo + rows - 1);
            }

            if (s_tris.Count == 0)
            {
                if (s_walls.TryGetValue((zone, edge), out var empty)) { DestroyWall(empty); s_walls.Remove((zone, edge)); }
                return true;
            }

            if (!s_walls.TryGetValue((zone, edge), out var wall))
            {
                wall = Create(zone, edge);
                s_walls[(zone, edge)] = wall;
            }
            wall.Mesh.Clear(false);
            wall.Mesh.SetVertices(s_verts);
            wall.Mesh.SetNormals(s_norms);
            wall.Mesh.SetTangents(s_tans);
            wall.Mesh.SetUVs(0, s_uvs);
            wall.Mesh.SetUVs(1, s_uv1);
            wall.Mesh.SetColors(s_cols);
            wall.Mesh.SetTriangles(s_tris, 0, calculateBounds: true);
            wall.Collider.sharedMesh = null;
            if (voxel) wall.Collider.sharedMesh = wall.Mesh;
            var renderer = wall.Go.GetComponent<MeshRenderer>();
            var material = TerrainMaterialCache.GetCurrentMaterial();
            if (renderer.sharedMaterial != material) renderer.sharedMaterial = material;
            wall.Signature = signature;
            wall.Voxel = voxel;
            BuildFalls(zone, edge, wall, fromVoxel);
            BuiltTotal++;
            CaveCellsCut += caveCells;
            return true;
        }

        // Water at the border that flows outward (no higher than the column one step inside, so a river's
        // upstream end never spills backwards) pours off the edge: one curtain per wet run, from the lip where
        // the river sheet ends down the cliff into the sea.
        private static void BuildFalls(Vector2s zone, int edge, Wall wall, bool[] fromVoxel)
        {
            var material = FiresWaterfalls.Material;
            Vector3 outward = Outward(edge);
            int plane = PlaneCoordinate(zone, edge);
            int start = AlongStart(zone, edge);
            int stepX = Mathf.RoundToInt(outward.x), stepZ = Mathf.RoundToInt(outward.z);
            for (int t = 0; t <= ZoneCells; t++)
            {
                s_outflow[t] = false;
                ColumnAt(zone, edge, t, out int wx, out int wz);
                int ix = wx - stepX, iz = wz - stepZ;
                float level, inner;
                bool wet, innerWet;
                if (fromVoxel[t])
                {
                    wet = TryWaterLevel(wx, wz, out level);
                    innerWet = TryWaterLevel(ix, iz, out inner);
                }
                else
                {
                    wet = AnalyticWater(wx, wz, s_grounds[t], out level);
                    innerWet = AnalyticWater(ix, iz, AnalyticGround(ix, iz), out inner);
                }
                if (!wet || level <= FiresWaterfalls.SeaLevel + 0.3f) continue;
                if (innerWet && level > inner + 0.05f) continue;
                s_levels[t] = level;
                s_outflow[t] = true;
            }

            FiresWaterfalls.BeginMesh();
            s_fallSites.Clear();
            s_mistSites.Clear();
            int runs = 0;
            if (material != null)
            {
                for (int t = 0; t <= ZoneCells; t++)
                {
                    if (!s_outflow[t]) continue;
                    int end = t;
                    while (end + 1 <= ZoneCells && s_outflow[end + 1]) end++;
                    s_fallColumns.Clear();
                    AddFallColumn(edge, plane, start, t - 0.5f, s_levels[t]);
                    for (int k = t; k <= end; k++) AddFallColumn(edge, plane, start, k, s_levels[k]);
                    AddFallColumn(edge, plane, start, end + 0.5f, s_levels[end]);
                    if (FiresWaterfalls.AppendCurtain(s_fallColumns, outward, FiresWaterfalls.EdgeLipOut, 0f, FallSeed(zone, edge, t)))
                        s_mistSites.Add(FiresWaterfalls.LastSite);
                    s_fallSites.Add(FiresWaterfalls.SoundSite(s_fallColumns, outward));
                    runs++;
                    t = end;
                }
            }

            if (runs == 0 && wall.FallsGo == null)
            {
                wall.FallRuns = 0;
                return;
            }
            if (wall.FallsGo == null)
            {
                wall.FallsGo = new GameObject("Falls");
                wall.FallsGo.layer = 0;
                wall.FallsGo.transform.SetParent(wall.Go.transform, false);
                wall.FallsMesh = new Mesh { name = wall.Go.name + "_Falls" };
                wall.FallsGo.AddComponent<MeshFilter>().sharedMesh = wall.FallsMesh;
                var renderer = wall.FallsGo.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.sharedMaterials = FiresWaterfalls.Materials;
                VoxelRenderGroup.Apply(renderer);
            }
            int vertices = FiresWaterfalls.EndMesh(wall.FallsMesh);
            wall.FallsGo.SetActive(vertices > 0);
            FiresWaterfalls.SetSounds(wall.FallsGo.transform, s_fallSites);
            FiresWaterfallMist.SetEmitters(wall.FallsGo.transform, s_mistSites);
            wall.FallRuns = runs;
        }

        private static void AddFallColumn(int edge, int plane, int start, float along, float lipY)
        {
            float a = start + along;
            s_fallColumns.Add(new FiresWaterfalls.Column
            {
                Base = AlongX(edge) ? new Vector3(a, 0f, plane) : new Vector3(plane, 0f, a),
                LipY = lipY,
                FallLength = lipY - (FiresWaterfalls.SeaLevel - FiresWaterfalls.PlungeDepth),
            });
        }

        private static float FallSeed(Vector2s zone, int edge, int t)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)zone.x) * 16777619u;
                h = (h ^ (uint)zone.y) * 16777619u;
                h = (h ^ (uint)edge) * 16777619u;
                h = (h ^ (uint)t) * 16777619u;
                return (h & 0xFFFF) / 6553.6f;
            }
        }

        // The column's stored generated-water level, from any generated chunk that holds it.
        private static bool TryWaterLevel(int wx, int wz, out float level)
        {
            level = 0f;
            int kx = FloorDiv(wx, VoxelChunk.CELLS), kz = FloorDiv(wz, VoxelChunk.CELLS);
            bool faceX = wx - kx * VoxelChunk.CELLS == 0, faceZ = wz - kz * VoxelChunk.CELLS == 0;
            for (int ox = 0; ox <= (faceX ? 1 : 0); ox++)
            {
                for (int oz = 0; oz <= (faceZ ? 1 : 0); oz++)
                {
                    var col = new Vector2Int(kx - ox, kz - oz);
                    if (!VoxelWorld.TryGetColumnKyRange(col, out int kyLo, out int kyHi)) continue;
                    for (int ky = kyHi; ky >= kyLo; ky--)
                    {
                        if (!VoxelWorld.TryGetChunk(new Vector3Int(col.x, ky, col.y), out var chunk) || chunk == null) continue;
                        if (chunk.Kind != ChunkKind.Full || chunk.PendingGenerate) continue;
                        level = chunk.GenWaterAtLocal(wx - col.x * VoxelChunk.CELLS, wz - col.y * VoxelChunk.CELLS);
                        return level > 0f;
                    }
                }
            }
            return false;
        }

        private static Wall Create(Vector2s zone, int edge)
        {
            if (s_root == null) s_root = new GameObject("FiresZoneBorderWalls");
            var go = new GameObject($"ZoneWall_{zone.x}_{zone.y}_{"WESN"[edge]}");
            go.transform.SetParent(s_root.transform, false);
            int terrainLayer = LayerMask.NameToLayer("terrain");
            if (terrainLayer >= 0) go.layer = terrainLayer;
            var mesh = new Mesh { name = go.name, indexFormat = IndexFormat.UInt32 };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = TerrainMaterialCache.GetCurrentMaterial();
            VoxelChunkView.ApplyShadowMode(renderer);
            VoxelRenderGroup.Apply(renderer);
            var collider = go.AddComponent<MeshCollider>();
            collider.convex = false;
            return new Wall { Go = go, Mesh = mesh, Collider = collider };
        }

        private static void Destroy((Vector2s zone, int edge) key)
        {
            if (!s_walls.TryGetValue(key, out var wall)) return;
            DestroyWall(wall);
            s_walls.Remove(key);
        }

        private static void DestroyWall(Wall wall)
        {
            if (wall == null) return;
            if (wall.Mesh != null) Object.Destroy(wall.Mesh);
            if (wall.FallsMesh != null) Object.Destroy(wall.FallsMesh);
            if (wall.Go != null) Object.Destroy(wall.Go);
        }

        internal static void RefreshVisibility()
        {
            foreach (var kv in s_walls)
            {
                if (kv.Value.Go == null) continue;
                foreach (var renderer in kv.Value.Go.GetComponentsInChildren<MeshRenderer>(true))
                    VoxelRenderGroup.Apply(renderer);
            }
            foreach (var kv in s_tops)
                if (kv.Value.Go != null) VoxelRenderGroup.Apply(kv.Value.Go.GetComponent<MeshRenderer>());
        }

        private static void EmitQuad(Vector2s zone, int edge, int plane, int start, Vector3 outward, Vector4 tan4,
            int t, int y0, int y1)
        {
            s_poly[0] = new Vector2(t, y0);
            s_poly[1] = new Vector2(t + 1, y0);
            s_poly[2] = new Vector2(t + 1, y1);
            s_poly[3] = new Vector2(t, y1);
            EmitPolygon(edge, plane, start, outward, tan4, 4);
        }

        // Marching squares over one cell of the border-plane density: the solid part of the cell,
        // corners walked a→b→c→e with the zero crossing on every edge that changes sign.
        private static void EmitCell(Vector2s zone, int edge, int plane, int start, Vector3 outward, Vector4 tan4,
            int t, int y, int a, int b, int c, int e)
        {
            int n = 0;
            s_corners[0] = new Vector2(t, y); s_corners[1] = new Vector2(t + 1, y);
            s_corners[2] = new Vector2(t + 1, y + 1); s_corners[3] = new Vector2(t, y + 1);
            s_values[0] = a; s_values[1] = b; s_values[2] = c; s_values[3] = e;
            for (int i = 0; i < 4; i++)
            {
                int k = (i + 1) & 3;
                int vi = s_values[i], vk = s_values[k];
                if (vi > 0) s_poly[n++] = s_corners[i];
                if ((vi > 0) != (vk > 0))
                {
                    float f = (float)vi / (vi - vk);
                    s_poly[n++] = Vector2.Lerp(s_corners[i], s_corners[k], f);
                }
            }
            if (n >= 3) EmitPolygon(edge, plane, start, outward, tan4, n);
        }

        private static Vector3 MapPoint(int edge, int plane, int start, Vector3 outward, Vector2 p)
        {
            float planeOffset = plane - (outward.x + outward.z) * WallInset;
            float along = start + p.x;
            return AlongX(edge) ? new Vector3(along, p.y, planeOffset) : new Vector3(planeOffset, p.y, along);
        }

        // Polygons arrive counter-clockwise in (along, y); the per-edge flip turns them to face the
        // closed side.
        private static void EmitPolygon(int edge, int plane, int start, Vector3 outward, Vector4 tan4, int count)
        {
            int baseIndex = s_verts.Count;
            for (int i = 0; i < count; i++)
            {
                var p = s_poly[i];
                s_verts.Add(MapPoint(edge, plane, start, outward, p));
                s_norms.Add(outward);
                s_tans.Add(tan4);
                s_uvs.Add(new Vector2(p.x / ZoneCells, p.y / VoxelChunk.CELLS));
                s_uv1.Add(Vector2.zero);
                s_cols.Add(s_edgeColors[ColumnIndex(p.x)]);
            }
            for (int i = 1; i < count - 1; i++)
            {
                s_tris.Add(baseIndex);
                if (s_flip) { s_tris.Add(baseIndex + i + 1); s_tris.Add(baseIndex + i); }
                else { s_tris.Add(baseIndex + i); s_tris.Add(baseIndex + i + 1); }
            }
        }
    }
}
