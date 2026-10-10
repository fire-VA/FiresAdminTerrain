using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Per-tile runtime data holder. One instance per loaded tile.
    //
    // Spawned by TileAnchor.LoadFromZdo, parented under the DirtFloorMeshCombiner
    // that welds this tile + its neighbors into one mesh. Pure data + helpers — no
    // Update loop, no ZDO traffic (that's the anchor's job). When the data mutates
    // (op applied, ZDO replicated), TileAnchor calls back into NotifyMutated to
    // bump LastSeenDataRevision and trigger combiner re-weld.
    //
    // Tile coordinate system:
    //   - TileXZ is the world-aligned 64m index (Mathf.FloorToInt(worldX / 64))
    //   - Origin (vertex 0,0) = (TileXZ.x * 64, _, TileXZ.y * 64)
    //   - Vertex (i, j) world position = origin + (i, height, j)   [1m vertex spacing]
    //   - Border vertices live at i=0 and i=64 (and j=0 / j=64) — they coincide
    //     with the neighbor tiles' opposite-edge vertices, so weld dedup is exact.
    [DisallowMultipleComponent]
    public sealed class TerrainTile : MonoBehaviour
    {
        // Side length in world units. Snap value for tile alignment.
        public const float TILE_SIZE = 64f;

        // Per-vertex spacing. One vertex per meter, matching vanilla heightmap.
        public const float VERTEX_SPACING = 1f;

        public Vector2i TileXZ { get; private set; }

        public float[] Heights { get; private set; }
        public bool[] HeightModified { get; private set; }
        public Color32[] Paint { get; private set; }
        public bool[] PaintModified { get; private set; }
        public List<DiskRecord> Disks { get; private set; }

        // Per-tile biome override (legacy v2 — kept for any code that wants
        // a quick "what's the dominant biome" answer + for codec v2→v3
        // upgrade where the tile-wide value gets promoted to per-vertex).
        // 0 = no override (vanilla biome wins). New code should write
        // per-vertex via Biome[]/BiomeModified[].
        public ushort BiomeOverride { get; set; }

        // Per-vertex biome override (codec v3+). Same length as Heights/Paint.
        // 0 entries fall back to vanilla biome at query time. Letting biome
        // be per-vertex (instead of per-tile) means a small brush only paints
        // biome inside the circle, not the whole 64m tile — and is the
        // foundation for cross-tile blending later.
        public ushort[] Biome { get; private set; }
        public bool[] BiomeModified { get; private set; }

        // 2026-05-26 round-30: per-tile FloatingIslandMode flag (codec v4).
        // false (default) → existing "stretch underside down to terrain"
        //   behavior. The underside mirrors the top mesh and reaches all
        //   the way down to vanilla terrain, no matter how high the top
        //   sits.
        // true → "auto-round when far above terrain". When the tile's top
        //   sits ≥FLOATING_ISLAND_TRIGGER_HEIGHT (20m) above local terrain,
        //   the underside generates a domed teardrop bottom instead of
        //   stretching down. Below that height, stretches to terrain
        //   like default mode. Tools choose mode at placement time —
        //   "Floor" / "Raise" → false; "Floor (Floating Island)" → true.
        public bool FloatingIslandMode { get; set; }

        // ZDO DataRevision the local grids were decoded from. TileAnchor.CheckLoad
        // compares against the current ZDO revision; mismatch = remote peer wrote,
        // re-decode.
        public uint LastSeenDataRevision { get; internal set; }

        // ── Cheap "any custom terrain loaded?" gate ───────────────────────────
        // ClutterSystem.GetGroundInfo fires one raycast per grass candidate —
        // thousands per 8m patch — and FAT prefixes that method. When no custom
        // tile is loaded, the prefix must NOT pay for a GetComponentInParent<
        // TerrainTile> hierarchy walk on every one of those hits. Count ACTIVE
        // instances (matching GetComponentInParent's active-only lookup) so the
        // hot path can short-circuit the walk with a single int compare.
        private static int s_activeCount;
        public static bool AnyActive => s_activeCount > 0;

        private void OnEnable() { s_activeCount++; }
        private void OnDisable() { if (s_activeCount > 0) s_activeCount--; }

        // Initialize from decoded TileData. Called once on tile spawn.
        // Safe to call again with fresh data on remote-driven re-decode.
        public void Initialize(TileData data, uint dataRevision)
        {
            TileXZ = data.TileXZ;
            Heights = data.Heights;
            HeightModified = data.HeightModified;
            Paint = data.Paint;
            PaintModified = data.PaintModified;
            Disks = data.Disks ?? new List<DiskRecord>();
            BiomeOverride = data.BiomeOverride;
            Biome = data.Biome ?? new ushort[TerrainTileCodec.VERTEX_COUNT];
            BiomeModified = data.BiomeModified ?? new bool[TerrainTileCodec.VERTEX_COUNT];
            FloatingIslandMode = data.FloatingIslandMode;
            // v2→v3 upgrade — promote tile-wide BiomeOverride to per-vertex
            // by writing it into every vertex. Old saves load the same look,
            // new brushes write per-vertex from here forward.
            if (BiomeOverride != 0 && !HasAnyBiomeModified())
            {
                for (int i = 0; i < Biome.Length; i++)
                {
                    Biome[i] = BiomeOverride;
                    BiomeModified[i] = true;
                }
            }
            LastSeenDataRevision = dataRevision;

            // Position the GameObject at the tile's world center for editor/debug
            // visibility. Combiner mesh is parented under this object so the
            // transform matters only as an anchor point for child meshes.
            transform.position = WorldCenter;

            EnsureMeshRenderer();
            NotifyMutated();
        }

        // Initialize a tile with no existing data (fresh edit). All grids empty,
        // no disks. Used when an op needs a tile that hasn't been loaded yet.
        public void InitializeEmpty(Vector2i tileXZ)
        {
            TileXZ = tileXZ;
            Heights = new float[TerrainTileCodec.VERTEX_COUNT];
            HeightModified = new bool[TerrainTileCodec.VERTEX_COUNT];
            Paint = new Color32[TerrainTileCodec.VERTEX_COUNT];
            PaintModified = new bool[TerrainTileCodec.VERTEX_COUNT];
            Biome = new ushort[TerrainTileCodec.VERTEX_COUNT];
            BiomeModified = new bool[TerrainTileCodec.VERTEX_COUNT];
            Disks = new List<DiskRecord>();
            LastSeenDataRevision = 0;
            transform.position = WorldCenter;

            EnsureMeshRenderer();
        }

        // Trigger a mesh rebuild after the tile's grids/disks have been mutated.
        // Callers: TileAnchor (after LoadFromZdo or ApplyOpAndSave); any in-tile
        // op API. Cheap when called repeatedly — TileMeshRenderer reuses buffers.
        public void NotifyMutated()
        {
            // The 2D tile layer is retired: a loaded tile draws nothing and has no collider (it only waits to be purged).
            if (TileAnchor.Retired) return;
            if (_meshRenderer == null) _meshRenderer = GetComponent<TileMeshRenderer>();
            if (_meshRenderer == null)
            {
                FiresLogger.LogWarning($"[TerrainTile] tile=({TileXZ.x},{TileXZ.y}) NotifyMutated: no TileMeshRenderer attached");
                return;
            }
            FiresLogger.LogVerbose($"[TerrainTile] tile=({TileXZ.x},{TileXZ.y}) NotifyMutated → rebuilding mesh");
            _meshRenderer.RebuildMesh();

            // 2026-05-26 round-30: re-clamp clutter onto the new mesh.
            // Vanilla ClutterSystem.ResetGrass re-rolls grass placement at
            // the CURRENT terrain height; calling it after we sculpt
            // ensures grass blades / flowers / clutter don't end up
            // floating in the air over our new sculpt or buried under
            // a raised hill. Radius covers the whole tile footprint plus
            // a small margin so cross-tile clutter near the seam also
            // refreshes.
            try
            {
                Vector3 worldCenter = WorldCenter;
                const float CLUTTER_REFRESH_MARGIN = 4f;
                float radius = TILE_SIZE * 0.5f + CLUTTER_REFRESH_MARGIN;
                VerdantsAscent.Terrain.Clutter.ClutterManager.RespawnInRadius(worldCenter, radius);
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogVerbose($"[TerrainTile] clutter refresh threw: {ex.Message}");
            }
        }

        private TileMeshRenderer _meshRenderer;

        private void EnsureMeshRenderer()
        {
            if (TileAnchor.Retired) return;   // no mesh, no collider: nothing floats over the ground (Fire, 2026-09-29)
            if (_meshRenderer != null) return;
            _meshRenderer = GetComponent<TileMeshRenderer>();
            if (_meshRenderer == null)
                _meshRenderer = gameObject.AddComponent<TileMeshRenderer>();
        }

        // Export current state for encoding. Returns the live arrays — caller
        // must not mutate (codec only reads).
        public TileData Snapshot()
        {
            return new TileData
            {
                TileXZ = TileXZ,
                Heights = Heights,
                HeightModified = HeightModified,
                Paint = Paint,
                PaintModified = PaintModified,
                Disks = Disks,
                BiomeOverride = BiomeOverride,
                Biome = Biome,
                BiomeModified = BiomeModified,
                FloatingIslandMode = FloatingIslandMode,
            };
        }

        private bool HasAnyBiomeModified()
        {
            if (BiomeModified == null) return false;
            for (int i = 0; i < BiomeModified.Length; i++)
                if (BiomeModified[i]) return true;
            return false;
        }

        // Sample the biome override at a world position. Returns 0 if no
        // override at the nearest vertex. Used by TileBiomeOverride patch.
        public ushort SampleBiomeAtWorld(Vector3 worldPos)
        {
            if (Biome == null || BiomeModified == null) return 0;
            if (!TryWorldToVertex(worldPos, out int i, out int j)) return 0;
            int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;
            if (!BiomeModified[idx]) return 0;
            return Biome[idx];
        }

        // Set a height vertex by world position. Clamps to tile bounds.
        // Returns true if the vertex was inside this tile (and was written).
        public bool SetHeightAtWorld(Vector3 worldPos, float height)
        {
            if (!TryWorldToVertex(worldPos, out int i, out int j)) return false;
            int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;
            Heights[idx] = height;
            HeightModified[idx] = true;
            return true;
        }

        public bool SetPaintAtWorld(Vector3 worldPos, Color32 color)
        {
            if (!TryWorldToVertex(worldPos, out int i, out int j)) return false;
            int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;
            Paint[idx] = color;
            PaintModified[idx] = true;
            return true;
        }

        public void AddOrReplaceDisk(DiskRecord disk)
        {
            for (int n = 0; n < Disks.Count; n++)
            {
                if (Disks[n].StableId == disk.StableId)
                {
                    Disks[n] = disk;
                    return;
                }
            }
            Disks.Add(disk);
        }

        public bool RemoveDisk(int stableId)
        {
            for (int n = 0; n < Disks.Count; n++)
            {
                if (Disks[n].StableId == stableId)
                {
                    Disks.RemoveAt(n);
                    return true;
                }
            }
            return false;
        }

        // World-space center of the tile (XZ; Y is irrelevant since the anchor lives
        // at Y=-5000 but the mesh renders at whatever height the disks define).
        public Vector3 WorldCenter =>
            new Vector3(
                TileXZ.x * TILE_SIZE + TILE_SIZE * 0.5f,
                0f,
                TileXZ.y * TILE_SIZE + TILE_SIZE * 0.5f);

        // Tile bounds in world XZ space. Y range is unbounded (caller picks).
        public Rect WorldXZRect => new Rect(
            TileXZ.x * TILE_SIZE,
            TileXZ.y * TILE_SIZE,
            TILE_SIZE,
            TILE_SIZE);

        // World position -> vertex (i, j) within this tile. Returns false if the
        // world point lies outside the tile rect. Vertex grid includes both edges
        // (0..64 inclusive), so border points belong to two tiles — both tiles
        // store the border vertex and weld dedups at apply time.
        public bool TryWorldToVertex(Vector3 worldPos, out int i, out int j)
        {
            float localX = worldPos.x - TileXZ.x * TILE_SIZE;
            float localZ = worldPos.z - TileXZ.y * TILE_SIZE;

            i = Mathf.RoundToInt(localX / VERTEX_SPACING);
            j = Mathf.RoundToInt(localZ / VERTEX_SPACING);

            return i >= 0 && i < TerrainTileCodec.VERTEX_GRID_SIZE
                && j >= 0 && j < TerrainTileCodec.VERTEX_GRID_SIZE;
        }

        // Vertex (i, j) -> world XZ position.
        public Vector3 VertexToWorld(int i, int j) =>
            new Vector3(
                TileXZ.x * TILE_SIZE + i * VERTEX_SPACING,
                Heights != null && i >= 0 && j >= 0
                    && i < TerrainTileCodec.VERTEX_GRID_SIZE
                    && j < TerrainTileCodec.VERTEX_GRID_SIZE
                    ? Heights[j * TerrainTileCodec.VERTEX_GRID_SIZE + i]
                    : 0f,
                TileXZ.y * TILE_SIZE + j * VERTEX_SPACING);

        // Sample the terrain height at a world position. Returns true + the
        // height value if the nearest vertex is modified (i.e. this tile has
        // brushed terrain at this position). Used by the IH ruler-snap patch
        // to lift the ruler ring onto our floating platforms.
        public bool TrySampleHeightAtWorld(Vector3 worldPos, out float height)
        {
            height = 0f;
            if (Heights == null || HeightModified == null) return false;
            if (!TryWorldToVertex(worldPos, out int i, out int j)) return false;
            int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;
            if (!HeightModified[idx]) return false;
            height = Heights[idx];
            return true;
        }

        // The brushed paint at a world position, when the nearest vertex was painted (admintools_test reads it back).
        public bool TrySamplePaintAtWorld(Vector3 worldPos, out Color32 color)
        {
            color = default;
            if (Paint == null || PaintModified == null) return false;
            if (!TryWorldToVertex(worldPos, out int i, out int j)) return false;
            int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;
            if (!PaintModified[idx]) return false;
            color = Paint[idx];
            return true;
        }
    }
}
