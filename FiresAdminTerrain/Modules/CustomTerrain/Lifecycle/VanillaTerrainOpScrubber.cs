using UnityEngine;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Called from the Priority.Low postfix of FAT's one TerrainOp.Awake hook
    // (FatVanillaHooks). When the player uses the
    // vanilla hoe (or any other vanilla terrain tool — IH, WEC, MVBP) to
    // paint dirt/cultivated/paved on vanilla terrain, we clear OUR biome
    // overlay + painted clutter + vanilla grass in the same radius so the
    // new vanilla paint shows clean.
    //
    // User directive 2026-05-25: "vanilla / infinity hammer terrain ops like
    // dirt and paved should clear our biome overlay and place their paint".
    //
    // Boundaries:
    //   * Does NOT touch our custom-tile data (TileBrush.BrushClear is for
    //     the player to use explicitly via va_brush_clear). The user picked
    //     the vanilla tool, they're modifying vanilla — the symmetric
    //     scope is vanilla-side overlays only.
    //   * Skipped when TileTerrainOpBlocker already rerouted the op (i.e.
    //     the op was OVER a custom tile). In that case the op was destroyed
    //     before Awake completed and our postfix never fires on it.
    //   * Only fires for paint ops (m_paintCleared). Raise/Lower/Smooth
    //     don't drag biome overlay with them — leave them alone.
    internal static class VanillaTerrainOpScrubber
    {
        internal static void OnTerrainOpAwake(TerrainOp __instance)
        {
            if (__instance == null) return;
            if (!__instance) return; // op may already be destroyed by TileTerrainOpBlocker
            if (VerdantsAscent.Terrain.Zones.ZoneModeRefusals.WasCancelled(__instance)) return;

            var s = __instance.m_settings;
            if (s == null) return;
            if (!s.m_paintCleared) return;
            if (!IsSafeToScrub()) return;

            Vector3 position = __instance.transform.position;
            float radius = Mathf.Max(s.m_paintRadius, 0.5f);
            ScrubPaintOverlay(position, radius, "TerrainOp.Awake");
        }

        private static void ScrubPaintOverlay(Vector3 position, float radius, string callSite)
        {
            var scope = RegionScrubber.Scope.VanillaBiome
                      | RegionScrubber.Scope.Clutter
                      | RegionScrubber.Scope.VanillaGrass;
            int n = RegionScrubber.ScrubRegion(
                position, radius, scope, RegionScrubber.Source.VanillaTerrainOp);
            if (n > 0)
            {
                FiresLogger.LogVerbose($"[VanillaTerrainOpScrubber/{callSite}] Vanilla paint at {position} r={radius:F1} → scrubbed {n} overlay/clutter/grass entry/entries.");
            }
        }

        // Skip scrubs that come from startup / login init paths.
        //
        // Two failure modes the guard catches:
        //   1. Legacy ClutterPaintTool fires a TerrainOp at world origin
        //      (0, ~50, 0) during player-spawn init. Without this guard the
        //      scrubber unnecessarily wipes any biome paint that happens to
        //      live near (0,0,0). Visible in the user log 2026-05-25 as
        //      two `Vanilla paint at (0.00, 50.00, 0.00)` lines back-to-back
        //      on every login.
        //   2. Vanilla heightmap init occasionally instantiates a no-op
        //      TerrainOp during world load — these should NOT count as
        //      "the user painted, clear our overlays".
        //
        // Heuristic: the local player must exist AND have been alive long
        // enough to have made a real input. 5 seconds since spawn covers
        // both the initial TerrainOp burst and the brief window where the
        // player can't have done anything yet anyway.
        private static float _spawnTime = -1f;

        private static bool IsSafeToScrub()
        {
            var p = Player.m_localPlayer;
            if (p == null) return false;

            if (_spawnTime < 0f) _spawnTime = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - _spawnTime < 5f) return false;

            return true;
        }

        // Reset the spawn-time gate when the local player respawns (logout
        // or death), from FAT's one Player.OnSpawned hook. Cheap.
        internal static void ResetSpawnTime() => _spawnTime = Time.realtimeSinceStartup;

        // Lower-level hook: WEC / Infinity Hammer's "terrain paint=…" console
        // command calls TerrainComp.DoOperation DIRECTLY on the matching
        // TerrainComp without instantiating a TerrainOp prefab. That bypasses
        // the TerrainOp.Awake postfix above, which is why IH paint operations
        // didn't clear our biome overlay in the user's 2026-05-25 report.
        //
        // Hooking DoOperation catches:
        //   * WEC / IH `terrain paint=…` console paths (the common case here)
        //   * Direct TerrainComp.DoOperation calls from any other mod that
        //     paints without going through TerrainOp
        //   * Vanilla TerrainOp.Apply (which itself ends up calling
        //     DoOperation) — those will double-fire the scrub (once here,
        //     once via TerrainOp.Awake above) but it's idempotent so harmless.
        //
        // The Priority.Low postfix of FAT's one TerrainComp.DoOperation hook
        // (FatVanillaHooks), ahead of the environment-box clutter mask.
        internal static void OnDoOperation(Vector3 pos, TerrainOp.Settings modifier)
        {
            if (modifier == null) return;
            if (!modifier.m_paintCleared) return;

            // Same login-grace guard as the TerrainOp.Awake path: skips
            // spurious paint ops that fire during world load / player spawn
            // before the user could have taken any input.
            if (!IsSafeToScrub()) return;

            ScrubPaintOverlay(pos, Mathf.Max(modifier.m_paintRadius, 0.5f), "DoOperation");
        }
    }
}
