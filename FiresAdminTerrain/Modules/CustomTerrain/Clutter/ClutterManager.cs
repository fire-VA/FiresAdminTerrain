using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Clutter
{
    // Compatibility shim left in place after Phase F decommissioned the
    // legacy DirtFloor / ClutterPaintTool / GrassOverlayController stack.
    //
    // Why this file still exists:
    //   * RegionScrubber calls ClearInRadius (Clutter scope).
    //   * BiomePaintPersistence and a few other KEEP files
    //     call PaintClutter / CycleNext / AllClutterTypes / etc. as a
    //     compile-time surface.
    //   * Deleting the class wholesale would force surgery into every
    //     one of those callers and balloon the Phase F diff.
    //
    // What it does today: every method is a NO-OP that logs once at
    // verbose level. ClearInRadius still calls ClutterSystem.ResetGrass
    // (vanilla) because that's all the scrubber needs for the user-visible
    // clear-grass behavior. Custom clutter SPAWN paths are deferred to
    // Phase D5 (FAP source-link clutter); once that lands, the stubs in
    // here grow real bodies.
    //
    // Net behavior change vs the legacy implementation:
    //   - "painted clutter" no longer respawns or persists. Existing
    //     painted-clutter ZDO bytes from a previous session load as dead
    //     data and never visually re-spawn until Phase D5.
    //   - "biome paint" no longer re-rolls custom clutter on biome change
    //     (still triggers vanilla grass roll via ClutterSystem.ResetGrass).
    // None of these break anything visually for tile-anchor terrain; the
    // user's primary paint workflow (Floor, Raise, Paint, Biome) is
    // entirely on the new path.
    public static class ClutterManager
    {
        // ── Spawn / Paint stubs ───────────────────────────────────────────

        public static int PaintClutter(Vector3 center, float radius, string clutterType, float density = 0.5f)
        {
            FiresLogger.LogVerbose($"[ClutterManager] PaintClutter({clutterType}) — DEFERRED to Phase D5 (FAP clutter source-link). center={center} r={radius:F1}");
            return 0;
        }

        public static string CurrentClutterType
        {
            get => string.Empty;
            set { /* no-op until D5 */ }
        }

        public static void CycleNext()      { /* no-op until D5 */ }
        public static void CyclePrevious()  { /* no-op until D5 */ }
        public static void EnableCycling()  { /* no-op until D5 */ }
        public static void DisableCycling() { /* no-op until D5 */ }

        // ── Clear ──────────────────────────────────────────────────────────
        //
        // RegionScrubber's Clutter scope routes here. Still safe to call —
        // we fall through to vanilla ClutterSystem.ResetGrass which is what
        // the user actually sees for "remove grass in radius".
        public static void ClearInRadius(Vector3 center, float radius)
        {
            if (ClutterSystem.instance != null)
            {
                ClutterSystem.instance.ResetGrass(center, radius);
            }
        }

        public static int ClearAllPainted()
        {
            FiresLogger.LogVerbose("[ClutterManager] ClearAllPainted — no-op (painted-clutter system retired; D5 will replace).");
            return 0;
        }

        public static void ClearInRadiusPermanent(Vector3 center, float radius)
        {
            ClearInRadius(center, radius);
        }

        // ── Refresh ───────────────────────────────────────────────────────

        public static void RespawnInRadius(Vector3 center, float radius)
        {
            // Legacy "respawn grass on every combiner overlapping radius".
            // No combiners now → vanilla grass re-roll only.
            if (ClutterSystem.instance != null)
            {
                ClutterSystem.instance.ResetGrass(center, radius);
            }
        }

        public static void RefreshInRadius(Vector3 center, float radius)
        {
            if (ClutterSystem.instance != null)
            {
                ClutterSystem.instance.ResetGrass(center, radius);
            }
        }

        // ── Queries ───────────────────────────────────────────────────────

        public static IReadOnlyList<string> AllClutterTypes => System.Array.Empty<string>();

        public static Dictionary<string, List<string>> GetClutterByCategory()
        {
            return new Dictionary<string, List<string>>();
        }

        public static string GetDefaultGrassForBiome(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows:     return "instanced_meadows_grass";
                case Heightmap.Biome.BlackForest: return "instanced_forest_groundcover";
                case Heightmap.Biome.Swamp:       return "instanced_swamp_grass";
                case Heightmap.Biome.Plains:      return "instanced_heathgrass";
                case Heightmap.Biome.Mistlands:   return "instanced_mistlands_grass_short";
                case Heightmap.Biome.AshLands:    return "instanced_ashlands_fern";
                case Heightmap.Biome.Mountain:
                case Heightmap.Biome.DeepNorth:   return null;
                default:                          return "instanced_meadows_grass";
            }
        }

        public static bool ShouldHaveClutter(Vector3 worldPos)
        {
            // Vanilla heightmap "cleared" check still applies. Custom-tile
            // paint-mask query waits for D1 (per-tile paint texture).
            var hm = Heightmap.FindHeightmap(worldPos);
            if (hm != null) return !hm.IsCleared(worldPos);
            return true;
        }

        // ── Persistence hooks (kept for BiomePaintPersistence) ────────────

        public static void OnTerrainCompLoaded(TerrainComp terrainComp)
        {
            VerdantsAscent.Terrain.Biome.BiomePaintPersistence.RestoreFromTerrainComp(terrainComp);
        }

        public static void OnTerrainCompDestroyed(TerrainComp terrainComp)
        {
            VerdantsAscent.Terrain.Biome.BiomePaintPersistence.OnTerrainCompDestroyed(terrainComp);
        }

        public static void SavePending()
        {
            VerdantsAscent.Terrain.Biome.BiomePaintPersistence.SaveCache();
        }

        // ── Undo support (TerrainUndoManager calls this) ──────────────────

        public class ClutterInstanceData
        {
            public Vector3 Position;
            public string ClutterType;
            public float Density;
        }

        public static List<ClutterInstanceData> GetClutterInRadius(Vector3 center, float radius)
        {
            return new List<ClutterInstanceData>();
        }

        public static void RestoreClutterState(List<ClutterInstanceData> clutterData)
        {
            // No-op until D5.
        }
    }
}
