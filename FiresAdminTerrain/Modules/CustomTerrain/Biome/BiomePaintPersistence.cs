using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Terrain.Clutter;

namespace VerdantsAscent.Terrain.Biome
{
    /// <summary>
    /// Persistence system for biome overlay painting and clutter.
    /// 
    /// ARCHITECTURE:
    /// - Biome paint data is stored in TerrainComp ZDO for persistence
    /// - EnvironmentBoxPatches.RebuildRenderMesh handles applying vertex colors
    /// - This class provides data storage/retrieval and the GetPaintAtPosition() API
    /// 
    /// Priority order (handled in EnvironmentBoxPatches):
    /// 1. Persistent biome paints (this system)
    /// 2. Environment Box biome overrides
    /// 3. World generator biome
    /// 
    /// ZDO Keys used:
    /// - "va_biome_paints" - Compressed byte array of biome paint data
    /// - "va_clutter_paints" - Compressed byte array of clutter paint data
    /// </summary>
    public static class BiomePaintPersistence
    {
        #region Constants

        // ZDO keys for our custom data
        private static readonly int ZDO_BIOME_PAINTS = "va_biome_paints".GetStableHashCode();
        private static readonly int ZDO_CLUTTER_PAINTS = "va_clutter_paints".GetStableHashCode();

        // Version for data format
        private const int DATA_VERSION = 1;

        #endregion

        #region Reflection Cache

        private static bool _reflectionCached = false;
        private static FieldInfo _hmapField;
        private static FieldInfo _nviewField;

