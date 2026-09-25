using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Hard-delete operations for tile anchors. Distinct from `va_brush_clear`
    // which only wipes vertex modifications inside the radius — these commands
    // destroy the whole anchor (ZDO + GameObject + mesh combiners) so the tile
    // is gone from the world save entirely.
    //
    // Cleanup chain (verified by reading the hierarchy):
    //   TileAnchor GO (ZNetView, Y=-5000)
    //     └── "Tile_X_Z" GO (TerrainTile + TileMeshRenderer)
    //          └── "Underside" GO (TileMeshUnderside)
    //
    // ZNetScene.Destroy(tileAnchor.gameObject) handles:
    //   1. ZDO removal from ZDOMan (and replication of removal to peers)
    //   2. Unity hierarchy destruction → TerrainTile + TileMeshRenderer +
    //      TileMeshUnderside all destroyed via OnDestroy cascade
    //   3. TileAnchor.OnDestroy → TileAnchorRegistry.Unregister (auto)
    //
    // Commands:
    //   va_brush_delete [radius=N]    — destroy loaded anchors in radius around pointer
    //   va_terrain_wipe_loaded        — destroy every loaded anchor
    //   va_terrain_wipe_all           — destroy every loaded anchor + scan ZDOMan
    //                                    for unloaded VATile ZDOs and destroy those too
    //   va_terrain_count              — info: loaded anchor count + ZDOMan VATile count
    public static class TileDeleteOps
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("va_brush_delete",
                "[va_brush_delete radius=10] Destroy every TileAnchor whose center is inside the radius around the pointer. Removes ZDO + mesh + combiners.",
                (args) => Cmd_BrushDelete(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_terrain_wipe_loaded",
                "Destroy every currently-loaded TileAnchor (and their meshes/ZDOs). Does NOT scan unloaded zones — use va_terrain_wipe_all for a full purge.",
                (args) => Cmd_WipeLoaded(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_terrain_wipe_all",
                "[ASYNC] Destroy every TileAnchor world-wide. 1) Loaded anchors. 2) Pokes every generated zone via reflection (Upgrade World-style) so cold zones stream their ZDOs into ZDOMan. 3) Destroys all VATile ZDOs. Watch the log for phase progress — irreversible.",
                (args) => Cmd_WipeAll(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_terrain_count",
                "Print the current loaded TileAnchor count and the ZDOMan VATile ZDO count (loaded + unloaded).",
                (args) => Cmd_Count(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_terrain_capture",
                "Force a heightmap-material capture from any vanilla Heightmap in the loaded scene. Useful if tiles are rendering as the Standard fallback (admin yard with no vanilla terrain ever loaded this session).",
                (args) =>
                {
                    if (FiresCore.Materials.TerrainMaterialCache.HasMaterial)
                    {
                        args.Context.AddString("va_terrain_capture: already cached — no-op. Use va_terrain_recapture to drop the cache and re-grab (e.g. after HD Terrain finished loading).");
                        return;
                    }
                    int scanned = FiresCore.Materials.TerrainMaterialCache.ForceCaptureFromScene();
                    if (FiresCore.Materials.TerrainMaterialCache.HasMaterial)
                    {
                        args.Context.AddString($"va_terrain_capture: scanned {scanned} heightmap(s), captured heightmap material. All tiles upgraded.");
                    }
                    else
                    {
                        args.Context.AddString($"va_terrain_capture: scanned {scanned} heightmap(s), NONE produced a heightmap-compatible material. Try standing near vanilla terrain and re-running.");
                    }
                },
                isCheat: true);

            new Terminal.ConsoleCommand("va_terrain_recapture",
                "Drop the cached tile material and re-capture from a vanilla Heightmap. Use after a shader-swap mod (e.g. ValheimHDTerrain) finishes loading so tiles pick up the new look. Refreshes every tile renderer in the world.",
                (args) =>
                {
                    int scanned = FiresCore.Materials.TerrainMaterialCache.ForceRecaptureFromScene();
                    if (FiresCore.Materials.TerrainMaterialCache.HasMaterial)
                    {
                        args.Context.AddString($"va_terrain_recapture: cleared cache, scanned {scanned} heightmap(s), captured fresh material. All tiles upgraded.");
                    }
                    else
                    {
                        args.Context.AddString($"va_terrain_recapture: cleared cache, scanned {scanned} heightmap(s) but no compatible shader found. Stand near vanilla terrain and re-run.");
                    }
                },
                isCheat: true);
        }

        // ── Pointer + radius brush variant ───────────────────────────────────
        //
        // Same arg resolution pattern as TileBrushCommands: IH `<area>` token
        // expands to `from=x,z,y circle=r-r2 …`; standalone console use falls
        // back to player look-raycast.
        //
        // POST-PHASE-B (2026-05-25): routes through RegionScrubber instead of
        // ZNetScene.Destroy. Anchors stay alive with empty data — this is
        // the "anchor-per-region, never destroy" model. The visual result is
        // identical (tile mesh + vanilla overlay + clutter all gone from
        // the radius), but the anchor ZDO persists so repaints write back
        // to the same identity without leaving orphan ZDO bytes.
        //
        // Scope = EverythingNoBuild — kills tile data, vanilla overlay,
        // vanilla paint, clutter, grass, AND vegetation in the radius.
        // Player-placed pieces are preserved.
        private static void Cmd_BrushDelete(Terminal.ConsoleEventArgs args)
        {
            int n = VerdantsAscent.Terrain.Lifecycle.RegionScrubber.ScrubFromRuler(
                args,
                VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope.EverythingNoBuild,
                VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Source.BrushDelete,
                defaultRadius: 10f);
            args.Context.AddString($"va_brush_delete (scrubbed): {n} change(s).");
        }

        private static int DeleteAnchorsInRadius(Vector3 center, float radius, out int skippedOwnership)
        {
            skippedOwnership = 0;
            if (ZNetScene.instance == null) return 0;

            int destroyed = 0;
            float r2 = radius * radius;

            // Snapshot the registry first — destroying anchors mutates it via OnDestroy.
            var snapshot = new List<TileAnchor>();
            foreach (var anchor in TileAnchorRegistry.All)
                if (anchor != null) snapshot.Add(anchor);

            foreach (var anchor in snapshot)
            {
                if (anchor == null) continue;
                var pos = TileAnchorRegistry.WorldCenterForTile(anchor.TileXZ);
                float dx = pos.x - center.x;
                float dz = pos.z - center.z;
                if (dx * dx + dz * dz > r2) continue;

                if (!TryDestroyAnchor(anchor, out string failReason))
                {
                    if (failReason == "not-owner") skippedOwnership++;
                    FiresLogger.LogWarning($"[TileDeleteOps] Skipped tile=({anchor.TileXZ.x},{anchor.TileXZ.y}): {failReason}");
                    continue;
                }
                destroyed++;
            }

            FiresLogger.LogInfo($"[TileDeleteOps] DeleteAnchorsInRadius: center={center} r={radius} → destroyed {destroyed}, skipped(non-owner) {skippedOwnership}");
            return destroyed;
        }

        // ── Wipe loaded ──────────────────────────────────────────────────────
        //
        // POST-PHASE-B: iterates every loaded anchor and scrubs its full
        // tile footprint via RegionScrubber. Anchors stay alive with
        // empty data (anchor-per-region, never destroy). Visually
        // equivalent to the old destroy-everything path but doesn't
        // orphan ZDO byte arrays / leave vanilla overlay ghosts behind.
        private static void Cmd_WipeLoaded(Terminal.ConsoleEventArgs args)
        {
            int total = WipeAllLoaded();
            args.Context.AddString($"va_terrain_wipe_loaded (scrubbed): {total} change(s) across loaded anchors.");
        }

        private static int WipeAllLoaded()
        {
            int total = 0;
            var snapshot = new List<TileAnchor>();
            foreach (var anchor in TileAnchorRegistry.All)
                if (anchor != null) snapshot.Add(anchor);

            // 32 m half-tile + a hair so the scrub circle inscribes the
            // full 64m tile square. Using a circle for a square footprint
            // means corners are slightly under-cleared; we accept this
            // tradeoff because all other entry points are circle-based
            // and consistency matters more than corner perfection.
            const float TILE_FULL_RADIUS = 46f; // ≈ sqrt(2)*32 — covers the corner verts too

            foreach (var anchor in snapshot)
            {
                if (anchor == null) continue;
                var center = TileAnchorRegistry.WorldCenterForTile(anchor.TileXZ);
                total += VerdantsAscent.Terrain.Lifecycle.RegionScrubber.ScrubRegion(
                    center, TILE_FULL_RADIUS,
                    VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope.EverythingNoBuild,
                    VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Source.WipeLoaded);
            }
            FiresLogger.LogInfo($"[TileDeleteOps] WipeAllLoaded (scrubbed): {total} change(s) across {snapshot.Count} anchor(s).");
            return total;
        }

        // ── Wipe everything (loaded + unloaded ZDOs across the world) ───────
        //
        // 3-phase coroutine — synchronous wouldn't finish in one frame on a
        // megaworld and would freeze the game:
        //
        // Phase 1 — destroy every currently-loaded TileAnchor via ZNetScene.Destroy
        //           (synchronous, fast).
        // Phase 2 — POKE every entry in ZoneSystem.m_generatedZones (HashSet of
        //           Vector2s, private). Calling ZoneSystem.PokeLocalZone(zoneID)
        //           via reflection forces that zone to spawn its ZDOs into
        //           ZDOMan. Without this, "cold" zones (generated historically
        //           but not visited this session) have their VATile ZDOs sitting
        //           on disk only — GetAllZDOsWithPrefabIterative wouldn't see them
        //           and our wipe would miss them. This is the canonical zone-
        //           poke pattern used by Upgrade World / WorldEditCommands for
        //           world-wide ops.
        //           Yields per N zones so the main thread doesn't stall.
        // Phase 3 — after poking + a small settle delay, iterate ZDOMan for all
        //           VATile ZDOs (now including the just-poked cold ones) and
        //           DestroyZDO each (claim ownership first).
        //
        // The coroutine is hosted on the FiresAdminTerrain plugin Instance
        // (DontDestroyOnLoad MonoBehaviour).
        private static void Cmd_WipeAll(Terminal.ConsoleEventArgs args)
        {
            if (ZDOMan.instance == null)
            {
                args.Context.AddString("va_terrain_wipe_all: ZDOMan.instance is null — must be in a world.");
                return;
            }
            if (ZoneSystem.instance == null)
            {
                args.Context.AddString("va_terrain_wipe_all: ZoneSystem.instance is null — must be in a world.");
                return;
            }
            if (FiresAdminTerrain.Instance == null)
            {
                args.Context.AddString("va_terrain_wipe_all: plugin instance missing — cannot host coroutine.");
                return;
            }

            args.Context.AddString("va_terrain_wipe_all: starting — phase 1 (loaded), then zone-poke, then ZDO scan. Watch the BepInEx log for progress.");
            FiresAdminTerrain.Instance.StartCoroutine(WipeAllCoroutine(args.Context));
        }

        // Tunables: how many zones to poke per frame before yielding, and how
        // long to wait after all pokes before the final ZDO scan.
        private const int POKE_ZONES_PER_FRAME = 25;
        private const float POKE_SETTLE_SECONDS = 1.5f;

        private static IEnumerator WipeAllCoroutine(Terminal term)
        {
            // ── Phase 1 — loaded anchors ─────────────────────────────────────
            // SCRUB not destroy — anchors stay alive but empty.
            int phase1Changes = WipeAllLoaded();
            FiresLogger.LogInfo($"[TileDeleteOps] WipeAll phase 1 complete: scrubbed {phase1Changes} change(s) across loaded anchors.");
            if (term != null) term.AddString($"[wipe_all] phase 1: scrubbed {phase1Changes} change(s) (loaded anchors stay alive empty).");
            yield return null;

            // ── Phase 2 — poke every generated zone ──────────────────────────
            var generatedZones = SnapshotGeneratedZones();
            if (generatedZones == null)
            {
                if (term != null) term.AddString("[wipe_all] could not read ZoneSystem.m_generatedZones via reflection — phase 2 aborted, only loaded ZDOs were destroyed.");
                FiresLogger.LogError("[TileDeleteOps] WipeAll: cannot reflect m_generatedZones. Aborting phase 2.");
                yield break;
            }

            FiresLogger.LogInfo($"[TileDeleteOps] WipeAll phase 2: starting poke of {generatedZones.Count} generated zones (batch={POKE_ZONES_PER_FRAME}/frame).");
            if (term != null) term.AddString($"[wipe_all] phase 2: poking {generatedZones.Count} generated zones (this may take a moment).");

            int pokes = 0, pokedNew = 0;
            foreach (var zoneKey in generatedZones)
            {
                if (TryPokeLocalZone(zoneKey, out bool spawnedFresh))
                {
                    pokes++;
                    if (spawnedFresh) pokedNew++;
                }
                if (pokes % POKE_ZONES_PER_FRAME == 0)
                    yield return null;
            }
            FiresLogger.LogInfo($"[TileDeleteOps] WipeAll phase 2 complete: poked {pokes}/{generatedZones.Count} zones ({pokedNew} freshly spawned).");

            // Settle: give ZoneSystem time to actually instantiate the queued
            // zones and stream their ZDOs into ZDOMan.
            yield return new WaitForSeconds(POKE_SETTLE_SECONDS);

            // ── Phase 3 — scrub every (now-warm) cold-zone anchor ────────────
            //
            // Pre-Phase-B used to DestroyZDO each VATile here. New behavior:
            // the zone poke caused the VATile ZDOs to instantiate as
            // TileAnchors. WipeAllLoaded above only saw the originally-loaded
            // anchors; the newly-instantiated ones are now in the registry
            // too. Iterate registry again and scrub them — anchors stay
            // alive, data clears. Same end result for the player; preserves
            // the per-zone ZDO identity for future repaints.
            int phase3Changes = WipeAllLoaded();
            FiresLogger.LogInfo($"[TileDeleteOps] WipeAll phase 3 complete: scrubbed {phase3Changes} change(s) on cold-zone anchors (now warm after poke).");
            if (term != null)
                term.AddString($"[wipe_all] DONE — phase1 changes={phase1Changes}, poked zones={pokes}, phase3 changes={phase3Changes}. Anchors stay alive empty.");
        }

        // ── ZoneSystem reflection: m_generatedZones + PokeLocalZone ──────────
        // Cached lazily on first wipe_all. Both are private vanilla members
        // (HashSet<Vector2s> and bool method respectively).
        private static FieldInfo s_generatedZonesField;
        private static MethodInfo s_pokeLocalZoneMethod;
        private static bool s_zoneReflectionAttempted;

        private static bool EnsureZoneReflection()
        {
            if (s_zoneReflectionAttempted) return s_generatedZonesField != null && s_pokeLocalZoneMethod != null;
            s_zoneReflectionAttempted = true;

            s_generatedZonesField = typeof(ZoneSystem).GetField(
                "m_generatedZones",
                BindingFlags.NonPublic | BindingFlags.Instance);

            s_pokeLocalZoneMethod = typeof(ZoneSystem).GetMethod(
                "PokeLocalZone",
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(Vector2s) },
                modifiers: null);

            if (s_generatedZonesField == null)
                FiresLogger.LogError("[TileDeleteOps] ZoneSystem.m_generatedZones field not found via reflection — wipe_all cannot enumerate cold zones.");
            if (s_pokeLocalZoneMethod == null)
                FiresLogger.LogError("[TileDeleteOps] ZoneSystem.PokeLocalZone(Vector2s) method not found via reflection — wipe_all cannot force-load cold zones.");

            return s_generatedZonesField != null && s_pokeLocalZoneMethod != null;
        }

        private static List<Vector2s> SnapshotGeneratedZones()
        {
            if (!EnsureZoneReflection()) return null;
            if (ZoneSystem.instance == null) return null;
            try
            {
                var raw = s_generatedZonesField.GetValue(ZoneSystem.instance);
                if (raw is HashSet<Vector2s> set)
                {
                    return new List<Vector2s>(set);
                }
                FiresLogger.LogError($"[TileDeleteOps] m_generatedZones returned unexpected type {raw?.GetType().FullName ?? "<null>"}");
                return null;
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogError($"[TileDeleteOps] SnapshotGeneratedZones threw: {ex}");
                return null;
            }
        }

        // PokeLocalZone returns true if it just spawned the zone fresh (out parameter
        // distinguishes that from "already loaded, just reset TTL").
        private static bool TryPokeLocalZone(Vector2s zoneID, out bool spawnedFresh)
        {
            spawnedFresh = false;
            if (!EnsureZoneReflection()) return false;
            if (ZoneSystem.instance == null) return false;
            try
            {
                object result = s_pokeLocalZoneMethod.Invoke(ZoneSystem.instance, new object[] { zoneID });
                if (result is bool b)
                {
                    spawnedFresh = b;
                    return true;
                }
                return false;
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[TileDeleteOps] PokeLocalZone({zoneID.x},{zoneID.y}) threw: {ex.Message}");
                return false;
            }
        }

        private static int WipeUnloadedVATileZdos()
        {
            if (ZDOMan.instance == null) return 0;

            int destroyed = 0;
            long mySessionId = ZDOMan.GetSessionID();
            var zdoList = new List<ZDO>();
            int index = 0;

            // GetAllZDOsWithPrefabIterative pages through ZDOMan in chunks;
            // returns true when the scan is complete.
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(
                TileAnchorPrefab.PrefabName, zdoList, ref index))
            {
                // Iterate paged — loop until scan completes.
            }

            FiresLogger.LogInfo($"[TileDeleteOps] WipeUnloadedVATileZdos: ZDOMan scan returned {zdoList.Count} '{TileAnchorPrefab.PrefabName}' ZDOs (some may already be destroyed via the loaded-wipe phase).");

            foreach (var zdo in zdoList)
            {
                if (zdo == null) continue;

                // Skip if a loaded GO still owns this ZDO — that path was
                // already handled by WipeAllLoaded via ZNetScene.Destroy.
                // (Defensive — ZNetScene.Destroy removes the ZDO synchronously,
                // so by the time we get here it shouldn't be in the list anymore,
                // but FindInstance is the cheapest safety check.)
                if (ZNetScene.instance != null && ZNetScene.instance.FindInstance(zdo) != null)
                    continue;

                try
                {
                    // Claim before destroy. DestroyZDO is a no-op if we're not
                    // the owner — set our session ID first.
                    zdo.SetOwner(mySessionId);
                    ZDOMan.instance.DestroyZDO(zdo);
                    destroyed++;
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[TileDeleteOps] DestroyZDO threw for ZDO {zdo.m_uid}: {ex.Message}");
                }
            }

            FiresLogger.LogInfo($"[TileDeleteOps] WipeUnloadedVATileZdos → destroyed {destroyed} unloaded VATile ZDOs.");
            return destroyed;
        }

        // ── Info ─────────────────────────────────────────────────────────────
        private static void Cmd_Count(Terminal.ConsoleEventArgs args)
        {
            int loaded = TileAnchorRegistry.Count;
            int zdoTotal = -1;

            if (ZDOMan.instance != null)
            {
                var zdoList = new List<ZDO>();
                int idx = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(
                    TileAnchorPrefab.PrefabName, zdoList, ref idx))
                { }
                zdoTotal = zdoList.Count;
            }

            args.Context.AddString(
                $"va_terrain_count: loaded TileAnchors={loaded} | ZDOMan VATile ZDOs={zdoTotal} (loaded + unloaded)");
        }

        // ── Core destroy helper ──────────────────────────────────────────────
        //
        // Claims ownership (so DestroyZDO actually broadcasts), then calls
        // ZNetScene.Destroy which:
        //   1. Removes the ZDO from ZDOMan
        //   2. Fires OnDestroy on every component in the GO hierarchy
        //   3. Unity destroys the GO (deferred to frame end, but '== null'
        //      is true immediately for Unity's overloaded check)
        //
        // Returns false + failReason if we can't acquire ownership.
        private static bool TryDestroyAnchor(TileAnchor anchor, out string failReason)
        {
            failReason = null;
            if (anchor == null) { failReason = "null-anchor"; return false; }

            var nview = anchor.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                failReason = "invalid-nview";
                return false;
            }

            if (!nview.IsOwner())
            {
                nview.ClaimOwnership();
                if (!nview.IsOwner())
                {
                    // ClaimOwnership is network-async — single-player it
                    // promotes synchronously, MP needs a tick. For a wipe
                    // command we skip rather than block.
                    failReason = "not-owner";
                    return false;
                }
            }

            // ZNetScene.Destroy does both ZDO removal AND GameObject.Destroy.
            // The hierarchy destruction (Unity-side) cascades through:
            //   TileAnchor → TerrainTile child → TileMeshRenderer + Underside child → TileMeshUnderside
            // Every MonoBehaviour's OnDestroy fires; TileAnchorRegistry.Unregister
            // runs automatically from TileAnchor.OnDestroy.
            try
            {
                ZNetScene.instance.Destroy(anchor.gameObject);
                return true;
            }
            catch (System.Exception ex)
            {
                failReason = $"destroy-threw:{ex.Message}";
                return false;
            }
        }

        // ── Arg helpers (mirror TileBrushCommands so behavior matches) ───────
        private static bool ResolveCenter(Terminal.ConsoleEventArgs args, out Vector3 center)
        {
            if (TryParseFromArg(args, out center)) return true;
            if (!TileBrush.TryGetPointerWorldPosition(out center))
            {
                args.Context.AddString("No local player — must be in-world.");
                return false;
            }
            return true;
        }

        private static bool TryParseFromArg(Terminal.ConsoleEventArgs args, out Vector3 pos)
        {
            pos = Vector3.zero;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null) continue;
                if (!a.StartsWith("from=", System.StringComparison.OrdinalIgnoreCase)) continue;

                string payload = a.Substring(5).Trim();
                string[] coords = payload.Split(',');
                if (coords.Length < 3) continue;

                if (float.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                    float.TryParse(coords[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    pos = new Vector3(x, y, z);
                    return true;
                }
            }
            return false;
        }

        private static float ResolveRadius(Terminal.ConsoleEventArgs args, float defaultRadius)
        {
            if (TryParseCircleArg(args, out float ihRadius) && ihRadius > 0f) return ihRadius;
            return ParseFloat(args, 1, defaultRadius);
        }

        private static bool TryParseCircleArg(Terminal.ConsoleEventArgs args, out float outerRadius)
        {
            outerRadius = 0f;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null || !a.StartsWith("circle=", System.StringComparison.OrdinalIgnoreCase)) continue;
                string payload = a.Substring("circle=".Length).Trim();
                string[] parts = payload.Split('-');
                if (parts.Length < 1) continue;
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out outerRadius))
                    return true;
            }
            return false;
        }

        private static float ParseFloat(Terminal.ConsoleEventArgs args, int idx, float def)
        {
            if (args.Length <= idx) return def;
            return float.TryParse(args[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        }
    }
}
