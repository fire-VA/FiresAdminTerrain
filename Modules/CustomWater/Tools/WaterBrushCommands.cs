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
                "[va_brush_water_delete radius=10] Delete any custom water disks overlapping the ruler radius.",
                args => DeleteDisks(args),
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
                Debug.LogError($"[WaterBrushCommands] PlaceLandSnowFromBrush threw: {ex}");
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
                Debug.LogError($"[WaterBrushCommands] PlaceSwimmableLava threw: {ex}");
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
                Debug.LogError($"[WaterBrushCommands] PlaceDisk({label}) threw: {ex}");
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

            // Sample terrain Y at the cursor XZ for the FIRST dab. The
            // drag pump's DispatchAt does the same per dab.
            Vector3 placeCenter = center;
            if (ZoneSystem.instance != null
                && ZoneSystem.instance.GetGroundHeight(
                    new Vector3(center.x, center.y + 50f, center.z),
                    out float terrainY))
            {
                placeCenter = new Vector3(center.x, terrainY + WATERFALL_OFFSET, center.z);
            }

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
                Debug.LogError($"[WaterBrushCommands] PlaceWaterfall threw: {ex}");
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
                Debug.LogError($"[WaterBrushCommands] DeleteDisks threw: {ex}");
                return;
            }

            args.Context.AddString($"va_brush_water_delete: removed {removed} disk(s) within r={radius:F1} of {center}.");
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        private static class Terminal_InitTerminal_RegisterWaterBrushCommands
        {
            private static void Postfix() => Register();
        }
    }
}
