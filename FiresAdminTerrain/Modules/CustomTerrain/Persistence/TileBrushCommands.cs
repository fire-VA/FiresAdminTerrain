using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Hammer-style brush console commands targeting the tile-anchor system.
    // All commands aim at the player's look-direction raycast hit (falls back
    // to player feet) — same UX as a hammer reticle without requiring IH.
    //
    // Usage examples:
    //   va_brush_flat 10          → 10m flat platform at look position (height = hit point Y)
    //   va_brush_flat 25 50       → 25m flat platform at Y=50 (sky-build)
    //   va_brush_raise 8 3        → raise 3m at look point, 8m radius, linear falloff
    //   va_brush_raise 8 3 2      → same but quadratic falloff (sharper edge)
    //   va_brush_paint 10         → paint vanilla dirt (red) in 10m radius
    //   va_brush_paint 10 0 0 255 → paint pure blue (paved)
    //   va_brush_clear 15         → clear 15m radius back to unmodified
    //
    // These commands are useful both for testing the tile pipeline and as
    // production admin tools — even after the legacy path is deleted in
    // Phase 8e, these stay as the user-facing brush API.
    public static class TileBrushCommands
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("va_brush_flat",
                "[va_brush_flat radius=10 height=auto] Flat platform at look position. Auto-height uses the raycast hit's Y.",
                (args) => Cmd_Flat(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_raise",
                "[va_brush_raise radius=10 delta=1 falloff=1] Raise terrain around look position. Falloff: 1=linear, 2+=sharper edge.",
                (args) => Cmd_Raise(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_lower",
                "[va_brush_lower radius=10 depth=1 falloff=1] Lower terrain around look position. Same as va_brush_raise but with negated height delta.",
                (args) => Cmd_Lower(args),
                isCheat: true);

            // 2026-05-26 round-30: Floating-Island variants. Same args as
            // their non-island counterparts but they ALSO flip the per-tile
            // FloatingIslandMode bit, so any tile section that ends up
            // ≥50m above local terrain renders with a domed teardrop
            // underside instead of stretching down to the ground.
            new Terminal.ConsoleCommand("va_brush_flat_island",
                "[va_brush_flat_island radius=10 height=<click.Y>] Floating-island variant of va_brush_flat. Tiles flagged so the underside auto-rounds into a teardrop bottom when sitting ≥50m above terrain.",
                (args) => Cmd_Flat(args, islandMode: true),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_raise_island",
                "[va_brush_raise_island radius=10 delta=1] Floating-island variant of va_brush_raise. Same dome-underside behavior as va_brush_flat_island.",
                (args) => Cmd_Raise(args, islandMode: true),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_lower_island",
                "[va_brush_lower_island radius=10 depth=1] Floating-island variant of va_brush_lower. Useful for carving floating-island bowls / lakes that auto-dome underneath.",
                (args) => Cmd_Lower(args, islandMode: true),
                isCheat: true);

            // Round-31 B-2: dedicated Smooth brush. Blender's Smooth-brush
            // port — uniform 1-ring Laplacian + iteration_strengths split
            // (4 max passes per dab) + per-stroke origHeights snapshot for
            // accumulate=false convergence behavior.
            new Terminal.ConsoleCommand("va_brush_smooth",
                "[va_brush_smooth radius=10 strength=0.5] Smooth terrain (Laplacian). Strength 0..1 controls iteration count (0.25=1 pass, 0.5=2, 0.75=3, 1.0=4). Hold-and-drag converges to a smoothed target (won't depress past it).",
                (args) => Cmd_Smooth(args),
                isCheat: true);

            // Round-37 B-6: Grab brush. Tracks cursor's Y delta from
            // stroke start; each dab targets snapshot + delta * weight.
            // For console use (no drag state), defaults dragDeltaY=1m.
            new Terminal.ConsoleCommand("va_brush_grab",
                "[va_brush_grab radius=10 dy=1] Drag terrain by cursor Y delta. Hold LMB and move cursor up/down — terrain follows. Target-toward-snapshot semantics (held brush waits for cursor motion instead of accumulating).",
                (args) => Cmd_Grab(args),
                isCheat: true);

            // Round-37 B-7: Noise brush. Perlin displacement.
            new Terminal.ConsoleCommand("va_brush_noise",
                "[va_brush_noise radius=10 intensity=0.5 scale=0.1] Add Perlin-noise displacement. intensity=max bump (±), scale=frequency (0.05=large bumps, 0.5=fine grain).",
                (args) => Cmd_Noise(args),
                isCheat: true);

            // Round-33: manual seam-repair. Brush ops auto-stitch their
            // own footprint now, but this command fixes PRE-EXISTING broken
            // seams left over from earlier sessions where the stitch logic
            // didn't exist yet. Defaults to a 50m radius centered at the
            // cursor; the IH ruler can override via `<area>` / circle=R.
            new Terminal.ConsoleCommand("va_stitch_seams",
                "[va_stitch_seams radius=50] Walks every tile in radius, syncs border verts with neighbors. Use to repair pre-existing seams between tile meshes. No effect on intact terrain.",
                (args) => Cmd_StitchSeams(args),
                isCheat: true);

            // Round-32 mode toggles. These can be bound to hammer
            // right-click hotkeys or fired from console / YAML "instant"
            // tools to flip the global brush behavior mid-session.
            new Terminal.ConsoleCommand("va_brush_toggle_heightlock",
                "Toggle between TrackCursor (default — drag follows terrain) and Locked (RPG-maker style — drag holds the click Y). Affects Flat brush behavior.",
                (args) =>
                {
                    var mode = TileBrushModes.ToggleHeightLock();
                    args.Context.AddString($"Brush height lock → {mode}");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_toggle_placement",
                "Toggle between Connected (default — underside stretches to terrain) and Floating (underside auto-rounds into a teardrop ≥50m above terrain). Affects all height-write brushes.",
                (args) =>
                {
                    var mode = TileBrushModes.TogglePlacementSkirt();
                    args.Context.AddString($"Brush placement → {mode}");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_toggle_clutter_clear",
                "Toggle between Clear (default — paint hides clutter grass under dirt/paved/cultivated, like Infinity Hammer) and Keep (paint leaves grass in place for overgrown/ruined areas). The grass-paint always restores grass regardless.",
                (args) =>
                {
                    var mode = TileBrushModes.ToggleClutterClear();
                    args.Context.AddString($"Brush clutter clear → {mode}");
                },
                isCheat: true);

            // Round-63 — draw-speed throttle for ALL drag brushes
            // (terrain + water). Lower = slower deposition = more
            // precision. Persists via PlayerPrefs.
            new Terminal.ConsoleCommand("va_brush_drawspeed",
                "[0.05-1] Set the brush draw-speed multiplier (1 = full speed, 0.25 = quarter speed for precise work). No arg prints current.",
                (args) =>
                {
                    if (args.Length > 1
                        && float.TryParse(args[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float v))
                    {
                        TileBrushModes.DrawSpeed = v;
                    }
                    args.Context.AddString($"Brush draw speed → {TileBrushModes.DrawSpeed:F2}");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_mode",
                "[heightlock=tracked|locked] [placement=connected|floating] — set one or both global brush modes explicitly. Both args optional; no args prints current state.",
                (args) =>
                {
                    string hl = null, plc = null;
                    for (int i = 1; i < args.Length; i++)
                    {
                        var a = args[i];
                        if (string.IsNullOrEmpty(a) || !a.Contains("=")) continue;
                        var kv = a.Split(new[] { '=' }, 2);
                        var key = kv[0].ToLowerInvariant();
                        var val = kv[1].ToLowerInvariant();
                        if (key == "heightlock" || key == "hl") hl = val;
                        else if (key == "placement" || key == "skirt") plc = val;
                    }
                    if (hl != null)
                    {
                        TileBrushModes.CurrentHeightLock = (hl == "locked" || hl == "lock" || hl == "true" || hl == "1")
                            ? TileBrushModes.HeightLock.Locked
                            : TileBrushModes.HeightLock.TrackCursor;
                    }
                    if (plc != null)
                    {
                        TileBrushModes.CurrentPlacementSkirt = (plc == "floating" || plc == "float" || plc == "true" || plc == "1")
                            ? TileBrushModes.PlacementSkirt.Floating
                            : TileBrushModes.PlacementSkirt.Connected;
                    }
                    args.Context.AddString($"Brush modes → HeightLock={TileBrushModes.CurrentHeightLock}, PlacementSkirt={TileBrushModes.CurrentPlacementSkirt}");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_paint",
                "[va_brush_paint radius=10 r=255 g=0 b=0 a=255] Paint vertex colors in radius. Default red = vanilla dirt.",
                (args) => Cmd_Paint(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_clear",
                "[va_brush_clear radius=10] Clear modified vertices back to unmodified in radius.",
                (args) => Cmd_Clear(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_hole_punch",
                "[va_brush_hole_punch radius=10] Carve a hole through the tile mesh in radius (clears Heights only, keeps Paint + Biome for later patching).",
                (args) => Cmd_HolePunch(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_hole_patch",
                "[va_brush_hole_patch radius=10] Fill unmodified vertices in radius by interpolating heights from nearby modified vertices. Inverse of va_brush_hole_punch.",
                (args) => Cmd_HolePatch(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_biome",
                "[va_brush_biome biome radius=10] Set per-tile biome override (one biome per 64m tile). biome: Meadows=1, Swamp=2, Mountain=4, BlackForest=8, Plains=16, AshLands=32, DeepNorth=64, Ocean=256, Mistlands=512. Use 0 to clear.",
                (args) => Cmd_Biome(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_brush_where",
                "Prints the brush pointer position (player look raycast) for debug.",
                (args) => Cmd_Where(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_undo",
                "Undo the most recent VA tile brush op. WEC's bound 'undo' also calls this when ServerDevcommands is installed.",
                (args) => Cmd_Undo(args),
                isCheat: true);

            new Terminal.ConsoleCommand("va_tile_redo",
                "Redo the most recently undone VA tile brush op.",
                (args) => Cmd_Redo(args),
                isCheat: true);
        }

        // Resolution order for the brush center:
        //   1. IH `<area>` token expands to `from=<x>,<z>,<y> circle=<r>-<r2> …`.
        //      If we find `from=` in args, parse it (this is set by IH when
        //      the player clicks at the ruler position — what we want for
        //      "click to place" tool behavior).
        //   2. Camera raycast from local player (50m). Used when running the
        //      command standalone in console or as an `instant: true` tool.
        // Note: from= uses Valheim's WEC convention `x,z,y` (NOT x,y,z).
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

        // Parse `from=x,z,y` named arg out of the console event args.
        // Returns true + populated Vector3 (world position) on success.
        // Tolerates extra whitespace and case. WEC convention: the three
        // values are X, Z, Y in that order — we map them to Vector3(x, y, z).
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

        private static void Cmd_Flat(Terminal.ConsoleEventArgs args, bool islandMode = false)
        {
            float radius = ResolveRadius(args, defaultRadius: 10f);
            if (!ResolveCenter(args, out var center)) return;
            float height = ResolveAbsoluteHeight(args, baseY: center.y);

            // Route through RPC so MP peers see the change (no-op fallback
            // to local-only when ZNet is null). Local count is approximate
            // — actual touched-tile count fans out via RPC + per-peer apply.
            TileBrushRPC.SendFlat(center, radius, height, square: false, islandMode: islandMode);
            TileBrushDrag.RecordLastBrush(islandMode ? "va_brush_flat_island" : "va_brush_flat", radius, height, 1f, default);
            args.Context.AddString($"va_brush_flat{(islandMode ? "_island" : "")}: center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} h={height:F1} → dispatched (MP-broadcast)");
        }

        private static void Cmd_Raise(Terminal.ConsoleEventArgs args, bool islandMode = false)
        {
            float radius = ResolveRadius(args, defaultRadius: 10f);
            // For raise, args[2] is the delta. Override with height=N named arg if IH-driven.
            float delta = ParseFloat(args, 2, 1f);
            if (TryParseNamedFloat(args, "height", out float ihDelta)) delta = ihDelta;
            float falloff = ParseFloat(args, 3, 1f);
            if (!ResolveCenter(args, out var center)) return;

            TileBrushRPC.SendRaise(center, radius, delta, falloff, islandMode: islandMode);
            TileBrushDrag.RecordLastBrush(islandMode ? "va_brush_raise_island" : "va_brush_raise", radius, delta, falloff, default);
            args.Context.AddString($"va_brush_raise{(islandMode ? "_island" : "")}: center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} delta={delta:F2} falloff={falloff:F2} → dispatched (MP-broadcast)");
        }

        // Lower is the same op as Raise with the delta sign flipped.
        // IH's `<h>` is always positive (Page Up increases), so the YAML tool
        // passes a positive depth and we negate here. This way the player
        // doesn't have to type minus signs and the Page-Up/Down semantics
        // stay intuitive (Up = deeper hole, Down = shallower).
        private static void Cmd_Lower(Terminal.ConsoleEventArgs args, bool islandMode = false)
        {
            float radius = ResolveRadius(args, defaultRadius: 10f);
            float depth = ParseFloat(args, 2, 1f);
            if (TryParseNamedFloat(args, "height", out float ihDepth)) depth = ihDepth;
            float falloff = ParseFloat(args, 3, 1f);
            if (!ResolveCenter(args, out var center)) return;

            // Lower = Raise with negative delta. Route through RPC.
            TileBrushRPC.SendRaise(center, radius, -Mathf.Abs(depth), falloff, islandMode: islandMode);
            TileBrushDrag.RecordLastBrush(islandMode ? "va_brush_lower_island" : "va_brush_lower", radius, depth, falloff, default);
            args.Context.AddString($"va_brush_lower{(islandMode ? "_island" : "")}: center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} depth={depth:F2} falloff={falloff:F2} → dispatched (MP-broadcast)");
        }

        // Round-37 B-6: Grab brush handler. `dy=` overrides cursor delta
        // for console use; live drag uses TileBrushDrag's own delta tracker.
        private static void Cmd_Grab(Terminal.ConsoleEventArgs args)
        {
            float radius = ResolveRadius(args, defaultRadius: 10f);
            float dy = 1f;
            if (TryParseNamedFloat(args, "dy", out float dyParam)) dy = dyParam;
            if (TryParseNamedFloat(args, "height", out float hParam)) dy = hParam;
            if (!ResolveCenter(args, out var center)) return;

            TileBrushRPC.SendGrab(center, radius, dy);
            TileBrushDrag.RecordLastBrush("va_brush_grab", radius, dy, 1f, default);
            args.Context.AddString($"va_brush_grab: center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} dy={dy:F2} → dispatched (MP-broadcast)");
        }

        // Round-37 B-7: Noise brush handler.
        private static void Cmd_Noise(Terminal.ConsoleEventArgs args)
        {
            float radius = ResolveRadius(args, defaultRadius: 10f);
            float intensity = 0.5f;
            float scale = 0.1f;
            if (TryParseNamedFloat(args, "intensity", out float i)) intensity = i;
            if (TryParseNamedFloat(args, "scale", out float s)) scale = s;
            // Positional fallback for non-IH callers
            if (args.Length > 2 && !args[2].Contains("=") && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pi))
                intensity = pi;
            if (args.Length > 3 && !args[3].Contains("=") && float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float ps))
                scale = ps;
            if (!ResolveCenter(args, out var center)) return;

            TileBrushRPC.SendNoise(center, radius, intensity, scale);
            // Repurpose _falloff as scale for drag re-dispatch
            TileBrushDrag.RecordLastBrush("va_brush_noise", radius, intensity, scale, default);
            args.Context.AddString($"va_brush_noise: center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} intensity={intensity:F2} scale={scale:F2} → dispatched (MP-broadcast)");
        }

        // Round-33: manual seam-repair tool. Walks every tile within
        // `radius` of the cursor and stitches their border verts with
        // neighbors. Use to repair pre-existing breaks from before the
        // auto-stitch landed (any new brush op auto-stitches now).
        private static void Cmd_StitchSeams(Terminal.ConsoleEventArgs args)
        {
            float radius = ResolveRadius(args, defaultRadius: 50f);
            if (!ResolveCenter(args, out var center)) return;

            int stitched = TileBrush.StitchTileBorders(center, radius);
            args.Context.AddString($"va_stitch_seams: center=({center.x:F1},{center.z:F1}) r={radius:F1} → {stitched} seam(s) stitched");
        }

        // Round-31 B-2: Smooth brush — Blender Smooth port.
        // Args:
        //   radius (positional 1 or `circle=R` from IH)
        //   strength (positional 2 or `strength=N`) — 0..1, controls iteration count
        // Drag re-fires via TileBrushDrag with the recorded strength; the
        // per-stroke snapshot makes hold-and-drag converge to a target.
        private static void Cmd_Smooth(Terminal.ConsoleEventArgs args)
        {
            float radius = ResolveRadius(args, defaultRadius: 10f);
            float strength = ParseFloat(args, 2, 0.5f);
            if (TryParseNamedFloat(args, "strength", out float namedStrength)) strength = namedStrength;
            strength = Mathf.Clamp01(strength);
            if (!ResolveCenter(args, out var center)) return;

            TileBrushRPC.SendSmooth(center, radius, strength);
            // Pass strength through as `_falloff` in the drag-cache (the
            // TileBrushDrag dispatcher repurposes _falloff for Smooth
            // strength so we don't have to add a new field).
            TileBrushDrag.RecordLastBrush("va_brush_smooth", radius, 0f, strength, default);
            args.Context.AddString($"va_brush_smooth: center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} strength={strength:F2} → dispatched (MP-broadcast)");
        }

        private static void Cmd_Paint(Terminal.ConsoleEventArgs args)
        {
            float radius = ResolveRadius(args, defaultRadius: 10f);
            byte r = (byte)Mathf.Clamp(ParseFloat(args, 2, 255f), 0, 255);
            byte g = (byte)Mathf.Clamp(ParseFloat(args, 3, 0f), 0, 255);
            byte b = (byte)Mathf.Clamp(ParseFloat(args, 4, 0f), 0, 255);
            byte a = (byte)Mathf.Clamp(ParseFloat(args, 5, 255f), 0, 255);
            if (!ResolveCenter(args, out var center)) return;

            var color = new Color32(r, g, b, a);

            // 2026-05-26 round-24 ROUTING FIX: previously va_brush_paint
            // ALWAYS dispatched to the custom-tile path via TileBrushRPC.
            // SendPaint, which walks TileAnchorRegistry.TilesOverlappingCircle
            // and writes to existing CUSTOM tiles only. When the cursor was
            // over vanilla terrain with no custom tile (e.g. a paved road),
            // the brush did NOTHING — the vanilla paint mask stayed
            // untouched and the new color was invisible. User complaint:
            // "operations like paint dirt paint grass etc still dont clear
            // the original terrain paint and place our new on over it".
            //
            // Same classifier the biome paint already uses:
            //   * hitCustomTile → existing custom-tile dispatch (per-vertex
            //     paint on the tile mesh, MP-synced via RPC).
            //   * else → unconditional OVERWRITE of the vanilla heightmap
            //     paint mask in the radius. Replaces whatever was there
            //     (paved cobblestones, prior dirt, etc.) with the new color.
            //     ForceHeightmapRegeneration fires inside the helper so
            //     the visual change is immediate.
            TileBrush.TryGetPointerTarget(out _, out bool hitCustomTile);

            if (hitCustomTile)
            {
                TileBrushRPC.SendPaint(center, radius, color, square: false);
                TileBrushDrag.RecordLastBrush("va_brush_paint", radius, 0f, 1f, color);
                args.Context.AddString($"va_brush_paint → CUSTOM | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} color=({r},{g},{b},{a}) → dispatched (MP-broadcast)");
            }
            else
            {
                try
                {
                    Color floatColor = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
                    int comps = VerdantsAscent.Terrain.Biome.BiomePaintPersistence
                        .OverwriteVanillaPaintMask(center, radius, floatColor);
                    args.Context.AddString($"va_brush_paint → VANILLA | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} color=({r},{g},{b},{a}) → wrote {comps} TerrainComp(s)");
                }
                catch (System.Exception ex)
                {
                    args.Context.AddString($"va_brush_paint → VANILLA failed: {ex.Message}");
                }
            }

            // Refresh clutter grass so the paint's cleared/uncleared state takes
            // effect immediately, matching Infinity Hammer + vanilla terrain paint.
            // ClutterSystem.IsCleared reads the paint mask (any RGB > 0.5 => cleared
            // => no grass), so ResetGrass re-rolls the in-radius patches to match:
            //   * dirt / cultivated / paved (RGB painted) HIDE the grass
            //   * the grass-paint (0,0,0) brings it BACK
            // The dedicated biome brush already does this (TileBrush.BrushBiome);
            // texture paint was the one path that skipped it, so grass showed
            // through painted dirt/paved. Gated on the ClutterClear mode so "Keep"
            // lays texture UNDER existing grass (overgrown/ruined) — except the
            // grass-paint always re-rolls, since restoring grass is its whole point.
            bool clearingPaint = r > 127 || g > 127 || b > 127;
            bool refreshClutter = !clearingPaint
                || TileBrushModes.CurrentClutterClear == TileBrushModes.ClutterClear.Clear;
            if (refreshClutter && ClutterSystem.instance != null)
            {
                try { ClutterSystem.instance.ResetGrass(center, radius); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[va_brush_paint] ResetGrass threw: {ex.Message}"); }
            }
        }

        private static void Cmd_Clear(Terminal.ConsoleEventArgs args)
        {
            float radius = ResolveRadius(args, defaultRadius: 10f);
            if (!ResolveCenter(args, out var center)) return;

            TileBrushRPC.SendClear(center, radius);
            TileBrushDrag.RecordLastBrush("va_brush_clear", radius, 0f, 1f, default);
            args.Context.AddString($"va_brush_clear: center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} → dispatched (MP-broadcast)");
        }

        private static void Cmd_HolePunch(Terminal.ConsoleEventArgs args)
        {
            float radius = ResolveRadius(args, defaultRadius: 5f);
            if (!ResolveCenter(args, out var center)) return;

            TileBrushRPC.SendHolePunch(center, radius);
            args.Context.AddString($"va_brush_hole_punch: center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} → dispatched (MP-broadcast)");
        }

        private static void Cmd_HolePatch(Terminal.ConsoleEventArgs args)
        {
            float radius = ResolveRadius(args, defaultRadius: 5f);
            if (!ResolveCenter(args, out var center)) return;

            TileBrushRPC.SendHolePatch(center, radius);
            args.Context.AddString($"va_brush_hole_patch: center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} → dispatched (MP-broadcast)");
        }

        // Biome paint — per-tile biome override. args[1] is biome ID:
        //   Meadows=1, Swamp=2, Mountain=4, BlackForest=8, Plains=16,
        //   AshLands=32, DeepNorth=64, Ocean=256, Mistlands=512, 0=clear.
        // Radius selects which tiles get the new biome (one tile = 64m square).
        //
        // CURSOR-TARGET ROUTING (user directive 2026-05-25):
        //   * Cursor center on CUSTOM tile  → write ONLY to the custom tile
        //                                      mesh via TileBrushRPC.SendBiome
        //                                      (per-vertex biome override).
        //   * Cursor center on VANILLA      → paint ONLY the vanilla heightmap
        //                                      via BiomeOverlayPainter.PaintOverlay
        //                                      (BiomePaintPersistence handles
        //                                      MP sync internally).
        // No more double-paint — the raycast in TryGetPointerTarget decides.
        // If the user supplied `from=` (IH ruler / console), we re-raycast
        // from camera to classify the target so the routing matches what the
        // player is actually pointing at.
        private static void Cmd_Biome(Terminal.ConsoleEventArgs args)
        {
            int biomeInt = (int)ParseFloat(args, 1, 0f);
            float radius = ResolveRadius(args, defaultRadius: 10f);
            // IH-driven path also passes biome as named arg (biome=N) for
            // YAML flexibility. Falls through to positional otherwise.
            if (TryParseNamedFloat(args, "biome", out float namedBiome))
                biomeInt = (int)namedBiome;
            if (!ResolveCenter(args, out var center)) return;

            ushort biome = (ushort)Mathf.Clamp(biomeInt, 0, ushort.MaxValue);

            // Always classify by re-raycasting through the camera — even when
            // ResolveCenter used `from=` (IH ruler). The ruler position
            // matches our camera ray center for hammer-style tools, so this
            // gives the right answer for both console and IH dispatch.
            TileBrush.TryGetPointerTarget(out _, out bool hitCustomTile);

            // ── LAVA SPECIAL CASE ────────────────────────────────────────
            // 2026-05-26 round-25 — DIG REMOVED from both paths. The lava
            // overlay now sits ON TOP of terrain at an alpha-controlled
            // Y offset (round-21 in PaintVanillaHeightmapMask wires alpha
            // to vanilla Heightmap.GetHeightOffset's `lerp(0.1m, 0.8m,
            // alpha)` formula, so the surface ramps from +0.8m at center
            // to +0.1m at the edge). Both the vanilla DIG and the custom-
            // tile LAVA_MAX_DEPTH depression are gone. User directive:
            // "we also no longer need to terrain op lower the ground by
            // any amount when placing lava ... just overlay it on top of
            // terrain and use the alpha channel to blend it right".
            //
            // The hitCustomTile classifier still routes to the right
            // surface so the paint lands where the cursor points:
            //   * CUSTOM tile  → SendBiome RPC sets per-vertex biome
            //   * VANILLA      → BiomeOverlayPainter writes the alpha-
            //                    falloff lava mask
            //
            // PRE-SCRUB still runs unconditionally to wipe any prior
            // overlay + clutter + grass in the radius so the new lava
            // paint shows clean.
            bool isLava = biome == (ushort)Heightmap.Biome.AshLands;
            if (isLava)
            {
                VerdantsAscent.Terrain.Lifecycle.RegionScrubber.ScrubRegion(
                    center, radius,
                    VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope.VanillaBiome
                    | VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope.Clutter
                    | VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope.VanillaGrass,
                    VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Source.BiomePaintReplace);

                if (hitCustomTile)
                {
                    TileBrushRPC.SendBiome(center, radius, biome);
                    args.Context.AddString($"va_brush_biome → LAVA (CUSTOM tile, overlay only) | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} → dispatched (MP-broadcast)");
                }
                else
                {
                    VerdantsAscent.Terrain.Lifecycle.LavaPlacement.PlaceLava(center, radius);
                    args.Context.AddString($"va_brush_biome → LAVA (VANILLA, overlay only) | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} | alpha-falloff blend");
                }
                return;
            }

            if (hitCustomTile)
            {
                // Custom tile path — TileBrushRPC fans out to all peers and
                // calls TileBrush.BrushBiome locally on each (custom only,
                // no vanilla side-effect after the 2026-05-25 split).
                TileBrushRPC.SendBiome(center, radius, biome);
                args.Context.AddString($"va_brush_biome → CUSTOM | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} biome={biome} → dispatched (MP-broadcast)");
            }
            else
            {
                // Vanilla path — BiomePaintPersistence has its own MP sync
                // via TerrainOperationRPC.RequestBiomePaint. We call locally;
                // the server-authoritative path takes over on MP.
                try
                {
                    var overlay = TileBrush.MapBiomeToOverlayType(biome);
                    int affected = VerdantsAscent.Terrain.Biome.BiomeOverlayPainter
                        .PaintOverlay(center, radius, overlay);
                    args.Context.AddString($"va_brush_biome → VANILLA | center=({center.x:F1},{center.y:F1},{center.z:F1}) r={radius:F1} biome={biome} overlay={overlay} affected={affected}");
                }
                catch (System.Exception ex)
                {
                    args.Context.AddString($"va_brush_biome → VANILLA failed: {ex.Message}");
                }
            }
        }

        // ── IH ruler parsing helpers ──────────────────────────────────────
        //
        // IH's `<area>` token expands to `from=<x>,<z>,<y> circle=<r>-<r2> …`.
        // `<r>` is the outer ruler radius (player can scroll to change);
        // `<r2>` is inner (donut, usually 0).
        // `<h>` is the ruler's height offset (Page Up / Page Down).
        //
        // Resolution rules:
        //   * radius: ruler `circle=R-R2` OUTER if present, else positional arg[1], else default
        //   * height (absolute, for Flat): named `height=N` ADDED to click Y, else positional arg[2] ABSOLUTE if parseable, else click Y
        //   * delta (for Raise): named `height=N` if present, else positional arg[2]

        private static float ResolveRadius(Terminal.ConsoleEventArgs args, float defaultRadius)
        {
            if (TryParseCircleArg(args, out float ihRadius) && ihRadius > 0f) return ihRadius;
            return ParseFloat(args, 1, defaultRadius);
        }

        private static float ResolveAbsoluteHeight(Terminal.ConsoleEventArgs args, float baseY)
        {
            // 1. IH `<h>` token via height= named arg → ADD to click Y (matches IH offset semantics)
            if (TryParseNamedFloat(args, "height", out float hOffset))
                return baseY + hOffset;
            // 2. Positional arg[2] as ABSOLUTE Y (backward compat with console use)
            if (args.Length > 2 && args[2] != null && !args[2].Contains("=") &&
                float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float absH))
                return absH;
            // 3. Default to click Y
            return baseY;
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

        private static bool TryParseNamedFloat(Terminal.ConsoleEventArgs args, string name, out float value)
        {
            value = 0f;
            string prefix = name + "=";
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null || !a.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                string payload = a.Substring(prefix.Length).Trim();
                if (float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return true;
            }
            return false;
        }

        private static void Cmd_Where(Terminal.ConsoleEventArgs args)
        {
            if (!ResolveCenter(args, out var center)) return;
            var tileXZ = TileAnchorRegistry.WorldToTileIndex(center);
            args.Context.AddString($"Pointer at {center} | tile=({tileXZ.x},{tileXZ.y}) | tile world rect = [{tileXZ.x * 64}, {tileXZ.y * 64}] .. [{(tileXZ.x + 1) * 64}, {(tileXZ.y + 1) * 64}]");
        }

        private static void Cmd_Undo(Terminal.ConsoleEventArgs args)
        {
            string r = TileUndoLog.Undo();
            args.Context.AddString(r ?? $"va_tile_undo: nothing to undo (stack depth = {TileUndoLog.UndoCount}).");
        }

        private static void Cmd_Redo(Terminal.ConsoleEventArgs args)
        {
            string r = TileUndoLog.Redo();
            args.Context.AddString(r ?? $"va_tile_redo: nothing to redo (stack depth = {TileUndoLog.RedoCount}).");
        }

        private static float ParseFloat(Terminal.ConsoleEventArgs args, int idx, float def)
        {
            if (args.Length <= idx) return def;
            return float.TryParse(args[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        }
    }
}