        private static void EnsureReflectionCached()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _hmapField = typeof(TerrainComp).GetField("m_hmap", flags);
                _nviewField = typeof(TerrainComp).GetField("m_nview", flags);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BiomePaintPersistence] Failed to cache reflection: {ex.Message}");
            }
        }

        #endregion

        #region Static State

        // Flag to prevent re-recording during restoration
        private static bool _isRestoring = false;

        // Track which TerrainComps have been restored this session
        private static HashSet<int> _restoredTerrainComps = new HashSet<int>();

        // Spawned clutter GameObjects for cleanup tracking
        private static Dictionary<int, List<GameObject>> _spawnedClutterByTerrainComp = new Dictionary<int, List<GameObject>>();

        // In-memory cache of all active biome paints for fast lookup during RebuildRenderMesh
        // This is populated from ZDO data when TerrainComps load
        private static List<BiomePaintData> _activePaints = new List<BiomePaintData>();

        /// <summary>
        /// Removes vanilla biome paint entries whose painted disk overlaps a
        /// world-space circle. Pure "remove from vanilla" operation — does
        /// NOT touch the legacy DirtFloorMeshCombiner branch (the legacy
        /// stack is on its way out — see Phase F in MASTER_GAMEPLAN.md).
        ///
        /// Use case: a centralized destroy/clear pass via RegionScrubber
        /// needs to wipe BiomeOverlay overlay bytes IN A REGION without
        /// touching the rest of the heightmap (ResetBiomeOverlayInternal
        /// is too coupled to combiner state). Returns the number of paint
        /// records removed.
        ///
        /// Persistence:
        ///   * Updates in-memory _activePaints (used by RebuildRenderMesh
        ///     patches for vertex coloring).
        ///   * Persists the trimmed list to TerrainComp ZDO when we own it
        ///     (otherwise the server applies and replicates via the standard
        ///     RPC pipeline).
        ///   * Triggers ForceHeightmapRegeneration so vertex colors refresh
        ///     immediately on the local renderer.
        ///   * Does NOT call ClutterSystem.ResetGrass — that's a separate
        ///     scope bit in RegionScrubber. Keeping concerns separate so the
        ///     scrubber can choose to clear biome bytes without also blowing
        ///     away grass when that's not the user's intent.
        ///
        /// MP NOTE: this is the LOCAL side. RegionScrubber's RPC dispatch
        /// fans the parent scrub op out to all peers; each peer calls
        /// RemoveOverlapping locally. The TerrainComp ZDO save only happens
        /// on the peer that owns the TerrainComp, just like vanilla.
        /// </summary>
        public static int RemoveOverlapping(Vector3 center, float radius)
        {
            if (_isRestoring) return 0;
            if (radius <= 0f) return 0;

            // FULLY-CONTAINED predicate (not the old "any-overlap" one) —
            // a clear circle only removes biome paint disks whose ENTIRE
            // footprint sits inside the clear circle. This prevents the
            // "small clear inside big paint wipes the whole big paint"
            // bug class: a 3m clear in the middle of a 10m AshLands paint
            // used to nuke the 10m disk (old: dist<=radius+p.Radius is true
            // for dist=0). New: requires `dist + p.Radius <= radius` —
            // the clear must surround the disk completely.
            //
            // TRADEOFF: pixel-accurate partial clear (where a small clear
            // visually erases only the overlap area inside a bigger paint)
            // requires storing "subtract" disks alongside positive ones and
            // applying them in the RebuildRenderMesh postfix. That's a
            // bigger refactor tracked separately. For now the user's most
            // common case (clear an area, repaint smaller) works because
            // wide clears do still remove fully-contained child paints.
            System.Func<float, float, bool> fullyContained =
                (dist, paintR) => (dist + paintR) <= radius;

            int removed = 0;
            if (_activePaints != null)
            {
                removed = _activePaints.RemoveAll(p =>
                {
                    float dist = Vector3.Distance(
                        new Vector3(p.X, 0, p.Z),
                        new Vector3(center.x, 0, center.z));
                    return fullyContained(dist, p.Radius);
                });
                _hasAnyPaintedAreas = _activePaints.Count > 0;
            }

            // Persist trim to TerrainComp ZDO when we own it.
            TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(center);
            if (terrainComp == null)
            {
                Heightmap hmap = Heightmap.FindHeightmap(center);
                if (hmap != null) terrainComp = hmap.GetComponent<TerrainComp>();
            }

            int zdoRemoved = 0;
            if (terrainComp != null)
            {
                var nview = terrainComp.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid() && nview.IsOwner())
                {
                    var existing = LoadBiomePaintsFromTerrainComp(terrainComp);
                    zdoRemoved = existing.RemoveAll(p =>
                    {
                        float dist = Vector3.Distance(
                            new Vector3(p.X, 0, p.Z),
                            new Vector3(center.x, 0, center.z));
                        return fullyContained(dist, p.Radius);
                    });
                    if (zdoRemoved > 0)
                    {
                        SaveBiomePaintsToTerrainComp(terrainComp, existing);
                    }
                }
            }

            // ── FILE-CACHE LAYER (the resurrection bug fix) ────────────
            //
            // BiomeFilePersistence holds a SECOND copy of biome paint data
            // in <worldName>.biomedata GZip blob, written by ZNet.SaveWorld
            // postfix. On world load, BiomeFilePersistence's
            // RestoreBiomePaintsCoroutine replays every entry via
            // PaintBiomeOverlayInternal — which re-adds to _activePaints
            // AND re-writes the TerrainComp ZDO.
            //
            // RemoveOverlapping used to skip this layer entirely. Result:
            // user clears a biome paint, restarts game, the file replays
            // the cleared entry on load and the overlay resurrects.
            //
            // Fix: call BiomeFilePersistence.RecordBiomeReset so the file
            // cache trim matches the in-memory + ZDO trim. RecordBiomeReset
            // uses the same fully-contained-vs-overlap question; we pass
            // the same circle so the predicates align (its own predicate is
            // "any overlap" today — slightly more aggressive than ours, but
            // since the in-memory + ZDO trims used the fully-contained
            // predicate, the file's bigger sweep just guarantees we never
            // resurrect anything we DID clear). User-visible effect: clear
            // persists across game restart.
            try
            {
                BiomeFilePersistence.RecordBiomeReset(center, radius);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomePaintPersistence] BiomeFilePersistence.RecordBiomeReset threw: {ex.Message}");
            }

            // Refresh vertex colors on overlapping Heightmaps so the change
            // is visible immediately without waiting for the next vanilla
            // regen tick.
            ForceHeightmapRegeneration(center, radius);

            int totalRemoved = removed + zdoRemoved;
            if (totalRemoved > 0)
            {
                Debug.Log($"[BiomePaintPersistence] RemoveOverlapping at {center} r={radius:F1} → removed {removed} in-memory + {zdoRemoved} from ZDO + file cache trimmed");
            }
            return totalRemoved;
        }
        private static bool _hasAnyPaintedAreas = false;

        #endregion

        #region Public API - Query Methods (Used by EnvironmentBoxPatches)

        /// <summary>
        /// Checks if there are any painted areas. Used for fast-path optimization.
        /// </summary>
        public static bool HasAnyPaintedAreas() => _hasAnyPaintedAreas;

        /// <summary>
        /// Gets the biome paint data affecting a specific world position.
        /// Called by EnvironmentBoxPatches.RebuildRenderMesh to apply vertex colors.
        /// Returns null if no paint affects this position.
        /// </summary>
        public static BiomePaintData GetPaintAtPosition(float worldX, float worldZ)
        {
            if (!_hasAnyPaintedAreas) return null;

            // Check all cached paints (most recent first for override behavior)
            for (int i = _activePaints.Count - 1; i >= 0; i--)
            {
                var paint = _activePaints[i];
                float dx = worldX - paint.X;
                float dz = worldZ - paint.Z;
                float distSq = dx * dx + dz * dz;
                float radiusSq = paint.Radius * paint.Radius;

                if (distSq <= radiusSq)
                {
                    return paint;
                }
            }

            return null;
        }

        #endregion

        #region Public API - Biome Painting

        /// <summary>
        /// Records a biome paint operation with multiplayer sync.
        /// The visual effect is applied by EnvironmentBoxPatches.RebuildRenderMesh.
        /// </summary>
        public static int PaintBiomeOverlay(Vector3 center, float radius, Heightmap.Biome biome)
        {
            if (_isRestoring) return 0;

            // Use RPC for multiplayer sync
            // Phase F: legacy TerrainOperationRPC dispatch removed. Always
            // apply locally; ZDO replication propagates to other peers
            // through the standard TerrainComp owner-write path inside
            // PaintBiomeOverlayInternal.
            return PaintBiomeOverlayInternal(center, radius, biome);
        }

        /// <summary>
        /// Internal biome paint - applies locally without RPC.
        /// Also clears clutter/grass for biomes that shouldn't have meadows-style vegetation.
        /// Routes persistence to correct storage based on terrain type.
        /// 
        /// BIOME PAINTING = VERTEX COLORS (controls texture blending like snow, plains grass, etc.)
        /// This is DIFFERENT from terrain paint mask which controls paved/cultivated/dirt.
        /// </summary>
        internal static int PaintBiomeOverlayInternal(Vector3 center, float radius, Heightmap.Biome biome)
        {
            EnsureReflectionCached();

            // When NOT restoring, clear existing clutter before painting biome overlay.
            // During restoration we skip this to avoid 800+ ClearInRadius calls that
            // each log "Cleared 0 clutter objects" and scan all combiners.
            if (!_isRestoring)
            {
                ClutterManager.ClearInRadius(center, radius);
            }

            int affectedVertices = 0;

            // Phase F (2026-05-25): legacy DirtFloorMeshCombiner branch
            // removed. Custom-tile biome paint flows through TileBrush.BrushBiome
            // → TileAnchor's per-vertex biome grid, NOT through this method.
            // This file now only handles the VANILLA HEIGHTMAP path; the
            // tile-side handler is in TileBrush.cs.

            // ON VANILLA TERRAIN
            // ALWAYS update in-memory cache and refresh visuals, regardless of ownership.
            // ZDO persistence is handled by the server via RPC (TerrainOperationRPC.RequestBiomePaint).
            // On dedicated servers, clients are never owners of TerrainComp, but they still
            // need local visual updates.

            var newPaint = new BiomePaintData
            {
                X = center.x,
                Y = center.y,
                Z = center.z,
                Radius = radius,
                Biome = biome
            };

            // Update in-memory cache FIRST (used by RebuildRenderMesh patches for vertex colors)
            _activePaints.Add(newPaint);
            _hasAnyPaintedAreas = true;

            // Try to persist to ZDO if we have ownership (single-player or server)
            TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(center);
            if (terrainComp == null)
            {
                Heightmap hmap = Heightmap.FindHeightmap(center);
                if (hmap != null)
                {
                    terrainComp = hmap.GetComponent<TerrainComp>();
                }
            }

            if (terrainComp != null)
            {
                var nview = terrainComp.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid() && nview.IsOwner())
                {
                    var existingPaints = LoadBiomePaintsFromTerrainComp(terrainComp);
                    existingPaints.Add(newPaint);
                    SaveBiomePaintsToTerrainComp(terrainComp, existingPaints);
                }
            }

            // Reset vanilla grass in the area
            if (ClutterSystem.instance != null)
            {
                ClutterSystem.instance.ResetGrass(center, radius);
            }

            // Only log individual paints when NOT restoring (avoids 800+ log lines on world load)
            if (!_isRestoring)
            {
                Debug.Log($"[BiomePaintPersistence] Painted {biome} on VANILLA terrain at {center}, radius {radius}");
            }

            // Force vanilla heightmaps to regenerate so our RebuildRenderMesh patch can apply colors
            ForceHeightmapRegeneration(center, radius);

            // Estimate affected vertices (rough calculation)
            return Mathf.RoundToInt(Mathf.PI * radius * radius);
        }

        /// <summary>
        /// Resets painted terrain back to original biome with multiplayer sync.
        /// </summary>
        public static int ResetBiomeOverlay(Vector3 center, float radius)
        {
            if (_isRestoring) return 0;

            // Phase F: legacy TerrainOperationRPC dispatch removed. Always
            // apply locally; the new RegionScrubber handles MP sync for the
            // visual clear via its request-pattern RPC.
            return ResetBiomeOverlayInternal(center, radius);
        }

        /// <summary>
        /// Internal biome reset - applies locally without RPC.
        /// Routes to correct storage based on terrain type.
        /// 
        /// BIOME RESET = Restore original VERTEX COLORS from world generator biome.
        /// </summary>
        internal static int ResetBiomeOverlayInternal(Vector3 center, float radius)
        {
            if (_isRestoring) return 0;

            EnsureReflectionCached();

            int removed = 0;

            // Phase F (2026-05-25): legacy DirtFloorMeshCombiner branch
            // removed. Custom-tile biome reset is handled by RegionScrubber
            // (TileBiome scope) → TileAnchor.ClearData; this method now
            // only handles the VANILLA HEIGHTMAP reset path.

            // ON VANILLA TERRAIN
            // ALWAYS update in-memory cache and refresh visuals, regardless of ownership.
            removed = _activePaints.RemoveAll(p =>
            {
                float dist = Vector3.Distance(new Vector3(p.X, 0, p.Z), new Vector3(center.x, 0, center.z));
                return dist <= radius + p.Radius;
            });
            _hasAnyPaintedAreas = _activePaints.Count > 0;

            // Try to persist to ZDO if we have ownership
            TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(center);
            if (terrainComp == null)
            {
                Heightmap hmap = Heightmap.FindHeightmap(center);
                if (hmap != null)
                {
                    terrainComp = hmap.GetComponent<TerrainComp>();
                }
            }

            if (terrainComp != null)
            {
                var nview = terrainComp.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid() && nview.IsOwner())
                {
                    var existingPaints = LoadBiomePaintsFromTerrainComp(terrainComp);
                    int zdoRemoved = existingPaints.RemoveAll(p =>
                    {
                        float dist = Vector3.Distance(new Vector3(p.X, 0, p.Z), new Vector3(center.x, 0, center.z));
                        return dist <= radius + p.Radius;
                    });
                    if (zdoRemoved > 0)
                    {
                        SaveBiomePaintsToTerrainComp(terrainComp, existingPaints);
                    }
                }
            }

            if (ClutterSystem.instance != null)
            {
                ClutterSystem.instance.ResetGrass(center, radius);
            }

            // Always force regeneration so patches can update vertex colors
            ForceHeightmapRegeneration(center, radius);

            if (removed > 0)
            {
                Debug.Log($"[BiomePaintPersistence] Reset {removed} paints on VANILLA terrain at {center}");
            }

            return removed;
        }

        #endregion

        #region Vanilla Heightmap Paint Mask

        /// <summary>
        /// Returns true if the given biome should clear grass (non-grass biomes like Mountain, AshLands, etc.)
        /// </summary>
        private static bool ShouldClearGrassForBiome(Heightmap.Biome biome)
        {
            return biome == Heightmap.Biome.Mountain ||
                   biome == Heightmap.Biome.DeepNorth ||
                   biome == Heightmap.Biome.AshLands ||
                   biome == Heightmap.Biome.Swamp ||
                   biome == Heightmap.Biome.Mistlands;
        }

        /// <summary>
        /// Paints the vanilla heightmap's paint mask texture to control grass spawning.
        /// This is the SAME thing vanilla's pave/cultivate tool does.
        /// ClutterSystem reads this mask to determine where NOT to spawn grass.
        /// 
        /// For non-grass biomes (Mountain, AshLands, etc), we paint as "paved" to clear grass.
        /// For grass biomes (Meadows, Plains, etc), we don't paint the mask.
        /// </summary>
        private static void PaintVanillaHeightmapMask(Vector3 center, float radius, Heightmap.Biome biome)
        {
            // Only clear grass for biomes that shouldn't have meadows-style grass
            bool shouldClearGrass = ShouldClearGrassForBiome(biome);

            Debug.Log($"[BiomePaintPersistence] PaintVanillaHeightmapMask: biome={biome}, shouldClearGrass={shouldClearGrass}");

            if (!shouldClearGrass) return;

            // Find the heightmap
            Heightmap hmap = Heightmap.FindHeightmap(center);
            if (hmap == null) return;

            // Get or create TerrainComp (vanilla's terrain modification component)
            // IMPORTANT: Use GetAndCreateTerrainCompiler for initial setup only
            TerrainComp tc = TerrainComp.FindTerrainCompiler(center);
            if (tc == null) return;

            try
            {
                // Access the paint mask via reflection
                var paintMaskField = typeof(TerrainComp).GetField("m_paintMask",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var modifiedField = typeof(TerrainComp).GetField("m_modifiedPaint",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var nviewField = typeof(TerrainComp).GetField("m_nview",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (paintMaskField == null) return;

                // Get the paint mask array
                var paintMask = paintMaskField.GetValue(tc) as Color[];
                if (paintMask == null)
                {
                    // Initialize paint mask if it doesn't exist
                    int maskSize = hmap.m_width + 1;
                    paintMask = new Color[maskSize * maskSize];
                    for (int i = 0; i < paintMask.Length; i++)
                    {
                        paintMask[i] = Color.black; // No paint
                    }
                    paintMaskField.SetValue(tc, paintMask);
                }

                // Get heightmap parameters
                var hmPos = hmap.transform.position;
                float scale = hmap.m_scale;
                int width = hmap.m_width;
                int resolution = width + 1;
                float halfSize = width * scale * 0.5f;
                Vector3 origin = hmPos - new Vector3(halfSize, 0f, halfSize);

                float radiusSq = radius * radius;
                bool anyChanged = false;

                // ── LAVA ALPHA-OFFSET (round-21) ─────────────────────────
                // Vanilla Heightmap.GetHeightOffset for AshLands does:
                //   lerp(GroundMaterialOffset.Ashlands=0.1m,
                //        GroundMaterialOffset.Lava=0.8m, alpha)
                // where `alpha` is the paint mask alpha at that vertex
                // (Heightmap.cs:749-757, GetGroundMaterialOffset l.962-967).
                //
                // Before this fix we wrote `m_paintMaskPaved` (alpha=1) for
                // the entire painted radius — lava surface sat at full
                // 0.8m above terrain all the way to the boundary, then
                // cliff-edged where the mask stopped. Now we vary the
                // alpha with distance so the lava surface ramps from
                // full-height at center down to ashlands-floor at the
                // edge, blending naturally into surrounding terrain.
                //
                // RGB stays paved-style (0, 0, 1) on AshLands to keep the
                // grass suppression intact. Other "paved" biomes keep
                // alpha=1 (they don't have a height-offset formula).
                bool isAshLands = (biome == Heightmap.Biome.AshLands);
                float radiusF = Mathf.Max(radius, 0.01f);

                // Paint the mask for all vertices within radius
                for (int z = 0; z < resolution; z++)
                {
                    for (int x = 0; x < resolution; x++)
                    {
                        float worldX = origin.x + x * scale;
                        float worldZ = origin.z + z * scale;

                        float dx = worldX - center.x;
                        float dz = worldZ - center.z;
                        float distSq = dx * dx + dz * dz;

                        if (distSq <= radiusSq)
                        {
                            int index = z * resolution + x;

                            if (isAshLands)
                            {
                                // Smoothstep falloff for a soft edge.
                                // t=1 at center, t=0 at boundary.
                                float dist = Mathf.Sqrt(distSq);
                                float u = 1f - dist / radiusF;
                                float t = u * u * (3f - 2f * u);
                                paintMask[index] = new Color(0f, 0f, 1f, t);
                            }
                            else
                            {
                                // Other paved biomes (Mountain, DeepNorth,
                                // Swamp, Mistlands) keep the constant
                                // paved write — they don't have a vanilla
                                // alpha→Y-offset formula.
                                paintMask[index] = Heightmap.m_paintMaskPaved;
                            }
                            anyChanged = true;
                        }
                    }
                }

                if (anyChanged)
                {
                    // Mark as modified so it gets saved
                    if (modifiedField != null)
                    {
                        var modifiedPaint = modifiedField.GetValue(tc) as bool[];
                        if (modifiedPaint == null)
                        {
                            modifiedPaint = new bool[paintMask.Length];
                            modifiedField.SetValue(tc, modifiedPaint);
                        }

                        // Mark all painted vertices as modified
                        for (int z = 0; z < resolution; z++)
                        {
                            for (int x = 0; x < resolution; x++)
                            {
                                float worldX = origin.x + x * scale;
                                float worldZ = origin.z + z * scale;

                                float dx = worldX - center.x;
                                float dz = worldZ - center.z;
                                float distSq = dx * dx + dz * dz;

                                if (distSq <= radiusSq)
                                {
                                    int index = z * resolution + x;
                                    modifiedPaint[index] = true;
                                }
                            }
                        }
                    }

                    // Save to ZDO
                    var nview = nviewField?.GetValue(tc) as ZNetView;
                    if (nview != null && nview.IsValid() && nview.IsOwner())
                    {
                        // Call Save method via reflection to persist the changes
                        var saveMethod = typeof(TerrainComp).GetMethod("Save",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        saveMethod?.Invoke(tc, null);
                    }

                    Debug.Log($"[BiomePaintPersistence] Painted vanilla heightmap mask at {center}, radius {radius}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomePaintPersistence] Failed to paint vanilla heightmap mask: {ex.Message}");
            }
        }

        /// <summary>
        /// Public 2026-05-26 round-24: OVERWRITE the vanilla heightmap paint
        /// mask with an arbitrary color inside the brush radius. Used by the
        /// `va_brush_paint` console command when the cursor is over vanilla
        /// terrain (no custom tile) — previously the brush only ever wrote
        /// to custom tiles, so painting over a vanilla paved road left the
        /// cobblestones intact and the new paint invisible.
        ///
        /// This is a UNCONDITIONAL OVERWRITE — the prior paint mask value at
        /// each affected vertex is replaced with `paintColor` outright. Use:
        ///   * (255,0,0,255) → vanilla dirt   (vanilla red channel)
        ///   * (0,255,0,255) → vanilla cultivated  (green channel)
        ///   * (0,0,255,255) → vanilla paved   (blue channel)
        ///   * (0,0,0,255)   → vanilla grass (m_paintMaskNothing)
        ///   * (0,0,0,0)     → fully cleared (also reverts to grass)
        ///
        /// Returns the number of TerrainComps touched.
        /// </summary>
        public static int OverwriteVanillaPaintMask(Vector3 center, float radius, Color paintColor)
        {
            if (radius <= 0f) return 0;

            int touched = 0;

            // Walk every TerrainComp whose footprint overlaps the brush
            // circle. Each comp covers one 64m zone; a small brush may
            // touch 1-4 comps depending on placement.
            var seen = new HashSet<TerrainComp>();
            float r = Mathf.Max(radius, 0.5f);
            Vector3[] samples =
            {
                center,
                center + new Vector3( r, 0,  0),
                center + new Vector3(-r, 0,  0),
                center + new Vector3( 0, 0,  r),
                center + new Vector3( 0, 0, -r),
                center + new Vector3( r * 0.707f, 0,  r * 0.707f),
                center + new Vector3(-r * 0.707f, 0,  r * 0.707f),
                center + new Vector3( r * 0.707f, 0, -r * 0.707f),
                center + new Vector3(-r * 0.707f, 0, -r * 0.707f),
            };

            foreach (var pt in samples)
            {
                var tc = TerrainComp.FindTerrainCompiler(pt);
                if (tc == null || !seen.Add(tc)) continue;

                var hmap = tc.m_hmap;
                if (hmap == null) continue;

                try
                {
                    var paintMaskField = typeof(TerrainComp).GetField("m_paintMask",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var modifiedField = typeof(TerrainComp).GetField("m_modifiedPaint",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var nviewField = typeof(TerrainComp).GetField("m_nview",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                    if (paintMaskField == null) continue;

                    var paintMask = paintMaskField.GetValue(tc) as Color[];
                    if (paintMask == null)
                    {
                        int maskSize = hmap.m_width + 1;
                        paintMask = new Color[maskSize * maskSize];
                        for (int i = 0; i < paintMask.Length; i++) paintMask[i] = Color.black;
                        paintMaskField.SetValue(tc, paintMask);
                    }

                    var modifiedPaint = modifiedField?.GetValue(tc) as bool[];
                    if (modifiedField != null && modifiedPaint == null)
                    {
                        modifiedPaint = new bool[paintMask.Length];
                        modifiedField.SetValue(tc, modifiedPaint);
                    }

                    var hmPos = hmap.transform.position;
                    float scale = hmap.m_scale;
                    int width = hmap.m_width;
                    int resolution = width + 1;
                    float halfSize = width * scale * 0.5f;
                    Vector3 origin = hmPos - new Vector3(halfSize, 0f, halfSize);

                    float radiusSq = radius * radius;
                    bool anyChanged = false;

                    for (int z = 0; z < resolution; z++)
                    {
                        for (int x = 0; x < resolution; x++)
                        {
                            float worldX = origin.x + x * scale;
                            float worldZ = origin.z + z * scale;
                            float dx = worldX - center.x;
                            float dz = worldZ - center.z;
                            if (dx * dx + dz * dz > radiusSq) continue;

                            int index = z * resolution + x;
                            paintMask[index] = paintColor;
                            if (modifiedPaint != null) modifiedPaint[index] = true;
                            anyChanged = true;
                        }
                    }

                    if (!anyChanged) continue;

                    var nview = nviewField?.GetValue(tc) as ZNetView;
                    if (nview != null && nview.IsValid() && nview.IsOwner())
                    {
                        var saveMethod = typeof(TerrainComp).GetMethod("Save",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        saveMethod?.Invoke(tc, null);
                    }

                    touched++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BiomePaintPersistence.OverwriteVanillaPaintMask] threw on TerrainComp: {ex.Message}");
                }
            }

            // Force regen so the new paint shows up immediately. Otherwise
            // the player has to walk away + back for the vertex colors to
            // refresh from the patched paint mask.
            ForceHeightmapRegeneration(center, radius);

            Debug.Log($"[BiomePaintPersistence] OverwriteVanillaPaintMask at {center}, r={radius:F1}, color={paintColor} → {touched} TerrainComp(s)");
            return touched;
        }

        #endregion

        #region Biome Paint ZDO Save/Load

        private static List<BiomePaintData> LoadBiomePaintsFromTerrainComp(TerrainComp terrainComp)
        {
            var paints = new List<BiomePaintData>();

            try
            {
                var nview = terrainComp.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return paints;

                var zdo = nview.GetZDO();
                if (zdo == null) return paints;

                byte[] data = zdo.GetByteArray(ZDO_BIOME_PAINTS);
                if (data == null || data.Length == 0) return paints;

                byte[] decompressed = Utils.Decompress(data);
                var pkg = new ZPackage(decompressed);

                int version = pkg.ReadInt();
                if (version != DATA_VERSION)
                {
                    Debug.LogWarning($"[BiomePaintPersistence] Unknown biome data version {version}");
                    return paints;
                }

                int count = pkg.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    paints.Add(new BiomePaintData
                    {
                        X = pkg.ReadSingle(),
                        Y = pkg.ReadSingle(),
                        Z = pkg.ReadSingle(),
                        Radius = pkg.ReadSingle(),
                        Biome = (Heightmap.Biome)pkg.ReadInt()
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomePaintPersistence] Failed to load biome paints: {ex.Message}");
            }

            return paints;
        }

        private static void SaveBiomePaintsToTerrainComp(TerrainComp terrainComp, List<BiomePaintData> paints)
        {
            try
            {
                var nview = terrainComp.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

                var zdo = nview.GetZDO();
                if (zdo == null) return;

                var pkg = new ZPackage();
                pkg.Write(DATA_VERSION);
                pkg.Write(paints.Count);

                foreach (var paint in paints)
                {
                    pkg.Write(paint.X);
                    pkg.Write(paint.Y);
                    pkg.Write(paint.Z);
                    pkg.Write(paint.Radius);
                    pkg.Write((int)paint.Biome);
                }

                byte[] compressed = Utils.Compress(pkg.GetArray());
                zdo.Set(ZDO_BIOME_PAINTS, compressed);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomePaintPersistence] Failed to save biome paints: {ex.Message}");
            }
        }

        #endregion

        #region Public API - Clutter Painting

        /// <summary>
        /// Records a clutter paint operation and saves to TerrainComp ZDO.
        /// Uses RPC if not owner of TerrainComp.
        /// </summary>
        public static void RecordClutterPaint(Vector3 center, float radius, string clutterType, float density)
        {
            if (_isRestoring) return;

            TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(center);
            if (terrainComp == null)
            {
                Heightmap hmap = Heightmap.FindHeightmap(center);
                if (hmap != null)
                {
                    // Only get existing TerrainComp, don't create
                    terrainComp = hmap.GetComponent<TerrainComp>();
                }
            }

            if (terrainComp == null)
            {
                // No TerrainComp exists yet at this position. This is normal for areas
                // where no vanilla terrain modifications (level/raise/paint) have been made.
                // The clutter will still display visually but won't persist across zone reload
                // until the terrain is modified (which creates a TerrainComp).
                return;
            }

            // Check if we can write directly or need to use RPC
            var nview = terrainComp.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                Debug.LogWarning($"[BiomePaintPersistence] TerrainComp has no valid ZNetView at {center}");
                return;
            }

            if (!nview.IsOwner())
            {
                // Phase F: legacy TerrainPaintRPC.RequestClutterPaint
                // removed. Non-owners now no-op — clutter MP sync waits on
                // Phase D5 (FAP source-link clutter) which will route
                // through RegionScrubberRPC.
                return;
            }

            // We're the owner - save directly
            RecordClutterPaintDirect(center, radius, clutterType, density);
        }

        /// <summary>
        /// Records a clutter paint directly without ownership check.
        /// Called by RPC handler on server after ownership is verified.
        /// </summary>
        public static void RecordClutterPaintDirect(Vector3 center, float radius, string clutterType, float density)
        {
            TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(center);
            if (terrainComp == null)
            {
                Heightmap hmap = Heightmap.FindHeightmap(center);
                if (hmap != null)
                {
                    // Only get existing TerrainComp, don't create
                    terrainComp = hmap.GetComponent<TerrainComp>();
                }
            }

            if (terrainComp == null) return;

            var existingPaints = LoadClutterPaintsFromTerrainComp(terrainComp);

            existingPaints.Add(new ClutterPaintData
            {
                X = center.x,
                Y = center.y,
                Z = center.z,
                Radius = radius,
                ClutterType = clutterType,
                Density = density
            });

            SaveClutterPaintsToTerrainCompDirect(terrainComp, existingPaints);

            Debug.Log($"[BiomePaintPersistence] Recorded clutter paint: {clutterType} at {center}, radius {radius}");
        }

        /// <summary>
        /// Records clutter clear operation.
        /// Uses RPC if not owner of TerrainComp.
        /// </summary>
        public static void RecordClutterClear(Vector3 center, float radius)
        {
            TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(center);
            if (terrainComp == null) return;

            var nview = terrainComp.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            if (!nview.IsOwner())
            {
                // Phase F: TerrainPaintRPC.RequestClutterClear removed.
                // Non-owners no-op; clutter clear MP sync handled via
                // RegionScrubber's Clutter scope (calls
                // ClutterManager.ClearInRadius locally on every peer).
                return;
            }

            RecordClutterClearDirect(center, radius);
        }

        /// <summary>
        /// Records clutter clear directly without ownership check.
        /// Called by RPC handler on server after ownership is verified.
        /// </summary>
        public static void RecordClutterClearDirect(Vector3 center, float radius)
        {
            TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(center);
            if (terrainComp == null) return;

            var existingPaints = LoadClutterPaintsFromTerrainComp(terrainComp);

            int removed = existingPaints.RemoveAll(p =>
            {
                float dist = Vector3.Distance(new Vector3(p.X, 0, p.Z), center);
                return dist <= radius + p.Radius;
            });

            if (removed > 0)
            {
                SaveClutterPaintsToTerrainCompDirect(terrainComp, existingPaints);
                Debug.Log($"[BiomePaintPersistence] Removed {removed} clutter paints at {center}");
            }
        }

        /// <summary>
        /// Clears ALL clutter persistence data from ALL loaded TerrainComps.
        /// Used by ClearAllClutter to ensure clutter doesn't respawn on zone reload.
        /// </summary>
        public static void ClearAllClutterPersistence()
        {
            int clearedComps = 0;

            // Find all loaded TerrainComps and clear their clutter data
            var terrainComps = UnityEngine.Object.FindObjectsByType<TerrainComp>(UnityEngine.FindObjectsSortMode.None);
            foreach (var tc in terrainComps)
            {
                if (tc == null) continue;

                var nview = tc.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                var zdo = nview.GetZDO();
                if (zdo == null) continue;

                // Check if this TerrainComp has clutter data
                byte[] data = zdo.GetByteArray(ZDO_CLUTTER_PAINTS);
                if (data != null && data.Length > 0)
                {
                    // Only clear if we're the owner
                    if (nview.IsOwner())
                    {
                        // Save empty clutter list
                        SaveClutterPaintsToTerrainCompDirect(tc, new List<ClutterPaintData>());
                        clearedComps++;
                    }
                }
            }

            Debug.Log($"[BiomePaintPersistence] Cleared clutter persistence from {clearedComps} TerrainComps");
        }

        /// <summary>
        /// Records biome paint directly without ownership check.
        /// Called by RPC handler on server after ownership is verified.
        /// </summary>
        public static void RecordBiomePaintDirect(Vector3 center, float radius, Heightmap.Biome biome)
        {
            EnsureReflectionCached();

            TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(center);
            if (terrainComp == null)
            {
                Heightmap hmap = Heightmap.FindHeightmap(center);
                if (hmap != null)
                {
                    // Only get existing TerrainComp, don't create
                    terrainComp = hmap.GetComponent<TerrainComp>();
                }
            }

            if (terrainComp == null) return;

            var existingPaints = LoadBiomePaintsFromTerrainComp(terrainComp);

            // Remove overlapping paints
            existingPaints.RemoveAll(p =>
            {
                float dist = Vector3.Distance(new Vector3(p.X, 0, p.Z), new Vector3(center.x, 0, center.z));
                return dist <= radius + p.Radius;
            });

            var newPaint = new BiomePaintData
            {
                X = center.x,
                Y = center.y,
                Z = center.z,
                Radius = radius,
                Biome = biome
            };
            existingPaints.Add(newPaint);

            SaveBiomePaintsToTerrainCompDirect(terrainComp, existingPaints);

            // Update in-memory cache
            _activePaints.RemoveAll(p =>
            {
                float dist = Vector3.Distance(new Vector3(p.X, 0, p.Z), new Vector3(center.x, 0, center.z));
                return dist <= radius + p.Radius;
            });
            _activePaints.Add(newPaint);
            _hasAnyPaintedAreas = true;

            // NOTE: Do NOT call hmap.Poke() - it causes terrain compiler spam

            Debug.Log($"[BiomePaintPersistence] Recorded biome paint: {biome} at {center}, radius {radius}");
        }

        /// <summary>
        /// Records biome clear directly without ownership check.
        /// </summary>
        public static void RecordBiomeClearDirect(Vector3 center, float radius)
        {
            EnsureReflectionCached();

            TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(center);
            if (terrainComp == null) return;

            var existingPaints = LoadBiomePaintsFromTerrainComp(terrainComp);
            int removed = existingPaints.RemoveAll(p =>
            {
                float dist = Vector3.Distance(new Vector3(p.X, 0, p.Z), new Vector3(center.x, 0, center.z));
                return dist <= radius + p.Radius;
            });

            if (removed > 0)
            {
                SaveBiomePaintsToTerrainCompDirect(terrainComp, existingPaints);

                _activePaints.RemoveAll(p =>
                {
                    float dist = Vector3.Distance(new Vector3(p.X, 0, p.Z), new Vector3(center.x, 0, center.z));
                    return dist <= radius + p.Radius;
                });
                _hasAnyPaintedAreas = _activePaints.Count > 0;

                // NOTE: Do NOT call hmap.Poke() - it causes terrain compiler spam

                Debug.Log($"[BiomePaintPersistence] Cleared {removed} biome paints at {center}");
            }
        }

        #endregion

        #region Clutter ZDO Save/Load

        private static List<ClutterPaintData> LoadClutterPaintsFromTerrainComp(TerrainComp terrainComp)
        {
            var paints = new List<ClutterPaintData>();

            try
            {
                var nview = terrainComp.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return paints;

                var zdo = nview.GetZDO();
                if (zdo == null) return paints;

                byte[] data = zdo.GetByteArray(ZDO_CLUTTER_PAINTS);
                if (data == null || data.Length == 0) return paints;

                byte[] decompressed = Utils.Decompress(data);
                var pkg = new ZPackage(decompressed);

                int version = pkg.ReadInt();
                if (version != DATA_VERSION) return paints;

                int count = pkg.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    paints.Add(new ClutterPaintData
                    {
                        X = pkg.ReadSingle(),
                        Y = pkg.ReadSingle(),
                        Z = pkg.ReadSingle(),
                        Radius = pkg.ReadSingle(),
                        ClutterType = pkg.ReadString(),
                        Density = pkg.ReadSingle()
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomePaintPersistence] Failed to load clutter paints: {ex.Message}");
            }

            return paints;
        }

        private static void SaveClutterPaintsToTerrainComp(TerrainComp terrainComp, List<ClutterPaintData> paints)
        {
            try
            {
                var nview = terrainComp.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

                SaveClutterPaintsToTerrainCompDirect(terrainComp, paints);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomePaintPersistence] Failed to save clutter paints: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves clutter paints directly without ownership check.
        /// Used by Direct methods after ownership is verified.
        /// </summary>
        private static void SaveClutterPaintsToTerrainCompDirect(TerrainComp terrainComp, List<ClutterPaintData> paints)
        {
            try
            {
                var nview = terrainComp.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;

                var zdo = nview.GetZDO();
                if (zdo == null) return;

                var pkg = new ZPackage();
                pkg.Write(DATA_VERSION);
                pkg.Write(paints.Count);

                foreach (var paint in paints)
                {
                    pkg.Write(paint.X);
                    pkg.Write(paint.Y);
                    pkg.Write(paint.Z);
                    pkg.Write(paint.Radius);
                    pkg.Write(paint.ClutterType ?? "");
                    pkg.Write(paint.Density);
                }

                byte[] compressed = Utils.Compress(pkg.GetArray());
                zdo.Set(ZDO_CLUTTER_PAINTS, compressed);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomePaintPersistence] Failed to save clutter paints: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves biome paints directly without ownership check.
        /// Used by Direct methods after ownership is verified.
        /// </summary>
        private static void SaveBiomePaintsToTerrainCompDirect(TerrainComp terrainComp, List<BiomePaintData> paints)
        {
            try
            {
                var nview = terrainComp.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;

                var zdo = nview.GetZDO();
                if (zdo == null) return;

                var pkg = new ZPackage();
                pkg.Write(DATA_VERSION);
                pkg.Write(paints.Count);

                foreach (var paint in paints)
                {
                    pkg.Write(paint.X);
                    pkg.Write(paint.Y);
                    pkg.Write(paint.Z);
                    pkg.Write(paint.Radius);
                    pkg.Write((int)paint.Biome);
                }

                byte[] compressed = Utils.Compress(pkg.GetArray());
                zdo.Set(ZDO_BIOME_PAINTS, compressed);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomePaintPersistence] Failed to save biome paints: {ex.Message}");
            }
        }

        #endregion

        #region Restoration (Zone Load)

        /// <summary>
        /// Restores biome and clutter paints for a TerrainComp when it loads.
        /// Biome paints are loaded into memory cache for use by EnvironmentBoxPatches.
        /// Clutter paints are spawned with a delay to ensure ClutterSystem is ready.
        /// </summary>
        public static void RestoreFromTerrainComp(TerrainComp terrainComp)
        {
            if (terrainComp == null) return;

            int instanceId = terrainComp.GetInstanceID();
            if (_restoredTerrainComps.Contains(instanceId)) return;

            _restoredTerrainComps.Add(instanceId);

            // Load biome paints into memory cache (immediate - doesn't need ClutterSystem)
            var biomePaints = LoadBiomePaintsFromTerrainComp(terrainComp);
            if (biomePaints.Count > 0)
            {
                _activePaints.AddRange(biomePaints);
                _hasAnyPaintedAreas = true;
                Debug.Log($"[BiomePaintPersistence] Loaded {biomePaints.Count} biome paints from TerrainComp at {terrainComp.transform.position}");

                // NOTE: Do NOT call hmap.Poke() here!
                // Poke() internally calls GetAndCreateTerrainCompiler() which creates duplicate TerrainComps
                // causing the "Found another terrain compiler" warning spam.
                // The heightmap will regenerate naturally as part of zone loading.
            }

            // Load clutter paints - defer spawning until ClutterSystem is ready
            var clutterPaints = LoadClutterPaintsFromTerrainComp(terrainComp);
            if (clutterPaints.Count > 0)
            {
                // Use coroutine to wait for ClutterSystem to be ready
                ClutterRestoreHelper.RestoreClutterDelayed(clutterPaints, terrainComp.transform.position);
            }
        }

        /// <summary>
        /// Called when a TerrainComp is destroyed (zone unloading).
        /// Removes tracking but doesn't clear in-memory paint cache since
        /// the TerrainComp might be reloaded when player returns to the area.
        /// </summary>
        public static void OnTerrainCompDestroyed(TerrainComp terrainComp)
        {
            if (terrainComp == null) return;

            int instanceId = terrainComp.GetInstanceID();
            _restoredTerrainComps.Remove(instanceId);

            // Note: We keep _activePaints since the data is still valid
            // and will be needed if the player returns to this area.
            // The ZDO data persists independently.

            if (_spawnedClutterByTerrainComp.TryGetValue(instanceId, out var clutterList))
            {
                foreach (var obj in clutterList)
                {
                    if (obj != null) UnityEngine.Object.Destroy(obj);
                }
                _spawnedClutterByTerrainComp.Remove(instanceId);
            }
        }

        /// <summary>
        /// Called when world unloads (logout, main menu).
        /// Clears all in-memory state.
        /// </summary>
        public static void OnWorldUnload()
        {
            _restoredTerrainComps.Clear();
            _activePaints.Clear();
            _hasAnyPaintedAreas = false;

            foreach (var kvp in _spawnedClutterByTerrainComp)
            {
                foreach (var obj in kvp.Value)
                {
                    if (obj != null) UnityEngine.Object.Destroy(obj);
                }
            }
            _spawnedClutterByTerrainComp.Clear();

            Debug.Log("[BiomePaintPersistence] World unloaded - cleared all paint caches");
        }

        /// <summary>
        /// Checks if we're currently restoring data (prevents re-recording).
        /// Used by ClutterPaintTool to know not to record during restoration.
        /// </summary>
        public static bool IsRestoring => _isRestoring;

        /// <summary>
        /// Sets the restoring flag (used by ClutterRestoreHelper).
        /// </summary>
        internal static void SetRestoring(bool value) => _isRestoring = value;

        // Legacy API compatibility
        public static void Update() { }
        public static void SaveCache() { }

        /// <summary>
        /// Forces nearby heightmaps to regenerate their meshes.
        /// This triggers our RebuildRenderMesh patch to apply biome paint colors.
        /// </summary>
        private static void ForceHeightmapRegeneration(Vector3 center, float radius)
        {
            // Find all heightmaps that might be affected
            var heightmaps = Heightmap.GetAllHeightmaps();
            if (heightmaps == null) return;

            foreach (var hmap in heightmaps)
            {
                if (hmap == null) continue;

                // Check if this heightmap overlaps with our paint area
                var hmPos = hmap.transform.position;
                float hmSize = hmap.m_width * hmap.m_scale * 0.5f;

                // Simple distance check (heightmap center to paint center)
                float dist = Vector3.Distance(
                    new Vector3(hmPos.x, 0, hmPos.z),
                    new Vector3(center.x, 0, center.z)
                );

                // If within range, regenerate
                if (dist <= radius + hmSize + 10f)
                {
                    hmap.Regenerate();
                }
            }
        }

        #endregion

        #region Clutter Restore Helper

        /// <summary>
        /// Helper class that handles delayed clutter restoration using a MonoBehaviour.
        /// </summary>
        private class ClutterRestoreHelper : MonoBehaviour
        {
            private static ClutterRestoreHelper _instance;
            private static readonly Queue<(List<ClutterPaintData> paints, Vector3 position)> _pendingRestores =
                new Queue<(List<ClutterPaintData>, Vector3)>();

            public static void RestoreClutterDelayed(List<ClutterPaintData> paints, Vector3 position)
            {
                EnsureInstance();
                _pendingRestores.Enqueue((paints, position));
            }

            private static void EnsureInstance()
            {
                if (_instance != null) return;

                var go = new GameObject("BiomePaint_ClutterRestoreHelper");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _instance = go.AddComponent<ClutterRestoreHelper>();
            }

            private float _checkTimer = 0f;
            private const float CHECK_INTERVAL = 0.5f;

            private void Update()
            {
                if (_pendingRestores.Count == 0) return;

                _checkTimer += Time.deltaTime;
                if (_checkTimer < CHECK_INTERVAL) return;
                _checkTimer = 0f;

                // Check if ClutterSystem is ready
                if (ClutterSystem.instance == null) return;
                if (ClutterSystem.instance.m_clutter == null || ClutterSystem.instance.m_clutter.Count == 0) return;

                // Process all pending restores
                SetRestoring(true);
                int totalPaints = 0;
                int totalInstances = 0;

                try
                {
                    while (_pendingRestores.Count > 0)
                    {
                        var (paints, position) = _pendingRestores.Dequeue();

                        foreach (var paint in paints)
                        {
                            // Phase F: legacy ClutterPaintTool.PaintClutter
                            // removed. Painted-clutter restoration now
                            // deferred to Phase D5 (FAP source-link clutter)
                            // — until then, persisted clutter loads as data
                            // but doesn't visually respawn. The byte arrays
                            // stay in TerrainComp ZDOs so D5 can read them.
                            VerdantsAscent.Terrain.Clutter.ClutterManager
                                .PaintClutter(paint.Position, paint.Radius, paint.ClutterType, paint.Density);
                            totalPaints++;
                        }
                    }

                    if (totalPaints > 0)
                    {
                        Debug.Log($"[BiomePaintPersistence] Restored {totalPaints} clutter paints ({totalInstances} instances spawned)");
                    }
                }
                finally
                {
                    SetRestoring(false);
                }
            }

            private void OnDestroy()
            {
                _instance = null;
                _pendingRestores.Clear();
            }
        }

        #endregion

        #region Data Structures

        /// <summary>
        /// A single painted biome overlay operation.
        /// </summary>
        public class BiomePaintData
        {
            public float X;
            public float Y;
            public float Z;
            public float Radius;
            public Heightmap.Biome Biome;

            public Vector3 Position => new Vector3(X, Y, Z);
        }

        /// <summary>
        /// A single painted clutter operation.
        /// </summary>
        public class ClutterPaintData
        {
            public float X;
            public float Y;
            public float Z;
            public float Radius;
            public string ClutterType;
            public float Density;

            public Vector3 Position => new Vector3(X, Y, Z);
        }

        #endregion

        #region Harmony Patches

        [HarmonyPatch]
        public static class BiomePaintPatches
        {
            /// <summary>
            /// Patch Heightmap.RebuildRenderMesh to apply our biome paint colors.
            /// This runs AFTER vanilla builds the mesh, so we can override vertex colors.
            /// </summary>
            [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last - 5)] // Run after other patches but before EnvironmentBoxPatches
            public static void Heightmap_RebuildRenderMesh_Postfix(Heightmap __instance)
            {
                // Fast exit if no painted areas
                if (!_hasAnyPaintedAreas) return;

                try
                {
                    // Get the render mesh via reflection
                    if (_renderMeshField == null)
                    {
                        _renderMeshField = typeof(Heightmap).GetField("m_renderMesh",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    }

                    var renderMesh = _renderMeshField?.GetValue(__instance) as Mesh;
                    if (renderMesh == null || renderMesh.vertexCount == 0) return;

                    // Get heightmap properties for UV calculation
                    var hmPos = __instance.transform.position;
                    float scale = __instance.m_scale;
                    int width = __instance.m_width;
                    float halfSize = width * scale * 0.5f;
                    int num = width + 1;

                    // Calculate origin (bottom-left corner)
                    Vector3 origin = hmPos - new Vector3(halfSize, 0f, halfSize);

                    // Get current colors
                    Color32[] colors = renderMesh.colors32;
                    if (colors == null || colors.Length != num * num)
                    {
                        colors = new Color32[num * num];
                    }

                    bool anyChanged = false;

                    // Check each vertex for biome paint
                    for (int z = 0; z < num; z++)
                    {
                        for (int x = 0; x < num; x++)
                        {
                            float worldX = origin.x + x * scale;
                            float worldZ = origin.z + z * scale;

                            // Check if there's a biome paint at this position
                            var paint = GetPaintAtPosition(worldX, worldZ);
                            if (paint != null)
                            {
                                int index = z * num + x;
                                colors[index] = Heightmap.GetBiomeColor(paint.Biome);
                                anyChanged = true;
                            }
                        }
                    }

                    if (anyChanged)
                    {
                        renderMesh.colors32 = colors;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[BiomePaintPersistence] Error in RebuildRenderMesh patch: {ex.Message}");
                }
            }

            // Reflection cache for render mesh field
            private static System.Reflection.FieldInfo _renderMeshField;

            /// <summary>
            /// Patch TerrainComp.Load to restore our biome/clutter data.
            /// </summary>
            [HarmonyPatch(typeof(TerrainComp), "Load")]
            [HarmonyPostfix]
            public static void TerrainComp_Load_Postfix(TerrainComp __instance, bool __result)
            {
                if (!__result) return;

                try
                {
                    RestoreFromTerrainComp(__instance);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BiomePaintPersistence] Error in TerrainComp_Load: {ex.Message}");
                }
            }

            [HarmonyPatch(typeof(TerrainComp), "OnDestroy")]
            [HarmonyPostfix]
            public static void TerrainComp_OnDestroy_Postfix(TerrainComp __instance)
            {
                try
                {
                    OnTerrainCompDestroyed(__instance);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BiomePaintPersistence] Error in TerrainComp_OnDestroy: {ex.Message}");
                }
            }

            [HarmonyPatch(typeof(ZNetScene), "OnDestroy")]
            [HarmonyPrefix]
            public static void ZNetScene_OnDestroy_Prefix()
            {
                OnWorldUnload();
            }
        }

        #endregion
    }
}
