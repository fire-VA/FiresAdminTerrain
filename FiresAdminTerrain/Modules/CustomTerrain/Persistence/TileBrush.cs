using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Hammer-style brush operations against the tile-anchor system.
    //
    // Each Brush* method takes a world center + radius, finds every 64m tile
    // the brush footprint overlaps, spawns (or fetches) a TileAnchor for each,
    // and applies the op via ApplyOpAndSave per tile. Per-vertex math is
    // clipped to each tile's bounds — a single brush at a tile corner fans
    // out across 4 tiles naturally.
    //
    // Border vertices coincide between adjacent tiles. Same world (x,z) →
    // same vertex value in both tiles (the brush math is deterministic on
    // world coordinates), so the seam is exact without any dedup pass.
    //
    // Returns the number of tiles touched (for caller logging / undo).
    //
    // All methods are safe to call from console commands, Harmony patches,
    // or future IH integration. No assumption about IH being installed.
    public static class TileBrush
    {
        // Set every vertex inside the radius to a flat height. Use for
        // platform / level-terrain ops. Outside the radius, vertices are
        // left unchanged (unmodified).
        //
        // Square: if true, the brush footprint is a square (radius = half-side)
        // instead of a circle. Matches vanilla terrain op semantics.
        public static int BrushFlat(Vector3 center, float radius, float height, bool square = false, bool islandMode = false)
        {
            if (radius <= 0f)
            {
                FiresLogger.LogWarning($"[TileBrush.BrushFlat] radius<=0 ({radius}); no-op");
                return 0;
            }

            float r2 = radius * radius;
            int touched = 0;
            int verts = 0;

            TileUndoLog.BeginOperation($"VATileFlat:h={height:F1}{(islandMode ? ",island" : "")}", center, radius);

            // 2026-05-27 round-42 — REVERTED round-41's deferred rebuild
            // path; tiles were rendering INVISIBLE on placement until a
            // separate op triggered NotifyMutated on the same area. The
            // round-41 RebuildTouched pass apparently wasn't waking up the
            // mesh assignment reliably. Restored the original per-tile
            // immediate rebuild (NotifyMutated inside ApplyOpAndSave) so
            // visibility works the moment the data lands.
            //
            // The other round-41 fixes are KEPT:
            //   * SmoothHeightsInRadius_Single skips border verts → stitch
            //     owns those deterministically
            //   * ResolveBaseline gives border verts a neighbor-aware
            //     baseline so Apply{Flat,Raise} produces the same value
            //     on both sides of a seam by construction
            // The post-op StitchTileBorders still runs to clean up any
            // residual border divergence (which should be much less now
            // that the smooth doesn't touch borders and the baseline is
            // aligned).
            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchor.Spawn(tileXZ);
                if (anchor == null) continue;

                int wroteThisTile = anchor.ApplyOpAndSave(tile =>
                {
                    int w = ApplyFlatToTile(tile, center, radius, r2, height, square);
                    if (w > 0 && !square) SmoothHeightsInRadius(tile, center, radius);
                    if (w > 0 && islandMode && !tile.FloatingIslandMode)
                        tile.FloatingIslandMode = true;
                    return w;
                });

                if (wroteThisTile > 0)
                {
                    touched++;
                    verts += wroteThisTile;
                }
            }

            if (touched > 0) StitchTileBorders(center, radius);

            TileUndoLog.EndOperation();

            FiresLogger.LogVerbose($"[TileBrush.BrushFlat] center={center} r={radius} h={height} sq={square} island={islandMode} → {touched} tiles, {verts} verts");
            return touched;
        }

        // Set every vertex in the footprint to `altitude` plus a tilt along
        // `angle`, totalling `amount` across the footprint. `smooth` fades the
        // tilt toward the edge. World Edit Commands' slope, on tiles.
        public static int BrushSlope(Vector3 center, float radius, float angle, float altitude, float amount, float smooth, bool square)
        {
            if (radius <= 0f)
            {
                FiresLogger.LogWarning($"[TileBrush.BrushSlope] radius<=0 ({radius}); no-op");
                return 0;
            }

            int touched = 0;
            int verts = 0;

            TileUndoLog.BeginOperation($"VATileSlope:alt={altitude:F1},amt={amount:F1}", center, radius);

            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchor.Spawn(tileXZ);
                if (anchor == null) continue;

                int wroteThisTile = anchor.ApplyOpAndSave(tile =>
                    ApplySlopeToTile(tile, center, radius, angle, altitude, amount, smooth, square));

                if (wroteThisTile > 0)
                {
                    touched++;
                    verts += wroteThisTile;
                }
            }

            if (touched > 0) StitchTileBorders(center, radius);

            TileUndoLog.EndOperation();

            FiresLogger.LogVerbose($"[TileBrush.BrushSlope] center={center} r={radius} alt={altitude} amt={amount} angle={angle} sq={square} -> {touched} tiles, {verts} verts");
            return touched;
        }

        // Raise terrain inside the radius by `delta`. Falloff: 0=hard edge,
        // 1=linear, >1=power curve (sharper falloff at edge). Matches
        // vanilla RaiseTerrain semantics so behavior is intuitive to users
        // coming from the vanilla hoe.
        public static int BrushRaise(Vector3 center, float radius, float delta, float falloff = 1f, bool islandMode = false)
        {
            if (radius <= 0f)
            {
                FiresLogger.LogWarning($"[TileBrush.BrushRaise] radius<=0 ({radius}); no-op");
                return 0;
            }

            int touched = 0;
            int verts = 0;

            TileUndoLog.BeginOperation($"VATileRaise:d={delta:F1}", center, radius);

            // Round-42 — see BrushFlat for revert rationale.
            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchor.Spawn(tileXZ);
                if (anchor == null) continue;

                int wroteThisTile = anchor.ApplyOpAndSave(tile =>
                {
                    int w = ApplyRaiseToTile(tile, center, radius, delta, falloff);
                    if (w > 0) SmoothHeightsInRadius(tile, center, radius);
                    if (w > 0 && islandMode && !tile.FloatingIslandMode)
                        tile.FloatingIslandMode = true;
                    return w;
                });

                if (wroteThisTile > 0)
                {
                    touched++;
                    verts += wroteThisTile;
                }
            }

            if (touched > 0) StitchTileBorders(center, radius);

            TileUndoLog.EndOperation();

            FiresLogger.LogVerbose($"[TileBrush.BrushRaise] center={center} r={radius} delta={delta} falloff={falloff} → {touched} tiles, {verts} verts");
            return touched;
        }

        // Set every vertex's paint inside the radius to `color`. RGBA matches
        // vanilla heightmap paint mask convention (R=dirt, G=cultivated,
        // B=paved, A=preserves existing vegetation clear).
        public static int BrushPaint(Vector3 center, float radius, Color32 color, bool square = false)
        {
            if (radius <= 0f)
            {
                FiresLogger.LogWarning($"[TileBrush.BrushPaint] radius<=0 ({radius}); no-op");
                return 0;
            }

            float r2 = radius * radius;
            int touched = 0;
            int verts = 0;

            TileUndoLog.BeginOperation($"VATilePaint:#{color.r:X2}{color.g:X2}{color.b:X2}", center, radius);

            // 2026-05-30 round-54 — paint should NEVER auto-spawn new
            // tile anchors. The vanilla paint pipeline routes through
            // TileTerrainOpBlocker which calls BrushPaint for any op
            // position near a tile anchor — INCLUDING phantom ops
            // some external mods (FAP_ClutterClearTool spawns at world
            // origin) fire. Calling TileAnchor.Spawn for those phantom
            // positions creates anchors with invalid ZNetView (because
            // ZDOMan is in the wrong state) and floods the log with
            // "invalid ZNetView" + "Can't remove ZNetView" errors.
            //
            // Switching to GetAt — paint only applies to tiles that
            // ALREADY exist. Painting on an area with no custom tile
            // is a no-op (vanilla will handle that area via its own
            // paint mask).
            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchorRegistry.GetAt(tileXZ);
                if (anchor == null) continue;

                int wroteThisTile = anchor.ApplyOpAndSave(tile =>
                    ApplyPaintToTile(tile, center, radius, r2, color, square));

                if (wroteThisTile > 0)
                {
                    touched++;
                    verts += wroteThisTile;
                }
            }

            TileUndoLog.EndOperation();

            // UNIFIED PAINT: mirror the stroke into the VANILLA terrain system when the
            // colour maps to a canonical paint type — so a tool stroke crossing off our
            // anchored tiles keeps painting on plain vanilla ground instead of stopping
            // in a straight line at the tile border. Runs vanilla's own owner-RPC (exact
            // math, MP-correct). Non-canonical colours stay tile-only (the vanilla mask
            // RPC can only carry the 4 paint types). NOTE: the vanilla side has no undo.
            if (!square)
            {
                var vt = VerdantsAscent.Terrain.Paint.FiresUnifiedPaint.ColorToPaintType(color);
                if (vt.HasValue)
                {
                    int mirrored = VerdantsAscent.Terrain.Paint.FiresUnifiedPaint.MirrorPaint(center, radius, vt.Value, false);
                    if (mirrored > 0)
                        FiresLogger.LogVerbose($"[TileBrush.BrushPaint] mirrored to {mirrored} vanilla comp(s) as {vt.Value}.");
                }
            }

            FiresLogger.LogVerbose($"[TileBrush.BrushPaint] center={center} r={radius} color=#{color.r:X2}{color.g:X2}{color.b:X2}{color.a:X2} sq={square} → {touched} tiles, {verts} verts");
            return touched;
        }

        // Paint biome on every vertex inside the brush circle on CUSTOM TILES
        // ONLY. Per-vertex (codec v3) — a small radius only repaints verts
        // inside the circle, leaving the rest of the 64m tile at its previous
        // biome (or vanilla). biome=0 CLEARS the per-vertex override (vanilla
        // biome wins again at those verts).
        //
        // Vanilla heightmap biome paint is a SEPARATE path:
        // BiomeOverlayPainter.PaintOverlay → BiomePaintPersistence. The
        // dispatcher (TileBrushCommands.Cmd_Biome) raycasts the cursor and
        // chooses exactly one path so we never double-paint:
        //   * cursor over custom tile → THIS method (custom only)
        //   * cursor over vanilla     → BiomeOverlayPainter.PaintOverlay
        // User directive 2026-05-25.
        //
        // Returns the count of tiles touched (any vert changed). MP-safe via
        // TileBrushRPC.
        public static int BrushBiome(Vector3 center, float radius, ushort biome)
        {
            if (radius <= 0f)
            {
                FiresLogger.LogWarning($"[TileBrush.BrushBiome] radius<=0 ({radius}); no-op");
                return 0;
            }

            float r2 = radius * radius;
            int touched = 0;
            int totalVerts = 0;

            // Per-op undo snapshot for custom-tile biome. Begin BEFORE the
            // tile writes so we capture the BEFORE state of every vertex in
            // the radius across every overlapping tile; End AFTER all writes
            // so the deltas are computed in one batch.
            TileUndoLog.BeginOperation($"VATileBiome:{biome}", center, radius);

            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchor.Spawn(tileXZ);
                if (anchor == null) continue;

                int wroteThisTile = anchor.ApplyOpAndSave(tile =>
                    ApplyBiomeToTile(tile, center, radius, r2, biome));

                if (wroteThisTile > 0)
                {
                    touched++;
                    totalVerts += wroteThisTile;
                }
            }

            TileUndoLog.EndOperation();

            // D7 — re-roll vanilla grass under the painted area. Otherwise
            // painting Plains-on-Meadows leaves meadows-tinted grass on top
            // of the plains vertex color, looking wrong. ClutterSystem
            // doesn't auto-respond to vertex-color changes; we explicitly
            // call ResetGrass at the same radius so the next grass-roll
            // picks the (now-Plains-tinted) biome.
            //
            // Custom-biome clutter re-spawn (e.g. snow clutter on
            // Mountain paint, ashvine on AshLands) needs D5 (FAP clutter
            // wired). That can plug in here in a follow-up.
            if (touched > 0 && ClutterSystem.instance != null)
            {
                try
                {
                    ClutterSystem.instance.ResetGrass(center, radius);
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[TileBrush.BrushBiome] ResetGrass threw: {ex.Message}");
                }
            }

            FiresLogger.LogVerbose($"[TileBrush.BrushBiome] center={center} r={radius} biome={biome} → {touched} custom tile(s), {totalVerts} custom verts (custom-only path)");
            return touched;
        }

        // Map the per-vertex biome ushort to the RPG-maker OverlayType enum
        // used by BiomeOverlayPainter.PaintOverlay (vanilla heightmap path).
        // Called by TileBrushCommands.Cmd_Biome when the cursor is over
        // vanilla terrain.
        // Snow = Mountain, Lava = AshLands. Unknown / 0 → Reset (restores
        // world-gen biome via the painter's reset path).
        public static VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType MapBiomeToOverlayType(ushort biome)
        {
            switch ((Heightmap.Biome)biome)
            {
                case Heightmap.Biome.Mountain:    return VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType.Snow;
                case Heightmap.Biome.AshLands:    return VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType.Lava;
                case Heightmap.Biome.Swamp:       return VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType.Swamp;
                case Heightmap.Biome.BlackForest: return VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType.BlackForest;
                case Heightmap.Biome.Plains:      return VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType.Plains;
                case Heightmap.Biome.Mistlands:   return VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType.Mistlands;
                case Heightmap.Biome.Meadows:     return VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType.Meadows;
                case Heightmap.Biome.DeepNorth:   return VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType.DeepNorth;
                default:                          return VerdantsAscent.Terrain.Biome.BiomeOverlayPainter.OverlayType.Reset;
            }
        }

        // Per-tile inner loop for biome paint. Only verts inside the radius
        // get rewritten. Verts already at the target biome no-op-skip so
        // dragging the brush over the same area doesn't re-mark them and
        // bump revision needlessly.
        //
        // biome == 0 clears (BiomeModified=false + Biome=0). Otherwise
        // sets Biome=biome + BiomeModified=true.
        //
        // 2026-05-26 round-25 — LAVA DEPRESSION REMOVED. The earlier
        // LAVA_MAX_DEPTH=1m custom-tile depression was a workaround for
        // making the lava mesh sit flush with surrounding un-dug terrain.
        // Round-21 wired the lava overlay to vanilla's alpha→Y-offset
        // formula (alpha=1 → +0.8m, alpha=0 → +0.1m), so the lava surface
        // now naturally varies in height across the disk and blends down
        // to terrain at the edges WITHOUT any depression. User directive:
        // "we also no longer need to terrain op lower the ground by any
        // amount when placing lava ... just overlay it on top of terrain
        // and use the alpha channel to blend it right". So this method
        // now ONLY writes the biome value — heights stay untouched.
        private static int ApplyBiomeToTile(TerrainTile tile, Vector3 center, float radius, float r2, ushort biome)
        {
            ComputeVertexRange(tile, center, radius, out int iMin, out int iMax, out int jMin, out int jMax);

            int wrote = 0;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                float distSq = dx * dx + dz * dz;
                if (distSq > r2) continue;

                int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;
                if (biome == 0)
                {
                    // Clear path.
                    if (!tile.BiomeModified[idx]) continue; // already unset
                    tile.BiomeModified[idx] = false;
                    tile.Biome[idx] = 0;
                    wrote++;
                }
                else
                {
                    bool alreadyAtTarget = tile.BiomeModified[idx] && tile.Biome[idx] == biome;
                    if (alreadyAtTarget) continue;

                    tile.Biome[idx] = biome;
                    tile.BiomeModified[idx] = true;
                    wrote++;
                }
            }
            return wrote;
        }

        // Clear every vertex inside the radius back to unmodified. Now a
        // thin shim over RegionScrubber — the scrubber owns the per-tile
        // ClearData call, the vanilla biome overlay removal, the clutter
        // clear, and the grass reset, all in one batched pass with one
        // undo entry and one (future) RPC broadcast. This replaces the
        // hand-rolled per-tile loop below which only cleared tile data
        // and left vanilla overlay / clutter / grass orphans behind —
        // the bug class behind "ghost overlay resurrects on repaint".
        //
        // Scope = VisualOnly (TileOnly + VanillaBiome + Clutter + VanillaGrass).
        // We don't include VanillaPaint here — vanilla dirt/cult/paved
        // belongs to the vanilla hoe; BrushClear shouldn't nuke pavement
        // the player laid down with vanilla tools. Vegetation/Pieces also
        // excluded — clearing a tile shouldn't despawn the player's loot
        // chests or planted trees.
        public static int BrushClear(Vector3 center, float radius)
        {
            if (radius <= 0f) return 0;

            int n = VerdantsAscent.Terrain.Lifecycle.RegionScrubber.ScrubRegion(
                center, radius,
                VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope.VisualOnly,
                VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Source.BrushClear);

            FiresLogger.LogVerbose($"[TileBrush.BrushClear] center={center} r={radius:F1} → scrubber returned {n} change(s)");
            return n;
        }

        // ── Precision Hole Punch ─────────────────────────────────────────
        //
        // Carves a hole through the tile mesh in a circular radius. Clears
        // ONLY Heights[] + HeightModified[] — Paint and Biome stay
        // untouched, so re-patching later restores the painted look.
        //
        // RPG-maker called this "Precision Hole Punch" — the user's main
        // use case is carving cave entrances on floating platforms without
        // also nuking the painted dirt around the hole. Different from
        // BrushClear (which clears the full visual scope = tile data +
        // vanilla biome + clutter + grass).
        public static int BrushHolePunch(Vector3 center, float radius)
        {
            if (radius <= 0f) return 0;

            int touched = 0;
            TileUndoLog.BeginOperation("VATileHolePunch", center, radius);

            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchorRegistry.GetAt(tileXZ);
                if (anchor == null) continue; // no tile here = nothing to punch

                int wrote = anchor.ApplyOpAndSave(tile => ApplyHolePunchToTile(tile, center, radius * radius));
                if (wrote > 0) touched++;
            }

            TileUndoLog.EndOperation();
            FiresLogger.LogVerbose($"[TileBrush.BrushHolePunch] center={center} r={radius:F1} → {touched} tile(s) punched");
            return touched;
        }

        private static int ApplyHolePunchToTile(TerrainTile tile, Vector3 center, float r2)
        {
            int wrote = 0;
            int size = TerrainTileCodec.VERTEX_GRID_SIZE;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;

            for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                if (dx * dx + dz * dz > r2) continue;

                int idx = j * size + i;
                if (!tile.HeightModified[idx]) continue;
                tile.HeightModified[idx] = false;
                tile.Heights[idx] = 0f;
                wrote++;
            }
            return wrote;
        }

        // ── Patch Hole ───────────────────────────────────────────────────
        //
        // Fills unmodified vertices in a radius by interpolating heights
        // from nearby MODIFIED vertices on the same tile. Designed to be
        // the inverse of BrushHolePunch — repair / fill cave entrances.
        //
        // Strategy per unmodified vertex inside the radius:
        //   1. Walk outward in a small N-ring (8-neighborhood, then 16, then 24)
        //   2. First modified neighbor found: copy its height
        //   3. None found inside the tile: skip (still no nearby data)
        // Cheap (max ~24 lookups per vertex) and produces a smooth fill
        // because adjacent verts inherit from the same source.
        public static int BrushPatchHole(Vector3 center, float radius)
        {
            if (radius <= 0f) return 0;

            int touched = 0;
            float r2 = radius * radius;
            TileUndoLog.BeginOperation("VATilePatchHole", center, radius);

            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchor.Spawn(tileXZ); // spawn if needed — patching can extend onto fresh tile
                if (anchor == null) continue;

                int wrote = anchor.ApplyOpAndSave(tile => ApplyPatchHoleToTile(tile, center, radius, r2));
                if (wrote > 0) touched++;
            }

            TileUndoLog.EndOperation();
            FiresLogger.LogVerbose($"[TileBrush.BrushPatchHole] center={center} r={radius:F1} → {touched} tile(s) patched");
            return touched;
        }

        private static int ApplyPatchHoleToTile(TerrainTile tile, Vector3 center, float radius, float r2)
        {
            ComputeVertexRange(tile, center, radius, out int iMin, out int iMax, out int jMin, out int jMax);

            int wrote = 0;
            int size = TerrainTileCodec.VERTEX_GRID_SIZE;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                if (dx * dx + dz * dz > r2) continue;

                int idx = j * size + i;
                if (tile.HeightModified[idx]) continue; // already filled

                // Look outward up to 3 rings for a modified neighbor to inherit from.
                if (TryFindNearbyModifiedHeight(tile, i, j, size, maxRing: 3, out float patchY))
                {
                    tile.Heights[idx] = patchY;
                    tile.HeightModified[idx] = true;
                    wrote++;
                }
            }
            return wrote;
        }

        // Spiral outward from (i, j) up to `maxRing` cells. Returns the
        // first modified neighbor's height. False if none in the search
        // bounds. Cheap — at maxRing=3 it's at most 24 lookups.
        private static bool TryFindNearbyModifiedHeight(TerrainTile tile, int ci, int cj, int gridSize, int maxRing, out float height)
        {
            height = 0f;
            int maxIdx = gridSize - 1;
            for (int ring = 1; ring <= maxRing; ring++)
            {
                // Top + bottom rows of this ring
                for (int di = -ring; di <= ring; di++)
                {
                    int i1 = ci + di;
                    if (i1 < 0 || i1 > maxIdx) continue;
                    foreach (int dj in new[] { -ring, ring })
                    {
                        int j1 = cj + dj;
                        if (j1 < 0 || j1 > maxIdx) continue;
                        int nIdx = j1 * gridSize + i1;
                        if (tile.HeightModified[nIdx])
                        {
                            height = tile.Heights[nIdx];
                            return true;
                        }
                    }
                }
                // Left + right columns of this ring (excluding corners already checked above)
                for (int dj = -ring + 1; dj <= ring - 1; dj++)
                {
                    int j1 = cj + dj;
                    if (j1 < 0 || j1 > maxIdx) continue;
                    foreach (int di in new[] { -ring, ring })
                    {
                        int i1 = ci + di;
                        if (i1 < 0 || i1 > maxIdx) continue;
                        int nIdx = j1 * gridSize + i1;
                        if (tile.HeightModified[nIdx])
                        {
                            height = tile.Heights[nIdx];
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // ---- per-tile implementation ----
        //
        // Each Apply* method runs inside a single tile's ApplyOpAndSave
        // callback. The world-bounds intersection of the brush with the
        // tile's 64m rect is converted to a vertex (i, j) range; we only
        // touch the verts in that rect.
        //
        // VertexWorld(tile, i, j) = (tileX*64 + i, _, tileY*64 + j) with
        // i,j in [0, 64] inclusive. The center vertex spacing matches
        // vanilla heightmap (1m).

        // ── 2026-05-26 PHASE 1 (Drawing Feel Parity) ────────────────────
        //
        // Brush ops now use SMOOTHSTEP falloff at the edge instead of a
        // hard cutoff. Mirrors `DirtFloorMeshCombiner.MeshGen.cs:971-993
        // CalculateBlendWeight` from RPG-maker — the proven "smooth like
        // Blender" recipe. Edge weights ramp `t² · (3-2t)` over the last
        // `BRUSH_EDGE_BLEND_DISTANCE` of the brush radius, lerping the
        // existing tile heights toward the brush target instead of
        // overwriting outright.
        //
        // The smoothstep band converts hard brush edges into soft beveled
        // transitions and lets repeated stamps blend together — matching
        // the user's stated goal of "lowering ruler and dragging into a
        // mesh will carve a nice clean path; raising and dragging will
        // create nice hills".
        //
        // After each brush op the controller runs `SmoothHeightsInRadius`
        // (port of RPG-maker's 2-pass 8-neighbor Gaussian) to dissolve
        // any residual 1m terracing at stamp boundaries.

        /// <summary>
        /// Width of the smoothstep falloff band, in world meters. The
        /// outer `BRUSH_EDGE_BLEND_DISTANCE` of the brush radius lerps
        /// from full effect → no effect using `t²·(3-2t)`. Matches
        /// RPG-maker `BlendDistance = 1f`.
        /// </summary>
        private const float BRUSH_EDGE_BLEND_DISTANCE = 1.0f;

        /// <summary>
        /// Hermite smoothstep over `[0, 1]`. `t=1` at center → `t=0` at edge.
        /// </summary>
        private static float Smoothstep01(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Compute brush weight at a given distance from center. Inside
        /// the inner radius (radius - BLEND): weight 1. Outside the
        /// radius: 0. Across the BLEND band: smoothstep ramp.
        ///
        /// Round-31 B-4: thin shim over `BrushWeightCurve` with the
        /// historical "flat center, smoothstep edge band" behavior.
        /// New code should prefer `BrushWeightCurve(...)` directly to
        /// pick a falloff preset.
        /// </summary>
        private static float BrushWeight(float dist, float radius)
        {
            if (dist >= radius) return 0f;
            float innerR = radius - BRUSH_EDGE_BLEND_DISTANCE;
            if (innerR <= 0f)
            {
                // Small brush: pure smoothstep across the entire radius.
                return Smoothstep01(1f - dist / Mathf.Max(radius, 0.001f));
            }
            if (dist <= innerR) return 1f;
            // Edge band: smoothstep from 1 (inner edge) → 0 (outer edge).
            float u = (dist - innerR) / BRUSH_EDGE_BLEND_DISTANCE; // 0..1 across band
            return Smoothstep01(1f - u);
        }

        /// <summary>
        /// Phase B-4: full-radius curve-based brush weight. Picks a
        /// `BrushFalloffCurves.Preset` (Smooth / Smoother / Sharp / etc.)
        /// and evaluates across the ENTIRE radius — no flat-center
        /// plateau like `BrushWeight`. Use this when you want the brush
        /// to feel like a single continuous curve from center to edge,
        /// not a flat slab with a beveled rim.
        /// </summary>
        private static float BrushWeightCurve(float dist, float radius, BrushFalloffCurves.Preset preset)
        {
            return BrushFalloffCurves.EvaluateAtDist(preset, dist, radius);
        }

        private static int ApplyFlatToTile(TerrainTile tile, Vector3 center, float radius, float r2, float height, bool square)
        {
            ComputeVertexRange(tile, center, radius, out int iMin, out int iMax, out int jMin, out int jMax);

            int wrote = 0;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;
            int gridSize = TerrainTileCodec.VERTEX_GRID_SIZE;

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                float distSq = dx * dx + dz * dz;
                if (!square && distSq > r2) continue;

                int idx = j * gridSize + i;

                if (square)
                {
                    // Square stamps stay hard-edged (used by the IH ruler
                    // rectangle tool which intentionally wants a flat slab).
                    tile.Heights[idx] = height;
                    tile.HeightModified[idx] = true;
                    wrote++;
                    continue;
                }

                // Smoothstep-band blend: at the inner core, fully set to
                // `height`; across the edge band, lerp from the existing
                // height (or `center.y` if unmodified) toward `height`.
                float dist = Mathf.Sqrt(distSq);
                float w = BrushWeight(dist, radius);
                if (w <= 0f) continue;

                // Round-41 — at BORDER verts use neighbor-aware baseline so
                // adjacent tiles produce identical Heights at the shared
                // world XZ (root cause #3 in the stitching plan).
                float existingH = ResolveBaseline(tile, i, j, idx, gridSize, center.y);
                tile.Heights[idx] = Mathf.Lerp(existingH, height, w);
                tile.HeightModified[idx] = true;
                wrote++;
            }
            return wrote;
        }

        // Normalised footprint edge and WEC's amount halving, spelled out.
        private const float SlopeEdgeDistance = 1f;
        private const float SlopeHalfAmount = 0.5f;

        // Height is a pure function of world position here, so tiles sharing a
        // border vertex agree without a neighbour-aware baseline.
        private static int ApplySlopeToTile(TerrainTile tile, Vector3 center, float radius, float angle,
                                            float altitude, float amount, float smooth, bool square)
        {
            ComputeVertexRange(tile, center, radius, out int iMin, out int iMax, out int jMin, out int jMax);

            int wrote = 0;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;
            int gridSize = TerrainTileCodec.VERTEX_GRID_SIZE;

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float nx = (originX + i * TerrainTile.VERTEX_SPACING - center.x) / radius;
                float nz = (originZ + j * TerrainTile.VERTEX_SPACING - center.z) / radius;
                float distance = square ? Mathf.Max(Mathf.Abs(nx), Mathf.Abs(nz)) : Mathf.Sqrt(nx * nx + nz * nz);
                if (distance > SlopeEdgeDistance) continue;

                int idx = j * gridSize + i;
                tile.Heights[idx] = altitude
                    + SlopeTilt(angle, nx, nz) * SlopeSmoothFactor(smooth, distance) * amount * SlopeHalfAmount;
                tile.HeightModified[idx] = true;
                wrote++;
            }
            return wrote;
        }

        private static float SlopeTilt(float angle, float nx, float nz) => Mathf.Sin(angle) * nx + Mathf.Cos(angle) * nz;

        private static float SlopeSmoothFactor(float smooth, float distance)
            => (SlopeEdgeDistance - distance) >= smooth ? 1f : (SlopeEdgeDistance - distance) / smooth;

        private static int ApplyRaiseToTile(TerrainTile tile, Vector3 center, float radius, float delta, float falloff)
        {
            ComputeVertexRange(tile, center, radius, out int iMin, out int iMax, out int jMin, out int jMax);

            int wrote = 0;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > radius) continue;

                // Phase 1: universal smoothstep falloff. The legacy `falloff`
                // arg is now ignored — kept on the signature for backward
                // compat with TileBrushRPC.SendRaise / TileBrushDrag.
                // The smoothstep band gives Raise its "nice hills" shape
                // (`t²·(3-2t)` produces a dome, not a cone).
                float w = BrushWeight(dist, radius);
                if (w <= 0f) continue;

                float scaledDelta = delta * w;

                int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;
                // Round-41 — neighbor-aware baseline at borders so the
                // raise math is deterministic per world XZ even when one
                // side was previously modified and the other wasn't.
                float baseH = ResolveBaseline(tile, i, j, idx, TerrainTileCodec.VERTEX_GRID_SIZE, center.y);
                tile.Heights[idx] = baseH + scaledDelta;
                tile.HeightModified[idx] = true;
                wrote++;
            }
            return wrote;
        }

        // Round-41 root-cause-#3 fix — neighbor-aware baseline at border
        // verts. Interior verts use the standard `HeightModified ? Heights
        // : center.y` rule. BORDER verts (i=0 / i=GRID-1 / j=0 / j=GRID-1)
        // additionally consult the adjacent tile across the seam: if THAT
        // tile has HeightModified=true at the matching world XZ, use ITS
        // Heights value instead. So adjacent tiles always start from the
        // same baseline → Apply* produces the same new value on both →
        // no border divergence to clean up later.
        private static float ResolveBaseline(TerrainTile tile, int i, int j, int idx, int gridSize, float defaultY)
        {
            if (tile.HeightModified[idx]) return tile.Heights[idx];

            bool isBorder = (i == 0 || i == gridSize - 1 || j == 0 || j == gridSize - 1);
            if (!isBorder) return defaultY;

            // Convert local (i,j) to world XZ, then find the OTHER tile
            // (or tiles, for the corner case) that share this vertex.
            // For a corner vert (i and j both border), there are up to
            // THREE neighbors sharing the same world XZ — check all of
            // them; first one with HeightModified=true wins.
            float worldX = tile.TileXZ.x * TerrainTile.TILE_SIZE + i * TerrainTile.VERTEX_SPACING;
            float worldZ = tile.TileXZ.y * TerrainTile.TILE_SIZE + j * TerrainTile.VERTEX_SPACING;

            // Cardinal neighbor (the one across the border axis we're on).
            // Use a tolerant rounded lookup so border vertices that sit
            // exactly on the seam route to the OTHER tile, not back to
            // self via floor(worldX/64) rounding into our own index.
            int[] dxs;
            int[] dzs;
            if (i == 0 && j == 0)              { dxs = new[] { -1, 0, -1 }; dzs = new[] { 0, -1, -1 }; }
            else if (i == gridSize - 1 && j == 0) { dxs = new[] { 1, 0, 1 };  dzs = new[] { 0, -1, -1 }; }
            else if (i == 0 && j == gridSize - 1) { dxs = new[] { -1, 0, -1 }; dzs = new[] { 0, 1, 1 }; }
            else if (i == gridSize - 1 && j == gridSize - 1) { dxs = new[] { 1, 0, 1 }; dzs = new[] { 0, 1, 1 }; }
            else if (i == 0)                   { dxs = new[] { -1 }; dzs = new[] { 0 }; }
            else if (i == gridSize - 1)        { dxs = new[] { 1 };  dzs = new[] { 0 }; }
            else if (j == 0)                   { dxs = new[] { 0 };  dzs = new[] { -1 }; }
            else                                { dxs = new[] { 0 };  dzs = new[] { 1 }; }

            for (int k = 0; k < dxs.Length; k++)
            {
                var neighborXZ = new Vector2i(tile.TileXZ.x + dxs[k], tile.TileXZ.y + dzs[k]);
                var neighborAnchor = TileAnchorRegistry.GetAt(neighborXZ);
                if (neighborAnchor == null) continue;
                var neighborTile = neighborAnchor.GetComponent<TerrainTile>();
                if (neighborTile == null || neighborTile.Heights == null) continue;

                // Compute the neighbor's local (i,j) for this world XZ.
                float nOriginX = neighborXZ.x * TerrainTile.TILE_SIZE;
                float nOriginZ = neighborXZ.y * TerrainTile.TILE_SIZE;
                int ni = Mathf.RoundToInt((worldX - nOriginX) / TerrainTile.VERTEX_SPACING);
                int nj = Mathf.RoundToInt((worldZ - nOriginZ) / TerrainTile.VERTEX_SPACING);
                if (ni < 0 || ni >= gridSize || nj < 0 || nj >= gridSize) continue;
                int nidx = nj * gridSize + ni;
                if (!neighborTile.HeightModified[nidx]) continue;
                return neighborTile.Heights[nidx];
            }

            return defaultY;
        }

        // ── 2026-05-26 round-31 BLENDER PORT (Phase B-2) ─────────────────
        //
        // Smoothing kernel rewritten to match Blender's Smooth brush:
        // UNIFORM 1-ring Laplacian (source/blender/editors/sculpt_paint/
        // mesh/sculpt_smooth.cc:47-57 neighbor_data_average_mesh). Counter-
        // intuitively SIMPLER than the previous weighted Gaussian — Blender's
        // creamy feel comes from RUNNING MORE PASSES, not from kernel
        // cleverness. The new SmoothHeightsInRadius_Single does ONE pass
        // and is called repeatedly via iteration_strengths.
        //
        // The TerracingPostPass wrapper below is what BrushFlat / BrushRaise
        // call — preserves the original 2-pass dissolve-terraces behavior
        // for backward compat, but now uses the uniform kernel.

        /// <summary>
        /// One single Laplacian smoothing pass. Reads from `srcHeights`,
        /// writes to `tile.Heights`. Caller chooses src (snapshot vs live)
        /// based on accumulate-mode semantics. Strength `t` ∈ [0,1] is the
        /// per-pass blend factor: `new = lerp(src, neighborAvg, t)`.
        ///
        /// Uniform weight per neighbor — matches Blender's `mix_indices`
        /// behavior in `neighbor_data_average_mesh`. Sums all 8 ring-1
        /// neighbors with weight 1 each, divides by count of valid
        /// neighbors. Center vertex contributes nothing (pure Laplacian).
        ///
        /// `MIN_DIFF_TO_SMOOTH` gate preserves sharp intentional edges:
        /// if a vertex is already within 5cm of its neighborhood mean we
        /// don't touch it. Matches `if (diff > 0.05f)` clause in RPG-maker
        /// `SmoothHeightEdges` (and is the right behavior for terrain).
        /// </summary>
        private static void SmoothHeightsInRadius_Single(TerrainTile tile, Vector3 center, float radius, float[] srcHeights, float t)
        {
            if (t <= 0f) return;
            const float MIN_DIFF_TO_SMOOTH = 0.05f;
            const float SMOOTH_MARGIN = 1.5f;

            float marginRadius = radius + SMOOTH_MARGIN;
            // 2026-05-26 round-32: extend the iteration range to the FULL
            // grid so border verts (i=0, i=GRID-1, j=0, j=GRID-1) can be
            // smoothed. Their neighbor lookups fall through to the
            // adjacent tile via SampleHeightAcrossTiles below — without
            // this border-inclusion the seam between two tiles stayed
            // as a hard ridge that no amount of smoothing could fix.
            ComputeVertexRange(tile, center, marginRadius, out int iMin, out int iMax, out int jMin, out int jMax);
            if (iMax - iMin < 1 || jMax - jMin < 1) return;

            int gridSize = TerrainTileCodec.VERTEX_GRID_SIZE;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;
            float marginR2 = marginRadius * marginRadius;

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                int idx = j * gridSize + i;
                if (!tile.HeightModified[idx]) continue;

                // 2026-05-26 round-41 — SKIP BORDER VERTS. The smoothing
                // kernel produces ORDER-DEPENDENT results at tile borders:
                // when tile A smooths first it reads tile B's pre-smooth
                // values; when tile B smooths it reads A's post-smooth
                // values. The two sides land on different averages → seam.
                // Letting StitchTileBorders' deterministic averaging own
                // the border verts removes the asymmetry by construction.
                // The cost: a single-vert strip along the seam isn't
                // smoothed, which the stitch's averaging cleans up.
                if (i == 0 || i == gridSize - 1 || j == 0 || j == gridSize - 1)
                    continue;

                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                if (dx * dx + dz * dz > marginR2) continue;

                // Uniform 8-neighbor Laplacian — all neighbors weight 1.
                // Neighbor lookups go through SampleNeighborHeight which
                // walks across tile boundaries. Returns the vertex height
                // OR the current vert's own height if the neighbor isn't
                // available (degenerate-fallback so border smoothing
                // doesn't get pulled toward zero).
                float ownH = srcHeights[idx];
                float sum = 0f; int n = 0;
                AccumulateNeighbor(tile, srcHeights, i - 1, j,     ownH, ref sum, ref n);
                AccumulateNeighbor(tile, srcHeights, i + 1, j,     ownH, ref sum, ref n);
                AccumulateNeighbor(tile, srcHeights, i,     j - 1, ownH, ref sum, ref n);
                AccumulateNeighbor(tile, srcHeights, i,     j + 1, ownH, ref sum, ref n);
                AccumulateNeighbor(tile, srcHeights, i - 1, j - 1, ownH, ref sum, ref n);
                AccumulateNeighbor(tile, srcHeights, i + 1, j - 1, ownH, ref sum, ref n);
                AccumulateNeighbor(tile, srcHeights, i - 1, j + 1, ownH, ref sum, ref n);
                AccumulateNeighbor(tile, srcHeights, i + 1, j + 1, ownH, ref sum, ref n);
                if (n == 0) continue;
                float avg = sum / n;

                float current = ownH;
                float diff = Mathf.Abs(current - avg);
                if (diff <= MIN_DIFF_TO_SMOOTH) continue;

                // Lerp toward avg by the pass-strength `t`. With t=1 (a
                // full iteration_strengths pass) this jumps all the way
                // to avg; fractional passes get partial blend.
                tile.Heights[idx] = Mathf.Lerp(current, avg, t);
            }
        }

        // Cross-tile neighbor lookup. If (i, j) is inside this tile's
        // grid, reads srcHeights directly. Otherwise resolves the world
        // XZ position and queries TileAnchorRegistry for the adjacent
        // tile; returns that tile's modified height at the same world
        // position. Falls back to `ownH` (the center vert's height) if
        // the neighbor tile isn't loaded or its vertex isn't modified —
        // a sane no-op rather than pulling toward zero.
        //
        // Border verts coincide between adjacent tiles by construction
        // (`TerrainTile` docs), so this lookup is exact, not approximate.
        private static void AccumulateNeighbor(TerrainTile tile, float[] srcHeights, int i, int j, float ownH, ref float sum, ref int count)
        {
            int gridSize = TerrainTileCodec.VERTEX_GRID_SIZE;

            if (i >= 0 && i < gridSize && j >= 0 && j < gridSize)
            {
                int idx = j * gridSize + i;
                if (!tile.HeightModified[idx]) return; // unmodified neighbor = skip (no contribution)
                sum += srcHeights[idx];
                count++;
                return;
            }

            // Cross-tile lookup. Compute the world XZ of the requested
            // neighbor vertex, find which tile owns it, and query.
            float worldX = tile.TileXZ.x * TerrainTile.TILE_SIZE + i * TerrainTile.VERTEX_SPACING;
            float worldZ = tile.TileXZ.y * TerrainTile.TILE_SIZE + j * TerrainTile.VERTEX_SPACING;

            // Tile index = floor(world / 64). Use Mathf.FloorToInt so
            // negative coordinates round the right way.
            int neighborTileX = Mathf.FloorToInt(worldX / TerrainTile.TILE_SIZE);
            int neighborTileZ = Mathf.FloorToInt(worldZ / TerrainTile.TILE_SIZE);
            var neighborXZ = new Vector2i(neighborTileX, neighborTileZ);

            // Skip the lookup if it would round back to this same tile
            // (can happen for verts exactly on the border, modulo math).
            if (neighborXZ.x == tile.TileXZ.x && neighborXZ.y == tile.TileXZ.y) return;

            var neighborAnchor = TileAnchorRegistry.GetAt(neighborXZ);
            if (neighborAnchor == null) return; // not loaded → skip
            var neighborTile = neighborAnchor.GetComponent<TerrainTile>();
            if (neighborTile == null || neighborTile.Heights == null) return;

            // Compute the neighbor tile's local (i,j) for our world XZ.
            float neighborOriginX = neighborTileX * TerrainTile.TILE_SIZE;
            float neighborOriginZ = neighborTileZ * TerrainTile.TILE_SIZE;
            int ni = Mathf.RoundToInt((worldX - neighborOriginX) / TerrainTile.VERTEX_SPACING);
            int nj = Mathf.RoundToInt((worldZ - neighborOriginZ) / TerrainTile.VERTEX_SPACING);
            if (ni < 0 || ni >= gridSize || nj < 0 || nj >= gridSize) return;
            int nidx = nj * gridSize + ni;
            if (!neighborTile.HeightModified[nidx]) return;

            sum += neighborTile.Heights[nidx];
            count++;
        }

        /// <summary>
        /// Blender-style multi-pass dispatcher. Splits `strength` into
        /// integer + fractional iterations (matches `iteration_strengths`
        /// in mesh/brushes/smooth.cc:30-45). Each integer pass runs at
        /// strength 1.0; the final fractional pass uses the remainder.
        /// Returns the number of passes actually executed.
        ///
        /// `srcSnapshot` is the per-stroke snapshot of heights at LMB-down.
        /// When non-null, passes target TOWARD the snapshot (accumulate=
        /// false mode — held brush converges to a target and stops). When
        /// null, passes read live heights for each iteration (accumulate=
        /// true mode — held brush keeps building).
        /// </summary>
        private static int SmoothHeightsInRadius_Iterated(TerrainTile tile, Vector3 center, float radius, float strength, float[] srcSnapshot)
        {
            strength = Mathf.Clamp01(strength);
            if (strength <= 0.001f) return 0;

            // Blender's MAX_SMOOTH_ITERATIONS = 4. One dab can run up to
            // 4 full passes at strength 1.0 plus a fractional last pass.
            const int MAX_ITERS = 4;
            float scaled = strength * MAX_ITERS;
            int fullCount = Mathf.FloorToInt(scaled);
            float fractional = scaled - fullCount;

            // Snapshot-mode: re-derive `srcHeights` from the snapshot
            // each pass so the algorithm targets origHeights, not the
            // post-previous-pass state. This is what makes holding the
            // smooth brush converge (instead of degenerating).
            // Live-mode: read tile.Heights directly each pass.
            int passes = 0;
            for (int p = 0; p < fullCount; p++)
            {
                float[] src = srcSnapshot ?? tile.Heights;
                SmoothHeightsInRadius_Single(tile, center, radius, src, 1f);
                passes++;
            }
            if (fractional > 0.001f)
            {
                float[] src = srcSnapshot ?? tile.Heights;
                SmoothHeightsInRadius_Single(tile, center, radius, src, fractional);
                passes++;
            }
            return passes;
        }

        // Legacy wrapper kept so BrushFlat / BrushRaise post-pass calls
        // don't have to change their call sites. Uses the new uniform
        // kernel but runs the same 2 full passes the old weighted
        // Gaussian did (matches the previous dissolve-terraces strength).
        // Live-mode (no snapshot) because this fires AFTER a height
        // write, so the new heights ARE the source of truth.
        private static void SmoothHeightsInRadius(TerrainTile tile, Vector3 center, float radius)
        {
            // Round-31: legacy post-pass smoothing is now a thin shim over
            // the Blender iteration_strengths dispatcher. Strength=0.5 →
            // 2 full passes at strength 1.0 (matches the old 2-pass
            // weighted Gaussian intensity, but uses the cleaner uniform
            // 1-ring kernel). No snapshot — live-mode is correct for the
            // post-stamp dissolve case (we WANT to smooth the just-written
            // heights, not the pre-stroke heights).
            SmoothHeightsInRadius_Iterated(tile, center, radius, strength: 0.5f, srcSnapshot: null);
        }

        // ── 2026-05-26 round-33 SEAM STITCHING ─────────────────────────
        //
        // After any brush op writes to multiple tiles, run a stitch pass
        // that syncs border verts between adjacent tiles. Border verts
        // coincide between tiles by construction (same world XZ), so
        // they MUST have the same Y or the visual mesh splits into
        // disconnected sheets — exactly the artifact the user reported.
        //
        // Causes of divergence:
        //   1. Brush op writes to tile A but not tile B (radius doesn't
        //      reach far enough into B).
        //   2. SmoothHeightsInRadius_Single processes A and B separately;
        //      A's border vert reads from B's pre-smooth heights (via
        //      AccumulateNeighbor), but B's border vert reads from its
        //      OWN pre-smooth heights — the two end up with different
        //      averages.
        //   3. Different brush sessions wrote to A vs B at different times.
        //
        // Stitch strategy: for each tile in the touched set, walk its 4
        // borders. For each border vert that's HeightModified, check the
        // adjacent tile's matching border vert. If the neighbor is also
        // HeightModified, AVERAGE the two heights and write back to both.
        // If only one side is modified, COPY that height to the neighbor
        // and flag it modified (extends the floor by 1 vert into the
        // neighbor — required so the mesh quad on that side renders).
        //
        // Runs in a separate ApplyOpAndSave so the ZDO write/RPC fires
        // for both tiles, not just the brush-touched ones.
        // 2026-05-26 round-41 — single rebuild pass over a set of tiles.
        // Brush ops collect the set of touched tiles via deferred
        // ApplyOpAndSave calls, then this runs ONCE at the end with
        // fully-stitched data — eliminating the multi-rebuild churn
        // that previously left visible seam ridges.
        private static void RebuildTouched(HashSet<Vector2i> rebuildSet)
        {
            if (rebuildSet == null || rebuildSet.Count == 0) return;
            foreach (var tileXZ in rebuildSet)
            {
                var anchor = TileAnchorRegistry.GetAt(tileXZ);
                if (anchor == null) continue;
                var tile = anchor.GetComponent<TerrainTile>();
                tile?.NotifyMutated();
            }
        }

        // 2026-05-26 round-41 — overload that defers rebuilds and
        // accumulates affected tiles into the caller's rebuild set.
        // The legacy signature below delegates to this with deferRebuild=false
        // so external callers (debug commands, manual stitch tool) still
        // work unchanged.
        public static int StitchTileBorders(Vector3 center, float radius)
        {
            var rebuildSet = new HashSet<Vector2i>();
            int n = StitchTileBorders(center, radius, rebuildSet, deferRebuild: false);
            // Legacy callers want immediate rebuilds — and since deferRebuild=false
            // already triggered them via ApplyOpAndSave, there's nothing left to do.
            return n;
        }

        public static int StitchTileBorders(Vector3 center, float radius, HashSet<Vector2i> rebuildSet, bool deferRebuild)
        {
            int stitched = 0;
            int gridSize = TerrainTileCodec.VERTEX_GRID_SIZE;

            // Collect all tiles overlapping the brush PLUS their cardinal
            // neighbors — stitching needs to walk every shared seam in
            // the affected zone.
            var touched = new System.Collections.Generic.HashSet<Vector2i>();
            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                touched.Add(tileXZ);
                touched.Add(new Vector2i(tileXZ.x + 1, tileXZ.y));
                touched.Add(new Vector2i(tileXZ.x - 1, tileXZ.y));
                touched.Add(new Vector2i(tileXZ.x,     tileXZ.y + 1));
                touched.Add(new Vector2i(tileXZ.x,     tileXZ.y - 1));
            }

            FiresLogger.LogInfo($"[TileBrush.StitchTileBorders] start | center=({center.x:F1},{center.z:F1}) r={radius:F1} | {touched.Count} tile(s) in scope");

            // Track which tiles actually had border verts modified so we
            // only re-save those (avoids gratuitous WriteToZdo + RPC fan-out
            // on neighbor tiles whose seams were already in sync).
            var modifiedTiles = new System.Collections.Generic.HashSet<Vector2i>();

            // 2026-05-26 round-35 BUGFIX: previously we mutated tile.Heights
            // OUTSIDE an ApplyOpAndSave context, then called ApplyOpAndSave
            // with a no-op lambda hoping it would commit the prior writes.
            // That flow had an ordering hazard: claim-ownership might fail
            // on non-owner peers (silently dropping our stitch writes),
            // and the no-op lambda made it look like 0 work happened.
            //
            // New flow: do the per-tile stitch math INSIDE the tile's own
            // ApplyOpAndSave callback. This way ownership is claimed before
            // the mutation, the callback returns the actual write count,
            // and persistence + mesh rebuild fire iff verts changed.
            foreach (var tileXZ in touched)
            {
                var anchorA = TileAnchorRegistry.GetAt(tileXZ);
                if (anchorA == null) continue;

                Vector2i capturedXZ = tileXZ;
                int wrote = anchorA.ApplyOpAndSave(tileA =>
                {
                    if (tileA.Heights == null) return 0;
                    int localWrites = 0;

                    // EAST seam: A's i=GRID-1 column == EAST tile's i=0 column.
                    var eastXZ = new Vector2i(capturedXZ.x + 1, capturedXZ.y);
                    var anchorE = TileAnchorRegistry.GetAt(eastXZ);
                    if (anchorE != null)
                    {
                        var tileE = anchorE.GetComponent<TerrainTile>();
                        if (tileE != null && tileE.Heights != null)
                        {
                            int n = StitchBorderColumn(tileA, gridSize - 1, tileE, 0, gridSize);
                            if (n > 0)
                            {
                                localWrites += n;
                                modifiedTiles.Add(eastXZ);
                                FiresLogger.LogInfo($"[StitchTileBorders]   EAST seam ({capturedXZ.x},{capturedXZ.y})↔({eastXZ.x},{eastXZ.y}) → {n} vert(s)");
                            }
                        }
                    }

                    // NORTH seam: A's j=GRID-1 row == NORTH tile's j=0 row.
                    var northXZ = new Vector2i(capturedXZ.x, capturedXZ.y + 1);
                    var anchorN = TileAnchorRegistry.GetAt(northXZ);
                    if (anchorN != null)
                    {
                        var tileN = anchorN.GetComponent<TerrainTile>();
                        if (tileN != null && tileN.Heights != null)
                        {
                            int n = StitchBorderRow(tileA, gridSize - 1, tileN, 0, gridSize);
                            if (n > 0)
                            {
                                localWrites += n;
                                modifiedTiles.Add(northXZ);
                                FiresLogger.LogInfo($"[StitchTileBorders]   NORTH seam ({capturedXZ.x},{capturedXZ.y})↔({northXZ.x},{northXZ.y}) → {n} vert(s)");
                            }
                        }
                    }

                    return localWrites;
                }, deferRebuild: deferRebuild);

                if (wrote > 0)
                {
                    stitched += wrote;
                    // The OWN tile (`tileXZ`) had a successful ApplyOpAndSave
                    // (wrote>0 → ZDO write). If we're deferring rebuilds, add
                    // it to the caller's rebuild set so the final pass picks
                    // it up.
                    if (deferRebuild && rebuildSet != null) rebuildSet.Add(capturedXZ);
                }
            }

            // Force a save on neighbor tiles whose borders WE mutated. They
            // didn't go through their own ApplyOpAndSave loop (we wrote to
            // them across tiles), so the no-op-lambda commit fires WriteToZdo
            // (and conditionally NotifyMutated based on deferRebuild).
            foreach (var tileXZ in modifiedTiles)
            {
                var anchor = TileAnchorRegistry.GetAt(tileXZ);
                if (anchor == null) continue;
                anchor.ApplyOpAndSave(_ => 1, deferRebuild: deferRebuild);
                if (deferRebuild && rebuildSet != null) rebuildSet.Add(tileXZ);
            }

            FiresLogger.LogInfo($"[TileBrush.StitchTileBorders] complete | {stitched} vert(s) stitched across {modifiedTiles.Count} neighbor tile(s) committed (deferRebuild={deferRebuild})");
            return stitched;
        }

        // East-west seam: tile A's column at iA == tile B's column at iB.
        // Walks j ∈ [0, gridSize) and syncs each matching vert pair.
        // Returns the number of vertex pairs actually stitched (changed).
        private static int StitchBorderColumn(TerrainTile tileA, int iA, TerrainTile tileB, int iB, int gridSize)
        {
            int changed = 0;
            for (int j = 0; j < gridSize; j++)
            {
                int idxA = j * gridSize + iA;
                int idxB = j * gridSize + iB;
                if (StitchVertPair(tileA, idxA, tileB, idxB)) changed++;
            }

            // Round-63 — soften the crease the averaging leaves behind.
            // Averaging ONLY the border verts bends the surface at a
            // single vert width → visible hard ridge along the seam
            // (user screenshots). Relax each tile's column ADJACENT to
            // the border halfway toward the midpoint of its lateral
            // neighbors (border col + next-inner col). Each tile relaxes
            // its OWN inner column with its own data + the now-shared
            // border → deterministic, no cross-tile divergence.
            if (changed > 0)
            {
                // Round-64 — smooth the border LINE itself first. The
                // smooth brush deliberately skips border verts (round-41
                // order-dependence guard), so after a strong smooth the
                // interior has moved while the border kept its stale
                // pre-smooth height → residual hard edge (user report).
                // Here in the stitch we have BOTH tiles' final data, so
                // a border Laplacian is deterministic: blend each border
                // vert toward the average of its seam neighbours + the
                // first inner vert from EACH side, written to both tiles.
                SmoothSeamColumn(tileA, iA, tileB, iB, gridSize);

                SoftenCreaseColumn(tileA, iA, gridSize);
                SoftenCreaseColumn(tileB, iB, gridSize);
            }
            return changed;
        }

        // Deterministic border-line smooth for an east-west seam. For
        // each seam vert: avg( seamPrev, seamNext, innerA, innerB ) —
        // the full cross-tile 1-ring — then blend halfway and write the
        // SAME value into both tiles' border verts.
        private static void SmoothSeamColumn(TerrainTile tileA, int iA, TerrainTile tileB, int iB, int gridSize)
        {
            int innerColA = iA == 0 ? 1 : iA - 1;
            int innerColB = iB == 0 ? 1 : iB - 1;

            // Snapshot the seam line so the pass reads consistent inputs.
            var seam = new float[gridSize];
            var seamValid = new bool[gridSize];
            for (int j = 0; j < gridSize; j++)
            {
                int idxA = j * gridSize + iA;
                seam[j] = tileA.Heights[idxA];
                seamValid[j] = tileA.HeightModified[idxA];
            }

            for (int j = 0; j < gridSize; j++)
            {
                if (!seamValid[j]) continue;

                int idxA = j * gridSize + iA;
                int idxB = j * gridSize + iB;
                int idxInnerA = j * gridSize + innerColA;
                int idxInnerB = j * gridSize + innerColB;

                float sum = 0f; int n = 0;
                if (j > 0 && seamValid[j - 1]) { sum += seam[j - 1]; n++; }
                if (j < gridSize - 1 && seamValid[j + 1]) { sum += seam[j + 1]; n++; }
                if (tileA.HeightModified[idxInnerA]) { sum += tileA.Heights[idxInnerA]; n++; }
                if (tileB.HeightModified[idxInnerB]) { sum += tileB.Heights[idxInnerB]; n++; }
                if (n < 2) continue;

                float smoothed = Mathf.Lerp(seam[j], sum / n, 0.5f);
                tileA.Heights[idxA] = smoothed;
                tileB.Heights[idxB] = smoothed;
            }
        }

        // North-south seam: tile A's row at jA == tile B's row at jB.
        private static int StitchBorderRow(TerrainTile tileA, int jA, TerrainTile tileB, int jB, int gridSize)
        {
            int changed = 0;
            for (int i = 0; i < gridSize; i++)
            {
                int idxA = jA * gridSize + i;
                int idxB = jB * gridSize + i;
                if (StitchVertPair(tileA, idxA, tileB, idxB)) changed++;
            }

            if (changed > 0)
            {
                // Round-64 — see SmoothSeamColumn.
                SmoothSeamRow(tileA, jA, tileB, jB, gridSize);

                SoftenCreaseRow(tileA, jA, gridSize);
                SoftenCreaseRow(tileB, jB, gridSize);
            }
            return changed;
        }

        // North-south twin of SmoothSeamColumn.
        private static void SmoothSeamRow(TerrainTile tileA, int jA, TerrainTile tileB, int jB, int gridSize)
        {
            int innerRowA = jA == 0 ? 1 : jA - 1;
            int innerRowB = jB == 0 ? 1 : jB - 1;

            var seam = new float[gridSize];
            var seamValid = new bool[gridSize];
            for (int i = 0; i < gridSize; i++)
            {
                int idxA = jA * gridSize + i;
                seam[i] = tileA.Heights[idxA];
                seamValid[i] = tileA.HeightModified[idxA];
            }

            for (int i = 0; i < gridSize; i++)
            {
                if (!seamValid[i]) continue;

                int idxA = jA * gridSize + i;
                int idxB = jB * gridSize + i;
                int idxInnerA = innerRowA * gridSize + i;
                int idxInnerB = innerRowB * gridSize + i;

                float sum = 0f; int n = 0;
                if (i > 0 && seamValid[i - 1]) { sum += seam[i - 1]; n++; }
                if (i < gridSize - 1 && seamValid[i + 1]) { sum += seam[i + 1]; n++; }
                if (tileA.HeightModified[idxInnerA]) { sum += tileA.Heights[idxInnerA]; n++; }
                if (tileB.HeightModified[idxInnerB]) { sum += tileB.Heights[idxInnerB]; n++; }
                if (n < 2) continue;

                float smoothed = Mathf.Lerp(seam[i], sum / n, 0.5f);
                tileA.Heights[idxA] = smoothed;
                tileB.Heights[idxB] = smoothed;
            }
        }

        // Relax the column ADJACENT to a stitched border column: pull it
        // halfway toward the midpoint of (border, next-inner) so the
        // surface curves smoothly through the averaged border instead of
        // kinking at it. Skips verts whose 3-sample stencil isn't fully
        // modified (don't invent heights at floating-terrain edges).
        private static void SoftenCreaseColumn(TerrainTile tile, int borderCol, int gridSize)
        {
            int dir = borderCol == 0 ? 1 : -1;   // toward tile interior
            int innerCol = borderCol + dir;
            int inner2Col = borderCol + dir * 2;
            if (innerCol < 0 || innerCol >= gridSize || inner2Col < 0 || inner2Col >= gridSize) return;

            for (int j = 0; j < gridSize; j++)
            {
                int idxB = j * gridSize + borderCol;
                int idxI = j * gridSize + innerCol;
                int idxI2 = j * gridSize + inner2Col;
                if (!tile.HeightModified[idxB] || !tile.HeightModified[idxI] || !tile.HeightModified[idxI2])
                    continue;

                float target = (tile.Heights[idxB] + tile.Heights[idxI2]) * 0.5f;
                tile.Heights[idxI] = Mathf.Lerp(tile.Heights[idxI], target, 0.5f);
            }
        }

        private static void SoftenCreaseRow(TerrainTile tile, int borderRow, int gridSize)
        {
            int dir = borderRow == 0 ? 1 : -1;
            int innerRow = borderRow + dir;
            int inner2Row = borderRow + dir * 2;
            if (innerRow < 0 || innerRow >= gridSize || inner2Row < 0 || inner2Row >= gridSize) return;

            for (int i = 0; i < gridSize; i++)
            {
                int idxB = borderRow * gridSize + i;
                int idxI = innerRow * gridSize + i;
                int idxI2 = inner2Row * gridSize + i;
                if (!tile.HeightModified[idxB] || !tile.HeightModified[idxI] || !tile.HeightModified[idxI2])
                    continue;

                float target = (tile.Heights[idxB] + tile.Heights[idxI2]) * 0.5f;
                tile.Heights[idxI] = Mathf.Lerp(tile.Heights[idxI], target, 0.5f);
            }
        }

        // Sync a single border-vert pair. Three cases:
        //   * Both modified, heights differ → average; write average to both.
        //   * One modified, other not → copy the modified value to the
        //     unmodified side + flag it modified. This extends the floor
        //     by 1 vert into the neighbor so the seam quad renders.
        //   * Both unmodified → nothing to do.
        //   * Both modified, heights equal → already stitched, no-op.
        private static bool StitchVertPair(TerrainTile tileA, int idxA, TerrainTile tileB, int idxB)
        {
            bool aMod = tileA.HeightModified[idxA];
            bool bMod = tileB.HeightModified[idxB];

            if (aMod && bMod)
            {
                float ha = tileA.Heights[idxA];
                float hb = tileB.Heights[idxB];
                if (Mathf.Abs(ha - hb) <= 0.001f) return false;
                float avg = (ha + hb) * 0.5f;
                tileA.Heights[idxA] = avg;
                tileB.Heights[idxB] = avg;
                return true;
            }
            if (aMod)
            {
                tileB.Heights[idxB] = tileA.Heights[idxA];
                tileB.HeightModified[idxB] = true;
                return true;
            }
            if (bMod)
            {
                tileA.Heights[idxA] = tileB.Heights[idxB];
                tileA.HeightModified[idxA] = true;
                return true;
            }
            return false;
        }

        // ── 2026-05-26 round-37 PHASE B-6: GRAB brush (Blender Grab) ─────
        //
        // Drags terrain vertically by the cursor's Y delta from stroke start.
        // Uses the per-stroke snapshot from NotifyStrokeStart so the drag
        // targets a STABLE reference height — each dab during the stroke
        // sets `Heights[idx] = origHeights[idx] + dragDeltaY * brushWeight`.
        // Releasing LMB then re-clicking starts a fresh snapshot/baseline.
        //
        // Why this is different from Raise:
        //   * Raise accumulates: each dab adds delta to current Heights.
        //     Holding still on the same spot keeps raising forever.
        //   * Grab is target-toward: each dab targets origHeights + delta.
        //     Holding still on the same spot does nothing — the brush
        //     waits for the cursor to move. Releasing locks in the
        //     current delta as the new baseline for the next stroke.
        //
        // Mirrors Blender's SCULPT_BRUSH_TYPE_GRAB (`sculpt_grab_brush.cc`):
        // `accumulate = false`, snapshot-based, delta from initial position.
        public static int BrushGrab(Vector3 center, float radius, float dragDeltaY)
        {
            if (radius <= 0f) return 0;
            float r2 = radius * radius;
            int touched = 0;
            TileUndoLog.BeginOperation($"VATileGrab:dy={dragDeltaY:F2}", center, radius);

            // Round-42 — immediate per-tile rebuild (see BrushFlat).
            // Stitch fires AFTER the loop (was previously per-tile stitch
            // inside ApplyOpAndSave — that nested call was wasteful + bug-
            // prone since each per-tile stitch only saw partial state).
            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchor.Spawn(tileXZ);
                if (anchor == null) continue;
                s_strokeSnapshots.TryGetValue(tileXZ, out float[] snapshot);
                int wroteThisTile = anchor.ApplyOpAndSave(tile =>
                    ApplyGrabToTile(tile, center, radius, r2, dragDeltaY, snapshot));
                if (wroteThisTile > 0) touched++;
            }

            if (touched > 0) StitchTileBorders(center, radius);

            TileUndoLog.EndOperation();
            FiresLogger.LogVerbose($"[TileBrush.BrushGrab] center={center} r={radius:F1} dy={dragDeltaY:F2} → {touched} tile(s)");
            return touched;
        }

        private static int ApplyGrabToTile(TerrainTile tile, Vector3 center, float radius, float r2, float dragDeltaY, float[] snapshot)
        {
            ComputeVertexRange(tile, center, radius, out int iMin, out int iMax, out int jMin, out int jMax);
            int wrote = 0;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                float distSq = dx * dx + dz * dz;
                if (distSq > r2) continue;

                int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;
                float dist = Mathf.Sqrt(distSq);
                float w = BrushWeight(dist, radius);
                if (w <= 0f) continue;

                // origHeights from snapshot if present (mid-stroke), else
                // current Heights (single-click case). Either way, the
                // grab targets origHeights + delta, not Heights + delta.
                float baseH;
                if (snapshot != null) baseH = snapshot[idx];
                else baseH = tile.HeightModified[idx] ? tile.Heights[idx] : center.y;

                tile.Heights[idx] = baseH + dragDeltaY * w;
                tile.HeightModified[idx] = true;
                wrote++;
            }
            return wrote;
        }

        // ── 2026-05-26 round-37 PHASE B-7: NOISE brush (Blender-ish Noise) ──
        //
        // Perlin-noise displacement for organic terrain detail. Single dab
        // or held-drag both work — the noise is deterministic on world XZ
        // so repeated dabs over the same spot produce the same pattern.
        //
        // Per vertex inside the brush:
        //   noise = (PerlinNoise(wx*scale, wz*scale) - 0.5) * 2  // -1 .. +1
        //   Heights[idx] = baseH + noise * intensity * brushWeight
        //
        // baseH = snapshot[idx] if mid-stroke (so the noise is applied
        // relative to the pre-stroke surface, not the noisy in-progress
        // state). Allows held-drag to keep painting the same noise pattern
        // without compounding it into chaos.
        //
        // Use cases: add rocky detail to flat cliff faces, eroded variation
        // on smooth slopes, organic ground texture on what would otherwise
        // be perfectly flat platforms.
        public static int BrushNoise(Vector3 center, float radius, float intensity, float scale)
        {
            if (radius <= 0f) return 0;
            if (Mathf.Abs(intensity) <= 0.001f) return 0;
            scale = Mathf.Max(scale, 0.001f);

            float r2 = radius * radius;
            int touched = 0;
            TileUndoLog.BeginOperation($"VATileNoise:i={intensity:F2}s={scale:F2}", center, radius);

            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchor.Spawn(tileXZ);
                if (anchor == null) continue;
                s_strokeSnapshots.TryGetValue(tileXZ, out float[] snapshot);
                int wroteThisTile = anchor.ApplyOpAndSave(tile =>
                    ApplyNoiseToTile(tile, center, radius, r2, intensity, scale, snapshot));
                if (wroteThisTile > 0) touched++;
            }

            if (touched > 0) StitchTileBorders(center, radius);

            TileUndoLog.EndOperation();
            FiresLogger.LogVerbose($"[TileBrush.BrushNoise] center={center} r={radius:F1} intensity={intensity:F2} scale={scale:F2} → {touched} tile(s)");
            return touched;
        }

        private static int ApplyNoiseToTile(TerrainTile tile, Vector3 center, float radius, float r2, float intensity, float scale, float[] snapshot)
        {
            ComputeVertexRange(tile, center, radius, out int iMin, out int iMax, out int jMin, out int jMax);
            int wrote = 0;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                float distSq = dx * dx + dz * dz;
                if (distSq > r2) continue;

                int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;
                float dist = Mathf.Sqrt(distSq);
                float w = BrushWeight(dist, radius);
                if (w <= 0f) continue;

                // Perlin returns [0, 1]. Remap to [-1, +1] so noise pushes
                // both up and down (cratered + raised, not just one).
                float n01 = Mathf.PerlinNoise(wx * scale, wz * scale);
                float noise = (n01 - 0.5f) * 2f;

                float baseH;
                if (snapshot != null) baseH = snapshot[idx];
                else baseH = tile.HeightModified[idx] ? tile.Heights[idx] : center.y;

                tile.Heights[idx] = baseH + noise * intensity * w;
                tile.HeightModified[idx] = true;
                wrote++;
            }
            return wrote;
        }

        // ── 2026-05-26 round-31 PHASE B-2: Smooth brush + origHeights snapshot ──
        //
        // Dedicated Smooth brush — matches Blender's Smooth (Laplacian)
        // brush behavior. Per-stroke snapshot ensures holding the brush
        // converges to a smoothed target instead of degenerating into a
        // depression (`accumulate = false` mode from `ss.cache->accum`).
        //
        // Lifecycle:
        //   * On LMB-down, TileBrushDrag calls NotifyStrokeStart(center, radius)
        //     → we snapshot Heights[] for every tile in radius into s_strokeSnapshots.
        //   * Each dab during the stroke calls BrushSmooth(center, radius, strength)
        //     → uses the snapshot as the read source.
        //   * On LMB-up, TileBrushDrag calls NotifyStrokeEnd()
        //     → snapshots cleared.

        // Per-stroke snapshot keyed by tile XZ. Value is a per-tile clone
        // of Heights[] taken at stroke start. Used as the read-source for
        // every smoothing pass during the stroke so the brush targets
        // a stable reference instead of compounding live-state drift.
        private static readonly System.Collections.Generic.Dictionary<Vector2i, float[]> s_strokeSnapshots
            = new System.Collections.Generic.Dictionary<Vector2i, float[]>();

        /// <summary>
        /// Called by TileBrushDrag when LMB transitions from up → down.
        /// Snapshots Heights[] of every tile whose footprint touches the
        /// brush radius at stroke start. Subsequent BrushSmooth calls
        /// during the stroke read from these snapshots so the smoothing
        /// converges to a target instead of degenerating.
        /// </summary>
        public static void NotifyStrokeStart(Vector3 center, float radius)
        {
            s_strokeSnapshots.Clear();
            // Generous radius — the lazy-pointer cursor may pull the
            // brush slightly off the click position. Include all tiles
            // within 1.5x the brush radius so subsequent dabs find
            // their snapshot.
            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius * 1.5f))
            {
                var anchor = TileAnchorRegistry.GetAt(tileXZ);
                if (anchor == null) continue;
                var tile = anchor.GetComponent<TerrainTile>();
                if (tile == null || tile.Heights == null) continue;
                var snap = new float[tile.Heights.Length];
                System.Array.Copy(tile.Heights, snap, tile.Heights.Length);
                s_strokeSnapshots[tileXZ] = snap;
            }
            FiresLogger.LogVerbose($"[TileBrush.NotifyStrokeStart] snapshot taken for {s_strokeSnapshots.Count} tile(s) at center={center} r={radius:F1}");
        }

        /// <summary>
        /// Called by TileBrushDrag when LMB transitions from down → up.
        /// Discards the per-stroke snapshots so memory doesn't accumulate
        /// and the next stroke gets a fresh baseline.
        /// </summary>
        public static void NotifyStrokeEnd()
        {
            int n = s_strokeSnapshots.Count;
            s_strokeSnapshots.Clear();
            FiresLogger.LogVerbose($"[TileBrush.NotifyStrokeEnd] cleared {n} snapshot(s)");
        }

        /// <summary>
        /// Smooth brush dispatch. Mirrors Blender's Smooth (Laplacian).
        /// `strength` ∈ [0,1] controls iteration count via the
        /// iteration_strengths splitter (0.25 = 1 pass, 0.5 = 2 passes,
        /// 0.75 = 3 passes, 1.0 = 4 full passes). Per-stroke snapshot
        /// from NotifyStrokeStart is used as the read source — held
        /// brush converges + stops instead of compounding.
        /// </summary>
        public static int BrushSmooth(Vector3 center, float radius, float strength)
        {
            if (radius <= 0f) return 0;
            strength = Mathf.Clamp01(strength);
            if (strength <= 0.001f) return 0;

            int touched = 0;
            TileUndoLog.BeginOperation($"VATileSmooth:s={strength:F2}", center, radius);

            foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
            {
                var anchor = TileAnchor.Spawn(tileXZ);
                if (anchor == null) continue;

                s_strokeSnapshots.TryGetValue(tileXZ, out float[] snapshot);

                int wroteThisTile = anchor.ApplyOpAndSave(tile =>
                    SmoothHeightsInRadius_Iterated(tile, center, radius, strength, snapshot));
                if (wroteThisTile > 0) touched++;
            }

            // Round-42: stitch fires AFTER all tiles done. The smooth
            // kernel skips border verts (round-41 in
            // SmoothHeightsInRadius_Single) so the stitch's averaging is
            // the authoritative border-vert result, not a cleanup pass.
            if (touched > 0) StitchTileBorders(center, radius);

            TileUndoLog.EndOperation();
            FiresLogger.LogVerbose($"[TileBrush.BrushSmooth] center={center} r={radius:F1} strength={strength:F2} → {touched} tile(s)");
            return touched;
        }

        private static int ApplyPaintToTile(TerrainTile tile, Vector3 center, float radius, float r2, Color32 color, bool square)
        {
            ComputeVertexRange(tile, center, radius, out int iMin, out int iMax, out int jMin, out int jMax);

            int wrote = 0;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;

            for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                int idx = j * TerrainTileCodec.VERTEX_GRID_SIZE + i;

                if (square)
                {
                    // Square fill stays a hard set (vanilla has no square paint).
                    tile.Paint[idx] = color;
                    tile.PaintModified[idx] = true;
                    wrote++;
                    continue;
                }

                float dx = wx - center.x;
                float dz = wz - center.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;

                // VANILLA-EXACT falloff (TerrainComp.PaintCleared verbatim):
                //   t = pow(1 - clamp01(d / r), 0.1), lerp toward the target colour,
                //   ALPHA PRESERVED (alpha is the cleared-vegetation channel, managed
                //   by its own ops). The old hard `Paint[idx] = color` fill made tile
                //   strokes edge differently from vanilla strokes — the last visible
                //   difference across a tile/vanilla boundary. One formula, no seam.
                float t = Mathf.Pow(1f - Mathf.Clamp01(Mathf.Sqrt(d2) / radius), 0.1f);
                Color cur = tile.Paint[idx];
                Color blended = Color.Lerp(cur, color, t);
                // Vanilla preserves alpha because its masks always carry meaningful
                // worldgen alpha. A FRESH tile vert's alpha is just unset (0) — first
                // touch stamps the tool's alpha (old behaviour), repaints preserve.
                blended.a = tile.PaintModified[idx] ? cur.a : ((Color)color).a;
                tile.Paint[idx] = blended;
                tile.PaintModified[idx] = true;
                wrote++;
            }
            return wrote;
        }

        private static int ApplyClearToTile(TerrainTile tile, Vector3 center, float r2)
        {
            int wrote = 0;
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;
            int size = TerrainTileCodec.VERTEX_GRID_SIZE;

            for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                float wx = originX + i * TerrainTile.VERTEX_SPACING;
                float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                float dx = wx - center.x;
                float dz = wz - center.z;
                if (dx * dx + dz * dz > r2) continue;

                int idx = j * size + i;
                if (tile.HeightModified[idx]) { tile.HeightModified[idx] = false; tile.Heights[idx] = 0f; wrote++; }
                if (tile.PaintModified[idx]) { tile.PaintModified[idx] = false; tile.Paint[idx] = default; wrote++; }
                // Also clear biome override so Heightmap.FindBiome at this
                // position falls back to vanilla biome. Without this, erasing
                // a tile leaves the biome ghost behind — vegetation, AI, and
                // ambient sound would still think it's the painted biome.
                // Spotted 2026-05-25.
                if (tile.BiomeModified != null && tile.BiomeModified[idx])
                {
                    tile.BiomeModified[idx] = false;
                    tile.Biome[idx] = 0;
                    wrote++;
                }
            }
            return wrote;
        }

        // Clip the brush circle to the tile's 64m rect → vertex (i,j) range.
        // Clamps to [0, 64] so we never index out of bounds.
        private static void ComputeVertexRange(
            TerrainTile tile, Vector3 center, float radius,
            out int iMin, out int iMax, out int jMin, out int jMax)
        {
            float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
            float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;
            int size = TerrainTileCodec.VERTEX_GRID_SIZE - 1; // 64 (max vertex index)

            iMin = Mathf.Clamp(Mathf.FloorToInt((center.x - radius - originX) / TerrainTile.VERTEX_SPACING), 0, size);
            iMax = Mathf.Clamp(Mathf.CeilToInt((center.x + radius - originX) / TerrainTile.VERTEX_SPACING), 0, size);
            jMin = Mathf.Clamp(Mathf.FloorToInt((center.z - radius - originZ) / TerrainTile.VERTEX_SPACING), 0, size);
            jMax = Mathf.Clamp(Mathf.CeilToInt((center.z + radius - originZ) / TerrainTile.VERTEX_SPACING), 0, size);
        }

        // Resolve "where the player is looking" for a hammer-style pointer.
        // Three-stage resolution:
        //   1. Physics.Raycast 600m — matches tile RENDER LOD so any visible
        //      tile / vanilla terrain along the camera ray is hit. Bumped
        //      from 50m 2026-05-25 (user complaint: floors landed under feet
        //      when looking far away because the short ray missed and the
        //      fallback dropped to player feet).
        //   2. Ray-to-player-Y-plane intersection — if the raycast missed
        //      (looking at the sky, or no colliders cooked at distance),
        //      project the camera ray to the horizontal plane passing
        //      through the player's current Y. The result is where the
        //      cursor "would" hit if the world were a flat plane at the
        //      player's elevation. Skylands work great with this.
        //   3. Feet fallback — only if dir.y is near zero (camera looking
        //      perfectly level on a flat horizon) and even the plane
        //      intersection didn't give a forward t > 0.
        public static bool TryGetPointerWorldPosition(out Vector3 hit)
        {
            return TryGetPointerTarget(out hit, out _);
        }

        // Same raycast as TryGetPointerWorldPosition, PLUS reports which kind
        // of terrain the cursor center hit:
        //   * hitCustomTile = true  → the ray hit a TerrainTile collider (our
        //                              custom tile mesh). Caller should route
        //                              biome / paint ops to the custom-tile
        //                              system and SKIP the vanilla overlay.
        //   * hitCustomTile = false → the ray hit vanilla terrain (Heightmap)
        //                              or any other collider, or fell through
        //                              all collider checks. Caller should route
        //                              biome / paint ops to the VANILLA overlay
        //                              system and SKIP the custom tile path.
        //
        // User directive 2026-05-25: "if the center of our cursor is on the
        // custom terrain we JUST write to custom terrain, if were over vanilla
        // we JUST paint to the vanilla. clean and simple". Previously both
        // paths fired unconditionally and the user saw double-paint behaviour.
        public static bool TryGetPointerTarget(out Vector3 hit, out bool hitCustomTile)
        {
            hit = Vector3.zero;
            hitCustomTile = false;

            var p = Player.m_localPlayer;
            if (p == null) return false;

            var cam = GameCamera.instance;
            Vector3 origin = cam != null ? cam.transform.position : p.transform.position + Vector3.up * 1.7f;
            Vector3 dir    = cam != null ? cam.transform.forward  : p.transform.forward;

            const float MAX_RAYCAST = 600f;
            int mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "WaterVolume");

            // RaycastAll so we can pick the nearest hit AND classify it
            // properly. Single Raycast gives us "first thing the ray hit"
            // which is what we want — but we need the collider component to
            // tell custom vs vanilla apart. TerrainTile lives on the same
            // GameObject as its MeshCollider so `GetComponent<TerrainTile>`
            // on the hit's GameObject answers the question.
            if (Physics.Raycast(origin, dir, out var rh, MAX_RAYCAST, mask))
            {
                hit = rh.point;
                if (rh.collider != null)
                {
                    // Walk parents in case the collider sits on a child of
                    // the tile root. TerrainTile is on the tile root itself
                    // today but a future refactor could nest it under a
                    // collider child — be defensive.
                    var t = rh.collider.GetComponentInParent<TerrainTile>();
                    hitCustomTile = t != null;
                }
                return true;
            }

            // No collider in the ray's path — project onto the player's Y
            // plane so the brush lands at the cursor direction × forward
            // distance instead of dropping under the player. Ray-plane
            // intersection can't tell custom from vanilla; default to
            // VANILLA (false) so an empty-air aim doesn't accidentally
            // spawn custom tiles all over the world. User can still aim
            // directly AT a custom tile to get the custom branch.
            if (Mathf.Abs(dir.y) > 0.001f)
            {
                float t = (p.transform.position.y - origin.y) / dir.y;
                if (t > 0f && t < MAX_RAYCAST)
                {
                    hit = origin + dir * t;
                    return true;
                }
            }

            // Last resort: feet. Only reachable when camera is perfectly
            // horizontal AND no collider exists in 600m forward.
            hit = p.transform.position;
            return true;
        }
    }
}
