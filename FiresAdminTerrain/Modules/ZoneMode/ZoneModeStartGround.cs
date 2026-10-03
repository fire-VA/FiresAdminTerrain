using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // Where FAT's own start objects may stand (the starter Chunk Generator, the first Chunk Hammer's chest): natural
    // ground nobody has painted (no path, field or paved road, a player's or a location's), clear of everything built.
    // The paint is the zone heightmap's mask, the one the voxel chunks copy their ground from; the zone's terrain
    // compiler's own paint counts the moment it loads, before the heightmap rebuilds from it. Ground whose compiler is
    // saved in the zone but not loaded yet is not known, and nothing is placed on it until it is.
    internal static class ZoneModeStartGround
    {
        internal enum Ground { Natural, Painted, Built, Occupied, Unknown }

        private const float PaintLimit = 0.25f;
        private const float PaintMargin = 1f;
        private const float PaintStep = 1f;
        private const float CellSize = 8f;
        private const string CompilerPrefabName = "_TerrainCompiler";
        private const float WetDepth = 0.05f;
        private const float WaterAboveGround = 0.5f;

        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> CompilerPaint =
            AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> CompilerPainted =
            AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");

        private static readonly Dictionary<long, List<Vector3>> s_builds = new Dictionary<long, List<Vector3>>();
        private static readonly Dictionary<long, List<Vector3>> s_objects = new Dictionary<long, List<Vector3>>();
        private static readonly Dictionary<int, bool> s_obstacleByPrefab = new Dictionary<int, bool>();
        private static readonly HashSet<Vector2s> s_compilerZones = new HashSet<Vector2s>();
        private static readonly List<ZDO> s_found = new List<ZDO>();
        private static int s_compilerPrefab;

        // Reads what stands within `zones` zones of the centre, for Check. Creatures, dropped items and FAT's own start
        // objects stand in nothing's way.
        internal static void Survey(Vector3 centre, int zones)
        {
            s_builds.Clear();
            s_objects.Clear();
            s_compilerZones.Clear();
            s_found.Clear();
            if (s_compilerPrefab == 0) s_compilerPrefab = CompilerPrefabHash();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(centre), new SimulationDistance(zones, 0), s_found);
            foreach (var zdo in s_found)
            {
                if (zdo == null) continue;
                int prefab = zdo.GetPrefab();
                if (prefab == s_compilerPrefab)
                {
                    s_compilerZones.Add(zdo.GetSector());
                    continue;
                }
                if (ZoneModeChunkMachines.IsProtected(zdo)) continue;
                if (ZoneModeBuilds.IsPlaced(zdo)) Add(s_builds, zdo.GetPosition());
                else if (IsObstacle(prefab)) Add(s_objects, zdo.GetPosition());
            }
            s_found.Clear();
        }

        // Natural when nothing built stands within `buildClearance` of the footprint's edge, nothing else within
        // `objectClearance` of its centre, and no point of the footprint, or PaintMargin beyond it, is painted.
        internal static Ground Check(Vector3 centre, float footprint, float buildClearance, float objectClearance)
        {
            if (Near(s_builds, centre, footprint + buildClearance)) return Ground.Built;
            if (Near(s_objects, centre, objectClearance)) return Ground.Occupied;
            float radius = footprint + PaintMargin;
            for (float dz = -radius; dz <= radius; dz += PaintStep)
            {
                for (float dx = -radius; dx <= radius; dx += PaintStep)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    float paint = PaintAt(new Vector3(centre.x + dx, 0f, centre.z + dz), out bool known);
                    if (!known) return Ground.Unknown;
                    if (paint > PaintLimit) return Ground.Painted;
                }
            }
            return Ground.Natural;
        }

        // Generated water (a river, lake or pond the world planned) standing over the spot. Ask
        // GenWaterField.EnsureWaterKnown first: until the water is known this answers dry.
        internal static bool UnderGeneratedWater(Vector3 spot)
            => Voxel.GenWaterField.TryWaterSurfaceSmooth(spot.x, spot.z, out float level, out float depth)
               && depth > WetDepth && level > spot.y - WaterAboveGround;

        // The strongest of dirt, cultivation and paving at the point.
        private static float PaintAt(Vector3 point, out bool known)
        {
            known = false;
            var heightmap = Heightmap.FindHeightmap(point);
            if (heightmap == null || heightmap.GetPaintMask() == null) return 0f;
            Vector3 zoneCentre = heightmap.transform.position;
            var compiler = TerrainComp.FindTerrainCompiler(zoneCentre);
            if (compiler == null && s_compilerZones.Contains(ZoneSystem.GetZone(zoneCentre))) return 0f;
            known = true;
            heightmap.WorldToVertexMask(point, out int x, out int y);
            float paint = Strength(heightmap.GetPaintMask(x, y));
            if (compiler == null) return paint;
            var painted = CompilerPainted(compiler);
            var colors = CompilerPaint(compiler);
            int pitch = heightmap.m_width + 1;
            if (painted == null || colors == null || x < 0 || y < 0 || x >= pitch || y >= pitch) return paint;
            int index = y * pitch + x;
            if (index >= painted.Length || index >= colors.Length || !painted[index]) return paint;
            return Mathf.Max(paint, Strength(colors[index]));
        }

        private static float Strength(Color paint) => Mathf.Max(paint.r, Mathf.Max(paint.g, paint.b));

        private static bool IsObstacle(int prefabHash)
        {
            if (s_obstacleByPrefab.TryGetValue(prefabHash, out bool obstacle)) return obstacle;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            if (prefab == null) return false;
            obstacle = prefab.GetComponent<Character>() == null && prefab.GetComponent<ItemDrop>() == null;
            s_obstacleByPrefab[prefabHash] = obstacle;
            return obstacle;
        }

        private static int CompilerPrefabHash()
        {
            var zonePrefab = ZoneSystem.instance != null ? ZoneSystem.instance.m_zonePrefab : null;
            var heightmap = zonePrefab != null ? zonePrefab.GetComponentInChildren<Heightmap>(true) : null;
            string name = heightmap != null && heightmap.m_terrainCompilerPrefab != null
                ? heightmap.m_terrainCompilerPrefab.name : CompilerPrefabName;
            return name.GetStableHashCode();
        }

        private static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;

        private static void Add(Dictionary<long, List<Vector3>> grid, Vector3 at)
        {
            long key = CellKey(Mathf.FloorToInt(at.x / CellSize), Mathf.FloorToInt(at.z / CellSize));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<Vector3>(4);
            list.Add(at);
        }

        private static bool Near(Dictionary<long, List<Vector3>> grid, Vector3 centre, float radius)
        {
            float radius2 = radius * radius;
            int x0 = Mathf.FloorToInt((centre.x - radius) / CellSize), x1 = Mathf.FloorToInt((centre.x + radius) / CellSize);
            int z0 = Mathf.FloorToInt((centre.z - radius) / CellSize), z1 = Mathf.FloorToInt((centre.z + radius) / CellSize);
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!grid.TryGetValue(CellKey(x, z), out var list)) continue;
                    foreach (var at in list)
                    {
                        float dx = at.x - centre.x, dz = at.z - centre.z;
                        if (dx * dx + dz * dz < radius2) return true;
                    }
                }
            }
            return false;
        }
    }
}
