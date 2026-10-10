using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Lifecycle;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    // Infinity Hammer-compatible brush commands for placing + clearing
    // custom water bodies. Mirrors the va_brush_* terrain brush family —
    // same IH arg parsing (from= / circle= / height=) via the shared
    // RulerArgs helper, same conventions for radius / height defaults.
    //
    // Console commands registered here:
    //   va_brush_water           — place a Water disk at the ruler cursor
    //   va_brush_lava            — place a Lava disk at the ruler cursor
    //   va_brush_water_tar       — Tar disk
    //   va_brush_water_boiling   — Boiling water disk (Ashlands hot springs)
    //   va_brush_water_freezing  — Freezing water disk (DeepNorth icy lakes)
    //   va_brush_water_ice       — Solid ice disk (walkable surface)
    //   va_brush_water_delete    — delete custom water disks overlapping
    //                              the ruler radius (no terrain change)
    //
    // Each command:
    //   * Reads center via RulerArgs.TryResolveCenter (IH `from=` wins,
    //     cursor raycast fallback).
    //   * Reads radius via RulerArgs.ResolveRadius (IH `circle=` wins).
    //   * Reads height offset via RulerArgs.TryParseNamedFloat("height").
    //   * Calls CustomWaterManager.CreateWaterDisk / CreateLavaDisk /
    //     etc. to spawn the disk. The WaterMeshCombiner handles persistence
    //     via WaterAnchorPrefab's CustomRiver anchor.
    public static class WaterBrushCommands
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            WaterCleanupCommand.RegisterCommand();   // 0.2.755: va_water_cleanup_spread
            WaterAtCommand.RegisterCommand();        // 0.2.776: va_water_at, who claims the water over a point

            new Terminal.ConsoleCommand("va_brush_water",
                "[va_brush_water radius=10 depth=3] Place a Water disk at the IH ruler cursor.",
                args => PlaceDisk(args, CustomLiquidType.Water, "Water"),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_lava",
                "[va_brush_lava radius=10 depth=3] Place a Lava disk at the IH ruler cursor.",
                args => PlaceDisk(args, CustomLiquidType.Lava, "Lava"),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_water_tar",
                "[va_brush_water_tar radius=10 depth=3] Place a Tar disk at the IH ruler cursor.",
                args => PlaceDisk(args, CustomLiquidType.Tar, "Tar"),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_water_boiling",
                "[va_brush_water_boiling radius=10 depth=3] Place a boiling-water disk (Ashlands hot spring).",
                args => PlaceDisk(args, CustomLiquidType.BoilingWater, "BoilingWater"),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_water_freezing",
                "[va_brush_water_freezing radius=10 depth=3] Place a freezing-water disk (DeepNorth icy lake).",
                args => PlaceDisk(args, CustomLiquidType.FreezingWater, "FreezingWater"),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_water_ice",
                "[va_brush_water_ice radius=10 depth=1] Place a solid ice disk (walkable surface).",
                args => PlaceDisk(args, CustomLiquidType.Ice, "Ice"),
                isCheat: true);

            // 2026-05-25 round-19: Snow is fully decoupled from the water
            // system. The va_brush_snow command now routes into the
            // LandSnowController (terrain-anchor mesh, no MeshCollider,
            // no WaterMeshCombiner registration). This eliminates all
            // InWater / IsSwimming / wet / swim-trigger side effects that
            // came from the old water-disk path. EXACT clone of LandIce
            // mesh-generation pipeline; only difference is material +
            // collider absence.
            new Terminal.ConsoleCommand("va_brush_snow",
                "[va_brush_snow <area>] Place a terrain-anchor snow disk at the cursor (visual-only, no collider, NO water-volume coupling — does NOT trigger swim/wet/InWater).",
                args => PlaceLandSnowFromBrush(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_water_delete",
                "[va_brush_water_delete radius=10] Delete water WITHIN the ruler radius (carves the pour locally, doesn't nuke the whole source).",
                args => DeleteDisks(args),
                isCheat: true);

            // Round-78 — MOP restored for the vanilla-liquid era. Soft,
            // precision local removal: takes a fraction of the water depth
            // under the ruler per pass so holding it feathers an edge back
            // instead of punching a hole. Drag-pumped.
            new Terminal.ConsoleCommand("va_brush_water_mop",
                "[va_brush_water_mop radius=3] Mop — precision, gradual water removal under the ruler (hold/drag to feather edges). Also DAMS spring flow.",
                args => MopWater(args),
                isCheat: true);

            // Round-84 — FREEZE. Holds a spring's mesh in its current shape and
            // stops the flow/overflow sim (toggle). Placing a new spring over it
            // unfreezes it so the new source updates the mesh.
            new Terminal.ConsoleCommand("va_brush_water_freeze",
                "[va_brush_water_freeze radius=8] Freeze/unfreeze spring flow — locks the mesh in place, no more overflow (toggle).",
                args => FreezeWater(args),
                isCheat: true);

            // Round-W-2 — Waterfall brush. Each dab places a shallow
            // water disk at (terrainRaycastY + offset) instead of at
            // the ruler surface Y. Drag UP or DOWN a slope to lay a
            // continuous flowing sheet that hugs the cliff face.
            // (See Docs/PLAN_WaterOverhaul.md §W-2.)
            new Terminal.ConsoleCommand("va_brush_waterfall",
                "[va_brush_waterfall radius=4] Place a waterfall — each drag dab samples terrain Y, water hugs the slope.",
                args => PlaceWaterfall(args, CustomLiquidType.Water, "Waterfall"),
                isCheat: true);

            // Round-56 — Water VOLUME. Pours a cylinder of water (ruler
            // radius, filled up to the ruler Y), settles it into the
            // terrain depression over ~1.5s, then gels so it holds shape.
            // Single placement per click — intentionally NOT drag-pumped.
            // (See Docs/PLAN_WaterVolumePlacement.md.)
            new Terminal.ConsoleCommand("va_brush_water_volume",
                "[va_brush_water_volume radius=8] Pour a settling water volume — fills the ruler circle to the ruler Y, settles ~1.5s, then gels in place.",
                args => PlaceWaterVolume(args),
                isCheat: true);

            // Round-58 — SPRING. Infinite flowing source: the ruler circle
            // keeps emitting, the stream runs downhill as a constant-
            // thickness sheet (thickness= arg, default 1m), and gels when
            // the flow front reaches equilibrium. Single placement.
            new Terminal.ConsoleCommand("va_brush_water_spring",
                "[va_brush_water_spring thickness=1 radius=3] Spring — infinite source, flows downhill at a constant layer thickness, then gels.",
                args => PlaceSpring(args),
                isCheat: true);

            // Round-63 — LiquidVolume render-chain dump for debugging
            // invisible pours (compares against vanilla tar pits).
            FiresLiquidWaterDebug.RegisterDumpCommand();

            // Round-65 — material A/B switch for the invisible-combiner-water
            // diagnosis. Mode 1 strips the FTS FiresWater wrap (raw vanilla
            // Custom/Water); mode 2 is a bright opaque Standard material
            // (ground truth that the MESH itself is visible). Hot-swaps all
            // live combiners immediately.
            new Terminal.ConsoleCommand("va_water_mat_debug",
                "[va_water_mat_debug 0|1|2] Combiner material A/B: 0=normal (FiresWater wrap), 1=vanilla water_liquid (Custom/Tar), 2=bright debug.",
                args =>
                {
                    int mode = WaterMeshCombiner.DebugMaterialMode;
                    if (args.Length >= 2 && int.TryParse(args.Args[1], out int m))
                        mode = Mathf.Clamp(m, 0, 2);
                    WaterMeshCombiner.DebugMaterialMode = mode;
                    int n = WaterMeshCombiner.ReapplyMaterialsAll();
                    string label = mode == 0 ? "normal (wrapped FiresWater)"
                        : mode == 1 ? "vanilla water_liquid (Custom/Tar — tar-pit shader, proven renderer)"
                        : "bright debug material";
                    args.Context.AddString($"va_water_mat_debug: mode={mode} — {label} — re-applied to {n} combiner(s).");
                },
                isCheat: true);

            // Round-66 — pour shader choice. The end goal is FiresWater on
            // ALL custom water (user directive); water_liquid stays the
            // default control case until the pour-destroy bug is resolved.
            new Terminal.ConsoleCommand("va_water_pour_shader",
                "[va_water_pour_shader 0|1] Liquid pour skin: 0=vanilla water_liquid (default), 1=FiresWater shader. Re-skins live pours + future spawns.",
                args =>
                {
                    if (args.Length >= 2 && int.TryParse(args.Args[1], out int v))
                        FiresLiquidWaterPrefab.UseFiresWaterShader = v != 0;
                    int n = FiresLiquidWaterPrefab.ApplyShaderToLivePours();
                    args.Context.AddString($"va_water_pour_shader: {(FiresLiquidWaterPrefab.UseFiresWaterShader ? "FiresWater" : "vanilla water_liquid")} — re-skinned {n} live pour(s).");
                },
                isCheat: true);

            // 2026-05-25 round-4: NO dig, NO biome paint — just the custom
            // lava water disk plus a thin 0.002m water overlay that gives
            // the molten-glass refraction look. User feedback: "our
            // SWIMMABLE lava is different and should not cause terrain
            // ops, and should just place a layer of our custom lava water
            // with a thin layer of water .002 over the top (tighten up
            // the gap)". The dig + biome paint live in the separate
            // Biome Ashlands tool now.
            new Terminal.ConsoleCommand("va_brush_swimmable_lava",
                "[va_brush_swimmable_lava <area>] Place a swimmable custom lava disk at the cursor (no terrain dig, no biome paint).",
                args => PlaceSwimmableLava(args),
                isCheat: true);
        }

        // 2026-05-26 round-26 §2.2: Routes va_brush_snow through the new
        // VALandSnow anchor pipeline (LandSnowAnchor.SpawnNew). No more
        // ice1 clone — the dedicated prefab owns the ZDO under
        // `va_landsnow_*` keys. Identical UX to LandSnowCommands.PlaceLandSnow
        // but with RulerArgs (IH circle=/height=) parsing instead of
        // positional args.
        private static void PlaceLandSnowFromBrush(Terminal.ConsoleEventArgs args)
        {
            if (!RulerArgs.TryResolveCenter(args, out var center, out _)) return;
            float radius = RulerArgs.ResolveRadius(args, defaultRadius: 10f);

            if (RulerArgs.TryParseNamedFloat(args, "height", out float h))
                center.y += h;

            try
            {
                var ctrl = VerdantsAscent.Water.Core.LandSnowAnchor.SpawnNew(center);
                if (ctrl == null)
                {
                    args.Context.AddString("va_brush_snow FAILED: VALandSnow prefab not registered yet.");
                    return;
                }
                ctrl.Initialize(center, radius);

                args.Context.AddString($"va_brush_snow → LandSnow | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1}");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"va_brush_snow FAILED: {ex.Message}");
                FiresLogger.LogError($"[WaterBrushCommands] PlaceLandSnowFromBrush threw: {ex}");
            }
        }

        private static void PlaceSwimmableLava(Terminal.ConsoleEventArgs args)
        {
            if (!RulerArgs.TryResolveCenter(args, out var center, out _)) return;
            float radius = RulerArgs.ResolveRadius(args, defaultRadius: 10f);
            float depth = 3f;
            if (RulerArgs.TryParseNamedFloat(args, "depth", out float d) && d > 0f) depth = d;

            // IH `height` Y offset — useful for placing lava above/below the
            // cursor hit (e.g. on a sky platform).
            if (RulerArgs.TryParseNamedFloat(args, "height", out float h))
                center.y += h;

            try
            {
                CustomWaterManager.CreateDiskOfType(center, radius, depth, CustomLiquidType.Lava);
                args.Context.AddString($"va_brush_swimmable_lava | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} depth={depth:F1} — lava disk + 0.002m water overlay");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"va_brush_swimmable_lava FAILED: {ex.Message}");
                FiresLogger.LogError($"[WaterBrushCommands] PlaceSwimmableLava threw: {ex}");
            }
        }

        // Shared disk-placement helper. Picks center from IH ruler or cursor
        // raycast, picks radius from IH circle= or positional arg, defaults
        // depth to 3m (deeper than the 1m surface mesh so the disk can
        // visually pool when the area is dug down).
        private static void PlaceDisk(Terminal.ConsoleEventArgs args, CustomLiquidType liquid, string label)
        {
            if (!RulerArgs.TryResolveCenter(args, out var center, out _)) return;

            float radius = RulerArgs.ResolveRadius(args, defaultRadius: 10f);
            float depth = 3f;
            if (RulerArgs.TryParseNamedFloat(args, "depth", out float d) && d > 0f) depth = d;

            // Honor IH `<h>` height token as a Y offset on the disk's
            // anchor position. Useful when the player wants the disk to
            // sit above/below the cursor hit (e.g. for sky-platform pools).
            if (RulerArgs.TryParseNamedFloat(args, "height", out float h))
                center.y += h;

            try
            {
                // Single unified path — CreateDiskOfType makes sure the disk
                // AND its owning combiner both end up with the right
                // CustomLiquidType so the variant's material/handler/overlay
                // all attach correctly. Previously Lava + Boiling + Freezing
                // + Ice all rendered as plain water because the disk's type
                // was set after the combiner had already chosen a material
                // (user complaint 2026-05-25: "all of our water tools just
                // show up as water not actually working to swap the shaders").
                var disk = CustomWaterManager.CreateDiskOfType(center, radius, depth, liquid);

                // Round-W-1 — record the placement so WaterBrushDrag picks
                // up the drag-pump. Holding LMB after the initial click
                // will keep stamping disks along the cursor path. The
                // dab pump uses the recorded liquid + radius + depth +
                // surface Y to fire identical placements at each step.
                WaterBrushDrag.RecordLastBrush(
                    WaterBrushDrag.BrushKind.Disk,
                    radius, depth, liquid, surfaceY: center.y);

                args.Context.AddString($"va_brush_water → {label} | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} depth={depth:F1} (drag to paint)");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"va_brush_water → {label} FAILED: {ex.Message}");
                FiresLogger.LogError($"[WaterBrushCommands] PlaceDisk({label}) threw: {ex}");
            }
        }

        // Round-61 — Water VOLUME now spawns the VANILLA LiquidVolume
        // (FiresLiquidWater = TarLiquid clone, LiquidType.Water +
        // water_liquid material). Vanilla's own fluid sim settles it
        // over terrain with collision, swim, persistence, and the real
        // liquid visuals — no custom mesh path. The ruler Y (center.y
        // + IH height offset) is the FILL level: pour depth = fill −
        // ground; volume = π·r²·depth. Pour MORE on top to build depth
        // (each click is an independent pour that merges as it settles).
        //
        // (The round-56 grid-sim path — CreateSettlingVolume — remains
        // in the codebase powering the Spring; the Volume tool uses the
        // vanilla system for cookie-milk parity.)
        private static void PlaceWaterVolume(Terminal.ConsoleEventArgs args)
        {
            // Round-65 — entry log BEFORE any early return. The last test
            // session had zero trace of this command: we couldn't tell
            // "command never fired" apart from "center resolve silently
            // failed". Now both cases are named in the log.
            FiresLogger.LogInfo($"[WaterBrushCommands] va_brush_water_volume fired | line='{args.FullLine}'");
            if (!RulerArgs.TryResolveCenter(args, out var center, out _))
            {
                args.Context.AddString("va_brush_water_volume FAILED: could not resolve center (no IH ruler position, cursor raycast missed).");
                FiresLogger.LogWarning("[WaterBrushCommands] va_brush_water_volume ABORTED — TryResolveCenter failed.");
                return;
            }

            float radius = RulerArgs.ResolveRadius(args, defaultRadius: 8f);
            if (RulerArgs.TryParseNamedFloat(args, "height", out float h))
                center.y += h;

            try
            {
                var go = FiresLiquidWaterPrefab.Spawn(center, radius);
                if (go == null)
                {
                    args.Context.AddString("va_brush_water_volume FAILED: FiresLiquidWater prefab unavailable (see log).");
                    return;
                }
                WaterCleanupCommand.TagToolPour(go);   // 0.2.755: a deliberate placement; warns past a sane total
                args.Context.AddString($"va_brush_water_volume → vanilla liquid pour | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} — settling…");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"va_brush_water_volume FAILED: {ex.Message}");
                FiresLogger.LogError($"[WaterBrushCommands] PlaceWaterVolume threw: {ex}");
            }
        }

        // Round-58/59 — Spring placement. `thickness=` comes from the YAML
        // command string so it's user-tunable per tool entry without a
        // config round-trip. The IH ruler level (from-Y + height offset)
        // is the source POOL fill level — raise the ruler with PgUp to
        // get a deeper pool that overflows into the stream.
        // Round-80 — SPRING = CUSTOM SHEET SIM (user directive). Vanilla
        // LiquidVolume can only settle to ONE FLAT level, so it can't make a
        // sloped river. Back to our combiner sheet sim, but with the fill
        // target reworked: each cell fills to (local bank height − bankOffset)
        // instead of a fixed thickness, so the surface FOLLOWS the channel
        // slope and flat ground (bank≈terrain) stays dry. thickness= is now
        // the "height off the bank" X (how far below the rim the water sits).
        private static void PlaceSpring(Terminal.ConsoleEventArgs args)
        {
            FiresLogger.LogInfo($"[WaterBrushCommands] va_brush_water_spring fired | line='{args.FullLine}'");
            if (!RulerArgs.TryResolveCenter(args, out var center, out _))
            {
                args.Context.AddString("va_brush_water_spring FAILED: could not resolve center (no IH ruler position, cursor raycast missed).");
                FiresLogger.LogWarning("[WaterBrushCommands] va_brush_water_spring ABORTED — TryResolveCenter failed.");
                return;
            }

            float radius = RulerArgs.ResolveRadius(args, defaultRadius: 3f);
            float bankOffset = 1f;   // "height off bank": water surface sits this far below the rim
            if (RulerArgs.TryParseNamedFloat(args, "thickness", out float th) && th > 0f)
                bankOffset = th;

            if (RulerArgs.TryParseNamedFloat(args, "height", out float hOff))
                center.y += hOff;

            try
            {
                // Round-82 — SPRING = FiresLiquidRiver on the vanilla liquid
                // mesh (source disk + bank-following sim), not the flat
                // combiner sheet. bankOffset = "height off bank" X.
                var go = FiresLiquidWaterPrefab.SpawnSpring(center, radius, bankOffset);
                if (go == null)
                {
                    args.Context.AddString("va_brush_water_spring FAILED: FiresLiquidWater prefab unavailable (see log).");
                    return;
                }
                WaterCleanupCommand.TagToolPour(go);   // 0.2.755: a deliberate placement (va_water_cleanup_spread keeptool); warns past a sane total
                args.Context.AddString($"va_brush_water_spring → liquid river | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} offBank={bankOffset:F1}m — source at ruler, flows to bank−{bankOffset:F1} down the slope…");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"va_brush_water_spring FAILED: {ex.Message}");
                FiresLogger.LogError($"[WaterBrushCommands] PlaceSpring threw: {ex}");
            }
        }

        // Round-W-2 — Waterfall placement. Same arg parsing as PlaceDisk
        // but the FIRST dab samples terrain Y (so the water sits ABOVE
        // the ground at the cursor by WATERFALL_OFFSET, not at the
        // ruler-flat Y) and records the drag pump in Waterfall kind so
        // subsequent dabs do the same.
        //
        // Tighter default radius (4m) → narrow continuous stream rather
        // than a wide sheet. User can override via IH circle= as usual.
        private static void PlaceWaterfall(Terminal.ConsoleEventArgs args, CustomLiquidType liquid, string label)
        {
            if (!RulerArgs.TryResolveCenter(args, out var center, out _)) return;

            float radius = RulerArgs.ResolveRadius(args, defaultRadius: 4f);
            // Shallow per-dab depth — waterfalls are thin flowing sheets,
            // not bottomless pools clinging to the rock face.
            const float WATERFALL_DAB_DEPTH = 0.3f;
            const float WATERFALL_OFFSET = 0.5f;

            // Ground Y for the FIRST dab. Round-68: physics probe against
            // real colliders — GetGroundHeight is heightmap-blind and put
            // waterfall dabs meters under custom cliffs. After the
            // crosshair correction center already SITS on the visible
            // surface, so the probe just standardizes the offset; the
            // drag pump's DispatchAt does the same per dab.
            Vector3 placeCenter = center;
            if (WaterBrushDrag.TryProbeGroundBelow(center, out float terrainY))
                placeCenter = new Vector3(center.x, terrainY + WATERFALL_OFFSET, center.z);

            try
            {
                CustomWaterManager.CreateDiskOfType(placeCenter, radius, WATERFALL_DAB_DEPTH, liquid);

                // Record as Waterfall kind so drag dabs follow the
                // raycast-Y instead of the surfaceY.
                WaterBrushDrag.RecordLastBrush(
                    WaterBrushDrag.BrushKind.Waterfall,
                    radius, WATERFALL_DAB_DEPTH, liquid, surfaceY: placeCenter.y);

                args.Context.AddString($"va_brush_waterfall | center=({placeCenter.x:F1},{placeCenter.y:F1},{placeCenter.z:F1}) r={radius:F1} thickness={WATERFALL_DAB_DEPTH:F2} (drag along slope)");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"va_brush_waterfall FAILED: {ex.Message}");
                FiresLogger.LogError($"[WaterBrushCommands] PlaceWaterfall threw: {ex}");
            }
        }

        // Remove water disks within radius. Walks WaterMeshCombiner.AllCombiners,
        // removes overlapping disks. The MopWaterAt path is the closest
        // existing API. Each removal triggers persistence + mesh rebuild.
        private static void DeleteDisks(Terminal.ConsoleEventArgs args)
        {
            if (!RulerArgs.TryResolveCenter(args, out var center, out _)) return;
            float radius = RulerArgs.ResolveRadius(args, defaultRadius: 10f);

            int removed = 0;
            try
            {
                // Snapshot since the iteration mutates the underlying list.
                var combiners = new System.Collections.Generic.List<WaterMeshCombiner>(
                    WaterMeshCombiner.AllCombiners);
                foreach (var c in combiners)
                {
                    if (c == null) continue;
                    var disks = new System.Collections.Generic.List<WaterDisk>(c.WaterDisks);
                    foreach (var d in disks)
                    {
                        float dxz = Vector3.Distance(
                            new Vector3(d.Position.x, 0, d.Position.z),
                            new Vector3(center.x,    0, center.z));
                        if (dxz <= radius + d.Radius)
                        {
                            c.RemoveWaterDisk(d);
                            removed++;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"va_brush_water_delete FAILED: {ex.Message}");
                FiresLogger.LogError($"[WaterBrushCommands] DeleteDisks threw: {ex}");
                return;
            }

            // Round-78 — RULER-LOCAL liquid removal. The delete tool now
            // zeroes just the water depth WITHIN the ruler circle (not the
            // whole pour) so you can carve a big settled sheet down to the
            // shape you want. A pour whose entire grid drains to nothing is
            // then network-destroyed; otherwise it stays and re-simulates.
            var (pours, vol) = RemoveLiquidLocal(center, radius, fraction: 1f);

            args.Context.AddString($"va_brush_water_delete: removed {removed} disk(s) + {vol:F0}m³ from {pours} pour(s) within r={radius:F1} of {center}.");
        }

        // Round-78 — Mop: precision, SOFT local removal. Same footprint math
        // as delete but only takes a fraction of the depth per pass, so
        // holding it over an edge feathers the water back gradually instead
        // of punching a hard hole. Smaller default radius than delete.
        private static void MopWater(Terminal.ConsoleEventArgs args)
        {
            if (!RulerArgs.TryResolveCenter(args, out var center, out _)) return;
            float radius = RulerArgs.ResolveRadius(args, defaultRadius: 3f);
            // Round-84 — mop DAMS spring flow (stops re-flow over the mopped edge).
            var (pours, vol) = RemoveLiquidLocal(center, radius, fraction: 0.5f, dam: true);
            // Also mop combiner disks under the cursor (legacy drawn water).
            int mopped = 0;
            try
            {
                foreach (var c in new System.Collections.Generic.List<WaterMeshCombiner>(WaterMeshCombiner.AllCombiners))
                    if (c != null && c.MopWaterAt(center, radius)) mopped++;
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[WaterBrushCommands] mop combiner pass: {ex.Message}"); }

            // Generated carve+fill water mops too (persisted per column).
            int gen = 0;
            try { gen = VerdantsAscent.Terrain.Voxel.VoxelWorld.MopGeneratedWater(center, radius); }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[WaterBrushCommands] mop generated pass: {ex.Message}"); }

            args.Context.AddString($"va_brush_water_mop: mopped {vol:F0}m³ from {pours} pour(s) + {mopped} disk mesh(es) + {gen} generated column(s) at r={radius:F1}.");
        }

        // Round-84 — Freeze/unfreeze spring flow. Toggles each spring under the
        // ruler: frozen = mesh held, no overflow sim. Persisted.
        private static void FreezeWater(Terminal.ConsoleEventArgs args)
        {
            if (!RulerArgs.TryResolveCenter(args, out var center, out _)) return;
            float radius = RulerArgs.ResolveRadius(args, defaultRadius: 8f);
            var centerXZ = new Vector2(center.x, center.z);
            int froze = 0, thawed = 0;
            try
            {
                foreach (var lv in Object.FindObjectsByType<LiquidVolume>(FindObjectsSortMode.None))
                {
                    if (lv == null) continue;
                    var root = lv.transform.root.gameObject;
                    if (!root.name.StartsWith(FiresLiquidWaterPrefab.PrefabName)) continue;
                    var river = root.GetComponent<FiresLiquidRiverController>();
                    if (river == null) continue;

                    float reach = radius + lv.m_width * lv.m_scale * 0.5f;
                    if (Vector2.Distance(new Vector2(lv.transform.position.x, lv.transform.position.z), centerXZ) > reach) continue;

                    var nv = root.GetComponent<ZNetView>();
                    if (nv != null && nv.IsValid()) nv.ClaimOwnership();
                    river.SetFrozen(!river.m_frozen);
                    if (river.m_frozen) froze++; else thawed++;
                }
            }
            catch (System.Exception ex) { FiresLogger.LogError($"[WaterBrushCommands] FreezeWater threw: {ex}"); }

            args.Context.AddString($"va_brush_water_freeze: froze {froze}, unfroze {thawed} spring(s) at r={radius:F1}.");
        }

        // Shared ruler-local liquid remover for delete (fraction 1) and mop
        // (fraction < 1). Zeroes/reduces LiquidVolume depth cells within the
        // ring; network-destroys any pour left fully dry. Returns (pours
        // touched, m³ removed). Owner-claims each pour before mutating so the
        // change syncs to everyone.
        private static (int, float) RemoveLiquidLocal(Vector3 center, float radius, float fraction, bool dam = false)
        {
            int pours = 0;
            float volume = 0f;
            var centerXZ = new Vector2(center.x, center.z);
            try
            {
                foreach (var lv in Object.FindObjectsByType<LiquidVolume>(FindObjectsSortMode.None))
                {
                    if (lv == null) continue;
                    var root = lv.transform.root.gameObject;
                    if (!root.name.StartsWith(FiresLiquidWaterPrefab.PrefabName)) continue;
                    var nv = root.GetComponent<ZNetView>();
                    if (nv == null || !nv.IsValid()) continue;

                    // Skip pours whose grid can't reach the ring at all.
                    float reach = radius + lv.m_width * lv.m_scale * 0.5f;
                    var srcXZ = new Vector2(root.transform.position.x, root.transform.position.z);
                    float dSource = Vector2.Distance(srcXZ, centerXZ);
                    if (dSource > reach) continue;

                    nv.ClaimOwnership();

                    // Round-84 — MOP = DAM. Mopping a spring stamps a barrier:
                    // those cells never re-fill and water can't flow across
                    // them, so the mesh stops re-flowing over the mopped edge.
                    // Lets you carve/control the flow after the sim draws it.
                    if (dam)
                    {
                        var river = root.GetComponent<FiresLiquidRiverController>();
                        if (river != null) river.BlockCells(centerXZ, radius);
                    }

                    // Round-79 — DELETE OVER THE SOURCE = kill the whole
                    // spring. A ruler-local zero can't remove a spring: the
                    // emitter re-pumps every reload and the sim reflows water
                    // back into the emptied cells. So when the ring covers the
                    // pour's source, clear its emitter ZDO and network-destroy
                    // it. (Mop — fraction < 1 — never does this; it only trims.)
                    bool overSource = dSource <= radius;
                    if (fraction >= 1f && overSource)
                    {
                        StopSpringEmitter(root, nv);
                        ZNetScene.instance.Destroy(root);
                        pours++;
                        continue;
                    }

                    float removed = FiresLiquidGridOps.RemoveInRadius(lv, centerXZ, radius, fraction, out bool anyLeft);

                    // Scrub thin remnants the trim exposed (e.g. a feathered
                    // edge left below the cull threshold) so they don't persist
                    // and z-fight after reload.
                    CullPour(lv);

                    if (removed <= 0f) continue;
                    volume += removed;
                    pours++;
                    if (!anyLeft)
                    {
                        StopSpringEmitter(root, nv);
                        ZNetScene.instance.Destroy(root);
                    }
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogError($"[WaterBrushCommands] RemoveLiquidLocal threw: {ex}");
            }
            return (pours, volume);
        }

        // Round-82 — disarm a spring: zero its rate ZDO (the re-attach patch
        // skips rate<=0, so no controller re-attaches after reload) and remove
        // the live FiresLiquidRiverController. Safe on non-spring pours (no-op).
        private static void StopSpringEmitter(GameObject root, ZNetView nv)
        {
            try
            {
                if (nv != null && nv.IsValid())
                {
                    nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_RATE, 0f);
                    nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_FILL, 0f);
                }
                var river = root.GetComponent<FiresLiquidRiverController>();
                if (river != null) Object.Destroy(river);
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[WaterBrushCommands] StopSpringEmitter: {ex.Message}"); }
        }

        // Round-79 — run the flat-ground spill cull once on a pour under the
        // sim lock, so a delete/mop pass also scrubs thin z-fighting remnants.
        private static void CullPour(LiquidVolume lv)
        {
            try
            {
                var depths = FiresLiquidGridOps.Depths(lv);
                var heights = FiresLiquidGridOps.Heights(lv);
                var mutex = FiresLiquidGridOps.MeshDataLock(lv);
                if (depths == null || heights == null || mutex == null) return;
                if (heights.Count != depths.Count) return;
                int num = lv.m_width + 1;
                int c = lv.m_width / 2;
                int srcHalf = Mathf.Max(1, lv.m_initialArea / 2) + 1;
                System.Threading.Monitor.Enter(mutex);
                try
                {
                    if (FiresLiquidGridOps.CullFlatSpill(depths, heights, num, c, c, srcHalf, 0.35f, 0.5f) > 0f)
                    {
                        FiresLiquidGridOps.Dirty(lv) = true;
                        FiresLiquidGridOps.NeedsSaving(lv) = true;
                    }
                }
                finally { System.Threading.Monitor.Exit(mutex); }
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[WaterBrushCommands] CullPour: {ex.Message}"); }
        }
    }
}
