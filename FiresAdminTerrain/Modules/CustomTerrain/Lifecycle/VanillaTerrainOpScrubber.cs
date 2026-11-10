using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Biome;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Called from the Priority.Low postfix of FAT's one TerrainOp.Awake hook
    // (FatVanillaHooks). When the player uses the
    // vanilla hoe (or any other vanilla terrain tool — IH, WEC, MVBP) to
    // paint dirt/cultivated/paved on vanilla terrain, we clear OUR biome
    // overlay + painted clutter + vanilla grass in the same radius so the
    // new vanilla paint shows clean.
    //
    // Vanilla / Infinity Hammer terrain ops like dirt and paved clear our
    // biome overlay and place their paint.
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
            if (IsShovelOp(s, __instance.gameObject.name)) { RegionScrubber.Stats.ShovelSkipped++; return; }
            Remember(position);
            ScrubPaintOverlay(position, radius, "TerrainOp.Awake");
        }

        // ── 0.2.1060 (Fire's Deep North perf; [seasons]' SNOW_PERF_DIAG section 6) ──────────────────────────────────
        // In Deep North every attack places vanilla's snow-shovel TerrainOp (Attack: m_snowShovel, the player's
        // m_snowShovelDefault / m_snowShovelStrong prefab), and the op reached BOTH hooks below, so each one sent two
        // scrub REQUESTs (290 on the dedi for 145 path clears, FPS 9). A shovel op only clears snow: it can never lay
        // anything our biome overlay or grass reset would have to clear, so it is skipped outright.
        private static HashSet<TerrainOp.Settings> s_shovelSettings;
        private static readonly HashSet<string> s_shovelNames = new HashSet<string>();

        private static bool IsShovelOp(TerrainOp.Settings s, string goName)
        {
            if (s_shovelSettings == null) LearnShovels();
            if (s_shovelSettings != null && s_shovelSettings.Contains(s)) return true;
            if (goName != null && s_shovelNames.Count > 0 && s_shovelNames.Contains(Utils.GetPrefabName(goName))) return true;
            // Any other op that only paints deep snow (no level / raise / smooth) is the same kind of op.
            return s.m_paintType == TerrainModifier.PaintType.DeepSnow && !s.m_level && !s.m_raise && !s.m_smooth;
        }

        // The shovel prefabs come from the Player prefab, so the dedi (no local player) knows them too. DoOperation
        // receives the PREFAB's own settings object (TerrainOp.Settings.Deserialize), so a reference match is exact.
        private static void LearnShovels()
        {
            var scene = ZNetScene.instance;
            var player = scene != null ? scene.GetPrefab("Player")?.GetComponent<Player>() : null;
            if (player == null) return;
            s_shovelSettings = new HashSet<TerrainOp.Settings>();
            foreach (var prefab in new[] { player.m_snowShovelDefault, player.m_snowShovelStrong })
            {
                var op = prefab != null ? prefab.GetComponent<TerrainOp>() : null;
                if (op == null || op.m_settings == null) continue;
                s_shovelSettings.Add(op.m_settings);
                s_shovelNames.Add(prefab.name);
            }
            FiresLogger.LogDebug($"[RegionScrubber] snow shovel ops skipped by settings: {string.Join(", ", s_shovelNames)}");
        }

        // Both hooks see a vanilla TerrainOp on the peer that places it AND owns the TerrainComp (Awake, then
        // TerrainComp.DoOperation from its own RPC), so DoOperation drops an op Awake already scrubbed: same spot
        // (0.5 m) within 1 s. DoOperation still covers what Awake never sees (WEC / IH direct DoOperation calls).
        private static readonly List<(Vector3 at, float time)> s_recent = new List<(Vector3, float)>();

        private static void Remember(Vector3 at)
        {
            float now = Time.unscaledTime;
            s_recent.RemoveAll(r => now - r.time > 1f);
            if (s_recent.Count < 64) s_recent.Add((at, now));
        }

        private static bool SeenRecently(Vector3 at)
        {
            float now = Time.unscaledTime;
            foreach (var r in s_recent)
                if (now - r.time <= 1f && (r.at - at).sqrMagnitude <= 0.25f) return true;
            return false;
        }

        private static void ScrubPaintOverlay(Vector3 position, float radius, string callSite)
        {
            // Nothing of ours in the radius: a scrub would change nothing (vanilla's op already resets the grass).
            RegionScrubber.Stats.MaybeReport();
            if (!BiomePaintPersistence.AnyFullyContained(position, radius)) { RegionScrubber.Stats.EmptySkipped++; return; }
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
        //      live near (0,0,0). Visible in a log as
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
        // didn't clear our biome overlay.
        //
        // Hooking DoOperation catches:
        //   * WEC / IH `terrain paint=…` console paths (the common case here)
        //   * Direct TerrainComp.DoOperation calls from any other mod that
        //     paints without going through TerrainOp
        //   * Vanilla TerrainOp.Apply (which itself ends up calling
        //     DoOperation). That op was already scrubbed by TerrainOp.Awake
        //     above, and the double-fire was NOT harmless: each one sent its own
        //     REQUEST RPC (0.2.1060: 2 per Deep North attack, FPS 9). Since
        //     0.2.1060 an op Awake scrubbed is dropped here (SeenRecently).
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
            if (IsShovelOp(modifier, null)) { RegionScrubber.Stats.ShovelSkipped++; return; }
            if (SeenRecently(pos)) { RegionScrubber.Stats.Duplicates++; return; }

            ScrubPaintOverlay(pos, Mathf.Max(modifier.m_paintRadius, 0.5f), "DoOperation");
        }
    }
}
