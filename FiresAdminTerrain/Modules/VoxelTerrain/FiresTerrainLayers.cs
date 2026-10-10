using System.Globalization;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ONE LAYER RESOLVER for every terrain, water and paint tool (FAT 0.2.756; Fire 10-06: "all the tools should only affect
    // the layer they are raycast on ... if it's on a cave floor it levels the cave floor, if it's on the terrain above it
    // levels that section of mesh without filling the cave below, if it's on a skyland it only affects that layer ... clean
    // and easy"). A tool resolves the point it aims at (its raycast hit) and keeps every edit inside that layer's band:
    //   Island(id)  that skyland's body: from IslandUndersideKeep over its underside up (the open sky over it). The ground
    //               below and every other island are another layer.
    //   Cave        there is a roof over the point (the first solid stepping up, under the column's ground top): from the
    //               world's bottom (a cave floor may be dug into) to RoofKeep under that roof.
    //   Ground      everything else: up to IslandGap under any island over it; down to RoofKeep over the ceiling of a cave
    //               under it (found within CaveSearch of the surface), so nothing laid on the surface fills into the cave and
    //               no cut breaks into it. With no cave under it the band goes to the world's bottom.
    // Every column is resolved on its own (a cave column under a mouth's open sky is Ground), so a tool working a circle
    // checks SameLayer per column and leaves the others alone. The V-tools, IH/WEC ([generator]), the water tools ([seasons])
    // and the biome paint all use this. Main thread (it reads the live voxel density).
    internal enum TerrainLayerKind { Ground, Cave, Island }

    internal struct TerrainLayer
    {
        public TerrainLayerKind Kind;
        public uint IslandId;      // Island only
        public Vector3 Hit;
        public float Surface;      // the solid-to-air crossing nearest the hit (the hit's own y when none is loaded)
        public float Floor;        // the lowest y an edit of this layer may touch
        public float Ceiling;      // the highest y

        public bool Holds(float y) => y >= Floor && y <= Ceiling;
        public float Clamp(float y) => Mathf.Clamp(y, Floor, Ceiling);

        // Clips [bottom, top] to the band; false when nothing of it is left.
        public bool ClampSpan(ref float bottom, ref float top)
        {
            bottom = Mathf.Max(bottom, Floor);
            top = Mathf.Min(top, Ceiling);
            return top > bottom;
        }

        public string Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            switch (Kind)
            {
                case TerrainLayerKind.Island:
                    return string.Format(inv, "island {0} (edits {1:0.0} and up)", IslandId, Floor);
                case TerrainLayerKind.Cave:
                    return string.Format(inv, "cave (edits up to {0:0.0}, {1:0.0} under its roof)", Ceiling, FiresTerrainLayers.RoofKeep);
                default:
                    return float.IsNegativeInfinity(Floor) || Floor <= FiresTerrainLayers.WorldBottom
                        ? (Ceiling >= FiresTerrainLayers.WorldTop ? "ground" : string.Format(inv, "ground (edits up to {0:0.0}, under an island)", Ceiling))
                        : string.Format(inv, "ground over a cave (edits down to {0:0.0}{1})", Floor,
                            Ceiling >= FiresTerrainLayers.WorldTop ? "" : string.Format(inv, ", up to {0:0.0}", Ceiling));
            }
        }
    }

    internal static class FiresTerrainLayers
    {
        internal const float RoofKeep = 1.5f;              // rock left under a cave's roof, and over a cave's ceiling
        internal const float IslandUndersideKeep = 1.5f;   // an island edit stays this far over its underside
        internal const float IslandGap = 2f;               // ground edits stay this far under an island over them
        internal const float WorldBottom = -500f, WorldTop = 2000f;
        private const float IslandMargin = 2f, SurfaceReach = 12f, CaveSearch = 48f, Step = 0.5f, GroundMargin = 1f;

        // The layer of the point a tool aims at.
        internal static TerrainLayer Resolve(Vector3 hit)
        {
            var layer = new TerrainLayer { Hit = hit, Surface = hit.y, Floor = WorldBottom, Ceiling = WorldTop };
            if (VoxelWorld.TryFindSurfaceY(hit.x, hit.z, hit.y, SurfaceReach, out float surface)) layer.Surface = surface;

            if (FiresSkyIslands.TryIslandAt(hit, IslandMargin, out uint id, out float bottom, out _))
            {
                layer.Kind = TerrainLayerKind.Island;
                layer.IslandId = id;
                layer.Floor = bottom + IslandUndersideKeep;
                return layer;
            }
            if (TryRoofAbove(hit, out float roof))
            {
                layer.Kind = TerrainLayerKind.Cave;
                layer.Ceiling = roof - RoofKeep;
                return layer;
            }
            layer.Kind = TerrainLayerKind.Ground;
            if (TryCaveCeilingBelow(new Vector3(hit.x, layer.Surface, hit.z), out float ceiling)) layer.Floor = ceiling + RoofKeep;
            if (FiresSkyIslands.TryBottomAt(hit.x, hit.z, out float islandBottom) && islandBottom > layer.Surface)
                layer.Ceiling = islandBottom - IslandGap;
            return layer;
        }

        // The crosshair's layer (ZoneModeAreaTool.TryGetAim: land, rock or a build, else the sea plane).
        internal static bool TryAim(out TerrainLayer layer)
        {
            layer = default;
            if (!Zones.ZoneModeAreaTool.TryGetAim(out var hit)) return false;
            layer = Resolve(hit);
            return true;
        }

        internal static bool SameLayer(in TerrainLayer a, in TerrainLayer b)
            => a.Kind == b.Kind && (a.Kind != TerrainLayerKind.Island || a.IslandId == b.IslandId);

        // The same layer in another column: its surface nearest `at`'s, resolved on its own. False when the column has no
        // surface within reach of it or that surface belongs to another layer (leave the column alone).
        internal static bool TryResolveColumn(float x, float z, in TerrainLayer at, out TerrainLayer col)
        {
            col = default;
            if (!VoxelWorld.TryFindSurfaceY(x, z, at.Surface, SurfaceReach, out float surface)) return false;
            col = Resolve(new Vector3(x, surface, z));
            return SameLayer(in col, in at);
        }

        // The roof over a point in a cave: the first solid voxel stepping up from it, under the column's ground top (open
        // sky, a mouth under an overhang or an island overhead is no roof).
        internal static bool TryRoofAbove(Vector3 p, out float roof)
        {
            roof = float.NaN;
            if (!VoxelZoneStreamer.TryGroundHeight(p.x, p.z, out float ground) || p.y > ground - GroundMargin) return false;
            for (float y = p.y + Step; y < ground - GroundMargin; y += Step)
            {
                if (!VoxelWorld.TryIsSolid(p.x, y, p.z, out bool solid)) return false;
                if (!solid) continue;
                roof = y;
                return true;
            }
            return false;
        }

        // The ceiling of a cave under a ground surface: stepping down from just under it, the first air found after solid,
        // within CaveSearch. False when the column is solid all the way (or not loaded).
        internal static bool TryCaveCeilingBelow(Vector3 surface, out float ceiling)
        {
            ceiling = float.NaN;
            bool inRock = false;
            for (float y = surface.y - Step; y > surface.y - CaveSearch; y -= Step)
            {
                if (!VoxelWorld.TryIsSolid(surface.x, y, surface.z, out bool solid)) return false;
                if (solid) { inRock = true; continue; }
                if (!inRock) continue;
                ceiling = y + Step;
                return true;
            }
            return false;
        }
    }
}
