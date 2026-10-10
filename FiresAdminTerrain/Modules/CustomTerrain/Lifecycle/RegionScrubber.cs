using System;
using UnityEngine;
using VerdantsAscent.Terrain.Biome;
using VerdantsAscent.Terrain.Clutter;
using VerdantsAscent.Terrain.Persistence;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Centralized "clear stuff in a region" API.
    //
    // ONE call site for every flavor of clear / delete / wipe operation
    // across the mod. Replaces the scattered destruction surface from the
    // RPG-maker era (14 different paths, redundant clutter calls, orphan
    // ZDO keys). Every entry point — console command, brush op, RPC
    // handler, vanilla TerrainOp interceptor, undo replay — funnels
    // through one of the two entry methods below and passes ONLY:
    //
    //   (Vector3 center, float radius, Scope scope, Source source)
    //
    // The scrubber owns:
    //   * per-anchor TileAnchor.ClearData (which scope flags = which fields)
    //   * vanilla biome overlay removal (BiomePaintPersistence)
    //   * vanilla zone-paint clear (ZonePaintManager)
    //   * ClutterManager.ClearInRadius (painted clutter)
    //   * ClutterSystem.ResetGrass (vanilla grass)
    //   * vegetation / pieces ZDO walks (opt-in via scope flags)
    //   * RPC fanout (request pattern — non-admin → server → broadcast)
    //   * undo snapshot bracketing (every scrub is undoable for free)
    //
    // NEVER destroys a TileAnchor. Anchor lifecycle is intentionally
    // separated: the anchor is the persistent container, the data inside
    // it is what gets cleared. Old behavior (anchor auto-destroyed on
    // empty) caused the "ghost overlay resurrects on repaint" bug class
    // because vanilla overlay ZDO bytes survived the anchor destroy.
    // Now: anchor stays alive empty, all related vanilla state was
    // scrubbed in the same call, repaint starts from a true blank slate.
    //
    // See Docs/MASTER_GAMEPLAN.md §3 (Phase B) for the design rationale.
    public static class RegionScrubber
    {
        // What gets cleared. Bit flags so callers can combine layers.
        [Flags]
        public enum Scope
        {
            None         = 0,

            // Per-vertex state inside our custom TileAnchor grids.
            TileHeights  = 1 << 0,   // Heights[] + HeightModified[] → vanilla terrain wins
            TilePaint    = 1 << 1,   // Paint[] + PaintModified[]   → mask reverts to "nothing"
            TileBiome    = 1 << 2,   // Biome[] + BiomeModified[]   → vanilla biome wins

            // Vanilla terrain state — modifies vanilla heightmap / TerrainComp
            // ZDO bytes (not our tile anchors). Required so deleting custom
            // terrain ALSO removes whatever we wrote into vanilla in the same
            // area, otherwise repaint resurrects the overlay.
            VanillaBiome = 1 << 3,   // BiomePaintPersistence._activePaints in radius
            VanillaPaint = 1 << 4,   // ZonePaintManager paint mask (dirt/cult/paved)

            // Vegetation / clutter / grass.
            Clutter      = 1 << 5,   // ClutterManager.ClearInRadius (painted clutter)
            VanillaGrass = 1 << 6,   // ClutterSystem.ResetGrass

            // World-object sweeps — heavier, opt-in. Used by hard wipes.
            Vegetation   = 1 << 7,   // ZDOMan walk: trees, rocks, pickups in radius
            Pieces       = 1 << 8,   // ZDOMan walk: player-placed pieces in radius

            // Convenience masks.
            // TileOnly                 — per-tile data only (BrushClear default before vanilla layers).
            // VisualOnly               — what BrushClear should actually do (tile + vanilla visuals).
            // EverythingNoBuild        — what BrushDelete / Wipe should do, preserving player builds.
            // EverythingIncludingBuild — nuclear option for admin wipe-all.
            TileOnly                 = TileHeights | TilePaint | TileBiome,
            VisualOnly               = TileOnly | VanillaBiome | Clutter | VanillaGrass,
            EverythingNoBuild        = VisualOnly | VanillaPaint | Vegetation,
            EverythingIncludingBuild = EverythingNoBuild | Pieces,
        }

        // Why this scrub fired. Used for logging + RPC payload routing +
        // future telemetry / undo grouping.
        public enum Source
        {
            Console,            // direct va_scrub or admin command
            BrushClear,         // va_brush_clear / Clear Brushed Area tool
            BrushDelete,        // va_brush_delete / delete tool
            WipeLoaded,         // va_terrain_wipe_loaded
            WipeAll,            // va_terrain_wipe_all
            VanillaTerrainOp,   // vanilla hoe paint / smooth postfix
            BiomePaintReplace,  // biome paint preflight to clear overlapping vanilla overlay
            Undo,               // undo replay
            Rpc,                // we're the receiver of a broadcast scrub (don't re-broadcast)
        }

        // ── Public entry points ─────────────────────────────────────────

        // World-space scrub. Walks every TileAnchor whose footprint overlaps
        // the circle and applies the per-anchor inner loop, then runs the
        // requested vanilla-layer clears ONCE, then broadcasts the op via
        // RPC (request pattern: client → server → broadcast).
        //
        // Returns the count of "things changed" (per-vertex writes + vanilla
        // entries removed + clutter objects destroyed). 0 = no-op-skip.
        //
        // broadcastRpc = false when we're the RECEIVER of a broadcast (the
        // op already ran on the sender + every other peer; we just apply
        // locally without re-fanning-out).
        public static int ScrubRegion(
            Vector3 center,
            float radius,
            Scope scope,
            Source source,
            bool broadcastRpc = true)
        {
            if (radius <= 0f || scope == Scope.None) return 0;

            // ── MP dispatch (request pattern, per user choice 2026-05-25) ──
            //
            // SP / host / receiving-an-APPLY-broadcast → fall through to
            // local apply directly. Avoid the round trip when we're
            // already the authoritative side.
            //
            // Client (non-server) → fire a REQUEST RPC to the server, then
            // STOP. The server validates admin status; if approved, it
            // applies locally + broadcasts an APPLY RPC back. Every peer
            // (including the original requester) receives the APPLY and
            // calls ApplyLocally with source=Rpc, broadcastRpc=false.
            //
            // This keeps non-admin clients from being able to alter the
            // world even momentarily: they don't apply locally at all,
            // they just shoot a request that the server can reject. No
            // visual jank from local-apply-then-rollback.
            if (source != Source.Rpc
                && broadcastRpc
                && ZNet.instance != null
                && ZRoutedRpc.instance != null
                && !ZNet.instance.IsServer())
            {
                RegionScrubberRPC.SendRequest(center, radius, scope, source);
                FiresLogger.LogVerbose($"[RegionScrubber] Client {source} → REQUEST dispatched to server; awaiting APPLY broadcast.");
                return 0;
            }

            int n = ApplyLocally(center, radius, scope, source);

            // Server (or SP) — broadcast APPLY so other peers mirror it.
            if (source != Source.Rpc
                && broadcastRpc
                && ZNet.instance != null
                && ZRoutedRpc.instance != null
                && ZNet.instance.IsServer())
            {
                RegionScrubberRPC.BroadcastApply(center, radius, scope, source);
            }

            return n;
        }

        // Inner apply — runs the actual layer-by-layer scrub on the local
        // peer. Public so RegionScrubberRPC handlers can call it directly
        // without re-entering the ScrubRegion MP dispatch (which would
        // recurse). DO NOT call from outside the lifecycle namespace —
        // use ScrubRegion / ScrubFromRuler instead.
        public static int ApplyLocally(
            Vector3 center,
            float radius,
            Scope scope,
            Source source)
        {
            if (radius <= 0f || scope == Scope.None) return 0;

            int changes = 0;
            string opName = $"VAScrub:{source}:{scope}";

            // ── Undo bracket ─────────────────────────────────────────────
            // TileUndoLog snapshots the per-vertex state of every overlapping
            // tile BEFORE we mutate, computes deltas AFTER, and pushes one
            // entry onto the undo stack. So every scrub op is undoable for
            // free via va_tile_undo / WEC. We don't snapshot when receiving
            // an RPC broadcast (the original sender already recorded undo
            // on their side — duplicate entries on peers would be noise).
            bool recordUndo = source != Source.Undo && source != Source.Rpc;
            if (recordUndo) TileUndoLog.BeginOperation(opName, center, radius);

            try
            {
                // ── Layer 1: per-tile data (TileHeights / TilePaint / TileBiome) ──
                bool needsAnchorClear =
                    (scope & (Scope.TileHeights | Scope.TilePaint | Scope.TileBiome)) != 0;
                if (needsAnchorClear)
                {
                    foreach (var tileXZ in TileAnchorRegistry.TilesOverlappingCircle(center, radius))
                    {
                        // Critical: GetAt — NOT Spawn. Scrub never spawns
                        // anchors. If a zone has no anchor today, there's
                        // nothing to clear there; lazy-spawn rules (per user
                        // answer 2026-05-25) say anchors arrive only when a
                        // brush PAINT touches them.
                        var anchor = TileAnchorRegistry.GetAt(tileXZ);
                        if (anchor == null) continue;
                        changes += anchor.ClearData(center, radius, scope);
                    }
                }

                // ── Layer 2: vanilla biome overlay (BiomePaintPersistence) ──
                if ((scope & Scope.VanillaBiome) != 0)
                {
                    try
                    {
                        changes += BiomePaintPersistence.RemoveOverlapping(center, radius);
                    }
                    catch (Exception ex)
                    {
                        FiresLogger.LogWarning($"[RegionScrubber] VanillaBiome layer threw: {ex.Message}");
                    }
                }

                // Layer 3 (VanillaPaint) decommissioned in Phase F. The
                // legacy ZonePaintManager / ZonePaintLayer pipeline is
                // deleted. Vanilla heightmap paint mask writes happen via
                // TerrainComp.PaintCleared (vanilla path) and our scrubber
                // intentionally does NOT touch player-laid pavement.
                // VanillaPaint scope flag remains in the enum so callers
                // compile, but it is now a no-op until/unless we add a
                // direct TerrainComp.PaintCleared-clearing path.

                // ── Layer 4: painted clutter ──
                if ((scope & Scope.Clutter) != 0)
                {
                    try
                    {
                        ClutterManager.ClearInRadius(center, radius);
                        // ClutterManager.ClearInRadius doesn't return a count;
                        // bump changes by 1 so callers can see something
                        // happened. Real count would require refactoring
                        // ClearInRadius — out of scope for B4.
                        changes += 1;
                    }
                    catch (Exception ex)
                    {
                        FiresLogger.LogWarning($"[RegionScrubber] Clutter layer threw: {ex.Message}");
                    }
                }

                // ── Layer 5: vanilla grass (ClutterSystem.ResetGrass) ──
                if ((scope & Scope.VanillaGrass) != 0)
                {
                    try
                    {
                        if (ClutterSystem.instance != null)
                        {
                            ClutterSystem.instance.ResetGrass(center, radius);
                            changes += 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        FiresLogger.LogWarning($"[RegionScrubber] VanillaGrass layer threw: {ex.Message}");
                    }
                }

                // ── Layer 6 / 7: Vegetation / Pieces ──
                //
                // ZDOMan walk for trees, rocks, pickups (Vegetation) and
                // player-placed pieces (Pieces). These are heavier ops —
                // each opt-in via separate scope flags so casual brush
                // clears don't accidentally nuke a player's base.
                //
                // DEFERRED: implementation lands in Task #95 (B12) once
                // the lighter scopes are field-tested. Today these flags
                // log a TODO so any caller that flips them sees a clear
                // signal that the layer isn't wired yet.
                if ((scope & (Scope.Vegetation | Scope.Pieces)) != 0)
                {
                    FiresLogger.LogWarning($"[RegionScrubber] Vegetation/Pieces scope requested by {source} — not yet implemented (Phase B12). Layer skipped.");
                }
            }
            finally
            {
                if (recordUndo) TileUndoLog.EndOperation();
            }

            FiresLogger.LogInfo($"[RegionScrubber] {source} | center={center} r={radius:F1} scope={scope} → {changes} change(s)");
            return changes;
        }

        // Ruler-aware scrub. Reads the currently-active Infinity Hammer
        // ruler state (position, radius, height offset) and dispatches a
        // ScrubRegion using those values.
        //
        // When called from a console command, pass the ConsoleEventArgs so
        // we can parse the IH-expanded `from=`, `circle=`, `height=` tokens.
        // When called from non-console code, the args=null overload uses a
        // cursor raycast.
        //
        // Hard rule: when the ruler is active (`from=` present), the ruler's
        // Y wins over any raycast. This is what makes ruler-set elevations
        // (Page Up/Down) actually persist into the scrub — without it, the
        // raycast would slide to whatever surface is under the cursor and
        // the player's ruler intent gets lost.
        public static int ScrubFromRuler(
            Terminal.ConsoleEventArgs args,
            Scope scope,
            Source source,
            float defaultRadius = 10f,
            bool broadcastRpc = true)
        {
            if (!RulerArgs.TryResolveCenter(args, out var center, out bool usedRuler))
                return 0;

            float radius = RulerArgs.ResolveRadius(args, defaultRadius);

            // IH `<h>` Y offset — added to whatever center we resolved.
            // For most scrub scopes the Y doesn't matter (radius scrub is
            // XZ-circular), but for sky-built tiles where the player wants
            // to scrub from inside the platform without looking down at it,
            // honoring the ruler's Y keeps the op intuitive.
            if (RulerArgs.TryParseNamedFloat(args, "height", out float hOffset))
                center.y += hOffset;

            FiresLogger.LogVerbose($"[RegionScrubber] ScrubFromRuler resolved | usedRuler={usedRuler} center={center} r={radius:F1} scope={scope}");

            return ScrubRegion(center, radius, scope, source, broadcastRpc);
        }

        // Args-less overload — pure cursor raycast. Used by non-console
        // callers (Harmony patches, direct API usage).
        public static int ScrubFromRuler(
            Scope scope,
            Source source,
            float defaultRadius = 10f,
            bool broadcastRpc = true)
        {
            return ScrubFromRuler(null, scope, source, defaultRadius, broadcastRpc);
        }
    }
}
